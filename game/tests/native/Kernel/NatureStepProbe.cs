using Godot;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// LOOK-3's headless measure of the nature work a physics tick (CommandHost.NatureTicks), on a room booted as the game boots it
/// ("-- --room=DIR", a stand-in landscape; saves go to a test folder). It lights the widest fire the island allows in the grassiest
/// spot near the spawn and lets it burn out, on a fresh island each time: with the step whole in its tick (NatureStepSpread off, as
/// before LOOK-3) and with its reading spread over the ticks before it. It prints NATURE_STEP lines; it checks only that both fires
/// left the same island (the nature block, byte for byte) and, with "-- --audit" (a third fire, every reading taken ahead cast
/// again and compared, every plant left out watched), that none differs.
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
            var failed = !same;
            if (OS.GetCmdlineUserArgs().Contains("--audit"))
            {
                var audit = await Burn("audit", spread: true, audit: true);
                GD.Print($"NATURE_STEP audit: {audit.Mismatches} readings or plants differ; the same island: {(audit.Nature == whole.Nature ? "yes" : "NO")}");
                failed |= audit.Mismatches != 0 || audit.Nature != whole.Nature;
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

    private sealed record Burnt(string Nature, long Mismatches);

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
        // The fire is lit on the tick after a step, under the same weather each time: the boot takes as many ticks as it takes, and
        // what the fire does must not depend on them.
        for (var stepped = host.NatureTicks.StepTicks; host.NatureTicks.StepTicks == stepped;) await Frames(1);
        host.Nature.WeatherS = 30;
        var preview = host.PlayerNaturePreview("fire", centre, 1.5f, 60);
        var lit = preview["ok"]?.GetValue<bool>() == true ? host.PlayerNatureCast("fire", centre, 1.5f, 60, preview["data"]?["preview_id"]?.GetValue<string>()) : preview;
        if (lit["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("the fire was not lit: " + lit.ToJsonString());
        host.NatureTicks.Reset();
        var burning = 0;
        var cells = 0;
        var frames = 0;
        var out_ = 0;
        // 130 steps for every fire (65 s: the fire's 60 s and its embers), so each island is read after the same steps.
        for (; frames < 60 * 80 && host.NatureTicks.StepTicks < 130; frames++)
        {
            await Frames(1);
            if (host.Nature.Fire != null) out_ = frames + 1;
            if (frames % 30 != 0) continue;
            burning = Math.Max(burning, host.Nature.Cells.Values.Count(c => c.BurningS > 0));
            cells = Math.Max(cells, host.Nature.Cells.Count);
        }
        GD.Print(FormattableString.Invariant(
            $"NATURE_STEP {label}: a fire of 1.5 m for 60 s at ({best.X:0.0}, {best.Z:0.0}), {most} burnable copies in its reach, {_world.Room.Objects.Count} objects in the room; out after {out_ / 60.0:0.0} s; at most {burning} cells burning, {cells} cells, {host.Nature.Plants.Values.Count(p => p.Burnt)} plants burnt down"));
        GD.Print($"NATURE_STEP {label}: {host.NatureTicks.Summary()}");
        GD.Print($"NATURE_STEP {label}: ground read ahead {host.NatureGroundAhead}, taken by a step {host.NatureGroundUsed}, cast by a step {host.NatureGroundCast}, dropped {host.NatureGroundDropped}; plants asked {host.NaturePlantsAsked}, left out {host.NaturePlantsLeftOut}");
        var nature = CanonicalJson.Text(NatureBlock.Pack(host.Nature));
        var mismatches = host.NatureAheadMismatches;
        _world.QueueFree();
        await Frames(10);
        return new Burnt(nature, mismatches);
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
