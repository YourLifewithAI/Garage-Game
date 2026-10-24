using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native;

namespace GarageGame.Tests.Look;

/// <summary>
/// GPU probes of the capture harness (--probe=NAME): measurements that need a real renderer and are not review captures.
/// They write probe_NAME.json beside the captures and use only RoomWorld's public surface and the look through Godot's
/// property and method calls, so the same file runs against an older commit (tools/look/capture-look.ps1 -Baseline) and
/// before and after evidence comes from the same code.
///
///   rooms           the shapes captured rooms take: the contract's garage example, a room with a window and no sun hint, a
///                   single-sided wall, three windows: frame brightness, the sun, where the lights stand
///   window          the window seen from inside, by day and by night: the brightness of the pane
///   free-viewport   freeing a viewport that used the grain and vignette effect, and the look itself, while others render
///   shimmer         floorboards in motion under no anti-aliasing, FXAA, TAA and 4x MSAA: how much of the pattern flickers
/// </summary>
public partial class LookCaptureHarness
{
    private async Task RunProbe(string name, JsonElement root, string outDir)
    {
        var resolution = new Vector2I(root.GetProperty("resolution")[0].GetInt32(), root.GetProperty("resolution")[1].GetInt32());
        Engine.MaxFps = 0;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var result = new Dictionary<string, object?> { ["probe"] = name, ["commit"] = Arg("commit", ""), ["captured_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
        switch (name)
        {
            case "rooms": result["rooms"] = await ProbeRooms(); break;
            case "window": result["window"] = await ProbeWindow(outDir); break;
            case "free-viewport": result["steps"] = await ProbeFreeViewport(outDir, resolution); break;
            case "shimmer": result["shimmer"] = await ProbeShimmer(outDir); break;
            case "island-memory": result["steps"] = await ProbeIslandMemory(); break;
            default: throw new ArgumentException("unknown probe " + name);
        }
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"probe_{name}.json"), new UTF8Encoding(false).GetBytes(json.Replace("\r\n", "\n") + "\n"));
        GD.Print($"LOOK_PROBE_DONE {name} out={outDir}");
        GetTree().Quit(0);
    }

    // ---------- plumbing ----------

    private async Task<RoomWorld> LoadWorld(string roomDirectory)
    {
        var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        world.RoomDirectory = roomDirectory;
        if (Arg("style", "").Length > 0) world.StylePresetPath = Arg("style", "");
        AddChild(world);
        for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await NextFrame();
        if (!world.WorldReady) throw new InvalidOperationException($"room {roomDirectory} did not load: {world.LoadError}");
        foreach (var layer in world.FindChildren("*", "CanvasLayer", true, false).OfType<CanvasLayer>()) layer.Visible = false;
        foreach (var label in world.FindChildren("*", "Label3D", true, false).OfType<Label3D>()) label.Visible = false;
        world.Player.ReadKeyboard = false;
        world.Player.SetInputEnabled(false);
        world.Companion.Stay();
        _world = world;
        _look = world.Look;
        return world;
    }

    private async Task UnloadWorld()
    {
        _world.QueueFree();
        await NextFrame();
        await NextFrame();
    }

    private void FreeTarget()
    {
        _target?.QueueFree();
        _target = null;
        GetNodeOrNull("Preview")?.QueueFree();
        GetViewport().Disable3D = false;
    }

    private static double MeanLuma(Image image, Rect2I? region = null, int step = 3)
    {
        var rect = region ?? new Rect2I(0, 0, image.GetWidth(), image.GetHeight());
        rect = rect.Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        double sum = 0;
        var count = 0;
        for (var y = rect.Position.Y; y < rect.End.Y; y += step)
            for (var x = rect.Position.X; x < rect.End.X; x += step)
            {
                sum += Luma(image.GetPixel(x, y));
                count++;
            }
        return count == 0 ? 0 : sum / count;
    }

    private async Task<Image> Shot(Vector3 position, Vector3 lookAt, float fov, Vector3 focus, int frames = 50)
    {
        _camera.GlobalPosition = position;
        _camera.LookAt(lookAt, Mathf.Abs((lookAt - position).Normalized().Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up);
        _camera.Fov = fov;
        _camera.Near = 0.005f;
        _camera.Far = 50f;
        _camera.CullMask = 0xFFFFFu & ~HiddenBodyLayer;
        if (_look != null && _look.HasMethod("FrameCamera")) _look.Call("FrameCamera", _camera, focus);
        for (var i = 0; i < frames; i++) await NextFrame();
        return Grab();
    }

    private static Dictionary<string, object?> Lights(RoomWorld world)
    {
        var look = world.Look;
        var info = new Dictionary<string, object?>();
        info["sun_scale"] = Math.Round(Convert.ToDouble(look.GetType().GetProperty("SunScale")?.GetValue(look) ?? 0f), 3);
        info["key_visible"] = ((Light3D)look.Get("Key").AsGodotObject()).Visible;
        info["key_energy"] = Math.Round(((Light3D)look.Get("Key").AsGodotObject()).LightEnergy, 3);
        var bounds = world.Room.Bounds.Grow(0.001f);
        var fills = new List<Dictionary<string, object?>>();
        foreach (var fill in (look.GetType().GetProperty("SkyFills")?.GetValue(look) as IEnumerable<SpotLight3D>) ?? Array.Empty<SpotLight3D>())
            fills.Add(new Dictionary<string, object?>
            {
                ["id"] = fill.GetMeta("light_hint_id").AsString(), ["shadow"] = fill.ShadowEnabled, ["inside_room"] = bounds.HasPoint(fill.GlobalPosition),
                ["position"] = new[] { Math.Round(fill.GlobalPosition.X, 2), Math.Round(fill.GlobalPosition.Y, 2), Math.Round(fill.GlobalPosition.Z, 2) },
            });
        info["sky_fills"] = fills;
        info["sky_fills_standing_outside_the_room"] = fills.Count(f => f["inside_room"] is false);
        return info;
    }

    // ---------- rooms ----------

    private async Task<List<Dictionary<string, object?>>> ProbeRooms()
    {
        var resolution = new Vector2I(960, 540);
        var results = new List<Dictionary<string, object?>>();
        // Each case is a moment the sun reaches (or should be held back from) the floor: the review moment, 17:00 on 15 April
        // (the sun 18 degrees up in the west), or noon in July.
        var fixtures = new List<(string Label, string Directory, double Hour, int Day, string Note)>
        {
            ("test_room", RoomWorld.DefaultRoom, 17, 105, "the shipped room at the review moment: the sun through the west window onto the rug"),
            ("test_room_noon", RoomWorld.DefaultRoom, 12, 196, "the shipped room at noon in July, the sun nearly overhead: no direct sun can reach the floor, so every bit of gain is light that got through the shell"),
            ("window_no_sun_hint", LookFixtureRooms.WindowWithoutSunHint(), 17, 105, "the test room with its sun hint taken away: a window is still a way in for the sun (M1)"),
            ("captured_like", LookFixtureRooms.CapturedLike(), 17, 105, "no sun hint and the window only an opening in a solid wall, a captured room's shape: with no hole in the wall the sun stays out (M1 needs the builder to cut the opening too)"),
            ("single_sided_wall", LookFixtureRooms.SingleSidedWestWall(), 17, 105, "the west wall one single-sided quad and no window, the sun outside: it must stay out (M4)"),
            ("three_windows", LookFixtureRooms.ThreeWindows(), 17, 105, "three windows against a budget of three shadowed lights (M3)"),
        };
        var garage = Arg("garage", "");
        if (garage.Length > 0)
            fixtures.Add(("garage_example", garage, 12, 196, "the contract's captured-room example at noon in July (review M1: 0.06 against 0.58)"));
        foreach (var f in fixtures)
        {
            var world = await LoadWorld(f.Directory);
            var entry = new Dictionary<string, object?> { ["room"] = f.Label, ["note"] = f.Note };
            SetClock(f.Hour, f.Day);
            BuildTarget(resolution);
            for (var i = 0; i < 20; i++) await NextFrame();
            var bounds = world.Room.Bounds;
            var centre = bounds.GetCenter();
            // Straight down on the floor from just under the ceiling, the whole floor in frame.
            var height = bounds.End.Y - 0.12f;
            var half = Mathf.Max(bounds.Size.Z, bounds.Size.X * 9f / 16f) * 1.05f * 0.5f;
            var fov = Mathf.RadToDeg(2f * Mathf.Atan(half / height));
            var floorFocus = new Vector3(centre.X, 0f, centre.Z);
            var key = (Light3D)_look!.Get("Key").AsGodotObject();
            var sunWasVisible = key.Visible;
            key.Visible = sunWasVisible;
            var on = await Shot(new Vector3(centre.X, height, centre.Z), floorFocus, fov, floorFocus, 40);
            var space = _world.GetWorld3D().DirectSpaceState;
            var toSun = key.GlobalBasis.Z.Normalized();
            var excluded = new Godot.Collections.Array<Rid>();
            foreach (var body in world.Built.GetNode("Objects").GetChildren().OfType<CollisionObject3D>()) excluded.Add(body.GetRid());
            excluded.Add(world.Player.GetRid());
            excluded.Add(world.Companion.GetRid());
            var open = new List<Vector2>();
            var blocked = new List<Vector2>();
            for (var x = bounds.Position.X + 0.1f; x < bounds.End.X - 0.1f; x += 0.1f)
                for (var z = bounds.Position.Z + 0.1f; z < bounds.End.Z - 0.1f; z += 0.1f)
                {
                    var point = new Vector3(x, 0.002f, z);
                    var query = PhysicsRayQueryParameters3D.Create(point, point + toSun * 20f);
                    query.Exclude = excluded;
                    var hit = space.IntersectRay(query);
                    var screen = _camera.UnprojectPosition(point);
                    if (screen.X < 8 || screen.Y < 8 || screen.X > resolution.X - 8 || screen.Y > resolution.Y - 8) continue;
                    if (hit.Count == 0) open.Add(screen);
                    else if (hit["collider"].AsGodotObject() is Node node && node.HasMeta("surface_role")) blocked.Add(screen);
                }
            key.Visible = false;
            for (var i = 0; i < 30; i++) await NextFrame();
            var off = Grab();
            key.Visible = sunWasVisible;
            double Patch(Image image, Vector2 at)
            {
                double sum = 0;
                for (var y = -2; y <= 2; y++)
                    for (var x = -2; x <= 2; x++)
                        sum += Luma(image.GetPixel(Mathf.Clamp((int)at.X + x, 0, image.GetWidth() - 1), Mathf.Clamp((int)at.Y + y, 0, image.GetHeight() - 1)));
                return sum / 25.0;
            }
            double MedianGain(List<Vector2> points)
            {
                if (points.Count == 0) return 0;
                var gains = points.Select(p => Patch(on, p) - Patch(off, p)).OrderBy(g => g).ToArray();
                return gains[gains.Length / 2];
            }
            entry["moment"] = $"{f.Hour:0.##} h, day {f.Day}";
            entry["sun_elevation_deg"] = Math.Round(Mathf.RadToDeg(Mathf.Asin(toSun.Y)), 1);
            entry["mean_luma"] = Math.Round(MeanLuma(on), 4);
            entry["mean_luma_without_sun"] = Math.Round(MeanLuma(off), 4);
            entry["lights"] = Lights(world);
            entry["floor_points_open_to_the_sun"] = open.Count;
            entry["floor_points_behind_the_shell"] = blocked.Count;
            entry["gain_where_the_sun_is_open"] = Math.Round(MedianGain(open), 4);
            entry["gain_behind_the_shell"] = Math.Round(MedianGain(blocked), 4);
            on.SavePng(System.IO.Path.Combine(Arg("out", "."), $"probe_room_{f.Label}.png"));
            FreeTarget();
            await UnloadWorld();
            results.Add(entry);
            GD.Print($"LOOK_PROBE rooms {f.Label} mean_luma={entry["mean_luma"]} without_sun={entry["mean_luma_without_sun"]} open={open.Count} gain_open={entry["gain_where_the_sun_is_open"]} behind={blocked.Count} gain_behind={entry["gain_behind_the_shell"]}");
        }
        return results;
    }

    // ---------- the window seen from inside ----------

    private async Task<List<Dictionary<string, object?>>> ProbeWindow(string outDir)
    {
        var world = await LoadWorld(RoomWorld.DefaultRoom);
        BuildTarget(new Vector2I(1280, 720));
        for (var i = 0; i < 20; i++) await NextFrame();
        var results = new List<Dictionary<string, object?>>();
        // The opening: x = -2, 0.5 to 2.0 m up, z from -0.3 to 0.9 (the test room's west window).
        var corners = new[] { new Vector3(-2f, 0.5f, -0.3f), new Vector3(-2f, 0.5f, 0.9f), new Vector3(-2f, 2.0f, -0.3f), new Vector3(-2f, 2.0f, 0.9f) };
        foreach (var (label, hour, day) in new[] { ("noon_july", 12.0, 196), ("golden_hour_april", 17.4, 105), ("overcast_winter_noon", 12.0, 15), ("night", 2.0, 279) })
        {
            SetClock(hour, day);
            var image = await Shot(new Vector3(1.2f, 1.1f, 0.3f), new Vector3(-2f, 1.25f, 0.3f), 62f, new Vector3(-1.0f, 1.0f, 0.3f), 40);
            var points = corners.Select(c => _camera.UnprojectPosition(c)).ToArray();
            var inset = 0.12f;
            var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X); var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
            var rect = new Rect2I((int)(minX + (maxX - minX) * inset), (int)(minY + (maxY - minY) * inset), (int)((maxX - minX) * (1 - 2 * inset)), (int)((maxY - minY) * (1 - 2 * inset)));
            var sum = new Color(0, 0, 0);
            var count = 0;
            for (var y = rect.Position.Y; y < rect.End.Y; y += 4)
                for (var x = rect.Position.X; x < rect.End.X; x += 4)
                {
                    var c = image.GetPixel(Mathf.Clamp(x, 0, image.GetWidth() - 1), Mathf.Clamp(y, 0, image.GetHeight() - 1));
                    sum += c;
                    count++;
                }
            var mean = new Color(sum.R / count, sum.G / count, sum.B / count);
            results.Add(new Dictionary<string, object?>
            {
                ["moment"] = label, ["pane_mean_luma"] = Math.Round(Luma(mean), 4), ["pane_mean_rgb"] = new[] { Math.Round(mean.R, 3), Math.Round(mean.G, 3), Math.Round(mean.B, 3) },
                ["pane_hex"] = "#" + mean.ToHtml(false), ["frame_mean_luma"] = Math.Round(MeanLuma(image), 4),
            });
            image.SavePng(System.IO.Path.Combine(outDir, $"probe_window_{label}.png"));
            GD.Print($"LOOK_PROBE window {label} pane_luma={results[^1]["pane_mean_luma"]} #{mean.ToHtml(false)}");
        }
        return results;
    }

    // ---------- freeing a viewport that used the grain and vignette effect (review M8) ----------

    private async Task<List<string>> ProbeFreeViewport(string outDir, Vector2I resolution)
    {
        var steps = new List<string>();
        var progress = System.IO.Path.Combine(outDir, "probe_free-viewport.progress.txt");
        System.IO.File.WriteAllText(progress, "");
        void Step(string text)
        {
            steps.Add(text);
            System.IO.File.AppendAllText(progress, text + "\n");
            GD.Print("LOOK_PROBE free-viewport " + text);
        }
        await LoadWorld(RoomWorld.DefaultRoom);
        BuildTarget(new Vector2I(960, 540));
        await Shot(new Vector3(-1.84f, 2.26f, 1.36f), new Vector3(0.15f, 0.05f, 0.25f), 60f, new Vector3(0f, 0.06f, 0.6f), 30);
        Step("main review target renders with the effect");

        SubViewport Extra(string name, Vector2I size)
        {
            var viewport = new SubViewport { Name = name, Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            AddChild(viewport);
            var camera = new Camera3D { Name = name + "Camera", Fov = 60f };
            viewport.AddChild(camera);
            camera.GlobalPosition = new Vector3(1.5f, 1.4f, 1.2f);
            camera.LookAt(new Vector3(0f, 0.2f, 0.4f));
            camera.Current = true;
            return viewport;
        }

        // 1. A second viewport sharing the world is freed while the first keeps rendering.
        var second = Extra("SecondTarget", new Vector2I(640, 360));
        for (var i = 0; i < 30; i++) await NextFrame();
        Step("a second viewport rendered with the effect");
        second.Free();
        for (var i = 0; i < 30; i++) await NextFrame();
        Step("the second viewport was freed (immediately) and the first kept rendering");

        // 2. Several viewports freed through the queue, in the middle of rendering.
        var several = Enumerable.Range(0, 4).Select(i => Extra("Many" + i, new Vector2I(480 + 32 * i, 270))).ToArray();
        for (var i = 0; i < 20; i++) await NextFrame();
        foreach (var viewport in several) viewport.QueueFree();
        for (var i = 0; i < 20; i++) await NextFrame();
        Step("four viewports were freed through the queue");

        // 3. A new viewport after all that: the effect must still be usable.
        var third = Extra("ThirdTarget", new Vector2I(800, 450));
        for (var i = 0; i < 30; i++) await NextFrame();
        var post = _look!.Get("Post").AsGodotObject();
        Step($"a new viewport rendered afterwards (post effect ran: {post?.GetType().GetProperty("Ran")?.GetValue(post)}, error '{post?.GetType().GetProperty("Error")?.GetValue(post)}')");

        // 4. The main target is freed while the third keeps rendering.
        FreeTarget();
        for (var i = 0; i < 30; i++) await NextFrame();
        Step("the review target (the one that measured the effect) was freed while another viewport rendered");

        // 5. Resize and free in the same frame, repeatedly.
        for (var round = 0; round < 12; round++)
        {
            var burst = Extra("Burst" + round, new Vector2I(320 + round * 16, 180));
            await NextFrame();
            if (round % 2 == 0) burst.Size = new Vector2I(400, 225);
            burst.QueueFree();
            await NextFrame();
        }
        Step("twelve viewports were created, resized and freed within a frame or two");

        // 6. The look is taken out of the tree while a viewport still renders with its compositor.
        third.Size = new Vector2I(512, 288);
        await NextFrame();
        _look!.GetParent().RemoveChild(_look);
        for (var i = 0; i < 20; i++) await NextFrame();
        Step("the look was removed from the tree while the third viewport rendered");
        _look.Free();
        for (var i = 0; i < 20; i++) await NextFrame();
        Step("the look was freed");
        // The garbage collector finalizes what the look dropped (its compositor effect) on its own thread, as it does in a
        // running game after a room unloads: freeing GPU objects from there is what used to fail.
        _look = null;
        for (var round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await NextFrame();
        }
        Step("the garbage collector finalized what the look dropped");
        third.QueueFree();
        for (var i = 0; i < 10; i++) await NextFrame();
        Step("the last viewport was freed");
        await UnloadWorld();
        Step("the room was unloaded");
        return steps;
    }

    // ---------- floorboards in motion (shimmer under FXAA) ----------

    private async Task<List<Dictionary<string, object?>>> ProbeShimmer(string outDir)
    {
        await LoadWorld(RoomWorld.DefaultRoom);
        var results = new List<Dictionary<string, object?>>();
        SetClock(17.0, 105);
        // The grain is fixed to the screen (so anti-aliasing keeps it) and the floor moves under it; the measurement is of the
        // floor's own flicker, so the effect is off.
        if (_look?.Get("Post").AsGodotObject() is CompositorEffect post) post.Enabled = false;
        const int poses = 60;
        const int patch = 200;
        var origin = new Vector3(-0.4f, 0.55f, 1.1f);
        var focus = new Vector3(0f, 0.0f, 0.2f);

        // One pass over the poses: a camera gliding sideways over the floor, the planks in the crisp band. Returns the luma of a
        // patch (at the 1920 x 1080 scale) per pose; a supersampled pass is averaged down to that scale.
        async Task<List<float[]>> Pass(Vector2I size, Viewport.ScreenSpaceAAEnum aa, bool taa, Viewport.Msaa msaa, double glide, int scale, string label)
        {
            BuildTarget(size);
            _target!.ScreenSpaceAA = aa;
            _target.UseTaa = taa;
            _target.Msaa3D = msaa;
            for (var i = 0; i < 30; i++) await NextFrame();
            var frames = new List<float[]>();
            Vector2I? centre = null;
            for (var step = 0; step < poses; step++)
            {
                _camera.GlobalPosition = origin + new Vector3((float)(glide * step), 0f, 0f);
                _camera.LookAt(focus + new Vector3((float)(glide * step), 0f, 0f), Vector3.Up);
                _camera.Fov = 50f;
                _camera.Near = 0.005f;
                _camera.Far = 50f;
                if (step == 0 && _look != null && _look.HasMethod("FrameCamera")) _look.Call("FrameCamera", _camera, focus);
                await NextFrame();
                await NextFrame();
                var image = Grab();
                var projected = _camera.UnprojectPosition(focus);
                centre ??= new Vector2I((int)(projected.X / scale), (int)(projected.Y / scale));
                var data = new float[patch * patch];
                for (var y = 0; y < patch; y++)
                    for (var x = 0; x < patch; x++)
                    {
                        double sum = 0;
                        for (var sy = 0; sy < scale; sy++)
                            for (var sx = 0; sx < scale; sx++)
                                sum += Luma(image.GetPixel(Mathf.Clamp((centre.Value.X - patch / 2 + x) * scale + sx, 0, image.GetWidth() - 1), Mathf.Clamp((centre.Value.Y - patch / 2 + y) * scale + sy, 0, image.GetHeight() - 1)));
                        data[y * patch + x] = (float)(sum / (scale * scale));
                    }
                frames.Add(data);
                if (step == 0) image.SavePng(System.IO.Path.Combine(outDir, $"probe_shimmer_{label}.png"));
            }
            FreeTarget();
            await NextFrame();
            await NextFrame();
            return frames;
        }

        // The reference: the same poses rendered at four times the pixels with 4x MSAA, averaged down.
        var reference = await Pass(new Vector2I(3840, 2160), Viewport.ScreenSpaceAAEnum.Disabled, false, Viewport.Msaa.Msaa4X, 0.0006, 2, "reference");
        var configurations = new (string Label, string File, Viewport.ScreenSpaceAAEnum Aa, bool Taa, Viewport.Msaa Msaa, double Glide)[]
        {
            ("no anti-aliasing", "none", Viewport.ScreenSpaceAAEnum.Disabled, false, Viewport.Msaa.Disabled, 0.0006),
            ("FXAA (the project's setting)", "fxaa", Viewport.ScreenSpaceAAEnum.Fxaa, false, Viewport.Msaa.Disabled, 0.0006),
            ("TAA (the old setting)", "taa", Viewport.ScreenSpaceAAEnum.Disabled, true, Viewport.Msaa.Disabled, 0.0006),
            ("MSAA 4x", "msaa4", Viewport.ScreenSpaceAAEnum.Disabled, false, Viewport.Msaa.Msaa4X, 0.0006),
        };
        foreach (var (label, file, aa, taa, msaa, glide) in configurations)
        {
            var frames = await Pass(new Vector2I(1920, 1080), aa, taa, msaa, glide, 1, file);
            var (flicker, bias, contrast) = ShimmerScore(frames, reference);
            results.Add(new Dictionary<string, object?>
            {
                ["configuration"] = label, ["poses"] = frames.Count, ["glide_m_per_pose"] = glide, ["flicker"] = Math.Round(flicker, 5), ["flicker_over_contrast"] = Math.Round(flicker / Math.Max(contrast, 1e-6), 4),
                ["static_error"] = Math.Round(bias, 5), ["patch_contrast_std"] = Math.Round(contrast, 4),
            });
            GD.Print($"LOOK_PROBE shimmer {label}: flicker {flicker:0.00000} (over contrast {flicker / Math.Max(contrast, 1e-6):0.0000}), static error {bias:0.00000}");
        }
        // The same pose rendered again must give the same pixels: the measurement has no noise of its own.
        var again = await Pass(new Vector2I(1920, 1080), Viewport.ScreenSpaceAAEnum.Fxaa, false, Viewport.Msaa.Disabled, 0.0006, 1, "fxaa_again");
        var firstFxaa = await Pass(new Vector2I(1920, 1080), Viewport.ScreenSpaceAAEnum.Fxaa, false, Viewport.Msaa.Disabled, 0.0006, 1, "fxaa_repeat");
        double repeat = 0;
        for (var f = 0; f < again.Count; f++) repeat = Math.Max(repeat, again[f].Zip(firstFxaa[f]).Max(p => Math.Abs(p.First - p.Second)));
        results.Add(new Dictionary<string, object?> { ["configuration"] = "repeatability: the largest luma difference between two renders of the same FXAA pose", ["max_difference"] = Math.Round(repeat, 5) });
        GD.Print($"LOOK_PROBE shimmer repeatability: two renders of the same poses differ by at most {repeat:0.00000}");
        return results;
    }

    /// <summary>
    /// How much a pattern flickers as the camera glides, against a supersampled reference of the same poses. At every pixel
    /// of the patch the error is the frame minus the reference; its change from pose to pose is the flicker (a steady blur or
    /// bias is not flicker, a jagged edge that crawls is). Returns the mean over the patch of the error's standard deviation
    /// across poses, the mean steady error, and the patch's own contrast for scale.
    /// </summary>
    private static (double Flicker, double Bias, double Contrast) ShimmerScore(List<float[]> frames, List<float[]> reference)
    {
        var size = frames[0].Length;
        double flicker = 0, bias = 0;
        for (var i = 0; i < size; i++)
        {
            double sum = 0, squares = 0;
            for (var f = 0; f < frames.Count; f++)
            {
                var error = frames[f][i] - reference[f][i];
                sum += error;
                squares += error * error;
            }
            var mean = sum / frames.Count;
            flicker += Math.Sqrt(Math.Max(squares / frames.Count - mean * mean, 0));
            bias += Math.Abs(mean);
        }
        var all = reference[0];
        var average = all.Average();
        var contrast = Math.Sqrt(all.Average(v => (v - average) * (v - average)));
        return (flicker / size, bias / size, contrast);
    }
}
