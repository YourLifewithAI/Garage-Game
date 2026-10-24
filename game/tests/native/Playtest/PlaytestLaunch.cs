using Godot;
using GarageGame.Native;
using GarageGame.Native.Playtest;

namespace GarageGame.Tests;

/// <summary>
/// The game as its main scene boots it (NativeGameBoot: --room=, --style=, the team's boot), with the playtest recorder attached by
/// the integrator's one line once the world is ready: a windowed launch that records before RoomWorld carries the line itself (R1's
/// windowed check and the hitch's measurement). Run it windowed:
/// Godot --path game --resolution 1280x720 res://tests/native/Playtest/playtest_launch.tscn -- --room=&lt;id&gt; --playtest-beat=10
/// </summary>
public partial class PlaytestLaunch : NativeGameBoot
{
    private RoomWorld? _world;

    public override void _Ready()
    {
        base._Ready();
        foreach (var child in GetChildren())
            if (child is RoomWorld world) _world = world;
    }

    public override void _Process(double delta)
    {
        if (_world == null || !_world.WorldReady) return;
        // The line RoomWorld will carry, right after CompanionBridge.Attach(this).
        if (PlaytestRecorder.Of(_world) == null) PlaytestRecorder.Attach(_world);
        SetProcess(false);
    }
}
