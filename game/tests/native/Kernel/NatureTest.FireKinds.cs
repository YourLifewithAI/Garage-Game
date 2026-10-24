using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Fire's behaviours (MECHANICS.md decisions 77 to 80) through the command path, in the test room: Fireline learned by three Fire casts
/// along one line and then cast as a drag (the whole line at once, one undo step, Fire's guardrails), Snuff learned by rain on the Gubble's
/// fire or a bubble cast on a fire and then cast on something burning (everything in 1.5 m out, campfires too, the ground damp for 30 s,
/// the Gubble's Snuff of a lit campfire held for the player), and the Will-o'-wisp on water (transient, three at most, X removes the
/// oldest, not the Gubble's one fire). Each kind teaches its expertise once; every committed Fire cast counts toward Fire's hints.
/// </summary>
public partial class NatureTest
{
    private bool FireKnows(string id) => _host.GlowProgress.Has(ExpertiseLists.Fire, id);
    private int FireCasts => _host.GlowProgress.Of(ExpertiseLists.Fire)?.SuccessfulCasts ?? 0;

    private static JsonObject LineArgs(Vector3 start, Vector3 end, string? previewId = null)
    {
        var args = Cast("fire", start, 0.15, 6, previewId);
        args["end_m"] = new JsonArray(end.X, end.Y, end.Z);
        return args;
    }

    /// <summary>A Fireline previewed and committed as exactly that preview: the commit's result.</summary>
    private JsonObject Fireline(string principal, Vector3 start, Vector3 end, out JsonObject preview)
    {
        preview = Send(Command(NextId("line-preview"), "nature.cast", LineArgs(start, end), preview: true), principal);
        var token = preview["data"]?["preview_id"]?.GetValue<string>();
        return Send(Command(NextId("line"), "nature.cast", LineArgs(start, end, token)), principal);
    }

    /// <summary>The player's fire put out by Creative's undo, so the next cast is not kept waiting (one fire at a time).</summary>
    private void UndoFire()
    {
        if (_host.Nature.Fire != null) _host.PlayerNatureUndo();
    }

    private async Task FireKindsWorld(string name)
    {
        await NewWorld(SavePath(name));
        _host.ConfigureGlowProgress(new CountingProgressStore());
        _host.LearnFireForTests();
    }

    private async Task TestFireKinds()
    {
        await TestFireOnTheClick();
        await TestFireline();
        await TestSnuff();
        await TestSnuffByBubble();
        await TestWisp();
    }

    /// <summary>
    /// Decision 83: the player's Fire lights on the click (PlayerFireLight, what the wheel, key 6 and the Magic panel call): its preview and
    /// the cast of exactly that preview in one call, with no hold, so the outline can flash as it lights. The recorder sees a preview then a
    /// cast; the price is charged once; a refused preview casts nothing; the place still decides (an unlit firepit, Snuff on a fire); and
    /// Creative's one ask about builds still holds the cast for the player's card. The Gubble's own Fire stays two commands of its own.
    /// </summary>
    private async Task TestFireOnTheClick()
    {
        await FireKindsWorld("fire-click");
        var handled = new System.Collections.Generic.List<CommandHandled>();
        _host.Handled += handled.Add;
        var at = new Vector3(0.2f, 0.01f, -0.6f);
        var revision = _host.Revision;
        var pips = _host.Vitals.Pips;
        var click = _host.PlayerFireLight(at, radius: 0.2f, duration: 6);
        var fire = handled.Where(h => h.Op == "nature.cast").Select(h => $"{(h.Preview ? "preview" : "cast")}:{(h.Ok ? "ok" : h.Code)}").ToArray();
        Check(click.Previewed && click.Lit && !click.Held && click.Kind == FireKindWords.Fire && _host.Nature.Fire?.Principal == Player && _host.Revision == revision + 1 &&
              click.Preview["data"]?["preview_id"] != null && click.Preview["data"]?["cells"]?.AsArray().Count > 0 && click.Preview["data"]?["radius_m"]?.GetValue<double>() == 0.2 &&
              click.Result["data"]?["started_cells"]?.GetValue<int>() > 0 && fire.SequenceEqual(new[] { "preview:ok", "cast:ok" }) && _host.Vitals.Pips == pips - 2,
            $"one click previews and lights the fire, the preview's outline and kind in hand for the flash, a preview then a cast for the recorder, 2 pips once " +
            $"({string.Join(", ", fire)}; pips {pips} to {_host.Vitals.Pips}): " + Canonical(click.Result["error"]));
        var busy = _host.PlayerFireLight(at + new Vector3(0.6f, 0, 0), radius: 0.2f, duration: 6);
        Check(!busy.Previewed && !busy.Lit && ReferenceEquals(busy.Preview, busy.Result) && Code(busy.Result) == "target_busy" && _host.Revision == revision + 1,
            "a click while a fire burns is refused at its preview, and nothing is cast: " + Code(busy.Result));
        UndoFire();
        // The wheel names no size: the pack's own Fire (v5: 1.5 m, 30 s), on the same one click.
        var wheel = _host.PlayerFireLight(at);
        Check(wheel.Lit && wheel.Preview["data"]?["radius_m"]?.GetValue<double>() == 1.5 && _host.Nature.Fire?.RemainingS > 20,
            "a click that names no size lights the pack's own Fire (1.5 m, 30 s): " + Canonical(wheel.Result["error"] ?? wheel.Preview["data"]?["radius_m"]));
        UndoFire();
        // The place decides on the click as before: an unlit firepit is lit; and once Snuff is learned (rain on the Gubble's fire), a click
        // on the Gubble's fire is Snuff, the player's one way to end a fire (decision 82).
        var pit = new Vector3(0.9f, 0, 1.0f);
        _host.FirepitsForTests = new[] { pit };
        var lit = _host.PlayerFireLight(pit + new Vector3(0, 0.01f, 0), radius: 0.05f, duration: 6);
        Check(lit.Lit && lit.Kind == FireKindWords.Firepit && _host.IsCampfireLit("firepit:1"), "a click on an unlit firepit lights it: " + Canonical(lit.Result["error"]));
        var gubbleFire = Fire(Companion, NearGubble, 0.3, 30, out _);
        Steps(1);
        _host.Nature.WeatherS = RainAtS;
        Steps(1);
        _host.Nature.WeatherS = 300;
        var cell = _host.Nature.Cells.Values.FirstOrDefault(c => c.BurningS > 0)?.Position ?? NearGubble;
        var snuff = _host.PlayerFireLight(cell, radius: 0.2f, duration: 6);
        Check(Ok(gubbleFire) && FireKnows(ExpertiseLists.Snuff) && snuff.Lit && snuff.Kind == FireKindWords.Snuff && _host.Nature.Fire == null &&
              !_host.Nature.Cells.Values.Any(c => c.BurningS > 0),
            "and once Snuff is learned a click on the Gubble's fire is Snuff: it all goes out: " + Canonical(snuff.Result["error"]));
        // Creative's one ask about builds is unchanged: the click's cast is held for the player's card, and the yes lights it.
        await FireKindsWorld("fire-click-ask");
        var shed = Build("Click shed", 1.5, 1.2);
        var asks = _host.PlayerFireLight(new Vector3(1.35f, 0.01f, 1.2f), radius: 0.3f, duration: 6);
        var request = _host.PendingApprovals.FirstOrDefault()?.RequestId;
        var waited = _host.Nature.Fire == null;
        var approved = request == null ? new JsonObject() : _host.Approve(request);
        Check(shed.Length > 0 && asks.Previewed && asks.Held && !asks.Lit && asks.Preview["data"]?["requires_approval"]?.GetValue<bool>() == true && waited &&
              Ok(approved) && _host.Nature.Fire?.Principal == Player,
            "in Creative a click whose fire would reach a build is held for the card (the one ask), and the player's yes lights it: " + Canonical(asks.Result["error"]?["code"]));
    }

    /// <summary>Decision 78: three Fire casts along a line teach Fireline; then a drag lights the whole line at once, one fire under Fire's guardrails.</summary>
    private async Task TestFireline()
    {
        Check(CommandHost.AlongOneLine(new Vector3(0, 0, 0), new Vector3(0.6f, 0, 0), new Vector3(1.2f, 0, 0.1f)) &&
              !CommandHost.AlongOneLine(new Vector3(0, 0, 0), new Vector3(0.6f, 0, 0), new Vector3(1.2f, 0, 0.8f)) &&
              !CommandHost.AlongOneLine(new Vector3(0, 0, 0), new Vector3(0.3f, 0, 0), new Vector3(1.2f, 0, 0)),
            $"three centres are along one line when each is within {FireKindLaws.FirelineLearnOffLineM} m of it and all are {FireKindLaws.FirelineLearnApartM} m apart");
        await FireKindsWorld("fire-kinds-line");
        var a = new Vector3(-0.15f, 0.01f, 1.0f);
        var b = new Vector3(0.45f, 0.01f, 1.0f);
        var c = new Vector3(1.05f, 0.01f, 1.0f);
        var off = new Vector3(0.45f, 0.01f, 0.4f);
        var early = Send(Command(NextId("line-early"), "nature.cast", LineArgs(a, c), preview: true), Player);
        Check(Code(early) == "permission_denied" && early["error"]?["message"]?.GetValue<string>() == CommandHost.FirelineNotLearned,
            "before Fireline is learned a drag (end_m) is refused, in the host's words: " + early["error"]?["message"]);
        var casts = FireCasts;
        foreach (var at in new[] { a, b, off })
        {
            var cast = Fire(Player, at, 0.15, 6, out _);
            Check(Ok(cast), "(a Fire cast) " + Canonical(cast["error"]));
            UndoFire();
        }
        Check(!FireKnows(ExpertiseLists.Fireline) && FireCasts == casts + 3, "three Fire casts in a triangle teach nothing; each committed cast is counted toward Fire's hints");
        _now += TimeSpan.FromSeconds(FireKindLaws.FirelineLearnWindowS + 1);
        // The window is 5 minutes (provisional, the second playtest round; decision 78 said 2): casts 2.5 and 2.3 minutes apart, the
        // first and the third 4.8 minutes apart, still teach it.
        foreach (var (at, after) in new[] { (a, 150.0), (b, 140.0) })
        {
            var cast = Fire(Player, at, 0.15, 6, out _);
            Check(Ok(cast), "(a Fire cast) " + Canonical(cast["error"]));
            UndoFire();
            _now += TimeSpan.FromSeconds(after);
        }
        var notYet = FireKnows(ExpertiseLists.Fireline);
        Check(Ok(Fire(Player, c, 0.15, 6, out _)), "(the third, along the line)");
        UndoFire();
        Check(FireKindLaws.FirelineLearnWindowS == 300 && !notYet && FireKnows(ExpertiseLists.Fireline),
            $"past the window ({FireKindLaws.FirelineLearnWindowS} s) the old casts no longer count; three in a row, 0.6 m apart, within 5 minutes (290 s from the first to the third, past the old 2 minutes) teach Fireline");
        // The drag: the whole line catches at once; one fire, one undo step.
        var start = new Vector3(-0.3f, 0.01f, 1.2f);
        var end = new Vector3(1.2f, 0.01f, 1.2f);
        var cells = CommandHost.LineCells(start, end).Count;
        var revision = _host.Revision;
        var lit = Fireline(Player, start, end, out var preview);
        var burning = _host.Nature.Cells.Values.Count(cell => cell.BurningS > 0);
        Check(Ok(preview) && preview["data"]?["kind"]?.GetValue<string>() == FireKindWords.Fireline && preview["data"]?["end_m"] != null &&
              preview["data"]?["cell_count"]?.GetValue<int>() == cells && preview["data"]?["affected_ids"] != null,
            $"its preview says what the place made of it (a fireline, {cells} cells along {start.DistanceTo(end):0.0} m) and what it could reach: " + Canonical(preview["error"]));
        Check(Ok(lit) && _host.Revision == revision + 1 && lit["transient"]?.GetValue<bool>() == false && burning == cells && _host.Nature.Fire != null &&
              _host.Nature.Undo.LastOrDefault()?.Label == "Fireline",
            $"held to light: every cell along the line burns at once ({burning} of {cells}), one durable fire with one undo step: " + Canonical(lit["error"] ?? lit["data"]));
        Steps(4);
        Check(_host.Nature.Cells.Values.Count(cell => cell.BurningS > 0) is > 0 and <= NatureState.MaxBurning, "and it burns on within Fire's caps");
        var undo = _host.PlayerNatureUndo();
        Check(Ok(undo) && _host.Nature.Fire == null && !_host.Nature.Cells.Values.Any(cell => cell.BurningS > 0), "Creative's undo takes the whole line back as one step");
        var tooLong = Send(Command(NextId("line-long"), "nature.cast", LineArgs(start, start + new Vector3(3.2f, 0, 0)), preview: true), Player);
        var far = Send(Command(NextId("line-far"), "nature.cast", LineArgs(b, new Vector3(2.9f, 0.01f, 1.0f)), preview: true), Player);
        var grow = Send(Command(NextId("line-grow"), "nature.cast", new JsonObject
        {
            ["capability"] = "grow", ["center_m"] = new JsonArray(b.X, b.Y, b.Z), ["radius_m"] = 0.15, ["duration_s"] = 1, ["end_m"] = new JsonArray(c.X, c.Y, c.Z),
        }, preview: true), Player, contractValid: false);
        Check(Code(tooLong) == "invalid_args" && Code(far) == "out_of_bounds" && Code(grow) == "request_invalid",
            "a line longer than 3 m is refused, one whose end is beyond the Gubble's reach waits for it to come closer, and only Fire names an end");
        // The Gubble's Fireline keeps every Fire guardrail: the player's switch, one fire of its own, and the wait after its last; X stops
        // the Gubble and puts no fire out (decision 82), so the line burns out first.
        _host.SetGubbleFire(false);
        var off1 = Send(Command(NextId("line-off"), "nature.cast", LineArgs(start, end), preview: true), Companion);
        _host.SetGubbleFire(true);
        var gubble = Fireline(Companion, start, end, out _);
        var second = Send(Command(NextId("line-second"), "nature.cast", LineArgs(start, end), preview: true), Companion);
        var x = Send(Command(NextId("x"), "goal.stop", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }), Player);
        var burnsOn = _host.Nature.Fire != null;
        for (var i = 0; i < 200 && _host.Nature.Fire != null; i++) Steps(1);
        var wait = Send(Command(NextId("line-wait"), "nature.cast", LineArgs(start, end), preview: true), Companion);
        Check(Code(off1) == "permission_denied" && Ok(gubble) && Code(second) == "target_busy" && Ok(x) && burnsOn && _host.Nature.Fire == null && Code(wait) == "rate_limited",
            $"the Gubble's Fireline: refused while the player's switch is off, lit when on, one at a time, burning on past X until it burns out, then the wait: {Code(off1)}, {Code(second)}, {Code(wait)}");
        // The island saved with Fireline receipts in it loads again, every receipt read back.
        var saved = _host.Revision;
        await NewWorld(SavePath("fire-kinds-line"));
        Check(_host.Revision == saved && _host.Revision > 0 && _host.NatureNotice.Length == 0, $"the island saved after its Firelines loads again at revision {_host.Revision} of {saved}");
    }

    /// <summary>Decision 79: Snuff learned by rain on the Gubble's fire; then Fire on something burning puts it all out, campfires too, the ground damp for 30 s.</summary>
    private async Task TestSnuff()
    {
        await FireKindsWorld("fire-kinds-snuff");
        var p = NearGubble;
        Check(Ok(Fire(Player, p, 0.3, 30, out _)), "(the player lights a fire)");
        var plain = Send(Command(NextId("aim-burning"), "nature.cast", Cast("fire", p, 0.3, 30), preview: true), Player);
        Check(Code(plain) == "target_busy" && !FireKnows(ExpertiseLists.Snuff), "before Snuff is learned, Fire aimed at a fire is plain Fire, which waits for the fire there");
        UndoFire();
        Check(Ok(Fire(Companion, p, 0.3, 30, out _)), "(the Gubble lights a fire)");
        Steps(1);
        var dry = FireKnows(ExpertiseLists.Snuff);
        _host.Nature.WeatherS = RainAtS;
        Steps(1);
        Check(!dry && FireKnows(ExpertiseLists.Snuff), "rain falling on a fire the Gubble started teaches Snuff");
        // Past the rain (fair again from 270 s of the cycle), the clock still running on, so the Gubble's wait has long passed.
        _host.Nature.WeatherS = 300;
        UndoFire();
        _now += TimeSpan.FromSeconds(30);
        Steps(50);
        // Snuff: Fire aimed at something burning.
        Check(Ok(Fire(Player, p, 0.3, 30, out _)), "(the player lights a fire)");
        Steps(6);
        var target = _host.Nature.Cells.Values.Where(cell => cell.BurningS > 0).OrderBy(cell => cell.Position.DistanceTo(p)).First().Position;
        var preview = Send(Command(NextId("snuff-preview"), "nature.cast", Cast("fire", target, 0.3, 30), preview: true), Player);
        var token = preview["data"]?["preview_id"]?.GetValue<string>();
        var revision = _host.Revision;
        var casts = FireCasts;
        var snuff = Send(Command(NextId("snuff"), "nature.cast", Cast("fire", target, 0.3, 30, token)), Player);
        Check(Ok(preview) && preview["data"]?["kind"]?.GetValue<string>() == FireKindWords.Snuff && preview["data"]?["affected_ids"] == null && preview["data"]?["cell_count"]?.GetValue<int>() > 0,
            "once learned, Fire aimed at something burning previews as Snuff: what it puts out, and nothing it would burn");
        Check(Ok(snuff) && snuff["transient"]?.GetValue<bool>() == true && snuff["data"]?["kind"]?.GetValue<string>() == FireKindWords.Snuff && _host.Revision == revision &&
              !_host.Nature.Cells.Values.Any(cell => cell.BurningS > 0) && _host.Nature.Fire == null && FireCasts == casts + 1,
            "held, Snuff puts out everything burning within 1.5 m: the fire is out, no room revision (a transient receipt), and the cast counts: " + Canonical(snuff["error"] ?? snuff["data"]));
        // Ground in the circle that never burned (0.8 m from the snuff's point) is damp too: it cannot catch until the 30 s are over.
        var aside = target + new Vector3(0.8f, 0, 0);
        var damp = Send(Command(NextId("damp"), "nature.cast", Cast("fire", aside, 0.1, 30), preview: true), Player);
        Steps((int)Math.Ceiling(FireKindLaws.SnuffDampS / NatureLaws.StepS) + 1);
        var dried = Fire(Player, aside, 0.1, 6, out _);
        Check(Code(damp) == "invalid_args" && Ok(dried), $"the snuffed ground cannot catch for {FireKindLaws.SnuffDampS} s, and can after: {Code(damp)}, then {Code(dried) ?? "lit"}");
        UndoFire();
        // Campfires: the player's Snuff puts a lit campfire out; the Gubble's waits for the player's yes.
        var pit = new Vector3(0.9f, 0, 1.0f);
        _host.FirepitsForTests = new[] { pit };
        Check(Ok(Fire(Player, pit + new Vector3(0, 0.01f, 0), 0.05, 6, out _)) && _host.IsCampfireLit("firepit:1"), "(the firepit is lit: a campfire)");
        var mine = Fire(Player, pit + new Vector3(0.05f, 0.01f, 0), 0.3, 6, out var minePreview);
        Check(minePreview["data"]?["kind"]?.GetValue<string>() == FireKindWords.Snuff && Ok(mine) && _host.IsCampfireOut("firepit:1"),
            "the player's Fire aimed at a lit campfire is Snuff, and it goes out (only rain or Snuff puts one out)");
        Check(Ok(Fire(Companion, pit + new Vector3(0, 0.01f, 0), 0.05, 6, out _)) && _host.IsCampfireLit("firepit:1"), "(the Gubble's Fire relights it)");
        var held = Fire(Companion, pit + new Vector3(0.05f, 0.01f, 0), 0.3, 6, out var heldPreview);
        var request = _host.PendingApprovals.FirstOrDefault()?.RequestId;
        var stillLit = _host.IsCampfireLit("firepit:1");
        var approved = request == null ? new JsonObject() : _host.Approve(request);
        Check(heldPreview["data"]?["requires_approval"]?.GetValue<bool>() == true && Code(held) == "approval_required" && stillLit && Ok(approved) && _host.IsCampfireOut("firepit:1"),
            "the Gubble's own-idea Snuff of a lit campfire waits for the player's yes, and the yes puts it out: " + Canonical(held["error"]));
        // A wild fire asks nothing, and Snuff is allowed while the Gubble's fire budget waits (once the last snuff's damp has dried).
        Steps((int)Math.Ceiling(FireKindLaws.SnuffDampS / NatureLaws.StepS) + 1);
        var gubbleFire = Fire(Companion, p, 0.15, 6, out _);
        Check(Ok(gubbleFire), "(the Gubble lights a fire) " + Canonical(gubbleFire["error"]));
        // It burns out (decision 82: X would put it out no more), and the Gubble's wait starts.
        for (var i = 0; i < 200 && _host.Nature.Fire != null; i++) Steps(1);
        var waiting = Send(Command(NextId("wait"), "nature.cast", Cast("fire", p, 0.15, 6), preview: true), Companion);
        Check(Ok(Fire(Player, p + new Vector3(-0.6f, 0, 0), 0.3, 30, out _)), "(the player lights a fire, away from the firepit)");
        Steps(2);
        var wild = _host.Nature.Cells.Values.Where(cell => cell.BurningS > 0).First().Position;
        var gubbleSnuff = Fire(Companion, wild, 0.3, 6, out _);
        Check(Code(waiting) == "rate_limited" && Ok(gubbleSnuff) && _host.Nature.Fire == null,
            "while the Gubble waits after its own fire, its Snuff of a wild fire is allowed and asks nothing: " + Canonical(gubbleSnuff["error"]));
    }

    /// <summary>Decision 79's other way: a bubble cast on a fire teaches Snuff (the host sees the bubble over burning ground).</summary>
    private async Task TestSnuffByBubble()
    {
        await FireKindsWorld("fire-kinds-bubble");
        var p = NearGubble;
        Check(Ok(Fire(Player, p, 0.3, 30, out _)), "(the player lights a fire)");
        Steps(2);
        var before = FireKnows(ExpertiseLists.Snuff);
        var bubble = Send(Command(NextId("bubble"), "nature.cast", new JsonObject
        {
            ["capability"] = "bubble_carry", ["center_m"] = new JsonArray(p.X, p.Y, p.Z), ["radius_m"] = 0.08, ["duration_s"] = 20,
        }), Player);
        Steps(1);
        Check(!before && Ok(bubble) && FireKnows(ExpertiseLists.Snuff), "a Bubble cast on a fire teaches Snuff: " + Canonical(bubble["error"]));
    }

    /// <summary>Decision 80: Fire on water makes a Will-o'-wisp: transient, three at most, X removes the oldest, not the Gubble's one fire.</summary>
    private async Task TestWisp()
    {
        await FireKindsWorld("fire-kinds-wisp");
        _world!.AddChild(Pool());
        await Frames(2);
        Check(_companion.TryTeleportTo(new Vector3(-0.2f, 0.01f, 0.7f)), "(the Gubble floats by the pool)");
        await Frames(2);
        var on = new Vector3(-0.8f, PoolY, 0.7f);
        var revision = _host.Revision;
        var wisp = Fire(Player, on, 0.3, 30, out var preview);
        var first = _host.Wisps.FirstOrDefault();
        Check(Ok(preview) && preview["data"]?["kind"]?.GetValue<string>() == FireKindWords.Wisp && preview["data"]?["affected_ids"] == null,
            "Fire cast on water previews as a Will-o'-wisp, which burns nothing: " + Canonical(preview["error"] ?? preview["data"]));
        Check(Ok(wisp) && wisp["transient"]?.GetValue<bool>() == true && first != null && wisp["data"]?["wisp"]?.GetValue<string>() == first.Id &&
              wisp["created"]?[0]?.GetValue<string>() == first.Id && Math.Abs(first.Anchor.Y - (PoolY + FireKindLaws.WispAboveWaterM)) < 1e-4f &&
              _host.Revision == revision && _host.Nature.Fire == null && FireKnows(ExpertiseLists.WillOWisp),
            "held, it makes a cold flame 15 cm over the water: a transient receipt naming its effect id, no room revision, no fire, and the first teaches Will-o'-wisp");
        Check(Ok(Fire(Player, NearGubble, 0.15, 6, out _)) && _host.Wisps.Count == 1, "it is no fire: a fire is lit while it hovers");
        var during = Fire(Companion, on + new Vector3(0, 0, 0.2f), 0.3, 30, out _);
        Check(Ok(during) && _host.Wisps.Count == 2, "and a wisp is set while a fire burns (not the one fire)");
        UndoFire();
        _host.SetGubbleFire(false);
        var third = Fire(Companion, on + new Vector3(0.2f, 0, 0), 0.3, 30, out _);
        _host.SetGubbleFire(true);
        var fourth = Fire(Player, on + new Vector3(0.2f, 0, 0.2f), 0.3, 30, out _);
        Check(Ok(third) && Ok(fourth) && _host.Wisps.Count == FireKindLaws.MaxWisps && _host.Wisps.All(w => w.Id != first!.Id),
            "three at most: the fourth removes the oldest (the Gubble's wisp burns nothing, so the player's fire switch does not stop it)");
        Send(Command(NextId("x"), "goal.stop", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }), Player);
        Check(_host.Wisps.Count == FireKindLaws.MaxWisps - 1, "X removes the oldest wisp");
        _now += TimeSpan.FromSeconds(FireKindLaws.WispLifeS + 1);
        Steps(1);
        Check(_host.Wisps.Count == 0, $"each lasts {FireKindLaws.WispLifeS / 60:0} minutes");
        Check(!CanonicalJson.Text(NatureBlock.Pack(_host.Nature)).Contains("effect:", StringComparison.Ordinal), "nothing of a wisp is in the nature save");
        Check(_host.PriceFor(FireKindWords.Fireline, Player) == 8 && _host.PriceFor(FireKindWords.Snuff, Player) == 2 && _host.PriceFor(FireKindWords.Wisp, Player) == 4,
            "each kind is priced as itself from the pack's cost block when the player orders it: a Fireline 8, a Snuff 2, a Will-o'-wisp 4");
    }
}
