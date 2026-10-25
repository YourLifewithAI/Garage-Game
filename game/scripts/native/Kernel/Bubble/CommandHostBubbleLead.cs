using Godot;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The Gubble leading a bubble (goal.set lead; decisions 57, 58 and 60): the smart ask's steering. The player orders it (the HUD's
/// right-click on a bubble, then on a spot) or the Gubble sets it on its own; the target is only a bubble (its effect: id), never an
/// avatar, and position_m is the spot.
/// - The Gubble walks toward the spot and the bubble trails it on its leash (BubbleLaws.LeashM), then drifts the last of the way;
///   it may pop before it arrives (its timer, the player's pop, the dive key or X at any moment, even mid-lead).
/// - A spot higher than the bubble can drift (BubbleLaws.LiftAboveDriftM) makes it a lift: near the spot, or once its drift is
///   blocked, the bubble floats up there, at most BubbleLaws.LiftRiseM ("the 1.2 m rise stays, to test"). The lift's price is
///   charged when it starts to rise, through the guard, to the principal who set the lead; no pips, no rise: goal.set refuses a
///   lift the pool cannot pay, and a pool that emptied since ends the lead with energy_exhausted. The first lift that floats the
///   player up to its spot teaches Lift.
/// - The Gubble's own stop pops the bubble it leads (goal.stop, or a new goal), and the player falls from where it popped (decision
///   88, replacing 60's drift down); a fall that hurts them then is the Gubble's consequence, costing Trust (decision 96). Goals stay
///   free (no pips for a lead itself, nothing saved); the goal.set receipt says how each lead ended:
///   arrived, popped, popped_by_player, timed_out, blocked, no_pips or stopped, and lifted once it rose.
/// - Moving the player: a bubble that holds the player moves only under its lead, the carry exception (consent given by the ghost,
///   the walk-in or the step aboard; the exit always works). No goal moves the player's body directly.
/// </summary>
public partial class CommandHost
{
    /// <summary>The words of a lead whose bubble popped before it got there (its job's failure; the mock host's too).</summary>
    public const string LeadPoppedWords = "The bubble popped before it got there.";
    /// <summary>The words of a lead that could not get its bubble there (the mock host's too).</summary>
    public const string LeadBlockedWords = "The bubble cannot get there from here.";

    /// <summary>
    /// The HUD's view of the one bubble (the smart ask selects it with a right-click and leads it with another): its id, what the place
    /// made of it (carry, boat or walk_in), where it is and how big, whether it holds the player, whether the smart ask can select it
    /// now (the Gubble is here to lead it), the spot it is led to (null when it is not led), whether it is rising on a lift, and who cast it.
    /// </summary>
    public readonly record struct BubbleView(string Id, string Kind, Vector3 CenterM, float RadiusM, bool HoldsPlayer, bool Selectable, Vector3? LedToM, bool Lifting,
        string CastBy);

    /// <summary>The one bubble now (BubbleLaws.MaxBubbles), or null: what the HUD highlights and leads.</summary>
    public BubbleView? ActiveBubble
    {
        get
        {
            if (_bubbles.Count == 0) return null;
            var bubble = _bubbles[^1];
            var lead = LiveLead(bubble);
            return new BubbleView(bubble.Id, bubble.KindWord, bubble.Center, bubble.Radius, bubble.Holding,
                Companion != null && IsInstanceValid(Companion) && Companion.IsInsideTree(), lead?.Spot, lead is { Rising: true },
                bubble.Principal == PlayerPrincipal ? "player" : "companion");
        }
    }

    /// <summary>The bubble a pointer at this point picks (within its radius and slackM), or null: the smart ask's first right-click.</summary>
    public string? BubbleAt(Vector3 point, float slackM = 0.02f)
    {
        if (!point.IsFinite()) return null;
        foreach (var bubble in _bubbles)
            if (point.DistanceTo(bubble.Center) <= bubble.Radius + Math.Max(0, slackM)) return bubble.Id;
        return null;
    }

    /// <summary>
    /// The smart ask's second right-click: the player's order to the Gubble to lead this bubble to a spot (goal.set lead, the player's
    /// command). preview: only whether it would go and whether it is a lift (data.lift, data.lift_price), changing nothing.
    /// </summary>
    public JsonObject PlayerLead(string bubble, Vector3 spot, bool preview = false) =>
        PlayerCommand("goal.set", new JsonObject
        {
            ["actor"] = CompanionAvatarId, ["goal"] = "lead", ["target"] = bubble, ["position_m"] = KernelJson.Vector(spot),
        }, preview);

    /// <summary>
    /// goal.set lead (GoalSet routes it here once the actor is the Gubble): the bubble is checked by its id (the companion names only
    /// one the team sees now), the spot inside the island (on water for a boat), a lift the setter's pool can pay; then the Gubble's
    /// goal is replaced, it walks toward the spot, and the bubble's lead starts as a job.
    /// </summary>
    private JsonObject LeadGoal(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        var actor = Str(args, "actor")!;
        var id = Str(args, "target")!;
        var spot = KernelJson.ReadVector(args.GetProperty("position_m"));
        var bubble = _bubbles.Find(b => b.Id == id) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        // The companion names only what the team sees now: a bubble out of sight answers as one that is not there.
        if (principal != PlayerPrincipal && !_approving && !TeamSeesSpot(bubble.Kind == BubbleKind.Boat ? bubble.Center : bubble.Center - Vector3.Up * (bubble.Radius * 0.5f), principal))
            throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        if (!InsideIsland(spot)) throw new Refusal("out_of_bounds", "That spot is outside the island.", "$.args.position_m");
        if (Companion == null || !IsInstanceValid(Companion) || !Companion.IsInsideTree()) throw new Refusal("not_ready", "The Gubble is not in the room.", retryable: true);
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) throw new Refusal("not_ready", "The player's body is not in the room yet.", retryable: true);
        float ground;
        if (bubble.Kind == BubbleKind.Boat)
            ground = FloatingWaterAt(spot) ?? throw new Refusal("invalid_args", "A boat is led over water: choose a spot on the water.", "$.args.position_m");
        else ground = GroundBelow(spot + Vector3.Up * 0.05f, 0.35f) ?? spot.Y;
        // Higher than its paid height (where it formed, took the player or was lifted to, at its hover) can drift: a lift.
        var lift = bubble.Kind != BubbleKind.Boat && RestOver(bubble, ground) - PaidHeight(bubble) > BubbleLaws.LiftAboveDriftM;
        var data = new JsonObject { ["actor"] = actor, ["goal"] = "lead", ["bubble"] = id, ["position_m"] = KernelJson.Vector(spot), ["lift"] = lift };
        if (principal != PlayerPrincipal) data["target_seen"] = "now";
        if (lift)
        {
            // No pips, no rise: a lift the setter's pool cannot pay now is refused before anything moves.
            try { GuardEnergy("bubble_lift", principal); }
            catch (Refusal refusal) { throw LeadRefusal(refusal); }
            data["lift_price"] = PriceFor("bubble_lift", principal);
        }
        if (preview)
        {
            var previewed = Previewed("goal.set", principal, actionId, actor);
            previewed["data"] = data;
            return previewed;
        }
        // A new lead of the same bubble re-aims it (its old lead ends, stopped, and the bubble stays); any other goal's end pops the
        // bubble it led (LeadCancelled, from CancelGoal).
        if (bubble.Lead is { } old)
        {
            bubble.Lead = null;
            UpdateTransient(old.Principal, old.ActionId, old.Fingerprint, "ended", "stopped");
        }
        CancelGoal(actor, principal);
        Companion.GoTo(new Aabb(spot, Vector3.Zero), BubbleLaws.LeadGubbleStopM);
        var job = StartJob(principal, actor, actionId, "lead", id, new Aabb(spot, Vector3.Zero), Companion.IntentSerial);
        bubble.Lead = new BubbleLead { JobId = job.Id, Principal = principal, ActionId = actionId, Fingerprint = fingerprint, Spot = spot, SpotGroundY = ground, Lift = lift };
        return Transient("goal.set", principal, actionId, fingerprint, new JsonArray(actor, id), data, job.Id);
    }

    /// <summary>A refusal of the guard's, said of the lead's spot (the lift's price is the spot's, not a capability field's).</summary>
    private static Refusal LeadRefusal(Refusal refusal) =>
        refusal.Code == "energy_exhausted"
            ? new Refusal("energy_exhausted", refusal.Message, "$.args.position_m", retryable: true) { Balance = refusal.Balance, Price = refusal.Price }
            : new Refusal(refusal.Code, refusal.Message, "$.args.position_m", refusal.Retryable);

    /// <summary>The height a bubble's centre rests at over ground at this height: round the held body at its hover, or empty, on the ground.</summary>
    private float RestOver(Bubble bubble, float ground) =>
        bubble.Kind == BubbleKind.Boat ? ground + BubbleLaws.BoatDeckAboveWaterM
            : bubble.Holding && Player != null ? ground + SweepRadius(Player) + BubbleLaws.CarryHoverM : ground + bubble.Radius + 0.005f;

    /// <summary>The height a carry holds with no lift (BaseY, at its hover while it holds the player): a drift's step rises no higher.</summary>
    private static float PaidHeight(Bubble bubble) => bubble.BaseY + (bubble.Holding && bubble.Kind == BubbleKind.Carry ? BubbleLaws.CarryHoverM : 0);

    /// <summary>
    /// The Gubble's lead that carried the player where they are now, in the bubble that still holds them (running or ended), or null:
    /// a lead the player ordered is theirs. Fire that hurts them there is that lead's consequence (NoteLeadHarm).
    /// </summary>
    private BubbleLead? GubbleLeadHoldingThePlayer() =>
        HoldsPlayer() is { Kind: BubbleKind.Carry, CarriedBy: { Principal: CompanionPrincipal } lead } ? lead : null;

    /// <summary>The bubble's lead if its job is still the Gubble's running goal; a lead whose job ended some other way is let go.</summary>
    private BubbleLead? LiveLead(Bubble bubble)
    {
        if (bubble.Lead is not { } lead) return null;
        if (_runningGoals.TryGetValue(CompanionAvatarId, out var job) && job.Id == lead.JobId && job.State == "running") return lead;
        bubble.Lead = null;
        return null;
    }

    /// <summary>
    /// The level step a led bubble takes toward its spot, at most most, held back by its string: never farther than BubbleLaws.LeashM
    /// from the Gubble, which walks there too (Held: the string held it back this tick).
    /// </summary>
    private (Vector2 Step, bool Held) LeadStep(Bubble bubble, BubbleLead lead, float most)
    {
        var toSpot = new Vector2(lead.Spot.X - bubble.Center.X, lead.Spot.Z - bubble.Center.Z);
        var gap = toSpot.Length();
        if (gap < 1e-5f || !float.IsFinite(gap)) return (Vector2.Zero, false);
        var step = toSpot / gap * Math.Min(most, gap);
        if (Companion == null || !IsInstanceValid(Companion)) return (Vector2.Zero, true);
        var gubble = Companion.GlobalPosition;
        var next = bubble.Center + new Vector3(step.X, 0, step.Y);
        var away = LevelGap(next, gubble);
        return away > BubbleLaws.LeashM && away > LevelGap(bubble.Center, gubble) ? (Vector2.Zero, true) : (step, false);
    }

    /// <summary>
    /// A lift starts to rise once the bubble is near its spot or its drift is blocked: charged now, to the principal who set the lead,
    /// through the guard. No pips, no rise: the lead ends (no_pips, energy_exhausted) and the bubble stays where it is. False when the
    /// lead ended.
    /// </summary>
    private bool StartLift(Bubble bubble, BubbleLead lead)
    {
        if (LevelGap(bubble.Center, lead.Spot) > BubbleLaws.LiftApproachM && lead.BlockedS < BubbleLaws.LiftWhenBlockedS) return true;
        int price;
        try { price = GuardEnergy("bubble_lift", lead.Principal); }
        catch (Refusal refusal)
        {
            EndLead(bubble, lead, "no_pips", LeadRefusal(refusal));
            return false;
        }
        ChargeEnergy(price);
        TrustLiftCharged(lead); // decision 69: the Lift's charge at the rise makes the Gubble's lead a paid action (CommandHostTrust)
        lead.Rising = true;
        lead.RiseToY = Math.Min(RestOver(bubble, lead.SpotGroundY), bubble.Center.Y + BubbleLaws.LiftRiseM);
        lead.BlockedS = 0;
        UpdateTransient(lead.Principal, lead.ActionId, lead.Fingerprint, "lifted", true);
        return true;
    }

    /// <summary>
    /// After a led bubble's step: blocked time (it wanted to move toward the spot and could not, or the Gubble cannot get near it;
    /// rising, only the rise counts: it takes the time it takes, but a rise that a ceiling stops is blocked too), the arrival (at the
    /// spot and settled at its height there; a lift up at the spot's), and a lead blocked too long, which ends where it is.
    /// </summary>
    private void AfterLeadStep(Bubble bubble, BubbleLead lead, float dt, float wanted, float went, bool held, bool settled, float riseWanted, float riseWent)
    {
        var atSpot = LevelGap(bubble.Center, lead.Spot) <= BubbleLaws.LeadArriveM;
        var highEnough = !lead.Lift || bubble.Center.Y >= RestOver(bubble, lead.SpotGroundY) - BubbleLaws.LeadArriveRiseM;
        if (atSpot && settled && highEnough)
        {
            // The first lift that floats the player up to its spot teaches Lift.
            if (lead.Lift && bubble.Holding) Learned("bubble_lift");
            EndLead(bubble, lead, "arrived", null);
            return;
        }
        // Stuck: its drift met something, its string holds it back while the Gubble cannot get on, or it is over the spot and its rise
        // has topped out short.
        var gubbleStuck = Companion == null || !IsInstanceValid(Companion) || Companion.GoalBlocked;
        var stuck = (!held && wanted > 1e-5f && went < wanted * 0.2f) || (held && gubbleStuck) || (atSpot && settled && !highEnough);
        // Still rising toward the top of its lift: blocked only while something over it stops the rise (the W2 review: a lift under a
        // ceiling ran on until the bubble's timer).
        if (lead.Rising && bubble.Center.Y < lead.RiseToY - BubbleLaws.LeadArriveRiseM)
            lead.BlockedS = riseWanted > 1e-6f && riseWent < riseWanted * 0.2f ? lead.BlockedS + dt : 0;
        else lead.BlockedS = stuck ? lead.BlockedS + dt : 0;
        if (lead.BlockedS >= BubbleLaws.LeadBlockedAfterS && (!lead.Lift || lead.Rising))
            EndLead(bubble, lead, "blocked", new Refusal("target_unreachable", LeadBlockedWords, "$.args.position_m"));
    }

    /// <summary>
    /// A lead ends: a lift that rose holds the height it got to (when the bubble is still there: its paid height, BaseY), the goal.set
    /// receipt says how, and the Gubble's job finishes: succeeded on arrival, failed with why otherwise (a popped bubble, blocked, no
    /// pips). The Gubble stays put. A drift's step over rising ground stays no more than a step (BubbleLaws.DriftStepM): no chain of
    /// leads climbs for free.
    /// </summary>
    private void EndLead(Bubble bubble, BubbleLead lead, string word, Refusal? why)
    {
        if (!ReferenceEquals(bubble.Lead, lead)) return;
        bubble.Lead = null;
        if (lead.Rising && _bubbles.Contains(bubble)) bubble.BaseY = bubble.Center.Y - (bubble.Holding && bubble.Kind == BubbleKind.Carry ? BubbleLaws.CarryHoverM : 0);
        UpdateTransient(lead.Principal, lead.ActionId, lead.Fingerprint, "ended", word);
        if (!_runningGoals.TryGetValue(CompanionAvatarId, out var job) || job.Id != lead.JobId) return;
        Finish(job, why);
        if (why != null && Companion != null && IsInstanceValid(Companion)) Companion.Stay();
    }

    /// <summary>
    /// CancelGoal's hook (a new goal, a stop, a wish that ended): a lead's job cancelled pops the bubble it led, and the player falls
    /// from where it popped (decision 88, replacing 60's drift). Its receipt says stopped. A new goal the Gubble set itself (by: the
    /// companion's principal) is its own pop (decision 96); the player's new order for it, or anything else, is not.
    /// </summary>
    private void LeadCancelled(GoalJob job, string? by)
    {
        if (job.Goal != "lead") return;
        var bubble = _bubbles.Find(b => b.Lead?.JobId == job.Id);
        if (bubble?.Lead is not { } lead) return;
        bubble.Lead = null;
        UpdateTransient(lead.Principal, lead.ActionId, lead.Fingerprint, "ended", "stopped");
        EndBubble(bubble, by == CompanionPrincipal ? BubbleEnd.PoppedByGubble : BubbleEnd.Popped);
    }

    /// <summary>
    /// goal.stop of the Gubble: the bubble it leads pops, before its goal is cancelled, as the player's own pop when it is the player's
    /// X (whoever cast it), and as the Gubble's own (decision 96) when it is the Gubble's own stop. Returns its id, or null when it leads none.
    /// </summary>
    private string? PopLedBubble(bool byPlayer)
    {
        if (!_runningGoals.TryGetValue(CompanionAvatarId, out var job) || job.Goal != "lead") return null;
        var bubble = _bubbles.Find(b => b.Lead?.JobId == job.Id);
        if (bubble?.Lead is not { } lead) return null;
        bubble.Lead = null;
        UpdateTransient(lead.Principal, lead.ActionId, lead.Fingerprint, "ended", "stopped");
        EndBubble(bubble, byPlayer ? BubbleEnd.PoppedByPlayer : BubbleEnd.PoppedByGubble);
        return bubble.Id;
    }
}
