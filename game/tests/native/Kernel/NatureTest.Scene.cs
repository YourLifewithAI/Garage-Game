using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// What fire leaves for the scene and the verbs, and the smaller rules: a burnt tree's climbing collision goes with it and
/// comes back on undo; a burned-down object is gone for every verb that names it, a scorched creation can only be repaired
/// or removed; a partly burnt object in the hands keeps the hands' collision; the Gubble needs the player to turn back on
/// a trail the player turned off; malformed nature commands are request_invalid; the island's limits are the pack's; a
/// preview id is 128 random bits; nature changes are never transient.
/// </summary>
public partial class NatureTest
{
    private async Task TestScene()
    {
        await TestBurntRemains();
        await TestCarriedThings();
        await TestOlderFlowers();
        await TestNatureRequests();
    }

    /// <summary>
    /// The book burns down with a tree's climbing faces inside it; the verbs that name it find nothing; the undo brings all
    /// back (in Creative, the only mode with undo); a scorched creation (after Creative's one ask) waits for repair.
    /// </summary>
    private async Task TestBurntRemains()
    {
        await NewWorld(SavePath("remains"));
        var climb = new StaticBody3D { Name = "tree_climb_bark", CollisionLayer = RoomBuilder.HiddenLayer, CollisionMask = 0 };
        climb.SetMeta("entity_id", "shell:tree_climb_bark");
        // One face inside the book's bounds (a pole in a tree that burns), one far away (another tree's).
        var faces = new ConcavePolygonShape3D
        {
            Data = new[]
            {
                new Vector3(0.44f, 0.01f, 0.1f), new Vector3(0.46f, 0.01f, 0.1f), new Vector3(0.45f, 0.03f, 0.1f),
                new Vector3(-1, 0.01f, -1), new Vector3(-0.98f, 0.01f, -1), new Vector3(-0.99f, 0.03f, -1),
            },
        };
        var holder = new CollisionShape3D { Shape = faces };
        climb.AddChild(holder);
        _world!.GetNode("Room/Shell").AddChild(climb);
        var book = _host.ObjectNodeOf("obj:book") as CollisionObject3D;
        var layer = book?.CollisionLayer ?? 0;
        var lit = Fire(Player, new Vector3(0.45f, 0.01f, -0.1f), 0.15, 10, out _);
        Steps(16);
        Check(Ok(lit) && _host.Nature.Objects.TryGetValue("obj:book", out var ash) && ash.BurnedDown && book != null && !book.Visible && book.CollisionLayer == 0,
            "the book burns down: hidden, its collision gone");
        Check(((ConcavePolygonShape3D)holder.Shape).GetFaces().Length == 3, "the climbing faces inside the burnt book went with it; the others stay");
        Check(_player.TryTeleportTo(new Vector3(-0.3f, 0.006f, 0.62f)), "the player stands by the doorstop");
        await Frames(4);
        Check(Ok(Send(Command(NextId("grab-doorstop"), "entity.grab", new JsonObject { ["target"] = "obj:doorstop" }), Player)), "and picks it up");
        var onAsh = Send(Command(NextId("release-on-book"), "entity.release", new JsonObject { ["placement"] = new JsonObject { ["position_m"] = new JsonArray(0.45, 0.04, 0.1), ["on"] = "obj:book" } }), Player);
        var lockAsh = Send(Command(NextId("lock-book"), "protect.lock", new JsonObject { ["targets"] = new JsonArray("obj:book") }, expectedRevision: _host.Revision), Player);
        var grabAsh = Send(Command(NextId("grab-book"), "entity.grab", new JsonObject { ["target"] = "obj:book" }), Companion);
        Check(Code(onAsh) == "target_not_found" && Code(lockAsh) == "target_not_found" && Code(grabAsh) == "target_not_found",
            "a burned-down object is gone for every verb that names it: put on, protected, picked up");
        var undone = Send(Command(NextId("undo-book"), "room.undo", new JsonObject { ["to_revision"] = _host.Nature.UndoTarget(_host.Revision) ?? 0 }, expectedRevision: _host.Revision), Player);
        Check(Ok(undone) && !_host.Nature.Objects.ContainsKey("obj:book") && book!.Visible && book.CollisionLayer == layer &&
            ((ConcavePolygonShape3D)holder.Shape).GetFaces().Length == 6, "undoing the fire brings the book, its collision and the climbing faces back: " + Canonical(undone["error"]));
        Send(Command(NextId("release-doorstop"), "entity.release", new JsonObject()), Player);

        var hut = Build("Remains hut", 1.5, 1.2);
        var held = Fire(Player, new Vector3(1.35f, 0.01f, 1.2f), 0.3, 6, out _);
        _host.Approve(held["approval_needed"]?["request_id"]?.GetValue<string>() ?? "");
        Steps(4);
        var lockHut = Send(Command(NextId("lock-hut"), "protect.lock", new JsonObject { ["targets"] = new JsonArray(hut) }, expectedRevision: _host.Revision), Player);
        var onHut = Send(Command(NextId("build-on-hut"), "creation.place", new JsonObject { ["source"] = Source("On the hut"), ["placement"] = new JsonObject { ["position_m"] = new JsonArray(1.5, 0.12, 1.2), ["on"] = hut } }), Player);
        Check(_host.IsNatureUnavailable(hut) && Code(lockHut) == "target_busy" && Code(onHut) == "target_busy", "a scorched creation can be neither protected nor built on");
        Steps(14);
        var removed = Send(Command(NextId("remove-hut"), "entity.remove", new JsonObject { ["target"] = hut }, expectedRevision: _host.Revision), Player);
        Check(Ok(removed) && !_host.Nature.Objects.ContainsKey(hut), "but it can be removed, and its scorch record goes with it in the same save");
    }

    /// <summary>Review major 12: a partly burnt book in the hands keeps the hands' collision (none) through the simulation's ticks.</summary>
    private async Task TestCarriedThings()
    {
        await NewWorld(SavePath("carried"));
        var book = (CollisionObject3D)_host.ObjectNodeOf("obj:book")!;
        var layer = book.CollisionLayer;
        var lit = Fire(Player, new Vector3(0.45f, 0.01f, -0.1f), 0.15, 1, out _);
        Steps(3);
        Check(Ok(lit) && _host.Nature.Fire == null && _host.Nature.Objects.TryGetValue("obj:book", out var singed) && singed.Burn is > 0 and < 1 && book.CollisionLayer == layer,
            "a short fire singes the book without burning it down; its collision is untouched");
        Check(_companion.TryTeleportTo(new Vector3(0.45f, 0.006f, 0.3f)), "the Gubble stands by the book");
        await Frames(4);
        var grab = Send(Command(NextId("grab-book"), "entity.grab", new JsonObject { ["target"] = "obj:book", ["actor"] = CommandHost.CompanionAvatarId }), Companion);
        Check(Ok(grab) && book.CollisionLayer == 0, "and picks it up (in the hands it collides with nothing): " + Code(grab));
        for (var i = 0; i < 8; i++) _host._PhysicsProcess(0.5);
        Check(book.CollisionLayer == 0, "the simulation's ticks never give a carried thing its collision back");
        Send(Command(NextId("drop-book"), "entity.release", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }), Companion);
        Check(book.CollisionLayer == layer, "putting it down does");
    }

    /// <summary>
    /// Decision 52: Bloom became Grow and its flower trail was removed. An older island's save that holds the trail's flowers (and the
    /// trail switched on) loads cleanly: its flowers are dropped, whatever else it holds (here a grown plant) is kept, and nothing plants
    /// flowers again as the Gubble walks.
    /// </summary>
    private async Task TestOlderFlowers()
    {
        await NewWorld(SavePath("older-flowers"));
        Check(Ok(Send(Command(NextId("older-grow"), "nature.cast", Cast("grow", NearGubble, 0.1, 1)), Player)), "a Grow is saved with the island");
        await FreeWorld();
        var path = ProjectSettings.GlobalizePath(SavePath("older-flowers"));
        var save = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        var older = NatureBlock.Unpack(save["nature"]!);
        older.TrailEnabled = true;
        var planted = Flowers(older, NearGubble + new Vector3(0, 0, -0.3f), 0.1f, 120, null);
        save["nature"] = NatureBlock.Pack(older);
        System.IO.File.WriteAllBytes(path, CanonicalJson.Bytes(save));
        await NewWorld(SavePath("older-flowers"));
        var grown = _host.Nature.Plants.Count;
        Check(planted > 0 && _host.Authority.Call("is_ready").AsBool() && _host.NatureNotice.Length == 0 && _host.Nature.Cells.Count == 0 && grown > 0,
            $"an older island's save with the trail's flowers loads cleanly: the {planted} flower cells are dropped, its grown plants kept ({grown})");
        _companion.GlobalPosition += new Vector3(0.3f, 0, 0.2f);
        Steps(4);
        Check(_host.Nature.Cells.Count == 0 && Send(Query("nature.describe", new JsonObject()), Player)["data"]?["trail_enabled"]?.GetValue<bool>() == false,
            "and no trail plants flowers as the Gubble walks: nature.describe says the trail is off");
    }

    /// <summary>Review item 14 and 15: request shapes, the pack's limits, the preview id, durable receipts.</summary>
    private async Task TestNatureRequests()
    {
        await NewWorld(SavePath("requests"));
        var malformed = new (string Label, JsonObject Args, string Op, string Expected)[]
        {
            ("an ability that is not grow or fire", Cast("ice", NearGubble, 0.2, 10), "nature.cast", "request_invalid"),
            ("Bloom, retired by decision 52", Cast("bloom", NearGubble, 0.2, 10), "nature.cast", "request_invalid"),
            ("a radius past the contract's 1.5 m", Cast("grow", NearGubble, 2, 10), "nature.cast", "request_invalid"),
            ("a duration that is not a number", Edited(Cast("grow", NearGubble, 0.2, 10), a => a["duration_s"] = "long"), "nature.cast", "request_invalid"),
            ("a missing centre", Edited(Cast("grow", NearGubble, 0.2, 10), a => a.Remove("center_m")), "nature.cast", "request_invalid"),
            ("a preview id this host never made", Cast("fire", NearGubble, 0.2, 10, "not-a-preview"), "nature.cast", "request_invalid"),
            ("an argument the op does not have", Edited(Cast("grow", NearGubble, 0.2, 10), a => a["owner"] = Player), "nature.cast", "field_unknown"),
            ("a trail that is neither on nor off", new JsonObject { ["enabled"] = "yes" }, "nature.trail", "request_invalid"),
            ("an undo to a negative revision", new JsonObject { ["to_revision"] = -1 }, "room.undo", "request_invalid"),
            ("an undo to a revision that is not a whole number", new JsonObject { ["to_revision"] = 1.5 }, "room.undo", "request_invalid"),
        };
        foreach (var (label, args, op, expected) in malformed)
            Check(Code(Send(Command(NextId("malformed"), op, args, expectedRevision: op == "room.undo" ? _host.Revision : null), Player, contractValid: false)) == expected, $"{label} is {expected}");
        Check(Code(Send(Command(NextId("unnamed-undo"), "room.undo", new JsonObject { ["to_revision"] = 0 }), Player, contractValid: false)) == "request_invalid",
            "an undo that does not name the room revision it undoes from is request_invalid");
        var pack = JsonNode.Parse(Godot.FileAccess.GetFileAsString(IslandRules.PathFor(RoomWorld.DefaultRulesId, ShippedRules.Version)))!.AsObject();
        var grow = pack["abilities"]!.AsArray().First(a => a!["capability"]!.GetValue<string>() == "grow")!;
        grow["area_radius_max_m"] = 0.3;
        grow["area_radius_default_m"] = 0.3;
        _host.UseRules(IslandRules.Parse(CanonicalJson.Bytes(pack)));
        var wide = Send(Command(NextId("wide-bloom"), "nature.cast", Cast("grow", NearGubble, 0.4, 10)), Player);
        Check(Code(wide) == "invalid_args" && wide["error"]?["allowed"]?.GetValue<double>() == 0.3, "a radius past the pack's own limit is refused with the pack's limit");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var preview = Send(Command(NextId("id-preview"), "nature.cast", Cast("fire", NearGubble, 0.2, 10), preview: true), Player);
        Check(System.Text.RegularExpressions.Regex.IsMatch(preview["data"]?["preview_id"]?.GetValue<string>() ?? "", @"\A[a-f0-9]{32}\z"), "a preview id is 32 hex characters (128 random bits)");
        Check(new[] { "nature.cast", "nature.trail", "nature.repair", "room.undo" }.All(op => !CommandHost.IsTransientOp(op)) &&
            new[] { "world.set_mode", "glow.configure" }.All(CommandHost.IsTransientOp), "nature changes keep durable receipts; the player's own settings transient ones");
        await FreeWorld();
    }
}
