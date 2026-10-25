using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Saves;

/// <summary>What a migration run did, for the boot's line and the tests: every path relative to the user folder.</summary>
public sealed record MigrationReport
{
    /// <summary>none (nothing to move), moved (the first launch: written and verified, the old files kept), removed (a later launch: the old files removed), done (finished before), busy (another game holds the lease).</summary>
    public string Phase { get; init; } = "none";
    public string TeamId { get; init; } = TeamLayout.DefaultTeamId;
    /// <summary>The mode the single-player room saves were played in, which their island saves now keep (moved only).</summary>
    public string IslandMode { get; init; } = "";
    public IReadOnlyList<string> Found { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Written { get; init; } = Array.Empty<string>();
    public int Verified { get; init; }
    public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();
    /// <summary>Old files left where they are, each with its reason (never a loss: a file is kept whenever anything is in doubt).</summary>
    public IReadOnlyList<string> Kept { get; init; } = Array.Empty<string>();
    public double Milliseconds { get; init; }

    public string Line() => Inv($"phase={Phase} team={TeamId}{(IslandMode.Length > 0 ? " islands=" + IslandMode : "")} found={Found.Count} written={Written.Count} verified={Verified} removed={Removed.Count} kept={Kept.Count} in {Milliseconds:0} ms");
    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The single-player saves before teams, moved under the default team (docs/game/MECHANICS.md section 12, the one step that can
/// lose a save): <b>write the new file, verify it, and remove the old one only on a later launch</b>, so the old files are kept
/// until the next launch. Nothing is ever overwritten: a team file that is already there keeps it, and the old one stays.
/// <list type="number">
/// <item><b>The first launch</b> (no ledger yet) reads every old file (Glow's per-mode records, the vitals store's version 1 file
/// and M1's version 2 documents under progression/, every room save under saves/rooms/), writes its new copy (a temporary file
/// flushed and moved into place, never over a file), reads the copy back and checks it (byte for byte, and what it means), then
/// writes the ledger (teams/legacy_migration.json): each old file moved, its SHA-256, and its copy. The old files are not touched.
/// Glow's per-mode records become one expertise store (their union, GlowProgress.Union); each mode's vitals become its version 2
/// document under the team (a version 1 mode the player never played stays fresh); each room save becomes the island save of the
/// mode the player was playing (the vitals store's own choice: the mode written last, Creative when none), byte for byte.</item>
/// <item><b>A later launch</b> removes each old file the ledger lists, only while it is byte for byte what was moved (an older build
/// that wrote it since keeps it) and its copy still reads; then the folders it leaves empty and its stores' lease files.</item>
/// </list>
/// System.IO only, on whatever user folder it is given, so it runs the same on a copy of the founder's saves. One run at a time
/// (an exclusive lease beside the ledger); another game instance meanwhile skips it.
/// </summary>
public static class SaveMigration
{
    public const string LedgerSchema = SchemaIds.LegacyMigration;
    public const int LedgerVersion = 1;
    private const int MaxIslandBytes = 64 * 1024 * 1024;
    private const int MaxLedgerBytes = 1024 * 1024;

    private sealed record Entry(string Kind, string From, string Sha256, string To);
    private sealed record Ledger(string TeamId, string MigratedUtc, string? RemovedUtc, IReadOnlyList<Entry> Files, IReadOnlyList<string> Kept);

    /// <summary>Runs whichever phase is due for this user folder (see the type). Never throws for a file's sake: what it could not do is in Kept.</summary>
    public static MigrationReport Run(TeamLayout layout, DateTime nowUtc, string team = TeamLayout.DefaultTeamId)
    {
        var clock = Stopwatch.StartNew();
        if (!TeamLayout.ValidTeamId(team)) throw new ArgumentException("Not a team id.", nameof(team));
        MigrationReport report;
        try
        {
            Directory.CreateDirectory(layout.TeamsRoot);
            using var lease = new FileStream(layout.MigrationLedger + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var ledger = ReadLedger(layout, out var unreadable);
            if (unreadable) report = new MigrationReport { Phase = "done", TeamId = team, Kept = new[] { "the migration's ledger does not read: nothing old is moved or removed" } };
            else if (ledger == null) report = MoveEverything(layout, nowUtc, team);
            else if (ledger.RemovedUtc == null) report = RemoveTheOld(layout, nowUtc, ledger);
            else report = new MigrationReport { Phase = "done", TeamId = ledger.TeamId };
        }
        catch (Exception error) when (FileError(error))
        {
            report = new MigrationReport { Phase = "busy", TeamId = team, Kept = new[] { "the migration could not take its lease (another game running?): nothing was moved or removed" } };
        }
        return report with { Milliseconds = clock.Elapsed.TotalMilliseconds };
    }

    // ---- the first launch: write, verify, record; the old files stay ----

    private static MigrationReport MoveEverything(TeamLayout layout, DateTime nowUtc, string team)
    {
        var found = new List<string>();
        var written = new List<string>();
        var kept = new List<string>();
        var entries = new List<Entry>();
        var verified = 0;

        // Glow's per-mode records (version 2, or 1), Creative's first: one expertise store, their union.
        var glows = TeamLayout.Modes.Select(mode => (Mode: mode, Path: layout.LegacyGlow(mode))).Where(g => File.Exists(g.Path)).ToList();
        var progressionTouched = glows.Count > 0;
        if (glows.Count > 0)
        {
            found.AddRange(glows.Select(g => layout.Relative(g.Path)));
            var read = glows.Select(g => (g.Mode, g.Path, Bytes: TryRead(g.Path, LocalGlowProgressStore.MaxSaveBytes))).ToList();
            var records = new List<GlowProgress>();
            var parsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (mode, path, bytes) in read)
            {
                if (bytes == null) { kept.Add(layout.Relative(path) + ": does not read"); continue; }
                try { records.Add(LocalGlowProgressStore.ParseDocument(bytes).Progress); parsed.Add(path); }
                catch (GlowProgressException) { kept.Add(layout.Relative(path) + ": not a Glow record this game reads"); }
            }
            if (records.Count == read.Count)
            {
                var union = GlowProgress.Union(records);
                var bytes = LocalGlowProgressStore.Serialize(union, team);
                var target = layout.ExpertiseFile(team);
                var outcome = Place(target, bytes, check => LocalGlowProgressStore.ParseDocument(check) is var d && d.TeamId == team && d.Progress.Same(union));
                Record(layout, outcome, target, read.Select(r => ("expertise", r.Path, r.Bytes!)), written, kept, entries, ref verified);
            }
            else kept.AddRange(read.Where(r => parsed.Contains(r.Path)).Select(r => layout.Relative(r.Path) + ": kept with the Glow records that do not read; nothing of Glow moved"));
        }

        // The vitals: M1's version 2 documents (team "local", root progression/) and the version 1 file, as the vitals store reads them.
        var v2 = TeamLayout.Modes.Select(mode => (Mode: mode, Path: layout.LegacyVitalsV2(mode))).Where(v => File.Exists(v.Path)).ToList();
        var v1Path = layout.LegacyVitalsV1;
        var hasV1 = File.Exists(v1Path);
        progressionTouched |= v2.Count > 0 || hasV1;
        found.AddRange(v2.Select(v => layout.Relative(v.Path)));
        if (hasV1) found.Add(layout.Relative(v1Path));
        var documents = new Dictionary<string, (byte[] Bytes, LocalPlayerVitalsStore.ModeDocument Document)>(StringComparer.Ordinal);
        var vitalsReadable = true;
        foreach (var (mode, path) in v2)
        {
            var bytes = TryRead(path, LocalPlayerVitalsStore.MaxBytes);
            try
            {
                if (bytes == null) throw new PlayerVitalsException("unreadable");
                var document = LocalPlayerVitalsStore.ParseMode(bytes);
                if (document.Mode != mode) throw new PlayerVitalsException("filed under another mode");
                documents[mode] = (bytes, document);
            }
            catch (PlayerVitalsException) { vitalsReadable = false; kept.Add(layout.Relative(path) + ": not a vitals document this game reads"); }
        }
        byte[]? v1Bytes = null;
        PlayerModesProfile? v1 = null;
        if (hasV1)
        {
            v1Bytes = TryRead(v1Path, LocalPlayerVitalsStore.MaxBytes);
            try { v1 = v1Bytes == null ? throw new PlayerVitalsException("unreadable") : LocalPlayerVitalsStore.ParseLegacy(v1Bytes); }
            catch (PlayerVitalsException) { vitalsReadable = false; kept.Add(layout.Relative(v1Path) + ": not a vitals file this game reads"); }
        }
        // The mode the player was playing: the vitals store's own choice (the one written last; version 1's own; else Creative).
        // Live played as Challenge before teams, and Live's saves wait for Live: its room saves are Challenge's (TeamBoot.LastMode too).
        var playing = documents.Count > 0
            ? documents.OrderByDescending(p => p.Value.Document.WrittenUtc).ThenBy(p => TeamLayout.Modes.ToList().IndexOf(p.Key)).First().Key
            : v1?.Mode ?? "creative";
        var islandMode = playing == "live" ? "challenge" : playing;
        DateTime? newestV2 = documents.Count > 0 ? documents.Max(p => p.Value.Document.WrittenUtc) : null;
        if (!vitalsReadable)
        {
            kept.AddRange(documents.Select(d => layout.Relative(layout.LegacyVitalsV2(d.Key)) + ": kept with the vitals that do not read; no vitals moved"));
            if (v1 != null) kept.Add(layout.Relative(v1Path) + ": kept with the vitals that do not read; no vitals moved");
        }
        else if (documents.Count > 0 || v1 != null)
        {
            var settled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mode in TeamLayout.Modes)
            {
                PlayerVitals vitals;
                DateTime at;
                var fromV2 = documents.TryGetValue(mode, out var doc);
                if (fromV2) (vitals, at) = (doc.Document.Vitals, doc.Document.WrittenUtc);
                else if (v1 != null && (!v1.For(mode).Same(PlayerVitals.Fresh) || (newestV2 == null && mode == playing)))
                {
                    // Version 1 had no pool: the rules fill an unset pool, and every pack's rules clamp the contract's ceiling to its own
                    // full pool, so a played mode keeps a full pool as it would have. Version 1 kept no time: its modes are written a
                    // second before the newest version 2 document, or, with none, now for the mode it was playing and a second before
                    // for the others, so the mode written last is still the one being played (the next boot plays it).
                    var old = v1.For(mode);
                    vitals = PlayerVitals.Of(old.Health, old.MaxHealth, old.Stamina, old.MaxStamina, old.Challenges, old.Recoveries, old.Fractals, PlayerVitals.ContractMaxPips);
                    var stamp = newestV2 is { } newest ? newest - TimeSpan.FromSeconds(1) : nowUtc.ToUniversalTime() - (mode == playing ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
                    at = new DateTime(stamp.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
                }
                else { settled.Add(mode); continue; }
                var bytes = LocalPlayerVitalsStore.SerializeMode(team, mode, at, vitals);
                var target = layout.VitalsFile(team, mode);
                var outcome = Place(target, bytes, check => LocalPlayerVitalsStore.ParseMode(check) is var d && d.TeamId == team && d.Mode == mode && d.WrittenUtc == at && d.Vitals.Same(vitals));
                var sources = fromV2 ? new[] { ("vitals", layout.LegacyVitalsV2(mode), doc.Bytes) } : Array.Empty<(string, string, byte[])>();
                Record(layout, outcome, target, sources, written, kept, entries, ref verified);
                if (outcome is Outcome.Written or Outcome.AlreadyThere) settled.Add(mode);
            }
            // Version 1 goes once every mode it held is settled: moved, superseded by a version 2 document moved, or never played.
            if (v1 != null)
            {
                if (settled.Count == TeamLayout.Modes.Count) entries.Add(new Entry("vitals_v1", layout.Relative(v1Path), Sha256(v1Bytes!), layout.Relative(Path.Combine(layout.TeamFolder(team), "modes"))));
                else kept.Add(layout.Relative(v1Path) + ": a mode it holds did not move");
            }
        }

        // The room saves: each the island save of the mode played, byte for byte (the host adds the team block when it next saves).
        foreach (var (room, prefix, path) in LegacyIslands(layout))
        {
            found.Add(layout.Relative(path));
            var bytes = TryRead(path, MaxIslandBytes);
            if (bytes == null) { kept.Add(layout.Relative(path) + ": does not read"); continue; }
            var target = layout.IslandSave(team, islandMode, room, prefix);
            var outcome = Place(target, bytes, _ => true);
            Record(layout, outcome, target, new[] { ("island", path, bytes) }, written, kept, entries, ref verified);
        }

        if (found.Count == 0) return new MigrationReport { Phase = "none", TeamId = team };
        var ledger = new Ledger(team, TeamFile.Stamp(nowUtc), null, entries, kept);
        if (!WriteLedger(layout, ledger))
            kept.Add("the ledger could not be written: the old files stay; the next launch finds the copies and records them");
        return new MigrationReport
        {
            Phase = "moved", TeamId = team, IslandMode = islandMode, Found = found, Written = written, Verified = verified, Kept = kept,
        };
    }

    private enum Outcome { Written, AlreadyThere, Occupied, Failed }

    /// <summary>
    /// The new file: written to a temporary file beside it, flushed, moved into place only where nothing is (never over a file),
    /// then read back and checked byte for byte and by <paramref name="means"/>. A file already there with exactly these bytes is
    /// this move done before (a ledger that failed to write); any other file there is the team's own and stays. A copy that does
    /// not verify is removed again (it is this run's own), and its old file stays.
    /// </summary>
    private static Outcome Place(string target, byte[] bytes, Func<byte[], bool> means)
    {
        if (File.Exists(target))
            return TryRead(target, Math.Max(bytes.Length, 1)) is { } there && there.AsSpan().SequenceEqual(bytes) ? Outcome.AlreadyThere : Outcome.Occupied;
        string? temporary = null;
        var placed = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, target, overwrite: false);
            temporary = null;
            placed = true;
            var back = TryRead(target, Math.Max(bytes.Length, 1));
            bool good;
            try { good = back != null && back.AsSpan().SequenceEqual(bytes) && means(back); }
            catch (Exception error) when (error is GlowProgressException or PlayerVitalsException) { good = false; }
            if (good) return Outcome.Written;
            File.Delete(target);
            return Outcome.Failed;
        }
        catch (Exception error) when (FileError(error))
        {
            // Only this run's own copy is ever removed; a file someone else put there meanwhile is left alone.
            if (placed) try { File.Delete(target); } catch (Exception again) when (FileError(again)) { }
            return File.Exists(target) && !placed ? Outcome.Occupied : Outcome.Failed;
        }
        finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception error) when (FileError(error)) { } }
    }

    private static void Record(TeamLayout layout, Outcome outcome, string target, IEnumerable<(string Kind, string Path, byte[] Bytes)> sources,
        List<string> written, List<string> kept, List<Entry> entries, ref int verified)
    {
        var list = sources.ToList();
        switch (outcome)
        {
            case Outcome.Written:
                written.Add(layout.Relative(target));
                verified++;
                entries.AddRange(list.Select(s => new Entry(s.Kind, layout.Relative(s.Path), Sha256(s.Bytes), layout.Relative(target))));
                break;
            case Outcome.AlreadyThere:
                verified++;
                entries.AddRange(list.Select(s => new Entry(s.Kind, layout.Relative(s.Path), Sha256(s.Bytes), layout.Relative(target))));
                break;
            case Outcome.Occupied:
                kept.AddRange(list.Select(s => layout.Relative(s.Path) + ": the team already has " + layout.Relative(target)));
                break;
            default:
                kept.AddRange(list.Select(s => layout.Relative(s.Path) + ": its copy did not write or verify"));
                break;
        }
    }

    /// <summary>Every room save before teams: saves/rooms/&lt;room&gt;/&lt;manifest prefix&gt;/inventions.json, names checked, in name order.</summary>
    private static IEnumerable<(string Room, string Prefix, string Path)> LegacyIslands(TeamLayout layout)
    {
        if (!Directory.Exists(layout.LegacyRooms)) yield break;
        foreach (var room in Directory.GetDirectories(layout.LegacyRooms).OrderBy(d => d, StringComparer.Ordinal))
        {
            var roomId = Path.GetFileName(room);
            if (!TeamLayout.ValidRoomId(roomId)) continue;
            foreach (var manifest in Directory.GetDirectories(room).OrderBy(d => d, StringComparer.Ordinal))
            {
                var prefix = Path.GetFileName(manifest);
                var save = Path.Combine(manifest, "inventions.json");
                if (TeamLayout.ValidManifestPrefix(prefix) && File.Exists(save)) yield return (roomId, prefix, save);
            }
        }
    }

    // ---- a later launch: remove what was moved, only while it is what was moved ----

    private static MigrationReport RemoveTheOld(TeamLayout layout, DateTime nowUtc, Ledger ledger)
    {
        var removed = new List<string>();
        var kept = new List<string>();
        foreach (var entry in ledger.Files)
        {
            var source = layout.Absolute(entry.From);
            var target = layout.Absolute(entry.To);
            if (!File.Exists(source)) continue;
            var bytes = TryRead(source, entry.Kind == "island" ? MaxIslandBytes : LocalPlayerVitalsStore.MaxBytes);
            if (bytes == null || Sha256(bytes) != entry.Sha256) { kept.Add(entry.From + ": changed since it was moved (an older build?); kept"); continue; }
            if (!CopyReads(entry.Kind, target)) { kept.Add(entry.From + ": its copy " + entry.To + " is missing or does not read; kept"); continue; }
            try
            {
                File.Delete(source);
                removed.Add(entry.From);
                DropLease(source + ".lock");
            }
            catch (Exception error) when (FileError(error)) { kept.Add(entry.From + ": could not be removed; kept"); }
        }
        // The folders the moved files leave empty (never one with anything left in it).
        foreach (var entry in ledger.Files)
            for (var folder = Path.GetDirectoryName(layout.Absolute(entry.From)); folder != null && folder.Length > layout.Root.Length && IsUnder(layout, folder); folder = Path.GetDirectoryName(folder))
                if (!TryRemoveEmpty(folder)) break;
        WriteLedger(layout, ledger with { RemovedUtc = TeamFile.Stamp(nowUtc), Kept = ledger.Kept.Concat(kept).ToList() });
        return new MigrationReport { Phase = "removed", TeamId = ledger.TeamId, Found = ledger.Files.Select(f => f.From).ToList(), Removed = removed, Kept = kept };
    }

    /// <summary>Whether the new copy still reads as its kind (it is the live save now, so it may have changed: only that it reads).</summary>
    private static bool CopyReads(string kind, string target)
    {
        try
        {
            switch (kind)
            {
                case "expertise": return TryRead(target, LocalGlowProgressStore.MaxSaveBytes) is { } glow && LocalGlowProgressStore.ParseDocument(glow).Version == LocalGlowProgressStore.Version;
                case "vitals": return TryRead(target, LocalPlayerVitalsStore.MaxBytes) is { } vitals && LocalPlayerVitalsStore.ParseMode(vitals).Mode.Length > 0;
                case "vitals_v1":
                    // Version 1's entry names the team's modes folder: every mode's document there must read as its mode.
                    return Directory.Exists(target) && TeamLayout.Modes.All(mode => Path.Combine(target, mode, "vitals.json") is var file &&
                        (!File.Exists(file) || (TryRead(file, LocalPlayerVitalsStore.MaxBytes) is { } part && LocalPlayerVitalsStore.ParseMode(part).Mode == mode)));
                case "island":
                    if (TryRead(target, MaxIslandBytes) is not { } island) return false;
                    using (var document = CanonicalJson.Parse(island)) return document.RootElement.ValueKind == JsonValueKind.Object;
                default: return false;
            }
        }
        catch (Exception error) when (error is GlowProgressException or PlayerVitalsException or CanonicalJsonException) { return false; }
    }

    /// <summary>A store's lease file beside a removed save, removed only while nobody holds it (opened exclusively, deleted on close).</summary>
    private static void DropLease(string path)
    {
        if (!File.Exists(path)) return;
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (Exception error) when (FileError(error)) { }
    }

    /// <summary>Whether a folder is one the old layout made (under single_player/ or saves/, or one of those two): only those are tidied.</summary>
    private static bool IsUnder(TeamLayout layout, string folder) =>
        new[] { Path.Combine(layout.Root, "single_player"), Path.Combine(layout.Root, "saves") }
            .Any(top => folder == top || folder.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static bool TryRemoveEmpty(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) return false;
            Directory.Delete(folder, recursive: false);
            return true;
        }
        catch (Exception error) when (FileError(error)) { return false; }
    }

    // ---- the ledger ----

    private static Ledger? ReadLedger(TeamLayout layout, out bool unreadable)
    {
        unreadable = false;
        var bytes = TryRead(layout.MigrationLedger, MaxLedgerBytes);
        if (bytes == null) { unreadable = File.Exists(layout.MigrationLedger); return null; }
        try
        {
            using var document = CanonicalJson.Parse(bytes);
            var root = document.RootElement;
            if (!SchemaIds.IsSchema(root.GetProperty("schema").GetString(), "legacy_migration") || root.GetProperty("version").GetRawText() != "1") throw new FormatException();
            var team = root.GetProperty("team_id").GetString()!;
            if (!TeamLayout.ValidTeamId(team)) throw new FormatException();
            var files = root.GetProperty("files").EnumerateArray().Select(f => new Entry(f.GetProperty("kind").GetString()!, f.GetProperty("from").GetString()!,
                f.GetProperty("sha256").GetString()!, f.GetProperty("to").GetString()!)).ToList();
            // Every path stays under the user folder: a ledger is a file, and a file is never trusted to point elsewhere.
            if (files.Any(f => f.Kind is not ("expertise" or "vitals" or "vitals_v1" or "island") || !Inside(layout, f.From) || !Inside(layout, f.To) || f.Sha256.Length != 64))
                throw new FormatException();
            var removed = root.TryGetProperty("removed_utc", out var at) ? at.GetString() : null;
            var kept = root.TryGetProperty("kept", out var k) ? k.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
            return new Ledger(team, root.GetProperty("migrated_utc").GetString()!, removed, files, kept);
        }
        catch (Exception error) when (error is CanonicalJsonException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            unreadable = true;
            return null;
        }
    }

    private static bool Inside(TeamLayout layout, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal)) return false;
        var full = layout.Absolute(relative);
        return full.StartsWith(layout.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool WriteLedger(TeamLayout layout, Ledger ledger)
    {
        var document = new Dictionary<string, object?>
        {
            ["schema"] = LedgerSchema, ["version"] = LedgerVersion, ["team_id"] = ledger.TeamId, ["migrated_utc"] = ledger.MigratedUtc,
            ["files"] = ledger.Files.Select(f => new { kind = f.Kind, from = f.From, sha256 = f.Sha256, to = f.To }).ToArray(),
            ["kept"] = ledger.Kept.ToArray(),
        };
        if (ledger.RemovedUtc != null) document["removed_utc"] = ledger.RemovedUtc;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(layout.TeamsRoot);
            temporary = layout.MigrationLedger + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, layout.MigrationLedger, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception error) when (FileError(error)) { return false; }
        finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception error) when (FileError(error)) { } }
    }

    // ---- files ----

    private static byte[]? TryRead(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > maxBytes) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception error) when (FileError(error)) { return null; }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
