using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The hearts: drawn, not written (docs/game/MECHANICS.md sections 1 and 4). A heart is four quarters, filled clockwise from its
/// top left and lost from the last back, so a hurt of one quarter is a quarter you see. At most two rows of fifteen. At one heart
/// or less the hearts shake. In Creative they are drawn full and grey (nothing hurts there). The meter holds only what to draw and
/// redraws only when that changes, or each frame while it shakes; the host's numbers reach it through Set. Each heart is one
/// picture (MeterTextures), so a redraw is a call for each heart.
/// </summary>
public partial class HeartsMeter : Control
{
    public const float HeartPx = 26f;
    public const float GapPx = 3f;
    /// <summary>How far a shaking heart moves from its place, in pixels.</summary>
    public const float ShakePx = 1.6f;

    /// <summary>The quarters left, as drawn (all of them in Creative).</summary>
    public int Quarters { get; private set; }
    public int MaxQuarters { get; private set; }
    public bool Grey { get; private set; }
    public bool Shaking => MeterMath.Shakes(Quarters, Grey);
    public int HeartCount => MeterMath.HeartCount(MaxQuarters);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetProcess(Shaking);
    }

    /// <summary>The hearts as they are now: quarters left, the most, and whether they are grey. Redrawn only when something changed.</summary>
    public void Set(int quarters, int maxQuarters, bool grey)
    {
        quarters = System.Math.Clamp(quarters, 0, System.Math.Max(0, maxQuarters));
        if (quarters == Quarters && maxQuarters == MaxQuarters && grey == Grey) return;
        var layout = maxQuarters != MaxQuarters;
        (Quarters, MaxQuarters, Grey) = (quarters, maxQuarters, grey);
        if (layout) UpdateMinimumSize();
        SetProcess(Shaking);
        QueueRedraw();
    }

    public override Vector2 _GetMinimumSize()
    {
        var hearts = HeartCount;
        var rows = MeterMath.HeartRows(hearts);
        var columns = System.Math.Min(hearts, MeterMath.HeartsPerRow);
        return new Vector2(columns * (HeartPx + GapPx), rows * (HeartPx + GapPx));
    }

    /// <summary>While the hearts shake the meter redraws every frame; otherwise it is asleep.</summary>
    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var began = MeterCost.Begin();
        DrawHearts();
        MeterCost.End(began, MeterCost.Piece.Hearts);
    }

    private void DrawHearts()
    {
        var hearts = HeartCount;
        if (hearts == 0) return;
        var shake = Shaking;
        var seconds = Time.GetTicksMsec() / 1000.0;
        for (var heart = 0; heart < hearts; heart++)
        {
            var at = new Vector2(heart % MeterMath.HeartsPerRow, heart / MeterMath.HeartsPerRow) * (HeartPx + GapPx) - Vector2.One;
            if (shake) at += new Vector2(Mathf.Sin((float)(seconds * 58 + heart * 2.1)), Mathf.Cos((float)(seconds * 47 + heart * 1.3))) * ShakePx;
            // Full in Creative (grey); else what the quarters say: a heart's quarters clockwise from the top left.
            DrawTexture(Grey ? MeterTextures.GreyHeart : MeterTextures.Heart(MeterMath.FillOf(Quarters, heart)), at);
        }
    }
}
