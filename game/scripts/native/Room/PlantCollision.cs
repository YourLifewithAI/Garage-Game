using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Room;

/// <summary>
/// The collision a copy's row describes (contracts/README.md "Plant copies"; packet S6), as shapes the game builds itself rather
/// than meshes it loads. A tree: its trunk, a convex twelve-sided frustum from the copy's base to trunk_top_m tapering from
/// trunk_radius_m to pole_radius_m (the drawn bark cone's size), solid; a hidden pole, a convex twelve-sided prism of pole_radius_m
/// from 1 cm inside the trunk's top to pole_top_m, its sides flush with the trunk's top so a climber passes no lip; and a cap per
/// crown, a one-sided collar from the rim ellipse up to a flat perch ellipse, exactly as pipeline/landscape/export/trees.py
/// builds it (twelve collar sectors and a top fan, no underside), so a climber comes up through it from inside and stands on top.
/// A solid stone: the convex hull of its full-detail mesh under its row's transform. Pure geometry: any thread.
///
/// The game builds each kind of shape once, in a unit size, and places it per row with a scaling transform (Unit*, *Place):
/// Jolt scales a convex hull or a mesh without rebuilding it, where building a hull per tree cost about 84 microseconds (the
/// 10x garage's 4,900 trees took 1.2 s of main-thread work; shared, about 1.5 microseconds a shape). A trunk's taper and a
/// cap's perch-to-rim ratios are rounded to 1/256 for sharing: the perch's half-sizes move by at most 0.3 mm, and a trunk's
/// top is never narrower than the pole it carries (rounded up, by at most 0.2 mm wider).
/// </summary>
public static class PlantCollision
{
    /// <summary>Sides of the trunk, the pole and each cap's rings: trees.py's SEGMENTS.</summary>
    public const int Segments = 12;
    /// <summary>The pole starts this far inside the trunk's top (trees.py POLE_OVERLAP_M), so the two meet without a seam.</summary>
    public const float PoleOverlapM = 0.01f;
    /// <summary>
    /// The collision margin of the convex shapes. Godot's default (4 cm) lets Jolt round a shape's edges by up to that much,
    /// which on a 5 cm trunk would round its top into a shoulder a climber pulls over onto; 1 mm keeps the edges where they are.
    /// </summary>
    public const float ConvexMarginM = 0.001f;

    /// <summary>The steps the trunk's taper (pole_radius_m / trunk_radius_m) and a cap's perch-to-rim ratios are rounded to, for sharing shapes.</summary>
    public const int RatioSteps = 256;

    /// <summary>A trunk's taper in RatioSteps, rounded up so its top is never narrower than its pole.</summary>
    public static int TaperSteps(TreeShape tree) => Math.Max(1, (int)Math.Ceiling(tree.PoleRadiusM / (double)tree.TrunkRadiusM * RatioSteps - 1e-6));

    /// <summary>The unit trunk: a ring of radius 1 at y 0 and one of taperSteps / RatioSteps at y 1.</summary>
    public static Vector3[] UnitTrunk(int taperSteps) => Trunk(new TreeShape(0, 0, 0, 1, taperSteps / (float)RatioSteps, 1, 2, Array.Empty<TreeCap>()));

    public static Transform3D TrunkPlace(TreeShape tree) =>
        new(Basis.FromScale(new Vector3(tree.TrunkRadiusM, tree.TrunkTopM - tree.BaseY, tree.TrunkRadiusM)), new Vector3(tree.AxisX, tree.BaseY, tree.AxisZ));

    /// <summary>The unit pole: radius 1 from y 0 to y 1, at the trunk's angles.</summary>
    public static Vector3[] UnitPole() => Pole(new TreeShape(-1, 0, 0, 1, 1, PoleOverlapM, 1, Array.Empty<TreeCap>()));

    public static Transform3D PolePlace(TreeShape tree)
    {
        var bottom = Math.Max(tree.BaseY, tree.TrunkTopM - PoleOverlapM);
        return new Transform3D(Basis.FromScale(new Vector3(tree.PoleRadiusM, tree.PoleTopM - bottom, tree.PoleRadiusM)), new Vector3(tree.AxisX, bottom, tree.AxisZ));
    }

    /// <summary>A cap's perch-to-rim ratios along x and z, in RatioSteps.</summary>
    public static (int X, int Z) CapSteps(TreeCap cap) =>
        (Math.Clamp((int)Math.Round(cap.TopHalfXM / (double)cap.RimHalfXM * RatioSteps), 1, RatioSteps),
         Math.Clamp((int)Math.Round(cap.TopHalfZM / (double)cap.RimHalfZM * RatioSteps), 1, RatioSteps));

    /// <summary>The unit cap: its rim of radius 1 at y 0, its perch at y 1 with the given ratios.</summary>
    public static Vector3[] UnitCapFaces((int X, int Z) steps) =>
        CapFaces(new TreeCap(0, 0, 1, 1, 0, 1, steps.X / (float)RatioSteps, steps.Z / (float)RatioSteps));

    public static Transform3D CapPlace(TreeCap cap) =>
        new(Basis.FromScale(new Vector3(cap.RimHalfXM, cap.TopM - cap.RimM, cap.RimHalfZM)), new Vector3(cap.X, cap.RimM, cap.Z));

    /// <summary>
    /// A tree's climbing collision grown with the tree (Grow, decision 52): every height above its base and every offset from its root
    /// (pivotX, pivotZ) times factor, every radius and half-size times factor, so the same unit shapes are placed with the grown
    /// transform. Its taper and its caps' perch-to-rim ratios do not change, so the shared shapes stay shared.
    /// </summary>
    public static TreeShape Grown(TreeShape tree, float pivotX, float pivotZ, float factor)
    {
        if (factor == 1) return tree;
        float X(float x) => pivotX + (x - pivotX) * factor;
        float Z(float z) => pivotZ + (z - pivotZ) * factor;
        float Y(float y) => tree.BaseY + (y - tree.BaseY) * factor;
        var caps = new TreeCap[tree.Caps.Length];
        for (var k = 0; k < caps.Length; k++)
        {
            var c = tree.Caps[k];
            caps[k] = new TreeCap(X(c.X), Z(c.Z), c.RimHalfXM * factor, c.RimHalfZM * factor, Y(c.RimM), Y(c.TopM), c.TopHalfXM * factor, c.TopHalfZM * factor);
        }
        return new TreeShape(tree.BaseY, X(tree.AxisX), Z(tree.AxisZ), tree.TrunkRadiusM * factor, tree.PoleRadiusM * factor, Y(tree.TrunkTopM), Y(tree.PoleTopM), caps);
    }

    /// <summary>The trunk's corners in room metres: a ring of trunk_radius_m at the base and one of pole_radius_m at trunk_top_m.</summary>
    public static Vector3[] Trunk(TreeShape tree)
    {
        var points = new Vector3[2 * Segments];
        for (var i = 0; i < Segments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / Segments);
            points[i] = new Vector3(tree.AxisX + tree.TrunkRadiusM * cos, tree.BaseY, tree.AxisZ + tree.TrunkRadiusM * sin);
            points[Segments + i] = new Vector3(tree.AxisX + tree.PoleRadiusM * cos, tree.TrunkTopM, tree.AxisZ + tree.PoleRadiusM * sin);
        }
        return points;
    }

    /// <summary>The hidden pole's corners: pole_radius_m from just inside the trunk's top to pole_top_m, at the trunk's own angles.</summary>
    public static Vector3[] Pole(TreeShape tree)
    {
        var points = new Vector3[2 * Segments];
        var bottom = Math.Max(tree.BaseY, tree.TrunkTopM - PoleOverlapM);
        for (var i = 0; i < Segments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / Segments);
            points[i] = new Vector3(tree.AxisX + tree.PoleRadiusM * cos, bottom, tree.AxisZ + tree.PoleRadiusM * sin);
            points[Segments + i] = new Vector3(tree.AxisX + tree.PoleRadiusM * cos, tree.PoleTopM, tree.AxisZ + tree.PoleRadiusM * sin);
        }
        return points;
    }

    /// <summary>
    /// A cap's faces (three corners each) in Godot's winding, fronts outward and up: the collar from the rim ring to the perch
    /// ring, and the perch's fan round its centre. trees.py writes the same triangles counter-clockwise for glTF; Godot's front
    /// faces are clockwise, as its importer turns them.
    /// </summary>
    public static Vector3[] CapFaces(TreeCap cap)
    {
        var rim = new Vector3[Segments];
        var top = new Vector3[Segments];
        for (var i = 0; i < Segments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / Segments);
            rim[i] = new Vector3(cap.X + cap.RimHalfXM * cos, cap.RimM, cap.Z + cap.RimHalfZM * sin);
            top[i] = new Vector3(cap.X + cap.TopHalfXM * cos, cap.TopM, cap.Z + cap.TopHalfZM * sin);
        }
        var centre = new Vector3(cap.X, cap.TopM, cap.Z);
        var faces = new Vector3[Segments * 9];
        var f = 0;
        for (var i = 0; i < Segments; i++)
        {
            var k = (i + 1) % Segments;
            // trees.py (glTF, counter-clockwise): [i, S+i, S+k], [i, S+k, k], [centre, S+k, S+i]; each with its last two corners swapped.
            faces[f++] = rim[i]; faces[f++] = top[k]; faces[f++] = top[i];
            faces[f++] = rim[i]; faces[f++] = rim[k]; faces[f++] = top[k];
            faces[f++] = centre; faces[f++] = top[i]; faces[f++] = top[k];
        }
        return faces;
    }

    /// <summary>A solid copy's hull points in room metres: its mesh's points under its row's transform.</summary>
    public static Vector3[] Solid(PlantSet set, int row)
    {
        var transform = set.Transform(row);
        var points = new Vector3[set.SolidPoints.Count];
        for (var i = 0; i < points.Length; i++) points[i] = transform * set.SolidPoints[i];
        return points;
    }

    /// <summary>The convex outline of points seen from above (x, z), counter-clockwise, at height y: Andrew's monotone chain.</summary>
    public static Vector3[] Footprint(IReadOnlyList<Vector3> points, float y)
    {
        var flat = new List<Vector2>(points.Count);
        foreach (var p in points) flat.Add(new Vector2(p.X, p.Z));
        flat.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        if (flat.Count < 3) return flat.ConvertAll(p => new Vector3(p.X, y, p.Y)).ToArray();
        var hull = new List<Vector2>();
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var pass in new[] { false, true })
        {
            var start = hull.Count;
            for (var n = 0; n < flat.Count; n++)
            {
                var p = flat[pass ? flat.Count - 1 - n : n];
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull.ConvertAll(p => new Vector3(p.X, y, p.Y)).ToArray();
    }
}
