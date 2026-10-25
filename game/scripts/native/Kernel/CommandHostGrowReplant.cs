using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Replanting (decision 105, opened with seedless growth at two Grow expertise; decision 67's "unless replanted"): Grow on burnt ground
/// returns one plant, the nearest burnt plant record to the cast (a clump of flowers, a tree, a single plant: one record) of a set that
/// sets seed, as a seedling: its burn cleared in Nature.Plants (the manifest untouched), half its natural size, growing through its
/// phases on the island clock as a sprout does. A tree comes back with its trunk, pole and perches at its size, so it is climbed again.
/// The ash in the circle greens at once, as Green ash does (decision 75). A plant with collision (a tree, a solid one) is not replanted
/// while an avatar stands where it would grow: no cast moves an avatar.
/// </summary>
public partial class CommandHost
{
    public const string ReplantStandingWords = "Someone is standing where the plant would grow back: step away from it first.";

    /// <summary>The burnt copies rooted in the circle whose set seeds (contracts/README.md "Plant copies": seeds), nearest the cast first.</summary>
    private List<PlantRef> BurntToReplant(Vector3 center, float radius) =>
        PlantsRootedIn(center, radius).Where(p => p.Set.Seeds && PlantLaws.Grows(p.Set) && Nature.Plants.TryGetValue(p.Id, out var plant) && plant.Burnt)
            .OrderBy(p => CampLaws.Flat(p.Set.Position(p.Row), center)).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    /// <summary>The nearest burnt plant back as a seedling, and the ash in the circle greened (one room revision; grown_plants 1).</summary>
    private JsonObject CastReplant(JsonElement root, IslandAbility ability, Cast cast, PlantRef plant, List<string> ash, string principal, string action, string fingerprint,
        bool preview, string? approvedBy)
    {
        if (AvatarWhereItGrows(plant)) throw new Refusal("target_busy", ReplantStandingWords, "$.args.center_m", retryable: true);
        var sown = SownNowS();
        return CommitGrowKind(root, ability, cast, GrowKindWords.Replant, 1, "The Gubble asks to replant a burnt plant here.", null, (candidate, step) =>
        {
            candidate.RememberPlant(step, plant.Id);
            var record = candidate.Plant(plant.Id);
            record.Burn = 0;
            record.SownS = sown;
            record.Factor = SeedlingFactorNow(sown);
            GreenAsh(candidate, step, ash);
        }, _ => ash.Count > 0 ? new[] { ExpertiseLists.GreenAsh } : Array.Empty<string>(), principal, action, fingerprint, preview, approvedBy);
    }

    /// <summary>Green ash (decision 75): the ash cells given green at once, each remembered by the step.</summary>
    private static void GreenAsh(NatureState candidate, NatureUndoStep step, IEnumerable<string> ash)
    {
        foreach (var key in ash)
        {
            NatureState.RememberCell(step, candidate.Cells, key);
            var cell = candidate.Cells[key];
            cell.Ash = 0;
            cell.Fuel = Math.Max(cell.Fuel, PlantLaws.GreenAshFuel);
        }
    }

    /// <summary>
    /// Whether an avatar stands where a replanted copy's collision would be once it is grown (its natural size): a tree's trunk and pole,
    /// a solid plant's bounds. Plants without collision never are.
    /// </summary>
    private bool AvatarWhereItGrows(PlantRef plant)
    {
        if (plant.Set.Collides == "none") return false;
        foreach (var body in new SmallPlayerController?[] { Player, Companion })
        {
            if (body == null || !IsInstanceValid(body)) continue;
            var feet = body.GlobalPosition;
            var radius = body.BodyRadiusM + 0.01f;
            if (plant.Set.IsTree)
            {
                var tree = plant.Set.Tree(plant.Row);
                var axis = new Vector3(tree.AxisX, feet.Y, tree.AxisZ);
                var across = CampLaws.Flat(axis, feet);
                var overlaps = feet.Y < tree.PoleTopM && feet.Y + body.BodyHeightM > tree.BaseY;
                if (overlaps && across < Math.Max(tree.TrunkRadiusM, tree.PoleRadiusM) + radius) return true;
            }
            else
            {
                var bounds = PlantBounds(plant, 1).Grow(radius);
                if (bounds.Intersects(new Aabb(feet, new Vector3(0, body.BodyHeightM, 0)).Grow(0.001f))) return true;
            }
        }
        return false;
    }
}
