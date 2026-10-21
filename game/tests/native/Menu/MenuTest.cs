using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// The opening menu (docs/runs/RUN-2-MECHANICS.md, packet M7; docs/game/MECHANICS.md section 3 and decisions 1, 3, 4, 44, 49, 66):
/// the islands it lists (the tutorial first, a thumbnail from the shape data), the teams it shows and makes (the New team panel
/// writes the team file), the mode and difficulty it offers through the island rules (Live shown, refused), "Copy my Creative island into
/// Challenge" once per island per team with the carried builds written down, Continue, and the headless walk from the menu to the
/// fixture room: a team written, the choices driven as a player drives them, the room booted with that team, island, mode and
/// difficulty through the boot --team --mode --difficulty take. Headless, --fixed-fps 60. Run: res://tests/native/Menu/native_menu.tscn.
/// Its files go under user://tests/menu (the runner's user folder).
/// </summary>
public partial class MenuTest : Node
{
    private const string Root = "user://tests/menu";
    private int _checks;
    private int _failures;
    private IslandRules? _rules;
    private TeamLayout _layout = null!;

    public override async void _Ready()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        try
        {
            var root = ProjectSettings.GlobalizePath(Root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            RoomHud.ProfilePath = Root + "/profile/avatar_profile_v1.cfg";
            _rules = IslandRules.Load(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion);
            _layout = new TeamLayout(Path.Combine(root, "model"));
            TestCatalog();
            TestTeamMaker();
            TestModel();
            TestCopy();
            TestLastGame();
            TestBootChoices();
            await TestMenuToRoom();
            if (OS.GetCmdlineUserArgs().Contains("--broken-room")) await TestBrokenIsland();
            await TestCopyThroughTheMenu();
            TestLinkWords();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GD.Print($"NATIVE_MENU: {_checks - _failures}/{_checks} checks passed; the islands listed (the tutorial first, the player's before the shipped stand-ins, names cleaned, an island that cannot be saved left out), a team made in the New team panel and written, the mode and difficulty offered through the island rules (Live shown and refused, Gentle suggested on a first Challenge, a save's difficulty kept), \"Copy my Creative island into Challenge\" once per island per team with its builds written down and the Creative original untouched, Continue, and the walk from the menu to the fixture room with the team, island, mode and difficulty reaching its boot; not the look of the cards");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.Print("FAIL the suite threw: " + error);
            GetTree().Quit(1);
        }
        finally
        {
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
            IslandCatalog.RootsOverride = null;
            TeamBoot.UserRootOverride = null;
        }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) { GD.Print("PASS: " + label); return; }
        _failures++;
        GD.Print("FAIL " + label);
    }

    private Task Frames(int count)
    {
        var source = new TaskCompletionSource();
        var remaining = count;
        void Step()
        {
            if (--remaining > 0) return;
            GetTree().PhysicsFrame -= Step;
            source.SetResult();
        }
        GetTree().PhysicsFrame += Step;
        return source.Task;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
    }

    private static string Folder(string name)
    {
        var path = ProjectSettings.GlobalizePath($"{Root}/{name}");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        return path;
    }

    private static readonly DateTime Now = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    // ---- the islands the synthetic user folder holds (manifests the menu reads: id, name, tutorial, shape) ----

    private const string Shape = """
        {"bounds":{"min_m":[-5,0,-4],"max_m":[5,3,4]},
         "shell":{"parts":[
           {"id":"shell:floor","role":"floor","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[5,0,-4],[5,0,4],[-5,0,4]],"thickness_m":0.1}},
           {"id":"shell:wall_n","role":"wall","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[5,0,-4],[5,3,-4],[-5,3,-4]],"thickness_m":0.1}}],
          "openings":[]},
         "objects":[{"id":"obj:a","position_m":[0,0,0],"size_m":[1,1,1],"yaw_deg":30,"colours":[{"hex":"#aa5533"}]},
                    {"id":"obj:b","position_m":[2,0,1],"size_m":[0.5,0.5,0.5],"yaw_deg":0}]}
        """;

    private static string Manifest(string roomId, string display, bool tutorial, bool shape)
    {
        var extensions = new List<string>();
        if (tutorial) extensions.Add("\"x_island_tutorial\":true");
        if (shape) extensions.Add("\"x_landscape_source\":" + Shape);
        return "{\"schema\":\"enfractal.room\",\"version\":1,\"room_id\":" + System.Text.Json.JsonSerializer.Serialize(roomId) + ",\"display_name\":" + System.Text.Json.JsonSerializer.Serialize(display) +
               (extensions.Count > 0 ? ",\"extensions\":{" + string.Join(",", extensions) + "}" : "") + "}";
    }

    private string _islandRoot = "";

    private string IslandsFolder()
    {
        if (_islandRoot.Length > 0) return _islandRoot;
        var root = Folder("rooms");
        Write(Path.Combine(root, "plain_isle", "room.json"), Manifest("plain_isle", "Plain   Isle", tutorial: false, shape: false));
        Write(Path.Combine(root, "zz_tutorial", "room.json"), Manifest("tutorial_isle", "Tutorial\u0007 Isle\n", tutorial: true, shape: true));
        Write(Path.Combine(root, "bad_id", "room.json"), Manifest("Bad Id!", "Bad", tutorial: false, shape: false));
        Write(Path.Combine(root, "not_json", "room.json"), "this is not a manifest");
        Write(Path.Combine(root, "no_manifest", "readme.txt"), "no room here");
        // The shipped test room is "Test room"; a user room with its id would win (the player's first).
        return _islandRoot = ProjectSettings.LocalizePath(root);
    }

    private void TestCatalog()
    {
        var islands = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" });
        Check(islands.Select(i => i.RoomId).SequenceEqual(new[] { "tutorial_isle", "plain_isle", "test_room" }),
            "the islands are listed tutorial first, then the player's, then the shipped stand-ins; a room that cannot be saved (a bad id, no manifest, not JSON) is left out: " + string.Join(",", islands.Select(i => i.RoomId)));
        var tutorial = islands[0];
        Check(tutorial.Tutorial && tutorial.HasShape && tutorial.DisplayName == "Tutorial Isle" && islands[1].DisplayName == "Plain Isle" && !islands[1].Tutorial && !islands[1].HasShape && islands[2].Shipped,
            $"a name is cleaned the way the game cleans display text (\"{tutorial.DisplayName}\"), the tutorial flag and the shape data are read");
        var bytes = File.ReadAllBytes(Path.Combine(ProjectSettings.GlobalizePath(IslandsFolder()), "zz_tutorial", "room.json"));
        Check(tutorial.ManifestPrefix == GarageGame.Native.Room.RoomData.Sha256Hex(bytes)[..16] && TeamLayout.ValidManifestPrefix(tutorial.ManifestPrefix),
            "the island's saves are named by the manifest's hash, as the game names them (CommandHost.SavePathFor)");
        var shape = tutorial.ReadShape();
        Check(shape != null && shape.Floors.Count == 1 && shape.Walls.Count == 1 && shape.Boxes.Count == 2 && islands[1].ReadShape() == null,
            "the thumbnail's shape data is the manifest's x_landscape_source (a floor, a wall, two boxes); a room without it has none");
        // The player's room with a shipped room's id wins.
        var duplicate = Folder("rooms2");
        Write(Path.Combine(duplicate, "mine", "room.json"), Manifest("test_room", "My test room", tutorial: false, shape: false));
        var both = IslandCatalog.Scan(new[] { ProjectSettings.LocalizePath(duplicate), "res://rooms" });
        Check(both.Count == 1 && both[0].DisplayName == "My test room" && !both[0].Shipped, "a room id found in both the player's folder and the shipped rooms is the player's, as RoomWorld.ResolveRoom opens it");
    }

    // ---- teams: made in the New team panel, written as team files ----

    private void TestTeamMaker()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "mira" };
        Check(TeamMaker.IdFrom("Mira Quinn", new HashSet<string>()) == "mira-quinn" && TeamMaker.IdFrom("Mira", taken) == "mira-2" && TeamMaker.IdFrom("Jürgen", new HashSet<string>()) == "jurgen" &&
              TeamMaker.IdFrom("日本", new HashSet<string>()) == "team" && TeamMaker.IdFrom("  ", new HashSet<string>()) == "team" && TeamMaker.IdFrom("7 Wonders", new HashSet<string>()) == "team-7-wonders" &&
              TeamMaker.IdFrom("nul", new HashSet<string>()) == "team-2" && TeamLayout.ValidTeamId(TeamMaker.IdFrom(new string('x', 90), new HashSet<string>())),
            "a team id from the name typed: lowercase letters and digits, accents folded, taken ids numbered, a name with no letters \"team\", never a Windows device's name, never longer than a team id may be");

        var layout = new TeamLayout(Folder("makers"));
        var offered = TeamMaker.Offered(_rules);
        Check(offered.Count >= 3 && offered.All(k => k.Available) && TeamMaker.KnackName(_rules, "quick_feet") == "Quick feet",
            $"the island offers {offered.Count} knacks to choose among, by the pack's names");
        var mira = TeamMaker.Create(layout, _rules, "Mira Quinn", "stickbear", "Pip", "#a18cc3", "quick_feet", Now);
        var back = new TeamStore(layout.TeamFile("mira-quinn"), "mira-quinn").Load();
        Check(back != null && back.Serialize().SequenceEqual(mira.Serialize()) && back.DisplayName == "Mira Quinn" && back.Costume == "stickbear" && back.GubbleName == "Pip" && back.GubbleColour == "#a18cc3" &&
              back.Knacks.Single() == new ChosenKnack(1, "quick_feet", 1) && back.Trust == 0 && back.SlotsOpen == 1,
            "a new team is written as its team file: the person, the costume, the Gubble's name and colour, the knack chosen at rank 1, Trust at base, one slot open");
        var surprised = TeamMaker.Create(layout, _rules, "Theo", "", "", "#65b9b0", null, Now, new Random(7));
        var wanted = offered[new Random(7).Next(offered.Count)].Knack;
        Check(surprised.Knacks.Single().Knack == wanted && surprised.Costume == "prototype" && surprised.GubbleName == CompanionAvatar.DefaultName,
            $"\"surprise me\" gives a knack the island offers ({wanted}), a missing costume is the plain figure and a missing Gubble name is the Gubble");
        var twin = TeamMaker.Create(layout, _rules, "Mira Quinn", "", "Wisp", "#65b9b0", "", Now);
        Check(twin.TeamId == "mira-quinn-2" && twin.Knacks.Count == 0 && File.Exists(layout.TeamFile("mira-quinn")) && File.ReadAllBytes(layout.TeamFile("mira-quinn")).SequenceEqual(mira.Serialize()),
            "a second team of the same name is a team of its own (mira-quinn-2) and the first is not touched; \"no knack\" is a team with none");
        string Refused(Action make) { try { make(); return ""; } catch (TeamFileException error) { return error.Message; } }
        Check(Refused(() => TeamMaker.Create(layout, _rules, "   ", "", "", "#65b9b0", "", Now)) == "Give the team a name first." &&
              Refused(() => TeamMaker.Create(layout, _rules, "Ok", "", "", "#65b9b0", "no_such_knack", Now)).Contains("no knack by that name", StringComparison.Ordinal) &&
              Refused(() => TeamMaker.Create(layout, _rules, "Ok", "Not A Token!", "", "#65b9b0", "", Now)).Contains("costume", StringComparison.Ordinal) &&
              !Directory.Exists(Path.Combine(layout.TeamsRoot, "ok")),
            "no name, a knack the island does not offer and a costume that is not a token are each refused, and no team folder is left behind");
        var long40 = TeamMaker.Create(layout, _rules, new string('M', 60), "", "", "#65b9b0", "", Now);
        Check(long40.DisplayName.Length == TeamFile.MaxNameLength, "a name over forty characters is cut to forty, as every display name is");
        // A free swap in the menu: the knack changes, the team file with it, and the rank earned by the one set aside is kept.
        var swapped = TeamMaker.SwapKnack(layout, _rules, "mira-quinn", 1, "cat_feet");
        Check(swapped.Knacks.Single().Knack == "cat_feet" && TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("mira-quinn"))).Knacks.Single().Knack == "cat_feet" &&
              Refused(() => TeamMaker.SwapKnack(layout, _rules, "mira-quinn", 2, "quick_feet")).Contains("Only the first knack slot", StringComparison.Ordinal),
            "swapping a knack in the menu is free and written to the team file; a slot that is not open yet stays closed");
    }

    // ---- the model: what it offers and how it chooses ----

    private static string Vitals(string team, string mode, int hearts, int bands, DateTime at) =>
        Encoding.UTF8.GetString(LocalPlayerVitalsStore.SerializeMode(team, mode, at, PlayerVitals.Of(100, 100, 0.4f, 0.4f, Array.Empty<string>(), 0, new PlayerFractals(hearts * 5 + bands * 5, 0, hearts, bands), 8)));

    private MenuModel NewModel(TeamLayout layout, IReadOnlyList<IslandEntry>? islands = null, IslandRules? rules = null, bool noRules = false) =>
        new(layout, noRules ? null : rules ?? _rules, islands ?? IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" })) { Clock = () => Now };

    private void TestModel()
    {
        var layout = new TeamLayout(Folder("model"));
        TeamMaker.Create(layout, _rules, "Mira", "stickbear", "Pip", "#a18cc3", "", Now);
        Write(layout.VitalsFile("mira", "challenge"), Vitals("mira", "challenge", 2, 1, Now));
        Write(layout.ExpertiseFile("mira"), Encoding.UTF8.GetString(LocalGlowProgressStore.Serialize(GlowProgress.Restore(20, new[] { "lantern", "torch", "bioluminesce", "constellation", "skylight" }, Array.Empty<string>()), "mira")));
        var islands = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" });
        var tutorial = islands.First(i => i.RoomId == "tutorial_isle");
        var plain = islands.First(i => i.RoomId == "plain_isle");
        Write(layout.IslandSave("mira", "creative", "plain_isle", plain.ManifestPrefix), "{}");
        Write(layout.IslandSave("mira", "creative", "tutorial_isle", tutorial.ManifestPrefix), "{}");
        Write(layout.IslandSave("mira", "creative", "test_room", "0123456789abcdef"), "{}");   // a save for another manifest of the room: not this island's
        TeamMaker.Create(layout, _rules, "Theo", "", "Wisp", "#65b9b0", "", Now);

        var model = NewModel(layout);
        var card = model.Teams.First(t => t.TeamId == "mira");
        Check(model.Teams.Count == 2 && card.Readable && card.File!.DisplayName == "Mira" && card.IslandsVisited == 3 && card.ExpertiseCount == 5 && card.LastMode == "challenge" &&
              card.Hearts == 7 && Math.Abs(card.StaminaM - 0.8) < 1e-6 && card.Pips == 9,
            $"a team card shows the person, the islands visited ({card.IslandsVisited}), the Gubble's expertise ({card.ExpertiseCount}) and the numbers it plays with, from its last mode's vitals under the island rules: {card.Hearts} hearts, {card.StaminaM} m, {card.Pips} pips");
        var fresh = model.Teams.First(t => t.TeamId == "theo");
        Check(fresh.Hearts == 5 && Math.Abs(fresh.StaminaM - 0.4) < 1e-6 && fresh.Pips == 8 && fresh.IslandsVisited == 0 && fresh.ExpertiseCount == 0 && fresh.LastPlayedUtc == null,
            $"a team that has not played shows the starting numbers ({fresh.Hearts}, {fresh.StaminaM} m, {fresh.Pips})");
        Write(layout.TeamFile("broken"), "this is not a team file");
        model.Reload();
        var broken = model.Teams.First(t => t.TeamId == "broken");
        Check(!broken.Readable && broken.Problem.Length > 0 && !model.SelectTeam("broken") && model.Notice == broken.Problem && model.TeamId == null && File.ReadAllText(layout.TeamFile("broken")) == "this is not a team file",
            "a team file that does not read is listed, refused with its reason, and kept as it was");

        Check(model.WhyNotReady == "Choose your character first.", "nothing chosen: " + model.WhyNotReady);
        Check(model.SelectTeam("mira") && model.TeamId == "mira" && model.WhyNotReady == "Choose where to." && !model.SelectIsland("nowhere") && model.SelectIsland("plain_isle") && model.WhyNotReady == "Choose a mode.",
            "choosing a team, then an island, then a mode; an island that is not here is refused");
        Check(model.Saves.Single(s => s.Mode == "creative").Exists && !model.Saves.Single(s => s.Mode == "challenge").Exists && model.FixedDifficulty == null,
            "the island's saves for this team, by mode: Creative's there, Challenge's not (a save of another manifest is not this island's)");

        // Mode and difficulty are the island rules' to offer.
        Check(model.Offers("creative") && model.Offers("challenge") && model.OfferedDifficulties.SequenceEqual(new[] { "gentle", "standard", "brave" }) && !model.Offers("live"),
            "the pack offers Creative, Challenge at Gentle, Standard and Brave, and not Live");
        Check(!model.ChooseMode("live") && model.Notice.StartsWith(CommandHost.LiveNotYet, StringComparison.Ordinal) && model.Mode == null,
            $"Live is shown and refused through ModeAvailable: \"{model.Notice}\"");
        Check(!model.ChooseMode("hard") && model.Mode == null && !model.ChooseMode("challenge", "hard") && model.Mode == null, "a mode or a difficulty the menu does not know is refused");
        Check(model.FirstChallenge && model.ChooseMode("challenge") && model.Difficulty == "gentle" && model.OfferedDifficulties.Contains("gentle"),
            "no Challenge save yet on any island: Gentle is the one offered");
        // The team's first Challenge save suggests Gentle.
        var newcomer = NewModel(layout);
        newcomer.SelectTeam("theo");
        newcomer.SelectIsland("plain_isle");
        Check(newcomer.FirstChallenge && newcomer.ChooseMode("challenge") && newcomer.Difficulty == "gentle" && newcomer.ChooseMode("challenge", "brave") && newcomer.Difficulty == "brave",
            "on a team's first Challenge save Gentle is the one offered, and Brave can be chosen instead");
        Check(newcomer.ChooseMode("creative") && newcomer.Difficulty == null && newcomer.Mode == "creative", "Creative has no difficulty");
        // A Challenge save keeps its difficulty; the menu shows it, never changes it.
        Write(layout.IslandSave("mira", "challenge", "plain_isle", plain.ManifestPrefix), "{\"team\":{\"team_id\":\"mira\",\"mode\":\"challenge\",\"difficulty\":\"brave\"}}");
        model.Reload();
        Check(model.FixedDifficulty == "brave" && model.ChooseMode("challenge", "gentle") && model.Difficulty == "brave" && !model.FirstChallenge,
            "a Challenge save here plays at the difficulty it was made with (Brave), whatever the menu is asked, and the team's first-Challenge suggestion is spent");
        model.SelectIsland("tutorial_isle");
        Check(model.FixedDifficulty == null && model.Difficulty == "standard", "on an island with no Challenge save the offered difficulty is Standard once the first is spent (" + model.Difficulty + ")");

        // Without a modes block in the pack: today's Creative and Challenge at Standard, nothing else.
        var legacy = NewModel(layout, noRules: true);
        Check(legacy.Offers("creative") && legacy.Offers("challenge", "standard") && !legacy.Offers("challenge", "gentle") && !legacy.Offers("live"),
            "a pack with no modes block offers today's Creative and Challenge at Standard");
    }

    // ---- Copy my Creative island into Challenge ----

    private static string CreativeSave(string team, params string[] creations) => new JsonObject
    {
        ["schema"] = "enfractal.creation-world", ["version"] = 7, ["compiler_version"] = 1, ["revision"] = 4,
        ["instances"] = new JsonArray(creations.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["owner_id"] = "player:local", ["revision"] = 1 }).ToArray()),
        ["team"] = new JsonObject { ["team_id"] = team, ["mode"] = "creative", ["journal"] = new JsonArray(), ["discovered"] = new JsonObject() },
        ["nature"] = new JsonObject
        {
            ["format"] = 1, ["cells"] = "AAAA", ["names"] = new JsonArray("grass"), ["wallet"] = new JsonObject { ["wood"] = 3 },
            ["undo"] = new JsonArray(new JsonObject { ["revision"] = 3, ["base"] = 2, ["mode"] = "creative" }),
            ["undone"] = new JsonObject { ["at"] = 4, ["state_of"] = 2 },
            ["fire"] = new JsonObject { ["principal"] = "player", ["remaining_s"] = 5 },
            ["fractals"] = new JsonArray("fractal:a", "fractal:b"),
        },
    }.ToJsonString();

    private void TestCopy()
    {
        var layout = new TeamLayout(Folder("copy"));
        TeamMaker.Create(layout, _rules, "Mira", "", "", "#65b9b0", "", Now);
        TeamMaker.Create(layout, _rules, "Theo", "", "", "#65b9b0", "", Now);
        var island = IslandCatalog.Scan(new[] { IslandsFolder() }).First(i => i.RoomId == "tutorial_isle");
        var source = layout.IslandSave("mira", "creative", island.RoomId, island.ManifestPrefix);
        Write(source, CreativeSave("mira", "creation:2", "creation:1"));
        var original = File.ReadAllBytes(source);
        var model = NewModel(layout);
        model.SelectTeam("mira");
        model.SelectIsland("tutorial_isle");
        Check(!model.CopyOffered && model.ChooseMode("challenge") && model.CopyOffered && !model.CopyUsed && !model.CopyCreative && model.Difficulty == "gentle",
            "Challenge on an island with a Creative save and no Challenge one offers the copy (and Gentle: a first Challenge); in Creative it is not offered");
        Check(model.ChooseCopy(true) && model.CopyCreative && model.ChooseCopy(false) && !model.CopyCreative && model.ChooseCopy(true), "\"Start fresh\" and \"Copy my Creative island\" are the two answers");
        var launch = model.Play();
        var target = layout.IslandSave("mira", "challenge", island.RoomId, island.ManifestPrefix);
        Check(launch != null && launch.Mode == "challenge" && launch.Difficulty == "gentle" && launch.Arguments.SequenceEqual(new[] { "--team=mira", "--mode=challenge", "--difficulty=gentle" }) && File.Exists(target),
            "playing with the copy chosen makes the Challenge save, then launches with the team, mode and difficulty: " + (launch == null ? model.Notice : string.Join(" ", launch.Arguments)));
        var copy = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
        var nature = copy["nature"]!.AsObject();
        Check(copy["team"]!["mode"]!.GetValue<string>() == "challenge" && copy["team"]!["difficulty"]!.GetValue<string>() == "gentle" && copy["team"]!["team_id"]!.GetValue<string>() == "mira" &&
              copy["team"]!["journal"] != null && copy["team"]!["discovered"] != null && copy["instances"]!.AsArray().Count == 2 && nature["cells"]!.GetValue<string>() == "AAAA",
            "the copy is a Challenge save of this team at the difficulty chosen, and carries the creations, the ground, the journal and the map");
        Check(nature["undo"]!.AsArray().Count == 0 && nature["undone"] == null && nature["fire"] == null && nature["fractals"] == null && nature["wallet"]!.AsObject().Count == 0,
            "and strips what Creative alone has: the undo steps and the record of the last undo, a fire still burning, the Fractals picked up and the materials gathered");
        Check(File.ReadAllBytes(source).SequenceEqual(original), "the Creative original is untouched, byte for byte");
        var carried = CarriedBuilds.Read(layout, "mira");
        Check(carried.Count == 1 && carried["tutorial_isle"].Builds.SequenceEqual(new[] { "creation:1", "creation:2" }) && carried["tutorial_isle"].Difficulty == "gentle" && carried["tutorial_isle"].ManifestPrefix == island.ManifestPrefix &&
              CarriedBuilds.CarriedOn(layout, "mira", "tutorial_isle").Count == 2 && CarriedBuilds.CarriedOn(layout, "mira", "plain_isle").Count == 0 && CarriedBuilds.CarriedOn(layout, "theo", "tutorial_isle").Count == 0,
            "the builds carried over are written down (carried_builds.json) so that they count for no Challenge first, souvenir or achievement: " + string.Join(",", carried["tutorial_isle"].Builds));
        // Once per island per team.
        File.Delete(target);
        model.Reload();
        Check(!model.CopyOffered && model.CopyUsed && !model.ChooseCopy(true) && model.Notice.Contains("once already", StringComparison.Ordinal),
            "the copy is offered once per island per team: with the Challenge save gone it is still not offered again (\"" + model.Notice + "\")");
        string Copying() { try { CarriedBuilds.Copy(layout, "mira", island, "gentle", Now); return ""; } catch (MenuSaveException error) { return error.Message; } }
        Check(Copying().Contains("once already", StringComparison.Ordinal) && !File.Exists(target), "and the copy itself refuses a second time");
        var theo = NewModel(layout);
        Write(layout.IslandSave("theo", "creative", island.RoomId, island.ManifestPrefix), CreativeSave("theo", "creation:1"));
        theo.SelectTeam("theo");
        theo.SelectIsland("tutorial_isle");
        theo.ChooseMode("challenge", "brave");
        Check(theo.CopyOffered && !theo.CopyUsed, "another team's copy of the same island is its own: Theo may copy it once");
        // It never copies over a Challenge save, and never from a Creative save that does not read.
        var other = layout.IslandSave("theo", "challenge", island.RoomId, island.ManifestPrefix);
        Write(other, "{\"kept\":true}");
        string CopyingAs(string team) { try { CarriedBuilds.Copy(layout, team, island, "brave", Now); return ""; } catch (MenuSaveException error) { return error.Message; } }
        Check(CopyingAs("theo").Contains("never copied over", StringComparison.Ordinal) && File.ReadAllText(other) == "{\"kept\":true}" && CarriedBuilds.Read(layout, "theo").Count == 0,
            "a Challenge save is never copied over, and nothing is written down for a copy that was not made");
        File.Delete(other);
        Write(layout.IslandSave("theo", "creative", island.RoomId, island.ManifestPrefix), "not json");
        Check(CopyingAs("theo").Contains("could not be read", StringComparison.Ordinal) && !File.Exists(other) && CarriedBuilds.Read(layout, "theo").Count == 0, "a Creative save that does not read is not copied, and nothing is written");
        Write(layout.IslandSave("theo", "creative", island.RoomId, island.ManifestPrefix), CreativeSave("theo"));
        Write(CarriedBuilds.LedgerPath(layout, "theo"), "not a ledger");
        Check(!CarriedBuilds.Offered(layout, "theo", island) && CopyingAs("theo").Contains("could not be read", StringComparison.Ordinal) && File.ReadAllText(CarriedBuilds.LedgerPath(layout, "theo")) == "not a ledger" && !File.Exists(other),
            "a list of carried builds that does not read keeps the copy from being offered or made, and is kept as it was");
    }

    private void TestLastGame()
    {
        var layout = new TeamLayout(Folder("last"));
        Check(LastGame.Read(layout) == null, "there is no last game before one is played");
        var last = new LastGame("mira", "plain_isle", "challenge", "gentle", Now);
        Check(last.Write(layout) && LastGame.Read(layout) == last, "the last game is written and read back");
        Write(LastGame.PathFor(layout), "{\"schema\":\"enfractal.last_game\",\"version\":1,\"team_id\":\"mira\",\"room_id\":\"plain_isle\",\"mode\":\"challenge\",\"difficulty\":\"nasty\",\"written_utc\":\"2026-10-16T09:00:00Z\"}");
        Check(LastGame.Read(layout) == null, "a last game that names a difficulty there is not (or an extra field, a team that is not one) is no last game");
        Write(LastGame.PathFor(layout), "{\"schema\":\"enfractal.last_game\",\"version\":1,\"team_id\":\"../x\",\"room_id\":\"plain_isle\",\"mode\":\"creative\",\"difficulty\":null,\"written_utc\":\"2026-10-16T09:00:00Z\"}");
        Check(LastGame.Read(layout) == null && !new LastGame("../x", "plain_isle", "creative", null, Now).Write(layout), "a team id that is a path is never read or written");
    }

    private void TestBootChoices()
    {
        Check(MenuBoot.ShowsMenu(Array.Empty<string>(), "Windows") && !MenuBoot.ShowsMenu(new[] { "--room=landscape_garage_nominal" }, "Windows") &&
              MenuBoot.ShowsMenu(new[] { "--room=landscape_garage_nominal", "--menu" }, "Windows") && MenuBoot.ShowsMenu(new[] { "--team=mira", "--mode=challenge" }, "Windows") &&
              MenuBoot.ShowsMenu(new[] { "--room=" }, "Windows"),
            "the menu opens when the game starts without a room, or with --menu; with a room named it goes straight in");
        Check(!MenuBoot.ShowsMenu(Array.Empty<string>(), "headless") && !MenuBoot.ShowsMenu(new[] { "--quit-after-ready=240" }, "Windows") && !MenuBoot.ShowsMenu(new[] { "--native-contract-probe", "--menu" }, "Windows") &&
              MenuBoot.ShowsMenu(new[] { "--menu" }, "headless"),
            "a headless run and a boot check never wait on a menu unless --menu asks; the release probe never opens it");
        var launch = new MenuLaunch("mira", new IslandEntry("plain_isle", "Plain Isle", "user://rooms/plain_isle", "0123456789abcdef", false, false, false), "challenge", "brave",
            MenuLaunch.ArgumentsFor("mira", "challenge", "brave"));
        var arguments = MenuBoot.ArgumentsFor(new[] { "--no-playtest-record", "--team=other", "--mode=creative", "--fullscreen", "--difficulty=gentle", "--menu", "--knack=quick_feet:2" }, launch);
        Check(arguments.SequenceEqual(new[] { "--no-playtest-record", "--fullscreen", "--knack=quick_feet:2", "--team=mira", "--mode=challenge", "--difficulty=brave" }),
            "the room's command line keeps what the game was launched with (no record, full screen, a test's knack) and takes the team, mode and difficulty from the menu: " + string.Join(" ", arguments));
        Check(MenuLaunch.ArgumentsFor("mira", "creative", "gentle").SequenceEqual(new[] { "--team=mira", "--mode=creative" }), "Creative passes no difficulty");
        var back = MenuBoot.RelaunchArguments(new[] { "--path", "C:/dev/game", "--resolution", "1280x720" },
            new[] { "--build=abc1234", "--room=landscape_garage_nominal", "--team=mira", "--mode=challenge", "--difficulty=gentle", "--fullscreen", "--quit-after-ready=240", "--menu", "--no-playtest-record" });
        Check(back.SequenceEqual(new[] { "--path", "C:/dev/game", "--resolution", "1280x720", "--", "--build=abc1234", "--fullscreen", "--no-playtest-record", "--menu" }),
            "\"Back to the menu\" starts the game again with its own engine arguments and the user ones it had, but for the room, team, mode and difficulty it was playing, and --menu: " + string.Join(" ", back));
        var from = MenuBoot.RelaunchArguments(new[] { "--resolution", "1280x720" }, new[] { "--room=x" }, "C:/dev/game");
        Check(from.SequenceEqual(new[] { "--path", "C:/dev/game", "--resolution", "1280x720", "--", "--menu" }) &&
              MenuBoot.RelaunchArguments(new[] { "--path", "D:/other" }, Array.Empty<string>(), "C:/dev/game").SequenceEqual(new[] { "--path", "D:/other", "--", "--menu" }),
            "a game run from its source tree is told its project again (the engine does not list it), unless it already was: " + string.Join(" ", from));
    }

    private void TestLinkWords()
    {
        Check(GubbleLinkNote.StatusWords(null) == "" && GubbleLinkNote.Connected == "Gubble: connected" && GubbleLinkNote.Waiting == "Gubble: waiting for your AI",
            "the room's first seconds say \"Gubble: connected\" or \"Gubble: waiting for your AI\"");
    }
}
