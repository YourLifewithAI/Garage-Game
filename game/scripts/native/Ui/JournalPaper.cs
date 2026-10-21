using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The open book's two pages, drawn at any resolution from nothing but code: a worn leather cover with stitching, layered deckled
/// paper, foxing and old stains, a shaded stitched gutter, a light botanical drawing in the margin and the ring of an old cup.
/// No external art or photos (Codex's journal book, 12 October; the founder's look).
/// </summary>
public partial class JournalPaper : Control
{
    /// <summary>Where the gutter falls across the spread, as a share of its width (the left page is the narrower).</summary>
    public const float SpineShare = 0.465f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w < 1 || h < 1) return;
        var spine = w * SpineShare;
        DrawRect(new Rect2(5, 8, w, h), new Color(0, 0, 0, 0.5f));
        var cover = new StyleBoxFlat
        {
            BgColor = new Color("503325"), BorderColor = new Color("2b1d18"),
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
        };
        DrawStyleBox(cover, new Rect2(Vector2.Zero, Size));
        // The cover's stitching, all round.
        var thread = new Color("ae895a");
        for (var x = 15f; x < w - 15; x += 9)
        {
            DrawLine(new Vector2(x, 9), new Vector2(x + 4, 9), thread);
            DrawLine(new Vector2(x, h - 9), new Vector2(x + 4, h - 9), thread);
        }
        for (var y = 18f; y < h - 18; y += 9)
        {
            DrawLine(new Vector2(9, y), new Vector2(9, y + 4), thread);
            DrawLine(new Vector2(w - 9, y), new Vector2(w - 9, y + 4), thread);
        }
        // Uneven deckled page edges, layered over the worn cover.
        for (var layer = 4; layer >= 0; layer--)
            DrawRect(new Rect2(20 + layer, 17 + layer * 2, w - 40, h - 35), new Color(layer == 0 ? "e7d2a6" : "b9a074"));
        // Foxing: fine specks, then a few faint stains.
        var rng = new RandomNumberGenerator { Seed = 271828 };
        for (var i = 0; i < 2100; i++)
        {
            var speck = new Vector2(rng.RandfRange(23, w - 23), rng.RandfRange(20, h - 20));
            DrawCircle(speck, rng.RandfRange(0.25f, 1.5f), new Color(0.3f, 0.17f, 0.05f, rng.RandfRange(0.015f, 0.065f)));
        }
        for (var i = 0; i < 36; i++)
        {
            var stain = new Vector2(rng.RandfRange(26, w - 26), rng.RandfRange(28, h - 28));
            for (var ring = 0; ring < 4; ring++)
                DrawCircle(stain + Vector2.One * ring, 5 + i % 13 + ring * 3, new Color(0.52f, 0.28f, 0.06f, 0.01f));
        }
        // The paper darkens a little towards its edges.
        for (var i = 0; i < 18; i++)
        {
            var alpha = (18 - i) * 0.004f;
            DrawRect(new Rect2(20 + i, 18 + i, w - 40 - i * 2, h - 36 - i * 2), new Color(0.36f, 0.19f, 0.04f, alpha), false, 1);
        }
        // The gutter is shaded in narrow bands, like paper turning into a stitched binding.
        for (var i = 26; i >= 0; i--)
            DrawLine(new Vector2(spine + i, 20), new Vector2(spine + i, h - 20), new Color(0.28f, 0.15f, 0.06f, (27 - i) / 140f), 1);
        DrawLine(new Vector2(spine, 20), new Vector2(spine, h - 20), new Color("846544"), 2);
        for (var y = 45f; y < h - 40; y += 68)
            DrawArc(new Vector2(spine, y), 7, 0.4f, 2.6f, 8, new Color("7b5838"), 1.2f, true);
        // A restrained botanical marginal drawing, deliberately lighter than the writing.
        var stem = new Vector2(w * 0.28f, h * 0.78f);
        var ink = new Color(0.36f, 0.32f, 0.16f, 0.28f);
        DrawPolyline(new[] { stem, stem + new Vector2(-18, -24), stem + new Vector2(-24, -54), stem + new Vector2(-10, -85) }, ink, 1.4f, true);
        for (var i = 0; i < 4; i++)
        {
            var at = stem + new Vector2(-20, -15 - i * 17);
            var side = i % 2 == 0 ? -1 : 1;
            DrawPolyline(new[] { at, at + new Vector2(side * 30, -20), at + new Vector2(side * 20, 1), at }, ink, 1, true);
        }
        // A faded ring left by an old cup near the outer edge.
        DrawArc(new Vector2(w - 50, h - 90), 33, 1.6f, 4.5f, 48, new Color(0.40f, 0.23f, 0.08f, 0.1f), 3, true);
    }
}
