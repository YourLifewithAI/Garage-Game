using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using GarageGame.Native;
using GarageGame.Native.Look.Intro;

namespace GarageGame.Tests.Look;

/// <summary>
/// Where the game's slow frames go (Run 2, the first recorded play's hitches), in a real window as the game boots a room: every frame
/// from the moment the room starts to build is timed (wall clock between frames), and each frame that took more than --over= ms says
/// what it spent it on: the process and physics steps, the renderer's CPU and GPU time, collections and their pauses, pipelines
/// compiled, and what the world was doing (loading, ready, the intro, the fish surveyed). It also prints the longest frame of every
/// five seconds from the room's ready, as the playtest record's frame events do, so a run can be set against a record.
/// <code>
/// godot --path game res://tests/native_look_frames.tscn -- --room=ID_OR_DIR --seconds=60 [--over=30] [--out=DIR] [--keys=T@40,T@41]
/// </code>
/// It checks nothing; the numbers are for the report. Never headless (headless draws nothing and plays no intro).
/// </summary>
public partial class FrameProbe : Node
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last = -1;
    private double _readyAt = double.NaN;
    private readonly List<Frame> _frames = new();
    private RoomWorld? _world;
    private Rid _viewport;
    private int _gc0, _gc1, _gc2;
    private double _pause;
    private double _pipelines;

    private readonly record struct Frame(double At, double Ms, double ProcessMs, double PhysicsMs, double RenderCpuMs, double GpuMs, string Gc, double PauseMs, double Pipelines, string State);

    private sealed partial class First : Node
    {
        public Action? Tick;
        public override void _Process(double delta) => Tick?.Invoke();
    }

    private static string Arg(string name, string fallback) =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..] ?? fallback;

    public override void _Ready()
    {
        _viewport = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        var first = new First { Name = "FirstInFrame", ProcessPriority = int.MinValue, ProcessMode = ProcessModeEnum.Always, Tick = Sample };
        AddChild(first);
        var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        world.RoomDirectory = RoomWorld.ResolveRoom(Arg("room", "res://rooms/test_room").Replace('\\', '/'));
        _world = world;
        GD.Print($"FRAME_PROBE start: room {world.RoomDirectory}, {Arg("seconds", "60")} s, report frames over {Arg("over", "30")} ms");
        AddChild(world);
    }

    private string StateNow()
    {
        if (_world == null) return "boot";
        if (!_world.WorldReady) return "loading";
        var parts = new List<string> { "ready" };
        if (RoomIntro.Of(_world) is { } intro) parts.Add(intro.Playing ? $"intro {intro.Seconds:0.0}s" : "intro starting");
        if (_world.GetChildren().OfType<GarageGame.Native.Look.Fauna.PondLife>().FirstOrDefault() is { } life && !life.Surveyed) parts.Add("fish not yet surveyed");
        return string.Join(", ", parts);
    }

    private int _stageFrames = -1;

    /// <summary>"--stage=X,Y,Z": once the room is ready, stand the player there (as the capture harness stages a camera) and say where he is a second later.</summary>
    private void StageProbe()
    {
        if (Arg("stage", "").Length == 0 || _world is not { WorldReady: true }) return;
        if (_stageFrames < 0)
        {
            var v = Arg("stage", "").Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
            var at = new Vector3(v[0], v[1], v[2]);
            GarageGame.Native.Room.TerrainCollision.EnsureAround(_world, at, holdS: 3.0);
            _world.Player.Rotation = new Vector3(0, Mathf.DegToRad(float.Parse(Arg("stage-yaw", "90"), CultureInfo.InvariantCulture)), 0);
            _stageFrames = 0;
            GD.Print($"FRAME_PROBE stage: asked for {at}; before the physics server has the tile: {_world.Player.TryTeleportTo(at)} at {_world.Player.GlobalPosition}");
        }
        else if (++_stageFrames is 4 or 30 or 120)
        {
            var again = _world.Player.TryTeleportTo(new Vector3(float.Parse(Arg("stage", "").Split(',')[0], CultureInfo.InvariantCulture), float.Parse(Arg("stage", "").Split(',')[1], CultureInfo.InvariantCulture), float.Parse(Arg("stage", "").Split(',')[2], CultureInfo.InvariantCulture)));
            var camera = GetViewport().GetCamera3D();
            GD.Print($"FRAME_PROBE stage: frame {_stageFrames}: teleport again {again}, the player is at {_world.Player.GlobalPosition} facing {-_world.Player.GlobalBasis.Z}, on floor {_world.Player.IsOnFloor()}; the current camera {camera?.Name} at {camera?.GlobalPosition} looking {(camera != null ? -camera.GlobalBasis.Z : Vector3.Zero)}");
        }
    }

    private bool _atOnceSet;

    /// <summary>"--survey-at-once": the fish survey done in one physics frame, as it was before the first recorded play's fix (the comparison).</summary>
    private void SurveyAtOnce()
    {
        if (_atOnceSet || _world == null || !OS.GetCmdlineUserArgs().Contains("--survey-at-once")) return;
        if (_world.GetChildren().OfType<GarageGame.Native.Look.Fauna.PondLife>().FirstOrDefault() is not { } life) return;
        life.AtOnce();
        _atOnceSet = true;
    }

    private bool _forced;

    /// <summary>"--force-draw": draw the room once, by hand, the moment it is ready: does the first frame's stall (the island's pipelines compiling) move into this call?</summary>
    private void ForceDraw()
    {
        if (_forced || _world is not { WorldReady: true } || !OS.GetCmdlineUserArgs().Contains("--force-draw")) return;
        _forced = true;
        var timer = Stopwatch.StartNew();
        RenderingServer.ForceDraw(false);
        GD.Print(FormattableString.Invariant($"FRAME_PROBE force-draw: one hand-made draw of the ready room took {timer.Elapsed.TotalMilliseconds:0.0} ms"));
    }

    private void Sample()
    {
        StageProbe();
        SurveyAtOnce();
        ForceDraw();
        var now = _clock.Elapsed.TotalMilliseconds;
        if (double.IsNaN(_readyAt) && _world is { WorldReady: true }) _readyAt = now;
        if (_last >= 0)
        {
            var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
            var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            var pipelines = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh)
                + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw)
                + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);
            var gc = $"{gc0 - _gc0}/{gc1 - _gc1}/{gc2 - _gc2}";
            _frames.Add(new Frame(now / 1000.0, now - _last, Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0, Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
                RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport), RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport), gc, pause - _pause, pipelines - _pipelines, StateNow()));
            (_gc0, _gc1, _gc2, _pause, _pipelines) = (gc0, gc1, gc2, pause, pipelines);
        }
        else
        {
            (_gc0, _gc1, _gc2, _pause) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds);
            _pipelines = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh)
                + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw)
                + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);
        }
        _last = now;
        if (!double.IsNaN(_readyAt) && now - _readyAt > double.Parse(Arg("seconds", "60"), CultureInfo.InvariantCulture) * 1000.0) Finish();
    }

    private bool _done;

    private void Finish()
    {
        if (_done) return;
        _done = true;
        var over = double.Parse(Arg("over", "30"), CultureInfo.InvariantCulture);
        var ready = _readyAt / 1000.0;
        GD.Print(FormattableString.Invariant($"FRAME_PROBE room ready at {ready:0.00} s; {_frames.Count} frames; mean frame {_frames.Average(f => f.Ms):0.00} ms"));
        foreach (var f in _frames.Where(f => f.Ms > over))
            GD.Print(FormattableString.Invariant($"FRAME_PROBE slow t={f.At - ready,7:0.00} s (since ready) {f.Ms,7:0.0} ms: process {f.ProcessMs:0.0}, physics {f.PhysicsMs:0.0}, render cpu {f.RenderCpuMs:0.0}, gpu {f.GpuMs:0.0}, collections {f.Gc}, gc pause {f.PauseMs:0.0}, pipelines {f.Pipelines:0}; {f.State}"));
        // As the record's frame events: the longest frame of each five seconds from the room's ready.
        var windows = new List<string>();
        for (var start = 0.0; start < _frames.Last().At - ready; start += 5.0)
        {
            var window = _frames.Where(f => f.At - ready > start && f.At - ready <= start + 5.0).ToList();
            if (window.Count == 0) continue;
            var worst = window.MaxBy(f => f.Ms);
            windows.Add(FormattableString.Invariant($"{start,3:0}-{start + 5,3:0} s: mean {window.Average(f => f.Ms):0.00} ms, longest {worst.Ms:0.0} ms ({worst.State}), {window.Count} frames"));
        }
        foreach (var line in windows) GD.Print("FRAME_PROBE window " + line);
        if (Arg("out", "") is { Length: > 0 } outDir)
        {
            System.IO.Directory.CreateDirectory(outDir);
            var json = new JsonObject
            {
                ["ready_at_s"] = Math.Round(ready, 3), ["frames"] = _frames.Count,
                ["slow"] = new JsonArray(_frames.Where(f => f.Ms > over).Select(f => (JsonNode)new JsonObject
                {
                    ["t_since_ready_s"] = Math.Round(f.At - ready, 3), ["ms"] = Math.Round(f.Ms, 1), ["process_ms"] = Math.Round(f.ProcessMs, 1), ["physics_ms"] = Math.Round(f.PhysicsMs, 1),
                    ["render_cpu_ms"] = Math.Round(f.RenderCpuMs, 1), ["gpu_ms"] = Math.Round(f.GpuMs, 1), ["collections"] = f.Gc, ["gc_pause_ms"] = Math.Round(f.PauseMs, 1), ["pipelines"] = f.Pipelines, ["state"] = f.State,
                }).ToArray()),
                ["windows"] = new JsonArray(windows.Select(w => (JsonNode)w).ToArray()),
            };
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "frame_probe.json"), json.ToJsonString());
        }
        GetTree().Quit(0);
    }
}
