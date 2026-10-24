using Godot;
using System;

namespace GarageGame.Native.Look;

/// <summary>Everything the colour grade depends on at one moment.</summary>
public sealed record GradeParams(
    float Saturation, float Contrast, float Warmth, Color ShadowTint, Color HighlightTint,
    Color SeasonTint, float Night, GradeTuning Tuning)
{
    /// <summary>A grade that changes nothing: grey tints carry no colour.</summary>
    public static GradeParams Identity { get; } = new(1f, 1f, 0f, new Color(0.5f, 0.5f, 0.5f), new Color(0.5f, 0.5f, 0.5f), new Color(0.5f, 0.5f, 0.5f), 0f, LookTuning.Default.Grade);

    /// <summary>The palette grade for a preset at a moment: palette times season, plus night (less of it while the lamps are on).</summary>
    public static GradeParams For(StylePreset preset, LookMoment moment, bool lampsOn = false) => new(
        preset.Saturation * moment.SeasonSaturation,
        preset.Contrast,
        Mathf.Clamp(preset.Warmth + moment.SeasonWarmth, -1f, 1f),
        preset.PaletteShadowTint,
        preset.HighlightTint,
        moment.SeasonTint,
        LookClock.NightAmount(preset, moment.Daylight) * (lampsOn ? preset.Tuning.Grade.NightWithLamps : 1f),
        preset.Tuning.Grade);

    /// <summary>
    /// The same grade with every input rounded to steps finer than one 8-bit LUT level, so moments that grade
    /// identically share one cached LUT (the hour moves Night slowly; the day moves the season slowly).
    /// </summary>
    public GradeParams Quantized() => this with
    {
        Saturation = Q(Saturation, 0.002f), Contrast = Q(Contrast, 0.002f), Warmth = Q(Warmth, 0.002f), Night = Q(Night, 0.01f),
        ShadowTint = QC(ShadowTint), HighlightTint = QC(HighlightTint), SeasonTint = QC(SeasonTint),
    };

    private static float Q(float value, float step) => MathF.Round(value / step) * step;

    private static Color QC(Color c) => new(Q(c.R, 1f / 255f), Q(c.G, 1f / 255f), Q(c.B, 1f / 255f));
}

/// <summary>
/// The display-space colour grade the look applies after tone mapping, baked into a 3D lookup table for
/// Environment.adjustment_color_correction. Order: warmth, season tint, split toning (cool shadows, warm
/// highlights), saturation, night, contrast. Tints act through their colour only, so grey tints and a
/// neutral palette leave the image unchanged. The strengths come from the preset (x_look_grade).
/// </summary>
public static class ColorGrade
{
    public static float Luma(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    private static Color Chroma(Color tint)
    {
        var luma = Luma(tint);
        return new Color(tint.R - luma, tint.G - luma, tint.B - luma);
    }

    public static Color Apply(GradeParams g, Color input)
    {
        var t = g.Tuning;
        var c = input;
        // White balance: warmth lifts red, trims blue.
        c = new Color(c.R * (1f + t.WarmthRgb.X * g.Warmth), c.G * (1f + t.WarmthRgb.Y * g.Warmth), c.B * (1f + t.WarmthRgb.Z * g.Warmth));
        // Season tint as a gentle colour cast, strongest in the mid-tones and faded out of the shadows below
        // season_tint_shadow_fade, so shade stays cool against a warm season (warm key against cool shadow).
        var luma = Luma(c);
        var mid = 4f * luma * (1f - luma);
        var lit = t.SeasonTintShadowFade > 0f ? Mathf.SmoothStep(0f, t.SeasonTintShadowFade, luma) : 1f;
        c += Chroma(g.SeasonTint) * (t.SeasonTint * mid * lit);
        // Split toning: colour the shadows and the highlights.
        luma = Mathf.Clamp(Luma(c), 0f, 1f);
        var shade = (1f - luma) * (1f - luma);
        var light = luma * luma;
        c += Chroma(g.ShadowTint) * (t.ShadowTone * shade) + Chroma(g.HighlightTint) * (t.HighlightTone * light);
        // Saturation around luma.
        luma = Luma(c);
        c = new Color(luma, luma, luma).Lerp(c, g.Saturation);
        // Night: bluer, less saturated, deeper shadows.
        if (g.Night > 0f)
        {
            luma = Mathf.Clamp(Luma(c), 0f, 1f);
            c = new Color(luma, luma, luma).Lerp(c, 1f - t.NightDesaturate * g.Night);
            c = new Color(c.R * (1f + t.NightTintRgb.X * g.Night), c.G * (1f + t.NightTintRgb.Y * g.Night), c.B * (1f + t.NightTintRgb.Z * g.Night));
            var deepen = 1f - t.NightDeepen * g.Night * (1f - luma);
            c = new Color(c.R * deepen, c.G * deepen, c.B * deepen);
        }
        // Contrast around mid grey.
        c = new Color(0.5f + (c.R - 0.5f) * g.Contrast, 0.5f + (c.G - 0.5f) * g.Contrast, 0.5f + (c.B - 0.5f) * g.Contrast);
        return new Color(Mathf.Clamp(c.R, 0f, 1f), Mathf.Clamp(c.G, 0f, 1f), Mathf.Clamp(c.B, 0f, 1f));
    }

    /// <summary>
    /// RGB8 LUT bytes: slice z is blue, row y is green, column x is red. Godot samples the LUT directly with
    /// the colour as texture coordinate, so texel i holds the grade of input (i + 0.5) / size, which makes
    /// linear filtering reproduce the grade between texels. Pure and thread-safe.
    /// </summary>
    public static byte[] LutBytes(GradeParams g)
    {
        var size = g.Tuning.LutSize;
        var bytes = new byte[size * size * size * 3];
        var index = 0;
        for (var z = 0; z < size; z++)
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var graded = Apply(g, new Color((x + 0.5f) / size, (y + 0.5f) / size, (z + 0.5f) / size));
                    bytes[index++] = ToByte(graded.R);
                    bytes[index++] = ToByte(graded.G);
                    bytes[index++] = ToByte(graded.B);
                }
        return bytes;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    public static ImageTexture3D LutTexture(GradeParams g) => LutTexture(LutBytes(g), g.Tuning.LutSize);

    /// <summary>Build the 3D texture from LUT bytes. Main thread only.</summary>
    public static ImageTexture3D LutTexture(byte[] bytes, int size)
    {
        var slices = new Godot.Collections.Array<Image>();
        var sliceBytes = size * size * 3;
        for (var z = 0; z < size; z++)
            slices.Add(Image.CreateFromData(size, size, false, Image.Format.Rgb8, bytes.AsSpan(z * sliceBytes, sliceBytes).ToArray()));
        var texture = new ImageTexture3D();
        var error = texture.Create(Image.Format.Rgb8, size, size, size, false, slices);
        if (error != Error.Ok) throw new InvalidOperationException($"could not build the colour grade LUT ({error})");
        return texture;
    }
}
