using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using Godot;

namespace GarageGame.Tests.Look;

/// <summary>
/// A manual windowed check of the view through the Gubble's eyes (its behaviour is tested headless in the play HUD suite,
/// PlayHudTest.GubbleView.cs). It is in no runner. Run it windowed on a GPU, when nobody is playing:
///   Godot --path game --fixed-fps 60 --resolution 1280x720 res://tests/native_gubble_view_capture.tscn -- --capture-dir=ABSOLUTE_DIR
/// In the default room at noon, in the first-person view (F1), it opens the view with I, turns the Gubble's eye toward the
/// player and checks that the screen shows the live picture, that the picture changes as the Gubble's eye turns (it is live),
/// and that the Gubble's eye draws the player's body (the picture changes when that one layer is left out). Back in F1, it checks
/// that the player's own body neither shows nor casts a shadow there, as before. It shows a refusal of the Gubble's over the
/// view, and measures the journal's Discoveries and Abilities pages. It saves three frames to ABSOLUTE_DIR: the open view
/// (gubble-view.png), the Gubble's words over it (gubble-view-words.png) and the Abilities page (journal-abilities.png). Its saves and profile stay under user://tests/gubble_view_capture and are removed when it ends.
/// </summary>
public partial class GubbleViewCapture : Node
{
    private const string Root = "user://tests/gubble_view_capture";
    private int _checks;

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Check(bool ok, string text)
    {
        if (!ok) throw new Exception(text);
        GD.Print("PASS: " + text);
        _checks++;
    }

    /// <summary>An image of a viewport as drawn on the last frame.</summary>
    private async Task<Image> Grab(Viewport viewport)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = viewport.GetTexture().GetImage();
        image.Convert(Image.Format.Rgb8);
        return image;
    }

    /// <summary>Mean absolute difference (0 to 1) of two images over a region given in fractions of their size, sampled on a grid.</summary>
    private static float Difference(Image a, Image b, Rect2 region)
    {
        var total = 0.0;
        var samples = 0;
        for (var gy = 0; gy < 48; gy++)
            for (var gx = 0; gx < 64; gx++)
            {
                var u = region.Position.X + region.Size.X * (gx + 0.5f) / 64;
                var v = region.Position.Y + region.Size.Y * (gy + 0.5f) / 48;
                var ca = a.GetPixel((int)(u * (a.GetWidth() - 1)), (int)(v * (a.GetHeight() - 1)));
                var cb = b.GetPixel((int)(u * (b.GetWidth() - 1)), (int)(v * (b.GetHeight() - 1)));
                total += (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B)) / 3.0;
                samples++;
            }
        return (float)(total / samples);
    }

    /// <summary>How much an image varies (the mean distance of its samples from their mean): a blank picture is near 0.</summary>
    private static float Variation(Image image)
    {
        var colours = Enumerable.Range(0, 48 * 64).Select(i => image.GetPixel((int)((i % 64 + 0.5f) / 64 * (image.GetWidth() - 1)), (int)((i / 64 + 0.5f) / 48 * (image.GetHeight() - 1)))).ToArray();
        var mean = new Color(colours.Average(c => c.R), colours.Average(c => c.G), colours.Average(c => c.B));
        return colours.Average(c => (Math.Abs(c.R - mean.R) + Math.Abs(c.G - mean.G) + Math.Abs(c.B - mean.B)) / 3f);
    }

    private static void RemoveRoot()
    {
        var directory = ProjectSettings.GlobalizePath(Root);
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
        catch (System.IO.IOException error) { GD.Print("GUBBLE_VIEW_CAPTURE: could not remove its saves: " + error.Message); }
    }

    public override async void _Ready()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        try
        {
            var output = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--capture-dir=", StringComparison.Ordinal))?[14..];
            Check(EyeView.CanRender, "this run draws (run it windowed, not headless)");
            RemoveRoot();
            CommandHost.SaveRoot = Root + "/saves";
            CommandHost.DefaultGlowProgressPath = Root + "/profile/glow_progress_v1.json";
            CommandHost.DefaultPlayerVitalsPath = Root + "/profile/player_vitals_v1.json";
            CommandHost.DefaultWishSaveRoot = Root + "/saves/wishes";
            RoomHud.ProfilePath = Root + "/profile/avatar_profile_v1.cfg";
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            Check(world.WorldReady, "the room loads: " + world.LoadError);
            var player = world.Player;
            var companion = world.Companion;
            var hud = world.GetNode<RoomHud>("RoomHud");
            player.ReadKeyboard = false;
            // Noon (the T key's third step), so the sun casts its shadows; the Gubble waits where it is.
            for (var i = 0; i < 3; i++) hud.StepTimeOfDay();
            companion.Stay();
            hud.SetViewMode(0);
            await Frames(90);

            // Through the Gubble's eyes, turned toward the player's middle.
            hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.I, Pressed = true });
            Check(hud.GubbleViewOpen && hud.GubbleEye.Live, "I opens the view through the Gubble's eyes");
            var eye = hud.GubbleEyePose();
            var target = player.GlobalPosition + Vector3.Up * (player.BodyHeightM * 0.5f) - eye.Origin;
            var facing = -companion.GlobalBasis.Z;
            var yaw = Mathf.RadToDeg(new Vector2(facing.X, facing.Z).AngleTo(new Vector2(target.X, target.Z)));
            var pitch = Mathf.RadToDeg(Mathf.Atan2(target.Y, new Vector2(target.X, target.Z).Length()));
            hud.TurnView(new Vector2(Mathf.DegToRad(yaw) / RoomHud.MouseRadiansPerPixel, -Mathf.DegToRad(pitch) / RoomHud.MouseRadiansPerPixel));
            GD.Print($"GUBBLE_VIEW_CAPTURE: the player is {target.Length():0.00} m from the Gubble's eye; eye turned {hud.GubbleViewYawDeg:0.0} and tilted {hud.GubbleViewPitchDeg:0.0} degrees");
            await Frames(30);
            var viewport = hud.GubbleEye.EyeViewport!;
            var picture = await Grab(viewport);
            var screen = await Grab(GetViewport());
            Check(Variation(picture) > 0.02f, $"the Gubble's eye draws a real picture (variation {Variation(picture):0.000})");
            // The middle of the screen, clear of the label at the top and the icon at the lower right.
            var middle = new Rect2(0.1f, 0.25f, 0.7f, 0.5f);
            Check(Difference(picture, screen, middle) < 0.04f, $"the screen shows the live picture from the Gubble's eye (difference {Difference(picture, screen, middle):0.000})");
            if (output != null)
            {
                System.IO.Directory.CreateDirectory(output);
                Check(screen.SavePng(System.IO.Path.Combine(output, "gubble-view.png")) == Error.Ok, "saved the open view as gubble-view.png");
            }

            // The Gubble sees the player in F1: leaving out only the player's body layer changes the picture where the player is.
            var camera = hud.GubbleEye.EyeCamera!;
            camera.CullMask = EyeView.GubbleEyeCullMask & ~EyeView.PlayerBodyLayer;
            await Frames(4);
            var withoutBody = await Grab(viewport);
            camera.CullMask = EyeView.GubbleEyeCullMask;
            var at = camera.UnprojectPosition(player.GlobalPosition + Vector3.Up * (player.BodyHeightM * 0.5f)) / (Vector2)viewport.Size;
            var around = new Rect2(at - new Vector2(0.08f, 0.12f), new Vector2(0.16f, 0.24f));
            Check(Difference(picture, withoutBody, around) > 0.02f, $"in F1 the Gubble's eye draws the player's body ({Difference(picture, withoutBody, around):0.000} where the player stands, at {at})");

            // Live: turning the eye changes the picture.
            hud.TurnView(new Vector2(Mathf.DegToRad(40) / RoomHud.MouseRadiansPerPixel, 0));
            await Frames(4);
            var turned = await Grab(viewport);
            Check(Difference(picture, turned, new Rect2(0, 0, 1, 1)) > 0.02f, "the picture is live: it follows the Gubble's eye as it turns");

            // Back in F1: the player's own body neither shows nor casts a shadow there, as before (looking down at its feet).
            hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true });
            Check(!hud.GubbleViewOpen && !GetViewport().Disable3D, "Esc returns to the player's own eyes");
            player.TurnView(new Vector2(0, 320));
            await Frames(20);
            var first = await Grab(GetViewport());
            await Frames(2);
            var second = await Grab(GetViewport());
            var meshes = player.GetNode<Node3D>("OriginalPrototypeBody").FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().ToList();
            var cast = meshes.Select(m => m.CastShadow).ToList();
            foreach (var mesh in meshes) mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            await Frames(4);
            var noShadow = await Grab(GetViewport());
            for (var i = 0; i < meshes.Count; i++) meshes[i].CastShadow = cast[i];
            var view = new Rect2(0.05f, 0.2f, 0.9f, 0.75f);
            var noise = Difference(first, second, view);
            Check(Difference(second, noShadow, view) <= noise + 0.003f, $"in F1 the player's body casts no shadow into its own view (difference {Difference(second, noShadow, view):0.0000}, frame noise {noise:0.0000})");

            // The Gubble's words over its view: a refusal while looking reads at the top, above the label.
            hud.Cue.Think(GubbleMagic.FetchIcon);
            hud.OpenGubbleView();
            hud.Cue.Refuse("That spot is out of reach. Bring the Gubble closer and try again.");
            await Frames(10);
            var words = await Grab(GetViewport());
            Check(hud.GubbleViewBubbleText.Length > 0 && hud.GubbleViewSaysText.Length > 0, $"the Gubble's words show over its view: {hud.GubbleViewBubbleText} {hud.GubbleViewSaysText}");
            if (output != null) Check(words.SavePng(System.IO.Path.Combine(output, "gubble-view-words.png")) == Error.Ok, "saved the words over the view as gubble-view-words.png");
            hud.CloseGubbleView();

            // The journal's Discoveries and Abilities pages: their words keep within the page's width (a long page scrolls).
            hud.OpenJournal();
            await Frames(70);
            foreach (var page in new[] { Native.Ui.JournalPages.Discoveries, Native.Ui.JournalPages.Abilities })
            {
                hud.Journal.OpenPage(page);
                await Frames(3);
                var scroll = hud.Journal.FindChildren("*", nameof(ScrollContainer), true, false).OfType<ScrollContainer>().First();
                var entries = scroll.GetChild<Control>(0);
                var area = scroll.GetGlobalRect();
                var labels = entries.FindChildren("*", nameof(Label), true, false).OfType<Label>().ToList();
                GD.Print($"GUBBLE_VIEW_CAPTURE: the {page} page's words are {entries.Size.Y:0} px tall in a {area.Size.Y:0} px page ({labels.Count} lines)");
                Check(labels.All(l => l.GetGlobalRect().Position.X >= area.Position.X - 1 && l.GetGlobalRect().End.X <= area.End.X + 1 && l.GetLineCount() <= 3),
                    $"the {page} page's lines keep within the page's width");
                if (page == Native.Ui.JournalPages.Abilities && output != null)
                    Check((await Grab(GetViewport())).SavePng(System.IO.Path.Combine(output, "journal-abilities.png")) == Error.Ok, "saved the Abilities page as journal-abilities.png");
            }
            hud.CloseJournal();
            await Frames(60);

            world.Free();
            await Frames(2);
            GD.Print($"GUBBLE_VIEW_CAPTURE: {_checks}/{_checks} checks passed");
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
