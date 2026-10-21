using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// The open book: a stitched leather spread of handwritten parchment (JournalPaper), the cover's words on the left page and the
/// chosen page on the right, written by JournalPages from the host's data. The pages are read again while the book is open, so
/// a discovery made, a note saved or a remapped key shows without turning the page. A note is the player's own words, written
/// through journal.note; names and notes are shown literally (plain labels, never markup).
/// </summary>
public partial class JournalPanel : Control
{
    public const string IndexPage = JournalPages.Index;
    /// <summary>How often an open page is read again from the host, and the map redrawn.</summary>
    public const double RefreshSeconds = 0.5;
    public const double MapRefreshSeconds = 1.0;
    private static readonly Color FaintInk = new("846849");
    private static readonly Color SmudgeInk = new(0.45f, 0.36f, 0.26f, 0.45f);

    public Func<CommandHost?> FindHost { get; set; } = () => null;
    public Action CloseRequested { get; set; } = () => { };
    /// <summary>
    /// A button on a page was pressed (its Target): the HUD does what it asks and answers with the words to show at the page's
    /// foot (the host's own, written for a child). The page is read again at once.
    /// </summary>
    public Func<string, string> ButtonPressed { get; set; } = _ => "";
    /// <summary>The journal key's name now (the cover's words name it, with Esc).</summary>
    public Func<string> KeyLabel { get; set; } = () => "";
    /// <summary>Set by the book while it is opening or open: only then are the pages read again.</summary>
    public bool Reading { get; set; }
    public string Page { get; private set; } = IndexPage;
    public JournalMap? MapView { get; private set; }
    /// <summary>The page's lines as written now.</summary>
    public IReadOnlyList<JournalLine> Lines => _lines;
    /// <summary>The page's words, one line each (the headless suite reads the book through this).</summary>
    public string PageText => string.Join("\n", _lines.Select(line => line.Text));
    /// <summary>The line at the foot of the page: the page's own, or what became of the last note.</summary>
    public string Footer => _footer?.Text ?? "";
    private List<JournalLine> _lines = new();
    private VBoxContainer _entries = null!;
    private Label _footer = null!;
    private Label _heading = null!;
    private Label _companionLine = null!;
    private Button _closeButton = null!;
    private LineEdit _note = null!;
    private VBoxContainer _editor = null!;
    private ScrollContainer _scroll = null!;
    private Button _more = null!;
    private int _leaf;
    private string _message = "";
    private double _refreshAge;
    private double _mapAge;

    public override void _Ready()
    {
        Name = "JournalPanel";
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        OffsetLeft = 28;
        OffsetTop = 24;
        OffsetRight = -28;
        OffsetBottom = -24;
        Theme = BookTheme();
        var paper = new JournalPaper();
        AddChild(paper);
        paper.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margins = new MarginContainer();
        AddChild(margins);
        margins.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margins.AddThemeConstantOverride("margin_left", 50);
        margins.AddThemeConstantOverride("margin_right", 50);
        margins.AddThemeConstantOverride("margin_top", 32);
        margins.AddThemeConstantOverride("margin_bottom", 32);
        var spread = new HBoxContainer();
        spread.AddThemeConstantOverride("separation", 72);
        margins.AddChild(spread);
        // The left page: the cover's words, the way back to the index, and the way out.
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 0.84f };
        left.AddThemeConstantOverride("separation", 14);
        spread.AddChild(left);
        Write(left, "ENFRACTAL", 16, new Color("816344"));
        Write(left, "Our field\njournal", 42);
        Write(left, "A shared adventure", 23);
        _companionLine = Write(left, "You & the Gubble", 20, FaintInk);
        left.AddChild(new HSeparator());
        Write(left, "Places discovered, small wonders,\nand things worth remembering.", 19);
        AddLink(left, "Return to the index", () => OpenPage(IndexPage));
        left.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        Write(left, "Some pages are still unwritten.", 17, new Color("816344"));
        _closeButton = AddLink(left, "Close the journal", () => CloseRequested());
        _closeButton.Name = "JournalCloseButton";
        // The right page: the chosen page, a further leaf, the line at its foot, and the note editor on the notes page.
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 10);
        spread.AddChild(right);
        _heading = Write(right, IndexPage, 30);
        _heading.Name = "JournalHeading";
        right.AddChild(new HSeparator());
        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        right.AddChild(_scroll);
        _entries = new VBoxContainer { Name = "JournalEntries", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _entries.AddThemeConstantOverride("separation", 14);
        _scroll.AddChild(_entries);
        _more = AddLink(right, "Next page →", NextLeaf);
        _more.Name = "JournalNextLeaf";
        _footer = Write(right, "", 15, new Color("806447"));
        _footer.Name = "JournalStatus";
        _editor = new VBoxContainer { Name = "JournalEditor" };
        right.AddChild(_editor);
        Write(_editor, "Leave a thought on the page…", 16);
        _note = new LineEdit { Name = "JournalNote", MaxLength = 280, PlaceholderText = "Something worth remembering" };
        _editor.AddChild(_note);
        _note.TextSubmitted += _ => SaveNote();
        AddLink(_editor, "Save this note", SaveNote).Name = "JournalSaveNote";
        OpenPage(IndexPage);
    }

    /// <summary>Handwriting from the system's own fonts (none is downloaded), in brown ink.</summary>
    private static Theme BookTheme()
    {
        var theme = new Theme
        {
            DefaultFontSize = 19,
            DefaultFont = new SystemFont { FontNames = new[] { "Segoe Print", "Chilanka", "Comic Sans MS", "cursive" } },
        };
        var ink = new Color("443326");
        foreach (var type in new[] { "Label", "Button", "LineEdit" })
        {
            theme.SetColor("font_color", type, ink);
            theme.SetColor("font_hover_color", type, new Color("8c482c"));
            theme.SetColor("font_pressed_color", type, new Color("a46030"));
            theme.SetColor("font_focus_color", type, ink);
        }
        foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
            theme.SetStylebox(state, "Button", new StyleBoxFlat
            {
                BgColor = new Color(0.45f, 0.29f, 0.12f, state == "hover" ? 0.12f : 0),
                BorderColor = new Color(0.45f, 0.29f, 0.12f, state == "focus" ? 0.55f : 0.15f), BorderWidthBottom = 1,
                ContentMarginTop = 7, ContentMarginBottom = 7, ContentMarginLeft = 8, ContentMarginRight = 8,
            });
        theme.SetStylebox("normal", "LineEdit", new StyleBoxFlat
        {
            BgColor = new Color(1, 0.95f, 0.8f, 0.18f), BorderColor = new Color("9c8057"), BorderWidthBottom = 1,
            ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 6, ContentMarginBottom = 6,
        });
        theme.SetColor("font_placeholder_color", "LineEdit", new Color("887352"));
        theme.SetColor("caret_color", "LineEdit", ink);
        // An action button: the same ink in a box of its own, so it reads as something to press and not as a link to turn to.
        theme.SetTypeVariation(ActionButton, "Button");
        theme.SetColor("font_color", ActionButton, ink);
        theme.SetColor("font_hover_color", ActionButton, new Color("8c482c"));
        theme.SetColor("font_pressed_color", ActionButton, new Color("a46030"));
        theme.SetColor("font_disabled_color", ActionButton, new Color(0.27f, 0.2f, 0.15f, 0.45f));
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            theme.SetStylebox(state, ActionButton, new StyleBoxFlat
            {
                BgColor = new Color(0.45f, 0.29f, 0.12f, state == "hover" ? 0.2f : state == "pressed" ? 0.28f : state == "disabled" ? 0.03f : 0.1f),
                BorderColor = new Color(0.45f, 0.29f, 0.12f, state == "disabled" ? 0.2f : 0.7f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginTop = 7, ContentMarginBottom = 7, ContentMarginLeft = 14, ContentMarginRight = 14,
            });
        return theme;
    }

    /// <summary>The look of a page's action buttons (the Fractals' two): a box round the words, dimmed when the button is off.</summary>
    public const string ActionButton = "ActionButton";

    /// <summary>Words on a page: a plain label, wrapped, never markup.</summary>
    private static Label Write(Node parent, string text, int size = 19, Color? colour = null)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        if (colour != null) label.AddThemeColorOverride("font_color", colour.Value);
        parent.AddChild(label);
        return label;
    }

    /// <summary>A link on a page: a click follows it. It never takes the keyboard focus, so Space and Enter never press it.</summary>
    private static Button AddLink(Node parent, string text, Action pressed)
    {
        var button = new Button { Text = text, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 40), FocusMode = FocusModeEnum.None };
        button.Pressed += pressed;
        parent.AddChild(button);
        return button;
    }

    /// <summary>Turn to a page, at its first leaf.</summary>
    public void OpenPage(string page)
    {
        Page = page;
        _leaf = 0;
        _message = "";
        if (_scroll != null) _scroll.ScrollVertical = 0;
        Refresh(force: true);
    }

    private void NextLeaf()
    {
        _leaf += JournalPages.PerLeaf;
        if (_scroll != null) _scroll.ScrollVertical = 0;
        Refresh(force: true);
    }

    public override void _Process(double delta)
    {
        if (!Reading) return;
        _refreshAge += delta;
        _mapAge += delta;
        if (_refreshAge >= RefreshSeconds) Refresh();
        if (MapView != null && _mapAge >= MapRefreshSeconds && FindHost() is { } host)
        {
            _mapAge = 0;
            MapView.UpdateFrom(host);
        }
    }

    /// <summary>Save the note in the editor as the player's journal.note; the editor clears only when the host took it.</summary>
    public void SaveNote()
    {
        if (FindHost() is not { } host)
        {
            _message = "The journal is not ready yet.";
            Refresh();
            return;
        }
        var result = host.PlayerJournalNote(_note.Text.Trim());
        if (result["ok"]?.GetValue<bool>() != true)
        {
            _message = result["error"]?["message"]?.GetValue<string>() ?? "The note could not be saved.";
            Refresh();
            return;
        }
        _note.Text = "";
        OpenPage(JournalPages.Notes);
        _message = "Your note is saved.";
        Refresh();
    }

    /// <summary>A button was pressed: the HUD does what it asks, the words it answers with go to the page's foot, and the page is read again.</summary>
    public void Press(string action)
    {
        _message = ButtonPressed(action);
        Refresh(force: true);
    }

    /// <summary>Read the page again from the host; its lines are written out again only when they changed.</summary>
    public void Refresh(bool force = false)
    {
        if (_entries == null) return;
        _refreshAge = 0;
        var host = FindHost();
        var page = JournalPages.Build(Page, host, _leaf);
        // The page's name on screen: the Discoveries page reads the pack's word for expertise (JournalPages.Title).
        _heading.Text = JournalPages.Title(Page, host);
        _more.Visible = page.HasMore;
        _editor.Visible = Page == JournalPages.Notes;
        _footer.Text = _message.Length > 0 ? _message : page.Footer;
        var companion = host?.Companion is { } body && IsInstanceValid(body) ? KernelJson.DisplayText(body.CompanionName, 40) : "the Gubble";
        _companionLine.Text = $"You & {companion}";
        var key = KeyLabel();
        var escape = PlayerControls.Label(Act.MouseRelease);
        _closeButton.Text = key.Length > 0 ? $"Close the journal · {key} or {escape}" : $"Close the journal · {escape}";
        if (!force && page.Lines.SequenceEqual(_lines)) return;
        _lines = page.Lines.ToList();
        WriteLines(host);
    }

    /// <summary>
    /// The lines as labels, links and the map. A heading, a title or a link starts a group; the lines after it join that group,
    /// close beneath it, and groups stand apart.
    /// </summary>
    private void WriteLines(CommandHost? host)
    {
        foreach (var child in _entries.GetChildren())
        {
            _entries.RemoveChild(child);
            child.QueueFree();
        }
        MapView = null;
        VBoxContainer? group = null;
        foreach (var line in _lines)
        {
            if (group == null || line.Ink is JournalInk.Heading or JournalInk.Title or JournalInk.Link)
            {
                group = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                group.AddThemeConstantOverride("separation", line.Ink == JournalInk.Link ? 0 : 4);
                _entries.AddChild(group);
            }
            switch (line.Ink)
            {
                case JournalInk.Heading: Write(group, line.Text, 24); break;
                case JournalInk.Title: Write(group, line.Text, 22); break;
                case JournalInk.Faint: Write(group, line.Text, 16, FaintInk); break;
                case JournalInk.Smudge: Write(group, line.Text, 19, SmudgeInk).Name = "JournalSmudge"; break;
                case JournalInk.Link:
                    var target = line.Target;
                    AddLink(group, line.Text, () => OpenPage(target));
                    break;
                case JournalInk.Button:
                    var action = line.Target;
                    var button = AddLink(group, line.Text, () => Press(action));
                    button.ThemeTypeVariation = ActionButton;
                    button.Name = action.Replace(':', '_');
                    button.Disabled = !line.Enabled;
                    button.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                    button.Alignment = HorizontalAlignment.Center;
                    break;
                case JournalInk.Map:
                    MapView = new JournalMap { Name = "JournalMap", CustomMinimumSize = new Vector2(0, 325), SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    group.AddChild(MapView);
                    if (host != null) MapView.UpdateFrom(host);
                    _mapAge = 0;
                    break;
                default: Write(group, line.Text); break;
            }
        }
    }
}
