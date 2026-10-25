using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// The pockets and the chest on screen (MECHANICS.md decisions 87, 98 to 103, 108 and 113; docs/game/INVENTORY.md), written from what the
/// host already says and nothing else: its readers (PocketEntries, ChestEntries, the caps, the hand, the Gubble's chest switch) for the
/// words, and the player's own commands (inventory.use, drop, store, take, and the hotbar's use and drop of the selected slot) for the
/// buttons and keys, each answered in the host's words. The camp's Inventory page uses CampLines, the hotbar (RoomHud.Hotbar.cs, which
/// retired the Tab panel) takes what a unit does and its use's words from here, and E's notices (RoomHud.Interact.cs) take theirs too. Names are untrusted display text: they go into labels as plain text and
/// are never read as anything else. No rule is decided here: a button the host would refuse is pressed anyway and its refusal is the answer.
/// </summary>
public static class InventoryPages
{
    /// <summary>A button's target: "inv|verb|item|to|count" (an item is a kind's id or a thing's "obj:" id, which holds a colon).</summary>
    public const string Prefix = "inv|";
    public const string Eat = "eat", Drop = "drop", Store = "store", Take = "take";
    /// <summary>A count of "all" moves every unit; a number moves that many.</summary>
    public const string All = "all";

    /// <summary>The target of a button: verb eat, drop, store (pockets to the chest) or take (chest to the hand or the pockets).</summary>
    public static string Target(string verb, string item, string to = "", string count = "") => string.Join('|', "inv", verb, item, to, count);

    // ---- the words ----

    /// <summary>A mass as the game says it: the host's rounding, no trailing zeros ("0.3", "0.06").</summary>
    public static string Kg(double kg) => kg.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>"3 of 10 things": what a store holds against its slots (decision 120: counts, not weights).</summary>
    public static string CapsWords(int entries, InventoryCaps caps) =>
        $"{entries} of {caps.Entries} {(caps.Entries == 1 ? "thing" : "things")}";

    public static string PocketsCaps(CommandHost host) =>
        host.PocketCaps is { } caps ? CapsWords(host.PocketEntries.Count, caps) : "";

    public static string ChestCaps(CommandHost host) =>
        host.ChestCaps is { } caps ? CapsWords(host.ChestEntries.Count, caps) : "";

    /// <summary>
    /// The logs and stones (the stacks of kinds with a material: what repairs spend, decision 116), as "3 logs · 1 stone" in the pack's
    /// words, or "none yet". The materials wallet is retired (P-Inventory-2): these are the stacks, the pockets' and the chest's together.
    /// </summary>
    public static string MaterialStacks(CommandHost host, bool chestToo = true)
    {
        var entries = chestToo ? host.PocketEntries.Concat(host.ChestEntries) : host.PocketEntries;
        var parts = entries.Where(e => e.Kind == "stack" && e.Material != null).GroupBy(e => e.Item).Select(g =>
        {
            var count = g.Sum(e => e.Count);
            var name = host.InventoryRules?.Kind(g.Key) is { } kind ? KernelJson.DisplayText(kind.Words(count), 60) : g.First().Name;
            return $"{count} {name.ToLowerInvariant()}";
        }).ToList();
        return parts.Count == 0 ? "none yet" : string.Join(" · ", parts);
    }

    /// <summary>What a heal of so many quarters of a heart is, in words ("half a heart"), in the island's word for hearts.</summary>
    public static string Heal(CommandHost host, int quarters)
    {
        var heart = MeterWords.One(host, "hearts");
        return quarters switch
        {
            1 => $"a quarter of a {heart}",
            2 => $"half a {heart}",
            3 => $"three quarters of a {heart}",
            4 => $"a whole {heart}",
            _ when quarters > 4 && quarters % 4 == 0 => $"{quarters / 4} {MeterWords.Lower(host, "hearts")}",
            _ => $"{quarters} quarters of a {heart}",
        };
    }

    /// <summary>One entry as a line: its name (and count, for a stack), its mass, what a unit heals, and a thing from another island.</summary>
    public static string EntryWords(CommandHost host, InventoryEntryView entry)
    {
        var words = entry.Kind == "stack" ? $"{entry.Name} ×{entry.Count}" : entry.Name;
        words += " · " + Kg(entry.MassKg) + " kg";
        if (entry.HealQuarters is { } heal) words += $" · heals {Heal(host, heal)} each";
        if (!entry.Here) words += " · from another island";
        return words;
    }

    /// <summary>A kind's one unit as a sentence says it: "an apple", "berries" (a kind whose one-word and plural are one). From the pack's untrusted words.</summary>
    public static string UnitWords(CommandHost host, string item)
    {
        if (host.InventoryRules?.Kind(item) is not { } kind) return "it";
        var name = KernelJson.DisplayText(kind.Name, 40).ToLowerInvariant();
        if (kind.Plural == kind.Name || name.Length == 0) return name;
        return ("aeiou".Contains(name[0]) ? "an " : "a ") + name;
    }

    private static string Counted(CommandHost host, string item, int count) =>
        host.InventoryRules?.Kind(item) is { } kind ? $"{count} {KernelJson.DisplayText(kind.Words(count), 40)}" : $"{count}";

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;

    /// <summary>The host's refusal, as it words it (written for a child); a fallback only when it says nothing.</summary>
    public static string Failed(JsonObject result, string fallback) =>
        result["error"]?["message"]?.GetValue<string>() is { Length: > 0 } words ? words : fallback;

    // ---- what E says (RoomHud.Interact.cs) ----

    /// <summary>After E picked food: "You picked an apple. In your pockets: 3 apples."; the pick's words came from PlayerCanPick.</summary>
    public static string PickedWords(CommandHost host, FoodOutcome pick)
    {
        var picked = pick.Words.StartsWith("Pick ", StringComparison.Ordinal) ? "You picked " + pick.Words["Pick ".Length..] : "You picked it";
        return host.PocketEntries.FirstOrDefault(e => e.Item == pick.Kind) is { } have
            ? $"{picked}. In your pockets: {have.Count} {have.Name.ToLowerInvariant()}."
            : picked + ".";
    }

    /// <summary>After E put the held thing away: where it went.</summary>
    public static string StoredWords(string name, string? to) => to == "chest" ? $"{name} is in the chest now." : $"{name} is in your pockets now.";

    // ---- the buttons ----

    /// <summary>Run a button's target as the player's own command and return the words it answered with (the host's, if it refused).</summary>
    public static string Run(CommandHost host, string target)
    {
        var part = target.Split('|');
        if (part.Length != 5 || part[0] != "inv") return "";
        var (verb, item, to, count) = (part[1], part[2], part[3], part[4]);
        int? units = count.Length > 0 && count != All && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        return verb switch
        {
            Eat => EatWords(host, item),
            Drop => DropWords(host, item),
            Store => StoreWords(host, item, units),
            Take => TakeWords(host, item, to, units),
            _ => "",
        };
    }

    /// <summary>inventory.use on one unit of a stack: what healed, or in Creative the sound's receipt (decisions 108 and 111), or the host's refusal.</summary>
    public static string EatWords(CommandHost host, string item) => EatenWords(host, item, host.PlayerUse(item), "That could not be eaten.");

    /// <summary>
    /// The hotbar's armed unit used (decision 119; HOTBAR.md "The hand"), by the kernel's calls on the selected slot: a log or stone (a kind
    /// with a material) is set down loose before the player (inventory.drop), anything else is used (inventory.use: food eaten, a seed refused
    /// in the kernel's words). The words say what happened, or are the host's refusal.
    /// </summary>
    public static string UseSelectedWords(CommandHost host, string item)
    {
        if (host.InventoryRules?.Kind(item) is { Material: not null }) return DropSelectedWords(host, item);
        return EatenWords(host, item, host.PlayerUseSelected(), "That has no use yet.");
    }

    /// <summary>
    /// The hotbar's armed unit dropped (F, decision 130: F drops a food unit as it drops a thing; E on a log or stone sets it down the same way):
    /// the kernel's inventory.drop naming no item, so one unit of the selected slot lies loose on the ground before the player. The words say
    /// what was set down, or are the host's refusal (no ground ahead).
    /// </summary>
    public static string DropSelectedWords(CommandHost host, string item)
    {
        var unit = UnitWords(host, item);
        var dropped = host.PlayerDropSelected();
        return Ok(dropped) ? $"You set {unit} down." : Failed(dropped, "That could not be set down.");
    }

    /// <summary>What a unit of a stack does, in a word or two, for its slot on the bar: food is eaten, a log or stone set down, anything else kept.</summary>
    public static string UnitDoes(InventoryEntryView entry) => entry.HealQuarters != null ? "eat" : entry.Material != null ? "set down" : "keep";

    /// <summary>What a unit does, for the bar's caption: eaten and what it heals (nothing in Creative), set down, or nothing yet; e is E's key now, click the drag button's.</summary>
    public static string UnitCaption(CommandHost host, InventoryEntryView entry, string e, string click)
    {
        if (entry.HealQuarters is { } heal)
            return $"{e} or a {click} eats one: " + (host.Mode == "creative" ? "nothing heals in Creative" : $"heals {Heal(host, heal)}");
        if (entry.Material != null) return $"{e} or a {click} sets one down";
        return "nothing to do with one yet";
    }

    /// <summary>The thing the bar took into the hand: what E and F do with it now (E stores it in the chest at a camp, else sets it down; F drops it).</summary>
    public static string HandThingWords(CommandHost host)
    {
        var e = PlayerControls.KeyLabel(Act.Interact);
        return (host.PlayerAtCamp() ? $"{e} stores it in the chest" : $"{e} sets it down") + $", {PlayerControls.KeyLabel(Act.Hands)} drops it";
    }

    /// <summary>The bar's selection in the pockets, as the camp's Inventory page says it (decision 87 with the hotbar: it shows the selection).</summary>
    public static string SelectionWords(CommandHost host)
    {
        var tab = PlayerControls.KeyLabel(Act.BarLayer);
        var hand = host.Hand;
        if (host.HandNow.IsNone || hand.Layer != HandSelection.PocketsLayer) return $"On the bar: the Gubble's magic is down ({tab} brings your pockets down).";
        if (hand.PocketsSlot is not { } slot) return "On the bar: no pocket slot chosen.";
        var entry = host.PocketEntries.FirstOrDefault(e => e.Slot == slot);
        return hand.Holds switch
        {
            "unit" when entry != null => $"On the bar: slot {slot}, {entry.Name} ×{entry.Count}, one ready to use.",
            "thing" => $"On the bar: slot {slot}, {KernelJson.DisplayText(host.HeldName(CommandHost.PlayerAvatar), 60)}, in your hand.",
            "pocketed" when entry != null => $"On the bar: slot {slot}, {entry.Name}, which stays in your pockets.",
            _ => $"On the bar: slot {slot}, empty.",
        };
    }

    /// <summary>A use's receipt in words: what healed, or in Creative the sound's receipt, or the host's refusal (fallback when it says nothing).</summary>
    private static string EatenWords(CommandHost host, string item, JsonObject result, string fallback)
    {
        if (!Ok(result)) return Failed(result, fallback);
        var data = result["data"];
        var healed = data?["healed_quarters"]?.GetValue<int>() ?? 0;
        var kept = (data?["count"]?.GetValue<int>() ?? 1) == 0;
        // The receipt says the sound played (data.sound): the notice has it in words.
        var sound = data?["sound"]?.GetValue<string>() == "eat" ? "Crunch! " : "";
        var unit = UnitWords(host, item);
        if (healed > 0) return $"{sound}You ate {unit}: {Heal(host, healed)} healed.";
        return kept ? $"{sound}You ate {unit}. Nothing heals in Creative, so it stays in your pockets." : $"{sound}You ate {unit}.";
    }

    /// <summary>inventory.drop on a thing in the pockets: set down before the player, or the host's refusal.</summary>
    public static string DropWords(CommandHost host, string item)
    {
        var name = host.PocketEntries.FirstOrDefault(e => e.Item == item)?.Name ?? "it";
        var result = host.PlayerDrop(item);
        return Ok(result) ? $"You set {name} down." : Failed(result, "That could not be set down.");
    }

    /// <summary>inventory.store from the pockets to the chest, at a camp: some units of a stack, or a thing.</summary>
    public static string StoreWords(CommandHost host, string item, int? units)
    {
        var before = host.PocketEntries.FirstOrDefault(e => e.Item == item);
        var result = host.PlayerStoreFromPockets(item, units);
        if (!Ok(result)) return Failed(result, "That could not be stored.");
        if (before == null) return "Stored in the chest.";
        return before.Kind == "stack" ? $"Stored {Counted(host, item, units ?? before.Count)} in the chest." : $"Stored {before.Name} in the chest.";
    }

    /// <summary>inventory.take from the chest: a thing into the hand or the pockets, a stack's units into the pockets.</summary>
    public static string TakeWords(CommandHost host, string item, string to, int? units)
    {
        var before = host.ChestEntries.FirstOrDefault(e => e.Item == item);
        var result = host.PlayerTake(item, to.Length > 0 ? to : null, units);
        if (!Ok(result)) return Failed(result, "That could not be taken.");
        if (before == null) return "Taken out of the chest.";
        if (before.Kind == "stack") return $"Took {Counted(host, item, units ?? before.Count)} into your pockets.";
        return to == "hand" ? $"{before.Name} is in your hand." : $"{before.Name} is in your pockets.";
    }

    // ---- the camp's Inventory page (decision 87) ----

    /// <summary>
    /// The Inventory page at the camp: what the hands carry (the wallet is retired: logs and stones are stacks), then the chest with a Take for each entry,
    /// the Gubble's switch in words (the setting itself is in the settings), and the pockets beside it with a Store for each entry.
    /// </summary>
    public static List<JournalLine> CampLines(CommandHost host)
    {
        var carried = host.HeldName(CommandHost.PlayerAvatar);
        var lines = new List<JournalLine>
        {
            new(JournalInk.Heading, CampPages.Inventory),
            new(JournalInk.Text, "Carrying: " + (string.IsNullOrEmpty(carried) ? "empty hands" : KernelJson.DisplayText(carried, 60))),
        };
        if (!host.HasInventory)
        {
            lines.Add(new(JournalInk.Text, CommandHost.InventoryNotHere));
            return lines;
        }
        var reachable = host.ChestReachable;
        lines.Add(new(JournalInk.Heading, "The chest"));
        lines.Add(new(JournalInk.Faint, ChestCaps(host)));
        lines.Add(new(JournalInk.Faint, GubbleChestWords(host)));
        if (!reachable) lines.Add(new(JournalInk.Text, CommandHost.ChestAtCamp));
        var chest = host.ChestEntries;
        if (chest.Count == 0) lines.Add(new(JournalInk.Text, "The chest is empty."));
        foreach (var entry in chest)
        {
            lines.Add(new(JournalInk.Title, EntryWords(host, entry)));
            if (entry.Kind == "stack")
            {
                lines.Add(new(JournalInk.Button, "Take one into your pockets", Target(Take, entry.Item, "pockets", "1"), reachable));
                if (entry.Count > 1) lines.Add(new(JournalInk.Button, $"Take all {entry.Count} into your pockets", Target(Take, entry.Item, "pockets", All), reachable));
            }
            else
            {
                lines.Add(new(JournalInk.Button, "Take into your hand", Target(Take, entry.Item, "hand"), reachable));
                lines.Add(new(JournalInk.Button, "Take into your pockets", Target(Take, entry.Item, "pockets"), reachable));
            }
        }
        lines.Add(new(JournalInk.Heading, "Your pockets"));
        lines.Add(new(JournalInk.Faint, PocketsCaps(host)));
        // The hotbar's selection (decision 119): which slot is in the hand; each entry carries its slot on the bar, the chosen one marked.
        lines.Add(new(JournalInk.Text, SelectionWords(host)));
        var pockets = host.PocketEntries;
        var chosen = !host.HandNow.IsNone && host.HandNow.Layer == HandSelection.PocketsLayer ? host.HandNow.Pockets : null;
        if (pockets.Count == 0) lines.Add(new(JournalInk.Text, "Your pockets are empty."));
        foreach (var entry in pockets)
        {
            lines.Add(new(JournalInk.Title, $"{entry.Slot} · {EntryWords(host, entry)}" + (entry.Slot == chosen ? " · chosen" : "")));
            if (entry.Kind == "stack")
            {
                lines.Add(new(JournalInk.Button, "Store one in the chest", Target(Store, entry.Item, "chest", "1"), reachable));
                if (entry.Count > 1) lines.Add(new(JournalInk.Button, $"Store all {entry.Count} in the chest", Target(Store, entry.Item, "chest", All), reachable));
            }
            else lines.Add(new(JournalInk.Button, "Store in the chest", Target(Store, entry.Item, "chest"), reachable && entry.Here));
        }
        return lines;
    }

    /// <summary>The Gubble's chest switch as read-only words: it is the player's setting (decision 100), changed in the settings and never here.</summary>
    public static string GubbleChestWords(CommandHost host) => host.GubbleChestAllowed
        ? $"The Gubble may use the chest. You can change that in the settings ({PlayerControls.KeyLabel(Act.HudCustomize)})."
        : $"The Gubble may not use the chest. You can let it in the settings ({PlayerControls.KeyLabel(Act.HudCustomize)}).";
}
