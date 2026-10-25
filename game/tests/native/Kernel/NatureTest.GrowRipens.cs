using Godot;
using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Decision 127, as the founder refined it on 25 October (P-Nature-2, item 1): a Grow cast grows a plant first, up to its cap, and
/// ripens its food only once the plant is at its cap. In the test room with plants (its broadleaf bearing apples under the shipped
/// pack's food block, every plant of a food species bearing): a cast on the tree below its cap grows it and leaves its picked food
/// unripe, even the cast that brings it to its cap; a cast at the cap ripens it and is charged as any Grow (the player's order 4, the
/// Gubble's own idea 2), one room revision with nothing grown; at the cap with its food ripe, or with what was picked grown back, the
/// cast is refused as before, before the price.
/// </summary>
public partial class NatureTest
{
    /// <summary>The shipped pack with every plant of a food species bearing (one_in 1), so the test room's one broadleaf bears.</summary>
    private static IslandRules EveryPlantBears()
    {
        var pack = JsonNode.Parse(Godot.FileAccess.GetFileAsString(IslandRules.PathFor(RoomWorld.DefaultRulesId, ShippedRules.Version)))!.AsObject();
        foreach (var source in pack["food"]!["sources"]!.AsArray()) source!["one_in"] = 1;
        return IslandRules.Parse(Encoding.UTF8.GetBytes(pack.ToJsonString()));
    }

    /// <summary>A plant's first spot picked at a moment, as E on it leaves the island's nature (CommandHostFood's record; picking itself is the food suite's).</summary>
    private void PickedAt(string plant, DateTime at) =>
        _host.Nature.Food[plant] = new NatureFood { Id = plant, Picked = 1, PickedS = (long)Math.Floor((at - DateTime.UnixEpoch).TotalSeconds) };

    private async Task TestGrowRipensAtTheCap()
    {
        await NewWorld(SavePath("grow-ripens"), afterRules: () => _host.UseRules(EveryPlantBears()));
        var spots = _host.FoodPlants.FirstOrDefault(p => p.Id == Tree)?.Spots.Count ?? 0;
        Check(spots > 0 && _host.FoodLeft(Tree).Count == spots, $"the test room's broadleaf bears food under the shipped pack, every plant bearing ({spots} spots)");
        _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f);
        var near = TreeAt + new Vector3(-0.2f, 0.01f, 0);
        JsonObject GrowTree(string principal, bool preview = false) => Send(Command(NextId("ripen"), "nature.cast", Cast("grow", near, 0.5, 1), preview: preview), principal);
        // The 0.95 m tree reaches its cap (0.4 m taller) in two casts (NatureTest.Grow's TestGrowCast).
        var cap = 1 + 0.4f / 0.95f;
        PickedAt(Tree, _now);
        var unripe = _host.FoodLeft(Tree).Count;
        Check(unripe == spots - 1, "one of its spots is picked");
        // Below its cap: the cast grows it and its food stays as it was (each price below from a full pool: NatureTest.Trust's FullPool).
        FullPool();
        var pips = _host.Vitals.Pips;
        var first = GrowTree(Player);
        Check(Ok(first) && Grown(first) == 1 && Factor(Tree) > 1 && Factor(Tree) < cap - 1e-3f && _host.FoodLeft(Tree).Count == unripe && _host.Nature.Food.ContainsKey(Tree) &&
              pips - _host.Vitals.Pips == 4,
            $"a cast on a plant below its cap grows it ({Factor(Tree):0.000}) and does not ripen its food ({_host.FoodLeft(Tree).Count} of {spots} left), at Grow's price: " +
            Canonical(first["error"] ?? first["data"]));
        var second = GrowTree(Player);
        Check(Ok(second) && Grown(second) == 1 && Math.Abs(Factor(Tree) - cap) < 1e-4f && _host.FoodLeft(Tree).Count == unripe,
            $"the cast that brings it to its cap ({Factor(Tree):0.000}) grows it and still does not ripen its food ({_host.FoodLeft(Tree).Count} of {spots})");
        // At its cap: what a cast would do, changing nothing.
        var revision = _host.Revision;
        FullPool();
        pips = _host.Vitals.Pips;
        var preview = GrowTree(Player, preview: true);
        Check(Ok(preview) && Grown(preview) == 0 && _host.FoodLeft(Tree).Count == unripe && _host.Revision == revision && _host.Vitals.Pips == pips,
            "at its cap with food unripe a preview says the cast goes (nothing grows) and changes nothing: " + Canonical(preview["error"] ?? preview["data"]));
        var third = GrowTree(Player);
        Check(Ok(third) && Grown(third) == 0 && _host.FoodLeft(Tree).Count == spots && !_host.Nature.Food.ContainsKey(Tree) && Math.Abs(Factor(Tree) - cap) < 1e-4f &&
              _host.Revision == revision + 1 && pips - _host.Vitals.Pips == 4,
            $"a cast on a plant at its cap ripens its food ({_host.FoodLeft(Tree).Count} of {spots}), grows nothing and is charged as any Grow (4 pips, " +
            $"{pips} to {_host.Vitals.Pips}), one room revision: " + Canonical(third["error"] ?? third["data"]));
        // At its cap with its food ripe: refused as before, before the price.
        pips = _host.Vitals.Pips;
        var fourth = GrowTree(Player);
        Check(Code(fourth) == "invalid_args" && Message(fourth) == "Every plant there is already full size." && _host.Revision == revision + 1 && _host.Vitals.Pips == pips,
            "at its cap with its food ripe the cast is refused as before, before the price: " + Message(fourth));
        // The Gubble's own idea at the cap: the Gubble's price.
        PickedAt(Tree, _now);
        FullPool();
        pips = _host.Vitals.Pips;
        var gubble = GrowTree(Companion);
        Check(Ok(gubble) && Grown(gubble) == 0 && _host.FoodLeft(Tree).Count == spots && pips - _host.Vitals.Pips == 2,
            $"the Gubble's own Grow at the cap ripens the food at its own price (2 pips, {pips} to {_host.Vitals.Pips}): " + Canonical(gubble["error"] ?? gubble["data"]));
        // Picked a day and more ago: everything has grown back, so there is nothing to ripen, and the cast is refused as before.
        PickedAt(Tree, _now - TimeSpan.FromHours(25));
        revision = _host.Revision;
        var grownBack = GrowTree(Player);
        Check(Code(grownBack) == "invalid_args" && Message(grownBack) == "Every plant there is already full size." && _host.Revision == revision && _host.FoodLeft(Tree).Count == spots,
            "with what was picked grown back there is nothing to ripen: refused as before: " + Message(grownBack));
        _host.Nature.Food.Remove(Tree);
        await FreeWorld();
    }
}
