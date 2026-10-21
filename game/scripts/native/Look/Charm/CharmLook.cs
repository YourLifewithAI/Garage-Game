using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look.Charm;

/// <summary>
/// The hamlet's charm (Run 2, L11: docs/runs/RUN-2-LOOK.md, docs/look/CHARM.md): the life details that make the generator's
/// hamlet look lived in, hung on its own things. Smoke rises from every chimney and leans with the wind, thin by day and thicker
/// once the lamps are on; the windows glow warm and the lanterns shine when the lamps are on; bunting and washing hang on strings
/// between the cottages and sway with the meadow's wind; fairy lights twinkle along the fences and the bunting at night. Attach it
/// beside NatureLook once the room is built:
/// <code>
/// AddChild(CharmLook.Create(Room, Built, Look));
/// </code>
/// It reads only the room's data (its objects, spawns, sea and trees), the ground under the strings (rays down on its first
/// physics frame) and the look's LampsOn, its wind axis and its haze. Placement is deterministic from the room's id and
/// Tuning.Seed. One MultiMesh a kind, one draw each; only the smoke is written on the CPU each frame, the sway and the twinkle are
/// the shaders'. Nothing here is an entity, saved, collides, lights anything or is seen by the kernel, sight or map. On by
/// default in open land, never in a room (a room's charm is the bible's indoor list); "--look-charm=off" after "--" (or
/// GARAGE_GAME_LOOK_CHARM=off) pins it away for a capture that compares.
/// </summary>
public partial class CharmLook : Node3D
{
    /// <summary>Every number of the charm (code constants this round, as the effects' are; the report proposes them as the preset's charm block).</summary>
    public static class Tuning
    {
        /// <summary>The charm's own seed, with the room's id the source of all its variety (the preset's reserved charm.seed).</summary>
        public const int Seed = 20261006;
        /// <summary>The charm is drawn to this distance (the hamlet's small things are a pixel or two past it).</summary>
        public const float FarM = 60f;
        /// <summary>The night kinds and the smoke's thickness follow LampsOn over this many seconds.</summary>
        public const float LitFadeS = 2f;

        // ---------- the kit's shapes (pipeline/landscape/harness/kit.py, fitted to its size), as fractions of a thing's size ----------

        /// <summary>A cottage's chimney top (stone_masonry at the back right), and the chimney's width as a fraction of the cottage's.</summary>
        public static readonly Vector3 ChimneyTop = new(0.27871f, 1f, 0.20406f);
        public const float ChimneyWidth = 0.14741f;
        /// <summary>A cottage's right window (cloth, beside the door on the local -Z face; the left one mirrors it) and its size.</summary>
        public static readonly Vector3 WindowCentre = new(0.26224f, 0.37733f, -0.49792f);
        public static readonly Vector2 WindowSize = new(0.17207f, 0.16983f);
        /// <summary>The roof's four eave corners: it spans the cottage's full width and depth, its lower edge at this height.</summary>
        public const float EaveHeight = 0.60379f;
        public static readonly Vector3[] EaveCorners =
        {
            new(-0.5f, EaveHeight, -0.5f), new(0.5f, EaveHeight, -0.5f), new(-0.5f, EaveHeight, 0.5f), new(0.5f, EaveHeight, 0.5f),
        };
        /// <summary>A tower's round roof starts here (its eave, a fraction of its height) at half its width.</summary>
        public const float TowerEave = 0.73805f;
        /// <summary>A lantern's cloth middle, and a fence's three posts (their tops are the fence's height).</summary>
        public static readonly Vector3 LanternCloth = new(0f, 0.41967f, 0f);
        public static readonly float[] FencePosts = { -0.45f, 0f, 0.45f };

        // ---------- chimney smoke: a stream of soft puffs from each chimney, the bubbles' pattern (CPU-updated quads) ----------

        /// <summary>Puffs a chimney (under the plan's 20), each living SmokeLifeS, rising and drifting with the wind, from SmokeStartM to SmokeEndM across.</summary>
        public const int SmokePuffs = 16;
        public const float SmokeLifeS = 7f, SmokeRiseMps = 0.045f, SmokeDriftMps = 0.024f, SmokeStartM = 0.03f, SmokeEndM = 0.16f;
        /// <summary>A puff's wobble about its path, and how thick the stream is by day and once the lamps are on (the puffs' opacity).</summary>
        public const float SmokeWobbleM = 0.008f, SmokeDay = 0.4f, SmokeNight = 0.7f;
        /// <summary>Puffs grow with their chimney: these sizes are for a chimney this wide (the garage hamlet's, 6.3 cm), never under 0.6 of them.</summary>
        public const float SmokeChimneyM = 0.063f;
        /// <summary>A puff starts this far above the chimney's top (so it never pokes out of the stone).</summary>
        public const float SmokeLiftM = 0.004f;
        public static readonly Color SmokeColour = new(0.93f, 0.92f, 0.9f);
        /// <summary>The puff's paint: how soft its rim, how lumpy, how far the light wraps round it, how much it shines with the sun behind it.</summary>
        public const float SmokeSoftness = 0.7f, SmokeLumps = 0.35f, SmokeWrap = 1f, SmokeScatter = 1.2f;

        // ---------- warm windows and lantern glow (emissive only: the budget's light slots are the sun's, the windows' and the wisps') ----------

        /// <summary>The glowing pane sits this far off the wall over the cloth window, this much bigger than it.</summary>
        public const float PaneOffsetM = 0.0015f, PaneGrow = 1.0f;
        public static readonly Color WindowColour = new(1f, 0.74f, 0.42f);
        /// <summary>How bright a lit window is (over 1: the bloom catches it), its mullions' width (of the pane's half), a slow flicker of the fire inside.</summary>
        public const float WindowEnergy = 1.25f, Mullion = 0.09f, WindowFlicker = 0.08f;
        /// <summary>A lantern's halo: this times the lantern's height across, its colour and brightness, its flicker.</summary>
        public const float LanternHalo = 1.6f, LanternEnergy = 2.2f, LanternFlicker = 0.12f;
        public static readonly Color LanternColour = new(1f, 0.8f, 0.52f);
        public const float LanternHeartSharpness = 9f, LanternHaloAmount = 0.55f;

        // ---------- strings: bunting and laundry between the cottages ----------

        /// <summary>Strings hang between two of the hamlet's buildings (eave corner to eave corner) this far apart, or to a near tree's trunk.</summary>
        public const float SpanMinM = 0.12f, SpanMaxM = 1.1f;
        /// <summary>A string sags this fraction of its span in its middle.</summary>
        public const float SagFraction = 0.08f;
        /// <summary>A tree holds a string at this fraction of its trunk's height (or lower, at the eave's).</summary>
        public const float TreeAnchorHeight = 0.8f;
        /// <summary>The rays that find the ground under the strings start RayAboveM over the hamlet's highest roof and end RayBelowM under its lowest footing.</summary>
        public const float RayAboveM = 0.5f, RayBelowM = 1.5f;
        /// <summary>Where a string may hang: never within SpawnClearM of a spawn; its lowest cloth HeadroomM over the ground; AnchorInsetM into its own anchors only at its ends.</summary>
        public const float SpawnClearM = 0.35f, HeadroomM = 0.12f, AnchorInsetM = 0.01f;
        /// <summary>The string itself: half its thickness, its colour, and the length of a bare piece of it (it bends between pieces).</summary>
        public const float StringHalfM = 0.0008f, StringPieceM = 0.03f;
        public static readonly Color StringColour = new(0.42f, 0.36f, 0.3f);

        /// <summary>A bunting flag: a triangle FlagWidthM wide hanging FlagDropM, every FlagSpacingM along the string, in the palette's accents.</summary>
        public const float FlagWidthM = 0.02f, FlagDropM = 0.024f, FlagSpacingM = 0.026f;
        /// <summary>The preset's accents (palette.accents), used when the look gives none.</summary>
        public static readonly Color[] Accents = { new("d28f63"), new("65b9b0"), new("d7b765"), new("a18cc3"), new("75965c") };

        /// <summary>The washing: towels, shirts, socks and sheets (width, drop), LaundryGapM apart, LaundryEndsM clear of each end, in washed-out colours.</summary>
        public static readonly (float WidthM, float DropM)[] Garments = { (0.030f, 0.036f), (0.024f, 0.030f), (0.012f, 0.022f), (0.042f, 0.032f), (0.020f, 0.026f) };
        public const float LaundryGapM = 0.006f, LaundryEndsM = 0.03f;
        public static readonly Color[] LaundryColours = { new("f4f1ea"), new("efe3c4"), new("bcd3e6"), new("e9c3c3"), new("cfe5d2"), new("f2e6a8") };
        /// <summary>The deepest cloth any string carries (the headroom is kept under it).</summary>
        public static readonly float ClothDropMaxM = Math.Max(FlagDropM, Garments.Max(g => g.DropM));

        /// <summary>The sway with the meadow's wind: a flag's or a garment's free edge leans SwayM, the string swings StringSwingM in its middle, the cloth flutters FlutterM.</summary>
        public const float SwayM = 0.007f, SwayPeriodS = 2.6f, StringSwingM = 0.004f, FlutterM = 0.0015f, FlutterPeriodS = 0.7f;
        /// <summary>The washing is heavier: it sways less and slower.</summary>
        public const float LaundrySwayM = 0.005f, LaundrySwayPeriodS = 3.1f;
        /// <summary>The cloth's paint: tone variation across it, wrapped light, a little sheen.</summary>
        public const float ClothToneVariation = 0.1f, ClothWrap = 0.5f, ClothSheen = 0.12f;

        // ---------- fairy lights: along the fences' tops and the bunting's string, at night ----------

        /// <summary>A bulb is BulbSizeM across up close and never under BulbMinPixels on screen; it hangs BulbHangM under its string.</summary>
        public const float BulbSizeM = 0.009f, BulbMinPixels = 4f, BulbHangM = 0.003f, BulbEnergy = 3.6f;
        /// <summary>Along a fence: a bulb every FenceBulbSpacingM, the strand sagging FenceSagM between posts.</summary>
        public const float FenceBulbSpacingM = 0.024f, FenceSagM = 0.006f;
        /// <summary>The twinkle: each bulb on its own period between TwinkleMinS and TwinkleMaxS, never dimmer than 1 - BulbTwinkle.</summary>
        public const float BulbTwinkle = 0.45f, TwinkleMinS = 2.2f, TwinkleMaxS = 4.4f;
        public const float BulbHeartSharpness = 7f, BulbHalo = 0.7f;
        public static readonly Color BulbWarm = new("ffd9a0");
        /// <summary>A share of the bulbs is coloured, the rest warm white.</summary>
        public const float BulbColouredShare = 0.3f;
        public static readonly Color[] BulbColours = { new("ff9a8a"), new("9ad1ff"), new("b8f0a0"), new("ffe08a") };
    }

    public const string SmokeShaderPath = "res://shaders/charm_smoke.gdshader";
    public const string ClothShaderPath = "res://shaders/charm_cloth.gdshader";
    public const string GlowShaderPath = "res://shaders/charm_glow.gdshader";
    /// <summary>The pin for a capture that compares: "--look-charm=off" after "--", or this environment variable ("on" keeps the default).</summary>
    public const string PinArgument = "--look-charm=";
    public const string PinVariable = "GARAGE_GAME_LOOK_CHARM";

    /// <summary>The uniforms each shader declares and CharmLook sets (the look suite holds the lists to the shaders).</summary>
    public static readonly string[] SmokeParameterNames = { "smoke_color", "softness", "lumps", "light_wrap", "scatter" };
    public static readonly string[] ClothParameterNames =
    {
        "wind_dir", "sway_m", "sway_period_s", "swing_m", "flutter_m", "flutter_period_s", "string_half_m", "string_color", "tone_variation", "light_wrap", "sheen",
    };
    public static readonly string[] GlowParameterNames =
    {
        "glow_color", "energy", "lit", "billboard", "min_pixels", "heart_sharpness", "halo", "twinkle", "twinkle_min_s", "twinkle_max_s", "mullion",
        "fog_on", "fog_begin_m", "fog_end_m", "fog_density", "fog_curve",
    };

    /// <summary>Floats an instance takes in a pool's buffer: its transform (12), colour (4) and custom data (4).</summary>
    private const int Stride = 20;
    private static readonly StringName LitName = "lit";

    private RoomData? _room;
    private Node3D? _built;
    private LookDirector? _look;
    private float[] _smokeBuffer = Array.Empty<float>();
    /// <summary>Each pool's buffer as last written (the engine keeps none it can give back headless).</summary>
    private readonly Dictionary<MultiMeshInstance3D, float[]> _buffers = new();
    private ShaderMaterial? _smokeMaterial, _buntingMaterial, _laundryMaterial, _windowMaterial, _lanternMaterial, _bulbMaterial;
    private ShaderMaterial?[] _nightMaterials = Array.Empty<ShaderMaterial?>();
    private MultiMeshInstance3D?[] _nightPools = Array.Empty<MultiMeshInstance3D?>();
    private float _litShown = -1f;
    private (bool On, float Begin, float End, float Density, float Curve) _fog = (false, -1f, -1f, -1f, -1f);
    private Vector3 _wind = Vector3.Right;
    private string _groundNote = "";

    /// <summary>Whether the charm draws on this room: open land and not pinned off.</summary>
    public bool Enabled { get; private set; }
    public HamletSurvey? Survey { get; private set; }
    public CharmPlan? Plan { get; private set; }
    public bool Planned { get; private set; }
    /// <summary>Whether it lives on its own each frame (tests turn it off and step it with Advance).</summary>
    public bool Running { get; set; } = true;
    /// <summary>The charm's own clock: the smoke reads it.</summary>
    public double LifeS { get; private set; }
    /// <summary>The lamps for the charm (tests): null follows the look's LampsOn.</summary>
    public bool? LampsPinned { get; set; }
    /// <summary>How far the night kinds are in, 0 (day) to 1 (the lamps on); it follows the lamps over Tuning.LitFadeS.</summary>
    public float Lit { get; private set; }
    public MultiMeshInstance3D? Smoke { get; private set; }
    public MultiMeshInstance3D? Bunting { get; private set; }
    public MultiMeshInstance3D? Laundry { get; private set; }
    public MultiMeshInstance3D? Windows { get; private set; }
    public MultiMeshInstance3D? Lanterns { get; private set; }
    public MultiMeshInstance3D? FairyLights { get; private set; }
    public IEnumerable<MultiMeshInstance3D> Pools => new[] { Smoke, Bunting, Laundry, Windows, Lanterns, FairyLights }.OfType<MultiMeshInstance3D>();
    public string Summary { get; private set; } = "";

    public static CharmLook Create(RoomData room, Node3D built, LookDirector? look)
    {
        var charm = new CharmLook { Name = "CharmLook" };
        charm.Configure(room, built, look);
        return charm;
    }

    /// <summary>Bind to the room (and the look, for its lamps, wind and haze). The hamlet is planned on the first physics frame, when the ground can be asked.</summary>
    public void Configure(RoomData room, Node3D built, LookDirector? look)
    {
        _room = room;
        _built = built;
        _look = look;
        var pin = Pin(OS.GetCmdlineUserArgs(), System.Environment.GetEnvironmentVariable(PinVariable));
        Enabled = pin != false && LandscapeLook.IsOpenLand(room);
        Survey = HamletSurvey.Of(room);
        Summary = !Enabled ? (pin == false ? "pinned off (--look-charm=off or GARAGE_GAME_LOOK_CHARM)" : "not drawn in a room (a room's charm is the bible's indoor list)") : "waiting for the ground";
        if (!Enabled) GD.Print("CHARM_LOOK: " + Summary);
        SetPhysicsProcess(Enabled);
    }

    /// <summary>The capture pin: false for off (the argument first, then the variable), true for on, null when neither says.</summary>
    public static bool? Pin(IEnumerable<string> arguments, string? variable)
    {
        var text = arguments.FirstOrDefault(a => a.StartsWith(PinArgument, StringComparison.Ordinal))?[PinArgument.Length..] ?? variable;
        return text?.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" or "no" => false,
            "on" or "1" or "true" or "yes" => true,
            _ => null,
        };
    }

    public override void _EnterTree() => TopLevel = true;

    public override void _PhysicsProcess(double delta)
    {
        if (Planned || !Enabled || _room == null) { SetPhysicsProcess(false); return; }
        // The land's collision is built where the bodies are: the hamlet's ground is built for the moment the strings ask it.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tiles = 0;
        foreach (var thing in Survey!.Things.Where(t => t.Kind is HamletKind.Cottage or HamletKind.Tower))
            tiles += TerrainCollision.EnsureAround(this, thing.PositionM, Tuning.SpanMaxM + 1f, holdS: 0.5);
        _groundNote = tiles > 0 ? $" (and {tiles} tiles of the land's collision built for its rays in {clock.Elapsed.TotalMilliseconds:0} ms)" : "";
        // The rays span only the hamlet's own heights (from a little over its roofs to well under its footings), never the room's
        // whole height (27 m on the 10x island), so they meet the ground under the strings and not every crown and cap above it.
        var hamlet = Survey.Things;
        var top = hamlet.Count > 0 ? hamlet.Max(t => t.PositionM.Y + t.SizeM.Y) + Tuning.RayAboveM : _room.Bounds.End.Y;
        var bottom = hamlet.Count > 0 ? hamlet.Min(t => t.PositionM.Y) - Tuning.RayBelowM : _room.Bounds.Position.Y;
        PlanNow(RayGround(GetWorld3D().DirectSpaceState, _room, top, bottom));
        SetPhysicsProcess(false);
    }

    /// <summary>
    /// The ground's height under a point: the first drawn shell surface a ray down from top to bottom meets on the world layer (past
    /// things, trunks and tree caps); null when it meets none there.
    /// </summary>
    public static Func<Vector2, float?> RayGround(PhysicsDirectSpaceState3D space, RoomData room, float top, float bottom)
    {
        var ground = room.Shell.Where(p => p.Drawn && p.Role is not ("wall" or "ceiling")).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return point =>
        {
            var exclude = new Godot.Collections.Array<Rid>();
            for (var tries = 0; tries < 8; tries++)
            {
                using var query = PhysicsRayQueryParameters3D.Create(new Vector3(point.X, top, point.Y), new Vector3(point.X, bottom, point.Y), RoomBuilder.WorldLayer, exclude);
                using var hit = space.IntersectRay(query);
                if (hit.Count == 0) return null;
                if (hit["collider"].AsGodotObject() is Node node && node.HasMeta("entity_id") && ground.Contains(node.GetMeta("entity_id").AsString()))
                    return hit["position"].AsVector3().Y;
                exclude.Add(hit["rid"].AsRid());
            }
            return null;
        };
    }

    /// <summary>The trees that can hold a string's end: every tree copy's trunk (its base, radius and top).</summary>
    public static List<CharmPlan.Tree> Trees(RoomData room, IEnumerable<HamletThing> near)
    {
        var trees = new List<CharmPlan.Tree>();
        var spots = near.Select(t => t.Ground).ToArray();
        if (spots.Length == 0) return trees;
        foreach (var set in room.Copies?.Sets.Where(s => s.Kind == PlantKind.Tree) ?? Enumerable.Empty<PlantSet>())
            for (var row = 0; row < set.Count; row++)
            {
                var at = set.Position(row);
                var flat = new Vector2(at.X, at.Z);
                if (!spots.Any(s => s.DistanceTo(flat) < Tuning.SpanMaxM + 0.8f)) continue;
                var shape = set.Tree(row);
                trees.Add(new CharmPlan.Tree(at, shape.TrunkRadiusM, shape.TrunkTopM - at.Y));
            }
        return trees;
    }

    /// <summary>Plan the hamlet's charm now with this ground, and build its pools (the first physics frame calls it; tests call it with their own ground).</summary>
    public void PlanNow(Func<Vector2, float?> groundAt)
    {
        if (_room == null || Survey == null) return;
        Planned = true;
        if (!Enabled) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var accents = _look != null && IsInstanceValid(_look) && _look.Preset != null ? _look.Preset.Accents : Tuning.Accents;
        var structures = Survey.Things.Where(t => t.Kind is HamletKind.Cottage or HamletKind.Tower).ToList();
        Plan = CharmPlan.Make(_room.RoomId, Survey, _room.Spawns, Trees(_room, structures), groundAt, _room.Sea?.LevelM, accents, Tuning.Seed);
        _wind = WindAxis();
        Build();
        clock.Stop();
        Summary = Plan.Chimneys.Count + Plan.Lanterns.Count + Plan.Strings.Count + Plan.Bulbs.Count == 0
            ? "the island has no hamlet"
            : $"{Survey.Count(HamletKind.Cottage)} cottages, {Survey.Count(HamletKind.Tower)} towers, {Survey.Count(HamletKind.Fence)} fences, {Survey.Count(HamletKind.Lantern)} lanterns, " +
              $"{Survey.Count(HamletKind.DryingLine)} drying lines: smoke from {Plan.Chimneys.Count} chimneys, {Plan.Windows.Count} windows, {Plan.Lanterns.Count} lantern glows, " +
              $"{Plan.Strings.Count(s => s.Load == StringLoad.Bunting)} bunting strings ({Plan.Flags.Count} flags), {Plan.Strings.Count(s => s.Load == StringLoad.Laundry)} washing lines ({Plan.Garments.Count} garments), " +
              $"{Plan.Bulbs.Count} fairy lights; planned in {clock.Elapsed.TotalMilliseconds:0} ms{_groundNote}" + (Plan.Notes.Count > 0 ? "; " + string.Join("; ", Plan.Notes) : "");
        GD.Print("CHARM_LOOK: " + Summary);
    }

    /// <summary>The meadow's wind axis (the way the grass's strokes lean, LandscapeLook.Roles["meadow"]), level.</summary>
    public static Vector3 WindAxis()
    {
        var axis = LandscapeLook.Roles.TryGetValue("meadow", out var meadow) ? meadow.Axis : Vector3.Right;
        axis.Y = 0f;
        return axis.LengthSquared() < 1e-8f ? Vector3.Right : axis.Normalized();
    }

    public override void _Process(double delta)
    {
        if (!Running) return;
        Advance((float)delta);
    }

    /// <summary>Move the charm on by dt seconds: the lamps' fade, the smoke, the haze.</summary>
    public void Advance(float dt)
    {
        if (Plan == null) return;
        LifeS += dt;
        var lamps = LampsPinned ?? (_look != null && IsInstanceValid(_look) && _look.LampsOn);
        Lit = Mathf.MoveToward(Lit, lamps ? 1f : 0f, dt / Tuning.LitFadeS);
        if (Lit != _litShown)
        {
            _litShown = Lit;
            foreach (var material in _nightMaterials) material?.SetShaderParameter(LitName, Lit);
            // A night kind is drawn only while it shows: by day it costs nothing.
            foreach (var pool in _nightPools)
                if (pool != null) pool.Visible = Lit > 0f;
        }
        DrawSmoke();
        FollowHaze();
    }

    // ---------- the smoke, as pure functions (the look suite checks them) ----------

    /// <summary>A puff of a chimney's stream at a moment: where it is, how wide, how opaque (0 when it is between lives). Thicker as lit goes to 1.</summary>
    public static (Vector3 At, float SizeM, float Opacity, float Spin) Puff(Chimney chimney, int index, double t, Vector3 wind, float lit)
    {
        var life = Tuning.SmokeLifeS;
        var u = (float)((t / life + chimney.Phase + index / (float)Tuning.SmokePuffs) % 1.0);
        var age = u * life;
        var h1 = Frac(chimney.Phase * 7.13f + index * 0.618034f);
        var h2 = Frac(chimney.Phase * 3.71f + index * 0.414214f);
        // It rises slower as it cools, and the wind takes more of it as it climbs.
        var rise = Tuning.SmokeRiseMps * age * (1f - 0.35f * u);
        var drift = Tuning.SmokeDriftMps * age * (0.4f + 0.6f * u);
        var side = new Vector3(-wind.Z, 0f, wind.X);
        var wobble = side * (Tuning.SmokeWobbleM * u * Mathf.Sin(Mathf.Tau * (h1 + age * 0.23f)));
        var at = chimney.TopM + Vector3.Up * (Tuning.SmokeLiftM + rise) + wind * drift + wobble;
        var size = Mathf.Lerp(Tuning.SmokeStartM, Tuning.SmokeEndM, Mathf.Sqrt(u)) * (0.85f + 0.3f * h2) * Mathf.Max(0.6f, chimney.WidthM / Tuning.SmokeChimneyM);
        var density = Mathf.Lerp(Tuning.SmokeDay, Tuning.SmokeNight, lit);
        // It thickens as it leaves the chimney and thins slowly as it spreads.
        var opacity = density * Mathf.SmoothStep(0f, 0.12f, u) * Mathf.Pow(1f - u, 1.5f);
        return (at, size, opacity, h1);
    }

    private static float Frac(float x) => x - Mathf.Floor(x);

    private void DrawSmoke()
    {
        if (Smoke?.Multimesh is not { } smoke || Plan == null) return;
        var n = 0;
        foreach (var chimney in Plan.Chimneys)
            for (var i = 0; i < Tuning.SmokePuffs; i++)
            {
                var (at, size, opacity, spin) = Puff(chimney, i, LifeS, _wind, Lit);
                WriteTransform(_smokeBuffer, n, Basis.Identity.Scaled(Vector3.One * size), at);
                WriteColourCustom(_smokeBuffer, n, new Color(1f, 1f, 1f, opacity), spin, 0f, 0f, 0f);
                n++;
            }
        RenderingServer.MultimeshSetBuffer(smoke.GetRid(), _smokeBuffer);
    }

    // ---------- building the pools ----------

    private void Build()
    {
        foreach (var pool in Pools.ToArray())
            if (IsInstanceValid(pool)) { RemoveChild(pool); pool.QueueFree(); }
        _buffers.Clear();
        Smoke = Bunting = Laundry = Windows = Lanterns = FairyLights = null;
        if (Plan == null) return;
        _smokeMaterial = SmokeMaterial();
        _buntingMaterial = ClothMaterial(laundry: false, _wind);
        _laundryMaterial = ClothMaterial(laundry: true, _wind);
        _windowMaterial = GlowMaterial(GlowKind.Window);
        _lanternMaterial = GlowMaterial(GlowKind.Lantern);
        _bulbMaterial = GlowMaterial(GlowKind.Bulb);
        _litShown = -1f;
        var quad = new QuadMesh { Size = Vector2.One };

        if (Plan.Chimneys.Count > 0)
        {
            var count = Plan.Chimneys.Count * Tuning.SmokePuffs;
            _smokeBuffer = new float[count * Stride];
            var box = Bounds(Plan.Chimneys.Select(c => c.TopM)).Grow(Tuning.SmokeEndM);
            box = box.Merge(box.Expand(box.GetCenter() + Vector3.Up * (Tuning.SmokeRiseMps * Tuning.SmokeLifeS + Tuning.SmokeEndM)))
                .Grow(Tuning.SmokeDriftMps * Tuning.SmokeLifeS + Tuning.SmokeEndM);
            Smoke = Pool("Smoke", quad, count, _smokeMaterial, box, _smokeBuffer);
            DrawSmoke();
        }
        if (Plan.Windows.Count > 0)
        {
            var buffer = new float[Plan.Windows.Count * Stride];
            for (var i = 0; i < Plan.Windows.Count; i++)
            {
                var pane = Plan.Windows[i];
                WriteTransform(buffer, i, new Basis(pane.Frame.X * pane.SizeM.X, pane.Frame.Y * pane.SizeM.Y, pane.Frame.Z), pane.CentreM);
                WriteColourCustom(buffer, i, Colors.White, pane.Phase, 0f, 0f, 0f);
            }
            Windows = Pool("Windows", quad, Plan.Windows.Count, _windowMaterial, Bounds(Plan.Windows.Select(w => w.CentreM)).Grow(0.1f), buffer);
        }
        if (Plan.Lanterns.Count > 0)
        {
            var buffer = new float[Plan.Lanterns.Count * Stride];
            for (var i = 0; i < Plan.Lanterns.Count; i++)
            {
                var glow = Plan.Lanterns[i];
                WriteTransform(buffer, i, Basis.Identity.Scaled(Vector3.One * glow.SizeM), glow.CentreM);
                WriteColourCustom(buffer, i, Colors.White, glow.Phase, 0f, 0f, 0f);
            }
            Lanterns = Pool("Lanterns", quad, Plan.Lanterns.Count, _lanternMaterial, Bounds(Plan.Lanterns.Select(l => l.CentreM)).Grow(0.3f), buffer);
        }
        if (Plan.Bulbs.Count > 0)
        {
            var buffer = new float[Plan.Bulbs.Count * Stride];
            for (var i = 0; i < Plan.Bulbs.Count; i++)
            {
                var bulb = Plan.Bulbs[i];
                WriteTransform(buffer, i, Basis.Identity.Scaled(Vector3.One * Tuning.BulbSizeM), bulb.AtM);
                WriteColourCustom(buffer, i, bulb.Tint, bulb.Phase, 0f, 0f, 0f);
            }
            // A bulb is drawn wider with distance (never under its pixels): its box reaches a few metres round the hamlet.
            FairyLights = Pool("FairyLights", quad, Plan.Bulbs.Count, _bulbMaterial, Bounds(Plan.Bulbs.Select(b => b.AtM)).Grow(2f), buffer);
        }
        if (Plan.Flags.Count > 0) Bunting = ClothPool("Bunting", CharmMeshes.Flag(), Plan.Flags, laundry: false);
        if (Plan.Garments.Count > 0) Laundry = ClothPool("Laundry", CharmMeshes.Garment(), Plan.Garments, laundry: true);
        _nightMaterials = new[] { _windowMaterial, _lanternMaterial, _bulbMaterial };
        _nightPools = new[] { Windows, Lanterns, FairyLights };
        foreach (var pool in _nightPools)
            if (pool != null) pool.Visible = false;
    }

    /// <summary>
    /// A pool of cloth on its strings: every piece with its own bit of string (a flag) or none (a garment), and string-only pieces
    /// where the cloth leaves the string bare (between the anchors and the bunting's ends; the whole washing line).
    /// </summary>
    private MultiMeshInstance3D ClothPool(string name, Mesh mesh, List<Cloth> pieces, bool laundry)
    {
        var rows = new List<(Basis Basis, Vector3 At, Color Colour, float Phase, float SpanPhase, float S, float DsPerUnit, float Mode)>();
        foreach (var group in pieces.GroupBy(p => p.SpanIndex))
        {
            var span = Plan!.Strings[group.Key];
            var length = span.LengthM;
            foreach (var piece in group)
                rows.Add((new Basis(piece.Frame.X * piece.WidthM, piece.Frame.Y * piece.DropM, piece.Frame.Z), piece.TopM, piece.Colour, piece.Phase, span.Phase, piece.S,
                    piece.WidthM / length, laundry ? CharmMeshes.ClothOnly : CharmMeshes.ClothAndString));
            // The bare string: for bunting the two ends (its flags carry the rest), for the washing all of it.
            var bare = new List<(float From, float To)>();
            if (laundry) bare.Add((0f, 1f));
            else
            {
                var ordered = group.OrderBy(p => p.S).ToArray();
                var half = 0.5f * Tuning.FlagSpacingM / length;
                bare.Add((0f, ordered[0].S - half));
                bare.Add((ordered[^1].S + half, 1f));
            }
            foreach (var (from, to) in bare)
            {
                if (to - from <= 1e-4f) continue;
                var segments = Math.Max(1, (int)Math.Ceiling((to - from) * length / Tuning.StringPieceM));
                for (var k = 0; k < segments; k++)
                {
                    var s0 = from + (to - from) * k / segments;
                    var s1 = from + (to - from) * (k + 1) / segments;
                    var a = span.At(s0);
                    var b = span.At(s1);
                    var along = (b - a).Normalized();
                    var up = (Vector3.Up - along * along.Dot(Vector3.Up)).Normalized();
                    var piece = a.DistanceTo(b) * 1.04f;
                    rows.Add((new Basis(along * piece, up, along.Cross(up)), (a + b) * 0.5f, Colors.White, span.Phase, span.Phase, (s0 + s1) * 0.5f, piece / length, CharmMeshes.StringOnly));
                }
            }
        }
        var buffer = new float[rows.Count * Stride];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            WriteTransform(buffer, i, r.Basis, r.At);
            // Its colour, and in the colour's alpha its string's phase (the whole string swings as one); custom: its own phase, where on
            // its span it hangs, how far along the span one unit of its width runs, which of its parts it draws.
            WriteColourCustom(buffer, i, new Color(r.Colour.R, r.Colour.G, r.Colour.B, r.SpanPhase), r.Phase, r.S, r.DsPerUnit, r.Mode);
        }
        var box = Bounds(Plan!.Strings.SelectMany(s => new[] { s.FromM, s.ToM, s.At(0.5f) })).Grow(Tuning.ClothDropMaxM + Tuning.SwayM + 0.05f);
        return Pool(name, mesh, rows.Count, laundry ? _laundryMaterial! : _buntingMaterial!, box, buffer);
    }

    private MultiMeshInstance3D Pool(string name, Mesh mesh, int count, Material material, Aabb box, float[]? buffer = null)
    {
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = mesh,
            InstanceCount = count, CustomAabb = box,
        };
        var pool = new MultiMeshInstance3D
        {
            Name = name, Multimesh = multimesh, MaterialOverride = material, CustomAabb = box,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            VisibilityRangeEnd = Tuning.FarM,
        };
        AddChild(pool);
        if (buffer != null)
        {
            _buffers[pool] = buffer;
            RenderingServer.MultimeshSetBuffer(multimesh.GetRid(), buffer);
        }
        return pool;
    }

    private static Aabb Bounds(IEnumerable<Vector3> points)
    {
        Aabb? box = null;
        foreach (var p in points) box = box?.Expand(p) ?? new Aabb(p, Vector3.Zero);
        return box ?? new Aabb();
    }

    private static void WriteTransform(float[] buffer, int i, Basis b, Vector3 at)
    {
        var o = i * Stride;
        buffer[o + 0] = b.X.X; buffer[o + 1] = b.Y.X; buffer[o + 2] = b.Z.X; buffer[o + 3] = at.X;
        buffer[o + 4] = b.X.Y; buffer[o + 5] = b.Y.Y; buffer[o + 6] = b.Z.Y; buffer[o + 7] = at.Y;
        buffer[o + 8] = b.X.Z; buffer[o + 9] = b.Y.Z; buffer[o + 10] = b.Z.Z; buffer[o + 11] = at.Z;
    }

    private static void WriteColourCustom(float[] buffer, int i, Color colour, float c0, float c1, float c2, float c3)
    {
        var o = i * Stride;
        buffer[o + 12] = colour.R; buffer[o + 13] = colour.G; buffer[o + 14] = colour.B; buffer[o + 15] = colour.A;
        buffer[o + 16] = c0; buffer[o + 17] = c1; buffer[o + 18] = c2; buffer[o + 19] = c3;
    }

    /// <summary>The instance a pool draws at an index (its transform, colour and custom data), read back from what was last written (for the look suite).</summary>
    public (Transform3D Transform, Color Colour, Color Custom) Instance(MultiMeshInstance3D pool, int index)
    {
        var b = _buffers[pool];
        var o = index * Stride;
        var basis = new Basis(new Vector3(b[o], b[o + 4], b[o + 8]), new Vector3(b[o + 1], b[o + 5], b[o + 9]), new Vector3(b[o + 2], b[o + 6], b[o + 10]));
        return (new Transform3D(basis, new Vector3(b[o + 3], b[o + 7], b[o + 11])), new Color(b[o + 12], b[o + 13], b[o + 14], b[o + 15]), new Color(b[o + 16], b[o + 17], b[o + 18], b[o + 19]));
    }

    /// <summary>The look's haze and sea mist, mirrored into the glows (the engine's own fog would add its colour over an additive quad).</summary>
    private void FollowHaze()
    {
        if (_windowMaterial == null) return;
        var environment = _look != null && IsInstanceValid(_look) ? _look.Environment : GetWorld3D()?.Environment;
        var fog = environment is { FogEnabled: true } e && e.FogMode == Godot.Environment.FogModeEnum.Depth
            ? (true, e.FogDepthBegin, e.FogDepthEnd, e.FogDensity, e.FogDepthCurve)
            : (false, 0f, 1f, 0f, 1f);
        if (fog == _fog) return;
        _fog = fog;
        foreach (var material in _nightMaterials)
        {
            if (material == null) continue;
            material.SetShaderParameter("fog_on", fog.Item1 ? 1f : 0f);
            material.SetShaderParameter("fog_begin_m", fog.Item2);
            material.SetShaderParameter("fog_end_m", fog.Item3);
            material.SetShaderParameter("fog_density", fog.Item4);
            material.SetShaderParameter("fog_curve", fog.Item5);
        }
    }

    // ---------- materials ----------

    public static ShaderMaterial SmokeMaterial()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(SmokeShaderPath), ResourceName = "charm smoke" };
        material.SetShaderParameter("smoke_color", Tuning.SmokeColour);
        material.SetShaderParameter("softness", Tuning.SmokeSoftness);
        material.SetShaderParameter("lumps", Tuning.SmokeLumps);
        material.SetShaderParameter("light_wrap", Tuning.SmokeWrap);
        material.SetShaderParameter("scatter", Tuning.SmokeScatter);
        return material;
    }

    public static ShaderMaterial ClothMaterial(bool laundry, Vector3 wind)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(ClothShaderPath), ResourceName = laundry ? "charm laundry" : "charm bunting" };
        material.SetShaderParameter("wind_dir", wind);
        material.SetShaderParameter("sway_m", laundry ? Tuning.LaundrySwayM : Tuning.SwayM);
        material.SetShaderParameter("sway_period_s", laundry ? Tuning.LaundrySwayPeriodS : Tuning.SwayPeriodS);
        material.SetShaderParameter("swing_m", Tuning.StringSwingM);
        material.SetShaderParameter("flutter_m", Tuning.FlutterM);
        material.SetShaderParameter("flutter_period_s", Tuning.FlutterPeriodS);
        material.SetShaderParameter("string_half_m", Tuning.StringHalfM);
        material.SetShaderParameter("string_color", Tuning.StringColour);
        material.SetShaderParameter("tone_variation", Tuning.ClothToneVariation);
        material.SetShaderParameter("light_wrap", Tuning.ClothWrap);
        material.SetShaderParameter("sheen", Tuning.ClothSheen);
        return material;
    }

    public enum GlowKind { Window, Lantern, Bulb }

    public static ShaderMaterial GlowMaterial(GlowKind kind)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(GlowShaderPath), ResourceName = "charm " + kind.ToString().ToLowerInvariant() };
        material.SetShaderParameter("glow_color", kind switch { GlowKind.Window => Tuning.WindowColour, GlowKind.Lantern => Tuning.LanternColour, _ => Colors.White });
        material.SetShaderParameter("energy", kind switch { GlowKind.Window => Tuning.WindowEnergy, GlowKind.Lantern => Tuning.LanternEnergy, _ => Tuning.BulbEnergy });
        material.SetShaderParameter("lit", 0f);
        material.SetShaderParameter("billboard", kind == GlowKind.Window ? 0f : 1f);
        material.SetShaderParameter("min_pixels", kind == GlowKind.Bulb ? Tuning.BulbMinPixels : 0f);
        material.SetShaderParameter("heart_sharpness", kind == GlowKind.Lantern ? Tuning.LanternHeartSharpness : Tuning.BulbHeartSharpness);
        material.SetShaderParameter("halo", kind == GlowKind.Lantern ? Tuning.LanternHaloAmount : Tuning.BulbHalo);
        material.SetShaderParameter("twinkle", kind switch { GlowKind.Window => Tuning.WindowFlicker, GlowKind.Lantern => Tuning.LanternFlicker, _ => Tuning.BulbTwinkle });
        material.SetShaderParameter("twinkle_min_s", Tuning.TwinkleMinS);
        material.SetShaderParameter("twinkle_max_s", Tuning.TwinkleMaxS);
        material.SetShaderParameter("mullion", Tuning.Mullion);
        material.SetShaderParameter("fog_on", 0f);
        material.SetShaderParameter("fog_begin_m", 0f);
        material.SetShaderParameter("fog_end_m", 1f);
        material.SetShaderParameter("fog_density", 0f);
        material.SetShaderParameter("fog_curve", 1f);
        return material;
    }
}
