using Godot;

namespace GarageGame.Native;

/// <summary>
/// What a body did in one physics tick, for whatever draws it (the characters round, docs/runs/RUN-2-CHARACTERS.md). The capsule
/// moves; a drawn character reads this and poses itself. Nothing here moves the body: a visual that reads it can never push the
/// capsule, which stays the truth for collision, steps, climbing, swimming and floating.
/// </summary>
public readonly struct BodyState
{
    /// <summary>Counts the body's physics ticks from 1 (0: no tick yet).</summary>
    public ulong Tick { get; init; }
    /// <summary>The feet, in world space.</summary>
    public Vector3 Position { get; init; }
    /// <summary>The body's yaw (radians, about +Y); the drawn body may turn further to swim or climb.</summary>
    public float Yaw { get; init; }
    /// <summary>The body's velocity after this tick's move (its own motion and any creation's push), m/s.</summary>
    public Vector3 Velocity { get; init; }
    /// <summary>How fast it moved this tick along the ground, the water or the face it climbs, m/s.</summary>
    public float SpeedMps { get; init; }
    /// <summary>
    /// Metres moved since the body was made, along the ground, the water or the face (a teleport, a trip home or a wrap does not
    /// count). Gaits phase their steps by it, so feet never slide. A double: a float's step passes a 5 mm tick within kilometres,
    /// and the phase would stall (the characters round's review).
    /// </summary>
    public double DistanceM { get; init; }
    public bool Grounded { get; init; }
    /// <summary>The floor's normal while grounded; up otherwise.</summary>
    public Vector3 FloorNormal { get; init; }
    /// <summary>Seconds since the body last stood, climbed, swam or floated (0 while it does).</summary>
    public float AirTimeS { get; init; }
    /// <summary>This tick the body came down from the air onto a floor, or into water deep enough to catch it.</summary>
    public bool Landed { get; init; }
    /// <summary>The downward speed of that landing, the speed fall damage reads (PlayerVitals.FallDamage); 0 without a landing.</summary>
    public float LandingSpeedMps { get; init; }
    /// <summary>The landing was into water deep enough to catch the body, which never hurts.</summary>
    public bool LandedInDeepWater { get; init; }
    public bool Climbing { get; init; }
    /// <summary>The climbed face's outward normal while climbing.</summary>
    public Vector3 ClimbNormal { get; init; }
    public bool Swimming { get; init; }
    /// <summary>0 upright to 1 lying in the water to swim.</summary>
    public float SwimTilt { get; init; }
    public bool Diving { get; init; }
    /// <summary>The body floats (the Gubble): it never stands, climbs or swims.</summary>
    public bool Floating { get; init; }
    /// <summary>
    /// A bubble holds the body (a carry or a lift: SmallPlayerController.BubbleRide.Enveloped, decision 43): the drawn body floats inside
    /// it, as a float gait would, whatever its own gait is (it neither steps nor falls). A boat is no float: the body stands on its deck.
    /// </summary>
    public bool InBubble { get; init; }
    /// <summary>Its hands are full (the command host's answer).</summary>
    public bool Carrying { get; init; }
    public bool Sprinting { get; init; }
    /// <summary>
    /// The speeds this body walks and runs at now, m/s: its own base times its knack (Quick feet) and the empty bar's slow. The
    /// gaits and the contact layers measure a body's pace against these, not against fixed numbers. 0 where a state was built
    /// without them (a test double): readers then fall back to the shared base (Characters/Gaits/BodyPace).
    /// </summary>
    public float WalkSpeedMps { get; init; }
    public float RunSpeedMps { get; init; }
    /// <summary>The player pushes but the body does not move.</summary>
    public bool Blocked { get; init; }
    /// <summary>The wind's push on the body, m/s in X and Z.</summary>
    public Vector2 WindMps { get; init; }
    /// <summary>Control is held (a tumble): input is ignored until it is released or runs out.</summary>
    public bool ControlHeld { get; init; }
    /// <summary>The movement the player asks for while control is held (zero otherwise): a tumble's early get-up reads it.</summary>
    public Vector2 HeldInput { get; init; }
    /// <summary>The water's surface height where the body is, or NaN on dry ground.</summary>
    public float WaterSurfaceY { get; init; }
    public float WaterDepthM { get; init; }
    public float BodyHeightM { get; init; }
    /// <summary>The world's gravity now (m/s², world.set_physics), which secondary motion should fall under.</summary>
    public float GravityMps2 { get; init; }
}

/// <summary>
/// Something that draws a body in place of the prototype figure: a drawn character's rig (Characters/), or a test double.
/// </summary>
public interface IBodyVisual
{
    /// <summary>
    /// The node the body puts under its visual root (the node named OriginalPrototypeBody, kept for the tests and the look). The
    /// body owns this node's transform: the seat on the floor and the swim tilt are on the visual root, and the Gubble's gestures
    /// move this node. The visual poses what lies under it.
    /// </summary>
    Node3D Root { get; }

    /// <summary>Called at the end of every physics tick, after the body moved and its state was taken.</summary>
    void BodyTicked(in BodyState state, float delta);
}
