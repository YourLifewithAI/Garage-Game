using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;
using GarageGame.Native.Sandbox;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Where E or F would pick a loose unit up (PlayerCanPick's ground candidates and the pockets' take from the ground): a windfall of a plant
/// (its spot, still lying there) or a unit dropped on the island (its index in the nature block's list), with its kind, its box and the
/// angle and gap the choice orders by.
/// </summary>
internal readonly record struct LooseCandidate(string Kind, Aabb Box, string? Plant, int Spot, int Dropped, float Angle, float Gap);

/// <summary>
/// Loose food and seeds (P-Inventory-2; docs/game/MECHANICS.md decision 118: "food and seeds are things in the world"): a unit set down
/// with inventory.drop lies loose before the player, a thing of its kind in the world (the nature block's loose list, saved with the
/// island, at most NatureState.MaxLoose, the oldest going first); a windfall lies loose at its plant's trunk from the start (the food
/// layout's windfall spots, which grow back with their plant); either is picked up again into its stack with F or E (inventory.take from
/// the ground, the faced one of its kind within reach). Loose units burn in fire: a dropped one is gone, a windfall picked by the flames
/// (it grows back with its plant). The look draws them with the food (FoodView). Seeds come from eating (an apple a seed, berries one
/// time in three from the eating's action id, provisional) and from harvesting a farmable plant (FoodHarvested, a hook nothing calls yet).
/// </summary>
public partial class CommandHost
{
    public const string NothingLooseInReach = "There is none of that within reach to pick up.";
    public const string NoGroundToSetDown = "There is no ground before you to set it down on.";
    public const string GroundIntoPockets = "From the ground, a unit goes into the pockets.";
    /// <summary>What the food view and E's choice say a loose unit's source is (FoodOutcome.Source): the pockets' take from the ground.</summary>
    public const string FromTheGround = "ground";

    /// <summary>The loose units dropped on the island now (kind and bottom centre), oldest first: the HUD's and the tests' reader.</summary>
    public IReadOnlyList<(string Kind, Vector3 At)> LooseUnits => Nature.Loose.Select(l => (l.Kind, l.PositionM)).ToList();

    /// <summary>A loose unit's box from its bottom centre (food its own size; a seed, a log or a stone FoodLaws.LooseM).</summary>
    private static Aabb LooseBox(string kind, Vector3 at)
    {
        var size = kind switch { "apple" => FoodLaws.AppleM, "berries" => FoodLaws.ClusterM, _ => FoodLaws.LooseM };
        return new Aabb(at - new Vector3(size * 0.5f, 0, size * 0.5f), new Vector3(size, size, size));
    }

    /// <summary>
    /// The loose units the player faces within reach with a clear way to them (within SandboxControls.AheadDegrees), the most faced
    /// first, then the nearest: the windfalls still lying at their plants' trunks and the units dropped. kind: only that kind's.
    /// </summary>
    internal List<LooseCandidate> LooseInReach(string? kind = null)
    {
        var found = new List<LooseCandidate>();
        if (InventoryRules == null || Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) return found;
        var feet = Player.GlobalPosition;
        void Consider(string unitKind, Aabb box, string? plant, int spot, int dropped)
        {
            if (kind != null && unitKind != kind) return;
            if (!FoodWithinReach(Player, box)) return;
            var angle = FoodAngle(Player, box);
            if (angle > SandboxControls.AheadDegrees) return;
            found.Add(new LooseCandidate(unitKind, box, plant, spot, dropped, angle, feet.DistanceSquaredTo(box.GetCenter())));
        }
        if (FoodRulesNow is { } rules && Room?.Copies is { } copies)
            foreach (var plant in FoodLayout.Near(copies, rules, feet.X, feet.Z, FoodNearM))
            {
                var factor = ShownFactor(plant.Id);
                if (factor <= 0) continue;
                var picked = PickedNow(plant.Id);
                for (var i = 0; i < plant.Spots.Count; i++)
                    if (plant.Spots[i].Place == FoodPlace.Windfall && (picked & (1 << i)) == 0) Consider(plant.Source.Kind, plant.Box(i, factor), plant.Id, i, -1);
            }
        for (var i = 0; i < Nature.Loose.Count; i++)
        {
            var unit = Nature.Loose[i];
            if (Math.Abs(unit.PositionM.X - feet.X) > FoodNearM || Math.Abs(unit.PositionM.Z - feet.Z) > FoodNearM) continue;
            Consider(unit.Kind, LooseBox(unit.Kind, unit.PositionM), null, -1, i);
        }
        return found.OrderBy(c => c.Angle).ThenBy(c => c.Gap).ThenBy(c => c.Plant ?? "", StringComparer.Ordinal).ThenBy(c => c.Dropped)
            .Where(c => Physics().ReachClear(Player, c.Box, new Rid())).ToList();
    }

    /// <summary>A loose unit of a kind the player faces within reach into the pockets, as the player's inventory.take from the ground (F and E).</summary>
    public JsonObject PlayerTakeFromGround(string kind, bool preview = false) =>
        PlayerCommand("inventory.take", new JsonObject { ["from"] = FromTheGround, ["item"] = kind, ["to"] = "pockets" }, preview);

    /// <summary>
    /// inventory.take from the ground (P-Inventory-2, decision 118): the faced loose unit of the kind within reach into its stack, a windfall
    /// (its plant's spot picked: it grows back with the plant) or a unit dropped (gone from the island's list). Refused: no such unit within
    /// reach (target_not_found); the pockets past a cap (occupied, their words). The pockets gain first, then the island gives, one durable
    /// room revision with nothing in affected (a loose unit is no entity; a windfall's plant is not one of the authority's either, and the
    /// request names the kind, not the plant). The player's alone (the pockets are: CompanionChestOnly refuses before this).
    /// </summary>
    private JsonObject TakeFromGround(JsonElement args, InventoryRules rules, string principal, string actionId, string fingerprint, bool preview)
    {
        if (Str(args, "to") != "pockets") throw new Refusal("request_invalid", GroundIntoPockets, "$.args.to");
        var item = Str(args, "item")!;
        if (args.TryGetProperty("count", out var count) && count.GetInt32() != 1) throw new Refusal("invalid_args", "One unit is picked up at a time.", "$.args.count");
        var kind = rules.Kind(item) ?? throw new Refusal("target_not_found", NothingLooseInReach, "$.args.item");
        var found = LooseInReach(kind.Id);
        if (found.Count == 0) throw new Refusal("target_not_found", NothingLooseInReach, "$.args.item", retryable: true);
        var unit = found[0];
        var pockets = PocketContents;
        var next = pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1);
        CheckFits(next, rules, rules.Pockets, pockets: true, kind);
        if (_natureUnavailable) throw new Refusal("not_ready", NatureNotice, retryable: true);
        var candidate = Nature.Copy();
        if (unit.Plant is { } plant)
        {
            foreach (var gone in candidate.Food.Values.Where(GrownBack).Select(f => f.Id).ToList()) candidate.Food.Remove(gone);
            if (!candidate.Food.ContainsKey(plant) && candidate.Food.Count >= FoodLaws.MaxFoodPlants) throw new Refusal("occupied", FoodTooMany, "$.args.item");
            candidate.Food[plant] = Picking(plant, unit.Spot);
        }
        else candidate.Loose.RemoveAt(unit.Dropped);
        if (preview) return Previewed("inventory.take", principal, actionId);
        SavePockets(next);
        try { return CommitInventory(candidate, _chest, "inventory.take", principal, actionId, fingerprint, Array.Empty<string>(), Moved(kind.Id, 1, "pockets")); }
        catch (Refusal)
        {
            SavePockets(pockets, keepIfUnsaved: true);
            throw;
        }
    }

    /// <summary>
    /// The ground before the player where a unit set down lies (its bottom centre): a hand's breadth ahead of the feet, on the first
    /// walkable face below; null when there is none within a hand's height.
    /// </summary>
    private Vector3? LooseSpot()
    {
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) return null;
        var ahead = Player.GlobalPosition + SandboxControls.Facing(Player) * (Player.BodyRadiusM + FoodLaws.LooseAheadM);
        using var query = PhysicsRayQueryParameters3D.Create(ahead + Vector3.Up * FoodLaws.LooseProbeM, ahead - Vector3.Up * FoodLaws.LooseProbeM, RoomBuilder.WorldLayer, KernelNames.NoRids);
        var hit = GetViewport().FindWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return null;
        var at = hit["position"].AsVector3();
        return Room.Bounds.Grow(0.01f).HasPoint(at) ? at : null;
    }

    /// <summary>
    /// inventory.drop of a stack's unit (P-Inventory-2, decision 118): one unit lies loose on the ground before the player, a thing of its
    /// kind in the world (picked up again into its stack with F or E). The island gains it first (one durable room revision, nothing in
    /// affected), then the pockets give (a pockets write lost now leaves the unit both loose and counted, never lost).
    /// </summary>
    private JsonObject DropUnit(string kind, InventoryContents pockets, string principal, string actionId, string fingerprint, bool preview)
    {
        var spot = LooseSpot() ?? throw new Refusal("out_of_bounds", NoGroundToSetDown, "$.args.item", retryable: true);
        if (preview) return Previewed("inventory.drop", principal, actionId);
        RequirePocketsFiled();
        var candidate = Nature.Copy();
        candidate.Drop(new NatureLoose { Kind = kind, PositionM = spot });
        var result = CommitInventory(candidate, _chest, "inventory.drop", principal, actionId, fingerprint, Array.Empty<string>(), Moved(kind, 1, "ground"));
        SavePockets(pockets.WithUnits(kind, pockets.Units(kind) - 1), keepIfUnsaved: true);
        return result;
    }

    /// <summary>
    /// Fire takes the loose units it reaches (one step of the nature simulation): a unit dropped within FoodLaws.LooseCatchM of a burning
    /// cell is gone; a windfall there is picked by the flames (it grows back with its plant, as one picked). Not remembered by any undo
    /// step: what fire took of the loose is the island's, as a pick is. Returns whether anything burned.
    /// </summary>
    private bool BurnLooseUnits()
    {
        if (Nature.Fire is not { } fire || InventoryRules == null) return false;
        var burning = Nature.Cells.Values.Where(c => c.BurningS > 0).Select(c => c.Position).ToList();
        if (burning.Count == 0) return false;
        bool Reached(Vector3 at) => burning.Any(c => new Vector2(c.X - at.X, c.Z - at.Z).Length() <= FoodLaws.LooseCatchM && Math.Abs(c.Y - at.Y) <= FoodLaws.LooseCatchM);
        var changed = Nature.Loose.RemoveAll(unit => Reached(unit.PositionM)) > 0;
        if (FoodRulesNow is { } rules && Room?.Copies is { } copies)
            foreach (var plant in FoodLayout.Near(copies, rules, fire.Center.X, fire.Center.Z, fire.RadiusM + FoodNearM))
            {
                var factor = ShownFactor(plant.Id);
                if (factor <= 0) continue;
                for (var i = 0; i < plant.Spots.Count; i++)
                {
                    if (plant.Spots[i].Place != FoodPlace.Windfall || (PickedNow(plant.Id) & (1 << i)) != 0 || !Reached(plant.At(i, factor))) continue;
                    if (!Nature.Food.ContainsKey(plant.Id) && Nature.Food.Count >= FoodLaws.MaxFoodPlants) break;
                    Nature.Food[plant.Id] = Picking(plant.Id, i);
                    changed = true;
                }
            }
        if (changed) ApplyFood();
        return changed;
    }

    // ---- seeds (decision 118) ----

    /// <summary>
    /// The seeds eating a unit of a kind gives (decision 118, provisional): an apple one, berries one time in three, chosen from the eating
    /// command's action id (the same answer whatever replays it); any other kind none.
    /// </summary>
    internal static int SeedsFromEating(string kind, string actionId) => kind switch
    {
        "apple" => FoodLaws.SeedsPerApple,
        "berries" => FoodLayout.Hash("seed|" + actionId) % FoodLaws.BerriesSeedOneIn == 0 ? 1 : 0,
        _ => 0,
    };

    /// <summary>The pockets with so many seeds more, when the pack has a seed kind and they fit (a seed that would not fit is not kept); and how many went in.</summary>
    private (InventoryContents Pockets, int Seeds) WithSeeds(InventoryContents pockets, int seeds)
    {
        if (seeds <= 0 || InventoryRules is not { } rules || rules.Kind(FoodLaws.SeedKind) is not { } seed) return (pockets, 0);
        var next = pockets.WithUnits(seed.Id, pockets.Units(seed.Id) + seeds);
        return FitsWords(next, rules, rules.Pockets, pockets: true, seed) == null ? (next, seeds) : (pockets, 0);
    }

    /// <summary>
    /// The harvest of a farmable plant (decision 118: seeds come "from harvesting a farmable plant when farming exists"): a seed into the
    /// pockets when they have room. The hook farming will call; nothing calls it yet. Returns the seeds that went in.
    /// </summary>
    internal int FoodHarvested(string plant)
    {
        if (InventoryRules == null || IslandHeld) return 0;
        var pockets = PocketContents;
        var (next, seeds) = WithSeeds(pockets, FoodLaws.SeedsPerHarvest);
        if (seeds > 0) SavePockets(next, keepIfUnsaved: true);
        return seeds;
    }
}
