using System;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// The stride (Stickbear, any drawing with legs hinted <c>stride</c>): two steps per stride cycle, phased by distance.
/// A 10 cm body at the shared walk speed covers far more ground per step than its legs can reach, so each step is a
/// lope: a planted stance while the hip passes over the foot (the leg's angle follows the distance travelled, the
/// pelvis drops so the foot stays on the floor and the foot stays level: no sliding), then a short flight. The free leg
/// swings through with a high step (lifted into the hip, toe up); arms counter-swing, hands trailing; the head bobs
/// twice a stride; a run lengthens the stride, raises the flight and leans the body. Works without the contact layer's
/// IK; with it, the IK only has to correct the plant on uneven ground.
/// </summary>
public sealed class StrideGait : IGait
{
    /// <summary>The planted leg's sweep either side of vertical, at most (radians).</summary>
    public const float StanceSweepRad = 0.35f;
    /// <summary>The swinging leg's extra forward reach at mid-swing (the high step), radians.</summary>
    public const float HighStepRad = 0.25f;

    private readonly GaitRig _rig;
    private readonly int[] _leg = new int[2];
    private readonly int[] _foot = new int[2];
    private readonly Limb _armRight, _armLeft;
    private readonly int _head, _body;
    private readonly float _legLength;

    public StrideGait(GaitRig rig)
    {
        _rig = rig;
        for (var s = 0; s < 2; s++)
        {
            _leg[s] = rig.Swingable(rig.Leg[s]) ? rig.Leg[s] : -1;
            _foot[s] = _leg[s] >= 0 && rig.Swingable(rig.Foot[s]) ? rig.Foot[s] : -1;
        }
        _armRight = new Limb(rig, rig.Arm[GaitRig.Right], rig.Hand[GaitRig.Right], 1);
        _armLeft = new Limb(rig, rig.Arm[GaitRig.Left], rig.Hand[GaitRig.Left], -1);
        _head = rig.Swingable(rig.HeadBone) ? rig.HeadBone : -1;
        _body = rig.Swingable(rig.BodyBone) ? rig.BodyBone : -1;
        // The pendulum the planted foot swings on: hip to ankle when a level foot carries the sole, else hip to the ground.
        var hip = 0f;
        var ankle = 0f;
        var legs = 0;
        for (var s = 0; s < 2; s++)
        {
            if (rig.Leg[s] < 0) continue;
            hip += rig.Body.Bones[rig.Leg[s]].RestModel.Y;
            ankle += rig.Foot[s] >= 0 ? rig.Body.Bones[rig.Foot[s]].RestModel.Y : 0;
            legs++;
        }
        _legLength = legs > 0 ? Mathf.Max((hip - ankle) / legs, 0.005f) : Mathf.Max(rig.Profile.LegLengthM, 0.005f);
    }

    public MotionKind Kind => MotionKind.Stride;
    public int StepsPerCycle => 2;
    public bool LeansAboutCentre => false;
    public float RunLeanRad => 0.1f * _rig.Profile.Multipliers.Lean;
    public int FootForStep(int step) => (step & 1) == 0 ? _rig.Foot[GaitRig.Right] : _rig.Foot[GaitRig.Left];

    /// <summary>The leg the stride swings from hip to ankle, metres.</summary>
    public float LegLengthM => _legLength;

    /// <summary>The share of each step a foot is planted at this stride length: the planted reach over the step.</summary>
    public float ContactShare(float strideM, out float sweepRad)
    {
        var step = Mathf.Max(0.5f * strideM, 1e-4f);
        var reach = 2 * _legLength * MathF.Sin(StanceSweepRad);
        var share = Mathf.Clamp(reach / step, 0.15f, 0.85f);
        sweepRad = MathF.Asin(Mathf.Min(0.95f, share * step / (2 * _legLength)));
        return share;
    }

    /// <summary>The top of the flight between steps at <paramref name="run"/>, metres above standing.</summary>
    public float FlightM(float run) => _legLength * (0.06f + 0.1f * run) * _rig.Profile.Bounce;

    /// <summary>
    /// A leg's angle at its own phase <paramref name="p"/> (0 at its touchdown; positive swings the foot forward), how far it
    /// is drawn up into the hip, and how far its toe tips up.
    /// </summary>
    public static float LegAngle(float p, float share, float sweep, float drop, out float retract, out float toe)
    {
        var half = 0.5f * share;
        if (p < half)
        {
            retract = 0;
            toe = 0;
            return MathF.Asin(MathF.Sin(sweep) * (1 - 2 * p / half));
        }
        var t = (p - half) / (1 - half);
        var arc = MathF.Sin(Mathf.Pi * t);
        retract = (drop + 0.002f) * MathF.Pow(arc, 0.8f);
        toe = 0.3f * arc;
        return -sweep + 2 * sweep * GaitMath.Smootherstep(t) + HighStepRad * arc;
    }

    public void Pose(PoseBuffer pose, in GaitFrame frame, ref RootMotion root)
    {
        var profile = _rig.Profile;
        var m = profile.Multipliers;
        var travel = frame.Travel;
        var air = frame.Air * frame.Active;
        if (travel <= 1e-4f && air <= 1e-4f) return;
        var share = ContactShare(frame.StrideM, out var sweep);
        var drop = _legLength * (1 - MathF.Cos(sweep));
        var flight = FlightM(frame.Run);
        var cycles = frame.Cycles;

        // The pelvis: on the planted leg's circle during contact (the foot stays on the floor), then a short flight.
        var q = GaitMath.Frac(2 * cycles);
        float lift;
        if (q < share)
        {
            var stance = MathF.Asin(MathF.Sin(sweep) * (1 - 2 * q / share));
            lift = -_legLength * (1 - MathF.Cos(stance));
        }
        else
        {
            var f = (q - share) / (1 - share);
            lift = -drop + (flight + drop) * 4 * f * (1 - f);
        }
        root.Offset.Y += travel * lift;

        Span<float> angles = stackalloc float[2];
        for (var s = 0; s < 2; s++)
        {
            var p = GaitMath.Frac(cycles + (s == GaitRig.Left ? 0.5 : 0));
            var angle = LegAngle(p, share, sweep, drop, out var retract, out var toe);
            // In the air: one leg reaching forward, the other trailing.
            var airAngle = s == GaitRig.Right ? 0.35f : -0.25f;
            var a = travel * angle + air * airAngle;
            angles[s] = a;
            if (_leg[s] < 0) continue;
            pose.AddRotation(_leg[s], new Quaternion(Vector3.Right, a));
            if (retract > 0) pose.AddPosition(_leg[s], new Vector3(0, travel * retract, 0));
            // The foot stays level under the turning leg, tipping its toe up as it swings.
            if (_foot[s] >= 0) pose.AddRotation(_foot[s], new Quaternion(Vector3.Right, -a + travel * toe + air * 0.2f));
        }

        // Arms counter-swing their own side's leg; hands trail a little; up and out in the air.
        var swing = 0.7f * m.ArmSwing;
        _armRight.Swing(pose, swing * angles[GaitRig.Right], 0.25f * air + 0.08f * travel * frame.Run);
        _armLeft.Swing(pose, swing * angles[GaitRig.Left], 0.25f * air + 0.08f * travel * frame.Run);
        var trail = 0.25f * swing * travel;
        _armRight.Bend(pose, trail * MathF.Sin(Mathf.Tau * (GaitMath.Frac(cycles) - 0.1f) + 0.5f * Mathf.Pi));
        _armLeft.Bend(pose, trail * MathF.Sin(Mathf.Tau * (GaitMath.Frac(cycles + 0.5) - 0.1f) + 0.5f * Mathf.Pi));

        // The head bobs twice a stride, a beat behind the body; the body twists a little with the hips.
        if (_head >= 0) pose.AddRotation(_head, GaitMath.Pitch(travel * 0.05f * profile.Bounce * MathF.Sin(Mathf.Tau * GaitMath.Frac(2 * cycles) - 0.7f)));
        if (_body >= 0) pose.AddRotation(_body, GaitMath.Yaw(travel * 0.06f * GaitMath.Wave(cycles)));
    }
}
