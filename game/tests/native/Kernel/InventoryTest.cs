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
/// P-Inventory's pockets and chest (docs/game/MECHANICS.md decisions 98 to 103, 108, 111 and 113) through garage_game.command in the test
/// room, with the real bodies, the real authority and a real team file: the pack's block, the caps, the pockets per mode, the chest's save
/// and load and the Creative copy's strip, every companion refusal and the team switch "Let the Gubble use the chest", the loss by mode,
/// the instant heal, a unique thing's id round trip, the readers and E's choice. The room adds three windfall apples, a seed pod and a cup
/// (InventoryTest.Room). Pass "-- --dump=DIR" to write every message and result for contracts/validate.py.
/// </summary>
public partial class InventoryTest : Node3D
{
    private const string TestRoot = "user://tests/inventory";
    private const string Player = CommandHost.PlayerPrincipal;
    private const string Companion = CommandHost.CompanionPrincipal;
    private const string PlayerAvatar = CommandHost.PlayerAvatar;
    private const string CompanionAvatar = CommandHost.CompanionAvatarId;
    private const string TeamId = "carrier";
    private int _checks;
    private int _failures;
    private int _ids;
    private int _dumped;
    private string? _dump;
    private ScriptErrors? _errors;
    private RoomData _room = null!;
    private Node3D? _world;
    private CommandHost _host = null!;
    private SmallPlayerController _player = null!;
    private CompanionAvatar _companion = null!;
    private VitalsStore _vitals = null!;
    private TeamStore? _teamStore;
    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    public override async void _Ready()
    {
        try
        {
            _errors = new ScriptErrors();
            OS.AddLogger(_errors);
            _dump = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];
            if (_dump != null) Directory.CreateDirectory(_dump);
            RemoveAll();
            _room = InventoryRoom();
            await TestRules();
            await TestCompanionRefusals();
            await TestThingRoundTrip();
            await TestChestSavedAndReconciled();
            await TestStacksAndCaps();
            await TestPocketsPerMode();
            await TestLossByMode();
            await TestInstantHeal();
            await TestGubbleChestSwitch();
            await TestCreativeCopyStrip();
            await TestReaders();
            await FreeWorld();
            OS.RemoveLogger(_errors);
            Check(_errors.Count == 0, $"no script or engine error while the suite ran ({_errors.Count}; first: {_errors.First})");
            GD.Print($"NATIVE_KERNEL_INVENTORY: {_checks - _failures}/{_checks} checks passed; the pack's inventory block, the pockets per mode and their caps, the chest saved, " +
                "reconciled and stripped from the Creative copy, every companion refusal and the Gubble's chest switch, the loss by mode, the instant heal, a unique thing's id " +
                $"round trip, the readers and E's choice{(_dump != null ? $"; {_dumped} messages dumped" : "")}");
            RemoveAll();
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError("Inventory test exception: " + error);
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
    /// A fresh test room in a mode, its avatars at their spawns (the starting campfire 0.2 m before the player: the camp), a host saving to
    /// the named save, storybook_wild v5 with the windfall apples and the seed pod as units of their kinds (editPack edits the inventory
    /// block further), the mode's vitals in memory and, with team, a real team file in the save's folder. The host's processing is off.
    /// </summary>
    private async Task NewWorld(string name, string mode = "challenge", bool team = true, Action<JsonObject>? editPack = null, bool keepFiles = false)
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
        _host.UseRules(Pack(editPack));
        var rules = VitalsRules.For(_host.Rules, mode);
        _vitals = new VitalsStore(PlayerModesProfile.Restore(mode, PlayerModesProfile.Modes.ToDictionary(m => m, m => rules.Fresh()), null));
        _host.ConfigurePlayerVitals(_vitals);
        _teamStore = null;
        if (team)
        {
            _teamStore = new TeamStore(TeamPath(name), TeamId);
            if (_teamStore.Load() == null) _teamStore.Save(TeamFile.Create(TeamId, _now, "Player", TeamFile.PrototypeCostume, "Gubble", "#65b9b0"));
            _host.UseTeam(_teamStore);
        }
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

    /// <summary>The shipped v5, its inventory block made to know this room's windfalls (P-Food's job in the game), then edited.</summary>
    private static IslandRules Pack(Action<JsonObject>? edit)
    {
        var pack = JsonNode.Parse(Godot.FileAccess.GetFileAsString($"res://rules/{RoomWorld.DefaultRulesId}/v{ShippedRules.Version}.json"))!.AsObject();
        var kinds = pack["inventory"]!["kinds"]!.AsArray();
        kinds.First(k => k!["id"]!.GetValue<string>() == "apple")!["assets"] = new JsonArray("windfall_apple");
        kinds.First(k => k!["id"]!.GetValue<string>() == "seed")!["assets"] = new JsonArray("seed_pod");
        edit?.Invoke(pack["inventory"]!.AsObject());
        return IslandRules.Parse(Encoding.UTF8.GetBytes(pack.ToJsonString()));
    }

    /// <summary>
    /// The test room with three windfall apples (0.02 kg, a unit of the apple kind once a pack says so), a seed pod (a seed) and a cup
    /// (0.05 kg, a unique thing), on the rug about the spawn: the apples and the pod east of it, the cup west, all within the camp's 0.5 m.
    /// </summary>
    private static RoomData InventoryRoom()
    {
        var source = ProjectSettings.GlobalizePath(RoomWorld.DefaultRoom);
        var target = ProjectSettings.GlobalizePath($"{TestRoot}/room/test_room");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
        }
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "room.json")))!.AsObject();
        var files = manifest["files"]!.AsArray();
        var objects = manifest["objects"]!.AsArray();
        var assets = new[]
        {
            ("windfall_apple", "Apple", "fallen apple", 0.02, new[] { 0.025, 0.025, 0.025 }, "foliage"),
            ("seed_pod", "Seed pod", "seed pod", 0.001, new[] { 0.012, 0.012, 0.012 }, "foliage"),
            ("cup_proxy", "Cup", "cup", 0.05, new[] { 0.03, 0.04, 0.03 }, "ceramic"),
        };
        foreach (var (asset, name, category, mass, size, role) in assets)
        {
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "objects/doorstop_proxy/asset.json")))!.AsObject();
            record["asset_id"] = asset;
            record["display_name"] = name;
            record["category"] = category;
            record["category_group"] = "decor";
            record["dimensions_m"] = new JsonArray(size.Select(v => (JsonNode?)v).ToArray());
            record["physics"] = new JsonObject { ["movable"] = true, ["mass_kg"] = mass };
            record["materials"] = new JsonArray(new JsonObject { ["slot"] = "body", ["role"] = role, ["base_color"] = "#b84a32" });
            record["affordances"] = new JsonArray();
            var bytes = Encoding.UTF8.GetBytes(record.ToJsonString());
            var relative = $"objects/{asset}/asset.json";
            Directory.CreateDirectory(Path.Combine(target, "objects", asset));
            File.WriteAllBytes(Path.Combine(target, relative), bytes);
            files.Add(new JsonObject { ["path"] = relative, ["sha256"] = CanonicalJson.Sha256Hex(bytes), ["bytes"] = bytes.Length });
        }
        // On the rug, as the doorstop is (the rug covers the camp's clearing, 6 mm high).
        foreach (var (id, asset, x, z) in new[] { ("obj:apple_1", "windfall_apple", 0.3, 0.35), ("obj:apple_2", "windfall_apple", 0.3, 0.45), ("obj:apple_3", "windfall_apple", 0.3, 0.55),
                     ("obj:pod", "seed_pod", 0.3, 0.65), ("obj:cup", "cup_proxy", -0.2, 0.75) })
            objects.Add(new JsonObject
            {
                ["id"] = id, ["asset"] = $"objects/{asset}/asset.json",
                ["transform"] = new JsonObject { ["position_m"] = new JsonArray(x, 0.006, z), ["rotation"] = new JsonArray(0.0, 0.0, 0.0, 1.0) },
                ["support"] = new JsonObject { ["kind"] = "object", ["target_id"] = "obj:rug" },
            });
        File.WriteAllText(Path.Combine(target, "room.json"), manifest.ToJsonString(), new UTF8Encoding(false));
        return RoomData.Load($"{TestRoot}/room/test_room");
    }

    // ---- the bodies ----

    private async Task Stand(SmallPlayerController body, Vector3 at, string where)
    {
        body.SetControlInput(Vector2.Zero);
        Check(body.TryTeleportTo(at), $"{body.Name} stands {where}");
        await Frames(4);
    }

    /// <summary>The player picks a room object up (standing beside it first), as the F key's entity.grab: true when it is in hand.</summary>
    private async Task<bool> PickUp(string id, Vector3 beside)
    {
        await Stand(_player, beside, "beside " + id);
        var grabbed = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = id }), Player);
        return Ok(grabbed) && _host.HeldBy(PlayerAvatar) == id;
    }

    private static readonly Vector3 AtCamp = new(0.1f, 0, 0.62f);
    private static readonly Vector3 ByApple1 = new(0.24f, 0, 0.35f);
    private static readonly Vector3 ByApple2 = new(0.24f, 0, 0.45f);
    private static readonly Vector3 ByApple3 = new(0.24f, 0, 0.55f);
    private static readonly Vector3 ByPod = new(0.24f, 0, 0.65f);
    private static readonly Vector3 ByCup = new(-0.13f, 0, 0.75f);
    private static readonly Vector3 ByDoorstop = new(-0.3f, 0, 0.62f);
    private static readonly Vector3 AwayFromCamp = new(1.5f, 0, -0.5f);

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

    private JsonObject Send(JsonObject message, string principal, bool contractValid = true)
    {
        _now += TimeSpan.FromMilliseconds(50);
        var result = JsonNode.Parse(_host.Handle(message.ToJsonString(), principal))!.AsObject();
        if (_dump != null)
        {
            var index = ++_dumped;
            if (contractValid) File.WriteAllBytes(Path.Combine(_dump, $"{index:0000}_message.json"), CanonicalJson.Bytes(message));
            File.WriteAllBytes(Path.Combine(_dump, $"{index:0000}_result.json"), CanonicalJson.Bytes(result));
        }
        return result;
    }

    private JsonObject Store(string principal, string to, string? from = null, string? item = null, int? count = null, bool preview = false)
    {
        var args = new JsonObject { ["to"] = to };
        if (from != null) args["from"] = from;
        if (item != null) args["item"] = item;
        if (count != null) args["count"] = count.Value;
        return Send(Command(NextId("store"), "inventory.store", args, preview), principal);
    }

    private JsonObject Take(string principal, string item, string to, int? count = null, bool preview = false)
    {
        var args = new JsonObject { ["item"] = item, ["to"] = to };
        if (count != null) args["count"] = count.Value;
        return Send(Command(NextId("take"), "inventory.take", args, preview), principal);
    }

    private JsonObject Use(string principal, string item, bool preview = false) => Send(Command(NextId("use"), "inventory.use", new JsonObject { ["item"] = item }, preview), principal);
    private JsonObject Drop(string principal, string item, bool preview = false) => Send(Command(NextId("drop"), "inventory.drop", new JsonObject { ["item"] = item }, preview), principal);

    private JsonObject Status(string principal = Player) => Send(Query("player.status", new JsonObject()), principal)["data"]!.AsObject();

    private IReadOnlyList<string> EntityIds() =>
        Send(Query("entities.list", new JsonObject { ["limit"] = 100 }), Player)["data"]!["items"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToList();

    private int Units(IReadOnlyList<InventoryEntryView> entries, string kind) => entries.FirstOrDefault(e => e.Item == kind)?.Count ?? 0;

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;
    private static string? Code(JsonObject result) => result["error"]?["code"]?.GetValue<string>();
    private static string? Message(JsonObject result) => result["error"]?["message"]?.GetValue<string>();
    private static string? FieldPath(JsonObject result) => result["error"]?["field_path"]?.GetValue<string>();
    private static string Canonical(JsonNode? node) => node == null ? "" : CanonicalJson.Text(node);

    private static bool Refused(JsonObject result, string code, string message, string path) => Code(result) == code && Message(result) == message && FieldPath(result) == path;

    private int Quarters => _host.Rules!.Traits!.Hearts == null ? 0 : VitalsRules.For(_host.Rules, _host.Mode).Quarters(_host.Vitals);

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
        GD.PushError("Inventory: " + label);
    }

    /// <summary>Counts every script or engine error while the suite runs, so one fails the suite itself.</summary>
    private sealed partial class ScriptErrors : Logger
    {
        private readonly object _lock = new();
        public int Count { get; private set; }
        public string First { get; private set; } = "";

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if (errorType == (int)ErrorType.Warning || function == "push_error" || code.StartsWith("Inventory", StringComparison.Ordinal)) return;
            lock (_lock)
            {
                Count++;
                if (First.Length == 0) First = $"{file}:{line} {code} {rationale}";
            }
        }
    }
}
