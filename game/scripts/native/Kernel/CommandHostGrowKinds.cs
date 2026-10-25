using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>What a Grow cast is, by its place (decisions 75 and 104 to 107): plain Grow, a waterpad, sprouts, a replanting, a bridge or ivy.</summary>
public enum GrowKind { Grow, Waterpad, Sprouts, Replant, Bridge, Ivy }

/// <summary>The receipts' and previews' words for Grow's unlocks (contracts: data.kind; plain Grow and the waterpad carry none, as before).</summary>
public static class GrowKindWords
{
    public const string Sprouts = "sprouts";
    public const string Replant = "replant";
    public const string Bridge = "bridge";
    public const string Ivy = "ivy";
    /// <summary>The unlocks in the order capabilities.list names the locked ones (GrowLockedKinds).</summary>
    public static readonly string[] Order = { Sprouts, Replant, Bridge, Ivy };
}

/// <summary>
/// Grow's unlocks in numbers (docs/game/MECHANICS.md decisions 76 and 104 to 107, docs/magic/GROW-UNLOCKS.md). The decisions name the
/// counts (2, 3, 5), the seedling's half size, the bridge's 1.2 m and its 0.1 m deck, the ivy's 0.4 m strip and its one-per-ledge rule;
/// the note names the 12 sprouts, 0.15 m apart, the 1,024 an island, the 0.15 m void and the 0.02 m lips, the 70 degree wall and the
/// 0.4 m root. Every other number here is provisional, for the founder to tune by playing. The seedling's phases and the ivy's drain
/// are pack values to be (the change request in the P-Grow report): until the parser reads them, these are their local defaults.
/// </summary>
public static class GrowKindLaws
{
    // ---- the gate (decision 76): the team's Grow expertise count ----
    public const int SeedlessAt = 2, BridgeAt = 3, IvyAt = 5;

    // ---- seedlings (decision 104): half size, natural through phases on the island clock ----
    /// <summary>A sprout or a replanted plant starts at this factor of its natural size...</summary>
    public const float SeedlingStart = 0.5f;
    /// <summary>...and reaches it through this many phases (provisional, a pack value to be)...</summary>
    public const int SeedlingPhases = 3;
    /// <summary>...each this many game hours of the island clock (one: provisional, a pack value to be). A reload continues them.</summary>
    public const double SeedlingPhaseH = 1;
    /// <summary>A saved sowing time is at most this (about the year 5138): anything later is no time.</summary>
    public const long MaxSownS = 100_000_000_000;

    /// <summary>A seedling's factor in a phase: SeedlingStart, then a third of the way to natural a phase, natural after the last.</summary>
    public static float SeedlingFactor(int phase) =>
        phase >= SeedlingPhases ? 1f : SeedlingStart + (1 - SeedlingStart) * Math.Max(0, phase) / SeedlingPhases;

    /// <summary>The phase a seedling sown at sownS is in at nowS on a clock of dayScale game days a real day (0 to SeedlingPhases).</summary>
    public static int SeedlingPhase(long sownS, long nowS, int dayScale)
    {
        var hours = Math.Max(0, nowS - sownS) / 3600.0 * Math.Max(1, dayScale);
        return (int)Math.Min(SeedlingPhases, Math.Floor(hours / SeedlingPhaseH + 1e-9));
    }

    // ---- sprouts (decision 104) ----
    /// <summary>At most this many sprouts a cast, in its circle, each at least SproutApartM from every plant (the note's 12 and 0.15 m).</summary>
    public const int SproutsPerCast = 12;
    public const float SproutApartM = 0.15f;
    /// <summary>The spots sprouts may take: a lattice fixed to the island, its points this far apart, each moved by at most SproutJitterM by its hash.</summary>
    public const float SproutLatticeM = 0.18f, SproutJitterM = 0.01f;
    /// <summary>The ground a sprout takes: fertile (grass or soil) and this moist, as Bloom's flowers needed (the note's 0.12).</summary>
    public const float SproutMinMoisture = NatureLaws.GrowthMinMoisture;
    /// <summary>The sprouts copy the nearest this many grass and flower sets (each sprout one of them, by a hash of its spot)...</summary>
    public const int SproutSetsMixed = 3;
    /// <summary>...found within this of the cast (none further: there is nothing to copy).</summary>
    public const float SproutSetSearchM = 32f;

    // ---- the bridge (decision 106) ----
    /// <summary>A deck from lip to lip at least BridgeMinM and never more than BridgeMaxM long (decision 106: 1.2 m, fixed), BridgeWidthM wide.</summary>
    public const float BridgeMinM = 0.4f, BridgeMaxM = 1.2f, BridgeWidthM = 0.1f;
    /// <summary>Its lips level within this (one step of the body's), the deck at the higher one.</summary>
    public const float BridgeLevelM = 0.02f;
    /// <summary>A lip is standable ground within this of the drag's end, above or below it.</summary>
    public const float BridgeLipM = 0.04f;
    /// <summary>A true void: no ground within this below the deck (over twice the 0.065 m hop), or swimmable water.</summary>
    public const float BridgeVoidDropM = 0.15f;
    /// <summary>The deck is sampled this often along its length; the void's edges are then found to within BridgeEdgeM.</summary>
    public const float BridgeSampleM = 0.025f, BridgeEdgeM = 0.004f;
    /// <summary>Nothing in the way: the space from a step over the deck to this high over it is clear along the whole deck.</summary>
    public const float BridgeClearM = 0.1f;
    /// <summary>Water at least this deep under the deck is swimmable (the body swims at 80 percent of its 10 cm height).</summary>
    public const float BridgeSwimDepthM = 0.08f;

    // ---- wall ivy (decision 107) ----
    /// <summary>A wall at least 70 degrees steep (an overhang to 110): a face whose normal's height is at most this.</summary>
    public static readonly float IvyMaxNormalY = Mathf.Cos(Mathf.DegToRad(70f));
    /// <summary>The aim is on the wall: its face within IvyAimM of the cast's point, with no standable ground within IvyGroundM under it.</summary>
    public const float IvyAimM = 0.03f, IvyGroundM = 0.02f;
    /// <summary>Ivy roots on standable ground within this below the aim (the note's 0.4 m).</summary>
    public const float IvyRootBelowM = 0.4f;
    /// <summary>A strip this long up the face and this wide (decision 107: one band, 0.4 m; 0.1 m wide), grown whole by one cast.</summary>
    public const float IvyLengthM = 0.4f, IvyWidthM = 0.1f;
    /// <summary>One ivy a ledge: no strip whose foot is within IvyApartM of another's...</summary>
    public const float IvyApartM = 0.3f;
    /// <summary>...or whose top reaches the same ledge: a ledge within IvySameLedgeRiseM of the other's height and IvySameLedgeM of it across (provisional).</summary>
    public const float IvySameLedgeM = 2f, IvySameLedgeRiseM = 0.05f;
    /// <summary>The ledge a strip reaches: standable ground at least IvyLedgeRiseM over its foot and at most IvyLedgeReachM over its top.</summary>
    public const float IvyLedgeRiseM = 0.15f, IvyLedgeReachM = 0.1f;
    /// <summary>A climb on a strip drains the bar at this share of a bare face's rate (decision 107: a quarter, provisional, a pack value to be).</summary>
    public const float IvyClimbFactor = 0.25f;
}

/// <summary>
/// Grow's three unlocks (decision 76, as the founder answered them on 25 October: decisions 104 to 107), on Grow's one command path,
/// nature.cast with capability grow, where the place decides as for Fire (decision 77) and the team's Grow expertise count opens each:
/// <list type="bullet">
/// <item><b>Seedless growth</b> at two (104, CommandHostGrowSprouts): Grow on bare fertile soil with no plant to grow makes up to 12
/// sprouts copying the nearest grass and flower sets; each a seedling at half size growing in phases on the island clock.</item>
/// <item><b>Replanting</b> at two too (105, CommandHostGrowReplant): Grow on burnt ground returns the nearest burnt plant of a set that
/// seeds as a seedling, its burn cleared; a tree regains its climb.</item>
/// <item><b>The bridge</b> at three (106, CommandHostGrowBridge): a drag (end_m) from lip to lip grows a level deck over a true void,
/// collided one-sided over the void alone.</item>
/// <item><b>Wall ivy</b> at five (107, CommandHostGrowIvy): Grow at a wall roots a strip at its foot; a climb on it costs a quarter.</item>
/// </list>
/// Before its count a kind is refused permission_denied before the price, in the kernel's words, whoever asks (the player's keys
/// order the Gubble's magic too); a place-decided kind falls back to plain Grow while that has anything to grow, as Snuff falls back
/// to Fire. Every kind is priced as any Grow (4 ordered, 2 as the Gubble's idea; decision 107), saved in the nature block beside the
/// plants and waterpads, one undo step in Creative, and burns as a plant. No kind moves an avatar: sprouts and ivy have no collision,
/// a replanted tree is refused where an avatar stands, and the bridge's collision lies over the void alone, one-sided.
/// </summary>
public partial class CommandHost
{
    /// <summary>The refusals before a kind's count (the companion's mock says the same; the HUD shows them as they are).</summary>
    public const string SproutsLocked = "Growing plants from bare soil comes once the Gubble has learned two of Grow's expertise.";
    public const string ReplantLocked = "Replanting a burnt plant comes once the Gubble has learned two of Grow's expertise.";
    public const string BridgeLocked = "A bridge of plants comes once the Gubble has learned three of Grow's expertise.";
    public const string IvyLocked = "Climbing ivy comes once the Gubble has learned five of Grow's expertise.";
    /// <summary>Grow on seedlings still growing through their phases, with nothing else there to grow.</summary>
    public const string GrowStillGrowing = "The seedlings there are still growing: give them time.";

    /// <summary>The team's Grow expertise learned (decision 76's count): every unlock reads it, whoever casts.</summary>
    public int GrowExpertiseCount => GlowProgress.Of(ExpertiseLists.Grow)?.Discoveries.Count ?? 0;
    public bool SeedlessUnlocked => GrowExpertiseCount >= GrowKindLaws.SeedlessAt;
    public bool BridgeUnlocked => GrowExpertiseCount >= GrowKindLaws.BridgeAt;
    public bool IvyUnlocked => GrowExpertiseCount >= GrowKindLaws.IvyAt;

    /// <summary>The unlocks the team has not opened yet, by their data.kind words, in order (capabilities.list's locked kinds of Grow).</summary>
    public IReadOnlyList<string> GrowLockedKinds()
    {
        var locked = new List<string>();
        if (!SeedlessUnlocked) locked.AddRange(new[] { GrowKindWords.Sprouts, GrowKindWords.Replant });
        if (!BridgeUnlocked) locked.Add(GrowKindWords.Bridge);
        if (!IvyUnlocked) locked.Add(GrowKindWords.Ivy);
        return locked;
    }

    /// <summary>A locked kind's words (the refusal's), or null once it is open: for the HUD's lock on key 2 and the Magic panel.</summary>
    public string? GrowLockedWords(string kind) => kind switch
    {
        GrowKindWords.Sprouts => SeedlessUnlocked ? null : SproutsLocked,
        GrowKindWords.Replant => SeedlessUnlocked ? null : ReplantLocked,
        GrowKindWords.Bridge => BridgeUnlocked ? null : BridgeLocked,
        GrowKindWords.Ivy => IvyUnlocked ? null : IvyLocked,
        _ => null,
    };

    /// <summary>Test seam: the team knows the first count of Grow's expertise, in their order (nothing saved or announced).</summary>
    internal void LearnGrowForTests(int count)
    {
        foreach (var id in ExpertiseLists.GrowOrder.Take(count))
            if (!GlowProgress.Has(ExpertiseLists.Grow, id)) GlowProgress = GlowProgress.WithExpertise(ExpertiseLists.Grow, id);
    }

    // ---- what the player's controls send ----

    /// <summary>The player's Grow at a point, or dragged from lip to lip (key 2 held and dragged, as 6 drags a Fireline): the place decides.</summary>
    public JsonObject PlayerGrowCast(Vector3 point, Vector3? end = null, float? radius = null) => PlayerCommand("nature.cast", GrowArgs(point, end, radius));

    /// <summary>What it would make there (data.kind, grown_plants, a bridge's lips as center_m and end_m), changing nothing: the HUD's outline.</summary>
    public JsonObject PlayerGrowPreview(Vector3 point, Vector3? end = null, float? radius = null) => PlayerCommand("nature.cast", GrowArgs(point, end, radius), preview: true);

    private JsonObject GrowArgs(Vector3 point, Vector3? end, float? radius)
    {
        var args = NatureCastArgs("grow", point, radius, null);
        if (end is { } to) args["end_m"] = KernelJson.Vector(to);
        return args;
    }

    // ---- the place decides (decision 77's pattern) ----

    /// <summary>A drag is a bridge, refused before its count (permission_denied) ahead of every other check of the cast, as a Fireline is.</summary>
    private void RequireGrowDrag(JsonElement args)
    {
        if (args.TryGetProperty("end_m", out _) && !BridgeUnlocked) throw new Refusal("permission_denied", BridgeLocked, "$.args.end_m");
    }

    /// <summary>
    /// One of Grow's unlocks committed (or previewed, or held for a keyed pack's yes) as one room revision, priced as any Grow: the
    /// candidate's change, the receipt in the kernel's words (data.kind, grown_plants: what grew), and what the cast showed counted
    /// toward Grow's expertise and hints (RecordGrowCast).
    /// </summary>
    private JsonObject CommitGrowKind(JsonElement root, IslandAbility ability, Cast cast, string kind, int grown, string holdWords, Action<JsonObject>? describe,
        Action<NatureState, NatureUndoStep> change, Func<NatureState, IEnumerable<string>> learned, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        var keyed = principal != PlayerPrincipal && ability.Tier == "keyed_yes";
        var data = new JsonObject
        {
            ["center_m"] = KernelJson.Vector(cast.Center), ["radius_m"] = KernelJson.Num(cast.Radius), ["duration_s"] = KernelJson.Num(cast.Duration),
            ["cell_count"] = 0, ["cells"] = new JsonArray(), ["cells_truncated"] = false, ["grown_plants"] = grown, ["requires_approval"] = keyed, ["kind"] = kind,
        };
        describe?.Invoke(data);
        if (preview) return NaturePreviewed("nature.cast", principal, action, data, Array.Empty<string>());
        if (keyed && approvedBy == null) return Hold(root, principal, action, fingerprint, "nature.cast", holdWords, new(), bindRevision: true);
        var candidate = Nature.Copy();
        var step = candidate.BeginUndo("Grow", Revision + 1, principal, Mode);
        change(candidate, step);
        data.Remove("cells");
        data.Remove("cells_truncated");
        data["started_cells"] = 0;
        // The guard at commit (CommandHostEnergy): priced as any Grow (decision 107), charged once the revision is saved.
        var price = GuardEnergy(ability.Capability, principal);
        var result = CommitNature(candidate, "nature.cast", principal, action, fingerprint, approvedBy, Array.Empty<string>(), data, undo: false);
        ChargeEnergy(price);
        RecordGrowCast(learned(Nature).ToList());
        return result;
    }

    // ---- the seedlings' clock (decision 104) ----

    private JsonObject? _dayScaleOf;
    private int _dayScale = 1;

    /// <summary>The island clock's game days a real day (MECHANICS.md section 9: its x_clock record; 1 for the machine's own clock).</summary>
    private int GrowDayScale()
    {
        if (ReferenceEquals(_dayScaleOf, ClockRecord)) return _dayScale;
        _dayScaleOf = ClockRecord;
        _dayScale = ClockRecord != null && global::GarageGame.Native.Look.WorldClock.FromNode(ClockRecord, out var problem) is { } clock && problem.Length == 0 && !clock.IsRealClock ? clock.DayScale : 1;
        return _dayScale;
    }

    /// <summary>Now on the island clock, in whole seconds of UTC (the host's clock, which tests move): when a seedling is sown.</summary>
    private long SownNowS() => (long)Math.Floor((Clock().ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds);

    /// <summary>The factor a seedling sown at sownS has now: its phase on the island clock, so a reload continues it.</summary>
    private float SeedlingFactorNow(long sownS) => GrowKindLaws.SeedlingFactor(GrowKindLaws.SeedlingPhase(sownS, SownNowS(), GrowDayScale()));

    /// <summary>
    /// The seedlings' sizes follow the island clock: each replanted plant's and each sprout's factor is its phase now (derived, never
    /// saved). Returns whether any changed, so the views are given them again (each nature step, a load, a cast).
    /// </summary>
    private bool RefreshSeedlings()
    {
        var changed = false;
        foreach (var plant in Nature.Plants.Values)
        {
            if (plant.SownS is not { } sown || plant.Burnt) continue;
            var factor = SeedlingFactorNow(sown);
            if (factor == plant.Factor) continue;
            plant.Factor = factor;
            changed = true;
        }
        foreach (var sprout in Nature.Sprouts)
        {
            var factor = SeedlingFactorNow(sprout.SownS);
            if (factor == sprout.Factor) continue;
            sprout.Factor = factor;
            changed = true;
        }
        return changed;
    }

    /// <summary>The nature step's look at the seedlings: a phase that advanced grows them in the scene (no save: the clock decides them).</summary>
    private void StepSeedlings()
    {
        if (RefreshSeedlings()) ApplyPlants();
    }
}
