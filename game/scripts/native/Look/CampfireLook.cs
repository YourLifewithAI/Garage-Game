using Godot;
using System.Collections.Generic;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Look;

/// <summary>
/// The island's campfires drawn (Fire's camp): a ring of stones round two crossed logs at every campfire spot the host knows, and on a
/// lit one the fire's own flames (NatureLook's flame and heart meshes and colours, flickering the same way) with a warm light, on one
/// that is out charred logs. A view
/// of the host's state only: it reads CommandHost.CampSites and IsCampfireLit, redraws when CampsChanged says so, and changes nothing.
/// </summary>
public partial class CampfireLook : Node3D
{
    /// <summary>The ring of stones round a campfire, in metres (a 10 cm body sits beside it).</summary>
    public const float RingRadiusM = 0.045f;
    private const int Stones = 8;
    private CommandHost? _host;
    private bool _dirty = true;
    private double _age;
    private readonly List<(Node3D Root, MeshInstance3D[] Flames, OmniLight3D Light)> _fires = new();
    private static readonly SphereMesh StoneMesh = new() { Radius = .011f, Height = .014f, RadialSegments = 6, Rings = 3 };
    private static readonly CylinderMesh LogMesh = new() { TopRadius = .006f, BottomRadius = .006f, Height = .07f, RadialSegments = 6, Rings = 1 };
    private static readonly SphereMesh FlameMesh = new() { Radius = .011f, Height = .06f, RadialSegments = 6, Rings = 4 };
    private static readonly SphereMesh HeartMesh = new() { Radius = .006f, Height = .031f, RadialSegments = 6, Rings = 3 };
    private static readonly StandardMaterial3D StoneMaterial = new() { AlbedoColor = new Color("8d8a83"), Roughness = .95f };
    private static readonly StandardMaterial3D LogMaterial = new() { AlbedoColor = new Color("5b3d27"), Roughness = .9f };
    /// <summary>A campfire that is out (decision 73: rain, later Snuff): its logs charred until the Gubble's Fire relights it.</summary>
    private static readonly StandardMaterial3D CharredMaterial = new() { AlbedoColor = new Color("2b2622"), Roughness = 1f };
    private static readonly StandardMaterial3D FlameMaterial = Glowing(new Color("ed733d"));
    private static readonly StandardMaterial3D HeartMaterial = Glowing(new Color("ffe0a0"));

    /// <summary>How many campfire spots are drawn, how many of them burn, and how many are out (the tests read these).</summary>
    public int Drawn { get; private set; }
    public int Burning { get; private set; }
    public int Out { get; private set; }

    public static CampfireLook Create(CommandHost host)
    {
        var look = new CampfireLook { Name = "CampfireLook" };
        look.Configure(host);
        return look;
    }

    public void Configure(CommandHost host)
    {
        if (_host != null && IsInstanceValid(_host)) _host.CampsChanged -= MarkDirty;
        _host = host;
        _host.CampsChanged += MarkDirty;
        _dirty = true;
    }

    private static StandardMaterial3D Glowing(Color color) => new()
    {
        AlbedoColor = color, Roughness = .9f, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = .75f,
    };

    private void MarkDirty() => _dirty = true;

    public override void _ExitTree()
    {
        if (_host != null && IsInstanceValid(_host)) _host.CampsChanged -= MarkDirty;
    }

    public override void _Process(double delta)
    {
        if (_host == null || !IsInstanceValid(_host)) return;
        _age += delta;
        if (_dirty) Rebuild();
        for (var f = 0; f < _fires.Count; f++)
        {
            var (_, flames, light) = _fires[f];
            for (var i = 0; i < flames.Length; i++)
            {
                var flicker = .8f + .2f * Mathf.Sin((float)_age * 9 + i * 2.1f + f * 1.3f);
                flames[i].Scale = new Vector3(1, flicker, 1);
            }
            light.LightEnergy = .55f + .1f * Mathf.Sin((float)_age * 7 + f);
        }
    }

    /// <summary>Draws every campfire spot again from the host's state (rare: a load, or a firepit lit).</summary>
    public void Rebuild()
    {
        _dirty = false;
        foreach (var child in GetChildren()) child.QueueFree();
        _fires.Clear();
        Drawn = Burning = Out = 0;
        if (_host == null || !IsInstanceValid(_host)) return;
        foreach (var camp in _host.CampSites)
        {
            var root = new Node3D { Name = camp.Id.Replace(':', '_'), Position = camp.PositionM };
            AddChild(root);
            Drawn++;
            for (var i = 0; i < Stones; i++)
            {
                var angle = Mathf.Tau * i / Stones;
                root.AddChild(new MeshInstance3D { Mesh = StoneMesh, MaterialOverride = StoneMaterial, Position = new Vector3(Mathf.Cos(angle) * RingRadiusM, .005f, Mathf.Sin(angle) * RingRadiusM) });
            }
            var charred = _host.IsCampfireOut(camp.Id);
            if (charred) Out++;
            var logs = charred ? CharredMaterial : LogMaterial;
            root.AddChild(new MeshInstance3D { Mesh = LogMesh, MaterialOverride = logs, Position = Vector3.Up * .006f, Rotation = new Vector3(Mathf.Pi / 2, .6f, 0) });
            root.AddChild(new MeshInstance3D { Mesh = LogMesh, MaterialOverride = logs, Position = Vector3.Up * .009f, Rotation = new Vector3(Mathf.Pi / 2, -.6f, 0) });
            if (!_host.IsCampfireLit(camp.Id)) continue;
            Burning++;
            var flames = new MeshInstance3D[4];
            for (var i = 0; i < 3; i++)
            {
                var angle = Mathf.Tau * i / 3;
                flames[i] = new MeshInstance3D
                {
                    Mesh = FlameMesh, MaterialOverride = FlameMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = new Vector3(Mathf.Cos(angle) * .01f, .03f, Mathf.Sin(angle) * .01f),
                };
                root.AddChild(flames[i]);
            }
            flames[3] = new MeshInstance3D { Mesh = HeartMesh, MaterialOverride = HeartMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = Vector3.Up * .022f };
            root.AddChild(flames[3]);
            var light = new OmniLight3D { LightColor = new Color("ffb070"), LightEnergy = .6f, OmniRange = .8f, Position = Vector3.Up * .06f, ShadowEnabled = false };
            root.AddChild(light);
            _fires.Add((root, flames, light));
        }
    }
}
