using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>Depth-of-field distances for one camera, in metres from the camera.</summary>
public readonly record struct DepthOfField(bool FarEnabled, float FarDistance, float FarTransition, bool NearEnabled, float NearDistance, float NearTransition, float Amount);

/// <summary>
/// Applies a style preset to a room (Track L, L2) with light only from real sources: the sun on the solar model's
/// path (the moon at night), shadowed by the room's shell so it comes in only through the windows; a soft sky fill
/// through each window in the sky colour of the hour; the room's lamps, which switch on as the light goes; and VoxelGI
/// baked over the whole shell as a closed interior, so what comes through the window bounces around the room.
/// Also environment and tone mapping, soft shadows, SSAO, restrained glow, depth of field that keeps the player's
/// reach crisp, and a colour grade driven by the preset's palette, the hour and the season. Visual only: it never
/// changes room data, collision or protection.
///
/// It dresses every mesh that enters the tree under its parent (the room, later rebuilds, creations): paint
/// seeds and box edges for painterly materials, GI modes for room entities, and a VoxelGI (re)bake whenever new
/// shell geometry arrives. RoomWorld may also call Dress(room) directly.
/// The renderer is a project setting; when the machine renders with something else, the look says so.
/// </summary>
public partial class LookDirector : Node3D
{
    /// <summary>Meta marking a VoxelGI bake stand-in material; none may remain after a bake.</summary>
    public const string BakeStandInMeta = "look_bake_stand_in";
    /// <summary>Meta marking a mesh the look has already dressed.</summary>
    public const string DressedMeta = "look_dressed";
    /// <summary>How often the look re-reads the real clock, in seconds: often enough that a winter sunset changes the light in steps under 1%.</summary>
    public const double ClockUpdateSeconds = 10.0;
    /// <summary>How many grade LUTs stay cached (about 110 KB each at 33 cubed).</summary>
    public const int GradeCacheSize = 12;
    /// <summary>The shader that draws the sky a window shows.</summary>
    public const string SkyShaderPath = "res://shaders/painterly_window_sky.gdshader";
    /// <summary>Meta marking a mesh whose captured materials the look has made painterly (their originals are kept for the GI bake).</summary>
    public const string CapturedPaintedMeta = "look_captured_painted";
    /// <summary>Meta on a mesh that wears a landscape's water: it is see-through, so it casts no shadow and stays out of the GI bake.</summary>
    public const string WaterMeta = "look_water";

    public StylePreset Preset { get; private set; } = null!;
    public int RoomLightCount { get; private set; }
    /// <summary>Empty when the machine renders with the renderer the preset is designed for; otherwise what is missing.</summary>
    public string RendererNote { get; private set; } = "";
    public string GiNote { get; private set; } = "";
    public LookMoment Moment { get; private set; } = null!;
    /// <summary>True once room geometry with a shell has been dressed (and baked, on a GPU).</summary>
    public bool Dressed { get; private set; }
    /// <summary>
    /// Whether the room is open land (a landscape: its shell has no wall and no ceiling). On open land a real sky lights the
    /// land: the sun needs no opening, the sky's own light fills the shade, and there is no closed interior to bake.
    /// </summary>
    public bool OpenLand { get; private set; }
    /// <summary>
    /// The far plane of the room's play cameras (ViewDistances.FarPlaneM: 100 m in a room and on the 1x island, 250 m on the 10x
    /// island). The look keeps the game's current camera at it every frame; cameras the review harness frames keep their own.
    /// </summary>
    public float FarPlaneM { get; private set; } = ViewDistances.MinFarM;
    /// <summary>The sun's shadows on open land (ViewDistances.OpenLandShadows); unused indoors.</summary>
    public LandShadows LandShadows { get; private set; }
    public VoxelGI? Gi { get; private set; }
    /// <summary>
    /// The focus highlight (Run 2): the one thing the next key press would act on wears a soft rim and line (FocusLook). The play
    /// lane says which thing with SetFocusHighlight; things that can't be used stay plain.
    /// </summary>
    public FocusHighlight Highlight => _highlight ??= new FocusHighlight();
    private FocusHighlight? _highlight;

    /// <summary>Put the focus highlight on one thing (its every mesh), or on nothing (null). One thing at a time: the last one is cleared.</summary>
    public void SetFocusHighlight(Node3D? target) => Highlight.Set(target);

    /// <summary>The endless sea past the room's own meshes (OpenSea), once a room with a sea is dressed; null in a room without one.</summary>
    public OpenSea? OpenSea { get; private set; }
    public Godot.Environment Environment { get; private set; } = null!;
    /// <summary>The sun by day and the moon by night. Hidden in a room without a sun light hint (no window lets the sun in).</summary>
    public DirectionalLight3D Key { get; private set; } = null!;
    /// <summary>How strongly the sun reaches this room: the room's sun light hint's relative intensity in "sun" key mode, 0 without one.</summary>
    public float SunScale { get; private set; } = 1f;
    /// <summary>The soft sky fill outside each window, aimed in through the opening.</summary>
    public IReadOnlyList<SpotLight3D> SkyFills => _skyFills;
    /// <summary>The room's lamps (ceiling lamps, lamps, screens).</summary>
    public IReadOnlyList<Light3D> Lamps => _lamps;
    /// <summary>The glowing bulb of each lamp, shown while the lamp is on (none when the preset sets no glow).</summary>
    public IReadOnlyList<MeshInstance3D> LampGlows => _lampGlows;
    /// <summary>Whether the lamps are on now: pinned by SetLamps or --look-lamps, or switched on by themselves as the light goes.</summary>
    public bool LampsOn { get; private set; }
    /// <summary>Grain and vignette (Forward+ compositor effect), when the preset asks for them.</summary>
    public LookPostEffect? Post { get; private set; }
    /// <summary>Whether the running renderer supports depth of field; without it no camera attributes are set.</summary>
    public bool DofSupported { get; private set; }
    /// <summary>The player body depth of field focuses on. If unset, the parent's first SmallPlayerController that is not a companion.</summary>
    public Node3D? FocusTarget { get; set; }
    /// <summary>The companion body: what "companion" focus follows, and what "player" focus keeps crisp too when it is near. If unset, the parent's first CompanionAvatar.</summary>
    public Node3D? FocusCompanion { get; set; }
    /// <summary>
    /// A world point every unframed camera focuses on while set: the build camera's cursor hit or free-camera
    /// target. Clear it (null) to return to the preset's focus mode. "cursor" focus uses it and otherwise looks
    /// focus_band_m ahead.
    /// </summary>
    public Vector3? FocusOverride { get; set; }
    /// <summary>
    /// The observe view (the founder, 7 October: "the player should be able to switch to a view like that to look at what
    /// they built with their companion"): a very tight tilt-shift band, a sliver of the room crisp around the focus point
    /// with everything nearer and farther melting away, like looking at a model on a table. Focus follows
    /// the interpolated player body in third person, ahead of any cursor override; the band does not stretch for the companion.
    /// Observe tracks depth immediately; ordinary focus eases. Off by default; the HUD supplies its key.
    /// </summary>
    public bool Observe { get; set; }
    /// <summary>
    /// Which play view the camera stands for, so each blurs its own way (x_look_views): set by whoever frames a camera the look
    /// does not otherwise know (the review harness, a preview). Unset, the game's camera follows the HUD's view keys
    /// (RoomHud.ViewMode: F1 eye, F2 shoulder, F3 diorama, F4 isometric), the player's own eye camera is the eye view, and any
    /// other camera keeps the preset's miniature blur.
    /// </summary>
    public LookView? View { get; set; }
    /// <summary>Where the clock comes from: the real clock and calendar, the island's world clock, the preset's fixed hour and day, or a pin (and who pinned it).</summary>
    public string ClockNote { get; private set; } = "";
    /// <summary>
    /// The island's clock record (the room state's extensions.x_clock), or null for the machine's own clock. Null, or a record with
    /// day_scale 1 and no phase, leaves the look exactly as it was; the preset's follow_clock and follow_calendar still decide whether
    /// the look follows a clock at all, and a pin still wins over it.
    /// </summary>
    public WorldClock? WorldClock { get; private set; }
    /// <summary>Empty when the room declared its own site; otherwise why the sun uses the look's fallback site.</summary>
    public string SiteNote { get; private set; } = "";
    /// <summary>Things that went wrong with the look but did not stop the room: shown in reports and the review harness.</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    /// <summary>LUTs built so far (cache misses), for tests and reports.</summary>
    public int GradeBuilds { get; private set; }
    /// <summary>LUTs currently cached.</summary>
    public int CachedGrades => _luts.Count;
    /// <summary>GI bakes started so far (one per new shell geometry), for tests and reports.</summary>
    public int Bakes { get; private set; }
    /// <summary>
    /// What the player should be told about the look, or empty: today the renderer fallback, which makes the room
    /// look flatter. The HUD shows it; the log and the review harness get every warning.
    /// </summary>
    public string PlayerNotice => RendererNote.Length > 0 ? "The room is drawn with a simpler renderer on this machine, so the look is flatter: " + RendererNote : "";

    private RoomData _room = null!;
    private float? _pinnedHour;
    private int? _pinnedDay;
    private string _clockSource = "";
    private double _clockTimer;
    private int _framesSinceApply;
    private readonly Dictionary<ulong, CameraAttributesPractical> _attributes = new();
    private readonly HashSet<ulong> _framed = new();
    private readonly List<Light3D> _roomLights = new();
    private readonly List<SpotLight3D> _skyFills = new();
    private readonly List<Light3D> _lamps = new();
    private readonly List<MeshInstance3D> _lampGlows = new();
    private bool? _lampsPinned;
    private float? _moonYaw;
    private readonly List<string> _warnings = new();
    private readonly HashSet<string> _warned = new();
    private readonly List<MeshInstance3D> _pending = new();
    private bool _flushQueued;
    private readonly Dictionary<GradeParams, ImageTexture3D> _luts = new();
    private readonly LinkedList<GradeParams> _lutOrder = new();
    private GradeParams? _wantedGrade;
    private Task<byte[]>? _lutTask;
    private GradeParams? _lutTaskKey;

    /// <summary>Apply a preset to a room, working the sun out for the site the room's manifest declares (or the fallback, said in SiteNote).</summary>
    public void Apply(StylePreset preset, RoomData room)
    {
        var site = RoomSite.For(room, out var siteWarning);
        Apply(preset, room, site, siteWarning);
    }

    /// <summary>
    /// Apply a preset to a room standing at a site. The look keeps the preset with that site worked in (Preset), so the
    /// shared preset file never carries one. A site note is information, not a warning: a room without a site is common
    /// until rooms declare one, and the look says so in DescribeLook and the log.
    /// </summary>
    public void Apply(StylePreset preset, RoomData room, RoomSite site, string siteNote = "")
    {
        Preset = preset.WithSite(site);
        preset = Preset;
        _room = room;
        OpenLand = LandscapeLook.IsOpenLand(room);
        FarPlaneM = ViewDistances.FarPlaneM(room);
        LandShadows = ViewDistances.OpenLandShadows(ViewDistances.SpanM(room));
        SiteNote = siteNote;
        if (siteNote.Length > 0) GD.Print("LOOK: " + siteNote);
        MaterialLibrary.Configure(preset);
        Environment = BuildEnvironment(preset);
        BuildSky();
        var world = new WorldEnvironment { Name = "Environment", Environment = Environment };
        if (preset.Grain > 0f || preset.Vignette > 0f)
        {
            Post = new LookPostEffect { Grain = preset.Grain, Vignette = preset.Vignette, Tuning = preset.Tuning.Post };
            world.Compositor = new Compositor { CompositorEffects = new Godot.Collections.Array<CompositorEffect> { Post } };
        }
        AddChild(world);
        BuildKey(preset, room);
        BuildRoomLights(preset, room);
        var running = RenderingServer.GetCurrentRenderingMethod();
        DofSupported = running is "forward_plus" or "mobile";
        RendererNote = RendererNoteFor(preset.PresetId, preset.RendererMethod, running);
        if (RendererNote.Length > 0) Warn(RendererNote);
        if (preset.KuwaharaEnabled || preset.OutlineEnabled)
            Warn("the preset enables kuwahara or outline post effects, which this runtime does not implement yet");
        var (hour, day, error) = LookClock.ClockOverride(OS.GetCmdlineUserArgs(), OS.GetEnvironment);
        if (error.Length > 0) Warn(error + "; following the preset's clock instead");
        _pinnedHour = hour;
        _pinnedDay = day is { } d ? Math.Clamp(d, 1, 366) : null;
        _clockSource = hour != null || day != null ? "pinned by --look-clock/--look-date or ENFRACTAL_LOOK_CLOCK/ENFRACTAL_LOOK_DATE" : "";
        var (lampsOn, lampsError) = LookClock.LampsOverride(OS.GetCmdlineUserArgs(), OS.GetEnvironment);
        if (lampsError.Length > 0) Warn(lampsError + "; the lamps switch themselves instead");
        _lampsPinned = lampsOn;
        ApplyMoment(synchronous: true);
        _framesSinceApply = 0;
        QueueExisting();
    }

    /// <summary>Meshes already under the parent (a room built before the look, or before the look entered the tree) are dressed too.</summary>
    private void QueueExisting()
    {
        if (Preset == null || !IsInsideTree() || GetParent() is not { } parent) return;
        foreach (var mesh in parent.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()) Queue(mesh);
    }

    /// <summary>What the room loses when the machine does not render with the renderer the preset is designed for.</summary>
    public static string RendererNoteFor(string presetId, string designedFor, string running)
    {
        if (running == designedFor) return "";
        var loss = running == "gl_compatibility"
            ? "; the Compatibility renderer has no VoxelGI bounce, depth of field, TAA or grain and vignette, so the room looks flatter and harder-edged"
            : "";
        return $"preset {presetId} is designed for {designedFor}; this machine renders with {running}{loss}";
    }

    private void Warn(string message)
    {
        if (!_warned.Add(message)) return;
        _warnings.Add(message);
        GD.Print("LOOK WARNING: " + message);
    }

    // ---------- environment ----------

    private ShaderMaterial? _skyMaterial;

    /// <summary>
    /// The background becomes the sky of the hour (review M2): a window shows it from inside, so it must be the sky and not
    /// the preset's dark slate. It lights nothing (ambient and reflections stay off), so the room's exposure is unchanged.
    /// </summary>
    private void BuildSky()
    {
        var shader = GD.Load<Shader>(SkyShaderPath);
        if (shader == null) { Warn($"the window sky shader {SkyShaderPath} could not be loaded, so windows show the preset's flat background"); return; }
        _skyMaterial = new ShaderMaterial { Shader = shader, ResourceName = "window sky" };
        Environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        Environment.Sky = new Sky { SkyMaterial = _skyMaterial, ProcessMode = Sky.ProcessModeEnum.Automatic, RadianceSize = Sky.RadianceSizeEnum.Size32 };
    }

    private static Godot.Environment BuildEnvironment(StylePreset preset)
    {
        var ssao = preset.Tuning.Ssao;
        var glow = preset.Tuning.Glow;
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = preset.Background,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = preset.TonemapMode switch
            {
                "linear" => Godot.Environment.ToneMapper.Linear,
                "reinhard" => Godot.Environment.ToneMapper.Reinhardt,
                "aces" => Godot.Environment.ToneMapper.Aces,
                "agx" => Godot.Environment.ToneMapper.Agx,
                _ => Godot.Environment.ToneMapper.Filmic,
            },
            TonemapExposure = preset.Exposure,
            TonemapWhite = preset.White,
            SsaoEnabled = preset.AoEnabled,
            SsaoRadius = preset.AoRadiusM,
            SsaoIntensity = preset.AoIntensity,
            SsaoPower = ssao.Power,
            SsaoDetail = ssao.Detail,
            SsaoHorizon = ssao.Horizon,
            SsaoSharpness = ssao.Sharpness,
            SsaoLightAffect = ssao.LightAffect,
            SsaoAOChannelAffect = ssao.AoChannelAffect,
            SsilEnabled = preset.SsilEnabled,
            SsilRadius = preset.SsilRadiusM,
            SsilIntensity = preset.SsilIntensity,
            SsilSharpness = ssao.Sharpness,
            GlowEnabled = preset.GlowEnabled,
            GlowIntensity = preset.GlowIntensity,
            GlowStrength = glow.Strength,
            GlowBloom = preset.GlowBloom,
            GlowBlendMode = glow.BlendMode switch
            {
                "additive" => Godot.Environment.GlowBlendModeEnum.Additive,
                "screen" => Godot.Environment.GlowBlendModeEnum.Screen,
                "replace" => Godot.Environment.GlowBlendModeEnum.Replace,
                "mix" => Godot.Environment.GlowBlendModeEnum.Mix,
                _ => Godot.Environment.GlowBlendModeEnum.Softlight,
            },
            GlowHdrThreshold = glow.HdrThreshold,
            AdjustmentEnabled = true,
        };
        for (var level = 0; level < 7; level++) environment.SetGlowLevel(level, glow.Levels[level]);
        return environment;
    }

    // ---------- lights ----------

    private void BuildKey(StylePreset preset, RoomData room)
    {
        var diagonal = room.Bounds.Size.Length();
        var softness = preset.ShadowSoftness;
        var s = preset.Tuning.Shadows;
        // Light only from real sources: the sun reaches a room through its openings, and the shell casts its shadows like
        // everything else, so direct sun lands only where an opening lets it. A room says the sun can come in with a sun
        // hint; a room with a window but no sun hint (a captured room usually has the window, rarely the sun) gets the sun
        // at full strength through that window, because a window is the sun's way in (review M1). A room with neither is
        // a closed box lit by its lamps.
        var sunHint = room.LightHints.FirstOrDefault(h => h.Kind == "sun");
        var hasWindow = room.LightHints.Any(h => h.Kind == "window");
        // On open land there is no wall to keep the sun out: a real sky lights the land, so the sun is at full strength even
        // when the landscape declared no sun hint (the window only ever told where the sun rises).
        SunScale = preset.KeyMode == "sun" ? sunHint?.RelativeIntensity ?? (hasWindow || OpenLand ? 1f : 0f) : 1f;
        Key = new DirectionalLight3D
        {
            Name = "Key",
            ShadowEnabled = preset.KeyCastsShadows && preset.ShadowsEnabled,
            LightAngularDistance = s.KeyAngularBaseDeg + s.KeyAngularPerSoftnessDeg * softness,
            ShadowBlur = s.BlurBase + s.BlurPerSoftness * softness,
            DirectionalShadowMode = (OpenLand ? LandShadows.Splits : s.KeySplits) switch
            {
                1 => DirectionalLight3D.ShadowMode.Orthogonal,
                4 => DirectionalLight3D.ShadowMode.Parallel4Splits,
                _ => DirectionalLight3D.ShadowMode.Parallel2Splits,
            },
            // Open land casts its shadows as far as the eye judges them (the hills and trees around), not just over a room: 30 m in
            // two splits on the 1x island, up to 60 m in four on a big one (ViewDistances.OpenLandShadows).
            DirectionalShadowMaxDistance = OpenLand ? LandShadows.DistanceM : Mathf.Max(s.KeyMinDistanceM, diagonal * s.KeyDistancePerDiagonal),
            DirectionalShadowSplit1 = OpenLand ? LandShadows.Split1 : s.KeySplit1,
            DirectionalShadowBlendSplits = true,
            ShadowBias = s.KeyBias,
            ShadowNormalBias = s.KeyNormalBias,
            LightBakeMode = Light3D.BakeMode.Dynamic,
            Visible = SunScale > 0f,
        };
        if (OpenLand && LandShadows.Splits == 4)
        {
            Key.DirectionalShadowSplit2 = LandShadows.Split2;
            Key.DirectionalShadowSplit3 = LandShadows.Split3;
        }
        AddChild(Key);
        // The moon stands outside the room's brightest window, so moonlight falls in through it.
        var window = room.LightHints
            .Where(h => h.Kind == "window" && h.Direction is { } d && new Vector2(d.X, d.Z).LengthSquared() > 1e-6f)
            .OrderByDescending(h => h.RelativeIntensity).FirstOrDefault();
        _moonYaw = window?.Direction is { } inward ? Mathf.RadToDeg(Mathf.Atan2(-inward.X, -inward.Z)) : null;
    }

    /// <summary>
    /// The room's light hints as lights: each window a soft sky fill standing outside the opening and aimed in; each lamp
    /// an omni light. The shadow budget (max_shadowed_lights, the sun taking one) goes first to the brightest windows,
    /// then to lamps. A sky fill that gets no shadow would shine straight through the wall it stands behind, so it stands
    /// inside the room, at the opening, instead (review M3): its light then starts where the window is and never from
    /// outside the shell.
    /// </summary>
    private void BuildRoomLights(StylePreset preset, RoomData room)
    {
        var diagonal = room.Bounds.Size.Length();
        var s = preset.Tuning.Shadows;
        var lamps = preset.Tuning.Lamps;
        var shadowBudget = Math.Max(0, preset.MaxShadowedLights - (Key.ShadowEnabled && Key.Visible ? 1 : 0));
        foreach (var hint in room.LightHints.Where(h => h.PositionM != null)
                     .OrderBy(h => h.Kind == "window" ? 0 : 1).ThenByDescending(h => h.RelativeIntensity))
        {
            Light3D light;
            float energy;
            var shadowed = preset.ShadowsEnabled && shadowBudget > 0;
            if (hint.Kind is "ceiling_lamp" or "lamp" or "screen")
            {
                energy = hint.RelativeIntensity * preset.RoomLightEnergyScale * preset.HonorRoomLights;
                if (energy <= 0) continue;
                light = new OmniLight3D { OmniRange = diagonal * lamps.RangePerDiagonal, OmniAttenuation = lamps.Attenuation, Position = hint.PositionM!.Value };
                light.LightSize = s.LampSizeBaseM + s.LampSizePerSoftnessM * preset.ShadowSoftness;
                light.LightColor = hint.Color.Lerp(lamps.LampTint, lamps.LampTintAmount);
                _lamps.Add(light);
                if (lamps.GlowRadiusM > 0f && lamps.GlowEnergy > 0f) _lampGlows.Add(BuildLampGlow(hint, light.LightColor, lamps));
            }
            else if (hint.Kind == "window" && hint.Direction is { } direction && direction.LengthSquared() > 1e-6f)
            {
                energy = hint.RelativeIntensity * lamps.SkyFillEnergy * preset.HonorRoomLights;
                if (energy <= 0) continue;
                var inward = direction.Normalized();
                var spot = new SpotLight3D { SpotRange = diagonal * lamps.RangePerDiagonal + lamps.WindowStandoffM, SpotAngle = lamps.WindowSpotAngleDeg };
                spot.Position = SkyFillPosition(room.Bounds, hint.PositionM!.Value, inward, lamps.WindowStandoffM, shadowed);
                var up = Mathf.Abs(inward.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
                spot.Basis = Basis.LookingAt(inward, up);
                spot.LightSize = lamps.WindowLightSizeM;
                spot.SetMeta("window_color", hint.Color);
                light = spot;
                _skyFills.Add(spot);
            }
            else continue;
            light.Name = "RoomLight_" + hint.Id;
            light.LightEnergy = energy;
            light.ShadowEnabled = shadowed;
            if (shadowed) shadowBudget--;
            light.ShadowBlur = s.BlurBase + s.BlurPerSoftness * preset.ShadowSoftness;
            light.ShadowBias = s.LampBias;
            light.ShadowNormalBias = s.LampNormalBias;
            light.SetMeta("light_hint_id", hint.Id);
            light.SetMeta("base_energy", energy);
            AddChild(light);
            _roomLights.Add(light);
            RoomLightCount++;
        }
    }

    /// <summary>The bulb of a lamp: a small emissive sphere in the lamp's own colour, which casts no shadow and takes no part in GI.</summary>
    private MeshInstance3D BuildLampGlow(LightHint hint, Color colour, LampTuning lamps)
    {
        var material = new StandardMaterial3D
        {
            ResourceName = "lamp glow " + hint.Id, AlbedoColor = new Color(0f, 0f, 0f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true, Emission = colour, EmissionEnergyMultiplier = lamps.GlowEnergy,
        };
        var glow = new MeshInstance3D
        {
            Name = "LampGlow_" + hint.Id, Mesh = new SphereMesh { Radius = lamps.GlowRadiusM, Height = lamps.GlowRadiusM * 2f, RadialSegments = 24, Rings = 12 },
            MaterialOverride = material, Position = hint.PositionM!.Value, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false,
        };
        AddChild(glow);
        return glow;
    }

    /// <summary>
    /// Where a window's sky fill stands. With a shadow it stands standoff metres outside the opening, so the shell between
    /// it and the room blocks it everywhere but through the opening. Without one it must not stand behind any wall, so it
    /// takes the opening's place but inside the room (at least 2 cm inside the bounds), looking in.
    /// </summary>
    public static Vector3 SkyFillPosition(Aabb bounds, Vector3 hintPosition, Vector3 inward, float standoffM, bool shadowed)
    {
        if (shadowed) return hintPosition - inward * standoffM;
        var inner = bounds.Grow(-0.02f);
        return new Vector3(
            Mathf.Clamp(hintPosition.X, inner.Position.X, inner.End.X),
            Mathf.Clamp(hintPosition.Y, inner.Position.Y, inner.End.Y),
            Mathf.Clamp(hintPosition.Z, inner.Position.Z, inner.End.Z));
    }

    /// <summary>The sky's brightness at a moment, relative to the brightest sky of the keys (1 at the brightest).</summary>
    public static float SkyLevel(StylePreset preset, LookMoment moment)
    {
        var brightest = preset.TimeOfDayEnabled && preset.TimeKeys.Count > 0 ? preset.TimeKeys.Max(k => k.AmbientEnergy) : preset.AmbientEnergy;
        return brightest > 0f ? Mathf.Clamp(moment.AmbientEnergy / brightest, 0f, 1f) : 0f;
    }

    /// <summary>
    /// The colour of the sky seen through a window: the hour's sky colour through the window's own colour, at full
    /// brightness (the energy carries the level), keeping `saturation` of its hue (0 white, 1 the full sky colour).
    /// </summary>
    public static Color SkyColor(Color window, Color sky, float saturation)
    {
        var c = new Color(window.R * sky.R, window.G * sky.G, window.B * sky.B);
        var max = Mathf.Max(c.R, Mathf.Max(c.G, c.B));
        return max > 1e-4f ? new Color(1f, 1f, 1f).Lerp(new Color(c.R / max, c.G / max, c.B / max), saturation) : new Color(0f, 0f, 0f);
    }

    /// <summary>Pin the room's lamps on or off (true, false), or let them switch themselves as the light goes (null).</summary>
    public void SetLamps(bool? on)
    {
        _lampsPinned = on;
        if (Moment != null) ApplyMoment(synchronous: true);
    }

    private void ApplyLamps()
    {
        if (Moment == null) return;
        LampsOn = _lampsPinned ?? Moment.Daylight < Preset.Tuning.Lamps.SwitchOnBelowDaylight;
        foreach (var lamp in _lamps) lamp.Visible = LampsOn;
        foreach (var glow in _lampGlows) glow.Visible = LampsOn;
    }

    // ---------- time of day and season ----------

    /// <summary>Pin the look to an hour and a day of the year (review captures, previews). The grade is rebuilt before this returns.</summary>
    public void SetClock(float hour, int dayOfYear)
    {
        _pinnedHour = hour;
        _pinnedDay = Math.Clamp(dayOfYear, 1, 366);
        _clockSource = "pinned by SetClock";
        ApplyMoment(synchronous: true);
    }

    /// <summary>Pin the clock but build a missing grade LUT off the main thread; the old grade stays until it is ready.</summary>
    public void SetClockAsync(float hour, int dayOfYear)
    {
        _pinnedHour = hour;
        _pinnedDay = Math.Clamp(dayOfYear, 1, 366);
        _clockSource = "pinned by SetClock";
        ApplyMoment(synchronous: false);
    }

    /// <summary>Follow the preset again: the real clock and calendar when it says so, else its default hour and fixed day.</summary>
    public void ReleaseClock()
    {
        _pinnedHour = null;
        _pinnedDay = null;
        _clockSource = "";
        ApplyMoment(synchronous: true);
    }

    /// <summary>
    /// The hour and the day the world's clock reads now, before any pin: the machine's clock with no island clock record, the island's
    /// clock with one. The HUD's time and season steps use it for the part they leave unpinned.
    /// </summary>
    public (float Hour, int DayOfYear) RealNow() => LookClock.Now(Preset, WorldClock);

    /// <summary>Set the island's clock record (null: the machine's own clock) and redraw the sky for it. Reapplied when a new room state is read.</summary>
    public void SetWorldClock(WorldClock? clock)
    {
        WorldClock = clock;
        if (Preset != null) ApplyMoment(synchronous: true);
    }

    /// <summary>
    /// Reads the room state's extensions.x_clock (null when it has none) and sets it. A record that does not pass the schema's checks, or names a
    /// zone this machine does not know, is warned about and the look keeps the machine's clock. Returns the warning, or empty.
    /// </summary>
    public string SetWorldClockFrom(System.Text.Json.Nodes.JsonNode? record)
    {
        var clock = WorldClock.FromNode(record, out var error);
        if (error.Length > 0) Warn(error + "; following the machine's clock instead");
        SetWorldClock(clock);
        return error;
    }

    /// <summary>Whether the grade for the current moment is the one on screen (false while an off-thread LUT is building).</summary>
    public bool GradeCurrent => _wantedGrade != null && _luts.TryGetValue(_wantedGrade, out var lut) && Environment.AdjustmentColorCorrection == lut;

    private void ApplyMoment(bool synchronous)
    {
        var now = RealNow();
        var hour = _pinnedHour ?? (Preset.FollowClock ? now.Hour : Preset.DefaultHour);
        var day = _pinnedDay ?? (Preset.FollowCalendar ? now.DayOfYear : Preset.Tuning.Seasons.FixedDayOfYear);
        var realClock = (_pinnedHour == null && Preset.FollowClock) || (_pinnedDay == null && Preset.FollowCalendar);
        var world = WorldClock is { IsRealClock: false } clock ? clock : null;
        ClockNote = (_pinnedHour != null ? "hour pinned" : Preset.FollowClock ? (world != null ? $"world clock x{world.DayScale} ({world.ZoneId})" : "real clock") : "preset hour")
            + (_pinnedDay != null ? ", day pinned" : Preset.FollowCalendar ? ", real calendar" : ", preset day")
            + (_clockSource.Length > 0 ? " (" + _clockSource + ")" : "")
            + (realClock && Preset.Tuning.Sun.RealClockDaylightSaving && world is not { DayScale: > 1 } ? "; read as standard time" : "");
        Moment = LookClock.At(Preset, hour, day, _moonYaw);
        // The season on the land (leaves, meadow, ground): albedo only, through the land shader's globals; nothing below reads it.
        LandSeason.Apply(Moment);
        Key.LightColor = Moment.KeyColor;
        // On open land the whole ground stands in the sun, where indoors only a patch of floor does: the sun that is bright enough
        // for that patch would blow out every lit surface, so the open land's sun is a fraction of it (OpenLandLight.SunStrength).
        Key.LightEnergy = Moment.KeyEnergy * SunScale * (OpenLand ? OpenLandLight.SunStrength : 1f);
        // A summer sun casts a harder shadow than a winter one: the season sets how soft the shadow edge is.
        Key.ShadowBlur = (Preset.Tuning.Shadows.BlurBase + Preset.Tuning.Shadows.BlurPerSoftness * Preset.ShadowSoftness) * Mathf.Lerp(Moment.Look.SunBlur, 1f, Moment.MoonWeight);
        var skyLook = LookSky.At(Preset, Moment, _moonYaw);
        if (_skyMaterial != null) LookSky.Apply(_skyMaterial, skyLook);
        OpenSea?.SetHorizon(skyLook.Horizon, skyLook.Brightness);
        // Open land without a sea has the haze alone (with a sea, OpenSea draws it and closes the mist in from it).
        if (OpenLand && OpenSea == null) ViewDistances.Apply(Environment, ViewDistances.Fog(0f, FarPlaneM, haze: true), skyLook.Horizon, skyLook.Brightness);
        Key.RotationDegrees = new Vector3(-Moment.KeyElevationDeg, Moment.KeyAzimuthDeg, 0);
        var sky = SkyLevel(Preset, Moment);
        foreach (var fill in _skyFills)
        {
            fill.LightColor = SkyColor(fill.GetMeta("window_color").AsColor(), Moment.AmbientColor, Preset.Tuning.Lamps.SkyFillSaturation);
            fill.LightEnergy = (float)fill.GetMeta("base_energy").AsDouble() * sky;
        }
        ApplyLamps();
        Environment.AmbientLightColor = Moment.AmbientColor;
        // Indoors the sky's own light is only what comes through the windows (the VoxelGI interior and the sky fills), so the
        // environment's ambient is nearly off; on open land the sky is all there is, and its colour of the hour fills the shade.
        Environment.AmbientLightEnergy = Moment.AmbientEnergy * (OpenLand ? OpenLandLight.AmbientScale : Preset.Tuning.Gi.EnvironmentAmbientScale);
        ApplyGrade(GradeParams.For(Preset, Moment, LampsOn).Quantized(), synchronous);
    }

    private void ApplyGrade(GradeParams grade, bool synchronous)
    {
        _wantedGrade = grade;
        if (_luts.TryGetValue(grade, out var cached))
        {
            Environment.AdjustmentColorCorrection = cached;
            _lutOrder.Remove(grade);
            _lutOrder.AddFirst(grade);
            return;
        }
        if (synchronous)
        {
            CacheLut(grade, ColorGrade.LutTexture(grade));
            Environment.AdjustmentColorCorrection = _luts[grade];
            return;
        }
        if (_lutTask != null && Equals(_lutTaskKey, grade)) return;
        _lutTaskKey = grade;
        _lutTask = Task.Run(() => ColorGrade.LutBytes(grade));
    }

    private void CacheLut(GradeParams grade, ImageTexture3D texture)
    {
        GradeBuilds++;
        _luts[grade] = texture;
        _lutOrder.Remove(grade);
        _lutOrder.AddFirst(grade);
        while (_lutOrder.Count > GradeCacheSize)
        {
            _luts.Remove(_lutOrder.Last!.Value);
            _lutOrder.RemoveLast();
        }
    }

    private void CollectLut()
    {
        if (_lutTask is not { IsCompleted: true } task || _lutTaskKey is not { } key) return;
        _lutTask = null;
        _lutTaskKey = null;
        if (!task.IsCompletedSuccessfully) { Warn("the colour grade could not be built: " + task.Exception?.GetBaseException().Message); return; }
        if (!_luts.ContainsKey(key)) CacheLut(key, ColorGrade.LutTexture(task.Result, key.Tuning.LutSize));
        if (Equals(_wantedGrade, key)) Environment.AdjustmentColorCorrection = _luts[key];
        else if (_wantedGrade != null && !_luts.ContainsKey(_wantedGrade)) ApplyGrade(_wantedGrade, synchronous: false);
    }

    // ---------- dressing ----------

    public override void _EnterTree()
    {
        GetTree().NodeAdded += OnNodeAdded;
        QueueExisting();
    }

    public override void _ExitTree()
    {
        GetTree().NodeAdded -= OnNodeAdded;
        ReleaseGlowAudio();
        Post?.Release();
    }

    private void OnNodeAdded(Node node)
    {
        if (Preset != null && node is MeshInstance3D mesh) Queue(mesh);
    }

    private void Queue(MeshInstance3D mesh)
    {
        if (mesh.HasMeta(DressedMeta) || GetParent() is not { } parent || !parent.IsAncestorOf(mesh) || IsAncestorOf(mesh)) return;
        _pending.Add(mesh);
        if (_flushQueued) return;
        _flushQueued = true;
        CallDeferred(MethodName.FlushPending);
    }

    private void FlushPending()
    {
        _flushQueued = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var meshes = _pending.Where(m => IsInstanceValid(m) && m.IsInsideTree()).Distinct().ToArray();
        _pending.Clear();
        Node3D? bakeRoot = null;
        foreach (var mesh in meshes)
            if (DressMesh(mesh)) bakeRoot ??= SubtreeRoot(mesh);
        var dressed = clock.Elapsed.TotalMilliseconds;
        if (bakeRoot != null) Bake(bakeRoot);
        var baked = clock.Elapsed.TotalMilliseconds;
        if (bakeRoot != null) BuildOpenSea(bakeRoot);
        // The first recorded play's first frames: where the load's last frame went (a room's dressing, its bake, its sea).
        if (clock.Elapsed.TotalMilliseconds > 30)
            GD.Print(FormattableString.Invariant($"LOOK: dressed {meshes.Length} meshes in {dressed:0} ms, baked in {baked - dressed:0} ms, built the sea in {clock.Elapsed.TotalMilliseconds - baked:0} ms"));
    }

    /// <summary>
    /// Dress a built room or any later subtree now: paint seeds and box edges for painterly materials, the
    /// shell's visual layer and GI modes for room entities, and a VoxelGI bake when it brings shell geometry.
    /// Meshes already dressed are skipped, so calling it again, or after the automatic pass, is harmless.
    /// </summary>
    public void Dress(Node3D root)
    {
        var bake = false;
        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Append(root as MeshInstance3D).OfType<MeshInstance3D>())
            bake |= DressMesh(mesh);
        if (bake) Bake(root);
        if (bake) BuildOpenSea(root);
    }

    /// <summary>
    /// A room with a sea gets the open sea once its shell is dressed: the surface and bed that follow the camera past the room's own
    /// meshes, the backdrop's distant islands laid flat on the bed, and the sea mist that hides the island from the seam out.
    /// </summary>
    private void BuildOpenSea(Node3D root)
    {
        if (OpenSea != null || _room?.Sea is not { } sea || !root.IsInsideTree()) return;
        var meshes = root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Where(m => m.HasMeta(DressedMeta)).ToArray();
        OpenSea = OpenSea.Build(sea, _room.Bounds, meshes.Where(m => m.HasMeta(WaterMeta)),
            meshes.Where(m => !m.HasMeta(WaterMeta) && m.HasMeta(LandscapeLook.LandscapePaintedMeta)));
        OpenSea.Atmosphere = Environment;
        OpenSea.Haze = OpenLand;
        AddChild(OpenSea);
        // The sea's fish follow the player's body, as depth of field does; never the Gubble.
        OpenSea.SetSwimmer(() => FocusTarget ?? GetParent()?.GetChildren().OfType<SmallPlayerController>().FirstOrDefault(c => c is not CompanionAvatar));
        if (Moment != null)
        {
            var skyLook = LookSky.At(Preset, Moment, _moonYaw);
            OpenSea.SetHorizon(skyLook.Horizon, skyLook.Brightness);
        }
        if (OpenSea.Note.Length > 0) Warn(OpenSea.Note);
        GD.Print($"LOOK: open sea: surface {(OpenSea.Surface != null ? "follows the camera" : "missing")}, one sea floor at RoomSea.OpenSeaBedAt, no distant islands, the mist hiding the island from {OpenSea.SeamM:0.#} m (the seam)");
    }

    /// <summary>Dress one mesh; returns true when it is shell geometry that belongs in the GI bake.</summary>
    private bool DressMesh(MeshInstance3D mesh)
    {
        if (mesh.HasMeta(DressedMeta)) return false;
        mesh.SetMeta(DressedMeta, true);
        var owner = OwnerEntity(mesh);
        if (owner == null) return false; // not room data (an avatar, a gizmo): left alone
        // A captured mesh arrives with the materials its file gave it (plain glTF materials); they become painterly too.
        if (mesh.MaterialOverride == null) PaintCaptured(mesh, owner);
        if (MaterialLibrary.IsPainterly(mesh.MaterialOverride) || mesh.HasMeta(CapturedPaintedMeta))
        {
            // The land's parts (terrain, scenery, merged plants) share world-space marks, so a mark runs on across the parts
            // that meet; only a prop (a cottage, a crate) gets its own seed.
            if (!(mesh.HasMeta(LandscapeLook.LandscapePaintedMeta) && owner.HasMeta("surface_role")))
                mesh.SetInstanceShaderParameter("paint_seed", Seed(owner.GetMeta("entity_id").AsString()));
            if (mesh.Mesh is BoxMesh box)
            {
                mesh.SetInstanceShaderParameter("box_edges", 1f);
                mesh.SetInstanceShaderParameter("box_half_extents", box.Size * 0.5f);
            }
        }
        // The whole shell (floor, walls, ceiling) is baked: a closed interior that bounces what comes through the window.
        var isShell = owner.HasMeta("surface_role");
        var movable = owner.HasMeta("movable") && owner.GetMeta("movable").AsBool();
        mesh.GIMode = isShell || !movable ? GeometryInstance3D.GIModeEnum.Static : GeometryInstance3D.GIModeEnum.Dynamic;
        // The shell casts double-sided shadows: a photographed room's walls are single surfaces, and a single-sided wall
        // casts no shadow on the side it faces away from, so the sun would pass straight through it (review M4). A closed
        // slab loses nothing.
        mesh.CastShadow = isShell ? GeometryInstance3D.ShadowCastingSetting.DoubleSided : GeometryInstance3D.ShadowCastingSetting.On;
        // Water you can see into would shade its own bed as a slab: it casts no shadow and is no wall for the bounce.
        if (mesh.HasMeta(WaterMeta))
        {
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            mesh.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
        }
        // Ground that arrives once the sea is built (a tile, a part) wears the shore like the ground that was there.
        if (OpenSea != null && mesh.HasMeta(LandscapeLook.LandscapePaintedMeta)) OpenSea.AdoptLand(mesh);
        return isShell;
    }

    /// <summary>
    /// Give a captured mesh's plain materials the painterly treatment of their role (review minor: captured assets got none),
    /// keeping each surface's own colour and colour texture. The instance's surface overrides carry the painterly
    /// materials, so the mesh's own materials stay as they were and the GI bake can still voxelize them.
    /// </summary>
    private static void PaintCaptured(MeshInstance3D mesh, Node3D owner)
    {
        if (mesh.Mesh == null) return;
        var painted = false;
        for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.GetActiveMaterial(surface) is not BaseMaterial3D source) continue;
            // A landscape's material (the exporter's, naming its harness role and baking tints and blends into the vertex
            // colour) keeps its vertex colour inside the painterly treatment of its own harness role.
            if (LandscapeLook.TryRole(source, out var landRole))
            {
                var bake = LandscapeLook.BakeColor(mesh.Mesh, surface, source.AlbedoColor);
                mesh.SetSurfaceOverrideMaterial(surface, MaterialLibrary.ForLandscape(landRole, source.AlbedoColor, bake));
                mesh.SetMeta(LandscapeLook.LandscapePaintedMeta, true);
                if (LandscapeLook.IsWater(landRole)) mesh.SetMeta(WaterMeta, true);
                painted = true;
                continue;
            }
            var role = RoleFor(owner, source.ResourceName);
            mesh.SetSurfaceOverrideMaterial(surface, MaterialLibrary.ForCaptured(role, source.AlbedoColor, source.AlbedoTexture));
            painted = true;
        }
        if (painted) mesh.SetMeta(CapturedPaintedMeta, true);
    }

    /// <summary>The material role of a captured surface: a shell part's own role, or the asset slot named like the surface's material (else the asset's first role).</summary>
    public static string RoleFor(Node3D owner, string materialName)
    {
        if (owner.HasMeta("surface_role") && owner.HasMeta("material_role")) return owner.GetMeta("material_role").AsString();
        if (!owner.HasMeta("material_roles")) return "default";
        var roles = owner.GetMeta("material_roles").AsGodotDictionary();
        if (roles.Count == 0) return "default";
        if (materialName.Length > 0 && roles.TryGetValue(materialName, out var named)) return named.AsString();
        return roles.Values.First().AsString();
    }

    private Node3D? SubtreeRoot(Node node)
    {
        var parent = GetParent();
        for (var current = node; current != null; current = current.GetParent())
            if (current.GetParent() == parent) return current as Node3D;
        return null;
    }

    private static Node3D? OwnerEntity(Node node)
    {
        for (var current = node.GetParent(); current != null; current = current.GetParent())
            if (current is Node3D spatial && spatial.HasMeta("entity_id")) return spatial;
        return null;
    }

    /// <summary>A stable per-entity seed in [0, 100) so two identical props are painted differently.</summary>
    public static float Seed(string entityId)
    {
        var hash = 2166136261u;
        foreach (var c in entityId) hash = (hash ^ c) * 16777619u;
        return hash / (float)uint.MaxValue * 100f;
    }

    /// <summary>The VoxelGI volume for a room: its bounds plus a margin so the shell's thickness is inside.</summary>
    public static Aabb GiVolume(RoomData room, GiTuning gi) => room.Bounds.Grow(gi.MarginM);

    private void Bake(Node3D root)
    {
        Dressed = true;
        Bakes++;
        if (Preset.GiMode != "voxelgi") GiNote = $"gi mode {Preset.GiMode}: no global illumination node";
        else if (OpenLand) GiNote = "open land: no VoxelGI (a closed interior is what it bakes); the sky's ambient light and the sun light the land";
        else BakeVoxelGi(root);
        if (GiNote.Length > 0) GD.Print("LOOK: " + GiNote);
    }

    /// <summary>
    /// Run a bake with flat stand-ins of the painterly colours on every mesh that has one (VoxelGI voxelizes
    /// BaseMaterial3D albedo only), and put every original material back afterwards, even when the bake or the
    /// swap itself throws.
    /// </summary>
    public static void WithBakeStandIns(IEnumerable<GeometryInstance3D> meshes, Action bake)
    {
        var swaps = new List<(GeometryInstance3D Mesh, Material? Original)>();
        var surfaces = new List<(MeshInstance3D Mesh, int Surface, Material Painted)>();
        try
        {
            foreach (var mesh in meshes)
            {
                // A captured mesh's painterly materials sit on its instance; VoxelGI voxelizes the mesh's own glTF materials
                // (colour and texture) when they are lifted off for the bake.
                if (mesh is MeshInstance3D { Mesh: not null } captured && (captured.HasMeta(CapturedPaintedMeta) || captured.HasMeta(LandscapeLook.LandscapePaintedMeta)))
                    for (var surface = 0; surface < captured.Mesh.GetSurfaceCount(); surface++)
                        if (captured.GetSurfaceOverrideMaterial(surface) is { } painted && MaterialLibrary.IsPainterly(painted))
                        {
                            surfaces.Add((captured, surface, painted));
                            // A landscape surface is voxelized as its baked colour (VoxelGI ignores vertex colour, and the glTF
                            // factor under it is the blends' cream envelope); a captured one as its own glTF material.
                            if (MaterialLibrary.BakeAlbedo(painted) is { } landColour && painted.HasMeta("landscape"))
                            {
                                var landStandIn = new StandardMaterial3D { AlbedoColor = landColour };
                                landStandIn.SetMeta(BakeStandInMeta, true);
                                captured.SetSurfaceOverrideMaterial(surface, landStandIn);
                            }
                            else captured.SetSurfaceOverrideMaterial(surface, null);
                        }
                if (MaterialLibrary.BakeAlbedo(mesh.MaterialOverride) is { } albedo)
                {
                    var standIn = new StandardMaterial3D { AlbedoColor = albedo };
                    standIn.SetMeta(BakeStandInMeta, true);
                    swaps.Add((mesh, mesh.MaterialOverride));
                    mesh.MaterialOverride = standIn;
                }
            }
            bake();
        }
        finally
        {
            foreach (var (mesh, original) in swaps) mesh.MaterialOverride = original;
            foreach (var (mesh, surface, painted) in surfaces) mesh.SetSurfaceOverrideMaterial(surface, painted);
        }
    }

    private void BakeVoxelGi(Node3D root)
    {
        if (RenderingServer.GetRenderingDevice() == null)
        {
            GiNote = $"VoxelGI needs a GPU rendering device (Forward+ or Mobile); none here (display {DisplayServer.GetName()}, driver '{RenderingServer.GetCurrentRenderingDriverName()}'), so no GI";
            return;
        }
        var tuning = Preset.Tuning.Gi;
        var volume = GiVolume(_room, tuning);
        if (Gi == null)
        {
            Gi = new VoxelGI { Name = "RoomGI" };
            AddChild(Gi);
        }
        Gi.Size = volume.Size;
        Gi.Position = volume.GetCenter();
        Gi.Subdiv = tuning.Subdiv switch
        {
            64 => VoxelGI.SubdivEnum.Subdiv64,
            256 => VoxelGI.SubdivEnum.Subdiv256,
            512 => VoxelGI.SubdivEnum.Subdiv512,
            _ => VoxelGI.SubdivEnum.Subdiv128,
        };
        var clock = Stopwatch.StartNew();
        var meshes = root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()
            .Where(m => m.GIMode == GeometryInstance3D.GIModeEnum.Static);
        WithBakeStandIns(meshes, () => Gi.Bake(root));
        var data = Gi.Data;
        if (data != null)
        {
            data.Energy = Preset.GiEnergy;
            data.Propagation = Mathf.Clamp(Preset.GiBounceFeedback, 0f, 1f);
            data.UseTwoBounces = Preset.GiBounceFeedback > tuning.TwoBouncesAbove;
            // A closed room: cones that leave the volume see nothing, so sky light enters only as the window's fill.
            data.Interior = true;
            data.NormalBias = tuning.NormalBias;
            data.Bias = tuning.Bias;
        }
        GiNote = string.Format(CultureInfo.InvariantCulture, "VoxelGI {0:0.0#} x {1:0.0#} x {2:0.0#} m at subdiv {3} baked in {4} ms",
            volume.Size.X, volume.Size.Y, volume.Size.Z, tuning.Subdiv, clock.ElapsedMilliseconds);
    }

    // ---------- depth of field ----------

    /// <summary>
    /// Depth of field for a camera focused at a distance. The in-focus band is the preset's focus_band_m
    /// around the focus; far blur ramps over a distance that grows with the focus distance; near blur covers
    /// what is closer than near_blur_distance_m (or the band). Tilt-shift narrows the band and strengthens
    /// the blur as the camera looks down on the room, which is what makes a high view read as a miniature.
    /// Tilt-shift also shortens the blur ramps, so the crisp band ends sooner and the frame's top and bottom melt.
    /// crispFromM keeps everything from there to the focus crisp (the player's reach, seen from its eye), and
    /// alsoM is a second distance the band stretches to keep crisp (the companion beside the player).
    /// view is how the view blurs (x_look_views): null or "miniature" is all of the above; "far" keeps everything from the
    /// lens to crisp_beyond_focus_m past the subject crisp, with no near blur and no tilt-shift, and softens only what lies beyond.
    /// </summary>
    public static DepthOfField DepthOfFieldFor(StylePreset preset, float focusDistance, float lookingDown, float crispFromM = float.PositiveInfinity, float alsoM = float.NaN, bool observe = false, ViewBlur? view = null)
    {
        var t = preset.Tuning.Dof;
        var d = Mathf.Max(focusDistance, 0.05f);
        if (observe)
        {
            // A sliver of crisp depth, short ramps and full blur outside it: the tightest tilt-shift the look makes.
            var half = 0.5f * t.ObserveBandM;
            var nearEdge = Mathf.Max(0.01f, d - half);
            return new DepthOfField(preset.DofEnabled, d + half, t.ObserveFarTransitionM, preset.DofEnabled && nearEdge > 0.01f, nearEdge, t.ObserveNearTransitionM, t.ObserveAmount);
        }
        var nearest = float.IsFinite(alsoM) && alsoM > 0.05f ? Mathf.Min(d, alsoM) : d;
        var farthest = float.IsFinite(alsoM) && alsoM > 0.05f ? Mathf.Max(d, alsoM) : d;
        if (view is { Far: true } far)
        {
            // Nothing near is soft (Godot's near blur smears well past its set distance: in F2 the ground under the camera, in F3
            // the cliff beside it); the land stays crisp to the far start, and the far shore, the distant islands and the
            // horizon soften over the transition.
            var lens = Mathf.Min(preset.NearBlurDistanceM, 0.5f * nearest);
            return new DepthOfField(preset.DofEnabled, farthest + far.CrispBeyondFocusM, far.FarTransitionM,
                false, lens, Mathf.Max(lens * (t.NearTransitionBase - t.NearTransitionPerBlur * preset.NearBlur), 0.01f), far.FarAmount);
        }
        var tilt = preset.TiltShiftEnabled ? preset.TiltShiftStrength * Mathf.Clamp(lookingDown * t.TiltPitchGain, 0f, 1f) : 0f;
        var halfBand = 0.5f * preset.FocusBandM * (1f - t.TiltBandNarrowing * tilt);
        var shorten = 1f - t.TiltTransitionShortening * tilt;
        var farDistance = farthest + halfBand;
        var farTransition = (t.FarTransitionBaseM + t.FarTransitionPerM * farthest) * (t.FarBlurReference - preset.FarBlur) * shorten;
        var nearDistance = Mathf.Min(Mathf.Max(preset.NearBlurDistanceM, nearest - halfBand), Mathf.Max(preset.NearBlurDistanceM, crispFromM));
        var nearTransition = nearDistance * (t.NearTransitionBase - t.NearTransitionPerBlur * preset.NearBlur) * shorten;
        var amount = Mathf.Min(1f, (t.AmountBase + t.AmountPerBlur * Mathf.Max(preset.FarBlur, preset.NearBlur)) * (1f + t.TiltAmountGain * tilt));
        return new DepthOfField(preset.DofEnabled && preset.FarBlur > 0f, farDistance, Mathf.Max(farTransition, 0.05f),
            preset.DofEnabled && preset.NearBlur > 0f && nearDistance > 0.01f, nearDistance, Mathf.Max(nearTransition, 0.01f), amount);
    }

    /// <summary>Give a camera the preset's depth of field, focused on a world point. Used by the review harness and previews.</summary>
    public void FrameCamera(Camera3D camera, Vector3 focusPoint)
    {
        _framed.Add(camera.GetInstanceId());
        if (OpenSea != null) OpenSea.Camera = camera;
        Focus(camera, focusPoint);
    }

    /// <summary>
    /// Move one band toward another by the fraction t (0 stays, 1 arrives): distances, transitions and amount blend; which
    /// blurs are on follows the target at once. Depth-of-field focus eases like an eye refocusing, it never snaps.
    /// </summary>
    public static DepthOfField Ease(DepthOfField from, DepthOfField to, float t) => new(
        to.FarEnabled, Mathf.Lerp(from.FarDistance, to.FarDistance, t), Mathf.Lerp(from.FarTransition, to.FarTransition, t),
        to.NearEnabled, Mathf.Lerp(from.NearDistance, to.NearDistance, t), Mathf.Lerp(from.NearTransition, to.NearTransition, t),
        Mathf.Lerp(from.Amount, to.Amount, t));

    /// <summary>The depth-of-field attributes the look gave a camera, if any.</summary>
    public CameraAttributesPractical? AttributesFor(Camera3D camera) => _attributes.TryGetValue(camera.GetInstanceId(), out var a) ? a : null;

    private readonly Dictionary<ulong, DepthOfField> _eased = new();

    private void Focus(Camera3D camera, Vector3 focusPoint, float crispFromM = float.PositiveInfinity, Vector3? alsoPoint = null, double delta = 0)
    {
        if (!DofSupported) return;
        if (!_attributes.TryGetValue(camera.GetInstanceId(), out var attributes))
        {
            attributes = new CameraAttributesPractical();
            _attributes[camera.GetInstanceId()] = attributes;
        }
        var forward = -camera.GlobalBasis.Z;
        var distance = (focusPoint - camera.GlobalPosition).Dot(forward);
        var also = alsoPoint is { } point ? (point - camera.GlobalPosition).Dot(forward) : float.NaN;
        var dof = DepthOfFieldFor(Preset, distance, Mathf.Max(0f, -forward.Y), crispFromM, also, Observe, ViewFor(camera) is { } view ? Preset.Tuning.BlurFor(view) : null);
        // Ordinary focus eases. Observe follows the current body/camera depth immediately,
        // including a mode change on the shared F3/F4 camera; the rig already eases its motion.
        var ease = Preset.Tuning.Dof.FocusEaseS;
        var id = camera.GetInstanceId();
        if (!Observe && delta > 0 && ease > 0f && _eased.TryGetValue(id, out var previous)) dof = Ease(previous, dof, 1f - Mathf.Exp((float)(-delta / ease)));
        _eased[id] = dof;
        Post?.SetFocus(dof);
        attributes.DofBlurFarEnabled = dof.FarEnabled;
        attributes.DofBlurFarDistance = dof.FarDistance;
        attributes.DofBlurFarTransition = dof.FarTransition;
        attributes.DofBlurNearEnabled = dof.NearEnabled;
        attributes.DofBlurNearDistance = dof.NearDistance;
        attributes.DofBlurNearTransition = dof.NearTransition;
        attributes.DofBlurAmount = dof.Amount;
        if (camera.Attributes != attributes) camera.Attributes = attributes;
    }

    private Node3D? FocusBody() => Preset.DofFocus switch
    {
        "player" => FocusTarget ?? GetParent()?.GetChildren().OfType<SmallPlayerController>().FirstOrDefault(c => c is not CompanionAvatar),
        "companion" => CompanionBody(),
        _ => null,
    };

    private Node3D? CompanionBody()
    {
        if (FocusCompanion != null) return FocusCompanion;
        // Found once and kept (asked every frame: a list of the parent's children each time was garbage for the collector).
        if (_companion == null || !IsInstanceValid(_companion)) _companion = GetParent()?.GetChildren().OfType<CompanionAvatar>().FirstOrDefault();
        return _companion;
    }

    private CompanionAvatar? _companion;

    private RoomHud? _hud;

    /// <summary>
    /// The view a camera stands for: View when it is set; else the player's own eye camera is the eye view, a camera framed with
    /// FrameCamera keeps the miniature blur (null), and the game's camera is the view the HUD's keys chose.
    /// </summary>
    public LookView? ViewFor(Camera3D camera)
    {
        if (View is { } chosen) return chosen;
        if (FocusBody() is SmallPlayerController controller && IsInstanceValid(controller) && camera == controller.EyeCamera) return LookView.Eye;
        if (_framed.Contains(camera.GetInstanceId())) return null;
        if (_hud == null || !IsInstanceValid(_hud)) _hud = GetParent()?.GetChildren().OfType<RoomHud>().FirstOrDefault();
        return _hud?.ViewMode switch { 0 => LookView.Eye, 1 => LookView.Shoulder, 2 => LookView.Diorama, 3 => LookView.Isometric, _ => null };
    }

    /// <summary>
    /// A second point to keep crisp: with "player" focus, the companion's body while it is within
    /// companion_follow_m of the player (the two avatars read as one subject), seen from any camera but the eye.
    /// Over the last 30% of that distance the point slides to the player's own focus, so the band never jumps.
    /// </summary>
    public Vector3? AlsoInFocusFor(Camera3D camera)
    {
        var follow = Preset.Tuning.Dof.CompanionFollowM;
        if (Observe || FocusOverride != null || Preset.DofFocus != "player" || follow <= 0f || CrispFromFor(camera) < float.PositiveInfinity) return null;
        if (FocusBody() is not { } player || !IsInstanceValid(player) || CompanionBody() is not { } companion || !IsInstanceValid(companion) || companion == player) return null;
        var separation = companion.GlobalPosition.DistanceTo(player.GlobalPosition);
        if (separation >= follow) return null;
        var height = companion is SmallPlayerController body ? body.BodyHeightM : 0f;
        var point = companion.GlobalPosition + Vector3.Up * (Preset.Tuning.Dof.BodyFocusHeightFraction * height);
        return point.Lerp(FocusPointFor(camera), Mathf.SmoothStep(0.7f * follow, follow, separation));
    }

    /// <summary>From the focus body's own eye, everything out to eye_crisp_body_heights body heights (its reach) stays crisp.</summary>
    public float CrispFromFor(Camera3D camera) =>
        !Observe && FocusOverride == null && FocusBody() is SmallPlayerController controller && IsInstanceValid(controller) && camera == controller.EyeCamera
            ? Preset.Tuning.Dof.EyeCrispBodyHeights * controller.BodyHeightM
            : float.PositiveInfinity;

    /// <summary>
    /// Where a camera should focus under the preset's focus mode. "player": a third-person camera focuses on
    /// the player's body (body_focus_height_fraction up it); the player's own EyeCamera focuses
    /// eye_focus_body_heights body heights ahead, just past its reach. "companion": the companion's body.
    /// "fixed" (and "cursor", not implemented yet): focus_band_m ahead of the camera.
    /// </summary>
    public Vector3 FocusPointFor(Camera3D camera)
    {
        var body = FocusBody();
        if (Observe && body is SmallPlayerController avatar && IsInstanceValid(avatar) && camera != avatar.EyeCamera)
            return avatar.GetGlobalTransformInterpolated().Origin + Vector3.Up * (avatar.BodyHeightM * 0.5f);
        if (FocusOverride is { } target) return target;
        var forward = -camera.GlobalBasis.Z;
        var dof = Preset.Tuning.Dof;
        if (body != null && IsInstanceValid(body))
        {
            if (body is SmallPlayerController controller)
            {
                if (camera == controller.EyeCamera) return camera.GlobalPosition + forward * (dof.EyeFocusBodyHeights * controller.BodyHeightM);
                return controller.GlobalPosition + Vector3.Up * (dof.BodyFocusHeightFraction * controller.BodyHeightM);
            }
            return body.GlobalPosition;
        }
        return camera.GlobalPosition + forward * Preset.FocusBandM;
    }

    public override void _Process(double delta)
    {
        UpdateGlows(delta);
        UpdateEffects(delta);
        UpdateBubbles(delta);
        if (Preset == null) return;
        KeepFarPlane();
        _highlight?.Prune();
        CollectLut();
        if (_framesSinceApply++ == 2 && !Dressed)
            Warn("no room geometry with a shell was dressed after the first frames; the look has no GI bake and no shell layer");
        if ((_pinnedHour == null && Preset.FollowClock) || (_pinnedDay == null && Preset.FollowCalendar))
        {
            _clockTimer += delta;
            if (_clockTimer > ClockUpdateSeconds) { _clockTimer = 0; ApplyMoment(synchronous: false); }
        }
        if (!Preset.DofEnabled || !DofSupported) return;
        var camera = GetViewport()?.GetCamera3D();
        if (camera == null || _framed.Contains(camera.GetInstanceId())) return;
        Focus(camera, FocusPointFor(camera), CrispFromFor(camera), AlsoInFocusFor(camera), delta);
    }

    /// <summary>
    /// The game's current camera sees to the room's far plane (FarPlaneM): the HUD's views and the player's eye are made at 100 m, so
    /// the look sets the rule's distance whenever one of them is current. A camera the review harness framed keeps its own.
    /// </summary>
    private void KeepFarPlane()
    {
        var camera = GetViewport()?.GetCamera3D();
        if (camera == null || _framed.Contains(camera.GetInstanceId()) || Mathf.IsEqualApprox(camera.Far, FarPlaneM)) return;
        camera.Far = FarPlaneM;
    }

    /// <summary>
    /// Problems with the look as rendered, for the review harness: an undressed room, a renderer fallback,
    /// VoxelGI without data, bake stand-ins left on meshes, a post effect that never ran, and any warnings.
    /// Empty means the GPU paths did what they should.
    /// </summary>
    public string[] SelfCheck()
    {
        var problems = new List<string>(_warnings);
        if (!Dressed) problems.Add("the room was never dressed");
        var gpu = RenderingServer.GetRenderingDevice() != null;
        if (gpu && Preset.GiMode == "voxelgi" && !OpenLand && (Gi == null || Gi.Data == null)) problems.Add("VoxelGI has no baked data");
        var standIns = GetParent()?.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>()
            .Count(g => g.MaterialOverride?.HasMeta(BakeStandInMeta) == true) ?? 0;
        if (standIns > 0) problems.Add($"{standIns} mesh(es) still carry VoxelGI bake stand-ins");
        if (gpu && Post != null && !Post.Ran) problems.Add("the grain and vignette effect never ran" + (Post.Error.Length > 0 ? ": " + Post.Error : ""));
        return problems.Distinct().ToArray();
    }

    /// <summary>A one-line description of what the look applied, for review reports.</summary>
    public string DescribeLook() => DescribeCore() + string.Format(CultureInfo.InvariantCulture, "; site {0} ({1:0} degrees latitude, -Z facing {2:0}, solar noon {3:0.##} h){4}",
        Preset.Site.Source, Preset.Site.LatitudeDeg, Preset.Site.NegZBearingDeg, Preset.Site.SolarNoonH, SiteNote.Length > 0 ? ": " + SiteNote : "");

    private string DescribeCore() => string.Format(CultureInfo.InvariantCulture,
        "{0}@{1} ({2}) sha256={3}; renderer={4}{5}; key={6} elevation {7:0.#} yaw {8:0.#} energy {9:0.##} (sun at {23:0.#} degrees, bearing {24:0.#}; moon weight {25:0.##}; sun scale {26:0.##}); sky fill {27}; lamps {28}; hour {10:0.##} day {11} season {12} ({22}); {13}; ssao={14} ssil={15} glow={16} dof={17} tilt={18}; grain {19} vignette {20} post effect {21}",
        Preset.PresetId, Preset.PresetVersion, Preset.Status, Preset.Sha256, RenderingServer.GetCurrentRenderingMethod(),
        RendererNote.Length > 0 ? " (" + RendererNote + ")" : "", Preset.KeyMode, Moment.KeyElevationDeg, Moment.KeyAzimuthDeg, Key.LightEnergy,
        Moment.Hour, Moment.DayOfYear, Moment.Season, GiNote, Preset.AoEnabled, Preset.SsilEnabled, Preset.GlowEnabled, Preset.DofEnabled && DofSupported,
        Preset.TiltShiftEnabled ? Preset.TiltShiftStrength : 0f, Preset.Grain, Preset.Vignette,
        Post == null ? "off" : Post.Ran ? "ran" : Post.Error.Length > 0 ? "failed: " + Post.Error : "not run (no GPU device)", ClockNote,
        Moment.SunElevationDeg, Moment.SunBearingDeg, Moment.MoonWeight, SunScale,
        _skyFills.Count == 0 ? "none" : string.Join(", ", _skyFills.Select(f => string.Format(CultureInfo.InvariantCulture, "energy {0:0.###} colour #{1}", f.LightEnergy, f.LightColor.ToHtml(false)))),
        _lamps.Count == 0 ? "none" : (LampsOn ? "on" : "off") + (_lampsPinned == null ? " (switched by the light)" : " (pinned)"));
}
