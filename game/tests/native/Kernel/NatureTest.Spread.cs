using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// LOOK-3: the nature step's reading, spread over the ticks before it, changes nothing. One scripted play on the planted test room's
/// own ground (the room's physics, no stand-in sampler), driven a physics frame at a time through the host's own tick, is played
/// three times: with the step whole in its tick (NatureStepSpread off, as before LOOK-3), with its reading spread, and spread with
/// the audit (every reading taken ahead cast again at the step, every plant left out watched). The play lights a fire among the
/// grass, builds in it, picks a thing up and puts it down, asks for Grow (refused while the fire burns), removes the build (each in
/// the middle of a step's window, sent before its tick's work as a frame's input is), stops and loads the save while the fire burns,
/// builds again, casts Grow once the fire is out, burns the tree and takes that fire back. Compared between the three, byte for byte: every receipt and the tick each fire went out on, every
/// save the play wrote (looked at twice a second), the replay of every command sent again at the end, the island at the end and
/// the last save. Only what the host draws at random is evened first (a preview's token, and the print of a message that carries it).
/// </summary>
public partial class NatureTest
{
    private const double SpreadTick = 1.0 / 60;
    private const int SpreadReloadTick = 400;
    private const int SpreadTicks = 1930;
    /// <summary>The first fire: half a metre wide on the bare floor east of the spawns, more cells than may burn at once, so it burns as a front.</summary>
    private static readonly Vector3 SpreadFireAt = new(1.3f, 0.01f, 0.95f);

    /// <summary>The test room with the suite's plants and a field of 25 more tufts under the first fire: most stand away from the flames at any one step.</summary>
    private static RoomData SpreadRoom()
    {
        var at = TuftsAt.ToList();
        for (var x = 0; x < 5; x++)
            for (var z = 0; z < 5; z++)
                at.Add(new Vector2(0.9f + 0.2f * x, 0.6f + 0.175f * z));
        var grass = at.Select((spot, i) => new IslandFixture.Row(spot.X, 0, spot.Y, 11f * i - 160f, Vector3.One, new Color(0.85f, 0.95f, 0.7f), 0.13f, 0, 0, (uint)(100 + i))).ToList();
        var tree = IslandFixture.Broadleaf(TreeAt.X, TreeAt.Z, 0, 0, 31);
        return RoomData.Load(IslandFixture.WithPlants(RoomWorld.DefaultRoom, $"{TestRoot}/spread-room/test_room", new[] { IslandFixture.Grass("grass", grass), IslandFixture.Trees(new[] { tree }) }));
    }

    private sealed class SpreadRun
    {
        public readonly List<string> Receipts = new();
        public readonly List<string> Saves = new();
        public readonly List<string> Replays = new();
        public string Nature = "", LastSave = "", Notes = "";
        public long Ahead, Used, Cast, Dropped, Asked, LeftOut, Mismatches;
        public int Replayed, FiresOut, PlantsBurnt;
        public bool Happened;
    }

    private RoomData _spreadRoom = null!;

    private async Task TestSpread()
    {
        var ids = _ids;
        var now = _now;
        _spreadRoom = SpreadRoom();
        // The launch switch (the frame timing's before and after): the variable starts a host with the step whole.
        OS.SetEnvironment(CommandHost.NatureSpreadVariable, "off");
        await NewWorld(SavePath("spread-switch"));
        var off = _host.NatureStepSpread;
        OS.UnsetEnvironment(CommandHost.NatureSpreadVariable);
        await NewWorld(SavePath("spread-switch"));
        Check(!off && _host.NatureStepSpread, $"{CommandHost.NatureSpreadVariable}=off starts a host with the step whole in its tick; without it the reading is spread");
        var whole = await SpreadPlay("spread-whole", spread: false, audit: false, ids, now);
        var spread = await SpreadPlay("spread-on", spread: true, audit: false, ids, now);
        var audit = await SpreadPlay("spread-audit", spread: true, audit: true, ids, now);
        Check(whole.Happened && whole.FiresOut == 2 && whole.PlantsBurnt >= 3 && whole.Saves.Count >= 6 && whole.Replayed >= 6,
            $"the play does what it says: {whole.Notes}; {whole.FiresOut} fires out, {whole.PlantsBurnt} plants burnt down, {whole.Saves.Count} saves, {whole.Replayed} commands replayed");
        Check(whole.Ahead == 0 && whole.Used == 0 && whole.Asked == 0 && whole.LeftOut == 0 && whole.Cast > 0,
            $"switched off, the step reads everything in its own tick, as before ({whole.Cast} ground rays in steps, {whole.Ahead} ahead, {whole.LeftOut} plants left out)");
        Check(spread.Ahead > 0 && spread.Used > 0 && spread.Used > spread.Cast && spread.LeftOut > 0 && spread.Dropped > 0,
            $"spread, the steps take their ground from the ticks before ({spread.Ahead} read ahead, {spread.Used} taken, {spread.Cast} cast in steps), leave out the plants no flame is near ({spread.LeftOut}, {spread.Asked} asked), and drop what was read when the world changes ({spread.Dropped})");
        foreach (var (name, other) in new[] { ("spread", spread), ("spread with the audit", audit) })
        {
            Check(whole.Receipts.SequenceEqual(other.Receipts), $"{name}: the same receipts, byte for byte ({whole.Receipts.Count}): {FirstDifference(whole.Receipts, other.Receipts)}");
            Check(whole.Saves.SequenceEqual(other.Saves) && whole.LastSave == other.LastSave, $"{name}: the same saved state, every save of the play and the last ({whole.Saves.Count}): {FirstDifference(whole.Saves.Append(whole.LastSave).ToList(), other.Saves.Append(other.LastSave).ToList())}");
            Check(whole.Replays.SequenceEqual(other.Replays), $"{name}: the same replay of every command ({whole.Replays.Count}): {FirstDifference(whole.Replays, other.Replays)}");
            Check(whole.Nature == other.Nature, $"{name}: the same island at the end: {FirstDifference(new List<string> { whole.Nature }, new List<string> { other.Nature })}");
        }
        Check(audit.Mismatches == 0 && audit.Used > 0 && audit.LeftOut > 0,
            $"the audit: of {audit.Used} readings taken ahead and {audit.LeftOut} plants left out, none differs from the step's own ray or caught ({audit.Mismatches})");
        GD.Print($"NATURE_SPREAD: one play three ways, {whole.Receipts.Count} receipts, {whole.Saves.Count + 1} saves and {whole.Replays.Count} replays the same; whole {whole.Cast} ground rays in steps; " +
            $"spread {spread.Ahead} read ahead, {spread.Used} taken, {spread.Cast} cast in steps, {spread.Dropped} dropped, plants {spread.Asked} asked and {spread.LeftOut} left out; audit {audit.Mismatches} differ");
        await TestSpreadWindow();
    }

    /// <summary>
    /// When what was read ahead is void: a change to the world in the middle of a window drops the readings held, none is taken on
    /// the tick the change first shows (the physics has not taken it up yet), and reading starts again the tick after; a host not
    /// advanced on the very next physics frame cannot vouch for the frame it missed; and a change on the step's own tick makes the
    /// step cast for everything itself.
    /// </summary>
    private async Task TestSpreadWindow()
    {
        await NewWorld(SavePath("spread-window"), roomOverride: _spreadRoom);
        _host.NatureSampleForTests = null;
        async Task Tick()
        {
            await Frames(1);
            _host._PhysicsProcess(SpreadTick);
        }
        // To a tick in a window with readings held and the step still some ticks off.
        async Task<bool> Held()
        {
            for (var i = 0; i < 120; i++)
            {
                await Tick();
                if (_host.NatureGroundHeld >= 20 && !_host.NatureStepsNext(SpreadTick * 4)) return true;
            }
            return false;
        }
        _companion.TryTeleportTo(new Vector3(0.75f, 0.006f, 0.6f));
        for (var i = 0; i < 20; i++) await Tick();
        var lit = Fire(Player, SpreadFireAt, 0.5, 14, out _);
        var held = await Held();
        var dropped = _host.NatureGroundDropped;
        await Frames(1);
        var crate = Build("Window crate", 1.4, 1.1);
        _host._PhysicsProcess(SpreadTick);
        var onBuild = _host.NatureGroundHeld;
        var droppedByBuild = _host.NatureGroundDropped - dropped;
        await Tick();
        Check(Ok(lit) && held && crate != "" && onBuild == 0 && droppedByBuild >= 20 && _host.NatureGroundHeld > 0,
            $"a build in the middle of a window: the readings held are dropped ({droppedByBuild}), none is taken on the tick the build first shows ({onBuild}), and reading starts again on the next ({_host.NatureGroundHeld}); lit {Ok(lit)}, held {held}");
        held = await Held();
        dropped = _host.NatureGroundDropped;
        await Frames(2);
        _host._PhysicsProcess(SpreadTick);
        var onGap = _host.NatureGroundHeld;
        var droppedByGap = _host.NatureGroundDropped - dropped;
        await Tick();
        Check(held && onGap == 0 && droppedByGap >= 20 && _host.NatureGroundHeld > 0,
            $"a physics frame the host was not advanced on: what was read before it is dropped ({droppedByGap}), nothing is read on the tick after it ({onGap}), and reading starts again on the next ({_host.NatureGroundHeld})");
        // To the tick before a step, readings in hand; then a change before the step's own tick's work.
        var ready = false;
        for (var i = 0; i < 120 && !ready; i++)
        {
            await Tick();
            ready = _host.NatureStepsNext(SpreadTick) && _host.NatureGroundHeld >= 20;
        }
        var (used, cast, steps) = (_host.NatureGroundUsed, _host.NatureGroundCast, _host.NatureTicks.StepTicks);
        dropped = _host.NatureGroundDropped;
        await Frames(1);
        var removed = Send(Command(NextId("window-remove"), "entity.remove", new JsonObject { ["target"] = crate }, expectedRevision: _host.Revision), Player);
        _host._PhysicsProcess(SpreadTick);
        Check(ready && Ok(removed) && _host.NatureTicks.StepTicks == steps + 1 && _host.NatureGroundUsed == used && _host.NatureGroundCast > cast && _host.NatureGroundDropped - dropped >= 20,
            $"a change on the step's own tick: the step takes none of what was read ({_host.NatureGroundUsed - used}) and casts for all of it ({_host.NatureGroundCast - cast}); ready {ready}, removed {Ok(removed)}, dropped {_host.NatureGroundDropped - dropped}");
        // And a window nothing disturbs is taken whole.
        (used, cast, steps) = (_host.NatureGroundUsed, _host.NatureGroundCast, _host.NatureTicks.StepTicks);
        for (var i = 0; i < 40 && _host.NatureTicks.StepTicks == steps; i++) await Tick();
        for (var i = 0; i < 40 && _host.NatureTicks.StepTicks == steps + 1; i++) await Tick();
        Check(_host.NatureGroundUsed > used && _host.NatureGroundCast == cast, $"a window nothing disturbs: its step takes every reading from the ticks before ({_host.NatureGroundUsed - used} taken, {_host.NatureGroundCast - cast} cast)");
        await FreeWorld();
    }

    /// <summary>Where two lists of texts first differ ("" when they do not), with a little of each from there.</summary>
    private static string FirstDifference(List<string> a, List<string> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : "(none)";
            var y = i < b.Count ? b[i] : "(none)";
            if (x == y) continue;
            var at = 0;
            while (at < x.Length && at < y.Length && x[at] == y[at]) at++;
            var from = Math.Max(0, at - 80);
            return $"item {i} at {at}: \"{x[from..Math.Min(x.Length, at + 80)]}\" against \"{y[from..Math.Min(y.Length, at + 80)]}\"";
        }
        return "";
    }

    private async Task<SpreadRun> SpreadPlay(string name, bool spread, bool audit, int ids, DateTime now)
    {
        var run = new SpreadRun();
        var file = ProjectSettings.GlobalizePath(SavePath(name));
        var tokens = new List<string>();
        var sent = new List<(JsonObject Message, string Principal)>();
        var tick = 0;
        var burning = false;
        // What the host draws at random: a preview's token (in the preview's receipt and the cast's), and with it the print the
        // save keeps of the cast's message.
        string Even(string text)
        {
            for (var i = 0; i < tokens.Count; i++) text = text.Replace(tokens[i], $"preview-{i}");
            text = Regex.Replace(text, "\"(fingerprint|content_sha256)\":\"[0-9a-f]{64}\"", "\"$1\":\"-\"");
            return Regex.Replace(text, "(entry|job)-[a-z2-7]{26}", "$1-drawn");
        }
        async Task Host()
        {
            await NewWorld(SavePath(name), roomOverride: _spreadRoom);
            _host.NatureSampleForTests = null;
            _host.NatureStepSpread = spread;
            _host.NatureAheadAudit = audit;
            _host.NatureSaveIntervalS = 2;
        }
        void Count()
        {
            run.Ahead += _host.NatureGroundAhead;
            run.Used += _host.NatureGroundUsed;
            run.Cast += _host.NatureGroundCast;
            run.Dropped += _host.NatureGroundDropped;
            run.Asked += _host.NaturePlantsAsked;
            run.LeftOut += _host.NaturePlantsLeftOut;
            run.Mismatches += _host.NatureAheadMismatches;
        }
        JsonObject Do(string id, string op, JsonObject args, string principal = Player, bool preview = false, bool revision = false)
        {
            var message = Command(NextId(id), op, args, preview, revision ? _host.Revision : null);
            var result = Send(message, principal);
            if (preview && result["data"]?["preview_id"]?.GetValue<string>() is { } token) tokens.Add(token);
            if (!preview) sent.Add(((JsonObject)message.DeepClone(), principal));
            run.Receipts.Add(Even($"tick {tick} {op}: {Canonical(result)}"));
            return result;
        }
        JsonObject Light(Vector3 centre, double radius, double duration)
        {
            var preview = Do("spread-preview", "nature.cast", Cast("fire", centre, radius, duration), preview: true);
            return Do("spread-fire", "nature.cast", Cast("fire", centre, radius, duration, preview["data"]?["preview_id"]?.GetValue<string>()));
        }
        JsonObject Place(string label, double x, double z) =>
            Do("spread-build", "creation.place", new JsonObject { ["source"] = Source(label), ["placement"] = new JsonObject { ["position_m"] = new JsonArray(x, 0, z) } });

        await Host();
        _ids = ids;
        _now = now;
        JsonObject? lit = null, built = null, grabbed = null, released = null, early = null, grown = null, removed = null, rebuilt = null, tree = null, undone = null;
        // Each command at a tick in the middle of a step's window (steps fall on every thirtieth tick of a host), before that tick's work.
        var plan = new Dictionary<int, Action>
        {
            [20] = () => _companion.TryTeleportTo(new Vector3(0.75f, 0.006f, 0.6f)),
            [46] = () => lit = Light(SpreadFireAt, 0.5, 12),
            [105] = () => built = Place("Spread crate", 1.4, 1.1),
            [130] = () => _companion.TryTeleportTo(new Vector3(0.45f, 0.006f, 0.3f)),
            [137] = () => grabbed = Do("spread-grab", "entity.grab", new JsonObject { ["target"] = "obj:book", ["actor"] = CommandHost.CompanionAvatarId }, Companion),
            [168] = () => released = Do("spread-drop", "entity.release", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }, Companion),
            [197] = () => early = Do("spread-grow-early", "nature.cast", Cast("grow", NearGubble, 0.08, 1)),
            [226] = () => removed = Do("spread-remove", "entity.remove", new JsonObject { ["target"] = built?["created"]?[0]?.GetValue<string>() ?? "" }, revision: true),
            [445] = () => rebuilt = Place("Spread box", 1.2, 0.8),
            [860] = () => grown = Do("spread-grow", "nature.cast", Cast("grow", NearGubble, 0.08, 1)),
            [900] = () => _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f),
            [905] = () => tree = Light(TreeAt + new Vector3(-0.1f, 0.01f, 0), 0.3, 15),
            [1900] = () => undone = Do("spread-undo", "room.undo", new JsonObject { ["to_revision"] = _host.Nature.UndoTarget(_host.Revision) ?? 0 }, revision: true),
        };
        while (tick < SpreadTicks)
        {
            await Frames(1);
            tick++;
            if (plan.TryGetValue(tick, out var act)) act();
            _host._PhysicsProcess(SpreadTick);
            if (burning && _host.Nature.Fire == null)
            {
                run.FiresOut++;
                run.Receipts.Add($"tick {tick}: the fire is out");
            }
            burning = _host.Nature.Fire != null;
            if (tick == 1890) run.PlantsBurnt = _host.Nature.Plants.Values.Count(p => p.Burnt);
            if (tick % 30 == 0 && System.IO.File.Exists(file))
            {
                var saved = Even(System.IO.File.ReadAllText(file));
                if (run.Saves.Count == 0 || run.Saves[^1] != saved) run.Saves.Add(saved);
            }
            if (tick != SpreadReloadTick) continue;
            // The play stops while the fire burns and goes on from its save: what the save holds is all the second half has.
            Count();
            await Host();
        }
        run.Happened = lit != null && Ok(lit) && built != null && Ok(built) && grabbed != null && Ok(grabbed) && released != null && Ok(released) && early != null && Code(early) == "target_busy" && grown != null && Ok(grown) &&
            removed != null && Ok(removed) && rebuilt != null && Ok(rebuilt) && tree != null && Ok(tree) && undone != null && Ok(undone);
        run.Notes = string.Join(", ", new (string Label, JsonObject? Result)[] { ("fire", lit), ("build", built), ("grab", grabbed), ("drop", released), ("grow in the fire (refused)", early), ("grow", grown), ("remove", removed), ("build again", rebuilt), ("tree fire", tree), ("undo", undone) }
            .Select(r => r.Label + " " + (r.Result == null ? "not sent" : Ok(r.Result) ? "ok" : Code(r.Result) + " " + r.Result["error"]?["message"])));
        // The replay: every command of the play sent again as it was. The host answers each from its durable receipt.
        foreach (var (message, principal) in sent)
        {
            var again = Send(message, principal);
            if (again["replayed"]?.GetValue<bool>() == true) run.Replayed++;
            run.Replays.Add(Even($"{message["op"]}: {Canonical(again)}"));
        }
        run.Nature = Canonical(NatureBlock.Pack(_host.Nature));
        Count();
        await FreeWorld();
        run.LastSave = System.IO.File.Exists(file) ? Even(System.IO.File.ReadAllText(file)) : "";
        return run;
    }
}
