using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Navigation;

/// <summary>
/// The bake's source geometry, read off the collision shapes as triangle arrays: the main thread takes only references and
/// transforms (and builds the few small shapes), and the worker reads the big trimeshes' faces itself. Each body's
/// fingerprint and bounds say which tiles to bake again when it changes.
/// </summary>
public partial class RoomNavigation
{
    /// <summary>What a body was at the last bake. Bounds are in the source root's space; HasBounds is false for a body with no triangles.</summary>
    private sealed record BodyRecord(int Signature, Aabb Bounds, bool HasBounds);

    /// <summary>A box taller than the agent, projected from its foot (see Obstruct).</summary>
    private readonly record struct Obstruction(Vector3[] Corners, float Bottom, float Height, Aabb Bounds);

    /// <summary>
    /// One static body as the bake reads it. Triangles are kept in Godot's face order (front faces clockwise, as a trimesh's
    /// faces come); they are turned to Recast's order where they go into the bake (AppendTriangles), as Godot's own add_faces does.
    /// </summary>
    private sealed class BodySource
    {
        public ulong Id;
        public int Signature;
        /// <summary>Trimeshes, read on the worker (their faces are the bulk of the land), with their transform into the source root's space.</summary>
        public readonly List<(ConcavePolygonShape3D Shape, Transform3D ToRoot)> Concave = new();
        /// <summary>Triangles already in the source root's space (boxes, and what Godot's own parser made of other shapes).</summary>
        public readonly List<Vector3> Fixed = new();
        public readonly List<Obstruction> Obstructions = new();
        private float[]? _triangles;

        /// <summary>Triangles read at an earlier bake for the same fingerprint (the land never changes: its faces are read once).</summary>
        public void Reuse(float[] triangles) => _triangles = triangles;

        /// <summary>Nine floats a triangle, in the source root's space (read once, on the worker).</summary>
        public float[] Triangles()
        {
            if (_triangles != null) return _triangles;
            var faces = Concave.Select(c => (c.Shape.GetFaces(), c.ToRoot)).ToList();
            var triangles = new float[(Fixed.Count + faces.Sum(f => f.Item1.Length)) * 3];
            var k = 0;
            foreach (var vertex in Fixed) { triangles[k++] = vertex.X; triangles[k++] = vertex.Y; triangles[k++] = vertex.Z; }
            foreach (var (points, toRoot) in faces)
                foreach (var point in points)
                {
                    var vertex = toRoot * point;
                    triangles[k++] = vertex.X; triangles[k++] = vertex.Y; triangles[k++] = vertex.Z;
                }
            return _triangles = triangles;
        }

        public Aabb? Bounds()
        {
            var triangles = Triangles();
            Aabb? bounds = null;
            if (triangles.Length >= 3)
            {
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (var k = 0; k + 2 < triangles.Length; k += 3)
                {
                    minX = Math.Min(minX, triangles[k]); maxX = Math.Max(maxX, triangles[k]);
                    minY = Math.Min(minY, triangles[k + 1]); maxY = Math.Max(maxY, triangles[k + 1]);
                    minZ = Math.Min(minZ, triangles[k + 2]); maxZ = Math.Max(maxZ, triangles[k + 2]);
                }
                bounds = new Aabb(new Vector3(minX, minY, minZ), new Vector3(maxX - minX, maxY - minY, maxZ - minZ));
            }
            foreach (var block in Obstructions) bounds = bounds?.Merge(block.Bounds) ?? block.Bounds;
            return bounds;
        }
    }

    /// <summary>Shapes Godot's parser made into triangles for a body, in the body's own space, by the shapes' fingerprint (they are parsed again only when they change).</summary>
    private readonly Dictionary<ulong, (int Shapes, Vector3[] Triangles)> _parsed = new();
    private NavigationMesh? _parseSettings;

    /// <summary>
    /// The static bodies under the source root, found by one walk of its tree and walked again only when a body or a shape
    /// is added or removed anywhere (the scene tree's signals): on the magnified island each walk (thousands of nodes) took
    /// 2 to 4 ms, and a poll comes four times a second. Bodies on any layer are kept: carrying a thing takes it off the world
    /// layer and back, which the fingerprint notices.
    /// </summary>
    private readonly List<StaticBody3D> _bodies = new();
    private bool _bodiesDirty = true;

    private void SceneChanged(Node node)
    {
        if (node is StaticBody3D or CollisionShape3D && IsInstanceValid(SourceRoot) && (node == SourceRoot || SourceRoot.IsAncestorOf(node))) _bodiesDirty = true;
    }

    private List<StaticBody3D> Bodies()
    {
        if (!_bodiesDirty) return _bodies;
        _bodies.Clear();
        void Walk(Node node)
        {
            if (node is StaticBody3D body) _bodies.Add(body);
            foreach (var child in node.GetChildren()) Walk(child);
        }
        Walk(SourceRoot);
        _bodiesDirty = false;
        return _bodies;
    }

    /// <summary>
    /// Every static body on the world layer under the source root: its fingerprint, its trimeshes (by reference), its boxes as
    /// triangles, and anything else (convex hulls, cylinders, spheres, capsules, height maps) through Godot's own parser,
    /// exactly as the single bake read it; and the fingerprint of them all (as Signature).
    /// </summary>
    private List<BodySource> Snapshot(Transform3D toRoot, out int signature)
    {
        var bodies = new List<BodySource>();
        var hash = new HashCode();
        foreach (var body in Bodies())
        {
            if (!Parses(body)) continue;
            var source = Snapshot(body, toRoot);
            hash.Add(source.Signature);
            bodies.Add(source);
        }
        signature = hash.ToHashCode();
        return bodies;
    }

    private BodySource Snapshot(StaticBody3D body, Transform3D toRoot)
    {
        {
            var source = new BodySource { Id = body.GetInstanceId(), Signature = BodySignature(body) };
            var parse = false;
            foreach (var child in body.GetChildren())
            {
                if (child is not CollisionShape3D { Disabled: false } holder || holder.Shape == null) continue;
                var xform = toRoot * holder.GlobalTransform;
                switch (holder.Shape)
                {
                    case ConcavePolygonShape3D concave: source.Concave.Add((concave, xform)); break;
                    case BoxShape3D box:
                        AddBox(source.Fixed, xform, box.Size);
                        Obstruct(source.Obstructions, xform, box.Size);
                        break;
                    default: parse = true; break;
                }
            }
            if (parse)
            {
                // One shape Godot's parser must read makes it read them all, so the body is never counted twice.
                source.Concave.Clear();
                source.Fixed.Clear();
                var shapes = ShapesSignature(body);
                if (!_parsed.TryGetValue(source.Id, out var parsed) || parsed.Shapes != shapes)
                {
                    _parseSettings ??= new NavigationMesh
                    {
                        GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
                        GeometryCollisionMask = RoomBuilder.WorldLayer,
                        GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.RootNodeChildren,
                    };
                    var data = new NavigationMeshSourceGeometryData3D();
                    NavigationServer3D.ParseSourceGeometryData(_parseSettings, data, body);
                    var vertices = data.Vertices;
                    var indices = data.Indices;
                    // The parser stores Recast's order (each face's second and third corners swapped): back to Godot's.
                    var triangles = new Vector3[indices.Length - indices.Length % 3];
                    Vector3 At(int index) => new(vertices[index * 3], vertices[index * 3 + 1], vertices[index * 3 + 2]);
                    for (var i = 0; i + 2 < indices.Length; i += 3)
                    {
                        triangles[i] = At(indices[i]);
                        triangles[i + 1] = At(indices[i + 2]);
                        triangles[i + 2] = At(indices[i + 1]);
                    }
                    _parsed[source.Id] = parsed = (shapes, triangles);
                }
                var bodyToRoot = toRoot * body.GlobalTransform;
                foreach (var vertex in parsed.Triangles) source.Fixed.Add(bodyToRoot * vertex);
            }
            return source;
        }
    }

    private static readonly int[] BoxFaces =
    {
        0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0,
    };

    /// <summary>A box's twelve faces in Godot's order: seen from outside, each face's corners run clockwise.</summary>
    private static void AddBox(List<Vector3> triangles, Transform3D xform, Vector3 size)
    {
        var h = size * 0.5f;
        var corners = new[]
        {
            new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, h.Z), new Vector3(-h.X, -h.Y, h.Z),
            new Vector3(-h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z),
        };
        for (var i = 0; i < BoxFaces.Length; i += 3)
        {
            var (a, b, c) = (corners[BoxFaces[i]], corners[BoxFaces[i + 1]], corners[BoxFaces[i + 2]]);
            // Outward from the box's middle (the origin here): a clockwise front face's (b - a) x (c - a) points inward.
            if ((b - a).Cross(c - a).Dot(a + b + c) > 0) (b, c) = (c, b);
            triangles.Add(xform * a);
            triangles.Add(xform * b);
            triangles.Add(xform * c);
        }
    }

    /// <summary>
    /// Recast rasterizes surfaces, not solids: the floor's top under a box standing on it stayed walkable whenever the box was
    /// taller than the agent (Lane P, the open sea round: the place beside a climber snapped inside a cliff, and the test
    /// room's big box held a walkable polygon at its foot). Every upright box on the world layer taller than the agent is
    /// a projected obstruction from its foot up to two cells under its top, so its top stays walkable and nothing under it is.
    /// Only boxes: a ramp's or a hull's slope is walkable ground.
    /// </summary>
    private void Obstruct(List<Obstruction> obstructions, Transform3D xform, Vector3 size)
    {
        if (Mathf.Abs(xform.Basis.Y.Normalized().Dot(Vector3.Up)) < 0.999f) return;
        var half = size * 0.5f;
        var corners = new[] { new Vector3(-half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, half.Z), new Vector3(-half.X, -half.Y, half.Z) }
            .Select(c => xform * c).ToArray();
        var bottom = corners.Min(c => c.Y);
        var height = Mathf.Abs((xform.Basis.Y * size.Y).Y) - 2 * CellHeightM;
        if (height + 2 * CellHeightM <= AgentHeightM || height <= 0) return;
        var low = new Vector3(corners.Min(c => c.X), bottom, corners.Min(c => c.Z));
        var high = new Vector3(corners.Max(c => c.X), bottom + height, corners.Max(c => c.Z));
        obstructions.Add(new Obstruction(corners, bottom, height, new Aabb(low, high - low)));
    }

    /// <summary>
    /// A cheap fingerprint of the static collision the mesh is baked from: which bodies and shapes, where, and what each
    /// shape is. A shape resized in place keeps its instance id, so its dimensions are hashed too (Lane P review).
    /// </summary>
    private int Signature()
    {
        var hash = new HashCode();
        foreach (var body in Bodies())
            if (Parses(body)) hash.Add(BodySignature(body));
        return hash.ToHashCode();
    }

    /// <summary>
    /// Whether the bake reads a body from the scene: a static body on the world layer, except a tiled terrain part
    /// (RoomBuilder.TerrainTilesMeta) when the bake takes the ground from the room's data instead (Room; the same faces).
    /// </summary>
    public bool Parses(StaticBody3D body) =>
        IsInstanceValid(body) && body.IsInsideTree() && (body.CollisionLayer & RoomBuilder.WorldLayer) != 0 &&
        !(Room?.Terrain != null && body.HasMeta(TerrainTilesMeta));

    /// <summary>RoomBuilder.TerrainTilesMeta as a StringName made once (S12: a string makes a new one, for the finalizer, each call).</summary>
    private static readonly StringName TerrainTilesMeta = RoomBuilder.TerrainTilesMeta;

    /// <summary>One body's fingerprint: its identity, where it is, and its shapes.</summary>
    private int BodySignature(StaticBody3D body)
    {
        var hash = new HashCode();
        hash.Add(body.GetInstanceId());
        hash.Add(Quantize(body.GlobalTransform));
        hash.Add(ShapesSignature(body));
        return hash.ToHashCode();
    }

    /// <summary>The body's shapes alone (which, what, where in the body), without the body's own place.</summary>
    private int ShapesSignature(StaticBody3D body)
    {
        var hash = new HashCode();
        var children = body.GetChildren();
        using var childArray = (Godot.Collections.Array)children;
        foreach (var child in children)
            if (child is CollisionShape3D shape)
            {
                hash.Add(shape.GetInstanceId());
                hash.Add(shape.Disabled);
                hash.Add(shape.Shape?.GetInstanceId() ?? 0);
                hash.Add(ShapeContent(shape.Shape));
                hash.Add(Quantize(shape.Transform));
            }
        return hash.ToHashCode();
    }

    /// <summary>The shape's dimensions to the millimetre; for mesh-sized data, a count of its changed signals instead.</summary>
    private int ShapeContent(Shape3D? shape)
    {
        var hash = new HashCode();
        switch (shape)
        {
            case null: return 0;
            case BoxShape3D box: hash.Add(Millimetres(box.Size)); break;
            case SphereShape3D sphere: hash.Add(Millimetres(sphere.Radius)); break;
            case CapsuleShape3D capsule: hash.Add(Millimetres(capsule.Radius)); hash.Add(Millimetres(capsule.Height)); break;
            case CylinderShape3D cylinder: hash.Add(Millimetres(cylinder.Radius)); hash.Add(Millimetres(cylinder.Height)); break;
            default:
                // Hulls and meshes are counted by their changed signal (reading a hull's points each poll cost the magnified
                // island's 67 things a millisecond a poll); every shape emits it when its data is set.
                var id = shape.GetInstanceId();
                if (!_shapeVersions.ContainsKey(id))
                {
                    _shapeVersions[id] = 0;
                    shape.Changed += () => _shapeVersions[id] = _shapeVersions.GetValueOrDefault(id) + 1;
                }
                hash.Add(_shapeVersions[id]);
                break;
        }
        return hash.ToHashCode();
    }

    private static int Millimetres(float value) => Mathf.RoundToInt(value * 1000);

    private static int Millimetres(Vector3 value) => HashCode.Combine(Millimetres(value.X), Millimetres(value.Y), Millimetres(value.Z));

    /// <summary>Millimetre and milliradian resolution, so float noise never triggers a re-bake.</summary>
    private static int Quantize(Transform3D transform) =>
        HashCode.Combine(Millimetres(transform.Origin), Millimetres(transform.Basis.X), Millimetres(transform.Basis.Y), Millimetres(transform.Basis.Z));
}
