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
/// The review of 13 October, its blockers and majors through the contract's command path only (each check failed on
/// 7f21aad): nature is room state and survives a rebuild from room state; its receipts are the authority's and a
/// checkpoint compacts them; a nature change is a room revision and room.undo names room revisions, across both
/// principals' changes; every nature change needs a role that builds, also when a held one is approved; a companion
/// learns nothing of a creation the team has not seen; and the founder's fire rule as decided through 13 October (the
/// Gubble's fire near the player is NatureTest.Consequences: no longer held, it hurts and the Gubble hears of it).
/// </summary>
public partial class NatureTest
{
    private static readonly Vector3 NearGubble = new(0.45f, 0.01f, 0.95f);

    /// <summary>Review blocker 1: room state carries the island's nature, a rebuild restores it, and the room's own save reloads it.</summary>
    private async Task TestNatureIsRoomState()
    {
        await NewWorld(SavePath("state-a"));
        var bloom = Send(Command("state-bloom", "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Player);
        var cells = _host.Nature.Plants.Count;
        Check(Ok(bloom) && cells > 0, "the player's Grow grows the plants there");
        var before = _host.ExportRoomState()!;
        Check(before["extensions"]?["x_play_nature"] is JsonObject, "room state carries the island's nature as x_play_nature");
        await NewWorld(SavePath("state-b"));
        var rebuilt = _host.ImportRoomState(before);
        Check(rebuilt.Ok && _host.Nature.Plants.Count == cells, "a room built from nothing is rebuilt from that room state, its grown plants included: " + rebuilt.Message);
        var after = _host.ExportRoomState()!;
        Check(Canonical(Unsessioned(after)) == Canonical(Unsessioned(before)), "and writes the same room state again, nature and its receipt included");
        await NewWorld(SavePath("state-b"));
        Check(_host.Nature.Plants.Count == cells && Canonical(Unsessioned(_host.ExportRoomState()!)) == Canonical(Unsessioned(before)), "the rebuilt room's own save loads the same nature again");
    }

    /// <summary>Review blocker 2: a nature command's receipt is a durable authority receipt, and room.checkpoint compacts it like any other.</summary>
    private async Task TestReceiptsCompact()
    {
        await NewWorld(SavePath("compact"));
        var bloom = Command("compact-bloom", "nature.cast", Cast("grow", NearGubble, 0.2, 1));
        Check(Ok(Send(bloom, Player)), "a Grow commits");
        Check(Ok(Send(Command("compact-checkpoint", "room.checkpoint", new JsonObject()), Player)), "the player takes a checkpoint");
        var lookup = Send(Query("receipt.lookup", new JsonObject { ["action_id"] = "compact-bloom" }), Player);
        Check(lookup["data"]?["found"]?.GetValue<bool>() == true && lookup["data"]?["compacted"]?.GetValue<bool>() == true, "the checkpoint compacted the Grow's receipt: " + Canonical(lookup["data"]));
        Check(Send(bloom, Player)["replayed"]?.GetValue<bool>() == true, "the compacted Grow still replays");
        Check(Code(Send(Command("compact-bloom", "nature.cast", Cast("grow", NearGubble, 0.3, 1)), Player)) == "action_id_conflict", "and still refuses other content under its id");
    }

    /// <summary>
    /// Review blocker 6: a nature change is a room revision; room.undo names the room revision to go back to, as the
    /// contract says; one principal's change keeps the other's undo step; a companion's undo of the player's change waits.
    /// </summary>
    private async Task TestUndoByRoomRevision()
    {
        await NewWorld(SavePath("undo"));
        Check(Build("Undo crate", -1.5, 0.9).Length > 0, "the player builds something first (the room revision moves)");
        var beforeBloom = _host.Revision;
        var bloom = Send(Command("undo-player-bloom", "nature.cast", Cast("grow", NearGubble, 0.15, 1)), Player);
        Check(Ok(bloom) && _host.Revision == beforeBloom + 1 && bloom["revision"]?.GetValue<int>() == beforeBloom + 1, "a Grow is a room revision of its own");
        var companionBloom = Send(Command("undo-gubble-bloom", "nature.cast", Cast("grow", NearGubble + new Vector3(0.3f, 0, 0), 0.1, 1)), Companion);
        Check(Ok(companionBloom) && _host.Revision == beforeBloom + 2, "the Gubble's Grow is the next one");
        var undone = Send(Command("undo-both", "room.undo", new JsonObject { ["to_revision"] = beforeBloom }, expectedRevision: _host.Revision), Player);
        Check(Ok(undone) && _host.Nature.Plants.Count == 0 && _host.Revision == beforeBloom + 3,
            "room.undo to the revision before the player's Grow takes back both: the Gubble's change kept the player's step: " + Canonical(undone["error"]));
        var again = Send(Command("undo-player-again", "nature.cast", Cast("grow", NearGubble, 0.15, 1)), Player);
        var held = Send(Command("undo-gubble-asks", "room.undo", new JsonObject { ["to_revision"] = _host.Revision - 1 }, expectedRevision: _host.Revision), Companion);
        Check(Ok(again) && Code(held) == "approval_required" && _host.Nature.Plants.Count > 0, "the Gubble's undo of the player's change waits for the player, changing nothing");
        var requestId = held["approval_needed"]?["request_id"]?.GetValue<string>();
        var approved = requestId == null ? new JsonObject() : _host.Approve(requestId);
        Check(Ok(approved) && approved["approved_by"]?.GetValue<string>() == Player && _host.Nature.Plants.Count == 0, "and the player's click takes the change back");
    }

    /// <summary>Review major 7: every nature change needs a role that builds, before a preview, a hold or a commit, and again on the player's click.</summary>
    private async Task TestAuthorityChecks()
    {
        await NewWorld(SavePath("roles"));
        _host.Authority.Call("set_role", Player, Companion, "visitor");
        var bloom = Send(Command(NextId("visitor-bloom"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Companion);
        var preview = Send(Command(NextId("visitor-preview"), "nature.cast", Cast("fire", NearGubble, 0.2, 10), preview: true), Companion);
        Check(Code(bloom) == "permission_denied" && Code(preview) == "permission_denied" && _host.Nature.Plants.Count == 0,
            "a visitor Gubble can neither Grow nor preview Fire: " + Code(bloom) + ", " + Code(preview));
        _host.Authority.Call("set_role", Player, Companion, "editor");
        var pack = JsonNode.Parse(Godot.FileAccess.GetFileAsString(IslandRules.PathFor(RoomWorld.DefaultRulesId, ShippedRules.Version)))!.AsObject();
        pack["abilities"]!.AsArray().First(a => a!["capability"]!.GetValue<string>() == "grow")!["tier"] = "keyed_yes";
        _host.UseRules(IslandRules.Parse(CanonicalJson.Bytes(pack)));
        var held = Send(Command(NextId("keyed-bloom"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Companion);
        _host.Authority.Call("set_role", Player, Companion, "visitor");
        var requestId = held["approval_needed"]?["request_id"]?.GetValue<string>();
        var approved = requestId == null ? new JsonObject() : _host.Approve(requestId);
        Check(Code(held) == "approval_required" && !Ok(approved) && _host.Nature.Plants.Count == 0,
            "a held Grow asked as an editor commits nothing when the player approves it after the Gubble became a visitor");
        _host.Authority.Call("set_role", Player, Companion, "editor");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        // The flower trail went with Bloom (decision 52): asked of either, it is refused in words and changes nothing.
        var revision = _host.Revision;
        var trail = Send(Command(NextId("gubble-trail-on"), "nature.trail", new JsonObject { ["enabled"] = true }), Companion);
        var mine = Send(Command(NextId("player-trail-off"), "nature.trail", new JsonObject { ["enabled"] = false }), Player);
        Check(Code(trail) == "unsupported_capability" && Code(mine) == "unsupported_capability" && trail["error"]?["message"]?.GetValue<string>() == CommandHost.TrailRetired &&
              _host.Revision == revision && _host.Nature.Cells.Count == 0, "the flower trail is gone: nature.trail is refused, whoever asks, and changes nothing");
    }

    /// <summary>
    /// Review major 8: a creation behind a wall that neither avatar has seen. The Gubble's fire would reach it: the host
    /// knows (in Creative it asks the player once), but nothing the Gubble reads names it or counts it.
    /// </summary>
    private async Task TestUnseenStaysUnseen()
    {
        await NewWorld(SavePath("unseen"));
        var hidden = Build("Hidden shed", -1.5, 0.9);
        var wall = new StaticBody3D { Name = "TestWall", Position = new Vector3(-1.41f, 0.5f, 0.9f), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.02f, 1f, 1f) } });
        _world!.AddChild(wall);
        await Frames(2);
        var seen = Send(Query("entities.list", new JsonObject()), Companion);
        Check(hidden.Length > 0 && Ok(seen) && !Canonical(seen).Contains(hidden, StringComparison.Ordinal), "the wall hides the new creation from both avatars");
        var commit = Fire(Companion, new Vector3(-1.3f, 0.01f, 0.9f), 0.4, 10, out var preview);
        Check(Ok(preview) && preview["data"]?["requires_approval"]?.GetValue<bool>() == true, "the host knows the Gubble's fire would reach a creation: it needs the player's yes");
        Check(!Canonical(preview).Contains(hidden, StringComparison.Ordinal), "the Gubble's fire preview never names the creation it cannot see: " + Canonical(preview["data"]?["affected_ids"]));
        var reason = commit["approval_needed"]?["reason"]?.GetValue<string>() ?? "";
        Check(Code(commit) == "approval_required" && !Canonical(commit).Contains(hidden, StringComparison.Ordinal) && !reason.Any(char.IsDigit),
            "the held fire's reason neither names nor counts what it would scorch: " + reason);
        var requestId = commit["approval_needed"]?["request_id"]?.GetValue<string>();
        var lit = requestId == null ? new JsonObject() : _host.Approve(requestId);
        Steps(6);
        Check(Ok(lit) && !Canonical(lit).Contains(hidden, StringComparison.Ordinal) && _host.Nature.Objects.TryGetValue(hidden, out var scorch) && scorch.Burn > 0,
            "the approved fire scorches the creation, and the Gubble's receipt does not name it");
        var described = Send(Query("nature.describe", new JsonObject()), Companion);
        var forPlayer = Send(Query("nature.describe", new JsonObject()), Player);
        Check(Ok(described) && !Canonical(described).Contains(hidden, StringComparison.Ordinal) && Canonical(forPlayer).Contains(hidden, StringComparison.Ordinal),
            "nature.describe lists the scorched creation for the player only");
    }

    /// <summary>
    /// Review major 9, the founder's rule as decided through 13 October: in Creative, before any fire burns an unlocked
    /// creation, the player is asked once (no: the burn stops at it; yes: it is scorched, repaired for free); in Challenge
    /// the player's own fire and the Gubble's scorch it with no ask; the island's own objects burn down completely.
    /// </summary>
    private async Task TestFireRule()
    {
        var spot = new Vector3(1.35f, 0.01f, 1.2f);
        await NewWorld(SavePath("rule"));
        var shed = Build("Creative shed", 1.5, 1.2);
        var held = Fire(Player, spot, 0.3, 6, out var preview);
        var reason = held["approval_needed"]?["reason"]?.GetValue<string>() ?? "";
        Check(shed.Length > 0 && Ok(preview) && preview["data"]?["requires_approval"]?.GetValue<bool>() == true && Canonical(preview["data"]?["affected_ids"]).Contains(shed, StringComparison.Ordinal) &&
            Code(held) == "approval_required" && reason.Contains("Is it OK if they burn?", StringComparison.Ordinal),
            "in Creative a fire that would reach a creation asks the player once, in plain words: " + reason);
        var requestId = held["approval_needed"]?["request_id"]?.GetValue<string>();
        var spared = requestId == null ? new JsonObject() : _host.Deny(requestId);
        Check(Ok(spared) && spared["approved_by"] == null && _host.Nature.Fire != null && _host.PendingApprovals.Count == 0,
            "the player's no still lights the fire, approving nothing, and nothing asks again: " + Canonical(spared["error"]));
        Steps(14);
        Check(!_host.Nature.Objects.ContainsKey(shed) && _host.Nature.Fire == null && _host.Nature.Cells.Values.Any(c => c.Ash > 0), "the fire burns, and stops at the spared creation");
        var book = Fire(Player, new Vector3(0.45f, 0.01f, -0.1f), 0.15, 10, out var bookPreview);
        Check(Ok(book) && Canonical(bookPreview["data"]?["affected_ids"]).Contains("obj:book", StringComparison.Ordinal), "a fire by the book would burn it");
        Steps(16);
        var listed = Send(Query("entities.list", new JsonObject { ["limit"] = 100 }), Player);
        Check(_host.Nature.Objects.TryGetValue("obj:book", out var ash) && ash.Burn >= 1 && Ok(listed) && !Canonical(listed).Contains("obj:book", StringComparison.Ordinal),
            "the island's own book burns down completely and is gone from the room");
        Check(_host.ExportRoomState()!["entities"]?["obj:book"]?["removed"]?.GetValue<bool>() == true, "room state lists it as removed, at the pose it burned at");

        await NewWorld(SavePath("rule-yes"));
        var cabin = Build("Creative cabin", 1.5, 1.2);
        var asked = Fire(Companion, spot, 0.3, 6, out _);
        var yes = asked["approval_needed"]?["request_id"]?.GetValue<string>() is { } yesId ? _host.Approve(yesId) : new JsonObject();
        Steps(4);
        var wallet = _host.Nature.Wallet.ToDictionary(p => p.Key, p => p.Value);
        Check(Code(asked) == "approval_required" && Ok(yes) && _host.Nature.Objects.TryGetValue(cabin, out var burnt) && burnt.Burn > 0,
            "in Creative the Gubble's fire asks the same once, and the player's yes lets the creation burn to scorched remains");
        Steps(12);
        Check(Ok(_host.PlayerNatureRepair(cabin)) && !_host.Nature.Objects.ContainsKey(cabin) && wallet.All(p => _host.Nature.Wallet[p.Key] == p.Value),
            "its design is kept, and Creative repairs it for free");

        await NewWorld(SavePath("rule-gubble-no"));
        var hall = Build("Creative hall", 1.5, 1.2);
        var gubbleAsks = Fire(Companion, spot, 0.3, 6, out _);
        var noId = gubbleAsks["approval_needed"]?["request_id"]?.GetValue<string>();
        var no = noId == null ? new JsonObject() : _host.Deny(noId);
        var status = noId == null ? new JsonObject() : Send(Query("approval.status", new JsonObject { ["request_id"] = noId }), Companion);
        Steps(14);
        Check(Ok(no) && status["data"]?["state"]?.GetValue<string>() == "denied" && status["data"]?["result"]?["ok"]?.GetValue<bool>() == true && !_host.Nature.Objects.ContainsKey(hall),
            "the player's no to the Gubble's fire spares the creation; the Gubble reads that its ask was denied and its fire lit without it");

        await NewWorld(SavePath("rule-challenge"), challenge: true);
        var hut = Build("Challenge hut", 1.5, 1.2);
        var lit = Fire(Player, spot, 0.3, 6, out var challengePreview);
        Steps(4);
        Check(challengePreview["data"]?["requires_approval"]?.GetValue<bool>() == false && Ok(lit) && _host.PendingApprovals.Count == 0 &&
            _host.Nature.Objects.TryGetValue(hut, out var scorched) && scorched.Burn > 0 && _host.IsNatureUnavailable(hut),
            "in Challenge the player's own fire scorches their creation with no warning and no ask; out of use until repaired");

        await NewWorld(SavePath("rule-challenge-gubble"), challenge: true);
        var barn = Build("Challenge barn", 1.5, 1.2);
        var gubbleLit = Fire(Companion, spot, 0.3, 6, out var gubblePreview);
        Steps(4);
        Check(gubblePreview["data"]?["requires_approval"]?.GetValue<bool>() == false && Ok(gubbleLit) && _host.Nature.Objects.TryGetValue(barn, out var charred) && charred.Burn > 0,
            "and so does the Gubble's fire, with no ask: " + Code(gubbleLit));
        Check(Code(Send(Command(NextId("lock-scorched"), "protect.lock", new JsonObject { ["targets"] = new JsonArray(barn) }, expectedRevision: _host.Revision), Player)) == "target_busy",
            "a scorched creation waits for repair before anything else");
    }
}
