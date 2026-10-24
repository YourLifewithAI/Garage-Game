using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native;

/// <summary>
/// The body's side of the Bubble (docs/game/MECHANICS.md section 5, decision 88; Kernel/Bubble/CommandHostBubble.cs): the ride.
/// The kernel decides whether a bubble holds this body and where that bubble is; the body rides it and owns its own exit.
/// - Enveloped (a carry or a lift): the body follows the bubble's centre, with gravity, walking and jumps off; it moves only with the
///   bubble, which moves it only while it holds it.
/// - Aboard (the boat): the body stands on the deck, carried by it, and walks about on it; past the rim it has walked off.
/// - The exit is the body's own and always works: the dive key held (or a trip home) leaves at once, whatever holds it, read even
///   while a tumble holds control; holding it as a bubble forms pops it straight away.
/// - Out of a bubble is a fall (decision 88, replacing decisions 43's and 60's slow drift down): a bubble gone while the body is in the
///   air, popped by the player, its timer, the Gubble or anything else, leaves the body falling from where it is, with the motion it had
///   and none of the bubble's lift, under the body's own rules: the landing reports its speed, so it hurts as any fall (decision 24) and
///   tumbles; a climbable face pushed into on the way down is grabbed as from a jump, and the bar decides the rest; deep water catches it.
/// Unbound (no kernel, the Gubble's own body) none of this runs.
/// </summary>
public partial class SmallPlayerController
{
    /// <summary>How the body rides a bubble now (since decision 88 there is no deflate: out of a bubble the body is on its own, falling or not).</summary>
    public enum BubbleRide { None, Enveloped, Aboard }

    /// <summary>The bubble that holds this body: its centre (a boat's deck, at its middle), radius, motion, and whether it is the boat.</summary>
    public readonly record struct BubbleHold(Vector3 CenterM, float RadiusM, Vector3 VelocityMps, bool Boat);

    /// <summary>Bound by the kernel: the bubble that holds this body now, or null. Unbound: no bubble ever holds it.</summary>
    public System.Func<BubbleHold?>? BubbleHoldSource { get; set; }
    /// <summary>Bound by the kernel: the body left the bubble that held it, by the dive key or a trip home (true) or off a boat's rim (false).</summary>
    public System.Action<bool>? BubbleLeft { get; set; }
    public BubbleRide Ride { get; private set; }
    /// <summary>Whether the dive key is held this tick (the kernel turns a ghost away with it).</summary>
    public bool BubblePopKeyHeld { get; private set; }
    /// <summary>Times the body left a bubble by its own key, and times a bubble's end left it in the air, falling (tests).</summary>
    public int BubblePops { get; private set; }
    public int BubbleFalls { get; private set; }

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
            default:
                // Out of it: the walk, the fall and the grab take the body from this very tick (decision 88).
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

    /// <summary>
    /// Out of the bubble (decision 88): on the ground or in deep water the body simply stands or swims; in the air it falls from where it
    /// is, keeping the level motion the ride gave it (within a led carry's pace) and none of its rise, so gravity, the landing's
    /// speed and its fall damage, the tumble and the grab of a face are the body's own from the next step. No jump is left buffered.
    /// </summary>
    private void LeaveRide()
    {
        Ride = BubbleRide.None;
        _airDescentMps = 0;
        _vitalsLandingSpeedMps = 0;
        _jumpBuffer = 0;
        _coyote = 0;
        if (IsOnFloor() || InSwimmingWater)
        {
            Velocity = Vector3.Zero;
            return;
        }
        // The level motion a led bubble had (at most its drift's pace: the ride's catch-up is the bubble's, not the body's), no rise.
        var level = new Vector3(Velocity.X, 0, Velocity.Z).LimitLength(BubbleLaws.CarrySpeedMps);
        Velocity = new Vector3(level.X, Mathf.Min(0, Velocity.Y), level.Z);
        BubbleFalls++;
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

    /// <summary>Water deep enough to swim, with the feet at or under its surface: a body left there just swims.</summary>
    private bool InSwimmingWater => Water.Wet && Water.DepthM >= SwimDepthM && Water.Under(GlobalPosition) > 0;
}
