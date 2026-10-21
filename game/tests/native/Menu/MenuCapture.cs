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
/// Pictures of the opening menu for the founder's eye (windowed; the GPU): the menu with two teams and the installed islands, the New
/// team panel filled in, Challenge with the copy of a Creative island on offer, the room a moment after Play (the link line), and the
/// settings. Run it on a scratch profile (APPDATA and LOCALAPPDATA under the lane's scratch, the island copied into its rooms folder), windowed:
/// Godot --path game --resolution 1280x720 res://tests/native/Menu/native_menu_capture.tscn -- --out=&lt;folder&gt; --no-playtest-record
/// Nothing here is a check; the headless suite (MenuTest) is the test.
/// </summary>
public partial class MenuCapture : Node
{
    private string _out = "";
    private int _shots;
    private MenuBoot? _boot;

    public override async void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            // The game started again by "Back to the menu" carries --menu: this stand-in says so and ends (it never starts the room again).
            if (args.Contains("--menu"))
            {
                GD.Print("MENU_CAPTURE relaunched at the menu, user arguments: " + string.Join(" ", args));
                GetTree().Quit();
                return;
            }
            _out = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?["--out=".Length..] ?? ProjectSettings.GlobalizePath("user://menu_capture");
            Directory.CreateDirectory(_out);
            // The intro hides the HUD for fifteen seconds; the pictures of the room are of the room.
            MenuProfile.Set(RoomHud.PlayIntroProfileKey, false);
            var rules = IslandRules.Load(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion);
            var layout = MenuRoots.Layout();
            var now = DateTime.UtcNow;
            if (!File.Exists(layout.TeamFile("mira")))
            {
                TeamMaker.Create(layout, rules, "Mira", "stickbear", "Pip", "#a18cc3", "quick_feet", now);
                TeamMaker.Create(layout, rules, "Theo", "potato_man", "the Gubble", "#d7b765", "cat_feet", now.AddMinutes(-5));
            }
            var islands = IslandCatalog.Scan();
            GD.Print($"MENU_CAPTURE islands: {string.Join(", ", islands.Select(i => i.RoomId))}");
            // Mira has a Creative island on the first island of the list: Challenge there offers the copy.
            var first = islands.FirstOrDefault(i => i.Directory.StartsWith("user://", StringComparison.Ordinal)) ?? islands[0];
            var creative = layout.IslandSave("mira", "creative", first.RoomId, first.ManifestPrefix);
            if (!File.Exists(creative)) { Directory.CreateDirectory(Path.GetDirectoryName(creative)!); File.WriteAllText(creative, "{}"); }

            _boot = new MenuBoot { AutoOpen = false };
            AddChild(_boot);
            var menu = _boot.Open(Array.Empty<string>());
            await Frames(8);
            await Shot("01-menu-opening");
            menu.Model.SelectTeam("mira");
            menu.Model.SelectIsland(first.RoomId);
            menu.Model.ChooseMode("challenge");
            menu.Refresh();
            await Frames(6);
            await Shot("02-choices-with-copy");
            menu.CopyButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(4);
            await Shot("03-copy-chosen");
            menu.OpenNewTeam();
            menu.NameEdit.Text = "Juniper";
            menu.Picker.Choose(menu.Picker.Row.Count > 2 ? menu.Picker.Row[2].Id : "");
            menu.GubbleNameEdit.Text = "Pop";
            menu.GubbleColourButton.EmitSignal(BaseButton.SignalName.Pressed);
            menu.KnackChoice.Select(3);
            await Frames(8);
            await Shot("04-new-team");
            menu.CloseNewTeam();
            menu.Model.ChooseMode("creative");
            menu.Refresh();
            await Frames(4);
            menu.PlayButton.EmitSignal(BaseButton.SignalName.Pressed);
            var world = _boot.World;
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); world != null && booting.Elapsed.TotalSeconds < 120 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            await Frames(90);
            await Shot("05-in-the-room");
            if (args.Contains("--back-to-menu"))
            {
                GD.Print("MENU_CAPTURE back to the menu: " + MenuBoot.BackToMenu(GetTree()));
                return;
            }
            var hud = world!.GetNode<RoomHud>("RoomHud");
            var press = new InputEventKey { PhysicalKeycode = Key.C, Pressed = true };
            hud._UnhandledInput(press);
            await Frames(8);
            await Shot("06-settings");
            hud._UnhandledInput(press);
            // An AI joins: the companion's own client (companion/, the link the Python server uses) connects for a while; the line says so.
            var note = world.GetNode<GubbleLinkNote>("GubbleLinkNote");
            var session = ProjectSettings.GlobalizePath("user://companion/session.json");
            var companion = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\').Replace('\\', '/');
            companion = companion[..companion.LastIndexOf('/')] + "/companion";
            var start = new System.Diagnostics.ProcessStartInfo("uv") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var part in new[] { "run", "--project", companion, "--locked", "--quiet", "python", "-I", "-c", ClientCode, session, "30" }) start.ArgumentList.Add(part);
            using var client = System.Diagnostics.Process.Start(start);
            for (var waiting = System.Diagnostics.Stopwatch.StartNew(); waiting.Elapsed.TotalSeconds < 60 && note.Status != GubbleLinkNote.Connected;) await Frames(10);
            GD.Print($"MENU_CAPTURE link: {note.Status} / showing \"{note.Words}\"");
            await Frames(20);
            await Shot("07-connected");
            GD.Print($"MENU_CAPTURE link after: showing \"{note.Words}\"");
            GD.Print($"MENU_CAPTURE done: {_shots} pictures in {_out}");
        }
        catch (Exception error)
        {
            GD.Print("MENU_CAPTURE failed: " + error);
        }
        GetTree().Quit();
    }

    private const string ClientCode = "import asyncio, sys\nfrom pathlib import Path\nfrom garage_game_companion.link import LinkClient\nasync def main():\n    c = LinkClient.from_file(Path(sys.argv[1]))\n    await c.connect()\n    await asyncio.sleep(float(sys.argv[2]))\n    await c.close()\nasyncio.run(main())\n";

    private async Task Shot(string name)
    {
        await Frames(2);
        var image = GetViewport().GetTexture().GetImage();
        var path = Path.Combine(_out, name + ".png");
        image.SavePng(path);
        _shots++;
        GD.Print($"MENU_CAPTURE {name}: {image.GetWidth()}x{image.GetHeight()}");
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
