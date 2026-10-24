using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// The island-memory probe (--probe=island-memory --room=DIR, S9): where an island's memory goes, with the real renderer. The room
/// is put together step by step as RoomWorld does (its data, the look, the land and its things, the copies) and taken apart again
/// (the copies, the land's collision, the land's finest level), and after each step the process's memory is read with garbage
/// collected first: the working set, private bytes, the engine's own allocations, C#'s live heap and video memory. Adding shows
/// what each part costs the operating system; taking apart shows exactly what each part holds in the engine and on the GPU.
/// The full game adds the avatars, the HUD, the kernel, navigation and the fish on top (their total is the captures' memory).
/// </summary>
public partial class LookCaptureHarness
{
    private async Task<List<Dictionary<string, object>>> ProbeIslandMemory()
    {
        var directory = RoomWorld.ResolveRoom(Arg("room", "").Replace('\\', '/'));
        var steps = new List<Dictionary<string, object>>();
        async Task Step(string what, int frames = 30)
        {
            for (var i = 0; i < frames; i++) await NextFrame();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            for (var i = 0; i < 3; i++) await NextFrame();
            var memory = MemoryNow();
            memory["step"] = what;
            steps.Add(memory);
            GD.Print($"LOOK_PROBE island-memory {what}: working set {memory["working_set_mib"]} MiB, private {memory["private_mib"]}, engine {memory["engine_static_mib"]}, C# {memory["managed_live_mib"]}, video {memory["video_mib"]}");
        }
        BuildTarget(new Vector2I(1920, 1080));
        await Step("the engine, the window and the harness");
        var room = RoomData.Load(directory);
        await Step($"the room's data read ({room.RoomId})");
        var holder = new Node3D { Name = "MemoryProbe" };
        _target!.AddChild(holder);
        var look = new LookDirector { Name = "Look" };
        holder.AddChild(look);
        look.Apply(StylePreset.Resolve(RoomWorld.DefaultStyleId, RoomWorld.DefaultStyleVersion), room);
        _look = look;
        await Step("the look applied (environment, sky, grade)");
        var built = RoomBuilder.Build(room);
        var copies = built.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
        if (copies != null) built.RemoveChild(copies);
        holder.AddChild(built);
        look.Dress(built);
        var bounds = room.Bounds;
        var spawn = room.SpawnFor("player").PositionM;
        // A camera over the spawn looking across the island, so the land is drawn (pipelines compiled, every level in use somewhere).
        _camera.GlobalPosition = spawn + new Vector3(0f, 3f, 0f);
        _camera.LookAt(new Vector3(bounds.GetCenter().X, spawn.Y, bounds.GetCenter().Z), Vector3.Up);
        _camera.Far = 250f;
        await Step("the land, its water and its things built and drawn (terrain levels, collision, objects)", 60);
        if (copies != null)
        {
            built.AddChild(copies);
            for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.Elapsed.TotalSeconds < 60 && !copies.Complete;) await NextFrame();
            await Step($"the copies built and drawn ({room.Copies!.TotalCount} copies)", 60);
            copies.QueueFree();
            await Step("the copies freed");
        }
        foreach (var shape in built.FindChildren("*", "CollisionShape3D", true, false).OfType<CollisionShape3D>().Where(c => c.Shape is ConcavePolygonShape3D))
            shape.QueueFree();
        await Step("the land's collision freed");
        foreach (var mesh in built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Where(m => m.HasMeta("terrain_level") && m.GetMeta("terrain_level").AsInt32() == 0))
            mesh.QueueFree();
        await Step("the land's finest level freed");
        holder.QueueFree();
        _look = null;
        await Step("everything freed but the room's data");
        GC.KeepAlive(room);
        return steps;
    }
}
