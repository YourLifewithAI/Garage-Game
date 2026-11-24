using Godot;
using System;

namespace GarageGame.Native.Ui;

/// <summary>
/// The title page's paper (Run 4, MENU-4): a naturalist's journal, open. Everything here is drawn in code and from the system's own
/// fonts (a handwriting face for headings, a book face for words; none is downloaded), so the page needs no art. MenuStyle is the
/// Choose page's teal; this is the journal's brown ink, cream paper and one red ribbon. Only the title page reads it until MENU-15.
/// <para>
/// How well the words read is counted, not claimed (Contrast is the reading contrast of two colours, 1 to 21; the suite counts it
/// at every hour). By day: ink on paper 9.9, the soft ink 5.9, sepia 5.0, the warning 5.7, cream on the ribbon 7.9 and on a shut
/// ribbon 5.4. Under the hour's wash (TitleSky.Wash) the wash is thinned until ink still reads at InkLeast, and every softer ink on
/// the paper is darkened until it reads at SoftLeast (Legible), so the page reads at night as it does by day.
/// </para>
/// </summary>
public static class MenuTheme
{
    public static readonly Color Paper = new("e7d2a6");
    public static readonly Color PaperDeep = new("d8bf8a");
    public static readonly Color PaperLit = new("f0e0b8");
    public static readonly Color Ink = new("33261a");
    public static readonly Color InkSoft = new("5d472e");
    public static readonly Color Sepia = new("6b5032");
    public static readonly Color Ribbon = new("7a2f26");
    public static readonly Color RibbonHover = new("93402f");
    public static readonly Color RibbonDown = new("5f221b");
    /// <summary>A ribbon that cannot be pressed yet: the red gone out of it, its words still plain.</summary>
    public static readonly Color RibbonShut = new("755a51");
    public static readonly Color Cream = new("f7ecd0");
    public static readonly Color Gold = new("c9993a");
    public static readonly Color Warning = new("8a2f1f");

    /// <summary>The least reading contrast of the page's main ink on its paper, at any hour.</summary>
    public const float InkLeast = 7f;
    /// <summary>The least reading contrast of every other word on the paper (the soft ink, sepia, the warning), at any hour.</summary>
    public const float SoftLeast = 4.5f;
    /// <summary>No word a child reads is smaller than this.</summary>
    public const int SmallestText = 18;

    private static readonly Font SystemHand = new SystemFont { FontNames = new[] { "Segoe Print", "Chilanka", "Comic Sans MS", "cursive" } };
    private static readonly Font SystemBook = new SystemFont { FontNames = new[] { "Georgia", "Palatino Linotype", "Book Antiqua", "Cambria", "Times New Roman", "serif" } };

    /// <summary>Handwriting for headings and the date: the system's own, in the order a machine is likely to have one.</summary>
    public static Font Hand { get; private set; } = SystemHand;
    /// <summary>A book face for the words a child reads in a row.</summary>
    public static Font Book { get; private set; } = SystemBook;
    /// <summary>The book face, leaned the way a naturalist's notes lean.</summary>
    public static Font Lean { get; private set; } = Leaned(SystemBook);

    private static Font Leaned(Font book) => new FontVariation { BaseFont = book, VariationTransform = new Transform2D(new Vector2(1, 0), new Vector2(-0.2f, 1), Vector2.Zero) };

    /// <summary>
    /// Other faces for the pages made after this call (null: the system's own again). A machine has the faces it has: the page is
    /// laid out by measuring, so a test gives it wider and taller ones and sees that it still fits.
    /// </summary>
    public static void UseFonts(Font? hand = null, Font? book = null)
    {
        Hand = hand ?? SystemHand;
        Book = book ?? SystemBook;
        Lean = Leaned(Book);
    }

    // ---- how well a colour reads on another ----

    /// <summary>How bright a colour is to the eye, 0 to 1 (the relative luminance of the web's contrast rule).</summary>
    public static float Luminance(Color colour)
    {
        static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Linear(colour.R) + 0.7152f * Linear(colour.G) + 0.0722f * Linear(colour.B);
    }

    /// <summary>The reading contrast of two colours, 1 (the same) to 21 (black on white): 4.5 reads, 7 reads well.</summary>
    public static float Contrast(Color a, Color b)
    {
        var (light, dark) = (MathF.Max(Luminance(a), Luminance(b)), MathF.Min(Luminance(a), Luminance(b)));
        return (light + 0.05f) / (dark + 0.05f);
    }

    /// <summary>A wash laid over a ground: the colour seen.</summary>
    public static Color Over(Color ground, Color wash) => new(
        ground.R + (wash.R - ground.R) * wash.A, ground.G + (wash.G - ground.G) * wash.A, ground.B + (wash.B - ground.B) * wash.A, 1f);

    /// <summary>The colour as it is where it already reads at this contrast on this ground; else darkened, as little as will do, until it does (black where nothing will).</summary>
    public static Color Legible(Color colour, Color ground, float least)
    {
        if (Contrast(colour, ground) >= least) return colour;
        var (clear, dark) = (0f, 1f);
        if (Contrast(colour.Darkened(1f), ground) < least) return new Color(0, 0, 0, colour.A);
        for (var step = 0; step < 12; step++)
        {
            var middle = (clear + dark) / 2f;
            if (Contrast(colour.Darkened(middle), ground) >= least) dark = middle; else clear = middle;
        }
        return colour.Darkened(dark);
    }

    /// <summary>The wash thinned, as little as will do, until this ink still reads at this contrast on the washed ground.</summary>
    public static Color Thinned(Color wash, Color ground, Color ink, float least)
    {
        if (Contrast(ink, Over(ground, wash)) >= least) return wash;
        var (thin, thick) = (0f, wash.A);
        for (var step = 0; step < 12; step++)
        {
            var middle = (thin + thick) / 2f;
            if (Contrast(ink, Over(ground, new Color(wash, middle))) >= least) thin = middle; else thick = middle;
        }
        return new Color(wash, thin);
    }

    // ---- the page's parts ----

    public static Label Text(string text, Font font, int size, Color colour, string name = "", bool wrap = false, int width = 0)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = false, TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming };
        if (name.Length > 0) label.Name = name;
        label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", Math.Max(size, SmallestText));
        label.AddThemeColorOverride("font_color", colour);
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(width, 0);
        }
        return label;
    }

    public static StyleBoxFlat Box(Color fill, Color edge, int edgeWidth = 2, int radius = 7, int margin = 10, int shadow = 5)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill, BorderColor = edge,
            BorderWidthLeft = edgeWidth, BorderWidthTop = edgeWidth, BorderWidthRight = edgeWidth, BorderWidthBottom = edgeWidth,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = margin, ContentMarginBottom = margin,
        };
        if (shadow > 0)
        {
            box.ShadowColor = new Color(0.12f, 0.07f, 0.02f, 0.38f);
            box.ShadowSize = shadow;
            box.ShadowOffset = new Vector2(2, 3);
        }
        return box;
    }

    /// <summary>The leading button: a red ribbon laid across the page, cream words.</summary>
    public static void AsRibbon(Button button, int size = 28)
    {
        button.AddThemeStyleboxOverride("normal", Box(Ribbon, Ink));
        button.AddThemeStyleboxOverride("hover", Box(RibbonHover, Ink));
        button.AddThemeStyleboxOverride("pressed", Box(RibbonDown, Ink, shadow: 1));
        button.AddThemeStyleboxOverride("hover_pressed", Box(RibbonDown, Ink, shadow: 1));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), Gold, 4, shadow: 0));
        button.AddThemeStyleboxOverride("disabled", Box(RibbonShut, InkSoft, 2, shadow: 0));
        Words(button, Cream, Cream, size);
    }

    /// <summary>The quiet button: a slip of paper stuck to the page, ink words.</summary>
    public static void AsSlip(Button button, int size = 26)
    {
        button.AddThemeStyleboxOverride("normal", Box(PaperLit, InkSoft));
        button.AddThemeStyleboxOverride("hover", Box(new Color("fbf0cc"), Ink));
        button.AddThemeStyleboxOverride("pressed", Box(PaperDeep, Ink, shadow: 1));
        button.AddThemeStyleboxOverride("hover_pressed", Box(PaperDeep, Ink, shadow: 1));
        // Gold is lost on pale paper: the keyboard's mark on a slip is a heavier ink line.
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), Ink, 4, shadow: 0));
        button.AddThemeStyleboxOverride("disabled", Box(PaperDeep, Sepia, 1, shadow: 0));
        Words(button, Ink, InkSoft, size);
    }

    /// <summary>A small link on the paper: no box until the pointer or the keyboard is on it. Its ink is the page's to set (it lies on the washed paper).</summary>
    public static void AsLink(Button button, int size = 20)
    {
        button.AddThemeStyleboxOverride("normal", Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 0, 6, 8, 0));
        button.AddThemeStyleboxOverride("hover", Box(new Color(PaperLit, 0.75f), InkSoft, 1, 6, 8, 0));
        button.AddThemeStyleboxOverride("pressed", Box(PaperDeep, Ink, 1, 6, 8, 0));
        button.AddThemeStyleboxOverride("hover_pressed", Box(PaperDeep, Ink, 1, 6, 8, 0));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), Ink, 3, 6, 8, 0));
        Words(button, InkSoft, InkSoft, size);
        button.AddThemeColorOverride("font_hover_color", Ink);
    }

    /// <summary>A link's resting ink (the hover and the pressed ink stay the main ink).</summary>
    public static void LinkInk(Button button, Color colour)
    {
        button.AddThemeColorOverride("font_color", colour);
        button.AddThemeColorOverride("font_focus_color", colour);
        button.AddThemeColorOverride("font_disabled_color", colour);
    }

    private static void Words(Button button, Color colour, Color disabled, int size)
    {
        button.AddThemeFontOverride("font", Hand);
        button.AddThemeFontSizeOverride("font_size", Math.Max(size, SmallestText));
        button.AddThemeColorOverride("font_color", colour);
        button.AddThemeColorOverride("font_hover_color", colour);
        button.AddThemeColorOverride("font_pressed_color", colour);
        button.AddThemeColorOverride("font_hover_pressed_color", colour);
        button.AddThemeColorOverride("font_focus_color", colour);
        button.AddThemeColorOverride("font_disabled_color", disabled);
    }
}
