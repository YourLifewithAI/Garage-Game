using Godot;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// The land's collision where the bodies are (S12b, TerrainCollision), on a wide synthetic island (IslandFixture.WriteWideLand:
/// 224 x 64 m of rolling ground in 56 tiles, a sea round it, a jetty at the far east end): every tile collides at boot; once the
/// bodies have stood a while the far tiles go, and come back before anything touches them: a body running fast, a thing flung
/// across the land, a body dropped from the air onto ground that was gone, the Gubble catching up beside the player far away,
/// the player sent home to the jetty from the other end, a fire burning where nobody is, a node that asks to keep its ground,
/// and a swimmer wrapped round the open sea. No body is ever below the ground.
/// </summary>
public partial class IslandLoadingTest
{
    private RoomWorld _land = null!;

    private async Task CheckGroundStreaming()
    {
        // Groups 1 and 2 both used (the contract numbers them with none unused): the first row is a patch of its own.
        var grass = IslandFixture.GrassRows(2_000, new Rect2(-16, -32, 224, 64), seed: 9).Select((r, i) => i == 0 ? r with { Group = 1 } : r).ToList();
        var directory = IslandFixture.WriteWideLand("island_streaming", -1, 12, -2, 1, new[] { IslandFixture.Grass("grass", grass) });
        // A fresh island each run: the creation placed far away (PlacesFar) is not left in a save for the next.
        var saves = ProjectSettings.GlobalizePath($"{CommandHost.SaveRoot}/island_streaming");
        if (System.IO.Directory.Exists(saves)) System.IO.Directory.Delete(saves, recursive: true);
        _land = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        _land.RoomDirectory = directory;
        AddChild(_land);
        await UntilReady(_land);
        Check(_land.WorldReady, "the wide island boots: " + _land.LoadError);
        if (!_land.WorldReady) return;
        var ground = _land.Built.GetNode<TerrainCollision>(TerrainCollision.ViewName);
        var part = _land.Built.GetNode<StaticBody3D>("Shell/terrain_grass");
        var player = _land.Player;
        player.ReadKeyboard = false;
        var far = OnGround(190, -24);

        // Boot: the whole land collides, as before, so whatever the boot asks of it far away (a save's creations checked on
        // their surfaces, the ponds surveyed) finds it.
        var bootHit = Ray(far + Vector3.Up * 5, far + Vector3.Down * 5, RoomBuilder.WorldLayer);
        Check(ground.TileCount == 56 && ground.TilesBuilt == 56 && bootHit.Count > 0 && Mathf.Abs(bootHit["position"].AsVector3().Y - far.Y) < 0.05f,
            $"every tile of the land collides at boot ({ground.TilesBuilt} of {ground.TileCount}), the far end too");

        // Released once the bodies have stood a while; asked for, a tile is there at once, and hits name the part.
        await Seconds(TerrainCollision.BootHoldS + TerrainCollision.ReleaseAfterS + 1);
        var kept = ground.TilesBuilt;
        Check(kept < 30 && TerrainCollision.CollidesAt(_land, player.GlobalPosition) && !TerrainCollision.CollidesAt(_land, far) &&
              Ray(far + Vector3.Up * 5, far + Vector3.Down * 5, RoomBuilder.WorldLayer).Count == 0,
            $"after the boot the land collides only round the bodies and the places a body may be put ({kept} of {ground.TileCount} tiles; {ground.Releases} released)");
        Check(TerrainCollision.EnsureAround(_land, far, 1f) > 0, "a tile asked for is built");
        var asked = Ray(far + Vector3.Up * 5, far + Vector3.Down * 5, RoomBuilder.WorldLayer);
        var fromBelow = Ray(far + Vector3.Down * 2, far + Vector3.Up * 5, RoomBuilder.WorldLayer);
        Check(asked.Count > 0 && Mathf.Abs(asked["position"].AsVector3().Y - far.Y) < 0.05f && asked["collider"].AsGodotObject() == part &&
              part.GetMeta("entity_id").AsString() == "shell:terrain_grass" && fromBelow.Count == 0,
            "a tile built on request answers a ray at once (no physics step between), names its shell part, and is one-sided");
        var tiled = _land.Room.Terrain!.Part("shell:terrain_grass")!;
        Check(tiled.GroundTriangles(0) == 128 && ground.TrianglesBuilt == Enumerable.Range(0, tiled.Tiles.Count).Where(i => TerrainCollision.CollidesAt(_land, Centre(tiled, i))).Sum(i => (long)tiled.GroundTriangles(i)),
            $"each tile holds its level-0 ground (128 triangles), and the triangles built are the built tiles' ({ground.TrianglesBuilt})");

        await HoldsItsGround();
        var copies = _land.Built.GetNode<PlantCopiesView>(PlantCopiesView.ViewName);
        var (fills, empties) = (copies.VisualFills, copies.VisualEmpties);
        await RunsAcross();
        await Frames(30);
        CopiesFollow(copies, fills, empties);
        await FlingsAThing();
        await DropsFromTheAir();
        await CatchesUp();
        // Everyone far from the start now (the Gubble caught up): the copies there are let go.
        await Frames(60);
        Check(copies.VisualEmpties > empties && copies.BandOf("grass", 0, -1) == -1 && copies.InstancesHeld < 1500,
            $"the copies nobody is near are let go ({copies.VisualEmpties - empties} tiles emptied; the start's tile empty; {copies.InstancesHeld} of 2000 held)");
        await GoesHome();
        await KeepsAFire();
        await WrapsRound();
        await SeesOverFarHills();
        PlacesFar();
        Check((player.CollisionMask & RoomBuilder.FarGroundLayer) == 0 && (_land.Companion.CollisionMask & RoomBuilder.FarGroundLayer) == 0 && _onFarGround == 0,
            $"no body collides with the far ground: neither avatar's mask holds it, and no body rested on it in any tick of these runs ({_onFarGround} ticks)");
        Measure($"ISLAND_GROUND streaming: {ground.Builds} tiles built, {ground.Releases} released, {ground.SyncBuilds} built in the tick a body needed them (longest {ground.LongestSyncMs:0.0} ms), at most {ground.MostTilesBuilt} of {ground.TileCount} at once");
        _land.QueueFree();
        await Frames(2);
    }

    /// <summary>Ticks in which a body's collisions included the far ground (it must never touch it).</summary>
    private int _onFarGround;

    /// <summary>Whether this tick's slide collisions of an avatar include a far-ground body.</summary>
    private void WatchFarGround(SmallPlayerController body)
    {
        var ground = _land.Built.GetNode<TerrainCollision>(TerrainCollision.ViewName);
        for (var i = 0; i < body.GetSlideCollisionCount(); i++)
            if (ground.IsFarGround(body.GetSlideCollision(i).GetColliderRid())) { _onFarGround++; return; }
    }

    /// <summary>
    /// Sight and aim across the land (S12b, the far ground): the player stands 50 m short of a 2 m hill whose walkable ground is
    /// not built. A ray on the world layer alone passes through the hill (what sight would do without the far ground); the
    /// team's perception, the companion's view check and the HUD's aim all stop on it, while a box on this side is seen.
    /// </summary>
    private async Task SeesOverFarHills()
    {
        var player = _land.Player;
        var ground = _land.Built.GetNode<TerrainCollision>(TerrainCollision.ViewName);
        player.GlobalPosition = OnGround(70, 0) + Vector3.Up * 0.01f;
        player.Velocity = Vector3.Zero;
        player.Rotation = new Vector3(0, -Mathf.Pi / 2, 0);
        player.ResetPhysicsInterpolation();
        await Seconds(TerrainCollision.ReleaseAfterS + 1);
        var hill = OnGround((float)IslandFixture.HillX, 0);
        var eye = player.GlobalPosition + Vector3.Up * 0.08f;
        var behind = OnGround(150, 0) + Vector3.Up * 0.05f;
        var before = OnGround(85, 0) + Vector3.Up * 0.05f;
        var worldOnly = Ray(eye, behind, RoomBuilder.WorldLayer, player.GetRid());
        var sight = Ray(eye, behind, RoomBuilder.SightMask, player.GetRid());
        var part = _land.Built.GetNode<StaticBody3D>("Shell/terrain_grass");
        var hitX = sight.Count > 0 ? sight["position"].AsVector3().X : float.NaN;
        Check(!TerrainCollision.CollidesAt(_land, hill) && ground.FarTilesShown > 0 && worldOnly.Count == 0 && sight.Count > 0 && hitX > 96 && hitX < (float)IslandFixture.HillX &&
              sight["collider"].AsGodotObject() == part,
            $"the hill's walkable ground is not built and the world layer alone sees through it; sight meets the far ground on its near slope (at x {hitX:0.0} m), naming the land ({ground.FarTilesShown} tiles of far ground shown)");
        static JsonObject Box(string id, Vector3 at) => new()
        {
            ["id"] = id, ["kind"] = "object",
            ["bounds_m"] = new JsonObject { ["min_m"] = new JsonArray(at.X - 0.05, at.Y - 0.05, at.Z - 0.05), ["max_m"] = new JsonArray(at.X + 0.05, at.Y + 0.05, at.Z + 0.05) },
        };
        var host = _land.GetNode<CommandHost>("CommandHost");
        var seen = host.Perceive(player, "avatar:player", new System.Collections.Generic.List<JsonObject> { Box("obj:behind_hill", behind), Box("obj:this_side", before) });
        Check(!seen.Contains("obj:behind_hill") && seen.Contains("obj:this_side"),
            $"the team's perception does not see a box behind the far hill and sees one on this side ({string.Join(", ", seen)})");
        Measure(FormattableString.Invariant($"ISLAND_FAR sight from (70, 0) toward (150, 0): the world layer alone {(worldOnly.Count == 0 ? "sees through the hill" : "is stopped")}, sight stops at x {hitX:0.0} m on the far ground; perceived: {string.Join(", ", seen)}"));
        var hud = _land.GetChildren().OfType<RoomHud>().First();
        hud.AimRayForTests = (eye, eye + (behind - eye).Normalized() * 150f);
        var aim = hud.AimNow();
        hud.AimRayForTests = null;
        Check(aim.Hit && aim.Point.X > 96 && aim.Point.X < (float)IslandFixture.HillX && Mathf.Abs(aim.Point.Y - (float)IslandFixture.Height(aim.Point.X, aim.Point.Z)) < 0.1f,
            $"the HUD's aim across the land stops on the far hill ({aim.Point}), not 150 m past it");
    }

    /// <summary>
    /// A creation placed far from both bodies (S12b): the kernel builds the ground there before the authority checks the footing,
    /// so the placement is accepted on the real (walkable) ground, at its height, not refused for a missing surface.
    /// </summary>
    private void PlacesFar()
    {
        var host = _land.GetNode<CommandHost>("CommandHost");
        var spot = OnGround(170, -20);
        Check(!TerrainCollision.CollidesAt(_land, spot), "the far spot's walkable ground is not built before the placement");
        JsonObject Place(bool preview)
        {
            var command = new JsonObject
            {
                ["schema"] = "enfractal.command", ["version"] = 1, ["action_id"] = "far-place-0001", ["room_id"] = _land.Room.RoomId, ["op"] = "creation.place",
                ["args"] = new JsonObject
                {
                    ["source"] = new JsonObject
                    {
                        ["schema"] = "enfractal.creation", ["version"] = 1, ["name"] = "Far marker", ["seed"] = 1, ["mount"] = "ground",
                        ["parts"] = new JsonArray(new JsonObject { ["id"] = "base", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.06, 0), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = "wood" }),
                        ["nodes"] = new JsonArray(
                            new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "base", ["params"] = new JsonObject() },
                            new JsonObject { ["id"] = "glow", ["op"] = "light", ["part_id"] = "base", ["params"] = new JsonObject { ["intensity"] = 1, ["duration_s"] = 1 } }),
                        ["edges"] = new JsonArray(new JsonObject { ["from"] = "use", ["to"] = "glow" }),
                    },
                    ["placement"] = new JsonObject { ["position_m"] = new JsonArray(spot.X, spot.Y, spot.Z) },
                },
            };
            if (preview) command["preview"] = true;
            return JsonNode.Parse(host.Handle(command.ToJsonString(), CommandHost.PlayerPrincipal))!.AsObject();
        }
        var previewed = Place(preview: true);
        var y = previewed["data"]?["position_m"]?[1]?.GetValue<double>() ?? double.NaN;
        // The authority stands a creation on the highest of its footprint's corners and middle (a 12 cm box): the walkable ground there.
        var footprint = new[] { Vector3.Zero, new Vector3(0.06f, 0, 0.06f), new Vector3(-0.06f, 0, 0.06f), new Vector3(0.06f, 0, -0.06f), new Vector3(-0.06f, 0, -0.06f) };
        var heights = footprint.Select(o => GroundUnder(spot + o)).ToArray();
        float? groundY = heights.All(h => h != null) ? heights.Max() : null;
        var placed = Place(preview: false);
        Measure(FormattableString.Invariant($"ISLAND_FAR a creation placed at (170, -20), 80 m from both bodies: {(placed["ok"]!.GetValue<bool>() ? "accepted" : "refused")} at {y:0.0000} m over walkable ground at {groundY:0.0000} m"));
        Check(previewed["ok"]!.GetValue<bool>() && placed["ok"]!.GetValue<bool>() && groundY is { } g && Math.Abs(y - g) < 0.0005,
            $"a creation placed 80 m from both bodies is accepted on the real ground (at {y:0.000} m, the walkable ground {groundY:0.000} m): {placed["error"]?["message"]}");
    }
    private static Vector3 OnGround(float x, float z) => new(x, (float)IslandFixture.Height(x, z), z);

    private static Vector3 Centre(TiledPart part, int tile) => new(part.Tiles[tile].Tx * 16 + 8, 0, part.Tiles[tile].Tz * 16 + 8);

    private async Task Seconds(double seconds) => await Frames((int)Math.Ceiling(seconds * Engine.PhysicsTicksPerSecond));

    /// <summary>The ground straight under a point (from 2 m over it), or null where nothing collides.</summary>
    private float? GroundUnder(Vector3 at)
    {
        var hit = Ray(at + Vector3.Up * 2, at + Vector3.Down * 3, RoomBuilder.WorldLayer, _land.Player.GetRid());
        return hit.Count > 0 ? hit["position"].AsVector3().Y : null;
    }

    /// <summary>A body's feet against the ground under them this tick: how far below it they are (0 when on or over it), and whether ground was there at all.</summary>
    private (float Below, bool Ground) Footing(Node3D body, float foot = 0f)
    {
        var at = body.GlobalPosition + Vector3.Down * foot;
        var y = GroundUnder(at);
        return y is { } g ? (Math.Max(0f, g - at.Y), true) : (0f, false);
    }

    /// <summary>A node in TerrainCollision.AnchorGroup keeps the ground under it, far from every body, and lets it go when it leaves the group.</summary>
    private async Task HoldsItsGround()
    {
        var at = OnGround(120, 20);
        var anchor = new Node3D { Name = "Anchor", Position = at };
        anchor.AddToGroup(TerrainCollision.AnchorGroup);
        _land.AddChild(anchor);
        await Seconds(0.2);
        var built = TerrainCollision.CollidesAt(_land, at);
        await Seconds(TerrainCollision.ReleaseAfterS + 1);
        var kept = TerrainCollision.CollidesAt(_land, at);
        anchor.QueueFree();
        await Seconds(TerrainCollision.ReleaseAfterS + 1);
        Check(built && kept && !TerrainCollision.CollidesAt(_land, at), "a node that asks to keep its ground (TerrainCollision.AnchorGroup) has it while it asks, far from the bodies");
    }

    /// <summary>The player runs east at 6 m/s for 8 s: the ground ahead is there before the feet, and they never go below it.</summary>
    private async Task RunsAcross()
    {
        var player = _land.Player;
        var ground = _land.Built.GetNode<TerrainCollision>(TerrainCollision.ViewName);
        var speed = player.RunSpeedMps;
        player.RunSpeedMps = 6f;
        player.SetMovementFrame(null);
        player.Rotation = new Vector3(0, -Mathf.Pi / 2, 0);
        var start = player.GlobalPosition;
        var syncBefore = ground.SyncBuilds;
        float worst = 0;
        var missing = 0;
        player.SetControlInput(new Vector2(0, 1), sprint: true);
        for (var tick = 0; tick < 8 * Engine.PhysicsTicksPerSecond; tick++)
        {
            await Frames(1);
            WatchFarGround(player);
            var (below, there) = Footing(player);
            worst = Math.Max(worst, below);
            if (!there || !TerrainCollision.CollidesAt(_land, player.GlobalPosition)) missing++;
        }
        player.SetControlInput(Vector2.Zero);
        player.RunSpeedMps = speed;
        await Frames(10);
        var ran = new Vector2(player.GlobalPosition.X - start.X, player.GlobalPosition.Z - start.Z).Length();
        Measure($"ISLAND_GROUND running: {ran:0.0} m in 8 s, feet at most {worst * 1000:0.0} mm under the ground, {missing} ticks without ground under them, {ground.SyncBuilds - syncBefore} tiles built in the tick they were needed");
        Check(ran > 30 && worst < 0.005f && missing == 0 && ground.SyncBuilds == syncBefore,
            $"running at 6 m/s the ground ahead is built before the feet reach it ({ran:0} m; {missing} ticks without ground, {ground.SyncBuilds - syncBefore} built at the last moment)");
        // Round a body the land answers queries as before: nature's active cells (12 m), the camera's arm, sight close by.
        var here = player.GlobalPosition;
        var round = new[] { new Vector3(12, 0, 0), new Vector3(-12, 0, 0), new Vector3(0, 0, 12), new Vector3(0, 0, -12), new Vector3(9, 0, 9), new Vector3(-9, 0, -9) }
            .Select(o => OnGround(here.X + o.X, Mathf.Clamp(here.Z + o.Z, -31.5f, 31.5f))).ToArray();
        Check(round.All(p => GroundUnder(p) is { } y && Mathf.Abs(y - p.Y) < 0.05f),
            "within 12 m of the running player every ray down finds the ground (nature's active cells, the camera's arm and sight nearby are answered as before)");
    }

    /// <summary>
    /// The copies follow the player as the ground does (S12b): its own tile in full detail, one 30-odd metres off in the lightest
    /// mesh, one 100 m ahead not held at all; tiles were filled again and emptied as it ran.
    /// </summary>
    private void CopiesFollow(PlantCopiesView copies, int fills, int empties)
    {
        var at = _land.Player.GlobalPosition;
        var (tx, tz) = PlantCopies.TileOf(at.X, at.Z);
        var set = _land.Room.Copies!.Find("grass")!;
        var bands = DrawDistances.CopyBands(set.Kind, 0.13f, set.MeshPaths.Count);
        int Band(float x, float z) { var (a, b) = PlantCopies.TileOf(x, z); return set.FindTile(a, b) >= 0 ? copies.BandOf("grass", a, b) : -2; }
        var near = Band(at.X, at.Z);
        var mid = Band(at.X + 36, at.Z);
        var ahead = Band(at.X + 100, at.Z);
        Check(near == 0 && mid == bands.Length - 1 && ahead == -1 && copies.InstancesHeld < _land.Room.Copies.TotalCount && copies.VisualFills > fills,
            $"the copies are held round the player as it runs: its tile ({tx}, {tz}) in band {near}, 36 m ahead in band {mid}, 100 m ahead not held ({ahead}); {copies.InstancesHeld} of {_land.Room.Copies.TotalCount} held; {copies.VisualFills - fills} tiles filled on the way");
    }

    /// <summary>A ball flung east at 14 m/s across ground that had gone: its ground is built ahead of it, and it never drops through.</summary>
    private async Task FlingsAThing()
    {
        var at = OnGround(100, 20);
        Check(!TerrainCollision.CollidesAt(_land, at) && !TerrainCollision.CollidesAt(_land, OnGround(130, 20)), "the ball's way lies over ground that is not built");
        var ball = new RigidBody3D
        {
            Name = "Ball", Mass = 1f, ContinuousCd = true, CollisionLayer = 0, CollisionMask = RoomBuilder.WorldLayer, Position = at + Vector3.Up * 0.06f,
            PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0f, Bounce = 0f }, LinearVelocity = new Vector3(14, 0, 0),
        };
        ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.05f } });
        _land.AddChild(ball);
        float worst = 0;
        var missing = 0;
        var lowest = float.MaxValue;
        var worstAt = "";
        for (var tick = 0; tick < 4 * Engine.PhysicsTicksPerSecond; tick++)
        {
            await Frames(1);
            var (below, _) = Footing(ball, 0.05f);
            if (below > worst) worstAt = $"{ball.GlobalPosition} tick {tick}, its tile {(TerrainCollision.CollidesAt(_land, ball.GlobalPosition) ? "built" : "missing")}, moving {ball.LinearVelocity}";
            worst = Math.Max(worst, below);
            if (!TerrainCollision.CollidesAt(_land, ball.GlobalPosition)) missing++;
            lowest = Math.Min(lowest, ball.GlobalPosition.Y);
        }
        var travelled = ball.GlobalPosition.X - at.X;
        Measure($"ISLAND_GROUND a ball flung at 14 m/s: {travelled:0.0} m in 4 s, its bottom at most {worst * 1000:0.0} mm under the ground ({worstAt}), lowest at {lowest:0.000} m");
        // Jolt lets a fast contact sink by up to its penetration slop (2 cm) before it pushes back; through the ground is far more.
        Check(travelled > 25 && missing == 0 && worst < 0.025f && lowest > 0.05f, $"a thing flung across ground that had gone is carried on it ({travelled:0} m, {missing} ticks over missing ground, at most {worst * 1000:0} mm in), never through it");
        ball.QueueFree();
    }

    /// <summary>The player put 3 m over ground that had gone, far from everything: the ground is built in the tick it arrives, and it lands on it.</summary>
    private async Task DropsFromTheAir()
    {
        var player = _land.Player;
        var ground = _land.Built.GetNode<TerrainCollision>(TerrainCollision.ViewName);
        var spot = OnGround(150, -24);
        Check(!TerrainCollision.CollidesAt(_land, spot), "the ground under the drop is not built");
        var syncBefore = ground.SyncBuilds;
        player.GlobalPosition = spot + Vector3.Up * 3;
        player.Velocity = Vector3.Zero;
        player.ResetPhysicsInterpolation();
        float worst = 0;
        for (var tick = 0; tick < 3 * Engine.PhysicsTicksPerSecond && !(player.IsOnFloor() && tick > 10); tick++)
        {
            await Frames(1);
            WatchFarGround(player);
            worst = Math.Max(worst, Footing(player).Below);
        }
        var groundY = GroundUnder(player.GlobalPosition);
        Check(player.IsOnFloor() && groundY is { } y && Mathf.Abs(player.GlobalPosition.Y - y) < 0.02f && worst < 0.005f && ground.SyncBuilds > syncBefore,
            $"dropped onto ground that had gone, the player lands on it (at {player.GlobalPosition.Y:0.000} m over ground at {groundY:0.000}; built in the tick it arrived: {ground.SyncBuilds - syncBefore} tiles)");
    }

    /// <summary>The Gubble, left more than CatchUpM behind, catches up beside the player far away, onto ground that is there.</summary>
    private async Task CatchesUp()
    {
        var player = _land.Player;
        var companion = _land.Companion;
        companion.Follow();
        var before = companion.CatchUps;
        for (var waited = 0; waited < 8 * Engine.PhysicsTicksPerSecond && companion.CatchUps == before; waited++) await Frames(1);
        await Seconds(0.5);
        var gap = companion.GlobalPosition.DistanceTo(player.GlobalPosition);
        var (below, there) = Footing(companion);
        Check(companion.CatchUps > before && gap < 5 && there && below < 0.005f && TerrainCollision.CollidesAt(_land, companion.GlobalPosition),
            $"the Gubble catches up beside the player far away ({gap:0.00} m off) onto ground that is built, never under it");
    }

    /// <summary>B: the player goes home to the jetty at the far east end, and stands on its root.</summary>
    private async Task GoesHome()
    {
        var player = _land.Player;
        var jetty = _land.Room.Sea!.JettyData!.Value;
        player.GlobalPosition = OnGround(-10, 20) + Vector3.Up * 0.01f;
        player.Velocity = Vector3.Zero;
        player.ResetPhysicsInterpolation();
        await Seconds(TerrainCollision.ReleaseAfterS + 1);
        var pinned = TerrainCollision.CollidesAt(_land, jetty.RootM);
        var trips = player.HomeTrips;
        Check(player.RequestHome(), "the player asks to go home");
        for (var waited = 0; waited < 5 * Engine.PhysicsTicksPerSecond && player.HomeTrips == trips; waited++) await Frames(1);
        await Seconds(0.5);
        var (below, there) = Footing(player);
        Check(pinned && player.HomeTrips > trips && player.LastHome == "jetty" && player.GlobalPosition.DistanceTo(jetty.RootM) < 0.5f && player.IsOnFloor() && there && below < 0.005f,
            $"home from the far west, the player stands on the jetty's root 200 m away ({player.LastHome}, {player.GlobalPosition.DistanceTo(jetty.RootM):0.00} m from it), whose ground was kept all along");
    }

    /// <summary>A fire burning where nobody is keeps its ground, so nature's spread never runs out of land; it goes once the fire is out.</summary>
    private async Task KeepsAFire()
    {
        var host = _land.GetNode<CommandHost>("CommandHost");
        var at = OnGround(60, -24);
        Check(!TerrainCollision.CollidesAt(_land, at), "the fire's ground is not built before it burns");
        // A fire with no burning cells goes out at the next nature step; here it is kept lit every tick, as a fire that burns on.
        var fire = new NatureFire { Center = at, RadiusM = 1f, RemainingS = 600f };
        async Task Burn(double seconds)
        {
            for (var tick = 0; tick < seconds * Engine.PhysicsTicksPerSecond; tick++)
            {
                host.Nature.Fire = fire;
                await Frames(1);
            }
        }
        await Burn(1.5);
        var lit = TerrainCollision.CollidesAt(_land, at);
        await Burn(TerrainCollision.ReleaseAfterS + 1);
        var kept = TerrainCollision.CollidesAt(_land, at) && TerrainCollision.CollidesAt(_land, at + new Vector3(1, 0, 0));
        var burning = host.Nature.Fire != null;
        host.Nature.Fire = null;
        await Seconds(TerrainCollision.ReleaseAfterS + 2.5);        Check(lit && kept && !TerrainCollision.CollidesAt(_land, at), $"a fire burning far from everyone keeps the ground under it while it burns, and lets it go when it is out (lit {lit}, kept {kept}, burning then {burning})");
    }

    /// <summary>A swimmer out past the open sea's wrap comes back in on the far side: nothing it arrives on is missing, and it never sinks under the land.</summary>
    private async Task WrapsRound()
    {
        var player = _land.Player;
        var sea = _land.Room.Sea!;
        if (!float.IsFinite(player.WrapM)) { Check(false, "the wide island has a wrap"); return; }
        var outward = new Vector3(0, 0, 1);
        var at = new Vector3(sea.OpenSeaCentreM.X, sea.LevelM - 0.03f, sea.OpenSeaCentreM.Y) + outward * (player.WrapM + 0.5f);
        var wraps = player.Wraps;
        player.GlobalPosition = at;
        player.Velocity = outward * 0.3f;
        player.ResetPhysicsInterpolation();
        float worst = 0;
        for (var tick = 0; tick < Engine.PhysicsTicksPerSecond; tick++)
        {
            await Frames(1);
            worst = Math.Max(worst, Footing(player).Below);
        }
        var landed = player.GlobalPosition;
        Check(player.Wraps > wraps && landed.Z < sea.OpenSeaCentreM.Y && worst < 0.005f && TerrainCollision.CollidesAt(_land, landed),
            $"a swimmer wrapped round the open sea comes in on the far side ({landed.X:0} m, {landed.Z:0} m), nothing it reaches missing and never under the land");
    }
}
