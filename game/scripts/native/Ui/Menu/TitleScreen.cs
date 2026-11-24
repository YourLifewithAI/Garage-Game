using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GarageGame.Native.Look;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>
/// The hour and the moon the title page shows, and the few words it uses: pure, so a test can pin them. The hour and day come from
/// the machine's clock, or from the look's own pins (--look-clock=HH:MM, --look-date=YYYY-MM-DD: LookClock.ClockOverride), so the
/// preview clock of run-room.ps1 -Hour and -Date moves this page too. The moon is the mean lunation from the new moon of 6 January
/// 2000 (about half a day out at worst), enough to say which way the moon leans.
/// </summary>
public static class TitleSky
{
    public const double Lunation = 29.530588853;
    private static readonly DateTime NewMoon = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

    public sealed record Reading(float Hour, DateTime Date, bool Pinned);

    /// <summary>The hour and the day the page shows: the look's pins where given, else the machine's local time.</summary>
    public static Reading Read(IReadOnlyList<string> arguments, Func<string, string?> environment, DateTime localNow)
    {
        var (hour, day, _) = LookClock.ClockOverride(arguments, environment);
        var date = localNow.Date;
        if (day is { } d) date = new DateTime(localNow.Year, 1, 1).AddDays(d - 1);
        return new Reading(hour ?? (float)localNow.TimeOfDay.TotalHours, date, hour != null || day != null);
    }

    /// <summary>One word for the part of the day.</summary>
    public static string DayPart(float hour) => hour switch { < 5f => "night", < 7f => "dawn", < 11f => "morning", < 13f => "noon", < 17f => "afternoon", < 19f => "dusk", < 21f => "evening", _ => "night" };

    /// <summary>"9:05".</summary>
    public static string Clock(float hour)
    {
        var minutes = (int)MathF.Floor(Math.Clamp(hour, 0f, 23.99f) * 60f + 0.01f);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes / 60}:{minutes % 60:00}");
    }

    public static bool IsDay(float hour) => hour is >= 6f and < 18f;

    /// <summary>"Tuesday 24 November".</summary>
    public static string DateWords(DateTime date) => date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

    /// <summary>Where the moon is in its month: 0 new, 0.5 full, back to 1 new.</summary>
    public static float MoonPhase(DateTime date, float hour)
    {
        var instant = DateTime.SpecifyKind(date.Date.AddHours(hour), DateTimeKind.Utc);
        var age = (instant - NewMoon).TotalDays % Lunation;
        if (age < 0) age += Lunation;
        return (float)(age / Lunation);
    }

    /// <summary>The share of the moon's face that is lit.</summary>
    public static float MoonLitShare(float phase) => (1f - MathF.Cos(MathF.Tau * phase)) / 2f;

    private static readonly string[] MoonNames =
    {
        "new moon", "thin moon growing", "half moon growing", "fat moon growing", "full moon", "fat moon shrinking", "half moon shrinking", "thin moon shrinking",
    };

    public static string MoonName(float phase) => MoonNames[(int)MathF.Floor(phase * 8f + 0.5f) % 8];

    /// <summary>The lit part of the moon's face as a polygon around the origin: its right side while it grows, its left while it shrinks. Empty at the new moon.</summary>
    public static Vector2[] MoonLit(float phase, float radius, int steps = 24)
    {
        var share = MoonLitShare(phase);
        if (share < 0.02f) return Array.Empty<Vector2>();
        var terminator = MathF.Cos(MathF.Tau * phase);
        var points = new List<Vector2>();
        for (var i = 0; i <= steps; i++)
        {
            var t = -MathF.PI / 2 + MathF.PI * i / steps;
            points.Add(new Vector2(radius * MathF.Cos(t), radius * MathF.Sin(t)));
        }
        for (var i = steps; i >= 0; i--)
        {
            var t = -MathF.PI / 2 + MathF.PI * i / steps;
            points.Add(new Vector2(terminator * radius * MathF.Cos(t), radius * MathF.Sin(t)));
        }
        if (phase >= 0.5f) for (var i = 0; i < points.Count; i++) points[i] = new Vector2(-points[i].X, points[i].Y);
        return points.ToArray();
    }

    private static readonly (float Hour, Color Sky, Color Wash)[] Keys =
    {
        (0f, new Color("2d2d4a"), new Color(0.07f, 0.09f, 0.26f, 0.22f)),
        (5f, new Color("2d2d4a"), new Color(0.07f, 0.09f, 0.26f, 0.22f)),
        (6.5f, new Color("e0a070"), new Color(0.95f, 0.55f, 0.45f, 0.13f)),
        (9f, new Color("a8cfd2"), new Color(1f, 0.96f, 0.78f, 0.04f)),
        (16f, new Color("a8cfd2"), new Color(1f, 0.93f, 0.7f, 0.05f)),
        (18f, new Color("e08a5a"), new Color(0.92f, 0.45f, 0.2f, 0.15f)),
        (20.5f, new Color("5b4a78"), new Color(0.3f, 0.2f, 0.42f, 0.18f)),
        (22f, new Color("2d2d4a"), new Color(0.07f, 0.09f, 0.26f, 0.22f)),
        (24f, new Color("2d2d4a"), new Color(0.07f, 0.09f, 0.26f, 0.22f)),
    };

    private static (Color Sky, Color Wash) Between(float hour)
    {
        hour = Math.Clamp(hour, 0f, 24f);
        for (var i = 1; i < Keys.Length; i++)
        {
            if (hour > Keys[i].Hour) continue;
            var (a, b) = (Keys[i - 1], Keys[i]);
            var t = b.Hour > a.Hour ? (hour - a.Hour) / (b.Hour - a.Hour) : 0f;
            return (a.Sky.Lerp(b.Sky, t), a.Wash.Lerp(b.Wash, t));
        }
        return (Keys[^1].Sky, Keys[^1].Wash);
    }

    /// <summary>The sky's colour at an hour (the little window on the page).</summary>
    public static Color SkyColour(float hour) => Between(hour).Sky;

    /// <summary>The thin wash of the hour's light laid over the paper: blue at night, rose at dawn, amber at dusk, nearly clear by day.</summary>
    public static Color Wash(float hour) => Between(hour).Wash;

    /// <summary>The word "island" is the exporter's and the files', not the player's: on the page a room's land is a land.</summary>
    public static string Plain(string words) =>
        Regex.Replace(words ?? "", "island", match => char.IsUpper(match.Value[0]) ? "Land" : "land", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>A small window of sky at the head of the page: the sun climbing over a hill by day, the moon with its true phase and a few stars by night.</summary>
public partial class HourDial : Control
{
    public float Hour { get; set; } = 12f;
    public float Phase { get; set; }
    /// <summary>"sun" by day, "moon" by night (the page's test reads it).</summary>
    public string Body => TitleSky.IsDay(Hour) ? "sun" : "moon";

    public HourDial()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(150, 68);
    }

    public override void _Draw()
    {
        var (w, h) = (Size.X, Size.Y);
        if (w < 24 || h < 24) return;
        DrawRect(new Rect2(0, 0, w, h), TitleSky.SkyColour(Hour));
        var night = !TitleSky.IsDay(Hour);
        if (night)
            foreach (var (sx, sy) in new[] { (0.12f, 0.2f), (0.3f, 0.12f), (0.46f, 0.3f), (0.62f, 0.14f), (0.78f, 0.28f), (0.9f, 0.12f), (0.2f, 0.45f), (0.7f, 0.5f) })
                DrawCircle(new Vector2(w * sx, h * sy), 1.2f, new Color(MenuTheme.Cream, 0.8f));
        var ground = h * 0.8f;
        var t = Math.Clamp(night ? (Hour - 18f + 24f) % 24f / 12f : (Hour - 6f) / 12f, 0f, 1f);
        var at = new Vector2(16 + t * (w - 32), ground - MathF.Sin(MathF.PI * t) * (ground - 16));
        if (!night)
        {
            DrawCircle(at, 8.5f, new Color("e8b830"));
            for (var ray = 0; ray < 8; ray++)
            {
                var direction = Vector2.Right.Rotated(ray * MathF.PI / 4);
                DrawLine(at + direction * 11.5f, at + direction * 16f, new Color("e8b830"), 2f);
            }
        }
        else
        {
            const float radius = 9f;
            DrawCircle(at, radius, new Color(MenuTheme.Cream, 0.2f));
            var lit = TitleSky.MoonLit(Phase, radius).Select(p => p + at).ToArray();
            if (lit.Length >= 3 && Geometry2D.TriangulatePolygon(lit).Length >= 3) DrawColoredPolygon(lit, MenuTheme.Cream);
        }
        var hill = new List<Vector2> { new(0, h) };
        for (var x = 0f; x <= w; x += 6) hill.Add(new Vector2(x, ground + MathF.Sin(x * 0.07f) * 3f - 1f));
        hill.Add(new Vector2(w, ground - 1f));
        hill.Add(new Vector2(w, h));
        DrawColoredPolygon(hill.ToArray(), night ? new Color("3a2c20") : MenuTheme.InkSoft);
        DrawRect(new Rect2(1, 1, w - 2, h - 2), MenuTheme.Ink, false, 2f);
    }
}

/// <summary>The map's frame: a pencilled mat with ticks at its corners. The land's picture sits inside it.</summary>
public partial class MapMat : Control
{
    public MapMat()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(360, 280);
    }

    public override void _Draw()
    {
        var (w, h) = (Size.X, Size.Y);
        if (w < 40 || h < 40) return;
        DrawRect(new Rect2(3, 5, w - 3, h - 5), new Color(0.12f, 0.07f, 0.02f, 0.25f));
        DrawRect(new Rect2(0, 0, w - 3, h - 5), MenuTheme.PaperLit);
        DrawRect(new Rect2(0, 0, w - 3, h - 5), MenuTheme.Ink, false, 2f);
        DrawRect(new Rect2(6, 6, w - 15, h - 17), MenuTheme.Sepia, false, 1f);
        // Ticks at the sides, like a surveyor's frame.
        for (var x = 24f; x < w - 24; x += 24) { DrawLine(new Vector2(x, 0), new Vector2(x, 5), MenuTheme.Ink, 1f); DrawLine(new Vector2(x, h - 5), new Vector2(x, h - 10), MenuTheme.Ink, 1f); }
    }
}

/// <summary>A compass mark: an arrow with N, north at the top of the map.</summary>
public partial class NorthMark : Control
{
    public NorthMark()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(30, 34);
    }

    public override void _Draw()
    {
        var c = new Vector2(Size.X / 2, Size.Y * 0.62f);
        DrawColoredPolygon(new[] { c + new Vector2(0, -15), c + new Vector2(6, 7), c + new Vector2(0, 3), c + new Vector2(-6, 7) }, MenuTheme.Ink);
        DrawString(MenuTheme.Hand, new Vector2(c.X - 5, 10), "N", HorizontalAlignment.Left, -1, 12, MenuTheme.Ink);
    }
}

/// <summary>
/// The title page (Run 4, MENU-4): a naturalist's journal lying open on the table, drawn in code. On its left page the room as land,
/// seen from above, with its name (the land's picture, MENU-5); on its right page the head of a field journal (the date, the part of
/// the day, the moon), the game's name, and the start rule's buttons: <b>Continue</b>, two lines and never cut, only where its save
/// is; <b>Bring my game here</b>; <b>New game</b>, which over a game asks once; and small "Choose" and "Quit". The rule itself is
/// MenuModel's (MENU-3), the same one the Choose page (MainMenu) keeps; nothing here moves a file. "Choose" opens the Choose page
/// (MenuBoot swaps them). The word "island" is nowhere on this page (TitleSky.Plain). A thin wash of the hour's light lies over the
/// paper (HourWash; it can be switched off).
/// </summary>
public partial class TitleScreen : Control
{
    public MenuModel Model { get; set; } = null!;
    /// <summary>Told the game to start (Continue, "Bring my game here" or New game).</summary>
    public Action<MenuLaunch>? Launch { get; set; }
    /// <summary>The Choose page was asked for (the small "Choose", or New game before a team or a land is chosen).</summary>
    public Action? Choose { get; set; }
    public Action? QuitGame { get; set; }
    /// <summary>The user arguments this launch was started with (null: MainMenu.LaunchArguments, else the process's).</summary>
    public Func<IReadOnlyList<string>>? LaunchArguments { get; set; }
    /// <summary>The machine's local time (a test names its own).</summary>
    public Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;
    /// <summary>The thin colour of the hour over the paper; off, the paper is plain.</summary>
    public bool HourWash { get; set; } = true;
    /// <summary>For this long after New game's question goes up, New game pressed again does nothing (a double click is one press, as on the Choose page).</summary>
    public int ConfirmGuardMs { get; set; } = 700;

    public const int ButtonWidth = 520, ButtonHeight = 120, QuietButtonHeight = 100;

    private IReadOnlyList<string> _arguments = Array.Empty<string>();
    private Button _continue = null!, _bring = null!, _newGame = null!, _goBack = null!, _choose = null!, _quit = null!;
    private Label _continueWord = null!, _continueLine = null!, _notice = null!, _hint = null!, _landName = null!, _caption = null!;
    private Label _date = null!, _hour = null!, _title = null!, _tagline = null!;
    private VBoxContainer _continueLines = null!;
    private HourDial _dial = null!;
    private ColorRect _wash = null!;
    private IslandThumbnail _picture = null!;
    private Control _leftPage = null!, _rightPage = null!;
    private StartOffer _offer = StartOffer.None;
    private TeamHold _hold = TeamHold.Free;
    private double _sinceAsked, _sinceSky;
    private ulong _questionAt;
    private bool _secondOfDouble, _newGameLeads = true;
    private string? _pictureOf;

    public Button ContinueButton => _continue;
    public Label ContinueWord => _continueWord;
    /// <summary>Continue's second line: the game it opens, wrapped (never trimmed).</summary>
    public Label ContinueLine => _continueLine;
    public Button BringButton => _bring;
    public Button NewGameButton => _newGame;
    public Button GoBackButton => _goBack;
    public Button ChooseButton => _choose;
    public Button QuitButton => _quit;
    public Label NoticeLabel => _notice;
    public Label HintLabel => _hint;
    public Label LandName => _landName;
    public Label DateLine => _date;
    public Label HourLine => _hour;
    public Label TitleLabel => _title;
    public HourDial Dial => _dial;
    public ColorRect Wash => _wash;
    public IslandThumbnail Picture => _picture;
    /// <summary>The left page's contents and the right's, for the test that the land's picture is on the left.</summary>
    public Control LeftPage => _leftPage;
    public Control RightPage => _rightPage;
    public TitleSky.Reading Sky { get; private set; } = new(12f, DateTime.Today, false);

    public override void _Ready()
    {
        Name = "TitleScreen";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _arguments = LaunchArguments?.Invoke() ?? MainMenu.LaunchArguments?.Invoke() ?? OS.GetCmdlineUserArgs();
        var theme = new Theme { DefaultFontSize = 18, DefaultFont = MenuTheme.Book };
        theme.SetColor("font_color", "Label", MenuTheme.Ink);
        Theme = theme;
        // What this launch names is chosen first, then the last game's; what is still open gets a plain default, so the buttons are ready.
        Model.ChooseFromLaunch(_arguments);
        Model.ChooseFromLastGame();
        Settle();

        // A dark table, the book, the hour's wash, and the page's words.
        var table = new ColorRect { Name = "Table", Color = new Color("2a1d14"), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(table);
        table.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var book = new JournalPaper { Name = "Book" };
        AddChild(book);
        book.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);
        _wash = new ColorRect { Name = "HourWash", Color = new Color(0, 0, 0, 0), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_wash);
        _wash.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);

        var margin = new MarginContainer { Name = "Margin" };
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 12);
        AddChild(margin);
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var pages = new HBoxContainer { Name = "Pages" };
        pages.AddThemeConstantOverride("separation", 0);
        margin.AddChild(pages);
        _leftPage = Page("LeftPage", JournalPaper.SpineShare, 46, 30);
        _rightPage = Page("RightPage", 1f - JournalPaper.SpineShare, 38, 46);
        pages.AddChild(_leftPage);
        pages.AddChild(_rightPage);
        BuildLeft((VBoxContainer)_leftPage.GetChild(0));
        BuildRight((VBoxContainer)_rightPage.GetChild(0));

        Refresh();
        UpdateSky();
        (_continue.Visible ? _continue : _newGame).GrabFocus();
    }

    private static MarginContainer Page(string name, float share, int outer, int gutter)
    {
        var page = new MarginContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = share };
        // The left page's outer edge is on the left; the right page's on the right.
        var (left, right) = name == "LeftPage" ? (outer, gutter) : (gutter, outer);
        page.AddThemeConstantOverride("margin_left", left);
        page.AddThemeConstantOverride("margin_right", right);
        page.AddThemeConstantOverride("margin_top", 34);
        page.AddThemeConstantOverride("margin_bottom", 34);
        var column = new VBoxContainer { Name = "Column", SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);
        page.AddChild(column);
        return page;
    }

    // ---- the left page: the land ----

    private void BuildLeft(VBoxContainer column)
    {
        _landName = MenuTheme.Text("", MenuTheme.Hand, 30, MenuTheme.Ink, "LandName", wrap: true, width: 300);
        column.AddChild(_landName);
        var kind = MenuTheme.Text("The room, as the land it has become.", MenuTheme.Lean, 18, MenuTheme.Sepia, "LandKind", wrap: true, width: 300);
        column.AddChild(kind);
        var mat = new MapMat { Name = "MapMat", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddChild(mat);
        _picture = new IslandThumbnail { Name = "Picture", CustomMinimumSize = new Vector2(200, 160) };
        mat.AddChild(_picture);
        _picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _picture.OffsetLeft = 12; _picture.OffsetTop = 12; _picture.OffsetRight = -15; _picture.OffsetBottom = -17;
        var row = new HBoxContainer { Name = "CaptionRow" };
        column.AddChild(row);
        _caption = MenuTheme.Text("Seen from above. North is at the top.", MenuTheme.Lean, 17, MenuTheme.Sepia, "Caption", wrap: true, width: 200);
        _caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(_caption);
        row.AddChild(new NorthMark { Name = "North" });
    }

    // ---- the right page: the head, the name, the buttons ----

    private void BuildRight(VBoxContainer column)
    {
        var head = new HBoxContainer { Name = "Head" };
        head.AddThemeConstantOverride("separation", 14);
        column.AddChild(head);
        _dial = new HourDial { Name = "Dial" };
        head.AddChild(_dial);
        var lines = new VBoxContainer { Name = "HeadLines", SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        lines.AddThemeConstantOverride("separation", 0);
        head.AddChild(lines);
        lines.AddChild(MenuTheme.Text("Field journal", MenuTheme.Hand, 17, MenuTheme.Sepia, "JournalLabel"));
        _date = MenuTheme.Text("", MenuTheme.Book, 22, MenuTheme.Ink, "DateLine", wrap: true, width: 200);
        lines.AddChild(_date);
        _hour = MenuTheme.Text("", MenuTheme.Lean, 18, MenuTheme.InkSoft, "HourLine", wrap: true, width: 200);
        lines.AddChild(_hour);

        _title = MenuTheme.Text("Garage Game", MenuTheme.Hand, 48, MenuTheme.Ink, "Title", wrap: true, width: 300);
        column.AddChild(_title);
        _tagline = MenuTheme.Text("A room, grown into a land.", MenuTheme.Lean, 20, MenuTheme.InkSoft, "Tagline", wrap: true, width: 300);
        column.AddChild(_tagline);
        column.AddChild(new Control { Name = "Gap", SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });

        var buttons = new VBoxContainer { Name = "Buttons" };
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);
        _goBack = Make("GoBack", MenuModel.GoBackWords, ButtonHeight);
        MenuTheme.AsRibbon(_goBack);
        _goBack.Visible = false;
        _goBack.Pressed += GoBack;
        buttons.AddChild(_goBack);
        // Continue is two lines, and neither is cut: the word, and under it the game it opens, wrapped; the button grows if a long name needs it.
        _continue = Make("Continue", "", ButtonHeight);
        MenuTheme.AsRibbon(_continue);
        _continue.Visible = false;
        _continueLines = new VBoxContainer { Name = "ContinueLines", Alignment = BoxContainer.AlignmentMode.Center };
        _continueLines.AddThemeConstantOverride("separation", 0);
        _continueWord = MenuTheme.Text("Continue", MenuTheme.Hand, 30, MenuTheme.Cream, "ContinueWord");
        _continueWord.HorizontalAlignment = HorizontalAlignment.Center;
        _continueLines.AddChild(_continueWord);
        _continueLine = MenuTheme.Text("", MenuTheme.Book, 18, MenuTheme.Cream, "ContinueLine", wrap: true, width: ButtonWidth - 40);
        _continueLine.HorizontalAlignment = HorizontalAlignment.Center;
        _continueLines.AddChild(_continueLine);
        Inside(_continue, _continueLines, 14, 6);
        _continue.Pressed += () => Start(Model.ContinueHere());
        buttons.AddChild(_continue);
        _bring = Make("BringGame", MenuModel.BringWords, ButtonHeight);
        MenuTheme.AsRibbon(_bring);
        _bring.Visible = false;
        _bring.Pressed += () => Start(Model.BringHere());
        buttons.AddChild(_bring);
        _newGame = Make("NewGame", MenuModel.NewGameWords, ButtonHeight);
        MenuTheme.AsRibbon(_newGame);
        _newGame.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) _secondOfDouble = click.DoubleClick;
        };
        _newGame.Pressed += PressNewGame;
        buttons.AddChild(_newGame);

        var speech = new VBoxContainer { Name = "Speech" };
        speech.AddThemeConstantOverride("separation", 2);
        column.AddChild(speech);
        _notice = MenuTheme.Text("", MenuTheme.Book, 18, MenuTheme.Warning, "Notice", wrap: true, width: ButtonWidth - 20);
        speech.AddChild(_notice);
        _hint = MenuTheme.Text("", MenuTheme.Book, 18, MenuTheme.InkSoft, "Hint", wrap: true, width: ButtonWidth - 20);
        speech.AddChild(_hint);

        var small = new HBoxContainer { Name = "Small" };
        column.AddChild(small);
        _choose = Make("Choose", "Choose", 46, 150);
        MenuTheme.AsLink(_choose, 22);
        _choose.TooltipText = "Pick a different person, land or way to play.";
        _choose.Pressed += () => Choose?.Invoke();
        small.AddChild(_choose);
        small.AddChild(new Control { Name = "Space", SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _quit = Make("Quit", "Quit", 46, 110);
        MenuTheme.AsLink(_quit, 22);
        _quit.Pressed += () => QuitGame?.Invoke();
        small.AddChild(_quit);
    }

    private static Button Make(string name, string text, int height, int width = ButtonWidth) =>
        new() { Name = name, Text = text, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(width, height), SizeFlagsHorizontal = SizeFlags.ExpandFill };

    private static void Inside(Button button, Control contents, int horizontal, int vertical)
    {
        var padded = new MarginContainer { Name = "Padding" };
        foreach (var side in new[] { "left", "right" }) padded.AddThemeConstantOverride("margin_" + side, horizontal);
        foreach (var side in new[] { "top", "bottom" }) padded.AddThemeConstantOverride("margin_" + side, vertical);
        padded.AddChild(contents);
        button.AddChild(padded);
        padded.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MenuStyle.IgnoreMouse(padded);
    }

    // ---- what the model says, drawn ----

    /// <summary>A plain start where nothing is chosen: the land (one with a map, the player's own first) and a mode, so the buttons are ready.</summary>
    private void Settle()
    {
        var notice = Model.Notice;
        if (Model.Island == null && Model.Islands.Count > 0)
        {
            var pick = Model.Islands.FirstOrDefault(i => i.HasShape && !i.Shipped && !i.Tutorial) ?? Model.Islands.FirstOrDefault(i => i.HasShape && !i.Tutorial) ??
                       Model.Islands.FirstOrDefault(i => i.HasShape) ?? Model.Islands[0];
            Model.SelectIsland(pick.RoomId);
        }
        if (Model.Mode == null && Model.Team is { Readable: true } && Model.Offers("creative")) Model.ChooseMode("creative");
        Model.SetNotice(notice);
    }

    /// <summary>"Mira on the Garage, Challenge (Brave)": the game the buttons would start, by the land's cleaned name.</summary>
    public string? GameWords()
    {
        if (Model.WhyNotReady != null) return null;
        var words = $"{Model.Team!.DisplayName} on {Model.Island!.LandName}, {MenuModel.ModeWords(Model.Mode!)}";
        if (Model.Mode == "challenge") words += $" ({MenuModel.DifficultyWords(Model.Difficulty ?? "standard")})";
        if (Model.CopyCreative) words += ", with your Creative island";
        return TitleSky.Plain(words);
    }

    /// <summary>The page as the model says it is now: the land, the buttons and the words.</summary>
    public void Refresh()
    {
        _offer = Model.Offer;
        RefreshLand();
        RefreshButtons();
    }

    private void RefreshLand()
    {
        var island = Model.Island;
        var key = island == null ? "" : island.RoomId + "|" + island.ManifestPrefix + "|" + island.Directory;
        if (key == _pictureOf) return;
        _pictureOf = key;
        _landName.Text = island == null ? "No land is here yet." : TitleSky.Plain(island.LandName);
        var shape = island?.ReadShape();
        _picture.SetShape(shape);
        _caption.Text = island == null ? "Install a room to see its land here." : shape == null ? "No map of this one yet." : "Seen from above. North is at the top.";
    }

    private void RefreshButtons()
    {
        var why = Model.WhyNotReady;
        var offer = _offer;
        var hold = why == null ? Model.Hold() : TeamHold.Free;
        _hold = hold;
        var waiting = hold == TeamHold.Leaving;
        var asking = Model.Asking;
        _newGame.Text = asking ? MenuModel.YesNewGameWords : MenuModel.NewGameWords;
        _goBack.Visible = asking;
        _continue.Visible = offer.Continue && !asking;
        _bring.Visible = offer.Bring && !asking;
        _continue.Disabled = waiting;
        _bring.Disabled = waiting;
        _newGame.Disabled = waiting;
        _continueLine.Text = offer.Continue ? GameWords() ?? "" : "";
        _continue.TooltipText = offer.Continue ? "Continue: " + _continueLine.Text : "";
        _continueLines.Modulate = waiting ? new Color(1, 1, 1, 0.6f) : Colors.White;
        // New game leads (the ribbon) where it stands alone; beside Continue, "Bring my game here" or "Go back" it is a quiet slip.
        var leads = !offer.Continue && !offer.Bring && !asking;
        if (leads != _newGameLeads || !_styled)
        {
            _styled = true;
            _newGameLeads = leads;
            if (leads) MenuTheme.AsRibbon(_newGame); else MenuTheme.AsSlip(_newGame);
        }
        _newGame.CustomMinimumSize = new Vector2(ButtonWidth, leads ? ButtonHeight : QuietButtonHeight);
        FitContinue();
        _notice.Text = TitleSky.Plain(Model.Notice);
        _notice.Visible = _notice.Text.Length > 0;
        _hint.AddThemeFontSizeOverride("font_size", asking ? 20 : 18);
        // The question needs the room: the line under the name steps aside while it is up.
        _tagline.Visible = !asking;
        _hint.AddThemeColorOverride("font_color", asking ? MenuTheme.Ink : MenuTheme.InkSoft);
        // Continue already says which game it opens, so the line under the buttons does not say it twice.
        _hint.Text = TitleSky.Plain(why ?? (asking ? Model.Question : waiting ? MenuModel.StillClosingWords : offer.Case == StartCase.Rebuilt ? MenuModel.RebuiltWords
            : hold == TeamHold.StillOpen ? MenuModel.OpenElsewhereWords : offer.Continue ? "" : GameWords() ?? ""));
        _hint.Visible = _hint.Text.Length > 0;
    }

    private bool _styled;

    /// <summary>The button is as tall as its two lines need (a long team's and land's names wrap to a third line).</summary>
    private void FitContinue()
    {
        var need = _continueLines.GetCombinedMinimumSize().Y + 12 + 6;
        _continue.CustomMinimumSize = new Vector2(ButtonWidth, Math.Max(ButtonHeight, MathF.Ceiling(need)));
    }

    /// <summary>The hour's sun or moon, the head's words and the wash over the paper.</summary>
    public void UpdateSky()
    {
        var local = LocalNow();
        Sky = TitleSky.Read(_arguments, System.Environment.GetEnvironmentVariable, local);
        var phase = TitleSky.MoonPhase(Sky.Date, Sky.Hour);
        _date.Text = TitleSky.DateWords(Sky.Date);
        _hour.Text = $"{TitleSky.DayPart(Sky.Hour)[..1].ToUpperInvariant()}{TitleSky.DayPart(Sky.Hour)[1..]}, {TitleSky.Clock(Sky.Hour)}. The {TitleSky.MoonName(phase)}.";
        _dial.Hour = Sky.Hour;
        _dial.Phase = phase;
        _dial.QueueRedraw();
        _wash.Color = HourWash ? TitleSky.Wash(Sky.Hour) : new Color(0, 0, 0, 0);
    }

    private void Start(MenuLaunch? launch)
    {
        Refresh();
        if (launch == null) return;
        Launch?.Invoke(launch);
    }

    /// <summary>
    /// New game, by the rule of the Choose page (MainMenu.PressNewGame): over a game the first press puts the question up and gives
    /// "Go back" the focus; the next, not sooner than ConfirmGuardMs after it and never the second click of a double click, puts the old
    /// game away and starts. Where there is no game it starts at once. With no person or land chosen it opens the Choose page instead.
    /// </summary>
    private void PressNewGame()
    {
        if (Model.WhyNotReady != null) { Choose?.Invoke(); return; }
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

    private void GoBack()
    {
        Model.GoBack();
        Refresh();
        (_continue.Visible ? _continue : _bring.Visible ? _bring : _newGame).GrabFocus();
    }

    public override void _Process(double delta)
    {
        _sinceSky += delta;
        if (_sinceSky >= 20 && !Sky.Pinned) { _sinceSky = 0; UpdateSky(); }
        if (_hold == TeamHold.Free) return;
        _sinceAsked += delta;
        if (_sinceAsked < 0.1) return;
        _sinceAsked = 0;
        var hold = Model.Hold();
        if (hold == _hold) return;
        if (Model.Notice == MenuModel.StillClosingWords || (hold == TeamHold.Free && Model.Notice == PlayLease.StillOpen)) Model.SetNotice("");
        Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } escape && escape.Keycode == Key.Escape && Model.Asking)
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

    private static void ToggleFullscreen()
    {
        if (DisplayServer.GetName() == "headless") return;
        var full = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
        DisplayServer.WindowSetMode(full ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        MenuProfile.Set(RoomHud.FullscreenProfileKey, !full);
    }
}
