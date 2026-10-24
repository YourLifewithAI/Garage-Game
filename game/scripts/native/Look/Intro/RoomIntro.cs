using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Look.Fauna;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look.Intro;

/// <summary>
/// The room-to-island intro (the founder, 8 October; the walls become the coast, the ninth session): on every load, about 15 s of the
/// room, roughly painted, turning into its island, skippable at any moment. The story and its times are IntroTimeline's, the camera's
/// one move IntroCameraPath's. What it animates is the island itself, never a second one:
/// <list type="bullet">
/// <item>the land's own tiles, drawn by painterly_land.gdshader between the room's heights (IntroField, from the source boxes and floor)
/// and their own, its colours from the room's to the island's;</item>
/// <item>the copies, stones and cottages, grown in from nothing by the same shader as a wave passes them, nearest the camera first;</item>
/// <item>the island's water, its sea and its ponds, drawn lower and rising (its meshes' positions; never their colliders);</item>
/// <item>and, built for the intro only, the source room's walls and ceiling (IntroShell), which dissolve and slump away.</item>
/// </list>
/// The shader is driven by Godot's global shader parameters (game/project.godot's [shader_globals]): one call a frame moves the whole
/// island, and at rest the land draws exactly as without the intro.
///
/// It never changes the world: nothing here sends a command, writes a save or touches the command host. The bodies stand on the real
/// ground throughout (only the drawing moves); while the intro plays the player's input is off, the HUD is hidden and every input
/// event comes here first (any key, a click or a gamepad button skips; the rest is swallowed). When it ends, played out or skipped,
/// everything it changed is put back exactly: the materials' marks, the cull margins, the water's positions, what it hid, the look's
/// view and focus, the play camera, the HUD, the input and the globals. It is never played headless, by a boot check
/// (--quit-after-ready) or with the player's setting "Play the intro" off (RoomHud.LoadPlayIntro), so ROOM_WORLD_READY is never later
/// for it: it starts after.
/// </summary>
public partial class RoomIntro : Node
{
    public const string NodeName = "RoomIntro";
    /// <summary>The marks the shader reads (intro_part): 1 the land, 3 still (left as it is); 0, the default, grows in.</summary>
    public const float PartLand = 1f, PartStill = 3f;
    /// <summary>A slow frame moves the story at most this far, so a hitch never skips a part of it.</summary>
    public const double MaxStepS = 1.0 / 20.0;
    /// <summary>The sea and the ponds start just under the lowest of the island's ground (the shelf's foot), by this much.</summary>
    public const float SeaDropMarginM = 0.1f;
    /// <summary>The growing wave's soft edge, as a share of the room's longer side.</summary>
    public const float GrowEdgeShare = 0.12f;
    /// <summary>
    /// On an island whose second level of land detail is this fine for its size (a share of the room's longer side), the land is drawn
    /// at that level until the camera comes down to the player: the room and the overview never need the finest level, which is
    /// built for a 10 cm body's feet and is most of the land's vertices (the 10x garage: 40 cm instead of 10 cm cells).
    /// </summary>
    public const float CoarseLandShare = 0.01f;
    /// <summary>The land's haze starts this much farther out for every metre the camera is from the play camera, up to HazeLiftMaxM.</summary>
    public const float HazeLiftPerM = 0.5f, HazeLiftMaxM = 90f;
    /// <summary>Tests: draw the land coarse (true) or fine (false) whatever the island's size; null follows CoarseLandShare.</summary>
    internal static bool? ForceCoarseLand { get; set; }

    public static readonly StringName RiseName = "intro_rise", AreaName = "intro_area", GrowName = "intro_grow", StartName = "intro_start", ColourName = "intro_colour";
    private static readonly StringName PartName = "intro_part";
    /// <summary>The globals at rest, as game/project.godot declares them.</summary>
    public static readonly Vector4 RiseAtRest = new(1, 1, 0, 0);

    /// <summary>Whether an intro holds the globals now (false at rest), for tests: only one intro plays at a time.</summary>
    public static bool GlobalsPlaying { get; private set; }

    public RoomWorld World { get; private set; } = null!;
    /// <summary>The story's clock (seconds since the room first showed).</summary>
    public double Seconds { get; private set; }
    /// <summary>Tests and captures hold the clock (the picture keeps drawing; Seek moves it).</summary>
    public bool Paused { get; set; }
    public IntroMoment Moment { get; private set; } = IntroTimeline.At(0);
    /// <summary>The data is read and the island marked: the story is playing (or held).</summary>
    public bool Playing { get; private set; }
    public bool Finished { get; private set; }
    /// <summary>How it ended: "played", "skipped", or why it could not play.</summary>
    public string EndReason { get; private set; } = "";
    public IntroField? Field { get; private set; }
    public IntroSource? Source { get; private set; }
    public IntroCameraPath? Path { get; private set; }
    public Camera3D? Camera { get; private set; }
    public IntroShell? Shell { get; private set; }
    /// <summary>The land's instances it marked, the still ones, and the water it lowers (counts, for tests and the log).</summary>
    public int LandMarked { get; private set; }
    public int StillMarked { get; private set; }
    public int WaterMoved => _water.Count;
    /// <summary>The land's meshes drawn at their second level until the arrival (0 on a small island).</summary>
    public int CoarseHeld => _coarse.Count;
    /// <summary>Raised once when the intro ends, after everything is put back.</summary>
    public event Action<RoomIntro>? Ended;

    private RoomHud? _hud;
    private Camera3D? _playCamera;
    private Task<(IntroSource Source, IntroField Field)?>? _preparing;
    private CanvasLayer? _coverLayer;
    private ColorRect? _cover;
    private ImageTexture? _startTexture, _colourTexture;
    private readonly List<(GeometryInstance3D Node, float Margin)> _marked = new();
    private readonly List<(Node3D Node, Vector3 Position)> _water = new();
    private readonly List<(Node3D Node, bool Visible)> _bodies = new();
    private readonly List<(Node3D Node, bool Visible)> _life = new();
    private readonly List<(MeshInstance3D Mesh, StandardMaterial3D Material, double Born)> _puffs = new();
    private bool _inputBefore = true;
    private bool _lookHeld;
    private LookView? _viewBefore;
    private Vector3? _focusBefore;
    private float _seaDrop;
    private Vector2 _growCentre;
    private float _growEdge, _growReach, _focusBeyond;
    private bool _puffed;
    private (Node3D Node, bool Visible)? _copies;
    private readonly List<(GeometryInstance3D Mesh, float Begin, float BeginMargin, float End, float EndMargin)> _coarse = new();
    private HazeLift? _hazeLift;

    /// <summary>
    /// The land's haze pushed out while the camera is far from the player, so the room and the overview are seen clearly and the sea
    /// shows round the island: after the open sea has set the fog each frame (it does so every frame, from its own data, so nothing
    /// needs putting back), the fog starts farther out by the camera's distance from the play camera (its far end stays). None of it at the hand-over.
    /// </summary>
    private sealed partial class HazeLift : Node
    {
        public Func<float>? Lift;
        public Godot.Environment? Environment;
        public override void _Process(double delta)
        {
            if (Environment == null || !IsInstanceValid(Environment) || !Environment.FogEnabled || Lift?.Invoke() is not { } lift || lift <= 0f) return;
            // Only its start moves, and the far plane turns wholly to the horizon's colour: seen from high up the sea ends at the far plane
            // below the true horizon, an edge the play views never see (their eye is a few centimetres over the water).
            Environment.FogDepthBegin = Math.Min(Environment.FogDepthBegin + lift, 0.8f * Environment.FogDepthEnd);
            Environment.FogDensity = Mathf.Lerp(Environment.FogDensity, 1f, Math.Clamp(2f * lift / HazeLiftMaxM, 0f, 1f));
        }
    }

    /// <summary>The intro on a room that has one: null (and why, in the log) when it is not to be played.</summary>
    public static RoomIntro? TryStart(RoomWorld world)
    {
        if (WhyNot(world, RoomHud.ProfilePath) is { } why)
        {
            if (why.Length > 0) GD.Print("INTRO: not played: " + why);
            return null;
        }
        return Start(world);
    }

    /// <summary>
    /// Why a world plays no intro, or null when it does: never headless (nothing is drawn; tests and boot checks), never for a boot
    /// check (--quit-after-ready), never with the player's setting off, never for a room that is not a landscape. "" means quietly.
    /// </summary>
    public static string? WhyNot(RoomWorld world, string profilePath) =>
        WhyNot(DisplayServer.GetName() == "headless", OS.GetCmdlineUserArgs(), RoomHud.LoadPlayIntro(profilePath),
            world.WorldReady && world.Room != null && world.Room.Shell.Any(p => p.Role == "ground"), Of(world) != null);

    /// <summary>The rule itself, from its inputs (the headless suite checks every branch of it).</summary>
    public static string? WhyNot(bool headless, IEnumerable<string> userArgs, bool settingOn, bool landscapeReady, bool alreadyPlaying)
    {
        if (headless) return "";
        if (userArgs.Any(a => a.StartsWith("--quit-after-ready=", StringComparison.Ordinal))) return "";
        if (!settingOn) return "the player's setting \"Play the intro\" is off";
        if (!landscapeReady) return "the room is not a ready landscape (no ground)";
        if (alreadyPlaying) return "an intro is already playing";
        return null;
    }

    /// <summary>Play the intro on a ready world now, whatever the setting (the hook above, tests and captures).</summary>
    public static RoomIntro Start(RoomWorld world)
    {
        var intro = new RoomIntro { Name = NodeName, World = world, ProcessPriority = -100, ProcessMode = ProcessModeEnum.Always };
        world.AddChild(intro);
        return intro;
    }

    /// <summary>The intro playing in a world, if any.</summary>
    public static RoomIntro? Of(Node world) => world.GetNodeOrNull<RoomIntro>(NodeName) is { Finished: false } intro ? intro : null;

    public override void _Ready()
    {
        _hud = World.GetChildren().OfType<RoomHud>().FirstOrDefault();
        _playCamera = GetViewport().GetCamera3D();
        Cover();
        var room = World.Room;
        var area = new Rect2(room.Bounds.Position.X, room.Bounds.Position.Z, room.Bounds.Size.X, room.Bounds.Size.Z);
        // Off the main thread: the manifest read again and hashed (about 1 MB on the 10x island), and the field (a few 10,000 cells).
        _preparing = Task.Run<(IntroSource, IntroField)?>(() =>
        {
            var source = IntroSource.Load(room);
            if (source == null) return null;
            var bounds = new Rect2(new Vector2(source.Bounds.Position.X, source.Bounds.Position.Z), new Vector2(source.Bounds.Size.X, source.Bounds.Size.Z));
            return (source, IntroField.Build(source, area.Merge(bounds)));
        });
    }

    public override void _Process(double delta)
    {
        if (Finished) return;
        if (!Playing)
        {
            if (_preparing is not { IsCompleted: true } prepared) return;
            if (prepared.IsFaulted) { Finish("its data could not be read: " + (prepared.Exception?.InnerException?.Message ?? "unknown")); return; }
            if (prepared.Result is not { } data) { Finish("the room has no source (extensions.x_landscape_source)"); return; }
            try { SetUp(data.Source, data.Field); }
            catch (Exception error)
            {
                Finish("it could not be set up: " + error.Message);
                return;
            }
            // The clock starts on the next frame: this one may carry the set-up's own cost.
            Apply(0);
            return;
        }
        if (!Paused) Seconds += Math.Min(delta, MaxStepS);
        Apply(Seconds);
        if (Moment.Done) Finish("played");
    }

    /// <summary>Any key, a click or a gamepad button skips; every other event is swallowed while the intro plays.</summary>
    public override void _Input(InputEvent input)
    {
        if (Finished) return;
        if (IsSkip(input)) Skip();
        GetViewport().SetInputAsHandled();
    }

    public static bool IsSkip(InputEvent input) => input switch
    {
        InputEventKey key => key.Pressed && !key.Echo,
        InputEventMouseButton button => button.Pressed,
        InputEventJoypadButton pad => pad.Pressed,
        InputEventScreenTouch touch => touch.Pressed,
        _ => false,
    };

    /// <summary>Straight to play, the world exactly as normal.</summary>
    public void Skip() => Finish("skipped");

    /// <summary>Tests and captures: the story at a moment (the clock moves there; the picture follows on the next frame).</summary>
    public void Seek(double seconds)
    {
        Seconds = Math.Clamp(seconds, 0.0, IntroTimeline.DurationS);
        if (Playing) Apply(Seconds);
    }

    // ---------- the room shown, the HUD and the bodies held ----------

    /// <summary>Black over everything while the data is read; the HUD hidden; the bodies out of sight; the player's input off.</summary>
    private void Cover()
    {
        _coverLayer = new CanvasLayer { Name = "IntroCover", Layer = 90 };
        _cover = new ColorRect { Name = "Black", Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        _coverLayer.AddChild(_cover);
        AddChild(_coverLayer);
        _cover.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _hud?.BeginIntro();
        foreach (var body in new Node3D?[] { World.Player, World.Companion })
            if (body != null && IsInstanceValid(body))
            {
                _bodies.Add((body, body.Visible));
                body.Visible = false;
            }
        if (World.Player != null && IsInstanceValid(World.Player))
        {
            _inputBefore = World.Player.InputEnabled;
            World.Player.SetInputEnabled(false);
        }
    }

    // ---------- the island marked ----------

    private void SetUp(IntroSource source, IntroField field)
    {
        Source = source;
        Field = field;
        var room = World.Room;
        var longest = Math.Max(source.Bounds.Size.X, source.Bounds.Size.Z);
        _startTexture = ImageTexture.CreateFromImage(field.StartImage());
        _colourTexture = ImageTexture.CreateFromImage(field.ColourImage());
        // The land, still parts and the copies' default: every painterly land instance in the world is the land (a ground part's), a
        // thing that grows in (an object, a copy: left at the default), or still (the sea floor, the open sea's bed, anything else).
        var landTop = field.FloorY;
        foreach (var node in World.FindChildren("*", "GeometryInstance3D", true, false))
        {
            if (node is not GeometryInstance3D instance || !PaintedLand(instance)) continue;
            if (OwnerRole(instance) == "ground")
            {
                var box = instance.GlobalTransform * instance.GetAabb();
                landTop = Math.Max(landTop, box.End.Y);
                var margin = Math.Max(0f, Math.Max(field.TopY - box.End.Y, box.Position.Y - field.FloorY));
                _marked.Add((instance, instance.ExtraCullMargin));
                instance.SetInstanceShaderParameter(PartName, PartLand);
                instance.ExtraCullMargin = Math.Max(instance.ExtraCullMargin, margin + 0.01f);
                LandMarked++;
            }
            else if (!GrowsIn(instance))
            {
                _marked.Add((instance, instance.ExtraCullMargin));
                instance.SetInstanceShaderParameter(PartName, PartStill);
                StillMarked++;
            }
        }
        // The water drawn lower (its meshes, never the colliders beside them under the same part).
        var seaLevel = room.Sea?.LevelM ?? field.FloorY;
        _seaDrop = Math.Max(0.2f, seaLevel - room.Bounds.Position.Y + SeaDropMarginM);
        foreach (var node in World.Built.FindChildren("*", "MeshInstance3D", true, false))
            if (node is MeshInstance3D mesh && mesh.HasMeta(LookDirector.WaterMeta))
                _water.Add((mesh, mesh.Position));
        // The fish wait for their water.
        foreach (var life in World.GetChildren().OfType<PondLife>().Cast<Node3D>().Append(World.Look?.OpenSea?.Life).OfType<Node3D>())
            _life.Add((life, life.Visible));
        // The source room's walls and ceiling.
        Shell = IntroShell.Build(source);
        AddChild(Shell.Root);
        // The land at its second level of detail on a big island, until the camera comes down to the player.
        if (room.Terrain is { LevelCount: >= 2 } terrain && (ForceCoarseLand ?? terrain.LevelGridM[1] <= CoarseLandShare * longest))
            foreach (var (node, _) in _marked)
                if (node.HasMeta(RoomBuilder.TerrainLevelMeta) && node.GetMeta(RoomBuilder.TerrainLevelMeta).AsInt32() is var level and (0 or 1))
                {
                    _coarse.Add((node, node.VisibilityRangeBegin, node.VisibilityRangeBeginMargin, node.VisibilityRangeEnd, node.VisibilityRangeEndMargin));
                    if (level == 0) { node.VisibilityRangeEnd = 0.001f; node.VisibilityRangeEndMargin = 0f; }
                    else { node.VisibilityRangeBegin = 0f; node.VisibilityRangeBeginMargin = 0f; }
                }
        // The copies wait for their phase: drawn at no size they would still cost their vertices (S12b: hundreds of thousands).
        if (World.Built.GetNodeOrNull<Node3D>(PlantCopiesView.ViewName) is { } copies)
        {
            _copies = (copies, copies.Visible);
            copies.Visible = false;
        }
        // The camera, from inside the room to the play camera, clear of the island's real ground (the far ground sight rays see).
        var player = World.Player;
        var end = PlayPose();
        var space = World.GetWorld3D().DirectSpaceState;
        var probeTop = Math.Max(landTop, room.Bounds.End.Y) + 10f;
        var probeBottom = room.Bounds.Position.Y - 5f;
        float Ground(float x, float z)
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(x, probeTop, z), new Vector3(x, probeBottom, z), RoomBuilder.SightMask));
            return hit.Count > 0 ? hit["position"].AsVector3().Y : field.FloorY;
        }
        Path = new IntroCameraPath(source.Bounds, player.GlobalPosition, end.Transform, landTop, field.StartAt, Ground);
        Camera = new Camera3D { Name = "IntroCamera", Fov = IntroCameraPath.StartFovDeg, Near = 0.2f, Far = World.Look?.FarPlaneM ?? 250f };
        AddChild(Camera);
        Camera.MakeCurrent();
        if (World.Look is { } look)
        {
            _lookHeld = true;
            _viewBefore = look.View;
            _focusBefore = look.FocusOverride;
            // The far blur of the play views: crisp from the lens to past what the camera looks at, only the far shore softening.
            look.View = LookView.Shoulder;
        }
        // The wave of growth starts under the camera as the plants' phase begins, and reaches every corner of the island.
        var plantsAt = Path.Pose(IntroTimeline.Window("plants").Start, end.Transform, end.Fov).Transform.Origin;
        _growCentre = new Vector2(plantsAt.X, plantsAt.Z);
        _growEdge = Math.Max(GrowEdgeShare * longest, 0.05f);
        var bounds = room.Bounds;
        _growReach = new[] { (bounds.Position.X, bounds.Position.Z), (bounds.End.X, bounds.Position.Z), (bounds.Position.X, bounds.End.Z), (bounds.End.X, bounds.End.Z) }
            .Max(c => _growCentre.DistanceTo(new Vector2(c.Item1, c.Item2))) + 2f * _growEdge;
        _focusBeyond = 0.25f * longest;
        if (World.Look?.Environment is { } environment && World.Look.OpenSea != null)
        {
            _hazeLift = new HazeLift { Name = "HazeLift", ProcessPriority = int.MaxValue, Environment = environment };
            _hazeLift.Lift = () => Camera != null && IsInstanceValid(Camera) && Playing
                ? Math.Clamp(HazeLiftPerM * Camera.GlobalPosition.DistanceTo(PlayPose().Transform.Origin), 0f, HazeLiftMaxM) : 0f;
            AddChild(_hazeLift);
        }
        RenderingServer.GlobalShaderParameterSet(AreaName, field.Area);
        RenderingServer.GlobalShaderParameterSet(StartName, _startTexture.GetRid());
        RenderingServer.GlobalShaderParameterSet(ColourName, _colourTexture.GetRid());
        GlobalsPlaying = true;
        Playing = true;
        GD.Print(FormattableString.Invariant(
            $"INTRO: playing on {room.RoomId}: {source.Boxes.Count} boxes, {source.Walls.Count} walls, {source.Ceilings.Count} ceilings; a {field.Width}x{field.Depth} field of {field.CellM:0.###} m cells; {LandMarked} land instances, {StillMarked} still, {_water.Count} water meshes {_seaDrop:0.##} m under; land top {landTop:0.#} m"));
    }

    /// <summary>Whether an instance wears the land's painterly material (the only one the intro's globals reach).</summary>
    private static bool PaintedLand(GeometryInstance3D instance)
    {
        static bool Land(Material? material) => material is ShaderMaterial shader && shader.HasMeta("landscape");
        if (Land(instance.MaterialOverride)) return true;
        if (instance is MeshInstance3D { Mesh: { } mesh } meshInstance)
            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                if (Land(meshInstance.GetSurfaceOverrideMaterial(surface)) || Land(mesh.SurfaceGetMaterial(surface))) return true;
        if (instance is MultiMeshInstance3D { Multimesh.Mesh: { } copies })
            for (var surface = 0; surface < copies.GetSurfaceCount(); surface++)
                if (Land(copies.SurfaceGetMaterial(surface))) return true;
        return false;
    }

    /// <summary>The surface role of the shell part an instance belongs to ("ground" for the land), or "".</summary>
    private static string OwnerRole(Node node)
    {
        for (var current = node; current != null; current = current.GetParent())
            if (current.HasMeta("surface_role")) return current.GetMeta("surface_role").AsString();
        return "";
    }

    /// <summary>What grows in: a room object (a cottage, a stone, the crate) and the copies (their view's MultiMeshes).</summary>
    private bool GrowsIn(GeometryInstance3D instance)
    {
        if (instance is MultiMeshInstance3D) return true;
        for (Node? current = instance; current != null && current != World; current = current.GetParent())
            if (current.HasMeta("asset_id")) return true;
        return false;
    }

    /// <summary>The play camera's pose (its live transform and field of view), or the player's place when there is none.</summary>
    private (Transform3D Transform, float Fov) PlayPose()
    {
        if (_playCamera != null && IsInstanceValid(_playCamera)) return (_playCamera.GlobalTransform, _playCamera.Fov);
        var player = World.Player;
        return (new Transform3D(player.GlobalBasis, player.GlobalPosition + player.GlobalBasis.Z * 0.3f + Vector3.Up * 0.1f), 68f);
    }

    // ---------- each frame ----------

    private void Apply(double seconds)
    {
        Moment = IntroTimeline.At(seconds);
        var moment = Moment;
        if (_cover != null) _cover.Color = new Color(0, 0, 0, moment.Cover);
        _hud?.SetSkipPrompt(moment.Prompt);
        RenderingServer.GlobalShaderParameterSet(RiseName, new Vector4(moment.Land, moment.Coast, 0f, 1f));
        RenderingServer.GlobalShaderParameterSet(GrowName, new Vector4(_growCentre.X, _growEdge, _growCentre.Y, moment.Grow * _growReach));
        Shell?.Apply(moment);
        var drop = Vector3.Down * (_seaDrop * (1f - moment.Sea));
        foreach (var (node, position) in _water)
            if (IsInstanceValid(node)) node.Position = position + drop;
        foreach (var (node, visible) in _life)
            if (IsInstanceValid(node)) node.Visible = visible && moment.LifeShown;
        if (_copies is { } copies && IsInstanceValid(copies.Node)) copies.Node.Visible = copies.Visible && moment.Grow > 0f;
        // The land's finest level is back as the camera comes down to the player.
        if (_coarse.Count > 0 && seconds >= IntroTimeline.Window("arrive").Start) RestoreLand();
        foreach (var (node, visible) in _bodies)
            if (IsInstanceValid(node)) node.Visible = visible && moment.BodiesShown;
        if (moment.BodiesShown && !_puffed) Puff();
        UpdatePuffs(seconds);
        if (Path != null && Camera != null)
        {
            var end = PlayPose();
            var pose = Path.Pose(seconds, end.Transform, end.Fov);
            Camera.GlobalTransform = pose.Transform;
            Camera.Fov = pose.FovDeg;
            Camera.Near = pose.NearM;
            if (World.Look is { } look)
            {
                var player = World.Player;
                var arriving = seconds >= IntroTimeline.Window("arrive").Start;
                var centre = Source!.Bounds.GetCenter();
                look.FocusOverride = arriving
                    ? player.GlobalPosition + Vector3.Up * (0.5f * player.BodyHeightM)
                    : pose.Transform.Origin - pose.Transform.Basis.Z * (pose.Transform.Origin.DistanceTo(new Vector3(centre.X, Source.FloorY, centre.Z)) + _focusBeyond);
            }
        }
    }

    /// <summary>A soft burst of light where each body appears.</summary>
    private void Puff()
    {
        _puffed = true;
        foreach (var (node, visible) in _bodies)
        {
            if (!visible || !IsInstanceValid(node)) continue;
            var height = node is SmallPlayerController body ? body.BodyHeightM : 0.1f;
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add, AlbedoColor = new Color(1f, 0.93f, 0.75f, 0.8f), NoDepthTest = false,
            };
            var mesh = new MeshInstance3D
            {
                Name = "IntroPuff", Mesh = new SphereMesh { Radius = height, Height = 2f * height, RadialSegments = 16, Rings = 8 },
                MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(mesh);
            mesh.GlobalPosition = node.GlobalPosition + Vector3.Up * (0.5f * height);
            _puffs.Add((mesh, material, Seconds));
        }
    }

    private void UpdatePuffs(double seconds)
    {
        foreach (var (mesh, material, born) in _puffs)
        {
            var age = (float)Math.Clamp((seconds - born) / 0.7, 0.0, 1.0);
            mesh.Scale = Vector3.One * (0.3f + 1.4f * age);
            material.AlbedoColor = new Color(material.AlbedoColor, 0.8f * (1f - age) * (1f - age));
            mesh.Visible = age < 1f;
        }
    }

    // ---------- the end ----------

    /// <summary>Put back everything the intro changed, then leave. Safe to call more than once.</summary>
    private void Finish(string reason)
    {
        if (Finished) return;
        Finished = true;
        EndReason = reason;
        Restore();
        if (_playCamera != null && IsInstanceValid(_playCamera) && _playCamera.IsInsideTree()) _playCamera.MakeCurrent();
        foreach (var child in GetChildren()) child.QueueFree();
        GD.Print(FormattableString.Invariant($"INTRO: ended ({reason}) at {Seconds:0.0} s"));
        Ended?.Invoke(this);
        QueueFree();
    }

    private void Restore()
    {
        if (Playing || GlobalsPlaying) RestGlobals();
        Playing = false;
        foreach (var (node, margin) in _marked)
            if (IsInstanceValid(node))
            {
                // Nil puts the shader's default back (0) and forgets the mark, as if it had never been set.
                node.SetInstanceShaderParameter(PartName, new Variant());
                node.ExtraCullMargin = margin;
            }
        _marked.Clear();
        foreach (var (node, position) in _water)
            if (IsInstanceValid(node)) node.Position = position;
        _water.Clear();
        foreach (var (node, visible) in _life.Concat(_bodies))
            if (IsInstanceValid(node)) node.Visible = visible;
        _life.Clear();
        _bodies.Clear();
        if (_copies is { } copies && IsInstanceValid(copies.Node)) copies.Node.Visible = copies.Visible;
        _copies = null;
        RestoreLand();
        if (_hazeLift != null && IsInstanceValid(_hazeLift)) _hazeLift.Lift = null;
        if (_lookHeld && World.Look is { } look && IsInstanceValid(look))
        {
            look.View = _viewBefore;
            look.FocusOverride = _focusBefore;
        }
        _lookHeld = false;
        if (World.Player is { } player && IsInstanceValid(player)) player.SetInputEnabled(_inputBefore);
        if (_hud != null && IsInstanceValid(_hud)) _hud.EndIntro();
        if (Camera != null && IsInstanceValid(Camera) && Camera.Current) Camera.ClearCurrent(false);
    }

    /// <summary>The land's levels of detail drawn at their own distances again.</summary>
    private void RestoreLand()
    {
        foreach (var (mesh, begin, beginMargin, end, endMargin) in _coarse)
            if (IsInstanceValid(mesh))
            {
                mesh.VisibilityRangeBegin = begin;
                mesh.VisibilityRangeBeginMargin = beginMargin;
                mesh.VisibilityRangeEnd = end;
                mesh.VisibilityRangeEndMargin = endMargin;
            }
        _coarse.Clear();
    }

    /// <summary>The globals as the project declares them: the land and everything on it drawn as without the intro.</summary>
    private static void RestGlobals()
    {
        RenderingServer.GlobalShaderParameterSet(RiseName, RiseAtRest);
        RenderingServer.GlobalShaderParameterSet(GrowName, Vector4.Zero);
        RenderingServer.GlobalShaderParameterSet(AreaName, Vector4.Zero);
        RenderingServer.GlobalShaderParameterSet(StartName, new Rid());
        RenderingServer.GlobalShaderParameterSet(ColourName, new Rid());
        GlobalsPlaying = false;
    }

    /// <summary>A world that leaves during the intro (the game quits) leaves the globals at rest and waits for the read to end.</summary>
    public override void _ExitTree()
    {
        if (!Finished)
        {
            Finished = true;
            EndReason = "the world left";
            Restore();
        }
        if (_preparing is { IsCompleted: false } preparing)
        {
            try { preparing.Wait(TimeSpan.FromSeconds(30)); }
            catch (AggregateException) { /* reported where it is awaited */ }
        }
    }
}
