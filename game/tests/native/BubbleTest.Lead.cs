using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests;

/// <summary>
/// The Gubble's lead (goal.set lead, decisions 57, 58 and 60): arriving, popping short, the Gubble's own stop popping what it leads,
/// a new lead re-aiming; the lift (floating up to a higher spot, charged when it starts to rise to whoever set the lead, refused
/// without pips, the 1.2 m rise); a fall into deep water from a lift (decision 88); and the HUD's view of the one bubble.
/// </summary>
public partial class BubbleTest
{
    // ---- the lead arrives, or the bubble pops before it gets there ----

    private async Task TestLeadArrivesAndPops()
    {
        await NewWorld("lead", mode: "challenge", pips: 8);
        var feet = _player.GlobalPosition;
        // An empty bubble to walk into, led by the player's order to a spot: it gets there and stays; the lead itself costs nothing.
        var waiting = CastTheBubble(Player, feet + new Vector3(0.3f, 0, 0.1f));
        var pips = Pips;
        var spot = feet + new Vector3(0.65f, 0, -0.4f);
        var preview = Lead(Player, BubbleOf(waiting), spot, preview: true);
        var still = BubbleCenter;
        var led = Lead(Player, BubbleOf(waiting), spot);
        var running = JobState(Player, led);
        Check(Ok(preview) && preview["preview"]?.GetValue<bool>() == true && preview["data"]?["lift"]?.GetValue<bool>() == false && preview["job_id"] == null &&
              Ok(led) && led["transient"]?.GetValue<bool>() == true && led["data"]?["goal"]?.GetValue<string>() == "lead" && led["data"]?["bubble"]?.GetValue<string>() == BubbleOf(waiting) &&
              led["affected"]?.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { CommandHost.CompanionAvatarId, BubbleOf(waiting) }) == true && running == "running",
            $"goal.set lead: its preview says whether it is a lift and starts nothing; set, it runs as the Gubble's job ({running}): " + Canonical(led));
        var state = await UntilLeadEnds(Player, led);
        var there = BubbleCenter;
        await Frames(60);
        Check(state == "succeeded" && LevelGap(there, spot) <= BubbleLaws.LeadArriveM + 0.005f && BubbleCenter.DistanceTo(there) < 0.002f && Pips == pips &&
              ReceiptData(Player, led)?["ended"]?.GetValue<string>() == "arrived" && still.DistanceTo(BubbleCenter) > 0.3f,
            $"the bubble arrives at the spot ({LevelGap(there, spot):0.000} m off), its job succeeded, its receipt says arrived, it stays there, and the lead was free ({pips} to {Pips} pips)");
        // A carry holding the player: the Gubble's lead takes them along, then the dive key.
        await PlayerAt(feet);
        var carry = TheBubbleOnThePlayer(Companion);
        await Frames(30);
        var from = _player.GlobalPosition;
        var along = Lead(Companion, BubbleOf(carry), feet + new Vector3(-0.5f, 0, -0.35f));
        var arrived = await UntilLeadEnds(Companion, along);
        Check(Ok(along) && along["data"]?["target_seen"]?.GetValue<string>() == "now" && arrived == "succeeded" && _player.Ride == SmallPlayerController.BubbleRide.Enveloped &&
              LevelGap(_player.GlobalPosition, from) > 0.4f,
            $"the Gubble's own lead takes the bubble holding the player where it leads ({arrived}, {LevelGap(_player.GlobalPosition, from):0.00} m)");
        await DiveKey();
        await UntilDown();
        // Popping short: a bubble whose time runs out on the way pops there, and the player falls where it popped (from its hover).
        await PlayerAt(feet);
        var brief = TheBubbleOnThePlayer(Companion, duration: 2.5);
        var far = feet + new Vector3(-1.2f, 0, -0.9f);
        var away = Lead(Companion, BubbleOf(brief), far);
        var ended = await UntilLeadEnds(Companion, away);
        var where = _player.GlobalPosition;
        var landing = await UntilDown();
        Check(Ok(away) && ended == "failed" && JobError(Companion, away)?["code"]?.GetValue<string>() == "target_not_found" &&
              JobError(Companion, away)?["message"]?.GetValue<string>() == CommandHost.LeadPoppedWords && ReceiptData(Companion, away)?["ended"]?.GetValue<string>() == "timed_out" &&
              ReceiptData(Companion, brief)?["ended"]?.GetValue<string>() == "timed_out" && LevelGap(where, far) > 0.3f && landing < BodyKnacks.None.SafeLandingSpeedMps,
            $"a bubble may pop before it arrives: its time ran out on the way, the lead failed (the bubble popped), its receipt says timed_out, and the player came down " +
            $"short of the spot ({LevelGap(where, far):0.00} m short, landing {landing:0.000} m/s)");
    }

    // ---- the Gubble's own stop pops the bubble it leads (decision 60), and a new lead re-aims it ----

    private async Task TestTheGubblesStopPopsWhatItLeads()
    {
        await NewWorld("lead-stop", mode: "challenge");
        var feet = _player.GlobalPosition;
        var health = Health;
        var carry = TheBubbleOnThePlayer(Player, duration: 60);
        var first = Lead(Companion, BubbleOf(carry), feet + new Vector3(-0.6f, 0, -0.3f));
        await Frames(40);
        // A new lead of the same bubble re-aims it: the bubble stays, the first lead ends (stopped), the new one runs.
        var second = Lead(Companion, BubbleOf(carry), feet + new Vector3(0.5f, 0, -0.4f));
        Check(Ok(first) && Ok(second) && _host.PlayerInBubble && JobState(Companion, first) == "cancelled" && JobState(Companion, second) == "running" &&
              ReceiptData(Companion, first)?["ended"]?.GetValue<string>() == "stopped",
            "a new lead of the same bubble re-aims it: the bubble stays and the first lead's receipt says stopped");
        await Frames(40);
        // The Gubble's own stop pops the bubble it leads, a player's bubble too, and the player falls where it popped (from its hover:
        // unhurt, so no consequence is written; APopInTheAirIsAFall has the hurt).
        var stop = Send(Command(NextId("stop"), "goal.stop", new JsonObject()), Companion);
        var where = _player.GlobalPosition;
        var landing = await UntilDown();
        Check(Ok(stop) && !_host.PlayerInBubble && stop["affected"]?.AsArray().Any(n => n!.GetValue<string>() == BubbleOf(carry)) == true && JobState(Companion, second) == "cancelled" &&
              ReceiptData(Companion, second)?["ended"]?.GetValue<string>() == "stopped" && ReceiptData(Player, carry)?["ended"]?.GetValue<string>() == "popped" &&
              LevelGap(_player.GlobalPosition, where) < 0.05f && landing < BodyKnacks.None.SafeLandingSpeedMps && Health == health && Facts(CommandHost.PopFallPrefix) == 0,
            $"the Gubble's own stop pops the bubble it leads (popped), even the player's; the player falls where it popped, unhurt from the hover, and no fact is written ({landing:0.000} m/s, health {Health})");
        // Any other new goal of the Gubble's pops it too.
        var again = TheBubbleOnThePlayer(Companion, duration: 60);
        var third = Lead(Companion, BubbleOf(again), feet + new Vector3(-0.6f, 0, -0.3f));
        await Frames(30);
        var follow = Send(Command(NextId("follow"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "follow" }), Companion);
        Check(Ok(third) && Ok(follow) && !_host.PlayerInBubble && JobState(Companion, third) == "cancelled" && ReceiptData(Companion, again)?["ended"]?.GetValue<string>() == "popped",
            "a new goal of the Gubble's (follow) pops the bubble it led: " + Canonical(ReceiptData(Companion, again)));
        await UntilDown();
        // A bubble nobody leads is not the Gubble's to pop by a goal: a new goal leaves the player's own bubble alone.
        var mine = CastTheBubble(Player, feet + new Vector3(0.3f, 0, 0.1f));
        var go = Send(Command(NextId("go"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "go_to", ["position_m"] = Vec(feet + new Vector3(0.4f, 0, -0.4f)) }), Companion);
        Check(Ok(mine) && Ok(go) && _host.ActiveBubbles.Count == 1, "a new goal pops only a bubble the Gubble leads");
    }

    // ---- the lift (decision 58): led to a higher spot, it floats up there ----

    private async Task TestLift()
    {
        await NewWorld("lift", mode: "challenge");
        var start = _player.GlobalPosition;
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        await Frames(30);
        var pips = Pips;
        var led = Lead(Companion, BubbleOf(carry), BoxTop);
        var atSet = Pips;
        var chargedAt = -1;
        for (var i = 0; i < 600 && Pips == atSet; i++)
        {
            await Frames(1);
            chargedAt = i;
        }
        var rising = _host.ActiveBubble is { Lifting: true };
        var lifted = ReceiptData(Companion, led)?["lifted"]?.GetValue<bool>() == true;
        var gapAtRise = LevelGap(BubbleCenter, BoxTop);
        var state = await UntilLeadEnds(Companion, led);
        Check(Ok(led) && led["data"]?["lift"]?.GetValue<bool>() == true && led["data"]?["lift_price"]?.GetValue<int>() == 3 && atSet == pips && Pips == pips - 3 && rising && lifted &&
              gapAtRise <= BubbleLaws.LiftApproachM + 0.05f,
            $"a lead to a higher spot is a lift: nothing is charged when it is set, and the Gubble's own-idea price, 3, when it starts to rise near the spot " +
            $"({chargedAt / 60.0:0.0} s in, {gapAtRise:0.00} m from it): " + Canonical(led["data"]));
        Check(state == "succeeded" && ReceiptData(Companion, led)?["ended"]?.GetValue<string>() == "arrived" && _player.GlobalPosition.Y > BoxTop.Y &&
              LevelGap(_player.GlobalPosition, BoxTop) < 0.08f && _learned.Contains("bubble_lift") && _host.GlowProgress.Has(ExpertiseLists.Bubbles, ExpertiseLists.BubbleLift),
            $"it floats the player up onto the box ({_player.GlobalPosition.Y:0.00} m), the lead arrives, and the first lift that works teaches Lift: {string.Join(",", _learned)}");
        await DiveKey();
        await UntilDown();
        Check(_player.IsOnFloor() && Math.Abs(_player.GlobalPosition.Y - BoxTop.Y) < 0.03f, $"popped, the player drops onto the box top ({_player.GlobalPosition.Y:0.000} m)");
        // The rise stays 1.2 m (to test): a spot higher than that floats the bubble up 1.2 m, and the lead ends there, blocked.
        await NewWorld("lift-high");
        var high = TheBubbleOnThePlayer(Companion, duration: 60);
        await Frames(45);
        var hover = _player.GlobalPosition.Y;
        var tooHigh = Lead(Companion, BubbleOf(high), start + new Vector3(0.05f, 1.6f, 0));
        var ended = await UntilLeadEnds(Companion, tooHigh, 2400);
        var rose = _player.GlobalPosition.Y - hover;
        Check(Ok(tooHigh) && ended == "failed" && JobError(Companion, tooHigh)?["code"]?.GetValue<string>() == "target_unreachable" &&
              ReceiptData(Companion, tooHigh)?["ended"]?.GetValue<string>() == "blocked" && ReceiptData(Companion, tooHigh)?["lifted"]?.GetValue<bool>() == true &&
              Math.Abs(rose - BubbleLaws.LiftRiseM) < 0.08f && _host.PlayerInBubble,
            $"a spot higher than the rise: it floats up its {BubbleLaws.LiftRiseM} m ({rose:0.00} m) and the lead ends there, blocked; the bubble holds the player where it got to");
        var held = _player.GlobalPosition;
        await Frames(60);
        Check(held.DistanceTo(_player.GlobalPosition) < 0.01f, "after the lead the bubble stays where it got to, the player inside");
        _host.PlayerGoal("stop");
        var landing = await UntilDown(1800);
        Check(landing > BodyKnacks.None.SafeLandingSpeedMps && Health == 100 && _player.IsOnFloor(),
            $"and X pops it up there: the player falls the 1.2 m (decision 88), and in Creative it hurts no one ({landing:0.00} m/s, health {Health})");
    }

    // ---- the lift's price: by the principal who set the lead, at the rise; no pips, no rise ----

    private async Task TestLiftPrice()
    {
        // The player's lift: 6 pips, charged at the rise.
        await NewWorld("lift-ordered", mode: "challenge", pips: 8);
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        var ordered = _host.PlayerLead(BubbleOf(carry), BoxTop);
        var afterSet = Pips;
        var state = await UntilLeadEnds(Player, ordered);
        Check(Ok(ordered) && ordered["data"]?["lift_price"]?.GetValue<int>() == 6 && afterSet == 6 && Pips == 0 && state == "succeeded",
            $"the player's ordered lift costs the ordered price, 6, charged at the rise to the player who set it ({afterSet} after the set, {Pips} after the rise)");
        // No pips: the Gubble's lift with 2 in the pool is refused, and nothing moves.
        await NewWorld("lift-poor", mode: "challenge", pips: 4);
        var poorCarry = TheBubbleOnThePlayer(Companion, duration: 60);
        var at = BubbleCenter;
        var poor = Lead(Companion, BubbleOf(poorCarry), BoxTop);
        await Frames(120);
        Check(Code(poor) == "energy_exhausted" && Path(poor) == "$.args.position_m" && poor["error"]?["balance"]?.GetValue<int>() == 2 && poor["error"]?["price"]?.GetValue<int>() == 3 &&
              poor["job_id"] == null && BubbleCenter.DistanceTo(at) < 0.05f && Pips == 2,
            "no pips, no rise: a lift the setter's pool cannot pay is refused energy_exhausted, with the balance and the price, and nothing moves: " + Canonical(poor["error"]));
        // A pool that emptied between the set and the rise: the lead ends there (no_pips), and the bubble does not rise.
        await NewWorld("lift-emptied", mode: "challenge", pips: 5);
        var emptied = TheBubbleOnThePlayer(Companion, duration: 60);
        await Frames(30);
        var low = BubbleCenter.Y;
        var set = Lead(Companion, BubbleOf(emptied), BoxTop);
        var glow = _host.PlayerEffect("glow");
        var ended = await UntilLeadEnds(Companion, set);
        var error = JobError(Companion, set);
        Check(Ok(set) && Ok(glow) && ended == "failed" && error?["code"]?.GetValue<string>() == "energy_exhausted" && error?["balance"]?.GetValue<int>() == 1 &&
              error?["price"]?.GetValue<int>() == 3 && ReceiptData(Companion, set)?["ended"]?.GetValue<string>() == "no_pips" && ReceiptData(Companion, set)?["lifted"] == null &&
              BubbleCenter.Y < low + 0.05f && Pips == 1 && _host.PlayerInBubble,
            $"the pool emptied before the rise: the lead ends where it is, energy_exhausted, its receipt no_pips, and the bubble never rose ({BubbleCenter.Y - low:0.000} m): " + Canonical(error));
        // Creative's shadow pool never refuses a lift.
        await NewWorld("lift-creative", mode: "creative", pips: 0);
        var free = TheBubbleOnThePlayer(Companion, duration: 60);
        var shadow = Lead(Companion, BubbleOf(free), BoxTop);
        var shadowState = await UntilLeadEnds(Companion, shadow);
        Check(Ok(shadow) && shadowState == "succeeded" && Pips == 0, $"in Creative the shadow pool never refuses a lift ({shadowState})");
    }

    // ---- a fall into deep water ----

    private async Task TestFallIntoWater()
    {
        await NewWorld("fall-water", mode: "challenge");
        var health = Health;
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        var over = new Vector3((PoolMin.X + PoolMax.X) / 2, 0.7f, (PoolMin.Y + PoolMax.Y) / 2);
        var led = Lead(Companion, BubbleOf(carry), over);
        var state = await UntilLeadEnds(Companion, led);
        var height = _player.GlobalPosition.Y - PoolY;
        _host.PlayerGoal("stop");
        var landing = await UntilDown();
        await Frames(30);
        Check(Ok(carry) && Ok(led) && state == "succeeded" && height > 0.5f && _player.IsSwimming && Health == health && landing > BodyKnacks.None.SafeLandingSpeedMps,
            $"a lift over deep water popped {height:0.00} m up falls into it, the water takes the landing, and the body just swims (swimming {_player.IsSwimming}, " +
            $"landing {landing:0.000} m/s, health {health} to {Health})");
    }

    // ---- what the HUD pass reads: the one bubble, the smart ask's pick, and a lead's preview ----

    private async Task TestHudView()
    {
        await NewWorld("hud", mode: "challenge");
        var feet = _player.GlobalPosition;
        var none = _host.ActiveBubble;
        var waiting = CastTheBubble(Player, feet + new Vector3(0.3f, 0, 0.1f));
        var view = _host.ActiveBubble;
        var picked = _host.BubbleAt(BubbleCenter + new Vector3(0.05f, 0, 0));
        var missed = _host.BubbleAt(BubbleCenter + new Vector3(0.3f, 0, 0));
        Check(none == null && view is { Kind: "walk_in", HoldsPlayer: false, Selectable: true, LedToM: null, Lifting: false, CastBy: "player" } && view.Value.Id == BubbleOf(waiting) &&
              Math.Abs(view.Value.RadiusM - BubbleLaws.CarryRadiusDefaultM) < 1e-6 && picked == BubbleOf(waiting) && missed == null,
            $"ActiveBubble shows the one bubble (id, kind, centre, radius, held or not, selectable, led or not, rising or not, who cast it) and BubbleAt picks it: {view}");
        var preview = _host.PlayerLead(BubbleOf(waiting), BoxTop, preview: true);
        Check(Ok(preview) && preview["preview"]?.GetValue<bool>() == true && preview["data"]?["lift"]?.GetValue<bool>() == true && preview["data"]?["lift_price"]?.GetValue<int>() == 6 &&
              _host.ActiveBubble?.LedToM == null,
            "PlayerLead's preview says the spot is a lift and its price for the player, and leads nothing: " + Canonical(preview["data"]));
        var led = _host.PlayerLead(BubbleOf(waiting), feet + new Vector3(0.6f, 0, -0.4f));
        var leading = _host.ActiveBubble;
        Check(Ok(led) && leading?.LedToM is { } to && to.DistanceTo(feet + new Vector3(0.6f, 0, -0.4f)) < 0.001f,
            $"while it is led, ActiveBubble names the spot it is led to: {leading}");
        var carry = TheBubbleOnThePlayer(Companion);
        Check(_host.ActiveBubble is { Kind: "carry", HoldsPlayer: true, CastBy: "companion", LedToM: null }, $"a carry on the player shows held, cast by the Gubble: {_host.ActiveBubble}");
        _host.PlayerGoal("stop");
        var boat = CastTheBubble(Player, new Vector3(-0.85f, WaterY, 0.6f));
        Check(Ok(carry) && Ok(boat) && _host.ActiveBubble is { Kind: "boat", HoldsPlayer: false }, $"and a boat, a boat: {_host.ActiveBubble}");
        await Task.CompletedTask;
    }
}
