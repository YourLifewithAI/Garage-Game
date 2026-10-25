using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Decision 122 (P-Nature-2, item 2): a sprout's cast never fails for light; the seed locks its spot at any hour, and the seedling's
/// phases advance only in the island clock's daylight (GrowKindLaws.DaylightFrom to DaylightUntil, 06:00 to 18:00), so a night cast
/// grows from sunrise. Decision 121, as built: the phases are derived from the sowing and now alone, so the hours the game was closed
/// count their daylight as the hours it was open. In the test room with plants, the machine's clock pinned to UTC (NewWorld), then an
/// island clock of four days a real day (x_clock).
/// </summary>
public partial class NatureTest
{
    /// <summary>The next moment after from at an hour of the day in UTC.</summary>
    private static DateTime NextUtcHour(DateTime from, double hour)
    {
        var at = from.Date.AddHours(hour);
        while (at <= from) at = at.AddDays(1);
        return at;
    }

    /// <summary>An x_clock record in UTC, at a day scale (MECHANICS.md section 9).</summary>
    private static JsonObject UtcClockAt(int dayScale) =>
        new() { ["zone_id"] = "Etc/UTC", ["day_scale"] = dayScale, ["seasons_per_year"] = 4, ["phase_h"] = 0, ["hemisphere"] = "north" };

    private async Task TestSeedlingsInDaylight()
    {
        // The closed form: only the hours from 06:00 to 18:00 of each island day count.
        var cases = new (double From, double Span, double Daylight)[]
        {
            (12, 3, 3), (17, 3, 1), (22, 9, 1), (5.5, 1, 0.5), (6, 0, 0), (18, 12, 0), (0, 48, 24), (23, 32, 13), (30, 1, 1),
        };
        var counted = cases.Select(c => GrowKindLaws.DaylightHours(c.From, c.Span)).ToList();
        Check(cases.Zip(counted).All(c => Math.Abs(c.First.Daylight - c.Second) < 1e-9) && GrowKindLaws.DaylightFrom == 0.25 && GrowKindLaws.DaylightUntil == 0.75,
            "a span's daylight counts only its island hours from 06:00 to 18:00 (a day fraction of 0.25 to 0.75): " + string.Join(", ", counted.Select(h => h.ToString("0.##"))));

        // A night cast: the seed locks its spot at once, at half size; nothing grows until sunrise, then three phases of an hour.
        _now = NextUtcHour(_now, 22);
        var night = _now;
        await NewWorld(SavePath("daylight-night"));
        _host.LearnGrowForTests(2);
        _now = night;
        FullPool();
        var pips = _host.Vitals.Pips;
        var cast = GrowCast(Player, BareAt, 0.5);
        var sown = _now;
        Check(Ok(cast) && Kind(cast) == GrowKindWords.Sprouts && Grown(cast) == 12 && _host.Nature.Sprouts.Count == 12 && _host.Nature.Sprouts.All(s => s.Factor == GrowKindLaws.SeedlingStart) &&
              pips - _host.Vitals.Pips == 4,
            "at 22:00 on the island clock a sprout cast is made as by day, never refused for light: 12 seeds lock their spots at half size, at Grow's price: " +
            Canonical(cast["error"] ?? cast["data"]));
        // A burnt tuft replanted the same night is a seedling too, on the same clock.
        _host.Nature.Plants[Tuft(4)] = new NaturePlant { Id = Tuft(4), Burn = 1 };
        FullPool();
        var replant = GrowCast(Player, new Vector3(0.75f, 0.01f, 0.95f), 0.05);
        Check(Ok(replant) && Kind(replant) == GrowKindWords.Replant && _host.Nature.Plants[Tuft(4)] is { SownS: not null, Burn: 0 } && Factor(Tuft(4)) == GrowKindLaws.SeedlingStart,
            "a burnt tuft replanted at night comes back a seedling at half size: " + Canonical(replant["error"] ?? replant["data"]));
        var sizes = new System.Collections.Generic.List<(string At, float Sprout, float Tuft)>();
        foreach (var (label, after) in new[]
                 {
                     ("23:00", TimeSpan.FromHours(1)), ("05:59", TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1)), ("07:00", TimeSpan.FromHours(9) + TimeSpan.FromSeconds(1)),
                     ("08:00", TimeSpan.FromHours(10) + TimeSpan.FromSeconds(1)), ("09:00", TimeSpan.FromHours(11) + TimeSpan.FromSeconds(1)),
                 })
        {
            _now = sown + after;
            Steps(1);
            sizes.Add((label, _host.Nature.Sprouts[0].Factor, Factor(Tuft(4))));
        }
        var expected = new[] { 0.5f, 0.5f, 2f / 3, 5f / 6, 1f };
        Check(sizes.Zip(expected).All(p => Math.Abs(p.First.Sprout - p.Second) < 1e-4f && Math.Abs(p.First.Tuft - p.Second) < 1e-4f),
            "sown at 22:00, the seedlings stay at half size through the night and grow from sunrise, a phase an hour of daylight, natural at 09:00: " +
            string.Join(", ", sizes.Select(s => $"{s.At} {s.Sprout:0.000}/{s.Tuft:0.000}")));

        // The game closed (decision 121): a reload after time away finds the seedlings as far on as the daylight in between, and no further.
        var away = new System.Collections.Generic.List<(string At, float Sprout)>();
        foreach (var (label, after, size) in new[]
                 {
                     ("06:30", TimeSpan.FromHours(8.5), 0.5f), ("08:00", TimeSpan.FromHours(10) + TimeSpan.FromSeconds(1), 5f / 6), ("two days on", TimeSpan.FromDays(2), 1f),
                 })
        {
            _now = sown + after;
            await NewWorld(SavePath("daylight-night"));
            away.Add((label, _host.Nature.Sprouts.Count == 12 && _host.Nature.Sprouts.All(s => Math.Abs(s.Factor - size) < 1e-4f) ? size : -1));
        }
        Check(away.All(a => a.Sprout >= 0), "the game closed from 22:00, a reload at 06:30 finds them at half size, at 08:00 two phases on, two days on natural: " +
            string.Join(", ", away.Select(a => $"{a.At} {a.Sprout:0.000}")));

        // On an island clock of four days a real day (x_clock), real 12:00 UTC is the island's midnight and its sunrise is at real 13:30;
        // a phase is a quarter of a real hour of its daylight.
        _now = NextUtcHour(_now, 12);
        var noonUtc = _now;
        await NewWorld(SavePath("daylight-fast"));
        _host.ClockRecord = UtcClockAt(4);
        _host.LearnGrowForTests(2);
        _now = noonUtc;
        FullPool();
        var fast = GrowCast(Player, BareAt, 0.5);
        var fastSown = _now;
        var quick = new System.Collections.Generic.List<(string At, float Sprout)>();
        foreach (var (label, after) in new[]
                 {
                     ("13:29", TimeSpan.FromMinutes(89)), ("13:45", TimeSpan.FromMinutes(105) + TimeSpan.FromSeconds(1)), ("14:15", TimeSpan.FromMinutes(135) + TimeSpan.FromSeconds(1)),
                 })
        {
            _now = fastSown + after;
            Steps(1);
            quick.Add((label, _host.Nature.Sprouts.Count > 0 ? _host.Nature.Sprouts[0].Factor : -1));
        }
        Check(Ok(fast) && quick.Zip(new[] { 0.5f, 2f / 3, 1f }).All(p => Math.Abs(p.First.Sprout - p.Second) < 1e-4f),
            "four days a real day: sown at the island's midnight (real 12:00), they wait for its sunrise (real 13:30) and are natural three island hours on (real 14:15): " +
            string.Join(", ", quick.Select(q => $"{q.At} {q.Sprout:0.000}")) + " " + Canonical(fast["error"]));
        _host.ClockRecord = null;
        await FreeWorld();
    }
}
