using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GarageGame.Native.Room;

/// <summary>
/// The parts of a pinned GLB the room contract checks and the loader reads without Godot's importer (10x world, S6): its node
/// names and roots, each mesh's POSITION bounds and triangle count (as contracts/validate.py counts them), and a mesh's triangles
/// for collision and navigation. Pure C# over verified bytes, so it runs on any thread. Every offset is checked: a GLB in the
/// player's folder is untrusted, and a bad one is refused (RoomLoadException), never read past its end.
/// </summary>
public sealed class GlbReader
{
    public sealed record Node(string? Name, int Mesh, bool HasChildren, bool HasTransform);
    public sealed record Primitive(int Mode, int Position, int Indices);
    private sealed record Accessor(int BufferView, long ByteOffset, int ComponentType, string Type, long Count, double[]? Min, double[]? Max, bool Sparse);
    private sealed record BufferView(int Buffer, long ByteOffset, long ByteLength, int ByteStride);

    public string Label { get; }
    public IReadOnlyList<Node> Nodes { get; }
    /// <summary>The default scene's root nodes (scene, else 0), or empty when the file names none.</summary>
    public IReadOnlyList<int> Roots { get; }
    public IReadOnlyList<IReadOnlyList<Primitive>> Meshes { get; }
    private readonly Accessor[] _accessors;
    private readonly BufferView[] _views;
    private readonly byte[] _bytes;
    private readonly long _binStart, _binLength;

    private GlbReader(string label, byte[] bytes, Node[] nodes, int[] roots, Primitive[][] meshes, Accessor[] accessors, BufferView[] views, long binStart, long binLength)
    {
        Label = label;
        _bytes = bytes;
        Nodes = nodes;
        Roots = roots;
        Meshes = meshes;
        _accessors = accessors;
        _views = views;
        _binStart = binStart;
        _binLength = binLength;
    }

    /// <summary>Reads a GLB that RoomData.RequireSelfContainedGlb has accepted.</summary>
    public static GlbReader Read(byte[] bytes, string label)
    {
        RoomData.RequireSelfContainedGlb(bytes, label);
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        long binStart = 0, binLength = 0;
        var next = 20L + jsonLength;
        // An optional BIN chunk follows the JSON chunk (8-byte header, then the bytes).
        if (next + 8 <= bytes.Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)next + 4)) == 0x004E4942)
        {
            binLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)next));
            binStart = next + 8;
            Expect(binStart + binLength <= bytes.Length, $"{label} has a BIN chunk past its end");
        }
        var json = bytes.AsSpan(20, (int)jsonLength).TrimEnd(stackalloc byte[] { 0x20, 0x00 }).ToArray();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var nodes = new List<Node>();
        foreach (var node in Items(root, "nodes"))
        {
            if (node.ValueKind != JsonValueKind.Object) { nodes.Add(new Node(null, -1, false, false)); continue; }
            var name = node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var mesh = node.TryGetProperty("mesh", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var index) ? index : -1;
            var transform = false;
            foreach (var key in new[] { "matrix", "translation", "rotation", "scale" }) transform |= node.TryGetProperty(key, out _);
            nodes.Add(new Node(name, mesh, node.TryGetProperty("children", out _), transform));
        }
        var roots = new List<int>();
        var scene = root.TryGetProperty("scene", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sceneIndex) ? sceneIndex : 0;
        var scenes = new List<JsonElement>(Items(root, "scenes"));
        if (scene >= 0 && scene < scenes.Count && scenes[scene].ValueKind == JsonValueKind.Object)
            foreach (var r in Items(scenes[scene], "nodes"))
                roots.Add(r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var value) ? value : -1);
        var meshes = new List<Primitive[]>();
        foreach (var mesh in Items(root, "meshes"))
        {
            var primitives = new List<Primitive>();
            if (mesh.ValueKind == JsonValueKind.Object)
                foreach (var p in Items(mesh, "primitives"))
                {
                    if (p.ValueKind != JsonValueKind.Object) { primitives.Add(new Primitive(-1, -1, -1)); continue; }
                    var mode = p.TryGetProperty("mode", out var mo) && mo.ValueKind == JsonValueKind.Number && mo.TryGetInt32(out var modeValue) ? modeValue : 4;
                    var position = p.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
                                   attributes.TryGetProperty("POSITION", out var pos) && pos.ValueKind == JsonValueKind.Number && pos.TryGetInt32(out var posIndex) ? posIndex : -1;
                    var indices = p.TryGetProperty("indices", out var ind) && ind.ValueKind == JsonValueKind.Number && ind.TryGetInt32(out var indIndex) ? indIndex : -1;
                    primitives.Add(new Primitive(mode, position, indices));
                }
            meshes.Add(primitives.ToArray());
        }
        var accessors = new List<Accessor>();
        foreach (var a in Items(root, "accessors"))
        {
            if (a.ValueKind != JsonValueKind.Object) { accessors.Add(new Accessor(-1, 0, 0, "", 0, null, null, false)); continue; }
            accessors.Add(new Accessor(
                IntOr(a, "bufferView", -1), LongOr(a, "byteOffset", 0), IntOr(a, "componentType", 0),
                a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "",
                LongOr(a, "count", -1), Numbers(a, "min"), Numbers(a, "max"), a.TryGetProperty("sparse", out _)));
        }
        var views = new List<BufferView>();
        foreach (var v in Items(root, "bufferViews"))
            views.Add(v.ValueKind == JsonValueKind.Object
                ? new BufferView(IntOr(v, "buffer", -1), LongOr(v, "byteOffset", 0), LongOr(v, "byteLength", -1), IntOr(v, "byteStride", 0))
                : new BufferView(-1, 0, -1, 0));
        return new GlbReader(label, bytes, nodes.ToArray(), roots.ToArray(), meshes.ToArray(), accessors.ToArray(), views.ToArray(), binStart, binLength);
    }

    /// <summary>Triangles the file's meshes hold, indexed or not, as contracts/validate.py counts them; null when a primitive is not a triangle list or cannot be counted.</summary>
    public long? Triangles()
    {
        long total = 0;
        foreach (var mesh in Meshes)
            foreach (var primitive in mesh)
            {
                if (primitive.Mode != 4) return null;
                var index = primitive.Indices >= 0 ? primitive.Indices : primitive.Position;
                if (index < 0 || index >= _accessors.Length || _accessors[index].Count < 0) return null;
                total += _accessors[index].Count / 3;
            }
        return total;
    }

    /// <summary>The union of a mesh's POSITION accessor bounds (glTF requires min and max on them), or null when missing.</summary>
    public (Vector3d Low, Vector3d High)? MeshExtent(int mesh)
    {
        if (mesh < 0 || mesh >= Meshes.Count || Meshes[mesh].Count == 0) return null;
        var low = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var high = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var primitive in Meshes[mesh])
        {
            if (primitive.Position < 0 || primitive.Position >= _accessors.Length) return null;
            var accessor = _accessors[primitive.Position];
            if (accessor.Min is not { Length: >= 3 } min || accessor.Max is not { Length: >= 3 } max) return null;
            low = Vector3d.Min(low, new Vector3d(min[0], min[1], min[2]));
            high = Vector3d.Max(high, new Vector3d(max[0], max[1], max[2]));
        }
        return low.IsFinite && high.IsFinite ? (low, high) : null;
    }

    /// <summary>The union of every mesh's POSITION bounds, or null when a mesh has none.</summary>
    public (Vector3d Low, Vector3d High)? Extent()
    {
        if (Meshes.Count == 0) return null;
        (Vector3d Low, Vector3d High)? all = null;
        for (var m = 0; m < Meshes.Count; m++)
        {
            if (MeshExtent(m) is not { } e) return null;
            all = all is { } a ? (Vector3d.Min(a.Low, e.Low), Vector3d.Max(a.High, e.High)) : e;
        }
        return all;
    }

    /// <summary>
    /// A mesh's triangles as a face list (three corners each), in Godot's winding: glTF's front faces are counter-clockwise and
    /// Godot's importer swaps each triangle's last two corners, so these are the faces Mesh.CreateTrimeshShape would give for the
    /// imported mesh, from the file's own float positions (the importer may quantise them). Triangle lists only.
    /// </summary>
    public Vector3[] Faces(int mesh)
    {
        Expect(mesh >= 0 && mesh < Meshes.Count, $"{Label}: mesh {mesh} does not exist");
        var faces = new List<Vector3>();
        foreach (var primitive in Meshes[mesh])
        {
            Expect(primitive.Mode == 4, $"{Label}: mesh {mesh} holds a primitive that is not a triangle list");
            var positions = ReadVec3(primitive.Position);
            if (primitive.Indices >= 0)
            {
                var indices = ReadIndices(primitive.Indices);
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    Expect(indices[i] < positions.Length && indices[i + 1] < positions.Length && indices[i + 2] < positions.Length, $"{Label}: an index points past its positions");
                    faces.Add(positions[indices[i]]);
                    faces.Add(positions[indices[i + 2]]);
                    faces.Add(positions[indices[i + 1]]);
                }
            }
            else
                for (var i = 0; i + 2 < positions.Length; i += 3)
                {
                    faces.Add(positions[i]);
                    faces.Add(positions[i + 2]);
                    faces.Add(positions[i + 1]);
                }
        }
        return faces.ToArray();
    }

    /// <summary>
    /// A mesh's triangles as Faces gives them, kept small: its float positions (three a vertex, every primitive's in turn) and the
    /// faces' corners as indices into them, in the same order (so the face list rebuilt from them is Faces' exactly), as unsigned
    /// shorts when the positions allow.
    /// </summary>
    public (float[] Positions, ushort[]? Small, int[]? Large) Ground(int mesh)
    {
        Expect(mesh >= 0 && mesh < Meshes.Count, $"{Label}: mesh {mesh} does not exist");
        var points = new List<Vector3>();
        var corners = new List<int>();
        foreach (var primitive in Meshes[mesh])
        {
            Expect(primitive.Mode == 4, $"{Label}: mesh {mesh} holds a primitive that is not a triangle list");
            var positions = ReadVec3(primitive.Position);
            var offset = points.Count;
            points.AddRange(positions);
            if (primitive.Indices >= 0)
            {
                var indices = ReadIndices(primitive.Indices);
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    Expect(indices[i] < positions.Length && indices[i + 1] < positions.Length && indices[i + 2] < positions.Length, $"{Label}: an index points past its positions");
                    corners.Add(offset + indices[i]);
                    corners.Add(offset + indices[i + 2]);
                    corners.Add(offset + indices[i + 1]);
                }
            }
            else
                for (var i = 0; i + 2 < positions.Length; i += 3)
                {
                    corners.Add(offset + i);
                    corners.Add(offset + i + 2);
                    corners.Add(offset + i + 1);
                }
        }
        var flat = new float[points.Count * 3];
        for (var i = 0; i < points.Count; i++) (flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2]) = (points[i].X, points[i].Y, points[i].Z);
        return points.Count <= ushort.MaxValue + 1 ? (flat, corners.Select(c => (ushort)c).ToArray(), null) : (flat, null, corners.ToArray());
    }

    /// <summary>Whether Faces can read a mesh (float positions, triangle lists, plain indices inside the BIN chunk), without reading it: null, or why not.</summary>
    public string? Unreadable(int mesh)
    {
        try
        {
            Expect(mesh >= 0 && mesh < Meshes.Count, $"mesh {mesh} does not exist");
            foreach (var primitive in Meshes[mesh])
            {
                Expect(primitive.Mode == 4, "it holds a primitive that is not a triangle list");
                var (positions, _, _) = Locate(primitive.Position, 12);
                Expect(positions.ComponentType == 5126 && positions.Type == "VEC3", "its POSITION is not float VEC3");
                if (primitive.Indices < 0) continue;
                var size = primitive.Indices < _accessors.Length ? _accessors[primitive.Indices].ComponentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 } : 0;
                Expect(size > 0, "its indices are not unsigned bytes, shorts or ints");
                Locate(primitive.Indices, size);
            }
            return null;
        }
        catch (RoomLoadException error) { return error.Message; }
    }

    /// <summary>Every POSITION of a mesh (no duplicates removed).</summary>
    public Vector3[] Positions(int mesh)
    {
        Expect(mesh >= 0 && mesh < Meshes.Count, $"{Label}: mesh {mesh} does not exist");
        var points = new List<Vector3>();
        foreach (var primitive in Meshes[mesh]) points.AddRange(ReadVec3(primitive.Position));
        return points.ToArray();
    }

    private Vector3[] ReadVec3(int accessorIndex)
    {
        var (accessor, start, stride) = Locate(accessorIndex, 12);
        Expect(accessor.ComponentType == 5126 && accessor.Type == "VEC3", $"{Label}: POSITION must be float VEC3");
        var result = new Vector3[accessor.Count];
        var span = _bytes.AsSpan();
        for (long i = 0; i < accessor.Count; i++)
        {
            var at = (int)(start + i * stride);
            result[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(span[at..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(at + 4)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(at + 8)..]));
        }
        return result;
    }

    private int[] ReadIndices(int accessorIndex)
    {
        var size = _accessors.Length > accessorIndex && accessorIndex >= 0 ? _accessors[accessorIndex].ComponentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 } : 0;
        Expect(size > 0, $"{Label}: indices must be unsigned bytes, shorts or ints");
        var (accessor, start, stride) = Locate(accessorIndex, size);
        Expect(accessor.Type == "SCALAR", $"{Label}: indices must be scalars");
        var result = new int[accessor.Count];
        var span = _bytes.AsSpan();
        for (long i = 0; i < accessor.Count; i++)
        {
            var at = (int)(start + i * stride);
            var value = size switch { 1 => span[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(span[at..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(span[at..]) };
            Expect(value <= int.MaxValue, $"{Label}: an index is out of range");
            result[i] = (int)value;
        }
        return result;
    }

    /// <summary>Where an accessor's elements start in the file and how far apart they are, checked to lie inside its buffer view and the BIN chunk.</summary>
    private (Accessor Accessor, long Start, long Stride) Locate(int accessorIndex, int elementBytes)
    {
        Expect(accessorIndex >= 0 && accessorIndex < _accessors.Length, $"{Label}: accessor {accessorIndex} does not exist");
        var accessor = _accessors[accessorIndex];
        Expect(!accessor.Sparse, $"{Label}: sparse accessors are not read");
        Expect(accessor.Count >= 0 && accessor.Count <= int.MaxValue / 16, $"{Label}: accessor {accessorIndex} has an invalid count");
        Expect(accessor.BufferView >= 0 && accessor.BufferView < _views.Length, $"{Label}: accessor {accessorIndex} has no buffer view");
        var view = _views[accessor.BufferView];
        Expect(view.Buffer == 0 && view.ByteOffset >= 0 && view.ByteLength >= 0 && accessor.ByteOffset >= 0, $"{Label}: buffer view {accessor.BufferView} is invalid");
        var stride = view.ByteStride > 0 ? view.ByteStride : elementBytes;
        Expect(stride >= elementBytes, $"{Label}: buffer view {accessor.BufferView} has a stride shorter than its element");
        var needed = accessor.Count == 0 ? 0 : accessor.ByteOffset + (accessor.Count - 1) * stride + elementBytes;
        Expect(needed <= view.ByteLength && view.ByteOffset + view.ByteLength <= _binLength, $"{Label}: accessor {accessorIndex} reaches past its buffer");
        return (accessor, _binStart + view.ByteOffset + accessor.ByteOffset, stride);
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Array.Empty<JsonElement>();

    private static int IntOr(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static long LongOr(JsonElement element, string name, long fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : fallback;

    private static double[]? Numbers(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var numbers = new List<double>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) return null;
            numbers.Add(item.GetDouble());
        }
        return numbers.ToArray();
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new RoomLoadException(message);
    }
}

/// <summary>A double-precision point for the contract checks, which compare file numbers as contracts/validate.py does (in doubles).</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d Min(Vector3d a, Vector3d b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vector3d Max(Vector3d a, Vector3d b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };
}
