using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Companion;

/// <summary>
/// The in-game end of the player's AI (A2): the companion link in front of the kernel's command host.
/// - Owns the link for the player's account first: it holds the ownership lock (session.lock beside the session
///   file, opened with no sharing) before it checks or writes anything, for the link's whole life. Only then does it
///   start <see cref="CompanionLinkServer"/> on loopback and write the session file the MCP server reads
///   (user://companion/session.json; docs/companion/TRANSPORT.md), and it removes that file on exit under the same
///   lock. A second window, or a stale file left by a crash, can never take or keep the link from the lock's holder.
/// - Keeps the main thread safe from traffic that needs no token: the link's diagnostics are counted off the main
///   thread and summarised at most once a second, and the main thread runs a bounded number of requests and session
///   events each frame.
/// - Answers every request on Godot's main thread through <see cref="CommandHost.HandleObject"/> as
///   companion:local. Nothing a connection sends chooses the principal.
/// - Tells the host when a link session starts and ends (<see cref="CommandHost.SessionEvent"/>). Since Run 2 the
///   team's map and journal belong to the room and are saved with it, so a session no longer clears them.
/// - Shows the companion's state on its avatar (<see cref="CompanionStatusCue"/>): listening, planning, acting, or
///   waiting for the player's yes; nothing when no AI is linked.
/// - Keeps Trust's clock (M8): the host settles the Gubble's windows of clean initiative and checks the free early warning
///   a few times a second (<see cref="CommandHost.SettleTrust"/>), and the Gubble's cue shows the warning's words.
/// The goals themselves (follow, come, look_at, point_at) run in the host, which watches the body for their arrival.
/// The integrator's one-line wiring: RoomWorld calls <c>Companion.CompanionBridge.Attach(this)</c> after the command host.
/// </summary>
public partial class CompanionBridge : Node
{
    public const string DefaultSessionPath = "user://companion/session.json";
    /// <summary>After a request from the AI (a look at the world, a question), the avatar shows "planning" this long unless it acts.</summary>
    public const double PlanningHoldS = 3.0;
    /// <summary>A command-line switch for the player: run the room without the companion link.</summary>
    public const string DisableArgument = "--no-companion-link";
    /// <summary>The account's ownership of the link, beside the session file. Its holder is the one game serving the link.</summary>
    public const string LockFileName = "session.lock";
    /// <summary>The link's diagnostics reach the console at most this often, as counts.</summary>
    public const double NoteSummaryS = 1.0;
    /// <summary>Distinct kinds of diagnostic counted between summaries; any more are counted together.</summary>
    public const int MaxNoteKinds = 32;
    /// <summary>Requests and session events the main thread handles per frame; the rest wait for the next frame.</summary>
    public const int MaxWorkPerFrame = 32;
    /// <summary>Requests waiting for the main thread at once. The link sends one at a time, so this is never reached by the real companion.</summary>
    public const int MaxQueuedRequests = 64;
    /// <summary>How often Trust's clock runs (M8): settling is exact whenever it runs, so this only sets how soon the warning shows.</summary>
    public const double TrustTickS = 0.25;

    public CommandHost Host { get; private set; } = null!;
    public CompanionAvatar? Companion { get; private set; }
    /// <summary>user:// or an absolute path.</summary>
    public string SessionPath { get; private set; } = DefaultSessionPath;
    public CompanionLinkServer? Link { get; private set; }
    /// <summary>A separate token and listener grant only wish claim/update access, never world commands.</summary>
    public CompanionLinkServer? RunnerLink { get; private set; }
    public string RunnerSessionPath => Path.Combine(Path.GetDirectoryName(Globalize(SessionPath))!, "wish-runner.json");
    public string RunnerNotice { get; private set; } = "";
    /// <summary>Why the link is not running, for the player; empty while it runs.</summary>
    public string LinkNotice { get; private set; } = "";
    /// <summary>offline (no AI linked), listening, planning, acting or waiting.</summary>
    public string State { get; private set; } = "offline";
    public CompanionStatusCue? Cue { get; private set; }
    /// <summary>Requests answered for the AI this run (for the console and tests).</summary>
    public int Answered { get; private set; }

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly object _noteGate = new();
    private Dictionary<string, int> _notes = new(StringComparer.Ordinal);
    private int _otherNotes;
    private double _lastNotesS = double.NegativeInfinity;
    private int _queuedRequests;
    private FileStream? _ownership;
    private int _sessions;
    private double _lastRequestS = double.NegativeInfinity;
    private bool _closed;
    private double _trustClockS;

    public static CompanionBridge Attach(RoomWorld world) =>
        Create(world, CommandHost.Of(world) ?? throw new InvalidOperationException("Attach the command host before the companion bridge."), world.Companion);

    public static CompanionBridge Create(Node parent, CommandHost host, CompanionAvatar? companion, string sessionPath = DefaultSessionPath)
    {
        var bridge = new CompanionBridge { Name = "CompanionBridge", Host = host, Companion = companion, SessionPath = sessionPath };
        parent.AddChild(bridge);
        return bridge;
    }

    /// <summary>The bridge attached under a room world, if any.</summary>
    public static CompanionBridge? Of(Node world) => world.GetNodeOrNull<CompanionBridge>("CompanionBridge");

    public override void _Ready()
    {
        // Requests are answered even while the tree is paused, so the AI never waits on a menu.
        ProcessMode = ProcessModeEnum.Always;
        if (Companion != null && IsInstanceValid(Companion))
        {
            Cue = new CompanionStatusCue();
            // Under the name tag, so the cue keeps its billboard size and hides with it when the camera is close.
            (Companion.GetNodeOrNull<Label3D>("CompanionLabel") as Node ?? Companion).AddChild(Cue);
        }
        // The free early warning of Trust's bands (M8): the Gubble's cue says it, linked AI or not.
        Host.EarlyWarning += ShowWarning;
        if (OS.GetCmdlineUserArgs().Contains(DisableArgument))
        {
            LinkNotice = "The companion link is off for this run.";
            GD.Print("COMPANION_LINK off " + DisableArgument);
            return;
        }
        var sessionFile = Globalize(SessionPath);
        // Ownership first, before the session file is read or written: whichever game holds the lock serves the link.
        _ownership = TakeOwnership(sessionFile);
        if (_ownership == null)
        {
            LinkNotice = "Another Garage Game window holds the companion link; this one runs without it.";
            GD.Print("COMPANION_LINK not started: another game holds the companion link");
            return;
        }
        Link = new CompanionLinkServer(Host.Room.RoomId, Dispatch, name => _work.Enqueue(() => OnSession(name)));
        Link.Note += Noted;
        try
        {
            Link.Start();
            WriteSessionFile(sessionFile, Link.Host, Link.Port, Link.Token, Link.RoomId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            Link.Stop();
            Link = null;
            ReleaseOwnership();
            LinkNotice = "The companion link could not start.";
            GD.Print("COMPANION_LINK not started: " + error.GetType().Name);
            return;
        }
        // Never the token: the session file is the only place it is written.
        GD.Print($"COMPANION_LINK listening room={Host.Room.RoomId} host={Link.Host} port={Link.Port}");
        // This starts a local listener only. The player explicitly launches their chosen runner program.
        RunnerLink = new CompanionLinkServer(Host.Room.RoomId, DispatchRunner,
            name => _work.Enqueue(() => { if (!_closed && IsInstanceValid(Host)) Host.WishRunnerSession(name == "start"); }),
            principal: CommandHost.RunnerPrincipal, avatar: "");
        RunnerLink.Note += Noted;
        try
        {
            RunnerLink.Start();
            WriteSessionFile(RunnerSessionPath, RunnerLink.Host, RunnerLink.Port, RunnerLink.Token, RunnerLink.RoomId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            RunnerLink.Stop(); RunnerLink = null;
            RunnerNotice = "The local wish runner link could not start.";
        }
    }

    public override void _Process(double delta)
    {
        // A bounded share of each frame: the rest waits for the next one, so nothing on the link can hold the frame.
        for (var done = 0; done < MaxWorkPerFrame && _work.TryDequeue(out var work); done++) work();
        PrintNotes();
        UpdateState();
        _trustClockS += delta;
        if (_trustClockS >= TrustTickS && IsInstanceValid(Host))
        {
            _trustClockS = 0;
            Host.SettleTrust();
        }
    }

    private void ShowWarning(string words)
    {
        if (Cue != null && IsInstanceValid(Cue)) Cue.Warn(words);
    }

    public override void _ExitTree()
    {
        _closed = true;
        if (IsInstanceValid(Host))
        {
            Host.EarlyWarning -= ShowWarning;
            // The last windows settle before the room goes: a gain already earned is written to the team file now.
            Host.SettleTrust();
        }
        Link?.Stop();
        RunnerLink?.Stop();
        if (IsInstanceValid(Host)) Host.WishRunnerSession(false);
        // Anything still queued is refused, so no connection waits on a host that is gone.
        while (_work.TryDequeue(out var work)) work();
        // Still holding the lock: no other game can write a session file between the check and the delete.
        if (Link != null) DeleteSessionFileIfOurs(Globalize(SessionPath), Link.Token);
        if (RunnerLink != null) DeleteSessionFileIfOurs(RunnerSessionPath, RunnerLink.Token);
        ReleaseOwnership();
    }

    /// <summary>
    /// The account's ownership of the link: the lock file beside the session file, opened with no sharing (on POSIX
    /// .NET takes an exclusive flock). Held for the link's lifetime; the operating system frees it when this game
    /// ends, a crash included. Null when another game (or the mock game, through link.py's SessionLock) holds it.
    /// </summary>
    public static FileStream? TakeOwnership(string sessionFile)
    {
        var folder = Path.GetDirectoryName(sessionFile) ?? throw new IOException("The session file needs a folder.");
        try
        {
            Directory.CreateDirectory(folder);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = System.IO.FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new FileStream(Path.Combine(folder, LockFileName), options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void ReleaseOwnership()
    {
        _ownership?.Dispose();
        _ownership = null;
    }

    // ---- the link's diagnostics, counted off the main thread ----

    /// <summary>Called on the link's threads for every event, refusals included: a count, never a queued print.</summary>
    private void Noted(string name, string detail)
    {
        var key = detail.Length > 0 ? name + " " + detail : name;
        lock (_noteGate)
        {
            if (_notes.TryGetValue(key, out var count)) _notes[key] = count + 1;
            else if (_notes.Count < MaxNoteKinds) _notes[key] = 1;
            else _otherNotes++;
        }
    }

    /// <summary>At most once a second, one console line per kind of event, with its count.</summary>
    private void PrintNotes()
    {
        if (Seconds() - _lastNotesS < NoteSummaryS) return;
        Dictionary<string, int> notes;
        int other;
        lock (_noteGate)
        {
            if (_notes.Count == 0 && _otherNotes == 0) return;
            (notes, _notes) = (_notes, new Dictionary<string, int>(StringComparer.Ordinal));
            (other, _otherNotes) = (_otherNotes, 0);
        }
        _lastNotesS = Seconds();
        foreach (var (key, count) in notes) GD.Print(count == 1 ? $"COMPANION_LINK {key}" : $"COMPANION_LINK {key} (x{count})");
        if (other > 0) GD.Print($"COMPANION_LINK other events (x{other})");
    }

    // ---- requests, on the main thread ----

    private Task<string> Dispatch(string message) => Dispatch(message, runner: false);
    private Task<string> DispatchRunner(string message) => Dispatch(message, runner: true);
    private Task<string> Dispatch(string message, bool runner)
    {
        // Only an authenticated companion reaches here, one request at a time; a backlog means something is wrong,
        // and that connection is closed rather than queued.
        if (Interlocked.Increment(ref _queuedRequests) > MaxQueuedRequests)
        {
            Interlocked.Decrement(ref _queuedRequests);
            throw new InvalidOperationException("Too many requests are waiting for the game.");
        }
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            Interlocked.Decrement(ref _queuedRequests);
            if (_closed || !IsInstanceValid(Host))
            {
                answer.TrySetException(new ObjectDisposedException(nameof(CompanionBridge)));
                return;
            }
            // The authenticated endpoint assigns the role. No message can promote a runner to a companion. A look (view.look)
            // waits a frame or two for its picture on the main thread; everything else answers at once.
            _ = Answer(runner, message, answer);
        });
        return answer.Task;
    }

    private async Task Answer(bool runner, string message, TaskCompletionSource<string> answer)
    {
        try
        {
            var result = runner ? Host.HandleWishRunner(message) : await Host.HandleObjectAsync(message, CommandHost.CompanionPrincipal);
            Answered++;
            var op = result["op"]?.GetValue<string>() ?? "invalid";
            // A stop is not planning: the avatar shows the stop at once.
            if (!runner) _lastRequestS = op is "goal.stop" or "effect.stop" ? double.NegativeInfinity : Seconds();
            answer.TrySetResult(CanonicalJson.Text(result));
        }
        catch (Exception error) { answer.TrySetException(error); }
    }

    private void OnSession(string name)
    {
        if (_closed || !IsInstanceValid(Host)) return;
        Host.SessionEvent(CommandHost.CompanionPrincipal, name);
        _sessions = Math.Max(0, _sessions + (name == "start" ? 1 : -1));
        if (name == "start") _lastRequestS = double.NegativeInfinity;
        GD.Print($"COMPANION_LINK session {name}");
    }

    private static double Seconds() => Time.GetTicksMsec() / 1000.0;

    // ---- the visible state ----

    /// <summary>What the avatar shows: offline with no AI linked; otherwise waiting, acting, planning or listening, in that order.</summary>
    private void UpdateState()
    {
        string next;
        if (_sessions <= 0) next = "offline";
        else if (Host.PendingApprovals.Any(a => a.Principal == CommandHost.CompanionPrincipal)) next = "waiting";
        else if (Acting()) next = "acting";
        else if (Seconds() - _lastRequestS < PlanningHoldS) next = "planning";
        else next = "listening";
        if (next == State) return;
        State = next;
        Cue?.Display(next);
        GD.Print("COMPANION_STATE " + next);
    }

    /// <summary>
    /// The body is carrying out a goal: a goal job is running (a follow job only while it moves), a come is under way,
    /// it is still turning to look or point, or it follows the player and is moving.
    /// </summary>
    private bool Acting()
    {
        if (Companion == null || !IsInstanceValid(Companion)) return Host.RunningGoal(CommandHost.CompanionAvatarId) != null;
        if (Host.RunningGoal(CommandHost.CompanionAvatarId) is { } job && job.Goal != "follow") return true;
        return Companion.CurrentIntent switch
        {
            "come" => true,
            "follow" => Companion.FollowMoving,
            "look" or "point" => Companion.HasLookTarget && !Companion.FacesLookTarget,
            _ => false,
        };
    }

    // ---- the session file ----

    private static string Globalize(string path) =>
        path.StartsWith("user://", StringComparison.Ordinal) || path.StartsWith("res://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;

    /// <summary>
    /// Written atomically (a temporary file in the same folder, then a rename), UTF-8 with LF and no byte-order mark,
    /// mode 0600 on POSIX; on Windows the user's AppData ACL already limits it to the player's account.
    /// </summary>
    public static void WriteSessionFile(string path, string host, int port, string token, string roomId)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("The session file needs a folder.");
        Directory.CreateDirectory(directory);
        string Quoted(string value) => JsonSerializer.Serialize(value);
        var text = "{\n" +
            "  \"schema\": \"enfractal.companion_session\",\n" +
            "  \"version\": 1,\n" +
            $"  \"host\": {Quoted(host)},\n" +
            $"  \"port\": {port.ToString(CultureInfo.InvariantCulture)},\n" +
            $"  \"token\": {Quoted(token)},\n" +
            $"  \"room_id\": {Quoted(roomId)},\n" +
            $"  \"pid\": {System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)},\n" +
            $"  \"created_utc\": {Quoted(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}\n" +
            "}\n";
        var temp = Path.Combine(directory, Path.GetFileName(path) + "." + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = System.IO.FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temp, options)) stream.Write(new UTF8Encoding(false).GetBytes(text));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>On exit the file goes if it still holds this game's token. Called under the ownership lock, which every writer holds.</summary>
    public static void DeleteSessionFileIfOurs(string path, string token)
    {
        try
        {
            if (ReadSessionField(path, "token") == token) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string? ReadSessionField(string path, string name)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(name, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }
}
