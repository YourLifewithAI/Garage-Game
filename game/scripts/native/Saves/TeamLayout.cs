using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Saves;

/// <summary>
/// Where a team's saves live (contracts/README.md "Teams and the save layout"; docs/game/MECHANICS.md section 2), under one user
/// folder (Godot's user://, or a test's scratch folder): every path here is absolute, made with System.IO alone, so the migration
/// runs the same on the game's folder and on a copy of it.
/// <code>
/// teams/&lt;team_id&gt;/team.json                         the team file (garage_game.team v1)
/// teams/&lt;team_id&gt;/expertise.json                    the expertise store (garage_game.glow_progress v3)
/// teams/&lt;team_id&gt;/modes/&lt;mode&gt;/vitals.json          the vitals store (garage_game.player_modes v2)
/// teams/&lt;team_id&gt;/modes/&lt;mode&gt;/islands/&lt;room_id&gt;/&lt;manifest prefix&gt;/inventions.json   the island save (creation-world)
/// single_player/room/avatar_profile_v1.cfg              the machine's settings (MachineSettings), not a team's
/// </code>
/// Two teams never share a file: every path a team writes is under its own folder, named by its id. The legacy files the teams
/// packet migrates (SaveMigration) are named here too, read only by the migration.
/// </summary>
public sealed class TeamLayout
{
    /// <summary>The team a boot plays when none is named, and the one the single-player saves before teams migrate into.</summary>
    public const string DefaultTeamId = "local";
    public static readonly IReadOnlyList<string> Modes = Array.AsReadOnly(new[] { "creative", "challenge", "live" });
    private static readonly Regex TeamIdPattern = new(@"\A[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    /// <summary>
    /// The names Windows keeps for its devices, in any case: a folder so named cannot be made (teams\nul), so no team is named so
    /// (the review's finding 3; contracts common.schema.json team_id refuses them too). COM0 and LPT0 are on Microsoft's list as well.
    /// </summary>
    private static readonly Regex DeviceName = new(@"\A(con|prn|aux|nul|com[0-9]|lpt[0-9])\z", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RoomIdPattern = new(@"\A[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    private static readonly Regex PrefixPattern = new(@"\A[0-9a-f]{16}\z", RegexOptions.Compiled);

    /// <summary>The user folder, absolute (Godot's user:// globalized, or a test's folder).</summary>
    public string Root { get; }

    public TeamLayout(string root)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root)) throw new ArgumentException("A team layout needs an absolute user folder.", nameof(root));
        // Godot's user:// globalizes with a final separator: the same folder, spelt one way, so every path made from it compares.
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>A team id (contracts common.schema.json team_id): a lowercase token naming its folder, never a Windows device's name.</summary>
    public static bool ValidTeamId(string? id) => id != null && TeamIdPattern.IsMatch(id) && !DeviceName.IsMatch(id);
    public static bool ValidMode(string? mode) => mode is "creative" or "challenge" or "live";
    public static bool ValidRoomId(string? id) => id != null && RoomIdPattern.IsMatch(id);
    public static bool ValidManifestPrefix(string? prefix) => prefix != null && PrefixPattern.IsMatch(prefix);

    public string TeamsRoot => Path.Combine(Root, "teams");
    public string TeamFolder(string team) => Path.Combine(TeamsRoot, CheckedTeam(team));
    public string TeamFile(string team) => Path.Combine(TeamFolder(team), "team.json");
    public string ExpertiseFile(string team) => Path.Combine(TeamFolder(team), Kernel.LocalGlowProgressStore.FileName);
    public string ModeFolder(string team, string mode) => Path.Combine(TeamFolder(team), "modes", CheckedMode(mode));
    public string VitalsFile(string team, string mode) => Path.Combine(ModeFolder(team, mode), "vitals.json");
    public string IslandsRoot(string team, string mode) => Path.Combine(ModeFolder(team, mode), "islands");
    public string IslandSave(string team, string mode, string room, string prefix) =>
        Path.Combine(IslandsRoot(team, mode), CheckedRoom(room), CheckedPrefix(prefix), "inventions.json");

    /// <summary>The record of the single-player saves' move into the default team (SaveMigration): the machine's, beside the teams.</summary>
    public string MigrationLedger => Path.Combine(TeamsRoot, "legacy_migration.json");

    // ---- the single-player layout before teams, read by the migration only ----

    public string LegacyProgression => Path.Combine(Root, "single_player", "progression");
    /// <summary>Glow's version 2 record of a mode: glow_trial_v1.json (Creative's), glow_trial_v1.challenge.json, glow_trial_v1.live.json.</summary>
    public string LegacyGlow(string mode) => Path.Combine(LegacyProgression, CheckedMode(mode) == "creative" ? "glow_trial_v1.json" : $"glow_trial_v1.{mode}.json");
    /// <summary>The vitals store's version 1 (all three modes in one file).</summary>
    public string LegacyVitalsV1 => Path.Combine(LegacyProgression, "player_modes_v1.json");
    /// <summary>The vitals store's version 2 as M1 wrote it before teams, its root the progression folder (team "local").</summary>
    public string LegacyVitalsV2(string mode) => Path.Combine(LegacyProgression, "modes", CheckedMode(mode), "vitals.json");
    /// <summary>The room saves before teams: saves/rooms/&lt;room_id&gt;/&lt;manifest prefix&gt;/inventions.json, one per room and manifest, every mode in one.</summary>
    public string LegacyRooms => Path.Combine(Root, "saves", "rooms");
    /// <summary>The machine's settings file ("Let my AI see", "Let the Gubble use fire", "Play the intro", the numbers option; the appearance the HUD keeps).</summary>
    public string MachineSettingsFile => Path.Combine(Root, "single_player", "room", "avatar_profile_v1.cfg");

    /// <summary>A path's place under the user folder, with forward slashes (the ledger's and the reports' spelling).</summary>
    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    public string Absolute(string relative) => Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string CheckedTeam(string team) => ValidTeamId(team) ? team : throw new ArgumentException("Not a team id.", nameof(team));
    private static string CheckedMode(string mode) => ValidMode(mode) ? mode : throw new ArgumentException("Not a mode.", nameof(mode));
    private static string CheckedRoom(string room) => ValidRoomId(room) ? room : throw new ArgumentException("Not a room id.", nameof(room));
    private static string CheckedPrefix(string prefix) => ValidManifestPrefix(prefix) ? prefix : throw new ArgumentException("Not a manifest prefix.", nameof(prefix));

    // ---- the same places in Godot's spelling, for the host's statics (CommandHost.DefaultPlayerVitalsPath and the rest) ----

    /// <summary>
    /// A team's place under user:// (or a folder under it, <paramref name="userRoot"/>), as the host's statics hold it: the host
    /// globalizes them, and the creation authority keeps the island saves inside the user folder.
    /// </summary>
    public static string UserTeamFolder(string team, string userRoot = "user://") => (userRoot == "user://" ? "user://" : userRoot.TrimEnd('/') + "/") + "teams/" + CheckedTeam(team);
    public static string UserVitalsPath(string team, string mode, string userRoot = "user://") => $"{UserTeamFolder(team, userRoot)}/modes/{CheckedMode(mode)}/vitals.json";
    public static string UserExpertisePath(string team, string userRoot = "user://") => $"{UserTeamFolder(team, userRoot)}/{Kernel.LocalGlowProgressStore.FileName}";
    public static string UserIslandsRoot(string team, string mode, string userRoot = "user://") => $"{UserTeamFolder(team, userRoot)}/modes/{CheckedMode(mode)}/islands";
}
