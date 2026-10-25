using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Look;

namespace GarageGame.Native.Room;

/// <summary>
/// The island's wall ivy in the scene (MECHANICS.md decision 107, Grow's ivy): each strip a band of leaves 0.4 m up a wall's face from its
/// foot and 0.1 m wide, drawn just off the face and never collided (the wall itself is what the body grabs, so ivy moves no one). What
/// it changes is the climb's price: the body asks ClimbFactorAt while it climbs, and a climber on a strip pays the strip's share of the
/// bar (a quarter, provisional), the full price again off it or once it has burnt. Nothing here is authoritative: the command host
/// hands the view the strips its nature keeps and the share (SetIvy), and it builds them again only when they change. Lives under the
/// room node beside the plant copies.
/// </summary>
public partial class IvyView : Node3D
{
    public const string ViewName = "Ivy";
    /// <summary>A strip's length up the face and its width (Kernel.GrowKindLaws.IvyLengthM and IvyWidthM), drawn this far off the face.</summary>
    public const float LengthM = 0.4f, WidthM = 0.1f, OffFaceM = 0.004f;
    private static readonly Color LeafColor = new("3f7a34");
    private static readonly Color ScorchColor = new("2f2a22");
    /// <summary>The views in the scene now: a climbing body asks those in its own world (none: no ivy anywhere, no cost to ask).</summary>
    private static readonly List<IvyView> Live = new();

    /// <summary>One strip: its id, its base on the face (at its foot's height), the face's outward normal and its burn.</summary>
    public readonly record struct Strip(string Id, Vector3 BaseM, Vector3 NormalM, float Burn);

    private List<Strip> _strips = new();
    private float _factor = 1f;

    /// <summary>How many strips the view holds now, and the share of the bar a climb on one pays (the tests read them).</summary>
    public int Count => _strips.Count;
    public float Factor => _factor;

    public static IvyView Create() => new() { Name = ViewName };

    public override void _EnterTree() => Live.Add(this);
    public override void _ExitTree() => Live.Remove(this);

    /// <summary>The strips as the host's nature keeps them and the share a climb on one pays; rebuilt only on a change. Returns whether anything changed.</summary>
    public bool SetIvy(IReadOnlyList<Strip> strips, float factor)
    {
        var changed = !strips.SequenceEqual(_strips) || factor != _factor;
        _factor = Math.Clamp(factor, 0, 1);
        if (!changed) return false;
        _strips = strips.ToList();
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        foreach (var strip in _strips)
        {
            var (up, across) = Frame(strip.NormalM);
            var centre = strip.BaseM + up * (LengthM * 0.5f) + strip.NormalM * OffFaceM;
            var color = LeafColor.Lerp(ScorchColor, Math.Clamp(strip.Burn, 0, 1));
            AddChild(new MeshInstance3D
            {
                Name = strip.Id.Replace(':', '_'), Mesh = new QuadMesh { Size = new Vector2(WidthM, LengthM) }, MaterialOverride = MaterialLibrary.For("foliage", color),
                Transform = new Transform3D(new Basis(across, up, strip.NormalM), centre), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        return true;
    }

    /// <summary>A face's frame: up along it (the world's up laid on the face) and across it, for a strip whose normal faces out.</summary>
    public static (Vector3 Up, Vector3 Across) Frame(Vector3 normal)
    {
        var up = (Vector3.Up - normal * normal.Y).Normalized();
        return (up, up.Cross(normal).Normalized());
    }

    /// <summary>
    /// The share of the bar a climb pays here: a strip's (this view's factor) when the climbing body's centre lies on one (within its
    /// length and width, near the face, a body's radius of slack each way), else 1. Asks only the views in the body's own world.
    /// </summary>
    public static float ClimbFactorAt(Node3D body, Vector3 feet, float bodyHeightM, float bodyRadiusM)
    {
        if (Live.Count == 0) return 1f;
        var world = body.GetWorld3D();
        var factor = 1f;
        var centre = feet + Vector3.Up * (bodyHeightM * 0.5f);
        foreach (var view in Live)
        {
            if (!view.IsInsideTree() || view.GetWorld3D() != world) continue;
            foreach (var strip in view._strips)
            {
                var (up, across) = Frame(strip.NormalM);
                var offset = centre - strip.BaseM;
                var along = offset.Dot(up);
                var side = offset.Dot(across);
                var out_ = offset.Dot(strip.NormalM);
                if (along >= -bodyRadiusM && along <= LengthM + bodyRadiusM && Math.Abs(side) <= WidthM * 0.5f + bodyRadiusM && out_ >= -bodyRadiusM && out_ <= bodyRadiusM * 2 + 0.06f)
                    factor = Math.Min(factor, view._factor);
            }
        }
        return factor;
    }
}
