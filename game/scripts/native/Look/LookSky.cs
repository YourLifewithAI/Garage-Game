using Godot;

namespace GarageGame.Native.Look;

/// <summary>
/// What the sky looks like at one moment, as the shader draws it behind a window (review M2: a window used to show the
/// preset's dark slate background, by day and by night). Colours are display colours; the shader converts them. The sky
/// is the brightest thing a window shows, so Brightness lifts it into the glow's range and it reads as light, not paint.
/// </summary>
public readonly record struct SkyLook(
    Color Zenith, Color Horizon, Color SunColor, Vector3 ToSun, float SunVisible, Color MoonColor, Vector3 ToMoon, float MoonVisible,
    Color CloudLight, Color CloudShade, float CloudAmount, float Brightness, float Stars, float SunDisc, float SunHalo, float MoonDisc, Color Ground);

/// <summary>The sky behind the windows, from the preset's x_look_sky and the season, the sun's height and the moon.</summary>
public static class LookSky
{
    /// <summary>
    /// The sky at a moment. The zenith and horizon colours run from day through dusk (the sun under dusk_end_deg high)
    /// to night (as the moon takes over), each season sets how saturated, bright and cloudy it is, and the sun and the
    /// moon stand in their real directions in the room's frame (moonYawDeg is the Godot yaw the look put the moon at).
    /// </summary>
    public static SkyLook At(StylePreset preset, LookMoment moment, float? moonYawDeg = null)
    {
        var t = preset.Tuning.Sky;
        var sun = preset.Tuning.Sun;
        var look = moment.Look;
        var elevation = moment.SunElevationDeg;
        // 0 in full day, 1 with the sun at or under dusk_start_deg: low sun, warm horizon, violet zenith.
        var dusk = 1f - Mathf.SmoothStep(t.DuskStartDeg, t.DuskEndDeg, elevation);
        var night = moment.MoonWeight;
        var zenith = t.DayZenith.Lerp(t.DuskZenith, dusk).Lerp(t.NightZenith, night);
        var horizon = t.DayHorizon.Lerp(t.DuskHorizon, dusk).Lerp(t.NightHorizon, night);
        zenith = Saturate(zenith, look.SkySaturation);
        horizon = Saturate(horizon, look.SkySaturation);
        var sunColor = moment.KeyColor;
        var toSun = LookClock.Direction(elevation, LookClock.Yaw(sun, moment.SunBearingDeg));
        var toMoon = LookClock.Direction(sun.MoonElevationDeg, moonYawDeg ?? LookClock.Yaw(sun, sun.MoonBearingDeg));
        // Clouds catch the colour of the low sun and fade to dark shapes at night.
        var cloudLight = t.CloudLight.Lerp(sunColor, 0.65f * dusk * (1f - night));
        var cloudShade = t.CloudShade.Lerp(zenith, 0.5f * dusk).Lerp(t.NightHorizon, night * 0.8f);
        cloudLight = cloudLight.Lerp(t.NightHorizon.Lightened(0.15f), night);
        var clouds = look.CloudAmount;
        // The meadow under the horizon takes the season's colour (fresh in spring, ochre in autumn, frosted in winter) and
        // sinks into the night.
        var ground = t.Ground.Lerp(moment.SeasonTint, t.GroundSeasonTint).Lerp(t.NightHorizon, night * 0.85f);
        return new SkyLook(
            zenith, horizon, sunColor, toSun, Mathf.SmoothStep(-1.5f, 1.5f, elevation) * (1f - night),
            new Color(0.86f, 0.9f, 1f), toMoon, night,
            cloudLight, cloudShade, clouds,
            t.Brightness * look.SkyBrightness * Mathf.Lerp(1f, 0.45f, night),
            t.StarStrength * night * night * (1f - clouds * 0.7f),
            t.SunDiscDeg, t.SunHalo, t.MoonDiscDeg, ground);
    }

    /// <summary>A colour moved toward its own grey (amount under 1) or away from it (over 1).</summary>
    public static Color Saturate(Color color, float amount)
    {
        var luma = ColorGrade.Luma(color);
        var grey = new Color(luma, luma, luma);
        var c = grey.Lerp(color, amount);
        return new Color(Mathf.Clamp(c.R, 0f, 1f), Mathf.Clamp(c.G, 0f, 1f), Mathf.Clamp(c.B, 0f, 1f));
    }

    /// <summary>Hand a sky to its shader material.</summary>
    public static void Apply(ShaderMaterial material, SkyLook sky)
    {
        material.SetShaderParameter("zenith", sky.Zenith);
        material.SetShaderParameter("horizon", sky.Horizon);
        material.SetShaderParameter("sun_color", sky.SunColor);
        material.SetShaderParameter("to_sun", sky.ToSun);
        material.SetShaderParameter("sun_visible", sky.SunVisible);
        material.SetShaderParameter("moon_color", sky.MoonColor);
        material.SetShaderParameter("to_moon", sky.ToMoon);
        material.SetShaderParameter("moon_visible", sky.MoonVisible);
        material.SetShaderParameter("cloud_light", sky.CloudLight);
        material.SetShaderParameter("cloud_shade", sky.CloudShade);
        material.SetShaderParameter("cloud_amount", sky.CloudAmount);
        material.SetShaderParameter("brightness", sky.Brightness);
        material.SetShaderParameter("stars", sky.Stars);
        material.SetShaderParameter("sun_disc", Mathf.DegToRad(sky.SunDisc));
        material.SetShaderParameter("sun_halo", sky.SunHalo);
        material.SetShaderParameter("moon_disc", Mathf.DegToRad(sky.MoonDisc));
        material.SetShaderParameter("ground", sky.Ground);
    }
}
