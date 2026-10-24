using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using Godot;

namespace GarageGame.Tests.Characters;

/// <summary>
/// A manual windowed look at the drawn Gubble's glow (the characters round, K5): what the headless checks cannot see. Its behaviour
/// is tested headless in the play HUD suite (PlayHudTest.Gubble.cs); this is in no runner. Run it windowed on a GPU, when nobody is
/// playing (no window titled Garage Game* or EnFractal*):
///   Godot --path game --resolution 1280x720 res://tests/native_character_gubble.tscn -- --out=ABSOLUTE_DIR [--distance=0.24]
/// In the default room it frames the companion, the drawn Gubble, from 24 cm in front (or --distance), with the HUD hidden, and saves four frames:
/// by day with no glow (gubble_day.png), with its own glow at dusk (gubble_halo_dusk.png) and at night (gubble_halo_night.png):
/// the halo on the aura bone, the light on the torch flame; and mid-cast toward a spot to its right at night, the body leaning and
/// the torch arm swung (gubble_cast_night.png). It prints the bubble's light level and the anchors for each. Its saves and profile
/// stay under user://tests/gubble_halo_capture and are removed when it ends.
/// </summary>
public partial class GubbleHaloCapture : Node
{
    private const string Root = "user://tests/gubble_halo_capture";
    private const int Day = 286;

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task<Image> Grab()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgb8);
        return image;
    }

    private static void RemoveRoot()
    {
        var directory = ProjectSettings.GlobalizePath(Root);
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
        catch (System.IO.IOException error) { GD.Print("GUBBLE_HALO_CAPTURE: could not remove its saves: " + error.Message); }
    }

    public override async void _Ready()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        try
        {
            if (DisplayServer.GetName() == "headless")
            {
                GD.Print("GUBBLE_HALO_CAPTURE: needs a real window on the GPU; --headless renders nothing.");
                GetTree().Quit(2);
                return;
            }
            var output = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?[6..]
                ?? throw new ArgumentException("--out=ABSOLUTE_DIR is required");
            System.IO.Directory.CreateDirectory(output);
            RemoveRoot();
            CommandHost.SaveRoot = Root + "/saves";
            CommandHost.DefaultGlowProgressPath = Root + "/profile/glow_progress_v1.json";
            CommandHost.DefaultPlayerVitalsPath = Root + "/profile/player_vitals_v1.json";
            CommandHost.DefaultWishSaveRoot = Root + "/saves/wishes";
            RoomHud.ProfilePath = Root + "/profile/avatar_profile_v1.cfg";
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(world);
            // The room's data loads off the main thread (RoomData.LoadAsync, S6): wait on the clock, not a count of frames.
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            if (!world.WorldReady) throw new InvalidOperationException("the room did not load: " + world.LoadError);
            var look = world.Look;
            var companion = world.Companion;
            var drawn = GubbleCharacter.Of(companion) ?? throw new InvalidOperationException("the room's companion is not the drawn Gubble");
            foreach (var layer in world.FindChildren("*", "CanvasLayer", true, false).OfType<CanvasLayer>()) layer.Visible = false;
            world.Player.ReadKeyboard = false;
            world.Player.SetInputEnabled(false);
            CommandHost.Of(world)!.PlayerGoal("stay");
            // The player steps back out of the picture.
            world.Player.TryTeleportTo(companion.GlobalPosition + companion.GlobalBasis.Z.Normalized() * 0.5f + companion.GlobalBasis.X.Normalized() * 0.3f);
            await Frames(60);

            var middle = drawn.HaloAnchor.GlobalPosition;
            var forward = -companion.GlobalBasis.Z.Normalized();
            // --distance=M: how far in front the camera stands (0.24 m, a close look; the shoulder view is about half a metre).
            var distance = float.Parse(OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--distance=", StringComparison.Ordinal))?[11..] ?? "0.24", CultureInfo.InvariantCulture);
            var camera = new Camera3D { Name = "GubbleCamera", Fov = 40f, Near = 0.005f, Far = 50f };
            AddChild(camera);
            camera.GlobalPosition = middle + forward * distance + Vector3.Up * (0.125f * distance) + companion.GlobalBasis.X.Normalized() * (0.2f * distance);
            camera.LookAt(middle + Vector3.Up * 0.01f, Vector3.Up);
            look.FrameCamera(camera, middle);
            camera.MakeCurrent();

            async Task Shot(string name, float hour)
            {
                look.SetClock(hour, Day);
                await Frames(45);
                drawn.ReadLight();
                await Frames(2);
                var image = await Grab();
                var path = System.IO.Path.Combine(output, name);
                var saved = image.SavePng(path);
                GD.Print(string.Create(CultureInfo.InvariantCulture,
                    $"GUBBLE_HALO_CAPTURE: {name} at {hour:0.0} h: {saved}; bubble light level {drawn.LightLevel:0.00}; halo {drawn.HaloAnchor.GlobalPosition - companion.GlobalPosition}, light {drawn.LightAnchor?.GlobalPosition - companion.GlobalPosition}; glows {look.GlowCount}"));
            }

            await Shot("gubble_day.png", 12f);
            const string halo = "effect:capture_halo";
            look.StartGlow(halo, companion, Vector3.Zero, 0.6f, 0.6f);
            await Shot("gubble_halo_dusk.png", 18.5f);
            await Shot("gubble_halo_night.png", 22f);
            // Mid-cast toward a spot on its right, at night with its glow: the body leans and the torch arm swings.
            look.SetClock(22f, Day);
            await Frames(10);
            var spot = companion.GlobalPosition + companion.GlobalBasis.X.Normalized() * 0.35f + forward * 0.2f;
            companion.CastGesture(spot, "light");
            for (var i = 0; i < 300 && companion.GestureProgress < 0.32f; i++) await Frames(1);
            var cast = await Grab();
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"GUBBLE_HALO_CAPTURE: gubble_cast_night.png at {companion.GestureProgress:0.00} of the cast: {cast.SavePng(System.IO.Path.Combine(output, "gubble_cast_night.png"))}; arm swing {drawn.Gestures.LastArmSwingRad:0.00} rad"));
            look.StopGlow(halo);
            look.ReleaseClock();
            world.Free();
            await Frames(2);
            GD.Print("GUBBLE_HALO_CAPTURE_DONE out=" + output);
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
        finally
        {
            (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath) = previous;
            RemoveRoot();
        }
    }
}
