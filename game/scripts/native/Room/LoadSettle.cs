using Godot;
using System;
using System.Diagnostics;
using System.Linq;

namespace GarageGame.Native.Room;

/// <summary>
/// The end of a room's loading (S12b): once its copies are drawn round the camera and its navigation is baked (or there is none
/// after WaitS), C#'s heap is compacted once, the large-object heap with it, and what it no longer needs is given back to the
/// system. Loading leaves a 10x island's heap at about twice what it keeps alive (the files read, the land's faces for its
/// collision and navigation, the tables checked); compacted it is about 100 MB smaller, for one pause of some 30 ms while the
/// world has only just appeared. The node then frees itself. Nothing here is the room's state.
/// </summary>
public partial class LoadSettle : Node
{
    public const string NodeName = "LoadSettle";
    /// <summary>At least this long after entering the scene (the bodies, the kernel and navigation join the world in the meantime).</summary>
    public const double WaitS = 1.0;
    /// <summary>Compact anyway after this long, whatever is still arriving.</summary>
    public const double GiveUpS = 30.0;

    /// <summary>When the compaction ran (null: not yet), its pause, and the managed heap before and after it, in bytes.</summary>
    public static (double PauseMs, long HeapBefore, long HeapAfter)? Last { get; private set; }

    private readonly Stopwatch _since = new();

    public override void _EnterTree() => _since.Restart();

    public override void _Process(double delta)
    {
        if (_since.Elapsed.TotalSeconds < WaitS) return;
        var copies = GetParent()?.GetNodeOrNull<PlantCopiesView>(PlantCopiesView.ViewName);
        var navigation = GetParent()?.GetParent()?.GetChildren().OfType<Navigation.RoomNavigation>().FirstOrDefault();
        var arrived = (copies == null || copies.Complete) && (navigation == null || navigation.IsReady);
        if (!arrived && _since.Elapsed.TotalSeconds < GiveUpS) return;
        Compact("the loading");
        SetProcess(false);
        QueueFree();
    }

    /// <summary>One blocking, compacting, decommitting collection of every generation and the large-object heap.</summary>
    public static void Compact(string what)
    {
        var before = GC.GetGCMemoryInfo().HeapSizeBytes;
        var clock = Stopwatch.StartNew();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Last = (clock.Elapsed.TotalMilliseconds, before, GC.GetGCMemoryInfo().HeapSizeBytes);
        GD.Print(FormattableString.Invariant(
            $"ROOM_SETTLED {what}'s garbage compacted away in {Last.Value.PauseMs:0} ms: C#'s heap {before / 1048576.0:0} MB, now {Last.Value.HeapAfter / 1048576.0:0} MB"));
    }
}
