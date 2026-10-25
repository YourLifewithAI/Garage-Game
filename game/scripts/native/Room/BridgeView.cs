using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Look;

namespace GarageGame.Native.Room;

/// <summary>
/// The island's bridges of plants in the scene (MECHANICS.md decision 106, Grow's bridge): each a level deck of woven stems a tenth of a
/// metre wide, drawn from lip to lip and collided only over the void it crosses, as a one-sided rectangle facing up (a
/// ConcavePolygonShape3D without backface collision) on the world layer: solid from above, so the player walks across and stands on it,
/// while nothing below or beside it is pushed, and it never reaches over the ground at the lips. Its collision names it (the entity id
/// bridge:n, the material leaf), so the nature sampler finds leaf on it and fire burns it as a plant. Nothing here is authoritative:
/// the command host hands the view the bridges its nature keeps (SetBridges), and it builds them again only when they change.
/// Lives under the room node beside the plant copies.
/// </summary>
public partial class BridgeView : Node3D
{
    public const string ViewName = "Bridges";
    /// <summary>The drawn deck's width and thickness (its top is the deck's height; the collision is its top alone).</summary>
    public const float WidthM = 0.1f, ThicknessM = 0.015f;
    private static readonly Color DeckColor = new("5e7f35");
    private static readonly Color ScorchColor = new("2f2a22");

    /// <summary>One bridge as drawn: its id, its lips (the deck's height), the void's span along it (metres from From) and its burn.</summary>
    public readonly record struct Shown(string Id, Vector3 From, Vector3 To, float SpanFrom, float SpanTo, float Burn);

    private List<Shown> _shown = new();

    /// <summary>How many bridges the view holds now (the tests read it).</summary>
    public int Count => _shown.Count;

    public static BridgeView Create() => new() { Name = ViewName };

    /// <summary>The bridges as the host's nature keeps them; rebuilt only on a change. Returns whether anything changed.</summary>
    public bool SetBridges(IReadOnlyList<Shown> bridges)
    {
        if (bridges.SequenceEqual(_shown)) return false;
        _shown = bridges.ToList();
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        foreach (var bridge in _shown)
        {
            var length = bridge.From.DistanceTo(bridge.To);
            if (length < 1e-3f) continue;
            var along = (bridge.To - bridge.From) / length;
            var body = new StaticBody3D { Name = bridge.Id.Replace(':', '_'), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
            body.SetMeta(Kernel.KernelNames.EntityIdMeta, bridge.Id);
            body.SetMeta(Kernel.KernelNames.MaterialRoleMeta, "leaf");
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = DeckFaces(bridge.From, along, bridge.SpanFrom, bridge.SpanTo), BackfaceCollision = false } });
            var color = DeckColor.Lerp(ScorchColor, Math.Clamp(bridge.Burn, 0, 1));
            var deck = new MeshInstance3D
            {
                Name = "Deck", Mesh = new BoxMesh { Size = new Vector3(WidthM, ThicknessM, length) }, MaterialOverride = MaterialLibrary.For("foliage", color),
                Transform = new Transform3D(Basis.LookingAt(along, Vector3.Up), (bridge.From + bridge.To) * 0.5f + Vector3.Down * (ThicknessM * 0.5f)),
            };
            body.AddChild(deck);
            AddChild(body);
        }
        return true;
    }

    /// <summary>
    /// The collision over the void: a rectangle at the deck's height from span start to span end along the deck, WidthM wide, as two
    /// triangles facing up (wound as WaterpadView.DiscFaces: Godot's front faces, seen from above).
    /// </summary>
    public static Vector3[] DeckFaces(Vector3 from, Vector3 along, float spanFrom, float spanTo)
    {
        var across = new Vector3(-along.Z, 0, along.X).Normalized() * (WidthM * 0.5f);
        var a = from + along * spanFrom;
        var b = from + along * spanTo;
        var (p0, p1, p2, p3) = (a - across, b - across, b + across, a + across);
        // Front faces up: a triangle (o, o + u, o + v) faces up when u × v points down (as +X then +Z does).
        if ((p1 - p0).Cross(p3 - p0).Y > 0) (p1, p3) = (p3, p1);
        return new[] { p0, p1, p2, p0, p2, p3 };
    }
}
