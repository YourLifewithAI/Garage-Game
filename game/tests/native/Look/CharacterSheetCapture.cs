using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// The character sheets (Run 2, characters round, L8): one contact sheet per character, nine tiles, rendered on the GPU in the
/// test room at the review clock (17:00 on 15 April, the lamps on: the low sun through the west window lands on the player's
/// spawn, where every tile stands). Run it through tools/look/character_sheet.ps1, never by hand over someone playing.
/// Each tile builds its character afresh (the body CharacterSheetPoses.BuildBody makes, or the bare GLB with CharacterLook applied)
/// and stands it on the tile's stage: the room's floor, a 30° slope across it, water at the shins, a wall, each with real
/// collision. The tiles are idle, walk, run, a hop landing, a 30° slope, wading, climbing, a tumble mid-flop, and for a floating
/// character floating and casting (the halo lit through the real look) or for the others a close look at the face. The pose for
/// a tile is called once per physics tick, as the game ticks a body's pose stack, until it says the tile is ready; a tile without
/// one shows the rest pose with "rest pose" in its label. With --timing it also measures what a bubble costs: the GPU time of a
/// 1920 x 1080 frame with the character's bubble parts shown and hidden, at a play distance and close up, in alternating blocks.
/// Needs a real window: under --headless it says so and exits with code 2.
/// With --variants as well, it measures the shader's render-mode variants (one skin, lit) the same way, for the budget's record.
/// With --motion-only=true it renders nothing and may run headless (with --fixed-fps 60): every tile's motion is run on its stage
/// and judged by its pose, one CHARACTER_SHEET_MOTION line a tile, then "CHARACTER_SHEET_MOTION: n/n tiles show their motion"
/// (exit 1 when any does not): the sheets' motion, checked without a GPU.
/// User arguments (after "--"): --out=DIR --assets=DIR [--characters=ID,ID] [--tile=WxH] [--timing=true] [--variants=true] [--motion-only=true] [--commit=TEXT] [--note=TEXT]
/// </summary>
public partial class CharacterSheetCapture : Node
{
    /// <summary>The player's spawn in the test room: the 17:00 April sun lands here.</summary>
    private static readonly Vector3 Station = new(0f, 0f, 0.6f);
    private const float FloatLiftM = 0.02f;
    private const float SlopeDeg = 30f;
    private const int ReviewDay = 105; // 15 April
    /// <summary>The sheets run at the physics rate: a body's pose stack runs once a physics tick and its springs are tuned to that step.</summary>
    private const int SheetFps = 60;

    private readonly Dictionary<string, string> _args = new();
    private RoomWorld _world = null!;
    private LookDirector _look = null!;
    private SubViewport _target = null!;
    private Camera3D _camera = null!;
    private readonly List<object> _sheets = new();
    private readonly List<object> _costs = new();
    /// <summary>--motion-only=true: no rendering (headless allowed); every tile's motion is run and judged, and nothing is written.</summary>
    private bool _motionOnly;
    private int _motionTiles, _motionShown;

    private string Arg(string name, string fallback) => _args.TryGetValue(name, out var value) ? value : fallback;

    private async Task NextFrame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task NextPhysicsFrame() => await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

    public override async void _Ready()
    {
        try
        {
            foreach (var argument in OS.GetCmdlineUserArgs())
            {
                if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;
                var split = argument.IndexOf('=');
                if (split > 2) _args[argument[2..split]] = argument[(split + 1)..];
                else _args[argument[2..]] = "true";
            }
            _motionOnly = Arg("motion-only", "false") == "true";
            if (DisplayServer.GetName() == "headless" && !_motionOnly)
            {
                GD.Print("CHARACTER_SHEET: needs a real window on the GPU; headless renders nothing");
                GetTree().Quit(2);
                return;
            }
            var outDir = Arg("out", "");
            var assets = Arg("assets", "");
            if ((outDir.Length == 0 && !_motionOnly) || assets.Length == 0) throw new ArgumentException("--out=DIR and --assets=DIR are required");
            if (outDir.Length > 0) System.IO.Directory.CreateDirectory(outDir);
            var ids = Arg("characters", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0)
                ids = System.IO.Directory.GetDirectories(assets).Where(d => System.IO.File.Exists(System.IO.Path.Combine(d, "character.json")))
                    .Select(d => System.IO.Path.GetFileName(d)!).OrderBy(d => d, StringComparer.Ordinal).ToArray();
            var tile = Arg("tile", "720x450").Split('x').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var registered = CharacterSheetPoses.Discover();
            GD.Print($"CHARACTER_SHEET: pose providers: {(registered.Count == 0 ? "none yet (every tile is the rest pose)" : string.Join(", ", registered))}");

            // Headless (--fixed-fps) the motion runs as fast as it can, one physics tick a frame.
            Engine.MaxFps = _motionOnly ? 0 : SheetFps;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(_world);
            // The room's data loads off the main thread (RoomData.LoadAsync, S6), so wait on the clock: uncapped and headless, 600
            // frames went by before a 1x room had read.
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_world.WorldReady && _world.LoadError.Length == 0;) await NextFrame();
            if (!_world.WorldReady) throw new InvalidOperationException("room did not load: " + _world.LoadError);
            _look = _world.Look;
            foreach (var layer in _world.FindChildren("*", "CanvasLayer", true, false).OfType<CanvasLayer>()) layer.Visible = false;
            foreach (var label in _world.FindChildren("*", "Label3D", true, false).OfType<Label3D>()) label.Visible = false;
            _world.Player.ReadKeyboard = false;
            _world.Player.SetInputEnabled(false);
            _world.Companion.Stay();
            _world.Player.TryTeleportTo(new Vector3(1.6f, 0f, -1.1f));
            _world.Companion.TryTeleportTo(new Vector3(1.75f, 0f, -1.2f));
            HideAvatars();
            _look.SetClock(17f, ReviewDay);
            _look.SetLamps(true);
            BuildTarget(new Vector2I(tile[0], tile[1]));
            for (var i = 0; i < 30; i++) await NextFrame();

            foreach (var id in ids) await Sheet(id, System.IO.Path.Combine(assets, id), outDir, new Vector2I(tile[0], tile[1]));
            if (_motionOnly)
            {
                // The headless check of the sheets' motion: every tile reached its moment and the pose found what its label names.
                GD.Print($"CHARACTER_SHEET_MOTION: {_motionShown}/{_motionTiles} tiles show their motion");
                GetTree().Quit(_motionShown == _motionTiles && _motionTiles > 0 ? 0 : 1);
                return;
            }
            // The bubble's cost is a GPU time: measured uncapped, as before.
            Engine.MaxFps = 0;
            if (Arg("timing", "false") == "true")
                foreach (var id in ids) await BubbleCost(id, System.IO.Path.Combine(assets, id), outDir);

            var report = new Dictionary<string, object>
            {
                ["schema"] = "enfractal.character_sheets",
                ["version"] = 1,
                ["commit"] = Arg("commit", ""),
                ["note"] = Arg("note", ""),
                ["engine"] = Engine.GetVersionInfo()["string"].AsString(),
                ["adapter"] = RenderingServer.GetVideoAdapterName(),
                ["clock"] = "17:00 on 15 April, lamps on, test room, the player's spawn",
                ["pose_providers"] = registered,
                ["sheets"] = _sheets,
                ["bubble_cost"] = _costs,
            };
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "character_sheets.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");
            GD.Print($"CHARACTER_SHEET_DONE sheets={_sheets.Count} costs={_costs.Count} out={outDir}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError("Character sheet failed: " + error);
            GetTree().Quit(1);
        }
    }

    private void HideAvatars()
    {
        foreach (var body in new Node[] { _world.Player, _world.Companion })
            foreach (var mesh in body.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>())
            {
                mesh.Visible = false;
                // The companion shows its name tag again whenever the window's camera is far enough away: on no render layer, it never draws.
                if (mesh is Label3D) mesh.Layers = 0;
            }
    }

    /// <summary>An offscreen target of the tile's size mirroring the window's 3D settings (as the review harness), and a framed window camera so the look's own focus stays ours.</summary>
    private void BuildTarget(Vector2I size)
    {
        var window = GetViewport();
        _target = new SubViewport
        {
            Name = "SheetTarget", Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = window.Msaa3D, ScreenSpaceAA = window.ScreenSpaceAA, UseTaa = window.UseTaa, UseDebanding = window.UseDebanding,
            PositionalShadowAtlasSize = window.PositionalShadowAtlasSize, PositionalShadowAtlas16Bits = window.PositionalShadowAtlas16Bits,
        };
        for (var quadrant = 0; quadrant < 4; quadrant++)
            _target.SetPositionalShadowAtlasQuadrantSubdiv(quadrant, window.GetPositionalShadowAtlasQuadrantSubdiv(quadrant));
        AddChild(_target);
        _camera = new Camera3D { Name = "SheetCamera" };
        _target.AddChild(_camera);
        _camera.Current = true;
        RenderingServer.ViewportSetMeasureRenderTime(_target.GetViewportRid(), true);
        // The window's own camera: framed by us, so the look does not refocus its post effect on the HUD's camera every frame.
        var windowCamera = new Camera3D { Name = "WindowCamera", Position = Station + new Vector3(0, 0.1f, 0.3f) };
        AddChild(windowCamera);
        _look.FrameCamera(windowCamera, Station);
        windowCamera.MakeCurrent();
        window.Disable3D = true;
        var preview = new CanvasLayer { Name = "Preview", Layer = 100 };
        AddChild(preview);
        var rect = new TextureRect { Texture = _target.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        preview.AddChild(rect);
    }

    // ---------- one character ----------

    /// <summary>What a sheet reads once from character.json: the name, the motion kind and the drawn bounds.</summary>
    private sealed record CharacterInfo(string Folder, string Json, string Name, string Motion, Aabb Bounds);

    private sealed class Character
    {
        public required Node3D Holder;
        public required Node3D Visual;
        public required CharacterLookResult Look;
    }

    private static CharacterInfo ReadInfo(string folder)
    {
        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(folder, "character.json"));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var name = root.GetProperty("name").GetString() ?? System.IO.Path.GetFileName(folder);
        var motion = root.GetProperty("motion").GetProperty("kind").GetString() ?? "";
        var min = root.GetProperty("bounds_m").GetProperty("min").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
        var max = root.GetProperty("bounds_m").GetProperty("max").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
        return new CharacterInfo(folder, json, name, motion,
            new Aabb(new Vector3(min[0], min[1], min[2]), new Vector3(max[0] - min[0], max[1] - min[1], max[2] - min[2])));
    }

    /// <summary>A fresh body for one tile, standing with its feet at <paramref name="feet"/>, facing -Z.</summary>
    private Character Build(CharacterInfo info, string tileId, Vector3 feet)
    {
        var holder = new Node3D { Name = "SheetCharacter", Transform = new Transform3D(Basis.Identity, feet) };
        var built = CharacterSheetPoses.BuildBody?.Invoke(new CharacterSheetBuild(info.Folder, tileId, _world));
        if (built != null)
        {
            holder.AddChild(built.Root);
            AddChild(holder);
            return new Character { Holder = holder, Visual = built.Root, Look = built.Look };
        }
        var visual = LoadGlb(System.IO.Path.Combine(info.Folder, "character.glb"));
        holder.AddChild(visual);
        AddChild(holder);
        var look = CharacterLook.Apply(visual, CharacterLook.PartsFromJson(info.Json), holder);
        return new Character { Holder = holder, Visual = visual, Look = look };
    }

    private static Node3D LoadGlb(string path)
    {
        var document = new GltfDocument();
        var state = new GltfState();
        var error = document.AppendFromBuffer(System.IO.File.ReadAllBytes(path), "", state);
        if (error != Error.Ok) throw new InvalidOperationException($"{path} could not be read ({error})");
        return document.GenerateScene(state) as Node3D ?? throw new InvalidOperationException($"{path} has no 3D scene");
    }

    /// <summary>A tile's set: where the feet stand, the props around them, and where the camera looks from (relative to the feet).</summary>
    private sealed record TileSet(Vector3 Feet, CharacterSheetStage Stage, List<Node3D> Props, Vector3 EyeOffset, Vector3 LookOffset, float FovDeg);

    private TileSet Set(string tileId, CharacterInfo info)
    {
        var floats = info.Motion == "float";
        var lift = floats ? FloatLiftM : 0f;
        var props = new List<Node3D>();
        var frontEye = new Vector3(0.15f, 0.1f, -0.23f);
        var middle = new Vector3(0f, 0.05f, 0f);
        // A stride swings the legs fore and aft, which a view from the front hides: travelling, it is seen more from the side.
        // (A waddle's roll and a hop's height read from the front.)
        var travelEye = info.Motion == "stride" ? new Vector3(0.22f, 0.085f, -0.14f) : frontEye;
        switch (tileId)
        {
            case "slope_30":
            {
                // Across the slope (it rises to the character's right, +X), so feet on legs plant at two heights and a body
                // without legs rolls to the ground; seen from the front, low, from the downhill side.
                var surface = Station + new Vector3(0f, 0.06f, 0f);
                var tan = Mathf.Tan(Mathf.DegToRad(SlopeDeg));
                // A floating body hovers clear of the slope under its whole width.
                var feet = surface + Vector3.Up * (floats ? lift + tan * Mathf.Max(Mathf.Abs(info.Bounds.Position.X), Mathf.Abs(info.Bounds.End.X)) : 0f);
                var tilt = new Basis(Vector3.Back, Mathf.DegToRad(SlopeDeg));
                var normal = tilt * Vector3.Up;
                props.Add(Prop("Slope", new Vector3(0.44f, 0.02f, 0.36f), new Transform3D(tilt, surface - normal * 0.01f), MaterialLibrary.For("wood", new Color("9a6f45")), collide: true));
                return new TileSet(feet, new CharacterSheetStage(feet, normal, null, null), props, new Vector3(-0.1f, 0.075f, -0.3f), middle, 36f);
            }
            case "wading":
            {
                // At the shins: a 10 cm character's knees are 2 to 2.5 cm up.
                const float depth = 0.02f;
                // A floating body hovers over the water, never in it.
                var feet = Station + Vector3.Up * (floats ? depth + lift : 0f);
                var water = new StandardMaterial3D
                {
                    ResourceName = "sheet water", AlbedoColor = new Color(0.36f, 0.56f, 0.6f, 0.55f), Roughness = 0.08f,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                };
                props.Add(Prop("Water", new Vector3(0.6f, depth, 0.6f), new Transform3D(Basis.Identity, Station + Vector3.Up * (depth / 2)), water, shadow: false));
                return new TileSet(feet, new CharacterSheetStage(feet, Vector3.Up, Station.Y + depth, null), props, travelEye, middle, 36f);
            }
            case "climbing":
            {
                var feet = Station + new Vector3(0f, 0.04f + lift, 0f);
                var wallZ = feet.Z + info.Bounds.Position.Z - 0.004f;
                props.Add(Prop("Wall", new Vector3(0.6f, 0.36f, 0.04f), new Transform3D(Basis.Identity, new Vector3(feet.X, 0.18f, wallZ - 0.02f)), MaterialLibrary.For("plaster", new Color("c9bfae")), collide: true));
                // From its right and a little behind, so the body against the face and the hands reaching up it both show.
                return new TileSet(feet, new CharacterSheetStage(feet, Vector3.Up, null, new Plane(Vector3.Back, wallZ)), props, new Vector3(0.25f, 0.1f, 0.14f), new Vector3(0f, 0.055f, -0.01f), 36f);
            }
            case "ninth" when !floats:
            {
                var feet = Station + Vector3.Up * lift;
                return new TileSet(feet, new CharacterSheetStage(feet, Vector3.Up, null, null), props, new Vector3(0.06f, 0.075f, -0.15f), new Vector3(0f, 0.06f, 0f), 32f);
            }
            case "ninth":
            {
                // Floating and casting: from the front, a little to its left, where its arm is; the spot it casts at is off to
                // its left, in the frame, so the lean, the arm's swing and the spark all read.
                var feet = Station + Vector3.Up * lift;
                var spot = feet + new Vector3(-0.16f, 0.08f, -0.06f);
                return new TileSet(feet, new CharacterSheetStage(feet, Vector3.Up, null, null, spot), props, new Vector3(-0.07f, 0.12f, -0.34f), new Vector3(-0.05f, 0.06f, 0f), 36f);
            }
            default:
            {
                var feet = Station + Vector3.Up * lift;
                return new TileSet(feet, new CharacterSheetStage(feet, Vector3.Up, null, null), props, tileId is "walk" or "run" ? travelEye : frontEye, middle, 36f);
            }
        }
    }

    /// <summary>A box prop; with collide, a static body on the world's collision layer, so feet, rays and a tumble find it.</summary>
    private Node3D Prop(string name, Vector3 size, Transform3D transform, Material material, bool shadow = true, bool collide = false)
    {
        var mesh = new MeshInstance3D
        {
            Name = "Sheet" + name, Mesh = new BoxMesh { Size = size }, MaterialOverride = material,
            CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (!collide)
        {
            mesh.Transform = transform;
            AddChild(mesh);
            return mesh;
        }
        var body = new StaticBody3D { Name = "Sheet" + name, CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Transform = transform };
        body.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        mesh.Name = "Mesh";
        body.AddChild(mesh);
        AddChild(body);
        return body;
    }

    private static string LabelFor(string tileId, string label, CharacterInfo info, CharacterSheetPoseStatus status, string note)
    {
        var floats = info.Motion == "float";
        if (tileId == "ninth") label = floats ? "floating and casting (the halo lit)" : "a close look";
        // A floating body never lands, climbs, swims or tumbles (RUN-2-CHARACTERS.md): the tile shows it floating, and says so.
        var never = floats && tileId is "hop_landing" or "climbing" or "wading" or "tumble" ? " (a floating body never does this: it floats)" : "";
        var said = status == CharacterSheetPoseStatus.Rest ? " - rest pose" : note.Length > 0 ? ": " + note : "";
        return $"{label}{never}{said}";
    }

    private async Task Sheet(string id, string folder, string outDir, Vector2I tileSize)
    {
        var info = ReadInfo(folder);
        var tiles = new List<(Image Image, string Label)>();
        CharacterLookResult? lookResult = null;
        var posedTiles = 0;
        var floats = info.Motion == "float";
        var deltaS = 1f / Engine.PhysicsTicksPerSecond;
        foreach (var (tileId, tileLabel) in CharacterSheetPoses.Tiles)
        {
            HideAvatars();
            var set = Set(tileId, info);
            var casting = tileId == "ninth" && floats;
            var glowId = "character_sheet_halo";
            var companion = _world.Companion;
            var savedAnchors = new List<(StringName Meta, Variant? Value)>();
            if (casting)
            {
                // The halo through the real look, on the companion's body (its height and aura rules); the companion stands where
                // the drawing does, facing the same way, so a cast it plays leans the drawing toward the spot.
                companion.TryTeleportTo(set.Feet - Vector3.Up * FloatLiftM);
                companion.Rotation = Vector3.Zero;
                companion.ResetPhysicsInterpolation();
                for (var i = 0; i < 20; i++) await NextFrame();
            }
            var character = Build(info, tileId, set.Feet);
            lookResult ??= character.Look;
            var drawn = character.Visual.GetNodeOrNull<DrawnGubble>(GubbleCharacter.NodeName);
            if (casting)
            {
                // The look puts the halo (and its light) on the anchors the companion names: here, this drawing's own.
                if (drawn != null)
                    foreach (var (meta, anchor) in new (StringName, Node3D?)[] { (GlowLook.HaloAnchorMeta, drawn.HaloAnchor), (GlowLook.LightAnchorMeta, drawn.LightAnchor) })
                    {
                        savedAnchors.Add((meta, companion.HasMeta(meta) ? (Variant?)companion.GetMeta(meta) : null));
                        if (anchor != null) companion.SetMeta(meta, anchor);
                        else if (companion.HasMeta(meta)) companion.RemoveMeta(meta);
                    }
                _look.StartGlow(glowId, companion, Vector3.Zero, 0.6f, 0.6f);
            }
            // The film is unshaded: it takes the place's light from the look's own estimate, as the game would give it.
            var level = _look.LightLevelAt(set.Feet + Vector3.Up * 0.05f);
            CharacterLook.SetLightLevel(character.Visual, level);
            _camera.Fov = set.FovDeg;
            _camera.LookAtFromPosition(set.Feet + set.EyeOffset, set.Feet + set.LookOffset, Vector3.Up);
            _look.FrameCamera(_camera, set.Feet + set.LookOffset);
            // New props' collision is in the physics space from the next step: let two pass before anything casts at them.
            await NextPhysicsFrame();
            await NextPhysicsFrame();

            var context = new CharacterSheetPoseContext(tileId, character.Holder, character.Visual, folder, info.Motion, set.Stage, _world, _camera, deltaS);
            var status = CharacterSheetPoseStatus.Rest;
            var maxTicks = (int)Math.Ceiling(CharacterSheetPoses.MaxTileSeconds / deltaS);
            var capped = false;
            for (var tick = 0; tick < maxTicks; tick++)
            {
                await NextPhysicsFrame();
                context.Tick = tick;
                status = CharacterSheetPoses.TryPose(context);
                _look.FrameCamera(_camera, character.Holder.GlobalPosition + set.LookOffset);
                if (status != CharacterSheetPoseStatus.Posing) break;
                capped = tick == maxTicks - 1;
            }
            if (status == CharacterSheetPoseStatus.Rest)
            {
                // No pose: the rest pose, the look given a moment to settle on the new framing.
                var started = Time.GetTicksUsec();
                for (var frame = 0; frame < 30 || (Time.GetTicksUsec() - started) / 1e6 < 0.8; frame++)
                {
                    if (casting && drawn == null) character.Holder.GlobalPosition = companion.GlobalPosition;
                    _look.FrameCamera(_camera, character.Holder.GlobalPosition + set.LookOffset);
                    await NextFrame();
                }
            }
            else posedTiles++;
            var note = context.Note + (capped ? " (taken at the time limit)" : "") + (context.Shows == false ? $" (not shown: {context.Missing})" : "");
            var text = LabelFor(tileId, tileLabel, info, status, note) + (character.Look.Count(CharacterSurfaceRole.Bubble) > 0 ? $" (light {level:0.00})" : "");
            if (_motionOnly)
            {
                var shown = status == CharacterSheetPoseStatus.Ready && !capped && context.Shows != false;
                _motionTiles++;
                if (shown) _motionShown++;
                GD.Print($"CHARACTER_SHEET_MOTION character={id} tile={tileId} {(shown ? "ok" : "FAIL")} status={status} ticks={context.Tick + 1} label=\"{text}\"");
                tiles.Add((Image.CreateEmpty(1, 1, false, Image.Format.Rgb8), text));
            }
            else
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var image = _target.GetTexture().GetImage();
                image.Convert(Image.Format.Rgb8);
                tiles.Add((image, text));
            }
            _look.StopGlow(CharacterSheetPoses.CastGlowId);
            if (casting)
            {
                _look.StopGlow(glowId);
                foreach (var (meta, value) in savedAnchors)
                    if (value is { } kept) companion.SetMeta(meta, kept);
                    else if (companion.HasMeta(meta)) companion.RemoveMeta(meta);
                companion.TryTeleportTo(new Vector3(1.75f, 0f, -1.2f));
            }
            foreach (var prop in set.Props) prop.QueueFree();
            character.Holder.QueueFree();
            await NextFrame();
        }
        if (_motionOnly) return;
        var sheet = await Compose(info.Name, info.Motion, posedTiles == tiles.Count, tiles, tileSize);
        var file = $"sheet_{id}.png";
        sheet.SavePng(System.IO.Path.Combine(outDir, file));
        var look = lookResult!;
        _sheets.Add(new Dictionary<string, object>
        {
            ["character"] = id, ["name"] = info.Name, ["motion"] = info.Motion, ["file"] = file,
            ["tiles"] = tiles.Select(t => t.Label).ToArray(),
            ["surfaces"] = Enum.GetValues<CharacterSurfaceRole>().ToDictionary(r => r.ToString().ToLowerInvariant(), r => look.Count(r)),
        });
        GD.Print($"CHARACTER_SHEET character={id} file={file} posed={posedTiles}/{tiles.Count} bubble={look.Count(CharacterSurfaceRole.Bubble)} ink={look.Count(CharacterSurfaceRole.Ink)} effect={look.Count(CharacterSurfaceRole.Effect)} felt={look.Count(CharacterSurfaceRole.Plain)}");
        foreach (var (_, label) in tiles) GD.Print($"CHARACTER_SHEET_TILE character={id} {label}");
    }

    /// <summary>The nine tiles in three rows with their labels under a title, drawn by a 2D viewport and read back.</summary>
    private async Task<Image> Compose(string title, string motion, bool posed, List<(Image Image, string Label)> tiles, Vector2I tileSize)
    {
        const int titleH = 44, labelH = 46, columns = 3;
        var rows = (tiles.Count + columns - 1) / columns;
        var size = new Vector2I(tileSize.X * columns, titleH + rows * (labelH + tileSize.Y));
        var canvas = new SubViewport { Size = size, Disable3D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(canvas);
        var background = new ColorRect { Color = new Color("1d1f24"), Size = size };
        canvas.AddChild(background);
        var what = posed ? $"{motion}: the rig, its look and every packet's motion, run from scripted body states" : "the look (L8), rest pose where no pose is registered";
        var heading = new Label { Text = $"{title} ({what}); {Arg("commit", "")}", Position = new Vector2(12, 8), Size = new Vector2(size.X - 24, titleH - 8) };
        heading.AddThemeFontSizeOverride("font_size", 22);
        heading.AddThemeColorOverride("font_color", new Color("f2efe6"));
        canvas.AddChild(heading);
        for (var i = 0; i < tiles.Count; i++)
        {
            var x = i % columns * tileSize.X;
            var y = titleH + i / columns * (labelH + tileSize.Y);
            var label = new Label
            {
                Text = $"{i + 1}. {tiles[i].Label}", Position = new Vector2(x + 10, y + 3), Size = new Vector2(tileSize.X - 20, labelH - 4),
                AutowrapMode = TextServer.AutowrapMode.WordSmart, ClipText = true,
            };
            label.AddThemeFontSizeOverride("font_size", 15);
            label.AddThemeConstantOverride("line_spacing", -2);
            label.AddThemeColorOverride("font_color", new Color("d9d4c7"));
            canvas.AddChild(label);
            canvas.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(tiles[i].Image), Position = new Vector2(x, y + labelH), Size = tileSize });
        }
        for (var i = 0; i < 3; i++) await NextFrame();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = canvas.GetTexture().GetImage();
        image.Convert(Image.Format.Rgb8);
        canvas.QueueFree();
        return image;
    }

    // ---------- what a bubble costs ----------

    /// <summary>
    /// The bubble shader with its render modes changed, for the cost record only (--variants=true): the shipped film ("shipped"),
    /// one skin, and lit versions, so the budget's trade is measured rather than guessed. Every variant keeps the same code.
    /// </summary>
    private static readonly (string Id, string[] Remove, string[] Add)[] Variants =
    {
        ("shipped", Array.Empty<string>(), Array.Empty<string>()),
        ("one_skin", new[] { "cull_disabled" }, new[] { "cull_back" }),
        ("lit", new[] { "unshaded", "shadows_disabled" }, Array.Empty<string>()),
        ("lit_no_shadows", new[] { "unshaded" }, Array.Empty<string>()),
        ("lit_no_shadows_one_skin", new[] { "unshaded", "cull_disabled" }, new[] { "cull_back" }),
        ("lit_no_shadows_no_ambient_one_skin", new[] { "unshaded", "cull_disabled" }, new[] { "cull_back", "ambient_light_disabled" }),
    };

    private static Shader VariantOf(Shader shipped, string[] remove, string[] add)
    {
        var code = shipped.Code;
        var line = System.Text.RegularExpressions.Regex.Match(code, @"render_mode([^;]*);");
        var modes = line.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Except(remove).Concat(add);
        return new Shader { Code = code.Replace(line.Value, "render_mode " + string.Join(", ", modes) + ";") };
    }

    /// <summary>
    /// The GPU time of a 1920 x 1080 frame of the character with its bubble parts shown and hidden, in alternating blocks, at a
    /// play distance (about 0.45 m, the companion camera's) and close up (0.15 m, beside it). Only characters with bubble parts.
    /// </summary>
    private async Task BubbleCost(string id, string folder, string outDir)
    {
        var info = ReadInfo(folder);
        var feet = Station + Vector3.Up * (info.Motion == "float" ? FloatLiftM : 0f);
        var character = Build(info, "bubble_cost", feet);
        var bubbles = character.Look.Of(CharacterSurfaceRole.Bubble).ToList();
        var meshes = bubbles.Select(s => s.Mesh).Distinct().ToList();
        if (meshes.Count == 0)
        {
            character.Holder.QueueFree();
            return;
        }
        CharacterLook.SetLightLevel(character.Visual, _look.LightLevelAt(feet + Vector3.Up * 0.05f));
        var previousSize = _target.Size;
        _target.Size = new Vector2I(1920, 1080);
        var rid = _target.GetViewportRid();
        var shipped = ((ShaderMaterial)bubbles[0].Material).Shader;
        var variants = Arg("variants", "false") == "true" ? Variants : Variants.Take(1).ToArray();
        foreach (var (variant, remove, add) in variants)
        {
            var shader = variant == "shipped" ? shipped : VariantOf(shipped, remove, add);
            foreach (var surface in bubbles)
            {
                var material = (ShaderMaterial)surface.Material;
                var values = shipped.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToDictionary(n => n, n => material.GetShaderParameter(n));
                material.Shader = shader;
                foreach (var (name, value) in values) material.SetShaderParameter(name, value);
            }
            foreach (var (framing, eye, fov) in new[] { ("play", new Vector3(0.25f, 0.1f, -0.38f), 36f), ("close", new Vector3(0.06f, 0.07f, -0.13f), 50f) })
            {
                _camera.Fov = fov;
                _camera.LookAtFromPosition(feet + eye, feet + new Vector3(0f, 0.05f, 0f), Vector3.Up);
                _look.FrameCamera(_camera, feet + new Vector3(0f, 0.05f, 0f));
                for (var i = 0; i < 60; i++) await NextFrame();
                var on = new List<double>();
                var off = new List<double>();
                Image? shown = null, hidden = null;
                for (var block = 0; block < 5; block++)
                    foreach (var visible in new[] { true, false })
                    {
                        foreach (var mesh in meshes) mesh.Visible = visible;
                        for (var i = 0; i < 10; i++) await NextFrame();
                        for (var i = 0; i < 60; i++)
                        {
                            await NextFrame();
                            (visible ? on : off).Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
                        }
                        if (block == 0)
                        {
                            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                            var image = _target.GetTexture().GetImage();
                            image.Convert(Image.Format.Rgb8);
                            if (visible) shown = image; else hidden = image;
                        }
                    }
                foreach (var mesh in meshes) mesh.Visible = true;
                var coverage = Coverage(shown!, hidden!);
                var cost = on.Average() - off.Average();
                var noise = Math.Sqrt(Variance(on) / on.Count + Variance(off) / off.Count);
                var file = $"bubble_{id}_{framing}_{variant}.png";
                shown!.SavePng(System.IO.Path.Combine(outDir, file));
                _costs.Add(new Dictionary<string, object>
                {
                    ["character"] = id, ["variant"] = variant, ["framing"] = framing, ["resolution"] = new[] { 1920, 1080 },
                    ["gpu_ms_with_bubble"] = Math.Round(on.Average(), 4), ["gpu_ms_without_bubble"] = Math.Round(off.Average(), 4),
                    ["bubble_ms"] = Math.Round(cost, 4), ["standard_error_ms"] = Math.Round(noise, 4), ["frames_each"] = on.Count,
                    ["bubble_screen_fraction"] = Math.Round(coverage, 4), ["image"] = file,
                });
                GD.Print($"CHARACTER_SHEET_BUBBLE_COST character={id} variant={variant} framing={framing} bubble_ms={cost:0.0000} se_ms={noise:0.0000} with={on.Average():0.000} without={off.Average():0.000} screen={coverage:0.000}");
            }
        }
        _target.Size = previousSize;
        character.Holder.QueueFree();
        await NextFrame();
    }

    private static double Variance(List<double> values)
    {
        var mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Count - 1);
    }

    /// <summary>The share of pixels the bubble changes (more than 2/255 in a channel).</summary>
    private static double Coverage(Image a, Image b)
    {
        var data = a.GetData();
        var other = b.GetData();
        var changed = 0;
        for (var i = 0; i + 2 < data.Length && i + 2 < other.Length; i += 3)
            if (Math.Abs(data[i] - other[i]) > 2 || Math.Abs(data[i + 1] - other[i + 1]) > 2 || Math.Abs(data[i + 2] - other[i + 2]) > 2) changed++;
        return changed / (double)(data.Length / 3);
    }
}
