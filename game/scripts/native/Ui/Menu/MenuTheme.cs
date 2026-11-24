using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The title page's paper (Run 4, MENU-4): a naturalist's journal, open. Everything here is drawn in code and from the system's own
/// fonts (a handwriting face for headings, a book face for words; none is downloaded), so the page needs no art. MenuStyle is the
/// Choose page's teal; this is the journal's brown ink, cream paper and one red ribbon. Only the title page reads it until MENU-15.
/// Colours keep a reading contrast of at least 7 to 1 (ink on paper, cream on ribbon).
/// </summary>
public static class MenuTheme
{
    public static readonly Color Paper = new("e7d2a6");
    public static readonly Color PaperDeep = new("d8bf8a");
    public static readonly Color PaperLit = new("f0e0b8");
    public static readonly Color Ink = new("33261a");
    public static readonly Color InkSoft = new("5d472e");
    public static readonly Color Sepia = new("7a5d3b");
    public static readonly Color Ribbon = new("7a2f26");
    public static readonly Color RibbonHover = new("93402f");
    public static readonly Color RibbonDown = new("5f221b");
    public static readonly Color Cream = new("f7ecd0");
    public static readonly Color Gold = new("c9993a");
    public static readonly Color Warning = new("8a2f1f");

    /// <summary>Handwriting for headings and the date: the system's own, in the order a machine is likely to have one.</summary>
    public static readonly Font Hand = new SystemFont { FontNames = new[] { "Segoe Print", "Chilanka", "Comic Sans MS", "cursive" } };
    /// <summary>A book face for the words a child reads in a row.</summary>
    public static readonly Font Book = new SystemFont { FontNames = new[] { "Georgia", "Palatino Linotype", "Book Antiqua", "Cambria", "Times New Roman", "serif" } };
    /// <summary>The book face, leaned the way a naturalist's notes lean.</summary>
    public static readonly Font Lean = new FontVariation { BaseFont = Book, VariationTransform = new Transform2D(new Vector2(1, 0), new Vector2(-0.2f, 1), Vector2.Zero) };

    public static Label Text(string text, Font font, int size, Color colour, string name = "", bool wrap = false, int width = 0)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = false, TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming };
        if (name.Length > 0) label.Name = name;
        label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", size);
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
        button.AddThemeStyleboxOverride("disabled", Box(new Color("8d6f64"), InkSoft, 2, shadow: 0));
        Words(button, Cream, Cream, size);
    }

    /// <summary>The quiet button: a slip of paper stuck to the page, ink words.</summary>
    public static void AsSlip(Button button, int size = 26)
    {
        button.AddThemeStyleboxOverride("normal", Box(PaperLit, InkSoft));
        button.AddThemeStyleboxOverride("hover", Box(new Color("fbf0cc"), Ink));
        button.AddThemeStyleboxOverride("pressed", Box(PaperDeep, Ink, shadow: 1));
        button.AddThemeStyleboxOverride("hover_pressed", Box(PaperDeep, Ink, shadow: 1));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), Gold, 4, shadow: 0));
        button.AddThemeStyleboxOverride("disabled", Box(new Color("cdb98f"), Sepia, 1, shadow: 0));
        Words(button, Ink, Sepia, size);
    }

    /// <summary>A small link on the paper: underlined feel, no box until the pointer or the keyboard is on it.</summary>
    public static void AsLink(Button button, int size = 20)
    {
        button.AddThemeStyleboxOverride("normal", Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 0, 6, 8, 0));
        button.AddThemeStyleboxOverride("hover", Box(new Color(PaperLit, 0.75f), InkSoft, 1, 6, 8, 0));
        button.AddThemeStyleboxOverride("pressed", Box(PaperDeep, Ink, 1, 6, 8, 0));
        button.AddThemeStyleboxOverride("hover_pressed", Box(PaperDeep, Ink, 1, 6, 8, 0));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), Gold, 3, 6, 8, 0));
        Words(button, InkSoft, Sepia, size);
        button.AddThemeColorOverride("font_hover_color", Ink);
    }

    private static void Words(Button button, Color colour, Color disabled, int size)
    {
        button.AddThemeFontOverride("font", Hand);
        button.AddThemeFontSizeOverride("font_size", size);
        button.AddThemeColorOverride("font_color", colour);
        button.AddThemeColorOverride("font_hover_color", colour);
        button.AddThemeColorOverride("font_pressed_color", colour);
        button.AddThemeColorOverride("font_hover_pressed_color", colour);
        button.AddThemeColorOverride("font_focus_color", colour);
        button.AddThemeColorOverride("font_disabled_color", disabled);
    }
}
