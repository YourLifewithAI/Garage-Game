using System;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Look;

/// <summary>
/// Fire's behaviours in the look (MECHANICS.md decisions 78 and 80): the Fireline's outline while it is previewed, and the
/// Will-o'-wisps, cold ghostly flames hovering over the water and drifting slowly round their spots (the host's Wisps; the drift is
/// the look's alone and changes nothing): a soft flame by day, and by night a small light as well, on as the island's lamps would
/// be (the Fractals' rule). The flame reuses the fire's shapes, pale and cool; the light is a small pool with no shadow, as Glow's.
/// </summary>
public partial class NatureLook
{
    /// <summary>The wisp's look, provisional: its flame's size and colours, and its night light.</summary>
    public static class WispTuning
    {
        public const float FlameRadiusM = 0.012f, FlameHeightM = 0.045f, CoreRadiusM = 0.006f, CoreHeightM = 0.022f;
        public static readonly Color Flame = new("9fe8e0"), Core = new("e9fffb"), Light = new("a8f0ea");
        public const float FlameEmission = 1.4f, LightEnergy = 0.7f, LightRangeM = 0.6f;
        /// <summary>The light is full this far under the lamps' switch-on daylight (as a Fractal's).</summary>
        public const float LightFullAtSwitch = 0.5f;
        public const float FlickerHz = 1.3f, BobM = 0.008f;
    }

    private readonly MeshInstance3D[] _wispFlames = new MeshInstance3D[FireKindLaws.MaxWisps];
    private readonly MeshInstance3D[] _wispCores = new MeshInstance3D[FireKindLaws.MaxWisps];
    private readonly OmniLight3D[] _wispLights = new OmniLight3D[FireKindLaws.MaxWisps];

    /// <summary>The wisps drawn now (tests).</summary>
    public int WispsShown { get; private set; }
    /// <summary>How much of the night light the wisps carry now (0 by day, 1 at night).</summary>
    public float WispNight { get; private set; }
    /// <summary>Where each wisp is drawn now, and its light's energy (tests).</summary>
    public (Vector3 At, float Energy) WispDrawn(int index) => (_wispFlames[index].GlobalPosition, _wispLights[index].LightEnergy);

    private void BuildWisps()
    {
        var flameMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(WispTuning.Flame, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true, Emission = WispTuning.Flame, EmissionEnergyMultiplier = WispTuning.FlameEmission,
        };
        var coreMaterial = new StandardMaterial3D
        {
            AlbedoColor = WispTuning.Core, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, EmissionEnabled = true, Emission = WispTuning.Core, EmissionEnergyMultiplier = WispTuning.FlameEmission,
        };
        for (var i = 0; i < FireKindLaws.MaxWisps; i++)
        {
            _wispFlames[i] = new MeshInstance3D
            {
                Name = "WispFlame" + i, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = flameMaterial,
                Mesh = new SphereMesh { Radius = WispTuning.FlameRadiusM, Height = WispTuning.FlameHeightM, RadialSegments = 8, Rings = 4 },
            };
            _wispCores[i] = new MeshInstance3D
            {
                Name = "WispCore" + i, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = coreMaterial,
                Mesh = new SphereMesh { Radius = WispTuning.CoreRadiusM, Height = WispTuning.CoreHeightM, RadialSegments = 6, Rings = 3 },
            };
            _wispLights[i] = new OmniLight3D
            {
                Name = "WispLight" + i, Visible = false, LightColor = WispTuning.Light, OmniRange = WispTuning.LightRangeM, ShadowEnabled = false,
                LightBakeMode = Light3D.BakeMode.Disabled, LightEnergy = 0,
            };
            AddChild(_wispFlames[i]);
            AddChild(_wispCores[i]);
            AddChild(_wispLights[i]);
        }
    }

    /// <summary>Where a wisp is at a moment of its life: round its spot within WispDriftM, a slow loop with a gentle bob; the same at the same moment.</summary>
    public static Vector3 WispAt(Vector3 anchor, string id, double lifeS)
    {
        var phase = (float)(StableHash(id) % 1000) / 1000f * Mathf.Tau;
        var turn = (float)(lifeS / FireKindLaws.WispDriftPeriodS) * Mathf.Tau;
        var reach = FireKindLaws.WispDriftM;
        return anchor + new Vector3(Mathf.Sin(turn + phase) * reach, Mathf.Sin(turn * 3.7f + phase) * WispTuning.BobM, Mathf.Sin(turn * 0.62f * 2f + phase * 1.3f) * reach * 0.8f);
    }

    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = unchecked((hash ^ c) * 16777619u);
        return hash;
    }

    private void AnimateWisps()
    {
        if (_host == null || _wispFlames[0] == null) return;
        var wisps = _host.Wisps;
        var look = _host.LookHook;
        WispNight = look != null && IsInstanceValid(look) && look.Moment != null && look.Preset != null
            ? 1f - Mathf.SmoothStep(look.Preset.Tuning.Lamps.SwitchOnBelowDaylight * WispTuning.LightFullAtSwitch, look.Preset.Tuning.Lamps.SwitchOnBelowDaylight, look.Moment.Daylight)
            : 0f;
        var now = _host.Clock();
        WispsShown = Math.Min(wisps.Count, FireKindLaws.MaxWisps);
        for (var i = 0; i < FireKindLaws.MaxWisps; i++)
        {
            var shown = i < WispsShown;
            _wispFlames[i].Visible = _wispCores[i].Visible = shown;
            _wispLights[i].Visible = shown && WispNight > 0f;
            if (!shown) continue;
            var wisp = wisps[i];
            var life = (now - wisp.Born).TotalSeconds;
            var at = WispAt(wisp.Anchor, wisp.Id, life);
            var flicker = 0.85f + 0.15f * Mathf.Sin((float)life * Mathf.Tau * WispTuning.FlickerHz + i);
            _wispFlames[i].Position = ToLocal(at);
            _wispFlames[i].Scale = new Vector3(flicker, 1f / flicker, flicker);
            _wispCores[i].Position = ToLocal(at) - Vector3.Up * (WispTuning.FlameHeightM * 0.18f);
            _wispLights[i].Position = ToLocal(at);
            _wispLights[i].LightEnergy = WispTuning.LightEnergy * WispNight * flicker;
        }
    }

    /// <summary>A Fireline's outline while it is previewed (decision 78): the band the line's cells lie in, from its start to its end.</summary>
    public void ShowFireLinePreview(Vector3 start, Vector3 end, float halfWidth)
    {
        if (_preview == null || !start.IsFinite() || !end.IsFinite() || start.DistanceTo(end) < 1e-3f) return;
        var along = new Vector3(end.X - start.X, 0, end.Z - start.Z).Normalized();
        var side = new Vector3(-along.Z, 0, along.X) * Math.Max(halfWidth, 0.01f);
        var lift = Vector3.Up * 0.012f;
        var mesh = _preview.Mesh as ImmediateMesh ?? new ImmediateMesh();
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        foreach (var corner in new[] { start + side, end + side, end - side, start - side, start + side })
            mesh.SurfaceAddVertex(ToLocal(corner + lift));
        mesh.SurfaceEnd();
        _preview.Mesh = mesh;
        _preview.Visible = true;
        PreviewIsLine = true;
    }

    /// <summary>Whether the outline shown is a Fireline's (tests).</summary>
    public bool PreviewIsLine { get; private set; }
}
