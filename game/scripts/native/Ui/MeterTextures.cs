using System;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The meters' small pictures, drawn once in software the first time they are needed and kept: each heart (nought to four quarters
/// in red, and the full grey one Creative shows) and the two kinds of pip (a pip you have and a spent one; the shadow pool tints the same two grey). A meter then
/// draws each of its parts as one picture instead of a handful of polygons, which is what keeps them inside the frame's budget
/// (docs/runs/RUN-2-MECHANICS.md section 9): the cost of a draw is mostly the calls.
/// </summary>
public static class MeterTextures
{
    /// <summary>A heart's picture is this many pixels square: the heart (HeartsMeter.HeartPx) and a pixel of margin for its outline.</summary>
    public const int HeartPx = 28;
    public const int PipPx = 32;

    private static Texture2D[]? _hearts;
    private static Texture2D? _greyHeart;
    private static Texture2D? _pipFull, _pipSpent;

    // The hearts sit on the status panel's dark teal: an empty one is a dark red with a muted red rim, so it still reads as a place for a quarter.
    private static readonly Color RedBack = new(0.30f, 0.09f, 0.09f, 0.85f);
    private static readonly Color RedFill = new("e24d3d");
    private static readonly Color RedEdge = new("a35248");
    private static readonly Color GreyBack = new(0.20f, 0.23f, 0.23f, 0.85f);
    private static readonly Color GreyFill = new("a8b0b0");
    private static readonly Color GreyEdge = new("7c8686");

    /// <summary>The red heart with this many quarters full (0 to 4).</summary>
    public static Texture2D Heart(int quarters)
    {
        _hearts ??= BuildHearts();
        return _hearts[Math.Clamp(quarters, 0, MeterMath.QuartersPerHeart)];
    }

    /// <summary>The grey, full heart of Creative.</summary>
    public static Texture2D GreyHeart => _greyHeart ??= Make(HeartPx, HeartPixels(MeterMath.QuartersPerHeart, grey: true));

    /// <summary>
    /// A heart's pixels (red, green, blue, alpha, row by row, HeartPx square) with this many quarters filled, clockwise from the top
    /// left: the picture a texture is made of, also what the suite reads to hold each quarter to its quarter of the heart.
    /// </summary>
    public static byte[] HeartPixels(int quarters, bool grey = false) =>
        RenderHeart(quarters, grey ? GreyBack : RedBack, grey ? GreyFill : RedFill, grey ? GreyEdge : RedEdge);

    /// <summary>A pip you have: pale with a dark rim, tinted by the Gubble's colour where it is drawn.</summary>
    public static Texture2D PipFull => _pipFull ??= RenderPip(filled: true, ring: false);

    /// <summary>A spent pip: a ring, tinted.</summary>
    public static Texture2D PipSpent => _pipSpent ??= RenderPip(filled: false, ring: true);

    private static Texture2D[] BuildHearts()
    {
        var made = new Texture2D[MeterMath.QuartersPerHeart + 1];
        for (var quarters = 0; quarters <= MeterMath.QuartersPerHeart; quarters++) made[quarters] = Make(HeartPx, HeartPixels(quarters));
        return made;
    }

    // ---- a heart, by sampling its outline ----

    private static byte[] RenderHeart(int quarters, Color back, Color fill, Color edge)
    {
        var outline = MeterMath.HeartOutline();
        var scale = HeartPx - 2f;
        const int samples = 3;
        var pixels = new byte[HeartPx * HeartPx * 4];
        for (var y = 0; y < HeartPx; y++)
            for (var x = 0; x < HeartPx; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var u = (x + (sx + 0.5f) / samples - 1f) / scale;
                        var v = (y + (sy + 0.5f) / samples - 1f) / scale;
                        if (!Inside(outline, u, v)) continue;
                        var quarter = u < 0.5f ? (v < 0.5f ? 0 : 3) : (v < 0.5f ? 1 : 2);
                        var colour = quarter < quarters ? fill : back;
                        r += colour.R * colour.A; g += colour.G * colour.A; b += colour.B * colour.A; a += colour.A;
                    }
                var cover = 1f / (samples * samples);
                (r, g, b, a) = (r * cover, g * cover, b * cover, a * cover);
                // The outline: dark where the pixel's middle is within a pixel and a half of the heart's edge.
                var distance = EdgeDistance(outline, (x + 0.5f - 1f) / scale, (y + 0.5f - 1f) / scale) * scale;
                var stroke = Mathf.Clamp(1.5f - distance, 0f, 1f) * edge.A;
                r = edge.R * stroke + r * (1 - stroke); g = edge.G * stroke + g * (1 - stroke); b = edge.B * stroke + b * (1 - stroke);
                a = stroke + a * (1 - stroke);
                Store(pixels, (y * HeartPx + x) * 4, r, g, b, a);
            }
        return pixels;
    }

    /// <summary>Whether a point of the unit square is inside the polygon (even-odd).</summary>
    private static bool Inside(Vector2[] polygon, float u, float v)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > v) != (b.Y > v) && u < (b.X - a.X) * (v - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The distance from a point to the polygon's boundary, in the polygon's own units.</summary>
    private static float EdgeDistance(Vector2[] polygon, float u, float v)
    {
        var best = float.MaxValue;
        var point = new Vector2(u, v);
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[j], polygon[i]);
            var along = b - a;
            var t = Mathf.Clamp((point - a).Dot(along) / Math.Max(1e-9f, along.LengthSquared()), 0f, 1f);
            best = Math.Min(best, point.DistanceTo(a + along * t));
        }
        return best;
    }

    // ---- pips ----

    private static Texture2D RenderPip(bool filled, bool ring)
    {
        var pixels = new byte[PipPx * PipPx * 4];
        var middle = PipPx / 2f;
        var radius = PipPx / 2f - 3f;
        for (var y = 0; y < PipPx; y++)
            for (var x = 0; x < PipPx; x++)
            {
                var d = new Vector2(x + 0.5f - middle, y + 0.5f - middle).Length();
                float r = 0, g = 0, b = 0, a = 0;
                if (filled)
                {
                    // A dark halo just outside, then the pale body.
                    var halo = Mathf.Clamp(radius + 2.5f - d, 0f, 1f) * 0.55f;
                    var body = Mathf.Clamp(radius + 0.5f - d, 0f, 1f);
                    (r, g, b) = (0.05f * halo * (1 - body) + 0.97f * body, 0.08f * halo * (1 - body) + 0.97f * body, 0.08f * halo * (1 - body) + 0.97f * body);
                    a = body + halo * (1 - body);
                }
                else if (ring)
                {
                    var stroke = Mathf.Clamp(1.2f - Mathf.Abs(d - (radius - 1.5f)) * 1.0f, 0f, 1f) * 0.9f;
                    (r, g, b, a) = (stroke, stroke, stroke, stroke);
                }
                Store(pixels, (y * PipPx + x) * 4, r, g, b, a);
            }
        return Make(PipPx, pixels);
    }

    /// <summary>Premultiplied colour written as the straight bytes an image holds.</summary>
    private static void Store(byte[] pixels, int at, float r, float g, float b, float a)
    {
        a = Mathf.Clamp(a, 0f, 1f);
        if (a <= 1e-4f) return;
        pixels[at] = (byte)Math.Round(Mathf.Clamp(r / a, 0f, 1f) * 255);
        pixels[at + 1] = (byte)Math.Round(Mathf.Clamp(g / a, 0f, 1f) * 255);
        pixels[at + 2] = (byte)Math.Round(Mathf.Clamp(b / a, 0f, 1f) * 255);
        pixels[at + 3] = (byte)Math.Round(a * 255);
    }

    private static Texture2D Make(int size, byte[] pixels)
    {
        using var image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The HUD leaves the tree: the pictures go with it (the next HUD draws them again), so nothing outlives the engine's own resources.</summary>
    public static void Release()
    {
        if (_hearts != null) foreach (var heart in _hearts) heart.Dispose();
        _hearts = null;
        foreach (var texture in new[] { _greyHeart, _pipFull, _pipSpent }) texture?.Dispose();
        (_greyHeart, _pipFull, _pipSpent) = (null, null, null);
    }
}
