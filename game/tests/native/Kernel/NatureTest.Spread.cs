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
/// with the step whole in its tick (NatureStepSpread off, as before LOOK-3), with its reading spread, and spread with the audit
/// (every reading taken ahead cast again at the step, every plant left out watched). The play changes the ground under its fire, so
/// a reading taken before a change would be wrong after it: a book is put down on burning ground two ticks before a step, picked up
/// again on a step's own tick, and put down in the middle of a window; a low dome is built on burning ground and removed on a
/// step's own tick; and a lamp with a rotor runs on a timer the whole time (a running effect must not stop the reading). It also
/// builds in the fire, asks for Grow (refused while the fire burns), stops and loads the save while the fire burns, builds again,
/// casts Grow once the fire is out, burns the tree and takes that fire back. Compared between the three, byte for byte: every
/// receipt and the tick each fire went out on, every save the play wrote (looked at twice a second), the replay of every command
/// sent again at the end, the island at the end and the last save. Only what the host draws at random is evened first (a
/// preview's token, and the print of a message that carries it). A fourth play, with the host blind to the world's mark, shows the
/// play can tell: its island comes out different, and the audit counts the readings that went stale.
/// Then, part by part: what voids a reading (TestSpreadWindow), the tick a moved collider needs to reach the physics, in what the
/// island comes to (TestSpreadSettle), each part of the mark on its own (TestSpreadMark), the land's and the plant copies'
/// collision tiles on a wide land (TestSpreadLand), and the plants the step may not leave out (TestSpreadPlants).
/// </summary>
public partial class NatureTest
{
    private const double SpreadTick = 1.0 / 60;
    private const int SpreadReloadTick = 400;
    private const int SpreadTicks = 1930;
    private const CommandHost.WorldMarkPart NoPart = CommandHost.WorldMarkPart.None;
    private const CommandHost.WorldMarkPart AllParts = CommandHost.WorldMarkPart.All;
    /// <summary>The first fire: half a metre wide on the bare floor east of the spawns, more cells than may burn at once, so it burns as a front.</summary>
    private static readonly Vector3 SpreadFireAt = new(1.3f, 0.01f, 0.95f);
    /// <summary>Where the Gubble stands in that fire to put the book down and pick it up, and the two places the book is put.</summary>
    private static readonly Vector3 SpreadGubbleAt = new(1.12f, 0.006f, 0.8f);
    private static readonly Vector3 SpreadBookAt = new(1.27f, 0, 0.93f), SpreadBookThenAt = new(1.3f, 0, 0.72f);

    /// <summary>
    /// The test room with the suite's plants and a field of tufts under the first fire: 25 of them, most away from the flames at any
    /// one step; dense, 81 (with the suite's five, more than a tick measures: PlantBoxesPerTick).
    /// </summary>
    private static RoomData SpreadRoom(bool dense = false)
    {
        var at = TuftsAt.ToList();
        var side = dense ? 9 : 5;
        for (var x = 0; x < side; x++)
            for (var z = 0; z < side; z++)
                at.Add(dense ? new Vector2(0.9f + 0.1f * x, 0.5f + 0.1f * z) : new Vector2(0.9f + 0.2f * x, 0.6f + 0.175f * z));
        var grass = at.Select((spot, i) => new IslandFixture.Row(spot.X, 0, spot.Y, Mathf.PosMod(11f * i + 20f, 360f) - 180f, Vector3.One, new Color(0.85f, 0.95f, 0.7f), 0.13f, 0, 0, (uint)(100 + i))).ToList();
        var tree = IslandFixture.Broadleaf(TreeAt.X, TreeAt.Z, 0, 0, 31);
        var name = dense ? "spread-room-dense" : "spread-room";
        return RoomData.Load(IslandFixture.WithPlants(RoomWorld.DefaultRoom, $"{TestRoot}/{name}/test_room", new[] { IslandFixture.Grass("grass", grass), IslandFixture.Trees(new[] { tree }) }));
    }

    /// <summary>A lamp that lights itself and turns its rotor on a timer, twice a second: an effect that is always running. Copper: no fire asks about it.</summary>
    private static JsonObject LampSource(string name) => new()
    {
        ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = name, ["seed"] = 1, ["mount"] = "ground",
        ["parts"] = new JsonArray(
            new JsonObject { ["id"] = "base", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.06, 0), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = "copper" },
            new JsonObject { ["id"] = "top", ["shape"] = "rotor", ["position_m"] = new JsonArray(0, 0.18, 0), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.2, 0.1, 0.2), ["material"] = "copper" }),
        ["nodes"] = new JsonArray(
            new JsonObject { ["id"] = "tick", ["op"] = "timer", ["part_id"] = "base", ["params"] = new JsonObject { ["interval_s"] = 0.5 } },
            new JsonObject { ["id"] = "glow", ["op"] = "light", ["part_id"] = "base", ["params"] = new JsonObject { ["intensity"] = 1, ["duration_s"] = 2 } },
            new JsonObject { ["id"] = "turn", ["op"] = "spin", ["part_id"] = "top", ["params"] = new JsonObject { ["speed_rpm"] = 45, ["duration_s"] = 2 } }),
        ["edges"] = new JsonArray(new JsonObject { ["from"] = "tick", ["to"] = "glow" }, new JsonObject { ["from"] = "tick", ["to"] = "turn" }),
    };

    /// <summary>
    /// A low copper dome, 30 cm wide and 10 cm at its very middle (no part may be lower): everywhere else its top is under the 10 cm a
    /// ground ray starts from, so the ray meets the dome and not the floor under it.
    /// </summary>
    private static JsonObject DomeSource(string name)
    {
        var dome = Source(name, "copper");
        dome["parts"]![0]!["shape"] = "sphere";
        dome["parts"]![0]!["position_m"] = new JsonArray(0, 0.05, 0);
        dome["parts"]![0]!["size_m"] = new JsonArray(0.3, 0.1, 0.3);
        return dome;
    }

    private sealed class SpreadRun
    {
        public readonly List<string> Receipts = new();
        public readonly List<string> Saves = new();
        public readonly List<string> Replays = new();
        public readonly List<string> Alarms = new();
        public string Nature = "", LastSave = "", Notes = "", Moves = "";
        public long Ahead, Used, Cast, Dropped, Asked, LeftOut, Mismatches, SpotChecked, SpotFailures, Moving;
        public int Replayed, FiresOut, PlantsBurnt, LampTicks;
        public bool Happened;
    }

    private RoomData _spreadRoom = null!;

    private async Task TestSpread()
    {
        var ids = _ids;
        var now = _now;
        _spreadRoom = SpreadRoom();
        await TestSpreadSwitch();
        var whole = await SpreadPlay("spread-whole", spread: false, audit: false, ids, now);
        var spread = await SpreadPlay("spread-on", spread: true, audit: false, ids, now);
        var audit = await SpreadPlay("spread-audit", spread: true, audit: true, ids, now);
        var blind = await SpreadPlay("spread-blind", spread: true, audit: true, ids, now, blind: AllParts, spotChecks: 0);
        Check(whole.Happened && whole.FiresOut == 2 && whole.PlantsBurnt >= 3 && whole.Saves.Count >= 6 && whole.Replayed >= 6,
            $"the play does what it says: {whole.Notes}; {whole.FiresOut} fires out, {whole.PlantsBurnt} plants burnt down, {whole.Saves.Count} saves, {whole.Replayed} commands replayed");
        Check(whole.Ahead == 0 && whole.Used == 0 && whole.Asked == 0 && whole.LeftOut == 0 && whole.Cast > 0,
            $"switched off, the step reads everything in its own tick, as before ({whole.Cast} ground rays in steps, {whole.Ahead} ahead, {whole.LeftOut} plants left out)");
        Check(spread.Ahead > 0 && spread.Used > 0 && spread.Used > spread.Cast && spread.LeftOut > 0 && spread.Dropped > 0,
            $"spread, the steps take their ground from the ticks before ({spread.Ahead} read ahead, {spread.Used} taken, {spread.Cast} cast in steps), leave out the plants no flame is near ({spread.LeftOut}, {spread.Asked} asked), and drop what was read when the world changes ({spread.Dropped}; the mark moved: {spread.Moves})");
        Check(spread.LampTicks > SpreadTicks * 3 / 4 && spread.Moving == 0 && spread.Used > spread.Cast,
            $"a lamp and its rotor run on a timer through the play (an effect running on {spread.LampTicks} of {SpreadTicks} ticks): they move no collision, so the reading goes on ({spread.Used} taken, {spread.Moving} ticks without a mark)");
        Check(spread.SpotChecked > 0 && spread.SpotFailures == 0 && spread.Alarms.Count == 0 && audit.SpotFailures == 0 && audit.Alarms.Count == 0,
            $"the step's spot check casts a few readings again each step ({spread.SpotChecked}) and finds every one as it was read ({spread.SpotFailures} differ{(spread.Alarms.Count > 0 ? ": " + spread.Alarms[0] : "")})");
        var same = true;
        foreach (var (name, other) in new[] { ("spread", spread), ("spread with the audit", audit) })
        {
            var receipts = whole.Receipts.SequenceEqual(other.Receipts);
            var saves = whole.Saves.SequenceEqual(other.Saves) && whole.LastSave == other.LastSave;
            var replays = whole.Replays.SequenceEqual(other.Replays);
            var island = whole.Nature == other.Nature;
            same &= receipts && saves && replays && island;
            Check(receipts, $"{name}: the same receipts, byte for byte ({whole.Receipts.Count}): {FirstDifference(whole.Receipts, other.Receipts)}");
            Check(saves, $"{name}: the same saved state, every save of the play and the last ({whole.Saves.Count}): {FirstDifference(whole.Saves.Append(whole.LastSave).ToList(), other.Saves.Append(other.LastSave).ToList())}");
            Check(replays, $"{name}: the same replay of every command ({whole.Replays.Count}): {FirstDifference(whole.Replays, other.Replays)}");
            Check(island, $"{name}: the same island at the end: {FirstDifference(new List<string> { whole.Nature }, new List<string> { other.Nature })}");
        }
        Check(audit.Mismatches == 0 && audit.Used > 0 && audit.LeftOut > 0,
            $"the audit: of {audit.Used} readings taken ahead and {audit.LeftOut} plants left out, none differs from the step's own ray or caught ({audit.Mismatches})");
        // The play can tell: a host blind to the mark takes readings that went stale, the audit counts them, and the island differs.
        var blindDiffers = blind.Nature != whole.Nature || !blind.Saves.SequenceEqual(whole.Saves);
        Check(blind.Mismatches > 0 && blindDiffers,
            $"with the host blind to the world's mark the same play goes wrong, so the play can tell: the audit counts {blind.Mismatches} readings that no longer held, and the island {(blindDiffers ? "differs" : "is the same")}: {FirstDifference(new List<string> { whole.Nature }, new List<string> { blind.Nature })}");
        GD.Print($"NATURE_SPREAD: one play three ways, {whole.Receipts.Count} receipts, {whole.Saves.Count + 1} saves and {whole.Replays.Count} replays {(same ? "the same" : "NOT THE SAME")}; whole {whole.Cast} ground rays in steps; " +
            $"spread {spread.Ahead} read ahead, {spread.Used} taken, {spread.Cast} cast in steps, {spread.Dropped} dropped (the mark moved: {spread.Moves}), {spread.SpotChecked} spot-checked, plants {spread.Asked} asked and {spread.LeftOut} left out; " +
            $"audit {audit.Mismatches} differ; blind to the mark, the audit counts {blind.Mismatches} and the island {(blindDiffers ? "differs" : "IS THE SAME")}");
        await TestSpreadWindow();
        await TestSpreadSettle(ids, now);
        await TestSpreadMark();
        await TestSpreadLand();
        await TestSpreadPlants(ids, now);
    }

    /// <summary>
    /// The launch switch (the frame timing's before and after): the variable starts a host with the step whole; the argument does
    /// too, and wins over the variable. Run with "-- --nature-spread=off", this suite passes too, and its hosts start whole.
    /// </summary>
    private async Task TestSpreadSwitch()
    {
        var launchedOff = CommandHost.NatureSpreadOff(OS.GetCmdlineUserArgs(), null);
        OS.SetEnvironment(CommandHost.NatureSpreadVariable, "off");
        await NewWorld(SavePath("spread-switch"));
        var off = _host.NatureStepSpread;
        OS.UnsetEnvironment(CommandHost.NatureSpreadVariable);
        await NewWorld(SavePath("spread-switch"));
        Check(!off && _host.NatureStepSpread == !launchedOff,
            $"{CommandHost.NatureSpreadVariable}=off starts a host with the step whole in its tick; without it the reading is spread{(launchedOff ? " (not in this run: it was launched with " + CommandHost.NatureSpreadArgument + "off, and its hosts start whole)" : "")}");
        var none = Array.Empty<string>();
        Check(CommandHost.NatureSpreadOff(new[] { "--nature-spread=off" }, null) && CommandHost.NatureSpreadOff(new[] { "--room=somewhere", "--nature-spread=off" }, "on") &&
              !CommandHost.NatureSpreadOff(new[] { "--nature-spread=on" }, "off") && !CommandHost.NatureSpreadOff(new[] { "--nature-spread=OFF" }, null) &&
              CommandHost.NatureSpreadOff(none, "off") && CommandHost.NatureSpreadOff(new[] { "--nature-spread=" }, "off") && !CommandHost.NatureSpreadOff(none, "") && !CommandHost.NatureSpreadOff(none, null) &&
              !CommandHost.NatureSpreadOff(new[] { "--nature-spread" }, null),
            "the launch argument --nature-spread=off switches the spread off, and wins over the variable; an argument that says anything else keeps it on; with no argument the variable decides");
    }

    /// <summary>
    /// When what was read ahead is void: a change to the world in the middle of a window drops the readings held, none is taken on
    /// the tick the change first shows (the physics has not taken it up yet), and reading starts again the tick after; a host not
    /// advanced on the very next physics frame cannot vouch for the frame it missed, between steps or just before one; and a change
    /// on the step's own tick makes the step cast for everything itself.
    /// </summary>
    private async Task TestSpreadWindow()
    {
        await NewWorld(SavePath("spread-window"), roomOverride: _spreadRoom);
        _host.NatureSampleForTests = null;
        _host.NatureStepSpread = true;
        var alarms = new List<string>();
        _host.NatureSpreadAlarmForTests = alarms.Add;
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
        // To the tick before a step, readings in hand.
        async Task<bool> BeforeStep()
        {
            for (var i = 0; i < 120; i++)
            {
                await Tick();
                if (_host.NatureTicksToStep(SpreadTick) == 1 && _host.NatureGroundHeld >= 20) return true;
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
        // The frame missed is the one just before the step: the step takes nothing read before the gap.
        var ready = await BeforeStep();
        var (used, cast, steps) = (_host.NatureGroundUsed, _host.NatureGroundCast, _host.NatureTicks.StepTicks);
        dropped = _host.NatureGroundDropped;
        await Frames(2);
        _host._PhysicsProcess(SpreadTick);
        Check(ready && _host.NatureTicks.StepTicks == steps + 1 && _host.NatureGroundUsed == used && _host.NatureGroundCast > cast && _host.NatureGroundDropped - dropped >= 20,
            $"a physics frame missed just before the step: the step takes none of what was read before it ({_host.NatureGroundUsed - used}) and casts for all of it ({_host.NatureGroundCast - cast}); ready {ready}, dropped {_host.NatureGroundDropped - dropped}");
        // A change before the step's own tick's work.
        ready = await BeforeStep();
        (used, cast, steps) = (_host.NatureGroundUsed, _host.NatureGroundCast, _host.NatureTicks.StepTicks);
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
        Check(_host.NatureGroundUsed > used && _host.NatureGroundCast == cast && alarms.Count == 0, $"a window nothing disturbs: its step takes every reading from the ticks before ({_host.NatureGroundUsed - used} taken, {_host.NatureGroundCast - cast} cast)");
        await FreeWorld();
    }

    /// <summary>
    /// The settling tick, in what the island comes to. A collider moved by its node reaches the physics only when the frame's
    /// transforms are flushed, after this tick's work. A later lane's thing (here a low slab nothing names, so no ground for a fire)
    /// is moved onto burning ground on the tick before a step and noted, then moved off again on the tick before a later step: a
    /// reading taken on the tick of the move would still meet the world as it was. Played with the step whole, spread, and spread
    /// with the audit (the spot check off, so nothing else catches it): the same island, none counted. And the slab matters: the
    /// island without it is another.
    /// </summary>
    private async Task TestSpreadSettle(int ids, DateTime now)
    {
        async Task<(string Nature, long Mismatches, long Used, long Dropped, int Under, bool Happened)> Play(string name, bool spread, bool audit, bool moves)
        {
            await NewWorld(SavePath(name), roomOverride: _spreadRoom);
            _ids = ids;
            _now = now;
            _host.NatureSampleForTests = null;
            _host.NatureStepSpread = spread;
            _host.NatureAheadAudit = audit;
            _host.NatureSpotChecks = 0;
            var away = new Vector3(-1.2f, 0.04f, 0.9f);
            var slab = new StaticBody3D { Name = "SettleSlab", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = away };
            slab.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.3f, 0.08f, 0.3f) } });
            _world!.AddChild(slab);
            _companion.TryTeleportTo(new Vector3(0.75f, 0.006f, 0.6f));
            for (var i = 0; i < 20; i++)
            {
                await Frames(1);
                _host._PhysicsProcess(SpreadTick);
            }
            var lit = Fire(Player, SpreadFireAt, 0.5, 14, out _);
            var first = _host.NatureTicks.StepTicks;
            var (on, off, under) = (false, false, 0);
            for (var i = 0; i < 1000; i++)
            {
                await Frames(1);
                var steps = _host.NatureTicks.StepTicks - first;
                if (moves && _host.NatureTicksToStep(SpreadTick) == 2 && ((steps == 4 && !on) || (steps == 9 && !off)))
                {
                    if (steps == 4)
                    {
                        // Onto the middle of what burns now (the same cell in every play: they are one play up to here).
                        var burning = _host.Nature.Cells.Values.Where(c => c.BurningS > 0).OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
                        if (burning.Count > 0)
                        {
                            var middle = burning[burning.Count / 2].Position;
                            slab.Position = new Vector3(middle.X, 0.04f, middle.Z);
                            under = burning.Count(c => Math.Abs(c.Position.X - middle.X) < 0.15f && Math.Abs(c.Position.Z - middle.Z) < 0.15f);
                        }
                        on = true;
                    }
                    else
                    {
                        slab.Position = away;
                        off = true;
                    }
                    _host.NoteWorldCollisionChanged();
                }
                _host._PhysicsProcess(SpreadTick);
            }
            var happened = Ok(lit) && _host.Nature.Fire == null && (!moves || (on && off && under > 0));
            var result = (Canonical(NatureBlock.Pack(_host.Nature)), _host.NatureAheadMismatches, _host.NatureGroundUsed, _host.NatureGroundDropped, under, happened);
            await FreeWorld();
            return result;
        }
        var whole = await Play("spread-settle-whole", spread: false, audit: false, moves: true);
        var spread = await Play("spread-settle-on", spread: true, audit: false, moves: true);
        var audit = await Play("spread-settle-audit", spread: true, audit: true, moves: true);
        var bare = await Play("spread-settle-bare", spread: false, audit: false, moves: false);
        Check(whole.Happened && spread.Happened && audit.Happened && bare.Happened && spread.Used > 0 && spread.Dropped > 0 && whole.Nature != bare.Nature &&
              spread.Nature == whole.Nature && audit.Nature == whole.Nature && audit.Mismatches == 0,
            $"a collider moved by its node on the tick before a step, and noted (onto {whole.Under} burning cells, off again 2.5 s later): no reading is taken on the tick of the move, when the physics still holds the old place; " +
            $"the same island as with the step whole: {FirstDifference(new List<string> { whole.Nature }, new List<string> { spread.Nature })}{FirstDifference(new List<string> { whole.Nature }, new List<string> { audit.Nature })}; " +
            $"the audit counts {audit.Mismatches}; the slab matters (the island without it is {(whole.Nature != bare.Nature ? "another" : "THE SAME")}); {spread.Used} readings taken ahead, {spread.Dropped} dropped; " +
            $"happened {whole.Happened} {spread.Happened} {audit.Happened} {bare.Happened}");
    }

    /// <summary>
    /// The world's mark, part by part (CommandHost.WorldMarkPart): with the host blind to every part but one, that part's own change
    /// voids the readings held, and the host counts the move as that part's. So a mark that lost a part fails here. Then the guards
    /// behind the mark: a collider moved where it stands is seen when its lane says so (NoteWorldCollisionChanged), and when nobody
    /// says so, the step's spot check finds the stale reading, switches the reading ahead off for this host and says why.
    /// </summary>
    private async Task TestSpreadMark()
    {
        await NewWorld(SavePath("spread-mark"), roomOverride: _spreadRoom);
        _host.NatureSampleForTests = null;
        _host.NatureStepSpread = true;
        var alarms = new List<string>();
        _host.NatureSpreadAlarmForTests = alarms.Add;
        async Task Tick()
        {
            await Frames(1);
            _host._PhysicsProcess(SpreadTick);
        }
        var fires = 0;
        // A fire that keeps burning: when one is out, the next is lit beside where it was (burnt ground does not burn again).
        var hearths = new[] { SpreadFireAt, new Vector3(1.25f, 0.01f, -0.35f), new Vector3(-0.9f, 0.01f, 0.2f), new Vector3(1.3f, 0.01f, -1.0f) };
        async Task<bool> Held(int clear = 6)
        {
            for (var i = 0; i < 400; i++)
            {
                if (_host.Nature.Fire == null && fires < hearths.Length && !Ok(Fire(Player, hearths[fires++], 0.5, 30, out _))) return false;
                await Tick();
                if (_host.NatureGroundHeld >= 20 && _host.NatureTicksToStep(SpreadTick) > clear) return true;
            }
            return false;
        }
        // One part alone sees: its change, made before a tick's work, voids the readings held (by the next tick: the plants' version
        // and the land's tiles move at the end of the tick the change is made in), and the host counts it as that part's move.
        async Task Alone(CommandHost.WorldMarkPart part, string what, Action change)
        {
            _host.NatureMarkBlindForTests = AllParts & ~part;
            var held = await Held();
            var (dropped, moved, had) = (_host.NatureGroundDropped, _host.NatureMarkMoved(part), _host.NatureGroundHeld);
            await Frames(1);
            change();
            _host._PhysicsProcess(SpreadTick);
            if (_host.NatureGroundDropped == dropped) await Tick();
            var gone = _host.NatureGroundDropped - dropped;
            Check(held && had >= 20 && gone >= had && _host.NatureMarkMoved(part) > moved,
                $"the mark's {part} alone: {what} voids the readings held ({gone} dropped of {had} held; counted as its move {_host.NatureMarkMoved(part) - moved} time(s); held {held}, fires lit {fires})");
            _host.NatureMarkBlindForTests = NoPart;
        }
        // Copper: no fire asks about these builds, wherever it is lit.
        JsonObject Place(JsonObject source, double x, double z) =>
            Send(Command(NextId("mark-build"), "creation.place", new JsonObject { ["source"] = source, ["placement"] = new JsonObject { ["position_m"] = new JsonArray(x, 0, z) } }), Player);
        string Crate(string name, double x, double z) => Place(Source(name, "copper"), x, z)["created"]?[0]?.GetValue<string>() ?? "";
        int Running()
        {
            using var value = _host.Runtime.Get("animations");
            using var list = value.AsGodotArray();
            return list.Count;
        }

        // A lamp on a timer, lit and turning before the fire and all through it (the player lets creations run their effects).
        _host.Runtime.Call("set_local_consent", true);
        var lamp = Place(LampSource("Mark lamp"), 0.3, -0.8);
        _companion.TryTeleportTo(new Vector3(0.45f, 0.006f, 0.3f));
        for (var i = 0; i < 40; i++) await Tick();
        var firstHeld = await Held();
        var (usedBefore, effects) = (_host.NatureGroundUsed, 0);
        for (var i = 0; i < 90; i++)
        {
            await Tick();
            if (Running() > 0) effects++;
        }
        Check(Ok(lamp) && firstHeld && effects > 60 && _host.NatureGroundUsed > usedBefore && _host.NatureMarkMoved(CommandHost.WorldMarkPart.Moving) == 0,
            $"a creation's light and rotor running ({effects} of 90 ticks) stop nothing: the steps go on taking their readings ({_host.NatureGroundUsed - usedBefore} taken), and no tick is without a mark; lamp {Canonical(lamp["error"])}");

        var crate = "";
        await Alone(CommandHost.WorldMarkPart.Revision, "a build (the room revision)", () => crate = Crate("Mark crate", 0.2, -0.3));
        StaticBody3D? stone = null;
        await Alone(CommandHost.WorldMarkPart.Colliders, "a body come into the tree (no command, no revision)", () =>
        {
            stone = new StaticBody3D { Name = "MarkStone", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = new Vector3(-1.2f, 0.02f, 0.9f) };
            stone.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.3f, 0.04f, 0.3f) } });
            _world!.AddChild(stone);
        });
        // Blind to the room revision, which moves with it: the permission revision alone.
        await Alone(CommandHost.WorldMarkPart.Permissions, "the Gubble made a visitor (the permission revision)", () => _host.Runtime.Call("set_companion_builder", false));
        _host.Runtime.Call("set_companion_builder", true);
        await Alone(CommandHost.WorldMarkPart.Hands, "a thing picked up (no revision: a hold is not saved)",
            () => Check(Ok(Send(Command(NextId("mark-grab"), "entity.grab", new JsonObject { ["target"] = "obj:book", ["actor"] = CommandHost.CompanionAvatarId }, expectedRevision: null), Companion)), "(the Gubble picks the book up)"));
        await Alone(CommandHost.WorldMarkPart.Scene, "a thing shown burnt (the burnt things the scene shows)", () => _host.ScorchForTests(crate));
        // The copies' view gone for a tick, as when a room is built again: the host's plants have no view (the plants' version moves),
        // and the view the mark knew is not there (the copies' view).
        var view = Copies;
        var plants = _host.Nature.Plants.Count;
        await Alone(CommandHost.WorldMarkPart.Plants, "the plants the scene shows changing (their view gone)", () => view.Name = "CopiesAway");
        view.Name = PlantCopiesView.ViewName;
        await Alone(CommandHost.WorldMarkPart.CopiesView, "another view of the plant copies", () => view.Name = "CopiesAway");
        view.Name = PlantCopiesView.ViewName;
        var built = _world!.GetNode<Node3D>("Room");
        await Alone(CommandHost.WorldMarkPart.RoomNode, "another room node", () => built.Name = "RoomAway");
        built.Name = "Room";
        Check(plants > 0 && Copies == view, $"(the fire had caught plants by then, {plants}, and the views are back under their names)");
        // A collider moved where it stands: no node comes or goes, no command runs. The lane that moves it says so.
        await Alone(CommandHost.WorldMarkPart.Noted, "a collider moved where it stands, and noted", () =>
        {
            stone!.Position = new Vector3(-1.2f, 0.02f, 0.5f);
            _host.NoteWorldCollisionChanged();
        });
        // A part with a collider turning: there is no mark while it turns, and the reading starts again when it stops.
        // The compiler lets only a rotor spin today, so the test sets a box turning itself, as the runtime would: its own animation
        // record, in the runtime's own list.
        var turner = Crate("Mark turner", 0.2, -0.55);
        Node3D? turning = null;
        var movingBefore = _host.NatureMarkMoved(CommandHost.WorldMarkPart.Moving);
        await Alone(CommandHost.WorldMarkPart.Moving, "a part with a collider set turning", () =>
        {
            using var assemblies = _host.Runtime.Get("assemblies").AsGodotDictionary();
            using var running = _host.Runtime.Get("animations").AsGodotArray();
            turning = (Node3D)((Node3D)assemblies[turner].AsGodotObject()).GetMeta("parts").AsGodotDictionary()["base"].AsGodotObject();
            running.Add(new Godot.Collections.Dictionary
            {
                ["instance_id"] = turner, ["node_id"] = "turn", ["by"] = "player", ["part"] = turning, ["op"] = "spin",
                ["params"] = new Godot.Collections.Dictionary { ["speed_rpm"] = 30.0, ["duration_s"] = 0.3 }, ["until"] = _host.Runtime.Get("clock_s").AsDouble() + 0.3, ["angle"] = 0.0,
                ["base"] = turning.GetMeta("authored_basis"),
            });
        });
        var without = 0;
        for (var i = 0; i < 12; i++)
        {
            await Tick();
            if (_host.NatureGroundHeld == 0) without++;
        }
        var turned = turning != null && !turning.Basis.IsEqualApprox(turning.GetMeta("authored_basis").AsBasis());
        for (var i = 0; i < 12; i++) await Tick();
        var again = await Held(2);
        Check(turner != "" && turned && without >= 8 && _host.NatureMarkMoved(CommandHost.WorldMarkPart.Moving) - movingBefore >= 8 && again,
            $"while the part turns (turned: {turned}) nothing is read ahead ({without} of 12 ticks with nothing held, {_host.NatureMarkMoved(CommandHost.WorldMarkPart.Moving) - movingBefore} ticks without a mark), and the reading starts again when it stops ({again})");

        // Nobody says so: a collider moved onto burning ground, unseen by the mark. The spot check (here every reading) finds it.
        _host.NatureSpotChecks = int.MaxValue;
        var beforeStep = false;
        for (var i = 0; i < 400 && !beforeStep; i++)
        {
            if (_host.Nature.Fire == null && fires < hearths.Length) Fire(Player, hearths[fires++], 0.5, 30, out _);
            await Tick();
            beforeStep = _host.NatureTicksToStep(SpreadTick) == 3 && _host.NatureGroundHeld >= 20;
        }
        var burning = _host.Nature.Cells.Values.Where(c => c.BurningS > 0).OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
        var under = burning.Count > 0 ? burning[burning.Count / 2].Position : Vector3.Zero;
        var (takenBefore, castBefore, checkedBefore) = (_host.NatureGroundUsed, _host.NatureGroundCast, _host.NatureGroundSpotChecked);
        await Frames(1);
        stone!.Position = new Vector3(under.X, 0.02f, under.Z);
        _host._PhysicsProcess(SpreadTick);
        var stillHeld = _host.NatureGroundHeld;
        await Tick();
        await Tick();
        Check(beforeStep && burning.Count > 0 && stillHeld >= 20 && _host.NatureSpotCheckFailures >= 1 && _host.NatureSpreadDistrusted && alarms.Count == 1 && alarms[0].StartsWith("NATURE_SPREAD", StringComparison.Ordinal) &&
              _host.NatureGroundUsed == takenBefore && _host.NatureGroundCast > castBefore && _host.NatureGroundSpotChecked > checkedBefore,
            $"a collider moved onto burning ground with nobody saying so: the mark stands ({stillHeld} readings still held), the step's spot check finds a reading that no longer holds ({_host.NatureSpotCheckFailures}), " +
            $"the step takes none ({_host.NatureGroundUsed - takenBefore}) and casts for itself ({_host.NatureGroundCast - castBefore}), and the host says so once ({alarms.Count}): {alarms.FirstOrDefault()}");
        var (aheadAfter, stepsAfter) = (_host.NatureGroundAhead, _host.NatureTicks.StepTicks);
        castBefore = _host.NatureGroundCast;
        for (var i = 0; i < 70; i++) await Tick();
        Check(_host.NatureGroundAhead == aheadAfter && _host.NatureGroundHeld == 0 && _host.NatureTicks.StepTicks >= stepsAfter + 2 && (_host.Nature.Fire == null || _host.NatureGroundCast > castBefore) && alarms.Count == 1,
            $"from then on this host reads nothing ahead ({_host.NatureGroundAhead - aheadAfter} read, {_host.NatureGroundHeld} held): every step casts for itself, as before LOOK-3 ({_host.NatureGroundCast - castBefore} cast in {_host.NatureTicks.StepTicks - stepsAfter} steps)");
        await FreeWorld();

        // The nature itself (every durable nature change brings another): a fire taken back on a step's own tick.
        await NewWorld(SavePath("spread-mark-nature"), roomOverride: _spreadRoom);
        _host.NatureSampleForTests = null;
        _host.NatureStepSpread = true;
        _host.NatureMarkBlindForTests = AllParts & ~CommandHost.WorldMarkPart.Nature;
        for (var i = 0; i < 20; i++) await Tick();
        var again2 = Fire(Player, SpreadFireAt, 0.5, 14, out _);
        var ready = false;
        for (var i = 0; i < 200 && !ready; i++)
        {
            await Tick();
            ready = _host.NatureTicksToStep(SpreadTick) == 1 && _host.NatureGroundHeld >= 20;
        }
        var (was, natureMoved, taken) = (_host.NatureGroundDropped, _host.NatureMarkMoved(CommandHost.WorldMarkPart.Nature), _host.NatureGroundUsed);
        var holding = _host.NatureGroundHeld;
        await Frames(1);
        var undone = Send(Command(NextId("mark-undo"), "room.undo", new JsonObject { ["to_revision"] = _host.Nature.UndoTarget(_host.Revision) ?? 0 }, expectedRevision: _host.Revision), Player);
        _host._PhysicsProcess(SpreadTick);
        Check(Ok(again2) && ready && Ok(undone) && _host.NatureGroundDropped - was >= holding && holding >= 20 && _host.NatureGroundUsed == taken && _host.NatureMarkMoved(CommandHost.WorldMarkPart.Nature) > natureMoved,
            $"the mark's Nature alone: a fire taken back on a step's own tick voids the readings held ({_host.NatureGroundDropped - was} dropped of {holding}; none taken: {_host.NatureGroundUsed - taken}); undo {Canonical(undone["error"])}");
        await FreeWorld();
    }

    /// <summary>
    /// The land's collision tiles and the plant copies', on a wide land (IslandFixture.WriteWideLand: 96 by 32 m in 16 m tiles, a
    /// broadleaf far to the east): the land lets its far tiles go a few seconds after the boot, builds one again the moment a body
    /// is put there, and the copies' view collides a tile when a body comes near. Each moves its own part of the mark and voids the
    /// readings held; and through all of it the audit finds every reading the steps took to be what the step's own ray gives.
    /// </summary>
    private async Task TestSpreadLand()
    {
        var grass = IslandFixture.GrassRows(400, new Rect2(-4, -12, 8, 8), seed: 5).Select((r, i) => i == 0 ? r with { Group = 1 } : r).ToList();
        var treeAt = new Vector2(57, -8);
        var tree = IslandFixture.Broadleaf(treeAt.X, treeAt.Y, (float)IslandFixture.Height(treeAt.X, treeAt.Y), 0, 7);
        var land = RoomData.Load(IslandFixture.WriteWideLand("nature_spread_land", -1, 4, -1, 0, new[] { IslandFixture.Grass("grass", grass), IslandFixture.Trees(new[] { tree }) }));
        var room = _room;
        _room = land;
        try
        {
            // The copies' view waits with its far tiles until the test lets it go on (as when a save loads with a fire burning, the view
            // still colliding its tiles tile by tile): the tree's tile collides in the middle of a window.
            await NewWorld(SavePath("spread-land"), roomOverride: land, built: () => Copies.SetProcess(false));
            _host.NatureSampleForTests = null;
            _host.NatureStepSpread = true;
            _host.NatureAheadAudit = true;
            var alarms = new List<string>();
            _host.NatureSpreadAlarmForTests = alarms.Add;
            var terrain = _world!.GetNode("Room").GetNodeOrNull<TerrainCollision>(TerrainCollision.ViewName);
            var copies = Copies;
            async Task Tick()
            {
                await Frames(1);
                _host._PhysicsProcess(SpreadTick);
            }
            async Task<bool> Held()
            {
                for (var i = 0; i < 240; i++)
                {
                    await Tick();
                    if (_host.NatureGroundHeld >= 20 && _host.NatureTicksToStep(SpreadTick) > 6) return true;
                }
                return false;
            }
            Vector3 Ground(float x, float z) => new(x, (float)IslandFixture.Height(x, z) + 0.02f, z);
            var spawn = land.SpawnFor("player").PositionM;
            var lit = Fire(Player, spawn + new Vector3(0.8f, 0.02f, 0), 0.6, 30, out _);
            // The boot's far tiles go: fewer built, none built anew. Only the tiles' part of the mark moves.
            _host.NatureMarkBlindForTests = AllParts & ~(CommandHost.WorldMarkPart.TerrainTiles | CommandHost.WorldMarkPart.TerrainBuilds);
            var (tilesAtBoot, buildsAtBoot) = (terrain?.TilesBuilt ?? 0, terrain?.Builds ?? 0);
            for (var i = 0; i < 400 && terrain != null && terrain.TilesBuilt == tilesAtBoot; i++) await Tick();
            await Tick();
            Check(Ok(lit) && terrain != null && terrain.TilesBuilt < tilesAtBoot && terrain.Builds == buildsAtBoot && _host.NatureMarkMoved(CommandHost.WorldMarkPart.TerrainTiles) >= 1 && _host.NatureMarkMoved(CommandHost.WorldMarkPart.TerrainBuilds) == 0,
                $"the land lets its far tiles go ({tilesAtBoot} built at the boot, {terrain?.TilesBuilt} now, none built anew): the mark's TerrainTiles moves ({_host.NatureMarkMoved(CommandHost.WorldMarkPart.TerrainTiles)}), its TerrainBuilds does not ({_host.NatureMarkMoved(CommandHost.WorldMarkPart.TerrainBuilds)}); lit {Canonical(lit["error"])}");
            async Task Alone(CommandHost.WorldMarkPart part, string what, Action change)
            {
                _host.NatureMarkBlindForTests = AllParts & ~part;
                var held = await Held();
                var (dropped, moved, had) = (_host.NatureGroundDropped, _host.NatureMarkMoved(part), _host.NatureGroundHeld);
                await Frames(1);
                change();
                _host._PhysicsProcess(SpreadTick);
                for (var i = 0; i < 4 && _host.NatureGroundDropped == dropped; i++) await Tick();
                var gone = _host.NatureGroundDropped - dropped;
                Check(held && had >= 20 && gone >= had && _host.NatureMarkMoved(part) > moved,
                    $"the mark's {part} alone: {what} voids the readings held ({gone} dropped of {had} held; counted as its move {_host.NatureMarkMoved(part) - moved} time(s); held {held})");
                _host.NatureMarkBlindForTests = NoPart;
            }
            var builds = terrain?.Builds ?? 0;
            await Alone(CommandHost.WorldMarkPart.TerrainBuilds, "the land's tile built under a body put far away", () => _companion.GlobalPosition = Ground(40, -8));
            var collided = copies.CollisionTilesBuilt;
            await Alone(CommandHost.WorldMarkPart.CopiesTiles, "a tile of the plant copies collided (the far tree's, the view going on with its tiles)", () => copies.SetProcess(true));
            Check(terrain != null && terrain.Builds > builds && copies.CollisionTilesBuilt > collided, $"(the land built {terrain?.Builds - builds} tile(s) for the body, and the copies collided {copies.CollisionTilesBuilt - collided} more)");
            // The fire burns on while the land comes and goes round the bodies: every reading a step took is the step's own ray's.
            _companion.GlobalPosition = Ground(spawn.X + 0.5f, spawn.Z);
            var used = _host.NatureGroundUsed;
            for (var i = 0; i < 300; i++) await Tick();
            Check(_host.NatureGroundUsed > used && _host.NatureAheadMismatches == 0 && _host.NatureSpotCheckFailures == 0 && alarms.Count == 0 && !_host.NatureSpreadDistrusted,
                $"the audit over a land that comes and goes: of {_host.NatureGroundUsed} readings taken ahead none differs from the step's own ray ({_host.NatureAheadMismatches}), and no spot check fails ({_host.NatureSpotCheckFailures}); the mark moved: {_host.NatureMarkMoves}");
            GD.Print($"NATURE_SPREAD land: {_host.NatureGroundAhead} read ahead, {_host.NatureGroundUsed} taken, {_host.NatureGroundCast} cast in steps, {_host.NatureGroundDropped} dropped; the mark moved: {_host.NatureMarkMoves}; audit {_host.NatureAheadMismatches} differ");
            await FreeWorld();
        }
        finally
        {
            _room = room;
        }
    }

    /// <summary>
    /// The plants a step may leave out (CommandHost.PlantsUnderFire) are only the ones measured at their size now with no burning
    /// cell in their squares. A plant not measured yet, and a plant measured at another size, stay in BurnPlants' list:
    /// - a stalled host steps before the measuring is done (one tick measures 48 of 86 plants, then a step every tick): the plants
    ///   never measured burn as they do with the step whole;
    /// - a replanted tree, measured at half size beside a small fire, grows to its natural size on the island clock while the fire
    ///   burns (a step's own StepSeedlings, with no tick to measure it again): at its new size it reaches the flames, and burns.
    /// Each is played with the step whole, spread, and spread with the audit; the islands are the same and the audit counts nothing.
    /// </summary>
    private async Task TestSpreadPlants(int ids, DateTime now)
    {
        var dense = SpreadRoom(dense: true);
        var fieldIds = Enumerable.Range(0, dense.Copies!.Find("grass")!.Count).Select(row => PlantCopies.IdOf("grass", row)).ToList();
        async Task<(string Nature, long Mismatches, int Measured, int Unmeasured, int UnmeasuredBurning, long LeftOut)> Stalled(string name, bool spread, bool audit)
        {
            await NewWorld(SavePath(name), roomOverride: dense);
            _ids = ids;
            _now = now;
            _host.NatureStepSpread = spread;
            _host.NatureAheadAudit = audit;
            var lit = Fire(Player, SpreadFireAt, 0.5, 20, out _);
            // One tick before the first step: a share of the fire's plants is measured. Then the host stalls: a step every tick.
            _host.AdvanceNature(SpreadTick);
            var measured = _host.NaturePlantsMeasured;
            var unmeasured = fieldIds.Where(id => !_host.NaturePlantMeasured(id)).ToList();
            Steps(40);
            var result = (Canonical(NatureBlock.Pack(_host.Nature)), _host.NatureAheadMismatches, measured, unmeasured.Count,
                unmeasured.Count(id => _host.Nature.Plants.TryGetValue(id, out var plant) && plant.Burn > 0), _host.NaturePlantsLeftOut);
            if (!Ok(lit)) result.Item1 = "the fire was not lit: " + Canonical(lit["error"]);
            await FreeWorld();
            return result;
        }
        var whole = await Stalled("spread-plants-whole", spread: false, audit: false);
        var spread = await Stalled("spread-plants-on", spread: true, audit: false);
        var audit = await Stalled("spread-plants-audit", spread: true, audit: true);
        Check(spread.Measured > 0 && spread.Unmeasured > 0 && spread.UnmeasuredBurning > 0 && spread.LeftOut > 0 && whole.Measured == 0 && spread.Nature == whole.Nature && audit.Nature == whole.Nature && audit.Mismatches == 0,
            $"a step before the measuring is done: {spread.Measured} of the fire's plants measured, {spread.Unmeasured} of the field's {fieldIds.Count} not; the unmeasured stay in the list and burn ({spread.UnmeasuredBurning} caught) while the measured with no flame near are left out ({spread.LeftOut}); " +
            $"the same island as with the step whole: {FirstDifference(new List<string> { whole.Nature }, new List<string> { spread.Nature })}{FirstDifference(new List<string> { whole.Nature }, new List<string> { audit.Nature })}; the audit counts {audit.Mismatches}");

        // The replanted tree, measured small, grown before the next step.
        var sownAt = NextUtcHour(now, 8);
        async Task<(string Nature, long Mismatches, float Small, float Grown, float Burn, bool Measured, string Notes)> Seedling(string name, bool spread, bool audit)
        {
            _now = sownAt;
            await NewWorld(SavePath(name), roomOverride: dense);
            _ids = ids;
            _host.NatureStepSpread = spread;
            _host.NatureAheadAudit = audit;
            _host.LearnGrowForTests(2);
            _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f);
            // The tree burns down and is replanted: a seedling at half size, sown at 08:00 on the island clock.
            var first = Fire(Player, TreeAt + new Vector3(-0.1f, 0.01f, 0), 0.3, 30, out _);
            for (var step = 0; step < 80 && _host.Nature.Fire != null; step++) Steps(1);
            _now = sownAt;
            var replant = GrowCast(Player, TreeAt + new Vector3(-0.2f, 0.01f, 0), 0.5);
            var small = Factor(Tree);
            // A small fire beside it: its nearest cell lies in the squares the tree catches from at its natural size, outside the
            // squares it catches from at half size.
            dense.Copies!.TryResolve(Tree, out var set, out var row);
            Aabb Bounds(float factor)
            {
                var place = set.Transform(row);
                return (new Transform3D(place.Basis * Basis.FromScale(Vector3.One * factor), place.Origin) * set.MeshBounds).Grow(PlantLaws.CatchM);
            }
            var nearest = NatureState.Grid(Bounds(small).Position).X - 1;
            var z = NatureState.Grid(new Vector3(0, 0, TreeAt.Z)).Z;
            var cell = new Vector3((nearest + 0.5f) * NatureState.CellM, 0.04f, (z + 0.5f) * NatureState.CellM);
            var reaches = Bounds(1).HasPoint(cell) && !Bounds(small).HasPoint(cell);
            var second = Fire(Player, new Vector3((nearest - 0.5f) * NatureState.CellM, 0.01f, (z + 0.5f) * NatureState.CellM), 0.06, 10, out _);
            // One tick: the tree is measured at half size. Then three daylight hours pass, and the host steps every tick.
            _host.AdvanceNature(SpreadTick);
            var measured = _host.NaturePlantMeasured(Tree);
            _now = sownAt + TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1);
            Steps(1);
            var grown = Factor(Tree);
            var early = _host.Nature.Plants.TryGetValue(Tree, out var before) ? before.Burn : -1;
            Steps(3);
            var burn = _host.Nature.Plants.TryGetValue(Tree, out var after) ? after.Burn : -1;
            var notes = $"first fire {Ok(first)}, replant {(Ok(replant) ? "ok" : Canonical(replant["error"]))}, second fire {(Ok(second) ? "ok" : Canonical(second["error"]))}, the flames' cell in reach only at natural size {reaches}, burn before it grew {early:0.###}";
            var result = (Canonical(NatureBlock.Pack(_host.Nature)), _host.NatureAheadMismatches, small, grown, burn, measured, notes);
            await FreeWorld();
            return result;
        }
        var wholeTree = await Seedling("spread-seedling-whole", spread: false, audit: false);
        var spreadTree = await Seedling("spread-seedling-on", spread: true, audit: false);
        var auditTree = await Seedling("spread-seedling-audit", spread: true, audit: true);
        Check(spreadTree.Measured && Math.Abs(spreadTree.Small - 0.5f) < 1e-4f && spreadTree.Grown == 1 && wholeTree.Burn > 0 && spreadTree.Burn == wholeTree.Burn && spreadTree.Nature == wholeTree.Nature && auditTree.Nature == wholeTree.Nature && auditTree.Mismatches == 0,
            $"a plant measured at another size: the replanted tree, measured at {spreadTree.Small:0.##} of its size, grows to {spreadTree.Grown:0.##} while the fire burns and catches at its new size (burn {spreadTree.Burn:0.###}, {wholeTree.Burn:0.###} with the step whole); " +
            $"the same island: {FirstDifference(new List<string> { wholeTree.Nature }, new List<string> { spreadTree.Nature })}{FirstDifference(new List<string> { wholeTree.Nature }, new List<string> { auditTree.Nature })}; the audit counts {auditTree.Mismatches}; {spreadTree.Notes}");
        _ids = ids + 1000;
        _now = now + TimeSpan.FromDays(2);
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

    private async Task<SpreadRun> SpreadPlay(string name, bool spread, bool audit, int ids, DateTime now, CommandHost.WorldMarkPart blind = NoPart, int? spotChecks = null)
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
            _host.NatureMarkBlindForTests = blind;
            if (spotChecks is { } checks) _host.NatureSpotChecks = checks;
            _host.NatureSpreadAlarmForTests = run.Alarms.Add;
            _host.NatureSaveIntervalS = 2;
            // The player lets creations run their effects (the HUD's switch, each session): the lamp's timer fires.
            _host.Runtime.Call("set_local_consent", true);
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
            run.SpotChecked += _host.NatureGroundSpotChecked;
            run.SpotFailures += _host.NatureSpotCheckFailures;
            run.Moving += _host.NatureMarkMoved(CommandHost.WorldMarkPart.Moving);
            run.Moves += (run.Moves.Length > 0 ? "; then " : "") + _host.NatureMarkMoves;
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
        JsonObject Place(string id, JsonObject source, double x, double z) =>
            Do(id, "creation.place", new JsonObject { ["source"] = source, ["placement"] = new JsonObject { ["position_m"] = new JsonArray(x, 0, z) } });
        JsonObject PutBook(string id, Vector3 at) => Do(id, "entity.release", new JsonObject
        {
            ["actor"] = CommandHost.CompanionAvatarId, ["placement"] = new JsonObject { ["position_m"] = new JsonArray(at.X, at.Y, at.Z) },
        }, Companion);
        JsonObject TakeBook(string id) => Do(id, "entity.grab", new JsonObject { ["target"] = "obj:book", ["actor"] = CommandHost.CompanionAvatarId }, Companion);
        int Running()
        {
            using var value = _host.Runtime.Get("animations");
            using var list = value.AsGodotArray();
            return list.Count;
        }

        await Host();
        _ids = ids;
        _now = now;
        JsonObject? lamp = null, lit = null, built = null, grabbed = null, put = null, taken = null, putAgain = null, early = null, grown = null, removed = null, slab = null, slabGone = null,
            rebuilt = null, tree = null, undone = null;
        // Each command falls where it tells most in a step's window, before that tick's work (as a frame's input is): any tick (the
        // middle of a window: steps fall on every thirtieth tick of a host), a step's own tick, or so many ticks before a step.
        var plan = new List<(int From, Func<int, bool> When, Action Act)>();
        void At(int from, Action act) => plan.Add((from, _ => true, act));
        void OnStep(int from, Action act) => plan.Add((from, toStep => toStep == 1, act));
        void BeforeStep(int from, int ticks, Action act) => plan.Add((from, toStep => toStep == ticks + 1, act));
        At(8, () => lamp = Place("spread-lamp", LampSource("Spread lamp"), 0.3, -0.8));
        At(20, () => _companion.TryTeleportTo(new Vector3(0.75f, 0.006f, 0.6f)));
        At(46, () => lit = Light(SpreadFireAt, 0.5, 12));
        At(105, () => built = Place("spread-build", Source("Spread crate"), 1.4, 1.1));
        At(130, () => _companion.TryTeleportTo(new Vector3(0.45f, 0.006f, 0.3f)));
        At(137, () => grabbed = TakeBook("spread-grab"));
        At(140, () => _companion.TryTeleportTo(SpreadGubbleAt));
        // Put down on burning ground two ticks before a step: the physics takes the book's new place up at its next step, so a
        // reading taken on this tick would still meet the bare floor.
        BeforeStep(141, 2, () => put = PutBook("spread-put", SpreadBookAt));
        // Picked up again on a step's own tick: what was read in the ticks before met the book.
        OnStep(165, () => taken = TakeBook("spread-take"));
        At(197, () => early = Do("spread-grow-early", "nature.cast", Cast("grow", NearGubble, 0.08, 1)));
        At(200, () => putAgain = PutBook("spread-put-again", SpreadBookThenAt));
        At(226, () => removed = Do("spread-remove", "entity.remove", new JsonObject { ["target"] = built?["created"]?[0]?.GetValue<string>() ?? "" }, revision: true));
        At(255, () => slab = Place("spread-dome", DomeSource("Spread dome"), 1.45, 1.0));
        // Removed on a step's own tick: what was read in the ticks before met the dome's top.
        OnStep(275, () => slabGone = Do("spread-dome-remove", "entity.remove", new JsonObject { ["target"] = slab?["created"]?[0]?.GetValue<string>() ?? "" }, revision: true));
        At(445, () => rebuilt = Place("spread-build", Source("Spread box"), 1.1, 1.15));
        At(860, () => grown = Do("spread-grow", "nature.cast", Cast("grow", NearGubble, 0.08, 1)));
        At(900, () => _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f));
        At(905, () => tree = Light(TreeAt + new Vector3(-0.1f, 0.01f, 0), 0.3, 15));
        At(1900, () => undone = Do("spread-undo", "room.undo", new JsonObject { ["to_revision"] = _host.Nature.UndoTarget(_host.Revision) ?? 0 }, revision: true));
        var done = new bool[plan.Count];
        var when = new List<string>();
        while (tick < SpreadTicks)
        {
            await Frames(1);
            tick++;
            var toStep = _host.NatureTicksToStep(SpreadTick);
            for (var i = 0; i < plan.Count; i++)
            {
                if (done[i] || tick < plan[i].From || !plan[i].When(toStep)) continue;
                done[i] = true;
                plan[i].Act();
                if (plan[i].From != tick) when.Add($"{plan[i].From} at {tick}");
            }
            _host._PhysicsProcess(SpreadTick);
            if (Running() > 0) run.LampTicks++;
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
        var results = new (string Label, JsonObject? Result)[]
        {
            ("lamp", lamp), ("fire", lit), ("build", built), ("grab", grabbed), ("put on burning ground", put), ("take from it", taken), ("put again", putAgain), ("grow in the fire (refused)", early),
            ("remove", removed), ("dome", slab), ("dome removed", slabGone), ("build again", rebuilt), ("grow", grown), ("tree fire", tree), ("undo", undone),
        };
        run.Happened = results.All(r => r.Result != null && (r.Label.StartsWith("grow in", StringComparison.Ordinal) ? Code(r.Result) == "target_busy" : Ok(r.Result))) && done.All(d => d);
        run.Notes = string.Join(", ", results.Select(r => r.Label + " " + (r.Result == null ? "not sent" : Ok(r.Result) ? "ok" : Code(r.Result) + " " + r.Result["error"]?["message"]))) +
                    $" (timed by the step: {string.Join(", ", when)})";
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
