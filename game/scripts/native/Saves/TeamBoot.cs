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
/// <item><b>Prepare</b>, before the host exists: the single-player saves before teams moved under the default team (SaveMigration:
/// write, verify, and remove the old files only on a later launch); the team's folder and team file (seeded from the machine's
/// settings for a new team); the mode (the one named, else the one this team played last, else Creative; Live is refused); and the
/// host's store paths pointed into the team's folder: the vitals store, the expertise store and the island saves under the mode.</item>
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

    public bool GameBoot { get; private init; }
    public string TeamId { get; private init; } = TeamLayout.DefaultTeamId;
    public string Mode { get; private init; } = "creative";
    /// <summary>The difficulty the boot named (Challenge only); the island save's own, once made, wins (CommandHost.BootMode).</summary>
    public string? Difficulty { get; private init; }
    public MigrationReport? Migration { get; private init; }
    public TeamLayout? Layout { get; private init; }
    public IReadOnlyList<string> Notices { get; private init; } = Array.Empty<string>();
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

        // The saves from before teams, into the default team: write and verify now, remove the old files on a later launch.
        var migration = SaveMigration.Run(layout, now);
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
        };
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"TEAM_BOOT team={team} mode={mode}{(difficulty != null ? " difficulty=" + difficulty : "")} migration: {migration.Line()}"));
        foreach (var notice in notices) GD.Print("TEAM_BOOT " + notice);
        return boot;
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
