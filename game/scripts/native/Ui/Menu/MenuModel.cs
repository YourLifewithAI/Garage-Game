using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>
/// A game the menu is about to start: whose, where, in which mode and (Challenge) at what difficulty, and the command line the
/// game's boot reads for it (the same arguments run-room.ps1 -Team -Mode -Difficulty passes: --team=, --mode=, --difficulty=).
/// </summary>
public sealed record MenuLaunch(string TeamId, IslandEntry Island, string Mode, string? Difficulty, IReadOnlyList<string> Arguments)
{
    public static IReadOnlyList<string> ArgumentsFor(string team, string mode, string? difficulty) =>
        difficulty != null && mode == "challenge" ? new[] { $"--team={team}", $"--mode={mode}", $"--difficulty={difficulty}" } : new[] { $"--team={team}", $"--mode={mode}" };
}

/// <summary>
/// What the opening menu has chosen and what it may offer next (docs/game/MECHANICS.md section 3), with no scene in it, so the
/// headless menu test drives it as a player does: choose a team, an island, a mode (and a difficulty, and whether to copy the
/// Creative island), then play; or Continue. The scene (MainMenu) draws this and sends its clicks here.
/// <list type="bullet">
/// <item>A mode and difficulty are chosen when an island save is first made and fixed after (decision 3): where a Challenge save exists
/// the difficulty is the save's, shown, not chosen. One save per mode on an island; the menu opens the other mode's save, it never
/// changes a save's mode.</item>
/// <item>Which modes and difficulties are on offer is the island rules' to say (IslandRules.ModeAvailable): Live is shown, refused,
/// as long as its rows say they are not available. Gentle is the offered default on a team's first Challenge save.</item>
/// <item>On an island with a Creative save and no Challenge one, Challenge offers "Start fresh" or "Copy my Creative island into
/// Challenge", the copy once per island per team (decision 44; CarriedBuilds).</item>
/// </list>
/// </summary>
public sealed class MenuModel
{
    public static readonly string[] Difficulties = { "gentle", "standard", "brave" };
    /// <summary>What the menu says for each mode and difficulty (the founder's words: "Live (for now just a solo preview)", Brave rather than Hard).</summary>
    public static string ModeWords(string mode) => mode switch { "creative" => "Creative", "challenge" => "Challenge", _ => "Live (for now just a solo preview)" };
    public static string DifficultyWords(string difficulty) => difficulty switch { "gentle" => "Gentle", "brave" => "Brave", _ => "Standard" };

    public TeamLayout Layout { get; }
    public IslandRules? Rules { get; }
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public IReadOnlyList<TeamCard> Teams { get; private set; } = Array.Empty<TeamCard>();
    public IReadOnlyList<IslandEntry> Islands { get; private set; }

    public string? TeamId { get; private set; }
    public string? RoomId { get; private set; }
    public string? Mode { get; private set; }
    public string? Difficulty { get; private set; }
    /// <summary>The difficulty was picked by the player (not read from a save, not the suggested one): it stays when the island changes.</summary>
    private bool _difficultyPicked;
    /// <summary>"Copy my Creative island into Challenge" chosen (rather than "Start fresh").</summary>
    public bool CopyCreative { get; private set; }
    /// <summary>The menu's last word to the player: a refusal or what just happened. Empty when all is well.</summary>
    public string Notice { get; private set; } = "";

    public MenuModel(TeamLayout layout, IslandRules? rules, IReadOnlyList<IslandEntry> islands)
    {
        Layout = layout;
        Rules = rules;
        Islands = islands;
        Reload();
    }

    /// <summary>The teams read again from the team folder (a new team, a swapped knack, a game just played).</summary>
    public void Reload()
    {
        Teams = TeamCards.Read(Layout, Rules);
        if (TeamId != null && Team == null) TeamId = null;
        Tidy();
    }

    public TeamCard? Team => TeamId == null ? null : Teams.FirstOrDefault(t => t.TeamId == TeamId);
    public IslandEntry? Island => RoomId == null ? null : Islands.FirstOrDefault(i => i.RoomId == RoomId);

    /// <summary>The menu says something to the player (a refusal from the scene's own controls, e.g. a knack that would not save).</summary>
    public void SetNotice(string words) => Notice = words;

    // ---- what is on offer ----

    /// <summary>Whether the island rules offer a mode and difficulty now (IslandRules.ModeAvailable; a pack without a modes block offers today's Creative and Challenge at Standard).</summary>
    public bool Offers(string mode, string? difficulty = null) =>
        Rules != null ? Rules.ModeAvailable(mode, difficulty) : mode == "creative" ? difficulty == null : mode == "challenge" && (difficulty is null or "standard");

    /// <summary>The difficulties the pack offers for Challenge, in the menu's order.</summary>
    public IReadOnlyList<string> OfferedDifficulties => Difficulties.Where(d => Offers("challenge", d)).ToList();

    /// <summary>The team's saves on the chosen island (Creative, Challenge), or none.</summary>
    public IReadOnlyList<IslandSaveInfo> Saves => Team is { Readable: true } team && Island is { } island ? IslandSaveInfo.Of(Layout, team.TeamId, island) : Array.Empty<IslandSaveInfo>();
    public IslandSaveInfo? SaveOf(string mode) => Saves.FirstOrDefault(s => s.Mode == mode);

    /// <summary>The difficulty the chosen island's Challenge save keeps (null: no save yet, so it is chosen now).</summary>
    public string? FixedDifficulty => SaveOf("challenge") is { Exists: true } save ? save.Difficulty ?? "standard" : null;

    /// <summary>Whether this would be the team's first Challenge save anywhere: Gentle is then the offered one, highlighted.</summary>
    public bool FirstChallenge => Team is { Readable: true } team && !HasChallengeSave(team.TeamId);

    private bool HasChallengeSave(string team)
    {
        var root = Layout.IslandsRoot(team, "challenge");
        return System.IO.Directory.Exists(root) && System.IO.Directory.EnumerateFiles(root, "inventions.json", System.IO.SearchOption.AllDirectories).Any();
    }

    /// <summary>"Copy my Creative island into Challenge" is on offer: a Creative save here, no Challenge one, the island's one copy not used.</summary>
    public bool CopyOffered => Mode == "challenge" && Team is { Readable: true } team && Island is { } island && CarriedBuilds.Offered(Layout, team.TeamId, island);
    /// <summary>The island's one copy was made already (so the menu says so rather than offering it).</summary>
    public bool CopyUsed => Team is { Readable: true } team && Island is { } island && CarriedBuilds.Used(Layout, team.TeamId, island.RoomId);

    // ---- choosing ----

    public bool SelectTeam(string teamId)
    {
        var card = Teams.FirstOrDefault(t => t.TeamId == teamId);
        if (card == null) { Notice = "That team is not here."; return false; }
        if (!card.Readable) { Notice = card.Problem.Length > 0 ? card.Problem : "That team's file could not be read."; return false; }
        TeamId = teamId;
        Notice = "";
        Tidy();
        return true;
    }

    public bool SelectIsland(string roomId)
    {
        if (Islands.All(i => i.RoomId != roomId)) { Notice = "That island is not here."; return false; }
        RoomId = roomId;
        Notice = "";
        Tidy();
        return true;
    }

    /// <summary>
    /// A mode, and in Challenge a difficulty (the offered default when none is named: the save's own where one exists, Gentle on the
    /// team's first Challenge save, else Standard). Refused, with the menu's words in Notice, when the island rules do not offer it.
    /// </summary>
    public bool ChooseMode(string mode, string? difficulty = null)
    {
        if (mode is not ("creative" or "challenge" or "live")) { Notice = "Choose Creative or Challenge."; return false; }
        if (!Offers(mode))
        {
            // Live is shown, and refused for as long as the island rules say it is not available (the pack's Live rows).
            Notice = mode == "live" ? CommandHost.LiveNotYet + " Choose Creative or Challenge." : ModeWords(mode) + " is not on offer here.";
            return false;
        }
        if (mode == "challenge")
        {
            if (FixedDifficulty is { } fixedBySave) { difficulty = fixedBySave; _difficultyPicked = false; }
            else if (difficulty != null && !Offers("challenge", difficulty)) { Notice = DifficultyWords(difficulty) + " is not on offer here."; return false; }
            else _difficultyPicked = difficulty != null;
            difficulty ??= DefaultDifficulty();
            if (difficulty == null) { Notice = "Challenge is not on offer here."; return false; }
        }
        else { difficulty = null; _difficultyPicked = false; }
        Mode = mode;
        Difficulty = difficulty;
        if (mode != "challenge" || !CopyOffered) CopyCreative = false;
        Notice = "";
        return true;
    }

    /// <summary>The difficulty offered first: Gentle on a team's first Challenge save (highlighted), else Standard, else the first the pack offers.</summary>
    public string? DefaultDifficulty()
    {
        var offered = OfferedDifficulties;
        if (offered.Count == 0) return null;
        if (FirstChallenge && offered.Contains("gentle")) return "gentle";
        return offered.Contains("standard") ? "standard" : offered[0];
    }

    /// <summary>"Copy my Creative island into Challenge" (true) or "Start fresh" (false). Refused when the copy is not on offer.</summary>
    public bool ChooseCopy(bool copy)
    {
        if (copy && !CopyOffered) { Notice = CopyUsed ? "This island has been copied into Challenge once already." : "There is no Creative island here to copy."; return false; }
        CopyCreative = copy;
        Notice = "";
        return true;
    }

    /// <summary>A choice that no longer holds after the team or the island changed is let go (a mode fixed by the save, a copy no longer on offer).</summary>
    private void Tidy()
    {
        if (Mode == "challenge")
        {
            if (FixedDifficulty is { } kept) { Difficulty = kept; _difficultyPicked = false; }
            else if (!_difficultyPicked || Difficulty == null || !Offers("challenge", Difficulty)) { Difficulty = DefaultDifficulty(); _difficultyPicked = false; }
            if (!CopyOffered) CopyCreative = false;
        }
        else CopyCreative = false;
    }

    // ---- playing ----

    /// <summary>Why Play is not ready yet, in the player's words; null when it is.</summary>
    public string? WhyNotReady =>
        Team is not { Readable: true } ? "Choose your character first." :
        Island == null ? "Choose where to." :
        Mode == null ? "Choose a mode." :
        Mode == "challenge" && Difficulty == null ? "Choose how hard." : null;

    /// <summary>
    /// Start the chosen game: the copy first when it was chosen (it refuses to overwrite and to be used twice), the last game
    /// launch with its command line (the game remembers itself for Continue once its room is up: RoomWorld). Null, with Notice, when something is not ready or the copy fails.
    /// </summary>
    public MenuLaunch? Play()
    {
        if (WhyNotReady is { } why) { Notice = why; return null; }
        var team = Team!;
        var island = Island!;
        var mode = Mode!;
        var difficulty = mode == "challenge" ? Difficulty : null;
        if (mode == "challenge" && CopyCreative)
        {
            try
            {
                CarriedBuilds.Copy(Layout, team.TeamId, island, difficulty!, Clock());
                CopyCreative = false;
            }
            catch (MenuSaveException error) { Notice = error.Message; return null; }
        }
        Notice = "";
        return new MenuLaunch(team.TeamId, island, mode, difficulty, MenuLaunch.ArgumentsFor(team.TeamId, mode, difficulty));
    }

    // ---- Continue ----

    /// <summary>The last game, when the team, the island and the mode are all still here to open; null otherwise.</summary>
    public LastGame? LastGameToContinue()
    {
        if (LastGame.Read(Layout) is not { } last) return null;
        if (Teams.FirstOrDefault(t => t.TeamId == last.TeamId) is not { Readable: true }) return null;
        if (Islands.All(i => i.RoomId != last.RoomId) || last.Mode == "live" || !Offers(last.Mode, last.Mode == "challenge" ? last.Difficulty ?? "standard" : null)) return null;
        return last;
    }

    /// <summary>"Mira on the Garage, Challenge" in one click: the words on the Continue button; null when there is no game to continue.</summary>
    public string? ContinueWords()
    {
        if (LastGameToContinue() is not { } last) return null;
        var team = Teams.First(t => t.TeamId == last.TeamId);
        var island = Islands.First(i => i.RoomId == last.RoomId);
        var kept = last.Mode == "challenge" ? IslandSaveInfo.One(Layout, last.TeamId, island, "challenge").Difficulty ?? last.Difficulty : null;
        return $"{team.DisplayName} on {island.DisplayName}, {ModeWords(last.Mode)}" + (kept != null ? $" ({DifficultyWords(kept)})" : "");
    }

    /// <summary>Continue: the last team, island and mode, booted. Null, with Notice, when there is none.</summary>
    public MenuLaunch? Continue()
    {
        if (LastGameToContinue() is not { } last) { Notice = "There is no game to continue yet."; return null; }
        var island = Islands.First(i => i.RoomId == last.RoomId);
        var difficulty = last.Mode == "challenge" ? IslandSaveInfo.One(Layout, last.TeamId, island, "challenge").Difficulty ?? last.Difficulty : null;
        TeamId = last.TeamId;
        RoomId = last.RoomId;
        Mode = last.Mode;
        Difficulty = difficulty;
        CopyCreative = false;
        Notice = "";
        return new MenuLaunch(last.TeamId, island, last.Mode, difficulty, MenuLaunch.ArgumentsFor(last.TeamId, last.Mode, difficulty));
    }
}
