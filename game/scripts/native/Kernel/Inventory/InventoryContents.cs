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
/// new record. One entry per kind (a stack is capped at its kind's size) and one per thing.
/// </summary>
public sealed record InventoryContents(IReadOnlyDictionary<string, int> Stacks, IReadOnlyList<InventoryThing> Things)
{
    public const int MaxUnits = 1000;
    public const int MaxNameLength = 80;
    private static readonly Regex Token = new(@"\A[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    private static readonly Regex ObjectId = new(@"\Aobj:[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    private static readonly Regex Prefix = new(@"\A[0-9a-f]{16}\z", RegexOptions.Compiled);

    public static InventoryContents Empty { get; } = new(new SortedDictionary<string, int>(StringComparer.Ordinal), Array.Empty<InventoryThing>());

    public bool IsEmpty => Stacks.Count == 0 && Things.Count == 0;
    public int Entries => Stacks.Count + Things.Count;
    public int Units(string kind) => Stacks.GetValueOrDefault(kind);
    public InventoryThing? Thing(string id) => Things.FirstOrDefault(t => t.Id == id);

    /// <summary>A kind's stack at this many units (0 takes the entry away).</summary>
    public InventoryContents WithUnits(string kind, int units)
    {
        var stacks = new SortedDictionary<string, int>(Stacks.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        if (units <= 0) stacks.Remove(kind);
        else stacks[kind] = units;
        return this with { Stacks = stacks };
    }

    public InventoryContents WithThing(InventoryThing thing) => this with { Things = Things.Where(t => t.Id != thing.Id).Append(thing).ToArray() };
    public InventoryContents WithoutThing(string id) => this with { Things = Things.Where(t => t.Id != id).ToArray() };
    public InventoryContents WithoutStacks() => this with { Stacks = Empty.Stacks };
    /// <summary>Only the things the predicate keeps (a reconciliation with the island's room state).</summary>
    public InventoryContents Keeping(Func<InventoryThing, bool> keep) => Things.All(keep) ? this : this with { Things = Things.Where(keep).ToArray() };

    /// <summary>The mass of everything held: each stack's units at its kind's mass (a kind the pack no longer names weighs nothing), each thing its own.</summary>
    public double MassKg(InventoryRules? rules) =>
        Stacks.Sum(p => p.Value * (rules?.Kind(p.Key)?.MassKg ?? 0)) + Things.Sum(t => t.MassKg);

    public bool Same(InventoryContents other) =>
        ReferenceEquals(this, other) || (Stacks.Count == other.Stacks.Count && Stacks.All(p => other.Stacks.TryGetValue(p.Key, out var n) && n == p.Value) &&
                                         Things.SequenceEqual(other.Things));

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

    /// <summary>The pockets as the team file keeps them, or null when they hold nothing (a mode with nothing writes no key).</summary>
    public JsonObject? PocketsBlock()
    {
        if (IsEmpty) return null;
        var block = new JsonObject();
        if (Stacks.Count > 0) block["stacks"] = new JsonObject(Stacks.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
        if (Things.Count > 0)
            block["things"] = new JsonArray(Things.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id, ["room_id"] = t.RoomId, ["manifest_prefix"] = t.ManifestPrefix, ["name"] = t.Name, ["mass_kg"] = Math.Round(t.MassKg, 6),
            }).ToArray());
        return block;
    }

    /// <summary>One mode's pockets read strictly (null: what is wrong is in the message); the team file refuses the whole file for any problem.</summary>
    public static InventoryContents? FromPockets(JsonElement block, out string problem)
    {
        problem = "";
        if (block.ValueKind != JsonValueKind.Object || !block.EnumerateObject().Any() || block.EnumerateObject().Any(p => p.Name is not ("stacks" or "things"))) { problem = "pockets"; return null; }
        var stacks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var things = new List<InventoryThing>();
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
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 5 ||
                    item.EnumerateObject().Any(p => p.Name is not ("id" or "room_id" or "manifest_prefix" or "name" or "mass_kg"))) { problem = "a thing"; return null; }
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
        return new InventoryContents(stacks, things);
    }

    /// <summary>A thing's name as it is kept: untrusted single-line display text, 1 to 80 code points, nothing hidden.</summary>
    public static bool IsName(string text) => text.Length > 0 && KernelText.CodePoints(text).Length <= MaxNameLength && !KernelText.HasHidden(text);
}
