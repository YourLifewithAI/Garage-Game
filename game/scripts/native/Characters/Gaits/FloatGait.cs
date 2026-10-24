using System;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// The float (the Gubble, or any drawing hinted <c>float</c>): a slow bob, about ±4 mm over 2.6 s, lifted so the hem never
/// dips below where it was drawn; a slow wander and sway so it is never fully still; a lean into travel and a bank into
/// turns (the layer's lean spring, about the body's middle); a gentle roll phased by the distance it floats. Arms the
/// rig left without a spring drift with the bob. No footfalls.
/// </summary>
public sealed class FloatGait : IGait
{
    /// <summary>The bob's period and half-height at a sway and bounce of 1.</summary>
    public const float BobPeriodS = 2.6f, BobM = 0.004f;

    private readonly GaitRig _rig;
    private readonly Limb _armRight, _armLeft;
    private readonly int _head;

    public FloatGait(GaitRig rig)
    {
        _rig = rig;
        _armRight = new Limb(rig, rig.Arm[GaitRig.Right], rig.Hand[GaitRig.Right], 1);
        _armLeft = new Limb(rig, rig.Arm[GaitRig.Left], rig.Hand[GaitRig.Left], -1);
        _head = rig.Swingable(rig.HeadBone) ? rig.HeadBone : -1;
    }

    public MotionKind Kind => MotionKind.Float;
    public int StepsPerCycle => 0;
    public bool LeansAboutCentre => true;
    public float RunLeanRad => 0.06f * _rig.Profile.Multipliers.Lean;
    public int FootForStep(int step) => -1;

    /// <summary>The bob's half-height for this character (metres).</summary>
    public float BobAmplitudeM => BobM * _rig.Profile.Multipliers.Sway * _rig.Profile.Bounce;

    public void Pose(PoseBuffer pose, in GaitFrame frame, ref RootMotion root)
    {
        var profile = _rig.Profile;
        var m = profile.Multipliers;
        var t = frame.TimeS;
        var active = frame.Active;
        var bob = GaitMath.Wave(t / BobPeriodS);
        var amplitude = BobAmplitudeM;
        // Never still: a slow wander and sway on incommensurate periods, sized by the drift.
        var wander = 0.0015f * m.Drift;
        root.Offset += active * new Vector3(
            wander * GaitMath.Wave(t / 7.3),
            amplitude * (1 + bob),
            0.7f * wander * GaitMath.Wave(t / 5.9 + 0.17));
        root.Yaw += active * 0.05f * m.Drift * GaitMath.Wave(t / 9.7 + 0.06);
        // A gentle roll as it travels, phased by distance; an idle sway at rest.
        root.Roll += active * (0.02f * m.Sway * GaitMath.Wave(t / 4.3 + 0.32) + 0.045f * m.Sway * frame.Move * GaitMath.Wave(frame.Cycles));
        // A soft bubble: squashed at the bottom of the bob, a little stretched at the top.
        root.SquashY *= 1 + active * 0.12f * profile.Squash * bob;
        var drift = active * 0.14f * GaitMath.Wave(t / BobPeriodS + 0.1);
        _armRight.Swing(pose, 0.25f * drift, 0.5f * drift);
        _armLeft.Swing(pose, -0.25f * drift, 0.5f * drift);
        if (_head >= 0) pose.AddRotation(_head, GaitMath.Pitch(active * 0.03f * GaitMath.Wave(t / BobPeriodS + 0.6)));
    }
}
