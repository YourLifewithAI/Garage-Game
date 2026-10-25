using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The island's abilities (Run 2, Glow; docs/runs/RUN-2-GLOW.md section 2). The island's rules pack (IslandRules) says
/// which abilities exist and their bounds; effect.start casts one, carried out by the Gubble, through the one command
/// path, as the player (the HUD's PlayerEffect) or as the companion. The primitives: light.emit (Glow, a real light), and since
/// the Bubbles and Fireworks round particles.float (Bubbles) and particles.burst (Fireworks, a cosmetic burst the look draws,
/// LookDirector.StartFireworks). Since the founder's redesign (decisions 55 to 60) Bubbles is no stream of small bubbles: it casts
/// one player-sized bubble at a spot, the place deciding what it is (Kernel/Bubble/CommandHostBubble, BubbleEffectStart). Glow was
/// the first: a real light that follows the Gubble (targets: its own avatar) or floats at a spot within its reach and in the
/// team's sight.
/// - Bounds: params, radius and duration inside the ability's; omitted params take the pack's defaults.
/// - Who: cast_by names the principals that may cast it; a companion may target only its own avatar, and no one any
///   other entity.
/// - Caps: at most max_active of an ability at once, and MaxActiveEffects in the room whatever the ability, the bubble counted
///   (one at a time: a bubble's cast replaces the one there, so that one does not count against it).
/// - The receipt is transient, with an opaque effect: id (130 random bits); the effect ends at its duration, on
///   effect.stop (one id or all), on goal.stop of the Gubble, when the rules change and when the room unloads.
/// Effects are never saved: they live with the session, like goals. Successful Glow uses also advance the
/// separate single-player growth profile; that profile survives room changes and is not room-undo state.
/// </summary>
public partial class CommandHost
{
    /// <summary>The island's rules, or null when none loaded: then there are no abilities, and effect.start says why.</summary>
    public IslandRules? Rules { get; private set; }
    /// <summary>Why the island has no abilities (the pack did not load), for the player; empty when the rules loaded.</summary>
    public string RulesNotice { get; private set; } = "";
    /// <summary>The look the host lights glows with (RoomWorld's); null in fixtures, where effects run with nothing drawn.</summary>
    public LookDirector? LookHook { get; set; }
    /// <summary>The engine's cap on active effects (MaxActiveEffects). Tests lower it.</summary>
    public int EffectLimit { get; internal set; } = MaxActiveEffects;

    /// <summary>One running effect. In memory only: never in a receipt's durable store, a snapshot or a save.</summary>
    private sealed class ActiveEffect
    {
        public required string Id { get; init; }
        public required string Capability { get; init; }
        /// <summary>The principal that cast it: the player may stop any effect, a companion only its own.</summary>
        public required string Principal { get; init; }
        public bool FollowsCompanion { get; init; }
        public DateTime Expires { get; init; }
        public Vector3 Centre { get; set; }
        public string Variant { get; init; } = "plain";
        public string Touch { get; init; } = "plain";
        public string? BubbleSource { get; init; }
        public HashSet<string> Connections { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The running effects, oldest first.</summary>
    private readonly List<ActiveEffect> _effects = new();

    /// <summary>The ids of the running effects, oldest first (tests, the HUD).</summary>
    public IReadOnlyList<string> ActiveEffectIds
    {
        get
        {
            ExpireEffects();
            return _effects.Select(e => e.Id).ToList();
        }
    }

    /// <summary>RoomWorld's one line: load the island's rules from res://rules/&lt;id&gt;/v&lt;N&gt;.json. A pack that fails leaves no abilities, never a crash.</summary>
    public CommandHost LoadRules(string rulesId, int rulesVersion)
    {
        IslandRules? rules = null;
        var notice = "";
        try { rules = IslandRules.Load(rulesId, rulesVersion); }
        catch (Exception error)
        {
            notice = $"The island's rules ({KernelJson.DisplayText(rulesId ?? "", 64)} v{rulesVersion}) did not load, so the Gubble has no abilities: {error.Message}";
        }
        UseRules(rules, notice);
        return this;
    }

    /// <summary>Use these rules (null: none, with notice saying why). Effects started under the old rules end.</summary>
    public void UseRules(IslandRules? rules, string notice = "")
    {
        EndEffects(_effects.ToList());
        // An island nothing has happened to yet begins with the new rules' wallet (a save that holds play keeps its own).
        var fresh = Nature.Untouched(trailAllowed: true, StartWallet);
        Rules = rules;
        if (fresh) Nature.Wallet = NatureState.FreshWallet(rules?.Traits?.Wallet);
        RulesNotice = rules == null ? (notice.Length > 0 ? notice : "This island has no rules, so the Gubble has no abilities.") : "";
        if (rules == null) GD.Print("COMMAND_HOST " + RulesNotice);
        // The body and the pool play by the pack's traits, cost and modes blocks (today's numbers without them).
        ResolveVitalsRules();
    }

    /// <summary>
    /// The HUD's ability keys: the player's effect.start, carried out by the Gubble, like PlayerGoal. No point is the
    /// Gubble's own glow (targets its avatar); a point is a light there. The area's radius and the duration are the
    /// pack's defaults, and so are the params (left out), except a bubble's time at a spot: the Bubble's own (8 s, a boat
    /// 20 s, decision 112; PlayerBubbleDurationS). Returns the result for the HUD to show.
    /// </summary>
    public JsonObject PlayerEffect(string capability, Vector3? point = null, string variant = "auto", string? touch = null)
    {
        var ability = Rules?.Ability(capability);
        var centre = point ?? (Companion != null && IsInstanceValid(Companion) ? Companion.GlobalPosition : Vector3.Zero);
        var args = new JsonObject
        {
            // An ability the island lacks still goes through the host, which answers why (unsupported_capability).
            ["capability"] = capability, ["params"] = new JsonObject(),
            ["area"] = new JsonObject { ["center_m"] = KernelJson.Vector(centre), ["radius_m"] = ability?.AreaRadiusDefaultM ?? 0.5 },
            ["duration_s"] = ability is { Primitive: "particles.float" } && point is { } spot ? PlayerBubbleDurationS(ability, spot) : ability?.DurationDefaultS ?? 1.0,
        };
        if (point == null) args["targets"] = new JsonArray(CompanionAvatarId);
        if (capability == "glow")
        {
            args["variant"] = variant;
            args["touch"] = touch ?? GlowProgress.SelectedTouch;
        }
        return PlayerCommand("effect.start", args);
    }

    /// <summary>An effect.start checked against the island's rules: what it would start.</summary>
    private sealed record EffectPlan(IslandAbility Ability, bool Self, Vector3 Centre, double RadiusM, double DurationS, SortedDictionary<string, double> Params,
        string Variant = "plain", string Touch = "plain", string? BubbleSource = null);
    private static readonly string[] GlowVariants = { "auto", "plain", "lantern", "torch", "bioluminesce", "skylight", "glowing_bubbles" };
    public const float ConstellationLinkM = 0.45f;
    public const float ConstellationPointSeparationM = 0.025f;

    public IReadOnlyList<(string Id, Vector3 Center, string Variant, bool Self)> ActiveGlowPoints
    {
        get { ExpireEffects(); return _effects.Where(e => e.Capability == "glow").Select(e => (e.Id, e.Centre, e.Variant, e.FollowsCompanion)).ToArray(); }
    }
    public IReadOnlyList<string> GlowConnections(string effect)
    {
        ExpireEffects();
        return _effects.FirstOrDefault(item => item.Id == effect)?.Connections.OrderBy(id => id, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
    }

    /// <summary>
    /// Checks an effect.start against the rules, the caster, the targets, the bounds, the place and the caps, in that
    /// order, and refuses with the contract's codes. Runs before any hold, so a held request is one that can start.
    /// </summary>
    private EffectPlan PlanEffect(JsonElement args, string principal)
    {
        ExpireEffects();
        if (Rules == null)
            throw new Refusal("unsupported_capability", "This island's rules did not load, so it grants no abilities.", "$.args.capability");
        var capability = Str(args, "capability")!;
        if (capability is "grow" or "fire")
            throw new Refusal("unsupported_capability", "Use nature.cast for this ability; preview Fire first.", "$.args.capability");
        var ability = Rules.Ability(capability)
            ?? throw new Refusal("unsupported_capability", "This island has no ability by that name. Call capabilities.list.", "$.args.capability");
        CheckGlowProgressAvailable(capability);
        var role = principal == PlayerPrincipal ? "player" : "companion";
        if (!ability.CastBy.Contains(role))
            throw new Refusal("permission_denied", principal == PlayerPrincipal ? "This island does not let the player cast that." : "This island does not let the companion cast that.", "$.args.capability");
        // Targets: the Gubble's own avatar (self, the light follows it) or none (point). Nothing else, from anyone.
        var targets = args.TryGetProperty("targets", out var named) ? named.EnumerateArray().Select(t => t.GetString()!).ToList() : new List<string>();
        var self = targets.Count > 0;
        if (self && (targets.Count != 1 || targets[0] != CompanionAvatarId))
            throw principal == PlayerPrincipal
                ? new Refusal("invalid_args", "This ability works on the Gubble or at a spot; it cannot be cast on that.", "$.args.targets")
                : new Refusal("permission_denied", "A companion's ability may target only its own avatar.", "$.args.targets");
        if (self && !ability.Targets.Contains("self"))
            throw new Refusal("invalid_args", "This ability cannot be cast on the Gubble itself; name no targets and a spot.", "$.args.targets",
                allowed: new JsonArray(ability.Targets.OrderBy(t => t, StringComparer.Ordinal).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()));
        if (!self && !ability.Targets.Contains("point"))
            throw new Refusal("invalid_args", "This ability works only on the Gubble itself; name its avatar in targets.", "$.args.targets",
                allowed: new JsonArray(ability.Targets.OrderBy(t => t, StringComparer.Ordinal).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()));
        // The Bubble is cast at a spot, like Glow, and the place decides (decision 56): never on the Gubble itself.
        var bubble = ability.Primitive == "particles.float";
        if (self && bubble) throw new Refusal("invalid_args", BubbleAtASpotWords, "$.args.targets");
        // Bounds: every param the ability's, within its range; omitted ones take the defaults.
        var parameters = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in args.GetProperty("params").EnumerateObject())
        {
            var path = "$.args.params." + parameter.Name;
            if (!ability.Params.TryGetValue(parameter.Name, out var range))
                throw new Refusal("invalid_args", "This ability takes no parameter by that name.", path,
                    allowed: new JsonArray(ability.Params.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()));
            if (parameter.Value.ValueKind != JsonValueKind.Number) throw new Refusal("invalid_args", "This parameter is a number.", path);
            var value = CanonicalJson.ReadNumber(parameter.Value);
            if (value < range.Min || value > range.Max)
                throw new Refusal("invalid_args", "This parameter is out of the ability's range.", path, actual: JsonValue.Create(value), allowed: new JsonArray(range.Min, range.Max));
            parameters[parameter.Name] = value;
        }
        foreach (var (name, range) in ability.Params) parameters.TryAdd(name, range.Default);
        var area = args.GetProperty("area");
        var radius = CanonicalJson.ReadNumber(area.GetProperty("radius_m"));
        if (radius > ability.AreaRadiusMaxM)
            throw new Refusal("invalid_args", "The area is larger than the ability allows.", "$.args.area.radius_m", actual: JsonValue.Create(radius), allowed: JsonValue.Create(ability.AreaRadiusMaxM));
        var duration = CanonicalJson.ReadNumber(args.GetProperty("duration_s"));
        if (duration > ability.DurationMaxS)
            throw new Refusal("invalid_args", "That is longer than the ability lasts.", "$.args.duration_s", actual: JsonValue.Create(duration), allowed: JsonValue.Create(ability.DurationMaxS));
        // The Gubble carries every ability out.
        if (Companion == null || !IsInstanceValid(Companion) || !Companion.IsInsideTree())
            throw new Refusal("not_ready", "The Gubble is not in the room.", retryable: true);
        var centre = self ? Companion.GlobalPosition : KernelJson.ReadVector(area.GetProperty("center_m"));
        if (!self)
        {
            // A spot: inside the island (InsideIsland, the one rule for casts, effects and leads), within the Gubble's reach, and in the
            // team's sight now (where the wisp will float).
            if (!InsideIsland(centre))
                throw new Refusal("out_of_bounds", "That spot is outside the room.", "$.args.area.center_m");
            var distance = Companion.GlobalPosition.DistanceTo(centre);
            if (distance > ability.ReachM)
                throw new Refusal("out_of_bounds", "That spot is beyond the Gubble's reach; it must come closer first.", "$.args.area.center_m", retryable: true,
                    actual: JsonValue.Create(Math.Round(distance, 3)), allowed: JsonValue.Create(ability.ReachM));
            if (!TeamSeesSpot(centre, principal))
                throw new Refusal("target_not_found", "Neither of you can see that spot now. Look there first.", "$.args.area.center_m");
        }
        // Caps: the ability's own, then the room's. The bubble is one at a time (a recast replaces it, BubbleLaws.MaxBubbles), and it
        // counts toward the room's cap at any other effect's start.
        var mine = _effects.Count(e => e.Capability == ability.Capability);
        var allowance = capability == "glow" ? Math.Min(ability.MaxActive, GlowProgress.ActiveLightAllowance) : ability.MaxActive;
        if (!bubble && mine >= allowance)
            throw new Refusal("budget_exceeded", "As many of these are running as the island allows; stop one first.", "$.args.capability",
                actual: JsonValue.Create(mine), allowed: JsonValue.Create(allowance));
        var running = _effects.Count + (bubble ? 0 : _bubbles.Count);
        if (running >= EffectLimit)
            throw new Refusal("budget_exceeded", "As many effects are running as the room allows; stop one first.", "$.args.capability",
                actual: JsonValue.Create(running), allowed: JsonValue.Create(EffectLimit));
        var variant = args.TryGetProperty("variant", out var selectedVariant) ? selectedVariant.GetString()! : "auto";
        var touch = args.TryGetProperty("touch", out var selectedTouch) ? selectedTouch.GetString()! : GlowProgress.SelectedTouch;
        string? bubbleSource = null;
        if (capability == "glow")
        {
            variant = ResolveGlowVariant(variant, self, centre);
            if (variant == "bioluminesce" && GlowWaterSurfaceAt(centre) is { } surface)
            {
                centre = new Vector3(centre.X, surface, centre.Z);
                if (Companion.GlobalPosition.DistanceTo(centre) > ability.ReachM)
                    throw new Refusal("out_of_bounds", "The water surface is beyond the Gubble's reach.", "$.args.area.center_m");
                if (!TeamSeesSpot(centre, principal))
                    throw new Refusal("target_not_found", "Neither of you can see the water surface.", "$.args.area.center_m");
            }
            if (variant == "torch")
            {
                parameters["intensity"] = Math.Min(ability.Params["intensity"].Max, parameters["intensity"] * 1.65);
                radius = Math.Min(ability.AreaRadiusMaxM, radius * 1.5);
            }
            if (variant == "skylight") radius = ability.AreaRadiusMaxM;
            if (variant == "glowing_bubbles")
                bubbleSource = _effects.LastOrDefault(e => e.Capability == "bubbles" &&
                    (self ? e.FollowsCompanion : !e.FollowsCompanion && e.Centre.DistanceTo(centre) <= ConstellationLinkM))?.Id
                    ?? throw new Refusal("target_not_found", "Glow needs nearby bubbles to light.", "$.args.variant");
        }
        else
        {
            if (args.TryGetProperty("variant", out _) || args.TryGetProperty("touch", out _))
                throw new Refusal("invalid_args", "Glow touches and variants belong to Glow.", "$.args");
            variant = touch = "plain";
        }
        return new EffectPlan(ability, self, centre, radius, duration, parameters, variant, touch, bubbleSource);
    }

    private string ResolveGlowVariant(string requested, bool self, Vector3 centre)
    {
        if (self)
        {
            if (requested is "auto" or "plain" or "lantern") return "lantern";
            if (requested == "glowing_bubbles") return requested;
            throw new Refusal("invalid_args", "That Glow needs a place on the island.", "$.args.variant");
        }
        if (requested == "lantern") throw new Refusal("invalid_args", "Lantern follows the Gubble.", "$.args.variant");
        var water = GlowWaterSurfaceAt(centre) != null;
        var torch = !water && GlowHighSurface(centre);
        if (requested == "auto") return water ? "bioluminesce" : torch ? "torch" : "plain";
        if (requested == "bioluminesce" && !water)
            throw new Refusal("invalid_args", "This Glow needs a water surface.", "$.args.area.center_m");
        if (requested == "torch" && !torch)
            throw new Refusal("invalid_args", "This Glow needs an elevated surface, tree or building.", "$.args.area.center_m");
        if (requested == "skylight" && !GlowOpenSky(centre))
            throw new Refusal("invalid_args", "The sky above that place is covered.", "$.args.area.center_m");
        return requested;
    }

    private float? GlowWaterSurfaceAt(Vector3 centre)
    {
        var space = GetViewport().World3D.DirectSpaceState;
        var water = RoomWater.At(space, centre, 0.12f, 0.12f);
        if (!water.Wet && Room.Sea != null) water = Room.Sea.OpenWater(space, centre, 0.12f, 0.12f);
        return water.Wet && Math.Abs(water.SurfaceY - centre.Y) <= 0.12f ? water.SurfaceY : null;
    }

    private bool GlowHighSurface(Vector3 centre)
    {
        // Height and a real support ray, never a label such as "tree" supplied by a model.
        using var query = PhysicsRayQueryParameters3D.Create(centre + Vector3.Up * 0.08f, centre + Vector3.Down * 0.16f, RoomBuilder.BodyMask, KernelNames.NoRids);
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return false;
        var ground = Room.Sea?.LevelM ?? Room.Bounds.Position.Y;
        return hit["position"].AsVector3().Y >= ground + 0.2f;
    }

    private bool GlowOpenSky(Vector3 centre)
    {
        using var query = PhysicsRayQueryParameters3D.Create(centre + Vector3.Up * 0.12f, centre + Vector3.Up * 20f, RoomBuilder.WorldLayer, KernelNames.NoRids);
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        return hit.Count == 0;
    }

    /// <summary>
    /// Whether a point effect of this ability could start at a spot now as far as the place goes: within the ability's reach of the
    /// Gubble (by marginM to spare), and in the team's sight for the player. The HUD asks it to send the Gubble closer first; it
    /// loosens nothing, as effect.start makes every check again. Null when the island has no such ability or no Gubble is here.
    /// </summary>
    public (bool InReach, bool InSight)? EffectSpot(string capability, Vector3 spot, float marginM = 0)
    {
        if (Rules?.Ability(capability) is not { } ability || Companion == null || !IsInstanceValid(Companion) || !Companion.IsInsideTree() || !spot.IsFinite())
            return null;
        return (Companion.GlobalPosition.DistanceTo(spot) <= ability.ReachM - Math.Max(0, marginM), TeamSeesSpot(spot, PlayerPrincipal));
    }

    /// <summary>Whether the place a point effect would float is in sight now: of the Gubble's eyes, or the player's (the team's sight).</summary>
    private bool TeamSeesSpot(Vector3 centre, string principal)
    {
        var place = new Aabb(centre - new Vector3(EffectSightHalfWidthM, 0, EffectSightHalfWidthM),
            new Vector3(EffectSightHalfWidthM * 2, GlowLook.WispLiftM, EffectSightHalfWidthM * 2));
        if (Companion != null && IsInstanceValid(Companion) && Companion.IsInsideTree() && SeesBox(Companion, place)) return true;
        return (principal == PlayerPrincipal || SharedSight) && Player != null && IsInstanceValid(Player) && Player.IsInsideTree() && SeesBox(Player, place);
    }

    /// <summary>effect.start, checked by PlanEffect: preview says what would start; otherwise the look lights it (or refuses, and nothing starts).</summary>
    private JsonObject EffectStart(EffectPlan plan, string principal, string actionId, string fingerprint, bool preview, string? approvedBy)
    {
        // The Bubble: one player-sized bubble at the spot, the place deciding what it is (Kernel/Bubble/CommandHostBubble).
        if (plan.Ability.Primitive == "particles.float") return BubbleEffectStart(plan, principal, actionId, fingerprint, preview, approvedBy);
        var look = LookHook != null && IsInstanceValid(LookHook) && LookHook.IsInsideTree() ? LookHook : null;
        if (preview)
        {
            // A preview is honest about the look too: it says now what the start would meet.
            if (look != null && look.EffectCount >= EffectLook.MaxEffects)
                throw new Refusal("budget_exceeded", "The game cannot show another effect right now; stop one first.", "$.args.capability", retryable: true);
            var previewed = Previewed("effect.start", principal, actionId);
            previewed["data"] = EffectData(plan, null, null);
            return previewed;
        }
        // The look's own budget first, as the preview says it: an empty pool is never the reason given for a start that could not
        // be shown anyway (the review's minor).
        if (look != null && look.EffectCount >= EffectLook.MaxEffects)
            throw new Refusal("budget_exceeded", "The game cannot show another effect right now; stop one first.", "$.args.capability", retryable: true);
        // The guard at commit (CommandHostEnergy): the price by principal, after every other check; charged once it starts.
        var price = GuardEnergy(plan.Ability.Capability, principal);
        // 'effect:' and 26 lowercase base32 characters (130 random bits): opaque, like job ids, never a counter.
        var id = "effect:" + System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz234567", 26);
        var intensity = plan.Params.TryGetValue("intensity", out var level) ? level : 1.0;
        // The look draws it first: if it cannot, the command is refused and nothing is left half-started.
        if (look != null && !StartLook(look, plan, id, (float)intensity))
        {
            look.StopEffect(id);
            throw new Refusal("budget_exceeded", "The game cannot show another effect right now; stop one first.", "$.args.capability", retryable: true);
        }
        var expires = Clock() + TimeSpan.FromSeconds(plan.DurationS);
        var nearby = new List<ActiveEffect>();
        if (plan.Ability.Capability == "glow" && !plan.Self && plan.Variant is "plain" or "torch")
            foreach (var point in _effects.Where(e => e.Capability == "glow" && !e.FollowsCompanion && e.Principal == principal && e.Variant is "plain" or "torch"))
                if (point.Centre.DistanceTo(plan.Centre) is >= ConstellationPointSeparationM and <= ConstellationLinkM &&
                    nearby.All(other => other.Centre.DistanceTo(point.Centre) >= ConstellationPointSeparationM)) nearby.Add(point);
        var constellation = nearby.Count >= 2;
        if (plan.Ability.Capability == "glow")
        {
            try { RecordGlowCast(plan.Variant, constellation); }
            catch
            {
                // Still in the same command/frame: no successful effect or receipt has been published.
                look?.StopEffect(id);
                throw;
            }
        }
        var active = new ActiveEffect { Id = id, Capability = plan.Ability.Capability, Principal = principal, FollowsCompanion = plan.Self, Expires = expires,
            Centre = plan.Centre, Variant = plan.Variant, Touch = plan.Touch, BubbleSource = plan.BubbleSource };
        _effects.Add(active);
        if (constellation)
        {
            var group = nearby.Append(active).ToArray();
            foreach (var first in group)
                foreach (var second in group)
                    if (first != second) first.Connections.Add(second.Id);
            DrawGlowConnections();
        }
        if (plan.Ability.Capability == "glow") look?.PlayGlowSound(id);
        ChargeEnergy(price);
        return Transient("effect.start", principal, actionId, fingerprint, new JsonArray(id), EffectData(plan, id, expires), created: new JsonArray(id), approvedBy: approvedBy);
    }

    /// <summary>The look's call for the ability's primitive: Glow lights, Fireworks burst. A primitive the look has no call for draws nothing (the Bubble is no effect the look starts: it shows a bubble, Kernel/Bubble).</summary>
    private bool StartLook(LookDirector look, EffectPlan plan, string id, float intensity)
    {
        var follow = plan.Self ? Companion : null;
        return plan.Ability.Primitive switch
        {
            "light.emit" => look.StartGlowVariant(id, follow, plan.Centre, (float)plan.RadiusM, intensity, plan.Variant, plan.Touch, plan.BubbleSource,
                (float)GlowProgress.SoundVolume, GlowProgress.SoundMuted),
            "particles.burst" => look.StartFireworks(id, follow, plan.Centre, (float)plan.RadiusM, intensity),
            _ => false,
        };
    }

    private JsonObject EffectData(EffectPlan plan, string? id, DateTime? expires)
    {
        var data = new JsonObject();
        if (id != null) data["effect"] = id;
        data["capability"] = plan.Ability.Capability;
        data["category"] = plan.Ability.Category;
        data["target"] = plan.Self ? "self" : "point";
        data["params"] = new JsonObject(plan.Params.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value))));
        data["area"] = new JsonObject { ["center_m"] = KernelJson.Vector(plan.Centre), ["radius_m"] = plan.RadiusM };
        data["duration_s"] = plan.DurationS;
        if (plan.Ability.Capability == "glow") { data["variant"] = plan.Variant; data["touch"] = plan.Touch; }
        if (expires != null) data["expires_utc"] = KernelJson.Utc(expires.Value);
        return data;
    }

    /// <summary>Ends effects (the look's light goes too) and says which.</summary>
    private List<string> EndEffects(IEnumerable<ActiveEffect> ending)
    {
        var ended = new List<string>();
        var batch = ending.ToList();
        var sourceIds = batch.Select(effect => effect.Id).ToHashSet(StringComparer.Ordinal);
        batch.AddRange(_effects.Where(effect => effect.BubbleSource != null && sourceIds.Contains(effect.BubbleSource) && !sourceIds.Contains(effect.Id)));
        foreach (var effect in batch)
        {
            if (!_effects.Remove(effect)) continue;
            if (LookHook != null && IsInstanceValid(LookHook)) LookHook.StopEffect(effect.Id);
            ended.Add(effect.Id);
        }
        if (ended.Count > 0)
        {
            foreach (var effect in _effects) effect.Connections.RemoveWhere(ended.Contains);
            DrawGlowConnections();
        }
        return ended;
    }

    /// <summary>The effects a stop by principal covers: the player's covers every effect, a companion's only its own.</summary>
    private IEnumerable<ActiveEffect> Stoppable(string principal) => _effects.Where(e => principal == PlayerPrincipal || e.Principal == principal);

    /// <summary>effect.stop's glows: one id (an unknown one, or another's, stops nothing) or all that principal may stop.</summary>
    private List<string> StopEffects(string principal, string which)
    {
        ExpireEffects();
        return EndEffects(Stoppable(principal).Where(e => which == "all" || e.Id == which));
    }

    /// <summary>goal.stop's glows: the Gubble carries every effect, so stopping it (or everything) ends the ones the principal may stop.</summary>
    private List<string> StopCompanionEffects(string principal)
    {
        ExpireEffects();
        return EndEffects(Stoppable(principal));
    }

    /// <summary>An effect ends at its duration (the host's clock).</summary>
    private void ExpireEffects()
    {
        if (_effects.Count == 0) return;
        var now = Clock();
        EndEffects(_effects.Where(e => now >= e.Expires));
    }

    /// <summary>The room unloads: every glow ends, the look's included.</summary>
    private void EndAllEffects()
    {
        EndEffects(_effects.ToList());
        if (LookHook != null && IsInstanceValid(LookHook) && !LookHook.IsQueuedForDeletion()) LookHook.StopAllEffects();
    }

    /// <summary>capabilities.list from the island's rules: capability_summary items by capability, filtered by category, paged by cursor.</summary>
    private JsonObject Capabilities(JsonElement args)
    {
        IEnumerable<IslandAbility> abilities = (Rules?.Abilities ?? Array.Empty<IslandAbility>()).OrderBy(a => a.Capability, StringComparer.Ordinal);
        if (args.TryGetProperty("category", out var category)) abilities = abilities.Where(a => a.Category == category.GetString());
        var all = abilities.ToList();
        var start = 0;
        if (args.TryGetProperty("cursor", out var cursor) &&
            (!int.TryParse(cursor.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out start) || start > all.Count))
            throw new Refusal("invalid_args", "That cursor is not from this list.", "$.args.cursor");
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : CapabilitiesDefaultLimit;
        var page = all.Skip(start).Take(limit).ToList();
        var items = page.Select(a =>
        {
            var item = new JsonObject
            {
                ["capability"] = a.Capability, ["category"] = a.Category,
                ["params"] = new JsonObject(a.Params.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonObject { ["min"] = p.Value.Min, ["max"] = p.Value.Max }))),
                ["area_radius_max_m"] = a.AreaRadiusMaxM, ["duration_max_s"] = a.DurationMaxS,
                ["command_op"] = a.Primitive is "growth.seed" or "growth.grow" or "element.ignite" ? "nature.cast" : "effect.start",
                ["max_active"] = a.Primitive == "particles.float" ? Math.Min(EffectiveEffectLimit(a.Capability), BubbleLaws.MaxBubbles) : EffectiveEffectLimit(a.Capability),
                ["reach_m"] = a.ReachM,
            };
            // Decision 72: Fire is listed, locked, until the first rest by the starting campfire teaches it (CommandHostCamps.RequireFireLearned).
            if (a.Primitive == "element.ignite" && !FireLearned) item["locked"] = true;
            return (JsonNode?)item;
        }).ToArray();
        var data = new JsonObject { ["items"] = new JsonArray(items) };
        if (start + page.Count < all.Count) data["next_cursor"] = (start + page.Count).ToString(CultureInfo.InvariantCulture);
        return data;
    }

    public JsonObject PlayerAdjustGlow(string effect, Vector3? center = null, IReadOnlyList<string>? connections = null)
    {
        var args = new JsonObject { ["effect"] = effect };
        if (center is { } point) args["center_m"] = KernelJson.Vector(point);
        if (connections != null) args["connections"] = new JsonArray(connections.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return PlayerCommand("effect.adjust", args);
    }

    private JsonObject EffectAdjust(JsonElement args, string principal, string actionId, string fingerprint, bool preview, string? approvedBy = null)
    {
        ExpireEffects();
        CheckGlowProgressAvailable("glow");
        if (GlowProgress.ActiveLightAllowance < 5)
            throw new Refusal("permission_denied", "Moving constellation points and changing their links comes with the fifth light.");
        var id = args.GetProperty("effect").GetString()!;
        var effect = _effects.FirstOrDefault(e => e.Id == id && e.Capability == "glow")
            ?? throw new Refusal("target_not_found", "That Glow has ended.", "$.args.effect");
        if (effect.Principal != principal)
            throw new Refusal("permission_denied", "A caster can adjust only their own Glow.", "$.args.effect");
        if (effect.FollowsCompanion || effect.Variant is not ("plain" or "torch"))
            throw new Refusal("invalid_args", "Only a free constellation point can be adjusted.", "$.args.effect");
        var centre = args.TryGetProperty("center_m", out var point) ? KernelJson.ReadVector(point) : effect.Centre;
        var ability = Rules?.Ability("glow") ?? throw new Refusal("unsupported_capability", "This island has no Glow.");
        if (Companion == null || !IsInstanceValid(Companion) || !Companion.IsInsideTree()) throw new Refusal("not_ready", "The Gubble is not in the room.");
        if ((Room.Sea == null && !Room.Bounds.Grow(0.001f).HasPoint(centre)) || Companion.GlobalPosition.DistanceTo(centre) > ability.ReachM)
            throw new Refusal("out_of_bounds", "The point must stay within the Gubble's reach.", "$.args.center_m");
        if (!TeamSeesSpot(centre, principal)) throw new Refusal("target_not_found", "Neither of you can see that spot.", "$.args.center_m");
        ResolveGlowVariant(effect.Variant, false, centre);
        var links = args.TryGetProperty("connections", out var connections)
            ? connections.EnumerateArray().Select(node => node.GetString()!).ToArray() : effect.Connections.ToArray();
        foreach (var linkedId in links)
        {
            var linked = _effects.FirstOrDefault(e => e.Id == linkedId && e.Id != id && e.Capability == "glow" && !e.FollowsCompanion && e.Variant is "plain" or "torch")
                ?? throw new Refusal("target_not_found", "A link needs another free Glow point.", "$.args.connections");
            if (linked.Principal != principal) throw new Refusal("permission_denied", "A caster can link only their own Glow points.", "$.args.connections");
            if (linked.Centre.DistanceTo(centre) > ability.ReachM)
                throw new Refusal("out_of_bounds", "Those points are too far apart to connect.", "$.args.connections");
        }
        var data = new JsonObject { ["effect"] = id, ["center_m"] = KernelJson.Vector(centre),
            ["connections"] = new JsonArray(links.Select(link => (JsonNode?)JsonValue.Create(link)).ToArray()) };
        if (preview)
        {
            var result = Previewed("effect.adjust", principal, actionId); result["data"] = data; return result;
        }
        var look = LookHook != null && IsInstanceValid(LookHook) && LookHook.IsInsideTree() ? LookHook : null;
        if (look != null && !look.MoveGlow(id, centre)) throw new Refusal("not_ready", "That Glow cannot be moved right now.");
        effect.Centre = centre;
        foreach (var other in _effects) other.Connections.Remove(id);
        effect.Connections.Clear();
        foreach (var linkedId in links)
        {
            effect.Connections.Add(linkedId);
            _effects.First(e => e.Id == linkedId).Connections.Add(id);
        }
        DrawGlowConnections();
        return Transient("effect.adjust", principal, actionId, fingerprint, new JsonArray(id), data, approvedBy: approvedBy);
    }

    private void DrawGlowConnections()
    {
        if (LookHook == null || !IsInstanceValid(LookHook)) return;
        LookHook.SetGlowConnections(_effects.Where(e => e.Capability == "glow").SelectMany(e => e.Connections
            .Where(other => string.CompareOrdinal(e.Id, other) < 0).Select(other => (e.Id, other))).ToArray());
    }

    private static void CheckEffectAdjustArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new Refusal("request_invalid", "A Glow adjustment is an object.", "$.args");
        foreach (var field in args.EnumerateObject())
            if (field.Name is not ("effect" or "center_m" or "connections"))
                throw new Refusal("field_unknown", "This argument is not part of a Glow adjustment.", "$.args." + KernelJson.DisplayText(field.Name, 64));
        if (!args.TryGetProperty("effect", out var id)) throw new Refusal("request_invalid", "Name a Glow effect.", "$.args.effect");
        if (id.ValueKind != JsonValueKind.String || !EffectId.IsMatch(id.GetString()!))
            throw new Refusal("request_invalid", "Name a Glow effect.", "$.args.effect");
        if (!args.TryGetProperty("center_m", out _) && !args.TryGetProperty("connections", out _))
            throw new Refusal("request_invalid", "Move the point or choose its connections.", "$.args");
        if (args.TryGetProperty("center_m", out var point)) CheckVector(point, "$.args.center_m");
        if (args.TryGetProperty("connections", out var links) && (links.ValueKind != JsonValueKind.Array || links.GetArrayLength() > 4 ||
            links.EnumerateArray().Any(link => link.ValueKind != JsonValueKind.String || !EffectId.IsMatch(link.GetString()!)) ||
            links.EnumerateArray().Select(link => link.GetString()).Distinct(StringComparer.Ordinal).Count() != links.GetArrayLength()))
            throw new Refusal("request_invalid", "Connections are up to four different Glow effects.", "$.args.connections");
    }

    /// <summary>effect.start's arguments as the contract types them (request_invalid otherwise); the ability's bounds come after, in PlanEffect.</summary>
    private static void CheckEffectArgs(JsonElement args)
    {
        var capability = args.GetProperty("capability");
        if (capability.ValueKind != JsonValueKind.String || !Token.IsMatch(capability.GetString()!))
            throw new Refusal("request_invalid", "capability is a lowercase token.", "$.args.capability");
        foreach (var parameter in args.GetProperty("params").EnumerateObject())
        {
            var value = parameter.Value;
            var scalar = value.ValueKind switch
            {
                JsonValueKind.Number => Math.Abs(CanonicalJson.ReadNumber(value)) <= 1000000,
                JsonValueKind.True or JsonValueKind.False => true,
                JsonValueKind.String => KernelText.CodePoints(value.GetString()!).Length <= 64,
                _ => false,
            };
            if (!scalar) throw new Refusal("request_invalid", "Effect parameters are numbers within a million, booleans or short strings.", "$.args.params");
        }
        var area = args.GetProperty("area");
        if (area.ValueKind != JsonValueKind.Object || area.EnumerateObject().Any(p => p.Name is not ("center_m" or "radius_m")) ||
            !area.TryGetProperty("center_m", out var centre) || !area.TryGetProperty("radius_m", out var radius))
            throw new Refusal("request_invalid", "area has center_m and radius_m.", "$.args.area");
        CheckVector(centre, "$.args.area.center_m");
        if (radius.ValueKind != JsonValueKind.Number || CanonicalJson.ReadNumber(radius) is <= 0 or > 10)
            throw new Refusal("request_invalid", "radius_m is more than 0 and at most 10 metres.", "$.args.area.radius_m");
        var duration = args.GetProperty("duration_s");
        if (duration.ValueKind != JsonValueKind.Number || CanonicalJson.ReadNumber(duration) is <= 0 or > 600)
            throw new Refusal("request_invalid", "duration_s is more than 0 and at most 600 seconds.", "$.args.duration_s");
        if (args.TryGetProperty("variant", out var variant) && (variant.ValueKind != JsonValueKind.String || !GlowVariants.Contains(variant.GetString()!, StringComparer.Ordinal)))
            throw new Refusal("request_invalid", "Choose a supported Glow variant.", "$.args.variant");
        if (args.TryGetProperty("touch", out var touch) && (touch.ValueKind != JsonValueKind.String || !GlowProgress.Touches.Contains(touch.GetString()!, StringComparer.Ordinal)))
            throw new Refusal("request_invalid", "Choose a supported Glow touch.", "$.args.touch");
    }
}
