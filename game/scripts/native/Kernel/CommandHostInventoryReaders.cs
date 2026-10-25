using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>What the inventory's E would do here, or the reason it cannot (the camps' RestOutcome style): to is chest or pockets.</summary>
public sealed record InventoryOutcome(bool Ok, string Words, string? To = null);

/// <summary>
/// One entry as the HUD and player.status show it: the item (a kind's id or a thing's object id), stack or thing, its name (untrusted
/// display text: show it, never read it as anything else), the units (a thing is one), the mass of the entry in kilograms (data, never a
/// cap: decision 120), the quarters of a heart one unit heals (null: no use yet, or a thing), whether it is on this island (false only for
/// a thing in the pockets from another island, which comes out there alone), and the kind's material (a log wood, a stone stone: what a
/// repair spends, decision 116; null for food, seeds and things).
/// </summary>
public sealed record InventoryEntryView(string Item, string Kind, string Name, int Count, double MassKg, int? HealQuarters, bool Here, string? Material = null);

/// <summary>
/// The inventory's readers and the player's keys (P-Inventory): what the pockets and the chest hold for the HUD and player.status, the
/// caps, E's choice (PlayerCanStore, PlayerCanTake, in the camps' PlayerCanRest style), the player's commands as the HUD sends them, the
/// capabilities.list rows, and the loss on a knockout and the deep-water return (decision 101).
/// </summary>
public partial class CommandHost
{
    /// <summary>The pockets' entries now: stacks in the pack's kinds' order, then the things, oldest first.</summary>
    public IReadOnlyList<InventoryEntryView> PocketEntries => Views(PocketContents, pockets: true);

    /// <summary>The chest's entries now (the island's one chest, whichever campfire is near).</summary>
    public IReadOnlyList<InventoryEntryView> ChestEntries => Views(ChestContents, pockets: false);

    /// <summary>The pockets' and the chest's slots (decision 120: counts; masses are each entry's data, never a cap).</summary>
    public InventoryCaps? PocketCaps => InventoryRules?.Pockets;
    public InventoryCaps? ChestCaps => InventoryRules?.Chest;

    /// <summary>Whether the chest is within reach now: the player stands at a camp (any lit campfire, or the starting one).</summary>
    public bool ChestReachable => HasInventory && PlayerAtCamp();

    private IReadOnlyList<InventoryEntryView> Views(InventoryContents contents, bool pockets)
    {
        var rules = InventoryRules;
        var order = rules?.Kinds.Select(k => k.Id).ToList() ?? new List<string>();
        var list = new List<InventoryEntryView>();
        foreach (var (kind, count) in contents.Stacks.OrderBy(p => order.IndexOf(p.Key) is var i && i >= 0 ? i : int.MaxValue).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var known = rules?.Kind(kind);
            var name = KernelJson.DisplayText(known?.Words(count) ?? kind, InventoryContents.MaxNameLength);
            list.Add(new InventoryEntryView(kind, "stack", name, count, Math.Round(count * (known?.MassKg ?? 0), 4), known?.HealQuarters, true, known?.Material));
        }
        foreach (var thing in contents.Things)
            list.Add(new InventoryEntryView(thing.Id, "thing", KernelJson.DisplayText(thing.Name, InventoryContents.MaxNameLength), 1, Math.Round(thing.MassKg, 4), null,
                !pockets || OfThisIsland(thing)));
        return list;
    }

    // ---- E's choice (decision 113) and the player's commands ----

    /// <summary>
    /// What E would do with the thing in the player's hand, by the inventory (decision 113), without doing it: at a camp, store it in the
    /// chest first (when it fits); else into the pockets, anywhere (when they have room): a unit of food or seeds joins its stack, a unique
    /// thing takes an entry of its own (decision 99); otherwise not this, and the words say why (the HUD's E then gathers a log or stone,
    /// or sets the thing down, as built). Ok carries where it goes and words for the prompt; a refusal carries the reason PlayerStore would
    /// answer (the pockets' when neither fits).
    /// </summary>
    public InventoryOutcome PlayerCanStore()
    {
        if (InventoryRules is not { } rules) return new(false, InventoryNotHere);
        if (!_held.TryGetValue(PlayerAvatar, out var held)) return new(false, HoldSomething);
        var name = HeldName(PlayerAvatar);
        var kind = KindOfObject(rules, held.Item);
        if (PlayerAtCamp())
        {
            var chest = ChestContents;
            var filled = kind != null ? chest.WithUnits(kind.Id, chest.Units(kind.Id) + 1) : chest.WithThing(ThingFor(held.Item));
            if (FitsWords(filled, rules, rules.Chest, pockets: false, kind) == null) return new(true, $"Store {name} in the chest", "chest");
        }
        var pockets = PocketContents;
        var into = kind != null ? pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1) : pockets.WithThing(ThingFor(held.Item));
        if (FitsWords(into, rules, rules.Pockets, pockets: true, kind) is { } words) return new(false, words);
        return new(true, $"Put {name} in your pockets", "pockets");
    }

    /// <summary>
    /// Whether the chest can be opened here, or one entry taken out now (item, into the hand for a thing, the pockets for a stack), with
    /// words, without doing it. The chest needs a camp; a thing needs empty hands within the carry limit; a stack room in the pockets.
    /// </summary>
    public InventoryOutcome PlayerCanTake(string? item = null)
    {
        if (InventoryRules is not { } rules) return new(false, InventoryNotHere);
        if (!PlayerAtCamp()) return new(false, ChestAtCamp);
        var chest = ChestContents;
        if (item == null) return chest.IsEmpty ? new(false, "The chest is empty.") : new(true, "Open the chest", "chest");
        if (item.StartsWith("obj:", StringComparison.Ordinal))
        {
            if (chest.Thing(item) is not { } thing) return new(false, NotInChest);
            if (_held.ContainsKey(PlayerAvatar)) return new(false, "Your hands are full: put down what you hold first.");
            var mass = Room.Objects.FirstOrDefault(o => o.Id == item)?.Asset.MassKg ?? float.MaxValue;
            if (mass > Sandbox.SandboxRules.CarryLimitKg(PlayerAvatar)) return new(false, "That is too heavy for you to carry: the Gubble could.");
            return new(true, $"Take {KernelJson.DisplayText(thing.Name, InventoryContents.MaxNameLength)}", "hand");
        }
        var have = chest.Units(item);
        if (have == 0) return new(false, NotInChest);
        var pockets = PocketContents;
        var kind = rules.Kind(item);
        if (FitsWords(pockets.WithUnits(item, pockets.Units(item) + 1), rules, rules.Pockets, pockets: true, kind) is { } words) return new(false, words);
        return new(true, $"Take {kind?.Words(have) ?? item}", "pockets");
    }

    /// <summary>The caps' refusal words for a change (CheckFits's), or null when it fits.</summary>
    private static string? FitsWords(InventoryContents next, InventoryRules rules, InventoryCaps caps, bool pockets, InventoryKind? kind)
    {
        try { CheckFits(next, rules, caps, pockets, kind); return null; }
        catch (Refusal refusal) { return refusal.Message; }
    }

    /// <summary>E's store: what PlayerCanStore chose (to the chest at a camp, else the held thing to the pockets), as the player's command.</summary>
    public JsonObject PlayerStore(bool preview = false)
    {
        var to = PlayerCanStore().To ?? (PlayerAtCamp() ? "chest" : "pockets");
        return PlayerCommand("inventory.store", new JsonObject { ["to"] = to }, preview);
    }

    /// <summary>A pockets entry (count units of a stack, or all) into the chest, at a camp.</summary>
    public JsonObject PlayerStoreFromPockets(string item, int? count = null, bool preview = false)
    {
        var args = new JsonObject { ["from"] = "pockets", ["to"] = "chest", ["item"] = item };
        if (count != null) args["count"] = count.Value;
        return PlayerCommand("inventory.store", args, preview);
    }

    /// <summary>A chest entry out: a thing into the hand (or the pockets), a stack's units into the pockets.</summary>
    public JsonObject PlayerTake(string item, string? to = null, int? count = null, bool preview = false)
    {
        var args = new JsonObject { ["item"] = item, ["to"] = to ?? (item.StartsWith("obj:", StringComparison.Ordinal) ? "hand" : "pockets") };
        if (count != null) args["count"] = count.Value;
        return PlayerCommand("inventory.take", args, preview);
    }

    /// <summary>One unit of a stack in the pockets used (an apple eaten).</summary>
    public JsonObject PlayerUse(string item, bool preview = false) => PlayerCommand("inventory.use", new JsonObject { ["item"] = item }, preview);

    /// <summary>A thing from the pockets set down before the player (its pose is a put-down's receipt: a full ledger is compacted first, as the hand keys do).</summary>
    public JsonObject PlayerDrop(string item) => PlayerSandbox("inventory.drop", new JsonObject { ["item"] = item });

    // ---- the readers on the command surface ----

    /// <summary>player.status's pockets and chest (read-only; names untrusted display text), when the pack has an inventory block.</summary>
    private void AddInventoryStatus(JsonObject data)
    {
        if (InventoryRules is not { } rules) return;
        data["pockets"] = InventoryStatus(PocketEntries, rules.Pockets, pockets: true);
        data["chest"] = InventoryStatus(ChestEntries, rules.Chest, pockets: false);
    }

    private static JsonObject InventoryStatus(IReadOnlyList<InventoryEntryView> entries, InventoryCaps caps, bool pockets) => new()
    {
        ["entries"] = new JsonArray(entries.Take(64).Select(e =>
        {
            var entry = new JsonObject { ["item"] = e.Item, ["kind"] = e.Kind, ["name"] = e.Name, ["count"] = Math.Clamp(e.Count, 1, InventoryContents.MaxUnits), ["mass_kg"] = Math.Clamp(e.MassKg, 0, 50) };
            if (e.HealQuarters is { } heal) entry["heal_quarters"] = heal;
            if (e.Material is { } material) entry["material"] = material;
            if (pockets && e.Kind == "thing") entry["here"] = e.Here;
            return (JsonNode?)entry;
        }).ToArray()),
        ["max_entries"] = caps.Entries,
    };

    /// <summary>
    /// capabilities.list's inventory: the four ops, which are the player's alone, and whether the Gubble may use the chest now. On the
    /// whole list's first answer only (no category, no cursor): the ops are no ability of a category, and a page after the first repeats nothing.
    /// </summary>
    private JsonObject WithInventoryOps(JsonObject data, System.Text.Json.JsonElement args)
    {
        if (InventoryRules == null || args.TryGetProperty("category", out _) || args.TryGetProperty("cursor", out _)) return data;
        data["inventory"] = new JsonObject
        {
            ["gubble_chest"] = GubbleChestAllowed,
            ["ops"] = new JsonArray(new[] { "inventory.store", "inventory.take", "inventory.use", "inventory.drop" }
                .Select(op => (JsonNode?)new JsonObject { ["op"] = op, ["player_only"] = PlayerOnlyOps.Contains(op) }).ToArray()),
        };
        return data;
    }

    // ---- the loss (decision 101) ----

    /// <summary>
    /// A knockout (sea false) or the deep-water return (sea true) in Challenge and Live: the pockets' stacks are lost, unique things stay,
    /// and the journal says so in the game's words. Creative loses nothing; the chest never spills. The loss stands for the session even
    /// when the team file cannot be written.
    /// </summary>
    private void LosePocketStacks(bool sea)
    {
        if (Mode == "creative" || InventoryRules == null || Room == null) return;
        var pockets = PocketContents;
        if (pockets.Stacks.Count == 0) return;
        var lost = string.Join(", ", Views(pockets, pockets: true).Where(e => e.Kind == "stack").Select(e => $"{e.Count} {e.Name}"));
        SavePockets(pockets.WithoutStacks(), keepIfUnsaved: true);
        var line = (sea ? "The sea took the food and seeds from your pockets: " : "You were knocked out and lost the food and seeds from your pockets: ") + lost + "." +
                   (pockets.Things.Count > 0 ? " Everything else stayed in your pockets." : "");
        AddHistory(Fact("consequence", line, PlayerPrincipal, PlayerPrincipal, null, null, Player != null && IsInstanceValid(Player) ? Player.GlobalPosition : null));
        SaveTeam();
    }

    private int _shoreReturnsSeen;

    /// <summary>Test seam: a creation scorched as fire leaves one (its repair quote from its hash-pinned design), for the repair's cost.</summary>
    internal void ScorchForTests(string id)
    {
        var state = new NatureObject { Id = id, Creation = true, Burn = 0.5f };
        QuoteRepair(state);
        Nature.Objects[id] = state;
        ApplyNatureObjects();
    }

    /// <summary>Test seam: the deep-water return's loss, as the body's shore return calls it (no water in the test room to swim out of).</summary>
    internal void DeepWaterReturnForTests() => LosePocketStacks(sea: true);

    /// <summary>The body went home (B, the far net or the deep-water return): only the deep-water return, an empty bar in deep water, loses the stacks.</summary>
    private void PlayerWentHome(SmallPlayerController body)
    {
        if (body.ShoreReturns == _shoreReturnsSeen) return;
        _shoreReturnsSeen = body.ShoreReturns;
        LosePocketStacks(sea: true);
    }
}
