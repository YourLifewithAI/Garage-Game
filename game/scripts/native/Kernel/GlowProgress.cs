using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Kernel;

/// <summary>A fixed, player-safe error: saved text and filesystem paths never become Gubble dialogue.</summary>
public sealed class GlowProgressException(string message) : Exception(message);

/// <summary>
/// Host-owned progress for Glow: the verified uses, the expertise learned (the code's discoveries) and the hints delivered, with
/// Glow's touch and sound, and beside it the same three for the other abilities (Bubbles, Fire: Others, M11). It is the
/// team's whole expertise store, across modes (decision 2: expertise travels), independent of room saves and physical player
/// traits. Its top-level fields are Glow's, as before; ExpertiseCount sums every ability for the pips. Only completed host observations may advance it; model prose is not evidence. Discovery IDs name behaviours
/// verified by the host; strings from commands or journals cannot award them.
/// </summary>
public sealed class GlowProgress
{
    public const int MaxSuccessfulCasts = 1_000_000;
    public const int HintAfterCasts = 10;
    public const string Lantern = "lantern";
    public const string Torch = "torch";
    public const string Bioluminesce = "bioluminesce";
    public const string Constellation = "constellation";
    public const string Skylight = "skylight";
    public const string LanternHint = "lantern_self";

    internal static readonly string[] DiscoveryOrder = { Lantern, Torch, Bioluminesce, Constellation, Skylight };
    internal static readonly string[] HintOrder = { LanternHint, "torch_high_place", "bioluminesce_water", "constellation_nearby", "skylight_sky" };
    public static readonly IReadOnlyList<string> Touches = Array.AsReadOnly(new[] { "plain", "air", "ember", "frost", "chime" });
    private static readonly IReadOnlyDictionary<string, ExpertiseRecord> NoOthers = new ReadOnlyDictionary<string, ExpertiseRecord>(new Dictionary<string, ExpertiseRecord>());

    public static GlowProgress Fresh { get; } = new(0, Array.Empty<string>(), Array.Empty<string>(), NoOthers);
    public int SuccessfulCasts { get; }
    public IReadOnlyList<string> Discoveries { get; }
    public IReadOnlyList<string> DeliveredHints { get; }
    /// <summary>
    /// The other abilities' expertise (bubbles and fire), by ability id, only those with something recorded.
    /// Glow's own fields above stay the founder's original store: this block is what version 3's other ability keys hold.
    /// </summary>
    public IReadOnlyDictionary<string, ExpertiseRecord> Others { get; }
    public string SelectedTouch { get; }
    public double SoundVolume { get; }
    public bool SoundMuted { get; }
    public int ActiveLightAllowance => Discoveries.Count >= 5 ? 5 : Discoveries.Count >= 2 ? 4 : 3;
    /// <summary>
    /// Every expertise the pair learned, in every ability: what the Gubble's pips count (one pip per five, MECHANICS section 5).
    /// </summary>
    public int ExpertiseCount => Discoveries.Count + Others.Values.Sum(record => record.Discoveries.Count);

    private GlowProgress(int successfulCasts, string[] discoveries, string[] deliveredHints, IReadOnlyDictionary<string, ExpertiseRecord> others,
        string touch = "plain", double volume = 0.35, bool muted = false)
    {
        SuccessfulCasts = successfulCasts;
        Discoveries = new ReadOnlyCollection<string>(discoveries);
        DeliveredHints = new ReadOnlyCollection<string>(deliveredHints);
        Others = others;
        SelectedTouch = touch;
        SoundVolume = volume;
        SoundMuted = muted;
    }

    public bool HasDiscovery(string id) => Discoveries.Contains(id, StringComparer.Ordinal);

    /// <summary>One ability's record: Glow's from its own fields, another's as kept (empty when nothing is recorded there). Null for an ability the store does not know.</summary>
    public ExpertiseRecord? Of(string ability)
    {
        if (ExpertiseLists.Find(ability) is not { } list) return null;
        if (ability == ExpertiseLists.Glow) return ExpertiseRecord.Unchecked(list, SuccessfulCasts, Discoveries, DeliveredHints);
        return Others.TryGetValue(ability, out var record) ? record : ExpertiseRecord.Empty(list);
    }

    /// <summary>Whether an expertise is learned, in any ability (an unknown ability or id is simply not learned).</summary>
    public bool Has(string ability, string id) => ability == ExpertiseLists.Glow ? HasDiscovery(id) : Others.TryGetValue(ability, out var record) && record.Has(id);

    /// <summary>Whether two records hold the same progress and settings (a migration's verification reads its write back with this).</summary>
    public bool Same(GlowProgress other) => ReferenceEquals(this, other) || (other != null && SuccessfulCasts == other.SuccessfulCasts &&
        Discoveries.SequenceEqual(other.Discoveries, StringComparer.Ordinal) && DeliveredHints.SequenceEqual(other.DeliveredHints, StringComparer.Ordinal) &&
        SelectedTouch == other.SelectedTouch && SoundVolume == other.SoundVolume && SoundMuted == other.SoundMuted &&
        Others.Count == other.Others.Count && Others.All(pair => other.Others.TryGetValue(pair.Key, out var theirs) && pair.Value.Same(theirs)));

    /// <summary>
    /// An expertise the host verified from the simulation, outside a cast of its own (the Bubble's carry, boat and lift): learned
    /// once, however often it happens again. Returns this progress when it is already learned. A candidate to save, like any change.
    /// An id the ability's list does not hold, an ability the store does not know, and Glow's own (its expertise are recorded by
    /// AfterObservedCast) are refused.
    /// </summary>
    public GlowProgress WithExpertise(string ability, string id)
    {
        var list = ExpertiseLists.Find(ability);
        if (list == null || ability == ExpertiseLists.Glow) throw new GlowProgressException("Expertise has an unknown ability.");
        var before = Of(ability)!;
        var after = before.WithLearned(id);
        return ReferenceEquals(after, before) ? this : WithRecord(after);
    }

    /// <summary>
    /// A committed cast of another ability the host verified (Grow's): its record counts the cast, learns what it showed once each and
    /// delivers its hints as Glow's do (ExpertiseRecord.AfterCast). A candidate to save, like any change.
    /// </summary>
    public GlowProgress AfterAbilityCast(string ability, IEnumerable<string> learned)
    {
        var list = ExpertiseLists.Find(ability);
        if (list == null || ability == ExpertiseLists.Glow) throw new GlowProgressException("Expertise has an unknown ability.");
        return WithRecord(Of(ability)!.AfterCast(learned));
    }

    private GlowProgress WithRecord(ExpertiseRecord record)
    {
        var others = Others.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        others[record.Ability] = record;
        return Restore(SuccessfulCasts, Discoveries, DeliveredHints, SelectedTouch, SoundVolume, SoundMuted, others.Values);
    }

    /// <summary>
    /// Builds a candidate only. The host must save it successfully before publishing it. A self Glow is
    /// Lantern; a plain point Glow counts practice. Other discoveries require AfterObservedCast's
    /// verified host observation. Curated nudges are remembered once across reloads.
    /// </summary>
    public GlowProgress AfterCast(bool lantern) => AfterObservedCast(lantern ? Lantern : null);

    public GlowProgress AfterObservedCast(string? discovery, bool constellation = false)
    {
        var count = Math.Min(MaxSuccessfulCasts, SuccessfulCasts + 1);
        var discoveries = Discoveries.ToList();
        var hints = DeliveredHints.ToList();
        if (discovery != null && !DiscoveryOrder.Contains(discovery, StringComparer.Ordinal))
            throw new GlowProgressException("Glow progress has an invalid discovery.");
        if (discovery != null && !HasDiscovery(discovery)) discoveries.Add(discovery);
        if (constellation && !discoveries.Contains(Constellation)) discoveries.Add(Constellation);
        if (count >= HintAfterCasts * (hints.Count + 1))
            for (var index = 0; index < DiscoveryOrder.Length; index++)
                if (!discoveries.Contains(DiscoveryOrder[index]) && !hints.Contains(HintOrder[index]))
                { hints.Add(HintOrder[index]); break; }
        if (count == SuccessfulCasts && discoveries.Count == Discoveries.Count && hints.Count == DeliveredHints.Count)
            return this;
        return Restore(count, discoveries, hints, SelectedTouch, SoundVolume, SoundMuted, Others.Values);
    }

    public GlowProgress WithConfiguration(string touch, double volume, bool muted) =>
        Restore(SuccessfulCasts, Discoveries, DeliveredHints, touch, volume, muted, Others.Values);

    /// <summary>
    /// One team's expertise from the records each mode kept before the teams packet (version 2 kept one file per mode): the union.
    /// Every expertise learned in any mode and every hint delivered in any mode is kept; the verified uses are the sum, since each
    /// mode counted its own casts and none was counted twice (and a sum keeps a hint's ten uses each, where the larger count alone
    /// could not). Glow's touch and sound are the first record's (the caller lists Creative first: the original trial's).
    /// Throws GlowProgressException if no record is given; the result is checked as any restored record is.
    /// </summary>
    public static GlowProgress Union(IReadOnlyList<GlowProgress> records)
    {
        if (records == null || records.Count == 0 || records.Any(r => r == null)) throw new GlowProgressException("Glow progress has no records to merge.");
        var first = records[0];
        var casts = (int)Math.Min(MaxSuccessfulCasts, records.Sum(r => (long)r.SuccessfulCasts));
        var found = DiscoveryOrder.Where(id => records.Any(r => r.HasDiscovery(id)));
        var hints = HintOrder.Where(id => records.Any(r => r.DeliveredHints.Contains(id, StringComparer.Ordinal)));
        return Restore(casts, found, hints, first.SelectedTouch, first.SoundVolume, first.SoundMuted);
    }

    /// <summary>
    /// Validated restoration for the host's local save, never a command or companion input. others are the other abilities' records
    /// (each from ExpertiseRecord.Restore, so already checked against its own list); one an ability twice, Glow's key, or an empty
    /// record (nothing recorded: the store does not keep it) is normalised or refused.
    /// </summary>
    public static GlowProgress Restore(int successfulCasts, IEnumerable<string> discoveries, IEnumerable<string> deliveredHints,
        string touch = "plain", double volume = 0.35, bool muted = false, IEnumerable<ExpertiseRecord>? others = null)
    {
        if (successfulCasts < 0 || successfulCasts > MaxSuccessfulCasts)
            throw new GlowProgressException("Glow progress has an invalid use count.");
        var found = ValidateIds(discoveries, DiscoveryOrder, "Glow progress has invalid discoveries.");
        var hints = ValidateIds(deliveredHints, HintOrder, "Glow progress has invalid hint records.");
        // A cast may complete both its own variant and the three-point constellation.
        if (found.Length > successfulCasts + (found.Contains(Constellation) ? 1 : 0) ||
            (successfulCasts == 0 && found.Length > 0) || successfulCasts < hints.Length * HintAfterCasts)
            throw new GlowProgressException("Glow progress has inconsistent use records.");
        if (!Touches.Contains(touch, StringComparer.Ordinal) || !double.IsFinite(volume) || volume is < 0 or > 1)
            throw new GlowProgressException("Glow progress has invalid touch or sound settings.");
        var kept = new Dictionary<string, ExpertiseRecord>(StringComparer.Ordinal);
        foreach (var record in others ?? Array.Empty<ExpertiseRecord>())
        {
            if (record == null || record.Ability == ExpertiseLists.Glow || !ReferenceEquals(ExpertiseLists.Find(record.Ability), record.List) || kept.ContainsKey(record.Ability))
                throw new GlowProgressException("Glow progress has invalid abilities.");
            if (!record.IsEmpty) kept[record.Ability] = record;
        }
        // In the store's own order (the catalog's), so equal progress is equal bytes.
        var ordered = new Dictionary<string, ExpertiseRecord>(StringComparer.Ordinal);
        foreach (var list in ExpertiseLists.All)
            if (kept.TryGetValue(list.Ability, out var record)) ordered[list.Ability] = record;
        return new GlowProgress(successfulCasts, found, hints, kept.Count == 0 ? NoOthers : new ReadOnlyDictionary<string, ExpertiseRecord>(ordered), touch, volume, muted);
    }

    private static string[] ValidateIds(IEnumerable<string> values, string[] known, string error)
    {
        if (values == null) throw new GlowProgressException(error);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (value == null || !known.Contains(value, StringComparer.Ordinal) || !seen.Add(value))
                throw new GlowProgressException(error);
        return known.Where(seen.Contains).ToArray();
    }
}

/// <summary>Injectable storage keeps tests isolated and lets the host stage a cast before publishing progress.</summary>
public interface IGlowProgressStore
{
    GlowProgress Load();
    void Save(GlowProgress state);
}

/// <summary>
/// The team's expertise store (contracts/team.schema.json $defs/expertise_store: enfractal.glow_progress version 3, the team
/// folder's expertise.json), strict and bounded. Version 3 keys the expertise by ability (abilities.glow, bubbles and fire,
/// each against its own fixed list of ids: ExpertiseLists; a key the code does not know is a newer save's and locks the store
/// read-only whole, never dropped) and names its team. A Glow-only store is the founder's original, byte for byte, and is
/// rewritten unchanged until something new is learned. Version 3 stays one version: the contract's abilities map holds up to
/// sixteen keys "without a new version". Versions 1 and 2 (one file per mode, user://single_player/progression/glow_trial_v1.json and its .challenge and
/// .live siblings) are read, never written: the teams packet's migration (Saves/SaveMigration.cs) merges them as their union.
/// A store always writes version 3. Its team is the folder's name when the file is &lt;team&gt;/expertise.json, "local"
/// otherwise (a test's own path). Load errors lock this instance against writes, preserving the original file. A persistent
/// sidecar lease excludes other store writers across game instances, then saves replace a flushed temporary file atomically. A
/// byte comparison detects external changes optimistically; unrelated tools that do not honour the lease are not coordinated
/// writers. No raw names, hints, dialogue or other free text is stored here.
/// </summary>
public sealed class LocalGlowProgressStore : IGlowProgressStore
{
    /// <summary>The default team's store (Saves/TeamLayout.cs); the boot points the host at its team's.</summary>
    public const string DefaultUserPath = "user://teams/local/expertise.json";
    /// <summary>Version 2's Creative record (the original trial path); Challenge's and Live's add .challenge and .live before .json.</summary>
    public const string LegacyUserPath = "user://single_player/progression/glow_trial_v1.json";
    public const string FileName = "expertise.json";
    public const string SchemaId = "enfractal.glow_progress";
    public const int Version = 3;
    public const string LocalTeamId = "local";
    /// <summary>Glow's key in version 3's abilities (the other abilities' keys and lists are ExpertiseLists').</summary>
    public const string GlowAbility = "glow";
    public const int MaxSaveBytes = 4096;
    private static readonly string[] Fields = { "schema", "version", "successful_casts", "discoveries", "delivered_hints" };
    private static readonly string[] VersionTwoFields = Fields.Concat(new[] { "touch", "sound_volume", "muted" }).ToArray();
    private static readonly string[] VersionThreeFields = { "schema", "version", "team_id", "abilities", "touch", "sound_volume", "muted" };
    private static readonly string[] AbilityFields = { "successful_casts", "discoveries", "delivered_hints" };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _path;
    private readonly string _teamId;
    private readonly object _gate = new();
    private bool _loaded;
    private bool _readOnly;
    private byte[]? _lastBytes;

    public bool IsReadOnly { get { lock (_gate) return _readOnly; } }
    /// <summary>The team this store writes into its documents.</summary>
    public string TeamId => _teamId;

    /// <summary>Version 2's per-mode file for a mode (Creative's is the original trial path), read for migration only.</summary>
    public static string LegacyPathFor(string mode) =>
        mode == "creative" ? LegacyUserPath : LegacyUserPath.Replace(".json", "." + mode + ".json", StringComparison.Ordinal);

    /// <param name="absolutePath">The host globalizes its team's path; tests supply their own absolute scratch path.</param>
    public LocalGlowProgressStore(string absolutePath)
    {
        try
        {
            if (string.IsNullOrEmpty(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
                throw new GlowProgressException("Glow progress needs an absolute local save path.");
            _path = Path.GetFullPath(absolutePath);
            if (string.IsNullOrEmpty(Path.GetFileName(_path)) || string.IsNullOrEmpty(Path.GetDirectoryName(_path)))
                throw new GlowProgressException("Glow progress needs a local save filename.");
            var folder = Path.GetFileName(Path.GetDirectoryName(_path)!);
            _teamId = Path.GetFileName(_path) == FileName && Saves.TeamLayout.ValidTeamId(folder) ? folder : LocalTeamId;
        }
        catch (Exception error) when (IsFileError(error) || error is ArgumentException or NotSupportedException)
        {
            throw new GlowProgressException("Glow progress has an invalid local save path.");
        }
    }

    public GlowProgress Load()
    {
        lock (_gate)
        {
            try
            {
                var bytes = ReadBytes();
                var state = bytes == null ? GlowProgress.Fresh : Parse(bytes);
                _lastBytes = bytes;
                _loaded = true;
                _readOnly = false;
                return state;
            }
            catch (GlowProgressException)
            {
                _readOnly = true;
                throw;
            }
            catch (Exception error) when (IsFileError(error))
            {
                _readOnly = true;
                throw new GlowProgressException("Glow progress could not be read. The existing save was kept.");
            }
        }
    }

    public void Save(GlowProgress state)
    {
        if (state == null) throw new GlowProgressException("Glow progress is missing.");
        lock (_gate)
        {
            if (!_loaded || _readOnly)
                throw new GlowProgressException("Glow progress is read-only until its save can be loaded.");
            string? temporary = null;
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                // Never delete this file: removing a released lockfile can split waiting writers onto
                // different files. FileShare.None fails immediately if another game instance holds it.
                using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var current = ReadBytes();
                if (!SameBytes(current, _lastBytes))
                {
                    _readOnly = true;
                    throw new GlowProgressException("Glow progress changed outside this session. Reload it before continuing.");
                }
                var bytes = Serialize(state, _teamId);
                temporary = Path.Combine(directory, Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                temporary = null;
                _lastBytes = bytes;
            }
            catch (Exception error) when (IsFileError(error))
            {
                throw new GlowProgressException("Glow progress could not be saved. The previous progress was kept.");
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (IsFileError(error)) { }
                }
            }
        }
    }

    /// <summary>A store's document of any version as Glow's record (versions 1 and 2 for migration, 3 as written).</summary>
    public static GlowProgress Parse(byte[] bytes) => ParseDocument(bytes).Progress;

    /// <summary>What a document says: its version, its team (version 3 only; null before) and Glow's record.</summary>
    public readonly record struct Document(int Version, string? TeamId, GlowProgress Progress);

    public static Document ParseDocument(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxSaveBytes)
            throw new GlowProgressException("Glow progress has an invalid file size.");
        JsonDocument document;
        try
        {
            // Utf8JsonReader can defer validating string bytes until a value is accessed. Check the
            // complete buffer here, so malformed UTF-8 always takes the same safe load-error path.
            StrictUtf8.GetCharCount(bytes);
            document = CanonicalJson.Parse(bytes);
        }
        catch (DecoderFallbackException) { throw new GlowProgressException("Glow progress is not valid UTF-8."); }
        catch (CanonicalJsonException) { throw new GlowProgressException("Glow progress is not a valid save file."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new GlowProgressException("Glow progress has unsupported fields.");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) || versionNumber is not (1 or 2 or Version) || version.GetRawText() != versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new GlowProgressException("Glow progress has an unsupported save version.");
            var allowedFields = versionNumber switch { 1 => Fields, 2 => VersionTwoFields, _ => VersionThreeFields };
            ExactFields(root, allowedFields);
            if (root.GetProperty("schema").ValueKind != JsonValueKind.String || root.GetProperty("schema").GetString() != SchemaId)
                throw new GlowProgressException("Glow progress has an unsupported save format.");
            string? team = null;
            var record = root;
            var glowKept = true;
            var others = new List<ExpertiseRecord>();
            if (versionNumber == Version)
            {
                var id = root.GetProperty("team_id");
                if (id.ValueKind != JsonValueKind.String || !Saves.TeamLayout.ValidTeamId(id.GetString()!))
                    throw new GlowProgressException("Glow progress names an invalid team.");
                team = id.GetString();
                // Keyed by ability, each against its own list. A key the code does not know is a newer save's: refused whole and
                // kept (fail-closed), never dropped on the next write. Glow's key may be missing (a store that learned only
                // another ability so far): Glow starts fresh.
                var abilities = root.GetProperty("abilities");
                if (abilities.ValueKind != JsonValueKind.Object || abilities.EnumerateObject().Count() == 0 ||
                    abilities.EnumerateObject().Any(property => ExpertiseLists.Find(property.Name) == null))
                    throw new GlowProgressException("Glow progress has unsupported abilities.");
                // Bloom became Grow (decision 52): an older store's "bloom" is Grow's record, written as "grow" with the next save;
                // a store holding both is refused whole, as anything ambiguous is.
                if (abilities.TryGetProperty(ExpertiseLists.LegacyBloom, out _) && abilities.TryGetProperty(ExpertiseLists.Grow, out _))
                    throw new GlowProgressException("Glow progress holds both Bloom's and Grow's records.");
                foreach (var list in ExpertiseLists.All.Where(list => list.Ability != GlowAbility))
                {
                    if (!abilities.TryGetProperty(list.Ability, out var other) &&
                        !(list.Ability == ExpertiseLists.Grow && abilities.TryGetProperty(ExpertiseLists.LegacyBloom, out other))) continue;
                    ExactFields(other, AbilityFields);
                    others.Add(ExpertiseRecord.Restore(list, UseCount(other), Ids(other.GetProperty("discoveries"), 5), Ids(other.GetProperty("delivered_hints"), 5)));
                }
                glowKept = abilities.TryGetProperty(GlowAbility, out record);
                if (glowKept) ExactFields(record, AbilityFields);
            }
            var count = glowKept ? UseCount(record) : 0;
            var hints = glowKept ? Ids(record.GetProperty("delivered_hints"), versionNumber == 1 ? 1 : 5) : Array.Empty<string>();
            if (versionNumber == 1 && hints.Any(id => id != GlowProgress.LanternHint))
                throw new GlowProgressException("Glow progress has invalid legacy hint records.");
            var found = glowKept ? Ids(record.GetProperty("discoveries"), 5) : Array.Empty<string>();
            if (versionNumber == 1) return new Document(1, null, GlowProgress.Restore(count, found, hints));
            var touch = root.GetProperty("touch");
            var volume = root.GetProperty("sound_volume");
            var muted = root.GetProperty("muted");
            if (touch.ValueKind != JsonValueKind.String || volume.ValueKind != JsonValueKind.Number ||
                !volume.TryGetDouble(out var level) || muted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GlowProgressException("Glow progress has invalid touch or sound settings.");
            return new Document(versionNumber, team, GlowProgress.Restore(count, found, hints, touch.GetString()!, level, muted.GetBoolean(), others));
        }
    }

    private static int UseCount(JsonElement record)
    {
        var casts = record.GetProperty("successful_casts");
        if (casts.ValueKind != JsonValueKind.Number || !casts.TryGetInt32(out var count))
            throw new GlowProgressException("Glow progress has an invalid use count.");
        return count;
    }

    /// <summary>Version 3, as the store writes it: compact JSON, UTF-8 without a BOM, one final LF (also on Windows).</summary>
    public static byte[] Serialize(GlowProgress state, string teamId = LocalTeamId)
    {
        if (state == null) throw new GlowProgressException("Glow progress is missing.");
        if (teamId == null || !Saves.TeamLayout.ValidTeamId(teamId)) throw new GlowProgressException("Glow progress names an invalid team.");
        // Glow's key first and always (a Glow-only store is the original, byte for byte), then every other ability that has
        // something recorded, in the catalog's order.
        var abilities = new Dictionary<string, object>
        {
            [GlowAbility] = new
            {
                successful_casts = state.SuccessfulCasts,
                discoveries = state.Discoveries,
                delivered_hints = state.DeliveredHints,
            },
        };
        foreach (var list in ExpertiseLists.All)
            if (state.Others.TryGetValue(list.Ability, out var record))
                abilities[list.Ability] = new { successful_casts = record.SuccessfulCasts, discoveries = record.Discoveries, delivered_hints = record.DeliveredHints };
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = SchemaId,
            version = Version,
            team_id = teamId,
            abilities,
            touch = state.SelectedTouch,
            sound_volume = state.SoundVolume,
            muted = state.SoundMuted,
        });
        var bytes = new byte[json.Length + 1];
        json.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    private byte[]? ReadBytes()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxSaveBytes)
                throw new GlowProgressException("Glow progress has an invalid file size.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void ExactFields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            throw new GlowProgressException("Glow progress has unsupported fields.");
    }

    private static string[] Ids(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum ||
            value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new GlowProgressException("Glow progress has invalid discovery or hint records.");
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static bool SameBytes(byte[]? first, byte[]? second) =>
        first == null ? second == null : second != null && first.AsSpan().SequenceEqual(second);

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
