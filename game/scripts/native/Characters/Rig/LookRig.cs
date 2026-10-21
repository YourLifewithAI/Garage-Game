using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>One look-at: a <see cref="LookAtModifier3D"/> turning one bone toward <see cref="LookRig.Target"/>.</summary>
public sealed class LookAt
{
    public required LookAtModifier3D Modifier { get; init; }
    public required int Bone { get; init; }
    /// <summary>The yaw (primary, about +Y) and pitch (secondary) limits, radians, symmetric about rest.</summary>
    public required float YawLimitRad { get; init; }
    public required float PitchLimitRad { get; init; }
}

/// <summary>
/// The face's look-ats: the head (the body when there is no head) and up to two eyes, all aimed at one
/// <see cref="Target"/> node. Built inactive so they never fight the gait; the life and contact layers move the
/// target, then set <see cref="SkeletonModifier3D.Active"/> and <see cref="SkeletonModifier3D.Influence"/>.
/// </summary>
public sealed class LookRig
{
    public const int MaxLookAts = 3;
    public static readonly float HeadYawLimit = Mathf.DegToRad(50), HeadPitchLimit = Mathf.DegToRad(30);
    public static readonly float BodyYawLimit = Mathf.DegToRad(20), BodyPitchLimit = Mathf.DegToRad(10);
    public static readonly float EyeYawLimit = Mathf.DegToRad(25), EyePitchLimit = Mathf.DegToRad(15);

    private readonly List<LookAt> _eyes = new();

    /// <summary>Where the character looks: a child of the body, 1 m straight ahead at eye height until a layer moves it.</summary>
    public required Node3D Target { get; init; }
    /// <summary>The head's look-at (on the body when there is no head); null for a character with neither.</summary>
    public LookAt? Head { get; internal set; }
    public bool HeadIsBody { get; internal set; }
    public IReadOnlyList<LookAt> Eyes => _eyes;
    public int Count => (Head != null ? 1 : 0) + _eyes.Count;

    /// <summary>Turns every look-at on or off, with one influence (0 to 1).</summary>
    public void SetActive(bool active, float influence = 1)
    {
        influence = float.IsFinite(influence) ? Mathf.Clamp(influence, 0, 1) : 0;
        if (Head != null) { Head.Modifier.Active = active; Head.Modifier.Influence = influence; }
        foreach (var eye in _eyes) { eye.Modifier.Active = active; eye.Modifier.Influence = influence; }
    }

    internal void AddEye(LookAt eye) => _eyes.Add(eye);

    internal static LookAt Make(string name, RigBone bone, float yaw, float pitch, string targetPath)
    {
        var modifier = new LookAtModifier3D
        {
            Name = name,
            BoneName = bone.Name,
            ForwardAxis = SkeletonModifier3D.BoneAxis.MinusZ,
            PrimaryRotationAxis = Vector3.Axis.Y,
            UseSecondaryRotation = true,
            Relative = true,
            OriginFrom = LookAtModifier3D.OriginFromEnum.Self,
            UseAngleLimitation = true,
            SymmetryLimitation = true,
            // The modifier's limit angle is the whole symmetric range (measured: 50° allows 25° each way), so double it.
            PrimaryLimitAngle = 2 * yaw,
            SecondaryLimitAngle = 2 * pitch,
            PrimaryDampThreshold = 0.8f,
            SecondaryDampThreshold = 0.8f,
            Duration = 0.15f,
            TargetNode = new NodePath(targetPath),
            Active = false,
        };
        return new LookAt { Modifier = modifier, Bone = bone.Index, YawLimitRad = yaw, PitchLimitRad = pitch };
    }
}

/// <summary>One leg's two-bone IK: the hip (leg bone) and the ankle (foot bone), reaching the sole to <see cref="Target"/>.</summary>
public sealed class LegIk
{
    public required TwoBoneIK3D Solver { get; init; }
    public required int Setting { get; init; }
    public required int HipBone { get; init; }
    public required int FootBone { get; init; }
    public required BodySide Side { get; init; }
    /// <summary>Where the sole should be: a child of the body, at the sole's rest position until the contact layer moves it.</summary>
    public required Node3D Target { get; init; }
    /// <summary>The bend's direction: in front of the leg.</summary>
    public required Node3D Pole { get; init; }
    /// <summary>The sole's offset from the foot's joint (the virtual end bone), in the foot bone's space.</summary>
    public required Vector3 SoleOffset { get; init; }
    /// <summary>The sole at rest, in the character's space.</summary>
    public required Vector3 RestSole { get; init; }
}
