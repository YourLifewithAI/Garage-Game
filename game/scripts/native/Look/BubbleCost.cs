using System.Diagnostics;

namespace GarageGame.Native.Look;

/// <summary>
/// What the Bubble costs a frame (the brief's frame budget; the pattern of Ui/MeterCost): its drawing (the look's per-frame update:
/// the breath, the string, the ring, the light read) and what choosing and steering it cost the HUD (the per-frame update, the ray
/// test for the bubble, and the host's preview of a lead that the reticle asks) and the two right-clicks' own work (choosing it and
/// leading it, the host's command included). While Measuring (the suite and the capture switch it
/// on, a player's game never does), each piece adds its time to the frame it runs in; the HUD's update closes the frame. The numbers
/// are CPU wall time of the code, in milliseconds: the GPU's share of drawing a film is outside it (the capture reads the renderer's).
/// </summary>
public static class BubbleCost
{
    public enum Piece { Draw, Select, Aim, Preview, Choose, Lead, Show }

    public static bool Measuring { get; set; }
    public static long Frames { get; private set; }
    public static long TotalTicks { get; private set; }
    public static long WorstTicks { get; private set; }
    /// <summary>Every measured frame's cost in milliseconds, in order.</summary>
    public static System.Collections.Generic.List<double> SamplesMs { get; } = new();
    /// <summary>Managed memory the measured pieces allocated.</summary>
    public static long AllocatedBytes { get; private set; }
    public static readonly long[] PieceTicks = new long[7];
    private static long _frame;
    private static long _beganBytes;

    /// <summary>The start of a piece (0 when not measuring). Pieces do not nest.</summary>
    public static long Begin()
    {
        if (!Measuring) return 0;
        _beganBytes = System.GC.GetAllocatedBytesForCurrentThread();
        return Stopwatch.GetTimestamp();
    }

    /// <summary>The piece that began at this timestamp adds its time to the frame.</summary>
    public static void End(long began, Piece piece)
    {
        if (!Measuring || began == 0) return;
        var took = Stopwatch.GetTimestamp() - began;
        _frame += took;
        PieceTicks[(int)piece] += took;
        AllocatedBytes += System.GC.GetAllocatedBytesForCurrentThread() - _beganBytes;
    }

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
    /// <summary>One piece's mean per frame, in milliseconds.</summary>
    public static double PieceMeanMs(Piece piece) => Frames == 0 ? 0 : PieceTicks[(int)piece] * 1000.0 / Stopwatch.Frequency / Frames;
}
