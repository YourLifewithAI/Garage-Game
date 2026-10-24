using Godot;
using GarageGame.Native.Navigation;
using GarageGame.Native.Room;

namespace GarageGame.Native;

/// <summary>
/// Deterministic, game-only companion body. Its goals arrive as garage_game.command goal.set /
/// goal.stop through Kernel/CommandHost; this is not an AI adapter or an authorization service.
/// It keeps its own body profile (WorldScaleProfile.Companion), a 10 cm body like the player's since the
/// founder's decision of 6 October, but a separate object so companion upgrades can change it later; the
/// original geometric appearance does not settle final companion art.
/// With the room's navigation bound (Navigation/RoomNavigation) follow and come plan routes round
/// furniture; local steering still handles the last stretch and anything the mesh does not hold.
/// </summary>
[GlobalClass]
public partial class CompanionAvatar : SmallPlayerController
{
    public string CompanionId { get; private set; } = "local_companion";
    /// <summary>The founder's name for the companion (8 October): the Gubble. The player may rename it in the customization panel.</summary>
    public const string DefaultName = "the Gubble";
    /// <summary>The name the companion had before it was the Gubble; a saved profile still holding it takes the new default.</summary>
    public const string FormerDefaultName = "Wisp";
    /// <summary>A saved name (a profile, a room's save): the former default becomes the Gubble; any other name is the player's choice.</summary>
    public static string SavedName(string saved) => saved == FormerDefaultName ? DefaultName : saved;

    /// <summary>
    /// Sets or clears the aura colour, carried as the node meta LookDirector.AuraColorMeta that the look reads when a glow starts
    /// (docs/look/GLOW.md): restart a glow to recolour it. A drawn body (the Gubble's, K5) takes it at once: its bubble's rim and
    /// its sparkles (CharacterLook.Recolor); cleared, they go back to the drawing's own aura colour.
    /// </summary>
    public void SetAuraColor(Color? color)
    {
        AuraColor = color;
        if (color is { } c) SetMeta(GarageGame.Native.Look.LookDirector.AuraColorMeta, c);
        else if (HasMeta(GarageGame.Native.Look.LookDirector.AuraColorMeta)) RemoveMeta(GarageGame.Native.Look.LookDirector.AuraColorMeta);
        if (BodyVisual?.Root is { } drawn && GodotObject.IsInstanceValid(drawn)) GarageGame.Native.Look.CharacterLook.Recolor(drawn, this);
    }
    public string CompanionName { get; private set; } = DefaultName;
    /// <summary>The name tag over the body: the name with a capital, as a name stands on its own ("The Gubble").</summary>
    public string NameTag => CompanionName.Length > 0 ? char.ToUpperInvariant(CompanionName[0]) + CompanionName[1..] : CompanionName;
    /// <summary>The Gubble's aura colour (the family's note), if it has one: its glow takes it. Null: the look's warm white-gold.</summary>
    public Color? AuraColor { get; private set; }
    public string CurrentIntent { get; private set; } = "stay";
    public bool GoalBlocked { get; private set; }
    public bool IsPointing => _pointer != null && _pointer.Visible;
    /// <summary>
    /// Counts every change of goal (follow, stay, come, stop, look, point), so the command host can tell the goal it
    /// set from a newer one and finish or cancel that goal's job honestly.
    /// </summary>
    public int IntentSerial { get; private set; }
    /// <summary>The IntentSerial of the last come that arrived beside the player (the body then stays); -1 before any.</summary>
    public int ComeArrivedSerial { get; private set; } = -1;
    /// <summary>The IntentSerial of the last go_to that arrived (the body then stays); -1 before any.</summary>
    public int GoToArrivedSerial { get; private set; } = -1;
    /// <summary>The box a go_to walks to (meaningful while CurrentIntent is "go_to").</summary>
    public Aabb GoToTarget => _goToTarget;
    /// <summary>Whether the body is turned toward a look or point target.</summary>
    public bool HasLookTarget => _hasLookTarget;
    /// <summary>The point the body looks or points at (meaningful while HasLookTarget).</summary>
    public Vector3 LookTarget => _lookTarget;
    /// <summary>How closely a look or point must face its target to count as arrived (about 3 degrees).</summary>
    public const float FacingToleranceRad = 0.05f;
    /// <summary>
    /// True while looking or pointing and turned to face the target within FacingToleranceRad, or standing so close
    /// that there is nothing to turn to: the arrival of a look_at or point_at goal.
    /// </summary>
    public bool FacesLookTarget
    {
        get
        {
            if (!_hasLookTarget) return false;
            var direction = Planar(_lookTarget - GlobalPosition);
            if (direction.LengthSquared() <= 0.001f) return true;
            return Mathf.Abs(Mathf.AngleDifference(Rotation.Y, Mathf.Atan2(-direction.X, -direction.Z))) <= FacingToleranceRad;
        }
    }
    protected override WorldScaleProfile Profile => WorldScaleProfile.Companion;

    // Loose follow (founder playtest, 6 October: "it always moves directly behind me"). Distances are planar,
    // centre to centre: the 10 cm companion (2 cm radius) keeps a comfortable band beside the 10 cm player's line
    // of travel instead of a point rigidly attached behind it, which swung behind on every turn. Every distance
    // below was scaled from the 0.24 m body (about ×0.42) when the companion shrank to the player's size.
    /// <summary>Closer than this and the companion eases out to its place (was 0.30 m for the 0.24 m body).</summary>
    public const float FollowNearM = 0.12f;
    /// <summary>Farther than this and it closes in (was 0.65 m).</summary>
    public const float FollowFarM = 0.30f;
    /// <summary>Its place: this far to the side of the player's line of travel (was 0.40 m) ...</summary>
    public const float FollowSideM = 0.16f;
    /// <summary>... and this far ahead, so it stays in view of the over-the-shoulder camera (was 0.08 m).</summary>
    public const float FollowLeadM = 0.04f;
    /// <summary>Come stops this far from the player (was 0.32 m).</summary>
    public const float ComeArrivalM = 0.14f;
    /// <summary>Outside follow and stop, the companion steps aside when the player comes this close (was 0.20 m).</summary>
    public const float YieldM = 0.08f;
    /// <summary>Speed per metre of distance to its place; the player's own velocity is added while it moves.</summary>
    public const float FollowGainPerS = 3.0f;
    /// <summary>The slowest purposeful approach, so the last centimetres never creep (was 0.15 m/s).</summary>
    public const float MinApproachMps = 0.08f;
    public const float CompanionTurnRateRadPerS = 8.0f;
    /// <summary>A route is planned again at least this often while it is in use (a long one less often: RouteRefreshFor).</summary>
    public const double RouteRefreshS = 0.1;
    /// <summary>
    /// Routes longer than this (today's islands are about 7 m across) are planned again less often, in proportion to their
    /// length, up to LongRouteRefreshS, and only once the goal has moved farther, in the same proportion (packet S7: on the
    /// magnified island a search takes milliseconds of a worker thread, and a goal 40 m off moving 3 cm changes nothing).
    /// </summary>
    public const float LongRouteM = 4f;
    public const double LongRouteRefreshS = 1.0;
    /// <summary>
    /// Routes are searched on a worker thread (RoomNavigation.RequestRoute). With no route to steer by meanwhile (the first
    /// for a goal, or one planned on an older bake), or a short one to plan (within LongRouteM), the tick waits this long for
    /// it. On a small map (RoomNavigation.IsSmall: today's rooms) it waits for the search, however long (they take well under a
    /// millisecond), so the route is taken in the tick it is asked for, as before, and a run plays out the same every time.
    /// </summary>
    public const double RouteWaitMs = 2;

    /// <summary>How long a tick waits for a search it needs (see RouteWaitMs).</summary>
    private double RouteWait => Navigation is { IsSmall: true } ? 1000 : RouteWaitMs;
    /// <summary>A route corner closer than this counts as reached (was 0.05 m).</summary>
    public const float CornerReachedM = 0.025f;
    /// <summary>A planned route this much longer than the straight line is a real detour round something (was 0.05 m).</summary>
    public const float DetourM = 0.03f;
    /// <summary>Trying to move for this long without covering StuckDistanceM reports the goal blocked.</summary>
    public const double StuckAfterS = 1.0;
    /// <summary>Was 0.03 m for the 0.24 m body.</summary>
    public const float StuckDistanceM = 0.015f;
    /// <summary>How far ahead local steering probes for support and collisions (was 0.14 m).</summary>
    public const float SteeringProbeM = 0.06f;

    /// <summary>
    /// Body speeds. The run must outpace the player's 0.96 m/s run, and Quick feet at full rank (1.152 m/s), so follow can close a gap
    /// while the player runs: 1.40 m/s is 1.46× the plain run and 1.22× the quickest (the founder, 15 October: about 1.4; the 0.24 m
    /// body ran at 1.65 m/s). Accelerations keep the player's 0.16 s ramp to full run (1.40 / 0.16 = 8.75 m/s²).
    /// Step, floor snap and jump are the player's (the 0.24 m body stepped 4 cm and snapped 2.5 cm).
    /// </summary>
    public const float CompanionWalkMps = 0.32f;
    public const float CompanionRunMps = 1.40f;
    public const float CompanionGroundAccelerationMps2 = 8.75f;
    public const float CompanionAirAccelerationMps2 = 2.5f;

    /// <summary>Whether follow is moving the body (false while it rests inside its band).</summary>
    public bool FollowMoving => _following;
    /// <summary>+1 when the companion keeps to the right of the player's line of travel, -1 for the left.</summary>
    public float FollowSide => _followSide;
    /// <summary>The room's walkable map, or null for local steering only.</summary>
    public RoomNavigation? Navigation { get; private set; }
    /// <summary>True on a tick the companion steered along a planned route round something in its way.</summary>
    public bool FollowingRoute { get; private set; }
    /// <summary>The corners of the route last planned (empty when none).</summary>
    public Vector3[] RoutePoints => _route.Points;
    /// <summary>The bake (RoomNavigation.Revision) the route in use was planned on.</summary>
    public int RouteRevision => _routeRevision;
    /// <summary>True on a tick the companion held still because the route it needs was still being searched for.</summary>
    public bool RoutePending => _routePending;

    // The Gubble floats (founder, 8 October: "a ghost bubble, set apart from the world"). It walks the planned route while
    // that reaches its goal, hovering over the ground; when the walk cannot take it there (up a cliff, across water, a long way
    // round), it floats straight there instead, rising over what is in its way and never through it.
    /// <summary>A walking route counts as reaching only if it ends this level with its goal (a cliff top over its foot does not).</summary>
    public const float WalkLevelM = 0.06f;
    /// <summary>A walk this many times the straight way (and FloatDetourM more) is too long a way round: it floats instead.</summary>
    public const float FloatDetourFactor = 2.5f;
    public const float FloatDetourM = 0.5f;
    /// <summary>Within this height of the player (feet to feet), the companion is level with them: follow rests, come arrives.</summary>
    public const float LevelGapM = 0.12f;
    /// <summary>True while the companion floats straight to its goal instead of walking a route.</summary>
    public bool FloatingThere => _floating;
    /// <summary>How many times the companion has chosen to float rather than walk.</summary>
    public int FloatStarts { get; private set; }
    /// <summary>The point of the go_to box the companion makes for: the side it can reach (meaningful while CurrentIntent is "go_to").</summary>
    public Vector3 GoToAim => _goToAim;

    /// <summary>Whether it holds a height it came to rest at while floating (beside a player up a tree, aloft beside a climber).</summary>
    public bool HoldingHeight => _holdY != null;
    /// <summary>
    /// A come that arrives by floating holds its height beside the player (ArriveFloating). Once the player has moved this far
    /// from where they stood (or LevelGapM up or down), there is nobody to be beside up there: the hold goes and the Gubble,
    /// staying, settles over what is under it (the founder on the magnified island, 13 October: it stayed high in the sky).
    /// </summary>
    public const float HoldReleaseM = FollowFarM;
    /// <summary>Times a held height was let go because the player moved away from it.</summary>
    public int HoldReleases { get; private set; }

    /// <summary>
    /// The watchdog for "stuck up high" (the founder on the magnified island, 13 October: the Gubble ended up high in the sky,
    /// saying it could not get there). Floating more than LevelGapM over its goal and moving less than StuckDistanceM in
    /// StuckAfterS (WatchHeight), something it hovers on holds it up (a roof over the player, a ledge over their head), or a height
    /// it was holding. It lets the height go and makes for open air beside the goal (LandingBeside): there it sinks, and walks the
    /// rest. At most MaxLandings times for one goal; with nowhere to come down it says blocked, and the host gives up honestly.
    /// </summary>
    public int HighReplans { get; private set; }
    public const int MaxLandings = 2;
    /// <summary>Where it is coming down beside its goal after the watchdog found it stuck up high (null when not).</summary>
    public Vector3? Landing => _landing;

    // ---- Play distances (packet S8 of the 10x world, 13 October): the room magnified ten times round the same body. Every
    // number here is provisional, for the founder to tune by feel; each says why it is the size it is.

    /// <summary>
    /// The Gubble catching up (RUN-2-TEN-TIMES.md, the founder's decision 4, its recommended default): following or coming to the
    /// player, farther than this from them in a straight line, and out of the player's view, it reappears beside them (a ghost
    /// may), at a spot the view does not hold either, so nothing pops on screen at either end. Room-sized: on the 10x island it is
    /// about 20 s of the Gubble's run; today's islands (about 7 m across) never reach it. Never in a fetch it is carrying out
    /// (Come(catchUp: false)), never into a wall (the body must fit) or water, and never where no walk joins the spot to the player.
    /// </summary>
    public const float CatchUpM = 25f;
    /// <summary>The catch-up distance in use (CatchUpM; tests and tuning; PositiveInfinity turns catching up off).</summary>
    public float CatchUpBeyondM { get; set; } = CatchUpM;
    /// <summary>How often a Gubble far from the player looks whether it may catch up (a few rays and one short route search).</summary>
    public const double CatchUpCheckS = 0.5;
    /// <summary>
    /// The rings round the player where a catch-up may set the Gubble down, nearest first: from just behind the shoulder camera
    /// (32 cm behind the player) out past where the high views' cameras stand (1.4 and 2.7 m), so some spot is out of every view.
    /// Body-sized: beside the player whatever the room.
    /// </summary>
    public static readonly float[] CatchUpRingsM = { 0.5f, 0.75f, 1.0f, 1.5f, 2.0f, 3.0f, 4.0f };
    /// <summary>Times the Gubble caught up with the player.</summary>
    public int CatchUps { get; private set; }
    /// <summary>The last catch-up: where it was and where it reappeared.</summary>
    public (Vector3 From, Vector3 To)? LastCatchUp { get; private set; }
    /// <summary>Raised on a catch-up with where it was and where it reappeared: the host tells the AI (a caught_up notice).</summary>
    public event System.Action<Vector3, Vector3>? CaughtUp;

    /// <summary>
    /// Rising up a tall face (the 10x island's mesas stand 10 to 25 m; at FloatRiseMps, 0.3 m/s, a 20 m face took over a minute).
    /// A face no taller than this rises at FloatRiseMps as before: today's islands' land stands under 3 m (HoverSupportSearchM).
    /// </summary>
    public const float TallFaceM = HoverSupportSearchM;
    /// <summary>The fastest rise up a tall face: a face this many times TallFaceM tall (and more) rises at it (provisional).</summary>
    public const float TallFaceRiseMps = 1.5f;
    /// <summary>Near the top the rise eases: this much speed for each metre still to rise, never below FloatRiseMps.</summary>
    public const float RiseEasePerS = 1.0f;
    /// <summary>
    /// The rise speed up a face faceM tall with remainingM still to rise: baseMps for a face within TallFaceM (unchanged), else in
    /// proportion to the face's height up to TallFaceRiseMps, easing to baseMps over the last metre or so. A 20 m face takes about
    /// 15 s (was 67).
    /// </summary>
    public static float RiseMpsFor(float remainingM, float faceM, float baseMps)
    {
        if (!(faceM > TallFaceM) || !(remainingM > 0)) return baseMps;
        var cruise = Mathf.Max(baseMps, Mathf.Min(TallFaceRiseMps, baseMps * faceM / TallFaceM));
        return Mathf.Clamp(remainingM * RiseEasePerS, baseMps, cruise);
    }
    /// <summary>The face in the way of the last rise: its height from where the rise began, and how much of it was still to rise.</summary>
    public float RiseFaceM { get; private set; }
    public float RiseRemainingM { get; private set; }
    /// <summary>The fastest rise up a face so far (metres a second; tests).</summary>
    public float FastestRiseMps { get; private set; }

    /// <summary>Why it last chose to float rather than walk (diagnostics and the long-trip measures): no_route, out_of_reach, goal_off_level, start_off_level or long_way, with the number that decided it, or stuck_walking (a long trip's walk got nowhere).</summary>
    public string FloatReason { get; private set; } = "";
    /// <summary>Times a float went back to walking because a walk from where it was then reached the goal (a long trip, S8).</summary>
    public int FloatEnds { get; private set; }
    /// <summary>
    /// Floating on a long trip, it asks this often whether a walk from where it is now reaches the goal, and walks it if so (it used
    /// to float the rest of the way once it had started: a 72 m come on the 10x island walked 6 s and floated 73).
    /// </summary>
    public const double FloatRecheckS = 1.0;

    private SmallPlayerController? _player;
    private Node3D _pointer = null!;
    private Label3D _label = null!;
    private const float NameTagHeightFraction = 0.025f;
    /// <summary>The name tag's dark outline, in label pixels: thick enough to read over a busy landscape (the Glow playtest).</summary>
    public const int NameTagOutline = 10;
    private const float NameTagHideWithinM = 0.25f;
    private Vector3 _lookTarget;
    private Aabb _goToTarget;
    private float _goToArrivalM;
    private bool _hasLookTarget;
    private bool _entered;
    private bool _following;
    private float _followSide = 1.0f;
    private Vector3 _travel = Vector3.Forward;
    private RoomNavigation.Route _route = RoomNavigation.Route.None;
    private int _routeIndex;
    private Vector3 _routeGoal;
    private double _routeAge;
    private int _routeRevision = -1;
    private RoomNavigation.RouteRequest? _routeRequest;
    private bool _routePending;
    private RoomNavigation.RouteRequest[]? _sideRequests;
    private Vector3[] _sideCandidates = System.Array.Empty<Vector3>();
    private bool _sidePending;
    private Vector3 _progressAnchor;
    private double _progressTime;
    private bool _stuck;
    private bool _floating;
    private float? _holdY;
    /// <summary>Where the player stood when a come arriving by floating began to hold its height beside them (see HoldReleaseM).</summary>
    private Vector3? _holdBeside;
    /// <summary>The goal of this tick's steering (the player, the follow place, the go_to side), for the watchdog's "above it".</summary>
    private Vector3? _goal;
    private Vector3? _landing;
    /// <summary>Set on landing beside the goal: the walk from there is taken however long it is, so it never floats back up.</summary>
    private bool _walkFromLanding;
    /// <summary>Landings made for this goal (MaxLandings).</summary>
    private int _landings;
    private Vector3 _goToAim;
    private double _goToAimAge;
    private Vector3 _playerLast;
    private Vector3 _playerMotion;
    private bool _playerSeen;
    private float _playerStillS;
    private int _goToAimRevision = -1;
    /// <summary>Whether this goal may catch up (a come for a fetch's way back may not).</summary>
    private bool _mayCatchUp = true;
    private double _catchUpAge;
    /// <summary>FloatRiseMps and FloatSinkMps as set before the body entered the tree: the rise up a face, and the sink down a drop, within TallFaceM.</summary>
    private float _baseRiseMps;
    private float _baseSinkMps;
    /// <summary>The tallest the drop of a long way down has been (null while not coming down one taller than TallFaceM).</summary>
    private float? _dropFromM;
    /// <summary>The height a rise up a face began at (null while not rising), and how long since the body last rose over something.</summary>
    private float? _riseFromY;
    private double _sinceRiseS;
    /// <summary>Floating: time since it last asked whether a walk reaches, and whether the route in hand is the answer to that ask.</summary>
    private double _floatRecheckAge;
    private bool _recheckTaken;

    public override void _Ready()
    {
        ReadKeyboard = false;
        // The Gubble never climbs, swims or walks on the ground (founder, 8 October): it floats, over water and up cliffs.
        CanClimb = false;
        CanSwim = false;
        Floats = true;
        WalkSpeedMps = CompanionWalkMps;
        RunSpeedMps = CompanionRunMps;
        GroundAccelerationMps2 = CompanionGroundAccelerationMps2;
        AirAccelerationMps2 = CompanionAirAccelerationMps2;
        _baseRiseMps = FloatRiseMps;
        _baseSinkMps = FloatSinkMps;
        SetAppearance(new Color("65b9b0"));
        base._Ready();
        CollisionLayer = 4;
        CollisionMask = RoomBuilder.BodyMask | 2;
        _entered = true;
        // The label floats a little above the body; the pointing cue comes from chest height.
        // The name tag is drawn solid, with an alpha cut (founder playtest, 6 October): a see-through tag writes no
        // depth, so depth of field read the wall behind it and blurred it in F2, and writes no motion, so temporal
        // anti-aliasing smeared and doubled it while the camera moved in F3. Cut out, it is an object like the body.
        var h = BodyHeightM;
        _label = new Label3D
        {
            Name = "CompanionLabel", Text = NameTag,
            Position = Vector3.Up * (h * 1.35f), FontSize = 30, OutlineSize = NameTagOutline, PixelSize = 0.0003f, FixedSize = true,
            Modulate = new Color("f6dfab"), OutlineModulate = new Color("18332d"),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = false,
            AlphaCut = Label3D.AlphaCutMode.Discard, AlphaScissorThreshold = 0.5f
        };
        AddChild(_label);
        _pointer = new Node3D { Name = "PointingCue", Position = Vector3.Up * (h * 0.667f), Visible = false };
        AddChild(_pointer);
        var gold = new StandardMaterial3D { AlbedoColor = new Color("edc06e"), Roughness = 0.8f };
        var shaft = AddMesh(_pointer, new CylinderMesh { TopRadius = h * 0.021f, BottomRadius = h * 0.021f, Height = h * 0.71f, RadialSegments = 8 },
            new Vector3(0, 0, -h * 0.375f), gold);
        shaft.Rotation = new Vector3(-Mathf.Pi * 0.5f, 0, 0);
        var tip = AddMesh(_pointer, new CylinderMesh { TopRadius = 0, BottomRadius = h * 0.079f, Height = h * 0.167f, RadialSegments = 8 },
            new Vector3(0, 0, -h * 0.8125f), gold);
        tip.Rotation = new Vector3(-Mathf.Pi * 0.5f, 0, 0);
    }

    public override void _Process(double delta)
    {
        UpdateGesture(delta);
        var camera = GetViewport().GetCamera3D();
        if (camera == null) { _label.Visible = false; return; }
        var cameraTransform = camera.GetCameraTransform();
        _label.Visible = GetGlobalTransformInterpolated().Origin.DistanceTo(cameraTransform.Origin) >= NameTagHideWithinM;
        if (!_label.Visible) return;

        // FixedSize draws as though the tag were one metre away, but FOV still changes its size.
        // Project a 2.5%-high screen segment at that depth to keep F2, F3 and F4 equally readable.
        var screen = GetViewport().GetVisibleRect();
        var centre = screen.GetCenter();
        var halfHeight = Vector2.Down * (screen.Size.Y * NameTagHeightFraction * 0.5f);
        var heightM = camera.ProjectPosition(centre - halfHeight, 1).DistanceTo(camera.ProjectPosition(centre + halfHeight, 1));
        // Include the outline in the budget, including after a display-name change.
        var labelHeightM = _label.GetAabb().Size.Y + 2 * _label.OutlineSize * _label.PixelSize;
        if (labelHeightM > 0) _label.Scale = Vector3.One * (heightM / labelHeightM);
    }

    /// <summary>The Gubble's body is on its own layer, which its eye leaves out (the AI's pictures, the player's view through it).</summary>
    protected override uint OwnBodyLayer => GarageGame.Native.Look.EyeView.GubbleBodyLayer;

    protected override void BuildVisual()
    {
        base.BuildVisual();
        // The hat: a cone as wide as the body on top of the head, a third of the body's height (0.075 m on 0.24 m).
        var trim = new StandardMaterial3D { AlbedoColor = new Color("eec471"), Roughness = 0.9f };
        var hat = BodyHeightM * 0.3125f;
        AddMesh(VisualRoot, new CylinderMesh
        {
            TopRadius = 0, BottomRadius = BodyRadiusM, Height = hat, RadialSegments = 12
        }, new Vector3(0, BodyHeightM - hat * 0.5f, 0), trim);
    }

    public bool ConfigureIdentity(string id)
    {
        if (_entered || string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-') return false;
        CompanionId = id;
        return true;
    }

    public void SetDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) return;
        foreach (var c in name) if (char.IsControl(c)) return;
        CompanionName = name.Trim();
        if (_label != null) _label.Text = NameTag;
    }

    public void BindPlayer(SmallPlayerController player)
    {
        if (_player != null && GodotObject.IsInstanceValid(_player))
        {
            _player.WentHome -= PlayerWentHome;
            _player.Wrapped -= PlayerWrapped;
        }
        _player = player;
        player.WentHome += PlayerWentHome;
        player.Wrapped += PlayerWrapped;
    }

    /// <summary>When the player wraps round the sea, a Gubble this close to them (or following them) comes too: never stranded across the seam.</summary>
    public const float WrapWithPlayerWithinM = 2.0f;
    /// <summary>Times the Gubble wrapped round with the player.</summary>
    public int WrappedWithPlayer { get; private set; }

    /// <summary>
    /// The player wrapped round the sea by move: following them, or within WrapWithPlayerWithinM of where they were, the Gubble
    /// moves the same, keeping its place beside them (and what it holds rides with it). Otherwise it stays, and a come or a fetch
    /// still finds the player where they are now.
    /// </summary>
    private void PlayerWrapped(SmallPlayerController player, Vector3 move)
    {
        _playerLast += move;
        if (!IsInsideTree() || !GodotObject.IsInstanceValid(player)) return;
        if (CurrentIntent != "follow" && GlobalPosition.DistanceTo(player.GlobalPosition - move) > WrapWithPlayerWithinM) return;
        GlobalPosition += move;
        ResetPhysicsInterpolation();
        _progressAnchor += move;
        _route = RoomNavigation.Route.None;
        _routeRequest = null;
        _goToAimRevision = -1;
        _sideRequests = null;
        WrappedWithPlayer++;
    }

    /// <summary>
    /// The player went home (B, or the far net): the Gubble comes too (the founder's island: it floats over the sea and is
    /// never lost). Following or coming to the player, or out past the reef itself, it appears beside them, on whichever side
    /// has room; with none, it floats there.
    /// </summary>
    private void PlayerWentHome(SmallPlayerController player)
    {
        if (!IsInsideTree() || !GodotObject.IsInstanceValid(player)) return;
        var outAtSea = Sea != null && Sea.PastReefM(new Vector2(GlobalPosition.X, GlobalPosition.Z)) > 0;
        if (CurrentIntent is not ("follow" or "come") && !outAtSea) return;
        var right = player.GlobalBasis.X;
        var forward = -player.GlobalBasis.Z;
        // Beside them on whichever side has room, then a little farther round (Codex Sol's review: with the four spots by a
        // crowded jetty taken, the Gubble was left out at sea for good); with nowhere to stand, floating over their head.
        if (!PlaceNearHome(player))
        {
            // Nowhere at all near home (Codex Sol's review: tall things all round it): it keeps trying while the player stays.
            _homePending = true;
            return;
        }
        _route = RoomNavigation.Route.None;
        _floating = false;
        // A height it held out there (resting aloft, an arrival by floating) means nothing at home.
        FloatAltitudeFloorY = _holdY = null;
        _holdBeside = _landing = null;
        if (CurrentIntent == "follow") _following = false;
        WentHomeWithPlayer++;
    }

    /// <summary>
    /// Beside the player on whichever side has room, then farther round out to a metre; with nowhere to stand, floating over
    /// their head or over one of those spots; last, at its own spawn.
    /// </summary>
    private bool PlaceNearHome(SmallPlayerController player)
    {
        var right = player.GlobalBasis.X;
        var forward = -player.GlobalBasis.Z;
        foreach (var ring in new[] { FollowSideM, 0.25f, 0.35f, 0.5f, 0.75f, 1.0f })
            for (var k = 0; k < 8; k++)
            {
                var angle = k * Mathf.Pi / 4;
                if (TryTeleportTo(player.GlobalPosition + (right * Mathf.Cos(angle) + forward * Mathf.Sin(angle)) * ring)) return true;
            }
        foreach (var ring in new[] { 0.0f, FollowSideM, 0.35f, 0.75f })
            for (var k = 0; k < (ring == 0 ? 1 : 8); k++)
            {
                var angle = k * Mathf.Pi / 4;
                var spot = player.GlobalPosition + (right * Mathf.Cos(angle) + forward * Mathf.Sin(angle)) * ring;
                for (var up = 1; up <= 6; up++)
                    if (TryPlaceFloating(spot + Vector3.Up * (player.BodyHeightM * up + HoverHeightM))) return true;
            }
        return TryTeleportTo(SpawnPoint) || TryPlaceFloating(SpawnPoint + Vector3.Up * HoverHeightM);
    }

    private bool _homePending;
    private double _homePendingAge;

    /// <summary>Times the Gubble came home with the player (B, the far net).</summary>
    public int WentHomeWithPlayer { get; private set; }

    /// <summary>Give the companion the room's navigation (or none): follow and come then route round obstacles.</summary>
    public void BindNavigation(RoomNavigation? navigation)
    {
        Navigation = navigation;
        _route = RoomNavigation.Route.None;
        _routeRequest = null;
        _sideRequests = null;
    }
    public void Follow() => BeginIntent("follow");
    public void Stay() => BeginIntent("stay");
    /// <summary>Come to the player. catchUp false (a fetch's way back with the thing): it never catches up, it travels the whole way.</summary>
    public void Come(bool catchUp = true)
    {
        BeginIntent("come");
        _mayCatchUp = catchUp;
    }
    public void Stop() => BeginIntent("stop");

    /// <summary>
    /// Walk to a box (a thing's bounds, or a place as an empty box), routing round furniture as come does, and stop
    /// once the body's centre is within arrivalM of the box's footprint. The command host's go_to (and A2's fetch)
    /// watch GoToArrivedSerial; a body that cannot get there reports GoalBlocked, honestly.
    /// </summary>
    public void GoTo(Aabb target, float arrivalM)
    {
        if (!target.Position.IsFinite() || !target.Size.IsFinite()) return;
        BeginIntent("go_to");
        _goToTarget = target.Abs();
        _goToArrivalM = Mathf.Max(0.005f, arrivalM);
        _goToAimRevision = -1;
        _sideRequests = null;
    }

    private void BeginIntent(string intent)
    {
        IntentSerial++;
        CurrentIntent = intent;
        GoalBlocked = false;
        _hasLookTarget = false;
        _following = false;
        _route = RoomNavigation.Route.None;
        _routeRequest = null;
        _stuck = false;
        _progressTime = 0;
        _floating = false;
        _holdY = null;
        _holdBeside = _landing = _goal = null;
        _walkFromLanding = _highBlocked = false;
        _landings = 0;
        _lastLandingAt = _tripGoal = null;
        _noProgress = false;
        _mayCatchUp = true;
        _floatRecheckAge = 0;
        _recheckTaken = _floatWhenStuck = false;
        _riseFromY = null;
        if (_pointer != null) _pointer.Visible = false;
        SetControlInput(Vector2.Zero);
        Velocity = new Vector3(0, Velocity.Y, 0);
        if (intent == "follow" && HasPlayer())
        {
            // Start from the player's facing and keep whichever side the companion is already on.
            _travel = Planar(-_player!.GlobalBasis.Z);
            _travel = _travel.LengthSquared() < 0.0001f ? Vector3.Forward : _travel.Normalized();
            var lateral = Planar(GlobalPosition - _player.GlobalPosition).Dot(_travel.Cross(Vector3.Up));
            _followSide = lateral < 0 ? -1.0f : 1.0f;
        }
    }

    private bool HasPlayer() => _player != null && GodotObject.IsInstanceValid(_player) && _player.IsInsideTree() && IsInsideTree();

    public void LookAtPoint(Vector3 point)
    {
        if (!point.IsFinite()) return;
        BeginIntent("look");
        _lookTarget = point;
        _hasLookTarget = true;
    }

    public void PointAt(Vector3 point)
    {
        if (!point.IsFinite()) return;
        LookAtPoint(point);
        CurrentIntent = "point";
        if (_pointer != null) _pointer.Visible = true;
    }

    // ---- Gestures: the Gubble's body answers (the magic design's "What the player sees"), moved here from the HUD ----

    /// <summary>The body's gestures. Each moves only the drawn body (its parts), never the avatar or its collider.</summary>
    public enum Gesture { None, Shake, Shiver, Wiggle, Shrug, Cast, CastSelf }

    public const double ShakeS = 0.6;
    public const double ShiverS = 1.2;
    public const double WiggleS = 0.45;
    public const double ShrugS = 0.6;
    /// <summary>The cast: a quick lean toward the spot (a toss) that peaks at CastPeakFraction, then settles; the look's spark leaves at its start. The Bubble is cast like Glow, one bubble at a spot (decision 55): no longer lean blowing a stream.</summary>
    public const double CastS = 0.55;
    /// <summary>Fireworks: a quick toss as the rocket leaves (the look's rocket leaves at once and bursts 0.35 to 1 s later).</summary>
    public const double TossS = 0.4;
    /// <summary>How long the cast gesture plays for each ability category (the look's timing, docs/look/EFFECTS.md).</summary>
    public static double CastSeconds(string category) => category switch { "burst" => TossS, _ => CastS };
    /// <summary>The category of the last cast gesture (its timing).</summary>
    public string LastCastCategory { get; private set; } = "";
    public const float CastPeakFraction = 0.25f;
    /// <summary>How far the body leans toward the spot it casts at, in radians.</summary>
    public const float CastLeanRad = 0.38f;
    /// <summary>A glow on itself: the body lifts this many body heights and settles.</summary>
    public const float CastLiftHeights = 0.14f;
    /// <summary>How dark the body goes when it dims (an overlay's alpha).</summary>
    public const float DimAlpha = 0.45f;

    /// <summary>The gesture playing now, and the last one played.</summary>
    public Gesture Playing { get; private set; }
    public Gesture LastGesture { get; private set; }
    /// <summary>Where the last cast gesture leaned (the spot), or null for a glow on itself.</summary>
    public Vector3? LastCastToward { get; private set; }
    public bool Dimmed { get; private set; }
    /// <summary>How far through the gesture playing it is, 0 to 1 (0 with none): a drawn Gubble's arms follow it (K5).</summary>
    public float GestureProgress { get; private set; }
    /// <summary>
    /// The gesture's transform this frame, in the visual root's space, about the body's middle (or its feet for a cast), identity
    /// with none. With the prototype figure it moves the visual root's children; with a drawn body (BodyVisual) nothing here moves
    /// them, and the drawn Gubble's gesture layer adds it to its root bone (Characters/Gubble, K5).
    /// </summary>
    public Transform3D GestureOffset { get; private set; } = Transform3D.Identity;

    private double _gestureAge;
    private double _gestureS;
    private Vector3 _castDirection = Vector3.Forward;
    private readonly System.Collections.Generic.List<(Node3D Node, Transform3D Pose)> _rest = new();
    private readonly System.Collections.Generic.List<(GeometryInstance3D Mesh, Material? Before)> _dimmed = new();
    // One per body, never static: a Godot object held by a static field outlives the engine's C# bindings at exit (a fatal error).
    private readonly StandardMaterial3D DimOverlay = new()
    {
        ResourceName = "gubble dim", AlbedoColor = new Color(0.02f, 0.03f, 0.05f, DimAlpha), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    /// <summary>A refusal: side to side about its upright, no.</summary>
    public void Shake() => Play(Gesture.Shake, ShakeS);
    /// <summary>The dusk moment: a fast small tremble.</summary>
    public void Shiver() => Play(Gesture.Shiver, ShiverS);
    /// <summary>Done: a happy bounce.</summary>
    public void Wiggle() => Play(Gesture.Wiggle, WiggleS);
    /// <summary>Not yet: up and down once.</summary>
    public void Shrug() => Play(Gesture.Shrug, ShrugS);

    /// <summary>
    /// The cast (the Glow playtest: "the Gubble placing the glowing effect"): a quick lean toward the spot, a toss, or a lift for
    /// a glow on itself (toward null). Call it as the cast starts: the look's spark leaves the body then (StartGlow), and the
    /// lean peaks CastPeakFraction into the gesture, so the body follows through as the spark flies.
    /// </summary>
    public void CastGesture(Vector3? toward, string category = "light")
    {
        LastCastToward = toward;
        LastCastCategory = category;
        var direction = toward is { } spot && spot.IsFinite() && IsInsideTree() ? ToLocal(spot) : Vector3.Zero;
        direction.Y = 0;
        _castDirection = direction.LengthSquared() > 1e-6f ? direction.Normalized() : Vector3.Forward;
        Play(toward == null ? Gesture.CastSelf : Gesture.Cast, CastSeconds(category));
    }

    /// <summary>Dim or undim the body: an overlay on its drawn meshes.</summary>
    public void Dim(bool on)
    {
        if (on == Dimmed) return;
        Dimmed = on;
        if (on && VisualRoot != null)
        {
            foreach (var node in VisualRoot.FindChildren("*", "GeometryInstance3D", true, false))
                if (node is GeometryInstance3D mesh)
                {
                    _dimmed.Add((mesh, mesh.MaterialOverlay));
                    mesh.MaterialOverlay = DimOverlay;
                }
            return;
        }
        foreach (var (mesh, before) in _dimmed)
            if (GodotObject.IsInstanceValid(mesh) && mesh.MaterialOverlay == DimOverlay) mesh.MaterialOverlay = before;
        _dimmed.Clear();
    }

    private void Play(Gesture gesture, double seconds)
    {
        if (VisualRoot == null || !GodotObject.IsInstanceValid(VisualRoot)) return;
        // The prototype's parts are moved here; a drawn body poses itself from GestureOffset (its gesture layer).
        if (Playing == Gesture.None && BodyVisual == null)
        {
            _rest.Clear();
            foreach (var child in VisualRoot.GetChildren())
                if (child is Node3D part) _rest.Add((part, part.Transform));
        }
        Playing = gesture;
        LastGesture = gesture;
        _gestureAge = 0;
        _gestureS = seconds;
    }

    /// <summary>0 to 1 and back: up by CastPeakFraction of the gesture, eased down after.</summary>
    public static float CastEnvelope(float t) => t <= 0 || t >= 1 ? 0f
        : t < CastPeakFraction ? Mathf.Sin(t / CastPeakFraction * Mathf.Pi * 0.5f) : Mathf.Cos((t - CastPeakFraction) / (1f - CastPeakFraction) * Mathf.Pi * 0.5f);

    /// <summary>The pose of the gesture playing, at t (0 to 1) of it.</summary>
    private Transform3D GesturePose(float t)
    {
        var fade = 1f - t;
        var h = BodyHeightM;
        return Playing switch
        {
            Gesture.Shake => new Transform3D(new Basis(Vector3.Up, 0.45f * fade * Mathf.Sin(t * Mathf.Tau * 3f)), Vector3.Zero),
            Gesture.Shiver => new Transform3D(Basis.Identity, new Vector3(0.012f * h * Mathf.Sin(t * Mathf.Tau * 14f), 0, 0)),
            Gesture.Wiggle => new Transform3D(new Basis(Vector3.Back, 0.18f * fade * Mathf.Sin(t * Mathf.Tau * 2f)), Vector3.Up * (0.08f * h * Mathf.Sin(t * Mathf.Pi))),
            // The top tips toward the spot: a turn about up x direction. Fast in, slow out, with a little hop.
            Gesture.Cast => new Transform3D(new Basis(Vector3.Up.Cross(_castDirection).Normalized(), CastLeanRad * CastEnvelope(t)), Vector3.Up * (0.05f * h * Mathf.Sin(t * Mathf.Pi))),
            Gesture.CastSelf => new Transform3D(Basis.Identity, Vector3.Up * (CastLiftHeights * h * CastEnvelope(t))),
            _ => new Transform3D(Basis.Identity, Vector3.Up * (0.06f * h * Mathf.Sin(t * Mathf.Pi))),
        };
    }

    private void UpdateGesture(double delta)
    {
        if (Playing == Gesture.None) return;
        _gestureAge += delta;
        var t = (float)(_gestureAge / _gestureS);
        if (t >= 1f)
        {
            RestorePose();
            return;
        }
        // Turned about the body's middle, so a shake turns it in place; a cast's lean tips it about its feet.
        var pivot = Playing == Gesture.Cast ? Vector3.Zero : Vector3.Up * (BodyHeightM * 0.5f);
        var about = new Transform3D(Basis.Identity, pivot) * GesturePose(t) * new Transform3D(Basis.Identity, -pivot);
        GestureProgress = t;
        GestureOffset = about;
        // Drawn mid-gesture: the prototype's parts go back to rest at once, and the drawn body takes the rest of the gesture.
        if (BodyVisual != null) RestoreParts();
        foreach (var (node, pose0) in _rest)
            if (GodotObject.IsInstanceValid(node)) node.Transform = about * pose0;
    }

    private void RestoreParts()
    {
        foreach (var (node, pose0) in _rest)
            if (GodotObject.IsInstanceValid(node)) node.Transform = pose0;
        _rest.Clear();
    }

    private void RestorePose()
    {
        RestoreParts();
        Playing = Gesture.None;
        GestureProgress = 0;
        GestureOffset = Transform3D.Identity;
    }

    public override void _ExitTree()
    {
        RestorePose();
        Dim(false);
        base._ExitTree();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_entered) return;
        var dt = Mathf.Clamp((float)delta, 0, 0.05f);
        if (_homePending && HasPlayer() && (_homePendingAge += dt) >= 0.5)
        {
            _homePendingAge = 0;
            if (!_player!.GoingHome && PlaceNearHome(_player))
            {
                _homePending = false;
                FloatAltitudeFloorY = _holdY = null;
                _holdBeside = _landing = null;
                _floating = false;
                WentHomeWithPlayer++;
            }
        }
        GoalBlocked = false;
        FollowingRoute = false;
        // Walking hovers over the ground and ducks under what is low; only a float rises over things (set by the goals below).
        RiseOverObstacles = false;
        ReleaseHoldWhenLeft();
        FloatAltitudeFloorY = _holdY;
        _goal = null;
        var desired = Vector3.Zero;
        var hasPlayer = HasPlayer();
        // Gravity is a world property: the companion lives under the same profile the player was given.
        if (hasPlayer && _player!.WorldPhysicsRevision > WorldPhysicsRevision) SetWorldPhysics(_player.WorldPhysicsProfile);
        if (hasPlayer) TrackPlayerMotion(dt);
        if (InputEnabled && hasPlayer) CatchUp(dt);
        if (InputEnabled && hasPlayer)
        {
            var playerOffset = Planar(GlobalPosition - _player!.GlobalPosition);
            if (CurrentIntent == "follow") desired = FollowVelocity(playerOffset, dt);
            // Stay may yield, but an explicit Stop cancels navigation. The player
            // can still pass because its collision mask excludes the companion. Floating above or below the player (over
            // the wall it rose over), it is in nobody's way.
            else if (CurrentIntent != "stop" && playerOffset.Length() < YieldM && Mathf.Abs(GlobalPosition.Y - _player.GlobalPosition.Y) <= LevelGapM)
                desired = (playerOffset.LengthSquared() < 0.0001f ? GlobalBasis.X : playerOffset.Normalized()) * WalkSpeedMps;
            else if (CurrentIntent == "come") desired = ComeVelocity(playerOffset, dt);
            else if (CurrentIntent == "go_to") desired = GoToVelocity(dt);
        }
        var trying = desired.LengthSquared() > 0.0025f;
        if (desired.LengthSquared() > 0.0004f) MoveWith(desired, dt);
        else
        {
            SetControlInput(Vector2.Zero);
            // At rest beside the player, turn to face them; the player turning on the spot never moves the companion.
            if (CurrentIntent == "follow" && hasPlayer)
            {
                var toPlayer = Planar(_player!.GlobalPosition - GlobalPosition);
                if (toPlayer.LengthSquared() > 0.0001f) TurnToward(toPlayer, 0.5f * CompanionTurnRateRadPerS * dt, 0.6f);
            }
        }
        WatchProgress(dt, trying);
        WatchHeight(dt);
        WatchTrip(dt);
        if (_stuck || _highBlocked || _noProgress) Blocked(_stuck ? "stuck" : _highBlocked ? "stuck_up_high" : "no_progress");
        if (_hasLookTarget)
        {
            var direction = _lookTarget - GlobalPosition;
            direction.Y = 0;
            if (direction.LengthSquared() > 0.001f)
                Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, Mathf.Atan2(-direction.X, -direction.Z), Mathf.Min(1, (float)delta * 10)), 0);
            if (_pointer.Visible && _pointer.GlobalPosition.DistanceSquaredTo(_lookTarget) > 0.0001f)
            {
                var toTarget = (_lookTarget - _pointer.GlobalPosition).Normalized();
                if (Mathf.Abs(toTarget.Dot(Vector3.Up)) < 0.99f) _pointer.LookAt(_lookTarget, Vector3.Up);
            }
        }
        SetRiseSpeed(desired, dt);
        base._PhysicsProcess(delta);
    }

    /// <summary>
    /// Loose follow: rest anywhere inside the band. When the player walks off, or the companion is outside the
    /// band, steer to a place beside the player's line of travel on the side it is already on, adding the
    /// player's velocity so it neither lags nor oscillates. The line of travel changes only when the player
    /// moves, so turning on the spot never re-targets the companion.
    /// </summary>
    private Vector3 FollowVelocity(Vector3 playerOffset, float dt)
    {
        var distance = playerOffset.Length();
        // How the player really moves, not the velocity it asks for: pushing into a face before a climb, the asked velocity
        // flickers between a step and nothing from one tick to the next, and follow started and stopped with it.
        var playerVelocity = Planar(_playerMotion);
        var playerSpeed = playerVelocity.Length();
        var walking = playerSpeed > 0.05f;
        if (walking) _travel = playerVelocity / playerSpeed;
        // A climber going up or down the face is on the move too, though not across the ground.
        var moving = walking || (_player!.IsClimbing && Mathf.Abs(_playerMotion.Y) > 0.05f);
        // Follow rests only once the player has stood still a moment: a player edging into a face moves every other tick.
        _playerStillS = moving ? 0 : _playerStillS + dt;
        var still = _playerStillS >= PlayerStillS;
        var right = _travel.Cross(Vector3.Up);
        var lateral = playerOffset.Dot(right);
        // It changes side only when it is clearly on the other one (1.5 body radii off the line; was 6 cm).
        if (Mathf.Abs(lateral) > 0.03f) _followSide = Mathf.Sign(lateral);
        var place = FollowPlace(right);
        var toPlace = Planar(place - GlobalPosition);
        var placeDistance = toPlace.Length();
        // Coming down beside its place after the watchdog found it stuck up high: that first.
        _goal = place;
        if (_landing != null && LandingVelocity() is { } landing) return landing;
        // Resting aloft (held at its height beside a climber, or over the drop beside a player up a tree), its place moves away
        // up and down as well as across.
        var aloft = _holdY != null;
        var placeGap = aloft ? (place - GlobalPosition).Length() : placeDistance;
        // Something between the companion and its place (it is behind the box): that is out of the band too. Floating, or
        // resting aloft, it goes straight there, so a walk's way round is no matter.
        var routed = PlanRoute(place, 0.05f, dt);
        // The way there is still being searched for, with nothing to steer by: hold still a tick or two, deciding nothing.
        if (_routePending) return Vector3.Zero;
        var detour = routed && !_route.Direct && _route.LengthM > placeDistance + DetourM && !_floating && !aloft;
        // In a narrow gap the walkable place can be nearer than the band's edge; never chase away from it.
        var near = Mathf.Min(FollowNearM, Planar(place - _player!.GlobalPosition).Length() - 0.02f);
        // Far above or below the player (up a cliff, in a tree, across the water from a bank) is out of the band too.
        var apart = Mathf.Abs(GlobalPosition.Y - _player.GlobalPosition.Y) > LevelGapM;
        if (!_following)
        {
            _following = distance > FollowFarM || distance < near || detour || apart || (moving && placeGap > 0.06f);
            if (_following) (FloatAltitudeFloorY, _holdY, _holdBeside) = (null, null, null);
        }
        // Floating, it rests only once level with its place: resting lower, it would hold there and fall behind at once.
        else if (still && !detour && !apart && (!_floating || Mathf.Abs(GlobalPosition.Y - place.Y) <= WalkLevelM) &&
                 (placeDistance < 0.02f || (distance > near + 0.02f && distance < FollowFarM - 0.06f)))
            _following = false;
        if (!_following)
        {
            // Come to rest while floating, it holds its height there, as an arrival by floating does. It used to sink to the
            // ground below at once, which opened the gap to a climber again and floated it back up, over and over: the founder's
            // video (9 October) of the HUD flipping between "follow" and "floating there" and the Gubble shaking beside a climber.
            if (_floating) (_holdY, _holdBeside) = (GlobalPosition.Y, null);
            _floating = false;
            _walkFromLanding = false;
            return Vector3.Zero;
        }
        if (ChooseFloat(place, routed))
        {
            RiseOverObstacles = true;
            FloatAltitudeFloorY = place.Y;
            var floating = (moving ? playerVelocity : Vector3.Zero) + toPlace * FollowGainPerS;
            if (!moving && floating.Length() < MinApproachMps && placeDistance > 0.005f) floating = floating.Normalized() * MinApproachMps;
            return floating.LimitLength(RunSpeedMps);
        }
        // Honest about a place it cannot reach: it goes as near as the floor allows and says blocked.
        if (routed && !_route.Reaches) Blocked("out_of_reach");
        if (detour || (routed && !_route.Reaches))
            return RouteVelocity(Mathf.Clamp(_route.LengthM * FollowGainPerS + playerSpeed, MinApproachMps, RunSpeedMps));
        var desired = (moving ? playerVelocity : Vector3.Zero) + toPlace * FollowGainPerS;
        // Finish the last few centimetres briskly instead of creeping.
        if (!moving && desired.Length() < MinApproachMps && placeDistance > 0.005f) desired = desired.Normalized() * MinApproachMps;
        return desired.LimitLength(RunSpeedMps);
    }

    /// <summary>
    /// The player's motion over the last tick (metres a second): where it went, not the velocity it asked for. A jump of more
    /// than PlayerJumpM in one tick is a teleport (a recovery, a wash ashore), not motion.
    /// </summary>
    private void TrackPlayerMotion(float dt)
    {
        var at = _player!.GlobalPosition;
        var step = at - _playerLast;
        _playerMotion = _playerSeen && dt > 0 && step.Length() < PlayerJumpM ? step / dt : Vector3.Zero;
        _playerLast = at;
        _playerSeen = true;
    }

    private const float PlayerJumpM = 0.25f;
    /// <summary>How long the player stands still before follow comes to rest.</summary>
    public const float PlayerStillS = 0.25f;

    /// <summary>The place beside the player on the walkable side: when a wall or furniture covers that side, the other one.</summary>
    private Vector3 FollowPlace(Vector3 right)
    {
        // A climbing player: just off the face beside them, where the companion floats while they climb.
        if (_player!.IsClimbing)
        {
            var outward = Planar(_player.ClimbNormal);
            if (outward.LengthSquared() > 0.0001f) return _player.GlobalPosition + outward.Normalized() * FollowSideM;
        }
        Vector3 At(float side) => _player!.GlobalPosition + right * (side * FollowSideM) + _travel * FollowLeadM;
        var place = At(_followSide);
        if (!NavigationReady) return place;
        // How far a place is from the walkable mesh: across, or up and down beyond the level band (the place beside a
        // player on a crown or a cliff top is over ground far below, the place beside a swimmer over the pond's bed).
        float Off(Vector3 wanted, Vector3 snapped) =>
            Mathf.Max(Planar(snapped - wanted).Length(), Mathf.Abs(snapped.Y - wanted.Y) > LevelGapM ? float.PositiveInfinity : 0);
        var snapped = Navigation!.ClosestPoint(place);
        var offMesh = Off(place, snapped);
        // Within an agent radius of the walkable mesh, the place is only nudged off a wall (was 8 cm, the old agent radius).
        if (offMesh <= Navigation.AgentRadiusM) return snapped;
        var other = At(-_followSide);
        var otherSnapped = Navigation.ClosestPoint(other);
        var otherOff = Off(other, otherSnapped);
        if (otherOff <= Navigation.AgentRadiusM || otherOff < offMesh - 0.5f * Navigation.AgentRadiusM)
        {
            _followSide = -_followSide;
            (place, snapped, offMesh) = (other, otherSnapped, otherOff);
        }
        // Far from anything walkable (over water, up a tree, out at sea): the place itself, where the companion floats.
        return float.IsPositiveInfinity(offMesh) || offMesh > 2 * Navigation.AgentRadiusM ? place : snapped;
    }

    /// <summary>
    /// Where come makes for: the player, or for a player under water (a diver), the spot over them at the surface, where a
    /// swimmer floats (Codex Sol's review: the Gubble hovered over a diver for ever, never level with them).
    /// </summary>
    private Vector3 ComeTarget()
    {
        var at = _player!.GlobalPosition;
        var water = _player.Water;
        if (water.Wet && at.Y < water.SurfaceY - _player.SwimFloatDepthM) at.Y = water.SurfaceY - _player.SwimFloatDepthM;
        return at;
    }

    private Vector3 ComeVelocity(Vector3 playerOffset, float dt)
    {
        var distance = playerOffset.Length();
        var target = ComeTarget();
        _goal = target;
        if (_landing != null && LandingVelocity() is { } landing) return landing;
        var routed = PlanRoute(target, ComeArrivalM + 0.05f, dt);
        if (_routePending) return Vector3.Zero;
        if (ChooseFloat(target, routed))
        {
            var level = Mathf.Abs(GlobalPosition.Y - target.Y) <= LevelGapM;
            var clear = target.Y > _player!.GlobalPosition.Y + 0.001f || ClearTo(_player.GlobalPosition + Vector3.Up * (_player.BodyHeightM * 0.5f), _player.GetRid());
            if (distance <= ComeArrivalM && level && clear)
            {
                ComeArrivedSerial = IntentSerial;
                ArriveFloating(_player.GlobalPosition);
                return Vector3.Zero;
            }
            RiseOverObstacles = true;
            FloatAltitudeFloorY = target.Y;
            // Not yet level with the player, or something between: it keeps on toward them (up the face in its way, over the
            // wall and down beside them) until it arrives.
            if (distance < 1e-4f) return Vector3.Zero;
            var approach = Mathf.Clamp((distance - ComeArrivalM + 0.02f) * FollowGainPerS, MinApproachMps, RunSpeedMps);
            return -playerOffset / distance * approach;
        }
        // Close in a straight line but with a wall or box between does not count as arrived, and neither does a
        // player it cannot reach at all (across a thin wall): that is blocked, honestly (Lane P review).
        var detour = routed && !_route.Direct && _route.LengthM > distance + DetourM;
        if (distance <= ComeArrivalM && !detour && (!routed || _route.Reaches))
        {
            // Arrived: the command host finishes this come's job (the serial tells it which come it was).
            ComeArrivedSerial = IntentSerial;
            Stay();
            return Vector3.Zero;
        }
        if (routed && !_route.Reaches) Blocked("out_of_reach");
        if (detour || (routed && !_route.Reaches))
            return RouteVelocity(Mathf.Clamp((_route.LengthM - ComeArrivalM + 0.02f) * FollowGainPerS, MinApproachMps, RunSpeedMps));
        var speed = Mathf.Clamp((distance - ComeArrivalM + 0.02f) * FollowGainPerS, MinApproachMps, RunSpeedMps);
        return -playerOffset / distance * speed;
    }

    /// <summary>Toward the nearest point of the go_to box's footprint; arrived within _goToArrivalM of it, then stays.</summary>
    private Vector3 GoToVelocity(float dt)
    {
        var here = GlobalPosition;
        var low = _goToTarget.Position;
        var high = _goToTarget.End;
        var nearest = new Vector3(Mathf.Clamp(here.X, low.X, high.X), Mathf.Clamp(here.Y, low.Y, high.Y), Mathf.Clamp(here.Z, low.Z, high.Z));
        var distance = Planar(nearest - here).Length();
        // The side of the box the walk can reach (backlog: the nearest reachable side, not the nearest side across a wall).
        var aim = ReachableSide(dt);
        _goal = aim;
        if (_landing != null && LandingVelocity() is { } landing) return landing;
        // The side to make for is still being chosen (its routes are searched on a worker): hold still meanwhile.
        if (_sidePending) return Vector3.Zero;
        var routed = PlanRoute(aim, _goToArrivalM, dt);
        if (_routePending) return Vector3.Zero;
        if (ChooseFloat(aim, routed))
        {
            // Level with the box: its bottom no higher than the level band over the feet, its top no lower than reach under them.
            var level = low.Y - here.Y <= LevelGapM && here.Y - high.Y <= ReachM;
            var clear = ClearTo(nearest + (here - nearest).Normalized() * 0.01f, default);
            if (distance <= _goToArrivalM && level && clear)
            {
                GoToArrivedSerial = IntentSerial;
                ArriveFloating(null);
                return Vector3.Zero;
            }
            RiseOverObstacles = true;
            FloatAltitudeFloorY = low.Y;
            var toward = Planar(nearest - here);
            if (distance < 1e-4f) return Vector3.Zero;
            var approach = Mathf.Clamp((distance - _goToArrivalM + 0.02f) * FollowGainPerS, MinApproachMps, RunSpeedMps);
            return toward / distance * approach;
        }
        var offset = Planar(aim - here);
        var aimDistance = offset.Length();
        var detour = routed && !_route.Direct && _route.LengthM > aimDistance + DetourM;
        if (distance <= _goToArrivalM && !detour)
        {
            // Arrived: the command host finishes this go_to's job (the serial tells it which one it was).
            GoToArrivedSerial = IntentSerial;
            Stay();
            return Vector3.Zero;
        }
        if (routed && !_route.Reaches) Blocked("out_of_reach");
        if (detour || (routed && !_route.Reaches))
            return RouteVelocity(Mathf.Clamp((_route.LengthM - _goToArrivalM + 0.02f) * FollowGainPerS, MinApproachMps, RunSpeedMps));
        var speed = Mathf.Clamp((distance - _goToArrivalM + 0.02f) * FollowGainPerS, MinApproachMps, RunSpeedMps);
        return aimDistance > 0.0001f ? offset / aimDistance * speed : Vector3.Zero;
    }

    /// <summary>
    /// Where a go_to makes for: of the walkable points just outside the box's footprint (its sides and corners, and the
    /// nearest point), the one with the shortest walk that reaches it, chosen again as the map or the body moves on. Without
    /// a walkable map, or with no side the walk reaches, the nearest point of the footprint (the companion then floats there).
    /// The walks are searched on worker threads, all at once (S7); until a side is chosen on the bake the map holds,
    /// _sidePending holds the body still.
    /// </summary>
    private Vector3 ReachableSide(float dt)
    {
        var here = GlobalPosition;
        var low = _goToTarget.Position;
        var high = _goToTarget.End;
        var nearest = new Vector3(Mathf.Clamp(here.X, low.X, high.X), Mathf.Clamp(here.Y, low.Y, high.Y), Mathf.Clamp(here.Z, low.Z, high.Z));
        _goToAimAge += dt;
        _sidePending = false;
        if (!NavigationReady) return _goToAim = nearest;
        // Floating, it makes for the box itself (GoToVelocity): no side to choose, and no searches to pay for.
        if (_floating) return _goToAim;
        if (_sideRequests != null && _sideRequests.All(r => r.IsDone)) ChooseSide(nearest);
        var revision = Navigation!.Revision;
        if (_sideRequests == null && (_goToAimRevision != revision || _goToAimAge >= 0.5))
        {
            _goToAimAge = 0;
            var centre = _goToTarget.GetCenter();
            var standOff = Navigation.AgentRadiusM;
            var candidates = new System.Collections.Generic.List<Vector3>();
            foreach (var (x, z) in new[] { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1), (2, 2) })
            {
                var outside = x == 2 ? nearest + Planar(nearest - centre).Normalized() * standOff
                    : new Vector3(x < 0 ? low.X - standOff : x > 0 ? high.X + standOff : centre.X, low.Y, z < 0 ? low.Z - standOff : z > 0 ? high.Z + standOff : centre.Z);
                var candidate = Navigation.ClosestPoint(outside);
                var footprint = new Vector3(Mathf.Clamp(candidate.X, low.X, high.X), candidate.Y, Mathf.Clamp(candidate.Z, low.Z, high.Z));
                // It must stand within the arrival distance of the footprint, level with the box's bottom.
                if (Planar(candidate - footprint).Length() > _goToArrivalM || Mathf.Abs(candidate.Y - low.Y) > LevelGapM) continue;
                candidates.Add(candidate);
            }
            _sideCandidates = candidates.ToArray();
            _sideRequests = _sideCandidates.Select(c => Navigation.RequestRoute(here, c, 0.02f, SearchM(here, c))).ToArray();
            // Nothing chosen on this bake yet, or a short way (today's islands): wait for the searches here, briefly.
            if (_goToAimRevision != revision || Planar(nearest - here).Length() <= LongRouteM || Navigation.IsSmall)
            {
                var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(RouteWait * System.Diagnostics.Stopwatch.Frequency / 1000);
                foreach (var request in _sideRequests)
                    request.Wait(Math.Max(0, (deadline - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
                if (_sideRequests.All(r => r.IsDone)) ChooseSide(nearest);
            }
        }
        _sidePending = _goToAimRevision != revision;
        return _goToAim;
    }

    /// <summary>Of the searched sides, the shortest walk that reaches one (planned on the bake the map holds); else the nearest point.</summary>
    private void ChooseSide(Vector3 nearest)
    {
        var requests = _sideRequests!;
        _sideRequests = null;
        if (requests.Any(r => r.Stale)) return;
        var best = nearest;
        var bestLength = float.PositiveInfinity;
        for (var i = 0; i < requests.Length; i++)
        {
            if (!requests[i].TryTake(out var route) || !route.Reaches || route.LengthM >= bestLength) continue;
            best = _sideCandidates[i];
            bestLength = route.LengthM;
        }
        _goToAim = best;
        _goToAimRevision = requests.Length > 0 ? requests[0].Revision : Navigation!.Revision;
    }

    /// <summary>
    /// Float or walk: the walk while the planned route reaches the goal, level with it, and not a long way round; otherwise
    /// float. Once floating it floats until the goal changes, it arrives, or follow comes to rest.
    /// </summary>
    private bool ChooseFloat(Vector3 goal, bool routed)
    {
        if (_floating)
        {
            // On a long trip it asks now and then whether a walk from where it is now reaches the goal (RecheckWalk): one that
            // will do, starting where the body is, is walked the rest of the way.
            if (!_recheckTaken) return true;
            _recheckTaken = false;
            if (WhyNotWalk(goal, routed) != null || Planar(_route.Points[0] - GlobalPosition).Length() > Navigation!.AgentRadiusM + 0.1f) return true;
            _floating = false;
            FloatEnds++;
            return false;
        }
        var reason = _floatWhenStuck ? "stuck_walking" : WhyNotWalk(goal, routed);
        _floatWhenStuck = false;
        if (reason == null) return false;
        FloatReason = reason;
        _floating = true;
        FloatStarts++;
        // A search still in flight from the walk was asked from somewhere behind: a float asks afresh (RecheckWalk).
        _routeRequest = null;
        _floatRecheckAge = 0;
        _recheckTaken = false;
        return true;
    }

    /// <summary>
    /// Why the route in hand will not do for walking to the goal, or null when it will: a walk that reaches it, ends level with it,
    /// starts from the ground under the body (one held aloft, beside a climber or by a player up a tree, floats) and is not a long
    /// way round (FloatDetourFactor; taken however long after a landing).
    /// </summary>
    private string? WhyNotWalk(Vector3 goal, bool routed)
    {
        if (!routed || _route.IsEmpty) return "no_route";
        if (!_route.Reaches) return "out_of_reach";
        if (Mathf.Abs(_route.Points[^1].Y - goal.Y) > WalkLevelM) return Why("goal_off_level", _route.Points[^1].Y - goal.Y);
        if (Mathf.Abs(_route.Points[0].Y - GlobalPosition.Y) > LevelGapM && !HoversLowOverLand(goal, _route.Points[0])) return Why("start_off_level", _route.Points[0].Y - GlobalPosition.Y);
        if (!_walkFromLanding && _route.LengthM > (goal - GlobalPosition).Length() * FloatDetourFactor + FloatDetourM) return Why("long_way", _route.LengthM / Mathf.Max(0.01f, (goal - GlobalPosition).Length()));
        return null;
    }

    private void Blocked(string reason)
    {
        GoalBlocked = true;
        BlockedReason = reason;
    }

    /// <summary>
    /// On a long trip (the goal farther than LongRouteM) a walk may start a little above or below the body, within WalkStartM: the
    /// body hovers over what the walkable map leaves out (a slope steeper than its 45 degrees, a stone, a dip), and walking it
    /// settles onto the ground as it goes. On the 10x island every float of a 65 m come but those where no walk reached started so,
    /// 12 to 15 cm off the map, and floated on over the ridges, up to 7 m over the player. A height it holds (beside a climber,
    /// by a player up a tree) is no walk's start; today's trips are as before.
    /// </summary>
    private bool HoversLowOverLand(Vector3 goal, Vector3 start) =>
        Planar(goal - GlobalPosition).Length() > LongRouteM && _holdY == null && Mathf.Abs(start.Y - GlobalPosition.Y) <= WalkStartM;

    /// <summary>How far above or below the body a long trip's walk may start (HoversLowOverLand): three body heights, body-sized.</summary>
    public const float WalkStartM = 0.3f;

    private static string Why(string reason, float value) => reason + " " + value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Why the body last said blocked (diagnostics): stuck, stuck_up_high, no_progress, out_of_reach or no_clear_heading.</summary>
    public string BlockedReason { get; private set; } = "";

    /// <summary>This body and one other, as a query's exclusions: one array kept and refilled (S12), never a new one for every ray.</summary>
    private Godot.Collections.Array<Rid> ExcludeSelfAnd(Rid other)
    {
        _excludePair ??= new Godot.Collections.Array<Rid>();
        _excludePair.Clear();
        _excludePair.Add(GetRid());
        _excludePair.Add(other);
        return _excludePair;
    }
    private Godot.Collections.Array<Rid>? _excludePair;

    /// <summary>Nothing solid between the body's middle and a point (the point's own body left out).</summary>
    private bool ClearTo(Vector3 point, Rid ignore)
    {
        var from = GlobalPosition + Vector3.Up * (BodyHeightM * 0.5f);
        using var query = PhysicsRayQueryParameters3D.Create(from, point, RoomBuilder.BodyMask, ignore.IsValid ? ExcludeSelfAnd(ignore) : ExcludeSelf);
        using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count == 0;
    }

    /// <summary>
    /// Arrived by floating: it stays, holding its height (beside a player up a tree, it does not sink to the ground below) until
    /// the player it came to (beside, for a come) moves away (HoldReleaseM).
    /// </summary>
    private void ArriveFloating(Vector3? beside)
    {
        var height = GlobalPosition.Y;
        Stay();
        _holdY = height;
        _holdBeside = beside;
    }

    /// <summary>The player has moved away from where the Gubble began holding its height beside them: the hold goes.</summary>
    private void ReleaseHoldWhenLeft()
    {
        if (_holdY == null || _holdBeside is not { } beside || !HasPlayer()) return;
        var moved = _player!.GlobalPosition - beside;
        if (Planar(moved).Length() <= HoldReleaseM && Mathf.Abs(moved.Y) <= LevelGapM) return;
        _holdY = null;
        _holdBeside = null;
        HoldReleases++;
    }

    /// <summary>
    /// The longest search a route to a goal needs: a walk longer than FloatDetourFactor times the way there (and FloatDetourM)
    /// is not taken (the Gubble floats), so searching further only costs (on the magnified island, a goal out of reach meant a
    /// search of everywhere the start could walk). Twice that, for the search's own measure along polygon edges and slopes.
    /// </summary>
    private static float SearchM(Vector3 from, Vector3 goal) => 2 * (from.DistanceTo(goal) * FloatDetourFactor + FloatDetourM);

    /// <summary>
    /// Where to come down beside a goal the Gubble is stuck high over (the watchdog): of the walkable spots round the goal, nearest
    /// rings first, one level with the goal, with nothing over it up to the body's height (no roof, no ledge), and from which a
    /// walk reaches the goal; on the ring, the one nearest the body. Null when there is none.
    /// </summary>
    private Vector3? LandingBeside(Vector3 goal)
    {
        if (!NavigationReady) return null;
        var here = GlobalPosition;
        var space = GetWorld3D().DirectSpaceState;
        foreach (var ring in LandingRingsM)
        {
            Vector3? best = null;
            for (var k = 0; k < 8; k++)
            {
                var angle = k * Mathf.Pi / 4;
                var spot = Navigation!.ClosestPoint(goal + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * ring);
                if (Mathf.Abs(spot.Y - goal.Y) > LevelGapM || Planar(spot - goal).Length() > ring + Navigation.AgentRadiusM) continue;
                if (best is { } kept && Planar(spot - here).Length() >= Planar(kept - here).Length()) continue;
                using var query = PhysicsRayQueryParameters3D.Create(new Vector3(spot.X, Mathf.Max(here.Y, spot.Y) + BodyHeightM, spot.Z), spot + Vector3.Up * 0.01f, RoomBuilder.BodyMask, ExcludeSelf);
                using (var hit = space.IntersectRay(query)) if (hit.Count > 0) continue;
                if (!Navigation.FindRoute(spot, goal, ComeArrivalM, SearchM(spot, goal)).Reaches) continue;
                best = spot;
            }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>The rings round a goal where the watchdog looks for open air to come down in.</summary>
    private static readonly float[] LandingRingsM = { FollowSideM, 0.25f, 0.35f, 0.5f, 0.75f, 1.0f, 1.5f, 2.0f };

    /// <summary>
    /// Making for the landing spot: across to it (rising over what is in the way), then down to it. Level with it, the landing
    /// is over: null, and the goal's own steering plans again from there, walking the rest.
    /// </summary>
    private Vector3? LandingVelocity()
    {
        var spot = _landing!.Value;
        var offset = Planar(spot - GlobalPosition);
        if (offset.Length() <= CornerReachedM && Mathf.Abs(GlobalPosition.Y - spot.Y) <= LevelGapM)
        {
            _landing = null;
            _floating = false;
            _walkFromLanding = true;
            _route = RoomNavigation.Route.None;
            return null;
        }
        _floating = true;
        RiseOverObstacles = true;
        FloatAltitudeFloorY = spot.Y;
        if (offset.Length() <= CornerReachedM) return Vector3.Zero;
        return offset.Normalized() * Mathf.Clamp(offset.Length() * FollowGainPerS, MinApproachMps, RunSpeedMps);
    }

    private bool NavigationReady => Navigation != null && GodotObject.IsInstanceValid(Navigation) && Navigation.IsReady;

    /// <summary>
    /// Plan a route to the goal, or keep the current one while it is fresh. False when there is no navigation to plan on.
    /// The search runs on a worker thread (S7). While it runs the route in use is kept, if it was planned on the bake the map
    /// holds and for about this goal; with none such, _routePending holds the body still, so it never decides to float or walk
    /// on a route it does not have, or on one planned on an older bake.
    /// </summary>
    private bool PlanRoute(Vector3 goal, float toleranceM, float dt)
    {
        _routePending = false;
        if (!NavigationReady)
        {
            _route = RoomNavigation.Route.None;
            _routeRequest = null;
            return false;
        }
        _routeAge += dt;
        var revision = Navigation!.Revision;
        var length = Mathf.Max(_route.LengthM, Planar(goal - GlobalPosition).Length());
        var slack = length <= LongRouteM ? 0.03f : 0.03f * length / LongRouteM;
        // Floating, it goes straight there and no route is used: none is planned (on the magnified island a search for a goal
        // out of reach covers everywhere the start can walk to, up to about 35 ms, and it came round ten times a second), but
        // on a long trip it asks now and then whether a walk from here reaches.
        if (_floating) return RecheckWalk(goal, toleranceM, slack, dt);
        TakeRoute(goal, slack);
        bool Usable() => !_route.IsEmpty && _routeRevision == revision && Planar(goal - _routeGoal).Length() <= Mathf.Max(slack, 0.25f * length);
        var due = _route.IsEmpty || _routeRevision != revision || _routeAge >= RouteRefreshFor(length) || Planar(goal - _routeGoal).Length() > slack;
        if (due && _routeRequest == null)
        {
            _routeRequest = Navigation.RequestRoute(GlobalPosition, goal, toleranceM, SearchM(GlobalPosition, goal));
            if (!Usable() || Planar(goal - GlobalPosition).Length() <= LongRouteM || Navigation.IsSmall)
            {
                _routeRequest.Wait(RouteWait);
                TakeRoute(goal, slack);
            }
        }
        if (!Usable())
        {
            _routePending = true;
            return false;
        }
        while (_routeIndex < _route.Points.Length - 1 && Planar(_route.Points[_routeIndex] - GlobalPosition).Length() < CornerReachedM) _routeIndex++;
        return !_route.IsEmpty;
    }

    /// <summary>
    /// Floating on a long trip (the goal farther than LongRouteM; today's islands never ask, and a float there floats on as
    /// before), low over the ground and not coming down to a landing or holding a height: every FloatRecheckS it asks whether a
    /// walk from where it is now reaches the goal, on a worker thread, floating on meanwhile. ChooseFloat walks the answer if it
    /// will do (S8: a 72 m come on the 10x island floated 73 s of its 80 once it had started to float).
    /// </summary>
    private bool RecheckWalk(Vector3 goal, float toleranceM, float slack, float dt)
    {
        _floatRecheckAge += dt;
        if (TakeRoute(goal, slack)) _recheckTaken = true;
        var low = HoverSupportY is { } ground && GlobalPosition.Y - ground <= WalkStartM;
        if (_routeRequest == null && _floatRecheckAge >= FloatRecheckS && low && _landing == null && _holdY == null && Planar(goal - GlobalPosition).Length() > LongRouteM)
        {
            _floatRecheckAge = 0;
            _routeRequest = Navigation!.RequestRoute(GlobalPosition, goal, toleranceM, SearchM(GlobalPosition, goal));
        }
        return !_route.IsEmpty;
    }

    /// <summary>A finished search becomes the route in use if it was planned on the bake the map holds and for about this goal; else it is dropped. True when taken.</summary>
    private bool TakeRoute(Vector3 goal, float slack)
    {
        if (_routeRequest is not { IsDone: true } done) return false;
        _routeRequest = null;
        if (!done.TryTake(out var route) || Planar(goal - done.Goal).Length() > slack) return false;
        _route = route;
        _routeIndex = 1;
        _routeGoal = done.Goal;
        _routeAge = 0;
        _routeRevision = route.Revision;
        return true;
    }

    /// <summary>How often a route of this length is planned again: RouteRefreshS up to LongRouteM, then in proportion, up to LongRouteRefreshS.</summary>
    public static double RouteRefreshFor(float lengthM) =>
        lengthM <= LongRouteM ? RouteRefreshS : Math.Min(LongRouteRefreshS, RouteRefreshS * lengthM / LongRouteM);

    /// <summary>Steer for the next corner of the planned route; zero at its end.</summary>
    private Vector3 RouteVelocity(float speed)
    {
        var points = _route.Points;
        if (points.Length == 0) return Vector3.Zero;
        var last = _routeIndex >= points.Length - 1;
        var toCorner = Planar(points[Mathf.Min(_routeIndex, points.Length - 1)] - GlobalPosition);
        if (last && toCorner.Length() < CornerReachedM) return Vector3.Zero;
        FollowingRoute = true;
        return toCorner.Normalized() * (last ? Mathf.Min(speed, Mathf.Max(MinApproachMps, toCorner.Length() * FollowGainPerS)) : speed);
    }

    /// <summary>Report blocked when trying to move has covered almost nothing for a second, and plan again.</summary>
    private void WatchProgress(float dt, bool trying)
    {
        if (!trying)
        {
            _progressTime = 0;
            _progressAnchor = GlobalPosition;
            _stuck = false;
            return;
        }
        _progressTime += dt;
        if (_progressTime < StuckAfterS) return;
        // Floating up a cliff or over a wall is progress too.
        _stuck = (_floating ? GlobalPosition - _progressAnchor : Planar(GlobalPosition - _progressAnchor)).Length() < StuckDistanceM;
        if (_stuck) _routeAge = RouteRefreshS;
        _progressTime = 0;
        _progressAnchor = GlobalPosition;
        // On a long trip (S8) a body stuck one way tries the other before it says blocked: a walk held by something the walkable
        // map leaves out floats over it, and a float held at its goal's height asks at once whether a walk from here reaches.
        if (!_stuck || _goal is not { } goal || Planar(goal - GlobalPosition).Length() <= LongRouteM || _landing != null) return;
        if (!_floating)
        {
            _floatWhenStuck = true;
            _stuck = false;
        }
        else _floatRecheckAge = FloatRecheckS;
    }

    /// <summary>Walking on a long trip got nowhere for StuckAfterS: the next ChooseFloat floats (stuck_walking).</summary>
    private bool _floatWhenStuck;

    /// <summary>
    /// A trip that gets no nearer its goal by NoProgressM for NoProgressS says blocked until it does (S8): whatever it is trying
    /// (walking, floating, a landing, round and round), the host then gives up honestly after UnreachableAfterS instead of letting
    /// it wander for ever. Room-sized: a long detour round a 10x mesa still gets nearer within it; today's trips take seconds.
    /// A goal that moves NoProgressM (a player walking off) starts the count again.
    /// </summary>
    public const double NoProgressS = 30;
    public const float NoProgressM = 1f;
    private Vector3? _tripGoal;
    private float _tripBest;
    private double _tripAge;
    private bool _noProgress;
    /// <summary>Where the watchdog last sent it down to a landing (MaxLandings counts per place).</summary>
    private Vector3? _lastLandingAt;

    private void WatchTrip(float dt)
    {
        if (_goal is not { } goal || CurrentIntent is not ("come" or "go_to"))
        {
            (_tripGoal, _tripAge, _noProgress) = (null, 0, false);
            return;
        }
        var distance = GlobalPosition.DistanceTo(goal);
        if (_tripGoal is not { } was || was.DistanceTo(goal) > NoProgressM || distance <= _tripBest - NoProgressM)
        {
            (_tripGoal, _tripBest, _tripAge, _noProgress) = (goal, distance, 0, false);
            return;
        }
        _tripAge += dt;
        _noProgress = _tripAge >= NoProgressS;
    }

    private Vector3 _highAnchor;
    private double _highTime;
    /// <summary>Stuck up high with nowhere to come down: blocked until it moves again (the host then refuses the goal honestly).</summary>
    private bool _highBlocked;

    /// <summary>
    /// The watchdog for stuck up high (see HighReplans): floating more than LevelGapM over this tick's goal, and moved less than
    /// StuckDistanceM in StuckAfterS, whether or not it is trying to move (right over its goal it asks for no motion at all).
    /// It lets any held height go and comes down in open air beside the goal; with nowhere to come down, or a landing that gets
    /// nowhere itself, it stays blocked.
    /// </summary>
    private void WatchHeight(float dt)
    {
        var landing = _landing != null;
        if (!_floating || _goal is not { } goal || (!landing && GlobalPosition.Y - goal.Y <= LevelGapM))
        {
            _highTime = 0;
            _highAnchor = GlobalPosition;
            _highBlocked = false;
            return;
        }
        _highTime += dt;
        if (_highTime < StuckAfterS) return;
        var moved = GlobalPosition.DistanceTo(_highAnchor);
        _highTime = 0;
        _highAnchor = GlobalPosition;
        _highBlocked = false;
        if (moved >= StuckDistanceM) return;
        if (landing)
        {
            // Coming down beside the goal and getting nowhere: no more landings for this goal.
            _landing = null;
            _landings = MaxLandings;
            _highBlocked = true;
            return;
        }
        HighReplans++;
        _holdY = null;
        _holdBeside = null;
        // MaxLandings is per place: a long trip stuck up high again far from where it last came down may come down again (S8).
        if (_lastLandingAt is { } landedAt && Planar(GlobalPosition - landedAt).Length() > LongRouteM) _landings = 0;
        _landing = _landings < MaxLandings ? LandingBeside(goal) : null;
        if (_landing != null)
        {
            _lastLandingAt = GlobalPosition;
            _landings++;
            _stuck = false;
        }
        else _highBlocked = true;
    }

    // ---- catching up (S8) ----

    /// <summary>
    /// Following or coming (not a fetch's way back), farther than CatchUpBeyondM from the player and out of their view: it
    /// reappears beside them (CatchUpSpot) and goes on with its goal from there. Looked at every CatchUpCheckS while far.
    /// </summary>
    private void CatchUp(float dt)
    {
        if (CurrentIntent is not ("follow" or "come") || !_mayCatchUp || _homePending || _player!.GoingHome) { _catchUpAge = 0; return; }
        if (GlobalPosition.DistanceTo(_player.GlobalPosition) <= CatchUpBeyondM) { _catchUpAge = 0; return; }
        if ((_catchUpAge += dt) < CatchUpCheckS) return;
        _catchUpAge = 0;
        var from = GlobalPosition;
        if (BodyInPlayerView(from) || CatchUpSpot() is not { } spot || !TryTeleportTo(spot)) return;
        // Whatever it was doing out there (a float, a held height, a landing, a route) means nothing beside the player.
        _route = RoomNavigation.Route.None;
        _routeRequest = null;
        _sideRequests = null;
        _floating = _following = _stuck = _highBlocked = _walkFromLanding = _recheckTaken = _floatWhenStuck = false;
        FloatAltitudeFloorY = _holdY = null;
        _holdBeside = _landing = null;
        _landings = 0;
        _progressTime = 0;
        _progressAnchor = _highAnchor = GlobalPosition;
        CatchUps++;
        LastCatchUp = (from, GlobalPosition);
        CaughtUp?.Invoke(from, GlobalPosition);
    }

    /// <summary>
    /// Where a catch-up sets the Gubble down: on the rings round the player (CatchUpRingsM), nearest first, a spot out of the
    /// player's view, level with them, dry, where the body fits, and (with the room's walkable map) on it, with a walk from it
    /// reaching the player. Null when there is none: it travels on and looks again.
    /// </summary>
    private Vector3? CatchUpSpot()
    {
        var player = _player!;
        var at = player.GlobalPosition;
        var space = GetWorld3D().DirectSpaceState;
        var mapped = NavigationReady;
        var forward = Planar(-player.GlobalBasis.Z);
        forward = forward.LengthSquared() > 1e-6f ? forward.Normalized() : Vector3.Forward;
        foreach (var ring in CatchUpRingsM)
            for (var k = 0; k < 8; k++)
            {
                // Behind the player first: the shoulder camera looks the way they face.
                var direction = (-forward).Rotated(Vector3.Up, (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * Mathf.Pi / 4);
                var wanted = at + direction * ring;
                var spot = wanted;
                if (mapped)
                {
                    spot = Navigation!.ClosestPoint(wanted);
                    if (Planar(spot - wanted).Length() > Navigation.AgentRadiusM + 0.05f) continue;
                }
                spot.Y += HoverHeightM;
                if (Mathf.Abs(spot.Y - at.Y) > LevelGapM || BodyInPlayerView(spot)) continue;
                if (RoomWater.At(space, spot, RoomWater.SearchM, 0.05f, GetRid()).Wet || (Sea != null && Sea.OpenWater(space, spot, RoomWater.SearchM, 0.05f, GetRid()).Wet)) continue;
                if (mapped && !Navigation!.FindRoute(spot, at, ComeArrivalM + 0.05f, SearchM(spot, at)).Reaches) continue;
                return spot;
            }
        return null;
    }

    /// <summary>Whether the player's view holds the Gubble standing here: its middle or its name tag (drawn the same size at any distance).</summary>
    public bool BodyInPlayerView(Vector3 feet) =>
        InPlayerView(feet + Vector3.Up * (BodyHeightM * 0.5f)) || InPlayerView(feet + Vector3.Up * (BodyHeightM * 1.35f));

    /// <summary>
    /// Whether the player's view holds a point: inside the view camera's frustum and far plane, with nothing solid between (the
    /// player's own body and the Gubble's left out). With no camera, nobody sees.
    /// </summary>
    public bool InPlayerView(Vector3 point)
    {
        var camera = IsInsideTree() ? GetViewport()?.GetCamera3D() : null;
        if (camera == null || !camera.IsInsideTree() || !point.IsFinite()) return false;
        var eye = camera.GlobalPosition;
        if (camera.IsPositionBehind(point) || !camera.IsPositionInFrustum(point) || eye.DistanceTo(point) > camera.Far) return false;
        using var query = PhysicsRayQueryParameters3D.Create(eye, point, RoomBuilder.SightMask,
            _player != null && GodotObject.IsInstanceValid(_player) ? ExcludeSelfAnd(_player.GetRid()) : ExcludeSelf);
        using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count == 0;
    }

    // ---- rising up a face, and coming down a long way (S8) ----

    /// <summary>
    /// Before the body moves: rising over a face last tick (RisingOver), the rise this tick is RiseMpsFor the face's height from
    /// where the rise began and what is left of it (the top of what stands just ahead, looked for from above); otherwise, and for
    /// every face within TallFaceM, FloatRiseMps as before. Coming down a drop that began taller than TallFaceM (off a mesa), the
    /// sink is the same rule turned over; within TallFaceM, FloatSinkMps as before.
    /// </summary>
    private void SetRiseSpeed(Vector3 desired, float dt)
    {
        FloatRiseMps = _baseRiseMps;
        FloatSinkMps = _baseSinkMps;
        // How far it has to come down to the height it settles to (Hover's: over the ground under it, or its goal's height).
        var settle = HoverSupportY is { } under ? under + HoverHeightM : FloatAltitudeFloorY;
        if (FloatAltitudeFloorY is { } floor && settle is { } over && floor > over) settle = floor;
        var drop = settle is { } to ? GlobalPosition.Y - to : 0f;
        // The drop is as tall as it has been on this way down: floating out over a steep slope, the land falls away as it goes.
        if (drop <= LevelGapM) _dropFromM = null;
        else if (drop > TallFaceM) _dropFromM = Mathf.Max(_dropFromM ?? 0f, drop);
        if (_dropFromM is { } fromM) FloatSinkMps = RiseMpsFor(drop, fromM, _baseSinkMps);
        _sinceRiseS = RisingOver ? 0 : _sinceRiseS + dt;
        // The rise is over once the body has not risen over anything for a moment (the bob, a step on the lip).
        if (_sinceRiseS > 0.5) _riseFromY = null;
        if (!_floating || !RiseOverObstacles || !RisingOver || Planar(desired).LengthSquared() < 1e-6f) return;
        var here = GlobalPosition;
        _riseFromY ??= HoverSupportY ?? here.Y;
        var column = here + Planar(desired).Normalized() * (BodyRadiusM + 0.03f);
        var top = PlayableBounds is { } bounds ? bounds.End.Y + 1 : here.Y + 40;
        if (top <= here.Y) return;
        using var query = PhysicsRayQueryParameters3D.Create(new Vector3(column.X, top, column.Z), new Vector3(column.X, here.Y, column.Z), RoomBuilder.BodyMask, ExcludeSelf);
        using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return;
        var faceTop = hit[HitPosition].AsVector3().Y + HoverHeightM;
        RiseFaceM = faceTop - _riseFromY.Value;
        RiseRemainingM = faceTop - here.Y;
        FloatRiseMps = RiseMpsFor(RiseRemainingM, RiseFaceM, _baseRiseMps);
        FastestRiseMps = Mathf.Max(FastestRiseMps, FloatRiseMps);
    }

    /// <summary>Move along a world-space velocity: the motion is exact through body-relative input while the body turns smoothly to face it.</summary>
    private void MoveWith(Vector3 desired, float dt)
    {
        var speed = desired.Length();
        // Floating heads straight for its goal (the body rises over or ducks under what is in the way); walking steers locally.
        var heading = _floating ? desired / speed : ChooseClearDirection(desired / speed);
        if (heading.LengthSquared() == 0)
        {
            Blocked("no_clear_heading");
            SetControlInput(Vector2.Zero);
            Velocity = new Vector3(0, Velocity.Y, 0);
            return;
        }
        if (!_hasLookTarget) TurnToward(heading, CompanionTurnRateRadPerS * dt, 0);
        var local = GlobalBasis.Inverse() * heading;
        SetControlInput(new Vector2(local.X, -local.Z) * Mathf.Min(1, speed / RunSpeedMps), sprint: true);
    }

    private void TurnToward(Vector3 direction, float maximumStep, float deadZone)
    {
        var yaw = Mathf.Atan2(-direction.X, -direction.Z);
        if (Mathf.Abs(Mathf.AngleDifference(Rotation.Y, yaw)) <= deadZone) return;
        Rotation = new Vector3(0, Mathf.RotateToward(Rotation.Y, yaw, maximumStep), 0);
    }

    private static Vector3 Planar(Vector3 value) => new(value.X, 0, value.Z);

    /// <summary>A heading without its components that would carry the body past a bound it stands within a probe of.</summary>
    private Vector3 AlongBounds(Vector3 heading)
    {
        if (PlayableBounds is not { } bounds || !BoundsHoldSides) return heading;
        var here = GlobalPosition;
        var reach = BodyRadiusM + SteeringProbeM;
        if ((heading.X > 0 && here.X + reach > bounds.End.X) || (heading.X < 0 && here.X - reach < bounds.Position.X)) heading.X = 0;
        if ((heading.Z > 0 && here.Z + reach > bounds.End.Z) || (heading.Z < 0 && here.Z - reach < bounds.Position.Z)) heading.Z = 0;
        return heading;
    }

    private Vector3 ChooseClearDirection(Vector3 desired)
    {
        // Local steering for the last stretch, round the player and round anything the navigation mesh does
        // not hold; routes round furniture come from RoomNavigation. Blocked reports blocked, never teleports.
        foreach (var angle in new[] { 0.0f, -0.65f, 0.65f, -1.15f, 1.15f })
        {
            var candidate = desired.Rotated(Vector3.Up, angle);
            // The room's bounds are a wall to the body (PlayableBounds): a heading across one keeps only its part along it.
            if (!InsidePlayableBounds(GlobalPosition + candidate * SteeringProbeM))
            {
                candidate = AlongBounds(candidate);
                if (candidate.LengthSquared() < 0.04f) continue;
                candidate = candidate.Normalized();
            }
            var probe = candidate * SteeringProbeM;
            // A floating body needs no ground ahead: off a ledge or over water it hovers over what is there.
            if (!Floats && !HasSupportNear(GlobalPosition + probe)) continue;
            // Flat travel does not require the extra headroom used for a step.
            if (!TestMove(GlobalTransform, probe, null, SafeMargin)) return candidate;
            var raised = GlobalTransform;
            raised.Origin += Vector3.Up * StepHeightM;
            if (TestMove(raised, probe, null, SafeMargin)) continue;
            return candidate;
        }
        return Vector3.Zero;
    }
}
