using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Grow (docs/game/MECHANICS.md decisions 52 and 67), in the test room with plants (IslandFixture.WithPlants: grass tufts round
/// NearGubble, a broadleaf in the far corner): the cast (every plant rooted in the circle grows the pack's quarter, up to twice its size
/// or 0.4 m taller; a cast where nothing can grow is refused with the reason), grown plants staying grown (the save, a reload, the
/// copies drawn at their size, Creative's undo, the cost), a grown tree climbed (its trunk, pole and perches grown with it, the top
/// perch at most 0.4 m higher), and plants burning as fire's objects (they catch, burn as fuel, longer when grown, burn down and stay
/// gone, saved, in the fire's one undo step).
/// </summary>
public partial class NatureTest
{
    /// <summary>The broadleaf's root: a corner of the test room clear of the spawns, the builds and the fires the other checks light.</summary>
    private static readonly Vector3 TreeAt = new(1.6f, 0, -1.1f);
    /// <summary>The grass tufts, 0.13 m tall: three round NearGubble and two along +x from it (0.2 m and 0.3 m), as the other checks cast.</summary>
    private static readonly Vector2[] TuftsAt = { new(0.45f, 0.95f), new(0.52f, 0.98f), new(0.40f, 0.90f), new(0.65f, 0.95f), new(0.75f, 0.95f) };

    /// <summary>The test room with its plants: a grass set (the tufts) and a broadleaf set (one tree), written under the suite's root.</summary>
    private static RoomData PlantedRoom()
    {
        var grass = TuftsAt.Select((at, i) => new IslandFixture.Row(at.X, 0, at.Y, 30f * i, Vector3.One, new Color(0.85f, 0.95f, 0.7f), 0.13f, 0, 0, (uint)(100 + i))).ToList();
        var tree = IslandFixture.Broadleaf(TreeAt.X, TreeAt.Z, 0, 0, 31);
        var directory = IslandFixture.WithPlants(RoomWorld.DefaultRoom, $"{TestRoot}/planted/test_room", new[] { IslandFixture.Grass("grass", grass), IslandFixture.Trees(new[] { tree }) });
        return RoomData.Load(directory);
    }

    private static string Tuft(int index) => PlantCopies.IdOf("grass", RowOf("grass", TuftsAt[index]));
    private const string Tree = "plant:broadleaf-0";

    /// <summary>The table row of the tuft at a spot (rows are sorted by tile, then x, then z).</summary>
    private static int RowOf(string set, Vector2 at) => TuftsAt.OrderBy(t => t.X).ThenBy(t => t.Y).ToList().IndexOf(at);

    private PlantCopiesView Copies => _world!.GetNode("Room").GetNode<PlantCopiesView>(PlantCopiesView.ViewName);

    private float Factor(string id) => _host.Nature.Plants.TryGetValue(id, out var plant) ? plant.Factor : 1;

    /// <summary>The first hit straight down onto a layer at (x, z), from 2 m (under the test room's ceiling): its height, or NaN.</summary>
    private float HitDown(float x, float z, uint layer)
    {
        using var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, 2, z), new Vector3(x, -0.5f, z), layer);
        var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        return hit.Count == 0 ? float.NaN : hit["position"].AsVector3().Y;
    }

    /// <summary>Whether a level ray across the tree at a height hits its trunk (the world layer).</summary>
    private bool TrunkAt(float height)
    {
        using var query = PhysicsRayQueryParameters3D.Create(TreeAt + new Vector3(-0.25f, height, 0), TreeAt + new Vector3(0.25f, height, 0), RoomBuilder.WorldLayer);
        return GetViewport().World3D.DirectSpaceState.IntersectRay(query).Count > 0;
    }

    private async Task TestGrow()
    {
        await TestGrowCast();
        await TestGrownStay();
        await TestGrownTreeClimbed();
        await TestPlantsBurn();
        // Decision 127, refined (NatureTest.GrowRipens.cs): a plant grows to its cap first, then a cast ripens its food.
        await TestGrowRipensAtTheCap();
    }

    /// <summary>Item 2: the cast, its cap, and the casts refused before the price.</summary>
    private async Task TestGrowCast()
    {
        await NewWorld(SavePath("grow-cast"));
        var revision = _host.Revision;
        // Bare ground that is not fertile (stone, a build): nothing grows there. Bare fertile soil is seedless growth's (NatureTest.GrowUnlocks).
        _host.NatureSampleForTests = (x, z) => Soil(x, z) is { } cell ? new NatureCell { X = cell.X, Z = cell.Z, Y = cell.Y, Surface = "shell:floor", Material = "stone", Fertile = false } : null;
        var bare = Send(Command(NextId("grow-bare"), "nature.cast", Cast("grow", new Vector3(-0.5f, 0.01f, 0.3f), 0.3, 1)), Player);
        _host.NatureSampleForTests = Soil;
        Check(Code(bare) == "invalid_args" && (bare["error"]?["message"]?.GetValue<string>() ?? "").Contains("no plant", StringComparison.Ordinal) && _host.Revision == revision,
            "Grow where no plant grows (bare stone) is refused before any price, with the reason: " + bare["error"]?["message"]);
        var preview = Send(Command(NextId("grow-preview"), "nature.cast", Cast("grow", NearGubble, 0.15, 1), preview: true), Player);
        Check(Ok(preview) && preview["data"]?["grown_plants"]?.GetValue<int>() == 3 && preview["data"]?["cell_count"]?.GetValue<int>() == 0 && _host.Nature.Plants.Count == 0,
            "a preview says how many plants would grow (the three tufts rooted in the circle) and changes nothing: " + Canonical(preview["data"]));
        var wide = Send(Command(NextId("grow-wide"), "nature.cast", Cast("grow", NearGubble, 1.2, 1)), Player);
        Check(Code(wide) == "invalid_args" && wide["error"]?["allowed"]?.GetValue<double>() == 1.0, "a circle past the pack's 1.0 m is refused with the pack's limit");
        // One tuft alone, four casts to its cap (0.13 m tall: twice its size comes before 0.4 m taller), the fifth refused.
        var one = Tuft(0);
        var factors = new List<float>();
        for (var cast = 0; cast < 4; cast++)
        {
            var grown = Send(Command(NextId("grow-tuft"), "nature.cast", Cast("grow", NearGubble, 0.05, 1)), Player);
            factors.Add(Ok(grown) ? Factor(one) : -1);
            Check(Ok(grown) && grown["data"]?["grown_plants"]?.GetValue<int>() == 1 && grown["data"]?["started_cells"]?.GetValue<int>() == 0, "each cast grows the one tuft in the circle: " + Canonical(grown["error"] ?? grown["data"]));
        }
        Check(factors.SequenceEqual(new[] { 1.25f, 1.5f, 1.75f, 2f }), "a quarter of its natural size a cast, up to twice its size: " + string.Join(", ", factors));
        var full = Send(Command(NextId("grow-full"), "nature.cast", Cast("grow", NearGubble, 0.05, 1)), Player);
        Check(Code(full) == "invalid_args" && full["error"]?["message"]?.GetValue<string>() == "Every plant there is already full size.", "a fifth cast is refused: every plant there is full size");
        // The tree: 0.95 m, so 0.4 m taller (1.35 m, a factor of 1.42) comes before twice its size; two casts.
        _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f);
        var near = TreeAt + new Vector3(-0.2f, 0.01f, 0);
        var first = Send(Command(NextId("grow-tree"), "nature.cast", Cast("grow", near, 0.5, 1)), Player);
        var second = Send(Command(NextId("grow-tree"), "nature.cast", Cast("grow", near, 0.5, 1)), Player);
        var third = Send(Command(NextId("grow-tree"), "nature.cast", Cast("grow", near, 0.5, 1)), Player);
        var cap = 1 + 0.4f / 0.95f;
        Check(Ok(first) && Ok(second) && Code(third) == "invalid_args" && Math.Abs(Factor(Tree) - cap) < 1e-4f && Math.Abs(0.95f * Factor(Tree) - 1.35f) < 1e-3f,
            $"a 0.95 m tree is full in two casts at 1.35 m (factor {Factor(Tree):0.000}), the third refused");
        await FreeWorld();
    }

    /// <summary>Item 3: grown plants stay grown: saved, reloaded, drawn at their size; Creative's undo takes a cast back; the cost.</summary>
    private async Task TestGrownStay()
    {
        await NewWorld(SavePath("grow-stay"));
        var revision = _host.Revision;
        var cast = Send(Command(NextId("stay-grow"), "nature.cast", Cast("grow", NearGubble, 0.15, 1)), Player);
        var castMs = _host.PlantsRefreshMs;
        var castTiles = _host.PlantsRefreshTiles;
        Check(Ok(cast) && Copies.FactorOf(Tuft(0)) == 1.25f && Copies.FactorOf(Tuft(3)) == 1, "the cast's plants are drawn at their size at once, the others untouched");
        var again = Send(Command(NextId("stay-again"), "nature.cast", Cast("grow", NearGubble, 0.15, 1)), Player);
        var undo = Send(Command(NextId("stay-undo"), "room.undo", new JsonObject { ["to_revision"] = _host.Revision - 1 }, expectedRevision: _host.Revision), Player);
        Check(Ok(again) && Ok(undo) && Factor(Tuft(0)) == 1.25f && Copies.FactorOf(Tuft(0)) == 1.25f, "Creative's undo takes the last cast back, drawing and all");
        var saved = Canonical(NatureBlock.Pack(_host.Nature));
        await NewWorld(SavePath("grow-stay"));
        var loadMs = _host.PlantsRefreshMs;
        Check(Canonical(NatureBlock.Pack(_host.Nature)) == saved && Factor(Tuft(1)) == 1.25f && Copies.FactorOf(Tuft(1)) == 1.25f && _host.Revision == revision + 3,
            "grown plants are saved with the island and come back on load, drawn at their size");
        GD.Print(System.FormattableString.Invariant($"GROW_COST a cast refilled {castTiles} tile(s) in {castMs:0.00} ms; a load gave the copies their sizes in {loadMs:0.00} ms"));
        Check(castTiles == 1 && castMs < 50 && loadMs < 50, $"only the tile holding the grown plants is rebuilt ({castTiles}), within a frame's budget ({castMs:0.00} ms, load {loadMs:0.00} ms)");
        // The saved plants, checked strictly as every saved block is.
        var block = NatureBlock.Pack(_host.Nature);
        var plantsBlock = block["plants"]!.AsObject();
        Check(NatureBlock.Problem(block, _host.Revision, _room) == null && plantsBlock.Count == 3 && plantsBlock[Tuft(0)]?["factor"]?.GetValue<double>() == 1.25,
            "the block names each grown plant by id with its factor: " + Canonical(plantsBlock));
        var forgeries = new (string Label, Action<JsonObject> Edit)[]
        {
            ("a plant of a set the room lacks", b => b["plants"]!["plant:fern-0"] = new JsonObject { ["factor"] = 1.25 }),
            ("a plant past its table", b => b["plants"]!["plant:grass-99"] = new JsonObject { ["factor"] = 1.25 }),
            ("a plant spelt with a leading zero", b => b["plants"]!["plant:grass-01"] = new JsonObject { ["factor"] = 1.25 }),
            ("a plant past twice its size", b => b["plants"]![Tuft(0)] = new JsonObject { ["factor"] = 2.5 }),
            ("a plant at its natural size", b => b["plants"]![Tuft(0)] = new JsonObject { ["factor"] = 1 }),
            ("a burnt plant with a size", b => b["plants"]![Tuft(0)] = new JsonObject { ["factor"] = 1.5, ["burn"] = 1 }),
            ("a plant with a field it does not have", b => b["plants"]![Tuft(0)]!["owner"] = "companion:local"),
            ("an empty record of plants", b => b["plants"] = new JsonObject()),
        };
        foreach (var (label, edit) in forgeries)
            Check(NatureBlock.Problem(Edited(block, edit), _host.Revision, _room) != null, "the strict check refuses a block with " + label);
        await FreeWorld();
    }

    /// <summary>Item 4: a grown tree's trunk, pole and perches grow with it; the top perch at most 0.4 m higher; grass has no collision.</summary>
    private async Task TestGrownTreeClimbed()
    {
        await NewWorld(SavePath("grow-climb"));
        await Frames(2);
        var perch = HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer);
        var trunkBefore = TrunkAt(0.6f);
        var grassBefore = HitDown(TuftsAt[4].X, TuftsAt[4].Y, RoomBuilder.HiddenLayer | RoomBuilder.WorldLayer);
        _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f);
        var near = TreeAt + new Vector3(-0.2f, 0.01f, 0);
        Send(Command(NextId("climb-grow"), "nature.cast", Cast("grow", near, 0.5, 1)), Player);
        Send(Command(NextId("climb-grow"), "nature.cast", Cast("grow", near, 0.5, 1)), Player);
        Send(Command(NextId("climb-grass"), "nature.cast", Cast("grow", NearGubble + new Vector3(0.3f, 0, 0), 0.05, 1)), Player);
        await Frames(2);
        var grown = HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer);
        var factor = Factor(Tree);
        Check(Math.Abs(perch - 0.94f) < 0.01f && Math.Abs(grown - 0.94f * factor) < 0.01f && grown - perch <= 0.4f + 0.002f && grown - perch > 0.35f,
            $"the grown tree's top perch rises with it, at most 0.4 m: from {perch:0.000} m to {grown:0.000} m (factor {factor:0.000})");
        Check(!trunkBefore && TrunkAt(0.6f), "its trunk grows with it: at 0.6 m a level ray missed the trunk (0.53 m) and hits it now (0.75 m)");
        var climb = Copies.TileNode("broadleaf", 0, -1)?.FindChildren("*", "Node3D", false, false).Count(n => n.Name.ToString().StartsWith("Climb", StringComparison.Ordinal)) ?? -1;
        Check(climb == 1, $"its pole and perches are still the hidden layer's one climbing body for the tile, built again in place ({climb})");
        // The ground nature samples on a copy's collision names that copy: before, a trunk's top was a cell named "" (no entity), and
        // the save refused its whole nature block on the next load ("a name"; the windowed launch on the founder's island found it).
        _host.NatureSampleForTests = null;
        var (tx, tz) = NatureState.Grid(TreeAt);
        var onTrunk = _host.SampleCellForTests(tx, tz);
        var onFloor = _host.SampleCellForTests(tx - 10, tz);
        _host.NatureSampleForTests = Soil;
        Check(onTrunk is { Surface: Tree, Material: "bark" } && onFloor is { Surface: "shell:floor" },
            $"a cell on a tree's trunk names the tree ({onTrunk?.Surface}, {onTrunk?.Material}), one on the floor the floor ({onFloor?.Surface})");
        Check(float.IsNaN(grassBefore) || grassBefore < 0.02f, $"grass, flowers and ferns have no collision to climb, grown or not (the ray meets the floor at {grassBefore:0.000} m)");
        var after = HitDown(TuftsAt[4].X, TuftsAt[4].Y, RoomBuilder.HiddenLayer | RoomBuilder.WorldLayer);
        Check(Factor(Tuft(4)) == 1.25f && (float.IsNaN(after) || after < 0.02f), $"and a grown tuft gains none ({after:0.000} m)");
        await FreeWorld();
    }

    /// <summary>
    /// Item 5 (decision 67): plants burn as fire's objects. A grown tuft and the tree catch, burn (the grown one longer), burn down and
    /// are gone with their drawing and their collision, leaving ash; they stay gone through the weather and a reload, and the fire's one
    /// undo step in Creative brings them back.
    /// </summary>
    private async Task TestPlantsBurn()
    {
        await NewWorld(SavePath("grow-burn"));
        await Frames(2);
        var grownTuft = Tuft(4);
        var naturalTuft = Tuft(3);
        for (var cast = 0; cast < 4; cast++) Send(Command(NextId("burn-grow"), "nature.cast", Cast("grow", NearGubble + new Vector3(0.3f, 0, 0), 0.05, 1)), Player);
        Check(Factor(grownTuft) == 2f && Factor(naturalTuft) == 1f, "one tuft is grown to its full size first, its neighbour left as it was");
        var beforeFire = _host.Revision;
        var lit = Fire(Player, new Vector3(0.7f, 0.01f, 0.95f), 0.25, 30, out var preview);
        Check(Ok(preview) && Ok(lit) && preview["data"]?["affected_ids"]?.AsArray().All(id => !id!.GetValue<string>().StartsWith("plant:", StringComparison.Ordinal)) == true,
            "a fire among the plants is lit; plants are never a fire preview's things to ask about: " + Canonical(lit["error"] ?? preview["data"]?["affected_ids"]));
        int? grownCaught = null, naturalCaught = null, grownDown = null, naturalDown = null;
        for (var step = 1; step <= 80 && _host.Nature.Fire != null; step++)
        {
            Steps(1);
            var grown = _host.Nature.Plants.GetValueOrDefault(grownTuft);
            var natural = _host.Nature.Plants.GetValueOrDefault(naturalTuft);
            if (grown is { Burn: > 0 }) grownCaught ??= step;
            if (natural is { Burn: > 0 }) naturalCaught ??= step;
            if (grown is { Burnt: true }) grownDown ??= step;
            if (natural is { Burnt: true }) naturalDown ??= step;
        }
        var ash = _host.Nature.Cells.Values.FirstOrDefault(c => c.Key == NatureState.Key(NatureState.Grid(new Vector3(0.75f, 0, 0.95f)).X, NatureState.Grid(new Vector3(0.75f, 0, 0.95f)).Z));
        Check(grownDown != null && naturalDown != null && _host.Nature.Plants[grownTuft].Burnt && Copies.FactorOf(grownTuft) == 0 && Copies.FactorOf(naturalTuft) == 0 && ash is { Ash: > 0 },
            $"both tufts catch and burn down, gone from the drawn island, leaving ash (caught {grownCaught}/{naturalCaught}, down {grownDown}/{naturalDown})");
        Check(grownDown - grownCaught > naturalDown - naturalCaught, $"the grown one, more fuel, takes longer to burn down ({grownDown - grownCaught} steps against {naturalDown - naturalCaught})");
        Check(_host.Nature.Undo.Count(s => s.Revision == beforeFire + 1) == 1 && _host.Nature.Undo.Single(s => s.Revision == beforeFire + 1).Plants.ContainsKey(grownTuft),
            "the burnt plants are in the fire's one undo step");
        // Stays gone: a whole weather cycle (rain, then drying) regrows the ash's fuel and never the plants.
        for (var i = 0; i < (int)(NatureLaws.WeatherCycleS / NatureLaws.StepS); i++) Steps(1);
        Check(_host.Nature.Plants[grownTuft].Burnt && _host.Nature.Plants[naturalTuft].Burnt, "six minutes of weather later the burnt plants are still gone: the simulation never regrows them");
        // The tree: a fire at its root burns it down, its climbing collision with it.
        _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f);
        var treeFire = Fire(Player, TreeAt + new Vector3(-0.1f, 0.01f, 0), 0.3, 30, out _);
        for (var step = 0; step < 80 && _host.Nature.Fire != null; step++) Steps(1);
        await Frames(2);
        Check(Ok(treeFire) && _host.Nature.Plants.GetValueOrDefault(Tree) is { Burnt: true } && float.IsNaN(HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer)) && !TrunkAt(0.3f),
            $"a tree burns down as any object does: gone with its trunk, pole and perches ({Canonical(treeFire["error"])}, burn {_host.Nature.Plants.GetValueOrDefault(Tree)?.Burn}, perch {HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer)}, trunk {TrunkAt(0.3f)})");
        var burnt = Canonical(NatureBlock.Pack(_host.Nature)["plants"]);
        var saved = Canonical(NatureBlock.Pack(_host.Nature));
        await NewWorld(SavePath("grow-burn"));
        await Frames(2);
        Check(Canonical(NatureBlock.Pack(_host.Nature)) == saved && Copies.FactorOf(Tree) == 0 && float.IsNaN(HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer)),
            "the burnt plants are saved with the island: after a reload the tree is still gone, collision and all: " + burnt);
        // The fire's undo in Creative brings the tree back (the last change), whole.
        var undo = Send(Command(NextId("burn-undo"), "room.undo", new JsonObject { ["to_revision"] = _host.Revision - 1 }, expectedRevision: _host.Revision), Player);
        await Frames(2);
        Check(Ok(undo) && !_host.Nature.Plants.ContainsKey(Tree) && Copies.FactorOf(Tree) == 1 && Math.Abs(HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer) - 0.94f) < 0.01f,
            "Creative's undo takes the fire back in one step: the tree stands again, perches and all");
        await FreeWorld();
    }
}
