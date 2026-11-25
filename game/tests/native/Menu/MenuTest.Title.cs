using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// The title page (Run 4, MENU-4), headless. The page is laid out in a sub-viewport of a named size, so "fits 1280x720" is measured
/// at 1280x720 whatever the test window is: every visible control is on the screen, no label is clipped or trimmed, no word is under
/// 18 px, no button's word is wider than the button, and the page's minimum size is inside the screen, in every state the page has
/// (a game, "Bring my game here", the question, a game still open, a long notice), with the longest names a team and a land can
/// have, and again in wider and taller fonts than this machine's; Continue's two lines are whole; the word "island" is in nothing
/// the page says of its own, and a name is shown as it is; the land's picture is drawn, on the left page, from its shape data; and
/// New game and Continue are driven by real clicks and keys through MenuBoot (OpenTitle), by the start rule of MENU-3, the wait on
/// the team's lease and the rebuilt room among them. No check reads the machine's clock, zone, regional settings or environment:
/// every page here is given its own (Hermetic). What the page looks like on a screen is the capture scene's
/// (native_menu_title_capture.tscn, capture-title.ps1), for the founder's eye.
/// </summary>
public partial class MenuTest
{
    private static IEnumerable<Control> ControlsUnder(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is Control control) yield return control;
            foreach (var inner in ControlsUnder(child)) yield return inner;
        }
    }

    private static IEnumerable<string> WordsOn(Node root) =>
        ControlsUnder(root).SelectMany(c => new[] { c is Label l ? l.Text : c is Button b ? b.Text : "", c.TooltipText }).Where(w => !string.IsNullOrEmpty(w));

    private static bool HasIsland(Node root) => WordsOn(root).Any(w => w.Contains("island", StringComparison.OrdinalIgnoreCase));

    /// <summary>The clock, the zone (six hours behind UTC, no daylight saving), the way of writing and the empty environment every title page of the suite is given.</summary>
    private static readonly DateTime TitleNow = new(2026, 11, 24, 14, 5, 0);
    private static readonly TimeZoneInfo TitleZone = TimeZoneInfo.CreateCustomTimeZone("title-test", TimeSpan.FromHours(-6), "title-test", "title-test");

    private static void Hermetic(TitleScreen page)
    {
        page.ReadEnvironment = _ => null;
        page.LocalNow = () => TitleNow;
        page.Zone = TitleZone;
        page.Ways = new TitleSky.Ways(true, true);
    }

    private async Task<(SubViewport View, TitleScreen Page)> TitleIn(Vector2I size, MenuModel model, string[] arguments, Func<DateTime>? now = null, Action<TitleScreen>? arrange = null)
    {
        var view = new SubViewport { Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(view);
        var page = new TitleScreen { Model = model, LaunchArguments = () => arguments };
        Hermetic(page);
        if (now != null) page.LocalNow = now;
        arrange?.Invoke(page);
        view.AddChild(page);
        await Frames(5);
        return (view, page);
    }

    /// <summary>What does not fit, in words; null when the page fits its screen.</summary>
    private static string? TitleProblem(SubViewport view, TitleScreen page)
    {
        var screen = new Rect2(Vector2.Zero, view.Size).Grow(0.5f);
        var least = page.GetNode<MarginContainer>("Margin").GetCombinedMinimumSize();
        if (least.X > view.Size.X + 0.5f || least.Y > view.Size.Y + 0.5f) return $"the page needs {least} of {view.Size} (given way {page.Tightness} of {TitleScreen.MaxTightness})";
        foreach (var control in ControlsUnder(page).Where(Shown))
        {
            if (!screen.Encloses(control.GetGlobalRect())) return $"{control.Name} {control.GetGlobalRect()} is off the screen {view.Size}";
            if (control is Label label && label.Text.Length > 0)
            {
                if (label.GetLineCount() * label.GetLineHeight() > label.Size.Y + 1) return $"{label.Name} is clipped ({label.GetLineCount()} lines in {label.Size.Y})";
                if (label.ClipText || label.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming) return $"{label.Name} may be trimmed";
                if (label.GetThemeFontSize("font_size") < MenuTheme.SmallestText) return $"{label.Name} is {label.GetThemeFontSize("font_size")} px";
            }
            if (control is Button { Text.Length: > 0 } button)
            {
                if (button.GetThemeFont("font").GetStringSize(button.Text, HorizontalAlignment.Left, -1, button.GetThemeFontSize("font_size")).X > button.Size.X - 8) return $"{button.Name} is narrower than its word";
                if (button.GetThemeFontSize("font_size") < MenuTheme.SmallestText) return $"{button.Name} is {button.GetThemeFontSize("font_size")} px";
            }
        }
        if (page.LeftPage.GetGlobalRect().End.X > page.RightPage.GetGlobalRect().Position.X + 0.5f) return "the two pages overlap";
        return null;
    }

    /// <summary>A key as the window sends one: down, then up, through the viewport (the pages' own Esc handlers get it in their turn).</summary>
    private async Task PressKey(Key key)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true }, true);
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false }, true);
        await Frames(3);
    }

    // ---- the hour, the sun, the moon, the words ----

    private void TestTitleSky()
    {
        var pinned = TitleSky.Read(new[] { "--look-clock=09:30", "--look-date=2026-11-24" }, _ => null, new DateTime(2026, 1, 2, 3, 4, 0));
        var real = TitleSky.Read(Array.Empty<string>(), _ => null, new DateTime(2026, 1, 2, 15, 30, 0));
        Check(pinned.HourPinned && pinned.DatePinned && Math.Abs(pinned.Hour - 9.5f) < 0.001f && pinned.Date == new DateTime(2026, 11, 24) && !real.Pinned && Math.Abs(real.Hour - 15.5f) < 0.001f && real.Date == new DateTime(2026, 1, 2),
            "the page's hour and day are the look's own pins (--look-clock, --look-date: the preview clock), else the machine's local time");
        var otherYear = TitleSky.Read(new[] { "--look-date=2027-03-01" }, _ => null, new DateTime(2026, 11, 24, 10, 0, 0));
        var byVariable = TitleSky.Read(Array.Empty<string>(), name => name == "GARAGE_GAME_LOOK_DATE" ? " 2028-02-29 " : null, new DateTime(2026, 11, 24, 10, 0, 0));
        Check(otherYear.Date == new DateTime(2027, 3, 1) && TitleSky.DateWords(otherYear.Date) == "Monday 1 March" && otherYear.DatePinned && !otherYear.HourPinned && Math.Abs(otherYear.Hour - 10f) < 0.001f &&
              byVariable.Date == new DateTime(2028, 2, 29) && TitleSky.DateWords(byVariable.Date, true) == "Tuesday, February 29",
            $"a pinned date keeps its own year, so its weekday is right (1 March 2027 is \"{TitleSky.DateWords(otherYear.Date)}\"), and a date pin alone pins no hour");
        Check(TitleSky.DayPart(9.5f) == "morning" && TitleSky.DayPart(6f) == "dawn" && TitleSky.DayPart(12f) == "noon" && TitleSky.DayPart(15f) == "afternoon" && TitleSky.DayPart(18f) == "dusk" &&
              TitleSky.DayPart(20f) == "evening" && TitleSky.DayPart(23f) == "night" && TitleSky.DayPart(2f) == "night",
            "the part of the day is said in one plain word");
        Check(TitleSky.Clock(9.5f) == "9:30" && TitleSky.Clock(0.08f) == "0:04" && TitleSky.Clock(22f) == "22:00" && TitleSky.Clock(9.5f, true) == "9:30 am" && TitleSky.Clock(0.08f, true) == "12:04 am" &&
              TitleSky.Clock(12f, true) == "12:00 pm" && TitleSky.Clock(22f, true) == "10:00 pm" && TitleSky.Clock(23.99f, true) == "11:59 pm" &&
              TitleSky.DateWords(new DateTime(2026, 11, 24)) == "Tuesday 24 November" && TitleSky.DateWords(new DateTime(2026, 11, 24), true) == "Tuesday, November 24",
            "the clock is written in twelve hours with am and pm or in twenty-four, and the date with the month or the day first");
        Check(TitleSky.Ways.From("h:mm tt", "MMMM d") == new TitleSky.Ways(true, true) && TitleSky.Ways.From("HH:mm", "d MMMM") == new TitleSky.Ways(false, false) &&
              TitleSky.Ways.From("H:mm", "d. MMMM") == new TitleSky.Ways(false, false) && TitleSky.Ways.Of(System.Globalization.CultureInfo.InvariantCulture) == new TitleSky.Ways(true, true),
            "which way is the machine's own to say (its regional settings: \"h:mm tt\" and \"MMMM d\" are 9:30 pm and November 24); a machine that says nothing gets those");

        // The sun: the game's own solar model at the land's site.
        var winter = TitleSky.SunOn(null, new DateTime(2026, 11, 24).DayOfYear);
        var summer = TitleSky.SunOn(new RoomSite(45f, 0f, 12f, RoomSite.FromRoom), new DateTime(2026, 6, 21).DayOfYear);
        var (riseModel, setModel) = LookClock.SunTimes(RoomSite.Fallback.LatitudeDeg, RoomSite.Fallback.SolarNoonH, LookTuning.Default.Sun.SunriseElevationDeg, new DateTime(2026, 11, 24).DayOfYear);
        Check(Math.Abs(winter.Rise - riseModel) < 0.001f && Math.Abs(winter.Set - setModel) < 0.001f && winter.Rise is > 6.2f and < 7f && winter.Set is > 16.6f and < 17.4f && summer.Length > 15f && summer.Length < 16.2f &&
              winter.Up(12f) && !winter.Up(17.5f) && winter.Along(winter.Rise) == 0f && Math.Abs(winter.Along(winter.Noon)!.Value - 0.5f) < 0.001f && winter.Along(18f) == null,
            $"the sun rises and sets by the game's own solar model at the land's site (the look's fallback site on 24 November: {TitleSky.Clock(winter.Rise)} to {TitleSky.Clock(winter.Set)}; 45 degrees north at midsummer: {summer.Length:0.0} hours of sun)");
        Check(TitleSky.DayPart(17.5f, winter) == "dusk" && TitleSky.DayPart(16.5f, winter) == "dusk" && TitleSky.DayPart(15.5f, winter) == "afternoon" && TitleSky.DayPart(18.5f, winter) == "evening" &&
              TitleSky.DayPart(21f, winter) == "night" && TitleSky.DayPart(6f, winter) == "dawn" && TitleSky.DayPart(5f, winter) == "night" && TitleSky.DayPart(8f, winter) == "morning" &&
              TitleSky.DayPart(20.5f, summer) == "dusk" && TitleSky.DayPart(18f, summer) == "afternoon",
            "dawn and dusk are the hour either side of the sun's own rising and setting, so the word and the window agree in winter and in summer");
        Check(TitleSky.Stars(winter.Set + 0.5f, winter) == 0f && TitleSky.Stars(winter.Rise, winter) < 0.5f && TitleSky.Stars(23f, winter) == 1f && TitleSky.Stars(12f, winter) == 0f &&
              TitleSky.Stars(18.25f) == 0f && TitleSky.Stars(3f) == 1f,
            "no star stands in a dusk or a day sky; they show as plainly as the sky is dark");

        // The moon.
        var full = TitleSky.MoonPhase(new DateTime(2026, 11, 24, 15, 0, 0, DateTimeKind.Utc));
        var fresh = TitleSky.MoonPhase(new DateTime(2026, 11, 9, 7, 0, 0, DateTimeKind.Utc));
        Check(TitleSky.MoonLitShare(full) > 0.9f && TitleSky.MoonName(full) == "full moon" && TitleSky.MoonLitShare(fresh) < 0.1f && TitleSky.MoonName(fresh) == "new moon",
            $"the moon is right on two known days (full on 24 November 2026: {TitleSky.MoonLitShare(full):0.00} lit; new on 9 November: {TitleSky.MoonLitShare(fresh):0.00} lit)");
        var local = new DateTime(2026, 11, 24, 15, 0, 0);
        var utc = TitleSky.ToUtc(local, TitleZone);
        Check(utc == new DateTime(2026, 11, 24, 21, 0, 0, DateTimeKind.Utc) && utc.Kind == DateTimeKind.Utc && Math.Abs(TitleSky.MoonPhase(utc) - TitleSky.MoonPhase(DateTime.SpecifyKind(local, DateTimeKind.Utc)) - 0.25f / (float)TitleSky.Lunation) < 1e-4f,
            "the moon's age is taken at the true instant: a local hour is turned to UTC by the machine's zone first (six hours on, six hours behind UTC)");
        static float AreaShare(Vector2[] shape, float radius) => MathF.Abs(LandPicture.Area(shape)) / (MathF.PI * radius * radius);
        var shapesTrue = new[] { 0.1f, 0.25f, 0.4f, 0.5f, 0.6f, 0.75f, 0.9f }.All(p => TitleSky.MoonLit(p, 10f, 64) is { Length: > 0 } shape && Math.Abs(AreaShare(shape, 10f) - TitleSky.MoonLitShare(p)) < 0.02f);
        var grows = TitleSky.MoonLit(0.2f, 10f).All(p => p.X >= -0.001f);
        var shrinks = TitleSky.MoonLit(0.8f, 10f).All(p => p.X <= 0.001f);
        Check(shapesTrue && grows && shrinks && TitleSky.MoonLit(0f, 10f).Length == 0 && TitleSky.MoonName(0.25f) == "half moon growing" && TitleSky.MoonName(0.75f) == "half moon shrinking",
            "the moon's lit part is drawn with the area its phase has, on the right while it grows and the left while it shrinks, and none at the new moon");
        var six = TitleSky.Sun.Plain;
        Check(TitleSky.MoonAlong(18.5f, 0.5f, six) is > 0f and < 0.1f && Math.Abs(TitleSky.MoonAlong(0f, 0.5f, six)!.Value - 0.5f) < 0.001f && TitleSky.MoonAlong(12f, 0.5f, six) == null &&
              Math.Abs(TitleSky.MoonAlong(18f, 0.25f, six)!.Value - 0.5f) < 0.001f && TitleSky.MoonAlong(3f, 0.25f, six) == null && Math.Abs(TitleSky.MoonAlong(6f, 0.75f, six)!.Value - 0.5f) < 0.001f &&
              TitleSky.MoonAlong(22f, 0.75f, six) == null && TitleSky.MoonAlong(17.4f, 0.5f, winter) is > 0f and < 0.1f && TitleSky.MoonAlong(6.2f, 0.5f, winter) is > 0.9f and < 1f,
            "the moon is in the sky only while it is up: the full moon rises as the sun sets and stands highest at midnight, the growing half is highest at dusk and down before dawn, the shrinking half rises at midnight; in winter the full moon is up the whole long night");

        // The wash, and how well the page reads under it.
        var darkest = TitleSky.Wash(3f);
        var clear = TitleSky.Wash(12f);
        var dusk = TitleSky.Wash(18f);
        Check(darkest.A > clear.A + 0.1f && dusk.R > dusk.B && darkest.B > darkest.R && TitleSky.SkyColour(3f).V < TitleSky.SkyColour(12f).V && clear.A <= 0.06f && darkest.A <= 0.25f,
            "the hour's wash is thin, blue at night, amber at dusk and nearly clear by day; the sky window is darker at night");
        var (worstInk, worstSoft, worstAt) = (99f, 99f, 0f);
        foreach (var sun in new[] { six, winter, summer })
            for (var hour = 0f; hour <= 24f; hour += 0.25f)
            {
                var ground = MenuTheme.Over(MenuTheme.Paper, TitleSky.Wash(hour, sun));
                var ink = MenuTheme.Contrast(MenuTheme.Ink, ground);
                var soft = new[] { MenuTheme.InkSoft, MenuTheme.Sepia, MenuTheme.Warning }.Min(c => MenuTheme.Contrast(MenuTheme.Legible(c, ground, MenuTheme.SoftLeast), ground));
                if (ink < worstInk) (worstInk, worstAt) = (ink, hour);
                worstSoft = Math.Min(worstSoft, soft);
            }
        var night = MenuTheme.Over(MenuTheme.Paper, TitleSky.Wash(23f));
        Check(worstInk >= MenuTheme.InkLeast - 0.01f && worstSoft >= MenuTheme.SoftLeast - 0.01f && MenuTheme.Contrast(MenuTheme.InkSoft, night) < MenuTheme.SoftLeast &&
              MenuTheme.Legible(MenuTheme.Ink, MenuTheme.Paper, MenuTheme.InkLeast) == MenuTheme.Ink && MenuTheme.InkLeast >= 7f && MenuTheme.SoftLeast >= 4.5f,
            $"under the wash, at every quarter hour of a plain, a winter and a summer day, ink reads on the paper at {worstInk:0.00} to 1 or better (the thinnest at {TitleSky.Clock(worstAt)}) and every softer ink, darkened as little as will do, at {worstSoft:0.00} or better (undarkened, the soft ink would read at {MenuTheme.Contrast(MenuTheme.InkSoft, night):0.00} at night)");
        Check(MenuTheme.Contrast(MenuTheme.Ink, MenuTheme.Paper) >= 7f && MenuTheme.Contrast(MenuTheme.InkSoft, MenuTheme.Paper) >= 4.5f && MenuTheme.Contrast(MenuTheme.Sepia, MenuTheme.Paper) >= 4.5f &&
              MenuTheme.Contrast(MenuTheme.Warning, MenuTheme.Paper) >= 4.5f && MenuTheme.Contrast(MenuTheme.Cream, MenuTheme.Ribbon) >= 7f && MenuTheme.Contrast(MenuTheme.Cream, MenuTheme.RibbonShut) >= 4.5f &&
              MenuTheme.Contrast(MenuTheme.Ink, MenuTheme.PaperLit) >= 7f && MenuTheme.Contrast(MenuTheme.InkSoft, MenuTheme.PaperDeep) >= 4.5f && Math.Abs(MenuTheme.Contrast(Colors.Black, Colors.White) - 21f) < 0.01f,
            $"by day every ink reads: ink on paper {MenuTheme.Contrast(MenuTheme.Ink, MenuTheme.Paper):0.0}, the soft ink {MenuTheme.Contrast(MenuTheme.InkSoft, MenuTheme.Paper):0.0}, sepia {MenuTheme.Contrast(MenuTheme.Sepia, MenuTheme.Paper):0.0}, the warning {MenuTheme.Contrast(MenuTheme.Warning, MenuTheme.Paper):0.0}, cream on the ribbon {MenuTheme.Contrast(MenuTheme.Cream, MenuTheme.Ribbon):0.0} and on a shut ribbon {MenuTheme.Contrast(MenuTheme.Cream, MenuTheme.RibbonShut):0.0}, a shut slip's words {MenuTheme.Contrast(MenuTheme.InkSoft, MenuTheme.PaperDeep):0.0}");

        // The game's own word, and nobody's name.
        Check(TitleSky.Plain("That island is not here.") == "That land is not here." && TitleSky.Plain("Islands visited: 2") == "Lands visited: 2" && TitleSky.Plain("ISLAND") == "LAND" &&
              TitleSky.Plain("This island has been copied; the island's builds stay.") == "This land has been copied; the land's builds stay." && TitleSky.Plain("Mira on the Garage") == "Mira on the Garage",
            "\"island\" is the files' word: in a sentence of the game's own it is \"land\", whatever the letters");
        Check(TitleSky.Plain("Islanders") == "Islanders" && TitleSky.Plain("the Islander's game") == "the Islander's game" && TitleSky.Plain("It was not opened (rooms/island/room.json is not a room).") == "It was not opened (rooms/island/room.json is not a room)." &&
              TitleSky.Plain("could not read teams/mira/modes/challenge/islands/pip") == "could not read teams/mira/modes/challenge/islands/pip" && TitleSky.Plain("no field \"island\" in it") == "no field \"island\" in it" &&
              TitleSky.Plain("island_pip was left") == "island_pip was left" && TitleWords.CouldNotOpen("no island here") == "That land could not be opened (no island here). It was left as it was.",
            "only the whole word is the game's: a longer word that holds it (Islanders), a path, a file's name and anything in brackets or quotes are left as they are");
    }

    // ---- the page fits, Continue is whole, no "island", the land is on the left ----

    private const string LongNotice = "That game is still open, so it was not touched. Close it and try again, please, then come back.";

    private async Task TestTitleFits()
    {
        var layout = SceneLayout("title-fits");
        var rooms = Folder("title-rooms");
        var longLand = new string('W', 40);
        Write(Path.Combine(rooms, "long_land", "room.json"), Manifest("long_land", longLand, tutorial: false, shape: true));
        Write(Path.Combine(rooms, "long_two", "room.json"), Manifest("long_two", longLand, tutorial: false, shape: true));
        Write(Path.Combine(rooms, "pip", "room.json"), Manifest("island_pip", "Island of Pip", tutorial: false, shape: true));
        var folder = ProjectSettings.LocalizePath(rooms);
        var islands = IslandCatalog.Scan(new[] { folder, "res://rooms" });
        var big = islands.First(i => i.RoomId == "long_land");
        var mira = TeamMaker.Create(layout, _rules, new string('M', 60), "", "Pip", "#a18cc3", "", Now);
        WriteGame(layout, mira.TeamId, "challenge", big, "brave");
        var jets = TeamMaker.Create(layout, _rules, "Jets", "", "Wisp", "#65b9b0", "", Now);
        var small = islands.First(i => i.RoomId == "island_pip");
        WriteGame(layout, jets.TeamId, "challenge", small, "brave");
        var longWords = $"{mira.DisplayName} on {longLand}, Challenge (Brave)";
        MenuModel Chosen(string team, string room, string? notice = null)
        {
            var model = new MenuModel(layout, _rules, islands);
            model.SelectTeam(team);
            model.SelectIsland(room);
            model.ChooseMode("challenge", "brave");
            if (notice != null) model.SetNotice(notice);
            return model;
        }

        // A plain game, at three sizes: nothing gives way, both buttons are the full 520 by 120, and the question still fits.
        foreach (var size in new[] { new Vector2I(1280, 720), new Vector2I(1152, 648), new Vector2I(1920, 1080) })
        {
            var model = Chosen(jets.TeamId, "island_pip");
            var (view, page) = await TitleIn(size, model, Array.Empty<string>());
            var problem = TitleProblem(view, page);
            Check(problem == null && page.Tightness == 0 && Shown(page.Tagline) && Shown(page.ContinueButton) && page.ContinueButton.Size.X >= TitleScreen.ButtonWidth && page.ContinueButton.Size.Y >= TitleScreen.ButtonHeight &&
                  page.NewGameButton.Size.X >= TitleScreen.ButtonWidth && page.NewGameButton.Size.Y >= TitleScreen.ButtonHeight && page.ContinueLine.Text == "Jets on Island of Pip, Challenge (Brave)" &&
                  page.TitleLabel.GetThemeFontSize("font_size") == 48 && Shown(page.GetNode<Label>("Margin/Pages/RightPage/Column/Head/HeadLines/JournalLabel")),
                $"{size.X}x{size.Y}: a plain game's title page fits with nothing given way: Continue {page.ContinueButton.Size} and New game {page.NewGameButton.Size}, both 520 by 120 or more, the name at 48 px, text from 18 px (the page needs {page.Needs} of {page.Has})" + (problem != null ? ": " + problem : ""));
            page.ConfirmGuardMs = 0;
            Press(page.NewGameButton);
            await Frames(4);
            problem = TitleProblem(view, page);
            Check(model.Asking && Shown(page.GoBackButton) && !Shown(page.ContinueButton) && problem == null && page.HintLabel.GetThemeFontSize("font_size") >= 20 && !Shown(page.Tagline),
                $"{size.X}x{size.Y}: with New game's question up the page still fits" + (problem != null ? ": " + problem : ""));
            // The question is read before its answers: it stands over the two buttons, what is asked on a line of its own.
            var (asked, answer) = (page.HintLabel.GetGlobalRect(), page.GoBackButton.GetGlobalRect());
            Check(Shown(page.HintLabel) && asked.End.Y <= answer.Position.Y + 0.5f && page.HintLabel.Text == TitleScreen.QuestionLines(model.Question) &&
                  page.HintLabel.Text.StartsWith("New game?\nYour Challenge game from ", StringComparison.Ordinal) && page.HintLabel.Text.Count(c => c == '\n') == 1,
                $"{size.X}x{size.Y}: the question stands over its two answers (its foot at {asked.End.Y:0}, \"Go back\" from {answer.Position.Y:0}), what is asked on its own line: {page.HintLabel.Text.Replace("\n", " / ")}");
            view.QueueFree();
            await Frames(2);
        }

        // The longest names a team and a land can have, with a long notice, in every state the page has, at 1280x720.
        var states = new List<(string Label, Func<MenuModel> Model, Func<TitleScreen, bool> Is)>
        {
            ("Continue", () => Chosen(mira.TeamId, "long_land", LongNotice), p => Shown(p.ContinueButton) && p.NoticeLabel.GetLineCount() >= 2),
            ("Bring my game here", () => Chosen(mira.TeamId, "long_two", LongNotice), p => Shown(p.BringButton) && !Shown(p.ContinueButton) && p.HintLabel.Text == longWords && p.NoticeLabel.GetLineCount() >= 2),
            ("a game still open", () => { var m = Chosen(mira.TeamId, "long_land", LongNotice); m.LeaseWait = TimeSpan.Zero; return m; }, p => Shown(p.ContinueButton) && p.HintLabel.Text == MenuModel.OpenElsewhereWords),
        };
        foreach (var (label, make, isState) in states)
        {
            using var open = label == "a game still open" ? PlayLease.TryPlay(layout, mira.TeamId) : null;
            var model = make();
            var (view, page) = await TitleIn(new Vector2I(1280, 720), model, Array.Empty<string>());
            var problem = TitleProblem(view, page);
            Check(isState(page) && problem == null && page.NewGameButton.Size.Y >= TitleScreen.LowQuietHeight && page.ContinueButton.Size.Y >= (Shown(page.ContinueButton) ? TitleScreen.LowButtonHeight : 0),
                $"1280x720, {label}, the longest names and a long notice: the page fits (it needs {page.Needs} of {page.Has}, having given way {page.Tightness} of {TitleScreen.MaxTightness}; New game {page.NewGameButton.Size})" + (problem != null ? ": " + problem : ""));
            if (label == "Continue")
            {
                var line = page.ContinueLine;
                var inside = page.ContinueButton.GetGlobalRect();
                var wide = page.ContinueButton.Size.X - 40;
                Check(line.Text == longWords && line.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming && !line.ClipText && !line.Text.Contains('…') && !line.Text.Contains("...") &&
                      line.GetLineCount() * line.GetLineHeight() <= line.Size.Y + 1 && page.ContinueWord.Text == "Continue" && inside.Encloses(line.GetGlobalRect()) && inside.Encloses(page.ContinueWord.GetGlobalRect()) &&
                      page.ContinueWord.GetGlobalRect().End.Y <= line.GetGlobalRect().Position.Y + 3 && line.GetThemeFont("font").GetStringSize(longWords, HorizontalAlignment.Left, wide, line.GetThemeFontSize("font_size")).Y <= line.Size.Y + 1,
                    $"1280x720: Continue's two lines are whole for the longest names ({line.GetLineCount()} lines of {line.GetThemeFontSize("font_size")} px in {line.Size}): \"{line.Text}\"");
            }
            // The question over it (and, where the game is still open, its refusal): the page still fits.
            page.ConfirmGuardMs = 0;
            Press(page.NewGameButton);
            await Frames(4);
            if (open != null) { Press(page.NewGameButton); await Frames(4); }
            problem = TitleProblem(view, page);
            Check(model.Asking && Shown(page.GoBackButton) && problem == null && (open == null || page.NoticeLabel.Text == PlayLease.StillOpen),
                $"1280x720, {label}: New game's question over it fits too (it needs {page.Needs} of {page.Has}, given way {page.Tightness})" + (problem != null ? ": " + problem : ""));
            view.QueueFree();
            await Frames(2);
        }

        // The page is laid out by measuring, so it fits in other faces than this machine's: here, every word wider and taller.
        var plainTightness = 0;
        var wider = new FontVariation { BaseFont = ThemeDB.FallbackFont, SpacingGlyph = 2, SpacingTop = 3, SpacingBottom = 3 };
        try
        {
            MenuTheme.UseFonts(wider, wider);
            foreach (var room in new[] { "long_land", "long_two" })
            {
                var (view, page) = await TitleIn(new Vector2I(1280, 720), Chosen(mira.TeamId, room, LongNotice), Array.Empty<string>());
                var problem = TitleProblem(view, page);
                var line = page.ContinueLine;
                plainTightness = Math.Max(plainTightness, page.Tightness);
                Check(problem == null && (room != "long_land" || (line.Text == longWords && line.GetLineCount() * line.GetLineHeight() <= line.Size.Y + 1 && page.ContinueButton.GetGlobalRect().Encloses(line.GetGlobalRect()))) &&
                      ReferenceEquals(page.ContinueWord.GetThemeFont("font"), wider),
                    $"1280x720 in wider, taller faces ({(room == "long_land" ? "Continue" : "Bring my game here")}, the longest names, a long notice): the page gives way {page.Tightness} of {TitleScreen.MaxTightness} and fits (it needs {page.Needs} of {page.Has})" + (problem != null ? ": " + problem : ""));
                view.QueueFree();
                await Frames(2);
            }
        }
        finally { MenuTheme.UseFonts(); }
        Check(plainTightness > 0 && !ReferenceEquals(MenuTheme.Hand, wider) && !ReferenceEquals(MenuTheme.Book, wider), $"(the wider faces did make the page give way, {plainTightness} of {TitleScreen.MaxTightness}, and the system's own are back)");

        // The word "island" is in nothing the page says of its own, whatever it is saying: no team, a game, the copy, a question, a refusal that says it.
        var said = new List<(string Label, Action<MenuModel> Arrange)>
        {
            ("nothing chosen", m => { }),
            ("a team with a game", m => { m.SelectTeam(mira.TeamId); m.SelectIsland("long_land"); m.ChooseMode("challenge", "brave"); }),
            ("a first game", m => { m.SelectTeam(jets.TeamId); m.SelectIsland("test_room"); m.ChooseMode("creative"); }),
            ("a refusal that says island", m => { m.SelectTeam(mira.TeamId); m.SelectIsland("long_land"); m.SetNotice(MenuModel.IslandGoneWords); }),
            ("a refusal with a path", m => { m.SelectTeam(mira.TeamId); m.SelectIsland("long_land"); m.SetNotice("This island was not opened (rooms/island/room.json)."); }),
        };
        // (The lands here are named without the word: a land's own name is the next check's.)
        var plainLands = islands.Where(i => i.RoomId != "island_pip").ToList();
        foreach (var (label, arrange) in said)
        {
            var model = new MenuModel(layout, _rules, plainLands);
            arrange(model);
            var (view, page) = await TitleIn(new Vector2I(1280, 720), model, Array.Empty<string>());
            var own = WordsOn(page).Where(w => !w.Contains("(rooms/island/room.json)", StringComparison.Ordinal)).ToList();
            Check(!own.Any(w => w.Contains("island", StringComparison.OrdinalIgnoreCase)) && page.LandName.Text.Length > 0 &&
                  (label != "a refusal that says island" || page.NoticeLabel.Text == "That land is not here any more. Nothing was changed.") &&
                  (label != "a refusal with a path" || page.NoticeLabel.Text == "This land was not opened (rooms/island/room.json)."),
                $"the word island is in nothing the page says of its own ({label}): " + string.Join(" | ", WordsOn(page).Take(14)));
            view.QueueFree();
            await Frames(2);
        }
        var asking = new MenuModel(layout, _rules, islands);
        asking.SelectTeam(mira.TeamId); asking.SelectIsland("long_land"); asking.ChooseMode("challenge", "brave");
        var (askView, askPage) = await TitleIn(new Vector2I(1280, 720), asking, Array.Empty<string>());
        askPage.ConfirmGuardMs = 0;
        Press(askPage.NewGameButton);
        await Frames(3);
        Check(asking.Asking && !HasIsland(askPage) && askPage.HintLabel.Text.StartsWith("New game?\nYour Challenge game", StringComparison.Ordinal), "nor while New game's question is up: " + askPage.HintLabel.Text.Replace("\n", " / "));
        askView.QueueFree();

        // A name is shown as it is: the page rewords the game's sentences, never what the family called a team or a land.
        var folk = TeamMaker.Create(layout, _rules, "Islanders", "", "Pip", "#a18cc3", "", Now);
        WriteGame(layout, folk.TeamId, "creative", small);
        var named = new MenuModel(layout, _rules, islands);
        named.SelectTeam(folk.TeamId); named.SelectIsland("island_pip"); named.ChooseMode("creative");
        var (pipView, pipPage) = await TitleIn(new Vector2I(1280, 720), named, Array.Empty<string>());
        Check(pipPage.LandName.Text == "Island of Pip" && pipPage.ContinueLine.Text == "Islanders on Island of Pip, Creative" && pipPage.ContinueButton.TooltipText == "Continue: Islanders on Island of Pip, Creative" &&
              TitleProblem(pipView, pipPage) == null,
            $"a team called Islanders on a land called \"Island of Pip\" keep their names: \"{pipPage.LandName.Text}\", \"{pipPage.ContinueLine.Text}\"");
        pipView.QueueFree();
        await Frames(2);

        // The land's picture is on the left, drawn from the room's shape data; a room without one gets the plain stand-in.
        var tutorialModel = new MenuModel(layout, _rules, IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" }));
        tutorialModel.SelectTeam(mira.TeamId);
        tutorialModel.SelectIsland("tutorial_isle");
        var (view1, shaped) = await TitleIn(new Vector2I(1280, 720), tutorialModel, Array.Empty<string>());
        var land = shaped.Picture.Land;
        var spine = view1.Size.X * 0.465f + 12;
        var allButtons = ControlsUnder(shaped).OfType<Button>().Where(Shown).ToList();
        var counts = land != null ? LandCounts.Of(land.Strokes(shaped.Picture.Size)) : null;
        Check(shaped.Picture.HasShape && land != null && shaped.Picture.FloorTriangles >= 2 && shaped.Picture.Drawn.Bands == 1 && shaped.Picture.Drawn.Things == 2 && counts != null && shaped.Picture.Drawn == counts &&
              shaped.Picture.GetGlobalRect().End.X <= spine && shaped.Picture.GetGlobalRect().Position.X > 0 && allButtons.Count >= 3 && allButtons.All(b => b.GetGlobalRect().Position.X >= spine) &&
              shaped.LandName.GetGlobalRect().End.X <= spine && shaped.LandName.Text == "Tutorial Isle",
            $"the land's picture is on the left page ({shaped.Picture.GetGlobalRect()}, the spine at {spine}), the buttons on the right, and it is drawn from the room's shape data: {shaped.Picture.Drawn}");
        // The compass mark: the letter in a child's size, and the arrow clear under it.
        var north = shaped.North;
        Check(NorthMark.LetterSize >= MenuTheme.SmallestText && north.ArrowTip >= north.LetterBaseline + 2f && north.Size.Y >= north.ArrowTip + NorthMark.ArrowHeight && north.Size.X >= 14f &&
              new Rect2(Vector2.Zero, view1.Size).Encloses(north.GetGlobalRect()) && north.GetGlobalRect().End.X <= spine,
            $"the compass mark's N is {NorthMark.LetterSize} px and its arrow starts {north.ArrowTip - north.LetterBaseline} px under the letter's line, inside a mark of {north.Size}");
        view1.QueueFree();
        var plainModel = new MenuModel(layout, _rules, IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" }));
        plainModel.SelectTeam(mira.TeamId);
        plainModel.SelectIsland("test_room");
        var (view2, standIn) = await TitleIn(new Vector2I(1280, 720), plainModel, Array.Empty<string>());
        Check(!standIn.Picture.HasShape && standIn.Picture.DrawnStrokes.Count > 0 && standIn.Picture.GetGlobalRect().End.X <= spine && standIn.LandName.Text == "Test room",
            "a room without shape data still has its picture place on the left, with the plain stand-in drawn: " + standIn.LandName.Text);
        view2.QueueFree();
        await Frames(2);
    }

    // ---- this hour ----

    private async Task TestTitleHour()
    {
        var layout = SceneLayout("title-hour");
        var islands = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" });
        TeamMaker.Create(layout, _rules, "Jets", "", "Wisp", "#65b9b0", "", Now);
        MenuModel Fresh() { var m = new MenuModel(layout, _rules, islands); m.SelectTeam("jets"); return m; }
        var size = new Vector2I(1280, 720);
        var (morningView, morning) = await TitleIn(size, Fresh(), new[] { "--look-clock=09:30", "--look-date=2026-11-24" }, () => new DateTime(2026, 1, 1, 3, 0, 0));
        Check(morning.DateLine.Text == "Tuesday, November 24" && morning.HourLine.Text == "Morning, 9:30 am. The full moon." && morning.Dial.Body == "sun" && morning.Wash.Color.A < 0.08f && morning.Sky.HourPinned &&
              morning.Sun.Rise is > 6.2f and < 7f,
            $"a pinned clock moves the page: \"{morning.DateLine.Text}\", \"{morning.HourLine.Text}\", the {morning.Dial.Body} in the window, a wash of {morning.Wash.Color.A:0.00}");
        morningView.QueueFree();

        var (nightView, night) = await TitleIn(size, Fresh(), new[] { "--look-clock=22:00", "--look-date=2026-11-24" });
        var reads = night.OnPaper.Select(o => (o.Control.Name, Ratio: MenuTheme.Contrast(o.Control.GetThemeColor("font_color"), night.Ground), o.Least)).ToList();
        Check(night.HourLine.Text == "Night, 10:00 pm. The full moon." && night.Dial.Body == "moon" && night.Dial.Stars == 1f && night.Wash.Color.A > 0.12f && night.Wash.Color.B > night.Wash.Color.R && TitleProblem(nightView, night) == null,
            $"at night the moon is in the window with its phase, among the stars, and the wash is a thin blue ({night.Wash.Color.A:0.00}): \"{night.HourLine.Text}\"");
        Check(reads.Count >= 10 && reads.All(r => r.Ratio >= r.Least - 0.01f) && MenuTheme.Contrast(MenuTheme.InkSoft, night.Ground) < MenuTheme.SoftLeast && night.Ground != MenuTheme.Paper &&
              MenuTheme.Contrast(night.TitleLabel.GetThemeColor("font_color"), night.Ground) >= MenuTheme.InkLeast - 0.01f,
            "at night every word on the paper still reads: " + string.Join(", ", reads.Select(r => $"{r.Name} {r.Ratio:0.0}")));
        night.HourWash = false;
        night.UpdateSky();
        Check(night.Wash.Color.A == 0f && night.Ground == MenuTheme.Paper && night.HourLine.GetThemeColor("font_color") == MenuTheme.InkSoft, "the wash can be switched off: the paper is plain and the inks are their own");
        nightView.QueueFree();
        var (plainView, plainPaper) = await TitleIn(size, Fresh(), new[] { "--look-clock=22:00", TitleScreen.NoHourWashArgument });
        Check(!plainPaper.HourWash && plainPaper.Wash.Color.A == 0f, "a launch with --no-hour-wash leaves the paper plain");
        plainView.QueueFree();

        // Dusk in late November: the sun has set by the game's own model, the full moon is just up, and no star stands in the orange sky.
        var (duskView, dusk) = await TitleIn(size, Fresh(), new[] { "--look-clock=17:50", "--look-date=2026-11-24" });
        Check(dusk.HourLine.Text == "Dusk, 5:50 pm. The full moon." && dusk.Dial.SunAlong == null && dusk.Dial.Body == "moon" && dusk.Dial.MoonAlong is > 0f and < 0.15f && dusk.Dial.Stars == 0f && dusk.Wash.Color.R > dusk.Wash.Color.B,
            $"at dusk the word and the window agree: \"{dusk.HourLine.Text}\", the sun down, the full moon {dusk.Dial.MoonAlong:0.00} along its rise, no stars, an amber wash");
        duskView.QueueFree();
        var (sixView, sixToSix) = await TitleIn(size, Fresh(), new[] { "--look-clock=17:50", "--look-date=2026-11-24" }, arrange: p => p.TrueSun = false);
        Check(sixToSix.Sun == TitleSky.Sun.Plain && sixToSix.Dial.Body == "sun" && sixToSix.HourLine.Text.StartsWith("Dusk, ", StringComparison.Ordinal), "with the land's own sun switched off the sun is up from six to six, as the page first drew it");
        sixView.QueueFree();
        var (sixArgView, sixByLaunch) = await TitleIn(size, Fresh(), new[] { "--look-clock=17:50", "--look-date=2026-11-24", TitleScreen.PlainSunArgument });
        Check(!sixByLaunch.TrueSun && sixByLaunch.Sun == TitleSky.Sun.Plain && sixByLaunch.Dial.Body == "sun", "and a launch switches it off with " + TitleScreen.PlainSunArgument);
        sixArgView.QueueFree();
        var (newView, newMoon) = await TitleIn(size, Fresh(), new[] { "--look-clock=22:00", "--look-date=2026-11-09" });
        Check(newMoon.Dial.Body == "stars" && newMoon.Dial.MoonAlong == null && newMoon.HourLine.Text == "Night, 10:00 pm. The new moon.", "at the new moon there is no moon in the window, only stars: " + newMoon.HourLine.Text);
        newView.QueueFree();

        // The machine's own clock, its zone, and how it writes them.
        var (realView, real) = await TitleIn(size, Fresh(), Array.Empty<string>());
        var instant = new DateTime(2026, 11, 24, 20, 5, 0, DateTimeKind.Utc);
        Check(real.HourLine.Text.StartsWith("Afternoon, 2:05 pm.", StringComparison.Ordinal) && real.DateLine.Text == "Tuesday, November 24" && !real.Sky.Pinned && Math.Abs(real.Dial.Phase - TitleSky.MoonPhase(instant)) < 1e-5f &&
              Math.Abs(real.Dial.Phase - TitleSky.MoonPhase(DateTime.SpecifyKind(TitleNow, DateTimeKind.Utc))) > 0.005f,
            "with no pin it is the clock the page is given (never the test machine's own), and the moon's age is taken at that instant in UTC: " + real.HourLine.Text);
        realView.QueueFree();
        var (otherView, other) = await TitleIn(size, Fresh(), Array.Empty<string>(), arrange: p => p.Ways = new TitleSky.Ways(false, false));
        Check(other.HourLine.Text.StartsWith("Afternoon, 14:05.", StringComparison.Ordinal) && other.DateLine.Text == "Tuesday 24 November", "a machine that writes twenty-four hours and the day first gets them so: " + other.DateLine.Text + ", " + other.HourLine.Text);
        otherView.QueueFree();
        var set = false;
        var (envView, env) = await TitleIn(size, Fresh(), Array.Empty<string>(), arrange: p => p.ReadEnvironment = name => { set = true; return name == "GARAGE_GAME_LOOK_CLOCK" ? "06:40" : null; });
        Check(set && env.HourLine.Text.StartsWith("Dawn, 6:40 am.", StringComparison.Ordinal) && env.Sky.HourPinned, "the look's pins in the environment move the page too, through the page's own reader: " + env.HourLine.Text);
        envView.QueueFree();

        // A date pin alone does not stop the clock; a pinned hour stands still.
        var clock = new DateTime(2026, 11, 24, 10, 0, 0);
        var (runView, running) = await TitleIn(size, Fresh(), new[] { "--look-date=2027-03-01" }, () => clock);
        var first = running.HourLine.Text;
        clock = clock.AddMinutes(45);
        running._Process(21);
        Check(running.DateLine.Text == "Monday, March 1" && first.StartsWith("Morning, 10:00 am.", StringComparison.Ordinal) && running.HourLine.Text.StartsWith("Morning, 10:45 am.", StringComparison.Ordinal) && running.Sky.DatePinned && !running.Sky.HourPinned,
            $"a pinned date alone (1 March 2027, a Monday) does not stop the clock: \"{first}\" then \"{running.HourLine.Text}\"");
        runView.QueueFree();
        var (stillView, still) = await TitleIn(size, Fresh(), new[] { "--look-clock=09:30" }, () => clock);
        clock = clock.AddMinutes(45);
        still._Process(21);
        Check(still.HourLine.Text.StartsWith("Morning, 9:30 am.", StringComparison.Ordinal), "a pinned hour stands still: " + still.HourLine.Text);
        stillView.QueueFree();
        await Frames(2);
    }

    // ---- New game and Continue from the page, through MenuBoot ----

    private async Task<TitleScreen> OpenTitleBoot(params string[] arguments)
    {
        MainMenu.LaunchArguments = () => arguments;
        _boot = new MenuBoot { AutoOpen = false, OnNewTitle = Hermetic };
        AddChild(_boot);
        var page = _boot.OpenTitle(arguments);
        await Frames(3);
        return page;
    }

    private async Task TestTitleStart()
    {
        // MenuBoot: the title page first, the Choose page where --choose says so.
        var empty = SceneLayout("title-empty");
        _boot = new MenuBoot { AutoOpen = false, OnNewTitle = Hermetic };
        AddChild(_boot);
        _boot.OpenFirst(Array.Empty<string>());
        await Frames(2);
        var first = _boot.Title != null && _boot.Menu == null;
        _boot.OpenFirst(new[] { MenuBoot.ChooseArgument });
        await Frames(2);
        Check(first && _boot.Menu != null && _boot.Title == null, "the game opens on the title page; --choose opens the Choose page first");
        await CloseGame();

        // A machine with no team: New game has nobody to start, so it opens the Choose page, where "New team" is.
        var lonely = await OpenTitleBoot();
        Check(Shown(lonely.NewGameButton) && !Shown(lonely.ContinueButton) && lonely.HintLabel.Text == "Choose your character first." && lonely.LandName.Text.Length > 0, "no team: New game alone, and the page says what to choose first: " + lonely.HintLabel.Text);
        await Click(lonely.NewGameButton);
        Check(_boot!.Menu != null && _boot.Title == null && _boot.World == null && !Directory.Exists(empty.TeamsRoot), "New game with no team opens the Choose page and makes nothing");
        await CloseGame();

        var layout = SceneLayout("title-start");
        TeamMaker.Create(layout, _rules, "Jets", "", "Wisp", "#65b9b0", "", Now);
        var folder = layout.TeamFolder("jets");

        // No game: New game starts a first one at once, nothing asked.
        var page = await OpenTitleBoot("--room=test_room", "--mode=challenge", "--difficulty=standard");
        Check(Shown(page.NewGameButton) && !page.NewGameButton.Disabled && page.NewGameButton.Text == "New game" && !Shown(page.ContinueButton) && !Shown(page.BringButton) && !Shown(page.GoBackButton) &&
              page.HintLabel.Text == "Jets on Test room, Challenge (Standard)" && page.NewGameButton.HasFocus() && page.LandName.Text == "Test room",
            "a launch that names a room opens the title page on it, with the team, the game it would start in words, and New game alone and focused: " + page.HintLabel.Text);
        Check(Shown(page.HintLabel) && page.HintLabel.GetGlobalRect().Position.Y >= page.NewGameButton.GetGlobalRect().End.Y - 0.5f,
            $"a line that is no question stands under the buttons (from {page.HintLabel.GetGlobalRect().Position.Y:0}, New game's foot at {page.NewGameButton.GetGlobalRect().End.Y:0})");
        await Click(page.NewGameButton);
        var firstGame = await WaitForRoom();
        if (firstGame is not { } game) return;
        Check(_boot!.Title == null && game.Host.SaveTeamIdentity == new SaveTeam("jets", "challenge", "standard") && NothingPutAway(layout),
            $"a real click on New game starts a first game at once, nothing asked ({game.Host.SaveTeamIdentity}), and the title page is gone");
        await CloseGame();

        // Continue: the game as it was left.
        var again = await OpenTitleBoot();
        Check(Shown(again.ContinueButton) && !again.ContinueButton.Disabled && again.ContinueLine.Text == "Jets on Test room, Challenge (Standard)" && again.ContinueButton.HasFocus() &&
              Shown(again.NewGameButton) && !again.NewGameButton.Disabled && !Shown(again.BringButton) && !Shown(again.GoBackButton) && again.ContinueWord.Text == "Continue" &&
              again.ContinueButton.GetGlobalRect().Encloses(again.ContinueLine.GetGlobalRect()),
            $"a game in this mode: the title page opens on it with Continue (focused, two lines) and New game: \"{again.ContinueWord.Text}\" / \"{again.ContinueLine.Text}\"");
        await Click(again.ContinueButton);
        var second = await WaitForRoom();
        if (second is not { } left) return;
        Check(left.Host.SaveTeamIdentity == new SaveTeam("jets", "challenge", "standard") && NothingPutAway(layout), "Continue opens the game as it was left, and puts nothing away");
        await CloseGame();

        // The buttons wait on the team's lease (MENU-3's rule). After "Back to the menu" the page opens while the old window is
        // still leaving: this is the state the title page is in every time a game is left.
        var before = Hashes(folder);
        using (var leaving = PlayLease.TryPlay(layout, "jets")!)
        {
            var held = await OpenTitleBoot();
            var shut = Shown(held.ContinueButton) && held.ContinueButton.Disabled && Shown(held.NewGameButton) && held.NewGameButton.Disabled && held.HintLabel.Text == MenuModel.StillClosingWords && Shown(held.HintLabel);
            await Click(held.ContinueButton);
            await Click(held.NewGameButton);
            Press(held.ContinueButton);
            Press(held.NewGameButton);
            await Frames(2);
            Check(shut && _boot!.World == null && _boot.Title == held && !held.Model.Asking && held.ContinueButton.Disabled && held.NewGameButton.Disabled && Same(before, Hashes(folder)) && NothingPutAway(layout) &&
                  MenuTheme.Contrast(MenuTheme.Cream, MenuTheme.RibbonShut) >= MenuTheme.SoftLeast,
                "while the team's room is still leaving, the title page's Continue and New game are shut and say why, and a click or a press starts nothing and moves nothing: " + held.HintLabel.Text);
        }
        var opened = _boot!.Title!;
        await Frames(30);
        Check(!opened.ContinueButton.Disabled && !opened.NewGameButton.Disabled && !Shown(opened.HintLabel) && _boot.World == null, "as soon as the room has let go, the buttons open by themselves");
        opened.Model.LeaseWait = TimeSpan.Zero;
        opened.ConfirmGuardMs = 0;
        using (var other = PlayLease.TryPlay(layout, "jets")!)
        {
            opened.Refresh();
            await Frames(12);
            var open = !opened.ContinueButton.Disabled && !opened.NewGameButton.Disabled && opened.HintLabel.Text == MenuModel.OpenElsewhereWords;
            await Click(opened.NewGameButton);
            await Click(opened.NewGameButton);
            Check(open && opened.Model.Asking && Shown(opened.NoticeLabel) && opened.NoticeLabel.Text == PlayLease.StillOpen && _boot.World == null && Same(before, Hashes(folder)) && NothingPutAway(layout),
                "once the wait is over a game still open in another window is said so; New game asks, then is refused with the words, and nothing moves: " + opened.NoticeLabel.Text);
        }
        await Frames(30);
        Check(!Shown(opened.NoticeLabel) && opened.Model.Asking && opened.HintLabel.Text == TitleScreen.QuestionLines(opened.Model.Question), "and once it is closed the refusal's words go by themselves; the question stays, for New game to go on");
        await Click(opened.GoBackButton);
        await CloseGame();

        // New game over the game: asks once, "Go back" has the focus; a double click is not the answer; Esc and "Go back" take it down.
        var third = await OpenTitleBoot();
        third.ConfirmGuardMs = 600_000;
        await Click(third.NewGameButton);
        var savedOn = MenuModel.Day(NewestWrite(layout, "jets", "challenge"));
        Check(third.Model.Asking && Shown(third.GoBackButton) && third.GoBackButton.HasFocus() && !Shown(third.ContinueButton) && third.NewGameButton.Text == "Yes, new game" &&
              third.HintLabel.Text == $"New game?\nYour Challenge game from {savedOn} is put away, not deleted." && third.HintLabel.GetThemeFontSize("font_size") >= 18 && _boot!.World == null &&
              third.HintLabel.GetGlobalRect().End.Y <= third.GoBackButton.GetGlobalRect().Position.Y + 0.5f && Same(before, Hashes(folder)) && NothingPutAway(layout),
            "New game over a game asks once: the question stands over the buttons that answer it, \"Go back\" has the focus, and nothing has moved: " + third.HintLabel.Text.Replace("\n", " / "));
        await Click(third.NewGameButton);
        Check(third.Model.Asking && _boot.World == null && Same(before, Hashes(folder)) && NothingPutAway(layout), "a second click straight after the first is not the answer");
        await PressKey(Key.Escape);
        Check(!third.Model.Asking && Shown(third.ContinueButton) && third.ContinueButton.HasFocus() && !Shown(third.GoBackButton) && _boot.Title == third && Same(before, Hashes(folder)), "the Esc key goes back: the question is gone and Continue has the focus");
        await Click(third.NewGameButton);
        await Click(third.GoBackButton);
        Check(!third.Model.Asking && Shown(third.ContinueButton) && third.NewGameButton.Text == "New game" && NothingPutAway(layout), "and so does a click on \"Go back\"");
        // Asked again and answered: the old game is put away whole, and a first game starts.
        await Click(third.NewGameButton);
        third.ConfirmGuardMs = 0;
        await Click(third.NewGameButton);
        var fresh = await WaitForRoom();
        if (fresh is not { } started) return;
        Check(GameSetAside.List(layout, "jets").Count == 1 && started.Host.SaveTeamIdentity == new SaveTeam("jets", "challenge", "standard") && !NothingPutAway(layout),
            $"New game pressed again puts the old game away ({GameSetAside.List(layout, "jets").Count} kept) and starts a first one");
        await CloseGame();

        // "Bring my game here" where the game is on another land; it waits on the lease as the others do.
        var scene = await OpenTitleBoot();
        var model = scene.Model;
        WriteGame(layout, "jets", "challenge", model.Island, "standard");
        model.Reload();
        model.SelectIsland("plain_isle");
        scene.Refresh();
        await Frames(3);
        Check(Shown(scene.BringButton) && scene.BringButton.Text == "Bring my game here" && !scene.BringButton.Disabled && !Shown(scene.ContinueButton) && Shown(scene.NewGameButton) && !HasIsland(scene) &&
              scene.NewGameButton.Size.Y >= TitleScreen.ButtonHeight && scene.HintLabel.Text == "Jets on Plain Isle, Challenge (Standard)",
            "where this mode's game is on another land: \"Bring my game here\" and New game, no Continue: " + scene.HintLabel.Text);
        using (var leaving = PlayLease.TryPlay(layout, "jets")!)
        {
            scene.Refresh();
            var shut = scene.BringButton.Disabled && scene.NewGameButton.Disabled && scene.HintLabel.Text == MenuModel.StillClosingWords;
            await Click(scene.BringButton);
            Check(shut && _boot!.World == null, "\"Bring my game here\" waits on the team's lease too");
        }
        await Frames(30);
        Check(!scene.BringButton.Disabled && !scene.NewGameButton.Disabled, "and opens again by itself");

        // A room rebuilt since its save (a save only under an older manifest): New game alone, and the page says the old game is kept.
        var plain = model.Island!;
        Write(layout.IslandSave("jets", "challenge", "plain_isle", plain.ManifestPrefix == "0123456789abcdef" ? "fedcba9876543210" : "0123456789abcdef"), "{}");
        scene.Refresh();
        await Frames(3);
        Check(model.Offer.Case == StartCase.Rebuilt && Shown(scene.NewGameButton) && !scene.NewGameButton.Disabled && !Shown(scene.ContinueButton) && !Shown(scene.BringButton) && scene.HintLabel.Text == MenuModel.RebuiltWords &&
              scene.NewGameButton.Size.Y >= TitleScreen.ButtonHeight && !HasIsland(scene),
            "a room rebuilt since its save: New game alone, and the page says so: " + scene.HintLabel.Text);
        scene.ConfirmGuardMs = 0;
        await Click(scene.NewGameButton);
        Check(model.Asking && Shown(scene.GoBackButton) && _boot!.World == null && GameSetAside.List(layout, "jets").Count == 1, "over a rebuilt room's old game New game asks first, as over any game");
        var over = Shown(scene.HintLabel) && scene.HintLabel.GetGlobalRect().End.Y <= scene.GoBackButton.GetGlobalRect().Position.Y + 0.5f;
        await Click(scene.GoBackButton);
        Check(over && !model.Asking && Shown(scene.HintLabel) && scene.HintLabel.Text == MenuModel.RebuiltWords && scene.HintLabel.GetGlobalRect().Position.Y >= scene.NewGameButton.GetGlobalRect().End.Y - 0.5f,
            "the question stood over the buttons, and \"Go back\" puts the page's own line under them again: " + scene.HintLabel.Text);

        // A game New game put away, and none in play: the page says where it went (so also when the land then did not open).
        GameSetAside.PutAway(layout, "jets", "challenge", DateTime.UtcNow);
        model.Reload();
        model.SelectIsland("test_room");
        scene.Refresh();
        await Frames(3);
        Check(model.Offer.Case == StartCase.NoGame && scene.HintLabel.Text.StartsWith("Jets on Test room, Challenge (", StringComparison.Ordinal) &&
              scene.HintLabel.Text.EndsWith(")\nYour last Challenge game is put away: New game makes a new one.", StringComparison.Ordinal) && TitleWords.PutAway("Challenge") == "Your last Challenge game is put away: New game makes a new one." &&
              Shown(scene.NewGameButton) && !Shown(scene.ContinueButton) && !Shown(scene.BringButton) && GameSetAside.List(layout, "jets").Count == 2,
            "with this mode's game put away and none in play, the title page says where it went: " + scene.HintLabel.Text.Replace("\n", " / "));
        await CloseGame();

        // Choose and Esc go between the two pages over one model. What the player chose on the Choose page stands when Esc
        // brings the title page back, also under a launch that named a room, a mode and an hour; and it stands on the way back.
        var launched = await OpenTitleBoot("--room=test_room", "--mode=challenge", "--difficulty=standard", "--look-clock=09:30");
        var shared = launched.Model;
        await Click(launched.ChooseButton);
        var choose = _boot!.Menu;
        Check(choose != null && _boot.Title == null && ReferenceEquals(choose.Model, shared) && shared.RoomId == "test_room" && shared.Mode == "challenge", "\"Choose\" opens the Choose page over the same choices");
        if (choose == null) return;
        await Click(choose.IslandButtons["plain_isle"]);
        await Click(choose.ModeButtons["creative"]);
        var chosen = $"{shared.RoomId}/{shared.Mode}";
        await PressKey(Key.Escape);
        var back = _boot.Title;
        Check(chosen == "plain_isle/creative" && back != null && _boot.Menu == null && ReferenceEquals(back.Model, shared) && shared.RoomId == "plain_isle" && shared.Mode == "creative" &&
              back.LandName.Text == "Plain Isle" && back.HintLabel.Text.StartsWith("Jets on Plain Isle, Creative", StringComparison.Ordinal) && back.HourLine.Text.StartsWith("Morning, 9:30 am.", StringComparison.Ordinal),
            $"the Esc key on the Choose page comes back to the title page with what was chosen there, not the launch's ({chosen}): \"{back?.LandName.Text}\", \"{back?.HintLabel.Text.Replace("\n", " / ")}\", the launch's hour still pinned");
        if (back == null) return;
        await Click(back.ChooseButton);
        var chooseAgain = _boot.Menu;
        Check(chooseAgain != null && shared.RoomId == "plain_isle" && shared.Mode == "creative" && chooseAgain.IslandButtons["plain_isle"].ButtonPressed && chooseAgain.ModeButtons["creative"].ButtonPressed,
            "and \"Choose\" again shows those choices, not the launch's");
        if (chooseAgain == null) return;
        // The Choose page's own Esc comes first: its question goes, the page stays.
        await Click(chooseAgain.IslandButtons["test_room"]);
        await Click(chooseAgain.ModeButtons["challenge"]);
        WriteGame(layout, "jets", "challenge", shared.Island, "standard");
        shared.Reload();
        chooseAgain.Refresh();
        chooseAgain.ConfirmGuardMs = 0;
        await Click(chooseAgain.NewGameButton);
        var up = shared.Asking;
        await PressKey(Key.Escape);
        Check(up && !shared.Asking && _boot.Menu == chooseAgain && _boot.Title == null, "Esc on the Choose page takes its question down first, and only then, pressed again, goes back to the title page");
        await PressKey(Key.Escape);
        Check(_boot.Title != null && _boot.Menu == null && shared.RoomId == "test_room" && shared.Mode == "challenge" && Shown(_boot.Title.ContinueButton), "(pressed again: the title page, on the game chosen)");
        await CloseGame();

        // A page that opens where this mode's game is on another land: "Bring my game here" leads, so the keyboard starts on it
        // (Enter there is not New game).
        var elsewhere = await OpenTitleBoot("--room=plain_isle", "--mode=challenge", "--difficulty=standard");
        Check(Shown(elsewhere.BringButton) && !Shown(elsewhere.ContinueButton) && Shown(elsewhere.NewGameButton) && elsewhere.BringButton.HasFocus() && !elsewhere.NewGameButton.HasFocus(),
            $"a title page that opens on \"Bring my game here\" gives it the focus, not New game (the focus is on {elsewhere.GetViewport().GuiGetFocusOwner()?.Name})");
        await CloseGame();
        MainMenu.LaunchArguments = null;
    }

    /// <summary>
    /// Under --broken-room (the room's own refusal is an engine error on stderr, which the runners treat as a failure): a land that
    /// cannot be opened brings back the page the game was started from. From the title page that is the title page, with a plain
    /// word about it; and where New game had put a game away on the way, the page says so.
    /// </summary>
    private async Task TestTitleBrokenRoom()
    {
        var layout = SceneLayout("title-broken");
        TeamMaker.Create(layout, _rules, "Jets", "", "Wisp", "#65b9b0", "", Now);
        async Task<TitleScreen?> ComeBack()
        {
            TitleScreen? page = null;
            for (var waiting = System.Diagnostics.Stopwatch.StartNew(); waiting.Elapsed.TotalSeconds < 30 && ((page = _boot!.Title) == null || _boot.World != null);) await Frames(2);
            await Frames(3);
            return page;
        }
        var title = await OpenTitleBoot("--room=plain_isle", "--mode=creative");
        await Click(title.NewGameButton);
        var back = await ComeBack();
        Check(back != null && back != title && _boot!.World == null && _boot.Menu == null && Shown(back.NoticeLabel) && back.NoticeLabel.Text.StartsWith("That land could not be opened (", StringComparison.Ordinal) &&
              back.NoticeLabel.Text.EndsWith("It was left as it was.", StringComparison.Ordinal) && !HasIsland(back) && back.HintLabel.Text == "Jets on Plain Isle, Creative" && Shown(back.NewGameButton) && NothingPutAway(layout),
            "from the title page, a land that cannot be opened brings the title page back with a word about it: " + back?.NoticeLabel.Text);
        await CloseGame();

        // New game over a game, on a land that then does not open: the old game is put away all the same (kept whole), and the
        // title page that comes back says where it went.
        var elsewhere = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" }).First(i => i.RoomId == "test_room");
        WriteGame(layout, "jets", "creative", elsewhere);
        var over = await OpenTitleBoot("--room=plain_isle", "--mode=creative");
        over.ConfirmGuardMs = 0;
        var offered = over.Model.Offer.Case;
        await Click(over.NewGameButton);
        var asked = over.Model.Asking && _boot!.World == null && NothingPutAway(layout);
        await Click(over.NewGameButton);
        var again = await ComeBack();
        var away = GameSetAside.List(layout, "jets");
        Check(offered == StartCase.GameElsewhere && asked && again != null && _boot!.World == null && again.NoticeLabel.Text.StartsWith("That land could not be opened (", StringComparison.Ordinal) &&
              away.Count == 1 && away[0].Ledger.Mode == "creative" && GameSetAside.Problems(away[0].Folder).Count == 0 && again.Model.Offer.Case == StartCase.NoGame && !Shown(again.ContinueButton) && !Shown(again.BringButton) &&
              again.HintLabel.Text == "Jets on Plain Isle, Creative\nYour last Creative game is put away: New game makes a new one.",
            "New game from the title page over a game, on a land that then cannot be opened: the old game is put away whole, and the title page that comes back says so: " + again?.HintLabel.Text.Replace("\n", " / "));
        await CloseGame();

        // Started from the Choose page that the title page opened: it is the Choose page that comes back, and Esc still leads to the title page.
        var third = await OpenTitleBoot("--room=plain_isle", "--mode=creative");
        await Click(third.ChooseButton);
        var choose = _boot!.Menu!;
        Press(choose.NewGameButton);
        MainMenu? menu = null;
        for (var waiting = System.Diagnostics.Stopwatch.StartNew(); waiting.Elapsed.TotalSeconds < 30 && ((menu = _boot.Menu) == null || menu == choose || _boot.World != null);) await Frames(2);
        await Frames(3);
        var cameBack = menu != null && menu != choose && _boot.Title == null && menu.NoticeLabel.Text.StartsWith("That land could not be opened (", StringComparison.Ordinal);
        await PressKey(Key.Escape);
        Check(cameBack && _boot.Title != null && _boot.Menu == null, "started from the Choose page, it is the Choose page that comes back, and Esc there still goes to the title page");
        await CloseGame();
        MainMenu.LaunchArguments = null;
    }
}
