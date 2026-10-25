using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GarageGame.Native.Kernel;

/// <summary>
/// One kind that stacks (the rules pack's inventory.kinds; P-Inventory, docs/game/MECHANICS.md decisions 98, 99 and 92): its id, the
/// words for one and for many (untrusted text, shown only), one unit's mass, the most units in one entry, the quarters of a heart a
/// unit heals when used (null: no use yet, as a seed), and the room assets that are a loose unit of it (a windfall a held one stores
/// as a unit; none yet, P-Food places them).
/// </summary>
public sealed record InventoryKind(string Id, string Name, string Plural, double MassKg, int Stack, int? HealQuarters, IReadOnlyList<string> Assets)
{
    /// <summary>The words for this many units: the one-word for one, the plural for more (the one-word when the pack names no plural).</summary>
    public string Words(int count) => count == 1 ? Name : Plural;
}

/// <summary>
/// The pockets' or the chest's caps (decision 120: counts, not weights): at most this many slots, each a stack (up to its kind's stack) or a
/// unique thing. Masses stay data (a kind's mass_kg, a thing's asset), never a limit.
/// </summary>
public sealed record InventoryCaps(int Entries);

/// <summary>The inventory's words for a count: "ten" up to twenty, digits above (the refusals' "Your pockets are full: ten things.").</summary>
public static class InventoryWords
{
    private static readonly string[] Small =
    {
        "no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    };

    public static string Number(int count) => count >= 0 && count < Small.Length ? Small[count] : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"ten things", "one thing".</summary>
    public static string Things(int count) => Number(count) + (count == 1 ? " thing" : " things");
}

/// <summary>The pack's inventory block: the kinds, the pockets' caps (carried everywhere, per team and mode) and the chest's (one per island save).</summary>
public sealed record InventoryRules(IReadOnlyList<InventoryKind> Kinds, InventoryCaps Pockets, InventoryCaps Chest)
{
    public InventoryKind? Kind(string id) => Kinds.FirstOrDefault(k => k.Id == id);

    /// <summary>The kind a room asset is a loose unit of, or null (a unique thing).</summary>
    public InventoryKind? KindOfAsset(string assetId) => Kinds.FirstOrDefault(k => k.Assets.Contains(assetId, StringComparer.Ordinal));
}

/// <summary>
/// The rules pack's optional inventory block (P-Inventory; contracts/island-rules.schema.json $defs/inventory), read fail-closed with the
/// rest of the pack: the schema's fields, types and bounds, and validate.py's checks (each kind once, an asset a unit of one kind at most).
/// Absent: null, no pockets and no chest (v4 and earlier are untouched).
/// </summary>
public sealed partial class IslandRules
{
    public InventoryRules? Inventory { get; private init; }

    private static InventoryRules? ParseInventoryBlock(JsonElement root) => root.TryGetProperty("inventory", out var block) ? ParseInventory(block) : null;

    private static InventoryRules ParseInventory(JsonElement block)
    {
        const string at = "$.inventory";
        Expect(block.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(block, at, new[] { "kinds", "pockets", "chest" }, new[] { "kinds", "pockets", "chest" });
        var list = block.GetProperty("kinds");
        Expect(list.ValueKind == JsonValueKind.Array && list.GetArrayLength() is >= 1 and <= 16, at + ".kinds lists 1 to 16 kinds.");
        var kinds = new List<InventoryKind>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var where = $"{at}.kinds[{index++}]";
            Expect(item.ValueKind == JsonValueKind.Object, where + " must be an object.");
            Fields(item, where, new[] { "id", "name", "plural", "mass_kg", "stack", "use", "assets" }, new[] { "id", "name", "mass_kg", "stack" });
            var id = TokenAt(item, "id", where);
            var name = Display(item, "name", where, NamesRules.MaxLength, 1);
            var plural = item.TryGetProperty("plural", out _) ? Display(item, "plural", where, NamesRules.MaxLength, 1) : name;
            var mass = Positive(item, "mass_kg", where, 0.5);
            var stack = Int(item, "stack", where);
            Expect(stack is >= 1 and <= 1000, where + ".stack must be an integer from 1 to 1000.");
            int? heal = null;
            if (item.TryGetProperty("use", out var use))
            {
                Expect(use.ValueKind == JsonValueKind.Object, where + ".use must be an object.");
                Fields(use, where + ".use", new[] { "heal_quarters" }, new[] { "heal_quarters" });
                heal = Int(use, "heal_quarters", where + ".use");
                Expect(heal is >= 1 and <= 20, where + ".use.heal_quarters must be an integer from 1 to 20.");
            }
            var assets = new List<string>();
            if (item.TryGetProperty("assets", out var listed))
            {
                Expect(listed.ValueKind == JsonValueKind.Array && listed.GetArrayLength() is >= 1 and <= 16, where + ".assets lists 1 to 16 assets.");
                foreach (var asset in listed.EnumerateArray())
                {
                    Expect(asset.ValueKind == JsonValueKind.String && Token.IsMatch(asset.GetString()!), where + ".assets names assets by their lowercase ids.");
                    assets.Add(asset.GetString()!);
                }
                Expect(assets.Distinct(StringComparer.Ordinal).Count() == assets.Count, where + ".assets names an asset twice.");
            }
            kinds.Add(new InventoryKind(id, name, plural, mass, stack, heal, assets));
        }
        // validate.py's checks a schema cannot express.
        Expect(kinds.Select(k => k.Id).Distinct(StringComparer.Ordinal).Count() == kinds.Count, at + ": an inventory kind appears more than once.");
        var units = kinds.SelectMany(k => k.Assets).ToList();
        Expect(units.Distinct(StringComparer.Ordinal).Count() == units.Count, at + ": an asset is a unit of more than one inventory kind.");
        return new InventoryRules(kinds, Caps(block, "pockets", at, 16), Caps(block, "chest", at, 64));
    }

    /// <summary>The pockets' or the chest's slots (decision 120: a count; a pack before it that names a mass cap is refused, as any unknown field).</summary>
    private static InventoryCaps Caps(JsonElement block, string name, string at, int maxEntries)
    {
        var caps = block.GetProperty(name);
        var where = $"{at}.{name}";
        Expect(caps.ValueKind == JsonValueKind.Object, where + " must be an object.");
        Fields(caps, where, new[] { "entries" }, new[] { "entries" });
        var entries = Int(caps, "entries", where);
        Expect(entries >= 1 && entries <= maxEntries, $"{where}.entries must be an integer from 1 to {maxEntries}.");
        return new InventoryCaps(entries);
    }
}
