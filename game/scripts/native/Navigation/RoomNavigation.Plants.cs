using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Navigation;

/// <summary>
/// The trees as nature has them (Run 2, Lane P, "the navigation follows grown and burnt trees"): the room's data says where a
/// tree's trunk stands at its natural size (IslandNavigation), and the island's nature (the command host's, which this node does
/// not own) says what Grow and fire did to it since: a grown tree's trunk is the same shape larger (PlantCollision.Grown, the very
/// shape the copies' view collides), a tree burnt down has none. Each tile bakes the trunks as nature has them, and a change of
/// nature re-bakes only the tiles round the trees that changed (a cast, a fire burning a tree down, Creative's undo, a load alike:
/// the navigation reads the state, never the cause).
/// <para>
/// The host says when nature moved (its NatureChanged event: a command, a load, or a simulation step that changed anything); the
/// next poll (PollIntervalS) reads the plants nature lists as other than natural, compares them with what the last bake took, and
/// marks the changed trees' boxes dirty, as MarkDirty does for a moved body. Nothing is read while nature is quiet. A bake takes a
/// snapshot of the sizes on the main thread, so the workers never read nature.
/// </para>
/// </summary>
public partial class RoomNavigation
{
    /// <summary>A copy that obstructs (a tree's trunk, a solid copy) at a size nature gave it, resolved to its row once.</summary>
    private sealed record PlantSize(string Id, PlantSet Set, int Row, float Factor);

    /// <summary>The command host whose nature the trees follow (Create's nature); null leaves every tree at its natural size.</summary>
    private Kernel.CommandHost? _nature;
    /// <summary>Nature has to be read: at the first bake, and after the host says it changed.</summary>
    private bool _naturePending = true;
    /// <summary>The sizes the last bake took, or the bake about to start will: obstructing copies at other than natural size. Never changed once made.</summary>
    private Dictionary<string, PlantSize> _plantsSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _plantScratch = new(StringComparer.Ordinal);
    /// <summary>The copies that obstruct, resolved once per id (those sets only: nature may list thousands of grass tufts that never do).</summary>
    private readonly Dictionary<string, (PlantSet Set, int Row)?> _obstructing = new(StringComparer.Ordinal);
    private string[]? _obstructingPrefixes;

    /// <summary>Trees and solid copies at other than their natural size, as the last bake took them (grown, and burnt down).</summary>
    public int PlantsFollowed => _plantsSeen.Count;
    /// <summary>Of them, the copies burnt down (no obstacle at all).</summary>
    public int PlantsBurnt => _plantsSeen.Values.Count(p => p.Factor <= 0);
    /// <summary>The main thread's time reading nature at the last poll that looked, and the longest of them (milliseconds).</summary>
    public double LastPlantScanMs { get; private set; }
    public double WorstPlantScanMs { get; private set; }
    /// <summary>Polls that found nature's sizes changed, and trees marked dirty by them, since the node was made.</summary>
    public int PlantChangesSeen { get; private set; }
    public int PlantsMarkedDirty { get; private set; }

    private void OnNatureChanged() => _naturePending = true;

    /// <summary>Whether a set's copies obstruct: a tree's trunk, or a solid copy with a hull (the shapes IslandNavigation gives).</summary>
    private static bool Obstructs(PlantSet set) => set.Kind == PlantKind.Tree || set.Collides == "solid" && set.SolidPoints.Count > 0;

    /// <summary>The copy an id names when it obstructs (a tree, or a solid copy with a hull), else null.</summary>
    private (PlantSet Set, int Row)? ObstructingCopy(string id)
    {
        if (_obstructing.TryGetValue(id, out var known)) return known;
        if (Room?.Copies is not { } copies) return null;
        // Only an id of an obstructing set is read (exactly, as the room reads ids); grass and flowers are told apart by their prefix.
        if (_obstructingPrefixes == null)
        {
            var prefixes = new List<string>();
            foreach (var each in copies.Sets)
                if (Obstructs(each)) prefixes.Add(PlantCopies.IdOf(each.Name, 0)[..^1]);
            _obstructingPrefixes = prefixes.ToArray();
        }
        var named = false;
        foreach (var prefix in _obstructingPrefixes)
            if (id.StartsWith(prefix, StringComparison.Ordinal)) { named = true; break; }
        if (!named) return null;
        (PlantSet, int)? found = copies.TryResolve(id, out var set, out var row) && Obstructs(set) ? (set, row) : null;
        _obstructing[id] = found;
        return found;
    }

    /// <summary>
    /// Read nature and compare it with the sizes the bakes have: if a tree's size differs, mark the tiles it touches dirty (at its
    /// largest, so a trunk that shrank or vanished clears the cells it took) and keep the new sizes for the next bake. Returns
    /// whether anything differed. The scan is one pass over the plants nature keeps (grown and burnt: 12,288 at the most), only
    /// when the host said nature changed.
    /// </summary>
    private bool DetectPlants()
    {
        if (!_naturePending) return false;
        _naturePending = false;
        if (_nature == null || !IsInstanceValid(_nature) || Room?.Copies == null) return false;
        var started = Stopwatch.GetTimestamp();
        try
        {
            _plantScratch.Clear();
            foreach (var (id, plant) in _nature.Nature.Plants)
                if (plant.Shown != 1f) _plantScratch[id] = plant.Shown;
            var obstructing = 0;
            var differs = false;
            foreach (var (id, factor) in _plantScratch)
            {
                if (ObstructingCopy(id) == null) continue;
                obstructing++;
                if (!_plantsSeen.TryGetValue(id, out var was) || was.Factor != factor) differs = true;
            }
            if (!differs && obstructing == _plantsSeen.Count) return false;
            var next = new Dictionary<string, PlantSize>(obstructing, StringComparer.Ordinal);
            foreach (var (id, factor) in _plantScratch)
                if (ObstructingCopy(id) is { } copy) next[id] = new PlantSize(id, copy.Set, copy.Row, factor);
            var marked = 0;
            foreach (var (id, size) in next)
                if (!_plantsSeen.TryGetValue(id, out var was) || was.Factor != size.Factor) { MarkPlantDirty(size, was?.Factor ?? 1f); marked++; }
            foreach (var (id, was) in _plantsSeen)
                if (!next.ContainsKey(id)) { MarkPlantDirty(was, 1f); marked++; }
            _plantsSeen = next;
            PlantChangesSeen++;
            PlantsMarkedDirty += marked;
            return true;
        }
        finally
        {
            LastPlantScanMs = Elapsed(started);
            WorstPlantScanMs = Math.Max(WorstPlantScanMs, LastPlantScanMs);
        }
    }

    /// <summary>Re-bake the tiles round a tree whose size changed: its trunk at the larger of the sizes it had and has (natural at the least).</summary>
    private void MarkPlantDirty(PlantSize size, float otherFactor)
    {
        var box = PlantObstacles.Bounds(size.Set, size.Row, Math.Max(1f, Math.Max(size.Factor, otherFactor)));
        _dirtyBoxes.Add(box.Grow(CellM));
    }
}

/// <summary>
/// A copy's obstacle at a size, from the room's data alone (pure, any thread): the shapes IslandNavigation gives a copy at its
/// natural size, and the same shapes grown. A tree's trunk is PlantCollision.Grown's base ring (the shape the copies' view
/// collides, so what is walkable is what the body can walk); a solid copy's hull is scaled about its root as the view places it.
/// </summary>
internal static class PlantObstacles
{
    /// <summary>The obstacle of one copy at a size (a factor of its natural size, above 0).</summary>
    public static IslandNavigation.Obstacle Of(PlantSet set, int row, string id, float factor)
    {
        if (set.Kind == PlantKind.Tree)
        {
            var root = set.Position(row);
            var shape = PlantCollision.Grown(set.Tree(row), root.X, root.Z, factor);
            var ring = PlantCollision.Trunk(shape).Take(PlantCollision.Segments).ToArray();
            return new IslandNavigation.Obstacle(id, PlantCollision.Footprint(ring, shape.BaseY), shape.BaseY, shape.TrunkTopM - shape.BaseY);
        }
        var origin = set.Position(row);
        var hull = PlantCollision.Solid(set, row);
        if (factor != 1f)
            for (var i = 0; i < hull.Length; i++) hull[i] = origin + (hull[i] - origin) * factor;
        var bottom = hull.Min(p => p.Y);
        // At its natural size exactly what IslandNavigation gives (the same arithmetic); grown, its top scaled about its root as the hull is.
        var top = factor == 1f ? set.TopM(row) : origin.Y + (set.TopM(row) - origin.Y) * factor;
        return new IslandNavigation.Obstacle(id, PlantCollision.Footprint(hull, bottom), bottom, top - bottom);
    }

    /// <summary>The box that holds a copy's obstacle at a size: its footprint from its bottom to its top (for the tiles to bake again when it changes).</summary>
    public static Aabb Bounds(PlantSet set, int row, float factor)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue, y0, y1;
        if (set.Kind == PlantKind.Tree)
        {
            var root = set.Position(row);
            var shape = PlantCollision.Grown(set.Tree(row), root.X, root.Z, factor);
            (x0, x1, z0, z1) = (shape.AxisX - shape.TrunkRadiusM, shape.AxisX + shape.TrunkRadiusM, shape.AxisZ - shape.TrunkRadiusM, shape.AxisZ + shape.TrunkRadiusM);
            (y0, y1) = (shape.BaseY, shape.TrunkTopM);
        }
        else
        {
            var origin = set.Position(row);
            var hull = PlantCollision.Solid(set, row);
            y0 = float.MaxValue;
            foreach (var point in hull)
            {
                var scaled = origin + (point - origin) * factor;
                (x0, x1, z0, z1, y0) = (Math.Min(x0, scaled.X), Math.Max(x1, scaled.X), Math.Min(z0, scaled.Z), Math.Max(z1, scaled.Z), Math.Min(y0, scaled.Y));
            }
            y1 = origin.Y + (set.TopM(row) - origin.Y) * factor;
        }
        return new Aabb(new Vector3(x0, y0, z0), new Vector3(x1 - x0, y1 - y0, z1 - z0));
    }

    /// <summary>A resized copy's obstacle with its footprint's rectangle, made once for a bake (a tile asks for the ones that reach into it).</summary>
    public readonly record struct Standing(IslandNavigation.Obstacle Obstacle, Rect2 Footprint);

    /// <summary>The obstacles of the copies at other than natural size that stand (not burnt down), made once for a bake.</summary>
    public static Standing[] Resized(IEnumerable<(string Id, PlantSet Set, int Row, float Factor)> sizes) =>
        sizes.Where(s => s.Factor > 0).Select(s =>
        {
            var obstacle = Of(s.Set, s.Row, s.Id, s.Factor);
            float x0 = obstacle.Outline.Min(p => p.X), x1 = obstacle.Outline.Max(p => p.X), z0 = obstacle.Outline.Min(p => p.Z), z1 = obstacle.Outline.Max(p => p.Z);
            return new Standing(obstacle, new Rect2(x0, z0, x1 - x0, z1 - z0));
        }).ToArray();

    /// <summary>
    /// What IslandNavigation.AddTo gives a tile, with the copies nature resized: the natural obstacles in the area except those
    /// copies, and the resized ones that reach into it (a grown trunk may reach in from beyond the area's edge). Returns how many
    /// obstructions were added.
    /// </summary>
    public static int AddTo(RoomData room, NavigationMeshSourceGeometryData3D source, Rect2 area, IReadOnlyDictionary<string, float> resized, Standing[] standing)
    {
        if (resized.Count == 0) return IslandNavigation.AddTo(room, source, area, ground: false);
        var added = 0;
        foreach (var obstacle in IslandNavigation.Obstacles(room, area))
        {
            if (resized.ContainsKey(obstacle.EntityId)) continue;
            source.AddProjectedObstruction(obstacle.Outline, obstacle.BottomM, obstacle.HeightM, false);
            added++;
        }
        foreach (var (obstacle, footprint) in standing)
        {
            if (!footprint.Intersects(area, includeBorders: true)) continue;
            source.AddProjectedObstruction(obstacle.Outline, obstacle.BottomM, obstacle.HeightM, false);
            added++;
        }
        return added;
    }
}
