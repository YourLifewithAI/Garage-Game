using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look;

/// <summary>
/// What one season does to the island's land (L10), each change from 0 (summer, the land as the generator drew it) to 1 (all the
/// way to the season's colour). Leaves: Fresh toward the spring colour, Turn toward each tree's colour between gold and rust (autumn),
/// Pale toward the winter pale, Blossom how far into blossom the broadleaves are (Tuning.BlossomTrees of them flower). Ground: Snow the share of upward faces
/// under a dusting (leaves, rock and cliff tops), Straw the meadow's tips and the grass toward straw, Frost the pale cool lift on the
/// meadow and the grass at dawn, Wet how much darker (and so richer) soil and paths are. MeadowGreen the meadow fresher, EvergreenDark
/// a conifer toward its winter colour, RoomOakSaturation the saturation of the room's live oak (1 leaves it).
/// </summary>
public sealed record LandSeasonLook(
    float Fresh, float Turn, float Pale, float Blossom, float Snow, float Straw, float Frost, float Wet, float MeadowGreen, float EvergreenDark,
    float RoomOakSaturation);

/// <summary>
/// The land's season at one moment, as the shaders get it. Leaf is (fresh, turn, pale, blossom), Ground (straw, frost, wet, snow),
/// Tone (meadow green, evergreen dark, how far apart the trees start to turn, how long one takes), each the four seasons' numbers
/// blended by the moment's weights; frost is scaled by FrostTime, the hour's share of it (all of it at night and dawn, melting as the
/// morning sun climbs). All zero is the land as it was drawn.
/// </summary>
public readonly record struct LandSeasonState(SeasonWeights Weights, Vector4 Leaf, Vector4 Ground, Vector4 Tone, float FrostTime, float RoomOakSaturation);

/// <summary>
/// The seasons on the land (packet L10, docs/look/SEASONS.md). The grade, the sun and the sky already follow the season; this gives
/// the island's leaves, meadow and ground theirs from the same cross-fade (LookMoment.Weights): broadleaf trees fresh in spring and
/// some in blossom, as drawn in summer, gold to rust tree by tree in autumn, pale with a dusting of snow in winter; conifers green
/// all year and a touch darker in winter; grass, the low herbs and the meadow's tips to straw in autumn, frost on them in winter; soil and paths darker in the
/// wet seasons; snow on the tops of rock and cliffs. Every change is albedo moved along a season ramp: the marks stay the harness's,
/// the light never changes.
///
/// The state reaches painterly_land.gdshader as Godot global shader parameters, registered here at run time on the first moment the
/// look applies (RenderingServer.GlobalShaderParameterAdd; project.godot stays untouched) and set once per moment, so no material is
/// walked. Godot 4.7 resolves a global when a material is drawn, not when its shader loads, so the land shader MaterialLibrary loads
/// before the first moment takes the values; a land material drawn before any look applied reads zeros, which is summer. The room's
/// own painterly trees (painterly_foliage.gdshader, a live oak and an Ashe juniper, both evergreen in Texas) take a per-material
/// saturation through painterly_assets.gd: the oak shifts gently, the juniper not at all.
///
/// Every number is in Tuning this round; the report proposes the preset block (x_look_seasons.looks.SEASON.land, x_look_seasons.land_colors).
/// </summary>
public static class LandSeason
{
    /// <summary>The numbers of the land's seasons (code constants this round; a later preset version carries them, as the effects' do).</summary>
    public static class Tuning
    {
        /// <summary>The four seasons' changes. Autumn and spring unmistakable, winter gentle (the plan's recommended strength).</summary>
        public static readonly IReadOnlyDictionary<string, LandSeasonLook> Looks = new Dictionary<string, LandSeasonLook>
        {
            ["winter"] = new(Fresh: 0f, Turn: 0f, Pale: 0.7f, Blossom: 0f, Snow: 0.5f, Straw: 0.35f, Frost: 0.5f, Wet: 0.05f, MeadowGreen: 0f, EvergreenDark: 0.8f, RoomOakSaturation: 0.85f),
            ["spring"] = new(Fresh: 0.6f, Turn: 0f, Pale: 0f, Blossom: 1f, Snow: 0f, Straw: 0f, Frost: 0f, Wet: 0.1f, MeadowGreen: 0.5f, EvergreenDark: 0f, RoomOakSaturation: 1.1f),
            ["summer"] = new(Fresh: 0f, Turn: 0f, Pale: 0f, Blossom: 0f, Snow: 0f, Straw: 0f, Frost: 0f, Wet: 0f, MeadowGreen: 0f, EvergreenDark: 0f, RoomOakSaturation: 1f),
            ["autumn"] = new(Fresh: 0f, Turn: 1f, Pale: 0f, Blossom: 0f, Snow: 0f, Straw: 0.6f, Frost: 0f, Wet: 0.14f, MeadowGreen: 0f, EvergreenDark: 0f, RoomOakSaturation: 1f),
        };

        // The summer colours the season's colours are measured against: the harness palette's own (linear, pipeline/landscape/harness/common.py).
        /// <summary>The foliage role's colour, the summer every leaf and grass blade is drawn from.</summary>
        public static readonly Vector3 FoliageReference = new(0.16f, 0.37f, 0.10f);
        /// <summary>The meadow role's colour.</summary>
        public static readonly Vector3 MeadowReference = new(0.19f, 0.40f, 0.10f);
        /// <summary>The snow role's colour: what a dusting of snow is.</summary>
        public static readonly Vector3 SnowColor = new(0.80f, 0.86f, 0.91f);

        // The season's colours (linear albedo; sRGB in the comments, before the marks, the softening and the grade).
        /// <summary>Spring leaves and grass, a fresh yellow-green (#89bc4b).</summary>
        public static readonly Vector3 Fresh = new(0.25f, 0.50f, 0.07f);
        /// <summary>The first of autumn (#cbaa3f).</summary>
        public static readonly Vector3 Gold = new(0.60f, 0.40f, 0.05f);
        /// <summary>The last of autumn (#aa5d30).</summary>
        public static readonly Vector3 Rust = new(0.40f, 0.11f, 0.03f);
        /// <summary>Winter leaves, pale, cold and thin in colour (#919c89).</summary>
        public static readonly Vector3 Pale = new(0.28f, 0.33f, 0.25f);
        /// <summary>A conifer in winter, a touch darker and bluer than its summer (#61935d).</summary>
        public static readonly Vector3 Evergreen = new(0.12f, 0.29f, 0.11f);
        /// <summary>Grass gone to straw (#b5a465).</summary>
        public static readonly Vector3 Straw = new(0.46f, 0.37f, 0.13f);
        /// <summary>The meadow's tips gone to straw (#ada265).</summary>
        public static readonly Vector3 MeadowStraw = new(0.42f, 0.36f, 0.13f);
        /// <summary>The meadow in spring, its greenest (#79b650).</summary>
        public static readonly Vector3 MeadowFresh = new(0.19f, 0.47f, 0.08f);
        /// <summary>Blossom, a pale pink (#f1c8d4): pink enough never to read as the winter's snow.</summary>
        public static readonly Vector3 Blossom = new(0.88f, 0.58f, 0.66f);
        /// <summary>Frost, a pale cool white (#cedae5).</summary>
        public static readonly Vector3 Frost = new(0.62f, 0.70f, 0.78f);

        /// <summary>The share of broadleaves that flower in spring (each tree by its own number), and the share of a flowering tree's upward dabs that blossom.</summary>
        public const float BlossomTrees = 0.35f, BlossomDabs = 0.45f;

        /// <summary>How far apart (of the autumn turn's 0 to 1) the trees of a wood start to turn, so a wood turns tree by tree, not all at once.</summary>
        public const float TurnStagger = 0.45f;
        /// <summary>How long (of the turn's 0 to 1) one tree takes to turn: every tree has turned when the turn is whole.</summary>
        public const float TurnSpan = 0.5f;

        /// <summary>The share of the frost left by day, once the morning sun stands FrostMeltElevationDeg high (all of it at night and dawn).</summary>
        public const float FrostByDay = 0.45f;
        /// <summary>How high the morning sun stands when the frost has melted to its daytime share.</summary>
        public const float FrostMeltElevationDeg = 25f;
        /// <summary>How far below the horizon the evening sun goes before the night's frost is back in full (civil twilight).</summary>
        public const float FrostReturnElevationDeg = -6f;
    }

    /// <summary>Which way a plant's leaves go through the year: a broadleaf's (and any foliage not otherwise known), a conifer's, or grass's (and a herb's).</summary>
    public enum Habit { Deciduous = 0, Evergreen = 1, Grass = 2 }

    /// <summary>The land shader's per-instance habit (instance uniform season_habit): the plant copies' view sets it on each set's tiles (HabitFor).</summary>
    public const string HabitParameter = "season_habit";

    /// <summary>
    /// The habit of a plant set by its fuel class (the manifest's own word for what burns how: a conifer, a broadleaf, a shrub, grass,
    /// a herb): conifers are evergreen; grass and the low herbs (flowers, ferns, heather) go to straw and frost, as the meadow does;
    /// a broadleaf's and a shrub's leaves turn, and so does any foliage not otherwise known.
    /// </summary>
    public static Habit HabitFor(string fuelClass) => fuelClass switch
    {
        "conifer" => Habit.Evergreen,
        "grass" or "herb" => Habit.Grass,
        _ => Habit.Deciduous,
    };

    /// <summary>The global shader parameters this sets, with their types, in the order painterly_land.gdshader declares them.</summary>
    public static readonly IReadOnlyList<(string Name, RenderingServer.GlobalShaderParameterType Type)> Globals = new[]
    {
        ("season_leaf", RenderingServer.GlobalShaderParameterType.Vec4),
        ("season_ground", RenderingServer.GlobalShaderParameterType.Vec4),
        ("season_tone", RenderingServer.GlobalShaderParameterType.Vec4),
        ("season_bloom", RenderingServer.GlobalShaderParameterType.Vec4),
        ("season_fresh", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_gold", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_rust", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_pale", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_evergreen", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_straw", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_meadow_straw", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_meadow_fresh", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_blossom", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_snow", RenderingServer.GlobalShaderParameterType.Vec3),
        ("season_frost", RenderingServer.GlobalShaderParameterType.Vec3),
    };

    /// <summary>The script that paints the room's own trees (the live oak and the Ashe juniper) and its function that takes the season.</summary>
    public const string RoomTreesScript = "res://scripts/painterly_assets.gd";
    public const string RoomTreesFunction = "apply_season";

    private static readonly StringName[] Names = Globals.Select(g => new StringName(g.Name)).ToArray();
    private static readonly StringName RoomTreesCall = new(RoomTreesFunction);
    private static bool _registered;
    private static GodotObject? _roomTrees;
    private static readonly Dictionary<string, Variant> Last = new();

    /// <summary>The state applied last (null before the first moment), and the value each global was given, for the look's report and its tests.</summary>
    public static LandSeasonState? Applied { get; private set; }
    public static IReadOnlyDictionary<string, Variant> AppliedValues => Last;
    /// <summary>Whether the globals were registered here (false when the project declares them, or before the first moment).</summary>
    public static bool RegisteredHere { get; private set; }

    /// <summary>The land's season at a moment: the moment's season weights, and its sun for the frost's hour.</summary>
    public static LandSeasonState At(LookMoment moment) => At(moment.Weights, moment.SunElevationDeg, moment.SunBearingDeg);

    public static LandSeasonState At(SeasonWeights weights, float sunElevationDeg, float sunBearingDeg)
    {
        float Blend(Func<LandSeasonLook, float> field) =>
            weights.Winter * field(Tuning.Looks["winter"]) + weights.Spring * field(Tuning.Looks["spring"])
            + weights.Summer * field(Tuning.Looks["summer"]) + weights.Autumn * field(Tuning.Looks["autumn"]);
        var frostTime = FrostTime(sunElevationDeg, sunBearingDeg);
        return new LandSeasonState(weights,
            new Vector4(Blend(l => l.Fresh), Blend(l => l.Turn), Blend(l => l.Pale), Blend(l => l.Blossom)),
            new Vector4(Blend(l => l.Straw), Blend(l => l.Frost) * frostTime, Blend(l => l.Wet), Blend(l => l.Snow)),
            new Vector4(Blend(l => l.MeadowGreen), Blend(l => l.EvergreenDark), Tuning.TurnStagger, Tuning.TurnSpan),
            frostTime, Blend(l => l.RoomOakSaturation));
    }

    /// <summary>
    /// How much of the frost the hour keeps: all of it while the sun is down and at dawn, melting to FrostByDay as the morning sun climbs
    /// to FrostMeltElevationDeg, none of it back until the evening sun is below the horizon, and all of it again by the end of civil
    /// twilight. Continuous through the day (the morning is the sun in the east, bearing under 180).
    /// </summary>
    public static float FrostTime(float sunElevationDeg, float sunBearingDeg)
    {
        var morning = sunBearingDeg < 180f;
        var share = morning
            ? 1f - Mathf.SmoothStep(0f, Tuning.FrostMeltElevationDeg, sunElevationDeg)
            : 1f - Mathf.SmoothStep(Tuning.FrostReturnElevationDeg, 0f, sunElevationDeg);
        return Mathf.Lerp(Tuning.FrostByDay, 1f, share);
    }

    /// <summary>A season's colour as the shader takes it: channel by channel over the summer colour it replaces.</summary>
    public static Vector3 Ratio(Vector3 colour, Vector3 summer) => new(colour.X / summer.X, colour.Y / summer.Y, colour.Z / summer.Z);

    /// <summary>The land's season at the look's moment, into the land shader's globals and onto the room's trees. Called once per moment.</summary>
    public static void Apply(LookMoment moment) => Apply(At(moment));

    public static void Apply(LandSeasonState state)
    {
        Register();
        Set(0, state.Leaf);
        Set(1, state.Ground);
        Set(2, state.Tone);
        Set(3, new Vector4(Tuning.BlossomTrees, Tuning.BlossomDabs, 0f, 0f));
        Set(4, Ratio(Tuning.Fresh, Tuning.FoliageReference));
        Set(5, Ratio(Tuning.Gold, Tuning.FoliageReference));
        Set(6, Ratio(Tuning.Rust, Tuning.FoliageReference));
        Set(7, Ratio(Tuning.Pale, Tuning.FoliageReference));
        Set(8, Ratio(Tuning.Evergreen, Tuning.FoliageReference));
        Set(9, Ratio(Tuning.Straw, Tuning.FoliageReference));
        Set(10, Ratio(Tuning.MeadowStraw, Tuning.MeadowReference));
        Set(11, Ratio(Tuning.MeadowFresh, Tuning.MeadowReference));
        Set(12, Tuning.Blossom);
        Set(13, Tuning.SnowColor);
        Set(14, Tuning.Frost);
        Applied = state;
        // The room's own trees: the live oak's saturation (the juniper keeps its own all year).
        _roomTrees ??= GD.Load<GDScript>(RoomTreesScript);
        _roomTrees.Call(RoomTreesCall, state.RoomOakSaturation);
        // Prototype (run2/look-grass-proto): the grass's canopy, which the meadow floor draws too.
        GrassLook.ApplyGlobals();
    }

    /// <summary>
    /// Declares the globals with the rendering server, once a process, before the first moment sets them: unless the project declares
    /// them itself ([shader_globals] in project.godot), when declaring them again would be an error.
    /// </summary>
    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        if (Globals.All(g => ProjectSettings.HasSetting("shader_globals/" + g.Name))) return;
        for (var i = 0; i < Globals.Count; i++)
            RenderingServer.GlobalShaderParameterAdd(Names[i], Globals[i].Type, Globals[i].Type == RenderingServer.GlobalShaderParameterType.Vec4 ? Vector4.Zero : Vector3.Zero);
        RegisteredHere = true;
    }

    private static void Set(int index, Variant value)
    {
        RenderingServer.GlobalShaderParameterSet(Names[index], value);
        Last[Globals[index].Name] = value;
    }
}
