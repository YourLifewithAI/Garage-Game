using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// LOOK-3's headless measure of the nature work a physics tick (CommandHost.NatureTicks), on a room booted as the game boots it
/// ("-- --room=DIR", a stand-in landscape; saves go to a test folder). It lights the widest fire the island allows in the grassiest
/// spot near the spawn and lets it burn out, on a fresh island each time: with the step whole in its tick (NatureStepSpread off, as
/// before LOOK-3) and with its reading spread over the ticks before it.
///
/// The world does not stand still while it burns, the same way in every fire: a lamp with a rotor runs on a timer beside the fire
/// (an effect always running); a copper crate is built on the burning ground fifteen seconds in and removed at forty (the ground
/// read under it changes both times, between two steps); and the Gubble is put down far across the land and back every five
/// seconds, so the land builds and lets go its collision tiles round it.
///
/// It prints NATURE_STEP lines: the work a tick, the longest tick of each five seconds with the saves in it, which parts of the
/// world's mark moved. It fails unless both fires left the same island (the nature block, byte for byte), the spread fire's steps
/// took readings from the ticks before, readings were dropped for the world's changes, an effect ran, and no spot check found a
/// reading that no longer held; with "-- --audit" (a third fire, every reading taken ahead cast again and compared, every plant
/// left out watched) also unless none differs.
/// </summary>
public partial class NatureStepProbe : Node3D
{
    private const string TestRoot = "user://tests/nature_step_probe";
    private RoomWorld _world = null!;
    private string _directory = "";

    public override async void _Ready()
    {
        try
        {
            _directory = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--room=", StringComparison.Ordinal))?["--room=".Length..]
                         ?? throw new ArgumentException("pass -- --room=DIR");
            CommandHost.SaveRoot = TestRoot + "/saves";
            var whole = await Burn("whole", spread: false, audit: false);
            var spread = await Burn("spread", spread: true, audit: false);
            var same = whole.Nature == spread.Nature;
            GD.Print($"NATURE_STEP the same island after both fires (the nature block, {whole.Nature.Length} bytes): {(same ? "yes" : "NO: " + Differs(whole.Nature, spread.Nature))}");
            var failed = !same | !Sound("spread", spread);
            if (OS.GetCmdlineUserArgs().Contains("--audit"))
            {
                var audit = await Burn("audit", spread: true, audit: true);
                GD.Print($"NATURE_STEP audit: {audit.Mismatches} readings or plants differ; the same island: {(audit.Nature == whole.Nature ? "yes" : "NO")}");
                failed |= audit.Mismatches != 0 || audit.Nature != whole.Nature || !Sound("audit", audit);
            }
            Remove();
            GD.Print(failed ? "NATURE_STEP_FAILED" : "NATURE_STEP_DONE");
            GetTree().Quit(failed ? 1 : 0);
        }
        catch (Exception error)
        {
            GD.PushError("Nature step probe failed: " + error);
            GetTree().Quit(1);
        }
    }

    private sealed record Burnt(string Nature, long Mismatches, long Taken, long Dropped, long SpotFailures, bool Distrusted, int Effects);

    /// <summary>A fire with the reading spread proves something only if readings were taken, the world moved under them and an effect ran.</summary>
    private static bool Sound(string label, Burnt burnt)
    {
        var wrong = new List<string>();
        if (burnt.Taken == 0) wrong.Add("its steps took no reading from the ticks before");
        if (burnt.Dropped == 0) wrong.Add("no reading was dropped, so the world never changed under one");
        if (burnt.Effects == 0) wrong.Add("no effect ran");
        if (burnt.SpotFailures != 0 || burnt.Distrusted) wrong.Add($"{burnt.SpotFailures} spot check(s) found a reading that no longer held");
        if (wrong.Count > 0) GD.Print($"NATURE_STEP {label}: NO: {string.Join("; ", wrong)}");
        return wrong.Count == 0;
    }

    private static JsonObject Part(string id, string shape, double y, double width, double height) => new()
    {
        ["id"] = id, ["shape"] = shape, ["position_m"] = new JsonArray(0, y, 0), ["rotation_deg"] = new JsonArray(0, 0, 0),
        ["size_m"] = new JsonArray(width, height, width), ["material"] = "copper",
    };

    private static JsonObject Logic(string id, string op, string part, JsonObject parameters) =>
        new() { ["id"] = id, ["op"] = op, ["part_id"] = part, ["params"] = parameters };

    /// <summary>A copper lamp that lights itself and turns its rotor on a timer, twice a second: an effect that is always running.</summary>
    private static JsonObject Lamp() => new()
    {
        ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = "Probe lamp", ["seed"] = 1, ["mount"] = "ground",
        ["parts"] = new JsonArray(Part("base", "box", 0.06, 0.12, 0.12), Part("top", "rotor", 0.18, 0.2, 0.1)),
        ["nodes"] = new JsonArray(
            Logic("tick", "timer", "base", new JsonObject { ["interval_s"] = 0.5 }),
            Logic("glow", "light", "base", new JsonObject { ["intensity"] = 1, ["duration_s"] = 2 }),
            Logic("turn", "spin", "top", new JsonObject { ["speed_rpm"] = 45, ["duration_s"] = 2 })),
        ["edges"] = new JsonArray(new JsonObject { ["from"] = "tick", ["to"] = "glow" }, new JsonObject { ["from"] = "tick", ["to"] = "turn" }),
    };

    /// <summary>A copper crate, 12 cm: it does not burn, and the ground read under it stands 12 cm higher.</summary>
    private static JsonObject Crate() => new()
    {
        ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = "Probe crate", ["seed"] = 1, ["mount"] = "ground",
        ["parts"] = new JsonArray(Part("base", "box", 0.06, 0.12, 0.12)),
        ["nodes"] = new JsonArray(Logic("use", "interact", "base", new JsonObject())),
        ["edges"] = new JsonArray(),
    };

    /// <summary>A command from the player, as the game's own controls send theirs.</summary>
    private JsonObject Ask(CommandHost host, string id, string op, JsonObject args, bool revision = false)
    {
        var command = new JsonObject { ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = id, ["room_id"] = _world.Room.RoomId, ["op"] = op, ["args"] = args };
        if (revision) command["expected_revision"] = host.Revision;
        return host.HandleObject(command.ToJsonString(), CommandHost.PlayerPrincipal);
    }

    /// <summary>Builds a creation at the first of the spots that takes it (the ground's height read by a ray); the words say how it went.</summary>
    private (string Id, string Words) Build(CommandHost host, string id, JsonObject source, IEnumerable<Vector3> spots)
    {
        var space = GetWorld3D().DirectSpaceState;
        var tries = 0;
        var why = "no ground at any spot";
        foreach (var spot in spots)
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(spot + Vector3.Up * 2f, spot + Vector3.Down * 2f));
            if (hit.Count == 0) continue;
            var at = hit["position"].AsVector3();
            var result = Ask(host, $"{id}-{++tries}", "creation.place", new JsonObject
            {
                ["source"] = source.DeepClone(), ["placement"] = new JsonObject { ["position_m"] = KernelJson.Vector(at) },
            });
            if (result["ok"]?.GetValue<bool>() == true)
                return (result["created"]?[0]?.GetValue<string>() ?? "", FormattableString.Invariant($"built at ({at.X:0.0}, {at.Z:0.0})"));
            why = result["error"]?["message"]?.GetValue<string>() ?? "refused";
        }
        return ("", "not built (" + why + ")");
    }

    private static IEnumerable<Vector3> Ring(Vector3 centre, float radius, int count, float turn = 0f) =>
        Enumerable.Range(0, count).Select(i => centre + new Vector3(Mathf.Cos(turn + Mathf.Tau * i / count), 0, Mathf.Sin(turn + Mathf.Tau * i / count)) * radius);

    private static int Running(CommandHost host)
    {
        using var value = host.Runtime.Get("animations");
        using var list = value.AsGodotArray();
        return list.Count;
    }

    private async Task<Burnt> Burn(string label, bool spread, bool audit)
    {
        Remove();
        _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        _world.RoomDirectory = RoomWorld.ResolveRoom(_directory.Replace('\\', '/'));
        AddChild(_world);
        for (var booting = Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 180 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frames(1);
        if (!_world.WorldReady) throw new InvalidOperationException(_world.LoadError);
        _world.Player.ReadKeyboard = false;
        GarageGame.Native.Look.Intro.RoomIntro.Of(_world)?.Skip();
        var view = _world.Built.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
        for (var waited = Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 120 && view is { Complete: false };) await Frames(1);
        await Frames(60);
        var host = CommandHost.Of(_world)!;
        host.Clock = () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        host.MachineZone = TimeZoneInfo.Utc;
        host.NatureStepSpread = spread;
        host.NatureAheadAudit = audit;
        // The same light on every cell whatever the hour this runs at (the look's light follows the machine's clock).
        host.LookHook = null;
        host.LearnFireForTests();
        var spawn = _world.Room.SpawnFor("player").PositionM;
        // The grassiest spot near the spawn: of every fortieth burnable copy within 10 m, the one with the most within 1.5 m.
        var copies = _world.Room.Copies;
        var best = spawn;
        var most = -1;
        if (copies != null)
            foreach (var (set, row) in copies.RowsNear(spawn.X, spawn.Z, 10f).Where(p => p.Item1.FuelClass != "none").Take(4000).Where((_, i) => i % 40 == 0))
            {
                var at = set.Position(row);
                var count = copies.RowsNear(at.X, at.Z, 1.5f).Count(p => p.Item1.FuelClass != "none");
                if (count > most && _world.Player.TryTeleportTo(at + Vector3.Up * 0.02f)) { best = at; most = count; }
            }
        _world.Player.TryTeleportTo(best + Vector3.Up * 0.02f);
        _world.Companion.Stay();
        _world.Companion.TryTeleportTo(best + new Vector3(0.3f, 0.02f, 0.2f));
        await Frames(60);
        var centre = _world.Player.GlobalPosition + new Vector3(0.6f, 0, 0);
        // The world while the fire burns. A lamp on a timer three metres from the fire's middle (the player lets creations run their
        // effects), and the Gubble's rounds: the two burnable copies farthest from the fire, of every two hundredth.
        host.Runtime.Call("set_local_consent", true);
        var lamp = Build(host, "probe-lamp", Lamp(), Ring(centre, 3f, 16).Concat(Ring(centre, 3.6f, 16)));
        var home = _world.Companion.GlobalPosition;
        var rounds = copies == null
            ? new List<Vector3>()
            : copies.Sets.Where(set => set.FuelClass != "none").SelectMany(set => Enumerable.Range(0, set.Count).Where(row => row % 200 == 0).Select(set.Position))
                .OrderByDescending(at => new Vector2(at.X - centre.X, at.Z - centre.Z).Length()).Take(2).ToList();
        await Frames(2);
        // The fire is lit on the tick after a step, under the same weather each time: the boot takes as many ticks as it takes, and
        // what the fire does must not depend on them.
        for (var stepped = host.NatureTicks.StepTicks; host.NatureTicks.StepTicks == stepped;) await Frames(1);
        host.Nature.WeatherS = 30;
        var preview = host.PlayerNaturePreview("fire", centre, 1.5f, 60);
        var lit = preview["ok"]?.GetValue<bool>() == true ? host.PlayerNatureCast("fire", centre, 1.5f, 60, preview["data"]?["preview_id"]?.GetValue<string>()) : preview;
        if (lit["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("the fire was not lit: " + lit.ToJsonString());
        host.NatureTicks.Reset();
        var delta = GetPhysicsProcessDeltaTime();
        var burning = 0;
        var cells = 0;
        var frames = 0;
        var out_ = 0;
        var effects = 0;
        var (turn, moves, farthest) = (0, 0, 0f);
        var crate = (Id: "", Words: "not built (the fire ended first)");
        var removed = "not removed";
        // 130 steps for every fire (65 s: the fire's 60 s and its embers), so each island is read after the same steps.
        for (; frames < 60 * 80 && host.NatureTicks.StepTicks < 130; frames++)
        {
            await Frames(1);
            if (host.Nature.Fire != null) out_ = frames + 1;
            if (Running(host) > 0) effects++;
            // Every fifth step, in the middle of the half second before the next (what was read ahead for it is half taken), the world changes.
            if (host.NatureTicks.StepTicks / 5 > turn && host.NatureTicksToStep(delta) == 15)
            {
                turn = host.NatureTicks.StepTicks / 5;
                if (turn == 6) crate = Build(host, "probe-crate", Crate(), Ring(centre, 0.5f, 8, 0.4f).Concat(Ring(centre, 0.9f, 8, 0.4f)));
                else if (turn == 16 && crate.Id.Length > 0)
                    removed = Ask(host, "probe-remove", "entity.remove", new JsonObject { ["target"] = crate.Id }, revision: true)["ok"]?.GetValue<bool>() == true ? "removed at 40 s" : "not removed";
                else if (turn % 2 == 1 && rounds.Count > 0)
                {
                    var to = turn % 4 == 1 ? rounds[turn / 4 % rounds.Count] + Vector3.Up * 0.02f : home;
                    TerrainCollision.EnsureAround(_world, to, 4f);
                    if (_world.Companion.TryTeleportTo(to))
                    {
                        moves++;
                        farthest = Math.Max(farthest, new Vector2(to.X - centre.X, to.Z - centre.Z).Length());
                    }
                }
            }
            if (frames % 30 != 0) continue;
            burning = Math.Max(burning, host.Nature.Cells.Values.Count(c => c.BurningS > 0));
            cells = Math.Max(cells, host.Nature.Cells.Count);
        }
        GD.Print(FormattableString.Invariant(
            $"NATURE_STEP {label}: a fire of 1.5 m for 60 s at ({best.X:0.0}, {best.Z:0.0}), {most} burnable copies in its reach, {_world.Room.Objects.Count} objects in the room; out after {out_ / 60.0:0.0} s; at most {burning} cells burning, {cells} cells, {host.Nature.Plants.Values.Count(p => p.Burnt)} plants burnt down"));
        GD.Print($"NATURE_STEP {label}: {host.NatureTicks.Summary()}");
        GD.Print($"NATURE_STEP {label}: {host.NatureTicks.WindowSummary()}");
        GD.Print(FormattableString.Invariant(
            $"NATURE_STEP {label}: the world meanwhile: a lamp {lamp.Words}, an effect running on {effects} of {frames} ticks; a crate {crate.Words} in the fire at 15 s, {removed}; the Gubble put down {moves} times, up to {farthest:0.0} m from the fire; the mark moved: {host.NatureMarkMoves}"));
        GD.Print($"NATURE_STEP {label}: ground read ahead {host.NatureGroundAhead}, taken by a step {host.NatureGroundUsed}, cast by a step {host.NatureGroundCast}, dropped {host.NatureGroundDropped}, spot-checked {host.NatureGroundSpotChecked} ({host.NatureSpotCheckFailures} no longer held); plants asked {host.NaturePlantsAsked}, left out {host.NaturePlantsLeftOut}");
        var burnt = new Burnt(CanonicalJson.Text(NatureBlock.Pack(host.Nature)), host.NatureAheadMismatches, host.NatureGroundUsed, host.NatureGroundDropped,
            host.NatureSpotCheckFailures, host.NatureSpreadDistrusted, effects);
        _world.QueueFree();
        await Frames(10);
        return burnt;
    }

    /// <summary>Where two texts first differ, with a little of each from there.</summary>
    private static string Differs(string a, string b)
    {
        var at = 0;
        while (at < a.Length && at < b.Length && a[at] == b[at]) at++;
        var from = Math.Max(0, at - 60);
        return $"at {at}: \"{a[from..Math.Min(a.Length, at + 60)]}\" against \"{b[from..Math.Min(b.Length, at + 60)]}\"";
    }

    private static void Remove()
    {
        var path = ProjectSettings.GlobalizePath(TestRoot);
        if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }
}
