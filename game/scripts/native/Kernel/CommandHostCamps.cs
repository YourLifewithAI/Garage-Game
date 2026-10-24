using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>A campfire's spot (bottom centre, on the ground): the island's starting campfire, or a firepit the kernel chose.</summary>
public sealed record CampSite(string Id, Vector3 PositionM, bool Starting);

/// <summary>What sitting down by a fire answered: whether the player sits, the words, the campfire, and what it taught or changed.</summary>
public sealed record RestOutcome(bool Ok, string Words, string? Campfire = null, bool LearnedFire = false, bool NewCamp = false);

/// <summary>
/// Fire's camp in numbers (docs/game/MECHANICS.md decisions 49, 61, 62, 70 and 71). Every number here is provisional: the design
/// names none of them.
/// </summary>
public static class CampLaws
{
    public const string StartingId = "camp:start";
    public const string FirepitPrefix = "firepit:";
    /// <summary>The starting campfire burns this far in front of the player's spawn, so the first load wakes the player facing it.</summary>
    public const float StartingFireAheadM = 0.2f;
    /// <summary>The sit action works within this of a lit campfire (three body heights), and at most RestRiseM above or below it.</summary>
    public const float RestRadiusM = 0.3f;
    public const float RestRiseM = 0.15f;
    /// <summary>
    /// A lit campfire's flames (decision 85: it hurts a player standing in it, as any fire does, at the pack's fire quarters a second): a
    /// body whose feet stand within this of its centre (the look's 4.5 cm ring of stones, CampfireLook.RingRadiusM, and half a stone), at
    /// most FlamesRiseM above its ground (over the flames, a hop included) and RestRiseM below. Everywhere else within RestRadiusM is the
    /// resting spot beside it, which is safe: the rest key never sits a body down in the flames (PlayerRest). Provisional.
    /// </summary>
    public const float FlamesM = 0.05f;
    public const float FlamesRiseM = 0.08f;
    /// <summary>
    /// The island's land for the firepits (decision 74): standable dry ground probed on a grid this fine, each point standing for
    /// this step squared of land; the candidates are those points, never within FirepitFractalClearM of a Fractal.
    /// </summary>
    public const float FirepitGridStepM = 1.5f;
    public const float FirepitFractalClearM = 1f;
    /// <summary>The count follows the land's area: one firepit per AreaPerFirepitM2, at least MinFirepits and at most MaxFirepitsByArea (within MaxFirepits).</summary>
    public const float AreaPerFirepitM2 = 250f;
    public const int MinFirepits = 3;
    public const int MaxFirepitsByArea = 12;
    /// <summary>Lloyd's relaxation of the farthest-point seed over the land: this many rounds of moving each firepit to its share's centre.</summary>
    public const int SpreadIterations = 24;
    /// <summary>A saved lit (or put out) firepit keeps its id while its spot moved less than this; past it, it is unlit and the island loads.</summary>
    public const float SameSpotM = 0.05f;
    /// <summary>The rule before decision 74 (farthest-point sampling from the bench Fractals, then a 3 m grid), kept only to read a save written then.</summary>
    public const int LegacyFirepitCount = 6;
    public const float LegacyFirepitSpacingM = 4f;
    public const float LegacyGridStepM = 3f;
    /// <summary>Decision 73: rain puts a lit campfire out once this much rain has fallen on it (the weather's rain, NatureLaws.WeatherRain01 and up), counted afresh each rain.</summary>
    public const float RainOutS = 30f;
    /// <summary>A Fire cast lights a firepit when its circle reaches within this of the firepit (and within FirepitCatchRiseM of its height).</summary>
    public const float FirepitCatchM = 0.12f;
    public const float FirepitCatchRiseM = 0.3f;
    /// <summary>
    /// Decision 62's camp, for a pack that names no camp pace of its own: hearts refill this many times faster within
    /// CommandHost.CampRadiusM of the starting camp or any lit campfire (VitalsRules.CampHealQuarterEveryS's default). v5 names its
    /// own (traits.hearts.camp_heal_quarter_every_s, a quarter a second: decision 91).
    /// </summary>
    public const float CampHeartsFactor = 2f;
    /// <summary>The start spot is saved again once the player's last safe footing moved this far.</summary>
    public const float SpotMovedM = 0.25f;
    /// <summary>The sit ends once the body moved this far from where it sat down.</summary>
    public const float SitHoldM = 0.03f;
    public const int MaxFirepits = 16;

    /// <summary>How many firepits land of this area gets (decision 74): one per AreaPerFirepitM2, within MinFirepits and MaxFirepitsByArea.</summary>
    public static int FirepitCountFor(float landAreaM2) =>
        landAreaM2 <= 0 ? 0 : Math.Clamp((int)Math.Round(landAreaM2 / AreaPerFirepitM2), MinFirepits, Math.Min(MaxFirepitsByArea, MaxFirepits));

    /// <summary>
    /// The firepits spread evenly over the land (decision 74): a centroidal split of the land samples. The starting campfire is a fixed
    /// site; the firepits are seeded by farthest-point sampling among the candidates, then moved SpreadIterations times to the centre of
    /// the land nearer to them than to any other site (Lloyd's relaxation), and each lands on the candidate of its own share nearest that
    /// centre (none twice). No random number anywhere: the same land always gives the same firepits in the same order, so their ids
    /// (firepit:1, firepit:2, ...) are stable for an island on every load and machine. The count follows the land's area
    /// (FirepitCountFor, each sample standing for cellAreaM2), at most the candidates there are.
    /// </summary>
    public static List<Vector3> SpreadFirepits(Vector3 start, IReadOnlyList<Vector3> land, IReadOnlyList<Vector3> candidates, float cellAreaM2)
    {
        var count = Math.Min(FirepitCountFor(land.Count * cellAreaM2), candidates.Count);
        if (count <= 0) return new List<Vector3>();
        var sites = LegacyChooseFirepits(start, candidates, count, 0).Select(p => new Vector2(p.X, p.Z)).ToList();
        var origin = new Vector2(start.X, start.Z);
        var share = new int[land.Count];
        for (var round = 0; round <= SpreadIterations; round++)
        {
            var sums = new Vector2[sites.Count];
            var counts = new int[sites.Count];
            for (var i = 0; i < land.Count; i++)
            {
                var at = new Vector2(land[i].X, land[i].Z);
                var best = -1;
                var bestDistance = at.DistanceSquaredTo(origin);
                for (var s = 0; s < sites.Count; s++)
                {
                    var distance = at.DistanceSquaredTo(sites[s]);
                    if (distance < bestDistance) { bestDistance = distance; best = s; }
                }
                share[i] = best;
                if (best < 0) continue;
                sums[best] += at;
                counts[best]++;
            }
            if (round == SpreadIterations) break;
            for (var s = 0; s < sites.Count; s++)
                if (counts[s] > 0) sites[s] = sums[s] / counts[s];
        }
        // Each firepit on the candidate nearest its centre among its own share of the land (any candidate when its share has none).
        var isCandidate = new HashSet<Vector3>(candidates);
        var taken = new HashSet<int>();
        var chosen = new List<Vector3>();
        for (var s = 0; s < sites.Count; s++)
        {
            var best = -1;
            var bestDistance = float.MaxValue;
            for (var pass = 0; pass < 2 && best < 0; pass++)
                for (var i = 0; i < land.Count; i++)
                {
                    if ((pass == 0 && share[i] != s) || taken.Contains(i) || !isCandidate.Contains(land[i])) continue;
                    var distance = new Vector2(land[i].X, land[i].Z).DistanceSquaredTo(sites[s]);
                    if (distance < bestDistance) { bestDistance = distance; best = i; }
                }
            if (best < 0) continue;
            taken.Add(best);
            chosen.Add(land[best]);
        }
        return chosen;
    }

    /// <summary>
    /// Farthest-point sampling from the starting campfire: each pick is the candidate farthest (on the ground plane) from the starting
    /// campfire and every pick so far, the first listed on a tie; it stops at <paramref name="count"/> or when the farthest left is
    /// nearer than <paramref name="spacing"/>. SpreadFirepits' seed; before decision 74 it was the whole rule (read now only for a save
    /// written then, LegacyFirepitSpots), which pushed the firepits to the island's edges.
    /// </summary>
    public static List<Vector3> LegacyChooseFirepits(Vector3 start, IReadOnlyList<Vector3> candidates, int count, float spacing)
    {
        var chosen = new List<Vector3>();
        var left = candidates.ToList();
        while (chosen.Count < Math.Min(count, MaxFirepits) && left.Count > 0)
        {
            var best = -1f;
            var at = -1;
            for (var i = 0; i < left.Count; i++)
            {
                var nearest = Flat(left[i], start);
                foreach (var pick in chosen) nearest = Math.Min(nearest, Flat(left[i], pick));
                if (nearest > best) { best = nearest; at = i; }
            }
            if (best < spacing) break;
            chosen.Add(left[at]);
            left.RemoveAt(at);
        }
        return chosen;
    }

    public static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}

/// <summary>
/// Fire's camp (the Fire's camp packet, 16 October; MECHANICS.md decisions 49, 61, 62, 63, 70 and 71):
/// <list type="bullet">
/// <item><b>The starting campfire</b> (61): every island has one, CampLaws.StartingFireAheadM in front of the player's spawn, lit from
/// the start for now (the opening scene will put it out and have the Gubble relight it). The camp is where it burns.</item>
/// <item><b>Firepits</b> (62, 74): spread evenly over any island by the kernel, without a generator change: the island's standable dry
/// land probed on a FirepitGridStepM grid, the firepits at the candidates nearest the centres of a centroidal split of that land
/// (CampLaws.SpreadFirepits), as many as its area asks. Unlit at first; a Fire cast on one lights it (LightFirepit, from CastFire) and
/// it is a campfire from then on, saved with the island (with its spot: a saved firepit whose spot moved is unlit, and the island
/// loads); the first one lit teaches Campfire.</item>
/// <item><b>Only rain or Snuff puts a campfire out</b> (73, and 82 for every fire): rain on a lit campfire for CampLaws.RainOutS puts it
/// out (StepCampfireRain); the X stop never does; Snuff calls PutOutCampfire. A campfire that is out is no camp until the Gubble's Fire
/// relights it, on the firepit path, the starting campfire included. Until the team has learned Fire nothing could relight the starting
/// campfire, so until then rain spares it (the integrator's provisional choice). Out and lit are saved with the island.</item>
/// <item><b>No Fire until the first rest</b> (72): a Fire cast, whoever asks and in every mode, is refused until the team's store holds
/// first_fire (RequireFireLearned).</item>
/// <item><b>A lit campfire is a full camp</b> (71): PlayerAtCamp is true within CampRadiusM of the starting camp (while its campfire
/// burns) or of any lit campfire, so Fractals are spent and knacks chosen there; and the campfire the player last sat at is the camp they
/// wake at (a knockout's recovery lands there, with half the hearts and the pool at least half full).</item>
/// <item><b>Resting heals</b> (84, 91): under a pack that heals hearts only at camp (v5, heal_outside_camp false) hearts heal only while
/// the player rests, sitting by a lit campfire (PlayerResting), at the pack's camp pace (a quarter a second); standing at the camp heals
/// nothing. A pack that heals outside a camp (every pack before decision 84) heals as before: its pace anywhere, its camp pace at a camp
/// (HealQuarterEveryHereS). Nothing heals while the game is closed under v5 (93, CaughtUp); food waits for D1.</item>
/// <item><b>A campfire's flames hurt</b> (85): a player standing in a lit campfire (CampLaws.FlamesM) is hurt as by any fire, at the pack's
/// fire quarters a second, in Challenge and Live (CampfireFlamesAt, from the nature step); the resting spot beside it is safe.</item>
/// <item><b>Resting is sitting down by a fire</b> (70): PlayerRest, the HUD's rest key, within RestRadiusM of a lit campfire, on the
/// ground, out of its flames. The first rest at the starting campfire teaches Fire (61), once per team. The host verifies it from the body
/// itself. PlayerCanRest answers the same without sitting down, for the HUD's prompt.</item>
/// <item><b>The start spot</b> (49): the island save's team block keeps where the player and the Gubble last stood, one spot per mode
/// (the block "camp"); a spot that is not safe ground (water, mid-air, inside something) falls back to the starting campfire, and the
/// first load of an island starts there. The island's clock record (x_clock) is kept in the same block's neighbour, "clock".</item>
/// </list>
/// </summary>
public partial class CommandHost
{
    public const string RestNearFire = "Sit down by a lit campfire to rest: come closer to the fire.";
    public const string RestOnGround = "Stand on the ground by the fire to sit down.";
    public const string RestUnlit = "This firepit is not lit yet: ask the Gubble to cast Fire on it.";
    public const string RestOut = "This campfire is out: ask the Gubble to relight it.";
    /// <summary>Decision 85: the player stands in the campfire's flames, which hurt; the resting spot is beside it.</summary>
    public const string RestInFlames = "You are standing in the fire: step out of the flames to sit down beside it.";
    /// <summary>Decision 72's refusal (permission_denied, as the fifth light's lock on Constellation's controls): the companion's mock says the same.</summary>
    public const string FireNotLearned = "The Gubble has not learned Fire yet: rest by the starting campfire first.";

    private static readonly StringName CampHitPosition = "position";
    private static readonly StringName CampHitNormal = "normal";
    private static readonly StringName CampHitCollider = "collider";
    private static readonly System.Text.RegularExpressions.Regex CampIdPattern = new(@"\A(camp:start|firepit:[1-9][0-9]?)\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    private List<CampSite>? _camps;
    /// <summary>The firepits burning now (the starting campfire burns unless it is out).</summary>
    private readonly HashSet<string> _litCampfires = new(StringComparer.Ordinal);
    /// <summary>The campfires once lit and now out (rain, later Snuff), the starting campfire among them: each waits for the Gubble's Fire.</summary>
    private readonly HashSet<string> _outCampfires = new(StringComparer.Ordinal);
    /// <summary>The rain fallen on each lit campfire during this rain (seconds); cleared when the rain stops.</summary>
    private readonly Dictionary<string, float> _campRainS = new(StringComparer.Ordinal);
    /// <summary>A saved camp block's firepits not yet matched to this island's firepits (their spots need the room's physics).</summary>
    private (string[] Lit, string[] Out, Dictionary<string, Vector3>? Spots)? _savedPits;
    /// <summary>The land the firepits were spread over (its area, m², and its samples), for the log.</summary>
    private float _campLandM2;
    private List<Vector3> _campLand = new();
    private readonly Dictionary<string, JsonObject> _startSpots = new(StringComparer.Ordinal);
    private string? _homeCamp;
    private Vector3? _homeSpot;
    private bool _spotsLive;
    private Vector3 _spotNoted;
    private bool _resting;
    private Vector3 _restAt;
    private Vector3? _campSpawn;
    private IReadOnlyList<Vector3>? _firepitsForTests;

    /// <summary>The look and the HUD hear when a campfire is lit or the saved camp state is loaded.</summary>
    public event Action? CampsChanged;

    /// <summary>
    /// Test seam: the firepits' spots in place of the spread over the land (the test room is no island and has none). Setting it matches
    /// the island save's firepits, as loaded, to these spots again (a body's tick may have matched them to the room's own already).
    /// </summary>
    internal IReadOnlyList<Vector3>? FirepitsForTests
    {
        get => _firepitsForTests;
        set
        {
            _firepitsForTests = value;
            _camps = null;
            if (_loadedPits is not { } loaded) return;
            _litCampfires.Clear();
            _outCampfires.RemoveWhere(id => id != CampLaws.StartingId);
            _savedPits = loaded.Pits;
            (_homeCamp, _homeSpot) = (loaded.Home, loaded.HomeSpot);
        }
    }

    /// <summary>The island save's firepits and home camp as last loaded (FirepitsForTests matches them again).</summary>
    private ((string[] Lit, string[] Out, Dictionary<string, Vector3>? Spots)? Pits, string? Home, Vector3? HomeSpot)? _loadedPits;

    /// <summary>Every campfire spot: the starting campfire first, then the firepits (computed once the room has physics).</summary>
    public IReadOnlyList<CampSite> CampSites => EnsureCamps();

    public CampSite? StartingCampfire => EnsureCamps().Count > 0 ? EnsureCamps()[0] : null;

    /// <summary>The starting campfire is lit unless it is out; a firepit once a Fire cast lit it, until it is out.</summary>
    public bool IsCampfireLit(string id)
    {
        if (_savedPits != null) EnsureCamps();
        return id == CampLaws.StartingId ? !_outCampfires.Contains(id) : _litCampfires.Contains(id);
    }

    /// <summary>A campfire that burned once and is out now (rain or Snuff): the Gubble's Fire relights it.</summary>
    public bool IsCampfireOut(string id)
    {
        if (_savedPits != null) EnsureCamps();
        return _outCampfires.Contains(id);
    }

    /// <summary>Whether the team has learned Fire (decision 72: the first rest at the starting campfire teaches it).</summary>
    public bool FireLearned => GlowProgress.Has(ExpertiseLists.Fire, ExpertiseLists.FirstFire);

    /// <summary>Decision 72: no Fire until the first rest, whoever asks (the player's keys order the Gubble's magic too), in every mode.</summary>
    private void RequireFireLearned()
    {
        if (!FireLearned) throw new Refusal("permission_denied", FireNotLearned, "$.args.capability");
    }

    /// <summary>Test seam, the one helper the suites that light fires use: the team knows Fire, as after the first rest (nothing saved or announced).</summary>
    internal void LearnFireForTests()
    {
        if (!FireLearned) GlowProgress = GlowProgress.WithExpertise(ExpertiseLists.Fire, ExpertiseLists.FirstFire);
    }

    /// <summary>The campfire the player last sat at, the camp they wake at (null: the island's spawn).</summary>
    public string? HomeCamp => _homeCamp;

    /// <summary>Whether the player sits by a fire now: from their rest until the body moves (or PlayerStand).</summary>
    public bool PlayerResting => _resting && Player != null && IsInstanceValid(Player) && Player.GlobalPosition.DistanceTo(_restAt) <= CampLaws.SitHoldM;

    private List<CampSite> EnsureCamps()
    {
        if (_camps != null) return _camps;
        if (Room == null) return new List<CampSite>();
        var start = StartingCampfireSpot();
        var camps = new List<CampSite> { new(CampLaws.StartingId, start, true) };
        // Before the room has physics only the starting campfire is known (not kept: the firepits are probed once it can be).
        if (!IsInsideTree()) return camps;
        IReadOnlyList<Vector3> pits;
        if (_firepitsForTests != null) pits = _firepitsForTests;
        else
        {
            var (land, candidates) = FirepitLand(CampLaws.FirepitGridStepM, out var fractals);
            _campLandM2 = land.Count * CampLaws.FirepitGridStepM * CampLaws.FirepitGridStepM;
            _campLand = land;
            pits = CampLaws.SpreadFirepits(start, land, candidates.Where(c => fractals.All(f => CampLaws.Flat(c, f) >= CampLaws.FirepitFractalClearM)).ToList(),
                CampLaws.FirepitGridStepM * CampLaws.FirepitGridStepM);
        }
        for (var i = 0; i < pits.Count && i < CampLaws.MaxFirepits; i++) camps.Add(new CampSite(CampLaws.FirepitPrefix + (i + 1), pits[i], false));
        _camps = camps;
        ResolveSavedPits(camps, start);
        return camps;
    }

    /// <summary>
    /// A saved camp block's firepits matched to this island's (once the room has physics): each lit or put-out firepit keeps its id while
    /// its saved spot (or, for a save written before decision 74, the spot the old rule gave it) is within CampLaws.SameSpotM of this
    /// island's firepit of that id; one whose spot moved is unlit (forgotten), with the home camp if it was that one, and the island loads.
    /// </summary>
    private void ResolveSavedPits(List<CampSite> camps, Vector3 start)
    {
        if (_savedPits is not { } saved) return;
        _savedPits = null;
        var legacy = saved.Spots == null && saved.Lit.Concat(saved.Out).Any(id => id != CampLaws.StartingId) ? LegacyFirepitSpots(start) : null;
        bool Kept(string id)
        {
            if (id == CampLaws.StartingId) return true;
            var site = camps.FirstOrDefault(c => c.Id == id);
            var number = int.Parse(id[CampLaws.FirepitPrefix.Length..], System.Globalization.CultureInfo.InvariantCulture);
            Vector3? spot = saved.Spots != null ? saved.Spots.GetValueOrDefault(id) : legacy != null && number <= legacy.Count ? legacy[number - 1] : null;
            var kept = site != null && spot is { } at && site.PositionM.DistanceTo(at) <= CampLaws.SameSpotM;
            if (!kept) GD.Print($"CAMP saved {id} no longer stands where it was saved: it is unlit");
            return kept;
        }
        foreach (var id in saved.Lit.Where(Kept)) _litCampfires.Add(id);
        foreach (var id in saved.Out.Where(Kept)) _outCampfires.Add(id);
        if (_homeCamp != null && _homeCamp != CampLaws.StartingId && !_litCampfires.Contains(_homeCamp) && !_outCampfires.Contains(_homeCamp))
        {
            _homeCamp = null;
            _homeSpot = null;
        }
        _teamDirty = true;
        CampsChanged?.Invoke();
    }

    /// <summary>The firepits as the rule before decision 74 chose them (bench Fractals, then a 3 m grid, farthest-point sampling): only to read an older save.</summary>
    private List<Vector3> LegacyFirepitSpots(Vector3 start)
    {
        var found = AllFractals.Where(f => f.ReachBand != null && !WaterColumn(f.PositionM, 2f, 0.05f).Wet).Select(f => f.PositionM).ToList();
        if (found.Count < CampLaws.LegacyFirepitCount && (Room.Terrain != null || Room.Sea != null)) found.AddRange(FirepitLand(CampLaws.LegacyGridStepM, out _).Land);
        return CampLaws.LegacyChooseFirepits(start, found, CampLaws.LegacyFirepitCount, CampLaws.LegacyFirepitSpacingM);
    }

    /// <summary>In front of the player's spawn by CampLaws.StartingFireAheadM, on the ground there when the room has it.</summary>
    private Vector3 StartingCampfireSpot()
    {
        var spawn = Room.SpawnFor("player");
        var at = spawn.PositionM + new Basis(Vector3.Up, Mathf.DegToRad(spawn.YawDeg)) * Vector3.Forward * CampLaws.StartingFireAheadM;
        if (GroundBelow(at + Vector3.Up * 0.2f, 0.5f) is { } ground) at.Y = ground;
        return at;
    }

    /// <summary>
    /// The island's land (decision 74): standable dry ground on a step grid over the island's bounds, in grid order; the candidates are
    /// the same points (the caller keeps them clear of the Fractals, whose spots come back in fractals). An indoor room (no terrain, no
    /// sea) has no land for firepits.
    /// </summary>
    private (List<Vector3> Land, List<Vector3> Candidates) FirepitLand(float step, out List<Vector3> fractals)
    {
        var land = new List<Vector3>();
        fractals = AllFractals.Select(f => f.PositionM).ToList();
        if (Room.Terrain == null && Room.Sea == null) return (land, land);
        var bounds = Room.Bounds;
        for (var x = bounds.Position.X + step * 0.5f; x < bounds.End.X; x += step)
            for (var z = bounds.Position.Z + step * 0.5f; z < bounds.End.Z; z += step)
                if (StandableGround(new Vector3(x, bounds.End.Y + 1, z), bounds.Size.Y + 2) is { } spot) land.Add(spot);
        return (land, land);
    }

    /// <summary>The first thing under a point if it is walkable ground (not a room object or a creation, not a steep face) with no water over it.</summary>
    private Vector3? StandableGround(Vector3 top, float depth)
    {
        using var query = PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * depth, RoomBuilder.BodyMask, BubbleExclusions());
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || hit[CampHitNormal].AsVector3().Y < Mathf.Cos(Mathf.DegToRad(35))) return null;
        var at = hit[CampHitPosition].AsVector3();
        var (_, id) = NatureSurface(hit[CampHitCollider].AsGodotObject() as Node);
        if (id.StartsWith("obj:", StringComparison.Ordinal) || id.StartsWith("creation:", StringComparison.Ordinal)) return null;
        return WaterColumn(at, 2f, 0.05f).Wet ? null : at;
    }

    /// <summary>
    /// Whether a spot is at the camp: within CampRadiusM of the island's spawn while the starting campfire burns, or of a lit campfire
    /// (a campfire that is out is no camp, decision 73). Allocates nothing once the camps are known.
    /// </summary>
    internal bool AtCamp(Vector3 at)
    {
        if (Room == null) return false;
        _campSpawn ??= Room.SpawnFor("player").PositionM;
        if (IsCampfireLit(CampLaws.StartingId) && at.DistanceTo(_campSpawn.Value) <= CampRadiusM) return true;
        var camps = EnsureCamps();
        for (var i = 0; i < camps.Count; i++)
            if (IsCampfireLit(camps[i].Id) && at.DistanceTo(camps[i].PositionM) <= CampRadiusM) return true;
        return false;
    }

    /// <summary>
    /// Seconds per quarter of a heart healed where the player is now, hearts in quarters (the pack's traits.hearts; decisions 84 and 91),
    /// or null where nothing heals. Under a pack that heals only at camp (heal_outside_camp false, v5) the player heals only while resting,
    /// sat by a lit campfire (PlayerResting, decision 70's rest): at the camp's pace (camp_heal_quarter_every_s, a quarter a second in v5);
    /// standing at the camp, or anywhere else, heals nothing. A pack that heals outside a camp heals as before: the camp's pace at a camp
    /// (by default heal_quarter_every_s / CampHeartsFactor, decision 62's), heal_quarter_every_s elsewhere. Food (D1) adds its own heal later.
    /// </summary>
    internal double? HealQuarterEveryHereS()
    {
        if (_vitalsRules.HealsOutsideCamp) return PlayerAtCamp() ? _vitalsRules.CampHealQuarterEveryS : _vitalsRules.HealQuarterEveryS;
        return PlayerResting && PlayerAtCamp() ? _vitalsRules.CampHealQuarterEveryS : null;
    }

    /// <summary>
    /// The lit campfire whose flames these feet stand in (decision 85; CampLaws.FlamesM, FlamesRiseM), or null. Allocates nothing once the
    /// camps are known (the nature step asks it every step).
    /// </summary>
    internal CampSite? CampfireFlamesAt(Vector3 feet)
    {
        if (Room == null || !feet.IsFinite()) return null;
        var camps = EnsureCamps();
        for (var i = 0; i < camps.Count; i++)
        {
            var camp = camps[i];
            var rise = feet.Y - camp.PositionM.Y;
            if (CampLaws.Flat(feet, camp.PositionM) <= CampLaws.FlamesM && rise <= CampLaws.FlamesRiseM && rise >= -CampLaws.RestRiseM && IsCampfireLit(camp.Id)) return camp;
        }
        return null;
    }

    private CampSite? NearestCampfire(Vector3 at, float within, bool litOnly)
    {
        CampSite? best = null;
        var bestDistance = float.MaxValue;
        foreach (var camp in EnsureCamps())
        {
            if (litOnly && !IsCampfireLit(camp.Id)) continue;
            var flat = CampLaws.Flat(at, camp.PositionM);
            if (flat > within || Math.Abs(at.Y - camp.PositionM.Y) > CampLaws.RestRiseM || flat >= bestDistance) continue;
            best = camp;
            bestDistance = flat;
        }
        return best;
    }

    // ---- resting: sitting down by a fire (decision 70) ----

    /// <summary>
    /// The player's rest key: sit down by a lit campfire. The host checks the body itself (RestCheck: on the ground, not swimming, within
    /// RestRadiusM of a lit campfire, out of its flames). The first rest at the starting campfire teaches Fire, once per team
    /// (RecordExpertise); the campfire becomes the camp the player wakes at; and while the player sits there hearts heal at the camp's
    /// pace (HealQuarterEveryHereS, decision 84). Not a command in the contract: a trusted HUD call, never on the companion's surface.
    /// </summary>
    public RestOutcome PlayerRest()
    {
        var check = RestCheck(out var campfire);
        if (!check.Ok || campfire == null) return check;
        var at = Player!.GlobalPosition;
        _resting = true;
        _restAt = at;
        var learned = campfire.Starting && RecordExpertise(ExpertiseLists.Fire, ExpertiseLists.FirstFire);
        var newCamp = _homeCamp != campfire.Id;
        _homeCamp = campfire.Id;
        _homeSpot = at;
        Player.SetSpawnPoint(at);
        if (newCamp) NoteCampChanged();
        var words = campfire.Starting ? "You sit down by the campfire and rest." : "You sit down by the campfire and rest. This is your camp now: you will wake here.";
        return new(true, words, campfire.Id, learned, newCamp);
    }

    /// <summary>
    /// Whether the rest key would sit the player down here now, and the words it would say, without sitting them down or changing anything
    /// (the HUD's prompt at the fire, decision 86): the same checks as PlayerRest. Ok carries the campfire and no words (the prompt's words
    /// are the HUD's); a refusal carries the words PlayerRest would answer, and the campfire when one is near.
    /// </summary>
    public RestOutcome PlayerCanRest() => RestCheck(out _);

    /// <summary>
    /// The rest key's checks, from the body itself: on the ground, not swimming, within RestRadiusM of a lit campfire, and out of its flames
    /// (decision 85: the resting spot beside it is safe, so no one sits down in the fire). The campfire, or the refusal's words.
    /// </summary>
    private RestOutcome RestCheck(out CampSite? campfire)
    {
        campfire = null;
        if (Player == null || !IsInstanceValid(Player) || Room == null) return new(false, "There is no one here to sit down.");
        if (!Player.IsOnFloor() || Player.IsSwimming) return new(false, RestOnGround);
        var at = Player.GlobalPosition;
        if (NearestCampfire(at, CampLaws.RestRadiusM, litOnly: true) is not { } lit)
            return new(false, NearestCampfire(at, CampLaws.RestRadiusM, litOnly: false) is { } dark ? IsCampfireOut(dark.Id) ? RestOut : RestUnlit : RestNearFire);
        if (CampfireFlamesAt(at) != null) return new(false, RestInFlames, lit.Id);
        campfire = lit;
        return new(true, "", lit.Id);
    }

    /// <summary>The player stands up (any key that moves them does it too, by moving the body).</summary>
    public void PlayerStand() => _resting = false;

    // ---- lighting a firepit (decision 62): the Gubble's Fire cast on one ----

    /// <summary>
    /// The unlit firepit or campfire that is out a Fire cast's circle reaches (the starting campfire too once it is out, decision 73),
    /// the nearest to its centre; null when none (the cast is an ordinary fire).
    /// </summary>
    private CampSite? UnlitFirepitUnder(Vector3 center, float radius)
    {
        CampSite? best = null;
        var bestDistance = float.MaxValue;
        foreach (var camp in EnsureCamps())
        {
            if (IsCampfireLit(camp.Id)) continue;
            var flat = CampLaws.Flat(center, camp.PositionM);
            if (flat > radius + CampLaws.FirepitCatchM || Math.Abs(center.Y - camp.PositionM.Y) > CampLaws.FirepitCatchRiseM || flat >= bestDistance) continue;
            best = camp;
            bestDistance = flat;
        }
        return best;
    }

    /// <summary>
    /// A Fire cast on an unlit firepit lights the firepit and nothing else (the ring holds it: no ground catches, nothing burns), on
    /// the same path as every Fire: a preview first (one cell, the firepit, so the HUD's confirm works as for any fire), then the
    /// commit as exactly that preview, a keyed_yes pack's hold for the Gubble's, the price by principal charged at commit, a durable
    /// nature receipt. The firepit is a campfire from then on, saved with the island; the first one lit teaches Campfire.
    /// </summary>
    private JsonObject LightFirepit(JsonElement root, IslandAbility ability, Vector3 center, float radius, float duration, CampSite firepit, string principal,
        string action, string fingerprint, bool preview, string? approvedBy)
    {
        var keyed = principal != PlayerPrincipal && ability.Tier == "keyed_yes";
        var signature = CanonicalJson.Sha256Hex(CanonicalJson.Bytes(new JsonObject
        {
            ["capability"] = "fire", ["firepit"] = firepit.Id, ["center_m"] = KernelJson.Vector(center), ["radius_m"] = KernelJson.Num(radius), ["mode"] = Mode,
        }));
        var data = new JsonObject
        {
            ["center_m"] = KernelJson.Vector(center), ["radius_m"] = KernelJson.Num(radius), ["duration_s"] = KernelJson.Num(duration),
            ["cell_count"] = 1, ["requires_approval"] = keyed,
        };
        if (preview)
        {
            data["cells"] = new JsonArray(KernelJson.Vector(firepit.PositionM));
            data["cells_truncated"] = false;
            data["max_burning_cells"] = NatureState.MaxBurning;
            data["avatars_in_area"] = new JsonArray();
            // What the place made of the cast (decision 77), and the nothing it burns (the contract pairs a preview_id with affected_ids).
            data["affected_ids"] = new JsonArray();
            data["kind"] = FireKindWords.Firepit;
            foreach (var stale in _naturePreviews.Where(p => p.Value.Expires <= Clock()).Select(p => p.Key).ToList()) _naturePreviews.Remove(stale);
            while (_naturePreviews.Count >= 32) _naturePreviews.Remove(_naturePreviews.Keys.First());
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _naturePreviews[token] = new NaturePreview(principal, signature, Revision, Clock() + ApprovalLifetime);
            data["preview_id"] = token;
            return NaturePreviewed("nature.cast", principal, action, data, Array.Empty<string>());
        }
        var previewId = root.GetProperty("args").TryGetProperty("preview_id", out var given) ? given.GetString() : null;
        if (previewId == null || !_naturePreviews.TryGetValue(previewId, out var planned) || planned.Principal != principal || planned.Signature != signature ||
            planned.Expires <= Clock() || planned.Revision != Revision)
            throw new Refusal("revision_conflict", "Preview this fire again before casting: the place, what it would reach or its permission has changed.", "$.args.preview_id", retryable: true);
        if (keyed && approvedBy == null)
            return Hold(root, principal, action, fingerprint, "nature.cast", "The Gubble asks to light this firepit. Approve this exact firepit or deny it.", new(), bindRevision: true);
        var price = GuardEnergy(ability.Capability, principal);
        _naturePreviews.Remove(previewId);
        data["started_cells"] = 1;
        var committed = CommitNature(Nature.Copy(), "nature.cast", principal, action, fingerprint, approvedBy, Array.Empty<string>(), data, undo: false);
        // Lit, or relit when it was out (decision 73): the starting campfire burns again by leaving the out list alone.
        _outCampfires.Remove(firepit.Id);
        if (!firepit.Starting) _litCampfires.Add(firepit.Id);
        NoteCampChanged();
        ChargeEnergy(price);
        // Campfire is the first firepit lit (decision 63): relighting the starting campfire is not one. A committed Fire cast either way:
        // it counts toward Fire's hints, and toward three casts along a line (Fireline, CommandHostFireKinds).
        var learned = NoteFireForFireline(center, Revision).ToList();
        if (!firepit.Starting) learned.Add(ExpertiseLists.Campfire);
        RecordFireCast(learned);
        return committed;
    }

    // ---- putting a campfire out (decision 73): rain now, Snuff later ----

    /// <summary>
    /// Puts a lit campfire out (the starting campfire too): it is no camp until the Gubble's Fire relights it, saved with the island. The
    /// rain calls it (StepCampfireRain), and Snuff (decision 79, CommandHostFireKinds.CastSnuff). The simulation's change, not a room
    /// revision, like a fire burning out. Returns whether a campfire went out.
    /// </summary>
    public bool PutOutCampfire(string id)
    {
        if (!IsCampfireLit(id) || EnsureCamps().FirstOrDefault(c => c.Id == id) is not { } camp) return false;
        _litCampfires.Remove(id);
        _outCampfires.Add(id);
        _campRainS.Remove(id);
        if (_resting && CampLaws.Flat(_restAt, camp.PositionM) <= CampLaws.RestRadiusM) _resting = false;
        GD.Print($"CAMP {id} is out");
        NoteCampChanged();
        return true;
    }

    /// <summary>
    /// The rain on the campfires, a nature step's (CommandHostNature.StepNature): while it rains (the weather's rain, NatureLaws.WeatherRain01
    /// and up) every lit campfire counts the rain on it, and one that has had CampLaws.RainOutS goes out. The count starts again with
    /// each rain. Shelter is not modelled (the nature simulation wets every cell alike). Until the team has learned Fire, nothing could
    /// relight the starting campfire, so rain spares it (the integrator's provisional choice; the opening will script its own rain).
    /// </summary>
    internal void StepCampfireRain(float seconds)
    {
        if (Nature.Weather.Rain01 <= NatureLaws.WeatherRain01)
        {
            if (_campRainS.Count > 0) _campRainS.Clear();
            return;
        }
        if (Room == null || !IsInsideTree()) return;
        List<string>? goingOut = null;
        foreach (var camp in EnsureCamps())
        {
            if (!IsCampfireLit(camp.Id) || (camp.Starting && !FireLearned)) continue;
            var wet = _campRainS.GetValueOrDefault(camp.Id) + seconds;
            _campRainS[camp.Id] = wet;
            if (wet >= CampLaws.RainOutS) (goingOut ??= new List<string>()).Add(camp.Id);
        }
        if (goingOut != null) foreach (var id in goingOut) PutOutCampfire(id);
    }

    private void NoteCampChanged()
    {
        _teamDirty = true;
        SaveTeam();
        CampsChanged?.Invoke();
    }

    // ---- the start spot (decision 49) ----

    /// <summary>
    /// The boot's placing of the two bodies, once the island save is read and the mode set (RoomWorld): the spot this mode saved, if it
    /// is safe ground (dry, supported and with room for the whole body); else the starting campfire (the spawn, the fire in front), as
    /// on an island's first load. The Gubble goes to its own saved spot, else beside the camp. The camp the player last sat at becomes
    /// where a recovery lands. From then on the island save keeps the spots. Returns "saved", "fallback" or "first".
    /// </summary>
    public string PlaceAtStartSpot()
    {
        EnsureCamps();
        _spotsLive = true;
        if (GetNodeOrNull<CampKeeper>(CampKeeperPath) == null) AddChild(new CampKeeper { Name = CampKeeperName });
        if (Player == null || !IsInstanceValid(Player) || Room == null) return "first";
        var spawn = Room.SpawnFor("player");
        var saved = _startSpots.TryGetValue(Mode, out var spot) ? spot : null;
        string placed;
        // The first load leaves the bodies where the room put them (the spawns, the fire in front of the player): nothing moves.
        if (saved == null) placed = "first";
        else if (VectorOf(saved["player_m"]) is { } there && SafeGround(Player, there)) placed = "saved";
        else
        {
            Player.TryTeleportTo(spawn.PositionM);
            placed = "fallback";
        }
        if (placed != "first" && Companion != null && IsInstanceValid(Companion))
        {
            var gubble = placed == "saved" && VectorOf(saved!["companion_m"]) is { } at && SafeGround(Companion, at);
            if (!gubble) Companion.TryTeleportTo(Room.SpawnFor("companion", spawn).PositionM);
        }
        if (_homeCamp != null && IsCampfireLit(_homeCamp) && _homeSpot is { } home) Player.SetSpawnPoint(home);
        _spotNoted = Player.LastSafePosition;
        var camps = EnsureCamps();
        var nearest = camps.SelectMany((a, i) => camps.Skip(i + 1).Select(b => CampLaws.Flat(a.PositionM, b.PositionM))).DefaultIfEmpty(0).Min();
        var cover = _campLand.Select(p => camps.Min(c => CampLaws.Flat(p, c.PositionM))).DefaultIfEmpty(0).Max();
        GD.Print(System.FormattableString.Invariant($"CAMP start spot {placed} ({Mode}); campfires {camps.Count} ({camps.Count(c => IsCampfireLit(c.Id))} lit, {_outCampfires.Count} out) ") +
            System.FormattableString.Invariant($"over {_campLandM2:0} m2 of land, the nearest two {nearest:0.0} m apart, every land point within {cover:0.0} m of one: ") +
            string.Join(", ", camps.Select(c => System.FormattableString.Invariant($"{c.Id} ({c.PositionM.X:0.0}, {c.PositionM.Y:0.00}, {c.PositionM.Z:0.0})"))));
        return placed;
    }

    /// <summary>Safe ground for a body: no water there, and the body fits standing on a support (TryTeleportTo puts it there only then).</summary>
    private bool SafeGround(SmallPlayerController body, Vector3 feet) => !WaterColumn(feet, 0.3f, 0.05f).Wet && body.TryTeleportTo(feet);

    private static Vector3? VectorOf(JsonNode? node) =>
        node is JsonArray a && a.Count == 3 && a.All(v => v?.GetValueKind() == JsonValueKind.Number) ? new Vector3(Coordinate(a[0]!), Coordinate(a[1]!), Coordinate(a[2]!)) : null;

    /// <summary>A number however it was made (parsed, or written by this host as a double): read through its JSON text.</summary>
    private static float Coordinate(JsonNode value) => float.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A second's tick (CampKeeper): the island save is due again once the player's footing moved CampLaws.SpotMovedM.</summary>
    internal void NoteCampSpots()
    {
        if (!_spotsLive || Player == null || !IsInstanceValid(Player)) return;
        if (Player.LastSafePosition.DistanceTo(_spotNoted) < CampLaws.SpotMovedM) return;
        _spotNoted = Player.LastSafePosition;
        _teamDirty = true;
    }

    private const string CampKeeperName = "CampKeeper";
    private static readonly NodePath CampKeeperPath = CampKeeperName;

    // ---- the expertise announcement: every expertise newly learned, once ----

    private readonly Queue<(string Ability, string Id)> _learned = new();

    /// <summary>
    /// The next expertise newly learned and saved (Glow's, Bubbles', Fire's: every one PublishProgress adds), for the HUD's chime and
    /// card; each is given once, in the order learned. A store loaded or reconfigured announces nothing.
    /// </summary>
    public bool TakeLearned(out string ability, out string id)
    {
        if (_learned.TryDequeue(out var next)) { (ability, id) = next; return true; }
        ability = id = "";
        return false;
    }

    private void NoteLearned(GlowProgress before, GlowProgress after)
    {
        foreach (var list in ExpertiseLists.All)
            foreach (var id in list.Order)
                if (after.Has(list.Ability, id) && !before.Has(list.Ability, id) && _learned.Count < 16) _learned.Enqueue((list.Ability, id));
    }

    // ---- the island save's camp block (the team block's "camp") and the clock ("clock") ----

    /// <summary>
    /// The camp block: the spots per mode ({"player_m", "companion_m"}), the lit firepits, and the camp the player wakes at with its
    /// spot. Null while there is nothing to keep (a room no boot placed and nothing lit), so a save without camps is unchanged.
    /// </summary>
    internal JsonObject? CampBlock()
    {
        if (!_spotsLive && _startSpots.Count == 0 && _litCampfires.Count == 0 && _outCampfires.Count == 0 && _savedPits == null && _homeCamp == null) return null;
        var spots = new JsonObject();
        foreach (var (mode, spot) in _startSpots.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (!(_spotsLive && mode == Mode)) spots[mode] = spot.DeepClone();
        if (_spotsLive && Player != null && IsInstanceValid(Player) && Player.LastSafePosition.IsFinite())
        {
            var now = new JsonObject { ["player_m"] = KernelJson.Vector(Player.LastSafePosition) };
            if (Companion != null && IsInstanceValid(Companion) && Companion.GlobalPosition.IsFinite()) now["companion_m"] = KernelJson.Vector(Companion.GlobalPosition);
            spots[Mode] = now;
        }
        // A saved block not yet matched to this island's firepits (no physics yet) is written back as it was read.
        var lit = _savedPits?.Lit ?? _litCampfires.ToArray();
        var dark = _savedPits is { } pending ? pending.Out.Concat(_outCampfires.Where(id => id == CampLaws.StartingId)).ToArray() : _outCampfires.ToArray();
        var block = new JsonObject
        {
            ["spots"] = new JsonObject(spots.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
            ["lit"] = new JsonArray(lit.OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode?)id).ToArray()),
        };
        if (dark.Length > 0) block["out"] = new JsonArray(dark.OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode?)id).ToArray());
        // Each lit or put-out firepit's spot (decision 74): a later load keeps its id only while its spot is the same.
        var pits = new JsonObject();
        foreach (var id in lit.Concat(dark).Where(id => id != CampLaws.StartingId).OrderBy(id => id, StringComparer.Ordinal))
        {
            var spot = _savedPits is { } saved ? saved.Spots?.GetValueOrDefault(id) : _camps?.FirstOrDefault(c => c.Id == id)?.PositionM;
            if (spot is { } at) pits[id] = KernelJson.Vector(at);
        }
        if (pits.Count > 0 && pits.Count == lit.Concat(dark).Count(id => id != CampLaws.StartingId)) block["pits_m"] = pits;
        if (_homeCamp != null && _homeSpot is { } home)
        {
            block["home"] = _homeCamp;
            block["home_m"] = KernelJson.Vector(home);
        }
        return block;
    }

    /// <summary>What is wrong with a saved camp block (or null): the spots per mode, the lit firepits, the home camp and its spot.</summary>
    internal static string? CampBlockProblem(JsonNode? node)
    {
        if (node is not JsonObject block || block.Any(p => p.Key is not ("spots" or "lit" or "out" or "pits_m" or "home" or "home_m"))) return "the camp";
        if (block["spots"] is not JsonObject spots || spots.Any(p => p.Key is not ("creative" or "challenge" or "live") || p.Value is not JsonObject spot ||
            spot.Any(q => q.Key is not ("player_m" or "companion_m")) || !Vec3(spot["player_m"]) || (spot["companion_m"] != null && !Vec3(spot["companion_m"]))))
            return "the camp's spots";
        if (block["lit"] is not JsonArray lit || lit.Count > CampLaws.MaxFirepits || lit.Any(id => id?.GetValueKind() != JsonValueKind.String ||
            !CampIdPattern.IsMatch(id.GetValue<string>()) || id.GetValue<string>() == CampLaws.StartingId) ||
            lit.Select(id => id!.GetValue<string>()).Distinct().Count() != lit.Count) return "the camp's campfires";
        var litIds = lit.Select(id => id!.GetValue<string>()).ToList();
        var outIds = new List<string>();
        if (block["out"] is { } outNode)
        {
            if (outNode is not JsonArray dark || dark.Count is < 1 or > CampLaws.MaxFirepits + 1 || dark.Any(id => id?.GetValueKind() != JsonValueKind.String ||
                !CampIdPattern.IsMatch(id.GetValue<string>()))) return "the campfires that are out";
            outIds = dark.Select(id => id!.GetValue<string>()).ToList();
            if (outIds.Distinct().Count() != outIds.Count || outIds.Intersect(litIds).Any()) return "the campfires that are out";
        }
        if (block["pits_m"] is { } pitsNode)
        {
            var named = litIds.Concat(outIds).Where(id => id != CampLaws.StartingId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (pitsNode is not JsonObject pits || !pits.Select(p => p.Key).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(named) || pits.Any(p => !Vec3(p.Value)))
                return "the firepits' spots";
        }
        if ((block["home"] == null) != (block["home_m"] == null) || (block["home"] != null && (block["home"]!.GetValueKind() != JsonValueKind.String ||
            !CampIdPattern.IsMatch(block["home"]!.GetValue<string>()) || !Vec3(block["home_m"])))) return "the camp's home";
        return null;
    }

    /// <summary>The saved camp block, already checked (CampBlockProblem), or null: nothing saved.</summary>
    private void LoadCampBlock(JsonObject? block)
    {
        _litCampfires.Clear();
        _outCampfires.Clear();
        _campRainS.Clear();
        _savedPits = null;
        _loadedPits = null;
        _startSpots.Clear();
        _homeCamp = null;
        _homeSpot = null;
        _resting = false;
        if (block != null)
        {
            foreach (var (mode, spot) in block["spots"]!.AsObject()) _startSpots[mode] = (JsonObject)spot!.DeepClone();
            var lit = block["lit"]!.AsArray().Select(id => id!.GetValue<string>()).ToArray();
            var dark = block["out"] is JsonArray outs ? outs.Select(id => id!.GetValue<string>()).ToArray() : Array.Empty<string>();
            var spots = block["pits_m"] is JsonObject pits ? pits.ToDictionary(p => p.Key, p => VectorOf(p.Value)!.Value, StringComparer.Ordinal) : null;
            // The starting campfire is always this island's: only the firepits wait to be matched (ResolveSavedPits).
            if (dark.Contains(CampLaws.StartingId)) _outCampfires.Add(CampLaws.StartingId);
            if (lit.Concat(dark).Any(id => id != CampLaws.StartingId))
                _savedPits = (lit, dark.Where(id => id != CampLaws.StartingId).ToArray(), spots);
            if (block["home"] is { } home)
            {
                _homeCamp = home.GetValue<string>();
                _homeSpot = VectorOf(block["home_m"]);
            }
            _loadedPits = (_savedPits, _homeCamp, _homeSpot);
            // Matched now when the camps are already known (a save loaded into a running island).
            if (_savedPits != null && _camps != null) ResolveSavedPits(_camps, _camps[0].PositionM);
        }
        CampsChanged?.Invoke();
    }

    /// <summary>The island's clock record kept in the island save (M6's x_clock): believed only when the look's reader accepts it.</summary>
    private void LoadClockBlock(JsonNode? node)
    {
        if (node is JsonObject clock && global::GarageGame.Native.Look.WorldClock.FromNode(clock, out var error) != null && error.Length == 0) ClockRecord = (JsonObject)clock.DeepClone();
    }

    /// <summary>The team block with the camp and the clock, when there is any (CommandHostJournal.ExportTeam).</summary>
    private JsonObject WithCamp(JsonObject block)
    {
        if (CampBlock() is { } camp) block["camp"] = camp;
        if (ClockRecord != null) block["clock"] = ClockRecord.DeepClone();
        return block;
    }

    /// <summary>A team block's keys that are the camp's and the clock's (beside the journal's, the map's and whose save it is).</summary>
    private static bool IsCampKey(string key) => key is "camp" or "clock";

    /// <summary>What is wrong with a team block's camp and clock (the clock only in shape: the look's reader decides whether it is believed).</summary>
    private static string? CampKeysProblem(JsonObject team)
    {
        if (team["camp"] is { } camp && CampBlockProblem(camp) is { } problem) return problem;
        if (team["clock"] is { } clock && (clock is not JsonObject record || record.Count > 16)) return "the island's clock";
        return null;
    }
}

/// <summary>The host's second-long tick for the start spot (added by PlaceAtStartSpot, so only a booted island has it).</summary>
public partial class CampKeeper : Node
{
    private double _age;

    public override void _PhysicsProcess(double delta)
    {
        _age += delta;
        if (_age < 1) return;
        _age = 0;
        if (GetParent() is CommandHost host) host.NoteCampSpots();
    }
}
