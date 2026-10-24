using Godot;
using System;
using System.Linq;

namespace GarageGame.Native.Companion;

/// <summary>
/// The room as the game builds it (scenes/room.tscn), with the companion link on: the real game host for an AI client
/// or the companion suite, headless or windowed. Headless needs no GPU:
///   godot --headless --path game res://scripts/native/Companion/companion_room.tscn -- [--room=ID]
/// Once RoomWorld attaches the bridge itself (the integrator's wiring), this scene finds that bridge and only waits.
/// For harnesses: --companion-quit-file=PATH quits cleanly (deleting the session file) once PATH exists, and
/// --companion-parent-pid=N quits when process N ends, so a test that dies never leaves the room running.
/// </summary>
public partial class CompanionRoom : Node
{
    public RoomWorld World { get; private set; } = null!;
    public CompanionBridge? Bridge { get; private set; }

    private string _quitFile = "";
    private int _parentPid;
    private double _watch;
    private bool _quitting;

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        var arguments = OS.GetCmdlineUserArgs();
        string? Value(string name) => arguments.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        _quitFile = Value("--companion-quit-file") ?? "";
        _ = int.TryParse(Value("--companion-parent-pid"), out _parentPid);
        World = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        if (Value("--room") is { Length: > 0 } room) World.RoomDirectory = RoomWorld.ResolveRoom(room);
        AddChild(World);
        while (!World.WorldReady && World.LoadError.Length == 0) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (World.LoadError.Length > 0)
        {
            GD.PushError("COMPANION_ROOM the room failed to load: " + World.LoadError);
            GetTree().Quit(1);
            return;
        }
        Bridge = CompanionBridge.Of(World) ?? CompanionBridge.Attach(World);
        GD.Print($"COMPANION_ROOM_READY room={World.Room.RoomId} link={(Bridge.Link != null ? "on" : "off")}");
    }

    public override void _Process(double delta)
    {
        _watch += delta;
        if (_quitting || _watch < 0.25) return;
        _watch = 0;
        if ((_quitFile.Length > 0 && System.IO.File.Exists(_quitFile)) || (_parentPid > 0 && !Alive(_parentPid)))
        {
            _quitting = true;
            GD.Print("COMPANION_ROOM quitting");
            GetTree().Quit(0);
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
