using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The camp's screen (MECHANICS.md decision 87: sitting at a camp "opens the camp: the place to manage magic, see the expertise found,
/// check stats and knacks, manage the inventory (not built yet) and spend Fractals"). Sitting down (E or Z by a lit campfire) opens it on
/// the left, where the Magic panel opens, under the meters (they stay in view, the hearts healing while you rest) and beside the fire's view
/// (the look lane's resting scene); standing up closes it: E or its Stand up button, a step away (the host's rest ends when the body
/// moves), or the first Esc. Its pages are CampPages': Magic (the island's abilities and the Magic panel itself, opened over the camp and
/// back to it when it closes), the expertise found, You (hearts, bar, pips, knacks), the Fractals with the camp's two spends, and an
/// Inventory page that says it is coming. Every number is the host's, every button the player's own command through the host.
/// </summary>
public partial class RoomHud
{
    /// <summary>How often an open page is read again from the host (as the journal's).</summary>
    public const double CampRefreshS = 0.5;
    public const string CampHeading = "THE CAMP";

    private PanelContainer _campScreen = null!;
    private TabContainer _campTabs = null!;
    private Label _campLine = null!, _campFoot = null!;
    private readonly List<VBoxContainer> _campPages = new();
    private readonly List<List<JournalLine>> _campLines = new();
    private bool _campOpen;
    private double _campAge;

    /// <summary>Whether the camp is open (sitting by a fire); it stands aside, still open, while a panel or the journal holds the screen.</summary>
    public bool CampOpen => _campOpen;
    public PanelContainer CampScreen => _campScreen;
    public TabContainer CampTabs => _campTabs;
    /// <summary>The page shown now (its index into CampPages.Titles) and its words, one line each (the headless suite reads it).</summary>
    public int CampPage => _campTabs.CurrentTab;
    public string CampPageText => _campLines.Count > CampPage && CampPage >= 0 ? string.Join("\n", _campLines[CampPage].Select(l => l.Text)) : "";
    /// <summary>The words at the camp's foot: what its last button answered.</summary>
    public string CampFoot => _campFoot.Text;

    private void BuildCampScreen(Theme theme)
    {
        _campScreen = new PanelContainer { Name = "CampScreen", Position = new Vector2(18, 150), Theme = theme, Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_campScreen);
        var column = new VBoxContainer { Name = "CampColumn" };
        _campScreen.AddChild(column);
        var heading = new HBoxContainer();
        column.AddChild(heading);
        heading.AddChild(new Label { Text = CampHeading, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        // The camp never takes the keyboard focus (the body still walks while it is open: Space jumps, and must never press a button).
        var stand = new Button { Name = "CampStandUp", CustomMinimumSize = new Vector2(0, 26), FocusMode = Control.FocusModeEnum.None };
        stand.Pressed += StandFromCamp;
        heading.AddChild(stand);
        _keyTexts.Add((words => stand.Text = words, () => $"Stand up ({K(Act.Interact)})"));
        _campLine = WrapMagicLabel("");
        _campLine.Name = "CampLine";
        column.AddChild(_campLine);
        _campTabs = new TabContainer { Name = "CampTabs", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TabFocusMode = Control.FocusModeEnum.None };
        column.AddChild(_campTabs);
        foreach (var title in CampPages.Titles(null))
        {
            var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _campTabs.AddChild(scroll);
            var page = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            page.AddThemeConstantOverride("separation", 6);
            scroll.AddChild(page);
            _campPages.Add(page);
            _campLines.Add(new List<JournalLine>());
        }
        _campTabs.TabChanged += _ => RefreshCamp(force: true);
        _campFoot = WrapMagicLabel("");
        _campFoot.Name = "CampFoot";
        column.AddChild(_campFoot);
    }

    /// <summary>The camp opens (the player sat down): at the page it was left on, read from the host now.</summary>
    private void OpenCamp(string restWords)
    {
        _campOpen = true;
        _campLine.Text = restWords;
        _campFoot.Text = "";
        RefreshCamp(force: true);
    }

    /// <summary>The camp closes (the player stood up).</summary>
    private void CloseCamp()
    {
        _campOpen = false;
        _campScreen.Visible = false;
    }

    /// <summary>The camp's Stand up button: as E standing (the host's PlayerStand).</summary>
    private void StandFromCamp()
    {
        if (!_sitting) return;
        RestKey();
    }

    /// <summary>Each frame: the screen shows while the camp is open and no panel holds the screen; an open page is read again now and then.</summary>
    private void TickCampScreen(double delta)
    {
        var shown = _campOpen && !ReadingPanelOpen && !IntroPlaying && !ChoosingMagicSpot;
        if (_campScreen.Visible != shown)
        {
            _campScreen.Visible = shown;
            if (shown) RefreshCamp(force: true);
        }
        if (!shown) return;
        _campAge += delta;
        if (_campAge >= CampRefreshS) RefreshCamp();
    }

    /// <summary>The page shown, read again from the host; its controls are written out again only when its lines changed.</summary>
    private void RefreshCamp(bool force = false)
    {
        _campAge = 0;
        if (Host is not { } host || !_campOpen) return;
        var viewport = GetViewport().GetVisibleRect().Size;
        _campScreen.Size = new Vector2(Mathf.Clamp(viewport.X - 36, 320, 500), Mathf.Clamp(viewport.Y - 200, 300, 600));
        var titles = CampPages.Titles(host);
        for (var i = 0; i < titles.Length; i++) _campTabs.SetTabTitle(i, titles[i]);
        var page = _campTabs.CurrentTab;
        if (page < 0 || page >= _campPages.Count) return;
        var lines = CampPages.Build(page, host);
        if (!force && lines.SequenceEqual(_campLines[page])) return;
        _campLines[page] = lines;
        WriteCampLines(_campPages[page], lines);
    }

    /// <summary>The lines as labels and buttons, in the HUD's own hand (the journal writes the same lines in its book's).</summary>
    private void WriteCampLines(VBoxContainer page, List<JournalLine> lines)
    {
        foreach (var child in page.GetChildren()) { page.RemoveChild(child); child.QueueFree(); }
        foreach (var line in lines)
        {
            switch (line.Ink)
            {
                case JournalInk.Button:
                    var target = line.Target;
                    var button = new Button { Name = target.Replace(':', '_'), Text = line.Text, Disabled = !line.Enabled, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 32),
                        SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
                    button.Pressed += () => CampButton(target);
                    page.AddChild(button);
                    break;
                case JournalInk.Link or JournalInk.Map:
                    break;
                default:
                    var label = WrapMagicLabel(line.Text);
                    label.AddThemeFontSizeOverride("font_size", line.Ink switch { JournalInk.Heading => 19, JournalInk.Title => 17, JournalInk.Faint or JournalInk.Smudge => 14, _ => 16 });
                    if (line.Ink is JournalInk.Faint or JournalInk.Smudge) label.AddThemeColorOverride("font_color", MenuStyle.Muted);
                    page.AddChild(label);
                    break;
            }
        }
    }

    /// <summary>A button on a camp page: the Fractals' spends, a knack, or the Magic panel. The words it answers with go to the camp's foot.</summary>
    public string CampButton(string target)
    {
        string words;
        if (target == CampPages.MagicTarget)
        {
            // The Magic panel itself, over the camp (it stands aside while the panel is open, and is back when it closes).
            SetMagicPanelOpen(true);
            return "";
        }
        if (target.StartsWith(CampPages.KnackPrefix, StringComparison.Ordinal) && target.Split(':') is [_, var slotText, var knack] && int.TryParse(slotText, out var slot))
        {
            if (Host is not { } host) return "";
            var result = host.PlayerSetKnack(slot, knack);
            words = result["ok"]?.GetValue<bool>() == true ? $"Your knack is {MeterWords.Knack(host, knack)} now." : result["error"]?["message"]?.GetValue<string>() ?? "That knack could not be chosen.";
            _noticeText = words;
        }
        else words = JournalButton(target);
        _campFoot.Text = words;
        RefreshCamp(force: true);
        return words;
    }
}
