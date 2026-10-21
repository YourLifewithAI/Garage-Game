using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look.Intro;

/// <summary>
/// The source room's walls and ceiling for the intro, built from its polygons (the floor and the boxes are the island's own ground,
/// drawn from the start field): slabs with their openings cut (RoomBuilder.ExtrudePolygon, as the room look builds a room's shell),
/// painted by intro_shell.gdshader. Visual only: no collision, nothing the world knows; built in world space and freed when the intro ends.
/// </summary>
public sealed class IntroShell
{
    public const string ShaderPath = "res://shaders/intro_shell.gdshader";
    public static readonly Color WallColour = new("d9d3c6"), CeilingColour = new("e4e0d6");
    /// <summary>A wall's top curls this far outward as it slumps (radians over its height), and it sinks its height and this share more.</summary>
    public const float CurlRad = 1.9f, SinkShare = 1.15f;

    public Node3D Root { get; }
    public IReadOnlyList<(MeshInstance3D Mesh, ShaderMaterial Material)> Walls => _walls;
    public IReadOnlyList<(MeshInstance3D Mesh, ShaderMaterial Material)> Ceilings => _ceilings;
    private readonly List<(MeshInstance3D, ShaderMaterial)> _walls = new();
    private readonly List<(MeshInstance3D, ShaderMaterial)> _ceilings = new();
    private static readonly StringName Dissolve = "dissolve", Slump = "slump";

    private IntroShell(Node3D root) => Root = root;

    public static IntroShell Build(IntroSource source)
    {
        var shader = GD.Load<Shader>(ShaderPath) ?? throw new InvalidOperationException($"the intro's shell shader {ShaderPath} could not be loaded");
        var shell = new IntroShell(new Node3D { Name = "IntroRoom" });
        var centre = source.Bounds.GetCenter();
        var longest = Math.Max(source.Bounds.Size.X, source.Bounds.Size.Z);
        foreach (var wall in source.Walls)
        {
            var low = wall.Points.Min(p => p.Y);
            var height = wall.Points.Max(p => p.Y) - low;
            if (height <= 0f) continue;
            var normal = RoomBuilder.NewellNormal(wall.Points);
            var outward = new Vector3(normal.X, 0, normal.Z);
            if (outward.LengthSquared() < 1e-6f) continue;
            outward = outward.Normalized();
            var middle = wall.Points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / wall.Points.Length;
            if (outward.Dot(new Vector3(middle.X - centre.X, 0, middle.Z - centre.Z)) < 0f) outward = -outward;
            // Thin scanned walls would read as paper at the island's scale: a hundredth of their height at least.
            var thickness = Math.Max(wall.ThicknessM, 0.01f * height);
            var mesh = Slab(wall.Points, thickness, source.Openings.Where(o => o.HostId == wall.Id));
            var material = new ShaderMaterial { Shader = shader, ResourceName = "intro wall" };
            material.SetShaderParameter("albedo", WallColour);
            material.SetShaderParameter("hinge", new Vector3(middle.X, low, middle.Z));
            material.SetShaderParameter("outward", outward);
            material.SetShaderParameter("wall_height", height);
            material.SetShaderParameter("curl_rad", CurlRad);
            material.SetShaderParameter("sink_m", height * SinkShare);
            material.SetShaderParameter("noise_m", Math.Max(0.08f * height, 0.02f));
            material.SetShaderParameter("radial_reach", 0f);
            shell._walls.Add((Add(shell.Root, wall.Id, mesh, material, height * 2f), material));
        }
        foreach (var ceiling in source.Ceilings)
        {
            var middle = ceiling.Points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / ceiling.Points.Length;
            var reach = ceiling.Points.Max(p => new Vector2(p.X - middle.X, p.Z - middle.Z).Length());
            var mesh = Slab(ceiling.Points, Math.Max(ceiling.ThicknessM, 0.004f * longest), source.Openings.Where(o => o.HostId == ceiling.Id));
            var material = new ShaderMaterial { Shader = shader, ResourceName = "intro ceiling" };
            material.SetShaderParameter("albedo", CeilingColour);
            material.SetShaderParameter("radial_centre", middle);
            material.SetShaderParameter("radial_reach", Math.Max(reach, 0.01f));
            material.SetShaderParameter("noise_m", Math.Max(0.05f * longest, 0.02f));
            shell._ceilings.Add((Add(shell.Root, ceiling.Id, mesh, material, 0f), material));
        }
        return shell;
    }

    /// <summary>The walls' slump and fraying and the ceiling's dissolve at a moment.</summary>
    public void Apply(in IntroMoment moment)
    {
        foreach (var (mesh, material) in _walls)
        {
            material.SetShaderParameter(Slump, moment.Slump);
            material.SetShaderParameter(Dissolve, moment.WallFray);
            mesh.Visible = moment.WallFray < 1f;
        }
        foreach (var (mesh, material) in _ceilings)
        {
            material.SetShaderParameter(Dissolve, moment.Ceiling);
            mesh.Visible = moment.Ceiling < 1f;
        }
    }

    private static ArrayMesh Slab(Vector3[] points, float thickness, IEnumerable<IntroOpening> openings)
    {
        // Every opening is a hole in the intro's room: the garage door stands open, the window lets the morning in.
        var holes = openings.Select(o => new ShellOpening(o.Id, o.Kind, o.HostId, o.CentreM, o.SizeM, "open", false)).ToArray();
        return RoomBuilder.ExtrudePolygon(points, thickness, holes, Array.Empty<ShellOpening>()).Mesh;
    }

    private static MeshInstance3D Add(Node3D root, string id, ArrayMesh mesh, Material material, float cullMargin)
    {
        var instance = new MeshInstance3D
        {
            Name = RoomBuilder.NodeName(id), Mesh = mesh, MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            ExtraCullMargin = cullMargin,
        };
        root.AddChild(instance);
        return instance;
    }
}
