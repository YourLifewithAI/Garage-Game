using System;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The Gubble's pips as a row under the hearts and the stamina bar, always (docs/game/MECHANICS.md sections 1 and 5, decision 51,
/// the founder on 15 October: "The pips are a row under the hearts and the bar, always; nothing round the Gubble"; it replaces
/// decision 38's marker that was drawn only while the Gubble was out of frame, and the dots that hovered round the Gubble are
/// gone): a pip you have in the Gubble's aura colour, a spent one a ring, the shadow pool of Creative in its soft grey. With the
/// player's numbers option on it reads "Pips 6/8" beside the dots.
/// It takes no room on an island with no pool. It redraws only when what it shows changes (its pool, its numbers, whether the
/// HUD shows it, and the blink of a refused cast), so a row that waits costs a comparison.
/// </summary>
public partial class PipsMarker : Control
{
    /// <summary>A pip's picture is this many pixels square; the next one starts a pixel further on.</summary>
    public const float PipPx = 15f;
    public const float GapPx = 1f;
    public const float PitchPx = PipPx + GapPx;
    /// <summary>Pips in one row: the pool's ceiling of 40 is two rows.</summary>
    public const int PerRow = 20;
    /// <summary>How long a refused cast's flash lasts: the pool blinks to say it is empty.</summary>
    public const double FlashS = 0.6;
    /// <summary>The shadow pool's pips: the grey of the grey hearts and the grey bar (their fill, StaminaBar.GreyFill), and a dimmer ring.</summary>
    public static readonly Color GreyHave = new("a8b0b0");
    public static readonly Color GreySpent = new(0.66f, 0.69f, 0.69f, 0.9f);
    private const float NumbersGapPx = 8f;
    private const int NumbersFontSize = 14;

    public int Pips { get; private set; }
    public int MaxPips { get; private set; }
    public bool Shadow { get; private set; }
    public Color Aura { get; private set; } = new("a18cc3");
    /// <summary>Whether the row is drawn now: the HUD shows its meters and the island has a pool.</summary>
    public bool Shown { get; private set; }
    public double FlashLeftS { get; private set; }
    /// <summary>The pool in numbers ("Pips 6/8"), drawn beside the dots only while the player's numbers option is on; empty otherwise.</summary>
    public string Numbers { get; private set; } = "";
    /// <summary>The rows of dots the marker has room for (0 with no pool).</summary>
    public int Rows => MaxPips <= 0 ? 0 : (MaxPips + PerRow - 1) / PerRow;

    private string? _widthFor;
    private float _numbersWidth;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    /// <summary>The pool as it is now: pips left, the most, whether it is the shadow pool, the Gubble's colour. No room with no pool.</summary>
    public void SetPool(int pips, int maxPips, bool shadow, Color aura)
    {
        maxPips = Math.Max(0, maxPips);
        pips = Math.Clamp(pips, 0, maxPips);
        if (pips == Pips && maxPips == MaxPips && shadow == Shadow && aura == Aura) return;
        var layout = maxPips != MaxPips;
        (Pips, MaxPips, Shadow, Aura) = (pips, maxPips, shadow, aura);
        if (layout) UpdateMinimumSize();
        if (Shown) QueueRedraw();
    }

    /// <summary>The numbers option: the words beside the dots, or nothing.</summary>
    public void SetNumbers(string words)
    {
        if (words == Numbers) return;
        Numbers = words;
        UpdateMinimumSize();
        if (Shown) QueueRedraw();
    }

    /// <summary>The pool blinks: a cast was refused for want of pips ("The Gubble needs a moment").</summary>
    public void Flash()
    {
        FlashLeftS = FlashS;
        if (Shown) QueueRedraw();
    }

    /// <summary>
    /// Each frame: whether the row is drawn (the HUD shows its meters) and the time that passed, for the blink. Redraws on a change
    /// only, so a row that waits costs a comparison; a blink redraws at each flip.
    /// </summary>
    public void Present(bool shown, double delta)
    {
        shown &= MaxPips > 0;
        if (shown != Shown)
        {
            Shown = shown;
            QueueRedraw();
        }
        if (FlashLeftS <= 0) return;
        var was = FlashOn;
        FlashLeftS = Math.Max(0, FlashLeftS - delta);
        if (Shown && was != FlashOn) QueueRedraw();
    }

    private bool FlashOn => FlashLeftS > 0 && (int)(FlashLeftS * 10) % 2 == 0;

    /// <summary>Where pip i is drawn, from the marker's own top left: in rows of PerRow, left to right.</summary>
    public Vector2 Slot(int i) => new((i % PerRow) * PitchPx, (i / PerRow) * PitchPx);

    public override Vector2 _GetMinimumSize()
    {
        if (MaxPips <= 0) return Vector2.Zero;
        if (_widthFor != Numbers)
        {
            _widthFor = Numbers;
            _numbersWidth = Numbers.Length == 0 ? 0 : GetThemeDefaultFont().GetStringSize(Numbers, HorizontalAlignment.Left, -1, NumbersFontSize).X;
        }
        var dots = Math.Min(MaxPips, PerRow) * PitchPx;
        return new Vector2(dots + (_numbersWidth > 0 ? NumbersGapPx + _numbersWidth : 0), Rows * PitchPx);
    }

    public override void _Draw()
    {
        var began = MeterCost.Begin();
        DrawMarker();
        MeterCost.End(began, MeterCost.Piece.Pips);
    }

    private void DrawMarker()
    {
        if (!Shown || MaxPips == 0) return;
        var size = new Vector2(PipPx, PipPx);
        var flash = FlashOn;
        var tint = flash ? new Color("ffe9a8") : Aura.Lightened(0.15f);
        var ringTint = flash ? new Color("ff9d8a") : Colors.White;
        for (var i = 0; i < MaxPips; i++)
        {
            var rect = new Rect2(Slot(i), size);
            var has = i < Pips;
            // The shadow pool, in the grey of Creative's hearts and bar: a pip you have a filled grey dot, one spent a grey ring;
            // never the aura's colour, for nothing is spent for real.
            if (Shadow) DrawTextureRect(has ? MeterTextures.PipFull : MeterTextures.PipSpent, rect, false, has ? GreyHave : GreySpent);
            else if (has) DrawTextureRect(MeterTextures.PipFull, rect, false, tint);
            else DrawTextureRect(MeterTextures.PipSpent, rect, false, ringTint);
        }
        if (Numbers.Length == 0) return;
        var font = GetThemeDefaultFont();
        var at = new Vector2(Math.Min(MaxPips, PerRow) * PitchPx + NumbersGapPx, (PitchPx + font.GetAscent(NumbersFontSize) - font.GetDescent(NumbersFontSize)) / 2f);
        DrawString(font, at, Numbers, HorizontalAlignment.Left, -1, NumbersFontSize, new Color("f0e6cf"));
    }
}
