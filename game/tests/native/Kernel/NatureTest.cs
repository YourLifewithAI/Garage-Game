using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The island's nature (Grow, weather and Fire on the ground field; Bloom and its trail went with decision 52), in the test room (with plants, NatureTest.Grow) with the
/// real bodies and the real authority: the simulation's laws and bounds (NatureTest.Simulation), nature as room state and
/// its saved block (NatureTest.Persistence), the review's blockers and majors through the command path
/// (NatureTest.Review), and what the scene and the verbs make of fire (NatureTest.Scene). The ground under each cell is
/// a test sampler (soil everywhere), except where a check needs the room's physics. Pass "-- --dump=DIR" to write every
/// message and result for contracts/validate.py.
/// </summary>
public partial class NatureTest : Node3D
{
    private const string TestRoot = "user://tests/nature";
    private const string Player = CommandHost.PlayerPrincipal;
    private const string Companion = CommandHost.CompanionPrincipal;
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
    private DateTime _now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    public override async void _Ready()
    {
        try
        {
            _errors = new ScriptErrors();
            OS.AddLogger(_errors);
            _dump = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];
            if (_dump != null) System.IO.Directory.CreateDirectory(_dump);
            RemoveAll();
            _room = PlantedRoom();
            TestSimulation();
            TestBlock();
            await TestReview();
            await TestPersistence();
            await TestScene();
            await TestConsequences();
            await TestEnergy();
            await TestExpertise();
            await TestTrust();
            await TestCamps();
            await TestGrow();
            await TestGrowExpertise();
            // Fire's behaviours (NatureTest.FireKinds.cs): Fireline, Snuff, Will-o'-wisp, the place deciding.
            await TestFireKinds();
            // The Bubble's time (NatureTest.BubbleTime.cs, decisions 95 and 112): 8 s, a boat 20 s.
            await TestBubbleTime();
            await FreeWorld();
            OS.RemoveLogger(_errors);
            Check(_errors.Count == 0, $"no script or engine error while the suite ran ({_errors.Count}; first: {_errors.First})");
            GD.Print($"NATIVE_KERNEL_NATURE: {_checks - _failures}/{_checks} checks passed; the ground field's laws and bounds, time away, nature as room state (saved block, " +
                "throttle, rebuild, migration), receipts and undo by room revision (Creative only), the authority's checks, what a companion learns, the fire rule and its one ask, " +
                $"Grow (plants grown, saved, climbed and burnt), carried and burnt things, the Gubble's fire: consequences, guardrails and feedback, Trust (initiative, marks, burnt builds, bands, lessons), Fire's camp (start spots, resting, firepits; no Fire until the first rest, rain putting campfires out, firepits spread evenly), Grow's five expertise and the waterpad{(_dump != null ? $"; {_dumped} messages dumped" : "")}");
            RemoveAll();
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError("Nature test exception: " + error);
            GetTree().Quit(1);
        }
    }

    /// <summary>The review scenarios, each in a fresh room of its own (NatureTest.Review).</summary>
    private async Task TestReview()
    {
        await TestNatureIsRoomState();
        await TestReceiptsCompact();
        await TestUndoByRoomRevision();
        await TestAuthorityChecks();
        await TestUnseenStaysUnseen();
        await TestFireRule();
    }

    // ---- the world ----

    /// <summary>
    /// A fresh test room, its avatars at their spawns and a host saving to savePath, with the shipped rules and nature on.
    /// The host's own processing is off: each check advances the simulation itself, so nothing happens between checks.
    /// </summary>
    /// <param name="beforeRules">Runs as the host exists, before its rules load (a launch's vitals store: RoomWorld's order).</param>
    /// <param name="afterRules">Runs once the rules loaded, before nature and before any frame (a launch's knacks and set-mode).</param>
    /// <param name="learnFire">The team knows Fire, as after the first rest (decision 72), through the one helper every suite that lights fires uses.</param>
    private async Task NewWorld(string savePath, bool challenge = false, RoomData? roomOverride = null, Action? beforeRules = null, Action? afterRules = null, bool learnFire = true)
    {
        await FreeWorld();
        var room = roomOverride ?? _room;
        _world = new Node3D { Name = "World" };
        AddChild(_world);
        _world.AddChild(RoomBuilder.Build(room));
        await Frames(2);
        _player = new SmallPlayerController { Name = "Player", ReadKeyboard = false, Position = room.SpawnFor("player").PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_player);
        _companion = new CompanionAvatar { Name = "Companion", Position = room.SpawnFor("companion", room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_companion);
        _companion.BindPlayer(_player);
        _companion.Stay();
        _host = CommandHost.Create(_world, room, _player, _companion, null, savePath);
        _host.Clock = () => _now;
        _host.TeamSightIntervalS = 0;
        _host.SetProcess(false);
        _host.SetPhysicsProcess(false);
        if (challenge) _host.ConfigurePlayerVitals(new ChallengeVitals());
        beforeRules?.Invoke();
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        afterRules?.Invoke();
        _host.ConfigureNature();
        if (learnFire) _host.LearnFireForTests();
        _host.NatureSampleForTests = Soil;
        // The player's own "Let the Gubble use fire" (on by default), whatever a profile on this machine says.
        _host.SetGubbleFire(true);
        await Frames(4);
    }

    private async Task FreeWorld()
    {
        if (_world == null || !IsInstanceValid(_world)) return;
        _world.QueueFree();
        _world = null;
        await Frames(3);
    }

    private static string SavePath(string name) => $"{TestRoot}/{name}/inventions.json";

    /// <summary>Soil everywhere at floor height: grass, fertile, dry enough to burn.</summary>
    private static NatureCell? Soil(int x, int z) => new() { X = x, Z = z, Y = 0, Surface = "shell:soil", Material = "grass", Fertile = true, Fuel = 0.7f, Moisture = 0.35f };

    /// <summary>Loads the Challenge mode, whose consequences make creations scorch and fire hurt.</summary>
    private sealed class ChallengeVitals : IPlayerVitalsStore
    {
        public PlayerModesProfile Load() => PlayerModesProfile.Fresh.Select("challenge");
        public void Save(PlayerModesProfile profile) { }
    }

    /// <summary>Advances the simulation by whole steps, as the physics tick does.</summary>
    private void Steps(int count)
    {
        for (var i = 0; i < count; i++) _host.AdvanceNature(0.5);
    }

    // ---- messages ----

    private string NextId(string prefix) => $"{prefix}-{++_ids}";

    private JsonObject Command(string actionId, string op, JsonObject args, bool preview = false, int? expectedRevision = null)
    {
        var command = new JsonObject { ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = actionId, ["room_id"] = _room.RoomId, ["op"] = op, ["args"] = args };
        if (preview) command["preview"] = true;
        if (expectedRevision != null) command["expected_revision"] = expectedRevision;
        return command;
    }

    private JsonObject Query(string op, JsonObject args) =>
        new() { ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = NextId("q"), ["room_id"] = _room.RoomId, ["op"] = op, ["args"] = args };

    /// <summary>Sends a message as principal; contractValid false keeps a deliberately malformed message out of the dump.</summary>
    private JsonObject Send(JsonObject message, string principal, bool contractValid = true)
    {
        _now += TimeSpan.FromMilliseconds(50);
        var result = JsonNode.Parse(_host.Handle(message.ToJsonString(), principal))!.AsObject();
        if (_dump != null)
        {
            var index = ++_dumped;
            if (contractValid) System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, $"{index:0000}_message.json"), CanonicalJson.Bytes(message));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, $"{index:0000}_result.json"), CanonicalJson.Bytes(result));
        }
        return result;
    }

    private static JsonObject Cast(string capability, Vector3 center, double radius, double duration, string? previewId = null)
    {
        var args = new JsonObject { ["capability"] = capability, ["center_m"] = new JsonArray(center.X, center.Y, center.Z), ["radius_m"] = radius, ["duration_s"] = duration };
        if (previewId != null) args["preview_id"] = previewId;
        return args;
    }

    /// <summary>Previews a fire and commits exactly that preview: the commit's result (held, refused or lit).</summary>
    private JsonObject Fire(string principal, Vector3 center, double radius, double duration, out JsonObject preview)
    {
        preview = Send(Command(NextId("fire-preview"), "nature.cast", Cast("fire", center, radius, duration), preview: true), principal);
        var token = preview["data"]?["preview_id"]?.GetValue<string>();
        return Send(Command(NextId("fire"), "nature.cast", Cast("fire", center, radius, duration, token)), principal);
    }

    private static JsonObject Source(string name, string material = "wood") => new()
    {
        ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = name, ["seed"] = 1, ["mount"] = "ground",
        ["parts"] = new JsonArray(new JsonObject
        {
            ["id"] = "base", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.06, 0), ["rotation_deg"] = new JsonArray(0, 0, 0),
            ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = material,
        }),
        ["nodes"] = new JsonArray(new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "base", ["params"] = new JsonObject() }),
        ["edges"] = new JsonArray(),
    };

    /// <summary>The player places a wooden creation at a spot on the floor; its id, or "".</summary>
    private string Build(string name, double x, double z)
    {
        var made = Send(Command(NextId("build"), "creation.place", new JsonObject { ["source"] = Source(name), ["placement"] = new JsonObject { ["position_m"] = new JsonArray(x, 0, z) } }), Player);
        return made["created"]?[0]?.GetValue<string>() ?? "";
    }

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;
    private static string? Code(JsonObject result) => result["error"]?["code"]?.GetValue<string>();
    private static string Canonical(JsonNode? node) => node == null ? "" : CanonicalJson.Text(node);

    private static JsonObject Unsessioned(JsonObject state)
    {
        var copy = (JsonObject)state.DeepClone();
        copy.Remove("session");
        return copy;
    }

    private void RemoveAll()
    {
        var directory = ProjectSettings.GlobalizePath(TestRoot);
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
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
        GD.PushError("Nature: " + label);
    }

    /// <summary>Counts every script or engine error while the suite runs, so one fails the suite itself.</summary>
    private sealed partial class ScriptErrors : Logger
    {
        private readonly object _lock = new();
        public int Count { get; private set; }
        public string First { get; private set; } = "";

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            // Warnings and this suite's own failed checks (push_error) are reported elsewhere.
            if (errorType == (int)ErrorType.Warning || function == "push_error" || code.StartsWith("Nature", StringComparison.Ordinal)) return;
            lock (_lock)
            {
                Count++;
                if (First.Length == 0) First = $"{file}:{line} {code} {rationale}";
            }
        }
    }
}
