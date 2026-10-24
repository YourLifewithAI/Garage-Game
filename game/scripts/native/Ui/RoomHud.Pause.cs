using System;
using System.Collections.Generic;
using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// Esc is the menu (MECHANICS.md decisions 81 and 97; the second recorded play: "The esc key is often an in-game menu key where people
/// can choose to save, load, alter settings, or exit to the main menu"). While the player is in the middle of something (a panel open, the
/// wheel, a chosen bubble, key 6 held for a Fireline, a look drag, a spot being chosen for the Magic panel, the Gubble's eyes, the journal,
/// the camp's screen), the first Esc lets that go, as Esc always did; with nothing going on, one Esc opens this menu. It pauses the game
/// (the tree: the bodies, the host, the nature step, the Gubble's link all wait; the HUD and the playtest record run) and offers:
/// <list type="bullet">
/// <item><b>Resume</b> (or Esc again).</item>
/// <item><b>Save</b>: the island is saved as it is played; this writes now what waits for a throttle (CommandHost.SaveNow).</item>
/// <item><b>Load</b>: the team's other saves on this machine (SaveList, from the menu's own readers), each opening the game again there
/// (MenuBoot.OpenSave, as "Back to the menu" restarts it); and the main menu for the rest.</item>
/// <item><b>Settings</b>: M7's settings panel itself, over the paused game; closing it comes back here.</item>
/// <item><b>Exit to the main menu</b>: M7's "Back to the menu" (MenuBoot.BackToMenu).</item>
/// </list>
/// The mouse: the cursor is never held by the camera except during a look drag (the button held), and the drag ends when the button is let
/// go, when the window loses focus, and at the first Esc; the menu opens with the cursor shown, so the way out is always under the mouse.
/// </summary>
public partial class RoomHud
{
    public const string PauseHeading = "Paused";
    public const string LoadHeading = "Load a save";
    public const string LoadWords = "The game saves as you play. Loading another save starts the game again there.";
    public const string NoTeamWords = "Saves belong to a team: choose one in the main menu.";
    public const string NoOtherSavesWords = "There are no other saves for this team on this computer yet.";
    public const string SaveAlsoWords = "The game also saves as you play.";

    private CanvasLayer _pauseLayer = null!;
    private ColorRect _pauseVeil = null!;
    private PanelContainer _pauseMenu = null!;
    private VBoxContainer _pauseMain = null!, _loadPage = null!, _loadList = null!;
    private Label _pausePlace = null!, _pauseWords = null!;
    private Button _resume = null!;
    private bool _pauseOpen;
    private bool _pauseOwnsTree;
    private bool _settingsFromPause;
    private IReadOnlyList<SaveListEntry> _loadEntries = Array.Empty<SaveListEntry>();

    /// <summary>Whether the Esc menu is open (its own page, the Load page, or the settings opened from it).</summary>
    public bool PauseMenuOpen => _pauseOpen;
    /// <summary>The Esc menu's page now: "main", "load" or "settings" ("" when it is closed).</summary>
    public string PausePage => !_pauseOpen ? "" : _settingsFromPause ? "settings" : _loadPage.Visible ? "load" : "main";
    /// <summary>The menu's last words (what Save said, why Load or Exit could not start).</summary>
    public string PauseWords => _pauseWords.Text;
    public PanelContainer PauseMenu => _pauseMenu;
    /// <summary>The saves the Load page lists now (tests).</summary>
    public IReadOnlyList<SaveListEntry> LoadEntries => _loadEntries;
    /// <summary>Test seam: what Load does with a save (null: start the game again there, MenuBoot.OpenSave).</summary>
    internal Func<MenuLaunch, bool>? OpenSaveForTests { get; set; }
    /// <summary>Test seam: the islands and the team folder the Load page reads (null: this machine's, as the menu reads them).</summary>
    internal (Saves.TeamLayout Layout, IReadOnlyList<IslandEntry> Islands, string Team)? SavesForTests { get; set; }
    /// <summary>Test seam: the clock the Load page says "saved 5 minutes ago" by.</summary>
    internal Func<DateTime> PauseClockForTests { get; set; } = () => DateTime.UtcNow;

    /// <summary>The menu, its own layer above the HUD and the host's approval card, built hidden.</summary>
    private void BuildPauseMenu()
    {
        _pauseLayer = new CanvasLayer { Name = "PauseLayer", Layer = Layer + 50 };
        AddChild(_pauseLayer);
        _pauseVeil = new ColorRect { Name = "PauseVeil", Color = new Color(0.02f, 0.05f, 0.05f, 0.62f), Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _pauseLayer.AddChild(_pauseVeil);
        _pauseVeil.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _pauseMenu = new PanelContainer { Name = "PauseMenu", Visible = false, CustomMinimumSize = new Vector2(440, 0) };
        _pauseMenu.AddThemeStyleboxOverride("panel", MenuStyle.Box(MenuStyle.Panel, MenuStyle.Warm, 2, 16, 22));
        _pauseLayer.AddChild(_pauseMenu);
        var column = new VBoxContainer { Name = "PauseColumn" };
        column.AddThemeConstantOverride("separation", 10);
        _pauseMenu.AddChild(column);
        column.AddChild(MenuStyle.Unclipped(MenuStyle.Text(PauseHeading, 30, MenuStyle.Ink, "PauseHeading")));
        _pausePlace = MenuStyle.Unclipped(MenuStyle.Text("", 15, MenuStyle.Muted, "PausePlace"));
        column.AddChild(_pausePlace);
        _pauseMain = new VBoxContainer { Name = "PauseMain" };
        _pauseMain.AddThemeConstantOverride("separation", 8);
        column.AddChild(_pauseMain);
        _resume = PauseButton(_pauseMain, "PauseResume", "", ClosePauseMenu);
        _keyTexts.Add((words => _resume.Text = words, () => $"Resume ({K(Act.MouseRelease)})"));
        PauseButton(_pauseMain, "PauseSave", "Save", PauseSave);
        PauseButton(_pauseMain, "PauseLoad", "Load", ShowLoadPage);
        PauseButton(_pauseMain, "PauseSettings", "Settings", PauseSettings);
        PauseButton(_pauseMain, "PauseExit", "Exit to the main menu", PauseExitToMenu);
        _loadPage = new VBoxContainer { Name = "PauseLoadPage", Visible = false };
        _loadPage.AddThemeConstantOverride("separation", 8);
        column.AddChild(_loadPage);
        _loadPage.AddChild(MenuStyle.Unclipped(MenuStyle.Text(LoadHeading, 22)));
        var about = MenuStyle.Unclipped(MenuStyle.Text(LoadWords, 14, MenuStyle.Muted));
        about.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        about.CustomMinimumSize = new Vector2(400, 0);
        _loadPage.AddChild(about);
        _loadList = new VBoxContainer { Name = "PauseLoadList" };
        _loadList.AddThemeConstantOverride("separation", 6);
        _loadPage.AddChild(_loadList);
        PauseButton(_loadPage, "PauseLoadMenu", "Choose in the main menu", PauseExitToMenu);
        PauseButton(_loadPage, "PauseLoadBack", "Back", ShowPauseMain);
        _pauseWords = MenuStyle.Unclipped(MenuStyle.Text("", 15, MenuStyle.Warm, "PauseWords"));
        _pauseWords.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _pauseWords.CustomMinimumSize = new Vector2(400, 0);
        column.AddChild(_pauseWords);
    }

    private static Button PauseButton(Node parent, string name, string text, Action pressed)
    {
        var button = new Button { Name = name, Text = text, CustomMinimumSize = new Vector2(0, 44), FocusMode = Control.FocusModeEnum.All };
        MenuStyle.AsCard(button);
        button.Pressed += pressed;
        parent.AddChild(button);
        return button;
    }

    /// <summary>
    /// Esc with nothing going on: the menu opens and the game pauses. The wheel, a drag and the panels are let go first (the first Esc's
    /// work; this makes sure), and the cursor is shown.
    /// </summary>
    public void OpenPauseMenu()
    {
        if (_pauseOpen || IntroPlaying) return;
        CancelAsk();
        EndDrag();
        CloseJournalNow();
        if (SettingsOpen) ToggleSettings();
        if (MagicPanelOpen) SetMagicPanelOpen(false);
        _pauseOpen = true;
        if (IsInsideTree() && !GetTree().Paused)
        {
            GetTree().Paused = true;
            _pauseOwnsTree = true;
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _pauseWords.Text = "";
        _pausePlace.Text = PausePlaceWords();
        ShowPauseMain();
        // The record sees the menu as the screen shows it (the status cue): "Paused" while it is open, cleared when it closes.
        NoticeShown?.Invoke(new ScreenNotice("status", PauseHeading));
    }

    /// <summary>Resume: the menu goes and the game runs on.</summary>
    public void ClosePauseMenu()
    {
        if (!_pauseOpen) return;
        if (_settingsFromPause && SettingsOpen) { _settingsFromPause = false; ToggleSettings(); }
        _settingsFromPause = false;
        _pauseOpen = false;
        _pauseVeil.Visible = _pauseMenu.Visible = false;
        if (_pauseOwnsTree && IsInsideTree()) GetTree().Paused = false;
        _pauseOwnsTree = false;
        NoticeShown?.Invoke(new ScreenNotice("status", ""));
    }

    private void ShowPauseMain()
    {
        _settingsFromPause = false;
        _pauseVeil.Visible = _pauseMenu.Visible = true;
        _pauseMain.Visible = true;
        _loadPage.Visible = false;
        PlacePauseMenu();
        _resume.GrabFocus();
    }

    /// <summary>The panel in the middle of the screen, whatever its size.</summary>
    private void PlacePauseMenu()
    {
        var screen = GetViewport().GetVisibleRect().Size;
        _pauseMenu.ResetSize();
        var size = _pauseMenu.GetCombinedMinimumSize();
        _pauseMenu.Position = ((screen - size) * 0.5f).Max(Vector2.Zero);
    }

    /// <summary>The line under the heading: the island and the mode, as the host has them.</summary>
    private string PausePlaceWords()
    {
        if (Host is not { } host) return RoomTitle;
        var island = host.Room?.DisplayName is { Length: > 0 } name ? name : RoomTitle;
        var mode = host.IslandHeld ? Ui.IslandPaused.Name : MenuModel.ModeWords(host.Mode) + (host.Mode == "challenge" ? $" ({MenuModel.DifficultyWords(host.Difficulty)})" : "");
        return $"{island} · {mode}";
    }

    /// <summary>Save: what waits for a throttle is written now (CommandHost.SaveNow); the menu says what came of it.</summary>
    public void PauseSave()
    {
        if (Host is not { } host) { _pauseWords.Text = "The command host is not attached; there is nothing to save."; return; }
        var outcome = host.SaveNow();
        _pauseWords.Text = outcome.Saved ? $"{outcome.Words} {SaveAlsoWords}" : outcome.Words;
        NoticeShown?.Invoke(new ScreenNotice("status", _pauseWords.Text));
    }

    /// <summary>Load: the team's saves, from the menu's own readers; the one being played is marked and not offered again.</summary>
    public void ShowLoadPage()
    {
        _pauseMain.Visible = false;
        _loadPage.Visible = true;
        foreach (var child in _loadList.GetChildren()) { _loadList.RemoveChild(child); child.QueueFree(); }
        var host = Host;
        var (layout, islands, team) = SavesForTests ?? (MenuRoots.Layout(), IslandCatalog.Scan(), host?.SaveTeamIdentity?.TeamId ?? "");
        _loadEntries = team.Length == 0 ? Array.Empty<SaveListEntry>() : SaveList.For(layout, team, islands, host?.Room?.RoomId, host?.SaveTeamIdentity?.Mode ?? host?.Mode);
        if (team.Length == 0) _loadList.AddChild(Wrapped(NoTeamWords));
        else if (_loadEntries.Count == 0 || (_loadEntries.Count == 1 && _loadEntries[0].Playing)) _loadList.AddChild(Wrapped(NoOtherSavesWords));
        var now = PauseClockForTests();
        for (var i = 0; i < _loadEntries.Count; i++)
        {
            var entry = _loadEntries[i];
            var row = new HBoxContainer { Name = $"Save{i}" };
            row.AddThemeConstantOverride("separation", 10);
            var words = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            words.AddChild(MenuStyle.Text(SaveList.Title(entry), 16));
            words.AddChild(MenuStyle.Text(SaveList.When(entry, now), 13, MenuStyle.Muted));
            row.AddChild(words);
            if (!entry.Playing)
            {
                var launch = entry.Launch(team);
                var load = PauseButton(row, $"LoadSave{i}", "Load", () => LoadSave(launch));
                load.CustomMinimumSize = new Vector2(96, 40);
            }
            _loadList.AddChild(row);
        }
        PlacePauseMenu();
        (_loadPage.FindChild("LoadSave*", true, false) as Button ?? _loadPage.GetNode<Button>("PauseLoadBack")).GrabFocus();
    }

    private static Label Wrapped(string text)
    {
        var label = MenuStyle.Unclipped(MenuStyle.Text(text, 15, MenuStyle.Muted));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(400, 0);
        return label;
    }

    /// <summary>A save chosen: what waits is written, then the game starts again in that save (or says why it could not).</summary>
    private void LoadSave(MenuLaunch launch)
    {
        Host?.SaveNow();
        var started = OpenSaveForTests?.Invoke(launch) ?? MenuBoot.OpenSave(GetTree(), launch);
        if (!started) _pauseWords.Text = "That save could not be opened from here. Choose it in the main menu.";
    }

    /// <summary>Settings: M7's own panel, over the paused game; Done, its key or Esc comes back to the menu.</summary>
    private void PauseSettings()
    {
        _pauseVeil.Visible = _pauseMenu.Visible = false;
        if (!SettingsOpen) ToggleSettings();
        _settingsFromPause = true;
    }

    /// <summary>The settings panel closed while the menu holds the game: the menu again.</summary>
    private void SettingsClosedForPause()
    {
        if (!_settingsFromPause || !_pauseOpen) return;
        ShowPauseMain();
    }

    /// <summary>Exit to the main menu: what waits is written, then M7's "Back to the menu" (the game starts again at the menu).</summary>
    private void PauseExitToMenu()
    {
        Host?.SaveNow();
        var started = BackToMenuForTests?.Invoke() ?? MenuBoot.BackToMenu(GetTree());
        if (!started) _pauseWords.Text = "The menu could not be opened from here. Close the game and open it again.";
    }

    /// <summary>
    /// The keys while the menu is open: Esc steps back (settings or the Load page to the menu, the menu to the game), the settings key
    /// closes the settings it opened; every other key waits with the game. True: the menu took the press.
    /// </summary>
    private bool HandlePauseMenuKeys(InputEvent press, bool fresh)
    {
        if (!_pauseOpen) return false;
        if (!fresh) return true;
        if (_settingsFromPause)
        {
            if (PlayerControls.Hit(press, Act.MouseRelease) || PlayerControls.Hit(press, Act.HudCustomize)) ToggleSettings();
            return true;
        }
        if (PlayerControls.Hit(press, Act.MouseRelease))
        {
            if (_loadPage.Visible) ShowPauseMain();
            else ClosePauseMenu();
        }
        return true;
    }

    /// <summary>The HUD leaves the tree: a menu that paused the game lets it go.</summary>
    private void ReleasePause()
    {
        if (_pauseOwnsTree && IsInsideTree()) GetTree().Paused = false;
        _pauseOwnsTree = false;
        _pauseOpen = false;
    }

    /// <summary>
    /// The first Esc (decision 97): lets go of whatever the player is in the middle of, as Esc always did: the settings and the Magic
    /// panel, a look drag, key 6 held for a Fireline, the bubble the smart ask chose. True when something was let go; false when nothing
    /// was going on, and the Esc is the menu's. (The wheel, a spot being chosen, the Gubble's eyes and the journal take their Esc earlier.)
    /// </summary>
    private bool CancelWhatIsGoingOn()
    {
        var going = false;
        if (SettingsOpen) { ToggleSettings(); going = true; }
        if (MagicPanelOpen) { SetMagicPanelOpen(false); going = true; }
        if (_drag.Pressed) { EndDrag(); going = true; }
        if (_fireKeyStart != null) { _fireKeyStart = null; going = true; }
        if (SelectedBubble != null) { ClearBubbleSelection(); going = true; }
        return going;
    }
}
