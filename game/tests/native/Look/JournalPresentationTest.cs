using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;
using Godot;

namespace GarageGame.Tests.Look;

/// <summary>
/// A manual capture script for the journal book's look (Codex's GPU fixture of 12 October, kept for the founder's eye). It is in
/// no runner: the book's behaviour is tested headless in the play HUD suite (PlayHudTest.Journal.cs). Run it windowed on a GPU,
/// when nobody is playing, to save nine frames of the book (the dock, the flight, the cover opening, the index, three pages, the
/// cover closing, the return):
///   Godot --path game --fixed-fps 60 --resolution 1280x720 res://tests/native_journal_presentation.tscn -- --capture-dir=ABSOLUTE_DIR [--island=ROOM_DIR]
/// Without --island it opens the default room. Its saves and profiles stay under user://tests/journal_presentation and are
/// removed when it ends; it never touches the player's own.
/// </summary>
public partial class JournalPresentationTest : Node
{
    private const string Root = "user://tests/journal_presentation";
    private int _checks;
    private string? _output;
    private RoomHud _hud = null!;
    private JournalPresentation _book = null!;

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

    private void KeyPress(Key key) => _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = key, Pressed = true });

    /// <summary>A left click through the viewport, in its own coordinates, as the GUI receives a real one.</summary>
    private async Task Click(Control control)
    {
        var point = control.GetGlobalRect().GetCenter();
        GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Frames(1);
    }

    private async Task Capture(string name)
    {
        if (_output == null || DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        System.IO.Directory.CreateDirectory(_output);
        using var image = GetViewport().GetTexture().GetImage();
        Check(image.SavePng(System.IO.Path.Combine(_output, name + ".png")) == Error.Ok, "captured " + name);
    }

    private static void RemoveRoot()
    {
        var directory = ProjectSettings.GlobalizePath(Root);
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
        catch (System.IO.IOException error) { GD.Print("JOURNAL_PRESENTATION: could not remove its saves: " + error.Message); }
    }

    public override async void _Ready()
    {
        var previous = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        try
        {
            var args = OS.GetCmdlineUserArgs();
            _output = args.FirstOrDefault(a => a.StartsWith("--capture-dir=", StringComparison.Ordinal))?[14..];
            var room = args.FirstOrDefault(a => a.StartsWith("--island=", StringComparison.Ordinal))?[9..];
            RemoveRoot();
            CommandHost.SaveRoot = Root + "/saves";
            CommandHost.DefaultGlowProgressPath = Root + "/profile/glow_progress_v1.json";
            CommandHost.DefaultPlayerVitalsPath = Root + "/profile/player_vitals_v1.json";
            CommandHost.DefaultWishSaveRoot = Root + "/saves/wishes";
            RoomHud.ProfilePath = Root + "/profile/avatar_profile_v1.cfg";
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            if (room != null) world.RoomDirectory = room;
            AddChild(world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            Check(world.WorldReady, "the room loads: " + world.LoadError);
            world.Player.ReadKeyboard = false;
            _hud = world.GetNode<RoomHud>("RoomHud");
            _hud.SetViewMode(1);
            _book = _hud.Book;
            await Frames(90);
            Check(_book.Dock.Visible && !_hud.JournalOpen, "the closed journal is a visible book in its dock");
            var dock = _book.Dock.GetGlobalRect();
            var screen = GetViewport().GetVisibleRect();
            Check(Mathf.Abs(screen.End.X - dock.End.X - 18) < 1 && Mathf.Abs(screen.End.Y - dock.End.Y - 18) < 1, "the book rests 18 pixels from the lower-right edges");
            await Capture("01-journal-dock");
            await Click(_book.Dock);
            Check(_book.WantsOpen && !world.Player.InputEnabled, "a mouse click on the book starts opening it and holds the body");
            while (_book.Progress < 0.28f) await Frames(1);
            await Capture("02-book-expands");
            while (_book.Progress < 0.65f) await Frames(1);
            await Capture("03-cover-opens");
            while (_book.Progress < 1) await Frames(1);
            Check(_book.Panel.Visible && _book.Panel.Page == JournalPanel.IndexPage && !_book.Dock.Visible, "opening finishes at the index with one visible book");
            await Capture("04-journal-index");
            var abilities = _book.Panel.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text.Contains(JournalPages.Abilities, StringComparison.Ordinal));
            await Click(abilities);
            Check(_book.Panel.Page == JournalPages.Abilities, "an index entry opens its page through a mouse click");
            await Frames(3);
            await Capture("05-abilities");
            _book.Panel.OpenPage(JournalPages.Character);
            await Frames(3);
            await Capture("06-character");
            _book.Panel.OpenPage(JournalPages.Map);
            await Frames(3);
            await Capture("07-map");
            var close = (Button)_book.Panel.FindChild("JournalCloseButton", true, false)!;
            await Click(close);
            Check(!_book.WantsOpen && _hud.JournalOpen && !world.Player.InputEnabled, "the close link keeps the body held during the return");
            while (_book.Progress > 0.65f) await Frames(1);
            await Capture("08-cover-closes");
            while (_book.Progress > 0.22f) await Frames(1);
            await Capture("09-book-returns");
            while (_book.Progress > 0) await Frames(1);
            Check(_book.Dock.Visible && !_hud.JournalOpen && world.Player.InputEnabled, "closing returns the book to its dock and the body moves again");
            world.Free();
            await Frames(2);
            GD.Print($"JOURNAL_PRESENTATION: {_checks}/{_checks} checks passed");
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
