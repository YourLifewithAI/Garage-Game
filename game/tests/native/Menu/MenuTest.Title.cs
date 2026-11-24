using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// The title page (Run 4, MENU-4), headless. The page is laid out in a sub-viewport of a named size, so "fits 1280x720" is measured
/// at 1280x720 whatever the test window is: every visible control is on the screen, no label is clipped or trimmed, no button's word
/// is wider than the button, and the page's minimum size is inside the screen; Continue's two lines are whole even for the longest
/// names a team and a land can have; the word "island" is on no control, tooltip or notice in any of the page's states; the land's
/// picture is drawn, on the left page, from its shape data; and New game and Continue are driven by real clicks through MenuBoot
/// (OpenTitle), by the start rule of MENU-3. The hour, the date and the moon are pinned by the look's own clock pins. What the page
/// looks like on a screen is the capture scene's (native_menu_title_capture.tscn), for the founder's eye.
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

    private async Task<(SubViewport View, TitleScreen Page)> TitleIn(Vector2I size, MenuModel model, string[] arguments, Func<DateTime>? now = null)
    {
        var view = new SubViewport { Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(view);
        var page = new TitleScreen { Model = model, LaunchArguments = () => arguments };
        if (now != null) page.LocalNow = now;
        view.AddChild(page);
        await Frames(4);
        return (view, page);
    }

    /// <summary>What does not fit, in words; null when the page fits its screen.</summary>
    private static string? TitleProblem(SubViewport view, TitleScreen page)
    {
        var screen = new Rect2(Vector2.Zero, view.Size).Grow(0.5f);
        var margin = page.GetNode<MarginContainer>("Margin");
        var least = margin.GetCombinedMinimumSize();
        if (least.X > view.Size.X + 0.5f || least.Y > view.Size.Y + 0.5f) return $"the page needs {least} of {view.Size}";
        foreach (var control in ControlsUnder(page).Where(Shown))
        {
            if (!screen.Encloses(control.GetGlobalRect())) return $"{control.Name} {control.GetGlobalRect()} is off the screen {view.Size}";
            if (control is Label label && label.Text.Length > 0 && label.GetLineCount() * label.GetLineHeight() > label.Size.Y + 1) return $"{label.Name} is clipped ({label.GetLineCount()} lines in {label.Size.Y})";
            if (control is Button { Text.Length: > 0 } button && button.GetThemeFont("font").GetStringSize(button.Text, HorizontalAlignment.Left, -1, button.GetThemeFontSize("font_size")).X > button.Size.X - 8)
                return $"{button.Name} is narrower than its word";
        }
        if (page.LeftPage.GetGlobalRect().End.X > page.RightPage.GetGlobalRect().Position.X + 0.5f) return "the two pages overlap";
        return null;
    }

    // ---- the hour, the moon, the words ----

    private void TestTitleSky()
    {
        var pinned = TitleSky.Read(new[] { "--look-clock=09:30", "--look-date=2026-11-24" }, _ => null, new DateTime(2026, 1, 2, 3, 4, 0));
        var real = TitleSky.Read(Array.Empty<string>(), _ => null, new DateTime(2026, 1, 2, 15, 30, 0));
        Check(pinned.Pinned && Math.Abs(pinned.Hour - 9.5f) < 0.001f && pinned.Date == new DateTime(2026, 11, 24) && !real.Pinned && Math.Abs(real.Hour - 15.5f) < 0.001f && real.Date == new DateTime(2026, 1, 2),
            "the page's hour and day are the look's own pins (--look-clock, --look-date: the preview clock), else the machine's local time");
        Check(TitleSky.DayPart(9.5f) == "morning" && TitleSky.DayPart(6f) == "dawn" && TitleSky.DayPart(12f) == "noon" && TitleSky.DayPart(15f) == "afternoon" && TitleSky.DayPart(18f) == "dusk" &&
              TitleSky.DayPart(20f) == "evening" && TitleSky.DayPart(23f) == "night" && TitleSky.DayPart(2f) == "night" && TitleSky.Clock(9.5f) == "9:30" && TitleSky.Clock(0.08f) == "0:04" &&
              TitleSky.DateWords(new DateTime(2026, 11, 24)) == "Tuesday 24 November",
            "the part of the day, the clock and the date are said plainly");
        var full = TitleSky.MoonPhase(new DateTime(2026, 11, 24), 15f);
        var fresh = TitleSky.MoonPhase(new DateTime(2026, 11, 9), 7f);
        Check(TitleSky.MoonLitShare(full) > 0.9f && TitleSky.MoonName(full) == "full moon" && TitleSky.MoonLitShare(fresh) < 0.1f && TitleSky.MoonName(fresh) == "new moon",
            $"the moon is right on two known days (full on 24 November 2026: {TitleSky.MoonLitShare(full):0.00} lit; new on 9 November: {TitleSky.MoonLitShare(fresh):0.00} lit)");
        static float AreaShare(Vector2[] shape, float radius) => MathF.Abs(LandPicture.Area(shape)) / (MathF.PI * radius * radius);
        var shapesTrue = new[] { 0.1f, 0.25f, 0.4f, 0.5f, 0.6f, 0.75f, 0.9f }.All(p => TitleSky.MoonLit(p, 10f, 64) is { Length: > 0 } shape && Math.Abs(AreaShare(shape, 10f) - TitleSky.MoonLitShare(p)) < 0.02f);
        var grows = TitleSky.MoonLit(0.2f, 10f).All(p => p.X >= -0.001f);
        var shrinks = TitleSky.MoonLit(0.8f, 10f).All(p => p.X <= 0.001f);
        Check(shapesTrue && grows && shrinks && TitleSky.MoonLit(0f, 10f).Length == 0 && TitleSky.MoonName(0.25f) == "half moon growing" && TitleSky.MoonName(0.75f) == "half moon shrinking",
            "the moon's lit part is drawn with the area its phase has, on the right while it grows and the left while it shrinks, and none at the new moon");
        var darkest = TitleSky.Wash(3f);
        var clear = TitleSky.Wash(12f);
        var dusk = TitleSky.Wash(18f);
        Check(darkest.A > clear.A + 0.1f && dusk.R > dusk.B && darkest.B > darkest.R && TitleSky.SkyColour(3f).V < TitleSky.SkyColour(12f).V && clear.A <= 0.06f && darkest.A <= 0.25f,
            "the hour's wash is thin, blue at night, amber at dusk and nearly clear by day; the sky window is darker at night");
        Check(TitleSky.Plain("That island is not here.") == "That land is not here." && TitleSky.Plain("Islands visited: 2") == "Lands visited: 2" && TitleSky.Plain("Island of Pip") == "Land of Pip" &&
              TitleSky.Plain("ISLAND") == "Land" && TitleSky.Plain("Mira on the Garage") == "Mira on the Garage",
            "\"island\" is the files' word: on the page it is \"land\", whatever the letters");
    }

    // ---- the page fits, Continue is whole, no "island", the land is on the left ----

    private async Task TestTitleFits()
    {
        var layout = SceneLayout("title-fits");
        var rooms = Folder("title-rooms");
        var longLand = new string('W', 40);
        Write(Path.Combine(rooms, "long_land", "room.json"), Manifest("long_land", longLand, tutorial: false, shape: true));
        Write(Path.Combine(rooms, "pip", "room.json"), Manifest("island_pip", "Island of Pip", tutorial: false, shape: true));
        var folder = ProjectSettings.LocalizePath(rooms);
        var islands = IslandCatalog.Scan(new[] { folder, "res://rooms" });
        var big = islands.First(i => i.RoomId == "long_land");
        var mira = TeamMaker.Create(layout, _rules, new string('M', 60), "", "Pip", "#a18cc3", "", Now);
        WriteGame(layout, mira.TeamId, "challenge", big, "brave");

        var sizes = new[] { new Vector2I(1280, 720), new Vector2I(1152, 648), new Vector2I(1920, 1080) };
        var jets = TeamMaker.Create(layout, _rules, "Jets", "", "Wisp", "#65b9b0", "", Now);
        var small = islands.First(i => i.RoomId == "island_pip");
        WriteGame(layout, jets.TeamId, "challenge", small, "brave");
        foreach (var size in sizes)
        {
            // The two big windows get the longest names a team and a land can have, and a long notice; the small one, a plain game.
            var typical = size.Y == 648;
            var (team, place) = typical ? (jets, small) : (mira, big);
            var words = TitleSky.Plain($"{team.DisplayName} on {place.LandName}, Challenge (Brave)");
            var model = new MenuModel(layout, _rules, islands);
            model.SelectTeam(team.TeamId);
            model.SelectIsland(place.RoomId);
            model.ChooseMode("challenge", "brave");
            if (!typical) model.SetNotice("That game is still open, so it was not touched. Close it and try again, please, then come back.");
            var (view, page) = await TitleIn(size, model, Array.Empty<string>());
            var line = page.ContinueLine;
            var inside = page.ContinueButton.GetGlobalRect();
            var font = line.GetThemeFont("font");
            var wide = page.ContinueButton.Size.X - 40;
            Check(Shown(page.ContinueButton) && line.Text == words && line.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming && !line.ClipText && !line.Text.Contains('…') && !line.Text.Contains("...") &&
                  line.GetLineCount() * line.GetLineHeight() <= line.Size.Y + 1 && page.ContinueWord.Text == "Continue" && inside.Encloses(line.GetGlobalRect()) && inside.Encloses(page.ContinueWord.GetGlobalRect()) &&
                  page.ContinueWord.GetGlobalRect().End.Y <= line.GetGlobalRect().Position.Y + 3 && font.GetStringSize(words, HorizontalAlignment.Left, wide, line.GetThemeFontSize("font_size")).Y <= line.Size.Y + 1,
                $"{size.X}x{size.Y}: Continue's two lines are whole for the longest names ({line.GetLineCount()} lines of {line.GetThemeFontSize("font_size")} px in {line.Size}): \"{line.Text}\"");
            var problem = TitleProblem(view, page);
            Check(problem == null && page.ContinueButton.Size.X >= TitleScreen.ButtonWidth && page.ContinueButton.Size.Y >= TitleScreen.ButtonHeight && page.NewGameButton.Size.Y >= TitleScreen.QuietButtonHeight &&
                  page.ContinueLine.GetThemeFontSize("font_size") >= 18 && page.HintLabel.GetThemeFontSize("font_size") >= 18,
                $"{size.X}x{size.Y}: the title page fits (a long notice, the longest names, buttons {page.ContinueButton.Size} and {page.NewGameButton.Size}; text from 18 px)" + (problem != null ? ": " + problem : ""));
            // The question up: the two buttons, the question in a larger size.
            page.ConfirmGuardMs = 0;
            press(page.NewGameButton);
            await Frames(2);
            problem = TitleProblem(view, page);
            Check(model.Asking && Shown(page.GoBackButton) && !Shown(page.ContinueButton) && problem == null && page.HintLabel.GetThemeFontSize("font_size") >= 20 && !Shown(page.TitleLabel.GetParent().GetNode<Label>("Tagline")),
                $"{size.X}x{size.Y}: with New game's question up the page still fits" + (problem != null ? ": " + problem : ""));
            view.QueueFree();
            await Frames(2);
        }
        static void press(BaseButton button) => button.EmitSignal(BaseButton.SignalName.Pressed);

        // The word "island" is nowhere, whatever the page is saying: no team, a game, a question, a rebuilt room, a refusal, a land called "Island of Pip".
        var states = new List<(string Label, Action<MenuModel> Arrange)>
        {
            ("nothing chosen", m => { }),
            ("a team with a game", m => { m.SelectTeam(mira.TeamId); m.SelectIsland("long_land"); m.ChooseMode("challenge", "brave"); }),
            ("a land called Island of Pip", m => { m.SelectTeam(mira.TeamId); m.SelectIsland("island_pip"); m.ChooseMode("creative"); }),
            ("a refusal that says island", m => { m.SelectTeam(mira.TeamId); m.SetNotice(MenuModel.IslandGoneWords); }),
        };
        foreach (var (label, arrange) in states)
        {
            var model = new MenuModel(layout, _rules, islands);
            arrange(model);
            var (view, page) = await TitleIn(new Vector2I(1280, 720), model, Array.Empty<string>());
            Check(!HasIsland(page) && page.LandName.Text.Length > 0, $"the word island is on nothing the page shows or says ({label}): " + string.Join(" | ", WordsOn(page).Take(14)));
            view.QueueFree();
            await Frames(2);
        }
        var asking = new MenuModel(layout, _rules, islands);
        asking.SelectTeam(mira.TeamId); asking.SelectIsland("long_land"); asking.ChooseMode("challenge", "brave");
        var (askView, askPage) = await TitleIn(new Vector2I(1280, 720), asking, Array.Empty<string>());
        askPage.ConfirmGuardMs = 0;
        press(askPage.NewGameButton);
        await Frames(2);
        Check(asking.Asking && !HasIsland(askPage) && askPage.HintLabel.Text.StartsWith("New game? Your Challenge game", StringComparison.Ordinal), "nor while New game's question is up: " + askPage.HintLabel.Text);
        askView.QueueFree();
        var pip = new MenuModel(layout, _rules, islands);
        pip.SelectTeam(mira.TeamId); pip.SelectIsland("island_pip"); pip.ChooseMode("creative");
        var (pipView, pipPage) = await TitleIn(new Vector2I(1280, 720), pip, Array.Empty<string>());
        Check(pipPage.LandName.Text == "Land of Pip", "a land the family called \"Island of Pip\" is \"" + pipPage.LandName.Text + "\" on the page");
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
        var (morningView, morning) = await TitleIn(new Vector2I(1280, 720), Fresh(), new[] { "--look-clock=09:30", "--look-date=2026-11-24" }, () => new DateTime(2026, 1, 1, 3, 0, 0));
        Check(morning.DateLine.Text == "Tuesday 24 November" && morning.HourLine.Text.StartsWith("Morning, 9:30.", StringComparison.Ordinal) && morning.Dial.Body == "sun" && morning.Wash.Color.A < 0.08f && morning.Sky.Pinned,
            $"a pinned clock moves the page: \"{morning.DateLine.Text}\", \"{morning.HourLine.Text}\", the {morning.Dial.Body} in the window, a wash of {morning.Wash.Color.A:0.00}");
        morningView.QueueFree();
        var (nightView, night) = await TitleIn(new Vector2I(1280, 720), Fresh(), new[] { "--look-clock=22:00", "--look-date=2026-11-24" });
        Check(night.HourLine.Text == "Night, 22:00. The full moon." && night.Dial.Body == "moon" && night.Wash.Color.A > 0.15f && night.Wash.Color.B > night.Wash.Color.R && TitleProblem(nightView, night) == null,
            $"at night the moon is in the window with its phase, and the wash is a thin blue: \"{night.HourLine.Text}\"");
        night.HourWash = false;
        night.UpdateSky();
        Check(night.Wash.Color.A == 0f, "the wash can be switched off");
        nightView.QueueFree();
        var (realView, real) = await TitleIn(new Vector2I(1280, 720), Fresh(), Array.Empty<string>(), () => new DateTime(2026, 11, 24, 14, 5, 0));
        Check(real.HourLine.Text.StartsWith("Afternoon, 14:05.", StringComparison.Ordinal) && real.DateLine.Text == "Tuesday 24 November" && !real.Sky.Pinned, "with no pin it is the machine's own time: " + real.HourLine.Text);
        realView.QueueFree();
        await Frames(2);
    }

    // ---- New game and Continue from the page, through MenuBoot ----

    private async Task<TitleScreen> OpenTitleBoot(params string[] arguments)
    {
        MainMenu.LaunchArguments = () => arguments;
        _boot = new MenuBoot { AutoOpen = false };
        AddChild(_boot);
        var page = _boot.OpenTitle(arguments);
        await Frames(3);
        return page;
    }

    private async Task TestTitleStart()
    {
        // MenuBoot: the title page first, the Choose page where --choose says so.
        var empty = SceneLayout("title-empty");
        _boot = new MenuBoot { AutoOpen = false };
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

        // New game over the game: asks once, "Go back" has the focus; a double click is not the answer; Esc and "Go back" take it down.
        var third = await OpenTitleBoot();
        var before = Hashes(folder);
        third.ConfirmGuardMs = 600_000;
        await Click(third.NewGameButton);
        var savedOn = MenuModel.Day(NewestWrite(layout, "jets", "challenge"));
        Check(third.Model.Asking && Shown(third.GoBackButton) && third.GoBackButton.HasFocus() && !Shown(third.ContinueButton) && third.NewGameButton.Text == "Yes, new game" &&
              third.HintLabel.Text == $"New game? Your Challenge game from {savedOn} is put away, not deleted." && third.HintLabel.GetThemeFontSize("font_size") >= 18 && _boot!.World == null &&
              Same(before, Hashes(folder)) && NothingPutAway(layout),
            "New game over a game asks once: the question stands by the buttons, \"Go back\" has the focus, and nothing has moved: " + third.HintLabel.Text);
        await Click(third.NewGameButton);
        Check(third.Model.Asking && _boot.World == null && Same(before, Hashes(folder)) && NothingPutAway(layout), "a second click straight after the first is not the answer");
        third._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Frames(2);
        Check(!third.Model.Asking && Shown(third.ContinueButton) && third.ContinueButton.HasFocus() && !Shown(third.GoBackButton) && Same(before, Hashes(folder)), "Esc goes back: the question is gone and Continue has the focus");
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

        // "Bring my game here" where the game is on another land; Choose and Esc go between the two pages over one model.
        var scene = await OpenTitleBoot();
        var model = scene.Model;
        WriteGame(layout, "jets", "challenge", model.Island, "standard");
        model.Reload();
        model.SelectIsland("plain_isle");
        scene.Refresh();
        Check(Shown(scene.BringButton) && scene.BringButton.Text == "Bring my game here" && !Shown(scene.ContinueButton) && Shown(scene.NewGameButton) && !HasIsland(scene) && scene.NewGameButton.Size.Y >= TitleScreen.QuietButtonHeight,
            "where this mode's game is on another land: \"Bring my game here\" and New game, no Continue");
        await Click(scene.ChooseButton);
        var choose = _boot!.Menu;
        Check(choose != null && _boot.Title == null && ReferenceEquals(choose.Model, model) && choose.Model.RoomId == "plain_isle", "\"Choose\" opens the Choose page over the same choices");
        _boot._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Frames(3);
        Check(_boot.Title != null && _boot.Menu == null && ReferenceEquals(_boot.Title.Model, model), "Esc on the Choose page comes back to the title page");
        await CloseGame();
        MainMenu.LaunchArguments = null;
    }
}
