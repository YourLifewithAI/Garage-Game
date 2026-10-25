using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Companion;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Trust (M8 of docs/runs/RUN-2-MECHANICS.md; docs/game/MECHANICS.md section 5 and decisions 1, 6, 16, 42 and 45), in the real
/// test room with the real bodies, nature and v5's prices: the count from clean initiative, consequence facts and the player's
/// marks (item 1), since decision 69 only for paid actions (what the guard charged) and the consequences traced to them, a free
/// action's consequence written and moving no Trust; a consequence fact for a build the Gubble's fire burnt (item 2); the bands' discount and early warning, the fire
/// guardrails untouched (item 3); receipt.mark and trust.reset the player's alone (item 4); "lesson applied" from the host's own
/// observation, Trust readable and never writable (item 5); the journal's Trust words and the aura's band (item 6).
/// </summary>
public partial class NatureTest
{
    private async Task TestTrust()
    {
        TestTrustRules();
        await TestTrustFromInitiative();
        await TestTrustFromMarks();
        await TestBuildBurntIsAConsequence();
        await TestAFreeActionsConsequenceMovesNoTrust();
        await TestTrustBands();
        await TestLessonApplied();
        await TestTrustIsNeverTheCompanions();
        await TestAPreviewOrAHoldIsNoInitiative();
        await TestAFetchIsFreeAndEarnsNothing();
    }

    private JsonObject GubbleStays(string? actionId = null) =>
        Send(Command(actionId ?? NextId("gubble"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }), Companion);

    /// <summary>A command of the Gubble's the guard charges pips for (decision 69): its own Glow on itself, v5's own-idea price of one pip.</summary>
    private JsonObject GubbleGlows() => GlowSelf(Companion);

    /// <summary>The pool full again (eight pips), for the checks that spend more than one pool.</summary>
    private void FullPool() =>
        _host.ConfigurePlayerVitals(new EnergyStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(_host.BodyRules.Fresh().With(pips: 8))));

    /// <summary>v5 as the host's rules, edited as JSON first (a price, a tier): the pack's prices decide what the guard charges.</summary>
    private void UseEditedV5(Action<JsonObject> edit)
    {
        var pack = JsonNode.Parse(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5))))!.AsObject();
        edit(pack);
        _host.UseRules(IslandRules.Parse(System.Text.Encoding.UTF8.GetBytes(pack.ToJsonString())));
    }

    private static void Price(JsonObject pack, string capability, int ordered) => pack["cost"]!["prices"]![capability]!["ordered"] = ordered;

    private int _gubbleBuilds;

    /// <summary>
    /// A command of the Gubble's that changes the world and costs no pips (a free action: decision 69 says it never gains): a small
    /// wooden build of its own on the open floor north of the box, each at a spot of its own, far from where the tests light fires.
    /// </summary>
    private JsonObject GubbleBuilds()
    {
        var n = _gubbleBuilds++;
        var spot = new JsonArray(0.8 + 0.22 * (n % 4), 0, -0.45 - 0.22 * (n / 4 % 3));
        return Send(Command(NextId("gubble-build"), "creation.place", new JsonObject { ["source"] = Source($"Gubble's post {n}"), ["placement"] = new JsonObject { ["position_m"] = spot } }), Companion);
    }

    private JsonObject Mark(string gubbleActionId, string mark, string? actionId = null, string principal = Player, bool preview = false) =>
        Send(Command(actionId ?? NextId("mark"), "receipt.mark", new JsonObject { ["action_id"] = gubbleActionId, ["mark"] = mark }, preview), principal);

    private JsonObject ResetTrust(string principal = Player) => Send(Command(NextId("reset"), "trust.reset", new JsonObject()), principal);

    /// <summary>The journal's lessons (kind consequence) as the companion reads them, newest first.</summary>
    private List<string> Lessons() =>
        Send(Query("journal.read", new JsonObject { ["kind"] = "consequence", ["limit"] = 50 }), Companion)["data"]?["entries"]?.AsArray()
            .Select(e => e?["line"]?.GetValue<string>() ?? "").ToList() ?? new List<string>();

    /// <summary>Real time passes (the host's clock), then Trust's clock runs, as the companion bridge runs it each frame.</summary>
    private void Later(double seconds)
    {
        _now += TimeSpan.FromSeconds(seconds);
        _host.SettleTrust();
    }

    /// <summary>A team file under the test folder with this Trust count, served to the host (as the boot's UseTeam does).</summary>
    private TeamStore TrustTeam(string name, int trust)
    {
        var path = ProjectSettings.GlobalizePath($"{TestRoot}/{name}/team.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        var store = new TeamStore(path, "trust_team");
        store.Load();
        store.Save(TeamFile.Create("trust_team", new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc), "Mira", "prototype", "Pip", "#65b9b0").WithTrust(trust));
        var served = new TeamStore(path, "trust_team");
        _host.UseTeam(served);
        return served;
    }

    // ---- the rules themselves ----

    private void TestTrustRules()
    {
        Check(new[] { -7, 0, 4, 5, 14, 15, 29, 30, 900 }.Select(Trust.Band).SequenceEqual(new[] { 0, 0, 0, 1, 1, 2, 2, 3, 3 }) && Trust.Tier(-1) == -1 && Trust.Tier(0) == 0,
            "the bands open at 5, 15 and 30; below base the words say there is trust to win back");
        var prices = Enumerable.Range(0, 4).Select(band => (Trust.OwnIdeaPrice(2, 0.5, band), Trust.OwnIdeaPrice(4, 0.5, band), Trust.OwnIdeaPrice(6, 0.5, band))).ToList();
        Check(prices.SequenceEqual(new[] { (1, 2, 3), (1, 2, 2), (1, 1, 2), (1, 1, 1) }) && Trust.OwnIdeaPrice(0, 0.5, 3) == 0,
            "the bands widen the own-idea discount in whole pips, never below one for a priced ability: " + string.Join(" ", prices));
        var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new TrustLedger();
        TrustLedger.Entry Act(string id, double at, bool counted = true, bool paid = true) =>
            new() { ActionId = id, Op = "effect.start", Capability = "glow", What = "glow", At = t0.AddSeconds(at), Counted = counted, Paid = paid };
        ledger.Note(Act("a", 0));
        ledger.Note(Act("b", 10));
        ledger.Note(Act("c", 30));
        ledger.Note(Act("d", 130));
        ledger.Note(Act("e", 200, counted: false));
        ledger.Note(Act("g", 260, paid: false));
        ledger.Consequence(t0.AddSeconds(85));
        var settled = ledger.Settle(t0.AddSeconds(400)).Select(s => $"{s.Entry.ActionId}:{(s.Clean ? "clean" : "spoiled")}:{(s.Gained ? "+1" : "0")}").ToList();
        Check(settled.SequenceEqual(new[] { "a:clean:+1", "b:clean:0", "c:spoiled:0", "d:clean:+1", "e:clean:0", "g:clean:0" }) && ledger.Settle(t0.AddSeconds(400)).Count == 0,
            "a paid window closing clean gains, at most once in two minutes; a consequence inside a window spoils it; Creative's actions and free ones never gain; each window settles once: " + string.Join(" ", settled));
        // Decision 69's rule, from what the host charged: anything above zero pips is paid, nothing else.
        Check(Trust.Paid(1) && Trust.Paid(3) && !Trust.Paid(0) && !Trust.Paid(-1) && ledger.WasPaid("a") && !ledger.WasPaid("g"),
            "decision 69: a command is a paid action when the host charged it pips, and only then is it remembered as paid");
        ledger.Note(Act("f", 400));
        ledger.Reset();
        Check(ledger.Settle(t0.AddSeconds(900)).Count == 0 && ledger.LastGain == null && ledger.Find("f") != null, "the player's reset closes every open window with no gain; the receipts can still be marked");
        // A goal (the W2 review's minor 4, then decision 69): its window waits for its job, opens when the job succeeds, and a job that
        // fails or is stopped gains nothing; a consequence while the job runs spoils it. A goal costs nothing at its commit: a lead gains
        // only once its Lift is charged (Charged), a fetch never.
        var jobs = new TrustLedger();
        TrustLedger.Entry Goal(string id, double at, string goal = "lead") =>
            new() { ActionId = id, Op = "goal.set", Capability = goal, What = goal, At = t0.AddSeconds(at), Counted = true, JobId = "job-" + id, Pending = true };
        jobs.Note(Goal("fails", 0));
        jobs.Note(Goal("spoiled", 0));
        jobs.Charged("fails");
        jobs.Charged("spoiled");
        var waiting = jobs.Settle(t0.AddSeconds(100)).Count;
        jobs.JobEnded("job-fails", false, t0.AddSeconds(5));
        jobs.Consequence(t0.AddSeconds(10));
        jobs.JobEnded("job-spoiled", true, t0.AddSeconds(20));
        jobs.Note(Goal("arrives", 30));
        jobs.Note(Goal("no-lift", 30));
        jobs.Note(Goal("fetched", 30, "fetch"));
        jobs.Charged("arrives");
        jobs.JobEnded("job-arrives", true, t0.AddSeconds(100));
        jobs.JobEnded("job-no-lift", true, t0.AddSeconds(100));
        jobs.JobEnded("job-fetched", true, t0.AddSeconds(100));
        var early = jobs.Settle(t0.AddSeconds(150)).Select(s => s.Entry.ActionId).ToList();
        var late = jobs.Settle(t0.AddSeconds(161)).Select(s => $"{s.Entry.ActionId}:{(s.Gained ? "+1" : "0")}").ToList();
        Check(waiting == 0 && early.SequenceEqual(new[] { "fails", "spoiled" }) && late.SequenceEqual(new[] { "arrives:+1", "no-lift:0", "fetched:0" }),
            $"a goal's window waits for its job: a failed job gains nothing, a consequence while it ran spoils it, a success opens the window then, and only a " +
            $"charged lead gains ({waiting} settled while running; {string.Join(" ", early)} settled by 150 s; {string.Join(" ", late)})");
        // The trace (decision 69): a fire by the revision its paid cast committed at; a paid action stays traceable past the commands the
        // ledger forgets, since only paid ones enter its list of paid actions.
        var trace = new TrustLedger();
        trace.Note(new TrustLedger.Entry { ActionId = "fire-1", Op = "nature.cast", Capability = "fire", What = "fire", At = t0, Counted = true, Paid = true, Revision = 7 });
        trace.Note(new TrustLedger.Entry { ActionId = "fire-2", Op = "nature.cast", Capability = "fire", What = "fire", At = t0, Counted = true, Paid = false, Revision = 9 });
        for (var i = 0; i < TrustLaws.MaxRemembered + 10; i++)
            trace.Note(new TrustLedger.Entry { ActionId = $"stay-{i}", Op = "goal.set", Capability = "stay", What = "move", At = t0, Counted = true });
        Check(trace.Find("fire-1") == null && trace.PaidFireAt(7) && !trace.PaidFireAt(9) && !trace.PaidFireAt(8) && trace.WasPaid("fire-1") && !trace.WasPaid("fire-2"),
            "a paid fire is traced by its revision even after a thousand free commands pushed it out of the remembered ones; a free fire never is");
    }

    // ---- item 1: clean initiative, spaced; consequences ----

    private async Task TestTrustFromInitiative()
    {
        await EnergyWorld("trust-initiative");
        // Decision 69: "Only paid actions and consequences directly related to those actions earn trust." A build changes the world
        // and costs nothing, so it opens no window.
        var builds = new[] { GubbleBuilds(), GubbleBuilds() };
        Later(61);
        Check(builds.All(Ok) && Pips == 8 && _host.TrustCount == 0,
            $"the Gubble's builds change the world but cost no pips: free actions gain nothing (decision 69; {Pips} pips, Trust {_host.TrustCount})");
        var first = GubbleGlows();
        Later(30);
        Check(Ok(first) && Pips == 7 && _host.TrustCount == 0, $"a paid action of the Gubble's (its Glow, one pip): its window is still open at 30 s, nothing yet ({Code(first) ?? "ok"})");
        Later(31);
        Check(_host.TrustCount == 1, $"its next 60 s produced no consequence fact: +1 ({_host.TrustCount})");
        GubbleGlows();
        Later(61);
        Check(_host.TrustCount == 1, "a second clean window within two minutes of the last gain gains nothing");
        GubbleGlows();
        Later(61);
        Check(_host.TrustCount == 2, $"once two minutes have passed since the last gain, a clean window gains again ({_host.TrustCount})");
        GubbleGlows(); // still the Gubble's: a stop, a note or a checkpoint is no action, and opens no window
        Send(Command(NextId("note"), "journal.note", new JsonObject { ["text"] = "Staying put." }), Companion);
        Send(Command(NextId("stop"), "goal.stop", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }), Companion);
        var player = Send(Command(NextId("order"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }), Player);
        Check(Ok(player) && _host.TrustRemembered == 6, $"the player's own orders, the Gubble's stops and notes are not its initiative ({_host.TrustRemembered} remembered)");
        // The founder, 16 October: "Trust shouldn't be impacted by simple directional commands, only things that impact the world."
        _now += TimeSpan.FromSeconds(200);
        var moves = new[]
        {
            GubbleStays(),
            Send(Command(NextId("follow"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "follow" }), Companion),
            Send(Command(NextId("go-to"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "go_to", ["position_m"] = new JsonArray(0.6, 0, 1.0) }), Companion),
        };
        Later(61);
        Check(moves.All(Ok) && _host.TrustCount == 2 && _host.TrustRemembered == 9,
            $"a stay, a follow and a go_to of the Gubble's gain nothing, two minutes clear of the last gain ({_host.TrustCount}); they are remembered for a mark");
        GubbleGlows();
        Later(61);
        Check(_host.TrustCount == 3, $"a Glow of its own at the same point gains: the directional ones gained nothing for their own sake ({_host.TrustCount})");

        // The Gubble's paid fire hurts the player: the consequence fact costs 3 and spoils every window open over it, its own included.
        _now += TimeSpan.FromSeconds(200);
        FullPool();
        var before = _host.TrustCount;
        var glowed = GubbleGlows();
        var lit = Fire(Companion, PlayerSpot(), 0.25, 6, out _);
        Steps(2);
        Check(Ok(lit) && Pips == 6 && _host.TrustCount == before - 3, $"a consequence fact of the Gubble's paid fire (one pip, decision 94) costs 3 ({before} to {_host.TrustCount}, {Pips} pips left)");
        Later(61);
        Check(Ok(glowed) && _host.TrustCount == before - 3, "the windows the consequence fell in (the fire's and the Glow before it) gain nothing");
        Check(Lessons().Any(l => l.StartsWith("The Gubble's fire burned you", StringComparison.Ordinal)), "the consequence fact is the journal's, as before M8");

        // Decision 45 under 69: a suggestion of the Gubble's the player approves is still its own idea; it earns initiative only when
        // the click's commit charged pips. An approved removal is free and gains nothing.
        await EnergyWorld("trust-approved");
        var shed = Build("Approved shed", 1.5, 1.2);
        var asked = Send(Command(NextId("remove"), "entity.remove", new JsonObject { ["target"] = shed }, expectedRevision: _host.Revision), Companion);
        var approved = asked["approval_needed"]?["request_id"]?.GetValue<string>() is { } request ? _host.Approve(request) : new JsonObject();
        Later(61);
        Check(Code(asked) == "approval_required" && Ok(approved) && approved["approved_by"]?.GetValue<string>() == Player && _host.TrustCount == 0,
            $"the player's click commits the Gubble's suggestion as its own idea; a removal costs no pips, so it gains nothing ({_host.TrustCount})");
        // A Glow held for the click (a keyed_yes pack) is paid when the click commits it: that one gains if nothing follows.
        UseEditedV5(pack => pack["abilities"]!.AsArray().OfType<JsonObject>().First(a => a["capability"]!.GetValue<string>() == "glow")["tier"] = "keyed_yes");
        var held = GubbleGlows();
        var paid = _host.PendingApprovals.FirstOrDefault()?.RequestId is { } glowRequest ? _host.Approve(glowRequest) : new JsonObject();
        Later(61);
        Check(Code(held) == "approval_required" && Ok(paid) && paid["approved_by"]?.GetValue<string>() == Player && Pips == 7 && _host.TrustCount == TrustLaws.CleanInitiative,
            $"a held Glow of the Gubble's, charged its pip at the player's click, is a paid action of its own idea: +1 once its window closes clean ({Pips} pips, Trust {_host.TrustCount})");

        // From what the host charged, never a list: a pack that prices Glow at nothing makes the same Glow free, and it gains nothing.
        await EnergyWorld("trust-unpriced");
        UseEditedV5(pack => Price(pack, "glow", 0));
        var unpriced = GubbleGlows();
        Later(61);
        Check(Ok(unpriced) && Pips == 8 && _host.TrustCount == 0, $"a Glow the pack prices at nothing is charged nothing, so it gains nothing ({Code(unpriced) ?? "ok"}, {Pips} pips, Trust {_host.TrustCount})");

        // Creative counts nothing: a clean window gains nothing there.
        await NewWorld(SavePath("trust-creative"));
        var creative = GubbleGlows();
        Later(61);
        Check(Ok(creative) && _host.Mode == "creative" && _host.TrustCount == 0, $"in Creative Trust is not counted ({Code(creative) ?? "ok"})");
    }

    // ---- item 4 (and 1): the player's marks and reset ----

    private async Task TestTrustFromMarks()
    {
        await EnergyWorld("trust-marks");
        GubbleStays("gubble-stay-1");
        GubbleStays("gubble-stay-2");
        var playerOrder = Send(Command("player-order-1", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }), Player);
        var preview = Mark("gubble-stay-1", "yes", preview: true);
        Check(Ok(preview) && preview["preview"]?.GetValue<bool>() == true && _host.TrustCount == 0, "a mark's preview changes nothing");
        var yes = Mark("gubble-stay-1", "yes", "mark-1");
        Check(Ok(yes) && yes["transient"]?.GetValue<bool>() == true && yes["data"] == null && _host.TrustCount == TrustLaws.MarkYes,
            $"the player's \"yes, like that\" on a receipt of the Gubble's: +2, a transient receipt with no data ({_host.TrustCount})");
        var replay = Mark("gubble-stay-1", "yes", "mark-1");
        Check(replay["replayed"]?.GetValue<bool>() == true && _host.TrustCount == 2, "the same mark sent again replays its receipt and counts once");
        var again = Mark("gubble-stay-1", "not_that");
        Check(Code(again) == "invalid_args" && again["error"]?["field_path"]?.GetValue<string>() == "$.args.action_id" && again["error"]?["message"]?.GetValue<string>() == CommandHost.MarkedOnce &&
            _host.TrustCount == 2, "each receipt is marked once");
        var notThat = Mark("gubble-stay-2", "not_that");
        Check(Ok(notThat) && _host.TrustCount == 0, $"\"not that\": -2 ({_host.TrustCount})");
        var own = Mark("player-order-1", "yes");
        var unknown = Mark("no-such-receipt", "yes");
        Check(Ok(playerOrder) && Code(own) == "target_not_found" && Code(unknown) == "target_not_found" && own["error"]?["message"]?.GetValue<string>() == CommandHost.MarkNotFound &&
            own["error"]?["field_path"]?.GetValue<string>() == "$.args.action_id", "only a receipt of the Gubble's can be marked: the player's own, or none, is not found");
        var lessons = Lessons();
        Check(lessons.Contains("You marked the Gubble's move: yes, like that.") && lessons.Contains("You marked the Gubble's move: not that."),
            "the journal keeps each mark in the game's words, as one of the AI's lessons: " + string.Join(" | ", lessons));
        GubbleStays("gubble-stay-3");
        Mark("gubble-stay-3", "yes");
        Mark(GubbleStays()["action_id"]!.GetValue<string>(), "yes");
        Mark(GubbleStays()["action_id"]!.GetValue<string>(), "yes");
        Check(_host.TrustCount == 6 && _host.TrustBand == 1 && Lessons().First() == "Trust grew: you trust the Gubble a little.",
            $"a band reached is told in words, never a number ({_host.TrustCount}, band {_host.TrustBand}): " + Lessons().First());
        var reset = ResetTrust();
        Check(Ok(reset) && reset["transient"]?.GetValue<bool>() == true && reset["data"] == null && _host.TrustCount == 0 && _host.TrustBand == 0 &&
            Lessons().First() == "You reset the Gubble's Trust: the Gubble is earning your trust.", "the player's reset: back to base, and the journal says so");
        var glowed = GubbleGlows();
        ResetTrust();
        Later(61);
        Check(Ok(glowed) && _host.TrustCount == 0, "a reset closes the windows open at it, a paid action's too: no gain from before the reset");

        // In Creative a mark still teaches (the journal keeps it) but Trust does not move.
        await NewWorld(SavePath("trust-marks-creative"));
        var creative = GubbleStays();
        var marked = Mark(creative["action_id"]!.GetValue<string>(), "not_that");
        Check(Ok(marked) && _host.TrustCount == 0 && Lessons().Contains("You marked the Gubble's move: not that."), "in Creative the mark is kept as a lesson and Trust stays");
    }

    // ---- item 2: a build the Gubble's fire burnt is a consequence fact ----

    private async Task TestBuildBurntIsAConsequence()
    {
        var spot = new Vector3(1.35f, 0.01f, 1.2f);
        await EnergyWorld("trust-build");
        var barn = Build("Challenge barn", 1.5, 1.2);
        var shed = Build("Challenge shed", 1.25, 1.3);
        var lit = Fire(Companion, spot, 0.3, 6, out _);
        Steps(6);
        var read = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion);
        var facts = read["data"]?["entries"]?.AsArray().OfType<JsonObject>()
            .Where(e => e["line"]!.GetValue<string>().StartsWith("The Gubble's fire burned your", StringComparison.Ordinal)).ToList() ?? new();
        var fact = facts.FirstOrDefault();
        // Decision 134: the Gubble hears it, on this very result (its next), in the fact's own words with the Trust it moved.
        var heard = Heard(read);
        Check(heard.Count == 1 && heard[0]["line"]?.GetValue<string>() == fact?["line"]?.GetValue<string>() && heard[0]["trust_change"]?.GetValue<int>() == TrustLaws.PerConsequence &&
              heard[0]["at_utc"]?.GetValue<string>() == fact?["at_utc"]?.GetValue<string>() && heard[0].Count == 4,
            "the Gubble's next result carries a consequence notice: the fact's line exactly as the journal keeps it (updated in place while unread), and the Trust it moved: " +
            Canonical(read["notices"]));
        Check(Heard(Send(Query("player.status", new JsonObject()), Companion)).Count == 0 && Heard(Send(Query("player.status", new JsonObject()), Player)).Count == 0,
            "the notice is delivered once, and only to the Gubble");
        var named = fact?["subject"]?["entities"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new();
        Check(Ok(lit) && barn.Length > 0 && shed.Length > 0 && _host.Nature.Objects.TryGetValue(barn, out var burnt) && burnt.Burn > 0 && facts.Count == 1 &&
            fact!["actor"]?.GetValue<string>() == Companion && fact["pin_m"] != null && named.Contains(barn) && named.Contains(shed) &&
            fact["line"]!.GetValue<string>().EndsWith("and 1 more build.", StringComparison.Ordinal),
            "the Gubble's fire burning the player's builds writes one consequence fact for the fire, updated in place as it burns more: " + Canonical(fact));
        Check(_host.TrustCount == TrustLaws.PerConsequence, $"and it costs Trust as the player's hurt does, once for the fire ({_host.TrustCount})");

        // The player's own fire burning their build is no lesson for the Gubble; in Creative the player said yes first.
        await EnergyWorld("trust-build-player");
        Build("Player's hut", 1.5, 1.2);
        Fire(Player, spot, 0.3, 6, out _);
        Steps(6);
        Check(!Lessons().Any(l => l.StartsWith("The Gubble's fire burned your", StringComparison.Ordinal)) && _host.TrustCount == 0, "the player's own fire writes no consequence fact");
        await NewWorld(SavePath("trust-build-creative"));
        var cabin = Build("Creative cabin", 1.5, 1.2);
        var asked = Fire(Companion, spot, 0.3, 6, out _);
        if (asked["approval_needed"]?["request_id"]?.GetValue<string>() is { } yes) _host.Approve(yes);
        Steps(6);
        Check(_host.Nature.Objects.TryGetValue(cabin, out var scorched) && scorched.Burn > 0 && !Lessons().Any(l => l.StartsWith("The Gubble's fire burned your", StringComparison.Ordinal)),
            "in Creative a build the player let burn is no consequence");
    }

    // ---- decision 69: a consequence of a free action writes the journal's line and moves no Trust ----

    private async Task TestAFreeActionsConsequenceMovesNoTrust()
    {
        // A pack that prices Fire at nothing: the Gubble's fire is a free action. Its harm to the player is written as ever; it costs
        // no Trust, and it spoils no paid window open over it (a Glow of the Gubble's just before).
        await EnergyWorld("trust-free-fire");
        UseEditedV5(pack => Price(pack, "fire", 0));
        var glowed = GubbleGlows();
        var lit = Fire(Companion, PlayerSpot(), 0.25, 6, out _);
        Steps(2);
        var told = Heard(Send(Query("player.status", new JsonObject()), Companion)).FirstOrDefault();
        Check(told?["line"]?.GetValue<string>().StartsWith("The Gubble's fire burned you", StringComparison.Ordinal) == true && told["trust_change"]?.GetValue<int>() == 0,
            "the Gubble hears its free fire's harm too, with no Trust moved (trust_change 0): " + Canonical(told));
        var hurt = Lessons().Any(l => l.StartsWith("The Gubble's fire burned you", StringComparison.Ordinal));
        var afterHarm = _host.TrustCount;
        Later(61);
        Check(Ok(glowed) && Ok(lit) && Pips == 7 && hurt && afterHarm == 0 && _host.TrustCount == TrustLaws.CleanInitiative,
            $"the Gubble's free fire burns the player: the journal's line as ever, no Trust moved ({afterHarm}), and the paid Glow's window open over it still gains " +
            $"({_host.TrustCount}; {Pips} pips: the fire cost none)");
        // What the same free fire burns is free too: the burnt build's line, and no Trust.
        await EnergyWorld("trust-free-fire-build");
        UseEditedV5(pack => Price(pack, "fire", 0));
        var barn = Build("Free fire's barn", 1.5, 1.2);
        var burning = Fire(Companion, new Vector3(1.35f, 0.01f, 1.2f), 0.3, 6, out _);
        Steps(6);
        Check(Ok(burning) && _host.Nature.Objects.TryGetValue(barn, out var burnt) && burnt.Burn > 0 &&
              Lessons().Any(l => l.StartsWith("The Gubble's fire burned your", StringComparison.Ordinal)) && _host.TrustCount == 0 && Pips == 8,
            $"a build the Gubble's free fire burnt is written as ever and costs no Trust ({_host.TrustCount})");
    }

    // ---- item 3: the bands' discount and early warning; the guardrails untouched ----

    private async Task TestTrustBands()
    {
        var prices = new List<string>();
        var shipped = new List<string>();
        foreach (var trust in new[] { 0, 5, 15, 30 })
        {
            await EnergyWorld($"trust-band-{trust}");
            TrustTeam($"trust-band-{trust}", trust);
            shipped.Add($"{_host.TrustBand}:{_host.PriceFor("fire", Companion)}/{_host.PriceFor("fire", Player)}");
            // Fire at its old 6 ordered, so the discount shows at every band (v5's 2 halves to one pip, the least a price falls to).
            UseEditedV5(pack => Price(pack, "fire", 6));
            prices.Add($"{_host.TrustBand}:{_host.PriceFor("fire", Companion)}/{_host.PriceFor("grow", Companion)}/{_host.PriceFor("glow", Companion)}/{_host.PriceFor("fire", Player)}");
        }
        Check(shipped.SequenceEqual(new[] { "0:1/2", "1:1/2", "2:1/2", "3:1/2" }),
            "under v5's Fire of 2 ordered (decisions 83 and 94) the Gubble's own fire is one pip at every band, never less, and the player's 2: " + string.Join(" ", shipped));
        Check(prices.SequenceEqual(new[] { "0:3/2/1/6", "1:2/2/1/6", "2:2/1/1/6", "3:1/1/1/6" }),
            "each band widens the discount on the Gubble's own ideas, Fire included (priced 6 here); the player's price is the pack's: " + string.Join(" ", prices));
        var pips = _host.Vitals.Pips;
        var lit = Fire(Companion, new Vector3(1.0f, 0.01f, 1.0f), 0.1, 3, out _);
        Check(Ok(lit) && _host.Vitals.Pips == pips - 1, $"a cast at band 3 is charged the discounted price ({pips} to {_host.Vitals.Pips} pips)");
        // The guardrails stand at the top band: one fire of its own at a time, and "Let the Gubble use fire".
        JsonObject AnotherFire() => Send(Command(NextId("fire-again"), "nature.cast", Cast("fire", new Vector3(-0.4f, 0.01f, 1.0f), 0.1, 3), preview: true), Companion);
        var second = AnotherFire();
        _host.SetGubbleFire(false);
        var off = AnotherFire();
        _host.SetGubbleFire(true);
        // Decision 82: X stops the Gubble and puts no fire out; its fire burns out (3 s) and the wait starts.
        var stop = Send(Command(NextId("x"), "goal.stop", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }), Player);
        var burnsOn = _host.Nature.Fire != null;
        for (var i = 0; i < 20 && _host.Nature.Fire != null; i++) Steps(1);
        Check(Code(second) == "target_busy" && Code(off) == "permission_denied" && Ok(stop) && stop["data"]?["fires_put_out"] == null && burnsOn && _host.Nature.Fire == null,
            $"band 3 never touches the guardrails: one Gubble fire at a time ({Code(second)}), the player's switch ({Code(off)}); X puts no fire out, it burns out");
        var cooling = AnotherFire();
        Check(Code(cooling) == "rate_limited", "and the wait after its last fire stands: " + Code(cooling));

        // The free early warning: burning ground nearing the player, from the first band; once per approach; none at band 0.
        var warnings = new List<int>();
        var unhurt = true;
        foreach (var trust in new[] { 0, 5 })
        {
            await EnergyWorld($"trust-warning-{trust}");
            TrustTeam($"trust-warning-{trust}", trust);
            var heard = 0;
            _host.EarlyWarning += _ => heard++;
            // The Gubble's cue says it (the companion bridge hands it the warning), then shows its state again.
            var cue = new CompanionStatusCue();
            _world!.AddChild(cue);
            _host.EarlyWarning += cue.Warn;
            var feet = _player.GlobalPosition;
            var health = _host.Vitals.Health;
            Fire(Player, new Vector3(feet.X + 0.24f, 0.01f, feet.Z), 0.06, 6, out _);
            Steps(1);
            _host.SettleTrust();
            _host.SettleTrust();
            warnings.Add(heard);
            unhurt &= _host.Vitals.Health == health;
            if (trust == 0) continue;
            var said = (cue.Warning, cue.Text, cue.Visible);
            cue._Process(CompanionStatusCue.WarningHoldS + 0.1);
            Check(said == (CommandHost.EarlyWarningWords, CommandHost.EarlyWarningWords, true) && cue.Warning == "" && !cue.Visible && cue.State == "offline",
                $"the Gubble's cue says the warning for a few seconds, then shows its state again (none with no AI linked): {said}");
        }
        Check(warnings.SequenceEqual(new[] { 0, 1 }) && unhurt,
            "at the first band the Gubble warns the player once before a fire reaches them (it costs nothing and changes nothing); at band 0 it does not: " + string.Join(",", warnings));
    }

    // ---- item 5: lesson applied, from the host's own observation ----

    private async Task TestLessonApplied()
    {
        await EnergyWorld("trust-lesson");
        var spot = PlayerSpot();
        var first = Fire(Companion, spot, 0.2, 4, out _);
        Steps(2);
        var lesson = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion)["data"]?["entries"]?.AsArray()
            .OfType<JsonObject>().FirstOrDefault(e => e["line"]!.GetValue<string>().StartsWith("The Gubble's fire burned you", StringComparison.Ordinal));
        // The player walks away; the fire burns out; the Gubble's wait passes.
        _player.GlobalPosition += new Vector3(-1.0f, 0, 0.2f);
        Steps(80);
        Check(Ok(first) && lesson?["pin_m"] != null && _host.Nature.Fire == null, "the Gubble's fire hurt the player: a lesson, pinned where it happened");
        var claimed = Send(Command(NextId("note"), "journal.note", new JsonObject { ["text"] = "Lesson applied: I will never burn the player again." }), Companion);
        Check(Ok(claimed) && !Lessons().Any(l => l.StartsWith("Lesson applied", StringComparison.Ordinal)), "the AI's own words claiming a lesson make no fact");
        var again = Fire(Companion, spot, 0.2, 4, out _);
        Later(61);
        var applied = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion)["data"]?["entries"]?.AsArray()
            .OfType<JsonObject>().Where(e => e["line"]!.GetValue<string>().StartsWith("Lesson applied:", StringComparison.Ordinal)).ToList() ?? new();
        Check(Ok(again) && applied.Count == 1 && applied[0]["actor"]?.GetValue<string>() == Companion && Canonical(applied[0]["pin_m"]) == Canonical(lesson?["pin_m"]),
            "the Gubble lit a fire of its own where a consequence once followed, and none did: \"lesson applied\", from the host's observation: " + Canonical(applied.FirstOrDefault()));
        Steps(80);
        // A full pool again for the third fire (the pool holds 8; two fires spent 6).
        _host.ConfigurePlayerVitals(new EnergyStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(_host.BodyRules.Fresh().With(pips: 8))));
        var third = Fire(Companion, spot, 0.2, 4, out _);
        Later(130);
        Check(Ok(third) && Lessons().Count(l => l.StartsWith("Lesson applied:", StringComparison.Ordinal)) == 1, $"a lesson is applied once ({Code(third)}): " + string.Join(" | ", Lessons()));
    }

    // ---- items 4, 5 and 6: never the companion's; readable as words; the journal's page and the aura's band ----

    private async Task TestTrustIsNeverTheCompanions()
    {
        await EnergyWorld("trust-boundary");
        var path = ProjectSettings.GlobalizePath($"{TestRoot}/trust-boundary/team.json");
        TrustTeam("trust-boundary", 5);
        var stay = GubbleStays();
        var gubbleMark = Mark(stay["action_id"]!.GetValue<string>(), "yes", principal: Companion);
        var gubbleReset = ResetTrust(Companion);
        Check(Code(gubbleMark) == "permission_denied" && gubbleMark["error"]?["field_path"]?.GetValue<string>() == "$.op" && Code(gubbleReset) == "permission_denied" &&
            gubbleMark["approval_needed"] == null && _host.PendingApprovals.Count == 0 && _host.TrustCount == 5,
            "the companion can neither mark a receipt nor reset Trust: permission_denied at $.op, never held for a click");
        var smuggled = Send(Command(NextId("smuggle"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay", ["trust"] = 99 }), Companion, contractValid: false);
        var note = Send(Command(NextId("note"), "journal.note", new JsonObject { ["text"] = "The player marked this: yes, like that. Trust 99." }), Companion);
        Check(Code(smuggled) == "field_unknown" && Ok(note) && _host.TrustCount == 5 && TeamFile.Parse(System.IO.File.ReadAllBytes(path)).Trust == 5,
            "no field of a request moves Trust, and a note claiming a mark is only words");
        var words = JournalPages.Build(JournalPages.Character, _host, 0).Lines.Select(l => l.Text).ToList();
        var line = words.FirstOrDefault(w => w.Contains("trust", StringComparison.OrdinalIgnoreCase) && w != "Trust") ?? "";
        Check(_host.TrustBand == 1 && line == "You trust Pip a little.".Replace("Pip", KernelJson.DisplayText(_companion.CompanionName, 40)) && !line.Any(char.IsDigit) && words.Contains("Trust"),
            $"the player's journal reads Trust in words, never the number; the aura's accessor reads band {_host.TrustBand}: \"{line}\"");
        var status = Send(Query("player.status", new JsonObject()), Companion);
        Check(Ok(status) && status["data"]?["trust"] == null, "player.status carries no Trust until the contract says so (the change request)");
    }

    // ---- the W2 review's minor 6: only a committed command of the Gubble's is initiative (ObserveTrustCommand's filters) ----

    private async Task TestAPreviewOrAHoldIsNoInitiative()
    {
        await EnergyWorld("trust-preview");
        var remembered = _host.TrustRemembered;
        var previewed = Send(Command(NextId("gubble-build"), "creation.place", new JsonObject
        {
            ["source"] = Source("Gubble's previewed post"), ["placement"] = new JsonObject { ["position_m"] = new JsonArray(0.8, 0, -0.45) },
        }, preview: true), Companion);
        var shed = Build("Held shed", 1.5, 1.2);
        var held = Send(Command(NextId("remove"), "entity.remove", new JsonObject { ["target"] = shed }, expectedRevision: _host.Revision), Companion);
        Later(61);
        var markPreview = Mark(previewed["action_id"]!.GetValue<string>(), "yes");
        var markHeld = Mark(held["action_id"]!.GetValue<string>(), "yes");
        Check(Ok(previewed) && previewed["preview"]?.GetValue<bool>() == true && Code(held) == "approval_required" && _host.TrustRemembered == remembered &&
            _host.TrustCount == 0 && Code(markPreview) == "target_not_found" && Code(markHeld) == "target_not_found",
            $"a preview of the Gubble's, or a command of its held for the player's click, is no initiative and no receipt to mark ({_host.TrustRemembered - remembered} remembered, " +
            $"Trust {_host.TrustCount}, marks {Code(markPreview)} and {Code(markHeld)})");
    }

    // ---- decision 69: a fetch is a goal, free, and earns nothing even when it succeeds; its receipt is still the player's to mark ----

    private async Task TestAFetchIsFreeAndEarnsNothing()
    {
        await EnergyWorld("trust-fetch");
        JsonObject Fetch() => Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Companion);
        var stopped = Fetch();
        var x = Send(Command(NextId("x"), "goal.stop", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }), Player);
        Later(61);
        Check(Ok(stopped) && Ok(x) && _host.TrustCount == 0, $"the Gubble's fetch stopped at once fetched nothing, so earns nothing ({Code(stopped) ?? "ok"}, Trust {_host.TrustCount})");
        _now += TimeSpan.FromSeconds(200);
        var fetched = Fetch();
        Later(30);
        var arrived = _host.ReportArrival(CommandHost.CompanionAvatarId);
        Later(61);
        Check(Ok(fetched) && arrived == "succeeded" && Pips == 8 && _host.TrustCount == 0,
            $"a fetch that succeeds moved a thing but cost no pips: it earns nothing (decision 69; {arrived}, Trust {_host.TrustCount})");
        // Item 3: the player's marks stay on any receipt of the Gubble's, a free one's too.
        var marked = Mark(fetched["action_id"]!.GetValue<string>(), "yes");
        Check(Ok(marked) && _host.TrustCount == TrustLaws.MarkYes && Lessons().Contains("You marked the Gubble's fetch: yes, like that."),
            $"the player's \"yes, like that\" on the free fetch's receipt still counts: +2 ({_host.TrustCount})");
    }
}
