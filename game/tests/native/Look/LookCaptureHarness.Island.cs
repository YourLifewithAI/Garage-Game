using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// The capture harness on islands of any scale (S9, the 10x world):
/// <list type="bullet">
/// <item>cameras worked out from the island's data ("derive": "island" in a cameras file stands for spawn, summit, hamlet, beach and
/// the flight, IslandCameras; "derive": "summit" for one of them), so one cameras file serves the 1x and the 10x garage;</item>
/// <item>memory with every camera's timings (the working set, private bytes and the peak, the engine's and C#'s share, video memory);</item>
/// <item>the slow frames of every measurement, each with what the frame spent its time on (the process and physics steps, the
/// renderer's CPU and GPU time, collections, pipeline compilations, copies' tiles built), so a spike can be traced.</item>
/// </list>
/// It uses only RoomWorld's public surface and the room's data, so it still captures commits from before S9.
/// </summary>
public partial class LookCaptureHarness
{
    private readonly List<string> _deriveNotes = new();
    private List<DerivedCamera>? _derived;

    /// <summary>The cameras file's entries with every "derive" entry replaced by the island's own cameras (as ordinary entries).</summary>
    private IEnumerable<JsonElement> Entries(JsonElement root)
    {
        foreach (var entry in root.GetProperty("cameras").EnumerateArray())
        {
            if (!entry.TryGetProperty("derive", out var derive)) { yield return entry; continue; }
            var wanted = derive.GetString() ?? "island";
            if (_derived == null)
            {
                // The rays go over the whole land: every tile of its collision is built for an instant (TerrainCollision keeps
                // it where the bodies are), once, so later camera lookups never hold the whole land again.
                TerrainCollision.EnsureAll(_world, holdS: 0.5);
                var space = _world.GetWorld3D().DirectSpaceState;
                _derived = IslandCameras.Derive(_world.Room, IslandCameras.RayGround(space, _world.Room.Bounds), IslandCameras.RaySky(space), _deriveNotes);
                foreach (var note in _deriveNotes) GD.Print("LOOK_CAPTURE derive: " + note);
            }
            var derived = _derived;
            foreach (var camera in derived.Where(c => wanted == "island" || c.Id == wanted)) yield return ToEntry(camera);
        }
    }

    /// <summary>A derived camera as a cameras-file entry: the HUD's view with the avatars staged, or a flight.</summary>
    private static JsonElement ToEntry(DerivedCamera camera)
    {
        static JsonArray V(Vector3 v) => new(Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3));
        var entry = new JsonObject
        {
            ["id"] = camera.Id, ["label"] = camera.Label, ["purpose"] = camera.Purpose, ["derived"] = true,
            ["stage"] = new JsonObject { ["player_m"] = V(camera.PlayerM), ["yaw_deg"] = Math.Round(camera.YawDeg, 2), ["companion_m"] = V(camera.CompanionM) },
        };
        if (camera.Flight is { } flight)
        {
            var along = (flight.ToM - flight.FromM).Normalized();
            var down = Mathf.Tan(Mathf.DegToRad(flight.LookDownDeg));
            entry["position_m"] = V(flight.FromM);
            entry["look_at_m"] = V(flight.FromM + along * 10f + Vector3.Down * (10f * down));
            entry["fov_deg"] = 68;
            entry["view"] = "shoulder";
            entry["hide_player_body"] = false;
            entry["flight"] = new JsonObject { ["from_m"] = V(flight.FromM), ["to_m"] = V(flight.ToM), ["speed_mps"] = flight.SpeedMps, ["look_down_deg"] = flight.LookDownDeg };
        }
        else entry["hud_view"] = camera.HudView;
        return JsonSerializer.SerializeToElement(entry);
    }

    /// <summary>A flight's camera at a time: along its line at its speed, looking ahead and down, holding at the end.</summary>
    private void Fly(JsonElement entry, double seconds)
    {
        if (!entry.TryGetProperty("flight", out var flight)) return;
        var from = Vec(flight.GetProperty("from_m"));
        var to = Vec(flight.GetProperty("to_m"));
        var length = from.DistanceTo(to);
        var travelled = Mathf.Min(length, (float)(seconds * flight.GetProperty("speed_mps").GetDouble()));
        var along = (to - from) / Mathf.Max(length, 1e-4f);
        var at = from + along * travelled;
        var down = Mathf.Tan(Mathf.DegToRad((float)flight.GetProperty("look_down_deg").GetDouble()));
        _camera.GlobalPosition = at;
        _camera.LookAt(at + along * 10f + Vector3.Down * (10f * down), Vector3.Up);
    }

    /// <summary>How long a flight takes at its speed, in frames at about 60 a second (at least the measurement's own count).</summary>
    private static int FlightFrames(JsonElement entry, int frames)
    {
        if (!entry.TryGetProperty("flight", out var flight)) return frames;
        var seconds = Vec(flight.GetProperty("from_m")).DistanceTo(Vec(flight.GetProperty("to_m"))) / flight.GetProperty("speed_mps").GetDouble();
        return Math.Max(frames, (int)(seconds * 60));
    }

    /// <summary>The process's memory now, in MiB: what the operating system sees, and the engine's, C#'s and the GPU's shares.</summary>
    private static Dictionary<string, object> MemoryNow()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return new Dictionary<string, object>
        {
            ["working_set_mib"] = Round(process.WorkingSet64 / 1048576.0),
            ["private_mib"] = Round(process.PrivateMemorySize64 / 1048576.0),
            ["peak_working_set_mib"] = Round(process.PeakWorkingSet64 / 1048576.0),
            ["engine_static_mib"] = Round(OS.GetStaticMemoryUsage() / 1048576.0),
            ["engine_static_peak_mib"] = Round(OS.GetStaticMemoryPeakUsage() / 1048576.0),
            ["managed_heap_mib"] = Round(gc.HeapSizeBytes / 1048576.0),
            ["managed_live_mib"] = Round(GC.GetTotalMemory(false) / 1048576.0),
            ["video_mib"] = Round(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0),
            ["video_texture_mib"] = Round(Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0),
            ["video_buffer_mib"] = Round(Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0),
        };
    }

    /// <summary>
    /// What the scene holds that costs memory, counted from the nodes: the copies' MultiMeshes (instances and their buffer bytes),
    /// the terrain's drawn meshes by level (vertices and indices), collision faces, navigation polygons, and the room data's bytes.
    /// </summary>
    private Dictionary<string, object> MemoryBreakdown()
    {
        var multimeshes = _world.Built.FindChildren("*", "MultiMeshInstance3D", true, false).OfType<MultiMeshInstance3D>().ToArray();
        var instances = multimeshes.Sum(m => (long)(m.Multimesh?.InstanceCount ?? 0));
        var levels = new SortedDictionary<int, (long Vertices, long Indices, int Meshes)>();
        foreach (var mesh in _world.Built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            if (mesh.Mesh == null || !mesh.HasMeta("terrain_level")) continue;
            var level = mesh.GetMeta("terrain_level").AsInt32();
            var (v, i, n) = levels.TryGetValue(level, out var sum) ? sum : (0, 0, 0);
            for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            {
                if (mesh.Mesh is ArrayMesh array) { v += array.SurfaceGetArrayLen(s); i += array.SurfaceGetArrayIndexLen(s); }
            }
            levels[level] = (v, i, n + 1);
        }
        var faces = _world.Built.FindChildren("*", "CollisionShape3D", true, false).OfType<CollisionShape3D>()
            .Sum(c => c.Shape is ConcavePolygonShape3D concave ? (long)concave.Data.Length / 3 : 0);
        var ground = _world.Built.GetNodeOrNull<TerrainCollision>(TerrainCollision.ViewName);
        var copies = _world.Built.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
        var verified = _world.Room.VerifiedMeshes.Values.Sum(b => (long)b.Length);
        return new Dictionary<string, object>
        {
            ["copies_multimeshes"] = multimeshes.Length,
            ["copies_instances_in_all_bands"] = instances,
            ["copies_instance_buffers_mib"] = Round(instances * 64 / 1048576.0),
            ["copies"] = _world.Room.Copies?.TotalCount ?? 0,
            ["terrain_levels"] = levels.ToDictionary(p => p.Key.ToString(), p => (object)new Dictionary<string, object>
                { ["meshes"] = p.Value.Meshes, ["vertices"] = p.Value.Vertices, ["indices"] = p.Value.Indices }),
            ["collision_triangles"] = faces + (ground?.TrianglesBuilt ?? 0),
            ["ground_tiles_built"] = ground?.TilesBuilt ?? 0,
            ["ground_tiles"] = ground?.TileCount ?? 0,
            ["copies_held"] = copies?.InstancesHeld ?? 0,
            ["room_verified_mesh_bytes_mib"] = Round(verified / 1048576.0),
            ["navigation_polygons"] = Performance.GetMonitor(Performance.Monitor.NavigationPolygonCount),
            ["objects"] = Performance.GetMonitor(Performance.Monitor.ObjectCount),
            ["nodes"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            ["resources"] = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
        };
    }

    /// <summary>One frame's attribution, read right after it: the engine's step times, the renderer's, collections and compilations so far.</summary>
    private readonly record struct FrameCost(double ProcessMs, double PhysicsMs, double NavigationMs, double RenderCpuMs, double GpuMs,
        int Gen0, int Gen1, int Gen2, double PauseMs, double Pipelines, int CopiesTiles, double Objects);

    private FrameCost CostNow(Rid viewport)
    {
        var view = _world.Built?.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
        return new FrameCost(
            Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
            Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
            Performance.GetMonitor(Performance.Monitor.TimeNavigationProcess) * 1000.0,
            RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport),
            RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds,
            Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh)
            + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw)
            + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization),
            view?.TilesBuiltTotal ?? 0,
            Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame));
    }

    /// <summary>A slow frame as a record: its time, and what of it went where.</summary>
    private static Dictionary<string, object> Spike(int frame, double wallMs, FrameCost before, FrameCost now)
    {
        var causes = new List<string>();
        if (now.Gen2 > before.Gen2) causes.Add("full collection");
        else if (now.Gen1 > before.Gen1) causes.Add("gen1 collection");
        else if (now.Gen0 > before.Gen0) causes.Add("gen0 collection");
        if (now.Pipelines > before.Pipelines) causes.Add($"{now.Pipelines - before.Pipelines:0} pipeline compilations");
        if (now.CopiesTiles > before.CopiesTiles) causes.Add($"{now.CopiesTiles - before.CopiesTiles} copies' tiles built");
        if (now.ProcessMs > 0.4 * wallMs) causes.Add("process step");
        if (now.PhysicsMs > 0.4 * wallMs) causes.Add("physics step");
        if (now.GpuMs > 0.6 * wallMs) causes.Add("GPU");
        if (now.RenderCpuMs > 0.4 * wallMs) causes.Add("render CPU");
        return new Dictionary<string, object>
        {
            ["frame"] = frame, ["ms"] = Round(wallMs), ["process_ms"] = Round(now.ProcessMs), ["physics_ms"] = Round(now.PhysicsMs),
            ["navigation_ms"] = Round(now.NavigationMs), ["render_cpu_ms"] = Round(now.RenderCpuMs), ["gpu_ms"] = Round(now.GpuMs),
            ["gc_pause_ms"] = Round(now.PauseMs - before.PauseMs), ["objects_drawn"] = now.Objects,
            ["causes"] = causes.Count > 0 ? string.Join(", ", causes) : "unattributed (outside the engine's steps: the OS, the driver or presenting)",
        };
    }
}
