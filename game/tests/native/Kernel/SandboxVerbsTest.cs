using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;
using GarageGame.Native.Sandbox;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The sandbox verbs (Run 2, P3) through enfractal.command in the real test room, with the real bodies and Jolt: the
/// player picks up the doorstop, carries it across the room and sets it on the big box; the companion carries the book
/// and stacks; pushes slide along surfaces, stop at walls, ride over the rug's edge and fall off the box; and the
/// refusals (too heavy, out of reach, no room, not your avatar, held, protected, something on it). Poses are saved with
/// their receipts and the scene is rebuilt from them on reload, a creation on a moved object included. Pass
/// "-- --dump=DIR" to write every message and result for contracts/validate.py.
/// </summary>
public partial class SandboxVerbsTest : Node3D
{
    private const string TestRoot = "user://tests/sandbox_verbs";
    private const string Player = CommandHost.PlayerPrincipal;
    private const string Companion = CommandHost.CompanionPrincipal;
    private const string PlayerAvatar = CommandHost.PlayerAvatar;
    private const string CompanionAvatar = CommandHost.CompanionAvatarId;
    private int _checks;
    private int _failures;
    private int _dumped;
    private int _ids;
    private string? _dump;
    private CommandHost _host = null!;
    private RoomData _room = null!;
    private SmallPlayerController _player = null!;
    private CompanionAvatar _companion = null!;
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private string _savePath = "";

    public override async void _Ready()
    {
        try
        {
            _dump = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];
            if (_dump != null) System.IO.Directory.CreateDirectory(_dump);
            RemoveSave();
            _room = RoomData.Load(RoomWorld.DefaultRoom);
            _savePath = $"{TestRoot}/test_room/{_room.ManifestSha256[..16]}/inventions.json";
            AddChild(RoomBuilder.Build(_room));
            await Frames(2);
            _player = new SmallPlayerController { Name = "Player", ReadKeyboard = false, Position = _room.SpawnFor("player").PositionM + Vector3.Up * 0.008f };
            AddChild(_player);
            _companion = new CompanionAvatar { Name = "Companion", Position = _room.SpawnFor("companion", _room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f };
            AddChild(_companion);
            _companion.BindPlayer(_player);
            NewHost();
            await Frames(10);

            await TestPickUpCarryAndSetOnTheBox();
            await TestGrabRefusals();
            await TestPushes();
            await TestCompanionCarriesAndStacks();
            await TestLocksAndPreviews();
            await TestPushOffTheBox();
            await TestReload();
            await TestRolesAndReadiness();
            await TestThinWall();
            await TestNothingRestsOnCreations();
            await TestHostileSaves();
            await TestFetch();
            await TestFetchStopsAndRefusals();
            await TestFetchFloating();
            await TestCatchUp();
            await TestUnreachable();
            GD.Print($"NATIVE_KERNEL_SANDBOX: {_checks - _failures}/{_checks} checks passed; pick up, carry, drop, place with snapping, stack and push through enfractal.command, with receipts, refusals, saved poses and a reload; fetch through the host{(_dump != null ? $"; {_dumped} messages dumped" : "")}");
            RemoveSave();
            // Let the wrappers this long run left to the finalizer go before the engine tears down: in the Linux container's
            // .NET, wrappers still pending at exit can abort Godot's shutdown after every check has passed.
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError("Sandbox test exception: " + error);
            GetTree().Quit(1);
        }
    }

    // ---- the acceptance path ----

    private async Task TestPickUpCarryAndSetOnTheBox()
    {
        await Stand(_player, new Vector3(-0.3f, 0, 0.62f), Vector3.Forward, "beside the doorstop on the rug");
        var preview = Send(Command("grab-preview", "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }, preview: true), Player);
        Check(Ok(preview) && preview["preview"]!.GetValue<bool>() && _host.HeldBy(PlayerAvatar) == null, "a previewed pick-up holds nothing");
        var grab = Command("grab-doorstop", "entity.grab", new JsonObject { ["target"] = "obj:doorstop" });
        var grabbed = Send(grab, Player);
        Check(Ok(grabbed) && grabbed["transient"]!.GetValue<bool>() && Affected(grabbed) == "obj:doorstop" && grabbed["data"] == null && grabbed["revision"]!.GetValue<int>() == 0,
            "the player picks up the doorstop: a transient receipt naming it, no data, the revision unchanged");
        Check(_host.HeldBy(PlayerAvatar) == "obj:doorstop" && _host.HeldName(PlayerAvatar) == "Doorstop", "the host knows the player holds it");
        Check(Send(grab, Player)["replayed"]?.GetValue<bool>() == true, "a pick-up retry replays its receipt");
        var held = Inspect("obj:doorstop", Player);
        Check(held["held_by"]?.GetValue<string>() == PlayerAvatar && Near(held["position_m"]!, _player.GlobalPosition.X, _player.GlobalPosition.Y + 0.104, _player.GlobalPosition.Z, 0.002),
            "entity.inspect says the player holds it, over the player's head");
        Check(Node("obj:doorstop") is CollisionObject3D { CollisionLayer: 0 }, "a carried thing is no obstacle while it is carried");
        Check(Code(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Companion)) == "target_busy", "the companion cannot take what the player holds");
        Check(Code(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:rug" }), Player)) == "target_busy", "an avatar carries one thing at a time");
        Check(Code(Send(Command(NextId("lock"), "protect.lock", new JsonObject { ["targets"] = new JsonArray("obj:doorstop") }, expectedRevision: _host.Revision), Player)) == "target_busy",
            "a carried thing cannot be protected until it is put down");

        // Across the room: from the rug by the door to beside the big box.
        var start = _player.GlobalPosition;
        Check(await WalkTo(_player, new Vector2(0.87f, 0.17f)), "the player walks across the room carrying it");
        await Face(_player, Vector3.Right);
        var carried = Inspect("obj:doorstop", Player);
        Check(start.DistanceTo(_player.GlobalPosition) > 1.1f && Near(carried["position_m"]!, _player.GlobalPosition.X, _player.GlobalPosition.Y + 0.104, _player.GlobalPosition.Z, 0.002) &&
            Node("obj:doorstop")!.GlobalPosition.DistanceTo(_player.GlobalPosition + Vector3.Up * 0.104f) < 0.002f,
            $"the doorstop rode with the player {start.DistanceTo(_player.GlobalPosition):0.00} m, in the summary and in the scene");

        var put = Command("put-on-box", "entity.release", new JsonObject { ["placement"] = Placement(0.99, 0.3, 0.2, "obj:box") });
        var set = Send(put, Player);
        Check(Ok(set) && !set["transient"]!.GetValue<bool>() && Affected(set) == "obj:doorstop" && set["data"] == null && set["revision"]!.GetValue<int>() == 1,
            "the player sets it on the box: a durable receipt naming it, no data, revision 1: " + Code(set));
        var onBox = Inspect("obj:doorstop", Player);
        Check(onBox["held_by"] == null && Near(onBox["position_m"]!, 0.99, 0.30, 0.2, 0.0005) && onBox["revision"]!.GetValue<int>() == 1,
            "it rests on the box's top at the spot asked, snapped to 0.30 m: " + onBox["position_m"]!.ToJsonString());
        Check(_host.HeldBy(PlayerAvatar) == null && Node("obj:doorstop") is CollisionObject3D { CollisionLayer: RoomBuilder.WorldLayer }, "the hold ended and it collides again");
        var top = Ray(new Vector3(0.99f, 1, 0.2f), new Vector3(0.99f, -0.1f, 0.2f));
        Check(top.Entity == "obj:doorstop" && Mathf.Abs(top.Height - 0.34f) < 0.0005f, $"the room's physics has it there at once: a ray finds its top at {top.Height:0.0000} m");
        Check(Send(put, Player)["replayed"]?.GetValue<bool>() == true && Send(put, Player)["revision"]!.GetValue<int>() == 1, "a put-down retry replays its receipt");
        var lookup = Query("receipt.lookup", new JsonObject { ["action_id"] = "put-on-box" }, Player);
        Check(lookup["data"]!["found"]!.GetValue<bool>() && lookup["data"]!["receipt"]!["op"]!.GetValue<string>() == "entity.release" && lookup["data"]!["receipt"]!["data"] == null,
            "receipt.lookup finds the durable receipt");
        Check(Code(Send(Command("put-on-box", "entity.release", new JsonObject()), Player)) == "action_id_conflict", "the same action id with other content is a conflict");
    }

    private async Task TestGrabRefusals()
    {
        var tooHeavy = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:book" }), Player);
        Check(Code(tooHeavy) == "target_too_heavy" && tooHeavy["error"]!["allowed"]!.GetValue<double>() == 0.5 && tooHeavy["error"]!["actual"]!.GetValue<double>() == 0.6,
            "the 0.6 kg book is too heavy for the player (0.5 kg), with both numbers");
        Check(Code(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:table" }), Player)) == "target_too_heavy", "so is the table");
        Check(Code(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "shell:floor" }), Player)) == "permission_denied", "the floor cannot be picked up");
        Check(Code(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = CompanionAvatar }), Player)) == "permission_denied", "nor can an avatar");
        Check(Code(Send(Command(NextId("drop"), "entity.release", new JsonObject()), Player)) == "invalid_args", "putting down with empty hands is refused");
        var player = _player.GlobalPosition;
        await Stand(_player, new Vector3(0.6f, 0, 1.0f), Vector3.Forward, "far from the box");
        var far = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player);
        Check(Code(far) == "out_of_bounds" && far["error"]!["message"]!.GetValue<string>().Contains("out of reach", StringComparison.Ordinal) && far["error"]!["retryable"]!.GetValue<bool>(),
            "a thing out of reach is refused: out of reach, move closer");
        await Stand(_player, player, Vector3.Right, "back beside the box");
        foreach (var (op, args) in new (string, JsonObject)[]
        {
            ("entity.grab", new JsonObject { ["target"] = "obj:rug", ["actor"] = PlayerAvatar }),
            ("entity.release", new JsonObject { ["actor"] = PlayerAvatar }),
            ("entity.push", new JsonObject { ["target"] = "obj:rug", ["actor"] = PlayerAvatar, ["distance_m"] = 0.1 }),
        })
            Check(Code(Send(Command(NextId("actor"), op, args), Companion)) == "actor_denied", $"{op}: the companion never acts through the player's avatar");
        Check(Code(Send(Command(NextId("actor"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop", ["actor"] = "avatar:stranger" }), Player)) == "target_not_found", "an avatar not in the room is not found");
        await Stand(_companion, new Vector3(1.6f, 0.01f, 0.2f), Vector3.Left, "behind the box");
        // The team's sight is shared: the player steps behind the box too, so neither avatar sees the book.
        await Stand(_player, new Vector3(1.7f, 0.01f, 0.32f), Vector3.Left, "behind the box too");
        var hidden = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:book" }), Companion);
        var unknown = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:nothing" }), Companion);
        Check(Code(hidden) == "target_not_found" && hidden["error"]!["message"]!.GetValue<string>() == unknown["error"]!["message"]!.GetValue<string>(),
            "the companion cannot pick up what it cannot see: the same answer as for a thing that does not exist");
    }

    private async Task TestPushes()
    {
        await Stand(_player, new Vector3(0.45f, 0, 0.225f), Vector3.Forward, "on the rug's edge, south of the book");
        var push = Command("push-book", "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0.2 });
        var pushed = Send(push, Player);
        var book = Inspect("obj:book", Player);
        Check(Ok(pushed) && !pushed["transient"]!.GetValue<bool>() && Affected(pushed) == "obj:book" && pushed["data"] == null && Near(book["position_m"]!, 0.45, 0, -0.1, 0.001),
            "the player pushes the book 20 cm away along the floor: " + book["position_m"]!.ToJsonString());
        Check(Send(push, Player)["replayed"]?.GetValue<bool>() == true && Near(Inspect("obj:book", Player)["position_m"]!, 0.45, 0, -0.1, 0.001), "a push retry replays and pushes nothing more");
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:book", ["placement"] = Placement(0.45, 0.3, -1.2) }), Player)) &&
            Near(Inspect("obj:book", Player)["position_m"]!, 0.45, 0, -1.2, 0.0005), "the player's editing hand places the book by the north wall, and it settles on the floor");
        await Stand(_player, new Vector3(0.45f, 0, -1.08f), Vector3.Back, "south of the book");
        var wall = Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 1.0 }), Player);
        var stopped = Inspect("obj:book", Player)["bounds_m"]!["min_m"]![2]!.GetValue<double>();
        Check(Ok(wall) && stopped >= -1.5005 && stopped <= -1.497, $"a 1 m push stops early at the wall (the book's edge at z {stopped:0.0000})");
        await Stand(_player, new Vector3(0.45f, 0, -1.32f), Vector3.Forward, "right behind the book");
        var again = Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0.1 }), Player);
        Check(Code(again) == "occupied", $"pushing it on into the wall moves nothing: something is in the way ({Code(again)}, edge now at z {Inspect("obj:book", Player)["bounds_m"]!["min_m"]![2]!.GetValue<double>():0.0000})");
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:book", ["placement"] = Placement(0.45, 0, 0.1) }), Player)), "the book goes back by the rug");
        await Stand(_player, new Vector3(0.45f, 0, -0.02f), Vector3.Back, "north of the book");
        var ledge = Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0.2 }), Player);
        Check(Ok(ledge) && Near(Inspect("obj:book", Player)["position_m"]!, 0.45, 0.006, 0.3, 0.001), "a push rides the book over the rug's 6 mm edge onto the rug: " + Inspect("obj:book", Player)["position_m"]!.ToJsonString());
        await Stand(_player, new Vector3(0.45f, 0.006f, 0.42f), Vector3.Forward, "on the rug, south of the book");
        var toward = Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0.05, ["toward_m"] = new JsonArray(1.45, 5, 0.3) }), Player);
        Check(Ok(toward) && Near(Inspect("obj:book", Player)["position_m"]!, 0.5, 0.006, 0.3, 0.001), "toward_m pushes toward a point, horizontally only");
        var heavy = Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:box", ["distance_m"] = 0.1 }), Player);
        Check(Code(heavy) == "target_too_heavy" && heavy["error"]!["allowed"]!.GetValue<double>() == 1.0 && heavy["error"]!["actual"]!.GetValue<double>() == 1.5,
            "the 1.5 kg box is too heavy for the player to push (twice the carry limit, 1 kg)");
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 1.5 }), Player, contractValid: false)) == "request_invalid" &&
            Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0 }), Player, contractValid: false)) == "request_invalid",
            "a push is more than 0 and at most 1 m");
        var centre = Inspect("obj:book", Player)["bounds_m"]!;
        var middle = new JsonArray((centre["min_m"]![0]!.GetValue<double>() + centre["max_m"]![0]!.GetValue<double>()) / 2, 0.5, (centre["min_m"]![2]!.GetValue<double>() + centre["max_m"]![2]!.GetValue<double>()) / 2);
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0.1, ["toward_m"] = middle }), Player)) == "invalid_args", "a push toward the thing's own centre has no direction");
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:doorstop", ["distance_m"] = 0.1 }), Player)) == "out_of_bounds", "a thing out of reach cannot be pushed");
        await Stand(_companion, new Vector3(1.1f, 0, 0.42f), Vector3.Forward, "south of the box");
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:box", ["distance_m"] = 0.1 }), Companion)) == "target_busy",
            "the box with the doorstop on it stays put: take what is on it off first");
        await Stand(_player, new Vector3(1.5f, 0, 1.0f), Vector3.Forward, "off the rug");
        await Stand(_companion, new Vector3(0.95f, 0, 0.6f), Vector3.Left, "east of the rug");
        var rug = Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:rug", ["distance_m"] = 0.1 }), Companion);
        Check(Code(rug) == "target_busy", "so does the rug with the book on it");
        // The book goes back to where it started, for the companion.
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:book", ["placement"] = Placement(0.45, 0, 0.1) }), Player)), "the book goes back to its place");
    }

    private async Task TestCompanionCarriesAndStacks()
    {
        await Stand(_companion, new Vector3(0.45f, 0, 0.225f), Vector3.Forward, "south of the book");
        var grabbed = Send(Command("companion-grab-book", "entity.grab", new JsonObject { ["target"] = "obj:book" }), Companion);
        Check(Ok(grabbed) && _host.HeldBy(CompanionAvatar) == "obj:book" && Inspect("obj:book", Companion)["held_by"]?.GetValue<string>() == CompanionAvatar,
            "the companion picks up the book (it carries up to 2 kg)");
        Check(Code(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:book", ["placement"] = Placement(0, 0, 0) }), Player)) == "target_busy",
            "the player's hand cannot take what the companion holds");
        var directed = Send(Command(NextId("drop"), "entity.release", new JsonObject { ["actor"] = CompanionAvatar, ["placement"] = Placement(1.2, 0.3, 0.1, "obj:box") }), Player);
        Check(Code(directed) is "out_of_bounds" or "occupied" && _host.HeldBy(CompanionAvatar) == "obj:book",
            "the player may direct the companion's avatar; from here the box is out of its reach (or the doorstop is in its way): " + Code(directed));
        await Stand(_companion, new Vector3(1.1f, 0, -0.03f), Vector3.Back, "north of the box");
        var crowded = Send(Command(NextId("drop"), "entity.release", new JsonObject { ["placement"] = Placement(1.1, 0.3, 0.2, "obj:box") }), Companion);
        Check(Code(crowded) == "occupied" && _host.HeldBy(CompanionAvatar) == "obj:book", "the middle of the box is taken by the doorstop: no room, and the companion still holds the book");
        var stacked = Send(Command("companion-book-on-box", "entity.release", new JsonObject { ["placement"] = Placement(1.2, 0.3, 0.1, "obj:box") }), Companion);
        var book = Inspect("obj:book", Player);
        Check(Ok(stacked) && book["held_by"] == null && Near(book["position_m"]!, 1.164, 0.30, 0.101, 0.0005),
            "set on the box's free corner, the book is snapped inside the box's top: " + book["position_m"]!.ToJsonString());
        await Stand(_player, new Vector3(0.9f, 0, 0.1f), Vector3.Right, "west of the box");
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player)), "the player takes the doorstop off the box");
        Check(Code(Send(Command(NextId("place"), "entity.release", new JsonObject { ["placement"] = Placement(0, 0, 0, CompanionAvatar) }), Player)) == "invalid_args",
            "nothing goes on top of what has no walkable top");
        Check(Code(Send(Command(NextId("drop"), "entity.release", new JsonObject { ["placement"] = Placement(0, 0, 0, "obj:doorstop") }), Player)) == "invalid_args", "a thing cannot be put on itself");
        var table = Send(Command(NextId("drop"), "entity.release", new JsonObject { ["placement"] = Placement(-0.9, 0.75, -0.9, "obj:table") }), Player);
        Check(Code(table) == "out_of_bounds", "the table is out of reach");
        var stack = Send(Command("stack-doorstop", "entity.release", new JsonObject { ["placement"] = Placement(1.0, 0.34, 0.1, "obj:book") }), Player);
        var doorstop = Inspect("obj:doorstop", Player);
        // The doorstop turned a quarter with the player on the way across, so it is 8 cm along x: snapped in from the book's edge.
        Check(Ok(stack) && Near(doorstop["position_m"]!, 1.095, 0.34, 0.1, 0.0005), "the player stacks the doorstop on the book on the box: " + doorstop["position_m"]!.ToJsonString());
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0.1 }), Player)) == "target_busy", "the book under the doorstop stays put");
        await Stand(_player, new Vector3(0.6f, 0, 1.0f), Vector3.Forward, "out of the way");
    }

    private async Task TestLocksAndPreviews()
    {
        Check(Ok(Send(Command(NextId("lock"), "protect.lock", new JsonObject { ["targets"] = new JsonArray("obj:doorstop") }, expectedRevision: _host.Revision), Player)), "the player protects the doorstop");
        await Stand(_player, new Vector3(0.9f, 0, 0.1f), Vector3.Right, "west of the box");
        Check(Code(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player)) == "target_protected", "a protected thing cannot be picked up");
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:doorstop", ["distance_m"] = 0.1 }), Player)) == "target_protected", "nor pushed");
        Check(Code(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(0, 0, 0) }), Player)) == "target_protected", "nor placed");
        Check(Ok(Send(Command(NextId("unlock"), "protect.unlock", new JsonObject { ["targets"] = new JsonArray("obj:doorstop") }, expectedRevision: _host.Revision), Player)), "the player unprotects it");
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player)), "the player takes it again");
        var revision = _host.Revision;
        var preview = Send(Command(NextId("drop"), "entity.release", new JsonObject { ["placement"] = Placement(1.0, 0.3, 0.25, "obj:box") }, preview: true), Player);
        Check(Ok(preview) && preview["preview"]!.GetValue<bool>() && _host.Revision == revision && _host.HeldBy(PlayerAvatar) == "obj:doorstop", "a previewed put-down moves nothing and keeps the hold");
        var blocked = Send(Command(NextId("drop"), "entity.release", new JsonObject()), Player);
        Check(Code(blocked) == "occupied" && _host.HeldBy(PlayerAvatar) == "obj:doorstop", "facing the box there is no room to set it down: the player keeps it");
        await Face(_player, Vector3.Left);
        var dropped = Send(Command("drop-doorstop", "entity.release", new JsonObject()), Player);
        var doorstop = Inspect("obj:doorstop", Player);
        Check(Ok(dropped) && Near(doorstop["position_m"]!, 0.83, 0, 0.1, 0.0005), "with no placement it is set down in front of the player and settles on the floor: " + doorstop["position_m"]!.ToJsonString());
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player)), "the player picks it up again");
        Check(Ok(Send(Command("doorstop-back", "entity.release", new JsonObject { ["placement"] = Placement(0.99, 0.3, 0.27, "obj:box") }), Player)) &&
            Near(Inspect("obj:doorstop", Player)["position_m"]!, 0.99, 0.3, 0.27, 0.0005), "and sets it on the box again, by the book");
    }

    private async Task TestPushOffTheBox()
    {
        await Stand(_player, new Vector3(0.9f, 0, 0.1f), Vector3.Right, "west of the box");
        var off = Send(Command("push-off-box", "entity.push", new JsonObject { ["target"] = "obj:doorstop", ["distance_m"] = 0.35, ["toward_m"] = new JsonArray(2.0, 0.3, 0.27) }), Player);
        var doorstop = Inspect("obj:doorstop", Player);
        Check(Ok(off) && Math.Abs(doorstop["position_m"]![1]!.GetValue<double>()) < 0.0005 && doorstop["bounds_m"]!["min_m"]![0]!.GetValue<double>() > 1.274,
            "pushed along the box's top, the doorstop goes over the far edge and falls to the floor: " + doorstop["position_m"]!.ToJsonString());
        var at = Vec(doorstop["position_m"]!);
        var floor = Ray(at + Vector3.Up, at + Vector3.Down * 0.1f);
        Check(floor.Entity == "obj:doorstop" && Mathf.Abs(floor.Height - 0.04f) < 0.0005f, "and the room's physics finds it there");
    }

    private async Task TestReload()
    {
        // A creation the player builds on the book, which play moved: on reload it must still stand on the moved book.
        var book = Inspect("obj:book", Player)["position_m"]!;
        var creation = Send(Command(NextId("place"), "creation.place", new JsonObject
        {
            ["source"] = Source("Book lamp"), ["placement"] = Placement(book[0]!.GetValue<double>(), book[1]!.GetValue<double>() + 0.04, book[2]!.GetValue<double>(), "obj:book"),
        }), Player);
        Check(Ok(creation), "the player builds on the moved book: " + Code(creation) + " " + creation["error"]?["message"]?.GetValue<string>() + " " + creation["error"]?["field_path"]?.GetValue<string>());
        var put = Command("put-on-box", "entity.release", new JsonObject { ["placement"] = Placement(0.99, 0.3, 0.2, "obj:box") });
        var saved = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(_savePath));
        Check(saved.Contains("\"object_poses\"", StringComparison.Ordinal) && saved.Contains("\"obj:doorstop\"", StringComparison.Ordinal) && !saved.Contains("held_by", StringComparison.Ordinal),
            "the save keeps the poses and never who holds what");
        var doorstop = Inspect("obj:doorstop", Player);
        var books = Inspect("obj:book", Player);
        _host.QueueFree();
        await Frames(2);
        // A stale scene: the new host must rebuild it from the save, not trust the nodes.
        Node("obj:doorstop")!.GlobalPosition = new Vector3(-1, 1, -1);
        Node("obj:book")!.GlobalPosition = new Vector3(0, 0, 0);
        await Frames(2);
        Check(Ray(new Vector3(1.164f, 1, 0.101f), new Vector3(1.164f, -0.1f, 0.101f)).Entity == "obj:box", "(the stale scene has reached the physics server: no book on the box)");
        NewHost();
        await Frames(3);
        Check(_host.SaveNotice.Length == 0 && Canonical(Inspect("obj:doorstop", Player)) == Canonical(doorstop) && Canonical(Inspect("obj:book", Player)) == Canonical(books),
            "after a reload the doorstop and the book are where play left them, revisions included");
        Check(Node("obj:doorstop")!.GlobalPosition.DistanceTo(Vec(doorstop["position_m"]!)) < 0.0002f && Node("obj:book")!.GlobalPosition.DistanceTo(Vec(books["position_m"]!)) < 0.0002f,
            "and the scene is rebuilt from the saved poses");
        var entities = _host.Entities();
        Check(entities.Any(e => e["kind"]!.GetValue<string>() == "creation" && e["display_name"]!.GetValue<string>() == "Book lamp"),
            "the creation on the moved book loaded: the poses reach the scene before the save's creations are checked");
        Check(Send(put, Player)["replayed"]?.GetValue<bool>() == true, "a put-down's receipt replays after the reload");
        await Stand(_player, new Vector3(0.9f, 0, 0.1f), Vector3.Right, "west of the box");
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:book", ["distance_m"] = 0.1 }), Player)) == "target_busy",
            "the book with the lamp on it stays put");
        Check(_player.TryTeleportTo(Vec(doorstop["position_m"]!) + Vector3.Up * 0.03f) && Mathf.Abs(_player.GlobalPosition.Y - 0.04f) < 0.006f,
            $"an avatar can stand on the moved doorstop ({_player.GlobalPosition.Y:0.000} m)");
    }

    // ---- the P3 review (Codex, 9cad588) ----

    /// <summary>Review major 1: a pick-up needs what any change needs, a ready room and a role that may build, before the hold begins.</summary>
    private async Task TestRolesAndReadiness()
    {
        await Stand(_player, new Vector3(1.5f, 0, 1.0f), Vector3.Forward, "off the doorstop, out of the way");
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(0.6, 0, -0.5) }), Player)), "the doorstop goes to the open floor");
        await Stand(_companion, new Vector3(0.6f, 0, -0.38f), Vector3.Forward, "south of the doorstop");
        Check(_host.Authority.Call("set_role", Player, Companion, "visitor").AsGodotDictionary()["ok"].AsBool(), "the player makes the companion a visitor");
        var visitor = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Companion);
        Check(Code(visitor) == "permission_denied" && _host.HeldBy(CompanionAvatar) == null && Node("obj:doorstop") is CollisionObject3D { CollisionLayer: RoomBuilder.WorldLayer },
            "a visitor cannot pick anything up, and the thing is untouched: " + Code(visitor));
        Check(_host.Authority.Call("set_role", Player, Companion, "editor").AsGodotDictionary()["ok"].AsBool(), "the companion is an editor again");
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Companion)), "an editor picks it up");
        _host.Authority.Call("set_role", Player, Companion, "visitor");
        await Frames(3);
        Check(_host.HeldBy(CompanionAvatar) == null && Node("obj:doorstop")!.GlobalPosition.DistanceTo(new Vector3(0.6f, 0, -0.5f)) < 0.001f &&
            Node("obj:doorstop") is CollisionObject3D { CollisionLayer: RoomBuilder.WorldLayer },
            "demoted mid-carry, the companion lets go: the doorstop goes back to where it was last put down");
        _host.Authority.Call("set_role", Player, Companion, "editor");
        await Stand(_companion, new Vector3(1.5f, 0, -1.0f), Vector3.Forward, "out of the way");

        // A save that failed to load: the room is not ready, so nothing can be picked up that could not be put down.
        _host.QueueFree();
        await Frames(2);
        var corrupt = $"{TestRoot}/corrupt/inventions.json";
        var absolute = ProjectSettings.GlobalizePath(corrupt);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
        System.IO.File.WriteAllText(absolute, "{not json");
        _host = CommandHost.Create(this, _room, _player, _companion, null, corrupt);
        _host.Clock = () => _now;
        await Frames(3);
        await Stand(_player, new Vector3(-0.3f, 0, 0.62f), Vector3.Forward, "beside the doorstop's manifest place");
        var unready = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player);
        Check(!_host.Authority.Call("is_ready").AsBool() && Code(unready) == "not_ready" && _host.HeldBy(PlayerAvatar) == null,
            "with a save that failed to load, a pick-up is not_ready and nothing is held: " + Code(unready));
        Check(Node("obj:doorstop")!.GlobalPosition.DistanceTo(new Vector3(-0.3f, 0.006f, 0.5f)) < 0.001f, "and the scene shows the room as its manifest has it, the state the host answers from");
        _host.QueueFree();
        await Frames(2);
        NewHost();
        await Frames(3);
        Check(_host.Authority.Call("is_ready").AsBool() && Node("obj:doorstop")!.GlobalPosition.DistanceTo(new Vector3(0.6f, 0, -0.5f)) < 0.001f, "back on the real save, the doorstop is where it was put");
    }

    /// <summary>Review major 3: a thin wall stops a reach and a put-down, whatever the distance.</summary>
    private async Task TestThinWall()
    {
        var wall = new StaticBody3D { Name = "Partition", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = new Vector3(0.6f, 0.15f, -0.396f) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f, 0.3f, 0.004f) } });
        AddChild(wall);
        await Frames(2);
        await Stand(_player, new Vector3(0.6f, 0, -0.37f), Vector3.Forward, "behind a 4 mm partition from the doorstop");
        var through = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player);
        Check(Code(through) == "out_of_bounds" && _host.HeldBy(PlayerAvatar) == null, "the doorstop 9 cm away behind the partition is out of reach: " + Code(through));
        if (_host.HeldBy(PlayerAvatar) != null) Send(Command(NextId("drop"), "entity.release", new JsonObject { ["placement"] = Placement(0.6, 0, -0.5) }), Player);
        Check(Code(Send(Command(NextId("push"), "entity.push", new JsonObject { ["target"] = "obj:doorstop", ["distance_m"] = 0.05 }), Player)) == "out_of_bounds", "and cannot be pushed through it");
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(0.6, 0, -0.28) }), Player)), "the doorstop goes to the player's side");
        await Face(_player, Vector3.Back);
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player)), "the player picks it up on its own side");
        await Face(_player, Vector3.Forward);
        var drop = Send(Command(NextId("drop"), "entity.release", new JsonObject()), Player);
        Check(Code(drop) == "occupied" && _host.HeldBy(PlayerAvatar) == "obj:doorstop", "facing the partition, it cannot be set down on the far side: " + Code(drop));
        var beyond = Send(Command(NextId("drop"), "entity.release", new JsonObject { ["placement"] = Placement(0.6, 0, -0.5) }), Player);
        Check(!Ok(beyond) && _host.HeldBy(PlayerAvatar) == "obj:doorstop", "nor placed there: " + Code(beyond));
        await Face(_player, Vector3.Back);
        Check(Ok(Send(Command(NextId("drop"), "entity.release", new JsonObject()), Player)), "set down on its own side, it goes");
        wall.QueueFree();
        await Frames(2);
    }

    /// <summary>
    /// Review major 4: a thing set on a creation would float once the creation goes, and load floating. Things rest only
    /// on the room's surfaces and objects.
    /// </summary>
    private async Task TestNothingRestsOnCreations()
    {
        var block = Send(Command(NextId("place"), "creation.place", new JsonObject { ["source"] = Source("Block"), ["placement"] = Placement(-1.0, 0, 0.0) }), Player);
        var id = block["created"]?[0]?.GetValue<string>() ?? "";
        Check(Ok(block), "the player builds a 10 cm block on the floor");
        var onBlock = Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(-1.0, 0.3, 0.0) }), Player);
        Check(Code(onBlock) == "occupied" && onBlock["error"]!["message"]!.GetValue<string>().Contains("creation", StringComparison.Ordinal),
            "the doorstop cannot be set on the block: " + Code(onBlock) + " " + Inspect("obj:doorstop", Player)["position_m"]?.ToJsonString());
        Check(Ok(Send(Command(NextId("remove"), "entity.remove", new JsonObject { ["target"] = id }, expectedRevision: _host.Revision), Player)), "the block is removed");
        await Frames(3);
        var floating = _host.Entities().Where(e => e["kind"]!.GetValue<string>() == "object" && !Supported(e)).Select(e => e["id"]!.GetValue<string>()).ToList();
        Check(floating.Count == 0, "and nothing is left floating: " + string.Join(", ", floating));
        await Frames(1);
    }

    /// <summary>
    /// Review major 2: a save's object poses are checked against the room as it is, not trusted. A pose raised into mid-air,
    /// sunk into a wall behind bounds that hide it, inside another object, or turned over refuses the whole save (as an
    /// unplaceable creation does): it is not loaded, its file is untouched, and the room shows its manifest places.
    /// </summary>
    private async Task TestHostileSaves()
    {
        var valid = JsonNode.Parse(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(_savePath)))!.AsObject();
        Check(valid["object_poses"]?["obj:doorstop"] != null, "the save has the doorstop's pose");
        var cases = new (string Label, Action<JsonObject> Edit)[]
        {
            ("raised 20 cm into mid-air", pose => { Shift(pose["position_m"]!, 1, 0.2); Shift(pose["bounds"]!["min_m"]!, 1, 0.2); Shift(pose["bounds"]!["max_m"]!, 1, 0.2); }),
            ("sunk into the east wall behind tiny bounds", pose => { pose["position_m"] = new JsonArray(1.99, 0, 0.0); pose["bounds"] = Box(1.989, 0, -0.001, 1.991, 0.001, 0.001); }),
            ("inside the big box behind tiny bounds", pose => { pose["position_m"] = new JsonArray(1.1, 0.1, 0.2); pose["bounds"] = Box(1.099, 0.1, 0.199, 1.101, 0.101, 0.201); }),
            ("turned upside down into the floor", pose => { pose["position_m"] = new JsonArray(0.6, 0.0, -0.8); pose["rotation"] = new JsonArray(1.0, 0.0, 0.0, 0.0); pose["bounds"] = Box(0.599, 0, -0.801, 0.601, 0.001, -0.799); }),
        };
        var index = 0;
        foreach (var (label, edit) in cases)
        {
            var hostile = (JsonObject)valid.DeepClone();
            edit(hostile["object_poses"]!["obj:doorstop"]!.AsObject());
            var path = $"{TestRoot}/hostile/{++index}/inventions.json";
            var absolute = ProjectSettings.GlobalizePath(path);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
            System.IO.File.WriteAllBytes(absolute, CanonicalJson.Bytes(hostile));
            _host.QueueFree();
            await Frames(2);
            _host = CommandHost.Create(this, _room, _player, _companion, null, path);
            _host.Clock = () => _now;
            await Frames(3);
            Check(!_host.Authority.Call("is_ready").AsBool() && Node("obj:doorstop")!.GlobalPosition.DistanceTo(new Vector3(-0.3f, 0.006f, 0.5f)) < 0.001f &&
                System.IO.File.ReadAllBytes(absolute).SequenceEqual(CanonicalJson.Bytes(hostile)),
                $"a save with the doorstop {label} is refused, left as it was, and the room shows the manifest's places");
        }
        _host.QueueFree();
        await Frames(2);
        NewHost();
        await Frames(3);
        Check(_host.Authority.Call("is_ready").AsBool(), "the untouched save still loads");
    }

    // ---- fetch on the real host (docs/companion/proposals/a2-real-host.md, section 3) ----

    /// <summary>The companion fetches the doorstop: it walks there, picks it up with entity.grab's checks, brings it back, and keeps holding it.</summary>
    private async Task TestFetch()
    {
        await Stand(_player, new Vector3(0.0f, 0.006f, 1.0f), Vector3.Left, "on the rug's south side");
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(-1.2, 0, 0.9) }), Player)), "the doorstop goes to the floor west of the rug");
        await Stand(_companion, new Vector3(-0.5f, 0.006f, 0.9f), Vector3.Left, "on the rug's west side, facing the doorstop");
        var fetch = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Companion);
        Check(Ok(fetch) && fetch["transient"]!.GetValue<bool>() && fetch["data"]?["target_seen"]?.GetValue<string>() == "now" &&
            System.Text.RegularExpressions.Regex.IsMatch(fetch["job_id"]?.GetValue<string>() ?? "", @"\Ajob-[a-z2-7]{26}\z") && _host.HeldBy(CompanionAvatar) == null,
            "the companion sets out to fetch the doorstop: a transient receipt and a job, nothing held yet: " + Code(fetch));
        var held = await UntilJobEnds(1500, () => _host.HeldBy(CompanionAvatar) == "obj:doorstop");
        var job = JobStatus(fetch, Companion);
        Check(held && job?["state"]?.GetValue<string>() == "succeeded", "it picked the doorstop up on the way and the job succeeds back at the player: " + job?.ToJsonString());
        var planar = new Vector2(_companion.GlobalPosition.X - _player.GlobalPosition.X, _companion.GlobalPosition.Z - _player.GlobalPosition.Z).Length();
        Check(planar <= GarageGame.Native.CompanionAvatar.ComeArrivalM + 0.02f && _host.HeldBy(CompanionAvatar) == "obj:doorstop" && Inspect("obj:doorstop", Companion)["held_by"]?.GetValue<string>() == CompanionAvatar,
            $"the companion stands {planar:0.00} m from the player, still holding the doorstop (entity.inspect says so)");
        var down = Send(Command(NextId("down"), "entity.release", new JsonObject { ["actor"] = CompanionAvatar }), Companion);
        var box = SandboxControls.Box(Inspect("obj:doorstop", Player));
        var playerBox = new Aabb(_player.GlobalPosition - new Vector3(_player.BodyRadiusM, 0, _player.BodyRadiusM), new Vector3(_player.BodyRadiusM * 2, _player.BodyHeightM, _player.BodyRadiusM * 2));
        Check(Ok(down) && !down["transient"]!.GetValue<bool>() && _host.HeldBy(CompanionAvatar) == null && !box.Intersects(playerBox),
            "entity.release puts it down (a durable receipt): with the player in front, beside the companion instead: " + Code(down));
    }

    /// <summary>Stops and new goals cancel a fetch in either phase; set-time refusals are entity.grab's.</summary>
    private async Task TestFetchStopsAndRefusals()
    {
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(-1.2, 0, 0.9) }), Player)), "the doorstop goes back west of the rug");
        await Stand(_companion, new Vector3(-0.5f, 0.006f, 0.9f), Vector3.Left, "facing the doorstop again");
        var fetch = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Companion);
        await Frames(20);
        var stop = Send(Command(NextId("stop"), "goal.stop", new JsonObject()), Companion);
        await Frames(2);
        Check(Ok(fetch) && Ok(stop) && JobStatus(fetch, Companion)?["state"]?.GetValue<string>() == "cancelled" && _host.HeldBy(CompanionAvatar) == null && _companion.CurrentIntent == "stop",
            "goal.stop cancels a fetch on its way there: nothing is picked up");

        await Stand(_companion, new Vector3(-1.2f, 0, 0.78f), Vector3.Forward, "beside the doorstop");
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Companion)), "the companion picks the doorstop up itself");
        var home = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Companion);
        Check(Ok(home) && _host.RunningGoal(CompanionAvatar)?.Goal == "fetch" && _companion.CurrentIntent == "come", "fetching what it already holds starts on the way back");
        await Frames(10);
        var follow = Send(Command(NextId("follow"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "follow" }), Companion);
        Check(Ok(follow) && JobStatus(home, Companion)?["state"]?.GetValue<string>() == "cancelled" && _host.HeldBy(CompanionAvatar) == "obj:doorstop",
            "a new goal cancels a fetch on its way back, and the companion keeps holding the doorstop");
        var busy = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:book" }), Player);
        Check(Code(busy) == "target_busy" && busy["error"]!["field_path"]!.GetValue<string>() == "$.args.actor", "holding something else, a fetch is target_busy at the actor");
        Check(Ok(Send(Command(NextId("down"), "entity.release", new JsonObject { ["actor"] = CompanionAvatar }), Player)), "the player has the companion put it down");
        var heavy = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:table" }), Player);
        Check(Code(heavy) == "target_too_heavy" && heavy["error"]!["allowed"]!.GetValue<double>() == 2.0 && heavy["error"]!["actual"]!.GetValue<double>() == 25.0,
            "the 25 kg table is too heavy to fetch, with both numbers");
        Check(Ok(Send(Command(NextId("lock"), "protect.lock", new JsonObject { ["targets"] = new JsonArray("obj:doorstop") }, expectedRevision: _host.Revision), Player)), "the player protects the doorstop");
        Check(Code(Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Player)) == "target_protected",
            "a protected thing cannot be fetched");
        Check(Ok(Send(Command(NextId("unlock"), "protect.unlock", new JsonObject { ["targets"] = new JsonArray("obj:doorstop") }, expectedRevision: _host.Revision), Player)), "and unprotects it");
    }

    /// <summary>
    /// The Gubble floats (founder, 8 October): it fetches the doorstop from the top of the 30 cm box, which no walk reaches,
    /// by floating up onto the box, picking it up there, and floating down and back to the player, still holding it.
    /// </summary>
    private async Task TestFetchFloating()
    {
        var box = SandboxControls.Box(Inspect("obj:box", Player));
        var centre = box.GetCenter();
        // Earlier checks left things on the box: the first free spot on its top.
        JsonObject onBox = new();
        foreach (var (dx, dz) in new[] { (0.0, 0.0), (-0.09, -0.09), (0.09, -0.09), (-0.09, 0.09), (0.09, 0.09) })
            if (Ok(onBox = Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(centre.X + dx, box.End.Y, centre.Z + dz, "obj:box") }), Player))) break;
        Check(Ok(onBox), "the doorstop goes on top of the big box: " + onBox.ToJsonString());
        await Stand(_player, new Vector3(0.0f, 0.006f, 1.0f), Vector3.Left, "on the rug's south side");
        await Stand(_companion, new Vector3(-0.5f, 0.006f, 0.9f), Vector3.Right, "on the rug's west side");
        var starts = _companion.FloatStarts;
        var highest = 0.0f;
        var fetch = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Player);
        var held = await UntilJobEnds(1800, () =>
        {
            highest = Mathf.Max(highest, _companion.GlobalPosition.Y);
            return _host.HeldBy(CompanionAvatar) == "obj:doorstop";
        });
        var job = JobStatus(fetch, Player);
        var planar = new Vector2(_companion.GlobalPosition.X - _player.GlobalPosition.X, _companion.GlobalPosition.Z - _player.GlobalPosition.Z).Length();
        Check(Ok(fetch) && held && job?["state"]?.GetValue<string>() == "succeeded" && _companion.FloatStarts > starts && highest > box.End.Y - 0.01f &&
            planar <= GarageGame.Native.CompanionAvatar.ComeArrivalM + 0.02f && _host.HeldBy(CompanionAvatar) == "obj:doorstop",
            $"the Gubble floats up onto the box (highest feet {highest:0.000} m, the top at {box.End.Y:0.000} m), picks the doorstop up and floats back to the player holding it ({planar:0.00} m away): " + job?.ToJsonString());
        Check(Ok(Send(Command(NextId("down"), "entity.release", new JsonObject { ["actor"] = CompanionAvatar }), Player)), "and puts it down beside the player");
    }

    /// <summary>
    /// The Gubble catching up (S8 of the 10x world): following, far from the player and out of their view (here past a lowered
    /// CatchUpBeyondM: the test room is small), it reappears beside them, and its AI's next result, of any kind, carries the
    /// caught_up notice, once; the player's results never do. A fetch carried back to a player facing away never catches up.
    /// </summary>
    private async Task TestCatchUp()
    {
        var previous = GetViewport().GetCamera3D();
        _player.EyeCamera.MakeCurrent();
        _companion.CatchUpBeyondM = 0.4f;
        await Stand(_player, new Vector3(0.0f, 0.006f, 0.15f), Vector3.Forward, "north of the rug, facing north");
        await Stand(_companion, new Vector3(0.0f, 0.006f, 1.35f), Vector3.Forward, "behind the player by the south wall, out of their view");
        var caught = _companion.CatchUps;
        var follow = Send(Command(NextId("follow"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "follow" }), Player);
        for (var i = 0; i < 60 && _companion.CatchUps == caught; i++) await Frames(1);
        var status = Query("player.status", new JsonObject(), Companion);
        var notice = status["notices"]?.AsArray().OfType<JsonObject>().FirstOrDefault(n => n["kind"]?.GetValue<string>() == "caught_up");
        Check(Ok(follow) && _companion.CatchUps == caught + 1 && notice?["distance_m"]?.GetValue<double>() > 0 && notice["from_m"] != null && notice["to_m"] != null && notice["at_utc"] != null,
            "following far behind the player and out of their view, the Gubble catches up, and its next result tells its AI: " + status["notices"]?.ToJsonString());
        Check(Query("player.status", new JsonObject(), Companion)["notices"] == null && Query("player.status", new JsonObject(), Player)["notices"] == null,
            "the notice comes once, and only to the Gubble");
        Send(Command(NextId("stay"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "stay" }), Player);

        _companion.CatchUpBeyondM = 0.05f;
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(-1.2, 0, 0.9) }), Player)), "the doorstop goes back west of the rug");
        await Stand(_player, new Vector3(0.0f, 0.006f, 1.0f), Vector3.Right, "on the rug's south side, facing east, away from the doorstop");
        await Stand(_companion, new Vector3(-0.5f, 0.006f, 0.9f), Vector3.Left, "facing the doorstop");
        caught = _companion.CatchUps;
        var fetch = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Player);
        var held = await UntilJobEnds(1500, () => _host.HeldBy(CompanionAvatar) == "obj:doorstop");
        var job = JobStatus(fetch, Player);
        Check(Ok(fetch) && held && job?["state"]?.GetValue<string>() == "succeeded" && _companion.CatchUps == caught,
            "a fetch carried back to a player facing away, past even a 5 cm catch-up distance, never catches up: it travels the whole way: " + job?.ToJsonString());
        Check(Ok(Send(Command(NextId("down"), "entity.release", new JsonObject { ["actor"] = CompanionAvatar }), Player)), "and puts the doorstop down");
        _companion.CatchUpBeyondM = GarageGame.Native.CompanionAvatar.CatchUpM;
        previous?.MakeCurrent();
    }

    /// <summary>A goal whose body has reported blocked for about 5 s fails with target_unreachable, and the body stops trying.</summary>
    private async Task TestUnreachable()
    {
        await Stand(_companion, new Vector3(-0.9f, 0, 0.3f), Vector3.Forward, "where the pen will be");
        var pen = new StaticBody3D { Name = "Pen", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = new Vector3(-0.9f, 0.1f, 0.3f) };
        foreach (var (offset, size) in new[] { (new Vector3(0, 0, -0.05f), new Vector3(0.104f, 0.2f, 0.004f)), (new Vector3(0, 0, 0.05f), new Vector3(0.104f, 0.2f, 0.004f)),
                                               (new Vector3(-0.05f, 0, 0), new Vector3(0.004f, 0.2f, 0.104f)), (new Vector3(0.05f, 0, 0), new Vector3(0.004f, 0.2f, 0.104f)) })
            pen.AddChild(new CollisionShape3D { Position = offset, Shape = new BoxShape3D { Size = size } });
        // A lid: the Gubble floats over open walls, so only a closed pen is one it really cannot leave.
        pen.AddChild(new CollisionShape3D { Position = new Vector3(0, 0.102f, 0), Shape = new BoxShape3D { Size = new Vector3(0.104f, 0.004f, 0.104f) } });
        AddChild(pen);
        await Frames(3);
        var fetch = Send(Command(NextId("fetch"), "goal.set", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "fetch", ["target"] = "obj:doorstop" }), Player);
        Check(Ok(fetch), "the player sends the penned companion to fetch the doorstop: " + Code(fetch));
        var frames = 0;
        while (_host.RunningGoal(CompanionAvatar) != null && frames < 900) { await Frames(1); frames++; }
        var job = JobStatus(fetch, Player);
        Check(job?["state"]?.GetValue<string>() == "failed" && job["result"]?["error"]?["code"]?.GetValue<string>() == "target_unreachable" &&
            job["result"]!["error"]!["field_path"]!.GetValue<string>() == "$.args.target" && !job["result"]!["error"]!["retryable"]!.GetValue<bool>(),
            $"after {frames / 60.0:0.0} s blocked the fetch fails with target_unreachable: " + job?.ToJsonString());
        Check(frames >= (int)(CommandHost.UnreachableAfterS * 60) - 5 && frames <= (int)(CommandHost.UnreachableAfterS * 60) + 150 && _companion.CurrentIntent == "stay" && _host.HeldBy(CompanionAvatar) == null,
            "about five seconds, and then the body stops trying");
        // Review major 5: a walk to a place, or a come with no target, is watched the same way, though it has no job to report.
        foreach (var (label, args) in new (string, JsonObject)[]
        {
            ("a go_to to a place", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "go_to", ["position_m"] = new JsonArray(-0.5, 0, 0.3) }),
            ("a come with no target", new JsonObject { ["actor"] = CompanionAvatar, ["goal"] = "come" }),
        })
        {
            var walk = Send(Command(NextId("walk"), "goal.set", args), Player);
            await Frames(10);
            var walking = _companion.CurrentIntent;
            var waited = 10;
            while (_companion.CurrentIntent == walking && waited < 600) { await Frames(1); waited++; }
            Check(Ok(walk) && walk["job_id"] == null && walking != "stay" && _companion.CurrentIntent == "stay" && waited >= 290 && waited <= 460,
                $"{label} from the pen stops trying after about five seconds blocked ({waited / 60.0:0.0} s, {walking} then {_companion.CurrentIntent})");
        }
        pen.QueueFree();
        await Frames(2);
    }

    /// <summary>Wait until the companion's running goal ends (or the frames run out); true if the condition held at some point.</summary>
    private async Task<bool> UntilJobEnds(int maxFrames, Func<bool> sawAlong)
    {
        var saw = false;
        for (var i = 0; i < maxFrames && _host.RunningGoal(CompanionAvatar) != null; i++)
        {
            await Frames(1);
            saw |= sawAlong();
        }
        return saw;
    }

    private JsonObject? JobStatus(JsonObject goalResult, string principal)
    {
        var id = goalResult["job_id"]?.GetValue<string>();
        return id == null ? null : Query("jobs.status", new JsonObject { ["job_id"] = id }, principal)["data"]?.AsObject();
    }

    private bool Supported(JsonObject entity)
    {
        var box = SandboxControls.Box(entity);
        var centre = box.GetCenter();
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(centre.X, box.Position.Y + 0.002f, centre.Z),
            new Vector3(centre.X, box.Position.Y - 0.003f, centre.Z), RoomBuilder.WorldLayer, new Godot.Collections.Array<Rid> { ((CollisionObject3D)Node(entity["id"]!.GetValue<string>())!).GetRid() }));
        return hit.Count > 0;
    }

    private static void Shift(JsonNode vector, int axis, double by) => vector[axis] = vector[axis]!.GetValue<double>() + by;

    private static JsonObject Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        new() { ["min_m"] = new JsonArray(x0, y0, z0), ["max_m"] = new JsonArray(x1, y1, z1) };

    // ---- helpers ----

    private void NewHost()
    {
        _host = CommandHost.Create(this, _room, _player, _companion, null, _savePath);
        _host.Clock = () => _now;
    }

    private async Task Stand(SmallPlayerController body, Vector3 at, Vector3 facing, string where)
    {
        body.SetControlInput(Vector2.Zero);
        Check(body.TryTeleportTo(at), $"{body.Name} stands {where}");
        await Face(body, facing);
    }

    private async Task Face(SmallPlayerController body, Vector3 facing)
    {
        body.Rotation = new Vector3(0, Mathf.Atan2(-facing.X, -facing.Z), 0);
        await Frames(4);
    }

    /// <summary>Walk a body to a spot on the floor plane, steering each tick: running while far, slowing near it.</summary>
    private async Task<bool> WalkTo(SmallPlayerController body, Vector2 target, int maxFrames = 900)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            var to = target - new Vector2(body.GlobalPosition.X, body.GlobalPosition.Z);
            if (to.Length() < 0.008f)
            {
                body.SetControlInput(Vector2.Zero);
                await Frames(20);
                return true;
            }
            body.Rotation = new Vector3(0, Mathf.Atan2(-to.X, -to.Y), 0);
            body.SetControlInput(new Vector2(0, to.Length() > 0.1f ? 1 : 0.35f), sprint: to.Length() > 0.3f);
            await Frames(1);
        }
        body.SetControlInput(Vector2.Zero);
        return false;
    }

    private Node3D? Node(string id) => GetNode("Room/Objects").GetChildren().OfType<Node3D>().FirstOrDefault(n => n.GetMeta("entity_id").AsString() == id);

    private (float Height, string Entity) Ray(Vector3 from, Vector3 to)
    {
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, RoomBuilder.WorldLayer));
        return hit.Count == 0 ? (float.NaN, "") : (hit["position"].AsVector3().Y, SandboxPhysics.EntityOf(hit["collider"].AsGodotObject()));
    }

    private JsonObject Inspect(string id, string principal) => Query("entity.inspect", new JsonObject { ["target"] = id }, principal)["data"]?["entity"]?.AsObject() ?? new JsonObject();

    private string NextId(string prefix) => $"{prefix}-{++_ids}";

    private JsonObject Command(string actionId, string op, JsonObject args, int? expectedRevision = null, bool preview = false)
    {
        var command = new JsonObject { ["schema"] = "enfractal.command", ["version"] = 1, ["action_id"] = actionId, ["room_id"] = "test_room", ["op"] = op, ["args"] = args };
        if (expectedRevision != null) command["expected_revision"] = expectedRevision;
        if (preview) command["preview"] = true;
        return command;
    }

    private JsonObject Query(string op, JsonObject args, string principal) =>
        Send(new JsonObject { ["schema"] = "enfractal.query", ["version"] = 1, ["query_id"] = "q-" + (++_dumped), ["room_id"] = "test_room", ["op"] = op, ["args"] = args }, principal);

    private JsonObject Send(JsonObject message, string principal, bool contractValid = true)
    {
        _now += TimeSpan.FromMilliseconds(50);
        var result = JsonNode.Parse(_host.Handle(message.ToJsonString(), principal))!.AsObject();
        if (_dump != null)
        {
            var index = ++_dumped;
            if (contractValid)
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, $"{index:0000}_message.json"), CanonicalJson.Bytes(message));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, $"{index:0000}_result.json"), CanonicalJson.Bytes(result));
        }
        return result;
    }

    private static JsonObject Source(string name) => new()
    {
        ["schema"] = "enfractal.creation", ["version"] = 1, ["name"] = name, ["seed"] = 1, ["mount"] = "ground",
        ["parts"] = new JsonArray(new JsonObject
        {
            ["id"] = "base", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.05, 0), ["rotation_deg"] = new JsonArray(0, 0, 0),
            ["size_m"] = new JsonArray(0.1, 0.1, 0.1), ["material"] = "wood",
        }),
        ["nodes"] = new JsonArray(new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "base", ["params"] = new JsonObject() }),
        ["edges"] = new JsonArray(),
    };

    private static JsonObject Placement(double x, double y, double z, string? on = null)
    {
        var placement = new JsonObject { ["position_m"] = new JsonArray(x, y, z) };
        if (on != null) placement["on"] = on;
        return placement;
    }

    private static string Affected(JsonObject result) => result["affected"]?.AsArray().Count == 1 ? result["affected"]![0]!.GetValue<string>() : "";

    private static bool Ok(JsonObject result) => result["ok"]!.GetValue<bool>();
    private static string? Code(JsonObject result) => result["error"]?["code"]?.GetValue<string>();
    private static string Canonical(JsonNode? node) => node == null ? "" : CanonicalJson.Text(node);
    private static Vector3 Vec(JsonNode node) => new((float)node[0]!.GetValue<double>(), (float)node[1]!.GetValue<double>(), (float)node[2]!.GetValue<double>());

    private static bool Near(JsonNode vector, double x, double y, double z, double tolerance) =>
        Math.Abs(vector[0]!.GetValue<double>() - x) <= tolerance && Math.Abs(vector[1]!.GetValue<double>() - y) <= tolerance && Math.Abs(vector[2]!.GetValue<double>() - z) <= tolerance;

    private void RemoveSave()
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
        GD.PushError("Sandbox: " + label);
    }
}
