using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look.Intro;
using GarageGame.Native.Room;
using GarageGame.Native.Saves;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Native.Ui;

/// <summary>
/// One island the menu offers (docs/game/MECHANICS.md section 3, "Where to?"): a room whose folder holds a manifest. The menu reads
/// only what a card shows: the room's id and name, whether its manifest marks it a tutorial (<c>x_island_tutorial</c>), whether it
/// carries the shape data a thumbnail is drawn from (<c>x_landscape_source</c>), and the manifest's hash, whose first sixteen hex
/// digits name this island's saves (CommandHost.SavePathFor). Every string in a manifest is untrusted: the name is cleaned the way
/// the game cleans display text.
/// </summary>
public sealed record IslandEntry(string RoomId, string DisplayName, string Directory, string ManifestPrefix, bool Tutorial, bool Shipped, bool HasShape)
{
    /// <summary>The longest island name a card shows (the contract's display_text cap for a name).</summary>
    public const int MaxNameLength = 40;

    /// <summary>The shape data a thumbnail is drawn from, read again from the manifest (null when the room has none or it does not read).</summary>
    public IntroSource? ReadShape()
    {
        if (!HasShape) return null;
        try { return IntroSource.FromManifest(FileAccess.GetFileAsBytes(Directory + "/room.json")); }
        catch (IntroSourceException) { return null; }
    }
}

/// <summary>
/// Where the menu looks for islands and how it reads them: the player's own rooms first (user://rooms, where the exporter installs
/// an island), then the rooms shipped with the game (res://rooms), as RoomWorld.ResolveRoom resolves a bare id. A room id found in
/// both is the player's. The order the menu lists them in: tutorial islands first (none is locked), then the player's, then the
/// shipped stand-ins, each by name.
/// </summary>
public static class IslandCatalog
{
    public static readonly string[] DefaultRoots = { "user://rooms", "res://rooms" };
    /// <summary>A manifest larger than this is not read for a card (the largest island's is about a megabyte).</summary>
    public const int MaxManifestBytes = 64 * 1024 * 1024;
    public const int MaxIslands = 64;

    /// <summary>Test seam: the roots the menu scans (null: the defaults).</summary>
    public static IReadOnlyList<string>? RootsOverride { get; set; }

    public static IReadOnlyList<IslandEntry> Scan(IEnumerable<string>? roots = null)
    {
        var found = new List<IslandEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots ?? RootsOverride ?? DefaultRoots)
        {
            using var directory = DirAccess.Open(root);
            if (directory == null) continue;
            var names = directory.GetDirectories().Where(n => !n.StartsWith('.')).OrderBy(n => n, StringComparer.Ordinal).ToList();
            foreach (var name in names)
            {
                if (found.Count >= MaxIslands) break;
                if (Read(root.TrimEnd('/') + "/" + name, shipped: root.StartsWith("res://", StringComparison.Ordinal)) is { } entry && seen.Add(entry.RoomId))
                    found.Add(entry);
            }
        }
        return found.OrderBy(e => e.Tutorial ? 0 : 1).ThenBy(e => e.Shipped ? 1 : 0).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One room folder's card, or null when it is not a room the game can save (no manifest, not readable, an id that is not one).</summary>
    public static IslandEntry? Read(string directory, bool shipped)
    {
        var path = directory + "/room.json";
        if (!FileAccess.FileExists(path)) return null;
        var bytes = FileAccess.GetFileAsBytes(path);
        if (bytes.Length == 0 || bytes.Length > MaxManifestBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("room_id", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var roomId = id.GetString()!;
            if (!TeamLayout.ValidRoomId(roomId)) return null;
            var name = root.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() ?? "" : "";
            var shown = KernelText.DisplayText(name, IslandEntry.MaxNameLength);
            if (shown.Length == 0 || shown == "Refused.") shown = roomId;
            var tutorial = false;
            var shape = false;
            if (root.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object)
            {
                tutorial = extensions.TryGetProperty("x_island_tutorial", out var flag) && flag.ValueKind == JsonValueKind.True;
                shape = extensions.TryGetProperty(IntroSource.ExtensionName, out var source) && source.ValueKind == JsonValueKind.Object;
            }
            return new IslandEntry(roomId, shown, directory, RoomData.Sha256Hex(bytes)[..16], tutorial, shipped, shape);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>What a team has on one island in one mode: the save's file, when it was last written and, in Challenge, the difficulty it keeps.</summary>
public sealed record IslandSaveInfo(string Mode, bool Exists, DateTime? WrittenUtc, string? Difficulty, string Path)
{
    /// <summary>The team's saves on an island, one per playable mode (Creative, Challenge), for the island's current manifest.</summary>
    public static IReadOnlyList<IslandSaveInfo> Of(TeamLayout layout, string team, IslandEntry island) =>
        new[] { "creative", "challenge" }.Select(mode => One(layout, team, island, mode)).ToList();

    public static IslandSaveInfo One(TeamLayout layout, string team, IslandEntry island, string mode)
    {
        var path = layout.IslandSave(team, mode, island.RoomId, island.ManifestPrefix);
        if (!System.IO.File.Exists(path)) return new IslandSaveInfo(mode, false, null, null, path);
        return new IslandSaveInfo(mode, true, System.IO.File.GetLastWriteTimeUtc(path), mode == "challenge" ? ReadDifficulty(path) : null, path);
    }

    /// <summary>The difficulty a Challenge save keeps (its team block, room state's x_team); null when it says none or does not read.</summary>
    private static string? ReadDifficulty(string path)
    {
        // The menu asks again after every click; a save is parsed once per version of the file.
        var stamp = (System.IO.File.GetLastWriteTimeUtc(path).Ticks, new System.IO.FileInfo(path).Length);
        lock (Kept)
        {
            if (Kept.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Difficulty;
        }
        var read = ReadDifficultyNow(path);
        lock (Kept)
        {
            if (Kept.Count > 256) Kept.Clear();
            Kept[path] = (stamp, read);
        }
        return read;
    }

    private static readonly Dictionary<string, ((long, long) Stamp, string? Difficulty)> Kept = new(StringComparer.Ordinal);

    private static string? ReadDifficultyNow(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            if (bytes.Length > 8 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("team", out var team) && team.ValueKind == JsonValueKind.Object &&
                team.TryGetProperty("difficulty", out var difficulty) && difficulty.ValueKind == JsonValueKind.String &&
                difficulty.GetString() is "gentle" or "standard" or "brave")
                return difficulty.GetString();
        }
        catch (Exception error) when (error is JsonException or System.IO.IOException or UnauthorizedAccessException) { }
        return null;
    }
}
