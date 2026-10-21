using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// A drawn character's body, built by <see cref="CharacterRig.Build(CharacterDefinition)"/>: a <see cref="Skeleton3D"/>
/// with the root bone and one bone per part, each part's mesh riding its bone through a <see cref="BoneAttachment3D"/>,
/// the spring chains, the look-ats and the leg IK, plus the measured <see cref="MotionProfile"/> and the
/// <see cref="PoseBuffer"/> the pose layers write. Feet at y=0, 0.10 m tall, facing -Z, exactly as the GLB.
/// The rig never moves the capsule; the body is a visual, placed under the controller like the prototype figure.
/// See docs/characters/RIG-API.md.
/// </summary>
public partial class CharacterBody : Node3D
{
    /// <summary>The root bone (<c>character_root</c>): on the ground at root_pivot_m. Squash and stretch here keep the feet put.</summary>
    public const int RootBone = 0;

    private RigBone[] _bones = Array.Empty<RigBone>();
    private BoneAttachment3D[] _attachments = Array.Empty<BoneAttachment3D>();
    private MeshInstance3D?[] _meshes = Array.Empty<MeshInstance3D?>();
    private LegIk[] _legs = Array.Empty<LegIk>();
    private string[] _notes = Array.Empty<string>();

    public CharacterDefinition Definition { get; private set; } = null!;
    public string CharacterId => Definition.Id;
    public Skeleton3D Skeleton { get; private set; } = null!;
    public IReadOnlyList<RigBone> Bones => _bones;
    public int BoneCount => _bones.Length;
    /// <summary>The body part's bone (the first part with the body role; the first part when none has it).</summary>
    public int BodyBone { get; private set; }
    /// <summary>The head's bone, or <see cref="BodyBone"/> when there is no head (the body is the head).</summary>
    public int HeadBone { get; private set; }
    public MotionProfile Profile { get; private set; } = null!;
    /// <summary>This frame's pose, written by the pose layers and applied by <see cref="ApplyPose"/>.</summary>
    public PoseBuffer Pose { get; private set; } = null!;
    public SecondaryMotion Secondary { get; private set; } = SecondaryMotion.None;
    public LookRig Look { get; private set; } = null!;
    /// <summary>One entry per leg-and-foot chain (at most 2), all on one <see cref="TwoBoneIK3D"/>, inactive until contact asks.</summary>
    public IReadOnlyList<LegIk> Legs => _legs;
    /// <summary>The legs' solver, or null for a character without leg-and-foot chains.</summary>
    public TwoBoneIK3D? LegSolver { get; private set; }
    /// <summary>What the inference chose not to do (a budget spent, a chain removed by rig.json), for logs and tests.</summary>
    public IReadOnlyList<string> Notes => _notes;
    /// <summary>The meshes' bounds at rest, in the body's space.</summary>
    public Aabb RestBounds { get; private set; }

    internal void Initialize(CharacterDefinition definition, Skeleton3D skeleton, RigBone[] bones, BoneAttachment3D[] attachments,
        MeshInstance3D?[] meshes, int bodyBone, int headBone, MotionProfile profile, PoseBuffer pose, SecondaryMotion secondary,
        LookRig look, TwoBoneIK3D? legSolver, LegIk[] legs, string[] notes, Aabb restBounds)
    {
        Definition = definition;
        Skeleton = skeleton;
        _bones = bones;
        _attachments = attachments;
        _meshes = meshes;
        BodyBone = bodyBone;
        HeadBone = headBone;
        Profile = profile;
        Pose = pose;
        Secondary = secondary;
        Look = look;
        LegSolver = legSolver;
        _legs = legs;
        _notes = notes;
        RestBounds = restBounds;
    }

    /// <summary>The bone named <paramref name="name"/> (a part name, or <c>character_root</c>), or -1.</summary>
    public int FindBone(string name)
    {
        for (var i = 0; i < _bones.Length; i++)
            if (string.Equals(_bones[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// The <paramref name="nth"/> bone (in part order) with <paramref name="role"/> on <paramref name="side"/> (any side
    /// when null), or -1. <c>FindBone(PartRole.Leg, BodySide.Right)</c> is the character's right leg.
    /// </summary>
    public int FindBone(PartRole role, BodySide? side = null, int nth = 0)
    {
        for (var i = 1; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            if (bone.Role != role || (side.HasValue && bone.Side != side.Value)) continue;
            if (nth-- == 0) return i;
        }
        return -1;
    }

    public int CountBones(PartRole role, BodySide? side = null)
    {
        var count = 0;
        for (var i = 1; i < _bones.Length; i++)
            if (_bones[i].Role == role && (!side.HasValue || _bones[i].Side == side.Value)) count++;
        return count;
    }

    /// <summary>Writes the bones with <paramref name="role"/> into <paramref name="into"/> (part order); returns how many there are.</summary>
    public int GetBones(PartRole role, Span<int> into, BodySide? side = null)
    {
        var count = 0;
        for (var i = 1; i < _bones.Length; i++)
        {
            if (_bones[i].Role != role || (side.HasValue && _bones[i].Side != side.Value)) continue;
            if (count < into.Length) into[count] = i;
            count++;
        }
        return count;
    }

    /// <summary>The attachment that carries the bone's mesh; its transform is the bone's final pose (after the modifiers) as of the last skeleton update.</summary>
    public BoneAttachment3D Attachment(int bone) => _attachments[bone];

    /// <summary>The part's mesh, or null for a joint-only part and the root.</summary>
    public MeshInstance3D? PartMesh(int bone) => _meshes[bone];

    /// <summary>
    /// Plants the feet with the leg IK (contact's switch): the IK solves the legs toward <see cref="LegIk.Target"/> and
    /// the dangling legs' springs go quiet; off again, the legs hang free on their springs. A no-op without legs.
    /// </summary>
    public void UseLegIk(bool on)
    {
        if (LegSolver != null) LegSolver.Active = on;
        Secondary.SetLegSpringsActive(!on);
    }

    /// <summary>Hands <see cref="Pose"/> to the skeleton. The skeleton's modifiers run on its next update.</summary>
    public void ApplyPose() => Pose.ApplyTo(Skeleton);

    /// <summary>The bone's final transform in the body's space (after springs, look-at and IK), as of the last skeleton update.</summary>
    public Transform3D FinalBoneTransform(int bone) => Skeleton.Transform * _attachments[bone].Transform;

    /// <summary>A deterministic hash of every bone's final transform (see <see cref="PoseHash"/>).</summary>
    public ulong FinalPoseHash()
    {
        var hash = PoseHash.Seed;
        foreach (var attachment in _attachments) PoseHash.Mix(ref hash, attachment.Transform);
        return hash;
    }

    /// <summary>The lowest mesh point under <paramref name="pose"/> (the body's own when null), in the body's space, before the modifiers.</summary>
    public float LowestPointY(PoseBuffer? pose = null)
    {
        pose ??= Pose;
        var lowest = float.MaxValue;
        for (var i = 0; i < _bones.Length; i++)
        {
            if (!_bones[i].HasMesh) continue;
            lowest = Mathf.Min(lowest, LowestCorner(Skeleton.Transform * pose.ModelTransform(i), _bones[i].MeshBounds));
        }
        return lowest == float.MaxValue ? 0 : lowest;
    }

    /// <summary>The lowest mesh point after the modifiers, from the attachments, in the body's space.</summary>
    public float FinalLowestPointY()
    {
        var lowest = float.MaxValue;
        for (var i = 0; i < _bones.Length; i++)
        {
            if (!_bones[i].HasMesh) continue;
            lowest = Mathf.Min(lowest, LowestCorner(FinalBoneTransform(i), _bones[i].MeshBounds));
        }
        return lowest == float.MaxValue ? 0 : lowest;
    }

    private static float LowestCorner(Transform3D transform, Aabb bounds)
    {
        var lowest = float.MaxValue;
        for (var corner = 0; corner < 8; corner++)
        {
            var point = bounds.Position + new Vector3((corner & 1) != 0 ? bounds.Size.X : 0, (corner & 2) != 0 ? bounds.Size.Y : 0, (corner & 4) != 0 ? bounds.Size.Z : 0);
            lowest = Mathf.Min(lowest, (transform * point).Y);
        }
        return lowest;
    }
}
