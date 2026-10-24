using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// "-- --grow-demo" (the Grow packet's windowed launch on a scratch profile, decisions 52 and 67; never part of a runner): on the
/// landscape given, the player grows a tree by the spawn twice and climbs it as trees are climbed, then grows the plants by its foot
/// and burns one with a fire; the room is booted again from its save and the burnt plant is still gone. "-- --shots=DIR" saves a
/// picture of each step there (a window is needed for pictures). Every line it measures starts GROW_DEMO.
/// </summary>
public partial class LandscapePlayTest
{
    private string? _shots;

    private void Shot(string name)
    {
        if (_shots == null || DisplayServer.GetName() == "headless") return;
        var image = GetViewport().GetTexture().GetImage();
        image.SavePng(System.IO.Path.Combine(_shots, name + ".png"));
    }

    private async Task TestGrowDemo()
    {
        _shots = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shots=", StringComparison.Ordinal))?["--shots=".Length..];
        if (_shots != null) System.IO.Directory.CreateDirectory(_shots);
        // A windowed boot opens with the room's intro, which holds the player: skipped, as a key press would.
        GarageGame.Native.Look.Intro.RoomIntro.Of(_world)?.Skip();
        await Frames(20);
        var player = _world.Player;
        var companion = _world.Companion;
        var space = GetWorld3D().DirectSpaceState;
        var spawn = _world.Room.SpawnFor("player").PositionM;
        // A tree near the spawn whose foot the player can walk to (as TestClimbATree finds one).
        string? treeId = null;
        PlantSet? treeSet = null;
        var treeRow = -1;
        Vector3 foot = default, toward = default;
        foreach (var (set, row) in (_world.Room.Copies?.RowsNear(spawn.X, spawn.Z, 12f) ?? Enumerable.Empty<(PlantSet, int)>())
                     .Where(p => p.Item1.IsTree).OrderBy(p => p.Item1.Position(p.Item2).DistanceTo(spawn)).Take(12))
        {
            var tree = set.Tree(row);
            for (var heading = 0; heading < 8 && treeId == null; heading++)
            {
                var out2 = new Vector2(Mathf.Cos(heading * Mathf.Pi / 4), Mathf.Sin(heading * Mathf.Pi / 4));
                var at = new Vector2(tree.AxisX, tree.AxisZ) + out2 * 0.12f * 1.5f;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(at.X, tree.BaseY + 0.4f, at.Y), new Vector3(at.X, tree.BaseY - 0.6f, at.Y), RoomBuilder.WorldLayer));
                if (hit.Count == 0 || hit["normal"].AsVector3().Y < 0.85f || !_navigation.FindRoute(spawn, hit["position"].AsVector3(), 0.06f).Reaches) continue;
                if (!player.TryTeleportTo(hit["position"].AsVector3())) continue;
                (treeId, treeSet, treeRow) = (PlantCopies.IdOf(set.Name, row), set, row);
                foot = player.GlobalPosition;
                toward = new Vector3(-out2.X, 0, -out2.Y);
            }
            if (treeId != null) break;
        }
        Check(treeId != null, "GROW_DEMO: a tree near the spawn with a walkable foot");
        if (treeId == null || treeSet == null) return;
        var root = treeSet.Position(treeRow);
        var before = treeSet.Tree(treeRow);
        PlaceGubbleNear(root, toward);
        player.Rotation = new Vector3(0, Mathf.Atan2(-toward.X, -toward.Z), 0);
        await Frames(40);
        Shot("1-tree-before");
        var castAt = root - toward * 0.1f + Vector3.Up * 0.01f;
        var first = _host.PlayerNatureCast("grow", castAt);
        var castMs = _host.PlantsRefreshMs;
        var second = _host.PlayerNatureCast("grow", castAt);
        var factor = _host.Nature.Plants.TryGetValue(treeId, out var plant) ? plant.Factor : 1;
        Measure(System.FormattableString.Invariant(
            $"GROW_DEMO {treeId} at ({root.X:0.00}, {root.Z:0.00}), {treeSet.TopM(treeRow) - root.Y:0.000} m tall: two Grow casts {Ok(first)}/{Ok(second)} ({Err(first)}{Err(second)}), factor {factor:0.000}, top perch {before.Caps.Max(c => c.TopM) - root.Y:0.000} m to {(before.Caps.Max(c => c.TopM) - root.Y) * factor:0.000} m; a cast took the copies {castMs:0.00} ms ({_host.PlantsRefreshTiles} tile)"));
        Check(Ok(first) && Ok(second) && factor > 1.2f, "GROW_DEMO: the tree grows twice");
        await Frames(20);
        Shot("2-tree-grown");
        // Climb it as trees are climbed: walk into the trunk, grab, climb, pull over onto its crown.
        var stamina = _host.Vitals.Stamina;
        var grabs = player.Grabs;
        var pulls = player.PullOvers;
        var highest = foot.Y;
        player.SetControlInput(new Vector2(0, 1));
        for (var frames = 0; frames < 1800 && !(player.PullOvers > pulls && player.IsOnFloor() && !player.IsClimbing); frames++)
        {
            await Frames(1);
            highest = Mathf.Max(highest, player.GlobalPosition.Y);
            if (frames == 240) Shot("3-climbing");
        }
        player.SetControlInput(Vector2.Zero);
        await Frames(30);
        var top = (before.Caps.Max(c => c.TopM) - root.Y) * factor + root.Y;
        Measure(System.FormattableString.Invariant(
            $"GROW_DEMO climbed: grabbed {player.Grabs - grabs}, pulled over {player.PullOvers - pulls}, rose {(player.GlobalPosition.Y - foot.Y) * 100:0.0} cm (highest {(highest - foot.Y) * 100:0.0} cm; the grown top perch {(top - foot.Y) * 100:0.0} cm), stamina {stamina:0.000} to {_host.Vitals.Stamina:0.000} ({_host.Mode})"));
        Check(player.Grabs > grabs && player.PullOvers > pulls && player.GlobalPosition.Y - foot.Y > (before.Caps.Max(c => c.TopM) - root.Y) + 0.05f,
            "GROW_DEMO: the player climbs the grown tree and stands higher than its old top perch");
        Shot("4-on-the-grown-tree");
        // Burn a grown plant by the foot: grow the plants there, then light a fire on the nearest grown one.
        player.TryTeleportTo(foot);
        await Frames(20);
        var grass = _world.Room.Copies!.RowsNear(foot.X, foot.Z, 0.9f).Where(p => !p.Set.IsTree && p.Set.Kind != PlantKind.Stone && p.Set.FuelClass != "none")
            .OrderBy(p => p.Set.Position(p.Row).DistanceTo(foot - toward * 0.3f)).Select(p => (p.Set, p.Row)).FirstOrDefault();
        var target = grass.Set == null ? root : grass.Set.Position(grass.Row);
        var targetId = grass.Set == null ? treeId : PlantCopies.IdOf(grass.Set.Name, grass.Row);
        PlaceGubbleNear(target, toward);
        await Frames(10);
        var grown = _host.PlayerNatureCast("grow", target + Vector3.Up * 0.01f);
        // Decision 72: the team knows Fire, as after the first rest (the one helper every suite that lights fires uses).
        _host.LearnFireForTests();
        var preview = _host.PlayerNaturePreview("fire", target + Vector3.Up * 0.01f, 0.35f, 20);
        var lit = Ok(preview) ? _host.PlayerNatureCast("fire", target + Vector3.Up * 0.01f, 0.35f, 20, preview["data"]?["preview_id"]?.GetValue<string>()) : preview;
        var frame = 0;
        for (; frame < 60 * 40 && _host.Nature.Fire != null; frame++)
        {
            await Frames(1);
            if (frame == 180) Shot("5-burning");
        }
        await Frames(30);
        var burnt = _host.Nature.Plants.Where(p => p.Value.Burnt).Select(p => p.Key).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Measure($"GROW_DEMO fire on {targetId}: grown {Ok(grown)} ({Err(grown)}), lit {Ok(lit)} ({Err(lit)}), out after {frame / 60.0:0.0} s; burnt down {burnt.Count}: {string.Join(", ", burnt.Take(8))}");
        Check(Ok(lit) && burnt.Contains(targetId), "GROW_DEMO: the grown plant burns down");
        Shot("6-burnt");
        // Boot the room again from its save: the burnt plants are still gone, the tree still grown.
        var directory = _world.RoomDirectory;
        _world.QueueFree();
        await Frames(10);
        _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        _world.RoomDirectory = directory;
        AddChild(_world);
        for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 120 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frames(1);
        _host = CommandHost.Of(_world)!;
        _world.Player.ReadKeyboard = false;
        GarageGame.Native.Look.Intro.RoomIntro.Of(_world)?.Skip();
        var loadMs = _host.PlantsRefreshMs;
        var save = CommandHost.SavePathFor(_world.Room);
        var savedNature = Godot.FileAccess.FileExists(save) ? System.Text.Json.Nodes.JsonNode.Parse(Godot.FileAccess.GetFileAsString(save))?["nature"] : null;
        Measure($"GROW_DEMO the save: {save} ready {_host.Authority.Call("is_ready").AsBool()}, notice \"{_host.NatureNotice}\", saved plants {savedNature?["plants"]?.AsObject().Count ?? -1}, " +
                $"its problem {NatureBlock.Problem(savedNature, int.MaxValue, _world.Room) ?? "none"}");
        var still = burnt.All(id => _host.Nature.Plants.TryGetValue(id, out var p) && p.Burnt);
        var copies = _world.Built.GetNode<PlantCopiesView>(PlantCopiesView.ViewName);
        Measure(System.FormattableString.Invariant(
            $"GROW_DEMO reloaded: the burnt plants still gone {still} (drawn at {copies.FactorOf(targetId):0.00}), the grown tree {(_host.Nature.Plants.GetValueOrDefault(treeId) is { Burnt: true } ? "burnt down by the same fire, still gone" : "still grown")} (drawn at {copies.FactorOf(treeId):0.000}); the load gave the copies their sizes in {loadMs:0.00} ms"));
        Check(_world.WorldReady && still && copies.FactorOf(targetId) == 0, "GROW_DEMO: after a reload the burnt plant is still gone");
        _world.Player.TryTeleportTo(foot);
        _world.Player.Rotation = new Vector3(0, Mathf.Atan2(-toward.X, -toward.Z), 0);
        await Frames(60);
        Shot("7-after-reload");
    }

    /// <summary>The Gubble beside a spot, within its reach: the first of a few places round it the body can stand, and it stays there.</summary>
    private void PlaceGubbleNear(Vector3 spot, Vector3 toward)
    {
        var companion = _world.Companion;
        companion.Stay();
        foreach (var away in new[] { 0.3f, 0.5f, 0.8f, 1.2f })
            for (var turn = 0; turn < 8; turn++)
            {
                var direction = (-toward).Rotated(Vector3.Up, turn * Mathf.Pi / 4);
                if (companion.TryTeleportTo(spot + direction * away)) return;
            }
    }

    private static string Err(System.Text.Json.Nodes.JsonObject result) => result["error"]?["message"]?.GetValue<string>() ?? "";
}
