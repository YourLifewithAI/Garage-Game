using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The stamina bar under the hearts, always visible (docs/game/MECHANICS.md sections 1 and 4). Its length is what the body can
/// spend: a tick for each band opened (the pack's band size, each larger with Big bar), the first row five bands long and a
/// second row under it past that ("two rows past 2.0 m"). The first row fills first. After the bar ran empty it is tinted until
/// it refills to a quarter (the body is slowed meanwhile, the stamina ledger's gate). In Creative it is drawn full and grey.
/// </summary>
public partial class StaminaBar : Control
{
    /// <summary>Pixels to a metre of bar: a band of 0.4 m is 44 px, so five bands are 220 px.</summary>
    public const float PxPerM = 110f;
    public const float RowPx = 11f;
    public const float GapPx = 3f;

    private static readonly Color Back = new(0.05f, 0.11f, 0.08f, 0.65f);
    private static readonly Color Fill = new("6cc070");
    private static readonly Color Slowed = new("e0a03c");
    private static readonly Color Edge = new("0b1a10");
    private static readonly Color Tick = new(0.03f, 0.1f, 0.05f, 0.75f);
    private static readonly Color GreyBack = new(0.12f, 0.14f, 0.14f, 0.65f);
    private static readonly Color GreyFill = new("a8b0b0");
    private static readonly Color GreyEdge = new("4c5555");

    public float StaminaM { get; private set; }
    public float MaxM { get; private set; }
    /// <summary>The first row's length in metres (five bands), where the second row starts.</summary>
    public float RowM { get; private set; } = MeterMath.LegacyRowM;
    /// <summary>One band's length in metres (the ticks), 0 for no ticks.</summary>
    public float BandM { get; private set; }
    public bool Grey { get; private set; }
    public bool IsSlowed { get; private set; }
    public int RowCount => MeterMath.BarRows(MaxM, RowM).Second > 0 ? 2 : MaxM > 0 ? 1 : 0;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    /// <summary>The bar as it is now. Redrawn only when something it shows changed (the stamina to a few millimetres).</summary>
    public void Set(float staminaM, float maxM, float rowM, float bandM, bool grey, bool slowed)
    {
        staminaM = float.IsFinite(staminaM) ? System.Math.Clamp(staminaM, 0, System.Math.Max(0, maxM)) : 0;
        var snapped = Mathf.Round(staminaM * 500f) / 500f;
        if (snapped == StaminaM && maxM == MaxM && rowM == RowM && bandM == BandM && grey == Grey && slowed == IsSlowed) return;
        var layout = maxM != MaxM || rowM != RowM;
        (StaminaM, MaxM, RowM, BandM, Grey, IsSlowed) = (snapped, maxM, rowM, bandM, grey, slowed);
        if (layout) UpdateMinimumSize();
        QueueRedraw();
    }

    public override Vector2 _GetMinimumSize()
    {
        var (first, second) = MeterMath.BarRows(MaxM, RowM);
        return new Vector2(Mathf.Max(first, second) * PxPerM, RowCount * RowPx + System.Math.Max(0, RowCount - 1) * GapPx);
    }

    public override void _Draw()
    {
        var began = MeterCost.Begin();
        DrawBar();
        MeterCost.End(began, MeterCost.Piece.Bar);
    }

    private void DrawBar()
    {
        var (first, second) = MeterMath.BarRows(MaxM, RowM);
        if (first <= 0) return;
        var (litFirst, litSecond) = MeterMath.BarFill(StaminaM, first, second);
        var fill = Grey ? GreyFill : IsSlowed ? Slowed : Fill;
        var back = Grey ? GreyBack : Back;
        var edge = Grey ? GreyEdge : Edge;
        DrawRow(0, first, litFirst, back, fill, edge);
        if (second > 0) DrawRow(1, second, litSecond, back, fill, edge);
    }

    private void DrawRow(int row, float lengthM, float litM, Color back, Color fill, Color edge)
    {
        var y = row * (RowPx + GapPx);
        var box = new Rect2(0, y, lengthM * PxPerM, RowPx);
        DrawRect(box, back);
        if (litM > 0) DrawRect(new Rect2(0, y, litM * PxPerM, RowPx), fill);
        // A tick at each band's end inside the row, so a child counts the bands.
        if (BandM > 0.001f)
            for (var mark = BandM; mark < lengthM - 0.001f; mark += BandM)
                DrawLine(new Vector2(mark * PxPerM, y + 1), new Vector2(mark * PxPerM, y + RowPx - 1), Tick, 1f);
        DrawRect(box, edge, false, 1.5f);
    }
}
