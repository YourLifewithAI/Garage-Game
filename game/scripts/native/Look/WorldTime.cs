using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Look;

/// <summary>
/// The island's clock record: the room state's extensions.x_clock (contracts/room-state.schema.json, $defs/x_clock;
/// docs/game/MECHANICS.md section 9, decision 15). It lives in the room state, never in the pinned manifest, and a
/// change to it starts a fresh save. Absent, or day_scale 1 with a phase of 0 (IsRealClock): the machine's own clock,
/// which is today's behaviour, and then the zone and the hemisphere are not applied either. Live will use day_scale 4.
/// Everything here is data: WorldTime reads it, and nothing else in the look reads the time.
/// </summary>
public sealed class WorldClock
{
    /// <summary>Game days per real day: divisors of 24, so a day is a whole number of real hours and nothing drifts.</summary>
    public static readonly IReadOnlyList<int> DayScales = new[] { 1, 2, 3, 4, 6, 8, 12, 24 };

    private static readonly Regex ZoneIdPattern = new(@"^[A-Za-z][A-Za-z0-9_+-]{0,31}(/[A-Za-z0-9_+-]{1,31}){0,2}$", RegexOptions.CultureInvariant);
    private static readonly string[] Keys = { "zone_id", "day_scale", "seasons_per_year", "phase_h", "hemisphere" };

    /// <summary>The world owner's time zone, an IANA id (Etc/UTC, Europe/Paris). A zone, never a place.</summary>
    public string ZoneId { get; }
    public TimeZoneInfo Zone { get; }
    public int DayScale { get; }
    /// <summary>Seasons follow the real calendar: 4 is the look's four seasons of about 91 days each.</summary>
    public int SeasonsPerYear { get; }
    /// <summary>An offset in game hours, so an evening-only child still meets night.</summary>
    public double PhaseH { get; }
    /// <summary>Which half of the year is summer: "north" or "south".</summary>
    public string Hemisphere { get; }

    /// <summary>Day scale 1 and no phase: the machine's own clock and calendar, the look exactly as it was before the record existed.</summary>
    public bool IsRealClock => DayScale == 1 && PhaseH == 0;

    private WorldClock(string zoneId, TimeZoneInfo zone, int dayScale, int seasonsPerYear, double phaseH, string hemisphere)
    {
        ZoneId = zoneId;
        Zone = zone;
        DayScale = dayScale;
        SeasonsPerYear = seasonsPerYear;
        PhaseH = phaseH;
        Hemisphere = hemisphere;
    }

    /// <summary>
    /// Checks the record the way the schema does and builds it. Null with a message (naming the field) for anything the
    /// schema refuses or a zone this machine does not know; zone lets a caller supply the zone it already resolved.
    /// </summary>
    public static WorldClock? TryCreate(string zoneId, int dayScale, int seasonsPerYear, double phaseH, string hemisphere, out string error, TimeZoneInfo? zone = null)
    {
        error = "";
        if (!ZoneIdPattern.IsMatch(zoneId ?? "") || zoneId!.Length > 64) { error = "x_clock.zone_id is not an IANA zone id"; return null; }
        if (!DayScales.Contains(dayScale)) { error = $"x_clock.day_scale {dayScale} is not a divisor of 24 (1, 2, 3, 4, 6, 8, 12 or 24)"; return null; }
        if (seasonsPerYear is < 1 or > 12) { error = "x_clock.seasons_per_year must be from 1 to 12"; return null; }
        if (!double.IsFinite(phaseH) || phaseH is < -12 or > 12) { error = "x_clock.phase_h must be from -12 to 12"; return null; }
        if (hemisphere is not ("north" or "south")) { error = "x_clock.hemisphere must be north or south"; return null; }
        if (zone == null)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
            catch (Exception problem) when (problem is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                error = $"x_clock.zone_id '{zoneId}' is not a time zone this machine knows";
                return null;
            }
        }
        return new WorldClock(zoneId, zone, dayScale, seasonsPerYear, phaseH, hemisphere);
    }

    /// <summary>
    /// Reads the room state's extensions.x_clock (a JsonObject from the kernel's reader). Null with no error when there is
    /// none; null with a message when it is there and wrong (the look then warns and keeps the machine's clock). Every key is
    /// required and no other is allowed, as in the schema.
    /// </summary>
    public static WorldClock? FromNode(JsonNode? node, out string error)
    {
        error = "";
        if (node == null) return null;
        if (node is not JsonObject record) { error = "x_clock must be an object"; return null; }
        foreach (var key in record.Select(p => p.Key))
            if (!Keys.Contains(key)) { error = $"x_clock.{key} is not allowed"; return null; }
        foreach (var key in Keys)
            if (!record.ContainsKey(key)) { error = $"x_clock.{key} is required"; return null; }
        if (!Text(record["zone_id"], out var zoneId)) { error = "x_clock.zone_id must be a string"; return null; }
        if (!Text(record["hemisphere"], out var hemisphere)) { error = "x_clock.hemisphere must be a string"; return null; }
        if (!Number(record["day_scale"], out var scale) || scale != Math.Floor(scale)) { error = "x_clock.day_scale must be a whole number"; return null; }
        if (!Number(record["seasons_per_year"], out var seasons) || seasons != Math.Floor(seasons)) { error = "x_clock.seasons_per_year must be a whole number"; return null; }
        // The founder, 15 October: four seasons a year only, for now (the contract's const); the mapping below stays general.
        if (seasons != 4) { error = "x_clock.seasons_per_year must be 4 for now"; return null; }
        if (!Number(record["phase_h"], out var phase)) { error = "x_clock.phase_h must be a number"; return null; }
        return TryCreate(zoneId, (int)Math.Clamp(scale, -1, 1000), (int)Math.Clamp(seasons, -1, 1000), phase, hemisphere, out error);
    }

    private static bool Text(JsonNode? node, out string value)
    {
        value = "";
        if (node is JsonValue v && v.TryGetValue<string>(out var text)) { value = text; return true; }
        return false;
    }

    private static bool Number(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out value)) return double.IsFinite(value);
        if (v.TryGetValue<int>(out var whole)) { value = whole; return true; }
        if (v.TryGetValue<long>(out var wide)) { value = wide; return true; }
        return false;
    }
}

/// <summary>
/// What the room and the preset say about reading the clock, which the record does not carry: the machine's own zone (the
/// clock of a world with no record), whether the real clock is read as standard time (the preset's real_clock_daylight_saving),
/// the room's hemisphere (the room's latitude, the half of the year its sun already follows), and the hour the game day starts.
/// </summary>
public readonly record struct WorldBasis(TimeZoneInfo MachineZone, bool RemoveDaylightSaving, string SiteHemisphere = "north", float DawnH = WorldTime.DefaultDawnH);

/// <summary>
/// One reading of the world's clock. Hour is the world's hour of day (0 up to 24), the hour the sun and the sky are drawn for.
/// DayOfYear is the real calendar day in the zone. SeasonDay is the day the sun and the seasons read: the calendar day
/// carried through seasons_per_year and the hemisphere (the same day when neither changes anything). GameDay counts game
/// days and rolls over at dawn, not at midnight.
/// </summary>
public readonly record struct WorldMoment(float Hour, int DayOfYear, int SeasonDay, long GameDay);

/// <summary>
/// WorldTime(utcNow, clock): the one place the world's time of day and season come from (docs/game/MECHANICS.md section 9). It is
/// pure: it takes the UTC instant and the record and never reads the machine's clock (the look reads it once, in
/// LookClock, and hands the instant in), so a test pins an instant and gets the same answer on every machine.
///
/// With no record, or day_scale 1 and no phase, it is the machine's clock read as the look has always read it (the preset's
/// standard-time rule included). Otherwise the world follows the owner's zone:
/// - day_scale 1: the real clock of the zone (standard time when the preset says so), moved by the phase.
/// - day_scale above 1: the zone's wall clock (daylight saving as the zone has it) times the scale, moved by the phase, so with
///   day_scale 4 midnight is at 00:00, 06:00, 12:00 and 18:00 on the zone's clock all year, and nothing drifts.
/// The date is always the zone's real calendar date: seasons follow the real calendar. The game day rolls over at dawn.
/// </summary>
public static class WorldTime
{
    /// <summary>The hour the game day starts when the caller does not give the sun's own sunrise.</summary>
    public const float DefaultDawnH = 6f;

    private const int HalfYearDays = 182;

    public static WorldMoment At(DateTime utcNow, WorldClock? clock, WorldBasis basis)
    {
        var utc = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var real = clock == null || clock.IsRealClock;
        var zone = real ? basis.MachineZone : clock!.Zone;
        var scale = real ? 1 : clock!.DayScale;
        var phase = real ? 0.0 : clock!.PhaseH;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var reading = scale == 1 ? StandardTime(local, zone, basis.RemoveDaylightSaving) : local;
        var clockHours = reading.TimeOfDay.TotalHours;
        var hour = real ? (float)clockHours : (float)Wrap(clockHours * scale + phase, 24.0);
        var day = reading.DayOfYear;
        var seasonDay = real ? day : SeasonDayOf(day, clock!, basis.SiteHemisphere);
        var sinceDawn = clockHours * scale + phase - basis.DawnH;
        var gameDay = reading.Date.Ticks / TimeSpan.TicksPerDay * scale + (long)Math.Floor(sinceDawn / 24.0);
        return new WorldMoment(hour, day, seasonDay, gameDay);
    }

    /// <summary>
    /// The day the sun and the seasons read: the calendar day carried through seasons_per_year (4 is the look's four seasons, a
    /// day for a day; another count runs the same four seasons faster or slower), then half a year away when the clock's
    /// hemisphere is not the room's, so a summer is a long, high-sun day wherever the clock says it is summer.
    /// </summary>
    public static int SeasonDayOf(int dayOfYear, WorldClock clock, string siteHemisphere)
    {
        var day = dayOfYear;
        if (clock.SeasonsPerYear != 4) day = (int)((long)(dayOfYear - 1) * clock.SeasonsPerYear / 4 % 365) + 1;
        if (clock.Hemisphere != siteHemisphere) day = (day - 1 + HalfYearDays) % 365 + 1;
        return day;
    }

    /// <summary>
    /// A local clock reading as the solar model wants it: with removeDaylightSaving, an hour (the zone's saving) earlier while the
    /// zone is in daylight saving, so the sun stays on standard time; otherwise as it reads.
    /// </summary>
    public static DateTime StandardTime(DateTime local, TimeZoneInfo zone, bool removeDaylightSaving)
    {
        var reading = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (removeDaylightSaving && zone.IsDaylightSavingTime(reading))
            reading -= zone.GetUtcOffset(reading) - zone.BaseUtcOffset;
        return reading;
    }

    private static double Wrap(double value, double period)
    {
        var wrapped = value % period;
        return wrapped < 0 ? wrapped + period : wrapped;
    }
}
