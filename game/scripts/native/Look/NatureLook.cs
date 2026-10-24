using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>
/// A disposable view of saved nature cells: ash, flames and rain, in fixed MultiMesh pools rather than thousands of scene nodes.
/// Bloom's flowers and its trail went with decision 52 (Grow grows the island's own plant copies, drawn by PlantCopiesView), so
/// nothing here draws flowers any more. Nothing here grows, burns, injures, collides or changes the host's data.
/// </summary>
public partial class NatureLook : Node3D
{
    public const int MaxVisibleAsh = 1000;
    public const int MaxVisibleFlames = 200;
    public const int MaxRainDrops = 160;
    private const float VisibleDistanceM = 8;
    private CommandHost? _host;
    private MultiMeshInstance3D _ash = null!, _flames = null!, _cores = null!, _rain = null!;
    private MeshInstance3D _preview = null!;
    private readonly List<NatureCell> _burning = new();
    private double _age, _refreshAge;
    private bool _dirty = true;
    private Vector3 _lastCenter;
    private bool _rainExposed = true;
    public int FlameCount => _burning.Count;
    public bool FirePreviewVisible => _preview != null && _preview.Visible;

    public void Configure(CommandHost host, Node3D built)
    {
        if (_host != null) _host.NatureChanged -= MarkDirty;
        _host = host;
        _host.NatureChanged += MarkDirty;
        _dirty = true;
        // `built` makes the caller's derived room explicit; authority and source state remain on host.
        _ = built;
    }

    public override void _Ready()
    {
        _ash = Pool("Ash", new CylinderMesh { TopRadius = .031f, BottomRadius = .031f, Height = .0012f, RadialSegments = 8, Rings = 1 }, MaxVisibleAsh, Material(new Color("383b39")));
        _flames = Pool("Flames", new SphereMesh { Radius = .011f, Height = .06f, RadialSegments = 6, Rings = 4 }, MaxVisibleFlames, Material(new Color("ed733d"), emission: true));
        _cores = Pool("FlameHearts", new SphereMesh { Radius = .006f, Height = .031f, RadialSegments = 6, Rings = 3 }, MaxVisibleFlames, Material(new Color("ffe0a0"), emission: true));
        var rainMaterial = Material(new Color(.70f, .85f, .94f, .5f));
        rainMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        rainMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _rain = Pool("Rain", new CylinderMesh { TopRadius = .0008f, BottomRadius = .0008f, Height = .042f, RadialSegments = 3, Rings = 1 }, MaxRainDrops, rainMaterial);
        _preview = new MeshInstance3D
        {
            Name = "FirePreviewRing", Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("ffd28c"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(_preview);
        // Fire's behaviours: the Will-o'-wisps (NatureLook.Wisps.cs).
        BuildWisps();
    }

    private MultiMeshInstance3D Pool(string title, Mesh mesh, int capacity, Material material)
    {
        var draw = new MultiMeshInstance3D
        {
            Name = title,
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = capacity, VisibleInstanceCount = 0 },
            MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        AddChild(draw);
        return draw;
    }

    private static StandardMaterial3D Material(Color color, bool emission = false) => new()
    {
        AlbedoColor = color, VertexColorUseAsAlbedo = true, Roughness = .9f,
        EmissionEnabled = emission, Emission = color, EmissionEnergyMultiplier = emission ? .75f : 0,
    };

    private void MarkDirty() => _dirty = true;

    public override void _ExitTree()
    {
        if (_host != null && IsInstanceValid(_host)) _host.NatureChanged -= MarkDirty;
    }

    public override void _Process(double delta)
    {
        if (_host == null || !IsInstanceValid(_host) || !_host.NatureConfigured || _ash == null) return;
        _age += delta; _refreshAge += delta;
        var center = _host.Player is { } player && IsInstanceValid(player) ? player.GlobalPosition : GlobalPosition;
        if (_refreshAge >= .5 || _dirty || center.DistanceSquaredTo(_lastCenter) > .5f)
        {
            _refreshAge = 0; _dirty = false; _lastCenter = center;
            RefreshCells(center);
            RefreshExposure(center);
        }
        AnimateFire(); AnimateRain(center); AnimateWisps();
    }

    private void RefreshCells(Vector3 center)
    {
        if (_host == null) return;
        var near = _host.Nature.Cells.Values.Where(cell => cell.Position.DistanceSquaredTo(center) <= VisibleDistanceM * VisibleDistanceM)
            .OrderBy(cell => cell.Position.DistanceSquaredTo(center));
        var ash = 0;
        _burning.Clear();
        foreach (var cell in near)
        {
            var position = ToLocal(cell.Position);
            if (cell.BurningS > 0 && _burning.Count < MaxVisibleFlames) _burning.Add(cell);
            if (cell.Ash > .02f && ash < MaxVisibleAsh)
            {
                var scale = Mathf.Clamp(cell.Ash, .15f, 1);
                _ash.Multimesh.SetInstanceTransform(ash, new Transform3D(Basis.FromScale(new Vector3(scale, 1, scale)), position + Vector3.Up * .0015f));
                _ash.Multimesh.SetInstanceColor(ash++, new Color(1, 1, 1, 1));
            }
        }
        _ash.Multimesh.VisibleInstanceCount = ash;
        _flames.Multimesh.VisibleInstanceCount = _cores.Multimesh.VisibleInstanceCount = _burning.Count;
    }

    private void AnimateFire()
    {
        var wind = _host!.NatureWeather.WindMps;
        for (var i = 0; i < _burning.Count; i++)
        {
            var cell = _burning[i];
            var flicker = .8f + .2f * Mathf.Sin((float)_age * 9 + cell.X * 1.7f + cell.Z * 2.9f);
            var strength = Mathf.Clamp(cell.BurningS / 3, .35f, 1);
            var lift = .026f * flicker * strength;
            var position = ToLocal(cell.Position) + Vector3.Up * lift + wind * .015f;
            _flames.Multimesh.SetInstanceTransform(i, new Transform3D(Basis.FromScale(new Vector3(strength, flicker * strength, strength)), position));
            _flames.Multimesh.SetInstanceColor(i, new Color(1, .82f + flicker * .15f, .75f, 1));
            _cores.Multimesh.SetInstanceTransform(i, new Transform3D(Basis.FromScale(Vector3.One * strength), position - Vector3.Up * lift * .4f));
            _cores.Multimesh.SetInstanceColor(i, Colors.White);
        }
    }

    private void RefreshExposure(Vector3 center)
    {
        if (!IsInsideTree()) return;
        // A local shower is hidden under a roof. Physics only chooses where to draw; it never changes weather.
        using var query = PhysicsRayQueryParameters3D.Create(center + Vector3.Up * .12f, center + Vector3.Up * 4, RoomBuilder.WorldLayer);
        _rainExposed = GetWorld3D().DirectSpaceState.IntersectRay(query).Count == 0;
    }

    private void AnimateRain(Vector3 center)
    {
        var weather = _host!.NatureWeather;
        var count = _rainExposed ? (int)(weather.Rain01 * MaxRainDrops) : 0;
        _rain.Multimesh.VisibleInstanceCount = count;
        if (count == 0) return;
        var direction = (Vector3.Down + weather.WindMps * 1.5f).Normalized();
        var basis = new Basis(new Quaternion(Vector3.Up, direction));
        var anchor = new Vector3(Mathf.Floor(center.X * 2) * .5f, center.Y, Mathf.Floor(center.Z * 2) * .5f);
        for (var i = 0; i < count; i++)
        {
            var seed = unchecked((uint)(i * 2654435761u));
            var x = ((seed & 1023) / 1023f - .5f) * 2.8f;
            var z = (((seed >> 10) & 1023) / 1023f - .5f) * 2.8f;
            var fall = Mathf.PosMod((float)_age * 1.8f + i * .217f, 1.6f);
            var at = anchor + new Vector3(x, 1.5f - fall, z) + weather.WindMps * fall;
            _rain.Multimesh.SetInstanceTransform(i, new Transform3D(basis, ToLocal(at)));
            _rain.Multimesh.SetInstanceColor(i, Colors.White);
        }
    }

    public void ShowFirePreview(Vector3 center, float radius)
    {
        if (_preview == null || !center.IsFinite() || !float.IsFinite(radius) || radius <= 0) return;
        var mesh = _preview.Mesh as ImmediateMesh ?? new ImmediateMesh();
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        for (var i = 0; i <= 72; i++)
        {
            var angle = i * Mathf.Tau / 72;
            mesh.SurfaceAddVertex(ToLocal(center + new Vector3(Mathf.Cos(angle) * radius, .012f, Mathf.Sin(angle) * radius)));
        }
        mesh.SurfaceEnd();
        _preview.Mesh = mesh; _preview.Visible = true;
        PreviewIsLine = false;
    }

    public void HideFirePreview() { if (_preview != null) _preview.Visible = false; }
}
