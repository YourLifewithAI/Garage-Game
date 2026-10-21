using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GarageGame.Native.Look;

/// <summary>Surface patterns the painterly shader knows; values match the constants in painterly_material.gdshader.</summary>
public enum PaintPattern { None = 0, Wood = 1, Fabric = 2, Plaster = 3, Paper = 4, Cardboard = 5, Brushed = 6, Speckle = 7, Smooth = 8, Stone = 9 }

/// <summary>
/// How a material role is painted beyond the preset's per-role treatment: the pattern, the size and direction
/// of brush marks, and how the role takes light. Sizes are metres in the real-scale room. Calm is how far the role's
/// colour is drawn toward its own grey, so the large quiet surfaces of a room (walls, floor, ceiling) can sit back and
/// let the small accents and the figures carry the colour: colour contrast as a way to focus (the founder, 7 October).
/// </summary>
public sealed record RoleLook(
    PaintPattern Pattern, float StrokeScaleM, float StrokeStretch, Vector3 StrokeAxis, float PatternScaleM,
    float Variation, float Wrap, float TerminatorWarmth, float Sheen, float Specular, float TextureStrength, float TextureScaleM,
    float Calm = 0f);

/// <summary>Painterly shader multipliers shared by every role.</summary>
public sealed record PaintTuning(
    float ToneWash, float ToneStrokes, float ToneBristle, float ToneGain, float SofteningCalm, float TemperatureVariation,
    float PastelMix, float PastelChroma, float PastelScale, float PastelLift, float WearBrightness, float WearLift,
    float CavityDarkening, float StrokeNormalGain, float MarkFadeStart, float MarkFadeEnd, float BevelFraction, float BevelMaxM,
    float AccentMix, float AccentDarken, float PhotoBlurLod = 2.5f);

/// <summary>
/// Shadows. The key (sun and moon) and the positional lights (window sky fill, lamps) have their own biases, because
/// Godot measures them differently (directional normal bias in texels of the split, positional in texels of the
/// atlas slot). Both stay small enough that a 10 cm body's shadow stays attached to its base. KeySplit1 is the
/// fraction of the key's shadow distance the first split covers.
/// </summary>
public sealed record ShadowTuning(
    int KeySplits, float KeyAngularBaseDeg, float KeyAngularPerSoftnessDeg, float BlurBase, float BlurPerSoftness,
    float KeyMinDistanceM, float KeyDistancePerDiagonal, float KeySplit1, float KeyBias, float KeyNormalBias,
    float LampBias, float LampNormalBias, float LampSizeBaseM, float LampSizePerSoftnessM);

public sealed record SsaoTuning(float Power, float Detail, float Horizon, float Sharpness, float LightAffect, float AoChannelAffect);

public sealed record GlowTuning(float Strength, string BlendMode, float HdrThreshold, IReadOnlyList<float> Levels);

/// <summary>
/// VoxelGI: resolution, margin, biases and when to use two bounces. The whole shell is baked as a closed interior. MarginM
/// is how far the volume reaches beyond the room's bounds: just enough for the shell's inner face. A margin that takes in
/// the whole slab takes in its outer face too, which the sun lights, and VoxelGI's cones carry that light through the wall
/// into the room (measured: a closed room brightened by 0.1 to 0.44 in luma when the sun was switched on, with no way in).
/// EnvironmentAmbientScale is how much of the time keys' ambient the environment keeps where VoxelGI does not reach.
/// </summary>
public sealed record GiTuning(int Subdiv, float MarginM, float Bias, float NormalBias, float TwoBouncesAbove, float EnvironmentAmbientScale);

/// <summary>
/// Room lights from the manifest's light hints. Lamps: falloff and reach, when they switch on by themselves
/// (daylight below SwitchOnBelowDaylight) until room state carries switches, and how far the style warms their
/// captured colour toward LampTint (storybook lamps glow warmer than life). Windows: a soft sky fill, a spot light
/// WindowStandoffM behind the window hint (outside the room) and WindowLightSizeM across, aimed in along the hint's
/// direction through the opening, in the sky colour of the hour (SkyFillSaturation of its hue: a light blue, not a
/// saturated one) and its brightness scaled by SkyFillEnergy. A lit lamp also shows its bulb, a small emissive sphere
/// GlowRadiusM across glowing at GlowEnergy (0 for none): a warm-white point of light that blooms close to the camera and
/// melts into a creamy bokeh disc in the blurred distance.
/// </summary>
public sealed record LampTuning(
    float Attenuation, float RangePerDiagonal, float WindowSpotAngleDeg, float SwitchOnBelowDaylight,
    float WindowStandoffM, float WindowLightSizeM, float SkyFillEnergy, float SkyFillSaturation, Color LampTint, float LampTintAmount,
    float GlowRadiusM = 0f, float GlowEnergy = 0f);

/// <summary>
/// The sun and the moon. The sun follows a solar model: elevation and bearing from the latitude, the date and the
/// local standard clock hour, with mean solar noon at SolarNoonH on the clock (time zone and longitude) and the
/// equation of time. NegZBearingDeg is the compass bearing of the room's -Z axis (0: -Z is north, so -X is west).
/// The first three numbers are the room's site (RoomSite), not the preset's: a preset on its own carries the fallback
/// site, and StylePreset.WithSite puts a room's own in.
/// Sunrise and sunset are where the sun's centre crosses SunriseElevationDeg (-0.833: the almanac's upper limb with
/// refraction). The key is the sun while the sun is above the horizon (drawn no lower than HorizonElevationDeg), the
/// moon once the sun is below TwilightElevationDeg, and cross-fades between, while the light is dim. The moon stands
/// MoonElevationDeg high outside the room's brightest window (at MoonBearingDeg in a room without one).
/// ReferenceDaylight is where the time keys' daylight rises and falls on their reference day: the keys' sunrise and
/// sunset, onto which the real ones are mapped. With RealClockDaylightSaving the real clock is read as standard time
/// while the machine's time zone is in daylight saving; pinned clocks are always standard time, the same everywhere.
/// </summary>
public sealed record SunTuning(
    float LatitudeDeg, float NegZBearingDeg, float SolarNoonH, bool RealClockDaylightSaving, float SunriseElevationDeg,
    float TwilightElevationDeg, float HorizonElevationDeg, float MoonElevationDeg, float MoonBearingDeg, float ReferenceDaylight);

/// <summary>
/// Season centres (winter, spring, summer, autumn), how long each holds its grade, how strongly the season colours
/// the sunlight, and the day used when the preset does not follow the calendar. Day length comes from the solar model.
/// </summary>
public sealed record SeasonTuning(IReadOnlyList<int> CentreDays, float Hold, float LightStrength, int FixedDayOfYear, IReadOnlyDictionary<string, SeasonLook> Looks);

/// <summary>
/// What a season does beyond its colour grade: how strong the sun is (summer harder, winter weaker), how soft its
/// shadow edge is (a lower ShadowBlur is a harder, brighter-day shadow), how saturated and bright the sky is, and
/// how much cloud it carries. Each is a multiplier, or for cloud an amount from 0 (clear) to 1 (overcast).
/// </summary>
public sealed record SeasonLook(float SunEnergy, float SunBlur, float SkySaturation, float SkyBrightness, float CloudAmount)
{
    public static readonly SeasonLook Neutral = new(1f, 1f, 1f, 1f, 0.5f);
}

/// <summary>
/// The sky seen through a window (review M2: the window was a dark slate square). Zenith and horizon colours for
/// day, dusk and night (the look mixes them by the sun's height and the moon's weight), the clouds' lit and shaded
/// colours, a brightness (the sky is the brightest thing a window shows, so it glows), the sun's disc and its halo,
/// the moon's disc and the stars at night.
/// </summary>
public sealed record SkyTuning(
    Color DayZenith, Color DayHorizon, Color DuskZenith, Color DuskHorizon, Color NightZenith, Color NightHorizon,
    Color CloudLight, Color CloudShade, float Brightness, float SunDiscDeg, float SunHalo, float MoonDiscDeg, float StarStrength,
    float DuskStartDeg, float DuskEndDeg, Color Ground, float GroundSeasonTint);

/// <summary>
/// The colour grade's strengths. NightWithLamps is how much of the night grade applies while the room's lamps are on:
/// the night grade cools and calms a moonlit room, but warm lamplight keeps its colour (the eye adapts to it).
/// </summary>
public sealed record GradeTuning(
    float ShadowTone, float HighlightTone, float SeasonTint, Vector3 WarmthRgb, float NightDesaturate, Vector3 NightTintRgb,
    float NightDeepen, int LutSize, float SeasonTintShadowFade, float NightFullBelow, float NightNoneAbove, float NightWithLamps);

public sealed record DofTuning(
    float TiltPitchGain, float TiltBandNarrowing, float FarTransitionBaseM, float FarTransitionPerM, float FarBlurReference,
    float NearTransitionBase, float NearTransitionPerBlur, float AmountBase, float AmountPerBlur,
    float EyeFocusBodyHeights, float BodyFocusHeightFraction, float EyeCrispBodyHeights,
    float TiltTransitionShortening, float TiltAmountGain, float CompanionFollowM,
    float FocusEaseS = 0.12f, float ObserveBandM = 0.08f, float ObserveFarTransitionM = 0.12f, float ObserveNearTransitionM = 0.1f, float ObserveAmount = 1f);

/// <summary>The play views the HUD's keys switch between (F1 to F4); the observe view (O) is a switch over any of them.</summary>
public enum LookView { Eye, Shoulder, Diorama, Isometric }

/// <summary>
/// How one view blurs (the founder, 9 October, in the island garage: "players are going to want to see the landscape a little
/// further out. Maybe the blurring is only for features further out"; in F4, "having the tilt-shift there makes sense").
/// "miniature" is the preset's own depth of field: a band around the subject and a tilt-shift that grows as the camera looks
/// down, so a high view reads as a model on a table. "far" keeps everything crisp from the lens out to
/// CrispBeyondFocusM past the subject (the whole island around the player), then softens over FarTransitionM, so only the far
/// shore, the distant islands and the horizon melt, at FarAmount (the bokeh's size; its cost grows with its square).
/// </summary>
public sealed record ViewBlur(string Style, float CrispBeyondFocusM = 0f, float FarTransitionM = 0f, float FarAmount = 0f)
{
    public static readonly ViewBlur Miniature = new("miniature");
    public bool Far => Style == "far";
}

/// <summary>
/// Grain and vignette, and the focus pass of the same post effect: colour contrast as a way to focus. Inside the
/// depth-of-field band colour is lifted (FocusSaturation) and out of it drawn back (DefocusSaturation, DefocusDarken),
/// so the avatars and accents in focus carry the colour against a calmer room. Highlights in the blurred distance are
/// pushed up (BokehGain above BokehThreshold) so they melt into the creamy discs of a miniature photograph.
/// </summary>
public sealed record PostTuning(
    float VignetteStart, float VignetteEnd, float GrainFine, float GrainSoft, float GrainSoftPx,
    float FocusSaturation = 1f, float DefocusSaturation = 1f, float DefocusDarken = 0f, float BokehThreshold = 4f, float BokehGain = 0f);

/// <summary>
/// Every look-defining number that is not a schema field, read from the preset's x_look_* extension blocks so a
/// pinned preset version reproduces its look (with the shader files it names unchanged). Blocks and fields are
/// optional for presets written before them, defaulting to the values the draft look was tuned with, and
/// Defaulted names every number a preset left to those defaults; the shipped preset states them all. Unknown
/// fields are refused so a typo cannot be silently ignored.
///
/// Role marks: a preset with an x_look_role_marks block paints every role it lists with those marks and every
/// other role with its "default" entry; only a preset without the block falls back to the built-in role table.
/// </summary>
public sealed record LookTuning(
    IReadOnlyDictionary<string, RoleLook> RoleMarks, RoleLook DefaultMarks, PaintTuning Paint, ShadowTuning Shadows,
    SsaoTuning Ssao, GlowTuning Glow, GiTuning Gi, LampTuning Lamps, SunTuning Sun, SeasonTuning Seasons,
    GradeTuning Grade, DofTuning Dof, PostTuning Post, SkyTuning Sky)
{
    /// <summary>"block.field" for every look number the preset left to the code's defaults (empty for the shipped preset).</summary>
    public IReadOnlyList<string> Defaulted { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How each play view blurs (x_look_views). A preset without the block (v1, written before it) blurs every view with its own
    /// miniature depth of field, exactly as it always did.
    /// </summary>
    public IReadOnlyDictionary<LookView, ViewBlur> Views { get; init; } = DefaultViews;

    public static readonly IReadOnlyDictionary<LookView, ViewBlur> DefaultViews = Enum.GetValues<LookView>().ToDictionary(v => v, _ => ViewBlur.Miniature);

    public RoleLook MarksFor(string role) => RoleMarks.TryGetValue(role, out var marks) ? marks : DefaultMarks;

    public ViewBlur BlurFor(LookView view) => Views.TryGetValue(view, out var blur) ? blur : ViewBlur.Miniature;

    /// <summary>The x_look_* blocks LookTuning reads (StylePreset reads the others).</summary>
    public static readonly IReadOnlyList<string> Blocks = new[]
    {
        "x_look_role_marks", "x_look_paint", "x_look_shadows", "x_look_ssao", "x_look_glow", "x_look_gi", "x_look_lamps",
        "x_look_sun", "x_look_seasons", "x_look_grade", "x_look_dof", "x_look_post", "x_look_sky", "x_look_views",
    };

    /// <summary>
    /// Blocks added after a preset version was written, with the first version that states them: an older version leaves them
    /// to the code's defaults, which reproduce its look (it may not change once candidate), and every later version states them.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> BlocksSince = new Dictionary<string, int> { ["x_look_views"] = 2 };

    /// <summary>The view names of x_look_views, in the HUD's key order (F1 eye, F2 shoulder, F3 diorama, F4 isometric).</summary>
    public static string ViewName(LookView view) => view.ToString().ToLowerInvariant();

    /// <summary>The four seasons in calendar order from midwinter, which is the order of x_look_seasons.centre_days.</summary>
    public static readonly string[] SeasonNames = { "winter", "spring", "summer", "autumn" };

    public static readonly RoleLook DefaultRoleMarks = new(PaintPattern.None, 0.05f, 2f, Vector3.Up, 0.1f, 0.4f, 0.25f, 0.2f, 0f, 0.35f, 0.35f, 0.6f);

    private static RoleLook M(PaintPattern p, float stroke, float stretch, Vector3 axis, float pattern, float variation, float wrap, float warmth, float sheen, float specular) =>
        new(p, stroke, stretch, axis, pattern, variation, wrap, warmth, sheen, specular, 0.35f, 0.6f);

    public static readonly IReadOnlyDictionary<string, RoleLook> DefaultRoles = new Dictionary<string, RoleLook>
    {
        ["painted_wall"] = M(PaintPattern.Plaster, 0.16f, 1.8f, Vector3.Up, 0.3f, 0.55f, 0.30f, 0.25f, 0f, 0.20f),
        ["plaster"] = M(PaintPattern.Plaster, 0.18f, 1.8f, Vector3.Right, 0.4f, 0.55f, 0.30f, 0.25f, 0f, 0.20f),
        ["wallpaper"] = M(PaintPattern.Plaster, 0.06f, 5f, Vector3.Up, 0.20f, 0.40f, 0.30f, 0.20f, 0f, 0.20f),
        ["wood"] = M(PaintPattern.Wood, 0.04f, 6f, Vector3.Right, 0.11f, 0.80f, 0.20f, 0.30f, 0f, 0.35f),
        ["wood_painted"] = M(PaintPattern.Wood, 0.05f, 5f, Vector3.Right, 0.11f, 0.40f, 0.25f, 0.25f, 0f, 0.30f),
        ["fabric"] = M(PaintPattern.Fabric, 0.05f, 2f, Vector3.Right, 0.004f, 0.60f, 0.45f, 0.30f, 0.35f, 0.05f),
        ["carpet"] = M(PaintPattern.Fabric, 0.06f, 1.5f, Vector3.Right, 0.006f, 0.55f, 0.50f, 0.30f, 0.45f, 0.03f),
        ["leather"] = M(PaintPattern.Smooth, 0.03f, 2f, Vector3.Right, 0.05f, 0.45f, 0.25f, 0.25f, 0.15f, 0.40f),
        ["metal"] = M(PaintPattern.Brushed, 0.05f, 8f, Vector3.Right, 0.03f, 0.35f, 0.10f, 0.10f, 0f, 0.90f),
        ["metal_painted"] = M(PaintPattern.Smooth, 0.05f, 3f, Vector3.Right, 0.05f, 0.40f, 0.20f, 0.20f, 0f, 0.50f),
        ["glass"] = M(PaintPattern.Smooth, 0.08f, 2f, Vector3.Up, 0.10f, 0.10f, 0.10f, 0.05f, 0f, 0.90f),
        ["mirror"] = M(PaintPattern.Smooth, 0.08f, 2f, Vector3.Up, 0.10f, 0.05f, 0.05f, 0.00f, 0f, 1.00f),
        ["ceramic"] = M(PaintPattern.Smooth, 0.04f, 2f, Vector3.Up, 0.05f, 0.30f, 0.20f, 0.20f, 0f, 0.60f),
        ["stone"] = M(PaintPattern.Stone, 0.06f, 2f, Vector3.Right, 0.02f, 0.70f, 0.20f, 0.25f, 0f, 0.20f),
        ["concrete"] = M(PaintPattern.Stone, 0.08f, 2f, Vector3.Right, 0.03f, 0.60f, 0.20f, 0.20f, 0f, 0.15f),
        ["brick"] = M(PaintPattern.Stone, 0.05f, 3f, Vector3.Right, 0.02f, 0.70f, 0.20f, 0.25f, 0f, 0.15f),
        ["paper"] = M(PaintPattern.Paper, 0.03f, 3f, Vector3.Right, 0.0012f, 0.35f, 0.35f, 0.25f, 0.05f, 0.10f),
        ["cardboard"] = M(PaintPattern.Cardboard, 0.04f, 3f, Vector3.Right, 0.004f, 0.50f, 0.30f, 0.30f, 0f, 0.10f),
        ["plastic"] = M(PaintPattern.Smooth, 0.05f, 2f, Vector3.Right, 0.05f, 0.30f, 0.25f, 0.20f, 0.05f, 0.60f),
        ["rubber"] = M(PaintPattern.Speckle, 0.03f, 2f, Vector3.Right, 0.0025f, 0.40f, 0.30f, 0.20f, 0f, 0.15f),
    };

    public static readonly LookTuning Default = new(
        DefaultRoles, DefaultRoleMarks,
        new PaintTuning(0.9f, 0.6f, 0.35f, 0.42f, 0.7f, 0.03f, 0.45f, 0.82f, 0.94f, 0.03f, 1.32f, 0.035f, 0.35f, 0.2f,
            0.15f, 0.45f, 0.08f, 0.025f, 0.55f, 0.12f, 2.5f),
        new ShadowTuning(2, 0.5f, 3.5f, 1f, 1f, 6f, 1.6f, 0.1f, 0.03f, 1.0f, 0.03f, 1.0f, 0.05f, 0.25f),
        new SsaoTuning(1.4f, 0.6f, 0.06f, 0.98f, 0.15f, 0.5f),
        new GlowTuning(1.0f, "softlight", 0.9f, new[] { 0f, 0f, 1f, 1f, 1f, 0f, 0f }),
        new GiTuning(128, 0.3f, 1.5f, 0f, 0.2f, 1f),
        new LampTuning(1.2f, 1f, 60f, 0.25f, 0f, 0.1f, 1f, 1f, new Color(1f, 1f, 1f), 0f, 0f, 0f),
        new SunTuning(RoomSite.Fallback.LatitudeDeg, RoomSite.Fallback.NegZBearingDeg, RoomSite.Fallback.SolarNoonH, false, -0.833f, -6f, 1f, 35f, 270f, 0.12f),
        new SeasonTuning(new[] { 15, 105, 196, 288 }, 0.25f, 0.25f, 196, SeasonNames.ToDictionary(n => n, _ => SeasonLook.Neutral)),
        new GradeTuning(0.8f, 0.35f, 0.24f, new Vector3(0.08f, 0.015f, -0.10f), 0.3f, new Vector3(-0.14f, -0.06f, 0.10f), 0.25f, 33, 0f, 0f, 1f, 1f),
        new DofTuning(1.5f, 0.4f, 0.25f, 0.35f, 1.3f, 0.9f, 0.5f, 0.02f, 0.10f, 3f, 0.5f, 1.5f, 0f, 1f, 0f),
        new PostTuning(0.45f, 1.05f, 1.2f, 0.8f, 3f),
        new SkyTuning(new Color("4f8fd6"), new Color("bfd9ee"), new Color("7a86c8"), new Color("ffb98a"), new Color("0f1730"), new Color("2b3a66"),
            new Color("fff4e6"), new Color("b7c4dc"), 1.6f, 3f, 0.5f, 3f, 0.6f, 4f, 24f, new Color("7d9a63"), 0.35f));

    private static readonly Dictionary<string, PaintPattern> PatternNames = Enum.GetValues<PaintPattern>().ToDictionary(p => p.ToString().ToLowerInvariant());

    public static LookTuning Parse(JsonElement extensions)
    {
        var d = Default;
        var defaulted = new List<string>();
        JsonElement Block(string name) => extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty(name, out var b) ? b : default;

        var roles = new Dictionary<string, RoleLook>(d.RoleMarks);
        var defaultMarks = d.DefaultMarks;
        var marks = Block("x_look_role_marks");
        if (marks.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw new InvalidOperationException("x_look_role_marks must be an object");
        if (marks.ValueKind == JsonValueKind.Object)
        {
            // A preset that paints roles itself owns all of them: unlisted roles take its default, never the built-in table.
            if (!marks.TryGetProperty("default", out var dm)) throw new InvalidOperationException("x_look_role_marks needs a \"default\" entry for the roles it does not list");
            defaultMarks = Role(dm, d.DefaultMarks, "x_look_role_marks.default", defaulted);
            roles.Clear();
            foreach (var role in marks.EnumerateObject().Where(r => r.Name != "default"))
                roles[role.Name] = Role(role.Value, d.RoleMarks.TryGetValue(role.Name, out var known) ? known : defaultMarks, "x_look_role_marks." + role.Name, defaulted);
        }
        else defaulted.Add("x_look_role_marks");

        var p = new Fields(Block("x_look_paint"), "x_look_paint", defaulted);
        var paint = new PaintTuning(
            p.F("tone_wash", d.Paint.ToneWash), p.F("tone_strokes", d.Paint.ToneStrokes), p.F("tone_bristle", d.Paint.ToneBristle),
            p.F("tone_gain", d.Paint.ToneGain), p.F("softening_calm", d.Paint.SofteningCalm), p.F("temperature_variation", d.Paint.TemperatureVariation),
            p.F("pastel_mix", d.Paint.PastelMix), p.F("pastel_chroma", d.Paint.PastelChroma), p.F("pastel_scale", d.Paint.PastelScale),
            p.F("pastel_lift", d.Paint.PastelLift), p.F("wear_brightness", d.Paint.WearBrightness), p.F("wear_lift", d.Paint.WearLift),
            p.F("cavity_darkening", d.Paint.CavityDarkening), p.F("stroke_normal_gain", d.Paint.StrokeNormalGain),
            p.F("mark_fade_start", d.Paint.MarkFadeStart), p.F("mark_fade_end", d.Paint.MarkFadeEnd),
            p.F("bevel_fraction", d.Paint.BevelFraction), p.F("bevel_max_m", d.Paint.BevelMaxM),
            p.F("accent_mix", d.Paint.AccentMix), p.F("accent_darken", d.Paint.AccentDarken), p.F("photo_blur_lod", d.Paint.PhotoBlurLod));
        p.Done();
        if (!(paint.MarkFadeStart > 0f && paint.MarkFadeEnd > paint.MarkFadeStart && paint.MarkFadeEnd <= 0.5f))
            throw new InvalidOperationException("x_look_paint: mark_fade_start and mark_fade_end must satisfy 0 < start < end <= 0.5, so marks are gone before they alias");

        var s = new Fields(Block("x_look_shadows"), "x_look_shadows", defaulted);
        var shadows = new ShadowTuning(
            s.I("key_splits", d.Shadows.KeySplits), s.F("key_angular_base_deg", d.Shadows.KeyAngularBaseDeg), s.F("key_angular_per_softness_deg", d.Shadows.KeyAngularPerSoftnessDeg),
            s.F("blur_base", d.Shadows.BlurBase), s.F("blur_per_softness", d.Shadows.BlurPerSoftness), s.F("key_min_distance_m", d.Shadows.KeyMinDistanceM),
            s.F("key_distance_per_diagonal", d.Shadows.KeyDistancePerDiagonal), s.F("key_split_1", d.Shadows.KeySplit1),
            s.F("key_bias", d.Shadows.KeyBias), s.F("key_normal_bias", d.Shadows.KeyNormalBias),
            s.F("lamp_bias", d.Shadows.LampBias), s.F("lamp_normal_bias", d.Shadows.LampNormalBias),
            s.F("lamp_size_base_m", d.Shadows.LampSizeBaseM), s.F("lamp_size_per_softness_m", d.Shadows.LampSizePerSoftnessM));
        s.Done();
        if (shadows.KeySplits is not (1 or 2 or 4)) throw new InvalidOperationException("x_look_shadows.key_splits must be 1, 2 or 4");
        if (shadows.KeySplit1 is <= 0f or >= 1f || shadows.KeyBias < 0f || shadows.KeyNormalBias < 0f || shadows.LampBias < 0f || shadows.LampNormalBias < 0f)
            throw new InvalidOperationException("x_look_shadows: key_split_1 must be between 0 and 1, and no bias may be negative");

        var a = new Fields(Block("x_look_ssao"), "x_look_ssao", defaulted);
        var ssao = new SsaoTuning(a.F("power", d.Ssao.Power), a.F("detail", d.Ssao.Detail), a.F("horizon", d.Ssao.Horizon),
            a.F("sharpness", d.Ssao.Sharpness), a.F("light_affect", d.Ssao.LightAffect), a.F("ao_channel_affect", d.Ssao.AoChannelAffect));
        a.Done();

        var g = new Fields(Block("x_look_glow"), "x_look_glow", defaulted);
        var glow = new GlowTuning(g.F("strength", d.Glow.Strength), g.S("blend_mode", d.Glow.BlendMode), g.F("hdr_threshold", d.Glow.HdrThreshold),
            g.Floats("levels", d.Glow.Levels));
        g.Done();
        if (glow.Levels.Count != 7) throw new InvalidOperationException("x_look_glow.levels must list 7 glow levels");
        if (glow.BlendMode is not ("additive" or "screen" or "softlight" or "replace" or "mix"))
            throw new InvalidOperationException($"x_look_glow.blend_mode '{glow.BlendMode}' is not additive, screen, softlight, replace or mix");

        var v = new Fields(Block("x_look_gi"), "x_look_gi", defaulted);
        var gi = new GiTuning(v.I("subdiv", d.Gi.Subdiv), v.F("margin_m", d.Gi.MarginM), v.F("bias", d.Gi.Bias), v.F("normal_bias", d.Gi.NormalBias),
            v.F("two_bounces_above", d.Gi.TwoBouncesAbove), v.F("environment_ambient_scale", d.Gi.EnvironmentAmbientScale));
        v.Done();
        if (gi.Subdiv is not (64 or 128 or 256 or 512)) throw new InvalidOperationException("x_look_gi.subdiv must be 64, 128, 256 or 512");
        if (gi.EnvironmentAmbientScale is < 0f or > 1f) throw new InvalidOperationException("x_look_gi.environment_ambient_scale must be between 0 and 1");

        var l = new Fields(Block("x_look_lamps"), "x_look_lamps", defaulted);
        var lamps = new LampTuning(l.F("attenuation", d.Lamps.Attenuation), l.F("range_per_diagonal", d.Lamps.RangePerDiagonal),
            l.F("window_spot_angle_deg", d.Lamps.WindowSpotAngleDeg), l.F("switch_on_below_daylight", d.Lamps.SwitchOnBelowDaylight),
            l.F("window_standoff_m", d.Lamps.WindowStandoffM), l.F("window_light_size_m", d.Lamps.WindowLightSizeM), l.F("sky_fill_energy", d.Lamps.SkyFillEnergy),
            l.F("sky_fill_saturation", d.Lamps.SkyFillSaturation), l.C("lamp_tint", d.Lamps.LampTint), l.F("lamp_tint_amount", d.Lamps.LampTintAmount),
            l.F("glow_radius_m", d.Lamps.GlowRadiusM), l.F("glow_energy", d.Lamps.GlowEnergy));
        l.Done();
        if (!(lamps.RangePerDiagonal > 0f && lamps.WindowSpotAngleDeg is > 0f and < 90f && lamps.SwitchOnBelowDaylight is >= 0f and <= 1f
              && lamps.WindowStandoffM >= 0f && lamps.WindowLightSizeM >= 0f && lamps.SkyFillEnergy >= 0f && lamps.SkyFillSaturation is >= 0f and <= 1f && lamps.LampTintAmount is >= 0f and <= 1f
              && lamps.GlowRadiusM >= 0f && lamps.GlowEnergy >= 0f))
            throw new InvalidOperationException("x_look_lamps: range_per_diagonal must be positive, window_spot_angle_deg between 0 and 90, switch_on_below_daylight between 0 and 1, and the window numbers not negative");

        // The site (latitude, which way -Z faces, solar noon) is a fact about a room, not a look number: it comes from the
        // room's manifest (RoomSite), and StylePreset.WithSite puts it into the Sun tuning. A preset on its own carries the
        // fallback site, and an x_look_sun block that still names a site field is refused so it cannot linger (review M5).
        var u = new Fields(Block("x_look_sun"), "x_look_sun", defaulted);
        var sun = new SunTuning(RoomSite.Fallback.LatitudeDeg, RoomSite.Fallback.NegZBearingDeg, RoomSite.Fallback.SolarNoonH,
            u.B("real_clock_daylight_saving", d.Sun.RealClockDaylightSaving), u.F("sunrise_elevation_deg", d.Sun.SunriseElevationDeg),
            u.F("twilight_elevation_deg", d.Sun.TwilightElevationDeg), u.F("horizon_elevation_deg", d.Sun.HorizonElevationDeg),
            u.F("moon_elevation_deg", d.Sun.MoonElevationDeg), u.F("moon_bearing_deg", d.Sun.MoonBearingDeg), u.F("reference_daylight", d.Sun.ReferenceDaylight));
        u.Done();
        if (sun.MoonBearingDeg is < 0f or >= 360f)
            throw new InvalidOperationException("x_look_sun: moon_bearing_deg must be from 0 up to (not including) 360");
        if (!(sun.TwilightElevationDeg < sun.SunriseElevationDeg && sun.SunriseElevationDeg <= 0f && sun.HorizonElevationDeg is >= 0f and <= 10f
              && sun.MoonElevationDeg is > 5f and <= 85f && sun.ReferenceDaylight is > 0f and < 1f))
            throw new InvalidOperationException("x_look_sun: twilight_elevation_deg < sunrise_elevation_deg <= 0, horizon_elevation_deg from 0 to 10, moon_elevation_deg above 5 and at most 85, and reference_daylight between 0 and 1");

        var e = new Fields(Block("x_look_seasons"), "x_look_seasons", defaulted);
        var looks = new Dictionary<string, SeasonLook>();
        var looksBlock = e.Object("looks");
        if (looksBlock.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in looksBlock.EnumerateObject().Select(l => l.Name).Where(n => !SeasonNames.Contains(n)))
                throw new InvalidOperationException($"x_look_seasons.looks.{name} is not winter, spring, summer or autumn");
            foreach (var name in SeasonNames)
            {
                var lf = new Fields(looksBlock.TryGetProperty(name, out var one) ? one : default, "x_look_seasons.looks." + name, defaulted);
                looks[name] = new SeasonLook(lf.F("sun_energy", 1f), lf.F("sun_blur", 1f), lf.F("sky_saturation", 1f), lf.F("sky_brightness", 1f), lf.F("cloud_amount", 0.5f));
                lf.Done();
            }
        }
        else foreach (var name in SeasonNames) looks[name] = SeasonLook.Neutral;
        var seasons = new SeasonTuning(e.Ints("centre_days", d.Seasons.CentreDays), e.F("hold", d.Seasons.Hold), e.F("light_strength", d.Seasons.LightStrength),
            e.I("fixed_day_of_year", d.Seasons.FixedDayOfYear), looks);
        e.Done();
        if (seasons.FixedDayOfYear is < 1 or > 366 || seasons.Hold is < 0f or >= 0.5f)
            throw new InvalidOperationException("x_look_seasons: fixed_day_of_year must be 1 to 366 and hold at least 0 and under 0.5");
        if (seasons.CentreDays.Count != 4 || seasons.CentreDays.Zip(seasons.CentreDays.Skip(1)).Any(t => t.Second <= t.First) || seasons.CentreDays[0] < 1 || seasons.CentreDays[3] > 365)
            throw new InvalidOperationException("x_look_seasons.centre_days must be four increasing days of the year (winter, spring, summer, autumn)");
        if (seasons.Looks.Values.Any(l => l.SunEnergy < 0f || l.SunBlur < 0f || l.SkySaturation < 0f || l.SkyBrightness < 0f || l.CloudAmount is < 0f or > 1f))
            throw new InvalidOperationException("x_look_seasons.looks: sun_energy, sun_blur, sky_saturation and sky_brightness must not be negative, and cloud_amount must be between 0 and 1");

        var r = new Fields(Block("x_look_grade"), "x_look_grade", defaulted);
        var grade = new GradeTuning(r.F("shadow_tone", d.Grade.ShadowTone), r.F("highlight_tone", d.Grade.HighlightTone), r.F("season_tint", d.Grade.SeasonTint),
            r.Vec("warmth_rgb", d.Grade.WarmthRgb), r.F("night_desaturate", d.Grade.NightDesaturate), r.Vec("night_tint_rgb", d.Grade.NightTintRgb),
            r.F("night_deepen", d.Grade.NightDeepen), r.I("lut_size", d.Grade.LutSize), r.F("season_tint_shadow_fade", d.Grade.SeasonTintShadowFade),
            r.F("night_full_below", d.Grade.NightFullBelow), r.F("night_none_above", d.Grade.NightNoneAbove), r.F("night_with_lamps", d.Grade.NightWithLamps));
        if (grade.NightWithLamps is < 0f or > 1f) throw new InvalidOperationException("x_look_grade.night_with_lamps must be between 0 and 1");
        if (!(grade.NightFullBelow >= 0f && grade.NightNoneAbove > grade.NightFullBelow && grade.NightNoneAbove <= 1f))
            throw new InvalidOperationException("x_look_grade: 0 <= night_full_below < night_none_above <= 1");
        if (grade.SeasonTintShadowFade is < 0f or > 1f) throw new InvalidOperationException("x_look_grade.season_tint_shadow_fade must be between 0 and 1");
        r.Done();
        if (grade.LutSize is < 8 or > 65) throw new InvalidOperationException("x_look_grade.lut_size must be between 8 and 65");

        var f = new Fields(Block("x_look_dof"), "x_look_dof", defaulted);
        var dof = new DofTuning(f.F("tilt_pitch_gain", d.Dof.TiltPitchGain), f.F("tilt_band_narrowing", d.Dof.TiltBandNarrowing),
            f.F("far_transition_base_m", d.Dof.FarTransitionBaseM), f.F("far_transition_per_m", d.Dof.FarTransitionPerM), f.F("far_blur_reference", d.Dof.FarBlurReference),
            f.F("near_transition_base", d.Dof.NearTransitionBase), f.F("near_transition_per_blur", d.Dof.NearTransitionPerBlur),
            f.F("amount_base", d.Dof.AmountBase), f.F("amount_per_blur", d.Dof.AmountPerBlur),
            f.F("eye_focus_body_heights", d.Dof.EyeFocusBodyHeights), f.F("body_focus_height_fraction", d.Dof.BodyFocusHeightFraction),
            f.F("eye_crisp_body_heights", d.Dof.EyeCrispBodyHeights), f.F("tilt_transition_shortening", d.Dof.TiltTransitionShortening),
            f.F("tilt_amount_gain", d.Dof.TiltAmountGain), f.F("companion_follow_m", d.Dof.CompanionFollowM),
            f.F("focus_ease_s", d.Dof.FocusEaseS), f.F("observe_band_m", d.Dof.ObserveBandM), f.F("observe_far_transition_m", d.Dof.ObserveFarTransitionM),
            f.F("observe_near_transition_m", d.Dof.ObserveNearTransitionM), f.F("observe_amount", d.Dof.ObserveAmount));
        if (dof.TiltTransitionShortening is < 0f or >= 1f || dof.TiltBandNarrowing is < 0f or >= 1f || dof.CompanionFollowM < 0f)
            throw new InvalidOperationException("x_look_dof: tilt_transition_shortening and tilt_band_narrowing must be at least 0 and under 1, companion_follow_m not negative");
        if (dof.FocusEaseS < 0f || dof.ObserveBandM <= 0f || dof.ObserveFarTransitionM <= 0f || dof.ObserveNearTransitionM <= 0f || dof.ObserveAmount is < 0f or > 1f)
            throw new InvalidOperationException("x_look_dof: focus_ease_s must not be negative, the observe band and transitions must be positive, and observe_amount between 0 and 1");
        f.Done();

        var o = new Fields(Block("x_look_post"), "x_look_post", defaulted);
        var post = new PostTuning(o.F("vignette_start", d.Post.VignetteStart), o.F("vignette_end", d.Post.VignetteEnd),
            o.F("grain_fine", d.Post.GrainFine), o.F("grain_soft", d.Post.GrainSoft), o.F("grain_soft_px", d.Post.GrainSoftPx),
            o.F("focus_saturation", d.Post.FocusSaturation), o.F("defocus_saturation", d.Post.DefocusSaturation), o.F("defocus_darken", d.Post.DefocusDarken),
            o.F("bokeh_threshold", d.Post.BokehThreshold), o.F("bokeh_gain", d.Post.BokehGain));
        o.Done();
        if (post.FocusSaturation < 0f || post.DefocusSaturation < 0f || post.DefocusDarken is < 0f or >= 1f || post.BokehThreshold < 0f || post.BokehGain < 0f)
            throw new InvalidOperationException("x_look_post: saturations, bokeh_threshold and bokeh_gain must not be negative, and defocus_darken must be at least 0 and under 1");

        var k = new Fields(Block("x_look_sky"), "x_look_sky", defaulted);
        var sky = new SkyTuning(k.C("day_zenith", d.Sky.DayZenith), k.C("day_horizon", d.Sky.DayHorizon), k.C("dusk_zenith", d.Sky.DuskZenith), k.C("dusk_horizon", d.Sky.DuskHorizon),
            k.C("night_zenith", d.Sky.NightZenith), k.C("night_horizon", d.Sky.NightHorizon), k.C("cloud_light", d.Sky.CloudLight), k.C("cloud_shade", d.Sky.CloudShade),
            k.F("brightness", d.Sky.Brightness), k.F("sun_disc_deg", d.Sky.SunDiscDeg), k.F("sun_halo", d.Sky.SunHalo), k.F("moon_disc_deg", d.Sky.MoonDiscDeg),
            k.F("star_strength", d.Sky.StarStrength), k.F("dusk_start_deg", d.Sky.DuskStartDeg), k.F("dusk_end_deg", d.Sky.DuskEndDeg),
            k.C("ground", d.Sky.Ground), k.F("ground_season_tint", d.Sky.GroundSeasonTint));
        k.Done();
        if (sky.Brightness < 0f || sky.SunDiscDeg < 0f || sky.MoonDiscDeg < 0f || sky.StarStrength < 0f || sky.SunHalo < 0f || !(sky.DuskStartDeg >= 0f && sky.DuskEndDeg > sky.DuskStartDeg) || sky.GroundSeasonTint is < 0f or > 1f)
            throw new InvalidOperationException("x_look_sky: brightness, disc sizes, halo and star strength must not be negative, and dusk_start_deg < dusk_end_deg with dusk_start_deg at least 0, and ground_season_tint between 0 and 1");

        var views = new Dictionary<LookView, ViewBlur>();
        var viewsBlock = Block("x_look_views");
        if (viewsBlock.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw new InvalidOperationException("x_look_views must be an object");
        if (viewsBlock.ValueKind == JsonValueKind.Object)
        {
            var names = Enum.GetValues<LookView>().ToDictionary(ViewName);
            foreach (var name in viewsBlock.EnumerateObject().Select(v => v.Name).Where(n => !names.ContainsKey(n)))
                throw new InvalidOperationException($"x_look_views.{name} is not eye, shoulder, diorama or isometric");
            foreach (var (name, view) in names)
            {
                // Every view is stated: a view the block leaves out would silently keep the miniature blur.
                if (!viewsBlock.TryGetProperty(name, out var entry)) throw new InvalidOperationException($"x_look_views needs an entry for {name}");
                var w = new Fields(entry, "x_look_views." + name, defaulted);
                var style = w.S("blur", "miniature");
                if (style is not ("miniature" or "far")) throw new InvalidOperationException($"x_look_views.{name}.blur '{style}' is not miniature or far");
                // A far view states its three numbers; a miniature view takes the preset's depth of field and states none.
                views[view] = style == "far"
                    ? new ViewBlur(style, w.F("crisp_beyond_focus_m", 0f), w.F("far_transition_m", 0f), w.F("far_amount", 0f))
                    : ViewBlur.Miniature;
                w.Done();
                var blur = views[view];
                if (blur.Far && !(blur.CrispBeyondFocusM > 0f && blur.FarTransitionM > 0f && blur.FarAmount is > 0f and <= 1f))
                    throw new InvalidOperationException($"x_look_views.{name}: a far view needs crisp_beyond_focus_m and far_transition_m above 0 and far_amount above 0 and at most 1");
            }
        }
        else
        {
            defaulted.Add("x_look_views");
            foreach (var view in Enum.GetValues<LookView>()) views[view] = ViewBlur.Miniature;
        }

        return new LookTuning(roles, defaultMarks, paint, shadows, ssao, glow, gi, lamps, sun, seasons, grade, dof, post, sky) { Defaulted = defaulted, Views = views };
    }

    private static RoleLook Role(JsonElement element, RoleLook fallback, string label, List<string> defaulted)
    {
        var f = new Fields(element, label, defaulted);
        var patternName = f.S("pattern", fallback.Pattern.ToString().ToLowerInvariant());
        if (!PatternNames.TryGetValue(patternName, out var pattern)) throw new InvalidOperationException($"{label}.pattern '{patternName}' is not one of {string.Join(", ", PatternNames.Keys)}");
        var axisName = f.S("stroke_axis", fallback.StrokeAxis == Vector3.Up ? "y" : fallback.StrokeAxis == Vector3.Back ? "z" : "x");
        var axis = axisName switch { "x" => Vector3.Right, "y" => Vector3.Up, "z" => Vector3.Back, _ => throw new InvalidOperationException($"{label}.stroke_axis must be x, y or z") };
        var look = new RoleLook(pattern, f.F("stroke_scale_m", fallback.StrokeScaleM), f.F("stroke_stretch", fallback.StrokeStretch), axis,
            f.F("pattern_scale_m", fallback.PatternScaleM), f.F("variation", fallback.Variation), f.F("wrap", fallback.Wrap),
            f.F("terminator_warmth", fallback.TerminatorWarmth), f.F("sheen", fallback.Sheen), f.F("specular", fallback.Specular),
            f.F("texture_strength", fallback.TextureStrength), f.F("texture_scale_m", fallback.TextureScaleM), f.F("calm", fallback.Calm));
        f.Done();
        if (look.StrokeScaleM <= 0f || look.PatternScaleM <= 0f || look.StrokeStretch < 1f || look.TextureScaleM <= 0f)
            throw new InvalidOperationException($"{label}: scales must be positive and stroke_stretch at least 1");
        if (look.Calm is < 0f or > 1f) throw new InvalidOperationException($"{label}: calm must be between 0 and 1");
        return look;
    }

    /// <summary>Reads named fields of one block with defaults, records each default it used, and refuses any field it did not read.</summary>
    private sealed class Fields
    {
        private readonly JsonElement _element;
        private readonly string _label;
        private readonly HashSet<string> _read = new();
        private readonly List<string> _defaulted;

        public Fields(JsonElement element, string label, List<string> defaulted)
        {
            if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                throw new InvalidOperationException($"{label} must be an object");
            _element = element;
            _label = label;
            _defaulted = defaulted;
        }

        private bool Get(string name, out JsonElement value)
        {
            _read.Add(name);
            value = default;
            if (_element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(name, out value)) return true;
            _defaulted.Add(_label + "." + name);
            return false;
        }

        public float F(string name, float fallback)
        {
            if (!Get(name, out var value)) return fallback;
            var number = value.GetSingle();
            if (!float.IsFinite(number)) throw new InvalidOperationException($"{_label}.{name} must be finite");
            return number;
        }

        public int I(string name, int fallback) => Get(name, out var value) ? value.GetInt32() : fallback;

        public bool B(string name, bool fallback) => Get(name, out var value) ? value.GetBoolean() : fallback;

        /// <summary>A nested object (read by the caller with its own Fields), or default when absent.</summary>
        public JsonElement Object(string name)
        {
            if (!Get(name, out var value)) return default;
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{_label}.{name} must be an object");
            return value;
        }

        public Color C(string name, Color fallback) => Get(name, out var value) ? new Color(value.GetString()!) : fallback;

        public string S(string name, string fallback) => Get(name, out var value) ? value.GetString()! : fallback;

        public Vector3 Vec(string name, Vector3 fallback)
        {
            if (!Get(name, out var value)) return fallback;
            if (value.GetArrayLength() != 3) throw new InvalidOperationException($"{_label}.{name} must have three numbers");
            return new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
        }

        public IReadOnlyList<float> Floats(string name, IReadOnlyList<float> fallback) =>
            Get(name, out var value) ? value.EnumerateArray().Select(x => x.GetSingle()).ToArray() : fallback;

        public IReadOnlyList<int> Ints(string name, IReadOnlyList<int> fallback) =>
            Get(name, out var value) ? value.EnumerateArray().Select(x => x.GetInt32()).ToArray() : fallback;

        public void Done()
        {
            if (_element.ValueKind != JsonValueKind.Object) return;
            var unknown = _element.EnumerateObject().Select(p => p.Name).Where(n => !_read.Contains(n)).ToArray();
            if (unknown.Length > 0) throw new InvalidOperationException($"{_label} has unknown field(s) {string.Join(", ", unknown)}");
        }
    }
}
