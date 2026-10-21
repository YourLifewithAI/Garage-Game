using Godot;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Navigation;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Scale;

/// <summary>
/// The scale suite (packet S10 of the 10x world): the 10x garage booted as the game boots a room and measured, one line per
/// measurement (SCALE_*, key=value) so sessions can compare their numbers:
/// <list type="bullet">
/// <item>SCALE_LOAD: the room read off the main thread and built, to ROOM_WORLD_READY, and the plant copies built after it;</item>
/// <item>SCALE_NAVIGATION: the navigation mesh baked in the background, ready how long after the room;</item>
/// <item>SCALE_MEMORY: the process's working set and its peak, Godot's static memory and the managed heap, after loading and
/// again at the end (headless renders nothing, so there is no video memory to read);</item>
/// <item>SCALE_WALK: the player runs across the island to the farthest land a route reaches (<see cref="ScaleWalkTest"/>);</item>
/// <item>SCALE_COME: the Gubble, left where the walk began, called from there as the player calls it (out of sight beyond
/// 25 m it catches up), and SCALE_COME_TRAVELLED, the same come travelled all the way.</item>
/// </list>
/// Run by tools/test-room.ps1 -Scale, never by default: the runner builds the 10x fixture once into the shared fixture cache
/// and passes "-- --landscape=DIR". The checks only ask that each step happens (it loads, the mesh bakes, the walk and the comes
/// arrive); the numbers are for comparing with each other and with RUN-2-TEN-TIMES.md's budgets, not gates. Headless, a tick is
/// the simulation's cost, not the frame time a player sees (the look harness measures that).
/// </summary>
public partial class ScaleSuiteTest : Node3D
{
    private const string TestRoot = "user://tests/scale_suite";
    private const string Player = CommandHost.PlayerPrincipal;
    private int _checks;
    private int _failures;
    private int _ids;
    private RoomWorld _world = null!;
    private CommandHost _host = null!;
    private RoomNavigation _navigation = null!;

    public override async void _Ready()
    {
        try
        {
            var directory = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--landscape=", StringComparison.Ordinal))?["--landscape=".Length..];
            if (string.IsNullOrEmpty(directory))
            {
                GD.PushError("Scale suite: pass -- --landscape=DIR, an exported island (tools/test-room.ps1 -Scale builds the 10x garage)");
                GetTree().Quit(1);
                return;
            }
            CommandHost.SaveRoot = TestRoot + "/saves";
            RemoveSaves();
            await Load(directory.Replace('\\', '/'));
            if (!_world.WorldReady || !_navigation.IsReady) { Finish(); return; }
            await Frames(30);

            // The Gubble stays where the walk begins, so the come afterwards is from the far end of the island.
            var companion = _world.Companion;
            var start = _world.Player.GlobalPosition;
            Check(Ok(Send(Command("stay", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }))), "the Gubble is told to stay");
            await Frames(10);
            var gubbleStart = companion.GlobalPosition;
            var walk = await new ScaleWalkTest(this, _world, _navigation).Run();
            Measure("SCALE_WALK " + walk.Line());
            Check(walk.Route.Reaches && walk.Arrived, $"the player runs {walk.Route.LengthM:0} m across the island along a route and arrives ({walk.EndGapM:0.00} m off the goal)");

            await Come("SCALE_COME", catchUp: true);
            Check(companion.TryTeleportTo(gubbleStart + Vector3.Up * 0.01f), "the Gubble put back where the walk began");
            Send(Command("stop", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stop" }));
            await Frames(10);
            await Come("SCALE_COME_TRAVELLED", catchUp: false);
            Measure("SCALE_MEMORY_END " + MemoryLine());
            Finish();
        }
        catch (Exception error)
        {
            GD.PushError("Scale suite exception: " + error);
            GetTree().Quit(1);
        }
    }

    /// <summary>Boot the room as the game does (RoomWorld, its load on a worker thread), then wait for its copies and its navigation.</summary>
    private async Task Load(string directory)
    {
        var clock = Stopwatch.StartNew();
        _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        _world.RoomDirectory = directory;
        AddChild(_world);
        while (!_world.WorldReady && _world.LoadError.Length == 0 && clock.Elapsed.TotalSeconds < 300) await Frames(1);
        var readyS = clock.Elapsed.TotalSeconds;
        Check(_world.WorldReady, $"the island boots ({readyS:0.0} s): {_world.LoadError}");
        if (!_world.WorldReady) return;
        _world.Player.ReadKeyboard = false;
        _host = CommandHost.Of(_world)!;
        _navigation = _world.GetNode<RoomNavigation>("RoomNavigation");
        var copies = _world.Built.GetChildren().OfType<PlantCopiesView>().FirstOrDefault();
        double? navigationS = _navigation.IsReady ? readyS : null, copiesS = copies == null || copies.Complete ? readyS : null;
        while ((navigationS == null || copiesS == null) && clock.Elapsed.TotalSeconds < readyS + 300)
        {
            await Frames(1);
            if (navigationS == null && _navigation.IsReady) navigationS = clock.Elapsed.TotalSeconds;
            if (copiesS == null && copies!.Complete) copiesS = clock.Elapsed.TotalSeconds;
        }
        var room = _world.Room;
        Measure(Invariant($"SCALE_LOAD room={room.RoomId} ready_s={readyS:0.0} copies_built_s={copiesS ?? -1:0.0} copies={room.Copies?.TotalCount ?? 0} ") +
                Invariant($"copy_tiles_drawn={copies?.VisualTiles ?? 0} copy_tiles_collided={copies?.CollisionTiles ?? 0} copy_build_frames={copies?.BuildFrames ?? 0} copy_build_ms={copies?.BuildMs ?? 0:0} copy_longest_frame_ms={copies?.LongestFrameMs ?? 0:0.0} ") +
                Invariant($"objects={room.Objects.Count} shell={room.Shell.Count} bounds_m={room.Bounds.Size.X:0.0}x{room.Bounds.Size.Z:0.0}x{room.Bounds.Size.Y:0.0}"));
        Check(copiesS != null, "the island's plant copies are all built");
        Measure(Invariant($"SCALE_NAVIGATION ready_s={navigationS ?? -1:0.0} after_room_s={(navigationS ?? readyS) - readyS:0.0} polygons={_navigation.PolygonCount} tiles={_navigation.TileCount} islands={_navigation.IslandCount} ") +
                Invariant($"bake_ms={_navigation.LastBakeMs:0} bake_main_thread_ms={_navigation.LastBakeMainThreadMs:0.0}"));
        Check(navigationS != null, "the island's navigation mesh is baked");
        Measure("SCALE_MEMORY " + MemoryLine());
    }

    private static string MemoryLine()
    {
        using var process = Process.GetCurrentProcess();
        const double gb = 1024.0 * 1024 * 1024;
        return Invariant($"working_set_gb={process.WorkingSet64 / gb:0.00} peak_working_set_gb={process.PeakWorkingSet64 / gb:0.00} private_gb={process.PrivateMemorySize64 / gb:0.00} ") +
               Invariant($"godot_static_gb={OS.GetStaticMemoryUsage() / gb:0.00} godot_static_peak_gb={OS.GetStaticMemoryPeakUsage() / gb:0.00} managed_gb={GC.GetTotalMemory(false) / gb:0.00} ") +
               Invariant($"video_gb={Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / gb:0.00}");
    }

    /// <summary>
    /// The player calls the Gubble (goal.set come through the command host, as the key does) and the come is watched through:
    /// how long, how it got there (walked a route, floated, caught up), where it ended. catchUp false travels all the way.
    /// </summary>
    private async Task Come(string label, bool catchUp)
    {
        var companion = _world.Companion;
        var player = _world.Player;
        companion.CatchUpBeyondM = catchUp ? CompanionAvatar.CatchUpM : float.PositiveInfinity;
        var fromM = PlanarDistance(companion.GlobalPosition, player.GlobalPosition);
        var catchUps = companion.CatchUps;
        var floats = companion.FloatStarts;
        var come = Send(Command("come", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "come" }));
        Check(Ok(come), $"{label}: the player calls the Gubble: {Code(come)}");
        var serial = companion.IntentSerial;
        int ticks = 0, routed = 0, floating = 0;
        var highest = float.NegativeInfinity;
        double worstTick = 0;
        var watch = new Stopwatch();
        for (; ticks < 400 * 60 && _host.RunningGoal(CommandHost.CompanionAvatarId) != null; ticks++)
        {
            watch.Restart();
            await Frames(1);
            worstTick = Math.Max(worstTick, watch.Elapsed.TotalMilliseconds);
            highest = Mathf.Max(highest, companion.GlobalPosition.Y - player.GlobalPosition.Y);
            if (companion.FollowingRoute) routed++;
            if (companion.FloatingThere) floating++;
        }
        var arrived = companion.ComeArrivedSerial == serial;
        var gap = PlanarDistance(companion.GlobalPosition, player.GlobalPosition);
        var level = companion.GlobalPosition.Y - player.GlobalPosition.Y;
        Measure(Invariant($"{label} from_m={fromM:0.0} arrived={(arrived ? "yes" : "no")} sim_s={ticks / 60.0:0.0} caught_up={companion.CatchUps - catchUps} walked_s={routed / 60.0:0.0} floated_s={floating / 60.0:0.0} ") +
                Invariant($"float_starts={companion.FloatStarts - floats} highest_over_player_m={highest:0.00} end_gap_m={gap:0.00} end_level_cm={level * 100:0.0} worst_tick_ms={worstTick:0.0}"));
        Check(arrived && gap <= CompanionAvatar.ComeArrivalM + 0.03f && Mathf.Abs(level) <= CompanionAvatar.LevelGapM,
            $"{label}: the Gubble comes {fromM:0} m and arrives beside the player, level with them ({gap:0.00} m away, {level * 100:0.0} cm off their height)");
        companion.CatchUpBeyondM = CompanionAvatar.CatchUpM;
    }

    internal Task Frames(int count) => FramesOf(this, count);

    internal static async Task FramesOf(Node node, int count)
    {
        for (var i = 0; i < count; i++) await node.ToSignal(node.GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private JsonObject Command(string prefix, string op, JsonObject args) =>
        new() { ["schema"] = "enfractal.command", ["version"] = 1, ["action_id"] = $"{prefix}-{++_ids}", ["room_id"] = _world.Room.RoomId, ["op"] = op, ["args"] = args };

    private JsonObject Send(JsonObject message) => JsonNode.Parse(_host.Handle(message.ToJsonString(), Player))!.AsObject();

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;
    private static string? Code(JsonObject result) => result["error"]?["code"]?.GetValue<string>();
    internal static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
    internal static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    internal void Measure(string line) => GD.Print(line);

    internal void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Scale suite: " + label);
    }

    private static void RemoveSaves()
    {
        var directory = ProjectSettings.GlobalizePath(TestRoot);
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
    }

    private void Finish()
    {
        GD.Print($"NATIVE_SCALE_SUITE: {_checks - _failures}/{_checks} checks passed; the island loaded and baked, the player ran across it, the Gubble came from the far end");
        RemoveSaves();
        CommandHost.SaveRoot = CommandHost.DefaultSaveRoot;
        // Wrappers pending at exit can abort Godot's shutdown (the other suites do the same).
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
