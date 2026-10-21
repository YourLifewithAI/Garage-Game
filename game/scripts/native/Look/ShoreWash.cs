using Godot;
using System;
using System.Collections.Generic;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>
/// The living shore's numbers (Look, Run 2: the first recorded play's mark 2, "the coastline is a perfect line where the water meets
/// the land; this should be dynamic, with waves gently washing against the shore", MECHANICS.md decision 66). Metres in the 10 cm
/// avatar's world, seconds. The look does the moving and the generator's coast stays exactly as it is: the water shader draws the
/// sea's edge back and in again (the sea's own depth under the wash), the land shader paints the wash on the sand and the sand's damp
/// band, both from the one function in shore_wash.gdshaderinc, which ShoreWash mirrors.
///
/// Calm and slow, storybook, never surf: every <see cref="PeriodS"/> a wash climbs a beach (the first <see cref="RiseShare"/> of the
/// period, eased at both ends) to a crest up to <see cref="RunUpM"/> over the sea's level, then draws back, slowly, to
/// <see cref="DrawBackM"/> under it. On the island garage's beaches (a slope of about 0.16 above the sea, 0.4 below it) that is
/// about 30 cm of sand wetted and 10 cm of shallows laid bare, a few body lengths; the edge moves at about a quarter of the body's
/// walking pace at its fastest. Each stretch of shore keeps its own time (<see cref="LaneM"/> wide, up to <see cref="LaneSpread"/> of
/// a period apart) and each wave its own height (<see cref="SizeLow"/> to 1), so the edge is never a ruler's line and the whole
/// shore never breathes in unison.
/// </summary>
public sealed record ShoreLook(
    float PeriodS, float RunUpM, float DrawBackM, float LaneM, float RiseShare,
    float SoftM, float FoamM, float BrushM, float BrushAmpM,
    float WetAboveM, float WetFadeM, float ReachM, float FlatSlope, Vector3 WetTint, float WetGloss, float FoamWidthM = 0.04f, float SkyRim = 0.75f)
{
    /// <summary>The island garage's shore: a wash every 8 s, up to 4.5 cm over the sea and 4 cm under it.</summary>
    public static readonly ShoreLook Default = new(
        PeriodS: 8f, RunUpM: 0.045f, DrawBackM: 0.04f, LaneM: 1.3f, RiseShare: 0.4f,
        SoftM: 0.003f, FoamM: 0.008f, BrushM: 0.09f, BrushAmpM: 0.004f,
        WetAboveM: 0.012f, WetFadeM: 0.012f, ReachM: 0.8f, FlatSlope: 0.03f, WetTint: new Vector3(0.70f, 0.70f, 0.73f), WetGloss: 0.35f);

    /// <summary>How far the shore's field holds a distance to the coast: twice the reach, so the field is whole across the soft edge of the reach.</summary>
    public float FieldRangeM => 2f * ReachM;
}

/// <summary>
/// The wash at a place and a moment, as the shaders draw it (shore_wash.gdshaderinc, line for line), and the shore's field: each
/// place's distance to the coast, a texture the land shader reads so the wash only ever climbs the beaches and never low ground
/// inland. The constants below are the include's numbers; the look suite reads them out of the include's text and checks them.
/// </summary>
public static class ShoreWash
{
    public const string IncludePath = "res://shaders/shore_wash.gdshaderinc";
    /// <summary>The shores' hold uniform's value when the wash runs on the engine's time (the shaders' TIME, which the engine repeats every hour: 450 whole periods).</summary>
    public const float FreeRunning = -1f;
    /// <summary>Each lane's time is up to this share of a period from its neighbour's.</summary>
    public const float LaneSpread = 0.8f;
    /// <summary>A wave's height is this share of the run-up at the least, the whole of it at the most.</summary>
    public const float SizeLow = 0.6f;
    /// <summary>The damp band's top is this share of the run-up at the least (smooth along the shore), the whole of it at the most.</summary>
    public const float DampLow = 0.8f;
    /// <summary>The field's finest cell (metres) and its most cells along a side.</summary>
    public const float FieldCellM = 0.1f;
    /// <summary>The ground is damp from this far under the least the wash draws back to (ramping in over it) up to this far over the damp band's top.</summary>
    public const float BandBelowM = 0.04f, BandAboveM = 0.01f;
    public const int FieldMaxSide = 1024;

    private static float Fract(float x) => x - MathF.Floor(x);

    private static float Smoothstep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    /// <summary>shore_hash: a hash of a point to [0, 1) (GPU float arithmetic differs in the last bits; nothing here depends on them).</summary>
    public static float Hash(Vector2 p)
    {
        float x = Fract(p.X * 0.1031f), y = Fract(p.Y * 0.1031f), z = Fract(p.X * 0.1031f);
        float d = x * (y + 33.33f) + y * (z + 33.33f) + z * (x + 33.33f);
        x += d; y += d; z += d;
        return Fract((x + y) * z);
    }

    /// <summary>shore_noise: smooth value noise in [0, 1].</summary>
    public static float Noise(Vector2 p)
    {
        var i = new Vector2(MathF.Floor(p.X), MathF.Floor(p.Y));
        var f = new Vector2(p.X - i.X, p.Y - i.Y);
        var u = new Vector2(f.X * f.X * (3f - 2f * f.X), f.Y * f.Y * (3f - 2f * f.Y));
        var bottom = Mix(Hash(i), Hash(i + new Vector2(1f, 0f)), u.X);
        var top = Mix(Hash(i + new Vector2(0f, 1f)), Hash(i + new Vector2(1f, 1f)), u.X);
        return Mix(bottom, top, u.Y);
    }

    /// <summary>
    /// shore_level: where the wash stands at a place (x, z in metres) when the shore's clock reads clockS: Level is its height above
    /// the sea's level in metres (negative: drawn back below it), Damp the top of the damp band there (smooth along the shore, never
    /// below a wave's crest).
    /// </summary>
    public static (float Level, float Damp) Level(Vector2 xz, float clockS, ShoreLook look)
    {
        var cycles = clockS / look.PeriodS + Noise(xz / look.LaneM) * LaneSpread;
        var wave = MathF.Floor(cycles);
        var phase = cycles - wave;
        var size = Mix(SizeLow, 1f, Noise(xz / (look.LaneM * 1.7f) + new Vector2(wave * 7.31f, wave * 3.17f)));
        var crest = look.RunUpM * size;
        var rise = Smoothstep(0f, 1f, phase / look.RiseShare);
        var fall = Smoothstep(0f, 1f, (phase - look.RiseShare) / (1f - look.RiseShare));
        var swash = phase < look.RiseShare ? rise : 1f - fall;
        var damp = look.RunUpM * Mix(DampLow, 1f, Noise(xz / (look.LaneM * 2.3f) + new Vector2(19.7f, 4.3f)));
        return (Mix(-look.DrawBackM, crest, swash), damp);
    }

    /// <summary>The water shader's water depth at a place where the bed lies depthM under the sea's level: the wash makes it deeper or shallower.</summary>
    public static float WaterDepth(float depthM, Vector2 xz, float clockS, ShoreLook look) =>
        depthM + Level(xz, clockS, look).Level + (Noise(xz / look.BrushM) - 0.5f) * look.BrushAmpM;

    /// <summary>
    /// The land shader's gate: 1 on a sloping beach within the reach of the coast (a distance coastM from it, a slope of tan rise over
    /// run), fading to 0 past the reach and on flat ground. Everything the land does at the shore is multiplied by it.
    /// </summary>
    public static float Gate(float coastM, float slope, ShoreLook look) =>
        (1f - Smoothstep(0.7f * look.ReachM, look.ReachM, coastM)) * Smoothstep(look.FlatSlope, 2f * look.FlatSlope, slope);

    /// <summary>The slope (rise over run) of a surface whose normal points up by ny, as the land shader takes it.</summary>
    public static float SlopeOf(float ny)
    {
        var n = MathF.Max(ny, 0.05f);
        return MathF.Sqrt(MathF.Max(1f - n * n, 0f)) / n;
    }

    /// <summary>
    /// What the land shader does to ground relM above the sea's level at a place: how damp it is (0 to 1) and how deep the wash stands
    /// over it (metres, 0 where it is dry or below the sea's level, which the water shader paints). Zero past the reach and on the flats.
    /// </summary>
    public static (float Wet, float Film) OnLand(float relM, Vector2 xz, float clockS, float coastM, float slope, ShoreLook look)
    {
        if (!(relM > -look.DrawBackM - BandBelowM && relM < look.RunUpM + look.WetAboveM + BandAboveM)) return (0f, 0f);
        var gate = Gate(coastM, slope, look);
        if (!(gate > 0f)) return (0f, 0f);
        var (level, damp) = Level(xz, clockS, look);
        var brush = (Noise(xz / look.BrushM) - 0.5f) * look.BrushAmpM;
        var top = damp + look.WetAboveM;
        var wet = (1f - Smoothstep(top - look.WetFadeM, top, relM + brush)) * Smoothstep(-look.DrawBackM - 0.035f, -look.DrawBackM + 0.015f, relM) * gate;
        var depth = level + brush - relM;
        var film = relM >= 0f && depth > 0f ? depth : 0f;
        return (wet, gate > 0f ? film : 0f);
    }

    // ---------- the field ----------

    /// <summary>The shore's field: a texture of each place's distance to the coast (R8, metres over <see cref="ShoreLook.FieldRangeM"/>), and the square it covers.</summary>
    public sealed record Field(ImageTexture Texture, Vector4 Area, float RangeM, int Width, int Depth, float CellM)
    {
        private Image? _pixels;

        /// <summary>The distance to the coast this field holds at a point (metres, up to its range), read as the shader reads it (bilinear).</summary>
        public float DistanceAt(Vector2 xz)
        {
            var image = _pixels ??= Texture.GetImage();
            var u = (xz.X - Area.X) * Area.Z * Width - 0.5f;
            var v = (xz.Y - Area.Y) * Area.W * Depth - 0.5f;
            int x0 = (int)MathF.Floor(u), z0 = (int)MathF.Floor(v);
            float fx = u - x0, fz = v - z0;
            float At(int x, int z) => image.GetPixel(Math.Clamp(x, 0, Width - 1), Math.Clamp(z, 0, Depth - 1)).R;
            var value = Mix(Mix(At(x0, z0), At(x0 + 1, z0), fx), Mix(At(x0, z0 + 1), At(x0 + 1, z0 + 1), fx), fz);
            return value * RangeM;
        }
    }

    /// <summary>
    /// The field of a room's coast: every texel within the field's range of the coast's edges holds its distance to the nearest edge;
    /// the rest is the range (far). Cheap: only the texels near an edge are visited. Null for a room with no coast.
    /// </summary>
    public static Field? BuildField(RoomSea sea, ShoreLook look)
    {
        var coast = sea.Coast;
        if (coast.Length < 3) return null;
        var range = look.FieldRangeM;
        Vector2 low = coast[0], high = coast[0];
        foreach (var p in coast) { low = new Vector2(MathF.Min(low.X, p.X), MathF.Min(low.Y, p.Y)); high = new Vector2(MathF.Max(high.X, p.X), MathF.Max(high.Y, p.Y)); }
        low -= new Vector2(range + FieldCellM, range + FieldCellM);
        high += new Vector2(range + FieldCellM, range + FieldCellM);
        var cell = MathF.Max(FieldCellM, MathF.Max(high.X - low.X, high.Y - low.Y) / FieldMaxSide);
        int width = (int)MathF.Ceiling((high.X - low.X) / cell), depth = (int)MathF.Ceiling((high.Y - low.Y) / cell);
        var data = new byte[width * depth];
        Array.Fill(data, (byte)255);
        for (var i = 0; i < coast.Length; i++)
        {
            var a = coast[i];
            var b = coast[(i + 1) % coast.Length];
            var ab = b - a;
            var length2 = ab.LengthSquared();
            var from = new Vector2(MathF.Min(a.X, b.X) - range, MathF.Min(a.Y, b.Y) - range);
            var to = new Vector2(MathF.Max(a.X, b.X) + range, MathF.Max(a.Y, b.Y) + range);
            int x0 = Math.Max(0, (int)MathF.Floor((from.X - low.X) / cell)), x1 = Math.Min(width - 1, (int)MathF.Ceiling((to.X - low.X) / cell));
            int z0 = Math.Max(0, (int)MathF.Floor((from.Y - low.Y) / cell)), z1 = Math.Min(depth - 1, (int)MathF.Ceiling((to.Y - low.Y) / cell));
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    var centre = new Vector2(low.X + (x + 0.5f) * cell, low.Y + (z + 0.5f) * cell);
                    var t = length2 > 1e-12f ? Math.Clamp((centre - a).Dot(ab) / length2, 0f, 1f) : 0f;
                    var d = (a + ab * t).DistanceTo(centre);
                    var encoded = (byte)Math.Clamp((int)MathF.Round(d / range * 255f), 0, 255);
                    var index = z * width + x;
                    if (encoded < data[index]) data[index] = encoded;
                }
        }
        var image = Image.CreateFromData(width, depth, false, Image.Format.R8, data);
        return new Field(ImageTexture.CreateFromImage(image), new Vector4(low.X, low.Y, 1f / (width * cell), 1f / (depth * cell)), range, width, depth, cell);
    }

    private static readonly Dictionary<string, StringName> Names = new();

    /// <summary>A parameter's name as the engine wants it, made once (a name made on every call is a Godot value left for the finalizer).</summary>
    private static StringName N(string name)
    {
        lock (Names)
        {
            if (!Names.TryGetValue(name, out var made)) Names[name] = made = new StringName(name);
            return made;
        }
    }

    /// <summary>The sea's shallow colour the wash over the sand is painted in until a room's own is known (a light turquoise).</summary>
    public static readonly Color DefaultFilmColour = new(0.62f, 0.82f, 0.82f);

    /// <summary>
    /// Every parameter of the water shader's shore block, as the library gives it to every water material (off) and OpenSea turns it on
    /// for the sea's. One list, so the names the shader declares and the names set can never part (the look suite compares them).
    /// </summary>
    public static (StringName Name, Variant Value)[] WaterParameters(ShoreLook look, bool on, float holdS = FreeRunning, Field? field = null) => new (StringName, Variant)[]
    {
        (N("shore_on"), on), (N("shore_hold_s"), holdS), (N("shore_period_s"), look.PeriodS), (N("shore_run_up_m"), look.RunUpM),
        (N("shore_draw_back_m"), look.DrawBackM), (N("shore_lane_m"), look.LaneM), (N("shore_rise_share"), look.RiseShare),
        (N("shore_soft_m"), look.SoftM), (N("shore_foam_m"), look.FoamM), (N("shore_brush_m"), look.BrushM), (N("shore_brush_amp_m"), look.BrushAmpM),
        (N("shore_reach_m"), look.ReachM), (N("shore_field"), field?.Texture ?? (Texture2D)EmptyField()), (N("shore_field_area"), field?.Area ?? new Vector4(0f, 0f, 1f, 1f)),
        (N("shore_field_range_m"), field?.RangeM ?? look.FieldRangeM),
    };

    /// <summary>
    /// Every parameter of the land shader's shore block: the same numbers as the water's, the sea's level, the damp ground's look, the
    /// wash over the sand (the water's own least and most it hides and its clarity, in the sea's shallow colour) and the field.
    /// </summary>
    public static (StringName Name, Variant Value)[] LandParameters(ShoreLook look, bool on, float holdS, float seaLevelM, Color filmColour, Color foamColour, Field? field) => new (StringName, Variant)[]
    {
        (N("shore_on"), on), (N("shore_hold_s"), holdS), (N("shore_sea_level"), seaLevelM), (N("shore_period_s"), look.PeriodS), (N("shore_run_up_m"), look.RunUpM),
        (N("shore_draw_back_m"), look.DrawBackM), (N("shore_lane_m"), look.LaneM), (N("shore_rise_share"), look.RiseShare),
        (N("shore_foam_m"), look.FoamM), (N("shore_foam_w_m"), look.FoamWidthM), (N("shore_brush_m"), look.BrushM), (N("shore_brush_amp_m"), look.BrushAmpM),
        (N("shore_wet_above_m"), look.WetAboveM), (N("shore_wet_fade_m"), look.WetFadeM), (N("shore_reach_m"), look.ReachM), (N("shore_flat_slope"), look.FlatSlope),
        (N("shore_wet_tint"), look.WetTint), (N("shore_wet_gloss"), look.WetGloss),
        (N("shore_film_alpha_min"), WaterLook.Still.SurfaceAlpha), (N("shore_film_alpha_max"), WaterLook.Still.MaxAlpha), (N("shore_film_clarity_m"), SeaLook.Default.ClarityM),
        (N("shore_film_color"), filmColour), (N("shore_foam_color"), foamColour), (N("shore_sky_color"), WaterLook.Still.SkyColor), (N("shore_sky_rim"), look.SkyRim),
        (N("shore_field"), field?.Texture ?? (Texture2D)EmptyField()), (N("shore_field_area"), field?.Area ?? new Vector4(0f, 0f, 1f, 1f)),
        (N("shore_field_range_m"), field?.RangeM ?? look.FieldRangeM),
    };

    /// <summary>The 1 by 1 white field every land material carries until a room with a sea gives it a real one (never read while the shore is off).</summary>
    public static ImageTexture EmptyField()
    {
        if (_empty != null) return _empty;
        _empty = ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.R8, new byte[] { 255 }));
        return _empty;
    }

    private static ImageTexture? _empty;
}
