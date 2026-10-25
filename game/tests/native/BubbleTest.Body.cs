using Godot;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// The body and the bubble: the ride, the fall out of it (decision 88: a fall from wherever a bubble pops in the air, hurting as any
/// fall, a face grabbed on the way down; replacing 43's deflate), the walk-in, the boat on water, the security row's tests by name, and
/// the budgets.
/// </summary>
public partial class BubbleTest
{
    private static float LevelGap(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>
    /// Frames until the body stands (or the water has it) and no bubble moves it, at most maxFrames; the largest landing speed it
    /// reported on the way (a fall out of a bubble reports its speed here, and hurts past the safe drop).
    /// </summary>
    private async Task<float> UntilDown(int maxFrames = 900)
    {
        var landing = 0f;
        for (var i = 0; i < maxFrames && (_player.Ride != SmallPlayerController.BubbleRide.None || !(_player.IsOnFloor() || _player.IsSwimming)); i++)
        {
            await Frames(1);
            if (_player.LastBodyState.Landed) landing = Math.Max(landing, _player.LastBodyState.LandingSpeedMps);
        }
        await Frames(3);
        if (_player.LastBodyState.Landed) landing = Math.Max(landing, _player.LastBodyState.LandingSpeedMps);
        return landing;
    }

    // ---- the ride and the fall out of it ----

    private async Task TestRideAndFall()
    {
        await NewWorld("ride", mode: "challenge");
        var start = _player.GlobalPosition;
        var health = Health;
        var carry = TheBubbleOnThePlayer(Companion);
        await Frames(45);
        var raised = _player.GlobalPosition.Y - start.Y;
        Check(Ok(carry) && _player.Ride == SmallPlayerController.BubbleRide.Enveloped && raised >= BubbleLaws.CarryHoverM * 0.6f && raised < 0.2f,
            $"the carry lifts the body off the floor and holds it ({raised * 100:0.0} cm up, {_player.Ride})");
        // Not led, it stays where it formed, wherever the Gubble goes.
        var held = _player.GlobalPosition;
        GubbleAt(start + new Vector3(-0.8f, 0, -0.3f));
        await Frames(90);
        Check(LevelGap(_player.GlobalPosition, held) < 0.01f && _player.Ride == SmallPlayerController.BubbleRide.Enveloped,
            $"a carry nobody leads stays put while the Gubble moves away: it moves only when led ({LevelGap(_player.GlobalPosition, held):0.000} m)");
        GubbleAt(_room.SpawnFor("companion", _room.SpawnFor("player")).PositionM);
        await DiveKey();
        var poppedRide = _player.Ride;
        Check(!_host.PlayerInBubble && _player.BubblePops == 1 && poppedRide == SmallPlayerController.BubbleRide.None &&
              ReceiptData(Companion, carry)?["ended"]?.GetValue<string>() == "popped_by_player",
            $"the dive key pops the Gubble's carry at once, popped by the player ({poppedRide}): " + Canonical(ReceiptData(Companion, carry)));
        var landing = await UntilDown();
        Check(_player.Ride == SmallPlayerController.BubbleRide.None && _player.IsOnFloor() && Health == health && landing > 0.1f && landing < BodyKnacks.None.SafeLandingSpeedMps,
            $"the body falls the carry's hover and stands, unhurt ({health} to {Health}): a landing under the safe drop ({landing:0.000} m/s)");
        Check(_learned.Contains("bubble_carry") && _host.GlowProgress.Has(ExpertiseLists.Bubbles, ExpertiseLists.BubbleCarry),
            "a carry that set the player down safely is a carry learned, kept in the team's expertise store: " + string.Join(",", _learned));
        // Put down somewhere else while it holds them (a recovery, a teleport), the body has left the bubble: it pops and never drags them back.
        var again = TheBubbleOnThePlayer(Companion);
        await Frames(30);
        var elsewhere = start + new Vector3(0.5f, 0, -0.3f);
        var teleported = _player.TryTeleportTo(elsewhere);
        await Frames(30);
        Check(Ok(again) && teleported && LevelGap(_player.GlobalPosition, elsewhere) < 0.02f && !_host.PlayerInBubble &&
              ReceiptData(Companion, again)?["ended"]?.GetValue<string>() == "popped",
            $"a body put down elsewhere has left its bubble: it pops (popped) and the body stays where it was put ({LevelGap(_player.GlobalPosition, elsewhere):0.000} m)");
    }

    // ---- the walk-in (decision 55): a bubble at a spot holds the player only when they walk into it ----

    private async Task TestWalkIn()
    {
        await NewWorld("walk-in");
        var spot = _player.GlobalPosition + new Vector3(0.25f, 0, 0.15f);
        var waiting = CastTheBubble(Companion, spot);
        var at = BubbleCenter;
        GubbleAt(_player.GlobalPosition + new Vector3(-0.6f, 0, -0.3f));
        await Frames(90);
        Check(Ok(waiting) && !_host.PlayerInBubble && BubbleCenter.DistanceTo(at) < 0.001f && _player.Ride == SmallPlayerController.BubbleRide.None,
            "a bubble at a spot waits where it was cast, never drifting toward the player or after the Gubble, and holds no one");
        // Walking into it is the consent.
        await PlayerAt(spot);
        await Frames(10);
        Check(_host.PlayerInBubble && _player.Ride == SmallPlayerController.BubbleRide.Enveloped && ReceiptData(Companion, waiting)?["carried"]?.GetValue<bool>() == true,
            "the player walking in is the consent: it holds them, and its receipt says carried: " + Canonical(ReceiptData(Companion, waiting)));
        var inside = _player.GlobalPosition;
        await Frames(60);
        Check(LevelGap(_player.GlobalPosition, inside) < 0.01f, "holding the player it stays where it is until the Gubble leads it");
        // The drawn body is told it is held (BodyState.InBubble), so it floats inside the bubble (the gait layer).
        Check(_player.LastBodyState.InBubble, "a bubble that holds the player puts it in the body's state, for the drawn body to float");
        await DiveKey();
        await UntilDown();
        Check(!_host.PlayerInBubble && ReceiptData(Companion, waiting)?["ended"]?.GetValue<string>() == "popped_by_player", "and the dive key pops it");
        await Frames(4);
        Check(!_player.LastBodyState.InBubble, "and popped, the body's state says it is out of the bubble");
    }

    // ---- the boat ----

    private async Task TestBoat()
    {
        await NewWorld("boat");
        var wading = new Vector3(-0.5f, 0, 0.6f);
        await PlayerAt(wading);
        var deck = new Vector3(-0.85f, WaterY, 0.6f);
        var onThePlayer = AtPoint(Companion, "bubble_boat", wading with { Y = WaterY }, radius: 0.12, duration: 60);
        var tooNear = CastTheBubble(Companion, wading + new Vector3(0.135f, WaterY, 0), duration: 60);
        var boat = CastTheBubble(Companion, deck, duration: 60);
        Check(Code(onThePlayer) == "invalid_args" && Path(onThePlayer) == "$.args.center_m" && Code(tooNear) == "invalid_args" && Ok(boat) && Kind(boat) == "boat" &&
              boat["data"]?["carried"]?.GetValue<bool>() == false && Math.Abs(BubbleCenter.Y - (WaterY + BubbleLaws.BoatDeckAboveWaterM)) < 0.005f &&
              _host.GlowProgress.Has(ExpertiseLists.Bubbles, ExpertiseLists.BubbleBoat),
            "a boat is cast on water beside the player, never under them, floats at the surface, and the cast on water is a boat learned: " + Canonical(boat["error"] ?? boat["data"]));
        // Not led, the empty boat floats where it was cast.
        await Frames(120);
        Check(BubbleCenter.DistanceTo(new Vector3(deck.X, WaterY + BubbleLaws.BoatDeckAboveWaterM, deck.Z)) < 0.005f, "an empty boat nobody leads floats where it was cast");
        // Led along the water, it stops short of the player standing between it and its spot.
        var past = Lead(Companion, BubbleOf(boat), new Vector3(-0.37f, WaterY, 0.6f));
        await Frames(300);
        var empty = BubbleCenter;
        Check(Ok(past) && empty.X > deck.X + 0.05f && LevelGap(empty, _player.GlobalPosition) > 0.12f + BubbleLaws.StepInClearanceM,
            $"the Gubble leads the empty boat along the water ({empty.X - deck.X:0.00} m), and it never slides under the player ({LevelGap(empty, _player.GlobalPosition):0.00} m away)");
        // Stepping aboard is the consent.
        await PlayerAt(empty with { Y = 0 });
        await Frames(10);
        Check(_host.PlayerInBubble && _player.Ride == SmallPlayerController.BubbleRide.Aboard && Math.Abs(_player.GlobalPosition.Y - empty.Y) < 0.01f &&
              ReceiptData(Companion, boat)?["carried"]?.GetValue<bool>() == true,
            $"the player steps aboard: the deck holds them ({_player.GlobalPosition.Y:0.000} m) and its receipt says carried");
        // The Gubble leads the occupied boat along the water; the player rides it. A dry spot is no lead for a boat.
        var aboard = _player.GlobalPosition;
        var dry = Lead(Player, BubbleOf(boat), new Vector3(0.3f, 0, 0.6f));
        var ride = Lead(Player, BubbleOf(boat), new Vector3(-1.1f, WaterY, 1.1f));
        var state = await UntilLeadEnds(Player, ride, 900);
        Check(Code(dry) == "invalid_args" && Path(dry) == "$.args.position_m" && Ok(ride) && state == "succeeded" && _player.Ride == SmallPlayerController.BubbleRide.Aboard &&
              LevelGap(_player.GlobalPosition, aboard) > 0.3f && ReceiptData(Player, ride)?["ended"]?.GetValue<string>() == "arrived" && !_player.LastBodyState.InBubble,
            $"a boat is led over water only ({Code(dry)}); the player's lead takes the occupied boat there and the player rides it ({state}, {LevelGap(_player.GlobalPosition, aboard):0.00} m)");
        // The dive key leaves it at once: the boat floats on, and takes the player again only once they have stepped off it.
        await DiveKey();
        await Frames(30);
        Check(!_host.PlayerInBubble && _host.ActiveBubbles.Count == 1 && _player.Ride == SmallPlayerController.BubbleRide.None && ReceiptData(Companion, boat)?["ended"] == null,
            $"the dive key leaves the boat; the boat floats on, and standing inside its rim does not board it again ({_player.Ride})");
        var floating = BubbleCenter;
        await PlayerAt(floating + new Vector3(0.12f + 0.08f, -floating.Y, 0));
        await PlayerAt(floating with { Y = 0 });
        await Frames(6);
        Check(_host.PlayerInBubble && _player.Ride == SmallPlayerController.BubbleRide.Aboard, "stepped off and back on, it holds them again");
        // Walking off the rim leaves it too.
        _player.SetControlInput(new Vector2(1, 0));
        for (var i = 0; i < 120 && _host.PlayerInBubble; i++) await Frames(1);
        _player.SetControlInput(Vector2.Zero);
        await Frames(20);
        Check(!_host.PlayerInBubble && _host.ActiveBubbles.Count == 1 && _player.Ride == SmallPlayerController.BubbleRide.None,
            "walking off the rim leaves the boat, which floats on");
        Wait(61);
        await Frames(2);
        Check(_host.ActiveBubbles.Count == 0 && ReceiptData(Companion, boat)?["ended"]?.GetValue<string>() == "timed_out", "the boat pops at its time, timed_out");
    }

    // ---- the security notes' row (docs/companion/SECURITY.md), each test by its name ----

    private async Task TestSecurityRow()
    {
        await TheDiveKeyPopsItAtAnyMoment();
        await APopInTheAirIsAFall();
        await AFallFromABubbleCanGrabAWall();
        await EveryOtherAbilityStillRefusesThePlayersAvatar();
        await TheCompanionCannotKeepAPlayerInABubbleThePlayerWantsOutOf();
        await ACompanionCastNeverTargetsThePlayerWithoutThePreviewOrTheStepIn();
        await ALeadTargetsOnlyABubbleAndMovesThePlayerOnlyUnderTheCarryException();
    }

    private async Task TheDiveKeyPopsItAtAnyMoment()
    {
        await NewWorld("security-dive", mode: "creative");
        var start = _player.GlobalPosition;
        // As it forms: the dive key held when the Gubble's cast lands pops it straight away.
        _player.SetDiveInput(true, false);
        var held = TheBubbleOnThePlayer(Companion);
        await Frames(2);
        var asItForms = Ok(held) && !_host.PlayerInBubble && ReceiptData(Companion, held)?["ended"]?.GetValue<string>() == "popped_by_player";
        _player.SetDiveInput(false, false);
        await UntilDown();
        // Rising on a lift, at its top, and while a tumble holds control.
        var moments = new (string Name, int Frames, bool Tumble)[] { ("mid-lead", 100, false), ("up on the box", 480, false), ("in a tumble", 160, true) };
        var popped = 0;
        foreach (var (name, frames, tumble) in moments)
        {
            await PlayerAt(start);
            var lift = TheBubbleOnThePlayer(Companion, duration: 60);
            var led = Lead(Companion, BubbleOf(lift), BoxTop);
            await Frames(frames);
            if (tumble) _player.HoldControl(2f);
            var wasHeld = _host.PlayerInBubble;
            await DiveKey();
            if (Ok(lift) && Ok(led) && wasHeld && !_host.PlayerInBubble && ReceiptData(Companion, lift)?["ended"]?.GetValue<string>() == "popped_by_player") popped++;
            else GD.PushError($"Bubble: not popped {name} (held {wasHeld}, {Code(led)})");
            await UntilDown();
        }
        Check(asItForms && popped == moments.Length, $"the dive key pops it at any moment: as it forms ({asItForms}), mid-lead, lifted up on the box, in a tumble ({popped} of {moments.Length})");
    }

    /// <summary>The journal's consequence facts in the Gubble's lessons (journal.read kind consequence) whose line starts so.</summary>
    private int Facts(string prefix) =>
        Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion)["data"]?["entries"]?.AsArray()
            .Count(e => e?["line"]?.GetValue<string>().StartsWith(prefix, StringComparison.Ordinal) == true) ?? 0;

    /// <summary>
    /// Decision 88 (replacing 43's and 60's slow drift): out of a bubble is a fall. Each way a carry pops high in the air (the dive key, X,
    /// its timer, the Gubble's own stop, the Gubble's new goal, the Gubble's recast) leaves the player falling from where they are, with the
    /// landing's fall damage as any fall in Challenge (decision 24); the fall that hurts after the Gubble's own pop is a consequence fact
    /// that costs Trust (decision 96), the timer's and the player's own pops never are, and neither is a Gubble recast the player said
    /// yes to (decision 115: on an island whose Bubbles wait for the player's yes, the player's pop); in Creative nothing hurts.
    /// </summary>
    private async Task APopInTheAirIsAFall()
    {
        var results = new System.Collections.Generic.List<string>();
        var safe = BodyKnacks.None.SafeLandingSpeedMps;
        // Each in a fresh Challenge world (its full pool and Trust at base).
        foreach (var how in new[] { "dive", "x", "timer", "gubble-stop", "gubble-goal", "gubble-recast", "approved-recast" })
        {
            await NewWorld("security-fall-" + how, mode: "challenge");
            // Decision 115: Bubbles that wait for the player's yes, the carry and the recast each approved by the player's click.
            var keyed = how == "approved-recast";
            if (keyed) KeyedBubbles();
            var start = _player.GlobalPosition;
            var health = Health;
            var trust = _host.TrustCount;
            var lift = TheBubbleOnThePlayer(Companion, duration: how == "timer" ? 6 : 60);
            if (keyed) lift = Clicked(lift);
            // A spot above the 1.2 m rise: the lead is still rising when the bubble pops (a new goal pops only a bubble that is led).
            var led = Lead(Companion, BubbleOf(lift), start + new Vector3(0.05f, 1.6f, 0));
            await Frames(60 * 6 - 20);
            var height = _player.GlobalPosition.Y - start.Y;
            if (how == "dive") await DiveKey();
            if (how == "x") _host.PlayerGoal("stop");
            if (how == "gubble-stop") Send(Command(NextId("stop"), "goal.stop", new JsonObject()), Companion);
            if (how == "gubble-goal") Send(Command(NextId("follow"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "follow" }), Companion);
            if (how == "gubble-recast") CastTheBubble(Companion, start + new Vector3(0.25f, 0, 0.35f));
            if (keyed) Clicked(CastTheBubble(Companion, start + new Vector3(0.25f, 0, 0.35f)));
            await Frames(how == "timer" ? 30 : 4);
            var ride = _player.Ride;
            var falling = !_player.IsOnFloor() && _player.Velocity.Y < -0.1f;
            var landing = await UntilDown();
            var facts = Facts(CommandHost.PopFallPrefix);
            var gubbles = how.StartsWith("gubble", StringComparison.Ordinal);
            results.Add($"{how}: {ride} falling {falling} from {height:0.00} m, landed {landing:0.00} m/s, health {health} to {Health}, facts {facts}, Trust {trust} to {_host.TrustCount}, " +
                        $"ended {ReceiptData(Companion, lift)?["ended"]}");
            Check(Ok(lift) && Ok(led) && ride == SmallPlayerController.BubbleRide.None && falling && height > 0.5f && landing > safe && Health < health &&
                  facts == (gubbles ? 1 : 0) && _host.TrustCount == (gubbles ? trust + TrustLaws.PerConsequence : trust),
                $"a pop in the air is a fall ({how}): {results.Last()}");
        }
        var ends = results.Select(r => r[(r.LastIndexOf(' ') + 1)..]).ToArray();
        Check(ends.SequenceEqual(new[] { "popped_by_player", "popped_by_player", "timed_out", "popped", "popped", "popped", "popped_by_player" }),
            "each pop's receipt says how it ended: " + string.Join("; ", results));
        // In Creative nothing hurts, and nothing is the Gubble's to answer for.
        await NewWorld("security-fall-creative", mode: "creative");
        var top = _player.GlobalPosition;
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        Lead(Companion, BubbleOf(carry), top + new Vector3(0.05f, 0.8f, 0));
        await Frames(60 * 6 - 20);
        Send(Command(NextId("stop"), "goal.stop", new JsonObject()), Companion);
        var fell = await UntilDown();
        Check(fell > safe && Health == 100 && Facts(CommandHost.PopFallPrefix) == 0, $"in Creative the same fall hurts no one and writes no fact ({fell:0.00} m/s, health {Health})");
    }

    /// <summary>
    /// Decision 88's wall: popped out beside a climbable face, the player falling and pushing into it grabs it as from a jump, and from
    /// there the bar decides the rest (the climb's own rules); a fall that ends on a face is no landing, so nothing hurts and no fact is the
    /// Gubble's, even after its own pop.
    /// </summary>
    private async Task AFallFromABubbleCanGrabAWall()
    {
        await NewWorld("security-fall-wall", mode: "challenge");
        var start = _player.GlobalPosition;
        // A tall face 7 cm in front of the player (the body faces -Z): the lift rises beside it, clear of it.
        var wall = new StaticBody3D { Name = "Wall", Position = start + new Vector3(0, 0.6f, -0.12f), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.4f, 1.2f, 0.1f) } });
        _world!.AddChild(wall);
        await Frames(2);
        var health = Health;
        var lift = TheBubbleOnThePlayer(Companion, duration: 60);
        var led = Lead(Companion, BubbleOf(lift), start + new Vector3(0, 0.8f, 0));
        await Frames(60 * 6 - 20);
        var height = _player.GlobalPosition.Y - start.Y;
        _player.SetControlInput(new Vector2(0, 1));
        Send(Command(NextId("stop"), "goal.stop", new JsonObject()), Companion);
        for (var i = 0; i < 90 && !_player.IsClimbing; i++) await Frames(1);
        var grabbedAt = _player.GlobalPosition.Y - start.Y;
        Check(Ok(lift) && Ok(led) && height > 0.5f && _player.IsClimbing && grabbedAt > 0.2f && Health == health && Facts(CommandHost.PopFallPrefix) == 0,
            $"popped beside a face, the falling player pushing into it grabs it ({height:0.00} m up, grabbed at {grabbedAt:0.00} m), unhurt, and nothing is the Gubble's");
        // The bar decides the rest: hold on and the climb drains it; let go and it is a fall of the player's own.
        _player.SetControlInput(Vector2.Zero);
        await Frames(30);
        Check(_player.IsClimbing && _host.Vitals.Stamina <= _host.Vitals.MaxStamina, $"holding on, the climb's own rules hold the body ({_host.Vitals.Stamina:0.000} m of bar)");
        _player.SetControlInput(Vector2.Zero, jump: true);
        for (var i = 0; i < 30 && _player.IsClimbing; i++) await Frames(1);
        await UntilDown();
        Check(!_player.IsClimbing && _player.IsOnFloor() && Facts(CommandHost.PopFallPrefix) == 0,
            $"a fall from the face later is the climb's, never the Gubble's pop's (health {Health})");
        wall.QueueFree();
    }

    private async Task EveryOtherAbilityStillRefusesThePlayersAvatar()
    {
        await NewWorld("security-others");
        var feet = _player.GlobalPosition;
        var bloom = Send(Command(NextId("bloom"), "nature.cast", new JsonObject
        {
            ["capability"] = "grow", ["center_m"] = Vec(feet), ["radius_m"] = 0.25, ["duration_s"] = 30, ["target"] = CommandHost.PlayerAvatar,
        }, preview: true), Companion, contractValid: false);
        var fire = Send(Command(NextId("fire"), "nature.cast", new JsonObject
        {
            ["capability"] = "fire", ["center_m"] = Vec(feet), ["radius_m"] = 0.25, ["duration_s"] = 10, ["target"] = CommandHost.PlayerAvatar,
        }, preview: true), Companion, contractValid: false);
        var boat = Send(Command(NextId("boat"), "nature.cast", BubbleArgs("bubble_boat", feet, CommandHost.PlayerAvatar, radius: 0.12), preview: true), Companion, contractValid: false);
        var bubbles = Send(Command(NextId("bubbles"), "effect.start", new JsonObject
        {
            ["capability"] = "bubbles", ["params"] = new JsonObject(), ["area"] = new JsonObject { ["center_m"] = Vec(feet), ["radius_m"] = 0.5 }, ["duration_s"] = 10,
            ["targets"] = new JsonArray(CommandHost.PlayerAvatar),
        }), Companion);
        var glow = Send(Command(NextId("glow"), "effect.start", new JsonObject
        {
            ["capability"] = "glow", ["params"] = new JsonObject(), ["area"] = new JsonObject { ["center_m"] = Vec(feet), ["radius_m"] = 0.5 }, ["duration_s"] = 10,
            ["targets"] = new JsonArray(CommandHost.PlayerAvatar),
        }), Companion);
        var directed = Send(Command(NextId("go"), "goal.set", new JsonObject { ["actor"] = CommandHost.PlayerAvatar, ["goal"] = "go_to", ["position_m"] = Vec(feet + Vector3.Right * 0.3f) }), Companion);
        var outcome = string.Join(" ", new[] { bloom, fire, boat, bubbles, glow, directed }.Select(r => $"{Code(r)}@{Path(r)}"));
        Check(outcome == "request_invalid@$.args.capability request_invalid@$.args.capability request_invalid@$.args.capability permission_denied@$.args.targets " +
                         "permission_denied@$.args.targets actor_denied@$.args.actor" && _player.Ride == SmallPlayerController.BubbleRide.None && _host.ActiveBubbles.Count == 0,
            "every other ability still refuses the player's avatar (Grow, Fire, the boat, the Bubble naming them, Glow) and the companion never directs it: " + outcome);
    }

    private async Task TheCompanionCannotKeepAPlayerInABubbleThePlayerWantsOutOf()
    {
        await NewWorld("security-keep");
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        var led = Lead(Companion, BubbleOf(carry), BoxTop);
        await Frames(60);
        // While it holds the player, nothing the companion sends holds them in: its goals only lead the bubble, and the dive key pops it.
        var go = Send(Command(NextId("go"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "go_to", ["position_m"] = Vec(new Vector3(-0.8f, 0, 0.2f)) }), Companion);
        var poppedByTheGoal = !_host.PlayerInBubble;
        await UntilDown();
        var again = TheBubbleOnThePlayer(Companion, duration: 60);
        await Frames(10);
        await DiveKey();
        var out1 = !_host.PlayerInBubble && ReceiptData(Companion, again)?["ended"]?.GetValue<string>() == "popped_by_player";
        await UntilDown();
        // Turning a ghost away (the dive key while it shows) is a no: its cast is refused, and nothing forms round them.
        CastTheBubble(Companion, _player.GlobalPosition, duration: 60, preview: true);
        var ghostShown = _host.BubbleGhostCount == 1;
        await DiveKey();
        Wait(BubbleLaws.GhostLeadS + 0.1);
        var turnedAway = CastTheBubble(Companion, _player.GlobalPosition, duration: 60);
        // The exit works again on the next offer.
        var later = TheBubbleOnThePlayer(Companion);
        await Frames(10);
        await DiveKey();
        Check(Ok(carry) && Ok(led) && Ok(go) && poppedByTheGoal && out1 && ghostShown && _host.BubbleGhostCount == 0 && Code(turnedAway) == "revision_conflict" && Ok(later) &&
              !_host.PlayerInBubble,
            "the companion cannot keep a player in a bubble the player wants out of: " +
            $"its new goal pops the bubble it leads ({poppedByTheGoal}), the dive key out ({out1}), a ghost turned away ({Code(turnedAway)}), the next offer popped too ({Ok(later)})");
        await UntilDown();
    }

    private async Task ACompanionCastNeverTargetsThePlayerWithoutThePreviewOrTheStepIn()
    {
        await NewWorld("security-consent");
        var feet = _player.GlobalPosition;
        var unpreviewed = Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar)), Companion, contractValid: false);
        var madeUp = Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar, previewId: new string('d', 32))), Companion);
        var atTheFeet = AtPoint(Companion, "bubble_carry", feet);
        var ghost = Preview(Companion);
        var noLead = Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar, previewId: ghost["data"]?["preview_id"]?.GetValue<string>())), Companion);
        var bubbleUnpreviewed = CastTheBubble(Companion, feet);
        // A bubble at a point beside the player stays put, whatever the Gubble does, until the player walks in; led, it stops short of them.
        var beside = CastTheBubble(Companion, feet + new Vector3(0.3f, 0, 0));
        var formed = _host.ActiveBubbles.Count;
        var at = BubbleCenter;
        GubbleAt(feet + new Vector3(-0.2f, 0, 0));
        await Frames(120);
        var stayed = BubbleCenter.DistanceTo(at) < 0.001f;
        var onto = Lead(Companion, BubbleOf(beside), feet);
        await Frames(240);
        var kept = BubbleCenter.DistanceTo(_player.GlobalPosition + Vector3.Up * (_player.BodyHeightM * 0.5f));
        Check(Code(unpreviewed) == "request_invalid" && Code(madeUp) == "revision_conflict" && Code(atTheFeet) == "invalid_args" && Code(noLead) == "not_ready" &&
              Code(bubbleUnpreviewed) == "revision_conflict" && Ok(beside) && formed == 1 && stayed && Ok(onto) && !_host.PlayerInBubble &&
              kept > 0.08f + BubbleLaws.StepInClearanceM && _player.Ride == SmallPlayerController.BubbleRide.None,
            "a companion cast never targets the player without the preview or the walk-in: " +
            $"unpreviewed {Code(unpreviewed)}, a made-up preview {Code(madeUp)}, at their feet {Code(atTheFeet)}, before the ghost's lead {Code(noLead)}, the Bubble unpreviewed " +
            $"{Code(bubbleUnpreviewed)}, beside them it waits ({stayed}), and led onto them it stops short ({kept:0.000} m)");
    }

    /// <summary>
    /// goal.set lead (the security row, extended to it): its target is only a bubble (an effect: id), never an avatar; the companion
    /// leads only a bubble the team sees; the actor is the Gubble; a bubble that holds the player moves only under its lead, and the
    /// dive key or X pops it at any moment, even mid-lead.
    /// </summary>
    private async Task ALeadTargetsOnlyABubbleAndMovesThePlayerOnlyUnderTheCarryException()
    {
        await NewWorld("security-lead");
        var feet = _player.GlobalPosition;
        var spot = feet + new Vector3(0.6f, 0, -0.3f);
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        var id = BubbleOf(carry);
        await Frames(10);
        var onThePlayer = Lead(Companion, CommandHost.PlayerAvatar, spot, contractValid: false);
        var onTheGubble = Lead(Companion, CommandHost.CompanionAvatarId, spot, contractValid: false);
        var onAThing = Lead(Companion, "obj:box", spot, contractValid: false);
        var noSpot = Send(Command(NextId("lead"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "lead", ["target"] = id }), Companion, contractValid: false);
        var unknown = Lead(Companion, "effect:" + new string('a', 26), spot);
        var thePlayersBody = Send(Command(NextId("lead"), "goal.set", new JsonObject
        {
            ["actor"] = CommandHost.PlayerAvatar, ["goal"] = "lead", ["target"] = id, ["position_m"] = Vec(spot),
        }), Companion);
        var outside = Lead(Companion, id, new Vector3(0.6f, 0, 1.7f));
        var refusals = string.Join(" ", new[] { onThePlayer, onTheGubble, onAThing, noSpot, unknown, thePlayersBody, outside }.Select(r => $"{Code(r)}@{Path(r)}"));
        Check(refusals == "request_invalid@$.args.target request_invalid@$.args.target request_invalid@$.args.target request_invalid@$.args.position_m " +
                          "target_not_found@$.args.target actor_denied@$.args.actor out_of_bounds@$.args.position_m" && _player.Ride == SmallPlayerController.BubbleRide.Enveloped,
            "a lead's target is only a bubble, never the player, the Gubble or a thing; it needs a spot inside the island; an unknown bubble is not there; the companion never " +
            "directs the player's body: " + refusals);
        // The player's held body moves only under the lead (consent was the ghost), and the dive key pops it mid-lead.
        var before = _player.GlobalPosition;
        var led = Lead(Companion, id, spot);
        await Frames(60);
        var moved = LevelGap(_player.GlobalPosition, before);
        var midLead = JobState(Companion, led) == "running" && _host.PlayerInBubble;
        await DiveKey();
        var down = !_host.PlayerInBubble;
        await Frames(2);
        Check(Ok(led) && moved > 0.1f && midLead && down && JobState(Companion, led) == "failed" && JobError(Companion, led)?["code"]?.GetValue<string>() == "target_not_found" &&
              ReceiptData(Companion, led)?["ended"]?.GetValue<string>() == "popped_by_player" && ReceiptData(Companion, carry)?["ended"]?.GetValue<string>() == "popped_by_player",
            $"the companion's lead moves the bubble holding the player ({moved:0.00} m), and the dive key pops it mid-lead: the lead failed, its receipt popped_by_player");
        await UntilDown();
        // X mid-lead: the player's stop pops the bubble the Gubble leads, as the player's own pop, whoever cast it.
        var players = TheBubbleOnThePlayer(Player, duration: 60);
        var away = _player.GlobalPosition + new Vector3(-0.8f, 0, 0.1f);
        var ordered = Lead(Player, BubbleOf(players), away);
        var gubbles = Lead(Companion, BubbleOf(players), away + new Vector3(0, 0, -0.2f));
        await Frames(60);
        var x = _host.PlayerGoal("stop");
        Check(Ok(ordered) && Ok(gubbles) && Ok(x) && !_host.PlayerInBubble && x["affected"]?.AsArray().Any(n => n!.GetValue<string>() == BubbleOf(players)) == true &&
              JobState(Player, ordered) == "cancelled" && JobState(Companion, gubbles) == "cancelled" && ReceiptData(Companion, gubbles)?["ended"]?.GetValue<string>() == "stopped" &&
              ReceiptData(Player, players)?["ended"]?.GetValue<string>() == "popped_by_player",
            "the companion may lead the player's bubble too (the Gubble leads every bubble), and X mid-lead pops it as the player's: " + Canonical(ReceiptData(Player, players)));
        await UntilDown();
    }

    // ---- the budgets ----

    private async Task TestBudget()
    {
        await NewWorld("budget");
        // Time the bubbles' tick and the body's ride with a carry the Gubble leads: under 0.5 ms a frame together.
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        Lead(Companion, BubbleOf(carry), _player.GlobalPosition + new Vector3(-1.0f, 0, 0));
        await Frames(10);
        var watch = new Stopwatch();
        const int frames = 240;
        for (var i = 0; i < frames; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            _now += TimeSpan.FromSeconds(Tick);
            watch.Start();
            _host.AdvanceBubbles(Tick);
            watch.Stop();
        }
        var kernelMs = watch.Elapsed.TotalMilliseconds / frames;
        GD.Print($"BUBBLE_BUDGET the bubbles' tick {kernelMs:0.000} ms a frame with {_host.ActiveBubbles.Count} bubble, carrying the player on a lead");
        Check(Ok(carry) && kernelMs < 0.5, $"the bubbles' tick stays under 0.5 ms a frame ({kernelMs:0.000} ms)");
    }
}
