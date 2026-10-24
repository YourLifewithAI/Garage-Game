using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Native.Look;

internal static class JsonFields
{
    /// <summary>A required property; a missing one fails with its name, which JsonElement.GetProperty does not give.</summary>
    public static JsonElement Req(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new KeyNotFoundException($"missing field '{name}'");
}

/// <summary>One material role's treatment from the preset (materials.default or materials.roles.&lt;role&gt;).</summary>
public sealed record MaterialTreatment(
    Color? Tint, float AlbedoSoftening, float StrokeNormalStrength, float EdgeWear, float Roughness, float Metallic, string? Texture);

/// <summary>One time-of-day key: the key light and ambient at an hour.</summary>
public sealed record TimeKey(float Hour, Color KeyColor, float KeyEnergy, Color AmbientColor, float AmbientEnergy);

/// <summary>A season's colour grade.</summary>
public sealed record SeasonGrade(float Saturation, float Warmth, Color Tint);

/// <summary>
/// An enfractal.style preset as the runtime reads it. contracts/validate.py is the full validator; this
/// reader fails closed on every field the look uses. Experimental x_look_* extension keys are read with
/// defaults, so a preset without them still loads; Tuning.Defaulted lists every look number such a preset
/// left to the code, and the shipped preset leaves none.
/// </summary>
public sealed class StylePreset
{
    public string Path { get; private init; } = "";
    public string Sha256 { get; private init; } = "";
    public string PresetId { get; private init; } = "";
    public int PresetVersion { get; private init; }
    public string DisplayName { get; private init; } = "";
    public string Status { get; private init; } = "";

    // renderer
    public string RendererMethod { get; private init; } = "";
    public string GiMode { get; private init; } = "none";
    public float GiEnergy { get; private init; } = 1f;
    public float GiBounceFeedback { get; private init; } = 0.5f;
    public bool ShadowsEnabled { get; private init; }
    public float ShadowSoftness { get; private init; } = 0.5f;
    public Color ShadowTint { get; private init; } = new("4a4060");
    public bool AoEnabled { get; private init; }
    public float AoIntensity { get; private init; } = 1f;
    public float AoRadiusM { get; private init; } = 0.25f;
    public bool GlowEnabled { get; private init; }
    public float GlowIntensity { get; private init; } = 0.6f;
    public float GlowBloom { get; private init; }
    public string TonemapMode { get; private init; } = "";
    public float Exposure { get; private init; }
    public float White { get; private init; } = 1f;

    // palette
    public float Saturation { get; private init; } = 1f;
    public float Contrast { get; private init; } = 1f;
    public float Warmth { get; private init; }
    public Color PaletteShadowTint { get; private init; } = new("808080");
    public Color HighlightTint { get; private init; } = new("ffffff");
    public IReadOnlyList<Color> Accents { get; private init; } = Array.Empty<Color>();

    // lighting
    public Color Background { get; private init; } = new("2a2f33");
    public Color KeyColor { get; private init; }
    public float KeyEnergy { get; private init; }
    public float KeyElevationDeg { get; private init; }
    public float KeyAzimuthDeg { get; private init; }
    public bool KeyCastsShadows { get; private init; }
    public Color AmbientColor { get; private init; }
    public float AmbientEnergy { get; private init; }
    public float HonorRoomLights { get; private init; }
    public float RoomLightEnergyScale { get; private init; } = 1f;

    // time of day and seasons
    public bool TimeOfDayEnabled { get; private init; }
    public float DefaultHour { get; private init; } = 12f;
    public bool FollowClock { get; private init; }
    public IReadOnlyList<TimeKey> TimeKeys { get; private init; } = Array.Empty<TimeKey>();
    public bool SeasonsEnabled { get; private init; }
    public bool FollowCalendar { get; private init; }
    public string Hemisphere { get; private set; } = "north";
    public IReadOnlyDictionary<string, SeasonGrade> SeasonGrades { get; private init; } = new Dictionary<string, SeasonGrade>();

    // camera
    public float FovDeg { get; private init; } = 70f;
    public bool DofEnabled { get; private init; }
    public string DofFocus { get; private init; } = "player";
    public float FocusBandM { get; private init; } = 0.6f;
    public float FarBlur { get; private init; }
    public float NearBlur { get; private init; }
    public float NearBlurDistanceM { get; private init; }
    public bool TiltShiftEnabled { get; private init; }
    public float TiltShiftStrength { get; private init; }

    // post
    public bool KuwaharaEnabled { get; private init; }
    public bool OutlineEnabled { get; private init; }
    public float Grain { get; private init; }
    public float Vignette { get; private init; }

    // materials and geometry
    public MaterialTreatment DefaultTreatment { get; private init; } = null!;
    public IReadOnlyDictionary<string, MaterialTreatment> RoleTreatments { get; private init; } = new Dictionary<string, MaterialTreatment>();
    public float DefaultRoughness => DefaultTreatment.Roughness;
    public IReadOnlyDictionary<string, float> RoleRoughness { get; private init; } = new Dictionary<string, float>();
    public float BevelM { get; private init; }
    public float WobbleM { get; private init; }

    // budgets
    public float TargetFrameMs { get; private init; } = 16.7f;
    public int MaxShadowedLights { get; private init; }
    public string ReferenceGpu { get; private init; } = "";

    // experimental extensions (x_look_*), with defaults
    /// <summary>
    /// "sun": the key is the real sun on the solar model's path (and the moon at night), and the room's shell casts its
    /// shadows, so direct light comes in only through openings; a room without a sun light hint gets none.
    /// "fixed": an ordinary directional light at the preset's elevation and azimuth.
    /// </summary>
    public string KeyMode { get; private init; } = "fixed";
    public bool SsilEnabled { get; private init; }
    public float SsilIntensity { get; private init; } = 1f;
    public float SsilRadiusM { get; private init; } = 0.3f;
    public float GlazeAmount { get; private init; } = 0.35f;
    /// <summary>Every other look-defining number, from the x_look_* blocks (see LookTuning).</summary>
    public LookTuning Tuning { get; private set; } = LookTuning.Default;
    /// <summary>The site the sun is worked out for: the room's own through WithSite, otherwise the look's fallback.</summary>
    public RoomSite Site { get; private set; } = RoomSite.Fallback;

    /// <summary>The x_look_* extension keys this runtime understands. Any other x_look_* key is refused, so a misspelled block cannot be silently ignored.</summary>
    public static readonly IReadOnlySet<string> KnownLookExtensions = new HashSet<string>
    {
        "x_look_key_mode", "x_look_glaze_amount", "x_look_ssil", "x_look_role_marks", "x_look_paint", "x_look_shadows", "x_look_ssao",
        "x_look_glow", "x_look_gi", "x_look_lamps", "x_look_sun", "x_look_seasons", "x_look_grade", "x_look_dof", "x_look_post", "x_look_sky", "x_look_views",
    };

    public const string StylesRoot = "res://styles";

    public static string PathFor(string presetId, int version) => $"{StylesRoot}/{presetId}/v{version}.json";

    /// <summary>
    /// This preset for a room that stands at a site: the same file (same hash, same numbers) with the sun's latitude,
    /// compass facing and solar noon taken from the room, and the seasons of its hemisphere. The shared preset never
    /// carries a site itself; the original object is left unchanged.
    /// </summary>
    public StylePreset WithSite(RoomSite site)
    {
        var clone = (StylePreset)MemberwiseClone();
        clone.Site = site;
        clone.Hemisphere = site.Declared ? site.Hemisphere : Hemisphere;
        clone.Tuning = Tuning with { Sun = Tuning.Sun with { LatitudeDeg = site.LatitudeDeg, NegZBearingDeg = site.NegZBearingDeg, SolarNoonH = site.SolarNoonH } };
        return clone;
    }

    public MaterialTreatment TreatmentFor(string role) => RoleTreatments.TryGetValue(role, out var treatment) ? treatment : DefaultTreatment;

    /// <summary>Resolve a pinned preset: game/styles/&lt;id&gt;/v&lt;version&gt;.json, which must declare that id and version and, when a hash is pinned, match it.</summary>
    public static StylePreset Resolve(string presetId, int version, string? expectedSha256 = null)
    {
        var preset = Load(PathFor(presetId, version));
        if (preset.PresetId != presetId || preset.PresetVersion != version)
            throw new InvalidOperationException($"{preset.Path} declares {preset.PresetId} v{preset.PresetVersion}, not {presetId} v{version}");
        if (expectedSha256 != null && preset.Sha256 != expectedSha256)
            throw new InvalidOperationException($"{preset.Path} does not match its pin; a pinned preset version must never change");
        return preset;
    }

    public static StylePreset Load(string path)
    {
        if (!FileAccess.FileExists(path)) throw new InvalidOperationException($"style preset not found: {path}");
        var bytes = FileAccess.GetFileAsBytes(path);
        if (Array.IndexOf(bytes, (byte)'\r') >= 0) throw new InvalidOperationException($"{path} must use LF line endings; presets are pinned by hash");
        try
        {
            return Parse(bytes, path);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {
            throw new InvalidOperationException($"{path} is not a usable enfractal.style preset: {error.Message}", error);
        }
    }

    public static StylePreset Parse(byte[] bytes, string path)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.Req("schema").GetString() != "enfractal.style" || root.Req("version").GetInt32() != 1)
            throw new InvalidOperationException($"{path} is not an enfractal.style version 1 preset");
        var renderer = root.Req("renderer");
        var gi = renderer.Req("gi");
        var shadows = renderer.Req("shadows");
        var ao = renderer.Req("ambient_occlusion");
        var glow = renderer.Req("glow");
        var tonemap = renderer.Req("tonemap");
        var palette = root.Req("palette");
        var lighting = root.Req("lighting");
        var key = lighting.Req("key");
        var ambient = lighting.Req("ambient");
        var timeOfDay = root.Req("time_of_day");
        var seasons = root.Req("seasons");
        var camera = root.Req("camera");
        var dof = camera.Req("depth_of_field");
        var tilt = camera.Req("tilt_shift");
        var post = root.Req("post");
        var materials = root.Req("materials");
        var geometry = root.Req("geometry");
        var budgets = root.Req("budgets");
        var extensions = root.TryGetProperty("extensions", out var ext) ? ext : default;

        var roles = new Dictionary<string, MaterialTreatment>();
        foreach (var role in materials.Req("roles").EnumerateObject()) roles[role.Name] = Treatment(role.Value);
        var grades = new Dictionary<string, SeasonGrade>();
        foreach (var season in seasons.Req("grades").EnumerateObject())
            grades[season.Name] = new SeasonGrade(F(season.Value, "saturation"), F(season.Value, "warmth"), C(season.Value, "tint"));
        foreach (var required in new[] { "spring", "summer", "autumn", "winter" })
            if (!grades.ContainsKey(required)) throw new KeyNotFoundException($"seasons.grades.{required}");
        var keys = timeOfDay.Req("keys").EnumerateArray()
            .Select(k => new TimeKey(F(k, "hour"), C(k, "key_color"), F(k, "key_energy"), C(k, "ambient_color"), F(k, "ambient_energy")))
            .ToArray();
        for (var i = 1; i < keys.Length; i++)
            if (keys[i].Hour <= keys[i - 1].Hour) throw new InvalidOperationException("time_of_day keys must be in strictly increasing hour order");
        var ssil = Extension(extensions, "x_look_ssil");
        if (extensions.ValueKind == JsonValueKind.Object)
            foreach (var name in extensions.EnumerateObject().Select(e => e.Name))
                if (name.StartsWith("x_look_", StringComparison.Ordinal) && !KnownLookExtensions.Contains(name))
                    throw new InvalidOperationException($"unknown look extension '{name}'");
        var keyMode = extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("x_look_key_mode", out var mode) ? mode.GetString()! : "fixed";
        if (keyMode is not ("fixed" or "sun"))
            throw new InvalidOperationException($"x_look_key_mode '{keyMode}' is not fixed or sun" + (keyMode == "diorama" ? " (the diorama key was removed: light comes only from real sources)" : ""));
        var tuning = LookTuning.Parse(extensions);
        var defaulted = tuning.Defaulted.ToList();
        foreach (var name in new[] { "x_look_key_mode", "x_look_glaze_amount" })
            if (extensions.ValueKind != JsonValueKind.Object || !extensions.TryGetProperty(name, out _)) defaulted.Add(name);

        return new StylePreset
        {
            Path = path,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            PresetId = root.Req("preset_id").GetString()!,
            PresetVersion = root.Req("preset_version").GetInt32(),
            DisplayName = root.Req("display_name").GetString()!,
            Status = root.Req("status").GetString()!,
            RendererMethod = renderer.Req("method").GetString()!,
            GiMode = gi.Req("mode").GetString()!,
            GiEnergy = F(gi, "energy"),
            GiBounceFeedback = Opt(gi, "bounce_feedback", 0.5f),
            ShadowsEnabled = shadows.Req("enabled").GetBoolean(),
            ShadowSoftness = Opt(shadows, "softness", 0.5f),
            ShadowTint = shadows.TryGetProperty("tint", out var shadowTint) ? new Color(shadowTint.GetString()!) : new Color("4a4060"),
            AoEnabled = ao.Req("enabled").GetBoolean(),
            AoIntensity = Opt(ao, "intensity", 1f),
            AoRadiusM = Opt(ao, "radius_m", 0.25f),
            GlowEnabled = glow.Req("enabled").GetBoolean(),
            GlowIntensity = Opt(glow, "intensity", 0.6f),
            GlowBloom = Opt(glow, "bloom", 0f),
            TonemapMode = tonemap.Req("mode").GetString()!,
            Exposure = F(tonemap, "exposure"),
            White = Opt(tonemap, "white", 1f),
            Saturation = F(palette, "saturation"),
            Contrast = F(palette, "contrast"),
            Warmth = F(palette, "warmth"),
            PaletteShadowTint = C(palette, "shadow_tint"),
            HighlightTint = C(palette, "highlight_tint"),
            Accents = palette.TryGetProperty("accents", out var accents) ? accents.EnumerateArray().Select(a => new Color(a.GetString()!)).ToArray() : Array.Empty<Color>(),
            Background = lighting.TryGetProperty("background", out var background) ? new Color(background.GetString()!) : new Color("2a2f33"),
            KeyColor = C(key, "color"),
            KeyEnergy = F(key, "energy"),
            KeyElevationDeg = F(key, "elevation_deg"),
            KeyAzimuthDeg = F(key, "azimuth_deg"),
            KeyCastsShadows = key.Req("casts_shadows").GetBoolean(),
            AmbientColor = C(ambient, "color"),
            AmbientEnergy = F(ambient, "energy"),
            HonorRoomLights = F(lighting, "honor_room_lights"),
            RoomLightEnergyScale = Opt(lighting, "room_light_energy_scale", 1f),
            TimeOfDayEnabled = timeOfDay.Req("enabled").GetBoolean(),
            DefaultHour = F(timeOfDay, "default_hour"),
            FollowClock = timeOfDay.Req("follow_clock").GetBoolean(),
            TimeKeys = keys,
            SeasonsEnabled = seasons.Req("enabled").GetBoolean(),
            FollowCalendar = seasons.Req("follow_calendar").GetBoolean(),
            Hemisphere = seasons.Req("hemisphere").GetString()!,
            SeasonGrades = grades,
            FovDeg = F(camera, "fov_deg"),
            DofEnabled = dof.Req("enabled").GetBoolean(),
            DofFocus = dof.Req("focus").GetString()!,
            FocusBandM = F(dof, "focus_band_m"),
            FarBlur = F(dof, "far_blur"),
            NearBlur = F(dof, "near_blur"),
            NearBlurDistanceM = F(dof, "near_blur_distance_m"),
            TiltShiftEnabled = tilt.Req("enabled").GetBoolean(),
            TiltShiftStrength = F(tilt, "strength"),
            KuwaharaEnabled = post.Req("kuwahara").Req("enabled").GetBoolean(),
            OutlineEnabled = post.Req("outline").Req("enabled").GetBoolean(),
            Grain = F(post, "grain"),
            Vignette = F(post, "vignette"),
            DefaultTreatment = Treatment(materials.Req("default")),
            RoleTreatments = roles,
            RoleRoughness = roles.ToDictionary(r => r.Key, r => r.Value.Roughness),
            BevelM = F(geometry, "bevel_m"),
            WobbleM = F(geometry, "wobble_m"),
            TargetFrameMs = F(budgets, "target_frame_ms"),
            MaxShadowedLights = budgets.Req("max_shadowed_lights").GetInt32(),
            ReferenceGpu = budgets.TryGetProperty("reference_gpu", out var gpu) ? gpu.GetString()! : "",
            KeyMode = keyMode,
            SsilEnabled = ssil.ValueKind == JsonValueKind.Object && ssil.Req("enabled").GetBoolean(),
            SsilIntensity = ssil.ValueKind == JsonValueKind.Object ? Opt(ssil, "intensity", 1f) : 1f,
            SsilRadiusM = ssil.ValueKind == JsonValueKind.Object ? Opt(ssil, "radius_m", 0.3f) : 0.3f,
            GlazeAmount = extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("x_look_glaze_amount", out var glaze) ? glaze.GetSingle() : 0.35f,
            Tuning = tuning with { Defaulted = defaulted },
        };
    }

    private static JsonElement Extension(JsonElement extensions, string name) =>
        extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty(name, out var value) ? value : default;

    private static MaterialTreatment Treatment(JsonElement element) => new(
        element.TryGetProperty("tint", out var tint) ? new Color(tint.GetString()!) : null,
        F(element, "albedo_softening"), F(element, "stroke_normal_strength"), F(element, "edge_wear"), F(element, "roughness"),
        Opt(element, "metallic", 0f), element.TryGetProperty("texture", out var texture) ? texture.GetString() : null);

    private static float F(JsonElement element, string name) => element.Req(name).GetSingle();

    private static float Opt(JsonElement element, string name, float fallback) =>
        element.TryGetProperty(name, out var value) ? value.GetSingle() : fallback;

    private static Color C(JsonElement element, string name) => new(element.Req(name).GetString()!);
}
