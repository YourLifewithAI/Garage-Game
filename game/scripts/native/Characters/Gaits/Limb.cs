using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// A limb a gait swings itself (an arm with its hand, a leg with its foot), with the axes that move its tip back (+Z) and
/// up (+Y) from wherever it points at rest: an arm drawn hanging down, held out or raised overhead swings the same way.
/// Absent (<see cref="Bone"/> -1) when the rig has no such limb, or a spring chain or rig.json owns it.
/// </summary>
public readonly struct Limb
{
    public readonly int Bone;
    public readonly int Tip;
    public readonly float Side;
    /// <summary>Turning about this (parent's frame) moves the tip toward +Z, back.</summary>
    public readonly Vector3 BackAxis;
    /// <summary>Turning about this (parent's frame) moves the tip toward +Y, up.</summary>
    public readonly Vector3 UpAxis;
    /// <summary>The joint-to-tip direction at rest (unit).</summary>
    public readonly Vector3 Direction;

    public bool Exists => Bone >= 0;

    public Limb(GaitRig rig, int bone, int tip, float side)
    {
        Side = side;
        Tip = tip >= 0 && rig.Swingable(tip) ? tip : -1;
        if (!rig.Swingable(bone))
        {
            Bone = -1;
            BackAxis = Vector3.Right;
            UpAxis = Vector3.Back;
            Direction = Vector3.Down;
            return;
        }
        Bone = bone;
        var bones = rig.Body.Bones;
        var joint = bones[bone];
        var direction = tip >= 0 ? bones[tip].RestModel - joint.RestModel : Vector3.Zero;
        if (direction.LengthSquared() < 1e-8f && joint.HasMesh) direction = joint.MeshBounds.GetCenter();
        if (direction.LengthSquared() < 1e-8f) direction = new Vector3(side * 0.5f, -1, 0);
        Direction = direction.Normalized();
        var back = Direction.Cross(Vector3.Back);
        BackAxis = back.LengthSquared() > 1e-6f ? back.Normalized() : Vector3.Right;
        var up = Direction.Cross(Vector3.Up);
        UpAxis = up.LengthSquared() > 1e-6f ? up.Normalized() : (side >= 0 ? Vector3.Back : Vector3.Forward);
    }

    /// <summary>Swings the limb back (positive) or forward and raises it (positive) or lowers it, about its joint.</summary>
    public void Swing(PoseBuffer pose, float back, float up)
    {
        if (Bone < 0) return;
        if (back != 0) pose.AddRotation(Bone, new Quaternion(BackAxis, back));
        if (up != 0) pose.AddRotation(Bone, new Quaternion(UpAxis, up));
    }

    /// <summary>Bends the tip (a hand, a paw) back by <paramref name="back"/> radians on its own joint.</summary>
    public void Bend(PoseBuffer pose, float back)
    {
        if (Tip < 0 || back == 0) return;
        pose.AddRotation(Tip, new Quaternion(BackAxis, back));
    }
}
