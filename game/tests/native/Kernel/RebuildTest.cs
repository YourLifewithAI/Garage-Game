using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;
using RoomStateOutcome = GarageGame.Native.Kernel.CommandHost.RoomStateOutcome;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// P6, the world as data (Run 2): play changes the test room (objects moved, creations built, locks, a note, a fetch's
/// task, discovered space, and the island's nature: the trail turned off, the table burned down, creations scorched in
/// Challenge), the host writes room state, the whole room is freed, and a room built from nothing (the manifest, the
/// avatars at their spawns, an empty save) is rebuilt from that state. The rebuilt room writes the same room state,
/// journal, discovered map and nature included, its scene stands where the state says, its receipts replay, and its own
/// save loads again the same. Then the save migration between room manifests: a re-exported room (the box moved) brings
/// over what still fits, its scorch marks and the burned table included, leaves the rest in the old file untouched, and
/// keeps old action ids from conflicting reuse.
/// Pass "-- --dump=DIR" to write the room states for contracts/validate.py --state.
/// </summary>
public partial class RebuildTest : Node3D
{
    private const string TestRoot = "user://tests/rebuild";
    private const string Player = CommandHost.PlayerPrincipal;
    private const string Companion = CommandHost.CompanionPrincipal;
    private int _checks;
    private int _failures;
    private int _ids;
    private string? _dump;
    private RoomData _room = null!;
    private Node3D _world = null!;
    private CommandHost _host = null!;
    private SmallPlayerController _player = null!;
    private CompanionAvatar _companion = null!;
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    /// <summary>The last fire's answer (Burn), for the checks' messages.</summary>
    private JsonObject _lastFire = new();

    public override async void _Ready()
    {
        try
        {
            _dump = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];
            if (_dump != null) System.IO.Directory.CreateDirectory(_dump);
            RemoveAll();
            CommandHost.SaveRoot = TestRoot + "/saves";
            _room = RoomData.Load(RoomWorld.DefaultRoom);
            await TestRebuildFromNothing();
            await TestMigrationBetweenManifests();
            GD.Print($"NATIVE_KERNEL_REBUILD: {_checks - _failures}/{_checks} checks passed; room state written, the room freed and rebuilt from nothing (shell, objects at their poses, creations, locks, avatars, journal, discovered map and nature), and saves migrated between room manifests (scorch marks included)");
            RemoveAll();
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError("Rebuild test exception: " + error);
            GetTree().Quit(1);
        }
    }

    // ---- save, free, rebuild from nothing, compare ----

    private async Task TestRebuildFromNothing()
    {
        await NewWorld(_room, $"{TestRoot}/a/inventions.json", sweep: true);
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = Placement(0.99, 0.3, 0.2, "obj:box") }), Player)), "the doorstop goes on the box");
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:book", ["placement"] = Placement(0.45, 0, -0.5) }), Player)), "the book goes north");
        var lamp = Command("lamp", "creation.place", new JsonObject { ["source"] = Source("Lamp"), ["placement"] = Turned(-1.5, 0, 0.9) });
        var lampId = Send(lamp, Player)["created"]?[0]?.GetValue<string>() ?? "";
        var arch = Send(Command(NextId("arch"), "creation.place", new JsonObject { ["source"] = Source("Arch"), ["placement"] = Placement(-1.5, 0, 0.5) }), Companion);
        Check(lampId.Length > 0 && Ok(arch), "the player builds a lamp turned a quarter, the companion an arch");
        // Review minor 9: both half-turns, +180 and -180, are one angle.
        var east = Send(Command(NextId("half"), "creation.place", new JsonObject { ["source"] = Source("Half east"), ["placement"] = HalfTurn(-1.0, 0, 0.9, 1) }), Player);
        var west = Send(Command(NextId("half"), "creation.place", new JsonObject { ["source"] = Source("Half west"), ["placement"] = HalfTurn(-1.0, 0, 0.5, -1) }), Player);
        Check(Ok(east) && Ok(west), "two creations turned half round, one each way");
        Check(Ok(Send(Command(NextId("lock"), "protect.lock", new JsonObject { ["targets"] = new JsonArray(lampId, "obj:rug") }, expectedRevision: _host.Revision), Player)), "the player protects the lamp and the rug");
        Check(Ok(Send(Command(NextId("note"), "journal.note", new JsonObject { ["text"] = "The lamp lights the reading corner." }), Companion)), "the companion writes a note");
        // The island's nature (the review of 13 October: RebuildTest never configured it): the table burned down,
        // and in Challenge a fire the player approves scorches the Gubble's arch and a half-turned creation.
        NatureOn(challenge: true);
        var archId = arch["created"]?[0]?.GetValue<string>() ?? "";
        var westId = west["created"]?[0]?.GetValue<string>() ?? "";
        Check(Burn(new Vector3(-0.5f, 0.01f, -0.4f), 0.3, 8, 20) && _host.Nature.Objects.TryGetValue("obj:table", out var ash) && ash.BurnedDown, "a fire burns the table down");
        Check(Burn(new Vector3(-1.3f, 0.01f, 0.35f), 0.3, 8, 16) && _host.IsNatureUnavailable(archId) && _host.IsNatureUnavailable(westId),
            "a fire the player approves scorches the arch and a half-turned creation: " + Canonical(_lastFire["error"]) + " " + string.Join(",", _host.Nature.Objects.Keys));
        // Review major 5: identities that are not the defaults.
        _companion.SetDisplayName("Pip");
        _companion.SetAppearance(new Color("3a7bd5"));
        _player.SetAppearance(new Color("c04040"));
        // The characters round: the drawn character the player wears is part of its identity (appearance.body).
        _player.SetCharacterId("stickbear");
        await Frames(40);
        _host.TeamSightIntervalS = 0;
        var before = _host.ExportRoomState()!;
        var scene = SceneOf();
        var instances = CreationIds();
        Dump("room_state_before.json", before);
        Check(before["entities"]![lampId]?["protection"]?["locked"]?.GetValue<bool>() == true && before["entities"]!["obj:doorstop"]?["transform"] != null &&
            before["entities"]!["avatar:companion"]?["avatar"]?["display_name"]?.GetValue<string>() == "Pip" && before["journal"]!["notes"]!.AsArray().Count == 1 &&
            before["journal"]!["history"]!.AsArray().Count >= 2 && before["discovered"]!["levels"]!.AsArray().Count > 0 && before["discovered"]!["entities"]!.AsObject().Count > 0,
            "room state holds the moved objects, the creations and their locks, the avatars, the journal and the discovered map");
        Check(before["entities"]!["avatar:player"]?["avatar"]?["appearance"]?["body"]?.GetValue<string>() == "stickbear" &&
            before["entities"]!["avatar:companion"]?["avatar"]?["appearance"]?.AsObject().ContainsKey("body") == false,
            "room state names the drawn character the player wears (appearance.body), and none for the Gubble");
        Check(before["extensions"]?["x_play_nature"] is JsonObject && before["entities"]!["obj:table"]?["removed"]?.GetValue<bool>() == true,
            "and the island's nature (x_play_nature), with the burned table removed at the pose it burned at");

        // Free everything: the room, the avatars and the host.
        _world.QueueFree();
        await Frames(3);
        // Review major 2: a rebuild whose save cannot be written changes nothing at all.
        await NewWorld(_room, $"{TestRoot}/b/inventions.json", sweep: false);
        _host.Authority.Set("persistence_sink", Callable.From((Godot.Collections.Dictionary _) => new Godot.Collections.Dictionary { ["ok"] = false, ["message"] = "the disk is full" }));
        var unsaved = _host.ImportRoomState(before);
        _host.Authority.Set("persistence_sink", new Callable());
        Check(!unsaved.Ok && _host.Authority.Call("is_ready").AsBool() && _host.Revision == 0 && CreationIds().Count == 0 && Summaries().Values.All(e => e["kind"]!.GetValue<string>() != "creation") &&
            _host.ExportJournal()["notes"]!.AsArray().Count == 0 && _host.ExportDiscovered()["levels"]!.AsArray().Count == 0 &&
            Node(_world, "obj:doorstop")!.GlobalPosition.DistanceTo(new Vector3(-0.3f, 0.006f, 0.5f)) < 1e-4f && _companion.CompanionName != "Pip",
            "a rebuild whose save fails leaves the room as it was: authority, journal, map, scene and avatars (review major 2): " + unsaved.Message);
        Check(_host.Revision == 0 && CreationIds().Count == 0, "a room built from nothing: no play, no creations, an empty journal");
        var rebuilt = _host.ImportRoomState(before);
        var after = _host.ExportRoomState()!;
        Dump("room_state_after.json", after);
        Check(rebuilt.Ok && Canonical(Unsessioned(after)) == Canonical(Unsessioned(before)),
            "rebuilt from room state, the room writes the same room state: entities (both half-turns, the avatars), receipts, journal, discovered map and extensions: " + rebuilt.Message);
        Check(Canonical(after["journal"]) == Canonical(before["journal"]) && Canonical(after["discovered"]) == Canonical(before["discovered"]), "the journal and the discovered map came through whole");
        Check(_companion.CompanionName == "Pip" && _companion.AppearanceColor.ToHtml(false) == "3a7bd5" && _player.AppearanceColor.ToHtml(false) == "c04040",
            "the avatars' identities are restored: the companion's name and both colours (review major 5)");
        Check(_player.CharacterId == "stickbear", "and the drawn character the player wore");
        Check(!Node(_world, "obj:table")!.Visible && _host.IsNatureUnavailable(archId) && _host.IsNatureUnavailable(westId) && !Summaries().ContainsKey("obj:table"),
            "the island's nature is restored: the table is gone from the scene and the room, the scorched creations still need repair");
        var sceneAfter = SceneOf();
        Check(scene.Count == sceneAfter.Count && scene.All(p => sceneAfter.TryGetValue(p.Key, out var t) && t.Origin.DistanceTo(p.Value.Origin) < 1e-4f && t.Basis.IsEqualApprox(p.Value.Basis)),
            "every object's node stands where it stood: the scene is derived from the state");
        Check(CreationIds().SequenceEqual(instances) && instances.Count == 4, "and the creations are back in the scene: " + string.Join(", ", CreationIds()));
        var entities = Summaries();
        Check(entities[lampId]["protected"]!.GetValue<bool>() && entities["obj:rug"]["protected"]!.GetValue<bool>() && !entities["obj:book"]["protected"]!.GetValue<bool>(), "the locks hold");
        var replay = Send(lamp, Player);
        Check(replay["replayed"]?.GetValue<bool>() == true && replay["created"]?[0]?.GetValue<string>() == lampId, "an earlier action id replays its receipt in the rebuilt room");
        Check(Code(Send(Command("lamp", "creation.place", new JsonObject { ["source"] = Source("Other"), ["placement"] = Placement(-1.0, 0, 0.9) }), Player)) == "action_id_conflict",
            "and refuses other content under it");
        Check(!_host.ImportRoomState(before).Ok, "a room that has play is never overwritten by an import");

        // The rebuilt room's own save loads again, the same.
        _world.QueueFree();
        await Frames(3);
        await NewWorld(_room, $"{TestRoot}/b/inventions.json", sweep: false);
        Check(_host.Authority.Call("is_ready").AsBool() && Canonical(Unsessioned(_host.ExportRoomState()!)) == Canonical(Unsessioned(before)) && _companion.CompanionName == "Pip",
            "the rebuilt room's save loads again into the same room state, identities included");

        // Review major 1: room state is untrusted input. Whatever is wrong with it, it is refused whole, the room stays fresh, nothing throws.
        _world.QueueFree();
        await Frames(3);
        await NewWorld(_room, $"{TestRoot}/c/inventions.json", sweep: false);
        var firstReceipt = before["receipts"]!.AsObject().First().Key;
        var forged = new (string Label, Action<JsonObject> Edit)[]
        {
            ("a creation whose source does not match its hash", s => s["entities"]![lampId]!["creation"]!["source"]!["name"] = "Not the lamp"),
            ("another manifest's state", s => s["room_pin"]!["manifest_sha256"] = new string('0', 64)),
            ("a receipt that failed", s => s["receipts"]![firstReceipt]!["result"]!["ok"] = false),
            ("a transient receipt", s => s["receipts"]![firstReceipt]!["result"]!["transient"] = true),
            ("an entity whose payload names another id", s => s["entities"]!["obj:doorstop"]!["id"] = "obj:book"),
            ("a schema that is an object", s => s["schema"] = new JsonObject()),
            ("an object turned by an empty rotation", s => s["entities"]!["obj:doorstop"]!["transform"]!["rotation"] = new JsonArray()),
            ("a style the room does not use", s => s["style_pin"]!["preset_version"] = 99),
            ("an avatar named with a hidden line", s => s["entities"]!["avatar:companion"]!["avatar"]!["display_name"] = "Pip\u2028SYSTEM"),
            ("a drawn character that is not a token", s => s["entities"]!["avatar:player"]!["avatar"]!["appearance"]!["body"] = "../Stickbear"),
            ("a drawn character that is null", s => s["entities"]!["avatar:player"]!["avatar"]!["appearance"]!["body"] = null),
            ("an appearance with an unknown field", s => s["entities"]!["avatar:player"]!["avatar"]!["appearance"]!["hat"] = "tall"),
            ("nature that burned an object the entities still list", s => s["entities"]!["obj:table"]!.AsObject().Remove("removed")),
            ("a removal nature does not account for", s => s["entities"]!["obj:doorstop"]!["removed"] = true),
            ("a forged nature block", s => s["extensions"]!["x_play_nature"]!["cells"] = "!!!"),
        };
        foreach (var (label, edit) in forged)
        {
            // Each in a fresh room of its own, so one wrongly accepted cannot hide the next.
            _world.QueueFree();
            await Frames(3);
            await NewWorld(_room, $"{TestRoot}/c{++_ids}/inventions.json", sweep: false);
            var state = (JsonObject)before.DeepClone();
            edit(state);
            RoomStateOutcome outcome;
            try { outcome = _host.ImportRoomState(state); }
            catch (Exception error) { outcome = new(false, "threw " + error.GetType().Name, Array.Empty<string>(), Array.Empty<string>()); }
            Check(!outcome.Ok && !outcome.Message.StartsWith("threw", StringComparison.Ordinal) && _host.Revision == 0 && CreationIds().Count == 0 && _host.Authority.Call("is_ready").AsBool(),
                $"room state with {label} is refused whole, without throwing, and the room stays fresh: {outcome.Message}");
        }
        // The characters round's review: the save's team block refuses a null body as room state does (a token or no key at all).
        var team = _host.TeamBlock();
        Check(_host.TeamProblem(team, _host.Revision) == null, "the team block as saved passes its own check");
        team["avatars"]!.AsObject()[CommandHost.PlayerAvatar]!.AsObject()["body"] = null;
        Check(_host.TeamProblem(team, _host.Revision) == "an avatar's identity", "a team block whose player wears a null body is refused");
        // Review major 4: transient play is play too: a running goal or something held keeps an import out.
        Check(Ok(Send(Command(NextId("follow"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "follow" }), Player)), "the companion follows the player");
        var goal = _host.ImportRoomState(before);
        Send(Command(NextId("stop"), "goal.stop", new JsonObject()), Player);
        Check(!goal.Ok && _host.Revision == 0 && CreationIds().Count == 0, "a room with a running goal is not rebuilt over: " + goal.Message);
        _world.QueueFree();
        await Frames(3);
        await NewWorld(_room, $"{TestRoot}/d/inventions.json", sweep: false);
        Check(_player.TryTeleportTo(new Vector3(-0.3f, 0.006f, 0.62f)), "the player stands by the doorstop");
        await Frames(4);
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player)), "and picks it up");
        var held = _host.ImportRoomState(before);
        Check(!held.Ok && _host.Revision == 0 && _host.HeldBy(CommandHost.PlayerAvatar) == "obj:doorstop", "a room where something is held is not rebuilt over: " + held.Message);
        _world.QueueFree();
        await Frames(3);
    }

    // ---- saves of a re-exported room ----

    private async Task TestMigrationBetweenManifests()
    {
        // The test room again, played in: a block on the floor, a lamp on the box, a cube where the box will stand, locks.
        await NewWorld(_room, CommandHost.SavePathFor(_room), sweep: false);
        var block = Send(Command("block", "creation.place", new JsonObject { ["source"] = Source("Block"), ["placement"] = Placement(-1.5, 0, 0.9) }), Player)["created"]?[0]?.GetValue<string>() ?? "";
        var onBox = Send(Command("on-box", "creation.place", new JsonObject { ["source"] = Source("Box lamp"), ["placement"] = Placement(1.1, 0.3, 0.2, "obj:box") }), Player)["created"]?[0]?.GetValue<string>() ?? "";
        var cube = Send(Command("cube", "creation.place", new JsonObject { ["source"] = Source("Cube"), ["placement"] = Placement(1.1, 0, -0.6) }), Player)["created"]?[0]?.GetValue<string>() ?? "";
        Check(block.Length > 0 && onBox.Length > 0 && cube.Length > 0, "a block on the floor, a lamp on the box, a cube north of the box");
        Check(Ok(Send(Command("lock-all", "protect.lock", new JsonObject { ["targets"] = new JsonArray(block, onBox, "obj:box") }, expectedRevision: _host.Revision), Player)), "the player protects the block, the lamp and the box");
        Check(Ok(Send(Command(NextId("note"), "journal.note", new JsonObject { ["text"] = "Building by the door." }), Companion)), "the companion writes a note");
        // Nature too: in Challenge a shed is scorched and the table burns down; both marks must come over (review blocker 1).
        NatureOn(challenge: true);
        var shed = Send(Command("shed", "creation.place", new JsonObject { ["source"] = Source("Shed"), ["placement"] = Placement(-1.0, 0, 0.5) }), Player)["created"]?[0]?.GetValue<string>() ?? "";
        Check(Burn(new Vector3(-0.85f, 0.01f, 0.5f), 0.2, 3, 8) && _host.IsNatureUnavailable(shed), "a fire the player approves scorches a shed");
        Check(Burn(new Vector3(-0.5f, 0.01f, -0.4f), 0.3, 8, 20) && _host.Nature.Objects.TryGetValue("obj:table", out var table) && table.BurnedDown, "and another burns the table down");
        var revision = _host.Revision;
        var blockRevision = Summaries()[block]["revision"]!.GetValue<int>();
        var oldPath = CommandHost.SavePathFor(_room);
        var oldAbsolute = ProjectSettings.GlobalizePath(oldPath);
        var oldBytes = System.IO.File.ReadAllBytes(oldAbsolute);
        _world.QueueFree();
        await Frames(3);

        // The room re-exported with the box moved north, onto the cube's spot: a new manifest, so a new save folder.
        var moved = ReExport(new Vector3(1.1f, 0, 0.2f), new Vector3(1.1f, 0, -0.6f));
        Check(moved.ManifestSha256 != _room.ManifestSha256, "the re-exported room has a new manifest");
        // Review major 6: in real play the sweep runs and wants to save; the offer stays until the player answers.
        await NewWorld(moved, CommandHost.SavePathFor(moved), sweep: true);
        _host.TeamSaveIntervalS = 0.5;
        await Frames(90);
        Check(_host.Revision == 0 && CreationIds().Count == 0 && _host.SaveNotice.Length > 0, "nothing loads by itself; the player is told about the earlier save");
        var candidate = _host.MigrationCandidate();
        Check(candidate == oldPath && !System.IO.File.Exists(ProjectSettings.GlobalizePath(CommandHost.SavePathFor(moved))),
            "with the sweep running for 1.5 s, the earlier save is still offered and nothing was saved over the offer: " + candidate);
        Check(!_host.MigrateFrom($"{TestRoot}/elsewhere/inventions.json").Ok, "only the offered save can be brought over");

        // Review major 1: the earlier save is untrusted input too. Each forgery is refused whole and the file is left as it was.
        var valid = JsonNode.Parse(System.IO.File.ReadAllText(oldAbsolute))!.AsObject();
        var receiptKey = valid["receipts"]!.AsObject().First().Key;
        var playerKey = valid["receipts"]!.AsObject().First(p => p.Key.StartsWith("player:", StringComparison.Ordinal)).Key;
        var forgeries = new (string Label, Func<byte[]> Bytes)[]
        {
            ("a receipt that failed", () => Edited(valid, v => v["receipts"]![receiptKey]!["receipt"]!["ok"] = false)),
            ("another compiler", () => Edited(valid, v => v["compiler_version"] = 2)),
            ("a receipt under someone else's key", () => Edited(valid, v => v["receipts"]![playerKey]!["principal"] = "companion:local")),
            ("more than 4 MiB", () => CanonicalJson.Bytes(valid).Concat(Enumerable.Repeat((byte)' ', 4 * 1024 * 1024)).ToArray()),
            ("no revision", () => Edited(valid, v => v.Remove("revision"))),
            ("a creation placed at two coordinates", () => Edited(valid, v => v["instances"]![0]!["position_m"] = new JsonArray(1.0, 2.0))),
        };
        foreach (var (label, bytes) in forgeries)
        {
            // Each in a fresh room of its own (a wrongly accepted one would leave play behind).
            _world.QueueFree();
            await Frames(3);
            var own = ProjectSettings.GlobalizePath(CommandHost.SavePathFor(moved));
            if (System.IO.File.Exists(own)) System.IO.File.Delete(own);
            await NewWorld(moved, CommandHost.SavePathFor(moved), sweep: false);
            var forged = bytes();
            System.IO.File.WriteAllBytes(oldAbsolute, forged);
            RoomStateOutcome outcome;
            try { outcome = _host.MigrateFrom(oldPath); }
            catch (Exception error) { outcome = new(false, "threw " + error.GetType().Name, Array.Empty<string>(), Array.Empty<string>()); }
            Check(!outcome.Ok && !outcome.Message.StartsWith("threw", StringComparison.Ordinal) && _host.Revision == 0 && CreationIds().Count == 0 &&
                System.IO.File.ReadAllBytes(oldAbsolute).SequenceEqual(forged), $"an earlier save with {label} is refused whole, without throwing: {outcome.Message}");
        }
        System.IO.File.WriteAllBytes(oldAbsolute, oldBytes);
        System.IO.File.SetLastWriteTimeUtc(oldAbsolute, DateTime.UtcNow);
        _world.QueueFree();
        await Frames(3);
        var ownSave = ProjectSettings.GlobalizePath(CommandHost.SavePathFor(moved));
        if (System.IO.File.Exists(ownSave)) System.IO.File.Delete(ownSave);
        await NewWorld(moved, CommandHost.SavePathFor(moved), sweep: true);
        await Frames(30);

        var migrated = _host.MigrateFrom(oldPath);
        Check(migrated.Ok && migrated.Kept.SequenceEqual(new[] { block, shed }) && migrated.LeftBehind.SequenceEqual(new[] { onBox, cube }),
            "the block and the shed come over; the lamp, whose box moved away, and the cube, which the box now stands on, stay behind (review major 3): " + migrated.Message);
        Check(_host.IsNatureUnavailable(shed) && _host.Nature.Objects.TryGetValue("obj:table", out var burned) && burned.BurnedDown && !Node(_world, "obj:table")!.Visible,
            "the scorched shed and the burned table come over too (review blocker 1)");
        var entities = Summaries();
        Check(entities.ContainsKey(block) && !entities.ContainsKey(onBox) && !entities.ContainsKey(cube) && entities[block]["revision"]!.GetValue<int>() == blockRevision && blockRevision == 2 && _host.Revision == revision,
            "the block keeps its id and revision, and the room its revision");
        Check(entities[block]["protected"]!.GetValue<bool>() && entities["obj:box"]["protected"]!.GetValue<bool>(), "locks on what still exists carry over (the block, the box)");
        Check(Node(_world, "obj:box")!.GlobalPosition.DistanceTo(new Vector3(1.1f, 0, -0.6f)) < 1e-4f, "objects stand where the new manifest puts them");
        Check(_host.ExportJournal()["notes"]!.AsArray().Count == 1 && _host.ExportDiscovered()["levels"]!.AsArray().Count > 0,
            "the journal comes over, and what the avatars discovered of this room while the offer stood is kept");
        var replay = Send(Command("block", "creation.place", new JsonObject { ["source"] = Source("Block"), ["placement"] = Placement(-1.5, 0, 0.9) }), Player);
        Check(replay["replayed"]?.GetValue<bool>() == true && Code(Send(Command("block", "creation.place", new JsonObject { ["source"] = Source("Other"), ["placement"] = Placement(-1.0, 0, 0.9) }), Player)) == "action_id_conflict",
            "the ledger starts compacted: an old action id replays and refuses conflicting reuse");
        Check(System.IO.File.ReadAllBytes(oldAbsolute).SequenceEqual(oldBytes), "the earlier save is never modified");
        Check(_host.MigrationCandidate() == null && !_host.MigrateFrom(oldPath).Ok, "once the room has its own save, nothing is offered again");
        Dump("room_state_migrated.json", _host.ExportRoomState()!);
        _world.QueueFree();
        await Frames(3);
        await NewWorld(moved, CommandHost.SavePathFor(moved), sweep: false);
        Check(_host.Authority.Call("is_ready").AsBool() && Summaries().ContainsKey(block), "the migrated room's own save loads");
        _world.QueueFree();
        await Frames(3);
    }

    private static byte[] Edited(JsonObject document, Action<JsonObject> edit)
    {
        var copy = (JsonObject)document.DeepClone();
        edit(copy);
        return CanonicalJson.Bytes(copy);
    }

    private static JsonObject HalfTurn(double x, double y, double z, int sign) =>
        new() { ["position_m"] = new JsonArray(x, y, z), ["rotation"] = new JsonArray(0, sign, 0, 0) };

    /// <summary>The test room copied to a user folder with one object's place changed: a re-exported room, a new manifest hash.</summary>
    private RoomData ReExport(Vector3 from, Vector3 to)
    {
        var source = ProjectSettings.GlobalizePath(RoomWorld.DefaultRoom);
        var target = ProjectSettings.GlobalizePath($"{TestRoot}/rooms/test_room");
        foreach (var file in System.IO.Directory.GetFiles(source, "*", System.IO.SearchOption.AllDirectories).Where(f => !f.EndsWith(".import", StringComparison.Ordinal) && !f.EndsWith(".uid", StringComparison.Ordinal)))
        {
            var destination = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            System.IO.File.Copy(file, destination, true);
        }
        var manifest = System.IO.Path.Combine(target, "room.json");
        var text = System.IO.File.ReadAllText(manifest);
        var old = $"\"id\": \"obj:box\",\n      \"asset\": \"objects/box_proxy/asset.json\",\n      \"transform\": {{\n        \"position_m\": [\n          {from.X:0.0###},\n          0.0,\n          {from.Z:0.0###}";
        var replacement = $"\"id\": \"obj:box\",\n      \"asset\": \"objects/box_proxy/asset.json\",\n      \"transform\": {{\n        \"position_m\": [\n          {to.X:0.0###},\n          0.0,\n          {to.Z:0.0###}";
        Check(text.Contains(old, StringComparison.Ordinal), "the room manifest has the box where the test expects it");
        System.IO.File.WriteAllBytes(manifest, System.Text.Encoding.UTF8.GetBytes(text.Replace(old, replacement)));
        return RoomData.Load($"{TestRoot}/rooms/test_room");
    }

    // ---- helpers ----

    private async Task NewWorld(RoomData room, string savePath, bool sweep)
    {
        _world = new Node3D { Name = "World" };
        AddChild(_world);
        _world.AddChild(RoomBuilder.Build(room));
        await Frames(2);
        _player = new SmallPlayerController { Name = "Player", ReadKeyboard = false, Position = room.SpawnFor("player").PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_player);
        _companion = new CompanionAvatar { Name = "Companion", Position = room.SpawnFor("companion", room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f };
        _world.AddChild(_companion);
        _companion.BindPlayer(_player);
        _host = CommandHost.Create(_world, room, _player, _companion, null, savePath);
        _host.Clock = () => _now;
        _host.TeamSightIntervalS = sweep ? 0.25 : 0;
        await Frames(4);
    }

    private Dictionary<string, Transform3D> SceneOf() =>
        _world.GetNode("Room/Objects").GetChildren().OfType<Node3D>().ToDictionary(n => n.GetMeta("entity_id").AsString(), n => n.GlobalTransform);

    private static Node3D? Node(Node world, string id) => world.GetNode("Room/Objects").GetChildren().OfType<Node3D>().FirstOrDefault(n => n.GetMeta("entity_id").AsString() == id);

    private List<string> CreationIds() => _host.Runtime.Get("instances").AsGodotDictionary().Keys.Select(k => k.AsString()).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private Dictionary<string, JsonObject> Summaries() => _host.Entities().ToDictionary(e => e["id"]!.GetValue<string>(), e => e);

    private static JsonObject Unsessioned(JsonObject state)
    {
        var copy = (JsonObject)state.DeepClone();
        copy.Remove("session");
        return copy;
    }

    private void Dump(string name, JsonObject document)
    {
        if (_dump != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, name), CanonicalJson.Bytes(document));
    }

    private string NextId(string prefix) => $"{prefix}-{++_ids}";

    private JsonObject Command(string actionId, string op, JsonObject args, int? expectedRevision = null)
    {
        var command = new JsonObject { ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = actionId, ["room_id"] = "test_room", ["op"] = op, ["args"] = args };
        if (expectedRevision != null) command["expected_revision"] = expectedRevision;
        return command;
    }

    /// <summary>The island's nature on in this room, with the shipped rules; in Challenge fires scorch creations (after the player's yes).</summary>
    private void NatureOn(bool challenge)
    {
        if (challenge) _host.ConfigurePlayerVitals(new ChallengeVitals());
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        _host.ConfigureNature();
    }

    /// <summary>Loads the Challenge mode.</summary>
    private sealed class ChallengeVitals : IPlayerVitalsStore
    {
        public PlayerModesProfile Load() => PlayerModesProfile.Fresh.Select("challenge");
        public void Save(PlayerModesProfile profile) { }
    }

    /// <summary>A fire the player previews and commits on the room's own ground (approved when it waits for a yes), then steps of it.</summary>
    private bool Burn(Vector3 center, double radius, double duration, int steps)
    {
        var args = new JsonObject { ["capability"] = "fire", ["center_m"] = new JsonArray(center.X, center.Y, center.Z), ["radius_m"] = radius, ["duration_s"] = duration };
        // A pack with connection energy charges each Fire: the player rests first, so the pool is full (nothing else here moves).
        _player.VitalsTick?.Invoke(120f, true);
        // Decision 72: the team knows Fire, as after the first rest (the one helper every suite that lights fires uses).
        _host.LearnFireForTests();
        var preview = Command(NextId("fire-preview"), "nature.cast", (JsonObject)args.DeepClone());
        preview["preview"] = true;
        var previewed = Send(preview, Player);
        if (!Ok(previewed))
        {
            _lastFire = previewed;
            return false;
        }
        args["preview_id"] = previewed["data"]!["preview_id"]!.GetValue<string>();
        var lit = Send(Command(NextId("fire"), "nature.cast", args), Player);
        if (lit["approval_needed"]?["request_id"]?.GetValue<string>() is { } requestId) lit = _host.Approve(requestId);
        for (var i = 0; i < steps; i++) _host.AdvanceNature(0.5);
        _lastFire = lit;
        return Ok(lit);
    }

    private JsonObject Send(JsonObject message, string principal)
    {
        _now += TimeSpan.FromMilliseconds(50);
        return JsonNode.Parse(_host.Handle(message.ToJsonString(), principal))!.AsObject();
    }

    private static JsonObject Source(string name) => new()
    {
        ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = name, ["seed"] = 1, ["mount"] = "ground",
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

    private static JsonObject Turned(double x, double y, double z) =>
        new() { ["position_m"] = new JsonArray(x, y, z), ["rotation"] = new JsonArray(0, Math.Sqrt(0.5), 0, Math.Sqrt(0.5)) };

    private static bool Ok(JsonObject result) => result["ok"]!.GetValue<bool>();
    private static string? Code(JsonObject result) => result["error"]?["code"]?.GetValue<string>();
    private static string Canonical(JsonNode? node) => node == null ? "" : CanonicalJson.Text(node);

    private void RemoveAll()
    {
        CommandHost.SaveRoot = CommandHost.DefaultSaveRoot;
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
        GD.PushError("Rebuild: " + label);
    }
}
