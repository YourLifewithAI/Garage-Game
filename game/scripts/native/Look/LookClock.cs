using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace GarageGame.Native.Look;

/// <summary>
/// The light and grade a preset calls for at one hour on one day of the year. KeyHour is the hour on the time keys'
/// reference day this moment maps to. SunElevationDeg and SunBearingDeg are where the real sun is (bearing clockwise
/// from north), up or not. The key's own direction (KeyElevationDeg, and KeyAzimuthDeg as Godot's yaw in the room)
/// is the sun's while the sun is up, the moon's at night, and a cross-fade in twilight (MoonWeight). Look is what the
/// season does to the sun and sky beyond its colour grade (a harder summer sun, a paler winter sky). Weights is how much of
/// each season the day holds, the very cross-fade the grade and the look use (the land's seasons read it, LandSeason).
/// </summary>
public sealed record LookMoment(
    float Hour, int DayOfYear, string Season, Color KeyColor, float KeyEnergy, Color AmbientColor, float AmbientEnergy,
    float KeyElevationDeg, float KeyAzimuthDeg, float Daylight, float MoonWeight, float SeasonSaturation, float SeasonWarmth, Color SeasonTint,
    float KeyHour, float SunElevationDeg, float SunBearingDeg, SeasonLook Look, SeasonWeights Weights);

/// <summary>
/// How much of each season a day holds: one of them alone around its middle (the preset's hold), two cross-faded between, always
/// summing to one. The same blend the grade's saturation, warmth and tint and the season's sun and sky follow (LookClock.Cross).
/// A preset without seasons is summer alone, the land as it was drawn.
/// </summary>
public readonly record struct SeasonWeights(float Winter, float Spring, float Summer, float Autumn)
{
    public static readonly SeasonWeights SummerOnly = new(0f, 0f, 1f, 0f);

    /// <summary>One season alone.</summary>
    public static SeasonWeights Only(string season) => Between(season, season, 0f);

    /// <summary>The first season at 1 - s, the second at s (the same season twice is that season alone).</summary>
    public static SeasonWeights Between(string from, string to, float s)
    {
        float W(string name) => (from == name ? 1f - s : 0f) + (to == name ? s : 0f);
        return new SeasonWeights(W("winter"), W("spring"), W("summer"), W("autumn"));
    }

    public float Of(string season) => season switch
    {
        "winter" => Winter, "spring" => Spring, "summer" => Summer, "autumn" => Autumn,
        _ => throw new ArgumentException($"'{season}' is not winter, spring, summer or autumn", nameof(season)),
    };

    public float Sum => Winter + Spring + Summer + Autumn;
}

/// <summary>
/// Time of day and season for a preset. Pure functions, so the review harness can pin a moment and tests can
/// check them without a renderer.
///
/// The sun follows a solar model (x_look_sun): its elevation and bearing come from the latitude, the date and the
/// local standard clock hour, so the day is as long as it really is on every date (at 30 degrees north about 10.2 h
/// at the December solstice, 12.1 h at the equinoxes and 14.1 h at the June solstice). Colour and brightness come
/// from the preset's time keys, which describe one reference day: the real sunrise and sunset are mapped onto the
/// keys' sunrise and sunset (where their daylight crosses reference_daylight), linearly through the day and through
/// the night, so the keys' golden hour always falls just before the real sunset. Daylight is the key energy over the
/// brightest key.
///
/// In "sun" key mode the key is the sun while the sun is above the horizon, the moon once the sun is below
/// twilight_elevation_deg, and turns from one to the other only in that twilight, while the light is dim.
/// </summary>
public static class LookClock
{
    private const int SampleSteps = 24 * 60;
    private const double DegToRad = Math.PI / 180.0;
    private static readonly ConditionalWeakTable<StylePreset, Tuple<float, float>> LimitsCache = new();

    /// <summary>The look at an hour (local standard time) on a day of the year. moonYawDeg places the moon (Godot yaw in the room); null takes the preset's moon bearing.</summary>
    public static LookMoment At(StylePreset preset, float hour, int dayOfYear, float? moonYawDeg = null)
    {
        hour = Mathf.PosMod(hour, 24f);
        var sun = preset.Tuning.Sun;
        var keyHour = KeyHour(preset, hour, dayOfYear);
        var key = preset.TimeOfDayEnabled && preset.TimeKeys.Count > 0
            ? Sample(preset.TimeKeys, keyHour)
            : new TimeKey(hour, preset.KeyColor, preset.KeyEnergy, preset.AmbientColor, preset.AmbientEnergy);
        var maxEnergy = MaxEnergy(preset);
        var daylight = preset.TimeOfDayEnabled && maxEnergy > 0 ? Mathf.Clamp(key.KeyEnergy / maxEnergy, 0f, 1f) : 1f;
        var (sunElevation, sunBearing) = SolarPosition(sun, dayOfYear, hour);
        float elevation = preset.KeyElevationDeg, azimuth = preset.KeyAzimuthDeg, moonWeight = 0f;
        if (preset.KeyMode == "sun")
        {
            moonWeight = 1f - Mathf.SmoothStep(sun.TwilightElevationDeg, 0f, sunElevation);
            var sunDirection = Direction(Mathf.Max(sunElevation, sun.HorizonElevationDeg), Yaw(sun, sunBearing));
            var moon = Direction(sun.MoonElevationDeg, moonYawDeg ?? Yaw(sun, sun.MoonBearingDeg));
            (elevation, azimuth) = Angles(Slerp(sunDirection, moon, moonWeight));
        }
        var (season, saturation, warmth, tint) = preset.SeasonsEnabled
            ? Grade(preset, dayOfYear)
            : ("none", 1f, 0f, new Color(0.5f, 0.5f, 0.5f));
        var seasonLook = preset.SeasonsEnabled ? SeasonLookAt(preset, dayOfYear) : SeasonLook.Neutral;
        var weights = preset.SeasonsEnabled ? WeightsAt(preset, dayOfYear) : SeasonWeights.SummerOnly;
        // The preset's key energy is the reference; time-of-day keys scale it relative to the brightest key. The season
        // sets the sun's strength (summer harder, winter weaker), not the moon's.
        var scaled = preset.TimeOfDayEnabled && maxEnergy > 0 ? preset.KeyEnergy * key.KeyEnergy / maxEnergy : preset.KeyEnergy;
        // Only the sunlight above the keys' night floor is the season's to scale (the moon's light is not), which keeps the
        // key's rise and fall through the day a single hump whatever the season's strength.
        var floor = preset.TimeOfDayEnabled && maxEnergy > 0 ? preset.KeyEnergy * MinEnergy(preset) / maxEnergy : 0f;
        var keyEnergy = floor + (scaled - floor) * seasonLook.SunEnergy;
        // The season colours the sunlight: paler in winter, golden in summer. The grade itself stays nearly neutral.
        var keyColor = preset.SeasonsEnabled ? key.KeyColor * SeasonLight(tint, preset.Tuning.Seasons.LightStrength) : key.KeyColor;
        return new LookMoment(hour, dayOfYear, season, keyColor, keyEnergy, key.AmbientColor, key.AmbientEnergy,
            elevation, azimuth, daylight, moonWeight, saturation, warmth, tint, keyHour, sunElevation, sunBearing, seasonLook, weights);
    }

    // ---------- the solar model ----------

    /// <summary>
    /// The sun's declination (radians) and the equation of time (minutes) on a day of the year at a clock hour:
    /// NOAA's Fourier series (Spencer 1971), good to about a minute of time and a few hundredths of a degree.
    /// </summary>
    public static (double Declination, double EquationOfTimeMin) SolarTerms(int dayOfYear, double hour)
    {
        var g = 2.0 * Math.PI / 365.0 * (dayOfYear - 1 + (hour - 12.0) / 24.0);
        var equation = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g) - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
        var declination = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g) - 0.006758 * Math.Cos(2 * g) + 0.000907 * Math.Sin(2 * g)
            - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
        return (declination, equation);
    }

    public static (float ElevationDeg, float BearingDeg) SolarPosition(SunTuning sun, int dayOfYear, float hour) =>
        SolarPosition(sun.LatitudeDeg, sun.SolarNoonH, dayOfYear, hour);

    /// <summary>
    /// Where the sun is, seen from a latitude at a local standard clock hour: its elevation above the horizon and its
    /// compass bearing (clockwise from north: 90 east, 180 south, 270 west). Mean solar noon falls at solarNoonH on
    /// the clock; the equation of time moves true noon by up to a quarter of an hour through the year.
    /// </summary>
    public static (float ElevationDeg, float BearingDeg) SolarPosition(float latitudeDeg, float solarNoonH, int dayOfYear, float hour)
    {
        var (declination, equation) = SolarTerms(dayOfYear, hour);
        var solarTime = hour - (solarNoonH - 12.0) + equation / 60.0;
        var hourAngle = (solarTime - 12.0) * 15.0 * DegToRad;
        var latitude = latitudeDeg * DegToRad;
        // The direction to the sun in east, north and up components.
        var east = -Math.Cos(declination) * Math.Sin(hourAngle);
        var north = Math.Sin(declination) * Math.Cos(latitude) - Math.Cos(declination) * Math.Cos(hourAngle) * Math.Sin(latitude);
        var up = Math.Sin(declination) * Math.Sin(latitude) + Math.Cos(declination) * Math.Cos(hourAngle) * Math.Cos(latitude);
        var elevation = Math.Asin(Math.Clamp(up, -1.0, 1.0)) / DegToRad;
        var bearing = Math.Atan2(east, north) / DegToRad;
        return ((float)elevation, (float)((bearing % 360.0 + 360.0) % 360.0));
    }

    public static (float Sunrise, float Sunset) SunTimes(SunTuning sun, int dayOfYear) =>
        SunTimes(sun.LatitudeDeg, sun.SolarNoonH, sun.SunriseElevationDeg, dayOfYear);

    /// <summary>
    /// Sunrise and sunset on the clock (local standard time): when the sun's centre crosses sunriseElevationDeg. In a
    /// polar day the sun never sets (sunrise is 12 h before true noon, sunset 12 h after); in a polar night it never rises.
    /// </summary>
    public static (float Sunrise, float Sunset) SunTimes(float latitudeDeg, float solarNoonH, float sunriseElevationDeg, int dayOfYear)
    {
        var (declination, equation) = SolarTerms(dayOfYear, 12.0);
        var latitude = latitudeDeg * DegToRad;
        var cosHalfDay = (Math.Sin(sunriseElevationDeg * DegToRad) - Math.Sin(latitude) * Math.Sin(declination)) / (Math.Cos(latitude) * Math.Cos(declination));
        var halfDayH = cosHalfDay <= -1.0 ? 12.0 : cosHalfDay >= 1.0 ? 0.0 : Math.Acos(cosHalfDay) / DegToRad / 15.0;
        var noon = solarNoonH - equation / 60.0;
        return ((float)(noon - halfDayH), (float)(noon + halfDayH));
    }

    /// <summary>Hours from sunrise to sunset on a day of the year, from the solar model.</summary>
    public static float DayLength(StylePreset preset, int dayOfYear)
    {
        var (rise, set) = SunTimes(preset.Tuning.Sun, dayOfYear);
        return set - rise;
    }

    /// <summary>Godot's yaw for a compass bearing in a room whose -Z axis faces negZBearingDeg (yaw 0 is light from +Z, yaw -90 from -X).</summary>
    public static float Yaw(SunTuning sun, float bearingDeg) => WrapDegrees(180f - (bearingDeg - sun.NegZBearingDeg));

    /// <summary>
    /// The hour on the keys' reference day that a real hour maps to: the real sunrise onto the keys' sunrise, the real
    /// sunset onto the keys' sunset, linearly through the day and through the night. Continuous and increasing. In
    /// "fixed" key mode (no sun) the keys are read by the clock directly.
    /// </summary>
    public static float KeyHour(StylePreset preset, float hour, int dayOfYear)
    {
        hour = Mathf.PosMod(hour, 24f);
        if (!preset.TimeOfDayEnabled || preset.KeyMode != "sun" || preset.TimeKeys.Count < 2) return hour;
        var (rise, set) = DayLimits(preset);
        var reference = set - rise;
        var (realRise, realSet) = SunTimes(preset.Tuning.Sun, dayOfYear);
        var length = Mathf.Clamp(realSet - realRise, 1f, 23f);
        var start = (realRise + realSet) * 0.5f - length * 0.5f;
        var sinceRise = Mathf.PosMod(hour - start, 24f);
        if (sinceRise <= length) return Mathf.PosMod(rise + sinceRise * reference / length, 24f);
        return Mathf.PosMod(set + (sinceRise - length) * (24f - reference) / (24f - length), 24f);
    }

    /// <summary>
    /// How much of the night look applies at a daylight level: none at or above x_look_grade.night_none_above (the
    /// golden hour stays warm), all of it at or below night_full_below, linear between.
    /// </summary>
    public static float NightAmount(StylePreset preset, float daylight)
    {
        var g = preset.Tuning.Grade;
        return Mathf.Clamp((g.NightNoneAbove - daylight) / (g.NightNoneAbove - g.NightFullBelow), 0f, 1f);
    }

    // ---------- the clock ----------

    /// <summary>
    /// A deterministic clock for reviews and playtests: --look-clock=HH:MM and --look-date=YYYY-MM-DD after "--" on
    /// the command line, or the ENFRACTAL_LOOK_CLOCK and ENFRACTAL_LOOK_DATE environment variables. Either may be
    /// given alone; the other then follows the preset (the real clock or calendar). A pinned clock is local standard
    /// time, so a pin means the same sun on every machine. Returns null when neither is set, and an error message for
    /// a value it cannot read (the look then warns and follows the preset).
    /// </summary>
    public static (float? Hour, int? DayOfYear, string Error) ClockOverride(IReadOnlyList<string> userArgs, Func<string, string?> environment)
    {
        var clock = Find(userArgs, environment, "look-clock", "ENFRACTAL_LOOK_CLOCK");
        var date = Find(userArgs, environment, "look-date", "ENFRACTAL_LOOK_DATE");
        float? hour = null;
        int? day = null;
        var errors = new List<string>();
        if (clock != null)
        {
            if (TimeSpan.TryParseExact(clock, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time) && time.TotalHours < 24)
                hour = (float)time.TotalHours;
            else errors.Add($"look clock '{clock}' is not HH:MM");
        }
        if (date != null)
        {
            if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) day = parsed.DayOfYear;
            else errors.Add($"look date '{date}' is not YYYY-MM-DD");
        }
        return (hour, day, string.Join("; ", errors));
    }

    /// <summary>
    /// Room lamps pinned on or off for reviews and playtests: --look-lamps=on|off|auto after "--", or ENFRACTAL_LOOK_LAMPS.
    /// Null (with no error) means the lamps switch themselves; an unreadable value is reported and ignored.
    /// </summary>
    public static (bool? LampsOn, string Error) LampsOverride(IReadOnlyList<string> userArgs, Func<string, string?> environment)
    {
        var value = Find(userArgs, environment, "look-lamps", "ENFRACTAL_LOOK_LAMPS");
        return value?.ToLowerInvariant() switch
        {
            null or "auto" => (null, ""),
            "on" => (true, ""),
            "off" => (false, ""),
            _ => (null, $"look lamps '{value}' is not on, off or auto"),
        };
    }

    private static string? Find(IReadOnlyList<string> userArgs, Func<string, string?> environment, string flag, string variable)
    {
        foreach (var argument in userArgs)
            if (argument.StartsWith("--" + flag + "=", StringComparison.Ordinal)) return argument[(flag.Length + 3)..];
        var value = environment(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Where "now" comes from: the machine's clock, as a UTC instant, read nowhere else in the look. Tests replace it to prove the
    /// look follows real time, and put it back. WorldTime takes the instant it is given and never calls this.
    /// </summary>
    public static Func<DateTime> UtcClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The real clock now, read as the preset asks: local standard time when it removes daylight saving. The machine's own clock, today's look.</summary>
    public static (float Hour, int DayOfYear) Now(StylePreset preset) => Now(preset, null);

    /// <summary>
    /// The world's hour and day now. With no clock record, or one with day_scale 1 and no phase, this is the machine's clock exactly
    /// as it has always been read; otherwise it is the island's clock (WorldTime), whose day is the one the sun and the seasons read.
    /// </summary>
    public static (float Hour, int DayOfYear) Now(StylePreset preset, WorldClock? clock)
    {
        var moment = WorldTime.At(UtcClock(), clock, BasisFor(preset, WorldTime.DefaultDawnH));
        return (moment.Hour, moment.SeasonDay);
    }

    /// <summary>
    /// The whole reading of the world's clock now, with the game day rolling over at the sun's own dawn on the day it is (the sunrise of
    /// the solar model, in world hours), so a game day is dawn to dawn.
    /// </summary>
    public static WorldMoment WorldNow(StylePreset preset, WorldClock? clock)
    {
        var utc = UtcClock();
        var first = WorldTime.At(utc, clock, BasisFor(preset, WorldTime.DefaultDawnH));
        var (sunrise, _) = SunTimes(preset.Tuning.Sun, first.SeasonDay);
        return WorldTime.At(utc, clock, BasisFor(preset, sunrise));
    }

    /// <summary>What reading the clock needs from the room and the preset: the machine's zone, the preset's standard-time rule, the room's hemisphere.</summary>
    public static WorldBasis BasisFor(StylePreset preset, float dawnH) =>
        new(TimeZoneInfo.Local, preset.Tuning.Sun.RealClockDaylightSaving, preset.Hemisphere, dawnH);

    /// <summary>
    /// A local clock reading as the solar model wants it: with removeDaylightSaving, an hour (the zone's saving) earlier
    /// while the zone is in daylight saving, so the sun stays on standard time; otherwise as it reads.
    /// </summary>
    public static (float Hour, int DayOfYear) StandardClock(DateTime local, TimeZoneInfo zone, bool removeDaylightSaving)
    {
        var reading = WorldTime.StandardTime(local, zone, removeDaylightSaving);
        return ((float)reading.TimeOfDay.TotalHours, reading.DayOfYear);
    }

    // ---------- the time keys ----------

    private static float MaxEnergy(StylePreset preset)
    {
        var max = 0f;
        foreach (var k in preset.TimeKeys) max = Mathf.Max(max, k.KeyEnergy);
        return max;
    }

    private static float MinEnergy(StylePreset preset)
    {
        var min = float.PositiveInfinity;
        foreach (var k in preset.TimeKeys) min = Mathf.Min(min, k.KeyEnergy);
        return float.IsFinite(min) ? min : 0f;
    }

    /// <summary>Interpolate time-of-day keys, wrapping from the last key of the evening to the first of the morning.</summary>
    public static TimeKey Sample(IReadOnlyList<TimeKey> keys, float hour)
    {
        hour = Mathf.PosMod(hour, 24f);
        if (keys.Count == 1) return keys[0] with { Hour = hour };
        TimeKey a, b;
        var h = hour;
        var first = keys[0];
        var last = keys[^1];
        if (hour >= last.Hour || hour < first.Hour)
        {
            (a, b) = (last, first with { Hour = first.Hour + 24f });
            if (h < a.Hour) h += 24f;
        }
        else
        {
            var i = 0;
            while (!(hour >= keys[i].Hour && hour < keys[i + 1].Hour)) i++;
            (a, b) = (keys[i], keys[i + 1]);
        }
        var t = (h - a.Hour) / (b.Hour - a.Hour);
        t = t * t * (3f - 2f * t);
        return new TimeKey(hour, a.KeyColor.Lerp(b.KeyColor, t), Mathf.Lerp(a.KeyEnergy, b.KeyEnergy, t),
            a.AmbientColor.Lerp(b.AmbientColor, t), Mathf.Lerp(a.AmbientEnergy, b.AmbientEnergy, t));
    }

    /// <summary>
    /// The keys' own sunrise and sunset: where their daylight rises through and falls back through
    /// x_look_sun.reference_daylight, found by sampling each minute. Without a clear day (keys never cross), 06:00 to 18:00.
    /// </summary>
    public static (float Sunrise, float Sunset) DayLimits(StylePreset preset)
    {
        var limits = LimitsCache.GetValue(preset, p => { var (rise, set) = ComputeDayLimits(p); return Tuple.Create(rise, set); });
        return (limits.Item1, limits.Item2);
    }

    private static (float Sunrise, float Sunset) ComputeDayLimits(StylePreset preset)
    {
        var max = MaxEnergy(preset);
        var threshold = preset.Tuning.Sun.ReferenceDaylight;
        if (max <= 0f) return (6f, 18f);
        float? rise = null, set = null;
        var previous = Sample(preset.TimeKeys, 0f).KeyEnergy / max;
        for (var step = 1; step <= SampleSteps; step++)
        {
            var hour = step * 24f / SampleSteps;
            var current = Sample(preset.TimeKeys, hour).KeyEnergy / max;
            if (previous < threshold && current >= threshold && rise == null) rise = hour;
            if (previous >= threshold && current < threshold && rise != null) set = hour;
            previous = current;
        }
        return rise is { } r && set is { } s && s > r ? (r, s) : (6f, 18f);
    }

    /// <summary>Unit vector the light comes from, for an elevation and a Godot yaw in degrees (-Z forward, +Y up).</summary>
    public static Vector3 Direction(float elevationDeg, float azimuthDeg)
    {
        var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(-elevationDeg), Mathf.DegToRad(azimuthDeg), 0f));
        return basis.Z.Normalized();
    }

    /// <summary>Elevation and Godot yaw of a direction the light comes from.</summary>
    public static (float Elevation, float Azimuth) Angles(Vector3 from)
    {
        var elevation = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(from.Y, -1f, 1f)));
        var azimuth = Mathf.RadToDeg(Mathf.Atan2(from.X, from.Z));
        return (elevation, azimuth);
    }

    private static Vector3 Slerp(Vector3 a, Vector3 b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        var dot = Mathf.Clamp(a.Dot(b), -1f, 1f);
        if (dot > 0.9995f) return a.Lerp(b, t).Normalized();
        // Nearly opposite directions: pass over the top of the sky rather than through the floor.
        if (dot < -0.9995f) return (a + Vector3.Up * 0.01f).Normalized().Slerp(b, t);
        return a.Slerp(b, t);
    }

    private static float WrapDegrees(float degrees) => Mathf.PosMod(degrees + 180f, 360f) - 180f;

    // ---------- seasons ----------

    /// <summary>The season whose mid-point is nearest the day, with the default season centres.</summary>
    public static string SeasonAt(int dayOfYear, string hemisphere) => SeasonAt(dayOfYear, hemisphere, LookTuning.Default.Seasons);

    public static string SeasonAt(int dayOfYear, string hemisphere, SeasonTuning seasons)
    {
        var (a, b, t) = Blend(dayOfYear, hemisphere, seasons);
        return t < 0.5f ? a : b;
    }

    public static (string From, string To, float T) Blend(int dayOfYear, string hemisphere) => Blend(dayOfYear, hemisphere, LookTuning.Default.Seasons);

    private static string[] SeasonNames => LookTuning.SeasonNames;

    /// <summary>The two seasons a day sits between and how far it is from the first towards the second.</summary>
    public static (string From, string To, float T) Blend(int dayOfYear, string hemisphere, SeasonTuning seasons)
    {
        var day = Mathf.PosMod(dayOfYear - (hemisphere == "south" ? 182 : 0) - 1, 365) + 1;
        var centres = seasons.CentreDays;
        for (var i = 0; i < centres.Count; i++)
        {
            var fromDay = centres[i];
            var toDay = centres[(i + 1) % centres.Count];
            var end = toDay > fromDay ? toDay : toDay + 365;
            var d = day >= fromDay ? day : day + 365;
            if (d >= fromDay && d < end) return (SeasonNames[i], SeasonNames[(i + 1) % 4], (float)(d - fromDay) / (end - fromDay));
        }
        return ("winter", "winter", 0f);
    }

    /// <summary>
    /// The seasons a day sits between and how far the cross-fade has gone from the first to the second: each season held around its
    /// middle (the preset's hold) and faded smoothly in between. The grade, the season's look and the weights all read this one blend.
    /// </summary>
    public static (string From, string To, float T, float S) Cross(StylePreset preset, int dayOfYear)
    {
        var seasons = preset.Tuning.Seasons;
        var (from, to, t) = Blend(dayOfYear, preset.Hemisphere, seasons);
        return (from, to, t, Mathf.SmoothStep(seasons.Hold, 1f - seasons.Hold, t));
    }

    /// <summary>How much of each season a day holds (the cross-fade the grade uses), summing to one.</summary>
    public static SeasonWeights WeightsAt(StylePreset preset, int dayOfYear)
    {
        var (from, to, _, s) = Cross(preset, dayOfYear);
        return SeasonWeights.Between(from, to, s);
    }

    private static (string Season, float Saturation, float Warmth, Color Tint) Grade(StylePreset preset, int dayOfYear)
    {
        // Hold each season's grade around its middle and cross-fade in between.
        var (from, to, t, s) = Cross(preset, dayOfYear);
        var a = preset.SeasonGrades[from];
        var b = preset.SeasonGrades[to];
        return (t < 0.5f ? from : to, Mathf.Lerp(a.Saturation, b.Saturation, s), Mathf.Lerp(a.Warmth, b.Warmth, s), a.Tint.Lerp(b.Tint, s));
    }

    /// <summary>What the season does to the sun and sky on a day: its look, held around the season's middle and cross-faded between seasons like the grade.</summary>
    public static SeasonLook SeasonLookAt(StylePreset preset, int dayOfYear)
    {
        var seasons = preset.Tuning.Seasons;
        var (from, to, _, s) = Cross(preset, dayOfYear);
        var a = seasons.Looks[from];
        var b = seasons.Looks[to];
        return new SeasonLook(Mathf.Lerp(a.SunEnergy, b.SunEnergy, s), Mathf.Lerp(a.SunBlur, b.SunBlur, s), Mathf.Lerp(a.SkySaturation, b.SkySaturation, s),
            Mathf.Lerp(a.SkyBrightness, b.SkyBrightness, s), Mathf.Lerp(a.CloudAmount, b.CloudAmount, s));
    }

    /// <summary>How a season's tint colours the key light: its hue at the given strength, lightness kept.</summary>
    public static Color SeasonLight(Color seasonTint, float strength)
    {
        var luma = Mathf.Max(ColorGrade.Luma(seasonTint), 1e-3f);
        var normalized = new Color(seasonTint.R / luma, seasonTint.G / luma, seasonTint.B / luma);
        return new Color(1f, 1f, 1f).Lerp(normalized, strength);
    }
}
