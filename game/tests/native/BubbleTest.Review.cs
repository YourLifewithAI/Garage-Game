using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// What the Wave 2 review of the Bubble and Trust found (its probes, kept as tests): a bubble judged where it forms, so it never
/// forms round the player without the ghost (the blocker); a held bubble climbing rising ground only as a paid lift; a lift under a
/// ceiling ending blocked; a lead earning Trust only as a paid lift that arrives (decision 69), and a lead carrying the player into
/// fire being the Gubble's consequence whoever lit it, moving Trust only when a paid action of its own led there; the companion's
/// lead naming only a bubble the team sees; and one rule for the island's bounds, with
/// a sea and without.
/// </summary>
public partial class BubbleTest
{
    private async Task TestTheReviewsFixes()
    {
        await TestABubbleIsJudgedWhereItForms();
        await TestNoFreeRideUpASlope();
        await TestALiftUnderACeilingEndsBlocked();
        await TestALeadEarnsTrustOnlyAsAPaidLiftThatArrives();
        await TestALeadIntoFireIsTheGubblesConsequence();
        await TestTheCompanionLeadsOnlyABubbleTheTeamSees();
        await TestOneRuleForTheIslandsBounds();
    }

    // ---- the blocker: a bubble aimed under the feet of a player on a box rested on the box top round them ----

    private async Task TestABubbleIsJudgedWhereItForms()
    {
        await NewWorld("review-consent", mode: "challenge", pips: 8);
        Check(_player.TryTeleportTo(new Vector3(0.955f, 0.3f, 0.2f)), "the player stands near the edge of the box's top");
        await Frames(10);
        var feet = _player.GlobalPosition;
        GubbleAt(new Vector3(0.6f, 0f, 0.2f));
        await Frames(4);
        var pips = Pips;
        // 14 cm under the feet, inside the box (its sight box shows on the box's side face): beyond the carry's reach of the feet, but
        // the bubble would rest on the box top round the player's middle.
        var buried = new Vector3(0.935f, feet.Y - 0.14f, feet.Z);
        var bubble = CastTheBubble(Companion, buried);
        await Frames(3);
        Check(Code(bubble) == "revision_conflict" && Path(bubble) == "$.args.area.center_m" && bubble["error"]?["message"]?.GetValue<string>() == CommandHost.GhostFirstWords &&
              !_host.PlayerInBubble && _host.ActiveBubbles.Count == 0 && Pips == pips,
            $"the Gubble's Bubble aimed under the feet of a player on a box, where it would form round them, is a carry: unpreviewed it is refused before the price " +
            $"(held {_host.PlayerInBubble}, {pips - Pips} pips): " + Canonical(bubble["error"] ?? bubble["data"]));
        var point = Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", new Vector3(0.935f, feet.Y - 0.10f, feet.Z), radius: 0.07)), Companion);
        await Frames(3);
        Check(Code(point) == "invalid_args" && Path(point) == "$.args.center_m" && !_host.PlayerInBubble && _host.ActiveBubbles.Count == 0 && Pips == pips,
            $"a nature.cast carry at a point 10 cm under the feet, where it would form round the player, is refused invalid_args before the price (held {_host.PlayerInBubble}): " +
            Canonical(point["error"] ?? point["data"]));
        // The ghost first is the consent: the same spot, previewed, then cast after the ghost's lead, carries them.
        var ghost = CastTheBubble(Companion, buried, preview: true);
        var ghosts = _host.BubbleGhostCount;
        Wait(BubbleLaws.GhostLeadS + 0.05);
        var consented = CastTheBubble(Companion, buried);
        var paid = pips - Pips;
        await Frames(3);
        Check(Ok(ghost) && Kind(ghost) == "carry" && ghosts == 1 && Ok(consented) && Kind(consented) == "carry" && _host.PlayerInBubble && paid == 2,
            $"previewed, the spot shows the ghost round the player, and the cast after it carries them at the carry's price ({paid} pips): " + Canonical(consented["error"] ?? consented["data"]));
        await DiveKey();
        await UntilDown();
        // Beside them on the box top a bubble still waits for the walk-in; the player's own key on the buried spot carries them at once.
        await PlayerAt(new Vector3(1.1f, 0.3f, 0.2f));
        var beside = CastTheBubble(Companion, _player.GlobalPosition + new Vector3(-0.14f, 0, 0));
        await Frames(3);
        var waited = Ok(beside) && Kind(beside) == "walk_in" && !_host.PlayerInBubble;
        _host.PlayerGoal("stop");
        await PlayerAt(feet);
        var own = CastTheBubble(Player, buried);
        await Frames(3);
        Check(waited && Ok(own) && Kind(own) == "carry" && _host.PlayerInBubble,
            $"beside them on the box top it waits for the walk-in ({waited}); the player's own key at the buried spot is a carry at once: " + Canonical(own["error"] ?? own["data"]));
        await DiveKey();
        await UntilDown();
    }

    // ---- the major: a held carry rode a 52 degree ramp up 0.73 m on a lead that was no lift, for no pips ----

    private async Task TestNoFreeRideUpASlope()
    {
        await NewWorld("review-slope", mode: "challenge");
        var feet = _player.GlobalPosition;
        const float degrees = 52f, run = 0.6f;
        var rad = Mathf.DegToRad(degrees);
        var rise = run * Mathf.Tan(rad);
        var ramp = new StaticBody3D { Name = "ReviewRamp", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f, 0.02f, run / Mathf.Cos(rad)) } });
        // Rising toward -Z from 0.12 m north of the feet.
        var startZ = feet.Z - 0.12f;
        ramp.Rotation = new Vector3(rad, 0, 0);
        ramp.Position = new Vector3(feet.X, feet.Y + rise * 0.5f - 0.01f, startZ - run * 0.5f);
        _world!.AddChild(ramp);
        await Frames(4);
        var carry = TheBubbleOnThePlayer(Companion, duration: 30);
        await Frames(30);
        var pips = Pips;
        var startY = _player.GlobalPosition.Y;
        // A floor spot past the ramp, no higher than the bubble: no lift, no price.
        var led = Lead(Companion, BubbleOf(carry), new Vector3(feet.X, feet.Y, startZ - run - 0.12f));
        var top = startY;
        // The job's state every tenth frame (the companion's queries are rate limited), the body's height every frame.
        for (var i = 0; i < 60 * 12 && (i % 10 != 0 || JobState(Companion, led) == "running"); i++)
        {
            await Frames(1);
            top = Math.Max(top, _player.GlobalPosition.Y);
        }
        var rose = top - startY;
        // Pips may refill meanwhile (the Gubble beside the player); none is charged.
        Check(Ok(carry) && Ok(led) && led["data"]?["lift"]?.GetValue<bool>() == false && rose <= BubbleLaws.LiftAboveDriftM + 0.03f && Pips >= pips &&
              ReceiptData(Companion, led)?["lifted"] == null && JobState(Companion, led) == "failed" && ReceiptData(Companion, led)?["ended"]?.GetValue<string>() == "blocked",
            $"a held carry led to a spot past a {degrees} degree ramp ({rise:0.00} m high) on a lead that is no lift climbs no more than the drift's step: it rose {rose:0.000} m " +
            $"({pips} pips, then {Pips}), and the ramp blocked it ({JobState(Companion, led)}, {ReceiptData(Companion, led)?["ended"]}): " + Canonical(led["error"] ?? led["data"]));
        // The step it rose is no foothold: a lead on up the ramp, 15 cm over where it formed but within a step of where it is now, is
        // a lift, paid (no chain of leads climbs for free).
        var onUp = Lead(Companion, BubbleOf(carry), new Vector3(feet.X, 0.15f, startZ - 0.15f / Mathf.Tan(rad)), preview: true);
        Check(_host.PlayerInBubble && Ok(onUp) && onUp["data"]?["lift"]?.GetValue<bool>() == true && onUp["data"]?["lift_price"]?.GetValue<int>() == 3,
            "a lead on up the ramp from the top of the drift's step is a lift at its price, judged from the height the bubble holds with no lift: " + Canonical(onUp["error"] ?? onUp["data"]));
        if (_host.PlayerInBubble) await DiveKey();
        await UntilDown();
    }

    // ---- a lift stopped by a ceiling ran until the bubble's timer ----

    private async Task TestALiftUnderACeilingEndsBlocked()
    {
        await NewWorld("review-ceiling", mode: "challenge");
        var feet = _player.GlobalPosition;
        var slab = new StaticBody3D { Name = "ReviewCeiling", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        slab.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.8f, 0.02f, 0.8f) } });
        slab.Position = feet + new Vector3(0, 0.3f, 0);
        _world!.AddChild(slab);
        await Frames(4);
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        var led = Lead(Companion, BubbleOf(carry), feet + new Vector3(0.05f, 0.9f, 0));
        var state = await UntilLeadEnds(Companion, led, 60 * 10);
        Check(Ok(led) && led["data"]?["lift"]?.GetValue<bool>() == true && state == "failed" && JobError(Companion, led)?["code"]?.GetValue<string>() == "target_unreachable" &&
              ReceiptData(Companion, led)?["ended"]?.GetValue<string>() == "blocked" && ReceiptData(Companion, led)?["lifted"]?.GetValue<bool>() == true && _host.PlayerInBubble &&
              BubbleCenter.Y < feet.Y + 0.3f,
            $"a lift a ceiling stops ends blocked within {BubbleLaws.LeadBlockedAfterS} s of stopping, the player still held under it ({state}, {ReceiptData(Companion, led)?["ended"]})");
        await DiveKey();
        await UntilDown();
    }

    // ---- MINOR 4, narrowed by decision 69: a lead earns Trust only as a paid lift (its Lift charged at the rise) that arrives ----

    private async Task TestALeadEarnsTrustOnlyAsAPaidLiftThatArrives()
    {
        await NewWorld("review-lead-trust", mode: "challenge", pips: 8);
        var feet = _player.GlobalPosition;
        var spot = feet + new Vector3(0.65f, 0, -0.4f);
        // The player's bubble, led by the Gubble, popped at once by the player: the lead fails (the bubble popped before it got there).
        var first = CastTheBubble(Player, feet + new Vector3(0.3f, 0, 0.1f));
        var failing = Lead(Companion, BubbleOf(first), spot);
        await Frames(2);
        Send(Command(NextId("stop"), "effect.stop", new JsonObject { ["effect"] = BubbleOf(first) }), Player);
        await Frames(2);
        var failed = JobState(Companion, failing);
        Wait(61);
        _host.SettleTrust();
        Check(Ok(failing) && failed == "failed" && _host.TrustCount == 0,
            $"the Gubble's lead whose bubble popped at once earns nothing: it changed nothing in the world ({failed}, Trust {_host.TrustCount})");
        // A level lead that gets there moved the bubble, but a lead is a goal and costs nothing: decision 69, it earns nothing.
        var second = CastTheBubble(Player, feet + new Vector3(0.3f, 0, 0.1f));
        var pips = Pips;
        var arriving = Lead(Companion, BubbleOf(second), spot);
        var state = await UntilLeadEnds(Companion, arriving);
        Wait(61);
        _host.SettleTrust();
        Check(Ok(arriving) && arriving["data"]?["lift"]?.GetValue<bool>() == false && state == "succeeded" && Pips == pips && _host.TrustCount == 0,
            $"a lead that arrives with no lift cost no pips, so earns nothing (decision 69; {state}, Trust {_host.TrustCount})");
        // The Gubble's lift of the player's own carry: charged at the rise, then it arrives; its window opens on arrival and gains when it
        // closes clean. The carry is the player's, so the only paid action of the Gubble's here is the lead's Lift.
        await NewWorld("review-lift-trust", mode: "challenge", pips: 8);
        var carry = TheBubbleOnThePlayer(Player, duration: 60);
        await Frames(30);
        var atSet = Pips;
        var lifting = Lead(Companion, BubbleOf(carry), BoxTop);
        var lifted = await UntilLeadEnds(Companion, lifting);
        Wait(30);
        _host.SettleTrust();
        var midWindow = _host.TrustCount;
        Wait(31);
        _host.SettleTrust();
        Check(Ok(carry) && Ok(lifting) && lifting["data"]?["lift"]?.GetValue<bool>() == true && lifted == "succeeded" && Pips == atSet - 3 && midWindow == 0 &&
              _host.TrustCount == TrustLaws.CleanInitiative,
            $"the Gubble's lift, charged 3 at the rise, that arrives opens its window on arrival and gains when it closes clean ({lifted}; {atSet - Pips} pips; " +
            $"{midWindow} at 30 s, {_host.TrustCount} at 61 s)");
    }

    // ---- MINOR 5: a Gubble lead carrying the player into fire that hurts them, whoever lit it ----

    private static NatureCell? Soil(int x, int z) => new() { X = x, Z = z, Y = 0, Surface = "shell:soil", Material = "grass", Fertile = true, Fuel = 0.7f, Moisture = 0.35f };

    private async Task TestALeadIntoFireIsTheGubblesConsequence()
    {
        // The Gubble's own carry (paid: 2 pips) and its lead: the harm follows directly from a paid action of its own (decision 69).
        await LeadIntoFire("review-lead-fire", null, paid: true);
        // The same harm when the Gubble's carry is free (a pack pricing the carry at nothing) and its lead no lift: a consequence of free
        // actions, written in the journal as ever, moving no Trust.
        await LeadIntoFire("review-lead-fire-free", pack => pack["cost"]!["prices"]!["bubble_carry"]!["ordered"] = 0, paid: false);
    }

    /// <summary>The Gubble's carry led by the Gubble into the player's fire: one consequence fact, and -3 only when paid traces it to a paid action.</summary>
    private async Task LeadIntoFire(string world, Action<JsonObject>? editPack, bool paid)
    {
        await NewWorld(world, mode: "challenge", pips: 8);
        if (editPack != null)
        {
            var pack = JsonNode.Parse(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5))))!.AsObject();
            editPack(pack);
            _host.UseRules(IslandRules.Parse(System.Text.Encoding.UTF8.GetBytes(pack.ToJsonString())));
        }
        var cost = TrustLaws.PerConsequence * (paid ? 1 : 0);
        _host.ConfigureNature();
        _host.NatureSampleForTests = Soil;
        _host.SetGubbleFire(true);
        // Decision 72: the team knows Fire, as after the first rest (the one helper every suite that lights fires uses).
        _host.LearnFireForTests();
        var feet = _player.GlobalPosition;
        var health = Health;
        var carry = TheBubbleOnThePlayer(Companion, duration: 60);
        await Frames(30);
        // The player's own fire, a little way off.
        var at = new Vector3(feet.X - 0.45f, 0.01f, feet.Z);
        var fireArgs = new JsonObject { ["capability"] = "fire", ["center_m"] = Vec(at), ["radius_m"] = 0.1, ["duration_s"] = 10 };
        var preview = Send(Command(NextId("fire-preview"), "nature.cast", fireArgs, preview: true), Player);
        var lit = Send(Command(NextId("fire"), "nature.cast", new JsonObject
        {
            ["capability"] = "fire", ["center_m"] = Vec(at), ["radius_m"] = 0.1, ["duration_s"] = 10, ["preview_id"] = preview["data"]?["preview_id"]?.GetValue<string>(),
        }), Player);
        var led = Lead(Companion, BubbleOf(carry), at);
        for (var i = 0; i < 60 * 10 && Health >= health; i++)
        {
            await Frames(1);
            _host.AdvanceNature(Tick);
        }
        // More harm from the same lead writes nothing more.
        for (var i = 0; i < 60; i++)
        {
            await Frames(1);
            _host.AdvanceNature(Tick);
        }
        var read = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion);
        var facts = read["data"]?["entries"]?.AsArray().OfType<JsonObject>()
            .Where(e => e["line"]!.GetValue<string>().StartsWith("The Gubble's bubble carried you into fire", StringComparison.Ordinal)).ToList() ?? new();
        // Decision 134: the Gubble hears it on its next result (this one), in the fact's words, with the Trust it moved.
        var heard = read["notices"]?.AsArray().OfType<JsonObject>().Where(n => n["kind"]?.GetValue<string>() == CommandHost.ConsequenceNoticeKind).ToList() ?? new();
        Check(heard.Count == 1 && facts.Count == 1 && heard[0]["line"]?.GetValue<string>() == facts[0]["line"]?.GetValue<string>() && heard[0]["trust_change"]?.GetValue<int>() == cost,
            $"the Gubble hears its lead's consequence in the fact's own words, trust_change {cost}: " + Canonical(read["notices"]));
        var hurt = Health < health;
        var afterHarm = _host.TrustCount;
        if (_host.PlayerInBubble) await DiveKey();
        Wait(61);
        _host.SettleTrust();
        Check(Ok(carry) && Ok(lit) && Ok(led) && hurt && facts.Count == 1 && facts[0]["actor"]?.GetValue<string>() == Companion && facts[0]["pin_m"] != null &&
              afterHarm == cost && _host.TrustCount == cost && Pips == (paid ? 4 : 6),
            (paid ? "the Gubble's lead carrying the player, in the carry it paid for, into the player's own fire writes one consequence fact, costs 3 and earns nothing"
                  : "the Gubble's free carry, led (no lift) into the player's own fire, writes the same consequence fact and moves no Trust (decision 69)") +
            $" (hurt {hurt}, {facts.Count} fact, Trust {afterHarm} then {_host.TrustCount}, {Pips} pips): " + Canonical(facts.FirstOrDefault()));
    }

    // ---- MINOR 6: the companion's lead names only a bubble the team sees (CommandHostBubbleLead's sight check) ----

    private async Task TestTheCompanionLeadsOnlyABubbleTheTeamSees()
    {
        await NewWorld("review-lead-sight");
        var home = _player.GlobalPosition;
        // East of the box, the player blows a bubble behind it (seen from where they stand)...
        await PlayerAt(new Vector3(1.75f, 0, 0.15f));
        GubbleAt(new Vector3(1.75f, 0, 0.45f));
        await Frames(4);
        var hidden = CastTheBubble(Player, new Vector3(1.45f, 0, 0.15f));
        // ...then both go back west of the box, which hides it from them.
        await PlayerAt(home);
        GubbleAt(_room.SpawnFor("companion", _room.SpawnFor("player")).PositionM);
        await Frames(6);
        var spot = new Vector3(1.6f, 0, 0.7f);
        var byTheGubble = Lead(Companion, BubbleOf(hidden), spot);
        var byThePlayer = Lead(Player, BubbleOf(hidden), spot, preview: true);
        Check(Ok(hidden) && Kind(hidden) == "walk_in" && Code(byTheGubble) == "target_not_found" && Path(byTheGubble) == "$.args.target" && byTheGubble["job_id"] == null &&
              Ok(byThePlayer) && _host.ActiveBubble?.LedToM == null,
            $"the companion leads only a bubble the team sees: one hidden behind the box answers as one that is not there ({Code(byTheGubble)}); the player's own order may lead it " +
            $"({Code(byThePlayer) ?? "ok"})");
    }

    // ---- MINOR 8: one rule for the island's bounds in the lead, the cast and every effect, with a sea and without ----

    private async Task TestOneRuleForTheIslandsBounds()
    {
        var outcome = new System.Collections.Generic.List<string>();
        foreach (var sea in new[] { false, true })
        {
            await NewWorld(sea ? "review-bounds-sea" : "review-bounds", room: sea ? SeaRoom() : null);
            var feet = _player.GlobalPosition;
            var bubble = CastTheBubble(Player, feet + new Vector3(0.3f, 0, 0.1f));
            var pastTheSide = Lead(Companion, BubbleOf(bubble), new Vector3(2.3f, 0, 0.6f), preview: true);
            var overTheTop = Lead(Companion, BubbleOf(bubble), new Vector3(0.6f, 2.5f, 0.6f), preview: true);
            JsonObject Glow(Vector3 at) => Send(Command(NextId("glow"), "effect.start", new JsonObject
            {
                ["capability"] = "glow", ["params"] = new JsonObject(), ["area"] = new JsonObject { ["center_m"] = Vec(at), ["radius_m"] = 0.5 }, ["duration_s"] = 10,
            }), Companion);
            var glowOverTheTop = Glow(new Vector3(feet.X, 2.5f, feet.Z));
            var glowPastTheSide = Glow(new Vector3(0.6f, 0, 1.6f));
            string Say(JsonObject r) => Ok(r) ? "ok" : $"{Code(r)}:{r["error"]?["message"]?.GetValue<string>()}";
            outcome.Add($"{(sea ? "sea" : "no sea")}: lead past the side {Say(pastTheSide)}; lead over the top {Say(overTheTop)}; glow over the top {Say(glowOverTheTop)}; " +
                        $"glow past the side {Say(glowPastTheSide)}");
            var outside = "That spot is outside the room.";
            Check(Ok(bubble) && (sea ? Ok(pastTheSide) : Code(pastTheSide) == "out_of_bounds") && Code(overTheTop) == "out_of_bounds" &&
                  Code(glowOverTheTop) == "out_of_bounds" && glowOverTheTop["error"]?["message"]?.GetValue<string>() == outside &&
                  (sea ? Code(glowPastTheSide) != "out_of_bounds" : Code(glowPastTheSide) == "out_of_bounds" && glowPastTheSide["error"]?["message"]?.GetValue<string>() == outside),
                "one rule for the island's bounds, the body's own: without a sea the room's bounds hold every side; round an island the sea has no edge, so only the top and " +
                "the bottom hold, for a lead and an effect alike: " + outcome.Last());
        }
    }

    /// <summary>The test room copied under the test folder with a sea round it (x_landscape_sea, its level under the floor).</summary>
    private RoomData SeaRoom()
    {
        var source = ProjectSettings.GlobalizePath(RoomWorld.DefaultRoom);
        var target = ProjectSettings.GlobalizePath($"{TestRoot}/sea/test_room");
        foreach (var file in System.IO.Directory.GetFiles(source, "*", System.IO.SearchOption.AllDirectories).Where(f => !f.EndsWith(".import", StringComparison.Ordinal) && !f.EndsWith(".uid", StringComparison.Ordinal)))
        {
            var destination = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            System.IO.File.Copy(file, destination, true);
        }
        var path = System.IO.Path.Combine(target, "room.json");
        var manifest = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        static JsonArray Loop(double x0, double x1, double z0, double z1) => new(new JsonArray(x0, z0), new JsonArray(x1, z0), new JsonArray(x1, z1), new JsonArray(x0, z1));
        manifest["extensions"] = new JsonObject
        {
            [RoomSea.ExtensionName] = new JsonObject
            {
                ["level_m"] = -0.05, ["swim_depth_m"] = 0.08,
                ["coast"] = new JsonObject { ["outline_m"] = Loop(-2, 2, -1.5, 1.5) },
                ["reef"] = new JsonObject { ["outline_m"] = Loop(-6, 6, -5.5, 5.5), ["crest_y_m"] = -0.03, ["band_half_width_m"] = 0.5, ["passes"] = new JsonArray() },
                ["play_area"] = new JsonObject { ["outline_m"] = Loop(-12, 12, -11.5, 11.5) },
                ["beaches"] = new JsonArray(),
            },
        };
        System.IO.File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        var room = RoomData.Load($"{TestRoot}/sea/test_room");
        Check(room.Sea != null, "the copied test room has a sea round it");
        return room;
    }
}
