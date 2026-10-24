using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>What fire does to a thing (the founder's rule, 13 October; CommandHost.FireRule).</summary>
public enum FireOutcome { Fireproof, Scorches, BurnsDown }

/// <summary>One row of the fire rule: what fire does to a kind of thing in a mode, and whether the player is asked first.</summary>
public readonly record struct FireRuling(FireOutcome Outcome, bool AskFirst);

/// <summary>
/// Grow (CommandHostGrow), weather and Fire on the ground field, host side (docs/magic/README.md). Bloom and its flower trail
/// went with decision 52: an older island's flowers are dropped when it loads, and nature.trail is refused. Nature is room state:
/// the authority saves it as the host's nature block beside the team block (NatureBlock), checked whole when a save loads.
/// - Durable nature changes (nature.cast, nature.repair, and room.undo of them) are ordinary room revisions:
///   each goes through the authority with a durable receipt (compacted by room.checkpoint like any other), and room.undo
///   names the room revision to go back to, as the contract says. The latest nature changes can be taken back, by either
///   principal; a companion's undo of the player's change waits for the player.
/// - The simulation (weather, spread, burning objects and plants) runs in memory from _PhysicsProcess in fixed steps and is saved
///   with every durable command and by a throttle, as the team block is: never moving the room revision.
/// - Every nature change needs a loaded save and a role that builds (RequireChange), before a preview, a hold or a commit,
///   and again when a held command commits after the player's click.
/// - The fire rule is one table, FireRule, read by every fire decision: preview targets, spread, object burn and the one ask.
/// - What a companion learns (affected things, counts, scorched creations, the avatars in a fire's area) is filtered by what
///   the team has perceived; the host keeps the complete set for protection and approval checks.
/// - Fire hurts whoever stands in it in Challenge and Live, whoever lit it (the founder, 13 October): the Gubble's fire is
///   kept in bounds by guardrails and feedback (CommandHostConsequences), never by a hold for the player's sake.
/// - Undo exists only in Creative (the founder, 13 October): each undo step remembers its mode, and a step recorded
///   outside Creative, or a fire that burned on outside it, can never be taken back.
/// </summary>
public partial class CommandHost
{
    /// <summary>
    /// The founder's fire rule (13 October, as decided through the day), the one table every fire decision reads:
    /// - locked things are always fireproof, in every mode;
    /// - creations (the player's, or the Gubble's on the player's behalf) survive fire as scorched, repairable remains that
    ///   keep their design (triggers off until repaired). In Creative, before any fire, the player's or the Gubble's, burns an
    ///   unlocked one, the player is asked once whether it may burn: yes, it is scorched (repair is free in Creative); no,
    ///   the burn stops at it. In Challenge and Live nothing asks, whoever lit the fire: "This is part of learning the
    ///   consequences of the actions you take." Repair costs the original materials there;
    /// - the island's own stock and generated objects burn down completely;
    /// - the island's plants (its plant copies, grown or not) burn as any other object does: down, gone with their collision, and
    ///   they stay gone unless replanted (decision 67); nothing asks (CommandHostGrow.BurnPlants).
    /// The next change to the rule is one edit here.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Kind, string Mode), FireRuling> FireRule = new Dictionary<(string, string), FireRuling>
    {
        [("locked", "creative")] = new(FireOutcome.Fireproof, AskFirst: false),
        [("locked", "challenge")] = new(FireOutcome.Fireproof, AskFirst: false),
        [("locked", "live")] = new(FireOutcome.Fireproof, AskFirst: false),
        [("creation", "creative")] = new(FireOutcome.Scorches, AskFirst: true),
        [("creation", "challenge")] = new(FireOutcome.Scorches, AskFirst: false),
        [("creation", "live")] = new(FireOutcome.Scorches, AskFirst: false),
        [("stock", "creative")] = new(FireOutcome.BurnsDown, AskFirst: false),
        [("stock", "challenge")] = new(FireOutcome.BurnsDown, AskFirst: false),
        [("stock", "live")] = new(FireOutcome.BurnsDown, AskFirst: false),
        [("plant", "creative")] = new(FireOutcome.BurnsDown, AskFirst: false),
        [("plant", "challenge")] = new(FireOutcome.BurnsDown, AskFirst: false),
        [("plant", "live")] = new(FireOutcome.BurnsDown, AskFirst: false),
    };

    /// <summary>A fire may reach at most this many things; a larger area is refused before it is previewed.</summary>
    public const int MaxFireTargets = 64;
    /// <summary>A preview lists at most this many cells (the count is always whole).</summary>
    public const int MaxPreviewCells = 200;
    /// <summary>Materials that burn, in an asset's materials or a creation's parts.</summary>
    private static readonly HashSet<string> FlammableMaterials = new(StringComparer.Ordinal) { "wood", "bark", "foliage", "fabric", "paper", "plastic", "leaf" };

    public NatureState Nature { get; private set; } = new();
    /// <summary>
    /// A new island's wallet under its rules (the review's finding 2, decision 26): the pack's traits.wallet.start, today's
    /// materials for a pack without one. Nature as it begins holds it, and the room counts as untouched while it still does.
    /// </summary>
    public IReadOnlyDictionary<string, int> StartWallet => NatureState.FreshWallet(Rules?.Traits?.Wallet);
    public NatureWeather NatureWeather => Nature.Weather;
    public string NatureNotice { get; private set; } = "";
    /// <summary>Whether this room runs its nature (RoomWorld turns it on); the saved block is loaded and kept either way.</summary>
    public bool NatureConfigured { get; private set; }
    /// <summary>Raised when what the ground field shows changed: a command, or a simulation step that changed anything.</summary>
    public event Action? NatureChanged;
    /// <summary>The simulation is saved at most this often while something is active (and at once when a fire ends, and on exit).</summary>
    public double NatureSaveIntervalS { get; set; } = 5.0;
    /// <summary>Whether room.undo can take back the latest nature change now (the HUD's Undo): only in Creative, only a step recorded there.</summary>
    public bool NatureCanUndo => Mode == "creative" && Nature.UndoTarget(Revision) != null;
    /// <summary>Test seam: the ground the simulation finds at a cell, in place of the room's physics.</summary>
    internal Func<int, int, NatureCell?>? NatureSampleForTests { get; set; }

    /// <summary>A Fire preview the host made: whose, for exactly which cast, at which room revision, until when.</summary>
    private sealed record NaturePreview(string Principal, string Signature, int Revision, DateTime Expires);
    /// <summary>One thing a fire could reach (internal: the whole set, whoever asks), what the rule does to it, and whether it asks first.</summary>
    private sealed record FireTarget(string Id, string Kind, FireOutcome Outcome, bool AskFirst, Aabb Bounds);
    /// <summary>True while the player's "no" commits a held fire: the creations it asked about are spared and stop the burn.</summary>
    private bool _sparing;

    private bool _natureUnavailable;
    private double _natureElapsed;
    private double _natureSaveClock;
    /// <summary>Something but the clock changed since the last save.</summary>
    private bool _natureDirty;
    /// <summary>The weather clock moved since the last save (saved on exit, never on its own by the throttle).</summary>
    private bool _natureClockMoved;
    /// <summary>The bounds of fireproof things (locks, Creative's creations), refreshed while a fire burns.</summary>
    private Aabb[] _natureShelter = Array.Empty<Aabb>();
    private readonly Dictionary<string, NaturePreview> _naturePreviews = new(StringComparer.Ordinal);
    private readonly Queue<string> _natureLightQueue = new();
    /// <summary>What the scene shows of nature now (burned and scorched ids, with their nodes): applied only when it changes.</summary>
    private string _natureSceneKey = "";
    private readonly HashSet<string> _natureShown = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, uint> _natureLayers = new();
    private readonly Dictionary<ulong, Material?> _natureMaterials = new();
    /// <summary>Each tree-climbing collision shape's own faces, as the room built them, by its CollisionShape3D.</summary>
    private readonly Dictionary<ulong, Vector3[]> _climbFaces = new();
    private StandardMaterial3D? _natureScorchMaterial;

    // ---- loading and configuring ----

    /// <summary>Turns this room's nature on (RoomWorld does): the simulation runs and the nature commands are accepted.</summary>
    public void ConfigureNature()
    {
        NatureConfigured = true;
        LoadNature();
    }

    /// <summary>Nature as a new island begins: nothing on the ground, the wallet the island's start.</summary>
    private NatureState NewNature() => new() { Wallet = NatureState.FreshWallet(Rules?.Traits?.Wallet) };

    /// <summary>
    /// Nature from the authority's save (already judged by CheckNature when the save loaded): the scene follows it. A
    /// sidecar nature.json from earlier test builds is never read. Without a loaded save, nature waits with a notice.
    /// </summary>
    private void LoadNature()
    {
        ForgetPlantFuel();
        _companionFireSeenS = double.NegativeInfinity;
        ForgetFireKinds();
        _naturePreviews.Clear();
        _natureLightQueue.Clear();
        _natureDirty = false;
        _natureClockMoved = false;
        if (Authority == null || !Authority.Call(KernelNames.IsReady).AsBool())
        {
            Nature = NewNature();
            _natureUnavailable = true;
            NatureNotice = "The room's saved state is not loaded, so its nature waits for it.";
            ApplyNatureObjects();
            return;
        }
        var saved = KernelJson.ToJson(Authority.Call(KernelNames.Nature)) as JsonObject;
        if (saved == null || saved.Count == 0) Nature = NewNature();
        else if (NatureBlock.Problem(saved, Revision, Room) is { } problem)
        {
            Nature = NewNature();
            _natureUnavailable = true;
            NatureNotice = "The saved nature is invalid (" + problem + "); it is kept as it was.";
            ApplyNatureObjects();
            return;
        }
        else Nature = NatureBlock.Unpack(saved);
        // Bloom's flowers went with decision 52: an older island's are dropped now, and saved so with the next save.
        if (Nature.DropFlowers()) _natureDirty = true;
        // A creation removed by an earlier build left its record behind: it is dropped (and saved so with the next change).
        foreach (var gone in Nature.Objects.Keys.Where(id => id.StartsWith("creation:", StringComparison.Ordinal) && EntityRevision(id) < 0).ToList())
            Nature.Objects.Remove(gone);
        _natureUnavailable = false;
        NatureNotice = "";
        ApplyNatureObjects();
        NatureChanged?.Invoke();
    }

    /// <summary>The authority's nature_check: a save's nature block is believed only if NatureBlock.Problem finds nothing wrong.</summary>
    public Godot.Collections.Dictionary CheckNature(Godot.Collections.Dictionary nature, int revision)
    {
        var problem = NatureBlock.Problem(KernelJson.ToJson(nature), revision, Room);
        return problem == null ? new() { ["ok"] = true } : new() { ["ok"] = false, ["message"] = "The saved nature is invalid: " + problem + "; the save was not loaded and is kept as it was." };
    }

    private void RequireNature()
    {
        if (!NatureConfigured) throw new Refusal("not_ready", "This room's nature is not running.", retryable: true);
        if (_natureUnavailable) throw new Refusal("not_ready", NatureNotice, retryable: true);
    }

    // ---- what the player's controls send ----

    public JsonObject PlayerNaturePreview(string capability, Vector3 point, float? radius = null, float? duration = null) =>
        PlayerCommand("nature.cast", NatureCastArgs(capability, point, radius, duration), preview: true);

    public JsonObject PlayerNatureCast(string capability, Vector3 point, float? radius = null, float? duration = null, string? previewId = null)
    {
        var args = NatureCastArgs(capability, point, radius, duration);
        if (previewId != null) args["preview_id"] = previewId;
        return PlayerCommand("nature.cast", args);
    }

    public JsonObject PlayerNatureRepair(string target) => PlayerCommand("nature.repair", new JsonObject { ["target"] = target });

    /// <summary>room.undo back to before the latest nature change (refused, as the contract says, when there is none).</summary>
    public JsonObject PlayerNatureUndo() =>
        PlayerCommand("room.undo", new JsonObject { ["to_revision"] = Nature.UndoTarget(Revision) ?? Math.Max(0, Revision - 1) }, expectedRevision: Revision);

    /// <summary>A cast with the pack's own default radius and duration where the control names none.</summary>
    private JsonObject NatureCastArgs(string capability, Vector3 point, float? radius, float? duration)
    {
        var ability = Rules?.Ability(capability);
        return new JsonObject
        {
            ["capability"] = capability, ["center_m"] = KernelJson.Vector(point),
            ["radius_m"] = radius ?? (float)(ability?.AreaRadiusDefaultM ?? 0.25), ["duration_s"] = duration ?? (float)(ability?.DurationDefaultS ?? 30),
        };
    }

    // ---- the commands ----

    /// <summary>The shape of the nature commands' args (the contract's): anything malformed is request_invalid, as for every op.</summary>
    private static void CheckNatureArgs(string op, JsonElement args)
    {
        var allowed = op switch
        {
            "nature.cast" => new[] { "capability", "center_m", "radius_m", "duration_s", "preview_id", "target", "end_m" },
            "nature.trail" => new[] { "enabled" },
            "nature.repair" => new[] { "target" },
            _ => Array.Empty<string>(),
        };
        foreach (var property in args.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new Refusal("field_unknown", "This argument is not part of the operation.", "$.args." + KernelJson.DisplayText(property.Name, 64));
        var required = op switch
        {
            "nature.cast" => new[] { "capability", "center_m", "radius_m", "duration_s" },
            "nature.trail" => new[] { "enabled" },
            "nature.repair" => new[] { "target" },
            _ => Array.Empty<string>(),
        };
        foreach (var name in required)
            if (!args.TryGetProperty(name, out _)) throw new Refusal("request_invalid", $"This operation needs '{name}'.", "$.args." + name);
        if (op == "nature.cast")
        {
            if (Str(args, "capability") is not ("grow" or "fire" or "bubble_carry" or "bubble_boat" or "bubble_lift"))
                throw new Refusal("request_invalid", "capability is grow or fire.", "$.args.capability");
            // The targets rule (the mechanics round): only the Bubbles carry and lift envelop an avatar, and such a cast is its
            // preview or names it (CheckEnvelope); the cast itself is the Bubble's (Kernel/Bubble/CommandHostBubble).
            if (args.TryGetProperty("target", out var envelops) && Str(args, "capability") is not ("bubble_carry" or "bubble_lift"))
                throw new Refusal("request_invalid", "Only the Bubble's carry and lift name a target.", "$.args.capability");
            if (envelops.ValueKind != JsonValueKind.Undefined && (envelops.ValueKind != JsonValueKind.String || !AvatarId.IsMatch(envelops.GetString()!)))
                throw new Refusal("request_invalid", "A Bubble's target is an avatar.", "$.args.target");
            CheckVector(args.GetProperty("center_m"), "$.args.center_m");
            // Fireline's drag (decision 78): an end point, Fire's alone (CommandHostFireKinds).
            if (args.TryGetProperty("end_m", out var end))
            {
                if (Str(args, "capability") != "fire") throw new Refusal("request_invalid", "Only Fire names an end_m: the end of a Fireline.", "$.args.end_m");
                CheckVector(end, "$.args.end_m");
            }
            // The contract's outer bounds; the island's narrower ones are the rules pack's (invalid_args when exceeded).
            foreach (var (name, max) in new[] { ("radius_m", NatureBlock.MaxFireRadiusM), ("duration_s", 600f) })
            {
                var value = args.GetProperty(name);
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0 || number > max)
                    throw new Refusal("request_invalid", $"{name} is a number above 0 and at most {max}.", "$.args." + name);
            }
            if (args.TryGetProperty("preview_id", out var id) && (id.ValueKind != JsonValueKind.String || !PreviewIdPattern.IsMatch(id.GetString()!)))
                throw new Refusal("request_invalid", "preview_id is the id a preview of this host returned.", "$.args.preview_id");
        }
        if (op == "nature.trail" && args.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new Refusal("request_invalid", "enabled is true or false.", "$.args.enabled");
        if (op == "nature.repair" && (Str(args, "target") is not { } target || !CreationId.IsMatch(target)))
            throw new Refusal("request_invalid", "Repair names one creation.", "$.args.target");
    }

    private static readonly System.Text.RegularExpressions.Regex PreviewIdPattern = new(@"\A[a-f0-9]{32}\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>nature.cast and nature.repair (nature.trail is refused): each checked, previewed or held, then committed as one room revision.</summary>
    private JsonObject NatureCommand(JsonElement root, string principal, string fingerprint, string? approvedBy)
    {
        var op = Op(root);
        var action = Str(root, "action_id")!;
        var args = root.GetProperty("args");
        var preview = root.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.True;
        // The Bubbles expertise change no ground and are saved nowhere: a bubble lives for the session, as an effect does.
        if (op == "nature.cast" && IsBubbleCapability(Str(args, "capability"))) return BubbleCast(root, principal, fingerprint, approvedBy);
        // The flower trail went with Bloom (decision 52): its args are still checked (CheckNatureArgs), and it changes nothing.
        if (op == "nature.trail") throw new Refusal("unsupported_capability", TrailRetired, "$.op");
        RequireNature();
        // What every change needs, before a preview, a hold or a commit, and again when the player's click commits a held one.
        RequireChange(principal);
        if (op == "nature.cast")
        {
            var capability = Str(args, "capability")!;
            var ability = Rules?.Ability(capability) ?? throw new Refusal("unsupported_capability", "This island does not grant that nature ability.", "$.args.capability");
            // Decision 72: no Fire until the first rest, whoever asks, before every other check of it (CommandHostCamps.RequireFireLearned).
            if (capability == "fire") RequireFireLearned();
            // Decision 77: the place decides what a Fire cast is (CommandHostFireKinds.FireKindOf): a drag Fireline, water a
            // Will-o'-wisp, something burning Snuff (each once learned), else Fire (an unlit firepit lit on its path, CastFire).
            var kind = capability == "fire" ? FireKindOf(args) : FireKind.Fire;
            // The Gubble's fire guardrails (the player's setting, one fire of its own, the wait after the last), before anything else.
            // Snuff and the wisp light nothing: neither is the Gubble's one fire, and both are allowed while its wait runs.
            if (capability == "fire" && principal != PlayerPrincipal && kind is FireKind.Fire or FireKind.Fireline) CheckCompanionFire();
            var cast = CheckCast(ability, args, principal, oneFire: kind is FireKind.Fire or FireKind.Fireline);
            if (capability != "fire") return CastGrow(root, ability, cast, principal, action, fingerprint, preview, approvedBy);
            return kind switch
            {
                FireKind.Snuff => CastSnuff(root, ability, cast, principal, action, fingerprint, preview, approvedBy),
                FireKind.Wisp => CastWisp(root, ability, cast, principal, action, fingerprint, preview, approvedBy),
                FireKind.Fireline => CastFire(root, ability, cast, principal, action, fingerprint, preview, approvedBy, CheckFireline(ability, args, cast, principal)),
                _ => CastFire(root, ability, cast, principal, action, fingerprint, preview, approvedBy),
            };
        }
        return Repair(root, Str(args, "target")!, principal, action, fingerprint, preview, approvedBy);
    }

    /// <summary>The words nature.trail is refused with (the companion's mock says the same).</summary>
    public const string TrailRetired = "The flower trail went with Bloom: Grow grows the island's own plants and leaves no trail.";

    /// <summary>A cast's place and size, checked against the island's rules for this caster: the pack's limits, never the code's.</summary>
    private readonly record struct Cast(Vector3 Center, float Radius, float Duration, float Intensity);

    private Cast CheckCast(IslandAbility ability, JsonElement args, string principal, bool oneFire = true)
    {
        var center = KernelJson.ReadVector(args.GetProperty("center_m"));
        var radius = (float)args.GetProperty("radius_m").GetDouble();
        var duration = (float)args.GetProperty("duration_s").GetDouble();
        if (!ability.CastBy.Contains(principal == PlayerPrincipal ? "player" : "companion"))
            throw new Refusal("permission_denied", "This island does not allow this caster to use that ability.", "$.args.capability");
        if (radius > ability.AreaRadiusMaxM)
            throw new Refusal("invalid_args", "That radius is more than this island allows.", "$.args.radius_m", allowed: JsonValue.Create(ability.AreaRadiusMaxM));
        if (duration > ability.DurationMaxS)
            throw new Refusal("invalid_args", "That duration is more than this island allows.", "$.args.duration_s", allowed: JsonValue.Create(ability.DurationMaxS));
        if (!Room.Bounds.Grow(0.02f).HasPoint(center)) throw new Refusal("out_of_bounds", "Choose a point inside this island.", "$.args.center_m");
        if (Companion == null || Companion.GlobalPosition.DistanceTo(center) > ability.ReachM)
            throw new Refusal("out_of_bounds", "Bring the Gubble closer before casting.", "$.args.center_m", retryable: true);
        if (!TeamSeesSpot(center, principal)) throw new Refusal("target_not_found", "The team must see the place the Gubble will affect.", "$.args.center_m");
        // One fire on the island at a time, whoever lit it: since decision 82 nothing but burning out or Snuff ends a fire, so the player's
        // own Fire no longer puts the Gubble's out (Snuff aimed at it is no fire, and passes: FireKindOf, oneFire false).
        if (oneFire && Nature.Fire != null) throw new Refusal("target_busy", FireBusyWords(principal), retryable: true);
        var intensity = ability.Params.TryGetValue("intensity", out var setting) ? (float)setting.Default : 0.6f;
        return new Cast(center, radius, duration, intensity);
    }

    /// <summary>
    /// A Fire cast: previewed first (the preview names the things it could reach and the avatars inside its area, as far as
    /// the caster's team has perceived them), then committed as exactly that preview. The fire rule decides each thing; the
    /// only ask left is Creative's "may your builds burn?", asked once (FireHoldReason): a yes scorches them, a no lights the
    /// fire with them spared (Deny commits it with _sparing). A keyed_yes pack's ask for the Gubble's fire is the same one
    /// hold, and its no lights nothing.
    /// </summary>
    private JsonObject CastFire(JsonElement root, IslandAbility ability, Cast cast, string principal, string action, string fingerprint, bool preview, string? approvedBy,
        Vector3? lineEnd = null)
    {
        // Fire's camp: a cast that reaches an unlit firepit lights that firepit and nothing else (CommandHostCamps.LightFirepit).
        if (lineEnd == null && UnlitFirepitUnder(cast.Center, cast.Radius) is { } firepit)
            return LightFirepit(root, ability, cast.Center, cast.Radius, cast.Duration, firepit, principal, action, fingerprint, preview, approvedBy);
        // Fireline (decision 78): the cells along the drag catch at once; the fire's circle is round the line's middle, wide enough for
        // the whole line (within Fire's radius), and the spread keeps inside it as any fire's does.
        var (areaCenter, areaRadius) = lineEnd is { } lineTo ? FirelineArea(cast.Center, lineTo, ability) : (cast.Center, cast.Radius);
        var area = cast with { Center = areaCenter, Radius = areaRadius };
        // The plants in its circle are fuel (CommandHostGrow): their ground cells read it while this cast is checked and lit.
        EnsurePlantFuel(area.Center, area.Radius);
        // Snuffed ground stays damp and cannot catch (decision 79), whatever samples it.
        NatureCell? Sample(int x, int z) => SampleNatureCell(x, z, lineEnd is { } to ? LineHeightAt(cast.Center, to, x, z) : cast.Center.Y) is { } cell && !InSnuffZone(cell) ? cell : null;
        var targets = FireTargets(area.Center, area.Radius);
        if (targets.Count > MaxFireTargets) throw new Refusal("invalid_args", "That fire could touch too many things. Choose a smaller area.", "$.args.radius_m");
        var burnable = targets.Where(t => t.Outcome != FireOutcome.Fireproof).ToList();
        var companion = principal != PlayerPrincipal;
        var keyed = companion && ability.Tier == "keyed_yes";
        var asked = burnable.Where(t => t.Outcome == FireOutcome.Scorches && t.AskFirst).ToList();
        var spare = _sparing && !keyed;
        var mustAsk = (asked.Count > 0 && !spare) || keyed;
        // Only these creations may be scorched, each at the revision it has now: a creation built or revised since never
        // inherits them, and every other creation stops the burn (RefreshNatureShelter), as a spared one does.
        var creations = burnable.Where(t => t.Outcome == FireOutcome.Scorches && !(spare && t.AskFirst)).ToDictionary(t => t.Id, t => EntityRevision(t.Id), StringComparer.Ordinal);
        RefreshNatureShelter(creations);
        // A Fireline's cells are the ones along it; a fire's, the dry fuel in its circle.
        var lineCells = lineEnd is { } along ? LineCells(cast.Center, along) : null;
        var eligible = (lineCells ?? NatureState.Disk(cast.Center, cast.Radius).ToList()).Select(g => Sample(g.X, g.Z))
            .Where(c => c != null && c.Fuel > 0 && c.Moisture < NatureLaws.IgnitionMaxMoisture).Select(c => c!).Take(lineCells != null ? NatureState.MaxBurning : int.MaxValue).ToList();
        if (eligible.Count == 0) throw new Refusal("invalid_args", lineCells != null ? "There is no dry fuel along that line." : "There is no dry fuel at that spot.", "$.args.center_m");
        var signed = new JsonObject
        {
            ["capability"] = "fire", ["center_m"] = KernelJson.Vector(cast.Center), ["radius_m"] = KernelJson.Num(area.Radius), ["duration_s"] = KernelJson.Num(cast.Duration),
            ["intensity"] = KernelJson.Num(cast.Intensity), ["mode"] = Mode,
            ["targets"] = new JsonArray(targets.Select(t => (JsonNode?)new JsonObject { ["id"] = t.Id, ["outcome"] = t.Outcome.ToString() }).ToArray()),
        };
        if (lineEnd is { } signedEnd) signed["end_m"] = KernelJson.Vector(signedEnd);
        var signature = CanonicalJson.Sha256Hex(CanonicalJson.Bytes(signed));
        var knows = TeamKnowsFor(principal);
        var data = CastData(cast with { Radius = area.Radius }, eligible, principal);
        if (lineEnd is { } dataEnd)
        {
            data["kind"] = FireKindWords.Fireline;
            data["end_m"] = KernelJson.Vector(dataEnd);
        }
        else if (preview) data["kind"] = FireKindWords.Fire;
        data["affected_ids"] = new JsonArray(burnable.Where(t => knows(t.Id)).Select(t => (JsonNode?)t.Id).ToArray());
        data["max_burning_cells"] = NatureState.MaxBurning;
        data["requires_approval"] = mustAsk;
        if (preview)
        {
            // Who stands where it could burn, as far as the caster perceives them: feedback, never a hold.
            data["avatars_in_area"] = AvatarsInArea(area, principal);
            foreach (var stale in _naturePreviews.Where(p => p.Value.Expires <= Clock()).Select(p => p.Key).ToList()) _naturePreviews.Remove(stale);
            while (_naturePreviews.Count >= 32) _naturePreviews.Remove(_naturePreviews.Keys.First());
            // 128 bits from a cryptographic generator, as every host-made handle (contracts/README.md "Identifiers").
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _naturePreviews[token] = new NaturePreview(principal, signature, Revision, Clock() + ApprovalLifetime);
            data["preview_id"] = token;
            return NaturePreviewed("nature.cast", principal, action, data, burnable.Where(t => knows(t.Id)).Select(t => t.Id));
        }
        var previewId = root.GetProperty("args").TryGetProperty("preview_id", out var given) ? given.GetString() : null;
        if (previewId == null || !_naturePreviews.TryGetValue(previewId, out var planned) || planned.Principal != principal || planned.Signature != signature ||
            planned.Expires <= Clock() || planned.Revision != Revision)
            throw new Refusal("revision_conflict", "Preview this fire again before casting: the place, what it would reach or its permission has changed.", "$.args.preview_id", retryable: true);
        // One ask at most: the click commits this command with approvedBy (no second hold), or the player's no commits it
        // sparing what was asked about (_sparing); the answer is never asked again.
        if (mustAsk && approvedBy == null)
            return Hold(root, principal, action, fingerprint, "nature.cast", FireHoldReason(companion, asked.Count > 0, keyed),
                asked.ToDictionary(t => t.Id, t => EntityRevision(t.Id), StringComparer.Ordinal), bindRevision: true, sparesOnDeny: asked.Count > 0 && !keyed);
        var candidate = Nature.Copy();
        // One fire, one undo step (decision 78: a Fireline too, taken back whole in Creative).
        var step = candidate.BeginUndo(lineCells != null ? "Fireline" : "Fire", Revision + 1, principal, Mode);
        var lit = lineCells != null
            ? candidate.IgniteCells(eligible.Select(c => (c.X, c.Z)), area.Center, area.Radius, cast.Duration, cast.Intensity, Revision + 1, creations, Sample, step, principal, approvedBy ?? principal)
            : candidate.Ignite(cast.Center, cast.Radius, cast.Duration, cast.Intensity, Revision + 1, creations, Sample, step, principal, approvedBy ?? principal);
        if (lit == 0) throw new Refusal("invalid_args", "Nothing can take hold at the center of that area.", "$.args.center_m");
        // The guard at commit (CommandHostEnergy): after the preview, the hold and every check, the fire taking hold among them (an
        // empty pool is never the reason given for a fire that could not start anyway); charged once the revision is saved. A
        // Fireline is priced as its kind (the pack's cost block, as the Bubble's kinds are).
        var price = GuardEnergy(lineCells != null ? FireKindWords.Fireline : ability.Capability, principal);
        _naturePreviews.Remove(previewId);
        data.Remove("cells");
        data.Remove("cells_truncated");
        data.Remove("affected_ids");
        data["started_cells"] = eligible.Where(TeamSeesCellFor(principal)).Count(c => candidate.Cells.TryGetValue(c.Key, out var cell) && cell.BurningS > 0);
        var committed = CommitNature(candidate, "nature.cast", principal, action, fingerprint, approvedBy, burnable.Where(t => knows(t.Id)).Select(t => t.Id), data, undo: false);
        ChargeEnergy(price);
        // The shared rules (CommandHostFireKinds): a committed Fire cast counts toward the hints, and three along a line teach Fireline.
        RecordFireCast(lineCells != null ? Array.Empty<string>() : NoteFireForFireline(cast.Center, Revision));
        return committed;
    }

    /// <summary>
    /// The one ask's plain words: what it would do, never which or how many things (names are untrusted, and the companion
    /// reads it too). Creative's question is about the builds: approve lets them burn, deny lights the fire and spares them.
    /// </summary>
    private static string FireHoldReason(bool companion, bool builds, bool keyed)
    {
        var whose = companion ? "The Gubble's fire" : "Your fire";
        if (builds && !keyed)
            return $"{whose} would reach your builds. Is it OK if they burn? Approve: they burn and stay repairable, free in Creative. Deny: the fire stops at them.";
        if (builds) return "The Gubble asks to light a fire here, and it would reach your builds. Approve: it is lit and they may burn, repairable free in Creative. Deny: no fire.";
        return "The Gubble asks to light a fire here. Approve this exact area or deny it.";
    }

    /// <summary>
    /// The player's words while the Gubble's fire burns (decision 82: X no longer puts it out): one fire at a time, so it burns out first,
    /// or, once Snuff is learned, Fire aimed at it snuffs it. The HUD shows the host's words as they are.
    /// </summary>
    public const string GubbleFireBurningWords = "The Gubble's fire is burning: one fire at a time. Let it burn out first.";
    public const string GubbleFireBurningSnuffWords = "The Gubble's fire is burning: one fire at a time. Let it burn out, or aim Fire at it to Snuff it.";

    /// <summary>Why another nature cast must wait for the fire that burns now, in words for whoever asks.</summary>
    private string FireBusyWords(string principal) => Nature.Fire?.Principal switch
    {
        CompanionPrincipal when principal != PlayerPrincipal => "Your fire is still burning: one fire at a time. Wait for it to go out.",
        CompanionPrincipal => SnuffLearned ? GubbleFireBurningSnuffWords : GubbleFireBurningWords,
        _ => "Let the current fire finish first.",
    };

    /// <summary>The avatars standing where a fire could burn them (within its radius and the reach of burning ground), as the caster perceives them.</summary>
    private JsonArray AvatarsInArea(Cast cast, string principal)
    {
        var inside = new JsonArray();
        foreach (var (body, id) in new (SmallPlayerController?, string)[] { (Player, PlayerAvatar), (Companion, CompanionAvatarId) })
        {
            if (body == null || !IsInstanceValid(body)) continue;
            var at = body.GlobalPosition;
            if (new Vector2(at.X - cast.Center.X, at.Z - cast.Center.Z).Length() > cast.Radius + NatureLaws.PlayerBurnM || Math.Abs(at.Y - cast.Center.Y) > 0.5f) continue;
            if (Perceives(principal, id)) inside.Add(id);
        }
        return inside;
    }

    /// <summary>A cast's data: its place and size, and the cells it would touch as far as the requester's team has perceived them.</summary>
    private JsonObject CastData(Cast cast, List<NatureCell> eligible, string principal)
    {
        var seen = eligible.Where(TeamSeesCellFor(principal)).ToList();
        return new JsonObject
        {
            ["center_m"] = KernelJson.Vector(cast.Center), ["radius_m"] = KernelJson.Num(cast.Radius), ["duration_s"] = KernelJson.Num(cast.Duration),
            ["cell_count"] = seen.Count,
            ["cells"] = new JsonArray(seen.Take(MaxPreviewCells).Select(c => (JsonNode?)KernelJson.Vector(c.Position)).ToArray()),
            ["cells_truncated"] = seen.Count > MaxPreviewCells,
        };
    }

    /// <summary>A preview's answer: its data read back as strict JSON, as a durable receipt's is (any reader's number type works).</summary>
    private JsonObject NaturePreviewed(string op, string principal, string action, JsonObject data, IEnumerable<string> affected)
    {
        var result = Previewed(op, principal, action, affected.ToArray());
        result["data"] = Normalized(data);
        return result;
    }

    /// <summary>A scorched creation repaired from its hash-pinned design at its original material cost (free in Creative).</summary>
    private JsonObject Repair(JsonElement root, string id, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        var instance = Instance(id) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        if (Authority.Call(KernelNames.IsLocked, id).AsBool()) throw new Refusal("target_protected", "Only the player can unlock this protected creation.", "$.args.target");
        if (!Nature.Objects.TryGetValue(id, out var item) || !item.Creation || item.Burn <= 0) throw new Refusal("invalid_args", "That creation does not need repair.", "$.args.target");
        if (item.SourceHash != CanonicalJson.Sha256Hex(CanonicalJson.Bytes(KernelJson.ToJson(instance["source"]))))
            throw new Refusal("revision_conflict", "This creation's design changed; its old repair quote no longer applies.", "$.args.target");
        if (Nature.Fire != null) throw new Refusal("target_busy", FireBusyWords(principal), retryable: true);
        var free = Mode == "creative";
        if (!free && item.RepairCost.Any(c => Nature.Wallet.GetValueOrDefault(c.Key) < c.Value))
            throw new Refusal("invalid_args", "The team's material supply cannot cover this repair yet.", "$.args.target");
        var data = new JsonObject { ["cost"] = free ? new JsonObject() : new JsonObject(item.RepairCost.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))) };
        if (preview) return NaturePreviewed("nature.repair", principal, action, data, new[] { id });
        if (principal != PlayerPrincipal && instance["owner_id"].AsString() == PlayerPrincipal && approvedBy == null)
            return Hold(root, principal, action, fingerprint, "nature.repair", $"The Gubble asks to repair {id}, which you made, using the team's materials.",
                new() { [id] = EntityRevision(id) }, bindRevision: true);
        var candidate = Nature.Copy();
        var step = candidate.BeginUndo("Repair", Revision + 1, principal, Mode);
        candidate.RememberObject(step, id);
        if (!free) foreach (var (material, units) in item.RepairCost) candidate.Wallet[material] -= units;
        candidate.Objects.Remove(id);
        return CommitNature(candidate, "nature.repair", principal, action, fingerprint, approvedBy, new[] { id }, data, undo: false);
    }

    /// <summary>
    /// room.undo (contracts/README.md: it names the room revision): takes the room back to to_revision when every change
    /// after it is a nature change the host can still take back (its latest steps, up to NatureState.MaxUndoSteps). A
    /// newer change of another kind (building, moving, protecting) cannot be undone yet. The undo is itself a room
    /// revision, and never fails for a full receipt ledger. A companion's undo of anyone else's change waits for the player.
    /// Only in Creative (Unsupported refuses it in Challenge and Live), and only steps recorded in Creative.
    /// </summary>
    private JsonObject NatureUndo(JsonElement root, string principal, string fingerprint, string? approvedBy)
    {
        var action = Str(root, "action_id")!;
        var preview = root.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.True;
        RequireNature();
        RequireChange(principal);
        var named = root.GetProperty("args").GetProperty("to_revision").GetInt64();
        if (named >= Revision) throw new Refusal("invalid_args", "Undo names an earlier room revision.", "$.args.to_revision", actual: JsonValue.Create(Revision));
        var target = (int)named;
        var steps = Nature.StepsBackTo(target, Revision);
        if (steps == null)
        {
            if (Nature.LatestSealed(Revision))
                throw new Refusal("unsupported_capability", "That change happened in Challenge or Live, where what happens stays: it cannot be undone.", "$.args.to_revision");
            if (Nature.UndoTarget(Revision) is not { } reachable)
                throw new Refusal("unsupported_capability", "The latest change is not one the island can undo yet: only nature changes (Grow, Fire, repairs) can be undone.", "$.args.to_revision");
            throw new Refusal("invalid_args", "That revision is not just before a nature change the island can still undo.", "$.args.to_revision", allowed: JsonValue.Create(reachable));
        }
        var objects = steps.SelectMany(s => s.Objects.Keys).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (objects.Any(id => Authority.Call(KernelNames.IsLocked, id).AsBool()))
            throw new Refusal("target_protected", "Unlock the protected thing before undoing what fire did to it.", "$.args.to_revision");
        var needsApproval = principal != PlayerPrincipal && steps.Any(s => s.Principal != principal);
        var knows = TeamKnowsFor(principal);
        var data = new JsonObject
        {
            ["undone"] = string.Join(", ", steps.Select(s => s.Label)), ["undone_steps"] = steps.Count, ["requires_approval"] = needsApproval,
        };
        if (preview) return NaturePreviewed("room.undo", principal, action, data, objects.Where(knows));
        if (needsApproval && approvedBy == null)
            return Hold(root, principal, action, fingerprint, "room.undo", "The Gubble asks to undo your latest nature change.",
                objects.Where(id => EntityRevision(id) >= 0).ToDictionary(id => id, EntityRevision, StringComparer.Ordinal), bindRevision: true);
        var candidate = Nature.Copy();
        candidate.UndoSteps(candidate.StepsBackTo(target, Revision)!, Revision + 1, target);
        return CommitNature(candidate, "room.undo", principal, action, fingerprint, approvedBy, objects.Where(knows), data, undo: true);
    }

    /// <summary>
    /// One durable nature change: the candidate's block, the receipt (what it affected, its data) and the room's next
    /// revision in one authority write. Only once that is on disk does the candidate become the room's nature. The first
    /// answer is rebuilt from the durable receipt, exactly as every replay will be.
    /// </summary>
    private JsonObject CommitNature(NatureState candidate, string op, string principal, string action, string fingerprint, string? approvedBy,
        IEnumerable<string> affected, JsonObject data, bool undo)
    {
        candidate.ChangedAt = Revision + 1;
        candidate.TrimUndo();
        var meta = new Godot.Collections.Dictionary { ["fingerprint"] = fingerprint, ["op"] = op, ["at_utc"] = Now() };
        // The player's own held change (a fire near a creation) needed the player's click; it is no one else's approval.
        if (approvedBy != null && principal != PlayerPrincipal) meta["approved_by"] = approvedBy;
        var request = new Godot.Collections.Dictionary
        {
            ["op"] = "nature", ["action_id"] = action, ["expected_revision"] = Revision, ["expected_permission_revision"] = PermissionRevision,
            ["nature"] = KernelJson.ToVariant(NatureBlock.Pack(candidate)), ["affected"] = new Godot.Collections.Array(affected.Select(id => (Variant)id)),
            ["data"] = KernelJson.ToVariant(data), ["undo"] = undo,
        };
        var outcome = Authority.Call(KernelNames.Submit, principal, request, meta).AsGodotDictionary();
        if (!outcome["ok"].AsBool()) throw Translate(outcome);
        PublishNature(candidate);
        _natureDirty = false;
        _natureClockMoved = false;
        _natureSaveClock = 0;
        return Durable(Authority.Call(KernelNames.ReceiptFor, principal, action).AsGodotDictionary(), replayed: false);
    }

    /// <summary>A saved nature becomes the room's: the scene follows, and the look hears of it.</summary>
    private void PublishNature(NatureState state)
    {
        Nature = state;
        NatureNotice = "";
        SeeCompanionFire();
        if (LookHook != null) LookHook.Wind = new Vector2(NatureWeather.WindMps.X, NatureWeather.WindMps.Z);
        ApplyNatureObjects();
        NatureChanged?.Invoke();
    }

    // ---- what a companion learns ----

    /// <summary>Whether the requester's team knows a thing: the player knows everything; a companion what either avatar sees now or the team's map holds.</summary>
    private Func<string, bool> TeamKnowsFor(string principal)
    {
        if (principal == PlayerPrincipal) return _ => true;
        var known = new HashSet<string>(Perception(principal), StringComparer.Ordinal);
        known.UnionWith(TeamMemory().Entries.Keys);
        return known.Contains;
    }

    /// <summary>
    /// Whether the requester's team has perceived a cell: for the player, every cell; for a companion, a cell in discovered
    /// space on its surface's level, or near an avatar's eyes on a surface with no level yet (within the radius the sweep
    /// discovers space in), or on a thing the team knows.
    /// </summary>
    private Func<NatureCell, bool> TeamSeesCellFor(string principal)
    {
        if (principal == PlayerPrincipal) return _ => true;
        var knows = TeamKnowsFor(principal);
        var eyes = new SmallPlayerController?[] { Player, Companion }.Where(b => b != null && IsInstanceValid(b) && b.IsInsideTree()).Select(b => b!.EyeCamera.GlobalPosition).ToList();
        return cell =>
        {
            if (!cell.Surface.StartsWith("shell:", StringComparison.Ordinal)) return knows(cell.Surface);
            if (_levels.ContainsKey(cell.Surface)) return DiscoveredAt(cell.Surface, cell.Position.X, cell.Position.Z);
            return eyes.Any(e => new Vector2(e.X - cell.Position.X, e.Z - cell.Position.Z).Length() <= DiscoverRadiusM);
        };
    }

    /// <summary>nature.describe: the island's nature as the requester's team knows it.</summary>
    private JsonObject NatureSnapshot(string principal)
    {
        var seesCell = TeamSeesCellFor(principal);
        var knows = TeamKnowsFor(principal);
        return new JsonObject
        {
            ["nature_revision"] = Nature.ChangedAt, ["trail_enabled"] = false,
            ["weather"] = new JsonObject { ["pattern"] = NatureWeather.Pattern, ["rain"] = NatureWeather.Rain01, ["wind_mps"] = KernelJson.Vector(NatureWeather.WindMps) },
            ["planted_cells"] = Nature.Cells.Values.Count(c => c.Growth > 0 && seesCell(c)),
            ["burning_cells"] = Nature.Cells.Values.Count(c => c.BurningS > 0 && seesCell(c)),
            ["wallet"] = new JsonObject(Nature.Wallet.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["scorched"] = new JsonArray(Nature.Objects.Values.Where(o => o.Creation && o.Burn > 0 && knows(o.Id)).Select(o => o.Id).OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode?)id).ToArray()),
            ["can_undo"] = NatureCanUndo,
            // An empty notice stays empty (the contract: "empty otherwise"): DisplayText answers "Refused." for empty text.
            ["notice"] = NatureNotice.Length == 0 ? "" : KernelJson.DisplayText(NatureNotice, 200),
        };
    }

    // ---- what fire left blocks the verbs ----

    /// <summary>Whether a thing is out of use because of fire: a stock object burned down, or a creation awaiting repair.</summary>
    public bool IsNatureUnavailable(string id) => Nature.Objects.TryGetValue(id, out var item) && (item.Creation ? item.Burn > 0 : item.BurnedDown);

    private bool IsBurnedDown(string id) => Nature.Objects.TryGetValue(id, out var item) && item.BurnedDown;

    /// <summary>
    /// What fire left blocks the verbs that change or use a thing, wherever a command names it (target, targets,
    /// placement.on): a stock object that burned down is gone, as if it were not in the room; a scorched creation can be
    /// repaired or removed, nothing else, until it is repaired. Goals and effects change no thing (a goal re-checks its
    /// target on arrival), so they are judged elsewhere.
    /// </summary>
    private void CheckNatureRemains(string op, JsonElement args)
    {
        if (op is "goal.set" or "goal.stop" or "effect.start" or "effect.stop" or "effect.adjust" or "nature.repair") return;
        foreach (var (id, path) in Named(op, args))
        {
            if (!Nature.Objects.TryGetValue(id, out var item)) continue;
            if (item.BurnedDown) throw new Refusal("target_not_found", "That is not in this room.", path);
            if (item.Creation && item.Burn > 0 && !(op == "entity.remove" && path == "$.args.target"))
                throw new Refusal("target_busy", "This creation is scorched; repair it before using, changing, protecting or building on it.", path);
        }
    }

    /// <summary>A removal's nature: the creation's scorch record goes with it, in the same save (null when it has none).</summary>
    private NatureState? NatureWithout(string id)
    {
        if (!Nature.Objects.ContainsKey(id)) return null;
        var cleared = Nature.Copy();
        cleared.Objects.Remove(id);
        return cleared;
    }

    // ---- the fire rule ----

    /// <summary>What the fire rule says for an entity summary now: locked, else a creation, else one of the island's own objects.</summary>
    private FireRuling FireRulingOf(JsonObject entity)
    {
        var kind = entity["protected"]?.GetValue<bool>() == true ? "locked" : entity["kind"]!.GetValue<string>() == "creation" ? "creation" : "stock";
        return FireRule.TryGetValue((kind, Mode), out var ruling) ? ruling : FireRule[(kind, "creative")];
    }

    /// <summary>
    /// Every object and creation a fire at center could reach, whoever asks (the protection and approval checks need them
    /// all): what does not burn is left out, a held thing is in hands, and each carries the rule's outcome for it now.
    /// </summary>
    private List<FireTarget> FireTargets(Vector3 center, float radius) =>
        Entities().Where(e => e["kind"]!.GetValue<string>() is "creation" or "object" && e["held_by"] == null && BoundsOf(e).Grow(radius).HasPoint(center) &&
                Flammable(e["id"]!.GetValue<string>()))
            .Select(e => (Entity: e, Ruling: FireRulingOf(e)))
            .Select(p => new FireTarget(p.Entity["id"]!.GetValue<string>(), p.Entity["kind"]!.GetValue<string>(), p.Ruling.Outcome, p.Ruling.AskFirst, BoundsOf(p.Entity)))
            .ToList();

    private bool Flammable(string id)
    {
        if (id.StartsWith("obj:", StringComparison.Ordinal))
            return Room.Objects.FirstOrDefault(o => o.Id == id)?.Asset.Materials.Any(m => FlammableMaterials.Contains(m.Role)) == true;
        var source = Instance(id)?["source"].AsGodotDictionary();
        return source != null && source.ContainsKey("parts") && source["parts"].AsGodotArray().Any(p => FlammableMaterials.Contains(p.AsGodotDictionary()["material"].AsString()));
    }

    /// <summary>
    /// The bounds whose ground does not burn: what the rule makes fireproof (locks), and every creation the fire may not
    /// scorch (spared by the player's no, or built or revised since it was lit), so the burn stops at it. authorized is the
    /// fire about to be lit; otherwise the fire that burns.
    /// </summary>
    private void RefreshNatureShelter(Dictionary<string, int>? authorized = null)
    {
        var allowed = authorized ?? Nature.Fire?.AuthorizedCreations ?? new Dictionary<string, int>();
        _natureShelter = Entities().Where(e => e["kind"]!.GetValue<string>() is "creation" or "object" && (FireRulingOf(e).Outcome == FireOutcome.Fireproof ||
                e["kind"]!.GetValue<string>() == "creation" && !(allowed.TryGetValue(e["id"]!.GetValue<string>(), out var revision) && EntityRevision(e["id"]!.GetValue<string>()) == revision)))
            .Select(BoundsOf).ToArray();
    }

    /// <summary>Sheltered ground, and the ground where the player recovered while the fire grace lasts.</summary>
    private bool NatureFireproof(NatureCell cell) =>
        _natureShelter.Any(b => b.Grow(NatureLaws.ShelterMarginM).HasPoint(cell.Position + Vector3.Up * 0.02f)) || InSnuffZone(cell) ||
        (_fireGraceS > 0 && _recoveredAt is { } safe && new Vector2(cell.Position.X - safe.X, cell.Position.Z - safe.Z).Length() <= NatureLaws.RecoverySafeM);

    /// <summary>The player recovered from an injury here: the burning ground around is put out at once (it stays out during the grace).</summary>
    private void MakeRecoverySafe(Vector3 at)
    {
        if (!NatureConfigured || _natureUnavailable || !Nature.PutOutNear(at, NatureLaws.RecoverySafeM)) return;
        _natureDirty = true;
        NatureChanged?.Invoke();
    }

    // ---- the simulation ----

    /// <summary>
    /// The simulation's tick, from _PhysicsProcess (its ray queries belong there, as the sight sweep's do): whole steps of
    /// NatureLaws.StepS with the remainder kept, at most NatureLaws.MaxCatchUpSteps a tick (time beyond them is dropped,
    /// so a stall or a reload never catches up), a bounded batch of light probes, and the throttled save: at most every
    /// NatureSaveIntervalS while something changes, at once when a fire ends. Tests call it directly.
    /// </summary>
    public void AdvanceNature(double delta)
    {
        if (!NatureConfigured || _natureUnavailable || !double.IsFinite(delta) || delta <= 0) return;
        if (Authority == null || !Authority.Call(KernelNames.IsReady).AsBool()) return;
        _natureElapsed += delta;
        _natureSaveClock += delta;
        var steps = 0;
        var fireEnded = false;
        while (_natureElapsed >= NatureLaws.StepS && steps < NatureLaws.MaxCatchUpSteps)
        {
            _natureElapsed -= NatureLaws.StepS;
            steps++;
            fireEnded |= StepNature();
        }
        _natureElapsed = Math.Min(_natureElapsed, NatureLaws.StepS);
        ProbeNatureLight();
        if (_natureDirty && (fireEnded || _natureSaveClock >= NatureSaveIntervalS)) SaveNature();
    }

    /// <summary>
    /// One step: weather, ground and fire, objects and plants catching, the player near the flames (hurt in Challenge and
    /// Live whoever lit the fire; the Gubble hears of its own fire's harm). Returns whether a fire ended.
    /// </summary>
    private bool StepNature()
    {
        _fireGraceS = Math.Max(0, _fireGraceS - NatureLaws.StepS);
        var hadFire = Nature.Fire != null;
        if (hadFire) RefreshNatureShelter();
        // Undo exists only in Creative: a fire that burns on while the mode is not Creative seals its step for good.
        if (Mode != "creative" && Nature.Fire is { } sealing && Nature.Undo.LastOrDefault(s => s.Revision == sealing.Revision) is { Undoable: true } open)
        {
            open.Mode = Mode;
            _natureDirty = true;
        }
        var eyes = new SmallPlayerController?[] { Player, Companion }.Where(b => b != null && IsInstanceValid(b)).Select(b => b!.GlobalPosition).ToList();
        var fire = Nature.Fire;
        bool Active(NatureCell cell) =>
            eyes.Any(e => new Vector2(e.X - cell.Position.X, e.Z - cell.Position.Z).Length() <= NatureLaws.ActiveRadiusM) ||
            (fire != null && new Vector2(fire.Center.X - cell.Position.X, fire.Center.Z - cell.Position.Z).Length() <= fire.RadiusM);
        NatureCell? Sample(int x, int z) => SampleNatureCell(x, z, Nature.Cells.TryGetValue(NatureState.Key(x, z), out var cell) ? cell.Y : fire?.Center.Y);
        // The plants in the fire's circle are fuel (CommandHostGrow): their ground cells read it while the fire burns.
        if (fire != null) EnsurePlantFuel(fire.Center, fire.RadiusM);
        var changed = Nature.Step(NatureLaws.StepS, Sample, NatureFireproof, Active);
        changed |= BurnNatureObjects(NatureLaws.StepS);
        changed |= BurnPlants(NatureLaws.StepS);
        if (Player != null && IsInstanceValid(Player))
        {
            var at = Player.GlobalPosition;
            var inCells = Nature.Cells.Values.Any(c => c.BurningS > 0 && c.Position.DistanceTo(at) < NatureLaws.PlayerBurnM);
            // Decision 85: a lit campfire hurts a player standing in its flames as any fire does, at the same quarter a second (one burn a
            // step however many fires reach them); the resting spot beside it is safe (CampLaws.FlamesM). A campfire is the player's camp,
            // no fire of the Gubble's: its harm is no consequence of the Gubble's, unless its lead carried the player into it (NoteLeadHarm).
            var inCampfire = !inCells && CampfireFlamesAt(at) != null;
            if (inCells || inCampfire)
            {
                var source = Nature.Fire ?? fire;
                if (BurnPlayer(NatureLaws.StepS) is { } harm)
                {
                    if (inCells && source != null)
                    {
                        NoteFireHarm(source, harm, at);
                        MarkFireHarm(source);
                    }
                    else if (inCampfire) NoteLeadHarm(at);
                }
            }
        }
        SeeCompanionFire();
        // Fire's behaviours (CommandHostFireKinds): Snuff learned by rain on the Gubble's fire or a bubble on a fire, the wisps' time, the damp ground.
        StepFireKinds();
        // Decision 73: rain puts the campfires out (CommandHostCamps.StepCampfireRain).
        StepCampfireRain(NatureLaws.StepS);
        if (LookHook != null) LookHook.Wind = new Vector2(NatureWeather.WindMps.X, NatureWeather.WindMps.Z);
        _natureClockMoved = true;
        var fireEnded = hadFire && Nature.Fire == null;
        if (fireEnded) ForgetPlantFuel();
        if (changed || fireEnded)
        {
            _natureDirty = true;
            NatureChanged?.Invoke();
        }
        return fireEnded;
    }

    /// <summary>
    /// Objects near burning ground catch, as the fire rule says: a stock object burns down (its pose at that moment kept for
    /// a later rebuild), a creation the fire was approved to scorch is scorched (its repair quote taken from its design).
    /// Each change is recorded in the fire's undo step.
    /// </summary>
    private bool BurnNatureObjects(float seconds)
    {
        var fire = Nature.Fire;
        if (fire == null) return false;
        var burning = Nature.Cells.Values.Where(c => c.BurningS > 0).ToList();
        var step = Nature.Undo.LastOrDefault(s => s.Revision == fire.Revision);
        var changed = false;
        foreach (var target in FireTargets(fire.Center, fire.RadiusM).Where(t => t.Outcome != FireOutcome.Fireproof))
        {
            if (target.Outcome == FireOutcome.Scorches && (!fire.AuthorizedCreations.TryGetValue(target.Id, out var revision) || EntityRevision(target.Id) != revision)) continue;
            var catching = target.Bounds.Grow(NatureLaws.ObjectCatchM);
            if (!burning.Any(c => catching.HasPoint(c.Position + Vector3.Up * 0.04f))) continue;
            Nature.RememberObject(step, target.Id);
            if (!Nature.Objects.TryGetValue(target.Id, out var state))
            {
                state = new NatureObject { Id = target.Id, Creation = target.Kind == "creation" };
                if (state.Creation) QuoteRepair(state);
                Nature.Objects[target.Id] = state;
            }
            state.Burn = Math.Min(1, state.Burn + seconds * NatureLaws.ObjectBurnPerIntensityS * fire.Intensity * (1 - Nature.Weather.Rain01));
            // M8: a build the Gubble's fire burns is a consequence fact (CommandHostConsequences.NoteBuildBurnt, once per build a fire).
            if (state.Creation && state.Burn > 0)
            {
                NoteBuildBurnt(fire, target.Id, target.Bounds.GetCenter());
                MarkFireHarm(fire);
            }
            if (state.BurnedDown && state.PositionM == null && Room.Objects.FirstOrDefault(o => o.Id == target.Id) is { } item)
            {
                var (position, rotation) = PoseOf(item, ObjectPoses());
                state.PositionM = position;
                state.Rotation = rotation;
            }
            changed = true;
        }
        if (changed) ApplyNatureObjects();
        return changed;
    }

    /// <summary>A scorched creation's repair quote: one unit per authored part, from the hash-pinned design (never from a request).</summary>
    private void QuoteRepair(NatureObject state)
    {
        var source = KernelJson.ToJson(Instance(state.Id)!["source"])!;
        state.SourceHash = CanonicalJson.Sha256Hex(CanonicalJson.Bytes(source));
        foreach (var part in source["parts"]!.AsArray())
        {
            var material = part!["material"]!.GetValue<string>();
            state.RepairCost[material] = state.RepairCost.GetValueOrDefault(material) + 1;
        }
    }

    /// <summary>A bounded batch of light probes (up to four rays each) per tick, every cell in turn; without a look, the default light.</summary>
    private void ProbeNatureLight()
    {
        if (Nature.Cells.Count == 0) return;
        if (_natureLightQueue.Count == 0) foreach (var key in Nature.Cells.Keys) _natureLightQueue.Enqueue(key);
        for (var probes = 0; probes < NatureLaws.LightProbesPerTick && _natureLightQueue.TryDequeue(out var key);)
        {
            if (!Nature.Cells.TryGetValue(key, out var cell)) continue;
            cell.Light = Math.Clamp(LookHook?.LightLevelAt(cell.Position + Vector3.Up * 0.02f) ?? NatureLaws.DefaultLight, 0, 2);
            probes++;
        }
    }

    /// <summary>
    /// Saves the simulation's nature now, with the team block in the same write when it waits too: the throttle, a fire's
    /// end, and exit. It never moves the room revision. While an earlier save is offered and the player has not answered,
    /// nothing is written (it would make a save of this room and withdraw the offer), as for the team block.
    /// </summary>
    private void SaveNature()
    {
        _natureSaveClock = 0;
        if (!NatureConfigured || _natureUnavailable || Authority == null || !Authority.Call(KernelNames.IsReady).AsBool()) return;
        if (_migrationOffer != null && !Godot.FileAccess.FileExists(SavePath)) return;
        Nature.TrimUndo();
        using var team = _teamDirty ? KernelJson.ToVariant(ExportTeam(_notes)) : default;
        using var nature = KernelJson.ToVariant(NatureBlock.Pack(Nature));
        if (!SavedOk(Authority.Call(KernelNames.SetNature, nature, team))) return;
        _natureDirty = false;
        _natureClockMoved = false;
        _teamDirty = false;
        // One write of the whole save at a time (S12): the team's throttle starts again from this write too.
        _saveClock = 0;
    }

    /// <summary>On exit: whatever changed since the last save, the weather clock included.</summary>
    private void SaveNatureOnExit()
    {
        if (_natureDirty || _natureClockMoved) SaveNature();
    }

    // ---- the ground under a cell ----

    /// <summary>
    /// The ground at a cell, from the room's physics: the first walkable face below the probe (started just above the
    /// intended ground, so a roof or an arbor never replaces the soil below it), its material and whether it is fertile.
    /// Water over it, a steep face or a fireproof place gives nothing.
    /// </summary>
    private NatureCell? SampleNatureCell(int x, int z, float? nearY = null) =>
        WithPlantFuel(NatureSampleForTests != null ? NatureSampleForTests(x, z) : SampleGround(x, z, nearY), x, z);

    /// <summary>The ground alone at a cell, from the room's physics (SampleNatureCell adds the fuel of the plants rooted there).</summary>
    private NatureCell? SampleGround(int x, int z, float? nearY)
    {
        if (!IsInsideTree()) return null;
        var top = Math.Min(Room.Bounds.End.Y - 0.002f, nearY is { } y ? y + 0.1f : Room.Bounds.End.Y - 0.002f);
        var at = new Vector3((x + 0.5f) * NatureState.CellM, top, (z + 0.5f) * NatureState.CellM);
        if (at.X < Room.Bounds.Position.X || at.Z < Room.Bounds.Position.Z || at.X > Room.Bounds.End.X || at.Z > Room.Bounds.End.Z) return null;
        using var query = PhysicsRayQueryParameters3D.Create(at, at with { Y = Room.Bounds.Position.Y - 0.5f }, RoomBuilder.WorldLayer, KernelNames.NoRids);
        query.HitBackFaces = false;
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || hit[KernelNames.HitNormal].AsVector3().Y < 0.819f) return null;
        var point = hit[KernelNames.HitPosition].AsVector3();
        Node? collider;
        using (var answer = hit[KernelNames.HitCollider]) collider = answer.AsGodotObject() as Node;
        int shape;
        using (var index = hit[HitShape]) shape = index.AsInt32();
        var (material, surface) = NatureSurface(collider, shape);
        // A cell names what it lies on: ground nothing names (no entity) is no ground the save could keep, so none.
        if (material == "" || surface == "") return null;
        var fertile = surface.StartsWith("shell:", StringComparison.Ordinal) && material is "grass" or "soil";
        var fuel = material is "grass" or "foliage" ? NatureLaws.GrassFuel : material is "wood" or "bark" ? NatureLaws.WoodFuel : 0;
        if (material is "fabric" or "paper" or "plastic" or "cloth" or "rubber" or "leaf") fuel = NatureLaws.LightFuel;
        if (RoomWater.SurfaceAbove(GetViewport().World3D.DirectSpaceState, point + Vector3.Up * 0.004f, Room.Bounds.Size.Y + 1) is { } water && water > point.Y + 0.005f) return null;
        var cell = new NatureCell
        {
            X = x, Z = z, Y = point.Y, Surface = surface, Material = material, Fertile = fertile, Fuel = fuel,
            Moisture = NatureLaws.FreshMoisture + NatureWeather.Rain01 * NatureLaws.RainMoisture,
        };
        if (NatureFireproof(cell)) return null;
        if (Nature.Cells.TryGetValue(cell.Key, out var current))
        {
            cell.Moisture = current.Moisture;
            cell.Fuel = Math.Max(cell.Fuel, current.Growth * NatureLaws.FlowerFuel);
        }
        return cell;
    }

    /// <summary>A node's text metadata, its Variant freed at once.</summary>
    private static string MetaText(Node node, StringName name)
    {
        using var value = node.GetMeta(name);
        return value.AsString();
    }

    private (string Material, string Id) NatureSurface(Node? collider, int shape = -1)
    {
        for (var node = collider; node != null; node = node.GetParent())
        {
            // A plant copy's collision (a trunk, a stone) names its copy, plant:<set>-<row>, from the shape the ray hit.
            if (node.HasMeta(KernelNames.MaterialRoleMeta) && node.HasMeta(PlantCopiesView.PlantRowsMeta))
                return (MetaText(node, KernelNames.MaterialRoleMeta), PlantCopiesView.EntityAt(node, shape));
            if (node.HasMeta(KernelNames.MaterialRoleMeta)) return (MetaText(node, KernelNames.MaterialRoleMeta), node.HasMeta(KernelNames.EntityIdMeta) ? MetaText(node, KernelNames.EntityIdMeta) : "");
            if (node.HasMeta(KernelNames.EntityIdMeta) && MetaText(node, KernelNames.EntityIdMeta) is { } id && id.StartsWith("obj:", StringComparison.Ordinal))
            {
                var item = Room.Objects.FirstOrDefault(o => o.Id == id);
                var material = item?.Asset.Materials.FirstOrDefault(m => FlammableMaterials.Contains(m.Role))?.Role ?? item?.Asset.Materials.FirstOrDefault()?.Role ?? "";
                return (material, id);
            }
            if (node.GetParent() != Runtime) continue;
            foreach (var pair in Runtime.Get(KernelNames.Assemblies).AsGodotDictionary())
            {
                if (pair.Value.AsGodotObject() != node) continue;
                var creationId = pair.Key.AsString();
                var instance = Instance(creationId);
                if (instance == null) return ("", "");
                var parts = instance["source"].AsGodotDictionary()["parts"].AsGodotArray();
                return (parts.Select(p => p.AsGodotDictionary()["material"].AsString()).FirstOrDefault(m => m is "wood" or "leaf") ?? "stone", creationId);
            }
        }
        return ("", "");
    }

    // ---- the scene, derived ----

    private Node3D? NatureNode(string id)
    {
        if (id.StartsWith("obj:", StringComparison.Ordinal)) return ObjectNode(id);
        if (Runtime == null || !IsInstanceValid(Runtime)) return null;
        return Runtime.Get(KernelNames.Assemblies).AsGodotDictionary().TryGetValue(id, out var assembly) ? assembly.AsGodotObject() as Node3D : null;
    }

    /// <summary>
    /// The scene follows nature, and only when what it shows changes (a burn, a repair, an undo, a load, or the runtime
    /// rebuilding a scorched creation's assembly): a stock object that burned down is hidden and loses its collision and
    /// its trees' climbing collision; a scorched creation is darkened and its triggers are off. A partly burnt object's
    /// collision is never touched, and neither is a held one's: the hands own that (TakeHold sets it to layer 0).
    /// </summary>
    private void ApplyNatureObjects()
    {
        // The plants first: the room's copies need no runtime (CommandHostGrow.ApplyPlants).
        ApplyPlants();
        if (Runtime == null || !IsInstanceValid(Runtime)) return;
        var burned = Nature.Objects.Values.Where(o => o.BurnedDown).Select(o => o.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var scorched = Nature.Objects.Values.Where(o => o.Creation && o.Burn > 0).Select(o => o.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var key = string.Join(",", burned) + "|" + string.Join(",", scorched.Select(id => id + "@" + (NatureNode(id)?.GetInstanceId() ?? 0)));
        if (key == _natureSceneKey) return;
        _natureSceneKey = key;
        var disabled = new Godot.Collections.Dictionary();
        foreach (var id in scorched) disabled[id] = true;
        Runtime.Set(KernelNames.NatureDisabledCreations, disabled);
        var held = _held.Values.Select(h => h.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _natureShown.Concat(burned).Concat(scorched).Distinct(StringComparer.Ordinal).ToList())
        {
            var node = NatureNode(id);
            if (node == null || !IsInstanceValid(node) || held.Contains(id)) continue;
            if (id.StartsWith("obj:", StringComparison.Ordinal))
            {
                var hidden = burned.Contains(id);
                node.Visible = !hidden;
                if (node is CollisionObject3D body) SetNatureCollision(body, hidden);
            }
            else SetScorch(node, scorched.Contains(id));
        }
        _natureShown.Clear();
        _natureShown.UnionWith(burned.Concat(scorched));
        UpdateClimbCollision(burned);
    }

    /// <summary>A burned-down body loses its collision; restored as it was when the burn is undone.</summary>
    private void SetNatureCollision(CollisionObject3D body, bool hidden)
    {
        var key = body.GetInstanceId();
        if (hidden)
        {
            _natureLayers.TryAdd(key, body.CollisionLayer);
            body.CollisionLayer = 0;
        }
        else if (_natureLayers.Remove(key, out var layer)) body.CollisionLayer = layer;
    }

    private void SetScorch(Node node, bool scorch)
    {
        if (node is MeshInstance3D mesh)
        {
            var key = mesh.GetInstanceId();
            _natureMaterials.TryAdd(key, mesh.MaterialOverlay);
            _natureScorchMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.13f, 0.09f, 0.72f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 1 };
            mesh.MaterialOverlay = scorch ? _natureScorchMaterial : _natureMaterials[key];
        }
        foreach (var child in node.GetChildren()) SetScorch(child, scorch);
    }

    /// <summary>
    /// A burnt tree's climbing collision goes with it. The landscape exporter merges every tree's climbing pole and crown
    /// caps into collision-only shell parts (shell:tree_climb_*): each of their faces inside a burned-down object's bounds is
    /// left out, and all come back when nothing there is burned.
    /// </summary>
    private void UpdateClimbCollision(List<string> burned)
    {
        if (GetParent()?.GetNodeOrNull(ShellPath) is not { } shell) return;
        var gone = burned.Select(id => Room.Objects.FirstOrDefault(o => o.Id == id)).Where(o => o != null).Select(o =>
        {
            var record = Nature.Objects[o!.Id];
            var (position, rotation) = record.PositionM is { } at && record.Rotation is { } turn ? (at, turn) : PoseOf(o, ObjectPoses());
            return Sandbox.SandboxPhysics.Bounds(position, rotation, o.Asset.DimensionsM * o.Scale).Grow(0.02f);
        }).ToList();
        foreach (var part in shell.GetChildren().OfType<CollisionObject3D>().Where(n => n.HasMeta("entity_id") && n.GetMeta("entity_id").AsString().StartsWith("shell:tree_climb", StringComparison.Ordinal)))
        foreach (var holder in part.GetChildren().OfType<CollisionShape3D>())
        {
            if (holder.Shape is not ConcavePolygonShape3D shape) continue;
            var id = holder.GetInstanceId();
            if (!_climbFaces.TryGetValue(id, out var faces)) _climbFaces[id] = faces = shape.GetFaces();
            var kept = new List<Vector3>(faces.Length);
            for (var i = 0; i + 2 < faces.Length; i += 3)
            {
                var centroid = holder.GlobalTransform * ((faces[i] + faces[i + 1] + faces[i + 2]) / 3f);
                if (gone.Any(b => b.HasPoint(centroid))) continue;
                kept.Add(faces[i]);
                kept.Add(faces[i + 1]);
                kept.Add(faces[i + 2]);
            }
            if (kept.Count == shape.GetFaces().Length) continue;
            holder.Shape = new ConcavePolygonShape3D { Data = kept.ToArray(), BackfaceCollision = shape.BackfaceCollision };
        }
    }
}
