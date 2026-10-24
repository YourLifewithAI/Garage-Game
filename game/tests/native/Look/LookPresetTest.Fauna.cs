using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Look.Fauna;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// Run 2, Lane L (the founder's playtest round): water you can see into, and fish. The water shader is a see-through glaze,
/// two-sided, that casts no shadow; the pond survey finds the water and its bed by a downward ray to the world layer; fish are
/// placed deterministically from the room and its water (more in bigger ponds, none in a shallow brook), stay in water deep
/// enough to swim in and between the bed and the surface, keep loosely together, dart away from an avatar in or at the water
/// and settle back; they are one MultiMesh and nothing else (no entity, no collider). Synthetic pond fixtures, since today's
/// garage water is shallow; the real garage is surveyed too when it has been exported.
/// </summary>
public partial class LookPresetTest
{
    private static readonly Vector3 PondAt = new(40f, 0f, 40f);

    private async Task CheckPondLife(StylePreset preset)
    {
        CheckWaterShader(preset);
        await CheckWaterDressing(preset);
        CheckShoalPlacement();
        await CheckPondFixture();
        await CheckPondSurveyInSlices();
        await CheckRealPondLife(preset);
        MaterialLibrary.Configure(preset);
    }

    /// <summary>The landscape's water wears the water shader: see-through with depth, two-sided, no refraction; every uniform set.</summary>
    private void CheckWaterShader(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        var still = MaterialLibrary.ForLandscape("still_water", new Color("619baf"), new Color("3a6f80")) as ShaderMaterial;
        var flowing = MaterialLibrary.ForLandscape("flowing_water", new Color("7eafc0"), new Color("4a8090")) as ShaderMaterial;
        var meadow = MaterialLibrary.ForLandscape("meadow", new Color("c7b8ad"), new Color("66a040")) as ShaderMaterial;
        Check(still?.Shader.ResourcePath == LandscapeLook.WaterShaderPath && flowing?.Shader.ResourcePath == LandscapeLook.WaterShaderPath && meadow?.Shader.ResourcePath == LandscapeLook.ShaderPath,
            "still and flowing water wear the water shader; the land keeps its own");
        Check(still != null && still.HasMeta("landscape") && still.GetMeta("material_role").AsString() == "still_water" && MaterialLibrary.BakeAlbedo(still) != null && still.GetShaderParameter("kind").AsInt32() == (int)LandKind.StillWater,
            "a water material is still a landscape material of its harness role, with a bake colour");
        Check(flowing != null && flowing.GetShaderParameter("clarity_m").AsSingle() > still!.GetShaderParameter("clarity_m").AsSingle(), "a brook runs clearer than a pond");
        var shader = GD.Load<Shader>(LandscapeLook.WaterShaderPath);
        var declared = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).Where(n => n != "depth_texture").ToHashSet();
        var set = MaterialLibrary.WaterParameterNames.ToHashSet();
        Check(declared.Count >= 25 && set.Except(declared).Count() == 0, "every parameter a water material is given is a uniform of the water shader: not declared " + string.Join(",", set.Except(declared)));
        Check(declared.Except(set).Count() == 0, "every uniform of the water shader is set: unset " + string.Join(",", declared.Except(set)));
        var code = Regex.Replace(Regex.Replace(shader.Code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        Check(Regex.IsMatch(code, @"render_mode[^;]*\bcull_disabled\b") && Regex.IsMatch(code, @"render_mode[^;]*\bblend_mix\b") && Regex.IsMatch(code, @"\bALPHA\s*="),
            "the water is a see-through glaze drawn from both sides (visible from below)");
        Check(Regex.IsMatch(code, @"hint_depth_texture") && !code.Contains("hint_screen_texture", StringComparison.Ordinal),
            "it reads the depth under it (clear shallows, a deep middle) and copies no screen: painted, no refraction");
        Check(Regex.IsMatch(code, @"\bFRONT_FACING\b") && Regex.IsMatch(code, @"baked_color\s*=\s*COLOR\.rgb"), "it paints the underside its own way, and keeps the generator's water colour");
        // The fish and the veil shaders parse, and declare what PondLife sets.
        var fish = GD.Load<Shader>(PondLife.ShaderPath).GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
        var veil = GD.Load<Shader>(PondLife.VeilShaderPath).GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
        Check(fish.IsSupersetOf(new[] { "minnow_color", "golden_color", "carp_color", "wag_amount" }) && veil.IsSupersetOf(new[] { "pond_count", "pond_box", "pond_level", "light_level" }),
            $"the fish shader ({fish.Count} uniforms) and the underwater veil ({veil.Count}) parse and take what PondLife sets");
        Check(WaterLook.Still.DeepTint.X < WaterLook.Still.DeepTint.Z && WaterLook.Still.DeepTint.Length() < WaterLook.Still.ShallowTint.Length() && WaterLook.Still.ClarityM is > 0.02f and < 0.2f,
            "the deep middle is darker and cooler than the shallows, and a few centimetres of water already cloud it");
    }

    /// <summary>A landscape's water mesh is dressed see-through: no shadow of its own (it would shade its bed as a slab), no GI wall.</summary>
    private async Task CheckWaterDressing(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        var room = RoomData.Load(RoomWorld.DefaultRoom);
        var (holder, look) = NewDirector(preset, room, "WaterDressHolder");
        var root = new Node3D { Name = "WaterRoot" };
        holder.AddChild(root);
        var water = PartOwner("shell:water_still_water", shell: true);
        var waterMesh = LandscapeMesh("still_water", new Color(0.12f, 0.33f, 0.43f), new Color(0.9f, 0.95f, 1f));
        water.AddChild(waterMesh);
        var ground = PartOwner("shell:terrain_meadow", shell: true);
        var groundMesh = LandscapeMesh("meadow", new Color(0.57f, 0.49f, 0.43f), new Color(0.31f, 0.73f, 0.14f));
        ground.AddChild(groundMesh);
        root.AddChild(water);
        root.AddChild(ground);
        look.Dress(root);
        await Frames(2);
        Check(waterMesh.GetSurfaceOverrideMaterial(0) is ShaderMaterial { } painted && painted.Shader.ResourcePath == LandscapeLook.WaterShaderPath && waterMesh.HasMeta(LookDirector.WaterMeta),
            "a landscape's still water is dressed with the water shader and marked as water");
        Check(waterMesh.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off && waterMesh.GIMode == GeometryInstance3D.GIModeEnum.Disabled,
            "the water casts no shadow on its own bed and is no wall for the bounce");
        Check(groundMesh.CastShadow == GeometryInstance3D.ShadowCastingSetting.DoubleSided && !groundMesh.HasMeta(LookDirector.WaterMeta), "the ground beside it still casts its shadows");
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>A pond surveyed analytically: an elliptical bowl, shelving to its deepest in the middle.</summary>
    private static Pond SyntheticPond(int index, Vector2 centre, float radiusX, float radiusZ, float deepest, WaterKind kind = WaterKind.Still, float cell = 0.015f)
    {
        var min = centre - new Vector2(radiusX, radiusZ) * 1.1f;
        var nx = (int)MathF.Ceiling(radiusX * 2.2f / cell);
        var nz = (int)MathF.Ceiling(radiusZ * 2.2f / cell);
        var surface = new float[nx * nz];
        var bed = new float[nx * nz];
        for (var i = 0; i < surface.Length; i++)
        {
            var x = min.X + (i % nx + 0.5f) * cell - centre.X;
            var z = min.Y + (i / nx + 0.5f) * cell - centre.Y;
            var r2 = x * x / (radiusX * radiusX) + z * z / (radiusZ * radiusZ);
            surface[i] = r2 <= 1f ? 0f : float.NaN;
            bed[i] = r2 <= 1f ? -(deepest * MathF.Pow(1f - r2, 0.6f) + 0.004f) : float.NaN;
        }
        var pond = new Pond { Index = index, Kind = kind, Min = min, Cell = cell, Nx = nx, Nz = nz, Surface = surface, Bed = bed };
        pond.Summarise();
        return pond;
    }

    /// <summary>Where fish live and how many: deterministic, more in bigger ponds, none in shallow water or a brook.</summary>
    private void CheckShoalPlacement()
    {
        var small = SyntheticPond(0, Vector2.Zero, 0.3f, 0.25f, 0.12f);
        var big = SyntheticPond(0, Vector2.Zero, 1.2f, 0.9f, 0.2f);
        var shallow = SyntheticPond(0, Vector2.Zero, 0.8f, 0.6f, 0.03f);
        var brook = SyntheticPond(0, Vector2.Zero, 0.6f, 0.04f, 0.025f, WaterKind.Flowing);
        var smallFish = new Shoal("room_a", new[] { small }).Fish.Count;
        var bigShoal = new Shoal("room_a", new[] { big });
        Check(smallFish >= 5 && bigShoal.Fish.Count > smallFish * 2, $"bigger ponds get more fish ({smallFish} in a 60 cm pond, {bigShoal.Fish.Count} in a 2.4 m one)");
        Check(bigShoal.Fish.Count(f => f.Variety == FishVariety.Carp) == 1 && bigShoal.Fish.Select(f => f.Variety).Distinct().Count() == 3,
            "a big pond has both colours of small fish and one big carp of its own");
        Check(bigShoal.Fish.All(f => f.LengthM is >= Shoal.SmallMinM and <= Shoal.SmallMaxM || f.Variety == FishVariety.Carp && f.LengthM <= 0.05f),
            "small fish are 1.5 to 3 cm long, the big one under 5 cm (the avatar is 10 cm)");
        Check(new Shoal("room_a", new[] { shallow }).Fish.Count == 0 && new Shoal("room_a", new[] { brook }).Fish.Count == 0,
            $"no fish in water too shallow for a home (under {Shoal.HomeDepthM * 100f:0.#} cm) or in a shallow brook");
        var schoolSizes = bigShoal.Schools.Where(s => !s.Solitary).Select(s => s.Members.Length).ToArray();
        Check(schoolSizes.All(n => n is >= 5 and <= 9) && bigShoal.Schools.Count <= Shoal.MaxSchoolsPerPond + 1 && bigShoal.Fish.Count <= Shoal.MaxFish,
            $"small schools of five to nine ({string.Join(",", schoolSizes)}), a few per pond, a cap on the room");
        Check(Enumerable.Range(1, 40).Select(i => Shoal.SchoolsFor(i * 0.05f)).Zip(Enumerable.Range(0, 40).Select(i => Shoal.SchoolsFor(i * 0.05f)), (a, b) => a >= b).All(x => x),
            "the number of schools never falls as a pond's deep water grows");
        // Deterministic from the room and its water: the same room places the same fish; another room places them otherwise.
        var again = new Shoal("room_a", new[] { SyntheticPond(0, Vector2.Zero, 1.2f, 0.9f, 0.2f) });
        var other = new Shoal("room_b", new[] { SyntheticPond(0, Vector2.Zero, 1.2f, 0.9f, 0.2f) });
        Check(again.Fish.Count == bigShoal.Fish.Count && again.Fish.Zip(bigShoal.Fish).All(p => p.First.Position == p.Second.Position && p.First.Variety == p.Second.Variety && p.First.LengthM == p.Second.LengthM),
            "the same room and water place the same fish, exactly");
        Check(other.Fish.Count != bigShoal.Fish.Count || other.Fish.Zip(bigShoal.Fish).Any(p => p.First.Position != p.Second.Position), "another room's fish are placed otherwise");
        foreach (var pond in new[] { big })
            Check(bigShoal.Fish.All(f => InWater(pond, f)), "every fish starts in water it can swim in, between the bed and the surface");
    }

    private static bool InWater(Pond pond, Fish fish)
    {
        if (pond.DepthAt(fish.Position.X, fish.Position.Z) < Shoal.SwimDepthM - 1e-4f) return false;
        var (low, high) = Shoal.Band(pond, fish, fish.Position.X, fish.Position.Z);
        return fish.Position.Y >= low - 1e-4f && fish.Position.Y <= high + 1e-4f;
    }

    /// <summary>A bowl of terrain on the world layer, its water a non-colliding shell part like the exporter's, and a shallow brook.</summary>
    private static Node3D PondFixture(float radiusX, float radiusZ, float deepest)
    {
        var root = new Node3D { Name = "PondFixture", Position = PondAt };
        // The land: a height grid, the bowl's bed inside the ellipse, a gently rising bank outside.
        const float cell = 0.02f;
        var nx = (int)(radiusX * 3f / cell);
        var nz = (int)(radiusZ * 3f / cell);
        float Height(float x, float z)
        {
            var r2 = x * x / (radiusX * radiusX) + z * z / (radiusZ * radiusZ);
            return r2 <= 1f ? -(deepest * MathF.Pow(1f - r2, 0.6f) + 0.004f) : 0.03f * (MathF.Sqrt(r2) - 1f) + 0.002f;
        }
        var faces = new List<Vector3>();
        for (var j = 0; j < nz; j++)
            for (var i = 0; i < nx; i++)
            {
                var x0 = -radiusX * 1.5f + i * cell;
                var z0 = -radiusZ * 1.5f + j * cell;
                var a = new Vector3(x0, Height(x0, z0), z0);
                var b = new Vector3(x0 + cell, Height(x0 + cell, z0), z0);
                var c = new Vector3(x0, Height(x0, z0 + cell), z0 + cell);
                var d = new Vector3(x0 + cell, Height(x0 + cell, z0 + cell), z0 + cell);
                faces.AddRange(new[] { a, b, c, b, d, c });
            }
        var land = new StaticBody3D { Name = "Land", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        var shape = new ConcavePolygonShape3D();
        shape.SetFaces(faces.ToArray());
        land.AddChild(new CollisionShape3D { Shape = shape });
        root.AddChild(land);
        // The brook beside it: a channel 2.5 cm deep.
        var brookBed = new StaticBody3D { Name = "BrookBed", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = new Vector3(radiusX * 1.5f + 0.2f, -0.025f - 0.01f, 0f) };
        brookBed.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.2f, 0.02f, 0.8f) } });
        root.AddChild(brookBed);
        root.AddChild(WaterPart("shell:water_still_water", Ellipse(radiusX * 1.02f, radiusZ * 1.02f), Vector3.Zero));
        root.AddChild(WaterPart("shell:water_flowing_water", Strip(0.08f, 0.6f), new Vector3(radiusX * 1.5f + 0.2f, 0f, 0f)));
        return root;
    }

    private static Node3D WaterPart(string id, ArrayMesh mesh, Vector3 at)
    {
        var part = new Node3D { Name = id.Replace(':', '_'), Position = at };
        part.SetMeta("entity_id", id);
        part.SetMeta("surface_role", "backdrop");
        part.SetMeta("material_role", "water");
        part.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });
        return part;
    }

    private static ArrayMesh Ellipse(float rx, float rz)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        const int ring = 48;
        for (var k = 0; k < ring; k++)
        {
            var a0 = Mathf.Tau * k / ring;
            var a1 = Mathf.Tau * (k + 1) / ring;
            tool.SetNormal(Vector3.Up);
            tool.AddVertex(Vector3.Zero);
            tool.AddVertex(new Vector3(MathF.Cos(a1) * rx, 0f, MathF.Sin(a1) * rz));
            tool.AddVertex(new Vector3(MathF.Cos(a0) * rx, 0f, MathF.Sin(a0) * rz));
        }
        tool.Index();
        return tool.Commit();
    }

    private static ArrayMesh Strip(float width, float length)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 a = new(-width / 2, 0f, -length / 2), b = new(width / 2, 0f, -length / 2), c = new(-width / 2, 0f, length / 2), d = new(width / 2, 0f, length / 2);
        foreach (var v in new[] { a, b, c, b, d, c }) { tool.SetNormal(Vector3.Up); tool.AddVertex(v); }
        tool.Index();
        return tool.Commit();
    }

    /// <summary>
    /// The first recorded play lost 0.6 to 0.8 s to one frame as the island opened: the survey of the water cast every bed ray in it.
    /// Spread over frames (a bounded few rays a step, the arithmetic on a worker thread) it makes exactly the ponds the one-shot survey
    /// makes, no step takes more than a few milliseconds of the main thread, and the game's PondLife finishes it over a number of frames.
    /// </summary>
    private async Task CheckPondSurveyInSlices()
    {
        var fixture = PondFixture(0.5f, 0.4f, 0.18f);
        AddChild(fixture);
        await PhysicsFrames(2);
        var space = GetWorld3D().DirectSpaceState;
        var whole = PondSurvey.Survey(fixture, PondSurvey.RayProbe(space));
        var surveyor = new PondSurvey.Surveyor(fixture, PondSurvey.RayProbe(space), background: true);
        double longest = 0, total = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!surveyor.Done && surveyor.Steps < 200000)
        {
            timer.Restart();
            surveyor.Step(0.25);
            longest = Math.Max(longest, timer.Elapsed.TotalMilliseconds);
            total += timer.Elapsed.TotalMilliseconds;
        }
        static bool Same(float[] a, float[] b) => a.Length == b.Length && Enumerable.Range(0, a.Length).All(i => a[i] == b[i] || (float.IsNaN(a[i]) && float.IsNaN(b[i])));
        var same = surveyor.Done && surveyor.Ponds.Count == whole.Count && Enumerable.Range(0, whole.Count).All(i =>
            surveyor.Ponds[i].Kind == whole[i].Kind && surveyor.Ponds[i].Nx == whole[i].Nx && surveyor.Ponds[i].Nz == whole[i].Nz && Same(surveyor.Ponds[i].Surface, whole[i].Surface)
            && Same(surveyor.Ponds[i].Bed, whole[i].Bed) && surveyor.Ponds[i].Level == whole[i].Level && surveyor.Ponds[i].MaxDepthM == whole[i].MaxDepthM);
        GD.Print($"LOOK_INFO: the pond survey in slices: {surveyor.Steps} steps, {surveyor.Rays} bed rays, longest step {longest:0.00} ms, {total:0.0} ms on the main thread in all");
        Check(same, $"surveyed in slices the water is exactly what the one-shot survey makes ({surveyor.Ponds.Count} bodies, {surveyor.Rays} rays)");
        Check(surveyor.Steps > 5 && longest < 12.0, $"and no step takes more than a few milliseconds of the main thread (the longest {longest:0.0} ms of {surveyor.Steps})");
        // The game's PondLife (the default: sliced) finishes over several physics frames and says so.
        var life = PondLife.Create("pond_slices", fixture);
        life.Running = false;
        AddChild(life);
        var frames = 0;
        while (!life.Surveyed && frames < 2000) { await PhysicsFrames(1); frames++; }
        Check(life.Surveyed && life.Ponds.Count == whole.Count && frames > 1 && life.Summary.Contains("bed rays over"), $"the game's PondLife surveys over {frames} physics frames: {life.Summary}");
        life.QueueFree();
        fixture.QueueFree();
        await PhysicsFrames(1);
    }

    /// <summary>The real path: survey by rays, fish in the pond and not the brook, staying in, schooling, darting and settling.</summary>
    private async Task CheckPondFixture()
    {
        var fixture = PondFixture(0.5f, 0.4f, 0.18f);
        AddChild(fixture);
        var life = PondLife.Create("pond_fixture", fixture).AtOnce();
        life.Running = false;
        AddChild(life);
        await PhysicsFrames(2);
        Check(life.Surveyed && life.Ponds.Count == 2, $"the survey finds the fixture's two bodies of water ({life.Ponds.Count}): {life.Summary}");
        var pond = life.Ponds.FirstOrDefault(p => p.Kind == WaterKind.Still);
        var brook = life.Ponds.FirstOrDefault(p => p.Kind == WaterKind.Flowing);
        if (pond == null || brook == null || life.Shoal == null) { Check(false, "the pond and the brook were both found"); fixture.QueueFree(); life.QueueFree(); return; }
        var middle = pond.DepthAt(PondAt.X, PondAt.Z);
        Check(Mathf.Abs(middle - 0.184f) < 0.01f && Mathf.Abs(pond.Level) < 0.001f, $"the bed is found by a ray down to the world layer: 18.4 cm deep in the middle ({middle * 100f:0.0} cm)");
        Check(Mathf.Abs(brook.MaxDepthM - 0.025f) < 0.004f, $"and the brook is {brook.MaxDepthM * 100f:0.0} cm deep");
        Check(pond.DepthAt(PondAt.X + 0.505f, PondAt.Z) < Shoal.SwimDepthM && pond.DepthAt(PondAt.X + 0.6f, PondAt.Z) == 0f, "where the water's edge laps over the bank there is no water to swim in");
        var shoal = life.Shoal;
        Check(shoal.Fish.Count > 0 && shoal.Schools.All(s => s.Pond == pond), $"fish live in the pond ({shoal.Fish.Count}) and none in the 2.5 cm brook");
        // One MultiMesh, no entities, no colliders.
        Check(life.School != null && life.School.Multimesh.InstanceCount == shoal.Fish.Count && life.FindChildren("*", "MultiMeshInstance3D", true, false).Count == 1,
            "every fish is an instance of one MultiMesh: a single draw");
        var meshFaces = life.School?.Multimesh.Mesh.GetFaces().Length / 3 ?? 0;
        Check(meshFaces is > 40 and < 400, $"a fish is a light mesh ({meshFaces} triangles)");
        Check(!life.HasMeta("entity_id") && life.FindChildren("*", "Node", true, false).All(n => !n.HasMeta("entity_id")) && life.FindChildren("*", "CollisionObject3D", true, false).Count == 0,
            "fish are not entities and collide with nothing: not saved, not in the kernel, sight or map");
        Check(life.IsUnderwater(new Vector3(PondAt.X, -0.05f, PondAt.Z)) && !life.IsUnderwater(new Vector3(PondAt.X, 0.05f, PondAt.Z)) && !life.IsUnderwater(new Vector3(PondAt.X + 2f, -0.05f, PondAt.Z)),
            "the underwater veil shows for a camera under the pond's surface, and not above it or away from it");
        Check(life.Veil != null && life.Veil.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off, "the veil is there, and casts nothing");

        // Ten seconds alone: they stay in the water and in their band, cruise, keep loosely together, and move.
        var dt = 1f / 60f;
        var none = Array.Empty<Vector3>();
        var start = shoal.Fish.Select(f => f.Position).ToArray();
        var escaped = 0;
        var fastest = 0f;
        for (var step = 0; step < 600; step++)
        {
            shoal.Step(dt, none);
            escaped += shoal.Fish.Count(f => !InWater(pond, f));
            fastest = Math.Max(fastest, shoal.Fish.Max(f => new Vector2(f.Velocity.X, f.Velocity.Z).Length()));
        }
        Check(escaped == 0, $"over ten seconds no fish ever leaves water it can swim in or its band between bed and surface ({escaped} fish-frames out)");
        Check(fastest <= Shoal.CruiseMaxMps * 1.01f, $"left alone they cruise, never faster than {Shoal.CruiseMaxMps * 100f:0} cm/s ({fastest * 100f:0.0})");
        var moved = shoal.Fish.Select((f, i) => (f.Position - start[i]).Length()).Average();
        Check(moved > 0.03f, $"they swim about (on average {moved * 100f:0} cm from where they started)");
        var spread = Spread(shoal);
        Check(spread < 0.2f, $"each school keeps loosely together (the farthest fish {spread * 100f:0} cm from its school's middle)");

        // An avatar wades in beside a school: they dart away, the school takes fright, then they settle back.
        var school = shoal.Schools.First(s => !s.Solitary);
        var centre = Centre(shoal, school);
        var avatar = new Vector3(centre.X, pond.Level - 0.03f, centre.Z);
        var before = school.Members.Average(m => Horizontal(shoal.Fish[m].Position - avatar));
        var peak = 0f;
        for (var step = 0; step < 30; step++)
        {
            shoal.Step(dt, new[] { avatar });
            peak = Math.Max(peak, school.Members.Average(m => new Vector2(shoal.Fish[m].Velocity.X, shoal.Fish[m].Velocity.Z).Length()));
        }
        var after = school.Members.Average(m => Horizontal(shoal.Fish[m].Position - avatar));
        Check(peak > Shoal.CruiseMaxMps * 3f && after > before + 0.05f,
            $"an avatar in the water beside them: the school darts away (mean speed up to {peak * 100f:0} cm/s; {before * 100f:0} cm away, then {after * 100f:0} cm in half a second)");
        Check(school.Members.All(m => shoal.Fish[m].Fear > 0f) && shoal.Fish.All(f => InWater(pond, f)), "the whole school takes fright, and none leaves the water to flee");
        for (var step = 0; step < 300; step++) shoal.Step(dt, none);
        var settled = shoal.Fish.Max(f => new Vector2(f.Velocity.X, f.Velocity.Z).Length());
        Check(shoal.Fish.All(f => f.Fear == 0f) && settled <= Shoal.CruiseMaxMps * 1.01f && Spread(shoal) < 0.2f,
            $"five seconds after the avatar leaves they have settled back: calm, cruising ({settled * 100f:0.0} cm/s at most) and schooling again");
        GD.Print($"LOOK_INFO: pond fixture: {shoal.Fish.Count} fish; alone they cruise at most {fastest * 100f:0.0} cm/s and wander {moved * 100f:0} cm in 10 s, schools within {spread * 100f:0} cm of their middle;"
            + $" an avatar wading in: mean speed peaks at {peak * 100f:0} cm/s, {before * 100f:0} -> {after * 100f:0} cm away in 0.5 s; 5 s later at most {settled * 100f:0.0} cm/s, schools within {Spread(shoal) * 100f:0} cm");
        // An avatar high on the bank, or far away, frightens nobody.
        var high = new Vector3(centre.X, pond.Level + 0.25f, centre.Z);
        for (var step = 0; step < 10; step++) shoal.Step(dt, new[] { high, new Vector3(centre.X + 1.5f, pond.Level, centre.Z) });
        Check(shoal.Fish.All(f => f.Fear == 0f), "an avatar 25 cm above the water, or 1.5 m away, frightens no fish");
        // The node itself moves the fish from its avatars, through the setter the world will use.
        var player = new Node3D { Name = "FakePlayer", Position = new Vector3(Centre(shoal, school).X, pond.Level - 0.02f, Centre(shoal, school).Z) };
        AddChild(player);
        life.SetAvatars(player, null);
        life.Advance(dt);
        Check(school.Members.Any(m => shoal.Fish[m].Fear > 0.9f), "the avatars handed to SetAvatars frighten the fish they come close to");
        // Same steps, same avatars, same motion: a second fixture of the same room swims exactly the same.
        var twinA = new Shoal("pond_fixture", life.Ponds);
        var twinB = new Shoal("pond_fixture", life.Ponds);
        for (var step = 0; step < 240; step++)
        {
            var threats = step is > 60 and < 90 ? new[] { avatar } : none;
            twinA.Step(dt, threats);
            twinB.Step(dt, threats);
        }
        Check(twinA.Fish.Zip(twinB.Fish).All(p => p.First.Position == p.Second.Position), "given the same steps and avatars, the motion is the same (deterministic, so it can be tested)");
        player.QueueFree();
        life.QueueFree();
        fixture.QueueFree();
        await Frames(1);
    }

    private static float Horizontal(Vector3 v) => new Vector2(v.X, v.Z).Length();

    private static Vector3 Centre(Shoal shoal, School school) => school.Members.Aggregate(Vector3.Zero, (s, m) => s + shoal.Fish[m].Position) / school.Members.Length;

    private static float Spread(Shoal shoal) =>
        shoal.Schools.Where(s => !s.Solitary).Select(s => { var c = Centre(shoal, s); return s.Members.Max(m => Horizontal(shoal.Fish[m].Position - c)); }).DefaultIfEmpty(0f).Max();

    /// <summary>The real garage landscape, when exported: its tarn is found and its deep middle holds fish; fish live only in water deep
    /// enough for a home (the tarn, and since C6 a river's deep pools), never in the far lake beyond the room.</summary>
    private async Task CheckRealPondLife(StylePreset preset)
    {
        var directory = FindLandscapeFixture();
        if (directory == null) { GD.Print("LOOK_INFO: no garage landscape exported; the real pond checks were skipped"); return; }
        MaterialLibrary.Configure(preset);
        var land = RoomData.Load(directory);
        var built = RoomBuilder.Build(land);
        AddChild(built);
        var life = PondLife.Create(land.RoomId, built).AtOnce();
        life.Running = false;
        AddChild(life);
        await PhysicsFrames(3);
        GD.Print("LOOK_INFO: garage pond life: " + life.Summary);
        var shoal = life.Shoal;
        var tarn = life.Ponds.Where(p => p.Kind == WaterKind.Still).OrderByDescending(p => p.MaxDepthM).FirstOrDefault();
        Check(tarn != null && tarn.MaxDepthM > 0.03f && land.Bounds.HasPoint(tarn.Centroid with { Y = 0.1f }), $"the garage's tarn is found inside the room, {tarn?.MaxDepthM * 100f:0.0} cm at its deepest");
        Check(shoal != null && (tarn!.AreaDeeperThan(Shoal.HomeDepthM) < Shoal.MinHomeAreaM2 || shoal.Schools.Any(s => s.Pond == tarn)),
            "where the tarn is deep enough for a home, a school lives in it");
        Check(shoal != null && shoal.Schools.All(s => s.Pond.MaxDepthM >= Shoal.HomeDepthM && land.Bounds.HasPoint(s.Pond.Centroid with { Y = 0.1f })),
            "fish only in the garage's water deep enough for a home (the tarn, a river's deep pool), never in the far lake beyond the room");
        if (shoal != null)
        {
            var out_ = 0;
            for (var step = 0; step < 180; step++)
            {
                shoal.Step(1f / 60f, Array.Empty<Vector3>());
                out_ += shoal.Fish.Count(f => !InWater(f.School < shoal.Schools.Count ? shoal.Schools[f.School].Pond : tarn!, f));
            }
            Check(out_ == 0, $"in the garage's tarn the fish stay in the water ({out_} fish-frames out over three seconds)");
        }
        life.QueueFree();
        built.QueueFree();
        await Frames(1);
    }
}
