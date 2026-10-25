using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Native.Room;

/// <summary>A room.json could not be loaded. The message is safe to show and names the failing rule.</summary>
public sealed class RoomLoadException(string message) : Exception(message);

/// <summary>A shell part. Drawn false (the manifest's optional "drawn", default true) makes it collision-only: a climbing pole hidden in a tree's leaves, a cap to stand on.</summary>
public sealed record ShellPart(
    string Id, string Role, Vector3[] Points, float ThicknessM, string? MeshPath,
    bool Collides, string MaterialRole, Color BaseColor, bool Drawn = true);

public sealed record MaterialSlot(string Slot, string Role, Color? BaseColor);

public sealed record AssetInfo(
    string AssetId, string Directory, string DisplayName, string Category, string CategoryGroup, string Tier,
    string ProvenanceKind, Vector3 DimensionsM, string GeometryKind, string? Primitive, string? MeshPath,
    string CollisionKind, string? CollisionFile, bool Movable, float MassKg, MaterialSlot[] Materials,
    string[] Affordances, string ReviewStatus);

public sealed record ObjectInstance(
    string Id, AssetInfo Asset, Vector3 PositionM, Quaternion Rotation, float Scale,
    string SupportKind, string? SupportTarget, string? DisplayName);

public sealed record SpawnPoint(string Id, string Role, Vector3 PositionM, float YawDeg);

public sealed record LightHint(string Id, string Kind, Vector3? PositionM, Vector3? Direction, Color Color, float RelativeIntensity);

public sealed record StylePin(string PresetId, int PresetVersion, string PresetSha256);

/// <summary>
/// An opening listed in shell.openings: a window, door, archway or vent in a host shell part. The builder cuts the hole
/// the opening describes through the host slab (CutsVisual) and, when a body can pass (Traversable), through its
/// collision too; a window the player cannot pass is still a hole to light and to the eye, with glass in it.
/// </summary>
public sealed record ShellOpening(string Id, string Kind, string HostPartId, Vector3 CenterM, Vector2 SizeM, string State, bool Traversable)
{
    /// <summary>Windows, archways, vents and pet doors are always open to light; a door or portal is a hole only while open.</summary>
    public bool CutsVisual => Kind is "window" or "archway" or "vent" or "pet_door" || State == "open";
    /// <summary>Only a hole a body can go through is a hole in the collision.</summary>
    public bool CutsCollision => CutsVisual && Traversable;
}

/// <summary>
/// Loads contracts/room-manifest.schema.json documents with integrity checks: every referenced file
/// must be listed with a matching SHA-256 and size, paths stay inside the room, and pinned bytes are
/// UTF-8 with LF line endings. Full schema validation is contracts/validate.py; this loader enforces
/// what the game relies on and fails closed.
/// </summary>
public sealed partial class RoomData
{
    public const int SchemaVersion = 1;
    // Same rules as contracts/common.schema.json: one spelling per file, no ".", ".." or empty segments.
    private static readonly Regex RelativePath = new(@"^(?!.*(?:^|/)\.\.?(?:/|\z))(?!.*//)(?!.*/\z)[A-Za-z0-9_][A-Za-z0-9_./-]{0,199}\z", RegexOptions.Compiled);
    private static readonly Regex AssetPath = new(@"^objects/[a-z][a-z0-9_-]{0,63}/asset\.json\z", RegexOptions.Compiled);
    // Control, line-separator, zero-width, bidirectional-override and other invisible characters cannot appear in
    // display text (contracts/common.schema.json). The four emoji markers pass this pattern and are checked in
    // context by MisplacedEmojiMarker.
    private static readonly Regex UnsafeText = new(@"[\u0000-\u001F\u007F-\u009F\u00AD\u034F\u061C\u115F\u1160\u180B-\u180F\u200B\u200C\u200E\u200F\u2028-\u202E\u2060-\u206F\u2800\u3164\uFE00-\uFE0D\uFEFF\uFFA0\uFFF9-\uFFFB\uDB40-\uDB7F]", RegexOptions.Compiled);
    // Extended_Pictographic (UTS #51, Unicode 15.1 emoji-data.txt), merged ranges: the characters emoji are made of.
    private static readonly (int Low, int High)[] ExtendedPictographic =
    {
        (0x00A9, 0x00A9), (0x00AE, 0x00AE), (0x203C, 0x203C), (0x2049, 0x2049), (0x2122, 0x2122), (0x2139, 0x2139),
        (0x2194, 0x2199), (0x21A9, 0x21AA), (0x231A, 0x231B), (0x2328, 0x2328), (0x2388, 0x2388), (0x23CF, 0x23CF),
        (0x23E9, 0x23F3), (0x23F8, 0x23FA), (0x24C2, 0x24C2), (0x25AA, 0x25AB), (0x25B6, 0x25B6), (0x25C0, 0x25C0),
        (0x25FB, 0x25FE), (0x2600, 0x2605), (0x2607, 0x2612), (0x2614, 0x2685), (0x2690, 0x2705), (0x2708, 0x2712),
        (0x2714, 0x2714), (0x2716, 0x2716), (0x271D, 0x271D), (0x2721, 0x2721), (0x2728, 0x2728), (0x2733, 0x2734),
        (0x2744, 0x2744), (0x2747, 0x2747), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757),
        (0x2763, 0x2767), (0x2795, 0x2797), (0x27A1, 0x27A1), (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2934, 0x2935),
        (0x2B05, 0x2B07), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x3030, 0x3030), (0x303D, 0x303D),
        (0x3297, 0x3297), (0x3299, 0x3299), (0x1F000, 0x1F0FF), (0x1F10D, 0x1F10F), (0x1F12F, 0x1F12F), (0x1F16C, 0x1F171),
        (0x1F17E, 0x1F17F), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F1AD, 0x1F1E5), (0x1F201, 0x1F20F), (0x1F21A, 0x1F21A),
        (0x1F22F, 0x1F22F), (0x1F232, 0x1F23A), (0x1F23C, 0x1F23F), (0x1F249, 0x1F3FA), (0x1F400, 0x1F53D), (0x1F546, 0x1F64F),
        (0x1F680, 0x1F6FF), (0x1F774, 0x1F77F), (0x1F7D5, 0x1F7FF), (0x1F80C, 0x1F80F), (0x1F848, 0x1F84F), (0x1F85A, 0x1F85F),
        (0x1F888, 0x1F88F), (0x1F8AE, 0x1F8FF), (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1FAFF), (0x1FC00, 0x1FFFD),
    };
    public const float CoordinateLimitM = 1000f;
    private static readonly Regex Token = new(@"^[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    private static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 };

    public string Directory { get; private init; } = "";
    public string RoomId { get; private init; } = "";
    public string DisplayName { get; private init; } = "";
    public string SourceKind { get; private init; } = "";
    public string ManifestSha256 { get; private init; } = "";
    public Aabb Bounds { get; private init; }
    public IReadOnlyList<ShellPart> Shell { get; private init; } = Array.Empty<ShellPart>();
    public IReadOnlyList<ObjectInstance> Objects { get; private init; } = Array.Empty<ObjectInstance>();
    public IReadOnlyList<SpawnPoint> Spawns { get; private init; } = Array.Empty<SpawnPoint>();
    public IReadOnlyList<LightHint> LightHints { get; private init; } = Array.Empty<LightHint>();
    public IReadOnlyList<ShellOpening> Openings { get; private init; } = Array.Empty<ShellOpening>();
    public StylePin? DefaultStyle { get; private init; }
    /// <summary>The island's sea (extensions.x_landscape_sea, checked on reading), or null: a room without it has no sea edge.</summary>
    public RoomSea? Sea { get; private init; }
    /// <summary>
    /// Mesh bytes exactly as hash-verified, keyed by full path, until the room is built (RoomBuilder.Build lets them go:
    /// ReleaseMeshBytes). MeshBytes gives a file's bytes either way, read and checked against the manifest again when let go.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> VerifiedMeshes => _meshes;
    private Dictionary<string, byte[]> _meshes = new();
    /// <summary>Every mesh file's manifest entry by full path (its path in the room, hash and size), to check a file read again.</summary>
    private Dictionary<string, (string Path, string Sha, long Bytes)> _meshPins = new();

    /// <summary>
    /// A mesh file's verified bytes by full path, or null when the room has no such mesh: kept from the load, or read again and
    /// checked as the load checked it (size, hash, self-contained) once the build let them go; a file that changed fails closed.
    /// </summary>
    public byte[]? MeshBytes(string path)
    {
        lock (_meshes)
        {
            if (_meshes.TryGetValue(path, out var kept)) return kept;
            if (!_meshPins.TryGetValue(path, out var pin)) return null;
            var bytes = ReadPinned(path, pin.Path);
            Expect(bytes.LongLength == pin.Bytes, $"room.json: {pin.Path} is {bytes.LongLength} bytes, files list says {pin.Bytes}");
            Expect(Sha256Hex(bytes) == pin.Sha, $"room.json: {pin.Path} hash does not match the files list");
            RequireSelfContainedGlb(bytes, $"room.json: {pin.Path}");
            _meshes[path] = bytes;
            return bytes;
        }
    }

    /// <summary>The mesh files read at load by full path, with each one's manifest entry (its path as listed, hash and size).</summary>
    private sealed class MeshFiles : Dictionary<string, byte[]>
    {
        public MeshFiles() : base(StringComparer.Ordinal) { }
        public readonly Dictionary<string, (string Path, string Sha, long Bytes)> Pins = new(StringComparer.Ordinal);
    }

    /// <summary>Keep a mesh file's verified bytes under its full path, and its entry in the files list that verified it.</summary>
    private static byte[] Keep(Dictionary<string, byte[]> meshes, string full, string path, Dictionary<string, (string Sha, long Bytes)> listed, byte[] bytes)
    {
        meshes[full] = bytes;
        if (meshes is MeshFiles files && listed.TryGetValue(path, out var pin)) files.Pins[full] = (path, pin.Sha, pin.Bytes);
        return bytes;
    }

    /// <summary>Let the mesh files' bytes go once the scene is built from them (S12b: a 10x island's are about 40 MB); MeshBytes reads them again if asked.</summary>
    public void ReleaseMeshBytes()
    {
        lock (_meshes) _meshes.Clear();
    }

    public SpawnPoint SpawnFor(string role, SpawnPoint? except = null) =>
        Spawns.FirstOrDefault(s => s.Role == role && s != except) ??
        Spawns.FirstOrDefault(s => s.Role == "any" && s != except) ??
        throw new RoomLoadException($"Room {RoomId} has no spawn for {role}.");

    public static RoomData Load(string directory)
    {
        // Everything below joins and splits on '/'; a native Windows path (System.IO.Path) arrives with '\'.
        directory = directory.Replace(System.IO.Path.DirectorySeparatorChar, '/').TrimEnd('/');
        var manifestBytes = ReadPinned(directory + "/room.json", "room.json");
        using var document = Parse(manifestBytes, "room.json");
        var root = document.RootElement;
        Expect(SchemaIds.IsSchema(Str(root, "schema"), "room") && Int(root, "version") == SchemaVersion, $"room.json is not a {SchemaIds.Both("room")} version 1 manifest");
        Expect(Str(root, "units") == "m" && Str(root, "axes") == "y_up_neg_z_forward", "room.json must use metres and Godot axes");
        var roomId = Str(root, "room_id");
        Expect(Token.IsMatch(roomId), "room_id is not a valid token");
        var folder = directory[(directory.LastIndexOf('/') + 1)..];
        Expect(folder == roomId, $"room_id {roomId} must match its directory name {folder}");

        var meshes = new MeshFiles();
        var listed = new Dictionary<string, (string Sha, long Bytes)>();
        foreach (var entry in Arr(root, "files"))
        {
            var path = SafePath(Str(entry, "path"));
            Expect(listed.TryAdd(path, (Str(entry, "sha256"), entry.GetProperty("bytes").GetInt64())), $"file {path} is listed twice");
        }
        Prefetch(directory, listed);
        var assets = new Dictionary<string, AssetInfo>();
        AssetInfo AssetAt(string path)
        {
            if (assets.TryGetValue(path, out var cached)) return cached;
            Expect(AssetPath.IsMatch(path), $"asset path {path} must be objects/<asset_id>/asset.json");
            var bytes = ReadListed(directory, listed, path, "room.json");
            var asset = ParseAsset(directory + "/" + path[..path.LastIndexOf('/')], bytes, path, meshes);
            assets[path] = asset;
            return asset;
        }

        var shell = Arr(root.GetProperty("shell"), "parts").Select(p => ParseShell(directory, listed, p, meshes)).ToArray();
        var objects = Arr(root, "objects").Select(o =>
        {
            var transform = o.GetProperty("transform");
            var support = o.GetProperty("support");
            return new ObjectInstance(
                Str(o, "id"), AssetAt(Str(o, "asset")), Vec3(transform.GetProperty("position_m")), Quat(transform.GetProperty("rotation")),
                transform.TryGetProperty("scale", out var scale) ? scale.GetSingle() : 1f,
                Str(support, "kind"), OptStr(support, "target_id"), OptDisplay(o, "display_name", 80));
        }).ToArray();
        var spawns = Arr(root, "spawns").Select(s => new SpawnPoint(Str(s, "id"), Str(s, "role"), Vec3(s.GetProperty("position_m")), s.GetProperty("yaw_deg").GetSingle())).ToArray();
        var hints = root.TryGetProperty("light_hints", out var h) ? h.EnumerateArray().Select(l => new LightHint(
            Str(l, "id"), Str(l, "kind"),
            l.TryGetProperty("position_m", out var position) ? Vec3(position) : null,
            l.TryGetProperty("direction", out var direction) ? Vec3(direction) : null,
            new Color(Str(l, "color")), l.GetProperty("relative_intensity").GetSingle())).ToArray() : Array.Empty<LightHint>();
        var openings = root.GetProperty("shell").TryGetProperty("openings", out var openingList) ? openingList.EnumerateArray().Select(o =>
        {
            var size = o.GetProperty("size_m");
            return new ShellOpening(Str(o, "id"), Str(o, "kind"), Str(o, "host_part_id"), Vec3(o.GetProperty("center_m")),
                new Vector2(size[0].GetSingle(), size[1].GetSingle()), Str(o, "state"), o.GetProperty("traversable").GetBoolean());
        }).ToArray() : Array.Empty<ShellOpening>();
        foreach (var opening in openings)
            Expect(shell.Any(part => part.Id == opening.HostPartId), $"opening {opening.Id} is hosted by {opening.HostPartId}, which is not a shell part");
        var bounds = root.GetProperty("bounds");
        var min = Vec3(bounds.GetProperty("min_m"));
        var max = Vec3(bounds.GetProperty("max_m"));
        Expect(min.X < max.X && min.Y < max.Y && min.Z < max.Z, "bounds min must be below max");
        var ids = shell.Select(s => s.Id).Concat(objects.Select(o => o.Id)).ToArray();
        Expect(ids.Length == ids.Distinct().Count(), "shell and object ids must be unique");
        // Copies burn and regrow, so nothing in the baseline rests on one (contracts/README.md, "Plant copies").
        foreach (var instance in objects)
            Expect(instance.SupportTarget?.StartsWith("plant:", StringComparison.Ordinal) != true,
                $"{instance.Id} rests on the plant copy {instance.SupportTarget}; no manifest object rests on a copy");
        Expect(spawns.Any(s => s.Role is "player" or "any") && spawns.Any(s => s.Role is "companion" or "any"), "room needs player and companion spawns");
        RoomSea? sea = null;
        if (root.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object &&
            extensions.TryGetProperty(RoomSea.ExtensionName, out var seaRecord))
        {
            // The generator's grid, which its far sea floor is measured from, is the source room's bounds grown by its margin.
            Aabb? source = null;
            if (extensions.TryGetProperty("x_landscape_source", out var origin) && origin.ValueKind == JsonValueKind.Object &&
                origin.TryGetProperty("bounds", out var sourceBounds) && sourceBounds.ValueKind == JsonValueKind.Object)
            {
                var low = Vec3(sourceBounds.GetProperty("min_m"));
                var high = Vec3(sourceBounds.GetProperty("max_m"));
                Expect(low.X < high.X && low.Y <= high.Y && low.Z < high.Z, "x_landscape_source bounds min must be below max");
                source = new Aabb(low, high - low);
            }
            sea = RoomSea.Parse(seaRecord, new Aabb(min, max - min), source);
        }
        // The 10x world (S2): plant copies and terrain tiles, checked against the bounds as the manifest writes them.
        var (lowM, highM) = (Doubles(bounds.GetProperty("min_m")), Doubles(bounds.GetProperty("max_m")));
        var copies = root.TryGetProperty("plant_copies", out var copiesBlock) ? ParsePlantCopies(copiesBlock, directory, listed, meshes, lowM, highM) : null;
        var terrain = root.TryGetProperty("terrain_tiles", out var tilesBlock) ? ParseTerrainTiles(tilesBlock, directory, shell, meshes) : null;
        StylePin? style = null;
        if (root.TryGetProperty("default_style", out var pin))
            style = new StylePin(Str(pin, "preset_id"), Int(pin, "preset_version"), Str(pin, "preset_sha256"));


        return new RoomData
        {
            Directory = directory, RoomId = roomId, DisplayName = Display(root, "display_name", 80), DefaultStyle = style, _meshes = meshes, _meshPins = meshes.Pins,
            SourceKind = Str(root.GetProperty("source"), "kind"), ManifestSha256 = Sha256Hex(manifestBytes),
            Bounds = new Aabb(min, max - min), Shell = shell, Objects = objects, Spawns = spawns, LightHints = hints, Openings = openings, Sea = sea,
            Copies = copies, Terrain = terrain,
        };
    }

    private static ShellPart ParseShell(string directory, Dictionary<string, (string Sha, long Bytes)> listed, JsonElement part, Dictionary<string, byte[]> meshes)
    {
        var geometry = part.GetProperty("geometry");
        var kind = Str(geometry, "kind");
        Vector3[] points = Array.Empty<Vector3>();
        string? mesh = null;
        var thickness = 0f;
        if (kind == "polygon")
        {
            points = Arr(geometry, "points_m").Select(Vec3).ToArray();
            thickness = geometry.GetProperty("thickness_m").GetSingle();
            Expect(points.Length >= 3 && thickness > 0, $"{Str(part, "id")} needs at least three points and a positive thickness");
        }
        else
        {
            mesh = SafePath(Str(geometry, "mesh"));
            Keep(meshes, directory + "/" + mesh, mesh, listed, ReadMesh(directory, listed, mesh, "room.json"));
        }
        if (part.TryGetProperty("texture", out var texture)) ReadListed(directory, listed, SafePath(texture.GetString()!), "room.json");
        var collides = part.GetProperty("collides").GetBoolean();
        var drawn = !part.TryGetProperty("drawn", out var shown) || shown.GetBoolean();
        Expect(drawn || collides, $"{Str(part, "id")} is neither drawn nor collides");
        return new ShellPart(Str(part, "id"), Str(part, "role"), points, thickness, mesh, collides,
            Str(part, "material_role"), part.TryGetProperty("base_color", out var color) ? new Color(color.GetString()!) : new Color("b3aea4"), drawn);
    }

    private static AssetInfo ParseAsset(string assetDirectory, byte[] bytes, string label, Dictionary<string, byte[]> meshes)
    {
        using var document = Parse(bytes, label);
        var root = document.RootElement;
        Expect(SchemaIds.IsSchema(Str(root, "schema"), "asset") && Int(root, "version") == 1, $"{label} is not a {SchemaIds.Both("asset")} version 1 document");
        Expect(Str(root, "pivot") == "bottom_center", $"{label} must use the bottom_center pivot");
        var assetId = Str(root, "asset_id");
        Expect(assetDirectory.EndsWith("/" + assetId, StringComparison.Ordinal), $"{label}: asset_id must match its directory name");
        var listed = new Dictionary<string, (string Sha, long Bytes)>();
        foreach (var entry in Arr(root, "files"))
            Expect(listed.TryAdd(SafePath(Str(entry, "path")), (Str(entry, "sha256"), entry.GetProperty("bytes").GetInt64())), $"{label} lists a file twice");
        var geometry = root.GetProperty("geometry");
        var geometryKind = Str(geometry, "kind");
        string? mesh = null, primitive = null;
        if (geometryKind == "mesh")
        {
            mesh = SafePath(Str(geometry, "mesh"));
            Keep(meshes, assetDirectory + "/" + mesh, mesh, listed, ReadMesh(assetDirectory, listed, mesh, label));
        }
        else primitive = Str(geometry, "primitive");
        var collision = root.GetProperty("collision");
        var collisionFile = OptStr(collision, "file");
        if (collisionFile != null) Keep(meshes, assetDirectory + "/" + collisionFile, SafePath(collisionFile), listed, ReadMesh(assetDirectory, listed, SafePath(collisionFile), label));
        var collisionKind = Str(collision, "kind");
        Expect(geometryKind != "mesh" || collisionKind != "primitive", $"{label}: a mesh asset cannot use primitive collision");
        var physics = root.GetProperty("physics");
        var dimensions = Vec3(root.GetProperty("dimensions_m"));
        Expect(dimensions.X > 0 && dimensions.Y > 0 && dimensions.Z > 0, $"{label} dimensions must be positive");
        var materials = Arr(root, "materials").Select(m => new MaterialSlot(Str(m, "slot"), Str(m, "role"),
            m.TryGetProperty("base_color", out var c) ? new Color(c.GetString()!) : null)).ToArray();
        return new AssetInfo(
            assetId, assetDirectory, Display(root, "display_name", 80), Display(root, "category", 60), Str(root, "category_group"), Str(root, "tier"),
            Str(root.GetProperty("provenance"), "kind"), dimensions, geometryKind, primitive, mesh,
            collisionKind, collisionFile, physics.GetProperty("movable").GetBoolean(), physics.GetProperty("mass_kg").GetSingle(),
            materials, Arr(root, "affordances").Select(a => a.GetString()!).ToArray(), Str(root.GetProperty("review"), "status"));
    }

    /// <summary>Reads a file that a manifest pins: it must be listed, and its size and hash must match.</summary>
    public static byte[] ReadListed(string directory, Dictionary<string, (string Sha, long Bytes)> listed, string path, string label)
    {
        Expect(listed.TryGetValue(path, out var pin), $"{label}: {path} is referenced but not listed in files");
        if (Prefetched.TryGetValue(listed, out var verified) && verified.TryGetValue(path, out var ready)) return ready;
        var bytes = ReadPinned(directory + "/" + path, path);
        Expect(bytes.LongLength == pin.Bytes, $"{label}: {path} is {bytes.LongLength} bytes, files list says {pin.Bytes}");
        Expect(Sha256Hex(bytes) == pin.Sha, $"{label}: {path} hash does not match the files list");
        return bytes;
    }

    /// <summary>Bytes of a room's listed files already read and verified (size and hash) by Prefetch, for that room's files list.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<string, (string Sha, long Bytes)>, System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>> Prefetched = new();

    /// <summary>
    /// A room's large pinned files (meshes and plant tables: a 10x island's are about 60 MB) read and hashed side by side before
    /// its parts ask for them in manifest order. Only bytes whose size and hash match are kept; anything else is read again where
    /// it is referenced, which reports the problem with its own message, so the rules and their messages are unchanged.
    /// </summary>
    private static void Prefetch(string directory, Dictionary<string, (string Sha, long Bytes)> listed)
    {
        var verified = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var large = listed.Where(e => e.Key.EndsWith(".glb", StringComparison.Ordinal) || e.Key.EndsWith(".bin", StringComparison.Ordinal)).ToArray();
        System.Threading.Tasks.Parallel.ForEach(large, entry =>
        {
            var full = directory + "/" + entry.Key;
            if (!FileAccess.FileExists(full)) return;
            var bytes = FileAccess.GetFileAsBytes(full);
            if (bytes.LongLength == entry.Value.Bytes && Sha256Hex(bytes) == entry.Value.Sha) verified[entry.Key] = bytes;
        });
        Prefetched.AddOrUpdate(listed, verified);
    }

    private static byte[] ReadPinned(string path, string label)
    {
        if (!FileAccess.FileExists(path)) throw new RoomLoadException($"{label} not found at {path}");
        var bytes = FileAccess.GetFileAsBytes(path);
        Expect(bytes.Length > 0 || FileAccess.GetOpenError() == Error.Ok, $"{label} could not be read ({FileAccess.GetOpenError()})");
        if (path.EndsWith(".json", StringComparison.Ordinal))
        {
            Expect(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), $"{label} must not start with a byte-order mark");
            Expect(Array.IndexOf(bytes, (byte)'\r') < 0, $"{label} must use LF line endings; pins hash the exact bytes");
        }
        return bytes;
    }

    private static byte[] ReadMesh(string directory, Dictionary<string, (string Sha, long Bytes)> listed, string path, string label)
    {
        Expect(path.EndsWith(".glb", StringComparison.Ordinal), $"{label}: mesh {path} must be a self-contained .glb");
        var bytes = ReadListed(directory, listed, path, label);
        RequireSelfContainedGlb(bytes, $"{label}: {path}");
        return bytes;
    }

    /// <summary>A pinned GLB must embed every buffer and image; an external URI would load unhashed bytes, possibly from outside the room.</summary>
    public static void RequireSelfContainedGlb(byte[] bytes, string label)
    {
        Expect(bytes.Length >= 20 && bytes[0] == (byte)'g' && bytes[1] == (byte)'l' && bytes[2] == (byte)'T' && bytes[3] == (byte)'F', $"{label} is not a binary glTF file");
        // GLB is little-endian. Lengths stay unsigned so a hostile chunk length cannot wrap negative.
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        Expect(version == 2 && length == bytes.Length, $"{label} has an invalid GLB header");
        var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) == 0x4E4F534A && 20L + chunkLength <= bytes.Length, $"{label} does not start with a JSON chunk");
        // Same tolerance as contracts/validate.py: trailing space or NUL padding is not part of the JSON.
        var json = bytes.AsSpan(20, (int)chunkLength).TrimEnd(stackalloc byte[] { 0x20, 0x00 }).ToArray();
        using var document = Parse(json, label);
        Expect(document.RootElement.ValueKind == JsonValueKind.Object, $"{label} has a GLB JSON chunk that is not an object");
        foreach (var kind in new[] { "buffers", "images" })
        {
            if (!document.RootElement.TryGetProperty(kind, out var items)) continue;
            Expect(items.ValueKind == JsonValueKind.Array, $"{label} has a GLB '{kind}' entry that is not an array");
            foreach (var item in items.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out var uri) &&
                    !(uri.ValueKind == JsonValueKind.String && uri.GetString()!.StartsWith("data:", StringComparison.Ordinal)))
                    throw new RoomLoadException($"{label} references external file '{uri}'; meshes must be self-contained");
        }
    }

    /// <summary>JsonDocument keeps the last of duplicate keys; the contract rejects them, so check first.</summary>
    private static void RejectDuplicateKeys(byte[] bytes, string label)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        var scopes = new Stack<HashSet<string>>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: scopes.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonTokenType.EndObject: scopes.Pop(); break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (!scopes.Peek().Add(name)) throw new RoomLoadException($"{label} has duplicate key '{name}'");
                        break;
                }
            }
        }
        catch (JsonException error) { throw new RoomLoadException($"{label} is not valid JSON: {error.Message}"); }
    }

    private static string Display(JsonElement element, string name, int maxLength)
    {
        var text = Str(element, name);
        Expect(text.Length <= maxLength && IsSafeText(text), $"'{name}' must be at most {maxLength} characters without control or bidirectional characters, invisible characters or misplaced emoji markers");
        return text;
    }

    /// <summary>The contract's untrusted-text rule: no hidden characters, and emoji markers only in place.</summary>
    public static bool IsSafeText(string text) => !UnsafeText.IsMatch(text) && MisplacedEmojiMarker(text) == null;

    /// <summary>
    /// The first emoji marker out of place, as U+XXXX, or null. VS15 and VS16 belong directly after an
    /// Extended_Pictographic character or a keycap base (0-9, # or *), one per base; the zero-width joiner between
    /// two emoji (the one before may carry one VS16 or skin tone); the keycap combiner U+20E3 after a keycap base,
    /// optionally with VS16. Anywhere else, alone or in runs, they could hide data. Same rule as
    /// contracts/validate.py and the companion's textsafety.py.
    /// </summary>
    public static string? MisplacedEmojiMarker(string text)
    {
        var codes = new List<int>();
        foreach (var rune in text.EnumerateRunes()) codes.Add(rune.Value);
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            if (code is not (0xFE0E or 0xFE0F or 0x200D or 0x20E3)) continue;
            var before = i > 0 ? codes[i - 1] : -1;
            bool inPlace;
            if (code is 0xFE0E or 0xFE0F)
            {
                inPlace = IsPictographic(before) || IsKeycapBase(before);
            }
            else if (code == 0x20E3)
            {
                inPlace = IsKeycapBase(before) || (before == 0xFE0F && i >= 2 && IsKeycapBase(codes[i - 2]));
            }
            else
            {
                var j = i - 1;
                if (j >= 0 && (codes[j] == 0xFE0F || codes[j] is >= 0x1F3FB and <= 0x1F3FF)) j--;
                inPlace = i + 1 < codes.Count && IsPictographic(codes[i + 1]) && j >= 0 && IsPictographic(codes[j]);
            }
            if (!inPlace) return $"U+{code:X4}";
        }
        return null;
    }

    private static bool IsKeycapBase(int code) => code is >= 0x30 and <= 0x39 or 0x23 or 0x2A;

    private static bool IsPictographic(int code)
    {
        int low = 0, high = ExtendedPictographic.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (code < ExtendedPictographic[middle].Low) high = middle - 1;
            else if (code > ExtendedPictographic[middle].High) low = middle + 1;
            else return true;
        }
        return false;
    }

    private static string? OptDisplay(JsonElement element, string name, int maxLength) =>
        element.TryGetProperty(name, out _) ? Display(element, name, maxLength) : null;

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonDocument Parse(byte[] bytes, string label)
    {
        RejectDuplicateKeys(bytes, label);
        try { return JsonDocument.Parse(bytes, Strict); }
        catch (JsonException error) { throw new RoomLoadException($"{label} is not valid JSON: {error.Message}"); }
    }

    private static string SafePath(string path)
    {
        Expect(RelativePath.IsMatch(path), $"unsafe or invalid relative path '{path}'");
        return path;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new RoomLoadException(message);
    }

    private static string Str(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new RoomLoadException($"missing or non-string field '{name}'");
        return value.GetString()!;
    }

    private static string? OptStr(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : throw new RoomLoadException($"missing or non-integer field '{name}'");

    private static IEnumerable<JsonElement> Arr(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : throw new RoomLoadException($"missing array '{name}'");

    private static Vector3 Vec3(JsonElement element)
    {
        var values = element.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Expect(values.Length == 3, "expected three numbers");
        var vector = new Vector3((float)values[0], (float)values[1], (float)values[2]);
        // Check after narrowing to float: a finite double can still overflow the runtime's float32.
        Expect(vector.IsFinite() && Mathf.Abs(vector.X) <= CoordinateLimitM && Mathf.Abs(vector.Y) <= CoordinateLimitM && Mathf.Abs(vector.Z) <= CoordinateLimitM,
            $"coordinates must be finite and within ±{CoordinateLimitM} m");
        return vector;
    }

    private static Quaternion Quat(JsonElement element)
    {
        var v = element.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Expect(v.Length == 4 && v.All(double.IsFinite), "rotation needs four finite numbers");
        var q = new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
        Expect(Mathf.Abs(q.Length() - 1f) <= 1e-3f, "rotation is not a unit quaternion");
        return q.Normalized();
    }
}
