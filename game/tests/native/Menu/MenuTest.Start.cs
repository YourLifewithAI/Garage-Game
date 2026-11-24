using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// The start rule on today's page (Run 4, MENU-3; docs/game/MECHANICS.md section 3, "Starting and continuing"). The founder: "Every
/// game I start, retains old saved information that I've asked to be wiped clean unless I ask to start a new game, which isn't
/// currently an option." So:
/// <list type="bullet">
/// <item>no Continue without its save;</item>
/// <item>the four cases, for a team, an island and a mode: no game, a game in this mode, a game in the other mode, both; and a
/// game of this mode on another island, and an island rebuilt since its save;</item>
/// <item>what a game is: the body a boot of the other mode leaves behind, which nothing has happened to, is none (a team that
/// has only played Challenge has no Creative game), on a hand-written folder and after a real boot;</item>
/// <item>New game over a game asks once, and pressed again puts it away whole (kept, never deleted; the other mode's game stays);
/// the question is about one game, and another mode or team takes it down;</item>
/// <item>the buttons wait on the team's play lease, and the page stops saying a game is open once it is closed;</item>
/// <item>on today's page the rule's buttons (Continue, New game) stand where Play was, and a launch that names a room lands on them:
/// a room shortcut's own command line (a room's folder outside the island roots, a mode, no team) opens the menu on that room,
/// boots it from its own folder and opens nothing by itself.</item>
/// </list>
/// The put-away itself, the lease and what a new start finds in the room are MENU-2's (tests/native/Saves/NewGameTest.cs).
/// </summary>
public partial class MenuTest
{
    private static readonly DateTime Saved = new(2026, 10, 25, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>Every file under a folder by its place in it, with its SHA-256: the test's own count of what moved. The stores' and the lease's empty .lock files are left out.</summary>
    private static Dictionary<string, string> Hashes(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
                .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var hash) && hash == p.Value);

    /// <summary>A game of a team in a mode as a played one leaves it: that mode's vitals and, where an island is named, its save there (Challenge's with its difficulty).</summary>
    private static void WriteGame(TeamLayout layout, string team, string mode, IslandEntry? island, string? difficulty = null, DateTime? at = null)
    {
        var written = new List<string> { layout.VitalsFile(team, mode) };
        Write(layout.VitalsFile(team, mode), Vitals(team, mode, 1, 0, at ?? Saved));
        if (island != null)
        {
            var save = layout.IslandSave(team, mode, island.RoomId, island.ManifestPrefix);
            Write(save, mode == "challenge" ? "{\"team\":{\"team_id\":\"" + team + "\",\"mode\":\"challenge\",\"difficulty\":\"" + (difficulty ?? "standard") + "\"}}" : "{\"team\":{\"team_id\":\"" + team + "\",\"mode\":\"creative\"}}");
            written.Add(save);
        }
        foreach (var file in written) File.SetLastWriteTimeUtc(file, at ?? Saved);
    }

    private static bool NothingPutAway(TeamLayout layout) => !Directory.Exists(GameSetAside.Root(layout)) || Directory.GetFileSystemEntries(GameSetAside.Root(layout)).Length == 0;

    /// <summary>
    /// The body a room's boot writes for the mode its store opens on while another mode is played (a Challenge boot leaves
    /// modes/creative/vitals.json and its store's empty lease): whole, rested, nothing found. The numbers are the ones a real boot wrote.
    /// </summary>
    private static void WriteFirstBody(TeamLayout layout, string team, string mode)
    {
        Write(layout.VitalsFile(team, mode), Vitals(team, mode, 0, 0, Saved));
        Write(layout.VitalsFile(team, mode) + ".lock", "");
    }

    /// <summary>A mode's vitals document with this health of 100, these Fractals found and in hand, and this many knockouts.</summary>
    private static string Body(string team, string mode, float health, int found = 0, int recoveries = 0) =>
        Encoding.UTF8.GetString(LocalPlayerVitalsStore.SerializeMode(team, mode, Saved, PlayerVitals.Of(health, 100, 0.4f, 0.4f, Array.Empty<string>(), recoveries, new PlayerFractals(found, found, 0, 0), 8)));

    /// <summary>When a team's game in a mode was last written, by the test's own reading of its files (the stores' leases left out).</summary>
    private static DateTime NewestWrite(TeamLayout layout, string team, string mode) =>
        Directory.GetFiles(layout.ModeFolder(team, mode), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal)).Max(File.GetLastWriteTimeUtc);

    /// <summary>
    /// A copy of the shipped test room in a folder of its own outside the island roots, under another name: the same room id
    /// under another manifest, as the fixture cache's island at ten times its size is to the installed one.
    /// </summary>
    private static string RoomOfItsOwn(string name, string display)
    {
        var from = ProjectSettings.GlobalizePath("res://rooms/test_room");
        var to = Path.Combine(Folder(name), "test_room");
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var manifest = Path.Combine(to, "room.json");
        var text = File.ReadAllText(manifest);
        var renamed = text.Replace("\"display_name\": \"Test room\"", "\"display_name\": " + System.Text.Json.JsonSerializer.Serialize(display));
        if (renamed == text) throw new InvalidOperationException("the test room's name was not found in its manifest");
        File.WriteAllBytes(manifest, new UTF8Encoding(false).GetBytes(renamed));
        return to.Replace('\\', '/');
    }

    // ---- the rule, on the model: what is offered, what each button does ----

    private void TestStartRule()
    {
        var layout = new TeamLayout(Folder("start"));
        TeamMaker.Create(layout, _rules, "Mira", "", "", "#65b9b0", "", Now);
        var islands = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" });
        var plain = islands.First(i => i.RoomId == "plain_isle");
        var tutorial = islands.First(i => i.RoomId == "tutorial_isle");
        var folder = layout.TeamFolder("mira");
        var brave = new[] { "--team=mira", "--mode=challenge", "--difficulty=brave" };

        // No Continue without its save (this check failed before MENU-3: Continue was offered for any last game on record).
        new LastGame("mira", "plain_isle", "challenge", "gentle", Now).Write(layout);
        var model = NewModel(layout);
        Check(!File.Exists(layout.IslandSave("mira", "challenge", "plain_isle", plain.ManifestPrefix)) && model.ContinueWords() == null && model.LastGameToContinue() == null,
            "no Continue without its save: the last game's save is not there, and Continue is not offered (" + model.ContinueWords() + ")");
        Check(model.LastGamePlayed() is { TeamId: "mira", RoomId: "plain_isle" } && model.Continue() == null && model.Notice == "There is no game to continue yet.",
            "the last game is still known (the menu opens on it), and Continue pressed all the same starts nothing: \"" + model.Notice + "\"");
        // The same, as the page offers it: the menu opens on the last game's choice, and the rule has no Continue for it.
        var door = NewModel(layout);
        door.ChooseFromLastGame();
        var atTheDoor = door.Offer;
        Check(door.TeamId == "mira" && door.RoomId == "plain_isle" && door.Mode == "challenge" && door.Difficulty == "gentle" && atTheDoor.Case == StartCase.NoGame && !atTheDoor.Continue && atTheDoor.NewGame &&
              door.ContinueHere() == null && door.Notice == MenuModel.NoSaveHereWords,
            $"no Continue without its save, as the page offers it: the menu opens on the last game's choice, the rule offers New game alone ({atTheDoor.Case}), and Continue pressed all the same opens nothing");

        // Case 1: no game. New game alone, nothing asked.
        model.SelectTeam("mira");
        model.SelectIsland("plain_isle");
        model.ChooseMode("challenge", "brave");
        var offer = model.Offer;
        Check(offer.Case == StartCase.NoGame && offer.NewGame && !offer.Continue && !offer.Bring && !offer.Asks && model.ChoiceWords == "Mira on Plain Isle, Challenge (Brave)",
            $"case 1, no game: New game alone, and it asks nothing ({offer.Case})");
        var noSave = model.ContinueHere();
        var noSaveWords = model.Notice;
        var noGame = model.BringHere();
        Check(noSave == null && noSaveWords == MenuModel.NoSaveHereWords && noGame == null && model.Notice == MenuModel.NoGameToBringWords,
            "Continue and \"Bring my game here\" pressed where the rule does not offer them start nothing: \"" + noSaveWords + "\" \"" + model.Notice + "\"");
        var before = Hashes(folder);
        var first = model.NewGame();
        Check(first != null && !model.Asking && first.Arguments.SequenceEqual(brave) && first.Island.RoomId == "plain_isle" && first.TeamId == "mira" && model.LastPutAway == null &&
              NothingPutAway(layout) && Same(before, Hashes(folder)),
            "New game where there is no game starts at once: nothing asked, nothing put away, nothing changed: " + (first == null ? model.Notice : string.Join(" ", first.Arguments)));

        // Case 2: a game in this mode (what that first game leaves once played: its vitals, its save here, another island's too).
        WriteGame(layout, "mira", "challenge", plain, "brave");
        WriteGame(layout, "mira", "challenge", tutorial, "gentle", Saved.AddDays(-2));
        model.Reload();
        offer = model.Offer;
        Check(offer.Case == StartCase.SaveHere && offer.Continue && offer.NewGame && offer.Asks && !offer.Bring && offer.LastSavedUtc is { } last && Math.Abs((last - Saved).TotalSeconds) < 2 &&
              model.ContinueWords() == "Mira on Plain Isle, Challenge (Brave)" && model.LastGameToContinue() != null,
            $"case 2, a game in this mode: Continue, or New game, which would ask; and the last game's Continue is offered now that its save is there ({offer.Case}, saved {offer.LastSavedUtc:u})");
        before = Hashes(folder);
        var opened = model.ContinueHere();
        Check(opened != null && opened.Arguments.SequenceEqual(brave) && !model.Asking && Same(before, Hashes(folder)) && NothingPutAway(layout),
            "Continue opens the save that is here, at the difficulty it keeps, and moves nothing");

        // Case 3: a game in the other mode only, as a played one really leaves it: a Challenge boot also writes the Creative body,
        // which nothing has happened to. Creative has no game, on any island: New game there asks nothing, puts nothing away
        // (no folder for a game nobody played) and leaves the Challenge game and that body alone.
        WriteFirstBody(layout, "mira", "creative");
        before = Hashes(folder);
        model.ChooseMode("creative");
        offer = model.Offer;
        model.SelectIsland("tutorial_isle");
        var onTheOther = model.Offer;
        model.SelectIsland("plain_isle");
        var creative = model.NewGame();
        Check(GameSetAside.HasGame(layout, "mira", "creative") && !StartRule.HasGame(layout, "mira", "creative") && offer.Case == StartCase.NoGame && onTheOther.Case == StartCase.NoGame &&
              !offer.Continue && !offer.Bring && !offer.Asks && !onTheOther.Bring && !onTheOther.Asks,
            $"case 3, a game in the other mode only: the Creative body a Challenge boot leaves is no Creative game, here or on another island: no Continue, no \"Bring my game here\", nothing to ask ({offer.Case}, {onTheOther.Case})");
        Check(creative != null && creative.Mode == "creative" && creative.Difficulty == null && !model.Asking && model.LastPutAway == null &&
              creative.Arguments.SequenceEqual(new[] { "--team=mira", "--mode=creative" }) && Same(before, Hashes(folder)) && NothingPutAway(layout),
            "New game there asks nothing and starts; nothing is put away, and the other mode's game and that first body are not touched: " + (creative == null ? model.Notice : string.Join(" ", creative.Arguments)));

        // Case 4: both. New game over the Challenge game asks once; pressed again it puts that game away, and only that game.
        WriteGame(layout, "mira", "creative", plain, at: Saved.AddDays(-5));
        var store = new TeamStore(layout.TeamFile("mira"), "mira");
        store.Save(store.Load()!.WithPockets("challenge", InventoryContents.Empty.WithUnits("apple", 3)).WithHand("challenge", new HandSelection(HandSelection.PocketsLayer, 1, null))
            .WithPockets("creative", InventoryContents.Empty.WithUnits("stone", 2)));
        model.Reload();
        Check(model.Offer.Case == StartCase.SaveHere && model.ChooseMode("challenge") && model.Offer.Case == StartCase.SaveHere && model.Difficulty == "brave",
            "case 4, both: each mode has its own Continue");
        before = Hashes(folder);
        var theCreative = Hashes(layout.ModeFolder("mira", "creative"));
        var theChallenge = Hashes(layout.ModeFolder("mira", "challenge"));
        var asked = model.NewGame();
        var question = model.Question;
        Check(asked == null && model.Asking && question == $"New game? Your Challenge game from {MenuModel.Day(Saved)} is put away, not deleted." && Same(before, Hashes(folder)) && NothingPutAway(layout),
            "New game over a game asks once, and the first press moves nothing: \"" + question + "\"");
        model.GoBack();
        Check(!model.Asking && model.Question.Length == 0 && model.Offer.Continue && Same(before, Hashes(folder)) && NothingPutAway(layout), "\"Go back\" takes the question down, and the game is as it was");

        // The question is about one game. Another mode, or a team (another one, or the same chosen again, as the Edit panel does),
        // takes it down; New game pressed after that asks about the game now chosen, and no game is put away that nobody was asked about.
        TeamMaker.Create(layout, _rules, "Owl", "", "", "#65b9b0", "", Now);
        WriteGame(layout, "owl", "challenge", plain, "gentle", Saved.AddDays(-9));
        model.Reload();
        var owl = Hashes(layout.TeamFolder("owl"));
        model.NewGame();
        var wasUp = model.Asking;
        model.ChooseMode("creative");
        var modeEndsIt = wasUp && !model.Asking && model.Question.Length == 0;
        var asksOfCreative = model.NewGame() == null && model.Asking && model.Question == $"New game? Your Creative game from {MenuModel.Day(Saved.AddDays(-5))} is put away, not deleted.";
        var creativeWhole = Same(before, Hashes(folder)) && NothingPutAway(layout);
        model.ChooseMode("challenge");
        var modeBackEndsIt = !model.Asking;
        model.NewGame();
        model.SelectTeam("mira");
        var sameTeamEndsIt = !model.Asking && model.Question.Length == 0;
        model.NewGame();
        model.SelectTeam("owl");
        var teamEndsIt = !model.Asking && model.Question.Length == 0;
        var asksOfOwl = model.NewGame() == null && model.Asking && model.Question == $"New game? Your Challenge game from {MenuModel.Day(Saved.AddDays(-9))} is put away, not deleted.";
        var bothWhole = Same(owl, Hashes(layout.TeamFolder("owl"))) && Same(before, Hashes(folder)) && NothingPutAway(layout);
        model.SelectTeam("mira");
        Check(modeEndsIt && modeBackEndsIt && sameTeamEndsIt && teamEndsIt && !model.Asking,
            $"another mode or another team takes New game's question down (mode {modeEndsIt}, back {modeBackEndsIt}, the same team again {sameTeamEndsIt}, another team {teamEndsIt})");
        Check(asksOfCreative && creativeWhole && asksOfOwl && bothWhole,
            $"and New game pressed after such a choice asks about the game now chosen and moves nothing: no game is put away that nobody was asked about (Creative asked {asksOfCreative}, whole {creativeWhole}; the other team asked {asksOfOwl}, whole {bothWhole})");

        model.NewGame();
        model.SelectIsland("tutorial_isle");
        var choiceEndsIt = !model.Asking;
        model.SelectIsland("plain_isle");
        model.NewGame();
        var started = model.NewGame();
        var away = GameSetAside.List(layout, "mira");
        Check(choiceEndsIt && started != null && !model.Asking && started.Mode == "challenge" && started.Difficulty == "brave" && started.Arguments.SequenceEqual(brave) && started.Island.RoomId == "plain_isle",
            "another choice takes the question down too; asked again, the second press starts the new game, in the old game's mode and at its difficulty: " + (started == null ? model.Notice : string.Join(" ", started.Arguments)));
        Check(away.Count == 1 && away[0].Ledger is { TeamId: "mira", Mode: "challenge" } && model.LastPutAway?.Folder == away[0].Folder && GameSetAside.Problems(away[0].Folder).Count == 0 &&
              Same(theChallenge, Hashes(Path.Combine(away[0].Folder, "modes", "challenge"))) && theChallenge.Count == 3 && !Directory.Exists(layout.ModeFolder("mira", "challenge")),
            "and puts the old game away first: its vitals and its save on every island, byte for byte, in a folder the contract accepts; none of it is left in play: " + string.Join("; ", away.Count == 1 ? GameSetAside.Problems(away[0].Folder) : new[] { away.Count + " folders" }));
        var team = TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("mira")));
        Check(Same(theCreative, Hashes(layout.ModeFolder("mira", "creative"))) && team.PocketsOf("creative").Units("stone") == 2 && team.PocketsOf("challenge").IsEmpty && team.HandOf("challenge").IsNone &&
              away.Count == 1 && TeamFile.Parse(File.ReadAllBytes(Path.Combine(away[0].Folder, "team.json"))).PocketsOf("challenge").Units("apple") == 3,
            "only that mode's game: the Creative game and its pockets are untouched, and the Challenge pockets went with the game");
        Check(model.Offer.Case == StartCase.NoGame && model.ContinueHere() == null && model.ContinueWords() == null && model.Difficulty == "brave",
            "after New game there is no Challenge game in play: no Continue, here or for the last game, until the new one is saved");
        var third = model.NewGame();
        Check(third != null && !model.Asking && GameSetAside.List(layout, "mira").Count == 1, "New game pressed where the game is already put away asks nothing and puts nothing more away");
        Check(model.ChooseMode("creative") && model.Offer is { Case: StartCase.SaveHere, Continue: true }, "and Creative still continues");

        // A game of this mode on another island: bring it here, or New game.
        TeamMaker.Create(layout, _rules, "Theo", "", "", "#65b9b0", "", Now);
        WriteGame(layout, "theo", "challenge", plain, "standard");
        var theo = NewModel(layout);
        theo.SelectTeam("theo");
        theo.SelectIsland("tutorial_isle");
        theo.ChooseMode("challenge", "gentle");
        offer = theo.Offer;
        before = Hashes(layout.TeamFolder("theo"));
        var notHere = theo.ContinueHere();
        var brought = theo.BringHere();
        Check(offer.Case == StartCase.GameElsewhere && offer.Bring && offer.NewGame && offer.Asks && !offer.Continue && notHere == null &&
              brought != null && brought.Island.RoomId == "tutorial_isle" && brought.Arguments.SequenceEqual(new[] { "--team=theo", "--mode=challenge", "--difficulty=gentle" }) &&
              Same(before, Hashes(layout.TeamFolder("theo"))) && GameSetAside.List(layout, "theo").Count == 0,
            $"a game of this mode on another island: no Continue here; \"Bring my game here\" starts here with that game, nothing put away; New game would ask ({offer.Case})");

        // An island rebuilt since its save (a save only under an older manifest): New game alone, and the old game is kept.
        WriteGame(layout, "theo", "creative", null);
        Write(layout.IslandSave("theo", "creative", "test_room", "0123456789abcdef"), "{}");
        theo.SelectIsland("test_room");
        theo.ChooseMode("creative");
        offer = theo.Offer;
        var rebuiltAsks = theo.NewGame() == null && theo.Asking;
        var rebuilt = theo.NewGame();
        var theirs = GameSetAside.List(layout, "theo");
        Check(offer.Case == StartCase.Rebuilt && offer.NewGame && offer.Asks && !offer.Continue && !offer.Bring && rebuiltAsks && rebuilt != null && theirs.Count == 1 && theirs[0].Ledger.Mode == "creative" &&
              theirs[0].Ledger.Files.Any(f => f.Path == "modes/creative/islands/test_room/0123456789abcdef/inventions.json") && GameSetAside.Problems(theirs[0].Folder).Count == 0 &&
              theo.Offer.Case == StartCase.NoGame && Same(before, Hashes(layout.TeamFolder("theo")).Where(p => !p.Key.StartsWith("modes/creative/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value)),
            $"an island rebuilt since its save: New game alone; it asks, and the old game is kept, put away whole with the save of the island as it was; the Challenge game stays ({offer.Case})");
    }

    // ---- what a game is: a first body alone is none; anything more is one ----

    private void TestStartFirstBody()
    {
        var layout = new TeamLayout(Folder("start-body"));
        TeamMaker.Create(layout, _rules, "Owl", "", "", "#65b9b0", "", Now);
        var islands = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" });
        var plain = islands.First(i => i.RoomId == "plain_isle");
        var tutorial = islands.First(i => i.RoomId == "tutorial_isle");
        var vitals = layout.VitalsFile("owl", "creative");
        StartCase Case() => StartRule.For(layout, "owl", plain, "creative").Case;

        var nothing = Case();
        WriteFirstBody(layout, "owl", "creative");
        Check(nothing == StartCase.NoGame && Case() == StartCase.NoGame && GameSetAside.HasGame(layout, "owl", "creative") && !StartRule.HasGame(layout, "owl", "creative") &&
              StartRule.FirstBody(LocalPlayerVitalsStore.ParseMode(File.ReadAllBytes(vitals)).Vitals),
            "a mode's folder that holds only a first body (and its store's empty lease) is no game, though New game's put-away would move it");

        // Anything more is a game: in doubt there is one, and New game asks.
        var more = new List<string>();
        void Is(string what, Action make, Action unmake)
        {
            make();
            if (Case() != StartCase.GameElsewhere || !StartRule.For(layout, "owl", plain, "creative").Asks) more.Add(what + " reads as " + Case());
            unmake();
            if (Case() != StartCase.NoGame) more.Add("after " + what + " the first body reads as " + Case());
        }
        void First() => Write(vitals, Vitals("owl", "creative", 0, 0, Saved));
        var stray = Path.Combine(layout.ModeFolder("owl", "creative"), "notes.txt");
        var store = new TeamStore(layout.TeamFile("owl"), "owl");
        var team = store.Load()!;
        Is("a body that was hurt", () => Write(vitals, Body("owl", "creative", 50)), First);
        Is("a body that found a Fractal", () => Write(vitals, Body("owl", "creative", 100, found: 1)), First);
        Is("a body that was knocked out", () => Write(vitals, Body("owl", "creative", 100, recoveries: 1)), First);
        Is("a vitals file that does not read", () => Write(vitals, "not a vitals file"), First);
        Is("another mode's vitals filed here", () => Write(vitals, Vitals("owl", "challenge", 0, 0, Saved)), First);
        Is("another team's vitals filed here", () => Write(vitals, Vitals("mira", "creative", 0, 0, Saved)), First);
        Is("a stray file beside it", () => Write(stray, "kept"), () => File.Delete(stray));
        Is("a lease with something in it", () => Write(vitals + ".lock", "held"), () => Write(vitals + ".lock", ""));
        Is("something in that mode's pockets", () => store.Save(store.Load()!.WithPockets("creative", InventoryContents.Empty.WithUnits("stone", 2))), () => store.Save(store.Load()!.WithPockets("creative", InventoryContents.Empty)));
        Is("something in that mode's hand", () => store.Save(store.Load()!.WithHand("creative", new HandSelection(HandSelection.PocketsLayer, 1, null))), () => store.Save(store.Load()!.WithHand("creative", HandSelection.None)));
        Is("a team file that does not read", () => Write(layout.TeamFile("owl"), "not a team file"), () => File.WriteAllBytes(layout.TeamFile("owl"), team.Serialize()));
        var save = layout.IslandSave("owl", "creative", "tutorial_isle", tutorial.ManifestPrefix);
        Is("a save on another island", () => Write(save, "{}"), () => Directory.Delete(layout.IslandsRoot("owl", "creative"), recursive: true));
        Check(more.Count == 0, "anything more than a first body is a game, and New game asks: a body hurt, grown or knocked out, a file that does not read or is filed wrong, a stray file, pockets, a hand, a save on another island" +
                               (more.Count == 0 ? "" : ": " + string.Join("; ", more)));

        // The other mode's own pockets are not this mode's game.
        store.Save(store.Load()!.WithPockets("challenge", InventoryContents.Empty.WithUnits("apple", 1)));
        Check(Case() == StartCase.NoGame && StartRule.For(layout, "owl", plain, "challenge").Case == StartCase.GameElsewhere,
            "the other mode's pockets are the other mode's game, not this one's");
    }

    // ---- the buttons wait on the team's play lease ----

    private void TestStartLease()
    {
        var layout = new TeamLayout(Folder("start-lease"));
        TeamMaker.Create(layout, _rules, "Jets", "", "", "#65b9b0", "", Now);
        var plain = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" }).First(i => i.RoomId == "plain_isle");
        WriteGame(layout, "jets", "challenge", plain, "standard");
        new LastGame("jets", "plain_isle", "challenge", "standard", Now).Write(layout);
        var uptime = TimeSpan.FromSeconds(100);
        var model = NewModel(layout);
        model.Uptime = () => uptime;
        model.ChooseFromLastGame();
        var folder = layout.TeamFolder("jets");
        var before = Hashes(folder);
        Check(model.Hold() == TeamHold.Free && model.LeaseWait == PlayLease.LeavingWait && model.Offer.Continue, "nobody has the team up: nothing waits (the wait is the lease's own, 5 s)");

        using (var room = PlayLease.TryPlay(layout, "jets")!)
        {
            var hold = model.Hold();
            var opened = model.ContinueHere();
            var openedWords = model.Notice;
            var fresh = model.NewGame();
            var freshWords = model.Notice;
            var lastGame = model.Continue();
            Check(hold == TeamHold.Leaving && opened == null && openedWords == MenuModel.StillClosingWords && fresh == null && !model.Asking && freshWords == MenuModel.StillClosingWords && lastGame == null &&
                  model.Notice == MenuModel.StillClosingWords && Same(before, Hashes(folder)) && NothingPutAway(layout),
                "while a room of the team is still up, Continue and New game wait: nothing starts, nothing is asked, nothing moves (\"" + openedWords + "\")");
            uptime += TimeSpan.FromSeconds(4.9);
            Check(model.Hold() == TeamHold.Leaving, "and they go on waiting for the lease's whole wait");
        }
        Check(model.Hold() == TeamHold.Free && model.ContinueHere() != null, "as soon as the room has let go of the team, the buttons start again");

        using (var other = PlayLease.TryPlay(layout, "jets")!)
        {
            var again = model.Hold();
            uptime += PlayLease.LeavingWait;
            var hold = model.Hold();
            var beside = model.ContinueHere();
            var asks = model.NewGame();
            var asking = model.Asking;
            var refused = model.NewGame();
            Check(again == TeamHold.Leaving && hold == TeamHold.StillOpen && beside != null && asks == null && asking && refused == null && model.Asking && model.Notice == PlayLease.StillOpen &&
                  Same(before, Hashes(folder)) && NothingPutAway(layout),
                "a team still up after the wait is a game open in another window: Continue opens beside it as before; New game asks, then is refused (\"" + model.Notice + "\"), and nothing moves");
        }
        var after = model.NewGame();
        Check(after != null && !model.Asking && GameSetAside.List(layout, "jets").Count == 1 && model.Offer.Case == StartCase.NoGame,
            "once that game is closed, New game pressed again puts the old game away: the question was asked once");
    }

    // ---- a put-away that the process ending cut short ----

    private void TestStartCutShort()
    {
        var layout = new TeamLayout(Folder("start-cut"));
        TeamMaker.Create(layout, _rules, "Raft", "", "", "#65b9b0", "", Now);
        var plain = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" }).First(i => i.RoomId == "plain_isle");
        WriteGame(layout, "raft", "challenge", plain, "gentle");
        var folder = layout.TeamFolder("raft");
        var before = Hashes(folder);
        var theGame = Hashes(layout.ModeFolder("raft", "challenge"));
        void CutShort()
        {
            GameSetAside.AtStep = step => { if (step == "moved") throw new GameSetAside.PowerCut(); };
            try { GameSetAside.PutAway(layout, "raft", "challenge", Now); }
            catch (GameSetAside.PowerCut) { }
            finally { GameSetAside.AtStep = null; }
        }
        try
        {
            CutShort();
            var halfMoved = GameSetAside.PendingPutAway(layout, "raft") == "challenge" && !Directory.Exists(layout.ModeFolder("raft", "challenge"));
            var model = NewModel(layout);
            model.SelectTeam("raft");
            model.SelectIsland("plain_isle");
            model.ChooseMode("challenge");
            Check(halfMoved && GameSetAside.PendingPutAway(layout, "raft") == null && Same(before, Hashes(folder)) && NothingPutAway(layout) && model.Offer is { Case: StartCase.SaveHere, Continue: true },
                "a put-away that the process ending cut short is put back when the menu opens: the game is whole again, nothing is left under set_aside, and Continue finds its save");

            // Cut short again under the open menu (another window's New game ended part-way): New game here still asks, and then
            // puts the whole game away, never half of it.
            CutShort();
            var offer = model.Offer;
            var asks = model.NewGame() == null && model.Asking;
            var started = model.NewGame();
            var away = GameSetAside.List(layout, "raft");
            Check(offer.Asks && !offer.Continue && asks && started != null && away.Count == 1 && Directory.GetDirectories(GameSetAside.Root(layout)).Length == 1 && GameSetAside.Problems(away[0].Folder).Count == 0 &&
                  Same(theGame, Hashes(Path.Combine(away[0].Folder, "modes", "challenge"))) && GameSetAside.PendingPutAway(layout, "raft") == null && !File.Exists(GameSetAside.NotePath(layout, "raft")),
                "New game always calls the put-away: a game half moved is put back first and then put away whole, never left behind a new one");
        }
        finally { GameSetAside.AtStep = null; }
    }

    // ---- what the menu opens on, and the room a launch names ----

    private void TestStartChoices()
    {
        var layout = new TeamLayout(Folder("start-choices"));
        var islands = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" });
        var plain = islands.First(i => i.RoomId == "plain_isle");
        var empty = NewModel(layout);
        empty.ChooseFromLastGame();
        Check(empty.TeamId == null && empty.RoomId == null && empty.Mode == null, "no team and no last game: the menu opens with nothing chosen");

        TeamMaker.Create(layout, _rules, "Imp", "", "", "#65b9b0", "", Now);
        var one = NewModel(layout);
        one.SetNotice("a word from the menu");
        one.ChooseFromLastGame();
        Check(one.TeamId == "imp" && one.RoomId == null && one.Mode == null && one.Notice == "a word from the menu" && one.WhyNotReady == "Choose where to.",
            "the one team there is is chosen; it has not played, so its island and mode are still asked; the menu's word stays");

        WriteGame(layout, "imp", "challenge", plain, "brave");
        var named = NewModel(layout);
        named.SelectRoom("tutorial_isle");
        named.ChooseFromLastGame();
        Check(named.TeamId == "imp" && named.RoomId == "tutorial_isle" && named.Mode == "challenge" && named.Offer is { Case: StartCase.GameElsewhere, Bring: true },
            "a launch that names only a room: the room stays, the one team and the mode it played last are chosen, and the rule's buttons are ready (" + named.Offer.Case + ")");

        TeamMaker.Create(layout, _rules, "Keg", "", "", "#65b9b0", "", Now);
        new LastGame("imp", "plain_isle", "challenge", "brave", Now).Write(layout);
        var two = NewModel(layout);
        two.ChooseFromLastGame();
        Check(two.TeamId == "imp" && two.RoomId == "plain_isle" && two.Mode == "challenge" && two.Difficulty == "brave" && two.Offer.Continue && !two.GamePutAway && two.ChoiceWords == "Imp on Plain Isle, Challenge (Brave)",
            "with two teams the menu opens on the last game: its team, island, mode and difficulty, Continue one press away (" + two.ChoiceWords + ")");
        var keg = NewModel(layout);
        keg.SelectTeam("keg");
        keg.ChooseFromLastGame();
        Check(keg.TeamId == "keg" && keg.RoomId == null && keg.Mode == null, "a team already chosen keeps its place, and takes nothing from another team's last game");
        var asked = NewModel(layout);
        asked.SelectTeam("imp");
        asked.SelectRoom("tutorial_isle");
        asked.ChooseMode("creative");
        asked.ChooseFromLastGame();
        Check(asked.RoomId == "tutorial_isle" && asked.Mode == "creative", "what the command line chose stays: the last game fills only what is open");

        GameSetAside.PutAway(layout, "imp", "challenge", Now);
        var after = NewModel(layout);
        after.ChooseFromLastGame();
        Check(after.TeamId == "imp" && after.RoomId == "plain_isle" && after.Mode == "challenge" && after.Difficulty == "brave" && after.Offer.Case == StartCase.NoGame && after.ContinueWords() == null && after.GamePutAway,
            "with the last game put away the menu still opens on its choices, with New game alone, at the difficulty it had, and knows a game of that mode was put away");

        // The room a launch names: an id, the folder of a listed island, or a folder of its own.
        var model = NewModel(layout);
        var listed = model.Islands.Select(i => i.RoomId).ToList();
        Check(model.SelectRoom("plain_isle") && model.RoomId == "plain_isle" && !model.SelectRoom("nowhere") && model.RoomId == "plain_isle" && model.Notice == "That island is not here.",
            "a launch's room by its id is the listed island; one that is not here is refused");
        var tutorialFolder = IslandsFolder() + "/zz_tutorial";
        Check(model.SelectRoom(ProjectSettings.GlobalizePath(tutorialFolder)) && model.RoomId == "tutorial_isle" && model.Islands.Select(i => i.RoomId).SequenceEqual(listed) && model.Island!.Directory == tutorialFolder,
            "a launch's room by the folder of a listed island is that island, and the list is as it was");
        var elsewhere = Folder("elsewhere");
        Write(Path.Combine(elsewhere, "plain_isle", "room.json"), Manifest("plain_isle", "Plain Isle again", tutorial: false, shape: true));
        Write(Path.Combine(elsewhere, "new_land", "room.json"), Manifest("new_land", "New land", tutorial: false, shape: false));
        var own = Path.Combine(elsewhere, "plain_isle").Replace('\\', '/');
        Check(model.SelectRoom(Path.Combine(elsewhere, "plain_isle") + Path.DirectorySeparatorChar) && model.RoomId == "plain_isle" && model.Islands.Select(i => i.RoomId).SequenceEqual(listed) &&
              model.Island!.Directory == own && model.Island.ManifestPrefix != plain.ManifestPrefix && model.Island.DisplayName == "Plain Isle again" && model.Island.HasShape,
            "a launch's room by a folder of its own takes the place of the listed island of its id: the launch's room is the one that starts, and its manifest names its saves");
        Check(model.SelectRoom(Path.Combine(elsewhere, "new_land")) && model.RoomId == "new_land" && model.Islands[0].RoomId == "new_land" && model.Islands.Count == listed.Count + 1,
            "a room that is not listed joins the list, first, and is chosen");
        Check(!model.SelectRoom(Path.Combine(elsewhere, "nothing_here")) && model.RoomId == "new_land" && model.Notice == "That island is not here.", "a folder with no room in it is refused, and the choice stays");
        model.SelectRoom(own);
        model.SelectTeam("imp");
        model.ChooseMode("challenge", "gentle");
        var start = model.NewGame();
        Check(start != null && start.Island.Directory == own && start.Island.RoomId == "plain_isle", "and the game starts in the launch's own folder: " + start?.Island.Directory);

        // Two islands carry one id now: the listed Plain Isle and the launch's own, which took its place in the list. A save on
        // the listed one is a game on another island, which is still here: it is not the launch's island before it was rebuilt.
        WriteGame(layout, "keg", "creative", plain);
        model.SelectTeam("keg");
        model.ChooseMode("creative");
        var twin = model.Offer;
        var listedOne = NewModel(layout);
        listedOne.SelectTeam("keg");
        listedOne.SelectIsland("plain_isle");
        listedOne.ChooseMode("creative");
        Check(twin.Case == StartCase.GameElsewhere && twin.Bring && twin.NewGame && !twin.Continue && listedOne.Offer.Case == StartCase.SaveHere &&
              StartRule.For(layout, "keg", model.Island!, "creative").Case == StartCase.Rebuilt &&
              StartRule.For(layout, "keg", model.Island!, "creative", new[] { plain.ManifestPrefix }).Case == StartCase.GameElsewhere,
            $"a save on the listed island of the same id is a game on another island (\"Bring my game here\"), not this island rebuilt; only a save under a manifest the menu does not know reads as rebuilt ({twin.Case})");

        // What a room shortcut passes once it opens the menu: a room's folder, a mode and a difficulty, and no team
        // (run-room.ps1 -Room landscape10 -Mode challenge -Difficulty standard). The room is the launch's own, the team is the last
        // game's, and the game saved there waits behind Continue at the difficulty it keeps: nothing opens unasked. The boot's own
        // first choice keeps only the folder's last name (the listed island of that id) and no mode without a team; the launch's
        // command line, read by the model (ChooseFromLaunch), puts both right.
        WriteGame(layout, "imp", "challenge", model.Island!, "brave");
        var line = new[] { "--no-playtest-record", "--menu", "--room=" + own, "--mode=challenge", "--difficulty=standard" };
        var shortcut = NewModel(layout);
        shortcut.SelectIsland("plain_isle");
        var listedFirst = shortcut.Island!.Directory != own && shortcut.Mode == null;
        shortcut.SetNotice("a word at the door");
        shortcut.ChooseFromLaunch(line);
        var fromLaunch = shortcut.TeamId == null && shortcut.Island!.Directory == own && shortcut.Mode == "challenge" && shortcut.Difficulty == "standard" && shortcut.Notice == "a word at the door";
        shortcut.ChooseFromLastGame();
        Check(listedFirst && fromLaunch && shortcut.TeamId == "imp" && shortcut.Island!.Directory == own && shortcut.Mode == "challenge" && shortcut.Difficulty == "brave" && !shortcut.Asking &&
              shortcut.Offer is { Case: StartCase.SaveHere, Continue: true, NewGame: true, Asks: true } && shortcut.ChoiceWords == "Imp on Plain Isle again, Challenge (Brave)" && shortcut.Notice == "a word at the door",
            "a room shortcut's command line (a folder, a mode, a difficulty, no team) lands on that room's Continue and New game, and the menu's word stays: " + shortcut.ChoiceWords + ", " + shortcut.Offer.Case);
        shortcut.ChooseFromLaunch(line);
        var twice = shortcut.TeamId == "imp" && shortcut.Island!.Directory == own && shortcut.Mode == "challenge" && shortcut.Difficulty == "brave" && shortcut.Islands.Count(i => i.RoomId == "plain_isle") == 1;
        var byName = NewModel(layout);
        byName.ChooseFromLaunch(new[] { "--team=keg", "--room=tutorial_isle", "--mode=challenge", "--difficulty=nasty", "--room=", "--mode= " });
        var nothingNamed = NewModel(layout);
        nothingNamed.ChooseFromLaunch(new[] { "--menu", "--fullscreen", "--team=nobody", "--room=" + Path.Combine(elsewhere, "nothing_here"), "--mode=live" });
        Check(twice && byName.TeamId == "keg" && byName.RoomId == "tutorial_isle" && byName.Mode == "challenge" && byName.Difficulty is "gentle" or "standard" && byName.Notice.Length == 0 &&
              nothingNamed.TeamId == null && nothingNamed.RoomId == null && nothingNamed.Mode == null && nothingNamed.Notice.Length == 0,
            "the launch's choices made twice change nothing; a team and a room's id are taken as named, a difficulty there is not falls back to the offered one, and what does not take (no such team, no room in the folder, Live) is no message at the door");

        // The island changed, or went, under the open menu: New game over a game is refused before anything is put away.
        WriteGame(layout, "keg", "challenge", model.Island!, "gentle");
        model.Reload();
        model.SelectTeam("keg");
        model.ChooseMode("challenge");
        var kit2 = Hashes(layout.TeamFolder("keg"));
        var asksFirst = model.Offer.Case == StartCase.SaveHere && model.NewGame() == null && model.Asking;
        Write(Path.Combine(elsewhere, "plain_isle", "room.json"), Manifest("plain_isle", "Plain Isle rebuilt", tutorial: false, shape: false));
        var changed = model.NewGame();
        var changedWords = model.Notice;
        File.Delete(Path.Combine(elsewhere, "plain_isle", "room.json"));
        var gone = model.NewGame();
        Check(asksFirst && changed == null && changedWords == MenuModel.IslandGoneWords && gone == null && model.Notice == MenuModel.IslandGoneWords &&
              Same(kit2, Hashes(layout.TeamFolder("keg"))) && GameSetAside.List(layout, "keg").Count == 0,
            "an island rebuilt or gone under the open menu: New game starts nothing and puts nothing away (\"" + changedWords + "\")");
    }

    // ---- today's page: the rule's buttons where Play was ----

    private static bool Shown(Control control) => control.IsVisibleInTree();

    private bool FootFits(MainMenu menu)
    {
        var screen = GetViewport().GetVisibleRect();
        var column = menu.GetNode<VBoxContainer>("Margin/Column");
        return column.GetCombinedMinimumSize().X + 64 <= screen.Size.X && column.GetCombinedMinimumSize().Y + 40 <= screen.Size.Y && menu.Foot.GetChildren().OfType<Control>().Where(Shown).All(c => screen.Encloses(c.GetGlobalRect()));
    }

    private async Task TestStartOnThePage()
    {
        var layout = SceneLayout("start-scene");
        TeamMaker.Create(layout, _rules, "Jets", "", "Wisp", "#65b9b0", "", Now);
        var folder = layout.TeamFolder("jets");

        // A launch that names a room (a room shortcut with --menu) on a machine whose one team has not played: a first game.
        var menu = await OpenMenu("--room=test_room");
        Check(menu.Model.TeamId == "jets" && menu.Model.RoomId == "test_room" && menu.Model.Mode == null && menu.NewGameButton.Disabled && menu.HintLabel.Text == "Choose a mode.",
            "a launch that names a room opens the menu on it with the one team chosen; the team has not played, so the mode is asked: " + menu.HintLabel.Text);
        Check(menu.Foot.GetChildren().Select(c => c.Name.ToString()).SequenceEqual(new[] { "Speech", "GoBack", "Continue", "BringGame", "NewGame" }) && menu.FindChild("Play", true, false) == null &&
              menu.Foot.GetParent() == menu.GetNode<VBoxContainer>("Margin/Column") && menu.Foot.GetIndex() == menu.Foot.GetParent().GetChildCount() - 1,
            "on today's page the rule's buttons stand where Play was, at the foot: \"Go back\", Continue, \"Bring my game here\", and New game last; there is no Play: " + string.Join(", ", menu.Foot.GetChildren().Select(c => c.Name.ToString())));
        await Click(menu.ModeButtons["challenge"]);
        await Click(menu.DifficultyButtons["standard"]);
        Check(Shown(menu.NewGameButton) && !menu.NewGameButton.Disabled && menu.NewGameButton.Text == "New game" && !Shown(menu.ContinueButton) && !Shown(menu.BringButton) && !Shown(menu.GoBackButton) &&
              menu.HintLabel.Text == "Jets on Test room, Challenge (Standard)" && menu.SavesLine.Text == "No Challenge island here yet: New game makes one." && FootFits(menu),
            "no game: New game alone (no Continue without its save), and the page says what it would start: " + menu.HintLabel.Text + " | " + menu.SavesLine.Text);
        await Click(menu.NewGameButton);
        var first = await WaitForRoom();
        if (first is not { } game) return;
        var hurt = game.Host.ApplyPlayerDamage(50, PlayerDamageSource.Critter);
        Check(!menu.Model.Asking && NothingPutAway(layout) && game.Host.SaveTeamIdentity == new SaveTeam("jets", "challenge", "standard") && hurt is { After: 50 } && game.Host.Vitals.Health == 50,
            $"a real click on New game starts a first game at once, nothing asked ({game.Host.SaveTeamIdentity}); it is played: health {game.Host.Vitals.Health}");
        await CloseGame();

        // Continue: the game as it was left.
        var again = await OpenMenu();
        Check(Shown(again.ContinueButton) && !again.ContinueButton.Disabled && again.ContinueLine.Text == "Jets on Test room, Challenge (Standard)" && again.ContinueButton.HasFocus() &&
              Shown(again.NewGameButton) && !again.NewGameButton.Disabled && !Shown(again.BringButton) && !Shown(again.GoBackButton) && FootFits(again) &&
              again.Foot.GetChildren().OfType<Control>().Where(Shown).Last() == again.NewGameButton && again.SavesLine.Text.Contains("Continue opens it", StringComparison.Ordinal),
            $"a game in this mode: the menu opens on it with Continue (focused) and New game: \"Continue: {again.ContinueLine.Text}\" | {again.SavesLine.Text}");
        // What a real boot leaves is no Creative game: this team has only ever played Challenge, so in Creative, on any island,
        // New game stands alone and would ask nothing (no "Bring my game here" for a game nobody played).
        var leftInCreative = Hashes(layout.ModeFolder("jets", "creative")).Keys.ToList();
        await Click(again.ModeButtons["creative"]);
        var creativeHere = again.Model.Offer;
        var creativeLine = again.SavesLine.Text;
        var newGameAlone = Shown(again.NewGameButton) && !again.NewGameButton.Disabled && !Shown(again.BringButton) && !Shown(again.ContinueButton) && again.HintLabel.Text == "Jets on Test room, Creative";
        await Click(again.IslandButtons["plain_isle"]);
        Check(creativeHere.Case == StartCase.NoGame && !creativeHere.Asks && newGameAlone && creativeLine == "No Creative island here yet: New game makes one." &&
              again.Model.RoomId == "plain_isle" && again.Model.Offer.Case == StartCase.NoGame && !Shown(again.BringButton) && !StartRule.HasGame(layout, "jets", "creative") && NothingPutAway(layout),
            $"after a real Challenge game the team has no Creative game, on this island or another: New game alone, nothing to ask ({creativeHere.Case}, {again.Model.Offer.Case}; the boot left in the Creative folder: {string.Join(", ", leftInCreative)}): {creativeLine}");
        await Click(again.IslandButtons["test_room"]);
        await Click(again.ModeButtons["challenge"]);
        // Continue's two lines are never cut: the word, and under it the game it opens, even with long names (a 10x garage island, Challenge at Standard).
        var word = again.ContinueButton.FindChild("ContinueWord", true, false) as Label;
        float Width(Label label, string text) => label.GetThemeFont("font").GetStringSize(text, HorizontalAlignment.Left, -1, label.GetThemeFontSize("font_size")).X;
        var inside = again.ContinueButton.GetGlobalRect();
        Check(word != null && word.Text == "Continue" && Width(word, word.Text) <= word.Size.X + 0.5f && Width(again.ContinueLine, again.ContinueLine.Text) <= again.ContinueLine.Size.X + 0.5f &&
              Width(again.ContinueLine, "Mira Quinn on Landscape garage nominal, Challenge (Standard)") <= again.ContinueLine.Size.X + 0.5f &&
              inside.Encloses(word.GetGlobalRect()) && inside.Encloses(again.ContinueLine.GetGlobalRect()) && word.GetGlobalRect().End.Y <= again.ContinueLine.GetGlobalRect().Position.Y + 3,
            $"Continue is two lines inside its button and neither is cut: \"{word?.Text}\" ({word?.GetGlobalRect()}) over \"{again.ContinueLine.Text}\" ({again.ContinueLine.GetGlobalRect()}) in {inside}; a long name needs {Width(again.ContinueLine, "Mira Quinn on Landscape garage nominal, Challenge (Standard)")} of {again.ContinueLine.Size.X}");
        await Click(again.ContinueButton);
        var second = await WaitForRoom();
        if (second is not { } left) return;
        Check(left.Host.Vitals.Health == 50 && left.Host.SaveTeamIdentity == new SaveTeam("jets", "challenge", "standard") && NothingPutAway(layout), $"Continue opens the game as it was left: health {left.Host.Vitals.Health}");
        await CloseGame();

        // New game over the game: asks once ("Go back" has the focus), and pressed again puts it away and starts a first game.
        var third = await OpenMenu();
        var before = Hashes(folder);
        var theGame = Hashes(layout.ModeFolder("jets", "challenge"));
        var guard = third.ConfirmGuardMs;
        third.ConfirmGuardMs = 600_000;
        var savedOn = MenuModel.Day(NewestWrite(layout, "jets", "challenge"));
        await Click(third.NewGameButton);
        Check(third.Model.Asking && Shown(third.GoBackButton) && third.GoBackButton.HasFocus() && third.GoBackButton.Text == "Go back" && !Shown(third.ContinueButton) && Shown(third.NewGameButton) && !third.NewGameButton.Disabled &&
              third.HintLabel.Text == third.Model.Question && third.HintLabel.Text == $"New game? Your Challenge game from {savedOn} is put away, not deleted." && FootFits(third) &&
              _boot!.World == null && Same(before, Hashes(folder)) && NothingPutAway(layout),
            "New game over a game asks once: the question stands by the buttons, \"Go back\" has the focus, and nothing has moved: " + third.HintLabel.Text);
        Check(guard >= 600 && third.NewGameButton.Text == "Yes, new game" && third.HintLabel.GetThemeFontSize("font_size") >= 18 && third.HintLabel.GetGlobalRect().Size.Y > 0 && GetViewport().GetVisibleRect().Encloses(third.HintLabel.GetGlobalRect()),
            $"while the question is up the button that answers it says so (\"{third.NewGameButton.Text}\"), the question is in a child's size ({third.HintLabel.GetThemeFontSize("font_size")}) and on the screen, and a double click's guard outlasts the system's half second ({guard} ms)");
        await Click(third.NewGameButton);
        Check(third.Model.Asking && _boot!.World == null && Same(before, Hashes(folder)) && NothingPutAway(layout), "a second click straight after the first (a double click) is not the answer: nothing moves");
        Check(third.DifficultyButtons.Values.All(Shown) && third.DifficultyButtons["standard"].ButtonPressed && third.Model.Difficulty == "standard",
            "while the question is up the difficulties are open for the new game, the old game's own chosen first");
        third._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Frames(2);
        Check(!third.Model.Asking && !Shown(third.GoBackButton) && Shown(third.ContinueButton) && third.ContinueButton.HasFocus() && third.DifficultyButtons.Values.Count(Shown) == 1 && Same(before, Hashes(folder)),
            "Esc goes back: the question is gone, Continue is back with the focus, and the save keeps its difficulty");
        await Click(third.NewGameButton);
        await Click(third.GoBackButton);
        Check(!third.Model.Asking && Shown(third.ContinueButton) && third.HintLabel.Text == "Jets on Test room, Challenge (Standard)" && third.NewGameButton.Text == "New game" && NothingPutAway(layout), "and so does a click on \"Go back\"");
        // The question is about one game: another mode, or a team's card, takes it down on the page too.
        await Click(third.NewGameButton);
        var questionUp = third.Model.Asking && Shown(third.GoBackButton);
        await Click(third.ModeButtons["creative"]);
        var modeTookItDown = !third.Model.Asking && !Shown(third.GoBackButton) && third.NewGameButton.Text == "New game" && third.HintLabel.Text == "Jets on Test room, Creative";
        await Click(third.ModeButtons["challenge"]);
        await Click(third.NewGameButton);
        var upAgain = third.Model.Asking && Shown(third.GoBackButton);
        await Click(third.TeamButtons["jets"]);
        Check(questionUp && modeTookItDown && upAgain && !third.Model.Asking && !Shown(third.GoBackButton) && Shown(third.ContinueButton) && _boot!.World == null && Same(before, Hashes(folder)) && NothingPutAway(layout),
            $"another mode, or a team's card, takes the question down on the page, and nothing has moved (up {questionUp}, the mode {modeTookItDown}, up again {upAgain}, the team's card {!third.Model.Asking})");
        await Click(third.NewGameButton);
        third.ConfirmGuardMs = 0;
        await Click(third.DifficultyButtons["brave"]);
        Check(third.Model.Asking && third.Model.Difficulty == "brave" && third.HintLabel.Text == third.Model.Question, "a difficulty chosen while the question is up is the new game's, and the question stays");
        // A click the system counts as the second of a double click is never the answer, however late it lands (no guard here).
        await Click(third.NewGameButton, secondOfDouble: true);
        Check(third.Model.Asking && _boot!.World == null && Same(before, Hashes(folder)) && NothingPutAway(layout), "the second click of a double click, as the system marks one, is not the answer even with no guard: nothing moves");
        await Click(third.NewGameButton);
        var fresh = await WaitForRoom();
        if (fresh is not { } started) return;
        var away = GameSetAside.List(layout, "jets");
        Check(started.Host.Vitals.Health == 100 && started.Host.SaveTeamIdentity == new SaveTeam("jets", "challenge", "brave") && started.World.Room.RoomId == "test_room",
            $"New game pressed again starts a first game: full health ({started.Host.Vitals.Health}), in the old game's mode, at the difficulty chosen ({started.Host.SaveTeamIdentity})");
        Check(away.Count == 1 && away[0].Ledger is { TeamId: "jets", Mode: "challenge" } && GameSetAside.Problems(away[0].Folder).Count == 0 && Same(theGame, Hashes(Path.Combine(away[0].Folder, "modes", "challenge"))) && theGame.Count >= 2,
            "and the old game is put away whole, byte for byte, in a folder the contract accepts: " + string.Join("; ", away.Count == 1 ? GameSetAside.Problems(away[0].Folder) : new[] { away.Count + " folders" }));
        await CloseGame();

        // The buttons wait on the team's lease: after "Back to the menu" the old window still writes for a moment.
        var fourth = await OpenMenu();
        using (var room = PlayLease.TryPlay(layout, "jets")!)
        {
            fourth.Refresh();
            await Frames(2);
            var waiting = Shown(fourth.ContinueButton) && fourth.ContinueButton.Disabled && fourth.NewGameButton.Disabled && fourth.HintLabel.Text == MenuModel.StillClosingWords;
            await Click(fourth.ContinueButton);
            Press(fourth.ContinueButton);
            Press(fourth.NewGameButton);
            await Frames(2);
            Check(waiting && _boot!.World == null && !fourth.Model.Asking && fourth.ContinueButton.Disabled && GameSetAside.List(layout, "jets").Count == 1,
                "while the team's room is still leaving, Continue and New game are shut and say why, and a press starts nothing: " + fourth.HintLabel.Text);
        }
        await Frames(30);
        Check(!fourth.ContinueButton.Disabled && !fourth.NewGameButton.Disabled && fourth.HintLabel.Text == "Jets on Test room, Challenge (Brave)", "as soon as the room has let go, the buttons open by themselves: " + fourth.HintLabel.Text);
        fourth.Model.LeaseWait = TimeSpan.Zero;
        fourth.ConfirmGuardMs = 0;
        using (var other = PlayLease.TryPlay(layout, "jets")!)
        {
            fourth.Refresh();
            await Frames(12);
            Check(!fourth.ContinueButton.Disabled && !fourth.NewGameButton.Disabled && fourth.HintLabel.Text == MenuModel.OpenElsewhereWords,
                "once the wait is over a game still open is said so, for as long as it is: " + fourth.HintLabel.Text);
        }
        await Frames(30);
        Check(fourth.HintLabel.Text == "Jets on Test room, Challenge (Brave)" && !fourth.ContinueButton.Disabled, "once that game is closed the page stops saying it is open, by itself: " + fourth.HintLabel.Text);
        using (var other = PlayLease.TryPlay(layout, "jets")!)
        {
            fourth.Refresh();
            var open = !fourth.ContinueButton.Disabled && !fourth.NewGameButton.Disabled && fourth.HintLabel.Text == MenuModel.OpenElsewhereWords;
            await Click(fourth.NewGameButton);
            await Click(fourth.NewGameButton);
            Check(open && fourth.Model.Asking && fourth.NoticeLabel.Visible && fourth.NoticeLabel.Text == PlayLease.StillOpen && _boot!.World == null && GameSetAside.List(layout, "jets").Count == 1,
                "once the wait is over a game still open in another window is said so; New game asks, then is refused with the words, and nothing moves: " + fourth.NoticeLabel.Text);
        }
        await Frames(30);
        Check(!fourth.NoticeLabel.Visible && fourth.Model.Asking && fourth.HintLabel.Text == fourth.Model.Question && GameSetAside.List(layout, "jets").Count == 1,
            "and once it is closed the refusal's words go by themselves; the question stays, for New game to go on");

        // The other cases on the page: a game of this mode on another island, and an island rebuilt since its save.
        fourth.Model.SelectIsland("plain_isle");
        fourth.Refresh();
        Check(Shown(fourth.BringButton) && fourth.BringButton.Text == "Bring my game here" && !Shown(fourth.ContinueButton) && Shown(fourth.NewGameButton) && !Shown(fourth.GoBackButton) && FootFits(fourth) &&
              fourth.SavesLine.Text == "No Challenge island here yet: bring your game here, or start a new one.",
            "a game of this mode on another island: \"Bring my game here\" and New game, no Continue: " + fourth.SavesLine.Text);
        var plain = fourth.Model.Island!;
        Write(layout.IslandSave("jets", "challenge", "plain_isle", plain.ManifestPrefix == "0123456789abcdef" ? "fedcba9876543210" : "0123456789abcdef"), "{}");
        fourth.Refresh();
        Check(fourth.Model.Offer.Case == StartCase.Rebuilt && Shown(fourth.NewGameButton) && !fourth.NewGameButton.Disabled && !Shown(fourth.ContinueButton) && !Shown(fourth.BringButton) && fourth.HintLabel.Text == MenuModel.RebuiltWords &&
              fourth.SavesLine.Text == "Your Challenge island here is from before this room was rebuilt: New game makes a new one.",
            "an island rebuilt since its save: New game alone, and the page says so in both its lines: " + fourth.HintLabel.Text + " | " + fourth.SavesLine.Text);

        // A game New game put away is said where there is none in play (so also when the room then did not open): put away as New game does it.
        GameSetAside.PutAway(layout, "jets", "challenge", DateTime.UtcNow);
        fourth.Model.Reload();
        fourth.Model.SelectIsland("test_room");
        fourth.Refresh();
        Check(fourth.Model.Offer.Case == StartCase.NoGame && fourth.SavesLine.Text == "Your last Challenge game is put away: New game makes a new one." && Shown(fourth.NewGameButton) && !fourth.NewGameButton.Disabled &&
              !Shown(fourth.ContinueButton) && !Shown(fourth.BringButton) && fourth.HintLabel.Text.StartsWith("Jets on Test room, Challenge (", StringComparison.Ordinal),
            "with this mode's game put away and none in play, the page says where it went: " + fourth.SavesLine.Text + " | " + fourth.HintLabel.Text);
        await CloseGame();
    }

    // ---- a room shortcut: the launch's own command line, a room in a folder outside the island roots ----

    private async Task TestStartFromAShortcut()
    {
        var layout = SceneLayout("start-shortcut");
        TeamMaker.Create(layout, _rules, "Nest", "", "Wisp", "#65b9b0", "", Now);
        var own = RoomOfItsOwn("start-shortcut-room", "Far room");
        var listed = IslandCatalog.Scan().First(i => i.RoomId == "test_room");
        // What run-room.ps1 -Room <a folder> -Mode challenge -Difficulty standard passes once a room shortcut opens the menu
        // (the launcher's diff in the packet's report): the menu, a room's folder, a mode, a difficulty, and no team.
        var line = new[] { "--no-playtest-record", "--menu", "--room=" + own, "--mode=challenge", "--difficulty=standard" };
        var menu = await OpenMenu(line);
        var island = menu.Model.Island;
        Check(MenuBoot.ShowsMenu(line, "Windows") && _boot!.World == null && menu.Model.TeamId == "nest" && island != null && island.Directory == own && island.RoomId == "test_room" &&
              island.ManifestPrefix != listed.ManifestPrefix && island.DisplayName == "Far room" && menu.Model.Mode == "challenge" && menu.Model.Difficulty == "standard" &&
              menu.IslandButtons.Keys.Order().SequenceEqual(new[] { "plain_isle", "test_room", "tutorial_isle" }) && menu.IslandButtons["test_room"].ButtonPressed &&
              (menu.IslandButtons["test_room"].FindChild("IslandName", true, false) as Label)?.Text == "Far room",
            $"a room shortcut's command line opens the menu on the launch's own room (its folder, though a listed island carries its id), with the mode and difficulty it names, and starts nothing: {menu.Model.ChoiceWords} in {island?.Directory}");
        Check(Shown(menu.NewGameButton) && !menu.NewGameButton.Disabled && !Shown(menu.ContinueButton) && !Shown(menu.BringButton) && menu.HintLabel.Text == "Nest on Far room, Challenge (Standard)" && FootFits(menu),
            "no game there yet: New game alone: " + menu.HintLabel.Text);
        await Click(menu.NewGameButton);
        var first = await WaitForRoom();
        if (first is not { } game) return;
        var prefix = game.World.Room.ManifestSha256[..16];
        var hurt = game.Host.ApplyPlayerDamage(50, PlayerDamageSource.Critter);
        Check(game.World.RoomDirectory == own && game.World.Room.RoomId == "test_room" && game.World.Room.DisplayName == "Far room" && prefix == island!.ManifestPrefix &&
              game.Host.SaveTeamIdentity == new SaveTeam("nest", "challenge", "standard") && hurt is { After: 50 } && NothingPutAway(layout),
            $"New game boots the launch's own room from its own folder, outside the island roots ({game.World.RoomDirectory}, {game.World.Room.DisplayName}, {game.Host.SaveTeamIdentity})");
        await CloseGame();
        Check(File.Exists(layout.IslandSave("nest", "challenge", "test_room", prefix)) && !File.Exists(layout.IslandSave("nest", "challenge", "test_room", listed.ManifestPrefix)) &&
              LastGame.Read(layout) is { TeamId: "nest", RoomId: "test_room", Mode: "challenge" },
            "its save is named by its own manifest, not the listed island's, and the game is remembered");

        // The shortcut again: the game waits behind Continue, and nothing opens by itself.
        var again = await OpenMenu(line);
        Check(_boot!.World == null && again.Model.Island!.Directory == own && Shown(again.ContinueButton) && !again.ContinueButton.Disabled && again.ContinueButton.HasFocus() &&
              again.ContinueLine.Text == "Nest on Far room, Challenge (Standard)" && Shown(again.NewGameButton) && !again.NewGameButton.Disabled && !again.Model.Asking && NothingPutAway(layout),
            "the shortcut again never loads the old game unasked: it waits behind Continue, with New game beside it: " + again.ContinueLine.Text);
        await Click(again.ContinueButton);
        var second = await WaitForRoom();
        if (second is not { } left) return;
        Check(left.World.RoomDirectory == own && left.Host.Vitals.Health == 50 && left.Host.SaveTeamIdentity == new SaveTeam("nest", "challenge", "standard"), $"Continue opens that game as it was left, in the launch's room: health {left.Host.Vitals.Health}");
        await CloseGame();

        // The listed island of that id is another island. Its own page (the menu opened with no room named) has no Continue for
        // a save that is not its own; and a game played on it is, from the shortcut's room, a game on another island.
        var byId = await OpenMenu();
        Check(byId.Model.RoomId == "test_room" && byId.Model.Island!.Directory != own && byId.Model.Island.ManifestPrefix == listed.ManifestPrefix && !Shown(byId.ContinueButton) && _boot!.World == null,
            "the listed island of the same id has no save of its own: no Continue there (no Continue without its save)");
        await CloseGame();
        WriteGame(layout, "nest", "creative", listed);
        var creative = await OpenMenu("--menu", "--room=" + own, "--mode=creative");
        Check(creative.Model.Island!.Directory == own && creative.Model.Mode == "creative" && creative.Model.Offer.Case == StartCase.GameElsewhere && Shown(creative.BringButton) && Shown(creative.NewGameButton) &&
              !Shown(creative.ContinueButton) && creative.HintLabel.Text != MenuModel.RebuiltWords && creative.SavesLine.Text == "No Creative island here yet: bring your game here, or start a new one.",
            $"a game on the listed island of the same id is a game on another island, not this room rebuilt ({creative.Model.Offer.Case}): {creative.SavesLine.Text}");
        await CloseGame();
    }
}
