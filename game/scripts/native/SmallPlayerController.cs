using Godot;
using Godot.Collections;
using GarageGame.Native.Room;

namespace GarageGame.Native;

/// <summary>
/// The player's 10 cm body. The root is at the feet; the room stays in real metres. Body properties
/// (size, speeds, step, jump) live here; gravity and wind are world properties from
/// scripts/world_physics_profile.gd. Heavier gravity than the room's default keeps the same jump, only quicker; lighter
/// gravity lets the body leap higher, as on the moon (see JumpSpeedMps).
/// Creation effects (friendly wind, worn gliders) arrive through SetCreationEffects from the
/// invention runtime; this body never decides on its own that an effect is allowed.
/// The procedural body is an original blockout, not accepted character art.
/// </summary>
[GlobalClass]
public partial class SmallPlayerController : CharacterBody3D
{
    public const string WorldPhysicsScript = "res://scripts/world_physics_profile.gd";

    [Export] public bool ReadKeyboard { get; set; } = true;
    [Export] public float WalkSpeedMps { get; set; } = 0.32f;
    /// <summary>Three times the walk (founder playtest, 6 October: 0.60 m/s did not feel like a change of pace).</summary>
    [Export] public float RunSpeedMps { get; set; } = 0.96f;
    /// <summary>Walk speed in about 0.05 s, the run in 0.16 s, and a stop from a run in 0.16 s (7.7 cm).</summary>
    [Export] public float GroundAccelerationMps2 { get; set; } = 6.0f;
    /// <summary>Scaled by the world profile's air_control (more in floaty gravity).</summary>
    [Export] public float AirAccelerationMps2 { get; set; } = 2.0f;
    /// <summary>How fast the body turns to face its motion when movement follows a free camera (F2 zoomed out to orbit).</summary>
    [Export] public float TurnRateRadPerS { get; set; } = 10.0f;
    /// <summary>Jump apex above the take-off floor in the room's default gravity and heavier. Clears the 4 cm book with room to spare.</summary>
    [Export] public float JumpApexM { get; set; } = 0.065f;
    /// <summary>
    /// The highest leap, in the lightest gravity: three body heights (founder, 8 October: "being able to leap up a hillside
    /// would be very satisfying"). Without it floaty gravity's leap would be 38 cm and last 2.3 s.
    /// </summary>
    [Export] public float MaxJumpApexM { get; set; } = 0.30f;
    [Export] public float StepHeightM { get; set; } = 0.02f;
    [Export] public float FloorSnapM { get; set; } = 0.015f;
    [Export] public float SafeMarginM { get; set; } = 0.001f;
    [Export] public float TerminalFallMps { get; set; } = 6.0f;
    /// <summary>A jump pressed this long before landing still happens.</summary>
    [Export] public float JumpBufferS { get; set; } = 0.10f;
    /// <summary>A jump pressed this long after walking off an edge still happens.</summary>
    [Export] public float CoyoteTimeS { get; set; } = 0.08f;
    /// <summary>Creation-induced speed is capped relative to the body, not at human scale.</summary>
    [Export] public float MaxCreationSpeedMps { get; set; } = 1.2f;
    public const float MaxCreationAccelerationMps2 = 4.0f;
    public const float MaxGlideLimitMps = 7.0f;
    /// <summary>
    /// The largest gap under the capsule that the visual body closes. Jolt rests a CharacterBody3D's capsule 0 to
    /// 1.5 mm above its support (measured: 0.50 to 1.44 mm on the floor, the rug and the book), because its motion cast finds
    /// contact only to a fraction of the motion; on a 10 cm figure that reads as hovering.
    /// </summary>
    public const float MaxSeatGapM = 0.0025f;
    /// <summary>The gap under the capsule that the visual body closed on the last physics tick (0 in the air).</summary>
    public float SeatGapM { get; private set; }

    public float GravityMps2 { get; private set; } = 3.5f;
    /// <summary>The world's fall limit (thick floaty air falls slower); the body's own TerminalFallMps still caps it.</summary>
    public float WorldTerminalFallMps { get; private set; } = 6.0f;
    /// <summary>The world's multiplier on the body's air acceleration.</summary>
    public float AirControl { get; private set; } = 1.0f;
    public float EffectiveTerminalFallMps => Mathf.Min(TerminalFallMps, WorldTerminalFallMps);
    public Vector2 WindMps { get; private set; }
    public string WorldPhysicsId { get; private set; } = "room_tuned";
    public int WorldPhysicsRevision { get; private set; } = -1;
    /// <summary>The default preset's gravity (world_physics_profile.gd DEFAULT), where the jump's take-off speed is set.</summary>
    public float ReferenceGravityMps2 { get; private set; } = 3.5f;
    /// <summary>
    /// Take-off speed. In the default gravity and heavier it is solved so the apex, integrated at the physics tick, is
    /// JumpApexM (the discrete apex is v^2/2g + v*dt/2): real gravity keeps the 6.5 cm jump, only quicker. In lighter
    /// gravity the legs push off as hard as in the default, so the leap rises higher as gravity falls, as on the moon
    /// (half the gravity, about twice the height), up to MaxJumpApexM.
    /// </summary>
    public float JumpSpeedMps => GravityMps2 >= ReferenceGravityMps2
        ? SpeedForApex(GravityMps2, HopApexM)
        : Mathf.Min(SpeedForApex(ReferenceGravityMps2, HopApexM), SpeedForApex(GravityMps2, Mathf.Max(HopApexM, MaxHopApexM)));

    /// <summary>The apex the jump reaches under the current gravity, integrated at the physics tick.</summary>
    public float JumpApexNowM
    {
        get
        {
            var speed = JumpSpeedMps;
            return speed * speed / (2.0f * GravityMps2) + speed * 0.5f / Engine.PhysicsTicksPerSecond;
        }
    }

    /// <summary>How long the jump lasts on flat ground: up at the take-off speed, down under the fall limit.</summary>
    public float JumpAirtimeS
    {
        get
        {
            var apex = JumpApexNowM;
            var limit = EffectiveTerminalFallMps;
            var free = Mathf.Sqrt(2.0f * apex / GravityMps2);
            var down = free * GravityMps2 <= limit ? free : limit / GravityMps2 + (apex - limit * limit / (2.0f * GravityMps2)) / limit;
            return JumpSpeedMps / GravityMps2 + down;
        }
    }

    private static float SpeedForApex(float gravity, float apex)
    {
        var half = gravity * 0.5f / Engine.PhysicsTicksPerSecond;
        return -half + Mathf.Sqrt(half * half + 2.0f * gravity * apex);
    }

    public bool InputEnabled { get; private set; } = true;
    /// <summary>
    /// Set while a free camera owns the mouse (F2 zoomed out past the HUD's orbit threshold; the shelved F3 and F4 views the look
    /// harness still sets): movement input is relative to this heading instead of the body's, the body turns to face where it
    /// moves, and mouse look is off. Under water W then swims level across this heading (it swims the way you look only while the
    /// camera is the body's own: F1 and F2 at the shoulder).
    /// </summary>
    public float? MovementFrameYaw { get; private set; }
    public Camera3D EyeCamera { get; private set; } = null!;
    public float BodyHeightM => (float)Profile.HeightMeters;
    public float BodyRadiusM => (float)Profile.RadiusMeters;
    public float ReachM => (float)Profile.InteractionReachMeters;
    public int StepsClimbed { get; private set; }
    public int JumpsStarted { get; private set; }
    public bool IsBlocked { get; private set; }
    public Color AppearanceColor { get; private set; } = new("d28f63");
    public Vector3 LastSafePosition { get; private set; }
    /// <summary>
    /// The room's playable volume (the manifest's bounds: "avatars outside it are recovered"), set by the command host. The
    /// body never walks, slides or is pushed out of it sideways: it stops at the bound as at a wall, with the whole capsule
    /// inside. A generated landscape's ground runs a few centimetres past its bounds and then stops, with nothing beyond.
    /// Null (the body suites' fixtures) keeps no bound. It is enforced in the body, not by colliders, so nothing else meets
    /// it: rays to the sun, sight, the navigation mesh and sandbox placement see the room exactly as before.
    /// </summary>
    public Aabb? PlayableBounds { get; private set; }
    /// <summary>A body this far below the playable bounds is falling out of the room and is recovered (without bounds: 12 m below its spawn).</summary>
    public const float RecoverBelowBoundsM = 1.0f;
    /// <summary>Physics ticks on which the playable bounds held the body back.</summary>
    public int BoundsStops { get; private set; }
    public Vector3 CreationVelocity => _creationVelocity;
    public bool HasCreationGuard { get; private set; }
    /// <summary>Ticks on which a creation guard took back the creation's share of a move.</summary>
    public int CreationGuardStops { get; private set; }

    // ---- Climbing and swimming (founder's playtest, 8 October: "climbing cliffs and climbing trees", "it should feel
    // like you're swimming when you're in deep water"). The numbers are starting points for the founder to tune by feel.

    /// <summary>Any colliding face too steep to stand on can be climbed by pushing into it. Off for the companion, which floats instead (part 2).</summary>
    [Export] public bool CanClimb { get; set; } = true;
    /// <summary>Water (Room/RoomWater) slows wading and floats the body in water over its head. Off for the companion.</summary>
    [Export] public bool CanSwim { get; set; } = true;
    /// <summary>Slow and steady, against a walk of 0.32 m/s.</summary>
    [Export] public float ClimbSpeedMps { get; set; } = 0.12f;
    /// <summary>On the ground, pushing into a face this long grabs it; brushing past or bumping it never does. In the air a push grabs at once.</summary>
    [Export] public float ClimbGrabDelayS { get; set; } = 0.2f;
    /// <summary>The pull over the top: up beside the face, then over onto the ledge.</summary>
    [Export] public float PullOverSpeedMps { get; set; } = 0.25f;
    /// <summary>Swimming against the walk; Shift swims at the walk itself.</summary>
    [Export] public float SwimSpeedFactor { get; set; } = 0.6f;
    /// <summary>The wading speed just before the water floats the body (it slows gradually from the walk as the water deepens).</summary>
    [Export] public float WadeSlowestFactor { get; set; } = 0.6f;
    [Export] public float SwimAccelerationMps2 { get; set; } = 1.6f;
    /// <summary>The push must point within 45 degrees of straight into the face to grab it.</summary>
    public const float ClimbGrabCos = 0.7f;
    /// <summary>Held back by what it pushes into, a body grabs with its push within 60 degrees of straight in (it is not brushing past).</summary>
    public const float ClimbHeldGrabCos = 0.5f;
    /// <summary>Held back on a walkable slope steeper than this, pushing up it, the body scrambles: it climbs the slope.</summary>
    public const float ScrambleSlopeDeg = 35.0f;
    /// <summary>A face leaning out over the climber more than 20 degrees past vertical is a roof: it cannot be grabbed or climbed onto.</summary>
    public const float ClimbOverhangNormalY = -0.34f;
    /// <summary>How far the hands reach for the face each tick, and the gentle press that keeps them on it.</summary>
    public const float ClimbProbeM = 0.015f;
    public const float ClimbStickMps = 0.06f;
    /// <summary>Jump lets go: a small kick away from the face and up.</summary>
    public const float ClimbKickAwayMps = 0.25f;
    public const float ClimbKickUpMps = 0.2f;
    /// <summary>After letting go, the same push does not grab again at once.</summary>
    public const float RegrabDelayS = 0.35f;
    public const float PullOverMaxS = 1.0f;
    public const float SwimBobM = 0.003f;
    public const float SwimBobPeriodS = 2.4f;
    /// <summary>The visible body tips forward this far to lie along the water (the capsule and the eye stay upright).</summary>
    public const float SwimTiltDeg = 75.0f;
    /// <summary>Water's drag on the body's vertical speed: quadratic (a fall is broken within centimetres) and linear (it settles).</summary>
    public const float WaterDragQuadraticPerM = 40.0f;
    public const float WaterDragLinearPerS = 4.0f;
    public const float SwimSpringPerS = 6.0f;
    public const float SwimSpringMaxMps = 0.25f;
    /// <summary>The water catches a falling or plunging body with its eye at least this far above the surface (no diving this round).</summary>
    public const float EyeClearanceM = 0.005f;
    /// <summary>A creation force pulling a climber off its face faster than this lets go, and the body carries that momentum.</summary>
    public const float ClimbCreationLetGoMps = 0.1f;
    /// <summary>The ledge a climber pulls over onto may be up to about the chest above the feet.</summary>
    public float PullOverReachM => BodyHeightM * 0.55f;
    /// <summary>Water this deep (surface to bed) floats the body: the water would reach the eyes.</summary>
    public float SwimDepthM => BodyHeightM * 0.8f;
    /// <summary>Shallower than this, a swimmer finds its feet and wades (below SwimDepthM so the change never flickers).</summary>
    public float SwimExitDepthM => BodyHeightM * 0.7f;
    /// <summary>A swimmer's feet ride this far under the surface: the eye (8.7 cm up) stays 2.7 cm above the water.</summary>
    public float SwimFloatDepthM => BodyHeightM * 0.6f;

    // ---- Floating (the Gubble: "a ghost bubble, set apart from the world"; the founder, 8 October). It never climbs, swims
    // or walks on the ground: it hovers a little above whatever is under it, the ground or the water, with a gentle bob.

    /// <summary>The body hovers instead of walking, climbing or swimming (the Gubble). Set before the body enters the tree.</summary>
    [Export] public bool Floats { get; set; }
    /// <summary>How far the feet hover over the ground or the water: a fifth of the 10 cm body, plainly off the ground at that scale.</summary>
    [Export] public float HoverHeightM { get; set; } = 0.02f;
    /// <summary>The bob: up and down this far either side of the hover, once per HoverBobPeriodS.</summary>
    public const float HoverBobM = 0.004f;
    public const float HoverBobPeriodS = 2.6f;
    /// <summary>How fast a floating body rises (up a cliff, over a wall) and sinks (off a ledge), and how firmly it settles to its height.</summary>
    [Export] public float FloatRiseMps { get; set; } = 0.30f;
    [Export] public float FloatSinkMps { get; set; } = 0.25f;
    public const float HoverSpringPerS = 8.0f;
    /// <summary>
    /// How far under a floating body its ground or water is looked for at least. Within playable bounds it is looked for down to
    /// their floor (HoverSearchM): the garage island magnified ten times (13 October) has mesas 10 to 25 m over the land, and 3 m
    /// found nothing past their edges, so the Gubble held its height high over the player and said it could not get there. Over
    /// nothing even so, it settles toward FloatAltitudeFloorY (the height of where it is going), or else holds its height.
    /// </summary>
    public const float HoverSupportSearchM = 3.0f;
    /// <summary>
    /// Set each tick by whoever steers a floating body. RiseOverObstacles: something in its way that it cannot duck under is
    /// risen over (up the face, never through it). FloatAltitudeFloorY: it floats no lower than this (up to a player on a cliff
    /// top or in a tree), and over nothing at all it settles to this; null for none.
    /// </summary>
    public bool RiseOverObstacles { get; set; }
    public float? FloatAltitudeFloorY { get; set; }
    /// <summary>The top of what the floating body hovers over on the last tick (ground or water), or null over nothing within reach.</summary>
    public float? HoverSupportY { get; private set; }
    /// <summary>Whether the floating body hovered over water on the last tick.</summary>
    public bool HoversOverWater { get; private set; }
    /// <summary>True on a tick the floating body rose over something in its way.</summary>
    public bool RisingOver { get; private set; }
    /// <summary>Ticks a floating body spent rising over something in its way, and ducking under something low.</summary>
    public int RiseTicks { get; private set; }
    public int DuckTicks { get; private set; }

    // ---- The open sea and the way home (the founder, 9 October: "the kids want to be able to swim forever"). The sea has no
    // edge: no current, no wash ashore, no wall a child meets. Past the island the water answers from the room's sea where
    // its meshes stop (RoomSea.OpenWater). Home is one key away from anywhere: B fades to the jetty (the founder: every room
    // has one where its door was, and it may become a portal to other islands), else the nearest beach, else the spawn.

    /// <summary>The room's sea (Room/RoomSea), set by the command host; null for a room without one (the bounds are its edge).</summary>
    public RoomSea? Sea { get; private set; }
    /// <summary>
    /// The last safety net, this far (planar) from the island's middle: a swimmer who gets there is taken home as by B. Float
    /// precision decides it (SmallAvatarPhysicsTest.MeasureFarOut). The body's own motion stays even out to 10 km (the steps
    /// round the same way every tick, though at 10 km the 1 mm float spacing slows the fast swim by 8 %). What shakes is what
    /// the eye sees: positions near the camera move by whole float steps, 0.06 mm at 1 km (about a pixel on the Gubble 16 cm
    /// from the eye), 0.12 mm at 2 km, 1 mm at 10 km. At 1 km nothing shakes by more than a pixel, and it is 52 minutes of
    /// fast swimming (0.32 m/s) without a break, an hour and a half at the plain swim (0.19 m/s).
    /// </summary>
    public const float DefaultFarNetM = 1000.0f;
    public float FarNetM { get; set; } = DefaultFarNetM;

    // ---- The sea wraps round to home (the founder, 9 October, late night): swim far enough out and you come back to your own
    // island from the other side, through the mist. The horizon is only sea and sky; other islands come later by the jetty.

    /// <summary>How far past the look's seam (OpenSea.SeamFor: from there out the mist wholly hides the island) the wrap is. The founder tunes it.</summary>
    public const float WrapPastSeamM = 1.5f;
    /// <summary>
    /// The wrap's distance from the island's centre (RoomSea.OpenSeaCentreM): a swimmer going out past it comes in from the opposite
    /// side at the same distance, heading the same way, so it swims back toward the island. The seam plus WrapPastSeamM, set with the
    /// sea and the room's bounds (about 19 m on the garage, a minute and a few seconds' swim from the reef); infinite without a sea.
    /// A test seam: the far-net suites set it infinite.
    /// </summary>
    public float WrapM { get; set; } = float.PositiveInfinity;
    /// <summary>Times this body has wrapped round.</summary>
    public int Wraps { get; private set; }
    /// <summary>Raised when the body wraps round, with the move it made: what travels with it (the Gubble, the cameras) moves the same.</summary>
    public event System.Action<SmallPlayerController, Vector3>? Wrapped;
    /// <summary>The fade out on the way home, then the fade back in.</summary>
    public const float HomeFadeS = 0.6f;
    /// <summary>Trips home so far (B, the far net); the shore returns of an empty bar are ShoreReturns.</summary>
    public int HomeTrips { get; private set; }
    /// <summary>Where the body last went home to: "jetty", a beach's id, or "spawn" ("" before any).</summary>
    public string LastHome { get; private set; } = "";
    /// <summary>True from B (or an empty bar in deep water: ShoreReturns) until the fade back in ends: the body takes no input meanwhile.</summary>
    public bool GoingHome => _homeTime >= 0;
    /// <summary>How dark the fade home is now: 0 to 1 and back (the HUD draws it). The same fade takes a swimmer who ran out of stamina back to the shore.</summary>
    public float HomeFade => _homeTime < 0 ? 0 : _homeTime < HomeFadeS ? _homeTime / HomeFadeS : Mathf.Clamp(2 - _homeTime / HomeFadeS, 0, 1);
    /// <summary>Raised when the body has been put down at home, or back on the shore after an empty bar in deep water (LastHome says which: "shore"); the Gubble comes too.</summary>
    public event System.Action<SmallPlayerController>? WentHome;
    /// <summary>Where B goes, for the HUD: the jetty, else the beach, else the start.</summary>
    public string HomeName => Sea?.JettyData != null ? "the jetty" : Sea != null && Sea.Beaches.Count > 0 ? "the beach" : "the start";
    // ---- Diving (the founder's son, 9 October: "dive into the water and swim down under the surface"). Hold Ctrl to dive and
    // Space to rise; under water W swims the way you look (F1 and F2). No breath limit and no harm (the island's rule: no
    // punishment). Let go and the body drifts slowly up, so nobody gets stuck on the bottom. Nothing goes through the bed,
    // the open-sea bed included, which has no collider. The founder tunes these by playing.

    /// <summary>Under water, the swim's speed is this share of the swim at the surface (the plain swim 0.19 m/s, 0.32 with Shift).</summary>
    [Export] public float DiveSpeedFactor { get; set; } = 1.0f;
    /// <summary>How fast Ctrl takes a diver down, and Space up.</summary>
    [Export] public float DiveSinkMps { get; set; } = 0.12f;
    [Export] public float DiveRiseMps { get; set; } = 0.15f;
    /// <summary>With no key held under water, the body drifts up this fast.</summary>
    [Export] public float DriftUpMps { get; set; } = 0.03f;
    /// <summary>A swimmer this far under its floating depth is diving; above it, the surface's own float takes over.</summary>
    public const float DiveStartM = 0.01f;
    /// <summary>True on a tick the body swam under the surface (Ctrl held, or still on its way back up from a dive).</summary>
    public bool IsDiving { get; private set; }
    /// <summary>Whether the eye is under the water's surface (a read for the look's view under water).</summary>
    public bool EyeUnderWater => Water.Wet && GlobalPosition.Y + EyeHeightM < Water.SurfaceY;
    /// <summary>Ticks spent diving, and dives begun.</summary>
    public int DiveTicks { get; private set; }
    public int DiveStarts { get; private set; }

    /// <summary>Dive input from a caller other than the keyboard (Ctrl and Space); retained until replaced.</summary>
    public void SetDiveInput(bool down, bool up)
    {
        _diveDown = down;
        _diveUp = up;
    }

    /// <summary>Whether the body is out past the island's reef (the open sea).</summary>
    public bool AtSea => Sea != null && Sea.PastReefM(new Vector2(GlobalPosition.X, GlobalPosition.Z)) > 0;

    public bool IsClimbing => _climbing;
    /// <summary>Trusted host callbacks for mode consequences. Unbound fixture bodies retain Creative movement.</summary>
    public System.Func<float, float, bool>? ClimbBudget { get; set; }
    public System.Action<float, bool>? VitalsTick { get; set; }
    /// <summary>Trusted body observation: downward speed in m/s just before landing, and whether deep water caught it.</summary>
    public System.Action<float, bool>? FallObserved { get; set; }
    public System.Action<Vector3, Vector3>? ClimbCompleted { get; set; }
    /// <summary>
    /// The ledger seam (the mechanics round, game/scripts/native/Seams/StaminaLedger.cs): the kernel binds it (M1), and the body
    /// reports its sprint, deep-water and dive seconds to it once a tick and obeys its gate (M5; SmallPlayerController.Exertion.cs).
    /// Unbound, as today: nothing gated.
    /// </summary>
    public Seams.IStaminaLedger? StaminaLedger
    {
        get => _staminaLedger;
        set
        {
            _staminaLedger = value;
            if (value == null) _gate = Seams.StaminaGate.Free;
        }
    }
    private Seams.IStaminaLedger? _staminaLedger;
    private float _vitalsLandingSpeedMps;
    /// <summary>The last on-foot tick's descent before the water caught it (m/s): a deep-water landing's speed.</summary>
    private float _airDescentMps;
    private Vector3? _vitalsClimbStart;
    public bool IsPullingOver => _climbing && _pullPhase > 0;
    public bool IsSwimming => _swimming;
    /// <summary>The face being climbed (outward, unit); meaningful while IsClimbing.</summary>
    public Vector3 ClimbNormal => _climbNormal;
    public int Grabs { get; private set; }
    public int PullOvers { get; private set; }
    public int ClimbReleases { get; private set; }
    public int SwimStarts { get; private set; }
    /// <summary>Recoveries so far (R, a fall out of the room, or a non-finite position).</summary>
    public int Recoveries { get; private set; }
    /// <summary>The water at the body's feet on the last physics tick (RoomWater); Dry when there is none or the body cannot swim.</summary>
    public RoomWater.Column Water { get; private set; } = RoomWater.Column.Dry;
    /// <summary>How far the visible body is tipped toward lying along the water (0 upright, 1 fully).</summary>
    public float SwimTilt => _tilt;
    /// <summary>
    /// The command host's answer to "are this body's hands full?" (it is carrying something). Climbing takes both hands: a
    /// body that is carrying does not grab a face, and one that picks something up lets go.
    /// </summary>
    public System.Func<bool>? HandsFull { get; set; }

    // ---- The characters round (docs/runs/RUN-2-CHARACTERS.md, Wave 0): hooks for a drawn body. None of them moves the capsule.

    /// <summary>The longest a tumble may hold control (a knockout's is about 2 s).</summary>
    public const float MaxControlHoldS = 5.0f;
    /// <summary>What draws the body now; null for the prototype figure.</summary>
    public IBodyVisual? BodyVisual => _bodyVisual;
    /// <summary>The state of the last physics tick (BodyState.Tick is 0 before the first).</summary>
    public BodyState LastBodyState { get; private set; }
    /// <summary>The drawn character the player chose (a token, its folder's id), or "" for the prototype figure.</summary>
    public string CharacterId { get; private set; } = "";
    /// <summary>Told when CharacterId changes; the composition root builds the new visual (RoomWorld).</summary>
    public System.Action<string>? CharacterChanged { get; set; }
    /// <summary>Control is held: movement, jumps, sprint and dives are ignored (a tumble). Looking round still works.</summary>
    public bool ControlHeld => _controlHoldS > 0;
    /// <summary>Seconds of control hold left.</summary>
    public float ControlHoldS => _controlHoldS;
    /// <summary>The movement asked for while control is held (zero otherwise): a tumble's early get-up reads it.</summary>
    public Vector2 HeldInput { get; private set; }
    /// <summary>
    /// How long the command host waits after a fall knocks the player out before it recovers them at the spawn, so the knockout
    /// tumble plays where they landed (0, the default: at once, as before the characters round). The tumble sets it.
    /// </summary>
    public float KnockoutRecoveryDelayS
    {
        get => _knockoutRecoveryDelayS;
        set => _knockoutRecoveryDelayS = float.IsFinite(value) ? Mathf.Clamp(value, 0, MaxControlHoldS) : 0;
    }

    /// <summary>
    /// Draw the body with a visual in place of the prototype figure, or with the prototype again (null). The prototype's parts
    /// stay under the visual root, hidden, so the tests and the look still find them; the visual's root is added beside them and
    /// marked for the eye views. The caller owns a visual it replaces and frees it. Before the body is ready it applies at _Ready.
    /// </summary>
    public void SetBodyVisual(IBodyVisual? visual)
    {
        if (ReferenceEquals(visual, _bodyVisual)) return;
        if (_bodyVisual != null && GodotObject.IsInstanceValid(_bodyVisual.Root) && VisualRoot != null && _bodyVisual.Root.GetParent() == VisualRoot)
            VisualRoot.RemoveChild(_bodyVisual.Root);
        _bodyVisual = visual;
        if (VisualRoot != null && _ready) ApplyBodyVisual();
    }

    /// <summary>
    /// Choose the drawn character by its id (a lowercase token) or the prototype (""). Refuses anything else. Only records the
    /// choice and tells CharacterChanged; the visual comes from the character library through the composition root.
    /// </summary>
    public bool SetCharacterId(string id)
    {
        if (id == null || (id.Length > 0 && !IsCharacterToken(id))) return false;
        if (id == CharacterId) return true;
        CharacterId = id;
        CharacterChanged?.Invoke(id);
        return true;
    }

    /// <summary>The room state's token: a lowercase letter, then up to 63 of a-z, 0-9, '_' and '-' (common.schema.json's token).</summary>
    public static bool IsCharacterToken(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id[0] is < 'a' or > 'z') return false;
        foreach (var c in id)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return false;
        return true;
    }

    /// <summary>Hold control for a while (the longer of what is left and this, at most MaxControlHoldS): a tumble.</summary>
    public void HoldControl(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0) return;
        _controlHoldS = Mathf.Max(_controlHoldS, Mathf.Min(seconds, MaxControlHoldS));
    }

    /// <summary>Give control back now (a tumble's get-up).</summary>
    public void ReleaseControl()
    {
        _controlHoldS = 0;
        HeldInput = Vector2.Zero;
    }

    private void ApplyBodyVisual()
    {
        foreach (var part in _prototypeParts)
            if (GodotObject.IsInstanceValid(part)) part.Visible = _bodyVisual == null;
        if (_bodyVisual == null || !GodotObject.IsInstanceValid(_bodyVisual.Root)) return;
        var root = _bodyVisual.Root;
        if (root.GetParent() is { } parent && parent != VisualRoot) parent.RemoveChild(root);
        if (root.GetParent() == null) VisualRoot.AddChild(root);
        GarageGame.Native.Look.EyeView.MarkBody(VisualRoot, OwnBodyLayer);
    }

    protected virtual WorldScaleProfile Profile => WorldScaleProfile.SmallPlayer;
    /// <summary>
    /// The render layer of this body's drawn parts. The body's own eye camera leaves that layer out of its cull mask, so the
    /// first-person view (F1) never shows the inside of the head, while every other camera, the Gubble's eye among them, still
    /// draws the body: a per-camera choice (Look/EyeView.cs), never the body hidden for everyone.
    /// </summary>
    protected virtual uint OwnBodyLayer => GarageGame.Native.Look.EyeView.PlayerBodyLayer;
    protected Node3D VisualRoot = null!;
    protected StandardMaterial3D BodyMaterial = null!;
    protected bool HasSafePosition;

    private CapsuleShape3D _capsule = null!;
    private Vector2 _control;
    private float _jumpBuffer;
    private float _coyote;
    private bool _sprint;
    private float _pitch;
    private Vector3 _spawnPoint;
    private bool _ready;
    /// <summary>
    /// This body alone, as a ray's or a shape's exclusions: made once (S12). A new Godot array for every query, and a new Variant for
    /// every key read from its answer, were Godot values the .NET finalizer freed later, on its own thread, by the hundred a second.
    /// Pass it to PhysicsRayQueryParameters3D.Create itself: left out there, Create makes an empty array of its own every call.
    /// </summary>
    protected Array<Rid> ExcludeSelf => _excludeSelf ??= new Array<Rid> { GetRid() };
    private Array<Rid>? _excludeSelf;

    /// <summary>A ray answer's keys, as Variants made once.</summary>
    protected static readonly Variant HitPosition = "position", HitNormal = "normal";

    /// <summary>The normal of a slide collision of the last move. Its KinematicCollision3D is disposed at once: the engine reuses it then.</summary>
    protected Vector3 SlideNormal(int index)
    {
        using var collision = GetSlideCollision(index);
        return collision.GetNormal();
    }

    private readonly KinematicCollision3D _stepContact = new();
    private Vector3 _creationAcceleration;
    private Vector3 _creationVelocity;
    private Vector3 _creationApplied;
    private float _creationGlideLimit;
    private Callable _creationGuard;
    private Dictionary _worldPhysics = new();
    private bool _climbing;
    private Vector3 _climbNormal = Vector3.Back;
    private float _grabPush;
    private float _regrabBlock;
    private int _pullPhase;
    private Vector3 _pullTarget;
    private float _pullTime;
    private int _faceLost;
    private readonly KinematicCollision3D _climbContact = new();
    private bool _swimming;
    private bool _waterLeap;
    private float _swimClock;
    private float _tilt;
    private float _visualYaw;
    private float _hoverClock;
    private float _homeTime = -1;
    private Vector3 _ownVelocity;
    private Vector3 _creationPushed;
    private float _swimSurfaceY = float.NaN;
    private bool _openSeaWater;
    private bool _diveDown;
    private bool _diveUp;
    private bool _wasDiving;
    private bool _homeArrived;
    private IBodyVisual? _bodyVisual;
    private readonly System.Collections.Generic.List<Node3D> _prototypeParts = new();
    private float _controlHoldS;
    private float _knockoutRecoveryDelayS;
    private ulong _bodyTick;
    private double _distanceM;
    private float _airTimeS;

    public override void _Ready()
    {
        MotionMode = MotionModeEnum.Grounded;
        CollisionLayer = 2;
        // A companion must never become an obstacle trapping direct player control. The hidden layer holds the parts that
        // are never drawn (a tree's climbing pole and crown caps): the body climbs and stands on them (RoomBuilder.BodyMask).
        CollisionMask = RoomBuilder.BodyMask;
        SafeMargin = SafeMarginM;
        FloorSnapLength = FloorSnapM;
        FloorMaxAngle = Mathf.DegToRad(45.0f);
        FloorStopOnSlope = true;
        FloorConstantSpeed = true;
        _capsule = new CapsuleShape3D { Height = BodyHeightM, Radius = BodyRadiusM };
        AddChild(new CollisionShape3D
        {
            Name = "SmallBodyCollision", Shape = _capsule,
            Position = Vector3.Up * (BodyHeightM * 0.5f)
        });
        // Near plane well inside the capsule radius so a wall a body-width away never clips; a room-scale
        // far plane keeps depth precision for the Compatibility renderer.
        EyeCamera = new Camera3D
        {
            Name = "EyeCamera", Position = Vector3.Up * (float)Profile.EyeHeightMeters,
            Near = Mathf.Min(0.01f, BodyRadiusM * 0.25f), Far = (GetParent() as RoomWorld)?.Look?.FarPlaneM ?? 100.0f, Fov = 72.0f, Current = false
        };
        AddChild(EyeCamera);
        BuildVisual();
        foreach (var child in VisualRoot.GetChildren())
            if (child is Node3D part) _prototypeParts.Add(part);
        GarageGame.Native.Look.EyeView.MarkBody(VisualRoot, OwnBodyLayer);
        EyeCamera.CullMask = GarageGame.Native.Look.EyeView.AllLayers & ~OwnBodyLayer;
        ReferenceGravityMps2 = (float)DefaultWorldPhysics()["gravity_mps2"].AsDouble();
        if (WorldPhysicsRevision < 0) ApplyWorldPhysics(DefaultWorldPhysics());
        _spawnPoint = GlobalPosition;
        LastSafePosition = _spawnPoint;
        _ready = true;
        if (_bodyVisual != null) ApplyBodyVisual();
    }

    protected virtual void BuildVisual()
    {
        VisualRoot = new Node3D { Name = "OriginalPrototypeBody" };
        AddChild(VisualRoot);
        BodyMaterial = new StandardMaterial3D { AlbedoColor = AppearanceColor, Roughness = 0.9f };
        // The pill reaches down to the feet, where the collision capsule's lowest point is. It used to start 3 %
        // of the body height up (3 mm on the 10 cm body, 7.2 mm on the old 0.24 m companion), so both avatars
        // hovered over the rug in the look captures.
        AddMesh(VisualRoot, new CapsuleMesh
        {
            Radius = BodyRadiusM * 0.82f, Height = BodyHeightM * 0.75f,
            RadialSegments = 12, Rings = 4
        }, new Vector3(0, BodyHeightM * 0.375f, 0), BodyMaterial);
        var face = new StandardMaterial3D { AlbedoColor = new Color("eed5a3"), Roughness = 1 };
        AddMesh(VisualRoot, new SphereMesh
        {
            Radius = BodyRadiusM * 0.82f, Height = BodyRadiusM * 1.64f,
            RadialSegments = 12, Rings = 6
        }, new Vector3(0, BodyHeightM * 0.81f, 0), face);
        var dark = new StandardMaterial3D { AlbedoColor = new Color("183631"), Roughness = 1 };
        var eye = BodyRadiusM * 0.117f;
        foreach (var side in new[] { -1.0f, 1.0f })
            AddMesh(VisualRoot, new SphereMesh { Radius = eye, Height = eye * 2, RadialSegments = 8, Rings = 4 },
                new Vector3(side * BodyRadiusM * 0.333f, BodyHeightM * 0.83f, -BodyRadiusM * 0.72f), dark);
    }

    protected static MeshInstance3D AddMesh(Node3D parent, Mesh mesh, Vector3 position, Material material)
    {
        var instance = new MeshInstance3D { Mesh = mesh, Position = position, MaterialOverride = material };
        parent.AddChild(instance);
        return instance;
    }

    public void SetAppearance(Color color)
    {
        if (!float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B)) return;
        AppearanceColor = new Color(Mathf.Clamp(color.R, 0, 1), Mathf.Clamp(color.G, 0, 1), Mathf.Clamp(color.B, 0, 1), 1);
        if (BodyMaterial != null) BodyMaterial.AlbedoColor = AppearanceColor;
    }

    /// <summary>Right and forward input, clamped to the unit disc. Retained until replaced. A jump request is buffered briefly.</summary>
    public void SetControlInput(Vector2 rightForward, bool jump = false, bool sprint = false)
    {
        _control = rightForward.IsFinite() ? rightForward.LimitLength() : Vector2.Zero;
        if (InputEnabled && jump) _jumpBuffer = JumpBufferS;
        _sprint = sprint;
    }

    public void SetInputEnabled(bool enabled)
    {
        InputEnabled = enabled;
        _control = Vector2.Zero;
        _jumpBuffer = 0;
        Velocity = new Vector3(0, Velocity.Y, 0);
    }

    public void SetSpawnPoint(Vector3 feetPosition)
    {
        if (feetPosition.IsFinite()) _spawnPoint = feetPosition;
    }

    /// <summary>The trusted host's room bounds (see PlayableBounds). Refused unless finite and wider than the body.</summary>
    public bool SetPlayableBounds(Aabb bounds)
    {
        var radius = (float)Profile.RadiusMeters;
        if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite() || bounds.Size.X <= radius * 2 || bounds.Size.Z <= radius * 2 || bounds.Size.Y <= 0) return false;
        PlayableBounds = bounds;
        PlaceWrap();
        return true;
    }

    /// <summary>Test seam for the body suites: no playable bounds again.</summary>
    internal void ClearPlayableBounds()
    {
        PlayableBounds = null;
        PlaceWrap();
    }

    /// <summary>The trusted host's sea for this room (null for none).</summary>
    public void SetSea(RoomSea? sea)
    {
        Sea = sea;
        PlaceWrap();
    }

    /// <summary>The wrap from the sea and the bounds (OpenSea.SeamFor needs both): beyond the seam, or nowhere.</summary>
    private void PlaceWrap() =>
        WrapM = Sea != null && PlayableBounds is { } bounds ? GarageGame.Native.Look.OpenSea.SeamFor(Sea, bounds) + WrapPastSeamM : float.PositiveInfinity;

    /// <summary>
    /// The wrap, after the move: a swimmer (not a floating body: the Gubble travels with the player instead) out past WrapM and still
    /// going out is put at the point opposite through the island's centre, at the same distance and height, keeping its heading and
    /// speed, so it is now coming in. Going out is what decides it, so a swimmer just wrapped never wraps straight back.
    /// </summary>
    private void WrapRound()
    {
        if (Sea == null || Floats || GoingHome || !float.IsFinite(WrapM)) return;
        var flat = new Vector2(GlobalPosition.X, GlobalPosition.Z) - Sea.OpenSeaCentreM;
        var reach = flat.Length();
        if (reach <= WrapM) return;
        if (Velocity.X * flat.X + Velocity.Z * flat.Y <= 0) return;
        var move = new Vector3(-2f * flat.X, 0, -2f * flat.Y);
        GlobalPosition += move;
        ResetPhysicsInterpolation();
        Wraps++;
        Wrapped?.Invoke(this, move);
    }

    /// <summary>
    /// Whether the bounds hold the body at their sides. Not round an island: its land never reaches them and the sea has no
    /// edge (the far net, FarNetM, is the last safety net out there). The top and the bottom hold either way.
    /// </summary>
    protected bool BoundsHoldSides => PlayableBounds != null && Sea == null;

    /// <summary>Whether feet here keep the whole capsule inside the playable bounds horizontally (always, without bounds or round an island).</summary>
    public bool InsidePlayableBounds(Vector3 feetPosition)
    {
        if (PlayableBounds is not { } bounds || !BoundsHoldSides) return true;
        var radius = BodyRadiusM - 0.0005f;
        return feetPosition.X >= bounds.Position.X + radius && feetPosition.X <= bounds.End.X - radius &&
               feetPosition.Z >= bounds.Position.Z + radius && feetPosition.Z <= bounds.End.Z - radius;
    }

    /// <summary>The horizontal velocity cut so this tick's motion ends inside the playable bounds: at the bound, the outward part stops.</summary>
    private Vector3 WithinBounds(Vector3 horizontal, float dt)
    {
        if (PlayableBounds is not { } bounds || !BoundsHoldSides || dt <= 0) return horizontal;
        var here = GlobalPosition;
        float Axis(float velocity, float position, float low, float high)
        {
            var next = position + velocity * dt;
            if (velocity > 0 && next > high) return Mathf.Max(0, (high - position) / dt);
            if (velocity < 0 && next < low) return Mathf.Min(0, (low - position) / dt);
            return velocity;
        }
        var limited = new Vector3(
            Axis(horizontal.X, here.X, bounds.Position.X + BodyRadiusM, bounds.End.X - BodyRadiusM), horizontal.Y,
            Axis(horizontal.Z, here.Z, bounds.Position.Z + BodyRadiusM, bounds.End.Z - BodyRadiusM));
        if (limited != horizontal) BoundsStops++;
        return limited;
    }

    /// <summary>
    /// After the move: a slide, a climb or a creation push that still crossed the bound is put back on it, and its outward
    /// speed stops. The top holds the whole body under it too (a climber, a pull-over, a leap).
    /// </summary>
    private void HoldInsideBounds()
    {
        if (PlayableBounds is not { } bounds) return;
        var here = GlobalPosition;
        var top = bounds.End.Y - BodyHeightM;
        var sides = BoundsHoldSides;
        var held = new Vector3(
            sides ? Mathf.Clamp(here.X, bounds.Position.X + BodyRadiusM, bounds.End.X - BodyRadiusM) : here.X, bounds.Size.Y > BodyHeightM ? Mathf.Min(here.Y, top) : here.Y,
            sides ? Mathf.Clamp(here.Z, bounds.Position.Z + BodyRadiusM, bounds.End.Z - BodyRadiusM) : here.Z);
        if (held == here) return;
        GlobalPosition = held;
        Velocity = new Vector3(held.X != here.X ? 0 : Velocity.X, held.Y != here.Y ? Mathf.Min(0, Velocity.Y) : Velocity.Y, held.Z != here.Z ? 0 : Velocity.Z);
        _creationVelocity = new Vector3(held.X != here.X ? 0 : _creationVelocity.X, _creationVelocity.Y, held.Z != here.Z ? 0 : _creationVelocity.Z);
        BoundsStops++;
    }

    /// <summary>A floating body out past the far net is put back on it, and its outward speed stops.</summary>
    private void HoldInsideFarNet()
    {
        var flat = new Vector2(GlobalPosition.X, GlobalPosition.Z) - Sea!.MiddleM;
        var reach = flat.Length();
        if (reach <= FarNetM || reach <= 0) return;
        var outward = flat / reach;
        var held = Sea.MiddleM + outward * FarNetM;
        GlobalPosition = new Vector3(held.X, GlobalPosition.Y, held.Y);
        var along = Velocity.X * outward.X + Velocity.Z * outward.Y;
        if (along > 0) Velocity -= new Vector3(outward.X, 0, outward.Y) * along;
        BoundsStops++;
    }

    /// <summary>The trusted host's gravity and wind. Accepts only a valid profile with a newer revision.</summary>
    public bool SetWorldPhysics(Dictionary profile)
    {
        var script = GD.Load<GDScript>(WorldPhysicsScript);
        if (!script.Call("validate", profile).AsBool() || profile["revision"].AsInt32() <= WorldPhysicsRevision) return false;
        ApplyWorldPhysics(profile);
        return true;
    }

    /// <summary>
    /// Where the G key sends the next world physics preset: the command host sets this to its world.set_physics path
    /// (Kernel/CommandHost.PlayerPhysics), so the key travels the single command path as the player. Unset, G does nothing.
    /// </summary>
    public System.Func<string, bool>? WorldPhysicsRequest { get; set; }

    /// <summary>The preset after the current one in world_physics_profile.gd PRESET_IDS (tuned, real, floaty, then tuned again).</summary>
    public string NextWorldPhysicsPreset()
    {
        var ids = GD.Load<GDScript>(WorldPhysicsScript).GetScriptConstantMap()["PRESET_IDS"].AsGodotArray();
        var next = 0;
        for (var index = 0; index < ids.Count; index++)
            if (ids[index].AsString() == WorldPhysicsId) next = (index + 1) % ids.Count;
        return ids[next].AsString();
    }

    /// <summary>The G key: ask the command host for the next preset. False when no host is attached or it refused.</summary>
    public bool RequestNextWorldPhysics() => WorldPhysicsRequest?.Invoke(NextWorldPhysicsPreset()) ?? false;

    /// <summary>Test seam for the body suites, which run without a command host: apply the next preset directly.</summary>
    internal string CycleWorldPhysics()
    {
        var profile = GD.Load<GDScript>(WorldPhysicsScript).Call("preset", NextWorldPhysicsPreset(), WorldPhysicsRevision + 1).AsGodotDictionary();
        SetWorldPhysics(profile);
        PrintWorldPhysics();
        return WorldPhysicsId;
    }

    /// <summary>The playtest's console line for the active world physics.</summary>
    public void PrintWorldPhysics() =>
        GD.Print($"PHYSICS_PROFILE {WorldPhysicsId} gravity={GravityMps2:0.##} m/s2 jump={JumpApexNowM * 100:0.#} cm airtime={JumpAirtimeS:0.00} s terminal_fall={EffectiveTerminalFallMps:0.##} m/s air_control={AirControl:0.##}");

    /// <summary>The active world profile, as the validated dictionary it was accepted from (a copy).</summary>
    public Dictionary WorldPhysicsProfile => _worldPhysics.Duplicate(true);

    /// <summary>
    /// Free-camera movement (F2 zoomed out): movement input is relative to <paramref name="yaw"/> and the body turns to face
    /// its motion. Null returns to body-relative movement and mouse look (F1, F2 near the shoulder).
    /// </summary>
    public void SetMovementFrame(float? yaw) =>
        MovementFrameYaw = yaw is { } value && float.IsFinite(value) ? value : null;

    /// <summary>Test seam for the ×10 import-scale measurement only: it bypasses the room physics bounds.</summary>
    internal void ScaleWorldPhysicsForProbe(float scale)
    {
        GravityMps2 *= scale;
        WorldTerminalFallMps *= scale;
    }

    private static Dictionary DefaultWorldPhysics() =>
        GD.Load<GDScript>(WorldPhysicsScript).GetScriptConstantMap()["DEFAULT"].AsGodotDictionary();

    private void ApplyWorldPhysics(Dictionary profile)
    {
        _worldPhysics = profile.Duplicate(true);
        WorldPhysicsId = profile["id"].AsString();
        WorldPhysicsRevision = profile["revision"].AsInt32();
        GravityMps2 = (float)profile["gravity_mps2"].AsDouble();
        WorldTerminalFallMps = (float)profile["terminal_fall_mps"].AsDouble();
        AirControl = (float)profile["air_control"].AsDouble();
        WindMps = new Vector2((float)profile["wind_x_mps"].AsDouble(), (float)profile["wind_z_mps"].AsDouble());
    }

    /// <summary>
    /// Creation forces for the next physics ticks. Without a valid position guard, or with a non-finite
    /// acceleration, every creation contribution is removed at once (consent revoked, effect expired).
    /// </summary>
    public void SetCreationEffects(Vector3 acceleration, float glideLimit, Callable positionGuard)
    {
        if (!IsCallable(positionGuard) || !acceleration.IsFinite() || !float.IsFinite(glideLimit))
        {
            ClearCreationMotion();
            return;
        }
        if (acceleration.IsZeroApprox())
        {
            Velocity -= _creationApplied;
            _creationApplied = Vector3.Zero;
            _creationVelocity = Vector3.Zero;
        }
        _creationAcceleration = acceleration.LimitLength(MaxCreationAccelerationMps2);
        _creationGlideLimit = Mathf.Clamp(glideLimit, 0, MaxGlideLimitMps);
        _creationGuard = positionGuard;
        HasCreationGuard = true;
    }

    public void ClearCreationMotion()
    {
        Velocity -= _creationApplied;
        _creationApplied = Vector3.Zero;
        _creationVelocity = Vector3.Zero;
        _creationAcceleration = Vector3.Zero;
        _creationGlideLimit = 0;
        _creationGuard = default;
        HasCreationGuard = false;
    }

    /// <summary>Guards come from GDScript as method callables (object and method name) or from C# as delegates.</summary>
    private static bool IsCallable(Callable callable) =>
        callable.Delegate != null || (callable.Target != null && GodotObject.IsInstanceValid(callable.Target) && callable.Method != null && callable.Method.ToString().Length > 0);

    private bool GuardAllows(Vector3 position) => !HasCreationGuard || _creationGuard.Call(position).AsBool();

    /// <summary>Only teleport to a supported, unoccupied surface near the supplied foot height.</summary>
    public bool TryTeleportTo(Vector3 feetPosition)
    {
        if (!_ready || !feetPosition.IsFinite() || !InsidePlayableBounds(feetPosition) || !FindSupportedPosition(feetPosition, out var position)) return false;
        GlobalPosition = position;
        // With physics interpolation on, a teleport must not be drawn as a glide from the old place to the new one.
        ResetPhysicsInterpolation();
        StopClimbingAndSwimming();
        ResetVitalsObservation();
        Velocity = Vector3.Zero;
        _jumpBuffer = 0;
        LastSafePosition = position;
        HasSafePosition = true;
        // Put down somewhere else, the body has not walked in from anywhere: it stands again before it has a shore to go back to.
        ShoreEntry = null;
        return true;
    }

    /// <summary>
    /// Put a floating body here, held in the air, if the whole capsule fits (the Gubble beside a player on a crowded jetty).
    /// Only for a body that floats: anything else would fall.
    /// </summary>
    protected bool TryPlaceFloating(Vector3 feetPosition)
    {
        if (!_ready || !Floats || !feetPosition.IsFinite() || !CapsuleFits(feetPosition)) return false;
        GlobalPosition = feetPosition;
        ResetPhysicsInterpolation();
        StopClimbingAndSwimming();
        Velocity = Vector3.Zero;
        return true;
    }

    /// <summary>Back to the last safe footing, else the spawn. While a creation moves the body, a checkpoint the guard refuses is skipped.</summary>
    public bool Recover()
    {
        Recoveries++;
        SetControlInput(Vector2.Zero);
        Velocity = Vector3.Zero;
        var guarded = HasCreationGuard;
        var recovered = false;
        if (HasSafePosition && (!guarded || GuardAllows(LastSafePosition)) && TryTeleportTo(LastSafePosition)) recovered = true;
        else if ((!guarded || GuardAllows(_spawnPoint)) && TryTeleportTo(_spawnPoint)) recovered = true;
        else
            foreach (var offset in new[] { Vector3.Right, Vector3.Left, Vector3.Forward, Vector3.Back })
            {
                var candidate = _spawnPoint + offset * (BodyHeightM * 0.8f);
                if ((!guarded || GuardAllows(candidate)) && TryTeleportTo(candidate)) { recovered = true; break; }
            }
        ClearCreationMotion();
        return recovered;
    }

    /// <summary>Injury recovery returns to the room spawn, rather than the last high ledge. Never teleports into solid geometry.</summary>
    public bool RecoverAtSpawn()
    {
        SetControlInput(Vector2.Zero);
        ClearCreationMotion();
        if (TryTeleportTo(_spawnPoint)) { Recoveries++; return true; }
        foreach (var offset in new[] { Vector3.Right, Vector3.Left, Vector3.Forward, Vector3.Back })
            if (TryTeleportTo(_spawnPoint + offset * BodyHeightM)) { Recoveries++; return true; }
        return false;
    }

    /// <summary>Teleports and a change of mode start a new observation; they never manufacture a completed climb or a fall.</summary>
    public void ResetVitalsObservation()
    {
        _vitalsLandingSpeedMps = 0;
        _vitalsClimbStart = null;
    }

    /// <summary>How far a pixel of mouse motion turns the view (the feel the captured mouse had).</summary>
    public const float LookRadiansPerPixel = 0.0025f;

    /// <summary>
    /// Turn the body and tilt the eye by a mouse motion in pixels: the HUD's look drag in F1 and in F2 near the shoulder (the cursor
    /// is free otherwise, the Glow playtest). Nothing while input is off or a view frame steers movement (F2 zoomed out to orbit).
    /// </summary>
    public void TurnView(Vector2 relative)
    {
        if (!InputEnabled || MovementFrameYaw != null || !relative.IsFinite()) return;
        RotateY(-relative.X * LookRadiansPerPixel);
        _pitch = Mathf.Clamp(_pitch - relative.Y * LookRadiansPerPixel, -1.35f, 1.35f);
        EyeCamera.Rotation = new Vector3(_pitch, 0, 0);
    }

    /// <summary>
    /// Face a heading and tilt the eye in one go, with no swing between the old view and the new: the HUD's F2 wheel, zooming in from
    /// the orbit view, hands the camera's heading and pitch to the body, so the shoulder camera ends up looking where the orbit
    /// camera looked (a free camera and a body turned to face its motion would otherwise jump when the shoulder view takes over).
    /// </summary>
    public void SetView(float yaw, float eyePitch)
    {
        if (!float.IsFinite(yaw) || !float.IsFinite(eyePitch)) return;
        Rotation = new Vector3(Rotation.X, yaw, Rotation.Z);
        _pitch = Mathf.Clamp(eyePitch, -1.35f, 1.35f);
        EyeCamera.Rotation = new Vector3(_pitch, 0, 0);
        ResetPhysicsInterpolation();
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!InputEnabled || !ReadKeyboard) return;
        // The controls are input actions (project.godot, [input]); Hit is a fresh press of one, not a held key repeating.
        var press = PlayerControls.Normalise(input);
        if (PlayerControls.Fresh(press))
        {
            if (PlayerControls.Hit(press, Act.Jump)) _jumpBuffer = JumpBufferS;
            if (PlayerControls.Hit(press, Act.BodyRecover)) Recover();
            // Home, from anywhere (the founder, 9 October): to the jetty, else the nearest beach, else the spawn.
            if (PlayerControls.Hit(press, Act.BodyHome)) RequestHome();
            // World physics is a world change: it sends world.set_physics through the command host as the player.
            if (PlayerControls.Hit(press, Act.PhysicsNext)) RequestNextWorldPhysics();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_ready) return;
        var dt = Mathf.Clamp((float)delta, 0, 0.05f);
        _vitalsLandingSpeedMps = 0;
        // Separate last tick's creation contribution from the body's own motion.
        Velocity -= _creationApplied;
        _creationApplied = Vector3.Zero;
        UpdateHomeTrip(dt);
        var control = InputEnabled && !GoingHome ? _control : Vector2.Zero;
        var sprint = _sprint;
        var diveDown = InputEnabled && !GoingHome && _diveDown;
        var diveUp = InputEnabled && !GoingHome && _diveUp;
        if (GoingHome) _jumpBuffer = 0;
        if (InputEnabled && ReadKeyboard && !GoingHome)
        {
            control = new Vector2(
                (PlayerControls.Held(Act.MoveRight) ? 1 : 0) - (PlayerControls.Held(Act.MoveLeft) ? 1 : 0),
                (PlayerControls.Held(Act.MoveForward) ? 1 : 0) - (PlayerControls.Held(Act.MoveBack) ? 1 : 0)).LimitLength();
            sprint = PlayerControls.Held(Act.MoveSprint);
            diveDown = PlayerControls.Held(Act.MoveDive);
            diveUp = PlayerControls.Held(Act.Jump);
        }
        // A tumble holds control: the body still falls, slides and is caught by water, but takes no input (the movement asked
        // for is kept for the tumble's early get-up).
        if (_controlHoldS > 0)
        {
            _controlHoldS = Mathf.Max(0, _controlHoldS - dt);
            HeldInput = control;
            control = Vector2.Zero;
            sprint = false;
            diveDown = false;
            diveUp = false;
            _jumpBuffer = 0;
        }
        else HeldInput = Vector2.Zero;
        // An empty bar takes the run away until the first bar is full again (the stamina ledger's gate); the walk keeps its pace.
        if (_gate.NoRun && !Floats) sprint = false;
        var onFloor = IsOnFloor();
        var wasSwimming = _swimming;
        _coyote = onFloor ? CoyoteTimeS : Mathf.Max(0, _coyote - dt);
        _regrabBlock = Mathf.Max(0, _regrabBlock - dt);
        var local = new Vector3(control.X, 0, -control.Y);
        Vector3 wish;
        if (MovementFrameYaw is { } frameYaw)
        {
            // Free camera: input is relative to the view, and the body turns to face where it goes.
            wish = new Basis(Vector3.Up, frameYaw) * local;
            if (wish.LengthSquared() > 0.0025f)
            {
                var facing = Mathf.Atan2(-wish.X, -wish.Z);
                Rotation = new Vector3(Rotation.X, Mathf.RotateToward(Rotation.Y, facing, TurnRateRadPerS * dt), Rotation.Z);
            }
        }
        else wish = GlobalBasis * local;
        wish.Y = 0;
        var before = GlobalPosition;
        _ownVelocity = Vector3.Zero;
        _creationPushed = Vector3.Zero;
        SampleWater(dt);
        // Where the body stands now, with the water read at the very same place (so it is dry there by the water's own account).
        RememberShoreEntry();
        _wasDiving = IsDiving;
        IsDiving = false;
        // What the bar pays for beyond a climb (the ledger): which way the body moved this tick, and in the water whether a key moved it.
        var onFoot = false;
        var swam = false;
        _stroked = false;
        // A bubble that holds the body moves it, and the body's own exit comes first (SmallPlayerController.Bubble.cs); out of it, the body falls.
        if (RideBubble(dt, wish)) { }
        else if (_climbing) Climb(dt, control);
        else if (Floats) Hover(dt, wish, sprint);
        else if (UpdateSwimming())
        {
            swam = true;
            Swim(dt, wish, sprint, control, diveDown, diveUp);
        }
        else
        {
            onFoot = true;
            Walk(dt, wish, sprint, onFloor);
        }
        HoldInsideBounds();
        WrapRound();
        // The far net, hours of swimming out: home as by B. A floating body (the Gubble, sent off on its own) is held at the
        // net instead, as at a wall (Codex Astra's review), and says blocked through its goal.
        if (Sea != null && !Floats && !GoingHome && new Vector2(GlobalPosition.X, GlobalPosition.Z).DistanceTo(Sea.MiddleM) > FarNetM) BeginHome();
        if (Sea != null && Floats) HoldInsideFarNet();
        // Project the creation contribution along contacts the same way MoveAndSlide projects motion.
        for (var index = 0; index < GetSlideCollisionCount(); index++)
        {
            var normal = SlideNormal(index);
            if (_creationApplied.Dot(normal) < 0) _creationApplied = _creationApplied.Slide(normal);
            if (_creationVelocity.Dot(normal) < 0) _creationVelocity = _creationVelocity.Slide(normal);
        }
        if (HasCreationGuard && _creationPushed != Vector3.Zero && !GuardAllows(GlobalPosition))
        {
            // Creation motion may not carry the body into space it is not allowed to be pushed into; the body's own walking,
            // swimming and climbing may, as they may with no creation about (Codex Sol's review of the open sea: a worn
            // glider's guard took back every whole move past the room's old bounds, a wall again). The tick is moved again
            // from where it began with the body's own motion alone, and the creation's motion stops. (Taking back only the
            // creation's share after the move missed a push that ended against a wall: the slide had already cut it away.)
            GlobalPosition = before;
            Velocity = _ownVelocity;
            _vitalsLandingSpeedMps = Mathf.Max(0, -Velocity.Y);
            if (_ownVelocity.LengthSquared() > 0) MoveAndSlide();
            Velocity = _ownVelocity;
            _creationApplied = Vector3.Zero;
            _creationVelocity = Vector3.Zero;
            CreationGuardStops++;
        }
        var actual = GlobalPosition - before;
        if (!_climbing) actual.Y = 0;
        IsBlocked = control.LengthSquared() > 0.1f && actual.LengthSquared() < 0.0000001f;
        // The distance a gait phases by: what the body really moved this tick. A wrap or a jump home is not a step.
        var moved = actual.Length();
        if (moved > MaxTickTravelM || !float.IsFinite(moved)) moved = 0;
        _distanceM += moved;
        if ((IsOnFloor() || (Floats && HoverSupportY != null)) && Mathf.Abs(Velocity.Y) < 0.1f && (!HasCreationGuard || GuardAllows(GlobalPosition)))
        {
            LastSafePosition = GlobalPosition;
            HasSafePosition = true;
            _waterLeap = false;
        }
        if (!GlobalPosition.IsFinite() || GlobalPosition.Y < _spawnPoint.Y - 12.0f ||
            (PlayableBounds is { } bounds && GlobalPosition.Y < bounds.Position.Y - RecoverBelowBoundsM))
        {
            // The out-of-room safety net is still a fall in consequence modes; report it before teleport resets the observation.
            if (GlobalPosition.IsFinite() && !Floats)
                FallObserved?.Invoke(_vitalsLandingSpeedMps, false);
            Recover();
        }
        ObserveBodyVitals(dt, TakeExertion(dt, moved, onFoot, swam && _stroked, sprint, control));
        // Out of stamina in deep water (the ledger's gate): the scene fades and the body is back on the shore where it went in.
        if (_swimming && _gate.Empty) BeginShoreReturn();
        TakeBodyState(dt, moved, onFloor, wasSwimming, sprint);
        UpdateVisual(dt);
        _bodyVisual?.BodyTicked(LastBodyState, dt);
    }

    /// <summary>More than this in one tick is a jump (a wrap, a trip home, a recovery), not travel.</summary>
    private const float MaxTickTravelM = 0.25f;

    /// <summary>The tick's BodyState, for the drawn body (IBodyVisual) and anything else that reads it.</summary>
    private void TakeBodyState(float dt, float moved, bool wasOnFloor, bool wasSwimming, bool sprinting)
    {
        var grounded = IsOnFloor();
        var caught = _swimming && !wasSwimming && !wasOnFloor && !_climbing;
        var landed = !Floats && !_climbing && ((grounded && !wasOnFloor) || caught);
        _airTimeS = grounded || _climbing || _swimming || Floats ? 0 : _airTimeS + dt;
        _bodyTick++;
        LastBodyState = new BodyState
        {
            Tick = _bodyTick,
            Position = GlobalPosition,
            Yaw = GlobalRotation.Y,
            Velocity = Velocity,
            SpeedMps = dt > 0 ? moved / dt : 0,
            DistanceM = _distanceM,
            Grounded = grounded,
            FloorNormal = grounded ? GetFloorNormal() : Vector3.Up,
            AirTimeS = _airTimeS,
            Landed = landed,
            LandingSpeedMps = !landed ? 0 : caught ? Mathf.Max(_vitalsLandingSpeedMps, _airDescentMps) : _vitalsLandingSpeedMps,
            LandedInDeepWater = landed && caught,
            Climbing = _climbing,
            ClimbNormal = _climbNormal,
            Swimming = _swimming,
            SwimTilt = _tilt,
            Diving = IsDiving,
            Floating = Floats,
            InBubble = Ride == BubbleRide.Enveloped,
            Carrying = HandsFull?.Invoke() ?? false,
            Sprinting = sprinting,
            WalkSpeedMps = WalkSpeedNowMps,
            RunSpeedMps = RunSpeedNowMps,
            Blocked = IsBlocked,
            WindMps = WindMps,
            ControlHeld = _controlHoldS > 0,
            HeldInput = HeldInput,
            WaterSurfaceY = Water.Wet ? Water.SurfaceY : float.NaN,
            WaterDepthM = Water.Wet ? Water.DepthM : 0,
            BodyHeightM = BodyHeightM,
            GravityMps2 = GravityMps2,
        };
    }

    /// <summary>
    /// The trusted body's report to the kernel, once a physics tick: what it exerted (the ledger's Report, before VitalsTick, which
    /// charges it and refills the bar on standable ground), a landing, then the tick. The gate the ledger then holds is what the
    /// body obeys from the next tick on.
    /// </summary>
    private void ObserveBodyVitals(float dt, Seams.BodyExertion exertion)
    {
        if (Floats || (VitalsTick == null && FallObserved == null && _staminaLedger == null)) return;
        var safelyGrounded = IsOnFloor() && !_climbing && !_swimming && !GoingHome;
        var caughtByWater = Water.Wet && Water.DepthM >= SwimDepthM && Water.Under(GlobalPosition) >= 0;
        if (_staminaLedger != null)
        {
            _staminaLedger.Report(exertion);
            ExertionReports++;
        }
        if (safelyGrounded || caughtByWater)
            FallObserved?.Invoke(_vitalsLandingSpeedMps, caughtByWater);
        VitalsTick?.Invoke(dt, safelyGrounded);
        TakeGate();
    }

    /// <summary>On foot or in the air: the walk, the run, steps, jumps and creation forces, slowed by the water it wades in.</summary>
    private void Walk(float dt, Vector3 wish, bool sprint, bool onFloor)
    {
        var start = GlobalPosition;
        // On land the pace is the base times the team's Quick feet and the empty bar's slow (WalkSpeedNowMps, RunSpeedNowMps).
        var desired = wish * (sprint ? RunSpeedNowMps : WalkSpeedNowMps) * WadeFactor();
        if (!onFloor && WindMps != Vector2.Zero)
            desired = (desired + new Vector3(WindMps.X, 0, WindMps.Y)).LimitLength(RunSpeedNowMps + WindMps.Length());
        var acceleration = onFloor ? GroundAccelerationMps2 : AirAccelerationMps2 * AirControl;
        var horizontal = WithinBounds(new Vector3(Velocity.X, 0, Velocity.Z).MoveToward(desired, acceleration * dt), dt);
        var vertical = onFloor ? Mathf.Min(0, Velocity.Y) : Mathf.Max(Velocity.Y - GravityMps2 * dt, -EffectiveTerminalFallMps);
        var jumped = false;
        if (_jumpBuffer > 0 && InputEnabled && _coyote > 0)
        {
            vertical = JumpSpeedMps;
            _jumpBuffer = 0;
            _coyote = 0;
            jumped = true;
            JumpsStarted++;
        }
        else _jumpBuffer = Mathf.Max(0, _jumpBuffer - dt);
        // Friendly lift is an explicit game capability: upward acceleration includes weight support.
        if (_creationAcceleration.Y > 0) vertical = Mathf.Max(vertical, 0);
        if (_creationGlideLimit > 0) vertical = Mathf.Max(vertical, -_creationGlideLimit);
        var total = WithCreation(new Vector3(horizontal.X, vertical, horizontal.Z), dt);
        // Falling into deep water: the water catches the body this tick, before the eye goes under. The descent it had in the air
        // is the landing's speed (BodyState.LandingSpeedMps) on the tick the body starts to swim, not what is left after the catch.
        _airDescentMps = onFloor ? 0 : Mathf.Max(0, -total.Y);
        total.Y = CatchInWater(total.Y, dt);
        Velocity = total;
        if (onFloor && !jumped && vertical <= 0 && _creationApplied == Vector3.Zero &&
            horizontal.LengthSquared() > 0.000001f && TryStep(horizontal * dt))
        {
            // The step already includes the horizontal movement for this tick.
            Velocity = Vector3.Down * 0.05f;
            MoveAndSlide();
            Velocity = new Vector3(horizontal.X, Velocity.Y, horizontal.Z);
        }
        else
        {
            // Capture the final descent after creation forces and the water catch, before the floor stops it.
            _vitalsLandingSpeedMps = Mathf.Max(0, -Velocity.Y);
            MoveAndSlide();
        }
        // A face too steep to stand on, pushed into: grab it (at once in the air, after a deliberate push on the ground).
        var grounded = IsOnFloor();
        TryGrab(dt, wish, deliberate: grounded, heldBack: !grounded || HeldBack(start, wish, WalkSpeedNowMps, dt));
    }

    /// <summary>
    /// Home (B): the fade out, home, the fade back in. Refused while already on the way, or while the body takes no input.
    /// Mid-climb the hands keep hold through the fade and let go at home; mid-leap the body flies on and lands at home; a
    /// carried thing stays in hand and comes home too (the command host places it with its holder every tick).
    /// </summary>
    public bool RequestHome()
    {
        if (!_ready || GoingHome || !InputEnabled) return false;
        BeginHome();
        return true;
    }

    private void BeginHome()
    {
        _homeTime = 0;
        _homeArrived = false;
        _shoreTrip = false;
        _jumpBuffer = 0;
    }

    private void UpdateHomeTrip(float dt)
    {
        if (_homeTime < 0) return;
        _homeTime += dt;
        if (!_homeArrived && _homeTime >= HomeFadeS)
        {
            _homeArrived = true;
            if (_shoreTrip) ArriveAtShore();
            else ArriveHome();
        }
        if (_homeTime >= 2 * HomeFadeS)
        {
            _homeTime = -1;
            _shoreTrip = false;
        }
    }

    /// <summary>
    /// Home: the jetty's landward end, standing and facing inland; a room with a sea but no jetty, the nearest beach; a room
    /// without a sea, the spawn. A spot with no room for the body tries a few round it, then the next home along; with none at
    /// all the body recovers to its last safe footing (it never falls off the world).
    /// </summary>
    private void ArriveHome()
    {
        var here = new Vector2(GlobalPosition.X, GlobalPosition.Z);
        var landed = false;
        LastHome = "";
        if (Sea?.JettyData is { } jetty)
        {
            var inland = Flat(jetty.RootM - jetty.EndM);
            if (inland == Vector3.Zero) inland = new Basis(Vector3.Up, Mathf.DegToRad(jetty.YawDeg)) * Vector3.Back;
            landed = LandAt(jetty.RootM, inland, "jetty");
        }
        if (!landed && LandAtBeachOrStart(here) != "") landed = true;
        if (!landed) Recover();
        Velocity = Vector3.Zero;
        ClearCreationMotion();
        HomeTrips++;
        WentHome?.Invoke(this);
    }

    /// <summary>The nearest beach, else the spawn: "beach", "start", or "" when neither has room (LastHome says which).</summary>
    private string LandAtBeachOrStart(Vector2 here)
    {
        if (Sea?.NearestBeach(here) is { } beach && LandAt(beach.WashAshoreM, new Basis(Vector3.Up, Mathf.DegToRad(beach.YawDeg)) * Vector3.Forward, beach.Id)) return "beach";
        if (!TryTeleportTo(_spawnPoint)) return "";
        LastHome = "spawn";
        return "start";
    }

    /// <summary>How far under a home's spot (the jetty's deck, a beach's sand) its landing may be found.</summary>
    public const float HomeDropM = 0.06f;

    /// <summary>
    /// Stand at a spot, or the nearest of a few beside it (inland first), facing inland. A landing must be dry ground within
    /// HomeDropM under the spot (Codex Astra's review: the seabed under a jetty's root was taken as home, under water), and
    /// not out past half the far net (Codex Sol's review: room data put there would start trip after trip).
    /// </summary>
    private bool LandAt(Vector3 spot, Vector3 inland, string name)
    {
        bool Near(Vector3 at) => Sea == null || new Vector2(at.X, at.Z).DistanceTo(Sea.MiddleM) <= FarNetM * 0.5f;
        if (!spot.IsFinite() || !Near(spot)) return false;
        var side = inland.Cross(Vector3.Up);
        foreach (var offset in new[] { Vector3.Zero, inland * 0.06f, side * 0.06f, -side * 0.06f, inland * 0.12f, side * 0.12f, -side * 0.12f, inland * 0.2f })
            if (Near(spot + offset) && FindSupportedPosition(spot + offset, out var feet) && feet.Y >= spot.Y - HomeDropM && DryAt(feet) && TryTeleportTo(feet))
            {
                Rotation = new Vector3(0, Mathf.Atan2(-inland.X, -inland.Z), 0);
                LastHome = name;
                return true;
            }
        return false;
    }

    /// <summary>
    /// Floating (Floats): the body hovers HoverHeightM over the ground or the water under it, with a gentle bob, and moves at
    /// the walk or the run. Nothing pulls it down: over a ledge or the sea it sinks gently to its height over what is below,
    /// and over nothing within HoverSearchM it settles toward FloatAltitudeFloorY, or holds its height. Something in its way is
    /// ducked under when lowering the body to the ground clears it, and otherwise, while RiseOverObstacles is set, risen over:
    /// the body slides up the face and over the top, never through it. A ceiling holds it down.
    /// </summary>
    private void Hover(float dt, Vector3 wish, bool sprint)
    {
        _coyote = 0;
        _jumpBuffer = 0;
        _hoverClock += dt;
        if (MotionMode != MotionModeEnum.Floating) MotionMode = MotionModeEnum.Floating;
        // Floating mode stops a move that meets a face within this angle of head-on instead of sliding it; a rise pressed
        // against a cliff is nearly head-on (0.3 m/s up against a 1.2 m/s push is 14 degrees), so it must always slide.
        WallMinSlideAngle = 0;
        var here = GlobalPosition;
        var desired = wish * (sprint ? RunSpeedMps : WalkSpeedMps);
        var horizontal = WithinBounds(new Vector3(Velocity.X, 0, Velocity.Z).MoveToward(desired, GroundAccelerationMps2 * dt), dt);
        var heading = desired.LengthSquared() > 1e-6f ? desired.Normalized() : Vector3.Zero;
        var ahead = heading * (BodyRadiusM + 0.01f);
        var support = HoverSupport(here, ahead, out var overWater);
        HoverSupportY = support;
        HoversOverWater = overWater;
        var bob = HoverBobM * Mathf.Sin(_hoverClock * Mathf.Tau / HoverBobPeriodS);
        var floor = FloatAltitudeFloorY is { } wanted && float.IsFinite(wanted) ? wanted : (float?)null;
        // Over nothing within reach: toward the height of where it is going (it used to hold its height there whatever it was).
        var target = support is { } top ? top + HoverHeightM + bob : floor is { } settle ? settle + bob : here.Y;
        if (floor is { } lowest && lowest + bob > target) target = lowest + bob;
        // Under a low ceiling, here or just ahead: hover lower (a passage just taller than the body).
        foreach (var column in heading == Vector3.Zero ? new[] { here } : new[] { here, here + ahead })
            if (Ceiling(column) is { } ceiling) target = Mathf.Min(target, ceiling - BodyHeightM - SafeMarginM * 2);
        if (support is { } ground) target = Mathf.Max(target, ground + SafeMarginM * 2);
        RisingOver = false;
        if (heading != Vector3.Zero && TestMove(GlobalTransform, heading * HoverProbeM, null, SafeMargin))
        {
            // In the way. Lowered to the ground, does it clear? Then duck under; else rise over, if allowed.
            var lowered = GlobalTransform;
            lowered.Origin.Y = (support ?? here.Y) + SafeMarginM * 2;
            if (lowered.Origin.Y < here.Y - 0.001f && !TestMove(GlobalTransform, lowered.Origin - here, null, SafeMargin) && !TestMove(lowered, heading * HoverProbeM, null, SafeMargin))
            {
                target = Mathf.Min(target, lowered.Origin.Y);
                DuckTicks++;
            }
            // (A roof over it stops the rise in the move itself; a test move up would report the face it is pressed against.)
            else if (RiseOverObstacles)
            {
                RisingOver = true;
                RiseTicks++;
            }
        }
        var rise = FarGapMps(target - here.Y, FloatRiseMps);
        var vertical = RisingOver ? rise : Mathf.Clamp((target - here.Y) * HoverSpringPerS, -FarGapMps(here.Y - target, FloatSinkMps), rise);
        Velocity = WithCreation(new Vector3(horizontal.X, vertical, horizontal.Z), dt);
        MoveAndSlide();
    }

    /// <summary>
    /// How deep under a floating body its ground or water is looked for: HoverSupportSearchM, or within playable bounds down to
    /// their floor, whichever is deeper.
    /// </summary>
    public float HoverSearchM => PlayableBounds is { } bounds && float.IsFinite(GlobalPosition.Y)
        ? Mathf.Max(HoverSupportSearchM, GlobalPosition.Y - bounds.Position.Y + 0.05f) : HoverSupportSearchM;

    /// <summary>
    /// The rise or the sink toward the height a floating body settles to: FloatRiseMps or FloatSinkMps, and further than
    /// HoverSupportSearchM from it (the magnified island's mesas; never on today's islands, whose land stands under 3 m) that
    /// many times faster for each HoverSupportSearchM of the gap, up to the run: 25 m off a mesa took 100 s, and takes about 40.
    /// </summary>
    private float FarGapMps(float gapM, float baseMps) =>
        gapM <= HoverSupportSearchM ? baseMps : Mathf.Min(baseMps * gapM / HoverSupportSearchM, Mathf.Max(baseMps, RunSpeedMps));

    /// <summary>How far ahead a floating body feels for something in its way.</summary>
    private float HoverProbeM => BodyRadiusM * 0.5f + 0.005f;

    /// <summary>
    /// The highest ground or water surface under the body or just ahead of it, looked for from the body's middle (so a table
    /// top overhead is a ceiling, not a floor) down HoverSearchM. Null over nothing.
    /// </summary>
    private float? HoverSupport(Vector3 feet, Vector3 ahead, out bool overWater)
    {
        overWater = false;
        float? best = null;
        var space = GetWorld3D().DirectSpaceState;
        var depth = HoverSearchM;
        foreach (var column in ahead == Vector3.Zero ? new[] { feet } : new[] { feet, feet + ahead })
        {
            var from = column + Vector3.Up * (BodyHeightM * 0.5f);
            // Disposed at once: these run every tick, and wrappers left to the finalizer pile up (and trip Godot's exit).
            using var query = PhysicsRayQueryParameters3D.Create(from, column + Vector3.Down * depth, RoomBuilder.BodyMask, ExcludeSelf);
            query.HitBackFaces = false;
            using var hit = space.IntersectRay(query);
            if (hit.Count > 0 && (best == null || hit[HitPosition].AsVector3().Y > best)) best = hit[HitPosition].AsVector3().Y;
            // Water is a sheet on its own layer (RoomWater): the highest one under the middle, or over the feet if the body has sunk in.
            var water = RoomWater.At(space, column + Vector3.Up * (BodyHeightM * 0.5f), BodyHeightM, depth, GetRid());
            if (!water.Wet && Sea != null) water = Sea.OpenWater(space, column + Vector3.Up * (BodyHeightM * 0.5f), BodyHeightM, depth, GetRid());
            if (water.Wet && (best == null || water.SurfaceY > best))
            {
                best = water.SurfaceY;
                overWater = true;
            }
        }
        return best;
    }

    /// <summary>The underside of whatever is over the body's head within its hover and bob (null when clear).</summary>
    private float? Ceiling(Vector3 feet)
    {
        var from = feet + Vector3.Up * (BodyHeightM * 0.5f);
        using var query = PhysicsRayQueryParameters3D.Create(from, feet + Vector3.Up * (BodyHeightM + HoverHeightM + HoverBobM + 0.01f), RoomBuilder.BodyMask, ExcludeSelf);
        query.HitBackFaces = false;
        using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? hit[HitPosition].AsVector3().Y : null;
    }

    /// <summary>
    /// The creation stage every state shares: the creation velocity grows by its acceleration (capped), is added to the
    /// body's own motion and is remembered as applied, so it is taken off again next tick and never snaps back.
    /// </summary>
    private Vector3 WithCreation(Vector3 own, float dt)
    {
        _creationVelocity = (_creationVelocity + _creationAcceleration * dt).LimitLength(MaxCreationSpeedMps);
        var total = own + _creationVelocity;
        if (_creationGlideLimit > 0) total.Y = Mathf.Max(total.Y, -_creationGlideLimit);
        _creationApplied = total - own;
        _ownVelocity = own;
        _creationPushed = _creationApplied;
        return total;
    }

    // ---- water ----

    private float EyeHeightM => (float)Profile.EyeHeightMeters;

    /// <summary>The water at the feet, looking down as far as this tick's fall will carry them, so a fast fall meets the surface it crosses.</summary>
    private void SampleWater(float dt)
    {
        if (!CanSwim || !IsInsideTree())
        {
            Water = RoomWater.Column.Dry;
            return;
        }
        var space = GetWorld3D().DirectSpaceState;
        var below = Mathf.Max(0.02f, -Velocity.Y * dt + 0.02f);
        // A diver looks up as far as the surface it went down from, and under a roof (a shelf it dives beneath) the water is
        // still its water, with the bed under it (Codex's reviews: the shelf's top was taken for the bed, above the diver).
        var above = BodyHeightM * 4;
        if (_swimming && float.IsFinite(_swimSurfaceY)) above = Mathf.Max(above, _swimSurfaceY - GlobalPosition.Y + 0.05f);
        Water = RoomWater.At(space, GlobalPosition, above, below, GetRid(), throughRoof: _swimming);
        // Where the water meshes stop, out past the island, the sea itself answers.
        _openSeaWater = false;
        if (!Water.Wet && Sea != null)
        {
            Water = Sea.OpenWater(space, GlobalPosition, above, below, GetRid(), throughRoof: _swimming);
            _openSeaWater = Water.Wet;
        }
        // Where a sea mesh runs on past the generated floor (no bed within RoomWater.BedSearchM), the open sea's bed.
        else if (Water.Wet && Sea != null && Water.BedY <= Water.SurfaceY - RoomWater.BedSearchM + 1e-4f)
        {
            Water = Water with { BedY = Mathf.Max(Water.BedY, Sea.OpenSeaBedAt(new Vector2(GlobalPosition.X, GlobalPosition.Z))) };
            _openSeaWater = true;
        }
        _swimSurfaceY = Water.Wet ? Water.SurfaceY : _swimming ? _swimSurfaceY : float.NaN;
    }

    /// <summary>
    /// Deep water never lets the feet sink so far this tick that the eye goes under: a falling or plunging body is caught
    /// with its eye EyeClearanceM above the surface (and one already that deep rises no faster than the swim spring).
    /// </summary>
    private float CatchInWater(float vertical, float dt)
    {
        if (!CanSwim || !Water.Wet || Water.DepthM < SwimDepthM || dt <= 0) return vertical;
        var deepest = Water.SurfaceY - (EyeHeightM - EyeClearanceM);
        if (GlobalPosition.Y + vertical * dt >= deepest) return vertical;
        return Mathf.Max(vertical, Mathf.Min((deepest - GlobalPosition.Y) / dt, SwimSpringMaxMps));
    }

    /// <summary>Wading slows the body gradually as the water rises up it, to WadeSlowestFactor where it begins to float.</summary>
    private float WadeFactor()
    {
        if (!Water.Wet) return 1.0f;
        var under = Water.Under(GlobalPosition);
        if (under <= 0) return 1.0f;
        return Mathf.Lerp(1.0f, WadeSlowestFactor, Mathf.Clamp(under / SwimDepthM, 0, 1));
    }

    /// <summary>
    /// Water over the head floats the body: it swims while the water is deep where it is, finds its feet where the bed
    /// shelves up, and is carried clear of the water by a leap until it falls back in.
    /// </summary>
    private bool UpdateSwimming()
    {
        var under = Water.Under(GlobalPosition);
        if (!CanSwim || !Water.Wet)
        {
            if (_swimming) EndSwim();
            return false;
        }
        if (_swimming)
        {
            if (under < -0.01f || Water.DepthM < SwimExitDepthM) EndSwim();
        }
        else if (!(_waterLeap && Velocity.Y > 0) && under > 0.002f && Water.DepthM >= SwimDepthM) StartSwim();
        return _swimming;
    }

    private void StartSwim()
    {
        _swimming = true;
        _waterLeap = false;
        _coyote = 0;
        _grabPush = 0;
        MotionMode = MotionModeEnum.Floating;
        SwimStarts++;
    }

    private void EndSwim()
    {
        _swimming = false;
        MotionMode = MotionModeEnum.Grounded;
    }

    /// <summary>
    /// At the surface: calmer and slower than walking, with a gentle bob. Water breaks a fall within centimetres and the
    /// body rises back to the surface. A jump at the surface leaps out with the land jump's take-off. A steep bank pushed
    /// into is grabbed and climbed out of; a shelving shore is walked out of (UpdateSwimming).
    /// </summary>
    private void Swim(float dt, Vector3 wish, bool sprint, Vector2 control, bool diveDown, bool diveUp)
    {
        // An empty bar (the ledger's gate): a diver is not held down and a swimmer does not swim or leap out. The tick the bar ran
        // empty the fade to the shore began (BeginShoreReturn), so the controls are already held; nothing refills in the water.
        var spent = _gate.Empty;
        // Only a dive takes the body under: a fall or a plunge is still caught at the surface (CatchInWater).
        if ((diveDown && !spent) || (_wasDiving && GlobalPosition.Y < Water.SurfaceY - SwimFloatDepthM - DiveStartM))
        {
            if (spent) Dive(dt, Vector2.Zero, sprint, false, diveUp);
            else Dive(dt, control, sprint, diveDown, diveUp);
            return;
        }
        _coyote = 0;
        _swimClock += dt;
        var feet = GlobalPosition;
        var target = Water.SurfaceY - SwimFloatDepthM + SwimBobM * Mathf.Sin(_swimClock * Mathf.Tau / SwimBobPeriodS);
        var spring = Mathf.Clamp((target - feet.Y) * SwimSpringPerS, -SwimSpringMaxMps, SwimSpringMaxMps);
        var excess = Velocity.Y - spring;
        excess = excess / (1 + WaterDragQuadraticPerM * Mathf.Abs(excess) * dt) * Mathf.Exp(-WaterDragLinearPerS * dt);
        var vertical = spring + excess;
        if (!spent && _jumpBuffer > 0 && InputEnabled && Mathf.Abs(feet.Y - target) < 0.015f)
        {
            vertical = JumpSpeedMps;
            _jumpBuffer = 0;
            _waterLeap = true;
            JumpsStarted++;
            EndSwim();
            _stroked = true;
        }
        else _jumpBuffer = Mathf.Max(0, _jumpBuffer - dt);
        // A stroke (the ledger's deep-water seconds, decision 64): a movement key moving the swimmer. Floating still costs nothing.
        if (!spent && control.LengthSquared() > StrokeControlSquared) _stroked = true;
        var desired = spent ? Vector3.Zero : wish * SwimPaceMps(sprint);
        var horizontal = WithinBounds(new Vector3(Velocity.X, 0, Velocity.Z).MoveToward(desired, SwimAccelerationMps2 * dt), dt);
        var total = WithCreation(new Vector3(horizontal.X, vertical, horizontal.Z), dt);
        if (_swimming) total.Y = CatchInWater(total.Y, dt);
        Velocity = total;
        MoveAndSlide();
        if (_swimming) TryGrab(dt, wish, deliberate: true, heldBack: HeldBack(feet, wish, SwimPaceMps(false), dt));
    }

    /// <summary>
    /// Under water: W swims the way you look (the view's pitch, the eye's in F1 and the shoulder camera's, which follows it,
    /// in F2 near the shoulder; in F2 zoomed out to orbit, and in the shelved F3 and F4, across the view's heading, level), A and
    /// D across; Ctrl down, Space up; no key, a slow drift up. The water's
    /// drag eases every change. The bed holds the body: the room's ground by collision, the open-sea bed by this clamp.
    /// </summary>
    private void Dive(float dt, Vector2 control, bool sprint, bool down, bool up)
    {
        _coyote = 0;
        _jumpBuffer = 0;
        if (!_wasDiving) DiveStarts++;
        IsDiving = true;
        DiveTicks++;
        // Under water a stroke is a key's own motion: Ctrl down, Space up, or a movement key. A diver let go drifts up for nothing.
        if (down || up || control.LengthSquared() > StrokeControlSquared) _stroked = true;
        Vector3 wish;
        if (MovementFrameYaw is { } frameYaw) wish = new Basis(Vector3.Up, frameYaw) * new Vector3(control.X, 0, -control.Y);
        else wish = ViewForward() * control.Y + GlobalBasis.X * control.X;
        var desired = wish.LimitLength() * SwimPaceMps(sprint) * DiveSpeedFactor;
        if (down) desired.Y -= DiveSinkMps;
        if (up) desired.Y += DiveRiseMps;
        if (!down && !up && control.LengthSquared() < 0.01f) desired.Y = DriftUpMps;
        var velocity = Velocity.MoveToward(desired, SwimAccelerationMps2 * dt);
        Velocity = WithCreation(velocity, dt);
        MoveAndSlide();
        // Nothing goes through the bed (the open-sea bed has no collider). Out in the open sea, the bed is the one where the
        // body is now: swimming off a deep ledge, the open-sea bed beyond stands higher (Codex Sol's review).
        var bedY = Water.BedY;
        if (_openSeaWater && Sea != null && Sea.OpenWater(GetWorld3D().DirectSpaceState, GlobalPosition, Water.SurfaceY - GlobalPosition.Y + 0.05f, 0.05f, GetRid(), throughRoof: true) is { Wet: true } there)
            bedY = there.BedY;
        var bed = bedY + SafeMarginM * 2;
        if (Water.Wet && GlobalPosition.Y < bed)
        {
            GlobalPosition = new Vector3(GlobalPosition.X, bed, GlobalPosition.Z);
            if (Velocity.Y < 0) Velocity = new Vector3(Velocity.X, 0, Velocity.Z);
        }
    }

    /// <summary>
    /// The way W swims under water: the active view's forward when it is this body's own camera (the eye in F1, the shoulder
    /// camera in F2, which looks a little lower than the eye: Codex's review found F2's W swimming upward), else the eye's.
    /// </summary>
    private Vector3 ViewForward()
    {
        var camera = IsInsideTree() ? GetViewport().GetCamera3D() : null;
        if (camera != null && IsAncestorOf(camera)) return -camera.GlobalBasis.Z.Normalized();
        return GlobalBasis * (new Basis(Vector3.Right, EyeCamera.Rotation.X) * Vector3.Forward);
    }

    /// <summary>Where the body started (its spawn): the companion's last place to come home to.</summary>
    protected Vector3 SpawnPoint => _spawnPoint;

    // ---- climbing ----

    private bool Standable(Vector3 normal) => normal.Y >= Mathf.Cos(FloorMaxAngle);

    /// <summary>Too steep to stand on, but not a roof.</summary>
    private bool Climbable(Vector3 normal) =>
        normal.Y < Mathf.Cos(FloorMaxAngle) - 0.005f && normal.Y >= ClimbOverhangNormalY && Flat(normal).LengthSquared() > 0.01f;

    private static Vector3 Flat(Vector3 value)
    {
        var flat = new Vector3(value.X, 0, value.Z);
        return flat.LengthSquared() > 1e-8f ? flat.Normalized() : Vector3.Zero;
    }

    private bool HandsAreFull() => HandsFull?.Invoke() ?? false;

    /// <summary>Whether this tick's push made under a quarter of its way: the body is held back by what it pushes into.</summary>
    private bool HeldBack(Vector3 start, Vector3 wish, float speed, float dt)
    {
        var moved = new Vector3(GlobalPosition.X - start.X, 0, GlobalPosition.Z - start.Z);
        return moved.Dot(wish.Normalized()) < wish.Length() * speed * dt * 0.25f;
    }

    /// <summary>
    /// Grab a face the body pushes into: too steep to stand on, the push within 45 degrees of straight in, rising above a
    /// step (a step is stepped, never grabbed), and on the ground held for ClimbGrabDelayS. A body held back by what it
    /// pushes into (or in the air) also takes a face it feels a step up and just ahead, with the push within 60 degrees;
    /// and held back on a slope steeper than ScrambleSlopeDeg, where the walk cannot go on up (a slope steepening into a
    /// cliff, a crease), it scrambles: the slope itself is grabbed and climbed.
    /// </summary>
    private void TryGrab(float dt, Vector3 wish, bool deliberate, bool heldBack)
    {
        if (!CanClimb || _regrabBlock > 0 || !InputEnabled || wish.LengthSquared() < 0.25f || HandsAreFull() || _gate.Empty || (ClimbBudget != null && !ClimbBudget(0, 0)))
        {
            _grabPush = 0;
            return;
        }
        var into = wish.Normalized();
        var cos = heldBack ? ClimbHeldGrabCos : ClimbGrabCos;
        Vector3? face = null;
        Vector3? ahead = null;
        var felt = false;
        Vector3? FeelAhead()
        {
            if (felt) return ahead;
            felt = true;
            if (TestMove(GlobalTransform, Vector3.Up * StepHeightM, null, SafeMargin)) return ahead = null;
            var raised = GlobalTransform;
            raised.Origin += Vector3.Up * StepHeightM;
            if (!TestMove(raised, into * 0.02f, _climbContact, SafeMargin)) return ahead = null;
            var normal = _climbContact.GetNormal();
            return ahead = Climbable(normal) && into.Dot(-Flat(normal)) >= cos ? normal : null;
        }
        for (var index = 0; index < GetSlideCollisionCount() && face == null; index++)
        {
            using var collision = GetSlideCollision(index);
            var normal = collision.GetNormal();
            if (Climbable(normal) && into.Dot(-Flat(normal)) >= cos && (RisesAboveStep(normal, collision.GetPosition()) || (heldBack && FeelAhead() != null))) face = normal;
        }
        if (face == null && heldBack && IsOnFloor())
        {
            face = FeelAhead();
            var floor = GetFloorNormal();
            if (face == null && floor.Y < Mathf.Cos(Mathf.DegToRad(ScrambleSlopeDeg)) && into.Dot(-Flat(floor)) >= cos) face = floor;
        }
        if (face is not { } found)
        {
            _grabPush = 0;
            return;
        }
        _grabPush += dt;
        if (deliberate && _grabPush < ClimbGrabDelayS) return;
        _climbing = true;
        _vitalsClimbStart = GlobalPosition;
        _climbNormal = found.Normalized();
        _grabPush = 0;
        _pullPhase = 0;
        _faceLost = 0;
        _swimming = false;
        _coyote = 0;
        _jumpBuffer = 0;
        MotionMode = MotionModeEnum.Floating;
        Velocity = Vector3.Zero;
        Grabs++;
    }

    /// <summary>
    /// A face is a climb, not a step, when the body meets it above step height (a face that starts at head height, a
    /// crown hanging over a perch, a ledge's lip just too tall to step) or it still stands in front of the body just
    /// above step height (a rising cliff, a trunk).
    /// </summary>
    private bool RisesAboveStep(Vector3 normal, Vector3 contact)
    {
        if (contact.Y > GlobalPosition.Y + StepHeightM + SafeMarginM) return true;
        var from = GlobalPosition + Vector3.Up * (StepHeightM + 0.004f);
        using var query = PhysicsRayQueryParameters3D.Create(from, from - Flat(normal) * (BodyRadiusM + 0.02f), CollisionMask, ExcludeSelf);
        query.HitBackFaces = false;
        using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 && !Standable(hit[HitNormal].AsVector3()) && hit[HitNormal].AsVector3().Y >= ClimbOverhangNormalY;
    }

    /// <summary>
    /// On the face: forward climbs up and back climbs down, whatever way the body or the camera is turned; left and right
    /// move along the face as the view sees them. The body is pressed gently to the face. A ledge within reach above is
    /// pulled over onto; ground met while climbing down is stepped off onto; jump lets go.
    /// (Founder's playtest, 9 October: "I can climb around halfway up and then something happens to the controls and then I
    /// seem to be forced to climb down." Up had been the push toward the face, through the body's heading. On a round trunk
    /// any angle between the heading and the face sent the climber round it, which turned the face further from the
    /// heading, until forward pointed away from the face and climbed down. The thin climbing pole above the trunk turned it fastest.)
    /// </summary>
    private void Climb(float dt, Vector2 control)
    {
        _coyote = 0;
        if (!CanClimb || HandsAreFull())
        {
            LetGo(kick: false);
            return;
        }
        if (_jumpBuffer > 0 && InputEnabled)
        {
            _jumpBuffer = 0;
            LetGo(kick: true);
            return;
        }
        // The grip fails when the bar is empty (the ledger's gate) or the climb cannot be paid for: the body falls from where it is.
        var budgetRise = (_pullPhase > 0 ? PullOverSpeedMps : Mathf.Max(0, control.Y) * ClimbSpeedMps) * dt;
        if (_gate.Empty || (ClimbBudget != null && !ClimbBudget(dt, budgetRise)))
        {
            GripFailures++;
            LetGo(kick: false);
            return;
        }
        if (_pullPhase > 0)
        {
            PullOver(dt);
            return;
        }
        var normal = _climbNormal;
        var outward = Flat(normal);
        var right = (-outward).Cross(Vector3.Up);
        var up = control.Y;
        var viewRight = MovementFrameYaw is { } frameYaw ? new Basis(Vector3.Up, frameYaw) * Vector3.Right : GlobalBasis.X;
        var side = control.X * (new Vector3(viewRight.X, 0, viewRight.Z).Dot(right) >= 0 ? 1.0f : -1.0f);
        // Down a bank into deep water: the water takes the body before the eye goes under. Climbing out upward holds on.
        if (CanSwim && Water.Wet && Water.DepthM >= SwimDepthM)
        {
            var under = Water.Under(GlobalPosition);
            if ((up <= 0.3f && under >= SwimFloatDepthM) || under >= EyeHeightM - 0.01f)
            {
                StopClimbing(Vector3.Zero);
                StartSwim();
                return;
            }
        }
        // Creation forces: one pulling the climber off the face lets go, and the body carries it; the rest move it along the face.
        _creationVelocity = (_creationVelocity + _creationAcceleration * dt).LimitLength(MaxCreationSpeedMps);
        if (_creationVelocity.Dot(normal) > ClimbCreationLetGoMps)
        {
            LetGo(kick: false);
            return;
        }
        var along = _creationVelocity - normal * _creationVelocity.Dot(normal);
        // The room's bounds hold a climber inside, at the top as at the sides.
        if (PlayableBounds is { } bounds && up > 0 && GlobalPosition.Y + BodyHeightM >= bounds.End.Y) up = 0;
        if (up > 0.3f && FindLedge(outward, out var ledge))
        {
            BeginPullOver(ledge);
            PullOver(dt);
            return;
        }
        var alongUp = Vector3.Up - normal * Vector3.Up.Dot(normal);
        alongUp = alongUp.LengthSquared() > 1e-4f ? alongUp.Normalized() : Vector3.Up;
        var alongSide = right - normal * right.Dot(normal);
        alongSide = alongSide.LengthSquared() > 1e-4f ? alongSide.Normalized() : right;
        var velocity = (alongUp * up + alongSide * side).LimitLength(1) * ClimbSpeedMps - normal * ClimbStickMps + along;
        _creationApplied = along;
        _ownVelocity = velocity - along;
        _creationPushed = along;
        var horizontal = WithinBounds(new Vector3(velocity.X, 0, velocity.Z), dt);
        Velocity = new Vector3(horizontal.X, velocity.Y, horizontal.Z);
        MoveAndSlide();
        if (up < -0.1f)
            for (var index = 0; index < GetSlideCollisionCount(); index++)
                if (Standable(SlideNormal(index)))
                {
                    // Down at the foot of the face: step off into walking.
                    StopClimbing(Vector3.Zero);
                    return;
                }
        // Keep hold: find the face under the hands again, following its curve (a trunk, a bulge in the rock).
        if (TestMove(GlobalTransform, -normal * ClimbProbeM, _climbContact, SafeMargin))
        {
            _faceLost = 0;
            var found = _climbContact.GetNormal();
            if (Climbable(found)) _climbNormal = normal.Lerp(found, 0.35f).Normalized();
            // Walkable ground or a roof under the hands (a ledge's lip, a trunk's shoulder): keep the face as it was.
            return;
        }
        // The face fell away: over the top or round a corner. Pull over onto what is there; else, after a moment, let go.
        if (FindLedge(outward, out ledge)) BeginPullOver(ledge);
        else if (++_faceLost > 3) LetGo(kick: false);
    }

    /// <summary>
    /// Standable ground beyond the face, no higher than PullOverReachM above the feet and above them, where the whole body
    /// fits, with a clear way straight up beside the face and then over onto it.
    /// </summary>
    private bool FindLedge(Vector3 outward, out Vector3 ledge)
    {
        ledge = Vector3.Zero;
        var space = GetWorld3D().DirectSpaceState;
        foreach (var ahead in new[] { BodyRadiusM * 1.5f, BodyRadiusM * 2.5f })
        {
            var column = GlobalPosition - outward * ahead;
            using var query = PhysicsRayQueryParameters3D.Create(column + Vector3.Up * (PullOverReachM + 0.005f), column + Vector3.Up * 0.002f, CollisionMask, ExcludeSelf);
            query.HitBackFaces = false;
            using var hit = space.IntersectRay(query);
            if (hit.Count == 0) continue;
            var normal = hit[HitNormal].AsVector3();
            if (!Standable(normal)) continue;
            var spot = hit[HitPosition].AsVector3() + Vector3.Up * (BodyRadiusM * (1.0f / normal.Y - 1.0f) + SafeMarginM * 2 + 0.001f);
            if (!InsidePlayableBounds(spot) || !CapsuleFits(spot)) continue;
            // The whole body must stand under the bounds' top there (a pull-over never lifts the head out of the room).
            if (PlayableBounds is { } bounds && bounds.Size.Y > BodyHeightM && spot.Y + BodyHeightM > bounds.End.Y) continue;
            var rise = Mathf.Max(0, spot.Y + 0.002f - GlobalPosition.Y);
            if (rise > 0 && TestMove(GlobalTransform, Vector3.Up * rise, null, SafeMargin)) continue;
            var raised = GlobalTransform;
            raised.Origin += Vector3.Up * rise;
            if (TestMove(raised, new Vector3(spot.X - GlobalPosition.X, 0, spot.Z - GlobalPosition.Z), null, SafeMargin)) continue;
            ledge = spot;
            return true;
        }
        return false;
    }

    private void BeginPullOver(Vector3 ledge)
    {
        _pullTarget = ledge;
        _pullPhase = 1;
        _pullTime = 0;
    }

    /// <summary>The reward at the top: up beside the face, then over onto the ledge, and standing.</summary>
    private void PullOver(float dt)
    {
        _pullTime += dt;
        // The pull-over is the hands' own short move: creation forces wait (their velocity keeps building, so nothing snaps).
        _creationVelocity = (_creationVelocity + _creationAcceleration * dt).LimitLength(MaxCreationSpeedMps);
        var here = GlobalPosition;
        var velocity = Vector3.Zero;
        if (_pullPhase == 1)
        {
            var rise = _pullTarget.Y + 0.002f - here.Y;
            if (rise <= 0.0005f) _pullPhase = 2;
            else velocity = Vector3.Up * Mathf.Min(PullOverSpeedMps, rise / dt);
        }
        if (_pullPhase == 2)
        {
            var over = new Vector3(_pullTarget.X - here.X, 0, _pullTarget.Z - here.Z);
            if (over.Length() <= 0.001f)
            {
                PullOvers++;
                CompleteObservedClimb();
                StopClimbing(Vector3.Zero);
                return;
            }
            velocity = over.Normalized() * Mathf.Min(PullOverSpeedMps, over.Length() / dt);
        }
        Velocity = velocity;
        MoveAndSlide();
        if (_pullTime > PullOverMaxS)
        {
            // Something moved into the way. A pull-over never ends in a fall: on support, stand there; else hold the face again.
            if (HasSupportNear(GlobalPosition, 0.01f))
            {
                PullOvers++;
                CompleteObservedClimb();
                StopClimbing(Vector3.Zero);
            }
            else _pullPhase = 0;
        }
    }

    private void CompleteObservedClimb()
    {
        if (_vitalsClimbStart is { } start) ClimbCompleted?.Invoke(start, GlobalPosition);
        _vitalsClimbStart = null;
    }

    /// <summary>Jump lets go with a small kick away and up; a lost face just drops the body. Either way the same push does not grab again at once.</summary>
    private void LetGo(bool kick)
    {
        var outward = Flat(_climbNormal);
        StopClimbing(kick ? outward * ClimbKickAwayMps + Vector3.Up * ClimbKickUpMps : outward * 0.02f);
        _regrabBlock = RegrabDelayS;
        ClimbReleases++;
    }

    private void StopClimbing(Vector3 velocity)
    {
        _climbing = false;
        _vitalsClimbStart = null;
        _pullPhase = 0;
        _faceLost = 0;
        MotionMode = MotionModeEnum.Grounded;
        Velocity = velocity;
    }

    /// <summary>A teleport or a recovery leaves any face or water behind.</summary>
    private void StopClimbingAndSwimming()
    {
        if (_climbing || _swimming) MotionMode = MotionModeEnum.Grounded;
        _climbing = false;
        _vitalsClimbStart = null;
        _swimming = false;
        _pullPhase = 0;
        _faceLost = 0;
        _grabPush = 0;
        _waterLeap = false;
    }

    /// <summary>
    /// The visible body: seated on what it stands on (SeatVisual), turned to face the face it climbs or the way it swims,
    /// and tipped forward to lie along the water while swimming, its middle at the water line. The capsule and the eye never tilt.
    /// </summary>
    private void UpdateVisual(float dt)
    {
        SeatVisual();
        _tilt = Mathf.MoveToward(_tilt, _swimming ? 1.0f : 0.0f, dt / 0.35f);
        float? facing = null;
        if (_climbing) facing = Mathf.Atan2(Flat(_climbNormal).X, Flat(_climbNormal).Z);
        else if (_swimming)
        {
            var travel = new Vector3(Velocity.X, 0, Velocity.Z);
            facing = travel.LengthSquared() > 0.0004f ? Mathf.Atan2(-travel.X, -travel.Z) : GlobalRotation.Y + _visualYaw;
        }
        var offset = facing is { } yaw ? Mathf.AngleDifference(GlobalRotation.Y, yaw) : 0.0f;
        _visualYaw = Mathf.RotateToward(_visualYaw, offset, TurnRateRadPerS * dt);
        if (Mathf.Abs(_visualYaw) < 1e-4f && facing == null) _visualYaw = 0;
        if (_tilt <= 0 && _visualYaw == 0)
        {
            VisualRoot.Transform = new Transform3D(Basis.Identity, Vector3.Down * SeatGapM);
            return;
        }
        var basis = new Basis(Vector3.Up, _visualYaw) * new Basis(Vector3.Right, -Mathf.DegToRad(SwimTiltDeg) * _tilt);
        var pivot = Vector3.Up * (BodyHeightM * 0.5f);
        var lift = Water.Wet ? Mathf.Clamp(Water.Under(GlobalPosition) - BodyHeightM * 0.5f, -BodyHeightM * 0.5f, BodyHeightM * 0.5f) * _tilt : 0.0f;
        VisualRoot.Transform = new Transform3D(basis, pivot - basis * pivot + Vector3.Up * lift + Vector3.Down * SeatGapM);
    }

    /// <summary>
    /// Seat the visual body on what the capsule stands on (see MaxSeatGapM). The physics body is left exactly where
    /// Jolt put it, so steps, snapping and the jitter measurements are unchanged; only the visual closes the gap.
    /// </summary>
    private void SeatVisual()
    {
        var gap = 0.0f;
        var normal = IsOnFloor() ? GetFloorNormal() : Vector3.Zero;
        if (normal.Y >= Mathf.Cos(FloorMaxAngle))
        {
            // The capsule's nearest point to its support plane: the bottom sphere's centre, less a radius along the
            // floor normal. A ray from just above it along the normal measures the gap exactly on flat ground.
            var nearest = GlobalPosition + Vector3.Up * BodyRadiusM - normal * BodyRadiusM;
            using var query = PhysicsRayQueryParameters3D.Create(nearest + normal * 0.001f, nearest - normal * (MaxSeatGapM + 0.0005f), CollisionMask, ExcludeSelf);
            using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (hit.Count > 0)
            {
                var along = (nearest - hit[HitPosition].AsVector3()).Dot(normal);
                // Farther than the largest resting gap is not a resting gap (an edge, a step): leave the visual alone.
                if (along <= MaxSeatGapM) gap = Mathf.Clamp(along / normal.Y, 0.0f, MaxSeatGapM);
            }
        }
        SeatGapM = gap;
    }

    protected bool HasSupportNear(Vector3 position, float maximumDrop = -1)
    {
        if (maximumDrop < 0) maximumDrop = BodyHeightM * 0.5f;
        using var query = PhysicsRayQueryParameters3D.Create(position + Vector3.Up * (BodyHeightM * 0.25f),
            position - Vector3.Up * maximumDrop, RoomBuilder.BodyMask, ExcludeSelf);
        using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 && hit[HitNormal].AsVector3().Y >= Mathf.Cos(FloorMaxAngle);
    }

    private bool FindSupportedPosition(Vector3 requested, out Vector3 result)
    {
        result = requested;
        using var ray = PhysicsRayQueryParameters3D.Create(requested + Vector3.Up * (BodyHeightM * 0.4f),
            requested - Vector3.Up * Mathf.Max(0.5f, BodyHeightM * 2), RoomBuilder.BodyMask, ExcludeSelf);
        using var hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (hit.Count == 0 || hit[HitNormal].AsVector3().Y < Mathf.Cos(FloorMaxAngle)) return false;
        // A vertical capsule touches a slope off its centre ray. Raise its lower
        // hemisphere enough to clear the support plane, then check the entire
        // body against nearby geometry (including roofs and uneven terrain).
        var supportNormal = hit[HitNormal].AsVector3();
        var slopeClearance = BodyRadiusM * (1.0f / supportNormal.Y - 1.0f);
        result = hit[HitPosition].AsVector3() + Vector3.Up * (slopeClearance + SafeMarginM * 2 + 0.001f);
        return CapsuleFits(result);
    }

    /// <summary>Whether the whole body fits with its feet here (nothing on its mask inside the capsule).</summary>
    private bool CapsuleFits(Vector3 feet)
    {
        using var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _capsule, CollisionMask = CollisionMask,
            Transform = new Transform3D(Basis.Identity, feet + Vector3.Up * (BodyHeightM * 0.5f)),
            Margin = SafeMarginM * 0.5f, Exclude = ExcludeSelf
        };
        // Disposed at once: the Gubble's way home tries dozens of spots in one tick, and wrappers left to the finalizer trip Godot's exit.
        var hits = GetWorld3D().DirectSpaceState.IntersectShape(query, 1);
        using var answer = (Godot.Collections.Array)hits;
        return hits.Count == 0;
    }

    private bool TryStep(Vector3 horizontalMotion)
    {
        if (StepHeightM <= 0 || StepHeightM > BodyHeightM * 0.25f ||
            !TestMove(GlobalTransform, horizontalMotion, _stepContact, SafeMargin) ||
            _stepContact.GetNormal().Y >= Mathf.Cos(FloorMaxAngle)) return false;
        var lift = Vector3.Up * StepHeightM;
        if (TestMove(GlobalTransform, lift, null, SafeMargin)) return false;
        var raised = GlobalTransform;
        raised.Origin += lift;
        // A stopped capsule's next acceleration step can be only millimetres.
        // Probe onto the tread beyond the rounded toe, but never teleport that
        // look-ahead distance: actual horizontal travel stays at this tick's input.
        var treadProbe = horizontalMotion + horizontalMotion.Normalized() * BodyRadiusM;
        if (TestMove(raised, treadProbe, null, SafeMargin)) return false;
        raised.Origin += treadProbe;
        if (!TestMove(raised, Vector3.Down * (StepHeightM + FloorSnapLength), _stepContact, SafeMargin) ||
            _stepContact.GetNormal().Y < Mathf.Cos(FloorMaxAngle)) return false;
        var tread = raised.Origin + _stepContact.GetTravel();
        var rise = tread.Y - GlobalPosition.Y;
        if (rise <= SafeMargin * 3 || rise > StepHeightM + SafeMargin) return false;
        GlobalPosition += horizontalMotion + Vector3.Up * rise;
        StepsClimbed++;
        return true;
    }
}
