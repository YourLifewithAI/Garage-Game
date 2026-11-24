using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Tests;

/// <summary>
/// New game's put-away against what would undo it (Run 4, MENU-2, the review's round): nothing brings a put-away game back
/// unasked, and a fault part-way leaves the old game whole on the paths a player takes.
/// <list type="bullet">
/// <item>The save folder's move from the room-era name and the migration of the saves from before teams both copy old saves in
/// wherever a file is missing, on every launch until their ledgers stand. Each is held off while it would write where New game
/// put a game away, or is putting one away.</item>
/// <item>The process ends during New game, the game restarts at the menu, and New game is pressed again with no room boot
/// between: the old game is put back first, then put away whole.</item>
/// <item>A save that changes between the scan and the move refuses the put-away: the bytes put away are the bytes scanned.</item>
/// </list>
/// </summary>
public partial class NewGameTest
{
    private static readonly DateTime Asked = new(2026, 11, 24, 9, 30, 15, DateTimeKind.Utc);

    // ---- the bytes put away are the bytes that were scanned ----

    private void TestAGameChangedWhileItMoved()
    {
        var layout = new TeamLayout(Folder("changed"));
        Craft(layout, "shifty", "challenge");
        var save = layout.IslandSave("shifty", "challenge", "test_room", "0123456789abcdef");
        Dictionary<string, string>? asChanged = null;
        string words;
        try
        {
            // Something writes the island save after it was scanned and before its folder moves.
            GameSetAside.AtStep = name =>
            {
                if (name != "copied") return;
                File.AppendAllText(save, "{\"written\":\"while the game was being put away\"}\n");
                asChanged = Hashes(layout.TeamsRoot);
                asChanged.Remove("shifty/" + GameSetAside.NoteName);
            };
            words = Refused(() => GameSetAside.PutAway(layout, "shifty", "challenge", Asked));
        }
        finally { GameSetAside.AtStep = null; }
        var after = Hashes(layout.TeamsRoot);
        var left = Directory.Exists(GameSetAside.Root(layout)) ? Directory.GetFileSystemEntries(GameSetAside.Root(layout)).Length : 0;
        Check(words == GameSetAside.Failed && asChanged != null && Same(asChanged, after) && GameSetAside.List(layout).Count == 0 && left == 0 && GameSetAside.HasGame(layout, "shifty", "challenge") &&
              TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("shifty"))).PocketsOf("challenge").Units("apple") == 5,
            $"a save written between the scan and the move refuses the put-away (\"{words}\"): the game is home as it then stood, its pockets with it, and no ledger pins bytes that are not there ({left} left under set_aside): {(asChanged == null ? "the step was never reached" : Differences(asChanged, after))}");
        var put = GameSetAside.PutAway(layout, "shifty", "challenge", Asked);
        Check(put != null && GameSetAside.Problems(put.Folder).Count == 0 && File.ReadAllText(Path.Combine(put.Folder, "modes", "challenge", "islands", "test_room", "0123456789abcdef", "inventions.json")).Contains("while the game was being put away", StringComparison.Ordinal),
            "asked again with nothing writing, the game is put away as it now stands, the late write with it");
    }

    // ---- the process ends during New game, and New game is pressed again with no room boot between ----

    private void TestNewGamePressedAgain()
    {
        var layout = new TeamLayout(Folder("again"));
        var whole = 0;
        var seen = new List<string>();
        try
        {
            foreach (var step in GameSetAside.Steps.Take(5))
            {
                var team = "again_" + step;
                Craft(layout, team, "challenge");
                var game = Hashes(layout.ModeFolder(team, "challenge"), locks: true);
                var teamBefore = File.ReadAllBytes(layout.TeamFile(team));
                GameSetAside.AtStep = name => { if (name == step) throw new GameSetAside.PowerCut(); };
                var cut = false;
                try { GameSetAside.PutAway(layout, team, "challenge", Asked); }
                catch (GameSetAside.PowerCut) { cut = true; }
                GameSetAside.AtStep = null;
                // The game restarts at the menu. The mode still has its game (so the menu asks), and New game is pressed again.
                var asks = GameSetAside.HasGame(layout, team, "challenge") && GameSetAside.PendingPutAway(layout, team) == "challenge";
                var put = GameSetAside.PutAway(layout, team, "challenge", Asked.AddMinutes(1));
                var listed = GameSetAside.List(layout, team);
                var folders = Directory.GetDirectories(GameSetAside.Root(layout), "*-" + team + "-challenge").Length;
                var away = put == null ? new Dictionary<string, string>() : Hashes(Path.Combine(put.Folder, "modes", "challenge"), locks: true);
                var good = cut && asks && put != null && listed.Count == 1 && listed[0].Folder == put.Folder && folders == 1 && GameSetAside.Problems(put.Folder).Count == 0 &&
                           put.Ledger.Files.Count == game.Count + 1 && game.Count == 4 && Same(game, away) &&
                           File.ReadAllBytes(Path.Combine(put.Folder, "team.json")).AsSpan().SequenceEqual(teamBefore) &&
                           TeamFile.Parse(File.ReadAllBytes(layout.TeamFile(team))) is { } now && now.PocketsOf("challenge").IsEmpty && now.HandOf("challenge").IsNone && now.PocketsOf("live").Units("log") == 1 &&
                           !Directory.Exists(layout.ModeFolder(team, "challenge")) && !File.Exists(GameSetAside.NotePath(layout, team)) &&
                           GameSetAside.PendingPutAway(layout, team) == null && !GameSetAside.HasGame(layout, team, "challenge");
                if (good) whole++;
                else seen.Add($"{step}: cut {cut}, asks {asks}, put away {put != null} with {put?.Ledger.Files.Count ?? 0} files of {game.Count + 1}, {listed.Count} listed, {folders} folders under set_aside: {Differences(game, away)}");
            }
        }
        finally { GameSetAside.AtStep = null; }
        Check(whole == 5,
            $"the process ending after each step of New game, then New game pressed again with no room boot between: the old game is put back first and put away whole (its island save, its vitals, the strays, the team file as it was, its pockets), one folder, one ledger, nothing left half moved ({whole}/5): {string.Join(" | ", seen)}");
    }

    // ---- the save folder's move from the room-era name never refills a put-away game's place ----

    private void TestTheOldFolderStaysOut()
    {
        var both = Folder("old_folder");
        var old = Path.Combine(both, UserFolderMove.OldFolderName);
        var layout = new TeamLayout(Path.Combine(both, "Garage-Game"));
        Directory.CreateDirectory(layout.Root);
        var ledger = Path.Combine(layout.Root, UserFolderMove.LedgerName);
        // The room-era folder: two teams' Challenge games and an installed room.
        var oldLayout = new TeamLayout(old);
        Craft(oldLayout, "mover", "challenge");
        Craft(oldLayout, "cutter", "challenge");
        var oldRoom = Path.Combine(old, "rooms", "old_room", "room.json");
        Write(oldRoom, "{\"stand_in\":\"an installed room\"}\n");
        var oldBefore = Hashes(old, locks: true);
        try
        {
            // The first launch: one file cannot be read (something holds it), so the move copies the rest and writes no ledger.
            UserFolderMoveReport first;
            using (new FileStream(oldRoom, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None)) first = TeamBoot.BringOldSaves(old, layout.Root);
            Check(first.Phase == "partial" && !File.Exists(ledger) && GameSetAside.HasGame(layout, "mover", "challenge") && GameSetAside.HasGame(layout, "cutter", "challenge") &&
                  GameSetAside.OldFolderWouldRefill(old, layout.Root).Count == 0,
                "the save folder's move that cannot read one file copies the rest and writes no ledger: the next launch copies whatever is missing (" + first.Line() + ")");

            // A put-away cut short: its note stands for its place, so the old folder's older copy never lands where the game is
            // about to be put back. The game was played since the move: its vitals are not the old folder's.
            File.WriteAllBytes(layout.VitalsFile("cutter", "challenge"), LocalPlayerVitalsStore.SerializeMode("cutter", "challenge", Asked, PlayerVitals.Fresh));
            var played = Hashes(layout.TeamFolder("cutter"));
            GameSetAside.AtStep = name => { if (name == "moved") throw new GameSetAside.PowerCut(); };
            try { GameSetAside.PutAway(layout, "cutter", "challenge", Asked); }
            catch (GameSetAside.PowerCut) { }
            GameSetAside.AtStep = null;
            UserFolderMoveReport whileCut;
            using (new FileStream(oldRoom, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None)) whileCut = TeamBoot.BringOldSaves(old, layout.Root);
            string line;
            using (var alone = PlayLease.TryAlone(layout, "cutter")!) line = GameSetAside.Recover(alone, layout);
            Check(whileCut.Phase == TeamBoot.HeldPhase && whileCut.Written.Count == 0 && whileCut.Found.Count == 4 && whileCut.Found.All(f => f.StartsWith("teams/cutter/modes/challenge/", StringComparison.Ordinal)) &&
                  line.EndsWith("was put back", StringComparison.Ordinal) && Same(played, Hashes(layout.TeamFolder("cutter"))) && GameSetAside.List(layout, "cutter").Count == 0,
                $"while a put-away cut short waits to be put back, the next launch's move is held off ({whileCut.Phase}, {whileCut.Found.Count} files it would have put there): the game as it was last played goes home, never the old folder's older copy (\"{line}\"): {Differences(played, Hashes(layout.TeamFolder("cutter")))}");

            // New game. The next launches: the old folder still holds the put-away game's files, and here they are missing.
            var put = GameSetAside.PutAway(layout, "mover", "challenge", Asked);
            UserFolderMoveReport next;
            using (new FileStream(oldRoom, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None)) next = TeamBoot.BringOldSaves(old, layout.Root);
            // And once the file can be read again: without the hold this launch finishes the move, the put-away game with it.
            var later = TeamBoot.BringOldSaves(old, layout.Root);
            var home = layout.ModeFolder("mover", "challenge");
            Check(put != null && next.Phase == TeamBoot.HeldPhase && later.Phase == TeamBoot.HeldPhase && later.Written.Count == 0 && later.Found.Count == 4 &&
                  later.Kept.Count == 4 && later.Kept.All(k => k.StartsWith("teams/mover/modes/challenge/", StringComparison.Ordinal)) &&
                  !GameSetAside.HasGame(layout, "mover", "challenge") && !Directory.Exists(home) && !File.Exists(ledger) && !File.Exists(Path.Combine(layout.Root, "rooms", "old_room", "room.json")) &&
                  GameSetAside.Problems(put.Folder).Count == 0 && GameSetAside.List(layout, "mover").Count == 1,
                $"after New game the next launches' move is held off: nothing of the put-away game is copied back from the old folder, the mode has no game, the put-away folder is whole ({later.Phase}, written {later.Written.Count}; has a game again: {GameSetAside.HasGame(layout, "mover", "challenge")}; in its place: {(Directory.Exists(home) ? string.Join(", ", Hashes(home).Keys) : "nothing")})");

            // A new game is played in the same places: once nothing of the old game's place is missing here, the move goes on,
            // never over a file that is there, finishes, and is done for good.
            foreach (var file in later.Found) Write(Path.Combine(layout.Root, file), "{\"stand_in\":\"the new game's own\"}\n");
            var newGame = Hashes(home);
            var resumed = TeamBoot.BringOldSaves(old, layout.Root);
            var done = TeamBoot.BringOldSaves(old, layout.Root);
            Check(resumed.Phase == "moved" && resumed.Written.Select(w => w.Replace('\\', '/')).SequenceEqual(new[] { "rooms/old_room/room.json" }) && File.Exists(ledger) && Same(newGame, Hashes(home)) &&
                  done.Phase == "done" && GameSetAside.Problems(put!.Folder).Count == 0,
                $"once the new game's own files stand in that place the move goes on and finishes: the room it could not read is copied, the new game's files are not written over ({resumed.Line()}; then {done.Phase})");
            Check(Same(oldBefore, Hashes(old, locks: true)), "and the old folder is as it was throughout, every file and its bytes: " + Differences(oldBefore, Hashes(old, locks: true)));
        }
        finally { GameSetAside.AtStep = null; }
    }

    // ---- the migration of the saves from before teams never refills a put-away game's place ----

    private async Task TestTheSavesFromBeforeTeamsStayOut()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var root = Folder("before_teams");
        var layout = new TeamLayout(root);
        const string team = TeamLayout.DefaultTeamId;
        try
        {
            // The default team's Challenge game, played: half the hearts gone, the fire out.
            var host = await Boot(root, "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            var doused = host.PutOutCampfire(Start);
            var hurt = host.ApplyPlayerDamage(50, PlayerDamageSource.Critter);
            var savePath = Path.GetFullPath(ProjectSettings.GlobalizePath(host.SavePath));
            await Unboot();
            // The same game as the saves from before teams hold it: a version 2 vitals document and a room save, where the
            // migration reads them. Its copies stand under the team; its ledger was never written (the write failed, or the
            // teams folder was wiped by hand), so every launch moves again whatever the team lacks.
            var prefix = Path.GetFileName(Path.GetDirectoryName(savePath)!);
            var oldSave = Path.Combine(layout.LegacyRooms, "test_room", prefix, "inventions.json");
            Directory.CreateDirectory(Path.GetDirectoryName(oldSave)!);
            Directory.CreateDirectory(Path.GetDirectoryName(layout.LegacyVitalsV2("challenge"))!);
            var played = doused && hurt is { After: 50 } && File.Exists(savePath) && File.Exists(layout.VitalsFile(team, "challenge")) && TeamLayout.ValidManifestPrefix(prefix);
            if (played)
            {
                File.Copy(savePath, oldSave);
                File.Copy(layout.VitalsFile(team, "challenge"), layout.LegacyVitalsV2("challenge"));
            }
            var oldFiles = Hashes(Path.Combine(root, "saves")).Concat(Hashes(Path.Combine(root, "single_player", "progression"))).ToDictionary(p => p.Key, p => p.Value);
            Check(played && oldFiles.Count == 2 && !File.Exists(layout.MigrationLedger) && !GameSetAside.LegacyWouldRefill(layout),
                "a played game of the default team, with the saves from before teams still on the disk and their ledger never written; while the game stands nothing is held off");

            // New game.
            var put = GameSetAside.PutAway(layout, team, "challenge", DateTime.UtcNow);
            Check(put != null && !GameSetAside.HasGame(layout, team, "challenge") && GameSetAside.LegacyWouldRefill(layout), "New game puts it away; the old saves would now refill its place");

            // The next start: a first game. Without the hold the migration moves the old vitals and the old room save back in.
            host = await Boot(root, "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            Check(host.Vitals.Health == 100 && host.IsCampfireLit(Start) && host.PocketContents.IsEmpty && Creations(host) == 0 && host.Revision == 0,
                "after New game the next start is a first game, though the saves from before teams lie on the disk with no ledger (not health 50 and the fire out): " + Body(host));
            await Unboot();
            var oldAfter = Hashes(Path.Combine(root, "saves")).Concat(Hashes(Path.Combine(root, "single_player", "progression"))).ToDictionary(p => p.Key, p => p.Value);
            Check(Same(oldFiles, oldAfter) && !File.Exists(layout.MigrationLedger) && put != null && GameSetAside.Problems(put.Folder).Count == 0 && GameSetAside.List(layout, team).Count == 1,
                "the old saves stay where they are, untouched, no ledger is written for a move that was held, and the put-away folder is whole: " + Differences(oldFiles, oldAfter));
        }
        finally
        {
            await Unboot();
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }

        // The hold is exact. Another team's put-away holds nothing: the migration writes only under its own team.
        var other = new TeamLayout(Folder("before_teams_other"));
        Craft(other, "pilot", "challenge");
        var elsewhere = GameSetAside.PutAway(other, "pilot", "challenge", Asked);
        Directory.CreateDirectory(Path.GetDirectoryName(other.LegacyVitalsV2("challenge"))!);
        File.WriteAllBytes(other.LegacyVitalsV2("challenge"), LocalPlayerVitalsStore.SerializeMode(team, "challenge", Earlier, PlayerVitals.Fresh));
        var free = !GameSetAside.LegacyWouldRefill(other);
        var moved = TeamBoot.MigrateOldSaves(other, Asked);
        Check(elsewhere != null && free && moved.Phase == "moved" && File.Exists(other.VitalsFile(team, "challenge")) && File.Exists(other.MigrationLedger),
            $"another team's put-away holds nothing off: the saves from before teams move under the default team as before ({moved.Line()})");
        // A put-away of the default team in a mode the old saves would not write into holds nothing either; one they would write into does.
        var exact = new TeamLayout(Folder("before_teams_exact"));
        Craft(exact, team, "creative");
        var creative = GameSetAside.PutAway(exact, team, "creative", Asked);
        Directory.CreateDirectory(Path.GetDirectoryName(exact.LegacyVitalsV2("challenge"))!);
        Directory.CreateDirectory(Path.GetDirectoryName(exact.LegacyVitalsV2("creative"))!);
        File.WriteAllBytes(exact.LegacyVitalsV2("challenge"), LocalPlayerVitalsStore.SerializeMode(team, "challenge", Earlier, PlayerVitals.Fresh));
        File.Delete(exact.VitalsFile(team, "challenge"));
        var notHeld = !GameSetAside.LegacyWouldRefill(exact);
        File.WriteAllBytes(exact.LegacyVitalsV2("creative"), LocalPlayerVitalsStore.SerializeMode(team, "creative", Earlier, PlayerVitals.Fresh));
        var held = TeamBoot.MigrateOldSaves(exact, Asked);
        Check(creative != null && notHeld && held.Phase == TeamBoot.HeldPhase && held.Kept.Count == 1 && !File.Exists(exact.VitalsFile(team, "creative")) && !File.Exists(exact.VitalsFile(team, "challenge")) &&
              !File.Exists(exact.MigrationLedger) && GameSetAside.PutAwayPlaces(exact).SequenceEqual(new[] { (team, "creative") }),
            $"old saves that would not write into the put-away game's place hold nothing off; one that would holds the whole move, and nothing is written ({held.Phase}: {string.Join("; ", held.Kept)})");
    }
}
