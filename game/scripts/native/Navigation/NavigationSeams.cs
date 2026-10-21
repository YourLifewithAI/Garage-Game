using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Navigation;

/// <summary>
/// Makes neighbouring tiles' seams match corner for corner, so the navigation map joins them by its own exact edge keys.
/// <para>
/// Godot's map joins two regions either where their polygon edges have the same corners (cheap: one hash per edge) or, with
/// edge connections on, where free edges lie within a margin of each other. The margin search compares every free edge with
/// every other (S7 measured it on the magnified island: 80,000 polygons in 90 tiles took the main thread 18.6 s to join), so
/// it is off. Tiles baked on one cell lattice with their boxes grown by the border (RoomNavigation.LayOutTiles) meet on the
/// seam at the same corners almost everywhere: on the magnified island all but about 30 corners, where one side's Recast cut
/// the seam at a corner the other's did not, or the two put the same corner a float's noise apart. Here every corner on a
/// seam, from either side, is put on both: corners within a cell of each other become one (the lower-numbered tile's), and
/// each tile's seam edges are cut at the other side's corners. Every seam edge then has its twin, and the map joins it.
/// </para>
/// </summary>
public static class NavigationSeams
{
    /// <summary>The tile lattice: seams at X0 + i Step (between columns) and Z0 + j Step (between rows).</summary>
    public readonly record struct Layout(float X0, float Z0, float Step, int Columns, int Rows, float Cell);

    /// <summary>Corners closer than this along a seam become one: under a 2 cm cell, so the map never keys two corners of one seam to the same cell.</summary>
    public const float SameCornerM = 0.019f;
    private const float OnSeamM = 0.001f;

    private readonly record struct SeamPoint(int Tile, Vector3 Position, int XLine, int ZLine);

    /// <summary>The conformed geometry of the given tiles, from their own (raw) geometry and their neighbours'.</summary>
    public static Dictionary<int, NavigationIslands.TileGeometry> Conform(NavigationIslands.TileGeometry?[] raw, IEnumerable<int> which, Layout layout)
    {
        var wanted = which.ToList();
        // The seams of the wanted tiles see only them and their neighbours: the corners on a seam come from the two tiles
        // either side of it (four at a lattice corner).
        var near = new SortedSet<int>();
        foreach (var t in wanted)
        {
            var (column, row) = (t % layout.Columns, t / layout.Columns);
            for (var r = Math.Max(0, row - 1); r <= Math.Min(layout.Rows - 1, row + 1); r++)
                for (var c = Math.Max(0, column - 1); c <= Math.Min(layout.Columns - 1, column + 1); c++)
                    near.Add(r * layout.Columns + c);
        }
        var points = new List<SeamPoint>();
        foreach (var t in near)
        {
            if (raw[t] is not { } tile) continue;
            var (column, row) = (t % layout.Columns, t / layout.Columns);
            var seen = new HashSet<Vector3>();
            foreach (var v in tile.Vertices)
            {
                var x = SeamLine(v.X, layout.X0, layout.Step, column, layout.Columns);
                var z = SeamLine(v.Z, layout.Z0, layout.Step, row, layout.Rows);
                if ((x >= 0 || z >= 0) && seen.Add(v)) points.Add(new SeamPoint(t, v, x, z));
            }
        }
        // Lattice corners first: every tile round one takes the lowest-numbered tile's corner there.
        var cornerRep = new Dictionary<(int, int), Vector3>();
        foreach (var group in points.Where(p => p.XLine >= 0 && p.ZLine >= 0).GroupBy(p => (p.XLine, p.ZLine)))
            cornerRep[group.Key] = group.OrderBy(p => p.Tile).First().Position;
        var rep = new Dictionary<(int Tile, Vector3 Position), Vector3>();
        // Per tile and seam line, the corners of the other side to cut its seam edges at (along the line, and the corner).
        var inserts = new Dictionary<(int Tile, bool XLine, int Line), List<(float Along, Vector3 Corner)>>();
        foreach (var alongZ in new[] { true, false })
        {
            var lines = points.Where(p => (alongZ ? p.XLine : p.ZLine) >= 0).GroupBy(p => alongZ ? p.XLine : p.ZLine);
            foreach (var line in lines)
            {
                var sorted = line.OrderBy(p => Along(p.Position, alongZ)).ThenBy(p => p.Tile).ToList();
                var i = 0;
                while (i < sorted.Count)
                {
                    // A cluster: corners within SameCornerM of its first, at most one from each tile.
                    var cluster = new List<SeamPoint> { sorted[i] };
                    var j = i + 1;
                    while (j < sorted.Count && Along(sorted[j].Position, alongZ) - Along(sorted[i].Position, alongZ) < SameCornerM &&
                           cluster.All(c => c.Tile != sorted[j].Tile))
                        cluster.Add(sorted[j++]);
                    i = j;
                    var corners = cluster.Where(c => c.XLine >= 0 && c.ZLine >= 0).ToList();
                    var position = corners.Count > 0 ? cornerRep[(corners[0].XLine, corners[0].ZLine)] : cluster.OrderBy(c => c.Tile).First().Position;
                    foreach (var member in cluster) rep[(member.Tile, member.Position)] = position;
                    // A lattice corner is the end of every seam edge that reaches it, never inside one: nothing to cut.
                    if (corners.Count > 0) continue;
                    // The two tiles either side of the line at this point; the one with no corner here cuts its edges at it.
                    var across = alongZ ? (int)Math.Floor((position.Z - layout.Z0) / layout.Step) : (int)Math.Floor((position.X - layout.X0) / layout.Step);
                    foreach (var side in new[] { line.Key - 1, line.Key })
                    {
                        var (column, row) = alongZ ? (side, across) : (across, side);
                        if (column < 0 || row < 0 || column >= layout.Columns || row >= layout.Rows) continue;
                        var tile = row * layout.Columns + column;
                        if (cluster.Any(c => c.Tile == tile)) continue;
                        if (!inserts.TryGetValue((tile, alongZ, line.Key), out var list)) inserts[(tile, alongZ, line.Key)] = list = new();
                        list.Add((Along(position, alongZ), position));
                    }
                }
            }
        }
        foreach (var list in inserts.Values) list.Sort((a, b) => a.Along.CompareTo(b.Along));
        var result = new Dictionary<int, NavigationIslands.TileGeometry>();
        foreach (var t in wanted)
        {
            if (raw[t] is not { } tile) continue;
            result[t] = ConformTile(tile, t, layout, rep, inserts);
        }
        return result;
    }

    private static NavigationIslands.TileGeometry ConformTile(NavigationIslands.TileGeometry raw, int t, Layout layout,
        Dictionary<(int Tile, Vector3 Position), Vector3> rep, Dictionary<(int Tile, bool XLine, int Line), List<(float Along, Vector3 Corner)>> inserts)
    {
        var (column, row) = (t % layout.Columns, t / layout.Columns);
        var vertices = new List<Vector3>(raw.Vertices.Length + 16);
        foreach (var v in raw.Vertices) vertices.Add(rep.TryGetValue((t, v), out var r) ? r : v);
        var added = new Dictionary<Vector3, int>();
        var start = new List<int>(raw.Count + 1);
        var indices = new List<int>(raw.Indices.Length + 16);
        for (var p = 0; p < raw.Count; p++)
        {
            start.Add(indices.Count);
            var first = raw.Start[p];
            var last = raw.Start[p + 1];
            for (var k = first; k < last; k++)
            {
                var ia = raw.Indices[k];
                var ib = raw.Indices[k + 1 < last ? k + 1 : first];
                indices.Add(ia);
                var (a, b) = (vertices[ia], vertices[ib]);
                foreach (var alongZ in new[] { true, false })
                {
                    var line = SeamLine(alongZ ? a.X : a.Z, alongZ ? layout.X0 : layout.Z0, layout.Step, alongZ ? column : row, alongZ ? layout.Columns : layout.Rows);
                    if (line < 0 || SeamLine(alongZ ? b.X : b.Z, alongZ ? layout.X0 : layout.Z0, layout.Step, alongZ ? column : row, alongZ ? layout.Columns : layout.Rows) != line) continue;
                    if (!inserts.TryGetValue((t, alongZ, line), out var cuts)) continue;
                    var (from, to) = (Along(a, alongZ), Along(b, alongZ));
                    var low = Math.Min(from, to) + 1e-4f;
                    var high = Math.Max(from, to) - 1e-4f;
                    var between = cuts.Where(c => c.Along > low && c.Along < high);
                    foreach (var (_, corner) in from <= to ? between : between.Reverse())
                    {
                        if (!added.TryGetValue(corner, out var index))
                        {
                            index = vertices.Count;
                            vertices.Add(corner);
                            added[corner] = index;
                        }
                        indices.Add(index);
                    }
                }
            }
        }
        start.Add(indices.Count);
        var geometry = new NavigationIslands.TileGeometry { Tile = t, Vertices = vertices.ToArray(), Start = start.ToArray(), Indices = indices.ToArray() };
        geometry.BuildGrid();
        return geometry;
    }

    /// <summary>The inner seam line a coordinate lies on, of the two either side of the tile's column (or row); -1 for none.</summary>
    private static int SeamLine(float value, float origin, float step, int index, int count)
    {
        foreach (var line in new[] { index, index + 1 })
        {
            if (line <= 0 || line >= count) continue;
            if (Math.Abs(value - (origin + line * step)) < OnSeamM) return line;
        }
        return -1;
    }

    private static float Along(Vector3 v, bool alongZ) => alongZ ? v.Z : v.X;
}
