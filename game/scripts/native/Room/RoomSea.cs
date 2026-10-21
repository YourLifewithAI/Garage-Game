using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Room;

/// <summary>
/// The island's sea, for play (the founder, 9 October: "My daughter hates the invisible wall"; every room an island in an
/// endless sea). Read from the room's <c>extensions.x_landscape_sea</c> (pipeline/landscape/export/sea.py), which stays an
/// extension until the edge and the founder's playtest settle what the game needs. Room files are untrusted: every loop is
/// bounded (3 to 4,096 points), every number finite and within the room's coordinate limit, ids are tokens, and each beach
/// stands inside the room's bounds; anything else refuses the room. A room without the extension has no sea edge.
/// <para>
/// The geometry the bodies ask: how far past the reef a point is, whether a point is in the playable water, the nearest
/// beach, the jetty (home, for the B key), and the open sea itself where the room's water meshes stop (OpenWater). The sea
/// has no edge (the founder, 9 October: "the kids want to be able to swim forever"): no current, no wash ashore. Outlines
/// are closed [x, z] loops in metres.
/// </para>
/// </summary>
public sealed class RoomSea
{
    public const int MaxOutlinePoints = 4096;
    public const int MaxBeaches = 64;
    public const int MaxPasses = 16;
    public const string ExtensionName = "x_landscape_sea";
    private static readonly Regex Id = new(@"\A[a-z0-9_]{1,32}\z", RegexOptions.Compiled);

    public readonly record struct Beach(string Id, Vector3 WashAshoreM, float YawDeg, Vector3 WaterM);
    public readonly record struct Pass(Vector2 CentreM, float WidthM);
    public readonly record struct Jetty(Vector3 RootM, Vector3 EndM, float YawDeg, float WidthM, float DeckTopM);

    public float LevelM { get; private init; }
    public float SwimDepthM { get; private init; }
    public Vector2[] Coast { get; private init; } = Array.Empty<Vector2>();
    public Vector2[] Reef { get; private init; } = Array.Empty<Vector2>();
    public float ReefCrestYM { get; private init; }
    public float ReefBandHalfWidthM { get; private init; }
    public IReadOnlyList<Pass> Passes { get; private init; } = Array.Empty<Pass>();
    public Vector2[] PlayArea { get; private init; } = Array.Empty<Vector2>();
    public IReadOnlyList<Beach> Beaches { get; private init; } = Array.Empty<Beach>();
    public Jetty? JettyData { get; private init; }

    /// <summary>
    /// The open sea's bed, the generator's own far sea floor (generator/sea.py, distant_islands, without its islands), which
    /// the room draws but does not collide with: OpenSeaDepthM under the sea's level out to OpenSeaStartM from
    /// OpenSeaCentreM, then sinking by OpenSeaFallM more over the next OpenSeaFallOverM, smoothly, and level beyond.
    /// The generator's grid is the source room's bounds grown by GeneratorMarginM; the floor starts 0.25 m inside its
    /// nearer side. A room without its source bounds (the body suites' seas) measures from the coast's middle and the
    /// playable water's nearer half-width.
    /// </summary>
    public const float OpenSeaDepthM = 0.66f;
    public const float OpenSeaFallM = 0.6f;
    public const float OpenSeaFallOverM = 12.0f;
    /// <summary>generate.py MARGIN: the grid runs this far past the source room's bounds.</summary>
    public const float GeneratorMarginM = 1.7f;
    /// <summary>The centre the open-sea floor sinks away from.</summary>
    public Vector2 OpenSeaCentreM { get; private init; }
    /// <summary>How far from OpenSeaCentreM the open-sea floor starts to sink.</summary>
    public float OpenSeaStartM { get; private init; }

    /// <summary>The middle of the island (its coast's box), which the far net is measured from.</summary>
    public Vector2 MiddleM { get; private init; }

    /// <summary>A sea built directly (the body suites' synthetic island). Loops must have at least three points.</summary>
    public RoomSea(float levelM, Vector2[] coast, Vector2[] reef, Vector2[] playArea, IReadOnlyList<Beach> beaches, Jetty? jetty = null)
    {
        if (coast.Length < 3 || reef.Length < 3 || playArea.Length < 3) throw new ArgumentException("a sea's loops need three points or more");
        LevelM = levelM;
        SwimDepthM = 0.08f;
        Coast = coast;
        Reef = reef;
        PlayArea = playArea;
        Beaches = beaches;
        JettyData = jetty;
        MiddleM = Middle(coast);
        (OpenSeaCentreM, OpenSeaStartM) = OpenSeaFrame(null, playArea, MiddleM);
    }

    /// <summary>The generator's grid from the source room's bounds; without them, the playable water's box round the coast's middle.</summary>
    private static (Vector2 Centre, float Start) OpenSeaFrame(Aabb? source, Vector2[] playArea, Vector2 middle, float marginM = GeneratorMarginM)
    {
        if (source is { } room)
        {
            var half = new Vector2(marginM, marginM) + new Vector2(room.Size.X, room.Size.Z) * 0.5f;
            return (new Vector2(room.GetCenter().X, room.GetCenter().Z), Mathf.Min(half.X, half.Y) - 0.25f);
        }
        float reach = float.PositiveInfinity;
        var low = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var high = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var p in playArea) { low = new Vector2(Mathf.Min(low.X, p.X), Mathf.Min(low.Y, p.Y)); high = new Vector2(Mathf.Max(high.X, p.X), Mathf.Max(high.Y, p.Y)); }
        reach = Mathf.Min(Mathf.Min(middle.X - low.X, high.X - middle.X), Mathf.Min(middle.Y - low.Y, high.Y - middle.Y));
        return (middle, Mathf.Max(0, reach));
    }

    private RoomSea() { }

    /// <summary>Read and check the extension; a RoomLoadException for anything out of bounds.</summary>
    public static RoomSea Parse(JsonElement sea, Aabb bounds, Aabb? source = null)
    {
        Expect(sea.ValueKind == JsonValueKind.Object, $"{ExtensionName} is not an object");
        var reef = Obj(sea, "reef");
        var beaches = new List<Beach>();
        foreach (var beach in Records(sea, "beaches", MaxBeaches))
        {
            var id = beach.TryGetProperty("id", out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString()! : "";
            Expect(Id.IsMatch(id), $"{ExtensionName}: a beach id is not a token");
            Expect(beaches.All(b => b.Id != id), $"{ExtensionName}: beach {id} is listed twice");
            var ashore = Vec(beach, "wash_ashore_m", 3);
            var at = new Vector3(ashore[0], ashore[1], ashore[2]);
            // A beach outside the room's bounds would put a body where the bounds recover it from.
            Expect(at.X > bounds.Position.X && at.X < bounds.End.X && at.Y >= bounds.Position.Y && at.Y < bounds.End.Y && at.Z > bounds.Position.Z && at.Z < bounds.End.Z,
                $"{ExtensionName}: beach {id} is outside the room's bounds");
            var water = Vec(beach, "water_m", 3);
            beaches.Add(new Beach(id, at, Num(beach, "yaw_deg", -360, 360), new Vector3(water[0], water[1], water[2])));
        }
        var passes = Records(reef, "passes", MaxPasses).Select(p => { var c = Vec(p, "centre_m", 2); return new Pass(new Vector2(c[0], c[1]), Num(p, "width_m", 0, 10)); }).ToArray();
        Jetty? jetty = null;
        if (sea.TryGetProperty("jetty", out var j) && j.ValueKind != JsonValueKind.Null)
        {
            Expect(j.ValueKind == JsonValueKind.Object, $"{ExtensionName}: the jetty is not an object");
            var root = Vec(j, "root_m", 3);
            var end = Vec(j, "end_m", 3);
            // Home (B) is the jetty's root: like a beach, it must stand inside the room's bounds (Codex Sol's review: a root
            // 1 km out was read, and B took the player out to the far net).
            bool In(float[] p) => p[0] > bounds.Position.X && p[0] < bounds.End.X && p[1] >= bounds.Position.Y && p[1] < bounds.End.Y && p[2] > bounds.Position.Z && p[2] < bounds.End.Z;
            Expect(In(root) && In(end), $"{ExtensionName}: the jetty is outside the room's bounds");
            // Its root stands on the deck's own height, and its deck stands over the sea (Codex Astra's review: a root over the
            // seabed half a metre down was a home under water).
            var deck = Num(j, "deck_top_m", -10, 10);
            var level = Num(sea, "level_m", -10, 10);
            Expect(Mathf.Abs(root[1] - deck) <= JettyRootDeckM && Mathf.Abs(end[1] - deck) <= JettyRootDeckM && deck > level,
                $"{ExtensionName}: the jetty's root and end must stand at its deck's height, over the sea");
            jetty = new Jetty(new Vector3(root[0], root[1], root[2]), new Vector3(end[0], end[1], end[2]), Num(j, "yaw_deg", -360, 360), Num(j, "width_m", 0, 10), Num(j, "deck_top_m", -10, 10));
        }
        var coast = Loop(Obj(sea, "coast"), "outline_m");
        var playArea = Loop(Obj(sea, "play_area"), "outline_m");
        var middle = Middle(coast);
        // The generator's margin, when the exporter writes it (x_landscape_sea.grid_margin_m); else generate.py's own 1.7 m.
        var margin = sea.TryGetProperty("grid_margin_m", out _) ? Num(sea, "grid_margin_m", 0, 100) : GeneratorMarginM;
        var (centre, start) = OpenSeaFrame(source, playArea, middle, margin);
        return new RoomSea
        {
            LevelM = Num(sea, "level_m", -10, 10), SwimDepthM = Num(sea, "swim_depth_m", 0, 10),
            Coast = coast, MiddleM = middle, OpenSeaCentreM = centre, OpenSeaStartM = start, Reef = Loop(reef, "outline_m"),
            ReefCrestYM = Num(reef, "crest_y_m", -10, 10), ReefBandHalfWidthM = Num(reef, "band_half_width_m", 0, 10),
            Passes = passes, PlayArea = playArea, Beaches = beaches, JettyData = jetty,
        };
    }

    // ---- what the bodies ask ----

    /// <summary>How far past the reef a point is (0 inside the reef's loop).</summary>
    public float PastReefM(Vector2 point) => Inside(Reef, point) ? 0 : Nearest(Reef, point).Distance;

    /// <summary>Whether a point is in the playable water (inside the play area's loop, which takes in the island).</summary>
    public bool InPlayArea(Vector2 point) => Inside(PlayArea, point);

    /// <summary>
    /// The open sea's bed at a point, a public read for the look's endless sea and for diving: with r the distance from
    /// OpenSeaCentreM and t = clamp((r - OpenSeaStartM) / OpenSeaFallOverM, 0, 1), LevelM - OpenSeaDepthM - OpenSeaFallM *
    /// t * t * (3 - 2t). The generator's far floor, as the room draws it.
    /// </summary>
    public float OpenSeaBedAt(Vector2 point)
    {
        var t = Mathf.Clamp((point.DistanceTo(OpenSeaCentreM) - OpenSeaStartM) / OpenSeaFallOverM, 0, 1);
        return LevelM - OpenSeaDepthM - OpenSeaFallM * t * t * (3 - 2 * t);
    }

    /// <summary>How far a jetty's root and end may stand from its deck's height.</summary>
    public const float JettyRootDeckM = 0.05f;

    /// <summary>
    /// The sea itself at a point's column, for where no water mesh answers (out past the island, where the meshes stop): its
    /// surface at LevelM when that lies from aboveM over the point down to belowM under it, outside the coast, over the first
    /// world-layer ground under the surface or else the open-sea bed. Ground at or above the surface (a sea stack's top) is
    /// dry, as RoomWater.At has it. RoomWater.Column.Dry otherwise.
    /// </summary>
    /// <summary>A ray answer's key, made once (S12).</summary>
    private static readonly Variant HitPosition = "position";

    public RoomWater.Column OpenWater(PhysicsDirectSpaceState3D space, Vector3 point, float aboveM, float belowM, Rid exclude = default, bool throughRoof = false)
    {
        if (!point.IsFinite() || LevelM > point.Y + aboveM || LevelM < point.Y - belowM) return RoomWater.Column.Dry;
        var flat = new Vector2(point.X, point.Z);
        if (Inside(Coast, flat)) return RoomWater.Column.Dry;
        // Real ground under the surface is looked for as deep as RoomWater looks (Codex Sol's review: the ray had stopped at
        // the open-sea bed, so ground below it read as that made-up bed); the open-sea bed stands in only where there is none.
        var bedY = OpenSeaBedAt(flat);
        var top = new Vector3(point.X, LevelM + RoomWater.BedLookAboveM, point.Z);
        // The exclusions go to Create itself and are freed here (S12).
        var excluded = new Godot.Collections.Array<Rid>();
        using var excludedArray = (Godot.Collections.Array)excluded;
        if (exclude.IsValid) excluded.Add(exclude);
        using var query = PhysicsRayQueryParameters3D.Create(top, new Vector3(point.X, LevelM - RoomWater.BedSearchM, point.Z), RoomBuilder.WorldLayer, excluded);
        query.HitBackFaces = false;
        var real = false;
        using (var hit = space.IntersectRay(query))
            if (hit.Count > 0) { bedY = hit[HitPosition].AsVector3().Y; real = true; }
        // A body already in the sea under a roof (a shelf): the bed is what lies under the body (Codex's reviews).
        if (throughRoof && real && bedY > point.Y + RoomWater.BedToleranceM && bedY <= LevelM - RoomWater.RimM)
        {
            query.From = new Vector3(point.X, point.Y + RoomWater.BedToleranceM, point.Z);
            using var under = space.IntersectRay(query);
            (bedY, real) = under.Count > 0 ? (under[HitPosition].AsVector3().Y, true) : (OpenSeaBedAt(flat), false);
        }
        if (bedY > LevelM - RoomWater.RimM) return RoomWater.Column.Dry;
        // The open-sea bed has no collider: a body under it (off a deep ledge) is still in the sea, and the bed holds it up.
        if (real && bedY > point.Y + RoomWater.BedToleranceM) return RoomWater.Column.Dry;
        return new RoomWater.Column(true, LevelM, bedY);
    }

    /// <summary>The beach nearest a point (null when the sea lists none).</summary>
    public Beach? NearestBeach(Vector2 point)
    {
        Beach? best = null;
        var bestDistance = float.PositiveInfinity;
        foreach (var beach in Beaches)
        {
            var distance = new Vector2(beach.WashAshoreM.X, beach.WashAshoreM.Z).DistanceTo(point);
            if (distance < bestDistance) (best, bestDistance) = (beach, distance);
        }
        return best;
    }

    /// <summary>
    /// The part of the room the walkable map needs: the coast's box (and a margin for the beaches' wet edge), from just under
    /// the sea's level to the room's top. The sea floor is no walk: the Gubble floats over the water and swimmers swim.
    /// </summary>
    public Aabb LandBounds(Aabb room, float marginM = 0.15f)
    {
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (var p in Coast) { minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X); minZ = Mathf.Min(minZ, p.Y); maxZ = Mathf.Max(maxZ, p.Y); }
        var low = new Vector3(Mathf.Max(room.Position.X, minX - marginM), Mathf.Max(room.Position.Y, LevelM - 0.03f), Mathf.Max(room.Position.Z, minZ - marginM));
        var high = new Vector3(Mathf.Min(room.End.X, maxX + marginM), room.End.Y, Mathf.Min(room.End.Z, maxZ + marginM));
        return high.X > low.X && high.Y > low.Y && high.Z > low.Z ? new Aabb(low, high - low) : room;
    }

    private static Vector2 Middle(Vector2[] loop)
    {
        var low = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var high = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var p in loop) { low = new Vector2(Mathf.Min(low.X, p.X), Mathf.Min(low.Y, p.Y)); high = new Vector2(Mathf.Max(high.X, p.X), Mathf.Max(high.Y, p.Y)); }
        return (low + high) * 0.5f;
    }

    /// <summary>Even-odd point in a closed loop.</summary>
    public static bool Inside(Vector2[] loop, Vector2 point)
    {
        var inside = false;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            var (a, b) = (loop[i], loop[j]);
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The nearest point of a closed loop's edges to a point, and how far it is.</summary>
    public static (float Distance, Vector2 Point) Nearest(Vector2[] loop, Vector2 point)
    {
        var best = (Distance: float.PositiveInfinity, Point: point);
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            var (a, b) = (loop[j], loop[i]);
            var ab = b - a;
            var length = ab.LengthSquared();
            var t = length > 1e-12f ? Mathf.Clamp((point - a).Dot(ab) / length, 0, 1) : 0;
            var on = a + ab * t;
            var distance = on.DistanceTo(point);
            if (distance < best.Distance) best = (distance, on);
        }
        return best;
    }

    // ---- checked reading ----

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new RoomLoadException(message);
    }

    private static JsonElement Obj(JsonElement element, string name)
    {
        Expect(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object, $"{ExtensionName}: missing object '{name}'");
        return value;
    }

    private static IEnumerable<JsonElement> Records(JsonElement element, string name, int most)
    {
        Expect(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= most,
            $"{ExtensionName}: '{name}' must be a list of at most {most}");
        foreach (var item in value.EnumerateArray())
        {
            Expect(item.ValueKind == JsonValueKind.Object, $"{ExtensionName}: an entry of '{name}' is not an object");
            yield return item;
        }
    }

    private static float Num(JsonElement element, string name, float low, float high)
    {
        Expect(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number, $"{ExtensionName}: missing number '{name}'");
        var number = value.GetDouble();
        Expect(double.IsFinite(number) && number >= low && number <= high, $"{ExtensionName}: '{name}' must be a finite number from {low} to {high}");
        return (float)number;
    }

    private static float[] Vec(JsonElement element, string name, int size)
    {
        Expect(element.TryGetProperty(name, out var value), $"{ExtensionName}: missing '{name}'");
        return Point(value, size);
    }

    private static float[] Point(JsonElement value, int size)
    {
        Expect(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == size, $"{ExtensionName}: a point must have {size} numbers");
        var numbers = new float[size];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            Expect(item.ValueKind == JsonValueKind.Number, $"{ExtensionName}: a point holds a non-number");
            var number = item.GetDouble();
            Expect(double.IsFinite(number) && Math.Abs(number) <= RoomData.CoordinateLimitM, $"{ExtensionName}: coordinates must be finite and within ±{RoomData.CoordinateLimitM} m");
            numbers[i++] = (float)number;
        }
        return numbers;
    }

    private static Vector2[] Loop(JsonElement element, string name)
    {
        Expect(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array, $"{ExtensionName}: missing loop '{name}'");
        var count = value.GetArrayLength();
        Expect(count >= 3 && count <= MaxOutlinePoints, $"{ExtensionName}: a loop must have 3 to {MaxOutlinePoints} points");
        return value.EnumerateArray().Select(p => { var xz = Point(p, 2); return new Vector2(xz[0], xz[1]); }).ToArray();
    }
}
