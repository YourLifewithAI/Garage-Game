using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// Pictures of the title page (Run 4, MENU-4) for the founder's eye: windowed, on the GPU, never headless (a headless run has no
/// picture to take). It keeps to a scratch user folder (nothing of the player's is read or written) and to the stand-in rooms: the
/// shipped ones (res://rooms), and a room folder named with --room=&lt;folder&gt; (a fixture: the landscape fixtures of
/// tools/landscape-fixture.ps1, never the founder's own rooms). One picture per state: a game to continue at dawn, morning, dusk and
/// night (the look's own clock pins), a new start with no game, New game's question, and the Choose page behind it.
/// <code>
/// Godot --path game --resolution 1280x720 res://tests/native/Menu/native_menu_title_capture.tscn -- --out=&lt;folder&gt; [--room=&lt;fixture room folder&gt;]
/// </code>
/// The picture of today's Choose page (the "before" set) is native_menu_capture.tscn's first picture, or any launch with --choose.
/// Nothing here is a check; the headless suite (MenuTest) is the test.
/// </summary>
public partial class MenuTitleCapture : Node
{
    private string _out = "";
    private int _shots;
    private MenuBoot? _boot;

    public override async void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            _out = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?["--out=".Length..] ?? ProjectSettings.GlobalizePath("user://title_capture");
            Directory.CreateDirectory(_out);
            var scratch = "user://tests/title_capture";
            var scratchPath = ProjectSettings.GlobalizePath(scratch);
            if (Directory.Exists(scratchPath)) Directory.Delete(scratchPath, recursive: true);
            Directory.CreateDirectory(scratchPath);
            TeamBoot.UserRootOverride = scratch;
            RoomHud.ProfilePath = scratch + "/profile/avatar_profile_v1.cfg";
            IslandCatalog.RootsOverride = new[] { "res://rooms" };
            var room = args.FirstOrDefault(a => a.StartsWith("--room=", StringComparison.Ordinal))?["--room=".Length..] ?? "test_room";
            var rules = IslandRules.Load(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion);
            var layout = MenuRoots.Layout();
            var now = DateTime.UtcNow;
            TeamMaker.Create(layout, rules, "Mira", "stickbear", "Pip", "#a18cc3", "quick_feet", now);
            TeamMaker.Create(layout, rules, "Theo", "potato_man", "the Gubble", "#d7b765", "cat_feet", now.AddMinutes(-5));
            // Mira has a game here (a Creative save, as MenuCapture makes one); Theo has none.
            var entry = IslandCatalog.Read(room.Contains('/') || room.Contains((char)92) ? room : "res://rooms/" + room, shipped: room.StartsWith("res://", StringComparison.Ordinal)) ?? throw new InvalidOperationException("no room at " + room);
            var save = layout.IslandSave("mira", "creative", entry.RoomId, entry.ManifestPrefix);
            Directory.CreateDirectory(Path.GetDirectoryName(save)!);
            File.WriteAllText(save, "{}");

            await Show("01-continue-morning", "mira", entry, room, "09:30");
            await Show("02-continue-dawn", "mira", entry, room, "06:30");
            await Show("03-continue-dusk", "mira", entry, room, "18:15");
            await Show("04-continue-night", "mira", entry, room, "22:00");
            await Show("05-new-game-alone", "theo", entry, room, "12:00");
            var asking = await Show("06-new-game-question", "mira", entry, room, "09:30");
            asking.ConfirmGuardMs = 600_000;
            asking.NewGameButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(4);
            await Shot("06-new-game-question");
            asking.GoBackButton.EmitSignal(BaseButton.SignalName.Pressed);
            _boot!.ShowChoose();
            await Frames(8);
            await Shot("07-choose-page");
            GD.Print($"TITLE_CAPTURE done: {_shots} pictures in {_out}");
        }
        catch (Exception error)
        {
            GD.Print("TITLE_CAPTURE failed: " + error);
        }
        GetTree().Quit();
    }

    private async Task<TitleScreen> Show(string name, string team, IslandEntry entry, string room, string clock)
    {
        _boot?.QueueFree();
        var arguments = new[] { "--team=" + team, "--room=" + room, "--mode=creative", "--look-clock=" + clock, "--look-date=2026-11-24" };
        MainMenu.LaunchArguments = () => arguments;
        _boot = new MenuBoot { AutoOpen = false };
        AddChild(_boot);
        var page = _boot.OpenTitle(arguments);
        await Frames(8);
        await Shot(name);
        GD.Print($"TITLE_CAPTURE {name}: continue={page.ContinueButton.Visible} land=\"{page.LandName.Text}\" strokes={page.Picture.DrawnStrokes.Count}");
        return page;
    }

    private async Task Shot(string name)
    {
        await Frames(2);
        if (DisplayServer.GetName() == "headless") { GD.Print($"TITLE_CAPTURE {name}: no picture (headless has none)"); return; }
        var image = GetViewport().GetTexture().GetImage();
        if (image == null || image.IsEmpty()) { GD.Print($"TITLE_CAPTURE {name}: no picture (headless has none)"); return; }
        image.SavePng(Path.Combine(_out, name + ".png"));
        _shots++;
        GD.Print($"TITLE_CAPTURE {name}: {image.GetWidth()}x{image.GetHeight()}");
    }

    private Task Frames(int count)
    {
        var source = new TaskCompletionSource();
        var remaining = count;
        void Step()
        {
            if (--remaining > 0) return;
            GetTree().ProcessFrame -= Step;
            source.SetResult();
        }
        GetTree().ProcessFrame += Step;
        return source.Task;
    }
}
