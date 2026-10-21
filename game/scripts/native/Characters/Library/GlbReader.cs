using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// Reads a character GLB from its verified bytes, the way the converter writes it (pipeline/characters/conv_a/convert.py,
/// class Glb): one self-contained binary glTF 2.0 file, triangle lists of float positions and normals with integer
/// indices, tightly packed in the one embedded buffer, base-colour materials, no skins, animations, cameras, textures
/// or external files. <see cref="BuildMesh"/> turns a mesh into an <see cref="ArrayMesh"/> directly, as Godot's glTF
/// reader would (front faces reversed to Godot's winding, base colours from linear to sRGB) but without its scene
/// pass: the five shipped characters build in a fraction of <see cref="GltfDocument"/>'s 59 ms.
/// </summary>
internal sealed class GlbReader
{
    public sealed record Node(string Name, Vector3 Translation, bool HasTransformBeyondTranslation, int Mesh, int[] Children);
    private sealed record Accessor(long Start, int Count, int ComponentType, string Type);
    private sealed record Primitive(int Position, int Normal, int Indices, int Material, long Triangles);
    private sealed record MaterialInfo(string Name, Color BaseColourLinear, float Metallic, float Roughness, bool DoubleSided);

    public const int MaxBytes = 32 * 1024 * 1024;
    /// <summary>
    /// The vertices the built primitives may copy in all, per triangle of the budget (three: a triangle soup). The converter writes
    /// about half a vertex per triangle; the bound keeps a small file from making the reader copy one huge accessor many times.
    /// </summary>
    public const int MaxVerticesPerTriangle = 3;
    private const string Label = "character.glb";

    private readonly List<Accessor> _accessors = new();
    private readonly List<Primitive[]> _meshes = new();
    private readonly List<MaterialInfo> _materials = new();
    private readonly List<string> _meshNames = new();
    private StandardMaterial3D?[] _builtMaterials = Array.Empty<StandardMaterial3D?>();

    public IReadOnlyList<Node> Nodes { get; private set; } = Array.Empty<Node>();
    public int[] SceneRoots { get; private set; } = Array.Empty<int>();
    public int[] MeshTriangles { get; private set; } = Array.Empty<int>();
    public int Triangles { get; private set; }

    public static GlbReader Read(byte[] bytes, int triangleBudget)
    {
        if (bytes.Length > MaxBytes) throw new CharacterLoadException($"{Label} is {bytes.Length} bytes, over the {MaxBytes}-byte limit");
        if (bytes.Length < 28 || bytes[0] != (byte)'g' || bytes[1] != (byte)'l' || bytes[2] != (byte)'T' || bytes[3] != (byte)'F')
            throw new CharacterLoadException($"{Label} is not a binary glTF file");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        if (version != 2 || length != bytes.Length) throw new CharacterLoadException($"{Label} has an invalid GLB header");
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != 0x4E4F534A || 20L + jsonLength > bytes.Length)
            throw new CharacterLoadException($"{Label} does not start with a JSON chunk");
        long binaryLength = 0;
        var binaryStart = 20L + jsonLength;
        if (binaryStart + 8 <= bytes.Length)
        {
            binaryLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)binaryStart));
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)binaryStart + 4)) != 0x004E4942 || binaryStart + 8 + binaryLength > bytes.Length)
                throw new CharacterLoadException($"{Label} has an invalid binary chunk");
        }
        var jsonCount = bytes.AsSpan(20, (int)jsonLength).TrimEnd(stackalloc byte[] { 0x20, 0x00 }).Length;
        // The structure below is checked member by member; a repeated glTF key cannot smuggle anything past it.
        using var document = StrictJson.Parse(bytes, 20, jsonCount, Label + " (JSON chunk)", 8 * 1024 * 1024, rejectDuplicateKeys: false);
        var root = document.RootElement;
        StrictJson.ExpectObject(root, Label);
        var reader = new GlbReader();

        foreach (var forbidden in new[] { "skins", "animations", "cameras", "textures", "extensionsRequired" })
            if (root.TryGetProperty(forbidden, out var present) && present.ValueKind == JsonValueKind.Array && present.GetArrayLength() > 0)
                throw new CharacterLoadException($"{Label} has {forbidden}; the converter writes none (the rig is built in the game)");
        foreach (var kind in new[] { "buffers", "images" })
        {
            if (!root.TryGetProperty(kind, out var items)) continue;
            if (items.ValueKind != JsonValueKind.Array) throw new CharacterLoadException($"{Label} has a '{kind}' entry that is not an array");
            foreach (var item in items.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out _))
                    throw new CharacterLoadException($"{Label} references an external file; a character GLB must be self-contained");
        }
        var buffers = root.TryGetProperty("buffers", out var bufferList) ? bufferList.GetArrayLength() : 0;
        if (buffers > 1) throw new CharacterLoadException($"{Label} has {buffers} buffers; expected the one embedded buffer");
        if (buffers == 1)
        {
            var declared = StrictJson.Number(StrictJson.Get(bufferList[0], "byteLength", "buffers[0]"), "buffers[0].byteLength", 0, MaxBytes);
            if (declared > binaryLength) throw new CharacterLoadException($"{Label} declares a buffer longer than its binary chunk");
        }

        var views = new List<(long Offset, long Length, int Stride)>();
        if (root.TryGetProperty("bufferViews", out var viewList))
        {
            StrictJson.Array(viewList, "bufferViews", 0, 1_000_000);
            var i = 0;
            foreach (var view in viewList.EnumerateArray())
            {
                var path = new JsonPath("bufferViews", i++);
                if (buffers != 1 || StrictJson.Integer(StrictJson.Get(view, "buffer", path), path.Key("buffer"), 0, 0) != 0)
                    throw new CharacterLoadException($"{Label}: {path} must use the embedded buffer");
                var offset = view.TryGetProperty("byteOffset", out var o) ? (long)StrictJson.Number(o, path.Key("byteOffset"), 0, MaxBytes) : 0;
                var size = (long)StrictJson.Number(StrictJson.Get(view, "byteLength", path), path.Key("byteLength"), 0, MaxBytes);
                var stride = view.TryGetProperty("byteStride", out var s) ? StrictJson.Integer(s, path.Key("byteStride"), 4, 252) : 0;
                if (offset + size > binaryLength) throw new CharacterLoadException($"{Label}: {path} runs past the binary chunk");
                views.Add((binaryStart + 8 + offset, size, stride));
            }
        }
        if (root.TryGetProperty("accessors", out var accessorList))
        {
            StrictJson.Array(accessorList, "accessors", 0, 1_000_000);
            var i = 0;
            foreach (var accessor in accessorList.EnumerateArray())
            {
                var path = new JsonPath("accessors", i++);
                if (accessor.TryGetProperty("sparse", out _)) throw new CharacterLoadException($"{Label}: {path} is sparse; the converter writes none");
                var count = StrictJson.Integer(StrictJson.Get(accessor, "count", path), path.Key("count"), 0, 50_000_000);
                var component = StrictJson.Integer(StrictJson.Get(accessor, "componentType", path), path.Key("componentType"), 5120, 5126);
                var type = StrictJson.String(StrictJson.Get(accessor, "type", path), path.Key("type"), 8);
                var width = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new CharacterLoadException($"{Label}: {path} has an unsupported type") };
                var componentBytes = component switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new CharacterLoadException($"{Label}: {path} has an unsupported component type") };
                if (!accessor.TryGetProperty("bufferView", out var viewIndex) || views.Count == 0) throw new CharacterLoadException($"{Label}: {path} has no buffer view");
                var view = views[StrictJson.Integer(viewIndex, path.Key("bufferView"), 0, views.Count - 1)];
                if (view.Stride != 0 && view.Stride != width * componentBytes) throw new CharacterLoadException($"{Label}: {path} is interleaved; the converter packs every accessor");
                var offset = accessor.TryGetProperty("byteOffset", out var o) ? (long)StrictJson.Number(o, path.Key("byteOffset"), 0, MaxBytes) : 0;
                if (offset % componentBytes != 0 || offset + (long)count * width * componentBytes > view.Length)
                    throw new CharacterLoadException($"{Label}: {path} runs past its buffer view");
                reader._accessors.Add(new Accessor(view.Offset + offset, count, component, type));
            }
        }

        if (root.TryGetProperty("materials", out var materialList))
        {
            StrictJson.Array(materialList, "materials", 0, 4096);
            var m = 0;
            foreach (var material in materialList.EnumerateArray())
            {
                var path = new JsonPath("materials", m++);
                StrictJson.ExpectObject(material, $"{Label}: {path}");
                var name = material.TryGetProperty("name", out var n) ? StrictJson.String(n, path.Key("name"), 200) : "";
                var colour = new Color(1, 1, 1);
                float metallic = 1, roughness = 1;
                if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    if (pbr.TryGetProperty("baseColorFactor", out var factor))
                    {
                        var c = StrictJson.Doubles(factor, path.Key("baseColorFactor"), 4, 1);
                        if (c[0] < 0 || c[1] < 0 || c[2] < 0 || c[3] < 0) throw new CharacterLoadException($"{Label}: {path}'s base colour is negative");
                        colour = new Color((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
                    }
                    if (pbr.TryGetProperty("metallicFactor", out var mf)) metallic = (float)StrictJson.Number(mf, path.Key("metallicFactor"), 0, 1);
                    if (pbr.TryGetProperty("roughnessFactor", out var rf)) roughness = (float)StrictJson.Number(rf, path.Key("roughnessFactor"), 0, 1);
                }
                var doubleSided = material.TryGetProperty("doubleSided", out var ds) && StrictJson.Bool(ds, path.Key("doubleSided"));
                reader._materials.Add(new MaterialInfo(name, colour, metallic, roughness, doubleSided));
            }
        }

        var meshTriangles = new List<int>();
        var total = 0L;
        // Every primitive copies its whole POSITION accessor (BuildMesh), so the vertices are bounded like the triangles: an
        // accessor shared by several primitives counts once for each, and vertices no triangle uses count too.
        var vertexTotal = 0L;
        if (root.TryGetProperty("meshes", out var meshList))
        {
            StrictJson.Array(meshList, "meshes", 0, 4096);
            var m = 0;
            foreach (var mesh in meshList.EnumerateArray())
            {
                var path = new JsonPath("meshes", m++);
                var primitives = StrictJson.Array(StrictJson.Get(mesh, "primitives", path), path.Key("primitives"), 1, 256);
                var parsed = new List<Primitive>();
                long triangles = 0;
                var p = 0;
                foreach (var primitive in primitives.EnumerateArray())
                {
                    var primitivePath = new JsonPath("meshes", m - 1, "primitives", p++);
                    if (primitive.TryGetProperty("mode", out var mode) && StrictJson.Integer(mode, primitivePath.Key("mode"), 0, 6) != 4)
                        throw new CharacterLoadException($"{Label}: {primitivePath} is not a triangle list");
                    if (primitive.TryGetProperty("targets", out _)) throw new CharacterLoadException($"{Label}: {primitivePath} has morph targets; the converter writes none");
                    var attributes = StrictJson.Get(primitive, "attributes", primitivePath);
                    var position = reader.AccessorIndex(StrictJson.Get(attributes, "POSITION", primitivePath.Key("attributes")), primitivePath.Key("attributes").Key("POSITION"), "VEC3", 5126);
                    var vertices = reader._accessors[position].Count;
                    var normal = -1;
                    if (attributes.TryGetProperty("NORMAL", out var normalValue))
                    {
                        normal = reader.AccessorIndex(normalValue, primitivePath.Key("attributes").Key("NORMAL"), "VEC3", 5126);
                        if (reader._accessors[normal].Count != vertices) throw new CharacterLoadException($"{Label}: {primitivePath} has a normal count unlike its vertex count");
                    }
                    var indices = -1;
                    long corners = vertices;
                    if (primitive.TryGetProperty("indices", out var indexValue))
                    {
                        indices = reader.AccessorIndex(indexValue, primitivePath.Key("indices"), "SCALAR", 0);
                        if (reader._accessors[indices].ComponentType is not (5121 or 5123 or 5125)) throw new CharacterLoadException($"{Label}: {primitivePath}'s indices are not unsigned integers");
                        corners = reader._accessors[indices].Count;
                    }
                    if (corners % 3 != 0) throw new CharacterLoadException($"{Label}: {primitivePath} has {corners} corners, not whole triangles");
                    var material = primitive.TryGetProperty("material", out var materialValue)
                        ? StrictJson.Integer(materialValue, primitivePath.Key("material"), 0, Math.Max(0, reader._materials.Count - 1)) : -1;
                    if (material >= reader._materials.Count) throw new CharacterLoadException($"{Label}: {primitivePath} names a missing material");
                    parsed.Add(new Primitive(position, normal, indices, material, corners / 3));
                    triangles += corners / 3;
                    vertexTotal += vertices;
                }
                total += triangles;
                reader._meshes.Add(parsed.ToArray());
                reader._meshNames.Add(mesh.TryGetProperty("name", out var meshName) ? StrictJson.String(meshName, path.Key("name"), 200) : "");
                meshTriangles.Add((int)Math.Min(triangles, int.MaxValue));
            }
        }
        if (total > triangleBudget)
            throw new CharacterLoadException(FormattableString.Invariant($"character.glb has {total:N0} triangles, over the {triangleBudget:N0}-triangle limit per character (the converter's refinement is the knob)"));
        if (vertexTotal > MaxVerticesPerTriangle * (long)triangleBudget)
            throw new CharacterLoadException(FormattableString.Invariant(
                $"character.glb's primitives name {vertexTotal:N0} vertices in all (an accessor shared by several primitives counts for each), over the {MaxVerticesPerTriangle * (long)triangleBudget:N0}-vertex limit per character"));

        var nodes = new List<Node>();
        var nodeList = StrictJson.Array(StrictJson.Get(root, "nodes", Label), "nodes", 1, 129);
        var k = 0;
        foreach (var node in nodeList.EnumerateArray())
        {
            var path = new JsonPath("nodes", k++);
            StrictJson.ExpectObject(node, $"{Label}: {path}");
            var name = node.TryGetProperty("name", out var nameValue) ? StrictJson.String(nameValue, path.Key("name"), 64) : "";
            var translation = node.TryGetProperty("translation", out var t) ? StrictJson.Vector(t, path.Key("translation"), 10) : Vector3.Zero;
            var extra = false;
            if (node.TryGetProperty("rotation", out var r))
            {
                var q = StrictJson.Doubles(r, path.Key("rotation"), 4, 2);
                extra |= Math.Abs(q[0]) > 1e-9 || Math.Abs(q[1]) > 1e-9 || Math.Abs(q[2]) > 1e-9 || Math.Abs(Math.Abs(q[3]) - 1) > 1e-9;
            }
            if (node.TryGetProperty("scale", out var s))
            {
                var v = StrictJson.Doubles(s, path.Key("scale"), 3, 1000);
                extra |= Math.Abs(v[0] - 1) > 1e-9 || Math.Abs(v[1] - 1) > 1e-9 || Math.Abs(v[2] - 1) > 1e-9;
            }
            extra |= node.TryGetProperty("matrix", out _) || node.TryGetProperty("skin", out _) || node.TryGetProperty("camera", out _);
            var meshIndex = node.TryGetProperty("mesh", out var meshValue) ? StrictJson.Integer(meshValue, path.Key("mesh"), 0, Math.Max(0, meshTriangles.Count - 1)) : -1;
            if (meshIndex >= meshTriangles.Count) throw new CharacterLoadException($"{Label}: {path} names a missing mesh");
            var children = new List<int>();
            if (node.TryGetProperty("children", out var childList))
            {
                StrictJson.Array(childList, path.Key("children"), 0, 128);
                foreach (var child in childList.EnumerateArray()) children.Add(StrictJson.Integer(child, path.Key("children"), 0, nodeList.GetArrayLength() - 1));
            }
            nodes.Add(new Node(name, translation, extra, meshIndex, children.ToArray()));
        }
        var roots = new List<int>();
        var sceneIndex = root.TryGetProperty("scene", out var sceneValue) ? StrictJson.Integer(sceneValue, "scene", 0, 0) : 0;
        var scenes = StrictJson.Array(StrictJson.Get(root, "scenes", Label), "scenes", 1, 1);
        if (scenes[sceneIndex].TryGetProperty("nodes", out var sceneNodes))
            foreach (var node in StrictJson.Array(sceneNodes, "scenes[0].nodes", 1, 1).EnumerateArray())
                roots.Add(StrictJson.Integer(node, "scenes[0].nodes[]", 0, nodes.Count - 1));
        reader.Nodes = nodes;
        reader.SceneRoots = roots.ToArray();
        reader.MeshTriangles = meshTriangles.ToArray();
        reader.Triangles = (int)total;
        reader._builtMaterials = new StandardMaterial3D?[reader._materials.Count];
        return reader;
    }

    private int AccessorIndex(JsonElement value, JsonPath path, string type, int componentType)
    {
        if (_accessors.Count == 0) throw new CharacterLoadException($"{Label}: {path} names a missing accessor");
        var index = StrictJson.Integer(value, path, 0, _accessors.Count - 1);
        var accessor = _accessors[index];
        if (accessor.Type != type || (componentType != 0 && accessor.ComponentType != componentType))
            throw new CharacterLoadException($"{Label}: {path} must be a {type} accessor{(componentType == 5126 ? " of floats" : "")}");
        return index;
    }

    /// <summary>One mesh as an <see cref="ArrayMesh"/>: a surface per primitive, triangles turned to Godot's winding, every index and vertex checked.</summary>
    public ArrayMesh BuildMesh(byte[] bytes, int meshIndex, string partName)
    {
        var mesh = new ArrayMesh { ResourceName = _meshNames[meshIndex] };
        foreach (var primitive in _meshes[meshIndex])
        {
            var position = _accessors[primitive.Position];
            var positionBytes = bytes.AsSpan((int)position.Start, position.Count * 12);
            if (!AllFinite(positionBytes)) throw new CharacterLoadException($"{Label}: the part {partName} has a non-finite vertex");
            var vertices = MemoryMarshal.Cast<byte, Vector3>(positionBytes).ToArray();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            if (primitive.Normal >= 0)
            {
                // The converter writes unit normals; Godot packs them as they are. A non-finite one is refused like a vertex.
                var normal = _accessors[primitive.Normal];
                var normalBytes = bytes.AsSpan((int)normal.Start, normal.Count * 12);
                if (!AllFinite(normalBytes)) throw new CharacterLoadException($"{Label}: the part {partName} has a non-finite normal");
                arrays[(int)Mesh.ArrayType.Normal] = MemoryMarshal.Cast<byte, Vector3>(normalBytes).ToArray();
            }
            arrays[(int)Mesh.ArrayType.Index] = Indices(bytes, primitive, vertices.Length, partName);
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (primitive.Material >= 0) mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, Material(primitive.Material));
        }
        return mesh;
    }

    /// <summary>True when no float in the span is NaN or infinite (exponent bits all set), checked with the framework's vectorised search.</summary>
    private static bool AllFinite(ReadOnlySpan<byte> floats)
    {
        var bits = MemoryMarshal.Cast<byte, uint>(floats);
        return bits.IndexOfAnyInRange(0x7F800000u, 0x7FFFFFFFu) < 0 && bits.IndexOfAnyInRange(0xFF800000u, 0xFFFFFFFFu) < 0;
    }

    /// <summary>The triangle list with each triangle's last two corners swapped: glTF's front faces wind counter-clockwise, Godot's clockwise.</summary>
    private int[] Indices(byte[] bytes, Primitive primitive, int vertexCount, string partName)
    {
        int[] result;
        if (primitive.Indices < 0)
        {
            result = new int[vertexCount];
            for (var i = 0; i < result.Length; i++) result[i] = i;
        }
        else
        {
            var accessor = _accessors[primitive.Indices];
            var start = (int)accessor.Start;
            switch (accessor.ComponentType)
            {
                case 5121:
                    result = new int[accessor.Count];
                    for (var i = 0; i < result.Length; i++) result[i] = bytes[start + i];
                    break;
                case 5123:
                    result = new int[accessor.Count];
                    var shorts = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan(start, accessor.Count * 2));
                    for (var i = 0; i < result.Length; i++) result[i] = shorts[i];
                    break;
                default:
                    result = MemoryMarshal.Cast<byte, int>(bytes.AsSpan(start, accessor.Count * 4)).ToArray();
                    break;
            }
            // Indices are unsigned: a value past int.MaxValue reads negative here and is out of range too.
            if (((ReadOnlySpan<int>)result).IndexOfAnyExceptInRange(0, vertexCount - 1) >= 0)
                throw new CharacterLoadException($"{Label}: the part {partName} indexes past its vertices");
        }
        for (var i = 0; i + 2 < result.Length; i += 3) (result[i + 1], result[i + 2]) = (result[i + 2], result[i + 1]);
        return result;
    }

    /// <summary>One material per glTF material, shared by the meshes that use it, made as Godot's glTF reader makes it.</summary>
    private StandardMaterial3D Material(int index)
    {
        if (_builtMaterials[index] is { } built) return built;
        var info = _materials[index];
        var material = new StandardMaterial3D
        {
            ResourceName = info.Name,
            AlbedoColor = info.BaseColourLinear.LinearToSrgb(),
            Metallic = info.Metallic,
            Roughness = info.Roughness,
            CullMode = info.DoubleSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
        };
        _builtMaterials[index] = material;
        return material;
    }
}
