using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Look.Fractals;

/// <summary>
/// The bodies a Fractal can take (Run 2, L9: docs/look/FRACTALS.md), built once in code and drawn for every Fractal by one
/// MultiMesh, as the fish are (FishMesh). Each is centred on its own middle, so the look turns and bobs it about that point, and
/// each is under 200 triangles:
/// <list type="bullet">
/// <item><b>The crystal</b>: a held fractal. A tetrahedron grows a smaller one on the middle of each face, twice (the Koch
/// tetrahedron's second step, 144 facets); a body diagonal stands upright, so it is a star of facets with a point up and a point
/// down. Flat normals, so every facet catches the light on its own. UV.x is a facet's own number (0 to 1), UV.y how far out it
/// grew (0 the first tetrahedron, 0.5 the first growth, 1 the second).</item>
/// <item><b>The seed</b>: a fern's crosier, a tapering felt tube that rises from a short stem and curls one and a half turns into
/// itself. UV.x runs along the tube (0 at the stem's foot, 1 at the heart of the curl), UV.y round it.</item>
/// </list>
/// The star has no body: it is light, drawn by the glint's shader on a quad (FractalLook).
/// </summary>
public static class FractalMesh
{
    /// <summary>The crystal's half height (its points up and down), metres; its radius round the upright axis follows from its shape.</summary>
    public const float CrystalHalfHeightM = 0.0135f;
    /// <summary>The seed's height, metres (the curl's top to the stem's foot).</summary>
    public const float SeedHeightM = 0.025f;

    private const int SeedSides = 6, SeedRings = 15;

    private static ArrayMesh? _crystal, _seed;

    public static ArrayMesh Crystal() => _crystal ??= BuildCrystal();
    public static ArrayMesh Seed() => _seed ??= BuildSeed();

    public static ArrayMesh? For(FractalForm form) => form switch
    {
        FractalForm.Crystal => Crystal(),
        FractalForm.Seed => Seed(),
        _ => null,
    };

    private static readonly Dictionary<FractalForm, (float, float)> ExtentsCache = new();

    /// <summary>
    /// A form's extents round its middle, from its mesh: half its height, and its radius round the upright axis (what it sweeps as it
    /// turns). The star's are its heart's.
    /// </summary>
    public static (float HalfHeightM, float RadiusM) Extents(FractalForm form)
    {
        if (ExtentsCache.TryGetValue(form, out var known)) return known;
        var extents = (FractalLook.Tuning.StarHeartM * 0.5f, FractalLook.Tuning.StarHeartM * 0.5f);
        if (For(form) is { } mesh)
        {
            float half = 0f, radius = 0f;
            foreach (var v in mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
            {
                half = MathF.Max(half, MathF.Abs(v.Y));
                radius = MathF.Max(radius, new Vector2(v.X, v.Z).Length());
            }
            extents = (half, radius);
        }
        ExtentsCache[form] = extents;
        return extents;
    }

    // ---------- the crystal ----------

    private readonly record struct Facet(Vector3 A, Vector3 B, Vector3 C, float Depth);

    private static ArrayMesh BuildCrystal()
    {
        // A regular tetrahedron on alternate corners of the cube [-1, 1]^3: the Koch growth never leaves that cube.
        Vector3 p0 = new(1, 1, 1), p1 = new(1, -1, -1), p2 = new(-1, 1, -1), p3 = new(-1, -1, 1);
        var facets = new List<Facet> { Away(p0, p1, p2, Vector3.Zero, 0f), Away(p0, p3, p1, Vector3.Zero, 0f), Away(p0, p2, p3, Vector3.Zero, 0f), Away(p1, p3, p2, Vector3.Zero, 0f) };
        for (var step = 1; step <= 2; step++)
        {
            var next = new List<Facet>(facets.Count * 6);
            foreach (var f in facets) Grow(f, step * 0.5f, next);
            facets = next;
        }
        // The body diagonal (1, 1, 1) upright, then scaled to the crystal's half height (the cube's corner is sqrt(3) out).
        var upright = new Basis(new Quaternion(new Vector3(1, 1, 1).Normalized(), Vector3.Up));
        var scale = CrystalHalfHeightM / MathF.Sqrt(3f);
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i < facets.Count; i++)
        {
            var f = facets[i];
            var a = upright * f.A * scale;
            var b = upright * f.B * scale;
            var c = upright * f.C * scale;
            // Every facet is kept facing out ((b - a) x (c - a) outward); Godot's front faces wind clockwise seen from outside.
            var n = (b - a).Cross(c - a).Normalized();
            var uv = new Vector2((i + 0.5f) / facets.Count, f.Depth);
            foreach (var v in new[] { a, c, b })
            {
                tool.SetNormal(n);
                tool.SetUV(uv);
                tool.AddVertex(v);
            }
        }
        var mesh = tool.Commit();
        mesh.ResourceName = "fractal crystal";
        return mesh;
    }

    /// <summary>
    /// One Koch step: the facet's three corner triangles stay (facing out as it did); its middle triangle grows a regular
    /// tetrahedron outward, whose three sides face away from that tetrahedron's own middle.
    /// </summary>
    private static void Grow(Facet f, float depth, List<Facet> into)
    {
        var ab = (f.A + f.B) * 0.5f;
        var bc = (f.B + f.C) * 0.5f;
        var ca = (f.C + f.A) * 0.5f;
        var normal = (f.B - f.A).Cross(f.C - f.A).Normalized();
        var edge = ab.DistanceTo(bc);
        var apex = (ab + bc + ca) / 3f + normal * edge * MathF.Sqrt(2f / 3f);
        var middle = (ab + bc + ca + apex) / 4f;
        into.Add(new Facet(f.A, ab, ca, f.Depth));
        into.Add(new Facet(ab, f.B, bc, f.Depth));
        into.Add(new Facet(ca, bc, f.C, f.Depth));
        into.Add(Away(ab, bc, apex, middle, depth));
        into.Add(Away(bc, ca, apex, middle, depth));
        into.Add(Away(ca, ab, apex, middle, depth));
    }

    /// <summary>A facet with its corners in the order that faces away from a point.</summary>
    private static Facet Away(Vector3 a, Vector3 b, Vector3 c, Vector3 from, float depth) =>
        (b - a).Cross(c - a).Dot((a + b + c) / 3f - from) >= 0f ? new Facet(a, b, c, depth) : new Facet(a, c, b, depth);

    // ---------- the seed ----------

    /// <summary>
    /// The crosier's centre line at t (0 the stem's foot, 1 the heart of the curl) and the tube's radius there, in its own units
    /// (the mesh is scaled to SeedHeightM after). A short stem rises and leans into a plump logarithmic coil of 1.6 turns whose
    /// rounds touch, drifting a little sideways (z) as it winds, so it reads as a curled seed from every side as it turns.
    /// </summary>
    public static (Vector3 Point, float Radius) Crosier(float t)
    {
        const float stem = 0.3f;
        const float turns = 1.6f;
        const float outer = 0.0095f, inner = 0.0025f, start = -0.35f, drift = 0.009f;
        var foot = new Vector2(0.004f, -0.012f);
        var centre = new Vector2(-0.001f, 0.003f);
        var join = centre + new Vector2(MathF.Cos(start), MathF.Sin(start)) * outer;
        if (t <= stem)
        {
            var s = t / stem;
            // A gentle lean from the foot up to the coil's start, arriving heading upward.
            var control = new Vector2(foot.X + 0.0045f, (foot.Y + join.Y) * 0.5f);
            var point = (1 - s) * (1 - s) * foot + 2 * (1 - s) * s * control + s * s * join;
            return (new Vector3(point.X, point.Y, 0f), Mathf.Lerp(0.0032f, 0.0037f, s));
        }
        var u = (t - stem) / (1f - stem);
        // From the outer start, counter-clockwise (up and over to the left), inward.
        var angle = start + u * turns * Mathf.Tau;
        var r = outer * MathF.Pow(inner / outer, u);
        var at = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
        return (new Vector3(at.X, at.Y, drift * u), Mathf.Lerp(0.0037f, 0.0016f, MathF.Pow(u, 0.9f)));
    }

    private static ArrayMesh BuildSeed()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var rings = new (Vector3 P, Vector3 N)[SeedRings, SeedSides];
        var along = new float[SeedRings];
        for (var r = 0; r < SeedRings; r++)
        {
            var t = r / (SeedRings - 1f);
            along[r] = t;
            var (point, radius) = Crosier(t);
            var tangent = (Crosier(Math.Min(1f, t + 0.01f)).Point - Crosier(Math.Max(0f, t - 0.01f)).Point).Normalized();
            // The tube's frame: across in the curl's plane, and the third axis out of it.
            var across = new Vector3(-tangent.Y, tangent.X, 0f).Normalized();
            var outOf = tangent.Cross(across).Normalized();
            for (var s = 0; s < SeedSides; s++)
            {
                var a = Mathf.Tau * s / SeedSides;
                var n = across * MathF.Cos(a) + outOf * MathF.Sin(a);
                rings[r, s] = (point + n * radius, n);
            }
        }
        // Centred on its own middle (the box round its points), and scaled to its height.
        Vector3 low = Vector3.One * float.MaxValue, high = Vector3.One * float.MinValue;
        foreach (var (p, _) in rings)
        {
            low = new Vector3(MathF.Min(low.X, p.X), MathF.Min(low.Y, p.Y), MathF.Min(low.Z, p.Z));
            high = new Vector3(MathF.Max(high.X, p.X), MathF.Max(high.Y, p.Y), MathF.Max(high.Z, p.Z));
        }
        var shift = (low + high) * 0.5f;
        var fit = SeedHeightM / (high.Y - low.Y);
        for (var r = 0; r < SeedRings; r++)
            for (var s = 0; s < SeedSides; s++)
                rings[r, s].P = (rings[r, s].P - shift) * fit;
        // One triangle, wound clockwise seen from the side its normals face (Godot's front faces).
        void Triangle((Vector3 P, Vector3 N, Vector2 Uv) a, (Vector3 P, Vector3 N, Vector2 Uv) b, (Vector3 P, Vector3 N, Vector2 Uv) c)
        {
            if ((c.P - a.P).Cross(b.P - a.P).Dot(a.N + b.N + c.N) < 0f) (b, c) = (c, b);
            foreach (var v in new[] { a, b, c })
            {
                tool.SetNormal(v.N);
                tool.SetUV(v.Uv);
                tool.AddVertex(v.P);
            }
        }
        for (var r = 0; r + 1 < SeedRings; r++)
            for (var s = 0; s < SeedSides; s++)
            {
                var s1 = (s + 1) % SeedSides;
                float u0 = s / (float)SeedSides, u1 = (s + 1) / (float)SeedSides;
                var a = (rings[r, s].P, rings[r, s].N, new Vector2(along[r], u0));
                var b = (rings[r, s1].P, rings[r, s1].N, new Vector2(along[r], u1));
                var c = (rings[r + 1, s].P, rings[r + 1, s].N, new Vector2(along[r + 1], u0));
                var d = (rings[r + 1, s1].P, rings[r + 1, s1].N, new Vector2(along[r + 1], u1));
                Triangle(a, c, b);
                Triangle(b, c, d);
            }
        // Close the foot (a little disc facing down the stem) and the tip of the curl.
        foreach (var (ring, inward) in new[] { (0, 1), (SeedRings - 1, SeedRings - 2) })
        {
            var centre = Vector3.Zero;
            for (var s = 0; s < SeedSides; s++) centre += rings[ring, s].P;
            centre /= SeedSides;
            var neighbour = Vector3.Zero;
            for (var s = 0; s < SeedSides; s++) neighbour += rings[inward, s].P;
            var axis = (centre - neighbour / SeedSides).Normalized();
            for (var s = 0; s < SeedSides; s++)
                Triangle((centre, axis, new Vector2(along[ring], 0.5f)), (rings[ring, s].P, axis, new Vector2(along[ring], 0f)),
                    (rings[ring, (s + 1) % SeedSides].P, axis, new Vector2(along[ring], 1f)));
        }
        var mesh = tool.Commit();
        mesh.ResourceName = "fractal seed";
        return mesh;
    }

    /// <summary>The number of triangles a form's mesh has (0 for the star, which has no body).</summary>
    public static int Triangles(FractalForm form) => For(form) is { } mesh ? mesh.SurfaceGetArrayLen(0) / 3 : 0;
}
