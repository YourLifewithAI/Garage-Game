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
using GarageGame.Native.Room;
using GarageGame.Native.Saves;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// P-Food (docs/game/MECHANICS.md decisions 92, 98, 101, 108, 111 and 113; D1 in docs/runs/RUN-2-PLAYTEST-ROUND-2.md) through
/// garage_game.command in the test room with plants (two broadleaf trees, a shrub, a heather clump and a grass tuft), the real bodies,
/// the real authority, nature on and a real team file: the pack's food block and its refusals, the deterministic layout, picking into
/// the pockets (E's reader and key, the canopy reached with a hop, full pockets, the companion refused and shown no food), eating (at
/// once, at full hearts, Creative's sound), growing back on the island clock, Grow ripening, fire burning the food and replanting
/// returning it, a reload and room state keeping it, the look's budget, and P-Inventory-2's loose units and seeds (dropped and picked up again with
/// E or F, saved and capped, burnt; an apple's seed, berries' one in three, a harvest's). With "-- --landscape=DIR" (the 1x garage fixture) the layout
/// is also checked on the stand-in garage; "-- --dump=DIR" writes every message and result for contracts/validate.py.
/// </summary>
public partial class FoodTest : Node3D
{
    private const string TestRoot = "user://tests/food";
    private const string Player = CommandHost.PlayerPrincipal;
    private const string Companion = CommandHost.CompanionPrincipal;
    private const string TeamId = "picker";
    private const string TreeA = "plant:broadleaf-0";
    private const string TreeB = "plant:broadleaf-1";
    private const string Shrub = "plant:shrub-0";
    private const string Heather = "plant:heather-0";
    private const string Tuft = "plant:grass-0";
    /// <summary>Tree A in the corner clear of the spawns and the builds; tree B across the room from it, for fire; the bushes on the far side.</summary>
    private static readonly Vector3 TreeAAt = new(1.4f, 0, -0.9f);
    private static readonly Vector3 TreeBAt = new(1.5f, 0, 1.0f);
    private static readonly Vector3 ShrubAt = new(-1.4f, 0, 0.9f);
    private static readonly Vector3 HeatherAt = new(-1.4f, 0, 1.25f);
    private static readonly Vector3 TuftAt = new(-1.0f, 0, 0.9f);
    private int _checks;
    private int _failures;
    private int _ids;
    private int _dumped;
    private string? _dump;
    private string? _landscape;
    private ScriptErrors? _errors;
    private RoomData _room = null!;
    private Node3D? _world;
    private CommandHost _host = null!;
    private SmallPlayerController _player = null!;
    private CompanionAvatar _companion = null!;
    private VitalsStore _vitals = null!;
    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _sounds = new();

    public override async void _Ready()
    {
        try
        {
            _errors = new ScriptErrors();
            OS.AddLogger(_errors);
            var args = OS.GetCmdlineUserArgs();
            _dump = args.FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];
            _landscape = args.FirstOrDefault(a => a.StartsWith("--landscape=", StringComparison.Ordinal))?["--landscape=".Length..];
            if (_dump != null) Directory.CreateDirectory(_dump);
            RemoveAll();
            _room = FoodRoom();
            TestRules();
            TestLayout();
            TestGarage();
            await TestPick();
            await TestCanopyWithAHop();
            await TestFullPockets();
            await TestCompanion();
            await TestEat();
            await TestRegrow();
            await TestRegrowOneADay();
            await TestGrowRipens();
            await TestFireAndReplant();
            await TestReload();
            await TestView();
            await TestLooseDropAndPickUp();
            TestLooseCap();
            await TestLooseBurns();
            TestSeedRule();
            await FreeWorld();
            OS.RemoveLogger(_errors);
            Check(_errors.Count == 0, $"no script or engine error while the suite ran ({_errors.Count}; first: {_errors.First})");
            GD.Print($"NATIVE_KERNEL_FOOD: {_checks - _failures}/{_checks} checks passed; the pack's food block, the layout from the plant's id{(_landscape != null ? " (and on the garage)" : "")}, picking into the pockets " +
                "(E, a hop for the canopy, full pockets, the companion refused and shown no food), eating (at once, full hearts, Creative's sound), growing back by the island clock, Grow " +
                $"ripening, fire and replanting, a reload and room state, the look's budget{(_dump != null ? $"; {_dumped} messages dumped" : "")}");
            RemoveAll();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError("Food test exception: " + error);
            GetTree().Quit(1);
        }
    }

    // ---- the world ----

    /// <summary>The vitals store of a fixture: every write kept, in memory.</summary>
    private sealed class VitalsStore(PlayerModesProfile profile) : IPlayerVitalsStore
    {
        public PlayerModesProfile Saved { get; private set; } = profile;
        public int Writes { get; private set; }
        public PlayerModesProfile Load() => Saved;
        public void Save(PlayerModesProfile next)
        {
            Saved = next;
            Writes++;
        }
    }

    /// <summary>
    /// A fresh room with its plants in a mode, its avatars at their spawns, a host saving to the named save, storybook_wild v5 with every
    /// plant of the food species bearing (one_in 1; editPack edits the pack further), nature on (fire learned, soil everywhere), the mode's
    /// vitals in memory and a real team file. The host's processing is off; FoodSound is listened to.
    /// </summary>
    private async Task NewWorld(string name, string mode = "challenge", Action<JsonObject>? editPack = null, bool keepFiles = false, bool everyPlant = true)
    {
        await FreeWorld();
        if (!keepFiles)
        {
            var folder = ProjectSettings.GlobalizePath($"{TestRoot}/{name}");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        _world = new Node3D { Name = "World" };
        AddChild(_world);
        _world.AddChild(RoomBuilder.Build(_room));
        await Frames(2);
        _player = new SmallPlayerController { Name = "Player", ReadKeyboard = false, Position = _room.SpawnFor("player").PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_player);
        _companion = new CompanionAvatar { Name = "Companion", Position = _room.SpawnFor("companion", _room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_companion);
        _companion.BindPlayer(_player);
        _companion.Stay();
        _host = CommandHost.Create(_world, _room, _player, _companion, null, SavePath(name));
        _host.Clock = () => _now;
        _host.TeamSightIntervalS = 0;
        _host.SetProcess(false);
        _host.SetPhysicsProcess(false);
        _host.UseRules(Pack(pack =>
        {
            if (everyPlant) foreach (var source in pack["food"]!["sources"]!.AsArray()) source!["one_in"] = 1;
            editPack?.Invoke(pack);
        }));
        var rules = VitalsRules.For(_host.Rules, mode);
        _vitals = new VitalsStore(PlayerModesProfile.Restore(mode, PlayerModesProfile.Modes.ToDictionary(m => m, m => rules.Fresh()), null));
        _host.ConfigurePlayerVitals(_vitals);
        var teams = new TeamStore(TeamPath(name), TeamId);
        if (teams.Load() == null) teams.Save(TeamFile.Create(TeamId, _now, "Player", TeamFile.PrototypeCostume, "Gubble", "#65b9b0"));
        _host.UseTeam(teams);
        _host.ConfigureNature();
        _host.LearnFireForTests();
        _host.NatureSampleForTests = Soil;
        _host.SetGubbleFire(true);
        _host.FoodSound += kind => _sounds.Add(kind);
        await Frames(4);
    }

    private async Task FreeWorld()
    {
        if (_world == null || !IsInstanceValid(_world)) return;
        _host.UnbindPlayerVitals();
        _world.QueueFree();
        _world = null;
        await Frames(3);
    }

    private static string SavePath(string name) => $"{TestRoot}/{name}/save/inventions.json";
    private static string TeamPath(string name) => ProjectSettings.GlobalizePath($"{TestRoot}/{name}/teams/{TeamId}/team.json");

    /// <summary>Soil everywhere at floor height: grass, fertile, dry enough to burn.</summary>
    private static NatureCell? Soil(int x, int z) => new() { X = x, Z = z, Y = 0, Surface = "shell:soil", Material = "grass", Fertile = true, Fuel = 0.7f, Moisture = 0.35f };

    /// <summary>The shipped v5 (its food block included), edited.</summary>
    private static IslandRules Pack(Action<JsonObject>? edit)
    {
        var pack = ShippedPack();
        edit?.Invoke(pack);
        return IslandRules.Parse(Encoding.UTF8.GetBytes(pack.ToJsonString()));
    }

    private static JsonObject ShippedPack() =>
        JsonNode.Parse(Godot.FileAccess.GetFileAsString($"res://rules/{RoomWorld.DefaultRulesId}/v{ShippedRules.Version}.json"))!.AsObject();

    /// <summary>The test room with two broadleaf trees, a shrub 12 cm tall, a heather clump 2.5 cm tall and a grass tuft (which bears nothing).</summary>
    private static RoomData FoodRoom()
    {
        var trees = new[] { IslandFixture.Broadleaf(TreeAAt.X, TreeAAt.Z, 0, 0, 41), IslandFixture.Broadleaf(TreeBAt.X, TreeBAt.Z, 0, 0, 42) };
        // The example's grass mesh stands 0.13 m: a bush's height is its y scale of it.
        IslandFixture.Row Bush(Vector3 at, float top, uint seed) => new(at.X, 0, at.Z, 0, new Vector3(1, top / 0.13f, 1), new Color(0.8f, 0.9f, 0.7f), top, 0, 0, seed);
        IslandFixture.SetSpec Plants(string name, string fuel, IslandFixture.Row row) =>
            new(name, name, "plant", name, "none", fuel, true, "plants/grass.glb", 12, new[] { ("plants/grass_far.glb", 2) }, 0, new[] { row });
        var directory = IslandFixture.WithPlants(RoomWorld.DefaultRoom, $"{TestRoot}/room/test_room", new[]
        {
            IslandFixture.Trees(trees), Plants("shrub", "shrub", Bush(ShrubAt, 0.12f, 51)), Plants("heather", "herb", Bush(HeatherAt, 0.025f, 52)),
            IslandFixture.Grass("grass", new[] { Bush(TuftAt, 0.13f, 53) }),
        });
        return RoomData.Load(directory);
    }

    // ---- the bodies ----

    /// <summary>Stands the player at a spot, turned to face a point (the body faces -Z), and lets it settle.</summary>
    private async Task Face(Vector3 at, Vector3 toward, string where)
    {
        _player.SetControlInput(Vector2.Zero);
        Check(_player.TryTeleportTo(at), "the player stands " + where);
        var way = toward - at;
        _player.Rotation = new Vector3(0, Mathf.Atan2(-way.X, -way.Z), 0);
        await Frames(4);
    }

    /// <summary>The spot a unit of a plant's food lies at now (its bottom centre) and the spot to stand facing it from, 6 cm short on the way from the stem.</summary>
    private (Vector3 Food, Vector3 Stand) Approach(string plant, int spot, float back = 0.06f)
    {
        var food = Layout(plant)!;
        var at = food.At(spot, 1);
        var away = new Vector3(at.X - food.Root.X, 0, at.Z - food.Root.Z);
        away = away.LengthSquared() < 1e-8f ? Vector3.Back : away.Normalized();
        return (at, new Vector3(at.X, 0, at.Z) + away * back);
    }

    private FoodPlant? Layout(string plant) => _room.Copies!.TryResolve(plant, out var set, out var row) && _host.FoodRulesNow is { } rules ? FoodLayout.Of(set, row, rules) : null;

    private IReadOnlyList<int> Spots(string plant, FoodPlace place) =>
        Layout(plant) is { } food ? Enumerable.Range(0, food.Spots.Count).Where(i => food.Spots[i].Place == place).ToList() : Array.Empty<int>();

    // ---- messages ----

    private string NextId(string prefix) => $"{prefix}-{++_ids}";

    private JsonObject Command(string actionId, string op, JsonObject args, bool preview = false)
    {
        var command = new JsonObject { ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = actionId, ["room_id"] = _room.RoomId, ["op"] = op, ["args"] = args };
        if (preview) command["preview"] = true;
        return command;
    }

    private JsonObject Query(string op, JsonObject args) =>
        new() { ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = NextId("q"), ["room_id"] = _room.RoomId, ["op"] = op, ["args"] = args };

    /// <summary>Sends a message as principal; contractValid false keeps a deliberately malformed message out of the dump.</summary>
    private JsonObject Send(JsonObject message, string principal, bool contractValid = true)
    {
        _now += TimeSpan.FromMilliseconds(50);
        var result = JsonNode.Parse(_host.Handle(message.ToJsonString(), principal))!.AsObject();
        Dump(contractValid ? message : null, result);
        return result;
    }

    /// <summary>A result the HUD's own call made (the player's command was built by the host): its result dumped alone.</summary>
    private JsonObject Dumped(JsonObject result)
    {
        Dump(null, result);
        return result;
    }

    private void Dump(JsonObject? message, JsonObject result)
    {
        if (_dump == null) return;
        var index = ++_dumped;
        if (message != null) File.WriteAllBytes(Path.Combine(_dump, $"{index:0000}_message.json"), CanonicalJson.Bytes(message));
        File.WriteAllBytes(Path.Combine(_dump, $"{index:0000}_result.json"), CanonicalJson.Bytes(result));
    }

    private JsonObject Pick(string principal, string plant, bool preview = false) =>
        Send(Command(NextId("pick"), "item.pickup", new JsonObject { ["target"] = plant }, preview), principal);

    private JsonObject Use(string principal, string item) => Send(Command(NextId("use"), "inventory.use", new JsonObject { ["item"] = item }), principal);

    private JsonObject GrowCast(string principal, Vector3 at, double radius, bool preview = false) =>
        Send(Command(NextId("grow"), "nature.cast", new JsonObject
        {
            ["capability"] = "grow", ["center_m"] = new JsonArray(at.X, at.Y, at.Z), ["radius_m"] = radius, ["duration_s"] = 1,
        }, preview), principal);

    /// <summary>Previews a fire and commits exactly that preview: the commit's result.</summary>
    private JsonObject Fire(Vector3 center, double radius, double duration)
    {
        var args = new JsonObject { ["capability"] = "fire", ["center_m"] = new JsonArray(center.X, center.Y, center.Z), ["radius_m"] = radius, ["duration_s"] = duration };
        var preview = Send(Command(NextId("fire-preview"), "nature.cast", (JsonObject)args.DeepClone(), preview: true), Player);
        args["preview_id"] = preview["data"]?["preview_id"]?.GetValue<string>();
        return Send(Command(NextId("fire"), "nature.cast", args), Player);
    }

    private void Steps(int count)
    {
        for (var i = 0; i < count; i++) _host.AdvanceNature(0.5);
    }

    private int Units(string kind) => _host.PocketEntries.FirstOrDefault(e => e.Item == kind)?.Count ?? 0;

    private int Quarters => VitalsRules.For(_host.Rules, _host.Mode).Quarters(_host.Vitals);

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;
    private static string? Code(JsonObject result) => result["error"]?["code"]?.GetValue<string>();
    private static string? Message(JsonObject result) => result["error"]?["message"]?.GetValue<string>();
    private static string? FieldPath(JsonObject result) => result["error"]?["field_path"]?.GetValue<string>();
    private static string Canonical(JsonNode? node) => node == null ? "" : CanonicalJson.Text(node);
    private static bool Refused(JsonObject result, string code, string message, string path) => Code(result) == code && Message(result) == message && FieldPath(result) == path;

    private void RemoveAll()
    {
        var directory = ProjectSettings.GlobalizePath(TestRoot);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Food: " + label);
    }

    /// <summary>Counts every script or engine error while the suite runs, so one fails the suite itself.</summary>
    private sealed partial class ScriptErrors : Logger
    {
        private readonly object _lock = new();
        public int Count { get; private set; }
        public string First { get; private set; } = "";

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if (errorType == (int)ErrorType.Warning || function == "push_error" || code.StartsWith("Food", StringComparison.Ordinal)) return;
            lock (_lock)
            {
                Count++;
                if (First.Length == 0) First = $"{file}:{line} {code} {rationale}";
            }
        }
    }
}
