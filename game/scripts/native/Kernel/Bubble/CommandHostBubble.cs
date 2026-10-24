using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The Bubble (docs/game/MECHANICS.md section 5; decisions 14, 31, 42, 43 and the founder's redesign, 55 to 60): one player-sized
/// bubble cast at a spot, like Glow, and the place decides what it does. Never an op that moves a body.
/// - The cast: effect.start of the island's Bubbles (particles.float), the place deciding (BubbleEffectStart): on the player a carry
///   round them, on water a boat, anywhere else a bubble that holds the player only when they walk into it. nature.cast still names
///   an expertise outright (bubble_carry on the player or at a point, bubble_boat on water); bubble_lift is no cast any more: Lift is
///   leading a bubble to a higher spot (CommandHostBubbleLead). The cosmetic stream of small bubbles is gone.
/// - A bubble is a world object with collision (its motion is swept against the world the bodies stand on) and transient: it
///   lives for the session like an effect, has an effect: id (effect.stop names it; goal.stop of the Gubble pops it; goal.set lead
///   leads it), and nothing about it is saved. Its receipt is transient too, and the host writes into it as the bubble lives:
///   carried once it holds the player, ended (popped_by_player, timed_out or popped) once it is gone, so the player can mark it (M8).
/// - One at a time (decision 59): a recast pops the one already there, and there is no pause after a pop. Bubbles count toward the
///   room's effect cap, at a bubble's cast and at any effect's start (PlanEffect).
/// - Consent: a carry on the player's avatar is previewed first (the ghost the player sees); a Gubble's commits at least
///   BubbleLaws.GhostLeadS after it. A bubble at a point never forms round the player, judged where it forms (a point under a
///   surface rests on it): effect.start makes such a spot a carry (the ghost first), nature.cast at a point refuses it; it holds them
///   only once they walk in. A boat holds them once they step aboard, and an empty bubble or boat never moves under or onto them.
/// - The exit: the dive key (the body) or X (the player's goal.stop) pops it at any moment, whoever cast it and whoever leads it;
///   leaving a boat by the dive key or by walking off its rim. Nothing the companion sends can hold a player in.
/// - Out of a bubble is a fall (decision 88, replacing 43's and 60's deflate): a bubble that pops in the air, by the player, its timer,
///   the Gubble or anything else, leaves the body falling from where it is (the body's ride, SmallPlayerController.Bubble.cs), hurt by the
///   landing as by any fall (decision 24), a climbable face within reach grabbed as from a jump. When the Gubble popped it by its own
///   action (BubbleEnd.PoppedByGubble) and the fall hurts the player, that is a consequence fact that costs Trust (decision 96,
///   CommandHostConsequences.NotePopFall); the timer's pop and the player's own never are. In Creative nothing hurts.
/// - Moving: a bubble moves only while the Gubble leads it (goal.set lead, decision 57), like a balloon on a string; it moves the
///   player only while it holds them. A bubble that holds the player rises to its hover; it keeps its height otherwise, rising
///   ground lifting it at most BubbleLaws.DriftStepM: only a lift (a lead to a higher spot, paid at the rise) raises it.
/// - The price: the pack's cost for what the place made of it, by principal, through the guard at commit (CommandHostEnergy): a
///   bubble to walk into is the Bubbles price, a carry on the player bubble_carry's, a boat bubble_boat's; a lift is bubble_lift's,
///   charged when it starts to rise. A preview, a pop and a refusal cost nothing. No expertise gate.
/// - Every number is BubbleLaws'.
/// </summary>
public partial class CommandHost
{
    /// <summary>The Bubbles expertise nature.cast names (the contract's enum; bubble_lift is refused as a cast since decision 58).</summary>
    public static bool IsBubbleCapability(string? capability) => capability is "bubble_carry" or "bubble_boat" or "bubble_lift";

    /// <summary>The words of a refused bubble_lift cast (the mock host's too).</summary>
    public const string LiftIsALead = "Lift is leading a bubble to a higher spot: cast the Bubble, then lead it there (goal.set lead).";
    /// <summary>The words of a Gubble's carry on the player cast before the player could see its ghost (the mock host's too).</summary>
    public const string GhostLeadWords = "The player is still seeing the bubble's ghost; cast it again in a moment.";
    /// <summary>A cast on the player whose ghost is not showing (the mock host's too).</summary>
    public const string PreviewAgainWords = "Preview this bubble again before casting: the place, the player or the time has changed.";
    /// <summary>effect.start's Bubble on the player by the Gubble with no ghost of exactly this cast showing (the mock host's too).</summary>
    public const string GhostFirstWords = "A bubble on the player is previewed first: preview this cast so the player sees its ghost, then cast it again.";
    /// <summary>The Bubble cast on the Gubble itself (the mock host's too).</summary>
    public const string BubbleAtASpotWords = "The Bubble is cast at a spot: on the player, on water or on the ground beside them. Name no targets.";

    /// <summary>The bubbles in the room now (at most one, BubbleLaws.MaxBubbles), oldest first.</summary>
    private readonly List<Bubble> _bubbles = new();
    /// <summary>A ghost preview of a carry on the player: whose, for exactly which cast, when made, until when.</summary>
    private sealed record BubbleGhost(string Principal, string Signature, Vector3 Center, float Radius, DateTime Made, DateTime Expires);
    private readonly Dictionary<string, BubbleGhost> _bubbleGhosts = new(StringComparer.Ordinal);
    /// <summary>A carry that held the player ended: the carry is learned once the body is down (OnBubbleLearned), unless the fall hurt.</summary>
    private bool _carrySetDownPending;
    /// <summary>The fall after that carry's end hurt the player (ObservePlayerFall): it did not set them down safely.</summary>
    private bool _carryFallHurt;
    private Func<SmallPlayerController.BubbleHold?>? _priorBubbleHold;
    private Action<bool>? _priorBubbleLeft;
    private bool _bubblesBound;
    private SphereShape3D? _bubbleShape;
    private PhysicsShapeQueryParameters3D? _bubbleQuery;
    private Godot.Collections.Array<Rid>? _bubbleExclude;
    private static readonly StringName HitPositionKey = "position";
    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>
    /// The expertise seam (wired to M11's store, CommandHostExpertise): a carry ended with the player set down safely, the Bubble was
    /// cast on water, a lift floated the player up to its spot. Unimplemented, its calls compile away.
    /// </summary>
    partial void OnBubbleLearned(string capability);

    /// <summary>Test seam: hears each OnBubbleLearned moment.</summary>
    internal Action<string>? BubbleLearnedForTests { get; set; }

    /// <summary>The bubbles now (tests): id, capability, centre, whether it holds the player.</summary>
    public IReadOnlyList<(string Id, string Capability, Vector3 Center, bool Holding)> ActiveBubbles
    {
        get
        {
            var list = new List<(string, string, Vector3, bool)>(_bubbles.Count);
            foreach (var bubble in _bubbles) list.Add((bubble.Id, bubble.Capability, bubble.Center, bubble.Holding));
            return list;
        }
    }

    /// <summary>Ghost previews showing now.</summary>
    public int BubbleGhostCount => _bubbleGhosts.Count;

    /// <summary>Whether a bubble holds the player now.</summary>
    public bool PlayerInBubble => HoldsPlayer() != null;

    // ---- what the player's controls send (the HUD's Bubbles key is PlayerEffect("bubbles", spot); tests use these too) ----

    public JsonObject PlayerBubblePreview(string capability, Vector3 center, string? target = null, float? radius = null, float? duration = null) =>
        PlayerCommand("nature.cast", BubbleCastArgs(capability, center, target, radius, duration), preview: true);

    public JsonObject PlayerBubbleCast(string capability, Vector3 center, string? target = null, string? previewId = null, float? radius = null, float? duration = null)
    {
        var args = BubbleCastArgs(capability, center, target, radius, duration);
        if (previewId != null) args["preview_id"] = previewId;
        return PlayerCommand("nature.cast", args);
    }

    /// <summary>
    /// The time the Bubbles key names (PlayerEffect), as the nature.cast controls name theirs (BubbleCastArgs): where the place makes a
    /// boat (water away from the player's feet, PlaceTheBubble's rule) the boat's BubbleLaws.BoatDurationDefaultS, anywhere else a
    /// bubble's CarryDurationDefaultS (8 s, decision 95), never past the island's duration_max_s. Before decision 95 the key sent the
    /// pack's duration_default_s (20 s in v5) for every bubble, the boat's too; the pack's default no longer decides a bubble's time.
    /// </summary>
    internal double PlayerBubbleDurationS(IslandAbility ability, Vector3 spot)
    {
        var onFeet = Player != null && IsInstanceValid(Player) && Player.GlobalPosition.DistanceTo(spot) <= BubbleLaws.CarryRadiusDefaultM + BubbleLaws.EnvelopReachM;
        var boat = !onFeet && Room != null && WaterForBoat(spot) != null;
        return Math.Min(boat ? BubbleLaws.BoatDurationDefaultS : BubbleLaws.CarryDurationDefaultS, ability.DurationMaxS);
    }

    /// <summary>The player's own pop, as the dive key gives it (the body calls it; tests too): whether a bubble popped or a boat was left.</summary>
    public bool PlayerPopsBubble()
    {
        var held = HoldsPlayer() != null;
        PlayerLeftBubble(true);
        return held;
    }

    private static JsonObject BubbleCastArgs(string capability, Vector3 center, string? target, float? radius, float? duration)
    {
        var boat = capability == "bubble_boat";
        var args = new JsonObject
        {
            ["capability"] = capability, ["center_m"] = KernelJson.Vector(center),
            ["radius_m"] = KernelJson.Num(radius ?? (boat ? BubbleLaws.BoatRadiusDefaultM : BubbleLaws.CarryRadiusDefaultM)),
            ["duration_s"] = KernelJson.Num(duration ?? (boat ? BubbleLaws.BoatDurationDefaultS : BubbleLaws.CarryDurationDefaultS)),
        };
        if (target != null) args["target"] = target;
        return args;
    }

    // ---- the cast ----

    /// <summary>The island's Bubbles (its particles.float ability), whose caster, reach and bounds every bubble uses.</summary>
    private IslandAbility? BubblesAbility()
    {
        if (Rules == null) return null;
        foreach (var ability in Rules.Abilities)
            if (ability.Primitive == "particles.float") return ability;
        return null;
    }

    /// <summary>The price a bubble pays, by what the place made of it: a bubble to walk into is the plain Bubble.</summary>
    private string BubblePriceKey(BubbleKind kind, string? target) =>
        kind == BubbleKind.Boat ? "bubble_boat" : target != null ? "bubble_carry" : BubblesAbility()?.Capability ?? "bubbles";

    /// <summary>
    /// effect.start of the island's Bubbles, checked by PlanEffect (the rules, the caster, a spot and no targets, the bounds, the
    /// Gubble's reach, the team's sight, the room's cap with the bubble it replaces): one player-sized bubble at the spot, the place
    /// deciding what it is (decision 56). Previewed (a carry on the player shows its ghost) or committed, its receipt transient.
    /// </summary>
    private JsonObject BubbleEffectStart(EffectPlan plan, string principal, string actionId, string fingerprint, bool preview, string? approvedBy)
    {
        var spot = plan.Centre;
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) throw new Refusal("not_ready", "The player's body is not in the room yet.", retryable: true);
        var (kind, target, radius, surface) = PlaceTheBubble(spot, "$.args.area.center_m");
        var duration = (float)plan.DurationS;
        var signature = BubbleSignature("effect.start:" + plan.Ability.Capability, spot, radius, duration, target);
        var data = new JsonObject
        {
            ["capability"] = plan.Ability.Capability, ["category"] = plan.Ability.Category, ["target"] = "point", ["kind"] = KindWord(kind, target),
            ["params"] = new JsonObject(), ["area"] = new JsonObject { ["center_m"] = KernelJson.Vector(spot), ["radius_m"] = KernelJson.Num(radius) },
            ["duration_s"] = plan.DurationS,
        };
        if (preview)
        {
            // The ghost the player sees round them is the consent for a carry on them.
            if (target != null) ShowGhost(principal, signature, Player.GlobalPosition + Vector3.Up * (Player.BodyHeightM * 0.5f), radius);
            var previewed = Previewed("effect.start", principal, actionId);
            previewed["data"] = data;
            return previewed;
        }
        // A carry on the player: the Gubble's waits for its ghost (the player's own key, or the player's click on a held cast, is
        // the consent itself). effect.start names no preview: the ghost of exactly this cast by this caster, still showing.
        string? ghostToken = null;
        if (target != null)
        {
            ghostToken = GhostFor(principal, signature);
            if (principal != PlayerPrincipal && approvedBy == null)
            {
                if (ghostToken == null) throw new Refusal("revision_conflict", GhostFirstWords, "$.args.area.center_m", retryable: true);
                if (Clock() - _bubbleGhosts[ghostToken].Made < TimeSpan.FromSeconds(BubbleLaws.GhostLeadS))
                    throw new Refusal("not_ready", GhostLeadWords, "$.args.area.center_m", retryable: true);
            }
        }
        // The guard at commit (CommandHostEnergy): the price of what the place made, by principal, after every check.
        var price = GuardEnergy(BubblePriceKey(kind, target), principal);
        if (ghostToken != null) DropGhost(ghostToken);
        var bubble = FormBubble(kind == BubbleKind.Boat ? "bubble_boat" : "bubble_carry", kind, "effect.start", principal, actionId, fingerprint, target, spot, radius,
            duration, surface);
        ChargeEnergy(price);
        data["effect"] = bubble.Id;
        data["bubble"] = bubble.Id;
        data["expires_utc"] = KernelJson.Utc(bubble.Expires);
        data["carried"] = bubble.Carried;
        var result = Transient("effect.start", principal, actionId, fingerprint, bubble.Target != null ? new JsonArray(bubble.Id, bubble.Target) : new JsonArray(bubble.Id), data,
            created: new JsonArray(bubble.Id), approvedBy: approvedBy);
        if (kind == BubbleKind.Boat) Learned("bubble_boat");
        return result;
    }

    private static string KindWord(BubbleKind kind, string? target) => kind == BubbleKind.Boat ? "boat" : target != null ? "carry" : "walk_in";

    /// <summary>
    /// Where the Bubble lands decides what it is (decision 56): a spot on the player (their feet within the bubble's radius and
    /// BubbleLaws.EnvelopReachM) is a carry round them; water is a boat, beside them, never under them; anywhere else a bubble that
    /// waits for them to walk in. The walk-in is judged where it forms (WalkInCentre: a spot under a surface rests on it), not where
    /// it was aimed: one that would form round the player (ClearOfThePlayer) is on them, so a carry, the Gubble's shown first as its
    /// ghost (the W2 review's blocker: aimed under the feet of a player on a box, it formed round them on the box top).
    /// </summary>
    private (BubbleKind Kind, string? Target, float Radius, float? Surface) PlaceTheBubble(Vector3 spot, string path)
    {
        var feet = Player!.GlobalPosition;
        if (feet.DistanceTo(spot) <= BubbleLaws.CarryRadiusDefaultM + BubbleLaws.EnvelopReachM) return (BubbleKind.Carry, PlayerAvatar, BubbleLaws.CarryRadiusDefaultM, null);
        if (WaterForBoat(spot) is { } surface)
        {
            if (LevelGap(feet, spot) <= BubbleLaws.BoatRadiusDefaultM + BubbleLaws.StepInClearanceM)
                throw new Refusal("invalid_args", "The player stands there: cast the boat beside them, and they step aboard.", path);
            return (BubbleKind.Boat, null, BubbleLaws.BoatRadiusDefaultM, surface);
        }
        if (!ClearOfThePlayer(WalkInCentre(spot, BubbleLaws.CarryRadiusDefaultM), BubbleLaws.CarryRadiusDefaultM))
            return (BubbleKind.Carry, PlayerAvatar, BubbleLaws.CarryRadiusDefaultM, null);
        return (BubbleKind.Carry, null, BubbleLaws.CarryRadiusDefaultM, null);
    }

    /// <summary>Where a bubble to walk into forms for a spot: resting on what is under it (a spot inside a box rests on its top).</summary>
    private Vector3 WalkInCentre(Vector3 spot, float radius) => ClearOfGround(spot + Vector3.Up * radius, radius);

    /// <summary>
    /// Whether a bubble round this centre is clear of the player's body: its middle farther than the bubble's radius, the step-in
    /// clearance and the body's own radius (the rule a led empty bubble keeps too). Walking in is then the player's own consent.
    /// </summary>
    private bool ClearOfThePlayer(Vector3 centre, float radius)
    {
        var body = Player!;
        var middle = body.GlobalPosition + Vector3.Up * (body.BodyHeightM * 0.5f);
        return middle.DistanceTo(centre) > radius + BubbleLaws.StepInClearanceM + body.BodyRadiusM;
    }

    /// <summary>
    /// nature.cast of a Bubbles expertise (routed here by NatureCommand before nature's own checks: a bubble is no change to the
    /// ground and needs no loaded save). Checked, previewed or committed; a commit forms the bubble and answers its transient receipt.
    /// </summary>
    private JsonObject BubbleCast(JsonElement root, string principal, string fingerprint, string? approvedBy)
    {
        var args = root.GetProperty("args");
        var action = Str(root, "action_id")!;
        var preview = root.TryGetProperty("preview", out var asked) && asked.ValueKind == JsonValueKind.True;
        var capability = Str(args, "capability")!;
        // Lift is no cast since the redesign (decision 58): leading a bubble to a higher spot floats it up there.
        if (capability == "bubble_lift") throw new Refusal("unsupported_capability", LiftIsALead, "$.args.capability");
        var kind = capability == "bubble_boat" ? BubbleKind.Boat : BubbleKind.Carry;
        var target = Str(args, "target");
        var ability = BubblesAbility() ?? throw new Refusal("unsupported_capability", "This island does not grant Bubbles.", "$.args.capability");
        if (!ability.CastBy.Contains(principal == PlayerPrincipal ? "player" : "companion"))
            throw new Refusal("permission_denied", "This island does not allow this caster to use Bubbles.", "$.args.capability");
        var center = KernelJson.ReadVector(args.GetProperty("center_m"));
        var radius = (float)args.GetProperty("radius_m").GetDouble();
        var duration = (float)args.GetProperty("duration_s").GetDouble();
        if (radius > ability.AreaRadiusMaxM)
            throw new Refusal("invalid_args", "That radius is more than this island allows.", "$.args.radius_m", allowed: JsonValue.Create(ability.AreaRadiusMaxM));
        var (least, most) = kind == BubbleKind.Boat ? (BubbleLaws.BoatRadiusMinM, BubbleLaws.BoatRadiusMaxM) : (BubbleLaws.CarryRadiusMinM, BubbleLaws.CarryRadiusMaxM);
        if (radius < least || radius > most)
            throw new Refusal("invalid_args", kind == BubbleKind.Boat ? "A Bubble boat is not that size." : "A bubble that carries the player is not that size.", "$.args.radius_m",
                allowed: new JsonArray(KernelJson.Num(least), KernelJson.Num(most)));
        if (duration > ability.DurationMaxS)
            throw new Refusal("invalid_args", "That duration is more than this island allows.", "$.args.duration_s", allowed: JsonValue.Create(ability.DurationMaxS));
        if (!InsideIsland(center)) throw new Refusal("out_of_bounds", "Choose a point inside this island.", "$.args.center_m");
        if (Companion == null || !IsInstanceValid(Companion) || Companion.GlobalPosition.DistanceTo(center) > ability.ReachM)
            throw new Refusal("out_of_bounds", "Bring the Gubble closer before casting.", "$.args.center_m", retryable: true, allowed: JsonValue.Create(ability.ReachM));
        if (!TeamSeesSpot(center, principal)) throw new Refusal("target_not_found", "The team must see the place the Gubble will affect.", "$.args.center_m");
        // The targets rule's avatar is the player's: the Gubble floats on its own, and any other avatar is not here.
        if (target != null && target != PlayerAvatar)
            throw target == CompanionAvatarId ? new Refusal("invalid_args", "A Bubble carries the player; the Gubble floats on its own.", "$.args.target")
                : new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) throw new Refusal("not_ready", "The player's body is not in the room yet.", retryable: true);
        var feet = Player.GlobalPosition;
        float? surface = null;
        if (target != null)
        {
            if (feet.DistanceTo(center) > radius + BubbleLaws.EnvelopReachM)
                throw new Refusal("invalid_args", "A carry on the player forms round them: center it where they stand.", "$.args.center_m");
        }
        else if (kind == BubbleKind.Boat)
        {
            surface = WaterForBoat(center) ?? throw new Refusal("invalid_args", "A Bubble boat is cast on water.", "$.args.center_m");
            if (LevelGap(feet, center) <= radius + BubbleLaws.StepInClearanceM)
                throw new Refusal("invalid_args", "The player stands there: cast the boat beside them, and they step aboard.", "$.args.center_m");
        }
        // A bubble at a point never forms round the player: walking in is their consent, so it waits beside them. Judged where it
        // forms (a point under a surface rests on it), before the guard, so a refusal costs nothing.
        else if (!ClearOfThePlayer(WalkInCentre(center, radius), radius))
            throw new Refusal("invalid_args", "The player stands there: cast the carry on the player (it is previewed first), or beside them so they can step in.", "$.args.center_m");
        // One bubble at a time: this one replaces the one there, so only the effects count toward the room's cap.
        if (_effects.Count >= EffectLimit)
            throw new Refusal("budget_exceeded", "As many effects are running as the room allows; stop one first.", "$.args.capability", retryable: true, allowed: JsonValue.Create(EffectLimit));
        var keyed = principal != PlayerPrincipal && ability.Tier == "keyed_yes";
        var signature = BubbleSignature(capability, center, radius, duration, target);
        var data = new JsonObject
        {
            ["capability"] = capability, ["center_m"] = KernelJson.Vector(center), ["radius_m"] = KernelJson.Num(radius), ["duration_s"] = KernelJson.Num(duration),
            ["cell_count"] = 0, ["cells"] = new JsonArray(), ["cells_truncated"] = false, ["requires_approval"] = keyed,
        };
        if (target != null) data["target"] = target;
        if (preview)
        {
            // The ghost the player sees round them is the consent; the cast names it.
            if (target != null) data["preview_id"] = ShowGhost(principal, signature, Player.GlobalPosition + Vector3.Up * (Player.BodyHeightM * 0.5f), radius);
            return NaturePreviewed("nature.cast", principal, action, data, target != null ? new[] { target } : Array.Empty<string>());
        }
        var previewId = args.TryGetProperty("preview_id", out var named) ? named.GetString() : null;
        // The player's click on a held cast is the consent itself, whatever became of the ghost while it waited.
        if ((previewId != null || target != null) && approvedBy == null)
        {
            if (previewId == null || !_bubbleGhosts.TryGetValue(previewId, out var ghost) || ghost.Principal != principal || ghost.Signature != signature || ghost.Expires <= Clock())
                throw new Refusal("revision_conflict", PreviewAgainWords, "$.args.preview_id", retryable: true);
            if (principal != PlayerPrincipal && Clock() - ghost.Made < TimeSpan.FromSeconds(BubbleLaws.GhostLeadS))
                throw new Refusal("not_ready", GhostLeadWords, "$.args.preview_id", retryable: true);
        }
        if (keyed && approvedBy == null) return Hold(root, principal, action, fingerprint, "nature.cast", "The Gubble asks to blow a bubble.", new());
        // The guard at commit (CommandHostEnergy): the price by principal, after every check; charged once the bubble exists.
        var price = GuardEnergy(BubblePriceKey(kind, target), principal);
        if (previewId != null) DropGhost(previewId);
        var bubble = FormBubble(capability, kind, "nature.cast", principal, action, fingerprint, target, center, radius, duration, surface);
        ChargeEnergy(price);
        var result = BubbleReceipt(bubble, center, duration, keyed, approvedBy);
        if (kind == BubbleKind.Boat) Learned("bubble_boat");
        return result;
    }

    /// <summary>The exact cast a ghost stands for: the place (as the contract writes it), the size, the time and the avatar.</summary>
    private static string BubbleSignature(string capability, Vector3 center, float radius, float duration, string? target) =>
        CanonicalJson.Sha256Hex(CanonicalJson.Bytes(new JsonObject
        {
            ["capability"] = capability, ["center_m"] = KernelJson.Vector(center), ["radius_m"] = KernelJson.Num(radius), ["duration_s"] = KernelJson.Num(duration),
            ["target"] = target ?? "",
        }));

    /// <summary>
    /// A new bubble: round the player (a carry on them), on the water (a boat), or resting at its point waiting for the walk-in. One at
    /// a time: whatever bubble was there pops first (decision 59), the body it held falling from there (decision 88); a recast of the
    /// Gubble's is its own pop (decision 96).
    /// </summary>
    private Bubble FormBubble(string capability, BubbleKind kind, string op, string principal, string action, string fingerprint, string? target, Vector3 center,
        float radius, float duration, float? surface)
    {
        for (var i = _bubbles.Count - 1; i >= 0; i--) EndBubble(_bubbles[i], principal == CompanionPrincipal ? BubbleEnd.PoppedByGubble : BubbleEnd.Popped);
        // A carry round the player holds them from its cast: a fall still pending from an earlier pop has ended in this bubble.
        if (target != null) ForgetPopFall();
        var body = Player!;
        var sweep = SweepRadius(body);
        var at = kind == BubbleKind.Boat ? new Vector3(center.X, surface!.Value + BubbleLaws.BoatDeckAboveWaterM, center.Z)
            : target != null ? ClearOfGround(body.GlobalPosition + Vector3.Up * (body.BodyHeightM * 0.5f), sweep)
            : WalkInCentre(center, radius);
        var bubble = new Bubble
        {
            // 'effect:' and 26 lowercase base32 characters (130 random bits): opaque, never a counter.
            Id = "effect:" + System.Security.Cryptography.RandomNumberGenerator.GetString(IdAlphabet, 26),
            Capability = capability, Kind = kind, Op = op, Principal = principal, ActionId = action, Fingerprint = fingerprint, Target = target, Radius = radius,
            Expires = Clock() + TimeSpan.FromSeconds(duration), Rules = Rules, Center = at, BaseY = at.Y,
            Holding = target != null, Carried = target != null,
        };
        _bubbles.Add(bubble);
        BindBubbles();
        if (LookHook != null && IsInstanceValid(LookHook)) LookHook.ShowBubble(bubble.Id, at, radius, kind == BubbleKind.Boat, ghost: false);
        return bubble;
    }

    /// <summary>
    /// A committed nature.cast's receipt: transient (nothing is saved), its data the bubble's id, the cast, when its time runs out and
    /// whether it has carried the player. Kept like every transient receipt, and written into as the bubble lives (UpdateBubbleReceipt).
    /// </summary>
    private JsonObject BubbleReceipt(Bubble bubble, Vector3 center, float duration, bool keyed, string? approvedBy)
    {
        var result = Result("nature.cast", bubble.Principal, bubble.ActionId, null, true);
        result["transient"] = true;
        result["affected"] = bubble.Target != null ? new JsonArray(bubble.Id, bubble.Target) : new JsonArray(bubble.Id);
        result["created"] = new JsonArray(bubble.Id);
        if (approvedBy != null) result["approved_by"] = approvedBy;
        var data = new JsonObject
        {
            ["capability"] = bubble.Capability, ["bubble"] = bubble.Id, ["center_m"] = KernelJson.Vector(center), ["radius_m"] = KernelJson.Num(bubble.Radius),
            ["duration_s"] = KernelJson.Num(duration), ["expires_utc"] = KernelJson.Utc(bubble.Expires), ["requires_approval"] = keyed, ["carried"] = bubble.Carried,
        };
        if (bubble.Target != null) data["target"] = bubble.Target;
        result["data"] = data;
        var key = bubble.Principal + "|" + bubble.ActionId;
        if (!_transientOrder.TryGetValue(bubble.Principal, out var order)) _transientOrder[bubble.Principal] = order = new LinkedList<string>();
        if (_transient.ContainsKey(key)) order.Remove(key);
        _transient[key] = (bubble.Fingerprint, (JsonObject)result.DeepClone());
        order.AddLast(key);
        while (order.Count > MaxTransientPerPrincipal)
        {
            _transient.Remove(order.First!.Value);
            order.RemoveFirst();
        }
        return result;
    }

    /// <summary>What happened to a bubble since its cast, written into its receipt (a replay and receipt.lookup answer it so).</summary>
    private void UpdateBubbleReceipt(Bubble bubble, string field, JsonNode value) => UpdateTransient(bubble.Principal, bubble.ActionId, bubble.Fingerprint, field, value);

    /// <summary>A field written into a kept transient receipt's data, if it is still kept and still that command's.</summary>
    private void UpdateTransient(string principal, string actionId, string fingerprint, string field, JsonNode value)
    {
        if (_transient.TryGetValue(principal + "|" + actionId, out var kept) && kept.Fingerprint == fingerprint && kept.Result["data"] is JsonObject data)
            data[field] = value;
    }

    // ---- ghosts (the preview the player sees round them) ----

    private string ShowGhost(string principal, string signature, Vector3 center, float radius)
    {
        PruneGhosts(Clock());
        while (_bubbleGhosts.Count >= BubbleLaws.MaxGhosts)
        {
            string? oldest = null;
            foreach (var key in _bubbleGhosts.Keys) { oldest = key; break; }
            DropGhost(oldest!);
        }
        // 128 bits from a cryptographic generator, as every host-made handle (contracts/README.md "Identifiers").
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _bubbleGhosts[token] = new BubbleGhost(principal, signature, center, radius, Clock(), Clock() + TimeSpan.FromSeconds(BubbleLaws.GhostLifetimeS));
        if (LookHook != null && IsInstanceValid(LookHook)) LookHook.ShowBubble("ghost:" + token, center, radius, boat: false, ghost: true);
        return token;
    }

    /// <summary>The oldest ghost this caster showed for exactly this cast that still shows, or null.</summary>
    private string? GhostFor(string principal, string signature)
    {
        PruneGhosts(Clock());
        foreach (var (token, ghost) in _bubbleGhosts)
            if (ghost.Principal == principal && ghost.Signature == signature) return token;
        return null;
    }

    private void DropGhost(string token)
    {
        if (!_bubbleGhosts.Remove(token)) return;
        if (LookHook != null && IsInstanceValid(LookHook)) LookHook.HideBubble("ghost:" + token);
    }

    /// <summary>The player turned the offer away (the dive key or X while a ghost showed): every ghost round them goes.</summary>
    private bool DropPlayerGhosts()
    {
        if (_bubbleGhosts.Count == 0) return false;
        foreach (var token in new List<string>(_bubbleGhosts.Keys)) DropGhost(token);
        return true;
    }

    private void DropGhostsOf(string principal)
    {
        if (_bubbleGhosts.Count == 0) return;
        foreach (var (token, ghost) in new List<KeyValuePair<string, BubbleGhost>>(_bubbleGhosts))
            if (ghost.Principal == principal) DropGhost(token);
    }

    private void PruneGhosts(DateTime now)
    {
        if (_bubbleGhosts.Count == 0) return;
        List<string>? stale = null;
        foreach (var (token, ghost) in _bubbleGhosts)
            if (ghost.Expires <= now) (stale ??= new List<string>()).Add(token);
        if (stale != null) foreach (var token in stale) DropGhost(token);
    }

    // ---- the exit ----

    /// <summary>The bubble that holds the player now, or null.</summary>
    private Bubble? HoldsPlayer()
    {
        for (var i = 0; i < _bubbles.Count; i++)
            if (_bubbles[i].Holding) return _bubbles[i];
        return null;
    }

    /// <summary>
    /// The body left the bubble that held it (SmallPlayerController.BubbleLeft): by the dive key or a trip home (byKey), which pops a
    /// carry as the player's own pop, even mid-lead, and steps off a boat; or by walking off a boat's rim, which leaves the boat
    /// floating. Either way the body falls if it is in the air (decision 88). The dive key also turns away any ghost round them.
    /// </summary>
    private void PlayerLeftBubble(bool byKey)
    {
        if (byKey) DropPlayerGhosts();
        var held = HoldsPlayer();
        if (held == null) return;
        if (held.Kind == BubbleKind.Boat)
        {
            held.Holding = false;
            held.BoardLatch = true;
            held.RiderLostS = 0;
            return;
        }
        EndBubble(held, byKey ? BubbleEnd.PoppedByPlayer : BubbleEnd.Popped);
    }

    /// <summary>
    /// goal.stop of the Gubble and effect.stop: the bubbles this principal may stop (the player any, X among them; a companion its
    /// own, by its own action: decision 96), one by id or all. The player's stop also turns away every ghost round them. Returns the ids
    /// popped.
    /// </summary>
    private List<string> PopBubbles(string principal, string which)
    {
        var popped = new List<string>();
        var player = principal == PlayerPrincipal;
        if (which == "all")
        {
            if (player) DropPlayerGhosts();
            else DropGhostsOf(principal);
        }
        for (var i = _bubbles.Count - 1; i >= 0; i--)
        {
            var bubble = _bubbles[i];
            if ((!player && bubble.Principal != principal) || (which != "all" && bubble.Id != which)) continue;
            popped.Add(bubble.Id);
            EndBubble(bubble, player ? BubbleEnd.PoppedByPlayer : principal == CompanionPrincipal ? BubbleEnd.PoppedByGubble : BubbleEnd.Popped);
        }
        popped.Reverse();
        return popped;
    }

    /// <summary>
    /// A bubble ends: its receipt says how, a lead taking it somewhere ends with it (it popped before it got there), the look lets it
    /// go, and a body it held falls from there (the body sees no hold; decision 88). A carry the Gubble popped by its own action while
    /// it held the player leaves that fall pending: if its landing hurts, that is the Gubble's consequence (NotePopFall, decision 96).
    /// </summary>
    private void EndBubble(Bubble bubble, BubbleEnd end)
    {
        if (!_bubbles.Remove(bubble)) return;
        if (bubble.Kind == BubbleKind.Carry && bubble.Holding)
        {
            _carrySetDownPending = true;
            _carryFallHurt = false;
            if (end == BubbleEnd.PoppedByGubble) NotePopFallPending();
            else ForgetPopFall();
        }
        bubble.Holding = false;
        UpdateBubbleReceipt(bubble, "ended", Bubble.EndWord(end));
        if (bubble.Lead is { } lead) EndLead(bubble, lead, Bubble.EndWord(end), new Refusal("target_not_found", LeadPoppedWords, "$.args.target"));
        if (LookHook != null && IsInstanceValid(LookHook)) LookHook.HideBubble(bubble.Id);
    }

    /// <summary>The room unloads: every bubble pops (nothing about one is saved) and the body is handed back.</summary>
    private void EndAllBubbles()
    {
        for (var i = _bubbles.Count - 1; i >= 0; i--) EndBubble(_bubbles[i], BubbleEnd.Popped);
        foreach (var token in new List<string>(_bubbleGhosts.Keys)) DropGhost(token);
        UnbindBubbles();
        _bubbleQuery?.Dispose();
        _bubbleQuery = null;
        _bubbleShape?.Dispose();
        _bubbleShape = null;
        if (_bubbleExclude != null) ((Godot.Collections.Array)_bubbleExclude).Dispose();
        _bubbleExclude = null;
    }

    private void Learned(string capability)
    {
        OnBubbleLearned(capability);
        BubbleLearnedForTests?.Invoke(capability);
    }

    // ---- the body ----

    /// <summary>Binds the player's body to its bubbles: where the one that holds it is, and its exit. Restored on exit.</summary>
    private void BindBubbles()
    {
        if (_bubblesBound || Player == null || !IsInstanceValid(Player)) return;
        _priorBubbleHold = Player.BubbleHoldSource;
        _priorBubbleLeft = Player.BubbleLeft;
        Player.BubbleHoldSource = PlayerBubbleHold;
        Player.BubbleLeft = PlayerLeftBubble;
        _bubblesBound = true;
    }

    private void UnbindBubbles()
    {
        if (!_bubblesBound) return;
        _bubblesBound = false;
        if (Player == null || !IsInstanceValid(Player)) return;
        if (Player.BubbleHoldSource == PlayerBubbleHold) Player.BubbleHoldSource = _priorBubbleHold;
        if (Player.BubbleLeft == PlayerLeftBubble) Player.BubbleLeft = _priorBubbleLeft;
    }

    /// <summary>The body's question each tick: the bubble that holds it, if any (no allocation).</summary>
    private SmallPlayerController.BubbleHold? PlayerBubbleHold()
    {
        var held = HoldsPlayer();
        return held == null ? null : new SmallPlayerController.BubbleHold(held.Center, held.Radius, held.Velocity, held.Kind == BubbleKind.Boat);
    }

    // ---- the tick ----

    /// <summary>
    /// The bubbles' tick, from _PhysicsProcess (tests call it): time runs out (the host's clock), the walk-in and the step aboard, the
    /// hover, and the lead (its drift, its lift, its arrival, a rise or a drift that stays blocked) swept against the world, and a body
    /// that left its bubble.
    /// </summary>
    public void AdvanceBubbles(double delta)
    {
        if (_bubbles.Count == 0 && _bubbleGhosts.Count == 0 && !_carrySetDownPending) return;
        if (!double.IsFinite(delta) || delta <= 0) return;
        var dt = (float)Math.Min(delta, 0.1);
        var now = Clock();
        PruneGhosts(now);
        var body = Player != null && IsInstanceValid(Player) && Player.IsInsideTree() ? Player : null;
        // The dive key held while a ghost shows turns the offer away.
        if (body != null && body.BubblePopKeyHeld) DropPlayerGhosts();
        if (_carrySetDownPending && body != null && body.Ride == SmallPlayerController.BubbleRide.None)
        {
            // A fall that ends on a face it grabbed (decision 88): the bar decides the rest, under the climb's own rules; no set-down,
            // and whatever follows is the climb's, never the pop's.
            if (body.IsClimbing)
            {
                _carrySetDownPending = false;
                ForgetPopFall();
            }
            // Down on the ground or in the water: the carry is learned when it set the player down safely, a fall that hurt not.
            else if (body.IsOnFloor() || body.IsSwimming)
            {
                _carrySetDownPending = false;
                if (!_carryFallHurt) Learned("bubble_carry");
            }
        }
        for (var i = _bubbles.Count - 1; i >= 0; i--)
        {
            if (i >= _bubbles.Count) continue;
            var bubble = _bubbles[i];
            if (!ReferenceEquals(bubble.Rules, Rules)) EndBubble(bubble, BubbleEnd.Popped);
            else if (now >= bubble.Expires) EndBubble(bubble, BubbleEnd.TimedOut);
            else if (body == null) { if (bubble.Holding) EndBubble(bubble, BubbleEnd.Popped); }
            else if (bubble.Kind == BubbleKind.Boat) StepBoat(bubble, body, dt);
            else StepCarry(bubble, body, dt);
        }
    }

    private void StepCarry(Bubble bubble, SmallPlayerController body, float dt)
    {
        var middle = body.GlobalPosition + Vector3.Up * (body.BodyHeightM * 0.5f);
        if (bubble.Holding)
        {
            bubble.RiderLostS = middle.DistanceTo(bubble.Center) > bubble.Radius + BubbleLaws.RiderSlackM ? bubble.RiderLostS + dt : 0;
            if (bubble.RiderLostS >= BubbleLaws.RiderLostAfterS)
            {
                EndBubble(bubble, BubbleEnd.Popped);
                return;
            }
        }
        // Waiting at its point (or led, empty): it holds the player once they walk in, never moving onto them.
        else if (!bubble.Carried && middle.DistanceTo(bubble.Center) <= bubble.Radius * 0.8f && HoldsPlayer() == null &&
                 body.Ride is not (SmallPlayerController.BubbleRide.Enveloped or SmallPlayerController.BubbleRide.Aboard))
        {
            bubble.Holding = bubble.Carried = true;
            bubble.BaseY = bubble.Center.Y;
            UpdateBubbleReceipt(bubble, "carried", true);
            ForgetPopFall();
        }
        var lead = LiveLead(bubble);
        // A bubble moves only while the Gubble leads it; one that holds the player still rises to its hover.
        if (!bubble.Holding && lead == null)
        {
            bubble.Velocity = Vector3.Zero;
            return;
        }
        var sweep = SweepRadius(body);
        var hover = bubble.Holding ? BubbleLaws.CarryHoverM : 0f;
        // It keeps its height, like a balloon on its string: rising ground lifts it at most the drift's own step (DriftStepM) above
        // the height it formed, took the player or was lifted at (BaseY); higher ground blocks its drift. Only a lift, paid when it
        // starts to rise, raises that height (decision 58; the W2 review's major: a held carry rode a 52 degree ramp up 0.73 m free).
        var targetY = bubble.BaseY + hover;
        if (GroundBelow(bubble.Center, BubbleLaws.LiftRiseM + 1f) is { } ground)
            targetY = Math.Max(targetY, Math.Min(ground + sweep + hover, bubble.BaseY + hover + BubbleLaws.DriftStepM));
        var rate = BubbleLaws.CarryRiseMps * dt;
        var level = Vector2.Zero;
        var held = false;
        if (lead != null)
        {
            (level, held) = LeadStep(bubble, lead, BubbleLaws.CarrySpeedMps * dt);
            if (lead.Lift && !lead.Rising && !StartLift(bubble, lead)) return;
            if (lead.Rising)
            {
                targetY = Math.Max(targetY, lead.RiseToY);
                rate = BubbleLaws.LiftRiseMps * dt;
            }
            // Empty, it never moves onto the player: it stops short of them (walking in stays their own consent).
            if (!bubble.Holding && level != Vector2.Zero)
            {
                var next = bubble.Center + new Vector3(level.X, 0, level.Y);
                var clear = bubble.Radius + BubbleLaws.StepInClearanceM + body.BodyRadiusM;
                if (middle.DistanceTo(next) <= clear && middle.DistanceTo(next) < middle.DistanceTo(bubble.Center)) level = Vector2.Zero;
            }
        }
        // Up or down first, then along: a face in the way stops the drift, never the rise that clears it.
        var rise = new Vector3(0, Mathf.Clamp(targetY - bubble.Center.Y, -rate, rate), 0);
        var risen = bubble.Center + rise * SweepFraction(bubble.Center, rise, sweep);
        var along = new Vector3(level.X, 0, level.Y);
        var moved = risen + along * SweepFraction(risen, along, sweep);
        bubble.Velocity = (moved - bubble.Center) / dt;
        var wanted = level.Length();
        var went = LevelGap(moved, bubble.Center);
        var climbed = risen.Y - bubble.Center.Y;
        bubble.Center = moved;
        // The lead that moves it while it holds the player carried them there (a consequence fact if that hurts them, NoteLeadHarm).
        if (lead != null && bubble.Holding) bubble.CarriedBy = lead;
        if (LookHook != null && IsInstanceValid(LookHook)) LookHook.MoveBubble(bubble.Id, moved);
        if (lead != null) AfterLeadStep(bubble, lead, dt, wanted, went, held, Math.Abs(targetY - moved.Y) <= BubbleLaws.LeadArriveRiseM, rise.Y, climbed);
    }

    private void StepBoat(Bubble bubble, SmallPlayerController body, float dt)
    {
        var feet = body.GlobalPosition;
        var off = LevelGap(feet, bubble.Center);
        if (bubble.Holding)
        {
            bubble.RiderLostS = off > bubble.Radius + BubbleLaws.RiderSlackM ? bubble.RiderLostS + dt : 0;
            if (bubble.RiderLostS >= BubbleLaws.RiderLostAfterS)
            {
                bubble.Holding = false;
                bubble.RiderLostS = 0;
            }
        }
        else if (bubble.BoardLatch)
        {
            if (off > bubble.Radius + BubbleLaws.StepInClearanceM) bubble.BoardLatch = false;
        }
        else if (off <= bubble.Radius && feet.Y >= bubble.Center.Y - BubbleLaws.BoardBelowDeckM && feet.Y <= bubble.Center.Y + BubbleLaws.BoardAboveDeckM &&
                 HoldsPlayer() == null && body.Ride == SmallPlayerController.BubbleRide.None)
        {
            bubble.Holding = true;
            ForgetPopFall();
            if (!bubble.Carried)
            {
                bubble.Carried = true;
                UpdateBubbleReceipt(bubble, "carried", true);
            }
        }
        // It floats on the water; it moves only while the Gubble leads it, wherever the water goes; empty, it never slides under the player.
        bubble.Velocity = Vector3.Zero;
        var lead = LiveLead(bubble);
        if (lead == null) return;
        var (step, held) = LeadStep(bubble, lead, BubbleLaws.BoatSpeedMps * dt);
        var wanted = step.Length();
        var went = 0f;
        var next = bubble.Center + new Vector3(step.X, 0, step.Y);
        if (step != Vector2.Zero && (bubble.Holding || LevelGap(feet, next) > bubble.Radius + BubbleLaws.StepInClearanceM + body.BodyRadiusM) &&
            FloatingWaterAt(next) is { } surface)
        {
            // Its rim meets the shore, rocks and walls: a small sphere just over the deck, swept.
            var motion = new Vector3(step.X, 0, step.Y);
            var rim = new Vector3(0, BubbleLaws.BoardAboveDeckM, 0);
            var moved = bubble.Center + motion * SweepFraction(bubble.Center + rim, motion, BubbleLaws.BoardAboveDeckM * 0.8f);
            moved.Y = surface + BubbleLaws.BoatDeckAboveWaterM;
            bubble.Velocity = (moved - bubble.Center) / dt;
            went = LevelGap(moved, bubble.Center);
            bubble.Center = moved;
            if (LookHook != null && IsInstanceValid(LookHook)) LookHook.MoveBubble(bubble.Id, moved);
        }
        AfterLeadStep(bubble, lead, dt, wanted, went, held, settled: true, riseWanted: 0, riseWent: 0);
    }

    private static float LevelGap(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>The island's play-space margin: a spot this far past the room's bounds still counts as inside them.</summary>
    public const float IslandSlackM = 0.02f;

    /// <summary>
    /// The island's bounds for a spot, one rule for every cast, effect and lead, the body's own (SmallPlayerController.BoundsHoldSides):
    /// without a sea, inside the room's bounds; round an island the sea has no edge, so only the top and the bottom hold (a boat may
    /// be cast and led out on the open sea, as a body swims there). Within IslandSlackM either way.
    /// </summary>
    private bool InsideIsland(Vector3 point)
    {
        var bounds = Room.Bounds.Grow(IslandSlackM);
        if (Room.Sea == null) return bounds.HasPoint(point);
        return point.IsFinite() && point.Y >= bounds.Position.Y && point.Y <= bounds.End.Y;
    }

    // ---- the world: collision and water ----

    /// <summary>A bubble's swept shape: the sphere round the body, so the bubble goes wherever the body fits.</summary>
    private static float SweepRadius(SmallPlayerController body) => body.BodyHeightM * 0.5f + 0.005f;

    /// <summary>The avatars are inside or beside their bubbles: the sweep passes through them.</summary>
    private Godot.Collections.Array<Rid> BubbleExclusions()
    {
        if (_bubbleExclude != null) return _bubbleExclude;
        _bubbleExclude = new Godot.Collections.Array<Rid>();
        if (Player != null && IsInstanceValid(Player)) _bubbleExclude.Add(Player.GetRid());
        if (Companion != null && IsInstanceValid(Companion)) _bubbleExclude.Add(Companion.GetRid());
        return _bubbleExclude;
    }

    /// <summary>How much of a motion a sphere makes before it meets what the bodies stand on and bump into (1: all of it).</summary>
    private float SweepFraction(Vector3 from, Vector3 motion, float radius)
    {
        if (motion.LengthSquared() < 1e-12f || !IsInsideTree()) return 1f;
        _bubbleShape ??= new SphereShape3D();
        if (!Mathf.IsEqualApprox(_bubbleShape.Radius, radius)) _bubbleShape.Radius = radius;
        if (_bubbleQuery == null)
        {
            _bubbleQuery = new PhysicsShapeQueryParameters3D { CollisionMask = RoomBuilder.BodyMask, Shape = _bubbleShape };
            _bubbleQuery.Exclude = BubbleExclusions();
        }
        _bubbleQuery.Transform = new Transform3D(Basis.Identity, from);
        _bubbleQuery.Motion = motion;
        var fractions = GetViewport().World3D.DirectSpaceState.CastMotion(_bubbleQuery);
        return fractions.Length > 0 && float.IsFinite(fractions[0]) ? Mathf.Clamp(fractions[0], 0, 1) : 1f;
    }

    /// <summary>The height of what a body would stand on under a point, within depth (null: nothing there).</summary>
    private float? GroundBelow(Vector3 point, float depth)
    {
        if (!IsInsideTree()) return null;
        using var query = PhysicsRayQueryParameters3D.Create(point, point + Vector3.Down * depth, RoomBuilder.BodyMask, BubbleExclusions());
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? hit[HitPositionKey].AsVector3().Y : null;
    }

    /// <summary>A point raised, if need be, so a sphere of this radius round it clears the ground under it.</summary>
    private Vector3 ClearOfGround(Vector3 point, float radius) =>
        GroundBelow(point + Vector3.Up * radius, radius * 2 + 0.01f) is { } ground && ground + radius + 0.005f > point.Y ? new Vector3(point.X, ground + radius + 0.005f, point.Z) : point;

    private RoomWater.Column WaterColumn(Vector3 point, float above, float below)
    {
        if (!IsInsideTree()) return RoomWater.Column.Dry;
        var space = GetViewport().World3D.DirectSpaceState;
        var water = RoomWater.At(space, point, above, below);
        if (!water.Wet && Room.Sea != null) water = Room.Sea.OpenWater(space, point, above, below);
        return water;
    }

    /// <summary>Water at a cast's spot (its surface within BubbleLaws.BoatCastSurfaceM of it), where the Bubble is a boat: the surface, or null.</summary>
    private float? WaterForBoat(Vector3 point)
    {
        var water = WaterColumn(point, BubbleLaws.BoatCastSurfaceM, BubbleLaws.BoatCastSurfaceM);
        return water.Wet && water.DepthM >= BubbleLaws.BoatCastMinDepthM && Math.Abs(water.SurfaceY - point.Y) <= BubbleLaws.BoatCastSurfaceM ? water.SurfaceY : null;
    }

    /// <summary>Water a boat floats on at a place (near the deck it has now): the surface, or null.</summary>
    private float? FloatingWaterAt(Vector3 point)
    {
        var water = WaterColumn(point, 0.15f, 0.15f);
        return water.Wet && water.DepthM >= BubbleLaws.BoatFloatMinDepthM ? water.SurfaceY : null;
    }
}
