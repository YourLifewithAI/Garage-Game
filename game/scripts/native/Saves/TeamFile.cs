using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Saves;

/// <summary>A fixed, player-safe error: the team file's own words, never a path or saved text.</summary>
public sealed class TeamFileException(string message) : Exception(message);

/// <summary>One knack in one slot, at the rank earned (contracts/team.schema.json $defs/team knacks.chosen).</summary>
public sealed record ChosenKnack(int Slot, string Knack, int Rank);

/// <summary>
/// The team file (contracts/team.schema.json $defs/team, enfractal.team version 1, the team folder's team.json): what belongs to the
/// team whatever the mode or island. The person is a profile, not an avatar (a display name and the costume worn); the knacks chosen
/// and their ranks; the Gubble's name and colour, bound to the team for life; Trust, counted by the host from receipts and
/// consequence facts (M8, WithTrust), never by a request. Immutable: every change is a new record. Display names
/// are untrusted text, shown only. The machine's switches are not here (MachineSettings).
/// </summary>
public sealed record TeamFile
{
    public const string SchemaId = "enfractal.team";
    public const int Version = 1;
    public const int MaxBytes = 16 * 1024;
    public const int MaxNameLength = 40;
    public const int TrustBound = 1_000_000;
    /// <summary>At most this many knacks keep a rank set aside (knacks.earned): a rules pack's table has at most 32.</summary>
    public const int MaxEarned = 32;
    /// <summary>No rank set aside: a team file without knacks.earned (every file before the review's finding 2) reads as this.</summary>
    public static readonly IReadOnlyDictionary<string, int> NoneEarned = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
    /// <summary>The costume of the prototype figure (no drawn character worn): the HUD's empty choice, a token here.</summary>
    public const string PrototypeCostume = "prototype";
    private static readonly Regex Token = new(@"\A[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    private static readonly Regex Colour = new(@"\A#[0-9a-fA-F]{6}\z", RegexOptions.Compiled);
    private static readonly Regex Utc = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z\z", RegexOptions.Compiled);

    public string TeamId { get; init; } = TeamLayout.DefaultTeamId;
    public string CreatedUtc { get; init; } = "";
    public string DisplayName { get; init; } = "Player";
    public string Costume { get; init; } = PrototypeCostume;
    public int SlotsOpen { get; init; } = 1;
    public IReadOnlyList<ChosenKnack> Knacks { get; init; } = Array.Empty<ChosenKnack>();
    /// <summary>
    /// The ranks earned by knacks in no slot now, above 1, by knack id in ordinal order (knacks.earned, optional): a knack swapped
    /// out keeps its rank here, and set back into a slot returns at it, so a free swap never costs an earned rank (the review's
    /// finding 2). A knack in a slot keeps its rank in Knacks, never here.
    /// </summary>
    public IReadOnlyDictionary<string, int> Earned { get; init; } = NoneEarned;
    public string GubbleName { get; init; } = CompanionAvatar.DefaultName;
    public string GubbleColour { get; init; } = "#65b9b0";
    public int Trust { get; init; }


    /// <summary>The knacks as the host reads them (CommandHost.UseKnacks): id to rank.</summary>
    public IReadOnlyDictionary<string, int> KnackRanks => Knacks.ToDictionary(k => k.Knack, k => k.Rank, StringComparer.Ordinal);

    /// <summary>A new team: one slot open, no knack yet, Trust at base. The names and colour are the caller's (the menu's, or the machine's settings for the first team).</summary>
    public static TeamFile Create(string teamId, DateTime createdUtc, string displayName, string costume, string gubbleName, string gubbleColour)
    {
        var team = new TeamFile
        {
            TeamId = teamId, CreatedUtc = Stamp(createdUtc), DisplayName = displayName, Costume = costume, GubbleName = gubbleName, GubbleColour = gubbleColour,
        };
        return team.Checked();
    }

    public static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The team with <paramref name="knack"/> in <paramref name="slot"/> (knack.set's rules, and the menu's): the slot must be open,
    /// the knack one the island's pack offers (its table, available), and in no other slot. A knack already in this slot keeps its
    /// rank. The knack the slot held is set aside at the rank it earned (Earned, above 1); the one set in returns at the rank it
    /// earned before, or 1 if it never ranked up (ranks are earned, never set; a swap is free and costs none). Throws
    /// TeamFileException with the reason.
    /// </summary>
    public TeamFile WithKnack(int slot, string knack, KnacksRules? table)
    {
        if (slot < 1 || slot > SlotsOpen) throw new TeamFileException(SlotsOpen == 1 ? "Only the first knack slot is open; the others open at the summits." : $"Slots 1 to {SlotsOpen} are open; the next opens at a summit.");
        if (table == null || table.Knack(knack) is not { Available: true }) throw new TeamFileException("This island has no knack by that name to choose.");
        if (Knacks.FirstOrDefault(k => k.Knack == knack) is { } held && held.Slot != slot) throw new TeamFileException($"That knack is already in slot {held.Slot}.");
        var leaving = Knacks.FirstOrDefault(k => k.Slot == slot);
        if (leaving != null && leaving.Knack == knack) return this;
        var earned = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, rank) in Earned) earned[id] = rank;
        if (leaving != null && leaving.Rank > 1) earned[leaving.Knack] = leaving.Rank;
        var returning = earned.Remove(knack, out var kept) ? kept : 1;
        var chosen = Knacks.Where(k => k.Slot != slot).Append(new ChosenKnack(slot, knack, returning)).OrderBy(k => k.Slot).ToArray();
        return (this with { Knacks = chosen, Earned = Frozen(earned) }).Checked();
    }

    /// <summary>
    /// The team with its Trust count (M8: only the host writes it, from receipts, marks and consequence facts; never a request), within
    /// the schema's bound. The same count returns this record, so an unchanged count writes nothing.
    /// </summary>
    public TeamFile WithTrust(int count)
    {
        var bounded = Math.Clamp(count, -TrustBound, TrustBound);
        return bounded == Trust ? this : (this with { Trust = bounded }).Checked();
    }

    /// <summary>Ranks set aside as the record keeps them: in ordinal order, the shared empty one when there are none.</summary>
    private static IReadOnlyDictionary<string, int> Frozen(SortedDictionary<string, int> earned) =>
        earned.Count == 0 ? NoneEarned : new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(earned);

    /// <summary>The record checked against the schema and validate.py's rules; throws TeamFileException if anything is out of bounds.</summary>
    public TeamFile Checked()
    {
        if (!TeamLayout.ValidTeamId(TeamId)) throw new TeamFileException("The team file names an invalid team.");
        if (CreatedUtc == null || !Utc.IsMatch(CreatedUtc) || !DateTime.TryParse(CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
            throw new TeamFileException("The team file has an invalid creation time.");
        if (!Name(DisplayName) || !Name(GubbleName)) throw new TeamFileException("The team file has an invalid name.");
        if (Costume == null || !Token.IsMatch(Costume)) throw new TeamFileException("The team file has an invalid costume.");
        if (GubbleColour == null || !Colour.IsMatch(GubbleColour)) throw new TeamFileException("The team file has an invalid colour.");
        if (SlotsOpen is < 1 or > 3 || Knacks == null || Knacks.Count > 3) throw new TeamFileException("The team file has invalid knack slots.");
        if (Knacks.Any(k => k == null || k.Slot < 1 || k.Slot > SlotsOpen || k.Rank is < 1 or > 3 || k.Knack == null || !Token.IsMatch(k.Knack)) ||
            Knacks.Select(k => k.Slot).Distinct().Count() != Knacks.Count || Knacks.Select(k => k.Knack).Distinct(StringComparer.Ordinal).Count() != Knacks.Count)
            throw new TeamFileException("The team file has invalid knacks.");
        // Ranks set aside: a knack id, rank 2 or 3 (rank 1 is every knack's start, never kept), never a knack in a slot.
        if (Earned == null || Earned.Count > MaxEarned ||
            Earned.Any(p => p.Key == null || !Token.IsMatch(p.Key) || p.Value is < 2 or > 3 || Knacks.Any(k => string.Equals(k.Knack, p.Key, StringComparison.Ordinal))))
            throw new TeamFileException("The team file has invalid knack ranks.");
        if (Trust is < -TrustBound or > TrustBound) throw new TeamFileException("The team file has an invalid Trust count.");
        return this;
    }

    /// <summary>A display name: one to forty code points of untrusted single-line text with nothing hidden (common.schema.json display_text).</summary>
    private static bool Name(string? text) => text != null && text.Length > 0 && KernelText.CodePoints(text).Length is >= 1 and <= MaxNameLength && !KernelText.HasHidden(text);

    /// <summary>
    /// The file as written: compact JSON in the schema's order, UTF-8 without a BOM, one final LF. knacks.earned only when a rank
    /// is set aside, so a team without one writes the same bytes as before it existed.
    /// </summary>
    public byte[] Serialize()
    {
        Checked();
        var chosen = Knacks.Select(k => new { slot = k.Slot, knack = k.Knack, rank = k.Rank }).ToArray();
        object knacks = Earned.Count == 0
            ? new { slots_open = SlotsOpen, chosen }
            : new { slots_open = SlotsOpen, chosen, earned = new SortedDictionary<string, int>(Earned.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) };
        var document = new
        {
            schema = SchemaId, version = Version, team_id = TeamId, created_utc = CreatedUtc,
            profile = new { display_name = DisplayName, costume = Costume },
            knacks,
            gubble = new { name = GubbleName, colour = GubbleColour },
            trust = new { count = Trust },
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var bytes = new byte[json.Length + 1];
        json.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    /// <summary>The file read strictly: unknown or missing fields, another version, a duplicate key or a value out of bounds refuse it.</summary>
    public static TeamFile Parse(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new TeamFileException("The team file has an invalid size.");
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = CanonicalJson.Parse(bytes);
            var root = document.RootElement;
            Fields(root, "schema", "version", "team_id", "created_utc", "profile", "knacks", "gubble", "trust");
            if (Text(root, "schema") != SchemaId || root.GetProperty("version").GetRawText() != "1") throw new TeamFileException("The team file has an unsupported version.");
            var profile = root.GetProperty("profile");
            Fields(profile, "display_name", "costume");
            var knacks = root.GetProperty("knacks");
            // knacks.earned is optional (the review's finding 2): a team file from before it reads as it is.
            var hasEarned = knacks.ValueKind == JsonValueKind.Object && knacks.TryGetProperty("earned", out _);
            if (hasEarned) Fields(knacks, "slots_open", "chosen", "earned");
            else Fields(knacks, "slots_open", "chosen");
            var chosen = knacks.GetProperty("chosen");
            if (chosen.ValueKind != JsonValueKind.Array || chosen.GetArrayLength() > 3) throw new TeamFileException("The team file has invalid knacks.");
            var list = new List<ChosenKnack>();
            foreach (var entry in chosen.EnumerateArray())
            {
                Fields(entry, "slot", "knack", "rank");
                list.Add(new ChosenKnack(Whole(entry.GetProperty("slot")), Text(entry, "knack"), Whole(entry.GetProperty("rank"))));
            }
            var earned = NoneEarned;
            if (hasEarned)
            {
                var kept = knacks.GetProperty("earned");
                if (kept.ValueKind != JsonValueKind.Object) throw new TeamFileException("The team file has invalid knack ranks.");
                var ranks = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var entry in kept.EnumerateObject())
                {
                    if (ranks.Count >= MaxEarned || !ranks.TryAdd(entry.Name, Whole(entry.Value))) throw new TeamFileException("The team file has invalid knack ranks.");
                }
                earned = Frozen(ranks);
            }
            var gubble = root.GetProperty("gubble");
            Fields(gubble, "name", "colour");
            var trust = root.GetProperty("trust");
            Fields(trust, "count");
            return new TeamFile
            {
                TeamId = Text(root, "team_id"), CreatedUtc = Text(root, "created_utc"),
                DisplayName = Text(profile, "display_name"), Costume = Text(profile, "costume"),
                SlotsOpen = Whole(knacks.GetProperty("slots_open")), Knacks = list, Earned = earned,
                GubbleName = Text(gubble, "name"), GubbleColour = Text(gubble, "colour"), Trust = Whole(trust.GetProperty("count"), allowNegative: true),
            }.Checked();
        }
        catch (Exception error) when (error is CanonicalJsonException or JsonException or InvalidOperationException or FormatException or OverflowException or DecoderFallbackException or KeyNotFoundException)
        {
            throw new TeamFileException("The team file is not a valid save file.");
        }
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length || value.EnumerateObject().Any(f => !names.Contains(f.Name, StringComparer.Ordinal)))
            throw new TeamFileException("The team file has unsupported fields.");
    }

    private static string Text(JsonElement value, string name) =>
        value.GetProperty(name) is { ValueKind: JsonValueKind.String } text ? text.GetString()! : throw new TeamFileException("The team file has an invalid value.");

    private static int Whole(JsonElement value, bool allowNegative = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.GetRawText().All(c => char.IsAsciiDigit(c) || (allowNegative && c == '-')) || !value.TryGetInt32(out var whole))
            throw new TeamFileException("The team file has an invalid number.");
        return whole;
    }
}

/// <summary>
/// The team file on disk: strict, bounded, written atomically under an exclusive lease (a temporary file flushed, then moved
/// over); a file that changed under it (another session) or cannot be read is kept, never overwritten, and the store refuses
/// to write until it is loaded again. Only the host (knack.set, Trust) and the menu write it; nothing a request carries
/// does, and the companion surface never sees it.
/// </summary>
public sealed class TeamStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private byte[]? _baseline;
    private bool _loaded;
    private bool _readOnly;

    public string TeamId { get; }
    public TeamStore(string absolutePath, string teamId)
    {
        if (string.IsNullOrEmpty(absolutePath) || !Path.IsPathFullyQualified(absolutePath)) throw new TeamFileException("The team file needs an absolute path.");
        if (!TeamLayout.ValidTeamId(teamId)) throw new TeamFileException("The team file names an invalid team.");
        _path = Path.GetFullPath(absolutePath);
        TeamId = teamId;
    }

    public bool Exists => File.Exists(_path);

    /// <summary>The team file, or null when there is none yet. A file that does not read (or names another team) locks the store and throws.</summary>
    public TeamFile? Load()
    {
        lock (_gate)
        {
            try
            {
                var bytes = Read(_path);
                var team = bytes == null ? null : TeamFile.Parse(bytes);
                if (team != null && team.TeamId != TeamId) throw new TeamFileException("The team file is filed under another team.");
                _baseline = bytes;
                _loaded = true;
                _readOnly = false;
                return team;
            }
            catch (Exception error) when (error is TeamFileException || FileError(error))
            {
                _readOnly = true;
                throw error as TeamFileException ?? new TeamFileException("The team file could not be read. It was kept as it was.");
            }
        }
    }

    public void Save(TeamFile team)
    {
        if (team == null || team.TeamId != TeamId) throw new TeamFileException("The team file names another team.");
        lock (_gate)
        {
            if (!_loaded || _readOnly) throw new TeamFileException("The team file is waiting to be loaded.");
            var bytes = team.Serialize();
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var current = Read(_path);
                if (!(current == null ? _baseline == null : _baseline != null && current.AsSpan().SequenceEqual(_baseline)))
                {
                    _readOnly = true;
                    throw new TeamFileException("The team file changed in another session. Reload before saving.");
                }
                if (current != null && current.AsSpan().SequenceEqual(bytes)) return;
                temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                File.Move(temporary, _path, overwrite: true);
                temporary = null;
                _baseline = bytes;
            }
            catch (Exception error) when (FileError(error)) { throw new TeamFileException("The team file could not be saved. The previous one was kept."); }
            finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception error) when (FileError(error)) { } }
        }
    }

    private static byte[]? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > TeamFile.MaxBytes) throw new TeamFileException("The team file has an invalid size.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
