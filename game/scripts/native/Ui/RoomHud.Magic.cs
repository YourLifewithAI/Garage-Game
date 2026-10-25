using System;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>Local controls for the host's magic commands. Every action is checked again by the host.</summary>
public partial class RoomHud
{
    private PanelContainer _magicPanel = null!;
    private TabContainer _magicTabs = null!;
    private Label _magicTargetText = null!;
    private Label _magicMessage = null!;
    private Label _islandVitalsLine = null!;
    private GubbleMagic.Aim _magicAim;
    private bool _pickingMagicSpot;
    private string? _movingGlow;
    private string _magicMessageText = "Choose a spot, then try your magic there.";
    private double _magicRefreshAge;
    public bool MagicPanelOpen => _magicPanel != null && _magicPanel.Visible;
    public bool ChoosingMagicSpot => _pickingMagicSpot;
    public string MagicMessage => _magicMessageText;
    private bool ReadingPanelOpen => SettingsOpen || JournalOpen || MagicPanelOpen || PocketsPanelOpen || GubbleViewOpen;

    private void BuildMagicPanel(Node actions, Node statusColumn, Theme theme)
    {
        var open = new Button { Name = "MagicPanelButton", Text = "Magic", CustomMinimumSize = new Vector2(0, 26) };
        open.Pressed += ToggleMagicPanel;
        actions.AddChild(open);
        _islandVitalsLine = new Label { Name = "IslandVitalsLine" };
        statusColumn.AddChild(_islandVitalsLine);
        _magicPanel = new PanelContainer { Name = "MagicPanel", Position = new Vector2(18, 150), Theme = theme, Visible = false };
        AddChild(_magicPanel);
        var column = new VBoxContainer();
        _magicPanel.AddChild(column);
        var heading = new HBoxContainer(); column.AddChild(heading);
        heading.AddChild(new Label { Text = "THE GUBBLE'S MAGIC", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        AddButton(heading, "Close", ToggleMagicPanel, 26);
        _magicTargetText = WrapMagicLabel("Choose a spot, or cast a light on the Gubble."); column.AddChild(_magicTargetText);
        var aim = new HBoxContainer(); column.AddChild(aim);
        AddButton(aim, "Choose spot", () => PickMagicSpot(), 26);
        AddButton(aim, "On the Gubble", () => { _magicAim = default; RefreshMagicPanel(); }, 26);
        _magicTabs = new TabContainer { Name = "MagicTabs", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddChild(_magicTabs);
        BuildNaturePage(MagicPage("Island"));
        BuildGlowPage(MagicPage("Glow"));
        BuildWishPage(MagicPage("Wishes"));
        _magicMessage = WrapMagicLabel(_magicMessageText); _magicMessage.Name = "MagicMessage"; column.AddChild(_magicMessage);
        RefreshMagicPanel();
    }

    private VBoxContainer MagicPage(string title)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _magicTabs.AddChild(scroll);
        var page = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(page);
        return page;
    }

    private static Label WrapMagicLabel(string text) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static void MagicHeading(Node parent, string title) => parent.AddChild(new Label { Text = title });

    public void ToggleMagicPanel() => SetMagicPanelOpen(!MagicPanelOpen);

    private void SetMagicPanelOpen(bool open)
    {
        CancelAsk(); EndDrag();
        if (open)
        {
            CloseJournalNow();
            if (SettingsOpen) ToggleSettings();
            if (PocketsPanelOpen) SetPocketsPanelOpen(false);
            _pickingMagicSpot = false;
        }
        _magicPanel.Visible = open;
        Player.SetInputEnabled(!ReadingPanelOpen);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        RefreshMagicPanel();
    }

    private void PickMagicSpot(string? moving = null)
    {
        SetMagicPanelOpen(false);
        _pickingMagicSpot = true; _movingGlow = moving;
        _noticeText = moving == null ? "Click a spot for your magic. Esc cancels." : "Click a new place for this light. Esc cancels.";
    }

    /// <summary>One ordinary world click selects a place; it never casts Fire or moves an entity directly.</summary>
    private bool HandleMagicSpotInput(InputEvent press, bool fresh)
    {
        if (!_pickingMagicSpot || !fresh) return false;
        if (PlayerControls.Hit(press, Act.MouseRelease))
        {
            _pickingMagicSpot = false; _movingGlow = null;
            SetMagicPanelOpen(true);
            _magicMessageText = "Spot selection cancelled.";
            return true;
        }
        if (!PlayerControls.Hit(press, Act.LookDrag)) return false;
        var aim = AimNow();
        if (!aim.Hit || aim.AtGubble)
        {
            _noticeText = "Choose a surface on the island. Esc cancels.";
            return true;
        }
        _magicAim = aim;
        var moving = _movingGlow; _movingGlow = null;
        SetMagicPanelOpen(true);
        _magicMessageText = "Spot selected. The Gubble will check whether it can reach it.";
        if (moving != null && Host is { } host) MagicResult(host.PlayerAdjustGlow(moving, aim.Point), "The light moved.");
        RefreshMagicPanel();
        if (aim.Entity != null && _repairIds.IndexOf(aim.Entity) is var repair && repair >= 0)
        { _repairChoice.Select(repair); RefreshRepairCost(); }
        return true;
    }

    private void MagicResult(JsonObject result, string success)
    {
        if (result["ok"]?.GetValue<bool>() == true) _magicMessageText = success;
        else
        {
            _magicMessageText = result["error"]?["code"]?.GetValue<string>() switch
            {
                "approval_required" => "This needs your confirmation. Review the approval card to finish.",
                "out_of_bounds" => "That spot is out of reach. Bring the Gubble closer and try again.",
                "target_not_found" => "The Gubble cannot see or find that spot now. Choose another spot.",
                "target_protected" or "permission_denied" => "That place is protected, or this action is not allowed.",
                "budget_exceeded" => "There is no room for another effect yet. Stop one and try again.",
                "revision_conflict" or "approval_mismatch" => "Something changed. Choose the spot and preview it again.",
                // The host's own words (they say what is busy: one fire at a time, which burns out or is Snuffed).
                "target_busy" => result["error"]?["message"]?.GetValue<string>() ?? "Let the current action finish first.",
                "not_ready" => "The journal or connection is not ready. Your earlier progress is safe.",
                "unsupported_capability" => "That magic is not available here yet.",
                // The pool is short of the price (the host's own words), and the pips blink.
                "energy_exhausted" => FlashPips(Kernel.CommandHost.EnergyExhaustedMessage),
                "invalid_args" => "That magic does not fit this spot, or the materials needed are missing.",
                _ => "That did not work here. Try another spot or check the current limits.",
            };
            if (result["error"]?["code"]?.GetValue<string>() == "approval_required") SetMagicPanelOpen(false);
        }
        RefreshMagicPanel();
    }

    private void TickMagicPanel(double delta)
    {
        _magicRefreshAge += delta;
        if (_magicRefreshAge < .15) return;
        _magicRefreshAge = 0;
        RefreshMagicPanel();
    }

    private void RefreshMagicPanel()
    {
        if (_magicPanel == null) return;
        var viewport = GetViewport().GetVisibleRect().Size;
        _magicPanel.Size = new Vector2(Mathf.Clamp(viewport.X - 36, 320, 480), Mathf.Clamp(viewport.Y - 200, 300, 620));
        _magicTargetText.Text = !_magicAim.Hit || _magicAim.AtGubble ? "Aim: on the Gubble" : _magicAim.Entity is { } id && id.StartsWith("creation:", StringComparison.Ordinal) ? "Aim: your selected creation" : "Aim: your selected spot";
        if (_magicMessage != null) _magicMessage.Text = _magicMessageText;
        if (Host is not { } host) return;
        var weather = WeatherWords(host.NatureWeather.Pattern);
        // The meters are drawn: this line carries their numbers only when the player's setting asks for them.
        _islandVitalsLine.Text = host.IslandHeld ? $"{IslandPaused.Name} · {weather}" : host.Mode == "creative" || !MeterNumbers ? $"{ModeWords(host.Mode)} · {weather}" :
            $"{ModeWords(host.Mode)} · Health {host.Vitals.Health:0}/{host.Vitals.MaxHealth:0} · {MeterWords.Name(host, "stamina")} {StaminaPercent(host.Vitals.Stamina, host.Vitals.MaxStamina):0}% · {weather}";
        if (!MagicPanelOpen) return;
        RefreshNaturePage(); RefreshGlowPage(); RefreshWishPage();
    }

    /// <summary>An empty pool refused a cast: the pips blink; the words are the host's.</summary>
    private string FlashPips(string words)
    {
        FlashPipMeters();
        return words;
    }

    private static string ModeWords(string mode) => mode switch { "challenge" => "Challenge", "live" => "Live", _ => "Creative" };
    private static string WeatherWords(string pattern) => pattern switch { "rain" => "Rain", "showers" => "Passing showers", _ => "Fair weather" };
    private static float StaminaPercent(float current, float maximum) => maximum > 0 ? Math.Clamp(current / maximum * 100, 0, 100) : 0;
}
