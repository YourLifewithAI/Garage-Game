using Godot;
using System.Collections.Generic;

namespace GarageGame.Native.Room;

/// <summary>
/// The room's water, for play. Every water shell part (material role "water") carries a query-only collider on physics
/// layer 4, "water" (bit value 8): an Area3D with monitoring off, made of the part's own surface triangles. Nothing collides
/// with it. Bodies keep mask 1 (the companion 1 and 2), and areas are invisible to every query that does not ask for them:
/// the navigation bake parses static bodies on the world layer only, the camera arms, the sight sweep, reach and drops cast
/// against layer 1 without areas, and the look's sun rays leave areas out by default.
/// <para>
/// RoomWater answers two questions, with rays that ask for areas on that layer alone: where is the water surface above or
/// below a point, and how deep is the water there (from the surface down to the first world-layer surface, the bed). The
/// water is data the scene derives from the room; nothing here is authoritative state.
/// </para>
/// </summary>
public static class RoomWater
{
    /// <summary>Physics layer 4, "water" (game/project.godot), as a bit value.</summary>
    public const uint Layer = 8;
    public const string MaterialRole = "water";
    public const string ColliderName = "WaterSurface";
    /// <summary>How far up and down the queries look for a surface by default.</summary>
    public const float SearchM = 1.0f;
    /// <summary>How far under a surface the bed is looked for; deeper water reads as this deep.</summary>
    public const float BedSearchM = 3.0f;

    /// <summary>The water at one column: its surface, and the bed under that surface (BedY is SurfaceY - BedSearchM when no bed was found).</summary>
    public readonly record struct Column(bool Wet, float SurfaceY, float BedY)
    {
        public static readonly Column Dry = new(false, float.NaN, float.NaN);
        /// <summary>Surface to bed, in metres; 0 where there is no water.</summary>
        public float DepthM => Wet ? SurfaceY - BedY : 0;
        /// <summary>How far a point is under the surface (negative above it; 0 where there is no water).</summary>
        public float Under(Vector3 point) => Wet ? SurfaceY - point.Y : 0;
    }

    public static bool IsWater(ShellPart part) => part.MaterialRole == MaterialRole;

    /// <summary>
    /// The query-only collider for a water part: one concave shape per surface mesh, on the water layer, monitoring and
    /// monitorable off (it watches nothing and nothing watches it; rays that ask for areas still find it).
    /// </summary>
    public static Area3D CreateCollider(IEnumerable<(Shape3D Shape, Transform3D Transform)> shapes)
    {
        var area = new Area3D
        {
            Name = ColliderName, CollisionLayer = Layer, CollisionMask = 0, Monitoring = false, Monitorable = false,
            InputRayPickable = false,
        };
        foreach (var (shape, transform) in shapes)
        {
            // The surface is a sheet that must be found from above and from below (a body under water looking up).
            if (shape is ConcavePolygonShape3D sheet) sheet.BackfaceCollision = true;
            area.AddChild(new CollisionShape3D { Shape = shape, Transform = transform });
        }
        return area;
    }

    /// <summary>The first water surface straight above the point, within maxM (null when none).</summary>
    public static float? SurfaceAbove(PhysicsDirectSpaceState3D space, Vector3 point, float maxM = SearchM) =>
        Surface(space, point, point + Vector3.Up * maxM);

    /// <summary>The first water surface straight below the point, within maxM (null when none).</summary>
    public static float? SurfaceBelow(PhysicsDirectSpaceState3D space, Vector3 point, float maxM = SearchM) =>
        Surface(space, point, point + Vector3.Down * maxM);

    /// <summary>How deep the water is at this point's column: from the surface over (or just under) the point down to the bed. 0 when dry.</summary>
    public static float DepthAt(PhysicsDirectSpaceState3D space, Vector3 point) => At(space, point).DepthM;

    /// <summary>How far above the point its water's bed may be found and the point still count as in that water (a body rests up to 1.5 mm off its support).</summary>
    public const float BedToleranceM = 0.01f;
    /// <summary>The bed is looked for from this far above the surface: at a pond's rim the sheet ends a few millimetres above or below the bank.</summary>
    public const float BedLookAboveM = 0.02f;
    /// <summary>Water shallower than this over its ground (or under it) is the rim of a pond, not water.</summary>
    public const float RimM = 0.005f;

    /// <summary>
    /// The water at a point's column: the highest surface from aboveM over the point down to belowM under it, and the bed
    /// under it: the first world-layer surface a ray meets coming down from BedLookAboveM over the water (never this
    /// layer). A rim, where the ground is within RimM of the surface or above it, is dry. Water whose bed, or anything
    /// solid, lies between its surface and the point is not the point's water (dry ground under a raised basin): dry too.
    /// Water with no ground within BedSearchM under it is deep. The body asks this once a tick at its feet.
    /// </summary>
    /// <para>
    /// throughRoof: for a body already in this water (a swimmer, a diver), ground over the point is a roof (a shelf it is
    /// under), not the bed: the bed is then looked for under the point (Codex's reviews of diving).
    /// </para>
    public static Column At(PhysicsDirectSpaceState3D space, Vector3 point, float aboveM = SearchM, float belowM = 0.05f, Rid exclude = default, bool throughRoof = false)
    {
        if (!point.IsFinite()) return Column.Dry;
        var surface = Surface(space, point + Vector3.Up * aboveM, point + Vector3.Down * belowM);
        if (surface is not { } level) return Column.Dry;
        var top = new Vector3(point.X, level, point.Z);
        // Bodies ask every tick: the query, its exclusions and its answers are disposed at once rather than left to the finalizer.
        // The exclusions go to Create itself (given none, it makes an empty Godot array of its own) and are freed here (S12).
        var excluded = new Godot.Collections.Array<Rid>();
        using var excludedArray = (Godot.Collections.Array)excluded;
        if (exclude.IsValid) excluded.Add(exclude);
        using var bed = PhysicsRayQueryParameters3D.Create(top + Vector3.Up * BedLookAboveM, top + Vector3.Down * BedSearchM, RoomBuilder.WorldLayer, excluded);
        bed.HitBackFaces = false;
        float? bedHit;
        using (var hit = space.IntersectRay(bed)) bedHit = hit.Count > 0 ? hit[HitPosition].AsVector3().Y : null;
        if (bedHit == null)
        {
            // A ray exactly through a shared vertex or edge of the ground's triangles can slip between them: look again a hair aside.
            var aside = new Vector3(0.0007f, 0, 0.0005f);
            bed.From += aside;
            bed.To += aside;
            using var again = space.IntersectRay(bed);
            bedHit = again.Count > 0 ? again[HitPosition].AsVector3().Y : null;
        }
        if (throughRoof && bedHit is { } roof && roof > point.Y + BedToleranceM && roof <= level - RimM)
        {
            bed.From = new Vector3(point.X, point.Y + BedToleranceM, point.Z);
            bed.To = new Vector3(point.X, level - BedSearchM, point.Z);
            using var under = space.IntersectRay(bed);
            bedHit = under.Count > 0 ? under[HitPosition].AsVector3().Y : null;
        }
        if (bedHit is not { } bedY) return new Column(true, level, level - BedSearchM);
        if (bedY > level - RimM || bedY > point.Y + BedToleranceM) return Column.Dry;
        return new Column(true, level, bedY);
    }

    private static float? Surface(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
    {
        if (!from.IsFinite() || !to.IsFinite()) return null;
        using var query = PhysicsRayQueryParameters3D.Create(from, to, Layer, NoExclusions);
        query.CollideWithAreas = true;
        query.CollideWithBodies = false;
        // The surface is a sheet: found from either side.
        query.HitBackFaces = true;
        using var hit = space.IntersectRay(query);
        return hit.Count > 0 ? hit[HitPosition].AsVector3().Y : null;
    }

    /// <summary>No exclusions, and a ray answer's key, made once (S12).</summary>
    private static readonly Godot.Collections.Array<Rid> NoExclusions = new();
    private static readonly Variant HitPosition = "position";
}
