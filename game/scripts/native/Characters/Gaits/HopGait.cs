using System;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// The hop (Cloudpuff, Tomato Man, any drawing hinted <c>hop</c>): one hop per stride cycle, phased by distance. On the
/// ground a landing squash, then an anticipation squash; off it a stretched rise, a tuck at the top and a stretch back
/// down, the descent slower than the rise as rig.json's drift grows (a cloud drifting down). Arms the gait owns go up on
/// the rise and press down at the landing; legs and feet on spring chains dangle on them; legs nothing else owns tuck at
/// the top. Every other hop leans to the other side (alternating little hops).
/// </summary>
public sealed class HopGait : IGait
{
    /// <summary>The share of a hop cycle spent on the ground.</summary>
    public const float ContactShare = 0.24f;

    private readonly GaitRig _rig;
    private readonly Limb _armRight, _armLeft, _legRight, _legLeft;
    private readonly float _springAccel = float.MaxValue;

    public HopGait(GaitRig rig)
    {
        _rig = rig;
        _armRight = new Limb(rig, rig.Arm[GaitRig.Right], rig.Hand[GaitRig.Right], 1);
        _armLeft = new Limb(rig, rig.Arm[GaitRig.Left], rig.Hand[GaitRig.Left], -1);
        _legRight = new Limb(rig, rig.Leg[GaitRig.Right], rig.Foot[GaitRig.Right], 1);
        _legLeft = new Limb(rig, rig.Leg[GaitRig.Left], rig.Foot[GaitRig.Left], -1);
        // The weakest spring's restoring acceleration, (2πf)²·L (the engine's spring pulls like a gravity of that size): a hop
        // whose arc curves down harder than that lets the part float up and over, so the arc is kept gentler (MaxArcShare).
        foreach (var chain in rig.Body.Secondary.Chains)
        {
            var omega = Mathf.Tau * chain.FrequencyHz;
            _springAccel = Mathf.Min(_springAccel, omega * omega * chain.RootSegmentM);
        }
    }

    /// <summary>The share of the weakest spring's restoring acceleration a hop's arc may reach at its top.</summary>
    public const float MaxArcShare = 0.6f;

    public MotionKind Kind => MotionKind.Hop;
    public int StepsPerCycle => 1;
    public bool LeansAboutCentre => false;
    public float RunLeanRad => 0.07f * _rig.Profile.Multipliers.Lean;
    public int FootForStep(int step) => -1;

    /// <summary>The top of a hop at <paramref name="run"/> (0 walk to 1 run), metres.</summary>
    public float HopHeightM(float run, float walkMps = MotionProfile.WalkSpeedMps, float runMps = MotionProfile.RunSpeedMps) =>
        HopHeightAt(Mathf.Lerp(walkMps, runMps, Mathf.Clamp(run, 0, 1)), run, walkMps);

    /// <summary>
    /// The top of a hop at <paramref name="speedMps"/>: 11% of the height times rig.json's hop_height and the bounce, higher
    /// at a run, and never so high that the arc's downward curvature (about π²·H over the flight time squared) passes
    /// <see cref="MaxArcShare"/> of the weakest spring's pull.
    /// </summary>
    public float HopHeightAt(float speedMps, float run, float walkMps = MotionProfile.WalkSpeedMps)
    {
        var profile = _rig.Profile;
        var height = 0.11f * profile.HeightM * profile.Multipliers.HopHeight * profile.Bounce * (1 + 0.35f * run);
        if (_springAccel == float.MaxValue) return height;
        var speed = Mathf.Max(speedMps, 0.05f * walkMps);
        var flight = (1 - ContactShare) * profile.StrideAt(speed) / speed;
        return Mathf.Min(height, MaxArcShare * _springAccel * flight * flight / (Mathf.Pi * Mathf.Pi));
    }

    /// <summary>The landing squash's depth (fraction of height), at most the plan's 15%.</summary>
    public float SquashDepth => Mathf.Min(_rig.Profile.Squash, 0.15f);

    public void Pose(PoseBuffer pose, in GaitFrame frame, ref RootMotion root)
    {
        var profile = _rig.Profile;
        var m = profile.Multipliers;
        var travel = frame.Travel;
        var squash = SquashDepth;
        var u = GaitMath.Frac(frame.Cycles);
        float height = 0, scale = 1, raise, tuck = 0, tip = 0;
        if (u < ContactShare)
        {
            var t = u / ContactShare;
            var land = t < 0.15f ? GaitMath.Smoothstep(0, 0.15f, t) : 1 - GaitMath.Smoothstep(0.15f, 0.6f, t);
            var gather = t > 0.45f ? MathF.Pow(MathF.Sin(Mathf.Pi * (t - 0.45f) / 0.55f), 2) : 0;
            scale = 1 - squash * (land + 0.55f * gather);
            raise = -0.4f * land - 0.2f * gather;
        }
        else
        {
            var f = (u - ContactShare) / (1 - ContactShare);
            // The drift slows the descent: the top comes early and the body sinks back slowly.
            var power = Mathf.Clamp(1 / (1 + 0.35f * (m.Drift - 1)), 0.6f, 1.4f);
            var g = MathF.Pow(f, power);
            var arc = MathF.Sin(Mathf.Pi * g);
            height = HopHeightAt(frame.SpeedMps, frame.Run, frame.WalkMps > 0 ? frame.WalkMps : MotionProfile.WalkSpeedMps) * arc;
            var stretch = 0.7f * squash * MathF.Abs(MathF.Cos(Mathf.Pi * g)) * GaitMath.Smoothstep(0, 0.06f, f) * (1 - GaitMath.Smoothstep(0.88f, 1, f));
            tuck = MathF.Pow(arc, 6);
            scale = 1 + stretch - 0.35f * squash * tuck;
            raise = GaitMath.Smoothstep(0, 0.35f, g) * (1 - GaitMath.Smoothstep(0.75f, 1, f));
            tip = arc;
        }
        // A real jump or fall (not the gait's own hop): stretched along the fall, arms up.
        var air = frame.Air * frame.Active;
        var airStretch = 1 + 0.5f * squash * Mathf.Clamp(MathF.Abs(frame.VerticalMps) / 1.2f, 0, 1);
        scale = 1 + travel * (scale - 1) + air * (airStretch - 1);
        raise = travel * raise + 0.8f * air;
        root.Offset.Y += travel * height;
        root.SquashY *= scale;
        root.Roll += travel * 0.06f * m.Sway * GaitMath.Wave(0.5 * frame.Cycles);
        root.Pitch += travel * 0.05f * tip;
        var lift = 0.35f * m.ArmSwing * profile.Bounce * raise;
        _armRight.Swing(pose, 0, lift);
        _armLeft.Swing(pose, 0, lift);
        _armRight.Bend(pose, -0.3f * lift);
        _armLeft.Bend(pose, -0.3f * lift);
        // Legs the springs do not hold tuck back at the top of the hop.
        var tuckBack = 0.45f * travel * tuck + 0.3f * air;
        _legRight.Swing(pose, tuckBack, 0);
        _legLeft.Swing(pose, tuckBack, 0);
    }
}
