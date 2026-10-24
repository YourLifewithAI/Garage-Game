using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Tests;

/// <summary>
/// Teams and save paths (docs/runs/RUN-2-MECHANICS.md, packet M3; docs/game/MECHANICS.md sections 2, 3, 7 and 12): the team folder
/// and its layout, the team file, the expertise store's version 3 and the union of the per-mode Glow records, the migration of the
/// saves from before teams (write, verify, and remove the old files only on a later launch; nothing overwritten; nothing removed that
/// changed or whose copy does not read), and the game's boot: --team=, --mode= and --difficulty= honoured, the mode fixed once the
/// room is up (world.set_mode refused, Live refused), the island save's team block keeping its difficulty, and the player's knack.set
/// (an open slot, a knack the pack offers, only at the camp) reaching the body through the team file.
/// Headless, --fixed-fps 60. Run: res://tests/native_team_saves.tscn. Its files go under user://tests/team_saves (the runner's user
/// folder). "-- --migrate-copy=&lt;folder&gt;" instead runs one launch's migration on a copy of a user folder and reports it.
/// </summary>
public partial class TeamSavesTest : Node3D
{
    private const string Root = "user://tests/team_saves";
    private int _checks;
    private int _failures;

    public override async void _Ready()
    {
        try
        {
            if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--migrate-copy=", StringComparison.Ordinal)) is { } copy)
            {
                MigrateCopy(copy["--migrate-copy=".Length..]);
                GetTree().Quit(_failures == 0 ? 0 : 1);
                return;
            }
            var root = ProjectSettings.GlobalizePath(Root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            TestLayout();
            TestTeamFile();
            TestExpertiseStore();
            TestMigration();
            TestMigrationKeepsWhatItShould();
            TestTheWindowSwitch();
            await TestTheGameBoot();
            await TestABootThatCannotSetItsMode();
            await TestAFixtureBootIsAsBefore();
            await TestTrustInTheTeamFile();
            TestTheRenamedStores();
            await TestTheRenamedSaves();
            TestTheFolderMove();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GD.Print($"NATIVE_TEAM_SAVES: {_checks - _failures}/{_checks} checks passed; the team folder and its layout (two teams never share a file), the team file, the expertise store's version 3 and the union of the per-mode Glow records, the migration (written and verified, the old files removed only on a later launch, never one that changed or whose copy does not read, never over a team's own file), the boot's team, mode and difficulty, world.set_mode and Live refused once the room is up, the save keeping its difficulty, an island whose mode could not be set held rather than played under another mode's rules, knack.set at the camp reaching the body with a swapped knack's earned rank kept, Trust kept in the team file (the player's marks and reset, never the companion's), and no Windows device's name for a team; files written before the rename (enfractal ids) read as they are and given the new ids by the next save, never by a read, the creations' sources kept as written; not the menu; the save folder's move from EnFractal to Garage-Game (copied and verified, caches left, nothing overwritten, resumed, never repeated)");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.Print("FAIL the suite threw: " + error);
            GetTree().Quit(1);
        }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
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

    private static string Folder(string name)
    {
        var path = ProjectSettings.GlobalizePath($"{Root}/{name}");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
    }

    private static Dictionary<string, string> Hashes(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => SaveMigration.Sha256(File.ReadAllBytes(f))) : new();

    // ---- the layout: one folder a team, never another's ----

    private void TestLayout()
    {
        var layout = new TeamLayout(Folder("layout"));
        Check(layout.Relative(layout.TeamFile("mira")) == "teams/mira/team.json" && layout.Relative(layout.ExpertiseFile("mira")) == "teams/mira/expertise.json" &&
              layout.Relative(layout.VitalsFile("mira", "challenge")) == "teams/mira/modes/challenge/vitals.json" &&
              layout.Relative(layout.IslandSave("mira", "creative", "landscape_garage_nominal", "2ef6bc0eb7a1e5d4")) == "teams/mira/modes/creative/islands/landscape_garage_nominal/2ef6bc0eb7a1e5d4/inventions.json",
            "the layout is the contract's: team.json, expertise.json, modes/<mode>/vitals.json, modes/<mode>/islands/<room>/<manifest prefix>/inventions.json");
        Check(TeamLayout.UserVitalsPath("mira", "creative") == "user://teams/mira/modes/creative/vitals.json" && TeamLayout.UserExpertisePath("mira") == "user://teams/mira/expertise.json" &&
              TeamLayout.UserIslandsRoot("mira", "challenge") == "user://teams/mira/modes/challenge/islands", "and in Godot's spelling for the host's statics");
        // Two teams never share a file: every path of one is under its own folder and none is the other's.
        var paths = new Func<string, IEnumerable<string>>(team => new[] { layout.TeamFile(team), layout.ExpertiseFile(team) }
            .Concat(TeamLayout.Modes.SelectMany(m => new[] { layout.VitalsFile(team, m), layout.IslandSave(team, m, "garage", "0123456789abcdef") })));
        var a = paths("mira").ToList();
        var b = paths("theo").ToList();
        Check(a.All(p => p.StartsWith(layout.TeamFolder("mira") + Path.DirectorySeparatorChar, StringComparison.Ordinal)) &&
              b.All(p => p.StartsWith(layout.TeamFolder("theo") + Path.DirectorySeparatorChar, StringComparison.Ordinal)) && !a.Intersect(b).Any() &&
              !layout.TeamFolder("mira").StartsWith(layout.TeamFolder("theo"), StringComparison.Ordinal), "two teams never share a file: each one's files are under its own folder");
        Check(new TeamLayout(layout.Root + "/").Root == layout.Root && new TeamLayout(layout.Root + "/").TeamFile("mira") == layout.TeamFile("mira"),
            "a user folder named with a final separator (user:// globalized) is the same folder");
        Check(!TeamLayout.ValidTeamId("Mira") && !TeamLayout.ValidTeamId("../x") && !TeamLayout.ValidTeamId("") && TeamLayout.ValidTeamId("boot_probe"),
            "a team id is a lowercase token, never a path");
        var refused = 0;
        foreach (var bad in new[] { "../local", "a/b", "" }) try { layout.TeamFolder(bad); } catch (ArgumentException) { refused++; }
        Check(refused == 3, "a path is never made from a team id that is not one");
        // The review's finding 3: Windows keeps these names for its devices (a teams\nul folder cannot be made), in any case.
        var devices = new[] { "con", "prn", "aux", "nul", "com0", "com1", "com9", "lpt0", "lpt1", "lpt9", "NUL", "Con", "cOm1", "LPT9" };
        var near = new[] { "con-1", "nul_team", "com10", "lpt", "console", "auxiliary", "com_1" };
        Check(devices.All(id => !TeamLayout.ValidTeamId(id)) && near.All(TeamLayout.ValidTeamId) && devices.All(id => Refuses(() => new TeamStore(Path.Combine(layout.Root, "teams", id, "team.json"), id))),
            "a Windows device's name is never a team id, whatever its case (nul, con, aux, prn, com0-9, lpt0-9); names that only start like one are");
        var device = 0;
        foreach (var bad in new[] { "nul", "com1", "lpt1" }) try { layout.TeamFolder(bad); } catch (ArgumentException) { device++; }
        Check(device == 3 && Refuses(() => TeamFile.Create("nul", DateTime.UtcNow, "Mira", "prototype", "Wisp", "#65b9b0")) &&
              Refuses(() => LocalGlowProgressStore.Serialize(GlowProgress.Fresh, "con")),
            "and no folder, team file or expertise store is made for one");
    }

    // ---- the team file ----

    private void TestTeamFile()
    {
        var made = new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);
        var team = TeamFile.Create("example_team", made, "Mira", "stickbear", "Wisp", "#65b9b0");
        var bytes = team.Serialize();
        var back = TeamFile.Parse(bytes);
        Check(back == team with { Knacks = back.Knacks } && back.Knacks.Count == 0 && back.SlotsOpen == 1 && back.Trust == 0 && bytes[^1] == '\n' && !bytes.Contains((byte)'\r'),
            "a new team: one slot open, no knack, Trust at base (a field, unused yet); it reads back as written, LF-terminated");
        var text = Encoding.UTF8.GetString(bytes);
        Check(text.StartsWith("{\"schema\":\"garage_game.team\",\"version\":1,\"team_id\":\"example_team\"", StringComparison.Ordinal) && text.Contains("\"trust\":{\"count\":0}", StringComparison.Ordinal),
            "the team file is garage_game.team version 1 with its Trust: " + text.Trim());
        var refused = new[]
        {
            text.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            text.Replace("\"schema\":", "\"let_my_ai_see\":true,\"schema\":", StringComparison.Ordinal),
            text.Replace("\"slots_open\":1", "\"slots_open\":4", StringComparison.Ordinal),
            text.Replace("\"Mira\"", "\"Mi\\u200bra\"", StringComparison.Ordinal),
            text.Replace("\"team_id\":\"example_team\"", "\"team_id\":\"../x\"", StringComparison.Ordinal),
            text.Replace("\"count\":0", "\"count\":0,\"count\":5", StringComparison.Ordinal),
        }.Count(t => Refuses(() => TeamFile.Parse(Encoding.UTF8.GetBytes(t))));
        Check(refused == 6, $"the team file is read strictly: another version, a field it does not have (a machine switch among them), a fourth slot, a hidden character, a path for an id, a duplicate key ({refused}/6 refused)");
        var pack = IslandRules.Parse(File.ReadAllBytes(ProjectSettings.GlobalizePath(IslandRules.PathFor(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion))));
        var table = pack.Knacks;
        var quick = team.WithKnack(1, "quick_feet", table);
        Check(quick.Knacks.Single() == new ChosenKnack(1, "quick_feet", 1) && ReferenceEquals(quick.WithKnack(1, "quick_feet", table), quick),
            "a knack chosen starts at rank 1; the same knack in the same slot changes nothing");
        var ranked = quick with { Knacks = new[] { new ChosenKnack(1, "quick_feet", 3) } };
        Check(ranked.WithKnack(1, "quick_feet", table).Knacks.Single().Rank == 3 && ranked.WithKnack(1, "high_hop", table).Knacks.Single() == new ChosenKnack(1, "high_hop", 1),
            "a rank is earned, never set: the knack kept keeps it, a new one starts at 1");
        // The review's finding 2: a free swap never costs an earned rank. Out, the rank is set aside; back in, it returns.
        var aside = ranked.WithKnack(1, "high_hop", table);
        var swappedBack = aside.WithKnack(1, "quick_feet", table);
        Check(aside.Earned.Count == 1 && aside.Earned["quick_feet"] == 3 && swappedBack.Knacks.Single() == new ChosenKnack(1, "quick_feet", 3) && ReferenceEquals(swappedBack.Earned, TeamFile.NoneEarned),
            $"a knack swapped out keeps the rank it earned (quick_feet {aside.Earned.GetValueOrDefault("quick_feet")}), and set back into its slot returns at it ({swappedBack.Knacks.Single().Rank})");
        var twice = (aside with { Knacks = new[] { new ChosenKnack(1, "high_hop", 2) } }).WithKnack(1, "cat_feet", table);
        Check(twice.Earned.Count == 2 && twice.Earned["high_hop"] == 2 && twice.Earned["quick_feet"] == 3 && twice.Knacks.Single() == new ChosenKnack(1, "cat_feet", 1) &&
              ReferenceEquals(quick.WithKnack(1, "high_hop", table).Earned, TeamFile.NoneEarned),
            "every knack set aside keeps its own rank; one that never ranked up is not kept (it returns at 1 anyway)");
        var asideText = Encoding.UTF8.GetString(twice.Serialize());
        Check(asideText.Contains("\"knacks\":{\"slots_open\":1,\"chosen\":[{\"slot\":1,\"knack\":\"cat_feet\",\"rank\":1}],\"earned\":{\"high_hop\":2,\"quick_feet\":3}}", StringComparison.Ordinal) &&
              TeamFile.Parse(twice.Serialize()).WithKnack(1, "quick_feet", table).Knacks.Single().Rank == 3 && !text.Contains("earned", StringComparison.Ordinal),
            "the ranks set aside are written as knacks.earned (only when there are any: a team without one writes the bytes it wrote before) and read back: " + asideText.Trim());
        var badRanks = new[]
        {
            asideText.Replace("\"quick_feet\":3", "\"quick_feet\":1", StringComparison.Ordinal),
            asideText.Replace("\"quick_feet\":3", "\"quick_feet\":4", StringComparison.Ordinal),
            asideText.Replace("\"quick_feet\":3", "\"cat_feet\":3", StringComparison.Ordinal),
            asideText.Replace("\"quick_feet\":3", "\"Quick Feet\":3", StringComparison.Ordinal),
            asideText.Replace("\"earned\":{\"high_hop\":2,\"quick_feet\":3}", "\"earned\":[\"high_hop\"]", StringComparison.Ordinal),
            asideText.Replace("\"earned\":{", "\"kept\":{", StringComparison.Ordinal),
        }.Count(t => Refuses(() => TeamFile.Parse(Encoding.UTF8.GetBytes(t))));
        Check(badRanks == 6, $"knacks.earned is read strictly: rank 1, rank 4, a knack in a slot, a name that is no knack id, a list, another field ({badRanks}/6 refused)");
        Check(Refuses(() => team.WithKnack(2, "high_hop", table)) && Refuses(() => team.WithKnack(1, "handy", table)) && Refuses(() => team.WithKnack(1, "reach", table)) &&
              Refuses(() => team.WithKnack(1, "quick_feet", null)) && Refuses(() => (quick with { SlotsOpen = 2 }).WithKnack(2, "quick_feet", table)),
            "knack.set's rules: only an open slot, only a knack the pack offers (Handy waits for Build), never one knack in two slots");
        var miraPath = Path.Combine(Folder("team_file"), "teams", "mira", "team.json");
        var store = new TeamStore(miraPath, "mira");
        Check(store.Load() == null && !store.Exists, "a team with no file loads none and writes nothing");
        var mira = TeamFile.Create("mira", made, "Mira", "prototype", "the Gubble", "#75965c");
        store.Save(mira);
        var other = new TeamStore(miraPath, "mira");
        Check(other.Load()!.Serialize().SequenceEqual(mira.Serialize()), "the team file is written and read back whole");
        store.Save(mira.WithKnack(1, "cat_feet", table));
        Check(Refuses(() => other.Save(mira.WithKnack(1, "big_bar", table))) && store.Load()!.Knacks.Single().Knack == "cat_feet",
            "a store that read an older team file never overwrites a newer one (another session)");
        Check(Refuses(() => new TeamStore(miraPath, "theo").Load()), "a team file filed under another team's name is refused, never adopted");
        // The contract's own examples: its team file reads (a rank set aside among them), and each invalid team file is refused.
        var examples = ProjectSettings.GlobalizePath("res://") + "../contracts/examples/";
        var example = TeamFile.Parse(File.ReadAllBytes(examples + "teams/example_team/team.json"));
        var invalid = Directory.GetFiles(examples + "teams_invalid", "team_*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Check(example is { TeamId: "example_team", DisplayName: "Mira" } && example.Knacks.Single() == new ChosenKnack(1, "quick_feet", 1) && example.Earned.Count == 1 &&
              example.Earned["high_hop"] == 2 && example.WithKnack(1, "high_hop", table).Knacks.Single() == new ChosenKnack(1, "high_hop", 2) &&
              invalid.Length == 7 && invalid.All(f => Refuses(() => TeamFile.Parse(File.ReadAllBytes(f)))),
            $"the contract's team file reads, High hop set back at the rank it earned, and its {invalid.Length} invalid team files are refused");
    }

    private static bool Refuses(Action action)
    {
        try { action(); return false; }
        catch (TeamFileException) { return true; }
        catch (GlowProgressException) { return true; }
        catch (PlayerVitalsException) { return true; }
    }

    // ---- the expertise store: version 3, the union ----

    private static GlowProgress CreativeGlow => GlowProgress.Restore(18, new[] { "lantern", "torch", "bioluminesce", "constellation" }, new[] { "bioluminesce_water" }, "plain", 0.35, false);
    private static GlowProgress ChallengeGlow => GlowProgress.Restore(12, new[] { "lantern", "skylight" }, new[] { "lantern_self" }, "ember", 0.8, true);
    private const string CreativeGlowV2 = "{\"schema\":\"enfractal.glow_progress\",\"version\":2,\"successful_casts\":18,\"discoveries\":[\"lantern\",\"torch\",\"bioluminesce\",\"constellation\"],\"delivered_hints\":[\"bioluminesce_water\"],\"touch\":\"plain\",\"sound_volume\":0.35,\"muted\":false}\n";
    private const string ChallengeGlowV2 = "{\"schema\":\"enfractal.glow_progress\",\"version\":2,\"successful_casts\":12,\"discoveries\":[\"lantern\",\"skylight\"],\"delivered_hints\":[\"lantern_self\"],\"touch\":\"ember\",\"sound_volume\":0.8,\"muted\":true}\n";

    private void TestExpertiseStore()
    {
        var union = GlowProgress.Union(new[] { CreativeGlow, ChallengeGlow });
        Check(union.SuccessfulCasts == 30 && union.Discoveries.SequenceEqual(new[] { "lantern", "torch", "bioluminesce", "constellation", "skylight" }) &&
              union.DeliveredHints.SequenceEqual(new[] { "lantern_self", "bioluminesce_water" }) && union.SelectedTouch == "plain" && union.SoundVolume == 0.35 && !union.SoundMuted,
            $"the per-mode Glow records merge as their union: every expertise and hint from any mode, the uses summed, Creative's touch and sound ({union.SuccessfulCasts} uses, {string.Join(",", union.Discoveries)})");
        Check(union.ActiveLightAllowance == 5 && GlowProgress.Union(new[] { ChallengeGlow }).Same(ChallengeGlow) && GlowProgress.Union(new[] { GlowProgress.Fresh, GlowProgress.Fresh }).Same(GlowProgress.Fresh),
            "the union of Creative's four and Challenge's Skylight is all five (five lights); one record is itself; nothing is nothing");
        Check(Refuses(() => GlowProgress.Union(Array.Empty<GlowProgress>())), "a union of nothing is refused");

        var folder = Folder("expertise");
        var path = Path.Combine(folder, "teams", "mira", "expertise.json");
        var store = new LocalGlowProgressStore(path);
        Check(store.TeamId == "mira" && new LocalGlowProgressStore(Path.Combine(folder, "progress.json")).TeamId == "local", "the store's team is its folder's (a test's own path: local)");
        store.Load();
        store.Save(union);
        var text = File.ReadAllText(path);
        var document = LocalGlowProgressStore.ParseDocument(File.ReadAllBytes(path));
        Check(document.Version == 3 && document.TeamId == "mira" && document.Progress.Same(union) &&
              text.StartsWith("{\"schema\":\"garage_game.glow_progress\",\"version\":3,\"team_id\":\"mira\",\"abilities\":{\"glow\":{\"successful_casts\":30,", StringComparison.Ordinal),
            "the store writes version 3, keyed by ability, naming its team: " + text.Trim());
        Check(LocalGlowProgressStore.ParseDocument(Encoding.UTF8.GetBytes(CreativeGlowV2)) is { Version: 2, TeamId: null } v2 && v2.Progress.Same(CreativeGlow),
            "version 2 (one file per mode) is still read, for the migration");
        var bloom = text.Replace("\"abilities\":{", "\"abilities\":{\"thunder\":{\"successful_casts\":1,\"discoveries\":[],\"delivered_hints\":[]},", StringComparison.Ordinal);
        Check(Refuses(() => LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(bloom))) && Refuses(() => LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(text.Replace("\"team_id\":\"mira\"", "\"team_id\":\"Mira\"", StringComparison.Ordinal)))),
            "an ability this game does not learn yet (a newer save's) and a malformed team are refused whole, so a write never drops them");
        // Decision 52: Bloom became Grow, and Grow takes Bloom's place in the store. A store that holds "bloom" is read as Grow's
        // record and written as "grow" from then on; one holding both is ambiguous and refused whole.
        var older = JsonNode.Parse(text)!.AsObject();
        older["abilities"]!["bloom"] = new JsonObject { ["successful_casts"] = 3, ["discoveries"] = new JsonArray(), ["delivered_hints"] = new JsonArray() };
        var migrated = LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(older.ToJsonString()));
        var rewritten = Encoding.UTF8.GetString(LocalGlowProgressStore.Serialize(migrated, "mira"));
        var both = JsonNode.Parse(older.ToJsonString())!.AsObject();
        both["abilities"]!["grow"] = new JsonObject { ["successful_casts"] = 1, ["discoveries"] = new JsonArray(), ["delivered_hints"] = new JsonArray() };
        Check(migrated.Others.TryGetValue(ExpertiseLists.Grow, out var grow) && grow.SuccessfulCasts == 3 && rewritten.Contains("\"grow\":{\"successful_casts\":3", StringComparison.Ordinal) &&
              !rewritten.Contains("bloom", StringComparison.Ordinal) && Refuses(() => LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(both.ToJsonString()))),
            "a store holding Bloom's record is read as Grow's and written as \"grow\"; one holding both is refused: " + rewritten.Trim());
        string WithGrow(string id) => older.ToJsonString().Replace("\"discoveries\":[],\"delivered_hints\":[]}}", $"\"discoveries\":[\"{id}\"],\"delivered_hints\":[]}}}}", StringComparison.Ordinal);
        Check(Refuses(() => LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(WithGrow("waterside")))) &&
              LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(WithGrow("tall_plant"))).Has(ExpertiseLists.Grow, ExpertiseLists.TallPlant),
            "Grow's list is the founder's (decision 75): Tall plant is accepted under a Bloom record read as Grow, the replaced Waterside is refused");
    }

    // ---- the migration ----

    private static readonly DateTime Earlier = new(2026, 10, 14, 19, 30, 0, 123, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private static PlayerVitals ChallengeBody => PlayerVitals.Of(85, 120, 0.55f, 0.8f, new[] { new string('a', 64) }, 1, new PlayerFractals(13, 3, 1, 1), 6);

    /// <summary>A user folder as it was before teams: Glow's two per-mode records, a version 2 vitals document, a version 1 file, three room saves, the machine's settings.</summary>
    private static TeamLayout LegacyFolder(string name)
    {
        var layout = new TeamLayout(Folder(name));
        Write(layout.LegacyGlow("creative"), CreativeGlowV2);
        Write(layout.LegacyGlow("challenge"), ChallengeGlowV2);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.LegacyVitalsV2("challenge"))!);
        File.WriteAllBytes(layout.LegacyVitalsV2("challenge"), WrittenBefore(LocalPlayerVitalsStore.SerializeMode("local", "challenge", Earlier, ChallengeBody)));
        Write(layout.LegacyVitalsV1, "{\"schema\":\"enfractal.player_modes\",\"version\":1,\"mode\":\"creative\",\"modes\":{" +
            "\"creative\":{\"health\":100,\"stamina\":0.3,\"challenges\":[],\"recoveries\":0}," +
            "\"challenge\":{\"health\":40,\"stamina\":0.45,\"challenges\":[],\"recoveries\":2}," +
            "\"live\":{\"health\":100,\"stamina\":0.45,\"challenges\":[],\"recoveries\":0}}}\n");
        Write(Path.Combine(layout.LegacyRooms, "old_room", "0123456789abcdef", "inventions.json"), "{\"schema\":\"enfractal.creation-world\",\"version\":6,\"revision\":4}\n");
        Write(Path.Combine(layout.LegacyRooms, "old_room", "fedcba9876543210", "inventions.json"), "{\"schema\":\"enfractal.creation-world\",\"version\":6,\"revision\":9}\n");
        Write(Path.Combine(layout.LegacyRooms, "other_room", "1111111111111111", "inventions.json"), "{\"schema\":\"enfractal.creation-world\",\"version\":5,\"revision\":1}\n");
        Write(Path.Combine(layout.LegacyRooms, "Not A Room", "1111111111111111", "inventions.json"), "{}\n");
        Write(layout.MachineSettingsFile, "[profile]\n\nversion=1\ncompanion_color=4\ncompanion_name=\"Wisp\"\nai_eyes=false\n");
        return layout;
    }

    private void TestMigration()
    {
        var layout = LegacyFolder("migration");
        var before = Hashes(layout.Root);
        var first = SaveMigration.Run(layout, Now);
        GD.Print("TEAM_SAVES_MIGRATION first launch: " + first.Line());
        Check(first.Phase == "moved" && first.Found.Count == 7 && first.Written.Count == 6 && first.Verified == 6 && first.Kept.Count == 0 && first.Milliseconds < 2000,
            $"the first launch finds the seven old files, writes and verifies six new ones (one expertise store, two vitals documents, three island saves): {first.Line()}");
        var after = Hashes(layout.Root);
        Check(before.All(p => after.TryGetValue(p.Key, out var h) && h == p.Value), "and leaves every old file as it was, byte for byte (kept until the next launch)");
        var expertise = LocalGlowProgressStore.ParseDocument(File.ReadAllBytes(layout.ExpertiseFile("local")));
        Check(expertise.TeamId == "local" && expertise.Progress.Same(GlowProgress.Union(new[] { CreativeGlow, ChallengeGlow })),
            "Glow's per-mode records are one expertise store for the team: their union");
        var challenge = LocalPlayerVitalsStore.ParseMode(File.ReadAllBytes(layout.VitalsFile("local", "challenge")));
        var creative = LocalPlayerVitalsStore.ParseMode(File.ReadAllBytes(layout.VitalsFile("local", "creative")));
        Check(challenge.TeamId == "local" && challenge.WrittenUtc == Earlier && challenge.Vitals.Same(ChallengeBody),
            "a version 2 vitals document moves as it was (its time too), naming its team");
        Check(creative.Vitals.Stamina == 0.3f && creative.Vitals.Pips == PlayerVitals.ContractMaxPips && creative.WrittenUtc == Earlier - TimeSpan.FromSeconds(1) &&
              !File.Exists(layout.VitalsFile("local", "live")),
            "a version 1 mode played (Creative) becomes its document with a full pool (version 1 had none), written before the newest; one never played (Live) stays fresh; one a version 2 document holds (Challenge) is that document's");
        Check(first.IslandMode == "challenge" && File.ReadAllText(layout.IslandSave("local", "challenge", "old_room", "0123456789abcdef")).Contains("\"revision\":4", StringComparison.Ordinal) &&
              File.ReadAllBytes(layout.IslandSave("local", "challenge", "other_room", "1111111111111111")).SequenceEqual(File.ReadAllBytes(Path.Combine(layout.LegacyRooms, "other_room", "1111111111111111", "inventions.json"))) &&
              !Directory.Exists(Path.Combine(layout.IslandsRoot("local", "challenge"), "Not A Room")),
            "each room save, every manifest's, becomes the island save of the mode being played (the vitals written last: Challenge), byte for byte; a folder that is no room id is left alone");
        Check(new LocalPlayerVitalsStore(layout.VitalsFile("local", "creative")).Load().Mode == "challenge", "and the team's vitals say Challenge was played last, so the next boot opens the same island saves");
        Check(File.Exists(layout.MigrationLedger) && File.ReadAllText(layout.MigrationLedger).Contains("\"removed_utc\"", StringComparison.Ordinal) == false, "a ledger records what moved; nothing is removed yet");
        Check(SchemaOf(layout.MigrationLedger) == "garage_game.legacy_migration" && SchemaOf(layout.ExpertiseFile("local")) == "garage_game.glow_progress" &&
              SchemaOf(layout.VitalsFile("local", "challenge")) == "garage_game.player_modes",
            "the ledger and the stores it wrote carry the new ids (the version 2 vitals it read said the old one)");
        // The ledger as a build from before the rename wrote it: the next launch reads it as it is.
        File.WriteAllBytes(layout.MigrationLedger, WrittenBefore(File.ReadAllBytes(layout.MigrationLedger)));

        // Before the next launch: an older build writes one old file, and one copy is spoiled.
        var changed = Path.Combine(layout.LegacyRooms, "other_room", "1111111111111111", "inventions.json");
        Write(changed, "{\"schema\":\"enfractal.creation-world\",\"version\":6,\"revision\":2}\n");
        File.WriteAllText(layout.IslandSave("local", "challenge", "old_room", "fedcba9876543210"), "not json");
        // The game's user folder comes with a final separator (user:// globalized): the same folder.
        var second = SaveMigration.Run(new TeamLayout(layout.Root + Path.DirectorySeparatorChar), Now + TimeSpan.FromHours(1));
        GD.Print("TEAM_SAVES_MIGRATION next launch: " + second.Line() + " | kept: " + string.Join("; ", second.Kept));
        Check(second.Phase == "removed" && second.Removed.Count == 5 && second.Kept.Count == 2,
            $"the next launch removes the five old files whose copies read: {second.Line()}");
        Check(!File.Exists(layout.LegacyGlow("creative")) && !File.Exists(layout.LegacyGlow("challenge")) && !File.Exists(layout.LegacyVitalsV1) &&
              !File.Exists(layout.LegacyVitalsV2("challenge")) && !File.Exists(Path.Combine(layout.LegacyRooms, "old_room", "0123456789abcdef", "inventions.json")),
            "the Glow records, both vitals files and the room save with a good copy are gone");
        Check(File.Exists(changed) && File.ReadAllText(changed).Contains("\"revision\":2", StringComparison.Ordinal) &&
              File.Exists(Path.Combine(layout.LegacyRooms, "old_room", "fedcba9876543210", "inventions.json")),
            "an old file written since the move (an older build) is kept, and so is one whose copy no longer reads (verified before removing)");
        Check(!Directory.Exists(Path.Combine(layout.LegacyRooms, "old_room", "0123456789abcdef")) && !Directory.Exists(Path.Combine(layout.LegacyProgression, "modes")) &&
              File.Exists(layout.MachineSettingsFile) && File.ReadAllText(layout.MachineSettingsFile).Contains("ai_eyes=false", StringComparison.Ordinal),
            "the folders the move emptied go; the machine's settings (\"Let my AI see\" among them) stay where they were, untouched");
        var third = SaveMigration.Run(layout, Now + TimeSpan.FromHours(2));
        Check(third.Phase == "done" && third.Removed.Count == 0 && File.Exists(changed), "a launch after that does nothing: the ledger is finished");
    }

    /// <summary>
    /// The window's switch (the first recorded play's HUD round, decision 66): "fullscreen" is one of the machine's switches, beside "Play the
    /// intro", read from the same settings file; the migration and a first team's seeding never touch it, and a file that holds it
    /// wrongly (another type, another version, no file) reads as a window.
    /// </summary>
    private void TestTheWindowSwitch()
    {
        var layout = LegacyFolder("window");
        var settings = layout.MachineSettingsFile;
        Write(settings, "[profile]\n\nversion=1\ncompanion_color=4\ncompanion_name=\"Wisp\"\nai_eyes=false\nplay_intro=false\nmeter_numbers=true\nfullscreen=true\n");
        var bytes = File.ReadAllBytes(settings);
        Check(MachineSettings.FullscreenKey == "fullscreen" && MachineSettings.SwitchKeys.Contains(MachineSettings.FullscreenKey) && MachineSettings.SwitchKeys.Contains(MachineSettings.PlayIntroKey) &&
              MachineSettings.SwitchKeys.Distinct().Count() == MachineSettings.SwitchKeys.Length,
            "the machine's switches list the window's key beside \"Play the intro\", each once");
        Check(RoomHud.LoadFullscreen(settings) && !RoomHud.LoadPlayIntro(settings) && RoomHud.LoadMeterNumbers(settings),
            "the HUD reads it from the machine's settings file, and the switches beside it keep their own values");
        SaveMigration.Run(layout, Now);
        SaveMigration.Run(layout, Now + TimeSpan.FromHours(1));
        Check(File.ReadAllBytes(settings).SequenceEqual(bytes) && RoomHud.LoadFullscreen(settings), "two launches' migrations leave the settings file as it was, the window's switch with it");
        var team = MachineSettings.SeedTeam(settings, "local", Now);
        Check(team.GubbleName == "the Gubble" && team.GubbleColour == MachineSettings.AppearancePalette[4] && File.ReadAllBytes(settings).SequenceEqual(bytes),
            "the first team is seeded from the same file without writing it, and nothing of the window's switch goes into the team (the team is the player's, the window the machine's)");
        Write(settings, "[profile]\n\nversion=1\nfullscreen=1\n");
        Check(!RoomHud.LoadFullscreen(settings), "a value that is not a true or false is a window");
        Write(settings, "[profile]\n\nversion=1\n");
        Check(!RoomHud.LoadFullscreen(settings) && !RoomHud.LoadFullscreen(Path.Combine(layout.Root, "no-such-profile.cfg")) && RoomHud.LoadPlayIntro(settings),
            "no key, or no file, is a window too (and the intro still plays by default)");
    }

    private void TestMigrationKeepsWhatItShould()
    {
        // The team already has its own expertise store: the old records stay, and the team's file is not overwritten.
        var layout = LegacyFolder("occupied");
        Write(layout.ExpertiseFile("local"), Encoding.UTF8.GetString(LocalGlowProgressStore.Serialize(GlowProgress.Fresh.AfterCast(true), "local")));
        var mine = File.ReadAllBytes(layout.ExpertiseFile("local"));
        var report = SaveMigration.Run(layout, Now);
        Check(File.ReadAllBytes(layout.ExpertiseFile("local")).SequenceEqual(mine) && report.Kept.Count(k => k.Contains("the team already has", StringComparison.Ordinal)) == 2 &&
              report.Written.Count == 5, "a team's own file is never overwritten: the old Glow records stay where they are, the rest moves");
        SaveMigration.Run(layout, Now + TimeSpan.FromHours(1));
        Check(File.Exists(layout.LegacyGlow("creative")) && File.Exists(layout.LegacyGlow("challenge")), "and they are never removed");

        // One Glow record does not read: nothing of Glow moves (the union needs all), the rest does.
        layout = LegacyFolder("unreadable");
        Write(layout.LegacyGlow("challenge"), "{\"schema\":\"enfractal.glow_progress\",\"version\":9}");
        report = SaveMigration.Run(layout, Now);
        Check(!File.Exists(layout.ExpertiseFile("local")) && report.Kept.Count == 2 && File.Exists(layout.VitalsFile("local", "challenge")),
            "a Glow record that does not read keeps every Glow record where it is (a partial union would lose expertise); the vitals and islands still move");
        SaveMigration.Run(layout, Now + TimeSpan.FromHours(1));
        Check(File.Exists(layout.LegacyGlow("creative")) && File.Exists(layout.LegacyGlow("challenge")), "and none is ever removed");

        // Nothing from before teams: nothing to do, no ledger.
        var empty = new TeamLayout(Folder("empty"));
        report = SaveMigration.Run(empty, Now);
        Check(report.Phase == "none" && !File.Exists(empty.MigrationLedger) && report.Milliseconds < 2000, "a user folder with nothing from before teams writes nothing");
    }

    // ---- the game's boot ----

    private RoomWorld? _world;

    private async Task<CommandHost?> Boot(string root, params string[] arguments)
    {
        TeamBoot.ForceGameBoot = true;
        TeamBoot.UserRootOverride = $"{Root}/{Path.GetFileName(root)}";
        TeamBoot.ArgumentsOverride = arguments;
        TeamBoot.Clock = () => DateTime.UtcNow;
        RoomHud.ProfilePath = Path.Combine(root, "single_player", "room", "avatar_profile_v1.cfg");
        CommandHost.DefaultWishSaveRoot = Path.Combine(root, "wishes");
        _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        AddChild(_world);
        for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frames(1);
        Check(_world.WorldReady, "the room boots: " + _world.LoadError);
        return _world.WorldReady ? CommandHost.Of(_world) : null;
    }

    private async Task Unboot()
    {
        _world?.QueueFree();
        _world = null;
        await Frames(3);
        TeamBoot.ForceGameBoot = false;
        TeamBoot.ArgumentsOverride = null;
    }

    private static (bool Ok, string Code, string Path, string Message, string Allowed) Answer(JsonObject result) =>
        (result["ok"]?.GetValue<bool>() == true, result["error"]?["code"]?.GetValue<string>() ?? "", result["error"]?["field_path"]?.GetValue<string>() ?? "",
         result["error"]?["message"]?.GetValue<string>() ?? "", result["error"]?["allowed"]?.ToJsonString() ?? "");

    private static JsonObject Companion(CommandHost host, string op, JsonObject args) => host.HandleObject(new JsonObject
    {
        ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = "team-" + Guid.NewGuid().ToString("N")[..12], ["room_id"] = host.Room.RoomId, ["op"] = op, ["args"] = args,
    }.ToJsonString(), CommandHost.CompanionPrincipal);

    private async Task TestTheGameBoot()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var layout = LegacyFolder("boot");
        try
        {
            // The first launch: a named team in Challenge at Gentle; the saves from before teams go to the default team, never this one.
            var host = await Boot(layout.Root, "--team=boot_team", "--mode=challenge", "--difficulty=gentle");
            if (host == null) return;
            Check(host.ModeFixed && host.Mode == "challenge" && host.Difficulty == "gentle" && host.SaveTeamIdentity == new SaveTeam("boot_team", "challenge", "gentle"),
                $"the boot honours --team=, --mode= and --difficulty=: {host.SaveTeamIdentity}, mode {host.Mode}, difficulty {host.Difficulty}");
            static string Os(string godot) => Path.GetFullPath(ProjectSettings.GlobalizePath(godot));
            Check(Os(CommandHost.DefaultPlayerVitalsPath) == layout.VitalsFile("boot_team", "challenge") && Os(CommandHost.DefaultGlowProgressPath) == layout.ExpertiseFile("boot_team") &&
                  Os(CommandHost.SaveRoot) == layout.IslandsRoot("boot_team", "challenge") && Os(host.SavePath).StartsWith(layout.IslandsRoot("boot_team", "challenge"), StringComparison.Ordinal) &&
                  CommandHost.SaveRoot.StartsWith("user://", StringComparison.Ordinal),
                "the host's stores are in the team's folder, the island save under its mode: " + layout.Relative(Os(host.SavePath)));
            var team = new TeamStore(layout.TeamFile("boot_team"), "boot_team").Load();
            Check(team != null && team.GubbleName == "the Gubble" && team.GubbleColour == "#75965c" && team.Costume == "prototype" && team.DisplayName == "Player" && team.Knacks.Count == 0,
                "a new team's file is made, seeded from the machine's settings (the Gubble's name and colour; the prototype figure)");
            Check(File.Exists(layout.ExpertiseFile("local")) && !File.Exists(layout.ExpertiseFile("boot_team")) && File.Exists(layout.LegacyGlow("creative")),
                "the saves from before teams moved into the default team, not into the team booted, and the old files are still there");

            // Once the room is up the mode is the save's: set-mode is refused, Live first, the companion before anything.
            var again = Answer(host.PlayerMode("creative"));
            Check(!again.Ok && again.Code == "unsupported_capability" && again.Path == "$.op" && again.Message == CommandHost.ModeAtMenu && host.Mode == "challenge",
                $"world.set_mode once the room is up is refused at $.op, \"{again.Message}\", and the mode stays");
            var live = Answer(host.PlayerMode("live"));
            Check(!live.Ok && live.Code == "unsupported_capability" && live.Path == "$.args.mode" && live.Message == CommandHost.LiveNotYet && live.Allowed == "[\"creative\",\"challenge\"]",
                $"Live is refused at $.args.mode, allowed creative and challenge: {live.Message} {live.Allowed}");
            var asked = Answer(Companion(host, "world.set_mode", new JsonObject { ["mode"] = "creative" }));
            Check(!asked.Ok && asked.Code == "permission_denied", "the companion is refused before anything: the mode is the player's");

            // knack.set: at the camp (the spawn), an open slot, a knack the pack offers.
            var player = _world!.Player;
            var path = layout.TeamFile("boot_team");
            var preview = Answer(host.PlayerSetKnack(1, "quick_feet", preview: true));
            Check(preview.Ok && TeamFile.Parse(File.ReadAllBytes(path)).Knacks.Count == 0 && host.Knacks.Count == 0, "a knack's preview changes nothing");
            var set = host.PlayerSetKnack(1, "quick_feet");
            Check(Answer(set).Ok && set["transient"]?.GetValue<bool>() == true && set["data"] == null && TeamFile.Parse(File.ReadAllBytes(path)).Knacks.Single() == new ChosenKnack(1, "quick_feet", 1) &&
                  host.Knacks.Count == 1 && host.Knacks["quick_feet"] == 1 && Mathf.IsEqualApprox(player.Knacks.WalkRunFactor, 1.1f),
                $"at the camp the player's knack.set goes into the team file at rank 1 and reaches the body (walk x{player.Knacks.WalkRunFactor:0.00})");
            var slot = Answer(host.PlayerSetKnack(2, "high_hop"));
            var handy = Answer(host.PlayerSetKnack(1, "handy"));
            var unknown = Answer(host.PlayerSetKnack(1, "reach"));
            Check(slot is { Ok: false, Code: "invalid_args", Path: "$.args.slot" } && handy is { Ok: false, Code: "invalid_args", Path: "$.args.knack" } && unknown is { Ok: false, Code: "invalid_args", Path: "$.args.knack" },
                "a slot not open yet, a knack whose verb does not exist yet (Handy) and no knack at all are refused");
            var bytes = File.ReadAllBytes(path);
            var spawn = _world.Room.SpawnFor("player").PositionM;
            player.GlobalPosition = spawn + new Vector3(CommandHost.CampRadiusM + 0.3f, 0, 0);
            var away = Answer(host.PlayerSetKnack(1, "high_hop"));
            Check(!away.Ok && away.Code == "not_ready" && away.Message == CommandHost.KnackAtCamp && File.ReadAllBytes(path).SequenceEqual(bytes) && host.Knacks.ContainsKey("quick_feet"),
                $"away from the camp a knack is not changed: \"{away.Message}\", the team file untouched");
            player.GlobalPosition = spawn + new Vector3(0.1f, 0, 0);
            Check(Answer(host.PlayerSetKnack(1, "high_hop")).Ok && TeamFile.Parse(File.ReadAllBytes(path)).Knacks.Single() == new ChosenKnack(1, "high_hop", 1) &&
                  host.Knacks.Count == 1 && host.Knacks["high_hop"] == 1 && Mathf.IsEqualApprox(player.Knacks.HopFactor, 1.1f) && player.Knacks.WalkRunFactor == 1f,
                "back at the camp the swap is free: High hop replaces Quick feet in slot 1");
            var companion = Answer(Companion(host, "knack.set", new JsonObject { ["slot"] = 1, ["knack"] = "big_bar" }));
            Check(!companion.Ok && companion.Code == "permission_denied" && TeamFile.Parse(File.ReadAllBytes(path)).Knacks.Single().Knack == "high_hop",
                "the companion never sets a knack");
            var savePath = ProjectSettings.GlobalizePath(host.SavePath);
            await Unboot();
            var saved = JsonNode.Parse(File.ReadAllText(savePath))!["team"]!.AsObject();
            Check(saved["team_id"]?.GetValue<string>() == "boot_team" && saved["mode"]?.GetValue<string>() == "challenge" && saved["difficulty"]?.GetValue<string>() == "gentle",
                "the island save's team block keeps whose save it is, its mode and its difficulty");

            // The next launch: the save keeps its difficulty whatever the boot asks; the team file's knacks come back; the old files go.
            host = await Boot(layout.Root, "--team=boot_team", "--mode=challenge", "--difficulty=brave");
            if (host == null) return;
            Check(host.Difficulty == "gentle" && host.SaveTeamIdentity?.Difficulty == "gentle" && host.TeamNotice.Contains("Gentle", StringComparison.Ordinal),
                $"a save keeps the difficulty it was made with: --difficulty=brave plays Gentle and says so (\"{host.TeamNotice}\")");
            Check(host.Knacks.Count == 1 && host.Knacks["high_hop"] == 1 && Mathf.IsEqualApprox(_world!.Player.Knacks.HopFactor, 1.1f), "the team file's knack reaches the body at the boot");
            Check(!File.Exists(layout.LegacyGlow("creative")) && !File.Exists(layout.LegacyVitalsV1) && File.Exists(layout.ExpertiseFile("local")),
                "and the old files are removed on this later launch, their copies kept");
            await Unboot();

            // A launch with no mode plays the one this team played last; --knack= overrides the team file's for this play only.
            host = await Boot(layout.Root, "--team=boot_team", "--knack=cat_feet:2");
            if (host == null) return;
            Check(host.Mode == "challenge" && host.Difficulty == "gentle" && host.Knacks.Count == 1 && host.Knacks["cat_feet"] == 2 &&
                  TeamFile.Parse(File.ReadAllBytes(path)).Knacks.Single().Knack == "high_hop",
                "with no --mode the team plays the mode it played last; --knack= is a test's override, never written to the team file");
            await Unboot();

            // Live is refused at the boot: the team plays the mode it played last instead, and says why.
            host = await Boot(layout.Root, "--team=other_team", "--mode=live");
            if (host == null) return;
            Check(host.Mode == "creative" && host.ModeFixed && host.SaveTeamIdentity == new SaveTeam("other_team", "creative", null) && File.Exists(layout.TeamFile("other_team")) &&
                  !File.ReadAllText(layout.TeamFile("other_team")).Contains("high_hop", StringComparison.Ordinal),
                "--mode=live is refused at the boot (another team, its own file, Creative)");
            await Unboot();

            // The review's finding 2 through the host: a team whose High hop earned rank 3 swaps it out and back at the camp, free.
            var rankedStore = new TeamStore(layout.TeamFile("ranked_team"), "ranked_team");
            rankedStore.Load();
            rankedStore.Save(TeamFile.Create("ranked_team", DateTime.UtcNow, "Theo", "prototype", "the Gubble", "#75965c") with { Knacks = new[] { new ChosenKnack(1, "high_hop", 3) } });
            host = await Boot(layout.Root, "--team=ranked_team", "--mode=creative");
            if (host == null) return;
            var ranked = layout.TeamFile("ranked_team");
            var rankThree = _world!.Player.Knacks.HopFactor;
            var swapped = Answer(host.PlayerSetKnack(1, "quick_feet"));
            var aside = TeamFile.Parse(File.ReadAllBytes(ranked));
            var returned = Answer(host.PlayerSetKnack(1, "high_hop"));
            var after = TeamFile.Parse(File.ReadAllBytes(ranked));
            Check(swapped.Ok && aside.Knacks.Single() == new ChosenKnack(1, "quick_feet", 1) && aside.Earned["high_hop"] == 3 &&
                  returned.Ok && after.Knacks.Single() == new ChosenKnack(1, "high_hop", 3) && after.Earned.Count == 0 &&
                  host.Knacks["high_hop"] == 3 && rankThree > 1.1f && Mathf.IsEqualApprox(_world.Player.Knacks.HopFactor, rankThree),
                $"a knack swapped out at the camp keeps its earned rank in the team file and comes back at it, to the body too (hop x{_world.Player.Knacks.HopFactor:0.00})");
            await Unboot();
        }
        finally
        {
            await Unboot();
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }

    /// <summary>
    /// The review's finding 1: a boot whose one set-mode is refused (here the team's vitals do not read, so the host cannot leave
    /// Creative) never plays the island save under another mode's rules.
    /// </summary>
    private async Task TestABootThatCannotSetItsMode()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var root = Folder("held");
        var layout = new TeamLayout(root);
        try
        {
            Write(layout.VitalsFile("held_team", "creative"), "not a vitals document");
            var vitalsBefore = File.ReadAllBytes(layout.VitalsFile("held_team", "creative"));
            var host = await Boot(root, "--team=held_team", "--mode=challenge");
            if (host == null) return;
            var savePath = Path.GetFullPath(ProjectSettings.GlobalizePath(host.SavePath));
            GD.Print($"HELD_EVIDENCE asked challenge; the host plays {host.Mode} (shadow pool {host.BodyRules.ShadowPool}, consequences {host.BodyRules.Consequences}); " +
                     $"the save is {host.SaveTeamIdentity}, at {layout.Relative(savePath)}; notice \"{host.TeamNotice}\"");
            var note = Answer(host.PlayerJournalNote("Written while the island could not open in Challenge."));
            await Frames(3);
            GD.Print($"HELD_EVIDENCE the player's note: ok {note.Ok} {note.Code} \"{note.Message}\"; the Challenge island save exists: {File.Exists(savePath)}");
            Check(host.Mode == host.SaveTeamIdentity?.Mode || (!note.Ok && !File.Exists(savePath)),
                $"a Challenge island never plays under {host.Mode}'s rules into its save (note {note.Ok}, save written {File.Exists(savePath)})");
            Check(host.IslandHeld && host.ModeFixed && host.TeamNotice == CommandHost.IslandHeldWords("challenge") && host.TeamNotice.StartsWith("This Challenge island could not open", StringComparison.Ordinal),
                $"the island is held, and the player reads why: \"{host.TeamNotice}\"");
            Check(note is { Ok: false, Code: "persistence_uncertain" } && note.Message == host.TeamNotice,
                "every change is refused with the same words (the creation authority's persistence sink refuses, so nothing is written or published)");
            var again = Answer(host.PlayerMode("challenge"));
            var knack = Answer(host.PlayerSetKnack(1, "quick_feet"));
            Check(again is { Ok: false, Code: "unsupported_capability" } && knack is { Ok: false, Code: "not_ready" } && knack.Message == host.TeamNotice &&
                  TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("held_team"))).Knacks.Count == 0,
                "the mode stays fixed (no later set-mode plays it under yet another mode), and not even a knack is saved");
            await Unboot();
            Check(!File.Exists(savePath) && File.ReadAllBytes(layout.VitalsFile("held_team", "creative")).SequenceEqual(vitalsBefore),
                "nothing reaches the island save, not even at exit, and the vitals file is kept as it was");

            // The same broken vitals in Creative: the host already plays Creative, so the Creative save is not held (its rules are its own).
            host = await Boot(root, "--team=held_team", "--mode=creative");
            if (host == null) return;
            Check(!host.IslandHeld && host.Mode == "creative" && host.SaveTeamIdentity?.Mode == "creative" && host.VitalsNotice.Length > 0,
                $"a Creative boot with the same vitals plays its Creative save under Creative's rules, saying the traits wait (\"{host.TeamNotice}\")");
            await Unboot();
        }
        finally
        {
            await Unboot();
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }

    /// <summary>A test's own RoomWorld is a fixture: its store paths stay as it set them, and modes change as before teams.</summary>
    private async Task TestAFixtureBootIsAsBefore()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var root = Folder("fixture");
        try
        {
            var set = (Path.Combine(root, "saves"), Path.Combine(root, "glow.json"), Path.Combine(root, "vitals.json"));
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath) = set;
            RoomHud.ProfilePath = Path.Combine(root, "profile.cfg");
            CommandHost.DefaultWishSaveRoot = Path.Combine(root, "wishes");
            TeamBoot.UserRootOverride = $"{Root}/fixture";
            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(_world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frames(1);
            var host = CommandHost.Of(_world);
            Check(host != null && (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath) == set && !host.ModeFixed && host.SaveTeamIdentity == null &&
                  !Directory.Exists(Path.Combine(root, "teams")), "a test's RoomWorld keeps the store paths it set and makes no team");
            Check(host != null && Answer(host.PlayerMode("challenge")).Ok && host.Mode == "challenge" && Answer(host.PlayerMode("live")).Ok,
                "and its modes change as before (the per-mode vitals stay testable)");
            var knack = host == null ? default : Answer(host.PlayerSetKnack(1, "quick_feet"));
            Check(!knack.Ok && knack.Code == "not_ready", "with no team, knack.set waits for one");
        }
        finally
        {
            await Unboot();
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }

    /// <summary>
    /// M8: Trust is the team's, kept in team.json's trust.count by the host alone: the player's mark on a receipt of the Gubble's moves
    /// it and it is written at once; the next launch reads it back, in any mode; the player's reset writes base; the companion writes
    /// nothing.
    /// </summary>
    private async Task TestTrustInTheTeamFile()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var root = Folder("trust");
        var layout = new TeamLayout(root);
        try
        {
            var host = await Boot(root, "--team=trust_team", "--mode=challenge");
            if (host == null) return;
            var path = layout.TeamFile("trust_team");
            var stay = Companion(host, "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" });
            var marked = Answer(host.PlayerMarkReceipt(stay["action_id"]!.GetValue<string>(), yes: true));
            Check(Answer(stay).Ok && marked.Ok && host.TrustCount == 2 && TeamFile.Parse(File.ReadAllBytes(path)).Trust == 2,
                $"the player's mark moves Trust and the team file keeps it at once ({TeamFile.Parse(File.ReadAllBytes(path)).Trust})");
            var bytes = File.ReadAllBytes(path);
            var gubble = Answer(Companion(host, "receipt.mark", new JsonObject { ["action_id"] = stay["action_id"]!.GetValue<string>(), ["mark"] = "yes" }));
            var gubbleReset = Answer(Companion(host, "trust.reset", new JsonObject()));
            Check(gubble.Code == "permission_denied" && gubbleReset.Code == "permission_denied" && File.ReadAllBytes(path).SequenceEqual(bytes),
                "the companion neither marks nor resets: the team file is untouched");
            await Unboot();

            host = await Boot(root, "--team=trust_team", "--mode=creative");
            if (host == null) return;
            Check(host.TrustCount == 2 && host.TrustBand == 0 && host.TrustWords.Contains("earning your trust", StringComparison.Ordinal),
                $"the next launch reads Trust back from the team file, in any mode: the team's, not the island's ({host.TrustCount}; \"{host.TrustWords}\")");
            var reset = Answer(host.PlayerResetTrust());
            Check(reset.Ok && host.TrustCount == 0 && TeamFile.Parse(File.ReadAllBytes(path)).Trust == 0, "the player's reset writes base to the team file");
            await Unboot();
        }
        finally
        {
            await Unboot();
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }

    // ---- the rename's phase 4: files written before it (decision C, contracts/README.md "Schema ids") ----

    private static readonly Regex NewSchemaId = new("\"schema\":( ?)\"garage_game\\.", RegexOptions.Compiled);

    /// <summary>A document as a build from before the rename wrote it: every schema id in its old spelling.</summary>
    private static byte[] WrittenBefore(byte[] bytes) => Encoding.UTF8.GetBytes(NewSchemaId.Replace(Encoding.UTF8.GetString(bytes), "\"schema\":$1\"enfractal."));

    private static string SchemaOf(string path) => JsonNode.Parse(File.ReadAllText(path))?["schema"]?.GetValue<string>() ?? "";

    /// <summary>Each store reads its file in either spelling of its id, writes the new one, and refuses any other.</summary>
    private void TestTheRenamedStores()
    {
        var folder = Folder("renamed_stores");
        var team = TeamFile.Create("mira", Now, "Mira", "prototype", "the Gubble", "#75965c").WithTrust(3);
        var teamBytes = team.Serialize();
        Check(Encoding.UTF8.GetString(teamBytes).StartsWith("{\"schema\":\"garage_game.team\",", StringComparison.Ordinal) && TeamFile.Parse(WrittenBefore(teamBytes)).Trust == 3,
            "the team file is written with the new id, and one written before the rename reads");
        var teamPath = Path.Combine(folder, "teams", "mira", "team.json");
        Write(teamPath, Encoding.UTF8.GetString(WrittenBefore(teamBytes)));
        var store = new TeamStore(teamPath, "mira");
        var before = File.ReadAllBytes(teamPath);
        Check(store.Load()?.Trust == 3 && File.ReadAllBytes(teamPath).SequenceEqual(before), "reading an old team file rewrites nothing");
        store.Save(store.Load()!.WithTrust(4));
        Check(SchemaOf(teamPath) == "garage_game.team" && store.Load()?.Trust == 4, "its next save writes the new id");
        var refused = 0;
        foreach (var wrong in new[] { "garagegame.team", "garage_game.player_modes", "enfractal.teams" })
            try { TeamFile.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(teamBytes).Replace("garage_game.team", wrong, StringComparison.Ordinal))); }
            catch (TeamFileException) { refused++; }
        Check(refused == 3, "a team file named with another id is refused");

        var expertise = LocalGlowProgressStore.Serialize(CreativeGlow, "mira");
        Check(SchemaOfBytes(expertise) == "garage_game.glow_progress" && LocalGlowProgressStore.ParseDocument(WrittenBefore(expertise)).Progress.Same(CreativeGlow),
            "the expertise store is written with the new id, and one written before the rename reads");
        var vitals = LocalPlayerVitalsStore.SerializeMode("mira", "challenge", Earlier, ChallengeBody);
        Check(SchemaOfBytes(vitals) == "garage_game.player_modes" && LocalPlayerVitalsStore.ParseMode(WrittenBefore(vitals)).Vitals.Same(ChallengeBody),
            "the vitals store is written with the new id, and one written before the rename reads");
        var wishes = WishQueue.Fresh("test_room").Serialize();
        Check(SchemaOfBytes(wishes) == "garage_game.wish_queue" && WishQueue.Parse(WrittenBefore(wishes), "test_room").RoomId == "test_room",
            "the wish queue is written with the new id, and one written before the rename reads");
        var wrongs = 0;
        foreach (var (bytes, parse) in new (byte[], Action<byte[]>)[]
        {
            (expertise, b => LocalGlowProgressStore.ParseDocument(b)), (vitals, b => LocalPlayerVitalsStore.ParseMode(b)), (wishes, b => WishQueue.Parse(b, "test_room")),
        })
            try { parse(Encoding.UTF8.GetBytes(NewSchemaId.Replace(Encoding.UTF8.GetString(bytes), "\"schema\":$1\"garagegame."))); }
            catch (Exception) { wrongs++; }
        Check(wrongs == 3, "the expertise store, the vitals store and the wish queue refuse a file named with another id");
    }

    private static string SchemaOfBytes(byte[] bytes) => JsonNode.Parse(Encoding.UTF8.GetString(bytes))?["schema"]?.GetValue<string>() ?? "";

    /// <summary>
    /// A team folder written before the rename (every file naming its kind enfractal.&lt;kind&gt;) boots as it is: the team file,
    /// the vitals and the island save read; reading rewrites nothing; the next save of each writes the new id through its own
    /// writer; the creation saved then keeps its source's old spelling, which its hash pins, beside the new creation's.
    /// </summary>
    private async Task TestTheRenamedSaves()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var root = Folder("renamed");
        var layout = new TeamLayout(root);
        try
        {
            var host = await Boot(root, "--team=mira", "--mode=challenge");
            if (host == null) return;
            var stay = Companion(host, "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" });
            var marked = Answer(host.PlayerMarkReceipt(stay["action_id"]!.GetValue<string>(), yes: true));
            var placed = Answer(host.HandleObject(Placement("Old lantern", 1.5, -1.0), CommandHost.PlayerPrincipal));
            var savePath = ProjectSettings.GlobalizePath(host.SavePath);
            Check(marked.Ok && placed.Ok && host.TrustCount == 2, $"a first launch keeps a Trust mark and a creation ({marked.Code} {placed.Code} {placed.Message})");
            await Unboot();
            var teamFolder = layout.TeamFolder("mira");
            var files = Directory.GetFiles(teamFolder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
            foreach (var file in files) File.WriteAllBytes(file, WrittenBefore(File.ReadAllBytes(file)));
            Check(files.Contains(layout.TeamFile("mira")) && files.Contains(savePath) && files.Contains(layout.VitalsFile("mira", "challenge")) &&
                  files.All(f => !File.ReadAllText(f).Contains("garage_game.", StringComparison.Ordinal) && SchemaOf(f).StartsWith("enfractal.", StringComparison.Ordinal)),
                "the team's folder as a build from before the rename left it: " + string.Join(", ", files.Select(f => layout.Relative(f) + " " + SchemaOf(f))));
            var old = Hashes(teamFolder);

            host = await Boot(root, "--team=mira", "--mode=challenge");
            if (host == null) return;
            var listed = host.HandleObject(new JsonObject
            {
                ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = "renamed-1", ["room_id"] = host.Room.RoomId, ["op"] = "entities.list",
                ["args"] = new JsonObject { ["filter"] = new JsonObject { ["kind"] = "creation" } },
            }.ToJsonString(), CommandHost.PlayerPrincipal);
            var creations = listed["data"]?["items"]?.AsArray().Count ?? -1;
            Check(host.TrustCount == 2 && creations == 1, $"the old team file and island save read: Trust {host.TrustCount}, {creations} creation");
            var untouched = Hashes(teamFolder);
            var played = layout.VitalsFile("mira", "challenge");
            Check(old.Where(p => p.Key != played).All(p => untouched.TryGetValue(p.Key, out var h) && h == p.Value) &&
                  (untouched[played] == old[played] || SchemaOf(played) == "garage_game.player_modes"),
                "the boot rewrote none of them, but the vitals of the mode being played when the game saved them (with the new id): " +
                string.Join(", ", old.Where(p => untouched.GetValueOrDefault(p.Key) != p.Value).Select(p => layout.Relative(p.Key) + " " + SchemaOf(p.Key))));
            var reset = Answer(host.PlayerResetTrust());
            var second = Answer(host.HandleObject(Placement("New lantern", -1.5, -1.0), CommandHost.PlayerPrincipal));
            Check(reset.Ok && second.Ok, $"the next changes are accepted ({reset.Code} {second.Code} {second.Message})");
            await Unboot();

            var island = File.ReadAllText(savePath);
            Check(SchemaOf(layout.TeamFile("mira")) == "garage_game.team" && TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("mira"))).Trust == 0,
                "the team file's next save writes the new id");
            Check(SchemaOf(savePath) == "garage_game.creation-world" && island.Contains("\"schema\":\"enfractal.creation\"", StringComparison.Ordinal) &&
                  island.Contains("\"schema\":\"garage_game.creation\"", StringComparison.Ordinal) && !island.Contains("enfractal.creation-world", StringComparison.Ordinal),
                "the island save's next save writes the new id; the old creation keeps its source as written beside the new one's");
            var after = Hashes(teamFolder);
            var rewritten = after.Where(p => !old.TryGetValue(p.Key, out var h) || h != p.Value).Select(p => p.Key).ToList();
            Check(rewritten.All(f => !SchemaOf(f).StartsWith("enfractal.", StringComparison.Ordinal)) &&
                  old.Keys.Except(rewritten).All(f => after.TryGetValue(f, out var h) && h == old[f]),
                "every file the game saved carries the new id, and every one it did not save is as it was: rewritten " + string.Join(", ", rewritten.Select(layout.Relative)));
        }
        finally
        {
            await Unboot();
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }

    private static string Placement(string name, double x, double z) => new JsonObject
    {
        ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = "renamed-" + Guid.NewGuid().ToString("N")[..12], ["room_id"] = "test_room", ["op"] = "creation.place",
        ["args"] = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = name, ["seed"] = 1, ["mount"] = "ground",
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["id"] = "base", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.06, 0), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = "wood",
                }),
                ["nodes"] = new JsonArray(new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "base", ["params"] = new JsonObject() }),
                ["edges"] = new JsonArray(),
            },
            ["placement"] = new JsonObject { ["position_m"] = new JsonArray(x, 0, z) },
        },
    }.ToJsonString();

    // ---- one launch's migration on a copy of a user folder ----

    private void MigrateCopy(string folder)
    {
        var layout = new TeamLayout(Path.GetFullPath(folder));
        var before = Hashes(layout.Root);
        var report = SaveMigration.Run(layout, DateTime.UtcNow);
        GD.Print("TEAM_SAVES_COPY " + report.Line());
        foreach (var line in report.Found) GD.Print("TEAM_SAVES_COPY found " + line);
        foreach (var line in report.Written) GD.Print("TEAM_SAVES_COPY written " + line);
        foreach (var line in report.Removed) GD.Print("TEAM_SAVES_COPY removed " + line);
        foreach (var line in report.Kept) GD.Print("TEAM_SAVES_COPY kept " + line);
        var teams = layout.TeamsRoot;
        if (Directory.Exists(teams))
            foreach (var team in Directory.GetDirectories(teams))
            {
                var files = Directory.GetFiles(team, "*", SearchOption.AllDirectories);
                var islands = files.Where(f => f.Contains(Path.DirectorySeparatorChar + "islands" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Sum(f => new FileInfo(f).Length);
                var rest = files.Sum(f => new FileInfo(f).Length) - islands;
                GD.Print(Inv($"TEAM_SAVES_COPY team {Path.GetFileName(team)}: {rest} bytes without the island saves, {islands} bytes of island saves, {files.Length} files"));
            }
        var after = Hashes(layout.Root);
        // Every file that was there is as it was (or removed by a later launch), the migration's own ledger aside.
        var changed = before.Where(p => p.Key != layout.MigrationLedger && after.TryGetValue(p.Key, out var h) && h != p.Value).Select(p => layout.Relative(p.Key)).ToList();
        Check(changed.Count == 0, "no file that was there changed: " + string.Join(", ", changed));
        if (report.Phase == "moved")
        {
            Check(report.Kept.Count == 0 && report.Verified == report.Written.Count, "every copy verified");
            Check(before.Keys.All(after.ContainsKey), "and every old file is still there");
        }
        GD.Print($"TEAM_SAVES_COPY: {_checks - _failures}/{_checks} checks passed");
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
