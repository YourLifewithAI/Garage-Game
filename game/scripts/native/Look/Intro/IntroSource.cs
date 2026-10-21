using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GarageGame.Native.Room;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Native.Look.Intro;

/// <summary>The source room could not be read for the intro. The message names the rule; the intro is then skipped, never the room.</summary>
public sealed class IntroSourceException(string message) : Exception(message);

/// <summary>A shell polygon of the source room (its floor, a wall or its ceiling), in the island's metres.</summary>
public sealed record IntroPolygon(string Id, string Role, Vector3[] Points, float ThicknessM);

/// <summary>An opening in a source wall (a door, a window): a hole in the intro's wall.</summary>
public sealed record IntroOpening(string Id, string Kind, string HostId, Vector3 CentreM, Vector2 SizeM);

/// <summary>
/// A posed box of the source room's inventory (a couch, a box, the table): its bottom centre, its size, its heading about +Y and its
/// main colour (sRGB, as the scan saw it).
/// </summary>
public sealed record IntroBox(string Id, Vector3 BottomM, Vector3 SizeM, float YawRad, Color Colour)
{
    public float TopM => BottomM.Y + SizeM.Y;

    /// <summary>Whether the box's footprint covers a point on the ground (its own axes turned by its heading).</summary>
    public bool Covers(float x, float z)
    {
        float dx = x - BottomM.X, dz = z - BottomM.Z;
        float cos = MathF.Cos(YawRad), sin = MathF.Sin(YawRad);
        var along = dx * cos - dz * sin;
        var across = dx * sin + dz * cos;
        return MathF.Abs(along) <= SizeM.X * 0.5f && MathF.Abs(across) <= SizeM.Z * 0.5f;
    }

    /// <summary>The footprint's bounds on the ground (x0, z0, x1, z1).</summary>
    public (float X0, float Z0, float X1, float Z1) FootprintBounds()
    {
        float cos = MathF.Abs(MathF.Cos(YawRad)), sin = MathF.Abs(MathF.Sin(YawRad));
        var halfX = 0.5f * (SizeM.X * cos + SizeM.Z * sin);
        var halfZ = 0.5f * (SizeM.X * sin + SizeM.Z * cos);
        return (BottomM.X - halfX, BottomM.Z - halfZ, BottomM.X + halfX, BottomM.Z + halfZ);
    }
}

/// <summary>
/// The room the island grew from, as the intro shows it: the manifest's extensions.x_landscape_source, which the landscape exporter
/// writes for the intro (pipeline/landscape/export/source.py: the C3 shell's floor, walls and ceiling as polygons, their openings, and
/// the C4 inventory's posed boxes with their colours; no labels). In the 10x world the exporter writes it magnified, so the room and
/// the island share coordinates.
///
/// The room's manifest is untrusted data: it is read again from the room's folder, its bytes must hash to the manifest the room was
/// loaded from (RoomData.ManifestSha256), and every number is checked finite and bounded, every list bounded, as the exporter writes
/// them. Anything else fails closed (IntroSourceException): the intro is skipped and the room plays as always.
/// </summary>
public sealed class IntroSource
{
    public const string ExtensionName = "x_landscape_source";
    public const int MaxParts = 128, MaxPoints = 64, MaxOpenings = 64, MaxBoxes = 20_000;
    public const float CoordinateLimitM = RoomData.CoordinateLimitM;
    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}\\z", RegexOptions.Compiled);
    /// <summary>The floor's colour when the room says none (the source room carries no shell colours): a warm grey, a garage's concrete.</summary>
    public static readonly Color DefaultFloorColour = new("a49d91");
    /// <summary>A box's colour when the scan saw none.</summary>
    public static readonly Color DefaultBoxColour = new("b9ad98");

    public Aabb Bounds { get; init; }
    public IReadOnlyList<IntroPolygon> Floors { get; init; } = Array.Empty<IntroPolygon>();
    public IReadOnlyList<IntroPolygon> Walls { get; init; } = Array.Empty<IntroPolygon>();
    public IReadOnlyList<IntroPolygon> Ceilings { get; init; } = Array.Empty<IntroPolygon>();
    public IReadOnlyList<IntroOpening> Openings { get; init; } = Array.Empty<IntroOpening>();
    public IReadOnlyList<IntroBox> Boxes { get; init; } = Array.Empty<IntroBox>();

    /// <summary>The floor's height: its lowest point, or the bounds' floor when the source has no floor polygon.</summary>
    public float FloorY => Floors.Count > 0 ? Floors.Min(f => f.Points.Min(p => p.Y)) : Bounds.Position.Y;
    /// <summary>The ceiling's height: its lowest point, or the bounds' top.</summary>
    public float CeilingY => Ceilings.Count > 0 ? Ceilings.Min(c => c.Points.Min(p => p.Y)) : Bounds.End.Y;

    /// <summary>
    /// The source of a loaded room, read again from its folder (off the main thread: a 10x manifest is about 1 MB). Null when the
    /// room has no source (a captured room, the test room); throws IntroSourceException when the file changed or the source breaks a rule.
    /// </summary>
    public static IntroSource? Load(RoomData room)
    {
        var path = room.Directory + "/room.json";
        if (!FileAccess.FileExists(path)) throw new IntroSourceException($"the room's manifest is gone ({path})");
        var bytes = FileAccess.GetFileAsBytes(path);
        if (RoomData.Sha256Hex(bytes) != room.ManifestSha256) throw new IntroSourceException("the room's manifest changed since it was loaded");
        return FromManifest(bytes);
    }

    /// <summary>The source in a manifest's bytes, or null when it has none.</summary>
    public static IntroSource? FromManifest(byte[] manifest)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException error) { throw new IntroSourceException("the manifest is not valid JSON: " + error.Message); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object ||
                !extensions.TryGetProperty(ExtensionName, out var source)) return null;
            return Parse(source);
        }
    }

    /// <summary>Read one x_landscape_source record (the exporter's shape), checked as described on the class.</summary>
    public static IntroSource Parse(JsonElement source)
    {
        Expect(source.ValueKind == JsonValueKind.Object, "the source is not an object");
        var bounds = Field(source, "bounds");
        var low = Vec3(Field(bounds, "min_m"));
        var high = Vec3(Field(bounds, "max_m"));
        Expect(low.X < high.X && low.Y <= high.Y && low.Z < high.Z, "the source's bounds are empty");
        var shell = Field(source, "shell");
        var parts = List(Field(shell, "parts"), MaxParts, "shell parts");
        var polygons = new List<IntroPolygon>();
        foreach (var part in parts)
        {
            var role = Text(Field(part, "role"));
            if (role is not ("floor" or "wall" or "ceiling")) continue;
            var geometry = Field(part, "geometry");
            Expect(Text(Field(geometry, "kind")) == "polygon", "a source shell part is not a polygon");
            var points = List(Field(geometry, "points_m"), MaxPoints, "polygon points").Select(Vec3).ToArray();
            Expect(points.Length >= 3, "a source polygon has fewer than three points");
            var thickness = Number(Field(geometry, "thickness_m"), 0, 1000);
            Expect(thickness > 0, "a source polygon's thickness is not positive");
            polygons.Add(new IntroPolygon(Text(Field(part, "id")), role, points, (float)thickness));
        }
        var openings = new List<IntroOpening>();
        if (shell.TryGetProperty("openings", out var openingList))
            foreach (var opening in List(openingList, MaxOpenings, "openings"))
            {
                var size = List(Field(opening, "size_m"), 2, "an opening's size").Select(v => (float)Number(v, 0, CoordinateLimitM)).ToArray();
                Expect(size.Length == 2 && size[0] > 0 && size[1] > 0, "an opening's size is not two positive numbers");
                openings.Add(new IntroOpening(Text(Field(opening, "id")), Text(Field(opening, "kind")), Text(Field(opening, "host_part_id")),
                    Vec3(Field(opening, "center_m")), new Vector2(size[0], size[1])));
            }
        var boxes = new List<IntroBox>();
        foreach (var item in List(Field(source, "objects"), MaxBoxes, "objects"))
        {
            var size = Vec3(Field(item, "size_m"));
            Expect(size.X > 0 && size.Y > 0 && size.Z > 0, "a source box's size is not positive");
            boxes.Add(new IntroBox(Text(Field(item, "id")), Vec3(Field(item, "position_m")), size, Yaw(item), Colour(item)));
        }
        return new IntroSource
        {
            Bounds = new Aabb(low, high - low),
            Floors = polygons.Where(p => p.Role == "floor").ToArray(),
            Walls = polygons.Where(p => p.Role == "wall").ToArray(),
            Ceilings = polygons.Where(p => p.Role == "ceiling").ToArray(),
            Openings = openings,
            Boxes = boxes,
        };
    }

    /// <summary>A box's heading: its rotation's (a unit quaternion, x y z w) when it has one, else its yaw_deg.</summary>
    private static float Yaw(JsonElement item)
    {
        if (item.TryGetProperty("rotation", out var rotation))
        {
            var q = List(rotation, 4, "a rotation").Select(v => (float)Number(v, -1, 1)).ToArray();
            Expect(q.Length == 4, "a rotation is not four numbers");
            var quaternion = new Quaternion(q[0], q[1], q[2], q[3]);
            Expect(MathF.Abs(quaternion.Length() - 1f) <= 1e-3f, "a rotation is not a unit quaternion");
            // The heading of the box's own z axis on the ground.
            var z = new Basis(quaternion.Normalized()).Z;
            return MathF.Atan2(z.X, z.Z);
        }
        if (item.TryGetProperty("yaw_deg", out var yaw)) return Mathf.DegToRad((float)Number(yaw, -360, 360));
        throw new IntroSourceException("a source box has neither a rotation nor a yaw");
    }

    /// <summary>A box's main colour: the first of its colours (the exporter sorts them by share, largest first).</summary>
    private static Color Colour(JsonElement item)
    {
        if (!item.TryGetProperty("colours", out var colours)) return DefaultBoxColour;
        var list = List(colours, 3, "colours");
        if (list.Count == 0) return DefaultBoxColour;
        var hex = Text(Field(list[0], "hex"));
        Expect(Hex.IsMatch(hex), "a source colour is not #rrggbb");
        return new Color(hex);
    }

    private static JsonElement Field(JsonElement element, string name)
    {
        Expect(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value), $"the source has no '{name}'");
        return element.GetProperty(name);
    }

    private static List<JsonElement> List(JsonElement element, int limit, string what)
    {
        Expect(element.ValueKind == JsonValueKind.Array, $"the source's {what} is not a list");
        Expect(element.GetArrayLength() <= limit, $"the source has more than {limit} {what}");
        return element.EnumerateArray().ToList();
    }

    private static string Text(JsonElement element)
    {
        Expect(element.ValueKind == JsonValueKind.String, "a source field is not text");
        var text = element.GetString()!;
        Expect(text.Length <= 128, "a source text is too long");
        return text;
    }

    private static double Number(JsonElement element, double low, double high)
    {
        Expect(element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) && value >= low && value <= high,
            string.Create(CultureInfo.InvariantCulture, $"a source number is not finite within {low} to {high}"));
        return element.GetDouble();
    }

    private static Vector3 Vec3(JsonElement element)
    {
        var values = List(element, 3, "vector").Select(v => (float)Number(v, -CoordinateLimitM, CoordinateLimitM)).ToArray();
        Expect(values.Length == 3, "a source vector is not three numbers");
        return new Vector3(values[0], values[1], values[2]);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new IntroSourceException(message);
    }
}
