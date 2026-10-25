using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;
using GarageGame.Native.Saves;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// P-Inventory-2's loose food and seeds (docs/game/MECHANICS.md decision 118): a unit dropped lies loose before the player and is picked up
/// again with E or F into its stack; loose units are saved with the island (the nature block) and capped, the oldest going first; they burn
/// in fire; an apple eaten gives a seed, berries one time in three; a harvest's hook gives one; a seed lies loose and is picked up as food is.
/// </summary>
public partial class FoodTest
{
    /// <summary>Open ground half a metre west of tree A, and the way the player faces there (-Z).</summary>
    private static readonly Vector3 OpenGround = TreeAAt + new Vector3(-0.5f, 0, 0.35f);

    private JsonObject Drop(string principal, string item, bool preview = false) =>
        Send(Command(NextId("drop"), "inventory.drop", new JsonObject { ["item"] = item }, preview), principal);

    private JsonObject TakeFromGround(string principal, string item) =>
        Send(Command(NextId("take"), "inventory.take", new JsonObject { ["from"] = "ground", ["item"] = item, ["to"] = "pockets" }), principal);

    /// <summary>Decision 118: a dropped unit lies loose, a thing of its kind in the world; E and F pick it up again into its stack.</summary>
    private async Task TestLooseDropAndPickUp()
    {
        await NewWorld("loose");
        await PickWindfall(0);
        await Face(OpenGround, OpenGround + Vector3.Forward, "on open ground");
        var revision = _host.Revision;
        var preview = Drop(Player, "apple", preview: true);
        Check(Ok(preview) && preview["preview"]?.GetValue<bool>() == true && _host.Revision == revision && _host.LooseUnits.Count == 0 && Units("apple") == 1,
            "a preview of the drop sets nothing down: " + Canonical(preview));
        var dropped = Drop(Player, "apple");
        var feet = _player.GlobalPosition;
        var lies = _host.LooseUnits.FirstOrDefault();
        var ahead = new Vector2(lies.At.X - feet.X, lies.At.Z - feet.Z);
        Check(Ok(dropped) && dropped["transient"]?.GetValue<bool>() == false && _host.Revision == revision + 1 && dropped["affected"] == null &&
              Canonical(dropped["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "apple", ["to"] = "ground" }) && Units("apple") == 0 &&
              _host.LooseUnits.Count == 1 && lies.Kind == "apple" && Mathf.Abs(ahead.Length() - (_player.BodyRadiusM + FoodLaws.LooseAheadM)) < 0.005f && ahead.Y < 0 &&
              Mathf.Abs(lies.At.Y) < 0.005f,
            $"inventory.drop of an apple sets one down before the player, on the ground (one durable revision; the pockets give it): {lies.At} " + Canonical(dropped));
        Check(TeamFile.Parse(File.ReadAllBytes(TeamPath("loose"))).PocketsOf("challenge").Units("apple") == 0, "the team file's pockets no longer hold it");
        Check(_host.FoodSpotsNear(feet, 0.3f).Any(s => s.PositionM.DistanceTo(lies.At) < 1e-4f && s.Look == FoodView.Look.Apple), "the look draws it where it lies, an apple");
        var faced = _host.PlayerCanPick();
        Check(faced is { Ok: true, Words: "Pick up an apple", Source: CommandHost.FromTheGround, Kind: "apple" }, $"E faces it: \"{faced.Words}\"");
        var picked = Dumped(_host.PlayerPick());
        Check(Ok(picked) && picked["op"]?.GetValue<string>() == "inventory.take" && _host.LooseUnits.Count == 0 && Units("apple") == 1 &&
              Canonical(picked["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "apple", ["to"] = "pockets" }),
            "E picks it up again into its stack (the pockets' take from the ground): " + Canonical(picked));
        Check(Ok(Drop(Player, "apple")) && _host.LooseUnits.Count == 1, "set down again");
        var hands = _host.PlayerHands();
        Check(hands is { Ok: true, Message: "Picked up. In your pockets: 1 apple." } && _host.LooseUnits.Count == 0 && Units("apple") == 1, $"F picks it up too: \"{hands.Message}\"");
        Check(Refused(TakeFromGround(Player, "apple"), "target_not_found", CommandHost.NothingLooseInReach, "$.args.item"), "with none left in reach, the take from the ground finds nothing");
        Check(Ok(Drop(Player, "apple")), "set down a third time");
        var companion = TakeFromGround(Companion, "apple");
        Check(Refused(companion, "permission_denied", CommandHost.PocketsArePlayers, "$.args.to") && _host.LooseUnits.Count == 1,
            "the companion never picks a loose unit into the player's pockets: " + Canonical(companion["error"]));
        Check(Refused(Drop(Companion, "apple"), "permission_denied", "Only the player can do that, directly in the game.", "$.op"), "nor drops one");
        // A harvest's hook (nothing calls it yet) and a seed loose.
        Check(_host.FoodHarvested(TreeA) == 1 && Units("seed") == 1 && TeamFile.Parse(File.ReadAllBytes(TeamPath("loose"))).PocketsOf("challenge").Units("seed") == 1,
            "a farmable plant's harvest (the hook farming will call) puts a seed in the pockets");
        await Face(OpenGround + new Vector3(0.2f, 0, 0), OpenGround + new Vector3(0.2f, 0, -1), "beside the apple");
        Check(Ok(Drop(Player, "seed")) && _host.LooseUnits.Count == 2 && _host.LooseUnits[1].Kind == "seed" && Units("seed") == 0, "a seed set down lies loose too");
        Check(_host.PlayerCanPick() is { Ok: true, Words: "Pick up a seed", Kind: "seed" } && Ok(Dumped(_host.PlayerPick())) && Units("seed") == 1 && _host.LooseUnits.Count == 1,
            "and E picks it up again: \"Pick up a seed\"");
        // Saved with the island: a reload keeps the loose apple where it lies; room state carries it.
        var kept = _host.LooseUnits[0];
        await NewWorld("loose", keepFiles: true);
        Check(_host.LooseUnits.Count == 1 && _host.LooseUnits[0].Kind == "apple" && _host.LooseUnits[0].At.DistanceTo(kept.At) < 1e-4f &&
              NatureBlock.Pack(_host.Nature)["loose"] is JsonArray { Count: 1 },
            "a reload keeps the loose apple where it lies (the nature block's loose list)");
        var state = _host.ExportRoomState()!;
        Check(state["extensions"]?["x_play_nature"]?["loose"] is JsonArray { Count: 1 } loose && loose[0]?["kind"]?.GetValue<string>() == "apple", "room state carries it in x_play_nature");
        await NewWorld("loose-import");
        var imported = _host.ImportRoomState((JsonObject)state.DeepClone());
        Check(imported.Ok && _host.LooseUnits.Count == 1, "and it imports: " + imported.Message);
        var tampered = (JsonObject)state.DeepClone();
        tampered["extensions"]!["x_play_nature"]!["loose"]![0]!["position_m"] = new JsonArray(40, 0, 0);
        await NewWorld("loose-tampered");
        Check(!_host.ImportRoomState(tampered).Ok && _host.Revision == 0, "a loose unit outside the room is refused");
    }

    /// <summary>
    /// Decision 132: a seed eating gives that does not fit the pockets lies loose at the player's feet (where a unit set down lies), never lost:
    /// a durable inventory.drop of the seed under the host's own action id, and the use's transient receipt says so (seeds_loose).
    /// </summary>
    private async Task TestSeedDropsLoose()
    {
        await NewWorld("seed-loose", editPack: p => p["inventory"]!["pockets"]!["entries"] = 1);
        await PickWindfall(0);
        await PickWindfall(1);
        Check(Units("apple") == 2 && _host.PocketEntries.Count == 1, "two apples fill the pockets' one slot");
        await Face(OpenGround, OpenGround + Vector3.Forward, "on open ground");
        _host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        var revision = _host.Revision;
        var preview = Send(Command(NextId("use"), "inventory.use", new JsonObject { ["item"] = "apple" }, preview: true), Player);
        Check(Ok(preview) && _host.Revision == revision && _host.LooseUnits.Count == 0 && Units("apple") == 2, "a preview of the eating sets no seed down: " + Canonical(preview));
        var eaten = Use(Player, "apple");
        var feet = _player.GlobalPosition;
        var lies = _host.LooseUnits.FirstOrDefault();
        var ahead = new Vector2(lies.At.X - feet.X, lies.At.Z - feet.Z).Length();
        Check(Ok(eaten) && eaten["transient"]?.GetValue<bool>() == true && Units("apple") == 1 && Units("seed") == 0 &&
              Canonical(eaten["data"]) == Canonical(new JsonObject { ["count"] = 1, ["healed_quarters"] = 2, ["item"] = "apple", ["seeds"] = 0, ["seeds_loose"] = 1, ["sound"] = "eat" }) &&
              _host.LooseUnits.Count == 1 && lies.Kind == "seed" && ahead <= _player.BodyRadiusM + FoodLaws.LooseAheadM + 0.005f && Mathf.Abs(lies.At.Y - feet.Y) < 0.02f,
            $"the apple's seed does not fit, so it lies loose at the player's feet ({ahead:0.000} m ahead), and the use's receipt says so (seeds_loose): " + Canonical(eaten));
        var state = _host.ExportRoomState()!;
        var drop = state["receipts"]!.AsObject().Select(p => p.Value!["result"]!).FirstOrDefault(r => r["op"]?.GetValue<string>() == "inventory.drop");
        Check(_host.Revision == revision + 1 && drop is JsonObject && drop["transient"]?.GetValue<bool>() == false &&
              Canonical(drop["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "seed", ["to"] = "ground" }) && state["extensions"]?["x_play_nature"]?["loose"] is JsonArray { Count: 1 },
            "one durable revision: the seed's own inventory.drop under the host's action id, the island keeping it loose: " + Canonical(drop));
        Check(Refused(TakeFromGround(Player, "seed"), "occupied", "Your pockets are full: one thing.", "$.args") && _host.LooseUnits.Count == 1,
            "with the pockets still full, picking it up is refused in their words and it stays where it lies");
        _host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        var last = Use(Player, "apple");
        Check(Ok(last) && last["data"]?["seeds"]?.GetValue<int>() == 1 && last["data"]?["seeds_loose"] == null && Units("seed") == 1 && _host.LooseUnits.Count == 1,
            "the last apple eaten frees the slot, so its seed goes into the pockets as before (no seeds_loose)");
        Check(Ok(TakeFromGround(Player, "seed")) && Units("seed") == 2 && _host.LooseUnits.Count == 0, "and the loose one is picked up again into the seeds' stack");
        // Creative eats as well (decision 125): the seed that does not fit lies loose there too.
        await NewWorld("seed-loose-creative", mode: "creative", editPack: p => p["inventory"]!["pockets"]!["entries"] = 1);
        await PickWindfall(0);
        await PickWindfall(1);
        await Face(OpenGround, OpenGround + Vector3.Forward, "on open ground, in Creative");
        var creative = Use(Player, "apple");
        Check(Ok(creative) && creative["data"]?["seeds_loose"]?.GetValue<int>() == 1 && creative["data"]?["seeds"]?.GetValue<int>() == 0 && _host.LooseUnits.Count == 1 &&
              _host.LooseUnits[0].Kind == "seed" && Units("apple") == 1,
            "in Creative the apple's seed lies loose at the feet too: " + Canonical(creative));
    }

    /// <summary>At most 256 loose units an island, the oldest going first; a block past the cap is refused.</summary>
    private void TestLooseCap()
    {
        var state = new NatureState();
        for (var i = 0; i < NatureState.MaxLoose + 44; i++) state.Drop(new NatureLoose { Kind = "seed", PositionM = new Vector3(i * 0.001f, 0, 0) });
        Check(state.Loose.Count == 256 && Mathf.Abs(state.Loose[0].PositionM.X - 0.044f) < 1e-6f && Mathf.Abs(state.Loose[^1].PositionM.X - 0.299f) < 1e-6f,
            "the island keeps 256 loose units at most, the oldest despawning first");
        var block = NatureBlock.Pack(state);
        Check(NatureBlock.Problem(block, 0, null) == null && NatureBlock.Unpack(block).Loose.Count == 256, "256 pack and unpack");
        ((JsonArray)block["loose"]!).Add(new JsonObject { ["kind"] = "seed", ["position_m"] = new JsonArray(0, 0, 0) });
        Check(NatureBlock.Problem(block, 0, null) is { } why && why.Contains("loose"), "a 257th is refused: " + NatureBlock.Problem(block, 0, null));
    }

    /// <summary>Loose units burn in fire: one dropped in the flames is gone; one away from them stays.</summary>
    private async Task TestLooseBurns()
    {
        await NewWorld("loose-fire", mode: "creative");
        await PickWindfall(0, TreeB);
        await PickWindfall(1, TreeB);
        var near = TreeBAt + new Vector3(-0.3f, 0, 0.1f);
        await Face(near, near + Vector3.Forward, "by tree B");
        Check(Ok(Drop(Player, "apple")), "an apple set down by tree B");
        var inFire = _host.LooseUnits[0].At;
        await Face(OpenGround, OpenGround + Vector3.Forward, "by tree A, far from tree B");
        Check(Ok(Drop(Player, "apple")) && _host.LooseUnits.Count == 2, "and one by tree A");
        Check(_player.TryTeleportTo(_room.SpawnFor("player").PositionM), "the player steps back to the spawn");
        _companion.GlobalPosition = TreeBAt + new Vector3(-0.6f, 0.01f, -0.3f);
        await Frames(4);
        var lit = Fire(inFire + new Vector3(0, 0.01f, 0), 0.2, 30);
        for (var step = 0; step < 120 && _host.Nature.Fire != null; step++) Steps(1);
        await Frames(2);
        Check(Ok(lit) && _host.LooseUnits.Count == 1 && _host.LooseUnits[0].At.DistanceTo(inFire) > 0.5f,
            "the fire burns the apple lying in its flames; the one by tree A stays: " + Canonical(lit["error"]));
    }

    /// <summary>Decision 118's seeds: an apple eaten always gives one; berries one time in three, the same answer for the same eating.</summary>
    private void TestSeedRule()
    {
        var berries = Enumerable.Range(1, 300).Count(i => CommandHost.SeedsFromEating("berries", $"use-{i}") == 1);
        Check(Enumerable.Range(1, 50).All(i => CommandHost.SeedsFromEating("apple", $"use-{i}") == 1) && berries is > 70 and < 130 &&
              Enumerable.Range(1, 300).All(i => CommandHost.SeedsFromEating("berries", $"use-{i}") == CommandHost.SeedsFromEating("berries", $"use-{i}")) &&
              CommandHost.SeedsFromEating("seed", "use-1") == 0,
            $"an apple gives a seed every time; berries one time in three by the eating's id ({berries} of 300), always the same for one id; a seed none");
    }
}
