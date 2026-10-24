using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Grow's numbers and the burning plants' (docs/game/MECHANICS.md decisions 52 and 67, docs/magic/README.md "Grow's first version"),
/// every one provisional: a first guess for playtests. What a pack grants (reach, radius, the share a cast grows) is the island's
/// rules'; the caps here are the engine's.
/// </summary>
public static class PlantLaws
{
    /// <summary>A plant's full size (its cap) is at most this many times its natural size...</summary>
    public const float CapFactor = 2f;
    /// <summary>...or this much taller than it started (one band), whichever comes first: a grown tree never skips a whole ladder.</summary>
    public const float CapExtraM = 0.4f;
    /// <summary>An older pack's Bloom (growth.seed) is read as Grow (decision 52) and grows this share a cast: its intensity meant flowers.</summary>
    public const float LegacyGrowShare = 0.25f;
    /// <summary>A root this far above or below the cast's centre is not in its circle (a ledge, a shelf, a roof).</summary>
    public const float RootHeightToleranceM = 0.5f;
    /// <summary>Plants rooted this far outside a fire's circle may still catch from its edge (a crown over the burning ground).</summary>
    public const float FireReachMarginM = 0.5f;
    /// <summary>The fuel a plant gives the ground cell it is rooted in, by its set's fuel class (a cell's fuel is at most 1).</summary>
    public const float GrassFuel = 0.5f, HerbFuel = 0.6f, ShrubFuel = 0.8f, TreeFuel = 1f;
    /// <summary>Grown plants are more fuel: their fuel and their burn time grow by this much per unit of growth (twice the size, half as much again).</summary>
    public const float GrownFuelFactor = 0.5f;
    /// <summary>A plant near burning ground burns as any other object does (NatureLaws.ObjectBurnPerIntensityS), slowed by rain and by its fuel.</summary>
    public const float BurnPerIntensityS = NatureLaws.ObjectBurnPerIntensityS;
    /// <summary>Burning ground this close to a plant's bounds burns it, as for any other object.</summary>
    public const float CatchM = NatureLaws.ObjectCatchM;
    /// <summary>The ash a plant that burned down leaves on the ground it was rooted in.</summary>
    public const float AshLeft = 0.6f;

    // Grow's five expertise (decision 75), every number provisional.
    /// <summary>Rain garden: while it rains (the weather's rain, NatureLaws.WeatherRain01 and up) a cast grows each plant this many steps (still capped).</summary>
    public const float RainGardenFactor = 2f;
    /// <summary>Thicket: three plants at full size, each within this of the other two (on the ground plane).</summary>
    public const float ThicketSpanM = 1f;
    /// <summary>Thicket looks at most this many full-size plants round the cast (the nearest), so the check stays small.</summary>
    public const int ThicketLooksAt = 48;
    /// <summary>Green ash: the fuel the greened ground has at once (the most moist ash regrows to over minutes, NatureLaws.FlowerFuel).</summary>
    public const float GreenAshFuel = NatureLaws.FlowerFuel;
    /// <summary>Waterpad: a Grow cast whose point is on water (its surface at most this far above the point, or WaterpadBelowM under it)...</summary>
    public const float WaterpadAboveM = 0.5f, WaterpadBelowM = 0.05f;
    /// <summary>...at least this deep grows a waterpad there; two leaves' centres stay a leaf's diameter apart (WaterpadView.DiameterM).</summary>
    public const float WaterpadMinDepthM = 0.03f;

    public static float Fuel(string fuelClass) => fuelClass switch
    {
        "grass" => GrassFuel, "herb" => HerbFuel, "shrub" => ShrubFuel, "broadleaf" or "conifer" => TreeFuel, _ => 0,
    };

    public static float FuelMultiplier(float factor) => 1 + GrownFuelFactor * Math.Max(0, factor - 1);

    /// <summary>Grass tufts, flowers, ferns, shrubs and trees grow; stones never do.</summary>
    public static bool Grows(PlantSet set) => set.Kind != PlantKind.Stone;

    /// <summary>Every plant with fuel burns; stones and a copy whose base is under water do not.</summary>
    public static bool Burns(PlantSet set, int row) => set.Kind != PlantKind.Stone && Fuel(set.FuelClass) > 0 && !set.Submerged(row);

    /// <summary>
    /// A copy's full size: twice its natural size or CapExtraM taller, whichever comes first, measured to the highest of its top, its
    /// pole and its perches, so a grown tree's top perch rises at most CapExtraM.
    /// </summary>
    public static float Cap(PlantSet set, int row)
    {
        var y = set.Position(row).Y;
        var height = set.TopM(row) - y;
        if (set.IsTree)
        {
            var tree = set.Tree(row);
            height = Math.Max(height, tree.PoleTopM - y);
            foreach (var cap in tree.Caps) height = Math.Max(height, cap.TopM - y);
        }
        return height <= 1e-4f ? CapFactor : Math.Min(CapFactor, 1 + CapExtraM / height);
    }
}

/// <summary>
/// Grow (decision 52, as the founder agreed it on 16 October with decision 67's fire): a cast at a point within reach and sight grows
/// every plant rooted in its circle by the pack's share of its natural size, up to its cap; grown plants stay grown, saved with the
/// island's nature (NatureState.Plants), drawn and collided at their size by the room's copies (PlantCopiesView.SetPlantFactors, only
/// the tiles holding a changed copy rebuilt), and Creative's undo takes a cast back. Fire burns plants as it burns any other object
/// (FireRule's "plant" row): one catches near burning ground, gives its ground cell fuel (more when grown) and burns down, gone with
/// its drawing and its collision, leaving ash; it stays gone, saved with the island, and goes in the fire's one undo step.
/// </summary>
public partial class CommandHost
{
    /// <summary>A plant copy of the room: its set, its row and its entity id.</summary>
    private readonly record struct PlantRef(PlantSet Set, int Row, string Id);

    /// <summary>The plants a fire can reach and the fuel they give their ground cells, for one circle and one state of the plants.</summary>
    private (Vector3 Center, float Radius, int Version)? _plantFuelKey;
    /// <summary>The copies rooted in a fire's reach, read from the room's tiles once per circle (the plants' state filters them).</summary>
    private (Vector3 Center, float Radius)? _fireReachKey;
    private List<PlantRef> _fireReach = new();
    private List<PlantRef> _firePlants = new();
    private readonly Dictionary<string, float> _plantFuel = new(StringComparer.Ordinal);
    /// <summary>Moves whenever the plants the scene shows change (a cast, a burn, an undo, a load).</summary>
    private int _plantsVersion;
    /// <summary>What the copies' view shows now, by id (absent: natural), and which view that is.</summary>
    private readonly Dictionary<string, float> _plantsShown = new(StringComparer.Ordinal);
    private ulong _plantsView;
    private static readonly NodePath CopiesPath = "Room/" + PlantCopiesView.ViewName;
    /// <summary>A ray result's shape index (which of a body's shapes it hit): a plant copy's collision names its row by it.</summary>
    private static readonly Variant HitShape = "shape";

    /// <summary>Test seam: the ground the room's physics gives a cell (nature's own sampler, with the plants' fuel), nearY as the simulation passes it.</summary>
    internal NatureCell? SampleCellForTests(int x, int z, float? nearY = null) => SampleNatureCell(x, z, nearY);

    /// <summary>The main thread's time the last change of the plants took the copies' view (refilling and colliding its tiles), and the tiles.</summary>
    public double PlantsRefreshMs { get; private set; }
    public int PlantsRefreshTiles { get; private set; }

    private float FactorOf(string id) => Nature.Plants.TryGetValue(id, out var plant) ? plant.Factor : 1;

    /// <summary>Every plant copy whose root lies within radius of center horizontally and within heightTolerance of its height.</summary>
    private List<PlantRef> PlantsRootedIn(Vector3 center, float radius, float heightTolerance = PlantLaws.RootHeightToleranceM)
    {
        var found = new List<PlantRef>();
        if (Room?.Copies is not { } copies) return found;
        foreach (var (set, row) in copies.RowsNear(center.X, center.Z, radius))
            if (Math.Abs(set.Position(row).Y - center.Y) <= heightTolerance) found.Add(new PlantRef(set, row, PlantCopies.IdOf(set.Name, row)));
        return found;
    }

    /// <summary>
    /// A Grow cast: every plant rooted in the circle that is not at its cap grows the pack's share of its natural size (a quarter in
    /// storybook_wild v5), up to its cap; while it rains, twice that (Rain garden); and the ash in the circle greens at once (Green
    /// ash: burnt plants stay gone). Cast on water, it grows a waterpad instead (CastWaterpad). A cast where nothing can grow and no
    /// ash can green (no plant, all full size, the island's grown plants at their most) is refused before the price, with the reason.
    /// It takes no ground cell, so its cell counts are 0; grown_plants says how many grew. One room revision, which room.undo takes
    /// back in Creative. What it showed teaches Grow's expertise, once per team (RecordGrowCast).
    /// </summary>
    private JsonObject CastGrow(JsonElement root, IslandAbility ability, Cast cast, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        if (WaterForPad(cast.Center) is { } surface) return CastWaterpad(root, ability, cast, surface, principal, action, fingerprint, preview, approvedBy);
        var share = ability.Primitive == "growth.grow" ? cast.Intensity : PlantLaws.LegacyGrowShare;
        var raining = Nature.Weather.Rain01 > NatureLaws.WeatherRain01;
        if (raining) share *= PlantLaws.RainGardenFactor;
        var rooted = PlantsRootedIn(cast.Center, cast.Radius).Where(p => PlantLaws.Grows(p.Set) && !(Nature.Plants.TryGetValue(p.Id, out var plant) && plant.Burnt)).ToList();
        var ash = AshIn(cast.Center, cast.Radius);
        if (rooted.Count == 0 && ash.Count == 0)
            throw new Refusal("invalid_args", "There is no plant to grow there: Grow makes the island's own plants bigger (grass, flowers, ferns, shrubs and trees).", "$.args.center_m");
        var growing = rooted.Where(p => FactorOf(p.Id) < PlantLaws.Cap(p.Set, p.Row) - 1e-4f).ToList();
        if (growing.Count == 0 && ash.Count == 0) throw new Refusal("invalid_args", "Every plant there is already full size.", "$.args.center_m");
        // The island keeps at most MaxGrownPlants grown: a plant already grown grows on, a natural one only while there is room.
        var room = NatureState.MaxGrownPlants - Nature.GrownPlants;
        var kept = new List<PlantRef>();
        foreach (var plant in growing)
        {
            if (Nature.Plants.TryGetValue(plant.Id, out var record) && record.Grown) kept.Add(plant);
            else if (room > 0) { kept.Add(plant); room--; }
        }
        if (kept.Count == 0 && ash.Count == 0) throw new Refusal("invalid_args", $"The island already keeps {NatureState.MaxGrownPlants} grown plants, the most it can: none more can grow.", "$.args.center_m");
        var keyed = principal != PlayerPrincipal && ability.Tier == "keyed_yes";
        var data = new JsonObject
        {
            ["center_m"] = KernelJson.Vector(cast.Center), ["radius_m"] = KernelJson.Num(cast.Radius), ["duration_s"] = KernelJson.Num(cast.Duration),
            ["cell_count"] = 0, ["cells"] = new JsonArray(), ["cells_truncated"] = false, ["grown_plants"] = kept.Count, ["requires_approval"] = keyed,
        };
        if (preview) return NaturePreviewed("nature.cast", principal, action, data, Array.Empty<string>());
        if (keyed && approvedBy == null) return Hold(root, principal, action, fingerprint, "nature.cast", "The Gubble asks to grow the plants in this area.", new(), bindRevision: true);
        var candidate = Nature.Copy();
        var step = candidate.BeginUndo("Grow", Revision + 1, principal, Mode);
        var tall = false;
        foreach (var plant in kept)
        {
            candidate.RememberPlant(step, plant.Id);
            var record = candidate.Plant(plant.Id);
            var cap = PlantLaws.Cap(plant.Set, plant.Row);
            record.Factor = Math.Min(cap, record.Factor + share);
            tall |= record.Factor >= cap - 1e-4f;
        }
        // Green ash (decision 75): the ash in the circle greens at once; the burnt plants stay gone (replanting is a later unlock).
        foreach (var key in ash)
        {
            NatureState.RememberCell(step, candidate.Cells, key);
            var cell = candidate.Cells[key];
            cell.Ash = 0;
            cell.Fuel = Math.Max(cell.Fuel, PlantLaws.GreenAshFuel);
        }
        data.Remove("cells");
        data.Remove("cells_truncated");
        data["started_cells"] = 0;
        // The guard at commit (CommandHostEnergy): the price by principal, charged once the revision is saved.
        var price = GuardEnergy(ability.Capability, principal);
        var result = CommitNature(candidate, "nature.cast", principal, action, fingerprint, approvedBy, Array.Empty<string>(), data, undo: false);
        ChargeEnergy(price);
        // What this cast showed, from nature as it is now (decision 75): each the first time teaches it, once per team.
        var learned = new List<string>();
        if (tall) learned.Add(ExpertiseLists.TallPlant);
        if (kept.Count > 0 && ThicketAround(cast.Center, cast.Radius, kept)) learned.Add(ExpertiseLists.Thicket);
        if (ash.Count > 0) learned.Add(ExpertiseLists.GreenAsh);
        if (raining && kept.Count > 0) learned.Add(ExpertiseLists.RainGarden);
        RecordGrowCast(learned);
        return result;
    }

    /// <summary>The ground cells in a cast's circle (and RootHeightToleranceM of its height) that hold ash: burnt ground for Green ash.</summary>
    private List<string> AshIn(Vector3 center, float radius)
    {
        var found = new List<string>();
        foreach (var (x, z) in NatureState.Disk(center, radius))
        {
            var key = NatureState.Key(x, z);
            if (Nature.Cells.TryGetValue(key, out var cell) && cell.Ash > 0 && cell.BurningS <= 0 && Math.Abs(cell.Y - center.Y) <= PlantLaws.RootHeightToleranceM) found.Add(key);
        }
        return found;
    }

    /// <summary>
    /// Thicket (decision 75): after a cast, three plants at full size each within PlantLaws.ThicketSpanM of the other two, one of them
    /// grown by this cast. The full-size plants rooted round the circle (the nearest ThicketLooksAt) are read from nature as it is now.
    /// </summary>
    private bool ThicketAround(Vector3 center, float radius, List<PlantRef> grown)
    {
        var full = PlantsRootedIn(center, radius + PlantLaws.ThicketSpanM)
            .Where(p => !(Nature.Plants.TryGetValue(p.Id, out var plant) && plant.Burnt) && FactorOf(p.Id) >= PlantLaws.Cap(p.Set, p.Row) - 1e-4f)
            .OrderBy(p => CampLaws.Flat(p.Set.Position(p.Row), center)).Take(PlantLaws.ThicketLooksAt).ToList();
        var mine = grown.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        bool Near(PlantRef a, PlantRef b) => CampLaws.Flat(a.Set.Position(a.Row), b.Set.Position(b.Row)) <= PlantLaws.ThicketSpanM;
        for (var i = 0; i < full.Count; i++)
            for (var j = i + 1; j < full.Count; j++)
            {
                if (!Near(full[i], full[j])) continue;
                for (var k = j + 1; k < full.Count; k++)
                    if (Near(full[i], full[k]) && Near(full[j], full[k]) && (mine.Contains(full[i].Id) || mine.Contains(full[j].Id) || mine.Contains(full[k].Id))) return true;
            }
        return false;
    }

    /// <summary>
    /// The team's Grow casts as its store counts them (decision 75, the shared rules): a committed cast the host verified, the expertise
    /// it showed learned once each, a hint at the 10th to 50th cast. A reward, never a precondition: a store that cannot be saved leaves
    /// the cast standing (TrySaveExpertise), and each newly learned one is announced by Fire's camp's hook (PublishProgress).
    /// </summary>
    private void RecordGrowCast(IEnumerable<string> learned)
    {
        GlowProgress candidate;
        try { candidate = GlowProgress.AfterAbilityCast(ExpertiseLists.Grow, learned); }
        catch (GlowProgressException) { return; }
        TrySaveExpertise(candidate);
    }

    // ---- the waterpad (decision 75): a Grow cast on water ----

    /// <summary>The water under a Grow cast's point, if it is on water (its surface near and deep enough): the surface's height, or null.</summary>
    private float? WaterForPad(Vector3 point)
    {
        var water = WaterColumn(point, PlantLaws.WaterpadAboveM, PlantLaws.WaterpadBelowM);
        return water.Wet && water.DepthM >= PlantLaws.WaterpadMinDepthM && water.SurfaceY >= point.Y - PlantLaws.WaterpadBelowM ? water.SurfaceY : null;
    }

    /// <summary>
    /// A Grow cast on water (the sea, a lagoon or a pond, inside the island, within reach and sight: CheckCast) grows one large water lily
    /// leaf at the point, floating at the surface (WaterpadView: solid from above, WaterpadView.DiameterM across). The island keeps at most
    /// NatureState.MaxWaterpads, two never closer than a leaf's diameter. One room revision, which Creative's undo takes back; the price
    /// as for any Grow; the first one teaches Waterpad. grown_plants is 1: the leaf is the plant that grew.
    /// </summary>
    private JsonObject CastWaterpad(JsonElement root, IslandAbility ability, Cast cast, float surface, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        var at = new Vector3(cast.Center.X, surface + WaterpadView.FloatM, cast.Center.Z);
        if (Nature.Waterpads.Count >= NatureState.MaxWaterpads)
            throw new Refusal("invalid_args", $"The island already keeps {NatureState.MaxWaterpads} water lily leaves, the most it can.", "$.args.center_m");
        if (Nature.Waterpads.Any(p => CampLaws.Flat(p.PositionM, at) < WaterpadView.DiameterM))
            throw new Refusal("invalid_args", "A water lily leaf already floats here: grow the next one a little further on.", "$.args.center_m");
        var keyed = principal != PlayerPrincipal && ability.Tier == "keyed_yes";
        var data = new JsonObject
        {
            ["center_m"] = KernelJson.Vector(cast.Center), ["radius_m"] = KernelJson.Num(cast.Radius), ["duration_s"] = KernelJson.Num(cast.Duration),
            ["cell_count"] = 0, ["cells"] = new JsonArray(), ["cells_truncated"] = false, ["grown_plants"] = 1, ["requires_approval"] = keyed,
        };
        if (preview) return NaturePreviewed("nature.cast", principal, action, data, Array.Empty<string>());
        if (keyed && approvedBy == null) return Hold(root, principal, action, fingerprint, "nature.cast", "The Gubble asks to grow a water lily leaf here.", new(), bindRevision: true);
        var candidate = Nature.Copy();
        var step = candidate.BeginUndo("Grow", Revision + 1, principal, Mode);
        candidate.RememberWaterpads(step);
        candidate.Waterpads.Add(new NatureWaterpad { Id = candidate.NextWaterpadId(), PositionM = at });
        data.Remove("cells");
        data.Remove("cells_truncated");
        data["started_cells"] = 0;
        var price = GuardEnergy(ability.Capability, principal);
        var result = CommitNature(candidate, "nature.cast", principal, action, fingerprint, approvedBy, Array.Empty<string>(), data, undo: false);
        ChargeEnergy(price);
        RecordGrowCast(new[] { ExpertiseLists.Waterpad });
        return result;
    }

    /// <summary>The room's waterpads follow nature's (the view under the room node, made when the first one grows); rebuilt only when they change.</summary>
    private void ApplyWaterpads()
    {
        var room = GetParent()?.GetNodeOrNull<Node3D>(RoomNodePath);
        if (room == null || !IsInstanceValid(room)) return;
        var view = room.GetNodeOrNull<WaterpadView>(WaterpadView.ViewName);
        if (view == null)
        {
            if (Nature.Waterpads.Count == 0) return;
            view = WaterpadView.Create();
            room.AddChild(view);
        }
        view.SetWaterpads(Nature.Waterpads.Select(p => (p.Id, p.PositionM, p.Burn)).ToList());
    }

    private static readonly NodePath RoomNodePath = "Room";

    /// <summary>A waterpad's bounds for catching fire: its leaf, from a little under the water to a burning cell's height over its top.</summary>
    private static Aabb WaterpadBounds(NatureWaterpad pad)
    {
        var radius = WaterpadView.DiameterM * 0.5f;
        return new Aabb(pad.PositionM - new Vector3(radius, 0.02f, radius), new Vector3(radius * 2, 0.08f, radius * 2));
    }

    /// <summary>
    /// Waterpads near burning ground burn as plants do (decision 75: "it can burn like any plant"), slowed by rain; one at 1 is gone, its
    /// collision with it, and the fire's undo step remembers them. A fire cast on a leaf finds leaf under it (its collision names it).
    /// </summary>
    private bool BurnWaterpads(float rate, Dictionary<(int, int), NatureCell> burning, NatureUndoStep? step)
    {
        var changed = false;
        foreach (var pad in Nature.Waterpads.ToList())
        {
            if (!Catches(WaterpadBounds(pad).Grow(PlantLaws.CatchM), burning)) continue;
            Nature.RememberWaterpads(step);
            pad.Burn = Math.Min(1, pad.Burn + rate);
            if (pad.Burn >= 1) Nature.Waterpads.Remove(pad);
            changed = true;
        }
        return changed;
    }

    // ---- the copies' view follows the plants ----

    /// <summary>
    /// The room's copies show the plants as nature has them: each changed plant's factor (0 for one burnt down) goes to the view,
    /// which fills and collides again only the tiles holding a changed copy. A new view (a rebuilt room) is given every plant.
    /// </summary>
    private void ApplyPlants()
    {
        // The waterpads with them: Grow's leaves on the water (decision 75), drawn and collided by their own view.
        ApplyWaterpads();
        var view = GetParent()?.GetNodeOrNull<PlantCopiesView>(CopiesPath);
        if (view == null || !IsInstanceValid(view))
        {
            if (_plantsShown.Count > 0 || Nature.Plants.Count > 0) _plantsVersion++;
            _plantsShown.Clear();
            _plantsView = 0;
            return;
        }
        if (view.GetInstanceId() != _plantsView)
        {
            _plantsShown.Clear();
            _plantsView = view.GetInstanceId();
        }
        var changes = new List<(string, float)>();
        foreach (var (id, plant) in Nature.Plants)
        {
            var shown = plant.Shown;
            if (shown == 1 ? _plantsShown.ContainsKey(id) : !(_plantsShown.TryGetValue(id, out var was) && was == shown)) changes.Add((id, shown));
        }
        foreach (var id in _plantsShown.Keys)
            if (!Nature.Plants.ContainsKey(id)) changes.Add((id, 1));
        if (changes.Count == 0) return;
        foreach (var (id, factor) in changes)
        {
            if (factor == 1) _plantsShown.Remove(id);
            else _plantsShown[id] = factor;
        }
        _plantsVersion++;
        var clock = Stopwatch.StartNew();
        PlantsRefreshTiles = view.SetPlantFactors(changes);
        PlantsRefreshMs = clock.Elapsed.TotalMilliseconds;
        // What a cast or a load costs the copies (the fire's steady burning is left out of the log): for playtests and the founder.
        if (Nature.Fire == null)
            GD.Print(System.FormattableString.Invariant($"PLANTS {changes.Count} changed, {PlantsRefreshTiles} tile(s) filled and collided again in {PlantsRefreshMs:0.00} ms"));
    }

    // ---- plants as fire's objects ----

    /// <summary>
    /// The plants a fire in this circle can reach (rooted within it and FireReachMarginM, burnable, not burnt down) and the fuel each
    /// gives the ground cell it is rooted in, kept until the circle or the plants change. SampleNatureCell reads the fuel.
    /// </summary>
    private void EnsurePlantFuel(Vector3 center, float radius)
    {
        if (_plantFuelKey is { } key && key.Center == center && key.Radius == radius && key.Version == _plantsVersion) return;
        _plantFuelKey = (center, radius, _plantsVersion);
        // The tiles are read once per circle; a plant burning down only filters the list again (a few hundred copies, not the tiles).
        if (_fireReachKey is not { } reach || reach.Center != center || reach.Radius != radius)
        {
            _fireReachKey = (center, radius);
            _fireReach = PlantsRootedIn(center, radius + PlantLaws.FireReachMarginM, radius + PlantLaws.FireReachMarginM).Where(p => PlantLaws.Burns(p.Set, p.Row)).ToList();
        }
        _firePlants = _fireReach.Where(p => !(Nature.Plants.TryGetValue(p.Id, out var plant) && plant.Burnt)).ToList();
        _plantFuel.Clear();
        foreach (var plant in _firePlants)
        {
            var (x, z) = NatureState.Grid(plant.Set.Position(plant.Row));
            var cell = NatureState.Key(x, z);
            var fuel = Math.Min(1, PlantLaws.Fuel(plant.Set.FuelClass) * PlantLaws.FuelMultiplier(FactorOf(plant.Id)));
            _plantFuel[cell] = Math.Max(_plantFuel.GetValueOrDefault(cell), fuel);
        }
    }

    /// <summary>No fire, no plant fuel: the next fire or cast reads its own circle's.</summary>
    private void ForgetPlantFuel()
    {
        _plantFuelKey = null;
        _fireReachKey = null;
        _fireReach = new List<PlantRef>();
        _firePlants = new List<PlantRef>();
        _plantFuel.Clear();
    }

    /// <summary>A ground cell with the fuel of the plants rooted in it (the plants of the circle EnsurePlantFuel read last).</summary>
    private NatureCell? WithPlantFuel(NatureCell? cell, int x, int z)
    {
        if (cell != null && _plantFuel.Count > 0 && _plantFuel.TryGetValue(NatureState.Key(x, z), out var fuel) && fuel > cell.Fuel) cell.Fuel = fuel;
        return cell;
    }

    /// <summary>A copy's drawn bounds at a factor of its natural size: its mesh's bounds under its row's transform, scaled about its root.</summary>
    private static Aabb PlantBounds(PlantRef plant, float factor)
    {
        var place = plant.Set.Transform(plant.Row);
        return new Transform3D(place.Basis * Basis.FromScale(Vector3.One * factor), place.Origin) * plant.Set.MeshBounds;
    }

    /// <summary>
    /// Plants near burning ground catch, as the fire rule says (its "plant" row: they burn down, nothing asks): each burns as an object
    /// does, slowed by rain and by its fuel (a grown plant burns longer), and one at 1 has burned down: gone, its ground left with ash.
    /// Every change is recorded in the fire's undo step. The island keeps at most MaxBurntPlants burnt or burning: past that, no other
    /// plant catches. Returns whether any plant changed.
    /// </summary>
    private bool BurnPlants(float seconds)
    {
        var fire = Nature.Fire;
        if (fire == null) return false;
        var ruling = FireRule.TryGetValue(("plant", Mode), out var row) ? row : FireRule[("plant", "creative")];
        if (ruling.Outcome == FireOutcome.Fireproof) return false;
        var copies = Room?.Copies != null;
        if (copies) EnsurePlantFuel(fire.Center, fire.RadiusM);
        var rate = seconds * PlantLaws.BurnPerIntensityS * fire.Intensity * (1 - Nature.Weather.Rain01);
        if (((!copies || _firePlants.Count == 0) && Nature.Waterpads.Count == 0) || rate <= 0) return false;
        var burning = new Dictionary<(int, int), NatureCell>();
        foreach (var cell in Nature.Cells.Values)
            if (cell.BurningS > 0) burning[(cell.X, cell.Z)] = cell;
        if (burning.Count == 0) return false;
        var step = Nature.Undo.LastOrDefault(s => s.Revision == fire.Revision);
        var room = NatureState.MaxBurntPlants - Nature.Plants.Values.Count(p => p.Burn > 0);
        var changed = false;
        foreach (var plant in copies ? _firePlants : new List<PlantRef>())
        {
            Nature.Plants.TryGetValue(plant.Id, out var record);
            if (record is { Burnt: true }) continue;
            var factor = record?.Factor ?? 1;
            if (!Catches(PlantBounds(plant, factor).Grow(PlantLaws.CatchM), burning)) continue;
            if (record is not { Burn: > 0 } && room-- <= 0) continue;
            Nature.RememberPlant(step, plant.Id);
            var burned = Nature.Plant(plant.Id);
            burned.Burn = Math.Min(1, burned.Burn + rate / PlantLaws.FuelMultiplier(factor));
            changed = true;
            if (burned.Burnt) LeaveAsh(plant, step);
        }
        changed |= BurnWaterpads(rate, burning, step);
        if (changed) ApplyPlants();
        return changed;
    }

    /// <summary>Whether a burning cell lies inside bounds (its ground a little raised, as objects catch).</summary>
    private static bool Catches(Aabb bounds, Dictionary<(int, int), NatureCell> burning)
    {
        var (x0, z0) = NatureState.Grid(bounds.Position);
        var (x1, z1) = NatureState.Grid(bounds.End);
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
            if (burning.TryGetValue((x, z), out var cell) && bounds.HasPoint(cell.Position + Vector3.Up * 0.04f)) return true;
        return false;
    }

    /// <summary>A plant that burned down leaves ash on the ground it was rooted in (recorded in the fire's undo step), where that ground is.</summary>
    private void LeaveAsh(PlantRef plant, NatureUndoStep? step)
    {
        var at = plant.Set.Position(plant.Row);
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
