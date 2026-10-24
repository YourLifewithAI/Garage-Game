using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// The Gubble's gestures on its drawn body (the characters round, K5; docs/runs/RUN-2-CHARACTERS.md, "The Gubble" and "The pose
/// stack", layer 5): the companion's shake, shiver, wiggle, shrug, cast and cast-on-itself, ported to the rig with the same
/// envelopes and numbers. The companion still times every gesture and works out its transform (CompanionAvatar.GestureOffset, about
/// the body's middle, or its feet for a cast); with a drawn body it moves nothing itself, and this layer adds that transform to the
/// root bone, so the drawn body leans, shakes and lifts exactly as the prototype figure's parts did, and its springs (the arm, the
/// flame, the sparkles) follow through. During a cast at a spot the torch arm also swings toward the spot on the cast's own envelope
/// (CompanionAvatar.CastEnvelope), a toss with the torch raised. The arm and the torch are found by role, never by name: the arm
/// that holds the torch (or, without one, a wand). The layer reads the companion's gesture state, its LastCastToward and where the
/// skeleton stands; it allocates nothing.
/// </summary>
public sealed class GubbleGestureLayer : IPoseLayer<BodyState>
{
    /// <summary>The most the torch arm swings toward the spot, at the cast's peak (radians; about 57 degrees).</summary>
    public const float CastArmSwingRad = 1.0f;
    /// <summary>The swing aims this far above the level line from the arm to the spot: a toss, the torch raised as it throws.</summary>
    public const float CastArmRiseRad = 0.35f;

    private readonly CompanionAvatar _companion;
    private readonly CharacterBody _body;
    private readonly Vector3 _reachRest;

    public GubbleGestureLayer(CompanionAvatar companion, CharacterBody body)
    {
        _companion = companion;
        _body = body;
        ArmBone = -1;
        PropBone = HeldProp(body);
        for (var bone = PropBone; bone > 0; bone = body.Bones[bone].Parent)
            if (body.Bones[bone].Role == PartRole.Arm && !body.Bones[bone].IsRigid) { ArmBone = bone; break; }
        // No prop drawn in the hand (the Gubble's torch is a separate item since the founder's note of 13 October): the arm
        // that ends in a hand, where a carried thing sits, swings instead, and the hand is the end it reaches with.
        for (var i = 1; i < body.BoneCount && ArmBone < 0; i++)
        {
            if (body.Bones[i].Role is not (PartRole.Hand or PartRole.Paw) || body.Bones[i].IsRigid) continue;
            for (var up = body.Bones[i].Parent; up > 0; up = body.Bones[up].Parent)
                if (body.Bones[up].Role == PartRole.Arm && !body.Bones[up].IsRigid) { ArmBone = up; PropBone = i; break; }
        }
        if (ArmBone >= 0)
        {
            _reachRest = body.Bones[PropBone].RestModel - body.Bones[ArmBone].RestModel;
            if (_reachRest.LengthSquared() < 1e-10f) ArmBone = -1;
        }
    }

    public PoseLayer Layer => PoseLayer.Gesture;
    /// <summary>The arm the cast swings: the one holding a drawn prop, else the one ending in a hand; -1 for a body with none.</summary>
    public int ArmBone { get; }
    /// <summary>The end the arm reaches with: a prop drawn in its hand (a torch, a wand), else the hand itself; -1 for none.</summary>
    public int PropBone { get; }
    /// <summary>The swing the last Apply gave the torch arm, radians (0 when it gave none).</summary>
    public float LastArmSwingRad { get; private set; }
    /// <summary>Where the arm reached at rest, from its joint to the prop's, in the character's space.</summary>
    public Vector3 ReachRest => _reachRest;

    /// <summary>The torch a body holds (the first non-rigid torch part), else a wand; -1 for neither.</summary>
    public static int HeldProp(CharacterBody body)
    {
        foreach (var role in new[] { PartRole.Torch, PartRole.Wand })
            for (var i = 1; i < body.BoneCount; i++)
                if (body.Bones[i].Role == role && !body.Bones[i].IsRigid) return i;
        return -1;
    }

    public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        LastArmSwingRad = 0;
        if (!GodotObject.IsInstanceValid(_companion) || !GodotObject.IsInstanceValid(_body)) return;
        var playing = _companion.Playing;
        if (playing == CompanionAvatar.Gesture.None) return;

        // The companion's offset is in its visual root's space; the root bone's is the skeleton's (the same unless the body or the
        // skeleton node were moved under the visual root).
        var skeletonInRoot = _body.Transform * _body.Skeleton.Transform;
        var offset = skeletonInRoot.AffineInverse() * _companion.GestureOffset * skeletonInRoot;
        const int root = CharacterBody.RootBone;
        var position = pose.GetPosition(root);
        pose.AddRotation(root, offset.Basis.GetRotationQuaternion());
        pose.AddPosition(root, offset * position - position);

        if (playing != CompanionAvatar.Gesture.Cast || ArmBone < 0 || _companion.LastCastToward is not { } spot || !spot.IsFinite()) return;
        var envelope = CompanionAvatar.CastEnvelope(_companion.GestureProgress);
        if (envelope <= 0 || !_body.Skeleton.IsInsideTree()) return;
        LastArmSwingRad = SwingArm(pose, _body.Skeleton.GlobalTransform.AffineInverse() * spot, envelope);
    }

    /// <summary>
    /// Turns the torch arm about its shoulder toward a spot (in the skeleton's space): from where it reaches now toward the level
    /// line to the spot raised by CastArmRiseRad, by at most CastArmSwingRad, times the envelope. Returns the angle it turned.
    /// </summary>
    public float SwingArm(PoseBuffer pose, Vector3 spot, float envelope)
    {
        if (ArmBone < 0 || !spot.IsFinite() || !float.IsFinite(envelope) || envelope <= 0) return 0;
        var arm = pose.ModelTransform(ArmBone);
        var level = new Vector3(spot.X - arm.Origin.X, 0, spot.Z - arm.Origin.Z);
        if (level.LengthSquared() < 1e-8f) return 0;
        var aim = level.Normalized() * Mathf.Cos(CastArmRiseRad) + Vector3.Up * Mathf.Sin(CastArmRiseRad);
        var reach = (arm.Basis * _reachRest).Normalized();
        var angle = reach.AngleTo(aim);
        var axis = reach.Cross(aim);
        if (angle < 1e-4f || axis.LengthSquared() < 1e-12f) return 0;
        var swing = Mathf.Min(angle, CastArmSwingRad) * Mathf.Min(envelope, 1f);
        // AddRotation turns the bone in its parent's frame: the turn found in the character's space, seen from the parent.
        var parent = pose.ModelTransform(pose.Parent(ArmBone)).Basis.GetRotationQuaternion();
        pose.AddRotation(ArmBone, parent.Inverse() * new Quaternion(axis.Normalized(), swing) * parent);
        return swing;
    }
}
