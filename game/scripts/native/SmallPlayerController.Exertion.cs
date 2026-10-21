using Godot;
using GarageGame.Native.Room;
using GarageGame.Native.Seams;

namespace GarageGame.Native;

/// <summary>
/// The body's side of the mechanics round (docs/runs/RUN-2-MECHANICS.md, packet M5): the knacks that move its own numbers, what it
/// reports to the kernel's stamina ledger each tick (running, deep water, diving) and what it obeys from the ledger's gate (an empty
/// bar: the grip fails, deep water fades back to the shore it went in from, no running until the first bar refills). Everything here
/// is the controller's and none of it touches the collision capsule, the size, the eye height or the reach.
/// </summary>
public partial class SmallPlayerController
{
    // ---- Knacks (docs/game/MECHANICS.md section 7): on the controller, never in a drawn character's motion profile ----

    /// <summary>
    /// The team's knacks that move this body's numbers: set by the kernel when the pack or the team's ranks change (Kernel/CommandHost),
    /// or by a test. None, today's body, until then. The companion's stays none: it has no knacks.
    /// </summary>
    public BodyKnacks Knacks
    {
        get => _knacks;
        set => _knacks = value ?? BodyKnacks.None;
    }
    private BodyKnacks _knacks = BodyKnacks.None;

    /// <summary>
    /// The pace on land now: the base (WalkSpeedMps, RunSpeedMps) times Quick feet. A floating body has no knack. Wading and
    /// swimming have their own factors on top (Walk, Swim). An empty bar takes the run away (the gate's NoRun), never the walk.
    /// </summary>
    private float LandSpeedFactor => Floats ? 1f : _knacks.WalkRunFactor;
    public float WalkSpeedNowMps => WalkSpeedMps * LandSpeedFactor;
    public float RunSpeedNowMps => RunSpeedMps * LandSpeedFactor;

    /// <summary>The hop's apex above the take-off floor in the room's default gravity and heavier, with High hop (JumpApexM is the base).</summary>
    public float HopApexM => JumpApexM * (Floats ? 1f : _knacks.HopFactor);
    /// <summary>The highest leap in the lightest gravity, with High hop: the cap rises with the hop, so the knack is felt on the moon too.</summary>
    public float MaxHopApexM => MaxJumpApexM * (Floats ? 1f : _knacks.HopFactor);

    /// <summary>The swim's pace at the surface (the plain swim, or the walk itself with Shift) with Strong swimmer. The dive uses it too.</summary>
    private float SwimPaceMps(bool sprint) => WalkSpeedMps * (sprint ? 1.0f : SwimSpeedFactor) * (Floats ? 1f : _knacks.SwimFactor);
    /// <summary>The plain swim's speed at the surface, and the dive's under it, with Strong swimmer (m/s). Neither is slowed by an empty bar: the water has its own rule.</summary>
    public float SwimSpeedNowMps => SwimPaceMps(false);
    public float DiveSpeedNowMps => SwimPaceMps(false) * DiveSpeedFactor;

    /// <summary>
    /// What a landing costs this body in health points (the vitals' curve with the team's safe drop; decision 24: absolute, before any
    /// difficulty). The kernel hurts by it and the tumble's look reads it.
    /// </summary>
    public float LandingCost(float landingSpeedMps, bool deepWater) => _knacks.FallDamage(landingSpeedMps, deepWater);

    // ---- The stamina ledger (Seams/StaminaLedger.cs): report, then obey ----

    /// <summary>A moving body counts as running once it covers at least this share of a walk's distance in the tick (it is not pushing a wall).</summary>
    public const float RunningShareOfWalk = 0.5f;
    /// <summary>A movement key counts as held, for a stroke in the water, past this length of the control vector (squared), as for running.</summary>
    private const float StrokeControlSquared = 0.01f;

    /// <summary>
    /// Whether a key moved the body in the water this tick (Swim, Dive): a movement key, the leap out, or under water the dive's own
    /// keys. The deep-water and dive seconds are reported only then (decision 64 of 16 October: a swimmer floating still pays nothing).
    /// </summary>
    private bool _stroked;

    private StaminaGate _gate = StaminaGate.Free;

    /// <summary>The gate the ledger held after the last tick: the bar's emptiness and whether the body may run. Free when no ledger is bound.</summary>
    public StaminaGate Gate => _gate;
    /// <summary>What the body reported to the ledger on the last tick.</summary>
    public BodyExertion LastExertion { get; private set; }
    /// <summary>Ticks reported to the ledger, and times a climb's grip failed for want of stamina.</summary>
    public int ExertionReports { get; private set; }
    public int GripFailures { get; private set; }

    /// <summary>
    /// What the bar pays for this tick beyond climbing: seconds running on foot (sprint held, moving), swimming in deep water at the
    /// surface, or under it, each only while a key moves the body (swam: a stroke this tick; a swimmer floating still, or a diver let
    /// go and drifting up, reports none, decision 64); never more than one, never more than the tick. Floating bodies and held ones
    /// (a tumble, a trip home) exert nothing.
    /// </summary>
    private BodyExertion TakeExertion(float dt, float moved, bool onFoot, bool swam, bool sprint, Vector2 control)
    {
        var exertion = BodyExertion.None;
        if (!(dt > 0) || Floats || GoingHome || _controlHoldS > 0) { LastExertion = exertion; return exertion; }
        if (swam) exertion = IsDiving ? new BodyExertion(0, 0, dt) : new BodyExertion(0, dt, 0);
        else if (onFoot && sprint && control.LengthSquared() > 0.01f && moved >= WalkSpeedNowMps * dt * RunningShareOfWalk) exertion = new BodyExertion(dt, 0, 0);
        LastExertion = exertion;
        return exertion;
    }

    /// <summary>The gate after the tick's report and VitalsTick (the ledger's); free with no ledger.</summary>
    private void TakeGate() => _gate = _staminaLedger?.Gate ?? StaminaGate.Free;

    // ---- Out of stamina in deep water: the scene fades and the body is back on the shore where it went in (the founder, 15 October,
    // decision 33: a slow drift home is annoyingly long). The fade is the way home's (B): the same veil, the same hold on the controls,
    // and the Gubble comes too. No hurt, and the bar's own rules are untouched: it refills on land, and the run stays locked until
    // the first bar is full again. ----

    /// <summary>
    /// Where the body last stood on dry, standable ground (not in the water, not on a climb, not floating): the shore it went in from.
    /// Null until it has stood somewhere, and again after a teleport (a spawn, a recovery, a trip home) until it next stands, so a body
    /// put into the water has no entry to go back to.
    /// </summary>
    public Vector3? ShoreEntry { get; private set; }
    /// <summary>Shore returns made so far (an empty bar in deep water); the trips home by B and the far net are HomeTrips.</summary>
    public int ShoreReturns { get; private set; }
    /// <summary>
    /// How the last shore return was placed: "entry" (where it went in), "nearest" (the nearest shore a swimmer could stand on,
    /// the entry being unknown or gone), "beach" (the sea's nearest beach), "start" (the spawn) or "recovered" (its last safe
    /// footing); "" before any.
    /// </summary>
    public string LastShoreReturn { get; private set; } = "";
    /// <summary>Where the swimmer was when the last shore return took it, the moment the fade was at its darkest.</summary>
    public Vector3 LastShoreReturnFrom { get; private set; }
    private bool _shoreTrip;

    /// <summary>
    /// How the nearest shore is looked for: widening rings of sight round the swimmer, nearest first (a ring every 4 cm to 1.2 m,
    /// every 10 cm to 3 m, every 40 cm to 12.6 m), sixteen places on each. The first ring that holds a shore is the nearest.
    /// </summary>
    public static float ShoreRingM(int ring) => ring < 30 ? 0.04f * (ring + 1) : ring < 48 ? 1.2f + 0.1f * (ring - 29) : 3.0f + 0.4f * (ring - 47);
    private const int ShoreRings = 72;
    private const int ShoreDirections = 16;
    /// <summary>
    /// A shore of shallows is walked on inland for dry ground this far, in steps of ShoreInlandStepM; the ground may rise ShoreInlandRiseM
    /// from one step to the next (a slope, not a bank: a standable slope rises no more than a step is long).
    /// </summary>
    private const float ShoreInlandM = 2.0f;
    private const float ShoreInlandStepM = 0.04f;
    private const float ShoreInlandRiseM = 0.05f;

    /// <summary>
    /// The ground a shore return goes back to lies at least this far above the water (a few millimetres, so that standing there
    /// again is dry whichever way the water's edge is read, and the body is put down on the very spot it left).
    /// </summary>
    public const float ShoreEntryClearM = 0.006f;

    /// <summary>
    /// Called once a tick, right after the water is read at the body's place: the spot a swimmer will be taken back to is the last
    /// one it stood on, dry.
    /// </summary>
    private void RememberShoreEntry()
    {
        if (Floats || _swimming || _climbing || GoingHome || !IsOnFloor()) return;
        if (Water.Wet && Water.Under(GlobalPosition) > -ShoreEntryClearM) return;
        ShoreEntry = GlobalPosition;
    }

    /// <summary>
    /// The gate says empty while the body swims in deep water (at the surface or under it): the way home's fade begins, to the shore
    /// instead of the jetty. Refused while a fade is already on its way (B, the far net, or this).
    /// </summary>
    private void BeginShoreReturn()
    {
        if (GoingHome) return;
        BeginHome();
        _shoreTrip = true;
        LastShoreReturnFrom = GlobalPosition;
    }

    /// <summary>
    /// The shore return at the fade's darkest: where the body went in if that ground is still there and dry (LandAt), else the
    /// nearest shore a swimmer could stand on (dry ground when some lies inland of it), else the sea's nearest beach, else the
    /// start, else the last safe footing. Standing, facing inland, its velocity cleared; the Gubble comes too.
    /// </summary>
    private void ArriveAtShore()
    {
        var here = GlobalPosition;
        LastShoreReturnFrom = here;
        LastHome = "";
        var how = "";
        if (ShoreEntry is { } entry)
        {
            // Facing the way it came from the water: away from the sea, as a trip home faces inland.
            var inland = Flat(entry - here);
            if (inland == Vector3.Zero) inland = Flat(-GlobalBasis.Z);
            if (LandAt(entry, inland, "shore")) how = "entry";
        }
        if (how == "" && FindNearestShore(here, out var spot, out var heading) && StandAtShore(spot, heading))
        {
            LastHome = "shore";
            how = "nearest";
        }
        if (how == "") how = LandAtBeachOrStart(new Vector2(here.X, here.Z));
        if (how == "")
        {
            Recover();
            how = "recovered";
        }
        // The HUD's words for a return to the water's edge; the start and a recovery keep theirs.
        if (how == "beach") LastHome = "shore";
        LastShoreReturn = how;
        Velocity = Vector3.Zero;
        ClearCreationMotion();
        ShoreReturns++;
        WentHome?.Invoke(this);
    }

    /// <summary>
    /// The nearest place a swimmer could stand without climbing, in widening rings of sight round it (StandableShore), and the
    /// heading from the swimmer to it. False when none lies within the last ring.
    /// </summary>
    private bool FindNearestShore(Vector3 feet, out Vector3 spot, out Vector3 heading)
    {
        var space = GetWorld3D().DirectSpaceState;
        for (var ring = 0; ring < ShoreRings; ring++)
        {
            var radius = ShoreRingM(ring);
            for (var step = 0; step < ShoreDirections; step++)
            {
                var angle = Mathf.Tau * step / ShoreDirections;
                heading = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                spot = feet + heading * radius;
                if (StandableShore(space, spot)) return true;
            }
        }
        (spot, heading) = (feet, Vector3.Zero);
        return false;
    }

    /// <summary>
    /// Stand at a shore the search found: the first dry ground on the way inland from it (the heading goes on from the swimmer past the
    /// shore, within ShoreInlandM and up no bank), else where the shallows are standable. Facing inland.
    /// </summary>
    private bool StandAtShore(Vector3 spot, Vector3 heading)
    {
        // The ground is looked for from above the water: the swimmer floats lower than a bank it is brought to.
        var top = (Water.Wet ? Water.SurfaceY : spot.Y) + BodyHeightM * 0.6f;
        Vector3? shallows = null;
        Vector3? previous = null;
        for (var step = 0; step * ShoreInlandStepM <= ShoreInlandM; step++)
        {
            var at = spot + heading * (step * ShoreInlandStepM);
            if (!FindSupportedPosition(new Vector3(at.X, top, at.Z), out var feet)) continue;
            if (previous is { } last && feet.Y > last.Y + ShoreInlandRiseM) break;
            previous = feet;
            if (DryAt(feet)) return TeleportFacing(feet, heading);
            shallows ??= feet;
        }
        return shallows is { } wade && TeleportFacing(wade, heading);
    }

    private bool TeleportFacing(Vector3 feet, Vector3 inland)
    {
        if (!TryTeleportTo(feet)) return false;
        if (inland != Vector3.Zero) Rotation = new Vector3(0, Mathf.Atan2(-inland.X, -inland.Z), 0);
        return true;
    }

    /// <summary>Whether a body with its feet here would stand above the water (or in none).</summary>
    private bool DryAt(Vector3 feet)
    {
        var space = GetWorld3D().DirectSpaceState;
        var water = RoomWater.At(space, feet, BodyHeightM, 0.005f, GetRid());
        if (!water.Wet && Sea != null) water = Sea.OpenWater(space, feet, BodyHeightM, 0.005f, GetRid());
        return !water.Wet || water.Under(feet) <= 0;
    }

    /// <summary>Whether a swimmer carried here would find its feet and could leave the water without climbing.</summary>
    private bool StandableShore(PhysicsDirectSpaceState3D space, Vector3 feet)
    {
        var above = Mathf.Max(BodyHeightM * 4, Water.SurfaceY - feet.Y + 0.05f);
        var column = RoomWater.At(space, feet, above, 0.05f, GetRid(), throughRoof: true);
        if (!column.Wet && Sea != null) column = Sea.OpenWater(space, feet, above, 0.05f, GetRid(), throughRoof: true);
        if (column.Wet) return column.DepthM < SwimExitDepthM;
        // Dry: ground that faces up and is no higher than the water's rim (a beach or a ramp, not a bank).
        using var query = PhysicsRayQueryParameters3D.Create(feet + Vector3.Up * (BodyHeightM * 0.5f), feet + Vector3.Down * BodyHeightM, RoomBuilder.BodyMask, ExcludeSelf);
        query.HitBackFaces = false;
        using var hit = space.IntersectRay(query);
        if (hit.Count == 0 || hit[HitNormal].AsVector3().Y < Mathf.Cos(FloorMaxAngle)) return false;
        return hit[HitPosition].AsVector3().Y <= Water.SurfaceY + RoomWater.RimM;
    }
}
