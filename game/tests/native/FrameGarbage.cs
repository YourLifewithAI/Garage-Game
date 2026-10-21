using Godot;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace GarageGame.Tests;

/// <summary>
/// The Godot wrappers a piece of code leaves for the .NET finalizer: every StringName, NodePath, Godot collection, Variant and
/// object wrapper registers with GodotSharp's DisposablesTracker when made and unregisters when disposed; one left registered
/// waits for the finalizer thread, which then frees its native side away from the main thread.
/// </summary>
public static class FrameGarbage
{
    private static readonly Func<int> Tracked = TrackedCounter();

    private static Func<int> TrackedCounter()
    {
        var tracker = typeof(GodotObject).Assembly.GetType("Godot.DisposablesTracker") ?? throw new InvalidOperationException("no DisposablesTracker");
        var counted = new[] { "GodotObjectInstances", "OtherInstances" }
            .Select(name => tracker.GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                            ?? throw new InvalidOperationException("no DisposablesTracker." + name))
            .Select(dictionary => (Dictionary: dictionary, Count: dictionary.GetType().GetProperty("Count")!))
            .ToArray();
        return () => counted.Sum(c => (int)c.Count.GetValue(c.Dictionary)!);
    }

    /// <summary>Wrappers registered with the tracker now (live, or waiting for the finalizer).</summary>
    public static int TrackedNow => Tracked();

    /// <summary>Settle the finalizer: every wrapper that is garbage now is finalized and unregistered.</summary>
    public static void Settle()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>A call's mean bytes allocated on this thread, its mean wrappers left registered, and its longest time, over repeats.</summary>
    public static (double Bytes, double Wrappers, double LongestMs) Measure(Action call, int repeats)
    {
        Settle();
        long bytes = 0;
        var longest = 0.0;
        var before = TrackedNow;
        for (var i = 0; i < repeats; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            call();
            longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
            bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        // Wrappers still registered after the calls that were not before: made and never disposed. (Collections in between could
        // finalize some; the settle before makes that rare, and a count that is already 0 cannot hide anything.)
        var left = TrackedNow - before;
        return (bytes / (double)repeats, left / (double)repeats, longest);
    }

    /// <summary>Frames run with no collection allowed: all threads' bytes and the wrappers left registered. Null if a collection came.</summary>
    public static async Task<(long Bytes, int Wrappers)?> MeasureFrames(int frames, Func<Task> frame)
    {
        Settle();
        // Starting the region collects first; the count and the bytes are taken after it.
        if (!GC.TryStartNoGCRegion(200L * 1024 * 1024)) return null;
        var before = TrackedNow;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var collections = GC.CollectionCount(0);
        try
        {
            for (var i = 0; i < frames; i++) await frame();
        }
        finally
        {
            if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }
        if (GC.CollectionCount(0) != collections) return null;
        return (GC.GetTotalAllocatedBytes(true) - allocated, TrackedNow - before);
    }
}
