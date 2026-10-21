using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>Where a derived camera stands the avatars and which of the HUD's views frames them (1: F2, the shoulder).</summary>
public sealed record DerivedCamera(string Id, string Label, string Purpose, Vector3 PlayerM, float YawDeg, Vector3 CompanionM, int HudView = 1)
{
    /// <summary>A flight: the camera moves from FromM to ToM at SpeedMps, looking along its way and LookDownDeg below the horizon.</summary>
    public (Vector3 FromM, Vector3 ToM, float SpeedMps, float LookDownDeg)? Flight { get; init; }
}

/// <summary>
/// The look suite's island cameras worked out from the island's own data (S9, the 10x world), so the same cameras file works on any
/// island at any scale: the 1x garage, the 10x garage and every island after them. Fixed world coordinates only fitted one export.
/// Every camera is a play view (the HUD's F2 over the shoulder) with the player standing somewhere the data names:
/// <list type="bullet">
/// <item><b>spawn</b>: at the player's spawn, facing the island's middle (the spawn's own heading often faces the sea);</item>
/// <item><b>summit</b>: on the highest ground inside the coast that no tree's crown overhangs within SummitClearM (the copies' rows
/// say where every tree stands and how wide it grows) and with open sky over it, facing the farthest land: the long view across
/// the island;</item>
/// <item><b>hamlet</b>: outside the densest cluster of structures (cottages keep their size at every scale, so the cluster's reach
/// is in metres: StructureClusterM), on its side toward the island's middle, facing its centre;</item>
/// <item><b>beach</b>: on the beach nearest the spawn, facing along the coast (sea on one hand, land on the other);</item>
/// <item><b>flight</b>: a camera flying over the island from the spawn's side to the far side at the summit's height plus a few
/// metres: what moving across the land costs (distance bands switching, tiles arriving), for the frame-time record.</item>
/// </list>
/// The ground and the open sky come from samplers (the harness casts rays at the built room); the derivation itself is pure.
/// </summary>
public static class IslandCameras
{
    public static readonly string[] Ids = { "spawn", "summit", "hamlet", "beach", "flight" };
    /// <summary>Structures within this of each other are one cluster: a hamlet's cottages stand a few body lengths apart.</summary>
    public const float StructureClusterM = 2.0f;
    /// <summary>How far outside its cluster the player stands to see the hamlet: a few body lengths.</summary>
    public const float HamletStandOffM = 0.5f;
    /// <summary>The companion stands this far to the player's right and a little ahead, so both show in F2.</summary>
    public const float CompanionBesideM = 0.15f;
    /// <summary>How many ground samples across the land's longer side the summit search takes.</summary>
    public const int SummitSamples = 96;
    /// <summary>The flight's speed (a fast run across the island) and its height above the summit.</summary>
    public const float FlightMps = 4f, FlightAboveSummitM = 3f, FlightLookDownDeg = 12f;
    /// <summary>The summit's open sky is checked at the spot and this far round it (where the shoulder camera and the Gubble stand).</summary>
    public const float SummitClearM = 0.8f;

    /// <summary>The land's box on the ground plane: the coast's box when the island has a sea, else the room's.</summary>
    public static Rect2 LandRect(RoomData room)
    {
        if (room.Sea is { } sea)
        {
            var land = sea.LandBounds(room.Bounds, 0f);
            return new Rect2(land.Position.X, land.Position.Z, land.Size.X, land.Size.Z);
        }
        return new Rect2(room.Bounds.Position.X, room.Bounds.Position.Z, room.Bounds.Size.X, room.Bounds.Size.Z);
    }

    /// <summary>The island's middle: the coast's own middle, else the land box's.</summary>
    public static Vector2 Middle(RoomData room) => room.Sea?.MiddleM ?? LandRect(room).GetCenter();

    /// <summary>Whether a point of the ground plane is land: inside the coast when there is one.</summary>
    public static bool OnLand(RoomData room, Vector2 point) => room.Sea is not { } sea || RoomSea.Inside(sea.Coast, point);

    /// <summary>The heading (Godot's yaw, degrees: 0 faces -Z) that faces from one ground point toward another.</summary>
    public static float YawToward(Vector2 from, Vector2 to)
    {
        var d = to - from;
        return d.LengthSquared() < 1e-8f ? 0f : Mathf.RadToDeg(Mathf.Atan2(-d.X, -d.Y));
    }

    /// <summary>The ground samples of the summit search: a grid over the land, every point on land with ground under it.</summary>
    public static List<Vector3> SampleLand(RoomData room, Func<Vector2, Vector3?> ground)
    {
        var rect = LandRect(room);
        var step = Mathf.Max(rect.Size.X, rect.Size.Y) / SummitSamples;
        var samples = new List<Vector3>();
        if (!(step > 0f)) return samples;
        for (var x = rect.Position.X + step * 0.5f; x < rect.End.X; x += step)
            for (var z = rect.Position.Y + step * 0.5f; z < rect.End.Y; z += step)
            {
                var point = new Vector2(x, z);
                if (!OnLand(room, point) || ground(point) is not { } hit) continue;
                if (room.Sea is { } sea && hit.Y < sea.LevelM) continue;
                samples.Add(hit);
            }
        return samples;
    }

    /// <summary>Every derived camera this island has (one with no beach or no structures leaves that camera out, and says why in notes).</summary>
    public static List<DerivedCamera> Derive(RoomData room, Func<Vector2, Vector3?> ground, Func<Vector3, bool> openSky, List<string> notes)
    {
        var cameras = new List<DerivedCamera>();
        var middle = Middle(room);
        var spawn = room.SpawnFor("player").PositionM;
        var spawnFlat = new Vector2(spawn.X, spawn.Z);
        cameras.Add(Staged("spawn", "The spawn", "Where the player arrives, facing the island's middle: the land, its trees and hills, the far shore and the sky, at the play view (F2).",
            spawn, YawToward(spawnFlat, middle)));

        var samples = SampleLand(room, ground);
        if (samples.Count == 0) notes.Add("summit: no ground found on the land");
        else
        {
            var crowns = Crowns(room);
            bool Clear(Vector3 at) => openSky(at) && !crowns.Any(c => c.Centre.DistanceTo(new Vector2(at.X, at.Z)) < c.RadiusM + SummitClearM);
            var clear = samples.OrderByDescending(s => s.Y).Take(Math.Max(256, samples.Count / 10)).Where(Clear).Select(s => (Vector3?)s).FirstOrDefault();
            if (clear == null) notes.Add("summit: no open sky on the highest ground; the highest point is used");
            var summit = clear ?? samples.MaxBy(s => s.Y);
            var summitFlat = new Vector2(summit.X, summit.Z);
            var farthest = samples.MaxBy(s => new Vector2(s.X, s.Z).DistanceSquaredTo(summitFlat));
            cameras.Add(Staged("summit", "The summit", $"On the highest ground ({summit.Y:0.##} m up), facing the farthest land: the long view across the island, where the far plane, the haze, far trees and far terrain show.",
                summit, YawToward(summitFlat, new Vector2(farthest.X, farthest.Z))));
            var start = new Vector2(spawn.X, spawn.Z);
            var end = new Vector2(farthest.X, farthest.Z);
            if (start.DistanceTo(end) < 1f) end = middle + (middle - start);
            var height = summit.Y + FlightAboveSummitM;
            cameras.Add(new DerivedCamera("flight", "A flight over the island",
                "A camera flying over the island from the spawn to the far side, above the summit: what crossing the land costs.",
                spawn, YawToward(start, end), spawn + Vector3.Right * CompanionBesideM)
            { Flight = (new Vector3(start.X, height, start.Y), new Vector3(end.X, height, end.Y), FlightMps, FlightLookDownDeg) });
        }

        var structures = room.Objects.Where(o => o.Asset.CategoryGroup == "structure").Select(o => o.PositionM).ToArray();
        if (structures.Length == 0) notes.Add("hamlet: the island has no structures");
        else
        {
            var cluster = structures.Select(s => structures.Where(o => new Vector2(o.X - s.X, o.Z - s.Z).Length() <= StructureClusterM).ToArray()).MaxBy(c => c.Length)!;
            var centre = new Vector2(cluster.Average(s => s.X), cluster.Average(s => s.Z));
            var reach = cluster.Max(s => new Vector2(s.X, s.Z).DistanceTo(centre));
            var away = middle - centre;
            if (away.LengthSquared() < 1e-6f) away = spawnFlat - centre;
            if (away.LengthSquared() < 1e-6f) away = Vector2.Down;
            var stand = centre + away.Normalized() * (reach + HamletStandOffM);
            var at = ground(stand);
            if (at is not { } standAt) notes.Add($"hamlet: no ground at {stand} beside its {cluster.Length} structures");
            else cameras.Add(Staged("hamlet", "The hamlet", $"Beside the densest cluster of structures ({cluster.Length} within {StructureClusterM} m), facing its middle: the cottages, the path and the land behind them.",
                standAt, YawToward(stand, centre)));
        }

        if (room.Sea is not { Beaches.Count: > 0 } withBeaches) notes.Add("beach: the island lists no beach");
        else
        {
            var beach = withBeaches.NearestBeach(spawnFlat)!.Value;
            var sand = new Vector2(beach.WashAshoreM.X, beach.WashAshoreM.Z);
            // Along the coast: the coast point about a quarter of the island's span away, on the side that keeps the sea on the left.
            var span = LandRect(room).Size.Length();
            var coast = withBeaches.Coast;
            var along = coast.Length == 0 ? sand + Vector2.Right : coast.MinBy(p => Mathf.Abs(p.DistanceTo(sand) - 0.25f * span));
            var at = ground(sand) ?? beach.WashAshoreM;
            cameras.Add(Staged("beach", "The beach", "On the beach nearest the spawn, facing along the coast: the sand, the sea's edge and foam, the open sea to the horizon on one hand and the land on the other.",
                at, YawToward(sand, along)));
        }
        return cameras.OrderBy(c => Array.IndexOf(Ids, c.Id)).ToList();
    }

    /// <summary>Where every tree's crown stands on the ground plane and how far it spreads: its row's place and its mesh's half-width times its scale.</summary>
    public static List<(Vector2 Centre, float RadiusM)> Crowns(RoomData room)
    {
        var crowns = new List<(Vector2, float)>();
        foreach (var set in room.Copies?.Sets.Where(s => s.Kind == PlantKind.Tree) ?? Enumerable.Empty<PlantSet>())
        {
            var half = 0.5f * Mathf.Max(set.MeshBounds.Size.X, set.MeshBounds.Size.Z);
            for (var row = 0; row < set.Count; row++)
            {
                var at = set.Position(row);
                var scale = set.Scale(row);
                crowns.Add((new Vector2(at.X, at.Z), half * Mathf.Max(scale.X, scale.Z)));
            }
        }
        return crowns;
    }

    /// <summary>The ground under a point of a built room: the first shell ground part a ray down meets, past trunks, stones and things.</summary>
    public static Func<Vector2, Vector3?> RayGround(PhysicsDirectSpaceState3D space, Aabb bounds) => point =>
    {
        var exclude = new Godot.Collections.Array<Rid>();
        var from = new Vector3(point.X, bounds.End.Y + 1f, point.Y);
        var to = new Vector3(point.X, bounds.Position.Y - 1f, point.Y);
        for (var tries = 0; tries < 8; tries++)
        {
            using var query = PhysicsRayQueryParameters3D.Create(from, to, RoomBuilder.WorldLayer, exclude);
            using var hit = space.IntersectRay(query);
            if (hit.Count == 0) return null;
            if (hit["collider"].AsGodotObject() is Node node && node.HasMeta("surface_role") && node.GetMeta("surface_role").AsString() == "ground"
                && node.HasMeta("entity_id") && node.GetMeta("entity_id").AsString().StartsWith("shell:", StringComparison.Ordinal))
                return hit["position"].AsVector3();
            exclude.Add(hit["rid"].AsRid());
        }
        return null;
    };

    /// <summary>
    /// Whether nothing (a crown's caps, a trunk, a thing) stands over a ground point within 8 m: the sky is open above it. The ray
    /// comes down from above, since a crown's caps are one-sided and face up: a ray from below passes through them.
    /// </summary>
    public static Func<Vector3, bool> RaySky(PhysicsDirectSpaceState3D space) => at =>
    {
        using var query = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 8f, at + Vector3.Up * 0.12f, RoomBuilder.BodyMask);
        using var hit = space.IntersectRay(query);
        return hit.Count == 0;
    };

    private static DerivedCamera Staged(string id, string label, string purpose, Vector3 player, float yawDeg)
    {
        var yaw = Mathf.DegToRad(yawDeg);
        var forward = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
        var right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
        return new DerivedCamera(id, label, purpose, player, yawDeg, player + right * CompanionBesideM + forward * 0.05f + Vector3.Up * 0.02f);
    }
}
