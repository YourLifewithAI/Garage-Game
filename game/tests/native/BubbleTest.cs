using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// The Bubble as the founder redesigned it (decisions 55 to 60; M9 and its follow-up of docs/runs/RUN-2-MECHANICS.md; Kernel/Bubble,
/// SmallPlayerController.Bubble.cs) in the real test room with the real bodies and host, under storybook_wild v5's prices, with a 3 cm
/// sheet of shallow water laid on the floor west of the player and a deep pool by the rug: the place deciding (effect.start), the
/// consent (the ghost and its lead, the walk-in), every refusal, the price by what the place made and by principal, the pops and the
/// receipts, one bubble at a time and the room's cap (BubbleTest.cs); the ride, the deflate, the walk-in, the boat, the security row
/// and the budgets (BubbleTest.Body.cs); the Gubble's lead, the lift and the HUD's view (BubbleTest.Lead.cs). The security notes' row
/// (docs/companion/SECURITY.md, "the companion casting on, or moving, the player's avatar") has its tests named for it. Pass
/// "-- --dump=DIR" to write every message and result for contracts/validate.py, and "-- --verbose" to print every check.
/// </summary>
public partial class BubbleTest : Node3D
{
    private const string TestRoot = "user://tests/bubble";
    private const string Player = CommandHost.PlayerPrincipal;
    private const string Companion = CommandHost.CompanionPrincipal;
    private const double Tick = 1.0 / 60.0;
    /// <summary>The shallow water's surface (3 cm over the floor) and its extent, west of the player's spawn.</summary>
    private const float WaterY = 0.03f;
    private static readonly Vector2 WaterMin = new(-1.3f, 0.1f), WaterMax = new(-0.35f, 1.3f);
    /// <summary>A deep pool by the rug: its surface 15 cm over the floor, its bed (deeper than the body swims in, 8 cm).</summary>
    private const float PoolY = 0.15f;
    private static readonly Vector2 PoolMin = new(0.5f, 0.9f), PoolMax = new(1.1f, 1.4f);
    /// <summary>The test room's box (obj:box, 0.35 by 0.3 by 0.35 m at x 1.1, z 0.2): its top is the higher spot a lift floats up to.</summary>
    private static readonly Vector3 BoxTop = new(1.1f, 0.3f, 0.2f);
    private int _checks;
    private int _failures;
    private int _ids;
    private int _dumped;
    private string? _dump;
    /// <summary>"-- --verbose" prints every check, passed or not.</summary>
    private bool _verbose;
    private ScriptErrors? _errors;
    private RoomData _room = null!;
    private Node3D? _world;
    private CommandHost _host = null!;
    private SmallPlayerController _player = null!;
    private CompanionAvatar _companion = null!;
    private DateTime _now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _learned = new();

    public override async void _Ready()
    {
        try
        {
            _errors = new ScriptErrors();
            OS.AddLogger(_errors);
            _dump = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];
            if (_dump != null) System.IO.Directory.CreateDirectory(_dump);
            _verbose = OS.GetCmdlineUserArgs().Contains("--verbose");
            RemoveAll();
            _room = RoomData.Load(RoomWorld.DefaultRoom);
            // "-- --review" runs only the Wave 2 review's checks (BubbleTest.Review.cs), for a quick look.
            if (OS.GetCmdlineUserArgs().Contains("--review")) await TestTheReviewsFixes();
            else await TestAll();
            await FreeWorld();
            OS.RemoveLogger(_errors);
            Check(_errors.Count == 0, $"no script or engine error while the suite ran ({_errors.Count}; first: {_errors.First})");
            GD.Print($"NATIVE_KERNEL_BUBBLE: {_checks - _failures}/{_checks} checks passed; the Bubble as redesigned: the place deciding (a carry on the player, a boat on " +
                "water, a bubble to walk into), the ghost and its lead, the walk-in, every refusal, the price by what the place made and by principal, the pops and the " +
                "receipts, one bubble at a time, the room's cap, the ride, the deflate, the boat, the Gubble's lead (arriving, popping short, its stop), the lift " +
                "(charged at the rise, refused without pips, the 1.2 m rise), the HUD's view, the security row, the player's marks on a bubble and its lead, and the " +
                "Wave 2 review's fixes (judged where it forms, no free ride up a slope, a lift under a ceiling, Trust only for a paid lift that arrives, a lead into fire paid or free, the lead's " +
                $"sight, one rule for the island's bounds){(_dump != null ? $"; {_dumped} messages dumped" : "")}");
            RemoveAll();
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError("Bubble test exception: " + error);
            GetTree().Quit(1);
        }
    }

    private async Task TestAll()
    {
        await TestThePlaceDecides();
        await TestConsent();
        await TestRefusals();
        await TestPriceByPrincipal();
        await TestPopsAndReceipts();
        await TestOneAtATime();
        await TestTheRoomsCap();
        await TestRideAndDeflate();
        await TestWalkIn();
        await TestBoat();
        await TestLeadArrivesAndPops();
        await TestTheGubblesStopPopsWhatItLeads();
        await TestLift();
        await TestLiftPrice();
        await TestDeflateIntoWater();
        await TestHudView();
        await TestSecurityRow();
        await TestBudget();
        await TestMarksOnTheBubble();
        await TestTheReviewsFixes();
    }

    // ---- the world ----

    private sealed class Store(PlayerModesProfile profile) : IPlayerVitalsStore
    {
        private PlayerModesProfile _saved = profile;
        public PlayerModesProfile Load() => _saved;
        public void Save(PlayerModesProfile next) => _saved = next;
    }

    /// <summary>
    /// A fresh test room under storybook_wild v5 in a mode (Creative's shadow pool unless a check is about the price or harm) with a
    /// pool of pips (at most the pack's maximum), its avatars at their spawns (the Gubble staying put), shallow water on the floor
    /// and a deep pool, and a host whose clock and bubble tick the suite drives (Frames).
    /// </summary>
    private async Task NewWorld(string name, string mode = "creative", int pips = 40, int rulesVersion = 5, RoomData? room = null)
    {
        await FreeWorld();
        room ??= _room;
        _world = new Node3D { Name = "World" };
        AddChild(_world);
        _world.AddChild(RoomBuilder.Build(room));
        _world.AddChild(Water(WaterMin, WaterMax, WaterY));
        _world.AddChild(Water(PoolMin, PoolMax, PoolY));
        await Frames(2);
        _player = new SmallPlayerController { Name = "Player", ReadKeyboard = false, Position = room.SpawnFor("player").PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_player);
        _companion = new CompanionAvatar { Name = "Companion", Position = room.SpawnFor("companion", room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_companion);
        _companion.BindPlayer(_player);
        _companion.Stay();
        _host = CommandHost.Create(_world, room, _player, _companion, null, $"{TestRoot}/{name}/inventions.json");
        _host.Clock = () => _now;
        _host.TeamSightIntervalS = 0;
        _host.SetProcess(false);
        _host.SetPhysicsProcess(false);
        _host.LoadRules("storybook_wild", rulesVersion);
        _host.UseDifficulty("standard");
        var rules = VitalsRules.For(_host.Rules, mode, "standard");
        var vitals = rules.Fresh().With(pips: Math.Min(pips, rules.MaxPips));
        var modes = PlayerModesProfile.Modes.ToDictionary(m => m, m => m == mode ? vitals : PlayerVitals.Fresh);
        _host.ConfigurePlayerVitals(new Store(PlayerModesProfile.Restore(mode, modes, null)));
        _learned.Clear();
        _host.BubbleLearnedForTests = _learned.Add;
        await Frames(6);
    }

    /// <summary>A sheet of water over the floor (the room's kind of water collider, RoomWater): 3 cm is shallow, 15 cm deep.</summary>
    private static Area3D Water(Vector2 min, Vector2 max, float surface)
    {
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { min, new Vector2(max.X, min.Y), max, min, max, new Vector2(min.X, max.Y) })
            sheet.AddVertex(new Vector3(corner.X, surface, corner.Y));
        return RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
    }

    private async Task FreeWorld()
    {
        if (_world == null || !IsInstanceValid(_world)) return;
        _world.QueueFree();
        _world = null;
        await Frames(3);
    }

    /// <summary>Physics frames, each a tick of the host's clock and its bubbles before the bodies move (as the game's own tick).</summary>
    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            _now += TimeSpan.FromSeconds(Tick);
            if (_world != null && IsInstanceValid(_host)) _host.AdvanceBubbles(Tick);
        }
    }

    /// <summary>The host's clock moves on without the bodies (a ghost's lead).</summary>
    private void Wait(double seconds) => _now += TimeSpan.FromSeconds(seconds);

    /// <summary>Puts the player's feet at a spot on the floor, still.</summary>
    private async Task PlayerAt(Vector3 feet)
    {
        _player.SetDiveInput(false, false);
        _player.SetControlInput(Vector2.Zero);
        Check(_player.TryTeleportTo(feet), $"the player stands at {feet}");
        await Frames(4);
    }

    private void GubbleAt(Vector3 at)
    {
        _companion.GlobalPosition = at + Vector3.Up * 0.008f;
        _companion.ResetPhysicsInterpolation();
    }

    /// <summary>The dive key, pressed for two frames.</summary>
    private async Task DiveKey()
    {
        _player.SetDiveInput(true, false);
        await Frames(2);
        _player.SetDiveInput(false, false);
    }

    // ---- messages ----

    private string NextId(string prefix) => $"{prefix}-{++_ids}";

    private JsonObject Command(string actionId, string op, JsonObject args, bool preview = false)
    {
        var command = new JsonObject { ["schema"] = "enfractal.command", ["version"] = 1, ["action_id"] = actionId, ["room_id"] = _room.RoomId, ["op"] = op, ["args"] = args.Parent == null ? args : args.DeepClone() };
        if (preview) command["preview"] = true;
        return command;
    }

    private JsonObject Query(string op, JsonObject args) =>
        new() { ["schema"] = "enfractal.query", ["version"] = 1, ["query_id"] = NextId("q"), ["room_id"] = _room.RoomId, ["op"] = op, ["args"] = args };

    /// <summary>Sends a message as principal; contractValid false keeps a deliberately malformed message out of the dump.</summary>
    private JsonObject Send(JsonObject message, string principal, bool contractValid = true)
    {
        var result = JsonNode.Parse(_host.Handle(message.ToJsonString(), principal))!.AsObject();
        if (_dump != null)
        {
            var index = ++_dumped;
            if (contractValid) System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, $"{index:0000}_message.json"), CanonicalJson.Bytes(message));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, $"{index:0000}_result.json"), CanonicalJson.Bytes(result));
        }
        return result;
    }

    private static JsonArray Vec(Vector3 at) => new(Math.Round(at.X, 4), Math.Round(at.Y, 4), Math.Round(at.Z, 4));

    private static JsonObject BubbleArgs(string capability, Vector3 center, string? target = null, double radius = 0.08, double duration = 20, string? previewId = null)
    {
        var args = new JsonObject { ["capability"] = capability, ["center_m"] = Vec(center), ["radius_m"] = radius, ["duration_s"] = duration };
        if (target != null) args["target"] = target;
        if (previewId != null) args["preview_id"] = previewId;
        return args;
    }

    /// <summary>effect.start of the island's Bubbles at a spot: the place decides what it is.</summary>
    private static JsonObject TheBubble(Vector3 spot, double duration = 20) => new()
    {
        ["capability"] = "bubbles", ["params"] = new JsonObject(), ["area"] = new JsonObject { ["center_m"] = Vec(spot), ["radius_m"] = 0.5 }, ["duration_s"] = duration,
    };

    private JsonObject CastTheBubble(string principal, Vector3 spot, double duration = 20, bool preview = false, string? actionId = null) =>
        Send(Command(actionId ?? NextId("bubble"), "effect.start", TheBubble(spot, duration), preview), principal);

    /// <summary>The Bubble cast on the player done right: the Gubble previews (the ghost) and casts the same a moment later; the player at once.</summary>
    private JsonObject TheBubbleOnThePlayer(string principal, double duration = 20)
    {
        var at = _player.GlobalPosition;
        if (principal != Player)
        {
            CastTheBubble(principal, at, duration, preview: true);
            Wait(BubbleLaws.GhostLeadS + 0.05);
        }
        return CastTheBubble(principal, at, duration);
    }

    /// <summary>Previews a nature.cast carry on the player as principal: the preview's result.</summary>
    private JsonObject Preview(string principal, double duration = 20, Vector3? at = null) =>
        Send(Command(NextId("ghost"), "nature.cast", BubbleArgs("bubble_carry", at ?? _player.GlobalPosition, CommandHost.PlayerAvatar, duration: duration), preview: true), principal);

    /// <summary>
    /// A nature.cast carry on the player done right: previewed, the ghost's lead waited out (the Gubble's), then cast naming the
    /// preview. Returns the commit's result; the preview's comes back in preview.
    /// </summary>
    private JsonObject CarryOnPlayer(string principal, out JsonObject preview, double duration = 20)
    {
        var at = _player.GlobalPosition;
        preview = Send(Command(NextId("ghost"), "nature.cast", BubbleArgs("bubble_carry", at, CommandHost.PlayerAvatar, duration: duration), preview: true), principal);
        if (principal != Player) Wait(BubbleLaws.GhostLeadS + 0.05);
        var token = preview["data"]?["preview_id"]?.GetValue<string>();
        return Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", at, CommandHost.PlayerAvatar, duration: duration, previewId: token)), principal);
    }

    private JsonObject AtPoint(string principal, string capability, Vector3 center, double radius = 0.08, double duration = 20) =>
        Send(Command(NextId(capability), "nature.cast", BubbleArgs(capability, center, radius: radius, duration: duration)), principal);

    /// <summary>goal.set lead: the Gubble leads a bubble (target) to a spot (position_m).</summary>
    private JsonObject Lead(string principal, string bubble, Vector3 spot, bool preview = false, bool contractValid = true) =>
        Send(Command(NextId("lead"), "goal.set", new JsonObject
        {
            ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "lead", ["target"] = bubble, ["position_m"] = Vec(spot),
        }, preview), principal, contractValid);

    private JsonObject Lookup(string principal, string actionId) =>
        Send(Query("receipt.lookup", new JsonObject { ["action_id"] = actionId }), principal);

    private JsonNode? ReceiptData(string principal, JsonObject committed) =>
        Lookup(principal, committed["action_id"]!.GetValue<string>())["data"]?["receipt"]?["data"];

    /// <summary>A job's state now (jobs.status as principal), or "" when there is none.</summary>
    private string JobState(string principal, JsonObject set) =>
        set["job_id"]?.GetValue<string>() is { } job ? Send(Query("jobs.status", new JsonObject { ["job_id"] = job }), principal)["data"]?["state"]?.GetValue<string>() ?? "" : "";

    private JsonNode? JobError(string principal, JsonObject set) =>
        set["job_id"]?.GetValue<string>() is { } job ? Send(Query("jobs.status", new JsonObject { ["job_id"] = job }), principal)["data"]?["result"]?["error"] : null;

    /// <summary>Frames until a lead's job is no longer running (at most maxFrames): its final state.</summary>
    private async Task<string> UntilLeadEnds(string principal, JsonObject set, int maxFrames = 1800)
    {
        for (var i = 0; i < maxFrames && JobState(principal, set) == "running"; i += 10) await Frames(10);
        return JobState(principal, set);
    }

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;
    private static string? Code(JsonObject result) => result["error"]?["code"]?.GetValue<string>();
    private static string? Path(JsonObject result) => result["error"]?["field_path"]?.GetValue<string>();
    private static string Canonical(JsonNode? node) => node == null ? "" : CanonicalJson.Text(node);
    private int Pips => _host.Vitals.Pips;
    private float Health => _host.Vitals.Health;
    private static string BubbleOf(JsonObject committed) => committed["data"]?["bubble"]?.GetValue<string>() ?? "";
    private static string? Kind(JsonObject committed) => committed["data"]?["kind"]?.GetValue<string>();
    private Vector3 BubbleCenter => _host.ActiveBubbles.Count > 0 ? _host.ActiveBubbles[0].Center : Vector3.Zero;

    private void RemoveAll()
    {
        var directory = ProjectSettings.GlobalizePath(TestRoot);
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (_verbose) GD.Print((condition ? "BUBBLE_PASS " : "BUBBLE_FAIL ") + label);
        if (condition) return;
        _failures++;
        GD.PushError("Bubble: " + label);
    }

    /// <summary>Counts every script or engine error while the suite runs, so one fails the suite itself.</summary>
    private sealed partial class ScriptErrors : Logger
    {
        private readonly object _lock = new();
        public int Count { get; private set; }
        public string First { get; private set; } = "";

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if (errorType == (int)ErrorType.Warning || function == "push_error" || code.StartsWith("Bubble", StringComparison.Ordinal)) return;
            lock (_lock)
            {
                Count++;
                if (First.Length == 0) First = $"{file}:{line} {code} {rationale}";
            }
        }
    }

    // ---- the place decides (decision 56): effect.start of Bubbles, one player-sized bubble at a spot ----

    private async Task TestThePlaceDecides()
    {
        await NewWorld("place");
        var feet = _player.GlobalPosition;
        // On the player: a carry round them, the ghost first for the Gubble.
        var unpreviewed = CastTheBubble(Companion, feet);
        var preview = CastTheBubble(Companion, feet, preview: true);
        var tooSoon = CastTheBubble(Companion, feet);
        Check(Code(unpreviewed) == "revision_conflict" && Path(unpreviewed) == "$.args.area.center_m" && Ok(preview) && preview["preview"]?.GetValue<bool>() == true &&
              Kind(preview) == "carry" && _host.BubbleGhostCount == 1 && Code(tooSoon) == "not_ready" && _host.ActiveBubbles.Count == 0,
            "the Gubble's Bubble on the player: unpreviewed it is refused (revision_conflict), its preview shows the ghost (kind carry), cast at once it waits (not_ready): " +
            $"{Code(unpreviewed)} {Kind(preview)} {Code(tooSoon)}");
        Wait(BubbleLaws.GhostLeadS + 0.05);
        var carry = CastTheBubble(Companion, feet);
        var id = BubbleOf(carry);
        Check(Ok(carry) && carry["transient"]?.GetValue<bool>() == true && Kind(carry) == "carry" && carry["created"]?[0]?.GetValue<string>() == id && carry["data"]?["effect"]?.GetValue<string>() == id &&
              carry["affected"]?.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { id, CommandHost.PlayerAvatar }) == true && carry["data"]?["carried"]?.GetValue<bool>() == true &&
              Math.Abs(carry["data"]?["area"]?["radius_m"]?.GetValue<double>() - BubbleLaws.CarryRadiusDefaultM ?? 1) < 1e-6 && _host.PlayerInBubble && _host.BubbleGhostCount == 0,
            "after the ghost's lead the same cast forms one player-sized bubble round the player, whatever the area's radius: " + Canonical(carry["data"]));
        // On water: a boat, learned the first time.
        _host.PlayerGoal("stop");
        await Frames(60);
        var shallows = new Vector3(-0.85f, WaterY, 0.6f);
        var boat = CastTheBubble(Companion, shallows);
        Check(Ok(boat) && Kind(boat) == "boat" && _host.ActiveBubbles.Single().Capability == "bubble_boat" &&
              Math.Abs(BubbleCenter.Y - (WaterY + BubbleLaws.BoatDeckAboveWaterM)) < 0.005f && _learned.Contains("bubble_boat") &&
              _host.GlowProgress.Has(ExpertiseLists.Bubbles, ExpertiseLists.BubbleBoat),
            "the Bubble cast on water is a boat floating at the surface, and the first cast on water teaches the boat: " + Canonical(boat["data"]) + " " + string.Join(",", _learned));
        var onTheBed = CastTheBubble(Companion, new Vector3(0.85f, 0, 1.15f));
        Check(Ok(onTheBed) && Kind(onTheBed) == "boat" && Math.Abs(BubbleCenter.Y - (PoolY + BubbleLaws.BoatDeckAboveWaterM)) < 0.005f,
            "pointed at a deep pool's bed, it is a boat at the pool's surface (any water is a boat): " + Canonical(onTheBed["data"] ?? onTheBed["error"]));
        // Anywhere else: a bubble that waits for the player to walk in.
        var spot = feet + new Vector3(0.3f, 0, 0.1f);
        var waiting = CastTheBubble(Player, spot);
        Check(Ok(waiting) && Kind(waiting) == "walk_in" && waiting["data"]?["carried"]?.GetValue<bool>() == false && waiting["affected"]?.AsArray().Count == 1 && !_host.PlayerInBubble &&
              _host.ActiveBubbles.Count == 1 && _host.ActiveBubbles[0].Capability == "bubble_carry",
            "the Bubble cast on the ground beside the player is a bubble to walk into, holding no one: " + Canonical(waiting["data"]));
        // The player's own Bubble on themselves needs no ghost: their key is the consent.
        var own = CastTheBubble(Player, _player.GlobalPosition);
        Check(Ok(own) && Kind(own) == "carry" && _host.PlayerInBubble, "the player's own Bubble on themselves forms at once: " + Canonical(own["error"] ?? own["data"]));
        // Never on the Gubble itself, never targeting the player's avatar.
        _host.PlayerGoal("stop");
        var onTheGubble = Send(Command(NextId("bubble"), "effect.start", new JsonObject
        {
            ["capability"] = "bubbles", ["params"] = new JsonObject(), ["area"] = new JsonObject { ["center_m"] = Vec(feet), ["radius_m"] = 0.5 }, ["duration_s"] = 20,
            ["targets"] = new JsonArray(CommandHost.CompanionAvatarId),
        }), Companion);
        var hud = _host.PlayerEffect("bubbles");
        Check(Code(onTheGubble) == "invalid_args" && Path(onTheGubble) == "$.args.targets" && Code(hud) == "invalid_args" && _host.ActiveBubbles.Count == 0,
            $"the Bubble on the Gubble itself is refused (invalid_args at $.args.targets), the HUD's key with no spot too ({Code(onTheGubble)}, {Code(hud)})");
        // The HUD's key at a spot is the Bubble there (the cosmetic stream is gone).
        var key = _host.PlayerEffect("bubbles", spot);
        Check(Ok(key) && Kind(key) == "walk_in" && _host.ActiveEffectIds.Count == 0 && _host.ActiveBubbles.Count == 1,
            "the HUD's Bubbles key at a spot casts the one bubble there, no stream of small bubbles and no effect besides it: " + Canonical(key["data"]));
        await Task.CompletedTask;
    }

    // ---- the consent of a nature.cast carry: the ghost preview, its lead, and a cast at a point ----

    private async Task TestConsent()
    {
        await NewWorld("consent");
        var unpreviewed = Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", _player.GlobalPosition, CommandHost.PlayerAvatar)), Companion, contractValid: false);
        Check(Code(unpreviewed) == "request_invalid" && Path(unpreviewed) == "$" && _host.ActiveBubbles.Count == 0,
            "a Gubble's carry on the player without its preview is refused request_invalid at $, and nothing forms: " + Canonical(unpreviewed["error"]));
        var pips = Pips;
        var preview = Preview(Companion);
        var token = preview["data"]?["preview_id"]?.GetValue<string>() ?? "";
        Check(Ok(preview) && preview["preview"]?.GetValue<bool>() == true && preview["data"]?["capability"]?.GetValue<string>() == "bubble_carry" &&
              preview["data"]?["target"]?.GetValue<string>() == CommandHost.PlayerAvatar && token.Length == 32 && preview["affected"]?[0]?.GetValue<string>() == CommandHost.PlayerAvatar &&
              preview["data"]?["cell_count"]?.GetValue<int>() == 0 && preview["data"]?["affected_ids"] == null && _host.BubbleGhostCount == 1 && _host.ActiveBubbles.Count == 0 && Pips == pips,
            "the Gubble's preview of a carry on the player answers its preview_id and shows the ghost; nothing forms and nothing is charged: " + Canonical(preview));
        var args = BubbleArgs("bubble_carry", _player.GlobalPosition, CommandHost.PlayerAvatar, previewId: token);
        var tooSoon = Send(Command(NextId("carry"), "nature.cast", args), Companion);
        Check(Code(tooSoon) == "not_ready" && Path(tooSoon) == "$.args.preview_id" && tooSoon["error"]?["retryable"]?.GetValue<bool>() == true && _host.ActiveBubbles.Count == 0,
            "cast at once, before the player has seen the ghost for BubbleLaws.GhostLeadS, it waits (not_ready, retryable) and nothing forms: " + Canonical(tooSoon["error"]));
        var moved = Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", _player.GlobalPosition + new Vector3(0.03f, 0, 0), CommandHost.PlayerAvatar, previewId: token)), Companion);
        Wait(BubbleLaws.GhostLeadS);
        var others = Send(Command(NextId("carry"), "nature.cast", args), Player);
        Check(Code(moved) == "revision_conflict" && Path(moved) == "$.args.preview_id" && Code(others) == "revision_conflict",
            $"a cast that is not exactly its preview, or names another caster's preview, is refused revision_conflict at $.args.preview_id ({Code(moved)}, {Code(others)})");
        var carry = Send(Command("carry-ok", "nature.cast", args), Companion);
        var id = BubbleOf(carry);
        Check(Ok(carry) && carry["transient"]?.GetValue<bool>() == true && carry["preview"]?.GetValue<bool>() == false && carry["created"]?[0]?.GetValue<string>() == id &&
              id.StartsWith("effect:", StringComparison.Ordinal) && carry["affected"]?.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { id, CommandHost.PlayerAvatar }) == true &&
              carry["data"]?["carried"]?.GetValue<bool>() == true && carry["data"]?["target"]?.GetValue<string>() == CommandHost.PlayerAvatar && carry["data"]?["ended"] == null &&
              carry["data"]?["expires_utc"] != null && _host.PlayerInBubble && _host.BubbleGhostCount == 0,
            "after the lead the cast naming its preview forms the bubble round the player: a transient receipt, its effect: id created, the player carried: " + Canonical(carry));
        var replay = Send(Command("carry-ok", "nature.cast", args), Companion);
        Check(Ok(replay) && replay["replayed"]?.GetValue<bool>() == true && BubbleOf(replay) == id && _host.ActiveBubbles.Count == 1,
            "the same cast again replays its receipt and forms nothing more");
        await Frames(30);
        Check(_player.Ride == SmallPlayerController.BubbleRide.Enveloped, "the body rides the bubble: " + _player.Ride);
        // A cast at a point never forms round the player: it waits beside them for the walk-in.
        _host.PlayerGoal("stop");
        await Frames(120);
        var onThePlayer = AtPoint(Companion, "bubble_carry", _player.GlobalPosition);
        var beside = AtPoint(Companion, "bubble_carry", _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        await Frames(5);
        Check(Code(onThePlayer) == "invalid_args" && Path(onThePlayer) == "$.args.center_m" && Ok(beside) && beside["data"]?["carried"]?.GetValue<bool>() == false &&
              beside["data"]?["target"] == null && beside["affected"]?.AsArray().Count == 1 && !_host.PlayerInBubble && _host.ActiveBubbles.Count == 1,
            "a carry at a point where the player stands is refused invalid_args; beside them it waits, carrying no one: " + Canonical(onThePlayer["error"]) + " " + Canonical(beside["data"]));
        // The player's own carry on themselves is previewed too (the contract's targets rule), with no lead (they ordered it).
        _host.PlayerGoal("stop");
        var own = CarryOnPlayer(Player, out var ownPreview);
        Check(Ok(ownPreview) && Ok(own) && _host.PlayerInBubble, "the player's own carry on themselves: previewed, then cast at once: " + Canonical(own["error"]));
        _host.PlayerGoal("stop");
        var ownToken = ownPreview["data"]?["preview_id"]?.GetValue<string>();
        var reused = Send(Command(NextId("carry"), "nature.cast", BubbleArgs("bubble_carry", ownPreview["data"]?["center_m"] is JsonArray c
            ? new Vector3(c[0]!.GetValue<float>(), c[1]!.GetValue<float>(), c[2]!.GetValue<float>()) : Vector3.Zero, CommandHost.PlayerAvatar, previewId: ownToken)), Player);
        Check(Code(reused) == "revision_conflict", "a preview is used once: " + Code(reused));
    }

    // ---- every refusal ----

    private async Task TestRefusals()
    {
        await NewWorld("refusals");
        var feet = _player.GlobalPosition;
        JsonObject Carry(string principal, JsonObject args, bool preview = true, bool valid = true) => Send(Command(NextId("refused"), "nature.cast", args, preview), principal, valid);
        var outcome = new Dictionary<string, (string? Code, string? Path)>
        {
            ["a carry on the Gubble"] = Pair(Carry(Player, BubbleArgs("bubble_carry", _companion.GlobalPosition, CommandHost.CompanionAvatarId))),
            ["a carry on an avatar not here (the player)"] = Pair(Carry(Player, BubbleArgs("bubble_carry", feet, "avatar:visitor"))),
            ["a carry on an avatar not here (the Gubble)"] = Pair(Carry(Companion, BubbleArgs("bubble_carry", feet, "avatar:visitor"))),
            ["a carry on the player centred away from them"] = Pair(Carry(Companion, BubbleArgs("bubble_carry", feet + new Vector3(0.4f, 0, 0), CommandHost.PlayerAvatar))),
            ["a boat on the dry floor"] = Pair(Carry(Companion, BubbleArgs("bubble_boat", feet + new Vector3(0.3f, 0, 0), radius: 0.12))),
            ["a carry too small to hold the body"] = Pair(Carry(Companion, BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar, radius: 0.05))),
            ["a carry past the island's radius"] = Pair(Carry(Companion, BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar, radius: 1.2))),
            ["a carry past the island's duration"] = Pair(Carry(Companion, BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar, duration: 90))),
            ["a lift cast (Lift is a lead now)"] = Pair(Carry(Companion, BubbleArgs("bubble_lift", feet, CommandHost.PlayerAvatar))),
            ["a carry beyond the Gubble's reach"] = Pair(Carry(Companion, BubbleArgs("bubble_carry", new Vector3(-1.6f, 0, -1.2f)))),
            ["a carry outside the island"] = Pair(Carry(Companion, BubbleArgs("bubble_carry", new Vector3(0.6f, 0, 1.6f)))),
            ["a boat on the player"] = Pair(Carry(Companion, BubbleArgs("bubble_boat", feet, CommandHost.PlayerAvatar, radius: 0.12), valid: false)),
            ["Grow on the player"] = Pair(Send(Command(NextId("bloom"), "nature.cast", new JsonObject
            {
                ["capability"] = "grow", ["center_m"] = Vec(feet), ["radius_m"] = 0.25, ["duration_s"] = 30, ["target"] = CommandHost.PlayerAvatar,
            }, preview: true), Companion, contractValid: false)),
            ["the Bubble beyond the Gubble's reach"] = Pair(CastTheBubble(Companion, new Vector3(-1.6f, 0, -1.2f), preview: true)),
            ["the Bubble past the island's duration"] = Pair(CastTheBubble(Companion, feet + new Vector3(0.3f, 0, 0), duration: 90, preview: true)),
        };
        var radiusAllowed = Carry(Companion, BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar, radius: 0.05))["error"]?["allowed"];
        Check(Canonical(radiusAllowed) == "[0.07,0.2]", "a carry's size refusal names its bounds: " + Canonical(radiusAllowed));
        var expected = new Dictionary<string, (string? Code, string? Path)>
        {
            ["a carry on the Gubble"] = ("invalid_args", "$.args.target"),
            ["a carry on an avatar not here (the player)"] = ("target_not_found", "$.args.target"),
            ["a carry on an avatar not here (the Gubble)"] = ("target_not_found", "$.args.target"),
            ["a carry on the player centred away from them"] = ("invalid_args", "$.args.center_m"),
            ["a boat on the dry floor"] = ("invalid_args", "$.args.center_m"),
            ["a carry too small to hold the body"] = ("invalid_args", "$.args.radius_m"),
            ["a carry past the island's radius"] = ("invalid_args", "$.args.radius_m"),
            ["a carry past the island's duration"] = ("invalid_args", "$.args.duration_s"),
            ["a lift cast (Lift is a lead now)"] = ("unsupported_capability", "$.args.capability"),
            ["a carry beyond the Gubble's reach"] = ("out_of_bounds", "$.args.center_m"),
            ["a carry outside the island"] = ("out_of_bounds", "$.args.center_m"),
            ["a boat on the player"] = ("request_invalid", "$.args.capability"),
            ["Grow on the player"] = ("request_invalid", "$.args.capability"),
            ["the Bubble beyond the Gubble's reach"] = ("out_of_bounds", "$.args.area.center_m"),
            ["the Bubble past the island's duration"] = ("invalid_args", "$.args.duration_s"),
        };
        foreach (var (name, want) in expected)
            Check(outcome[name] == want, $"{name}: {outcome[name]} (expected {want})");
        Check(_host.ActiveBubbles.Count == 0 && _host.BubbleGhostCount == 0, "no refusal forms a bubble or shows a ghost");
        // An island whose pack has no Bubbles (v1: Glow alone) grants none.
        _host.LoadRules("storybook_wild", 1);
        var none = Carry(Player, BubbleArgs("bubble_carry", feet, CommandHost.PlayerAvatar));
        var noBubble = CastTheBubble(Player, feet + new Vector3(0.3f, 0, 0));
        Check(Code(none) == "unsupported_capability" && Path(none) == "$.args.capability" && Code(noBubble) == "unsupported_capability",
            "a pack without Bubbles grants no carry and no Bubble: " + Canonical(none["error"]) + " " + Canonical(noBubble["error"]));
        await Task.CompletedTask;
    }

    private static (string?, string?) Pair(JsonObject result) => (Code(result), Path(result));

    // ---- the price, by what the place made and by principal ----

    private async Task TestPriceByPrincipal()
    {
        // The pool of a fresh team: 8 pips (storybook_wild v5).
        await NewWorld("price", mode: "challenge", pips: 8);
        var start = Pips;
        var preview = CastTheBubble(Companion, _player.GlobalPosition, preview: true);
        Check(Ok(preview) && Pips == start && start == 8, $"a preview costs nothing ({start} pips)");
        Wait(BubbleLaws.GhostLeadS + 0.05);
        var own = CastTheBubble(Companion, _player.GlobalPosition);
        Check(Ok(own) && Pips == start - 2, $"the Gubble's own Bubble on the player is a carry at its own-idea price, 2 pips ({start - Pips})");
        var stop = _host.PlayerGoal("stop");
        var refused = CastTheBubble(Companion, new Vector3(-1.6f, 0, -1.2f));
        Check(Ok(stop) && Code(refused) == "out_of_bounds" && Pips == start - 2, "a pop costs nothing, nor does a refused command");
        await Frames(30);
        var ordered = TheBubbleOnThePlayer(Player);
        _host.PlayerGoal("stop");
        Check(Ok(ordered) && Pips == start - 6, $"the player's ordered carry pays the ordered price, 4 pips ({start - Pips} spent)");
        await Frames(30);
        var walkIn = CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        Check(Ok(walkIn) && Kind(walkIn) == "walk_in" && Pips == start - 7, $"a bubble to walk into is the plain Bubble: 1 pip as the Gubble's idea ({start - Pips} spent)");
        var poor = CastTheBubble(Player, new Vector3(-0.85f, WaterY, 0.6f));
        Check(Code(poor) == "energy_exhausted" && poor["error"]?["balance"]?.GetValue<int>() == 1 && poor["error"]?["price"]?.GetValue<int>() == 4 && _host.ActiveBubbles.Count == 1 &&
              Kind(walkIn) == "walk_in" && Pips == 1,
            "the player's boat at 4 pips with 1 in the pool is refused energy_exhausted with the balance and the price, and the bubble there stays: " + Canonical(poor["error"]));
        var last = CastTheBubble(Companion, new Vector3(-0.85f, WaterY, 0.6f));
        Check(Code(last) == "energy_exhausted" && last["error"]?["price"]?.GetValue<int>() == 2, "the Gubble's boat is 2 pips, the boat's own-idea price: " + Canonical(last["error"]));
        await NewWorld("price-boat", mode: "challenge", pips: 8);
        var boat = CastTheBubble(Player, new Vector3(-0.85f, WaterY, 0.6f));
        var walk = CastTheBubble(Player, _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        Check(Ok(boat) && Ok(walk) && Pips == 2, $"the player's boat costs 4 and their bubble to walk into 2 ({8 - Pips})");
        // Creative's shadow pool drains and never refuses.
        await NewWorld("price-creative", mode: "creative", pips: 1);
        var shadow = TheBubbleOnThePlayer(Player);
        Check(Ok(shadow) && Pips == 0, $"in Creative the shadow pool pays what it can and never refuses ({Pips} left)");
    }

    // ---- the pops and the receipts ----

    private async Task TestPopsAndReceipts()
    {
        await NewWorld("pops");
        // X: the player's stop pops the Gubble's carry, as popped by the player.
        var carry = CarryOnPlayer(Companion, out _);
        var id = BubbleOf(carry);
        var x = _host.PlayerGoal("stop");
        var ended = ReceiptData(Companion, carry);
        Check(Ok(x) && x["affected"]?.AsArray().Any(n => n!.GetValue<string>() == id) == true && x["data"]?["effects_stopped"]?.GetValue<int>() >= 1 && !_host.PlayerInBubble &&
              ended?["ended"]?.GetValue<string>() == "popped_by_player" && ended?["capability"]?.GetValue<string>() == "bubble_carry" &&
              ended?["target"]?.GetValue<string>() == CommandHost.PlayerAvatar && ended?["carried"]?.GetValue<bool>() == true,
            "X (the player's goal.stop) pops the Gubble's carry, and its receipt keeps the kind, the target and popped_by_player: " + Canonical(ended));
        var replayed = Send(Command(carry["action_id"]!.GetValue<string>(), "nature.cast",
            BubbleArgs("bubble_carry", Vector3.Zero, CommandHost.PlayerAvatar, previewId: new string('b', 32))), Companion);
        Check(Code(replayed) == "action_id_conflict", "the cast's action id stays its own");
        // The timer: a bubble at its duration pops, timed_out; an effect.start Bubble's receipt says it too.
        await Frames(10);
        var waiting = CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f), duration: 2);
        Wait(2.1);
        await Frames(2);
        Check(Ok(waiting) && _host.ActiveBubbles.Count == 0 && ReceiptData(Companion, waiting)?["ended"]?.GetValue<string>() == "timed_out" &&
              ReceiptData(Companion, waiting)?["kind"]?.GetValue<string>() == "walk_in",
            "a bubble at its duration pops, and its receipt says timed_out: " + Canonical(ReceiptData(Companion, waiting)));
        // Anything else: the Gubble's own stop pops its own bubble, popped.
        var other = AtPoint(Companion, "bubble_carry", _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        var gubbleStop = Send(Command(NextId("stop"), "goal.stop", new JsonObject()), Companion);
        Check(Ok(gubbleStop) && gubbleStop["affected"]?.AsArray().Any(n => n!.GetValue<string>() == BubbleOf(other)) == true &&
              ReceiptData(Companion, other)?["ended"]?.GetValue<string>() == "popped",
            "the Gubble's own stop pops its own bubble, popped (by something else): " + Canonical(ReceiptData(Companion, other)));
        // effect.stop names a bubble: the player any, a companion only its own.
        var players = CastTheBubble(Player, _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        var notYours = Send(Command(NextId("stop"), "effect.stop", new JsonObject { ["effect"] = BubbleOf(players) }), Companion);
        var yours = Send(Command(NextId("stop"), "effect.stop", new JsonObject { ["effect"] = BubbleOf(players) }), Player);
        Check(Ok(notYours) && notYours["data"]?["effects_stopped"]?.GetValue<int>() == 0 && Ok(yours) && yours["data"]?["effects_stopped"]?.GetValue<int>() == 1 &&
              ReceiptData(Player, players)?["ended"]?.GetValue<string>() == "popped_by_player",
            "effect.stop by a bubble's id: the Gubble cannot pop the player's, the player can (popped_by_player)");
        // The rules changing pops what was cast under the old ones.
        var underOld = AtPoint(Companion, "bubble_carry", _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        _host.LoadRules("storybook_wild", 5);
        await Frames(2);
        Check(_host.ActiveBubbles.Count == 0 && ReceiptData(Companion, underOld)?["ended"]?.GetValue<string>() == "popped", "new rules pop the old rules' bubbles");
    }

    // ---- one bubble at a time (decision 59): a recast pops the one there, and there is no pause after a pop ----

    private async Task TestOneAtATime()
    {
        await NewWorld("one-at-a-time");
        var first = CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        var second = CastTheBubble(Player, _player.GlobalPosition + new Vector3(-0.3f, 0, 0.1f));
        Check(Ok(first) && Ok(second) && _host.ActiveBubbles.Count == 1 && _host.ActiveBubbles[0].Id == BubbleOf(second) &&
              ReceiptData(Companion, first)?["ended"]?.GetValue<string>() == "popped",
            "one bubble at a time: a recast pops the one already there (popped), whoever cast either: " + Canonical(ReceiptData(Companion, first)));
        // A recast while a bubble holds the player pops it (the body deflates) and forms the new one.
        var carry = TheBubbleOnThePlayer(Companion);
        await Frames(30);
        var high = _player.GlobalPosition.Y;
        var recast = CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.35f, 0, 0.1f));
        await Frames(3);
        Check(Ok(carry) && Ok(recast) && !_host.PlayerInBubble && _host.ActiveBubbles.Count == 1 && ReceiptData(Companion, carry)?["ended"]?.GetValue<string>() == "popped" &&
              _player.Ride is SmallPlayerController.BubbleRide.Deflating or SmallPlayerController.BubbleRide.None,
            $"a recast pops the bubble holding the player, who drifts down ({_player.Ride} from {high:0.000} m)");
        await Frames(120);
        // No pause after the player pops out: the Gubble may offer again at once (the ghost and its lead are the consent, as ever).
        var again = TheBubbleOnThePlayer(Companion);
        await Frames(10);
        await DiveKey();
        var offer = TheBubbleOnThePlayer(Companion);
        Check(Ok(again) && Ok(offer) && _host.PlayerInBubble && _player.BubblePops >= 1,
            "no pause after a pop: straight after the dive key the Gubble's next offer (its ghost, then the cast) forms: " + Canonical(offer["error"] ?? offer["data"]));
        Check(BubbleLaws.MaxBubbles == 1, "BubbleLaws.MaxBubbles is one");
    }

    // ---- the room's effect cap counts the bubble, at a bubble's cast and at any effect's start ----

    private async Task TestTheRoomsCap()
    {
        await NewWorld("cap");
        var listed = Send(Query("capabilities.list", new JsonObject()), Player);
        var bubbles = listed["data"]?["items"]?.AsArray().FirstOrDefault(i => i?["capability"]?.GetValue<string>() == "bubbles");
        Check(bubbles?["max_active"]?.GetValue<int>() == 1 && bubbles?["command_op"]?.GetValue<string>() == "effect.start",
            "capabilities.list says one Bubble at a time, cast with effect.start: " + Canonical(bubbles));
        _host.EffectLimit = 1;
        var bubble = CastTheBubble(Player, _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        var glow = _host.PlayerEffect("glow", _player.GlobalPosition + new Vector3(0.2f, 0, -0.2f));
        Check(Ok(bubble) && Code(glow) == "budget_exceeded" && glow["error"]?["allowed"]?.GetValue<int>() == 1 && glow["error"]?["actual"]?.GetValue<int>() == 1 &&
              _host.ActiveEffectIds.Count == 0,
            "with the room's cap at one and a bubble there, an effect's start is refused budget_exceeded, the bubble counted: " + Canonical(glow["error"]));
        var recast = CastTheBubble(Player, _player.GlobalPosition + new Vector3(-0.3f, 0, 0.1f));
        Check(Ok(recast) && _host.ActiveBubbles.Count == 1, "a recast replaces the bubble, so the one it pops does not count against it: " + Canonical(recast["error"]));
        _host.PlayerGoal("stop");
        var light = _host.PlayerEffect("glow", _player.GlobalPosition + new Vector3(0.2f, 0, -0.2f));
        var capped = CastTheBubble(Player, _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        var cappedCast = AtPoint(Player, "bubble_carry", _player.GlobalPosition + new Vector3(0.3f, 0, 0.1f));
        _host.EffectLimit = CommandHost.MaxActiveEffects;
        Check(Ok(light) && Code(capped) == "budget_exceeded" && Code(cappedCast) == "budget_exceeded" && cappedCast["error"]?["allowed"]?.GetValue<int>() == 1 && _host.ActiveBubbles.Count == 0,
            "while the effects fill the room's cap, neither the Bubble nor a nature.cast bubble forms: " + Canonical(capped["error"]) + " " + Canonical(cappedCast["error"]));
        await Task.CompletedTask;
    }
}
