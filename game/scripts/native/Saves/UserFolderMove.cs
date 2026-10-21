using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Saves;

/// <summary>What one run of the folder move did, for the boot's line and the tests: paths relative to the folders.</summary>
public sealed record UserFolderMoveReport
{
    /// <summary>none (no old folder, or the old and new folders are the same), moved (copied and verified, the ledger written),
    /// done (the ledger was already there: nothing to do), partial (a file could not be copied: no ledger, so the next launch tries again).</summary>
    public string Phase { get; init; } = "none";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public IReadOnlyList<string> Found { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Written { get; init; } = Array.Empty<string>();
    /// <summary>Already in the new folder with the same bytes (a run that was interrupted, or a file the game wrote twice).</summary>
    public IReadOnlyList<string> Already { get; init; } = Array.Empty<string>();
    /// <summary>Not copied, with the reason: a different file of the same name already in the new folder (the new one is kept,
    /// the old one stays in the old folder), or an error. Never a loss: the old folder is never changed.</summary>
    public IReadOnlyList<string> Kept { get; init; } = Array.Empty<string>();
    public double Milliseconds { get; init; }

    public string Line() => string.Create(CultureInfo.InvariantCulture,
        $"USER_FOLDER_MOVE phase={Phase} found={Found.Count} written={Written.Count} already={Already.Count} kept={Kept.Count} ms={Milliseconds:0}");
}

/// <summary>
/// The save folder's move from the room-era name (decided by the founder on 21 October 2026, docs/runs/GARAGE-GAME-RENAME.md, phase 3).
/// Godot puts <c>user://</c> under the project's <c>config/name</c>, which is "Garage-Game" since the rename, so the saves made before
/// it are in the sibling folder "EnFractal" (<c>%APPDATA%\Godot\app_userdata\EnFractal</c> on Windows). On every boot, before anything
/// reads <c>user://</c>, this copies that folder's game data into the new one, once:
/// <list type="bullet">
/// <item>every file is copied to a temporary name, its SHA-256 compared with the original's, and only then given its real name;</item>
/// <item>nothing in the new folder is ever overwritten: a file of the same name with the same bytes counts as already there, and one
/// with different bytes is kept as it is (the old one stays in the old folder, and the report says so);</item>
/// <item>the engine's own caches (logs, shader and Vulkan caches, object snapshots) and the companion's live session file are not
/// game data and are not copied: the engine rewrites them;</item>
/// <item>the old folder is never changed, so the move can always be done again by hand;</item>
/// <item>a ledger in the new folder, written last and only when every file was handled, marks the move done; a run that is
/// interrupted, or that hits an error, writes none, and the next launch picks up where it stopped.</item>
/// </list>
/// A file saved into the old folder after the move (by a build from before the rename) is not copied by a later launch.
/// </summary>
public static class UserFolderMove
{
    public const string OldFolderName = "EnFractal";
    public const string LedgerName = "moved_from_enfractal.json";
    public static readonly IReadOnlyList<string> NotGameData = new[] { "logs", "shader_cache", "vulkan", "objectdb_snapshots", "companion" };
    private const string Partial = ".moving";
    private static bool _ran;

    /// <summary>The boot's call: once per process, between this machine's old and new <c>user://</c> folders. Null after the first call.</summary>
    public static UserFolderMoveReport? RunOnce()
    {
        if (_ran) return null;
        _ran = true;
        var to = OS.GetUserDataDir();
        var parent = Path.GetDirectoryName(Path.GetFullPath(to));
        if (string.IsNullOrEmpty(parent)) return new UserFolderMoveReport();
        var report = Run(Path.Combine(parent, OldFolderName), to);
        if (report.Phase is "moved" or "partial")
        {
            GD.Print(report.Line());
            foreach (var kept in report.Kept) GD.Print("USER_FOLDER_MOVE kept " + kept);
        }
        return report;
    }

    /// <summary>The move between two folders (the tests give their own).</summary>
    public static UserFolderMoveReport Run(string from, string to)
    {
        var clock = Stopwatch.StartNew();
        from = Path.GetFullPath(from);
        to = Path.GetFullPath(to);
        if (string.Equals(from.TrimEnd(Path.DirectorySeparatorChar), to.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(from))
            return new UserFolderMoveReport { From = from, To = to };
        if (File.Exists(Path.Combine(to, LedgerName)))
            return new UserFolderMoveReport { Phase = "done", From = from, To = to, Milliseconds = clock.Elapsed.TotalMilliseconds };

        var found = new List<string>();
        var written = new List<string>();
        var already = new List<string>();
        var kept = new List<string>();
        var copied = new JsonArray();
        var failed = false;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(from, file);
            var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (NotGameData.Contains(top, StringComparer.OrdinalIgnoreCase) || relative == LedgerName || relative.EndsWith(Partial, StringComparison.Ordinal))
                continue;
            found.Add(relative);
            try
            {
                var hash = Sha256(file);
                var target = Path.Combine(to, relative);
                if (File.Exists(target))
                {
                    if (Sha256(target) == hash) already.Add(relative);
                    else kept.Add(relative + ": a different file of that name is already in the new folder (kept; the old one stays where it was)");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + Partial;
                File.Copy(file, temporary, overwrite: true);
                if (Sha256(temporary) != hash)
                {
                    File.Delete(temporary);
                    kept.Add(relative + ": the copy did not match the original (not copied; the next launch tries again)");
                    failed = true;
                    continue;
                }
                File.Move(temporary, target);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
                written.Add(relative);
                copied.Add(new JsonObject { ["path"] = relative.Replace('\\', '/'), ["sha256"] = hash });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                kept.Add(relative + ": " + error.Message + " (not copied; the next launch tries again)");
                failed = true;
            }
        }
        if (!failed)
        {
            Directory.CreateDirectory(to);
            var ledger = new JsonObject
            {
                ["about"] = "The save folder's move from the room-era name (docs/runs/GARAGE-GAME-RENAME.md, phase 3). The old folder was not changed.",
                ["from"] = from.Replace('\\', '/'),
                ["moved_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["written"] = copied,
                ["already"] = new JsonArray(already.Select(a => (JsonNode)JsonValue.Create(a.Replace('\\', '/'))!).ToArray()),
                ["kept"] = new JsonArray(kept.Select(k => (JsonNode)JsonValue.Create(k.Replace('\\', '/'))!).ToArray()),
            };
            File.WriteAllText(Path.Combine(to, LedgerName), ledger.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        return new UserFolderMoveReport
        {
            Phase = failed ? "partial" : "moved", From = from, To = to, Found = found, Written = written, Already = already, Kept = kept,
            Milliseconds = clock.Elapsed.TotalMilliseconds,
        };
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
