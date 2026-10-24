using Godot;
using System;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>The sight sweep spread over ticks (S12): the same team map as a sweep done whole, and the frame left no Godot wrapper.</summary>
public partial class JournalTest
{
    /// <summary>
    /// A sweep that may only do its share each tick (no time budget) is spread over the ticks before the next is due, and leaves the
    /// team's map (the discovered space and the things seen, as the save keeps them) byte for byte as a sweep done whole in one tick;
    /// neither leaves a Godot value for the .NET finalizer.
    /// </summary>
    private async Task TestSpreadSweepMatchesWhole()
    {
        await Stand(_companion, new Vector3(1.6f, 0, 1.2f), Vector3.Forward, "in the south-east, for the spread sweep");
        await Stand(_player, new Vector3(0.45f, 0, 0.62f), Vector3.Forward, "on the rug, facing the book, for the spread sweep");
        var (spread, spreadTicks, spreadLeft) = await OneSweep(budgetMs: 0);
        var (whole, wholeTicks, wholeLeft) = await OneSweep(budgetMs: double.PositiveInfinity);
        Check(spreadTicks > 1 && spreadTicks <= 13 && wholeTicks == 1,
            $"with no time to spare a sweep is spread over its ticks before the next is due ({spreadTicks} ticks; whole, {wholeTicks})");
        Check(spread == whole && spread.Contains("obj:book", StringComparison.Ordinal),
            "and the team's map it leaves is the whole sweep's, byte for byte (the book seen, the same space discovered)");
        Check(spreadLeft <= 0 && wholeLeft <= 0, $"neither leaves a Godot value for the finalizer ({spreadLeft} and {wholeLeft} left)");
        _host.SweepTickBudgetMs = CommandHost.DefaultSweepTickBudgetMs;
        _host.ForgetDiscoveredSpace();
        _host.ClearPerceptionMemory(Companion);
    }

    /// <summary>
    /// One sweep from a blank map, the bodies holding still, the host's ticks driven here one a frame: the map it leaves, the ticks it
    /// took, and the Godot wrappers the host's ticks left (counted around each tick alone, after the finalizer has caught up).
    /// </summary>
    private async Task<(string Map, int Ticks, int Left)> OneSweep(double budgetMs)
    {
        _host.TeamSightIntervalS = 0;
        _host.ForgetDiscoveredSpace();
        _host.ClearPerceptionMemory(Companion);
        await Frames(2);
        _host.SweepTickBudgetMs = budgetMs;
        _host.SetPhysicsProcess(false);
        var done = _host.SweepsDone;
        _host.TeamSightIntervalS = 0.25;
        int ticks = 0, left = 0;
        try
        {
            for (var tick = 0; tick < 60 && _host.SweepsDone == done; tick++)
            {
                FrameGarbage.Settle();
                var before = FrameGarbage.TrackedNow;
                _host._PhysicsProcess(1.0 / 60.0);
                left += FrameGarbage.TrackedNow - before;
                if (_host.SweepUnderWay || _host.SweepsDone != done) ticks++;
                await Frames(1);
            }
        }
        finally
        {
            _host.TeamSightIntervalS = 0;
            _host.SetPhysicsProcess(true);
        }
        return (Canonical(_host.ExportDiscovered()), ticks, left);
    }
}
