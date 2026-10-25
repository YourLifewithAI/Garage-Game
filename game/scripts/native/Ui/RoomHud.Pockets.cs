using System;
using System.Linq;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The pockets panel (MECHANICS.md decisions 98 to 103, 108 and 111; docs/game/INVENTORY.md): Tab opens and closes a small panel on the
/// left, where the Magic panel opens, listing what the pockets hold, each entry with its name (untrusted display text, plain in a label),
/// its count, its mass and, for food, what a unit heals, and the pockets' caps; each entry has Eat (the player's inventory.use: a unit
/// of food, healing at once, or in Creative only its sound) and Drop (inventory.drop: a thing set down before the player). The host
/// decides everything and its refusals, written for a child (hearts full, nothing to set down yet, another island's thing), are the
/// answer, shown at the panel's foot and as a notice. It is a panel like the Magic panel's: the body waits while it is open, nothing on
/// it takes the keyboard, Esc closes it first (decision 97), and opening any other panel closes it.
/// </summary>
public partial class RoomHud
{
    public const string PocketsHeading = "YOUR POCKETS";
    /// <summary>How often an open panel is read again from the host (as the camp's).</summary>
    public const double PocketsRefreshS = 0.5;
    public const string PocketsEmptyWords = "Nothing in your pockets yet. E picks food into them, and puts away a small thing you hold.";

    private PanelContainer _pocketsPanel = null!;
    private Label _pocketsCaps = null!, _pocketsFoot = null!;
    private VBoxContainer _pocketsList = null!;
    private double _pocketsAge;
    private string _pocketsBuilt = "";

    /// <summary>Whether the pockets panel is open (the body waits while it is).</summary>
    public bool PocketsPanelOpen => _pocketsPanel != null && _pocketsPanel.Visible;
    public PanelContainer PocketsPanel => _pocketsPanel;
    /// <summary>The caps line and the foot's words (what the last button answered), for the headless suite.</summary>
    public string PocketsCapsText => _pocketsCaps.Text;
    public string PocketsFoot => _pocketsFoot.Text;
    /// <summary>The panel's entries as rows, in the pockets' order (tests).</summary>
    public System.Collections.Generic.IEnumerable<HBoxContainer> PocketRows => _pocketsList.GetChildren().OfType<HBoxContainer>();

    private void BuildPocketsPanel(Theme theme)
    {
        _pocketsPanel = new PanelContainer { Name = "PocketsPanel", Position = new Vector2(18, 150), Theme = theme, Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_pocketsPanel);
        var column = new VBoxContainer { Name = "PocketsColumn" };
        _pocketsPanel.AddChild(column);
        var heading = new HBoxContainer();
        column.AddChild(heading);
        heading.AddChild(new Label { Text = PocketsHeading, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        // Nothing on the panel takes the keyboard (the body waits, but Space and Tab must never press a button).
        var close = new Button { Name = "PocketsClose", CustomMinimumSize = new Vector2(0, 26), FocusMode = Control.FocusModeEnum.None };
        close.Pressed += () => SetPocketsPanelOpen(false);
        heading.AddChild(close);
        _keyTexts.Add((words => close.Text = words, () => $"Close ({K(Act.PocketsPanel)})"));
        _pocketsCaps = WrapMagicLabel("");
        _pocketsCaps.Name = "PocketsCaps";
        column.AddChild(_pocketsCaps);
        var scroll = new ScrollContainer { Name = "PocketsScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(scroll);
        _pocketsList = new VBoxContainer { Name = "PocketsList", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _pocketsList.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_pocketsList);
        _pocketsFoot = WrapMagicLabel("");
        _pocketsFoot.Name = "PocketsFoot";
        column.AddChild(_pocketsFoot);
    }

    /// <summary>The pockets key: open the panel, or close it again.</summary>
    public void TogglePocketsPanel() => SetPocketsPanelOpen(!PocketsPanelOpen);

    /// <summary>
    /// Open or close the panel. Opening closes the journal, the settings and the Magic panel (and calls off the wheel, a drag and a spot
    /// being chosen), as the Magic panel does, and the body waits; closing gives the body back unless another panel holds it.
    /// </summary>
    public void SetPocketsPanelOpen(bool open)
    {
        if (open == PocketsPanelOpen) return;
        CancelAsk();
        EndDrag();
        if (open)
        {
            CloseJournalNow();
            if (SettingsOpen) ToggleSettings();
            if (MagicPanelOpen) SetMagicPanelOpen(false);
            _pickingMagicSpot = false;
            _movingGlow = null;
            _pocketsFoot.Text = "";
        }
        _pocketsPanel.Visible = open;
        Player.SetInputEnabled(!ReadingPanelOpen);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (open) RefreshPockets(force: true);
    }

    /// <summary>Each frame an open panel is read again from the host at PocketsRefreshS (a pick, a fall's loss or a knockout changes the pockets under it).</summary>
    private void TickPockets(double delta)
    {
        if (!PocketsPanelOpen) return;
        _pocketsAge += delta;
        if (_pocketsAge >= PocketsRefreshS) RefreshPockets();
    }

    /// <summary>The panel read from the host: its rows are made again only when what they say changed (so a pressed button is never rebuilt under the mouse).</summary>
    private void RefreshPockets(bool force = false)
    {
        _pocketsAge = 0;
        if (Host is not { } host) return;
        var viewport = GetViewport().GetVisibleRect().Size;
        _pocketsPanel.Size = new Vector2(Mathf.Clamp(viewport.X - 36, 320, 480), Mathf.Clamp(viewport.Y - 200, 260, 460));
        var entries = host.HasInventory ? host.PocketEntries : Array.Empty<InventoryEntryView>();
        var caps = host.HasInventory ? InventoryPages.PocketsCaps(host) : CommandHost.InventoryNotHere;
        if (_pocketsCaps.Text != caps) _pocketsCaps.Text = caps;
        var built = string.Join('\n', entries.Select(e => e.Item + "|" + InventoryPages.EntryWords(host, e)));
        if (!force && built == _pocketsBuilt) return;
        _pocketsBuilt = built;
        foreach (var child in _pocketsList.GetChildren()) { _pocketsList.RemoveChild(child); child.QueueFree(); }
        if (entries.Count == 0)
        {
            if (host.HasInventory) _pocketsList.AddChild(WrapMagicLabel(PocketsEmptyWords));
            return;
        }
        foreach (var entry in entries)
        {
            var safe = entry.Item.Replace(':', '_');
            var row = new HBoxContainer { Name = "Pocket_" + safe, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);
            var label = WrapMagicLabel(InventoryPages.EntryWords(host, entry));
            label.Name = "Entry";
            // (A wrapping label in a row has no width of its own: it asks for one.)
            label.CustomMinimumSize = new Vector2(190, 0);
            row.AddChild(label);
            // A thing is eaten by nobody; a thing from another island comes out there, not here (the host says so if pressed anyway).
            row.AddChild(PocketButton("Eat_" + safe, "Eat", InventoryPages.Target(InventoryPages.Eat, entry.Item), entry.Kind == "stack"));
            row.AddChild(PocketButton("Drop_" + safe, "Drop", InventoryPages.Target(InventoryPages.Drop, entry.Item), entry.Here));
            _pocketsList.AddChild(row);
        }
    }

    private Button PocketButton(string name, string text, string target, bool enabled)
    {
        var button = new Button { Name = name, Text = text, Disabled = !enabled, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(64, 32), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        button.Pressed += () => PocketsButton(target);
        return button;
    }

    /// <summary>A button on the panel: Eat or Drop as the player's own command; the host's words go to the panel's foot and the notice.</summary>
    public string PocketsButton(string target)
    {
        var words = Host is { } host ? InventoryPages.Run(host, target) : "The command host is not attached; the pockets are off.";
        _pocketsFoot.Text = words;
        _noticeText = words;
        if (PocketsPanelOpen) RefreshPockets(force: true);
        return words;
    }

    /// <summary>
    /// Tab is also the engine's focus-next key, which a button the player clicked (a footer button) would take before the HUD's unhandled
    /// input saw it: with no panel or menu of the HUD's own open (their buttons are meant to take the keyboard), the focus is let go first.
    /// </summary>
    public override void _Input(InputEvent input)
    {
        if (!PlayerControls.Fresh(input) || !PlayerControls.Hit(PlayerControls.Normalise(input), Act.PocketsPanel)) return;
        if (_pauseOpen || SettingsOpen || JournalOpen || GubbleViewOpen) return;
        if (GetViewport().GuiGetFocusOwner() != null) GetViewport().GuiReleaseFocus();
    }
}
