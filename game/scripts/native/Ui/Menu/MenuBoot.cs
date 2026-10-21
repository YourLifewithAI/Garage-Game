using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>Where the menu looks for the machine's teams: the same user folder the game's boot uses (TeamBoot), so a team made here is the one a boot opens.</summary>
public static class MenuRoots
{
    public static TeamLayout Layout() => new(ProjectSettings.GlobalizePath(TeamBoot.UserRootOverride ?? "user://"));
}

/// <summary>
/// The game's main scene (scenes/menu.tscn): a NativeGameBoot that opens the opening menu when the game starts without a room (or with
/// <c>--menu</c>), and otherwise boots the room straight away exactly as NativeGameBoot does (so run-room.ps1 -Room and the founder's
/// shortcut still go straight in, and so does any boot check). Being a NativeGameBoot, the room it starts is the game's own boot:
/// the team's boot (TeamBoot) honours the team, island, mode and difficulty the menu chose, through the same arguments --team=, --mode=
/// and --difficulty= take, and the HUD sees the game's boot for the window it remembers.
/// <list type="bullet">
/// <item>The menu does not open in a headless run or a boot check (--quit-after-ready=) unless --menu names it: those never wait on a person.</item>
/// <item>With <c>--menu</c> the team, room, mode and difficulty named on the command line are the menu's first choices.</item>
/// <item>Going back from a room to the menu restarts the game; the room is not torn down in place (a follow-up).</item>
/// </list>
/// </summary>
public partial class MenuBoot : NativeGameBoot
{
    public const string MenuArgument = "--menu";
    /// <summary>False in a test that opens the menu itself (Open) instead of letting the scene decide.</summary>
    public bool AutoOpen { get; set; } = true;
    public MainMenu? Menu { get; private set; }
    public MenuModel? Model { get; private set; }
    public RoomWorld? World { get; private set; }
    private IReadOnlyList<CharacterChoice>? _characters;

    /// <summary>Whether a launch opens the menu: asked for, or no room named; never headless or a boot check on its own, never the release probe.</summary>
    public static bool ShowsMenu(IReadOnlyCollection<string> arguments, string displayServer)
    {
        if (arguments.Contains("--native-contract-probe")) return false;
        if (arguments.Contains(MenuArgument)) return true;
        if (displayServer == "headless" || arguments.Any(a => a.StartsWith("--quit-after-ready=", StringComparison.Ordinal))) return false;
        return !arguments.Any(a => a.StartsWith("--room=", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a["--room=".Length..]));
    }

    private IReadOnlyList<string> _openedWith = Array.Empty<string>();
    private string _comeBackWith = "";

    /// <summary>
    /// A room that cannot be loaded (a manifest that fails its checks, a folder gone) must not leave a blank window: the menu comes
    /// back, with a plain word about it, and the island is left as it was.
    /// </summary>
    public override void _Process(double delta)
    {
        if (World is not { } world || !IsInstanceValid(world) || world.WorldReady || world.LoadError.Length == 0) return;
        var why = KernelText.DisplayText(world.LoadError, 140);
        RemoveChild(world);
        world.QueueFree();
        World = null;
        _comeBackWith = $"That island could not be opened ({why}). It was left as it was.";
        GD.Print("MENU_BACK_FROM_FAILED_ROOM " + why);
        Open(_openedWith);
    }

    public override void _Ready()
    {
        if (!AutoOpen) return;
        var arguments = OS.GetCmdlineUserArgs();
        if (!ShowsMenu(arguments, DisplayServer.GetName())) { base._Ready(); return; }
        Open(arguments);
    }

    /// <summary>The menu, over this machine's teams and islands, with the command line's choices made first.</summary>
    public MainMenu Open(IReadOnlyList<string> arguments)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DisplayServer.WindowSetTitle("Garage Game");
        if (RoomHud.StartsFullscreen(DisplayServer.GetName(), arguments.ToArray(), true, RoomHud.LoadFullscreen(RoomHud.ProfilePath)) && DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowSetCurrentScreen(DisplayServer.GetPrimaryScreen());
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        }
        IslandRules? rules = null;
        try { rules = IslandRules.Load(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion); }
        catch (IslandRulesException error) { GD.PushWarning("MENU: the island rules could not be read: " + error.Message); }
        var model = new MenuModel(MenuRoots.Layout(), rules, IslandCatalog.Scan());
        Preselect(model, arguments);
        _openedWith = arguments;
        if (_comeBackWith.Length > 0) { model.SetNotice(_comeBackWith); _comeBackWith = ""; }
        if (Menu != null && IsInstanceValid(Menu)) { RemoveChild(Menu); Menu.QueueFree(); }
        Model = model;
        Menu = new MainMenu { Model = model, Characters = ShippedCharacters, Launch = launch => StartRoom(launch), QuitGame = () => GetTree().Quit() };
        AddChild(Menu);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MENU_OPEN teams={model.Teams.Count} islands={model.Islands.Count} in {clock.Elapsed.TotalMilliseconds:0} ms"));
        return Menu;
    }

    /// <summary>The shipped characters, loaded once (they make meshes: the main thread, at the menu).</summary>
    private IReadOnlyList<CharacterChoice> ShippedCharacters() =>
        _characters ??= CharacterChoice.FromLibrary(CharacterLibrary.Load(CharacterLibrary.ShippedRoot));

    private static string? Argument(IEnumerable<string> arguments, string name) =>
        arguments.LastOrDefault(a => a.StartsWith(name, StringComparison.Ordinal))?[name.Length..];

    /// <summary>--team=, --room=, --mode= and --difficulty= name the menu's first choices (a launch with --menu keeps what the command line asked).</summary>
    private static void Preselect(MenuModel model, IReadOnlyList<string> arguments)
    {
        if (Argument(arguments, "--team=") is { } team) model.SelectTeam(team);
        if (Argument(arguments, "--room=") is { } room)
        {
            var id = room.Replace('\\', '/').TrimEnd('/').Split('/').Last();
            model.SelectIsland(id);
        }
        if (Argument(arguments, "--mode=") is { } mode && model.Team != null) model.ChooseMode(mode, Argument(arguments, "--difficulty="));
        // A choice that did not take is no message at the door.
        model.SetNotice("");
    }

    /// <summary>The game starts: the menu goes, and the room is built under this node as NativeGameBoot builds it, booted with the menu's choices.</summary>
    public RoomWorld StartRoom(MenuLaunch launch)
    {
        if (Menu != null)
        {
            RemoveChild(Menu);
            Menu.QueueFree();
            Menu = null;
        }
        var arguments = OS.GetCmdlineUserArgs();
        var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        world.RoomDirectory = launch.Island.Directory;
        world.BootArguments = ArgumentsFor(arguments, launch);
        // A look preview, as NativeGameBoot reads it.
        if (Argument(arguments, "--style=") is { } style && !string.IsNullOrWhiteSpace(style))
            world.StylePresetPath = $"{StylePreset.StylesRoot}/{(style.Contains('/') ? style : $"{RoomWorld.DefaultStyleId}/{style}")}.json";
        GD.Print($"MENU_LAUNCH team={launch.TeamId} room={launch.Island.RoomId} mode={launch.Mode}{(launch.Difficulty != null ? " difficulty=" + launch.Difficulty : "")}");
        DisplayServer.WindowSetTitle("Garage Game");
        AddChild(world);
        World = world;
        return world;
    }

    /// <summary>
    /// The command line that opens this game again at the menu ("Back to the menu": switching modes means going back to the menu, decision
    /// 3): the engine's own arguments as they were (the project's path, the window's size), then, after "--", the user arguments the
    /// game was launched with but for the room, team, mode and difficulty it was playing and any boot check's, and "--menu".
    /// </summary>
    public static IReadOnlyList<string> RelaunchArguments(IEnumerable<string> engineArguments, IEnumerable<string> userArguments, string? projectPath = null) =>
        // The engine does not list the project path it was started with, and a new process finds the project from where it starts: the path is given again (a game run from its source tree).
        (projectPath == null || engineArguments.Contains("--path") ? engineArguments : new[] { "--path", projectPath }.Concat(engineArguments)).Concat(new[] { "--" }).Concat(userArguments.Where(a => !a.StartsWith("--room=", StringComparison.Ordinal) && !a.StartsWith("--team=", StringComparison.Ordinal) &&
            !a.StartsWith("--mode=", StringComparison.Ordinal) && !a.StartsWith("--difficulty=", StringComparison.Ordinal) && !a.StartsWith("--quit-after-ready=", StringComparison.Ordinal) &&
            a != MenuArgument)).Concat(new[] { MenuArgument }).ToList();

    /// <summary>
    /// Starts this game again at the menu and ends this one. The island is saved as the game goes (every durable command writes it), and
    /// Trust settles when the room leaves the tree. Returns false, leaving the room as it is, when the new game could not be started.
    /// </summary>
    public static bool BackToMenu(SceneTree tree)
    {
        var project = ProjectSettings.GlobalizePath("res://").TrimEnd('/', (char)92);
        var arguments = RelaunchArguments(OS.GetCmdlineArgs(), OS.GetCmdlineUserArgs(), System.IO.File.Exists(System.IO.Path.Combine(project, "project.godot")) ? project : null);
        var started = OS.CreateProcess(OS.GetExecutablePath(), arguments.ToArray());
        if (started <= 0) return false;
        GD.Print($"MENU_BACK relaunched as pid {started}");
        tree.Quit();
        return true;
    }

    /// <summary>The command line the room's boot reads: what the game was launched with (the recorder's, full screen, a test's knack) but for the team, mode and difficulty, which are the menu's.</summary>
    public static IReadOnlyList<string> ArgumentsFor(IEnumerable<string> commandLine, MenuLaunch launch) =>
        commandLine.Where(a => !a.StartsWith("--team=", StringComparison.Ordinal) && !a.StartsWith("--mode=", StringComparison.Ordinal) &&
                               !a.StartsWith("--difficulty=", StringComparison.Ordinal) && a != MenuArgument).Concat(launch.Arguments).ToList();
}
