using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Room;

/// <summary>
/// The island's sprouts in the scene (MECHANICS.md decision 104, Grow's seedless growth): each a copy of one of the island's grass or
/// flower copies, drawn with that set's finest mesh (the plant copies' view lends it, PlantCopiesView.MeshFor) at the sprout's root,
/// turn and size (a seedling at half its natural size, growing through its phases), in the copy's tint, darkened as it burns. One
/// MultiMesh per set, built again only when the sprouts change. No collision: grass and flowers have none, so a sprout never moves an
/// avatar. Nothing here is authoritative: the command host hands the view the sprouts its nature keeps (SetSprouts). Lives under the
/// room node beside the plant copies.
/// </summary>
public partial class SproutView : Node3D
{
    public const string ViewName = "Sprouts";
    private static readonly Color ScorchColor = new("2f2a22");

    /// <summary>One sprout as drawn: the set it copies, its place (root, turn and size), its tint and its burn (0 to 1).</summary>
    public readonly record struct Shown(string Set, Transform3D Place, Color Tint, float Burn);

    private List<Shown> _shown = new();

    /// <summary>How many sprouts the view draws now, and their places (the tests read them).</summary>
    public int Count => _shown.Count;
    public IReadOnlyList<Shown> Drawn => _shown;

    public static SproutView Create() => new() { Name = ViewName };

    /// <summary>
    /// The sprouts as the host's nature keeps them, each set's with the mesh meshOf gives (a set it has no mesh for is not drawn).
    /// Rebuilt only on a change; returns whether anything changed.
    /// </summary>
    public bool SetSprouts(IReadOnlyList<Shown> sprouts, Func<string, Mesh?> meshOf)
    {
        if (sprouts.SequenceEqual(_shown)) return false;
        _shown = sprouts.ToList();
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        foreach (var group in _shown.GroupBy(s => s.Set, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (meshOf(group.Key) is not { } mesh) continue;
            var list = group.ToList();
            var multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = list.Count };
            for (var i = 0; i < list.Count; i++)
            {
                multimesh.SetInstanceTransform(i, list[i].Place);
                multimesh.SetInstanceColor(i, list[i].Tint.Lerp(ScorchColor, Math.Clamp(list[i].Burn, 0, 1)));
            }
            AddChild(new MultiMeshInstance3D { Name = group.Key, Multimesh = multimesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        return true;
    }
}
