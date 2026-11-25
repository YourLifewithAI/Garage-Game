using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GarageGame.Native.Look;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>
/// Every word the title page says of its own, in one place: each is a placeholder for the family to reword (docs/runs/RUN-4-MENU.md,
/// "Provisional values"). The start rule's words (New game, "Bring my game here", "Go back", the question) are MenuModel's.
/// </summary>
public static class TitleWords
{
    public const string Journal = "Field journal";
    public const string GameName = "Garage Game";
    public const string Tagline = "A room, grown into a land.";
    public const string LandKind = "The room, as the land it has become.";
    public const string MapCaption = "Seen from above. North is at the top.";
    public const string NoMap = "No map of this one yet.";
    public const string NoLand = "No land is here yet.";
    public const string NoLandCaption = "Install a room to see its land here.";
    public const string Continue = "Continue";
    public const string Choose = "Choose";
    public const string ChooseTip = "Pick a different person, land or way to play.";
    public const string Quit = "Quit";
    public const string North = "N";
    public const string CopyTail = ", with your Creative land";
    public static readonly string[] Moons =
    {
        "new moon", "thin moon growing", "half moon growing", "fat moon growing", "full moon", "fat moon shrinking", "half moon shrinking", "thin moon shrinking",
    };

    /// <summary>Where this mode's game went, said where none is in play (New game's own doing, also when the land then did not open).</summary>
    public static string PutAway(string modeWords) => $"Your last {modeWords} game is put away: New game makes a new one.";

    /// <summary>A land that would not open. The reason in brackets is the room's own word about itself, left as it is.</summary>
    public static string CouldNotOpen(string why) => $"That land could not be opened ({why}). It was left as it was.";

    /// <summary>"Morning, 9:30 am. The full moon."</summary>
    public static string Hour(string dayPart, string clock, string moon) => $"{char.ToUpperInvariant(dayPart[0])}{dayPart[1..]}, {clock}. The {moon}.";
}

/// <summary>
/// The hour, the sun and the moon the title page shows, and how it says them: pure, so a test can pin them.
/// <list type="bullet">
/// <item>The hour and the day are the machine's clock, or the look's own pins (--look-clock=HH:MM, --look-date=YYYY-MM-DD:
/// LookClock.ClockOverride), so the preview clock of run-room.ps1 -Hour and -Date moves this page too. This is the one place to
/// rewire when the run's shared hour lands (RUN-4.md, ruling 17: every reader takes the hour from LOOK-9).</item>
/// <item>The sun rises and sets by the game's own solar model (LookClock.SunTimes) at the land's site, or the look's fallback site
/// where the land declares none: the sun in the page's little window is the sun the land has once the game is in it.</item>
/// <item>The moon is the mean lunation from the new moon of 6 January 2000 (about half a day out at worst), enough to say which way
/// it leans; it stands highest later each day by its age, with the sun when new and at midnight when full, so it is in the window
/// only while it is up.</item>
/// </list>
/// </summary>
public static class TitleSky
{
    public const double Lunation = 29.530588853;
    private static readonly DateTime NewMoon = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

    /// <summary>The hour on the clock and the day the page shows, and which of the two a pin gave.</summary>
    public sealed record Reading(float Hour, DateTime Date, bool HourPinned, bool DatePinned)
    {
        public bool Pinned => HourPinned || DatePinned;
        /// <summary>The reading as a local date and time.</summary>
        public DateTime Local => Date.Date.AddHours(Math.Clamp(Hour, 0f, 23.999f));
    }

    /// <summary>The hour and the day the page shows: the look's pins where given, else the machine's local time. A pinned date keeps its own year.</summary>
    public static Reading Read(IReadOnlyList<string> arguments, Func<string, string?> environment, DateTime localNow)
    {
        var (hour, day, _) = LookClock.ClockOverride(arguments, environment);
        var date = localNow.Date;
        if (day is { } d) date = PinnedDate(arguments, environment) ?? new DateTime(localNow.Year, 1, 1).AddDays(d - 1);
        return new Reading(hour ?? (float)localNow.TimeOfDay.TotalHours, date, hour != null, day != null);
    }

    /// <summary>The date pin read whole (the look keeps only its day of the year): the first --look-date=, else GARAGE_GAME_LOOK_DATE, as LookClock finds them.</summary>
    private static DateTime? PinnedDate(IReadOnlyList<string> arguments, Func<string, string?> environment)
    {
        const string flag = "--look-date=";
        var text = arguments.FirstOrDefault(a => a.StartsWith(flag, StringComparison.Ordinal))?[flag.Length..] ?? environment("GARAGE_GAME_LOOK_DATE")?.Trim();
        return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    // ---- the sun ----

    /// <summary>When the sun rises and sets on the clock (standard time).</summary>
    public sealed record Sun(float Rise, float Set)
    {
        /// <summary>The plain day, six to six: what the page drew before it read the land's own sun (TitleScreen.TrueSun off).</summary>
        public static readonly Sun Plain = new(6f, 18f);
        public float Length => Math.Max(0f, Set - Rise);
        public float Noon => (Rise + Set) / 2f;
        public bool Up(float hour) => hour >= Rise && hour < Set;
        /// <summary>How far the sun is along its arc, 0 rising to 1 setting; null while it is down.</summary>
        public float? Along(float hour) => Up(hour) && Length > 0.01f ? (hour - Rise) / Length : null;
    }

    /// <summary>The sun's day at a site on a day of the year, by the game's own solar model; a land that declares no site gets the look's fallback site, as its world does.</summary>
    public static Sun SunOn(RoomSite? site, int dayOfYear)
    {
        var at = site ?? RoomSite.Fallback;
        var (rise, set) = LookClock.SunTimes(at.LatitudeDeg, at.SolarNoonH, LookTuning.Default.Sun.SunriseElevationDeg, Math.Clamp(dayOfYear, 1, 366));
        if (!float.IsFinite(rise) || !float.IsFinite(set) || set < rise) return Sun.Plain;
        return new Sun(Math.Clamp(rise, 0f, 24f), Math.Clamp(set, 0f, 24f));
    }

    /// <summary>The site a land's manifest declares; null where it declares none, or one the look refuses.</summary>
    public static RoomSite? SiteOf(IslandEntry? island)
    {
        if (island == null) return null;
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes(island.Directory + "/room.json");
            return bytes == null || bytes.Length == 0 || bytes.Length > IslandCatalog.MaxManifestBytes ? null : RoomSite.FromManifest(bytes);
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException) { return null; }
    }

    /// <summary>The hour the sun reads: the clock's, an hour earlier while the zone is in daylight saving, as the look reads it.</summary>
    public static float SunHour(DateTime local, TimeZoneInfo zone) =>
        (float)WorldTime.StandardTime(local, zone, LookTuning.Default.Sun.RealClockDaylightSaving).TimeOfDay.TotalHours;

    /// <summary>One word for the part of the day, by the sun: dawn and dusk are the hour either side of its rising and setting.</summary>
    public static string DayPart(float hour, Sun? sun = null)
    {
        var day = sun ?? Sun.Plain;
        return hour < day.Rise - 1f ? "night" : hour < day.Rise + 1f ? "dawn" : hour < 11f ? "morning" : hour < 13f ? "noon"
            : hour < day.Set - 1f ? "afternoon" : hour < day.Set + 1f ? "dusk" : hour < day.Set + 3f ? "evening" : "night";
    }

    // ---- how a family writes the time and the day ----

    /// <summary>Twelve hours with am and pm or twenty-four, and the month or the day first: the machine's own way, in the game's English.</summary>
    public sealed record Ways(bool TwelveHour, bool MonthFirst)
    {
        /// <summary>The way this machine writes them (its regional settings); a machine that says nothing writes "9:30 pm" and "November 24".</summary>
        public static Ways Of(CultureInfo culture) =>
            culture.Equals(CultureInfo.InvariantCulture) ? new Ways(true, true) : From(culture.DateTimeFormat.ShortTimePattern, culture.DateTimeFormat.MonthDayPattern);

        /// <summary>From a machine's two patterns: "h:mm tt" is twelve hours and "HH:mm" twenty-four; "MMMM d" is the month first and "d MMMM" the day.</summary>
        public static Ways From(string shortTime, string monthDay)
        {
            var (month, day) = (monthDay.IndexOf('M'), monthDay.IndexOf('d'));
            return new Ways(shortTime.Contains('h'), month >= 0 && (day < 0 || month < day));
        }
    }

    /// <summary>"9:05 pm", or "21:05".</summary>
    public static string Clock(float hour, bool twelveHour = false)
    {
        var minutes = (int)MathF.Floor(Math.Clamp(hour, 0f, 23.99f) * 60f + 0.01f);
        var (h, m) = (minutes / 60, minutes % 60);
        return twelveHour
            ? string.Create(CultureInfo.InvariantCulture, $"{(h % 12 == 0 ? 12 : h % 12)}:{m:00} {(h < 12 ? "am" : "pm")}")
            : string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}");
    }

    /// <summary>"Tuesday, November 24", or "Tuesday 24 November".</summary>
    public static string DateWords(DateTime date, bool monthFirst = false) =>
        date.ToString(monthFirst ? "dddd, MMMM d" : "dddd d MMMM", CultureInfo.InvariantCulture);

    // ---- the moon ----

    /// <summary>A local date and time as the instant it is, in UTC (an hour the clocks skip is read by the zone's standard offset).</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var reading = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(reading, zone); }
        catch (ArgumentException) { return DateTime.SpecifyKind(reading - zone.BaseUtcOffset, DateTimeKind.Utc); }
    }

    /// <summary>Where the moon is in its month at an instant (UTC): 0 new, 0.5 full, back to 1 new.</summary>
    public static float MoonPhase(DateTime utc)
    {
        var age = (DateTime.SpecifyKind(utc, DateTimeKind.Utc) - NewMoon).TotalDays % Lunation;
        if (age < 0) age += Lunation;
        return (float)(age / Lunation);
    }

    /// <summary>The share of the moon's face that is lit.</summary>
    public static float MoonLitShare(float phase) => (1f - MathF.Cos(MathF.Tau * phase)) / 2f;

    public static string MoonName(float phase) => TitleWords.Moons[(int)MathF.Floor(phase * 8f + 0.5f) % 8];

    /// <summary>
    /// How far the moon is along its arc at an hour, 0 rising to 1 setting; null while it is down. It stands highest later than the
    /// sun by its age (with the sun when new, at midnight when full), and it is up as long as the sun is when new and as long as the
    /// night is when full, as the real moon is.
    /// </summary>
    public static float? MoonAlong(float hour, float phase, Sun sun)
    {
        var length = Mathf.Lerp(sun.Length, 24f - sun.Length, MoonLitShare(phase));
        if (length < 0.01f) return null;
        var since = hour - (sun.Noon + phase * 24f);
        since = ((since + 12f) % 24f + 24f) % 24f - 12f;
        return MathF.Abs(since) < length / 2f ? since / length + 0.5f : null;
    }

    /// <summary>The lit part of the moon's face as a polygon around the origin: its right side while it grows, its left while it shrinks. Empty at the new moon.</summary>
    public static Vector2[] MoonLit(float phase, float radius, int steps = 24)
    {
        var share = MoonLitShare(phase);
        if (share < 0.02f) return Array.Empty<Vector2>();
        var terminator = MathF.Cos(MathF.Tau * phase);
        var points = new List<Vector2>();
        for (var i = 0; i <= steps; i++)
        {
            var t = -MathF.PI / 2 + MathF.PI * i / steps;
            points.Add(new Vector2(radius * MathF.Cos(t), radius * MathF.Sin(t)));
        }
        for (var i = steps; i >= 0; i--)
        {
            var t = -MathF.PI / 2 + MathF.PI * i / steps;
            points.Add(new Vector2(terminator * radius * MathF.Cos(t), radius * MathF.Sin(t)));
        }
        if (phase >= 0.5f) for (var i = 0; i < points.Count; i++) points[i] = new Vector2(-points[i].X, points[i].Y);
        return points.ToArray();
    }

    // ---- the sky's colour and the wash over the paper ----

    private static readonly Color NightSky = new("2d2d4a"), DawnSky = new("e0a070"), DaySky = new("a8cfd2"), DuskSky = new("e08a5a"), LateSky = new("5b4a78");
    private static readonly Color NightWash = new(0.07f, 0.09f, 0.26f, 0.22f), DawnWash = new(0.95f, 0.55f, 0.45f, 0.13f), MorningWash = new(1f, 0.96f, 0.78f, 0.04f),
        AfternoonWash = new(1f, 0.93f, 0.7f, 0.05f), DuskWash = new(0.92f, 0.45f, 0.2f, 0.15f), LateWash = new(0.3f, 0.2f, 0.42f, 0.18f);

    /// <summary>The colours through a day, set by the sun: night until an hour before it rises, dawn's colour half an hour after, the day's from three hours after to two before it sets, dusk's as it sets, then dark by four hours after.</summary>
    private static (float Hour, Color Sky, Color Wash)[] Keys(Sun sun)
    {
        var keys = new[]
        {
            (0f, NightSky, NightWash), (sun.Rise - 1f, NightSky, NightWash), (sun.Rise + 0.5f, DawnSky, DawnWash), (sun.Rise + Math.Min(3f, sun.Length * 0.4f), DaySky, MorningWash),
            (sun.Set - Math.Min(2f, sun.Length * 0.3f), DaySky, AfternoonWash), (sun.Set, DuskSky, DuskWash), (sun.Set + 2.5f, LateSky, LateWash), (sun.Set + 4f, NightSky, NightWash),
            (24f, NightSky, NightWash),
        };
        // A short day or a long one: the hours never run backwards or off the clock.
        for (var i = 1; i < keys.Length; i++) keys[i].Item1 = Math.Clamp(Math.Max(keys[i].Item1, keys[i - 1].Item1), 0f, 24f);
        return keys;
    }

    private static (Color Sky, Color Wash) Between(float hour, Sun sun)
    {
        hour = Math.Clamp(hour, 0f, 24f);
        var keys = Keys(sun);
        for (var i = 1; i < keys.Length; i++)
        {
            if (hour > keys[i].Hour) continue;
            var (a, b) = (keys[i - 1], keys[i]);
            var t = b.Hour > a.Hour ? (hour - a.Hour) / (b.Hour - a.Hour) : 0f;
            return (a.Sky.Lerp(b.Sky, t), a.Wash.Lerp(b.Wash, t));
        }
        return (keys[^1].Sky, keys[^1].Wash);
    }

    /// <summary>The sky's colour at an hour (the little window on the page).</summary>
    public static Color SkyColour(float hour, Sun? sun = null) => Between(hour, sun ?? Sun.Plain).Sky;

    /// <summary>How plainly the stars show, 0 to 1: by how dark the sky is, so none stands in a dawn or a dusk sky.</summary>
    public static float Stars(float hour, Sun? sun = null) => Math.Clamp((0.62f - SkyColour(hour, sun).V) / 0.3f, 0f, 1f);

    /// <summary>
    /// The thin wash of the hour's light laid over the paper: blue at night, rose at dawn, amber at dusk, nearly clear by day. It is
    /// never so thick that the page's ink reads worse than MenuTheme.InkLeast on the washed paper.
    /// </summary>
    public static Color Wash(float hour, Sun? sun = null) => MenuTheme.Thinned(Between(hour, sun ?? Sun.Plain).Wash, MenuTheme.Paper, MenuTheme.Ink, MenuTheme.InkLeast);

    // ---- the game's own word ----

    private static readonly Regex OwnWord = new(@"\([^)]*\)|""[^""]*""|(?<![\w/\\._-])islands?(?![\w/\\._-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// "Island" is the exporter's and the files' word, not the player's: in a sentence of the game's own (a refusal, a notice) the
    /// room's land is a land. Only the whole word is the game's: a name is never passed through here, a longer word that holds it
    /// ("Islanders") is left alone, and so is anything in brackets or quotes or in a path (the room's own word about itself).
    /// </summary>
    public static string Plain(string words) => OwnWord.Replace(words ?? "", match =>
    {
        var word = match.Value;
        if (word[0] is '(' or '"') return word;
        var land = word.Length > 6 ? "lands" : "land";
        return word.All(c => !char.IsLower(c)) ? land.ToUpperInvariant() : char.IsUpper(word[0]) ? char.ToUpperInvariant(land[0]) + land[1..] : land;
    });
}

/// <summary>
/// A small window of sky at the head of the page: the sun on its arc over a hill while it is up, the moon with its true phase while
/// it is up (pale by day), and stars as plain as the sky is dark.
/// </summary>
public partial class HourDial : Control
{
    /// <summary>The hour the sun reads.</summary>
    public float Hour { get; set; } = 12f;
    public float Phase { get; set; }
    public TitleSky.Sun Sun { get; set; } = TitleSky.Sun.Plain;
    /// <summary>A moon thinner than this is not drawn: it is lost beside the sun.</summary>
    public const float MoonShowsFrom = 0.04f;

    public float? SunAlong => Sun.Along(Hour);
    /// <summary>Where the moon is along its arc; null while it is down or too thin to see.</summary>
    public float? MoonAlong => TitleSky.MoonLitShare(Phase) >= MoonShowsFrom ? TitleSky.MoonAlong(Hour, Phase, Sun) : null;
    public float Stars => TitleSky.Stars(Hour, Sun);
    /// <summary>What stands in the window: "sun", else "moon", else "stars", else "sky" (the page's test reads it).</summary>
    public string Body => SunAlong != null ? "sun" : MoonAlong != null ? "moon" : Stars > 0.3f ? "stars" : "sky";

    private static readonly (float X, float Y)[] StarPlaces = { (0.12f, 0.2f), (0.3f, 0.12f), (0.46f, 0.3f), (0.62f, 0.14f), (0.78f, 0.28f), (0.9f, 0.12f), (0.2f, 0.45f), (0.7f, 0.5f) };

    public HourDial()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(150, 68);
    }

    public override void _Draw()
    {
        var (w, h) = (Size.X, Size.Y);
        if (w < 24 || h < 24) return;
        DrawRect(new Rect2(0, 0, w, h), TitleSky.SkyColour(Hour, Sun));
        var stars = Stars;
        if (stars > 0.02f)
            foreach (var (sx, sy) in StarPlaces) DrawCircle(new Vector2(w * sx, h * sy), 1.2f, new Color(MenuTheme.Cream, 0.8f * stars));
        var ground = h * 0.8f;
        Vector2 On(float along) => new(16 + along * (w - 32), ground - MathF.Sin(MathF.PI * along) * (ground - 16));
        if (MoonAlong is { } moon)
        {
            var at = On(moon);
            // By day the moon is a pale thing in the blue.
            var strength = SunAlong != null ? 0.55f : 1f;
            const float radius = 9f;
            DrawCircle(at, radius, new Color(MenuTheme.Cream, 0.2f * strength));
            var lit = TitleSky.MoonLit(Phase, radius).Select(p => p + at).ToArray();
            if (lit.Length >= 3 && Geometry2D.TriangulatePolygon(lit).Length >= 3) DrawColoredPolygon(lit, new Color(MenuTheme.Cream, strength));
        }
        if (SunAlong is { } sun)
        {
            var at = On(sun);
            DrawCircle(at, 8.5f, new Color("e8b830"));
            for (var ray = 0; ray < 8; ray++)
            {
                var direction = Vector2.Right.Rotated(ray * MathF.PI / 4);
                DrawLine(at + direction * 11.5f, at + direction * 16f, new Color("e8b830"), 2f);
            }
        }
        var hill = new List<Vector2> { new(0, h) };
        for (var x = 0f; x <= w; x += 6) hill.Add(new Vector2(x, ground + MathF.Sin(x * 0.07f) * 3f - 1f));
        hill.Add(new Vector2(w, ground - 1f));
        hill.Add(new Vector2(w, h));
        DrawColoredPolygon(hill.ToArray(), MenuTheme.InkSoft.Lerp(new Color("3a2c20"), SunAlong != null ? 0f : 1f));
        DrawRect(new Rect2(1, 1, w - 2, h - 2), MenuTheme.Ink, false, 2f);
    }
}

/// <summary>The map's frame: a pencilled mat with ticks at its corners. The land's picture sits inside it.</summary>
public partial class MapMat : Control
{
    public MapMat()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(360, 280);
    }

    public override void _Draw()
    {
        var (w, h) = (Size.X, Size.Y);
        if (w < 40 || h < 40) return;
        DrawRect(new Rect2(3, 5, w - 3, h - 5), new Color(0.12f, 0.07f, 0.02f, 0.25f));
        DrawRect(new Rect2(0, 0, w - 3, h - 5), MenuTheme.PaperLit);
        DrawRect(new Rect2(0, 0, w - 3, h - 5), MenuTheme.Ink, false, 2f);
        DrawRect(new Rect2(6, 6, w - 15, h - 17), MenuTheme.Sepia, false, 1f);
        // Ticks at the sides, like a surveyor's frame.
        for (var x = 24f; x < w - 24; x += 24) { DrawLine(new Vector2(x, 0), new Vector2(x, 5), MenuTheme.Ink, 1f); DrawLine(new Vector2(x, h - 5), new Vector2(x, h - 10), MenuTheme.Ink, 1f); }
    }
}

/// <summary>A compass mark: the letter N in a child's size, and under it, clear of it, an arrow pointing up the page. North is at the top of the map.</summary>
public partial class NorthMark : Control
{
    public const int LetterSize = MenuTheme.SmallestText;
    public const float ArrowHeight = 22f, Clear = 3f;
    /// <summary>The line the letter stands on, and the arrow's tip under it.</summary>
    public float LetterBaseline { get; private set; }
    public float ArrowTip => LetterBaseline + Clear;

    public NorthMark()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Measure();
    }

    private void Measure()
    {
        LetterBaseline = MathF.Ceiling(MenuTheme.Hand.GetAscent(LetterSize));
        var wide = MenuTheme.Hand.GetStringSize(TitleWords.North, HorizontalAlignment.Left, -1, LetterSize).X;
        CustomMinimumSize = new Vector2(MathF.Ceiling(Math.Max(wide, 14f)) + 8f, ArrowTip + ArrowHeight + 2f);
    }

    public override void _Draw()
    {
        var x = Size.X / 2;
        var wide = MenuTheme.Hand.GetStringSize(TitleWords.North, HorizontalAlignment.Left, -1, LetterSize).X;
        DrawString(MenuTheme.Hand, new Vector2(x - wide / 2, LetterBaseline), TitleWords.North, HorizontalAlignment.Left, -1, LetterSize, MenuTheme.Ink);
        var tip = ArrowTip;
        DrawColoredPolygon(new[] { new Vector2(x, tip), new Vector2(x + 6, tip + ArrowHeight), new Vector2(x, tip + ArrowHeight - 4), new Vector2(x - 6, tip + ArrowHeight) }, MenuTheme.Ink);
    }
}

/// <summary>
/// The title page (Run 4, MENU-4): a naturalist's journal lying open on the table, drawn in code. On its left page the room as land,
/// seen from above, with its name (the land's picture, MENU-5); on its right page the head of a field journal (the date, the part of
/// the day, the moon), the game's name, and the start rule's buttons: <b>Continue</b>, two lines and never cut, only where its save
/// is; <b>Bring my game here</b>; <b>New game</b>, which over a game asks once; and small "Choose" and "Quit". The rule itself is
/// MenuModel's (MENU-3), the same one the Choose page (MainMenu) keeps; nothing here moves a file. "Choose" opens the Choose page
/// (MenuBoot swaps them).
/// <list type="bullet">
/// <item>The word "island" is in nothing the page says of its own (TitleWords; TitleSky.Plain over the model's sentences). A team's
/// name and a land's are shown as they are, whatever letters they hold.</item>
/// <item>The page fits its screen by measuring: where its words need more room than there is, it gives way a step at a time
/// (Tightness: the line under the name first, then New game's height beside another button, the name's size, the head's label,
/// the buttons' height, the pages' top and bottom edges), and never trims a word. A plain game gives nothing way.</item>
/// <item>A thin wash of the hour's light lies over the paper (HourWash; <c>--no-hour-wash</c> leaves the paper plain); the inks on
/// the paper keep their reading contrast under it (MenuTheme.Legible).</item>
/// <item>New game's question is read before its answers: it stands over "Go back" and "Yes, new game", which stand where Continue
/// and New game were, with what is asked on a line of its own (PlaceHint, QuestionLines). Every other line the page says stands
/// under the buttons.</item>
/// </list>
/// </summary>
public partial class TitleScreen : Control
{
    public MenuModel Model { get; set; } = null!;
    /// <summary>Told the game to start (Continue, "Bring my game here" or New game).</summary>
    public Action<MenuLaunch>? Launch { get; set; }
    /// <summary>The Choose page was asked for (the small "Choose", or New game before a team or a land is chosen).</summary>
    public Action? Choose { get; set; }
    public Action? QuitGame { get; set; }
    /// <summary>The user arguments this launch was started with (null: MainMenu.LaunchArguments, else the process's).</summary>
    public Func<IReadOnlyList<string>>? LaunchArguments { get; set; }
    /// <summary>
    /// Whether the room, team and mode this launch names are chosen as the page opens. False where they already were (the page comes
    /// back from the Choose page): what the player chose there stands.
    /// </summary>
    public bool ChoosesFromLaunch { get; set; } = true;
    /// <summary>The machine's local time, its zone and its environment (a test names its own, so no check reads the real ones).</summary>
    public Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;
    public Func<string, string?> ReadEnvironment { get; set; } = System.Environment.GetEnvironmentVariable;
    /// <summary>How the time and the day are written: this machine's way.</summary>
    public TitleSky.Ways Ways { get; set; } = TitleSky.Ways.Of(CultureInfo.CurrentCulture);
    /// <summary>The thin colour of the hour over the paper; off, the paper is plain (also: --no-hour-wash).</summary>
    public bool HourWash { get; set; } = true;
    /// <summary>The sun rises and sets as the land's own does on this day; off, it is up from six to six (also: --plain-sun).</summary>
    public bool TrueSun { get; set; } = true;
    /// <summary>For this long after New game's question goes up, New game pressed again does nothing (a double click is one press, as on the Choose page).</summary>
    public int ConfirmGuardMs { get; set; } = 700;

    public const string NoHourWashArgument = "--no-hour-wash";
    public const string PlainSunArgument = "--plain-sun";
    public const int ButtonWidth = 520, ButtonHeight = 120, QuietButtonHeight = 100;
    /// <summary>The buttons' heights once the page has given way as far as its buttons (still a big target for a child's hand).</summary>
    public const int LowButtonHeight = 96, LowQuietHeight = 84;
    /// <summary>The furthest the page gives way (Tightness).</summary>
    public const int MaxTightness = 7;

    private IReadOnlyList<string> _arguments = Array.Empty<string>();
    private Button _continue = null!, _bring = null!, _newGame = null!, _goBack = null!, _choose = null!, _quit = null!;
    private Label _continueWord = null!, _continueLine = null!, _notice = null!, _hint = null!, _landName = null!, _caption = null!;
    private Label _date = null!, _hour = null!, _title = null!, _tagline = null!, _journal = null!;
    private VBoxContainer _continueLines = null!, _rightColumn = null!, _buttons = null!, _speech = null!;
    private MarginContainer _margin = null!;
    private HourDial _dial = null!;
    private ColorRect _wash = null!;
    private IslandThumbnail _picture = null!;
    private NorthMark _north = null!;
    private Control _leftPage = null!, _rightPage = null!;
    private StartOffer _offer = StartOffer.None;
    private TeamHold _hold = TeamHold.Free;
    private double _sinceAsked, _sinceSky;
    private ulong _questionAt;
    private bool _secondOfDouble, _newGameLeads = true, _styled, _skyRead;
    private int _refit, _tall = ButtonHeight;
    private string? _pictureOf;
    private RoomSite? _site;
    private Color _ground = MenuTheme.Paper;
    /// <summary>The words that lie on the paper, each with the ink it is written in and the least it must read at.</summary>
    private readonly List<(Control Control, Color Ink, float Least)> _onPaper = new();

    public Button ContinueButton => _continue;
    public Label ContinueWord => _continueWord;
    /// <summary>Continue's second line: the game it opens, wrapped (never trimmed).</summary>
    public Label ContinueLine => _continueLine;
    public Button BringButton => _bring;
    public Button NewGameButton => _newGame;
    public Button GoBackButton => _goBack;
    public Button ChooseButton => _choose;
    public Button QuitButton => _quit;
    public Label NoticeLabel => _notice;
    public Label HintLabel => _hint;
    public Label LandName => _landName;
    public Label DateLine => _date;
    public Label HourLine => _hour;
    public Label TitleLabel => _title;
    public Label Tagline => _tagline;
    public HourDial Dial => _dial;
    public ColorRect Wash => _wash;
    public IslandThumbnail Picture => _picture;
    public NorthMark North => _north;
    /// <summary>The left page's contents and the right's, for the test that the land's picture is on the left.</summary>
    public Control LeftPage => _leftPage;
    public Control RightPage => _rightPage;
    public TitleSky.Reading Sky { get; private set; } = new(12f, DateTime.Today, false, false);
    /// <summary>The sun's day the page shows: the land's own, on this date.</summary>
    public TitleSky.Sun Sun { get; private set; } = TitleSky.Sun.Plain;
    /// <summary>The paper's colour under the hour's wash: what the page's words are read against.</summary>
    public Color Ground => _ground;
    /// <summary>The words that lie on the paper (not on a button or the map's mat), with the least each must read at.</summary>
    public IEnumerable<(Control Control, float Least)> OnPaper => _onPaper.Select(o => (o.Control, o.Least)).Append(((Control)_hint, Model.Asking ? MenuTheme.InkLeast : MenuTheme.SoftLeast));
    /// <summary>How far the page has given way to fit its screen: 0, nothing; MaxTightness, all it can (GiveWay).</summary>
    public int Tightness { get; private set; }

    public override void _Ready()
    {
        Name = "TitleScreen";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _arguments = LaunchArguments?.Invoke() ?? MainMenu.LaunchArguments?.Invoke() ?? OS.GetCmdlineUserArgs();
        if (_arguments.Contains(NoHourWashArgument)) HourWash = false;
        if (_arguments.Contains(PlainSunArgument)) TrueSun = false;
        var theme = new Theme { DefaultFontSize = MenuTheme.SmallestText, DefaultFont = MenuTheme.Book };
        theme.SetColor("font_color", "Label", MenuTheme.Ink);
        Theme = theme;
        // What this launch names is chosen first, then the last game's; what is still open gets a plain default, so the buttons are
        // ready. A page that comes back from the Choose page keeps what was chosen there.
        if (ChoosesFromLaunch) Model.ChooseFromLaunch(_arguments);
        Model.ChooseFromLastGame();
        Settle();

        // A dark table, the book, the hour's wash, and the page's words.
        var table = new ColorRect { Name = "Table", Color = new Color("2a1d14"), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(table);
        table.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // The cup's ring lies in the book's top outer corner here, clear of "Quit" at the foot (JournalPaper.CupRingAtTop).
        var book = new JournalPaper { Name = "Book", CupRingAtTop = true };
        AddChild(book);
        book.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);
        _wash = new ColorRect { Name = "HourWash", Color = new Color(0, 0, 0, 0), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_wash);
        _wash.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);

        _margin = new MarginContainer { Name = "Margin" };
        foreach (var side in new[] { "left", "right", "top", "bottom" }) _margin.AddThemeConstantOverride("margin_" + side, 12);
        AddChild(_margin);
        _margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var pages = new HBoxContainer { Name = "Pages" };
        pages.AddThemeConstantOverride("separation", 0);
        _margin.AddChild(pages);
        _leftPage = Page("LeftPage", JournalPaper.SpineShare, 46, 30);
        _rightPage = Page("RightPage", 1f - JournalPaper.SpineShare, 38, 46);
        pages.AddChild(_leftPage);
        pages.AddChild(_rightPage);
        BuildLeft((VBoxContainer)_leftPage.GetChild(0));
        BuildRight((VBoxContainer)_rightPage.GetChild(0));
        Resized += () => _refit = 2;

        Refresh();
        // The keyboard starts on the button that leads: Continue, else "Bring my game here", else New game.
        (_continue.Visible ? _continue : _bring.Visible ? _bring : _newGame).GrabFocus();
    }

    private static MarginContainer Page(string name, float share, int outer, int gutter)
    {
        var page = new MarginContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = share };
        // The left page's outer edge is on the left; the right page's on the right.
        var (left, right) = name == "LeftPage" ? (outer, gutter) : (gutter, outer);
        page.AddThemeConstantOverride("margin_left", left);
        page.AddThemeConstantOverride("margin_right", right);
        page.AddThemeConstantOverride("margin_top", 34);
        page.AddThemeConstantOverride("margin_bottom", 34);
        var column = new VBoxContainer { Name = "Column", SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);
        page.AddChild(column);
        return page;
    }

    /// <summary>A word on the paper, in this ink, which must read at least this well there at any hour.</summary>
    private T OnThePaper<T>(T control, Color ink, float least) where T : Control
    {
        _onPaper.Add((control, ink, least));
        return control;
    }

    // ---- the left page: the land ----

    private void BuildLeft(VBoxContainer column)
    {
        _landName = OnThePaper(MenuTheme.Text("", MenuTheme.Hand, 30, MenuTheme.Ink, "LandName", wrap: true, width: 300), MenuTheme.Ink, MenuTheme.InkLeast);
        column.AddChild(_landName);
        column.AddChild(OnThePaper(MenuTheme.Text(TitleWords.LandKind, MenuTheme.Lean, 18, MenuTheme.Sepia, "LandKind", wrap: true, width: 300), MenuTheme.Sepia, MenuTheme.SoftLeast));
        var mat = new MapMat { Name = "MapMat", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddChild(mat);
        _picture = new IslandThumbnail { Name = "Picture", CustomMinimumSize = new Vector2(200, 160) };
        mat.AddChild(_picture);
        _picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _picture.OffsetLeft = 12; _picture.OffsetTop = 12; _picture.OffsetRight = -15; _picture.OffsetBottom = -17;
        var row = new HBoxContainer { Name = "CaptionRow" };
        column.AddChild(row);
        _caption = OnThePaper(MenuTheme.Text(TitleWords.MapCaption, MenuTheme.Lean, 18, MenuTheme.Sepia, "Caption", wrap: true, width: 200), MenuTheme.Sepia, MenuTheme.SoftLeast);
        _caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _caption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(_caption);
        _north = new NorthMark { Name = "North" };
        row.AddChild(_north);
    }

    // ---- the right page: the head, the name, the buttons ----

    private void BuildRight(VBoxContainer column)
    {
        _rightColumn = column;
        var head = new HBoxContainer { Name = "Head" };
        head.AddThemeConstantOverride("separation", 14);
        column.AddChild(head);
        _dial = new HourDial { Name = "Dial", SizeFlagsVertical = SizeFlags.ShrinkCenter };
        head.AddChild(_dial);
        var lines = new VBoxContainer { Name = "HeadLines", SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        lines.AddThemeConstantOverride("separation", 0);
        head.AddChild(lines);
        _journal = OnThePaper(MenuTheme.Text(TitleWords.Journal, MenuTheme.Hand, 18, MenuTheme.Sepia, "JournalLabel"), MenuTheme.Sepia, MenuTheme.SoftLeast);
        lines.AddChild(_journal);
        _date = OnThePaper(MenuTheme.Text("", MenuTheme.Book, 22, MenuTheme.Ink, "DateLine", wrap: true, width: 200), MenuTheme.Ink, MenuTheme.InkLeast);
        lines.AddChild(_date);
        _hour = OnThePaper(MenuTheme.Text("", MenuTheme.Lean, 18, MenuTheme.InkSoft, "HourLine", wrap: true, width: 200), MenuTheme.InkSoft, MenuTheme.SoftLeast);
        lines.AddChild(_hour);

        _title = OnThePaper(MenuTheme.Text(TitleWords.GameName, MenuTheme.Hand, 48, MenuTheme.Ink, "Title", wrap: true, width: 300), MenuTheme.Ink, MenuTheme.InkLeast);
        column.AddChild(_title);
        _tagline = OnThePaper(MenuTheme.Text(TitleWords.Tagline, MenuTheme.Lean, 20, MenuTheme.InkSoft, "Tagline", wrap: true, width: 300), MenuTheme.InkSoft, MenuTheme.SoftLeast);
        column.AddChild(_tagline);
        column.AddChild(new Control { Name = "Gap", SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });

        var buttons = new VBoxContainer { Name = "Buttons" };
        _buttons = buttons;
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);
        _goBack = Make("GoBack", MenuModel.GoBackWords, ButtonHeight);
        MenuTheme.AsRibbon(_goBack);
        _goBack.Visible = false;
        _goBack.Pressed += GoBack;
        buttons.AddChild(_goBack);
        // Continue is two lines, and neither is cut: the word, and under it the game it opens, wrapped; the button grows if a long name needs it.
        _continue = Make("Continue", "", ButtonHeight);
        MenuTheme.AsRibbon(_continue);
        _continue.Visible = false;
        _continueLines = new VBoxContainer { Name = "ContinueLines", Alignment = BoxContainer.AlignmentMode.Center };
        _continueLines.AddThemeConstantOverride("separation", 0);
        _continueWord = MenuTheme.Text(TitleWords.Continue, MenuTheme.Hand, 30, MenuTheme.Cream, "ContinueWord");
        _continueWord.HorizontalAlignment = HorizontalAlignment.Center;
        _continueLines.AddChild(_continueWord);
        _continueLine = MenuTheme.Text("", MenuTheme.Book, 18, MenuTheme.Cream, "ContinueLine", wrap: true, width: ButtonWidth - 40);
        _continueLine.HorizontalAlignment = HorizontalAlignment.Center;
        _continueLines.AddChild(_continueLine);
        Inside(_continue, _continueLines, 14, 6);
        _continue.Pressed += () => Start(Model.ContinueHere());
        buttons.AddChild(_continue);
        _bring = Make("BringGame", MenuModel.BringWords, ButtonHeight);
        MenuTheme.AsRibbon(_bring);
        _bring.Visible = false;
        _bring.Pressed += () => Start(Model.BringHere());
        buttons.AddChild(_bring);
        _newGame = Make("NewGame", MenuModel.NewGameWords, ButtonHeight);
        MenuTheme.AsRibbon(_newGame);
        _newGame.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) _secondOfDouble = click.DoubleClick;
        };
        _newGame.Pressed += PressNewGame;
        buttons.AddChild(_newGame);

        var speech = new VBoxContainer { Name = "Speech" };
        _speech = speech;
        speech.AddThemeConstantOverride("separation", 2);
        column.AddChild(speech);
        _notice = OnThePaper(MenuTheme.Text("", MenuTheme.Book, 18, MenuTheme.Warning, "Notice", wrap: true, width: ButtonWidth - 20), MenuTheme.Warning, MenuTheme.SoftLeast);
        speech.AddChild(_notice);
        // The hint's ink is the soft one, and the main one while it is New game's question (RefreshButtons). It stands under the
        // buttons, and over them while it is the question (PlaceHint).
        _hint = MenuTheme.Text("", MenuTheme.Book, 18, MenuTheme.InkSoft, "Hint", wrap: true, width: ButtonWidth - 20);
        speech.AddChild(_hint);

        var small = new HBoxContainer { Name = "Small" };
        column.AddChild(small);
        _choose = OnThePaper(Make("Choose", TitleWords.Choose, 46, 150), MenuTheme.InkSoft, MenuTheme.SoftLeast);
        MenuTheme.AsLink(_choose, 22);
        _choose.TooltipText = TitleWords.ChooseTip;
        _choose.Pressed += () => Choose?.Invoke();
        small.AddChild(_choose);
        small.AddChild(new Control { Name = "Space", SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _quit = OnThePaper(Make("Quit", TitleWords.Quit, 46, 110), MenuTheme.InkSoft, MenuTheme.SoftLeast);
        MenuTheme.AsLink(_quit, 22);
        _quit.Pressed += () => QuitGame?.Invoke();
        small.AddChild(_quit);
    }

    private static Button Make(string name, string text, int height, int width = ButtonWidth) =>
        new() { Name = name, Text = text, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(width, height), SizeFlagsHorizontal = SizeFlags.ExpandFill };

    private static void Inside(Button button, Control contents, int horizontal, int vertical)
    {
        var padded = new MarginContainer { Name = "Padding" };
        foreach (var side in new[] { "left", "right" }) padded.AddThemeConstantOverride("margin_" + side, horizontal);
        foreach (var side in new[] { "top", "bottom" }) padded.AddThemeConstantOverride("margin_" + side, vertical);
        padded.AddChild(contents);
        button.AddChild(padded);
        padded.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MenuStyle.IgnoreMouse(padded);
    }

    // ---- what the model says, drawn ----

    /// <summary>A plain start where nothing is chosen: the land (one with a map, the player's own first) and a mode, so the buttons are ready.</summary>
    private void Settle()
    {
        var notice = Model.Notice;
        if (Model.Island == null && Model.Islands.Count > 0)
        {
            var pick = Model.Islands.FirstOrDefault(i => i.HasShape && !i.Shipped && !i.Tutorial) ?? Model.Islands.FirstOrDefault(i => i.HasShape && !i.Tutorial) ??
                       Model.Islands.FirstOrDefault(i => i.HasShape) ?? Model.Islands[0];
            Model.SelectIsland(pick.RoomId);
        }
        if (Model.Mode == null && Model.Team is { Readable: true } && Model.Offers("creative")) Model.ChooseMode("creative");
        Model.SetNotice(notice);
    }

    /// <summary>"Mira on the Garage, Challenge (Brave)": the game the buttons would start. The team's name and the land's are as they are (a name is never reworded).</summary>
    public string? GameWords()
    {
        if (Model.WhyNotReady != null) return null;
        var words = $"{Model.Team!.DisplayName} on {Model.Island!.LandName}, {MenuModel.ModeWords(Model.Mode!)}";
        if (Model.Mode == "challenge") words += $" ({MenuModel.DifficultyWords(Model.Difficulty ?? "standard")})";
        if (Model.CopyCreative) words += TitleWords.CopyTail;
        return words;
    }

    /// <summary>The page as the model says it is now: the land, its sky, the buttons and the words; then it is fitted to its screen.</summary>
    public void Refresh()
    {
        _offer = Model.Offer;
        // A land has its own sun: the sky is read again when the land changes.
        if (RefreshLand() || !_skyRead) UpdateSky();
        RefreshButtons();
        Fit();
        _refit = 2;
    }

    private bool RefreshLand()
    {
        var island = Model.Island;
        var key = island == null ? "" : island.RoomId + "|" + island.ManifestPrefix + "|" + island.Directory;
        if (key == _pictureOf) return false;
        _pictureOf = key;
        _landName.Text = island == null ? TitleWords.NoLand : island.LandName;
        var shape = island?.ReadShape();
        _picture.SetShape(shape);
        _caption.Text = island == null ? TitleWords.NoLandCaption : shape == null ? TitleWords.NoMap : TitleWords.MapCaption;
        _site = TitleSky.SiteOf(island);
        return true;
    }

    private void RefreshButtons()
    {
        var why = Model.WhyNotReady;
        var offer = _offer;
        var hold = why == null ? Model.Hold() : TeamHold.Free;
        _hold = hold;
        var waiting = hold == TeamHold.Leaving;
        var asking = Model.Asking;
        _newGame.Text = asking ? MenuModel.YesNewGameWords : MenuModel.NewGameWords;
        _goBack.Visible = asking;
        _continue.Visible = offer.Continue && !asking;
        _bring.Visible = offer.Bring && !asking;
        // The start rule (MENU-3): while a room of the team is still leaving, every start waits on the team's lease.
        _continue.Disabled = waiting;
        _bring.Disabled = waiting;
        _newGame.Disabled = waiting;
        _continueLine.Text = offer.Continue ? GameWords() ?? "" : "";
        _continue.TooltipText = offer.Continue ? TitleWords.Continue + ": " + _continueLine.Text : "";
        // New game leads (the ribbon) where it stands alone; beside Continue, "Bring my game here" or "Go back" it is a quiet slip.
        var leads = !offer.Continue && !offer.Bring && !asking;
        if (leads != _newGameLeads || !_styled)
        {
            _styled = true;
            _newGameLeads = leads;
            if (leads) MenuTheme.AsRibbon(_newGame); else MenuTheme.AsSlip(_newGame);
        }
        _notice.Text = TitleSky.Plain(Model.Notice);
        _notice.Visible = _notice.Text.Length > 0;
        _hint.AddThemeFontSizeOverride("font_size", asking ? 20 : MenuTheme.SmallestText);
        // Continue already says which game it opens, so the line under the buttons does not say it twice.
        var said = why != null ? TitleSky.Plain(why) : asking ? QuestionLines(TitleSky.Plain(Model.Question)) : waiting ? TitleSky.Plain(MenuModel.StillClosingWords)
            : offer.Case == StartCase.Rebuilt ? TitleSky.Plain(MenuModel.RebuiltWords) : hold == TeamHold.StillOpen ? TitleSky.Plain(MenuModel.OpenElsewhereWords)
            : offer.Continue ? "" : GameWords() ?? "";
        // No game in play and one put away (New game's own doing, also when the land then did not open): the page says where it went.
        if (why == null && !asking && !waiting && offer.Case == StartCase.NoGame && Model.GamePutAway)
            said = (said.Length > 0 ? said + "\n" : "") + TitleWords.PutAway(MenuModel.ModeWords(Model.Mode!));
        _hint.Text = said;
        _hint.Visible = said.Length > 0;
        PlaceHint(over: asking && why == null);
        InkTheHint();
    }

    /// <summary>
    /// A question is read before its answers: while the line is New game's question it stands over the buttons ("Go back" and
    /// "Yes, new game" under it, where Continue and New game were); any other line stands under them, where it always did.
    /// </summary>
    private void PlaceHint(bool over)
    {
        Node home = over ? _rightColumn : _speech;
        if (_hint.GetParent() == home) return;
        _hint.GetParent()?.RemoveChild(_hint);
        home.AddChild(_hint);
        // Added last, then put where the buttons are: the buttons move down one place, under it.
        if (over) _rightColumn.MoveChild(_hint, _buttons.GetIndex());
    }

    /// <summary>
    /// New game's question as the page writes it: what is asked on a line of its own, and under it what becomes of the old game
    /// ("New game?" / "Your Creative game from 24 Nov is put away, not deleted."). No word is changed; a question that is one
    /// sentence stays one line.
    /// </summary>
    public static string QuestionLines(string question)
    {
        var words = question ?? "";
        var at = words.IndexOf("? ", StringComparison.Ordinal);
        return at < 0 || at + 2 >= words.Length ? words : words[..(at + 1)] + "\n" + words[(at + 2)..];
    }

    private void InkTheHint()
    {
        var asking = Model.Asking;
        _hint.AddThemeColorOverride("font_color", MenuTheme.Legible(asking ? MenuTheme.Ink : MenuTheme.InkSoft, _ground, asking ? MenuTheme.InkLeast : MenuTheme.SoftLeast));
    }

    // ---- fitting the screen ----

    /// <summary>
    /// The page at one step of giving way. 0: all of it. 1: the line under the name goes. 2: New game, beside another button, is
    /// QuietButtonHeight high. 3: the name is smaller. 4: the head lets its label go. 5: the name is smaller still. 6: every button
    /// is lower (LowButtonHeight, LowQuietHeight). 7: the pages' top and bottom edges are narrower. New game's question takes the
    /// line under the name down at any step (the question needs the room).
    /// </summary>
    private void GiveWay(int level)
    {
        Tightness = level;
        _tagline.Visible = !Model.Asking && level < 1;
        _tall = level >= 6 ? LowButtonHeight : ButtonHeight;
        var quiet = level >= 6 ? LowQuietHeight : level >= 2 ? QuietButtonHeight : ButtonHeight;
        _goBack.CustomMinimumSize = new Vector2(ButtonWidth, _tall);
        _bring.CustomMinimumSize = new Vector2(ButtonWidth, _tall);
        _newGame.CustomMinimumSize = new Vector2(ButtonWidth, _newGameLeads ? _tall : quiet);
        _title.AddThemeFontSizeOverride("font_size", level >= 5 ? 32 : level >= 3 ? 40 : 48);
        _journal.Visible = level < 4;
        var edge = level >= 7 ? 20 : 34;
        foreach (var page in new[] { _leftPage, _rightPage })
        {
            page.AddThemeConstantOverride("margin_top", edge);
            page.AddThemeConstantOverride("margin_bottom", edge);
        }
        FitContinue();
    }

    /// <summary>The height the page's words need, and the height it has.</summary>
    public float Needs => _margin.GetCombinedMinimumSize().Y;
    public float Has => Size.Y > 1f ? Size.Y : GetViewportRect().Size.Y;

    /// <summary>The page gives way a step at a time, and no further than it must, until its words fit its screen. No word is ever trimmed.</summary>
    public void Fit()
    {
        for (var level = 0; level <= MaxTightness; level++)
        {
            GiveWay(level);
            if (Needs <= Has + 0.5f) break;
        }
    }

    /// <summary>The button is as tall as its two lines need (a long team's and land's names wrap to a third line).</summary>
    private void FitContinue()
    {
        var need = _continueLines.GetCombinedMinimumSize().Y + 12 + 6;
        _continue.CustomMinimumSize = new Vector2(ButtonWidth, Math.Max(_tall, MathF.Ceiling(need)));
    }

    // ---- this hour ----

    /// <summary>The hour's sun or moon, the head's words, the wash over the paper, and the inks that lie on it.</summary>
    public void UpdateSky()
    {
        _skyRead = true;
        var local = LocalNow();
        Sky = TitleSky.Read(_arguments, ReadEnvironment, local);
        Sun = TrueSun ? TitleSky.SunOn(_site, Sky.Local.DayOfYear) : TitleSky.Sun.Plain;
        // A pinned hour is the look's own (the sun's); the machine's is read as the look reads it, on standard time.
        var sunHour = Sky.HourPinned ? Sky.Hour : TitleSky.SunHour(Sky.Local, Zone);
        var phase = TitleSky.MoonPhase(TitleSky.ToUtc(Sky.Local, Zone));
        _date.Text = TitleSky.DateWords(Sky.Date, Ways.MonthFirst);
        _hour.Text = TitleWords.Hour(TitleSky.DayPart(sunHour, Sun), TitleSky.Clock(Sky.Hour, Ways.TwelveHour), TitleSky.MoonName(phase));
        _dial.Hour = sunHour;
        _dial.Phase = phase;
        _dial.Sun = Sun;
        _dial.QueueRedraw();
        _wash.Color = HourWash ? TitleSky.Wash(sunHour, Sun) : new Color(0, 0, 0, 0);
        _ground = MenuTheme.Over(MenuTheme.Paper, _wash.Color);
        // Under the wash every ink on the paper is darkened, as little as will do, until it reads as well as it must.
        foreach (var (control, ink, least) in _onPaper)
        {
            var colour = MenuTheme.Legible(ink, _ground, least);
            if (control is Button link) MenuTheme.LinkInk(link, colour);
            else control.AddThemeColorOverride("font_color", colour);
        }
        InkTheHint();
    }

    private void Start(MenuLaunch? launch)
    {
        Refresh();
        if (launch == null) return;
        Launch?.Invoke(launch);
    }

    /// <summary>
    /// New game, by the rule of the Choose page (MainMenu.PressNewGame): over a game the first press puts the question up and gives
    /// "Go back" the focus; the next, not sooner than ConfirmGuardMs after it and never the second click of a double click, puts the old
    /// game away and starts. Where there is no game it starts at once. With no person or land chosen it opens the Choose page instead.
    /// </summary>
    private void PressNewGame()
    {
        if (Model.WhyNotReady != null) { Choose?.Invoke(); return; }
        var asked = Model.Asking;
        var second = _secondOfDouble;
        _secondOfDouble = false;
        if (asked && (second || Time.GetTicksMsec() - _questionAt < (ulong)Math.Max(0, ConfirmGuardMs))) return;
        var launch = Model.NewGame();
        if (!asked && Model.Asking)
        {
            _questionAt = Time.GetTicksMsec();
            Refresh();
            _goBack.GrabFocus();
            return;
        }
        Start(launch);
    }

    private void GoBack()
    {
        Model.GoBack();
        Refresh();
        (_continue.Visible ? _continue : _bring.Visible ? _bring : _newGame).GrabFocus();
    }

    public override void _Process(double delta)
    {
        // The layout settles a frame or two after the words change (a wrapped line's height follows its width): the fit is taken again.
        if (_refit > 0 && --_refit >= 0) Fit();
        _sinceSky += delta;
        // A pinned hour stands still; a pinned date alone does not stop the clock.
        if (_sinceSky >= 20 && !Sky.HourPinned) { _sinceSky = 0; UpdateSky(); }
        if (_hold == TeamHold.Free) return;
        _sinceAsked += delta;
        if (_sinceAsked < 0.1) return;
        _sinceAsked = 0;
        var hold = Model.Hold();
        if (hold == _hold) return;
        if (Model.Notice == MenuModel.StillClosingWords || (hold == TeamHold.Free && Model.Notice == PlayLease.StillOpen)) Model.SetNotice("");
        Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } escape && escape.Keycode == Key.Escape && Model.Asking)
        {
            GoBack();
            GetViewport().SetInputAsHandled();
        }
        else if (InputMap.HasAction(Act.ViewFullscreen) && PlayerControls.Hit(@event, Act.ViewFullscreen) && @event.IsPressed() && !@event.IsEcho())
        {
            ToggleFullscreen();
            GetViewport().SetInputAsHandled();
        }
    }

    private static void ToggleFullscreen()
    {
        if (DisplayServer.GetName() == "headless") return;
        var full = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
        DisplayServer.WindowSetMode(full ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        MenuProfile.Set(RoomHud.FullscreenProfileKey, !full);
    }
}
