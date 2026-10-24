using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>A save the menu could not write or read, in the player's words (never a path, never saved text).</summary>
public sealed class MenuSaveException(string message) : Exception(message);

/// <summary>
/// The last game played (the menu's Continue): which team, which island and which mode. The machine's, beside the teams
/// (teams/last_played.json), written whenever a game boots as the game (RoomWorld, so a launch with --room= counts as well as one
/// from the menu), and read strictly: a file that does not read is no last game.
/// </summary>
public sealed record LastGame(string TeamId, string RoomId, string Mode, string? Difficulty, DateTime WrittenUtc)
{
    public const string SchemaId = "enfractal.last_game";
    public const int Version = 1;
    public const int MaxBytes = 4096;

    public static string PathFor(TeamLayout layout) => Path.Combine(layout.TeamsRoot, "last_game.json");

    public static LastGame? Read(TeamLayout layout)
    {
        try
        {
            var path = PathFor(layout);
            if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaxBytes) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "difficulty", "mode", "room_id", "schema", "team_id", "version", "written_utc" }, StringComparer.Ordinal) is false) return null;
            if (Text(root, "schema") != SchemaId || root.GetProperty("version").ValueKind != JsonValueKind.Number || root.GetProperty("version").GetInt32() != Version) return null;
            var team = Text(root, "team_id");
            var room = Text(root, "room_id");
            var mode = Text(root, "mode");
            var difficulty = root.GetProperty("difficulty").ValueKind == JsonValueKind.Null ? null : Text(root, "difficulty");
            if (!TeamLayout.ValidTeamId(team) || !TeamLayout.ValidRoomId(room) || mode is not ("creative" or "challenge" or "live") ||
                (difficulty != null && difficulty is not ("gentle" or "standard" or "brave")) ||
                !DateTime.TryParse(Text(root, "written_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
            return new LastGame(team, room, mode, difficulty, at);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return null; }
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name) is { ValueKind: JsonValueKind.String } text ? text.GetString()! : throw new FormatException("not text");

    /// <summary>Written atomically (a temporary file, then a move over the old one). False when it could not be written: Continue then waits for the next game.</summary>
    public bool Write(TeamLayout layout)
    {
        string? temporary = null;
        try
        {
            if (!TeamLayout.ValidTeamId(TeamId) || !TeamLayout.ValidRoomId(RoomId) || !TeamLayout.ValidMode(Mode)) return false;
            var document = new
            {
                schema = SchemaId, version = Version, team_id = TeamId, room_id = RoomId, mode = Mode, difficulty = Difficulty,
                written_utc = WrittenUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document) + "\n");
            var path = PathFor(layout);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}

/// <summary>One Creative island copied into Challenge by a team: which island, when, at what difficulty, and the builds it carried over.</summary>
public sealed record CarriedCopy(string RoomId, string ManifestPrefix, string Difficulty, string CopiedUtc, IReadOnlyList<string> Builds);

/// <summary>
/// "Copy my Creative island into Challenge" (docs/game/MECHANICS.md section 3, decisions 13 and 44): offered once per island per
/// team; the copy carries the creations, the planted and burnt ground, the journal and the map, and strips what Creative alone
/// has (the undo steps, a fire still burning, the Fractals picked up and the materials gathered, which are growth of the Creative
/// body and do not travel: decision 2). The builds carried over are written down, in the team's folder
/// (<c>teams/&lt;team&gt;/carried_builds.json</c>), so that nothing built before the copy counts for any Challenge first, souvenir or
/// achievement: that ledger is also the record that the island's one copy is used. The Creative original is never touched.
/// </summary>
public static class CarriedBuilds
{
    public const string SchemaId = "enfractal.carried_builds";
    public const int Version = 1;
    public const int MaxLedgerBytes = 256 * 1024;
    public const int MaxSaveBytes = 4 * 1024 * 1024;
    public const int MaxBuilds = 4096;

    public static string LedgerPath(TeamLayout layout, string team) => Path.Combine(layout.TeamFolder(team), "carried_builds.json");

    /// <summary>The islands this team has copied, with the builds carried; empty when there is no ledger. A ledger that does not read throws (the copy is then refused, never guessed).</summary>
    public static IReadOnlyDictionary<string, CarriedCopy> Read(TeamLayout layout, string team)
    {
        var path = LedgerPath(layout, team);
        var copies = new SortedDictionary<string, CarriedCopy>(StringComparer.Ordinal);
        if (!File.Exists(path)) return copies;
        try
        {
            if (new FileInfo(path).Length is 0 or > MaxLedgerBytes) throw new MenuSaveException("The list of carried builds could not be read. It was kept as it was.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != SchemaId || root.GetProperty("version").GetInt32() != Version || root.GetProperty("team_id").GetString() != team)
                throw new MenuSaveException("The list of carried builds could not be read. It was kept as it was.");
            foreach (var entry in root.GetProperty("islands").EnumerateObject())
            {
                var body = entry.Value;
                var builds = body.GetProperty("builds").EnumerateArray().Select(b => b.GetString() ?? throw new FormatException("build")).ToList();
                if (!TeamLayout.ValidRoomId(entry.Name) || builds.Count > MaxBuilds) throw new FormatException("island");
                copies[entry.Name] = new CarriedCopy(entry.Name, body.GetProperty("manifest_prefix").GetString()!, body.GetProperty("difficulty").GetString()!, body.GetProperty("copied_utc").GetString()!, builds);
            }
            return copies;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        { throw new MenuSaveException("The list of carried builds could not be read. It was kept as it was."); }
    }

    /// <summary>The ids of the builds (creations) carried into this team's Challenge island, for the code that decides what counts (none when nothing was copied).</summary>
    public static IReadOnlyList<string> CarriedOn(TeamLayout layout, string team, string roomId) =>
        Read(layout, team).TryGetValue(roomId, out var copy) ? copy.Builds : Array.Empty<string>();

    /// <summary>
    /// Whether the copy is offered for this team on this island: a Creative save is there, Challenge has none yet, and the island's
    /// one copy is not used. Anything unreadable is not offered.
    /// </summary>
    public static bool Offered(TeamLayout layout, string team, IslandEntry island)
    {
        try
        {
            return IslandSaveInfo.One(layout, team, island, "creative").Exists && !IslandSaveInfo.One(layout, team, island, "challenge").Exists &&
                   !Read(layout, team).ContainsKey(island.RoomId);
        }
        catch (MenuSaveException) { return false; }
    }

    /// <summary>Whether the island's one copy is used up for this team (the menu says so instead of offering it).</summary>
    public static bool Used(TeamLayout layout, string team, string roomId)
    {
        try { return Read(layout, team).ContainsKey(roomId); }
        catch (MenuSaveException) { return true; }
    }

    /// <summary>
    /// The copy. Throws MenuSaveException with the reason when it is not offered or the save does not read; otherwise the Challenge
    /// save exists afterwards, at the difficulty chosen, and the ledger names the builds carried. The ledger is written first and
    /// put back if the save cannot be written, so a copy is never made without being written down.
    /// </summary>
    public static CarriedCopy Copy(TeamLayout layout, string team, IslandEntry island, string difficulty, DateTime nowUtc)
    {
        if (difficulty is not ("gentle" or "standard" or "brave")) throw new MenuSaveException("Choose a difficulty for the Challenge island first.");
        var from = IslandSaveInfo.One(layout, team, island, "creative");
        var to = layout.IslandSave(team, "challenge", island.RoomId, island.ManifestPrefix);
        if (!from.Exists) throw new MenuSaveException("There is no Creative island here to copy.");
        if (File.Exists(to)) throw new MenuSaveException("This island already has a Challenge save, and a save is never copied over.");
        var ledger = Read(layout, team);
        if (ledger.ContainsKey(island.RoomId)) throw new MenuSaveException("This island has been copied into Challenge once already, and that is all the copy there is.");

        JsonObject root;
        try
        {
            var bytes = File.ReadAllBytes(from.Path);
            if (bytes.Length is 0 or > MaxSaveBytes) throw new MenuSaveException("The Creative island could not be read, so it was not copied.");
            root = JsonNode.Parse(bytes, null, new JsonDocumentOptions { MaxDepth = 64 })?.AsObject() ?? throw new MenuSaveException("The Creative island could not be read, so it was not copied.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw new MenuSaveException("The Creative island could not be read, so it was not copied."); }

        // The creations the save holds (its instances: a list of {id, owner_id, source, ...}), by id.
        var builds = (root["instances"] as JsonArray)?.Select(item => (item as JsonObject)?["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null).OfType<string>()
            .Order(StringComparer.Ordinal).ToList() ?? new List<string>();
        if (builds.Count > MaxBuilds) throw new MenuSaveException("This island has more builds than the copy can list, so it was not copied.");
        Strip(root, team, difficulty);
        var save = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        if (save.Length > MaxSaveBytes) throw new MenuSaveException("The copy would be too big to save, so it was not made.");

        var copy = new CarriedCopy(island.RoomId, island.ManifestPrefix, difficulty, TeamFile.Stamp(nowUtc), builds);
        var ledgerPath = LedgerPath(layout, team);
        var before = File.Exists(ledgerPath) ? File.ReadAllBytes(ledgerPath) : null;
        string? temporary = null;
        try
        {
            WriteLedger(layout, team, ledger.Append(KeyValuePair.Create(island.RoomId, copy)).ToDictionary(p => p.Key, p => p.Value));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            temporary = to + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(save); stream.Flush(flushToDisk: true); }
            // Never over a save that appeared meanwhile: Move without overwrite throws if it is there.
            File.Move(temporary, to, overwrite: false);
            temporary = null;
            return copy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (before != null) File.WriteAllBytes(ledgerPath, before); else if (File.Exists(ledgerPath)) File.Delete(ledgerPath);
            throw new MenuSaveException("The copy could not be saved. Nothing was changed.");
        }
        finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// What a Creative island loses on its way into Challenge: its undo steps (and the record of the last undo), a fire still
    /// burning, the Fractals picked up and the materials gathered. It becomes this team's Challenge save, at this difficulty.
    /// A save from before the team block or the nature block has nothing of theirs to strip.
    /// </summary>
    private static void Strip(JsonObject root, string team, string difficulty)
    {
        var version = root["version"]?.GetValue<int>() ?? 0;
        if (version >= 5)
        {
            var block = root["team"] as JsonObject ?? new JsonObject();
            block["team_id"] = team;
            block["mode"] = "challenge";
            block["difficulty"] = difficulty;
            root["team"] = block;
        }
        if (version >= 6 && root["nature"] is JsonObject nature)
        {
            nature["undo"] = new JsonArray();
            nature.Remove("undone");
            nature.Remove("fire");
            nature.Remove("fractals");
            nature["wallet"] = new JsonObject();
        }
    }

    private static void WriteLedger(TeamLayout layout, string team, IReadOnlyDictionary<string, CarriedCopy> islands)
    {
        var document = new
        {
            schema = SchemaId, version = Version, team_id = team,
            islands = new SortedDictionary<string, object>(islands.ToDictionary(p => p.Key, p => (object)new
            {
                copied_utc = p.Value.CopiedUtc, from_mode = "creative", manifest_prefix = p.Value.ManifestPrefix, difficulty = p.Value.Difficulty, builds = p.Value.Builds,
            }), StringComparer.Ordinal),
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document) + "\n");
        var path = LedgerPath(layout, team);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
