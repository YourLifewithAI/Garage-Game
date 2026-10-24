using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native;

/// <summary>
/// The body's side of the Bubble (docs/game/MECHANICS.md section 5, decision 43; Kernel/Bubble/CommandHostBubble.cs): the ride.
/// The kernel decides whether a bubble holds this body and where that bubble is; the body rides it and owns its own exit.
/// - Enveloped (a carry or a lift): the body follows the bubble's centre, with gravity, walking and jumps off; it moves only with the
///   bubble, which moves it only while it holds it.
/// - Aboard (the boat): the body stands on the deck, carried by it, and walks about on it; past the rim it has walked off.
/// - The exit is the body's own and always works: the dive key held (or a trip home) leaves at once, whatever holds it, read even
///   while a tumble holds control; holding it as a bubble forms pops it straight away.
/// - Deflating: a bubble gone while the body is in the air, popped by the player, its timer or anything else, lets it down slowly
///   (BubbleLaws.DeflateDescentMps, the player steering a little) until it stands or the water takes it. A deflate landing reports no
///   landing speed, so it never hurts and never tumbles; landing in water just swims.
/// Unbound (no kernel, the Gubble's own body) none of this runs.
/// </summary>
public partial class SmallPlayerController
{
    /// <summary>How the body rides a bubble now.</summary>
    public enum BubbleRide { None, Enveloped, Aboard, Deflating }

    /// <summary>The bubble that holds this body: its centre (a boat's deck, at its middle), radius, motion, and whether it is the boat.</summary>
    public readonly record struct BubbleHold(Vector3 CenterM, float RadiusM, Vector3 VelocityMps, bool Boat);

    /// <summary>Bound by the kernel: the bubble that holds this body now, or null. Unbound: no bubble ever holds it.</summary>
    public System.Func<BubbleHold?>? BubbleHoldSource { get; set; }
    /// <summary>Bound by the kernel: the body left the bubble that held it, by the dive key or a trip home (true) or off a boat's rim (false).</summary>
    public System.Action<bool>? BubbleLeft { get; set; }
    public BubbleRide Ride { get; private set; }
    /// <summary>Whether the dive key is held this tick (the kernel turns a ghost away with it).</summary>
    public bool BubblePopKeyHeld { get; private set; }
    /// <summary>Times the body left a bubble by its own key, and landings at the end of a deflate (tests).</summary>
    public int BubblePops { get; private set; }
    public int DeflateLandings { get; private set; }

    /// <summary>The ride's tick, first in the movement chain: true when the bubble moved the body this tick (nothing else moves it then).</summary>
    private bool RideBubble(float dt, Vector3 wish)
    {
        if (BubbleHoldSource == null && Ride == BubbleRide.None)
        {
            BubblePopKeyHeld = false;
            return false;
        }
        // The exit is read whatever holds control: a tumble or the home fade never keeps the body in.
        var popKey = ReadKeyboard ? PlayerControls.Held(Act.MoveDive) : _diveDown;
        BubblePopKeyHeld = popKey;
        var hold = BubbleHoldSource?.Invoke();
        if (Ride is BubbleRide.Enveloped or BubbleRide.Aboard)
        {
            if (hold is not { } still || still.Boat != (Ride == BubbleRide.Aboard)) LeaveRide();
            else if (popKey || GoingHome) LeaveByKey();
            // Put down somewhere else (a recovery, a teleport): the body has left its bubble and is never dragged back to it.
            else if (Ride == BubbleRide.Enveloped && (GlobalPosition + Vector3.Up * (BodyHeightM * 0.5f)).DistanceTo(still.CenterM) > still.RadiusM + 2 * BubbleLaws.RiderSlackM)
            {
                BubbleLeft?.Invoke(false);
                LeaveRide();
            }
        }
        else if (hold is { } holds)
        {
            if (popKey || GoingHome) LeaveByKey();
            else StartRide(holds);
        }
        switch (Ride)
        {
            case BubbleRide.Enveloped when hold is { } bubble:
                FollowBubble(bubble, dt);
                return true;
            case BubbleRide.Aboard when hold is { } boat:
                RideBoat(boat, dt, wish);
                return true;
            case BubbleRide.Deflating:
                Deflate(wish);
                return true;
            default:
                return false;
        }
    }

    private void StartRide(BubbleHold hold)
    {
        Ride = hold.Boat ? BubbleRide.Aboard : BubbleRide.Enveloped;
        StopClimbingAndSwimming();
        Velocity = Vector3.Zero;
        _airDescentMps = 0;
        _vitalsLandingSpeedMps = 0;
        _jumpBuffer = 0;
    }

    private void LeaveByKey()
    {
        BubblePops++;
        BubbleLeft?.Invoke(true);
        LeaveRide();
    }

    /// <summary>Out of the bubble: in the air it deflates, on the ground or in deep water it simply stands or swims.</summary>
    private void LeaveRide()
    {
        Ride = IsOnFloor() || InSwimmingWater ? BubbleRide.None : BubbleRide.Deflating;
        _airDescentMps = 0;
        _vitalsLandingSpeedMps = 0;
        if (Ride == BubbleRide.None) Velocity = Vector3.Zero;
    }

    /// <summary>Enveloped: the body's middle goes where the bubble's centre is, as fast as it needs (within the ride's limit).</summary>
    private void FollowBubble(BubbleHold hold, float dt)
    {
        var feet = hold.CenterM - Vector3.Up * (BodyHeightM * 0.5f);
        var velocity = dt > 0 ? (feet - GlobalPosition) / dt : Vector3.Zero;
        Velocity = velocity.LimitLength(BubbleLaws.RideFollowMaxMps);
        _jumpBuffer = 0;
        MoveAndSlide();
    }

    /// <summary>Aboard: the deck carries the body, which walks about on it; past the rim it has walked off.</summary>
    private void RideBoat(BubbleHold hold, float dt, Vector3 wish)
    {
        var own = wish * WalkSpeedNowMps;
        var rise = dt > 0 ? Mathf.Clamp((hold.CenterM.Y - GlobalPosition.Y) / dt, -BubbleLaws.RideFollowMaxMps, BubbleLaws.RideFollowMaxMps) : 0;
        Velocity = new Vector3(own.X + hold.VelocityMps.X, rise, own.Z + hold.VelocityMps.Z);
        _jumpBuffer = 0;
        MoveAndSlide();
        if (new Vector2(GlobalPosition.X - hold.CenterM.X, GlobalPosition.Z - hold.CenterM.Z).Length() <= hold.RadiusM) return;
        BubbleLeft?.Invoke(false);
        LeaveRide();
    }

    /// <summary>Water deep enough to swim, with the feet at or under its surface: it catches a deflating body.</summary>
    private bool InSwimmingWater => Water.Wet && Water.DepthM >= SwimDepthM && Water.Under(GlobalPosition) > 0;

    /// <summary>The deflate: a slow drift down the player steers a little, until the body stands or deep water takes it.</summary>
    private void Deflate(Vector3 wish)
    {
        var drift = wish * BubbleLaws.DeflateSteerMps;
        Velocity = new Vector3(drift.X, -BubbleLaws.DeflateDescentMps, drift.Z);
        _jumpBuffer = 0;
        _airDescentMps = 0;
        MoveAndSlide();
        // Shallow water is waded: the drift goes on down to its bed. Deep water takes the body at its surface, and it swims.
        if (!IsOnFloor() && !InSwimmingWater) return;
        Ride = BubbleRide.None;
        Velocity = Vector3.Zero;
        DeflateLandings++;
        // Deep water takes it at once: it swims from this tick, so no tick of falling in between reports a landing speed.
        if (!IsOnFloor() && CanSwim && !_swimming) StartSwim();
    }
}
