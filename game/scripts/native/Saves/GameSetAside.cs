using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Saves;

/// <summary>Why a game was not put away, in the player's words (the family's to reword): never a path, never saved text.</summary>
public sealed class SetAsideException(string message, Exception? cause = null) : Exception(message, cause);

/// <summary>Why a set-aside ledger does not read, for a log or a test: never shown to a player.</summary>
public sealed class SetAsideLedgerException(string message) : Exception(message);

/// <summary>One file of a put-away game as its ledger pins it: its place in the folder (forward slashes), its SHA-256, its size.</summary>
public sealed record SetAsideFile(string Path, string Sha256, long Bytes);

/// <summary>One game put away: its folder under user://set_aside/ and its ledger.</summary>
public sealed record PutAwayGame(string Folder, SetAsideLedger Ledger);

/// <summary>
/// The ledger of one game that New game put away (contracts/menu.schema.json $defs/set_aside, garage_game.set_aside version 1;
/// contracts/README.md "The menu's files"): whose game, in which mode, when it was put away, when it was last saved, and every
/// file of it with its size and SHA-256. It pins bytes, never meaning: no put-away file is parsed to write or to accept one.
/// It holds no free text, no name and no path out of its folder, so nothing in it is shown or followed. Read strictly, by
/// the schema's rules and the validator's (contracts/validate.py check_set_aside), so a folder the game lists is one the
/// contract accepts: the last save no later than the put-away among them (Checked), and every listed file there under its own
/// name (GameSetAside.Problems walks the folder's real names, so a name the file system merely answers to is not found).
/// </summary>
public sealed record SetAsideLedger(string TeamId, string Mode, string SetAsideUtc, string LastPlayedUtc, IReadOnlyList<SetAsideFile> Files)
{
    /// <summary>Written only after the rename, so it has one spelling (as the story pack's): compare with ==.</summary>
    public const string SchemaId = SchemaIds.SetAside;
    public const int Version = 1;
    public const string FileName = "set_aside.json";
    public const string TeamFileName = "team.json";
    public const int MaxFiles = 4096;
    public const long MaxFileBytes = 268_435_456;
    public const int MaxPathLength = 400;
    /// <summary>The ledger as canonical JSON is at most this (the validator's bound); as written, indented, it is read up to MaxFileSize.</summary>
    public const int MaxCanonicalBytes = 1024 * 1024;
    public const int MaxFileSize = 4 * 1024 * 1024;
    private const string MomentFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static readonly Regex Moment = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z", RegexOptions.Compiled);
    private static readonly Regex Timestamp = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z\z", RegexOptions.Compiled);
    private static readonly Regex Hash = new(@"\A[0-9a-f]{64}\z", RegexOptions.Compiled);

    /// <summary>When the game was put away (UTC, a whole second).</summary>
    public DateTime SetAsideAt => DateTime.ParseExact(SetAsideUtc, MomentFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    /// <summary>When the game was last saved before that: the date a list of put-away games shows.</summary>
    public DateTime LastPlayedAt => DateTime.Parse(LastPlayedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    /// <summary>The one folder this ledger belongs in, under user://set_aside/: its moment, its team and its mode.</summary>
    public string FolderName => SetAsideUtc.Replace("-", "", StringComparison.Ordinal).Replace(":", "", StringComparison.Ordinal) + "-" + TeamId + "-" + Mode;

    /// <summary>A moment as the ledger and its folder spell it: UTC, to the whole second.</summary>
    public static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString(MomentFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// A file's place inside the folder: team.json, or a path under modes/&lt;mode&gt;/ with forward slashes, no '.', '..' or empty
    /// segment and no character a Windows file name cannot hold, at most 400 code points. Spaces and other printable characters
    /// pass: a stray file someone left beside a save is put away with the game, never a reason to refuse.
    /// </summary>
    public static bool ValidPath(string? path)
    {
        if (path == null) return false;
        if (path == TeamFileName) return true;
        var parts = path.Split('/');
        if (parts.Length < 3 || parts[0] != "modes" || !TeamLayout.ValidMode(parts[1])) return false;
        if (parts.Any(part => part.Length == 0 || part == "." || part == "..")) return false;
        var points = 0;
        for (var index = 0; index < path.Length; index++)
        {
            var c = path[index];
            if (char.IsHighSurrogate(c) && index + 1 < path.Length && char.IsLowSurrogate(path[index + 1])) index++;
            else if (char.IsSurrogate(c) || c < 0x20 || c == 0x7F || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
            points++;
        }
        return points <= MaxPathLength;
    }

    /// <summary>
    /// The ledger checked against the schema and the validator's rules (both moments real ones, and the last save no later
    /// than the put-away, to the microsecond as the validator compares them); throws SetAsideLedgerException with what is wrong.
    /// </summary>
    public SetAsideLedger Checked()
    {
        if (!TeamLayout.ValidTeamId(TeamId)) throw new SetAsideLedgerException("the ledger names an invalid team");
        if (!TeamLayout.ValidMode(Mode)) throw new SetAsideLedgerException("the ledger names an invalid mode");
        if (SetAsideUtc == null || !Moment.IsMatch(SetAsideUtc) || !Real(SetAsideUtc)) throw new SetAsideLedgerException("set_aside_utc is not a real moment to the whole second");
        if (LastPlayedUtc == null || !Timestamp.IsMatch(LastPlayedUtc) || !Real(LastPlayedUtc)) throw new SetAsideLedgerException("last_played_utc is not a real moment");
        if (Ticks(LastPlayedUtc) > Ticks(SetAsideUtc)) throw new SetAsideLedgerException("last_played_utc is after set_aside_utc: a game is last saved before it is put away");
        if (Files == null || Files.Count is < 1 or > MaxFiles) throw new SetAsideLedgerException($"the ledger lists 1 to {MaxFiles} files");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var home = "modes/" + Mode + "/";
        foreach (var file in Files)
        {
            if (file == null || !ValidPath(file.Path)) throw new SetAsideLedgerException("a file's path is not a place inside the folder");
            if (file.Sha256 == null || !Hash.IsMatch(file.Sha256)) throw new SetAsideLedgerException($"file {file.Path} has no SHA-256");
            if (file.Bytes is < 0 or > MaxFileBytes) throw new SetAsideLedgerException($"file {file.Path} has an invalid size");
            if (file.Path != TeamFileName && !file.Path.StartsWith(home, StringComparison.Ordinal))
                throw new SetAsideLedgerException($"file {file.Path} is not under {home}; a {Mode} game holds its own mode's files only");
            if (!seen.Add(file.Path)) throw new SetAsideLedgerException($"file {file.Path} is listed twice");
        }
        if (!seen.Contains(TeamFileName)) throw new SetAsideLedgerException("the ledger does not list team.json, the team file as it was");
        if (CanonicalSize() > MaxCanonicalBytes) throw new SetAsideLedgerException($"the ledger is more than {MaxCanonicalBytes} bytes of canonical JSON");
        return this;
    }

    private static bool Real(string moment) =>
        DateTime.TryParseExact(moment[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    /// <summary>A moment that has passed Real and its pattern, in ticks: its whole second, and its fraction of up to six digits.</summary>
    private static long Ticks(string moment)
    {
        var second = DateTime.ParseExact(moment[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var fraction = moment.Length > 21 ? moment[20..^1] : "";
        return second.Ticks + (fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(7, '0'), CultureInfo.InvariantCulture));
    }

    /// <summary>The ledger's size as canonical JSON (sorted keys, no spaces), as the validator counts it.</summary>
    private int CanonicalSize()
    {
        var text = new StringBuilder("{\"files\":[");
        for (var index = 0; index < Files.Count; index++)
        {
            if (index > 0) text.Append(',');
            text.Append("{\"bytes\":").Append(Files[index].Bytes.ToString(CultureInfo.InvariantCulture)).Append(",\"path\":");
            CanonicalJson.Quote(Files[index].Path, text);
            text.Append(",\"sha256\":\"").Append(Files[index].Sha256).Append("\"}");
        }
        text.Append("],\"last_played_utc\":\"").Append(LastPlayedUtc).Append("\",\"mode\":\"").Append(Mode).Append("\",\"schema\":\"").Append(SchemaId)
            .Append("\",\"set_aside_utc\":\"").Append(SetAsideUtc).Append("\",\"team_id\":\"").Append(TeamId).Append("\",\"version\":1}");
        return Encoding.UTF8.GetByteCount(text.ToString());
    }

    /// <summary>
    /// The ledger as written: UTF-8 without a byte-order mark, LF, two spaces a level, the schema's field order, one final
    /// newline (the form of the contract's example, so the example writes back byte for byte).
    /// </summary>
    public byte[] Serialize()
    {
        Checked();
        var text = new StringBuilder();
        text.Append("{\n  \"schema\": \"").Append(SchemaId).Append("\",\n  \"version\": ").Append(Version.ToString(CultureInfo.InvariantCulture))
            .Append(",\n  \"team_id\": \"").Append(TeamId).Append("\",\n  \"mode\": \"").Append(Mode)
            .Append("\",\n  \"set_aside_utc\": \"").Append(SetAsideUtc).Append("\",\n  \"last_played_utc\": \"").Append(LastPlayedUtc).Append("\",\n  \"files\": [\n");
        for (var index = 0; index < Files.Count; index++)
        {
            text.Append("    {\n      \"path\": ");
            CanonicalJson.Quote(Files[index].Path, text);
            text.Append(",\n      \"sha256\": \"").Append(Files[index].Sha256).Append("\",\n      \"bytes\": ").Append(Files[index].Bytes.ToString(CultureInfo.InvariantCulture))
                .Append(index + 1 < Files.Count ? "\n    },\n" : "\n    }\n");
        }
        text.Append("  ]\n}\n");
        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    /// <summary>
    /// The ledger read strictly: UTF-8 without a byte-order mark and without CR, no duplicate key, exactly the schema's fields
    /// (so no word on where anything would go back), its one id and version, and every rule of Checked.
    /// </summary>
    public static SetAsideLedger Parse(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxFileSize) throw new SetAsideLedgerException("the ledger has an invalid size");
        if (Array.IndexOf(bytes, (byte)'\r') >= 0) throw new SetAsideLedgerException("CR line endings are not allowed");
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = CanonicalJson.Parse(bytes);
            var root = document.RootElement;
            Fields(root, "schema", "version", "team_id", "mode", "set_aside_utc", "last_played_utc", "files");
            if (Text(root, "schema") != SchemaId) throw new SetAsideLedgerException($"the ledger's schema is not {SchemaId}");
            if (root.GetProperty("version").ValueKind != JsonValueKind.Number || root.GetProperty("version").GetRawText() != "1") throw new SetAsideLedgerException("the ledger has an unsupported version");
            var listed = root.GetProperty("files");
            if (listed.ValueKind != JsonValueKind.Array || listed.GetArrayLength() is < 1 or > MaxFiles) throw new SetAsideLedgerException($"the ledger lists 1 to {MaxFiles} files");
            var files = new List<SetAsideFile>(listed.GetArrayLength());
            foreach (var entry in listed.EnumerateArray())
            {
                Fields(entry, "path", "sha256", "bytes");
                var size = entry.GetProperty("bytes");
                if (size.ValueKind != JsonValueKind.Number || !size.GetRawText().All(char.IsAsciiDigit) || !size.TryGetInt64(out var count)) throw new SetAsideLedgerException("a file has an invalid size");
                files.Add(new SetAsideFile(Text(entry, "path"), Text(entry, "sha256"), count));
            }
            return new SetAsideLedger(Text(root, "team_id"), Text(root, "mode"), Text(root, "set_aside_utc"), Text(root, "last_played_utc"), files).Checked();
        }
        catch (Exception error) when (error is CanonicalJsonException or JsonException or InvalidOperationException or FormatException or OverflowException or DecoderFallbackException or KeyNotFoundException)
        {
            throw new SetAsideLedgerException("the ledger is not valid JSON of its kind");
        }
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length || value.EnumerateObject().Any(field => !names.Contains(field.Name, StringComparer.Ordinal)))
            throw new SetAsideLedgerException("the ledger has missing or unsupported fields");
    }

    private static string Text(JsonElement value, string name) =>
        value.GetProperty(name) is { ValueKind: JsonValueKind.String } text ? text.GetString()! : throw new SetAsideLedgerException($"{name} is not text");
}

/// <summary>
/// New game's put-away (Run 4, MENU-2; docs/game/MECHANICS.md section 3, "Starting and continuing"; the Run 4 brief's D1): one
/// game, which is one team in one mode, moved whole out of play into a dated folder of its own with a ledger of every file
/// and its hash. <b>Nothing is deleted, and nothing here ever brings a game back.</b>
/// <code>
/// user://set_aside/&lt;yyyyMMddTHHmmssZ&gt;-&lt;team_id&gt;-&lt;mode&gt;/
///     team.json                  the team file as it was (a copy): that mode's pockets and hand are in it
///     modes/&lt;mode&gt;/...           the mode's folder, moved whole: the vitals store and every island save of that mode
///     set_aside.json             garage_game.set_aside v1: the ledger, written last
/// </code>
/// <list type="bullet">
/// <item><b>What moves.</b> The mode's folder (hearts, stamina and Fractals; every island's save, with its fire, its builds, its
/// journal and its map), renamed in one step, and that mode's pockets and hand, emptied in the team file. The team stays:
/// the person and the costume, the knacks and their ranks, the Gubble, its expertise and Trust, the carried builds, the other
/// modes' games and the machine's files are not the game and are never touched. A new game then starts as a first one does:
/// no vitals store boots a fresh body, no island save a fresh island, empty pockets.</item>
/// <item><b>Run by the menu before any host exists.</b> Not a command: no request, no principal and no companion surface
/// reaches it (as the Creative copy, Ui/Menu/MenuSaves.cs). It holds the team's play lease alone (PlayLease), so it is
/// refused while a room of the team is up.</item>
/// <item><b>The ledger is written last, and it is the line.</b> Until it stands the game is not put away: a fault at any step
/// puts everything back (the folder moved home, the pockets returned byte for byte) and leaves the old game whole. A note in
/// the team's folder (put_away.pending), written before anything moves and removed after, lets the next start do the same if
/// the process itself ended part-way (Recover). Once the ledger stands, nothing undoes it.</item>
/// <item><b>A put-away game is only ever added.</b> Once its ledger stands, no file of its folder under user://set_aside/ is
/// removed or rewritten. A put-away that did not finish and was put back leaves nothing there: its folder held only its own
/// copy of the team file, and goes once the game is home (DropStub), so "Nothing was changed" is true of a refused one.</item>
/// <item><b>Nothing brings a put-away game back unasked.</b> Two things copy old saves into the user folder wherever a file is
/// missing: the save folder's move from the room-era name (UserFolderMove) and the migration of the saves from before teams
/// (SaveMigration). Each is held off for as long as it would write where New game put a game away, or is putting one away
/// (OldFolderWouldRefill, LegacyWouldRefill; TeamBoot asks before it runs either).</item>
/// </list>
/// System.IO only, on whatever user folder it is given, so it runs the same on a test's folder.
/// </summary>
public static class GameSetAside
{
    public const string RootName = "set_aside";
    /// <summary>The put-away's note in the team's folder, while one is at work: the name of the folder it fills.</summary>
    public const string NoteName = "put_away.pending";
    // What a player reads (the family's to reword): plain, short, never a path, never a file's name.
    public const string Failed = "The game could not be put away. Nothing was changed.";
    public const string FailedPutBackLater = "The game could not be put away. It is put back when the game next starts.";
    public const string TeamUnreadable = "This team could not be read. Nothing was changed.";
    public const string NotAsItIs = "This game's saves could not be put away as they are. Nothing was changed.";
    /// <summary>What the player reads when a room starts and a game that New game left half put away cannot be put back.</summary>
    public const string NotPutBack = "The game that was being put away could not be put back. Try again in a moment.";
    /// <summary>At most this many put-away games are listed (the newest); they are kept without limit.</summary>
    public const int MaxListed = 512;
    private const int MaxNoteBytes = 256;
    private static readonly Regex NamePattern = new(@"\A(?<at>[0-9]{8}T[0-9]{6}Z)-(?<team>[a-z][a-z0-9_-]{0,63})-(?<mode>creative|challenge|live)\z", RegexOptions.Compiled);

    /// <summary>
    /// Test seam (the tests are in this assembly; nothing outside it sets this): called after each step of a put-away with the
    /// step's name (Steps). An exception thrown from it is a fault at that step, put back like any other. A
    /// <see cref="PowerCut"/> is the process ending there: nothing is put back, as nothing would be.
    /// </summary>
    internal static Action<string>? AtStep { get; set; }
    /// <summary>The steps AtStep hears, in order. Up to "emptied" a fault puts everything back; "ledger" is after the line.</summary>
    public static readonly IReadOnlyList<string> Steps = Array.AsReadOnly(new[] { "noted", "copied", "moved", "checked", "emptied", "ledger" });
    /// <summary>A test's stand-in for the process ending at a step (AtStep).</summary>
    internal sealed class PowerCut() : Exception("the process ended here (a test)");

    public static string Root(TeamLayout layout) => Path.Combine(layout.Root, RootName);
    public static string NotePath(TeamLayout layout, string team) => Path.Combine(layout.TeamFolder(team), NoteName);
    public static string FolderName(DateTime utc, string team, string mode) => utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + team + "-" + mode;

    /// <summary>
    /// Whether this team has a game in this mode: anything New game would put away (a file in the mode's folder, or something in
    /// that mode's pockets or hand), or a game that a put-away cut short still holds (PendingPutAway: the next start, or the
    /// next New game, puts it back first, so it is this mode's game all along). In doubt (a file that does not read) there is
    /// one: New game asks before anything moves.
    /// </summary>
    public static bool HasGame(TeamLayout layout, string team, string mode)
    {
        if (!TeamLayout.ValidTeamId(team) || !TeamLayout.ValidMode(mode)) return false;
        try
        {
            if (PendingPutAway(layout, team) == mode) return true;
            var home = layout.ModeFolder(team, mode);
            if (Directory.Exists(home) && Entries(home, "").Any(e => !e.IsFolder)) return true;
            if (ReadBounded(layout.TeamFile(team), TeamFile.MaxBytes) is not { } bytes) return false;
            var file = TeamFile.Parse(bytes);
            return !file.PocketsOf(mode).IsEmpty || !file.HandOf(mode).IsNone;
        }
        catch (Exception error) when (FileError(error) || error is TeamFileException or SetAsideException) { return true; }
    }

    /// <summary>
    /// The mode of this team's game that a put-away left half done (the process ended during New game: its note stands and its
    /// ledger does not), or null. Until it is put back (Recover: the next room boot of the team, or the next PutAway) the game
    /// is neither in play nor put away. A note that does not read names nothing, and is none.
    /// </summary>
    public static string? PendingPutAway(TeamLayout layout, string team)
    {
        if (!TeamLayout.ValidTeamId(team)) return null;
        try
        {
            var note = NotePath(layout, team);
            if (!File.Exists(note) || ReadNote(note) is not { } name || ParseName(name) is not { } parsed || parsed.Team != team) return null;
            return File.Exists(Path.Combine(Root(layout), name, SetAsideLedger.FileName)) ? null : parsed.Mode;
        }
        catch (Exception error) when (FileError(error)) { return null; }
    }

    /// <summary>
    /// Whether a game of this team and mode was ever put away (a finished folder stands for it). What copies old saves into
    /// the user folder asks PutAwayPlaces, which counts a put-away that was cut short as well.
    /// </summary>
    public static bool WasPutAway(TeamLayout layout, string team, string mode)
    {
        try
        {
            var root = Root(layout);
            if (!Directory.Exists(root)) return false;
            return Directory.EnumerateDirectories(root).Any(folder => ParseName(Path.GetFileName(folder)) is { } name && name.Team == team && name.Mode == mode &&
                                                                    File.Exists(Path.Combine(folder, SetAsideLedger.FileName)));
        }
        catch (Exception error) when (FileError(error)) { return false; }
    }

    // ---- old saves stay out of a put-away game's place ----

    /// <summary>
    /// The places New game has put a game away from, or is putting one away from: one team's one mode each (its folder
    /// teams/&lt;team&gt;/modes/&lt;mode&gt;/). A finished put-away's folder stands for its place for good; a note stands for one that
    /// was cut short, until it is put back.
    /// </summary>
    public static IReadOnlyCollection<(string Team, string Mode)> PutAwayPlaces(TeamLayout layout)
    {
        var places = new HashSet<(string, string)>();
        try
        {
            var root = Root(layout);
            if (Directory.Exists(root))
                foreach (var folder in Directory.EnumerateDirectories(root))
                    if (ParseName(Path.GetFileName(folder)) is { } name && File.Exists(Path.Combine(folder, SetAsideLedger.FileName))) places.Add((name.Team, name.Mode));
            if (Directory.Exists(layout.TeamsRoot))
                foreach (var folder in Directory.EnumerateDirectories(layout.TeamsRoot))
                    if (Path.GetFileName(folder) is { } team && PendingPutAway(layout, team) is { } mode) places.Add((team, mode));
        }
        catch (Exception error) when (FileError(error)) { }
        return places;
    }

    /// <summary>
    /// For the save folder's move from the room-era name (UserFolderMove: until its ledger stands, every launch copies each file
    /// of the old folder that is missing here): the old folder's files it would copy into a put-away game's place, each by its
    /// path in the folder. None when the move is done, or when nothing of those places is missing here. While there are any the
    /// move is held off whole (TeamBoot.BringOldSaves): the old folder is never changed, so nothing is lost by waiting, and a
    /// game New game put away is never copied back in.
    /// </summary>
    public static IReadOnlyList<string> OldFolderWouldRefill(string oldFolder, string userFolder)
    {
        var found = new List<string>();
        try
        {
            oldFolder = Path.GetFullPath(oldFolder);
            userFolder = Path.GetFullPath(userFolder);
            if (!Directory.Exists(oldFolder) || !Directory.Exists(userFolder) || File.Exists(Path.Combine(userFolder, UserFolderMove.LedgerName))) return found;
            if (string.Equals(Path.TrimEndingDirectorySeparator(oldFolder), Path.TrimEndingDirectorySeparator(userFolder), StringComparison.OrdinalIgnoreCase)) return found;
            foreach (var (team, mode) in PutAwayPlaces(new TeamLayout(userFolder)).OrderBy(p => p.Team, StringComparer.Ordinal).ThenBy(p => p.Mode, StringComparer.Ordinal))
            {
                var place = Path.Combine("teams", team, "modes", mode);
                var old = Path.Combine(oldFolder, place);
                if (!Directory.Exists(old)) continue;
                foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    // What the move itself never copies: the stores' lease files and its own half-written copies.
                    if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".moving", StringComparison.Ordinal)) continue;
                    var relative = Path.Combine(place, Path.GetRelativePath(old, file));
                    if (!File.Exists(Path.Combine(userFolder, relative))) found.Add(relative.Replace('\\', '/'));
                }
            }
        }
        catch (Exception error) when (FileError(error)) { }
        return found;
    }

    /// <summary>
    /// For the migration of the saves from before teams (SaveMigration: until its ledger stands, a launch writes each old save's
    /// copy wherever the team has no file): whether it would write into a put-away game's place of its team. That is so while
    /// the ledger is missing, the place's vitals are missing with an old vitals file to make them from, or an old room save has
    /// no island save there. While it would, the migration is held off whole (TeamBoot.Prepare): its old files stay where they
    /// are, and a game New game put away is never copied back in.
    /// </summary>
    public static bool LegacyWouldRefill(TeamLayout layout, string team = TeamLayout.DefaultTeamId)
    {
        try
        {
            if (File.Exists(layout.MigrationLedger)) return false;
            foreach (var (_, mode) in PutAwayPlaces(layout).Where(p => p.Team == team))
            {
                if (!File.Exists(layout.VitalsFile(team, mode)) && (File.Exists(layout.LegacyVitalsV2(mode)) || File.Exists(layout.LegacyVitalsV1))) return true;
                // The old room saves go to the mode that was played last, which only the migration works out: any mode but Live.
                if (mode == "live" || !Directory.Exists(layout.LegacyRooms)) continue;
                foreach (var room in Directory.EnumerateDirectories(layout.LegacyRooms).Where(r => TeamLayout.ValidRoomId(Path.GetFileName(r))))
                    foreach (var manifest in Directory.EnumerateDirectories(room).Where(m => TeamLayout.ValidManifestPrefix(Path.GetFileName(m))))
                        if (File.Exists(Path.Combine(manifest, "inventions.json")) && !File.Exists(layout.IslandSave(team, mode, Path.GetFileName(room), Path.GetFileName(manifest)))) return true;
            }
            return false;
        }
        // In doubt the old saves stay out: they are never touched by waiting.
        catch (Exception error) when (FileError(error)) { return PutAwayPlaces(layout).Any(p => p.Team == team); }
    }

    /// <summary>
    /// The put-away. Null when the team has no game in this mode (nothing is made). Throws SetAsideException, with the old game
    /// whole, when a room of the team is up (after waiting <paramref name="wait"/> for one that is leaving) or anything fails.
    /// <para>
    /// New game always calls this, whatever HasGame said: a put-away that the process ending cut short is put back first, and
    /// then put away whole, so a game half moved is never left behind a new one.
    /// </para>
    /// </summary>
    public static PutAwayGame? PutAway(TeamLayout layout, string team, string mode, DateTime nowUtc, TimeSpan wait = default)
    {
        if (!TeamLayout.ValidTeamId(team)) throw new ArgumentException("Not a team id.", nameof(team));
        if (!TeamLayout.ValidMode(mode)) throw new ArgumentException("Not a mode.", nameof(mode));
        if (!Directory.Exists(layout.TeamFolder(team))) return null;
        // Held: a room of the team is up. A lease that cannot be opened at all is a fault like any other, never "still open".
        var answer = PlayLease.Ask(layout, team, alone: true, wait, out var taken);
        using var lease = taken ?? throw new SetAsideException(answer == PlayLeaseAnswer.Held ? PlayLease.StillOpen : Failed);
        return PutAway(lease, layout, mode, nowUtc);
    }

    /// <summary>The put-away under a lease the caller already holds alone (the lease names the team).</summary>
    public static PutAwayGame? PutAway(PlayLease alone, TeamLayout layout, string mode, DateTime nowUtc)
    {
        var team = Team(alone);
        if (!TeamLayout.ValidMode(mode)) throw new ArgumentException("Not a mode.", nameof(mode));
        var teamPath = layout.TeamFile(team);
        var home = layout.ModeFolder(team, mode);
        var prefix = "modes/" + mode;
        byte[] teamBytes;
        TeamFile emptied;
        bool pocketsHeld;
        Scan scan;
        try
        {
            // A put-away that a crash cut short is put back first: its game is the one this put-away is about.
            Recover(alone, layout);
            scan = Directory.Exists(home) ? ScanFolder(home, prefix) : Scan.None;
            if (ReadBounded(teamPath, TeamFile.MaxBytes) is not { } read)
            {
                if (scan.Files.Count == 0) return null;
                throw new SetAsideException(TeamUnreadable);
            }
            teamBytes = read;
            var file = TeamFile.Parse(teamBytes);
            if (file.TeamId != team) throw new SetAsideException(TeamUnreadable);
            emptied = file.WithPockets(mode, InventoryContents.Empty).WithHand(mode, HandSelection.None);
            pocketsHeld = !ReferenceEquals(emptied, file);
        }
        catch (TeamFileException error) { throw new SetAsideException(TeamUnreadable, error); }
        catch (Exception error) when (FileError(error) || error is SetAsideLedgerException) { throw new SetAsideException(Failed, error); }
        if (scan.Files.Count == 0 && !pocketsHeld) return null;

        // The moment in the folder's name, to the second; one folder a second for a team and mode, and a folder that is there is
        // never written into again (the next second is taken instead).
        var at = WholeSecond(nowUtc);
        var newest = scan.Newest ?? SafeWriteTime(teamPath) ?? at;
        var lastPlayed = newest > at ? at : newest;
        string name, folder;
        for (var tries = 0; ; tries++)
        {
            name = FolderName(at, team, mode);
            folder = Path.Combine(Root(layout), name);
            if (!Directory.Exists(folder) && !File.Exists(folder)) break;
            if (tries >= 120) throw new SetAsideException(Failed);
            at = at.AddSeconds(1);
        }
        var files = scan.Files.Append(new SetAsideFile(SetAsideLedger.TeamFileName, Sha256(teamBytes), teamBytes.Length)).OrderBy(f => f.Path, CanonicalJson.KeyOrder).ToList();
        var ledger = new SetAsideLedger(team, mode, SetAsideLedger.Stamp(at), SetAsideLedger.Stamp(lastPlayed), files);
        byte[] ledgerBytes;
        try
        {
            // What is written reads back by the game's own strict reader, inside the contract's bounds, before anything moves.
            ledgerBytes = ledger.Serialize();
            SetAsideLedger.Parse(ledgerBytes);
        }
        catch (Exception error) when (error is SetAsideLedgerException or CanonicalJsonException) { throw new SetAsideException(NotAsItIs, error); }

        var note = NotePath(layout, team);
        var noted = false;
        try
        {
            Directory.CreateDirectory(Root(layout));
            WriteWhole(note, Encoding.ASCII.GetBytes(name + "\n"));
            noted = true;
            Step("noted");
            Directory.CreateDirectory(folder);
            WriteNew(Path.Combine(folder, SetAsideLedger.TeamFileName), teamBytes);
            Step("copied");
            var moved = Path.Combine(folder, "modes", mode);
            if (Directory.Exists(home))
            {
                Directory.CreateDirectory(Path.Combine(folder, "modes"));
                Directory.Move(home, moved);
            }
            Step("moved");
            // The bytes put away are the bytes that were there when the player asked: every file again, by its hash.
            var after = Directory.Exists(moved) ? ScanFolder(moved, prefix) : Scan.None;
            if (!after.Files.SequenceEqual(scan.Files)) throw new IOException("The game's files changed while they were put away.");
            Step("checked");
            if (pocketsHeld) ReplaceTeamFile(teamPath, teamBytes, emptied.Serialize());
            Step("emptied");
            // The line: the ledger, whole or not at all.
            PlaceLedger(layout, team, folder, ledgerBytes);
        }
        catch (PowerCut) { throw; }
        catch (Exception error)
        {
            if (noted)
            {
                try
                {
                    var putBack = Undo(layout, team, mode, folder, at) == PutBack;
                    File.Delete(note);
                    // Nothing was changed: the folder this put-away made, holding only its own copy of the team file, goes too.
                    if (putBack) DropStub(layout, team, folder);
                }
                catch (Exception again) when (FileError(again) || again is TeamFileException or SetAsideException or SetAsideLedgerException)
                {
                    throw new SetAsideException(FailedPutBackLater, error);
                }
            }
            throw new SetAsideException(error is SetAsideException own ? own.Message : Failed, error);
        }
        Step("ledger");
        // The note has done its work. Left behind (a fault here), the next start finds the ledger standing and drops it.
        try { File.Delete(note); }
        catch (Exception error) when (FileError(error)) { }
        return new PutAwayGame(folder, ledger);
    }

    private static void Step(string name) => AtStep?.Invoke(name);

    // ---- putting back what did not finish ----

    /// <summary>
    /// The next start's part of "a fault part-way leaves the old game whole": if this team's put-away note is there, the process
    /// ended during a put-away. Where the ledger stands the put-away had finished, and only the note goes. Otherwise everything
    /// is put back: the mode's folder moved home and that mode's pockets and hand returned from the copy of the team file. If a
    /// newer game already stands in the folder's old place, nothing is moved over it: the old game's put-away is finished
    /// instead (its ledger written), so neither game is lost. Returns a line for the boot's log, empty when there was nothing to do.
    /// Called with the team's lease held alone (TeamBoot.Prepare, before a room reads a save; PutAway).
    /// </summary>
    public static string Recover(PlayLease alone, TeamLayout layout)
    {
        var team = Team(alone);
        var note = NotePath(layout, team);
        if (!File.Exists(note)) return "";
        var name = ReadNote(note);
        if (name == null || ParseName(name) is not { } parsed || parsed.Team != team)
            return "a put-away note that does not read was left as it is: nothing was moved";
        var folder = Path.Combine(Root(layout), name);
        var did = File.Exists(Path.Combine(folder, SetAsideLedger.FileName)) ? "had finished" : Undo(layout, team, parsed.Mode, folder, parsed.At);
        File.Delete(note);
        if (did == PutBack) DropStub(layout, team, folder);
        return $"the put-away {name} {did}";
    }

    private const string PutBack = "was put back";

    /// <summary>Puts back what an unfinished put-away moved into <paramref name="folder"/> (see Recover). Safe to run twice.</summary>
    private static string Undo(TeamLayout layout, string team, string mode, string folder, DateTime at)
    {
        var moved = Path.Combine(folder, "modes", mode);
        var home = layout.ModeFolder(team, mode);
        if (Directory.Exists(moved))
        {
            if (Directory.Exists(home) && Entries(home, "").Any(e => !e.IsFolder))
            {
                Finish(layout, team, mode, folder, at);
                return "was finished: a newer game stands in its place";
            }
            // Empty folders left where the game stood are no game: they make way (a folder with anything in it never does).
            if (Directory.Exists(home))
                foreach (var empty in Entries(home, "").Where(e => e.IsFolder).Select(e => e.Full).OrderByDescending(p => p.Length).Append(home)) Directory.Delete(empty, recursive: false);
            Directory.CreateDirectory(Path.GetDirectoryName(home)!);
            Directory.Move(moved, home);
        }
        ReturnPockets(layout, team, mode, folder);
        return PutBack;
    }

    /// <summary>
    /// What a put-away that did not finish leaves under set_aside once its game is home again: the folder it made, holding its
    /// own copy of the team file and the empty folders the move left. It goes, so a put-away that was refused or put back has
    /// changed nothing. Only ever exactly that: a folder of this team directly under set_aside with no ledger and no other file,
    /// whose copy is byte for byte the team file that stands (so nothing is removed that is not still there). Anything else
    /// stays as it is, and a fault here is no fault of the put-away.
    /// </summary>
    private static void DropStub(TeamLayout layout, string team, string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || ParseName(Path.GetFileName(folder)) is not { } named || named.Team != team) return;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder)), Root(layout), StringComparison.Ordinal)) return;
            var entries = Entries(folder, "");
            if (entries.Any(e => !e.IsFolder && e.Relative != SetAsideLedger.TeamFileName)) return;
            var copy = Path.Combine(folder, SetAsideLedger.TeamFileName);
            if (File.Exists(copy))
            {
                if (ReadBounded(copy, TeamFile.MaxBytes) is not { } copied || ReadBounded(layout.TeamFile(team), TeamFile.MaxBytes) is not { } stands || !copied.AsSpan().SequenceEqual(stands)) return;
                File.Delete(copy);
            }
            foreach (var empty in entries.Where(e => e.IsFolder).Select(e => e.Full).OrderByDescending(p => p.Length).Append(folder)) Directory.Delete(empty, recursive: false);
        }
        catch (Exception error) when (FileError(error) || error is SetAsideException) { }
    }

    /// <summary>That mode's pockets and hand back in the team file, from the copy made before they were emptied; nothing else of the team file changes.</summary>
    private static void ReturnPockets(TeamLayout layout, string team, string mode, string folder)
    {
        var teamPath = layout.TeamFile(team);
        if (ReadBounded(Path.Combine(folder, SetAsideLedger.TeamFileName), TeamFile.MaxBytes) is not { } copyBytes || ReadBounded(teamPath, TeamFile.MaxBytes) is not { } liveBytes) return;
        if (copyBytes.AsSpan().SequenceEqual(liveBytes)) return;
        TeamFile copy, live;
        // A copy that does not read was cut short as it was written, which is before anything was emptied.
        try { copy = TeamFile.Parse(copyBytes); }
        catch (TeamFileException) { return; }
        live = TeamFile.Parse(liveBytes);
        if (copy.TeamId != team || (live.PocketsOf(mode).Same(copy.PocketsOf(mode)) && live.HandOf(mode) == copy.HandOf(mode))) return;
        // As the put-away left it, the team file goes back byte for byte; had anything else changed since, only the pockets and the hand do.
        var asEmptied = copy.WithPockets(mode, InventoryContents.Empty).WithHand(mode, HandSelection.None).Serialize();
        var back = liveBytes.AsSpan().SequenceEqual(asEmptied) ? copyBytes : live.WithPockets(mode, copy.PocketsOf(mode)).WithHand(mode, copy.HandOf(mode)).Serialize();
        ReplaceTeamFile(teamPath, liveBytes, back);
    }

    /// <summary>
    /// An unfinished put-away finished where it lies: the old game's pockets emptied if the team file still holds them, then
    /// the ledger of what the folder holds, written last (see Recover). Safe to run twice.
    /// </summary>
    private static void Finish(TeamLayout layout, string team, string mode, string folder, DateTime at)
    {
        var copy = Path.Combine(folder, SetAsideLedger.TeamFileName);
        var teamBytes = ReadBounded(copy, TeamFile.MaxBytes) ?? throw new IOException("The put-away folder holds no copy of the team file.");
        var scan = ScanFolder(Path.Combine(folder, "modes", mode), "modes/" + mode);
        var newest = scan.Newest ?? SafeWriteTime(copy) ?? at;
        var files = scan.Files.Append(new SetAsideFile(SetAsideLedger.TeamFileName, Sha256(teamBytes), teamBytes.Length)).OrderBy(f => f.Path, CanonicalJson.KeyOrder).ToList();
        var ledger = new SetAsideLedger(team, mode, SetAsideLedger.Stamp(at), SetAsideLedger.Stamp(newest > at ? at : newest), files);
        var ledgerBytes = ledger.Serialize();
        EmptyOldPockets(layout, team, mode, teamBytes);
        PlaceLedger(layout, team, folder, ledgerBytes);
    }

    /// <summary>
    /// The old game's pockets and hand are the old game's, and its copy of the team file keeps them. Where the put-away was cut
    /// before it emptied them, the team file still holds them under the newer game: emptied now, as the put-away would have,
    /// so the newer game never carries the old game's things as well. Only while they are exactly what the copy holds: once the
    /// newer game has changed its pockets, what is in them cannot be told apart, and they are left as they are.
    /// </summary>
    private static void EmptyOldPockets(TeamLayout layout, string team, string mode, byte[] copyBytes)
    {
        var teamPath = layout.TeamFile(team);
        if (ReadBounded(teamPath, TeamFile.MaxBytes) is not { } liveBytes) return;
        TeamFile copy, live;
        try
        {
            copy = TeamFile.Parse(copyBytes);
            live = TeamFile.Parse(liveBytes);
        }
        catch (TeamFileException) { return; }
        if (copy.TeamId != team || (copy.PocketsOf(mode).IsEmpty && copy.HandOf(mode).IsNone)) return;
        if (!live.PocketsOf(mode).Same(copy.PocketsOf(mode)) || live.HandOf(mode) != copy.HandOf(mode)) return;
        ReplaceTeamFile(teamPath, liveBytes, live.WithPockets(mode, InventoryContents.Empty).WithHand(mode, HandSelection.None).Serialize());
    }

    /// <summary>
    /// The ledger into its folder, whole or not at all: written in the team's folder (the same disk), flushed, then moved in
    /// under its one name, never over a ledger that stands. So the set-aside folder never holds a part of one.
    /// </summary>
    private static void PlaceLedger(TeamLayout layout, string team, string folder, byte[] bytes)
    {
        var pending = Path.Combine(layout.TeamFolder(team), NoteName + ".ledger");
        try
        {
            WriteOver(pending, bytes);
            File.Move(pending, Path.Combine(folder, SetAsideLedger.FileName), overwrite: false);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (Exception error) when (FileError(error)) { }
        }
    }

    // ---- reading what was put away: to list it, never to load it ----

    /// <summary>
    /// The games put away on this machine (one team's, or everyone's), newest first: each folder under user://set_aside/ whose
    /// ledger reads and names it. Only ledgers are read; a folder without one is a put-away that did not finish and is not listed.
    /// </summary>
    public static IReadOnlyList<PutAwayGame> List(TeamLayout layout, string? team = null)
    {
        var found = new List<PutAwayGame>();
        var root = Root(layout);
        try
        {
            if (!Directory.Exists(root)) return found;
            // A folder's name starts with its moment, so names in falling order are the newest first.
            foreach (var folder in Directory.GetDirectories(root).OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                if (found.Count >= MaxListed) break;
                var name = Path.GetFileName(folder);
                if (ParseName(name) is not { } parsed || (team != null && parsed.Team != team)) continue;
                if (ReadLedger(folder) is { } ledger && ledger.FolderName == name) found.Add(new PutAwayGame(folder, ledger));
            }
        }
        catch (Exception error) when (FileError(error)) { }
        return found;
    }

    /// <summary>A folder's ledger, or null when it has none or it does not read.</summary>
    public static SetAsideLedger? ReadLedger(string folder)
    {
        try { return ReadBounded(Path.Combine(folder, SetAsideLedger.FileName), SetAsideLedger.MaxFileSize) is { } bytes ? SetAsideLedger.Parse(bytes) : null; }
        catch (Exception error) when (FileError(error) || error is SetAsideLedgerException) { return null; }
    }

    /// <summary>
    /// What is wrong with a put-away folder, as contracts/validate.py check_set_aside_folder finds it (none: the folder is whole):
    /// the ledger reads; the folder is named for it; every listed file is there with its size and SHA-256; the folder holds no
    /// other file and no link. A listed file is there under its listed name: the folder is walked by its files' own names and
    /// each is looked up exactly, so a name the file system merely answers to (on Windows another case, or a dot or a space at
    /// the end) is a file the ledger lists and the folder lacks. For a test, a tool or a careful reader; never shown to a player.
    /// </summary>
    public static IReadOnlyList<string> Problems(string folder)
    {
        var problems = new List<string>();
        SetAsideLedger ledger;
        try
        {
            var bytes = ReadBounded(Path.Combine(folder, SetAsideLedger.FileName), SetAsideLedger.MaxFileSize);
            if (bytes == null) return new[] { "the folder has no ledger: a put-away that did not finish" };
            ledger = SetAsideLedger.Parse(bytes);
        }
        catch (SetAsideLedgerException error) { return new[] { error.Message }; }
        catch (Exception error) when (FileError(error)) { return new[] { "the ledger could not be read" }; }
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        if (name != ledger.FolderName) problems.Add($"the ledger belongs in a folder named {ledger.FolderName}, not {name}");
        var listed = ledger.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        try
        {
            var there = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Entries(folder, ""))
            {
                if (entry.IsFolder) continue;
                there.Add(entry.Relative);
                if (entry.Relative == SetAsideLedger.FileName) continue;
                if (!listed.TryGetValue(entry.Relative, out var file)) { problems.Add($"file {entry.Relative} is in the folder but not in the ledger"); continue; }
                var (hash, size) = HashOf(entry.Full);
                if (size != file.Bytes) problems.Add($"file {entry.Relative} is {size} bytes, not the ledger's {file.Bytes}");
                else if (hash != file.Sha256) problems.Add($"file {entry.Relative} does not have the ledger's SHA-256");
            }
            problems.AddRange(listed.Keys.Where(path => !there.Contains(path)).Select(path => $"file {path} is in the ledger but not in the folder"));
        }
        catch (SetAsideException) { problems.Add("the folder holds a link, which a put-away game never does"); }
        catch (Exception error) when (FileError(error)) { problems.Add("a file in the folder could not be read"); }
        return problems;
    }

    // ---- files ----

    private sealed record Scan(IReadOnlyList<SetAsideFile> Files, DateTime? Newest)
    {
        public static Scan None { get; } = new(Array.Empty<SetAsideFile>(), null);
    }

    private readonly record struct Entry(string Relative, string Full, bool IsFolder);

    /// <summary>
    /// Everything under a folder, each by its place in it with forward slashes (after <paramref name="prefix"/>). A link (a
    /// symbolic link, a junction) is never walked into and never put away: it throws SetAsideException(NotAsItIs).
    /// </summary>
    private static List<Entry> Entries(string folder, string prefix)
    {
        var found = new List<Entry>();
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((folder, prefix));
        while (pending.Count > 0)
        {
            var (full, relative) = pending.Pop();
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new SetAsideException(NotAsItIs);
                var place = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                var isFolder = (entry.Attributes & FileAttributes.Directory) != 0;
                found.Add(new Entry(place, entry.FullName, isFolder));
                if (isFolder) pending.Push((entry.FullName, place));
            }
        }
        return found;
    }

    /// <summary>Every file under a folder with its size and SHA-256, in path order, and the newest write among them. A file whose place the ledger cannot hold refuses the put-away.</summary>
    private static Scan ScanFolder(string folder, string prefix)
    {
        var files = new List<SetAsideFile>();
        DateTime? newest = null;
        foreach (var entry in Entries(folder, prefix))
        {
            if (entry.IsFolder) continue;
            if (!SetAsideLedger.ValidPath(entry.Relative) || files.Count >= SetAsideLedger.MaxFiles - 1) throw new SetAsideException(NotAsItIs);
            var (hash, size) = HashOf(entry.Full);
            if (size > SetAsideLedger.MaxFileBytes) throw new SetAsideException(NotAsItIs);
            files.Add(new SetAsideFile(entry.Relative, hash, size));
            if (SafeWriteTime(entry.Full) is { } written && (newest == null || written > newest)) newest = written;
        }
        files.Sort((a, b) => CanonicalJson.CompareCodePoints(a.Path, b.Path));
        return new Scan(files, newest);
    }

    /// <summary>A file's SHA-256 and size, read while nobody writes it (a writer's open handle refuses the read, and so the put-away).</summary>
    private static (string Sha256, long Bytes) HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return (hash, stream.Length);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DateTime? SafeWriteTime(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            // A file system that keeps no time answers with the year 1601: no time at all.
            return written.Year < 1980 ? null : written;
        }
        catch (Exception error) when (FileError(error)) { return null; }
    }

    private static DateTime WholeSecond(DateTime moment)
    {
        var utc = moment.ToUniversalTime();
        return new DateTime(utc.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static byte[]? ReadBounded(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > maxBytes) throw new IOException("The file has an invalid size.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>A new file, flushed to the disk; never over a file that is there.</summary>
    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>A small file of the put-away's own (its note, its ledger before it is moved in), flushed to the disk, replacing an earlier one of the same name.</summary>
    private static void WriteOver(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The put-away's note, whole or absent: written beside itself, flushed, then moved over.</summary>
    private static void WriteWhole(string path, byte[] bytes)
    {
        WriteOver(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// The team file replaced as its own store replaces it (TeamStore.Save): under the store's lease, only while it still holds
    /// the bytes that were read, through a temporary file flushed and moved over.
    /// </summary>
    private static void ReplaceTeamFile(string path, byte[] expected, byte[] bytes)
    {
        string? temporary = null;
        try
        {
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (ReadBounded(path, TeamFile.MaxBytes) is not { } current || !current.AsSpan().SequenceEqual(expected)) throw new IOException("The team file changed while the game was put away.");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            WriteNew(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (Exception error) when (FileError(error)) { }
        }
    }

    private static string? ReadNote(string path)
    {
        try
        {
            // The note is a folder's name and one newline; anything else is no note of the put-away's.
            return ReadBounded(path, MaxNoteBytes) is { Length: > 1 } bytes && bytes[^1] == (byte)'\n' ? Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1) : null;
        }
        catch (Exception error) when (FileError(error)) { return null; }
    }

    private static (DateTime At, string Team, string Mode)? ParseName(string? name)
    {
        if (name == null || NamePattern.Match(name) is not { Success: true } match) return null;
        if (!DateTime.TryParseExact(match.Groups["at"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return null;
        var team = match.Groups["team"].Value;
        return TeamLayout.ValidTeamId(team) ? (at, team, match.Groups["mode"].Value) : null;
    }

    private static string Team(PlayLease alone)
    {
        if (alone is not { Alone: true, Held: true }) throw new ArgumentException("A put-away holds the team's play lease alone.", nameof(alone));
        return alone.TeamId;
    }

    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
