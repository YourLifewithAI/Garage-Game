using Godot;
using System;

namespace GarageGame.Native.Look.Fauna;

/// <summary>
/// One small fish, one unit long, nose toward -Z (Godot's forward), built once and drawn for every fish by a MultiMesh: a
/// spindle body taller than it is wide, a forked tail and a little dorsal fin. UV.x runs along the body (0 at the nose, 1 at
/// the tail's tip), UV.y up its side (0 the belly, 1 the back), and UV2.x is 1 on the fins, so the fish shader can paint a
/// back, a belly, a stripe, an eye and fins and bend the tail. About a hundred triangles.
/// </summary>
public static class FishMesh
{
    private const int Segments = 10;
    private const int Rings = 9;
    /// <summary>The body ends at the tail's root; the tail fin carries on to +0.5.</summary>
    private const float Nose = -0.5f, Root = 0.3f, Tip = 0.5f;
    private const float HalfHeight = 0.12f, HalfWidth = 0.055f;

    private static ArrayMesh? _mesh;

    public static ArrayMesh Get() => _mesh ??= Build();

    /// <summary>The body's half height and half width at t along it (0 nose, 1 tail root): blunt nose, deep shoulder, slim tail stalk.</summary>
    private static (float Height, float Width) Profile(float t)
    {
        var bulk = MathF.Pow(Math.Max(0f, MathF.Sin(MathF.PI * MathF.Pow(Math.Clamp(t, 0f, 1f), 0.62f))), 0.85f);
        var stalk = 0.16f * Math.Clamp((t - 0.55f) / 0.45f, 0f, 1f);
        var r = Math.Max(bulk, stalk);
        return (HalfHeight * r, HalfWidth * r);
    }

    private static ArrayMesh Build()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        void Vertex(Vector3 position, Vector3 normal, float along, float up, float fin)
        {
            tool.SetNormal(normal);
            tool.SetUV(new Vector2(along, up));
            tool.SetUV2(new Vector2(fin, 0f));
            tool.AddVertex(position);
        }
        // Rings from just behind the nose to the tail's root.
        var ring = new (Vector3 P, Vector3 N, float Up)[Rings, Segments];
        var alongOf = new float[Rings];
        for (var r = 0; r < Rings; r++)
        {
            var t = (r + 1f) / Rings;
            var z = Nose + (Root - Nose) * t;
            alongOf[r] = (z - Nose) / (Tip - Nose);
            var (h, w) = Profile(t);
            for (var s = 0; s < Segments; s++)
            {
                var a = Mathf.Tau * s / Segments;
                var y = MathF.Cos(a) * h;
                var x = MathF.Sin(a) * w;
                var n = new Vector3(MathF.Sin(a) / Math.Max(w, 1e-4f), MathF.Cos(a) / Math.Max(h, 1e-4f), 0f).Normalized();
                ring[r, s] = (new Vector3(x, y - 0.012f * t, z), n, 0.5f + 0.5f * MathF.Cos(a));
            }
        }
        // The nose: a fan from its tip to the first ring.
        var nose = new Vector3(0f, -0.004f, Nose);
        for (var s = 0; s < Segments; s++)
        {
            var b = ring[0, s];
            var c = ring[0, (s + 1) % Segments];
            Vertex(nose, Vector3.Forward, 0f, 0.5f, 0f);
            Vertex(c.P, (c.N + Vector3.Forward).Normalized(), alongOf[0], c.Up, 0f);
            Vertex(b.P, (b.N + Vector3.Forward).Normalized(), alongOf[0], b.Up, 0f);
        }
        for (var r = 0; r + 1 < Rings; r++)
            for (var s = 0; s < Segments; s++)
            {
                var a = ring[r, s];
                var b = ring[r, (s + 1) % Segments];
                var c = ring[r + 1, s];
                var d = ring[r + 1, (s + 1) % Segments];
                Vertex(a.P, a.N, alongOf[r], a.Up, 0f);
                Vertex(b.P, b.N, alongOf[r], b.Up, 0f);
                Vertex(c.P, c.N, alongOf[r + 1], c.Up, 0f);
                Vertex(b.P, b.N, alongOf[r], b.Up, 0f);
                Vertex(d.P, d.N, alongOf[r + 1], d.Up, 0f);
                Vertex(c.P, c.N, alongOf[r + 1], c.Up, 0f);
            }
        // Close the tail stalk.
        var last = Rings - 1;
        var stalkEnd = new Vector3(0f, ring[last, 0].P.Y - Profile(1f).Height, Root);
        for (var s = 0; s < Segments; s++)
        {
            var b = ring[last, s];
            var c = ring[last, (s + 1) % Segments];
            Vertex(stalkEnd, Vector3.Back, alongOf[last], 0.5f, 0f);
            Vertex(b.P, b.N, alongOf[last], b.Up, 0f);
            Vertex(c.P, c.N, alongOf[last], c.Up, 0f);
        }
        // A forked tail in the fish's upright plane (drawn from both sides by the shader).
        var rootY = stalkEnd.Y;
        float AlongZ(float z) => (z - Nose) / (Tip - Nose);
        Vector3 baseTop = new(0f, rootY + 0.035f, Root - 0.03f), baseLow = new(0f, rootY - 0.035f, Root - 0.03f);
        Vector3 tipTop = new(0f, rootY + 0.15f, Tip), tipLow = new(0f, rootY - 0.13f, Tip), notch = new(0f, rootY, Tip - 0.08f);
        void Fin(Vector3 a, Vector3 b, Vector3 c)
        {
            // Godot's front faces wind clockwise; the shader flips the normal on the back, so each side lights as its own.
            var n = (c - a).Cross(b - a).Normalized();
            Vertex(a, n, AlongZ(a.Z), 0.5f + a.Y, 1f);
            Vertex(b, n, AlongZ(b.Z), 0.5f + b.Y, 1f);
            Vertex(c, n, AlongZ(c.Z), 0.5f + c.Y, 1f);
        }
        Fin(baseTop, tipTop, notch);
        Fin(baseLow, notch, tipLow);
        Fin(baseTop, notch, baseLow);
        // A little dorsal fin on the shoulder, and a smaller one underneath behind the belly.
        var (shoulder, _) = Profile(0.45f);
        var dorsalZ = Nose + (Root - Nose) * 0.45f;
        Fin(new Vector3(0f, shoulder * 0.85f, dorsalZ - 0.08f), new Vector3(0f, shoulder + 0.075f, dorsalZ + 0.02f), new Vector3(0f, shoulder * 0.7f, dorsalZ + 0.12f));
        var (belly, _) = Profile(0.7f);
        var analZ = Nose + (Root - Nose) * 0.7f;
        Fin(new Vector3(0f, -belly * 0.8f, analZ - 0.05f), new Vector3(0f, -belly * 0.7f, analZ + 0.08f), new Vector3(0f, -belly - 0.04f, analZ + 0.06f));
        var mesh = tool.Commit();
        mesh.ResourceName = "fish";
        return mesh;
    }
}
