using Godot;

namespace GarageGame.Native.Ui;

/// <summary>The menu's look: the HUD's deep teal and warm cream, cards that read as buttons, a selected card with a cream border. A first pass for the founder's eye, not settled art.</summary>
public static class MenuStyle
{
    public static readonly Color Page = new("0d1c1b");
    public static readonly Color Panel = new(0.075f, 0.13f, 0.13f, 0.96f);
    public static readonly Color Card = new("17302d");
    public static readonly Color CardHover = new("1f3f3b");
    public static readonly Color CardSelected = new("2a5650");
    public static readonly Color Ink = new("f0e6cf");
    public static readonly Color Muted = new("9fb7ad");
    public static readonly Color Warm = new("d7b765");
    public static readonly Color Refused = new("e0a68c");

    public static StyleBoxFlat Box(Color background, Color? border = null, int borderWidth = 0, int radius = 12, int margin = 10)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = margin, ContentMarginBottom = margin,
        };
        if (border is { } colour && borderWidth > 0)
        {
            box.BorderColor = colour;
            box.BorderWidthLeft = box.BorderWidthTop = box.BorderWidthRight = box.BorderWidthBottom = borderWidth;
        }
        return box;
    }

    /// <summary>A button drawn as a card: dark, lighter under the pointer or the keyboard focus, teal with a cream border when it is the chosen one, dim when refused.</summary>
    public static void AsCard(Button button, int radius = 12)
    {
        button.AddThemeStyleboxOverride("normal", Box(Card, new Color("2c4b47"), 1, radius));
        button.AddThemeStyleboxOverride("hover", Box(CardHover, new Color("3d6760"), 1, radius));
        button.AddThemeStyleboxOverride("pressed", Box(CardSelected, Ink, 3, radius));
        button.AddThemeStyleboxOverride("hover_pressed", Box(CardSelected, Ink, 3, radius));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), Warm, 2, radius));
        button.AddThemeStyleboxOverride("disabled", Box(new Color("111f1e"), new Color("203634"), 1, radius));
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_disabled_color", Muted);
    }

    /// <summary>The big button: a warm fill (Play, Continue).</summary>
    public static void Big(Button button)
    {
        button.AddThemeStyleboxOverride("normal", Box(new Color("3f7f75"), Ink, 2, 14, 14));
        button.AddThemeStyleboxOverride("hover", Box(new Color("4d958a"), Ink, 2, 14, 14));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("2f6a61"), Ink, 3, 14, 14));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), Warm, 3, 14, 14));
        button.AddThemeStyleboxOverride("disabled", Box(new Color("1b2d2b"), new Color("2c4b47"), 1, 14, 14));
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_disabled_color", Muted);
        button.AddThemeFontSizeOverride("font_size", 22);
    }

    public static Label Text(string text, int size = 15, Color? colour = null, string name = "")
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        if (name.Length > 0) label.Name = name;
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", colour ?? Ink);
        return label;
    }

    /// <summary>
    /// A label that is never trimmed or clipped: Godot gives a label with an overrun behaviour no minimum width at all, so in a row it
    /// can be squeezed to nothing by its neighbours (the first row's heading was).
    /// </summary>
    public static Label Unclipped(Label label)
    {
        label.ClipText = false;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        return label;
    }

    /// <summary>Every control under this one lets the mouse through to the card (a button with children reads the click itself).</summary>
    public static void IgnoreMouse(Control control)
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in control.GetChildren())
            if (child is Control inner) IgnoreMouse(inner);
    }
}
