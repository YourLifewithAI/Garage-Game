using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// Pictures of the title page (Run 4, MENU-4) for the founder's eye: windowed, on the GPU, never headless (a headless run walks
/// the same states and takes no picture). Run it through its script, which waits for a quiet GPU's owner (no game window open),
/// sets DOTNET_ROOT, keeps to a scratch user folder and names the stand-in land:
/// <code>
/// pwsh -NoProfile -File game/tests/native/Menu/capture-title.ps1 -OutDir &lt;folder&gt;
/// </code>
/// <list type="bullet">
/// <item>Nothing of the player's is read or written: the teams and saves are made in a scratch user folder (user://tests/title_capture,
/// itself under the script's scratch APPDATA), and the lands are the shipped stand-ins, a made-up land of the scene's own
/// ("Sample land": walls, a door, a window, a dozen things), and the room folder named with --room=&lt;folder&gt; (a fixture: the
/// script passes the garage stand-in of tools/landscape-fixture.ps1; never a real room).</item>
/// <item><b>The before picture</b> is 00-before-todays-page.png: the Choose page, the page the game opened on before the title page,
/// on the same scratch teams and the same land. (The older native_menu_capture.tscn reads the real user folder unless APPDATA is
/// redirected, so it is not the way to take it.)</item>
/// <item>One picture per state of the title page: a game to continue at morning, dawn, dusk and night (the look's own clock pins),
/// New game alone, New game's question, "Bring my game here", the buttons waiting on a game still closing, the longest names with
/// a notice (the page given way), a land with no map, the paper without the hour's wash, and the Choose page behind "Choose".</item>
/// <item><b>The four states, each with its before</b> (<c>--set=four</c>; the script's <c>-Set four</c>): no saved game, a game to
/// continue, New game's question over that game, and the Choose page, first on the page the game opened on before the title page
/// (N-...-before.png) and then on the title page (N-...-after.png). They are taken on a stand-in user folder as a family's machine
/// has one: its rooms folder holds the land the script names, copied in as the exporter installs a room, beside the shipped test
/// room, and it has two teams, one with a game on that land and one with none. A picture whose page is not in the state its name
/// says fails the run, headless too, so no picture is ever of something else.</item>
/// </list>
/// The page's rules are the headless suite's to check (MenuTest); this scene checks only that each of the four states is the one
/// its picture names.
/// </summary>
public partial class MenuTitleCapture : Node
{
    private string _out = "";
    private int _shots;
    private MenuBoot? _boot;

    private const string Day = "2026-11-24";
    /// <summary>The four states with their befores, and nothing else.</summary>
    public const string FourArgument = "--set=four";
    /// <summary>The hour every picture of the four states is taken at: the pairs differ in the page, not in the light.</summary>
    private const string FourHour = "09:30";
    /// <summary>A stand-in room is a few megabytes; a folder larger than this is not one, and is not copied.</summary>
    private const long MaxInstallBytes = 64L * 1024 * 1024;

    /// <summary>A made-up land: a room six metres by seven with a door, a window and a dozen things, some against a wall, some on another.</summary>
    private static string SampleLand(string roomId, string name)
    {
        static string List(params double[] values) => "[" + string.Join(",", values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        static string Wall(string id, double x0, double z0, double x1, double z1, double height) =>
            "{\"id\":\"shell:" + id + "\",\"role\":\"wall\",\"geometry\":{\"kind\":\"polygon\",\"points_m\":[" + List(x0, 0, z0) + "," + List(x1, 0, z1) + "," + List(x1, height, z1) + "," + List(x0, height, z0) + "],\"thickness_m\":0.1}}";
        static string Thing(string id, double x, double y, double z, double sx, double sy, double sz, double yaw, string hex) =>
            "{\"id\":\"obj:" + id + "\",\"position_m\":" + List(x, y, z) + ",\"size_m\":" + List(sx, sy, sz) + ",\"yaw_deg\":" + List(yaw).Trim('[', ']') + ",\"colours\":[{\"hex\":\"" + hex + "\"}]}";
        var parts = new[]
        {
            "{\"id\":\"shell:floor\",\"role\":\"floor\",\"geometry\":{\"kind\":\"polygon\",\"points_m\":[[-3,0,-3.5],[3,0,-3.5],[3,0,3.5],[-3,0,3.5]],\"thickness_m\":0.1}}",
            Wall("wall_n", -3, -3.5, 3, -3.5, 2.6), Wall("wall_s", -3, 3.5, 3, 3.5, 2.6), Wall("wall_e", 3, -3.5, 3, 3.5, 2.6), Wall("wall_w", -3, -3.5, -3, 3.5, 2.6),
        };
        var openings = new[]
        {
            "{\"id\":\"door\",\"kind\":\"door\",\"host_part_id\":\"shell:wall_s\",\"center_m\":[1.4,1,3.5],\"size_m\":[0.9,2]}",
            "{\"id\":\"window\",\"kind\":\"window\",\"host_part_id\":\"shell:wall_n\",\"center_m\":[-0.8,1.5,-3.5],\"size_m\":[1.4,1]}",
            "{\"id\":\"big_door\",\"kind\":\"garage_door\",\"host_part_id\":\"shell:wall_e\",\"center_m\":[3,1.1,0.6],\"size_m\":[2.4,2.2]}",
        };
        var things = new[]
        {
            Thing("bench", -1.2, 0, -3.05, 2.4, 0.9, 0.8, 0, "#8a6a45"), Thing("jar", -1.8, 0.9, -3.05, 0.2, 0.25, 0.2, 0, "#5f8fa3"), Thing("box_on_bench", -0.7, 0.9, -3.0, 0.5, 0.3, 0.4, 12, "#b08a52"),
            Thing("shelf", -2.75, 0, 0.4, 0.45, 1.8, 1.6, 0, "#6f7a58"), Thing("cabinet", -2.7, 0, 2.6, 0.55, 1.2, 0.9, 0, "#9a9186"),
            Thing("chair", 0.3, 0, -1.2, 0.6, 0.9, 0.6, 25, "#a4533d"), Thing("table", 0.9, 0, 0.9, 1.4, 0.75, 0.8, -8, "#7b5a3c"), Thing("mug", 1.1, 0.75, 0.8, 0.1, 0.1, 0.1, 0, "#d9c9a3"),
            Thing("tote", -0.9, 0, 1.9, 0.7, 0.45, 0.5, 40, "#3f6f8a"), Thing("bin", 2.5, 0, -2.9, 0.5, 0.7, 0.5, 0, "#4f5f4a"), Thing("crate", 2.2, 0, 2.7, 0.8, 0.5, 0.6, 15, "#b5723c"),
            Thing("ball", -0.2, 0, 0.2, 0.25, 0.25, 0.25, 0, "#c7a23a"),
        };
        var source = "{\"bounds\":{\"min_m\":[-3,0,-3.5],\"max_m\":[3,2.6,3.5]},\"shell\":{\"parts\":[" + string.Join(",", parts) + "],\"openings\":[" + string.Join(",", openings) + "]},\"objects\":[" + string.Join(",", things) + "]}";
        return "{\"schema\":\"garage_game.room\",\"version\":1,\"room_id\":\"" + roomId + "\",\"display_name\":" + System.Text.Json.JsonSerializer.Serialize(name) + ",\"extensions\":{\"x_landscape_source\":" + source + "}}";
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
    }

    public override async void _Ready()
    {
        var code = 1;
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
            var named = args.FirstOrDefault(a => a.StartsWith("--room=", StringComparison.Ordinal))?["--room=".Length..];
            if (args.Contains(FourArgument)) await FourStates(scratch, scratchPath, named);
            else await EveryState(scratch, scratchPath, named);
            GD.Print($"TITLE_CAPTURE done: {_shots} pictures in {_out}");
            code = 0;
        }
        catch (Exception error)
        {
            GD.Print("TITLE_CAPTURE failed: " + error);
        }
        MainMenu.LaunchArguments = null;
        GetTree().Quit(code);
    }

    /// <summary>Every state the title page has, one picture each, and one before picture.</summary>
    private async Task EveryState(string scratch, string scratchPath, string? named)
    {
        // The lands: the scene's own two (a plain one and one with the longest name a land can have), and the shipped stand-ins.
        WriteText(Path.Combine(scratchPath, "lands", "sample_land", "room.json"), SampleLand("sample_land", "Sample land"));
        WriteText(Path.Combine(scratchPath, "lands", "long_land", "room.json"), SampleLand("long_land", "The Wide And Windy Workbench Tablelands"));
        IslandCatalog.RootsOverride = new[] { scratch + "/lands", "res://rooms" };
        var room = string.IsNullOrWhiteSpace(named) ? scratch + "/lands/sample_land" : named.Replace((char)92, '/');
        var rules = IslandRules.Load(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion);
        var layout = MenuRoots.Layout();
        var now = DateTime.UtcNow;
        TeamMaker.Create(layout, rules, "Mira", "stickbear", "Pip", "#a18cc3", "quick_feet", now);
        TeamMaker.Create(layout, rules, "Theo", "potato_man", "the Gubble", "#d7b765", "cat_feet", now.AddMinutes(-5));
        TeamMaker.Create(layout, rules, "Nest", "", "Wisp", "#65b9b0", "", now.AddMinutes(-10));
        var explorers = TeamMaker.Create(layout, rules, "The Long Named Explorers Of The Far Wall", "", "Wisp", "#65b9b0", "", now.AddMinutes(-15));
        var entry = IslandCatalog.Read(room, shipped: room.StartsWith("res://", StringComparison.Ordinal)) ?? throw new InvalidOperationException("no room at " + room);
        var longLand = IslandCatalog.Read(scratch + "/lands/long_land", shipped: false)!;
        var other = IslandCatalog.Read("res://rooms/test_room", shipped: true)!;
        // Mira has a game on the land pictured; Nest's is on another land ("Bring my game here"); the explorers' is on the long-named land; Theo has none.
        Game(layout, "mira", "creative", entry);
        Game(layout, "nest", "creative", other);
        Game(layout, explorers.TeamId, "challenge", longLand);

        // Today's page, the before picture: the Choose page as the game opened on it before the title page.
        await Before("mira", room);
        await Shot("00-before-todays-page");

        await Show("01-continue-morning", "mira", room, "creative", "09:30");
        await Show("02-continue-dawn", "mira", room, "creative", "06:45");
        await Show("03-continue-dusk", "mira", room, "creative", "16:50");
        await Show("04-continue-night", "mira", room, "creative", "22:00");
        await Show("05-new-game-alone", "theo", room, "creative", "12:00");
        var asking = await Show("06-new-game-question", "mira", room, "creative", "09:30", picture: false);
        asking.ConfirmGuardMs = 600_000;
        asking.NewGameButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(6);
        await Shot("06-new-game-question", asking);
        asking.GoBackButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Show("07-bring-my-game", "nest", room, "creative", "15:00");
        using (var leaving = PlayLease.TryPlay(layout, "mira"))
            await Show("08-still-closing", "mira", room, "creative", "09:30");
        var given = await Show("09-longest-names", explorers.TeamId, scratch + "/lands/long_land", "challenge", "20:10", picture: false);
        given.Model.SetNotice("That game is still open, so it was not touched. Close it and try again, please.");
        given.Refresh();
        await Frames(6);
        await Shot("09-longest-names", given);
        await Show("10-no-map", "theo", "res://rooms/test_room", "creative", "12:00");
        await Show("11-night-plain-paper", "mira", room, "creative", "22:00", TitleScreen.NoHourWashArgument);
        await Show("12-choose-page", "mira", room, "creative", "09:30", picture: false);
        _boot!.ShowChoose();
        await Frames(8);
        await Shot("12-choose-page");
    }

    /// <summary>
    /// The four states the founder is shown, each on the page the game opened on before the title page (the Choose page) and then on
    /// the title page, on a stand-in user folder: two lands (the one installed in its rooms folder and the shipped test room) and
    /// two teams (Mira, with a Creative game on the installed land; Theo, with none).
    /// </summary>
    private async Task FourStates(string scratch, string scratchPath, string? named)
    {
        var land = Install(scratch, scratchPath, named);
        // The roots a real user folder has: its own rooms first, then the rooms shipped with the game.
        IslandCatalog.RootsOverride = new[] { scratch + "/rooms", "res://rooms" };
        var rules = IslandRules.Load(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion);
        var layout = MenuRoots.Layout();
        var now = DateTime.UtcNow;
        TeamMaker.Create(layout, rules, "Mira", "stickbear", "Pip", "#a18cc3", "quick_feet", now);
        TeamMaker.Create(layout, rules, "Theo", "potato_man", "the Gubble", "#d7b765", "cat_feet", now.AddMinutes(-5));
        var entry = IslandCatalog.Read(land, shipped: false) ?? throw new InvalidOperationException("no room at " + land);
        Game(layout, "mira", "creative", entry);
        var lands = IslandCatalog.Scan();
        Must(lands.Count >= 2 && lands.Any(l => l.RoomId == entry.RoomId && !l.Shipped) && lands.Any(l => l.RoomId == "test_room" && l.Shipped),
            "the stand-in user folder holds the installed land and the shipped test room: " + string.Join(", ", lands.Select(l => l.RoomId)));
        GD.Print($"TITLE_CAPTURE lands: {string.Join(", ", lands.Select(l => l.RoomId + (l.Shipped ? " (shipped)" : " (installed)")))}; teams: mira (a Creative game on {entry.RoomId}), theo (no game)");

        // Before: the page the game opened on, with the start rule's buttons where Play was (MENU-3).
        var old = await Before("theo", land);
        Must(old.Model.TeamId == "theo" && old.Model.RoomId == entry.RoomId && old.NewGameButton.Visible && !old.ContinueButton.Visible && !old.Model.Asking, "before, no saved game: New game and no Continue");
        await Shot("1-no-saved-game-before", Words(old));
        old = await Before("mira", land);
        Must(old.Model.TeamId == "mira" && old.ContinueButton.Visible && old.NewGameButton.Visible && !old.Model.Asking, "before, a game to continue: Continue and New game");
        await Shot("2-continue-before", Words(old));
        old.ConfirmGuardMs = 600_000;
        old.NewGameButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(6);
        Must(old.Model.Asking && old.GoBackButton.Visible && !old.ContinueButton.Visible, "before, New game's question: the question is up");
        await Shot("3-new-game-question-before", Words(old));
        old.GoBackButton.EmitSignal(BaseButton.SignalName.Pressed);
        old = await Before("mira", land);
        Must(old.Model.TeamId == "mira" && !old.Model.Asking && old.IslandButtons.Count == lands.Count, "before, the Choose page: every land has its card");
        await Shot("4-choose-page-before", Words(old));

        // After: the title page, and the Choose page behind its "Choose".
        var page = await Show("1-no-saved-game-after", "theo", land, "creative", FourHour, picture: false);
        Must(page.Model.TeamId == "theo" && page.NewGameButton.Visible && !page.ContinueButton.Visible && !page.BringButton.Visible && !page.Model.Asking, "after, no saved game: New game alone");
        await Shot("1-no-saved-game-after", page);
        page = await Show("2-continue-after", "mira", land, "creative", FourHour, picture: false);
        Must(page.Model.TeamId == "mira" && page.ContinueButton.Visible && page.NewGameButton.Visible && !page.Model.Asking, "after, a game to continue: Continue and New game");
        await Shot("2-continue-after", page);
        page.ConfirmGuardMs = 600_000;
        page.NewGameButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(6);
        Must(page.Model.Asking && page.GoBackButton.Visible && !page.ContinueButton.Visible, "after, New game's question: the question is up");
        await Shot("3-new-game-question-after", page);
        page.GoBackButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Show("4-choose-page-after", "mira", land, "creative", FourHour, picture: false);
        var choose = _boot!.ShowChoose();
        await Frames(8);
        Must(_boot.Title == null && choose.Model.TeamId == "mira" && choose.ContinueButton.Visible && choose.IslandButtons.Count == lands.Count, "after, the Choose page: behind \"Choose\", on the same choices");
        await Shot("4-choose-page-after", Words(choose));
    }

    /// <summary>A picture is of the state its name says, or the run fails: said in the run's words either way.</summary>
    private static void Must(bool holds, string state)
    {
        if (!holds) throw new InvalidOperationException("not the state its picture names: " + state);
        GD.Print("TITLE_CAPTURE state: " + state);
    }

    /// <summary>
    /// The stand-in user folder's rooms folder, holding the land the script names as the exporter installs a room: the folder copied
    /// whole (a fixture's files are read-only; the copies are not, so the next run can clear them). With no land named it holds the
    /// scene's own made-up one. Returns the installed land's folder.
    /// </summary>
    private static string Install(string scratch, string scratchPath, string? named)
    {
        if (string.IsNullOrWhiteSpace(named))
        {
            WriteText(Path.Combine(scratchPath, "rooms", "sample_land", "room.json"), SampleLand("sample_land", "Sample land"));
            return scratch + "/rooms/sample_land";
        }
        var from = ProjectSettings.GlobalizePath(named.Replace((char)92, '/'));
        var entry = IslandCatalog.Read(from, shipped: false) ?? throw new InvalidOperationException("no room at " + from);
        var to = Path.Combine(scratchPath, "rooms", entry.RoomId);
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(file).Length;
            if (bytes > MaxInstallBytes) throw new InvalidOperationException($"{from} holds more than {MaxInstallBytes >> 20} MB: not a stand-in room this scene installs");
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetAttributes(target, FileAttributes.Normal);
        }
        return scratch + "/rooms/" + entry.RoomId;
    }

    /// <summary>The page the game opened on before the title page: the Choose page, on what a launch names.</summary>
    private async Task<MainMenu> Before(string team, string room)
    {
        if (_boot != null) { _boot.QueueFree(); _boot = null; await Frames(2); }
        var arguments = new[] { "--team=" + team, "--room=" + room, "--mode=creative" };
        MainMenu.LaunchArguments = () => arguments;
        _boot = new MenuBoot { AutoOpen = false };
        AddChild(_boot);
        var page = _boot.Open(arguments);
        await Frames(8);
        return page;
    }

    /// <summary>A game as a played one leaves it: the mode's save on that land (the menu reads that it is there, not what is in it).</summary>
    private static void Game(TeamLayout layout, string team, string mode, IslandEntry land)
    {
        var save = layout.IslandSave(team, mode, land.RoomId, land.ManifestPrefix);
        WriteText(save, mode == "challenge" ? "{\"team\":{\"team_id\":\"" + team + "\",\"mode\":\"challenge\",\"difficulty\":\"standard\"}}" : "{}");
    }

    private async Task<TitleScreen> Show(string name, string team, string room, string mode, string clock, string? extra = null, bool picture = true)
    {
        if (_boot != null) { _boot.QueueFree(); _boot = null; await Frames(2); }
        var arguments = new List<string> { "--team=" + team, "--room=" + room, "--mode=" + mode, "--look-clock=" + clock, "--look-date=" + Day };
        if (extra != null) arguments.Add(extra);
        MainMenu.LaunchArguments = () => arguments;
        _boot = new MenuBoot { AutoOpen = false };
        AddChild(_boot);
        var page = _boot.OpenTitle(arguments);
        await Frames(8);
        if (picture) await Shot(name, page);
        return page;
    }

    /// <summary>What the title page shows, in the run's words.</summary>
    private static string Words(TitleScreen page) =>
        $"land=\"{page.LandName.Text}\" strokes={page.Picture.DrawnStrokes.Count} hour=\"{page.HourLine.Text}\" window={page.Dial.Body} continue={page.ContinueButton.Visible} bring={page.BringButton.Visible} " +
        $"shut={page.NewGameButton.Disabled} asking={page.Model.Asking} given_way={page.Tightness} needs={page.Needs:0} of {page.Has:0}";

    /// <summary>What the Choose page shows, in the run's words.</summary>
    private static string Words(MainMenu page) =>
        $"the Choose page: team={page.Model.TeamId} land={page.Model.RoomId} mode={page.Model.Mode} lands={page.IslandButtons.Count} teams={page.TeamButtons.Count} continue={page.ContinueButton.Visible} " +
        $"new_game={page.NewGameButton.Visible} asking={page.Model.Asking}";

    private Task Shot(string name, TitleScreen? page = null) => Shot(name, page == null ? "the Choose page" : Words(page));

    private async Task Shot(string name, string words)
    {
        await Frames(2);
        if (DisplayServer.GetName() == "headless") { GD.Print($"TITLE_CAPTURE {name}: no picture (headless has none); {words}"); return; }
        var image = GetViewport().GetTexture().GetImage();
        if (image == null || image.IsEmpty()) { GD.Print($"TITLE_CAPTURE {name}: no picture (the window gave none); {words}"); return; }
        image.SavePng(Path.Combine(_out, name + ".png"));
        _shots++;
        GD.Print($"TITLE_CAPTURE {name}: {image.GetWidth()}x{image.GetHeight()}; {words}");
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
