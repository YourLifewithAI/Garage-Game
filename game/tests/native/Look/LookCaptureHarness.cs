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
/// Look review capture harness (Track L, packet L7 groundwork). Boots the room, drives the fixed review
/// cameras from tools/look/review_cameras.json at the review resolution, saves one PNG per camera and
/// measures frame time with vsync off. It renders through an offscreen SubViewport of exactly the review
/// size by default, so the result does not depend on the desktop size; --root-viewport renders in the
/// window instead (borderless, at the screen origin) as a cross-check.
///
/// It only uses RoomWorld's public surface, so the same file also captures older commits. Newer look
/// hooks (review clock, per-camera focus) are called by name when the LookDirector has them.
///
/// Needs a real window on the GPU: under --headless it reports that and exits with code 2.
/// After the cameras it checks the grain and vignette effect in the pixels (the first camera with and without
/// it), checks light from real sources in the pixels (the sun lands only through the window; the night with the
/// lamps off and on), saves close crops of the avatars' feet on the cameras the file names for contact shadows,
/// and writes the look's self-check (renderer fallback, missing GI bake, bake stand-ins left, post effect never
/// ran) to timings.json as look_problems. Any problem prints LOOK_CAPTURE_PROBLEM and exits with code 3, so a
/// capture never passes silently on a fallback renderer.
/// User arguments (after "--"): --cameras=PATH --out=DIR [--label=TEXT] [--warmup=N] [--frames=N]
/// [--only=ID,ID] [--sweep] [--root-viewport] [--commit=TEXT] [--note=TEXT] [--post-check=false]
/// [--light-checks=false] [--style=PATH] [--allow-problems] [--probe=rooms|window|free-viewport|shimmer] [--garage=DIR]
/// [--room=DIR|ID] [--jpg=QUALITY]
///   room  review another room than the test room (a landscape export, a captured room); its cameras file names the cameras
///   jpg   save JPEG files at that quality (1 to 100) instead of PNGs (a landscape set is committed, one picture under 1 MB)
/// A camera entry may also stage the avatars first ("stage": player_m, yaw_deg, companion_m: where they stand, which way the
/// player faces) and may ask for the player's own eye ("eye_view": true: the real eye camera's transform and focus rule).
/// It may mirror any of the HUD's views ("hud_view": 0 eye, 1 shoulder, 2 diorama, 3 isometric; "hud_pitch_deg" tilts the diorama),
/// name the view a fixed camera stands for ("view": eye, shoulder, diorama or isometric, which blurs its own way), and put the
/// focus highlight on a room object moved onto a spot ("focus": object, at_xz_m, camera_offset_m: the camera looks at it from
/// there). Looks without views or the highlight ignore those (the harness finds them by reflection).
/// A camera may set its own hour ("hour", the review clock's date) and light the Gubble's glows before it is framed ("glows": id,
/// on: "companion" for a halo or at_m for a wisp at a point, radius_m, intensity; the look's StartGlow, as the host calls it).
/// A wisp's "at_xz_m" stands it on the ground there instead (a ray down). "spark_s": T casts the glows again T seconds before the
/// picture, to catch the spark from the Gubble in flight. An entry's "kind" may be "fireworks" (StartFireworks: same fields); "bubbles"
/// is the stream of small bubbles the founder's redesign removed (decisions 55 to 60), so it is skipped with a note; "settle_s": T
/// lets the effects run T seconds before the warm-up.
/// </summary>
public partial class LookCaptureHarness : Node
{
    private const uint HiddenBodyLayer = 1u << 19;
    private readonly Dictionary<string, string> _args = new();
    private SubViewport? _target;
    private Camera3D _camera = null!;
    private RoomWorld _world = null!;
    private Node? _look;
    // A camera with "hud_view" copies the live HUD rig (F3 or F4) every frame and takes the focus the look would give it there.
    private RoomHud? _hud;
    private Camera3D? _mirror;
    private readonly List<Dictionary<string, object>> _results = new();
    private readonly List<string> _glows = new();

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
            if (DisplayServer.GetName() == "headless")
            {
                GD.Print("LOOK_CAPTURE: needs a real window on the GPU; --headless renders nothing. Run tools/look/capture-look.ps1.");
                GetTree().Quit(2);
                return;
            }
            var camerasPath = Require("cameras");
            var outDir = Require("out");
            var warmup = int.Parse(Arg("warmup", "120"), CultureInfo.InvariantCulture);
            var frames = int.Parse(Arg("frames", "300"), CultureInfo.InvariantCulture);
            using var document = JsonDocument.Parse(System.IO.File.ReadAllBytes(camerasPath));
            var root = document.RootElement;
            var resolution = new Vector2I(root.GetProperty("resolution")[0].GetInt32(), root.GetProperty("resolution")[1].GetInt32());
            System.IO.Directory.CreateDirectory(outDir);
            // A GPU probe (LookCaptureHarness.Probes.cs) instead of the review captures.
            if (Arg("probe", "").Length > 0)
            {
                await RunProbe(Arg("probe", ""), root, outDir);
                return;
            }

            Engine.MaxFps = 0;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            // A preset variant for tuning (never a review of record): the room's style is replaced by this file.
            if (Arg("style", "").Length > 0) _world.StylePresetPath = Arg("style", "");
            // Another room than the test room: a landscape export or a captured room (an id under the user's rooms, or a folder).
            if (Arg("room", "").Length > 0) _world.RoomDirectory = RoomWorld.ResolveRoom(Arg("room", "").Replace('\\', '/'));
            AddChild(_world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_world.WorldReady && _world.LoadError.Length == 0;) await NextFrame();
            if (!_world.WorldReady) throw new InvalidOperationException("room did not load: " + _world.LoadError);
            _look = _world.Look;
            await SkipIntro();
            AttachPondLife();
            // The fish are surveyed over the first few seconds (the game's way): the captures wait for them, as they wait for the copies.
            if (_world.FindChild("PondLife", true, false) is Node pondLife && pondLife.HasMethod("SurveyNow"))
                for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 60 && !pondLife.Get("Surveyed").AsBool();) await NextFrame();
            // Review captures show the room, not the interface: hide the HUD and the companion's floating name.
            foreach (var layer in _world.FindChildren("*", "CanvasLayer", true, false).OfType<CanvasLayer>()) layer.Visible = false;
            foreach (var label in _world.FindChildren("*", "Label3D", true, false).OfType<Label3D>()) label.Visible = false;
            _world.Player.ReadKeyboard = false;
            _world.Player.SetInputEnabled(false);
            _world.Companion.Stay();
            foreach (var mesh in _world.Player.GetNode("OriginalPrototypeBody").FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
                mesh.Layers = HiddenBodyLayer;

            if (root.TryGetProperty("clock", out var clock))
            {
                SetClock(clock.GetProperty("hour").GetDouble(), DayOfYear(clock.GetProperty("date").GetString()!));
                ReviewLamps(clock);
            }
            BuildTarget(resolution);
            // Let the avatars settle on the floor and the companion come to rest.
            for (var i = 0; i < 45; i++) await NextFrame();
            // An island's copies arrive over its first frames: every camera (and every camera worked out from the island) waits for them.
            var copies = _world.Built.GetNodeOrNull<GarageGame.Native.Room.PlantCopiesView>(GarageGame.Native.Room.PlantCopiesView.ViewName);
            for (var waited = System.Diagnostics.Stopwatch.StartNew(); copies != null && waited.Elapsed.TotalSeconds < 60 && !copies.Complete;) await NextFrame();

            var only = Arg("only", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            foreach (var entry in Entries(root).ToList())
            {
                var id = entry.GetProperty("id").GetString()!;
                if (only.Count > 0 && !only.Contains(id)) continue;
                var ownHour = entry.TryGetProperty("hour", out var hour) && root.TryGetProperty("clock", out var reviewClock)
                    && SetClock(hour.GetDouble(), DayOfYear(reviewClock.GetProperty("date").GetString()!));
                await PrepareStage(entry);
                Frame(entry);
                LightGlows(entry);
                if (entry.TryGetProperty("settle_s", out var settleS))
                {
                    var lit = Time.GetTicksUsec();
                    while ((Time.GetTicksUsec() - lit) / 1e6 < settleS.GetDouble()) await NextFrame();
                }
                for (var i = 0; i < warmup; i++) await NextFrame();
                var timing = await Measure(FlightFrames(entry, frames), entry);
                timing["memory"] = MemoryNow();
                if (entry.TryGetProperty("spark_s", out var sparkS))
                {
                    LightGlows(entry);
                    var cast = Time.GetTicksUsec();
                    while ((Time.GetTicksUsec() - cast) / 1e6 < sparkS.GetDouble()) await NextFrame();
                }
                var image = Grab();
                LightGlows(default);
                if (ownHour && root.TryGetProperty("clock", out var back))
                    SetClock(back.GetProperty("hour").GetDouble(), DayOfYear(back.GetProperty("date").GetString()!));
                var file = Save(image, id, outDir);
                timing["camera"] = id;
                timing["file"] = file;
                timing["image_size"] = new[] { image.GetWidth(), image.GetHeight() };
                if (ContactCameras(root).Contains(id)) timing["contact_crops"] = SaveContactCrops(image, id, outDir);
                _results.Add(timing);
                var memory = (Dictionary<string, object>)timing["memory"];
                GD.Print($"LOOK_CAPTURE camera={id} frame_ms_p50={timing["frame_ms_p50"]} frame_ms_p95={timing["frame_ms_p95"]} frame_ms_max={timing["frame_ms_max"]} gpu_ms_mean={timing["gpu_ms_mean"]} " +
                         $"working_set_mib={memory["working_set_mib"]} peak_mib={memory["peak_working_set_mib"]} video_mib={memory["video_mib"]} spikes={((List<Dictionary<string, object>>)timing["spikes"]).Count} image={image.GetWidth()}x{image.GetHeight()}");
            }
            if (Arg("sweep", "false") == "true") await Sweep(root, outDir);
            if (Arg("light-checks", "true") == "true") _lightChecks = await LightChecks(root, outDir);
            if (Arg("post-check", "true") == "true") _postCheck = await PostEffectCheck(root);
            var problems = LookProblems();
            WriteReport(outDir, camerasPath, resolution, warmup, frames, problems);
            foreach (var problem in problems) GD.Print("LOOK_CAPTURE_PROBLEM " + problem);
            GD.Print($"LOOK_CAPTURE_DONE cameras={_results.Count} out={outDir} problems={problems.Count}");
            // A renderer fallback, a post effect that did nothing or a broken bake must not pass silently as a review capture.
            GetTree().Quit(problems.Count == 0 || Arg("allow-problems", "false") == "true" ? 0 : 3);
        }
        catch (Exception error)
        {
            GD.PushError("Look capture failed: " + error);
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// The fish and the underwater veil (Run 2, Lane L), attached here as RoomWorld will once the integrator wires them (a change
    /// request), with the avatars to dart from. Found by name, so the harness still captures commits from before they existed.
    /// </summary>
    private void AttachPondLife()
    {
        if (_world.FindChild("PondLife", true, false) != null) return;
        var type = Type.GetType("GarageGame.Native.Look.Fauna.PondLife");
        if (type?.GetMethod("Create")?.Invoke(null, new object[] { _world.Room.RoomId, _world.Built }) is not Node3D life) return;
        _world.AddChild(life);
        type.GetMethod("SetAvatars")?.Invoke(life, new object[] { new Node3D?[] { _world.Player, _world.Companion } });
    }

    private string Arg(string name, string fallback) => _args.TryGetValue(name, out var value) ? value : fallback;

    /// <summary>Save a review image as a PNG, or as a JPEG when --jpg=QUALITY says so; returns the file name.</summary>
    private string Save(Image image, string id, string outDir)
    {
        if (Arg("jpg", "").Length > 0)
        {
            var name = $"{id}.jpg";
            image.SaveJpg(System.IO.Path.Combine(outDir, name), int.Parse(Arg("jpg", "90"), CultureInfo.InvariantCulture) / 100f);
            return name;
        }
        var png = $"{id}.png";
        image.SavePng(System.IO.Path.Combine(outDir, png));
        return png;
    }

    /// <summary>
    /// A review shows the island, not the room turning into it: the room-to-island intro a landscape plays when it loads is skipped
    /// (found by name, so looks from before the intro are untouched), and the frames it needs to put everything back are waited for.
    /// </summary>
    private async Task SkipIntro()
    {
        Node? intro = null;
        for (var i = 0; i < 180 && intro == null; i++)
        {
            intro = _world.GetNodeOrNull("RoomIntro");
            if (intro == null) await NextFrame();
        }
        if (intro == null || !intro.HasMethod("Skip")) return;
        // Its data is read off the main thread and it marks the island once it has: it can only be skipped when that is done.
        for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 60 && !intro.Get("Playing").AsBool() && !intro.Get("Finished").AsBool();) await NextFrame();
        intro.Call("Skip");
        for (var i = 0; i < 10; i++) await NextFrame();
        GD.Print("LOOK_CAPTURE: the room-to-island intro was skipped" + (GodotObject.IsInstanceValid(intro) ? " (still ending)" : ""));
    }

    /// <summary>
    /// A camera that stages the player somewhere on a big island builds the ground's collision there first and waits for the physics
    /// server to have it: a teleport the same frame finds no ground and leaves the player where he stood.
    /// </summary>
    private async Task PrepareStage(JsonElement entry)
    {
        if (!entry.TryGetProperty("stage", out var stage) || !stage.TryGetProperty("player_m", out var at)) return;
        GarageGame.Native.Room.TerrainCollision.EnsureAround(_world, Vec(at), holdS: 3.0);
        for (var i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    /// <summary>Stand the avatars where a camera wants them before it is framed: the player at a spot facing a heading, the companion at a spot.</summary>
    private void Stage(JsonElement stage)
    {
        var yaw = Mathf.DegToRad(stage.TryGetProperty("yaw_deg", out var yawElement) ? (float)yawElement.GetDouble() : 0f);
        if (stage.TryGetProperty("player_m", out var playerAt))
        {
            GarageGame.Native.Room.TerrainCollision.EnsureAround(_world, Vec(playerAt), holdS: 0.5);
            if (!_world.Player.TryTeleportTo(Vec(playerAt))) GD.Print($"LOOK_CAPTURE stage: the player could not be placed at {Vec(playerAt)} (no ground there yet?); the camera is wherever the player stood");
            _world.Player.Rotation = new Vector3(0, yaw, 0);
            _world.Player.ResetPhysicsInterpolation();
        }
        if (stage.TryGetProperty("companion_m", out var companionAt))
        {
            _world.Companion.Stay();
            _world.Companion.TryTeleportTo(Vec(companionAt));
            _world.Companion.Rotation = new Vector3(0, yaw + Mathf.Pi, 0);
            _world.Companion.ResetPhysicsInterpolation();
        }
    }

    /// <summary>
    /// End the glows the last camera lit and light this camera's ("glows"), through the look's StartGlow as the host calls it: a halo
    /// on the Gubble or a wisp at a point. Looks without the glow ignore it.
    /// </summary>
    private void LightGlows(JsonElement entry)
    {
        if (_look == null || !_look.HasMethod("StartGlow")) return;
        foreach (var id in _glows) _look.Call(_look.HasMethod("StopEffect") ? "StopEffect" : "StopGlow", id);
        _glows.Clear();
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("glows", out var glows)) return;
        foreach (var glow in glows.EnumerateArray())
        {
            var id = "effect:review_" + glow.GetProperty("id").GetString();
            var onGubble = glow.TryGetProperty("on", out var on) && on.GetString() == "companion";
            var at = onGubble ? Vector3.Zero : glow.TryGetProperty("at_xz_m", out var xz) ? Ground((float)xz[0].GetDouble(), (float)xz[1].GetDouble()) : Vec(glow.GetProperty("at_m"));
            var radius = glow.TryGetProperty("radius_m", out var r) ? (float)r.GetDouble() : 0.6f;
            var intensity = glow.TryGetProperty("intensity", out var i) ? (float)i.GetDouble() : 0.6f;
            var kind = glow.TryGetProperty("kind", out var k) ? k.GetString() : "glow";
            if (kind == "bubbles")
            {
                GD.Print($"LOOK_CAPTURE: the review glow {id} is a stream of bubbles, which is gone (decisions 55 to 60); skipped");
                continue;
            }
            var method = kind == "fireworks" ? "StartFireworks" : "StartGlow";
            if (!_look.HasMethod(method)) continue;
            var started = _look.Call(method, id, onGubble ? _world.Companion : new Variant(), at, radius, intensity).AsBool();
            if (!started) throw new InvalidOperationException($"the look refused the review glow {id}");
            _glows.Add(id);
        }
    }

    /// <summary>The ground under a spot: the first world-layer surface a ray down from 5 m meets (else the sea's level, 0).</summary>
    private Vector3 Ground(float x, float z)
    {
        var space = _world.GetWorld3D().DirectSpaceState;
        using var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, 5f, z), new Vector3(x, -5f, z), GarageGame.Native.Room.RoomBuilder.WorldLayer);
        using var hit = space.IntersectRay(query);
        return hit.Count > 0 ? hit["position"].AsVector3() : new Vector3(x, 0f, z);
    }

    private string Require(string name) => _args.TryGetValue(name, out var value) && value.Length > 0
        ? value : throw new ArgumentException($"missing --{name}=...");

    private static int DayOfYear(string isoDate) =>
        DateTime.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfYear;

    /// <summary>The review clock may pin the lamps ("lamps": "on" or "off"); otherwise they switch themselves with the light.</summary>
    private void ReviewLamps(JsonElement clock)
    {
        var setLamps = _look?.GetType().GetMethod("SetLamps");
        if (_look == null || setLamps == null) return;
        bool? pinned = clock.TryGetProperty("lamps", out var lamps) ? lamps.GetString() switch { "on" => true, "off" => false, _ => null } : null;
        setLamps.Invoke(_look, new object?[] { pinned });
    }

    private bool SetClock(double hour, int dayOfYear)
    {
        if (_look == null || !_look.HasMethod("SetClock")) return false;
        _look.Call("SetClock", hour, dayOfYear);
        return true;
    }

    private void BuildTarget(Vector2I size)
    {
        var window = GetViewport();
        if (Arg("root-viewport", "false") == "true")
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
            DisplayServer.WindowSetSize(size);
            DisplayServer.WindowSetPosition(Vector2I.Zero);
            _camera = new Camera3D { Name = "ReviewCamera" };
            AddChild(_camera);
            _camera.MakeCurrent();
            return;
        }
        // Mirror the window's 3D quality settings so the offscreen target renders what a player would see.
        _target = new SubViewport
        {
            Name = "ReviewTarget", Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = window.Msaa3D, ScreenSpaceAA = window.ScreenSpaceAA, UseTaa = window.UseTaa,
            UseDebanding = window.UseDebanding, UseOcclusionCulling = window.UseOcclusionCulling,
            MeshLodThreshold = window.MeshLodThreshold, Scaling3DMode = window.Scaling3DMode,
            Scaling3DScale = window.Scaling3DScale, FsrSharpness = window.FsrSharpness,
            TextureMipmapBias = window.TextureMipmapBias, AnisotropicFilteringLevel = window.AnisotropicFilteringLevel,
            PositionalShadowAtlasSize = window.PositionalShadowAtlasSize, PositionalShadowAtlas16Bits = window.PositionalShadowAtlas16Bits,
        };
        for (var quadrant = 0; quadrant < 4; quadrant++)
            _target.SetPositionalShadowAtlasQuadrantSubdiv(quadrant, window.GetPositionalShadowAtlasQuadrantSubdiv(quadrant));
        AddChild(_target);
        _camera = new Camera3D { Name = "ReviewCamera" };
        _target.AddChild(_camera);
        _camera.Current = true;
        // The window shows a scaled preview only; it no longer renders the room itself.
        window.Disable3D = true;
        var preview = new CanvasLayer { Name = "Preview", Layer = 100 };
        AddChild(preview);
        var rect = new TextureRect { Texture = _target.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        preview.AddChild(rect);
        RenderingServer.ViewportSetMeasureRenderTime(_target.GetViewportRid(), true);
    }

    private static readonly string[] HudViews = { "eye", "shoulder", "diorama", "isometric" };

    /// <summary>Tell the look which view a camera stands for (null: none, the preset's miniature blur); older looks have no views.</summary>
    private void SetView(string? name)
    {
        var property = _look?.GetType().GetProperty("View");
        if (property == null) return;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        property.SetValue(_look, name == null ? null : Enum.Parse(type, name, ignoreCase: true));
    }

    private Node3D? _focused;
    private Transform3D _focusedHome;

    /// <summary>
    /// The focus highlight's captures: a room object moved onto a spot (its base on the ground there) and given the focus, with
    /// the camera looking at it from an offset. Every other camera has no focus, and the object goes back where it stood.
    /// </summary>
    private (Vector3 Position, Vector3 LookAt)? StageFocus(JsonElement entry)
    {
        if (_focused != null && IsInstanceValid(_focused)) _focused.GlobalTransform = _focusedHome;
        _focused = null;
        var setFocus = _look?.GetType().GetMethod("SetFocusHighlight");
        setFocus?.Invoke(_look, new object?[] { null });
        if (!entry.TryGetProperty("focus", out var focus)) return null;
        var id = focus.GetProperty("object").GetString()!;
        var thing = _world.Built.FindChildren("*", "Node3D", true, false).OfType<Node3D>().FirstOrDefault(n => n.HasMeta("entity_id") && n.GetMeta("entity_id").AsString() == id)
            ?? throw new InvalidOperationException($"no room object {id} to put in focus");
        _focused = thing;
        _focusedHome = thing.GlobalTransform;
        var spot = focus.GetProperty("at_xz_m");
        var x = (float)spot[0].GetDouble();
        var z = (float)spot[1].GetDouble();
        var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, 4f, z), new Vector3(x, -2f, z));
        if (thing is CollisionObject3D own) query.Exclude = new Godot.Collections.Array<Rid> { own.GetRid() };
        var hit = _world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        var ground = hit.Count > 0 ? hit["position"].AsVector3() : new Vector3(x, 0f, z);
        var under = hit.Count > 0 && hit["collider"].AsGodotObject() is Node node ? EntityOf(node) : "nothing";
        thing.GlobalPosition = ground;
        setFocus?.Invoke(_look, new object?[] { thing });
        GD.Print($"LOOK_CAPTURE focus={id} at={ground} on={under} highlight={(setFocus != null ? "on" : "not in this build")}");
        var offset = Vec(focus.GetProperty("camera_offset_m"));
        return (ground + offset, ground + Vector3.Up * 0.03f);
    }

    private static string EntityOf(Node node)
    {
        for (var current = node; current != null; current = current.GetParent())
            if (current.HasMeta("entity_id")) return current.GetMeta("entity_id").AsString();
        return node.Name;
    }

    /// <summary>
    /// A camera may hold the living shore's wash at a second of its clock ("shore_clock_s"), so a before and an after show the same
    /// moment and the wash can be shown up or drawn back; otherwise the wash runs. Found by name: looks from before the shore ignore it.
    /// </summary>
    private void HoldShore(JsonElement entry)
    {
        if (_look?.Get("OpenSea").AsGodotObject() is not GodotObject open) return;
        if (entry.TryGetProperty("shore_clock_s", out var seconds) && open.HasMethod("HoldShoreAt")) open.Call("HoldShoreAt", (float)seconds.GetDouble());
        else if (open.HasMethod("ReleaseShore")) open.Call("ReleaseShore");
    }

    private void Frame(JsonElement entry)
    {
        // Leave the HUD rig a previous camera mirrored.
        _mirror = null;
        if (_hud != null && _hud.ViewMode != 1) _hud.SetViewMode(1);
        if (entry.TryGetProperty("stage", out var stage)) Stage(stage);
        HoldShore(entry);
        var focused = StageFocus(entry);
        SetView(entry.TryGetProperty("view", out var view) ? view.GetString() : null);
        if (entry.TryGetProperty("eye_view", out var eyeView) && eyeView.GetBoolean())
        {
            // The player's own eye, 8.7 cm up, with the look's own focus rule for it (a few body heights ahead).
            _look?.Set("Observe", false);
            SetView("eye");
            _camera.CullMask = 0xFFFFFu & ~HiddenBodyLayer;
            _mirror = _world.Player.EyeCamera;
            Mirror();
            return;
        }
        if (entry.TryGetProperty("hud_view", out var hudView))
        {
            FrameHudRig(entry, hudView.GetInt32());
            return;
        }
        var position = focused?.Position ?? Vec(entry.GetProperty("position_m"));
        var lookAt = focused?.LookAt ?? Vec(entry.GetProperty("look_at_m"));
        _camera.GlobalPosition = position;
        _camera.LookAt(lookAt, Mathf.Abs((lookAt - position).Normalized().Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up);
        _camera.Fov = (float)entry.GetProperty("fov_deg").GetDouble();
        _camera.Near = 0.005f;
        _camera.Far = 50f;
        // A camera worked out from the island (a flight) sees as far as the game's cameras do there (the look's FarPlaneM, where it has one).
        if (entry.TryGetProperty("derived", out _)) _camera.Far = _look?.GetType().GetProperty("FarPlaneM")?.GetValue(_look) is float far ? far : 100f;
        var hideBody = entry.TryGetProperty("hide_player_body", out var hide) && hide.GetBoolean();
        _camera.CullMask = hideBody ? 0xFFFFFu & ~HiddenBodyLayer : 0xFFFFFu;
        var focus = entry.TryGetProperty("focus_m", out var focusElement) ? Vec(focusElement) : focused?.LookAt ?? lookAt;
        // A camera may ask for the observe view (a very tight tilt-shift band); older looks have no such switch and ignore it.
        _look?.Set("Observe", entry.TryGetProperty("observe", out var observe) && observe.GetBoolean());
        if (_look != null && _look.HasMethod("FrameCamera")) _look.Call("FrameCamera", _camera, focus);
    }

    /// <summary>
    /// The founder plays in the HUD's views, which no fixed camera shows: the rig is placed by the real HUD (the eye, the shoulder
    /// arm, or the diorama's pivot, spring arm and lens), and the look's own focus rule for that camera (FocusPointFor, which
    /// includes whatever the HUD handed it) and that view's blur set the depth of field, so a change to either shows in the frame.
    /// </summary>
    private void FrameHudRig(JsonElement entry, int mode)
    {
        _hud ??= _world.FindChildren("*", "CanvasLayer", true, false).OfType<RoomHud>().FirstOrDefault()
            ?? throw new InvalidOperationException("the room has no HUD to mirror for hud_view");
        _look?.Set("Observe", entry.TryGetProperty("observe", out var observe) && observe.GetBoolean());
        // The eye hides the player's own body, as the HUD does in F1.
        _camera.CullMask = mode == 0 ? 0xFFFFFu & ~HiddenBodyLayer : 0xFFFFFu;
        _hud.SetViewMode(mode);
        if (mode == 2 && entry.TryGetProperty("hud_pitch_deg", out var pitch))
            _hud.OrbitDiorama(new Vector2(0f, Mathf.DegToRad((float)pitch.GetDouble() - _hud.DioramaPitchDeg) / RoomHud.MouseRadiansPerPixel));
        SetView(HudViews[Math.Clamp(mode, 0, 3)]);
        _mirror = GetViewport().GetCamera3D() ?? _hud.DioramaCamera;
        Mirror();
    }

    private void Mirror()
    {
        if (_mirror == null || !IsInstanceValid(_mirror)) return;
        _camera.GlobalTransform = _mirror.GlobalTransform;
        _camera.Fov = _mirror.Fov;
        _camera.Near = _mirror.Near;
        _camera.Far = _mirror.Far;
        if (_look != null && _look.HasMethod("FrameCamera") && _look.HasMethod("FocusPointFor"))
            _look.Call("FrameCamera", _camera, _look.Call("FocusPointFor", _mirror));
    }

    private static Vector3 Vec(JsonElement array) =>
        new((float)array[0].GetDouble(), (float)array[1].GetDouble(), (float)array[2].GetDouble());

    private async Task NextFrame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Mirror();
    }

    private Image Grab()
    {
        var image = (_target != null ? _target.GetTexture() : GetViewport().GetTexture()).GetImage();
        image.Convert(Image.Format.Rgb8);
        return image;
    }

    private async Task<Dictionary<string, object>> Measure(int frames, JsonElement entry = default)
    {
        var flying = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("flight", out _);
        var costs = new List<(FrameCost Before, FrameCost After)>(frames);
        var wall = new List<double>(frames);
        var gpu = new List<double>(frames);
        var cpu = new List<double>(frames);
        var drawCalls = new List<double>(frames);
        var primitives = new List<double>(frames);
        var rid = _target?.GetViewportRid() ?? GetViewport().GetViewportRid();
        if (_target == null) RenderingServer.ViewportSetMeasureRenderTime(rid, true);
        await NextFrame();
        var last = Time.GetTicksUsec();
        var started = last;
        var cost = CostNow(rid);
        for (var i = 0; i < frames; i++)
        {
            if (flying) Fly(entry, (Time.GetTicksUsec() - started) / 1e6);
            await NextFrame();
            var now = Time.GetTicksUsec();
            wall.Add((now - last) / 1000.0);
            last = now;
            var next = CostNow(rid);
            costs.Add((cost, next));
            cost = next;
            gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
            cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid));
            drawCalls.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
            primitives.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
        }
        // A slow frame: over the 16.7 ms of 60 frames a second, and well over this view's own median.
        var threshold = Math.Max(16.7, 2.5 * Percentile(wall, 0.50));
        return new Dictionary<string, object>
        {
            ["frames"] = frames,
            ["frame_ms_mean"] = Round(wall.Average()),
            ["frame_ms_p50"] = Round(Percentile(wall, 0.50)),
            ["frame_ms_p95"] = Round(Percentile(wall, 0.95)),
            ["frame_ms_p99"] = Round(Percentile(wall, 0.99)),
            ["frame_ms_max"] = Round(wall.Max()),
            ["gpu_ms_mean"] = Round(gpu.Average()),
            ["gpu_ms_p95"] = Round(Percentile(gpu, 0.95)),
            ["render_cpu_ms_mean"] = Round(cpu.Average()),
            ["draw_calls_mean"] = Round(drawCalls.Average()),
            ["primitives_mean"] = Round(primitives.Average()),
            ["video_mem_mib"] = Round(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0),
            ["spike_threshold_ms"] = Round(threshold),
            ["spikes"] = wall.Select((ms, i) => (ms, i)).Where(f => f.ms > threshold).Select(f => Spike(f.i, f.ms, costs[f.i].Before, costs[f.i].After)).ToList(),
            ["frames_over_25_ms"] = wall.Count(ms => ms > 25.0),
            ["gc_collections"] = new[] { costs[^1].After.Gen0 - costs[0].Before.Gen0, costs[^1].After.Gen1 - costs[0].Before.Gen1, costs[^1].After.Gen2 - costs[0].Before.Gen2 },
            ["pipeline_compilations"] = costs[^1].After.Pipelines - costs[0].Before.Pipelines,
        };
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double Round(double value) => Math.Round(value, 3);

    /// <summary>One camera across four seasons and four hours, composed into a single contact sheet.</summary>
    private async Task Sweep(JsonElement root, string outDir)
    {
        if (_look == null || !_look.HasMethod("SetClock"))
        {
            GD.Print("LOOK_CAPTURE: this build has no SetClock hook; skipping the time and season sweep.");
            return;
        }
        // The cameras file may name the sweep's camera, hours and dates ("sweep"); --sweep-camera overrides the camera.
        var sweep = root.TryGetProperty("sweep", out var configured) ? configured : default;
        var cameraId = Arg("sweep-camera", sweep.ValueKind == JsonValueKind.Object && sweep.TryGetProperty("camera", out var named) ? named.GetString()! : "ceiling_corner");
        var entry = Entries(root).First(c => c.GetProperty("id").GetString() == cameraId);
        Frame(entry);
        // The sweep shows the day as it goes: the lamps switch themselves with the light.
        _look.GetType().GetMethod("SetLamps")?.Invoke(_look, new object?[] { null });
        var dates = sweep.ValueKind == JsonValueKind.Object && sweep.TryGetProperty("dates", out var dateList)
            ? dateList.EnumerateArray().Select(d => (d.GetString()!, d.GetString()!)).ToArray()
            : new[] { ("winter", "2026-01-15"), ("spring", "2026-04-15"), ("summer", "2026-07-15"), ("autumn", "2026-10-15") };
        var hours = sweep.ValueKind == JsonValueKind.Object && sweep.TryGetProperty("hours", out var hourList)
            ? hourList.EnumerateArray().Select(h => h.GetDouble()).ToArray()
            : new[] { 7.0, 12.0, 16.5, 21.0 };
        var cell = new Vector2I(480, 270);
        var sheet = Image.CreateEmpty(cell.X * hours.Length, cell.Y * dates.Length, false, Image.Format.Rgb8);
        for (var row = 0; row < dates.Length; row++)
            for (var column = 0; column < hours.Length; column++)
            {
                SetClock(hours[column], DayOfYear(dates[row].Item2));
                for (var i = 0; i < 20; i++) await NextFrame();
                var image = Grab();
                image.Resize(cell.X, cell.Y, Image.Interpolation.Lanczos);
                sheet.BlitRect(image, new Rect2I(Vector2I.Zero, cell), new Vector2I(column * cell.X, row * cell.Y));
            }
        sheet.SavePng(System.IO.Path.Combine(outDir, $"sweep_{cameraId}.png"));
        var clock = root.GetProperty("clock");
        SetClock(clock.GetProperty("hour").GetDouble(), DayOfYear(clock.GetProperty("date").GetString()!));
        ReviewLamps(clock);
        GD.Print($"LOOK_CAPTURE sweep={cameraId} rows={string.Join(",", dates.Select(d => d.Item1))} columns={string.Join(",", hours.Select(h => $"{(int)h:00}:{(int)Math.Round(h % 1 * 60):00}"))}");
    }

    /// <summary>
    /// The post effect, checked on the GPU: the first camera rendered with the effect and without it. With it, the
    /// corners must be darker relative to the centre (the vignette) and neighbouring pixels must differ by a fixed
    /// pattern (the grain). Older commits without the effect report that and are not judged.
    /// </summary>
    private async Task<Dictionary<string, object>> PostEffectCheck(JsonElement root)
    {
        var result = new Dictionary<string, object>();
        var post = _look != null && _look.HasMethod("SelfCheck") ? _look.Get("Post").AsGodotObject() as CompositorEffect : null;
        if (post == null)
        {
            result["ran"] = false;
            result["note"] = "this build has no grain and vignette effect to check";
            return result;
        }
        Frame(Entries(root).First());
        post.Enabled = true;
        for (var i = 0; i < 30; i++) await NextFrame();
        var with = Grab();
        post.Enabled = false;
        for (var i = 0; i < 30; i++) await NextFrame();
        var without = Grab();
        post.Enabled = true;
        for (var i = 0; i < 5; i++) await NextFrame();
        var w = with.GetWidth();
        var h = with.GetHeight();
        var patch = Math.Max(8, h / 12);
        double Mean(Image image, int x0, int y0)
        {
            double sum = 0;
            for (var y = y0; y < y0 + patch; y++)
                for (var x = x0; x < x0 + patch; x++)
                {
                    var c = image.GetPixel(x, y);
                    sum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                }
            return sum / (patch * patch) + 1e-4;
        }
        var corners = new[] { (0, 0), (w - patch, 0), (0, h - patch), (w - patch, h - patch) };
        var cornerRatio = corners.Average(c => Mean(with, c.Item1, c.Item2) / Mean(without, c.Item1, c.Item2));
        var centreRatio = Mean(with, w / 2 - patch / 2, h / 2 - patch / 2) / Mean(without, w / 2 - patch / 2, h / 2 - patch / 2);
        // Grain: the per-pixel ratio between the two images, correlated with the pattern the shader is known to
        // multiply in (LookPostEffect.Factor, found by reflection so this file still builds against older commits).
        // Tone mapping, 8-bit output and TAA shrink a 5% grain to well under one 8-bit level, so its spread alone
        // cannot be told from frame-to-frame noise; the correlation can (noise alone gives about 1/sqrt(pixels)). The
        // grain is sampled over a centre patch a fifth of the frame tall, so a darker room (fewer pixels bright enough
        // to carry the grain past 8-bit rounding) still leaves the correlation well above the noise level.
        var factor = post.GetType().GetMethod("Factor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var pushConstants = post.GetType().GetMethod("PushConstants")?.Invoke(post, new object[] { new Vector2I(w, h) }) as float[];
        var measured = new List<double>();
        var expected = new List<double>();
        var grainPatch = Math.Max(8, h / 5);
        for (var y = h / 2 - grainPatch / 2; y < h / 2 + grainPatch / 2; y++)
            for (var x = w / 2 - grainPatch / 2; x < w / 2 + grainPatch / 2; x++)
            {
                var a = with.GetPixel(x, y);
                var b = without.GetPixel(x, y);
                var lb = 0.2126 * b.R + 0.7152 * b.G + 0.0722 * b.B;
                if (lb <= 0.08) continue;
                measured.Add((0.2126 * a.R + 0.7152 * a.G + 0.0722 * a.B) / lb);
                expected.Add(factor != null && pushConstants != null ? (float)factor.Invoke(null, new object[] { pushConstants, new Vector2I(x, y) })! : 1.0);
            }
        var spread = Spread(measured);
        var correlation = Correlation(measured, expected);
        var vignetteSeen = centreRatio - cornerRatio;
        result["ran"] = true;
        result["corner_to_centre_darkening"] = Round(vignetteSeen);
        result["grain_spread"] = Round(spread);
        result["grain_correlation"] = Round(correlation);
        result["noise_correlation_scale"] = Round(measured.Count > 0 ? 1.0 / Math.Sqrt(measured.Count) : 1.0);
        result["centre_pixels_compared"] = measured.Count;
        // The vignette (0.15 in the preset) darkens the corners about 5% after tone mapping; the grain must
        // correlate with its known pattern far above the noise level.
        result["passed"] = vignetteSeen > 0.02 && measured.Count > 1000 && correlation > 8.0 / Math.Sqrt(measured.Count);
        return result;
    }

    private Dictionary<string, object>? _postCheck;
    private Dictionary<string, object>? _lightChecks;

    private JsonElement CameraEntry(JsonElement root, string id) =>
        Entries(root).First(c => c.GetProperty("id").GetString() == id);

    private static JsonElement LightConfig(JsonElement root) => root.TryGetProperty("light_checks", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;

    private static HashSet<string> ContactCameras(JsonElement root)
    {
        var config = LightConfig(root);
        return config.ValueKind == JsonValueKind.Object && config.TryGetProperty("contact_cameras", out var list)
            ? list.EnumerateArray().Select(e => e.GetString()!).ToHashSet() : new HashSet<string>();
    }

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    /// <summary>
    /// Close crops of each avatar's feet from a review image, to judge contact shadows (a 10 cm body must look grounded):
    /// the avatar's base projected into the camera, cropped about two body heights tall and upscaled to 480 px.
    /// </summary>
    private List<string> SaveContactCrops(Image image, string cameraId, string outDir)
    {
        var files = new List<string>();
        foreach (var (name, body) in new (string, Node3D?)[] { ("player", _world.Player), ("companion", _world.Companion) })
        {
            if (body == null || _camera.IsPositionBehind(body.GlobalPosition)) continue;
            var heightVariant = body.Get("BodyHeightM");
            var height = heightVariant.VariantType == Variant.Type.Nil ? 0.1f : (float)heightVariant.AsDouble();
            var foot = _camera.UnprojectPosition(body.GlobalPosition);
            var head = _camera.UnprojectPosition(body.GlobalPosition + Vector3.Up * height);
            var tall = Mathf.Clamp(Mathf.Abs(foot.Y - head.Y) * 2.2f, 48f, image.GetHeight() * 0.8f);
            var wide = tall * 1.6f;
            var rect = new Rect2I((int)(foot.X - wide * 0.5f), (int)(foot.Y - tall * 0.7f), (int)wide, (int)tall)
                .Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
            if (rect.Size.X < 16 || rect.Size.Y < 16) continue;
            var crop = image.GetRegion(rect);
            var scale = 480f / crop.GetHeight();
            crop.Resize(Mathf.Max(1, (int)(crop.GetWidth() * scale)), 480, Image.Interpolation.Lanczos);
            var file = $"contact_{cameraId}_{name}.png";
            crop.SavePng(System.IO.Path.Combine(outDir, file));
            files.Add(file);
        }
        return files;
    }

    /// <summary>
    /// Light from real sources, checked in the pixels. The sun: at the review clock, from light_checks.sun_camera, floor
    /// points the physics says see the sun (no wall, ceiling or object on the line to it) must get clearly brighter
    /// when the sun is on, and floor points the shell hides from it must barely change (only by the sun's bounce). The
    /// night: at 02:00 on the review date, from each of light_checks.night_cameras, with the lamps off and on, saved as
    /// night_{camera}_lamps_off.png and night_{camera}_lamps_on.png with their brightness. Builds without a sun key or
    /// a lamp switch report that and are not judged.
    /// </summary>
    private async Task<Dictionary<string, object>> LightChecks(JsonElement root, string outDir)
    {
        var result = new Dictionary<string, object>();
        var key = _look?.Get("Key").AsGodotObject() as DirectionalLight3D;
        var setLamps = _look?.GetType().GetMethod("SetLamps");
        if (_look == null || key == null || setLamps == null || !_look.HasMethod("SetClock"))
        {
            result["ran"] = false;
            result["note"] = "this build has no sun key or lamp switch to check";
            return result;
        }
        var config = LightConfig(root);
        var clock = root.GetProperty("clock");
        var reviewHour = clock.GetProperty("hour").GetDouble();
        var reviewDay = DayOfYear(clock.GetProperty("date").GetString()!);
        string Field(string name, string fallback) => config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var v) ? v.GetString()! : fallback;

        // The sun through the window.
        SetClock(reviewHour, reviewDay);
        Frame(CameraEntry(root, Field("sun_camera", "ceiling_corner")));
        for (var i = 0; i < 30; i++) await NextFrame();
        var toSun = key.GlobalBasis.Z.Normalized();
        var space = _world.GetWorld3D().DirectSpaceState;
        var visible = GetViewport().GetVisibleRect().Size;
        var size = _target != null ? _target.Size : new Vector2I((int)visible.X, (int)visible.Y);
        var bounds = _world.Room.Bounds;
        var classes = new Dictionary<(int, int), int>();
        var screens = new Dictionary<(int, int), Vector2>();
        var nx = (int)(bounds.Size.X / 0.1f);
        var nz = (int)(bounds.Size.Z / 0.1f);
        for (var ix = 0; ix < nx; ix++)
            for (var iz = 0; iz < nz; iz++)
            {
                var point = new Vector3(bounds.Position.X + 0.05f + ix * 0.1f, 0.002f, bounds.Position.Z + 0.05f + iz * 0.1f);
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(point, point + toSun * 12f));
                var shell = hit.Count > 0 && hit["collider"].AsGodotObject() is Node node && node.HasMeta("surface_role");
                classes[(ix, iz)] = hit.Count == 0 ? 1 : shell ? -1 : 0;
                if (_camera.IsPositionBehind(point)) continue;
                var screen = _camera.UnprojectPosition(point);
                if (screen.X < 8 || screen.Y < 8 || screen.X > size.X - 8 || screen.Y > size.Y - 8) continue;
                var toCamera = (_camera.GlobalPosition - point).Normalized();
                if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(_camera.GlobalPosition, point + toCamera * 0.01f)).Count > 0) continue;
                screens[(ix, iz)] = screen;
            }
        // Only points well inside the sun patch or well inside the shell's shadow (all eight neighbours alike).
        bool Interior((int X, int Z) cell, int kind) =>
            Enumerable.Range(-1, 3).All(dx => Enumerable.Range(-1, 3).All(dz => classes.TryGetValue((cell.X + dx, cell.Z + dz), out var k) && k == kind));
        var litPoints = screens.Where(s => Interior(s.Key, 1)).Select(s => s.Value).ToArray();
        var shadePoints = screens.Where(s => Interior(s.Key, -1)).Select(s => s.Value).ToArray();
        key.Visible = true;
        for (var i = 0; i < 40; i++) await NextFrame();
        var on = Grab();
        key.Visible = false;
        for (var i = 0; i < 40; i++) await NextFrame();
        var off = Grab();
        key.Visible = true;
        double Patch(Image image, Vector2 at)
        {
            double sum = 0;
            for (var y = -2; y <= 2; y++)
                for (var x = -2; x <= 2; x++)
                    sum += Luma(image.GetPixel((int)at.X + x, (int)at.Y + y));
            return sum / 25.0;
        }
        double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? 0.0 : sorted[sorted.Length / 2];
        }
        var litGain = Median(litPoints.Select(s => Patch(on, s) - Patch(off, s)));
        var shadeGain = Median(shadePoints.Select(s => Patch(on, s) - Patch(off, s)));
        var sun = new Dictionary<string, object>
        {
            ["camera"] = Field("sun_camera", "ceiling_corner"),
            ["sun_elevation_deg"] = Round(Mathf.RadToDeg(Mathf.Asin(toSun.Y))),
            ["lit_points"] = litPoints.Length,
            ["shaded_points"] = shadePoints.Length,
            ["lit_gain"] = Round(litGain),
            ["shaded_gain"] = Round(shadeGain),
        };
        // The sun must land in its patch and the shell must hold it back elsewhere (what remains there is its bounce).
        sun["passed"] = litPoints.Length >= 5 && shadePoints.Length >= 20 && litGain > 0.06 && shadeGain < 0.3 * litGain;
        result["sun_through_window"] = sun;

        // The night, lamps off and on.
        var nights = new List<Dictionary<string, object>>();
        var cameras = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("night_cameras", out var list)
            ? list.EnumerateArray().Select(e => e.GetString()!).ToArray() : new[] { "ceiling_corner" };
        var nightHour = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("night_hour", out var h) ? h.GetDouble() : 2.0;
        SetClock(nightHour, reviewDay);
        foreach (var cameraId in cameras)
        {
            Frame(CameraEntry(root, cameraId));
            foreach (var lampsOn in new[] { false, true })
            {
                setLamps.Invoke(_look, new object?[] { lampsOn });
                for (var i = 0; i < 40; i++) await NextFrame();
                var image = Grab();
                var file = $"night_{cameraId}_lamps_{(lampsOn ? "on" : "off")}.png";
                image.SavePng(System.IO.Path.Combine(outDir, file));
                var lumas = new List<double>();
                for (var y = 0; y < image.GetHeight(); y += 6)
                    for (var x = 0; x < image.GetWidth(); x += 6)
                        lumas.Add(Luma(image.GetPixel(x, y)));
                lumas.Sort();
                nights.Add(new Dictionary<string, object>
                {
                    ["camera"] = cameraId, ["lamps"] = lampsOn ? "on" : "off", ["file"] = file,
                    ["mean_luma"] = Round(lumas.Average()), ["p05_luma"] = Round(lumas[lumas.Count / 20]), ["p50_luma"] = Round(lumas[lumas.Count / 2]),
                    ["p95_luma"] = Round(lumas[lumas.Count * 19 / 20]),
                });
            }
        }
        setLamps.Invoke(_look, new object?[] { null });
        result["night"] = nights;

        // Contact shadows under each real source: the contact cameras at moments the config names (the sun on the
        // avatars on a summer evening, the lamp at night), each saved whole and as close crops of the avatars' feet.
        var moments = new List<Dictionary<string, object>>();
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("contact_moments", out var momentList))
            foreach (var moment in momentList.EnumerateArray())
            {
                var label = moment.GetProperty("label").GetString()!;
                SetClock(moment.GetProperty("hour").GetDouble(), DayOfYear(moment.GetProperty("date").GetString()!));
                setLamps.Invoke(_look, new object?[] { moment.TryGetProperty("lamps", out var lamps) ? lamps.GetString() == "on" : null });
                foreach (var cameraId in ContactCameras(root))
                {
                    Frame(CameraEntry(root, cameraId));
                    for (var i = 0; i < 40; i++) await NextFrame();
                    var image = Grab();
                    var file = $"{label}_{cameraId}.png";
                    image.SavePng(System.IO.Path.Combine(outDir, file));
                    moments.Add(new Dictionary<string, object> { ["label"] = label, ["camera"] = cameraId, ["file"] = file, ["contact_crops"] = SaveContactCrops(image, $"{label}_{cameraId}", outDir) });
                }
            }
        setLamps.Invoke(_look, new object?[] { null });
        result["contact_moments"] = moments;
        // Lamps off at night: dark, but shapes must still read (some of the frame is clearly above black).
        var offFrames = nights.Where(n => (string)n["lamps"] == "off").ToArray();
        result["night_passed"] = offFrames.All(n => (double)n["p95_luma"] > 0.08) && nights.Where(n => (string)n["lamps"] == "on")
            .All(on => (double)on["mean_luma"] > (double)offFrames.First(off => (string)off["camera"] == (string)on["camera"])["mean_luma"]);
        SetClock(reviewHour, reviewDay);
        result["ran"] = true;
        return result;
    }

    private static double Spread(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
    }

    private static double Correlation(List<double> a, List<double> b)
    {
        if (a.Count < 2 || a.Count != b.Count) return 0.0;
        var ma = a.Average();
        var mb = b.Average();
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            sab += (a[i] - ma) * (b[i] - mb);
            saa += (a[i] - ma) * (a[i] - ma);
            sbb += (b[i] - mb) * (b[i] - mb);
        }
        return saa <= 0 || sbb <= 0 ? 0.0 : sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>The look's self-check (renderer fallback, missing GI, bake stand-ins left, post effect never ran) and the pixel check.</summary>
    private List<string> LookProblems()
    {
        var problems = new List<string>();
        if (_look != null && _look.HasMethod("SelfCheck")) problems.AddRange(_look.Call("SelfCheck").AsStringArray());
        if (_postCheck != null && _postCheck.TryGetValue("passed", out var passed) && passed is false)
            problems.Add($"the grain and vignette effect did not show in the pixels (corner darkening {_postCheck["corner_to_centre_darkening"]}, grain correlation {_postCheck["grain_correlation"]} against a noise scale of {_postCheck["noise_correlation_scale"]})");
        if (_lightChecks != null && _lightChecks.TryGetValue("sun_through_window", out var sunCheck) && sunCheck is Dictionary<string, object> sun && sun["passed"] is false)
            problems.Add($"the sun did not land only through the window in the pixels (lit gain {sun["lit_gain"]} over {sun["lit_points"]} points, shaded gain {sun["shaded_gain"]} over {sun["shaded_points"]})");
        if (_lightChecks != null && _lightChecks.TryGetValue("night_passed", out var night) && night is false)
            problems.Add("at night with the lamps off the frame was black (no shapes), or the lamps did not brighten it");
        return problems;
    }

    private void WriteReport(string outDir, string camerasPath, Vector2I resolution, int warmup, int frames, List<string> problems)
    {
        var report = new Dictionary<string, object?>
        {
            ["label"] = Arg("label", ""),
            ["commit"] = Arg("commit", ""),
            ["note"] = Arg("note", ""),
            ["captured_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["engine"] = Engine.GetVersionInfo()["string"].AsString(),
            ["rendering_method"] = RenderingServer.GetCurrentRenderingMethod(),
            ["rendering_driver"] = RenderingServer.GetCurrentRenderingDriverName(),
            ["adapter"] = RenderingServer.GetVideoAdapterName(),
            ["adapter_driver"] = string.Join(" ", OS.GetVideoAdapterDriverInfo()),
            ["target"] = _target != null ? "offscreen SubViewport (window shows a scaled preview)" : "window root viewport, borderless at the screen origin",
            ["resolution"] = new[] { resolution.X, resolution.Y },
            ["vsync"] = "disabled",
            ["warmup_frames"] = warmup,
            ["measured_frames"] = frames,
            ["style"] = _world.HasMeta("style") ? _world.GetMeta("style").AsString() : "",
            ["style_note"] = _world.StyleNote,
            ["room_manifest_sha256"] = _world.Room.ManifestSha256,
            ["cameras_file"] = camerasPath,
            ["msaa_3d"] = (_target != null ? _target.Msaa3D : GetViewport().Msaa3D).ToString(),
            ["screen_space_aa"] = (_target != null ? _target.ScreenSpaceAA : GetViewport().ScreenSpaceAA).ToString(),
            ["use_taa"] = _target != null ? _target.UseTaa : GetViewport().UseTaa,
            ["cameras"] = _results,
            ["memory_end"] = MemoryNow(),
            ["memory_breakdown"] = MemoryBreakdown(),
            ["derive_notes"] = _deriveNotes.Distinct().ToList(),
        };
        if (_look != null && _look.HasMethod("DescribeLook")) report["look"] = _look.Call("DescribeLook").AsString();
        report["look_problems"] = problems;
        if (_postCheck != null) report["post_effect_check"] = _postCheck;
        if (_lightChecks != null) report["light_checks"] = _lightChecks;
        if (Arg("style", "").Length > 0) report["style_override"] = Arg("style", "");
        if (_look != null && _look.HasMethod("SelfCheck")) report["player_notice"] = _look.Get("PlayerNotice").AsString();
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "timings.json"), new UTF8Encoding(false).GetBytes(json.Replace("\r\n", "\n") + "\n"));
    }
}
