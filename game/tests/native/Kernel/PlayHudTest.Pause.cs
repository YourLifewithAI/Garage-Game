using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Esc is the menu (the second playtest round's P2, decisions 81 and 97; RoomHud.Pause.cs): with nothing going on one Esc opens it and
/// pauses the game; while something is going on (a panel, the wheel, a look drag) the first Esc lets that go and the next opens it; Esc
/// again resumes. Its pages: Save (CommandHost.SaveNow), Load (the team's saves, each opening the game again there), M7's settings panel
/// itself (back to the menu when it closes) and Exit to the main menu (M7's Back to the menu). Every other key waits under it.
/// </summary>
public partial class PlayHudTest
{
    private async Task TestPauseMenu()
    {
        var host = CommandHost.Of(_world)!;
        var shown = new List<ScreenNotice>();
        void Watch(ScreenNotice notice) => shown.Add(notice);
        _hud.NoticeShown += Watch;
        try
        {
            await PauseOpensAndPauses(host, shown);
            await PauseCancelsFirst();
            await PauseSaveAndLoad(host);
            await PauseSettingsAndExit();
        }
        finally
        {
            _hud.NoticeShown -= Watch;
            if (_hud.PauseMenuOpen) _hud.ClosePauseMenu();
            _hud.SavesForTests = null;
            _hud.OpenSaveForTests = null;
            _hud.BackToMenuForTests = null;
            _hud.DragHeldForTests = null;
        }
    }

    /// <summary>One Esc with nothing going on: the menu, the game paused under it (the weather clock stops), keys waiting; Esc again resumes.</summary>
    private async Task PauseOpensAndPauses(CommandHost host, List<ScreenNotice> shown)
    {
        await Frames(2);
        Check(!_hud.PauseMenuOpen && !GetTree().Paused && !_hud.SettingsOpen && !_hud.MagicPanelOpen && !_hud.JournalOpen && _hud.SelectedBubble == null,
            "(nothing is going on: no panel, no wheel, no chosen bubble)");
        var clock = host.Nature.WeatherS;
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(30);
        var menu = _hud.PauseMenu;
        var screen = _hud.GetViewport().GetVisibleRect().Size;
        var rect = menu.GetGlobalRect();
        Check(_hud.PauseMenuOpen && _hud.PausePage == "main" && GetTree().Paused && menu.Visible && _hud.GetNode<ColorRect>("PauseLayer/PauseVeil").Visible,
            "with nothing going on, one Esc opens the menu and pauses the game");
        Check(host.Nature.WeatherS == clock, $"the game waits under it: the island's weather clock stood still for 30 frames ({clock:0.###} s)");
        Check(Mathf.Abs(rect.Position.X - (screen.X - rect.End.X)) <= 2 && Mathf.Abs(rect.Position.Y - (screen.Y - rect.End.Y)) <= 2 && rect.Size.X >= 400,
            $"the menu sits in the middle of the screen: {rect.Position.X:0}..{rect.End.X:0} x {rect.Position.Y:0}..{rect.End.Y:0} of {screen.X:0} x {screen.Y:0}");
        var names = new[] { "PauseResume", "PauseSave", "PauseLoad", "PauseSettings", "PauseExit" };
        var buttons = names.Select(n => menu.FindChild(n, true, false) as Button).ToArray();
        Check(buttons.All(b => b is { Visible: true }) && buttons[0]!.Text == "Resume (Esc)" && buttons[4]!.Text == "Exit to the main menu" &&
              buttons[0]!.HasFocus() && Input.MouseMode == Input.MouseModeEnum.Visible,
            "it offers Resume, Save, Load, Settings and Exit to the main menu, Resume holding the keyboard focus, the cursor shown");
        Check(_hud.GetNode<CanvasLayer>("PauseLayer").Layer > 6, "on a layer over the HUD and the host's approval card (layer 6)");
        Check(shown.Any(n => n.Channel == "status" && n.Text == RoomHud.PauseHeading), "the record sees \"Paused\" on the status cue");
        // Every other key waits with the game: H, J, M and C do nothing under the menu.
        var help = KeyHelpOpen();
        foreach (var key in new[] { Key.H, Key.J, Key.M, Key.C, Key.Key1 }) _hud._UnhandledInput(Press(key));
        await Frames(2);
        Check(KeyHelpOpen() == help && !_hud.JournalOpen && !_hud.MagicPanelOpen && !_hud.SettingsOpen && _hud.PauseMenuOpen, "under the menu the other keys wait (H, J, M, C, 1)");
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(20);
        Check(!_hud.PauseMenuOpen && !GetTree().Paused && !menu.Visible && host.Nature.WeatherS > clock, "Esc again resumes: the menu goes and the game runs on (the clock moves)");
        Check(shown.LastOrDefault(n => n.Channel == "status") is { Text: "" }, "and the status cue is cleared");
        // Resume as a button too.
        _hud.OpenPauseMenu();
        ((Button)menu.FindChild("PauseResume", true, false)!).EmitSignal(Button.SignalName.Pressed);
        Check(!_hud.PauseMenuOpen && !GetTree().Paused, "the Resume button resumes too");
    }

    private bool KeyHelpOpen() => _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").Visible;

    /// <summary>Decision 97: the first Esc cancels what is going on (the Magic panel, the settings, the wheel, a look drag), the next opens the menu.</summary>
    private async Task PauseCancelsFirst()
    {
        _hud._UnhandledInput(Press(Key.M));
        await Frames(2);
        var magic = _hud.MagicPanelOpen;
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(2);
        Check(magic && !_hud.MagicPanelOpen && !_hud.PauseMenuOpen, "with the Magic panel open, the first Esc closes it and no menu opens");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(_hud.PauseMenuOpen, "the next Esc opens the menu");
        _hud.ClosePauseMenu();
        _hud._UnhandledInput(Press(Key.C));
        _hud._UnhandledInput(Press(Key.Escape));
        Check(!_hud.SettingsOpen && !_hud.PauseMenuOpen, "the settings panel likewise: the first Esc closes it");
        // The wheel held open (the game paused by the wheel): the first Esc closes the wheel and the game runs again.
        _hud.AimRayForTests = Down(FloorNearGubble(.3f));
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        var wheel = _hud.Wheel.Open && GetTree().Paused;
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(2);
        Check(wheel && !_hud.Wheel.Held && !GetTree().Paused && !_hud.PauseMenuOpen, "with the wheel open, the first Esc closes it, unpaused, and no menu opens");
        _hud._UnhandledInput(RightButton(false));
        _hud.AimRayForTests = null;
        _hud._UnhandledInput(Press(Key.Escape));
        Check(_hud.PauseMenuOpen && GetTree().Paused, "then Esc opens the menu");
        _hud.ClosePauseMenu();
        // A look drag (the button held, the cursor captured once it moves): the first Esc ends it and frees the cursor.
        _hud.DragHeldForTests = () => true;
        _hud._UnhandledInput(LeftButton(true));
        _hud._UnhandledInput(Motion(new Vector2(40, 0)));
        var dragging = _hud.Drag.Dragging;
        _hud._UnhandledInput(Press(Key.Escape));
        Check(dragging && !_hud.Drag.Pressed && Input.MouseMode == Input.MouseModeEnum.Visible && !_hud.PauseMenuOpen,
            "during a look drag the first Esc ends the drag and gives the cursor back; no menu yet");
        _hud.DragHeldForTests = null;
        _hud._UnhandledInput(Press(Key.Escape));
        Check(_hud.PauseMenuOpen, "the next Esc opens the menu");
        _hud.ClosePauseMenu();
        await Frames(2);
    }

    /// <summary>Save writes what waits (CommandHost.SaveNow); Load lists the team's saves from the menu's own readers and opens one by starting the game there.</summary>
    private async Task PauseSaveAndLoad(CommandHost host)
    {
        _hud.OpenPauseMenu();
        ((Button)_hud.PauseMenu.FindChild("PauseSave", true, false)!).EmitSignal(Button.SignalName.Pressed);
        Check(_hud.PauseWords == $"{CommandHost.SavedNowWords} {RoomHud.SaveAlsoWords}" && host.SaveNow().Saved && _hud.PauseMenuOpen,
            "Save writes now what waits for a throttle and says so; the menu stays: " + _hud.PauseWords);
        // No team (this suite's room booted without one): Load says saves belong to a team.
        ((Button)_hud.PauseMenu.FindChild("PauseLoad", true, false)!).EmitSignal(Button.SignalName.Pressed);
        var page = _hud.PauseMenu.FindChild("PauseLoadPage", true, false) as Control;
        Check(_hud.PausePage == "load" && page is { Visible: true } && _hud.LoadEntries.Count == 0 &&
              page!.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text == RoomHud.NoTeamWords),
            "with no team playing, Load says saves belong to a team and offers the main menu");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(_hud.PausePage == "main", "Esc on the Load page steps back to the menu");
        // A team folder of its own: three saves, one of them the game being played.
        var root = ProjectSettings.GlobalizePath("user://tests/play_hud/pause_saves");
        if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
        var layout = new TeamLayout(root);
        var playing = new IslandEntry(host.Room.RoomId, "The test room", "res://rooms/" + host.Room.RoomId, "0123456789abcdef", false, true, false);
        var garage = new IslandEntry("landscape_garage", "The Garage", "user://rooms/landscape_garage", "fedcba9876543210", false, false, false);
        void WriteSave(IslandEntry island, string mode, string body, DateTime at)
        {
            var path = layout.IslandSave("mira", mode, island.RoomId, island.ManifestPrefix);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, body);
            System.IO.File.SetLastWriteTimeUtc(path, at);
        }
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        WriteSave(playing, host.Mode, "{}", now.AddHours(-3));
        WriteSave(garage, "challenge", "{\"team\":{\"team_id\":\"mira\",\"mode\":\"challenge\",\"difficulty\":\"gentle\"}}", now.AddMinutes(-5));
        WriteSave(garage, "creative", "{}", now.AddDays(-2));
        _hud.SavesForTests = (layout, new[] { playing, garage }, "mira");
        _hud.PauseClockForTests = () => now;
        MenuLaunch? opened = null;
        _hud.OpenSaveForTests = launch => { opened = launch; return true; };
        try
        {
            _hud.ShowLoadPage();
            var entries = _hud.LoadEntries;
            Check(entries.Count == 3 && entries[0].Playing && entries[0].Island.RoomId == host.Room.RoomId && entries[1].Mode == "challenge" && entries[2].Mode == "creative",
                "Load lists the team's saves, the one being played first, then the newest: " + string.Join(", ", entries.Select(SaveList.Title)));
            var rows = Enumerable.Range(0, 3).Select(i => _hud.PauseMenu.FindChild($"Save{i}", true, false) as HBoxContainer).ToArray();
            var texts = rows.Select(r => string.Join(" / ", r!.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text))).ToArray();
            Check(texts[1] == "The Garage · Challenge (Gentle) / saved 5 minutes ago" && texts[2] == "The Garage · Creative / saved 2 days ago" && texts[0].EndsWith("/ playing now", StringComparison.Ordinal),
                "each save reads its island, mode and difficulty, and when it was saved: " + string.Join(" | ", texts));
            Check(rows[0]!.FindChild("LoadSave0", true, false) == null && rows[1]!.FindChild("LoadSave1", true, false) is Button { Visible: true },
                "the game being played is not offered again; the others have a Load button");
            ((Button)rows[1]!.FindChild("LoadSave1", true, false)!).EmitSignal(Button.SignalName.Pressed);
            Check(opened is { TeamId: "mira", Mode: "challenge", Difficulty: "gentle" } launch && launch.Island.Directory == garage.Directory &&
                  launch.Arguments.SequenceEqual(new[] { "--team=mira", "--mode=challenge", "--difficulty=gentle" }),
                "Load opens that save: the team, the island's folder, its mode and the difficulty the save keeps");
            var arguments = MenuBoot.LoadArguments(new[] { "--path", "/game", "--fixed-fps", "60" },
                new[] { "--room=old_room", "--team=old", "--mode=creative", "--fullscreen", "--menu", "--quit-after-ready=5" }, opened!);
            Check(arguments.SequenceEqual(new[] { "--path", "/game", "--fixed-fps", "60", "--", "--fullscreen", "--room=user://rooms/landscape_garage", "--team=mira", "--mode=challenge", "--difficulty=gentle" }),
                "the game starts again straight into it: the engine's arguments, the launch's own (full screen kept), the save's room, team, mode and difficulty: " + string.Join(" ", arguments));
            _hud.OpenSaveForTests = _ => false;
            ((Button)rows[2]!.FindChild("LoadSave2", true, false)!).EmitSignal(Button.SignalName.Pressed);
            Check(_hud.PauseMenuOpen && _hud.PauseWords.Contains("could not be opened", StringComparison.Ordinal), "a save that cannot be opened from here says so and the menu stays");
        }
        finally
        {
            _hud.PauseClockForTests = () => DateTime.UtcNow;
            System.IO.Directory.Delete(root, true);
        }
        _hud.ClosePauseMenu();
        await Frames(2);
    }

    /// <summary>Settings is M7's own panel over the paused game, and closing it comes back to the menu; Exit is M7's Back to the menu.</summary>
    private async Task PauseSettingsAndExit()
    {
        _hud.OpenPauseMenu();
        ((Button)_hud.PauseMenu.FindChild("PauseSettings", true, false)!).EmitSignal(Button.SignalName.Pressed);
        await Frames(2);
        Check(_hud.SettingsOpen && _hud.PausePage == "settings" && !_hud.PauseMenu.Visible && GetTree().Paused && _hud.SettingsPanel.FindChild("GubbleFireSetting", true, false) != null,
            "Settings opens M7's own settings panel (its switches, Your AI, Back to the menu), the game still paused");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(!_hud.SettingsOpen && _hud.PausePage == "main" && _hud.PauseMenu.Visible && GetTree().Paused, "Esc closes the settings and comes back to the menu");
        ((Button)_hud.PauseMenu.FindChild("PauseSettings", true, false)!).EmitSignal(Button.SignalName.Pressed);
        _hud.SettingsPanel.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "Done").EmitSignal(Button.SignalName.Pressed);
        Check(!_hud.SettingsOpen && _hud.PausePage == "main", "and so does its Done button");
        var exits = 0;
        _hud.BackToMenuForTests = () => { exits++; return true; };
        ((Button)_hud.PauseMenu.FindChild("PauseExit", true, false)!).EmitSignal(Button.SignalName.Pressed);
        Check(exits == 1, "Exit to the main menu is M7's Back to the menu (the game starts again at the menu)");
        _hud.BackToMenuForTests = () => false;
        ((Button)_hud.PauseMenu.FindChild("PauseExit", true, false)!).EmitSignal(Button.SignalName.Pressed);
        Check(_hud.PauseMenuOpen && _hud.PauseWords.Contains("could not be opened", StringComparison.Ordinal), "and says so when it cannot");
        _hud.ClosePauseMenu();
        await Frames(2);
        Check(!GetTree().Paused, "(resumed)");
    }
}
