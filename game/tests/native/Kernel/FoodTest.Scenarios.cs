using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;
using GarageGame.Native.Saves;

namespace GarageGame.Tests.Kernel;

public partial class FoodTest
{
    private const string EverythingPicked = "Everything on it has been picked: it grows back within a day.";
    private const string PlayerOnly = "Only the player can do that, directly in the game.";

    /// <summary>Whole seconds of UTC, as the island clock saves a pick.</summary>
    private static long Seconds(DateTime at) => (long)Math.Floor((at - DateTime.UnixEpoch).TotalSeconds);

    private static JsonObject Clock(int dayScale) => new() { ["zone_id"] = "Etc/UTC", ["day_scale"] = dayScale, ["seasons_per_year"] = 4, ["phase_h"] = 0, ["hemisphere"] = "north" };

    // ---- the pack ----

    /// <summary>Item 1's data: v5's food block as D1 and decision 108 have it, and the pack refused for each thing a schema cannot say.</summary>
    private void TestRules()
    {
        var shipped = Pack(null);
        var food = shipped.Food;
        var apples = food?.For("broadleaf");
        Check(food is { RegrowH: 24 } && food.Sources.Count == 3 && apples is { Kind: "apple", OneIn: 3, Windfalls: (2, 4), Canopy: (1, 2), Clusters: (0, 0) } &&
              food.For("shrub") is { Kind: "berries", OneIn: 4, Clusters: (1, 1) } && food.For("heather") is { Kind: "berries", OneIn: 4, Clusters: (1, 1) } && food.For("grass") == null,
            "v5's food: apples on one broadleaf in three (two to four windfalls, one or two in the canopy), berries on one shrub and one heather in four, all back in 24 island hours");
        var refusals = new (string Why, Action<JsonObject> Edit)[]
        {
            ("food with no inventory to pick into", p => p.Remove("inventory")),
            ("a kind the inventory does not have", p => p["food"]!["sources"]![0]!["kind"] = "pear"),
            ("a species named twice", p => p["food"]!["sources"]!.AsArray().Add(p["food"]!["sources"]![0]!.DeepClone())),
            ("a range whose least is above its most", p => p["food"]!["sources"]![0]!["windfalls"] = new JsonArray(3, 2)),
            ("a source that bears nothing", p => p["food"]!["sources"]![1]!["clusters"] = new JsonArray(0, 0)),
            ("a source that bears more than eight", p => p["food"]!["sources"]![0]!["windfalls"] = new JsonArray(8, 8)),
            ("a count past eight", p => p["food"]!["sources"]![1]!["clusters"] = new JsonArray(1, 9)),
            ("a field the contract does not define", p => p["food"]!["sources"]![0]!["fruit"] = "red"),
            ("one in none", p => p["food"]!["sources"]![0]!["one_in"] = 0),
            ("no time to grow back", p => p["food"]!["regrow_h"] = 0),
            ("a month to grow back", p => p["food"]!["regrow_h"] = 721),
        };
        foreach (var (why, edit) in refusals)
        {
            string? refused = null;
            try { Pack(edit); }
            catch (IslandRulesException error) { refused = error.Message; }
            Check(refused != null, $"the pack is refused for {why}: {refused}");
        }
    }

    /// <summary>Item 1's layout: from the plant's id alone, the same on every load; where each unit lies on its plant.</summary>
    private void TestLayout()
    {
        var rules = Pack(p => { foreach (var source in p["food"]!["sources"]!.AsArray()) source!["one_in"] = 1; }).Food!;
        var copies = _room.Copies!;
        FoodPlant? Of(string id) => copies.TryResolve(id, out var set, out var row) ? FoodLayout.Of(set, row, rules) : null;
        var tree = Of(TreeA)!;
        var shape = tree.Set.Tree(tree.Row);
        var windfalls = tree.Spots.Where(s => s.Place == FoodPlace.Windfall).ToList();
        var canopy = tree.Spots.Where(s => s.Place == FoodPlace.Canopy).ToList();
        Check(windfalls.Count is >= 2 and <= 4 && canopy.Count is >= 1 and <= 2 && tree.Spots.Count == windfalls.Count + canopy.Count && tree.Source.Kind == "apple",
            $"tree A bears {windfalls.Count} windfalls and {canopy.Count} canopy apples");
        var foot = windfalls.Select(s => new Vector2(TreeAAt.X + s.OffsetM.X - shape.AxisX, TreeAAt.Z + s.OffsetM.Z - shape.AxisZ).Length()).ToList();
        Check(windfalls.All(s => s.OffsetM.Y == 0) && foot.All(d => d >= shape.TrunkRadiusM + FoodLaws.AppleM * 0.5f + FoodLaws.WindfallGapMinM - 1e-5f &&
                                                                    d <= shape.TrunkRadiusM + FoodLaws.AppleM * 0.5f + FoodLaws.WindfallGapMaxM + 1e-5f),
            "the windfalls lie on the ground at the trunk's foot: " + string.Join(", ", foot.Select(d => d.ToString("0.000"))));
        var cap = shape.Caps[0];
        Check(canopy.All(s => s.OffsetM.Y <= FoodLaws.CanopyHopM + 1e-5f && s.OffsetM.Y > SandboxReachUpM && s.HangM >= FoodLaws.StalkM &&
                              Math.Abs(s.OffsetM.Y + FoodLaws.AppleM + s.HangM - (cap.RimM - 0.001f)) < 1e-4f &&
                              new Vector2(TreeAAt.X + s.OffsetM.X - cap.X, TreeAAt.Z + s.OffsetM.Z - cap.Z).Length() <= Math.Max(cap.RimHalfXM, cap.RimHalfZM)),
            "the canopy apples hang under the crown on their stalks, above a standing body's reach and within a hop's: " +
            string.Join(", ", canopy.Select(s => $"{s.OffsetM.Y:0.000} m up, stalk {s.HangM:0.000} m")));
        var shrub = Of(Shrub)!;
        var heather = Of(Heather)!;
        Check(shrub.Spots.Count == 1 && shrub.Spots[0] is { Place: FoodPlace.Cluster } berries && berries.OffsetM.Y > 0 && berries.OffsetM.Y <= FoodLaws.ClusterHeightM + 1e-5f &&
              heather.Spots.Count == 1 && Math.Abs(heather.Spots[0].OffsetM.Y - (0.025f - FoodLaws.ClusterM)) < 1e-5f && shrub.Source.Kind == "berries" && heather.Source.Kind == "berries",
            $"a shrub and a heather clump bear a cluster of berries each, at the player's height ({shrub.Spots[0].OffsetM.Y:0.000} m) or on the low clump ({heather.Spots[0].OffsetM.Y:0.000} m)");
        Check(Of(Tuft) == null && Of(TreeB) is { } other && !other.Spots.SequenceEqual(tree.Spots), "a grass tuft bears nothing, and each tree its own spots, from its id");
        var again = RoomData.Load($"{TestRoot}/room/test_room").Copies!;
        Check(FoodLayout.All(again, rules).Select(Describe).SequenceEqual(FoodLayout.All(copies, rules).Select(Describe)) && Describe(Of(TreeA)!) == Describe(tree),
            "the same on another load of the room");
        // The hash every island's food rests on: changing it moves the food on every island, so it is pinned.
        Check(FoodLayout.Hash("food|plant:broadleaf-0") == PinnedHash, $"the layout's hash is the pinned one (FNV-1a, SplitMix64's finaliser): {FoodLayout.Hash("food|plant:broadleaf-0")}");
    }

    private const ulong PinnedHash = 8071041625701090528UL;

    /// <summary>A standing body reaches 3.5 of its 10 cm heights up (SandboxRules.ReachUpBodyHeights): a canopy apple needs a hop.</summary>
    private const float SandboxReachUpM = 0.35f;

    private static string Describe(FoodPlant plant) =>
        plant.Id + ":" + string.Join(";", plant.Spots.Select(s => System.FormattableString.Invariant($"{s.Place}@{s.OffsetM.X:0.#####},{s.OffsetM.Y:0.#####},{s.OffsetM.Z:0.#####}/{s.HangM:0.#####}")));

    /// <summary>Item 1 on the stand-in garage (the 1x landscape fixture): v5's choice, deterministic, and its counts.</summary>
    private void TestGarage()
    {
        if (_landscape == null)
        {
            GD.Print("FOOD_GARAGE skipped: pass -- --landscape=DIR (the 1x garage fixture)");
            return;
        }
        var rules = Pack(null).Food!;
        var first = RoomData.Load(_landscape);
        var second = RoomData.Load(_landscape);
        var plants = FoodLayout.All(first.Copies!, rules).ToList();
        Check(plants.Select(Describe).SequenceEqual(FoodLayout.All(second.Copies!, rules).Select(Describe)), "on the garage the food is the same on every load");
        int Count(string species) => first.Copies!.Sets.Where(s => s.Species == species).Sum(s => s.Count);
        int Bearing(string species) => plants.Count(p => p.Set.Species == species);
        var (broadleaf, shrubs, heathers) = (Count("broadleaf"), Count("shrub"), Count("heather"));
        var (trees, bushes, clumps) = (Bearing("broadleaf"), Bearing("shrub"), Bearing("heather"));
        var units = plants.Sum(p => p.Spots.Count);
        GD.Print($"FOOD_GARAGE {trees} of {broadleaf} broadleaf trees bear apples ({plants.Where(p => p.Source.Kind == "apple").Sum(p => p.Spots.Count)} apples), " +
                 $"{bushes} of {shrubs} shrubs and {clumps} of {heathers} heather clumps bear berries; {units} units in all");
        Check(broadleaf > 0 && trees >= broadleaf / 6 && trees <= broadleaf / 2 && bushes >= shrubs / 8 && bushes <= shrubs * 3 / 8 && clumps >= heathers / 8 && clumps <= heathers * 3 / 8,
            $"about one broadleaf in three and one shrub and heather clump in four bear ({trees}/{broadleaf}, {bushes}/{shrubs}, {clumps}/{heathers})");
        Check(plants.Where(p => p.Source.Kind == "apple").All(p => p.Spots.Count(s => s.Place == FoodPlace.Windfall) is >= 2 and <= 4 && p.Spots.Count(s => s.Place == FoodPlace.Canopy) is >= 1 and <= 2) &&
              plants.Where(p => p.Source.Kind == "berries").All(p => p.Spots.Count == 1 && p.Spots[0].Place == FoodPlace.Cluster),
            "each apple tree bears two to four windfalls and one or two canopy apples; each berry plant one cluster");
        Check(plants.All(p => Enumerable.Range(0, p.Spots.Count).All(i => first.Bounds.Grow(0.05f).HasPoint(p.At(i, 1)))) &&
              plants.SelectMany(p => p.Spots.Where(s => s.Place == FoodPlace.Canopy)).All(s => s.OffsetM.Y <= FoodLaws.CanopyHopM + 1e-5f),
            "every unit lies inside the island, every canopy apple within a hop of its tree's root");
        // The kernel's cost for the view's ask (FoodLayout.Near over 6 m), at twenty spots across the garage.
        var clock = Stopwatch.StartNew();
        var found = 0;
        for (var i = 0; i < 20; i++)
        {
            var x = Mathf.Lerp(first.Bounds.Position.X, first.Bounds.End.X, (i + 0.5f) / 20);
            var z = Mathf.Lerp(first.Bounds.Position.Z, first.Bounds.End.Z, (i * 7 % 20 + 0.5f) / 20);
            found += FoodLayout.Near(first.Copies!, rules, x, z, FoodView.DrawM).Count();
        }
        var ms = clock.Elapsed.TotalMilliseconds / 20;
        GD.Print(System.FormattableString.Invariant($"FOOD_GARAGE_ASK {ms:0.000} ms an ask on average over 20 spots ({found / 20.0:0.0} food plants within {FoodView.DrawM} m)"));
        Check(ms < 5, $"an ask of the food within {FoodView.DrawM} m costs {ms:0.000} ms on the garage");
    }

    // ---- item 2: picking ----

    /// <summary>E facing a windfall picks one into the pockets: the prompt's words, a preview, the durable receipt, the record, the team file.</summary>
    private async Task TestPick()
    {
        await NewWorld("pick");
        var tree = Layout(TreeA)!;
        var windfalls = Spots(TreeA, FoodPlace.Windfall);
        var far = Pick(Player, TreeA);
        Check(Refused(far, "out_of_bounds", "That is out of reach. Move closer.", "$.args.target") && !_host.PlayerCanPick().Ok && _host.PlayerCanPick().Source == null,
            "from the spawn the tree's food is out of reach, and E offers nothing");
        var (food, stand) = Approach(TreeA, windfalls[0]);
        await Face(stand, food, "facing the first windfall");
        var faced = _host.PlayerCanPick();
        Check(faced.Ok && faced.Words == "Pick an apple" && faced.Source == TreeA && faced.Kind == "apple" && faced.At is { } at && at.DistanceTo(tree.Box(windfalls[0], 1).GetCenter()) < 1e-4f,
            $"E faces the windfall: \"{faced.Words}\", the tree and where the apple lies");
        var revision = _host.Revision;
        var preview = Dumped(_host.PlayerPick(preview: true));
        Check(Ok(preview) && preview["preview"]?.GetValue<bool>() == true && _host.Revision == revision && Units("apple") == 0 && _host.FoodLeft(TreeA).Count == tree.Spots.Count,
            "a preview picks nothing: " + Canonical(preview));
        var picked = Dumped(_host.PlayerPick());
        var record = _host.Nature.Food.GetValueOrDefault(TreeA);
        Check(Ok(picked) && picked["transient"]?.GetValue<bool>() == false && picked["revision"]?.GetValue<int>() == revision + 1 && _host.Revision == revision + 1 &&
              Canonical(picked["affected"]) == Canonical(new JsonArray(TreeA)) && picked["data"] == null && Units("apple") == 1 && !_host.FoodLeft(TreeA).Contains(windfalls[0]) &&
              record is { } kept && kept.Picked == 1 << windfalls[0] && kept.PickedS == Seconds(_now),
            "E picks it into the pockets: one durable room revision naming the tree, the spot picked and when: " + Canonical(picked));
        var team = TeamFile.Parse(File.ReadAllBytes(TeamPath("pick")));
        Check(team.PocketsOf("challenge").Units("apple") == 1, "the team file's pockets hold the apple");
        var lookup = Send(Query("receipt.lookup", new JsonObject { ["action_id"] = picked["action_id"]!.GetValue<string>() }), Player);
        Check(lookup["data"]?["receipt"]?["revision"]?.GetValue<int>() == revision + 1 && Canonical(lookup["data"]?["receipt"]?["affected"]) == Canonical(new JsonArray(TreeA)),
            "receipt.lookup finds the pick, the tree in affected: " + Canonical(lookup["data"]));
        foreach (var spot in windfalls.Skip(1))
        {
            var (next, from) = Approach(TreeA, spot);
            await Face(from, next, $"facing windfall {spot}");
            var more = Dumped(_host.PlayerPick());
            Check(Ok(more) && !_host.FoodLeft(TreeA).Contains(spot), $"E picks windfall {spot} too: " + Canonical(more["error"]));
        }
        Check(Units("apple") == windfalls.Count && _host.PocketEntries.Count == 1, $"the {windfalls.Count} apples are one stack in the pockets");
        Check(Refused(Pick(Player, Tuft), "target_not_found", CommandHost.NoFoodOnIt, "$.args.target"), "a grass tuft bears no food");
        Check(Refused(Pick(Player, "plant:broadleaf-9"), "target_not_found", "That is not in this room.", "$.args.target"), "a plant past the room's table is not in this room");
        Check(Refused(Pick(Player, "fractal:k3x9q2"), "target_not_found", "That is not in this room.", "$.args.target"), "a Fractal's id still names a Fractal (none here)");
        var thing = Send(Command(NextId("pick"), "item.pickup", new JsonObject { ["target"] = "obj:book" }), Player, contractValid: false);
        Check(Refused(thing, "request_invalid", "item.pickup names a Fractal, or a plant to pick food from.", "$.args.target"), "item.pickup names a Fractal or a plant, never a thing");
    }

    /// <summary>Decision 108: the canopy's fruit is reached with a hop; then everything on the tree is picked until it grows back.</summary>
    private async Task TestCanopyWithAHop()
    {
        await NewWorld("canopy");
        var tree = Layout(TreeA)!;
        foreach (var spot in Spots(TreeA, FoodPlace.Windfall))
        {
            var (food, stand) = Approach(TreeA, spot);
            await Face(stand, food, $"facing windfall {spot}");
            Check(Ok(_host.PlayerPick()), $"windfall {spot} picked first");
        }
        foreach (var spot in Spots(TreeA, FoodPlace.Canopy))
        {
            var apple = tree.At(spot, 1);
            await Face(new Vector3(apple.X, 0, apple.Z), TreeAAt, $"under canopy apple {spot}");
            var standing = Pick(Player, TreeA);
            Check(Refused(standing, "out_of_bounds", CommandHost.FoodHopForIt, "$.args.target") && standing["error"]!["retryable"]!.GetValue<bool>() && _host.PlayerCanPick().Source == null,
                $"standing under it, canopy apple {spot} ({apple.Y:0.000} m up) is out of reach: hop for it");
            // The top of a hop: the body lifted by its hop's apex (no frame between, so it has not fallen yet).
            var ground = _player.GlobalPosition;
            _player.GlobalPosition = ground + Vector3.Up * _player.JumpApexNowM;
            var faced = _host.PlayerCanPick();
            var hopped = Dumped(_host.PlayerPick());
            _player.GlobalPosition = ground;
            Check(faced.Ok && faced.Source == TreeA && Ok(hopped) && !_host.FoodLeft(TreeA).Contains(spot),
                $"at the top of a hop ({_player.JumpApexNowM * 100:0.0} cm) E reaches it and picks it: " + Canonical(hopped["error"]));
            await Frames(4);
        }
        Check(_host.FoodLeft(TreeA).Count == 0 && Units("apple") == tree.Spots.Count && Refused(Pick(Player, TreeA), "not_ready", EverythingPicked, "$.args.target"),
            $"all {tree.Spots.Count} apples are in the pockets, and the tree has none left until it grows back");
    }

    /// <summary>Decision 113: full pockets say so, and nothing is picked.</summary>
    private async Task TestFullPockets()
    {
        await NewWorld("full", editPack: p => p["inventory"]!["pockets"]!["entries"] = 1);
        var (apple, byApple) = Approach(TreeA, Spots(TreeA, FoodPlace.Windfall)[0]);
        await Face(byApple, apple, "facing a windfall");
        Check(Ok(_host.PlayerPick()) && Units("apple") == 1, "an apple fills the pockets' one entry");
        var (berries, byBerries) = Approach(Shrub, 0);
        await Face(byBerries, berries, "facing the shrub's berries");
        var revision = _host.Revision;
        var faced = _host.PlayerCanPick();
        const string full = "Your pockets are full: they hold 1 things.";
        Check(!faced.Ok && faced.Words == full && faced.Source == Shrub && faced.Kind == "berries", $"E says the pockets are full: \"{faced.Words}\"");
        var refused = Dumped(_host.PlayerPick());
        Check(Refused(refused, "occupied", full, "$.args") && _host.Revision == revision && _host.FoodLeft(Shrub).Count == 1 && Units("berries") == 0 && Units("apple") == 1,
            "and E picks nothing: occupied, in the pockets' words: " + Canonical(refused));
        var (second, bySecond) = Approach(TreeA, Spots(TreeA, FoodPlace.Windfall)[1]);
        await Face(bySecond, second, "facing a second windfall");
        Check(_host.PlayerCanPick().Ok && Ok(_host.PlayerPick()) && Units("apple") == 2, "another apple still joins its stack");
    }

    /// <summary>The companion never picks (no op targets the player's hand or pockets) and is shown no food.</summary>
    private async Task TestCompanion()
    {
        await NewWorld("companion");
        var (food, stand) = Approach(TreeA, Spots(TreeA, FoodPlace.Windfall)[0]);
        await Face(stand, food, "facing a windfall");
        _companion.GlobalPosition = stand + new Vector3(0.05f, 0.01f, 0.05f);
        var revision = _host.Revision;
        var refused = Pick(Companion, TreeA);
        var unknown = Pick(Companion, "plant:broadleaf-9");
        var bare = Pick(Companion, Tuft);
        Check(Refused(refused, "permission_denied", PlayerOnly, "$.op") && Canonical(unknown["error"]) == Canonical(refused["error"]) && Canonical(bare["error"]) == Canonical(refused["error"]) &&
              _host.Revision == revision && _host.FoodLeft(TreeA).Count == Layout(TreeA)!.Spots.Count && Units("apple") == 0,
            "the companion's pick is refused before anything is looked up, the same for a tree, a tuft or no plant at all: " + Canonical(refused["error"]));
        var stored = Send(Command(NextId("store"), "inventory.store", new JsonObject { ["to"] = "pockets" }), Companion);
        Check(Refused(stored, "permission_denied", CommandHost.PocketsArePlayers, "$.args.to"), "nor can it put anything in the player's pockets");
        var entities = Send(Query("entities.list", new JsonObject { ["limit"] = 100 }), Companion);
        var capabilities = Send(Query("capabilities.list", new JsonObject()), Companion);
        Check(Ok(entities) && Ok(capabilities) && !Canonical(entities).Contains("food") && !Canonical(entities).Contains("apple") && !Canonical(entities).Contains("berr") &&
              !Canonical(capabilities).Contains("item.pickup") && !Canonical(capabilities).Contains("food"),
            "the companion's view of the room points no food out, and its capabilities offer no pick");
    }

    // ---- item 3: eating ----

    /// <summary>Decisions 92, 108 and 111: an apple two quarters and berries one, at once; the unit kept at full hearts; Creative's sound.</summary>
    private async Task TestEat()
    {
        await NewWorld("eat");
        await PickWindfall(0);
        var (berries, byBerries) = Approach(Shrub, 0);
        await Face(byBerries, berries, "facing the shrub's berries");
        Check(_host.PlayerCanPick() is { Ok: true, Words: "Pick berries" } && Ok(Dumped(_host.PlayerPick())) && Units("berries") == 1, "E: pick berries, into the pockets");
        _sounds.Clear();
        var played = _host.FoodSoundsPlayed;
        var full = Use(Player, "apple");
        Check(Refused(full, "not_ready", "Your hearts are full: the Apple stays in your pockets.", "$.args.item") && Units("apple") == 1 && _sounds.Count == 0 && _host.FoodSoundsPlayed == played,
            "at full hearts the apple stays in the pockets, and nothing is eaten");
        var max = VitalsRules.For(_host.Rules, _host.Mode).MaxQuarters(_host.Vitals);
        var harm = _host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        var hurt = Quarters;
        var eaten = Use(Player, "apple");
        Check(harm != null && hurt <= max - 3 && Ok(eaten) && Quarters == hurt + 2 && Units("apple") == 0 &&
              Canonical(eaten["data"]) == Canonical(new JsonObject { ["count"] = 1, ["healed_quarters"] = 2, ["item"] = "apple", ["sound"] = "eat" }) &&
              _sounds.SequenceEqual(new[] { "apple" }) && _host.FoodSoundsPlayed == played + 1,
            $"just hurt ({hurt} of {max} quarters), the apple heals half a heart at once with no unharmed wait, and makes its sound: " + Canonical(eaten));
        var munched = Use(Player, "berries");
        Check(Ok(munched) && Quarters == hurt + 3 && Canonical(munched["data"]) == Canonical(new JsonObject { ["count"] = 1, ["healed_quarters"] = 1, ["item"] = "berries", ["sound"] = "eat" }) &&
              _sounds.SequenceEqual(new[] { "apple", "berries" }),
            "berries heal a quarter, at once, with their sound");
        await NewWorld("eat-creative", mode: "creative");
        await PickWindfall(0);
        var health = _host.Vitals.Health;
        var creative = Use(Player, "apple");
        Check(Ok(creative) && Canonical(creative["data"]) == Canonical(new JsonObject { ["count"] = 0, ["healed_quarters"] = 0, ["item"] = "apple", ["sound"] = "eat" }) &&
              _host.Vitals.Health == health && Units("apple") == 1 && _sounds.LastOrDefault() == "apple",
            "in Creative eating heals nothing, keeps its sound, and the receipt says so (decision 108; nothing in the inventory is lost there, decision 101): " + Canonical(creative));
    }

    private async Task PickWindfall(int index, string plant = TreeA)
    {
        var (food, stand) = Approach(plant, Spots(plant, FoodPlace.Windfall)[index]);
        await Face(stand, food, $"facing windfall {index}");
        Check(Ok(Dumped(_host.PlayerPick())), $"windfall {index} picked");
    }

    // ---- item 4: growing back ----

    /// <summary>A day by the island clock restocks a plant; a day away does it too; the clock's day scale counts; a new pick starts afresh.</summary>
    private async Task TestRegrow()
    {
        await NewWorld("regrow");
        var spot = Spots(TreeA, FoodPlace.Windfall)[0];
        var at = Layout(TreeA)!.At(spot, 1);
        bool Drawn() => _host.FoodSpotsNear(TreeAAt, 1).Any(s => s.PositionM.DistanceTo(at) < 1e-4f);
        await PickWindfall(0);
        var picked = _now;
        Check(!Drawn() && !_host.FoodLeft(TreeA).Contains(spot), "a picked spot shows nothing");
        _now = picked + TimeSpan.FromHours(24) - TimeSpan.FromSeconds(2);
        Check(!_host.FoodLeft(TreeA).Contains(spot) && !Drawn(), "a moment short of a day it has not grown back");
        _now = picked + TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1);
        Check(_host.FoodLeft(TreeA).Count == Layout(TreeA)!.Spots.Count && Drawn(), "a day after the pick the apple is back, drawn again");
        await PickWindfall(1);
        var second = Spots(TreeA, FoodPlace.Windfall)[1];
        Check(_host.Nature.Food[TreeA].Picked == 1 << second && _host.Nature.Food[TreeA].PickedS == Seconds(_now),
            "a pick after it grew back starts afresh: only the new spot is picked, from now");
        // A day away: the save keeps when it was picked, and the reload finds it grown back.
        var away = _now;
        await FreeWorld();
        _now = away + TimeSpan.FromHours(25);
        await NewWorld("regrow", keepFiles: true);
        Check(_host.Nature.Food.ContainsKey(TreeA) && _host.FoodLeft(TreeA).Count == Layout(TreeA)!.Spots.Count, "after a day away, everything has grown back");
        // The island clock's day scale: four game days a real day, so six real hours are a day.
        _host.ClockRecord = Clock(4);
        await PickWindfall(0);
        var fast = _now;
        _now = fast + TimeSpan.FromHours(5);
        Check(!_host.FoodLeft(TreeA).Contains(spot), "on a four-days-a-day clock five real hours are not yet a day");
        _now = fast + TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1);
        Check(_host.FoodLeft(TreeA).Contains(spot), "and six are");
        _host.ClockRecord = null;
    }

    /// <summary>A Grow cast that grows a plant ripens its food at once, and the food hangs on the grown tree.</summary>
    private async Task TestGrowRipens()
    {
        await NewWorld("ripen", mode: "creative");
        await PickWindfall(0);
        await PickWindfall(1);
        Check(_host.FoodLeft(TreeA).Count == Layout(TreeA)!.Spots.Count - 2, "two apples picked");
        _companion.GlobalPosition = TreeAAt + new Vector3(-0.6f, 0.01f, 0.3f);
        Check(_player.TryTeleportTo(_room.SpawnFor("player").PositionM), "the player steps back to the spawn");
        await Frames(4);
        var grow = GrowCast(Player, TreeAAt + new Vector3(-0.2f, 0.01f, 0), 0.5);
        var factor = _host.Nature.Plants.GetValueOrDefault(TreeA)?.Factor ?? 1;
        var canopy = Spots(TreeA, FoodPlace.Canopy)[0];
        var hung = Layout(TreeA)!.At(canopy, factor);
        Check(Ok(grow) && factor > 1 && !_host.Nature.Food.ContainsKey(TreeA) && _host.FoodLeft(TreeA).Count == Layout(TreeA)!.Spots.Count &&
              _host.FoodSpotsNear(TreeAAt, 1).Any(s => s.PositionM.DistanceTo(hung) < 1e-4f && s.Look == FoodView.Look.Apple),
            $"Grow grows the tree ({factor:0.00}) and ripens its food at once; its canopy apple hangs on the grown crown at {hung.Y:0.000} m: " + Canonical(grow["error"]));
        Check(_host.FoodLeft(TreeB).Count == Layout(TreeB)!.Spots.Count && !_host.Nature.Food.ContainsKey(Shrub), "nothing else changed");
    }

    /// <summary>Fire burns the food with the plant; a burnt plant's food returns when it is replanted (P-Grow's replanting).</summary>
    private async Task TestFireAndReplant()
    {
        await NewWorld("fire", mode: "creative");
        await PickWindfall(0, TreeB);
        Check(_host.Nature.Food.ContainsKey(TreeB), "an apple picked from tree B");
        Check(_player.TryTeleportTo(_room.SpawnFor("player").PositionM), "the player steps back to the spawn");
        _companion.GlobalPosition = TreeBAt + new Vector3(-0.6f, 0.01f, -0.3f);
        await Frames(4);
        var lit = Fire(TreeBAt + new Vector3(-0.1f, 0.01f, 0), 0.3, 30);
        for (var step = 0; step < 120 && _host.Nature.Fire != null; step++) Steps(1);
        await Frames(2);
        Check(Ok(lit) && _host.Nature.Plants.GetValueOrDefault(TreeB) is { Burnt: true } && !_host.Nature.Food.ContainsKey(TreeB) && _host.FoodLeft(TreeB).Count == 0 &&
              !_host.FoodSpotsNear(TreeBAt, 0.5f).Any() && Refused(Pick(Player, TreeB), "target_not_found", "That is not in this room.", "$.args.target"),
            "the tree burns down and its food with it: nothing left, drawn or to pick: " + Canonical(lit["error"]));
        Check(_host.FoodLeft(TreeA).Count == Layout(TreeA)!.Spots.Count, "tree A, out of the fire's reach, keeps all its food");
        _host.LearnGrowForTests(2);
        var replant = GrowCast(Player, TreeBAt + new Vector3(-0.2f, 0.01f, 0), 0.5);
        await Frames(2);
        var seedling = _host.Nature.Plants.GetValueOrDefault(TreeB);
        var size = seedling?.Factor ?? 0;
        var canopy = Spots(TreeB, FoodPlace.Canopy)[0];
        Check(Ok(replant) && seedling is { Burn: 0, SownS: not null } && _host.FoodLeft(TreeB).Count == Layout(TreeB)!.Spots.Count &&
              _host.FoodSpotsNear(TreeBAt, 1).Any(s => s.PositionM.DistanceTo(Layout(TreeB)!.At(canopy, size)) < 1e-4f),
            $"replanted, the tree comes back as a seedling ({size:0.00}) bearing all its food again, on its smaller crown: " + Canonical(replant["error"]));
    }

    // ---- the save ----

    /// <summary>The counts in the nature block: a reload keeps them; room state carries them and the pick's receipt; a tampered record is refused.</summary>
    private async Task TestReload()
    {
        await NewWorld("reload");
        await PickWindfall(0);
        var spot = Spots(TreeA, FoodPlace.Windfall)[0];
        var expected = new JsonObject { [TreeA] = new JsonObject { ["picked"] = 1 << spot, ["picked_s"] = Seconds(_now) } };
        var packed = NatureBlock.Pack(_host.Nature)["food"];
        Check(Canonical(packed) == Canonical(expected), "the nature block keeps the spot picked and when: " + Canonical(packed));
        await NewWorld("reload", keepFiles: true);
        Check(Canonical(NatureBlock.Pack(_host.Nature)["food"]) == Canonical(expected) && !_host.FoodLeft(TreeA).Contains(spot) && Units("apple") == 1,
            "a reload keeps it: the spot still bare, the apple in the pockets");
        var state = _host.ExportRoomState()!;
        var receipt = state["receipts"]!.AsObject().Select(p => p.Value!["result"]!).FirstOrDefault(r => r["op"]?.GetValue<string>() == "item.pickup");
        Check(Canonical(state["extensions"]?["x_play_nature"]?["food"]) == Canonical(expected) && receipt != null && Canonical(receipt["affected"]) == Canonical(new JsonArray(TreeA)),
            "room state carries the food in x_play_nature and the pick's receipt, naming the tree");
        await NewWorld("reload-import");
        var imported = _host.ImportRoomState((JsonObject)state.DeepClone());
        Check(imported.Ok && Canonical(NatureBlock.Pack(_host.Nature)["food"]) == Canonical(expected) && !_host.FoodLeft(TreeA).Contains(spot), "and it imports: " + imported.Message);
        var tampers = new (string Why, Action<JsonObject> Edit)[]
        {
            ("no spot picked", s => s["extensions"]!["x_play_nature"]!["food"]![TreeA]!["picked"] = 0),
            ("a ninth spot", s => s["extensions"]!["x_play_nature"]!["food"]![TreeA]!["picked"] = 256),
            ("a plant not in this room", s => s["extensions"]!["x_play_nature"]!["food"]!["plant:broadleaf-7"] = new JsonObject { ["picked"] = 1, ["picked_s"] = 1 }),
            ("a field it never writes", s => s["extensions"]!["x_play_nature"]!["food"]![TreeA]!["count"] = 1),
            ("a pick that names a thing", s => s["receipts"]!.AsObject().First(p => p.Value!["result"]!["op"]!.GetValue<string>() == "item.pickup").Value!["result"]!["affected"] = new JsonArray("obj:book")),
        };
        foreach (var (why, edit) in tampers)
        {
            var tampered = (JsonObject)state.DeepClone();
            edit(tampered);
            await NewWorld("reload-tampered");
            var refused = _host.ImportRoomState(tampered);
            Check(!refused.Ok && _host.Revision == 0, $"a room state with {why} is refused: {refused.Message}");
        }
    }

    // ---- item 5: the look ----

    /// <summary>The food view in the world, fed by the host, and its budget measured headless: at most 64, three draws, unshaded, no shadow.</summary>
    private async Task TestView()
    {
        await NewWorld("view");
        var view = _world!.GetNode("Room").GetNode<PlantCopiesView>(PlantCopiesView.ViewName).Food;
        var all = _host.FoodSpotsNear(TreeAAt, FoodView.DrawM);
        view.Refill(TreeAAt);
        Check(view.Source != null && view.Drawn.Count == Math.Min(FoodView.MaxDrawn, all.Count) && all.Count == _host.FoodPlants.Sum(p => p.Spots.Count) &&
              view.Apples.Multimesh.VisibleInstanceCount == view.Drawn.Count(s => s.Look == FoodView.Look.Apple) &&
              view.Berries.Multimesh.VisibleInstanceCount == view.Drawn.Count(s => s.Look == FoodView.Look.Berries) && view.Stalks.Multimesh.VisibleInstanceCount == view.Apples.Multimesh.VisibleInstanceCount,
            $"the food view under the copies draws the island's {all.Count} units, apples with their stalks and the berries");
        var spot = Spots(TreeA, FoodPlace.Windfall)[0];
        var at = Layout(TreeA)!.At(spot, 1);
        await PickWindfall(0);
        view.Refill(TreeAAt);
        Check(view.Drawn.Count == all.Count - 1 && view.Drawn.All(s => s.PositionM.DistanceTo(at) > 1e-4f), "a picked spot shows nothing");
        Check(new[] { view.Apples, view.Stalks, view.Berries }.All(p => p.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off &&
                  p.MaterialOverride is StandardMaterial3D { ShadingMode: BaseMaterial3D.ShadingModeEnum.Unshaded } && p.Multimesh.InstanceCount == FoodView.MaxDrawn),
            "three pools, each unshaded and casting no shadow, of at most 64");
        // The budget, headless: 400 units offered round a point, the nearest 64 drawn, asked again at 200 foci.
        var test = FoodView.Create();
        AddChild(test);
        var offered = new List<FoodView.Spot>();
        for (var i = 0; i < 400; i++)
            offered.Add(new FoodView.Spot(new Vector3((i % 20 - 9.5f) * 0.4f, 0, (i / 20 - 9.5f) * 0.4f), i % 2 == 0 ? FoodView.Look.Apple : FoodView.Look.Berries, i % 4 == 0 ? 0.3f : 0));
        test.Source = (_, _) => offered;
        test.Refill(Vector3.Zero);
        var drawn = test.Drawn.Max(s => s.PositionM.Length());
        var undrawn = offered.Except(test.Drawn).Min(s => s.PositionM.Length());
        Check(test.Drawn.Count == FoodView.MaxDrawn && drawn <= undrawn + 1e-5f && test.Apples.Multimesh.VisibleInstanceCount + test.Berries.Multimesh.VisibleInstanceCount == FoodView.MaxDrawn,
            $"of 400 units offered, the 64 nearest are drawn (the farthest drawn {drawn:0.00} m, the nearest left out {undrawn:0.00} m)");
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) test.Refill(new Vector3((i % 10 - 5) * 0.3f, 0, (i / 10 % 10 - 5) * 0.3f));
        var ms = clock.Elapsed.TotalMilliseconds / 200;
        GD.Print(System.FormattableString.Invariant(
            $"FOOD_VIEW at most {FoodView.MaxDrawn} drawn in 3 draws (apples, stalks, berries), unshaded, no shadow; {test.AppleTriangles} triangles an apple with its stalk, {test.BerryTriangles} a cluster; an ask of 400 offered {ms:0.000} ms on average, the longest {test.LongestAskMs:0.000} ms; in the world {view.LastAskMs:0.000} ms"));
        Check(ms < 2 && test.AppleTriangles + test.BerryTriangles < 400, $"an ask costs {ms:0.000} ms; {test.AppleTriangles} and {test.BerryTriangles} triangles");
        test.Source = (_, _) => Array.Empty<FoodView.Spot>();
        test.Refill(Vector3.Zero);
        Check(test.Drawn.Count == 0 && !test.Apples.Visible && !test.Berries.Visible, "with nothing offered nothing is drawn");
        test.QueueFree();
        await Frames(2);
    }
}
