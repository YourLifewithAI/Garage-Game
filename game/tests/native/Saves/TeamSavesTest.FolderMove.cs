using Godot;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GarageGame.Native.Saves;

namespace GarageGame.Tests;

/// <summary>
/// The save folder's move from the room-era name (Saves/UserFolderMove.cs; docs/runs/GARAGE-GAME-RENAME.md, phase 3), on scratch
/// folders: game data copied byte for byte and the engine's caches left behind, the old folder unchanged, nothing in the new
/// folder overwritten, an interrupted move finished by the next run, and a done move never repeated.
/// </summary>
public partial class TeamSavesTest
{
    private void TestTheFolderMove()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "garage-game-folder-move-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var old = Path.Combine(scratch, "EnFractal");
            var fresh = Path.Combine(scratch, "Garage-Game");

            // No old folder: nothing to do.
            Check(UserFolderMove.Run(old, fresh).Phase == "none", "the folder move: with no old folder there is nothing to move");

            Write(old, "teams/local/team.json", "{\"team\":\"local\"}");
            Write(old, "teams/local/challenge/islands/garage.json", "{\"island\":1}");
            Write(old, "rooms/landscape_garage_nominal/room.json", "{\"room\":true}");
            Write(old, "playtests/20261017-1/manifest.json", "{\"play\":1}");
            Write(old, "avatar_profile_v1.cfg", "[avatar]\ncolor=\"teal\"\n");
            Write(old, "logs/godot.log", "engine log");
            Write(old, "shader_cache/a/b.cache", "cache");
            Write(old, "vulkan/vulkan.cache", "pipeline cache");
            Write(old, "companion/session.json", "{\"port\":1}");
            Write(old, "teams/local/team.json.lock", "");
            var before = Snapshot(old);

            // The engine has already made the new folder by the time the game runs (its logs and caches).
            Write(fresh, "logs/godot.log", "the new build's log");
            // A different file of the same name already there is kept; an identical one counts as already there.
            Write(fresh, "avatar_profile_v1.cfg", "[avatar]\ncolor=\"magenta\"\n");
            Write(fresh, "teams/local/team.json", "{\"team\":\"local\"}");

            var moved = UserFolderMove.Run(old, fresh);
            Check(moved.Phase == "moved" && moved.Found.Count == 5 && moved.Written.Count == 3 && moved.Already.Count == 1 && moved.Kept.Count == 1,
                $"the folder move: five files of game data found, three copied, one already there, one kept ({moved.Line()})");
            Check(Same(old, fresh, "teams/local/challenge/islands/garage.json") && Same(old, fresh, "rooms/landscape_garage_nominal/room.json")
                && Same(old, fresh, "playtests/20261017-1/manifest.json"),
                "the folder move: the island save, the installed room and the play are copied byte for byte");
            Check(File.ReadAllText(Path.Combine(fresh, "avatar_profile_v1.cfg")).Contains("magenta", StringComparison.Ordinal)
                && moved.Kept.Single().StartsWith("avatar_profile_v1.cfg", StringComparison.Ordinal),
                "the folder move: a different file already in the new folder is never overwritten, and the report names it");
            Check(!File.Exists(Path.Combine(fresh, "shader_cache", "a", "b.cache")) && !File.Exists(Path.Combine(fresh, "vulkan", "vulkan.cache"))
                && !File.Exists(Path.Combine(fresh, "companion", "session.json")) && !File.Exists(Path.Combine(fresh, "teams", "local", "team.json.lock"))
                && File.ReadAllText(Path.Combine(fresh, "logs", "godot.log")) == "the new build's log",
                "the folder move: the engine's caches, its logs, the companion's live session and the stores' lease files are not copied");
            Check(Snapshot(old).SequenceEqual(before), "the folder move: the old folder is unchanged, every file and its bytes");
            Check(File.Exists(Path.Combine(fresh, UserFolderMove.LedgerName)) && !Directory.EnumerateFiles(fresh, "*.moving", SearchOption.AllDirectories).Any(),
                "the folder move: the ledger is written and no temporary copy is left behind");

            // Done once, never again: a file the old build saves later is not copied by the next launch.
            Write(old, "teams/local/later.json", "{\"later\":true}");
            var again = UserFolderMove.Run(old, fresh);
            Check(again.Phase == "done" && !File.Exists(Path.Combine(fresh, "teams", "local", "later.json")),
                "the folder move: once the ledger is there, a later launch moves nothing");

            // An interrupted move (no ledger, part of the files copied, a temporary copy left) is finished by the next run.
            var cut = Path.Combine(scratch, "Garage-Game-interrupted");
            Write(cut, "teams/local/team.json", "{\"team\":\"local\"}");
            Write(cut, "rooms/landscape_garage_nominal/room.json.moving", "half a cop");
            var resumed = UserFolderMove.Run(old, cut);
            Check(resumed.Phase == "moved" && Same(old, cut, "rooms/landscape_garage_nominal/room.json") && Same(old, cut, "teams/local/later.json")
                && !File.Exists(Path.Combine(cut, "rooms", "landscape_garage_nominal", "room.json.moving")) && File.Exists(Path.Combine(cut, UserFolderMove.LedgerName)),
                "the folder move: an interrupted move is finished by the next launch, the half-written copy replaced: " + resumed.Line());

            // The same folder on both sides (a build that still uses the old name) does nothing.
            Check(UserFolderMove.Run(old, old).Phase == "none", "the folder move: the old and new folders being one folder moves nothing");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static bool Same(string a, string b, string relative)
    {
        var left = Path.Combine(a, relative.Replace('/', Path.DirectorySeparatorChar));
        var right = Path.Combine(b, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(left) && File.Exists(right) && File.ReadAllBytes(left).SequenceEqual(File.ReadAllBytes(right));
    }

    private static string[] Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
}
