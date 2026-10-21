using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look.Intro;

namespace GarageGame.Tests.Look;

/// <summary>
/// The intro on a GPU (never headless: headless draws nothing and plays no intro): boots a room in a real 1920x1080 window with vsync
/// off ("-- --room=DIR --out=DIR"), lets the intro play as a player sees it at load and times every frame of it (wall clock and GPU),
/// then times a stretch of ordinary play for comparison and the intro once more on the settled island, then plays it again held at chosen moments ("--times=0.5,2.5,...") and saves
/// a picture of each, letting the copies stream in first. Writes intro_timing.json and intro_NN_T.png into --out; prints INTRO_CAPTURE
/// lines. It checks nothing: the pictures are for the founder's eye.
/// </summary>
public partial class IntroCapture : Node
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last = -1;
    private readonly List<(double WallMs, double GpuMs, double Seconds, string Phase)> _frames = new();
    private Func<(double Seconds, string Phase)?>? _recording;

    private sealed partial class FirstInFrame : Node
    {
        public Action? Tick;
        public override void _Process(double delta) => Tick?.Invoke();
    }

    private static string Arg(string name, string fallback) =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..] ?? fallback;

    public override async void _Ready()
    {
        try
        {
            var outDir = System.IO.Path.GetFullPath(Arg("out", "."));
            System.IO.Directory.CreateDirectory(outDir);
            var times = Arg("times", "0.4,1.6,3.2,4.8,6.4,8.0,9.6,11.2,12.6,14.2").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            DisplayServer.WindowSetSize(new Vector2I(1920, 1080));
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            var tick = new FirstInFrame { Name = "FirstInFrame", ProcessPriority = int.MinValue, ProcessMode = ProcessModeEnum.Always };
            tick.Tick = Sample;
            AddChild(tick);
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            world.RoomDirectory = RoomWorld.ResolveRoom(Arg("room", "res://rooms/test_room").Replace('\\', '/'));
            AddChild(world);
            var boot = Stopwatch.StartNew();
            while (!world.WorldReady && world.LoadError.Length == 0 && boot.Elapsed.TotalSeconds < 180) await Frame();
            if (!world.WorldReady) throw new InvalidOperationException("the room did not load: " + world.LoadError);
            GD.Print(FormattableString.Invariant($"INTRO_CAPTURE ready in {boot.Elapsed.TotalSeconds:0.0} s"));
            // 1. As the player sees it at load: the hook started it. Every frame from its first drawn moment to its end.
            var intro = RoomIntro.Of(world) ?? throw new InvalidOperationException("no intro started at load (headless? the setting?)");
            var ended = false;
            intro.Ended += _ => ended = true;
            while (!intro.Playing && !intro.Finished) await Frame();
            _recording = () => intro.Finished ? null : (intro.Seconds, intro.Moment.Phase);
            while (!ended && boot.Elapsed.TotalSeconds < 300) await Frame();
            _recording = null;
            var played = _frames.ToList();
            _frames.Clear();
            GD.Print($"INTRO_CAPTURE played: {intro.EndReason}");
            // 2. Ordinary play afterwards, the same window and view: the comparison.
            for (var i = 0; i < 60; i++) await Frame();
            _recording = () => (0, "play");
            for (var i = 0; i < 400; i++) await Frame();
            _recording = null;
            var play = _frames.ToList();
            _frames.Clear();
            // 3. The intro again, the island settled (the load's own tail, its copies, navigation and compaction, no longer under it).
            var again = RoomIntro.Start(world);
            var againEnded = false;
            again.Ended += _ => againEnded = true;
            while (!again.Playing && !again.Finished) await Frame();
            _recording = () => again.Finished ? null : (again.Seconds, again.Moment.Phase);
            while (!againEnded) await Frame();
            _recording = null;
            var warm = _frames.ToList();
            _frames.Clear();
            for (var i = 0; i < 30; i++) await Frame();
            // 4. The moments, held: each picture after the copies had time to stream round the camera.
            var held = RoomIntro.Start(world);
            held.Paused = true;
            while (!held.Playing && !held.Finished) await Frame();
            var pictures = new JsonArray();
            for (var i = 0; i < times.Length; i++)
            {
                held.Seek(times[i]);
                for (var f = 0; f < 50; f++) await Frame();
                var image = GetViewport().GetTexture().GetImage();
                var file = FormattableString.Invariant($"intro_{i:00}_{times[i]:00.0}s_{held.Moment.Phase}.png");
                image.SavePng(System.IO.Path.Combine(outDir, file));
                pictures.Add(file);
                GD.Print($"INTRO_CAPTURE picture {file}");
            }
            held.Skip();
            for (var i = 0; i < 3; i++) await Frame();
            var summary = new JsonObject
            {
                ["room"] = world.Room.RoomId,
                ["gpu"] = RenderingServer.GetVideoAdapterName(),
                ["intro"] = Stats(played),
                ["intro_by_phase"] = new JsonObject(played.GroupBy(f => f.Phase).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, Stats(g.ToList())))),
                ["intro_slowest"] = new JsonArray(played.OrderByDescending(f => f.WallMs).Take(8).Select(f => (JsonNode)FormattableString.Invariant($"{f.WallMs:0.0} ms at {f.Seconds:0.00} s ({f.Phase})")).ToArray()),
                ["warm"] = Stats(warm),
                ["warm_by_phase"] = new JsonObject(warm.GroupBy(f => f.Phase).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, Stats(g.ToList())))),
                ["warm_slowest"] = new JsonArray(warm.OrderByDescending(f => f.WallMs).Take(8).Select(f => (JsonNode)FormattableString.Invariant($"{f.WallMs:0.0} ms at {f.Seconds:0.00} s ({f.Phase})")).ToArray()),
                ["play"] = Stats(play),
                ["pictures"] = pictures,
            };
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "intro_timing.json"), summary.ToJsonString());
            GD.Print("INTRO_CAPTURE intro " + Line(played));
            GD.Print("INTRO_CAPTURE warm  " + Line(warm));
            GD.Print("INTRO_CAPTURE play  " + Line(play));
            world.QueueFree();
            for (var i = 0; i < 3; i++) await Frame();
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError("INTRO_CAPTURE failed: " + error);
            GetTree().Quit(1);
        }
    }

    private void Sample()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_last >= 0 && _recording?.Invoke() is { } at)
            _frames.Add((now - _last, RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()), at.Seconds, at.Phase));
        _last = now;
    }

    private static JsonObject Stats(List<(double WallMs, double GpuMs, double Seconds, string Phase)> frames)
    {
        if (frames.Count == 0) return new JsonObject { ["frames"] = 0 };
        var wall = frames.Select(f => f.WallMs).OrderBy(v => v).ToList();
        var gpu = frames.Select(f => f.GpuMs).OrderBy(v => v).ToList();
        return new JsonObject
        {
            ["frames"] = frames.Count,
            ["frame_ms_p50"] = Math.Round(P(wall, 0.5), 2), ["frame_ms_p95"] = Math.Round(P(wall, 0.95), 2), ["frame_ms_max"] = Math.Round(wall[^1], 2),
            ["gpu_ms_p50"] = Math.Round(P(gpu, 0.5), 2), ["gpu_ms_p95"] = Math.Round(P(gpu, 0.95), 2),
            ["over_12ms"] = wall.Count(v => v > 12.0), ["over_25ms"] = wall.Count(v => v > 25.0),
        };
    }

    private static string Line(List<(double WallMs, double GpuMs, double Seconds, string Phase)> frames)
    {
        if (frames.Count == 0) return "no frames";
        var wall = frames.Select(f => f.WallMs).OrderBy(v => v).ToList();
        var gpu = frames.Select(f => f.GpuMs).OrderBy(v => v).ToList();
        return FormattableString.Invariant($"{frames.Count} frames: frame {P(wall, 0.5):0.0} ms p50, {P(wall, 0.95):0.0} p95, {wall[^1]:0.0} max; GPU {P(gpu, 0.5):0.0} p50, {P(gpu, 0.95):0.0} p95; {wall.Count(v => v > 12.0)} frames over 12 ms, {wall.Count(v => v > 25.0)} over 25");
    }

    private static double P(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
}
