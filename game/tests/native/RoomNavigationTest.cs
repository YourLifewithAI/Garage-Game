using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Navigation;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// The founder's report in the real test room: "If I walk behind the big box and summon my comp, it gets stuck on
/// the other side of the box." Boots the room as the game does, puts the box between the two avatars, and sends
/// come through the command host as key 3 does. No room or save files are written. Then the trees (TestTrunks): the navigation
/// follows the trees Grow and fire changed.
/// </summary>
public partial class RoomNavigationTest : Node3D
{
    private int _checks;
    private int _failures;

    public override async void _Ready()
    {
        try
        {
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            Check(world.WorldReady, "the room boots: " + world.LoadError);
            if (!world.WorldReady) { Finish(); return; }
            await Frames(5);
            var navigation = world.GetNodeOrNull<RoomNavigation>("RoomNavigation");
            Check(navigation != null && navigation.IsReady && world.Companion.Navigation == navigation,
                "the room bakes a navigation mesh from its static collision and gives it to the companion");
            if (navigation == null) { Finish(); return; }
            GD.Print($"ROOM_NAVIGATION_MEASURED {navigation.PolygonCount} polygons, bake {navigation.LastBakeMs:0.0} ms, agent radius {navigation.AgentRadiusM:0.000} m");

            // The big box (obj:box): 35 x 30 x 35 cm with its foot at (1.1, 0, 0.2). The player stands behind it,
            // the companion on the far side, so the straight line between them runs through the box.
            var box = world.Built.GetNode<Node3D>("Objects/box").GlobalPosition;
            var player = world.Player;
            var companion = world.Companion;
            var playerSpot = box + new Vector3(0, 0.003f, 0.43f);
            var farSide = box + new Vector3(0, 0.01f, -0.45f);
            var host = GarageGame.Native.Kernel.CommandHost.Of(world)!;
            host.PlayerGoal("stop");
            // No walkable floor inside the box (Lane P's finding, the open sea round): Recast rasterizes surfaces, not solids, so
            // the floor's top under a box taller than the agent kept a walkable polygon there, and the Gubble's place beside a
            // climber snapped inside a cliff. The nearest walkable point to the box's middle at floor height is its top or outside it.
            var inside = navigation!.ClosestPoint(box + new Vector3(0, 0.01f, 0));
            var underBox = Mathf.Abs(inside.X - box.X) < 0.17f && Mathf.Abs(inside.Z - box.Z) < 0.17f && inside.Y < box.Y + 0.25f;
            GD.Print($"NAVIGATION_MEASURED the nearest walkable point to the big box's middle at floor height: {inside - box} from the box's foot");
            Check(!underBox, $"no walkable floor inside the big box: the nearest walkable point to its middle at floor height is {inside - box} from its foot");
            Check(player.TryTeleportTo(playerSpot) && companion.TryTeleportTo(farSide), "player behind the big box, companion on the far side");
            var route = navigation.FindRoute(companion.GlobalPosition, player.GlobalPosition, 0.05f);
            Check(route.Reaches && route.LengthM > PlanarDistance(companion.GlobalPosition, player.GlobalPosition) + 0.1f,
                $"the planned route goes round the box (route {route.LengthM:0.00} m against {PlanarDistance(companion.GlobalPosition, player.GlobalPosition):0.00} m straight)");

            // Without navigation, as in the founder's playtest, it was stuck on the far side. The Gubble now floats: with no
            // walkable map it floats straight to the player, over the 30 cm box and never through it.
            companion.BindNavigation(null);
            var sent = host.PlayerGoal("come");
            Check(sent["ok"]!.GetValue<bool>(), "come is accepted from the player through the command host");
            var highestOver = 0.0f;
            var into = false;
            for (var i = 0; i < 480 && companion.CurrentIntent == "come"; i++)
            {
                await Frames(1);
                // Over the box by more than its radius (its rounded foot may round the edge), the body must be above the top.
                if (ClearanceFromSquare(companion.GlobalPosition, box, 0.175f - companion.BodyRadiusM) == 0)
                {
                    highestOver = Mathf.Max(highestOver, companion.GlobalPosition.Y - box.Y);
                    into |= companion.GlobalPosition.Y - box.Y < 0.30f - 0.002f;
                }
            }
            var gap = PlanarDistance(companion.GlobalPosition, player.GlobalPosition);
            GD.Print($"ROOM_NAVIGATION_MEASURED without navigation: intent {companion.CurrentIntent}, gap {gap:0.00} m, highest over the box {highestOver:0.000} m");
            Check(companion.CurrentIntent == "stay" && gap <= CompanionAvatar.ComeArrivalM + 0.02f && highestOver > 0.30f && !into,
                "without a walkable map the Gubble floats over the big box (never into it) and arrives, instead of staying stuck behind it");

            // With the room's navigation: round the box and arrived.
            host.PlayerGoal("stop");
            Check(companion.TryTeleportTo(farSide), "companion back on the far side");
            companion.BindNavigation(navigation);
            host.PlayerGoal("come");
            var frames = 0;
            var blocked = 0;
            var entered = false;
            for (; frames < 360 && companion.CurrentIntent == "come"; frames++)
            {
                await Frames(1);
                if (companion.GoalBlocked) blocked++;
                entered |= ClearanceFromSquare(companion.GlobalPosition, box, 0.175f) < companion.BodyRadiusM - 0.005f;
            }
            gap = PlanarDistance(companion.GlobalPosition, player.GlobalPosition);
            GD.Print($"ROOM_NAVIGATION_MEASURED with navigation: arrived in {frames} frames, gap {gap:0.00} m, blocked frames {blocked}");
            Check(companion.CurrentIntent == "stay" && gap <= CompanionAvatar.ComeArrivalM + 0.02f && blocked == 0 && !entered,
                "with navigation the summoned companion walks round the big box and arrives beside the player");

            // Follow from the far side: it comes round the box to rest near the player instead of waiting behind it.
            Check(companion.TryTeleportTo(farSide), "companion on the far side again");
            host.PlayerGoal("follow");
            await Frames(300);
            gap = PlanarDistance(companion.GlobalPosition, player.GlobalPosition);
            var way = navigation.FindRoute(companion.GlobalPosition, player.GlobalPosition, 0.05f);
            GD.Print($"ROOM_NAVIGATION_MEASURED follow from the far side: gap {gap:0.00} m, walk {way.LengthM:0.00} m");
            Check(!companion.FollowMoving && gap <= CompanionAvatar.FollowFarM + 0.01f && way.LengthM <= gap + 0.05f,
                "follow from the far side of the box comes round and rests with nothing between them");
            host.PlayerGoal("stop");
            await TestCompanionReplansOnNewBake(world, navigation, farSide, box);
            await MeasureCellSizes(world);
            await TestBeyondTheSearchCap(navigation);
            TestSeamsMatch();
            var (fieldNavigation, field) = await TestTileJoins();
            await TestReBakeAndStaleRoutes(fieldNavigation, field);
            // The trees run in rooms of their own: the test room goes first, so no two rooms share a place in the physics space.
            world.QueueFree();
            await Frames(3);
            await TestTrunks();
            Finish();
        }
        catch (System.Exception exception)
        {
            GD.PushError("Room navigation test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// The backlog's cell-size question: bake the real test room at three cell sizes, a few times each, and print the times;
    /// the frame at 60 Hz is 16.7 ms. The game's choice is RoomNavigation.CellSizeM. (Since S7 the bakes run on worker
    /// threads, and Bake waits for them, so these are the bakes' own times, not stalls of the main thread.)
    /// </summary>
    private async Task MeasureCellSizes(RoomWorld world)
    {
        foreach (var cell in new[] { 0.01f, 0.02f, 0.03f })
        {
            var probe = RoomNavigation.Create(this, world.Built, world.Room.Bounds, WorldScaleProfile.Companion, world.Companion.StepHeightM * 0.75f, cell);
            await Frames(2);
            var times = new System.Collections.Generic.List<double> { probe.LastBakeMs };
            for (var i = 0; i < 3; i++) { probe.Bake(); times.Add(probe.LastBakeMs); }
            times.Sort();
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"ROOM_NAVIGATION_CELL test room, cell {cell * 100:0} cm: median bake {times[times.Count / 2]:0.0} ms (min {times[0]:0.0}, max {times[^1]:0.0}), {probe.PolygonCount} polygons, agent radius {probe.AgentRadiusM:0.000} m"));
            Check(probe.IsReady && probe.PolygonCount > 0, $"the test room bakes at {cell * 100:0} cm cells");
            probe.QueueFree();
            await Frames(2);
        }
        var game = world.GetNode<RoomNavigation>("RoomNavigation");
        var rebakes = new System.Collections.Generic.List<double>();
        var main = new System.Collections.Generic.List<double>();
        for (var i = 0; i < 5; i++) { game.Bake(); rebakes.Add(game.LastBakeMs); main.Add(game.LastBakeMainThreadMs); }
        rebakes.Sort();
        main.Sort();
        // The bake is the workers' (about two 60 Hz frames headless); the main thread's share is reading the scene and handing over.
        Check(game.CellM == RoomNavigation.CellSizeM && main[2] < 4.0,
            $"the game's cell ({RoomNavigation.CellSizeM * 100:0} cm) bakes the test room on worker threads (median {rebakes[2]:0.0} ms), {main[2]:0.0} ms of it on the main thread (median)");
    }

    /// <summary>
    /// The founder's walk on the garage island magnified ten times (13 October): the Gubble got lost trying to reach the player.
    /// Godot's path query gives up after a number of polygons (4,096 by default) and returns a path to the polygon nearest the
    /// goal found so far; that island bakes to 55,110. Here a 5 m field of 5 cm squares (9,900 polygons, made by hand: Recast
    /// takes minutes over thousands of pillar holes) on the room navigation's own map, split by a wall whose only gap is at the
    /// far end. Start and goal stand a hand apart on either side of the wall at its closed end, so the search must cover nearly
    /// the whole field. The default query stops short; the room's route reaches round the far end.
    /// </summary>
    private async Task TestBeyondTheSearchCap(RoomNavigation navigation)
    {
        const int count = 100;
        const float square = 0.05f;
        const int gap = 8;
        var origin = new Vector3(100, 0, 0);
        int At(int i, int j) => i * (count + 1) + j;
        var vertices = new Vector3[(count + 1) * (count + 1)];
        for (var i = 0; i <= count; i++)
            for (var j = 0; j <= count; j++)
                vertices[At(i, j)] = origin + new Vector3(i * square, 0, j * square);
        var mesh = new NavigationMesh { CellSize = navigation.CellM, CellHeight = RoomNavigation.CellHeightM, Vertices = vertices };
        var wall = count / 2;
        for (var i = 0; i < count; i++)
            for (var j = 0; j < count; j++)
                if (i != wall || j >= count - gap) mesh.AddPolygon(new[] { At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j + 1) });
        var region = NavigationServer3D.RegionCreate();
        NavigationServer3D.RegionSetUseAsyncIterations(region, false);
        NavigationServer3D.RegionSetMap(region, navigation.Map);
        NavigationServer3D.RegionSetNavigationMesh(region, mesh);
        var inMap = System.Diagnostics.Stopwatch.StartNew();
        while (NavigationServer3D.MapGetClosestPointOwner(navigation.Map, origin + new Vector3(0.1f, 0, 0.1f)) != region && inMap.Elapsed.TotalSeconds < 10) await Frames(1);
        var cap = new NavigationPathQueryParameters3D().PathSearchMaxPolygons;
        Check(mesh.GetPolygonCount() > 2 * cap, $"the field has more than twice the default search cap's polygons ({mesh.GetPolygonCount()} against {cap})");
        var from = origin + new Vector3((wall - 2.5f) * square, 0, 2.5f * square);
        var to = origin + new Vector3((wall + 3.5f) * square, 0, 2.5f * square);
        var capped = NavigationServer3D.MapGetPath(navigation.Map, from, to, true);
        var cappedEnd = capped.Length > 0 ? capped[^1] : from;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var route = navigation.FindRoute(from, to, 0.05f);
        var searchMs = watch.Elapsed.TotalMilliseconds;
        var bounded = navigation.FindRoute(from, to, 0.05f, maxLengthM: 2.0f);
        var wayRound = 2 * (count - gap) * square;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED beyond the search cap: {mesh.GetPolygonCount()} polygons; start and goal {PlanarDistance(from, to):0.00} m apart across the wall; " +
            $"the default query ends {PlanarDistance(cappedEnd, to):0.00} m from the goal; the room's route {(route.Reaches ? "reaches" : "does not reach")} it, {route.LengthM:0.00} m long " +
            $"(the way round about {wayRound:0.0} m), in {searchMs:0.0} ms; held to a 2 m search it {(bounded.Reaches ? "reaches" : "does not reach")}"));
        Check(PlanarDistance(cappedEnd, to) > 0.1f, "the default query stops short of the goal on this field (the field is beyond its cap)");
        Check(route.Reaches && route.LengthM > wayRound - 0.5f,
            $"the room's route reaches the goal round the far end of the wall ({route.LengthM:0.00} m), beyond the default search cap");
        Check(!bounded.Reaches, "a route held to a search distance shorter than the way round does not reach");
        NavigationServer3D.FreeRid(region);
        await Frames(2);
    }

    // ---- tiles, bakes off the main thread, routes on a bake that is gone (packet S7 of the 10x world) ----

    /// <summary>
    /// The seams step on its own (NavigationSeams.Conform), with two hand-made tiles whose seam does not match: the right one
    /// cuts its seam edge at a corner the left one lacks, and its top corner sits a hair off the left one's (float noise, as
    /// Recast's two sides give). Recast's own tiles on the magnified island matched at all but about 30 such corners; the map
    /// joins only edges with the same two corners, so unmatched, the tiles are two islands, and matched, one.
    /// </summary>
    private void TestSeamsMatch()
    {
        NavigationIslands.TileGeometry Tile(int index, Vector3[] vertices, int[][] polygons)
        {
            var start = new System.Collections.Generic.List<int> { 0 };
            foreach (var polygon in polygons) start.Add(start[^1] + polygon.Length);
            var geometry = new NavigationIslands.TileGeometry { Tile = index, Vertices = vertices, Start = start.ToArray(), Indices = polygons.SelectMany(p => p).ToArray() };
            geometry.BuildGrid();
            return geometry;
        }
        var left = Tile(0, new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
            new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } });
        var right = Tile(1, new[] { new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 0, 1), new Vector3(1.0000002f, 0.0004f, 1), new Vector3(1, 0, 0.4f) },
            new[] { new[] { 0, 1, 4 }, new[] { 4, 1, 2 }, new[] { 4, 2, 3 } });
        var layout = new NavigationSeams.Layout(0, 0, 1, 2, 1, RoomNavigation.CellSizeM);
        var raw = new[] { left, right };
        var rawIslands = NavigationIslands.Build(1, raw, layout, RoomNavigation.CellHeightM, new NavigationIslands.Scratch());
        var conformed = NavigationSeams.Conform(raw, new[] { 0, 1 }, layout);
        var joined = NavigationIslands.Build(2, new[] { conformed[0], conformed[1] }, layout, RoomNavigation.CellHeightM, new NavigationIslands.Scratch());
        var seamCorners = conformed.Values.Select(g => string.Join(" ", g.Vertices.Where(v => Mathf.Abs(v.X - 1) < 0.001f).Distinct().OrderBy(v => v.Z).Select(v => $"{v.Z:0.0}/{v.Y:0.0000}"))).ToArray();
        GD.Print($"ROOM_NAVIGATION_MEASURED seams: two hand-made tiles, {rawIslands.Count} islands as baked, {joined.Count} once their seam is matched; seam corners left {seamCorners[0]}, right {seamCorners[1]}");
        Check(rawIslands.Count == 2 && joined.Count == 1 && seamCorners[0] == seamCorners[1],
            "tiles whose seam corners do not match are matched corner for corner, and joined");
    }

    /// <summary>
    /// A 36 x 4 m field far from the room, five tiles long (more than RoomNavigation.SmallBakeTiles, so its map is joined on
    /// a worker thread and a bake counts once its marker is in the map): the floor, a wall standing just past a seam, and a
    /// 15-degree ramp across the next. The tiles join without gaps (the walkable mesh is continuous across each seam), a
    /// route crosses both seams round the wall, another climbs the ramp over its seam, and all of it is one island.
    /// </summary>
    private async Task<(RoomNavigation Navigation, Node3D Field)> TestTileJoins()
    {
        var field = new Node3D { Name = "TileField", Position = new Vector3(200, 0, 0) };
        AddChild(field);
        StaticBody3D Block(string name, Vector3 size, Transform3D at)
        {
            var body = new StaticBody3D { Name = name, CollisionLayer = GarageGame.Native.Room.RoomBuilder.WorldLayer, CollisionMask = 0, Transform = at };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            field.AddChild(body);
            return body;
        }
        Block("Floor", new Vector3(36, 0.1f, 4), new Transform3D(Basis.Identity, new Vector3(0, -0.05f, 0)));
        // The seams of 8 m tiles laid from the field's west edge (x = 182) lie at x = 189.99, 197.99, 205.99 and 213.99 (half a
        // cell off the lattice).
        Block("Wall", new Vector3(0.1f, 0.3f, 3.0f), new Transform3D(Basis.Identity, new Vector3(-1.4f, 0.15f, -0.5f)));
        // A 50 cm block with a walkable top no walk reaches: an island of its own.
        Block("Block", new Vector3(1.0f, 0.5f, 1.0f), new Transform3D(Basis.Identity, new Vector3(-6.0f, 0.25f, 1.0f)));
        // Rough ground across the last seam (local x = 13.99): a 3 x 3 m trimesh of 1 to 2 cm bumps, so each side's Recast cuts
        // the seam at corners of its own (its walkable areas and detail heights), as the magnified island's land does.
        var bumps = new System.Collections.Generic.List<Vector3>();
        float Height(float x, float z) => 0.012f + 0.008f * Mathf.Sin(x * 23f) * Mathf.Cos(z * 17f) + 0.004f * Mathf.Sin((x + z) * 41f);
        for (var i = 0; i < 60; i++)
            for (var j = 0; j < 60; j++)
            {
                Vector3 At(int a, int b) { var (x, z) = (12.5f + a * 0.05f, -1.5f + b * 0.05f); return new Vector3(x, Height(x, z), z); }
                foreach (var (a, b, c) in new[] { (At(i, j), At(i + 1, j), At(i + 1, j + 1)), (At(i, j), At(i + 1, j + 1), At(i, j + 1)) })
                {
                    // Godot's front faces run clockwise seen from the front: (b - a) x (c - a) points away from it, down.
                    if ((b - a).Cross(c - a).Y > 0) { bumps.Add(a); bumps.Add(c); bumps.Add(b); }
                    else { bumps.Add(a); bumps.Add(b); bumps.Add(c); }
                }
            }
        var rough = new StaticBody3D { Name = "Rough", CollisionLayer = GarageGame.Native.Room.RoomBuilder.WorldLayer, CollisionMask = 0 };
        rough.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = bumps.ToArray() } });
        field.AddChild(rough);
        // A 15-degree ramp rising westward out of the floor east of the second seam, over the seam, to a 44 cm top.
        Block("Ramp", new Vector3(3.0f, 0.1f, 1.6f), new Transform3D(new Basis(Vector3.Forward, Mathf.DegToRad(15)), new Vector3(6.0f, 0.0f, 1.0f)));
        await Frames(2);
        // The bake bounds are in the field's own space; routes and closest points are in the world's.
        var navigation = RoomNavigation.Create(this, field, new Aabb(new Vector3(-18, -0.2f, -2), new Vector3(36, 1.0f, 4)), WorldScaleProfile.Companion, 0.02f);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!navigation.IsReady && waited.Elapsed.TotalSeconds < 30) await Frames(1);
        Check(navigation.IsReady && navigation.TileCount == 5 && navigation.LastTilesBaked == 5,
            $"a 36 m field bakes in 8 m tiles ({navigation.TileCount} tiles, {navigation.LastTilesBaked} baked, {navigation.PolygonCount} polygons in {navigation.LastBakeMs:0} ms), ready after {waited.Elapsed.TotalMilliseconds:0} ms");
        var seams = new[] { 189.99f, 197.99f, 205.99f, 213.99f };
        var worstGap = 0f;
        foreach (var seam in seams)
            foreach (var z in new[] { -1.8f, -1.2f, -0.6f })
            {
                var on = new Vector3(seam, 0, z);
                worstGap = Mathf.Max(worstGap, PlanarDistance(navigation.ClosestPoint(on), on));
            }
        var from = new Vector3(183, 0, 0);
        var to = new Vector3(217, 0, 0.2f);
        var route = navigation.FindRoute(from, to, 0.05f);
        var crossings = seams.Count(seam => route.Points.Any(p => p.X < seam) && route.Points.Any(p => p.X > seam));
        var upRamp = navigation.FindRoute(new Vector3(207.6f, 0, 1.0f), new Vector3(205.0f, 0.32f, 1.0f), 0.05f);
        var highest = upRamp.Points.Length == 0 ? 0 : upRamp.Points[^1].Y;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED tiles: {navigation.TileCount} tiles of {RoomNavigation.TileM:0} m, {navigation.PolygonCount} polygons, {navigation.IslandCount} islands, baked in {navigation.LastBakeMs:0} ms ({navigation.LastBakeMainThreadMs:0.0} ms of the main thread); " +
            $"the widest gap from a seam to the walkable mesh {worstGap * 1000:0.0} mm; the route across the field {(route.Reaches ? "reaches" : "does not reach")}, {route.LengthM:0.00} m against {PlanarDistance(from, to):0.00} m straight, crossing {crossings} seams; " +
            $"up the ramp across the second seam it {(upRamp.Reaches ? "reaches" : "does not reach")}, {highest:0.00} m up"));
        Check(worstGap < 0.005f, $"the tiles join without gaps: the walkable mesh reaches every seam (widest gap {worstGap * 1000:0.0} mm)");
        Check(route.Reaches && crossings == 4 && route.LengthM < PlanarDistance(from, to) + 1.0f,
            $"a route crosses all four seams round the wall and reaches ({route.LengthM:0.00} m, {crossings} seams)");
        Check(upRamp.Reaches && highest > 0.25f, $"a route up the ramp across a seam reaches its top ({highest:0.00} m up)");
        // Over the rough ground, every short step across its seam is direct: the two tiles' polygons meet edge to edge all along it.
        var steps = 0;
        var roundabout = 0;
        var worstStep = 0f;
        for (var z = -1.2f; z <= 1.2f; z += 0.1f)
        {
            var step = navigation.FindRoute(new Vector3(213.94f, 0.02f, z), new Vector3(214.04f, 0.02f, z), 0.03f);
            steps++;
            var length = step.Reaches ? step.LengthM : float.PositiveInfinity;
            worstStep = Mathf.Max(worstStep, length);
            if (length > 0.2f) roundabout++;
        }
        GD.Print($"ROOM_NAVIGATION_MEASURED rough ground across a seam: {steps} steps of 10 cm across it, {roundabout} not direct (the longest {worstStep:0.00} m)");
        Check(roundabout == 0, $"every 10 cm step across the seam over rough ground is direct ({roundabout} of {steps} were not; the longest {worstStep:0.00} m)");
        Check(navigation.IslandAt(from) >= 0 && navigation.IslandAt(from) == navigation.IslandAt(to), "the field's floor is one island across its seams");
        // The block's top: another island, so out of reach at once, with no search (the route is the start's point alone).
        var top = new Vector3(194.0f, 0.5f, 1.0f);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var toTop = navigation.FindRoute(from, top, 0.05f);
        var answeredMs = watch.Elapsed.TotalMilliseconds;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED the block's top: island {navigation.IslandAt(top)} against the floor's {navigation.IslandAt(from)}; the route {(toTop.Reaches ? "reaches" : "does not reach")} it, answered in {answeredMs:0.000} ms with {toTop.Points.Length} points"));
        Check(navigation.IslandAt(top) >= 0 && navigation.IslandAt(top) != navigation.IslandAt(from) && !toTop.Reaches && toTop.Points.Length == 1,
            "a goal on another island is out of reach at once, without a search");
        return (navigation, field);
    }

    /// <summary>
    /// A box set down across the field's middle tile is baked into the map on worker threads: only the tiles round it are
    /// baked again, no physics tick holds 4 ms for it, and routes then go round it. A route planned before is refused once
    /// the new bake is in the map (TryTake), and planned again.
    /// </summary>
    private async Task TestReBakeAndStaleRoutes(RoomNavigation navigation, Node3D field)
    {
        var from = new Vector3(200.5f, 0, -1.0f);
        var to = new Vector3(203.5f, 0, -1.0f);
        var before = navigation.RequestRoute(from, to, 0.05f);
        before.Wait(1000);
        var straight = navigation.FindRoute(from, to, 0.05f);
        var revision = navigation.Revision;
        Check(before.IsDone && before.Revision == revision && before.TryTake(out var planned) && planned.Reaches && navigation.IsCurrent(planned),
            "a route searched on a worker thread is found, and is current, on the bake it was planned on");
        var block = new StaticBody3D { Name = "Dropped", CollisionLayer = GarageGame.Native.Room.RoomBuilder.WorldLayer, CollisionMask = 0, Position = new Vector3(2.0f, 0.1f, -1.0f) };
        block.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.4f, 0.2f, 1.2f) } });
        field.AddChild(block);
        navigation.WorstTickMs = 0;
        var ticks = 0;
        var worstFrame = 0.0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var frame = new System.Diagnostics.Stopwatch();
        while (navigation.Revision == revision && clock.Elapsed.TotalSeconds < 10)
        {
            frame.Restart();
            await Frames(1);
            worstFrame = Math.Max(worstFrame, frame.Elapsed.TotalMilliseconds);
            ticks++;
        }
        var round = navigation.FindRoute(from, to, 0.05f);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED a box set down: re-baked {navigation.LastTilesBaked} of {navigation.TileCount} tiles in {navigation.LastBakeMs:0.0} ms on worker threads after {ticks} ticks; " +
            $"the navigation's worst tick {navigation.WorstTickMs:0.00} ms (the worst whole tick {worstFrame:0.0} ms), {navigation.LastBakeMainThreadMs:0.0} ms of the main thread in all; the route {straight.LengthM:0.00} m before, {round.LengthM:0.00} m round it after"));
        Check(navigation.Revision > revision && navigation.LastTilesBaked == 1,
            $"setting a box down re-bakes only its tile ({navigation.LastTilesBaked} of {navigation.TileCount}), by itself");
        Check(navigation.WorstTickMs < 4.0, $"no physics tick holds 4 ms for the re-bake (the navigation's worst tick {navigation.WorstTickMs:0.00} ms)");
        Check(round.Reaches && round.LengthM > straight.LengthM + 0.1f, $"routes go round the box once it is in the map ({round.LengthM:0.00} m against {straight.LengthM:0.00} m)");
        // The route searched before the box came: planned on a bake that is no longer in the map, so it is refused.
        var stale = navigation.RequestRoute(from, to, 0.05f);
        stale.Wait(1000);
        block.Position += new Vector3(0, 0, 3.0f);
        var moved = navigation.Revision;
        clock.Restart();
        while (navigation.Revision == moved && clock.Elapsed.TotalSeconds < 10) await Frames(1);
        Check(navigation.Revision > moved && stale.IsDone && stale.Stale && !stale.TryTake(out _) && !navigation.IsCurrent(round),
            "a route planned on a bake that is no longer in the map is refused, not trusted");
        var again = navigation.RequestRoute(from, to, 0.05f);
        again.Wait(1000);
        Check(again.TryTake(out var replanned) && replanned.Reaches && replanned.Revision == navigation.Revision && replanned.LengthM < round.LengthM - 0.1f,
            $"planned again on the new bake, the route is straight once more ({replanned.LengthM:0.00} m)");
        field.QueueFree();
        navigation.QueueFree();
        await Frames(2);
    }

    /// <summary>
    /// The companion re-plans on a new bake: something set down in the test room while it walks round the big box is baked in,
    /// and within a tick or two of the bake reaching the map the companion's route is planned on it; it still arrives.
    /// </summary>
    private async Task TestCompanionReplansOnNewBake(RoomWorld world, RoomNavigation navigation, Vector3 farSide, Vector3 box)
    {
        var host = GarageGame.Native.Kernel.CommandHost.Of(world)!;
        var companion = world.Companion;
        var player = world.Player;
        host.PlayerGoal("stop");
        Check(companion.TryTeleportTo(farSide), "the companion on the far side of the big box once more");
        await Frames(5);
        host.PlayerGoal("come");
        var block = new StaticBody3D { Name = "SetDown", CollisionLayer = GarageGame.Native.Room.RoomBuilder.WorldLayer, CollisionMask = 0, Position = box + new Vector3(-0.45f, 0.06f, -0.45f) };
        block.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.12f, 0.12f, 0.12f) } });
        world.Built.GetNode("Objects").AddChild(block);
        var revision = navigation.Revision;
        int frames = 0, bakedAt = -1, replannedAt = -1, pendingTicks = 0;
        for (; frames < 600 && companion.CurrentIntent == "come"; frames++)
        {
            await Frames(1);
            if (companion.RoutePending) pendingTicks++;
            if (bakedAt < 0 && navigation.Revision > revision) bakedAt = frames;
            if (bakedAt >= 0 && replannedAt < 0 && companion.RouteRevision == navigation.Revision) replannedAt = frames;
        }
        var gap = PlanarDistance(companion.GlobalPosition, player.GlobalPosition);
        GD.Print($"ROOM_NAVIGATION_MEASURED a box set down while the companion walks: baked in at tick {bakedAt}, its route planned on the new bake at tick {replannedAt}, held {pendingTicks} ticks for a route; arrived in {frames} ticks, gap {gap:0.00} m");
        Check(bakedAt >= 0 && (replannedAt >= 0 && replannedAt - bakedAt <= 2 || companion.CurrentIntent == "stay" && replannedAt < 0 && bakedAt >= frames - 2),
            $"the walking companion plans again on the new bake within two ticks of it reaching the map (baked at {bakedAt}, planned at {replannedAt})");
        Check(companion.CurrentIntent == "stay" && gap <= CompanionAvatar.ComeArrivalM + 0.02f, "and still arrives beside the player");
        block.QueueFree();
        revision = navigation.Revision;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (navigation.Revision == revision && clock.Elapsed.TotalSeconds < 10) await Frames(1);
        host.PlayerGoal("stop");
    }

    // ---- the navigation follows grown and burnt trees (Lane P) ----

    /// <summary>The trunk the planted rows give every tree, at its natural size (the fixture's broadleaf is 0.055 m: these are thick, so Grow's factor of 1.42 closes a gap).</summary>
    private const float TrunkRadiusM = 0.15f;
    /// <summary>
    /// Three places in the test room, each a ring of five trees round a middle: pen A is open (the trunks stand 15 cm apart) and
    /// closes when its trees are grown (their trunks reach 3 cm); pen B is closed (5 cm apart) and opens when one tree burns; one
    /// tree stands alone, for the wider map's re-bake.
    /// </summary>
    private static readonly Vector2 PenA = new(0.9f, -0.65f), PenB = new(-1.2f, 0.6f), LoneTree = new(-1.0f, -0.2f);
    private const float PenARadiusM = 0.385f, PenBRadiusM = 0.30f;
    private const string TrunksRoot = "user://tests/nav_trunks";
    private int _trunkIds;

    private static Vector2 RingAt(Vector2 middle, float radius, int k)
    {
        var angle = Mathf.DegToRad(90 + 72 * k);
        return middle + radius * new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private static IslandFixture.Row TreeRow(Vector2 at, uint seed) => new(at.X, 0, at.Y, 15f, Vector3.One, new Color(0.9f, 1f, 0.85f), 0.95f, 0, 0, seed,
        new[] { at.X, at.Y, TrunkRadiusM, 0.035f, 0.53f, 0.935f }, new[] { new[] { at.X, at.Y, 0.15f, 0.15f, 0.8f, 0.94f, 0.06f, 0.06f } });

    /// <summary>The test room with eleven trees: pen A's five, pen B's five and the lone one (one broadleaf set; the rows sort by tile, so ids are looked up by place).</summary>
    private static string WritePlantedRoom()
    {
        var rows = new List<IslandFixture.Row>();
        uint seed = 40;
        for (var k = 0; k < 5; k++) rows.Add(TreeRow(RingAt(PenA, PenARadiusM, k), seed++));
        for (var k = 0; k < 5; k++) rows.Add(TreeRow(RingAt(PenB, PenBRadiusM, k), seed++));
        rows.Add(TreeRow(LoneTree, seed));
        return IslandFixture.WithPlants(RoomWorld.DefaultRoom, $"{TrunksRoot}/room/test_room", new[] { IslandFixture.Trees(rows) });
    }

    private static string TreeId(RoomData room, Vector2 at)
    {
        var set = room.Copies!.Find("broadleaf")!;
        for (var row = 0; row < set.Count; row++)
            if (Mathf.Abs(set.Position(row).X - at.X) < 0.01f && Mathf.Abs(set.Position(row).Z - at.Y) < 0.01f) return PlantCopies.IdOf("broadleaf", row);
        throw new InvalidOperationException($"no tree at {at}");
    }

    private static Vector3 OnFloor(Vector2 at) => new(at.X, 0, at.Y);

    /// <summary>Soil everywhere at floor height, dry enough to burn (as the nature suite's).</summary>
    private static NatureCell? Soil(int x, int z) => new() { X = x, Z = z, Y = 0, Surface = "shell:soil", Material = "grass", Fertile = true, Fuel = 0.7f, Moisture = 0.35f };

    private JsonObject Say(CommandHost host, RoomData room, string op, JsonObject args, bool preview = false, int? expectedRevision = null)
    {
        var command = new JsonObject { ["schema"] = "enfractal.command", ["version"] = 1, ["action_id"] = $"trunks-{++_trunkIds}", ["room_id"] = room.RoomId, ["op"] = op, ["args"] = args };
        if (preview) command["preview"] = true;
        if (expectedRevision != null) command["expected_revision"] = expectedRevision;
        return JsonNode.Parse(host.Handle(command.ToJsonString(), CommandHost.PlayerPrincipal))!.AsObject();
    }

    private static JsonObject CastArgs(string capability, Vector3 centre, double radius, double duration, string? previewId = null)
    {
        var args = new JsonObject { ["capability"] = capability, ["center_m"] = new JsonArray(centre.X, centre.Y, centre.Z), ["radius_m"] = radius, ["duration_s"] = duration };
        if (previewId != null) args["preview_id"] = previewId;
        return args;
    }

    private static bool Ok(JsonObject result) => result["ok"]?.GetValue<bool>() == true;
    private static string Why(JsonObject result) => result["error"]?["message"]?.GetValue<string>() ?? "";

    /// <summary>Grow, twice at once (a tree needs two to reach its cap of 1.42), the Gubble standing where it sees the circle's middle.</summary>
    private async Task<bool> GrowAt(CommandHost host, RoomData room, RoomWorld world, Vector3 gubbleAt, Vector2 at, double radius, int casts = 2)
    {
        Check(world.Companion.TryTeleportTo(gubbleAt), "the Gubble goes where it can see the Grow cast's middle");
        await Frames(3);
        var ok = true;
        for (var i = 0; i < casts; i++)
        {
            var cast = Say(host, room, "nature.cast", CastArgs("grow", OnFloor(at) + new Vector3(0, 0.01f, 0), radius, 1));
            if (!Ok(cast)) GD.Print("ROOM_NAVIGATION_MEASURED a Grow cast was refused: " + Why(cast));
            ok &= Ok(cast);
        }
        return ok;
    }

    /// <summary>Fire at a spot among the trees, committed as the preview said it would be; then the simulation stepped until the fire is out.</summary>
    private async Task<bool> BurnAt(CommandHost host, RoomData room, RoomWorld world, Vector3 gubbleAt, Vector3 centre, double radius)
    {
        Check(world.Companion.TryTeleportTo(gubbleAt), "the Gubble goes where it can see the fire's middle");
        await Frames(3);
        var preview = Say(host, room, "nature.cast", CastArgs("fire", centre, radius, 30), preview: true);
        var token = preview["data"]?["preview_id"]?.GetValue<string>();
        var lit = Say(host, room, "nature.cast", CastArgs("fire", centre, radius, 30, token));
        if (!Ok(lit)) GD.Print("ROOM_NAVIGATION_MEASURED the fire was refused: " + Why(preview) + " / " + Why(lit));
        for (var step = 0; step < 240 && host.Nature.Fire != null; step++) host.AdvanceNature(NatureLaws.StepS);
        return Ok(lit) && host.Nature.Fire == null;
    }

    /// <summary>Frames until the navigation holds a bake newer than revision (false after seconds).</summary>
    private async Task<bool> Rebaked(RoomNavigation navigation, int revision, double seconds = 15)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (navigation.Revision <= revision && clock.Elapsed.TotalSeconds < seconds) await Frames(1);
        return navigation.Revision > revision;
    }

    /// <summary>The trees nature lists at other than their natural size (a landscape's grass and flowers burn too, and have no trunk).</summary>
    private static int TreesResized(CommandHost host, RoomData room) =>
        host.Nature.Plants.Count(p => p.Value.Shown != 1f && room.Copies!.TryResolve(p.Key, out var set, out _) && set.Kind == PlantKind.Tree);

    /// <summary>Frames until the navigation follows as many trees as nature lists at other than natural size, and has baked them (false after seconds).</summary>
    private async Task<bool> Follows(RoomNavigation navigation, CommandHost host, RoomData room, double seconds = 15)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool Caught() => navigation.PlantsFollowed == TreesResized(host, room) && !navigation.Baking;
        while (!Caught() && clock.Elapsed.TotalSeconds < seconds) await Frames(1);
        // One more poll and its bake, if nature moved again while this one went to the map.
        await Frames(30);
        while (!Caught() && clock.Elapsed.TotalSeconds < seconds) await Frames(1);
        return Caught();
    }

    private static bool Reaches(RoomNavigation navigation, Vector3 from, Vector3 to) => navigation.FindRoute(from, to, 0.05f).Reaches;

    private async Task<RoomWorld?> BootPlanted(string directory, double seconds = 60)
    {
        var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        world.RoomDirectory = directory;
        AddChild(world);
        for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < seconds && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
        Check(world.WorldReady, "the room boots: " + world.LoadError);
        if (!world.WorldReady) { world.QueueFree(); return null; }
        var navigation = world.GetNodeOrNull<RoomNavigation>("RoomNavigation");
        for (var waited = System.Diagnostics.Stopwatch.StartNew(); navigation != null && !navigation.IsReady && waited.Elapsed.TotalSeconds < seconds;) await Frames(1);
        Check(navigation is { IsReady: true }, "and bakes its navigation");
        return navigation is { IsReady: true } ? world : null;
    }

    /// <summary>
    /// The trees as Grow and fire leave them: a grown tree's trunk is an obstacle at its grown size and a burnt tree is none, as the
    /// copies' view collides them, when a cast or a fire changes them and on load; only the tiles round the trees change are baked
    /// again. In a planted copy of the test room: pen A's open gaps close when its trees are grown, the Gubble's go_to round it
    /// arrives; pen B's closed ring opens when a tree burns; a reload of the island keeps both.
    /// </summary>
    private async Task TestTrunks()
    {
        TestObstaclesAtSize();
        await WithTestStores(TrunksInPlantedRoom);
        // A landscape given to the run (-- --landscape=DIR, as the landscape suite takes one) is measured the same way: see below.
        var landscape = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--landscape=", StringComparison.Ordinal))?["--landscape=".Length..];
        if (!string.IsNullOrEmpty(landscape)) await WithTestStores(() => TrunksOnTheLandscape(landscape.Replace('\\', '/')));
    }

    /// <summary>
    /// Runs a part of the test with the island's save, the team's stores and the profile the test's own, never the player's: a
    /// cast writes the island's save and teaches the team's expertise. Removed afterwards.
    /// </summary>
    private async Task WithTestStores(Func<Task> body)
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        var scratch = ProjectSettings.GlobalizePath(TrunksRoot);
        if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, recursive: true);
        try
        {
            CommandHost.SaveRoot = TrunksRoot + "/saves";
            CommandHost.DefaultGlowProgressPath = TrunksRoot + "/expertise.json";
            CommandHost.DefaultPlayerVitalsPath = TrunksRoot + "/vitals.json";
            CommandHost.DefaultWishSaveRoot = TrunksRoot + "/wishes";
            RoomHud.ProfilePath = TrunksRoot + "/profile.cfg";
            await body();
        }
        finally
        {
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
            if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, recursive: true);
        }
    }

    private async Task TrunksInPlantedRoom()
    {
        var directory = WritePlantedRoom();
        var room = RoomData.Load(directory);
        var pennedA = OnFloor(PenA);
        var outsideA = OnFloor(PenA) + new Vector3(-0.8f, 0, 0);
        var pennedB = OnFloor(PenB);
        var outsideB = OnFloor(PenB) + new Vector3(0.8f, 0, 0);
        var aIds = Enumerable.Range(0, 5).Select(k => TreeId(room, RingAt(PenA, PenARadiusM, k))).ToArray();
        var bIds = Enumerable.Range(0, 5).Select(k => TreeId(room, RingAt(PenB, PenBRadiusM, k))).ToArray();

        var world = await BootPlanted(directory);
        if (world == null) return;
        var navigation = world.GetNode<RoomNavigation>("RoomNavigation");
        var host = CommandHost.Of(world)!;
        host.LearnFireForTests();
        host.NatureSampleForTests = Soil;

        // The trees as the room's data has them: pen A's gaps are open, pen B's ring is closed.
        Check(navigation.PlantsFollowed == 0 && navigation.LastRoomObstructions == 11, $"before anything grows, the navigation bakes the room's eleven trunks as they are ({navigation.LastRoomObstructions})");
        Check(Reaches(navigation, pennedA, outsideA) && navigation.IslandAt(pennedA) == navigation.IslandAt(outsideA) && navigation.IslandAt(pennedA) >= 0,
            "pen A, its trunks 15 cm apart, can be walked out of");
        Check(!Reaches(navigation, pennedB, outsideB) && navigation.IslandAt(pennedB) >= 0 && navigation.IslandAt(pennedB) != navigation.IslandAt(outsideB),
            "pen B, its trunks 5 cm apart, cannot (its middle is an island of its own)");

        // A grown tree is an obstacle at its grown size: two Grow casts take pen A's trees to 1.42, their trunks to 21 cm, and the gaps close.
        var revision = navigation.Revision;
        var grew = await GrowAt(host, room, world, OnFloor(PenA) + new Vector3(0.04f, 0.01f, 0), PenA, 0.5);
        var grownFactor = host.Nature.Plants.GetValueOrDefault(aIds[0])?.Factor ?? 1;
        Check(grew && aIds.All(id => host.Nature.Plants.GetValueOrDefault(id)?.Factor > 1.4f), $"Grow takes pen A's five trees to their cap (factor {grownFactor:0.000})");
        var rebaked = await Rebaked(navigation, revision);
        var scanMs = navigation.LastPlantScanMs;
        Check(rebaked && await Follows(navigation, host, room) && navigation.PlantsFollowed == 5, $"a cast changes the navigation within a moment: the trees are re-baked at their grown size ({navigation.PlantsFollowed} followed)");
        Check(!Reaches(navigation, pennedA, outsideA) && navigation.IslandAt(pennedA) >= 0 && navigation.IslandAt(pennedA) != navigation.IslandAt(outsideA),
            "pen A, grown, cannot be walked out of: a grown tree blocks a route it did not block before");
        var growThrough = navigation.FindRoute(OnFloor(RingAt(PenA, PenARadiusM, 1)), OnFloor(PenA), 0.05f);
        var nearest = navigation.ClosestPoint(OnFloor(RingAt(PenA, PenARadiusM, 0)));
        var nearestFromAxis = new Vector2(nearest.X, nearest.Z).DistanceTo(RingAt(PenA, PenARadiusM, 0));
        Check(nearestFromAxis >= TrunkRadiusM * grownFactor, $"nothing inside a grown trunk is walkable: the nearest walkable point to a trunk's axis is {nearestFromAxis * 100:0.0} cm from it (the grown trunk is {TrunkRadiusM * grownFactor * 100:0.0} cm)");
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED trunks grown: pen A's five trees to {grownFactor:0.00}; re-baked {navigation.LastTilesBaked} of {navigation.TileCount} tiles in {navigation.LastBakeMs:0.0} ms, {navigation.LastBakeMainThreadMs:0.0} ms of the main thread; nature read in {scanMs:0.00} ms; {navigation.LastRoomObstructions} obstructions"));

        // The Gubble's go_to across the grown pen: the way through (the open gaps) is gone, so it walks round, and arrives.
        var west = OnFloor(PenA) + new Vector3(-0.8f, 0, 0);
        var east = OnFloor(PenA) + new Vector3(0.85f, 0, 0);
        var companion = world.Companion;
        host.PlayerGoal("stop");
        Check(companion.TryTeleportTo(west + new Vector3(0, 0.01f, 0)), "the Gubble west of pen A");
        await Frames(5);
        var straight = PlanarDistance(west, east);
        var way = navigation.FindRoute(west, east, 0.05f);
        var sent = host.PlayerGoal("go_to", east);
        var serial = companion.IntentSerial;
        int frames = 0, blocked = 0;
        float closest = float.MaxValue, deepest = float.MaxValue;
        var walked = 0.0f;
        var last = companion.GlobalPosition;
        for (; frames < 2400 && companion.GoToArrivedSerial != serial; frames++)
        {
            await Frames(1);
            if (companion.GoalBlocked) blocked++;
            closest = Mathf.Min(closest, PlanarDistance(companion.GlobalPosition, pennedA));
            foreach (var id in aIds) deepest = Mathf.Min(deepest, new Vector2(companion.GlobalPosition.X, companion.GlobalPosition.Z).DistanceTo(TreeRoot(room, id)) - TrunkRadiusM * grownFactor);
            walked += PlanarDistance(companion.GlobalPosition, last);
            last = companion.GlobalPosition;
        }
        var arrivedAt = PlanarDistance(companion.GlobalPosition, east);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED go_to across grown pen A: {(companion.GoToArrivedSerial == serial ? "arrived" : "did not arrive")} in {frames} frames, {arrivedAt * 100:0.0} cm from the goal, walked {walked:0.00} m " +
            $"(straight {straight:0.00} m, planned {way.LengthM:0.00} m), blocked {blocked} frames, never nearer the pen's middle than {closest * 100:0} cm, never nearer a grown trunk than {deepest * 100:0.0} cm"));
        Check(Ok(sent) && companion.GoToArrivedSerial == serial && arrivedAt <= CommandHost.GoToStopM + 0.03f && blocked == 0,
            $"the Gubble's go_to across a grown pen arrives, never reported blocked ({frames} frames, {arrivedAt * 100:0.0} cm off, blocked {blocked})");
        Check(way.Reaches && way.LengthM > straight + 0.3f && closest > PenARadiusM && deepest > 0,
            $"it went round: planned {way.LengthM:0.00} m against {straight:0.00} m straight, never inside the ring (nearest {closest * 100:0} cm to its middle) or a grown trunk");
        host.PlayerGoal("stop");

        // A burnt tree is no obstacle at all: a fire on pen B's east tree burns it down, and the ring opens.
        revision = navigation.Revision;
        var east_k = Enumerable.Range(0, 5).OrderByDescending(k => RingAt(PenB, PenBRadiusM, k).X).First();
        var treeAt = RingAt(PenB, PenBRadiusM, east_k);
        var watching = OnFloor(treeAt) + new Vector3(0.5f, 0.01f, 0.1f);
        var burnt = await BurnAt(host, room, world, watching, OnFloor(treeAt) + new Vector3(0.2f, 0.01f, 0), 0.3);
        var down = bIds.Where(id => host.Nature.Plants.GetValueOrDefault(id) is { Burnt: true }).ToArray();
        Check(burnt && down.Contains(bIds[east_k]), $"a fire burns pen B's east tree down ({down.Length} of pen B's trees burnt down)");
        Check(await Follows(navigation, host, room) && navigation.PlantsBurnt >= down.Length && navigation.Revision > revision,
            $"the navigation follows the fire: {navigation.PlantsBurnt} burnt trees are no obstacle ({navigation.PlantChangesSeen} changes seen, {navigation.PlantsMarkedDirty} trees marked)");
        Check(Reaches(navigation, pennedB, outsideB) && navigation.IslandAt(pennedB) == navigation.IslandAt(outsideB),
            "pen B, a tree burnt, can be walked out of: a burnt tree opens a route it closed");
        Check(!Reaches(navigation, pennedA, outsideA), "while pen A, grown, is closed still");

        // Creative's undo takes the fire back: the tree stands again and the ring closes (the navigation reads the state, not the cause).
        if (host.NatureCanUndo)
        {
            revision = navigation.Revision;
            var undo = Say(host, room, "room.undo", new JsonObject { ["to_revision"] = host.Revision - 1 }, expectedRevision: host.Revision);
            Check(Ok(undo) && await Follows(navigation, host, room) && navigation.Revision > revision && !Reaches(navigation, pennedB, outsideB),
                "Creative's undo of the fire brings the trees back, and pen B is closed again: " + Why(undo));
            var again = await BurnAt(host, room, world, watching, OnFloor(treeAt) + new Vector3(0.2f, 0.01f, 0), 0.3);
            Check(again && await Follows(navigation, host, room) && Reaches(navigation, pennedB, outsideB), "and burnt again, it opens again");
        }
        else Check(false, "the planted test room's mode allows Creative's undo");

        await TrunksWiderMap(world, room, host, navigation);

        // A reload keeps both: the island's save names the grown trees and the burnt, and the first bake already holds them.
        var followed = navigation.PlantsFollowed;
        var burntBefore = navigation.PlantsBurnt;
        world.QueueFree();
        await Frames(5);
        var reloaded = await BootPlanted(directory);
        if (reloaded == null) return;
        var again2 = reloaded.GetNode<RoomNavigation>("RoomNavigation");
        var host2 = CommandHost.Of(reloaded)!;
        Check(again2.PlantsFollowed == followed && again2.PlantsBurnt == burntBefore && again2.PlantChangesSeen == 1,
            $"after a reload the first bake already follows the same trees ({again2.PlantsFollowed} of {followed}, {again2.PlantsBurnt} of {burntBefore} burnt, read once; bake {again2.Revision})");
        Check(!Reaches(again2, pennedA, outsideA) && Reaches(again2, pennedB, outsideB) && host2.Nature.Plants.Count > 0,
            "and after a reload pen A is still closed by its grown trees and pen B still open where a tree burnt");
        reloaded.QueueFree();
        await Frames(3);
    }

    /// <summary>
    /// The same on an exported landscape (the 1x garage's 56 trees, or the 10x garage's 4,900): the trees nearest the player's spawn
    /// are grown and burnt, and the re-bakes measured (tiles baked of the map's, the bake's time on the workers and the main
    /// thread's, the worst tick, what reading nature cost). Run by hand with -- --landscape=DIR; the runners do not pass one.
    /// </summary>
    private async Task TrunksOnTheLandscape(string directory)
    {
        var world = await BootPlanted(directory, 180);
        if (world == null) return;
        var room = world.Room;
        var navigation = world.GetNode<RoomNavigation>("RoomNavigation");
        var host = CommandHost.Of(world)!;
        host.LearnFireForTests();
        var spawn = world.Player.GlobalPosition;
        var trees = new List<(PlantSet Set, int Row, float Away)>();
        foreach (var set in room.Copies!.Sets.Where(set => set.Kind == PlantKind.Tree))
            for (var row = 0; row < set.Count; row++)
            {
                var away = new Vector2(set.Position(row).X - spawn.X, set.Position(row).Z - spawn.Z).Length();
                if (away > 0.6f) trees.Add((set, row, away));
            }
        trees.Sort((a, b) => a.Away.CompareTo(b.Away));
        Check(trees.Count >= 2, $"the landscape has trees to grow and burn ({trees.Count} beyond 60 cm of the spawn, {navigation.TileCount} navigation tiles, {navigation.PolygonCount} polygons)");
        if (trees.Count < 2) { world.QueueFree(); return; }
        // Each tree: the Gubble stands on the side of it toward the spawn (on walkable ground), the cast's middle between the two, outside the trunk.
        (Vector3 Gubble, Vector3 Centre, Vector3 Axis, float Radius) Around((PlantSet Set, int Row, float Away) tree)
        {
            var root = tree.Set.Position(tree.Row);
            var shape = tree.Set.Tree(tree.Row);
            var toSpawn = new Vector3(spawn.X - root.X, 0, spawn.Z - root.Z).Normalized();
            var spot = navigation.ClosestPoint(root + toSpawn * (shape.TrunkRadiusM + 0.4f));
            return (spot, root + toSpawn * (shape.TrunkRadiusM + 0.12f) + Vector3.Up * 0.01f, root, shape.TrunkRadiusM);
        }
        var grow = Around(trees[0]);
        // The yardstick: the re-bake the navigation already does for any body that moved, marked round the same tree.
        var revision = navigation.Revision;
        navigation.WorstTickMs = 0;
        navigation.MarkDirty(new Aabb(grow.Axis - new Vector3(0.3f, 0.1f, 0.3f), new Vector3(0.6f, 1.0f, 0.6f)));
        await Rebaked(navigation, revision);
        await Frames(10);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED landscape, the yardstick, the tiles round a moved body: re-baked {navigation.LastTilesBaked} of {navigation.TileCount} tiles in {navigation.LastBakeMs:0.0} ms on worker threads, " +
            $"{navigation.LastBakeMainThreadMs:0.0} ms of the main thread in all, the worst tick {navigation.WorstTickMs:0.00} ms"));
        revision = navigation.Revision;
        navigation.WorstTickMs = 0;
        Check(world.Companion.TryTeleportTo(grow.Gubble), $"the Gubble beside the nearest tree ({grow.Gubble})");
        await Frames(3);
        var cast = Enumerable.Range(0, 2).Select(_ => Say(host, room, "nature.cast", CastArgs("grow", grow.Centre, 0.4, 1))).ToArray();
        var rebaked = await Rebaked(navigation, revision);
        await Frames(10);
        var id = PlantCopies.IdOf(trees[0].Set.Name, trees[0].Row);
        var factor = host.Nature.Plants.GetValueOrDefault(id)?.Factor ?? 1;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED landscape, {id} grown to {factor:0.00}: re-baked {navigation.LastTilesBaked} of {navigation.TileCount} tiles in {navigation.LastBakeMs:0.0} ms on worker threads, " +
            $"{navigation.LastBakeMainThreadMs:0.0} ms of the main thread in all, the worst tick {navigation.WorstTickMs:0.00} ms, nature read in {navigation.LastPlantScanMs:0.00} ms; {navigation.LastRoomObstructions} obstructions"));
        Check(cast.All(Ok) && rebaked && navigation.PlantsFollowed >= 1 && navigation.LastTilesBaked <= 4 && navigation.LastTilesBaked < navigation.TileCount + (navigation.TileCount == 1 ? 1 : 0),
            $"a cast on the landscape re-bakes only the tiles round the tree ({navigation.LastTilesBaked} of {navigation.TileCount}): " + string.Join(" / ", cast.Select(Why)));
        var nearest = navigation.ClosestPoint(grow.Axis + Vector3.Up * 0.02f);
        Check(new Vector2(nearest.X - grow.Axis.X, nearest.Z - grow.Axis.Z).Length() >= grow.Radius * factor - 0.005f,
            $"nothing inside the grown trunk is walkable ({new Vector2(nearest.X - grow.Axis.X, nearest.Z - grow.Axis.Z).Length() * 100:0.0} cm from its axis, the trunk {grow.Radius * factor * 100:0.0} cm)");

        var burn = Around(trees[1]);
        revision = navigation.Revision;
        navigation.WorstTickMs = 0;
        Check(world.Companion.TryTeleportTo(burn.Gubble), $"the Gubble beside the second tree ({burn.Gubble})");
        await Frames(3);
        var preview = Say(host, room, "nature.cast", CastArgs("fire", burn.Centre, 0.3, 30), preview: true);
        var lit = Say(host, room, "nature.cast", CastArgs("fire", burn.Centre, 0.3, 30, preview["data"]?["preview_id"]?.GetValue<string>()));
        for (var step = 0; step < 400 && host.Nature.Fire != null; step++) host.AdvanceNature(NatureLaws.StepS);
        var down = host.Nature.Plants.Values.Count(p => p.Burnt);
        var treesDown = host.Nature.Plants.Count(p => p.Value.Burnt && room.Copies!.TryResolve(p.Key, out var set, out _) && set.Kind == PlantKind.Tree);
        var followed = await Follows(navigation, host, room);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED landscape, a fire: {down} plants burnt down, {navigation.PlantsBurnt} trees among them no obstacle; {navigation.Revision - revision} bakes in all, the last {navigation.LastTilesBaked} of {navigation.TileCount} tiles in {navigation.LastBakeMs:0.0} ms, " +
            $"{navigation.LastBakeMainThreadMs:0.0} ms of the main thread, the worst tick {navigation.WorstTickMs:0.00} ms, nature read in {navigation.LastPlantScanMs:0.00} ms (worst {navigation.WorstPlantScanMs:0.00} ms)"));
        Check(Ok(lit) && treesDown >= 1 && followed && navigation.PlantsBurnt == treesDown,
            $"a fire on the landscape burns {treesDown} trees down (of {down} plants) and the navigation follows ({navigation.PlantsBurnt} burnt trees are no obstacle): " + Why(lit));
        world.QueueFree();
        await Frames(3);
    }

    /// <summary>
    /// The obstacles at a size (PlantObstacles): at the natural size exactly what IslandNavigation gives the first bake, so a tile
    /// that takes some trees from nature and the rest from the room's data is one picture; grown, a trunk's ring and a stone's hull
    /// are scaled about the copy's root, and its height with them.
    /// </summary>
    private void TestObstaclesAtSize()
    {
        var room = RoomData.Load(IslandFixture.ExampleDirectory);
        var natural = IslandNavigation.Obstacles(room, new Rect2(-16, -16, 32, 16));
        var same = natural.Count == 4;
        foreach (var obstacle in natural)
        {
            Check(room.Copies!.TryResolve(obstacle.EntityId, out var set, out var row), $"{obstacle.EntityId} is a copy of the island example");
            if (!room.Copies.TryResolve(obstacle.EntityId, out set, out row)) continue;
            var at = PlantObstacles.Of(set, row, obstacle.EntityId, 1f);
            same &= at.BottomM == obstacle.BottomM && at.HeightM == obstacle.HeightM && at.Outline.SequenceEqual(obstacle.Outline);
        }
        Check(same, "a copy's obstacle at its natural size is exactly the one the room's data gives (the two trunks and the two stones of the island example)");
        foreach (var obstacle in natural)
        {
            room.Copies!.TryResolve(obstacle.EntityId, out var set, out var row);
            var root = set.Position(row);
            var grown = PlantObstacles.Of(set, row, obstacle.EntityId, 1.5f);
            // About the root: every corner of the footprint is the natural one's, 1.5 times as far from it; the height grows with it.
            var scaled = obstacle.Outline.Select(p => new Vector3(root.X + (p.X - root.X) * 1.5f, p.Y, root.Z + (p.Z - root.Z) * 1.5f)).ToArray();
            var corners = scaled.All(c => grown.Outline.Any(g => Mathf.Abs(g.X - c.X) < 0.002f && Mathf.Abs(g.Z - c.Z) < 0.002f)) && grown.Outline.Length == scaled.Length;
            var tree = set.Kind == PlantKind.Tree;
            var bottom = tree ? obstacle.BottomM : root.Y + (obstacle.BottomM - root.Y) * 1.5f;
            Check(corners && Mathf.Abs(grown.HeightM - obstacle.HeightM * 1.5f) < 0.002f && Mathf.Abs(grown.BottomM - bottom) < 0.002f,
                $"{obstacle.EntityId} grown to 1.5: its footprint is 1.5 times as wide about its root, {grown.HeightM:0.000} m tall against {obstacle.HeightM:0.000} m ({grown.Outline.Length} corners)");
            var box = PlantObstacles.Bounds(set, row, 1.5f);
            Check(grown.Outline.All(c => c.X >= box.Position.X - 0.002f && c.X <= box.End.X + 0.002f && c.Z >= box.Position.Z - 0.002f && c.Z <= box.End.Z + 0.002f) &&
                  box.Position.Y <= grown.BottomM + 0.002f && box.End.Y >= grown.BottomM + grown.HeightM - 0.002f,
                $"{obstacle.EntityId}: the box that is baked again when it grows holds its grown footprint and its height");
        }
    }

    private static Vector2 TreeRoot(RoomData room, string id)
    {
        room.Copies!.TryResolve(id, out var set, out var row);
        var at = set.Position(row);
        return new Vector2(at.X, at.Z);
    }

    /// <summary>
    /// Only the tiles round a changed tree are baked again: the same room mapped over 40 m (five tiles of 8 m, a larger map joined on
    /// worker threads) has the lone tree grown, and bakes one tile in a moment, no physics tick holding 4 ms for it.
    /// </summary>
    private async Task TrunksWiderMap(RoomWorld world, RoomData room, CommandHost host, RoomNavigation own)
    {
        var bounds = new Aabb(room.Bounds.Position, new Vector3(40, room.Bounds.Size.Y, room.Bounds.Size.Z));
        var wide = RoomNavigation.Create(this, world.Built, bounds, WorldScaleProfile.Companion, world.Companion.StepHeightM, room: room, nature: host);
        for (var waited = System.Diagnostics.Stopwatch.StartNew(); !wide.IsReady && waited.Elapsed.TotalSeconds < 30;) await Frames(1);
        Check(wide.IsReady && wide.TileCount >= 5 && !wide.IsSmall && wide.PlantsFollowed == own.PlantsFollowed,
            $"the same room over five tiles bakes (a larger map), following the same {wide.PlantsFollowed} trees as the room's own navigation");
        await Frames(20);
        var revision = wide.Revision;
        wide.WorstTickMs = 0;
        var grown = await GrowAt(host, room, world, OnFloor(LoneTree) + new Vector3(0.4f, 0.01f, 0), LoneTree + new Vector2(0.2f, 0), 0.3);
        var rebaked = await Rebaked(wide, revision);
        await Frames(10);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION_MEASURED a tree grown on a map of {wide.TileCount} tiles: re-baked {wide.LastTilesBaked} tile(s) in {wide.LastBakeMs:0.0} ms on worker threads, {wide.LastBakeMainThreadMs:0.0} ms of the main thread in all, " +
            $"the worst tick {wide.WorstTickMs:0.00} ms, nature read in {wide.LastPlantScanMs:0.00} ms (worst {wide.WorstPlantScanMs:0.00} ms)"));
        Check(grown && rebaked && wide.LastTilesBaked == 1 && wide.LastTilesBaked < wide.TileCount,
            $"a tree grown re-bakes only the tile it stands in ({wide.LastTilesBaked} of {wide.TileCount} tiles), not the whole map");
        Check(wide.WorstTickMs < 4.0 && wide.LastPlantScanMs < 2.0, $"within the bake's budget: no physics tick holds 4 ms for it (the worst {wide.WorstTickMs:0.00} ms), nature is read in {wide.LastPlantScanMs:0.00} ms");
        wide.QueueFree();
        await Frames(2);
    }

    private void Finish()
    {
        GD.Print($"NATIVE_ROOM_NAVIGATION: {_checks - _failures}/{_checks} checks passed; companion routes round the big box in the test room through the command host; tiles join, re-bake off the main thread, stale routes refused");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>Planar distance from a point to a square footprint (zero inside it).</summary>
    private static float ClearanceFromSquare(Vector3 point, Vector3 centre, float half) =>
        new Vector2(Mathf.Max(Mathf.Abs(point.X - centre.X) - half, 0), Mathf.Max(Mathf.Abs(point.Z - centre.Z) - half, 0)).Length();

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Room navigation: " + label);
    }
}
