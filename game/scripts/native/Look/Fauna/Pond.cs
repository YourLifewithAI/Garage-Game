using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look.Fauna;

public enum WaterKind { Still, Flowing }

/// <summary>
/// One body of water in a built room, surveyed on a grid: where the surface is (from the water mesh itself) and where the bed
/// is (a downward ray to the world layer). Derived from the scene, never saved; fish live inside it.
/// </summary>
public sealed class Pond
{
    public int Index { get; init; }
    public WaterKind Kind { get; init; }
    /// <summary>The grid's corner (world x, z), its cell size in metres and its size in cells.</summary>
    public Vector2 Min { get; init; }
    public float Cell { get; init; }
    public int Nx { get; init; }
    public int Nz { get; init; }
    /// <summary>Surface and bed heights per cell; NaN where there is no water or no bed under it.</summary>
    public float[] Surface { get; init; } = Array.Empty<float>();
    public float[] Bed { get; init; } = Array.Empty<float>();

    public float Level { get; private set; }
    public float WetAreaM2 { get; private set; }
    public float MaxDepthM { get; private set; }
    public Vector3 Centroid { get; private set; }

    /// <summary>Fill in the summary numbers once the grid holds the surface and the bed.</summary>
    public void Summarise()
    {
        var levels = 0.0;
        var wet = 0;
        var sum = Vector3.Zero;
        var deepest = 0f;
        for (var i = 0; i < Surface.Length; i++)
        {
            if (float.IsNaN(Surface[i])) continue;
            levels += Surface[i];
            wet++;
            sum += CellCentre(i);
            if (!float.IsNaN(Bed[i])) deepest = Math.Max(deepest, Surface[i] - Bed[i]);
        }
        Level = wet > 0 ? (float)(levels / wet) : 0f;
        WetAreaM2 = wet * Cell * Cell;
        MaxDepthM = deepest;
        Centroid = wet > 0 ? sum / wet : Vector3.Zero;
    }

    /// <summary>The area, m², where the water is at least this deep.</summary>
    public float AreaDeeperThan(float depthM)
    {
        var count = 0;
        for (var i = 0; i < Surface.Length; i++)
            if (DepthOfCell(i) >= depthM) count++;
        return count * Cell * Cell;
    }

    public Vector3 CellCentre(int index)
    {
        var x = index % Nx;
        var z = index / Nx;
        var surface = Surface[index];
        return new Vector3(Min.X + (x + 0.5f) * Cell, float.IsNaN(surface) ? 0f : surface, Min.Y + (z + 0.5f) * Cell);
    }

    public bool TryCell(float x, float z, out int index)
    {
        var cx = (int)MathF.Floor((x - Min.X) / Cell);
        var cz = (int)MathF.Floor((z - Min.Y) / Cell);
        index = cz * Nx + cx;
        return cx >= 0 && cz >= 0 && cx < Nx && cz < Nz;
    }

    /// <summary>Water depth in a cell, metres; 0 where there is no water or no bed under it.</summary>
    public float DepthOfCell(int index)
    {
        var surface = Surface[index];
        var bed = Bed[index];
        return float.IsNaN(surface) || float.IsNaN(bed) ? 0f : surface - bed;
    }

    /// <summary>Water depth straight down at a point, metres; 0 on dry land or outside the pond.</summary>
    public float DepthAt(float x, float z) => TryCell(x, z, out var index) ? DepthOfCell(index) : 0f;

    public float SurfaceAt(float x, float z) => TryCell(x, z, out var index) ? Surface[index] : float.NaN;
    public float BedAt(float x, float z) => TryCell(x, z, out var index) ? Bed[index] : float.NaN;

    /// <summary>Which way the water gets deeper (unit, horizontal), from the depths around a point: where a fish turns at an edge.</summary>
    public Vector3 DeeperAt(float x, float z)
    {
        var step = Cell * 1.5f;
        var gx = DepthAt(x + step, z) - DepthAt(x - step, z);
        var gz = DepthAt(x, z + step) - DepthAt(x, z - step);
        var g = new Vector3(gx, 0f, gz);
        if (g.LengthSquared() > 1e-12f) return g.Normalized();
        var toward = Centroid - new Vector3(x, Centroid.Y, z);
        toward.Y = 0f;
        return toward.LengthSquared() > 1e-12f ? toward.Normalized() : Vector3.Zero;
    }

    /// <summary>Every cell at least this deep, in grid order.</summary>
    public IEnumerable<int> CellsDeeperThan(float depthM) => Enumerable.Range(0, Surface.Length).Where(i => DepthOfCell(i) >= depthM);
}

/// <summary>
/// Finds a built room's water and surveys it (Run 2, Lane L). Water is a shell part whose material role is "water" (the
/// exporter's still and flowing water: non-colliding backdrop). Its triangles are split into separate bodies (a tarn, a brook,
/// a distant lake), each rasterised onto a grid for its surface, and the bed under each wet cell is found by a ray straight
/// down to the world layer (1). This does not use Lane P's water layer (4); RoomWater could answer the same later.
/// </summary>
public static class PondSurvey
{
    public const uint WorldMask = 1;
    /// <summary>The finest grid cell, metres, and the most cells one body of water gets (a far lake gets coarse cells).</summary>
    public const float FinestCellM = 0.015f;
    public const int MaxCellsPerPond = 6400;
    /// <summary>How far below the surface the bed is looked for, metres.</summary>
    public const float MaxDepthM = 2f;

    /// <summary>The water meshes of a built room, and whether each runs (a brook) or stands (a pond).</summary>
    public static List<(MeshInstance3D Mesh, WaterKind Kind)> WaterMeshes(Node root)
    {
        var found = new List<(MeshInstance3D, WaterKind)>();
        foreach (var part in root.FindChildren("*", "Node3D", true, false).OfType<Node3D>().Prepend(root as Node3D).OfType<Node3D>())
        {
            if (!part.HasMeta("surface_role") || !part.HasMeta("material_role") || part.GetMeta("material_role").AsString() != "water") continue;
            var id = part.HasMeta("entity_id") ? part.GetMeta("entity_id").AsString() : part.Name.ToString();
            foreach (var mesh in part.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                if (mesh.Mesh == null) continue;
                found.Add((mesh, KindOf(mesh, id)));
            }
        }
        return found;
    }

    private static WaterKind KindOf(MeshInstance3D mesh, string id)
    {
        for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            if (LandscapeLook.TryRole(mesh.Mesh.SurfaceGetMaterial(surface), out var role)) return role == "flowing_water" ? WaterKind.Flowing : WaterKind.Still;
        return id.Contains("flowing", StringComparison.Ordinal) ? WaterKind.Flowing : WaterKind.Still;
    }

    /// <summary>A bed probe that casts a ray straight down to the world layer; returns the bed's height or null.</summary>
    public static Func<Vector3, float?> RayProbe(PhysicsDirectSpaceState3D space)
    {
        // One query for all the rays, its ends moved for each (S12 stopped leaving a query, an array and a key to the finalizer for
        // every ray; the first recorded play's survey then made a new query for each of its thousands and 80 MB of garbage in all).
        var query = PhysicsRayQueryParameters3D.Create(Vector3.Zero, Vector3.Down * MaxDepthM, WorldMask, NoExclusions);
        query.CollideWithAreas = false;
        query.HitBackFaces = false;
        return from =>
        {
            query.From = from;
            query.To = from + Vector3.Down * MaxDepthM;
            using var hit = space.IntersectRay(query);
            return hit.Count > 0 ? hit[HitPosition].AsVector3().Y : null;
        };
    }

    private static readonly Godot.Collections.Array<Rid> NoExclusions = new();
    private static readonly Variant HitPosition = "position";

    /// <summary>Survey every body of water under root, in a stable order (by where it lies), probing beds with bedBelow.</summary>
    public static List<Pond> Survey(Node root, Func<Vector3, float?> bedBelow)
    {
        var surveyor = new Surveyor(root, bedBelow, background: false);
        while (!surveyor.Step(double.PositiveInfinity)) { }
        return surveyor.Ponds.ToList();
    }

    /// <summary>
    /// The same survey spread over frames (the first recorded play: surveying the island's water in one frame stopped the game for
    /// 0.6 to 0.8 s as the room opened). The main thread only reads the meshes' triangles (one mesh a step) and casts the bed rays,
    /// a few at a time within the budget each step is given; splitting the water into bodies and laying each on its grid, which is
    /// plain arithmetic on the triangles, goes to a worker thread. The ponds come out exactly as the one-shot survey made them (the
    /// look suite compares the two).
    /// </summary>
    public sealed class Surveyor
    {
        private enum Stage { Read, Build, Probe, Done }

        private readonly Func<Vector3, float?> _bedBelow;
        private readonly bool _background;
        private readonly List<(MeshInstance3D Mesh, WaterKind Kind)> _meshes;
        private readonly List<(WaterKind Kind, List<(Vector3 A, Vector3 B, Vector3 C)> Triangles)> _read = new();
        private System.Threading.Tasks.Task<List<Pond>>? _build;
        private List<Pond> _ponds = new();
        private Stage _stage = Stage.Read;
        private int _next, _pond, _cell;

        /// <summary>The rays cast so far, and the steps the survey took, for the report.</summary>
        public int Rays { get; private set; }
        public int Steps { get; private set; }

        public Surveyor(Node root, Func<Vector3, float?> bedBelow, bool background = true)
        {
            _bedBelow = bedBelow;
            _background = background;
            _meshes = WaterMeshes(root);
        }

        public bool Done => _stage == Stage.Done;
        public IReadOnlyList<Pond> Ponds => _ponds;

        /// <summary>Work for about budgetMs on the calling (main) thread; true once the survey is complete.</summary>
        public bool Step(double budgetMs)
        {
            Steps++;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (_stage != Stage.Done)
            {
                switch (_stage)
                {
                    case Stage.Read:
                        if (_next < _meshes.Count)
                        {
                            var (mesh, kind) = _meshes[_next++];
                            _read.Add((kind, Triangles(mesh)));
                        }
                        else
                        {
                            var read = _read.ToList();
                            _build = _background ? System.Threading.Tasks.Task.Run(() => Build(read)) : System.Threading.Tasks.Task.FromResult(Build(read));
                            _stage = Stage.Build;
                        }
                        break;
                    case Stage.Build:
                        if (!_build!.IsCompleted) return false;
                        _ponds = _build.GetAwaiter().GetResult();
                        _stage = Stage.Probe;
                        break;
                    case Stage.Probe:
                        if (_pond >= _ponds.Count) { foreach (var pond in _ponds) pond.Summarise(); _stage = Stage.Done; break; }
                        for (var batch = 0; batch < 32 && _pond < _ponds.Count; batch++)
                        {
                            var pond = _ponds[_pond];
                            if (_cell >= pond.Surface.Length) { _pond++; _cell = 0; continue; }
                            ProbeBed(pond, _cell++, _bedBelow);
                            Rays++;
                        }
                        break;
                }
                if (clock.Elapsed.TotalMilliseconds >= budgetMs) break;
            }
            return _stage == Stage.Done;
        }

        /// <summary>Everything that needs no scene: the bodies of water, in a stable order, each laid on its grid (beds still unknown).</summary>
        private static List<Pond> Build(List<(WaterKind Kind, List<(Vector3 A, Vector3 B, Vector3 C)> Triangles)> read)
        {
            var bodies = new List<(WaterKind Kind, List<(Vector3 A, Vector3 B, Vector3 C)> Triangles)>();
            foreach (var (kind, triangles) in read)
                foreach (var body in Bodies(triangles))
                    bodies.Add((kind, body));
            var ordered = bodies
                .Select(b => (b.Kind, b.Triangles, Centre: b.Triangles.Aggregate(Vector3.Zero, (sum, t) => sum + (t.A + t.B + t.C) / 3f) / Math.Max(1, b.Triangles.Count)))
                .OrderBy(b => b.Kind).ThenBy(b => MathF.Round(b.Centre.X, 3)).ThenBy(b => MathF.Round(b.Centre.Z, 3)).ToList();
            var ponds = new List<Pond>();
            foreach (var (kind, triangles, _) in ordered) ponds.Add(Rasterise(ponds.Count, kind, triangles));
            return ponds;
        }
    }

    /// <summary>A water mesh's triangles in world space.</summary>
    private static List<(Vector3 A, Vector3 B, Vector3 C)> Triangles(MeshInstance3D mesh)
    {
        var transform = mesh.IsInsideTree() ? mesh.GlobalTransform : mesh.Transform;
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.Mesh is ArrayMesh array && array.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles) continue;
            var arrays = mesh.Mesh.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.PackedInt32Array ? arrays[(int)Mesh.ArrayType.Index].AsInt32Array() : Enumerable.Range(0, vertices.Length).ToArray();
            for (var i = 0; i + 2 < indices.Length; i += 3)
                triangles.Add((transform * vertices[indices[i]], transform * vertices[indices[i + 1]], transform * vertices[indices[i + 2]]));
        }
        return triangles;
    }

    /// <summary>Triangles split into connected bodies (shared corners, welded to a millimetre).</summary>
    private static List<List<(Vector3 A, Vector3 B, Vector3 C)>> Bodies(List<(Vector3, Vector3, Vector3)> triangles)
    {
        // Union-find over welded corners.
        var keys = new Dictionary<(long, long, long), int>();
        var parent = new List<int>();
        int Key(Vector3 v)
        {
            var k = ((long)MathF.Round(v.X * 1000f), (long)MathF.Round(v.Y * 1000f), (long)MathF.Round(v.Z * 1000f));
            if (keys.TryGetValue(k, out var id)) return id;
            keys[k] = parent.Count;
            parent.Add(parent.Count);
            return parent.Count - 1;
        }
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var corners = triangles.Select(t => (Key(t.Item1), Key(t.Item2), Key(t.Item3))).ToArray();
        foreach (var (a, b, c) in corners)
        {
            parent[Find(b)] = Find(a);
            parent[Find(c)] = Find(a);
        }
        var groups = new Dictionary<int, List<(Vector3, Vector3, Vector3)>>();
        for (var i = 0; i < triangles.Count; i++)
        {
            var root = Find(corners[i].Item1);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = new();
            list.Add(triangles[i]);
        }
        return groups.Values.ToList();
    }

    private static Pond Rasterise(int index, WaterKind kind, List<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var (a, b, c) in triangles)
        {
            min = new Vector2(Math.Min(min.X, Math.Min(a.X, Math.Min(b.X, c.X))), Math.Min(min.Y, Math.Min(a.Z, Math.Min(b.Z, c.Z))));
            max = new Vector2(Math.Max(max.X, Math.Max(a.X, Math.Max(b.X, c.X))), Math.Max(max.Y, Math.Max(a.Z, Math.Max(b.Z, c.Z))));
        }
        var size = max - min;
        var cell = Math.Max(FinestCellM, MathF.Sqrt(Math.Max(size.X * size.Y, 1e-6f) / MaxCellsPerPond));
        var nx = Math.Max(1, (int)MathF.Ceiling(size.X / cell));
        var nz = Math.Max(1, (int)MathF.Ceiling(size.Y / cell));
        var surface = Enumerable.Repeat(float.NaN, nx * nz).ToArray();
        var bed = Enumerable.Repeat(float.NaN, nx * nz).ToArray();
        foreach (var (a, b, c) in triangles)
        {
            var x0 = Math.Max(0, (int)MathF.Floor((Math.Min(a.X, Math.Min(b.X, c.X)) - min.X) / cell));
            var x1 = Math.Min(nx - 1, (int)MathF.Floor((Math.Max(a.X, Math.Max(b.X, c.X)) - min.X) / cell));
            var z0 = Math.Max(0, (int)MathF.Floor((Math.Min(a.Z, Math.Min(b.Z, c.Z)) - min.Y) / cell));
            var z1 = Math.Min(nz - 1, (int)MathF.Floor((Math.Max(a.Z, Math.Max(b.Z, c.Z)) - min.Y) / cell));
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    var px = min.X + (x + 0.5f) * cell;
                    var pz = min.Y + (z + 0.5f) * cell;
                    if (Barycentric(a, b, c, px, pz) is { } w) surface[z * nx + x] = a.Y * w.X + b.Y * w.Y + c.Y * w.Z;
                }
        }
        return new Pond { Index = index, Kind = kind, Min = min, Cell = cell, Nx = nx, Nz = nz, Surface = surface, Bed = bed };
    }

    /// <summary>The bed under one wet cell of a pond's grid, by a ray straight down (left unknown where there is none).</summary>
    private static void ProbeBed(Pond pond, int i, Func<Vector3, float?> bedBelow)
    {
        var surface = pond.Surface[i];
        if (float.IsNaN(surface)) return;
        var x = i % pond.Nx;
        var z = i / pond.Nx;
        var from = new Vector3(pond.Min.X + (x + 0.5f) * pond.Cell, surface + 0.002f, pond.Min.Y + (z + 0.5f) * pond.Cell);
        if (bedBelow(from) is { } y && y < surface) pond.Bed[i] = y;
    }

    /// <summary>The barycentric weights of (x, z) in a triangle seen from above, or null outside it.</summary>
    private static Vector3? Barycentric(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-12f) return null;
        var wa = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
        var wb = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
        var wc = 1f - wa - wb;
        const float edge = -1e-5f;
        return wa >= edge && wb >= edge && wc >= edge ? new Vector3(wa, wb, wc) : null;
    }
}
