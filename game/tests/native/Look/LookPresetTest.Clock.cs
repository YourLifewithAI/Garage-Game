using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Look;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Look;

/// <summary>
/// The island's clock (the mechanics round, M6): WorldTime(utcNow, clock) and the x_clock record, tested as data. With no record, or
/// day_scale 1 and no phase, every reading is today's; Live's shape (day_scale 4) puts midnight at 00:00, 06:00, 12:00 and 18:00 on
/// the zone's clock with no drift, rolls the game day over at dawn, takes a phase offset, follows the owner's zone and its daylight
/// saving, runs the seasons on the real calendar and lets the hemisphere flip them. Every instant here is pinned.
/// `-- --worldclock-only` runs just these checks (a few seconds) for iterating.
/// </summary>
public partial class LookPresetTest
{
    private static readonly string[] MachineClockTokens =
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTime.Today", "DateTimeOffset.Now", "DateTimeOffset.UtcNow", "TimeZoneInfo.Local",
        "Stopwatch", "Environment.TickCount", "Time.Get", "OS.GetUnixTime", "GetSystemTime", "LookClock.UtcClock",
    };

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    /// <summary>Distance between two hours on the 24-hour circle.</summary>
    private static float Around(float a, float b)
    {
        var d = Mathf.Abs(a - b) % 24f;
        return Mathf.Min(d, 24f - d);
    }

    /// <summary>A zone with daylight saving from the last Sunday of March to the last Sunday of October, as the existing standard-time check builds it.</summary>
    private static TimeZoneInfo SummerTimeZone(string id, TimeSpan baseOffset) => TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id + " standard", id + " summer",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
        });

    private WorldClock Clock(TimeZoneInfo zone, int scale, double phase = 0, int seasons = 4, string hemisphere = "north")
    {
        var clock = WorldClock.TryCreate("Etc/UTC", scale, seasons, phase, hemisphere, out var error, zone);
        Check(clock != null, "a test clock record is valid: " + error);
        return clock!;
    }

    private void CheckWorldClock(StylePreset preset)
    {
        var utcZone = TimeZoneInfo.Utc;
        var summerZone = SummerTimeZone("look-summer", TimeSpan.Zero);
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("look-east", TimeSpan.FromHours(9), "look east", "look east");
        var site = preset.Hemisphere;

        // ---------- nothing changes until Live: today's reading for no record and for the default record ----------
        var instants = Enumerable.Range(0, 1216).Select(i => Utc(2026, 1, 1).AddMinutes(433.0 * i)).ToArray();
        // A record with day_scale 1 and no phase is inert: not even its zone, hemisphere or seasons are applied.
        var inert = WorldClock.TryCreate("Asia/Tokyo", 1, 8, 0, "south", out var inertError, tokyo);
        Check(inert is { IsRealClock: true } && inertError.Length == 0, "a record with day_scale 1 and no phase is the real clock");
        var drifted = 0;
        var seen = 0;
        foreach (var zone in new[] { utcZone, summerZone, tokyo })
            foreach (var removeSaving in new[] { true, false })
                foreach (var instant in instants)
                {
                    var wall = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
                    var today = LookClock.StandardClock(wall, zone, removeSaving);
                    var basis = new WorldBasis(zone, removeSaving, site);
                    foreach (var clock in new WorldClock?[] { null, inert })
                    {
                        var moment = WorldTime.At(instant, clock, basis);
                        seen++;
                        if (moment.Hour != today.Hour || moment.DayOfYear != today.DayOfYear || moment.SeasonDay != today.DayOfYear) drifted++;
                    }
                }
        Check(drifted == 0 && seen == 3 * 2 * 1216 * 2,
            $"with no clock record, or day_scale 1 and no phase, WorldTime reads exactly what the machine's clock always read: {seen} readings in 3 zones, with and without the standard-time rule, {drifted} differ");

        var saved = LookClock.UtcClock;
        try
        {
            var differ = 0;
            for (var i = 0; i < instants.Length; i += 7)
            {
                var instant = instants[i];
                LookClock.UtcClock = () => instant;
                var today = LookClock.StandardClock(TimeZoneInfo.ConvertTimeFromUtc(instant, TimeZoneInfo.Local), TimeZoneInfo.Local, preset.Tuning.Sun.RealClockDaylightSaving);
                var plain = LookClock.Now(preset);
                var withInert = LookClock.Now(preset, inert);
                var withNone = LookClock.Now(preset, null);
                if (plain != today || withInert != today || withNone != today) differ++;
                // And what is drawn from it: the same sun and the same sky.
                var drawn = LookClock.At(preset, plain.Hour, plain.DayOfYear);
                var again = LookClock.At(preset, today.Hour, today.DayOfYear);
                if (drawn != again || LookSky.At(preset, drawn, null) != LookSky.At(preset, again, null)) differ++;
            }
            Check(differ == 0, $"LookClock.Now with no record, with the default record and as before gives the same hour, day, sun and sky at {instants.Length / 7 + 1} pinned instants ({differ} differ)");

            // The look's one read of the machine's clock is the hook; WorldTime never calls it.
            LookClock.UtcClock = () => throw new InvalidOperationException("the machine's clock was read inside WorldTime");
            try
            {
                var moment = WorldTime.At(Utc(2026, 4, 15, 12), Clock(utcZone, 4), new WorldBasis(utcZone, true));
                Check(Around(moment.Hour, 0f) < 1e-4f, "WorldTime takes the instant it is given and does not call the machine's clock");
            }
            catch (InvalidOperationException) { Check(false, "WorldTime took the instant it is given without reading the machine's clock"); }
        }
        finally { LookClock.UtcClock = saved; }
        var source = FileAccess.GetFileAsString("res://scripts/native/Look/WorldTime.cs");
        var reads = MachineClockTokens.Where(source.Contains).ToArray();
        Check(source.Length > 1000 && reads.Length == 0, "WorldTime.cs holds no read of the machine's clock or zone: found " + string.Join(", ", reads));

        // ---------- Live's shape: day_scale 4 ----------
        var live = Clock(utcZone, 4);
        var basisUtc = new WorldBasis(utcZone, true, site);
        var midnights = 0;
        var offMidnight = new List<string>();
        for (var day = 0; day < 400; day++)
            foreach (var realHour in new[] { 0, 6, 12, 18 })
            {
                midnights++;
                var hour = WorldTime.At(Utc(2026, 3, 1).AddDays(day).AddHours(realHour), live, basisUtc).Hour;
                if (Around(hour, 0f) > 1e-4f) offMidnight.Add($"{realHour:00}:00 on day {day} reads {hour}");
            }
        Check(offMidnight.Count == 0, $"day_scale 4: midnight lands at 00:00, 06:00, 12:00 and 18:00 on the zone's clock, {midnights} times across 400 days ({string.Join("; ", offMidnight.Take(3))})");
        Check(Around(WorldTime.At(Utc(2026, 5, 1, 0, 15), live, basisUtc).Hour, 1f) < 1e-4f && Around(WorldTime.At(Utc(2026, 5, 1, 3), live, basisUtc).Hour, 12f) < 1e-4f
            && Around(WorldTime.At(Utc(2026, 5, 1, 9, 30), live, basisUtc).Hour, 14f) < 1e-4f,
            "a world hour is 15 real minutes: 00:15 is 01:00, 03:00 is noon and 09:30 is 14:00");
        var worst = 0f;
        for (var i = 0; i < 3000; i++)
        {
            var instant = Utc(2026, 1, 1).AddSeconds(i * 10_443.0 + 17);
            worst = Mathf.Max(worst, Around(WorldTime.At(instant, live, basisUtc).Hour, WorldTime.At(instant.AddHours(6), live, basisUtc).Hour));
        }
        Check(worst < 1e-3f, $"no drift: the same real moment six hours on reads the same world hour, 3000 instants in 12 months (worst {worst:0.######} h)");

        // ---------- the day rolls over at dawn ----------
        var changes = new List<(DateTime At, float Hour)>();
        var before = WorldTime.At(Utc(2026, 4, 15), live, basisUtc).GameDay;
        for (var minute = 1; minute <= 24 * 60; minute++)
        {
            var moment = WorldTime.At(Utc(2026, 4, 15).AddMinutes(minute), live, basisUtc);
            if (moment.GameDay != before) changes.Add((Utc(2026, 4, 15).AddMinutes(minute), moment.Hour));
            before = moment.GameDay;
        }
        Check(changes.Count == 4 && changes.All(c => c.Hour >= 6f && c.Hour < 6.07f) && changes.Select(c => (c.At - Utc(2026, 4, 15)).TotalMinutes).SequenceEqual(new[] { 90.0, 450.0, 810.0, 1170.0 }),
            $"the game day rolls over at dawn: four times a real day, each as the world hour passes 06:00, at {string.Join(", ", changes.Select(c => c.At.ToString("HH:mm")))}");
        Check(WorldTime.At(Utc(2026, 4, 14, 23, 59, 50), live, basisUtc).GameDay == WorldTime.At(Utc(2026, 4, 15, 0, 0, 10), live, basisUtc).GameDay
            && WorldTime.At(Utc(2026, 4, 15, 5, 59, 50), live, basisUtc).GameDay == WorldTime.At(Utc(2026, 4, 15, 6, 0, 10), live, basisUtc).GameDay,
            "the game day does not roll over at midnight, real or world");
        var rise = new WorldBasis(utcZone, true, site, 5.25f);
        Check(WorldTime.At(Utc(2026, 4, 15, 1, 18, 40), live, rise).GameDay + 1 == WorldTime.At(Utc(2026, 4, 15, 1, 19, 20), live, rise).GameDay,
            "the dawn is whatever hour the caller gives: 05:15 rolls it at 01:18:45");
        var sunrise = LookClock.SunTimes(preset.Tuning.Sun, 105).Sunrise;
        var sunriseAt = Utc(2026, 4, 15).AddHours(sunrise / 4.0);
        LookClock.UtcClock = () => sunriseAt.AddSeconds(-20);
        try
        {
            var early = LookClock.WorldNow(preset, live).GameDay;
            LookClock.UtcClock = () => sunriseAt.AddSeconds(20);
            Check(LookClock.WorldNow(preset, live).GameDay == early + 1, $"in the look the day rolls over at the sun's own sunrise on the day ({sunrise:0.00} h on 15 April)");
        }
        finally { LookClock.UtcClock = saved; }
        var calendar = new List<int>();
        for (var hour = 0; hour < 48; hour++) calendar.Add(WorldTime.At(Utc(2026, 4, 15).AddHours(hour), live, basisUtc).DayOfYear);
        Check(calendar.Take(24).All(d => d == 105) && calendar.Skip(24).All(d => d == 106), "the calendar day still turns at real midnight: seasons follow the real calendar, not the game day");

        // ---------- a phase offset ----------
        Check(Around(WorldTime.At(Utc(2026, 5, 1), Clock(utcZone, 4, 6), basisUtc).Hour, 6f) < 1e-4f
            && Around(WorldTime.At(Utc(2026, 5, 1, 4), Clock(utcZone, 4, -6), basisUtc).Hour, 10f) < 1e-4f
            && Around(WorldTime.At(Utc(2026, 5, 1, 12), Clock(utcZone, 1, -3), new WorldBasis(utcZone, false, site)).Hour, 9f) < 1e-4f
            && Around(WorldTime.At(Utc(2026, 5, 1, 18), Clock(utcZone, 1, 12), new WorldBasis(utcZone, false, site)).Hour, 6f) < 1e-4f
            && Around(WorldTime.At(Utc(2026, 5, 1, 18), Clock(utcZone, 1, -12), new WorldBasis(utcZone, false, site)).Hour, 6f) < 1e-4f,
            "a phase offset shifts the world hour, wrapping round the day: 6 h at midnight, -6 h at 04:00, -3 h at noon, plus or minus 12 h at 18:00");
        Check(WorldTime.At(Utc(2026, 4, 14, 23, 59), Clock(utcZone, 4, 6), basisUtc).GameDay + 1 == WorldTime.At(Utc(2026, 4, 15, 0, 1), Clock(utcZone, 4, 6), basisUtc).GameDay
            && WorldTime.At(Utc(2026, 4, 15, 1, 29), Clock(utcZone, 4, 6), basisUtc).GameDay == WorldTime.At(Utc(2026, 4, 15, 1, 31), Clock(utcZone, 4, 6), basisUtc).GameDay,
            "with a phase the game day still rolls over at dawn: the dawn of a +6 h clock is at real 00:00, not 01:30");
        // A child who only plays 18:00 to 20:00 meets noon, dusk and night in two hours with a phase, where without one it is night, dawn and morning.
        var evening = Enumerable.Range(0, 8).Select(i => WorldTime.At(Utc(2026, 5, 1, 18).AddMinutes(15 * i), Clock(utcZone, 4, 12), basisUtc).Hour).ToArray();
        Check(Around(evening[0], 12f) < 1e-3f && Around(evening[4], 16f) < 1e-3f, "a +12 h phase puts an evening-only child's 18:00 at noon, so the light is not the same night every evening");

        // ---------- the owner's zone and its daylight saving ----------
        var inTokyo = Clock(tokyo, 4);
        Check(Around(WorldTime.At(Utc(2026, 5, 1, 12), inTokyo, basisUtc).Hour, 12f) < 1e-4f && Around(WorldTime.At(Utc(2026, 5, 1, 12), live, basisUtc).Hour, 0f) < 1e-4f,
            "the world follows the owner's zone: at 12:00 UTC it is 21:00 in a UTC+9 zone (world noon) and midnight in UTC");
        Check(WorldTime.At(Utc(2026, 12, 31, 20), inTokyo, basisUtc).DayOfYear == 1 && WorldTime.At(Utc(2026, 12, 31, 20), live, basisUtc).DayOfYear == 365,
            "the calendar follows the owner's zone: 20:00 UTC on 31 December is already 1 January in a UTC+9 zone");
        var machineElsewhere = new WorldBasis(tokyo, true, site);
        Check(WorldTime.At(Utc(2026, 5, 1, 12), live, machineElsewhere).Hour == WorldTime.At(Utc(2026, 5, 1, 12), live, basisUtc).Hour,
            "the machine's own zone does not matter once the record names one");
        var inSummer = Clock(summerZone, 4);
        var wallBasis = new WorldBasis(utcZone, true, site);
        var summerMidnights = new[] { Utc(2026, 7, 14, 23), Utc(2026, 7, 15, 5), Utc(2026, 7, 15, 11), Utc(2026, 7, 15, 17), Utc(2026, 1, 15), Utc(2026, 1, 15, 6), Utc(2026, 1, 15, 12), Utc(2026, 1, 15, 18) };
        Check(summerMidnights.All(t => Around(WorldTime.At(t, inSummer, wallBasis).Hour, 0f) < 1e-4f),
            "daylight saving follows the zone: midnight stays on 00:00, 06:00, 12:00 and 18:00 of the zone's wall clock in summer time and in winter time");
        var leap = WorldTime.At(Utc(2026, 3, 29, 1, 0, 1), inSummer, wallBasis).Hour - WorldTime.At(Utc(2026, 3, 29, 0, 59, 59), inSummer, wallBasis).Hour;
        Check(Mathf.Abs(leap - 4f) < 0.01f, $"when the zone changes its clocks the world hour leaps with it, by four hours at day_scale 4 ({leap:0.000} h)");
        Check(Around(WorldTime.At(Utc(2026, 7, 15, 17, 30), Clock(summerZone, 1, 3), new WorldBasis(utcZone, true, site)).Hour, 20.5f) < 1e-4f
            && Around(WorldTime.At(Utc(2026, 7, 15, 17, 30), Clock(summerZone, 1, 3), new WorldBasis(utcZone, false, site)).Hour, 21.5f) < 1e-4f,
            "at day_scale 1 the real clock is still read as standard time when the preset says so, and the phase moves it");

        // ---------- seasons on the real calendar, and the hemisphere ----------
        var identity = Enumerable.Range(1, 366).All(d => WorldTime.SeasonDayOf(d, live, site) == d);
        Check(identity, "four seasons a year: the season day is the calendar day, all 366 of them");
        var runs = new List<int>();
        var last = LookClock.SeasonAt(1, "north", preset.Tuning.Seasons);
        var length = 0;
        for (var day = 1; day <= 365; day++)
        {
            var now = LookClock.SeasonAt(day, "north", preset.Tuning.Seasons);
            if (now == last) { length++; continue; }
            runs.Add(length);
            last = now;
            length = 1;
        }
        runs.Add(length);
        Check(runs.Count is 4 or 5 && runs.Skip(1).Take(3).All(r => r is >= 88 and <= 95), $"the seasons are about 91 real days each ({string.Join(", ", runs)})");
        var january = Utc(2026, 1, 15, 12);
        var north = LookClock.At(preset, 12f, WorldTime.At(january, Clock(utcZone, 4, 0, 4, site), basisUtc).SeasonDay);
        var flipped = Clock(utcZone, 4, 0, 4, site == "north" ? "south" : "north");
        var flipDay = WorldTime.At(january, flipped, basisUtc).SeasonDay;
        var south = LookClock.At(preset, 12f, flipDay);
        Check(north.Season == "winter" && south.Season == "summer" && flipDay != 15
            && LookClock.DayLength(preset, flipDay) > LookClock.DayLength(preset, 15) + 3f && south.KeyEnergy > 1.4f * north.KeyEnergy,
            $"the hemisphere flips the seasons: in January the room's own is {north.Season} and the flipped clock's {south.Season}, with a long high-sun day ({LookClock.DayLength(preset, flipDay):0.0} h against {LookClock.DayLength(preset, 15):0.0} h)");
        Check(WorldTime.At(january, Clock(utcZone, 4, 0, 4, site), basisUtc).SeasonDay == 15 && WorldTime.At(january, flipped, basisUtc).DayOfYear == 15,
            "a clock in the room's own hemisphere changes no day, and the calendar day itself is never shifted");
        Check(WorldTime.SeasonDayOf(100, Clock(utcZone, 4, 0, 8), site) == 199 && WorldTime.SeasonDayOf(365, Clock(utcZone, 4, 0, 1), site) == 92 && WorldTime.SeasonDayOf(1, Clock(utcZone, 4, 0, 12), site) == 1,
            "seasons_per_year runs the four seasons faster or slower on the same calendar (8 a year: day 100 reads as 199; 1 a year: day 365 as 92)");

        // ---------- the record ----------
        var good = WorldClock.FromNode(JsonNode.Parse("""{"zone_id":"Etc/UTC","day_scale":1,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}"""), out var goodError);
        Check(good is { IsRealClock: true, DayScale: 1, SeasonsPerYear: 4, Hemisphere: "north", ZoneId: "Etc/UTC" } && goodError.Length == 0,
            "the schema's example record (day_scale 1) is read: " + goodError);
        var liveRecord = WorldClock.FromNode(JsonNode.Parse("""{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":-2.5,"hemisphere":"south"}"""), out var liveError);
        Check(liveRecord is { IsRealClock: false, DayScale: 4, PhaseH: -2.5, Hemisphere: "south" } && liveError.Length == 0, "Live's record (day_scale 4, a phase, the south) is read: " + liveError);
        var made = WorldClock.FromNode(new JsonObject { ["zone_id"] = "Etc/UTC", ["day_scale"] = 4, ["seasons_per_year"] = 4, ["phase_h"] = 0, ["hemisphere"] = "north" }, out var madeError);
        Check(made is { DayScale: 4 } && madeError.Length == 0, "a record built in code, with whole numbers rather than parsed ones, is read too");
        Check(WorldClock.FromNode(null, out var absentError) == null && absentError.Length == 0, "no record is no clock and no error: today's clock");
        var refused = new (string Why, string Json, string Names)[]
        {
            ("a day that drifts", """{"zone_id":"Etc/UTC","day_scale":5,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}""", "day_scale"),
            ("a fractional scale", """{"zone_id":"Etc/UTC","day_scale":2.5,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}""", "day_scale"),
            ("a place for a zone", """{"zone_id":"../home/garage","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}""", "zone_id"),
            ("a zone nobody knows", """{"zone_id":"Nowhere/Land","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}""", "zone_id"),
            ("a compass for a hemisphere", """{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"east"}""", "hemisphere"),
            ("a phase past half a day", """{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":13,"hemisphere":"north"}""", "phase_h"),
            ("no seasons", """{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":0,"phase_h":0,"hemisphere":"north"}""", "seasons_per_year"),
            ("eight seasons a year (four only, for now)", """{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":8,"phase_h":0,"hemisphere":"north"}""", "seasons_per_year"),
            ("a key the schema does not have", """{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"north","place":"garage"}""", "place"),
            ("a missing key", """{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"hemisphere":"north"}""", "phase_h"),
            ("a string for a scale", """{"zone_id":"Etc/UTC","day_scale":"4","seasons_per_year":4,"phase_h":0,"hemisphere":"north"}""", "day_scale"),
            ("not an object", "[4]", "object"),
        };
        foreach (var (why, json, names) in refused)
        {
            var clock = WorldClock.FromNode(JsonNode.Parse(json), out var error);
            Check(clock == null && error.Contains(names, StringComparison.Ordinal), $"a record with {why} is refused, naming {names}: {error}");
        }
        var paris = WorldClock.TryCreate("Europe/Paris", 4, 4, 0, "north", out var parisError);
        if (paris != null)
            Check(Around(WorldTime.At(Utc(2026, 7, 14, 22), paris, basisUtc).Hour, 0f) < 1e-4f && Around(WorldTime.At(Utc(2026, 1, 14, 23), paris, basisUtc).Hour, 0f) < 1e-4f,
                "a real IANA zone (Europe/Paris) resolves, with its summer time: midnight at 00:00 Paris time in July (22:00 UTC) and in January (23:00 UTC)");
        else Check(false, "Europe/Paris resolves on this machine: " + parisError);
        var example = System.IO.Path.GetFullPath(ProjectSettings.GlobalizePath("res://") + "../contracts/examples/room_state/garage_example_state_mechanics.json");
        if (System.IO.File.Exists(example))
        {
            var state = JsonNode.Parse(System.IO.File.ReadAllText(example))!;
            var fromFile = WorldClock.FromNode(state["extensions"]?["x_clock"], out var fileError);
            Check(fromFile is { IsRealClock: true } && fileError.Length == 0, "the contract's own example room state (extensions.x_clock) is read as the real clock: " + fileError);
        }
    }

    /// <summary>The director reads the island's clock instead of the machine's, and a pin or a missing record changes nothing about it.</summary>
    private async Task CheckWorldClockInTheLook(StylePreset preset, GarageGame.Native.Room.RoomData room)
    {
        var saved = LookClock.UtcClock;
        var savedClock = OS.GetEnvironment("GARAGE_GAME_LOOK_CLOCK");
        var savedDate = OS.GetEnvironment("GARAGE_GAME_LOOK_DATE");
        OS.UnsetEnvironment("GARAGE_GAME_LOOK_CLOCK");
        OS.UnsetEnvironment("GARAGE_GAME_LOOK_DATE");
        try
        {
            var instant = Utc(2026, 4, 15, 18);
            LookClock.UtcClock = () => instant;
            var today = LookClock.StandardClock(TimeZoneInfo.ConvertTimeFromUtc(instant, TimeZoneInfo.Local), TimeZoneInfo.Local, preset.Tuning.Sun.RealClockDaylightSaving);
            var expected = LookClock.At(preset, today.Hour, today.DayOfYear);
            var (plainHolder, plain) = NewDirector(preset, room, "NoClockRecordHolder");
            var (inertHolder, inert) = NewDirector(preset, room, "DefaultClockRecordHolder");
            Check(inert.SetWorldClockFrom(JsonNode.Parse("""{"zone_id":"Asia/Tokyo","day_scale":1,"seasons_per_year":4,"phase_h":0,"hemisphere":"south"}""")).Length == 0,
                "the default record is accepted by the look");
            Check(plain.Moment == inert.Moment && plain.Moment.Hour == expected.Hour && plain.Moment.DayOfYear == expected.DayOfYear && plain.Moment.KeyEnergy == expected.KeyEnergy
                && plain.Moment.KeyElevationDeg == expected.KeyElevationDeg && plain.Moment.Season == expected.Season,
                "the look with no record and the look with the default record show the same moment as the machine's clock gives");
            Check(plain.ClockNote == inert.ClockNote && plain.ClockNote.StartsWith("real clock, real calendar", StringComparison.Ordinal)
                && plain.Key.LightEnergy == inert.Key.LightEnergy && plain.Key.RotationDegrees == inert.Key.RotationDegrees
                && plain.Environment.AmbientLightEnergy == inert.Environment.AmbientLightEnergy && plain.Lamps.Count == inert.Lamps.Count && plain.LampsOn == inert.LampsOn,
                "and the same key light, ambient, lamps and note: " + inert.ClockNote);
            plainHolder.QueueFree();

            // Live: four game days a real day, on the owner's zone.
            Check(inert.SetWorldClockFrom(JsonNode.Parse("""{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}""")).Length == 0 && inert.WorldClock is { DayScale: 4 },
                "the look takes a Live clock record");
            Check(Around(inert.Moment.Hour, 0f) < 1e-3f && inert.Moment.Daylight < 0.25f && inert.Moment.SunElevationDeg < -10f && inert.LampsOn && inert.Moment.MoonWeight > 0.9f
                && inert.ClockNote.StartsWith("world clock x4 (Etc/UTC), real calendar", StringComparison.Ordinal),
                $"18:00 real is world midnight: the sun is down, the moon is the key and the lamps are on ({inert.ClockNote})");
            instant = Utc(2026, 4, 15, 3);
            inert.SetWorldClock(inert.WorldClock);
            Check(Around(inert.Moment.Hour, 12f) < 1e-3f && inert.Moment.Daylight > 0.5f && inert.Moment.SunElevationDeg > 30f && !inert.LampsOn,
                $"03:00 real is world noon: the sun is high (elevation {inert.Moment.SunElevationDeg:0} degrees) and the lamps are off");
            instant = Utc(2026, 4, 15, 4, 30);
            inert._Process(11.0);
            Check(Around(inert.Moment.Hour, 18f) < 1e-3f, $"the look follows the world clock as it goes: 04:30 real is 18:00 after the update interval ({inert.Moment.Hour:0.00})");
            inert.SetClock(9f, 100);
            Check(Mathf.IsEqualApprox(inert.Moment.Hour, 9f) && inert.Moment.DayOfYear == 100, "a pin wins over the world clock");
            inert.ReleaseClock();
            Check(inert.ClockNote.StartsWith("world clock x4", StringComparison.Ordinal) && Around(inert.RealNow().Hour, 18f) < 1e-3f, "releasing the pin returns to the world clock, and RealNow says what it reads");
            inert.SetWorldClock(null);
            var back = plainMomentAt(instant);
            Check(inert.Moment.Hour == back.Hour && inert.Moment.DayOfYear == back.DayOfYear && inert.Moment.KeyEnergy == back.KeyEnergy && inert.Moment.SunElevationDeg == back.SunElevationDeg
                && inert.ClockNote.StartsWith("real clock", StringComparison.Ordinal), "taking the record away returns the look to the machine's clock");

            // A record that is not valid never stops the room: the look warns and keeps the machine's clock.
            var warning = inert.SetWorldClockFrom(JsonNode.Parse("""{"zone_id":"Etc/UTC","day_scale":5,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}"""));
            Check(warning.Contains("day_scale", StringComparison.Ordinal) && inert.WorldClock == null && inert.Warnings.Any(w => w.Contains("day_scale", StringComparison.Ordinal)),
                "a record the schema refuses is warned about and ignored: " + warning);
            inertHolder.QueueFree();

            // The hemisphere, in the look: the flipped clock's January has a summer sun and a summer palette.
            instant = Utc(2026, 1, 15, 3);
            var (flipHolder, flip) = NewDirector(preset, room, "FlippedHemisphereHolder");
            var other = preset.Hemisphere == "north" ? "south" : "north";
            flip.SetWorldClockFrom(JsonNode.Parse($$"""{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"{{other}}"}"""));
            var (ownHolder, own) = NewDirector(preset, room, "OwnHemisphereHolder");
            own.SetWorldClockFrom(JsonNode.Parse("""{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}"""));
            Check(flip.Moment.Season == "summer" && own.Moment.Season == "winter" && flip.Moment.SunElevationDeg > own.Moment.SunElevationDeg + 25f,
                $"in the look, a clock in the other hemisphere has a {flip.Moment.Season} January noon (sun {flip.Moment.SunElevationDeg:0} degrees) where the room's own has {own.Moment.Season} ({own.Moment.SunElevationDeg:0} degrees)");
            flipHolder.QueueFree();
            ownHolder.QueueFree();

            StylePreset Same() => preset;
            LookMoment plainMomentAt(DateTime at)
            {
                var read = LookClock.StandardClock(TimeZoneInfo.ConvertTimeFromUtc(at, TimeZoneInfo.Local), TimeZoneInfo.Local, Same().Tuning.Sun.RealClockDaylightSaving);
                return LookClock.At(Same(), read.Hour, read.DayOfYear);
            }
        }
        finally
        {
            LookClock.UtcClock = saved;
            if (savedClock.Length > 0) OS.SetEnvironment("GARAGE_GAME_LOOK_CLOCK", savedClock);
            if (savedDate.Length > 0) OS.SetEnvironment("GARAGE_GAME_LOOK_DATE", savedDate);
        }
        await Frames(1);
    }
}
