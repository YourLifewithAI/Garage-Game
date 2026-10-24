using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>A folder the library refused, or a sidecar it could not accept. The message is the reason, for people.</summary>
public sealed class CharacterLoadException : Exception
{
    public CharacterLoadException(string reason) : base(reason) { }
}

/// <summary>A see-through part (the converter's <c>bubble</c> material hint).</summary>
public sealed record BubbleHint(Color AuraColour);

/// <summary>One part of a character, as character.json describes it and the GLB carries it.</summary>
public sealed class PartDefinition
{
    /// <summary>Position in character.json's part list; parents always precede their children.</summary>
    public required int Index { get; init; }
    public required string Name { get; init; }
    /// <summary>character.json's descriptive role text (free prose; never parsed for behaviour).</summary>
    public required string RoleText { get; init; }
    /// <summary>Index of the parent part, or -1 for a part hanging from the character's root.</summary>
    public required int Parent { get; init; }
    public required int NodeIndex { get; init; }
    /// <summary>The joint in the character's space, metres (character.json's <c>pivot_m</c>).</summary>
    public required Vector3 PivotM { get; init; }
    /// <summary>The joint relative to the parent's joint (<c>local_pivot_m</c>), the bone's rest offset.</summary>
    public required Vector3 LocalPivotM { get; init; }
    public required PartKind Kind { get; init; }
    public required EffectMotion Effect { get; init; }
    public BubbleHint? Bubble { get; init; }
    /// <summary>The part's mesh, shared by every body built from this definition; null for a joint-only part.</summary>
    public Mesh? Mesh { get; internal set; }
    /// <summary>The mesh's bounds relative to the joint (empty for a joint-only part).</summary>
    public Aabb MeshBounds { get; internal set; }
    public int Triangles { get; internal set; }
}

/// <summary>
/// A validated character folder: character.json, the GLB's meshes and the optional rig.json sidecar. Immutable once
/// loaded; <see cref="CharacterRig.Build(CharacterDefinition)"/> makes any number of bodies from it.
/// </summary>
public sealed class CharacterDefinition
{
    /// <summary>The folder name, a token (<c>[a-z][a-z0-9_]{0,63}</c>).</summary>
    public required string Id { get; init; }
    /// <summary>The character's display name from character.json (untrusted text: show it, never act on it).</summary>
    public required string DisplayName { get; init; }
    /// <summary>The folder it was read from (res://, user:// or an absolute path).</summary>
    public required string Folder { get; init; }
    public required MotionKind Motion { get; init; }
    public required Vector3 RootPivotM { get; init; }
    public required float HeightM { get; init; }
    public required float WidthM { get; init; }
    public required float DepthM { get; init; }
    public required Aabb BoundsM { get; init; }
    public required int Triangles { get; init; }
    public required IReadOnlyList<PartDefinition> Parts { get; init; }
    /// <summary>SHA-256 of the GLB bytes the meshes were built from (lowercase hex).</summary>
    public required string GlbSha256 { get; init; }
    /// <summary>The validated rig.json, or <see cref="RigSidecar.Empty"/> when the folder has none.</summary>
    public required RigSidecar Sidecar { get; init; }
    public bool HasSidecar => !ReferenceEquals(Sidecar, RigSidecar.Empty);

    public int FindPart(string name)
    {
        for (var i = 0; i < Parts.Count; i++)
            if (string.Equals(Parts[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
