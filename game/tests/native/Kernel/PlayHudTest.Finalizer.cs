using Godot;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// S12: the HUD's frame and the host's focus leave no Godot value for the .NET finalizer. Every StringName, NodePath, Godot
/// collection, Variant and object wrapper left to it is freed later on the finalizer's own thread while the main thread uses the
/// engine; on the 10x island thousands a second waited there (S9), and the game crashed in NodePath.Finalize.
/// </summary>
public partial class PlayHudTest
{
    private async Task TestNoFinalizerGarbage()
    {
        var host = CommandHost.Of(_world)!;
        _hud.SetProcess(false);
        try
        {
            // Once first: what is made once and kept (a key name read from the map, a path) is not garbage.
            _hud._Process(1.0 / 60.0);
            host.PlayerFocus();
            FrameGarbage.Settle();
            var before = FrameGarbage.TrackedNow;
            for (var i = 0; i < 30; i++) _hud._Process(1.0 / 60.0);
            var frames = FrameGarbage.TrackedNow - before;
            FrameGarbage.Settle();
            before = FrameGarbage.TrackedNow;
            for (var i = 0; i < 10; i++) host.PlayerFocus();
            var focus = FrameGarbage.TrackedNow - before;
            FrameGarbage.Settle();
            before = FrameGarbage.TrackedNow;
            for (var i = 0; i < 5; i++) host.Entities();
            var entities = FrameGarbage.TrackedNow - before;
            Check(frames <= 0 && focus <= 0 && entities <= 0,
                $"thirty HUD frames, ten focus queries and five entity listings leave no Godot value for the .NET finalizer ({frames}, {focus} and {entities} left)");
        }
        finally { _hud.SetProcess(true); }
        await Frames(2);
    }
}
