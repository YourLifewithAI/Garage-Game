using System;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The closed journal in its dock at the lower right: the same leather cover the book shows in flight (DrawCover), with its key
/// beneath it, named from the input map. It never takes the keyboard focus, so Space and Enter (ui_accept) can never press it:
/// only a click or the journal's own key opens the book.
/// It can pulse (decision 53 of docs/game/MECHANICS.md: the journal pulses when five Fractals are in hand and a spend is
/// possible): the book breathes a few per cent larger and a warm halo behind it swells and fades. The HUD says when (SetPulsing)
/// and calls Animate each frame while it pulses; the cover and the key beneath it are not drawn again for it (a halo drawn once
/// and a scale and a fade set each frame), so a pulse costs a few property writes and allocates nothing.
/// </summary>
public partial class JournalBookIcon : Button
{
    /// <summary>One breath of the pulse, in seconds (provisional: slow enough to be calm, quick enough to be seen from the corner of the eye).</summary>
    public const float PulsePeriodS = 1.6f;
    /// <summary>How much larger the book is at the top of a breath, as a share of its size (provisional).</summary>
    public const float PulseGrowth = 0.07f;
    /// <summary>The halo's faintest and brightest at the bottom and the top of a breath (provisional).</summary>
    public const float HaloLow = 0.25f, HaloHigh = 1f;

    /// <summary>The journal key's name as the player's controls say it now ("J").</summary>
    public Func<string> KeyLabel { get; set; } = () => "";
    /// <summary>Whether the book is pulsing now.</summary>
    public bool Pulsing { get; private set; }
    /// <summary>Where in its breath the pulse is: 0 at the bottom, 1 at the top (0 at rest).</summary>
    public float PulseLevel { get; private set; }
    /// <summary>The halo behind the book (shown only while it pulses).</summary>
    public Control? Halo => _halo;
    private string _shownKey = "";
    private double _pulseClock;
    private JournalHalo? _halo;

    public override void _Ready()
    {
        Name = "JournalBookIcon";
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        MouseEntered += QueueRedraw;
        MouseExited += QueueRedraw;
        // Behind the cover (a child draws over its parent, so the halo is drawn under it), clicks pass through it.
        _halo = new JournalHalo { Name = "JournalPulseHalo", Visible = false, MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true };
        AddChild(_halo);
        _halo.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ShowKey();
    }

    /// <summary>The book breathes from its middle, wherever the dock's size ends up.</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationResized) PivotOffset = Size * 0.5f;
    }

    /// <summary>Start or stop the pulse. Stopped, the book is at rest at once: its size, no halo.</summary>
    public void SetPulsing(bool on)
    {
        if (on == Pulsing) return;
        Pulsing = on;
        _pulseClock = 0;
        PulseLevel = 0;
        Scale = Vector2.One;
        if (_halo != null) _halo.Visible = on;
        if (on)
        {
            PivotOffset = Size * 0.5f;
            Animate(0);
        }
    }

    /// <summary>One frame of the pulse: a breath of 1.6 s, the book and its halo set from where in it the pulse is. Nothing happens at rest.</summary>
    public void Animate(double delta)
    {
        if (!Pulsing) return;
        _pulseClock += delta;
        var phase = (float)(_pulseClock % PulsePeriodS) / PulsePeriodS;
        PulseLevel = 0.5f - 0.5f * Mathf.Cos(phase * Mathf.Tau);
        Scale = new Vector2(1f + PulseGrowth * PulseLevel, 1f + PulseGrowth * PulseLevel);
        if (_halo != null) _halo.Modulate = new Color(1f, 1f, 1f, HaloLow + (HaloHigh - HaloLow) * PulseLevel);
    }

    public override void _Process(double delta)
    {
        // A remapped key shows at once, as the rest of the HUD's words do.
        if (KeyLabel() != _shownKey) ShowKey();
    }

    private void ShowKey()
    {
        _shownKey = KeyLabel();
        TooltipText = _shownKey.Length > 0 ? $"Open the journal · {_shownKey}" : "Open the journal";
        QueueRedraw();
    }

    /// <summary>Where the cover sits on screen: the book's flight starts and ends here.</summary>
    public Rect2 CoverRect => new(GetGlobalRect().Position + new Vector2(13, 3), new Vector2(Size.X - 26, Size.Y - 24));

    public override void _Draw()
    {
        if (IsHovered()) DrawCircle(Size * 0.5f, Size.X * 0.47f, new Color(0.9f, 0.72f, 0.4f, 0.16f));
        DrawCover(this, new Rect2(new Vector2(13, 3), new Vector2(Size.X - 26, Size.Y - 24)), 1);
        var words = _shownKey.Length > 0 ? $"Journal · {_shownKey}" : "Journal";
        DrawString(ThemeDB.FallbackFont, new Vector2(9, Size.Y - 2), words, HorizontalAlignment.Center, Size.X - 18, 13, new Color("f0e6cf"));
    }

    /// <summary>
    /// The leather cover, drawn into a rectangle of any size: its page block, worn leather with a bound spine and its bands,
    /// stitching, gilt corners, the botanical gold emblem, the title and a ribbon. light dims the leather as the cover turns.
    /// </summary>
    public static void DrawCover(Control canvas, Rect2 rect, float light, bool reverse = false)
    {
        var origin = rect.Position;
        var size = rect.Size;
        if (size.X < 0.5f || size.Y < 1) return;
        canvas.DrawSetTransform(origin, 0, size / new Vector2(100, 140));
        canvas.DrawStyleBox(new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.36f), CornerRadiusTopLeft = 7, CornerRadiusTopRight = 9, CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 9 }, new Rect2(4, 5, 99, 140));
        canvas.DrawStyleBox(new StyleBoxFlat { BgColor = new Color("ac9165"), BorderColor = new Color("402a20"), BorderWidthLeft = 2, BorderWidthBottom = 3, CornerRadiusTopRight = 6, CornerRadiusBottomRight = 6 }, new Rect2(5, 5, 94, 133));
        for (var y = 12; y < 135; y += 3)
            canvas.DrawLine(new Vector2(93, y), new Vector2(99, y), new Color("d7bf92"), 0.7f);
        var leather = new Color(reverse ? "654734" : "563525");
        leather.R *= light;
        leather.G *= light;
        leather.B *= light;
        canvas.DrawStyleBox(new StyleBoxFlat
        {
            BgColor = leather, BorderColor = new Color("2c1c17"), BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 5, CornerRadiusBottomLeft = 5, CornerRadiusTopRight = 9, CornerRadiusBottomRight = 9,
        }, new Rect2(0, 0, 95, 135));
        // The spine and its raised bands.
        canvas.DrawRect(new Rect2(4, 2, 9, 130), new Color("352219"));
        for (var y = 20; y < 128; y += 28)
            canvas.DrawRect(new Rect2(3, y, 12, 4), new Color("926341"));
        // Scuffs in the leather.
        var rng = new RandomNumberGenerator { Seed = 1847 };
        for (var i = 0; i < 170; i++)
        {
            var scuff = new Vector2(rng.RandfRange(17, 90), rng.RandfRange(5, 129));
            canvas.DrawLine(scuff, scuff + new Vector2(rng.RandfRange(1, 5), rng.RandfRange(-2, 2)), new Color(0.85f, 0.64f, 0.37f, 0.09f), 0.6f);
        }
        // The tooled border, its stitching and the gilt corners.
        canvas.DrawRect(new Rect2(18, 9, 68, 116), new Color("ad8756"), false, 0.8f);
        for (var y = 13; y < 123; y += 6)
        {
            canvas.DrawLine(new Vector2(16, y), new Vector2(16, y + 2), new Color("c0a16e"), 0.7f);
            canvas.DrawLine(new Vector2(89, y), new Vector2(89, y + 2), new Color("c0a16e"), 0.7f);
        }
        canvas.DrawLine(new Vector2(19, 10), new Vector2(35, 10), new Color("d2ac70"), 2);
        canvas.DrawLine(new Vector2(70, 124), new Vector2(86, 124), new Color("d2ac70"), 2);
        if (!reverse)
        {
            // The botanical gold emblem, the title and the ribbon.
            var gold = new Color("c6a36c");
            canvas.DrawArc(new Vector2(52, 66), 18, 0, Mathf.Tau, 40, gold, 1, true);
            canvas.DrawLine(new Vector2(51, 81), new Vector2(51, 51), gold, 1.1f, true);
            canvas.DrawPolyline(new[] { new Vector2(51, 65), new Vector2(39, 59), new Vector2(43, 69), new Vector2(51, 72), new Vector2(65, 59), new Vector2(55, 61), new Vector2(51, 68) }, gold, 1, true);
            canvas.DrawString(ThemeDB.FallbackFont, new Vector2(22, 38), "JOURNAL", HorizontalAlignment.Center, 60, 9, gold);
            canvas.DrawRect(new Rect2(75, 133, 8, 10), new Color("854635"));
        }
        canvas.DrawSetTransform(Vector2.Zero);
    }
}

/// <summary>
/// The glow behind the pulsing book: a few warm, translucent rings round the cover, drawn once (and again if the dock is resized).
/// The pulse only changes its fade (Modulate), so a pulse does not draw it again.
/// </summary>
public partial class JournalHalo : Control
{
    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }

    public override void _Draw()
    {
        // The cover's middle, as the dock draws it (13, 3 and Size - (26, 24)): the rings spread from there.
        var middle = new Vector2(Size.X * 0.5f, 3 + (Size.Y - 24) * 0.5f);
        for (var ring = 0; ring < 6; ring++)
            DrawCircle(middle, Size.X * (0.34f + 0.06f * ring), new Color(0.98f, 0.78f, 0.38f, 0.075f));
    }
}
