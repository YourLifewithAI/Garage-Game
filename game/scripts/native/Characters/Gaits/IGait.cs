using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// What the gait layer worked out from the body's state this tick, for the gait's shapes. Every weight is a critically
/// damped spring's value, so starts, stops, jumps and climbs blend in and out.
/// </summary>
public struct GaitFrame
{
    /// <summary>Stride cycles travelled since the layer started: the phase is its fraction. Advanced by distance only.</summary>
    public double Cycles;
    /// <summary>The body's ground speed, lightly smoothed (m/s).</summary>
    public float SpeedMps;
    /// <summary>The stride cycle's length at this speed (<see cref="MotionProfile.StrideAt"/>).</summary>
    public float StrideM;
    /// <summary>The walk and the run of the body now (m/s; BodyPace): what <see cref="Run"/> is measured against.</summary>
    public float WalkMps, RunMps;
    /// <summary>0 standing to 1 moving.</summary>
    public float Move;
    /// <summary>0 at a walk to 1 at a run.</summary>
    public float Run;
    /// <summary>0 on the ground to 1 in the air (a jump or a fall, not the gait's own hop).</summary>
    public float Air;
    /// <summary>1 normally; toward 0 while climbing or swimming, when the contact layer poses the body.</summary>
    public float Active;
    /// <summary>The body's vertical speed (m/s, up positive).</summary>
    public float VerticalMps;
    public double TimeS;
    public float DeltaS;

    /// <summary>The weight of the gait's own travel shapes: moving, on the ground, not yielding.</summary>
    public readonly float Travel => Move * Active * (1 - Air);
}

/// <summary>What a gait asks of the root bone; the layer adds the lean and composes it about the right pivot.</summary>
public struct RootMotion
{
    /// <summary>A shift of the whole character (metres, character space).</summary>
    public Vector3 Offset;
    /// <summary>Radians: top toward -Z (forward) positive.</summary>
    public float Pitch;
    /// <summary>Radians: top toward +X (the character's right) positive.</summary>
    public float Roll;
    /// <summary>Radians about +Y.</summary>
    public float Yaw;
    /// <summary>The squash (below 1) or stretch (above 1) along Y, volume kept, about the ground.</summary>
    public float SquashY;
}

/// <summary>
/// One gait's shapes (the plan's IGait): the base pose of the limbs from the frame, and the root motion it asks for.
/// Sizes come from the character's motion profile and rig.json's multipliers; never from its name.
/// </summary>
public interface IGait
{
    MotionKind Kind { get; }
    /// <summary>Footfalls per stride cycle: 2 for a stride or a waddle, 1 for a hop, 0 for a float.</summary>
    int StepsPerCycle { get; }
    /// <summary>A float leans about its body's middle; the others about the edge of their footprint, which keeps every part above the floor.</summary>
    bool LeansAboutCentre { get; }
    /// <summary>The forward lean at a full run (radians), on top of the lean into acceleration.</summary>
    float RunLeanRad { get; }
    /// <summary>The bone that comes down on step <paramref name="step"/> (0 the first of the cycle), or -1 for both feet or none.</summary>
    int FootForStep(int step);
    void Pose(PoseBuffer pose, in GaitFrame frame, ref RootMotion root);
}
