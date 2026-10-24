using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Navigation;

/// <summary>
/// The walkable islands of a tiled bake: which polygons a walk can get between. Built on a worker after each bake from the
/// tiles' polygons, joined exactly as the navigation map joins them (two polygons whose edge has the same two corners, each
/// corner taken to the map's cell, as Godot's map keys them), so a route search can say at once that a goal on another
/// island is out of reach. S1 measured such a search on the magnified island at 10 to 36 ms of the main thread: it covered
/// everywhere the start could walk.
/// </summary>
public sealed class NavigationIslands
{
    /// <summary>One tile's polygons, as plain arrays, with a grid to find the polygon under a point.</summary>
    public sealed class TileGeometry
    {
        public int Tile;
        public Vector3[] Vertices = Array.Empty<Vector3>();
        /// <summary>Polygon i's corners are Indices[Start[i] .. Start[i + 1]).</summary>
        public int[] Start = new[] { 0 };
        public int[] Indices = Array.Empty<int>();
        public int Count => Start.Length - 1;
        /// <summary>Each corner's map key (Key), worked out once for the tile.</summary>
        public long[]? Keys;
        private float _gridX0, _gridZ0;
        private int _gridColumns, _gridRows;
        private List<int>[] _grid = Array.Empty<List<int>>();
        private const float GridCellM = 0.25f;

        public static TileGeometry From(int tile, NavigationMesh mesh)
        {
            var count = mesh.GetPolygonCount();
            var start = new int[count + 1];
            var indices = new List<int>(count * 3);
            for (var i = 0; i < count; i++)
            {
                start[i] = indices.Count;
                indices.AddRange(mesh.GetPolygon(i));
            }
            start[count] = indices.Count;
            return new TileGeometry { Tile = tile, Vertices = mesh.Vertices, Start = start, Indices = indices.ToArray() };
        }

        /// <summary>The polygons as a navigation mesh for the map (on the map's cells, so it never warns of a mismatch).</summary>
        public NavigationMesh ToMesh(float cell, float cellHeight)
        {
            var mesh = new NavigationMesh { CellSize = cell, CellHeight = cellHeight };
            mesh.SetVertices(Vertices);
            for (var p = 0; p < Count; p++) mesh.AddPolygon(Indices.AsSpan(Start[p], Start[p + 1] - Start[p]));
            return mesh;
        }

        public void BuildGrid()
        {
            if (Vertices.Length == 0 || Count == 0) return;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var v in Vertices) { minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X); minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z); }
            _gridX0 = minX;
            _gridZ0 = minZ;
            _gridColumns = Math.Max(1, (int)Math.Ceiling((maxX - minX) / GridCellM) + 1);
            _gridRows = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / GridCellM) + 1);
            _grid = new List<int>[_gridColumns * _gridRows];
            for (var p = 0; p < Count; p++)
            {
                float pMinX = float.MaxValue, pMinZ = float.MaxValue, pMaxX = float.MinValue, pMaxZ = float.MinValue;
                for (var k = Start[p]; k < Start[p + 1]; k++)
                {
                    var v = Vertices[Indices[k]];
                    pMinX = Math.Min(pMinX, v.X); pMaxX = Math.Max(pMaxX, v.X); pMinZ = Math.Min(pMinZ, v.Z); pMaxZ = Math.Max(pMaxZ, v.Z);
                }
                var (c0, r0) = Cell(pMinX - 0.001f, pMinZ - 0.001f);
                var (c1, r1) = Cell(pMaxX + 0.001f, pMaxZ + 0.001f);
                for (var r = r0; r <= r1; r++)
                    for (var c = c0; c <= c1; c++)
                        (_grid[r * _gridColumns + c] ??= new List<int>()).Add(p);
            }
        }

        /// <summary>The closest point to a point on any polygon whose cells lie within a square round it (and how far it is), or none.</summary>
        public void ClosestWithin(Vector3 point, float half, ref Vector3 best, ref float bestDistance)
        {
            if (_grid.Length == 0) return;
            var (c0, r0) = Cell(point.X - half, point.Z - half);
            var (c1, r1) = Cell(point.X + half, point.Z + half);
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                {
                    var list = _grid[r * _gridColumns + c];
                    if (list == null) continue;
                    foreach (var p in list)
                    {
                        var first = Vertices[Indices[Start[p]]];
                        for (var k = Start[p] + 1; k + 1 < Start[p + 1]; k++)
                        {
                            var on = ClosestOnTriangle(point, first, Vertices[Indices[k]], Vertices[Indices[k + 1]]);
                            var distance = on.DistanceSquaredTo(point);
                            if (distance < bestDistance) (best, bestDistance) = (on, distance);
                        }
                    }
                }
        }

        /// <summary>The bounds of the tile's polygons on the ground plane (min x, min z, max x, max z).</summary>
        public (float MinX, float MinZ, float MaxX, float MaxZ) Extent => (_gridX0, _gridZ0, _gridX0 + _gridColumns * GridCellM, _gridZ0 + _gridRows * GridCellM);

        /// <summary>Closest point on a triangle (Ericson, Real-Time Collision Detection 5.1.5).</summary>
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            var d1 = ab.Dot(ap);
            var d2 = ac.Dot(ap);
            if (d1 <= 0 && d2 <= 0) return a;
            var bp = p - b;
            var d3 = ab.Dot(bp);
            var d4 = ac.Dot(bp);
            if (d3 >= 0 && d4 <= d3) return b;
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            var d5 = ab.Dot(cp);
            var d6 = ac.Dot(cp);
            if (d6 >= 0 && d5 <= d6) return c;
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
            var denominator = 1 / (va + vb + vc);
            return a + ab * (vb * denominator) + ac * (vc * denominator);
        }

        private (int Column, int Row) Cell(float x, float z) =>
            (Math.Clamp((int)Math.Floor((x - _gridX0) / GridCellM), 0, _gridColumns - 1), Math.Clamp((int)Math.Floor((z - _gridZ0) / GridCellM), 0, _gridRows - 1));

        /// <summary>The polygon under (or nearest beside, within 1 cm) a point on the mesh, with how far off its surface the point is; -1 when none.</summary>
        public (int Polygon, float Off) PolygonAt(Vector3 point)
        {
            if (_grid.Length == 0) return (-1, float.PositiveInfinity);
            var (c, r) = Cell(point.X, point.Z);
            var list = _grid[r * _gridColumns + c];
            if (list == null) return (-1, float.PositiveInfinity);
            var best = -1;
            var bestOff = float.PositiveInfinity;
            foreach (var p in list)
            {
                var off = Distance(p, point);
                if (off < bestOff) (best, bestOff) = (p, off);
            }
            return (best, bestOff);
        }

        /// <summary>Distance from a point to a polygon: across (outside its outline, on the ground plane) plus up or down to its surface.</summary>
        private float Distance(int polygon, Vector3 point)
        {
            var first = Vertices[Indices[Start[polygon]]];
            var best = float.PositiveInfinity;
            for (var k = Start[polygon] + 1; k + 1 < Start[polygon + 1]; k++)
            {
                var (across, height) = TriangleDistance(first, Vertices[Indices[k]], Vertices[Indices[k + 1]], point);
                best = Math.Min(best, across + height);
            }
            return best;
        }

        private static (float Across, float Height) TriangleDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
        {
            var v0 = new Vector2(b.X - a.X, b.Z - a.Z);
            var v1 = new Vector2(c.X - a.X, c.Z - a.Z);
            var v2 = new Vector2(p.X - a.X, p.Z - a.Z);
            var d = v0.X * v1.Y - v1.X * v0.Y;
            if (Math.Abs(d) < 1e-12f) return (float.PositiveInfinity, float.PositiveInfinity);
            var u = (v2.X * v1.Y - v1.X * v2.Y) / d;
            var w = (v0.X * v2.Y - v2.X * v0.Y) / d;
            var height = a.Y + u * (b.Y - a.Y) + w * (c.Y - a.Y);
            if (u >= -1e-4f && w >= -1e-4f && u + w <= 1 + 1e-4f) return (0, Math.Abs(height - p.Y));
            var flat = new Vector2(p.X, p.Z);
            var across = Math.Min(SegmentDistance(flat, new Vector2(a.X, a.Z), new Vector2(b.X, b.Z)),
                Math.Min(SegmentDistance(flat, new Vector2(b.X, b.Z), new Vector2(c.X, c.Z)), SegmentDistance(flat, new Vector2(c.X, c.Z), new Vector2(a.X, a.Z))));
            return (across, Math.Abs(height - p.Y));
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
            return (a + ab * t).DistanceTo(p);
        }
    }

    public int Revision { get; private set; }
    /// <summary>How many islands (sets of polygons a walk can get between).</summary>
    public int Count { get; private set; }

    private readonly TileGeometry?[] _tiles;
    private readonly int[] _offsets;
    private readonly int[] _island;
    private readonly NavigationSeams.Layout _layout;

    private NavigationIslands(TileGeometry?[] tiles, int[] offsets, int[] island, int count, NavigationSeams.Layout layout)
    {
        _tiles = tiles;
        _offsets = offsets;
        _island = island;
        Count = count;
        _layout = layout;
    }

    /// <summary>The bake these islands belong to, set when its tiles go to the map.</summary>
    public void SetRevision(int revision) => Revision = revision;

    /// <summary>From the map's space (where routes and closest points are) to the tiles' (the source root's).</summary>
    public Transform3D ToTiles { get; set; } = Transform3D.Identity;

    /// <summary>The island of the polygon under a point on the mesh (as the map's closest point gives it); -1 when none is found.</summary>
    public int IslandAt(Vector3 point)
    {
        point = ToTiles * point;
        var (step, columns, rows) = (_layout.Step, _layout.Columns, _layout.Rows);
        var column = Math.Clamp((int)Math.Floor((point.X - _layout.X0) / step), 0, columns - 1);
        var row = Math.Clamp((int)Math.Floor((point.Z - _layout.Z0) / step), 0, rows - 1);
        var best = -1;
        var bestOff = 0.01f;
        // On a seam the point may belong to either side: the tiles round it whose square (and a centimetre) holds it too.
        for (var dr = -1; dr <= 1; dr++)
            for (var dc = -1; dc <= 1; dc++)
            {
                int c = column + dc, r = row + dr;
                if (c < 0 || r < 0 || c >= columns || r >= rows) continue;
                if ((dc != 0 || dr != 0) &&
                    (point.X < _layout.X0 + c * step - 0.01f || point.X > _layout.X0 + (c + 1) * step + 0.01f ||
                     point.Z < _layout.Z0 + r * step - 0.01f || point.Z > _layout.Z0 + (r + 1) * step + 0.01f))
                    continue;
                var tile = _tiles[r * columns + c];
                if (tile == null) continue;
                var (polygon, off) = tile.PolygonAt(point);
                if (polygon < 0 || off > bestOff) continue;
                (best, bestOff) = (_island[_offsets[tile.Tile] + polygon], off);
            }
        return best;
    }

    /// <summary>
    /// The closest point on the tiles' polygons to a point (map space), as the map's own closest point would give it, looked up
    /// in the tiles' grids round the point, widening until nothing farther could be closer. Godot's map looks through every
    /// polygon (2.7 ms a call on the magnified island's 80,000, and the Gubble asks every tick while it follows). Null when
    /// no polygon lies within limitM.
    /// </summary>
    public Vector3? ClosestPoint(Vector3 point, float limitM)
    {
        var local = ToTiles * point;
        var best = local;
        var bestDistance = float.PositiveInfinity;
        for (var half = 0.25f; ; half *= 2)
        {
            foreach (var tile in _tiles)
            {
                if (tile == null) continue;
                var (minX, minZ, maxX, maxZ) = tile.Extent;
                if (local.X + half < minX || local.X - half > maxX || local.Z + half < minZ || local.Z - half > maxZ) continue;
                tile.ClosestWithin(local, half, ref best, ref bestDistance);
            }
            // Anything not yet looked at lies more than half away across, so farther than half.
            if (bestDistance <= half * half) break;
            if (half > limitM) return null;
        }
        return ToTiles.AffineInverse() * best;
    }

    /// <summary>The working memory of Build, kept from one bake to the next (the bake's worker's alone): rebuilding a map of 80,000 polygons allocated megabytes each time, and the garbage collections that followed held ticks.</summary>
    public sealed class Scratch
    {
        public readonly Dictionary<(long, long), int> Edges = new();
        public int[] Parent = Array.Empty<int>();
    }

    /// <summary>Join polygons whose edges share both corners, keyed to the map's cells as the map keys them; number the islands.</summary>
    public static NavigationIslands Build(int number, TileGeometry?[] tiles, NavigationSeams.Layout layout, float cellHeight, Scratch scratch)
    {
        var offsets = new int[tiles.Length + 1];
        for (var t = 0; t < tiles.Length; t++) offsets[t + 1] = offsets[t] + (tiles[t]?.Count ?? 0);
        var total = offsets[tiles.Length];
        if (scratch.Parent.Length < total) scratch.Parent = new int[total + total / 4];
        var parent = scratch.Parent;
        for (var i = 0; i < total; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        var edges = scratch.Edges;
        edges.Clear();
        for (var t = 0; t < tiles.Length; t++)
        {
            var tile = tiles[t];
            if (tile == null) continue;
            var keys = tile.Keys;
            if (keys == null)
            {
                keys = new long[tile.Vertices.Length];
                for (var v = 0; v < keys.Length; v++) keys[v] = Key(tile.Vertices[v], layout.Cell, cellHeight);
                tile.Keys = keys;
            }
            for (var p = 0; p < tile.Count; p++)
            {
                var global = offsets[t] + p;
                var first = tile.Start[p];
                var last = tile.Start[p + 1];
                for (var k = first; k < last; k++)
                {
                    var ka = keys[tile.Indices[k]];
                    var kb = keys[tile.Indices[k + 1 < last ? k + 1 : first]];
                    var key = ka < kb ? (ka, kb) : (kb, ka);
                    if (edges.TryGetValue(key, out var other))
                    {
                        var (a, b) = (Find(global), Find(other));
                        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                    }
                    else edges[key] = global;
                }
            }
        }
        var island = new int[total];
        var count = 0;
        // A root is its own parent and the lowest of its set (Union keeps the lower), so the sets number in one pass.
        for (var i = 0; i < total; i++)
        {
            var root = Find(i);
            island[i] = root == i ? count++ : island[root];
        }
        return new NavigationIslands(tiles, offsets, island, count, layout) { Revision = -number };
    }

    /// <summary>A corner as the map keys it: its cell (the map's cell size across, cell height up), packed 21 bits an axis.</summary>
    public static long Key(Vector3 v, float cell, float cellHeight) =>
        ((long)MathF.Floor(v.X / cell) + (1 << 20) & 0x1FFFFF) << 42 | ((long)MathF.Floor(v.Y / cellHeight) + (1 << 20) & 0x1FFFFF) << 21 | ((long)MathF.Floor(v.Z / cell) + (1 << 20) & 0x1FFFFF);
}
