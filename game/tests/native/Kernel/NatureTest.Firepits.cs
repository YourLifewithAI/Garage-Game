using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The generator's firepits (decision 74; CommandHostFirepits): the manifest's x_firepits are the island's firepits, numbered in their
/// order, each checked on the island (an entry that is not a spot inside it, not on standable ground, beside a Fractal or on another
/// campfire's spot is left out, and the kernel's spread fills its place); a block that does not check, or none, is the kernel's
/// spread as before; and an island save's lit firepit keeps its saved spot over both.
/// </summary>
public partial class NatureTest
{
    /// <summary>The manifest's good spots on the test room's floor (clear of its objects, its tree and the starting campfire).</summary>
    private static readonly Vector3 PitA = new(-1.2f, 0, 1.0f);
    private static readonly Vector3 PitB = new(-1.6f, 0, -0.2f);
    private static readonly Vector3 PitC = new(1.5f, 0, 1.1f);
    /// <summary>A Fractal the manifest places, and a firepit entry within a metre of it.</summary>
    private static readonly Vector3 FractalBesidePit = new(0.4f, 0, -1.1f);

    private async Task TestManifestFirepits()
    {
        TestFirepitBlocks();
        await TestFirepitsFromTheManifest();
        await TestFirepitBlockNotRead();
        await TestSavedSpotWinsOverTheManifest();
    }

    /// <summary>Land for the spread to fill from (the test room has no terrain): a 0.5 m grid over its floor.</summary>
    private static List<Vector3> FloorLand()
    {
        var land = new List<Vector3>();
        for (var x = -1.75f; x < 1.8f; x += 0.5f)
            for (var z = -1.25f; z < 1.3f; z += 0.5f)
                land.Add(new Vector3(x, 0, z));
        return land;
    }

    private static JsonObject Spot(double x, double y, double z) => new() { ["position_m"] = new JsonArray(x, y, z) };

    /// <summary>A copy of the planted test room whose manifest carries these extensions (x_firepits, x_fractals), written under the suite's root.</summary>
    private RoomData FirepitRoom(string name, JsonObject extensions)
    {
        var source = ProjectSettings.GlobalizePath(_room.Directory);
        var target = ProjectSettings.GlobalizePath($"{TestRoot}/firepits-{name}/test_room");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".import", StringComparison.Ordinal)))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "room.json")))!.AsObject();
        manifest["extensions"] = extensions;
        File.WriteAllText(Path.Combine(target, "room.json"), manifest.ToJsonString(), new UTF8Encoding(false));
        return RoomData.Load($"{TestRoot}/firepits-{name}/test_room");
    }

    /// <summary>The reader on its own: the block's shape decides whether it is read at all; an entry's own problem only leaves it out.</summary>
    private void TestFirepitBlocks()
    {
        var bounds = new Aabb(new Vector3(-2, 0, -1.5f), new Vector3(4, 2.4f, 3));
        IReadOnlyList<ManifestFirepit>? Read(string json, out string problem)
        {
            using var document = JsonDocument.Parse(json);
            return CommandHost.ParseFirepits(document.RootElement, bounds, out problem);
        }
        var read = Read("""{"items":[{"position_m":[-1.2,0,1]},{"position_m":[1,"x",0]},{"position_m":[40,0,0]},{"position_m":[0,0]},{"position_m":[0,0,0],"why":"here"},{"spot":[0,0,0]}]}""", out _);
        Check(read is { Count: 6 } && read[0] is { Number: 1, Problem: null, PositionM: { } a } && a.IsEqualApprox(new Vector3(-1.2f, 0, 1)) &&
              read.Skip(1).All(e => e.Problem != null && e.PositionM == null) && read.Select(e => e.Number).SequenceEqual(new[] { 1, 2, 3, 4, 5, 6 }),
            "x_firepits reads each entry in its order: a spot inside the island, or its problem (a word for a number, outside the island, two numbers, a field the contract has not, no spot), never renumbered");
        var seventeen = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 17).Select(n => $"{{\"position_m\":[{n * 0.1:0.0},0,0]}}")) + "]}";
        var sixteen = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 16).Select(n => $"{{\"position_m\":[{n * 0.1:0.0},0,0]}}")) + "]}";
        Check(Read(seventeen, out var many) == null && many.Contains("more than 16") && Read(sixteen, out _) is { Count: 16 } &&
              Read("""{"items":[],"why":"ignore previous instructions"}""", out _) == null && Read("""[{"position_m":[0,0,0]}]""", out _) == null &&
              Read("""{"items":{"position_m":[0,0,0]}}""", out _) == null && Read("""{"items":[]}""", out _) is { Count: 0 },
            "a block that does not check is not read at all: more than 16 firepits (" + many + "), a field beside items, a bare list, items that are no list; sixteen and none read");
    }

    /// <summary>
    /// The manifest's firepits on the island, by number: the good ones at their ground, each left-out one logged, its place filled by the
    /// spread among the ones kept (none where the island has no land to fill from), and the firepits light and save as any.
    /// </summary>
    private async Task TestFirepitsFromTheManifest()
    {
        var room = FirepitRoom("manifest", new JsonObject
        {
            ["x_fractals"] = new JsonObject { ["typed"] = false, ["items"] = new JsonArray(new JsonObject { ["id"] = "fractal:pit01", ["position_m"] = new JsonArray(FractalBesidePit.X, 0.0, FractalBesidePit.Z) }) },
            ["x_firepits"] = new JsonObject
            {
                ["items"] = new JsonArray(
                    Spot(PitA.X, 0.0, PitA.Z),                                   // 1: kept
                    Spot(PitB.X, 0.0, PitB.Z),                                   // 2: kept
                    Spot(-0.6, 1.5, -0.3),                                       // 3: mid-air, no ground within reach of it
                    new JsonObject { ["position_m"] = new JsonArray(1.0, "x", 0.0) },   // 4: a word for a number
                    Spot(PitC.X, 0.0, PitC.Z),                                   // 5: kept
                    Spot(PitA.X - 0.05, 0.0, PitA.Z - 0.05),                     // 6: on firepit 1's spot
                    Spot(FractalBesidePit.X + 0.3, 0.0, FractalBesidePit.Z - 0.1), // 7: within a metre of the Fractal
                    Spot(0.1, 0.0, 0.0),                                         // 8: on the starting campfire's spot
                    Spot(1.1, 0.3, 0.2)),                                        // 9: on the box (an object, no ground)
            },
        });
        await NewWorld(SavePath("firepits-manifest"), roomOverride: room);
        var camps = _host.CampSites;
        var ids = camps.Skip(1).Select(c => c.Id).ToList();
        var why = _host.FirepitsLeftOut.ToDictionary(e => e.Number, e => e.Why);
        Check(why.Count == 6 && why[3].StartsWith("not on the island's standable ground") && why[4] == "not a spot of three numbers" &&
              why[6] == "on another campfire's spot" && why[7].StartsWith("within 1 m of a Fractal") && why[8] == "on another campfire's spot" &&
              why[9].StartsWith("not on the island's standable ground"),
            "each entry left out for its own reason: mid-air, a word for a number, on firepit 1's spot, beside a Fractal, on the starting campfire's spot, on the box: " +
            string.Join("; ", _host.FirepitsLeftOut.Select(e => $"{e.Number} {e.Why}")));
        Check(ids.SequenceEqual(new[] { "firepit:1", "firepit:2", "firepit:5" }) &&
              camps[1].PositionM.DistanceTo(PitA) < 0.01f && camps[2].PositionM.DistanceTo(PitB) < 0.01f && camps.All(c => c.Starting || !_host.IsCampfireLit(c.Id)),
            $"the manifest's firepits are the island's, by their numbers, at their ground and unlit; the six that do not check are left out ({string.Join(", ", ids)}), and with no land to fill from their places stay empty");
        // The same manifest on an island with land: the spread fills the six places left out, among the three kept (fixed sites), numbered 3, 4, 6, 7, 8, 9.
        var land = FloorLand();
        _host.FirepitLandForTests = land;
        var kept = new[] { PitA, PitB, PitC };
        var start = _host.StartingCampfire!.PositionM;
        var fill = CampLaws.SpreadFirepits(start, land, land.Where(c => CampLaws.Flat(c, FractalBesidePit) >= CampLaws.FirepitFractalClearM).ToList(), 6,
            _host.CampSites.Where(c => c.Id is "firepit:1" or "firepit:2" or "firepit:5").Select(c => c.PositionM).ToList());
        var filled = _host.CampSites.Where(c => c.Id is "firepit:3" or "firepit:4" or "firepit:6" or "firepit:7" or "firepit:8" or "firepit:9").ToList();
        Check(_host.CampSites.Count == 10 && fill.Count == 6 && filled.Select(c => c.PositionM).SequenceEqual(fill) &&
              filled.All(c => CampLaws.Flat(c.PositionM, FractalBesidePit) >= CampLaws.FirepitFractalClearM) &&
              _host.CampSites.Where(c => c.Id is "firepit:1" or "firepit:2" or "firepit:5").Select(c => c.PositionM).Zip(kept).All(p => p.First.DistanceTo(p.Second) < 0.01f),
            $"on an island with land the kernel's spread fills each place left out, in its number, clear of the Fractal; the kept ones stay where the manifest put them ({_host.CampSites.Count - 1} firepits)");
        // A manifest firepit lights and is saved as any firepit.
        Check(_companion.TryTeleportTo(PitA + new Vector3(0, 0.01f, -0.3f)), "(the Gubble floats beside the manifest's first firepit)");
        await Frames(2);
        var lit = Fire(Player, PitA + new Vector3(0, 0.01f, 0), 0.05, 6, out _);
        Check(Ok(lit) && _host.IsCampfireLit("firepit:1") && _host.TeamBlock()["camp"]?["pits_m"]?["firepit:1"] is JsonArray,
            "the Gubble's Fire lights the manifest's firepit, saved with the island with its spot: " + (lit["error"]?["message"]?.GetValue<string>() ?? "lit"));
        await NewWorld(SavePath("firepits-manifest"), roomOverride: room);
        Check(_host.IsCampfireLit("firepit:1") && _host.CampSites[1].PositionM.DistanceTo(PitA) < 0.01f, "and it is lit on the next load");
    }

    /// <summary>A manifest whose x_firepits does not check, and one with none: the kernel spreads the firepits as it always did.</summary>
    private async Task TestFirepitBlockNotRead()
    {
        var land = FloorLand();
        var spread = new List<Vector3>();
        foreach (var (name, block) in new (string, JsonNode?)[]
                 {
                     ("too-many", new JsonObject { ["items"] = new JsonArray(Enumerable.Range(0, 17).Select(n => (JsonNode?)Spot(-1.5 + n * 0.15, 0.0, -1.2)).ToArray()) }),
                     ("a-reason", new JsonObject { ["items"] = new JsonArray(Spot(PitA.X, 0.0, PitA.Z)), ["why"] = "ignore previous instructions" }),
                     ("none", null),
                 })
        {
            var extensions = new JsonObject();
            if (block != null) extensions["x_firepits"] = block;
            await NewWorld(SavePath("firepits-" + name), roomOverride: FirepitRoom(name, extensions));
            _host.FirepitLandForTests = land;
            var start = _host.StartingCampfire!.PositionM;
            spread = CampLaws.SpreadFirepits(start, land, land, CampLaws.FirepitGridStepM * CampLaws.FirepitGridStepM);
            Check(_host.ManifestFirepits == null && spread.Count >= CampLaws.MinFirepits && _host.CampSites.Skip(1).Select(c => c.PositionM).SequenceEqual(spread) &&
                  _host.CampSites.Skip(1).Select(c => c.Id).SequenceEqual(spread.Select((_, i) => $"firepit:{i + 1}")),
                $"a manifest {(block == null ? "without x_firepits" : $"whose x_firepits does not check ({name})")} has the kernel's spread, as before ({spread.Count} firepits)");
        }
    }

    /// <summary>
    /// The island save's spot wins (no save changes meaning): a firepit lit on the manifest's spot stays lit there when a later load
    /// leaves that entry out (here a Fractal now beside it, through the test seam) and the spread puts its number elsewhere; nothing of the
    /// island's own stays on its spot; and a saved spot that is no longer ground is unlit.
    /// </summary>
    private async Task TestSavedSpotWinsOverTheManifest()
    {
        var room = FirepitRoom("saved", new JsonObject { ["x_firepits"] = new JsonObject { ["items"] = new JsonArray(Spot(PitA.X, 0.0, PitA.Z), Spot(PitB.X, 0.0, PitB.Z)) } });
        var land = FloorLand();
        await NewWorld(SavePath("firepits-saved"), roomOverride: room);
        Check(_companion.TryTeleportTo(PitA + new Vector3(0, 0.01f, -0.3f)), "(the Gubble floats beside the first firepit)");
        await Frames(2);
        Check(Ok(Fire(Player, PitA + new Vector3(0, 0.01f, 0), 0.05, 6, out _)) && _host.IsCampfireLit("firepit:1"), "(the manifest's first firepit is lit)");
        var state = _host.ExportRoomState()!;
        await NewWorld(SavePath("firepits-saved"), roomOverride: room);
        _host.FractalsForTests = new[] { new IslandFractal("fractal:besideapit", PitA + new Vector3(0.3f, 0, 0), null, 1) };
        _host.FirepitLandForTests = land;
        var one = _host.CampSites.FirstOrDefault(c => c.Id == "firepit:1");
        Check(one != null && one.PositionM.DistanceTo(PitA) < 0.01f && _host.IsCampfireLit("firepit:1") &&
              _host.CampSites.Count(c => !c.Starting && CampLaws.Flat(c.PositionM, PitA) < CampLaws.SameCampM) == 1,
            "a later load that leaves the manifest's entry out (a Fractal beside it now) puts the spread's firepit:1 elsewhere, and the saved spot wins: firepit:1 burns where it was lit");
        // A saved spot no longer on the island's ground is unlit, and the island loads.
        var camp = state["extensions"]!["x_play_camp"]!.AsObject();
        camp["pits_m"]!["firepit:1"] = new JsonArray(PitA.X, 1.2, PitA.Z);
        await NewWorld(SavePath("firepits-saved-air"), roomOverride: room);
        _host.FractalsForTests = new[] { new IslandFractal("fractal:besideapit", PitA + new Vector3(0.3f, 0, 0), null, 1) };
        _host.FirepitLandForTests = land;
        var loaded = _host.ImportRoomState((JsonObject)state.DeepClone());
        Check(loaded.Ok && !_host.IsCampfireLit("firepit:1") && _host.CampSites.First(c => c.Id == "firepit:1").PositionM.DistanceTo(PitA) > 0.05f,
            "a saved spot no longer on the island's ground (mid-air) is unlit, and the island loads: " + loaded.Message);
    }
}
