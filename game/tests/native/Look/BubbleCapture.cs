using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Look.Intro;
using GarageGame.Native.Room;
using Godot;

namespace GarageGame.Tests.Look;

/// <summary>
/// A windowed look at the Bubble and what it costs a frame (the Bubble HUD and look lane, 16 October). Never in a runner: it needs a
/// GPU and a window. Run it on a scratch profile, when nobody is playing:
/// <code>
/// godot --path game --resolution 1280x720 res://tests/native/Look/bubble_capture.tscn -- --room=ID_OR_DIR --capture-dir=ABS_DIR
///       [--seconds=60] [--mode=creative] [--no-playtest-record] [--team=bubble_capture] [--runs=A,B,C] [--script-time=frames]
/// </code>
/// --runs picks which of the runs below happen (windowed: A,B,C; with --headless, which draws nothing and photographs nothing: A,B0,B,
/// the process and physics figures only, for the control B0).
/// It boots the room through the real world and HUD (the intro skipped), then three runs, the player's own input path throughout (the
/// right button's taps with the aim the camera gives, key 3 for the Bubble), and checks nothing but that they happened:
/// - A: --seconds of the room with no bubble, every frame timed (wall clock between frames, the process and physics steps, the
///   renderer's CPU and GPU time);
/// - B: --seconds with a bubble: cast, chosen, aimed at spots with the reticle asking the host's preview at every refresh (the aim
///   moves a little each frame, so no preview is reused), led across the floor and up a ledge when the island has one, chosen again
///   at once whenever a lead ends, recast whenever it pops; the same frame figures, and BubbleCost's (the bubble's draw, the HUD's
///   update, the ray test and the host's preview) in milliseconds a frame;
/// - X: a short probe of the first-use frames, each alone (a new icon in the Gubble's thought bubble, a Bubble cast by the host with no
///   thought bubble, the reticle's first words), every frame timed, to say which is the slow one;
/// - B0: the control for B's process and physics time: the Gubble walks the same round (go_to the same spots) with no bubble at all;
/// - C: the stages photographed (a bubble cast, chosen, a spot aimed at, a lift aimed at, led, lifting, a carry round the player), the
///   reticle's tag read, untimed: the pictures are the founder's to judge.
/// </summary>
public partial class BubbleCapture : Node
{
    private const string Root = "user://tests/bubble_capture";
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>The script's own time: the wall clock, or with --script-time=frames (the headless control, under --fixed-fps 60) the frames counted at 60 a second, so a run lasts as long in the game as it says.</summary>
    private double Now => _fixedFps ? Engine.GetProcessFrames() / 60.0 : _clock.Elapsed.TotalSeconds;
    private readonly bool _fixedFps = Arg("script-time", "wall") == "frames";
    private RoomWorld _world = null!;
    private RoomHud _hud = null!;
    private CommandHost _host = null!;
    private SmallPlayerController _player = null!;
    private Rid _viewport;
    private string _captureDir = "";
    private int _images;
    private int _tapsMade;
    private Sampler? _sampler;
    private double _last = -1;

    private sealed partial class First : Node
    {
        public Action? Tick;
        public override void _Process(double delta) => Tick?.Invoke();
    }

    private sealed class Sampler
    {
        public readonly List<double> Wall = new(), Process = new(), Physics = new(), RenderCpu = new(), Gpu = new(), Pipelines = new(), Pause = new();
        public readonly List<string> Labels = new();
        public readonly List<int> Collections = new();
    }

    /// <summary>What the script is doing now, for the frames that run long to say what they were.</summary>
    private string _label = "idle";
    private double _pipelinesBefore;
    private double _pauseBefore;
    private int _collectionsBefore;
    /// <summary>Whether the cursor is warped to the aim (the stages only: the warp is an operating-system call that would be counted in the timed runs).</summary>
    private bool _warp;

    private static string Arg(string name, string fallback) =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..] ?? fallback;

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Seconds(double seconds)
    {
        var until = Now + seconds;
        while (Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Sample()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_sampler != null && _last >= 0)
        {
            _sampler.Wall.Add(now - _last);
            _sampler.Process.Add(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0);
            _sampler.Physics.Add(Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0);
            _sampler.RenderCpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport));
            _sampler.Gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport));
            var pipelines = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh) +
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw) +
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);
            var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            var collections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            _sampler.Pipelines.Add(pipelines - _pipelinesBefore);
            _sampler.Pause.Add(pause - _pauseBefore);
            _sampler.Collections.Add(collections - _collectionsBefore);
            _sampler.Labels.Add(_label);
            (_pipelinesBefore, _pauseBefore, _collectionsBefore) = (pipelines, pause, collections);
        }
        _last = now;
    }

    /// <summary>What the look still draws of bubbles: none when nothing is cast (a part left behind would show as a stray line).</summary>
    private void Scan(string when)
    {
        var look = _world.Look;
        var parts = look.GetChildren().OfType<Node3D>().Where(n => n.Name.ToString().StartsWith("Bubble", StringComparison.Ordinal) || n.Name.ToString().StartsWith("Boat", StringComparison.Ordinal)).ToList();
        GD.Print($"BUBBLE_CAPTURE scan {when}: {look.BubbleDrawCount} bubbles drawn, {parts.Count} bubble parts under the look: " + string.Join(", ", parts.Select(n => $"{n.Name}{(n is Node3D { Visible: true } ? " (shown)" : "")}{(n.IsQueuedForDeletion() ? " (queued)" : "")}")));
    }

    private static double Percentile(List<double> values, double share)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(share * sorted.Count))];
    }

    private JsonObject Summary(string name, Sampler s)
    {
        var line = FormattableString.Invariant($"BUBBLE_CAPTURE {name}: {s.Wall.Count} frames; frame mean {s.Wall.Average():0.00} ms, p95 {Percentile(s.Wall, 0.95):0.00}, p99 {Percentile(s.Wall, 0.99):0.00}, longest {s.Wall.Max():0.0}; ") +
            FormattableString.Invariant($"process mean {s.Process.Average():0.00} ms (longest {s.Process.Max():0.0}), physics mean {s.Physics.Average():0.00} ms, render cpu mean {s.RenderCpu.Average():0.00} ms, gpu mean {s.Gpu.Average():0.00} ms (longest {s.Gpu.Max():0.00})");
        GD.Print(line);
        foreach (var index in Enumerable.Range(0, s.Wall.Count).OrderByDescending(i => s.Wall[i]).Take(5))
            GD.Print(FormattableString.Invariant($"BUBBLE_CAPTURE   {name} slow frame {index}: {s.Wall[index]:0.0} ms while {s.Labels[index]}: process {s.Process[index]:0.0}, physics {s.Physics[index]:0.0}, render cpu {s.RenderCpu[index]:0.0}, gpu {s.Gpu[index]:0.0}, pipelines compiled {s.Pipelines[index]:0}, collections {s.Collections[index]}, gc pause {s.Pause[index]:0.0} ms"));
        foreach (var group in Enumerable.Range(0, s.Wall.Count).GroupBy(i => s.Labels[i]).OrderByDescending(g => g.Count()).Take(8))
            GD.Print(FormattableString.Invariant($"BUBBLE_CAPTURE   {name} while {group.Key}: {group.Count()} frames, wall mean {group.Average(i => s.Wall[i]):0.00} ms (longest {group.Max(i => s.Wall[i]):0.0}), process mean {group.Average(i => s.Process[i]):0.00}, physics mean {group.Average(i => s.Physics[i]):0.00}"));
        var over = s.Wall.Count(w => w > 20);
        GD.Print(FormattableString.Invariant($"BUBBLE_CAPTURE   {name}: {over} frames over 20 ms, {s.Wall.Count(w => w > 33.4)} over 33 ms, {s.Pipelines.Sum():0} pipelines compiled in all"));
        return new JsonObject
        {
            ["frames"] = s.Wall.Count, ["frame_mean_ms"] = Math.Round(s.Wall.Average(), 3), ["frame_p95_ms"] = Math.Round(Percentile(s.Wall, 0.95), 3), ["frame_p99_ms"] = Math.Round(Percentile(s.Wall, 0.99), 3),
            ["frame_longest_ms"] = Math.Round(s.Wall.Max(), 2), ["process_mean_ms"] = Math.Round(s.Process.Average(), 3), ["process_longest_ms"] = Math.Round(s.Process.Max(), 2),
            ["physics_mean_ms"] = Math.Round(s.Physics.Average(), 3), ["render_cpu_mean_ms"] = Math.Round(s.RenderCpu.Average(), 3), ["gpu_mean_ms"] = Math.Round(s.Gpu.Average(), 3), ["gpu_longest_ms"] = Math.Round(s.Gpu.Max(), 3),
        };
    }

    // ---- the player's own input path ----

    private static InputEventKey Press(Key key) => new() { PhysicalKeycode = key, Pressed = true };

    private void Tap()
    {
        var began = Stopwatch.GetTimestamp();
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        var pressed = Stopwatch.GetTimestamp();
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        var ended = Stopwatch.GetTimestamp();
        _tapsMade++;
        var all = (ended - began) * 1000.0 / Stopwatch.Frequency;
        if (all > 5 || _tapsMade <= 3)
            GD.Print(FormattableString.Invariant($"BUBBLE_CAPTURE tap {_tapsMade} took {all:0.0} ms (press {(pressed - began) * 1000.0 / Stopwatch.Frequency:0.0}, release {(ended - pressed) * 1000.0 / Stopwatch.Frequency:0.0}) while {_label}; choosing so far {BubbleCost.PieceMeanMs(BubbleCost.Piece.Choose) * BubbleCost.Frames:0.0} ms, leading {BubbleCost.PieceMeanMs(BubbleCost.Piece.Lead) * BubbleCost.Frames:0.0} ms in all"));
    }

    /// <summary>The aim the camera gives for a point of the world on the screen (a few pixels of wobble, as a hand moves a mouse): the ray the HUD aims with, and the cursor warped there.</summary>
    private void AimAt(Vector3 world, double wobblePx = 0)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null || camera.IsPositionBehind(world))
        {
            _hud.AimRayForTests = (world + Vector3.Up * 0.3f, world + Vector3.Down * 0.3f);
            return;
        }
        var screen = camera.UnprojectPosition(world);
        if (wobblePx > 0)
        {
            var t = Now * 7.0;
            screen += new Vector2((float)Math.Cos(t), (float)Math.Sin(t * 1.3)) * (float)wobblePx;
        }
        var origin = camera.ProjectRayOrigin(screen);
        _hud.AimRayForTests = (origin, origin + camera.ProjectRayNormal(screen) * RoomHud.AimRangeM);
        if (_warp) GetViewport().WarpMouse(screen);
    }

    private float GroundAt(float x, float z, float near)
    {
        using var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, near + 1f, z), new Vector3(x, near - 1f, z), RoomBuilder.SightMask);
        using var hit = _player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : near;
    }

    private Vector3 Floor(Vector3 at) => new(at.X, GroundAt(at.X, at.Z, _player.GlobalPosition.Y), at.Z);

    /// <summary>The nearest place within a metre and a half that stands over the player's feet by a ledge's height (0.15 to 0.9 m), or null.</summary>
    private Vector3? FindLedge()
    {
        var feet = _player.GlobalPosition;
        Vector3? best = null;
        var bestDistance = float.MaxValue;
        for (var radius = 0.2f; radius <= 1.5f; radius += 0.1f)
            for (var step = 0; step < 24; step++)
            {
                var angle = step * Mathf.Tau / 24;
                var x = feet.X + Mathf.Sin(angle) * radius;
                var z = feet.Z + Mathf.Cos(angle) * radius;
                var y = GroundAt(x, z, feet.Y + 0.6f);
                if (y - feet.Y is >= 0.15f and <= 0.9f && radius < bestDistance) { best = new Vector3(x, y, z); bestDistance = radius; }
            }
        return best;
    }

    private async Task<bool> CastAt(Vector3 spot)
    {
        _label = "casting the bubble (key 3)";
        _hud.AimRayForTests = (spot + Vector3.Up * 0.3f, spot + Vector3.Down * 0.3f);
        _hud._UnhandledInput(Press(Key.Key3));
        for (var i = 0; i < 600 && _host.ActiveBubble == null; i++) await Frames(1);
        await Frames(3);
        _label = "the bubble waiting";
        return _host.ActiveBubble != null;
    }

    private async Task<bool> Choose()
    {
        if (_host.ActiveBubble is not { } bubble) return false;
        _label = "aiming at the bubble";
        AimAt(bubble.CenterM);
        await Frames(8);
        _label = "choosing the bubble (right-click)";
        Tap();
        await Frames(2);
        _label = "the bubble chosen";
        return _hud.SelectedBubble == bubble.Id;
    }

    /// <summary>Aim at a spot a moment (the reticle asks the host), tap, and wait for the lead to end or time out. True when it was set.</summary>
    private async Task<bool> LeadTo(Vector3 spot, double hoverS, bool wait = true, double wobblePx = 3)
    {
        var until = Now + hoverS;
        _label = "aiming at a spot (the reticle asks the host)";
        while (Now < until) { AimAt(spot, wobblePx); await Frames(1); }
        AimAt(spot);
        await Frames(2);
        _label = "leading the bubble (right-click)";
        Tap();
        await Frames(3);
        var set = _host.ActiveBubble is { LedToM: not null };
        _label = "the bubble led";
        if (!wait) return set;
        var deadline = Now + 25;
        while (_host.ActiveBubble is { LedToM: not null } && Now < deadline)
        {
            _label = _host.ActiveBubble is { Lifting: true } ? "the bubble rising on a lift" : "the bubble led";
            AimAt(spot, wobblePx);
            await Frames(1);
        }
        _label = "the lead over";
        return set;
    }

    private async Task<bool> Grab(string name)
    {
        if (_captureDir.Length == 0) return false;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgb8);
        var ok = image.SavePng(System.IO.Path.Combine(_captureDir, name + ".png")) == Error.Ok;
        if (ok) _images++;
        GD.Print($"BUBBLE_CAPTURE image {name}.png: {(ok ? "saved" : "NOT saved")}; the reticle says \"{_hud.AskTag.Text}\" ({(_hud.AskTag.Visible ? "shown" : "hidden")}); selected {_hud.SelectedBubble != null}; the Gubble's bubble {_hud.Cue.Bubble.Text} {_hud.Cue.Reason}");
        return ok;
    }

    // ---- the runs ----

    private async Task RunB(double seconds, Vector3 floorA, Vector3 floorB, Vector3? ledge)
    {
        var started = Now;
        var leads = 0;
        var casts = 0;
        var lifts = 0;
        var refusals = 0;
        var targets = new List<(Vector3 Spot, bool Lift)> { (floorA, false), (floorB, false) };
        if (ledge is { } up) targets.Insert(1, (up, true));
        var next = 0;
        while (Now - started < seconds)
        {
            if (_host.ActiveBubble == null)
            {
                if (!await CastAt(floorB)) { await Seconds(0.5); continue; }
                casts++;
            }
            if (_hud.SelectedBubble == null && !await Choose()) { await Seconds(0.5); continue; }
            // The reticle is asked at every refresh while the aim wanders over a spot, then the click leads it there; chosen again at once.
            var (spot, lift) = targets[next++ % targets.Count];
            if (!await LeadTo(spot, 1.2)) refusals++;
            else { leads++; if (lift) lifts++; }
            await Seconds(0.3);
        }
        GD.Print($"BUBBLE_CAPTURE B script: {casts} casts, {leads} leads set ({lifts} of them lifts), {refusals} refused, {_tapsMade} taps, previews asked {_hud.LeadPreviews}");
    }

    /// <summary>
    /// First uses, one at a time with a second of nothing between, every frame timed and labelled: a new icon in the Gubble's thought
    /// bubble (Glow's sun, never shown before), the Bubble cast by the host with no thought bubble, then its icon, then the reticle's words.
    /// </summary>
    private async Task RunProbe()
    {
        var spot = Floor(_player.GlobalPosition + -_player.GlobalBasis.Z * 0.4f);
        _hud.AimRayForTests = (spot + Vector3.Up * 0.3f, spot + Vector3.Down * 0.3f);
        _sampler = new Sampler();
        _label = "nothing";
        await Seconds(1);
        _label = "a new icon in the thought bubble (Glow's sun)";
        _hud.Cue.Think(GubbleMagic.SlotIcons[0]);
        await Seconds(1);
        _hud.Cue.Clear();
        _label = "nothing, after";
        await Seconds(1);
        _label = "the Bubble cast by the host, no thought bubble";
        _host.PlayerEffect("bubbles", spot);
        await Seconds(1);
        _label = "the Bubble's icon in the thought bubble";
        _hud.Cue.Think(GubbleMagic.BubbleIcon);
        await Seconds(1);
        _hud.Cue.Done();
        _label = "the done mark in the thought bubble";
        await Seconds(1);
        _label = "the reticle's first words (go and look / choose this bubble)";
        _hud.AimRayForTests = (_host.ActiveBubble!.Value.CenterM + Vector3.Up * 0.3f, _host.ActiveBubble!.Value.CenterM + Vector3.Down * 0.3f);
        await Seconds(1);
        _label = "nothing, at the end";
        await Seconds(1);
        _host.PlayerGoal("stop");
        Summary("X (first uses, one at a time)", _sampler);
        _sampler = null;
    }

    /// <summary>The control for B: the Gubble walks the same round, no bubble at all.</summary>
    private async Task RunControl(double seconds, Vector3 floorA, Vector3 floorB, Vector3? ledge)
    {
        var started = Now;
        var targets = new List<Vector3> { floorA };
        if (ledge is { } up) targets.Add(up);
        targets.Add(floorB);
        var next = 0;
        while (Now - started < seconds)
        {
            _label = "the Gubble walking (no bubble)";
            _host.PlayerGoal("go_to", targets[next++ % targets.Count]);
            await Seconds(6);
        }
        _host.PlayerGoal("stay");
    }

    /// <summary>A bubble to work on: the one there, or a new one cast at this spot (a bubble's time runs out).</summary>
    private async Task<bool> EnsureBubble(Vector3 spot)
    {
        if (_host.ActiveBubble != null) return true;
        _host.PlayerGoal("follow");
        await Seconds(3);
        _host.PlayerGoal("stay");
        var cast = await CastAt(spot);
        if (!cast) GD.Print($"BUBBLE_CAPTURE stages: the recast did not happen; the Gubble's bubble {_hud.Cue.Bubble.Text} {_hud.Cue.Reason}");
        return cast;
    }

    private async Task Stages()
    {
        // The player's view: the shoulder camera a little out, so a bubble's whole path is in frame.
        _warp = true;
        _label = "the stages";
        _hud.SetViewMode(1);
        _hud.ZoomShoulder(-4);
        // The Gubble beside the player first, so a lead is not waiting for it to come.
        _host.PlayerGoal("follow");
        await Seconds(3);
        _host.PlayerGoal("stay");
        await Seconds(0.5);
        var feet = _player.GlobalPosition;
        var forward = -_player.GlobalBasis.Z;
        var right = _player.GlobalBasis.X;
        var spotOne = Floor(feet + forward * 0.34f + right * 0.12f);
        _host.PlayerGoal("stay");
        Scan("before the stages' first cast");
        if (!await CastAt(spotOne)) { GD.Print("BUBBLE_CAPTURE stages: the Bubble was not cast"); return; }
        Scan("after the stages' first cast");
        await Seconds(1.2);
        AimAt(Floor(feet + forward * 0.5f - right * 0.35f));
        await Seconds(0.3);
        await Grab("01-cast");
        if (!await Choose()) { GD.Print("BUBBLE_CAPTURE stages: the bubble was not chosen"); return; }
        AimAt(_host.ActiveBubble!.Value.CenterM);
        await Seconds(0.5);
        await Grab("02-chosen");
        var spotTwo = Floor(feet + forward * 0.85f - right * 0.4f);
        AimAt(spotTwo);
        await Seconds(0.6);
        await Grab("03-lead-aimed");
        await LeadTo(spotTwo, 0.1, wait: false, wobblePx: 0);
        await Seconds(1.1);
        // (Aimed past the bubble, so the reticle's words are not about the bubble itself.)
        AimAt(Floor(feet + forward * 1.0f - right * 0.1f));
        await Grab("04-led");
        var deadline = Now + 20;
        while (_host.ActiveBubble is { LedToM: not null } && Now < deadline) await Frames(1);
        await Seconds(0.8);
        if (FindLedge() is { } ledge)
        {
            // (A bubble's time runs out: cast another if this one is gone, and choose it.)
            var have = await EnsureBubble(spotOne);
            var chosen = have && await Choose();
            GD.Print($"BUBBLE_CAPTURE stages: for the lift a bubble {(have ? "is there" : "was not cast")}, chosen {chosen}, the tag says \"{_hud.AskTag.Text}\"");
            if (have && !chosen && _host.ActiveBubble is { } there) { _hud.SelectBubble(there.Id); GD.Print("BUBBLE_CAPTURE stages: chosen by the HUD's own call instead (the ray missed it)"); }
            AimAt(ledge);
            await Seconds(0.6);
            await Grab("05-lift-aimed");
            await LeadTo(ledge, 0.1, wait: false, wobblePx: 0);
            var lifting = false;
            deadline = Now + 25;
            while (_host.ActiveBubble is { LedToM: not null } && Now < deadline)
            {
                if (_host.ActiveBubble is { Lifting: true } && !lifting) { lifting = true; await Seconds(0.8); AimAt(ledge); await Grab("06-lifting"); }
                await Frames(1);
            }
            GD.Print($"BUBBLE_CAPTURE stages: ledge at {ledge} ({ledge.Y - feet.Y:0.00} m over the player's feet); lifted {lifting}");
            await Seconds(0.8);
            await Grab("07-lifted");
        }
        else GD.Print("BUBBLE_CAPTURE stages: no ledge within 1.5 m of the player; no lift photographed");
        // A carry round the player: cast on the player's own feet, led a little; the drawn body floats inside. The player wears the
        // family's Potato Man, so the float gait shows (the prototype figure has no gait).
        _host.PlayerGoal("stay");
        Scan("before the carry");
        GD.Print($"BUBBLE_CAPTURE stages: the player wears potato_man: {_hud.ChooseCharacter("potato_man")}");
        _hud.ZoomShoulder(5);
        _hud.AimRayForTests = (_player.GlobalPosition + Vector3.Up * 0.3f, _player.GlobalPosition + Vector3.Down * 0.3f);
        _hud._UnhandledInput(Press(Key.Key3));
        for (var i = 0; i < 600 && !(_host.ActiveBubble is { HoldsPlayer: true }); i++) await Frames(1);
        await Seconds(1.0);
        AimAt(Floor(_player.GlobalPosition + forward * 0.3f));
        await Grab("08-carry");
        if (await Choose())
        {
            var around = Floor(_player.GlobalPosition + forward * 0.45f + right * 0.25f);
            await LeadTo(around, 0.1, wait: false, wobblePx: 0);
            await Seconds(1.4);
            await Grab("09-carry-led");
        }
        _host.PlayerGoal("stop");
    }

    public override async void _Ready()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        try
        {
            _captureDir = Arg("capture-dir", "");
            if (_captureDir.Length > 0) System.IO.Directory.CreateDirectory(_captureDir);
            var windowed = EyeView.CanRender;
            var runs = Arg("runs", windowed ? "A,B,C" : "A,B0,B").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            if (!windowed && runs.Contains("C")) throw new InvalidOperationException("the stages are photographed: run it windowed, not headless");
            CommandHost.SaveRoot = Root + "/saves";
            CommandHost.DefaultGlowProgressPath = Root + "/profile/glow_progress_v1.json";
            CommandHost.DefaultPlayerVitalsPath = Root + "/profile/player_vitals_v1.json";
            CommandHost.DefaultWishSaveRoot = Root + "/saves/wishes";
            RoomHud.ProfilePath = Root + "/profile/avatar_profile_v1.cfg";
            _viewport = GetViewport().GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
            AddChild(new First { Name = "FirstInFrame", ProcessPriority = int.MinValue, ProcessMode = ProcessModeEnum.Always, Tick = Sample });
            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            _world.RoomDirectory = RoomWorld.ResolveRoom(Arg("room", "res://rooms/test_room").Replace('\\', '/'));
            AddChild(_world);
            for (var booting = Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 120 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frames(1);
            if (!_world.WorldReady) throw new InvalidOperationException("the room did not load: " + _world.LoadError);
            _hud = _world.GetNode<RoomHud>("RoomHud");
            _player = _world.Player;
            _host = CommandHost.Of(_world)!;
            GD.Print($"BUBBLE_CAPTURE start: room {_world.RoomDirectory}, mode {_host.Mode}, window {GetViewport().GetVisibleRect().Size}, pool {_host.Vitals.Pips} pips");
            for (var waited = 0; RoomIntro.Of(_world) is { } intro && waited < 3000; waited++)
            {
                if (intro.Playing) intro.Skip();
                await Frames(1);
            }
            _hud.LightForTests = null;
            _hud.AimRayForTests = null;
            await Seconds(4);
            _host.PlayerGoal("follow");
            await Seconds(4);
            _host.PlayerGoal("stay");
            var seconds = double.Parse(Arg("seconds", "60"), CultureInfo.InvariantCulture);

            var a = new JsonObject();
            var b0 = new JsonObject();
            var b = new JsonObject();
            var cost = new JsonObject();
            // A: the room with no bubble.
            if (runs.Contains("A"))
            {
                _hud.AimRayForTests = null;
                _label = "no bubble";
                _sampler = new Sampler();
                await Seconds(seconds);
                a = Summary("A (no bubble)", _sampler);
                _sampler = null;
            }

            // B: a bubble cast, chosen, aimed, led and lifted, the minute through.
            var feet = _player.GlobalPosition;
            var forward = -_player.GlobalBasis.Z;
            var right = _player.GlobalBasis.X;
            var floorA = Floor(feet + forward * 0.5f - right * 0.35f);
            var floorB = Floor(feet + forward * 0.3f + right * 0.4f);
            var ledge = FindLedge();
            GD.Print($"BUBBLE_CAPTURE B setup: player {feet}, floor spots {floorA} {floorB}, ledge {(ledge is { } l ? l.ToString() : "none within 1.5 m")}");
            if (runs.Contains("B0"))
            {
                _label = "idle";
                _sampler = new Sampler();
                await RunControl(seconds, floorA, floorB, ledge);
                b0 = Summary("B0 (the Gubble walks the same round, no bubble)", _sampler);
                _sampler = null;
                await Seconds(1);
                // Back where the round began, so B finds the same ground.
                _host.PlayerGoal("follow");
                await Seconds(4);
                _host.PlayerGoal("stay");
            }
            if (runs.Contains("B"))
            {
                BubbleCost.Measuring = true;
                BubbleCost.Reset();
                _label = "idle";
                _sampler = new Sampler();
                await RunB(seconds, floorA, floorB, ledge);
                b = Summary("B (a bubble cast, chosen, aimed, led, lifted)", _sampler);
                _sampler = null;
                BubbleCost.Measuring = false;
                cost = new JsonObject
                {
                    ["frames"] = BubbleCost.Frames, ["mean_ms"] = Math.Round(BubbleCost.MeanMs, 4), ["longest_ms"] = Math.Round(BubbleCost.WorstMs, 3),
                    ["p99_ms"] = Math.Round(Percentile(BubbleCost.SamplesMs, 0.99), 4), ["draw_mean_ms"] = Math.Round(BubbleCost.PieceMeanMs(BubbleCost.Piece.Draw), 4),
                    ["select_mean_ms"] = Math.Round(BubbleCost.PieceMeanMs(BubbleCost.Piece.Select), 4), ["aim_mean_ms"] = Math.Round(BubbleCost.PieceMeanMs(BubbleCost.Piece.Aim), 4),
                    ["show_total_ms"] = Math.Round(BubbleCost.PieceMeanMs(BubbleCost.Piece.Show) * BubbleCost.Frames, 2), ["choosing_total_ms"] = Math.Round(BubbleCost.PieceMeanMs(BubbleCost.Piece.Choose) * BubbleCost.Frames, 2), ["leading_total_ms"] = Math.Round(BubbleCost.PieceMeanMs(BubbleCost.Piece.Lead) * BubbleCost.Frames, 2),
                    ["preview_mean_ms"] = Math.Round(BubbleCost.PieceMeanMs(BubbleCost.Piece.Preview), 4), ["allocated_kb"] = BubbleCost.AllocatedBytes / 1024,
                };
                GD.Print(FormattableString.Invariant($"BUBBLE_CAPTURE cost in B: {BubbleCost.Frames} frames; the bubble costs {BubbleCost.MeanMs:0.0000} ms a frame on average (longest {BubbleCost.WorstMs:0.000}, p99 {Percentile(BubbleCost.SamplesMs, 0.99):0.0000}): ") +
                    FormattableString.Invariant($"draw {BubbleCost.PieceMeanMs(BubbleCost.Piece.Draw):0.0000}, the HUD's update {BubbleCost.PieceMeanMs(BubbleCost.Piece.Select):0.0000}, the ray test {BubbleCost.PieceMeanMs(BubbleCost.Piece.Aim):0.0000}, the host's preview {BubbleCost.PieceMeanMs(BubbleCost.Piece.Preview):0.0000} ms; allocated {BubbleCost.AllocatedBytes / 1024.0:0.0} KB over the run"));
                _host.PlayerGoal("stop");
                await Seconds(1);
                Scan("after B");
            }

            if (runs.Contains("X")) await RunProbe();

            // C: the stages photographed, untimed.
            if (runs.Contains("C")) await Stages();
            if (_captureDir.Length > 0)
                System.IO.File.WriteAllText(System.IO.Path.Combine(_captureDir, "bubble_capture.json"), new JsonObject { ["a_no_bubble"] = a, ["b0_gubble_walks"] = b0, ["b_bubble"] = b, ["bubble_cost"] = cost, ["images"] = _images }.ToJsonString());
            GD.Print($"BUBBLE_CAPTURE done: {_images} images saved to {_captureDir}");
            _world.Free();
            await Frames(2);
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError("BUBBLE_CAPTURE failed: " + error);
            GetTree().Quit(1);
        }
        finally
        {
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }
}
