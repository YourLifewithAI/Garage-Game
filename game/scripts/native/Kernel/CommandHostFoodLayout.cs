using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Food's numbers (P-Food; docs/game/MECHANICS.md decisions 92 and 108, D1 in docs/runs/RUN-2-PLAYTEST-ROUND-2.md), every one
/// provisional: what a plant bears is the rules pack's food block (FoodRules), where on the plant it hangs is here.
/// </summary>
public static class FoodLaws
{
    /// <summary>An apple is a sphere this wide, on a stalk at least this long (D1's look: "a red sphere 1 cm across with a stalk").</summary>
    public const float AppleM = FoodView.AppleM, StalkM = FoodView.StalkM;
    /// <summary>A cluster of berries fits a box this wide and tall (six beads of BeadM, D1's "six 0.3 cm dark blue beads").</summary>
    public const float ClusterM = FoodView.ClusterM, BeadM = FoodView.BeadM;
    /// <summary>A windfall lies this far from its trunk's side (the gap from the bark to the apple), drawn from the plant's id.</summary>
    public const float WindfallGapMinM = 0.006f, WindfallGapMaxM = 0.05f;
    /// <summary>
    /// A canopy apple hangs under its crown (the lowest caps' rims, a little in from their edge), its bottom at most this far above
    /// the tree's root: above the 10 cm body's standing reach (3.5 body heights, 0.35 m) and below it with a hop (6.5 cm), so the
    /// canopy's fruit is "reached with a hop" (decision 108). A crown that hangs higher lets the apple down on a longer stalk; a
    /// lower crown holds it within reach of a standing body.
    /// </summary>
    public const float CanopyHopM = 0.38f;
    /// <summary>How far in from its cap's rim a canopy apple hangs (a share of the rim's half widths), so it is under the foliage.</summary>
    public const float CanopyInset = 0.85f;
    /// <summary>A cluster sits on its plant's side at the player's height (at most this above the root, below the plant's top).</summary>
    public const float ClusterHeightM = 0.05f;
    /// <summary>...this share of the plant's half width out from its stem.</summary>
    public const float ClusterInset = 0.75f;
    /// <summary>The most food spots one plant bears (its picked spots are saved as the bits of one byte).</summary>
    public const int MaxSpots = 8;
    /// <summary>The plants an island keeps a food record for (picked and not grown back yet).</summary>
    public const int MaxFoodPlants = 4096;
    /// <summary>
    /// Loose units (P-Inventory-2, decision 118): a seed's, a log's or a stone's box for reach (food has its own size); a unit set down lies
    /// this far before the body's side, found on the ground within LooseProbeM up or down; fire takes one within LooseCatchM of a burning cell.
    /// </summary>
    public const float LooseM = 0.012f, LooseAheadM = 0.03f, LooseProbeM = 0.08f, LooseCatchM = 0.06f;
    /// <summary>Seeds (decision 118, provisional): an apple eaten gives one, berries one time in three; a harvest one. The pack's kind for them.</summary>
    public const int SeedsPerApple = 1, SeedsPerHarvest = 1;
    public const ulong BerriesSeedOneIn = 3;
    public const string SeedKind = "seed";
}

/// <summary>Where on its plant a unit of food is: a windfall on the ground at the stem's foot, an apple under the crown, a cluster on the side.</summary>
public enum FoodPlace { Windfall, Canopy, Cluster }

/// <summary>One food spot of a plant at its natural size: where, its bottom centre's offset from the plant's root, and its stalk's length up to the crown.</summary>
public readonly record struct FoodSpot(FoodPlace Place, Vector3 OffsetM, float HangM);

/// <summary>One of the pack's food sources (contracts/island-rules.schema.json $defs/food_source): the kind it bears and where, by species.</summary>
public sealed record FoodSource(string Kind, string Species, int OneIn, (int Min, int Max) Windfalls, (int Min, int Max) Canopy, (int Min, int Max) Clusters);

/// <summary>The pack's food block: its sources, one per species, and the island hours a plant takes to bear again everything picked from it.</summary>
public sealed record FoodRules(IReadOnlyList<FoodSource> Sources, double RegrowH)
{
    public FoodSource? For(string species) => Sources.FirstOrDefault(s => s.Species == species);
}

/// <summary>A plant that bears food: its copy, the source that made it bear and its spots (at most FoodLaws.MaxSpots), in a fixed order.</summary>
public sealed record FoodPlant(string Id, PlantSet Set, int Row, FoodSource Source, IReadOnlyList<FoodSpot> Spots)
{
    public Vector3 Root => Set.Position(Row);

    /// <summary>A spot's bottom centre in room metres on the plant grown or shrunk to factor (scaled about its root, as its copy is drawn).</summary>
    public Vector3 At(int spot, float factor) => Root + Spots[spot].OffsetM * factor;

    /// <summary>A spot's box at factor: the apple's body (its stalk left out) or the cluster's, from its bottom centre.</summary>
    public Aabb Box(int spot, float factor)
    {
        var size = Spots[spot].Place == FoodPlace.Cluster ? FoodLaws.ClusterM : FoodLaws.AppleM;
        var at = At(spot, factor);
        return new Aabb(at - new Vector3(size * 0.5f, 0, size * 0.5f), new Vector3(size, size, size));
    }
}

/// <summary>
/// Which plants bear food and where (decision 108; D1's proposal): chosen from the plant's id alone, so the same on every load and
/// every machine, with no generator or manifest change (a later x_food can replace it). A source names a species; one copy in its
/// one_in bears (a hash of the id), and how many windfalls, canopy apples and clusters it bears, and where each lies, come from
/// further hashes of the id. Trees bear windfalls round their trunk's foot and apples under their lowest caps; other plants bear
/// clusters on their side and windfalls at their foot. Pure: it reads only the room's rows and the pack.
/// </summary>
public static class FoodLayout
{
    /// <summary>The food a copy bears, or null: not a species the pack names, a stone, not chosen, or nothing to bear.</summary>
    public static FoodPlant? Of(PlantSet set, int row, FoodRules rules)
    {
        if (set.Kind == PlantKind.Stone || rules.For(set.Species) is not { } source) return null;
        var id = PlantCopies.IdOf(set.Name, row);
        if (Hash("food|" + id) % (ulong)source.OneIn != 0) return null;
        var counts = Hash("food|count|" + id);
        var tree = set.IsTree && set.Tree(row).Caps.Length > 0;
        var windfalls = Pick(source.Windfalls, counts);
        var canopy = tree ? Pick(source.Canopy, counts >> 16) : 0;
        var clusters = Pick(source.Clusters, counts >> 32);
        var spots = new List<FoodSpot>(FoodLaws.MaxSpots);
        var root = set.Position(row);
        for (var i = 0; i < windfalls && spots.Count < FoodLaws.MaxSpots; i++) spots.Add(Windfall(set, row, root, id, i, windfalls));
        if (canopy > 0)
        {
            // The lowest caps first: the crown's underside, where a hop reaches.
            var caps = set.Tree(row).Caps.OrderBy(c => c.RimM).Take(2).ToArray();
            for (var i = 0; i < canopy && spots.Count < FoodLaws.MaxSpots; i++) spots.Add(Canopy(caps[i % caps.Length], root, id, i, canopy));
        }
        for (var i = 0; i < clusters && spots.Count < FoodLaws.MaxSpots; i++) spots.Add(Cluster(set, row, root, id, i, clusters));
        return spots.Count == 0 ? null : new FoodPlant(id, set, row, source, spots);
    }

    /// <summary>Every plant of the room that bears food, set by set in table order.</summary>
    public static IEnumerable<FoodPlant> All(PlantCopies copies, FoodRules rules)
    {
        foreach (var set in copies.Sets)
        {
            if (set.Kind == PlantKind.Stone || rules.For(set.Species) == null) continue;
            for (var row = 0; row < set.Count; row++)
                if (Of(set, row, rules) is { } plant) yield return plant;
        }
    }

    /// <summary>The plants bearing food rooted within radius of (x, z) horizontally: only the food species' tiles round the circle are read.</summary>
    public static IEnumerable<FoodPlant> Near(PlantCopies copies, FoodRules rules, float x, float z, float radius)
    {
        var (tx0, tz0) = PlantCopies.TileOf(x - radius, z - radius);
        var (tx1, tz1) = PlantCopies.TileOf(x + radius, z + radius);
        foreach (var set in copies.Sets)
        {
            if (set.Kind == PlantKind.Stone || rules.For(set.Species) == null) continue;
            for (var tx = tx0; tx <= tx1; tx++)
            for (var tz = tz0; tz <= tz1; tz++)
            {
                var tile = set.FindTile(tx, tz);
                if (tile < 0) continue;
                var (_, _, first, count) = set.Tile(tile);
                for (var row = first; row < first + count; row++)
                {
                    var at = set.Position(row);
                    float dx = at.X - x, dz = at.Z - z;
                    if (dx * dx + dz * dz <= radius * radius && Of(set, row, rules) is { } plant) yield return plant;
                }
            }
        }
    }

    /// <summary>A windfall round the stem's foot, the plant's windfalls spread round it evenly with a little play, each its own gap from the bark.</summary>
    private static FoodSpot Windfall(PlantSet set, int row, Vector3 root, string id, int index, int of)
    {
        var h = Hash($"food|spot|{id}|w{index}");
        var turn = Unit(h) * Mathf.Tau;
        var angle = turn + index * Mathf.Tau / of + (Unit(h >> 16) - 0.5f) * Mathf.Pi / of * 0.8f;
        float axisX = root.X, axisZ = root.Z, stem;
        if (set.IsTree)
        {
            var tree = set.Tree(row);
            (axisX, axisZ, stem) = (tree.AxisX, tree.AxisZ, tree.TrunkRadiusM);
        }
        else stem = HalfWidth(set, row);
        var distance = stem + FoodLaws.AppleM * 0.5f + Mathf.Lerp(FoodLaws.WindfallGapMinM, FoodLaws.WindfallGapMaxM, Unit(h >> 32));
        return new FoodSpot(FoodPlace.Windfall, new Vector3(axisX - root.X + MathF.Cos(angle) * distance, 0, axisZ - root.Z + MathF.Sin(angle) * distance), 0);
    }

    /// <summary>An apple under a cap's rim, in from its edge, hanging on its stalk to at most CanopyHopM above the root.</summary>
    private static FoodSpot Canopy(TreeCap cap, Vector3 root, string id, int index, int of)
    {
        var h = Hash($"food|spot|{id}|c{index}");
        var angle = Unit(h) * Mathf.Tau + index * Mathf.Tau / of;
        var x = cap.X + MathF.Cos(angle) * cap.RimHalfXM * FoodLaws.CanopyInset;
        var z = cap.Z + MathF.Sin(angle) * cap.RimHalfZM * FoodLaws.CanopyInset;
        var top = cap.RimM - 0.001f;
        var bottom = Math.Min(top - FoodLaws.AppleM - FoodLaws.StalkM, root.Y + FoodLaws.CanopyHopM);
        return new FoodSpot(FoodPlace.Canopy, new Vector3(x - root.X, bottom - root.Y, z - root.Z), top - bottom - FoodLaws.AppleM);
    }

    /// <summary>A cluster on the plant's side at the player's height (or its top, for a low clump), the plant's clusters spread round it.</summary>
    private static FoodSpot Cluster(PlantSet set, int row, Vector3 root, string id, int index, int of)
    {
        var h = Hash($"food|spot|{id}|b{index}");
        var angle = Unit(h) * Mathf.Tau + index * Mathf.Tau / of;
        var side = HalfWidth(set, row) * FoodLaws.ClusterInset;
        var height = Math.Clamp(set.TopM(row) - root.Y - FoodLaws.ClusterM, 0, FoodLaws.ClusterHeightM);
        return new FoodSpot(FoodPlace.Cluster, new Vector3(MathF.Cos(angle) * side, height, MathF.Sin(angle) * side), 0);
    }

    /// <summary>A copy's half width at its natural size: the smaller of its mesh's two ground extents under its row's scale, halved.</summary>
    private static float HalfWidth(PlantSet set, int row)
    {
        var scale = set.Scale(row);
        return Math.Max(0.002f, 0.5f * Math.Min(set.MeshBounds.Size.X * scale.X, set.MeshBounds.Size.Z * scale.Z));
    }

    private static int Pick((int Min, int Max) range, ulong bits) => range.Max <= 0 ? 0 : range.Min + (int)(bits % (ulong)(range.Max - range.Min + 1));

    private static float Unit(ulong bits) => (bits & 0xFFFF) / 65536f;

    /// <summary>A text's 64-bit hash, the same on every machine: FNV-1a over its UTF-8 bytes, mixed by SplitMix64's finaliser so every bit counts.</summary>
    public static ulong Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        hash *= 0x94D049BB133111EBUL;
        return hash ^ (hash >> 31);
    }
}

/// <summary>
/// The rules pack's optional food block (P-Food; contracts/island-rules.schema.json $defs/food), read fail-closed with the rest of the
/// pack: the schema's fields, types and bounds, and the checks a schema cannot express (an inventory block beside it, each source's
/// kind one of its kinds, each species once, each count range from its least to its most, something to bear and at most
/// FoodLaws.MaxSpots of it). Absent: no plant bears food.
/// </summary>
public sealed partial class IslandRules
{
    public FoodRules? Food { get; private init; }

    private static FoodRules? ParseFoodBlock(JsonElement root, InventoryRules? inventory) => root.TryGetProperty("food", out var block) ? ParseFood(block, inventory) : null;

    private static FoodRules ParseFood(JsonElement block, InventoryRules? inventory)
    {
        const string at = "$.food";
        Expect(inventory != null, "$.food needs an inventory block: food is picked into the pockets.");
        Expect(block.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(block, at, new[] { "sources", "regrow_h" }, new[] { "sources", "regrow_h" });
        var regrow = Positive(block, "regrow_h", at, 720);
        var list = block.GetProperty("sources");
        Expect(list.ValueKind == JsonValueKind.Array && list.GetArrayLength() is >= 1 and <= 16, at + ".sources lists 1 to 16 sources.");
        var sources = new List<FoodSource>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var where = $"{at}.sources[{index++}]";
            Expect(item.ValueKind == JsonValueKind.Object, where + " must be an object.");
            Fields(item, where, new[] { "kind", "species", "one_in", "windfalls", "canopy", "clusters" }, new[] { "kind", "species", "one_in" });
            var kind = TokenAt(item, "kind", where);
            Expect(inventory!.Kind(kind) != null, where + ".kind must be one of the inventory's kinds.");
            var species = TokenAt(item, "species", where);
            var oneIn = Int(item, "one_in", where);
            Expect(oneIn is >= 1 and <= 100, where + ".one_in must be an integer from 1 to 100.");
            var windfalls = Range(item, "windfalls", where);
            var canopy = Range(item, "canopy", where);
            var clusters = Range(item, "clusters", where);
            Expect(windfalls.Max + canopy.Max + clusters.Max is >= 1 and <= FoodLaws.MaxSpots, $"{where} bears 1 to {FoodLaws.MaxSpots} units at most.");
            sources.Add(new FoodSource(kind, species, oneIn, windfalls, canopy, clusters));
        }
        Expect(sources.Select(s => s.Species).Distinct(StringComparer.Ordinal).Count() == sources.Count, at + ": a species has more than one food source.");
        return new FoodRules(sources, regrow);
    }

    /// <summary>A count range [least, most], each 0 to FoodLaws.MaxSpots; absent is none.</summary>
    private static (int Min, int Max) Range(JsonElement item, string name, string at)
    {
        if (!item.TryGetProperty(name, out var range)) return (0, 0);
        Expect(range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2 &&
            range.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.GetRawText().All(char.IsAsciiDigit) && v.TryGetInt32(out var n) && n is >= 0 and <= FoodLaws.MaxSpots),
            $"{at}.{name} is [least, most], whole numbers from 0 to {FoodLaws.MaxSpots}.");
        var (min, max) = (range[0].GetInt32(), range[1].GetInt32());
        Expect(min <= max, $"{at}.{name}'s least is at most its most.");
        return (min, max);
    }
}
