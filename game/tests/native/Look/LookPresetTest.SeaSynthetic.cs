using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native.Look;
using GarageGame.Native.Look.Fauna;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// The open sea on synthetic geometry and physics, so its protections hold whether or not a landscape has been exported (Codex's
/// second-opinion reviews of Lane L's open sea round, Sol and Astra): the view under water asks physics' own water-column questions
/// (a dry shelf, a submerged ceiling, the shoreline); the backdrop's distant islands are laid flat on the bed (the Glow playtest: the
/// horizon is only sea and sky), and a backdrop the look cannot lay flat is hidden; the mist goes by the swimmer, not the camera; the
/// far sea's highlights fade with its colour; and the sea's fish keep to real water.
/// </summary>
public partial class LookPresetTest
{
    private const float SynthLevel = 0f;

    private static Vector2[] Square(float half) => new[] { new Vector2(-half, -half), new Vector2(half, -half), new Vector2(half, half), new Vector2(-half, half) };

    private static RoomSea SyntheticSea() => new(SynthLevel, Square(1f), Square(1.4f), Square(1.8f), Array.Empty<RoomSea.Beach>());

    private static StaticBody3D Block(Node parent, Vector3 centre, Vector3 size)
    {
        var body = new StaticBody3D { CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = centre };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        parent.AddChild(body);
        return body;
    }

    /// <summary>A sea surface sheet 60 m across: drawn with the sea's water material, and the query-only collider physics finds.</summary>
    private static MeshInstance3D SyntheticSeaSurface(Node parent)
    {
        const float half = 30f;
        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        var corners = new[] { new Vector3(-half, SynthLevel, -half), new Vector3(half, SynthLevel, -half), new Vector3(half, SynthLevel, half), new Vector3(-half, SynthLevel, half) };
        arrays[(int)Mesh.ArrayType.Vertex] = corners;
        arrays[(int)Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, 4).ToArray();
        arrays[(int)Mesh.ArrayType.Color] = Enumerable.Repeat(new Color(0.4f, 0.6f, 0.7f), 4).ToArray();
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var surface = new MeshInstance3D { Name = "SeaSurface", Mesh = mesh };
        surface.SetSurfaceOverrideMaterial(0, MaterialLibrary.ForLandscape("still_water", new Color("619baf"), new Color("3a6f80")));
        surface.SetMeta(LookDirector.WaterMeta, true);
        surface.SetMeta(LookDirector.DressedMeta, true);
        parent.AddChild(surface);
        var sheet = new ConcavePolygonShape3D { Data = new[] { corners[0], corners[1], corners[2], corners[0], corners[2], corners[3] } };
        parent.AddChild(RoomWater.CreateCollider(new[] { ((Shape3D)sheet, Transform3D.Identity) }));
        return surface;
    }

    /// <summary>
    /// A backdrop like the generator's: a floor ring 3 to 25 m out, 0.9 m down, and two islands rising out of the sea. Each island is
    /// built in two halves whose shared edge has its own vertex copies (a normal or UV seam), as an exporter may write them.
    /// indexed false writes the same triangles unindexed (geometry the look cannot split).
    /// </summary>
    private static MeshInstance3D SyntheticBackdrop(Node parent, bool indexed = true)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        const int sides = 24;
        // The floor ring.
        var radii = new[] { 3f, 6f, 10f, 15f, 25f };
        for (var r = 0; r < radii.Length; r++)
            for (var k = 0; k < sides; k++)
                positions.Add(new Vector3(Mathf.Cos(Mathf.Tau * k / sides) * radii[r], -0.9f, Mathf.Sin(Mathf.Tau * k / sides) * radii[r]));
        for (var r = 0; r + 1 < radii.Length; r++)
            for (var k = 0; k < sides; k++)
            {
                int a = r * sides + k, b = r * sides + (k + 1) % sides, c = a + sides, d = b + sides;
                indices.AddRange(new[] { a, d, b, a, c, d });
            }
        // Two islands: cones, each in two halves with their own copies of the shared apex and edge.
        foreach (var (centre, radius) in new[] { (new Vector2(8f, 0f), 1.6f), (new Vector2(-9f, 5f), 1.8f) })
            for (var half = 0; half < 2; half++)
            {
                var apex = positions.Count;
                positions.Add(new Vector3(centre.X, 1.0f, centre.Y));
                var ring = positions.Count;
                for (var k = 0; k <= 6; k++)
                {
                    var a = Mathf.Pi * (half + k / 6f);
                    positions.Add(new Vector3(centre.X + Mathf.Cos(a) * radius, -0.5f, centre.Y + Mathf.Sin(a) * radius));
                }
                for (var k = 0; k < 6; k++) indices.AddRange(new[] { apex, ring + k + 1, ring + k });
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        if (indexed)
        {
            arrays[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        }
        else arrays[(int)Mesh.ArrayType.Vertex] = indices.Select(i => positions[i]).ToArray();
        var count = ((Vector3[])arrays[(int)Mesh.ArrayType.Vertex]).Length;
        arrays[(int)Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, count).ToArray();
        arrays[(int)Mesh.ArrayType.Color] = Enumerable.Repeat(new Color(0.8f, 0.85f, 0.7f), count).ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var backdrop = new MeshInstance3D { Name = "Backdrop", Mesh = mesh };
        backdrop.SetSurfaceOverrideMaterial(0, MaterialLibrary.ForLandscape("moss", new Color("84a06f"), new Color("5a7a4a")));
        backdrop.SetMeta(LookDirector.DressedMeta, true);
        backdrop.SetMeta(LandscapeLook.LandscapePaintedMeta, true);
        parent.AddChild(backdrop);
        return backdrop;
    }

    private async Task CheckOpenSeaSynthetic(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        var sea = SyntheticSea();
        var holder = new Node3D { Name = "SyntheticSeaHolder" };
        AddChild(holder);
        var water = SyntheticSeaSurface(holder);
        var backdrop = SyntheticBackdrop(holder);
        // The bed under the sea, a rock under the water, a slab under the water with water beneath it, a shoal and a sea stack.
        Block(holder, new Vector3(0f, -0.66f - 0.5f, 0f), new Vector3(80f, 1f, 80f));
        Block(holder, new Vector3(4.5f, -0.275f, 0f), new Vector3(1f, 0.45f, 1f));  // a submerged rock: from -0.5 up to -0.05
        Block(holder, new Vector3(-4.5f, -0.15f, 0f), new Vector3(1f, 0.1f, 1f));   // submerged ceiling: a slab from -0.2 to -0.1
        Block(holder, new Vector3(0f, -0.35f, 6.5f), new Vector3(3f, 0.3f, 3f));    // a shallow shoal: top 0.2 m under the surface
        Block(holder, new Vector3(0f, -0.2f, -6.5f), new Vector3(3f, 1f, 3f));      // a rock standing out of the sea
        var bounds = new Aabb(new Vector3(-2f, -1f, -2f), new Vector3(4f, 3f, 4f));
        var open = OpenSea.Build(sea, bounds, new[] { water }, new[] { backdrop });
        holder.AddChild(open);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var space = GetWorld3D().DirectSpaceState;

        // 1. The view under water agrees with physics: the bodies' own column (RoomWater.At, then RoomSea.OpenWater), the eye
        // under its surface, and the sea's water (a pond keeps its own veil).
        bool PhysicsUnder(Vector3 eye)
        {
            var column = RoomWater.At(space, eye, RoomWater.SearchM, 0f);
            if (!column.Wet) column = sea.OpenWater(space, eye, RoomWater.SearchM, 0f);
            return column.Wet && eye.Y < column.SurfaceY && Mathf.Abs(column.SurfaceY - sea.LevelM) < 0.003f;
        }
        var eyes = new (string Name, Vector3 Eye)[]
        {
            ("the open sea", new Vector3(6f, -0.2f, -1.5f)), ("inside a submerged rock", new Vector3(4.5f, -0.3f, 0f)),
            ("under a submerged ceiling", new Vector3(-4.5f, -0.3f, 0f)), ("the shoreline, inside the simplified coast", new Vector3(0.6f, -0.05f, 0.6f)),
            ("below the bed", new Vector3(6f, -0.8f, -1.5f)), ("in the air", new Vector3(6f, 0.1f, -1.5f)),
        };
        var camera = new Camera3D { Far = 100f };
        holder.AddChild(camera);
        foreach (var (name, eye) in eyes)
        {
            camera.GlobalPosition = eye;
            open.Follow(camera);
            Check(open.Veil.Visible == PhysicsUnder(eye), $"the view under water agrees with physics {name} (veil {open.Veil.Visible}, physics {PhysicsUnder(eye)})");
        }

        // 4. No distant islands: both islands (each built in halves with their own seam vertices) lie flat on the bed with the floor.
        var top = backdrop.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Select(v => backdrop.GlobalTransform * v)
            .Max(v => v.Y - sea.OpenSeaBedAt(new Vector2(v.X, v.Z)));
        Check(top < 1e-4f && open.Note.Length == 0 && holder.FindChildren("DistantIsland*", "", true, false).Count == 0,
            $"the backdrop's islands are laid flat on the bed with its floor, nothing standing above it ({top * 1000f:0.###} mm): {open.Note}");

        // 2. The mist goes by the swimmer, not the camera: switching F1 to F4, or orbiting F3, never changes it.
        var swimmer = new Node3D { Name = "SynthSwimmer" };
        holder.AddChild(swimmer);
        open.SetSwimmer(() => swimmer);
        open.Atmosphere = new Godot.Environment();
        foreach (var along in new[] { open.IslandReachM - 0.5f, open.IslandReachM + 4f, open.SeamM, open.SeamM + 30f })
        {
            swimmer.GlobalPosition = new Vector3(open.SeamCentre.X + along, -0.02f, open.SeamCentre.Y);
            var mists = new List<(float Mist, float End)>();
            foreach (var offset in new[] { new Vector3(0f, 0.09f, 0f), new Vector3(0.1f, 0.13f, 0.3f), new Vector3(2.2f, 1.5f, 0f), new Vector3(-1.0f, 1.0f, 0.9f), new Vector3(0.7f, 2.2f, -0.6f) })
            {
                camera.GlobalPosition = swimmer.GlobalPosition + offset;
                open.Follow(camera);
                mists.Add((open.Mist, open.Atmosphere.FogEnabled ? open.Atmosphere.FogDepthEnd : float.PositiveInfinity));
            }
            Check(mists.All(m => m == mists[0]) && Mathf.IsEqualApprox(mists[0].Mist, OpenSea.MistFor(along, open.IslandReachM)),
                $"with the swimmer {along:0.0} m out, every view (F1, F2, F4, F3 orbiting) has the same mist ({mists[0].Mist:0.##})");
        }

        // 5. A backdrop the look cannot lay flat (more than one surface) is hidden, not left standing on the horizon, and the look says why.
        var twoSurfaces = SyntheticBackdrop(holder);
        var extra = (ArrayMesh)twoSurfaces.Mesh;
        extra.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, extra.SurfaceGetArrays(0));
        var rejected = OpenSea.Build(sea, bounds, new[] { water }, new[] { twoSurfaces });
        holder.AddChild(rejected);
        Check(!twoSurfaces.Visible && rejected.Note.Contains("hidden"), $"a backdrop of two surfaces is hidden, islands and all, and the look says why: {rejected.Note}");
        var unindexed = SyntheticBackdrop(holder, indexed: false);
        var flat = OpenSea.Build(sea, bounds, new[] { water }, new[] { unindexed });
        holder.AddChild(flat);
        var unindexedTop = unindexed.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Max(v => v.Y - sea.OpenSeaBedAt(new Vector2(v.X, v.Z)));
        Check(unindexed.Visible && unindexedTop < 1e-4f, "an unindexed backdrop is laid flat too");

        // 7. The sea's fish keep to real water: never in a rock standing out of the sea or over a shoal shallower than they need.
        var life = SeaLife.Create(sea, () => swimmer);
        holder.AddChild(life);
        Check(life.Habitable(new Vector2(6f, -1.5f)) && !life.Habitable(new Vector2(0f, -6.5f)) && !life.Habitable(new Vector2(0f, 6.5f)),
            $"the sea's fish may live in open water ({life.Habitable(new Vector2(6f, -1.5f))}), never in a rock standing out of the sea ({life.Habitable(new Vector2(0f, -6.5f))}) or over a shoal 20 cm deep ({life.Habitable(new Vector2(0f, 6.5f))})");
        swimmer.GlobalPosition = new Vector3(0f, -0.02f, -9f);
        for (var step = 0; step < 90; step++) life.Advance(1f / 30f);
        Check(life.Fish.All(f => f.Position.Y < -500f || life.Habitable(new Vector2(f.Position.X, f.Position.Z))), "and every fish stays over real water as it swims");

        // 6. Highlights fade with the colour: the far sea's (and the land shader's haze, kept for later) specular go into the horizon with them.
        var waterCode = Regex.Replace(GD.Load<Shader>(LandscapeLook.WaterShaderPath).Code, @"//[^\n]*", "");
        var landCode = Regex.Replace(GD.Load<Shader>(LandscapeLook.ShaderPath).Code, @"//[^\n]*", "");
        Check(Regex.IsMatch(waterCode, @"SPECULAR_LIGHT\s*\+=[^;]*\(1\.0\s*-\s*horizon_v\)") && Regex.IsMatch(landCode, @"SPECULAR_LIGHT\s*\+=[^;]*\(1\.0\s*-\s*haze_v\)"),
            "the far sea's and the hazed islands' highlights fade into the horizon with their colour");
        holder.QueueFree();
        await Frames(1);
    }
}
