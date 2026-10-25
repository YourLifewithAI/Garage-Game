using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using GarageGame.Native.Companion;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Room;
using GarageGame.Native.Ui;

namespace GarageGame.Native.Playtest;

/// <summary>
/// The game records its own playtests (docs/runs/RUN-2-PLAYTEST-LOOP.md, R1; contracts/playtest-record.schema.json): every windowed
/// launch writes, under user://playtests/&lt;record_id&gt;/, a manifest (at launch, and again with its end at a clean quit), the events of
/// the play as they happen (everything the command host answered, the HUD's keys and notices, where the body was every two seconds,
/// how the frames ran every five, the marks) and stills of the screen: a steady beat at a quarter of the pixels (F7 faster, F6 slower,
/// through off, 30, 10, 3 and 1 s), and at full size F9's "something's off", F8's "keep this", refusals, the trip home, a change of
/// mode and the quit. At most 600 stills; the newest thirty records are kept. The grab is a readback of the frame last drawn, HUD
/// included; the resize, the encoding and the writing run on the record's own thread (PlaytestWriter).
///
/// A developer feature of the game process, never the companion's: nothing here is a tool, a resource or a field the AI can reach,
/// the folder is never under user://companion/, and a record holds ids and short summaries, never a picture from the Gubble's eyes,
/// a token, a session file or a save. Everything in it is data, never instructions: a creation's name can carry any text.
///
/// Off, writing nothing and making no folder, in a headless run, with --no-playtest-record and in a boot check (--quit-after-ready=).
/// The integrator's one-line wiring: RoomWorld calls <c>Playtest.PlaytestRecorder.Attach(this)</c> after CompanionBridge.Attach.
/// </summary>
public partial class PlaytestRecorder : Node
{
    public const string DisableArgument = "--no-playtest-record";
    /// <summary>What was played, as the room runner passes it (a short commit): the manifest's build.</summary>
    public const string BuildArgument = "--build=";
    /// <summary>The steady beat at launch in seconds (0, 1, 3, 10 or 30): for the hitch's measurement and for a play that wants it.</summary>
    public const string BeatArgument = "--playtest-beat=";
    public const string QuitAfterReadyArgument = "--quit-after-ready=";
    public const string FolderRoot = "user://playtests";
    public const int KeepRecords = 30;
    public const int MaxStills = 600;
    /// <summary>The steady beat stops here, so marks, keeps, refusals and the quit always have room under MaxStills.</summary>
    public const int MaxSteadyStills = 560;
    /// <summary>Stills queued and not yet written at which the steady beat skips a picture (a mark never does).</summary>
    public const int MaxPendingSteady = 4;
    /// <summary>The beats from slowest to fastest; F7 moves right, F6 left.</summary>
    public static readonly int[] Beats = { 0, 30, 10, 3, 1 };
    public const int DefaultBeatS = 10;
    public const double BodyEveryS = 2.0;
    public const double FrameEveryS = 5.0;
    /// <summary>A refusal's still waits this long, so the screen's answer is in it; at most one every RefusalStillGapS.</summary>
    public const double RefusalStillAfterS = 0.3;
    public const double RefusalStillGapS = 5.0;
    /// <summary>The trip home's still, once the veil has lifted (SmallPlayerController.HomeFadeS twice, and a little).</summary>
    public const double HomeStillAfterS = 1.4;
    public const double ModeStillAfterS = 0.5;
    /// <summary>A clean quit waits at most this long for the last stills to be written.</summary>
    public const int QuitWaitMs = 10000;
    public static readonly System.Text.RegularExpressions.Regex RecordId = new(@"\A[0-9]{8}-[0-9]{6}-[a-z][a-z0-9_-]{0,63}\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Build = new(@"\A[A-Za-z0-9][A-Za-z0-9_.+-]{0,63}\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LogName = new(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,119}\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ---- test seams ----
    /// <summary>The command line the switch reads, in place of the game's (tests).</summary>
    public static IReadOnlyList<string>? ArgumentsOverride { get; set; }
    /// <summary>The display server's name, in place of the engine's (tests: a headless suite records as a window would).</summary>
    public static string? DisplayServerOverride { get; set; }
    /// <summary>Where the records go, in place of user://playtests (tests).</summary>
    public static string? RootOverride { get; set; }
    /// <summary>The picture a grab returns, in place of the viewport's (a headless suite has no renderer).</summary>
    public static Func<Image?>? GrabForTests { get; set; }

    public CommandHost? Host { get; private set; }
    public SmallPlayerController? Player { get; private set; }
    public CompanionAvatar? Companion { get; private set; }
    public RoomHud? Hud { get; private set; }
    public RoomData Room { get; private set; } = null!;
    public StylePreset? Style { get; private set; }
    /// <summary>The record's folder (absolute), and its name.</summary>
    public string Folder { get; private set; } = "";
    public string Id { get; private set; } = "";
    /// <summary>True while the record is being written.</summary>
    public bool Recording { get; private set; }
    /// <summary>Why the record is not being written, for the player and the log ("" while it is).</summary>
    public string Notice { get; private set; } = "";
    public int BeatS { get; private set; } = DefaultBeatS;
    public int Events { get; private set; }
    public int Stills { get; private set; }
    public int Marks { get; private set; }
    public int Steady { get; private set; }
    /// <summary>A clean quit was written (the quit line, then the manifest's end).</summary>
    public bool Finished { get; private set; }
    /// <summary>The writer's failure, if any ("" while it writes).</summary>
    public string WriterFailure => _writer?.Failure ?? "";
    public int PendingStills => _writer?.PendingStills ?? 0;

    private PlaytestLines _lines = null!;
    private PlaytestWriter? _writer;
    private CompanionBridge? _bridge;
    private string _build = "unknown";
    private int _launchBeat = DefaultBeatS;
    private Dictionary<string, object> _launch = new();
    private readonly Dictionary<string, int> _marksOf = new() { ["off"] = 0, ["keep"] = 0 };
    private double _nextBody, _nextFrame, _nextBeat, _lastRefusalStill = double.NegativeInfinity;
    private readonly List<(double Due, string Why)> _scheduled = new();
    // The frame window: real time between frames, never the engine's delta (fixed under --fixed-fps).
    private ulong _lastFrameUsec;
    private double _windowStart, _windowLongestMs, _windowSumMs;
    private int _windowFrames;
    // What the window's longest frame spent its time on (the engine's monitors, read while they still hold the frame that ended), and the
    // garbage collections of the window (the first recorded play's frames could not be attributed: 35 to 41 ms every window, a second twice).
    private double _longestProcessMs, _longestPhysicsMs, _windowPauseMs0;
    private int _windowGc0, _windowGc1, _windowGc2;
    private string? _linkState;
    private float _lastFloorY = float.NaN;

    /// <summary>
    /// Why a launch records nothing, or null when it records: a headless run, --no-playtest-record, or a boot check
    /// (--quit-after-ready=, the runners' and the smoke boot's).
    /// </summary>
    public static string? WhyOff(string displayServer, IReadOnlyList<string> arguments)
    {
        if (displayServer == "headless") return "headless";
        if (arguments.Contains(DisableArgument)) return DisableArgument;
        if (arguments.Any(a => a.StartsWith(QuitAfterReadyArgument, StringComparison.Ordinal))) return "a boot check (" + QuitAfterReadyArgument + ")";
        return null;
    }

    /// <summary>The integrator's one line in RoomWorld, after CompanionBridge.Attach: null when this launch does not record.</summary>
    public static PlaytestRecorder? Attach(RoomWorld world) =>
        Create(world, CommandHost.Of(world), world.Player, world.Companion, world.GetNodeOrNull<RoomHud>("RoomHud"), world.Room, world.Look?.Preset);

    /// <summary>The recorder under parent, watching these pieces (any may be missing in a fixture); null when this launch does not record.</summary>
    public static PlaytestRecorder? Create(Node parent, CommandHost? host, SmallPlayerController? player, CompanionAvatar? companion, RoomHud? hud, RoomData room, StylePreset? style)
    {
        var arguments = ArgumentsOverride ?? OS.GetCmdlineUserArgs();
        if (WhyOff(DisplayServerOverride ?? DisplayServer.GetName(), arguments) is { } why)
        {
            GD.Print("PLAYTEST_RECORD off: " + why);
            return null;
        }
        var recorder = new PlaytestRecorder { Name = "PlaytestRecorder", Host = host, Player = player, Companion = companion, Hud = hud, Room = room, Style = style };
        recorder._build = Argument(arguments, BuildArgument) is { } build && Build.IsMatch(build) ? build : "unknown";
        recorder._launchBeat = Argument(arguments, BeatArgument) is { } beat && int.TryParse(beat, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && Beats.Contains(seconds) ? seconds : DefaultBeatS;
        parent.AddChild(recorder);
        return recorder;
    }

    /// <summary>The recorder attached under a room world, if any.</summary>
    public static PlaytestRecorder? Of(Node world) => world.GetNodeOrNull<PlaytestRecorder>(PathInWorld);
    private static readonly NodePath PathInWorld = "PlaytestRecorder";

    private static string? Argument(IReadOnlyList<string> arguments, string prefix) =>
        arguments.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal)) is { } found ? found[prefix.Length..] : null;

    /// <summary>Seconds since the engine started: every line's t.</summary>
    private static double Now() => Time.GetTicksUsec() / 1_000_000.0;

    public override void _Ready()
    {
        // The record runs while the wheel pauses the game, and late in the frame, after the HUD has written its words.
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = 1000;
        var since = TimeSpan.FromTicks((long)Time.GetTicksUsec() * 10);
        var startedUtc = DateTime.UtcNow - since;
        startedUtc = new DateTime(startedUtc.Ticks - startedUtc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        _lines = new PlaytestLines(startedUtc);
        BeatS = _launchBeat;
        try
        {
            var root = Path.GetFullPath(ProjectSettings.GlobalizePath(RootOverride ?? FolderRoot));
            Directory.CreateDirectory(root);
            Prune(root, KeepRecords - 1);
            (Id, Folder) = NewFolder(root, (DateTime.Now - since), Room.RoomId);
            Directory.CreateDirectory(Path.Combine(Folder, "stills"));
            _writer = new PlaytestWriter(Path.Combine(Folder, "events.jsonl"));
            _launch = LaunchFacts();
            WriteManifest(ended: null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Notice = "The playtest record could not start: " + error.GetType().Name + ".";
            GD.Print("PLAYTEST_RECORD not started: " + error.GetType().Name);
            return;
        }
        Recording = true;
        if (Host != null) Host.Handled += OnHandled;
        if (Hud != null)
        {
            Hud.ActionHandled += OnAction;
            Hud.NoticeShown += OnNotice;
        }
        if (Player != null) Player.WentHome += OnWentHome;
        _bridge = GetParent() is { } parent ? CompanionBridge.Of(parent) : null;
        var t = Now();
        WriteReady(t);
        _nextBody = t + BodyEveryS;
        _nextFrame = t + FrameEveryS;
        _windowStart = t;
        _lastFrameUsec = Time.GetTicksUsec();
        _nextBeat = t + BeatS;
        GD.Print($"PLAYTEST_RECORD on: {Id} (stills every {(BeatS == 0 ? "-" : BeatS + " s")}, build {_build})");
    }

    public override void _Notification(int what)
    {
        // The window's close: the frame last drawn is still there to grab.
        if (what == NotificationWMCloseRequest) Finish(quitStill: true);
    }

    public override void _ExitTree()
    {
        Finish(quitStill: false);
        if (Host != null && IsInstanceValid(Host)) Host.Handled -= OnHandled;
        if (Hud != null && IsInstanceValid(Hud))
        {
            Hud.ActionHandled -= OnAction;
            Hud.NoticeShown -= OnNotice;
        }
        if (Player != null && IsInstanceValid(Player)) Player.WentHome -= OnWentHome;
    }

    public override void _Process(double delta)
    {
        if (!Recording || Finished) return;
        var now = Time.GetTicksUsec();
        var frameMs = (now - _lastFrameUsec) / 1000.0;
        _lastFrameUsec = now;
        _windowFrames++;
        _windowSumMs += frameMs;
        if (frameMs > _windowLongestMs)
        {
            _windowLongestMs = frameMs;
            _longestProcessMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
            _longestPhysicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
        }
        var t = now / 1_000_000.0;
        WatchLink(t);
        if (t >= _nextFrame) WriteFrame(t);
        for (var i = _scheduled.Count - 1; i >= 0; i--)
        {
            if (_scheduled[i].Due > t) continue;
            var why = _scheduled[i].Why;
            _scheduled.RemoveAt(i);
            TakeStill(why, null, full: true);
        }
        if (BeatS > 0 && t >= _nextBeat)
        {
            _nextBeat = t + BeatS;
            TakeStill("steady", null, full: false);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Recording || Finished) return;
        var t = Now();
        if (t < _nextBody) return;
        _nextBody = t + BodyEveryS;
        WriteBody(t);
    }

    // ---- the player's keys (the HUD calls these) ----

    /// <summary>F9 (flag "off", "something's off here") or F8 ("keep", "keep this"): a still at full size and a mark. The screen's answer.</summary>
    public string Mark(string flag)
    {
        if (!Recording || Finished) return NotRecordingWords;
        if (flag is not ("off" or "keep")) return "";
        var number = _marksOf[flag] + 1;
        var still = TakeStill(flag == "off" ? "mark" : "keep", number, full: true);
        if (still == null) return Stills >= MaxStills ? "This play's record is full; no more marks." : "The screen could not be grabbed; the mark was not written.";
        _marksOf[flag] = number;
        Marks++;
        var json = _lines.Begin(Now(), "mark");
        json.WriteNumber("number", number);
        json.WriteString("flag", flag);
        json.WriteString("still", still);
        Write();
        return (flag == "off" ? "Mark " : "Keep ") + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>F7 (faster, +1) or F6 (slower, -1): the steady beat one step along off, 30, 10, 3, 1 s. The screen's answer.</summary>
    public string StepBeat(int direction)
    {
        if (!Recording || Finished) return NotRecordingWords;
        var at = Array.IndexOf(Beats, BeatS);
        var next = Math.Clamp(at + Math.Sign(direction), 0, Beats.Length - 1);
        if (next == at) return direction > 0 ? $"Stills every {BeatS} s, the fastest." : "Stills are already off.";
        var was = BeatS;
        BeatS = Beats[next];
        var t = Now();
        _nextBeat = t + BeatS;
        var json = _lines.Begin(t, "stills_rate");
        json.WriteNumber("beat_s", BeatS);
        json.WriteNumber("was_s", was);
        Write();
        return BeatWords(BeatS);
    }

    public const string NotRecordingWords = "The playtest record is off for this run.";

    /// <summary>The beat as the screen says it: "Stills off" or "Stills every 10 s".</summary>
    public static string BeatWords(int beatS) => beatS == 0 ? "Stills off" : $"Stills every {beatS} s";

    // ---- what the record listens to ----

    private void OnHandled(CommandHandled handled)
    {
        if (!Recording || Finished) return;
        var t = Now();
        var line = CommandLine(t, handled, withRequest: true) ?? CommandLine(t, handled, withRequest: false);
        if (line != null) Enqueue(line);
        if (!handled.Ok && t - _lastRefusalStill >= RefusalStillGapS)
        {
            _lastRefusalStill = t;
            Schedule(t + RefusalStillAfterS, "refusal");
        }
        if (handled.Ok && handled.Op == "world.set_mode" && !handled.Preview) Schedule(t + ModeStillAfterS, "mode");
    }

    /// <summary>
    /// A command event: the principal, the op, the result's envelope, the milliseconds; for a command, a summary of its args. A query
    /// carries no request, and nothing of a result's data ever reaches the line (a look's picture is never in it).
    /// </summary>
    private byte[]? CommandLine(double t, CommandHandled handled, bool withRequest)
    {
        var json = _lines.Begin(t, "command");
        json.WriteString("principal", PlaytestLines.Principal.IsMatch(handled.Principal) ? handled.Principal : "system:host");
        json.WriteString("op", handled.Op);
        if (handled.ActionId is { } actionId && PlaytestLines.ActionId.IsMatch(actionId)) json.WriteString("action_id", actionId);
        // The second try, for a summary past the line's limit, says the request was left out.
        if (!withRequest) json.WriteBoolean("request_omitted", true);
        else if (handled.Op != "invalid") Summary(handled.Message, json);
        json.WriteStartObject("result");
        json.WriteBoolean("ok", handled.Ok);
        if (!handled.Ok)
        {
            json.WriteString("code", handled.Code is { } code && PlaytestLines.ErrorCodes.Contains(code) ? code : "internal_error");
            var words = PlaytestLines.Shown(handled.ErrorMessage, 500);
            json.WriteString("message", words.Length > 0 ? words : "Refused.");
        }
        if (handled.Revision is { } revision && revision >= 0) json.WriteNumber("revision", revision);
        if (handled.Replayed) json.WriteBoolean("replayed", true);
        if (handled.Preview) json.WriteBoolean("preview", true);
        json.WriteEndObject();
        json.WriteNumber("ms", PlaytestLines.Number(handled.Milliseconds, 2, 1_000_000) ?? 0);
        return _lines.End();
    }

    /// <summary>A command's args, summarised (PlaytestLines.WriteRequest); a query's never. False when nothing was written.</summary>
    private static bool Summary(string message, Utf8JsonWriter json)
    {
        if (message.Length > CommandHost.MaxMessageBytes) return false;
        try
        {
            using var document = CanonicalJson.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
                !SchemaIds.IsSchema(schema.GetString(), "command") || !root.TryGetProperty("args", out var args)) return false;
            return PlaytestLines.WriteRequest(json, args);
        }
        catch (Exception error) when (error is CanonicalJsonException or JsonException) { return false; }
    }

    private void OnAction(string name, string detail)
    {
        if (!Recording || Finished || !PlaytestLines.ActionName.IsMatch(name)) return;
        var json = _lines.Begin(Now(), "action");
        json.WriteString("name", name);
        var words = PlaytestLines.Shown(detail, PlaytestLines.DetailTextCap);
        if (words.Length > 0) json.WriteString("detail", words);
        Write();
    }

    private void OnNotice(ScreenNotice notice)
    {
        if (!Recording || Finished || notice.Channel is not ("hud" or "gubble" or "status" or "refusal")) return;
        WriteNotice(notice.Channel, notice.Text, notice.Reason, notice.Code, notice.ActionId);
    }

    private void WriteNotice(string channel, string text, string reason = "", string code = "", string actionId = "")
    {
        var json = _lines.Begin(Now(), "notice");
        json.WriteString("channel", channel);
        json.WriteString("text", PlaytestLines.Shown(text, PlaytestLines.NoticeTextCap));
        var why = PlaytestLines.Shown(reason, PlaytestLines.ReasonTextCap);
        if (why.Length > 0) json.WriteString("reason", why);
        if (PlaytestLines.ErrorCodes.Contains(code)) json.WriteString("code", code);
        if (PlaytestLines.ActionId.IsMatch(actionId)) json.WriteString("action_id", actionId);
        Write();
    }

    private void OnWentHome(SmallPlayerController body)
    {
        if (Recording && !Finished) Schedule(Now() + HomeStillAfterS, "home");
    }

    /// <summary>The companion link's state as its cue shows it: a status notice when it changes, and a session when an AI joins or leaves.</summary>
    private void WatchLink(double t)
    {
        if (_bridge == null || !IsInstanceValid(_bridge)) return;
        var state = _bridge.State;
        if (state == _linkState) return;
        var was = _linkState ?? "offline";
        _linkState = state;
        if (was == state) return;
        if (was == "offline" && state != "offline") WriteSession("started", 1);
        WriteNotice("status", CompanionStatusCue.Looks.TryGetValue(state, out var look) ? look.Text : "");
        if (was != "offline" && state == "offline") WriteSession("ended", 0);
    }

    private void WriteSession(string state, int sessions)
    {
        var json = _lines.Begin(Now(), "session");
        json.WriteString("state", state);
        json.WriteString("principal", CommandHost.CompanionPrincipal);
        json.WriteNumber("sessions", sessions);
        Write();
    }

    // ---- the samples ----

    private void WriteReady(double t)
    {
        var json = _lines.Begin(t, "ready");
        PlaytestLines.WritePoint(json, "spawn_m", Player != null && IsInstanceValid(Player) ? Player.GlobalPosition : Room.SpawnFor("player").PositionM);
        if (Host != null && IsInstanceValid(Host)) json.WriteNumber("revision", Math.Max(0, Host.Revision));
        Write();
    }

    private static readonly StringName EntityIdMeta = "entity_id";

    private Vector3 BodyAt => Player != null && IsInstanceValid(Player) ? Player.GlobalPosition : Vector3.Zero;

    /// <summary>The view as the HUD has it: eye (F1), shoulder (F2), and the shelved diorama and isometric; null without a HUD.</summary>
    private string? ViewName => Hud == null || !IsInstanceValid(Hud) ? null : Hud.ViewMode switch { 0 => "eye", 2 => "diorama", 3 => "isometric", _ => "shoulder" };

    /// <summary>The breadcrumb: where the body is, how high over what, what it is doing, its meters and what it holds, the view, the Gubble.</summary>
    private void WriteBody(double t)
    {
        if (Player == null || !IsInstanceValid(Player)) return;
        var position = Player.GlobalPosition;
        var state = BodyState();
        var (height, level) = Below(position, state);
        var json = _lines.Begin(Math.Max(t, Now()), "body");
        PlaytestLines.WritePoint(json, "position_m", position);
        json.WriteNumber("height_m", PlaytestLines.Number(height, 3, 1000) ?? 0);
        if (level.Length > 0) json.WriteString("level", PlaytestLines.Shown(level, 80));
        if (Host != null && IsInstanceValid(Host))
        {
            var mode = Host.Mode;
            if (mode is "creative" or "challenge" or "live") json.WriteString("mode", mode);
            var vitals = Host.Vitals;
            var rules = Host.BodyRules;
            json.WriteNumber("hearts", Math.Clamp(rules.Quarters(vitals) / 4.0, 0, 30));
            json.WriteNumber("bar_m", PlaytestLines.Number(Math.Clamp(vitals.Stamina, 0, 4.8), 3, 4.8) ?? 0);
            if (rules.HasPool) json.WriteNumber("pips", Math.Clamp(vitals.Pips, 0, 40));
            var held = Host.HeldBy(CommandHost.PlayerAvatar);
            if (held != null && PlaytestLines.EntityId.IsMatch(held)) json.WriteString("holding", held);
            else json.WriteNull("holding");
        }
        json.WriteString("state", state);
        json.WriteString("view", ViewName ?? "shoulder");
        if (Hud != null && IsInstanceValid(Hud))
        {
            if (Hud.GubbleViewOpen) json.WriteBoolean("gubble_view", true);
            if (Hud.Look?.Observe == true) json.WriteBoolean("observe", true);
        }
        json.WriteStartObject("gubble");
        var distance = Companion != null && IsInstanceValid(Companion) ? Companion.GlobalPosition.DistanceTo(position) : 0f;
        json.WriteNumber("distance_m", PlaytestLines.Number(distance, 3, 10000) is { } d ? Math.Max(0, d) : 0);
        json.WriteString("link", _bridge != null && IsInstanceValid(_bridge) && _bridge.State is "listening" or "planning" or "acting" or "waiting" ? _bridge.State : "offline");
        json.WriteEndObject();
        Write();
    }

    /// <summary>What the body is doing, in the contract's words.</summary>
    private string BodyState()
    {
        var player = Player!;
        if (player.GoingHome) return "fading_home";
        if (player.Ride != SmallPlayerController.BubbleRide.None) return "carried";
        if (player.IsClimbing) return "climbing";
        if (player.IsDiving) return "diving";
        if (player.IsSwimming) return "swimming";
        var body = player.LastBodyState;
        if (!body.Grounded && body.AirTimeS > 0.15f) return "falling";
        if (body.SpeedMps > 0.02f) return body.Sprinting ? "running" : "walking";
        return "standing";
    }

    /// <summary>
    /// The height over the ground straight below and what that ground is (the room entity it belongs to: shell:floor, obj:box),
    /// by one ray down. In water, "water"; over nothing, the height over the floor it last stood on.
    /// </summary>
    private (double Height, string Level) Below(Vector3 position, string state)
    {
        if (state is "swimming" or "diving") return (0, "water");
        var space = Player!.GetWorld3D()?.DirectSpaceState;
        if (space == null) return (double.IsNaN(_lastFloorY) ? 0 : position.Y - _lastFloorY, "");
        // Every Godot value made here is freed here, never left to the .NET finalizer (S12).
        var exclude = new Godot.Collections.Array<Rid> { Player.GetRid() };
        using var excluded = (Godot.Collections.Array)exclude;
        if (Companion != null && IsInstanceValid(Companion)) exclude.Add(Companion.GetRid());
        using var query = PhysicsRayQueryParameters3D.Create(position + Vector3.Up * 0.02f, position + Vector3.Down * 200f, 1, exclude);
        using var hit = space.IntersectRay(query);
        if (hit.Count == 0) return (double.IsNaN(_lastFloorY) ? 0 : position.Y - _lastFloorY, "");
        var floor = hit["position"].AsVector3();
        _lastFloorY = floor.Y;
        var level = "";
        if (hit["collider"].AsGodotObject() is Node node)
        {
            for (Node? at = node; at != null && level.Length == 0; at = at.GetParent())
                if (at.HasMeta(EntityIdMeta)) level = at.GetMeta(EntityIdMeta).AsString();
        }
        return (Math.Max(0, position.Y - floor.Y), level);
    }

    /// <summary>How the frames ran since the last sample: real time between frames, the mean and the longest; the engine's monitors.</summary>
    private void WriteFrame(double t)
    {
        var window = t - _windowStart;
        // Stamped as it is written: a notice this frame may already have gone out after t.
        var json = _lines.Begin(Math.Max(t, Now()), "frame");
        json.WriteNumber("window_s", PlaytestLines.Number(Math.Clamp(window, 0.001, 60), 3, 60) ?? 5);
        json.WriteNumber("mean_ms", PlaytestLines.Number(_windowFrames > 0 ? _windowSumMs / _windowFrames : 0, 2, 100000) ?? 0);
        json.WriteNumber("longest_ms", PlaytestLines.Number(_windowLongestMs, 2, 100000) ?? 0);
        json.WriteNumber("longest_process_ms", PlaytestLines.Number(_longestProcessMs, 2, 100000) is { } lp ? Math.Max(0, lp) : 0);
        json.WriteNumber("longest_physics_ms", PlaytestLines.Number(_longestPhysicsMs, 2, 100000) is { } lq ? Math.Max(0, lq) : 0);
        json.WriteNumber("gc_pause_ms", Math.Round(Math.Max(0, GC.GetTotalPauseDuration().TotalMilliseconds - _windowPauseMs0), 1));
        json.WriteString("gc_collections", $"{GC.CollectionCount(0) - _windowGc0}/{GC.CollectionCount(1) - _windowGc1}/{GC.CollectionCount(2) - _windowGc2}");
        json.WriteNumber("physics_ms", PlaytestLines.Number(Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000, 2, 100000) is { } p ? Math.Max(0, p) : 0);
        json.WriteNumber("draw_calls", (long)Math.Max(0, Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)));
        json.WriteNumber("video_mb", Math.Round(Math.Max(0, Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed)) / 1048576.0, 1));
        json.WriteNumber("static_mb", Math.Round(Math.Max(0, Performance.GetMonitor(Performance.Monitor.MemoryStatic)) / 1048576.0, 1));
        json.WriteNumber("objects", (long)Math.Max(0, Performance.GetMonitor(Performance.Monitor.ObjectCount)));
        json.WriteNumber("frames", _windowFrames);
        PlaytestLines.WritePoint(json, "position_m", BodyAt);
        Write();
        _nextFrame = t + FrameEveryS;
        _windowStart = t;
        _windowFrames = 0;
        _windowSumMs = 0;
        _windowLongestMs = 0;
        _longestProcessMs = _longestPhysicsMs = 0;
        (_windowPauseMs0, _windowGc0, _windowGc1, _windowGc2) = (GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    // ---- the stills ----

    private void Schedule(double due, string why)
    {
        foreach (var (_, already) in _scheduled)
            if (already == why) return;
        _scheduled.Add((due, why));
    }

    /// <summary>
    /// A still of the frame last drawn: numbered, named for why it was taken (with a mark's number), its event written now and its
    /// picture queued for the writer. The steady beat is at half the width and height and skips a picture while the window is
    /// minimised, the writer is behind or its share of the cap is spent. Null when no still was taken.
    /// </summary>
    private string? TakeStill(string why, int? markNumber, bool full)
    {
        if (!Recording || Finished || _writer == null || Stills >= MaxStills) return null;
        var steady = why == "steady";
        if (steady && (Steady >= MaxSteadyStills || _writer.PendingStills >= MaxPendingSteady || Minimised())) return null;
        var grabbing = System.Diagnostics.Stopwatch.GetTimestamp();
        var image = Grab();
        if (image == null) return null;
        var grabMs = System.Diagnostics.Stopwatch.GetElapsedTime(grabbing).TotalMilliseconds;
        var (width, height) = (image.GetWidth(), image.GetHeight());
        var size = full ? (Vector2I?)null : new Vector2I(Math.Max(1, width / 2), Math.Max(1, height / 2));
        var number = Stills + 1;
        var file = $"{number:0000}-{why}{(markNumber is { } mark ? "-" + mark.ToString(CultureInfo.InvariantCulture) : "")}.jpg";
        Stills = number;
        if (steady) Steady++;
        var json = _lines.Begin(Now(), "still");
        json.WriteString("file", file);
        json.WriteString("why", why);
        if (ViewName is { } view) json.WriteString("view", view);
        PlaytestLines.WritePoint(json, "position_m", BodyAt);
        json.WriteNumber("width", size?.X ?? width);
        json.WriteNumber("height", size?.Y ?? height);
        // What the readback cost the main thread (a field the contract does not name: the kinds are open), so a hitch has a cause.
        json.WriteNumber("grab_ms", PlaytestLines.Number(grabMs, 2, 100000) ?? 0);
        Write();
        _writer.EnqueueStill(image, Path.Combine(Folder, "stills", file), size);
        return file;
    }

    private bool Minimised() => GrabForTests == null && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized;

    /// <summary>The frame last drawn, HUD included: a readback of the main viewport. Null when there is no picture.</summary>
    private Image? Grab()
    {
        // A run without a renderer has no picture to read back (asking for one is an engine error).
        if (GrabForTests == null && DisplayServer.GetName() == "headless") return null;
        Image? image;
        try { image = GrabForTests != null ? GrabForTests() : GetViewport()?.GetTexture()?.GetImage(); }
        catch (Exception error) { GD.Print("PLAYTEST_RECORD grab failed: " + error.GetType().Name); return null; }
        if (image == null || image.IsEmpty() || image.GetWidth() < 2 || image.GetHeight() < 2)
        {
            image?.Dispose();
            return null;
        }
        return image;
    }

    // ---- writing ----

    private void Write()
    {
        if (_lines.End() is { } line) Enqueue(line);
    }

    private void Enqueue(byte[] line)
    {
        if (_writer == null) return;
        Events++;
        _writer.Enqueue(line);
    }

    /// <summary>
    /// The clean quit: a last still (when the window closes), the quit line with the totals, every queued line and still written, and
    /// the manifest rewritten with its end. A record whose writer failed or could not finish keeps its launch manifest: unfinished,
    /// as a killed game's is.
    /// </summary>
    public void Finish(bool quitStill)
    {
        if (!Recording || Finished || _writer == null) return;
        if (quitStill) TakeStill("quit", null, full: true);
        Finished = true;
        var json = _lines.Begin(Now(), "quit");
        // The quit's own t is the seconds played: the manifest repeats it.
        var played = _lines.LastT;
        json.WriteNumber("played_s", played);
        json.WriteNumber("events", Events + 1);
        json.WriteNumber("stills", Stills);
        json.WriteNumber("marks", Marks);
        if (_lines.End() is { } line) Enqueue(line);
        var done = _writer.Stop(QuitWaitMs);
        if (!done || _writer.Failure.Length > 0 || _writer.LinesWritten != Events)
        {
            GD.Print($"PLAYTEST_RECORD unfinished: {Id} ({(done ? _writer.Failure : "the writer did not finish")})");
            return;
        }
        try { WriteManifest(ended: played); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.Print("PLAYTEST_RECORD manifest not rewritten: " + error.GetType().Name);
            return;
        }
        GD.Print($"PLAYTEST_RECORD quit: {Id}, {played.ToString("0.0", CultureInfo.InvariantCulture)} s, {Events} events, {Stills} stills, {Marks} marks");
    }

    /// <summary>
    /// What a killed game leaves (tests): the record stops where it is, no quit line, nothing queued written, the launch manifest
    /// as it was.
    /// </summary>
    internal void AbandonForTests()
    {
        if (!Recording || Finished || _writer == null) return;
        Finished = true;
        _writer.Abandon();
    }

    /// <summary>A still as the beat (steady, at half size) or a moment (full size) would take it (tests).</summary>
    internal string? TakeStillForTests(string why) => TakeStill(why, null, full: why != "steady");

    // ---- the folder and the manifest ----

    /// <summary>
    /// The record's folder: the launch's local date and time, then the room id (20261015-193000-island_example). A second launch in
    /// the same second takes the next free second.
    /// </summary>
    public static (string Id, string Folder) NewFolder(string root, DateTime localLaunch, string roomId)
    {
        for (var step = 0; step < 60; step++)
        {
            var id = localLaunch.AddSeconds(step).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + roomId;
            if (!RecordId.IsMatch(id)) throw new ArgumentException("The room id cannot name a record.");
            var folder = Path.Combine(root, id);
            if (Directory.Exists(folder) || File.Exists(folder)) continue;
            Directory.CreateDirectory(folder);
            return (id, folder);
        }
        throw new IOException("No free record name.");
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> records under root (by name, which is by date) and removes the older ones. Only
    /// folders named as records are touched, and never through a link. Returns how many were removed.
    /// </summary>
    public static int Prune(string root, int keep)
    {
        if (!Directory.Exists(root)) return 0;
        var records = new DirectoryInfo(root).GetDirectories()
            .Where(d => RecordId.IsMatch(d.Name) && (d.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(d => d.Name, StringComparer.Ordinal).ToList();
        var removed = 0;
        foreach (var old in records.Skip(Math.Max(0, keep)))
        {
            try
            {
                old.Delete(recursive: true);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { GD.Print("PLAYTEST_RECORD could not remove an old record: " + error.GetType().Name); }
        }
        return removed;
    }

    /// <summary>The facts the manifest gives at launch, kept for its rewrite at the quit.</summary>
    private Dictionary<string, object> LaunchFacts()
    {
        var facts = new Dictionary<string, object>();
        var team = Host?.SaveTeamIdentity;
        facts["team_id"] = team?.TeamId is { } teamId && Saves.TeamLayout.ValidTeamId(teamId) ? teamId : Saves.TeamLayout.DefaultTeamId;
        var mode = Host?.Mode is "creative" or "challenge" or "live" ? Host.Mode : "creative";
        facts["mode"] = mode;
        if (mode == "challenge") facts["difficulty"] = Host?.Difficulty is "gentle" or "standard" or "brave" ? Host.Difficulty : "standard";
        facts["style_id"] = Style?.PresetId ?? RoomWorld.DefaultStyleId;
        facts["style_version"] = Style?.PresetVersion ?? RoomWorld.DefaultStyleVersion;
        if (Host?.Rules is { } rules && rules.RulesId.Length > 0 && rules.RulesVersion >= 1)
        {
            facts["rules_id"] = rules.RulesId;
            facts["rules_version"] = rules.RulesVersion;
        }
        var size = DisplayServer.WindowGetSize();
        facts["width"] = Math.Clamp(size.X, 1, 16384);
        facts["height"] = Math.Clamp(size.Y, 1, 16384);
        facts["fullscreen"] = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
        var method = PlaytestLines.Shown(RenderingServer.GetCurrentRenderingMethod(), 40);
        facts["method"] = method.Length > 0 ? method : "unknown";
        var adapter = PlaytestLines.Shown(RenderingServer.GetVideoAdapterName(), 120);
        facts["adapter"] = adapter.Length > 0 ? adapter : "unknown";
        using var version = Engine.GetVersionInfo();
        var engine = PlaytestLines.Shown(version["string"].AsString(), 40);
        if (engine.Length > 0) facts["engine"] = engine;
        var log = ProjectSettings.GetSetting("debug/file_logging/log_path", "user://logs/godot.log").AsString().GetFile();
        facts["log_file"] = LogName.IsMatch(log) ? log : "godot.log";
        return facts;
    }

    /// <summary>manifest.json, through a temporary file: at launch, and at a clean quit with ended_utc, the seconds played and the counts.</summary>
    private void WriteManifest(double? ended)
    {
        var facts = _launch;
        var bytes = PlaytestLines.Document(json =>
        {
            json.WriteString("schema", SchemaIds.PlaytestManifest);
            json.WriteNumber("version", 1);
            json.WriteString("record_id", Id);
            json.WriteString("build", _build);
            json.WriteString("room_id", Room.RoomId);
            json.WriteString("room_sha256", Room.ManifestSha256);
            json.WriteString("team_id", (string)facts["team_id"]);
            json.WriteString("mode", (string)facts["mode"]);
            if (facts.TryGetValue("difficulty", out var difficulty)) json.WriteString("difficulty", (string)difficulty);
            json.WriteStartObject("style");
            json.WriteString("preset_id", (string)facts["style_id"]);
            json.WriteNumber("version", (int)facts["style_version"]);
            json.WriteEndObject();
            if (facts.TryGetValue("rules_id", out var rulesId))
            {
                json.WriteStartObject("rules");
                json.WriteString("rules_id", (string)rulesId);
                json.WriteNumber("version", (int)facts["rules_version"]);
                json.WriteEndObject();
            }
            json.WriteStartObject("window");
            json.WriteNumber("width", (int)facts["width"]);
            json.WriteNumber("height", (int)facts["height"]);
            json.WriteBoolean("fullscreen", (bool)facts["fullscreen"]);
            json.WriteEndObject();
            json.WriteStartObject("renderer");
            json.WriteString("method", (string)facts["method"]);
            json.WriteString("adapter", (string)facts["adapter"]);
            json.WriteEndObject();
            if (facts.TryGetValue("engine", out var engine)) json.WriteString("engine", (string)engine);
            json.WriteString("log_file", (string)facts["log_file"]);
            json.WriteNumber("beat_s", _launchBeat);
            json.WriteString("started_utc", PlaytestLines.Utc(_lines.StartedUtc));
            if (ended is { } played)
            {
                json.WriteString("ended_utc", _lines.UtcAt(played));
                json.WriteNumber("played_s", played);
                json.WriteStartObject("counts");
                json.WriteNumber("events", Events);
                json.WriteNumber("stills", Stills);
                json.WriteNumber("marks", Marks);
                json.WriteEndObject();
            }
        });
        var path = Path.Combine(Folder, "manifest.json");
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}
