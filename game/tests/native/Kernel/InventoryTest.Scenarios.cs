using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

public partial class InventoryTest
{
    /// <summary>The pack's block: v5's kinds and caps, a pack without it (no pockets, no chest), and packs refused for one reason each.</summary>
    private async Task TestRules()
    {
        await NewWorld("rules");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var rules = _host.InventoryRules;
        Check(rules != null && rules.Kinds.Select(k => k.Id).SequenceEqual(new[] { "apple", "berries", "seed" }) && rules.Kind("apple")!.HealQuarters == 2 &&
              rules.Kind("berries")!.HealQuarters == 1 && rules.Kind("seed")!.HealQuarters == null && rules.Kind("apple")!.Stack == 20 && rules.Kind("apple")!.Words(2) == "Apples" &&
              rules.Kind("berries")!.Words(3) == "Berries" && rules.Pockets == new InventoryCaps(6, 0.3) && rules.Chest == new InventoryCaps(12, 2) && rules.Kinds.All(k => k.Assets.Count == 0),
            "v5 carries apples (half a heart), berries (a quarter) and seeds (no use yet), the pockets' 6 entries and 0.3 kg, the chest's 12 and 2 kg; nothing yields a kind yet");
        _host.LoadRules(RoomWorld.DefaultRulesId, 4);
        var without = Store(Player, "chest");
        var status = Status();
        var listed = Send(Query("capabilities.list", new JsonObject()), Player)["data"]!.AsObject();
        Check(_host.InventoryRules == null && Refused(without, "unsupported_capability", CommandHost.InventoryNotHere, "$.op") && status["pockets"] == null && status["chest"] == null &&
              listed["inventory"] == null && _host.PocketEntries.Count == 0,
            "a pack without the block has no pockets and no chest: the commands are unsupported, and the readers carry neither");
        var refusals = new (string Why, Action<JsonObject> Edit)[]
        {
            ("a kind twice", inventory => inventory["kinds"]!.AsArray().Add(inventory["kinds"]![0]!.DeepClone())),
            ("an asset of two kinds", inventory => inventory["kinds"]![1]!["assets"] = new JsonArray("windfall_apple")),
            ("a field the contract has not", inventory => inventory["kinds"]![0]!["colour"] = "red"),
            ("pockets of seventeen", inventory => inventory["pockets"]!["entries"] = 17),
            ("a heal of no quarters", inventory => inventory["kinds"]![0]!["use"] = new JsonObject { ["heal_quarters"] = 0 }),
        };
        foreach (var (why, edit) in refusals)
        {
            string refused;
            try { Pack(edit); refused = ""; }
            catch (IslandRulesException error) { refused = error.Message; }
            Check(refused.StartsWith("$.inventory", StringComparison.Ordinal), $"a pack whose inventory has {why} is refused whole, naming the block: {refused}");
        }
    }

    /// <summary>
    /// Every companion refusal, each before anything is looked up (decision 100): with "Let the Gubble use the chest" off every store and
    /// take; the pockets always; use and drop are player-only ops. With the switch on the companion's chest path looks things up.
    /// </summary>
    private async Task TestCompanionRefusals()
    {
        await NewWorld("companion");
        var start = _player.GlobalPosition;
        Check(!_host.GubbleChestAllowed, "\"Let the Gubble use the chest\" is off by default");
        // The companion holds nothing and the chest is empty: a lookup would say so; the answer is the switch's, at $.op.
        Check(Refused(Store(Companion, "chest"), "permission_denied", CommandHost.GubbleChestOff, "$.op"), "with the switch off the companion's store is refused at $.op before anything is looked up");
        Check(Refused(Take(Companion, "obj:nothing", "hand"), "permission_denied", CommandHost.GubbleChestOff, "$.op"), "and its take, the same whether or not the thing is there");
        Check(Refused(Store(Companion, "pockets"), "permission_denied", CommandHost.PocketsArePlayers, "$.args.to"), "the pockets are the player's alone: a store into them");
        Check(Refused(Store(Companion, "chest", from: "pockets", item: "apple"), "permission_denied", CommandHost.PocketsArePlayers, "$.args.from"), "and out of them");
        Check(Refused(Take(Companion, "apple", "pockets"), "permission_denied", CommandHost.PocketsArePlayers, "$.args.to"), "and a take into them");
        Check(Refused(Use(Companion, "apple"), "permission_denied", "Only the player can do that, directly in the game.", "$.op") &&
              Refused(Drop(Companion, "obj:cup"), "permission_denied", "Only the player can do that, directly in the game.", "$.op"),
            "use and drop are player-only ops, refused outright");
        var smuggled = Send(Command(NextId("store"), "inventory.store", new JsonObject { ["to"] = "chest", ["actor"] = PlayerAvatar }), Companion, contractValid: false);
        Check(Code(smuggled) == "field_unknown", "no field names an avatar to act through (the player's hand is never the companion's)");
        Check(_host.SetGubbleChest(true) && _host.GubbleChestAllowed, "the player's own control turns the switch on, and the team file keeps it");
        Check(Refused(Store(Companion, "chest"), "request_invalid", CommandHost.HoldSomething, "$.args"), "with it on the companion's store looks: it holds nothing");
        Check(Refused(Take(Companion, "obj:nothing", "hand"), "target_not_found", CommandHost.NotInChest, "$.args.item"), "and its take finds nothing of that id in the chest");
        Check(Refused(Store(Companion, "pockets"), "permission_denied", CommandHost.PocketsArePlayers, "$.args.to") &&
              Refused(Take(Companion, "apple", "pockets"), "permission_denied", CommandHost.PocketsArePlayers, "$.args.to"),
            "the pockets stay the player's whatever the switch says");
        Check(_player.GlobalPosition.DistanceTo(start) < 0.002f && _host.HeldBy(PlayerAvatar) == null, "nothing the companion asked moved the player or filled the player's hand");
    }

    /// <summary>
    /// A unique thing keeps its id (decision 99): held, into the chest (gone from the room, kept as removed with the gathered mark), back
    /// into the hand with the same id, set down; then into the pockets from the chest, carried away from camp and set down there, its pose
    /// saved as a put-down's, and after a reload it is where it was set.
    /// </summary>
    private async Task TestThingRoundTrip()
    {
        await NewWorld("round-trip");
        Check(await PickUp("obj:cup", ByCup), "the player picks the cup up at the camp");
        var can = _host.PlayerCanStore();
        Check(can.Ok && can.To == "chest" && can.Words == "Store Cup in the chest", $"E would store it in the chest first, at a camp (decision 113): {can.Words}");
        var revision = _host.Revision;
        var preview = Store(Player, "chest", preview: true);
        Check(Ok(preview) && preview["preview"]!.GetValue<bool>() && preview["data"] == null && _host.HeldBy(PlayerAvatar) == "obj:cup", "a previewed store changes nothing");
        var store = Command("store-cup", "inventory.store", new JsonObject { ["to"] = "chest" });
        var stored = Send(store, Player);
        Check(Ok(stored) && stored["transient"]!.GetValue<bool>() && stored["affected"]?[0]?.GetValue<string>() == "obj:cup" &&
              Canonical(stored["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "obj:cup", ["to"] = "chest" }) && _host.Revision == revision,
            "the cup goes in the chest: a transient receipt naming it, with data, no room revision: " + Canonical(stored));
        Check(_host.HeldBy(PlayerAvatar) == null && !EntityIds().Contains("obj:cup") && _host.Nature.Objects["obj:cup"] is { Gathered: true, BurnedDown: true } &&
              _host.ChestEntries.Single() is { Item: "obj:cup", Kind: "thing", Name: "Cup", Count: 1 } && Math.Abs(_host.ChestMassKg - 0.05) < 1e-6,
            "the hands are empty, the room no longer lists it, the island holds it away (gathered), and the chest lists it by name and mass");
        Check(Send(store, Player)["replayed"]?.GetValue<bool>() == true && _host.ChestEntries.Count == 1, "a retry replays its receipt and stores nothing twice");
        var saved = _host.TeamBlock()["camp"]?["chest"];
        Check(Canonical(saved) == Canonical(new JsonObject { ["things"] = new JsonArray("obj:cup") }), "the island save's camp block keeps the chest: " + Canonical(saved));
        var taken = Take(Player, "obj:cup", "hand");
        Check(Ok(taken) && taken["transient"]!.GetValue<bool>() && _host.HeldBy(PlayerAvatar) == "obj:cup" && !_host.Nature.Objects.ContainsKey("obj:cup") && _host.ChestEntries.Count == 0,
            "taken back into the hand with the same id: the island holds it again and the chest is empty: " + Canonical(taken));
        Check(_host.TeamBlock()["camp"]?["chest"] == null, "an empty chest writes no key");
        var released = Send(Command(NextId("release"), "entity.release", new JsonObject()), Player);
        Check(Ok(released) && EntityIds().Contains("obj:cup") && _host.HeldBy(PlayerAvatar) == null, "and set down, it is a room object like any other: " + Code(released));

        Check(await PickUp("obj:cup", ByCup) && Ok(Store(Player, "chest")), "picked up and stored again");
        var pocketed = Take(Player, "obj:cup", "pockets");
        Check(Ok(pocketed) && _host.PocketEntries.Single() is { Item: "obj:cup", Kind: "thing", Here: true } && _host.ChestEntries.Count == 0 && _host.Nature.Objects["obj:cup"].Gathered,
            "taken from the chest into the pockets, still away from the room: " + Canonical(pocketed));
        await Stand(_player, AwayFromCamp, "away from the camp");
        Check(Refused(Take(Player, "apple", "pockets"), "target_not_found", CommandHost.NotInChest, "$.args.item") &&
              Refused(Store(Player, "chest", from: "pockets", item: "obj:cup"), "not_ready", CommandHost.ChestAtCamp, "$.args.to"),
            "away from the camp the chest cannot be reached");
        revision = _host.Revision;
        var dropped = Drop(Player, "obj:cup");
        var cup = Send(Query("entity.inspect", new JsonObject { ["target"] = "obj:cup" }), Player)["data"]?["entity"];
        var at = cup?["position_m"] is JsonArray p ? new Vector3((float)p[0]!.GetValue<double>(), (float)p[1]!.GetValue<double>(), (float)p[2]!.GetValue<double>()) : Vector3.Inf;
        Check(Ok(dropped) && dropped["transient"]!.GetValue<bool>() && Canonical(dropped["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "obj:cup", ["to"] = "ground" }) &&
              _host.PocketEntries.Count == 0 && cup != null && at.DistanceTo(_player.GlobalPosition) < 0.2f && Math.Abs(at.Y) < 0.003f && _host.Revision == revision + 1,
            $"set down from the pockets before the player, on the floor ({at.X:0.000}, {at.Y:0.000}, {at.Z:0.000}), its pose a put-down's receipt: " + Canonical(dropped));
        await NewWorld("round-trip", keepFiles: true);
        var after = Send(Query("entity.inspect", new JsonObject { ["target"] = "obj:cup" }), Player)["data"]?["entity"]?["position_m"];
        Check(_host.PocketEntries.Count == 0 && after is JsonArray a && new Vector3((float)a[0]!.GetValue<double>(), (float)a[1]!.GetValue<double>(), (float)a[2]!.GetValue<double>()).DistanceTo(at) < 0.001f,
            "after a reload the cup is where it was set down, and the pockets are empty");
    }

    /// <summary>
    /// The chest is the island save's: it reloads with the island; a save without it reads as an empty chest; room state carries it in
    /// x_play_camp; a listing the island disagrees with (a thing in the room) is let go when read, and the next write keeps it so.
    /// </summary>
    private async Task TestChestSavedAndReconciled()
    {
        await NewWorld("chest-fresh");
        Check(_host.ChestEntries.Count == 0 && _host.TeamBlock()["camp"]?["chest"] == null, "a save without a chest reads as an empty one");
        await NewWorld("chest");
        Check(await PickUp("obj:cup", ByCup) && Ok(Store(Player, "chest")), "the cup goes in the chest");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "chest")) && Units(_host.ChestEntries, "apple") == 1,
            "a held windfall apple goes in the chest as a unit at a camp (decision 113: the chest first there)");
        await NewWorld("chest", keepFiles: true);
        Check(_host.ChestEntries.Select(e => e.Item).SequenceEqual(new[] { "apple", "obj:cup" }) && !EntityIds().Contains("obj:cup") && !EntityIds().Contains("obj:apple_1"),
            "after a reload the chest holds the apple and the cup, and neither is in the room");
        // The inventory's writes move no room revision: a put-down does, so room state's entity revisions fit its store revision (see the report).
        Check(Ok(Send(Command(NextId("place"), "entity.place", new JsonObject { ["target"] = "obj:doorstop", ["placement"] = new JsonObject { ["position_m"] = new JsonArray(-0.3, 0.006, 0.45) } }), Player)),
            "the doorstop is moved a little (a room revision)");
        var state = _host.ExportRoomState()!;
        Check(Canonical(state["extensions"]?["x_play_camp"]?["chest"]) == Canonical(new JsonObject { ["stacks"] = new JsonObject { ["apple"] = 1 }, ["things"] = new JsonArray("obj:cup") }) &&
              state["entities"]?["obj:cup"]?["removed"]?.GetValue<bool>() == true,
            "room state carries the chest in x_play_camp and keeps the cup as a removed entity");
        // A listing the island disagrees with: the doorstop is in the room, so the chest cannot hold it.
        state["extensions"]!["x_play_camp"]!["chest"]!["things"]!.AsArray().Add("obj:doorstop");
        await NewWorld("chest-import");
        var imported = _host.ImportRoomState((JsonObject)state.DeepClone());
        Check(imported.Ok && _host.ChestEntries.Select(e => e.Item).SequenceEqual(new[] { "apple", "obj:cup" }) && EntityIds().Contains("obj:doorstop"),
            "an imported room state brings the chest; a thing it lists that the island holds (the doorstop) is let go when read: " + imported.Message);
        Check(Ok(Take(Player, "apple", "pockets")) && Canonical(_host.TeamBlock()["camp"]?["chest"]) == Canonical(new JsonObject { ["things"] = new JsonArray("obj:cup") }),
            "and the next write keeps the chest as the island has it");
    }

    /// <summary>The caps: a stack's own size, the entries and the mass, for the pockets and the chest (occupied past any), and the counts.</summary>
    private async Task TestStacksAndCaps()
    {
        await NewWorld("caps", editPack: inventory =>
        {
            inventory["kinds"]![0]!["stack"] = 2;
            inventory["pockets"] = new JsonObject { ["entries"] = 2, ["mass_kg"] = 0.1 };
            inventory["chest"] = new JsonObject { ["entries"] = 2, ["mass_kg"] = 2 };
        });
        Check(await PickUp("obj:apple_1", ByApple1), "the player picks up a windfall apple");
        await Stand(_player, AwayFromCamp, "away from the camp, the apple in hand");
        var can = _host.PlayerCanStore();
        Check(can.Ok && can.To == "pockets" && can.Words == "Put Apple in your pockets", "away from the camp E would put a held unit in the pockets: " + can.Words);
        var pocketed = Store(Player, "pockets");
        Check(Ok(pocketed) && Canonical(pocketed["data"]) == Canonical(new JsonObject { ["count"] = 1, ["item"] = "apple", ["to"] = "pockets" }) &&
              pocketed["affected"]?[0]?.GetValue<string>() == "obj:apple_1" && Units(_host.PocketEntries, "apple") == 1 && _host.Nature.Objects["obj:apple_1"].Gathered,
            "the apple goes in the pockets as a unit, anywhere; the windfall has left the island: " + Canonical(pocketed));
        Check(await PickUp("obj:apple_2", ByApple2) && Ok(Store(Player, "pockets")) && Units(_host.PocketEntries, "apple") == 2, "a second apple stacks");
        Check(await PickUp("obj:apple_3", ByApple3), "a third is picked up, at the camp");
        var over = Store(Player, "pockets");
        Check(Refused(over, "occupied", "A stack holds at most 2 Apples.", "$.args") && over["error"]?["allowed"]?.GetValue<int>() == 2 &&
              Units(_host.PocketEntries, "apple") == 2 && _host.HeldBy(PlayerAvatar) == "obj:apple_3" && EntityIds().Contains("obj:apple_3"),
            "a stack over its kind's size is occupied, and nothing moved: the apple stays in hand and in the room");
        Check(_host.PlayerCanStore() is { Ok: true, To: "chest" }, "at the camp E would store it in the chest instead");
        Check(Ok(Send(Command(NextId("release"), "entity.release", new JsonObject()), Player)), "the player sets the third apple down");
        Check(await PickUp("obj:pod", ByPod) && Ok(Store(Player, "pockets")) && _host.PocketEntries.Count == 2 && Units(_host.PocketEntries, "seed") == 1, "a seed pod is a second entry: seeds");
        Check(await PickUp("obj:cup", ByCup), "the cup in hand");
        Check(Refused(Store(Player, "pockets"), "request_invalid", CommandHost.OnlyStacksFromHand, "$.args.to"), "a unique thing does not go from the hand into the pockets");
        Check(Ok(Store(Player, "chest")) && _host.ChestEntries.Count == 1, "it goes in the chest at the camp");
        Check(Refused(Take(Player, "obj:cup", "pockets"), "occupied", "Your pockets are full: they hold 2 things.", "$.args") && _host.ChestEntries.Count == 1,
            "the pockets' entries cap: occupied, and the cup stays in the chest");
        Check(Ok(Store(Player, "chest", from: "pockets", item: "seed")) && _host.PocketEntries.Count == 1 && Units(_host.ChestEntries, "seed") == 1, "the seeds go from the pockets into the chest");
        Check(Ok(Take(Player, "obj:cup", "pockets")) && Math.Abs(_host.PocketMassKg - 0.09) < 1e-6 && _host.ChestEntries.Count == 1, "now the cup fits the pockets: 0.09 kg of 0.1");
        Check(await PickUp("obj:doorstop", new Vector3(-0.3f, 0, 0.62f)) && Ok(Store(Player, "chest")) && _host.ChestEntries.Count == 2, "the doorstop goes in the chest (0.3 kg)");
        Check(Refused(Store(Player, "chest", from: "pockets", item: "obj:cup"), "occupied", "The chest is full: it holds 2 things.", "$.args"), "the chest's entries cap");
        var counted = Store(Player, "chest", from: "pockets", item: "apple", count: 5);
        Check(Refused(counted, "invalid_args", "There are only 2 of them.", "$.args.count") && counted["error"]?["allowed"]?.GetValue<int>() == 2, "a count past what the pockets hold");
        await Stand(_player, AwayFromCamp, "away from the camp");
        Check(Ok(Drop(Player, "obj:cup")) && _host.PocketEntries.Count == 1, "the cup is set down away from the camp");
        await Stand(_player, AtCamp, "back at the camp");
        Check(Refused(Take(Player, "obj:doorstop", "pockets"), "occupied", "That would be too heavy for your pockets: they hold 0.1 kg.", "$.args") && _host.ChestEntries.Count == 2,
            "the pockets' mass cap: the doorstop (0.3 kg) does not fit 0.1 kg");
        Check(Ok(Take(Player, "seed", "pockets", count: 1)) && Units(_host.PocketEntries, "seed") == 1 && Units(_host.ChestEntries, "seed") == 0, "a counted take of a stack");
    }

    /// <summary>The pockets are the team's per mode (decision 98): Creative's are its own, the team file keeps each mode's, and a reload reads them.</summary>
    private async Task TestPocketsPerMode()
    {
        await NewWorld("modes");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")) && Units(_host.PocketEntries, "apple") == 1, "a Challenge apple in the pockets");
        Check(Ok(_host.PlayerMode("creative")) && _host.Mode == "creative" && _host.PocketEntries.Count == 0, "Creative's pockets are its own: empty");
        Check(await PickUp("obj:apple_2", ByApple2) && Ok(Store(Player, "pockets")) && Ok(_host.PlayerMode("challenge")) && Units(_host.PocketEntries, "apple") == 1,
            "a Creative apple, then back in Challenge its one apple");
        var file = TeamFile.Parse(File.ReadAllBytes(TeamPath("modes")));
        Check(file.PocketsOf("challenge").Units("apple") == 1 && file.PocketsOf("creative").Units("apple") == 1 && file.PocketsOf("live").IsEmpty,
            "the team file keeps each mode's pockets");
        await NewWorld("modes", keepFiles: true);
        Check(Units(_host.PocketEntries, "apple") == 1 && _host.Mode == "challenge", "and a reload reads them");
        // A team file without pockets or settings writes the same bytes it always did.
        var plain = TeamFile.Create(TeamId, _now, "Player", TeamFile.PrototypeCostume, "Gubble", "#65b9b0");
        var text = Encoding.UTF8.GetString(plain.Serialize());
        var round = TeamFile.Parse(file.Serialize());
        Check(!text.Contains("pockets", StringComparison.Ordinal) && !text.Contains("settings", StringComparison.Ordinal) && round.Serialize().SequenceEqual(file.Serialize()) &&
              plain.WithPockets("challenge", InventoryContents.Empty) == plain,
            "a team file with no pockets and no switch has neither key, and one with them reads back byte for byte");
        await NewWorld("modes-unfiled", team: false);
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")) && Ok(_host.PlayerMode("creative")) && _host.PocketEntries.Count == 0 &&
              Ok(_host.PlayerMode("challenge")) && Units(_host.PocketEntries, "apple") == 1,
            "without a team (a fixture) the pockets are this session's, per mode all the same");
    }

    /// <summary>
    /// Decision 101: in Challenge a knockout and the deep-water return lose the pockets' stacks, unique things stay, a journal line says
    /// so; a trip home (B) loses nothing; Creative loses nothing; the chest never spills.
    /// </summary>
    private async Task TestLossByMode()
    {
        await NewWorld("loss");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")) && await PickUp("obj:apple_2", ByApple2) &&
              Ok(Store(Player, "pockets")) && await PickUp("obj:apple_3", ByApple3) && Ok(Store(Player, "chest")),
            "two apples in the pockets and one in the chest");
        Check(await PickUp("obj:cup", ByCup) && Ok(Store(Player, "chest")) && Ok(Take(Player, "obj:cup", "pockets")), "and the cup in the pockets");
        _player.RequestHome();
        await Frames(90);
        Check(Units(_host.PocketEntries, "apple") == 2, "a trip home (B) loses nothing");
        var harm = _host.ApplyPlayerDamage(10000, PlayerDamageSource.Fire);
        await Frames(3);
        var line = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Player)["data"]?["entries"]?.AsArray().FirstOrDefault()?["line"]?.GetValue<string>();
        Check(harm is { Recovered: true } && Units(_host.PocketEntries, "apple") == 0 && _host.PocketEntries.Single().Item == "obj:cup" && Units(_host.ChestEntries, "apple") == 1,
            "knocked out in Challenge: the pockets' apples are lost, the cup stays, and the chest never spills");
        Check(line == "You were knocked out and lost the food and seeds from your pockets: 2 Apples. Everything else stayed in your pockets.", "the journal says so: " + line);
        Check(TeamFile.Parse(File.ReadAllBytes(TeamPath("loss"))).PocketsOf("challenge").Stacks.Count == 0, "and the team file keeps the loss");
        Check(Ok(Take(Player, "apple", "pockets")) && Units(_host.PocketEntries, "apple") == 1, "an apple from the chest");
        _host.DeepWaterReturnForTests();
        line = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Player)["data"]?["entries"]?.AsArray().FirstOrDefault()?["line"]?.GetValue<string>();
        Check(Units(_host.PocketEntries, "apple") == 0 && _host.PocketEntries.Count == 1 && line == "The sea took the food and seeds from your pockets: 1 Apple. Everything else stayed in your pockets.",
            "the deep-water return loses them too: " + line);
        await NewWorld("loss-creative", mode: "creative");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")), "an apple in Creative's pockets");
        var hurt = _host.ApplyPlayerDamage(10000, PlayerDamageSource.Fire);
        _host.DeepWaterReturnForTests();
        Check(hurt == null && Units(_host.PocketEntries, "apple") == 1, "Creative loses nothing: no knockout, and the deep-water return keeps the apple");
    }

    /// <summary>Food heals at once (decisions 92 and 111): no unharmed wait; at full hearts the unit stays; a kind with no use; Creative changes nothing.</summary>
    private async Task TestInstantHeal()
    {
        await NewWorld("heal");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")) && await PickUp("obj:apple_2", ByApple2) &&
              Ok(Store(Player, "pockets")) && await PickUp("obj:pod", ByPod) && Ok(Store(Player, "pockets")),
            "two apples and the seeds in the pockets");
        var full = Use(Player, "apple");
        Check(Refused(full, "not_ready", "Your hearts are full: the Apple stays in your pockets.", "$.args.item") && full["error"]!["retryable"]!.GetValue<bool>() && Units(_host.PocketEntries, "apple") == 2,
            "at full hearts the apple stays");
        var max = VitalsRules.For(_host.Rules, _host.Mode).MaxQuarters(_host.Vitals);
        var harm = _host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        var hurt = Quarters;
        var writes = _vitals.Writes;
        var eaten = Use(Player, "apple");
        Check(harm != null && hurt < max - 2 && Ok(eaten) && Quarters == hurt + 2 && Canonical(eaten["data"]) == Canonical(new JsonObject { ["count"] = 1, ["healed_quarters"] = 2, ["item"] = "apple" }) &&
              Units(_host.PocketEntries, "apple") == 1 && _vitals.Writes > writes && VitalsRules.For(_host.Rules, _host.Mode).Quarters(_vitals.Saved.For("challenge")) == hurt + 2,
            $"just hurt ({hurt} of {max} quarters), an apple heals half a heart at once, with no unharmed wait, and the heal is saved: " + Canonical(eaten));
        Check(Refused(Use(Player, "seed"), "request_invalid", "Seeds have no use yet.", "$.args.item"), "seeds have no use yet");
        Check(Refused(Use(Player, "berries"), "target_not_found", CommandHost.NotInPockets, "$.args.item"), "nothing to eat of a kind the pockets do not hold");
        Check(Refused(Drop(Player, "apple"), "unsupported_capability", "Apples cannot be set down yet: use them, or keep them in the chest.", "$.args.item"),
            "a stack's unit has no body in the world yet, so it is not set down");
        await NewWorld("heal-creative", mode: "creative");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")), "an apple in Creative's pockets");
        var health = _host.Vitals.Health;
        var creative = Use(Player, "apple");
        Check(Ok(creative) && Canonical(creative["data"]) == Canonical(new JsonObject { ["count"] = 0, ["healed_quarters"] = 0, ["item"] = "apple" }) &&
              Units(_host.PocketEntries, "apple") == 1 && _host.Vitals.Health == health,
            "in Creative eating changes nothing but the receipt (decision 108), and nothing is lost (decision 101)");
    }

    /// <summary>
    /// The team switch "Let the Gubble use the chest" (decision 100): off by default; on, the companion stores what its own avatar holds and
    /// takes it back into its own hands at a camp; off again, refused; the team file keeps it, and writes as before when it is off.
    /// </summary>
    private async Task TestGubbleChestSwitch()
    {
        await NewWorld("switch");
        var before = File.ReadAllBytes(TeamPath("switch"));
        await Stand(_companion, new Vector3(0.6f, 0, 0.1f), "by the book");
        var grab = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:book", ["actor"] = CompanionAvatar }), Player);
        await Stand(_companion, new Vector3(0.3f, 0, 0.75f), "at the camp, the book in hand");
        Check(Ok(grab) && _host.HeldBy(CompanionAvatar) == "obj:book", "the player has the Gubble pick the book up");
        Check(Refused(Store(Companion, "chest"), "permission_denied", CommandHost.GubbleChestOff, "$.op") && _host.HeldBy(CompanionAvatar) == "obj:book", "off: the Gubble's store is refused");
        Check(_host.SetGubbleChest(true) && TeamFile.Parse(File.ReadAllBytes(TeamPath("switch"))).GubbleChest &&
              Send(Query("capabilities.list", new JsonObject()), Companion)["data"]?["inventory"]?["gubble_chest"]?.GetValue<bool>() == true,
            "on: the team file keeps the switch, and capabilities.list tells the companion");
        var stored = Store(Companion, "chest");
        Check(Ok(stored) && stored["principal"]!.GetValue<string>() == Companion && _host.HeldBy(CompanionAvatar) == null && _host.ChestEntries.Single().Item == "obj:book",
            "the Gubble stores what its own avatar holds into the chest: " + Canonical(stored));
        var taken = Take(Companion, "obj:book", "hand");
        Check(Ok(taken) && _host.HeldBy(CompanionAvatar) == "obj:book" && _host.HeldBy(PlayerAvatar) == null && _host.ChestEntries.Count == 0,
            "and takes it back into its own hands, never the player's");
        Check(Ok(Store(Companion, "chest")) && _host.ChestEntries.Count == 1, "the book back in the chest");
        var heavy = Take(Player, "obj:book", "hand");
        Check(Code(heavy) == "target_too_heavy" && FieldPath(heavy) == "$.args.item" && !_host.PlayerCanTake("obj:book").Ok, "the book is too heavy for the player's hand (0.6 kg of 0.5)");
        Check(_host.SetGubbleChest(false) && !TeamFile.Parse(File.ReadAllBytes(TeamPath("switch"))).GubbleChest, "the player turns the switch off, and the team file keeps that");
        Check(!_host.GubbleChestAllowed && Refused(Take(Companion, "obj:book", "hand"), "permission_denied", CommandHost.GubbleChestOff, "$.op"), "off again: the Gubble's take is refused");
        Check(File.ReadAllBytes(TeamPath("switch")).SequenceEqual(before), "a team file with the switch off writes the bytes it had before it was ever on");
        await NewWorld("switch", keepFiles: true);
        Check(!_host.GubbleChestAllowed && _host.ChestEntries.Single().Item == "obj:book", "after a reload the switch is off and the book is in the chest");
    }

    /// <summary>The Creative copy into Challenge (the menu's) starts with an empty chest, and the things it held are back on the island; the Creative save is untouched.</summary>
    private async Task TestCreativeCopyStrip()
    {
        await NewWorld("strip", mode: "creative");
        Check(await PickUp("obj:doorstop", ByDoorstop), "the player picks up the doorstop");
        await Stand(_player, AtCamp, "at the camp, the doorstop in hand");
        var chested = Store(Player, "chest");
        var pocketed = Take(Player, "obj:doorstop", "pockets");
        Check(Ok(chested) && Ok(pocketed) && _host.PocketEntries.Count == 1 && _host.PocketEntries[0].Item == "obj:doorstop",
            "the doorstop in Creative's pockets (by way of the chest; at 0.3 kg it fills the pockets' 0.3 kg exactly, and fits): " + Canonical(chested) + " " + Canonical(pocketed));
        Check(await PickUp("obj:cup", ByCup) && Ok(Store(Player, "chest")) && await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "chest")),
            "the cup and an apple in Creative's chest");
        await FreeWorld();
        var layout = new TeamLayout(ProjectSettings.GlobalizePath($"{TestRoot}/strip/user"));
        var prefix = _room.ManifestSha256[..16];
        var island = new IslandEntry(_room.RoomId, "Test room", ProjectSettings.GlobalizePath($"{TestRoot}/room/test_room"), prefix, false, false, false);
        var creative = layout.IslandSave(TeamId, "creative", _room.RoomId, prefix);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(creative)!);
        File.Copy(ProjectSettings.GlobalizePath(SavePath("strip")), creative, true);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(layout.TeamFile(TeamId))!);
        File.Copy(TeamPath("strip"), layout.TeamFile(TeamId), true);
        var original = File.ReadAllBytes(creative);
        var team = File.ReadAllBytes(layout.TeamFile(TeamId));
        CarriedBuilds.Copy(layout, TeamId, island, "standard", _now);
        var copy = JsonNode.Parse(File.ReadAllText(layout.IslandSave(TeamId, "challenge", _room.RoomId, prefix)))!.AsObject();
        var source = JsonNode.Parse(Encoding.UTF8.GetString(original))!.AsObject();
        Check(source["team"]?["camp"]?["chest"] != null && copy["team"]?["camp"] is JsonObject camp && camp["chest"] == null && copy["team"]?["mode"]?.GetValue<string>() == "challenge",
            "the copy's camp block has no chest: Challenge starts with an empty one");
        Check(copy["nature"]?["objects"]?["obj:cup"] == null && copy["nature"]?["objects"]?["obj:apple_1"] != null && File.ReadAllBytes(creative).SequenceEqual(original),
            "the cup the chest held is back on the copy's island (the apple, a unit now, stays gone like a gathered log); the Creative save is untouched");
        Check(source["nature"]?["objects"]?["obj:doorstop"]?["gathered"]?.GetValue<bool>() == true && copy["nature"]?["objects"]?["obj:doorstop"] == null &&
              File.ReadAllBytes(layout.TeamFile(TeamId)).SequenceEqual(team) && TeamFile.Parse(team).PocketsOf("creative").Things.Single().Id == "obj:doorstop" &&
              TeamFile.Parse(team).PocketsOf("challenge").IsEmpty,
            "the doorstop in Creative's pockets is back on the copy's island too; Creative keeps its pockets, and Challenge's start empty");
    }

    /// <summary>The readers: player.status's pockets and chest, capabilities.list's inventory, and E's choice at and away from the camp.</summary>
    private async Task TestReaders()
    {
        await NewWorld("readers");
        Check(_host.PlayerCanStore() is { Ok: false } nothing && nothing.Words == CommandHost.HoldSomething && _host.PlayerCanTake() is { Ok: false, Words: "The chest is empty." },
            "E with nothing in hand stores nothing, and the chest is empty");
        Check(await PickUp("obj:apple_1", ByApple1) && Ok(Store(Player, "pockets")) && await PickUp("obj:cup", ByCup) && Ok(Store(Player, "chest")),
            "an apple in the pockets, the cup in the chest");
        var status = Status(Companion);
        var expected = new JsonObject
        {
            ["pockets"] = new JsonObject
            {
                ["entries"] = new JsonArray(new JsonObject { ["count"] = 1, ["heal_quarters"] = 2, ["item"] = "apple", ["kind"] = "stack", ["mass_kg"] = 0.02, ["name"] = "Apple" }),
                ["mass_kg"] = 0.02, ["max_entries"] = 6, ["max_mass_kg"] = 0.3,
            },
            ["chest"] = new JsonObject
            {
                ["entries"] = new JsonArray(new JsonObject { ["count"] = 1, ["item"] = "obj:cup", ["kind"] = "thing", ["mass_kg"] = 0.05, ["name"] = "Cup" }),
                ["mass_kg"] = 0.05, ["max_entries"] = 12, ["max_mass_kg"] = 2,
            },
        };
        Check(Canonical(new JsonObject { ["pockets"] = status["pockets"]?.DeepClone(), ["chest"] = status["chest"]?.DeepClone() }) == Canonical(expected),
            "player.status shows the pockets and the chest, read-only, to the companion too: " + Canonical(status["pockets"]) + " " + Canonical(status["chest"]));
        var listed = Send(Query("capabilities.list", new JsonObject()), Companion)["data"]?["inventory"];
        Check(Canonical(listed) == Canonical(new JsonObject
        {
            ["gubble_chest"] = false, ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "inventory.store", ["player_only"] = false }, new JsonObject { ["op"] = "inventory.take", ["player_only"] = false },
                new JsonObject { ["op"] = "inventory.use", ["player_only"] = true }, new JsonObject { ["op"] = "inventory.drop", ["player_only"] = true }),
        }), "capabilities.list names the four ops with the player-only mark: " + Canonical(listed));
        Check(Send(Query("capabilities.list", new JsonObject { ["category"] = "light" }), Companion)["data"]?["inventory"] == null &&
              Send(Query("capabilities.list", new JsonObject { ["cursor"] = "1" }), Companion)["data"]?["inventory"] == null &&
              Send(Query("capabilities.list", new JsonObject { ["limit"] = 1 }), Companion)["data"]?["inventory"] != null,
            "only the whole list's first answer names them: a category's page or a later one repeats nothing");
        Check(_host.PlayerCanTake() is { Ok: true, To: "chest", Words: "Open the chest" } && _host.PlayerCanTake("obj:cup") is { Ok: true, To: "hand", Words: "Take Cup" } &&
              _host.PlayerCanTake("apple") is { Ok: false } && _host.ChestReachable,
            "at the camp the chest opens, and the cup can come out into the hand");
        await Stand(_player, AwayFromCamp, "away from the camp");
        Check(_host.PlayerCanTake() is { Ok: false } far && far.Words == CommandHost.ChestAtCamp && !_host.ChestReachable, "away from the camp the chest cannot be reached");
        var eaten = _host.PlayerUse("apple");
        Check(Code(eaten) == "not_ready" && Units(_host.PocketEntries, "apple") == 1, "the HUD's use (anywhere: the pockets go everywhere) of an apple at full hearts keeps it: " + Message(eaten));
    }
}
