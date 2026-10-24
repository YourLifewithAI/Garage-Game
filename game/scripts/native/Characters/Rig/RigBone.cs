using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// One bone of a character's rig. Bone 0 is the character's root (<c>character_root</c>, at root_pivot_m on the
/// ground); bone <c>i + 1</c> is part <c>i</c> of character.json, named and parented as the part.
/// </summary>
public sealed class RigBone
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    /// <summary>The parent bone, or -1 for the root.</summary>
    public required int Parent { get; init; }
    /// <summary>The part's index in <see cref="CharacterDefinition.Parts"/>, or -1 for the root bone.</summary>
    public required int Part { get; init; }
    public required PartRole Role { get; init; }
    public required BodySide Side { get; init; }
    public required PartKind Kind { get; init; }
    public required EffectMotion Effect { get; init; }
    /// <summary>The converter's bubble material hint (see-through, a shimmering rim); null for an opaque part.</summary>
    public BubbleHint? Bubble { get; init; }
    /// <summary>Décor, or marked rigid in rig.json: it rides its parent, every pose write to it is ignored, no chain.</summary>
    public required bool IsRigid { get; init; }
    /// <summary>The rest offset from the parent bone's joint (local_pivot_m; root_pivot_m for the root).</summary>
    public required Vector3 RestLocal { get; init; }
    /// <summary>The joint at rest, in the character's space (pivot_m).</summary>
    public required Vector3 RestModel { get; init; }
    /// <summary>The bone's own mesh bounds relative to its joint; empty when the part has no mesh.</summary>
    public required Aabb MeshBounds { get; init; }
    public required bool HasMesh { get; init; }
    public required int Depth { get; init; }
    public IReadOnlyList<int> Children { get; internal set; } = System.Array.Empty<int>();

    /// <summary>Distance from the joint to the farthest corner of the bone's own mesh (0 without a mesh).</summary>
    public float ReachM
    {
        get
        {
            if (!HasMesh) return 0;
            var low = MeshBounds.Position;
            var high = MeshBounds.End;
            var far = new Vector3(Mathf.Max(Mathf.Abs(low.X), Mathf.Abs(high.X)), Mathf.Max(Mathf.Abs(low.Y), Mathf.Abs(high.Y)), Mathf.Max(Mathf.Abs(low.Z), Mathf.Abs(high.Z)));
            return far.Length();
        }
    }

    public override string ToString() => $"{Name} ({PartRoles.Token(Role)}, {Side})";
}
