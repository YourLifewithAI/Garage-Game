using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Lane P capture harness for what the founder sees while playing: the companion's name tag and the running
/// player, in the real window with the real post settings. It boots the room, pins the clock, drives the HUD's
/// own cameras (F2 shoulder, F3 orbit, F4 isometric) and measures edge sharpness in the pixels, saving crops.
/// Run it with tools/kernel/capture-play.ps1; it needs a real window on the GPU (under --headless it exits 2).
///
/// User arguments (after "--"): --out=DIR [--tag=TEXT] [--scenarios=label_f2,label_f3_orbit,run_f3,run_f2,timing]
/// [--taa=true|false] [--msaa=0|2|4|8] [--saa=fxaa] [--fsr2=true] [--dof-jitter=true|false] [--interp=true|false] [--fps=0|60|144] [--vsync=true|false]
/// [--hour=17.5] [--date=2026-10-06] [--frames=N] [--probe] [--seam-z=-29.8,...] [--seam-pitch=0] (seam_f2: PlayCaptureHarness.Seam.cs)
///   taa, msaa  the window's anti-aliasing (the project default is TAA on, MSAA off)
///   interp     physics interpolation in the scene tree (the project default is off)
///   fps        the frame-rate cap (0 is uncapped); with --vsync=false it stands in for a monitor's refresh rate
/// Sharpness is the mean of the strongest tenth of Sobel gradients of the luma in a crop (bigger is crisper); a
/// body or a tag that is smeared, doubled or defocused scores lower than the same one at rest.
/// </summary>
public partial class PlayCaptureHarness : Node
{
    private readonly Dictionary<string, string> _args = new();
    private readonly List<Dictionary<string, object>> _records = new();
    private RoomWorld _world = null!;
    private RoomHud _hud = null!;
    private string _out = "";
    private string _tag = "";
    private Label3D _label = null!;
    private int _debugEdge;

    public override async void _Ready()
    {
        try
        {
            foreach (var argument in OS.GetCmdlineUserArgs())
            {
                var text = argument.TrimStart('-');
                var split = text.IndexOf('=');
                _args[split < 0 ? text : text[..split]] = split < 0 ? "true" : text[(split + 1)..];
            }
            // --probe: a scenario that measures without pictures (seam_f2) may run headless.
            if (DisplayServer.GetName() == "headless" && Arg("probe", "false") != "true")
            {
                GD.Print("PLAY_CAPTURE: needs a real window on the GPU; --headless renders nothing. Run tools/kernel/capture-play.ps1.");
                GetTree().Quit(2);
                return;
            }
            _out = Require("out");
            _tag = Arg("tag", "capture");
            System.IO.Directory.CreateDirectory(_out);
            var fps = int.Parse(Arg("fps", "0"), CultureInfo.InvariantCulture);
            Engine.MaxFps = fps;
            DisplayServer.WindowSetVsyncMode(Arg("vsync", "false") == "true" ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
            var window = GetViewport();
            window.UseTaa = Arg("taa", window.UseTaa.ToString()).ToLowerInvariant() == "true";
            window.Msaa3D = int.Parse(Arg("msaa", "0"), CultureInfo.InvariantCulture) switch
            {
                2 => Viewport.Msaa.Msaa2X, 4 => Viewport.Msaa.Msaa4X, 8 => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Disabled,
            };
            if (Arg("saa", "").Length > 0) window.ScreenSpaceAA = Arg("saa", "") == "fxaa" ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
            if (Arg("dof-jitter", "").Length > 0)
                RenderingServer.CameraAttributesSetDofBlurQuality(RenderingServer.DofBlurQuality.Medium, Arg("dof-jitter", "") == "true");
            if (Arg("fsr2", "false") == "true") { window.Scaling3DMode = Viewport.Scaling3DModeEnum.Fsr2; window.Scaling3DScale = 1.0f; }
            GetTree().PhysicsInterpolation = Arg("interp", GetTree().PhysicsInterpolation.ToString()).ToLowerInvariant() == "true";

            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            // --room=ID|DIR: a captured or exported room (a landscape) instead of the test room.
            if (Arg("room", "").Length > 0) _world.RoomDirectory = RoomWorld.ResolveRoom(Arg("room", "").Replace('\\', '/'));
            AddChild(_world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_world.WorldReady && _world.LoadError.Length == 0;) await NextFrame();
            if (!_world.WorldReady) throw new InvalidOperationException("room did not load: " + _world.LoadError);
            _hud = _world.GetNode<RoomHud>("RoomHud");
            _label = _world.Companion.GetNode<Label3D>("CompanionLabel");
            // Experiments on the tag, to compare how it is drawn without editing the game: --label-alpha=none|discard|prepass|hash
            // and --label-scissor=0.5 override what CompanionAvatar chose; --label-notest=true turns depth testing off.
            if (Arg("label-alpha", "").Length > 0)
                _label.AlphaCut = Arg("label-alpha", "") switch
                {
                    "discard" => Label3D.AlphaCutMode.Discard, "prepass" => Label3D.AlphaCutMode.OpaquePrepass,
                    "hash" => Label3D.AlphaCutMode.Hash, _ => Label3D.AlphaCutMode.Disabled,
                };
            if (Arg("label-scissor", "").Length > 0) _label.AlphaScissorThreshold = float.Parse(Arg("label-scissor", "0.5"), CultureInfo.InvariantCulture);
            if (Arg("label-notest", "").Length > 0) _label.NoDepthTest = Arg("label-notest", "") == "true";
            // The interface would only add colour to the crops; the 3D scene is what is measured.
            if (Arg("hud", "false") != "true")
                foreach (var layer in _world.FindChildren("*", "CanvasLayer", true, false).OfType<CanvasLayer>()) layer.Visible = false;
            _world.Player.ReadKeyboard = false;
            _world.Look.SetClock(float.Parse(Arg("hour", "17.5"), CultureInfo.InvariantCulture),
                DateTime.ParseExact(Arg("date", "2026-10-06"), "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfYear);
            _world.Look.SetLamps(null);
            for (var i = 0; i < 180; i++) await NextFrame();

            var scenarios = Arg("scenarios", "label_f2,label_f3_orbit,run_f3,run_f2,timing").Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var scenario in scenarios)
            {
                GD.Print($"PLAY_CAPTURE scenario={scenario} tag={_tag}");
                switch (scenario)
                {
                    case "label_f2": await LabelF2(); break;
                    case "label_f3_orbit": await LabelF3Orbit(); break;
                    case "run_f3": await RunScenario(2, "run_f3"); break;
                    case "run_f2": await RunScenario(1, "run_f2"); break;
                    case "judder_f3": await Judder(2, "judder_f3"); break;
                    case "judder_f2": await Judder(1, "judder_f2"); break;
                    case "hud_f4": await HudF4(); break;
                    case "steady_f3": await Steady(2, "steady_f3"); break;
                    case "steady_f2": await Steady(1, "steady_f2"); break;
                    case "timing": await Timing(); break;
                    case "landscape": await Landscape(); break;
                    case "seam_f2": await SeamF2(); break;
                    default: throw new ArgumentException("unknown scenario " + scenario);
                }
            }
            WriteReport();
            GD.Print($"PLAY_CAPTURE_DONE tag={_tag} records={_records.Count} out={_out}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError("Play capture failed: " + error);
            GetTree().Quit(1);
        }
    }

    private string Arg(string name, string fallback) => _args.TryGetValue(name, out var value) ? value : fallback;
    private string Require(string name) => _args.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"missing --{name}=...");
    private async Task NextFrame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task<Image> Grab()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgb8);
        return image;
    }

    private Camera3D ActiveCamera => GetViewport().GetCamera3D();

    /// <summary>Stand the avatars where a scenario wants them: the player at a spot facing a heading, the companion at an offset in front.</summary>
    private async Task Stage(Vector3 playerFeet, float yawDeg, Vector3 companionOffsetInFront)
    {
        var player = _world.Player;
        var companion = _world.Companion;
        _hud.SetViewMode(1);
        player.SetControlInput(Vector2.Zero);
        companion.Stay();
        player.TryTeleportTo(playerFeet);
        player.Rotation = new Vector3(0, Mathf.DegToRad(yawDeg), 0);
        player.ResetPhysicsInterpolation();
        var ahead = player.GlobalBasis * companionOffsetInFront;
        companion.TryTeleportTo(playerFeet + ahead);
        companion.Rotation = new Vector3(0, Mathf.DegToRad(yawDeg + 180), 0);
        companion.ResetPhysicsInterpolation();
        for (var i = 0; i < 60; i++) await NextFrame();
    }

    // ---- scenarios ----

    /// <summary>
    /// First pictures of a landscape room (--room=DIR) as the game shows it, nothing tuned: the 10 cm eye at the spawn
    /// looking towards the carryable thing's settlement, that thing close up over the player's shoulder, the observe view
    /// from the diorama camera, and one wide view from above the room's near edge.
    /// </summary>
    private async Task Landscape()
    {
        var room = _world.Room;
        var thing = room.Objects.FirstOrDefault(o => o.Asset.Movable) ?? room.Objects.First();
        var spawn = room.SpawnFor("player").PositionM;
        var toThing = new Vector2(thing.PositionM.X - spawn.X, thing.PositionM.Z - spawn.Z);
        var yaw = Mathf.RadToDeg(Mathf.Atan2(-toThing.X, -toThing.Y));
        async Task Save(string name)
        {
            for (var i = 0; i < 90; i++) await NextFrame();
            var image = await Grab();
            var path = System.IO.Path.Combine(_out, $"{_tag}_{name}.png");
            image.SavePng(path);
            AddRecord(name, "frame", new List<double> { 0 }, $"{_tag}_{name}.png", null);
            GD.Print($"PLAY_CAPTURE_FRAME {path}");
        }

        await Stage(spawn + Vector3.Up * 0.005f, yaw, new Vector3(0.12f, 0, -0.25f));
        _hud.SetViewMode(0);
        await Save("landscape_eye_spawn");

        // Close up: the player a quarter of a metre from the thing, facing it, under the shoulder camera.
        var away = new Vector3(-toThing.X, 0, -toThing.Y).Normalized() * 0.25f;
        await Stage(thing.PositionM + away + Vector3.Up * 0.01f, yaw, new Vector3(0.14f, 0, 0.05f));
        _hud.SetViewMode(1);
        await Save("landscape_crate_close");

        _hud.SetViewMode(2);
        _world.Look.Observe = true;
        await Save("landscape_observe");
        _world.Look.Observe = false;

        var wide = new Camera3D { Name = "WideCapture", Fov = 55, Near = 0.05f, Far = 200 };
        _world.AddChild(wide);
        var centre = room.Bounds.GetCenter();
        wide.GlobalPosition = new Vector3(centre.X, room.Bounds.End.Y + 2.5f, room.Bounds.Position.Z - 3.5f);
        wide.LookAt(new Vector3(centre.X, 0.3f, centre.Z + 0.5f), Vector3.Up);
        wide.MakeCurrent();
        await Save("landscape_wide");
        wide.QueueFree();
        _hud.SetViewMode(1);
    }

    /// <summary>F2: the shoulder camera on a companion standing in front, the tag over its head against the far room.</summary>
    private async Task LabelF2()
    {
        await Stage(new Vector3(0.0f, 0.003f, 0.9f), 0, new Vector3(0.04f, 0, -0.42f));
        _hud.SetViewMode(1);
        for (var i = 0; i < 90; i++) await NextFrame();
        var results = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            var image = await Grab();
            var rect = LabelRect(image);
            var measure = Sharpness(image, rect);
            results.Add(measure.Sharpness);
            if (i == 0)
            {
                image.SavePng(System.IO.Path.Combine(_out, $"{_tag}_label_f2_frame.png"));
                Crop(image, rect, 4).SavePng(System.IO.Path.Combine(_out, $"{_tag}_label_f2_crop.png"));
            }
            await NextFrame();
        }
        AddRecord("label_f2", "label", results, $"{_tag}_label_f2_frame.png", $"{_tag}_label_f2_crop.png");
    }

    /// <summary>F3: the diorama camera turning round the avatars while the tag stays over the companion.</summary>
    private async Task LabelF3Orbit()
    {
        await Stage(new Vector3(0.0f, 0.003f, 0.9f), 0, new Vector3(0.1f, 0, -0.16f));
        _hud.SetViewMode(2);
        for (var i = 0; i < 60; i++) await NextFrame();
        // A reference with the camera at rest, then the same tag with the camera orbiting at about 35 degrees a second.
        var rest = new List<double>();
        for (var i = 0; i < 6; i++) { var image = await Grab(); rest.Add(Sharpness(image, LabelRect(image)).Sharpness); await NextFrame(); }
        AddRecord("label_f3_rest", "label", rest, null, null);
        var moving = new List<double>();
        var saved = 0;
        for (var i = 0; i < 90; i++)
        {
            _hud.OrbitDiorama(new Vector2(4, 0));
            await NextFrame();
            if (i < 30 || i % 4 != 0) continue;
            var image = await Grab();
            var rect = LabelRect(image);
            moving.Add(Sharpness(image, rect).Sharpness);
            if (saved < 4)
            {
                saved++;
                if (saved == 1) image.SavePng(System.IO.Path.Combine(_out, $"{_tag}_label_f3_orbit_frame.png"));
                Crop(image, rect, 4).SavePng(System.IO.Path.Combine(_out, $"{_tag}_label_f3_orbit_crop{saved}.png"));
            }
        }
        AddRecord("label_f3_orbit", "label", moving, $"{_tag}_label_f3_orbit_frame.png", $"{_tag}_label_f3_orbit_crop1.png");
    }

    /// <summary>
    /// The player running along an open stretch of floor under F3 (or F2) past the companion standing still beside the track.
    /// Both bodies are measured in the same frames, a body-width edge softness each: how many pixels the silhouette takes to
    /// go from body to background (small is crisp). The founder saw the running player soft beside a sharp companion.
    /// </summary>
    private async Task RunScenario(int viewMode, string name)
    {
        var player = _world.Player;
        var companion = _world.Companion;
        // Along +X on the open floor (z = -0.3 clears the table, the box and the book); the companion waits beside the track.
        await Stage(new Vector3(-0.95f, 0.003f, -0.3f), -90, new Vector3(0.25f, 0, 0.0f));
        companion.TryTeleportTo(new Vector3(0.05f, 0.003f, -0.12f));
        companion.ResetPhysicsInterpolation();
        _hud.SetViewMode(viewMode);
        for (var i = 0; i < 40; i++) await NextFrame();
        // Standing first, from the start of the track: how crisp the player is in this camera at rest.
        var rest = new List<double>();
        for (var i = 0; i < 6; i++) { var image = await Grab(); if (EdgeSoftnessPx(image, player) is { } w) rest.Add(w); await NextFrame(); }
        if (rest.Count > 0) AddRecord(name + "_player_rest", "edge_px_player", rest, null, null, edge: true);
        // Then running: full speed after about 0.2 s; samples while the player passes the companion.
        player.SetControlInput(new Vector2(0, 1), sprint: true);
        for (var i = 0; i < 20; i++) await NextFrame();
        var runPlayer = new List<double>();
        var runCompanion = new List<double>();
        var saved = 0;
        var start = player.GlobalPosition;
        for (var i = 0; i < 400 && player.GlobalPosition.X < 0.5f; i++)
        {
            if (Mathf.Abs(player.GlobalPosition.X - 0.05f) > 0.4f || i % 2 != 0) { await NextFrame(); continue; }
            var image = await Grab();
            var p = EdgeSoftnessPx(image, player);
            var c = EdgeSoftnessPx(image, companion);
            if (p is { } pw) runPlayer.Add(pw);
            if (c is { } cw) runCompanion.Add(cw);
            if (saved < 6 && p != null)
            {
                saved++;
                if (saved == 1) image.SavePng(System.IO.Path.Combine(_out, $"{_tag}_{name}_frame.png"));
                var both = BodyRect(image, player).Merge(BodyRect(image, companion));
                Crop(image, both, 3).SavePng(System.IO.Path.Combine(_out, $"{_tag}_{name}_crop{saved}.png"));
            }
        }
        var ran = player.GlobalPosition.X - start.X;
        player.SetControlInput(Vector2.Zero);
        if (runPlayer.Count > 0)
        {
            var record = AddRecord(name + "_player_run", "edge_px_player", runPlayer, $"{_tag}_{name}_frame.png", $"{_tag}_{name}_crop1.png", edge: true);
            record["ran_m"] = Math.Round(ran, 3);
        }
        if (runCompanion.Count > 0) AddRecord(name + "_companion_standing", "edge_px_companion", runCompanion, null, null, edge: true);
        for (var i = 0; i < 30; i++) await NextFrame();
        _hud.SetViewMode(1);
    }

    /// <summary>
    /// How steadily the running player moves across the screen, from the pixels: the head's centroid in every displayed
    /// frame, its scatter (px) about a straight line through the run, and the rendered frame rate. A body that moves only on
    /// physics ticks (60 a second) while frames come at the display's rate steps unevenly across the screen.
    /// </summary>
    private async Task Judder(int viewMode, string name)
    {
        var player = _world.Player;
        await Stage(new Vector3(-0.95f, 0.003f, -0.3f), -90, new Vector3(0.25f, 0, 0.0f));
        _world.Companion.TryTeleportTo(new Vector3(1.6f, 0.003f, 1.0f));
        _world.Companion.ResetPhysicsInterpolation();
        _hud.SetViewMode(viewMode);
        for (var i = 0; i < 40; i++) await NextFrame();
        var reference = await Grab();
        var headPoint = ActiveCamera.UnprojectPosition(player.GlobalPosition + Vector3.Up * 0.081f);
        var headColour = reference.GetPixel((int)headPoint.X, (int)headPoint.Y);
        player.SetControlInput(new Vector2(0, 1), sprint: true);
        for (var i = 0; i < 30; i++) await NextFrame();
        var xs = new List<double>();
        var ys = new List<double>();
        var times = new List<double>();
        for (var i = 0; i < 70 && player.GlobalPosition.X < 0.9f; i++)
        {
            var image = await Grab();
            var expected = ActiveCamera.UnprojectPosition(player.GlobalPosition + Vector3.Up * 0.081f);
            double sx = 0, sy = 0;
            var n = 0;
            for (var y = (int)expected.Y - 50; y <= (int)expected.Y + 50; y++)
                for (var x = (int)expected.X - 50; x <= (int)expected.X + 50; x++)
                {
                    if (x < 0 || y < 0 || x >= image.GetWidth() || y >= image.GetHeight()) continue;
                    if (Distance(image.GetPixel(x, y), headColour) > 0.07) continue;
                    sx += x; sy += y; n++;
                }
            if (n < 30) continue;
            xs.Add(sx / n);
            ys.Add(sy / n);
            times.Add(Time.GetTicksUsec() / 1000.0);
        }
        player.SetControlInput(Vector2.Zero);
        if (xs.Count < 20) throw new InvalidOperationException($"{name}: the head was found in only {xs.Count} frames");
        // Scatter of each axis about its own straight line over time (least squares), in pixels, and the frame rate rendered.
        double Scatter(List<double> v)
        {
            var count = v.Count;
            var mt = times.Average();
            var mv = v.Average();
            var slope = Enumerable.Range(0, count).Sum(i => (times[i] - mt) * (v[i] - mv)) / Enumerable.Range(0, count).Sum(i => (times[i] - mt) * (times[i] - mt));
            return Math.Sqrt(Enumerable.Range(0, count).Average(i => Math.Pow(v[i] - (mv + slope * (times[i] - mt)), 2)));
        }
        var seconds = (times[^1] - times[0]) / 1000.0;
        var record = new Dictionary<string, object>
        {
            ["scenario"] = name, ["subject"] = "head_centroid", ["samples"] = xs.Count,
            ["scatter_x_px"] = Math.Round(Scatter(xs), 3), ["scatter_y_px"] = Math.Round(Scatter(ys), 3),
            ["rendered_fps"] = Math.Round((xs.Count - 1) / seconds, 1),
        };
        _records.Add(record);
        GD.Print($"PLAY_CAPTURE {name} sharpness scatter_x_px={record["scatter_x_px"]} scatter_y_px={record["scatter_y_px"]} rendered_fps={record["rendered_fps"]} samples={xs.Count}");
        for (var i = 0; i < 30; i++) await NextFrame();
        _hud.SetViewMode(1);
    }

    /// <summary>
    /// How steadily the running player and the room move across the screen, from the engine's own state, so no capture slows
    /// the frames: every rendered frame the screen positions of the player (F3: the camera follows it, so it should sit still
    /// on screen) and of a fixed point on the floor ahead (F2: the camera rides on the player, so the floor should stream by
    /// smoothly), projected through the transforms the renderer draws (interpolated ones when physics interpolation is on).
    /// The scatter is the RMS distance in pixels from a smooth (quadratic) path over the run. A body that moves only on
    /// physics ticks while frames come at the display's rate steps unevenly, and the scatter shows it.
    /// </summary>
    private async Task Steady(int viewMode, string name)
    {
        var player = _world.Player;
        await Stage(new Vector3(-0.95f, 0.003f, -0.3f), -90, new Vector3(0.25f, 0, 0.0f));
        _world.Companion.TryTeleportTo(new Vector3(1.6f, 0.003f, 1.0f));
        _world.Companion.ResetPhysicsInterpolation();
        _hud.SetViewMode(viewMode);
        for (var i = 0; i < 40; i++) await NextFrame();
        player.SetControlInput(new Vector2(0, 1), sprint: true);
        for (var i = 0; i < 30; i++) await NextFrame();
        var times = new List<double>();
        var bodyX = new List<double>();
        var bodyY = new List<double>();
        var floorX = new List<double>();
        var floorY = new List<double>();
        var last = Time.GetTicksUsec();
        var frameMs = new List<double>();
        var floorPoint = new Vector3(0.6f, 0f, -0.3f);
        for (var i = 0; i < 600 && player.GlobalPosition.X < 0.5f; i++)
        {
            await NextFrame();
            var now = Time.GetTicksUsec();
            frameMs.Add((now - last) / 1000.0);
            last = now;
            var camera = ActiveCamera;
            var view = camera.GetGlobalTransformInterpolated().AffineInverse();
            var size = GetViewport().GetVisibleRect().Size;
            var focal = size.Y * 0.5f / Mathf.Tan(Mathf.DegToRad(camera.Fov) * 0.5f);
            Vector2 Project(Vector3 point)
            {
                var local = view * point;
                return new Vector2(size.X * 0.5f + focal * local.X / -local.Z, size.Y * 0.5f - focal * local.Y / -local.Z);
            }
            var body = Project(player.GetGlobalTransformInterpolated().Origin + Vector3.Up * 0.05f);
            var floor = Project(floorPoint);
            times.Add(now / 1000.0);
            bodyX.Add(body.X); bodyY.Add(body.Y); floorX.Add(floor.X); floorY.Add(floor.Y);
        }
        player.SetControlInput(Vector2.Zero);
        var seconds = (times[^1] - times[0]) / 1000.0;
        // The frames the player was in view for the whole run only: the body at least 5 px on screen.
        var record = new Dictionary<string, object>
        {
            ["scenario"] = name, ["subject"] = "screen_path", ["samples"] = times.Count, ["rendered_fps"] = Math.Round((times.Count - 1) / seconds, 1),
            ["frame_ms_p95"] = Math.Round(Percentile(frameMs, 0.95), 2),
            ["body_scatter_x_px"] = Math.Round(PathScatter(times, bodyX), 3), ["body_scatter_y_px"] = Math.Round(PathScatter(times, bodyY), 3),
            ["floor_scatter_x_px"] = Math.Round(PathScatter(times, floorX), 3), ["floor_scatter_y_px"] = Math.Round(PathScatter(times, floorY), 3),
            ["floor_step_px_p95"] = Math.Round(StepP95(floorX, floorY), 2),
        };
        _records.Add(record);
        GD.Print($"PLAY_CAPTURE {name} sharpness body_scatter_px={record["body_scatter_x_px"]},{record["body_scatter_y_px"]} floor_scatter_px={record["floor_scatter_x_px"]},{record["floor_scatter_y_px"]} floor_step_px_p95={record["floor_step_px_p95"]} rendered_fps={record["rendered_fps"]} samples={times.Count}");
        for (var i = 0; i < 30; i++) await NextFrame();
        _hud.SetViewMode(1);
    }

    /// <summary>RMS distance of a path's samples from the best quadratic in time (least squares).</summary>
    private static double PathScatter(List<double> t, List<double> v)
    {
        var n = t.Count;
        var t0 = t[0];
        var x = t.Select(value => (value - t0) / 1000.0).ToArray();
        // Normal equations for v = a + b x + c x^2.
        var s = new double[5];
        var r = new double[3];
        for (var i = 0; i < n; i++)
        {
            var p = 1.0;
            for (var k = 0; k < 5; k++) { s[k] += p; if (k < 3) r[k] += p * v[i]; p *= x[i]; }
        }
        var m = new double[3, 4];
        for (var i = 0; i < 3; i++) { for (var j = 0; j < 3; j++) m[i, j] = s[i + j]; m[i, 3] = r[i]; }
        for (var c = 0; c < 3; c++)
        {
            var pivot = c;
            for (var i = c + 1; i < 3; i++) if (Math.Abs(m[i, c]) > Math.Abs(m[pivot, c])) pivot = i;
            for (var j = 0; j < 4; j++) (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);
            for (var i = 0; i < 3; i++)
            {
                if (i == c) continue;
                var f = m[i, c] / m[c, c];
                for (var j = c; j < 4; j++) m[i, j] -= f * m[c, j];
            }
        }
        double a = m[0, 3] / m[0, 0], b = m[1, 3] / m[1, 1], cc = m[2, 3] / m[2, 2];
        return Math.Sqrt(Enumerable.Range(0, n).Average(i => Math.Pow(v[i] - (a + b * x[i] + cc * x[i] * x[i]), 2)));
    }

    /// <summary>The 95th percentile of the distance a point moves across the screen between consecutive frames.</summary>
    private static double StepP95(List<double> xs, List<double> ys)
    {
        var steps = new List<double>();
        for (var i = 1; i < xs.Count; i++) steps.Add(Math.Sqrt(Math.Pow(xs[i] - xs[i - 1], 2) + Math.Pow(ys[i] - ys[i - 1], 2)));
        return Percentile(steps, 0.95);
    }

    /// <summary>The HUD as the player sees it in F4 with the time and season keys used (needs --hud=true): T to sunset, Shift+T to the June solstice.</summary>
    private async Task HudF4()
    {
        await Stage(new Vector3(0.0f, 0.003f, 0.9f), 0, new Vector3(0.1f, 0, -0.16f));
        _hud.SetViewMode(3);
        for (var i = 0; i < 4; i++) _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.T, Pressed = true });
        _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.T, Pressed = true, ShiftPressed = true });
        _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.T, Pressed = true, ShiftPressed = true });
        for (var i = 0; i < 120; i++) await NextFrame();
        var image = await Grab();
        image.SavePng(System.IO.Path.Combine(_out, $"{_tag}_hud_f4.png"));
        GD.Print($"PLAY_CAPTURE hud_f4 sharpness clock_text=\"{_hud.ClockText()}\"");
        _hud.SetViewMode(1);
        for (var i = 0; i < 5; i++) _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.T, Pressed = true });
        for (var i = 0; i < 3; i++) _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.T, Pressed = true, ShiftPressed = true });
    }

    /// <summary>Frame time and GPU time, uncapped and without vsync, on the F3 view of the avatars standing still.</summary>
    private async Task Timing()
    {
        await Stage(new Vector3(0.0f, 0.003f, 0.9f), 0, new Vector3(0.1f, 0, -0.16f));
        _hud.SetViewMode(2);
        Engine.MaxFps = 0;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var rid = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(rid, true);
        for (var i = 0; i < 120; i++) await NextFrame();
        var wall = new List<double>();
        var gpu = new List<double>();
        var last = Time.GetTicksUsec();
        for (var i = 0; i < 400; i++)
        {
            await NextFrame();
            var now = Time.GetTicksUsec();
            wall.Add((now - last) / 1000.0);
            last = now;
            gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
        }
        _records.Add(new Dictionary<string, object>
        {
            ["scenario"] = "timing", ["frames"] = wall.Count,
            ["frame_ms_mean"] = Math.Round(wall.Average(), 3), ["frame_ms_p50"] = Math.Round(Percentile(wall, 0.5), 3),
            ["frame_ms_p95"] = Math.Round(Percentile(wall, 0.95), 3), ["gpu_ms_mean"] = Math.Round(gpu.Average(), 3),
            ["gpu_ms_p95"] = Math.Round(Percentile(gpu, 0.95), 3),
        });
        Engine.MaxFps = int.Parse(Arg("fps", "0"), CultureInfo.InvariantCulture);
    }

    // ---- measuring ----

    private Rect2I LabelRect(Image image)
    {
        var camera = ActiveCamera;
        var centre = camera.UnprojectPosition(_label.GlobalPosition);
        var depth = Mathf.Max(0.01f, (_label.GlobalPosition - camera.GlobalPosition).Dot(-camera.GlobalBasis.Z));
        var pixelsPerMetre = image.GetHeight() * 0.5f / Mathf.Tan(Mathf.DegToRad(camera.Fov) * 0.5f) / depth;
        var size = _label.GetAabb().Size;
        var half = new Vector2(size.X * pixelsPerMetre * 0.5f + 6, size.Y * pixelsPerMetre * 0.5f + 6);
        return Clip(new Rect2I((int)(centre.X - half.X), (int)(centre.Y - half.Y), (int)(half.X * 2), (int)(half.Y * 2)), image);
    }

    private Rect2I BodyRect(Image image, SmallPlayerController body)
    {
        var camera = ActiveCamera;
        var foot = camera.UnprojectPosition(body.GlobalPosition);
        var head = camera.UnprojectPosition(body.GlobalPosition + Vector3.Up * body.BodyHeightM);
        var tall = Mathf.Max(24f, Mathf.Abs(foot.Y - head.Y));
        var centre = (foot + head) * 0.5f;
        var half = tall * 0.8f;
        return Clip(new Rect2I((int)(centre.X - half), (int)(centre.Y - half), (int)(half * 2), (int)(half * 2)), image);
    }

    /// <summary>
    /// How many pixels a body's silhouette takes to go from the body to the background across its torso (the 90% to 10% width of
    /// the colour step, averaged over both sides and three rows), or null when the body is not clearly in view. A crisp edge is
    /// one or two pixels; temporal anti-aliasing smears a moving body to several.
    /// </summary>
    private double? EdgeSoftnessPx(Image image, SmallPlayerController body)
    {
        var camera = ActiveCamera;
        var radius = body.BodyRadiusM * 0.82f;
        var widths = new List<double>();
        foreach (var height in new[] { 0.022f, 0.032f, 0.042f })
        {
            var middle = body.GlobalPosition + Vector3.Up * height;
            if (camera.IsPositionBehind(middle)) return null;
            var centre = camera.UnprojectPosition(middle);
            var radiusPx = Mathf.Abs(camera.UnprojectPosition(middle + camera.GlobalBasis.X * radius).X - centre.X);
            if (radiusPx < 6f) return null;
            foreach (var side in new[] { -1, 1 })
            {
                var y = (int)Mathf.Round(centre.Y);
                int X(float k) => (int)Mathf.Round(centre.X + side * k * radiusPx);
                if (X(2.3f) < 2 || X(2.3f) > image.GetWidth() - 3 || y < 2 || y > image.GetHeight() - 3) return null;
                // The body's colour just inside its edge and the background's just outside it, each over a few pixels.
                var bodyColour = MeanColour(image, X(0.35f), X(0.65f), y);
                var background = MeanColour(image, X(1.7f), X(2.3f), y);
                var span = Distance(bodyColour, background);
                if (span < 0.06) continue;
                // 1 in the body, 0 in the background, along the row from inside the body to outside it.
                var profile = new List<double>();
                for (var x = X(0.65f); side > 0 ? x <= X(1.7f) : x >= X(1.7f); x += side)
                    profile.Add(Math.Clamp(Distance(image.GetPixel(x, y), background) / span, 0.0, 1.0));
                var last = profile.FindLastIndex(v => v >= 0.8);
                if (last < 0) continue;
                var to = profile.FindIndex(last, v => v <= 0.2);
                if (to < 0) continue;
                if (Arg("debug-edge", "false") == "true" && _debugEdge++ < 6)
                    GD.Print($"PLAY_CAPTURE_DEBUG side={side} radiusPx={radiusPx:0.0} span={span:0.000} last={last} to={to} profile={string.Join(' ', profile.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))}");
                widths.Add(to - last);
            }
        }
        return widths.Count >= 3 ? widths.Average() : null;
    }

    private static Color MeanColour(Image image, int x0, int x1, int y)
    {
        float r = 0, g = 0, b = 0;
        var n = 0;
        for (var x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        {
            var c = image.GetPixel(x, y);
            r += c.R; g += c.G; b += c.B; n++;
        }
        return new Color(r / n, g / n, b / n);
    }

    private static double Distance(Color a, Color b) => Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

    private static Rect2I Clip(Rect2I rect, Image image) => rect.Intersection(new Rect2I(1, 1, image.GetWidth() - 2, image.GetHeight() - 2));

    private static Image Crop(Image image, Rect2I rect, int scale)
    {
        var crop = image.GetRegion(rect);
        crop.Resize(crop.GetWidth() * scale, crop.GetHeight() * scale, Image.Interpolation.Nearest);
        return crop;
    }

    /// <summary>Mean of the strongest tenth of Sobel gradients of the luma in a rectangle (0 to 1 per step; bigger is crisper).</summary>
    public static (double Sharpness, double Spread) Sharpness(Image image, Rect2I rect)
    {
        var w = rect.Size.X;
        var h = rect.Size.Y;
        if (w < 5 || h < 5) return (0, 0);
        var luma = new double[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = image.GetPixel(rect.Position.X + x, rect.Position.Y + y);
                luma[y * w + x] = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            }
        var gradients = new List<double>((w - 2) * (h - 2));
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                double L(int dx, int dy) => luma[(y + dy) * w + x + dx];
                var gx = (L(1, -1) + 2 * L(1, 0) + L(1, 1)) - (L(-1, -1) + 2 * L(-1, 0) + L(-1, 1));
                var gy = (L(-1, 1) + 2 * L(0, 1) + L(1, 1)) - (L(-1, -1) + 2 * L(0, -1) + L(1, -1));
                gradients.Add(Math.Sqrt(gx * gx + gy * gy) / 4.0);
            }
        gradients.Sort();
        var top = gradients.Skip(gradients.Count * 9 / 10).Average();
        var sorted = luma.OrderBy(v => v).ToArray();
        return (top, sorted[sorted.Length * 95 / 100] - sorted[sorted.Length * 5 / 100]);
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private Dictionary<string, object> AddRecord(string scenario, string subject, List<double> sharpness, string? frame, string? crop, bool edge = false)
    {
        var record = new Dictionary<string, object>
        {
            ["scenario"] = scenario, ["subject"] = subject, ["samples"] = sharpness.Count,
            [edge ? "edge_px_mean" : "sharpness_mean"] = Math.Round(sharpness.Average(), 5),
            [edge ? "edge_px_min" : "sharpness_min"] = Math.Round(sharpness.Min(), 5),
            [edge ? "edge_px_max" : "sharpness_max"] = Math.Round(sharpness.Max(), 5),
        };
        if (frame != null) record["frame"] = frame;
        if (crop != null) record["crop"] = crop;
        _records.Add(record);
        GD.Print(edge
            ? $"PLAY_CAPTURE {scenario} sharpness edge_px mean={record["edge_px_mean"]} min={record["edge_px_min"]} max={record["edge_px_max"]} samples={sharpness.Count}"
            : $"PLAY_CAPTURE {scenario} sharpness mean={record["sharpness_mean"]} min={record["sharpness_min"]} max={record["sharpness_max"]} samples={sharpness.Count}");
        return record;
    }

    private void WriteReport()
    {
        var window = GetViewport();
        var report = new Dictionary<string, object?>
        {
            ["tag"] = _tag,
            ["captured_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["engine"] = Engine.GetVersionInfo()["string"].AsString(),
            ["adapter"] = RenderingServer.GetVideoAdapterName(),
            ["rendering_method"] = RenderingServer.GetCurrentRenderingMethod(),
            ["window"] = new[] { window.GetVisibleRect().Size.X, window.GetVisibleRect().Size.Y },
            ["monitor_refresh_hz"] = Math.Round(DisplayServer.ScreenGetRefreshRate(), 2),
            ["fps_cap"] = Arg("fps", "0"), ["vsync"] = Arg("vsync", "false"),
            ["use_taa"] = window.UseTaa, ["msaa_3d"] = window.Msaa3D.ToString(), ["screen_space_aa"] = window.ScreenSpaceAA.ToString(), ["scaling_3d_mode"] = window.Scaling3DMode.ToString(),
            ["physics_interpolation"] = GetTree().PhysicsInterpolation,
            ["physics_ticks_per_second"] = Engine.PhysicsTicksPerSecond,
            ["clock"] = _world.Look.ClockNote, ["hour"] = Arg("hour", "17.5"), ["date"] = Arg("date", "2026-10-06"),
            ["records"] = _records,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(_out, $"{_tag}.json"), new UTF8Encoding(false).GetBytes(json.Replace("\r\n", "\n") + "\n"));
    }
}
