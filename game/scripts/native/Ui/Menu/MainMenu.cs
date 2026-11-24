using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>
/// The opening menu (docs/game/MECHANICS.md section 3; scenes/menu.tscn): three rows of big cards and the start rule's buttons.
/// <list type="number">
/// <item><b>Choose your character.</b> One card per team (the avatar, its name, its trait stats, the islands visited, the Gubble with its
/// colour and expertise count, and Trust in words), and "New team" (MainMenu.NewTeam.cs).</item>
/// <item><b>Where to?</b> One card per island with its picture drawn from the island's shape data, the tutorial first, and this team's saves
/// there by mode.</item>
/// <item><b>Mode.</b> Creative, Challenge (Gentle, Standard, Brave) or "Live (for now just a solo preview)", shown and refused while the island
/// rules say it is not available; on an island with a Creative save and no Challenge one, "Start fresh" or "Copy my Creative island
/// into Challenge", once per island per team.</item>
/// <item><b>The start rule's buttons</b>, where Play was (Run 4, MENU-3; section 3, "Starting and continuing"), for the team, the island
/// and the mode chosen above: <b>Continue</b> (two lines: the word, and under it "Mira on the Garage, Challenge"), only where its
/// save is; "Bring my game here" where this mode's game is on another island; and <b>New game</b>, always. New game over a game asks once: the question
/// stands where the hint was, "Go back" takes the focus, and New game pressed again puts the old game away (kept, never deleted)
/// and starts a first one. Nothing else opens a save. The menu opens with the last game chosen, so Continue is one click; while a
/// room of the team is still leaving, the buttons wait for it.</item>
/// </list>
/// The menu is MenuModel drawn: every click goes to the model and the rows show what it says. A launch is handed to <see cref="Launch"/>
/// (MenuBoot boots the room with the team, island, mode and difficulty, through the same boot that --team --mode --difficulty take).
/// </summary>
public partial class MainMenu : Control
{
    public MenuModel Model { get; set; } = null!;
    /// <summary>The shipped characters (ids, names, thumbnails) for the avatars and the New team panel; read when the menu opens.</summary>
    public Func<IReadOnlyList<CharacterChoice>?>? Characters { get; set; }
    /// <summary>Told the game to start (Continue, "Bring my game here" or New game).</summary>
    public Action<MenuLaunch>? Launch { get; set; }
    public Action? QuitGame { get; set; }

    private readonly Dictionary<string, Button> _teamButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _islandButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IslandThumbnail> _thumbnails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _modeButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _difficultyButtons = new(StringComparer.Ordinal);
    private readonly ButtonGroup _teamGroup = new(), _islandGroup = new(), _modeGroup = new(), _difficultyGroup = new(), _copyGroup = new();
    private HBoxContainer _teamRow = null!, _islandRow = null!;
    private HBoxContainer _knackRow = null!;
    private OptionButton _knackSwap = null!;
    private Label _knackNote = null!;
    private VBoxContainer _modeDetail = null!;
    private Label _savesLine = null!, _difficultyLabel = null!;
    private HBoxContainer _difficultyRow = null!, _copyRow = null!;
    private Button _startFresh = null!, _copyIt = null!;
    private Label _copyNote = null!;
    private Label _notice = null!, _hint = null!;
    private Button _newGame = null!, _continue = null!, _bring = null!, _goBack = null!, _quit = null!, _newTeam = null!, _editTeam = null!;
    /// <summary>The foot: the menu's words and the start rule's buttons.</summary>
    private HBoxContainer _foot = null!;
    /// <summary>The start buttons wait for a room of the team that is still leaving.</summary>
    private bool _waiting;
    /// <summary>How the team is held as the foot last showed it: while it is held at all, the lease is asked again a few times a second.</summary>
    private TeamHold _hold = TeamHold.Free;
    private double _sinceAsked;
    /// <summary>When New game's question went up (the engine's milliseconds).</summary>
    private ulong _questionAt;
    /// <summary>The mouse press now on New game is the second of a double click, as the system counts one.</summary>
    private bool _secondOfDouble;
    private bool _newGameLeads;
    /// <summary>Continue's two lines (the word, then the game it opens), and what the start rule offers as last read from the team's folder.</summary>
    private Label _continueLine = null!;
    private VBoxContainer _continueLines = null!;
    private StartOffer _offer = StartOffer.None;
    private IReadOnlyList<CharacterChoice> _characters = Array.Empty<CharacterChoice>();
    private IslandRules? Rules => Model.Rules;

    public IReadOnlyDictionary<string, Button> TeamButtons => _teamButtons;
    public IReadOnlyDictionary<string, Button> IslandButtons => _islandButtons;
    public IReadOnlyDictionary<string, IslandThumbnail> Thumbnails => _thumbnails;
    public IReadOnlyDictionary<string, Button> ModeButtons => _modeButtons;
    public IReadOnlyDictionary<string, Button> DifficultyButtons => _difficultyButtons;
    public Button NewTeamButton => _newTeam;
    public Button EditTeamButton => _editTeam;
    public Button StartFreshButton => _startFresh;
    public Button CopyButton => _copyIt;
    public Label CopyNote => _copyNote;
    public Label SavesLine => _savesLine;
    public HBoxContainer DifficultyRow => _difficultyRow;
    public HBoxContainer CopyRow => _copyRow;
    public Label NoticeLabel => _notice;
    public Label HintLabel => _hint;
    /// <summary>New game: always there, shut while something is not chosen or the team's room is still leaving.</summary>
    public Button NewGameButton => _newGame;
    /// <summary>Continue: shown only where the chosen team, island and mode have a save. Two lines: the word, and the game it opens (ContinueLine).</summary>
    public Button ContinueButton => _continue;
    /// <summary>Continue's second line: "Mira on the Garage, Challenge (Brave)".</summary>
    public Label ContinueLine => _continueLine;
    /// <summary>"Bring my game here": shown only where this mode's game is on another island.</summary>
    public Button BringButton => _bring;
    /// <summary>"Go back": shown only while New game's question is up.</summary>
    public Button GoBackButton => _goBack;
    public HBoxContainer Foot => _foot;
    /// <summary>
    /// For this long after New game's question goes up, New game pressed again does nothing: a double click is one press, never
    /// the question and its answer. Longer than the system's usual double click (half a second); a click the system itself counts
    /// as the second of a double click is never the answer either, however slow (PressNewGame). Provisional; a test shortens it.
    /// </summary>
    public int ConfirmGuardMs { get; set; } = 700;
    /// <summary>
    /// The command line this launch was started with, whose room and mode the page chooses first (MenuModel.ChooseFromLaunch): the
    /// process's own user arguments, the ones MenuBoot opens the menu with. Null is the process's; a test names its own.
    /// </summary>
    public static Func<IReadOnlyList<string>>? LaunchArguments { get; set; }
    public Button QuitButton => _quit;
    public OptionButton KnackSwap => _knackSwap;

    public override void _Ready()
    {
        Name = "MainMenu";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _characters = CharacterChoice.RowOf(Characters?.Invoke()).Skip(1).ToList();
        var theme = new Theme { DefaultFontSize = 18 };
        theme.SetColor("font_color", "Label", MenuStyle.Ink);
        theme.SetColor("font_color", "CheckButton", MenuStyle.Ink);
        Theme = theme;
        var page = new ColorRect { Name = "Page", Color = MenuStyle.Page, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = new MarginContainer { Name = "Margin" };
        foreach (var side in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + side, 32);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(margin);
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var column = new VBoxContainer { Name = "Column" };
        column.AddThemeConstantOverride("separation", 8);
        margin.AddChild(column);

        // The title, and the way out.
        var header = new HBoxContainer { Name = "Header" };
        column.AddChild(header);
        var title = new Label { Name = "Title", Text = "Garage Game" };
        title.AddThemeFontSizeOverride("font_size", 34);
        header.AddChild(title);
        var tagline = MenuStyle.Text("   a room you photographed, grown into an island, and the Gubble who plays it with you", 16, MenuStyle.Muted, "Tagline");
        tagline.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        tagline.VerticalAlignment = VerticalAlignment.Bottom;
        tagline.MouseFilter = MouseFilterEnum.Ignore;
        header.AddChild(tagline);
        _quit = new Button { Name = "Quit", Text = "Quit", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(90, 34) };
        MenuStyle.AsCard(_quit, 8);
        _quit.Pressed += () => QuitGame?.Invoke();
        header.AddChild(_quit);

        // The three rows scroll if the window is small; the foot (Continue, Play) never does.
        var scroll = new ScrollContainer { Name = "Scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        var rows = new VBoxContainer { Name = "Rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rows);

        // Row 1: the teams.
        var teamHeader = new HBoxContainer { Name = "TeamHeader" };
        rows.AddChild(teamHeader);
        teamHeader.AddChild(Section("1   Choose your character"));
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        teamHeader.AddChild(spacer);
        _knackRow = new HBoxContainer { Name = "KnackRow", Visible = false };
        _knackRow.AddThemeConstantOverride("separation", 8);
        teamHeader.AddChild(_knackRow);
        _knackNote = MenuStyle.Text("Knack:", 15, MenuStyle.Muted, "KnackLabel");
        MenuStyle.Unclipped(_knackNote);
        _knackNote.VerticalAlignment = VerticalAlignment.Center;
        _knackRow.AddChild(_knackNote);
        _knackSwap = new OptionButton { Name = "KnackSwap", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(250, 30), FitToLongestItem = false, ClipText = true, TooltipText = "Swap your knack: free in the menu." };
        MenuStyle.AsCard(_knackSwap, 8);
        _knackSwap.ItemSelected += OnKnackSwapped;
        _knackRow.AddChild(_knackSwap);
        _editTeam = new Button { Name = "EditTeam", Text = "Edit this team", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(150, 30), TooltipText = "Redraw yourself any day: your name, your character, the Gubble's name and colour. Everything you have earned stays." };
        MenuStyle.AsCard(_editTeam, 8);
        _editTeam.Pressed += OpenEditTeam;
        _knackRow.AddChild(_editTeam);
        _teamRow = Scrolling(rows, "TeamScroll", "TeamRow", TeamCardHeight);

        // Row 2: the islands.
        rows.AddChild(Section("2   Where to?"));
        _islandRow = Scrolling(rows, "IslandScroll", "IslandRow", IslandCardHeight);

        // Row 3: the mode, and what it needs.
        rows.AddChild(Section("3   How do you want to play?"));
        var modes = new HBoxContainer { Name = "ModeRow" };
        modes.AddThemeConstantOverride("separation", 10);
        rows.AddChild(modes);
        foreach (var (mode, words, line) in new[]
                 {
                     ("creative", "Creative", "Nothing hurts. Build, try, learn."),
                     ("challenge", "Challenge", "Hearts, stamina, and what you do counts."),
                     ("live", MenuModel.ModeWords("live"), "Coming later."),
                 })
        {
            var card = new Button { Name = "Mode_" + mode, ToggleMode = true, ButtonGroup = _modeGroup, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(ModeCardWidth, 92) };
            MenuStyle.AsCard(card);
            var inside = new VBoxContainer();
            inside.AddThemeConstantOverride("separation", 2);
            inside.AddChild(Wrapped(words, 17, MenuStyle.Ink, ModeCardWidth - 28));
            inside.AddChild(Wrapped(line, 13, MenuStyle.Muted, ModeCardWidth - 28));
            Inside(card, inside, 12);
            var captured = mode;
            card.Pressed += () => { Model.ChooseMode(captured); Refresh(); };
            modes.AddChild(card);
            _modeButtons[mode] = card;
        }
        _modeDetail = new VBoxContainer { Name = "ModeDetail", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _modeDetail.AddThemeConstantOverride("separation", 4);
        modes.AddChild(_modeDetail);
        _savesLine = Wrapped("", 15, MenuStyle.Muted, 560, "SavesLine");
        _modeDetail.AddChild(_savesLine);
        _difficultyRow = new HBoxContainer { Name = "DifficultyRow" };
        _difficultyRow.AddThemeConstantOverride("separation", 8);
        _modeDetail.AddChild(_difficultyRow);
        _difficultyLabel = MenuStyle.Text("How hard:", 15, MenuStyle.Muted, "DifficultyLabel");
        MenuStyle.Unclipped(_difficultyLabel);
        _difficultyLabel.VerticalAlignment = VerticalAlignment.Center;
        _difficultyRow.AddChild(_difficultyLabel);
        foreach (var difficulty in MenuModel.Difficulties)
        {
            var chip = new Button { Name = "Difficulty_" + difficulty, Text = MenuModel.DifficultyWords(difficulty), ToggleMode = true, ButtonGroup = _difficultyGroup, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(96, 34) };
            MenuStyle.AsCard(chip, 8);
            var captured = difficulty;
            chip.Pressed += () => { Model.ChooseMode("challenge", captured); Refresh(); };
            _difficultyRow.AddChild(chip);
            _difficultyButtons[difficulty] = chip;
        }
        _copyRow = new HBoxContainer { Name = "CopyRow" };
        _copyRow.AddThemeConstantOverride("separation", 8);
        _modeDetail.AddChild(_copyRow);
        _startFresh = new Button { Name = "StartFresh", Text = "Start fresh", ToggleMode = true, ButtonGroup = _copyGroup, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(120, 34) };
        _copyIt = new Button { Name = "CopyCreative", Text = "Copy my Creative island into Challenge", ToggleMode = true, ButtonGroup = _copyGroup, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(330, 34),
            TooltipText = "Your builds, the planted and burnt ground, the journal and the map come with you. The undo steps do not, and the builds now burn and cost the wallet to repair. Once for this island." };
        MenuStyle.AsCard(_startFresh, 8);
        MenuStyle.AsCard(_copyIt, 8);
        _startFresh.Pressed += () => { Model.ChooseCopy(false); Refresh(); };
        _copyIt.Pressed += () => { Model.ChooseCopy(true); Refresh(); };
        _copyRow.AddChild(_startFresh);
        _copyRow.AddChild(_copyIt);
        _copyNote = Wrapped("", 14, MenuStyle.Muted, 560, "CopyNote");
        _modeDetail.AddChild(_copyNote);

        // The foot: what the menu has to say, and the start rule's buttons where Play was (MENU-3): "Go back" while New game's question
        // is up, Continue where its save is, "Bring my game here" where the game is on another island, and New game, last.
        _foot = new HBoxContainer { Name = "Foot" };
        _foot.AddThemeConstantOverride("separation", 14);
        column.AddChild(_foot);
        var speech = new VBoxContainer { Name = "Speech", SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        _foot.AddChild(speech);
        _notice = Wrapped("", 16, MenuStyle.Refused, 380, "Notice");
        speech.AddChild(_notice);
        _hint = Wrapped("", 15, MenuStyle.Muted, 380, "Hint");
        speech.AddChild(_hint);
        _goBack = new Button { Name = "GoBack", Text = MenuModel.GoBackWords, Visible = false, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(180, 58) };
        MenuStyle.Big(_goBack);
        _goBack.Pressed += GoBack;
        _foot.AddChild(_goBack);
        // Continue is two lines, so its word is never cut: "Continue", then the game it opens in smaller letters; it takes the foot's
        // spare width, so a team's and an island's names fit.
        _continue = new Button { Name = "Continue", Visible = false, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(430, 58), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 4 };
        MenuStyle.Big(_continue);
        _continueLines = new VBoxContainer { Name = "ContinueLines", Alignment = BoxContainer.AlignmentMode.Center };
        _continueLines.AddThemeConstantOverride("separation", -2);
        var word = MenuStyle.Unclipped(MenuStyle.Text("Continue", 20, name: "ContinueWord"));
        word.HorizontalAlignment = HorizontalAlignment.Center;
        _continueLines.AddChild(word);
        _continueLine = MenuStyle.Text("", 14, name: "ContinueLine");
        _continueLine.HorizontalAlignment = HorizontalAlignment.Center;
        _continueLines.AddChild(_continueLine);
        Inside(_continue, _continueLines, 12, 3);
        _continue.Pressed += () => Start(Model.ContinueHere());
        _foot.AddChild(_continue);
        _bring = new Button { Name = "BringGame", Text = MenuModel.BringWords, Visible = false, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(280, 58) };
        MenuStyle.Big(_bring);
        _bring.Pressed += () => Start(Model.BringHere());
        _foot.AddChild(_bring);
        _newGame = new Button { Name = "NewGame", Text = MenuModel.NewGameWords, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(180, 58) };
        MenuStyle.Big(_newGame);
        _newGameLeads = true;
        _newGame.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) _secondOfDouble = click.DoubleClick;
        };
        _newGame.Pressed += PressNewGame;
        _foot.AddChild(_newGame);

        BuildNewTeam();
        // The room and the mode this launch names (a room shortcut: a room's folder, a mode, no team) are chosen first; what that
        // leaves open is the last game's. So the menu opens on that room's Continue and New game, and opens no game by itself.
        Model.ChooseFromLaunch(LaunchArguments?.Invoke() ?? OS.GetCmdlineUserArgs());
        Model.ChooseFromLastGame();
        Refresh();
        var focus = _continue.Visible ? _continue : Model.TeamId != null && _teamButtons.TryGetValue(Model.TeamId, out var chosen) ? chosen
            : Model.Teams.Count > 0 && _teamButtons.TryGetValue(Model.Teams[0].TeamId, out var first) ? first : _newTeam;
        focus.GrabFocus();
    }

    public const int TeamCardHeight = 116, TeamCardWidth = 400, IslandCardHeight = 124, ModeCardWidth = 190;

    private static Label Section(string text)
    {
        return MenuStyle.Unclipped(MenuStyle.Text(text, 19, MenuStyle.Warm));
    }

    /// <summary>A label that wraps within this width (never squeezed to nothing in a card or a column).</summary>
    private static Label Wrapped(string text, int size, Color colour, int width, string name = "")
    {
        var label = MenuStyle.Unclipped(MenuStyle.Text(text, size, colour, name));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(width, 0);
        return label;
    }

    /// <summary>A horizontally scrolling row of cards of this height.</summary>
    private static HBoxContainer Scrolling(Container parent, string scrollName, string rowName, int height)
    {
        var scroll = new ScrollContainer { Name = scrollName, CustomMinimumSize = new Vector2(0, height + 12), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(scroll);
        var row = new HBoxContainer { Name = rowName };
        row.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(row);
        return row;
    }

    /// <summary>A card's contents, inside a margin, letting the mouse through to the card.</summary>
    private static void Inside(Button card, Control contents, int margin, int vertical = -1)
    {
        var padded = new MarginContainer();
        foreach (var side in new[] { "left", "right" }) padded.AddThemeConstantOverride("margin_" + side, margin);
        foreach (var side in new[] { "top", "bottom" }) padded.AddThemeConstantOverride("margin_" + side, vertical >= 0 ? vertical : margin);
        padded.AddChild(contents);
        card.AddChild(padded);
        padded.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MenuStyle.IgnoreMouse(padded);
    }

    // ---- drawing the model ----

    /// <summary>Every row as the model says it is now: the cards chosen, what the mode needs, and the start rule's buttons.</summary>
    public void Refresh()
    {
        _offer = Model.Offer;
        RefreshTeams();
        RefreshIslands();
        RefreshMode();
        RefreshFoot();
    }

    private void RefreshTeams()
    {
        var signature = string.Join("|", Model.Teams.Select(t => $"{t.TeamId}:{(t.File == null ? t.Problem : System.Text.Encoding.UTF8.GetString(t.File.Serialize()))}:{t.IslandsVisited}:{t.ExpertiseCount}:{t.LastMode}:{t.Hearts}:{t.StaminaM}:{t.Pips}"));
        if (signature != _teamSignature)
        {
            _teamSignature = signature;
            foreach (var child in _teamRow.GetChildren()) { _teamRow.RemoveChild(child); child.QueueFree(); }
            _teamButtons.Clear();
            foreach (var card in Model.Teams) AddTeamCard(card);
            _newTeam = new Button { Name = "NewTeam", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(180, TeamCardHeight) };
            MenuStyle.AsCard(_newTeam);
            var plus = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            var sign = MenuStyle.Text("+", 44, MenuMuted());
            sign.HorizontalAlignment = HorizontalAlignment.Center;
            plus.AddChild(sign);
            var label = MenuStyle.Text("New team", 18);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            plus.AddChild(label);
            Inside(_newTeam, plus, 10);
            _newTeam.Pressed += OpenNewTeam;
            _teamRow.AddChild(_newTeam);
        }
        foreach (var (id, button) in _teamButtons) button.SetPressedNoSignal(id == Model.TeamId);
        RefreshKnackSwap();
    }

    private string? _teamSignature;
    private static Color MenuMuted() => MenuStyle.Muted;

    private void AddTeamCard(TeamCard card)
    {
        var button = new Button { Name = "Team_" + card.TeamId, ToggleMode = true, ButtonGroup = _teamGroup, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(TeamCardWidth, TeamCardHeight), Disabled = !card.Readable };
        MenuStyle.AsCard(button);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var avatar = new TextureRect { Name = "Avatar", CustomMinimumSize = new Vector2(76, 76), SizeFlagsVertical = SizeFlags.ShrinkCenter, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Texture = AvatarOf(card.File?.Costume ?? TeamFile.PrototypeCostume) };
        row.AddChild(avatar);
        var lines = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lines.AddThemeConstantOverride("separation", 1);
        row.AddChild(lines);
        if (!card.Readable)
        {
            lines.AddChild(MenuStyle.Text(card.TeamId, 19, name: "TeamName"));
            var problem = MenuStyle.Text(card.Problem.Length > 0 ? card.Problem : "This team's file could not be read.", 13, MenuStyle.Refused, "TeamProblem");
            problem.ClipText = false;
            problem.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            lines.AddChild(problem);
        }
        else
        {
            var team = card.File!;
            lines.AddChild(MenuStyle.Text(team.DisplayName, 20, name: "TeamName"));
            lines.AddChild(MenuStyle.Text(StatsOf(card), 13, MenuStyle.Muted, "TeamStats"));
            lines.AddChild(MenuStyle.Text(card.IslandsVisited == 1 ? "1 island visited" : $"{card.IslandsVisited} islands visited", 13, MenuStyle.Muted, "TeamIslands"));
            var gubble = new HBoxContainer { Name = "TeamGubble" };
            gubble.AddThemeConstantOverride("separation", 6);
            var dot = new ColorRect { CustomMinimumSize = new Vector2(12, 12), Color = new Color(team.GubbleColour), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            gubble.AddChild(dot);
            gubble.AddChild(MenuStyle.Text($"{team.GubbleName} · {MenuWords.Counted(Rules, "expertise", card.ExpertiseCount)}", 13, name: "TeamGubbleLine"));
            lines.AddChild(gubble);
            var knacks = team.Knacks.Count == 0 ? "no knack yet" : string.Join(", ", team.Knacks.Select(k => TeamMaker.KnackName(Rules, k.Knack)));
            lines.AddChild(MenuStyle.Text(knacks + " · " + Trust.PlayerWords(team.Trust, team.GubbleName), 12, MenuStyle.Muted, "TeamKnackTrust"));
        }
        Inside(button, row, 12, 8);
        var id = card.TeamId;
        button.Pressed += () => { Model.SelectTeam(id); Refresh(); };
        _teamRow.AddChild(button);
        _teamButtons[id] = button;
    }

    /// <summary>"Hearts 5 · Stamina 0.4 m · Pips 8", in the island's words for them.</summary>
    private string StatsOf(TeamCard card) =>
        string.Create(CultureInfo.InvariantCulture, $"{MenuWords.Name(Rules, "hearts")} {card.Hearts} · {MenuWords.Name(Rules, "stamina")} {card.StaminaM:0.0#} m · {MenuWords.Name(Rules, "pips")} {card.Pips}");

    private Texture2D AvatarOf(string costume)
    {
        var choice = _characters.FirstOrDefault(c => c.Id == costume);
        if (choice?.Thumbnail != null) return choice.Thumbnail;
        _swatches ??= new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        var key = costume + "|" + MenuProfile.PlayerColour();
        if (!_swatches.TryGetValue(key, out var swatch))
            _swatches[key] = swatch = CharacterChoice.Swatch(costume == TeamFile.PrototypeCostume ? CharacterChoice.PrototypeId : costume, MenuProfile.Palette[MenuProfile.PlayerColour()]);
        return swatch;
    }

    private Dictionary<string, Texture2D>? _swatches;

    private void RefreshIslands()
    {
        var signature = string.Join("|", Model.Islands.Select(i => i.RoomId + i.ManifestPrefix)) + "/" + Model.TeamId + "/" + SavesSignature();
        if (signature != _islandSignature)
        {
            _islandSignature = signature;
            foreach (var child in _islandRow.GetChildren()) { _islandRow.RemoveChild(child); child.QueueFree(); }
            _islandButtons.Clear();
            _thumbnails.Clear();
            foreach (var island in Model.Islands) AddIslandCard(island);
            if (Model.Islands.Count == 0)
                _islandRow.AddChild(MenuStyle.Text("No island is here yet. Install one (docs/NATIVE-BUILD.md) and open the menu again.", 16, MenuStyle.Refused, "NoIslands"));
        }
        foreach (var (id, button) in _islandButtons) button.SetPressedNoSignal(id == Model.RoomId);
    }

    private string? _islandSignature;

    private string SavesSignature() =>
        Model.Team is { Readable: true } team ? string.Join(",", Model.Islands.SelectMany(i => IslandSaveInfo.Of(Model.Layout, team.TeamId, i)).Select(s => $"{s.Mode}{s.Exists}{s.WrittenUtc:O}{s.Difficulty}")) : "";

    private void AddIslandCard(IslandEntry island)
    {
        var button = new Button { Name = "Island_" + island.RoomId, ToggleMode = true, ButtonGroup = _islandGroup, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(270, IslandCardHeight) };
        MenuStyle.AsCard(button);
        var lines = new VBoxContainer();
        lines.AddThemeConstantOverride("separation", 2);
        var picture = new IslandThumbnail { Name = "Thumbnail", CustomMinimumSize = new Vector2(246, 64) };
        picture.SetShape(island.ReadShape());
        lines.AddChild(picture);
        var name = new HBoxContainer();
        var title = MenuStyle.Text(island.DisplayName, 17, name: "IslandName");
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.AddChild(title);
        if (island.Tutorial) name.AddChild(MenuStyle.Text("tutorial", 13, MenuStyle.Warm, "TutorialTag"));
        lines.AddChild(name);
        lines.AddChild(MenuStyle.Text(SavesWords(island), 13, MenuStyle.Muted, "IslandSaves"));
        Inside(button, lines, 10, 7);
        var id = island.RoomId;
        button.Pressed += () => { Model.SelectIsland(id); Refresh(); };
        _islandRow.AddChild(button);
        _islandButtons[id] = button;
        _thumbnails[id] = picture;
    }

    /// <summary>This team's saves on an island, by mode: "Creative 3 Oct · Challenge (Gentle) 4 Oct", or "no saves yet" (or the island alone before a team is chosen).</summary>
    private string SavesWords(IslandEntry island)
    {
        if (Model.Team is not { Readable: true } team) return island.Shipped ? "a stand-in room" : "choose a team to see its saves";
        var saves = IslandSaveInfo.Of(Model.Layout, team.TeamId, island).Where(s => s.Exists).ToList();
        if (saves.Count == 0) return "no saves yet";
        return string.Join(" · ", saves.Select(s => MenuModel.ModeWords(s.Mode) + (s.Difficulty != null ? $" ({MenuModel.DifficultyWords(s.Difficulty)})" : "") + " " + Day(s.WrittenUtc)));
    }

    private static string Day(DateTime? utc) => utc == null ? "" : utc.Value.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);

    private void RefreshMode()
    {
        foreach (var (mode, button) in _modeButtons)
        {
            button.SetPressedNoSignal(Model.Mode == mode);
            // Live is shown, and dim while the island rules refuse it; a click still answers why.
            button.Modulate = Model.Offers(mode) ? Colors.White : new Color(1, 1, 1, 0.55f);
            button.TooltipText = Model.Offers(mode) ? "" : mode == "live" ? CommandHost.LiveNotYet : MenuModel.ModeWords(mode) + " is not on offer on this island.";
        }
        var challenge = Model.Mode == "challenge";
        _difficultyRow.Visible = challenge;
        if (challenge)
        {
            // While New game's question is up the save's difficulty is only the one offered first: the new game may take another.
            var kept = Model.Asking ? null : Model.FixedDifficulty;
            _difficultyLabel.Text = kept != null ? "This save plays at:" : Model.Asking ? "How hard, this time:" : "How hard:";
            foreach (var (difficulty, chip) in _difficultyButtons)
            {
                var offered = Model.Offers("challenge", difficulty) && (kept == null || kept == difficulty);
                chip.Visible = kept == null ? Model.Offers("challenge", difficulty) : kept == difficulty;
                chip.Disabled = !offered;
                chip.SetPressedNoSignal(Model.Difficulty == difficulty);
                chip.Text = MenuModel.DifficultyWords(difficulty) + (kept == null && Model.FirstChallenge && difficulty == "gentle" ? " (suggested)" : "");
                chip.TooltipText = difficulty switch { "gentle" => "Every hurt is halved; none is taken away.", "brave" => "Every hurt is a half again as much.", _ => "The game as designed." };
            }
        }
        _savesLine.Text = SavesLineWords();
        var copyOffered = Model.CopyOffered;
        _copyRow.Visible = copyOffered;
        _startFresh.SetPressedNoSignal(copyOffered && !Model.CopyCreative);
        _copyIt.SetPressedNoSignal(copyOffered && Model.CopyCreative);
        _copyNote.Text = copyOffered ? (Model.CopyCreative ? "Builds, ground, journal and map come along; undo does not, and the builds count for no Challenge first or souvenir. Once per island." : "")
            : challenge && Model.Team is { Readable: true } && Model.Island is { } island && Model.SaveOf("creative") is { Exists: true } && Model.SaveOf("challenge") is { Exists: false } && Model.CopyUsed
                ? "Your Creative island here was copied into Challenge once already; this one starts fresh." : "";
        _copyNote.Visible = _copyNote.Text.Length > 0;
    }

    private string SavesLineWords()
    {
        if (Model.Team is not { Readable: true } || Model.Island is not { } island) return "Choose a character and an island; then the mode.";
        if (Model.Mode == null) return $"{island.DisplayName}: choose Creative or Challenge.";
        if (Model.Mode == "live") return "";
        var save = Model.SaveOf(Model.Mode);
        var name = MenuModel.ModeWords(Model.Mode);
        return save is { Exists: true } ? $"Your {name} island here, saved {Day(save.WrittenUtc)}: Continue opens it. A save keeps its mode."
            // The line agrees with the hint: on a rebuilt island there is a save, of the island as it was.
            : _offer.Case == StartCase.Rebuilt ? $"Your {name} island here is from before this room was rebuilt: New game makes a new one."
            : _offer.Bring ? $"No {name} island here yet: bring your game here, or start a new one."
            // No game in play, and one put away (New game's own doing, also when the room then did not open): the page says where it went.
            : Model.GamePutAway ? $"Your last {name} game is put away: New game makes a new one."
            : $"No {name} island here yet: New game makes one.";
    }

    private void RefreshFoot()
    {
        _notice.Text = Model.Notice;
        _notice.Visible = Model.Notice.Length > 0;
        var why = Model.WhyNotReady;
        var offer = _offer;
        var hold = why == null ? Model.Hold() : TeamHold.Free;
        _hold = hold;
        _waiting = hold == TeamHold.Leaving;
        var asking = Model.Asking;
        // While the question is up, the button that answers it says so, and the question is in a child's size.
        _newGame.Text = asking ? MenuModel.YesNewGameWords : MenuModel.NewGameWords;
        _hint.AddThemeFontSizeOverride("font_size", asking ? 18 : 15);
        _goBack.Visible = asking;
        _continue.Visible = offer.Continue && !asking;
        _bring.Visible = offer.Bring && !asking;
        _continue.Disabled = _waiting;
        _bring.Disabled = _waiting;
        _newGame.Disabled = why != null || _waiting;
        if (offer.Continue)
        {
            _continueLine.Text = Model.ChoiceWords ?? "";
            _continue.TooltipText = "Continue: " + _continueLine.Text;
        }
        _continueLines.Modulate = _waiting ? new Color(1, 1, 1, 0.55f) : Colors.White;
        // New game leads (the warm fill) where it stands alone; beside Continue, "Bring my game here" or "Go back" it is the quiet one.
        var leads = !offer.Continue && !offer.Bring && !asking;
        if (leads != _newGameLeads)
        {
            _newGameLeads = leads;
            if (leads) MenuStyle.Big(_newGame);
            else
            {
                MenuStyle.AsCard(_newGame, 14);
                _newGame.AddThemeFontSizeOverride("font_size", 22);
            }
        }
        _hint.Text = why ?? (asking ? Model.Question : _waiting ? MenuModel.StillClosingWords : offer.Case == StartCase.Rebuilt ? MenuModel.RebuiltWords
            : hold == TeamHold.StillOpen ? MenuModel.OpenElsewhereWords : Model.ChoiceWords ?? "");
        _hint.AddThemeColorOverride("font_color", asking ? MenuStyle.Ink : MenuStyle.Muted);
    }

    private void Start(MenuLaunch? launch)
    {
        Refresh();
        if (launch == null) return;
        Launch?.Invoke(launch);
    }

    /// <summary>
    /// New game: over a game the first press puts the question up and gives "Go back" the focus; the next press, not sooner than
    /// ConfirmGuardMs after it and never the second click of a double click, puts the old game away and starts. Where there is
    /// no game it starts at once.
    /// </summary>
    private void PressNewGame()
    {
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

    /// <summary>"Go back": the question goes, nothing was moved, and the focus returns to the button that leads.</summary>
    private void GoBack()
    {
        Model.GoBack();
        Refresh();
        (_continue.Visible ? _continue : _bring.Visible ? _bring : _newGame).GrabFocus();
    }

    /// <summary>
    /// While the team is held (a room that is leaving, or after the wait a game still open), its lease is asked again ten times
    /// a second: the buttons open as soon as a leaving room has let go, and the page stops saying a game is open as soon as it
    /// is closed. Nothing is asked while nobody holds the team.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_hold == TeamHold.Free) return;
        _sinceAsked += delta;
        if (_sinceAsked < 0.1) return;
        _sinceAsked = 0;
        var hold = Model.Hold();
        if (hold == _hold) return;
        // The hold changed: the words that were about it go with it (the wait's, and once the game is closed, the refusal's).
        if (Model.Notice == MenuModel.StillClosingWords || (hold == TeamHold.Free && Model.Notice == PlayLease.StillOpen)) Model.SetNotice("");
        Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key && key.Keycode == Key.Escape && _newTeamOpen)
        {
            CloseNewTeam();
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false } escape && escape.Keycode == Key.Escape && Model.Asking)
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

    /// <summary>The full-screen key works in the menu as in the room, and remembers the choice for the next launch.</summary>
    private void ToggleFullscreen()
    {
        if (DisplayServer.GetName() == "headless") return;
        var full = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
        DisplayServer.WindowSetMode(full ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        MenuProfile.Set(RoomHud.FullscreenProfileKey, !full);
    }

    // ---- the knack of the chosen team: swapped here, for free ----

    private void RefreshKnackSwap()
    {
        var team = Model.Team?.File;
        _knackRow.Visible = team != null;
        if (team == null) return;
        var offered = TeamMaker.Offered(Rules);
        _knackSwap.Clear();
        _knackSwap.AddItem(team.Knacks.Count == 0 ? "Choose a knack" : "Change my knack");
        _knackSwap.SetItemDisabled(0, true);
        foreach (var knack in offered) _knackSwap.AddItem(KnackLine(knack));
        var held = team.Knacks.FirstOrDefault(k => k.Slot == 1);
        _knackNote.Text = held == null ? "Knack: none yet" : $"Knack: {TeamMaker.KnackName(Rules, held.Knack)}" + (held.Rank > 1 ? $" (rank {held.Rank})" : "");
        _knackSwap.Select(0);
        _knackSwapIds = offered.Select(k => k.Knack).ToList();
    }

    private IReadOnlyList<string> _knackSwapIds = Array.Empty<string>();

    private void OnKnackSwapped(long index)
    {
        if (index < 1 || index > _knackSwapIds.Count || Model.TeamId == null) return;
        try
        {
            TeamMaker.SwapKnack(Model.Layout, Rules, Model.TeamId, 1, _knackSwapIds[(int)index - 1]);
            Model.Reload();
        }
        catch (TeamFileException error) { Model.SetNotice(error.Message); }
        Refresh();
    }

    /// <summary>One line for a knack: its name and what it does at rank 1.</summary>
    public string KnackLine(KnackRule knack) => $"{TeamMaker.KnackName(Rules, knack.Knack)}: {KnackWords.Describe(knack)}";
}
