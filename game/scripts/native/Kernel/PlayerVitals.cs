using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Kernel;

public enum PlayerDamageSource { Fall, Fire, Critter, Player }
public sealed class PlayerVitalsException(string message) : Exception(message);

/// <summary>
/// The Fractals found in one mode and how they were spent at camps (contracts/team.schema.json $defs/vitals): Found counts
/// every one picked up, Unspent what is in hand, Hearts and Bands what was bought. Typed Fractals (the pack's flag) keep the
/// unspent ones by kind as well.
/// </summary>
public sealed record PlayerFractals(int Found, int Unspent, int Hearts, int Bands, int? TypedHeart = null, int? TypedBand = null)
{
    public static PlayerFractals None { get; } = new(0, 0, 0, 0);
    public bool Typed => TypedHeart != null;

    /// <summary>The contract's bounds and validate.py's checks: what is in hand was found, and typed ones add up.</summary>
    public bool Valid => Found is >= 0 and <= 100000 && Unspent >= 0 && Unspent <= Found && Hearts is >= 0 and <= 29 && Bands is >= 0 and <= 19 &&
        (TypedHeart == null) == (TypedBand == null) && (TypedHeart == null || (TypedHeart >= 0 && TypedBand >= 0 && TypedHeart + TypedBand == Unspent));

    /// <summary>One more found and in hand (a typed one by its kind: "heart" or "band").</summary>
    public PlayerFractals Picked(string? kind) => kind == null
        ? this with { Found = Found + 1, Unspent = Unspent + 1 }
        : this with { Found = Found + 1, Unspent = Unspent + 1, TypedHeart = (TypedHeart ?? 0) + (kind == "heart" ? 1 : 0), TypedBand = (TypedBand ?? 0) + (kind == "band" ? 1 : 0) };

    /// <summary>The Fractals in hand for growing this ("heart" or "band"): of that kind when typed is set, else every one unspent.</summary>
    public int InHand(string grow, bool typed) => typed ? (grow == "heart" ? TypedHeart : TypedBand) ?? 0 : Unspent;

    /// <summary>
    /// price Fractals spent at a camp (fractal.spend) for one more heart or band. Typed counts give up the named kind first (all
    /// of the price under the typed flag, which InHand checked), then the other, so they still add up to what is in hand.
    /// </summary>
    public PlayerFractals Spent(string grow, int price)
    {
        var heart = grow == "heart";
        var next = this with { Unspent = Unspent - price, Hearts = Hearts + (heart ? 1 : 0), Bands = Bands + (heart ? 0 : 1) };
        if (!Typed) return next;
        var own = Math.Min(price, heart ? TypedHeart!.Value : TypedBand!.Value);
        var other = price - own;
        return next with
        {
            TypedHeart = TypedHeart!.Value - (heart ? own : other),
            TypedBand = TypedBand!.Value - (heart ? other : own),
        };
    }
}

/// <summary>
/// One mode's body (contracts/team.schema.json $defs/vitals, the vitals store's version 2): health in points with its maximum
/// as a field, the stamina bar in metres of climbing with its maximum, the climbs completed, the recoveries, the Fractals and
/// the connection-energy pool in whole pips. Immutable: every change is a new record. What the numbers mean (quarters,
/// bands, the pool's maximum) is the island's rules (VitalsRules); the methods here without rules are today's, the legacy
/// rules of a pack with no traits block.
/// </summary>
public sealed class PlayerVitals
{
    // Today's numbers (a pack without traits.hearts and traits.stamina): kept as the legacy rules and for the body's tumble.
    public const float MaximumHealth = 100;
    public const float InitialStamina = 0.45f;
    public const float MaximumStamina = 3.0f;
    public const float StaminaReward = 0.10f;
    public const float GroundRecoveryPerSecond = 0.10f;
    public const float HoldingCostPerSecond = 0.01f;
    public const float MinimumChallengeRise = 0.25f;
    public const float SafeFallHeight = 0.30f;
    /// <summary>Free fall through SafeFallHeight under world_physics_profile.gd DEFAULT's 3.5 m/s² gravity.</summary>
    public static readonly float SafeLandingSpeedMps = MathF.Sqrt(2 * 3.5f * SafeFallHeight);
    /// <summary>A default-gravity 1.5 m fall lands at about 3.24 m/s and costs about 96 health.</summary>
    public const float FallDamagePerMps = 53.6f;
    public const int MaximumChallenges = 256;
    /// <summary>The contract's bounds on a body (player.status, the vitals store): 30 hearts with Big heart, the top band with Big bar.</summary>
    public const float ContractMaxHealth = 720;
    public const float ContractMaxStamina = 4.8f;
    public const int ContractMaxPips = 40;
    /// <summary>A pool read from a version 1 save, which had none: the rules fill it (a new pool is full).</summary>
    public const int UnsetPool = -1;

    public float Health { get; }
    public float MaxHealth { get; }
    public float Stamina { get; }
    public float MaxStamina { get; }
    public IReadOnlyList<string> Challenges { get; }
    public int CompletedClimbs => Challenges.Count;
    public int Recoveries { get; }
    public PlayerFractals Fractals { get; }
    /// <summary>The connection-energy pool in whole pips (in Creative the shadow pool); UnsetPool until the rules fill it.</summary>
    public int Pips { get; }

    /// <summary>Today's fresh body: 100 health, 0.45 m of stamina, no pool. A pack's rules make their own (VitalsRules.Fresh).</summary>
    public static PlayerVitals Fresh { get; } = new(MaximumHealth, MaximumHealth, InitialStamina, InitialStamina, Array.Empty<string>(), 0, PlayerFractals.None, UnsetPool);

    private PlayerVitals(float health, float maxHealth, float stamina, float maxStamina, string[] challenges, int recoveries, PlayerFractals fractals, int pips)
    {
        Health = health;
        MaxHealth = maxHealth;
        Stamina = stamina;
        MaxStamina = maxStamina;
        Challenges = Array.AsReadOnly(challenges);
        Recoveries = recoveries;
        Fractals = fractals;
        Pips = pips;
    }

    /// <summary>
    /// A body from its fields, checked against the contract's bounds (contracts/team.schema.json $defs/vitals and validate.py):
    /// health and stamina finite, within their maxima and the contract's; climbs distinct 64-hex ids, at most 256; the
    /// Fractals consistent; the pool 0 to 40 (or unset). Throws PlayerVitalsException for anything else.
    /// </summary>
    public static PlayerVitals Of(float health, float maxHealth, float stamina, float maxStamina, IEnumerable<string> challenges, int recoveries,
        PlayerFractals fractals, int pips)
    {
        var entries = challenges?.ToArray() ?? throw new PlayerVitalsException("Player traits have no challenge records.");
        if (entries.Length > MaximumChallenges || entries.Any(id => id == null || id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c))) ||
            entries.Distinct(StringComparer.Ordinal).Count() != entries.Length || recoveries < 0 || recoveries > 1_000_000 ||
            !float.IsFinite(maxHealth) || maxHealth < 1 || maxHealth > ContractMaxHealth || !float.IsFinite(health) || health < 0 || health > maxHealth ||
            !float.IsFinite(maxStamina) || maxStamina <= 0 || maxStamina > ContractMaxStamina + 0.00001f ||
            !float.IsFinite(stamina) || stamina < 0 || stamina > maxStamina + 0.00001f || fractals is not { Valid: true } || pips < UnsetPool || pips > ContractMaxPips)
            throw new PlayerVitalsException("Player traits contain invalid values.");
        return new PlayerVitals(health, maxHealth, Math.Min(stamina, maxStamina), maxStamina, entries.OrderBy(id => id, StringComparer.Ordinal).ToArray(), recoveries, fractals, pips);
    }

    /// <summary>Version 1's record (today's rules): its maximum stamina grew with each completed climb.</summary>
    public static PlayerVitals Restore(float health, float stamina, IEnumerable<string> challenges, int recoveries)
    {
        var entries = challenges?.ToArray() ?? throw new PlayerVitalsException("Player traits have no challenge records.");
        if (health > MaximumHealth) throw new PlayerVitalsException("Player traits contain invalid values.");
        return Of(health, MaximumHealth, stamina, LegacyMaxStamina(entries.Length), entries, recoveries, PlayerFractals.None, UnsetPool);
    }

    internal static float LegacyMaxStamina(int climbs) => Math.Min(MaximumStamina, InitialStamina + climbs * StaminaReward);

    /// <summary>A copy with some fields changed; the maxima bound health and stamina.</summary>
    internal PlayerVitals With(float? health = null, float? maxHealth = null, float? stamina = null, float? maxStamina = null, string[]? challenges = null,
        int? recoveries = null, PlayerFractals? fractals = null, int? pips = null)
    {
        var top = maxHealth ?? MaxHealth;
        var bar = maxStamina ?? MaxStamina;
        return new PlayerVitals(Math.Clamp(health ?? Health, 0, top), top, Math.Clamp(stamina ?? Stamina, 0, bar), bar, challenges ?? Challenges.ToArray(),
            recoveries ?? Recoveries, fractals ?? Fractals, pips ?? Pips);
    }

    /// <summary>Whether two records hold the same body (the host saves only what changed).</summary>
    public bool Same(PlayerVitals other) => ReferenceEquals(this, other) || (Health == other.Health && MaxHealth == other.MaxHealth && Stamina == other.Stamina &&
        MaxStamina == other.MaxStamina && Recoveries == other.Recoveries && Pips == other.Pips && Fractals == other.Fractals && Challenges.SequenceEqual(other.Challenges, StringComparer.Ordinal));

    // ---- today's rules (a pack without a traits block), kept as they were ----

    public PlayerVitals SpendClimb(float seconds, float rise, out bool enough) => VitalsRules.Legacy.SpendClimb(this, seconds, rise, out enough);
    public PlayerVitals Rest(float seconds, bool heal) => VitalsRules.Legacy.Rest(this, seconds, heal);
    public PlayerVitals Damage(float amount) => VitalsRules.Legacy.Damage(this, amount);
    /// <summary>Recovery costs position, half health and half stamina; earned traits and creations are kept.</summary>
    public PlayerVitals Recovered() => VitalsRules.Legacy.Recovered(this);
    public PlayerVitals CompleteClimb(string challenge) => VitalsRules.Legacy.CompleteClimb(this, challenge);

    /// <summary>
    /// Injury from downward landing speed in m/s, in health points, the same for every body (decision K: fall damage is
    /// absolute, so more hearts survive bigger falls); deep water catches the body without injury. Bounded by the contract's
    /// most health, so a fall still knocks out the toughest body. The hearts it costs are the rules' (VitalsRules.HarmQuarters).
    /// </summary>
    public static float FallDamage(float landingSpeedMps, bool deepWater) =>
        !float.IsFinite(landingSpeedMps) || landingSpeedMps < 0 || deepWater ? 0 :
            Math.Clamp((landingSpeedMps - SafeLandingSpeedMps) * FallDamagePerMps, 0, ContractMaxHealth);
}

/// <summary>
/// One local player, three independent mode records. Mode changes never refill a saved mode's vitals. Each mode remembers
/// when its record was last written (Written), so the host can catch up the time away.
/// </summary>
public sealed class PlayerModesProfile
{
    public static readonly IReadOnlyList<string> Modes = Array.AsReadOnly(new[] { "creative", "challenge", "live" });
    public string Mode { get; }
    private readonly Dictionary<string, PlayerVitals> _modes;
    private readonly Dictionary<string, DateTime> _written;
    public PlayerVitals Current => For(Mode);
    public static PlayerModesProfile Fresh => new("creative", Modes.ToDictionary(mode => mode, _ => PlayerVitals.Fresh), new Dictionary<string, DateTime>());
    private PlayerModesProfile(string mode, Dictionary<string, PlayerVitals> modes, Dictionary<string, DateTime> written) { Mode = mode; _modes = modes; _written = written; }
    public PlayerVitals For(string mode) => _modes[mode];
    /// <summary>When this mode's record was last written by the host, or null if never (a fresh mode, or one only in a version 1 save).</summary>
    public DateTime? Written(string mode) => _written.TryGetValue(mode, out var at) ? at : null;
    public static bool ValidMode(string mode) => Modes.Contains(mode, StringComparer.Ordinal);
    public static PlayerModesProfile Restore(string mode, IReadOnlyDictionary<string, PlayerVitals> modes, IReadOnlyDictionary<string, DateTime>? written = null)
    {
        if (!ValidMode(mode) || modes.Count != 3 || Modes.Any(name => !modes.TryGetValue(name, out var value) || value == null))
            throw new PlayerVitalsException("The player profile has unsupported modes.");
        return new PlayerModesProfile(mode, modes.ToDictionary(pair => pair.Key, pair => pair.Value),
            written?.Where(pair => ValidMode(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, DateTime>());
    }
    public PlayerModesProfile Select(string mode) => Restore(mode, _modes, _written);
    public PlayerModesProfile WithCurrent(PlayerVitals value)
    {
        var modes = new Dictionary<string, PlayerVitals>(_modes) { [Mode] = value };
        return new PlayerModesProfile(Mode, modes, _written);
    }
    /// <summary>The current mode's record stamped as written now.</summary>
    public PlayerModesProfile Stamped(DateTime utc) => new(Mode, _modes, new Dictionary<string, DateTime>(_written) { [Mode] = utc });
}

public interface IPlayerVitalsStore
{
    PlayerModesProfile Load();
    void Save(PlayerModesProfile profile);
}

/// <summary>
/// The vitals store (contracts/team.schema.json $defs/vitals_store, enfractal.player_modes version 2): one document per mode
/// at &lt;root&gt;/modes/&lt;mode&gt;/vitals.json, strict and bounded, written atomically under an exclusive lease; a save
/// that changed under it (another session) or that cannot be read is kept, never overwritten. Version 1 (the three modes in
/// one file, user://single_player/progression/player_modes_v1.json) is read for a mode with no version 2 document yet, and
/// never written or removed here (the teams packet, M3, moves the files and removes the old ones).
/// The store's place comes from the path it is given (CommandHost.DefaultPlayerVitalsPath, which the teams packet sets): a
/// team's &lt;root&gt;/modes/&lt;mode&gt;/vitals.json names its root (the team folder); any other file is a version 1 save,
/// and its folder is the root. The team id written is the root folder's name in a team layout, "local" otherwise.
/// Only the modes the host stamped (Written) are written, and only when their document changed.
/// </summary>
public sealed class LocalPlayerVitalsStore : IPlayerVitalsStore
{
    public const string DefaultUserPath = "user://single_player/progression/player_modes_v1.json";
    public const string SchemaId = "enfractal.player_modes";
    public const int Version = 2;
    public const int MaxBytes = 64 * 1024;
    public const string LocalTeamId = "local";
    private static readonly Regex UtcPattern = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z\z", RegexOptions.Compiled);
    private readonly string _root;
    private readonly string? _legacyPath;
    private readonly string _teamId;
    private readonly Dictionary<string, byte[]?> _baseline = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _readOnly;
    private readonly object _gate = new();

    public LocalPlayerVitalsStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new PlayerVitalsException("Player traits need an absolute local path.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { throw new PlayerVitalsException("Player traits have an invalid save path."); }
        var folder = Path.GetDirectoryName(full);
        var modes = folder == null ? null : Path.GetDirectoryName(folder);
        if (Path.GetFileName(full) == "vitals.json" && modes != null && Path.GetFileName(modes) == "modes" && PlayerModesProfile.ValidMode(Path.GetFileName(folder!)) &&
            Path.GetDirectoryName(modes) is { } team)
        {
            _root = team;
            _legacyPath = null;
            _teamId = Saves.TeamLayout.ValidTeamId(Path.GetFileName(team)) ? Path.GetFileName(team) : LocalTeamId;
        }
        else
        {
            _root = folder ?? throw new PlayerVitalsException("Player traits have an invalid save path.");
            _legacyPath = full;
            _teamId = LocalTeamId;
        }
    }

    /// <summary>Where a mode's version 2 document lives.</summary>
    public string ModePath(string mode) => Path.Combine(_root, "modes", mode, "vitals.json");

    public PlayerModesProfile Load()
    {
        lock (_gate)
        {
            try
            {
                var modes = new Dictionary<string, PlayerVitals>(StringComparer.Ordinal);
                var written = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                var baseline = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
                PlayerModesProfile? legacy = null;
                var legacyRead = false;
                foreach (var mode in PlayerModesProfile.Modes)
                {
                    var bytes = Read(ModePath(mode));
                    baseline[mode] = bytes;
                    if (bytes != null)
                    {
                        var document = ParseMode(bytes);
                        if (document.Mode != mode) throw new PlayerVitalsException("A mode's vitals are filed under another mode.");
                        modes[mode] = document.Vitals;
                        written[mode] = document.WrittenUtc;
                        continue;
                    }
                    if (!legacyRead)
                    {
                        legacyRead = true;
                        var old = _legacyPath == null ? null : Read(_legacyPath);
                        legacy = old == null ? null : ParseLegacy(old);
                    }
                    modes[mode] = legacy?.For(mode) ?? PlayerVitals.Fresh;
                }
                // The mode last written is the one being played (the teams packet's boot names it instead); a version 1 save kept its own.
                var current = written.Count > 0
                    ? written.OrderByDescending(p => p.Value).ThenBy(p => Array.IndexOf(PlayerModesProfile.Modes.ToArray(), p.Key)).First().Key
                    : legacy?.Mode ?? "creative";
                _baseline.Clear();
                foreach (var (mode, bytes) in baseline) _baseline[mode] = bytes;
                _loaded = true;
                _readOnly = false;
                return PlayerModesProfile.Restore(current, modes, written);
            }
            catch (Exception error) when (error is PlayerVitalsException || FileError(error))
            {
                _readOnly = true;
                throw new PlayerVitalsException("Player traits could not be loaded. The existing save was kept.");
            }
        }
    }

    public void Save(PlayerModesProfile profile)
    {
        lock (_gate)
        {
            if (!_loaded || _readOnly) throw new PlayerVitalsException("Player traits are waiting for their save to be loaded.");
            foreach (var mode in PlayerModesProfile.Modes)
            {
                if (profile.Written(mode) is not { } at) continue;
                var bytes = SerializeMode(_teamId, mode, at, profile.For(mode));
                if (_baseline.GetValueOrDefault(mode) is { } same && same.AsSpan().SequenceEqual(bytes)) continue;
                Write(mode, bytes);
            }
        }
    }

    private void Write(string mode, byte[] bytes)
    {
        var path = ModePath(mode);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Read(path);
            var baseline = _baseline.GetValueOrDefault(mode);
            if (!(current == null ? baseline == null : baseline != null && current.AsSpan().SequenceEqual(baseline)))
            {
                _readOnly = true;
                throw new PlayerVitalsException("Player traits changed in another session. Reload before saving.");
            }
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            _baseline[mode] = bytes;
        }
        catch (Exception error) when (FileError(error)) { throw new PlayerVitalsException("Player traits could not be saved. The previous save was kept."); }
        finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception error) when (FileError(error)) { } }
    }

    private static byte[]? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaxBytes) throw new PlayerVitalsException("Player traits have an invalid file size.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>One mode's document, version 2, as it is written: compact JSON and a final newline.</summary>
    public static byte[] SerializeMode(string teamId, string mode, DateTime writtenUtc, PlayerVitals vitals)
    {
        if (!Saves.TeamLayout.ValidTeamId(teamId) || !PlayerModesProfile.ValidMode(mode)) throw new PlayerVitalsException("Player traits name an invalid team or mode.");
        var fractals = new Dictionary<string, object>
        {
            ["found"] = vitals.Fractals.Found, ["unspent"] = vitals.Fractals.Unspent, ["hearts"] = vitals.Fractals.Hearts, ["bands"] = vitals.Fractals.Bands,
        };
        if (vitals.Fractals.Typed) fractals["typed"] = new Dictionary<string, int> { ["heart"] = vitals.Fractals.TypedHeart!.Value, ["band"] = vitals.Fractals.TypedBand!.Value };
        var document = new
        {
            schema = SchemaId, version = Version, team_id = teamId, mode,
            written_utc = writtenUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            vitals = new
            {
                health = vitals.Health, max_health = vitals.MaxHealth, stamina = vitals.Stamina, max_stamina = vitals.MaxStamina,
                challenges = vitals.Challenges, recoveries = vitals.Recoveries, fractals, pips = Math.Max(0, vitals.Pips),
            },
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document) + "\n");
    }

    public readonly record struct ModeDocument(string TeamId, string Mode, DateTime WrittenUtc, PlayerVitals Vitals);

    /// <summary>One mode's version 2 document, checked strictly: unknown or missing fields, a newer version and invalid values are refused.</summary>
    public static ModeDocument ParseMode(byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new PlayerVitalsException("Player traits have an invalid file size.");
            new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = CanonicalJson.Parse(bytes);
            var root = document.RootElement;
            Fields(root, "schema", "version", "team_id", "mode", "written_utc", "vitals");
            if (root.GetProperty("schema").GetString() != SchemaId || root.GetProperty("version").GetRawText() != "2")
                throw new PlayerVitalsException("Player traits have an unsupported version.");
            var team = root.GetProperty("team_id").GetString() ?? "";
            var mode = root.GetProperty("mode").GetString() ?? "";
            var at = root.GetProperty("written_utc").GetString() ?? "";
            if (!Saves.TeamLayout.ValidTeamId(team) || !PlayerModesProfile.ValidMode(mode) || !UtcPattern.IsMatch(at) ||
                !DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var written))
                throw new PlayerVitalsException("Player traits name an invalid team, mode or time.");
            var value = root.GetProperty("vitals");
            Fields(value, "health", "max_health", "stamina", "max_stamina", "challenges", "recoveries", "fractals", "pips");
            var challenges = value.GetProperty("challenges");
            if (challenges.ValueKind != JsonValueKind.Array || challenges.GetArrayLength() > PlayerVitals.MaximumChallenges)
                throw new PlayerVitalsException("Player traits have invalid challenges.");
            var fractals = value.GetProperty("fractals");
            if (fractals.ValueKind != JsonValueKind.Object || fractals.EnumerateObject().Any(f => f.Name is not ("found" or "unspent" or "hearts" or "bands" or "typed")) ||
                !new[] { "found", "unspent", "hearts", "bands" }.All(name => fractals.TryGetProperty(name, out _)))
                throw new PlayerVitalsException("Player traits have unsupported fields.");
            int? typedHeart = null, typedBand = null;
            if (fractals.TryGetProperty("typed", out var typed))
            {
                Fields(typed, "heart", "band");
                typedHeart = Whole(typed.GetProperty("heart"));
                typedBand = Whole(typed.GetProperty("band"));
            }
            var count = new PlayerFractals(Whole(fractals.GetProperty("found")), Whole(fractals.GetProperty("unspent")), Whole(fractals.GetProperty("hearts")),
                Whole(fractals.GetProperty("bands")), typedHeart, typedBand);
            var pips = Whole(value.GetProperty("pips"));
            if (pips < 0) throw new PlayerVitalsException("Player traits contain invalid values.");
            var vitals = PlayerVitals.Of(Finite(value.GetProperty("health")), Finite(value.GetProperty("max_health")), Finite(value.GetProperty("stamina")),
                Finite(value.GetProperty("max_stamina")), challenges.EnumerateArray().Select(item => item.GetString()!), Whole(value.GetProperty("recoveries")), count, pips);
            return new ModeDocument(team, mode, written, vitals);
        }
        catch (Exception error) when (error is CanonicalJsonException or JsonException or InvalidOperationException or FormatException or OverflowException or DecoderFallbackException or KeyNotFoundException)
        { throw new PlayerVitalsException("Player traits are not a valid save file."); }
    }

    /// <summary>Version 1 (the three modes in one file), read for migration only.</summary>
    public static PlayerModesProfile ParseLegacy(byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new PlayerVitalsException("Player traits have an invalid file size.");
            new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = CanonicalJson.Parse(bytes);
            var root = document.RootElement;
            Fields(root, "schema", "version", "mode", "modes");
            if (root.GetProperty("schema").GetString() != SchemaId || root.GetProperty("version").GetRawText() != "1")
                throw new PlayerVitalsException("Player traits have an unsupported version.");
            var modes = root.GetProperty("modes");
            Fields(modes, "creative", "challenge", "live");
            var values = new Dictionary<string, PlayerVitals>();
            foreach (var mode in PlayerModesProfile.Modes)
            {
                var value = modes.GetProperty(mode);
                Fields(value, "health", "stamina", "challenges", "recoveries");
                var challenges = value.GetProperty("challenges");
                if (challenges.ValueKind != JsonValueKind.Array || challenges.GetArrayLength() > PlayerVitals.MaximumChallenges)
                    throw new PlayerVitalsException("Player traits have invalid challenges.");
                values[mode] = PlayerVitals.Restore(Finite(value.GetProperty("health")), Finite(value.GetProperty("stamina")),
                    challenges.EnumerateArray().Select(item => item.GetString()!), Whole(value.GetProperty("recoveries")));
            }
            return PlayerModesProfile.Restore(root.GetProperty("mode").GetString()!, values);
        }
        catch (Exception error) when (error is CanonicalJsonException or JsonException or InvalidOperationException or FormatException or OverflowException or DecoderFallbackException or KeyNotFoundException)
        { throw new PlayerVitalsException("Player traits are not a valid save file."); }
    }

    private static float Finite(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) throw new PlayerVitalsException("Player traits contain invalid values.");
        var number = value.GetDouble();
        if (!double.IsFinite(number) || Math.Abs(number) > 1e6) throw new PlayerVitalsException("Player traits contain invalid values.");
        return (float)number;
    }

    private static int Whole(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.GetRawText().All(c => char.IsAsciiDigit(c) || c == '-') || !value.TryGetInt32(out var whole))
            throw new PlayerVitalsException("Player traits contain invalid values.");
        return whole;
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            value.EnumerateObject().Any(field => !names.Contains(field.Name, StringComparer.Ordinal)))
            throw new PlayerVitalsException("Player traits have unsupported fields.");
    }
    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
