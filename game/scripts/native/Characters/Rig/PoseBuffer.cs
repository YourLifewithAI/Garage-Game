using System;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// One frame's pose: a bone-local position, rotation and scale per bone, keyed by bone index, exactly like
/// <see cref="Skeleton3D"/>'s bone poses (relative to the parent bone; rests are translations only, so a rotation
/// turns a part about its own joint with the character's axes at rest). <see cref="Reset"/> returns every bone to
/// rest; the pose layers then set or add to the bones they own, in the fixed <see cref="PoseLayer"/> order;
/// <see cref="ApplyTo"/> hands the result to the skeleton, whose own modifiers (springs, look-at, IK) run after it.
/// Allocation-free after construction. Rigid bones (décor, or marked rigid in rig.json) ignore every write.
/// </summary>
public sealed class PoseBuffer
{
    private readonly int[] _parent;
    private readonly Vector3[] _rest;
    private readonly bool[] _rigid;
    private readonly Vector3[] _position;
    private readonly Quaternion[] _rotation;
    private readonly Vector3[] _scale;
    private readonly Vector3[] _appliedPosition;
    private readonly Quaternion[] _appliedRotation;
    private readonly Vector3[] _appliedScale;
    private readonly Transform3D[] _model;
    private bool _applied;
    private bool _modelValid;

    internal PoseBuffer(int[] parents, Vector3[] restPositions, bool[] rigid)
    {
        if (parents.Length != restPositions.Length || parents.Length != rigid.Length) throw new ArgumentException("bone arrays differ in length");
        for (var i = 0; i < parents.Length; i++)
            if (parents[i] >= i) throw new ArgumentException("a bone's parent must come before it");
        BoneCount = parents.Length;
        _parent = (int[])parents.Clone();
        _rest = (Vector3[])restPositions.Clone();
        _rigid = (bool[])rigid.Clone();
        _position = new Vector3[BoneCount];
        _rotation = new Quaternion[BoneCount];
        _scale = new Vector3[BoneCount];
        _appliedPosition = new Vector3[BoneCount];
        _appliedRotation = new Quaternion[BoneCount];
        _appliedScale = new Vector3[BoneCount];
        _model = new Transform3D[BoneCount];
        Reset();
    }

    public int BoneCount { get; }
    /// <summary>The layer now writing; layers must begin in non-decreasing order after <see cref="Reset"/>.</summary>
    public PoseLayer Layer { get; private set; }
    /// <summary>Writes refused because a value was NaN or infinite (the pose kept its previous value). Tests expect 0.</summary>
    public int NonFiniteWrites { get; private set; }
    /// <summary>Writes ignored because the bone is rigid. Not an error: rig.json may lock a part a gait would move.</summary>
    public int RigidWrites { get; private set; }

    public int Parent(int bone) => _parent[bone];
    public bool IsRigid(int bone) => _rigid[bone];
    /// <summary>The bone's rest offset from its parent's joint (character.json's local_pivot_m; the root's is root_pivot_m).</summary>
    public Vector3 RestPosition(int bone) => _rest[bone];

    /// <summary>Every bone back to rest; the next writer must call <see cref="Begin"/>.</summary>
    public void Reset()
    {
        Array.Copy(_rest, _position, BoneCount);
        Array.Fill(_rotation, Quaternion.Identity);
        Array.Fill(_scale, Vector3.One);
        Layer = PoseLayer.Rest;
        _modelValid = false;
    }

    /// <summary>Marks the start of a layer. Throws if a layer runs before one it must follow.</summary>
    public void Begin(PoseLayer layer)
    {
        if (layer < Layer) throw new InvalidOperationException($"pose layer {layer} cannot run after {Layer}; the order is fixed");
        Layer = layer;
    }

    public Vector3 GetPosition(int bone) => _position[bone];
    public Quaternion GetRotation(int bone) => _rotation[bone];
    public Vector3 GetScale(int bone) => _scale[bone];
    /// <summary>The bone's local transform as Skeleton3D composes it: turned, then scaled in its own frame (rotation x scale).</summary>
    public Transform3D GetLocal(int bone) => new(new Basis(_rotation[bone]).ScaledLocal(_scale[bone]), _position[bone]);

    /// <summary>Sets the bone's position relative to its parent's joint (rest is <see cref="RestPosition"/>).</summary>
    public void SetPosition(int bone, Vector3 position)
    {
        if (!Writable(bone, Finite(position))) return;
        _position[bone] = position;
        _modelValid = false;
    }

    /// <summary>Moves the bone by <paramref name="delta"/> in its parent's frame.</summary>
    public void AddPosition(int bone, Vector3 delta)
    {
        if (!Writable(bone, Finite(delta))) return;
        _position[bone] += delta;
        _modelValid = false;
    }

    /// <summary>Sets the bone's rotation relative to its parent (normalised; identity is rest).</summary>
    public void SetRotation(int bone, Quaternion rotation)
    {
        var length = rotation.Length();
        if (!Writable(bone, Finite(rotation) && length > 1e-6f)) return;
        _rotation[bone] = rotation / length;
        _modelValid = false;
    }

    /// <summary>Turns the bone by <paramref name="delta"/> about its joint, in its parent's frame: rotation = delta * rotation.</summary>
    public void AddRotation(int bone, Quaternion delta)
    {
        var length = delta.Length();
        if (!Writable(bone, Finite(delta) && length > 1e-6f)) return;
        _rotation[bone] = ((delta / length) * _rotation[bone]).Normalized();
        _modelValid = false;
    }

    public void SetScale(int bone, Vector3 scale)
    {
        if (!Writable(bone, Finite(scale) && scale.X > 0 && scale.Y > 0 && scale.Z > 0)) return;
        _scale[bone] = scale;
        _modelValid = false;
    }

    /// <summary>Multiplies the bone's scale component-wise (squash and stretch: keep X·Y·Z = 1 to keep volume).</summary>
    public void MultiplyScale(int bone, Vector3 factor)
    {
        if (!Writable(bone, Finite(factor) && factor.X > 0 && factor.Y > 0 && factor.Z > 0)) return;
        _scale[bone] *= factor;
        _modelValid = false;
    }

    /// <summary>Copies every bone from <paramref name="other"/> (same rig), keeping this buffer's layer.</summary>
    public void CopyFrom(PoseBuffer other)
    {
        if (other.BoneCount != BoneCount) throw new ArgumentException("pose buffers belong to different rigs");
        Array.Copy(other._position, _position, BoneCount);
        Array.Copy(other._rotation, _rotation, BoneCount);
        Array.Copy(other._scale, _scale, BoneCount);
        _modelValid = false;
    }

    /// <summary>Moves every non-rigid bone toward <paramref name="target"/> by <paramref name="weight"/> (0 keeps this pose, 1 copies the target).</summary>
    public void BlendToward(PoseBuffer target, float weight)
    {
        if (target.BoneCount != BoneCount) throw new ArgumentException("pose buffers belong to different rigs");
        if (!float.IsFinite(weight)) { NonFiniteWrites++; return; }
        weight = Mathf.Clamp(weight, 0, 1);
        for (var i = 0; i < BoneCount; i++)
        {
            if (_rigid[i]) continue;
            _position[i] = _position[i].Lerp(target._position[i], weight);
            _rotation[i] = _rotation[i].Slerp(target._rotation[i], weight).Normalized();
            _scale[i] = _scale[i].Lerp(target._scale[i], weight);
        }
        _modelValid = false;
    }

    /// <summary>The bone's transform in the character's (skeleton's) space under this pose, before the skeleton's modifiers.</summary>
    public Transform3D ModelTransform(int bone)
    {
        if (!_modelValid)
        {
            for (var i = 0; i < BoneCount; i++)
            {
                var local = GetLocal(i);
                _model[i] = _parent[i] < 0 ? local : _model[_parent[i]] * local;
            }
            _modelValid = true;
        }
        return _model[bone];
    }

    /// <summary>Hands the pose to the skeleton, touching only bones that changed since the last call.</summary>
    public void ApplyTo(Skeleton3D skeleton)
    {
        for (var i = 0; i < BoneCount; i++)
        {
            if (!_applied || _position[i] != _appliedPosition[i]) { skeleton.SetBonePosePosition(i, _position[i]); _appliedPosition[i] = _position[i]; }
            if (!_applied || _rotation[i] != _appliedRotation[i]) { skeleton.SetBonePoseRotation(i, _rotation[i]); _appliedRotation[i] = _rotation[i]; }
            if (!_applied || _scale[i] != _appliedScale[i]) { skeleton.SetBonePoseScale(i, _scale[i]); _appliedScale[i] = _scale[i]; }
        }
        _applied = true;
    }

    /// <summary>
    /// A deterministic 64-bit hash of the pose (FNV-1a over every value rounded to 1e-7, rotations sign-canonical):
    /// the same layers fed the same frames give the same hash, run after run.
    /// </summary>
    public ulong Hash()
    {
        var hash = PoseHash.Seed;
        for (var i = 0; i < BoneCount; i++)
        {
            PoseHash.Mix(ref hash, _position[i]);
            PoseHash.Mix(ref hash, _rotation[i]);
            PoseHash.Mix(ref hash, _scale[i]);
        }
        return hash;
    }

    private bool Writable(int bone, bool finite)
    {
        if ((uint)bone >= (uint)BoneCount) throw new ArgumentOutOfRangeException(nameof(bone), bone, "no such bone");
        if (!finite) { NonFiniteWrites++; return false; }
        if (_rigid[bone]) { RigidWrites++; return false; }
        return true;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}

/// <summary>The hash behind <see cref="PoseBuffer.Hash"/> and <see cref="CharacterBody.FinalPoseHash"/>.</summary>
public static class PoseHash
{
    public const ulong Seed = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
    private const double Quantum = 1e7;

    public static void Mix(ref ulong hash, float value)
    {
        var quantised = (long)Math.Round(value * Quantum);
        for (var b = 0; b < 8; b++)
        {
            hash ^= (byte)(quantised >> (8 * b));
            hash *= Prime;
        }
    }

    public static void Mix(ref ulong hash, Vector3 value)
    {
        Mix(ref hash, value.X);
        Mix(ref hash, value.Y);
        Mix(ref hash, value.Z);
    }

    /// <summary>Rotations q and -q are the same turn; hash the one with W ≥ 0.</summary>
    public static void Mix(ref ulong hash, Quaternion value)
    {
        if (value.W < 0 || (value.W == 0 && (value.X < 0 || (value.X == 0 && (value.Y < 0 || (value.Y == 0 && value.Z < 0))))))
            value = -value;
        Mix(ref hash, value.X);
        Mix(ref hash, value.Y);
        Mix(ref hash, value.Z);
        Mix(ref hash, value.W);
    }

    public static void Mix(ref ulong hash, Transform3D value)
    {
        Mix(ref hash, value.Basis.X);
        Mix(ref hash, value.Basis.Y);
        Mix(ref hash, value.Basis.Z);
        Mix(ref hash, value.Origin);
    }
}
