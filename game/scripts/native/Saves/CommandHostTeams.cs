using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Kernel;

/// <summary>Whose island save this is and how it plays (room state's extensions.x_team): the team, the mode, and in Challenge the difficulty.</summary>
public sealed record SaveTeam(string TeamId, string Mode, string? Difficulty);

/// <summary>
/// The command host's side of teams (the teams packet, M3; docs/game/MECHANICS.md sections 2, 3 and 7; contracts/README.md "The
/// mechanics round"), kept beside the team store it serves:
/// <list type="bullet">
/// <item><b>The boot-time mode.</b> The game's boot (Saves/TeamBoot.cs, the menu's stand-in) sends world.set_mode once, before the room
/// is up, with the difficulty the island save keeps (UseDifficulty, the trusted boot's call), and then fixes the mode: from then on
/// the host refuses world.set_mode with unsupported_capability at $.op ("The mode is chosen at the menu; this save keeps its mode."),
/// and Live, first, at $.args.mode with allowed creative and challenge (decided: switching modes means switching saves). A host no
/// team boot brought up (a test fixture) changes modes as before, so the per-mode vitals stay testable. A boot whose set-mode is
/// refused while the host plays another mode holds the island save instead of playing it under that mode's rules (IslandHeld).</item>
/// <item><b>The save's team block.</b> The island save's team block (the journal and the map's) also says whose save it is and how it
/// plays: team_id, mode and, in Challenge, difficulty (room state's x_team). A save's difficulty, once made, wins over the boot's.</item>
/// <item><b>knack.set</b>, the player's: a knack the island's pack offers into an open slot of the team file, in play only at the spawn
/// (the camp, today). The team file's knacks reach the body through UseKnacks.</item>
/// </list>
/// </summary>
public partial class CommandHost
{
    /// <summary>
    /// How near the spawn the player must stand to change a knack in play (the camp is the spawn today, where a recovery lands, within
    /// a body's height of it). Provisional: the design names no distance; half a metre is five body heights, a small camp's clearing.
    /// </summary>
    public const float CampRadiusM = 0.5f;
    public const string KnackAtCamp = "A knack is changed at the camp: go back to where you started and try again.";

    /// <summary>True once the game's boot fixed this room's mode (BootMode): world.set_mode is refused from then on.</summary>
    public bool ModeFixed { get; private set; }
    /// <summary>
    /// True when the boot could not open the island in its mode (its one set-mode refused while the host plays another, the review's
    /// finding 1): the island save is held as it was, and nothing done in this session reaches it (HoldIsland).
    /// </summary>
    public bool IslandHeld { get; private set; }
    /// <summary>Whose save this is and how it plays: the boot's, else what the loaded save's team block said (null: neither).</summary>
    public SaveTeam? SaveTeamIdentity => _bootTeam ?? _savedTeam;
    /// <summary>What the loaded island save's team block said about its team, mode and difficulty (null for a save from before teams).</summary>
    public SaveTeam? SavedTeam => _savedTeam;
    /// <summary>The team file this host serves (null without a team: a fixture).</summary>
    public TeamFile? Team { get; private set; }
    /// <summary>What the player is told about the team file or the boot's choices (empty when all is well).</summary>
    public string TeamNotice { get; private set; } = "";
    private TeamStore? _teamStore;
    private SaveTeam? _savedTeam;
    private SaveTeam? _bootTeam;
    private bool _teamBooted;

    /// <summary>
    /// Trusted boot wiring: the team's file, loaded now; its knacks and ranks go to the body and the vitals (UseKnacks). A team file
    /// that does not read is kept as it is: no knack, and knack.set waits for it.
    /// </summary>
    public void UseTeam(TeamStore store)
    {
        _teamStore = store;
        try
        {
            Team = store.Load();
            if (Team == null) TeamNotice = "This team has no team file yet.";
        }
        catch (TeamFileException)
        {
            Team = null;
            TeamNotice = "The team file could not be read. It was kept as it was; knacks wait for it.";
        }
        UseKnacks(Team?.KnackRanks ?? new Dictionary<string, int>());
    }

    /// <summary>
    /// The boot's one world.set_mode, before the room is up (the menu's, later): the island save's mode, and in Challenge its
    /// difficulty (the save's own once it has one; else the boot's; else standard), then the mode is fixed for the session. The
    /// team block is written with the room's next save. Returns the command's result. Live never reaches here (TeamBoot refuses it).
    /// A refused set-mode (the vitals or the expertise store did not read, or could not be saved) leaves the host in the mode it
    /// had: when that is not the island save's, the island is held (HoldIsland), never played under another mode's rules.
    /// </summary>
    public JsonObject BootMode(string teamId, string mode, string? difficulty)
    {
        if (ModeFixed) throw new InvalidOperationException("This room's mode was fixed by its boot.");
        if (!TeamLayout.ValidTeamId(teamId) || mode is not ("creative" or "challenge")) throw new ArgumentException("The boot names a team and Creative or Challenge.");
        _teamBooted = true;
        string? chosen = null;
        if (mode == "challenge")
        {
            chosen = _savedTeam is { Mode: "challenge", Difficulty: { } kept } ? kept : difficulty is "gentle" or "standard" or "brave" ? difficulty : "standard";
            if (difficulty != null && difficulty != chosen)
                TeamNotice = $"This Challenge save plays at {Capitalised(chosen)}: a save keeps the difficulty it was made with.";
            UseDifficulty(chosen);
        }
        if (_savedTeam != null && (_savedTeam.TeamId != teamId || _savedTeam.Mode != mode))
            TeamNotice = "This island save was filed under another team or mode; it is kept, and plays as this one.";
        var result = PlayerCommand("world.set_mode", new JsonObject { ["mode"] = mode });
        _bootTeam = new SaveTeam(teamId, mode, chosen);
        ModeFixed = true;
        if (result["ok"]?.GetValue<bool>() != true)
        {
            if (Mode != mode)
            {
                HoldIsland(mode);
                return result;
            }
            TeamNotice = result["error"]?["message"]?.GetValue<string>() ?? "The mode could not be set.";
        }
        if (_bootTeam != _savedTeam) _teamDirty = true;
        return result;
    }

    /// <summary>What the player reads when the island could not open in its mode (HoldIsland), in the mode's own name.</summary>
    public static string IslandHeldWords(string mode) =>
        $"This {Capitalised(mode)} island could not open: the traits or the expertise could not be read or saved. " +
        "It is held as it was, and nothing done on it now is saved; quit and launch again.";

    private static readonly Godot.StringName PersistenceSink = "persistence_sink";

    /// <summary>
    /// The island save held as it was: the mode stays fixed (no later set-mode plays it under yet another mode), and every write of
    /// the save is refused at the creation authority's one persisting step (its persistence sink), which then publishes nothing:
    /// no build, cast, gather, pickup, undo, note, nature step or team block reaches the file, during play or at exit. The save's
    /// team block is not rewritten. The player reads why (TeamNotice; every refused change answers with the same words).
    /// </summary>
    private void HoldIsland(string mode)
    {
        IslandHeld = true;
        TeamNotice = IslandHeldWords(mode);
        if (Authority != null) Authority.Set(PersistenceSink, new Godot.Callable(this, MethodName.RefuseHeldSave));
        Godot.GD.Print("COMMAND_HOST " + TeamNotice);
        Runtime?.Call(KernelNames.Notice, TeamNotice);
    }

    /// <summary>The held island's persistence sink: nothing is written, and the change is refused with the player's words.</summary>
    private Godot.Collections.Dictionary RefuseHeldSave(Godot.Collections.Dictionary envelope) => new() { ["ok"] = false, ["message"] = TeamNotice };

    private static string Capitalised(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// world.set_mode on a room a team boot brought up: Live first (not available yet, at $.args.mode), then any mode once the boot
    /// fixed it (at $.op). Before the companion's refusal never: player-only ops are refused first.
    /// </summary>
    private void CheckModeChange(JsonElement args)
    {
        if (!_teamBooted) return;
        if (Str(args, "mode") == "live")
            throw new Refusal("unsupported_capability", LiveNotYet, "$.args.mode", allowed: new JsonArray("creative", "challenge"));
        if (ModeFixed) throw new Refusal("unsupported_capability", ModeAtMenu, "$.op");
    }

    // ---- knack.set ----

    public JsonObject PlayerSetKnack(int slot, string knack, bool preview = false) =>
        PlayerCommand("knack.set", new JsonObject { ["slot"] = slot, ["knack"] = knack }, preview);

    /// <summary>
    /// Whether the player stands at a camp: within CampRadiusM of the room's player spawn or of any lit campfire, the starting one
    /// included (decision 71: a lit campfire is a full camp; CommandHostCamps.AtCamp). Allocates nothing once the camps are known.
    /// </summary>
    public bool PlayerAtCamp() => Player != null && IsInstanceValid(Player) && Room != null && AtCamp(Player.GlobalPosition);

    /// <summary>
    /// The player's knack.set (player-only, transient: the team file keeps it): the slot must be open, the knack one the island's pack
    /// offers and in no other slot; in play only at the camp. A knack newly chosen starts at rank 1 (ranks are earned, never set); the
    /// same knack in the same slot changes nothing. Written to the team file first; only a saved choice reaches the body.
    /// </summary>
    private JsonObject SetKnack(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        // A held island (HoldIsland) changes nothing, the team file included: nothing done in this session is saved.
        if (IslandHeld) throw new Refusal("not_ready", TeamNotice);
        if (_teamStore == null || Team is not { } team)
            throw new Refusal("not_ready", TeamNotice.Length > 0 ? TeamNotice : "No team is playing: knacks belong to a team.");
        var slot = args.GetProperty("slot").GetInt32();
        var knack = Str(args, "knack")!;
        if (slot > team.SlotsOpen)
            throw new Refusal("invalid_args", team.SlotsOpen == 1 ? "Only the first knack slot is open; the next opens at a summit." : $"Slots 1 to {team.SlotsOpen} are open; the next opens at a summit.",
                "$.args.slot", actual: slot, allowed: new JsonArray(Enumerable.Range(1, team.SlotsOpen).Select(n => (JsonNode?)n).ToArray()));
        var offered = Rules?.Knacks?.Table.Where(k => k.Available).Select(k => k.Knack).OrderBy(k => k, StringComparer.Ordinal).ToList() ?? new List<string>();
        if (!offered.Contains(knack, StringComparer.Ordinal))
            throw new Refusal("invalid_args", offered.Count == 0 ? "This island has no knacks to choose." : "This island has no knack by that name to choose.",
                "$.args.knack", allowed: new JsonArray(offered.Select(k => (JsonNode?)k).ToArray()));
        if (team.Knacks.FirstOrDefault(k => k.Knack == knack) is { } held && held.Slot != slot)
            throw new Refusal("invalid_args", $"That knack is already in slot {held.Slot}.", "$.args.knack");
        if (!PlayerAtCamp()) throw new Refusal("not_ready", KnackAtCamp, retryable: true);
        if (preview) return Previewed("knack.set", principal, actionId);
        TeamFile next;
        try
        {
            next = team.WithKnack(slot, knack, Rules?.Knacks);
            if (!ReferenceEquals(next, team)) _teamStore.Save(next);
        }
        catch (TeamFileException)
        {
            throw new Refusal("not_ready", "The team file could not be saved; the knack was not changed.", retryable: true);
        }
        Team = next;
        UseKnacks(next.KnackRanks);
        return Transient("knack.set", principal, actionId, fingerprint, new JsonArray(), null);
    }

    // ---- the save's team block: team_id, mode, difficulty (room state's x_team) ----

    /// <summary>The team block's keys that say whose save it is (beside the journal, the map, the open tasks' goals and the avatars).</summary>
    private static bool IsSaveTeamKey(string key) => key is "team_id" or "mode" or "difficulty";

    /// <summary>What is wrong with a block's team, mode and difficulty (or null): all or none; a team id; a mode; a difficulty in Challenge only, and there always.</summary>
    private static string? SaveTeamProblem(JsonObject block)
    {
        if (!block.ContainsKey("team_id") && !block.ContainsKey("mode") && !block.ContainsKey("difficulty")) return null;
        if (StrOf(block["team_id"]) is not { } team || !TeamLayout.ValidTeamId(team)) return "the save's team";
        if (StrOf(block["mode"]) is not ("creative" or "challenge" or "live")) return "the save's mode";
        var difficulty = block["difficulty"];
        if (StrOf(block["mode"]) == "challenge" ? StrOf(difficulty) is not ("gentle" or "standard" or "brave") : difficulty != null) return "the save's difficulty";
        return null;
    }

    private static SaveTeam? ReadSaveTeam(JsonObject? block) =>
        block == null || SaveTeamProblem(block) != null || StrOf(block["team_id"]) is not { } team ? null
            : new SaveTeam(team, StrOf(block["mode"])!, StrOf(block["difficulty"]));

    /// <summary>The team block with whose save it is, when the host knows (the boot's, or the save's own).</summary>
    private JsonObject WithSaveTeam(JsonObject block)
    {
        if (SaveTeamIdentity is not { } who) return block;
        block["team_id"] = who.TeamId;
        block["mode"] = who.Mode;
        if (who.Difficulty != null) block["difficulty"] = who.Difficulty;
        return block;
    }

    /// <summary>Room state's x_team for this save, or null.</summary>
    private JsonObject? XTeam() => SaveTeamIdentity is not { } who ? null : WithSaveTeam(new JsonObject());

    /// <summary>What is wrong with a room state's x_team (contracts room-state.schema.json $defs/x_team), or null.</summary>
    private static string? XTeamProblem(JsonNode node) =>
        node is not JsonObject x || x.Any(p => !IsSaveTeamKey(p.Key)) || !x.ContainsKey("team_id") || SaveTeamProblem(x) != null ? "whose save it is" : null;

    /// <summary>A room state's x_team carried into the save's team block (an import keeps whose save it is).</summary>
    private static void CopyXTeam(JsonObject extensions, JsonObject block)
    {
        if (extensions["x_team"] is not JsonObject x || XTeamProblem(x) != null) return;
        foreach (var (key, value) in x) block[key] = value?.DeepClone();
    }
}
