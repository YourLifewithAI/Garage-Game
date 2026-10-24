using Godot;
using System;
using System.Collections.Generic;
using GarageGame.Native.Look;

namespace GarageGame.Native.Room;

/// <summary>
/// The island's waterpads in the scene (MECHANICS.md decision 75, Grow's Waterpad): each one large water lily leaf, floating at the
/// water's surface, drawn as a leaf with a notch in the plant copies' foliage material and collided as a one-sided disc facing up (a
/// ConcavePolygonShape3D without backface collision) on the world layer: solid from above, so the player steps onto it and stands on
/// it, while a swimmer under it is not pushed. Its collision names it (the entity id waterpad:n, the material leaf), so the nature
/// sampler finds leaf under a fire cast on it and it burns as a plant does. Nothing here is authoritative: the command host hands the
/// view the waterpads its nature keeps (SetWaterpads), and the view builds them again only when they change. Lives under the room
/// node beside the plant copies.
/// </summary>
public partial class WaterpadView : Node3D
{
    public const string ViewName = "Waterpads";
    /// <summary>A leaf's diameter: about three times the player's 10 cm height (provisional).</summary>
    public const float DiameterM = 0.3f;
    /// <summary>The leaf's top floats this far over the water's surface.</summary>
    public const float FloatM = 0.006f;
    /// <summary>The leaf's notch, a wedge cut toward its centre, in degrees (only the drawing has it; the collision is the whole disc).</summary>
    public const float NotchDeg = 28f;
    private const int Segments = 24;
    private static readonly Color LeafColor = new("4f8a3a");
    private static readonly Color ScorchColor = new("2f2a22");
    private string _shownKey = "";

    /// <summary>How many leaves the view holds now (the tests read it).</summary>
    public int Count { get; private set; }

    public static WaterpadView Create() => new() { Name = ViewName };

    /// <summary>The leaves as the host's nature keeps them: (id, the leaf's centre at its top, its burn 0 to 1). Rebuilt only on a change.</summary>
    public void SetWaterpads(IReadOnlyList<(string Id, Vector3 PositionM, float Burn)> pads)
    {
        var key = string.Join("|", System.Linq.Enumerable.Select(pads, p => FormattableString.Invariant($"{p.Id}@{p.PositionM.X:0.###},{p.PositionM.Y:0.###},{p.PositionM.Z:0.###}#{p.Burn:0.00}")));
        if (key == _shownKey) return;
        _shownKey = key;
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        Count = 0;
        foreach (var (id, at, burn) in pads)
        {
            var body = new StaticBody3D { Name = id.Replace(':', '_'), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = at };
            body.SetMeta(Kernel.KernelNames.EntityIdMeta, id);
            body.SetMeta(Kernel.KernelNames.MaterialRoleMeta, "leaf");
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = DiscFaces(DiameterM * 0.5f), BackfaceCollision = false } });
            var color = LeafColor.Lerp(ScorchColor, Math.Clamp(burn, 0, 1));
            body.AddChild(new MeshInstance3D { Name = "Leaf", Mesh = LeafMesh(DiameterM * 0.5f), MaterialOverride = MaterialLibrary.For("foliage", color) });
            AddChild(body);
            Count++;
        }
    }

    /// <summary>The disc's triangles, a fan facing up (clockwise seen from above, Godot's front faces), at the body's origin.</summary>
    public static Vector3[] DiscFaces(float radius)
    {
        var faces = new Vector3[Segments * 3];
        for (var i = 0; i < Segments; i++)
        {
            var a = Mathf.Tau * i / Segments;
            var b = Mathf.Tau * (i + 1) / Segments;
            faces[i * 3] = Vector3.Zero;
            faces[i * 3 + 1] = new Vector3(Mathf.Cos(a) * radius, 0, Mathf.Sin(a) * radius);
            faces[i * 3 + 2] = new Vector3(Mathf.Cos(b) * radius, 0, Mathf.Sin(b) * radius);
        }
        return faces;
    }

    /// <summary>A lily leaf: a thin disc with a wedge notch, its top at the origin, a little thickness under it so it reads from the side.</summary>
    private static ArrayMesh LeafMesh(float radius)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var notch = Mathf.DegToRad(NotchDeg);
        var under = new Vector3(0, -0.004f, 0);
        for (var i = 0; i < Segments; i++)
        {
            var a = notch * 0.5f + (Mathf.Tau - notch) * i / Segments;
            var b = notch * 0.5f + (Mathf.Tau - notch) * (i + 1) / Segments;
            var pa = new Vector3(Mathf.Cos(a) * radius, 0, Mathf.Sin(a) * radius);
            var pb = new Vector3(Mathf.Cos(b) * radius, 0, Mathf.Sin(b) * radius);
            tool.SetNormal(Vector3.Up);
            tool.AddVertex(Vector3.Zero);
            tool.AddVertex(pa);
            tool.AddVertex(pb);
            var side = new Vector3(Mathf.Cos((a + b) * 0.5f), 0, Mathf.Sin((a + b) * 0.5f));
            tool.SetNormal(side);
            tool.AddVertex(pa);
            tool.AddVertex(pb + under);
            tool.AddVertex(pb);
            tool.AddVertex(pa);
            tool.AddVertex(pa + under);
            tool.AddVertex(pb + under);
        }
        return tool.Commit();
    }
}
