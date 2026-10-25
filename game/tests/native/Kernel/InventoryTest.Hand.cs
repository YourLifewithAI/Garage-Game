using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// P-Inventory-2's hand (docs/game/MECHANICS.md decision 119, docs/game/HOTBAR.md): inventory.select (player-only, transient) kept per mode in
/// the team file; player.status.hand; a stack's slot arms its unit for inventory.use and inventory.drop with no item; a unique thing's slot
/// takes it into the hand (the pockets' inventory.take) and another selection returns it to its own slot; a gathered log's slot is selected.
/// </summary>
public partial class InventoryTest
{
    private JsonObject Select(string principal, int? slot, string? layer = null, bool preview = false, string? actionId = null, bool contractValid = true)
    {
        var args = new JsonObject { ["slot"] = slot };
        if (layer != null) args["layer"] = layer;
        return Send(Command(actionId ?? NextId("select"), "inventory.select", args, preview), principal, contractValid);
    }

    private JsonObject UseSelected() => Send(Command(NextId("use"), "inventory.use", new JsonObject()), Player);
    private JsonObject DropSelected() => Send(Command(NextId("drop"), "inventory.drop", new JsonObject()), Player);

    private JsonObject HandOf(JsonObject status) => status["hand"]?.AsObject() ?? new JsonObject();

    private static JsonObject HandJson(string layer, int? slot, string holds, string? item = null, int? count = null, int? pockets = null, int? abilities = null, string? carrying = null)
    {
        var hand = new JsonObject { ["abilities_slot"] = abilities, ["holds"] = holds, ["layer"] = layer, ["pockets_slot"] = pockets, ["slot"] = slot };
        if (item != null) hand["item"] = item;
        if (count != null) hand["count"] = count;
        if (carrying != null) hand["carrying"] = carrying;
        return hand;
    }

    /// <summary>The selection, the armed unit, a unique thing into the hand and back, the abilities' layer, a reload, and the team file.</summary>
    private async Task TestHandSelection()
    {
        await NewWorld("hand");
        Check(Canonical(HandOf(Status())) == Canonical(HandJson("abilities", null, "nothing")) && _host.Hand is { Layer: "abilities", Slot: null, Holds: "nothing" },
            "a new team's hand holds nothing and shows the magic layer: no slot chosen on either layer: " + Canonical(HandOf(Status())));
        Check(Refused(Select(Companion, 1), "permission_denied", "Only the player can do that, directly in the game.", "$.op"),
            "the companion never touches the selection: refused at $.op before anything is looked up");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")) && await PickUp("obj:apple_2", ByApple2) && Ok(Store(Player, "pockets")) &&
              await PickUp("obj:cup", ByCup) && Ok(Store(Player, "pockets")),
            "two apples and the cup into the pockets");
        Check(_host.PocketEntries.Select(e => (e.Item, e.Slot)).SequenceEqual(new[] { ("apple", 1), ("obj:cup", 2) }),
            "each entry takes the lowest free slot as it comes: the apples' stack slot 1, the cup slot 2");
        var revision = _host.Revision;
        var selected = Select(Player, 1, actionId: "select-apples");
        Check(Ok(selected) && selected["transient"]?.GetValue<bool>() == true && _host.Revision == revision &&
              Canonical(selected["data"]) == Canonical(HandJson("pockets", 1, "unit", "apple", 2, pockets: 1)),
            "selecting slot 1 arms an apple: a transient receipt whose data is the hand: " + Canonical(selected));
        Check(Canonical(HandOf(Status(Companion))) == Canonical(HandJson("pockets", 1, "unit", "apple", 2, pockets: 1)), "player.status shows the hand, read-only, to the companion too");
        var team = TeamFile.Parse(File.ReadAllBytes(TeamPath("hand")));
        Check(team.HandOf("challenge") == new HandSelection("pockets", 1, null) && team.HandOf("creative").IsNone, "the team file keeps the selection for this mode only");
        Check(Refused(UseSelected(), "not_ready", "Your hearts are full: the Apple stays in your pockets.", "$.args.item"), "a use with no item uses the armed unit: at full hearts it stays");
        _host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        var eaten = UseSelected();
        Check(Ok(eaten) && Canonical(eaten["data"]?["item"]) == "\"apple\"" && Units(_host.PocketEntries, "apple") == 1, "hurt, the armed apple is eaten: the count on the slot falls");

        revision = _host.Revision;
        var cup = Select(Player, 2);
        Check(Ok(cup) && _host.HeldBy(PlayerAvatar) == "obj:cup" && EntityIds().Contains("obj:cup") && _host.Revision == revision + 1 &&
              Canonical(cup["data"]) == Canonical(HandJson("pockets", 2, "thing", "obj:cup", pockets: 2)) && _host.PocketEntries.All(e => e.Item != "obj:cup") &&
              _host.PocketContents.ReservedFor("obj:cup") == 2,
            "selecting the cup's slot takes it into the hand (one durable revision, the pockets' take), its slot kept for it: " + Canonical(cup["data"]));
        var take = Send(Query("journal.read", new JsonObject()), Player);
        Check(Ok(take), "(the journal reads)");
        Check(Ok(Store(Player, "pockets")) && _host.HeldBy(PlayerAvatar) == null && _host.PocketEntries.Single(e => e.Item == "obj:cup").Slot == 2,
            "E putting it in the pockets brings it back to its own slot");
        Check(Ok(Select(Player, 1)) && Ok(Select(Player, 2)) && _host.HeldBy(PlayerAvatar) == "obj:cup", "selected again, it is in the hand again");
        revision = _host.Revision;
        var back = Select(Player, 1);
        Check(Ok(back) && _host.HeldBy(PlayerAvatar) == null && !EntityIds().Contains("obj:cup") && _host.Revision == revision + 1 &&
              _host.PocketEntries.Select(e => (e.Item, e.Slot)).SequenceEqual(new[] { ("apple", 1), ("obj:cup", 2), ("seed", 3) }) && _host.Hand.Holds == "unit",
            "selecting another slot returns the cup to its own slot (one durable revision, a store into the pockets) and arms the apple (the eaten one's seed in slot 3)");
        // Decision 129: switching the bar's layers never touches what the hands carry; casting with full hands is allowed.
        Check(Ok(Select(Player, 2)) && _host.HeldBy(PlayerAvatar) == "obj:cup", "(the cup in the hand again)");
        revision = _host.Revision;
        Check(Ok(Select(Player, 3, "abilities")) && _host.HeldBy(PlayerAvatar) == "obj:cup" && _host.PocketEntries.All(e => e.Item != "obj:cup") && _host.Revision == revision &&
              Canonical(HandOf(Status())) == Canonical(HandJson("abilities", 3, "ability", pockets: 2, abilities: 3, carrying: "obj:cup")),
            "the abilities' layer in the hand keeps the cup in it (decision 129: no store, no revision), and keeps the pockets' slot for when the layer comes back: " + Canonical(HandOf(Status())));
        Check(Ok(Select(Player, 5, "abilities")) && _host.HeldBy(PlayerAvatar) == "obj:cup" && _host.Revision == revision, "a choice within the magic layer keeps it too");
        var cast = _host.PlayerEffect("glow");
        Check(Ok(cast) && _host.HeldBy(PlayerAvatar) == "obj:cup", "casting with full hands is allowed: Glow lights, the cup stays in the hand: " + Canonical(cast["error"]));
        Check(Ok(Select(Player, 2)) && _host.HeldBy(PlayerAvatar) == "obj:cup" && _host.Revision == revision && _host.Hand is { Layer: "pockets", Slot: 2, Holds: "thing", Item: "obj:cup" },
            "the pockets brought back down at the slot they kept (Tab) keep it as well: it is that slot's thing, in the hand: " + _host.Hand);
        Check(Ok(Select(Player, 3, "abilities")) && Ok(Select(Player, 1)) && _host.HeldBy(PlayerAvatar) == null && _host.Revision == revision + 1 &&
              _host.PocketEntries.Single(e => e.Item == "obj:cup").Slot == 2 && _host.Hand is { Layer: "pockets", Slot: 1, Holds: "unit", Item: "apple" },
            "only choosing a different pocket slot returns it (the pockets brought down at slot 1 from the strip): back in slot 2, the apple armed");
        Check(Ok(Select(Player, 3, "abilities")) && Ok(Select(Player, null)) && _host.Hand is { Layer: "pockets", Slot: null, Holds: "nothing", AbilitiesSlot: 3 },
            "no slot on the pockets' layer: the hand holds nothing");
        Check(Ok(Select(Player, 2)) && _host.HeldBy(PlayerAvatar) == "obj:cup", "(the cup in the hand)");
        await NewWorld("hand", keepFiles: true);
        Check(_host.HandNow == new HandSelection("pockets", 2, 3) && _host.HeldBy(PlayerAvatar) == null && EntityIds().Contains("obj:cup") && _host.PocketEntries.All(e => e.Item != "obj:cup") &&
              _host.Hand.Holds == "nothing",
            "after a reload the hand's selection is kept; the cup it held is not (holding is never saved): it lies on the island where it was last put down");
        Check(Ok(Store(Player, "chest", from: "pockets", item: "apple")) && _host.PocketEntries.All(e => e.Item == "seed"), "(the apple goes in the chest)");
    }

    /// <summary>Use and drop on the selected slot, the pockets' take into the hand, a gathered log's slot, and the refusals.</summary>
    private async Task TestHandReturnsAndRefusals()
    {
        await NewWorld("hand-more");
        Check(Refused(UseSelected(), "request_invalid", CommandHost.ChooseAPocketSlot, "$.args.item") && Refused(DropSelected(), "request_invalid", CommandHost.ChooseAPocketSlot, "$.args.item"),
            "with no slot chosen, a use or a drop that names nothing says to choose one");
        Check(Ok(Select(Player, 4)) && Refused(UseSelected(), "target_not_found", CommandHost.EmptySlot, "$.args.item") && Refused(DropSelected(), "target_not_found", CommandHost.EmptySlot, "$.args.item"),
            "an empty slot is empty");
        Check(Ok(_host.PlayerSelect(4)) && Refused(_host.PlayerUseSelected(), "target_not_found", CommandHost.EmptySlot, "$.args.item") &&
              Refused(_host.PlayerDropSelected(), "target_not_found", CommandHost.EmptySlot, "$.args.item"),
            "the HUD's calls say the same: PlayerSelect, PlayerUseSelected, PlayerDropSelected");
        Check(Ok(Select(Player, 5, "abilities")) && Refused(UseSelected(), "request_invalid", CommandHost.ChooseAPocketSlot, "$.args.item"), "an ability's slot is the HUD's to cast, not a use");
        Check(Refused(Select(Player, 11, contractValid: false), "request_invalid", "slot is a whole number from 1 to 10, or null for none.", "$.args.slot") &&
              Refused(Select(Player, 1, "wheel", contractValid: false), "request_invalid", "layer is pockets or abilities.", "$.args.layer"), "a slot past ten, or a layer the bar has not, is invalid");
        Check(await PickUp("obj:cup", ByCup) && Ok(Store(Player, "pockets")), "the cup into the pockets (slot 1)");
        var taken = Send(Command(NextId("take"), "inventory.take", new JsonObject { ["from"] = "pockets", ["item"] = "obj:cup", ["to"] = "hand" }), Player);
        Check(Ok(taken) && taken["transient"]?.GetValue<bool>() == false && Canonical(taken["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "obj:cup", ["to"] = "hand" }) &&
              _host.HeldBy(PlayerAvatar) == "obj:cup" && _host.HandNow == new HandSelection("pockets", 1, 5),
            "inventory.take from the pockets puts the cup in the hand, and its slot is the selection: " + Canonical(taken));
        Check(Refused(UseSelected(), "request_invalid", CommandHost.ThingHasNoUse, "$.args.item"), "a thing in the hand has no use");
        var revision = _host.Revision;
        var dropped = DropSelected();
        Check(Ok(dropped) && Canonical(dropped["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "obj:cup", ["to"] = "ground" }) && _host.HeldBy(PlayerAvatar) == null &&
              EntityIds().Contains("obj:cup") && _host.Revision == revision + 2 && _host.PocketContents.ReservedFor("obj:cup") == null && _host.Hand.Holds == "nothing",
            "a drop with no item sets the thing in the hand down before the player (its pose, then the drop's own receipt), and the slot is free: " + Canonical(dropped));
        Check(Refused(Send(Command(NextId("take"), "inventory.take", new JsonObject { ["from"] = "pockets", ["item"] = "apple", ["to"] = "hand" }), Player), "target_not_found", CommandHost.NotInPockets, "$.args.item") &&
              Refused(Send(Command(NextId("take"), "inventory.take", new JsonObject { ["from"] = "pockets", ["item"] = "obj:cup", ["to"] = "pockets" }), Player, contractValid: false), "request_invalid", "From the pockets, a thing comes into the hand.", "$.args.to") &&
              Refused(Send(Command(NextId("take"), "inventory.take", new JsonObject { ["from"] = "pockets", ["item"] = "obj:cup", ["to"] = "hand" }), Companion), "permission_denied", CommandHost.PocketsArePlayers, "$.args.from"),
            "the pockets' take names a thing in them, into the hand, and is the player's alone");
        // A gathered log's stack slot is the selection (the founder's refinement).
        await Stand(_player, ByLog(2), "beside the second log and the stone");
        Check(Ok(Gather(Player, "obj:log_2")) && _host.HandNow == new HandSelection("pockets", 1, 5) && _host.Hand is { Holds: "unit", Item: "log", Count: 1 },
            "a gathered log joins its stack and that slot becomes the hand's: " + _host.Hand);
        Check(Ok(Gather(Player, "obj:stone_1")) && _host.Hand is { Slot: 2, Holds: "unit", Item: "stone" }, "and the stone's own slot after it");

        await NewWorld("hand-full", editPack: inventory => inventory["pockets"]!["entries"] = 1);
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")) && Ok(Select(Player, 1)), "an apple fills the one slot, armed");
        Check(Refused(Select(Player, 2), "invalid_args", "Your pockets have one slot.", "$.args.slot"), "a slot past the pockets' own is refused in their words");
        Check(await PickUp("obj:cup", ByCup), "the cup is picked up with F, carried");
        Check(_host.Hand is { Holds: "unit", Item: "apple", Carrying: "obj:cup" }, "the hand's reader names what is carried beside the armed unit: " + _host.Hand);
        var full = Select(Player, null);
        Check(Refused(full, "occupied", "Your pockets are full: one thing.", "$.args") && _host.HeldBy(PlayerAvatar) == "obj:cup" && _host.HandNow == new HandSelection("pockets", 1, null),
            "moving the hand on would put the carried cup in the pockets; there is no room, so it is refused in their words and nothing changes");
        // Decision 129: the layers switch with full pockets and full hands alike (the hotbar's open question: the magic side no longer waits).
        Check(Ok(Select(Player, 2, "abilities")) && _host.HeldBy(PlayerAvatar) == "obj:cup" && _host.Hand is { Layer: "abilities", Holds: "ability", Carrying: "obj:cup" },
            "Tab to the magic layer while carrying the cup with full pockets: allowed, the cup still carried: " + _host.Hand);
        Check(Ok(Select(Player, 1)) && _host.HeldBy(PlayerAvatar) == "obj:cup" && _host.Hand is { Layer: "pockets", Slot: 1, Holds: "unit", Item: "apple", Carrying: "obj:cup" },
            "and back to the pockets at the slot they kept: the apple armed, the cup still carried (never stored by a switch): " + _host.Hand);
        Check(Refused(Select(Player, 1), "occupied", "Your pockets are full: one thing.", "$.args") && _host.HeldBy(PlayerAvatar) == "obj:cup",
            "choosing a pocket slot on the pockets' layer still stores a carried thing first, as before: with no room, refused");
    }
}
