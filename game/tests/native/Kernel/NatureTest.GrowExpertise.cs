using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Grow's five expertise (MECHANICS.md decision 75), each a behaviour that works from the start and teaches its expertise the first time
/// it works, once per team, in the test room with plants: Tall plant (a cast brings a plant to its full size), Thicket (three plants at
/// full size within 1 m of one another), Rain garden (a cast while it rains grows twice the step, still capped), Green ash (a cast on
/// burnt ground greens the ash at once; Creative's undo takes it back), the casts counted and the hint at the tenth, the journal's Grow
/// list with smudges, and Waterpad (a cast on water grows a lily leaf the player stands on, saved, undone, capped, and burning as a plant).
/// </summary>
public partial class NatureTest
{
    /// <summary>A pool for the waterpad: its surface 15 cm over the floor (deeper than the body), west of the spawn, clear of the plants.</summary>
    private const float PoolY = 0.15f;
    private static readonly Vector2 PoolMin = new(-1.3f, 0.1f), PoolMax = new(-0.35f, 1.3f);

    private static Area3D Pool()
    {
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { PoolMin, new Vector2(PoolMax.X, PoolMin.Y), PoolMax, PoolMin, PoolMax, new Vector2(PoolMin.X, PoolMax.Y) })
            sheet.AddVertex(new Vector3(corner.X, PoolY, corner.Y));
        return RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
    }

    private bool GrowLearned(string id) => _host.GlowProgress.Has(ExpertiseLists.Grow, id);
    private JsonObject GrowAt(Vector3 at, double radius) => Send(Command(NextId("grow"), "nature.cast", Cast("grow", at, radius, 1)), Player);
    private WaterpadView? Pads => _world!.GetNode("Room").GetNodeOrNull<WaterpadView>(WaterpadView.ViewName);

    private async Task TestGrowExpertise()
    {
        await NewWorld(SavePath("grow-expertise"));
        // Tall plant: one tuft, a quarter a cast, full in four; the fourth teaches it.
        for (var cast = 0; cast < 3; cast++) GrowAt(NearGubble, 0.05);
        var notYet = GrowLearned(ExpertiseLists.TallPlant);
        var fourth = GrowAt(NearGubble, 0.05);
        Check(Ok(fourth) && !notYet && GrowLearned(ExpertiseLists.TallPlant) && !GrowLearned(ExpertiseLists.Thicket) && _host.GlowProgress.Of("grow")!.Discoveries.Count == 1,
            "Tall plant: the cast that brings a plant to its full size teaches it (not the three before)");
        // Thicket: the three tufts round NearGubble (a few centimetres apart) all at full size; the cast that completes them teaches it.
        for (var cast = 0; cast < 3; cast++) GrowAt(NearGubble, 0.15);
        var notThicket = GrowLearned(ExpertiseLists.Thicket);
        var completing = GrowAt(NearGubble, 0.15);
        Check(Ok(completing) && !notThicket && GrowLearned(ExpertiseLists.Thicket), "Thicket: three plants at full size within 1 m of one another after a cast teaches it");
        // Rain garden: while it rains a cast grows twice the step (the 0.95 m tree, a quarter dry, full in one cast in the rain: capped).
        _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f);
        _host.Nature.WeatherS = RainAtS;
        var wet = GrowAt(TreeAt + new Vector3(-0.2f, 0.01f, 0), 0.5);
        var cap = 1 + 0.4f / 0.95f;
        Check(Ok(wet) && _host.NatureWeather.Pattern == "rain" && Math.Abs(Factor(Tree) - cap) < 1e-4f && GrowLearned(ExpertiseLists.RainGarden),
            $"Rain garden: in the rain one cast grows twice the step, still capped (the tree at {Factor(Tree):0.000} of its size), and teaches it");
        _host.Nature.WeatherS = 10;
        _companion.GlobalPosition = _room.SpawnFor("companion", _room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f;
        // Green ash: a fire burns out and leaves ash; Grow on that burnt ground (no plant there) greens it at once, and undo takes it back.
        var burnt = new Vector3(0.2f, 0.01f, -0.6f);
        var fire = Fire(Player, burnt, 0.05, 6, out _);
        for (var steps = 0; _host.Nature.Fire != null && steps < 200; steps++) Steps(1);
        int Ash() => NatureState.Disk(burnt, 0.1f).Count(g => _host.Nature.Cells.TryGetValue(NatureState.Key(g.X, g.Z), out var c) && c.Ash > 0);
        var ashBefore = Ash();
        var green = GrowAt(burnt, 0.1);
        var ashAfter = Ash();
        Check(Ok(fire) && ashBefore > 0 && Ok(green) && green["data"]?["grown_plants"]?.GetValue<int>() == 0 && ashAfter == 0 && GrowLearned(ExpertiseLists.GreenAsh),
            $"Green ash: Grow on burnt ground greens its ash at once ({ashBefore} ash cells, then {ashAfter}) and teaches it; the burnt plants stay gone");
        var undone = _host.PlayerNatureUndo();
        Check(Ok(undone) && Ash() == ashBefore, "Creative's undo takes the greening back");
        // Ten Grow casts so far: the hint at the tenth points at the first expertise not learned (Waterpad), and the journal lists Grow.
        var grow = _host.GlowProgress.Of("grow")!;
        var page = JournalPages.Build(JournalPages.Discoveries, _host, 0).Lines;
        Check(grow.SuccessfulCasts == 10 && grow.DeliveredHints.SequenceEqual(new[] { "waterpad_water" }) && page.Any(l => l.Ink == JournalInk.Title && l.Text == "Grow") &&
              page.Any(l => l.Text == "Green ash") && page.Count(l => l.Ink == JournalInk.Smudge) >= 1 && page.Any(l => l.Text == "Could something grow on the water?"),
            $"the team's {grow.SuccessfulCasts} Grow casts are counted, the tenth delivered a hint for Waterpad, and the journal lists Grow's four with a smudge and the hint");
        await TestWaterpad();
    }

    /// <summary>Waterpad (decision 75): a Grow cast on water grows a lily leaf the player stands on; saved, undone, capped, and burning as a plant.</summary>
    private async Task TestWaterpad()
    {
        _world!.AddChild(Pool());
        await Frames(2);
        var on = new Vector3(-0.8f, PoolY, 0.7f);
        Check(_companion.TryTeleportTo(new Vector3(-0.2f, 0.01f, 0.7f)), "(the Gubble floats by the pool)");
        await Frames(2);
        var pad = GrowAt(on, 0.1);
        var leaf = _host.Nature.Waterpads.FirstOrDefault();
        Check(Ok(pad) && pad["data"]?["grown_plants"]?.GetValue<int>() == 1 && leaf != null && Math.Abs(leaf.PositionM.Y - (PoolY + WaterpadView.FloatM)) < 1e-4f &&
              GrowLearned(ExpertiseLists.Waterpad) && Pads?.Count == 1,
            "Grow cast on water grows one large water lily leaf at the point, floating at the surface, and teaches Waterpad: " + Canonical(pad["error"] ?? pad["data"]));
        var again = GrowAt(on + new Vector3(0.05f, 0, 0), 0.1);
        Check(Code(again) == "invalid_args", "a second leaf on top of the first is refused: " + again["error"]?["message"]);
        Check(_player.TryTeleportTo(leaf!.PositionM), "(the player steps onto the leaf)");
        await Frames(20);
        Check(_player.IsOnFloor() && !_player.IsSwimming && Math.Abs(_player.GlobalPosition.Y - leaf.PositionM.Y) < 0.01f,
            $"the player stands on the leaf, dry, at its top ({_player.GlobalPosition.Y:0.000} m over the floor; the water's surface {PoolY} m)");
        Check(_player.TryTeleportTo(_room.SpawnFor("player").PositionM), "(the player steps back ashore)");
        await NewWorld(SavePath("grow-expertise"));
        _world!.AddChild(Pool());
        await Frames(2);
        Check(_host.Nature.Waterpads.Count == 1 && Pads?.Count == 1, "the leaf is saved with the island, drawn and collided again on the next load");
        var undone = _host.PlayerNatureUndo();
        Check(Ok(undone) && _host.Nature.Waterpads.Count == 0 && Pads?.Count == 0, "Creative's undo takes the leaf back");
        for (var i = 0; i < NatureState.MaxWaterpads; i++)
            _host.Nature.Waterpads.Add(new NatureWaterpad { Id = "waterpad:" + (i + 1), PositionM = new Vector3(-1.2f + 0.31f * (i % 3), PoolY, 0.2f + 0.31f * (i / 3)) });
        Check(_companion.TryTeleportTo(new Vector3(-0.2f, 0.01f, 0.7f)), "(the Gubble floats by the pool)");
        await Frames(2);
        var full = GrowAt(new Vector3(-0.45f, PoolY, 1.2f), 0.1);
        Check(Code(full) == "invalid_args" && (full["error"]?["message"]?.GetValue<string>() ?? "").Contains($"{NatureState.MaxWaterpads} water lily leaves", StringComparison.Ordinal),
            $"the island keeps at most {NatureState.MaxWaterpads} leaves: " + full["error"]?["message"]);
        _host.Nature.Waterpads.Clear();
        var burning = GrowAt(on, 0.1);
        await Frames(2);
        _host.NatureSampleForTests = null;
        var lit = Fire(Player, _host.Nature.Waterpads[0].PositionM, 0.1, 10, out var preview);
        var steps = 0;
        for (; _host.Nature.Waterpads.Count > 0 && steps < 120; steps++) Steps(1);
        Check(Ok(burning) && Ok(preview) && Ok(lit) && _host.Nature.Waterpads.Count == 0 && Pads?.Count == 0,
            $"Fire cast on a leaf burns it like any plant: gone in {steps * NatureLaws.StepS:0.0} s, its collision with it: " + (lit["error"]?["message"]?.GetValue<string>() ?? "lit"));
        await FreeWorld();
    }
}
