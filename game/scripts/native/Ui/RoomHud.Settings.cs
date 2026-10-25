using Godot;
using System.Text.Json.Nodes;
using GarageGame.Native.Companion;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The small in-play settings panel (docs/game/MECHANICS.md sections 2 and 3, decision 1): the player's switches that left the
/// Customize panel and the Magic page, and the two controls that belong to the pair. The switches belong to this computer, never to a
/// team (the machine's profile, Saves/MachineSettings.cs): "Let my AI see", "Let the Gubble use fire", "Play the intro" and the
/// numbers on the meters; and, the one that belongs to the team (its team file, decision 100), "Let the Gubble use the chest". Under "Your AI": "Change my AI", which says how to hand the Gubble to another AI (and whether one is
/// connected now), and "Reset Trust" beside it, the player's alone: it asks first, then sends the player-only trust.reset through the
/// host, which starts Trust over (the game never resets it, and the companion is refused it). The key is the one the Customize panel had
/// (the input action keeps its name, hud_customize, until project.godot renames it).
/// </summary>
public partial class RoomHud
{
    private PanelContainer _settings = null!;
    private Button _changeAi = null!, _resetTrust = null!, _confirmReset = null!, _keepTrust = null!;
    private HBoxContainer _trustConfirm = null!;
    private Label _aiNote = null!, _trustLine = null!;
    private CheckButton? _chestToggle;
    /// <summary>The fifth switch, "Let the Gubble use the chest" (the team's setting: the host writes it to the team file).</summary>
    public CheckButton? ChestToggle => _chestToggle;

    public const string ChangeAiWords = "To change your AI, close the one that is connected and start the other. It finds the Gubble where it stands, with the same journal, expertise and Trust. The game never learns which AI it is.";
    public const string ResetTrustQuestion = "Start the Gubble's Trust over, from nothing?";

    public bool SettingsOpen => _settings.Visible;
    public PanelContainer SettingsPanel => _settings;
    public CheckButton? FireToggle => _gubbleFire;
    public Button ChangeAiButton => _changeAi;
    public Button ResetTrustButton => _resetTrust;
    public Button ConfirmResetTrustButton => _confirmReset;
    public Button KeepTrustButton => _keepTrust;
    public HBoxContainer TrustConfirm => _trustConfirm;
    public Label AiNote => _aiNote;
    public Label TrustLine => _trustLine;

    private void BuildSettingsPanel(PanelContainer panel)
    {
        _settings = panel;
        var options = new VBoxContainer { Name = "SettingsColumn", CustomMinimumSize = new Vector2(560, 0) };
        panel.AddChild(options);
        options.AddChild(new Label { Text = "SETTINGS" });
        var belongs = new Label { Text = "These belong to this computer, whichever team plays. The chest switch is the exception: it belongs to your team.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        belongs.AddThemeFontSizeOverride("font_size", 14);
        options.AddChild(belongs);
        BuildEyesSetting(options);
        BuildFireSetting(options);
        BuildIntroSetting(options);
        BuildNumbersSetting(options);
        BuildChestSetting(options);

        options.AddChild(new Label { Text = "YOUR AI" });
        var buttons = new HBoxContainer { Name = "AiButtons" };
        options.AddChild(buttons);
        _changeAi = new Button { Name = "ChangeMyAi", Text = "Change my AI", FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38) };
        _changeAi.Pressed += ShowChangeAi;
        buttons.AddChild(_changeAi);
        _resetTrust = new Button { Name = "ResetTrust", Text = "Reset Trust", FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38),
            TooltipText = "Start the Gubble's Trust over. Only you can; the AI cannot, and the game never does." };
        _resetTrust.Pressed += AskResetTrust;
        buttons.AddChild(_resetTrust);
        _trustLine = new Label { Name = "TrustLine", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        options.AddChild(_trustLine);
        _trustConfirm = new HBoxContainer { Name = "TrustConfirm", Visible = false };
        options.AddChild(_trustConfirm);
        var question = new Label { Name = "TrustQuestion", Text = ResetTrustQuestion, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _trustConfirm.AddChild(question);
        _keepTrust = new Button { Name = "KeepTrust", Text = "Keep it", FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38) };
        _keepTrust.Pressed += () => { _trustConfirm.Visible = false; _resetTrust.GrabFocus(); };
        _trustConfirm.AddChild(_keepTrust);
        _confirmReset = new Button { Name = "ConfirmResetTrust", Text = "Yes, reset Trust", FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38) };
        _confirmReset.Pressed += ResetTrustNow;
        _trustConfirm.AddChild(_confirmReset);
        _aiNote = new Label { Name = "AiNote", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        options.AddChild(_aiNote);
        // Going back to the menu is how a mode is changed (decision 3: a save never changes its mode): the game starts again there.
        var leave = new HBoxContainer { Name = "SettingsFoot" };
        options.AddChild(leave);
        _backToMenu = new Button { Name = "BackToMenu", Text = "Back to the menu", FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38),
            TooltipText = "The game starts again at the opening menu, where a team, an island and a mode are chosen. Your island is saved as you play." };
        _backToMenu.Pressed += BackToMenuNow;
        leave.AddChild(_backToMenu);
        AddButton(leave, "Done", ToggleSettings);
    }

    private Button _backToMenu = null!;
    public Button BackToMenuButton => _backToMenu;
    /// <summary>Test seam: what "Back to the menu" does (null: restart the game at the menu, MenuBoot.BackToMenu).</summary>
    internal System.Func<bool>? BackToMenuForTests { get; set; }

    /// <summary>"Back to the menu": this game ends and a new one opens at the menu.</summary>
    public void BackToMenuNow()
    {
        var started = BackToMenuForTests?.Invoke() ?? MenuBoot.BackToMenu(GetTree());
        if (!started) _noticeText = "The menu could not be opened from here. Close the game and open it again.";
    }

    /// <summary>The player's own setting "Let the Gubble use fire" (the profile's, never room state); only this control changes it. It moved here from the Magic page.</summary>
    private void BuildFireSetting(Node options)
    {
        _gubbleFire = new CheckButton
        {
            Name = "GubbleFireSetting", Text = "Let the Gubble use fire",
            ButtonPressed = Host?.GubbleFireAllowed ?? Kernel.CommandHost.LoadGubbleFire(ProfilePath),
            // Decision 82: nothing but burning out or Snuff ends a fire; X stops the Gubble's moves, not its fire.
            TooltipText = "Your AI can light fires through the Gubble. In Challenge and Live its fire can hurt you, and it learns from that. A fire burns until it burns itself out, or until Snuff puts it out: X stops the Gubble, not its fire.",
            FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38),
        };
        _gubbleFire.Toggled += SetGubbleFire;
        options.AddChild(_gubbleFire);
    }

    /// <summary>
    /// The player's own setting "Let the Gubble use the chest" (decision 100; off by default): the team's, kept in its team file by the host
    /// (CommandHost.SetGubbleChest), never a command and never the companion's. Without an inventory on the island there is no chest to let it into.
    /// </summary>
    private void BuildChestSetting(Node options)
    {
        _chestToggle = new CheckButton
        {
            Name = "GubbleChestSetting", Text = "Let the Gubble use the chest",
            ButtonPressed = Host?.GubbleChestAllowed ?? false, Disabled = Host is not { HasInventory: true },
            TooltipText = "Your AI can store and take things in the camp's chest through the Gubble, with the Gubble's own hands. Never yours: not what you hold and not your pockets. It belongs to your team and is kept with it.",
            FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38),
        };
        _chestToggle.Toggled += SetGubbleChest;
        options.AddChild(_chestToggle);
    }

    /// <summary>The player's choice: the host follows it at once and writes it to the team file (this session only if it cannot, or there is no team file).</summary>
    public void SetGubbleChest(bool allowed)
    {
        var saved = Host?.SetGubbleChest(allowed) ?? false;
        if (_chestToggle != null && _chestToggle.ButtonPressed != allowed) _chestToggle.SetPressedNoSignal(allowed);
        _noticeText = (allowed ? "The Gubble may use the chest." : "The Gubble will not use the chest.") + (saved ? "" : " (Not saved: it lasts this session.)");
    }

    /// <summary>The panel opens: Trust in words as the host has it, no question pending, the first switch holds the keyboard focus.</summary>
    private void OnSettingsOpened()
    {
        _chestToggle?.SetPressedNoSignal(Host?.GubbleChestAllowed ?? false);
        if (_chestToggle != null) _chestToggle.Disabled = Host is not { HasInventory: true };
        _trustConfirm.Visible = false;
        _aiNote.Visible = false;
        _trustLine.Text = Host?.TrustWords ?? "";
        _trustLine.Visible = _trustLine.Text.Length > 0;
        _resetTrust.Disabled = Host == null;
        _eyesToggle?.GrabFocus();
    }

    /// <summary>"Change my AI": whether one is connected now, and how to hand the Gubble to another.</summary>
    private void ShowChangeAi()
    {
        var status = GubbleLinkNote.StatusWords(GetParent() is { } world ? CompanionBridge.Of(world) : null);
        _aiNote.Text = (status.Length > 0 ? status + ". " : "There is no AI link in this room. ") + ChangeAiWords;
        _aiNote.Visible = true;
    }

    /// <summary>"Reset Trust": the question first, with "Keep it" holding the focus; nothing is sent until the player says yes.</summary>
    private void AskResetTrust()
    {
        _aiNote.Visible = false;
        _trustConfirm.Visible = true;
        _keepTrust.GrabFocus();
    }

    /// <summary>The confirmed reset: trust.reset as the player, through the host. The answer (Trust in words, or why not) is said in the panel and the notice line.</summary>
    public void ResetTrustNow()
    {
        _trustConfirm.Visible = false;
        if (Host is not { } host)
        {
            _noticeText = "The command host is not attached; Trust cannot be reset.";
            return;
        }
        JsonObject result = host.PlayerResetTrust();
        var done = result["ok"]?.GetValue<bool>() == true;
        _noticeText = done ? "Trust starts again: " + host.TrustWords : result["error"]?["message"]?.GetValue<string>() ?? "Trust could not be reset.";
        _trustLine.Text = done ? host.TrustWords : _noticeText;
        _trustLine.Visible = true;
        _resetTrust.GrabFocus();
    }

    /// <summary>The key (C, the input action hud_customize), the button, Esc and the Esc menu's Settings: the panel opens and closes, and the body waits while it is open.</summary>
    private void ToggleSettings()
    {
        CancelAsk();
        EndDrag();
        CloseJournalNow();
        if (MagicPanelOpen) SetMagicPanelOpen(false);
        _settings.Visible = !_settings.Visible;
        Player.SetInputEnabled(!_settings.Visible);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (_settings.Visible) OnSettingsOpened();
        // Opened from the Esc menu (RoomHud.Pause.cs): closing it goes back there.
        else SettingsClosedForPause();
    }
}
