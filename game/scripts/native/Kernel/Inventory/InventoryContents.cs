using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Kernel;

/// <summary>
/// A unique thing away from its island (P-Inventory, decision 99): a room object of one island, by its id, its room and the manifest
/// prefix of that island's save (the pockets travel with the team, a thing never leaves its island's room state), with its name
/// (untrusted display text, shown only) and its asset's mass. On its island its entity is kept in room state as removed (the nature
/// block's gathered mark) until it comes out with the same id.
/// </summary>
public sealed record InventoryThing(string Id, string RoomId, string ManifestPrefix, string Name, double MassKg);

/// <summary>
/// What the pockets or the chest hold: stacks by kind (units, 1 to 1000) and unique things, oldest first. Immutable: every change is a
/// new record. One entry per kind (a stack is capped at its kind's size) and one per thing. Since P-Inventory-2 every entry sits in a
/// slot (1 to MaxSlots: the pockets are the hotbar's ten, decision 119; the chest's slots are kept in order only): a new entry takes the
/// lowest free slot (or the one asked for, when free), an entry keeps its slot while it lasts, and a slot is reserved for a unique thing
/// taken out into the hand ("hand|" and its id), so it comes back there and nothing else takes it meanwhile. A reservation counts as an
/// entry against the slots and is never written to a file: the hand is never saved.
/// </summary>
public sealed record InventoryContents(IReadOnlyDictionary<string, int> Stacks, IReadOnlyList<InventoryThing> Things)
{
    public const int MaxUnits = 1000;
    public const int MaxNameLength = 80;
    /// <summary>The most slots any pockets have (the team file's bound; the pack's are fewer: v5's ten).</summary>
    public const int MaxSlots = 16;
    private const string HandPrefix = "hand|";
    private static readonly Regex Token = new(@"\A[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    private static readonly Regex ObjectId = new(@"\Aobj:[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    private static readonly Regex Prefix = new(@"\A[0-9a-f]{16}\z", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, int> NoSlots = new SortedDictionary<string, int>(StringComparer.Ordinal);

    public static InventoryContents Empty { get; } = new(new SortedDictionary<string, int>(StringComparer.Ordinal), Array.Empty<InventoryThing>());

    /// <summary>Each entry's slot, by its key (a kind's id for a stack, an obj: id for a thing, "hand|" and an id for a reservation).</summary>
    public IReadOnlyDictionary<string, int> Slots { get; init; } = NoSlots;

    public bool IsEmpty => Stacks.Count == 0 && Things.Count == 0;
    /// <summary>Whether a slot is reserved for a thing in the hand (kept in memory only: a file never holds one).</summary>
    public bool HasReservations => Slots.Keys.Any(k => k.StartsWith(HandPrefix, StringComparison.Ordinal));
    /// <summary>The slots taken: the stacks, the things, and a slot reserved for a thing in the hand.</summary>
    public int Entries => Stacks.Count + Things.Count + Slots.Keys.Count(k => k.StartsWith(HandPrefix, StringComparison.Ordinal));
    public int Units(string kind) => Stacks.GetValueOrDefault(kind);
    public InventoryThing? Thing(string id) => Things.FirstOrDefault(t => t.Id == id);

    // ---- the slots (P-Inventory-2: the hotbar) ----

    /// <summary>An entry's slot (a kind's id or a thing's obj: id), or null.</summary>
    public int? SlotOf(string item) => Slotted.Slots.TryGetValue(item, out var slot) ? slot : null;

    /// <summary>The slot reserved for a thing taken into the hand, or null.</summary>
    public int? ReservedFor(string id) => Slots.TryGetValue(HandPrefix + id, out var slot) ? slot : null;

    /// <summary>The thing a slot is reserved for (it is in the hand), or null.</summary>
    public string? ReservationAt(int slot) => Slots.FirstOrDefault(p => p.Value == slot && p.Key.StartsWith(HandPrefix, StringComparison.Ordinal)).Key?[HandPrefix.Length..];

    /// <summary>What sits in a slot: a kind's id, a thing's obj: id, or null (empty, or reserved for the hand: ReservationAt).</summary>
    public string? ItemAt(int slot) => Slotted.Slots.FirstOrDefault(p => p.Value == slot && !p.Key.StartsWith(HandPrefix, StringComparison.Ordinal)).Key;

    /// <summary>The lowest slot nothing takes or is reserved for, from 1.</summary>
    public int FreeSlot()
    {
        var taken = Slots.Values.ToHashSet();
        for (var slot = 1; ; slot++)
            if (!taken.Contains(slot)) return slot;
    }

    /// <summary>
    /// Every entry in a slot of its own: a slot naming nothing held goes (a reservation stays), an entry with none (an older file, a new
    /// entry) takes the lowest free one, stacks first in their kinds' order then things oldest first, and two entries never share one.
    /// </summary>
    public InventoryContents Slotted
    {
        get
        {
            var keys = Stacks.Keys.Concat(Things.Select(t => t.Id)).ToList();
            if (keys.All(Slots.ContainsKey) && Slots.Count(p => !p.Key.StartsWith(HandPrefix, StringComparison.Ordinal)) == keys.Count &&
                Slots.Values.Distinct().Count() == Slots.Count) return this;
            var slots = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var taken = new HashSet<int>();
            foreach (var (key, slot) in Slots.Where(p => p.Key.StartsWith(HandPrefix, StringComparison.Ordinal)).OrderBy(p => p.Value))
                if (slot >= 1 && taken.Add(slot)) slots[key] = slot;
            foreach (var key in keys)
                if (Slots.TryGetValue(key, out var slot) && slot >= 1 && taken.Add(slot)) slots[key] = slot;
            foreach (var key in keys.Where(k => !slots.ContainsKey(k)))
            {
                var free = 1;
                while (taken.Contains(free)) free++;
                taken.Add(free);
                slots[key] = free;
            }
            return this with { Slots = slots };
        }
    }

    private InventoryContents WithSlot(string key, int? wanted)
    {
        var slots = new SortedDictionary<string, int>(Slots.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        if (slots.ContainsKey(key)) return this;
        var taken = slots.Values.ToHashSet();
        var slot = wanted is { } asked && asked >= 1 && !taken.Contains(asked) ? asked : FreeSlot();
        slots[key] = slot;
        return this with { Slots = slots };
    }

    private InventoryContents WithoutSlots(Func<string, bool> gone)
    {
        if (!Slots.Keys.Any(gone)) return this;
        return this with { Slots = new SortedDictionary<string, int>(Slots.Where(p => !gone(p.Key)).ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) };
    }

    /// <summary>A slot kept for a thing taken into the hand (it comes back there); the thing itself is no longer in these contents.</summary>
    public InventoryContents Reserving(string id, int slot) => WithoutSlots(k => k == HandPrefix + id).WithSlot(HandPrefix + id, slot);

    /// <summary>A thing's reservation let go (it was put down, stored elsewhere, or is no longer held).</summary>
    public InventoryContents Unreserving(string id) => WithoutSlots(k => k == HandPrefix + id);

    /// <summary>Only the reservations the predicate keeps (a thing still in the player's hand).</summary>
    public InventoryContents KeepingReservations(Func<string, bool> keep) =>
        WithoutSlots(k => k.StartsWith(HandPrefix, StringComparison.Ordinal) && !keep(k[HandPrefix.Length..]));

    /// <summary>A kind's stack at this many units (0 takes the entry and its slot away); a new stack takes slot, when free, else the lowest free.</summary>
    public InventoryContents WithUnits(string kind, int units, int? slot = null)
    {
        var stacks = new SortedDictionary<string, int>(Stacks.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        if (units <= 0) stacks.Remove(kind);
        else stacks[kind] = units;
        var next = (Slotted with { Stacks = stacks });
        return units <= 0 ? next.WithoutSlots(k => k == kind) : next.WithSlot(kind, slot);
    }

    /// <summary>A thing in, in slot when free (else the lowest free; its own reservation, if it has one, first), replacing one of its id.</summary>
    public InventoryContents WithThing(InventoryThing thing, int? slot = null)
    {
        var reserved = ReservedFor(thing.Id);
        var next = (Slotted with { Things = Things.Where(t => t.Id != thing.Id).Append(thing).ToArray() }).Unreserving(thing.Id);
        return next.WithSlot(thing.Id, reserved ?? slot);
    }

    public InventoryContents WithoutThing(string id) => (Slotted with { Things = Things.Where(t => t.Id != id).ToArray() }).WithoutSlots(k => k == id);
    public InventoryContents WithoutStacks() => (Slotted with { Stacks = Empty.Stacks }).WithoutSlots(k => Stacks.ContainsKey(k));
    /// <summary>Only the things the predicate keeps (a reconciliation with the island's room state).</summary>
    public InventoryContents Keeping(Func<InventoryThing, bool> keep)
    {
        if (Things.All(keep)) return this;
        var gone = Things.Where(t => !keep(t)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return (Slotted with { Things = Things.Where(keep).ToArray() }).WithoutSlots(gone.Contains);
    }

    /// <summary>The mass of everything held: each stack's units at its kind's mass (a kind the pack no longer names weighs nothing), each thing its own.</summary>
    public double MassKg(InventoryRules? rules) =>
        Stacks.Sum(p => p.Value * (rules?.Kind(p.Key)?.MassKg ?? 0)) + Things.Sum(t => t.MassKg);

    public bool Same(InventoryContents other) =>
        ReferenceEquals(this, other) || (Stacks.Count == other.Stacks.Count && Stacks.All(p => other.Stacks.TryGetValue(p.Key, out var n) && n == p.Value) &&
                                         Things.SequenceEqual(other.Things) && SameSlots(Slotted.Slots, other.Slotted.Slots));

    private static bool SameSlots(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var slot) && slot == p.Value);

    // ---- the chest in the island save's camp block (room state's x_play_camp.chest): stacks and the ids of this island's things ----

    /// <summary>The chest block, or null when it is empty (an empty chest writes no key).</summary>
    public JsonObject? ChestBlock()
    {
        if (IsEmpty) return null;
        var block = new JsonObject();
        if (Stacks.Count > 0) block["stacks"] = new JsonObject(Stacks.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
        if (Things.Count > 0) block["things"] = new JsonArray(Things.Select(t => (JsonNode?)t.Id).ToArray());
        return block;
    }

    /// <summary>What is wrong with a saved chest block (or null): its stacks and its things' ids, as contracts/room-state.schema.json $defs/chest says.</summary>
    public static string? ChestProblem(JsonNode? node)
    {
        if (node is not JsonObject block || block.Count == 0 || block.Any(p => p.Key is not ("stacks" or "things"))) return "the chest";
        if (block["stacks"] is { } stacks && StacksProblem(stacks) != null) return "the chest's stacks";
        if (block["things"] is { } things && (things is not JsonArray list || list.Count is < 1 or > 64 ||
            list.Any(id => id?.GetValueKind() != JsonValueKind.String || !ObjectId.IsMatch(id.GetValue<string>())) ||
            list.Select(id => id!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != list.Count)) return "the chest's things";
        return null;
    }

    /// <summary>A chest block already checked (ChestProblem), its things named for this island; null reads as an empty chest.</summary>
    public static InventoryContents FromChest(JsonObject? block, string roomId, string prefix, Func<string, (string Name, double MassKg)> describe)
    {
        if (block == null) return Empty;
        var stacks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (block["stacks"] is JsonObject saved) foreach (var (kind, count) in saved) stacks[kind] = (int)count!.GetValue<double>();
        var things = block["things"] is JsonArray ids
            ? ids.Select(id => id!.GetValue<string>()).Select(id => { var (name, mass) = describe(id); return new InventoryThing(id, roomId, prefix, name, mass); }).ToArray()
            : Array.Empty<InventoryThing>();
        return new InventoryContents(stacks, things);
    }

    private static string? StacksProblem(JsonNode stacks)
    {
        if (stacks is not JsonObject map || map.Count is < 1 or > 16) return "stacks";
        foreach (var (kind, count) in map)
            if (!Token.IsMatch(kind) || count?.GetValueKind() != JsonValueKind.Number || count.GetValue<double>() is var n && (n < 1 || n > MaxUnits || n != Math.Floor(n))) return "stacks";
        return null;
    }

    // ---- one mode's pockets in the team file (contracts/team.schema.json $defs/pockets) ----

    /// <summary>
    /// The pockets as the team file keeps them, or null when they hold nothing (a mode with nothing writes no key). Since P-Inventory-2
    /// each entry's slot (stack_slots, a thing's slot), so the hotbar's layout survives a reload; a reservation for the hand never.
    /// </summary>
    public JsonObject? PocketsBlock()
    {
        if (IsEmpty) return null;
        var slotted = Slotted;
        var block = new JsonObject();
        if (Stacks.Count > 0)
        {
            block["stacks"] = new JsonObject(Stacks.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
            block["stack_slots"] = new JsonObject(Stacks.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => KeyValuePair.Create(k, (JsonNode?)slotted.Slots[k])));
        }
        if (Things.Count > 0)
            block["things"] = new JsonArray(Things.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id, ["room_id"] = t.RoomId, ["manifest_prefix"] = t.ManifestPrefix, ["name"] = t.Name, ["mass_kg"] = Math.Round(t.MassKg, 6),
                ["slot"] = slotted.Slots[t.Id],
            }).ToArray());
        return block;
    }

    /// <summary>One mode's pockets read strictly (null: what is wrong is in the message); the team file refuses the whole file for any problem.</summary>
    public static InventoryContents? FromPockets(JsonElement block, out string problem)
    {
        problem = "";
        if (block.ValueKind != JsonValueKind.Object || !block.EnumerateObject().Any() || block.EnumerateObject().Any(p => p.Name is not ("stacks" or "things" or "stack_slots"))) { problem = "pockets"; return null; }
        var stacks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var things = new List<InventoryThing>();
        var slots = new SortedDictionary<string, int>(StringComparer.Ordinal);
        static bool SlotNumber(JsonElement value, out int slot)
        {
            slot = 0;
            return value.ValueKind == JsonValueKind.Number && value.GetRawText().All(char.IsAsciiDigit) && value.TryGetInt32(out slot) && slot is >= 1 and <= MaxSlots;
        }
        if (block.TryGetProperty("stacks", out var saved))
        {
            if (saved.ValueKind != JsonValueKind.Object || saved.EnumerateObject().Count() is < 1 or > 16) { problem = "stacks"; return null; }
            foreach (var entry in saved.EnumerateObject())
            {
                if (!Token.IsMatch(entry.Name) || entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.GetRawText().All(char.IsAsciiDigit) ||
                    !entry.Value.TryGetInt32(out var units) || units is < 1 or > MaxUnits || !stacks.TryAdd(entry.Name, units)) { problem = "stacks"; return null; }
            }
        }
        if (block.TryGetProperty("things", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 16) { problem = "things"; return null; }
            foreach (var item in list.EnumerateArray())
            {
                // P-Inventory-2: a thing may name its slot (a file from before has none: Slotted gives it one).
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != (item.TryGetProperty("slot", out _) ? 6 : 5) ||
                    item.EnumerateObject().Any(p => p.Name is not ("id" or "room_id" or "manifest_prefix" or "name" or "mass_kg" or "slot"))) { problem = "a thing"; return null; }
                if (item.TryGetProperty("slot", out var thingSlot))
                {
                    if (!SlotNumber(thingSlot, out var at) || slots.Values.Contains(at)) { problem = "a thing's slot"; return null; }
                    slots[item.GetProperty("id").GetString() ?? ""] = at;
                }
                var id = item.GetProperty("id");
                var room = item.GetProperty("room_id");
                var prefix = item.GetProperty("manifest_prefix");
                var name = item.GetProperty("name");
                var mass = item.GetProperty("mass_kg");
                if (id.ValueKind != JsonValueKind.String || !ObjectId.IsMatch(id.GetString()!) || room.ValueKind != JsonValueKind.String || !Token.IsMatch(room.GetString()!) ||
                    prefix.ValueKind != JsonValueKind.String || !Prefix.IsMatch(prefix.GetString()!) || name.ValueKind != JsonValueKind.String || !IsName(name.GetString()!) ||
                    mass.ValueKind != JsonValueKind.Number || !double.IsFinite(mass.GetDouble()) || mass.GetDouble() is <= 0 or > 10) { problem = "a thing"; return null; }
                things.Add(new InventoryThing(id.GetString()!, room.GetString()!, prefix.GetString()!, name.GetString()!, mass.GetDouble()));
            }
        }
        if (stacks.Count + things.Count > 16 || things.Select(t => (t.RoomId, t.ManifestPrefix, t.Id)).Distinct().Count() != things.Count) { problem = "entries"; return null; }
        if (block.TryGetProperty("stack_slots", out var stackSlots))
        {
            if (stackSlots.ValueKind != JsonValueKind.Object) { problem = "stack slots"; return null; }
            foreach (var entry in stackSlots.EnumerateObject())
                if (!stacks.ContainsKey(entry.Name) || slots.ContainsKey(entry.Name) || !SlotNumber(entry.Value, out var at) || slots.Values.Contains(at)) { problem = "stack slots"; return null; }
                else slots[entry.Name] = at;
        }
        return new InventoryContents(stacks, things) { Slots = slots }.Slotted;
    }

    /// <summary>A thing's name as it is kept: untrusted single-line display text, 1 to 80 code points, nothing hidden.</summary>
    /// <summary>A kind's id as the pack names it (a lowercase token), and a unique thing's (a room object's id).</summary>
    public static bool IsKindId(string id) => Token.IsMatch(id);
    public static bool IsThingId(string id) => ObjectId.IsMatch(id);

    public static bool IsName(string text) => text.Length > 0 && KernelText.CodePoints(text).Length <= MaxNameLength && !KernelText.HasHidden(text);
}

/// <summary>
/// The hand's selection in one mode (P-Inventory-2; docs/game/MECHANICS.md decision 119, docs/game/HOTBAR.md; inventory.select): which of
/// the hotbar's two layers is in the hand, and each layer's selected slot (1 to 10, or none). The pockets' layer's slot is what the hand
/// holds (a stack's unit armed, or a unique thing taken into the hand); the abilities' layer is the HUD's, and the kernel only records
/// it. Kept per mode in the team file, so a reload keeps the hand; the player's alone, never a companion's.
/// </summary>
public sealed record HandSelection(string Layer, int? Pockets, int? Abilities)
{
    public const string PocketsLayer = "pockets", AbilitiesLayer = "abilities";
    public const int MaxSlot = 10;

    /// <summary>A hand never chosen: the magic layer is down, as the HUD reads it (the hotbar's change request, 25 October), nothing in either layer.</summary>
    public static HandSelection None { get; } = new(AbilitiesLayer, null, null);

    /// <summary>The selected slot of the layer in the hand, or null.</summary>
    public int? Slot => Layer == AbilitiesLayer ? Abilities : Pockets;

    public bool IsNone => Layer == AbilitiesLayer && Pockets == null && Abilities == null;

    public bool Valid => Layer is PocketsLayer or AbilitiesLayer && Pockets is null or >= 1 and <= MaxSlot && Abilities is null or >= 1 and <= MaxSlot;

    /// <summary>The selection with one layer's slot chosen (or none) and that layer in the hand.</summary>
    public HandSelection Choosing(string layer, int? slot) => layer == AbilitiesLayer ? this with { Layer = layer, Abilities = slot } : this with { Layer = PocketsLayer, Pockets = slot };

    /// <summary>As the team file keeps it: the layer, and each layer's slot when one is chosen.</summary>
    public JsonObject Block()
    {
        var block = new JsonObject { ["layer"] = Layer };
        if (Pockets is { } pockets) block["pockets"] = pockets;
        if (Abilities is { } abilities) block["abilities"] = abilities;
        return block;
    }

    /// <summary>One mode's selection read strictly, or null when anything is wrong.</summary>
    public static HandSelection? FromBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object || block.EnumerateObject().Any(p => p.Name is not ("layer" or "pockets" or "abilities")) ||
            !block.TryGetProperty("layer", out var layer) || layer.ValueKind != JsonValueKind.String) return null;
        int? Slot(string name)
        {
            if (!block.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.Number && value.GetRawText().All(char.IsAsciiDigit) && value.TryGetInt32(out var slot) ? slot : -1;
        }
        var selection = new HandSelection(layer.GetString()!, Slot("pockets"), Slot("abilities"));
        return selection.Valid && !selection.IsNone ? selection : null;
    }
}
