using Godot;
using System.Collections.Generic;
using System.Globalization;
using GarageGame.Native.Characters.Gaits;
using GarageGame.Native.Look;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>Local controls and avatar preferences; no agent authority or world-save access.</summary>
public partial class RoomHud : CanvasLayer
{
    public SmallPlayerController Player { get; set; } = null!;
    public CompanionAvatar Companion { get; set; } = null!;
    /// <summary>Set by RoomWorld so the L key can switch the room's lamps; null in fixtures without a look.</summary>
    public global::GarageGame.Native.Look.LookDirector? Look { get; set; }
    public int ViewMode { get; private set; } = 1;
    public string RoomTitle { get; set; } = "ROOM";
    /// <summary>Set by RoomWorld: what the look and the style pin need the player to know (a renderer fallback, a style that did not verify).</summary>
    public string LookNotice { get; set; } = "";
    /// <summary>The player's profile (appearance, the Gubble's name and colour, has_used_glow). A test seam: the HUD's tests point it elsewhere.</summary>
    public static string ProfilePath { get; set; } = DefaultProfilePath;
    public const string DefaultProfilePath = "user://single_player/room/avatar_profile_v1.cfg";
    private static readonly Color[] Palette = { new("d28f63"), new("65b9b0"), new("d7b765"), new("a18cc3"), new("75965c") };
    private Label _state = null!;
    /// <summary>The panel at the upper left: the hearts, the bar and the pips' row, and nothing else (it hides with them).</summary>
    private PanelContainer _meterPanel = null!;
    /// <summary>The lines the upper-left card used to hold, now the top of the H help: the room's title, the state, the clock, the mode and the settings.</summary>
    private VBoxContainer _statusLines = null!;
    /// <summary>The upper-left panel (the meters only), for the HUD suite.</summary>
    public PanelContainer MeterPanel => _meterPanel;
    /// <summary>The status lines at the top of the H help (title, state, clock, mode, settings), for the HUD suite.</summary>
    public VBoxContainer StatusLines => _statusLines;
    /// <summary>The wash-ashore fade (the sea's edge): a full-screen veil the player's body darkens and lifts.</summary>
    private ColorRect _washVeil = null!;
    // The focus (RUN-2-OPEN-SEA.md, "Things to touch"): the thing the next hand key would act on, lit by the look, with a tag.
    private Label _focusTag = null!;
    private Node3D? _focusNode;
    private Vector3 _focusTop;
    private double _focusAge = double.MaxValue;
    private Vector3 _focusFrom;
    private float _focusYaw;
    private string _focusHeld = "";
    /// <summary>The focus is asked for again at most this often while the player moves or turns, and at least this often anyway (things move).</summary>
    public const double FocusQueryS = 0.1;
    public const double FocusRefreshS = 0.5;
    /// <summary>The thing the focus is on (null for none).</summary>
    public Node3D? FocusTarget => _focusNode;
    /// <summary>The focus tag (its words, and whether and where it shows).</summary>
    public Label FocusTag => _focusTag;
    /// <summary>Times the focus was asked for (the test keeps it cheap).</summary>
    public int FocusQueries { get; private set; }
    private float _armLength;
    // F2, the third-person view, zooms (the founder, 13 October: "zoom out further to pan around and see the world from more angles").
    // Near the shoulder it is the view it always was: the camera on a short arm off the body's right shoulder, and a drag turns the
    // body. Zoomed out, the arm's pivot slides from the shoulder to the player's middle; past OrbitFromM the camera is the orbit rig's
    // (the one F3 used), a drag orbits it round the player and movement follows its heading. The numbers are in the body's scale.
    private Vector3 _shoulderAt;
    private Vector3 _centreAt;
    /// <summary>F2's lens: the shoulder camera's, and the orbit camera's once the view is zoomed out (nothing jumps when one takes over).</summary>
    public const float ShoulderFovDeg = 68.0f;
    /// <summary>
    /// The far end of F2's wheel: 8 m, 80 body heights. There the 10 cm body is about 1 % of the frame's height and the frame shows
    /// some 19 m of the 67 x 77 m island across; much further and the player would be lost in it.
    /// </summary>
    public const float ShoulderFarM = 8.0f;
    /// <summary>
    /// Past this arm length a drag orbits the camera round the player instead of turning the body: 2.5 times today's arm, eight body
    /// heights, about six wheel clicks out. Up to it the view is still the shoulder camera, its pivot sliding to the player's middle.
    /// </summary>
    public const float OrbitFromM = 0.8f;
    /// <summary>One wheel click multiplies F2's arm by this (15 % shorter) or by its inverse (18 % longer): about 20 clicks from the shoulder to ShoulderFarM.</summary>
    public const float ShoulderZoomStep = 0.85f;
    // The shoulder camera's arm swings by the eye's pitch less a little, between these (radians).
    private const float ShoulderArmPitchOffsetRad = 0.18f, ShoulderArmPitchMinRad = -1.1f, ShoulderArmPitchMaxRad = 0.8f;
    /// <summary>
    /// How far the orbit camera may look down, in degrees: from 2 (nearly level, the horizon in view; lower and the arm would meet the
    /// ground at once and put the lens inside the 10 cm body) to the shoulder camera's own limit, about 63, so the orbit hands over to
    /// the shoulder camera, and back, at the same pitch.
    /// </summary>
    public const float OrbitMinPitchDeg = 2.0f;
    /// <summary>The steepest the orbit camera looks down (see OrbitMinPitchDeg).</summary>
    public static readonly float OrbitMaxPitchDeg = -Mathf.RadToDeg(ShoulderArmPitchMinRad);
    /// <summary>Today's arm, 3.2 body heights (32 cm for the 10 cm body): the near end of F2's wheel, where the view is exactly the shoulder view.</summary>
    public float ShoulderNearM => _armLength;
    /// <summary>F2's wanted arm length, from ShoulderNearM to ShoulderFarM; walls and the ground may hold the camera closer.</summary>
    public float ShoulderDistanceM { get; private set; }
    /// <summary>Whether F2 is zoomed out past OrbitFromM: the camera is the orbit rig's, a drag orbits it and movement follows its heading.</summary>
    public bool Orbiting => ViewMode == 1 && ShoulderDistanceM > OrbitFromM;
    /// <summary>The F2 orbit's heading (radians about +Y; 0 looks along -Z). Movement follows it while Orbiting.</summary>
    public float OrbitYaw { get; private set; }
    /// <summary>How far the F2 orbit camera looks down, in degrees (OrbitMinPitchDeg to OrbitMaxPitchDeg).</summary>
    public float OrbitPitchDeg { get; private set; } = 30.0f;
    /// <summary>How far over the bed the shoulder camera keeps its lens (the open-sea bed has no collider for the arm to meet).</summary>
    public const float ArmBedClearanceM = 0.02f;

    /// <summary>
    /// The shoulder arm's length so its end stays ArmBedClearanceM over the water's bed (Codex Astra's review: at the open-sea
    /// bed, looking up, the camera went 15 cm under it). The arm runs from origin along direction (unit) for up to full.
    /// </summary>
    public static float ArmLengthOverBed(Vector3 origin, Vector3 direction, float full, float bedY, float clearance)
    {
        if (!float.IsFinite(bedY) || direction.Y >= -1e-4f) return full;
        var room = origin.Y - (bedY + clearance);
        return room <= 0 ? 0.01f : Mathf.Clamp(room / -direction.Y, 0.01f, full);
    }
    /// <summary>The movement keys, with B home from anywhere (the founder, 9 October: to the jetty, or the beach, or the start).</summary>
    private string MoveKeys => $"{PlayerControls.MoveLabel()} move · {K(Act.MoveSprint)} run · {K(Act.Jump)} jump · {K(Act.BodyHome)} back to {Player.HomeName} · {K(Act.BodyRecover)} recover · {PlayerControls.DragLabel()} to look around";
    /// <summary>What an action's key is now, for the words on screen: they follow the input map, so a remapped key shows at once.</summary>
    private static string K(StringName action) => PlayerControls.KeyLabel(action);
    /// <summary>Every label and button that names a key, with the words it shows now. Rebuilt when what they are made from changes (RefreshKeyTextIfChanged, every frame).</summary>
    private readonly List<(System.Action<string> Set, System.Func<string> Words)> _keyTexts = new();
    private Label _notice = null!;
    private PanelContainer _footer = null!;
    private VBoxContainer _keyHelp = null!;
    private Label _helpHint = null!;
    private SpringArm3D _arm = null!;
    private Camera3D _shoulder = null!;
    // F3, the diorama view: a high-angle camera orbiting the player (founder playtest, 6 October: F3 froze). SHELVED on 13 October
    // (the game commits to first person and third person): no key reaches it, but ViewMode 2 stays in code for the look harness
    // (tools/look cameras observe_f3 and observe_f4 set hud_view 2 and 3), and the orbit rig below is the one F2 uses zoomed out.
    // Framed for the 10 cm body; a narrow lens and the look's depth of field make the room read as a miniature.
    public const float DioramaMinPitchDeg = 20.0f;
    public const float DioramaMaxPitchDeg = 80.0f;
    public const float DioramaDefaultPitchDeg = 45.0f;
    public const float DioramaMinDistanceM = 0.30f;
    public const float DioramaMaxDistanceM = 2.4f;
    public const float DioramaDefaultDistanceM = 1.4f;
    public const float DioramaFovDeg = 40.0f;
    // F4, the isometric view (Look lane, 6 October art direction): the isometric angle, a long narrow lens. SHELVED like F3 (and its
    // quarter turns on [ and ] are gone); ViewMode 3 stays in code for the look harness only.
    public const float IsoPitchDeg = 35.26f;
    public const float IsoDistanceM = 2.7f;
    public const float IsoFovDeg = 30.0f;
    /// <summary>While the companion is this close, the high views frame both avatars, centred between them.</summary>
    public const float FrameCompanionWithinM = 0.8f;
    /// <summary>The isometric heading: 45 degrees off the room's axes, the quarter turn nearest the player's facing (nothing turns it now).</summary>
    public float IsoYaw { get; private set; } = Mathf.Pi / 4;
    public const float MouseRadiansPerPixel = 0.0025f;
    /// <summary>The shelved F3's orbit heading (radians about +Y; 0 looks along -Z). Movement follows it in F3.</summary>
    public float DioramaYaw { get; private set; }
    /// <summary>How far the diorama camera looks down, in degrees.</summary>
    public float DioramaPitchDeg { get; private set; } = DioramaDefaultPitchDeg;
    /// <summary>The wanted distance from the player; walls and the ceiling may hold the camera closer.</summary>
    public float DioramaDistanceM { get; private set; } = DioramaDefaultDistanceM;
    public Camera3D DioramaCamera => _diorama;

    // Playtest clock keys (founder, second playtest: the clock follows real time, so a 3 a.m. playtest only showed
    // night). T steps the hour through the day's own light; Shift+T steps the date through the solstices and equinoxes.
    // Each steps round and ends back on the real clock (the real calendar for Shift+T).
    /// <summary>The steps of the T key. Their hours come from the sun's real rise and set on the day shown (TimeStopHour).</summary>
    public static readonly string[] TimeStopNames = { "dawn", "morning", "noon", "late afternoon", "sunset", "dusk", "night" };
    /// <summary>The steps of the Shift+T key, by day of the year (a 365-day year). Named for the astronomy, so they hold in either hemisphere.</summary>
    public static readonly (string Name, int DayOfYear)[] SeasonStops =
    {
        ("March equinox", 79), ("June solstice", 172), ("September equinox", 265), ("December solstice", 355),
    };
    /// <summary>The T step now (an index into TimeStopNames), or -1 while the hour follows the real clock.</summary>
    public int TimeStop { get; private set; } = -1;
    /// <summary>The Shift+T step now (an index into SeasonStops), or -1 while the date follows the real calendar.</summary>
    public int SeasonStop { get; private set; } = -1;
    private double _clockTimer;
    private Label _clock = null!;

    /// <summary>
    /// The hour (local standard time) of a T step on a day of the year. Dawn is a quarter of an hour before the sun's
    /// centre crosses the horizon, sunset a quarter of an hour before it sets (the golden hour), dusk half an hour
    /// after (the sun six degrees down, where the moon takes over), night two and a half hours after sunset.
    /// </summary>
    public static float TimeStopHour(StylePreset preset, int stop, int dayOfYear)
    {
        var (rise, set) = LookClock.SunTimes(preset.Tuning.Sun, dayOfYear);
        var hour = stop switch
        {
            0 => rise - 0.25f, 1 => rise + 2.5f, 2 => (rise + set) * 0.5f, 3 => set - 2.0f, 4 => set - 0.25f, 5 => set + 0.5f, _ => set + 2.5f,
        };
        return Mathf.PosMod(hour, 24f);
    }

    /// <summary>T: the next time of day, then back to the real clock after night. Does nothing in a room without a look.</summary>
    public void StepTimeOfDay()
    {
        if (Look == null) return;
        TimeStop = TimeStop + 1 >= TimeStopNames.Length ? -1 : TimeStop + 1;
        ApplyClock();
    }

    /// <summary>Shift+T: the next solstice or equinox, then back to the real calendar after the December solstice.</summary>
    public void StepSeason()
    {
        if (Look == null) return;
        SeasonStop = SeasonStop + 1 >= SeasonStops.Length ? -1 : SeasonStop + 1;
        ApplyClock();
    }

    /// <summary>Pin the look to what the two steps ask, each part falling back to the real clock or calendar; release the pin when both do.</summary>
    private void ApplyClock()
    {
        if (Look == null) return;
        _clockTimer = 0;
        if (TimeStop < 0 && SeasonStop < 0) { Look.ReleaseClock(); return; }
        var real = Look.RealNow();
        var day = SeasonStop >= 0 ? SeasonStops[SeasonStop].DayOfYear : real.DayOfYear;
        Look.SetClock(TimeStop >= 0 ? TimeStopHour(Look.Preset, TimeStop, day) : real.Hour, day);
    }

    /// <summary>The clock line of the H help's status lines: the hour and where it comes from, then the season and date likewise.</summary>
    public string ClockText()
    {
        if (Look?.Moment is not { } moment) return "";
        var minutes = Mathf.PosMod(Mathf.RoundToInt(moment.Hour * 60f), 24 * 60);
        var hour = $"{minutes / 60:00}:{minutes % 60:00} " + (TimeStop >= 0 ? TimeStopNames[TimeStop] : "real clock") + $" ({K(Act.TimeStep)})";
        var date = new System.DateTime(2026, 1, 1).AddDays(moment.DayOfYear - 1).ToString("d MMM", CultureInfo.InvariantCulture);
        return $"{hour}  ·  {moment.Season}, {date}, " + (SeasonStop >= 0 ? SeasonStops[SeasonStop].Name : "real date") + $" ({K(Act.SeasonStep)})";
    }

    private (float, string, int, string, string, bool, bool, string?, bool)? _stateAsOf;
    private string? _noticeShown;
    private string? _teamNoticeShown;
    private (int, int, string?, int, int, int)? _clockAsOf;

    private Node3D _dioramaPivot = null!;
    private SpringArm3D _dioramaArm = null!;
    private Camera3D _diorama = null!;
    private int _playerColor;
    private int _companionColor = 1;
    /// <summary>
    /// The notice line's words. It starts empty: the first notice of a play is a real one (the look's, a key's, an action's), never Run 0's
    /// placeholder sentence (the first recorded play, 16 October: its first notice at 0:14 was that sentence).
    /// </summary>
    private string _noticeText = "";

    public override void _Ready()
    {
        Name = "RoomHud";
        PlayerControls.EnsureDefaults();
        // Place the rendered camera before LookDirector computes its focus depth.
        ProcessPriority = -1;
        BuildCameras();
        LoadPreferences();
        var theme = new Theme { DefaultFontSize = 18 };
        var shade = new StyleBoxFlat
        {
            BgColor = new Color(0.075f, 0.13f, 0.13f, 0.94f),
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 12, ContentMarginBottom = 12,
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12
        };
        theme.SetStylebox("panel", "PanelContainer", shade);
        theme.SetColor("font_color", "Label", new Color("f0e6cf"));
        var compactTheme = (Theme)theme.Duplicate();
        compactTheme.DefaultFontSize = 14;
        var compactShade = (StyleBoxFlat)shade.Duplicate();
        compactShade.ContentMarginLeft = compactShade.ContentMarginRight = 8;
        compactShade.ContentMarginTop = compactShade.ContentMarginBottom = 6;
        compactTheme.SetStylebox("panel", "PanelContainer", compactShade);
        compactTheme.SetConstant("separation", "VBoxContainer", 2);
        compactTheme.SetConstant("separation", "HBoxContainer", 3);
        // The upper left holds the three meters and nothing else (decision 66, 16 October: "just have Health, Stamina, and Pips on the
        // upper left"): the hearts, the bar and the pips' row, drawn (RoomHud.Meters.cs). It shows only while the meters do.
        _meterPanel = new PanelContainer { Name = "MeterPanel", Position = new Vector2(18, 18), Theme = compactTheme, Visible = false };
        AddChild(_meterPanel);
        var column = new VBoxContainer { Name = "MeterColumn" }; _meterPanel.AddChild(column);
        BuildMeters(column);
        // What the card used to say and hold lives in the H help below, the one place every key and setting is read: the room's title, the
        // state, the clock and the mode (the status lines), the Gubble's buttons and the hand buttons (they work there). Built here, placed there.
        _statusLines = new VBoxContainer { Name = "StatusLines" };
        _statusLines.AddChild(new Label { Name = "RoomTitle", Text = RoomTitle });
        _state = new Label { Name = "State" }; _statusLines.AddChild(_state);
        _clock = new Label { Name = "Clock", Visible = false }; _statusLines.AddChild(_clock);
        // The Gubble's orders as buttons too: the same commands as the keys, the wheel and the smart ask (RoomHud.Gubble.cs).
        var actions = new HBoxContainer { Name = "CompanionActions" };
        // The Gubble's keys under its name (the Glow playtest: whose abilities these are).
        AddKeyLine(actions, () => Companion.NameTag + ":", "CompanionName");
        AddButton(actions, "Follow", () => Order("follow"), 26);
        AddButton(actions, "Wait", () => Order("stay"), 26);
        AddKeyButton(actions, () => $"{K(Act.GubbleRecall)} Come", Recall, 26);
        AddKeyButton(actions, () => $"{K(Act.GubbleStop)} Stop", StopGubble, 26);
        AddKeyButton(actions, () => $"{K(Act.GubbleSlot1)} {SlotName(0)}", () => CastSlot(0, AimNow()), 26);
        BuildSlotPrice(actions);
        AddButton(actions, "Settings", ToggleSettings, 26);
        // The hand keys send the same sandbox commands the companion uses (CommandHost.PlayerHands, PlayerPush).
        var hands = new HBoxContainer { Name = "HandActions" };
        AddKeyButton(hands, () => $"{K(Act.Hands)} Pick up / put down", Hands, 26);
        AddKeyButton(hands, () => $"{K(Act.Push)} Push", Push, 26);
        AddKeyButton(hands, () => $"{K(Act.ItemGather)} Gather", Gather, 26);
        _focusTag = new Label { Name = "FocusTag", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Theme = compactTheme };
        AddChild(_focusTag);
        _washVeil = new ColorRect { Name = "HomeVeil", Color = new Color(0.06f, 0.13f, 0.18f, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_washVeil);
        _washVeil.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // The sea wraps round: the high views' rig, eased every frame, is put straight on the player again, never swept across the island.
        Player.Wrapped += (_, _) => { if (ViewMode >= 2 || Orbiting) PlaceDioramaRig(snap: true); };
        Player.WentHome += body => _noticeText = body.LastHome switch
        {
            "jetty" => "Home: back at the jetty.",
            "shore" => "Out of breath: you are back on the shore.",
            "spawn" => "Home: back where you started.",
            "" => "There was no room at home; you are back where you last stood.",
            _ => "Home: back on the beach.",
        };
        _footer = new PanelContainer { Name = "HelpFooter", Theme = compactTheme, GrowVertical = Control.GrowDirection.Begin };
        AddChild(_footer);
        _footer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        _footer.OffsetLeft = 18; _footer.OffsetRight = -18; _footer.OffsetTop = _footer.OffsetBottom = -18;
        var help = new VBoxContainer(); _footer.AddChild(help);
        _helpHint = new Label(); help.AddChild(_helpHint);
        _keyTexts.Add((words => _helpHint.Text = words, () => $"{K(Act.HudHelp)} " + (_keyHelp.Visible ? "hide keys" : "keys")));
        _keyHelp = new VBoxContainer { Name = "KeyHelp", Visible = false }; help.AddChild(_keyHelp);
        // First what the card said (the title, the state, the clock, the mode, the settings), then every key.
        _keyHelp.AddChild(_statusLines);
        // Every key named below comes from the input map (PlayerControls.Label), so a remapped key shows here.
        AddKeyLine(_keyHelp, () => MoveKeys, "MoveKeys");
        AddKeyLine(_keyHelp, () => $"Climb: keep walking into a steep face, {K(Act.MoveForward)} up, {K(Act.MoveBack)} down, {K(Act.MoveLeft)}/{K(Act.MoveRight)} across, {K(Act.Jump)} lets go · Swim: deep water floats you, {K(Act.Jump)} leaps · Dive: hold {K(Act.MoveDive)}, {K(Act.Jump)} rises, {K(Act.MoveForward)} swims the way you look, let go to drift up");
        AddKeyLine(_keyHelp, () => $"{K(Act.ViewEye)} first person · {K(Act.ViewShoulder)} third person: {PlayerControls.ZoomLabel()} zooms out to look around, and zoomed out {PlayerControls.DragLabel()} orbits and {PlayerControls.MoveLabel()} follows the view");
        AddKeyLine(_keyHelp, () => $"{K(Act.ViewObserve)} observe (a very tight tilt-shift view, best zoomed out) · {K(Act.HudCustomize)} settings · {K(Act.HudMagic)} the Gubble's magic · {K(Act.Journal)} journal ({K(Act.MouseRelease)} closes any)", "PanelKeys");
        // The window (RoomHud.Screen.cs): the key, and what the game remembers for the next launch.
        AddKeyLine(_keyHelp, ScreenKeysLine, "ScreenKeys");
        AddKeyLine(_keyHelp, () => $"Walk into a {MeterWords.One(Host, "fractals")} to collect it · {K(Act.Hands)} pick up what you face · {K(Act.Hands)} again sets it down in front of you, or on top of what you face (the box, the book) · {K(Act.Push)} push what you face 10 cm · {K(Act.ItemGather)} gather a log or stone you hold into your wallet · {K(Act.Rest)} sit down by a lit campfire to rest (moving stands you up)");
        // The hand buttons, under the hand keys.
        _keyHelp.AddChild(hands);
        // The Gubble's keys, under its name (the Glow playtest: the player should see whose magic this is), the buttons first.
        AddKeyLine(_keyHelp, () => $"{Companion.NameTag}, your companion:", "GubbleHeading");
        _keyHelp.AddChild(actions);
        AddKeyLine(_keyHelp, () => $"{K(Act.GubbleAsk)} asks for what fits where you point (wait or follow, fetch or put down, light the dark, go and look; on a bubble, choose it, then a spot to lead it there, a higher one to lift it); hold it for the wheel · {K(Act.GubbleRecall)} come, then follow · {K(Act.GubbleStop)} stop · {K(Act.GubbleEyes)} look through its eyes", "GubbleKeys");
        AddKeyLine(_keyHelp, MagicKeysLine, "GubbleSlots");
        // The toggles that bend the world for testing, apart from the keys the game is played with.
        _keyHelp.AddChild(new Label { Name = "TestingHeading", Text = "Testing" });
        AddKeyLine(_keyHelp, () => $"{K(Act.PhysicsNext)} gravity · {K(Act.TimeStep)} time of day · {K(Act.SeasonStep)} season (each steps round to the real clock) · {K(Act.Lamps)} lamps · {K(Act.BounceStep)} your character's bounce (x0.5, x1, x1.5) · {K(Act.BouncePrint)} print its motion profile", "TestingKeys");
        // The playtest record's keys and its beat (RoomHud.Playtest.cs).
        AddKeyLine(_keyHelp, PlaytestKeysLine, "PlaytestKeys");
        // The notice line shows only when it has words: folded, the footer is the one small "H keys" line.
        _notice = new Label { Name = "Notice", Text = _noticeText, Visible = _noticeText.Length > 0 }; help.AddChild(_notice);
        // The footer is a line of no height at the bottom edge that grows up to the size its content needs, and gives it back when the help folds.
        // (It was told its size here once, which moved its top edge nowhere: the unfolded help ran off the bottom of the screen.)
        // The settings (RoomHud.Settings.cs): the player's switches and "Your AI". The Customize panel's avatars moved to the opening menu.
        var settingsPanel = new PanelContainer { Name = "SettingsPanel", Position = new Vector2(18, 190), Theme = theme, Visible = false };
        AddChild(settingsPanel);
        BuildSettingsPanel(settingsPanel);
        BuildGubbleControls(compactTheme);
        BuildSwimHint(compactTheme);
        BuildMagicPanel(actions, _statusLines, theme);
        // The settings read last in the status lines, under the mode line the Magic panel's builder added (RoomHud.Screen.cs).
        BuildSettingsLine(_statusLines);
        // The journal last: the book opens over everything else on screen (RoomHud.Journal.cs). Then the eye icon above its dock,
        // whose view through the Gubble's eyes covers the whole screen, the book's dock too (RoomHud.GubbleView.cs).
        BuildMinimap();
        BuildJournal();
        BuildGubbleView(compactTheme);
        WatchForPlaytest();
        ApplyAura();
        SetViewMode(1);
        if (LookNotice.Length > 0) _noticeText = LookNotice;
        // An empty line has nothing to report: the record's first notice is a real one (RoomHud.Playtest.cs).
        else if (_noticeText.Length == 0) _noticeShown = _noticeText;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        StartFullscreenIfAsked();
        RefreshKeyText();
        // The Bubble's film and parts are drawn once out of sight now, so the first cast is no hitch (LookDirector.WarmBubble).
        Look?.WarmBubble();
    }

    /// <summary>A help line: its words are rebuilt from the input map whenever a key changes.</summary>
    private void AddKeyLine(Node parent, System.Func<string> words, string name = "")
    {
        var label = new Label();
        if (name.Length > 0) label.Name = name;
        parent.AddChild(label);
        _keyTexts.Add((text => label.Text = text, words));
    }

    /// <summary>A button that names its key: its words are rebuilt from the input map whenever a key changes.</summary>
    private void AddKeyButton(Node parent, System.Func<string> words, System.Action action, int minimumHeight = 38)
    {
        var button = new Button { CustomMinimumSize = new Vector2(0, minimumHeight), FocusMode = Control.FocusModeEnum.All };
        button.Pressed += action;
        parent.AddChild(button);
        _keyTexts.Add((text => button.Text = text, words));
    }

    private void RefreshKeyText()
    {
        foreach (var (set, words) in _keyTexts) set(words());
        _keyTextsAsOf = KeyTextInputs();
    }

    /// <summary>
    /// Every frame: the key texts again only when something they are made from changed (S12: some twenty strings rebuilt and handed
    /// to the engine sixty times a second). They are made from the keys' names (PlayerControls.MapVersion), whether the help is open,
    /// the Gubble's name, the way home, the island's abilities and which of them the pool can pay (PoolHint), the playtest beat and the
    /// switches the help reads out (SettingsBits).
    /// </summary>
    private void RefreshKeyTextIfChanged()
    {
        if (KeyTextInputs() != _keyTextsAsOf) RefreshKeyText();
    }

    private (int Map, bool Help, string Name, string Home, object? Rules, int Pool, int Beat, int Settings) KeyTextInputs() =>
        (PlayerControls.MapVersion, _keyHelp.Visible, Companion.CompanionName, Player.HomeName, Host?.Rules, PoolHint() | FireKeyBits(), PlaytestBeat(), SettingsBits());

    /// <summary>What Fire's key words depend on (RoomHud.FireWheel.cs): whether Fire, and Fireline, are learned.</summary>
    private int FireKeyBits() => Host is not { } host ? 0 : (host.FireLearned ? 1 << 28 : 0) | (host.FirelineLearned ? 1 << 29 : 0);

    private (int Map, bool Help, string Name, string Home, object? Rules, int Pool, int Beat, int Settings)? _keyTextsAsOf;

    private static void AddButton(Node parent, string text, System.Action action, int minimumHeight = 38)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, minimumHeight), FocusMode = Control.FocusModeEnum.All };
        button.Pressed += action;
        parent.AddChild(button);
    }

    private void BuildCameras()
    {
        // Over-the-shoulder framing scales with the body (0.10 m player: 8 cm up, 32 cm behind).
        var height = Player.BodyHeightM;
        // The arm sweeps a lens-sized sphere, not a ray, so a camera held in along a face never stands closer to it than the lens's near
        // plane (mark 1's arm part: a ray grazing a slope left the lens just behind the face).
        _arm = new SpringArm3D
        {
            Name = "FollowCameraArm", Position = new Vector3(Player.BodyRadiusM * 2.2f, height * 0.8f, 0), SpringLength = height * 3.2f, Margin = height * 0.1f,
            CollisionMask = Room.RoomBuilder.WorldLayer, Shape = new SphereShape3D { Radius = height * ArmLensRadiusHeights },
        };
        Player.AddChild(_arm);
        _armLength = _arm.SpringLength;
        ShoulderDistanceM = _armLength;
        _shoulderAt = _arm.Position;
        _centreAt = new Vector3(0, height * 0.5f, 0);
        _arm.AddExcludedObject(Player.GetRid());
        _shoulder = new Camera3D { Name = "FollowCamera", Near = 0.005f, Far = Look?.FarPlaneM ?? 100, Fov = ShoulderFovDeg };
        _arm.AddChild(_shoulder);
        // The diorama rig is not a child of the body, so it orbits independently of the body's facing. The arm's
        // sphere keeps the lens out of walls, furniture and the ceiling; only world geometry (layer 1) stops it.
        // The rig is moved every rendered frame, not every physics tick, so it must not be interpolated; it follows the
        // player's interpolated position instead (PlaceDioramaRig). Without physics interpolation the two are the same.
        _dioramaPivot = new Node3D { Name = "DioramaPivot", PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off };
        Player.GetParent().AddChild(_dioramaPivot);
        _dioramaArm = new SpringArm3D
        {
            Name = "DioramaArm", SpringLength = DioramaDistanceM, Margin = 0.01f, CollisionMask = 1,
            Shape = new SphereShape3D { Radius = 0.02f }
        };
        _dioramaPivot.AddChild(_dioramaArm);
        _dioramaArm.AddExcludedObject(Player.GetRid());
        if (Companion != null) _dioramaArm.AddExcludedObject(Companion.GetRid());
        _diorama = new Camera3D { Name = "DioramaCamera", Near = 0.01f, Far = Look?.FarPlaneM ?? 100, Fov = DioramaFovDeg };
        _dioramaArm.AddChild(_diorama);
        PlaceDioramaRig(snap: true);
        IgnoreCollisionOnlyParts();
    }

    /// <summary>
    /// A shell part that is never drawn ("drawn": false: a tree's hidden climbing pole and crown caps) is no obstacle to a
    /// camera: the arms pass through it as through the leaves around it. (Founder's playtest, 9 October: inside a crown the
    /// follow camera was pulled in to the climber's head by the hidden caps.)
    /// </summary>
    private void IgnoreCollisionOnlyParts()
    {
        if (GetParent() is not RoomWorld { Built: { } built }) return;
        foreach (var node in built.FindChildren("*", "StaticBody3D", true, false))
            if (node is StaticBody3D body && body.HasMeta("drawn") && !body.GetMeta("drawn").AsBool())
            {
                _arm.AddExcludedObject(body.GetRid());
                _dioramaArm.AddExcludedObject(body.GetRid());
            }
    }

    public void SetViewMode(int mode)
    {
        if (mode is < 0 or > 3 || _shoulder == null) return;
        var entering = mode == 2 && ViewMode != 2;
        var enteringShoulder = mode == 1 && ViewMode != 1;
        // Enter the isometric view at the quarter-turn heading nearest the player's facing.
        if (mode == 3 && ViewMode != 3) IsoYaw = Mathf.Wrap(Mathf.Snapped(Player.GlobalRotation.Y - Mathf.Pi / 4, Mathf.Pi / 2) + Mathf.Pi / 4, -Mathf.Pi, Mathf.Pi);
        ViewMode = mode;
        // The body stays drawn in every view: in F1 the player's own eye leaves its layer out (EyeView.PlayerEyeCullMask),
        // so the Gubble's eye still sees the player.
        if (mode == 0) Player.EyeCamera.MakeCurrent();
        else if (mode == 1)
        {
            // Back in F2 zoomed out: the orbit camera starts behind the player again, at the pitch it had.
            if (enteringShoulder && Orbiting) OrbitYaw = PlayerYaw();
            ApplyShoulderCamera();
        }
        else
        {
            // Enter behind the player, at the last pitch and zoom the player chose.
            if (entering) DioramaYaw = Player.GlobalRotation.Y;
            _diorama.Fov = mode == 3 ? IsoFovDeg : DioramaFovDeg;
            PlaceDioramaRig(snap: true);
            _diorama.MakeCurrent();
        }
        Player.SetMovementFrame(mode switch { 2 => DioramaYaw, 3 => IsoYaw, 1 when Orbiting => OrbitYaw, _ => null });
    }

    /// <summary>The body's heading as it is drawn this frame (the interpolated one, as the shoulder camera on its arm sees it).</summary>
    private float PlayerYaw()
    {
        var back = Player.GetGlobalTransformInterpolated().Basis.Z;
        return Mathf.Atan2(back.X, back.Z);
    }

    /// <summary>F2's camera for the arm it has now: the shoulder camera up to OrbitFromM, the orbit rig's beyond it.</summary>
    private void ApplyShoulderCamera()
    {
        if (Orbiting)
        {
            _diorama.Fov = ShoulderFovDeg;
            PlaceDioramaRig(snap: true);
            _diorama.MakeCurrent();
        }
        else
        {
            UpdateShoulderArm();
            _shoulder.MakeCurrent();
        }
    }

    /// <summary>
    /// The shoulder camera's arm for this frame: it swings by the eye's pitch, its pivot slides from the shoulder to the player's middle
    /// as the arm grows to OrbitFromM (so the orbit camera, which circles the middle, takes over without a jump), and it is held a
    /// little over the open-sea bed. At ShoulderNearM it is exactly the arm F2 always had.
    /// </summary>
    private void UpdateShoulderArm()
    {
        var slide = Mathf.SmoothStep(ShoulderNearM, OrbitFromM, ShoulderDistanceM);
        var pivot = slide <= 0 ? _shoulderAt : _shoulderAt.Lerp(_centreAt, slide);
        _arm.Rotation = new Vector3(Mathf.Clamp(Player.EyeCamera.Rotation.X - ShoulderArmPitchOffsetRad, ShoulderArmPitchMinRad, ShoulderArmPitchMaxRad), 0, 0);
        var length = Mathf.Min(ShoulderDistanceM, OrbitFromM);
        // Mark 1 of the first recorded play (16 October), the arm's part: hung from the right shoulder, 2.2 radii out from the body's
        // middle, the arm met a face on the body's right within centimetres and the camera stood beside the body, which left the frame.
        // When the camera the shoulder's arm would give loses the body toward the frame's edge, the pivot comes in to just behind the
        // head on the body's middle line, so a camera held in stays behind the body, a body radius and more from it.
        var held = ViewMode == 1 && !Orbiting ? ShoulderHeldIn(pivot, length) : 0;
        _arm.Position = held > 0 ? pivot.Lerp(new Vector3(0, pivot.Y, Player.BodyRadiusM * ArmBehindHeadRadii), held) : pivot;
        _arm.SpringLength = ArmLengthOverBed(_arm.GlobalPosition, _arm.GlobalBasis.Z, length, Player.Water.Wet ? Player.Water.BedY : float.NegativeInfinity, ArmBedClearanceM);
    }

    /// <summary>F2's arm sweeps a sphere this many body heights round (6 mm for the 10 cm body: more than the lens's 5 mm near plane).</summary>
    public const float ArmLensRadiusHeights = 0.06f;
    /// <summary>Held in, F2's arm hangs from this many body radii behind the body's middle line, at the shoulder's height: outside the body.</summary>
    public const float ArmBehindHeadRadii = 1.5f;
    /// <summary>The share of the way from the frame's middle to its edge where the body's middle starts the arm's pivot in from the shoulder.</summary>
    private const float ArmHeldFromEdge = 0.6f;
    private Godot.Collections.Array<Rid>? _armExclude;

    /// <summary>
    /// How far F2's arm pivot comes in from the shoulder (0 to 1): 0 while the camera the shoulder's arm gives, held in by whatever
    /// it meets, has the body's middle well inside its frame (the view as it always was); 1 once that middle would be at the
    /// frame's edge, beside the camera or behind it. Measured from the shoulder's own arm each frame, so it never feeds on itself.
    /// </summary>
    private float ShoulderHeldIn(Vector3 pivot, float length)
    {
        var basis = Player.GlobalBasis * _arm.Basis;
        var from = Player.GlobalTransform * pivot;
        var space = Player.GetWorld3D().DirectSpaceState;
        _armExclude ??= new Godot.Collections.Array<Rid> { Player.GetRid() };
        // A shoulder in, past or against a wall beside the body (walking along it, or a face on the right; the lens's sphere would start
        // in it): nothing the arm meets from there counts.
        var middleLine = Player.GlobalTransform * new Vector3(0, pivot.Y, pivot.Z);
        var outward = from - middleLine;
        var beyond = outward.LengthSquared() > 1e-10f ? from + outward.Normalized() * (Player.BodyHeightM * ArmLensRadiusHeights + 0.001f) : from;
        using (var reach = PhysicsRayQueryParameters3D.Create(middleLine, beyond, _arm.CollisionMask, _armExclude))
        using (var wall = space.IntersectRay(reach))
            if (wall.Count > 0) return 1;
        using var query = PhysicsRayQueryParameters3D.Create(from, from + basis.Z * length, _arm.CollisionMask, _armExclude);
        using var hit = space.IntersectRay(query);
        if (hit.Count == 0) return 0;
        var free = Mathf.Max(0, from.DistanceTo(hit[Kernel.KernelNames.HitPosition].AsVector3()) - _arm.Margin - Player.BodyHeightM * ArmLensRadiusHeights);
        // The body's middle as that camera would see it (its space: -Z ahead).
        var seen = basis.Inverse() * (Player.GlobalPosition + Vector3.Up * (Player.BodyHeightM * 0.5f) - (from + basis.Z * free));
        if (seen.Z > -1e-4f) return 1;
        var size = GetViewport().GetVisibleRect().Size;
        var tanHalf = Mathf.Tan(Mathf.DegToRad(ShoulderFovDeg * 0.5f));
        var edge = Mathf.Max(Mathf.Abs(seen.Y) / tanHalf, Mathf.Abs(seen.X) / (tanHalf * (size.Y > 0 ? size.X / size.Y : 16f / 9f))) / -seen.Z;
        return Mathf.SmoothStep(ArmHeldFromEdge, 1f, edge);
    }

    /// <summary>Orbit the diorama camera by a mouse movement in pixels: sideways turns around the player, up and down tilts.</summary>
    public void OrbitDiorama(Vector2 mouseRelative)
    {
        if (!mouseRelative.IsFinite()) return;
        DioramaYaw = Mathf.Wrap(DioramaYaw - mouseRelative.X * MouseRadiansPerPixel, -Mathf.Pi, Mathf.Pi);
        DioramaPitchDeg = Mathf.Clamp(DioramaPitchDeg + Mathf.RadToDeg(mouseRelative.Y * MouseRadiansPerPixel), DioramaMinPitchDeg, DioramaMaxPitchDeg);
        if (ViewMode == 2) Player.SetMovementFrame(DioramaYaw);
    }

    /// <summary>Zoom by wheel steps: positive moves in, negative out. Each step is about 12 %.</summary>
    public void ZoomDiorama(float steps)
    {
        if (!float.IsFinite(steps)) return;
        DioramaDistanceM = Mathf.Clamp(DioramaDistanceM * Mathf.Pow(0.88f, steps), DioramaMinDistanceM, DioramaMaxDistanceM);
    }

    /// <summary>
    /// Zoom F2 by wheel steps: positive moves in, negative out. Each step is ShoulderZoomStep, between today's arm (ShoulderNearM, exactly the
    /// shoulder view) and ShoulderFarM. Crossing OrbitFromM swaps the cameras where they agree: going out, the orbit takes the body's
    /// heading and the arm's pitch and movement follows it; coming in, the body takes the orbit's heading and pitch (it turns to
    /// face the way the camera looked) and movement is the body's own again.
    /// </summary>
    public void ZoomShoulder(float steps)
    {
        if (!float.IsFinite(steps)) return;
        var orbiting = Orbiting;
        ShoulderDistanceM = Mathf.Clamp(ShoulderDistanceM * Mathf.Pow(ShoulderZoomStep, steps), ShoulderNearM, ShoulderFarM);
        if (ViewMode != 1) return;
        if (Orbiting == orbiting)
        {
            if (!orbiting) UpdateShoulderArm();
            return;
        }
        if (Orbiting)
        {
            OrbitYaw = PlayerYaw();
            OrbitPitchDeg = Mathf.Clamp(-Mathf.RadToDeg(Mathf.Clamp(Player.EyeCamera.Rotation.X - ShoulderArmPitchOffsetRad, ShoulderArmPitchMinRad, ShoulderArmPitchMaxRad)), OrbitMinPitchDeg, OrbitMaxPitchDeg);
        }
        else Player.SetView(OrbitYaw, ShoulderArmPitchOffsetRad - Mathf.DegToRad(OrbitPitchDeg));
        ApplyShoulderCamera();
        Player.SetMovementFrame(Orbiting ? OrbitYaw : null);
    }

    /// <summary>Orbit F2's camera by a mouse movement in pixels, while it is zoomed out: sideways turns round the player, up and down tilts.</summary>
    public void OrbitShoulder(Vector2 mouseRelative)
    {
        if (!Orbiting || !mouseRelative.IsFinite()) return;
        OrbitYaw = Mathf.Wrap(OrbitYaw - mouseRelative.X * MouseRadiansPerPixel, -Mathf.Pi, Mathf.Pi);
        OrbitPitchDeg = Mathf.Clamp(OrbitPitchDeg + Mathf.RadToDeg(mouseRelative.Y * MouseRadiansPerPixel), OrbitMinPitchDeg, OrbitMaxPitchDeg);
        Player.SetMovementFrame(OrbitYaw);
    }

    /// <summary>
    /// Centre the rig on the player's middle, or between the two avatars while the companion is near (fading over the
    /// last 30% of FrameCompanionWithinM so the view never jumps), eased so a jump or a step does not jolt the view. F2's orbit
    /// is always on the player alone (the focus is the body, wherever the Gubble is).
    /// </summary>
    private void PlaceDioramaRig(bool snap, float delta = 0)
    {
        // Where the bodies are drawn this frame: the interpolated position when physics interpolation is on, else the physics one.
        var playerAt = Player.GetGlobalTransformInterpolated().Origin;
        var centre = playerAt + Vector3.Up * (Player.BodyHeightM * 0.5f);
        if (Companion != null && ViewMode != 1)
        {
            var companionAt = Companion.GetGlobalTransformInterpolated().Origin;
            var apart = companionAt.DistanceTo(playerAt);
            var share = 0.5f * (1f - Mathf.SmoothStep(0.7f * FrameCompanionWithinM, FrameCompanionWithinM, apart));
            centre = centre.Lerp(companionAt + Vector3.Up * (Companion.BodyHeightM * 0.5f), share);
        }
        var weight = snap ? 1.0f : 1.0f - Mathf.Exp(-12.0f * delta);
        var position = _dioramaPivot.GlobalPosition.Lerp(centre, weight);
        var iso = ViewMode == 3;
        var shoulder = ViewMode == 1;
        var pitch = iso ? IsoPitchDeg : shoulder ? OrbitPitchDeg : DioramaPitchDeg;
        var yaw = iso ? IsoYaw : shoulder ? OrbitYaw : DioramaYaw;
        _dioramaPivot.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(-Mathf.DegToRad(pitch), yaw, 0), EulerOrder.Yxz), position);
        // F2's orbit is also held over the open-sea bed, which has no collider for the arm to meet; its arm is kept at the length where
        // the shoulder camera hands over while that camera is the current one, so the swap finds it where it should be.
        _dioramaArm.SpringLength = iso ? IsoDistanceM : shoulder
            ? ArmLengthOverBed(_dioramaArm.GlobalPosition, _dioramaArm.GlobalBasis.Z, Orbiting ? ShoulderDistanceM : OrbitFromM, Player.Water.Wet ? Player.Water.BedY : float.NegativeInfinity, ArmBedClearanceM)
            : DioramaDistanceM;
    }

    public override void _Process(double delta)
    {
        // One control's key read from the map again, in turn: a binding changed by anything still shows (PlayerControls.KeyLabel).
        PlayerControls.Recheck();
        // A drag whose release the HUD never saw (let go outside the window, a lost focus) ends: the cursor comes back.
        if (_drag.Pressed && !(DragHeldForTests?.Invoke() ?? PlayerControls.Held(Act.LookDrag))) EndDrag();
        // While the wheel has the game paused, only the wheel's clock runs.
        _wheel.Tick(delta);
        if (GubblePaused) return;
        TickMagicPanel(delta);
        TickFireFlash(delta);
        TickGubbleView();
        if (ViewMode >= 2 || Orbiting) PlaceDioramaRig(snap: false, (float)delta);
        else if (ViewMode == 1) _dioramaArm.SpringLength = OrbitFromM;
        UpdateShoulderArm();
        // The state line and the notice are written only when what they say changed (S12: no new string every frame).
        var held = Host?.HeldBy(Kernel.CommandHost.PlayerAvatar);
        var state = (Player.BodyHeightM, Player.WorldPhysicsId, PlayerControls.MapVersion, Companion.CompanionName, Companion.CurrentIntent,
            Companion.FloatingThere, Companion.GoalBlocked, held, Look?.Observe == true);
        if (state != _stateAsOf)
        {
            _stateAsOf = state;
            var holding = Host?.HeldName(Kernel.CommandHost.PlayerAvatar) ?? "";
            _state.Text = $"{Player.BodyHeightM * 100:0} cm player  ·  gravity {Player.WorldPhysicsId} ({K(Act.PhysicsNext)})  ·  {Companion.CompanionName}: {Companion.CurrentIntent}" +
                (Companion.FloatingThere ? " · floating there" : "") + (Companion.GoalBlocked ? " · path blocked" : "") +
                (holding.Length > 0 ? $"  ·  holding {holding} ({K(Act.Hands)})" : "") + (Look?.Observe == true ? $"  ·  observe view ({K(Act.ViewObserve)})" : "");
        }
        // An island held because its mode could not be set (M3's fix round) says why, once, without waiting for a refused change.
        if (Host is { IslandHeld: true, TeamNotice: { Length: > 0 } heldWords } && !ReferenceEquals(heldWords, _teamNoticeShown)) _noticeText = _teamNoticeShown = heldWords;
        if (!ReferenceEquals(_noticeShown, _noticeText))
        {
            _notice.Text = _noticeShown = _noticeText;
            _notice.Visible = _noticeText.Length > 0;
            NoticeLineShown(_noticeText);
        }
        UpdateFocus(delta);
        // Fractals are collected by walking into them (RoomHud.Pickup.cs).
        UpdatePickup(delta);
        BubbleCost.CloseFrame();
        var bubbleStart = BubbleCost.Begin();
        UpdateBubble(delta);
        BubbleCost.End(bubbleStart, BubbleCost.Piece.Select);
        UpdateAskTag(delta);
        UpdateSwimHint();
        UpdateChains();
        MeterCost.CloseFrame();
        var meterStart = MeterCost.Begin();
        UpdateMeters(delta);
        UpdateMinimap(delta);
        MeterCost.End(meterStart);
        UpdateDusk(delta);
        // Fire's camp: the sit's end and the expertise announcement's card (RoomHud.Camp.cs).
        TickCamp(delta);
        _washVeil.Color = new Color(_washVeil.Color, Player.HomeFade);
        RefreshKeyTextIfChanged();
        // The clock line likewise: written again when its minute, its steps, its day or a key changed.
        var moment = Look?.Moment;
        var clockInputs = (moment == null ? -1 : Mathf.PosMod(Mathf.RoundToInt(moment.Hour * 60f), 24 * 60), moment?.DayOfYear ?? -1, moment?.Season,
            TimeStop, SeasonStop, PlayerControls.MapVersion);
        if (clockInputs != _clockAsOf)
        {
            _clockAsOf = clockInputs;
            var clock = ClockText();
            _clock.Visible = clock.Length > 0;
            _clock.Text = clock;
        }
        // With the date pinned and the hour left on the real clock, the pin follows real time as the look itself would.
        if (Look != null && TimeStop < 0 && SeasonStop >= 0 && (_clockTimer += delta) > LookDirector.ClockUpdateSeconds) ApplyClock();
    }

    /// <summary>
    /// The focus, every frame: the look's highlight on the thing the next hand key would act on, in every view, and the tag
    /// beside it. None while climbing, diving or on the way home, or with nothing to act on. The host is asked again only
    /// when the player has moved, turned or picked up or put down, at most every FocusQueryS, and every FocusRefreshS anyway.
    /// </summary>
    private void UpdateFocus(double delta)
    {
        var host = Host;
        _focusAge += delta;
        if (host == null || Player.IsClimbing || Player.IsDiving || Player.GoingHome || ReadingPanelOpen || IntroPlaying)
        {
            SetFocus(null, default, "");
            _focusAge = double.MaxValue;
        }
        else
        {
            var held = host.HeldBy(Kernel.CommandHost.PlayerAvatar) ?? "";
            var changed = Player.GlobalPosition.DistanceSquaredTo(_focusFrom) > 1e-6f || Mathf.Abs(Mathf.AngleDifference(Player.GlobalRotation.Y, _focusYaw)) > 0.02f || held != _focusHeld;
            if ((changed && _focusAge >= FocusQueryS) || _focusAge >= FocusRefreshS)
            {
                _focusAge = 0;
                _focusFrom = Player.GlobalPosition;
                _focusYaw = Player.GlobalRotation.Y;
                _focusHeld = held;
                FocusQueries++;
                var focus = host.PlayerFocus();
                SetFocus(string.IsNullOrEmpty(focus.Id) ? null : host.ObjectNodeOf(focus.Id), focus.Box, focus.Tag ?? "");
            }
        }
        PlaceFocusTag();
    }

    private void SetFocus(Node3D? node, Aabb box, string tag)
    {
        if (node != null && !IsInstanceValid(node)) node = null;
        if (node != _focusNode)
        {
            _focusNode = node;
            Look?.SetFocusHighlight(node);
        }
        // The tag rides over the middle of the thing's top, as an offset from its node, so it follows a push at once.
        if (node != null) _focusTop = new Vector3(box.GetCenter().X, box.End.Y, box.GetCenter().Z) - node.GlobalPosition;
        if (node == null) tag = "";
        if (_focusTag.Text != tag) _focusTag.Text = tag;
    }

    /// <summary>Beside the thing on screen, never in the top-right corner (the minimap's); hidden when the thing is behind the view.</summary>
    private void PlaceFocusTag()
    {
        var camera = GetViewport().GetCamera3D();
        if (_focusNode == null || !IsInstanceValid(_focusNode) || camera == null || _focusTag.Text.Length == 0)
        {
            _focusTag.Visible = false;
            return;
        }
        var top = _focusNode.GlobalPosition + _focusTop;
        if (camera.IsPositionBehind(top))
        {
            _focusTag.Visible = false;
            return;
        }
        var screen = GetViewport().GetVisibleRect().Size;
        var size = _focusTag.GetCombinedMinimumSize();
        var at = camera.UnprojectPosition(top) + new Vector2(Sandbox.SandboxControls.FocusWords.TagRightPx, -Sandbox.SandboxControls.FocusWords.TagUpPx - size.Y);
        at = new Vector2(Mathf.Clamp(at.X, 0, Mathf.Max(0, screen.X - size.X)), Mathf.Clamp(at.Y, 0, Mathf.Max(0, screen.Y - size.Y)));
        if (at.X + size.X > screen.X - Sandbox.SandboxControls.FocusWords.MinimapWidthPx && at.Y < Sandbox.SandboxControls.FocusWords.MinimapHeightPx)
            at.Y = Sandbox.SandboxControls.FocusWords.MinimapHeightPx;
        _focusTag.Position = at;
        _focusTag.Visible = true;
    }

    public override void _UnhandledInput(InputEvent input)
    {
        // The controls are input actions (project.godot, [input]); Hit is a fresh press of one, not a held key repeating.
        var press = PlayerControls.Normalise(input);
        var fresh = PlayerControls.Fresh(press);
        // The playtest record sees every key first, and its own four keys work in every context (RoomHud.Playtest.cs).
        if (fresh) RecordActions(press);
        if (fresh && HandlePlaytestKeys(press)) { GetViewport().SetInputAsHandled(); return; }
        // The window's key is live in every context too (RoomHud.Screen.cs): the panels and the Gubble's eyes fill the screen as well.
        if (fresh && PlayerControls.Hit(press, Act.ViewFullscreen)) { ToggleFullscreen(); GetViewport().SetInputAsHandled(); return; }
        if (HandleMagicSpotInput(press, fresh)) { GetViewport().SetInputAsHandled(); return; }
        // The look drag (the cursor is free): once the pointer has moved LookDrag.StartPx with the button down, the cursor hides
        // and the motion turns the view as the captured mouse did; the release brings the cursor back where it was pressed.
        if (_drag.Pressed)
        {
            if (input is InputEventMouseMotion dragged)
            {
                var turn = _drag.Move(dragged.Relative, out var started);
                if (started) Input.MouseMode = Input.MouseModeEnum.Captured;
                if (_drag.Dragging)
                {
                    TurnView(turn);
                    GetViewport().SetInputAsHandled();
                }
                return;
            }
            if (PlayerControls.Released(press, Act.LookDrag))
            {
                EndDrag();
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        // Key 6 coming up ends its tap or drag, which lights Fire at once (RoomHud.FireWheel.cs).
        if (_fireKeyStart != null && PlayerControls.Released(press, Act.GubbleSlot6)) { FireKeyUp(); GetViewport().SetInputAsHandled(); return; }
        // The ask button held: the mouse is the wheel's (a flick picks a wedge), and its release is the tap or the choice.
        if (_wheel.Held)
        {
            if (input is InputEventMouseMotion held)
            {
                _wheel.Move(held.Relative);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (PlayerControls.Released(press, Act.GubbleAsk))
            {
                EndAsk();
                GetViewport().SetInputAsHandled();
                return;
            }
            // Esc cancels the wheel; every other key waits while it is held (the game is paused under it).
            if (fresh && PlayerControls.Hit(press, Act.MouseRelease))
            {
                CancelAsk();
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        if (GubblePaused) return;
        if (fresh)
        {
            // While the player looks through the Gubble's eyes, the view's keys are the only keys (RoomHud.GubbleView.cs).
            // Then the journal's: while the book is open or moving, they are the only keys (RoomHud.Journal.cs).
            if (HandleGubbleViewKeys(press) || HandleJournalKeys(press))
            {
                GetViewport().SetInputAsHandled();
                return;
            }
            // The eye icon's key opens the view from the game and from the appearance and Magic panels, as J opens the book.
            if (PlayerControls.Hit(press, Act.GubbleEyes))
            {
                OpenGubbleView();
                GetViewport().SetInputAsHandled();
                return;
            }
            // Esc closes the panel and calls off a drag; the cursor is never captured, so there is nothing to free.
            if (PlayerControls.Hit(press, Act.MouseRelease))
            {
                if (SettingsOpen) ToggleSettings();
                if (MagicPanelOpen) SetMagicPanelOpen(false);
                EndDrag();
                // Esc lets go of the bubble the smart ask chose (RoomHud.Bubble.cs).
                ClearBubbleSelection();
            }
            if (PlayerControls.Hit(press, Act.HudCustomize)) ToggleSettings();
            // M opens and closes the Magic panel (it was only a button inside the H help), from the game or the settings panel.
            else if (PlayerControls.Hit(press, Act.HudMagic)) ToggleMagicPanel();
            if (ReadingPanelOpen) return;
            // One key does one thing, the first below that it is (Shift+T is the season before it is a T).
            if (PlayerControls.Hit(press, Act.HudHelp))
            {
                _keyHelp.Visible = !_keyHelp.Visible;
                RefreshKeyText();
            }
            else if (PlayerControls.Hit(press, Act.ViewEye)) SetViewMode(0);
            else if (PlayerControls.Hit(press, Act.ViewShoulder)) SetViewMode(1);
            else if (PlayerControls.Hit(press, Act.Lamps) && Look != null) Look.SetLamps(!Look.LampsOn);
            // The tuning keys (docs/runs/RUN-2-CHARACTERS.md, "Tuning by playing"): Shift+P before P.
            else if (PlayerControls.Hit(press, Act.BouncePrint))
                _noticeText = BounceKey.Print(Player.BodyVisual) != null ? "Your character's motion profile is in the log." : "The prototype figure has no motion profile.";
            else if (PlayerControls.Hit(press, Act.BounceStep))
                _noticeText = BounceKey.Cycle(Player.BodyVisual) is { } bounce
                    ? "Your character's bounce: x" + bounce.ToString("0.#", CultureInfo.InvariantCulture) + " of its profile."
                    : "The prototype figure has no bounce to tune.";
            else if (PlayerControls.Hit(press, Act.ViewObserve) && Look != null) Look.Observe = !Look.Observe;
            else if (PlayerControls.Hit(press, Act.SeasonStep)) StepSeason();
            else if (PlayerControls.Hit(press, Act.TimeStep)) StepTimeOfDay();
            // The Gubble's keys are the player's commands, carried out by the Gubble, on the same path as the companion's own.
            else if (PlayerControls.Hit(press, Act.GubbleAsk))
            {
                BeginAsk();
                GetViewport().SetInputAsHandled();
            }
            else if (PlayerControls.Hit(press, Act.GubbleRecall)) Recall();
            else if (PlayerControls.Hit(press, Act.GubbleStop)) StopGubble();
            else if (SlotPressed(press) is { } slot)
            {
                // 6 is Fire: its press starts a tap or a drag, its release lights it (RoomHud.FireWheel.cs).
                if (slot == GubbleMagic.FireSlot) FireKeyDown();
                else CastSlot(slot, AimNow());
            }
            // Hand keys: pick up, put down and push, as commands from the player.
            else if (PlayerControls.Hit(press, Act.Hands)) Hands();
            else if (PlayerControls.Hit(press, Act.Push)) Push();
            else if (PlayerControls.Hit(press, Act.ItemGather)) Gather();
            // Fire's camp: sit down by a lit campfire to rest, and stand again (RoomHud.Camp.cs).
            else if (PlayerControls.Hit(press, Act.Rest)) RestKey();
            // The look drag arms on the press; a click that never moves does nothing.
            else if (PlayerControls.Hit(press, Act.LookDrag))
            {
                if (DragTurnsView && !_wheel.Held) _drag.Press(GetViewport().GetMousePosition());
            }
        }
        if (ViewMode is 1 or 2 && !ReadingPanelOpen)
        {
            // The wheel zooms F2 (and the shelved F3). Zoomed out, F2's camera orbits with the look drag and the body does not turn
            // with it (SetMovementFrame).
            var steps = fresh && PlayerControls.Hit(press, Act.ViewZoomIn) ? 1 : fresh && PlayerControls.Hit(press, Act.ViewZoomOut) ? -1 : 0;
            if (steps != 0)
            {
                if (ViewMode == 1) ZoomShoulder(steps);
                else ZoomDiorama(steps);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>The look drag: armed by the drag button, turning the view once the pointer has moved.</summary>
    public LookDrag Drag => _drag;
    private readonly LookDrag _drag = new();
    /// <summary>Test seam: whether the drag button is held (events handed straight to the HUD never reach Input's own state).</summary>
    internal System.Func<bool>? DragHeldForTests { get; set; }

    /// <summary>Whether a drag turns this view: F1 and F2 near the shoulder turn the body and the eye, F2 zoomed out orbits (so does the shelved F3); the shelved F4 does not turn. Through the Gubble's eyes, it turns the Gubble's eye.</summary>
    public bool DragTurnsView => GubbleViewOpen || ViewMode != 3;

    /// <summary>A drag's motion, in pixels, as the view's turn: the body and the eye in F1 and in F2 near the shoulder, the orbit in F2 zoomed out, the Gubble's eye through its eyes.</summary>
    public void TurnView(Vector2 relative)
    {
        if (GubbleViewOpen) TurnGubbleEye(relative);
        else if (ViewMode == 2) OrbitDiorama(relative);
        else if (Orbiting) OrbitShoulder(relative);
        else if (ViewMode is 0 or 1) Player.TurnView(relative);
    }

    /// <summary>The drag ends (released, Esc, the panel, a lost focus): a drag that turned the view gives the cursor back where it was pressed.</summary>
    private void EndDrag()
    {
        if (!_drag.Pressed) return;
        var anchor = _drag.Anchor;
        if (!_drag.Release()) return;
        if (Input.MouseMode == Input.MouseModeEnum.Captured) Input.MouseMode = Input.MouseModeEnum.Visible;
        GetViewport().WarpMouse(anchor);
    }

    /// <summary>Which ability slot key (0-based) the event presses, if any.</summary>
    private static int? SlotPressed(InputEvent press)
    {
        for (var i = 0; i < Act.GubbleSlots.Length; i++)
            if (PlayerControls.Hit(press, Act.GubbleSlots[i])) return i;
        return null;
    }

    /// <summary>
    /// The room's command host (null in fixtures without one), asked for many times a frame: kept while it is still the host under
    /// the HUD's parent, and found again (by a cached path, no garbage) when it is not.
    /// </summary>
    private Kernel.CommandHost? Host
    {
        get
        {
            var parent = GetParent();
            if (_host != null && IsInstanceValid(_host) && parent != null && _host.GetParent() == parent) return _host;
            return _host = parent != null ? Kernel.CommandHost.Of(parent) : null;
        }
    }
    private Kernel.CommandHost? _host;

    /// <summary>F: pick up what the player faces, or put down what it holds (on what it faces, if that has a top).</summary>
    private void Hands() => _noticeText = HandsOnFractal() ?? Host?.PlayerHands().Message ?? "The command host is not attached; the hand keys are off.";

    /// <summary>V: push what the player faces.</summary>
    private void Push() => _noticeText = Host?.PlayerPush().Message ?? "The command host is not attached; the hand keys are off.";

    private void LoadPreferences()
    {
        var config = new ConfigFile();
        var result = config.Load(ProfilePath);
        if (result == Error.FileNotFound) return;
        if (result != Error.Ok || config.GetValue("profile", "version", 0).VariantType != Variant.Type.Int || config.GetValue("profile", "version", 0).AsInt32() != 1)
        { _noticeText = "Appearance preferences could not be loaded; defaults are active."; return; }
        var playerColor = config.GetValue("profile", "player_color", 0);
        var companionColor = config.GetValue("profile", "companion_color", 1);
        if (playerColor.VariantType == Variant.Type.Int) _playerColor = Mathf.PosMod(playerColor.AsInt32(), Palette.Length);
        if (companionColor.VariantType == Variant.Type.Int) _companionColor = Mathf.PosMod(companionColor.AsInt32(), Palette.Length);
        var usedGlow = config.GetValue("profile", "has_used_glow", false);
        if (usedGlow.VariantType == Variant.Type.Bool) HasUsedGlow = usedGlow.AsBool();
        var name = config.GetValue("profile", "companion_name", CompanionAvatar.DefaultName);
        // A profile saved while the companion was still called Wisp takes the founder's name for it, the Gubble.
        if (name.VariantType == Variant.Type.String)
            Companion.SetDisplayName(CompanionAvatar.SavedName(name.AsString()));
        Player.SetAppearance(Palette[_playerColor]);
        Companion.SetAppearance(Palette[_companionColor]);
        RestoreCharacter(config.GetValue("profile", CharacterProfileKey, CharacterChoice.PrototypeId));
    }

    /// <summary>
    /// One value written into the profile as it is on disk, the rest left as saved (an unsaved colour or name is not saved with
    /// it). A profile that does not load is left untouched: the value then lasts this session only.
    /// </summary>
    private bool SaveProfileValue(string key, Variant value)
    {
        var config = new ConfigFile();
        var loaded = config.Load(ProfilePath);
        if (loaded != Error.Ok && loaded != Error.FileNotFound) return false;
        if (loaded == Error.Ok && (config.GetValue("profile", "version", 0).VariantType != Variant.Type.Int || config.GetValue("profile", "version", 0).AsInt32() != 1)) return false;
        config.SetValue("profile", "version", 1);
        config.SetValue("profile", key, value);
        return WriteProfile(config);
    }

    /// <summary>The profile is written whole to a temporary file, then moved over the old one.</summary>
    private static bool WriteProfile(ConfigFile config)
    {
        var directory = ProfilePath.GetBaseDir();
        var temporary = ProfilePath + ".tmp";
        var prepared = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(directory)) == Error.Ok && config.Save(temporary) == Error.Ok;
        return prepared && DirAccess.RenameAbsolute(ProjectSettings.GlobalizePath(temporary), ProjectSettings.GlobalizePath(ProfilePath)) == Error.Ok;
    }
}
