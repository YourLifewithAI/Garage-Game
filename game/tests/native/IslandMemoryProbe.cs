using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Navigation;
using GarageGame.Native.Room;
using GarageGame.Tests.Look;

namespace GarageGame.Tests;

/// <summary>
/// Where an island's memory goes in the full game (S12b, the 10x world's memory budget: 0.9 GB steady, 1.2 GB peak), headless or in
/// a window: boots a room as the game does ("-- --room=DIR"), waits until its copies are built and its navigation is ready, then
/// stands the avatars at each of the look suite's island cameras (spawn, summit, hamlet, beach: IslandCameras, from the island's
/// data) and, once the copies and the ground's collision have caught up there, reads the process's memory with garbage collected
/// first. It prints ISLAND_MEMORY lines (one JSON object each) and checks nothing.
/// </summary>
public partial class IslandMemoryProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            var room = args.FirstOrDefault(a => a.StartsWith("--room=", StringComparison.Ordinal))?["--room=".Length..] ?? throw new ArgumentException("pass -- --room=DIR");
            var settle = int.Parse(args.FirstOrDefault(a => a.StartsWith("--settle=", StringComparison.Ordinal))?["--settle=".Length..] ?? "240", CultureInfo.InvariantCulture);
            var clock = Stopwatch.StartNew();
            await Record("engine", settle: 10);
            if (args.Contains("--room-data"))
            {
                // The room's data alone, read as the game reads it, then let go.
                var data = await RoomData.LoadAsync(RoomWorld.ResolveRoom(room.Replace('\\', '/')));
                await Record("the room's data alone", settle: 10);
                GC.KeepAlive(data);
                data = null;
                await Record("the room's data let go", settle: 10);
            }
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            world.RoomDirectory = RoomWorld.ResolveRoom(room.Replace('\\', '/'));
            clock.Restart();
            AddChild(world);
            for (var waited = Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 120 && !world.WorldReady && world.LoadError.Length == 0;) await Frame();
            if (!world.WorldReady) throw new InvalidOperationException(world.LoadError);
            var readyMs = clock.Elapsed.TotalMilliseconds;
            var view = world.Built.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
            var navigation = world.GetNodeOrNull<RoomNavigation>("RoomNavigation");
            for (var waited = Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 120 && (view is { Complete: false } || navigation is { IsReady: false });) await Frame();
            var builtMs = clock.Elapsed.TotalMilliseconds;
            var peakAtLoad = PeakMib();
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"ISLAND_MEMORY_LOAD ready {readyMs:0} ms, copies and navigation {builtMs:0} ms, peak working set {peakAtLoad:0} MiB"));
            // The cameras are worked out from rays over the whole land: every tile of it built for a while.
            TerrainCollision.EnsureAll(world, holdS: 0.5);
            var space = world.GetWorld3D().DirectSpaceState;
            var notes = new List<string>();
            var cameras = IslandCameras.Derive(world.Room, IslandCameras.RayGround(space, world.Room.Bounds), IslandCameras.RaySky(space), notes)
                .Where(c => c.Flight == null).ToList();
            await Record("ready", world, settle: 60);
            if (args.Contains("--navigation") && navigation != null) await ProbeNavigation(world, navigation, cameras);
            if (args.Contains("--compact"))
            {
                var pause = Stopwatch.StartNew();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GD.Print(FormattableString.Invariant($"ISLAND_MEMORY_COMPACT an aggressive compacting collection took {pause.Elapsed.TotalMilliseconds:0.0} ms"));
                await Record("compacted", world, settle: 10);
            }
            foreach (var camera in cameras)
            {
                TerrainCollision.EnsureAround(world, camera.PlayerM, holdS: 0.5);
                var moved = Stage(world.Player, camera.PlayerM);
                GD.Print(FormattableString.Invariant($"ISLAND_MEMORY_STAGE {camera.Id}: the player at {camera.PlayerM} {(moved is { } at ? $"stands at {at}" : "could not stand there or within 4 m, so is put there")}"));
                world.Player.Rotation = new Vector3(0, Mathf.DegToRad(camera.YawDeg), 0);
                world.Player.ResetPhysicsInterpolation();
                world.Companion.Stay();
                Stage(world.Companion, camera.CompanionM);
                world.Companion.ResetPhysicsInterpolation();
                await Record(camera.Id, world, settle);
            }
            if (args.Contains("--managed"))
            {
                // C#'s live heap by owner: every object reachable through instance fields from each root, counted once (first root wins).
                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var roots = new List<(string, object)> { ("RoomData", world.Room) };
                foreach (var child in world.GetChildren()) roots.Add((child.Name + " (" + child.GetType().Name + ")", child));
                foreach (var child in world.Built.GetChildren()) roots.Add(("Built/" + child.Name + " (" + child.GetType().Name + ")", child));
                foreach (var (name, root) in roots)
                {
                    var (bytes, objects, largest) = Reach(root, seen);
                    if (bytes > 512 * 1024)
                        GD.Print(FormattableString.Invariant($"ISLAND_MEMORY_MANAGED {name}: {bytes / 1048576.0:0.0} MiB in {objects} objects; largest: {largest}"));
                }
            }
            if (args.Contains("--breakdown"))
            {
                // What each part of the game holds: taken away one at a time, garbage collected after each.
                foreach (var name in new[] { "CompanionBridge", "RoomHud", "NatureLook", "PondLife", "RoomNavigation", "CommandHost" })
                    if (world.GetChildren().FirstOrDefault(c => c.Name.ToString().Contains(name, StringComparison.Ordinal) || c.GetType().Name == name) is { } node)
                    {
                        node.QueueFree();
                        await Record("without " + node.Name, world, settle: 10);
                    }
                // The parts of the room that might be held more cheaply, one at a time: the far ground, the water's colliders, the copies' collision.
                if (world.Built.GetNodeOrNull<TerrainCollision>(TerrainCollision.ViewName) is { } land)
                {
                    land.FreeFarGround();
                    await Record("without the far ground", world, settle: 10);
                }
                foreach (var water in world.Built.FindChildren("*", "Area3D", true, false).OfType<Area3D>().Where(a => a.GetParent()?.HasMeta("entity_id") == true).ToArray())
                    water.QueueFree();
                await Record("without the water's colliders", world, settle: 10);
                if (view != null)
                {
                    view.FreeCollision();
                    await Record("without the copies' collision", world, settle: 10);
                }
                foreach (var child in world.Built.GetChildren().ToArray())
                {
                    child.QueueFree();
                    await Record("without the room's " + child.Name, world, settle: 10);
                }
                world.Look.QueueFree();
                await Record("without the look", world, settle: 10);
            }
            GD.Print("ISLAND_MEMORY_DONE");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError("Island memory probe failed: " + exception);
            GetTree().Quit(1);
        }
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    /// <summary>
    /// Navigation's reach and its re-bakes (S12b, the room-ground change request): routes from the spawn to the island's cameras
    /// and a few far corners, the polygons; then three small boxes marked dirty one at a time (as a creation placed there would),
    /// each re-bake's main-thread time, and the routes again.
    /// </summary>
    private async Task ProbeNavigation(RoomWorld world, RoomNavigation navigation, List<DerivedCamera> cameras)
    {
        var spawn = world.Room.SpawnFor("player").PositionM;
        var targets = cameras.Select(c => (c.Id, c.PlayerM)).ToList();
        foreach (var (x, z) in new[] { (-25f, 25f), (25f, 25f), (0f, 30f), (-28f, -20f), (10f, -20f), (-10f, -15f), (20f, -10f), (0f, 0f), (-15f, 5f) })
            targets.Add(($"({x:0}, {z:0})", navigation.ClosestPoint(new Vector3(x, 1f, z))));
        string Routes() => string.Join("; ", targets.Select(t =>
        {
            var route = navigation.FindRoute(spawn, t.Item2, 0.1f);
            return FormattableString.Invariant($"{t.Item1} {(route.Reaches ? "reaches" : "falls short")} {route.LengthM:0.0} m");
        }));
        GD.Print(FormattableString.Invariant($"ISLAND_NAVIGATION_PROBE first bake: {navigation.PolygonCount} polygons, main thread {navigation.LastBakeMainThreadMs:0.0} ms; routes from the spawn: {Routes()}"));
        var boxes = new[] { cameras.First(c => c.Id == "hamlet").PlayerM, new Vector3(10, 0, 0), new Vector3(-20, 0, 10) };
        foreach (var at in boxes)
        {
            var revision = navigation.Revision;
            var ground = navigation.ClosestPoint(new Vector3(at.X, 30f, at.Z));
            navigation.MarkDirty(new Aabb(ground - new Vector3(0.5f, 0.5f, 0.5f), Vector3.One));
            for (var waited = Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 30 && navigation.Revision == revision;) await Frame();
            GD.Print(FormattableString.Invariant($"ISLAND_NAVIGATION_PROBE re-bake round ({ground.X:0.0}, {ground.Z:0.0}): {navigation.LastTilesBaked} tiles, {navigation.LastBakeMs:0} ms on workers, main thread {navigation.LastBakeMainThreadMs:0.00} ms, worst tick {navigation.WorstTickMs:0.00} ms, {navigation.PolygonCount} polygons, {navigation.RoomGroundTriangles} ground triangles read"));
        }
        GD.Print(FormattableString.Invariant($"ISLAND_NAVIGATION_PROBE after the re-bakes: {navigation.PolygonCount} polygons; routes from the spawn: {Routes()}"));
    }

    /// <summary>
    /// Bytes (roughly: arrays by their elements, other objects by their fields), objects, and the three largest arrays reachable
    /// from root through instance fields (static fields and native memory are not followed), skipping anything already seen.
    /// </summary>
    private static (long Bytes, int Objects, string Largest) Reach(object root, HashSet<object> seen)
    {
        long bytes = 0;
        var objects = 0;
        var largest = new List<(long Bytes, string What)>();
        var stack = new Stack<(object Value, string Path)>();
        stack.Push((root, root.GetType().Name));
        while (stack.Count > 0)
        {
            var (value, path) = stack.Pop();
            if (!seen.Add(value)) continue;
            objects++;
            var type = value.GetType();
            if (value is Array array)
            {
                var element = type.GetElementType()!;
                long size = element.IsValueType ? (long)array.Length * (element.IsPrimitive ? System.Runtime.InteropServices.Marshal.SizeOf(element) : SizeOfStruct(element)) : array.Length * 8L;
                bytes += size + 24;
                largest.Add((size, $"{path} {type.Name}[{array.Length}]"));
                largest.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
                if (largest.Count > 3) largest.RemoveAt(3);
                if (!element.IsValueType || !element.IsPrimitive)
                    foreach (var item in array)
                        if (item != null && !item.GetType().IsPrimitive && item is not string && !item.GetType().IsEnum) stack.Push((item, path + "[]"));
                continue;
            }
            if (value is string text) { bytes += 22 + text.Length * 2; continue; }
            bytes += 16;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    bytes += field.FieldType.IsValueType ? 8 : 8;
                    if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType.IsPointer) continue;
                    object? next;
                    try { next = field.GetValue(value); } catch { continue; }
                    if (next == null || next is System.Reflection.Pointer || next.GetType().IsPrimitive) continue;
                    if (next is Delegate || next is System.Threading.Tasks.Task || next is Type || next is System.Reflection.MemberInfo) continue;
                    stack.Push((next, path.Length > 120 ? path : path + "." + field.Name));
                }
        }
        return (bytes, objects, string.Join("; ", largest.Select(l => FormattableString.Invariant($"{l.What} {l.Bytes / 1048576.0:0.0} MiB"))));
    }

    private static int SizeOfStruct(Type type)
    {
        try { return System.Runtime.InteropServices.Marshal.SizeOf(type); }
        catch { return 16; }
    }

    /// <summary>A body at a camera's spot, or the nearest spot within 4 m it can stand on (a slope too steep at the spot itself).</summary>
    private static Vector3? Stage(SmallPlayerController body, Vector3 at)
    {
        foreach (var ring in new[] { 0f, 0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f, 3f, 4f })
            for (var k = 0; k < (ring == 0 ? 1 : 12); k++)
            {
                var spot = at + new Vector3(Mathf.Cos(k * Mathf.Tau / 12), 0, Mathf.Sin(k * Mathf.Tau / 12)) * ring;
                if (body.TryTeleportTo(spot)) return body.GlobalPosition;
            }
        // Nowhere to stand (a peak too steep): put there all the same, so the view and the copies are the camera's.
        body.GlobalPosition = at + Vector3.Up * 0.05f;
        body.Velocity = Vector3.Zero;
        return null;
    }

    private static double PeakMib()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PeakWorkingSet64 / 1048576.0;
    }

    private async Task Record(string step, RoomWorld? world = null, int settle = 240)
    {
        for (var i = 0; i < settle; i++) await Frame();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        for (var i = 0; i < 3; i++) await Frame();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        static string R(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
        var fields = new List<string>
        {
            $"\"step\":\"{step}\"",
            $"\"working_set_mib\":{R(process.WorkingSet64 / 1048576.0)}",
            $"\"private_mib\":{R(process.PrivateMemorySize64 / 1048576.0)}",
            $"\"peak_working_set_mib\":{R(process.PeakWorkingSet64 / 1048576.0)}",
            $"\"engine_static_mib\":{R(OS.GetStaticMemoryUsage() / 1048576.0)}",
            $"\"managed_heap_mib\":{R(gc.HeapSizeBytes / 1048576.0)}",
            $"\"managed_committed_mib\":{R(gc.TotalCommittedBytes / 1048576.0)}",
            $"\"managed_live_mib\":{R(GC.GetTotalMemory(false) / 1048576.0)}",
            $"\"video_mib\":{R(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0)}",
            $"\"video_buffer_mib\":{R(Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0)}",
        };
        if (world?.Built is { } built)
        {
            var multimeshes = built.FindChildren("*", "MultiMeshInstance3D", true, false).OfType<MultiMeshInstance3D>().ToArray();
            var faces = built.FindChildren("*", "CollisionShape3D", true, false).OfType<CollisionShape3D>()
                .Sum(c => c.Shape is ConcavePolygonShape3D concave ? (long)concave.Data.Length / 3 : 0);
            fields.Add($"\"copies_multimeshes\":{multimeshes.Length}");
            fields.Add($"\"copies_instances\":{multimeshes.Sum(m => (long)(m.Multimesh?.InstanceCount ?? 0))}");
            fields.Add($"\"collision_shape_triangles\":{faces}");
            if (built.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName) is { } copies)
                fields.Add($"\"copies_held\":{copies.InstancesHeld},\"copies_tiles_drawn\":{copies.VisualTilesBuilt}");
            if (built.GetNodeOrNull<TerrainCollision>(TerrainCollision.ViewName) is { } ground)
                fields.Add($"\"ground_tiles\":{ground.TilesBuilt},\"ground_of\":{ground.TileCount},\"ground_triangles\":{ground.TrianglesBuilt},\"ground_sync_builds\":{ground.SyncBuilds},\"far_triangles\":{ground.FarTriangles},\"far_tiles_shown\":{ground.FarTilesShown}");
            fields.Add($"\"room_mesh_bytes_mib\":{R(world.Room.VerifiedMeshes.Values.Sum(b => (long)b.Length) / 1048576.0)}");
            fields.Add($"\"nodes\":{Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}");
            fields.Add(FormattableString.Invariant($"\"player_m\":\"{world.Player.GlobalPosition.X:0.##} {world.Player.GlobalPosition.Y:0.###} {world.Player.GlobalPosition.Z:0.##}\""));
        }
        GD.Print("ISLAND_MEMORY {" + string.Join(",", fields) + "}");
    }
}
