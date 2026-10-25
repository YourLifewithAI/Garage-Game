using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// The pockets and the chest on screen (MECHANICS.md decisions 87, 98 to 103, 108 and 113; docs/game/INVENTORY.md), written from what the
/// host already says and nothing else: its readers (PocketEntries, ChestEntries, the caps, the Gubble's chest switch) for the words, and
/// the player's own commands (inventory.use, drop, store, take) for the buttons, each answered in the host's words. The camp's Inventory
/// page uses CampLines, the Tab panel (RoomHud.Pockets.cs) reads the pockets itself and runs its buttons through this class too, and
/// E's notices (RoomHud.Interact.cs) take their words from here. Names are untrusted display text: they go into labels as plain text and
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

    /// <summary>"3 of 6 things · 0.06 of 0.3 kg": what a store holds against its caps.</summary>
    public static string CapsWords(int entries, InventoryCaps caps, double massKg) =>
        $"{entries} of {caps.Entries} {(caps.Entries == 1 ? "thing" : "things")} · {Kg(massKg)} of {Kg(caps.MassKg)} kg";

    public static string PocketsCaps(CommandHost host) =>
        host.PocketCaps is { } caps ? CapsWords(host.PocketEntries.Count, caps, host.PocketMassKg) : "";

    public static string ChestCaps(CommandHost host) =>
        host.ChestCaps is { } caps ? CapsWords(host.ChestEntries.Count, caps, host.ChestMassKg) : "";

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
    public static string EatWords(CommandHost host, string item)
    {
        var result = host.PlayerUse(item);
        if (!Ok(result)) return Failed(result, "That could not be eaten.");
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
    /// The Inventory page at the camp: what the hands carry and the wallet holds (as before), then the chest with a Take for each entry,
    /// the Gubble's switch in words (the setting itself is in the settings), and the pockets beside it with a Store for each entry.
    /// </summary>
    public static List<JournalLine> CampLines(CommandHost host)
    {
        var carried = host.HeldName(CommandHost.PlayerAvatar);
        var wallet = host.Nature.Wallet;
        var lines = new List<JournalLine>
        {
            new(JournalInk.Heading, CampPages.Inventory),
            new(JournalInk.Text, "Carrying: " + (string.IsNullOrEmpty(carried) ? "empty hands" : KernelJson.DisplayText(carried, 60))),
            new(JournalInk.Text, "In your wallet: " + (wallet.Count == 0 ? "nothing yet" : string.Join(" · ", wallet.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{CampPages.Material(p.Key)} {p.Value}")))),
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
        var pockets = host.PocketEntries;
        if (pockets.Count == 0) lines.Add(new(JournalInk.Text, "Your pockets are empty."));
        foreach (var entry in pockets)
        {
            lines.Add(new(JournalInk.Title, EntryWords(host, entry)));
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
