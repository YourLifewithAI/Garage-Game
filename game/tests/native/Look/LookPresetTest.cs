using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Look;

/// <summary>
/// Look track checks that run headless: the preset reader and the look numbers it carries, painterly materials
/// per role and the shader's uniforms, the colour grade, its season tint and its (small) global tint, time of day at
/// every minute of the day, the solar model, light only from real sources (the sun only through the window, the sky
/// fill, the lamps' switch, moonlit nights), depth of field and where it focuses, the grain and vignette effect's
/// parameters, the VoxelGI bake stand-ins, the look director's dressing of rooms, the grade cache, and the renderer
/// fallback notice.
/// Rendering itself (the VoxelGI bake, the post effect's pixels, the images) needs a GPU and is checked by the
/// capture harness (tools/look/capture-look.ps1), not here.
/// </summary>
public partial class LookPresetTest : Node3D
{
    private int _checks;
    private int _failures;
    private string _presetText = "";

    public override async void _Ready()
    {
        try
        {
            var preset = StylePreset.Resolve(RoomWorld.DefaultStyleId, RoomWorld.DefaultStyleVersion, FileAccess.GetSha256(RoomWorld.DefaultStyle));
            _presetText = Encoding.UTF8.GetString(FileAccess.GetFileAsBytes(RoomWorld.DefaultStyle));
            var room = RoomData.Load(RoomWorld.DefaultRoom);
            if (OS.GetCmdlineUserArgs().Contains("--worldclock-only"))
            {
                CheckWorldClock(preset);
                await CheckWorldClockInTheLook(preset, room);
                GD.Print($"NATIVE_LOOK_WORLDCLOCK: {_checks - _failures}/{_checks} checks passed");
                GetTree().Quit(_failures == 0 ? 0 : 1);
                return;
            }
            CheckPreset(preset, room);
            CheckLookNumbersLiveInThePreset(preset);
            CheckFailClosed();
            CheckMaterials(preset);
            CheckShaderUniforms(preset);
            CheckGrade(preset);
            CheckSeasonTint(preset);
            CheckClock(preset);
            CheckClockAtEveryMinute(preset);
            CheckRealClockAndSwings(preset);
            CheckWorldClock(preset);
            await CheckWorldClockInTheLook(preset, room);
            CheckSolarModel(preset);
            CheckReducedGlobalTint(preset, room);
            CheckReviewCameras(preset, room);
            CheckDepthOfField(preset);
            CheckPostEffectParameters(preset);
            CheckBakeStandIns(preset, room);
            CheckRendererFallback(preset, room);
            await CheckDirector(preset, room);
            await CheckDressing(preset, room);
            await CheckFocus(preset, room);
            await CheckGradeCache(preset, room);
            await CheckArtDirectionFocus(preset, room);
            await CheckSunThroughWindow(preset, room);
            CheckNightLevels(preset, room);
            CheckGoldenHourCoolFill(preset, room);
            await CheckSite(preset, room);
            await CheckSunAndWindows(preset, room);
            await CheckWindowSky(preset, room);
            await CheckCapturedWalls(preset);
            await CheckFocusEasing(preset, room);
            await CheckObserve(preset, room);
            var observeV2 = StylePreset.Resolve(RoomWorld.DefaultStyleId, 2);
            var v1 = StylePreset.Resolve(RoomWorld.DefaultStyleId, 1);
            CheckObservePlayerEnvelope(observeV2);
            CheckObserveFrameBudget(v1, observeV2);
            await CheckObservePlayerTracking(observeV2, room);
            CheckViewBlur(v1, observeV2);
            await CheckViewChoice(observeV2, room);
            CheckSeaMist();
            await CheckFocusHighlight(preset, room);
            CheckFocusPass(preset);
            CheckSeasonLooks(preset);
            await CheckLightProtections(preset, room);
            await CheckLandscape(preset, room);
            await CheckTenTimes(preset, room);
            await CheckPondLife(preset);
            await CheckOpenSeaSynthetic(preset);
            await CheckRealOpenSea(preset);
            await CheckShore(preset);
            CheckLandUpClose(preset);
            await CheckGlow(preset, room);
            await CheckEffects(preset, room);
            await CheckIntro(preset);
            await CheckSeasons(preset, room);
            await CheckFractals(preset);
            await CheckCharm(preset);
            MaterialLibrary.Configure(preset);
            GD.Print($"NATIVE_LOOK: {_checks - _failures}/{_checks} checks passed; preset reader and look numbers, role materials and shader uniforms, grade, season tint and reduced global tint, clock at every minute, the island's world clock (today's reading unchanged, day_scale 4 midnights, dawn rollover, phase, zone, seasons and hemisphere), solar model, sun only through the window, night levels and lamps, golden-hour cool fill, depth of field and focus, post effect parameters, bake stand-ins, room dressing, grade cache, renderer notice, the landscape's role marks and baked colours, open land, water you can see into and the fish in it, each view's blur, the open sea, its mist and the seam (no distant islands), how far an island of any size is seen (far plane, haze, shadows) and its look-suite cameras, the living shore (the wash's clock, its calm, the damp band within the named reach of the coast, nothing past the beaches), the focus highlight, the Gubble's glow and the light-level estimate, its bubbles and fireworks, the room-to-island intro, the seasons on the land, the Fractals and the hamlet's charm");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError("Look test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    // ---------- preset ----------

    private void CheckPreset(StylePreset preset, RoomData room)
    {
        Check(preset.Status is "seed" or "draft" or "candidate" or "approved", $"the preset has a known status ({preset.Status})");
        Check(preset.RendererMethod == "forward_plus" && ProjectSettings.GetSetting("rendering/renderer/rendering_method").AsString() == "forward_plus",
            "the preset and the project both use Forward+");
        Check(preset.GiMode == "voxelgi" && preset.GiEnergy > 0 && preset.AoEnabled && preset.GlowEnabled && preset.ShadowsEnabled,
            "VoxelGI, SSAO, glow and shadows are on");
        Check(preset.DofEnabled && preset.FocusBandM > 0 && preset.NearBlurDistanceM < 0.15f, "depth of field is on and near blur starts inside the 15 cm reach");
        Check(preset.TimeKeys.Count >= 2 && preset.SeasonGrades.Count == 4, "time-of-day keys and four season grades are read");
        Check(preset.KeyMode == "sun", "the key is the real sun (x_look_key_mode sun): light only from real sources");
        Check(preset.TargetFrameMs > 0 && preset.MaxShadowedLights >= 1, "budgets are read");
        Check(preset.RoleRoughness.ContainsKey("wood") && Mathf.IsEqualApprox(preset.DefaultRoughness, preset.DefaultTreatment.Roughness),
            "the roughness views RoomWorld's tests rely on still work");
        var roles = room.Shell.Select(s => s.MaterialRole).Concat(room.Objects.SelectMany(o => o.Asset.Materials.Select(m => m.Role))).Distinct().ToArray();
        var missing = roles.Where(r => !preset.RoleTreatments.ContainsKey(r)).ToArray();
        Check(missing.Length == 0, "every material role in the test room has its own treatment: missing " + string.Join(",", missing));
        var unpainted = preset.RoleTreatments.Keys.Where(r => !preset.Tuning.RoleMarks.ContainsKey(r)).ToArray();
        Check(unpainted.Length == 0, "every role the preset treats has its own brush marks in the preset: missing " + string.Join(",", unpainted));
    }

    /// <summary>Review finding: look constants lived in C#. The shipped preset now states every look number itself.</summary>
    private void CheckLookNumbersLiveInThePreset(StylePreset preset)
    {
        // A block added after the preset's version was written is left to the code's defaults, which reproduce that version's look.
        var unstated = preset.Tuning.Defaulted.Where(d => !(LookTuning.BlocksSince.TryGetValue(d.Split('.')[0], out var since) && since > preset.PresetVersion)).ToArray();
        Check(unstated.Length == 0, "the preset states every look number its version knows; none is left to the code's defaults: " + string.Join(", ", unstated.Take(12)));
        var newest = StylePreset.Resolve(RoomWorld.DefaultStyleId, LookTuning.BlocksSince.Values.Max());
        Check(newest.Tuning.Defaulted.Count == 0, $"v{newest.PresetVersion} states every look number, the newer blocks too: " + string.Join(", ", newest.Tuning.Defaulted.Take(12)));
        Check(LookTuning.Blocks.All(StylePreset.KnownLookExtensions.Contains), "every tuning block is a known look extension");
        // The numbers really come from the file: change a few and the parsed look follows.
        var edited = PresetWith(Field("sky_fill_energy", "3.25"), Field("lut_size", "17"), Field("vignette_start", "0.3"), Field("tilt_pitch_gain", "0.75"),
            Field("moon_elevation_deg", "40"), Field("key_normal_bias", "0.4"));
        Check(Mathf.IsEqualApprox(edited.Tuning.Lamps.SkyFillEnergy, 3.25f) && edited.Tuning.Grade.LutSize == 17 && Mathf.IsEqualApprox(edited.Tuning.Post.VignetteStart, 0.3f)
            && Mathf.IsEqualApprox(edited.Tuning.Dof.TiltPitchGain, 0.75f) && Mathf.IsEqualApprox(edited.Tuning.Sun.MoonElevationDeg, 40f)
            && Mathf.IsEqualApprox(edited.Tuning.Shadows.KeyNormalBias, 0.4f), "edited tuning numbers in the preset reach the runtime");
        // A role's marks come from the preset, not a table in the code.
        var restyled = PresetWith(("\"wood\": { \"pattern\": \"wood\"", "\"wood\": { \"pattern\": \"stone\""));
        MaterialLibrary.Configure(restyled);
        var wood = (ShaderMaterial)MaterialLibrary.For("wood", new Color("9a6f45"));
        var other = (ShaderMaterial)MaterialLibrary.For("emissive", new Color("9a6f45"));
        Check(wood.GetShaderParameter("pattern").AsInt32() == (int)PaintPattern.Stone, "a role's pattern is read from the preset's role marks");
        Check(other.GetShaderParameter("pattern").AsInt32() == (int)restyled.Tuning.DefaultMarks.Pattern
            && Mathf.IsEqualApprox(other.GetShaderParameter("stroke_scale_m").AsSingle(), restyled.Tuning.DefaultMarks.StrokeScaleM),
            "a role the preset does not list takes the preset's default marks");
        MaterialLibrary.Configure(preset);
    }

    private void CheckFailClosed()
    {
        var bytes = Encoding.UTF8.GetBytes(_presetText.Replace("\"focus_band_m\"", "\"focus_band\""));
        CheckThrows(() => StylePreset.Parse(bytes, "probe.json"), "focus_band_m", "a preset missing a field the look needs is refused, naming it");
        var lastHour = _presetText[_presetText.LastIndexOf("{ \"hour\": ", StringComparison.Ordinal)..].Split(',')[0];
        CheckThrows(() => PresetWith((lastHour, "{ \"hour\": 3.0")), "increasing hour order", "time-of-day keys out of order are refused");
        CheckThrows(() => PresetWith(("\"x_look_paint\": {", "\"x_look_paint\": { \"tone_wsah\": 1,")), "tone_wsah", "a misspelled tuning field is refused, naming it");
        CheckThrows(() => PresetWith(("\"x_look_post\": {", "\"x_look_psot\": {")), "x_look_psot", "a misspelled look block is refused, naming it");
        CheckThrows(() => PresetWith(("\"default\": { \"pattern\"", "\"fallback\": { \"pattern\"")), "default", "role marks without a default entry are refused");
        CheckThrows(() => PresetWith(("\"mark_fade_end\": 0.45", "\"mark_fade_end\": 0.8")), "alias", "marks that would alias before fading are refused");
        CheckThrows(() => PresetWith(("\"x_look_key_mode\": \"sun\"", "\"x_look_key_mode\": \"diorama\"")), "real sources", "the removed diorama key is refused, saying why");
        CheckThrows(() => PresetWith(("\"fixed_day_of_year\": 196", "\"fixed_day_of_year\": 196, \"day_length_h\": [9.5, 13, 15, 11]")), "day_length_h",
            "the old per-season day lengths are refused: the solar model replaced them");
        CheckThrows(() => PresetWith(("\"x_look_sun\": { \"real_clock", "\"x_look_sun\": { \"latitude_deg\": 30, \"real_clock")), "latitude_deg",
            "a preset that carries a site (latitude) is refused: the site belongs to the room (review M5)");
        CheckThrows(() => PresetWith(("\"x_look_sun\": { \"real_clock", "\"x_look_sun\": { \"solar_noon_h\": 12, \"real_clock")), "solar_noon_h",
            "a preset that carries a solar noon is refused too");
        var withoutExtensions = Encoding.UTF8.GetBytes(_presetText[.._presetText.IndexOf(",\n  \"extensions\"", StringComparison.Ordinal)] + "\n}\n");
        var fallback = StylePreset.Parse(withoutExtensions, "probe.json");
        Check(fallback.KeyMode == "fixed" && !fallback.SsilEnabled && Mathf.IsEqualApprox(fallback.GlazeAmount, 0.35f)
            && fallback.Tuning.Defaulted.Contains("x_look_paint.tone_wash") && fallback.Tuning.Defaulted.Contains("x_look_role_marks"),
            "a preset without x_look extensions loads with the code's defaults and lists what it defaulted");
    }

    // ---------- materials and the shader ----------

    private void CheckMaterials(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        foreach (var role in new[] { "painted_wall", "plaster", "wood", "fabric", "paper", "cardboard", "rubber" })
        {
            var color = new Color("8a7a66");
            var material = MaterialLibrary.For(role, color) as ShaderMaterial;
            var treatment = preset.TreatmentFor(role);
            var marks = preset.Tuning.MarksFor(role);
            Check(material != null && material.Shader?.ResourcePath == MaterialLibrary.ShaderPath, $"{role} gets the painterly shader");
            if (material == null) continue;
            Check(Mathf.IsEqualApprox(material.GetShaderParameter("albedo_softening").AsSingle(), treatment.AlbedoSoftening)
                && Mathf.IsEqualApprox(material.GetShaderParameter("stroke_normal_strength").AsSingle(), treatment.StrokeNormalStrength)
                && Mathf.IsEqualApprox(material.GetShaderParameter("edge_wear").AsSingle(), treatment.EdgeWear)
                && Mathf.IsEqualApprox(material.GetShaderParameter("roughness_value").AsSingle(), treatment.Roughness),
                $"{role} carries the preset's softening, stroke strength, edge wear and roughness");
            Check(material.GetShaderParameter("pattern").AsInt32() == (int)marks.Pattern
                && Mathf.IsEqualApprox(material.GetShaderParameter("stroke_scale_m").AsSingle(), marks.StrokeScaleM)
                && Mathf.IsEqualApprox(material.GetShaderParameter("specular_strength").AsSingle(), marks.Specular)
                && material.GetShaderParameter("stroke_axis").AsVector3().IsEqualApprox(marks.StrokeAxis),
                $"{role} uses its role's pattern, stroke size, axis and highlight");
            Check(MaterialLibrary.BakeAlbedo(material) is { } bake && bake.IsEqualApprox(MaterialLibrary.Glaze(color, treatment.Tint, preset.GlazeAmount)),
                $"{role} records the flat colour VoxelGI bakes with");
            Check(ReferenceEquals(material, MaterialLibrary.For(role, color)), $"{role} materials are cached per role and colour");
        }
        var metal = (ShaderMaterial)MaterialLibrary.For("metal", new Color("888888"));
        Check(Mathf.IsEqualApprox(metal.GetShaderParameter("metallic_value").AsSingle(), preset.TreatmentFor("metal").Metallic), "metallic comes from the role treatment");
        var unknown = (ShaderMaterial)MaterialLibrary.For("emissive", new Color("888888"));
        Check(Mathf.IsEqualApprox(unknown.GetShaderParameter("roughness_value").AsSingle(), preset.DefaultTreatment.Roughness)
            && unknown.GetShaderParameter("pattern").AsInt32() == (int)PaintPattern.None, "a role without its own treatment uses the default treatment, unpatterned");
        var paint = preset.Tuning.Paint;
        Check(Mathf.IsEqualApprox(unknown.GetShaderParameter("tone_gain").AsSingle(), paint.ToneGain)
            && Mathf.IsEqualApprox(unknown.GetShaderParameter("mark_fade_end").AsSingle(), paint.MarkFadeEnd)
            && Mathf.IsEqualApprox(unknown.GetShaderParameter("cavity_darkening").AsSingle(), paint.CavityDarkening),
            "the shader's shared paint numbers come from the preset");
        var base1 = new Color("b9b4ab");
        Check(MaterialLibrary.Glaze(base1, null, 0.35f).IsEqualApprox(base1) && MaterialLibrary.Glaze(base1, new Color("c79a63"), 0f).IsEqualApprox(base1),
            "no tint or no glaze leaves the base colour alone");
        var glazed = MaterialLibrary.Glaze(base1, new Color("c79a63"), 1f);
        Check(Mathf.Abs(ColorGrade.Luma(glazed) - ColorGrade.Luma(base1)) < 0.01f && glazed.R > glazed.B, "a full glaze takes the tint's hue at the base colour's lightness");
        // Detail fades before a mark aliases: all of it while a pixel covers under mark_fade_start of the mark, none past half a mark.
        Check(Mathf.IsEqualApprox(MaterialLibrary.Detail(0.04f, 0.04f * paint.MarkFadeStart * 0.9f, paint), 1f)
            && MaterialLibrary.Detail(0.04f, 0.04f * 0.5f, paint) <= 1e-4f
            && MaterialLibrary.Detail(0.04f, 0.04f * 0.3f, paint) is > 0f and < 1f, "brush marks fade out between mark_fade_start and half a mark (no aliasing)");
        var before = MaterialLibrary.For("wood", new Color("9a6f45"));
        MaterialLibrary.Configure(preset);
        Check(!ReferenceEquals(before, MaterialLibrary.For("wood", new Color("9a6f45"))), "configuring a preset clears the material cache");
    }

    /// <summary>
    /// Review finding: a uniform could be ignored and every check still passed. Now: every parameter MaterialLibrary
    /// sets is a uniform the shader declares (Godot silently keeps a misspelled one), every uniform the shader
    /// declares is set from the preset (none sits at the shader's own default), and the shader's code reads every
    /// uniform and instance uniform it declares.
    /// </summary>
    private void CheckShaderUniforms(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        foreach (var role in preset.RoleTreatments.Keys.Append("emissive")) MaterialLibrary.For(role, new Color("8a7a66"));
        MaterialLibrary.ForCaptured("wood", new Color("8a7a66"), ImageTexture.CreateFromImage(Image.CreateEmpty(4, 4, false, Image.Format.Rgb8)));
        var shader = GD.Load<Shader>(MaterialLibrary.ShaderPath);
        var declared = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
        var set = MaterialLibrary.ParameterNames.ToHashSet();
        Check(declared.Count >= 30, $"the shader lists its uniforms ({declared.Count})");
        var unknown = set.Except(declared).ToArray();
        Check(unknown.Length == 0, "every parameter MaterialLibrary sets is a uniform of the shader: not declared " + string.Join(",", unknown));
        var unset = declared.Except(set).ToArray();
        Check(unset.Length == 0, "every uniform of the shader is set from the preset, none left at the shader default: unset " + string.Join(",", unset));
        var source = Regex.Replace(Regex.Replace(shader.Code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        var declarations = Regex.Matches(source, @"^\s*(?:instance\s+)?uniform\s+\w+\s+(\w+)[^;]*;", RegexOptions.Multiline);
        var body = Regex.Replace(source, @"^\s*(?:instance\s+)?uniform\s+[^;]*;", "", RegexOptions.Multiline);
        var names = declarations.Select(m => m.Groups[1].Value).ToArray();
        var ignored = names.Where(n => !Regex.IsMatch(body, $@"\b{n}\b")).ToArray();
        Check(names.Length == declared.Count + 3 && ignored.Length == 0, "the shader reads every uniform it declares (and the three per-instance ones): ignored " + string.Join(",", ignored));
        var instance = Regex.Matches(source, @"^\s*instance\s+uniform\s+\w+\s+(\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        Check(instance.SetEquals(new[] { "box_edges", "box_half_extents", "paint_seed" }), "the per-instance uniforms are the ones the look director sets: " + string.Join(",", instance));
        // Light only from real sources: the shader adds no light where a light is shadowed (the lid-off sky fill is
        // gone), and its warm terminator band follows the light's own warmth, so the cool sky fill and the moon add none.
        var lightFunction = body[body.IndexOf("void light()", StringComparison.Ordinal)..];
        Check(!Regex.IsMatch(lightFunction, @"1\.0\s*-\s*ATTENUATION") && !lightFunction.Contains("shadow_fill"),
            "the shader adds no light inside a cast shadow: shade is lit only by real light (the window's fill and the bounce)");
        Check(Regex.IsMatch(lightFunction, @"light_warmth\s*=\s*clamp\(\(LIGHT_COLOR\.r\s*-\s*LIGHT_COLOR\.b\)") && Regex.IsMatch(lightFunction, @"band\s*=[^;]*\*\s*light_warmth\s*;"),
            "the warm terminator band scales with the light's warmth (none from cool light)");
    }

    // ---------- grade ----------

    private void CheckGrade(StylePreset preset)
    {
        var samples = new[] { new Color(0, 0, 0), new Color(1, 1, 1), new Color(0.2f, 0.5f, 0.8f), new Color(0.9f, 0.4f, 0.1f) };
        Check(samples.All(c => ColorGrade.Apply(GradeParams.Identity, c).IsEqualApprox(c)), "the identity grade changes nothing");
        var lut = ColorGrade.LutBytes(GradeParams.Identity);
        var size = GradeParams.Identity.Tuning.LutSize;
        var worst = 0;
        for (var z = 0; z < size; z += 4)
            for (var y = 0; y < size; y += 4)
                for (var x = 0; x < size; x += 4)
                {
                    var index = ((z * size + y) * size + x) * 3;
                    worst = Math.Max(worst, Math.Abs(lut[index] - (int)MathF.Round((x + 0.5f) / size * 255f)));
                    worst = Math.Max(worst, Math.Abs(lut[index + 2] - (int)MathF.Round((z + 0.5f) / size * 255f)));
                }
        Check(lut.Length == size * size * size * 3 && worst <= 1, $"the identity LUT holds texel-centre inputs (red along x, blue along z; worst {worst}/255)");
        var day = GradeParams.For(preset, LookClock.At(preset, preset.DefaultHour, 279));
        var night = GradeParams.For(preset, LookClock.At(preset, 23f, 279));
        var grey = new Color(0.4f, 0.4f, 0.4f);
        var dayGrey = ColorGrade.Apply(day, grey);
        var nightGrey = ColorGrade.Apply(night, grey);
        Check(ColorGrade.Luma(nightGrey) < ColorGrade.Luma(dayGrey) && nightGrey.B - nightGrey.R > dayGrey.B - dayGrey.R, "nights grade darker and bluer than the afternoon");
        var shadow = ColorGrade.Apply(day, new Color(0.15f, 0.15f, 0.15f));
        Check(shadow.B > shadow.R - 0.01f, "the palette's shadow tint keeps dark greys from going warm");
        // The palette is gentle and the seasons swing it (the founder, 7 October): winter is muted, spring and autumn vibrant.
        var strong = new Color(0.9f, 0.3f, 0.2f);
        float Chroma(Color c) => Mathf.Abs(c.R - ColorGrade.Luma(c));
        float NoonOf(int d) { var (r, s) = LookClock.SunTimes(preset.Tuning.Sun, d); return (r + s) * 0.5f; }
        var winterChroma = Chroma(ColorGrade.Apply(GradeParams.For(preset, LookClock.At(preset, NoonOf(15), 15)), strong));
        var springChroma = Chroma(ColorGrade.Apply(GradeParams.For(preset, LookClock.At(preset, NoonOf(105), 105)), strong));
        Check(preset.Saturation <= 1f && winterChroma < Chroma(strong) && springChroma > 0.9f * Chroma(strong) && winterChroma < 0.6f * springChroma,
            $"the palette is gentle and the seasons swing it: a strong colour keeps {winterChroma / Chroma(strong):P0} of its colour in winter and {springChroma / Chroma(strong):P0} in spring");
        var texture = ColorGrade.LutTexture(day);
        Check(texture.GetWidth() == preset.Tuning.Grade.LutSize && texture.GetDepth() == preset.Tuning.Grade.LutSize, "the grade builds a 3D texture of the preset's LUT size");
        Check(day.Quantized() == GradeParams.For(preset, LookClock.At(preset, preset.DefaultHour + 0.001f, 279)).Quantized(),
            "moments a fraction of a minute apart share one quantized grade (and so one cached LUT)");
    }

    /// <summary>
    /// Review finding: removing the season tint from the grade left every check passing, because winter is also
    /// cooler through its warmth. These checks hold everything else equal and look at the tint alone.
    /// </summary>
    private void CheckSeasonTint(StylePreset preset)
    {
        // Solar noon: the same point of the day in every season, whatever the day length.
        float Noon(int day) { var (r, s) = LookClock.SunTimes(preset.Tuning.Sun, day); return (r + s) * 0.5f; }
        var winterMoment = LookClock.At(preset, Noon(15), 15);
        var summerMoment = LookClock.At(preset, Noon(196), 196);
        var winter = GradeParams.For(preset, winterMoment);
        var summer = GradeParams.For(preset, summerMoment);
        Check(winter.SeasonTint.IsEqualApprox(winterMoment.SeasonTint) && winterMoment.SeasonTint.IsEqualApprox(preset.SeasonGrades["winter"].Tint)
            && summerMoment.SeasonTint.IsEqualApprox(preset.SeasonGrades["summer"].Tint), "mid-season days carry their season's tint into the grade");
        var neutral = new Color(0.5f, 0.5f, 0.5f);
        var grey = new Color(0.45f, 0.45f, 0.45f);
        foreach (var (name, grade) in new[] { ("winter", winter), ("summer", summer), ("autumn", GradeParams.For(preset, LookClock.At(preset, Noon(288), 288))) })
        {
            var tinted = ColorGrade.Apply(grade, grey);
            var untinted = ColorGrade.Apply(grade with { SeasonTint = neutral }, grey);
            var shift = new Vector3(tinted.R - untinted.R, tinted.G - untinted.G, tinted.B - untinted.B);
            var tint = preset.SeasonGrades[name].Tint;
            var tintLuma = ColorGrade.Luma(tint);
            var chroma = new Vector3(tint.R - tintLuma, tint.G - tintLuma, tint.B - tintLuma);
            Check(shift.Length() > 0.006f && shift.Normalized().Dot(chroma.Normalized()) > 0.9f,
                $"the {name} tint alone shifts a mid grey toward the tint's hue (shift {shift.Length():0.###}, alignment {shift.Normalized().Dot(chroma.Normalized()):0.##})");
        }
        // The season tint lives in the mid-tones and lights: at noon (no night grading), a dark grey stays cool in every
        // season, and the tint moves it far less than it moves a mid grey.
        foreach (var (name, day) in new[] { ("winter", 15), ("spring", 105), ("summer", 196), ("autumn", 288) })
        {
            var grade = GradeParams.For(preset, LookClock.At(preset, Noon(day), day));
            var dark = new Color(0.12f, 0.12f, 0.12f);
            var darkShift = Shift(grade, dark, neutral);
            var midShift = Shift(grade, grey, neutral);
            var graded = ColorGrade.Apply(grade, dark);
            Check(graded.B >= graded.R - 0.01f && darkShift < 0.3f * midShift,
                $"{name} at noon: shade stays cool (blue minus red {graded.B - graded.R:0.###}) and the season tint barely touches it ({darkShift:0.####} against {midShift:0.####} in the mid-tones)");
        }
        var winterLut = ColorGrade.LutBytes(winter);
        var winterUntinted = ColorGrade.LutBytes(winter with { SeasonTint = neutral });
        var size = winter.Tuning.LutSize;
        var mid = ((size / 2 * size + size / 2) * size + size / 2) * 3;
        Check(winterLut[mid + 2] - winterLut[mid] > winterUntinted[mid + 2] - winterUntinted[mid], "the winter LUT's mid-grey texel is bluer with the season tint than without");
        var winterKey = winterMoment.KeyColor;
        var summerKey = summerMoment.KeyColor;
        Check(winterKey.B / winterKey.R > summerKey.B / summerKey.R && winterMoment.SeasonWarmth < summerMoment.SeasonWarmth,
            "at noon, winter sunlight is bluer and cooler than summer sunlight");
    }

    /// <summary>How far the season tint alone moves a colour: the grade with the tint against the grade with a neutral tint.</summary>
    private static float Shift(GradeParams grade, Color input, Color neutral)
    {
        var tinted = ColorGrade.Apply(grade, input);
        var untinted = ColorGrade.Apply(grade with { SeasonTint = neutral }, input);
        return new Vector3(tinted.R - untinted.R, tinted.G - untinted.G, tinted.B - untinted.B).Length();
    }

    // ---------- clock ----------

    private void CheckClock(StylePreset preset)
    {
        var seasons = preset.Tuning.Seasons;
        Check(LookClock.SeasonAt(279, "north", seasons) == "autumn" && LookClock.SeasonAt(279, "south", seasons) == "spring", "6 October is autumn in the north and spring in the south");
        Check(LookClock.SeasonAt(15, "north", seasons) == "winter" && LookClock.SeasonAt(196, "north", seasons) == "summer" && LookClock.SeasonAt(105, "north", seasons) == "spring",
            "mid-January, mid-April and mid-July land in winter, spring and summer");
        var (from, to, t) = LookClock.Blend(365, "north", seasons);
        Check(from == "autumn" && to == "winter" && t is > 0f and < 1f, "the year wraps from autumn into winter");
        var keys = preset.TimeKeys;
        Check(keys.All(k => LookClock.Sample(keys, k.Hour).KeyColor.IsEqualApprox(k.KeyColor) && Mathf.IsEqualApprox(LookClock.Sample(keys, k.Hour).KeyEnergy, k.KeyEnergy)),
            "sampling at a key's hour returns that key");
        var beforeMidnight = LookClock.Sample(keys, 23.999f);
        var afterMidnight = LookClock.Sample(keys, 0.001f);
        var ambientStep = Mathf.Max(Mathf.Abs(beforeMidnight.AmbientColor.R - afterMidnight.AmbientColor.R),
            Mathf.Max(Mathf.Abs(beforeMidnight.AmbientColor.G - afterMidnight.AmbientColor.G), Mathf.Abs(beforeMidnight.AmbientColor.B - afterMidnight.AmbientColor.B)));
        Check(Mathf.Abs(beforeMidnight.KeyEnergy - afterMidnight.KeyEnergy) < 0.01f && ambientStep < 0.01f,
            $"night interpolation is continuous across midnight (ambient step {ambientStep:0.#####})");
        // A preset with a fixed key (no sun) puts the key exactly where it says, at any hour, and reads the keys by the clock.
        var fixedKey = PresetWith(("\"x_look_key_mode\": \"sun\"", "\"x_look_key_mode\": \"fixed\""));
        var atDefault = LookClock.At(fixedKey, 9.25f, 279);
        Check(Mathf.IsEqualApprox(atDefault.KeyElevationDeg, preset.KeyElevationDeg, 0.01f) && Mathf.IsEqualApprox(atDefault.KeyAzimuthDeg, preset.KeyAzimuthDeg, 0.01f)
            && Mathf.IsEqualApprox(atDefault.KeyHour, 9.25f), $"a fixed key sits exactly where the preset puts it ({atDefault.KeyElevationDeg:0.##}, {atDefault.KeyAzimuthDeg:0.##})");
        var deepNight = LookClock.At(preset, 2f, 279);
        Check(deepNight.MoonWeight >= 0.999f && Mathf.IsEqualApprox(deepNight.KeyElevationDeg, preset.Tuning.Sun.MoonElevationDeg, 0.01f),
            "deep in the night the key is the moon at its preset elevation");
        var afternoon = LookClock.At(preset, 15f, 279);
        var lateNight = LookClock.At(preset, 23f, 279);
        Check(afternoon.Season == "autumn" && afternoon.Daylight > 0.6f && lateNight.Daylight < 0.2f && lateNight.KeyEnergy < afternoon.KeyEnergy * 0.3f,
            "the afternoon is bright and late night is dark");
        Check(LookClock.At(preset, 2f, 279).KeyEnergy < LookClock.At(preset, 21f, 279).KeyEnergy, "2 a.m. is darker than 9 p.m.");
    }

    /// <summary>
    /// Review finding: the key flipped direction at dusk and dawn while still near full energy, and 2 a.m. was
    /// brighter than 9 p.m. Sampled at every minute of four days, one per season: the light is continuous, it
    /// rises once from its darkest minute (in the night) to its brightest and falls once back, the key only swings
    /// fast while it is dim, it is pure sun whenever daylight is at least half, and it never comes from below the
    /// horizon. Ambient light obeys the same rules.
    /// </summary>
    private void CheckClockAtEveryMinute(StylePreset preset)
    {
        var sun = preset.Tuning.Sun;
        const int minutes = 24 * 60;
        const float bright = 0.3f;
        // The sun crosses the sky at 15 degrees an hour at most: a quarter of a degree a minute.
        const float sunPerMinute = 0.26f;
        foreach (var day in new[] { 15, 105, 196, 288 })
        {
            var moments = Enumerable.Range(0, minutes).Select(m => LookClock.At(preset, m / 60f, day)).ToArray();
            var energy = moments.Select(m => m.KeyEnergy).ToArray();
            var ambient = moments.Select(m => m.AmbientEnergy).ToArray();
            var biggestStep = Enumerable.Range(0, minutes).Max(m => Mathf.Abs(energy[m] - energy[(m + minutes - 1) % minutes]));
            var biggestAmbientStep = Enumerable.Range(0, minutes).Max(m => Mathf.Abs(ambient[m] - ambient[(m + minutes - 1) % minutes]));
            Check(biggestStep <= 0.02f * preset.KeyEnergy && biggestAmbientStep <= 0.01f,
                $"day {day}: key and ambient energy change smoothly, minute to minute (largest steps {biggestStep:0.####}, {biggestAmbientStep:0.####})");
            var perUpdate = biggestStep * (float)LookDirector.ClockUpdateSeconds / 60f;
            Check(perUpdate <= 0.01f * preset.KeyEnergy,
                $"day {day}: following the real clock, one {LookDirector.ClockUpdateSeconds:0} s update changes the key by under 1% ({perUpdate / preset.KeyEnergy:P2})");
            Check(Unimodal(energy, out var darkest, out var brightest) && Unimodal(ambient, out _, out _),
                $"day {day}: the key rises once from its darkest minute ({darkest / 60}:{darkest % 60:00}) to its brightest ({brightest / 60}:{brightest % 60:00}) and falls once back, and so does the ambient");
            Check(darkest >= 22 * 60 || darkest <= 5 * 60, $"day {day}: the darkest minute is in the night ({darkest / 60}:{darkest % 60:00})");
            Check(energy[2 * 60] < energy[21 * 60] && energy[3 * 60] < energy[20 * 60], $"day {day}: 2 a.m. is darker than 9 p.m. and 3 a.m. darker than 8 p.m.");
            var worstBright = 0f;
            var worstAny = 0f;
            var moonWhileBright = 0;
            var belowHorizon = 0;
            var offTheSun = 0f;
            for (var m = 0; m < minutes; m++)
            {
                var a = moments[(m + minutes - 1) % minutes];
                var b = moments[m];
                var step = Mathf.RadToDeg(LookClock.Direction(a.KeyElevationDeg, a.KeyAzimuthDeg).AngleTo(LookClock.Direction(b.KeyElevationDeg, b.KeyAzimuthDeg)));
                worstAny = Mathf.Max(worstAny, step);
                if (Mathf.Min(a.Daylight, b.Daylight) >= bright) worstBright = Mathf.Max(worstBright, step);
                if (b.Daylight >= 0.5f && b.MoonWeight > 0f) moonWhileBright++;
                if (b.KeyElevationDeg < sun.HorizonElevationDeg - 0.01f && b.MoonWeight <= 0f) belowHorizon++;
                // While the sun is up the key is the real sun: the solar model's direction in the room (-Z north).
                if (b.SunElevationDeg >= sun.HorizonElevationDeg)
                {
                    var real = LookClock.Direction(b.SunElevationDeg, LookClock.Yaw(sun, b.SunBearingDeg));
                    offTheSun = Mathf.Max(offTheSun, Mathf.RadToDeg(real.AngleTo(LookClock.Direction(b.KeyElevationDeg, b.KeyAzimuthDeg))));
                }
            }
            Check(offTheSun < 0.01f, $"day {day}: while the sun is up the key comes from the real sun's direction (worst {offTheSun:0.###} degrees off)");
            Check(worstBright <= sunPerMinute, $"day {day}: while daylight is at least {bright}, the key moves only as the sun does (at most {worstBright:0.###} degrees a minute)");
            Check(worstAny <= 10f, $"day {day}: the key never jumps; it swings to the moon only while dim (at most {worstAny:0.##} degrees a minute)");
            Check(moonWhileBright == 0, $"day {day}: whenever daylight is at least half the key is pure sun ({moonWhileBright} minutes otherwise)");
            Check(belowHorizon == 0 && moments.All(m => m.KeyElevationDeg >= Mathf.Min(sun.HorizonElevationDeg, sun.MoonElevationDeg) - 0.01f),
                $"day {day}: the key never shines from below the horizon");
        }
    }

    /// <summary>True when a daily curve rises (never falling) from its minimum to its maximum and falls (never rising) back, wrapping at midnight.</summary>
    private static bool Unimodal(float[] values, out int minimum, out int maximum)
    {
        var n = values.Length;
        var lo = 0;
        var hi = 0;
        for (var i = 1; i < n; i++)
        {
            if (values[i] < values[lo]) lo = i;
            if (values[i] > values[hi]) hi = i;
        }
        minimum = lo;
        maximum = hi;
        const float tolerance = 1e-5f;
        for (var i = lo; i != hi; i = (i + 1) % n)
            if (values[(i + 1) % n] < values[i] - tolerance) return false;
        for (var i = hi; i != lo; i = (i + 1) % n)
            if (values[(i + 1) % n] > values[i] + tolerance) return false;
        return true;
    }

    /// <summary>
    /// The founder's 6 October direction: time of day follows the real clock by default (seasons already follow the
    /// calendar), with a deterministic override for reviews and tests; and the swings are stronger: short winter
    /// days and long summer ones, darker nights, warm twilight pastels, and seasons that clearly differ.
    /// </summary>
    private void CheckRealClockAndSwings(StylePreset preset)
    {
        Check(preset.FollowClock && preset.FollowCalendar, "the preset follows the real clock and calendar by default");
        string? NoEnv(string name) => null;
        var both = LookClock.ClockOverride(new[] { "--look-clock=18:45", "--look-date=2026-01-15" }, NoEnv);
        Check(both.Hour is { } h && Mathf.IsEqualApprox(h, 18.75f) && both.DayOfYear == 15 && both.Error.Length == 0, "--look-clock and --look-date pin the hour and the day");
        var fromEnv = LookClock.ClockOverride(Array.Empty<string>(), name => name == "GARAGE_GAME_LOOK_CLOCK" ? "7:05" : null);
        Check(fromEnv.Hour is { } e && Mathf.IsEqualApprox(e, 7f + 5f / 60f) && fromEnv.DayOfYear == null, "GARAGE_GAME_LOOK_CLOCK pins the hour alone; the day still follows the calendar");
        var argsWin = LookClock.ClockOverride(new[] { "--look-clock=09:00" }, name => name == "GARAGE_GAME_LOOK_CLOCK" ? "21:00" : null);
        Check(argsWin.Hour is { } a && Mathf.IsEqualApprox(a, 9f), "the command line wins over the environment");
        var bad = LookClock.ClockOverride(new[] { "--look-clock=25:00", "--look-date=2026-13-01" }, NoEnv);
        Check(bad.Hour == null && bad.DayOfYear == null && bad.Error.Contains("25:00") && bad.Error.Contains("2026-13-01"), "an unreadable override is reported and ignored: " + bad.Error);
        var none = LookClock.ClockOverride(Array.Empty<string>(), NoEnv);
        Check(none.Hour == null && none.DayOfYear == null && none.Error.Length == 0, "without an override the look follows the preset");
        var lampsOff = LookClock.LampsOverride(new[] { "--look-lamps=off" }, name => name == "GARAGE_GAME_LOOK_LAMPS" ? "on" : null);
        var lampsEnv = LookClock.LampsOverride(Array.Empty<string>(), name => name == "GARAGE_GAME_LOOK_LAMPS" ? "On" : null);
        var lampsBad = LookClock.LampsOverride(new[] { "--look-lamps=dim" }, NoEnv);
        Check(lampsOff.LampsOn == false && lampsEnv.LampsOn == true && LookClock.LampsOverride(Array.Empty<string>(), NoEnv) is (null, "")
            && lampsBad.LampsOn == null && lampsBad.Error.Contains("dim"), "--look-lamps and GARAGE_GAME_LOOK_LAMPS pin the lamps on or off (the command line wins); a bad value is reported");

        // The real day maps onto the keys' reference day: a continuous, increasing map, sunrise to sunrise and sunset to sunset.
        var (rise, set) = LookClock.DayLimits(preset);
        foreach (var day in new[] { 15, 105, 196, 288 })
        {
            var previous = LookClock.KeyHour(preset, 0f, day);
            var worst = 0f;
            var backwards = 0;
            for (var m = 1; m <= 24 * 60; m++)
            {
                var current = LookClock.KeyHour(preset, m / 60f, day);
                var step = Mathf.PosMod(current - previous, 24f);
                worst = Mathf.Max(worst, step);
                if (step > 12f) backwards++;
                previous = current;
            }
            var (realRise, realSet) = LookClock.SunTimes(preset.Tuning.Sun, day);
            Check(backwards == 0 && worst < 0.1f && Mathf.IsEqualApprox(LookClock.KeyHour(preset, realRise, day), rise, 1e-3f)
                && Mathf.IsEqualApprox(LookClock.KeyHour(preset, realSet, day), set, 1e-3f),
                $"day {day}: the real day ({realSet - realRise:0.##} h, {realRise:0.##} to {realSet:0.##}) maps continuously onto the reference day, sunrise to sunrise and sunset to sunset");
        }
        // Daylight follows the solar model's day: the keys' daylight is up exactly from the real sunrise to the real sunset.
        foreach (var day in new[] { 15, 172, 355 })
        {
            var up = Enumerable.Range(0, 24 * 60).Count(m => LookClock.At(preset, m / 60f, day).Daylight >= preset.Tuning.Sun.ReferenceDaylight) / 60f;
            Check(Mathf.Abs(up - LookClock.DayLength(preset, day)) < 0.05f, $"day {day}: daylight lasts the solar model's day ({up:0.##} h against {LookClock.DayLength(preset, day):0.##} h)");
        }
        GD.Print($"LOOK_INFO: reference day {rise:0.##} to {set:0.##}; review moment 16:30 on 6 October reads the keys at {LookClock.KeyHour(preset, 16.5f, 279):0.##} with the sun at {LookClock.At(preset, 16.5f, 279).SunElevationDeg:0.#} degrees, bearing {LookClock.At(preset, 16.5f, 279).SunBearingDeg:0.#}");
        var winterEvening = LookClock.At(preset, 17.75f, 15);
        var summerEvening = LookClock.At(preset, 17.75f, 196);
        Check(winterEvening.Daylight < preset.Tuning.Sun.ReferenceDaylight && summerEvening.Daylight > 0.4f && summerEvening.SunElevationDeg > 10f,
            $"at 17:45 the winter sun has set and the summer sun is still well up (daylight {winterEvening.Daylight:0.##} against {summerEvening.Daylight:0.##})");

        // Nights at moonlight level: dark and blue, but not black (the founder: dark, but you can make out shapes).
        var keys = preset.TimeKeys;
        var peak = keys.Max(k => k.KeyEnergy);
        var deep = LookClock.Sample(keys, 2f);
        var midday = LookClock.Sample(keys, 13f);
        Check(deep.KeyEnergy >= 0.06f * peak && deep.KeyEnergy <= 0.1f * peak && deep.KeyEnergy / peak < preset.Tuning.Sun.ReferenceDaylight
            && deep.AmbientEnergy <= 0.3f * midday.AmbientEnergy && deep.AmbientColor.B > deep.AmbientColor.R + 0.1f,
            $"nights are moonlit and blue (moonlight {deep.KeyEnergy / peak:P0} of the sun's peak, ambient {deep.AmbientEnergy / midday.AmbientEnergy:P0} of midday's)");
        // The evening moment the key falls to half its peak: the sunset.
        var sunsetHour = Enumerable.Range(0, 12 * 12).Select(i => preset.DefaultHour + i / 12f).First(h => LookClock.Sample(keys, h).KeyEnergy <= 0.5f * peak);
        var dusk = LookClock.Sample(keys, sunsetHour);
        Check(dusk.KeyColor.R > 1.5f * dusk.KeyColor.B && dusk.AmbientColor.B > dusk.AmbientColor.R + 0.1f,
            $"the sunset key (at {sunsetHour:0.##} on the reference day) is orange-gold under a light-blue sky: the twilight pastels");
        var grey = new Color(0.45f, 0.45f, 0.45f);
        var winterGrey = ColorGrade.Apply(GradeParams.For(preset, LookClock.At(preset, 13f, 15)), grey);
        var summerGrey = ColorGrade.Apply(GradeParams.For(preset, LookClock.At(preset, 13f, 196)), grey);
        var autumnGrey = ColorGrade.Apply(GradeParams.For(preset, LookClock.At(preset, 13f, 288)), grey);
        Check((winterGrey.B - winterGrey.R) - (summerGrey.B - summerGrey.R) >= 0.02f && (winterGrey.B - winterGrey.R) - (autumnGrey.B - autumnGrey.R) >= 0.02f,
            $"the seasons still differ in the grade, gently: winter cooler than summer and autumn (blue minus red: winter {winterGrey.B - winterGrey.R:0.###}, summer {summerGrey.B - summerGrey.R:0.###}, autumn {autumnGrey.B - autumnGrey.R:0.###})");
        // Twilight is not night: the golden hour keeps its warm colour, and the night look comes in only as the light goes.
        var golden = LookClock.At(preset, 16.5f, 279);
        var deepNight = LookClock.At(preset, 2f, 279);
        Check(golden.Daylight >= preset.Tuning.Grade.NightNoneAbove && GradeParams.For(preset, golden).Night == 0f && GradeParams.For(preset, deepNight).Night >= 0.95f,
            $"the golden hour (daylight {golden.Daylight:0.##}) gets none of the night grade and deep night all of it");
        var sweep = Enumerable.Range(0, 24 * 60).Select(m => GradeParams.For(preset, LookClock.At(preset, m / 60f, 15)).Night).ToArray();
        var nightStep = Enumerable.Range(1, sweep.Length - 1).Max(i => Mathf.Abs(sweep[i] - sweep[i - 1]));
        Check(nightStep < 0.05f, $"the night grade comes and goes smoothly (largest step {nightStep:0.###} a minute in mid-winter)");
    }

    /// <summary>
    /// The review cameras: the five baseline cameras are unchanged, every camera stands inside the room and looks at
    /// its focus, and the high-angle art-direction cameras read as a tilt-shift miniature: both avatars inside the
    /// crisp band, the floor at the bottom of the frame nearer than the band and the top of the frame farther.
    /// </summary>
    private void CheckReviewCameras(StylePreset preset, RoomData room)
    {
        var path = System.IO.Path.GetFullPath(ProjectSettings.GlobalizePath("res://") + "../tools/look/review_cameras.json");
        using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
        var root = document.RootElement;
        var cameras = root.GetProperty("cameras").EnumerateArray().ToArray();
        var ids = cameras.Select(c => c.GetProperty("id").GetString()).ToArray();
        Check(ids.Take(7).SequenceEqual(new[] { "player_eye", "over_shoulder", "companion", "low_corner", "ceiling_corner", "diorama_high", "iso_room" }) && ids.Skip(7).SequenceEqual(new[] { "window_view", "observe_view", "observe_f3", "observe_f4" })
            && ids.Distinct().Count() == ids.Length, "the five baseline review cameras come first, then the two art-direction cameras, the window and observe views and the two observe rigs, in order, and ids are unique: " + string.Join(",", ids));
        // The two observe rigs are the real HUD's F3 and F4 views (placed by the HUD, not by a pose in this file) with the observe switch on.
        var rigs = cameras.Where(c => c.TryGetProperty("hud_view", out _)).ToArray();
        Check(rigs.Select(c => c.GetProperty("id").GetString()).SequenceEqual(new[] { "observe_f3", "observe_f4" })
            && rigs.Select(c => c.GetProperty("hud_view").GetInt32()).SequenceEqual(new[] { 2, 3 }) && rigs.All(c => c.TryGetProperty("observe", out var o) && o.GetBoolean()),
            "observe_f3 and observe_f4 mirror the HUD's F3 and F4 views with the observe switch on");
        cameras = cameras.Where(c => !c.TryGetProperty("hud_view", out _)).ToArray();
        var baseline = new Dictionary<string, (Vector3 Position, Vector3 LookAt, float Fov)>
        {
            ["player_eye"] = (new Vector3(0f, 0.087f, 0.6f), new Vector3(-0.25f, 0.14f, -0.6f), 70f),
            ["over_shoulder"] = (new Vector3(-0.15f, 0.22f, 1.12f), new Vector3(0.12f, 0.06f, 0.1f), 68f),
            // Reframed in the 7 October fix round for the 10 cm companion (the old camera was placed for a 0.24 m one).
            ["companion"] = (new Vector3(0.62f, 0.095f, 0.2f), new Vector3(0.2f, 0.07f, 0.6f), 36f),
            ["low_corner"] = (new Vector3(1.84f, 0.05f, -1.34f), new Vector3(-0.2f, 0.5f, 0.8f), 65f),
            ["ceiling_corner"] = (new Vector3(-1.84f, 2.26f, 1.36f), new Vector3(0.15f, 0.05f, 0.25f), 60f),
        };
        Vector3 V(System.Text.Json.JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
        var clock = root.GetProperty("clock");
        Check(root.GetProperty("resolution")[0].GetInt32() == 1920 && root.GetProperty("resolution")[1].GetInt32() == 1080
            && Mathf.IsEqualApprox(clock.GetProperty("hour").GetSingle(), 17f) && clock.GetProperty("date").GetString() == "2026-04-15",
            "reviews stay at 1920 x 1080, pinned to 17:00 on 15 April (the low sun reaches the avatars)");
        var inside = room.Bounds.Grow(-0.02f);
        foreach (var camera in cameras)
        {
            var id = camera.GetProperty("id").GetString()!;
            var position = V(camera.GetProperty("position_m"));
            var lookAt = V(camera.GetProperty("look_at_m"));
            var focus = V(camera.GetProperty("focus_m"));
            var fov = camera.GetProperty("fov_deg").GetSingle();
            if (baseline.TryGetValue(id, out var original))
                Check(position.IsEqualApprox(original.Position) && lookAt.IsEqualApprox(original.LookAt) && Mathf.IsEqualApprox(fov, original.Fov), $"{id} is as recorded (only the companion camera was reframed)");
            var forward = (lookAt - position).Normalized();
            Check(inside.HasPoint(position) && (focus - position).Dot(forward) > 0.05f && fov is >= 20f and <= 90f, $"{id} stands inside the room and looks at its focus");
        }
        var player = room.SpawnFor("player").PositionM;
        var companion = room.SpawnFor("companion", room.SpawnFor("player")).PositionM;
        // The companion camera frames both 10 cm bodies, each a fifth of the frame or more.
        {
            var camera = cameras.First(c => c.GetProperty("id").GetString() == "companion");
            var position = V(camera.GetProperty("position_m"));
            var forward = (V(camera.GetProperty("look_at_m")) - position).Normalized();
            var verticalFov = Mathf.DegToRad(camera.GetProperty("fov_deg").GetSingle());
            var halfWidth = Mathf.Tan(verticalFov / 2f) * 16f / 9f;
            var bodies = new[] { player, companion };
            var right = forward.Cross(Vector3.Up).Normalized();
            var heights = bodies.Select(b => 0.1f / (b - position).Dot(forward) / (2f * Mathf.Tan(verticalFov / 2f))).ToArray();
            var across = bodies.Select(b => Mathf.Abs((b - position).Dot(right)) / (b - position).Dot(forward) / halfWidth).ToArray();
            Check(bodies.All(b => (b - position).Dot(forward) > 0.2f) && across.All(x => x < 0.85f) && heights.All(h => h >= 0.2f && h <= 0.6f),
                $"the companion camera frames both 10 cm avatars, each {heights[0]:P0} and {heights[1]:P0} of the frame's height, at {across[0]:P0} and {across[1]:P0} of the way to the frame's edge");
        }
        // At the review clock the sun reaches both avatars through the west window (the old moment never did).
        {
            using var roomJson = System.Text.Json.JsonDocument.Parse(FileAccess.GetFileAsBytes(RoomWorld.DefaultRoom + "/room.json"));
            var window = roomJson.RootElement.GetProperty("shell").GetProperty("openings").EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "window");
            var centre = V(window.GetProperty("center_m"));
            var size = window.GetProperty("size_m");
            var reviewDay = DateTime.ParseExact(clock.GetProperty("date").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).DayOfYear;
            var moment = LookClock.At(preset, clock.GetProperty("hour").GetSingle(), reviewDay);
            var toSun = LookClock.Direction(moment.KeyElevationDeg, moment.KeyAzimuthDeg);
            var through = new[] { player, companion }.Select(b =>
            {
                var point = b + Vector3.Up * 0.05f;
                var t = (centre.X - point.X) / toSun.X;
                var at = point + toSun * t;
                return toSun.X < -0.1f && Mathf.Abs(at.Y - centre.Y) < size[1].GetSingle() * 0.5f - 0.03f && Mathf.Abs(at.Z - centre.Z) < size[0].GetSingle() * 0.5f - 0.03f;
            }).ToArray();
            Check(through.All(x => x) && moment.SunElevationDeg is > 10f and < 30f, $"at the review moment the low sun ({moment.SunElevationDeg:0} degrees up, bearing {moment.SunBearingDeg:0}) comes through the window onto both avatars");
        }
        {
            var window = cameras.First(c => c.GetProperty("id").GetString() == "window_view");
            var observeView = cameras.First(c => c.GetProperty("id").GetString() == "observe_view");
            var position = V(window.GetProperty("position_m"));
            var forward = (V(window.GetProperty("look_at_m")) - position).Normalized();
            var halfFov = Mathf.DegToRad(window.GetProperty("fov_deg").GetSingle()) * 0.5f;
            var pane = new Vector3(-2f, 1.25f, 0.3f);
            Check((pane - position).Normalized().AngleTo(forward) < halfFov * 0.7f && new[] { player, companion }.All(b => (b + Vector3.Up * 0.05f - position).Normalized().AngleTo(forward) < halfFov),
                "the window view has the window pane in frame and both avatars in front of it");
            var observePosition = V(observeView.GetProperty("position_m"));
            var observeForward = (V(observeView.GetProperty("look_at_m")) - observePosition).Normalized();
            var focusDistance = (V(observeView.GetProperty("focus_m")) - observePosition).Dot(observeForward);
            var tight = LookDirector.DepthOfFieldFor(preset, focusDistance, -observeForward.Y, observe: true);
            float DepthOf(Vector3 p) => (p - observePosition).Dot(observeForward);
            Check(observeView.TryGetProperty("observe", out var flag) && flag.GetBoolean() && DepthOf(player + Vector3.Up * 0.05f) > tight.NearDistance - 0.04f && DepthOf(player + Vector3.Up * 0.05f) < tight.FarDistance + 0.04f,
                $"the observe view asks for the observe profile and its focus sits on the avatars (band {tight.NearDistance:0.###} to {tight.FarDistance:0.###} m)");
        }
        foreach (var camera in cameras.Where(c => c.GetProperty("id").GetString() is "diorama_high" or "iso_room"))
        {
            var id = camera.GetProperty("id").GetString()!;
            var position = V(camera.GetProperty("position_m"));
            var forward = (V(camera.GetProperty("look_at_m")) - position).Normalized();
            var halfFov = Mathf.DegToRad(camera.GetProperty("fov_deg").GetSingle()) * 0.5f;
            var pitch = Mathf.Asin(-forward.Y);
            var focusDistance = (V(camera.GetProperty("focus_m")) - position).Dot(forward);
            var dof = LookDirector.DepthOfFieldFor(preset, focusDistance, -forward.Y);
            float Depth(Vector3 point) => (point - position).Dot(forward);
            var avatars = new[] { player + Vector3.Up * 0.05f, companion + Vector3.Up * 0.05f };
            var framed = avatars.All(a => (a - position).Normalized().AngleTo(forward) < halfFov * 0.8f);
            var crisp = avatars.All(a => Depth(a) > dof.NearDistance && Depth(a) < dof.FarDistance);
            // The centre column's top and bottom rays: where they meet the floor or the room's walls.
            var right = forward.Cross(Vector3.Up).Normalized();
            var (bottom, top) = forward.Rotated(right, halfFov).Y < forward.Rotated(right, -halfFov).Y
                ? (forward.Rotated(right, halfFov), forward.Rotated(right, -halfFov))
                : (forward.Rotated(right, -halfFov), forward.Rotated(right, halfFov));
            float Hit(Vector3 ray) => RayToRoom(room.Bounds, position, ray) * ray.Dot(forward);
            var bottomDepth = Hit(bottom);
            var topDepth = Hit(top);
            GD.Print($"LOOK_INFO: {id} pitch {Mathf.RadToDeg(pitch):0} degrees; crisp {dof.NearDistance:0.##} to {dof.FarDistance:0.##} m (focus {focusDistance:0.##} m, blur {dof.Amount:0.###}); avatars at {Depth(avatars[0]):0.##} and {Depth(avatars[1]):0.##} m; frame foot {bottomDepth:0.##} m, top {topDepth:0.##} m");
            Check(Mathf.RadToDeg(pitch) >= 30f && framed && crisp && bottomDepth < dof.NearDistance && topDepth > dof.FarDistance + 0.5f * dof.FarTransition,
                $"{id} looks down {Mathf.RadToDeg(pitch):0} degrees with both avatars framed and crisp ({dof.NearDistance:0.##} to {dof.FarDistance:0.##} m), the frame's foot nearer than the band ({bottomDepth:0.##} m) and its top deep in the far blur ({topDepth:0.##} m)");
        }
    }

    /// <summary>Distance along a ray from inside the room to the room's bounds.</summary>
    private static float RayToRoom(Aabb bounds, Vector3 origin, Vector3 direction)
    {
        var best = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            if (Mathf.Abs(direction[axis]) < 1e-6f) continue;
            var plane = direction[axis] > 0f ? bounds.End[axis] : bounds.Position[axis];
            best = Mathf.Min(best, (plane - origin[axis]) / direction[axis]);
        }
        return best;
    }

    // ---------- depth of field ----------

    private void CheckDepthOfField(StylePreset preset)
    {
        const float body = 0.10f;
        var eyeFocus = preset.Tuning.Dof.EyeFocusBodyHeights * body;
        var eye = LookDirector.DepthOfFieldFor(preset, eyeFocus, 0f, preset.Tuning.Dof.EyeCrispBodyHeights * body);
        Check(eye.NearEnabled && eye.NearDistance <= 0.15f && eye.FarEnabled && eye.FarDistance >= 0.45f,
            $"from a 10 cm body's eye the 15 cm reach is crisp and the far room softens (near {eye.NearDistance:0.###}, far {eye.FarDistance:0.###})");
        var high = LookDirector.DepthOfFieldFor(preset, 2.9f, 0.7f);
        var level = LookDirector.DepthOfFieldFor(preset, 2.9f, 0f);
        Check(high.Amount > level.Amount && high.FarDistance < level.FarDistance && high.NearDistance > level.NearDistance,
            "looking down on the room narrows the band and strengthens the blur (tilt-shift)");
        Check(high.NearDistance < 2.9f && high.FarDistance > 2.9f, "the focus distance itself is always inside the band");
        var near = LookDirector.DepthOfFieldFor(preset, 0.6f, 0f);
        var far = LookDirector.DepthOfFieldFor(preset, 3f, 0f);
        Check(far.FarTransition > near.FarTransition, "the far blur ramps over a longer distance when the focus is farther");
        foreach (var distance in new[] { 0.3f, 0.6f, 1.1f, 2.0f, 2.9f })
            foreach (var down in new[] { 0f, 0.4f, 0.8f })
            {
                var dof = LookDirector.DepthOfFieldFor(preset, distance, down);
                if (!(dof.NearDistance < distance && dof.FarDistance > distance && dof.FarTransition > 0f && dof.NearTransition > 0f && dof.Amount is > 0f and <= 1f))
                    Check(false, $"depth of field is well formed at {distance} m looking down {down}: {dof}");
            }
    }

    /// <summary>Observe v2 must fit a full 10 cm body, rather than merely its focus point.</summary>
    private void CheckObservePlayerEnvelope(StylePreset preset)
    {
        Check(preset.PresetVersion == 2 && Mathf.IsEqualApprox(preset.Tuning.Dof.ObserveBandM, 0.14f),
            "the new Observe fixture reads v2 with its 14 cm band");
        foreach (var distance in new[] { 0.30f, 1.4f, 2.4f, 2.7f })
            foreach (var pitch in Enumerable.Range(20, 61).Select(p => (float)p).Append(RoomHud.IsoPitchDeg))
            {
                var angle = Mathf.DegToRad(pitch);
                // Conservative support of the height/radius envelope, plus visual seating.
                var extent = 0.05f * Mathf.Sin(angle) + 0.02f * Mathf.Cos(angle) + SmallPlayerController.MaxSeatGapM;
                var dof = LookDirector.DepthOfFieldFor(preset, distance, Mathf.Sin(angle), observe: true);
                Check(dof.NearDistance < distance - extent && dof.FarDistance > distance + extent,
                    $"Observe contains the whole body at depth {distance}, pitch {pitch}");
                Check(Mathf.IsEqualApprox(dof.FarDistance - dof.NearDistance, 0.14f)
                    && Mathf.IsEqualApprox(dof.NearTransition, 0.08f)
                    && Mathf.IsEqualApprox(dof.FarTransition, 0.10f)
                    && Mathf.IsEqualApprox(dof.Amount, preset.Tuning.Dof.ObserveAmount), "Observe retains its tight width, ramps and the preset's amount");
            }
        var close = LookDirector.DepthOfFieldFor(preset, 0.05f, 0.7f, observe: true);
        Check(!close.NearEnabled && close.NearDistance <= 0.05f && close.FarDistance > 0.05f,
            "a close collision-shortened view does not put the near edge past its own focus");
    }

    /// <summary>
    /// The observe view's frame budget (Run 2). Godot's circular bokeh gathers about (64 x amount)^2 / (2 x blur_scale) samples at
    /// every half-resolution pixel whatever the frame holds (bokeh_dof.glsl; blur_scale 1.0 at the project's bokeh quality 2), so
    /// what the observe profile costs over the ordinary tilt-shift follows its blur amount squared, not its band or its ramps.
    /// Run 1's captures (the founder's RTX 2070 SUPER, 17:00 on 15 April): diorama_high, amount 0.165, 10.07 ms of GPU time;
    /// observe_view, amount 0.3, 11.98 ms of GPU time and 17.34 ms at p95. That is 30.4 ms per unit of amount squared; v2 must put
    /// the observe view's p95 on those numbers at least 0.5 ms inside the preset's budget. Rendering confirms it (docs/look/reviews/run2).
    /// </summary>
    private void CheckObserveFrameBudget(StylePreset v1, StylePreset v2)
    {
        const float RunOneP95 = 17.34f, RunOneAmount = 0.3f;
        const float MsPerAmountSquared = (11.98f - 10.07f) / (0.3f * 0.3f - 0.165f * 0.165f);
        float P95At(float amount) => RunOneP95 - MsPerAmountSquared * (RunOneAmount * RunOneAmount - amount * amount);
        var v1Amount = v1.Tuning.Dof.ObserveAmount;
        Check(Mathf.IsEqualApprox(v1Amount, RunOneAmount) && P95At(v1Amount) > v1.TargetFrameMs, $"the model reproduces v1's over-budget observe view ({P95At(v1Amount):0.0} ms at amount {v1Amount})");
        var amount = v2.Tuning.Dof.ObserveAmount;
        Check(P95At(amount) <= v2.TargetFrameMs - 0.5f, $"v2's observe amount {amount} puts the observe view's modelled p95 at {P95At(amount):0.0} ms, at least 0.5 ms inside the {v2.TargetFrameMs} ms budget");
        var ordinary = LookDirector.DepthOfFieldFor(v2, 1.4f, Mathf.Sin(Mathf.DegToRad(45f)));
        Check(amount >= ordinary.Amount, $"and the observe view still blurs at least as hard as the ordinary tilt-shift ({amount} against {ordinary.Amount:0.###})");
    }

    /// <summary>One camera changes F3/F4 poses; a shared pivot override must not steal the player focus.</summary>
    private async Task CheckObservePlayerTracking(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "ObserveV2Holder");
        look.SetProcess(false);
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        look.FocusTarget = player;
        look.Observe = true;
        var camera = new Camera3D { Near = 0.01f };
        holder.AddChild(camera);
        camera.MakeCurrent();
        foreach (var (distance, pitch) in new[] { (1.4f, 45f), (2.7f, RoomHud.IsoPitchDeg), (0.30f, 80f), (2.4f, 20f) })
        {
            player.GlobalPosition += new Vector3(0.3f, 0.02f, -0.4f);
            var middle = player.GetGlobalTransformInterpolated().Origin + Vector3.Up * 0.05f;
            var forward = new Vector3(0f, -Mathf.Sin(Mathf.DegToRad(pitch)), -Mathf.Cos(Mathf.DegToRad(pitch)));
            var sharedPivot = middle + new Vector3(0f, 0f, -0.2f);
            camera.GlobalPosition = sharedPivot - forward * distance;
            camera.LookAt(sharedPivot);
            look.FocusOverride = sharedPivot;
            Check(look.FocusPointFor(camera).IsEqualApprox(middle), "Observe chooses rendered player middle before shared pivot");
            look._Process(1.0 / 60.0);
            var depth = Distance(camera, middle);
            Check(InFocus(camera, middle, out var band)
                && Mathf.Abs((band.Near + band.Far) * 0.5f - depth) < 1e-4f,
                "Observe follows the new player/camera depth in the same frame, including a view swap");
            foreach (var x in new[] { -0.02f, 0.02f })
                foreach (var y in new[] { -SmallPlayerController.MaxSeatGapM, 0.10f })
                    foreach (var z in new[] { -0.02f, 0.02f })
                        Check(InFocus(camera, player.GetGlobalTransformInterpolated().Origin + new Vector3(x, y, z), out _),
                            "Observe contains a conservative body-box corner including visual seating");
            var attributes = (CameraAttributesPractical)camera.Attributes!;
            Check(look.Post!.Focus is { } postBand && Mathf.Abs(postBand.NearDistance - band.Near) < 1e-4f
                && Mathf.Abs(postBand.FarDistance - band.Far) < 1e-4f,
                "post and decoded optical sharp edges agree");
            Check(Mathf.Abs(attributes.DofBlurAmount - preset.Tuning.Dof.ObserveAmount) < 1e-4f, "Observe keeps the prescribed blur amount");
        }
        look.Observe = false;
        Check(look.FocusPointFor(camera).IsEqualApprox(look.FocusOverride!.Value), "ordinary building focus still honours its override");
        look.FocusOverride = null;
        Check(look.FocusPointFor(player.EyeCamera).IsEqualApprox(player.EyeCamera.GlobalPosition
            - player.EyeCamera.GlobalBasis.Z * (preset.Tuning.Dof.EyeFocusBodyHeights * player.BodyHeightM)),
            "the eye camera keeps its existing focus rule");
        holder.QueueFree();
        await Frames(1);
    }

    // ---------- post effect ----------

    /// <summary>
    /// Review finding: removing the grain and vignette effect left every check passing. Headless there is no GPU, so
    /// these checks pin what the effect sends and does: the push constants match the compute shader's Params block
    /// name for name, carry the preset's numbers, and, run through a CPU mirror of the shader's arithmetic, darken the
    /// corners and add a grain of the preset's size; the shader applies both to the colour. The capture harness checks
    /// the rendered pixels on the GPU (post_effect_check in timings.json).
    /// </summary>
    private void CheckPostEffectParameters(StylePreset preset)
    {
        var block = Regex.Match(LookPostEffect.ComputeSource, @"uniform\s+Params\s*\{(?<fields>.*?)\}\s*params;", RegexOptions.Singleline);
        var declared = new List<string>();
        foreach (Match field in Regex.Matches(block.Groups["fields"].Value, @"(vec2|float)\s+(\w+);"))
        {
            if (field.Groups[1].Value == "vec2") { declared.Add(field.Groups[2].Value + ".x"); declared.Add(field.Groups[2].Value + ".y"); }
            else declared.Add(field.Groups[2].Value);
        }
        Check(block.Success && declared.SequenceEqual(LookPostEffect.PushConstantNames) && declared.Count * 4 % 16 == 0,
            "the effect's push constants match the compute shader's Params block, name for name, in a 16-byte multiple: " + string.Join(",", declared));
        var effect = new LookPostEffect { Grain = preset.Grain, Vignette = preset.Vignette, Tuning = preset.Tuning.Post };
        var size = new Vector2I(1920, 1080);
        var constants = effect.PushConstants(size);
        float P(string name) => constants[Array.IndexOf(LookPostEffect.PushConstantNames, name)];
        var post = preset.Tuning.Post;
        Check(constants.Length == LookPostEffect.PushConstantNames.Length && P("size.x") == 1920f && P("size.y") == 1080f && P("grain") == preset.Grain
            && P("vignette") == preset.Vignette && P("vignette_start") == post.VignetteStart && P("vignette_end") == post.VignetteEnd
            && P("grain_fine") == post.GrainFine && P("grain_soft") == post.GrainSoft && P("grain_soft_px") == post.GrainSoftPx,
            "the effect pushes the preset's grain, vignette and their shape");
        var centre = Patch(constants, new Vector2I(size.X / 2 - 32, size.Y / 2 - 32));
        var corner = Patch(constants, Vector2I.Zero);
        var expectedSpread = preset.Grain * Mathf.Sqrt((post.GrainFine * post.GrainFine + post.GrainSoft * post.GrainSoft) / 12f);
        Check(Mathf.Abs(centre.Mean - 1f) < 0.01f && corner.Mean < centre.Mean - 0.6f * preset.Vignette,
            $"the vignette darkens the corners and leaves the centre (centre {centre.Mean:0.###}, corner {corner.Mean:0.###})");
        Check(centre.Spread > 0.5f * expectedSpread && centre.Spread < 2f * expectedSpread,
            $"the grain varies pixel to pixel by about the preset's amount (spread {centre.Spread:0.####}, expected about {expectedSpread:0.####})");
        Check(Regex.IsMatch(LookPostEffect.ComputeSource, @"color\.rgb\s*\*=\s*vignette\s*\*\s*grain\s*;") && LookPostEffect.ComputeSource.Contains("imageStore(color_image, pixel, color)"),
            "the compute shader multiplies the colour by the vignette and the grain and writes it back");
        Check(effect.EffectCallbackType == CompositorEffect.EffectCallbackTypeEnum.PostTransparent && effect.Enabled, "the effect runs after transparent geometry, before tone mapping");
    }

    private static (float Mean, float Spread) Patch(float[] constants, Vector2I origin)
    {
        var values = new List<float>();
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                values.Add(LookPostEffect.Factor(constants, origin + new Vector2I(x, y)));
        var mean = values.Average();
        return (mean, Mathf.Sqrt(values.Average(v => (v - mean) * (v - mean))));
    }

    // ---------- VoxelGI bake stand-ins ----------

    /// <summary>
    /// Review finding: the bake's flat stand-in materials could stay on the room and every check still passed. The
    /// swap is exercised here without a GPU: during the bake every painterly mesh wears a flat stand-in of its bake
    /// colour, and afterwards every original material is back, also when the bake throws.
    /// </summary>
    private void CheckBakeStandIns(StylePreset preset, RoomData room)
    {
        MaterialLibrary.Configure(preset);
        var built = RoomBuilder.Build(room);
        var meshes = built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToArray();
        var originals = meshes.ToDictionary(m => m, m => m.MaterialOverride);
        var painterly = meshes.Count(m => MaterialLibrary.BakeAlbedo(m.MaterialOverride) != null);
        var swapped = 0;
        var flat = 0;
        LookDirector.WithBakeStandIns(meshes, () =>
        {
            foreach (var mesh in meshes)
            {
                if (mesh.MaterialOverride is not StandardMaterial3D standIn || !standIn.HasMeta(LookDirector.BakeStandInMeta)) continue;
                swapped++;
                if (MaterialLibrary.BakeAlbedo(originals[mesh]) is { } albedo && standIn.AlbedoColor.IsEqualApprox(albedo)) flat++;
            }
        });
        Check(painterly == meshes.Length && painterly == room.Shell.Count + room.Objects.Count && swapped == painterly && flat == painterly,
            $"during a bake every painterly mesh wears a flat stand-in of its bake colour ({swapped}/{painterly})");
        Check(meshes.All(m => ReferenceEquals(m.MaterialOverride, originals[m])), "after a bake every original material is back");
        var threw = false;
        try { LookDirector.WithBakeStandIns(meshes, () => throw new InvalidOperationException("bake failed")); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && meshes.All(m => ReferenceEquals(m.MaterialOverride, originals[m])), "when the bake throws, the error surfaces and every original material is still restored");
        built.Free();
    }

    // ---------- renderer fallback ----------

    /// <summary>Review finding: a renderer fallback went unreported. The director now says so to the log, the review harness and the player.</summary>
    private void CheckRendererFallback(StylePreset preset, RoomData room)
    {
        Check(LookDirector.RendererNoteFor("p", "forward_plus", "forward_plus").Length == 0, "no note when the machine renders with the preset's renderer");
        var note = LookDirector.RendererNoteFor("p", "forward_plus", "gl_compatibility");
        Check(note.Contains("gl_compatibility") && note.Contains("flatter"), "a fallback to Compatibility says what the room loses: " + note);
        Check(RenderingServer.GetCurrentRenderingMethod() == "forward_plus", $"this test runs the Forward+ rendering method ({RenderingServer.GetCurrentRenderingMethod()})");
        // A preset designed for another renderer than the one running: the director reports it everywhere.
        var other = PresetWith(("\"method\": \"forward_plus\"", "\"method\": \"mobile\""));
        var holder = new Node3D { Name = "FallbackHolder" };
        AddChild(holder);
        var look = new LookDirector { Name = "Look" };
        holder.AddChild(look);
        look.Apply(other, room);
        Check(look.RendererNote.Contains("mobile") && look.Warnings.Contains(look.RendererNote) && look.PlayerNotice.Contains(look.RendererNote)
            && look.SelfCheck().Contains(look.RendererNote), "a renderer mismatch reaches the warnings, the self-check and the player's notice");
        holder.Free();
        MaterialLibrary.Configure(preset);
    }

    // ---------- the look director ----------

    private async Task CheckDirector(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "DirectorHolder");
        look.SetClock(preset.DefaultHour, 279);
        var built = RoomBuilder.Build(room);
        holder.AddChild(built);
        await Frames(2);
        Check(look.Dressed && look.Bakes == 1, "the look dresses the built room after it is added, baking once");
        var environment = look.GetChildren().OfType<WorldEnvironment>().Single();
        Check(environment.Environment.SsaoEnabled && environment.Environment.GlowEnabled && environment.Environment.AdjustmentEnabled
            && environment.Environment.AdjustmentColorCorrection is ImageTexture3D, "the environment has SSAO, glow and the grade LUT");
        Check(Mathf.IsEqualApprox(environment.Environment.SsaoPower, preset.Tuning.Ssao.Power) && Mathf.IsEqualApprox(environment.Environment.GlowHdrThreshold, preset.Tuning.Glow.HdrThreshold),
            "SSAO and glow shape come from the preset");
        Check(environment.Compositor?.CompositorEffects.Count == 1 && look.Post != null && environment.Compositor.CompositorEffects[0] == look.Post
            && Mathf.IsEqualApprox(look.Post.Grain, preset.Grain) && Mathf.IsEqualApprox(look.Post.Vignette, preset.Vignette) && look.Post.Tuning == preset.Tuning.Post,
            "grain and vignette come from the preset through the environment's compositor effect");
        var hintLights = look.GetChildren().OfType<Light3D>().Where(l => l.HasMeta("light_hint_id")).ToArray();
        Check(hintLights.Length == look.RoomLightCount && hintLights.All(l => room.LightHints.Any(h => h.Id == l.GetMeta("light_hint_id").AsString()))
            && hintLights.OfType<OmniLight3D>().Any(l => l.GetMeta("light_hint_id").AsString() == "ceiling_lamp")
            && hintLights.OfType<SpotLight3D>().Any(l => l.GetMeta("light_hint_id").AsString() == "window_west"),
            $"the room's light hints are lit ({look.RoomLightCount}: the ceiling lamp and the west window's sky fill)");
        Check(look.Key.ShadowEnabled && look.SkyFills.All(f => f.ShadowEnabled) && look.Lamps.All(l => l.ShadowEnabled)
            && look.GetChildren().OfType<Light3D>().Count(l => l.ShadowEnabled) <= preset.MaxShadowedLights && preset.MaxShadowedLights <= 3,
            $"the sun, the window's sky fill and the lamp cast shadows, within the preset's {preset.MaxShadowedLights} shadowed lights");
        // Round 2 finding: with the sun's shadow softened by PCSS at 2.4 degrees, the 10 cm avatars and the props cast no
        // visible sun shadow at all (the window frame's still did). The sun's shadow is filtered instead, never softened
        // past the sun's own size, and no light's bias is large enough to push a 10 cm body's shadow off its base.
        Check(look.Key.LightAngularDistance <= 0.6f && look.Key.ShadowBias <= 0.05f && look.Key.ShadowNormalBias <= 1f
            && look.SkyFills.Concat(look.Lamps).All(l => l.ShadowBias <= 0.05f && l.ShadowNormalBias <= 1f),
            $"shadows keep 10 cm bodies grounded: the sun's is not softened past the sun's size ({look.Key.LightAngularDistance:0.##} degrees) and no bias pushes a shadow off its base");
        var lamp = hintLights.OfType<OmniLight3D>().First(l => l.GetMeta("light_hint_id").AsString() == "ceiling_lamp");
        Check(Mathf.IsEqualApprox(lamp.OmniRange, room.Bounds.Size.Length() * preset.Tuning.Lamps.RangePerDiagonal) && Mathf.IsEqualApprox(lamp.OmniAttenuation, preset.Tuning.Lamps.Attenuation),
            "the lamp's reach and falloff come from the preset");
        var meshes = built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToArray();
        var shell = meshes.Where(m => m.GetParent().HasMeta("surface_role")).ToArray();
        var objects = meshes.Except(shell).ToArray();
        Check(shell.Length == room.Shell.Count && shell.All(m => m.CastShadow == GeometryInstance3D.ShadowCastingSetting.DoubleSided && (m.Layers & look.Key.ShadowCasterMask) != 0),
            "every shell part casts the sun's shadows from both sides (a single-sided wall must not let the sun through), so direct sun comes in only through the openings");
        Check(shell.All(m => m.GIMode == GeometryInstance3D.GIModeEnum.Static) && shell.Any(m => m.GetParent().GetMeta("surface_role").AsString() == "ceiling"),
            "floor, walls and ceiling all join the GI bake: the room is a closed interior");
        Check(objects.Length == room.Objects.Count && objects.All(m => m.GIMode == GeometryInstance3D.GIModeEnum.Dynamic), "movable props are dynamic for GI");
        Check(objects.All(m => m.GetInstanceShaderParameter("box_edges").AsSingle() == 1f && m.GetInstanceShaderParameter("box_half_extents").AsVector3().X > 0f),
            "box props get softened, worn edges with their half extents");
        var seeds = meshes.Select(m => m.GetInstanceShaderParameter("paint_seed").AsSingle()).ToArray();
        Check(seeds.Distinct().Count() == seeds.Length && Mathf.IsEqualApprox(LookDirector.Seed("obj:table"), LookDirector.Seed("obj:table")),
            "each entity gets its own stable paint seed");
        Check(look.Gi == null && look.GiNote.Contains("no GI"), "without a GPU the VoxelGI bake is skipped and says so: " + look.GiNote);
        Check(LookDirector.GiVolume(room, preset.Tuning.Gi).Encloses(room.Bounds), "the GI volume encloses the room bounds");
        var thinnest = room.Shell.Where(p => p.MeshPath == null).Min(p => p.ThicknessM);
        Check(preset.Tuning.Gi.MarginM > 0f && preset.Tuning.Gi.MarginM < 0.75f * thinnest,
            $"the GI volume reaches {preset.Tuning.Gi.MarginM * 100f:0} cm past the room, less than the shell's {thinnest * 100f:0} cm thickness, so the slabs' outer faces (which the sun lights) stay outside it and no light is carried through the walls");
        var afternoonEnergy = look.Key.LightEnergy;
        Check(!look.LampsOn && !lamp.Visible, "on an October afternoon the lamp is off");
        look.SetClock(23f, 279);
        Check(look.Key.LightEnergy < afternoonEnergy * 0.3f && look.Moment.Daylight < 0.2f, "pinning the clock to night dims the key");
        Check(look.LampsOn && lamp.Visible && Mathf.IsEqualApprox(lamp.LightEnergy, (float)lamp.GetMeta("base_energy").AsDouble()),
            "at night the lamp switches itself on, at its own constant brightness (a lamp is a lamp)");
        look.SetLamps(false);
        Check(!look.LampsOn && !lamp.Visible, "a switch can turn the lamps off at night");
        look.SetClock(16.5f, 196);
        Check(!look.LampsOn, "pinned off, the lamps stay off");
        look.SetLamps(null);
        Check(!look.LampsOn && !lamp.Visible, "on a bright summer afternoon the lamps switch themselves off");
        look.SetLamps(true);
        Check(look.LampsOn && lamp.Visible, "a switch can turn the lamps on by day");
        look.SetLamps(null);
        look.SetClock(preset.DefaultHour, 279);
        Check(Mathf.IsEqualApprox(look.Key.LightEnergy, afternoonEnergy) && look.Moment.Season == "autumn", "pinning back to the default hour restores the afternoon");
        Check(look.RendererNote.Length == 0 && look.PlayerNotice.Length == 0, "no renderer mismatch is reported under Forward+");
        Check(look.DescribeLook().Contains($"{preset.PresetId}@{preset.PresetVersion}") && look.DescribeLook().Contains("season autumn"), "the look describes itself for review reports");
        Check(look.SelfCheck().All(p => p.Contains("VoxelGI") || p.Contains("grain")) && !look.SelfCheck().Any(p => p.Contains("stand-in")), "the self-check finds nothing wrong but the missing GPU");
        objects[0].MaterialOverride = new StandardMaterial3D();
        objects[0].MaterialOverride.SetMeta(LookDirector.BakeStandInMeta, true);
        Check(look.SelfCheck().Any(p => p.Contains("stand-in")), "the self-check reports a bake stand-in left on a mesh");
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>
    /// Review finding: dressing was fragile (it found the room by node name, once). Now any mesh that enters under the
    /// director's parent is dressed, whichever order things arrive in and whatever the nodes are called; explicit and
    /// automatic dressing never double up; a rebuilt room is dressed and rebaked; meshes that are not room entities are
    /// left alone.
    /// </summary>
    private async Task CheckDressing(StylePreset preset, RoomData room)
    {
        // A room built before the look is applied, under any name.
        var early = new Node3D { Name = "EarlyHolder" };
        AddChild(early);
        MaterialLibrary.Configure(preset);
        var first = RoomBuilder.Build(room);
        first.Name = "Diorama";
        early.AddChild(first);
        var lookEarly = new LookDirector { Name = "Look" };
        early.AddChild(lookEarly);
        lookEarly.Apply(preset, room);
        await Frames(2);
        Check(lookEarly.Bakes == 1 && IsDressed(first, room), "a room built before the look, named anything, is dressed");
        early.QueueFree();

        // A look applied before it enters the tree, then placed beside a room.
        var late = new Node3D { Name = "LateHolder" };
        AddChild(late);
        var second = RoomBuilder.Build(room);
        late.AddChild(second);
        var lookLate = new LookDirector { Name = "Look" };
        lookLate.Apply(preset, room);
        late.AddChild(lookLate);
        await Frames(2);
        Check(lookLate.Bakes == 1 && IsDressed(second, room), "a look applied before it enters the tree dresses the room it joins");
        late.QueueFree();

        // RoomWorld's order: the look first, then the room, then an explicit Dress; the automatic pass must not bake again.
        var (holder, look) = NewDirector(preset, room, "OrderHolder");
        var built = RoomBuilder.Build(room);
        holder.AddChild(built);
        look.Dress(built);
        Check(look.Bakes == 1 && IsDressed(built, room), "an explicit Dress dresses and bakes at once");
        await Frames(2);
        look.Dress(built);
        Check(look.Bakes == 1, "the automatic pass and a second Dress do not dress or bake again");
        // Things that are not room entities: an avatar's meshes, and a mesh outside the look's parent.
        var avatar = new Node3D { Name = "Avatar" };
        var avatarMesh = new MeshInstance3D { Mesh = new BoxMesh(), MaterialOverride = MaterialLibrary.For("fabric", new Color("d28f63")) };
        avatar.AddChild(avatarMesh);
        holder.AddChild(avatar);
        var outside = new MeshInstance3D { Mesh = new BoxMesh(), MaterialOverride = MaterialLibrary.For("wood", new Color("9a6f45")) };
        var outsideOwner = new Node3D();
        outsideOwner.SetMeta("entity_id", "obj:elsewhere");
        outsideOwner.AddChild(outside);
        AddChild(outsideOwner);
        await Frames(2);
        Check(avatarMesh.Layers == 1u && avatarMesh.GetInstanceShaderParameter("paint_seed").VariantType == Variant.Type.Nil && look.Bakes == 1,
            "a mesh that is not a room entity keeps its layer and gets no paint seed or bake");
        Check(!outside.HasMeta(LookDirector.DressedMeta) && outside.GetInstanceShaderParameter("paint_seed").VariantType == Variant.Type.Nil,
            "a mesh outside the look's parent is not touched");
        outsideOwner.QueueFree();
        // The room is rebuilt (an undo, a restyle): the new geometry is dressed and baked again.
        built.QueueFree();
        var rebuilt = RoomBuilder.Build(room);
        holder.AddChild(rebuilt);
        await Frames(2);
        Check(look.Bakes == 2 && IsDressed(rebuilt, room), "a rebuilt room is dressed and baked again");
        // A later object (a creation) that is a room entity but not shell: dressed, no rebake.
        var creation = new Node3D { Name = "Creation" };
        creation.SetMeta("entity_id", "creation:tower");
        var creationMesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.2f, 0.1f) }, MaterialOverride = MaterialLibrary.For("wood", new Color("9a6f45")) };
        creation.AddChild(creationMesh);
        holder.AddChild(creation);
        await Frames(2);
        Check(look.Bakes == 2 && Mathf.IsEqualApprox(creationMesh.GetInstanceShaderParameter("paint_seed").AsSingle(), LookDirector.Seed("creation:tower"))
            && creationMesh.GetInstanceShaderParameter("box_edges").AsSingle() == 1f && creationMesh.GIMode == GeometryInstance3D.GIModeEnum.Static,
            "a later creation is painted with its own seed and box edges, without a rebake");
        holder.QueueFree();
        await Frames(1);
    }

    private static bool IsDressed(Node3D built, RoomData room)
    {
        var meshes = built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToArray();
        return meshes.Length == room.Shell.Count + room.Objects.Count && meshes.All(m => m.HasMeta(LookDirector.DressedMeta) && m.GetInstanceShaderParameter("paint_seed").VariantType != Variant.Type.Nil)
            && meshes.Where(m => m.GetParent().HasMeta("surface_role")).All(m => m.GIMode == GeometryInstance3D.GIModeEnum.Static);
    }

    /// <summary>
    /// Review finding: removing where depth of field focuses left every check passing. Now the current camera's blur
    /// band is checked against the player's body as it moves, from a follow camera and from the player's own eye, and
    /// against the other focus modes; framed review cameras keep their own focus.
    /// </summary>
    private async Task CheckFocus(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "FocusHolder");
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        player.GlobalPosition = new Vector3(0f, 0f, 0.6f);
        var dof = preset.Tuning.Dof;
        var camera = new Camera3D { Name = "Follow" };
        holder.AddChild(camera);
        camera.GlobalPosition = new Vector3(0f, 0.6f, 1.4f);
        camera.LookAt(player.GlobalPosition);
        camera.MakeCurrent();
        await Frames(2);
        var bodyPoint = player.GlobalPosition + Vector3.Up * (dof.BodyFocusHeightFraction * player.BodyHeightM);
        Check(look.FocusPointFor(camera).IsEqualApprox(bodyPoint), "a follow camera focuses on the player's body (found without FocusTarget)");
        Check(InFocus(camera, bodyPoint, out var band) && !InFocus(camera, bodyPoint + (camera.GlobalPosition - bodyPoint).Normalized() * 0.9f, out _),
            $"the current camera's crisp band holds the player and not what is far nearer the camera ({band})");
        player.GlobalPosition = new Vector3(0.3f, 0f, -0.6f);
        camera.LookAt(player.GlobalPosition);
        await Settle();
        var movedPoint = player.GlobalPosition + Vector3.Up * (dof.BodyFocusHeightFraction * player.BodyHeightM);
        Check(InFocus(camera, movedPoint, out band) && !InFocus(camera, bodyPoint, out _) && Distance(camera, movedPoint) > Distance(camera, bodyPoint) + 0.5f,
            $"the band follows the player a metre away and leaves its old place ({band})");
        var stand = new Node3D { Name = "Stand-in" };
        holder.AddChild(stand);
        stand.GlobalPosition = new Vector3(-0.5f, 0.05f, 0.9f);
        look.FocusTarget = stand;
        await Settle();
        Check(look.FocusPointFor(camera).IsEqualApprox(stand.GlobalPosition) && InFocus(camera, stand.GlobalPosition, out _), "an explicit FocusTarget wins over the parent's player");
        look.FocusTarget = player;
        player.EyeCamera.MakeCurrent();
        await Settle();
        var eyeForward = -player.EyeCamera.GlobalBasis.Z;
        var eyePoint = player.EyeCamera.GlobalPosition + eyeForward * (dof.EyeFocusBodyHeights * player.BodyHeightM);
        var reach = dof.EyeCrispBodyHeights * player.BodyHeightM;
        Check(look.FocusPointFor(player.EyeCamera).IsEqualApprox(eyePoint) && InFocus(player.EyeCamera, eyePoint, out band) && band.Near <= reach + 1e-4f,
            $"the player's eye focuses just past its reach and keeps the reach ({reach:0.##} m) crisp ({band})");
        var review = new Camera3D { Name = "Review" };
        holder.AddChild(review);
        review.GlobalPosition = new Vector3(-1.8f, 2.2f, 1.3f);
        review.LookAt(Vector3.Zero);
        var fixedPoint = new Vector3(0.5f, 0.05f, 0.2f);
        look.FrameCamera(review, fixedPoint);
        review.MakeCurrent();
        await Frames(2);
        Check(InFocus(review, fixedPoint, out _) && look.AttributesFor(review) == review.Attributes, "a framed review camera keeps the focus it was given");
        holder.QueueFree();
        await Frames(1);

        var fixedPreset = PresetWith(("\"focus\": \"player\"", "\"focus\": \"fixed\""));
        var (fixedHolder, fixedLook) = NewDirector(fixedPreset, room, "FixedFocusHolder");
        var still = new Camera3D();
        fixedHolder.AddChild(still);
        still.GlobalPosition = new Vector3(0f, 0.3f, 1f);
        still.LookAt(Vector3.Zero);
        Check(fixedLook.FocusPointFor(still).IsEqualApprox(still.GlobalPosition - still.GlobalBasis.Z * fixedPreset.FocusBandM), "\"fixed\" focuses focus_band_m ahead of the camera");
        var companionPreset = PresetWith(("\"focus\": \"player\"", "\"focus\": \"companion\""));
        var (companionHolder, companionLook) = NewDirector(companionPreset, room, "CompanionFocusHolder");
        var friend = new Node3D();
        companionHolder.AddChild(friend);
        friend.GlobalPosition = new Vector3(0.45f, 0f, 0.6f);
        companionLook.FocusCompanion = friend;
        Check(companionLook.FocusPointFor(still).IsEqualApprox(friend.GlobalPosition), "\"companion\" focuses on the companion");
        fixedHolder.QueueFree();
        companionHolder.QueueFree();
        MaterialLibrary.Configure(preset);
        await Frames(1);
    }

    private readonly record struct Band(float Near, float Far)
    {
        public override string ToString() => $"crisp {Near:0.###} to {Far:0.###} m";
    }

    private static float Distance(Camera3D camera, Vector3 point) => (point - camera.GlobalPosition).Dot(-camera.GlobalBasis.Z);

    /// <summary>Whether a point lies in the crisp band of the camera's depth-of-field attributes.</summary>
    private static bool InFocus(Camera3D camera, Vector3 point, out Band band)
    {
        band = default;
        if (camera.Attributes is not CameraAttributesPractical attributes || !attributes.DofBlurFarEnabled) return false;
        band = new Band(attributes.DofBlurNearEnabled ? attributes.DofBlurNearDistance : 0f, attributes.DofBlurFarDistance);
        var distance = Distance(camera, point);
        return distance >= band.Near && distance <= band.Far;
    }

    /// <summary>Review finding: a 15 ms LUT rebuild. Grades are cached by their quantized inputs and missing ones build off the main thread.</summary>
    /// <summary>
    /// The founder's depth-of-field direction: while moving, focus follows the player and the companion (the band
    /// stretches to keep a nearby companion crisp); while building, it follows the cursor or the free camera
    /// (FocusOverride); and a high view reads as a tilt-shift miniature (narrow band, short ramps, stronger blur).
    /// </summary>
    private async Task CheckArtDirectionFocus(StylePreset preset, RoomData room)
    {
        var high = LookDirector.DepthOfFieldFor(preset, 1.1f, Mathf.Sin(Mathf.DegToRad(45f)));
        var level = LookDirector.DepthOfFieldFor(preset, 1.1f, 0f);
        Check(high.FarDistance - high.NearDistance <= 0.35f && high.Amount >= 0.12f && high.FarTransition < level.FarTransition && high.NearTransition < level.NearTransition,
            $"from 45 degrees up, 1.1 m away, the crisp band is a miniature's ({high.NearDistance:0.##} to {high.FarDistance:0.##} m, blur {high.Amount:0.###}) with shorter ramps than a level view");
        var (holder, look) = NewDirector(preset, room, "ArtFocusHolder");
        look.SetClock(preset.DefaultHour, 279);
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        player.GlobalPosition = new Vector3(0f, 0f, 0.6f);
        var friend = new Node3D { Name = "Friend" };
        holder.AddChild(friend);
        look.FocusCompanion = friend;
        var camera = new Camera3D();
        holder.AddChild(camera);
        camera.GlobalPosition = new Vector3(0f, 0.85f, 1.45f);
        camera.LookAt(player.GlobalPosition);
        camera.MakeCurrent();
        var playerPoint = player.GlobalPosition + Vector3.Up * (preset.Tuning.Dof.BodyFocusHeightFraction * player.BodyHeightM);
        friend.GlobalPosition = player.GlobalPosition + new Vector3(0.1f, 0f, -0.44f);
        await Frames(2);
        Check(InFocus(camera, playerPoint, out var band) && InFocus(camera, friend.GlobalPosition, out _),
            $"a companion 45 cm behind the player is kept crisp with it ({band})");
        var farEdges = new List<float>();
        for (var step = 0; step <= 12; step++)
        {
            friend.GlobalPosition = player.GlobalPosition + new Vector3(0f, 0f, -0.5f - 0.025f * step);
            await Frames(1);
            InFocus(camera, playerPoint, out var edge);
            farEdges.Add(edge.Far);
        }
        var biggestJump = farEdges.Zip(farEdges.Skip(1)).Max(p => Mathf.Abs(p.Second - p.First));
        Check(biggestJump < 0.15f, $"as the companion walks away the band shrinks back without a jump (largest step {biggestJump:0.###} m per 2.5 cm walked; a hard cut would jump about 0.4 m)");
        friend.GlobalPosition = player.GlobalPosition + new Vector3(0.2f, 0f, -2.0f);
        await Settle();
        Check(InFocus(camera, playerPoint, out band) && !InFocus(camera, friend.GlobalPosition, out _),
            $"a companion two metres away is not, and the player stays crisp ({band})");
        var cursor = new Vector3(-1.2f, 0.75f, -0.9f);
        look.FocusOverride = cursor;
        await Settle();
        Check(InFocus(camera, cursor, out band) && !InFocus(camera, playerPoint, out _), $"while building, focus follows the cursor or free camera's point ({band})");
        look.FocusOverride = null;
        await Settle();
        Check(InFocus(camera, playerPoint, out _), "clearing the override returns focus to the player");
        Check(look.ClockNote.Contains("pinned"), "the look reports a pinned clock: " + look.ClockNote);
        look.ReleaseClock();
        Check(look.ClockNote.StartsWith("real clock, real calendar", StringComparison.Ordinal), "released, it follows the real clock and calendar: " + look.ClockNote);
        holder.QueueFree();
        await Frames(1);
        // The deterministic override, as a reviewer or playtester sets it (the environment form; the command line reads the same way).
        var savedClock = OS.GetEnvironment("GARAGE_GAME_LOOK_CLOCK");
        var savedDate = OS.GetEnvironment("GARAGE_GAME_LOOK_DATE");
        OS.SetEnvironment("GARAGE_GAME_LOOK_CLOCK", "05:15");
        OS.SetEnvironment("GARAGE_GAME_LOOK_DATE", "2026-07-15");
        var (pinnedHolder, pinned) = NewDirector(preset, room, "PinnedClockHolder");
        Check(Mathf.IsEqualApprox(pinned.Moment.Hour, 5.25f) && pinned.Moment.DayOfYear == 196 && pinned.ClockNote.Contains("GARAGE_GAME_LOOK_CLOCK"),
            $"GARAGE_GAME_LOOK_CLOCK and GARAGE_GAME_LOOK_DATE pin the look when it is applied ({pinned.Moment.Hour:0.##} h, day {pinned.Moment.DayOfYear}; {pinned.ClockNote})");
        pinnedHolder.QueueFree();
        if (savedClock.Length > 0) OS.SetEnvironment("GARAGE_GAME_LOOK_CLOCK", savedClock); else OS.UnsetEnvironment("GARAGE_GAME_LOOK_CLOCK");
        if (savedDate.Length > 0) OS.SetEnvironment("GARAGE_GAME_LOOK_DATE", savedDate); else OS.UnsetEnvironment("GARAGE_GAME_LOOK_DATE");
        await Frames(1);
    }

    private async Task CheckGradeCache(StylePreset preset, RoomData room)
    {
        var clock = Stopwatch.StartNew();
        ColorGrade.LutBytes(GradeParams.For(preset, LookClock.At(preset, 9f, 100)));
        GD.Print($"LOOK_INFO: one {preset.Tuning.Grade.LutSize}-cubed grade LUT builds in {clock.Elapsed.TotalMilliseconds:0.#} ms on this machine");
        var (holder, look) = NewDirector(preset, room, "CacheHolder");
        look.SetClock(preset.DefaultHour, 279);
        var builds = look.GradeBuilds;
        for (var i = 0; i < 10; i++) look.SetClock(preset.DefaultHour, 279);
        await Frames(30);
        Check(look.GradeBuilds == builds && look.GradeCurrent, "re-pinning the same moment and thirty frames build no new grade");
        look.SetClock(23f, 15);
        var night = look.Environment.AdjustmentColorCorrection;
        look.SetClock(preset.DefaultHour, 279);
        var day = look.Environment.AdjustmentColorCorrection;
        look.SetClock(23f, 15);
        Check(look.GradeBuilds == builds + 1 && look.Environment.AdjustmentColorCorrection == night && night != day, "returning to a moment reuses its cached grade");
        look.SetClockAsync(4f, 196);
        var pending = !look.GradeCurrent;
        var waited = Stopwatch.StartNew();
        while (!look.GradeCurrent && waited.ElapsedMilliseconds < 5000) await Frames(1);
        Check(pending && look.GradeCurrent && look.GradeBuilds == builds + 2, $"a new moment's grade builds off the main thread and replaces the old one when ready (pending {pending}, current {look.GradeCurrent}, builds {look.GradeBuilds - builds})");
        for (var hour = 0; hour < 24; hour++) look.SetClock(hour, 50);
        Check(look.CachedGrades <= LookDirector.GradeCacheSize, $"the grade cache stays bounded ({look.CachedGrades} of {LookDirector.GradeCacheSize})");
        holder.QueueFree();
        await Frames(1);
    }

    // ---------- real-source light (round 2) ----------

    /// <summary>
    /// The founder's answer: the sun follows the real clock at about 30 degrees north, with the right day length on every
    /// date. The solar model against the almanac at 30 degrees north, through the year, at other latitudes, the defaults
    /// in the preset, the west window facing the evening sun, and the real clock read as standard time.
    /// </summary>
    private void CheckSolarModel(StylePreset preset)
    {
        var sun = preset.Tuning.Sun;
        Check(Mathf.IsEqualApprox(sun.LatitudeDeg, 30f) && Mathf.IsEqualApprox(sun.NegZBearingDeg, 0f) && Mathf.IsEqualApprox(sun.SolarNoonH, 12f)
            && !_presetText.Contains("latitude_deg", StringComparison.Ordinal) && !_presetText.Contains("neg_z_bearing_deg", StringComparison.Ordinal)
            && !_presetText.Contains("solar_noon_h", StringComparison.Ordinal),
            "the shared preset holds no site (review M5): a preset on its own works for the fallback site, 30 degrees north with -Z facing north (so -X is west)");
        // The founder's figures at 30 degrees north: about 10.2 h at the December solstice, 12 h at the equinoxes, 13.9 h at the June solstice.
        var december = LookClock.DayLength(preset, 355);
        var march = LookClock.DayLength(preset, 79);
        var june = LookClock.DayLength(preset, 172);
        var september = LookClock.DayLength(preset, 265);
        GD.Print($"LOOK_INFO: day length at {sun.LatitudeDeg} degrees: {december:0.00} h on 21 December, {march:0.00} h on 20 March, {june:0.00} h on 21 June, {september:0.00} h on 22 September");
        Check(Mathf.Abs(december - 10.2f) <= 0.15f && Mathf.Abs(march - 12f) <= 0.2f && Mathf.Abs(september - 12f) <= 0.2f && Mathf.Abs(june - 13.9f) <= 0.2f,
            $"day lengths at 30 degrees north: {december:0.00} h at the December solstice, {march:0.00} and {september:0.00} h at the equinoxes, {june:0.00} h at the June solstice (about 10.2, 12 and 13.9)");
        // Every date: the day grows and shrinks smoothly and is shortest and longest at the solstices.
        var lengths = Enumerable.Range(1, 365).Select(d => LookClock.DayLength(preset, d)).ToArray();
        var shortest = Array.IndexOf(lengths, lengths.Min()) + 1;
        var longest = Array.IndexOf(lengths, lengths.Max()) + 1;
        var dailyStep = Enumerable.Range(1, 364).Max(i => Mathf.Abs(lengths[i] - lengths[i - 1]));
        Check(Math.Abs(shortest - 355) <= 4 && Math.Abs(longest - 172) <= 4 && dailyStep < 0.05f,
            $"through the year the day is shortest on day {shortest} and longest on day {longest}, changing at most {dailyStep * 60f:0.#} minutes a day");
        // Noon elevations (90 - latitude + declination) and where the sun rises.
        float NoonElevation(int day) { var (r, s) = LookClock.SunTimes(sun, day); return LookClock.SolarPosition(sun, day, (r + s) * 0.5f).ElevationDeg; }
        float RiseBearing(int day) => LookClock.SolarPosition(sun, day, LookClock.SunTimes(sun, day).Sunrise).BearingDeg;
        Check(Mathf.Abs(NoonElevation(172) - 83.4f) < 0.5f && Mathf.Abs(NoonElevation(355) - 36.6f) < 0.5f && Mathf.Abs(NoonElevation(79) - 60f) < 0.7f,
            $"the noon sun stands {NoonElevation(355):0.#} degrees high at the December solstice, {NoonElevation(79):0.#} at the March equinox and {NoonElevation(172):0.#} at the June solstice");
        Check(RiseBearing(172) is > 55f and < 70f && RiseBearing(355) is > 110f and < 125f && Mathf.Abs(LookClock.SolarPosition(sun, 172, 12.1f).BearingDeg - 180f) < 30f,
            $"the sun rises north of east in June ({RiseBearing(172):0} degrees) and south of east in December ({RiseBearing(355):0}), and stands in the south at noon");
        // The latitude is used: farther north the June day is longer, and in the south hemisphere June is winter.
        var north50 = LookClock.SunTimes(50f, 12f, sun.SunriseElevationDeg, 172);
        var south30 = LookClock.SunTimes(-30f, 12f, sun.SunriseElevationDeg, 172);
        Check(Mathf.Abs(north50.Sunset - north50.Sunrise - 16.4f) < 0.2f && Mathf.Abs(south30.Sunset - south30.Sunrise - december) < 0.1f,
            $"latitude matters: 21 June lasts {north50.Sunset - north50.Sunrise:0.0} h at 50 degrees north and {south30.Sunset - south30.Sunrise:0.0} h at 30 degrees south");
        // The room's west window faces the evening sun: in the late afternoon the sun is in the west and the key comes from -X.
        foreach (var (hour, day) in new[] { (16.5f, 279), (18f, 196), (17.5f, 105) })
        {
            var moment = LookClock.At(preset, hour, day);
            var from = LookClock.Direction(moment.KeyElevationDeg, moment.KeyAzimuthDeg);
            Check(Mathf.Abs(moment.SunBearingDeg - 270f) < 35f && from.X < -0.75f * new Vector2(from.X, from.Z).Length(),
                $"day {day} at {hour:0.##}: the sun is in the west (bearing {moment.SunBearingDeg:0}) and its light comes from the room's west, -X");
        }
        // The real clock is read as standard time when the preset says so, in any zone with daylight saving; pins never change.
        var zone = TimeZoneInfo.CreateCustomTimeZone("look-test", TimeSpan.Zero, "look test", "look test standard", "look test summer",
            new[]
            {
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                    TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
            });
        var summer = LookClock.StandardClock(new DateTime(2026, 7, 15, 18, 30, 0), zone, true);
        var winter = LookClock.StandardClock(new DateTime(2026, 1, 15, 18, 30, 0), zone, true);
        var asRead = LookClock.StandardClock(new DateTime(2026, 7, 15, 18, 30, 0), zone, false);
        var pastMidnight = LookClock.StandardClock(new DateTime(2026, 7, 15, 0, 30, 0), zone, true);
        Check(Mathf.IsEqualApprox(summer.Hour, 17.5f) && summer.DayOfYear == 196 && Mathf.IsEqualApprox(winter.Hour, 18.5f) && Mathf.IsEqualApprox(asRead.Hour, 18.5f)
            && Mathf.IsEqualApprox(pastMidnight.Hour, 23.5f) && pastMidnight.DayOfYear == 195 && sun.RealClockDaylightSaving,
            "the real clock is read as standard time: 18:30 in summer time is 17:30 on the sun, winter is unchanged, and 00:30 falls back to the day before");
    }

    /// <summary>
    /// The founder's answer: the sun comes in through the west window only. The shell casts the sun's shadows (checked
    /// with the director); here the geometry, with physics rays from the built room: at the review moment and on a summer
    /// evening, every floor point whose line to the sun leaves through the window opening has a clear line, and every
    /// floor point whose line to the sun would cross a wall or the ceiling is blocked by the shell, so it gets no direct sun.
    /// </summary>
    private async Task CheckSunThroughWindow(StylePreset preset, RoomData room)
    {
        using var document = System.Text.Json.JsonDocument.Parse(FileAccess.GetFileAsBytes(RoomWorld.DefaultRoom + "/room.json"));
        var openings = document.RootElement.GetProperty("shell").GetProperty("openings").EnumerateArray().ToArray();
        var window = openings.Single(o => o.GetProperty("kind").GetString() == "window");
        Check(window.GetProperty("host_part_id").GetString() == "shell:wall_west", "the test room's only window is in the west wall");
        var centre = window.GetProperty("center_m");
        var size = window.GetProperty("size_m");
        float wallX = centre[0].GetSingle(), midY = centre[1].GetSingle(), midZ = centre[2].GetSingle();
        float halfWidth = size[0].GetSingle() * 0.5f, halfHeight = size[1].GetSingle() * 0.5f;
        var (holder, look) = NewDirector(preset, room, "SunWindowHolder");
        var built = RoomBuilder.Build(room);
        holder.AddChild(built);
        await PhysicsFrames(3);
        var objects = built.GetNode("Objects").GetChildren().OfType<CollisionObject3D>().Select(o => o.GetRid()).ToArray();
        var space = GetWorld3D().DirectSpaceState;
        const float margin = 0.03f;
        foreach (var (hour, day, label) in new[] { (16.5f, 279, "16:30 on 6 October"), (18f, 196, "18:00 on 15 July") })
        {
            look.SetClock(hour, day);
            var toSun = look.Key.GlobalBasis.Z.Normalized();
            int through = 0, throughClear = 0, walled = 0, walledBlocked = 0;
            var bad = new List<string>();
            var bounds = room.Bounds;
            for (var x = bounds.Position.X + 0.05f; x < bounds.End.X - 0.04f; x += 0.1f)
                for (var z = bounds.Position.Z + 0.05f; z < bounds.End.Z - 0.04f; z += 0.1f)
                {
                    var point = new Vector3(x, 0.002f, z);
                    // Where the line to the sun crosses the west wall's inner face.
                    var t = toSun.X < -1e-4f ? (wallX - point.X) / toSun.X : float.PositiveInfinity;
                    var at = point + toSun * t;
                    var inOpening = float.IsFinite(t) && Mathf.Abs(at.Y - midY) < halfHeight - margin && Mathf.Abs(at.Z - midZ) < halfWidth - margin;
                    var offOpening = !float.IsFinite(t) || Mathf.Abs(at.Y - midY) > halfHeight + margin || Mathf.Abs(at.Z - midZ) > halfWidth + margin;
                    var query = PhysicsRayQueryParameters3D.Create(point, point + toSun * 12f);
                    query.Exclude = new Godot.Collections.Array<Rid>(objects);
                    var hit = space.IntersectRay(query);
                    var shell = hit.Count > 0 && hit["collider"].AsGodotObject() is Node node && node.HasMeta("surface_role");
                    if (inOpening)
                    {
                        through++;
                        if (hit.Count == 0) throughClear++;
                        else if (bad.Count < 3) bad.Add($"({x:0.0}, {z:0.0}) should see the sun through the window");
                    }
                    else if (offOpening)
                    {
                        walled++;
                        if (shell) walledBlocked++;
                        else if (bad.Count < 3) bad.Add($"({x:0.0}, {z:0.0}) should be in the shell's shadow");
                    }
                }
            GD.Print($"LOOK_INFO: {label}: sun {look.Moment.SunElevationDeg:0.#} degrees high at bearing {look.Moment.SunBearingDeg:0}; {through} floor points see it through the window, {walled} are behind the walls");
            Check(look.Key.Visible && look.Key.LightEnergy > 0.1f && through >= 10 && throughClear == through && walled >= 100 && walledBlocked == walled,
                $"{label}: direct sun reaches the floor only through the window ({throughClear}/{through} points in the window's patch see it, {walledBlocked}/{walled} outside it are in the shell's shadow) {string.Join("; ", bad)}");
        }
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>
    /// The founder's answer: nights at moonlight level (dark, but you can make out shapes), cool moonlight through the
    /// window, lamps off still showing silhouettes, lamps on cozy. Measured against the golden-hour review moment.
    /// </summary>
    private void CheckNightLevels(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "NightHolder");
        look.SetClock(16.5f, 279);
        var goldenSun = look.Key.LightEnergy * ColorGrade.Luma(look.Key.LightColor);
        var goldenSky = look.SkyFills.Sum(f => f.LightEnergy);
        var goldenGrade = GradeParams.For(preset, look.Moment);
        look.SetClock(2f, 279);
        look.SetLamps(false);
        var moon = look.Key.LightEnergy * ColorGrade.Luma(look.Key.LightColor);
        var nightSky = look.SkyFills.Sum(f => f.LightEnergy);
        var fromMoon = look.Key.GlobalBasis.Z;
        var moonColor = look.Key.LightColor;
        GD.Print($"LOOK_INFO: at 2:00 the moon is {moon / goldenSun:P0} of the golden-hour sun and the window's sky {nightSky / goldenSky:P0} of its golden-hour sky; lamps {(look.LampsOn ? "on" : "off")}");
        Check(moon >= 0.06f * goldenSun && moon <= 0.3f * goldenSun && nightSky >= 0.15f * goldenSky && nightSky <= 0.4f * goldenSky,
            $"at night, lamps off, moonlight is {moon / goldenSun:P0} of the golden-hour sun and the sky fill {nightSky / goldenSky:P0} of its golden-hour level: dark, but light enough to make out shapes");
        Check(look.Moment.MoonWeight >= 0.999f && fromMoon.X < -0.7f && fromMoon.Y > 0.3f && moonColor.B > moonColor.R + 0.1f && look.Lamps.All(l => !l.Visible),
            "the moon shines in through the west window in a cool blue, and the lamps are off");
        // The night grade keeps the dim values where silhouettes live: it cools and calms them but does not crush them.
        var nightGrade = GradeParams.For(preset, look.Moment);
        var dim = new Color(0.1f, 0.1f, 0.1f);
        var kept = ColorGrade.Luma(ColorGrade.Apply(nightGrade, dim)) / ColorGrade.Luma(ColorGrade.Apply(goldenGrade, dim));
        Check(nightGrade.Night >= 0.95f && kept >= 0.75f, $"the night grade keeps {kept:P0} of a dim grey's brightness, so silhouettes stay readable");
        // Lamps on: the cozy main light, warm and several times the moonlight.
        look.SetLamps(null);
        var lamp = look.Lamps.Single();
        Check(look.LampsOn && lamp.Visible && lamp.LightColor.R - lamp.LightColor.B >= 0.2f && lamp.LightEnergy >= 3f * look.Key.LightEnergy,
            $"with the lamps on (they switch on by themselves at night) the warm lamp is the main light ({lamp.LightEnergy:0.##} against the moon's {look.Key.LightEnergy:0.##})");
        holder.Free();
    }

    /// <summary>
    /// The founder's answer: warm gold where the sun lands, cool light-blue in the sky fill, shade and shadows, as in
    /// Tiny Glade 1 and Titl Shift 7. Through the golden hour (the 90 minutes before sunset on the review day, and at the
    /// review moment), the sun is warm, the window's sky fill is cool light blue, and the grade keeps shade cool.
    /// </summary>
    private void CheckGoldenHourCoolFill(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "GoldenFillHolder");
        var sunset = LookClock.SunTimes(preset.Tuning.Sun, 279).Sunset;
        var worstFill = float.PositiveInfinity;
        var worstSun = float.PositiveInfinity;
        var worstShade = float.PositiveInfinity;
        foreach (var hour in Enumerable.Range(0, 91).Select(m => sunset - 1.5f + m / 60f).Append(16.5f))
        {
            look.SetClock(hour, 279);
            var fill = look.SkyFills.Single().LightColor;
            var sun = look.Key.LightColor;
            var shade = ColorGrade.Apply(GradeParams.For(preset, look.Moment), new Color(0.15f, 0.15f, 0.15f));
            worstFill = Mathf.Min(worstFill, Mathf.Min(fill.B - fill.R, fill.B - fill.G));
            worstSun = Mathf.Min(worstSun, (sun.R - sun.B) / Mathf.Max(sun.R, 1e-3f));
            worstShade = Mathf.Min(worstShade, shade.B - shade.R);
        }
        look.SetClock(16.5f, 279);
        var reviewFill = look.SkyFills.Single().LightColor;
        GD.Print($"LOOK_INFO: golden hour on 6 October (sunset {sunset:0.00}): sun #{look.Key.LightColor.ToHtml(false)}, sky fill #{reviewFill.ToHtml(false)} at the review moment");
        Check(worstFill >= 0.1f && worstSun >= 0.25f && worstShade >= 0f,
            $"through the golden hour the sky fill is cool light blue (blue leads by at least {worstFill:0.##}), the sun warm (red leads blue by at least {worstSun:P0}) and graded shade cool (blue minus red at least {worstShade:0.###})");
        holder.Free();
    }

    /// <summary>
    /// The founder's answer: reduce the global orange cast so the room's greens, blues, creams and pinks read as
    /// themselves; the warmth comes from the sunlight. The grade alone barely colours a neutral grey at the golden-hour
    /// review moment and at noon in every season, and every colour of the room keeps its hue through the material glaze
    /// and the grade.
    /// </summary>
    private void CheckReducedGlobalTint(StylePreset preset, RoomData room)
    {
        float Noon(int day) { var (r, s) = LookClock.SunTimes(preset.Tuning.Sun, day); return (r + s) * 0.5f; }
        var moments = new[] { ("the golden hour", LookClock.At(preset, 16.5f, 279)), ("winter noon", LookClock.At(preset, Noon(15), 15)),
            ("spring noon", LookClock.At(preset, Noon(105), 105)), ("summer noon", LookClock.At(preset, Noon(196), 196)), ("autumn noon", LookClock.At(preset, Noon(288), 288)) };
        foreach (var (name, moment) in moments)
        {
            var grade = GradeParams.For(preset, moment);
            var warmest = float.NegativeInfinity;
            var strongest = 0f;
            foreach (var level in new[] { 0.25f, 0.45f, 0.65f })
            {
                var graded = ColorGrade.Apply(grade, new Color(level, level, level));
                warmest = Mathf.Max(warmest, graded.R - graded.B);
                strongest = Mathf.Max(strongest, Mathf.Max(graded.R, Mathf.Max(graded.G, graded.B)) - Mathf.Min(graded.R, Mathf.Min(graded.G, graded.B)));
            }
            Check(warmest <= 0.03f && strongest <= 0.06f, $"{name}: the grade barely tints a neutral grey (red over blue at most {warmest:0.###}, colour at most {strongest:0.###})");
        }
        // The room's own colours through the glaze and the golden-hour grade, against the same grade with only its
        // gentle desaturation and contrast (no tint, warmth or toning): the difference is the tint the look adds.
        var golden = GradeParams.For(preset, moments[0].Item2);
        var neutral = new Color(0.5f, 0.5f, 0.5f);
        var untinted = golden with { Warmth = 0f, SeasonTint = neutral, ShadowTint = neutral, HighlightTint = neutral, Night = 0f };
        var colours = room.Shell.Select(s => (Name: s.Id, Role: s.MaterialRole, Base: s.BaseColor))
            .Concat(room.Objects.Select(o => (Name: o.Id, Role: o.Asset.Materials[0].Role, Base: o.Asset.Materials[0].BaseColor ?? new Color("b3aea4"))))
            .GroupBy(c => c.Base.ToHtml(false)).Select(g => g.First()).ToArray();
        var report = new List<string>();
        var drifted = new List<string>();
        foreach (var (name, role, colour) in colours)
        {
            var albedo = MaterialLibrary.Glaze(colour, preset.TreatmentFor(role).Tint, preset.GlazeAmount);
            var graded = ColorGrade.Apply(golden, albedo);
            var plain = ColorGrade.Apply(untinted, colour);
            // The tint the look adds, measured perceptually: the shift in Oklab's colour plane (about 0.02 is just noticeable).
            var (a, b) = (Oklab(graded), Oklab(plain));
            var tint = new Vector2(a.Y - b.Y, a.Z - b.Z).Length();
            report.Add($"{name} #{colour.ToHtml(false)} to #{graded.ToHtml(false)} (tint {tint:0.0000})");
            if (tint > 0.015f) drifted.Add($"{name} ({tint:0.000})");
        }
        GD.Print("LOOK_INFO: the room's colours at the golden hour: " + string.Join("; ", report));
        Check(drifted.Count == 0, "every colour of the room reads as itself: the glaze and the grade tint it by less than a just-noticeable step (0.015 in Oklab): tinted " + string.Join(", ", drifted));
        Color Of(string id) => MaterialLibrary.Glaze(colours.First(c => c.Name == id).Base, preset.TreatmentFor(colours.First(c => c.Name == id).Role).Tint, preset.GlazeAmount);
        var sage = ColorGrade.Apply(golden, Of("obj:table"));
        var book = ColorGrade.Apply(golden, Of("obj:book"));
        var rug = ColorGrade.Apply(golden, Of("obj:rug"));
        var wall = ColorGrade.Apply(golden, Of("shell:wall_east"));
        Check(sage.G > sage.R && sage.G > sage.B && book.B > book.R && book.B > book.G && rug.R > rug.G && rug.B >= rug.G - 0.005f && wall.B >= wall.R,
            "the sage table stays green, the book light blue, the rug pink rather than peach, and the east wall light blue");
    }

    // ---------- helpers ----------

    private (Node3D Holder, LookDirector Look) NewDirector(StylePreset preset, RoomData room, string name)
    {
        var holder = new Node3D { Name = name };
        AddChild(holder);
        var look = new LookDirector { Name = "Look" };
        holder.AddChild(look);
        look.Apply(preset, room);
        return (holder, look);
    }

    /// <summary>A replacement that sets one numeric field of the shipped preset, whatever its current value.</summary>
    private (string From, string To) Field(string name, string value)
    {
        var match = Regex.Match(_presetText, $"\"{name}\": -?[0-9.]+");
        if (!match.Success) throw new InvalidOperationException($"test edit target not found: {name}");
        return (match.Value, $"\"{name}\": {value}");
    }

    /// <summary>The shipped preset with text replacements, parsed (not loaded from disk, so no pin applies).</summary>
    private StylePreset PresetWith(params (string From, string To)[] edits)
    {
        var text = _presetText;
        foreach (var (from, to) in edits)
        {
            if (!text.Contains(from, StringComparison.Ordinal)) throw new InvalidOperationException($"test edit target not found: {from}");
            text = text.Replace(from, to);
        }
        return StylePreset.Parse(Encoding.UTF8.GetBytes(text), "variant.json");
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Oklab (L, a, b) of a display (sRGB) colour: a perceptual space where equal distances look about equally different.</summary>
    private static Vector3 Oklab(Color display)
    {
        var c = display.SrgbToLinear();
        var l = Mathf.Pow(0.4122214708f * c.R + 0.5363325363f * c.G + 0.0514459929f * c.B, 1f / 3f);
        var m = Mathf.Pow(0.2119034982f * c.R + 0.6806995451f * c.G + 0.1073969566f * c.B, 1f / 3f);
        var s = Mathf.Pow(0.0883024619f * c.R + 0.2817188376f * c.G + 0.6299787005f * c.B, 1f / 3f);
        return new Vector3(0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    /// <summary>Wait out the focus easing: ten times its time constant, so the band has arrived.</summary>
    private async Task Settle() => await Frames(80);

    private async Task PhysicsFrames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private void CheckThrows(Action action, string fragment, string label)
    {
        try { action(); Check(false, label + " (no error)"); }
        catch (Exception error) { Check(error.Message.Contains(fragment), $"{label} ({error.Message})"); }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Look: " + label);
    }
}
