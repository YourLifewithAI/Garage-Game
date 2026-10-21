using Godot;
using System;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>The sun's shadows over open land: how far they reach, in how many splits, and where the splits fall (fractions of the reach).</summary>
public readonly record struct LandShadows(float DistanceM, int Splits, float Split1, float Split2, float Split3);

/// <summary>The environment's depth fog: where it begins and is whole (metres from the camera), how much of the horizon's colour it reaches, and its curve.</summary>
public readonly record struct DepthFog(bool Enabled, float BeginM, float EndM, float Density, float Curve);

/// <summary>
/// How far you see (S9, the look at 10x): rules from the room's own data, so the 1x island, the 10x island and every island
/// between look right without a number per island. The body keeps its size at every scale, so the distances the eye judges
/// (where haze begins, how far shadows matter) are metres of the body's world; what grows with the island is how far there is
/// to see. The run's recommendation to the founder (docs/runs/RUN-2-TEN-TIMES.md): about 250 m, soft haze from about 40 m;
/// small plants to 40-60 m and trees to about 150 m (DrawDistances); the sun's shadows over about 60 m in four splits.
/// <list type="bullet">
/// <item><b>The far plane</b> is FarPerSpan island spans (the diagonal of the land's box), at least MinFarM (today's 100 m: every
/// room and the 1x island keep it) and at most MaxFarM (the plan's 250 m).</item>
/// <item><b>Aerial haze</b> on open land: the environment's depth fog in the sky's horizon colour, from HazeStartM to the far
/// plane, reaching HazeMax there along HazeCurve. Rooms have none. On the 1x island everything is nearer than HazeStartM, so
/// only the far sea takes it. The sea mist (OpenSea) closes in from it as a swimmer swims out.</item>
/// <item><b>The sun's shadows</b> on open land reach ShadowPerSpan island spans, at least MinShadowM (today's 30 m in two splits,
/// which the 1x island keeps) and at most MaxShadowM; past MinShadowM they take four splits, the first as sharp as before.</item>
/// </list>
/// </summary>
public static class ViewDistances
{
    public const float MinFarM = 100f, MaxFarM = 250f, FarPerSpan = 2.5f;
    /// <summary>Where the haze begins: about 400 body heights, past the reach of everything the body does.</summary>
    public const float HazeStartM = 40f;
    /// <summary>
    /// How much of the horizon's colour the haze reaches at the far plane, and how it grows: Godot's depth fog eases in (a
    /// smoothstep), so a curve of 0.5 lets the haze show from about 40 m (a twentieth at 50 m, a quarter at 100 m) instead of only
    /// near the far plane. The mist keeps the curve while it closes in, so every distance only ever thickens.
    /// </summary>
    public const float HazeMax = 0.6f, HazeCurve = 0.5f;
    public const float MinShadowM = 30f, MaxShadowM = 60f, ShadowPerSpan = 0.6f;
    /// <summary>The four splits past MinShadowM, as fractions of the reach: the first 4.8 m at 60 m (sharper than two splits' 7.5 m).</summary>
    public const float FourSplit1 = 0.08f, FourSplit2 = 0.2f, FourSplit3 = 0.45f;
    /// <summary>Two splits (the room look's and the 1x island's): the first over a quarter of the reach.</summary>
    public const float TwoSplit1 = 0.25f;

    /// <summary>The island's span: the diagonal of its land on the ground plane (the coast's box when it has a sea, else the room's box).</summary>
    public static float SpanM(RoomData room)
    {
        var box = room.Sea is { } sea ? sea.LandBounds(room.Bounds, 0f) : room.Bounds;
        return new Vector2(box.Size.X, box.Size.Z).Length();
    }

    /// <summary>The cameras' far plane for a room: FarPerSpan spans, between MinFarM and MaxFarM.</summary>
    public static float FarPlaneM(RoomData room) => FarPlaneForSpan(SpanM(room));

    public static float FarPlaneForSpan(float spanM) => Mathf.Clamp(FarPerSpan * (float.IsFinite(spanM) ? spanM : 0f), MinFarM, MaxFarM);

    /// <summary>The sun's shadows over open land with a given span: the reach, and two splits up to MinShadowM, four past it.</summary>
    public static LandShadows OpenLandShadows(float spanM)
    {
        var distance = Mathf.Clamp(ShadowPerSpan * (float.IsFinite(spanM) ? spanM : 0f), MinShadowM, MaxShadowM);
        return distance <= MinShadowM + 0.01f
            ? new LandShadows(MinShadowM, 2, TwoSplit1, 0f, 0f)
            : new LandShadows(distance, 4, FourSplit1, FourSplit2, FourSplit3);
    }

    /// <summary>
    /// The depth fog for a moment: with no mist, the haze (open land) or nothing; at full mist (OpenSea, 1) the sea mist that hides
    /// everything past OpenSea.MistFullM; between, every distance closes in geometrically and the amount grows with the mist, so
    /// the island fades steadily as a swimmer swims out. Without haze it is OpenSea.MistFog's fog, off while there is no mist.
    /// </summary>
    public static DepthFog Fog(float mist, float far, bool haze)
    {
        mist = Mathf.Clamp(float.IsFinite(mist) ? mist : 1f, 0f, 1f);
        if (!haze)
        {
            var (begin, end) = OpenSea.MistFog(mist, far);
            return new DepthFog(mist > 0f, begin, end, 1f, 1f);
        }
        far = Mathf.Max(far, HazeStartM * 2f);
        float Toward(float from, float to) => from * Mathf.Pow(to / from, mist);
        return new DepthFog(true, Toward(HazeStartM, OpenSea.MistNearM), Toward(far, OpenSea.MistFullM), Mathf.Lerp(HazeMax, 1f, mist), HazeCurve);
    }

    /// <summary>How much of the horizon's colour the fog gives a point at a distance (Godot's depth fog: the curve of a smoothstep, times the density).</summary>
    public static float FogAt(DepthFog fog, float distanceM)
    {
        if (!fog.Enabled) return 0f;
        var t = Mathf.Clamp((distanceM - fog.BeginM) / Mathf.Max(fog.EndM - fog.BeginM, 1e-4f), 0f, 1f);
        return Mathf.Pow(t * t * (3f - 2f * t), fog.Curve) * fog.Density;
    }

    /// <summary>Apply a fog to an environment (in the horizon's colour, never on the sky, no sun scatter or aerial perspective).</summary>
    public static void Apply(Godot.Environment environment, DepthFog fog, Color horizon, float energy)
    {
        if (environment.FogEnabled != fog.Enabled) environment.FogEnabled = fog.Enabled;
        if (!fog.Enabled) return;
        environment.FogMode = Godot.Environment.FogModeEnum.Depth;
        environment.FogDensity = fog.Density;
        environment.FogDepthBegin = fog.BeginM;
        environment.FogDepthEnd = fog.EndM;
        environment.FogDepthCurve = fog.Curve;
        environment.FogLightColor = horizon;
        environment.FogLightEnergy = energy;
        environment.FogSunScatter = 0f;
        environment.FogAerialPerspective = 0f;
        environment.FogSkyAffect = 0f;
        environment.FogHeightDensity = 0f;
    }
}
