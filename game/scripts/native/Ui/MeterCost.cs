using System.Diagnostics;

namespace GarageGame.Native.Ui;

/// <summary>
/// What the meters and the minimap cost a frame (the mechanics round's budget, docs/runs/RUN-2-MECHANICS.md section 9: at most
/// 0.5 ms a frame). While Measuring (the suite and the captures switch it on, a player's game never does), each piece that runs
/// for them adds its time to the frame: the HUD's update of the meters, the pips and the minimap, and each meter's draw (the
/// recording of its drawing; the GPU's share is outside it). The next frame's first update closes the frame.
/// </summary>
public static class MeterCost
{
    public static bool Measuring { get; set; }
    public static long Frames { get; private set; }
    public static long TotalTicks { get; private set; }
    public static long WorstTicks { get; private set; }
    /// <summary>Every measured frame's cost in milliseconds, in order (the suite reads the worst few to say what they were).</summary>
    public static System.Collections.Generic.List<double> SamplesMs { get; } = new();
    /// <summary>Managed memory the measured pieces allocated (the hot path allocates none when nothing changes: the S12 lesson).</summary>
    public static long AllocatedBytes { get; private set; }
    private static long _frame;
    private static long _beganBytes;

    /// <summary>The start of a piece (0 when not measuring). Pieces do not nest.</summary>
    public static long Begin()
    {
        if (!Measuring) return 0;
        _beganBytes = System.GC.GetAllocatedBytesForCurrentThread();
        return Stopwatch.GetTimestamp();
    }

    /// <summary>The pieces that are measured, each its own total (the suite says which costs what).</summary>
    public enum Piece { Update, Hearts, Bar, Pips, Map }
    public static readonly long[] PieceTicks = new long[5];

    /// <summary>The piece that began at this timestamp adds its time to the frame.</summary>
    public static void End(long began, Piece piece = Piece.Update)
    {
        if (!Measuring || began == 0) return;
        var took = Stopwatch.GetTimestamp() - began;
        _frame += took;
        PieceTicks[(int)piece] += took;
        AllocatedBytes += System.GC.GetAllocatedBytesForCurrentThread() - _beganBytes;
    }

    /// <summary>One piece's mean per frame, in milliseconds.</summary>
    public static double PieceMeanMs(Piece piece) => Frames == 0 ? 0 : PieceTicks[(int)piece] * 1000.0 / Stopwatch.Frequency / Frames;

    /// <summary>Close the frame that has run: its pieces are one sample.</summary>
    public static void CloseFrame()
    {
        if (!Measuring) return;
        Frames++;
        SamplesMs.Add(_frame * 1000.0 / Stopwatch.Frequency);
        TotalTicks += _frame;
        if (_frame > WorstTicks) WorstTicks = _frame;
        _frame = 0;
    }

    public static void Reset()
    {
        Frames = TotalTicks = WorstTicks = AllocatedBytes = 0;
        SamplesMs.Clear();
        System.Array.Clear(PieceTicks);
        _frame = 0;
    }

    public static double MeanMs => Frames == 0 ? 0 : TotalTicks * 1000.0 / Stopwatch.Frequency / Frames;
    public static double WorstMs => WorstTicks * 1000.0 / Stopwatch.Frequency;
}
