using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Playtest;

namespace GarageGame.Tests;

/// <summary>
/// The playtest recorder (docs/runs/RUN-2-PLAYTEST-LOOP.md, R1; Playtest/PlaytestRecorder) on the game's own room world, booted from
/// res://scenes/room.tscn as a test's RoomWorld and attached through the integrator's one line (PlaytestRecorder.Attach): when it is
/// off (headless, --no-playtest-record, a boot check) and leaves no folder; a play's record (the manifest at launch, everything the
/// host answered from either principal with names through DisplayText, the HUD's actions and notices, the breadcrumb, the frame
/// samples, the stills by their reasons and sizes, the clean quit with its counts) checked against the contract by contracts/validate.py;
/// a killed game's record (at most a second lost, still valid, unfinished); the prune to the newest thirty; the cap of 600 stills.
/// The suite is headless, so the grab is a test picture (PlaytestRecorder.GrabForTests) and the switch is told it has a window.
/// </summary>
public partial class PlaytestTest : Node
{
    private const string TestRoot = "user://tests/playtest";
    private int _checks;
    private int _failures;
    private ScriptErrors _errors = null!;
    private RoomWorld _world = null!;
    private RoomHud _hud = null!;
    private CommandHost _host = null!;
    private int _ids;
    private int _validated;
    private bool _validatorMissing;

    public override async void _Ready()
    {
        var previous = (CommandHost.SaveRoot, RoomHud.ProfilePath, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot);
        try
        {
            _errors = new ScriptErrors();
            OS.AddLogger(_errors);
            RemoveAll();
            // The room's own saves, profile and wishes in this suite's folder, never the runner's.
            CommandHost.SaveRoot = $"{TestRoot}/saves";
            RoomHud.ProfilePath = $"{TestRoot}/profile/avatar_profile_v1.cfg";
            CommandHost.DefaultGlowProgressPath = $"{TestRoot}/profile/glow.json";
            CommandHost.DefaultPlayerVitalsPath = $"{TestRoot}/profile/player_vitals_v1.json";
            CommandHost.DefaultWishSaveRoot = $"{TestRoot}/wishes";
            TestSwitch();
            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(_world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frames(1);
            Check(_world.WorldReady, "the room boots: " + _world.LoadError);
            _hud = _world.GetNode<RoomHud>("RoomHud");
            _host = CommandHost.Of(_world)!;
            await Frames(10);

            await TestOff();
            await TestRecord();
            await TestKill();
            await TestPrune();
            await TestStillCap();
            TestNothingLeftBehind();

            _world.QueueFree();
            await Frames(3);
            OS.RemoveLogger(_errors);
            Check(_errors.Count == 0, $"no script or engine error while the suite ran ({_errors.Count}; first: {_errors.First})");
            if (_validatorMissing) GD.Print("PLAYTEST_VALIDATOR not found: the records were held to the contract's shape in C# only");
            GD.Print($"NATIVE_KERNEL_PLAYTEST: {_checks - _failures}/{_checks} checks passed; the recorder off headless, with {PlaytestRecorder.DisableArgument} and in a boot check, " +
                "leaving no folder; a play's manifest, commands from both principals with names through DisplayText and no picture, token, session or save, the HUD's keys " +
                "and notices, the breadcrumb, the frames, the stills by reason and size, the marks and the clean quit; a killed game's record losing under a second; the " +
                $"newest thirty kept; the 600-still cap ({_validated} records passed contracts/validate.py)");
            Restore(previous);
            RemoveAll();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            Restore(previous);
            GD.PushError("Playtest test exception: " + error);
            GetTree().Quit(1);
        }
    }

    private static void Restore((string, string, string, string, string) previous)
    {
        (CommandHost.SaveRoot, RoomHud.ProfilePath, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot) = previous;
        PlaytestRecorder.ArgumentsOverride = null;
        PlaytestRecorder.DisplayServerOverride = null;
        PlaytestRecorder.RootOverride = null;
        PlaytestRecorder.GrabForTests = null;
    }

    // ---- the switch ----

    private void TestSwitch()
    {
        Check(PlaytestRecorder.WhyOff("headless", Array.Empty<string>()) == "headless", "a headless run records nothing");
        Check(PlaytestRecorder.WhyOff("Windows", new[] { "--room=landscape_garage_nominal", PlaytestRecorder.DisableArgument }) == PlaytestRecorder.DisableArgument,
            "--no-playtest-record turns the record off");
        Check(PlaytestRecorder.WhyOff("Windows", new[] { "--quit-after-ready=240" })?.Contains("boot check", StringComparison.Ordinal) == true, "a boot check (--quit-after-ready=) records nothing");
        Check(PlaytestRecorder.WhyOff("Windows", new[] { "--build=c19d99b", "--team=local" }) == null && PlaytestRecorder.WhyOff("X11", Array.Empty<string>()) == null,
            "a windowed launch records by default");
    }

    /// <summary>Off: no recorder, no folder, the keys say so; the runner's own user folder has no record.</summary>
    private async Task TestOff()
    {
        var root = $"{TestRoot}/off";
        PlaytestRecorder.RootOverride = root;
        var headless = PlaytestRecorder.Attach(_world);
        PlaytestRecorder.DisplayServerOverride = "Windows";
        PlaytestRecorder.ArgumentsOverride = new[] { PlaytestRecorder.DisableArgument };
        var flagged = PlaytestRecorder.Attach(_world);
        PlaytestRecorder.ArgumentsOverride = new[] { "--room=test_room", "--quit-after-ready=240" };
        var booted = PlaytestRecorder.Attach(_world);
        PlaytestRecorder.ArgumentsOverride = null;
        PlaytestRecorder.DisplayServerOverride = null;
        Check(headless == null && flagged == null && booted == null && PlaytestRecorder.Of(_world) == null, "off headless, with the flag and in a boot check: no recorder is attached");
        Check(!Directory.Exists(ProjectSettings.GlobalizePath(root)) && !Directory.Exists(ProjectSettings.GlobalizePath(PlaytestRecorder.FolderRoot)),
            "and no folder is made, neither the record's nor user://playtests");
        _hud._UnhandledInput(Press(Key.F9));
        await Frames(2);
        Check(NoticeLine() == PlaytestRecorder.NotRecordingWords, "F9 with the record off says so on screen: " + NoticeLine());
        Check(HelpLine("PlaytestKeys").Contains("the record is off for this run", StringComparison.Ordinal), "and the key help says the record is off: " + HelpLine("PlaytestKeys"));
    }

    private void TestNothingLeftBehind()
    {
        Check(!Directory.Exists(ProjectSettings.GlobalizePath(PlaytestRecorder.FolderRoot)), "the runner's user folder holds no user://playtests after the whole suite");
    }

    // ---- helpers ----

    /// <summary>A recorder on the world as a windowed launch would make it, its records under root, its grabs a test picture.</summary>
    private PlaytestRecorder? Record(string root, params string[] arguments)
    {
        PlaytestRecorder.RootOverride = root;
        PlaytestRecorder.DisplayServerOverride = "Windows";
        PlaytestRecorder.ArgumentsOverride = arguments;
        PlaytestRecorder.GrabForTests ??= () => PlaytestRecords.Picture(320, 180, new Color(0.3f, 0.5f, 0.4f));
        var recorder = PlaytestRecorder.Attach(_world);
        PlaytestRecorder.ArgumentsOverride = null;
        PlaytestRecorder.DisplayServerOverride = null;
        return recorder;
    }

    private async Task Unrecord(PlaytestRecorder recorder)
    {
        recorder.QueueFree();
        await Frames(2);
    }

    /// <summary>contracts/validate.py on a record: OK, or the suite says the validator was not found and relies on the C# shape.</summary>
    private void Validated(string folder, string label)
    {
        var shape = PlaytestRecords.Shape(folder);
        Check(shape.Count == 0, $"{label}: the record holds to the contract's shape: {string.Join("; ", shape)}");
        if (PlaytestRecords.Validate(folder) is not { } verdict)
        {
            _validatorMissing = true;
            return;
        }
        _validated++;
        Check(verdict.Ok, $"{label}: contracts/validate.py passes it: {verdict.Output}");
    }

    private JsonObject Player(string op, JsonObject args, string? actionId = null) => Send("enfractal.command", op, args, CommandHost.PlayerPrincipal, actionId);
    private JsonObject Companion(string op, JsonObject args) => Send("enfractal.command", op, args, CommandHost.CompanionPrincipal);

    private JsonObject Send(string schema, string op, JsonObject args, string principal, string? id = null)
    {
        var message = new JsonObject { ["schema"] = schema, ["version"] = 1, ["room_id"] = _host.Room.RoomId, ["op"] = op, ["args"] = args };
        message[schema == "enfractal.query" ? "query_id" : "action_id"] = id ?? $"pt-{++_ids}";
        return _host.HandleObject(message.ToJsonString(), principal);
    }

    private static InputEventKey Press(Key key, bool shift = false) => new() { PhysicalKeycode = key, Pressed = true, ShiftPressed = shift };

    // The notice left the footer for its own card under the reticle (the second playtest round's P2, item 4).
    private string NoticeLine() => _hud.NoticeText;

    private string HelpLine(string name) => _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").GetNode<Label>(name).Text;

    private static void RemoveAll()
    {
        var directory = ProjectSettings.GlobalizePath(TestRoot);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Real seconds (the record's clock), frames passing meanwhile.</summary>
    private async Task Seconds(double seconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(5);
            await Frames(1);
        }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Playtest: " + label);
    }

    /// <summary>Counts every script or engine error while the suite runs, so one fails the suite itself.</summary>
    private sealed partial class ScriptErrors : Logger
    {
        private readonly object _lock = new();
        public int Count { get; private set; }
        public string First { get; private set; } = "";

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if (errorType == (int)ErrorType.Warning || function == "push_error" || code.StartsWith("Playtest", StringComparison.Ordinal)) return;
            lock (_lock)
            {
                Count++;
                if (First.Length == 0) First = $"{file}:{line} {code} {rationale}";
            }
        }
    }
}
