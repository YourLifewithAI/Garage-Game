using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

/// <summary>Whether anyone has the chosen team's saves open (PlayLease), as the start buttons see it.</summary>
public enum TeamHold
{
    /// <summary>Nobody has the team up.</summary>
    Free,
    /// <summary>A room of the team is up or still leaving, and the buttons wait for it (at most MenuModel.LeaseWait).</summary>
    Leaving,
    /// <summary>The wait ran out and the team is still up: its game is open in another window.</summary>
    StillOpen,
}

/// <summary>
/// What the opening menu has chosen and what it may offer next (docs/game/MECHANICS.md section 3), with no scene in it, so the
/// headless menu test drives it as a player does: choose a team, an island, a mode (and a difficulty, and whether to copy the
/// Creative island), then start by the start rule's buttons. The scene (MainMenu) draws this and sends its clicks here.
/// <list type="bullet">
/// <item><b>One rule for every start</b> (Run 4, MENU-3; section 3, "Starting and continuing"), for the team, the island and the mode
/// chosen (Offer). A save here: Continue, or New game. No save here but a game of this mode on another island: "Bring my game
/// here", or New game. No game in this mode: New game, nothing asked. An island rebuilt since its save: New game alone. Continue is
/// the only thing that opens a save that is there, and it is offered only where its save is. New game over a game asks once (Asking);
/// pressed again it puts that mode's game away whole (GameSetAside.PutAway: kept, never deleted) and starts a first game. While a
/// room of the team is still leaving, every start waits (Hold).</item>
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

    // What a player reads on the start rule's buttons and lines (every word is the family's to reword): plain and short.
    public const string NewGameWords = "New game";
    public const string BringWords = "Bring my game here";
    public const string GoBackWords = "Go back";
    public const string RebuiltWords = "This room was rebuilt. Your old game is kept.";
    public const string StillClosingWords = "The last game is still closing. One moment.";
    public const string OpenElsewhereWords = "That game is still open in another window.";
    public const string NoSaveHereWords = "There is no game to continue here.";
    public const string NoGameToBringWords = "There is no game to bring here.";
    public const string PutAwayWords = "Your old game is put away.";
    public const string IslandGoneWords = "That island is not here any more. Nothing was changed.";

    /// <summary>New game was pressed once over a game, and the question is up: pressed again it puts the game away; GoBack takes the question down.</summary>
    public bool Asking { get; private set; }
    /// <summary>The question's words while it is up ("New game? Your Challenge game from 25 Oct is put away, not deleted.").</summary>
    public string Question { get; private set; } = "";
    /// <summary>The game New game put away last in this menu (null: none yet, or there was no game to put away).</summary>
    public PutAwayGame? LastPutAway { get; private set; }
    /// <summary>
    /// How long the start buttons wait for a room of the team that is still leaving: after "Back to the menu" the old window writes
    /// for a moment (PlayLease.LeavingWait; provisional, the menu page's 5 s). After that a team still up is a game open in another
    /// window: Continue opens beside it as it always has, and New game refuses until it is closed.
    /// </summary>
    public TimeSpan LeaseWait { get; set; } = PlayLease.LeavingWait;
    /// <summary>The wait's clock, which only runs forward (a test sets its own).</summary>
    public Func<TimeSpan> Uptime { get; set; } = () => Running.Elapsed;
    private static readonly Stopwatch Running = Stopwatch.StartNew();
    private readonly Dictionary<string, TimeSpan> _heldSince = new(StringComparer.Ordinal);

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
        // A put-away that the process ending cut short is put back first, so the cards and the buttons show each game as it is.
        try
        {
            if (System.IO.Directory.Exists(Layout.TeamsRoot))
                foreach (var folder in System.IO.Directory.GetDirectories(Layout.TeamsRoot).Take(4 * TeamCards.MaxTeams))
                    if (System.IO.Path.GetFileName(folder) is { } id) StartRule.Mend(Layout, id);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
        Teams = TeamCards.Read(Layout, Rules);
        if (TeamId != null && Team == null) { TeamId = null; TakeQuestionDown(); }
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
        TakeQuestionDown();
        Tidy();
        return true;
    }

    public bool SelectIsland(string roomId)
    {
        if (Islands.All(i => i.RoomId != roomId)) { Notice = "That island is not here."; return false; }
        RoomId = roomId;
        Notice = "";
        TakeQuestionDown();
        Tidy();
        return true;
    }

    /// <summary>
    /// The room a launch names (--room=): an island's id, or a room's folder. A folder that is not a listed island's joins the list
    /// in the place of a listed island of the same id (the launch's own room is the one meant, and its manifest names its saves),
    /// first when there is none, and is chosen. A folder with no room in it is refused like an island that is not here.
    /// </summary>
    public bool SelectRoom(string idOrFolder)
    {
        var named = (idOrFolder ?? "").Replace((char)92, '/').TrimEnd('/');
        if (!named.Contains('/')) return SelectIsland(named);
        if (IslandCatalog.Read(named, shipped: named.StartsWith("res://", StringComparison.Ordinal)) is not { } entry) return SelectIsland(named.Split('/').Last());
        var islands = Islands.ToList();
        var at = islands.FindIndex(i => i.RoomId == entry.RoomId);
        if (at < 0) islands.Insert(0, entry);
        else if (islands[at].ManifestPrefix != entry.ManifestPrefix || !SameFolder(islands[at].Directory, entry.Directory)) islands[at] = entry;
        Islands = islands;
        return SelectIsland(entry.RoomId);
    }

    private static bool SameFolder(string a, string b)
    {
        static string Full(string folder) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Godot.ProjectSettings.GlobalizePath(folder)));
        try { return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.IOException) { return false; }
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
        // While New game's question is up, a difficulty named for the same Challenge game is the new game's (the save's own is the
        // one offered first: a new game keeps the last difficulty unless another is chosen). Any other choice takes the question down.
        var forTheNewGame = Asking && mode == "challenge" && Mode == "challenge" && difficulty != null;
        if (mode == "challenge")
        {
            if (FixedDifficulty is { } fixedBySave && !forTheNewGame) { difficulty = fixedBySave; _difficultyPicked = false; }
            else if (difficulty != null && !Offers("challenge", difficulty)) { Notice = DifficultyWords(difficulty) + " is not on offer here."; return false; }
            else _difficultyPicked = difficulty != null;
            difficulty ??= DefaultDifficulty();
            if (difficulty == null) { Notice = "Challenge is not on offer here."; return false; }
        }
        else { difficulty = null; _difficultyPicked = false; }
        if (!forTheNewGame) TakeQuestionDown();
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
            if (FixedDifficulty is { } kept)
            {
                // The save's own; while New game's question is up, the one the new game will have (the save's unless another was chosen).
                if (!Asking || Difficulty == null || !Offers("challenge", Difficulty)) { Difficulty = kept; _difficultyPicked = false; }
            }
            else if (!_difficultyPicked || Difficulty == null || !Offers("challenge", Difficulty)) { Difficulty = DefaultDifficulty(); _difficultyPicked = false; }
            if (!CopyOffered) CopyCreative = false;
        }
        else CopyCreative = false;
    }

    // ---- starting: one rule for every start ----

    /// <summary>Why nothing can start yet, in the player's words; null when a team, an island, a mode and (Challenge) a difficulty are chosen.</summary>
    public string? WhyNotReady =>
        Team is not { Readable: true } ? "Choose your character first." :
        Island == null ? "Choose where to." :
        Mode == null ? "Choose a mode." :
        Mode == "challenge" && Difficulty == null ? "Choose how hard." : null;

    /// <summary>What the start rule offers for the team, the island and the mode chosen, read from the team's folder now (StartRule.For).</summary>
    public StartOffer Offer => WhyNotReady == null ? StartRule.For(Layout, Team!.TeamId, Island!, Mode!) : StartOffer.None;

    /// <summary>"Mira on the Garage, Challenge (Brave)": the game the buttons would start; null while something is not chosen.</summary>
    public string? ChoiceWords =>
        WhyNotReady != null ? null
            : $"{Team!.DisplayName} on {Island!.DisplayName}, {ModeWords(Mode!)}" + (Mode == "challenge" ? $" ({DifficultyWords(Difficulty ?? "standard")})" : "") + (CopyCreative ? ", with your Creative island" : "");

    /// <summary>
    /// Whether a room of the chosen team is up, and whether the start buttons still wait for it. The first time the team is seen
    /// held starts its wait; a team seen free ends it. Asking makes nothing and changes nothing on disk (PlayLease.InUse).
    /// </summary>
    public TeamHold Hold()
    {
        if (Team is not { Readable: true } team) return TeamHold.Free;
        if (!PlayLease.InUse(Layout, team.TeamId))
        {
            _heldSince.Remove(team.TeamId);
            return TeamHold.Free;
        }
        var now = Uptime();
        if (!_heldSince.TryGetValue(team.TeamId, out var since)) _heldSince[team.TeamId] = since = now;
        return now - since < LeaseWait ? TeamHold.Leaving : TeamHold.StillOpen;
    }

    /// <summary>
    /// Continue: the save that is here, opened as it was left. The only start that opens a save, and refused, with Notice, where
    /// there is none (the start rule's first case alone offers it).
    /// </summary>
    public MenuLaunch? ContinueHere() => Open(StartCase.SaveHere, NoSaveHereWords);

    /// <summary>"Bring my game here": this mode's game, which is on another island, comes to this one (the same body and pockets, a first visit here). Nothing is put away.</summary>
    public MenuLaunch? BringHere() => Open(StartCase.GameElsewhere, NoGameToBringWords);

    private MenuLaunch? Open(StartCase needed, string otherwise)
    {
        if (WhyNotReady is { } why) { Notice = why; return null; }
        if (Hold() == TeamHold.Leaving) { Notice = StillClosingWords; return null; }
        if (Asking) GoBack();
        if (Offer.Case != needed) { Notice = otherwise; return null; }
        return Launch(Team!, Island!, Mode!, Mode == "challenge" ? Difficulty : null);
    }

    /// <summary>
    /// New game. Over a game it asks once: the first press only puts the question up (Asking, Question) and returns null with
    /// nothing moved. Pressed again, or where there is no game in this mode, it puts that mode's game away whole
    /// (GameSetAside.PutAway, always called: a put-away that was cut short is put back first and then put away whole; kept, never
    /// deleted; the team, the Gubble and the other mode's game stay) and starts a first game: with the difficulty the old game had
    /// unless another was chosen while the question was up, and with the Creative copy when that was chosen. Null, with Notice and
    /// the old game whole, while the team's room is still leaving, when its game is open in another window, or when the put-away fails.
    /// </summary>
    public MenuLaunch? NewGame()
    {
        if (WhyNotReady is { } why) { Notice = why; return null; }
        if (Hold() == TeamHold.Leaving) { Notice = StillClosingWords; return null; }
        var team = Team!;
        var island = Island!;
        var mode = Mode!;
        if (!Asking && Offer is { Asks: true } offer)
        {
            Asking = true;
            Question = QuestionFor(mode, offer.LastSavedUtc);
            Notice = "";
            return null;
        }
        // Nothing is put away for a start that cannot happen: the island is still where the menu read it, the same island.
        if (IslandCatalog.Read(island.Directory, island.Shipped) is not { } there || there.RoomId != island.RoomId || there.ManifestPrefix != island.ManifestPrefix)
        {
            Notice = IslandGoneWords;
            return null;
        }
        var difficulty = mode == "challenge" ? Difficulty : null;
        PutAwayGame? away;
        try { away = GameSetAside.PutAway(Layout, team.TeamId, mode, Clock(), TimeSpan.Zero); }
        // The question stays up: once what is in the way is gone (a game closed), New game pressed again goes on.
        catch (SetAsideException error) { Notice = error.Message; return null; }
        TakeQuestionDown();
        if (away != null)
        {
            LastPutAway = away;
            // The cards: this mode's body is a first one again.
            Teams = TeamCards.Read(Layout, Rules);
        }
        // The new game keeps the difficulty (no save fixes it any more).
        if (mode == "challenge") { Difficulty = difficulty; _difficultyPicked = true; }
        var launch = Launch(team, island, mode, difficulty);
        if (launch == null && away != null) Notice = PutAwayWords + " " + Notice;
        return launch;
    }

    /// <summary>"Go back": the question goes, and nothing was moved.</summary>
    public void GoBack()
    {
        TakeQuestionDown();
        Notice = "";
        Tidy();
    }

    private void TakeQuestionDown()
    {
        Asking = false;
        Question = "";
    }

    private static string QuestionFor(string mode, DateTime? savedUtc) =>
        savedUtc is { } saved
            ? $"New game? Your {ModeWords(mode)} game from {Day(saved)} is put away, not deleted."
            : $"New game? Your {ModeWords(mode)} game is put away, not deleted.";

    /// <summary>"25 Oct": a day as the menu says it, in this machine's time.</summary>
    public static string Day(DateTime utc) => utc.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);

    /// <summary>
    /// The launch: the copy first when it was chosen (it refuses to overwrite and to be used twice), then the command line the
    /// room's boot reads (the game remembers itself for Continue once its room is up: RoomWorld). Null, with Notice, when the copy fails.
    /// </summary>
    private MenuLaunch? Launch(TeamCard team, IslandEntry island, string mode, string? difficulty)
    {
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

    // ---- the last game ----

    /// <summary>The last game played, when its team, its island and its mode are all still here; null otherwise. Its save may be gone (put away, or the island rebuilt): LastGameToContinue asks for it.</summary>
    public LastGame? LastGamePlayed()
    {
        if (LastGame.Read(Layout) is not { } last) return null;
        if (Teams.FirstOrDefault(t => t.TeamId == last.TeamId) is not { Readable: true }) return null;
        if (Islands.All(i => i.RoomId != last.RoomId) || last.Mode == "live" || !Offers(last.Mode, last.Mode == "challenge" ? last.Difficulty ?? "standard" : null)) return null;
        return last;
    }

    /// <summary>The last game, when its save is there to open: no Continue without its save.</summary>
    public LastGame? LastGameToContinue() =>
        LastGamePlayed() is { } last && IslandSaveInfo.One(Layout, last.TeamId, Islands.First(i => i.RoomId == last.RoomId), last.Mode).Exists ? last : null;

    /// <summary>"Mira on the Garage, Challenge" in one click: the words of a Continue for the last game; null when there is no game to continue.</summary>
    public string? ContinueWords()
    {
        if (LastGameToContinue() is not { } last) return null;
        var team = Teams.First(t => t.TeamId == last.TeamId);
        var island = Islands.First(i => i.RoomId == last.RoomId);
        var kept = last.Mode == "challenge" ? IslandSaveInfo.One(Layout, last.TeamId, island, "challenge").Difficulty ?? last.Difficulty : null;
        return $"{team.DisplayName} on {island.DisplayName}, {ModeWords(last.Mode)}" + (kept != null ? $" ({DifficultyWords(kept)})" : "");
    }

    /// <summary>Continue the last game: its team, island and mode chosen and its save opened. Null, with Notice, when its save is not there, or while its room is still leaving.</summary>
    public MenuLaunch? Continue()
    {
        if (LastGameToContinue() is not { } last) { Notice = "There is no game to continue yet."; return null; }
        var island = Islands.First(i => i.RoomId == last.RoomId);
        var difficulty = last.Mode == "challenge" ? IslandSaveInfo.One(Layout, last.TeamId, island, "challenge").Difficulty ?? last.Difficulty : null;
        TeamId = last.TeamId;
        RoomId = last.RoomId;
        Mode = last.Mode;
        Difficulty = difficulty;
        _difficultyPicked = false;
        CopyCreative = false;
        TakeQuestionDown();
        if (Hold() == TeamHold.Leaving) { Notice = StillClosingWords; return null; }
        Notice = "";
        return new MenuLaunch(last.TeamId, island, last.Mode, difficulty, MenuLaunch.ArgumentsFor(last.TeamId, last.Mode, difficulty));
    }

    /// <summary>
    /// What nothing has chosen yet is taken from the last game: its team (or the one team there is), its island, its mode and
    /// difficulty (or the mode the team played last). A choice already made stays: the command line's, the player's. So the menu
    /// opens with the start rule's buttons ready, and a launch that names only a room lands on that room's Continue and New game.
    /// Notice is left as it was.
    /// </summary>
    public void ChooseFromLastGame()
    {
        var notice = Notice;
        var last = LastGamePlayed();
        if (TeamId == null)
        {
            var readable = Teams.Where(t => t.Readable).ToList();
            if ((last?.TeamId ?? (readable.Count == 1 ? readable[0].TeamId : null)) is { } team) SelectTeam(team);
        }
        if (Team is { Readable: true } chosen)
        {
            var same = last != null && last.TeamId == chosen.TeamId;
            if (RoomId == null && same) SelectIsland(last!.RoomId);
            if (Mode == null && same)
            {
                if (!ChooseMode(last!.Mode, last.Mode == "challenge" ? last.Difficulty : null)) ChooseMode(last.Mode);
            }
            else if (Mode == null && chosen.LastPlayedUtc != null && chosen.LastMode is "creative" or "challenge") ChooseMode(chosen.LastMode);
        }
        Notice = notice;
    }
}
