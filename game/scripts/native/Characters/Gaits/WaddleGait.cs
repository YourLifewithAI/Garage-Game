using System;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// The waddle (Potato Man, and any legless drawing hinted <c>stride</c>): two steps per stride cycle, phased by distance.
/// The body rolls onto one foot and then the other, about the outer edge of the foot it stands on, which lifts the body
/// and the other foot a little on each roll; a yaw wobble runs a quarter cycle out of phase; a small squash at each
/// touchdown. The roll is wider and the steps slower at a walk, tighter and quicker at a run. Feet the gait owns shuffle
/// under the body (the stance foot moving back as the body moves on), the lifted one tipping its toe; arms on springs lag
/// on their own, arms without them swing a little.
/// </summary>
public sealed class WaddleGait : IGait
{
    private readonly GaitRig _rig;
    private readonly int _footRight, _footLeft;
    private readonly Limb _armRight, _armLeft;
    private readonly float _shuffleM;

    public WaddleGait(GaitRig rig)
    {
        _rig = rig;
        _footRight = rig.Swingable(rig.Foot[GaitRig.Right]) ? rig.Foot[GaitRig.Right] : -1;
        _footLeft = rig.Swingable(rig.Foot[GaitRig.Left]) ? rig.Foot[GaitRig.Left] : -1;
        _armRight = new Limb(rig, rig.Arm[GaitRig.Right], rig.Hand[GaitRig.Right], 1);
        _armLeft = new Limb(rig, rig.Arm[GaitRig.Left], rig.Hand[GaitRig.Left], -1);
        var footLength = rig.Profile.FootLengthM > 0 ? rig.Profile.FootLengthM : 0.3f * rig.Profile.BodyDepthM;
        _shuffleM = Mathf.Min(rig.Profile.ReachM, 0.8f * footLength);
    }

    public MotionKind Kind => MotionKind.Waddle;
    public int StepsPerCycle => 2;
    public bool LeansAboutCentre => false;
    public float RunLeanRad => 0.05f * _rig.Profile.Multipliers.Lean;
    public int FootForStep(int step) => (step & 1) == 0 ? _rig.Foot[GaitRig.Right] : _rig.Foot[GaitRig.Left];

    /// <summary>The roll to each side at <paramref name="run"/> (0 walk to 1 run), radians.</summary>
    public float RollRad(float run)
    {
        var profile = _rig.Profile;
        return 0.12f * profile.Multipliers.Sway * profile.Bounce * Mathf.Lerp(1, 0.6f, run);
    }

    public void Pose(PoseBuffer pose, in GaitFrame frame, ref RootMotion root)
    {
        var profile = _rig.Profile;
        var m = profile.Multipliers;
        var travel = frame.Travel;
        if (travel <= 1e-4f) return;
        var u = GaitMath.Frac(frame.Cycles);
        var roll = MathF.Sin(Mathf.Tau * u);
        var wobble = MathF.Cos(Mathf.Tau * u);
        // Onto the right foot for the first half of the cycle (roll toward +X), the left for the second.
        root.Roll += travel * RollRad(frame.Run) * roll;
        root.Yaw += travel * 0.07f * m.Sway * profile.Bounce * Mathf.Lerp(1, 0.7f, frame.Run) * wobble;
        // Both feet down at each touchdown (u = 0 and 0.5): the body settles into them.
        var touchdown = 0.5f + 0.5f * MathF.Cos(2 * Mathf.Tau * u);
        root.SquashY *= 1 - travel * 0.3f * Mathf.Min(profile.Squash, 0.15f) * touchdown;
        root.Offset.Y += travel * 0.0012f * profile.Bounce * MathF.Abs(roll);
        // The stance foot slides back under the body as it moves on; the lifted one comes forward, toe up.
        var shuffle = 0.5f * _shuffleM * travel;
        if (_footRight >= 0)
        {
            pose.AddPosition(_footRight, new Vector3(0, 0, -shuffle * wobble));
            pose.AddRotation(_footRight, new Quaternion(Vector3.Right, travel * 0.3f * MathF.Max(0, -roll)));
        }
        if (_footLeft >= 0)
        {
            pose.AddPosition(_footLeft, new Vector3(0, 0, shuffle * wobble));
            pose.AddRotation(_footLeft, new Quaternion(Vector3.Right, travel * 0.3f * MathF.Max(0, roll)));
        }
        var swing = travel * 0.3f * m.ArmSwing * wobble;
        _armRight.Swing(pose, swing, 0.1f * travel);
        _armLeft.Swing(pose, -swing, 0.1f * travel);
    }
}
