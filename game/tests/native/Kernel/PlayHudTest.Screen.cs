using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Playtest;
using GarageGame.Native.Room;
using GarageGame.Native.Saves;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The first recorded play's HUD round (16 October, docs/runs/RUN-2-PLAYTEST-ROUND-1.md, P2; MECHANICS.md decision 66): the card at the
/// upper left is gone and holds the meters alone, its title, state, clock, mode line, the Gubble's buttons and the hand buttons live
/// in the H help (the buttons work there) with the settings read out; the first notice of a play is empty, never Run 0's sentence; the
/// swim and dive hint stands by the reticle once for each entry into deep water, with the map's key names, and is gone on leaving; the
/// full-screen key toggles the window and writes the choice into the machine's profile, a launch that asks for it starts full screen
/// and a headless run or a boot check never does; the hint and the key reach the playtest record.
/// </summary>
public partial class PlayHudTest
{
    private VBoxContainer KeyHelpNode() => _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp");

    // ---- the card is in the help ----

    private async Task TestCardIsInTheHelp()
    {
        var host = CommandHost.Of(_world)!;
        var panel = _hud.MeterPanel;
        var help = KeyHelpNode();
        if (help.Visible) _hud._UnhandledInput(Press(Key.H));
        await Frames(4);

        // The upper left: the meters, and nothing else.
        var labels = panel.FindChildren("*", "Label", true, false).Select(label => label.Name.ToString()).Order().ToArray();
        Check(panel.IsVisibleInTree() && panel.GetChildCount() == 1 && panel.GetChild(0).GetChildCount() == 1 && panel.GetChild(0).GetChild(0).Name == "Meters" &&
              _hud.Hearts.IsVisibleInTree() && _hud.Bar.IsVisibleInTree(),
            "the panel at the upper left holds the meters (hearts, bar, the pips' row) and nothing else beside them");
        Check(panel.FindChildren("*", "Button", true, false).Count == 0 && labels.SequenceEqual(new[] { "BarNumbers", "HeartsNumbers" }) &&
              new[] { "RoomTitle", "State", "Clock", "IslandVitalsLine", "SettingsLine", "CompanionActions", "HandActions" }.All(name => panel.FindChild(name, true, false) == null),
            $"no title, state, clock, mode line, button or settings line is in it (its labels are the meters' numbers: {string.Join(", ", labels)})");
        Check(_hud.FindChild("MagicPanel", true, false) is PanelContainer { Position: var magicAt } && magicAt == new Vector2(18, 150),
            "the Magic panel keeps its place at (18, 150): nothing else on the screen moved");

        // The help holds what the card held, under the status lines and the two rows of buttons.
        var status = _hud.StatusLines;
        Check(status.GetParent() == help && help.GetNode("CompanionActions") is HBoxContainer && help.GetNode("HandActions") is HBoxContainer,
            "the status lines and both rows of buttons are in the help, under its key lines' node");
        var names = status.GetChildren().Select(child => child.Name.ToString()).ToArray();
        Check(names.SequenceEqual(new[] { "RoomTitle", "State", "Clock", "IslandVitalsLine", "SettingsLine" }),
            "in order: the room's title, the state, the clock, the mode line and the settings: " + string.Join(", ", names));
        var title = status.GetNode<Label>("RoomTitle").Text;
        var state = status.GetNode<Label>("State").Text;
        var clock = status.GetNode<Label>("Clock").Text;
        var mode = status.GetNode<Label>("IslandVitalsLine").Text;
        Check(title.Length > 0 && title == _hud.RoomTitle && state.Contains(" cm player", StringComparison.Ordinal) && state.Contains("gravity ", StringComparison.Ordinal) &&
              state.Contains(_world.Companion.CompanionName + ": ", StringComparison.Ordinal) && clock.Contains("real clock (T)", StringComparison.Ordinal) &&
              mode.StartsWith(host.Mode == "challenge" ? "Challenge" : host.Mode == "live" ? "Live" : "Creative", StringComparison.Ordinal) && mode.Contains(" · ", StringComparison.Ordinal),
            $"and they say what the card said: \"{title}\" / \"{state}\" / \"{clock}\" / \"{mode}\"");
        var settings = _hud.SettingsLine!.Text;
        Check(settings == $"Settings (C changes them): full screen off · play the intro {(_hud.IntroToggle!.ButtonPressed ? "on" : "off")} · numbers on the meters off · " +
                          $"your AI sees through the Gubble's eyes {(_hud.EyesToggle!.ButtonPressed ? "on" : "off")}",
            "the settings line reads the machine's switches out: " + settings);

        // Folded, the footer is the small hint line alone; unfolded, all of it is there to read.
        var hint = _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetChild<Label>(0);
        Check(!help.Visible && !status.IsVisibleInTree() && !help.GetNode<HBoxContainer>("CompanionActions").IsVisibleInTree() && hint.IsVisibleInTree() && hint.Text == "H keys",
            "folded, none of it shows and the footer's hint line, \"H keys\", stays");
        _hud._UnhandledInput(Press(Key.H));
        await Frames(4);
        Check(help.Visible && status.IsVisibleInTree() && status.GetNode<Label>("State").IsVisibleInTree() && status.GetNode<Label>("Clock").IsVisibleInTree() &&
              help.GetNode<HBoxContainer>("CompanionActions").IsVisibleInTree() && help.GetNode<HBoxContainer>("HandActions").IsVisibleInTree() && hint.Text == "H hide keys",
            "H opens it: the status lines and the buttons show with every key line");
        // All of it is on the screen: the footer grows up from the bottom edge, and the last line (the notice) is above it.
        var footerRect = _hud.GetNode<PanelContainer>("HelpFooter").GetGlobalRect();
        var visible = _hud.GetViewport().GetVisibleRect();
        Check(visible.Encloses(footerRect) && footerRect.End.Y <= visible.End.Y - 17, 
            $"and all of it fits the screen, growing up from the bottom edge: the footer spans y {footerRect.Position.Y:0} to {footerRect.End.Y:0} of {visible.End.Y:0}, x {footerRect.Position.X:0} to {footerRect.End.X:0} of {visible.End.X:0}");

        // The buttons work there: the Gubble's orders, the panels' buttons, the hands.
        var actions = help.GetNode<HBoxContainer>("CompanionActions");
        Button ButtonOf(Node row, string text) => row.GetChildren().OfType<Button>().First(button => button.Text == text);
        void Click(Button button) => button.EmitSignal(BaseButton.SignalName.Pressed);
        var companion = _world.Companion;
        Click(ButtonOf(actions, "Wait"));
        var waits = companion.CurrentIntent;
        Click(ButtonOf(actions, "Follow"));
        Check(waits == "stay" && companion.CurrentIntent == "follow", $"in the help, Wait and Follow still order the Gubble ({waits}, then {companion.CurrentIntent})");
        Click(ButtonOf(actions, "Settings"));
        var customizing = _hud.SettingsOpen;
        Click(ButtonOf(actions, "Settings"));
        Click(actions.GetNode<Button>("MagicPanelButton"));
        var magic = _hud.MagicPanelOpen;
        Click(actions.GetNode<Button>("MagicPanelButton"));
        Check(customizing && !_hud.SettingsOpen && magic && !_hud.MagicPanelOpen, "and Settings and Magic open and close their panels");
        var hands = help.GetNode<HBoxContainer>("HandActions");
        Click(ButtonOf(hands, "E Gather"));
        await Frames(2);
        Check(NoticeText().Contains("Hold a loose log or stone", StringComparison.Ordinal), "and the hand buttons send the player's commands (E Gather with empty hands answers in the host's words): " + NoticeText());
        _hud._UnhandledInput(Press(Key.H));
        await Frames(2);
        Check(!help.Visible, "H folds it again");
    }

    // ---- the first notice ----

    /// <summary>A fresh world with the record on: the line is empty until something is said, and the record's first notice is a real one.</summary>
    private async Task TestFirstNoticeIsEmpty()
    {
        var root = PlaytestRoot + "_first";
        PlaytestRecorder.RootOverride = root;
        PlaytestRecorder.DisplayServerOverride = "Windows";
        PlaytestRecorder.ArgumentsOverride = new[] { "--build=hud0002" };
        PlaytestRecorder.GrabForTests = () => Image.CreateEmpty(64, 36, false, Image.Format.Rgba8);
        RoomWorld? world = null;
        try
        {
            world = await BootRoom();
            PlaytestRecorder.ArgumentsOverride = null;
            PlaytestRecorder.DisplayServerOverride = null;
            if (world == null) return;
            var hud = world.GetNode<RoomHud>("RoomHud");
            var recorder = PlaytestRecorder.Of(world);
            Check(recorder is { Recording: true }, "a windowed launch's recorder on the fresh world");
            if (recorder == null) return;
            await Frames(6);
            var noticeLabel = hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<Label>("Notice");
            var line = noticeLabel.Text;
            Check(line == hud.LookNotice && !line.Contains("Placeholder", StringComparison.Ordinal) && noticeLabel.Visible == (line.Length > 0),
                $"the notice line starts with the look's words or none (\"{hud.LookNotice}\"), never Run 0's sentence, and shows only when it has words: \"{line}\"");
            hud._UnhandledInput(Press(Key.F9));
            await Frames(2);
            recorder.Finish(quitStill: false);
            var hudNotices = PlaytestRecords.Events(recorder.Folder).Kind("notice").Where(n => n.Str("channel") == "hud").Select(n => n.Str("text")).ToList();
            Check(hudNotices.Count > 0 && hudNotices[0] == (hud.LookNotice.Length > 0 ? hud.LookNotice : "Mark 1") && hudNotices.All(text => text.Length > 0 && !text.Contains("Placeholder", StringComparison.Ordinal)),
                "and the record's first notice carries the look's words or a key's (\"Mark 1\"), never an empty line or the placeholder: " + string.Join(" | ", hudNotices));
        }
        finally
        {
            PlaytestRecorder.ArgumentsOverride = null;
            PlaytestRecorder.DisplayServerOverride = null;
            PlaytestRecorder.RootOverride = null;
            PlaytestRecorder.GrabForTests = null;
            if (world != null && GodotObject.IsInstanceValid(world)) world.Free();
            await Frames(2);
            var folder = ProjectSettings.GlobalizePath(root);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    // ---- the swim and dive hint ----

    private Area3D? _testWater;

    /// <summary>A sheet of water 12 cm over the floor round the free spot: over the head of a 10 cm body, deep enough for it to float.</summary>
    private async Task AddWater()
    {
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
            sheet.AddVertex(new Vector3(FreeSpot.X + corner.X, 0.12f, FreeSpot.Z + corner.Y));
        _testWater = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        _world.AddChild(_testWater);
        await Frames(20);
    }

    private async Task RemoveWater()
    {
        _testWater?.QueueFree();
        _testWater = null;
        await Frames(20);
    }

    private async Task TestSwimHint()
    {
        var player = _world.Player;
        var seen = new List<ScreenNotice>();
        // The HUD's line only, and of it the hint and the clears (the Gubble's bubble clears too, and other words come and go).
        void Watch(ScreenNotice notice)
        {
            if (notice.Channel == "hud" && (notice.Text.Length == 0 || notice.Text.StartsWith("Deep water", StringComparison.Ordinal))) seen.Add(notice);
        }
        _hud.NoticeShown += Watch;
        SaveMap();
        try
        {
            await StandForViews(FreeSpot, 0.4f, -0.2f);
            var expected = "Deep water: Space leaps · hold Ctrl to dive · B home";
            Check(!player.IsSwimming && !_hud.SwimHint.Visible && !_hud.SwimHintEntered && _hud.SwimHint.Name == "SwimHint" && _hud.SwimHint.MouseFilter == Control.MouseFilterEnum.Ignore,
                "on dry floor there is no hint");

            await AddWater();
            Check(player.IsSwimming && _hud.SwimHintEntered && _hud.SwimHint.Visible && _hud.SwimHint.Text == expected,
                $"the body enters deep water (swimming {player.IsSwimming}) and the hint stands by the reticle, from the map's key names: \"{_hud.SwimHint.Text}\"");
            var screen = _hud.GetViewport().GetVisibleRect();
            Check(screen.Encloses(new Rect2(_hud.SwimHint.Position, _hud.SwimHint.GetCombinedMinimumSize())) && _hud.SwimHint.Theme == _hud.AskTag.Theme,
                "small and on the screen, in the ask tag's own style");
            await Frames(60);
            Check(seen.Count(n => n.Channel == "hud" && n.Text == expected) == 1 && seen.Count(n => n.Text.Length == 0) == 0 && _hud.SwimHint.Visible,
                "it is said once however long the body swims, and stays up meanwhile");

            // Remapped keys show at once.
            Bind(Act.Jump, Key.Kp5);
            Bind(Act.MoveDive, Key.Kp6);
            Bind(Act.BodyHome, Key.Kp7);
            await Frames(3);
            Check(_hud.SwimHint.Text == $"Deep water: {OS.GetKeycodeString(Key.Kp5)} leaps · hold {OS.GetKeycodeString(Key.Kp6)} to dive · {OS.GetKeycodeString(Key.Kp7)} home" && seen.Count(n => n.Text.Length > 0) == 1,
                $"remapped, the words follow the map and nothing is said again: \"{_hud.SwimHint.Text}\"");
            RestoreMap();
            await Frames(3);
            Check(_hud.SwimHint.Text == expected, "and back with the defaults");

            // A panel holds the screen: the tag stands aside, the entry still counts, and it is back when the panel goes.
            _hud._UnhandledInput(Press(Key.C));
            await Frames(3);
            var aside = !_hud.SwimHint.Visible && _hud.SwimHintEntered;
            _hud._UnhandledInput(Press(Key.C));
            await Frames(3);
            Check(aside && _hud.SwimHint.Visible && seen.Count(n => n.Text.Length > 0) == 1, "the appearance panel hides it while open, and it returns without being said again");

            // Leaving the water: gone, and said to be.
            await RemoveWater();
            Check(!player.IsSwimming && !_hud.SwimHintEntered && !_hud.SwimHint.Visible && seen[^1].Channel == "hud" && seen[^1].Text.Length == 0,
                $"out of the water the hint is gone (swimming {player.IsSwimming}) and the line is cleared in the record's notices");

            // Once for each entry.
            await AddWater();
            Check(player.IsSwimming && _hud.SwimHint.Visible && seen.Count(n => n.Text == expected) == 2, "entering again shows it again: once for each entry");
            await RemoveWater();
            Check(seen.Select(n => n.Text).SequenceEqual(new[] { expected, "", expected, "" }), "the notices were: the hint, the clear, the hint, the clear: " + string.Join(" | ", seen.Select(n => n.Text.Length == 0 ? "(clear)" : n.Text)));
        }
        finally
        {
            _hud.NoticeShown -= Watch;
            if (_testWater != null) await RemoveWater();
            RestoreMap();
            await StandForViews(FreeSpot, 0.4f, 0f);
            player.ReadKeyboard = true;
        }
    }

    // ---- full screen ----

    private async Task TestFullscreenKey()
    {
        var path = RoomHud.ProfilePath;
        Check(DisplayServer.GetName() == "headless" && !_hud.Fullscreen && !RoomHud.LoadFullscreen(path) && MachineSettings.SwitchKeys.Contains("fullscreen") && MachineSettings.SwitchKeys.Contains("play_intro"),
            "headless there is no window to change: the HUD starts as a window, the profile says none, and the machine's switches list the key beside \"Play the intro\"");
        var actionsSeen = new List<(string Name, string Detail)>();
        void Watch(string name, string detail) => actionsSeen.Add((name, detail));
        _hud.ActionHandled += Watch;
        SaveMap();
        try
        {
            var before = ReadProfile(path);
            _hud._UnhandledInput(Press(Key.F11));
            await Frames(4);
            var after = ReadProfile(path);
            Check(_hud.Fullscreen && RoomHud.LoadFullscreen(path) && after.GetValueOrDefault("fullscreen") == "true" &&
                  before.All(pair => pair.Key == "fullscreen" || after.GetValueOrDefault(pair.Key) == pair.Value),
                "F11 puts the window in full screen and writes the choice into the machine's profile, every other key kept: " + string.Join(", ", after.Select(p => p.Key + "=" + p.Value)));
            Check(NoticeText() == "Full screen: F11 goes back to a window.", "the screen says so: " + NoticeText());
            Check(_hud.SettingsLine!.Text.Contains("full screen on", StringComparison.Ordinal) && HelpLines().Contains("F11 full screen on or off (kept for your next launch)"),
                "and the help's settings line reads it, with the key named in its key lines: " + _hud.SettingsLine.Text);
            _hud._UnhandledInput(Press(Key.F11));
            await Frames(4);
            Check(!_hud.Fullscreen && !RoomHud.LoadFullscreen(path) && ReadProfile(path).GetValueOrDefault("fullscreen") == "false" && NoticeText() == "A window again: F11 goes full screen.",
                "F11 again: a window, written, and said: " + NoticeText());
            Check(actionsSeen.SequenceEqual(new[] { ("view_fullscreen", "to full screen"), ("view_fullscreen", "to a window") }),
                "the record would see both presses by the action's map name, with where each goes: " + string.Join(", ", actionsSeen));

            // Live in every context: with the appearance panel open.
            _hud._UnhandledInput(Press(Key.C));
            _hud._UnhandledInput(Press(Key.F11));
            Check(_hud.SettingsOpen && _hud.Fullscreen, "with the appearance panel open the key still works, and the panel stays open");
            _hud._UnhandledInput(Press(Key.F11));
            _hud._UnhandledInput(Press(Key.C));
            await Frames(2);

            // It is an action: moved to another key, the old one does nothing and the help says the new one.
            Bind(Act.ViewFullscreen, Key.Y);
            await Frames(3);
            _hud._UnhandledInput(Press(Key.F11));
            var stayed = _hud.Fullscreen;
            _hud._UnhandledInput(Press(Key.Y));
            await Frames(3);
            Check(!stayed && _hud.Fullscreen && HelpLines().Contains("Y full screen on or off (kept for your next launch)") && NoticeText() == "Full screen: Y goes back to a window.",
                "remapped to Y: F11 does nothing, Y toggles, and the help and the notice say Y");
            RestoreMap();
            _hud.SetFullscreen(false);

            // A profile that does not read is never overwritten: the choice then lasts this session only, and says so.
            var bad = "user://tests/play_hud/profile/unreadable.cfg";
            var saved = RoomHud.ProfilePath;
            RoomHud.ProfilePath = bad;
            try
            {
                File.WriteAllText(ProjectSettings.GlobalizePath(bad), "[profile]\nversion=9\n");
                _hud.SetFullscreen(true);
                await Frames(2);
                var bytes = File.ReadAllText(ProjectSettings.GlobalizePath(bad));
                Check(bytes == "[profile]\nversion=9\n" && _hud.Fullscreen && NoticeText().EndsWith("(Not saved: it lasts this session.)", StringComparison.Ordinal),
                    "a profile of another version is left as it is, and the notice says the choice is not saved: " + NoticeText());
            }
            finally
            {
                RoomHud.ProfilePath = saved;
                _hud.SetFullscreen(false);
                File.Delete(ProjectSettings.GlobalizePath(bad));
            }
            Check(!RoomHud.LoadFullscreen(path), "(the window and the profile are back as they were)");
        }
        finally
        {
            _hud.ActionHandled -= Watch;
            RestoreMap();
        }
    }

    private static Dictionary<string, string> ReadProfile(string path)
    {
        var config = new ConfigFile();
        var result = new Dictionary<string, string>();
        if (config.Load(path) != Error.Ok) return result;
        foreach (var key in config.GetSectionKeys("profile")) result[key] = config.GetValue("profile", key).ToString().ToLowerInvariant();
        return result;
    }

    /// <summary>Whether a launch starts in full screen: the argument forces it, the remembered choice decides, a headless run, a boot check and a tool's own scene never.</summary>
    private void TestFullscreenAtBoot()
    {
        var none = Array.Empty<string>();
        bool Starts(string server, string[] user, bool gameBoot, bool remembered) => RoomHud.StartsFullscreen(server, user, gameBoot, remembered);
        Check(!Starts("Windows", none, true, false) && Starts("Windows", none, true, true) && Starts("Windows", new[] { "--fullscreen" }, true, false) &&
              Starts("Windows", new[] { "--room=x", "--fullscreen", "--mode=challenge" }, true, false),
            "the game's own launch starts in a window; the remembered choice, or --fullscreen among its user arguments, makes it full screen");
        Check(!Starts("headless", none, true, true) && !Starts("headless", new[] { "--fullscreen" }, true, true),
            "headless there is nothing to put in full screen, whatever is asked or remembered");
        Check(!Starts("Windows", new[] { "--quit-after-ready=240" }, true, true) && !Starts("Windows", new[] { "--quit-after-ready=120", "--fullscreen" }, true, false),
            "a boot check (the runners', the smoke boot's) never goes full screen");
        Check(!Starts("Windows", none, false, true) && !Starts("Windows", new[] { "--fullscreen" }, false, false),
            "a tool's or a test's own scene (the look's captures, this suite) keeps its window, remembered or asked");
        // This suite builds the world under its own node, as a tool's scene does: the HUD was no game boot.
        Check(_hud.GetParent()!.GetParent() is not NativeGameBoot, "(and this suite's HUD is under a test's node, not the game's boot)");
        Check(RoomHud.FullscreenArgument == "--fullscreen" && RoomHud.FullscreenProfileKey == "fullscreen" && !RoomHud.DefaultFullscreen &&
              MachineSettings.FullscreenKey == RoomHud.FullscreenProfileKey,
            "the argument and the profile's key are the ones run-room.ps1 and the machine's settings name");
    }
}
