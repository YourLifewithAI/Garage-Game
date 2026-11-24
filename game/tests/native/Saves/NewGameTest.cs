using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Tests;

/// <summary>
/// New game's put-away and the play lease (Run 4, MENU-2; docs/game/MECHANICS.md section 3, "Starting and continuing";
/// contracts/README.md "The menu's files"). The founder: "Every game I start, retains old saved information." So:
/// <list type="bullet">
/// <item>a game is played until its hearts are half gone and its fire is out; a start that puts nothing away opens it as it
/// was left; after New game the next start has full hearts, a lit fire, empty pockets and no room save;</item>
/// <item>the put-away's bytes match by SHA-256, its folder is one the contract accepts, the team's rest is untouched, and
/// nothing brings the put-away game back;</item>
/// <item>a held lease refuses: New game while a room is up, a second put-away, a room while a put-away is at work;</item>
/// <item>a fault at any step, and the process ending at any step, leave the old game whole.</item>
/// </list>
/// Headless, --fixed-fps 60. Run: res://tests/native/Saves/native_new_game.tscn. Its files go under user://tests/new_game (the
/// runner's user folder).
/// </summary>
public partial class NewGameTest : Node3D
{
    private const string Root = "user://tests/new_game";
    private const string Start = CampLaws.StartingId;
    private static readonly DateTime Earlier = new(2026, 10, 25, 17, 0, 0, DateTimeKind.Utc);
    private int _checks;
    private int _failures;
    private string _linkCheck = "not run";
    private RoomWorld? _world;

    public override async void _Ready()
    {
        try
        {
            var root = ProjectSettings.GlobalizePath(Root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            TestTheLedger();
            TestTheLease();
            await TestANewGame();
            TestFaults();
            TestAGameChangedWhileItMoved();
            TestThePowerGoingOut();
            TestNewGamePressedAgain();
            TestWhatIsRefused();
            TestTheOldFolderStaysOut();
            await TestTheSavesFromBeforeTeamsStayOut();
            await TestABootAndAPutAway();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GD.Print($"NATIVE_NEW_GAME: {_checks - _failures}/{_checks} checks passed; a start that puts nothing away opens the old game as it was left (health 50, the fire out); after New game the next start has full hearts, a lit fire, empty pockets and hand and no room save; the put-away's bytes match by SHA-256 and its folder is one the contract accepts (the contract's example reads and writes back byte for byte, its ten broken folders are refused); the team's rest, the other mode and the other team untouched; nothing brings a put-away game back (boots, a second New game, a recovery with nothing to recover; the save folder's move and the saves from before teams are held off while they would refill its place, their ledgers never written); a held lease refuses (New game under a room that is up, until the host has written as it leaves; a second put-away; a room while a put-away is at work), and a lease that is read-only or cannot be opened is not taken for a held one; a fault at each step and the process ending at each step leave the old game whole, byte for byte, with nothing left under set_aside, and the next start plays it; New game pressed again after a cut puts the old game back first and away whole; a room never opens a game half put away; a newer game is never moved over; the link check: {_linkCheck}");
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

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Every file under a folder by its place in it, with its SHA-256: the test's own count, apart from the put-away's. The stores' and the lease's empty .lock files are left out unless asked for.</summary>
    private static Dictionary<string, string> Hashes(string root, bool locks = false) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => locks || !f.EndsWith(".lock", StringComparison.Ordinal))
                .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => Hash(File.ReadAllBytes(f)), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var hash) && hash == p.Value);

    private static string Differences(Dictionary<string, string> before, Dictionary<string, string> after) =>
        string.Join(", ", before.Keys.Union(after.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k)).Order(StringComparer.Ordinal).Take(8)
            .Select(k => (before.ContainsKey(k) ? after.ContainsKey(k) ? "changed " : "gone " : "new ") + k));

    /// <summary>The words a put-away is refused with, "" when it is not.</summary>
    private static string Refused(Action putAway)
    {
        try { putAway(); return ""; }
        catch (SetAsideException error) { return error.Message; }
    }

    // ---- the ledger: the contract's example, and what it refuses ----

    private void TestTheLedger()
    {
        var examples = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "contracts", "examples"));
        var folder = Path.Combine(examples, "set_aside", "20261123T190512Z-example_team-challenge");
        var bytes = File.ReadAllBytes(Path.Combine(folder, SetAsideLedger.FileName));
        var ledger = SetAsideLedger.Parse(bytes);
        Check(ledger is { TeamId: "example_team", Mode: "challenge", SetAsideUtc: "2026-11-23T19:05:12Z" } && ledger.Files.Count == 4 && ledger.FolderName == Path.GetFileName(folder) &&
              ledger.SetAsideAt == new DateTime(2026, 11, 23, 19, 5, 12, DateTimeKind.Utc) && ledger.LastPlayedAt < ledger.SetAsideAt,
            "the contract's example ledger reads: its team, its mode, its moment, its four files, and the folder it belongs in");
        Check(ledger.Serialize().SequenceEqual(bytes), "and writes back byte for byte: the game writes a ledger in the example's own form");
        var problems = GameSetAside.Problems(folder);
        Check(problems.Count == 0, "the contract's example folder is whole: " + string.Join("; ", problems));
        var broken = Directory.GetDirectories(Path.Combine(examples, "set_aside_invalid")).OrderBy(d => d, StringComparer.Ordinal).ToList();
        var refused = broken.Where(reason => Directory.GetDirectories(reason).Length == 1 && GameSetAside.Problems(Directory.GetDirectories(reason)[0]).Count > 0).ToList();
        Check(broken.Count == 10 && refused.Count == 10,
            $"each of the contract's ten broken folders is refused, as the validator refuses it ({refused.Count}/{broken.Count}): accepted " + string.Join(", ", broken.Except(refused).Select(Path.GetFileName)));
        var text = Encoding.UTF8.GetString(bytes);
        var badLedgers = new[]
        {
            text.Replace("\n", "\r\n", StringComparison.Ordinal),
            text.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal),
            text.Replace("\"mode\": \"challenge\"", "\"mode\": \"hard\"", StringComparison.Ordinal),
            text.Replace("2026-11-23T19:05:12Z", "2026-02-31T19:05:12Z", StringComparison.Ordinal),
            text.Replace("2026-11-23T19:05:12Z", "2026-11-23T19:05:12.5Z", StringComparison.Ordinal),
            text.Replace("\"bytes\": 640", "\"bytes\": -1", StringComparison.Ordinal),
            text.Replace("modes/challenge/vitals.json", "modes/challenge/../vitals.json", StringComparison.Ordinal),
            text.Replace("modes/challenge/vitals.json", "modes/challenge/vitals:json", StringComparison.Ordinal),
            text.Replace("modes/challenge/vitals.json", "team.json", StringComparison.Ordinal),
            text.Replace("\"team_id\": \"example_team\"", "\"team_id\": \"../x\"", StringComparison.Ordinal),
            text.Replace("\"schema\":", "\"principal\": \"player\",\n  \"schema\":", StringComparison.Ordinal),
            (char)0xFEFF + text,
        };
        var unread = badLedgers.Count(t => { try { SetAsideLedger.Parse(new UTF8Encoding(false).GetBytes(t)); return false; } catch (SetAsideLedgerException) { return true; } });
        Check(unread == badLedgers.Length,
            $"a ledger is read strictly: CR, another version, an unknown mode, a day the calendar lacks, a fraction of a second in its moment, a negative size, a path that climbs, a path Windows cannot hold, a file twice, a path for a team, a field it does not have, a byte-order mark ({unread}/{badLedgers.Length} refused)");
        Check(SetAsideLedger.ValidPath("modes/creative/islands/garage/0123456789abcdef/inventions - Copy.json") && SetAsideLedger.ValidPath("team.json") &&
              !SetAsideLedger.ValidPath("expertise.json") && !SetAsideLedger.ValidPath("modes/creative") && !SetAsideLedger.ValidPath("modes/creative/") &&
              !SetAsideLedger.ValidPath("modes/creative//a") && !SetAsideLedger.ValidPath("/modes/creative/a") && !SetAsideLedger.ValidPath("modes/night/a") &&
              !SetAsideLedger.ValidPath("modes/live/" + new string('a', 400)),
            "a file's place is team.json or lies under a mode's folder: a stray copy with spaces passes; the expertise, a bare folder, an empty segment and 400 characters and more do not");
    }

    // ---- the lease, with no room ----

    private void TestTheLease()
    {
        var layout = new TeamLayout(Folder("lease"));
        Check(!PlayLease.InUse(layout, "pilot") && !Directory.Exists(layout.TeamFolder("pilot")), "asking whether a team is in use makes nothing: a team with no folder is not");
        var first = PlayLease.TryPlay(layout, "pilot");
        var second = PlayLease.TryPlay(layout, "pilot");
        Check(first is { Held: true, Alone: false, TeamId: "pilot" } && second is { Held: true } && File.Exists(PlayLease.PathFor(layout, "pilot")) && new FileInfo(PlayLease.PathFor(layout, "pilot")).Length == 0,
            "two rooms hold one team's lease to play, as two windows on one team did before; the lease is an empty file in the team's folder");
        Check(PlayLease.TryAlone(layout, "pilot") == null && PlayLease.InUse(layout, "pilot") && PlayLease.TryAlone(layout, "other") is { } elsewhere && Let(elsewhere),
            "while a room holds it nobody takes it alone; another team's lease is its own");
        first!.Dispose();
        Check(PlayLease.TryAlone(layout, "pilot") == null && PlayLease.InUse(layout, "pilot"), "one room leaving does not free a team another still plays");
        second!.Dispose();
        second.Dispose();
        var alone = PlayLease.TryAlone(layout, "pilot");
        Check(!first.Held && !second.Held && alone is { Held: true, Alone: true }, "once every room has left, the put-away takes it alone (letting go twice is safe)");
        Check(PlayLease.TryPlay(layout, "pilot") == null && PlayLease.TryAlone(layout, "pilot") == null && PlayLease.InUse(layout, "pilot") &&
              PlayLease.Wait(layout, "pilot", alone: false, TimeSpan.FromMilliseconds(120)) == null,
            "while a put-away holds it alone no room starts on the team and no second put-away does, however briefly they wait");
        // A start waits for a hold that is about to be let go (a room that is leaving).
        var release = Task.Run(async () => { await Task.Delay(150); alone!.Dispose(); });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var waited = PlayLease.Wait(layout, "pilot", alone: true, TimeSpan.FromSeconds(5));
        release.Wait();
        Check(waited is { Held: true } && clock.Elapsed.TotalSeconds < 4, $"a start that waits takes the lease when the other lets go ({clock.Elapsed.TotalMilliseconds:0} ms)");
        waited?.Dispose();
        Check(!PlayLease.InUse(layout, "pilot") && File.Exists(PlayLease.PathFor(layout, "pilot")), "let go, the team is free; the lease file stays (the game deletes nothing)");

        // Held is not the same as cannot be opened. A lease file that came back read-only (a restored backup, a sync tool) is
        // held and let go like any other: nothing is ever written in it.
        var path = PlayLease.PathFor(layout, "pilot");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var free = !PlayLease.InUse(layout, "pilot");
            var answer = PlayLease.Ask(layout, "pilot", alone: true, TimeSpan.Zero, out var readOnly);
            var heldAlone = readOnly is { Held: true, Alone: true } && PlayLease.InUse(layout, "pilot") && PlayLease.TryPlay(layout, "pilot") == null &&
                            PlayLease.Ask(layout, "pilot", alone: false, TimeSpan.Zero, out _) == PlayLeaseAnswer.Held;
            readOnly?.Dispose();
            var room = PlayLease.TryPlay(layout, "pilot");
            var heldToPlay = room is { Held: true } && PlayLease.InUse(layout, "pilot") && PlayLease.Ask(layout, "pilot", alone: true, TimeSpan.Zero, out _) == PlayLeaseAnswer.Held;
            room?.Dispose();
            Check(free && answer == PlayLeaseAnswer.Taken && heldAlone && heldToPlay && !PlayLease.InUse(layout, "pilot"),
                $"a read-only lease file is held and let go like any other: free with nobody on it ({free}), taken alone ({answer}, {heldAlone}), taken to play ({heldToPlay})");
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        // A lease that cannot be opened at all (here a folder stands where the file belongs) is nobody's hold: the answer comes
        // at once, however long the caller would have waited, and the team is not "in use".
        Directory.CreateDirectory(PlayLease.PathFor(layout, "blocked"));
        clock.Restart();
        var alone2 = PlayLease.Ask(layout, "blocked", alone: true, TimeSpan.FromSeconds(5), out var none);
        var play2 = PlayLease.Ask(layout, "blocked", alone: false, TimeSpan.FromSeconds(5), out var none2);
        Check(alone2 == PlayLeaseAnswer.CannotOpen && play2 == PlayLeaseAnswer.CannotOpen && none == null && none2 == null && clock.Elapsed.TotalSeconds < 2 && !PlayLease.InUse(layout, "blocked") &&
              PlayLease.TryAlone(layout, "blocked") == null && PlayLease.Wait(layout, "blocked", alone: false, TimeSpan.FromSeconds(5)) == null && clock.Elapsed.TotalSeconds < 2,
            $"a lease that cannot be opened answers so at once, with no wait, and is not a held one ({alone2}, {play2}, {clock.Elapsed.TotalMilliseconds:0} ms)");
    }

    private static bool Let(PlayLease lease)
    {
        lease.Dispose();
        return true;
    }

    // ---- a game, New game, and the next start ----

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

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;

    private static string Placement(string name, double x, double z) => new JsonObject
    {
        ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = "newgame-" + Guid.NewGuid().ToString("N")[..12], ["room_id"] = "test_room", ["op"] = "creation.place",
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

    private static int Creations(CommandHost host) => host.HandleObject(new JsonObject
    {
        ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = "newgame-" + Guid.NewGuid().ToString("N")[..12], ["room_id"] = host.Room.RoomId, ["op"] = "entities.list",
        ["args"] = new JsonObject { ["filter"] = new JsonObject { ["kind"] = "creation" } },
    }.ToJsonString(), CommandHost.PlayerPrincipal)["data"]?["items"]?.AsArray().Count ?? -1;

    private static string Body(CommandHost host) =>
        $"health {host.Vitals.Health:0} of {host.Vitals.MaxHealth:0}, the fire {(host.IsCampfireLit(Start) ? "lit" : "out")}, {host.PocketContents.Entries} in the pockets, hand {(host.HandNow.IsNone ? "empty" : "chosen")}, {Creations(host)} built, revision {host.Revision}";

    private static TeamFile SeedTeam(TeamLayout layout, string team, Func<TeamFile, TeamFile> with)
    {
        var store = new TeamStore(layout.TeamFile(team), team);
        store.Load();
        var file = with(TeamFile.Create(team, Earlier, "Pilot", "prototype", "the Gubble", "#75965c"));
        store.Save(file);
        return file;
    }

    /// <summary>A team's game in one mode as files alone (no room): a vitals store, an island save and two strays beside it, pockets and a hand; and its Creative vitals and its expertise, which are not that game.</summary>
    private static void Craft(TeamLayout layout, string team, string mode)
    {
        SeedTeam(layout, team, file => file.WithPockets(mode, InventoryContents.Empty.WithUnits("apple", 5).WithUnits("stone", 2)).WithHand(mode, new HandSelection(HandSelection.PocketsLayer, 1, null))
            .WithPockets("live", InventoryContents.Empty.WithUnits("log", 1)));
        Directory.CreateDirectory(Path.GetDirectoryName(layout.VitalsFile(team, mode))!);
        File.WriteAllBytes(layout.VitalsFile(team, mode), LocalPlayerVitalsStore.SerializeMode(team, mode, Earlier, PlayerVitals.Fresh));
        var save = layout.IslandSave(team, mode, "test_room", "0123456789abcdef");
        Write(save, "{\"stand_in\":\"an island save's place\",\"revision\":7}\n");
        Write(Path.Combine(Path.GetDirectoryName(save)!, "inventions - Copy.json"), "{\"stand_in\":\"a copy someone left\"}\n");
        Write(save + ".pending", "{\"stand_in\":\"a writer's pending file\"}\n");
        var other = mode == "creative" ? "challenge" : "creative";
        Directory.CreateDirectory(Path.GetDirectoryName(layout.VitalsFile(team, other))!);
        File.WriteAllBytes(layout.VitalsFile(team, other), LocalPlayerVitalsStore.SerializeMode(team, other, Earlier, PlayerVitals.Fresh));
        Write(layout.ExpertiseFile(team), "{\"stand_in\":\"the team's expertise\"}\n");
        foreach (var file in Directory.GetFiles(layout.TeamFolder(team), "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, Earlier);
    }

    private async Task TestANewGame()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var root = Folder("new_game");
        var layout = new TeamLayout(root);
        try
        {
            // The team: things in its Challenge pockets and a hand chosen there; other things in its Creative pockets.
            SeedTeam(layout, "pilot", file => file.WithPockets("challenge", InventoryContents.Empty.WithUnits("apple", 3)).WithHand("challenge", new HandSelection(HandSelection.PocketsLayer, 1, null))
                .WithPockets("creative", InventoryContents.Empty.WithUnits("stone", 2)));
            // What is not this game: the Gubble's expertise, another team's Challenge game, and this team's record of a Creative copy.
            Directory.CreateDirectory(layout.TeamFolder("pilot"));
            File.WriteAllBytes(layout.ExpertiseFile("pilot"), LocalGlowProgressStore.Serialize(GlowProgress.Restore(12, new[] { "lantern", "skylight" }, new[] { "lantern_self" }, "ember", 0.8, true), "pilot"));
            Craft(layout, "other", "challenge");
            Write(Path.Combine(layout.TeamFolder("pilot"), "carried_builds.json"),
                "{\"schema\":\"garage_game.carried_builds\",\"version\":1,\"team_id\":\"pilot\",\"islands\":{\"test_room\":{\"copied_utc\":\"2026-10-14T18:30:00Z\",\"from_mode\":\"creative\",\"manifest_prefix\":\"0123456789abcdef\",\"difficulty\":\"standard\",\"builds\":[\"creation:00000001\"]}}}\n");

            // The Creative game: one build.
            var host = await Boot(root, "--team=pilot", "--mode=creative");
            if (host == null) return;
            Check(Ok(host.HandleObject(Placement("Creative lantern", 1.5, -1.0), CommandHost.PlayerPrincipal)) && Creations(host) == 1, "the team's Creative game has a build");
            await Unboot();

            // The Challenge game, played: the fire out, half the hearts gone, a build.
            host = await Boot(root, "--team=pilot", "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            Check(host.Vitals.Health == 100 && host.IsCampfireLit(Start) && host.PocketContents.Units("apple") == 3 && !host.HandNow.IsNone,
                "a first Challenge game starts with full hearts and a lit fire, the team's pockets and hand with it: " + Body(host));
            var out_ = host.PutOutCampfire(Start);
            var hurt = host.ApplyPlayerDamage(50, PlayerDamageSource.Critter);
            var built = Ok(host.HandleObject(Placement("Old lantern", -1.5, -1.0), CommandHost.PlayerPrincipal));
            Check(out_ && hurt is { After: 50 } && built && host.Vitals.Health == 50 && !host.IsCampfireLit(Start), "it is played: " + Body(host));
            var savePath = Path.GetFullPath(ProjectSettings.GlobalizePath(host.SavePath));
            await Unboot();
            Check(File.Exists(savePath) && File.Exists(layout.VitalsFile("pilot", "challenge")) && GameSetAside.HasGame(layout, "pilot", "challenge") && GameSetAside.HasGame(layout, "pilot", "creative") &&
                  !GameSetAside.HasGame(layout, "pilot", "live") && !GameSetAside.HasGame(layout, "nobody", "challenge"),
                "leaving it saves its island and its vitals: the team has a Challenge game and a Creative one, and no Live one");

            // A start that puts nothing away opens the old game as it was left. That is Continue; it was also every start before New game.
            host = await Boot(root, "--team=pilot", "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            Check(host.Vitals.Health == 50 && !host.IsCampfireLit(Start) && host.PocketContents.Units("apple") == 3 && Creations(host) == 1,
                "a start that puts nothing away finds the old game as it was left: " + Body(host));

            // A held lease refuses: New game under a room that is up, in its mode or the team's other one, and nothing moves.
            var whileUp = Refused(() => GameSetAside.PutAway(layout, "pilot", "challenge", DateTime.UtcNow));
            var otherMode = Refused(() => GameSetAside.PutAway(layout, "pilot", "creative", DateTime.UtcNow, TimeSpan.FromMilliseconds(100)));
            Check(whileUp == PlayLease.StillOpen && otherMode == PlayLease.StillOpen && PlayLease.InUse(layout, "pilot") && Directory.Exists(layout.ModeFolder("pilot", "challenge")) &&
                  !Directory.Exists(GameSetAside.Root(layout)) && !File.Exists(GameSetAside.NotePath(layout, "pilot")) && host.Vitals.Health == 50,
                $"while the team's room is up New game is refused (\"{whileUp}\") and nothing moves; another team's is not this lease");
            // The lease is still held once the host has left the tree (it has written what it writes as it leaves), and free after the room.
            bool? heldAsTheHostLeft = null;
            host.TreeExiting += () =>
            {
                var alone = PlayLease.TryAlone(layout, "pilot");
                heldAsTheHostLeft = alone == null;
                alone?.Dispose();
            };
            await Unboot();
            Check(heldAsTheHostLeft == true && !PlayLease.InUse(layout, "pilot"), $"the room holds its lease until its host has written and left the tree (held then: {heldAsTheHostLeft}), and lets go with the room");

            // New game.
            var before = Hashes(root);
            var game = Hashes(layout.ModeFolder("pilot", "challenge"), locks: true);
            var teamBefore = File.ReadAllBytes(layout.TeamFile("pilot"));
            var now = DateTime.UtcNow;
            var put = GameSetAside.PutAway(layout, "pilot", "challenge", now);
            Check(put != null, "New game puts the Challenge game away");
            // With nothing put away (a build from before the put-away), the next start is the old game again: the founder's complaint.
            var folder = put?.Folder ?? Path.Combine(GameSetAside.Root(layout), "none");
            var after = Hashes(root);
            var name = Path.GetFileName(folder);
            Check(put != null && Path.GetDirectoryName(folder) == GameSetAside.Root(layout) && name == put.Ledger.FolderName && name.EndsWith("-pilot-challenge", StringComparison.Ordinal) &&
                  put.Ledger.SetAsideAt <= now && now - put.Ledger.SetAsideAt < TimeSpan.FromSeconds(1) && put.Ledger.LastPlayedAt <= put.Ledger.SetAsideAt &&
                  put.Ledger.SetAsideAt - put.Ledger.LastPlayedAt < TimeSpan.FromMinutes(5),
                $"into user://set_aside/<moment>-pilot-challenge, named for the ledger's moment, last played just before: {name}");
            var moved = Hashes(Path.Combine(folder, "modes", "challenge"), locks: true);
            Check(game.Count >= 2 && Same(game, moved) && !Directory.Exists(layout.ModeFolder("pilot", "challenge")) && !File.Exists(savePath),
                $"the mode's folder is moved whole, every file's SHA-256 as it was ({game.Count} files): {Differences(game, moved)}");
            Check(File.Exists(Path.Combine(folder, "team.json")) && Hash(File.ReadAllBytes(Path.Combine(folder, "team.json"))) == Hash(teamBefore), "with a copy of the team file as it was, byte for byte");
            var listed = put?.Ledger.Files.ToDictionary(f => f.Path, f => (f.Sha256, f.Bytes)) ?? new Dictionary<string, (string, long)>();
            var inFolder = Hashes(folder, locks: true);
            Check(listed.Count == game.Count + 1 && inFolder.Count == listed.Count + 1 && inFolder.ContainsKey(SetAsideLedger.FileName) &&
                  listed.All(p => inFolder.TryGetValue(p.Key, out var hash) && hash == p.Value.Item1 && new FileInfo(Path.Combine(folder, p.Key)).Length == p.Value.Item2),
                $"the ledger lists every file there but itself, each with its size and its SHA-256 as the test counts them ({listed.Count} files)");
            var problems = GameSetAside.Problems(folder);
            Check(problems.Count == 0 && GameSetAside.List(layout).Select(g => g.Folder).SequenceEqual(new[] { folder }) && GameSetAside.List(layout, "other").Count == 0 &&
                  GameSetAside.WasPutAway(layout, "pilot", "challenge") && !GameSetAside.WasPutAway(layout, "pilot", "creative") && !GameSetAside.WasPutAway(layout, "other", "challenge"),
                "the folder is whole by the contract's rules and is the one game listed as put away: " + string.Join("; ", problems));
            GD.Print("NEW_GAME_FOLDER " + folder.Replace('\\', '/'));
            var teamAfter = File.ReadAllBytes(layout.TeamFile("pilot"));
            var emptied = TeamFile.Parse(teamBefore).WithPockets("challenge", InventoryContents.Empty).WithHand("challenge", HandSelection.None);
            Check(teamAfter.AsSpan().SequenceEqual(emptied.Serialize()) && TeamFile.Parse(teamAfter) is { } team && team.PocketsOf("challenge").IsEmpty && team.HandOf("challenge").IsNone &&
                  team.PocketsOf("creative").Units("stone") == 2 && team.DisplayName == "Pilot" && team.Trust == TeamFile.Parse(teamBefore).Trust,
                "in the team file only that mode's pockets and hand are emptied: the person, the Gubble, Trust and the Creative pockets are as they were");
            var rest = before.Where(p => p.Key != "teams/pilot/team.json" && !p.Key.StartsWith("teams/pilot/modes/challenge/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
            var restAfter = after.Where(p => p.Key != "teams/pilot/team.json" && !p.Key.StartsWith("set_aside/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
            Check(rest.Count >= 8 && Same(rest, restAfter) && rest.ContainsKey("teams/pilot/expertise.json") && rest.ContainsKey("teams/pilot/carried_builds.json") && rest.ContainsKey("teams/pilot/modes/creative/vitals.json") &&
                  rest.Keys.Any(k => k.StartsWith("teams/pilot/modes/creative/islands/", StringComparison.Ordinal)) && rest.ContainsKey("teams/other/modes/challenge/vitals.json") && rest.ContainsKey("teams/last_game.json") &&
                  !File.Exists(GameSetAside.NotePath(layout, "pilot")),
                $"the team's rest is unchanged, byte for byte: its expertise, its carried builds, its Creative game, the other team, the machine's files ({rest.Count} files): {Differences(rest, restAfter)}");
            Check(!GameSetAside.HasGame(layout, "pilot", "challenge") && GameSetAside.HasGame(layout, "pilot", "creative") && GameSetAside.PutAway(layout, "pilot", "challenge", now) == null &&
                  GameSetAside.PutAway(layout, "pilot", "live", now) == null && GameSetAside.PutAway(layout, "nobody", "challenge", now) == null && !Directory.Exists(layout.TeamFolder("nobody")) &&
                  Directory.GetDirectories(GameSetAside.Root(layout)).Length == 1,
                "the team now has no Challenge game: New game again, in a mode never played or for a team that is not there puts nothing away and makes nothing");

            // The next start: a first game.
            host = await Boot(root, "--team=pilot", "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            Check(host.Vitals.Health == 100 && host.Vitals.Health == host.Vitals.MaxHealth && host.Vitals.Recoveries == 0 && host.Vitals.Fractals.Found == 0,
                "after New game the next start has full hearts: " + Body(host));
            Check(host.IsCampfireLit(Start), "and a lit fire: " + Body(host));
            Check(host.PocketContents.IsEmpty && host.HandNow.IsNone, "and empty pockets and hand: " + Body(host));
            Check(Creations(host) == 0 && host.Revision == 0 && host.MigrationCandidate() == null && host.SaveTeamIdentity == new SaveTeam("pilot", "challenge", "standard"),
                "and no room save: nothing built, no earlier save offered: " + Body(host));
            // The new game is played a little, so it can be told from a fresh one and from the old one.
            var graze = host.ApplyPlayerDamage(25, PlayerDamageSource.Critter);
            await Unboot();
            Check(graze is { After: 75 }, "the new game is played a little (health 75)");

            // The other mode's game is the one it was.
            host = await Boot(root, "--team=pilot", "--mode=creative");
            if (host == null) return;
            Check(Creations(host) == 1 && host.PocketContents.Units("stone") == 2, "the team's Creative game is as it was: " + Body(host));
            await Unboot();

            // Nothing brings the put-away game back: not the boots, not a recovery with nothing to recover, not a second New game
            // (old saves that would refill its place: TestTheOldFolderStaysOut, TestTheSavesFromBeforeTeamsStayOut).
            var migration = TeamBoot.MigrateOldSaves(layout, DateTime.UtcNow);
            string recovered;
            using (var alone = PlayLease.TryAlone(layout, "pilot")!) recovered = GameSetAside.Recover(alone, layout);
            var second = GameSetAside.PutAway(layout, "pilot", "challenge", now);
            Check(second != null && second.Folder != folder && GameSetAside.Problems(second.Folder).Count == 0 && second.Ledger.SetAsideAt == put!.Ledger.SetAsideAt.AddSeconds(1) &&
                  GameSetAside.List(layout, "pilot").Select(g => g.Folder).SequenceEqual(new[] { second.Folder, folder }),
                "a second New game in the same second takes the next second's folder (a folder that is there is never written into again); both are listed, the newest first");
            host = await Boot(root, "--team=pilot", "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            Check(host.Vitals.Health == 100 && host.IsCampfireLit(Start) && Creations(host) == 0, "and the start after it is a first game again: " + Body(host));
            await Unboot();
            var later = Hashes(folder, locks: true);
            Check(migration.Phase is "none" or "done" && recovered == "" && Same(inFolder, later) && GameSetAside.Problems(folder).Count == 0,
                $"nothing brought the put-away game back and nothing touched its folder (migration {migration.Phase}): {Differences(inFolder, later)}");
        }
        finally
        {
            await Unboot();
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }

    // ---- a fault part-way leaves the old game whole ----

    private void TestFaults()
    {
        var layout = new TeamLayout(Folder("faults"));
        Craft(layout, "faulty", "challenge");
        var before = Hashes(layout.TeamsRoot);
        var at = new DateTime(2026, 11, 24, 9, 30, 15, DateTimeKind.Utc);
        var whole = 0;
        var seen = new List<string>();
        try
        {
            foreach (var step in GameSetAside.Steps.Take(5))
            {
                GameSetAside.AtStep = name => { if (name == step) throw new IOException("a test's fault at " + name); };
                var words = Refused(() => GameSetAside.PutAway(layout, "faulty", "challenge", at));
                GameSetAside.AtStep = null;
                var after = Hashes(layout.TeamsRoot);
                // "Nothing was changed": the game as it was, and nothing left under set_aside either (the folder the put-away
                // made, with its copy of the team file, goes once the game is home).
                var left = Directory.Exists(GameSetAside.Root(layout)) ? Directory.GetFileSystemEntries(GameSetAside.Root(layout)).Length : 0;
                var good = words == GameSetAside.Failed && Same(before, after) && GameSetAside.List(layout).Count == 0 && !PlayLease.InUse(layout, "faulty") && left == 0 &&
                           GameSetAside.HasGame(layout, "faulty", "challenge") && GameSetAside.PendingPutAway(layout, "faulty") == null;
                if (good) whole++;
                else seen.Add($"{step}: \"{words}\" {Differences(before, after)}; {left} left under set_aside");
            }
        }
        finally { GameSetAside.AtStep = null; }
        Check(whole == 5 && before.Count == 7,
            $"a fault after each step (the note, the copy, the move, the check, the pockets) is refused with \"{GameSetAside.Failed}\" and leaves the old game whole, byte for byte, with no note, no ledger, nothing under set_aside and the lease free ({whole}/5): {string.Join(" | ", seen)}");
        // Then it works, in that same second's folder (nothing was left in its way), and the ledger holds the strays beside the save too.
        var put = GameSetAside.PutAway(layout, "faulty", "challenge", at);
        Check(put != null && put.Ledger.SetAsideUtc == "2026-11-24T09:30:15Z" && put.Ledger.LastPlayedUtc == "2026-10-25T17:00:00Z" && GameSetAside.Problems(put.Folder).Count == 0 &&
              put.Ledger.Files.Select(f => f.Path).SequenceEqual(new[]
              {
                  "modes/challenge/islands/test_room/0123456789abcdef/inventions - Copy.json", "modes/challenge/islands/test_room/0123456789abcdef/inventions.json",
                  "modes/challenge/islands/test_room/0123456789abcdef/inventions.json.pending", "modes/challenge/vitals.json", "team.json",
              }) && GameSetAside.List(layout).Count == 1,
            "the put-away then works, in the folder of the second it was asked in; a stray copy and a writer's pending file beside the save are put away with it; last played is the newest write among its files");
        var team = TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("faulty")));
        Check(team.PocketsOf("challenge").IsEmpty && team.HandOf("challenge").IsNone && team.PocketsOf("live").Units("log") == 1 && File.Exists(layout.VitalsFile("faulty", "creative")) &&
              File.Exists(layout.ExpertiseFile("faulty")), "and only that mode's pockets and hand are emptied; the Live pockets, the Creative vitals and the expertise stay");

        // A game that is only pockets (no folder yet) is a game: put away too, as its team file.
        SeedTeam(layout, "pockets", file => file.WithPockets("creative", InventoryContents.Empty.WithUnits("berries", 4)));
        var only = GameSetAside.PutAway(layout, "pockets", "creative", at);
        Check(only != null && only.Ledger.Files.Select(f => f.Path).SequenceEqual(new[] { "team.json" }) && GameSetAside.Problems(only.Folder).Count == 0 &&
              TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("pockets"))).PocketsOf("creative").IsEmpty && !Directory.Exists(layout.ModeFolder("pockets", "creative")),
            "a game that is only something in the pockets is put away as the team file's copy, and the pockets are emptied");
    }

    private void TestThePowerGoingOut()
    {
        var layout = new TeamLayout(Folder("power"));
        var at = new DateTime(2026, 11, 24, 9, 30, 15, DateTimeKind.Utc);
        var whole = 0;
        var seen = new List<string>();
        try
        {
            foreach (var step in GameSetAside.Steps)
            {
                var team = "cut_" + step;
                Craft(layout, team, "challenge");
                var before = Hashes(layout.TeamFolder(team));
                GameSetAside.AtStep = name => { if (name == step) throw new GameSetAside.PowerCut(); };
                var cut = false;
                try { GameSetAside.PutAway(layout, team, "challenge", at); }
                catch (GameSetAside.PowerCut) { cut = true; }
                GameSetAside.AtStep = null;
                var midway = File.Exists(GameSetAside.NotePath(layout, team)) && !PlayLease.InUse(layout, team);
                var gone = !Directory.Exists(layout.ModeFolder(team, "challenge"));
                // Until it is put back the game is still this mode's game (the menu offers Continue, and asks before New game),
                // though its folder may be gone and its pockets empty; past the ledger it is put away, and the mode has none.
                var waits = GameSetAside.PendingPutAway(layout, team) == (step == "ledger" ? null : "challenge") && GameSetAside.HasGame(layout, team, "challenge") == (step != "ledger") &&
                            GameSetAside.PendingPutAway(layout, "nobody") == null;
                string line;
                using (var alone = PlayLease.TryAlone(layout, team)!) line = GameSetAside.Recover(alone, layout);
                var after = Hashes(layout.TeamFolder(team));
                var folders = Directory.GetDirectories(GameSetAside.Root(layout), "*-" + team + "-challenge").Length;
                bool good;
                if (step == "ledger")
                {
                    // Past the line the put-away had finished: the next start drops the note and brings nothing back.
                    var put = GameSetAside.List(layout, team);
                    good = cut && midway && gone && waits && line.EndsWith("had finished", StringComparison.Ordinal) && put.Count == 1 && folders == 1 && GameSetAside.Problems(put[0].Folder).Count == 0 &&
                           !Directory.Exists(layout.ModeFolder(team, "challenge")) && !File.Exists(GameSetAside.NotePath(layout, team)) &&
                           TeamFile.Parse(File.ReadAllBytes(layout.TeamFile(team))).PocketsOf("challenge").IsEmpty;
                }
                else
                    good = cut && midway && waits && gone == (step is "moved" or "checked" or "emptied") && line.EndsWith("was put back", StringComparison.Ordinal) && Same(before, after) &&
                           GameSetAside.List(layout, team).Count == 0 && !File.Exists(GameSetAside.NotePath(layout, team)) && folders == 0 &&
                           GameSetAside.PendingPutAway(layout, team) == null && GameSetAside.HasGame(layout, team, "challenge");
                if (good) whole++;
                else seen.Add($"{step}: cut {cut}, midway {midway}, gone {gone}, waits {waits}, {folders} folders under set_aside, \"{line}\" {Differences(before, after)}");
            }
        }
        finally { GameSetAside.AtStep = null; }
        Check(whole == 6,
            $"the process ending after each step leaves a note, and the game is still that mode's game while it waits; the next start puts the old game back whole, byte for byte (the folder home, the pockets returned, nothing left under set_aside), or, past the ledger, finds the put-away finished ({whole}/6): {string.Join(" | ", seen)}");

        try
        {
            // A newer game already where the old one stood: nothing is moved over it; the old game's put-away is finished instead.
            Craft(layout, "newer", "challenge");
            GameSetAside.AtStep = name => { if (name == "moved") throw new GameSetAside.PowerCut(); };
            try { GameSetAside.PutAway(layout, "newer", "challenge", at); }
            catch (GameSetAside.PowerCut) { }
            GameSetAside.AtStep = null;
            var fresh = LocalPlayerVitalsStore.SerializeMode("newer", "challenge", at, PlayerVitals.Fresh);
            Directory.CreateDirectory(Path.GetDirectoryName(layout.VitalsFile("newer", "challenge"))!);
            File.WriteAllBytes(layout.VitalsFile("newer", "challenge"), fresh);
            string line;
            using (var alone = PlayLease.TryAlone(layout, "newer")!) line = GameSetAside.Recover(alone, layout);
            var kept = GameSetAside.List(layout, "newer");
            Check(line.Contains("was finished", StringComparison.Ordinal) && kept.Count == 1 && GameSetAside.Problems(kept[0].Folder).Count == 0 && kept[0].Ledger.Files.Count == 5 &&
                  File.ReadAllBytes(layout.VitalsFile("newer", "challenge")).AsSpan().SequenceEqual(fresh) && Directory.GetFiles(layout.ModeFolder("newer", "challenge"), "*", SearchOption.AllDirectories).Length == 1 &&
                  !File.Exists(GameSetAside.NotePath(layout, "newer")),
                $"a newer game standing where the old one stood is never moved over: the old game's put-away is finished where it lies, whole and listed (\"{line}\")");
            // The cut came before the pockets were emptied, so the team file still held the old game's things under the newer
            // game: they are the old game's (its copy of the team file keeps them), and the newer game does not carry them too.
            var newerTeam = TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("newer")));
            var keptTeam = kept.Count == 1 ? TeamFile.Parse(File.ReadAllBytes(Path.Combine(kept[0].Folder, "team.json"))) : newerTeam;
            Check(newerTeam.PocketsOf("challenge").IsEmpty && newerTeam.HandOf("challenge").IsNone && newerTeam.PocketsOf("live").Units("log") == 1 &&
                  keptTeam.PocketsOf("challenge").Units("apple") == 5 && keptTeam.PocketsOf("challenge").Units("stone") == 2 && !keptTeam.HandOf("challenge").IsNone,
                $"and the old game's pockets and hand go with it: the newer game's are empty ({newerTeam.PocketsOf("challenge").Entries} kinds left), the put-away copy holds the 5 apples and 2 stones, the Live pockets stay");
            // Once the newer game has changed its pockets, what is in them cannot be told apart: they are left as they are.
            Craft(layout, "mixed", "challenge");
            GameSetAside.AtStep = name => { if (name == "moved") throw new GameSetAside.PowerCut(); };
            try { GameSetAside.PutAway(layout, "mixed", "challenge", at); }
            catch (GameSetAside.PowerCut) { }
            GameSetAside.AtStep = null;
            Directory.CreateDirectory(Path.GetDirectoryName(layout.VitalsFile("mixed", "challenge"))!);
            File.WriteAllBytes(layout.VitalsFile("mixed", "challenge"), fresh);
            var mixedStore = new TeamStore(layout.TeamFile("mixed"), "mixed");
            mixedStore.Save(mixedStore.Load()!.WithPockets("challenge", InventoryContents.Empty.WithUnits("apple", 6).WithUnits("stone", 2)));
            using (var alone = PlayLease.TryAlone(layout, "mixed")!) line = GameSetAside.Recover(alone, layout);
            var mixed = TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("mixed")));
            Check(line.Contains("was finished", StringComparison.Ordinal) && GameSetAside.List(layout, "mixed").Count == 1 && mixed.PocketsOf("challenge").Units("apple") == 6 && !mixed.HandOf("challenge").IsNone,
                $"pockets the newer game has changed since are left as they are ({mixed.PocketsOf("challenge").Units("apple")} apples)");

            // Empty folders where the game stood are no game: they make way, and the old game goes home.
            Craft(layout, "hollow", "challenge");
            var before = Hashes(layout.TeamFolder("hollow"));
            GameSetAside.AtStep = name => { if (name == "emptied") throw new GameSetAside.PowerCut(); };
            try { GameSetAside.PutAway(layout, "hollow", "challenge", at); }
            catch (GameSetAside.PowerCut) { }
            GameSetAside.AtStep = null;
            Directory.CreateDirectory(Path.Combine(layout.ModeFolder("hollow", "challenge"), "islands", "test_room"));
            using (var alone = PlayLease.TryAlone(layout, "hollow")!) line = GameSetAside.Recover(alone, layout);
            Check(line.EndsWith("was put back", StringComparison.Ordinal) && Same(before, Hashes(layout.TeamFolder("hollow"))) && GameSetAside.List(layout, "hollow").Count == 0,
                "empty folders left where the game stood make way for it: the old game goes home whole");

            // A note that is not the put-away's own names nothing to move: it is left, and the next put-away writes over it.
            Craft(layout, "noted", "challenge");
            before = Hashes(layout.TeamFolder("noted"));
            Write(GameSetAside.NotePath(layout, "noted"), "20261124T093015Z-other-challenge\n");
            using (var alone = PlayLease.TryAlone(layout, "noted")!) line = GameSetAside.Recover(alone, layout);
            var untouched = Hashes(layout.TeamFolder("noted"));
            untouched.Remove(GameSetAside.NoteName);
            var put = GameSetAside.PutAway(layout, "noted", "challenge", at);
            Check(line.Contains("left as it is", StringComparison.Ordinal) && Same(before, untouched) && put != null && GameSetAside.Problems(put.Folder).Count == 0 && !File.Exists(GameSetAside.NotePath(layout, "noted")),
                "a note that names another team's folder moves nothing; the next put-away works and leaves no note");
        }
        finally { GameSetAside.AtStep = null; }
    }

    // ---- what is refused before anything moves ----

    private void TestWhatIsRefused()
    {
        var layout = new TeamLayout(Folder("refused"));
        var at = new DateTime(2026, 11, 24, 9, 30, 15, DateTimeKind.Utc);
        // A second put-away while one is at work on the team.
        Craft(layout, "busy", "challenge");
        var before = Hashes(layout.TeamsRoot);
        string words;
        using (PlayLease.TryAlone(layout, "busy")) words = Refused(() => GameSetAside.PutAway(layout, "busy", "challenge", at));
        Check(words == PlayLease.StillOpen && Same(before, Hashes(layout.TeamsRoot)) && !Directory.Exists(GameSetAside.Root(layout)), "a second put-away while one is at work on the team is refused, and nothing moves");
        // A team file that does not read: its pockets cannot be emptied, so nothing is put away.
        var teamFile = layout.TeamFile("busy");
        var good = File.ReadAllBytes(teamFile);
        File.WriteAllText(teamFile, "{\"schema\":\"garage_game.team\"}\n");
        before = Hashes(layout.TeamsRoot);
        words = Refused(() => GameSetAside.PutAway(layout, "busy", "challenge", at));
        Check(words == GameSetAside.TeamUnreadable && Same(before, Hashes(layout.TeamsRoot)) && !Directory.Exists(GameSetAside.Root(layout)) && GameSetAside.HasGame(layout, "busy", "challenge"),
            $"a team file that does not read refuses the put-away (\"{words}\") before anything moves");
        File.WriteAllBytes(teamFile, good);
        // A file held open by a writer.
        before = Hashes(layout.TeamsRoot);
        using (new FileStream(layout.VitalsFile("busy", "challenge"), FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None))
            words = Refused(() => GameSetAside.PutAway(layout, "busy", "challenge", at));
        Check(words == GameSetAside.Failed && Same(before, Hashes(layout.TeamsRoot)) && !Directory.Exists(GameSetAside.Root(layout)),
            "a save a writer still holds open refuses the put-away before anything moves");
        // A link in the game's folder is never followed and never put away.
        var outside = Path.Combine(layout.Root, "outside");
        Write(Path.Combine(outside, "elsewhere.json"), "{}\n");
        var link = Path.Combine(layout.ModeFolder("busy", "challenge"), "linked");
        if (MakeLink(link, outside))
        {
            words = Refused(() => GameSetAside.PutAway(layout, "busy", "challenge", at));
            var kept = Directory.Exists(layout.ModeFolder("busy", "challenge")) && File.Exists(Path.Combine(outside, "elsewhere.json")) && GameSetAside.List(layout).Count == 0;
            Directory.Delete(link);
            Check(words == GameSetAside.NotAsItIs && kept && Same(before, Hashes(layout.TeamsRoot)), $"a link in the game's folder refuses the put-away (\"{words}\"): it is never followed and never moved");
            _linkCheck = "run";
        }
        else _linkCheck = "skipped (this machine lets the test make no link)";
        Check(GameSetAside.PutAway(layout, "busy", "challenge", at) is { } put && GameSetAside.Problems(put.Folder).Count == 0, "with nothing in the way the same game is put away");
        var refusedIds = 0;
        foreach (var (team, mode) in new[] { ("../busy", "challenge"), ("busy", "hard"), ("nul", "challenge") })
            try { GameSetAside.PutAway(layout, team, mode, at); }
            catch (ArgumentException) { refusedIds++; }
        Check(refusedIds == 3, "a path for a team, a mode that is none and a device's name are never a put-away's");
        // A lease that cannot be opened at all (a folder stands where the file belongs) is a fault, said as one and at once:
        // never "that game is still open", which nobody could ever close.
        Craft(layout, "blocked", "challenge");
        Directory.CreateDirectory(PlayLease.PathFor(layout, "blocked"));
        before = Hashes(layout.TeamsRoot);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        words = Refused(() => GameSetAside.PutAway(layout, "blocked", "challenge", at, TimeSpan.FromSeconds(5)));
        Check(words == GameSetAside.Failed && clock.Elapsed.TotalSeconds < 2 && Same(before, Hashes(layout.TeamsRoot)) && GameSetAside.HasGame(layout, "blocked", "challenge") &&
              !PlayLease.InUse(layout, "blocked") && GameSetAside.List(layout, "blocked").Count == 0,
            $"a lease that cannot be opened refuses the put-away as a fault (\"{words}\"), at once ({clock.Elapsed.TotalMilliseconds:0} ms), and nothing moves");
        Check(GameSetAside.TeamUnreadable.Contains("team", StringComparison.Ordinal) && new[] { GameSetAside.Failed, GameSetAside.FailedPutBackLater, GameSetAside.TeamUnreadable, GameSetAside.NotAsItIs, GameSetAside.NotPutBack, PlayLease.StillOpen, PlayLease.BeingPutAway }
                  .All(line => line.Length <= 90 && !line.Contains("file", StringComparison.OrdinalIgnoreCase) && !line.Contains("folder", StringComparison.OrdinalIgnoreCase) && !line.Contains('/') && !line.Contains('\\')),
            "every line a player reads is short and names no file, folder or path");
    }

    /// <summary>A link to a folder: a symbolic link where this machine lets a test make one, else (Windows) a junction.</summary>
    private static bool MakeLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            made?.WaitForExit(10000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // ---- the boot's part: a room waits for a put-away, and the next start puts back what a crash cut short ----

    private async Task TestABootAndAPutAway()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var wait = TeamBoot.LeaseWait;
        var root = Folder("boot");
        var layout = new TeamLayout(root);
        try
        {
            // A room that starts while its team's game is being put away is refused once it has waited, having read nothing.
            Craft(layout, "waiter", "challenge");
            TeamBoot.ForceGameBoot = true;
            TeamBoot.UserRootOverride = $"{Root}/boot";
            TeamBoot.ArgumentsOverride = new[] { "--team=waiter", "--mode=challenge" };
            TeamBoot.LeaseWait = TimeSpan.FromMilliseconds(150);
            var room = new Node { Name = "RoomStandIn" };
            AddChild(room);
            var before = Hashes(root);
            var words = "";
            using (PlayLease.TryAlone(layout, "waiter"))
            {
                try { TeamBoot.Prepare(room, Array.Empty<string>()); }
                catch (InvalidOperationException error) { words = error.Message; }
            }
            Check(words == PlayLease.BeingPutAway && room.GetChildCount() == 0 && Same(before, Hashes(root)),
                $"a room that starts while its team's game is being put away waits, then is refused (\"{words}\"), and has read and written nothing");
            // Once the put-away has let go the boot takes the lease to play, and New game is refused until the room has left.
            var boot = TeamBoot.Prepare(room, Array.Empty<string>());
            Check(boot.Lease is { Held: true, Alone: false, TeamId: "waiter" } && room.GetChildCount() == 1 && room.GetChild(0) is PlayLeaseKeeper && PlayLease.InUse(layout, "waiter") &&
                  Refused(() => GameSetAside.PutAway(layout, "waiter", "challenge", DateTime.UtcNow)) == PlayLease.StillOpen,
                "a boot holds its team's lease to play, kept by a node of the room, and New game is refused meanwhile");
            room.QueueFree();
            await Frames(2);
            Check(boot.Lease is { Held: false } && !PlayLease.InUse(layout, "waiter"), "the room gone, the lease is let go");
            TeamBoot.LeaseWait = wait;

            // A game half put away that cannot be put back: the room is refused. It never opens a fresh game over the old one.
            Craft(layout, "stuck", "challenge");
            var stuckGame = Hashes(layout.TeamFolder("stuck"));
            GameSetAside.AtStep = name => { if (name == "moved") throw new GameSetAside.PowerCut(); };
            try { GameSetAside.PutAway(layout, "stuck", "challenge", DateTime.UtcNow); }
            catch (GameSetAside.PowerCut) { }
            GameSetAside.AtStep = null;
            // Something stands where the game's folder must go home (a file of its name), so the put-back fails.
            var inTheWay = layout.ModeFolder("stuck", "challenge");
            File.WriteAllText(inTheWay, "in the way\n");
            TeamBoot.ArgumentsOverride = new[] { "--team=stuck", "--mode=challenge" };
            room = new Node { Name = "RoomStandInStuck" };
            AddChild(room);
            before = Hashes(root);
            words = "";
            try { TeamBoot.Prepare(room, Array.Empty<string>()); }
            catch (InvalidOperationException error) { words = error.Message; }
            Check(words == GameSetAside.NotPutBack && room.GetChildCount() == 0 && !PlayLease.InUse(layout, "stuck") && Same(before, Hashes(root)) &&
                  GameSetAside.PendingPutAway(layout, "stuck") == "challenge" && GameSetAside.HasGame(layout, "stuck", "challenge") && GameSetAside.List(layout, "stuck").Count == 0,
                $"a room that finds its team's game half put away and cannot put it back is refused (\"{words}\"): nothing is read or written, no lease is kept, and the game still waits to be put back");
            File.Delete(inTheWay);
            var back = TeamBoot.Prepare(room, Array.Empty<string>());
            Check(back.Lease is { Held: true } && back.Notices.Any(n => n.EndsWith("was put back", StringComparison.Ordinal)) && GameSetAside.PendingPutAway(layout, "stuck") == null &&
                  Same(stuckGame, Hashes(layout.TeamFolder("stuck"))) && GameSetAside.List(layout, "stuck").Count == 0 && Directory.GetDirectories(GameSetAside.Root(layout), "*-stuck-challenge").Length == 0,
                "with the way clear the next start puts the old game back whole, byte for byte, and then holds its lease to play: " + Differences(stuckGame, Hashes(layout.TeamFolder("stuck"))));
            room.QueueFree();
            await Frames(2);

            // A lease that cannot be opened at all is no put-away at work: the room boots at once, as it did before the lease,
            // with a notice and no hold (it does not wait five seconds and then say the game is being put away).
            Craft(layout, "nolease", "challenge");
            Directory.CreateDirectory(PlayLease.PathFor(layout, "nolease"));
            TeamBoot.ArgumentsOverride = new[] { "--team=nolease", "--mode=challenge" };
            room = new Node { Name = "RoomStandInNoLease" };
            AddChild(room);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var unheld = TeamBoot.Prepare(room, Array.Empty<string>());
            Check(unheld is { GameBoot: true, TeamId: "nolease", Mode: "challenge", Lease: null } && room.GetChildCount() == 0 && clock.Elapsed.TotalSeconds < 2 &&
                  unheld.Notices.Any(n => n.Contains("play lease could not be opened", StringComparison.Ordinal)),
                $"a room whose team's lease cannot be opened boots at once ({clock.Elapsed.TotalMilliseconds:0} ms) with a notice and no hold");
            room.QueueFree();
            await Frames(2);

            // The process ends part-way through New game; the next start puts the old game back and plays it.
            SeedTeam(layout, "pilot", file => file.WithPockets("challenge", InventoryContents.Empty.WithUnits("berries", 4)));
            var host = await Boot(root, "--team=pilot", "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            var hurt = host.ApplyPlayerDamage(25, PlayerDamageSource.Critter);
            var doused = host.PutOutCampfire(Start);
            await Unboot();
            GameSetAside.AtStep = name => { if (name == "emptied") throw new GameSetAside.PowerCut(); };
            var cut = false;
            try { GameSetAside.PutAway(layout, "pilot", "challenge", DateTime.UtcNow); }
            catch (GameSetAside.PowerCut) { cut = true; }
            GameSetAside.AtStep = null;
            var midway = cut && !Directory.Exists(layout.ModeFolder("pilot", "challenge")) && File.Exists(GameSetAside.NotePath(layout, "pilot")) &&
                         TeamFile.Parse(File.ReadAllBytes(layout.TeamFile("pilot"))).PocketsOf("challenge").IsEmpty;
            host = await Boot(root, "--team=pilot", "--mode=challenge", "--difficulty=standard");
            if (host == null) return;
            Check(hurt is { After: 75 } && doused && midway && host.Vitals.Health == 75 && !host.IsCampfireLit(Start) && host.PocketContents.Units("berries") == 4 &&
                  !File.Exists(GameSetAside.NotePath(layout, "pilot")) && GameSetAside.List(layout, "pilot").Count == 0,
                $"New game cut short by the process ending (the folder moved, the pockets emptied, no ledger): the next start puts the old game back and plays it (midway {midway}): " + Body(host));
            await Unboot();
        }
        finally
        {
            GameSetAside.AtStep = null;
            await Unboot();
            TeamBoot.LeaseWait = wait;
            TeamBoot.UserRootOverride = null;
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
        }
    }
}
