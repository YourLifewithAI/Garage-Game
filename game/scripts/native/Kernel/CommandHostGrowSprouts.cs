using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Seedless growth (decision 104, at two Grow expertise): Grow on bare fertile soil, with no plant there to grow and no burnt ground,
/// makes up to GrowKindLaws.SproutsPerCast sprouts in its circle, on a lattice fixed to the island (so the same cast in the same place
/// makes the same sprouts, and a second cast fills only the spots left), each at least 0.15 m from every plant, on fertile ground
/// (grass or soil) as moist as Bloom's flowers needed. Each copies one of the nearest grass and flower sets (its nearest copy's size and
/// tint, the set chosen by a hash of the spot), and is a seedling at half size growing through its phases on the island clock. The
/// island keeps at most NatureState.MaxSprouts. They have no collision, burn as plants (a burnt one is gone, leaving ash), are saved in
/// the nature block and undone in Creative; SproutView draws them.
/// </summary>
public partial class CommandHost
{
    /// <summary>A lattice spot a sprout may take: its root on the ground and its hash (which set it copies, how it is turned).</summary>
    private readonly record struct SproutSpot(Vector3 At, uint Hash);

    public const string SproutsNothingToCopy = "No grass or flowers grow near enough for the sprouts to copy.";
    public const string SproutsGroundFull = "Sprouts already grow in every free spot there.";

    /// <summary>The sets a sprout may copy: low plants that set seed and have no collision (grass, flowers, ferns, herbs; never a tree or a stone).</summary>
    internal static bool SproutSet(PlantSet set) =>
        set.Kind == PlantKind.Plant && set.Collides == "none" && set.FuelClass is "grass" or "herb" && set.Seeds && set.Count > 0;

    /// <summary>A lattice point's hash: deterministic, the same on every machine.</summary>
    private static uint SpotHash(int i, int j)
    {
        unchecked
        {
            var h = (uint)i * 0x9E3779B1u ^ (uint)j * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            return h ^ (h >> 15);
        }
    }

    /// <summary>
    /// The nearest copy of each of the nearest SproutSetsMixed grass and flower sets round a point (within SproutSetSearchM), nearest
    /// first: what the sprouts copy.
    /// </summary>
    private List<PlantRef> SproutTemplates(Vector3 center)
    {
        var best = new Dictionary<PlantSet, (float Distance, int Row)>();
        if (Room?.Copies is not { } copies) return new List<PlantRef>();
        for (var radius = 1f; radius <= GrowKindLaws.SproutSetSearchM; radius *= 2)
        {
            foreach (var (set, row) in copies.RowsNear(center.X, center.Z, radius))
            {
                if (!SproutSet(set)) continue;
                var distance = CampLaws.Flat(set.Position(row), center);
                if (!best.TryGetValue(set, out var was) || distance < was.Distance || (distance == was.Distance && row < was.Row)) best[set] = (distance, row);
            }
            if (best.Count >= GrowKindLaws.SproutSetsMixed) break;
        }
        return best.OrderBy(p => p.Value.Distance).ThenBy(p => p.Key.Name, StringComparer.Ordinal).Take(GrowKindLaws.SproutSetsMixed)
            .Select(p => new PlantRef(p.Key, p.Value.Row, PlantCopies.IdOf(p.Key.Name, p.Value.Row))).ToList();
    }

    /// <summary>
    /// The free spots of the cast's circle a sprout may take, nearest its centre first, at most SproutsPerCast: lattice points (jittered
    /// by their hash) on fertile ground moist enough, not burning, within the root tolerance of the cast's height, and at least
    /// SproutApartM from every sprout and every plant copy still standing.
    /// </summary>
    private List<SproutSpot> SproutSpots(Cast cast)
    {
        var center = cast.Center;
        var radius = cast.Radius;
        var spacing = GrowKindLaws.SproutLatticeM;
        var rowM = spacing * Mathf.Sqrt(3) / 2;
        var points = new List<(float Distance, int J, int I, Vector2 At, uint Hash)>();
        for (var j = (int)Math.Floor((center.Z - radius) / rowM) - 1; j <= (int)Math.Ceiling((center.Z + radius) / rowM) + 1; j++)
        {
            var offset = (j & 1) != 0 ? spacing * 0.5f : 0;
            for (var i = (int)Math.Floor((center.X - radius - offset) / spacing) - 1; i <= (int)Math.Ceiling((center.X + radius - offset) / spacing) + 1; i++)
            {
                var hash = SpotHash(i, j);
                var jitter = GrowKindLaws.SproutJitterM;
                var at = new Vector2(i * spacing + offset + ((hash & 0xFF) / 255f * 2 - 1) * jitter, j * rowM + (((hash >> 8) & 0xFF) / 255f * 2 - 1) * jitter);
                var distance = at.DistanceTo(new Vector2(center.X, center.Z));
                if (distance <= radius) points.Add((distance, j, i, at, hash));
            }
        }
        var spots = new List<SproutSpot>();
        foreach (var point in points.OrderBy(p => p.Distance).ThenBy(p => p.J).ThenBy(p => p.I))
        {
            if (spots.Count >= GrowKindLaws.SproutsPerCast) break;
            var at = new Vector3(point.At.X, center.Y, point.At.Y);
            var (x, z) = NatureState.Grid(at);
            var cell = SampleNatureCell(x, z, center.Y);
            if (cell == null || !cell.Fertile || cell.Moisture < GrowKindLaws.SproutMinMoisture || Math.Abs(cell.Y - center.Y) > PlantLaws.RootHeightToleranceM) continue;
            if (Nature.Cells.TryGetValue(cell.Key, out var known) && (known.BurningS > 0 || known.Ash > 0)) continue;
            var root = new Vector3(at.X, SproutRootY(at, cell), at.Z);
            if (Nature.Sprouts.Any(s => CampLaws.Flat(s.PositionM, root) < GrowKindLaws.SproutApartM && Math.Abs(s.PositionM.Y - root.Y) <= PlantLaws.RootHeightToleranceM)) continue;
            if (PlantsRootedIn(root, GrowKindLaws.SproutApartM).Any(p => !(Nature.Plants.TryGetValue(p.Id, out var plant) && plant.Burnt))) continue;
            spots.Add(new SproutSpot(root, point.Hash));
        }
        return spots;
    }

    /// <summary>A sprout's root height: the ground straight under its spot (the cell's when the test sampler stands in for the room).</summary>
    private float SproutRootY(Vector3 spot, NatureCell cell)
    {
        if (NatureSampleForTests != null || !IsInsideTree()) return cell.Y;
        using var query = PhysicsRayQueryParameters3D.Create(new Vector3(spot.X, cell.Y + 0.05f, spot.Z), new Vector3(spot.X, cell.Y - 0.05f, spot.Z), RoomBuilder.WorldLayer, KernelNames.NoRids);
        query.HitBackFaces = false;
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? hit[KernelNames.HitPosition].AsVector3().Y : cell.Y;
    }

    /// <summary>
    /// Sprouts on the free fertile spots of the circle (decision 104), copying the nearest grass and flower sets: refused before the
    /// price when there is nothing to copy or the island keeps its most. grown_plants is how many sprouted.
    /// </summary>
    private JsonObject CastSprouts(JsonElement root, IslandAbility ability, Cast cast, List<SproutSpot> spots, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        var templates = SproutTemplates(cast.Center);
        if (templates.Count == 0) throw new Refusal("invalid_args", SproutsNothingToCopy, "$.args.center_m");
        var room = NatureState.MaxSprouts - Nature.Sprouts.Count;
        if (room <= 0) throw new Refusal("invalid_args", $"The island already keeps {NatureState.MaxSprouts} sprouts, the most it can.", "$.args.center_m");
        var kept = spots.Take(room).ToList();
        var sown = SownNowS();
        return CommitGrowKind(root, ability, cast, GrowKindWords.Sprouts, kept.Count, "The Gubble asks to grow sprouts on the bare ground here.", null, (candidate, step) =>
        {
            candidate.RememberSprouts(step);
            foreach (var spot in kept)
                candidate.Sprouts.Add(new NatureSprout
                {
                    Template = templates[(int)(spot.Hash % (uint)templates.Count)].Id, PositionM = spot.At, SownS = sown, Factor = SeedlingFactorNow(sown),
                });
        }, _ => Array.Empty<string>(), principal, action, fingerprint, preview, approvedBy);
    }

    // ---- the scene and fire ----

    /// <summary>The set and row a sprout copies (null when the room no longer has it: a sprout of another manifest is not drawn).</summary>
    private (PlantSet Set, int Row)? SproutCopy(NatureSprout sprout) =>
        Room?.Copies is { } copies && copies.TryResolve(sprout.Template, out var set, out var row) ? (set, row) : null;

    /// <summary>A sprout's yaw (degrees): a hash of its root, so a reload turns it the same.</summary>
    private static float SproutYawDeg(NatureSprout sprout)
    {
        var h = SpotHash(BitConverter.SingleToInt32Bits(sprout.PositionM.X), BitConverter.SingleToInt32Bits(sprout.PositionM.Z));
        return h % 3600 / 10f;
    }

    /// <summary>The room's sprouts follow nature's (SproutView under the room node, made with the first one). Returns whether what it shows changed.</summary>
    private bool ApplySprouts()
    {
        var room = GetParent()?.GetNodeOrNull<Node3D>(RoomNodePath);
        if (room == null || !IsInstanceValid(room)) return false;
        var view = room.GetNodeOrNull<SproutView>(SproutView.ViewName);
        if (view == null)
        {
            if (Nature.Sprouts.Count == 0) return false;
            view = SproutView.Create();
            room.AddChild(view);
        }
        var copies = room.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
        var shown = new List<SproutView.Shown>(Nature.Sprouts.Count);
        foreach (var sprout in Nature.Sprouts)
        {
            if (SproutCopy(sprout) is not { } copy) continue;
            var (set, row) = copy;
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(SproutYawDeg(sprout))) * Basis.FromScale(set.Scale(row) * sprout.Factor);
            shown.Add(new SproutView.Shown(set.Name, new Transform3D(basis, sprout.PositionM), set.Tint(row), sprout.Burn));
        }
        return view.SetSprouts(shown, name => copies == null || !IsInstanceValid(copies) ? null : copies.MeshFor(name));
    }

    /// <summary>A sprout's drawn bounds at its size now, for catching fire (its mesh's bounds under its root, its size and any turn).</summary>
    private Aabb SproutBounds(NatureSprout sprout)
    {
        if (SproutCopy(sprout) is not { } copy) return new Aabb(sprout.PositionM, Vector3.Zero);
        var (set, row) = copy;
        var mesh = set.MeshBounds;
        var scale = set.Scale(row) * sprout.Factor;
        var half = Math.Max(Math.Max(Math.Abs(mesh.Position.X), Math.Abs(mesh.End.X)) * scale.X, Math.Max(Math.Abs(mesh.Position.Z), Math.Abs(mesh.End.Z)) * scale.Z);
        return new Aabb(sprout.PositionM + new Vector3(-half, mesh.Position.Y * scale.Y, -half), new Vector3(half * 2, mesh.Size.Y * scale.Y, half * 2));
    }

    /// <summary>The fuel the sprouts give the ground cells they are rooted in, within a fire's reach (EnsurePlantFuel adds it).</summary>
    private void AddSproutFuel(Vector3 center, float radius)
    {
        foreach (var sprout in Nature.Sprouts)
        {
            if (CampLaws.Flat(sprout.PositionM, center) > radius + PlantLaws.FireReachMarginM || SproutCopy(sprout) is not { } copy) continue;
            var set = copy.Set;
            var (x, z) = NatureState.Grid(sprout.PositionM);
            var cell = NatureState.Key(x, z);
            _plantFuel[cell] = Math.Max(_plantFuel.GetValueOrDefault(cell), Math.Min(1, PlantLaws.Fuel(set.FuelClass)));
        }
    }

    /// <summary>Sprouts near burning ground burn as plants do, slowed by rain; one at 1 is gone, leaving ash, and the fire's undo step remembers them.</summary>
    private bool BurnSprouts(float rate, Dictionary<(int, int), NatureCell> burning, NatureUndoStep? step)
    {
        var changed = false;
        foreach (var sprout in Nature.Sprouts.ToList())
        {
            if (!Catches(SproutBounds(sprout).Grow(PlantLaws.CatchM), burning)) continue;
            Nature.RememberSprouts(step);
            sprout.Burn = Math.Min(1, sprout.Burn + rate);
            if (sprout.Burn >= 1)
            {
                Nature.Sprouts.Remove(sprout);
                LeaveAshAt(sprout.PositionM, step);
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>A plant that burned down leaves ash on the ground it was rooted in (recorded in the fire's undo step), where that ground is.</summary>
    private void LeaveAshAt(Vector3 at, NatureUndoStep? step)
    {
        var (x, z) = NatureState.Grid(at);
        var key = NatureState.Key(x, z);
        if (!Nature.Cells.ContainsKey(key) && Nature.Cells.Count >= NatureState.MaxCells) return;
        var cell = Nature.Cells.GetValueOrDefault(key) ?? SampleNatureCell(x, z, at.Y);
        if (cell == null || cell.Surface.Length == 0) return;
        NatureState.RememberCell(step, Nature.Cells, key);
        cell.Ash = Math.Max(cell.Ash, PlantLaws.AshLeft);
        Nature.Cells[key] = cell;
    }
}
