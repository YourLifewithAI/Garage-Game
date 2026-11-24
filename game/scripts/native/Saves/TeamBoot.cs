using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Saves;

/// <summary>
/// The game's boot of a team (the opening menu's stand-in until M7): <c>--team=</c>, <c>--mode=</c> and <c>--difficulty=</c>
/// (run-room.ps1 -Team, -Mode, -Difficulty) honoured, in two steps around CommandHost.Attach:
/// <list type="number">
/// <item><b>Prepare</b>, before the host exists: the team's play lease (Run 4, MENU-2: held from here until the room has left the
/// tree, so New game never puts a game away under a room that is up; first, alone for a moment, a put-away that a crash cut
/// short is put back, GameSetAside.Recover); the single-player saves before teams moved under the default team (SaveMigration:
/// write, verify, and remove the old files only on a later launch; held off while it would write where New game put a game
/// away); the team's folder and team file (seeded from the machine's
/// settings for a new team); the mode (the one named, else the one this team played last, else Creative; Live is refused); and the
/// host's store paths pointed into the team's folder: the vitals store, the expertise store and the island saves under the mode.
/// A room that starts while the team's game is being put away waits for it, and is refused if it is still at work after
/// LeaseWait; one that finds a game half put away and cannot put it back is refused too: it never opens a game that is half moved.</item>
/// <item><b>Apply</b>, once the host and its rules are loaded: the team file's knacks and ranks to the body (UseTeam), then the boot's
/// one world.set_mode with the island save's difficulty (BootMode), after which the mode is fixed. A <c>--knack=</c> argument
/// stays as a test's override of the team file's knacks (M5's).</item>
/// </list>
/// Only the game's own boot does this (the main scene, or room.tscn run as the main scene). A test that builds a RoomWorld under its
/// own node is a fixture: it keeps whatever store paths it set, plays as before, and only its <c>--knack=</c> arguments apply.
/// </summary>
public sealed class TeamBoot
{
    /// <summary>
    /// Test seams: the user folder a team boot uses, in Godot's spelling under user:// (the island saves must be: the creation authority
    /// keeps them inside the user folder), user:// itself when null; a fixture booted as the game; its command line; and the clock.
    /// </summary>
    public static string? UserRootOverride { get; set; }
    public static IReadOnlyList<string>? ArgumentsOverride { get; set; }
    public static bool ForceGameBoot { get; set; }
    public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    /// <summary>How long a boot waits for a put-away at work on its team before it is refused (a test shortens it).</summary>
    public static TimeSpan LeaseWait { get; set; } = PlayLease.LeavingWait;

    public bool GameBoot { get; private init; }
    public string TeamId { get; private init; } = TeamLayout.DefaultTeamId;
    public string Mode { get; private init; } = "creative";
    /// <summary>The difficulty the boot named (Challenge only); the island save's own, once made, wins (CommandHost.BootMode).</summary>
    public string? Difficulty { get; private init; }
    public MigrationReport? Migration { get; private init; }
    public TeamLayout? Layout { get; private init; }
    public IReadOnlyList<string> Notices { get; private init; } = Array.Empty<string>();
    /// <summary>The room's hold on its team's play lease (null for a fixture): let go by its keeper once the room has left the tree.</summary>
    public PlayLease? Lease { get; private init; }
    private TeamStore? Store { get; init; }
    private IReadOnlyDictionary<string, int> KnackOverride { get; init; } = new Dictionary<string, int>();

    /// <summary>Whether this RoomWorld is the game's own boot: under the main scene (NativeGameBoot), the main scene itself, or a test's forced one.</summary>
    public static bool IsGameBoot(Node world) => ForceGameBoot || world.GetParent() is NativeGameBoot || (world.IsInsideTree() && world.GetTree().CurrentScene == world);

    private static string? Argument(IEnumerable<string> arguments, string name) =>
        arguments.LastOrDefault(a => a != null && a.StartsWith(name, StringComparison.Ordinal))?[name.Length..];

    public static TeamBoot Prepare(Node world, IReadOnlyList<string> arguments)
    {
        arguments = ArgumentsOverride ?? arguments;
        var knacks = BodyKnacks.FromArguments(arguments);
        if (!IsGameBoot(world)) return new TeamBoot { GameBoot = false, KnackOverride = knacks };
        var notices = new List<string>();
        var userRoot = UserRootOverride ?? "user://";
        if (!userRoot.StartsWith("user://", StringComparison.Ordinal)) throw new ArgumentException("A team boot's user folder is under user://.");
        var layout = new TeamLayout(ProjectSettings.GlobalizePath(userRoot));
        var now = Clock();

        var team = Argument(arguments, "--team=");
        if (team != null && !TeamLayout.ValidTeamId(team))
        {
            notices.Add("--team= takes a team id (lowercase letters, digits, - and _); the default team plays.");
            team = null;
        }
        team ??= TeamLayout.DefaultTeamId;

        // The team's play lease, before anything of the team's is read: this room holds it for as long as it is up.
        var lease = TakeLease(world, layout, team, notices);

        // The saves from before teams, into the default team: write and verify now, remove the old files on a later launch.
        var migration = MigrateOldSaves(layout, now);
        if (migration.Kept.Count > 0) notices.AddRange(migration.Kept.Select(k => "kept " + k));

        // The team's file: a new team is seeded from what this machine's settings say about the player.
        var store = new TeamStore(layout.TeamFile(team), team);
        try
        {
            if (store.Load() == null) store.Save(MachineSettings.SeedTeam(layout.MachineSettingsFile, team, now));
        }
        catch (TeamFileException error) { notices.Add(error.Message); }

        var mode = Argument(arguments, "--mode=");
        if (mode == "live") { notices.Add(CommandHost.LiveNotYet + " Choose creative or challenge."); mode = null; }
        else if (mode != null && mode is not ("creative" or "challenge")) { notices.Add("--mode= is creative or challenge."); mode = null; }
        mode ??= LastMode(layout, team);
        var difficulty = Argument(arguments, "--difficulty=");
        if (difficulty != null && (difficulty is not ("gentle" or "standard" or "brave") || mode != "challenge"))
        {
            notices.Add("--difficulty= is gentle, standard or brave, with --mode=challenge.");
            difficulty = null;
        }

        // The host's stores, in the team's folder (Attach reads these), in Godot's spelling.
        CommandHost.DefaultPlayerVitalsPath = TeamLayout.UserVitalsPath(team, mode, userRoot);
        CommandHost.DefaultGlowProgressPath = TeamLayout.UserExpertisePath(team, userRoot);
        CommandHost.SaveRoot = TeamLayout.UserIslandsRoot(team, mode, userRoot);

        var boot = new TeamBoot
        {
            GameBoot = true, TeamId = team, Mode = mode, Difficulty = difficulty, Migration = migration, Layout = layout, Notices = notices, Store = store, KnackOverride = knacks,
            Lease = lease,
        };
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"TEAM_BOOT team={team} mode={mode}{(difficulty != null ? " difficulty=" + difficulty : "")} migration: {migration.Line()}"));
        foreach (var notice in notices) GD.Print("TEAM_BOOT " + notice);
        return boot;
    }

    /// <summary>
    /// The room's hold on the team's play lease (PlayLease), kept by a node added to the room now, before the command host is
    /// attached: children leave the tree last one first, so the host has written what it writes as it leaves before the team is
    /// let go. First the lease is asked for alone: if nobody else has the team up, a put-away that the process ending cut short
    /// is put back (GameSetAside.Recover), so the room never opens half a game. Then the room's own hold, waited for while a
    /// put-away is at work; still refused after LeaseWait, the boot is refused with the player's words, and nothing was read.
    /// <list type="bullet">
    /// <item>A game still half put away once the room has its hold (the put-back failed, or could not be tried) refuses the boot
    /// too: opened so, the room would start a fresh game over the old game's pockets, and the old game would be put away for
    /// good by the next start. Nothing is lost by refusing: what moved lies whole in its folder, and the next start tries again.</item>
    /// <item>A lease file that cannot be opened at all (PlayLeaseAnswer.CannotOpen: a team folder that cannot be written) is
    /// not a put-away at work: the room boots at once, as it did before the lease, with a notice and no hold.</item>
    /// </list>
    /// </summary>
    private static PlayLease? TakeLease(Node world, TeamLayout layout, string team, List<string> notices)
    {
        if (PlayLease.TryAlone(layout, team) is { } alone)
        {
            try
            {
                if (GameSetAside.Recover(alone, layout) is { Length: > 0 } recovered) notices.Add(recovered);
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or TeamFileException or SetAsideException or SetAsideLedgerException)
            {
                // Nothing is lost (what moved lies whole in its set-aside folder); the next start tries again.
                GD.Print("TEAM_BOOT a put-away that did not finish could not be put back: " + error.GetType().Name);
            }
            finally { alone.Dispose(); }
        }
        var answer = PlayLease.Ask(layout, team, alone: false, LeaseWait, out var lease);
        if (answer == PlayLeaseAnswer.Held) throw new InvalidOperationException(PlayLease.BeingPutAway);
        if (GameSetAside.PendingPutAway(layout, team) != null)
        {
            lease?.Dispose();
            throw new InvalidOperationException(GameSetAside.NotPutBack);
        }
        if (lease == null)
        {
            notices.Add("this team's play lease could not be opened: the room is up without it, and a put-away would not see it");
            return null;
        }
        world.AddChild(new PlayLeaseKeeper { Name = "PlayLease", Lease = lease });
        return lease;
    }

    /// <summary>
    /// The saves from before teams, moved under the default team (SaveMigration), unless that would write where New game put a
    /// game away or is putting one away (GameSetAside.LegacyWouldRefill): then it is held off whole, with a notice, and its old
    /// files stay where they are. Nothing brings a put-away game back unasked.
    /// </summary>
    public static MigrationReport MigrateOldSaves(TeamLayout layout, DateTime nowUtc)
    {
        if (!GameSetAside.LegacyWouldRefill(layout)) return SaveMigration.Run(layout, nowUtc);
        return new MigrationReport { Phase = HeldPhase, Kept = new[] { "the saves from before teams, where they are: New game put this team's game away, and no old save is copied back in" } };
    }

    /// <summary>The phase a report carries when its copy was held off for a put-away game (MigrateOldSaves, BringOldSaves).</summary>
    public const string HeldPhase = "held";
    private static bool _askedForTheOldFolder;

    /// <summary>
    /// The boot's first step, before anything reads user:// (NativeGameBoot and the menu's boot call it): the saves made before
    /// the rename, copied once into this build's user folder (UserFolderMove.RunOnce), unless that copy would put files back
    /// where New game put a game away (GameSetAside.OldFolderWouldRefill). Then it is held off whole for this launch, with a
    /// line in the log: the old folder is never changed, so nothing is lost by waiting. Once per process; null after that.
    /// </summary>
    public static UserFolderMoveReport? BringOldSaves()
    {
        if (_askedForTheOldFolder) return null;
        _askedForTheOldFolder = true;
        var to = OS.GetUserDataDir();
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(to));
        if (!string.IsNullOrEmpty(parent) && Held(System.IO.Path.Combine(parent, UserFolderMove.OldFolderName), to) is { } held)
        {
            GD.Print(held.Line());
            foreach (var kept in held.Kept) GD.Print("USER_FOLDER_MOVE held " + kept);
            return held;
        }
        return UserFolderMove.RunOnce();
    }

    /// <summary>The same between two folders (the tests give their own): the move, or a report of phase "held" naming what it would have put back.</summary>
    public static UserFolderMoveReport BringOldSaves(string from, string to) => Held(from, to) ?? UserFolderMove.Run(from, to);

    private static UserFolderMoveReport? Held(string from, string to)
    {
        var back = GameSetAside.OldFolderWouldRefill(from, to);
        if (back.Count == 0) return null;
        const int named = 8;
        var kept = back.Take(named).Select(path => path + ": its game was put away by New game (not copied; it stays in the old folder)").ToList();
        if (back.Count > named) kept.Add(string.Create(CultureInfo.InvariantCulture, $"and {back.Count - named} more files of put-away games"));
        return new UserFolderMoveReport { Phase = HeldPhase, From = from, To = to, Found = back, Kept = kept };
    }

    /// <summary>
    /// The mode this team played last (the vitals store's own choice: the mode written last), Creative when none. Live played as
    /// Challenge before teams, and its saves wait for Live: Challenge (as the migration filed its room saves).
    /// </summary>
    private static string LastMode(TeamLayout layout, string team)
    {
        try
        {
            var last = new LocalPlayerVitalsStore(layout.VitalsFile(team, "creative")).Load().Mode;
            return last == "live" ? "challenge" : last;
        }
        catch (PlayerVitalsException) { return "creative"; }
    }

    /// <summary>After CommandHost.Attach and LoadRules: the team's knacks, the boot's one set-mode (then fixed), and a test's --knack= override.</summary>
    public void Apply(CommandHost host)
    {
        // A fixture's knacks are its --knack= arguments, as before teams (none: today's body); a team's, overridden only when named.
        if (GameBoot && Store != null)
        {
            host.UseTeam(Store);
            if (KnackOverride.Count > 0) host.UseKnacks(KnackOverride);
            var result = host.BootMode(TeamId, Mode, Difficulty);
            var who = host.SaveTeamIdentity;
            GD.Print($"TEAM_BOOT {(result["ok"]?.GetValue<bool>() == true ? "playing" : host.IslandHeld ? "held" : "refused")} {who?.Mode}{(who?.Difficulty is { } d ? " " + d : "")}, knacks {(host.Knacks.Count == 0 ? "none" : string.Join(",", host.Knacks.Select(k => $"{k.Key}:{k.Value}")))}" +
                (host.TeamNotice.Length > 0 ? "; " + host.TeamNotice : ""));
        }
        else host.UseKnacks(KnackOverride);
    }
}
