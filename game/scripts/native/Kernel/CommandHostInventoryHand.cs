using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Sandbox;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Kernel;

/// <summary>
/// What the hand holds now (player.status.hand; the hotbar's reader): the layer in the hand and its selected slot (null: none); what the
/// selection is (holds): "unit" (a stack's unit armed: item its kind, count its units), "thing" (the slot's unique thing, now in the
/// hand), "pocketed" (the slot's unique thing, still in the pockets: from another island, or too heavy to hold), "ability" (the
/// abilities layer's slot: the HUD's to arm), or "nothing" (no slot, or an empty one); what the hands carry when it is not the
/// selection's thing (a thing picked up with F); and each layer's slot. Names stay the entries' (PocketEntries); this carries ids only.
/// </summary>
public sealed record HandView(string Layer, int? Slot, string Holds, string? Item, int Count, string? Carrying, int? PocketsSlot, int? AbilitiesSlot);

/// <summary>
/// The hand's selection (P-Inventory-2; docs/game/MECHANICS.md decision 119, docs/game/HOTBAR.md; contracts: inventory.select and
/// player.status.hand). inventory.select (player-only, transient) records which layer of the hotbar is in the hand and that layer's
/// slot (1 to 10, or none), kept per mode in the team file (TeamFile.Hands), so a reload keeps the hand. The kernel acts on the
/// pockets' layer only: a stack's slot arms its unit (inventory.use and inventory.drop act on it when they name no item); a unique
/// thing's slot takes it into the hand (inventory.take from the pockets, a durable receipt under the host's own action id, its slot
/// reserved so it comes back there); and any other choice on the pockets' layer (another slot, or none) first returns what the hands
/// carry to the pockets (inventory.store into the pockets, to its own slot when it came from one, else the lowest free one), refused in
/// the pockets' words when there is no room, and then nothing changes. Switching the layers never touches what the hands carry
/// (decision 129): the abilities' layer in the hand, a choice within it, and the pockets brought back down at the slot they kept all leave
/// it held, so casting with full hands is allowed. The abilities' layer is the HUD's: the kernel only records it.
/// No companion command touches the selection or the hand (AGENTS.md: the player's avatar is the player's).
/// </summary>
public partial class CommandHost
{
    public const string ChooseAPocketSlot = "Choose one of your pockets' slots first.";
    public const string EmptySlot = "That slot is empty.";
    public const string ThingHasNoUse = "A thing has no use: set it down, or put it away.";
    public const string StacksStayInPockets = "A stack stays in your pockets: choose its slot to use a unit.";

    private readonly Dictionary<string, HandSelection> _handUnfiled = new(StringComparer.Ordinal);

    /// <summary>The hand's selection in this mode: the team file's, else this session's.</summary>
    public HandSelection HandNow => Team is { } team ? team.HandOf(Mode) : _handUnfiled.GetValueOrDefault(Mode) ?? HandSelection.None;

    /// <summary>What the hand holds now, for the HUD and player.status.hand.</summary>
    public HandView Hand
    {
        get
        {
            var hand = HandNow;
            var carried = HeldBy(PlayerAvatar);
            string holds = "nothing";
            string? item = null;
            var count = 0;
            if (hand.Layer == HandSelection.AbilitiesLayer) holds = hand.Abilities == null ? "nothing" : "ability";
            else if (hand.Pockets is { } slot && HasInventory)
            {
                var pockets = PocketContents;
                if (pockets.ReservationAt(slot) is { } reserved && reserved == carried) (holds, item, count) = ("thing", reserved, 1);
                else if (pockets.ItemAt(slot) is { } at)
                    (holds, item, count) = at.StartsWith("obj:", StringComparison.Ordinal) ? ("pocketed", at, 1) : ("unit", at, pockets.Units(at));
            }
            return new HandView(hand.Layer, hand.Slot, holds, item, count, carried != null && carried != item ? carried : null, hand.Pockets, hand.Abilities);
        }
    }

    /// <summary>player.status.hand (and inventory.select's data): what the hand holds, ids only.</summary>
    private JsonObject HandStatus()
    {
        var hand = Hand;
        var data = new JsonObject
        {
            ["layer"] = hand.Layer, ["slot"] = hand.Slot, ["holds"] = hand.Holds,
            ["pockets_slot"] = hand.PocketsSlot, ["abilities_slot"] = hand.AbilitiesSlot,
        };
        if (hand.Item != null) data["item"] = hand.Item;
        if (hand.Holds == "unit") data["count"] = Math.Clamp(hand.Count, 1, InventoryContents.MaxUnits);
        if (hand.Carrying != null) data["carrying"] = hand.Carrying;
        return data;
    }

    /// <summary>The player's key for the hotbar: inventory.select of a slot of a layer (or none), as the player's command.</summary>
    public JsonObject PlayerSelect(int? slot, string layer = HandSelection.PocketsLayer, bool preview = false) =>
        PlayerCommand("inventory.select", new JsonObject { ["layer"] = layer, ["slot"] = slot }, preview);

    /// <summary>A unique thing from the pockets into the hand (inventory.take from the pockets), its slot then the selection.</summary>
    public JsonObject PlayerTakeFromPockets(string item, bool preview = false) =>
        PlayerCommand("inventory.take", new JsonObject { ["from"] = "pockets", ["item"] = item, ["to"] = "hand" }, preview);

    // ---- inventory.select ----

    private JsonObject InventorySelect(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        RequireChange(principal);
        var layer = Str(args, "layer") ?? HandSelection.PocketsLayer;
        int? slot = args.GetProperty("slot").ValueKind == JsonValueKind.Null ? null : args.GetProperty("slot").GetInt32();
        var rules = InventoryRules;
        if (layer == HandSelection.PocketsLayer)
        {
            if (rules == null) throw new Refusal("unsupported_capability", InventoryNotHere, "$.op");
            if (slot > rules.Pockets.Entries)
                throw new Refusal("invalid_args", $"Your pockets have {InventoryWords.Number(rules.Pockets.Entries)} slot{(rules.Pockets.Entries == 1 ? "" : "s")}.", "$.args.slot",
                    actual: JsonValue.Create(slot), allowed: JsonValue.Create(rules.Pockets.Entries));
        }
        if (IslandHeld) throw new Refusal("not_ready", TeamNotice);
        if (rules != null) Settle();
        var before = HandNow;
        var next = before.Choosing(layer, slot);
        var pockets = rules != null ? PocketContents : InventoryContents.Empty;
        _held.TryGetValue(PlayerAvatar, out var held);
        var chosen = next.Layer == HandSelection.PocketsLayer ? next.Pockets : null;
        // Decision 129: switching the bar's layers never touches what the hands carry, and casting with full hands is allowed. Only a choice on
        // the pockets' layer does: another slot there (or none), or the pockets brought down at a slot other than the one they kept. The thing
        // already in the hand from the chosen slot stays even then; anything else the hands carry goes back to the pockets first.
        var choosesPockets = next.Layer == HandSelection.PocketsLayer && (before.Layer == HandSelection.PocketsLayer || next.Pockets != before.Pockets);
        var keeps = held != null && (!choosesPockets || (chosen is { } at && pockets.ReservationAt(at) == held.Target));
        var bring = chosen is { } wanted && pockets.ItemAt(wanted) is { } there && there.StartsWith("obj:", StringComparison.Ordinal) ? pockets.Thing(there) : null;
        if (held != null && !keeps && rules != null)
        {
            // Refused in the pockets' words when there is no room for it: nothing changes.
            var kind = KindOfObject(rules, held.Item);
            var back = kind != null ? pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1) : pockets.WithThing(ThingFor(held.Item));
            CheckFits(back, rules, rules.Pockets, pockets: true, kind);
        }
        if (preview) return Previewed("inventory.select", principal, actionId);
        if (held != null && !keeps && rules != null) ReturnHeldToPockets(held, rules, fingerprint);
        if (bring != null && rules != null && OfThisIsland(bring) && !_held.ContainsKey(PlayerAvatar)) TryTakeIntoHand(bring, chosen!.Value, fingerprint, refuse: false);
        SaveHand(next);
        return Transient("inventory.select", principal, actionId, fingerprint, new JsonArray(), HandStatus());
    }

    /// <summary>An action id and a fingerprint of the host's own for a durable change inside another command (a select's store or take).</summary>
    private (string ActionId, string Fingerprint) InnerAction(string op, string fingerprint)
    {
        var id = $"inv-{_session}-{++_counter}";
        return (id, CanonicalJson.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(op + "|" + fingerprint + "|" + id)));
    }

    /// <summary>
    /// What the hands carry back into the pockets (a select moving the hand on): a unit of a kind to its stack, a unique thing to the slot
    /// reserved for it (it came from there) or the lowest free one; a durable inventory.store receipt under the host's own action id, as a
    /// store from the hand into the pockets. The caps were checked by the select.
    /// </summary>
    private void ReturnHeldToPockets(Held held, InventoryRules rules, string fingerprint)
    {
        var pockets = PocketContents;
        var kind = KindOfObject(rules, held.Item);
        var next = kind != null ? pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1) : pockets.WithThing(ThingFor(held.Item));
        var (inner, innerFingerprint) = InnerAction("inventory.store", fingerprint);
        SavePockets(next);
        try { LeaveIsland(PlayerAvatar, held, _chest, PlayerPrincipal, inner, innerFingerprint, Moved(kind?.Id ?? held.Target, 1, "pockets")); }
        catch (Refusal)
        {
            SavePockets(pockets, keepIfUnsaved: true);
            throw;
        }
    }

    /// <summary>
    /// A unique thing of this island from the pockets into the player's hand, its slot reserved (it comes back there): back on the island
    /// first (one room revision with a durable inventory.take receipt, under actionId, or the host's own for a select), then the pockets
    /// give, then the hand takes it (holding is never saved: after a reload it lies where it was last put down, as anything held does).
    /// refuse: throw the reason (inventory.take); otherwise a select just leaves it in the pockets. Returns the receipt, or null.
    /// </summary>
    private JsonObject? TryTakeIntoHand(InventoryThing thing, int slot, string fingerprint, bool refuse, string? actionId = null)
    {
        Refusal? why = null;
        var room = Room.Objects.FirstOrDefault(o => o.Id == thing.Id);
        var body = BodyOf(PlayerAvatar);
        if (body == null || !body.IsInsideTree()) why = new Refusal("not_ready", "The player is not in the room.", "$.args.to", retryable: true);
        else if (_held.ContainsKey(PlayerAvatar)) why = new Refusal("target_busy", "This avatar is already holding something. Put it down first.", "$.args.to");
        else if (room == null) why = new Refusal("request_invalid", OtherIsland, "$.args.item");
        else if (room.Asset.MassKg > SandboxRules.CarryLimitKg(PlayerAvatar))
        {
            var limit = SandboxRules.CarryLimitKg(PlayerAvatar);
            why = new Refusal("target_too_heavy", $"That is too heavy to carry: {Kilograms(room.Asset.MassKg)} kg, and this avatar can manage {Kilograms(limit)} kg.", "$.args.item",
                actual: JsonValue.Create(Kilograms(room.Asset.MassKg)), allowed: JsonValue.Create(Kilograms(limit)));
        }
        if (why != null)
        {
            if (refuse) throw why;
            return null;
        }
        var pockets = PocketContents;
        RequirePocketsFiled();
        var (inner, innerFingerprint) = actionId != null ? (actionId, fingerprint) : InnerAction("inventory.take", fingerprint);
        var candidate = Nature.Copy();
        candidate.Objects.Remove(thing.Id);
        JsonObject result;
        try { result = CommitInventory(candidate, _chest, "inventory.take", PlayerPrincipal, inner, innerFingerprint, new[] { thing.Id }, Moved(thing.Id, 1, "hand")); }
        catch (Refusal) when (!refuse) { return null; }
        // Back on the island: a pockets write lost now leaves it listed, and the next read lets that listing go (never lost).
        SavePockets(pockets.WithoutThing(thing.Id).Reserving(thing.Id, slot), keepIfUnsaved: true);
        TakeHold(PlayerAvatar, body!, room!, PoseOf(room!, ObjectPoses()));
        return result;
    }

    /// <summary>
    /// inventory.take from the pockets (P-Inventory-2, HOTBAR.md "the hand"): a unique thing of this island into the player's hand, anywhere,
    /// its slot reserved and then the selection. The player's alone (the pockets and the hand are; a companion is refused before anything
    /// is looked up, CompanionChestOnly). A stack stays in the pockets (its slot arms a unit); a thing of another island comes out there.
    /// </summary>
    private JsonObject TakeFromPockets(JsonElement args, InventoryRules rules, string principal, string actionId, string fingerprint, bool preview)
    {
        var item = Str(args, "item")!;
        if (Str(args, "to") != "hand") throw new Refusal("request_invalid", "From the pockets, a thing comes into the hand.", "$.args.to");
        var pockets = PocketContents;
        if (!item.StartsWith("obj:", StringComparison.Ordinal))
            throw pockets.Units(item) == 0 ? new Refusal("target_not_found", NotInPockets, "$.args.item") : new Refusal("request_invalid", StacksStayInPockets, "$.args.item");
        var thing = pockets.Thing(item) ?? throw new Refusal("target_not_found", NotInPockets, "$.args.item");
        if (!OfThisIsland(thing)) throw new Refusal("request_invalid", OtherIsland, "$.args.item");
        if (args.TryGetProperty("count", out var count) && count.GetInt32() != 1) throw new Refusal("invalid_args", AThingIsOne, "$.args.count");
        var slot = pockets.SlotOf(item) ?? pockets.FreeSlot();
        if (preview)
        {
            if (_held.ContainsKey(PlayerAvatar)) throw new Refusal("target_busy", "This avatar is already holding something. Put it down first.", "$.args.to");
            return Previewed("inventory.take", principal, actionId, item);
        }
        var result = TryTakeIntoHand(thing, slot, fingerprint, refuse: true, actionId)!;
        SaveHand(HandNow.Choosing(HandSelection.PocketsLayer, slot <= HandSelection.MaxSlot ? slot : null));
        return result;
    }

    // ---- the selected slot, for inventory.use and inventory.drop with no item ----

    /// <summary>What the selected pocket slot holds for a use or a drop that names no item: a kind or a thing's id, or the reason.</summary>
    private string SelectedItem(bool forUse)
    {
        var hand = HandNow;
        if (hand.Layer != HandSelection.PocketsLayer || hand.Pockets is not { } slot) throw new Refusal("request_invalid", ChooseAPocketSlot, "$.args.item");
        var pockets = PocketContents;
        if (pockets.ReservationAt(slot) is { } inHand && HeldBy(PlayerAvatar) == inHand)
            return forUse ? throw new Refusal("request_invalid", ThingHasNoUse, "$.args.item") : inHand;
        var item = pockets.ItemAt(slot) ?? throw new Refusal("target_not_found", EmptySlot, "$.args.item");
        if (forUse && item.StartsWith("obj:", StringComparison.Ordinal)) throw new Refusal("request_invalid", ThingHasNoUse, "$.args.item");
        return item;
    }

    // ---- the writes ----

    /// <summary>The hand's selection into the team file (a fixture without a team: this session); one that cannot be written lasts the session.</summary>
    private void SaveHand(HandSelection hand)
    {
        if (Team is { } team)
        {
            TeamFile updated;
            try { updated = team.WithHand(Mode, hand); }
            catch (TeamFileException) { return; }
            if (ReferenceEquals(updated, team)) return;
            try { _teamStore?.Save(updated); }
            catch (TeamFileException)
            {
                if (TeamNotice.Length == 0) TeamNotice = "The hand's selection could not be saved to the team file; it counts for this session.";
            }
            Team = updated;
            return;
        }
        _handUnfiled[Mode] = hand;
    }

    /// <summary>inventory.select's args (contracts: $defs/args/inventory.select): slot 1 to 10 or null, and the layer.</summary>
    private static void CheckSelectArgs(JsonElement args)
    {
        foreach (var property in args.EnumerateObject())
            if (property.Name is not ("slot" or "layer"))
                throw new Refusal("field_unknown", "This argument is not part of the operation.", "$.args." + KernelJson.DisplayText(property.Name, 64));
        if (!args.TryGetProperty("slot", out var slot)) throw new Refusal("request_invalid", "This operation needs 'slot'.", "$.args.slot");
        if (slot.ValueKind != JsonValueKind.Null && (slot.ValueKind != JsonValueKind.Number || !IsIntegerLiteral(slot) || slot.GetDouble() is < 1 or > HandSelection.MaxSlot))
            throw new Refusal("request_invalid", "slot is a whole number from 1 to 10, or null for none.", "$.args.slot");
        if (args.TryGetProperty("layer", out _) && Str(args, "layer") is not (HandSelection.PocketsLayer or HandSelection.AbilitiesLayer))
            throw new Refusal("request_invalid", "layer is pockets or abilities.", "$.args.layer");
    }
}
