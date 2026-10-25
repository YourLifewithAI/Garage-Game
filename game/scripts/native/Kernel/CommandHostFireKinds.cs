using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>What a Fire cast is, by its place (decision 77): plain Fire (an unlit firepit lit on its path), a Fireline, Snuff or a Will-o'-wisp.</summary>
public enum FireKind { Fire, Fireline, Snuff, Wisp }

/// <summary>The receipts' and previews' words for Fire's kinds (contracts: data.kind), and the price keys of the three new ones.</summary>
public static class FireKindWords
{
    public const string Fire = "fire";
    public const string Firepit = "firepit";
    public const string Fireline = "fireline";
    public const string Snuff = "snuff";
    public const string Wisp = "will_o_wisp";
}

/// <summary>
/// Fire's behaviours in numbers (docs/game/MECHANICS.md decisions 77 to 80). The decisions name the 3 m line, the 1.5 m snuff and its
/// 30 s, the wisp's 15 cm, 5 minutes and three; every other number here is provisional, for the founder to tune by playing.
/// </summary>
public static class FireKindLaws
{
    // Fireline (78).
    /// <summary>
    /// Learning it: three committed Fire casts within this many seconds: 5 minutes, provisional (decision 78 said 2 minutes; the status
    /// page's next-session list, "perhaps 5 minutes", for the second playtest round)...
    /// </summary>
    public const double FirelineLearnWindowS = 300;
    /// <summary>...each at least this far from the others (decision 78)...</summary>
    public const float FirelineLearnApartM = 0.5f;
    /// <summary>...whose centres all lie within this of one straight line (provisional: the decision says "along one line").</summary>
    public const float FirelineLearnOffLineM = 0.25f;
    /// <summary>A drag of at most this (decision 78), and at least FirelineMinM (shorter is no line: the HUD's tap).</summary>
    public const float FirelineMaxM = 3f;
    public const float FirelineMinM = 0.2f;
    /// <summary>The ground cells whose centres lie within this of the line catch at once (about one 5 cm cell either side).</summary>
    public const float FirelineHalfWidthM = 0.04f;
    /// <summary>The line's fire may spread this much past its ends, within Fire's radius (1.5 m), round the line's middle.</summary>
    public const float FirelineSpreadM = 0.3f;
    // Snuff (79).
    /// <summary>Everything burning in this circle goes out (decision 79), and the ground there cannot catch for SnuffDampS (decision 79).</summary>
    public const float SnuffRadiusM = 1.5f;
    public const float SnuffDampS = 30f;
    /// <summary>A Fire cast is "on something burning" when a burning cell lies within this of its point (or a lit campfire within this of its ring).</summary>
    public const float SnuffAimM = 0.15f;
    public const float SnuffAimRiseM = 0.3f;
    /// <summary>A snuffed cell is at least this wet (it can neither catch nor spread a fire while the damp lasts).</summary>
    public const float SnuffMoisture = NatureLaws.IgnitionMaxMoisture;
    /// <summary>A burning cell reaches up to this above or below the snuff's point (the circle is flat; a cliff's top is not snuffed from below).</summary>
    public const float SnuffRiseM = 0.5f;
    // Will-o'-wisp (80).
    /// <summary>It hovers this far over the water (decision 80: about 15 cm) and drifts slowly within WispDriftM of its spot, a turn in WispDriftPeriodS.</summary>
    public const float WispAboveWaterM = 0.15f;
    public const float WispDriftM = 0.25f;
    public const float WispDriftPeriodS = 40f;
    /// <summary>It lasts this long (decision 80: 5 minutes), at most MaxWisps at a time (decision 80: three), a fourth replacing the oldest; no stop removes one (decision 109).</summary>
    public const double WispLifeS = 300;
    public const int MaxWisps = 3;
    /// <summary>Water under the cast's point: its surface at most WispWaterAboveM above the point or WispWaterBelowM under it, at least WispMinDepthM deep.</summary>
    public const float WispWaterAboveM = 0.5f, WispWaterBelowM = 0.05f, WispMinDepthM = 0.03f;
}

/// <summary>A Will-o'-wisp (decision 80): transient like a bubble, never saved; the look draws it drifting round its spot.</summary>
public sealed class Wisp
{
    public required string Id { get; init; }
    /// <summary>Where it hovers: WispAboveWaterM over the water's surface at the cast's point.</summary>
    public required Vector3 Anchor { get; init; }
    public required DateTime Born { get; init; }
    public required DateTime Expires { get; init; }
    public required string Principal { get; init; }
}

/// <summary>
/// Fire's behaviours (the Fire behaviours packet, 17 October; MECHANICS.md decisions 77 to 80), on Fire's one command path, nature.cast
/// with capability fire, where the place decides as for the Bubble (FireKindOf):
/// <list type="bullet">
/// <item><b>Fireline</b> (78): a cast that names end_m, once learned, lights every ground cell along the line from center_m to end_m (at
/// most 3 m) that can catch, at once; the fire spreads within Fire's caps round the line's middle. One fire, one undo step in Creative,
/// every Fire guardrail, both ends and the middle within reach and sight (CheckFireline). Learned by three committed Fire casts within
/// 5 minutes (FireKindLaws.FirelineLearnWindowS, provisional; decision 78 said 2), at least 0.5 m apart, along one line
/// (NoteFireForFireline). Priced as "fireline".</item>
/// <item><b>Snuff</b> (79): once learned, Fire aimed at something burning puts out every burning cell in a 1.5 m circle and every lit
/// campfire in it (PutOutCampfire); the ground there cannot catch for 30 s (InSnuffZone). It never hurts anyone, is no fire, and is
/// allowed while the Gubble's fire budget waits; the Gubble's own-idea Snuff of a lit campfire waits for the player's yes. A simulation
/// change like X's, kept by the nature save, with a transient receipt. Learned by rain on a fire the Gubble started, or a bubble cast on
/// a fire (StepFireKinds). Priced as "snuff".</item>
/// <item><b>Will-o'-wisp</b> (80): Fire cast on water makes a cold flame hovering over it, drifting slowly (the look's), a light at night;
/// it burns nothing and is not the Gubble's one fire; it lasts 5 minutes, at most three (a fourth replaces the oldest), and is never saved
/// (transient, like a bubble). A wisp is not a fire (decision 109): no stop removes it, X included (X removed the oldest until 25
/// October). The first one teaches it. Priced as "will_o_wisp".</item>
/// </list>
/// Every Fire kind is previewed first and committed as exactly that preview (the preview's kind and outline are what the HUD shows); since
/// decision 83 the player's are both in one click, back to back (PlayerFireLight), while the Gubble's stay its own two commands. The
/// shared rules: each expertise once per team, by either of the pair, announced by Fire's camp's chime and card; a committed Fire cast of
/// any kind counts toward Fire's hints (RecordFireCast); a fire that hurt the player or scorched a creation teaches nothing (MarkFireHarm).
/// </summary>
public partial class CommandHost
{
    /// <summary>The refusals for a kind not learned yet (the companion's mock says the same).</summary>
    public const string FirelineNotLearned = "The Gubble has not learned Fireline yet: cast plain Fire instead.";

    private readonly List<Wisp> _wisps = new();
    /// <summary>The snuffed circles whose ground cannot catch, until the weather clock passes Until.</summary>
    private readonly List<(Vector3 Center, float Radius, double Until)> _snuffZones = new();
    /// <summary>The team's recent committed Fire casts (for Fireline's learning): when, where, and the fire's room revision.</summary>
    private readonly List<(DateTime At, Vector3 Center, int Revision)> _recentFires = new();
    /// <summary>The room revisions of the fires that hurt the player or scorched a creation: they teach nothing (the shared rules).</summary>
    private readonly HashSet<int> _harmfulFires = new();
    /// <summary>The bubbles already looked at for Snuff (a bubble cast on a fire).</summary>
    private readonly HashSet<string> _bubblesSeenForSnuff = new(StringComparer.Ordinal);

    /// <summary>Whether the team has learned each of Fire's later kinds (decisions 78 to 80).</summary>
    public bool FirelineLearned => GlowProgress.Has(ExpertiseLists.Fire, ExpertiseLists.Fireline);
    public bool SnuffLearned => GlowProgress.Has(ExpertiseLists.Fire, ExpertiseLists.Snuff);

    /// <summary>The Will-o'-wisps on the island now, oldest first (the look draws them).</summary>
    public IReadOnlyList<Wisp> Wisps => _wisps;

    /// <summary>Raised when a wisp comes or goes.</summary>
    public event Action? WispsChanged;

    // ---- what the player's controls send ----

    /// <summary>The wheel's and key 6's Fire preview at a point (and, for a Fireline, to an end): the host says what the place makes of it.</summary>
    public JsonObject PlayerFirePreview(Vector3 point, Vector3? end = null) => PlayerCommand("nature.cast", FireArgs(point, end, null), preview: true);

    /// <summary>The cast of exactly that preview.</summary>
    public JsonObject PlayerFireCast(Vector3 point, Vector3? end, string previewId) => PlayerCommand("nature.cast", FireArgs(point, end, previewId));

    /// <summary>
    /// What the player's Fire on the click answered (PlayerFireLight, decision 83): the preview (what the place made of it, data.kind, and
    /// its outline, data.center_m, radius_m, end_m and cells, for the HUD to flash as it lights) and the cast's own result: lit (ok), held
    /// for Creative's one ask about builds (approval_required: the host's card, as before), or refused. A refused preview is both, and
    /// nothing was cast.
    /// </summary>
    public readonly record struct FireClick(JsonObject Preview, JsonObject Result)
    {
        /// <summary>The preview was made (its outline can flash), whatever the cast then answered.</summary>
        public bool Previewed => Preview["ok"]?.GetValue<bool>() == true;
        /// <summary>The fire (or firepit, Snuff, wisp, Fireline) is lit: the cast committed.</summary>
        public bool Lit => Result["ok"]?.GetValue<bool>() == true;
        /// <summary>Creative's one ask about builds holds the cast: the card answers it (approve: they burn; deny: the fire spares them).</summary>
        public bool Held => Result["error"]?["code"]?.GetValue<string>() == "approval_required";
        /// <summary>What the place made of it (FireKindWords: fire, firepit, fireline, snuff or will_o_wisp), from the preview; "" when refused.</summary>
        public string Kind => Preview["data"]?["kind"]?.GetValue<string>() ?? "";
    }

    /// <summary>
    /// The player's Fire lit on the click (decision 83: "being forced to wait for a preview [...] is annoying. Let's remove the wait time"),
    /// from the wheel, key 6 or the Magic panel: the preview and the cast of exactly that preview back to back, with no hold and nothing
    /// between them, on Fire's one command path (two nature.cast commands, as every Fire: the recorder logs the first as a preview, which
    /// costs nothing). Everything else is as it was: the place decides (an unlit firepit, Snuff on something burning, a wisp on water, a
    /// Fireline with an end once learned), Creative's one ask about builds still holds the cast for the player's card, the price is charged
    /// once at commit, and every guardrail and refusal stands (a refused preview casts nothing). The radius and duration are the Magic
    /// panel's when it names them; otherwise the pack's defaults, as the wheel's. The Gubble's own-idea Fire is untouched: its preview and
    /// cast stay two commands of its own, a moment apart, as the companion sends them.
    /// </summary>
    public FireClick PlayerFireLight(Vector3 point, Vector3? end = null, float? radius = null, float? duration = null)
    {
        var preview = PlayerCommand("nature.cast", FireArgs(point, end, null, radius, duration), preview: true);
        if (preview["ok"]?.GetValue<bool>() != true || preview["data"]?["preview_id"]?.GetValue<string>() is not { } token) return new FireClick(preview, preview);
        return new FireClick(preview, PlayerCommand("nature.cast", FireArgs(point, end, token, radius, duration)));
    }

    private JsonObject FireArgs(Vector3 point, Vector3? end, string? previewId, float? radius = null, float? duration = null)
    {
        var args = NatureCastArgs("fire", point, radius, duration);
        if (end is { } to) args["end_m"] = KernelJson.Vector(to);
        if (previewId != null) args["preview_id"] = previewId;
        return args;
    }

    // ---- the place decides (decision 77) ----

    /// <summary>
    /// What a Fire cast is: a drag (end_m) is a Fireline, refused until it is learned; on water a Will-o'-wisp; on something burning
    /// Snuff, once learned (until then it is plain Fire, which waits for the fire there as always); else plain Fire, which lights an
    /// unlit firepit in its circle on the firepit's own path (CastFire). The precise places (water, a fire) come before the firepit's
    /// circle, so a Snuff aimed beside a firepit is a Snuff.
    /// </summary>
    private FireKind FireKindOf(JsonElement args)
    {
        if (args.TryGetProperty("end_m", out _))
        {
            if (!FirelineLearned) throw new Refusal("permission_denied", FirelineNotLearned, "$.args.end_m");
            return FireKind.Fireline;
        }
        var center = KernelJson.ReadVector(args.GetProperty("center_m"));
        if (WaterForWisp(center) != null) return FireKind.Wisp;
        if (SnuffLearned && BurningAt(center)) return FireKind.Snuff;
        return FireKind.Fire;
    }

    /// <summary>The water under a Fire cast's point, deep enough for a wisp to hover over: its surface's height, or null.</summary>
    private float? WaterForWisp(Vector3 point)
    {
        if (!point.IsFinite()) return null;
        var water = WaterColumn(point, FireKindLaws.WispWaterAboveM, FireKindLaws.WispWaterBelowM);
        return water.Wet && water.DepthM >= FireKindLaws.WispMinDepthM && water.SurfaceY >= point.Y - FireKindLaws.WispWaterBelowM ? water.SurfaceY : null;
    }

    /// <summary>Whether something burns at a point: a burning cell within SnuffAimM of it, or a lit campfire whose ring is.</summary>
    private bool BurningAt(Vector3 point)
    {
        foreach (var cell in Nature.Cells.Values)
            if (cell.BurningS > 0 && CampLaws.Flat(cell.Position, point) <= FireKindLaws.SnuffAimM && Math.Abs(cell.Y - point.Y) <= FireKindLaws.SnuffAimRiseM) return true;
        return EnsureCamps().Any(c => IsCampfireLit(c.Id) && CampLaws.Flat(c.PositionM, point) <= FireKindLaws.SnuffAimM + CampLaws.FirepitCatchM &&
            Math.Abs(c.PositionM.Y - point.Y) <= CampLaws.FirepitCatchRiseM);
    }

    // ---- Fireline (decision 78) ----

    /// <summary>A Fireline's end, checked: inside the island, at most FirelineMaxM from its start and no shorter than FirelineMinM, and both its end and its middle within the Gubble's reach and the team's sight (CheckCast checked the start).</summary>
    private Vector3 CheckFireline(IslandAbility ability, JsonElement args, Cast cast, string principal)
    {
        var end = KernelJson.ReadVector(args.GetProperty("end_m"));
        var length = cast.Center.DistanceTo(end);
        if (length > FireKindLaws.FirelineMaxM)
            throw new Refusal("invalid_args", $"A Fireline is at most {FireKindLaws.FirelineMaxM:0.#} m long.", "$.args.end_m", allowed: JsonValue.Create(FireKindLaws.FirelineMaxM));
        if (length < FireKindLaws.FirelineMinM) throw new Refusal("invalid_args", "That line is too short: drag it out, or cast plain Fire.", "$.args.end_m");
        if (!Room.Bounds.Grow(0.02f).HasPoint(end)) throw new Refusal("out_of_bounds", "Choose a line inside this island.", "$.args.end_m");
        var middle = (cast.Center + end) * 0.5f;
        foreach (var point in new[] { end, middle })
        {
            if (Companion == null || Companion.GlobalPosition.DistanceTo(point) > ability.ReachM)
                throw new Refusal("out_of_bounds", "Bring the Gubble closer: the whole line must be within its reach.", "$.args.end_m", retryable: true);
            if (!TeamSeesSpot(point, principal)) throw new Refusal("target_not_found", "The team must see the whole line the Gubble will light.", "$.args.end_m");
        }
        return end;
    }

    /// <summary>The fire's circle for a line: round its middle, wide enough for both ends and FirelineSpreadM past them, within Fire's radius.</summary>
    private static (Vector3 Center, float Radius) FirelineArea(Vector3 start, Vector3 end, IslandAbility ability)
    {
        var most = (float)Math.Min(ability.AreaRadiusMaxM, NatureBlock.MaxFireRadiusM);
        return ((start + end) * 0.5f, Math.Min(most, CampLaws.Flat(start, end) * 0.5f + FireKindLaws.FirelineSpreadM));
    }

    /// <summary>The ground cells along a line: their centres within FirelineHalfWidthM of it, from its start to its end.</summary>
    internal static List<(int X, int Z)> LineCells(Vector3 start, Vector3 end)
    {
        var a = new Vector2(start.X, start.Z);
        var b = new Vector2(end.X, end.Z);
        var along = b - a;
        var length2 = Math.Max(along.LengthSquared(), 1e-8f);
        var half = FireKindLaws.FirelineHalfWidthM;
        var (x0, z0) = NatureState.Grid(new Vector3(Math.Min(a.X, b.X) - half, 0, Math.Min(a.Y, b.Y) - half));
        var (x1, z1) = NatureState.Grid(new Vector3(Math.Max(a.X, b.X) + half, 0, Math.Max(a.Y, b.Y) + half));
        var found = new List<(float T, int X, int Z)>();
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        {
            var centre = new Vector2((x + 0.5f) * NatureState.CellM, (z + 0.5f) * NatureState.CellM);
            var t = Math.Clamp((centre - a).Dot(along) / length2, 0f, 1f);
            if ((a + along * t).DistanceTo(centre) <= half) found.Add((t, x, z));
        }
        return found.OrderBy(f => f.T).ThenBy(f => f.X).ThenBy(f => f.Z).Select(f => (f.X, f.Z)).ToList();
    }

    /// <summary>The line's height over a cell (its ends' heights, in between along it): where the ground probe starts.</summary>
    private static float LineHeightAt(Vector3 start, Vector3 end, int x, int z)
    {
        var a = new Vector2(start.X, start.Z);
        var along = new Vector2(end.X, end.Z) - a;
        var centre = new Vector2((x + 0.5f) * NatureState.CellM, (z + 0.5f) * NatureState.CellM);
        var t = Math.Clamp((centre - a).Dot(along) / Math.Max(along.LengthSquared(), 1e-8f), 0f, 1f);
        return start.Y + (end.Y - start.Y) * t;
    }

    /// <summary>
    /// A committed Fire cast (plain Fire or a firepit lit) at a point, toward Fireline (decision 78): Fireline is learned when this and two
    /// others of the last FirelineLearnWindowS, each at least FirelineLearnApartM from the others, lie within FirelineLearnOffLineM of one
    /// straight line, and none of the three hurt the player or scorched a creation. Returns what it taught (none, or Fireline).
    /// </summary>
    internal IEnumerable<string> NoteFireForFireline(Vector3 center, int revision)
    {
        var now = Clock();
        _recentFires.RemoveAll(f => (now - f.At).TotalSeconds > FireKindLaws.FirelineLearnWindowS || f.At > now);
        var newest = (At: now, Center: center, Revision: revision);
        var earlier = _recentFires.Where(f => !_harmfulFires.Contains(f.Revision)).ToList();
        _recentFires.Add(newest);
        while (_recentFires.Count > 16) _recentFires.RemoveAt(0);
        if (FirelineLearned) return Array.Empty<string>();
        for (var i = 0; i < earlier.Count; i++)
        for (var j = i + 1; j < earlier.Count; j++)
            if (AlongOneLine(earlier[i].Center, earlier[j].Center, center)) return new[] { ExpertiseLists.Fireline };
        return Array.Empty<string>();
    }

    /// <summary>Three centres at least FirelineLearnApartM apart (on the ground plane), each within FirelineLearnOffLineM of the line through the two farthest apart.</summary>
    internal static bool AlongOneLine(Vector3 p, Vector3 q, Vector3 r)
    {
        var points = new[] { new Vector2(p.X, p.Z), new Vector2(q.X, q.Z), new Vector2(r.X, r.Z) };
        var (i, j, k) = (0, 1, 2);
        var best = -1f;
        foreach (var (a, b, c) in new[] { (0, 1, 2), (0, 2, 1), (1, 2, 0) })
        {
            var apart = points[a].DistanceTo(points[b]);
            if (points[a].DistanceTo(points[b]) < FireKindLaws.FirelineLearnApartM) return false;
            if (apart > best) { best = apart; (i, j, k) = (a, b, c); }
        }
        var along = (points[j] - points[i]).Normalized();
        var offset = points[k] - points[i];
        return Math.Abs(offset.X * along.Y - offset.Y * along.X) <= FireKindLaws.FirelineLearnOffLineM;
    }

    /// <summary>A fire hurt the player or scorched a creation (CommandHostNature's step): it teaches no Fire expertise (the shared rules).</summary>
    private void MarkFireHarm(NatureFire fire)
    {
        if (_harmfulFires.Count < 256) _harmfulFires.Add(fire.Revision);
    }

    // ---- Snuff (decision 79) ----

    /// <summary>
    /// Snuff: every burning cell in a SnuffRadiusM circle round the point goes out, and every lit campfire in it (PutOutCampfire); the
    /// ground there cannot catch for SnuffDampS (InSnuffZone) and the cells there are damp. Previewed first, then committed as exactly that
    /// preview; the Gubble's own-idea Snuff that would put out a lit campfire waits for the player's yes (it is the player's camp), while
    /// a wild fire asks nothing. No room revision: the simulation's change, as X's, saved at once, with a transient receipt.
    /// </summary>
    private JsonObject CastSnuff(JsonElement root, IslandAbility ability, Cast cast, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        var center = cast.Center;
        var radius = FireKindLaws.SnuffRadiusM;
        var burning = Nature.Cells.Values.Where(c => c.BurningS > 0 && InCircle(c.Position, center, radius)).ToList();
        var campfires = EnsureCamps().Where(c => IsCampfireLit(c.Id) && CampLaws.Flat(c.PositionM, center) <= radius + CampLaws.FirepitCatchM &&
            Math.Abs(c.PositionM.Y - center.Y) <= FireKindLaws.SnuffRiseM).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (burning.Count == 0 && campfires.Count == 0) throw new Refusal("invalid_args", "Nothing is burning there now.", "$.args.center_m");
        var companion = principal != PlayerPrincipal;
        var keyed = companion && ability.Tier == "keyed_yes";
        var mustAsk = keyed || (companion && campfires.Count > 0);
        var signature = CanonicalJson.Sha256Hex(CanonicalJson.Bytes(new JsonObject
        {
            ["capability"] = "fire", ["kind"] = FireKindWords.Snuff, ["center_m"] = KernelJson.Vector(center), ["radius_m"] = KernelJson.Num(radius), ["mode"] = Mode,
            ["campfires"] = new JsonArray(campfires.Select(id => (JsonNode?)id).ToArray()),
        }));
        var seen = burning.Where(TeamSeesCellFor(principal)).ToList();
        var data = new JsonObject
        {
            ["center_m"] = KernelJson.Vector(center), ["radius_m"] = KernelJson.Num(radius), ["duration_s"] = KernelJson.Num(FireKindLaws.SnuffDampS),
            ["cell_count"] = seen.Count, ["requires_approval"] = mustAsk, ["kind"] = FireKindWords.Snuff,
        };
        if (preview)
        {
            data["cells"] = new JsonArray(seen.Take(MaxPreviewCells).Select(c => (JsonNode?)KernelJson.Vector(c.Position)).ToArray());
            data["cells_truncated"] = seen.Count > MaxPreviewCells;
            data["preview_id"] = RememberFirePreview(principal, signature);
            return NaturePreviewed("nature.cast", principal, action, data, Array.Empty<string>());
        }
        var previewId = UseFirePreview(root, principal, signature);
        if (mustAsk && approvedBy == null)
            return Hold(root, principal, action, fingerprint, "nature.cast", keyed && campfires.Count == 0
                ? "The Gubble asks to put out the fire here with Snuff. Approve this exact area or deny it."
                : "The Gubble asks to put out your campfire with Snuff. Approve: it goes out until the Gubble's Fire relights it. Deny: it keeps burning.",
                new(), bindRevision: true);
        var price = GuardEnergy(FireKindWords.Snuff, principal);
        _naturePreviews.Remove(previewId);
        foreach (var cell in burning) cell.BurningS = 0;
        foreach (var cell in Nature.Cells.Values.Where(c => InCircle(c.Position, center, radius))) cell.Moisture = Math.Max(cell.Moisture, FireKindLaws.SnuffMoisture);
        _snuffZones.Add((center, radius, Nature.WeatherS + FireKindLaws.SnuffDampS));
        while (_snuffZones.Count > 16) _snuffZones.RemoveAt(0);
        // The fire is out once nothing of it burns; the Gubble's wait counts from now, as after its fire went out any other way.
        if (Nature.Fire is { } fire && !Nature.Cells.Values.Any(c => c.BurningS > 0))
        {
            if (fire.Principal == CompanionPrincipal) _companionFireSeenS = Nature.WeatherS;
            Nature.Fire = null;
            ForgetPlantFuel();
        }
        foreach (var id in campfires) PutOutCampfire(id);
        _natureDirty = true;
        NatureChanged?.Invoke();
        SaveNature();
        ChargeEnergy(price);
        RecordFireCast(Array.Empty<string>());
        data["started_cells"] = 0;
        return FireTransient(principal, action, fingerprint, data, null, approvedBy);
    }

    private static bool InCircle(Vector3 at, Vector3 center, float radius) =>
        CampLaws.Flat(at, center) <= radius && Math.Abs(at.Y - center.Y) <= FireKindLaws.SnuffRiseM;

    /// <summary>Whether a cell lies in a snuffed circle whose damp still lasts: it can neither catch nor carry a fire.</summary>
    private bool InSnuffZone(NatureCell cell)
    {
        foreach (var zone in _snuffZones)
            if (zone.Until > Nature.WeatherS && InCircle(cell.Position, zone.Center, zone.Radius)) return true;
        return false;
    }

    // ---- Will-o'-wisp (decision 80) ----

    /// <summary>
    /// A Will-o'-wisp: Fire cast on water makes a cold flame WispAboveWaterM over the water at the point; it burns nothing and is no fire
    /// (the one-fire rule and the Gubble's guardrails pass it by), lasts WispLifeS and is one of at most MaxWisps (a fourth replaces the oldest). It is
    /// never saved: a transient receipt names its effect: id, as a bubble's does. The first one teaches the expertise.
    /// </summary>
    private JsonObject CastWisp(JsonElement root, IslandAbility ability, Cast cast, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        var surface = WaterForWisp(cast.Center) ?? throw new Refusal("invalid_args", "A Will-o'-wisp needs water under it.", "$.args.center_m");
        var at = new Vector3(cast.Center.X, surface + FireKindLaws.WispAboveWaterM, cast.Center.Z);
        var keyed = principal != PlayerPrincipal && ability.Tier == "keyed_yes";
        var signature = CanonicalJson.Sha256Hex(CanonicalJson.Bytes(new JsonObject
        {
            ["capability"] = "fire", ["kind"] = FireKindWords.Wisp, ["center_m"] = KernelJson.Vector(cast.Center), ["mode"] = Mode,
        }));
        var data = new JsonObject
        {
            ["center_m"] = KernelJson.Vector(cast.Center), ["radius_m"] = KernelJson.Num(FireKindLaws.WispDriftM), ["duration_s"] = KernelJson.Num((float)FireKindLaws.WispLifeS),
            ["cell_count"] = 0, ["requires_approval"] = keyed, ["kind"] = FireKindWords.Wisp,
        };
        if (preview)
        {
            data["cells"] = new JsonArray();
            data["cells_truncated"] = false;
            data["preview_id"] = RememberFirePreview(principal, signature);
            return NaturePreviewed("nature.cast", principal, action, data, Array.Empty<string>());
        }
        var previewId = UseFirePreview(root, principal, signature);
        if (keyed && approvedBy == null)
            return Hold(root, principal, action, fingerprint, "nature.cast", "The Gubble asks to set a Will-o'-wisp over the water here.", new(), bindRevision: true);
        var price = GuardEnergy(FireKindWords.Wisp, principal);
        _naturePreviews.Remove(previewId);
        while (_wisps.Count >= FireKindLaws.MaxWisps) _wisps.RemoveAt(0);
        var now = Clock();
        var wisp = new Wisp
        {
            // 'effect:' and 26 lowercase base32 characters (130 random bits), as a bubble's: opaque, never a counter.
            Id = "effect:" + System.Security.Cryptography.RandomNumberGenerator.GetString(IdAlphabet, 26),
            Anchor = at, Born = now, Expires = now + TimeSpan.FromSeconds(FireKindLaws.WispLifeS), Principal = principal,
        };
        _wisps.Add(wisp);
        WispsChanged?.Invoke();
        ChargeEnergy(price);
        RecordFireCast(new[] { ExpertiseLists.WillOWisp });
        data["started_cells"] = 0;
        data["wisp"] = wisp.Id;
        return FireTransient(principal, action, fingerprint, data, wisp.Id, approvedBy);
    }

    // ---- each nature step ----

    /// <summary>
    /// A nature step's share (CommandHostNature.StepNature): the wisps whose time is up go; the damp of old snuffs dries; Snuff is learned
    /// when rain falls on a fire the Gubble started, or a bubble is cast on a fire (decision 79), the host verifying both from the
    /// simulation's own state, and never from a fire that hurt the player or scorched a creation.
    /// </summary>
    private void StepFireKinds()
    {
        var now = Clock();
        if (_wisps.RemoveAll(w => w.Expires <= now) > 0) WispsChanged?.Invoke();
        _snuffZones.RemoveAll(z => z.Until <= Nature.WeatherS);
        if (_bubblesSeenForSnuff.Count > 0) _bubblesSeenForSnuff.RemoveWhere(id => _bubbles.All(b => b.Id != id));
        var fire = Nature.Fire;
        var burning = fire == null ? null : Nature.Cells.Values.Where(c => c.BurningS > 0).ToList();
        // A bubble cast on a fire: each bubble is looked at once, at the first step it is there.
        foreach (var bubble in _bubbles)
        {
            if (!_bubblesSeenForSnuff.Add(bubble.Id) || burning is not { Count: > 0 } || _harmfulFires.Contains(fire!.Revision) || SnuffLearned) continue;
            var floor = bubble.Center.Y - bubble.Radius;
            if (burning.Any(c => CampLaws.Flat(c.Position, bubble.Center) <= bubble.Radius + FireKindLaws.SnuffAimM && c.Y >= floor - FireKindLaws.SnuffAimRiseM && c.Y <= bubble.Center.Y + bubble.Radius))
                RecordExpertise(ExpertiseLists.Fire, ExpertiseLists.Snuff);
        }
        // Rain on a fire the Gubble started.
        if (fire is { Principal: CompanionPrincipal } && burning is { Count: > 0 } && Nature.Weather.Rain01 > NatureLaws.WeatherRain01 &&
            !_harmfulFires.Contains(fire.Revision) && !SnuffLearned)
            RecordExpertise(ExpertiseLists.Fire, ExpertiseLists.Snuff);
    }

    /// <summary>A load forgets what lives only in memory: the wisps, the damp, the recent casts and the harm marks.</summary>
    private void ForgetFireKinds()
    {
        var hadWisps = _wisps.Count > 0;
        _wisps.Clear();
        _snuffZones.Clear();
        _recentFires.Clear();
        _harmfulFires.Clear();
        _bubblesSeenForSnuff.Clear();
        if (hadWisps) WispsChanged?.Invoke();
    }

    // ---- the shared rules: counted casts and hints ----

    /// <summary>
    /// A committed Fire cast of any kind, as the store counts it (the shared rules, as Grow's RecordGrowCast): the expertise it showed
    /// learned once each, and at the 10th to 50th cast the hint of the first Fire expertise neither learned nor hinted. A reward, never a
    /// precondition: a store that cannot be saved leaves the cast standing; each newly learned one is announced (PublishProgress).
    /// </summary>
    private void RecordFireCast(IEnumerable<string> learned)
    {
        GlowProgress candidate;
        try { candidate = GlowProgress.AfterAbilityCast(ExpertiseLists.Fire, learned); }
        catch (GlowProgressException) { return; }
        TrySaveExpertise(candidate);
    }

    // ---- previews and receipts shared by the kinds ----

    /// <summary>A Fire kind's preview handle: 128 random bits, kept for the caster and exactly this cast at this revision, as every Fire preview's.</summary>
    private string RememberFirePreview(string principal, string signature)
    {
        foreach (var stale in _naturePreviews.Where(p => p.Value.Expires <= Clock()).Select(p => p.Key).ToList()) _naturePreviews.Remove(stale);
        while (_naturePreviews.Count >= 32) _naturePreviews.Remove(_naturePreviews.Keys.First());
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _naturePreviews[token] = new NaturePreview(principal, signature, Revision, Clock() + ApprovalLifetime);
        return token;
    }

    /// <summary>The cast names its caster's own, unexpired preview of exactly this cast at this revision (revision_conflict otherwise).</summary>
    private string UseFirePreview(JsonElement root, string principal, string signature)
    {
        var previewId = root.GetProperty("args").TryGetProperty("preview_id", out var given) ? given.GetString() : null;
        if (previewId == null || !_naturePreviews.TryGetValue(previewId, out var planned) || planned.Principal != principal || planned.Signature != signature ||
            planned.Expires <= Clock() || planned.Revision != Revision)
            throw new Refusal("revision_conflict", "Preview this again before casting: the place or what is burning there has changed.", "$.args.preview_id", retryable: true);
        return previewId;
    }

    /// <summary>A Snuff's or a wisp's committed cast: a transient receipt (nothing of it is a room revision), kept like every transient one.</summary>
    private JsonObject FireTransient(string principal, string action, string fingerprint, JsonObject data, string? created, string? approvedBy)
    {
        var result = Result("nature.cast", principal, action, null, true);
        result["transient"] = true;
        if (created != null)
        {
            result["affected"] = new JsonArray(created);
            result["created"] = new JsonArray(created);
        }
        if (approvedBy != null && principal != PlayerPrincipal) result["approved_by"] = approvedBy;
        result["data"] = Normalized(data);
        var key = principal + "|" + action;
        if (!_transientOrder.TryGetValue(principal, out var order)) _transientOrder[principal] = order = new LinkedList<string>();
        if (_transient.ContainsKey(key)) order.Remove(key);
        _transient[key] = (fingerprint, (JsonObject)result.DeepClone());
        order.AddLast(key);
        while (order.Count > MaxTransientPerPrincipal)
        {
            _transient.Remove(order.First!.Value);
            order.RemoveFirst();
        }
        return result;
    }
}
