using Godot;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look;

/// <summary>
/// Maps a material role and base colour to a painterly material driven by the style preset (L3). The
/// preset's materials.roles (or materials.default) sets albedo softening, stroke normal strength, edge
/// wear, roughness, metallic, tint and an optional painted texture; x_look_role_marks sets the pattern and
/// marks per role and x_look_paint the shader's shared multipliers. A role tint is a glaze: it moves the
/// base colour's hue toward the tint while keeping its lightness.
/// </summary>
public static class MaterialLibrary
{
    public const string ShaderPath = "res://shaders/painterly_material.gdshader";
    private static StylePreset? _preset;
    private static Shader? _shader;
    private static Shader? _landShader;
    private static readonly Dictionary<string, Material> Cache = new();
    private static readonly HashSet<string> SetNames = new();
    private static readonly HashSet<string> LandSetNames = new();
    private static readonly HashSet<string> WaterSetNames = new();
    private static Shader? _waterShader;

    /// <summary>Every shader parameter name a material from here has been given; each must be a uniform of the shader.</summary>
    public static IReadOnlyCollection<string> ParameterNames => SetNames;

    /// <summary>Every parameter name a landscape material (ForLandscape) has been given; each must be a uniform of painterly_land.gdshader.</summary>
    public static IReadOnlyCollection<string> LandscapeParameterNames => LandSetNames;

    /// <summary>Every parameter name a landscape water material has been given; each must be a uniform of painterly_water.gdshader.</summary>
    public static IReadOnlyCollection<string> WaterParameterNames => WaterSetNames;

    public static void Configure(StylePreset preset)
    {
        _preset = preset;
        _shader = GD.Load<Shader>(ShaderPath);
        _landShader = GD.Load<Shader>(LandscapeLook.ShaderPath);
        _waterShader = GD.Load<Shader>(LandscapeLook.WaterShaderPath);
        Cache.Clear();
    }

    public static RoleLook LookFor(string role) => (_preset?.Tuning ?? LookTuning.Default).MarksFor(role);

    /// <summary>Whether a material was made here (and so takes paint seeds and box edges).</summary>
    public static bool IsPainterly(Material? material) => material != null && material.HasMeta("material_role") && material is ShaderMaterial;

    private static void Set(ShaderMaterial material, string name, Variant value)
    {
        SetNames.Add(name);
        material.SetShaderParameter(name, value);
    }

    public static Material For(string role, Color baseColor) => Make(role, baseColor, null);

    /// <summary>
    /// The painterly material for a captured mesh surface (review minor: captured assets got no painterly treatment): the
    /// role's marks and softening over the surface's own colour, with its colour texture, if it has one, softened into a
    /// block-in. Falls back to a plain material, like For, when no preset is configured.
    /// </summary>
    public static Material ForCaptured(string role, Color albedo, Texture2D? texture) => Make(role, albedo, texture);

    private static void SetLand(ShaderMaterial material, string name, Variant value)
    {
        LandSetNames.Add(name);
        material.SetShaderParameter(name, value);
    }

    /// <summary>
    /// The painterly material of a landscape's harness role (Run 2: the land in the game). factor is the glTF material's colour,
    /// the envelope of the role's blends; the shader multiplies it by the baked vertex colour, which is the colour the generator
    /// meant. bake is the flat colour VoxelGI voxelizes in its place. Falls back, like For, to a plain material that does keep
    /// the vertex colour when no preset is configured.
    /// </summary>
    public static Material ForLandscape(string role, Color factor, Color bake)
    {
        var key = "land|" + role + "|" + factor.ToHtml(false) + "|" + bake.ToHtml(false);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var marks = LandscapeLook.Roles[role];
        if (_preset == null || _landShader == null)
        {
            var plain = new StandardMaterial3D { AlbedoColor = factor, VertexColorUseAsAlbedo = true, Roughness = marks.Roughness, ResourceName = $"land {role}" };
            Cache[key] = plain;
            return plain;
        }
        var paint = _preset.Tuning.Paint;
        if (LandscapeLook.IsWater(role) && _waterShader != null) return Cache[key] = ForWater(role, marks, factor, bake, paint);
        var material = new ShaderMaterial { Shader = _landShader, ResourceName = $"painterly land {role}" };
        SetLand(material, "base_color", factor);
        SetLand(material, "kind", (int)marks.Kind);
        SetLand(material, "albedo_softening", marks.Softening);
        SetLand(material, "stroke_normal_strength", marks.NormalStrength);
        SetLand(material, "roughness_value", marks.Roughness);
        SetLand(material, "metallic_value", marks.Metallic);
        SetLand(material, "specular_strength", marks.Specular);
        SetLand(material, "scale_m", marks.ScaleM);
        SetLand(material, "scale2_m", marks.Scale2M);
        SetLand(material, "stroke_stretch", marks.Stretch);
        SetLand(material, "stroke_axis", marks.Axis);
        SetLand(material, "variation", marks.Variation);
        SetLand(material, "role_calm", marks.Calm);
        SetLand(material, "ramp_gain", marks.RampGain);
        SetLand(material, "flow_speed", marks.FlowSpeed);
        SetLand(material, "glaze", marks.Glaze ?? Vector3.One);
        SetLand(material, "wrap_light", marks.Wrap);
        SetLand(material, "terminator_warmth", marks.Warmth);
        SetLand(material, "sheen", marks.Sheen);
        SetLand(material, "sky_rim", marks.SkyRim);
        SetLand(material, "tone_wash", paint.ToneWash);
        SetLand(material, "tone_strokes", paint.ToneStrokes);
        SetLand(material, "tone_bristle", paint.ToneBristle);
        SetLand(material, "tone_gain", paint.ToneGain);
        SetLand(material, "temperature_variation", paint.TemperatureVariation);
        SetLand(material, "pastel_mix", paint.PastelMix);
        SetLand(material, "pastel_chroma", paint.PastelChroma);
        SetLand(material, "pastel_scale", paint.PastelScale);
        SetLand(material, "pastel_lift", paint.PastelLift);
        SetLand(material, "cavity_darkening", paint.CavityDarkening);
        SetLand(material, "stroke_normal_gain", paint.StrokeNormalGain);
        SetLand(material, "mark_fade_start", paint.MarkFadeStart);
        SetLand(material, "mark_fade_end", paint.MarkFadeEnd);
        // The brush's finer marks close up (the land up close, Run 2): full strength for every role.
        SetLand(material, "close_detail", LandscapeLook.CloseDetail);
        // The shader's haze stays off: the sea mist past the island is the environment's fog (OpenSea).
        SetLand(material, "haze_color", new Color(0.75f, 0.85f, 0.93f));
        SetLand(material, "haze_energy", 1f);
        SetLand(material, "haze_start_m", 0f);
        SetLand(material, "haze_end_m", 1f);
        SetLand(material, "haze_max", 0f);
        SetLand(material, "haze_far_m", 0f);
        // The living shore (ShoreWash): off, with the island garage's numbers ready; OpenSea turns it on for a room with a sea.
        foreach (var (name, value) in ShoreWash.LandParameters(ShoreLook.Default, false, ShoreWash.FreeRunning, 0f, ShoreWash.DefaultFilmColour, SeaLook.Default.FoamColor, null)) SetLand(material, name.ToString(), value);
        material.SetMeta("material_role", role);
        material.SetMeta("landscape", true);
        // VoxelGI voxelizes BaseMaterial3D albedo only, and ignores vertex colour: the look director bakes with this colour instead.
        material.SetMeta("bake_albedo", bake);
        Cache[key] = material;
        return material;
    }

    private static void SetWater(ShaderMaterial material, string name, Variant value)
    {
        WaterSetNames.Add(name);
        material.SetShaderParameter(name, value);
    }

    /// <summary>
    /// A landscape's still or flowing water: the water shader, a glaze you can see into (clear shallows, a darker and cooler deep
    /// middle, visible from below), with the role's ripples and the WaterLook of its kind.
    /// </summary>
    private static ShaderMaterial ForWater(string role, LandRole marks, Color factor, Color bake, PaintTuning paint)
    {
        var water = WaterLook.For(marks.Kind);
        var material = new ShaderMaterial { Shader = _waterShader, ResourceName = $"painterly water {role}" };
        SetWater(material, "base_color", factor);
        SetWater(material, "kind", (int)marks.Kind);
        SetWater(material, "scale_m", marks.ScaleM);
        SetWater(material, "stroke_axis", marks.Axis);
        SetWater(material, "stroke_stretch", marks.Stretch);
        SetWater(material, "variation", marks.Variation);
        SetWater(material, "flow_speed", marks.FlowSpeed);
        SetWater(material, "roughness_value", marks.Roughness);
        SetWater(material, "specular_strength", marks.Specular);
        SetWater(material, "stroke_normal_strength", marks.NormalStrength);
        SetWater(material, "glaze", marks.Glaze ?? Vector3.One);
        SetWater(material, "wrap_light", marks.Wrap);
        SetWater(material, "sky_rim", marks.SkyRim);
        SetWater(material, "clarity_m", water.ClarityM);
        SetWater(material, "surface_alpha", water.SurfaceAlpha);
        SetWater(material, "max_alpha", water.MaxAlpha);
        SetWater(material, "deep_m", water.DeepM);
        SetWater(material, "wash_steps", water.WashSteps);
        SetWater(material, "wash_wobble", water.WashWobble);
        SetWater(material, "shallow_tint", water.ShallowTint);
        SetWater(material, "deep_tint", water.DeepTint);
        SetWater(material, "shore_m", water.ShoreM);
        SetWater(material, "shore_strength", water.ShoreStrength);
        SetWater(material, "sky_color", water.SkyColor);
        SetWater(material, "underside_tint", water.UndersideTint);
        SetWater(material, "underside_alpha", water.UndersideAlpha);
        SetWater(material, "stroke_normal_gain", paint.StrokeNormalGain);
        SetWater(material, "mark_fade_start", paint.MarkFadeStart);
        SetWater(material, "mark_fade_end", paint.MarkFadeEnd);
        // The open sea's switches (OpenSea): off for a room's own water, which is painted in its own space and has no hole; the
        // horizon fade is set by the look for the camera's far plane once the room has a sea.
        SetWater(material, "world_pattern", false);
        SetWater(material, "use_hole", false);
        SetWater(material, "hole", Vector4.Zero);
        SetWater(material, "horizon_color", water.SkyColor);
        SetWater(material, "horizon_energy", 1f);
        SetWater(material, "horizon_fade_start_m", 0f);
        SetWater(material, "horizon_fade_end_m", 0f);
        // The sea's look (OpenSea turns it on for the sea's water): off for ponds and brooks.
        SetWater(material, "use_sea", false);
        SetWater(material, "sea_level", 0f);
        SetWater(material, "sea_deep_m", SeaLook.Default.DeepM);
        SetWater(material, "sea_clarity_m", SeaLook.Default.ClarityM);
        SetWater(material, "sea_deep_tint", SeaLook.Default.DeepTint);
        SetWater(material, "foam_color", SeaLook.Default.FoamColor);
        SetWater(material, "foam_depth_m", SeaLook.Default.FoamDepthM);
        SetWater(material, "foam_strength", SeaLook.Default.FoamStrength);
        SetWater(material, "wave_spacing_m", SeaLook.Default.WaveSpacingM);
        SetWater(material, "wave_speed", SeaLook.Default.WaveSpeed);
        // The living shore (ShoreWash): off for ponds and brooks; OpenSea turns it on for the sea's water.
        foreach (var (name, value) in ShoreWash.WaterParameters(ShoreLook.Default, false)) SetWater(material, name.ToString(), value);
        material.SetMeta("material_role", role);
        material.SetMeta("landscape", true);
        material.SetMeta("water", true);
        // VoxelGI voxelizes BaseMaterial3D albedo only: the look director bakes with this colour instead.
        material.SetMeta("bake_albedo", bake);
        return material;
    }

    private static Material Make(string role, Color baseColor, Texture2D? photo)
    {
        var key = role + "|" + baseColor.ToHtml(false) + (photo != null ? "|photo" + photo.GetInstanceId() : "");
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (_preset == null || _shader == null)
        {
            // No preset yet (a builder test without a look): a plain, honest material.
            var plain = new StandardMaterial3D { AlbedoColor = baseColor, AlbedoTexture = photo, Roughness = 0.92f, ResourceName = $"{role} {baseColor.ToHtml(false)}" };
            Cache[key] = plain;
            return plain;
        }
        var treatment = _preset.TreatmentFor(role);
        var look = LookFor(role);
        var paint = _preset.Tuning.Paint;
        var albedo = Glaze(baseColor, treatment.Tint, _preset.GlazeAmount);
        var material = new ShaderMaterial { Shader = _shader, ResourceName = $"painterly {role} {baseColor.ToHtml(false)}" };
        Set(material, "base_color", albedo);
        Set(material, "accent_color", Accent(albedo, _preset.Accents, paint));
        Set(material, "pattern", (int)look.Pattern);
        Set(material, "albedo_softening", treatment.AlbedoSoftening);
        Set(material, "stroke_normal_strength", treatment.StrokeNormalStrength);
        Set(material, "edge_wear", treatment.EdgeWear);
        Set(material, "roughness_value", treatment.Roughness);
        Set(material, "metallic_value", treatment.Metallic);
        Set(material, "specular_strength", look.Specular);
        Set(material, "stroke_scale_m", look.StrokeScaleM);
        Set(material, "stroke_stretch", look.StrokeStretch);
        Set(material, "stroke_axis", look.StrokeAxis);
        Set(material, "pattern_scale_m", look.PatternScaleM);
        Set(material, "variation", look.Variation);
        Set(material, "role_calm", look.Calm);
        Set(material, "wrap_light", look.Wrap);
        Set(material, "terminator_warmth", look.TerminatorWarmth);
        Set(material, "sheen", look.Sheen);
        Set(material, "bevel_min_m", _preset.BevelM);
        Set(material, "bevel_fraction", paint.BevelFraction);
        Set(material, "bevel_max_m", paint.BevelMaxM);
        Set(material, "tone_wash", paint.ToneWash);
        Set(material, "tone_strokes", paint.ToneStrokes);
        Set(material, "tone_bristle", paint.ToneBristle);
        Set(material, "tone_gain", paint.ToneGain);
        Set(material, "softening_calm", paint.SofteningCalm);
        Set(material, "temperature_variation", paint.TemperatureVariation);
        Set(material, "pastel_mix", paint.PastelMix);
        Set(material, "pastel_chroma", paint.PastelChroma);
        Set(material, "pastel_scale", paint.PastelScale);
        Set(material, "pastel_lift", paint.PastelLift);
        Set(material, "wear_brightness", paint.WearBrightness);
        Set(material, "wear_lift", paint.WearLift);
        Set(material, "cavity_darkening", paint.CavityDarkening);
        Set(material, "stroke_normal_gain", paint.StrokeNormalGain);
        Set(material, "mark_fade_start", paint.MarkFadeStart);
        Set(material, "mark_fade_end", paint.MarkFadeEnd);
        Set(material, "photo_mix", photo != null ? 1f : 0f);
        Set(material, "photo_blur_lod", paint.PhotoBlurLod);
        if (photo != null)
        {
            Set(material, "photo_texture", photo);
            material.SetMeta("captured", true);
        }
        if (treatment.Texture is { } texturePath && ResourceLoader.Exists(texturePath))
        {
            Set(material, "paint_texture", GD.Load<Texture2D>(texturePath));
            Set(material, "texture_strength", look.TextureStrength);
            Set(material, "texture_scale_m", look.TextureScaleM);
        }
        material.SetMeta("material_role", role);
        // VoxelGI voxelizes BaseMaterial3D albedo only; the look director bakes with this colour instead.
        material.SetMeta("bake_albedo", albedo);
        Cache[key] = material;
        return material;
    }

    /// <summary>Move a colour's hue toward a tint while keeping its lightness. No tint, or amount 0, leaves it unchanged.</summary>
    public static Color Glaze(Color baseColor, Color? tint, float amount)
    {
        if (tint is not { } t || amount <= 0f) return baseColor;
        var baseLuma = ColorGrade.Luma(baseColor);
        var tintLuma = Mathf.Max(ColorGrade.Luma(t), 1e-3f);
        var matched = new Color(t.R * baseLuma / tintLuma, t.G * baseLuma / tintLuma, t.B * baseLuma / tintLuma);
        var glazed = baseColor.Lerp(matched, amount);
        return new Color(Mathf.Clamp(glazed.R, 0f, 1f), Mathf.Clamp(glazed.G, 0f, 1f), Mathf.Clamp(glazed.B, 0f, 1f));
    }

    /// <summary>A trim colour for patterns such as a rug border: the palette accent furthest in hue from the base, softened toward it.</summary>
    public static Color Accent(Color baseColor, IReadOnlyList<Color> accents, PaintTuning paint)
    {
        if (accents.Count == 0) return baseColor.Darkened(paint.AccentDarken * 2f);
        var pick = accents.OrderByDescending(a => HueDistance(a, baseColor)).First();
        return baseColor.Lerp(pick, paint.AccentMix).Darkened(paint.AccentDarken);
    }

    private static float HueDistance(Color a, Color b)
    {
        var d = Mathf.Abs(a.H - b.H);
        return Mathf.Min(d, 1f - d) * Mathf.Min(a.S + 0.2f, 1f);
    }

    /// <summary>The flat colour VoxelGI should voxelize for a material built here, or null for other materials.</summary>
    public static Color? BakeAlbedo(Material? material) =>
        material != null && material.HasMeta("bake_albedo") ? material.GetMeta("bake_albedo").AsColor() : null;

    /// <summary>
    /// How much of a brush mark the shader keeps when one pixel covers footprint metres of a mark size metres
    /// across: all of it below mark_fade_start × size, none above mark_fade_end × size (smoothstep between).
    /// Mirrors detail() in painterly_material.gdshader; with mark_fade_end at most 0.5 a mark is gone before
    /// its Nyquist limit (a pixel covering half a period), so marks appear as the camera comes close instead
    /// of aliasing.
    /// </summary>
    public static float Detail(float sizeM, float footprintM, PaintTuning paint)
    {
        var x = Mathf.Clamp((footprintM - paint.MarkFadeStart * sizeM) / ((paint.MarkFadeEnd - paint.MarkFadeStart) * sizeM), 0f, 1f);
        return 1f - x * x * (3f - 2f * x);
    }

    // ---------- the characters (Run 2, characters round, L8; CharacterLook applies these; docs/look/CHARACTERS.md) ----------
    // A drawn character keeps the family's colours exactly, so none of these glaze, calm or soften a colour, and none needs the
    // room's preset: their numbers are FeltLook, BubbleLook and CharacterEffectLook, code constants for this round.

    public const string CharacterFeltShaderPath = "res://shaders/character_felt.gdshader";
    public const string BubbleShaderPath = "res://shaders/bubble.gdshader";
    public const string BubbleDepthShaderPath = "res://shaders/character_bubble_depth.gdshader";
    /// <summary>
    /// The bubble's depth pass draws after every other see-through thing (they all sit at priority 0 or below; the underwater veil,
    /// at 100, ignores depth), so the outline's depth hides nothing see-through and still reaches depth of field and the focus pass.
    /// </summary>
    public const int BubbleDepthRenderPriority = 90;

    private static Shader? _feltShader;
    private static Shader? _bubbleShader;
    private static Shader? _bubbleDepthShader;
    private static readonly Dictionary<(Color, FeltLook), ShaderMaterial> FeltCache = new();
    private static readonly Dictionary<Color, StandardMaterial3D> InkCache = new();
    private static readonly HashSet<string> FeltSetNames = new();
    private static readonly HashSet<string> BubbleSetNames = new();
    private static readonly HashSet<string> BubbleDepthSetNames = new();

    /// <summary>Every parameter name a character felt material has been given; each must be a uniform of character_felt.gdshader.</summary>
    public static IReadOnlyCollection<string> FeltParameterNames => FeltSetNames;
    /// <summary>Every parameter name a bubble material has been given; each must be a uniform of bubble.gdshader.</summary>
    public static IReadOnlyCollection<string> BubbleParameterNames => BubbleSetNames;
    /// <summary>Every parameter name a bubble's depth pass has been given; each must be a uniform of character_bubble_depth.gdshader.</summary>
    public static IReadOnlyCollection<string> BubbleDepthParameterNames => BubbleDepthSetNames;

    private static void SetIn(HashSet<string> names, ShaderMaterial material, string name, Variant value)
    {
        names.Add(name);
        material.SetShaderParameter(name, value);
    }

    /// <summary>
    /// The felt figurine treatment of an opaque character part: exactly baseColor (the family chose it), soft and a little fuzzy in
    /// the light, matte, with a little rim. Shared per colour and look: nothing on it ever changes.
    /// </summary>
    public static ShaderMaterial ForCharacterFelt(Color baseColor, FeltLook? look = null)
    {
        var l = look ?? FeltLook.Default;
        if (FeltCache.TryGetValue((baseColor, l), out var cached)) return cached;
        _feltShader ??= GD.Load<Shader>(CharacterFeltShaderPath);
        var material = new ShaderMaterial { Shader = _feltShader, ResourceName = $"character felt {baseColor.ToHtml(false)}" };
        SetIn(FeltSetNames, material, "base_color", baseColor);
        SetIn(FeltSetNames, material, "wrap_light", l.Wrap);
        SetIn(FeltSetNames, material, "terminator_warmth", l.TerminatorWarmth);
        SetIn(FeltSetNames, material, "sheen", l.Sheen);
        SetIn(FeltSetNames, material, "sheen_power", l.SheenPower);
        SetIn(FeltSetNames, material, "rim_light", l.RimLight);
        SetIn(FeltSetNames, material, "rim_power", l.RimPower);
        SetIn(FeltSetNames, material, "fuzz_strength", l.FuzzStrength);
        SetIn(FeltSetNames, material, "fuzz_scale_m", l.FuzzScaleM);
        SetIn(FeltSetNames, material, "tuft_scale_m", l.TuftScaleM);
        SetIn(FeltSetNames, material, "mark_fade_start", l.MarkFadeStart);
        SetIn(FeltSetNames, material, "mark_fade_end", l.MarkFadeEnd);
        material.SetMeta(CharacterLook.RoleMeta, "plain");
        material.SetMeta("bake_albedo", baseColor);
        return FeltCache[(baseColor, l)] = material;
    }

    /// <summary>A drawn line or mark: unshaded in exactly its own dark colour, so it reads as ink at any hour. Shared per colour.</summary>
    public static StandardMaterial3D ForCharacterInk(Color baseColor)
    {
        if (InkCache.TryGetValue(baseColor, out var cached)) return cached;
        var material = new StandardMaterial3D
        {
            ResourceName = $"character ink {baseColor.ToHtml(false)}", ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = baseColor,
        };
        material.SetMeta(CharacterLook.RoleMeta, "ink");
        return InkCache[baseColor] = material;
    }

    /// <summary>
    /// An effect part (a sparkle, a flame): its colour, emitting energy times over so the bloom (from 0.9) catches it, unshadowed.
    /// A new material each time: an aura recolours one character's effects without touching another's.
    /// </summary>
    public static StandardMaterial3D ForCharacterEffect(Color color, float energy)
    {
        var material = new StandardMaterial3D
        {
            ResourceName = "character effect", AlbedoColor = color, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = energy,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled, MetallicSpecular = 0f, Roughness = 1f, DisableReceiveShadows = true,
        };
        material.SetMeta(CharacterLook.RoleMeta, "effect");
        return material;
    }

    /// <summary>
    /// The soap bubble for one bubble part: the film in the fallback colour's light tint with the aura's shimmering rim, and its
    /// depth pass chained after it (drawn late). A new material each time: the hem's band is the part's own and the aura is the
    /// character's.
    /// </summary>
    public static ShaderMaterial ForBubble(Color fallback, Color aura, BubbleHem hem, BubbleLook? look = null)
    {
        var l = look ?? BubbleLook.Default;
        _bubbleShader ??= GD.Load<Shader>(BubbleShaderPath);
        _bubbleDepthShader ??= GD.Load<Shader>(BubbleDepthShaderPath);
        var depth = new ShaderMaterial { Shader = _bubbleDepthShader, ResourceName = "character bubble depth", RenderPriority = BubbleDepthRenderPriority };
        SetIn(BubbleDepthSetNames, depth, "rim_power", l.RimPower);
        SetIn(BubbleDepthSetNames, depth, "rim_depth_min", l.RimDepthMin);
        SetHem(BubbleDepthSetNames, depth, hem, l);
        var material = new ShaderMaterial { Shader = _bubbleShader, ResourceName = $"character bubble {fallback.ToHtml(false)}", NextPass = depth };
        SetIn(BubbleSetNames, material, "tint_color", fallback);
        SetIn(BubbleSetNames, material, "tint_alpha", l.TintAlpha);
        SetIn(BubbleSetNames, material, "aura_color", aura);
        SetIn(BubbleSetNames, material, "rim_power", l.RimPower);
        SetIn(BubbleSetNames, material, "rim_alpha", l.RimAlpha);
        SetIn(BubbleSetNames, material, "rim_glow", l.RimGlow);
        SetIn(BubbleSetNames, material, "shimmer", l.Shimmer);
        SetIn(BubbleSetNames, material, "shimmer_scale_m", l.ShimmerScaleM);
        SetIn(BubbleSetNames, material, "shimmer_speed", l.ShimmerSpeed);
        SetIn(BubbleSetNames, material, "iridescence", l.Iridescence);
        SetIn(BubbleSetNames, material, "film_bands", l.FilmBands);
        SetIn(BubbleSetNames, material, "back_skin", l.BackSkin);
        SetIn(BubbleSetNames, material, "glint", l.Glint);
        SetIn(BubbleSetNames, material, "glint_size", l.GlintSize);
        SetIn(BubbleSetNames, material, "light_level", 1f);
        SetHem(BubbleSetNames, material, hem, l);
        material.SetMeta(CharacterLook.RoleMeta, "bubble");
        return material;
    }

    private static void SetHem(HashSet<string> names, ShaderMaterial material, BubbleHem hem, BubbleLook look)
    {
        SetIn(names, material, "hem_amplitude_m", look.HemAmplitudeM);
        SetIn(names, material, "hem_waves", look.HemWaves);
        SetIn(names, material, "hem_speed", look.HemSpeed);
        SetIn(names, material, "hem_bottom_m", hem.BottomM);
        SetIn(names, material, "hem_top_m", hem.TopM);
        SetIn(names, material, "hem_center_m", hem.CenterM);
    }

    /// <summary>Give a bubble a new aura colour (its rim and shimmer).</summary>
    public static void SetBubbleAura(ShaderMaterial bubble, Color aura) => SetIn(BubbleSetNames, bubble, "aura_color", aura);

    /// <summary>Tell a bubble how lit its place is (0 to 1): the film is unshaded, so its tint and glint follow this.</summary>
    public static void SetBubbleLight(ShaderMaterial bubble, float level) => SetIn(BubbleSetNames, bubble, "light_level", level);
}
