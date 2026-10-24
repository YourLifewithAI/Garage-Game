using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Fire's camp (MECHANICS.md decisions 49, 61, 62 and 70 to 74; CommandHostCamps): the firepits spread evenly over any island, the start
/// spot kept per mode in the island save (and the clock beside it), the starting campfire, resting by sitting at a fire (which teaches
/// Fire once), no Fire until that first rest, firepits lit by the Gubble's Fire as campfires saved with the island (the first teaching
/// Campfire), a lit campfire as a full camp, rain putting campfires out (never the X stop) and the Gubble's Fire relighting them.
/// </summary>
public partial class NatureTest
{
    private static readonly JsonObject UtcClock = new() { ["zone_id"] = "Etc/UTC", ["day_scale"] = 1, ["seasons_per_year"] = 4, ["phase_h"] = 0, ["hemisphere"] = "north" };

    private async Task TestCamps()
    {
        TestFirepitSpread();
        await TestStartSpot();
        await TestStartingCampfireAndRest();
        await TestFirepits();
        await TestFireLock();
        await TestCampfireRain();
        await TestFirepitSpotsSaved();
    }

    /// <summary>Spread checks on made-up land (decision 74), each named: every land point within this of a site (the starting campfire or a firepit)...</summary>
    private const float SpreadCoverM = 20f;
    /// <summary>...and no firepit nearer the land's edge (a grid point that is not land) than this, the land being at least twice as wide.</summary>
    private const float SpreadEdgeMarginM = 3f;

    /// <summary>
    /// Decision 74's rule on two shapes of land, a long strip and an L (FirepitGridStepM samples): the count follows the area, every land
    /// point is near a site, no firepit is pushed to an edge, none stands within FirepitFractalClearM of a Fractal, and the same land
    /// gives the same firepits. The rule it replaced, farthest-point sampling, put the first firepit at the strip's far tip.
    /// </summary>
    private void TestFirepitSpread()
    {
        var step = CampLaws.FirepitGridStepM;
        List<Vector3> Grid(Func<float, float, bool> inside, float sizeX, float sizeZ)
        {
            var points = new List<Vector3>();
            for (var x = step * 0.5f; x < sizeX; x += step)
                for (var z = step * 0.5f; z < sizeZ; z += step)
                    if (inside(x, z)) points.Add(new Vector3(x, 0, z));
            return points;
        }
        float Edge(Vector3 at, Func<float, float, bool> inside)
        {
            var nearest = float.MaxValue;
            for (var x = -step * 0.5f - 6; x < 130; x += step)
                for (var z = -step * 0.5f - 6; z < 40; z += step)
                    if (!inside(x, z)) nearest = Math.Min(nearest, new Vector2(at.X - x, at.Z - z).Length());
            return nearest;
        }
        var shapes = new (string Name, Func<float, float, bool> Inside, float X, float Z, Vector3 Start)[]
        {
            ("a 120 by 6 m strip", (x, z) => x is > 0 and < 120 && z is > 0 and < 6, 120, 6, new Vector3(3.75f, 0, 2.25f)),
            ("an L of 9 m arms, 33 m each way", (x, z) => (x is > 0 and < 33 && z is > 0 and < 9) || (x is > 0 and < 9 && z is > 0 and < 33), 33, 33, new Vector3(3.75f, 0, 3.75f)),
        };
        foreach (var (name, inside, sizeX, sizeZ, start) in shapes)
        {
            var land = Grid(inside, sizeX, sizeZ);
            var area = land.Count * step * step;
            var pits = CampLaws.SpreadFirepits(start, land, land, step * step);
            var sites = pits.Append(start).ToList();
            var cover = land.Max(p => sites.Min(s => CampLaws.Flat(p, s)));
            var edge = pits.Min(p => Edge(p, inside));
            Check(pits.Count == CampLaws.FirepitCountFor(area) && pits.Count >= CampLaws.MinFirepits && cover <= SpreadCoverM && edge >= SpreadEdgeMarginM &&
                  pits.SequenceEqual(CampLaws.SpreadFirepits(start, land, land, step * step)),
                $"on {name} ({area:0} m2) the firepits spread evenly: {pits.Count} of them, every land point within {cover:0.0} m of a campfire, none nearer an edge than {edge:0.0} m, the same every time");
            // A Fractal's spot: the candidates keep clear of it, and the firepits with them.
            var fractal = pits[0];
            var clear = land.Where(c => CampLaws.Flat(c, fractal) >= CampLaws.FirepitFractalClearM).ToList();
            var moved = CampLaws.SpreadFirepits(start, land, clear, step * step);
            Check(moved.Count == pits.Count && moved.All(p => CampLaws.Flat(p, fractal) >= CampLaws.FirepitFractalClearM), $"on {name} no firepit stands within 1 m of a Fractal");
        }
        var strip = Grid(shapes[0].Inside, 120, 6);
        var old = CampLaws.LegacyChooseFirepits(shapes[0].Start, strip, CampLaws.LegacyFirepitCount, CampLaws.LegacyFirepitSpacingM);
        Check(Edge(old[0], shapes[0].Inside) < SpreadEdgeMarginM, $"(the rule it replaced put its first firepit {Edge(old[0], shapes[0].Inside):0.0} m from the strip's edge, at its far tip)");
        Check(CampLaws.FirepitCountFor(0) == 0 && CampLaws.FirepitCountFor(100) == CampLaws.MinFirepits && CampLaws.FirepitCountFor(1e6f) == CampLaws.MaxFirepitsByArea &&
              CampLaws.FirepitCountFor(5 * CampLaws.AreaPerFirepitM2) == 5, "the count follows the land's area, within its minimum and maximum");
    }

    /// <summary>Item 1: the start spot per mode in the island save, the fallback to the starting campfire, the first load there; x_clock kept.</summary>
    private async Task TestStartSpot()
    {
        await NewWorld(SavePath("camp-spot"));
        var spawn = _room.SpawnFor("player").PositionM;
        Check(_host.PlaceAtStartSpot() == "first" && CampLaws.Flat(_player.GlobalPosition, spawn) < 0.03f && _host.TeamBlock()["camp"]?["spots"]?["creative"] is JsonObject,
            "the first load of an island starts at the starting campfire, and the island save keeps the mode's spot from then on");
        var there = new Vector3(-0.5f, 0.01f, -0.3f);
        Check(_player.TryTeleportTo(there) && _companion.TryTeleportTo(there + new Vector3(0.25f, 0, 0)), "(the player and the Gubble walk off)");
        await Frames(3);
        var stood = _player.LastSafePosition;
        var gubble = _companion.GlobalPosition;
        _host.NoteCampSpots();
        await NewWorld(SavePath("camp-spot"));
        Check(_host.PlaceAtStartSpot() == "saved" && _player.GlobalPosition.DistanceTo(stood) < 0.03f && _companion.GlobalPosition.DistanceTo(gubble) < 0.05f,
            "the next load puts the player and the Gubble back where they last stood");
        // Unsafe spots fall back to the starting campfire: mid-air, and inside the box. The challenge spot stays its own (one per mode).
        var state = _host.ExportRoomState()!;
        var camp = state["extensions"]!["x_play_camp"]!.AsObject();
        camp["spots"] = new JsonObject
        {
            ["challenge"] = new JsonObject { ["player_m"] = new JsonArray(-0.5, 0.0, -0.3) },
            ["creative"] = new JsonObject { ["player_m"] = new JsonArray(0.0, 1.5, 0.0), ["companion_m"] = new JsonArray(1.1, 0.0, 0.2) },
        };
        state["extensions"]!["x_clock"] = UtcClock.DeepClone();
        await NewWorld(SavePath("camp-air"));
        var rebuilt = _host.ImportRoomState((JsonObject)state.DeepClone());
        var companionSpawn = _room.SpawnFor("companion", _room.SpawnFor("player")).PositionM;
        Check(rebuilt.Ok && _host.PlaceAtStartSpot() == "fallback" && CampLaws.Flat(_player.GlobalPosition, spawn) < 0.03f && CampLaws.Flat(_companion.GlobalPosition, companionSpawn) < 0.05f,
            "a spot in mid-air (and the Gubble's inside the box) falls back to the starting campfire: " + rebuilt.Message);
        Check(_host.TeamBlock()["camp"]?["spots"]?["challenge"]?["player_m"]?[0]?.GetValue<double>() == -0.5, "another mode's spot is kept as it was: one spot per mode");
        camp["spots"]!["creative"] = new JsonObject { ["player_m"] = new JsonArray(1.1, 0.0, 0.2) };
        await NewWorld(SavePath("camp-inside"));
        Check(_host.ImportRoomState((JsonObject)state.DeepClone()).Ok && _host.PlaceAtStartSpot() == "fallback" && CampLaws.Flat(_player.GlobalPosition, spawn) < 0.03f,
            "a spot inside something (the box) falls back too");
        await NewWorld(SavePath("camp-inside"));
        Check(_host.ClockRecord?["zone_id"]?.GetValue<string>() == "Etc/UTC" && _host.TeamBlock()["clock"] is JsonObject && _host.ExportRoomState()!["extensions"]?["x_clock"] is JsonObject,
            "the island's clock record (x_clock) is kept in the island save and read back on the next load");
    }

    /// <summary>Items 2 and 3: the starting campfire, a real fire spot, lit, drawn; sitting by it rests, and the first rest teaches Fire once.</summary>
    private async Task TestStartingCampfireAndRest()
    {
        await NewWorld(SavePath("camp-rest"));
        var store = new CountingProgressStore();
        _host.ConfigureGlowProgress(store);
        var spawn = _room.SpawnFor("player").PositionM;
        var start = _host.StartingCampfire!;
        Check(start.Id == CampLaws.StartingId && start.Starting && CampLaws.Flat(start.PositionM, spawn + new Vector3(0, 0, -CampLaws.StartingFireAheadM)) < 0.01f &&
              Math.Abs(start.PositionM.Y) < 0.02f && _host.IsCampfireLit(start.Id) && _host.CampSites.Count == 1,
            $"the island's starting campfire burns 0.2 m in front of the spawn, on the floor, lit ({start.PositionM}); an indoor room has no firepits");
        var look = CampfireLook.Create(_host);
        _world!.AddChild(look);
        look.Rebuild();
        Check(look.Drawn == 1 && look.Burning == 1, "the look draws it burning");
        Check(_player.TryTeleportTo(spawn + new Vector3(0.8f, 0, 0)), "(the player stands away from the fire)");
        await Frames(3);
        var away = _host.PlayerRest();
        Check(!away.Ok && away.Words == CommandHost.RestNearFire && !_host.PlayerResting && store.Saves == 0, "away from a fire the rest key says to come closer: " + away.Words);
        Check(_player.TryTeleportTo(spawn), "(the player stands by the fire)");
        await Frames(3);
        var rest = _host.PlayerRest();
        Check(rest.Ok && rest.LearnedFire && rest.Campfire == CampLaws.StartingId && _host.GlowProgress.Has(ExpertiseLists.Fire, ExpertiseLists.FirstFire) && store.Saves == 1 && _host.PlayerResting,
            "sitting down by the starting campfire rests, and the first rest teaches the Gubble Fire: " + rest.Words);
        var again = _host.PlayerRest();
        Check(again.Ok && !again.LearnedFire && store.Saves == 1, "a second rest teaches nothing more: once per team");
        Check(_player.TryTeleportTo(spawn + new Vector3(0.1f, 0, 0)) && !_host.PlayerResting, "moving stands the player up");
    }

    /// <summary>Items 5 and 6: firepits unlit at first, lit by the Gubble's Fire as campfires saved with the island; a lit one is a full camp.</summary>
    private async Task TestFirepits()
    {
        var pits = new[] { new Vector3(-1.2f, 0, 1.0f), new Vector3(1.5f, 0, -1.1f) };
        await NewWorld(SavePath("camp-pits"));
        var store = new CountingProgressStore();
        _host.ConfigureGlowProgress(store);
        _host.LearnFireForTests();
        _host.FirepitsForTests = pits;
        var spawn = _room.SpawnFor("player").PositionM;
        Check(_host.CampSites.Count == 3 && _host.CampSites[1].Id == "firepit:1" && !_host.IsCampfireLit("firepit:1") && !_host.IsCampfireLit("firepit:2"), "the firepits start unlit");
        var beside = pits[0] + new Vector3(0.15f, 0.01f, 0);
        Check(_player.TryTeleportTo(beside), "(the player stands by the first firepit)");
        await Frames(3);
        Check(!_host.PlayerAtCamp() && _host.HealQuarterEveryHereS() == null && _host.PlayerRest().Words == CommandHost.RestUnlit,
            "an unlit firepit is no camp yet: hearts do not heal there (v5, decision 84), and sitting there says so");
        Check(_companion.TryTeleportTo(pits[0] + new Vector3(0, 0.01f, -0.3f)), "(the Gubble floats beside it)");
        await Frames(2);
        var revision = _host.Revision;
        var lit = Fire(Companion, pits[0] + new Vector3(0, 0.01f, 0), 0.05, 6, out var preview);
        Check(Ok(preview) && preview["data"]?["cell_count"]?.GetValue<int>() == 1 && Ok(lit) && _host.Revision == revision + 1 && _host.IsCampfireLit("firepit:1") &&
              _host.Nature.Fire == null && _host.Nature.Cells.Values.All(c => c.BurningS <= 0),
            "the Gubble's Fire cast on the firepit lights it, one durable revision, and nothing else catches: " + (lit["error"]?["message"]?.GetValue<string>() ?? ""));
        Check(_host.GlowProgress.Has(ExpertiseLists.Fire, ExpertiseLists.Campfire) && store.Saves == 1, "the first firepit lit teaches Campfire");
        // A lit campfire is a full camp: the camp's spends and knacks (PlayerAtCamp), its hearts' pace, and the camp the player wakes at.
        Check(_host.PlayerAtCamp() && _host.HealQuarterEveryHereS() == 1, "beside the lit campfire the player is at a camp, and hearts heal there, a quarter a second (v5, decision 91)");
        var rest = _host.PlayerRest();
        Check(rest.Ok && rest.NewCamp && _host.HomeCamp == "firepit:1" && !rest.LearnedFire, "sitting by it makes it the player's camp: " + rest.Words);
        var sat = _player.GlobalPosition;
        Check(_player.TryTeleportTo(spawn) && _player.RecoverAtSpawn() && CampLaws.Flat(_player.GlobalPosition, sat) < 0.05f, "and a knockout's recovery wakes the player there");
        // Lit campfires are saved with the island.
        await NewWorld(SavePath("camp-pits"));
        _host.FirepitsForTests = pits;
        Check(_host.IsCampfireLit("firepit:1") && !_host.IsCampfireLit("firepit:2") && _host.HomeCamp == "firepit:1", "the lit campfire and the player's camp are saved with the island");
    }

    /// <summary>The weather clock in the middle of a rain (NatureState.WeatherAt: full rain from 165 s to 225 s of each cycle).</summary>
    private const double RainAtS = 170;
    /// <summary>The nature steps (0.5 s each) of rain that put a lit campfire out: CampLaws.RainOutS.</summary>
    private static int RainSteps => (int)Math.Ceiling(CampLaws.RainOutS / NatureLaws.StepS);

    /// <summary>
    /// Decision 72: no Fire until the first rest, whoever asks, in every mode: refused before the price with a plain reason (permission_denied),
    /// Fire still listed by capabilities.list and shown locked by the journal; the first rest teaches it and Fire then works.
    /// </summary>
    private async Task TestFireLock()
    {
        await NewWorld(SavePath("camp-lock"), learnFire: false);
        var revision = _host.Revision;
        var at = new Vector3(0.2f, 0.01f, -0.6f);
        var mine = Send(Command(NextId("fire"), "nature.cast", Cast("fire", at, 0.05, 6), preview: true), Player);
        var gubble = Send(Command(NextId("fire"), "nature.cast", Cast("fire", at, 0.05, 6), preview: true), Companion);
        var commit = Send(Command(NextId("fire"), "nature.cast", Cast("fire", at, 0.05, 6, "0123456789abcdef0123456789abcdef")), Companion);
        Check(Code(mine) == "permission_denied" && Code(gubble) == "permission_denied" && Code(commit) == "permission_denied" &&
              mine["error"]?["message"]?.GetValue<string>() == CommandHost.FireNotLearned && _host.Revision == revision && _host.Nature.Fire == null,
            "before the first rest Fire is refused, the player's and the Gubble's, preview or cast, before any price: " + mine["error"]?["message"]);
        var listed = Send(Query("capabilities.list", new JsonObject()), Companion)["data"]?["items"]?.AsArray();
        var abilities = JournalPages.Build(JournalPages.Abilities, _host, 0).Lines;
        Check(listed != null && listed.Any(i => i?["capability"]?.GetValue<string>() == "fire") && abilities.Any(l => l.Text == JournalPages.FireLocked),
            "Fire stays listed (locked, not missing), and the journal's Abilities page says it is locked until the first rest");
        Check(_player.TryTeleportTo(_room.SpawnFor("player").PositionM), "(the player stands by the starting campfire)");
        await Frames(3);
        var rest = _host.PlayerRest();
        var after = Send(Command(NextId("fire"), "nature.cast", Cast("fire", at, 0.05, 6), preview: true), Player);
        Check(rest.LearnedFire && Ok(after) && !JournalPages.Build(JournalPages.Abilities, _host, 0).Lines.Any(l => l.Text == JournalPages.FireLocked),
            "the first rest by the starting campfire teaches Fire, and Fire works from then on: " + (after["error"]?["message"]?.GetValue<string>() ?? "ok"));
        await NewWorld(SavePath("camp-lock-challenge"), challenge: true, learnFire: false);
        var challenge = Send(Command(NextId("fire"), "nature.cast", Cast("fire", at, 0.05, 6), preview: true), Player);
        Check(Code(challenge) == "permission_denied" && _host.Mode == "challenge", "in Challenge too: every mode waits for the first rest");
    }

    /// <summary>
    /// Decision 73: rain puts out every lit campfire after CampLaws.RainOutS of rain (the X stop never does); one that is out is no camp
    /// (no spends, knacks or faster hearts, no rest: it says so) until the Gubble's Fire relights it, the starting campfire included; out
    /// and lit are saved with the island; PutOutCampfire is the later Snuff's call. Before Fire is learned, rain spares the starting campfire.
    /// </summary>
    private async Task TestCampfireRain()
    {
        await NewWorld(SavePath("camp-rain-spared"), learnFire: false);
        _host.Nature.WeatherS = RainAtS;
        Steps(RainSteps + 4);
        Check(_host.NatureWeather.Pattern == "rain" && _host.IsCampfireLit(CampLaws.StartingId), "before the team has learned Fire, rain spares the starting campfire (nothing could relight it)");
        var pits = new[] { new Vector3(-1.2f, 0, 1.0f), new Vector3(1.5f, 0, -1.1f) };
        await NewWorld(SavePath("camp-rain"));
        _host.FirepitsForTests = pits;
        var spawn = _room.SpawnFor("player").PositionM;
        Check(_companion.TryTeleportTo(pits[0] + new Vector3(0, 0.01f, -0.3f)), "(the Gubble floats beside the first firepit)");
        await Frames(2);
        var lit = Fire(Companion, pits[0] + new Vector3(0, 0.01f, 0), 0.05, 6, out _);
        var stopped = _host.PlayerGoal("stop");
        Check(Ok(lit) && Ok(stopped) && _host.IsCampfireLit("firepit:1"), "the player's Stop (X) does not put a campfire out: " + (lit["error"]?["message"]?.GetValue<string>() ?? "lit"));
        _host.Nature.WeatherS = RainAtS;
        Steps(RainSteps - 1);
        var before = _host.IsCampfireLit("firepit:1") && _host.IsCampfireLit(CampLaws.StartingId);
        Steps(1);
        Check(before && _host.IsCampfireOut("firepit:1") && _host.IsCampfireOut(CampLaws.StartingId) && !_host.IsCampfireLit("firepit:1"),
            $"{CampLaws.RainOutS:0} s of rain put out every lit campfire, the starting one too once Fire is known, and not a step sooner");
        Check(_player.TryTeleportTo(pits[0] + new Vector3(0.15f, 0.01f, 0)), "(the player stands by the firepit that is out)");
        await Frames(3);
        var outRest = _host.PlayerRest();
        Check(!_host.PlayerAtCamp() && _host.HealQuarterEveryHereS() == null && !outRest.Ok && outRest.Words == CommandHost.RestOut,
            "a campfire that is out is no camp: no spends or knacks, no healing hearts (v5), and resting there says so: " + outRest.Words);
        Check(_player.TryTeleportTo(spawn), "(the player stands by the starting campfire)");
        await Frames(3);
        var look = CampfireLook.Create(_host);
        _world!.AddChild(look);
        look.Rebuild();
        Check(!_host.PlayerAtCamp() && look.Out == 2 && look.Burning == 0, "the starting camp is no camp while its campfire is out, and the look draws both out (charred logs)");
        await NewWorld(SavePath("camp-rain"));
        _host.FirepitsForTests = pits;
        Check(_host.IsCampfireOut("firepit:1") && _host.IsCampfireOut(CampLaws.StartingId), "out campfires are saved with the island");
        _host.Nature.WeatherS = 10;
        Check(_companion.TryTeleportTo(pits[0] + new Vector3(0, 0.01f, -0.3f)), "(the Gubble floats beside the firepit again)");
        await Frames(2);
        var relit = Fire(Player, pits[0] + new Vector3(0, 0.01f, 0), 0.05, 6, out _);
        Check(_companion.TryTeleportTo(_room.SpawnFor("companion", _room.SpawnFor("player")).PositionM) && _player.TryTeleportTo(spawn), "(both back at the camp)");
        await Frames(2);
        var start = _host.StartingCampfire!.PositionM;
        var home = Fire(Player, start + new Vector3(0, 0.01f, 0), 0.05, 6, out _);
        Check(Ok(relit) && Ok(home) && _host.IsCampfireLit("firepit:1") && _host.IsCampfireLit(CampLaws.StartingId) && _host.PlayerAtCamp() && _host.Nature.Fire == null,
            "the Gubble's Fire relights a campfire that is out, the starting campfire included, and it is a camp again: " + (home["error"]?["message"]?.GetValue<string>() ?? "relit"));
        Check(_host.PutOutCampfire("firepit:1") && _host.IsCampfireOut("firepit:1") && !_host.PutOutCampfire("firepit:1") && !_host.PutOutCampfire("firepit:9"),
            "PutOutCampfire (the later Snuff calls it) puts a lit campfire out, once");
    }

    /// <summary>
    /// Decision 74's saves: a lit firepit keeps its id while its spot is the same (the spot is saved beside it); one whose spot moved is
    /// unlit and the island loads; a save written before the change (no spots) is matched by the old rule's spots (the test room has none).
    /// </summary>
    private async Task TestFirepitSpotsSaved()
    {
        var pits = new[] { new Vector3(-1.2f, 0, 1.0f), new Vector3(1.5f, 0, -1.1f) };
        await NewWorld(SavePath("camp-moved"));
        _host.FirepitsForTests = pits;
        foreach (var pit in pits)
        {
            Check(_companion.TryTeleportTo(pit + new Vector3(0, 0.01f, -0.3f)), "(the Gubble floats beside a firepit)");
            await Frames(2);
            Check(Ok(Fire(Player, pit + new Vector3(0, 0.01f, 0), 0.05, 6, out _)), "(the firepit is lit)");
        }
        var state = _host.ExportRoomState()!;
        var camp = state["extensions"]!["x_play_camp"]!.AsObject();
        Check(camp["pits_m"]?["firepit:1"] is JsonArray && camp["pits_m"]?["firepit:2"] is JsonArray, "each lit firepit's spot is saved beside it");
        await NewWorld(SavePath("camp-moved"));
        _host.FirepitsForTests = new[] { pits[0] + new Vector3(0.5f, 0, 0), pits[1] };
        Check(!_host.IsCampfireLit("firepit:1") && _host.IsCampfireLit("firepit:2"), "a lit firepit whose spot moved is unlit and the island loads; one whose spot is the same keeps its id, lit");
        camp.Remove("pits_m");
        await NewWorld(SavePath("camp-older"));
        _host.FirepitsForTests = pits;
        var older = _host.ImportRoomState((JsonObject)state.DeepClone());
        Check(older.Ok && !_host.IsCampfireLit("firepit:1") && !_host.IsCampfireLit("firepit:2"),
            "a save from before decision 74 loads, its firepits matched by the old rule's spots (none in this room, so unlit): " + older.Message);
    }
}
