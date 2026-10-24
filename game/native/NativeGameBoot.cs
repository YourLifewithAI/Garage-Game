using Godot;
using System.Linq;
using GarageGame.Native.Look;

namespace GarageGame.Native;

public partial class NativeGameBoot : Node
{
    public override void _Ready()
    {
        var arguments = OS.GetCmdlineUserArgs();
        // Release templates reject command-line scene overrides. This bounded probe
        // uses the real exported entry point, without reading or writing user saves.
        if (arguments.Contains("--native-contract-probe"))
        {
            AddChild(new NativeContractProbe());
            return;
        }
        // The saves made before the rename, copied once into this build's user:// folder before anything reads it (phase 3).
        Saves.UserFolderMove.RunOnce();
        var world =GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        var room = arguments.FirstOrDefault(a => a.StartsWith("--room="))?["--room=".Length..];
        if (!string.IsNullOrWhiteSpace(room)) world.RoomDirectory = RoomWorld.ResolveRoom(room);
        // A look preview: --style=v2 (the default style's version) or --style=<preset_id>/v<N>, overriding the room's pin.
        var style = arguments.FirstOrDefault(a => a.StartsWith("--style="))?["--style=".Length..];
        if (!string.IsNullOrWhiteSpace(style))
            world.StylePresetPath = $"{StylePreset.StylesRoot}/{(style.Contains('/') ? style : $"{RoomWorld.DefaultStyleId}/{style}")}.json";
        DisplayServer.WindowSetTitle("Garage Game");
        AddChild(world);
    }
}
