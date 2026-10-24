using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// Where an island's frame time and garbage go (S9, the 10x world's worst frames), headless so it can run as often as needed:
/// boots a room ("-- --room=DIR"), waits until its copies are built and its navigation is ready, then measures the frame's
/// process and physics steps (bracketed by two nodes at the extreme priorities), the bytes the main thread allocates a frame, the
/// collections and their pauses, and which types the allocations are (the runtime's allocation ticks). Then it switches each of
/// the world's nodes off in turn (its processing disabled for a while) and says what each one costs: process and physics time and
/// allocations a frame. It prints ISLAND_PROBE lines; it checks nothing.
/// </summary>
public partial class IslandFrameProbe : Node
{
    private RoomWorld _world = null!;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _processStart, _processEnd, _physicsStart, _physicsMs;

    private sealed partial class Bracket : Node
    {
        public Action? OnProcess, OnPhysics;
        public override void _Process(double delta) => OnProcess?.Invoke();
        public override void _PhysicsProcess(double delta) => OnPhysics?.Invoke();
    }

    private sealed class GcListener : EventListener
    {
        public readonly Dictionary<string, long> Types = new();
        public readonly List<(int Generation, double PauseMs)> Collections = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _started;
        private int _generation;
        public bool Recording;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (!Recording || data.Payload == null) return;
            switch (data.EventName)
            {
                case "GCAllocationTick_V4":
                case "GCAllocationTick_V3":
                    var index = data.PayloadNames!.IndexOf("TypeName");
                    var amount = data.PayloadNames.IndexOf("AllocationAmount64");
                    var name = index >= 0 ? data.Payload[index]?.ToString() ?? "?" : "?";
                    var bytes = amount >= 0 ? Convert.ToInt64(data.Payload[amount]) : 100_000;
                    lock (Types) Types[name] = Types.GetValueOrDefault(name) + bytes;
                    break;
                case "GCStart_V2":
                    _started = _clock.Elapsed.TotalMilliseconds;
                    _generation = Convert.ToInt32(data.Payload[data.PayloadNames!.IndexOf("Depth")]);
                    break;
                case "GCEnd_V1":
                    lock (Collections) Collections.Add((_generation, _clock.Elapsed.TotalMilliseconds - _started));
                    break;
            }
        }
    }

    public override async void _Ready()
    {
        try
        {
            var room = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--room=", StringComparison.Ordinal))?["--room=".Length..]
                       ?? throw new ArgumentException("pass -- --room=DIR");
            var frames = int.Parse(OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--frames=", StringComparison.Ordinal))?["--frames=".Length..] ?? "600");
            var first = new Bracket { Name = "First", ProcessPriority = int.MinValue, ProcessPhysicsPriority = int.MinValue };
            var last = new Bracket { Name = "Last", ProcessPriority = int.MaxValue, ProcessPhysicsPriority = int.MaxValue };
            first.OnProcess = () => _processStart = _clock.Elapsed.TotalMilliseconds;
            last.OnProcess = () => _processEnd = _clock.Elapsed.TotalMilliseconds;
            first.OnPhysics = () => _physicsStart = _clock.Elapsed.TotalMilliseconds;
            last.OnPhysics = () => _physicsMs += _clock.Elapsed.TotalMilliseconds - _physicsStart;
            AddChild(first);
            AddChild(last);
            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            _world.RoomDirectory = RoomWorld.ResolveRoom(room.Replace('\\', '/'));
            AddChild(_world);
            for (var waited = Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 120 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frame();
            if (!_world.WorldReady) throw new InvalidOperationException(_world.LoadError);
            var view = _world.Built.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
            var navigation = _world.GetNodeOrNull<GarageGame.Native.Navigation.RoomNavigation>("RoomNavigation");
            for (var waited = Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 120 && (view is { Complete: false } || navigation is { IsReady: false });) await Frame();
            for (var i = 0; i < 120; i++) await Frame();
            if (_world.Room.Copies is { } copies)
            {
                long instanceBands = 0;
                foreach (var set in copies.Sets)
                {
                    var step = Math.Max(1, set.Count / 1024);
                    double sum = 0;
                    var n = 0;
                    for (var row = 0; row < set.Count; row += step, n++) sum += set.Scale(row).Y;
                    var typical = set.MeshHeightM * (float)(sum / Math.Max(1, n));
                    var bands = GarageGame.Native.Look.DrawDistances.CopyBands(set.Kind, typical, set.MeshPaths.Count);
                    instanceBands += (long)set.Count * bands.Length;
                    GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"ISLAND_PROBE set {set.Name} ({set.Kind}, {set.Count} copies, typical height {typical * 100f:0.#} cm, {set.MeshPaths.Count} meshes): {string.Join("; ", bands.Select(b => $"mesh {b.Mesh} {b.BeginM:0.#}-{b.EndM:0.#} m{(b.CastsShadow ? " shadow" : "")}"))}"));
                }
                GD.Print($"ISLAND_PROBE copies: {copies.TotalCount} copies, {instanceBands} instances in all bands ({instanceBands * 64 / 1048576.0:0} MB of instance buffers on the GPU, three times that in the engine's CPU copies)");
            }
            List<DerivedCamera> cameras;
            {
                var space = _world.GetWorld3D().DirectSpaceState;
                var notes = new List<string>();
                cameras = IslandCameras.Derive(_world.Room, IslandCameras.RayGround(space, _world.Room.Bounds), IslandCameras.RaySky(space), notes);
                GD.Print("ISLAND_PROBE island cameras: " + string.Join("; ", cameras.Select(c => $"{c.Id} at {c.PlayerM} facing {c.YawDeg:0}")) + (notes.Count > 0 ? "; notes: " + string.Join("; ", notes) : ""));
            }
            // "--cameras" (S12): the frame where each island camera stands the avatars (spawn, summit, hamlet, beach; S9's cameras),
            // headless: the HUD, the host, the bodies and the look's work as the player would have them there, and the collections.
            if (OS.GetCmdlineUserArgs().Contains("--cameras"))
            {
                foreach (var camera in cameras.Where(c => c.Flight == null))
                {
                    _world.Player.TryTeleportTo(camera.PlayerM);
                    _world.Player.Rotation = new Vector3(0, Mathf.DegToRad(camera.YawDeg), 0);
                    _world.Player.ResetPhysicsInterpolation();
                    _world.Companion.Stay();
                    _world.Companion.TryTeleportTo(camera.CompanionM);
                    _world.Companion.ResetPhysicsInterpolation();
                    for (var i = 0; i < 120; i++) await Frame();
                    Print($"at {camera.Id}", await Measure(frames));
                }
                var settled = GC.GetGCMemoryInfo(GCKind.Any);
                GD.Print($"ISLAND_PROBE collections in all: {GC.CollectionCount(0)} gen0, {GC.CollectionCount(1)} gen1, {GC.CollectionCount(2)} gen2; the last waited on {settled.FinalizationPendingCount} objects for the finalizer");
                GD.Print("ISLAND_PROBE_DONE");
                GetTree().Quit(0);
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--bands-only")) { GetTree().Quit(0); return; }
            if (OS.GetCmdlineUserArgs().Contains("--parts"))
            {
                await MeasureParts();
                GD.Print("ISLAND_PROBE_DONE");
                GetTree().Quit(0);
                return;
            }
            using var listener = new GcListener();
            listener.Recording = true;
            var all = await Measure(frames);
            listener.Recording = false;
            Print("all", all);
            var total = listener.Types.Values.Sum();
            GD.Print("ISLAND_PROBE allocations by type (sampled): " + string.Join("; ", listener.Types.OrderByDescending(t => t.Value).Take(16)
                .Select(t => $"{t.Key} {100.0 * t.Value / Math.Max(1, total):0}%")));
            GD.Print("ISLAND_PROBE collections: " + string.Join(", ", listener.Collections.GroupBy(c => c.Generation).OrderBy(g => g.Key)
                .Select(g => $"gen{g.Key} x{g.Count()} pauses {string.Join("/", g.Select(c => c.PauseMs.ToString("0.0")))} ms")));
            var info = GC.GetGCMemoryInfo(GCKind.Ephemeral);
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"ISLAND_PROBE last ephemeral collection: gen{info.Generation} #{info.Index}, pause {info.PauseDurations[0].TotalMilliseconds:0.0} ms, promoted {info.PromotedBytes / 1048576.0:0.0} MB, heap {info.HeapSizeBytes / 1048576.0:0} MB, " +
                $"gen0 {info.GenerationInfo[0].SizeBeforeBytes / 1048576.0:0.0}->{info.GenerationInfo[0].SizeAfterBytes / 1048576.0:0.0} MB, gen1 {info.GenerationInfo[1].SizeBeforeBytes / 1048576.0:0.0}->{info.GenerationInfo[1].SizeAfterBytes / 1048576.0:0.0} MB, " +
                $"gen2 {info.GenerationInfo[2].SizeAfterBytes / 1048576.0:0} MB, large objects {info.GenerationInfo[3].SizeAfterBytes / 1048576.0:0} MB, pinned {info.PinnedObjectsCount}, finalization pending {info.FinalizationPendingCount}, compacted {info.Compacted}"));
            foreach (var generation in new[] { 0, 1 })
            {
                var timer = Stopwatch.StartNew();
                GC.Collect(generation, GCCollectionMode.Forced, blocking: true);
                GD.Print($"ISLAND_PROBE a forced gen{generation} collection took {timer.Elapsed.TotalMilliseconds:0.0} ms");
            }
            GD.Print($"ISLAND_PROBE GC mode: server {System.Runtime.GCSettings.IsServerGC}, latency {System.Runtime.GCSettings.LatencyMode}, concurrent {AppContext.GetData("System.GC.Concurrent") ?? "default"}");
            // Each node of the world off in turn: what it costs is what the frame saves without it.
            var nodes = _world.GetChildren().Concat(_world.Look.GetChildren()).OfType<Node>()
                .Where(n => n != _world.Built && n.Name != "Environment").ToList();
            // The look's own periodic work: every ClockUpdateSeconds the real clock moves the light, the sky and the grade (a LUT
            // built off the main thread, then taken up). Here the hour steps 20 times, a frame apart, as ten minutes each would.
            var moment = Stopwatch.StartNew();
            double longestCall = 0, longestFrame = 0;
            for (var step = 0; step < 20; step++)
            {
                var call = Stopwatch.StartNew();
                _world.Look.SetClockAsync(9f + step / 6f, 172);
                longestCall = Math.Max(longestCall, call.Elapsed.TotalMilliseconds);
                for (var f = 0; f < 6; f++)
                {
                    var frame = Stopwatch.StartNew();
                    await Frame();
                    longestFrame = Math.Max(longestFrame, frame.Elapsed.TotalMilliseconds);
                }
            }
            GD.Print($"ISLAND_PROBE the clock's update (the light, sky and grade moving on, 20 times): longest call {longestCall:0.00} ms, longest frame after it {longestFrame:0.0} ms, {moment.Elapsed.TotalMilliseconds:0} ms in all, {_world.Look.GradeBuilds} grade LUTs built");
            // Two periodic jobs outside the look, timed on their own: the HUD's focus query (every 0.5 s) and one nature step (every 0.5 s).
            if (_world.FindChild("CommandHost", true, false) is GarageGame.Native.Kernel.CommandHost host)
            {
                double focusMs = 0, natureMs = 0;
                for (var i = 0; i < 10; i++)
                {
                    var timer = Stopwatch.StartNew();
                    host.PlayerFocus();
                    focusMs = Math.Max(focusMs, timer.Elapsed.TotalMilliseconds);
                    timer.Restart();
                    host.AdvanceNature(GarageGame.Native.Kernel.NatureLaws.StepS);
                    natureMs = Math.Max(natureMs, timer.Elapsed.TotalMilliseconds);
                    await Frame();
                }
                GD.Print($"ISLAND_PROBE outside the look: the HUD's focus query (CommandHost.PlayerFocus) takes up to {focusMs:0.0} ms, one nature step (CommandHost.AdvanceNature) up to {natureMs:0.0} ms");
            }
            if (OS.GetCmdlineUserArgs().Contains("--no-toggles")) nodes.Clear();
            foreach (var node in nodes)
            {
                var mode = node.ProcessMode;
                node.ProcessMode = ProcessModeEnum.Disabled;
                for (var i = 0; i < 10; i++) await Frame();
                var without = await Measure(Math.Min(frames, 240));
                node.ProcessMode = mode;
                for (var i = 0; i < 10; i++) await Frame();
                Print($"without {node.Name} ({node.GetType().Name})", without);
            }
            GD.Print("ISLAND_PROBE_DONE");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError("Island probe failed: " + exception);
            GetTree().Quit(1);
        }
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private sealed record Sample(double FrameMs, double ProcessMs, double PhysicsMs, double AllocatedKb, int Collections, double PauseMs);

    private async Task<List<Sample>> Measure(int frames)
    {
        var samples = new List<Sample>(frames);
        var last = _clock.Elapsed.TotalMilliseconds;
        for (var i = 0; i < frames; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var collections = GC.CollectionCount(0);
            var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            _physicsMs = 0;
            await Frame();
            var now = _clock.Elapsed.TotalMilliseconds;
            samples.Add(new Sample(now - last, _processEnd - _processStart, _physicsMs, (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024.0,
                GC.CollectionCount(0) - collections, GC.GetTotalPauseDuration().TotalMilliseconds - pause));
            last = now;
        }
        return samples;
    }

    private static void Print(string label, List<Sample> samples)
    {
        static double P(IEnumerable<double> values, double p) { var v = values.OrderBy(x => x).ToArray(); return v[Math.Clamp((int)Math.Ceiling(p * v.Length) - 1, 0, v.Length - 1)]; }
        var slowest = string.Join(", ", samples.OrderByDescending(s => s.FrameMs).Take(3).Select(s => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{s.FrameMs:0.0} ms (process {s.ProcessMs:0.0}, physics {s.PhysicsMs:0.0}, {s.Collections} collections, {s.PauseMs:0.0} ms paused)")));
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ISLAND_PROBE {label}: frame {samples.Average(s => s.FrameMs):0.00} ms mean, {P(samples.Select(s => s.FrameMs), 0.95):0.00} p95, {samples.Max(s => s.FrameMs):0.0} max; process {samples.Average(s => s.ProcessMs):0.000} ms mean, {samples.Max(s => s.ProcessMs):0.0} max; physics {samples.Average(s => s.PhysicsMs):0.000} ms mean, {samples.Max(s => s.PhysicsMs):0.0} max; main thread allocates {samples.Average(s => s.AllocatedKb):0.0} KB a frame; {samples.Sum(s => s.Collections)} collections, {samples.Sum(s => s.PauseMs):0.0} ms paused over {samples.Count} frames, the longest frame's pause {samples.Max(s => s.PauseMs):0.0} ms; slowest: {slowest}"));
    }
}
