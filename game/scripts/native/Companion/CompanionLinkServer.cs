using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Companion;

/// <summary>A frame the link refuses: over its size limit, or not one strict JSON object.</summary>
public sealed class LinkFrameException(string message) : Exception(message);

/// <summary>
/// The game's end of the companion link (docs/companion/TRANSPORT.md). The Python reference is
/// companion/src/garage_game_companion/link.py's LinkServer, and companion/tests/test_real_host.py runs its cases
/// against this end.
/// - Listens on a loopback literal only (127.0.0.1 or ::1), on an ephemeral port, and serves loopback peers only.
/// - A per-launch 256-bit token is proven by mutual HMAC-SHA256 over two fresh nonces; it never crosses the socket.
/// - The connection's principal is assigned here and never read from anything the connection sends; one companion
///   at a time; at most eight handshakes pending; five seconds to finish one; no lockout.
/// - Frames are a 4-byte big-endian length and one strict JSON object, written as canonical JSON v1. A frame over
///   its limit is refused before its body is read. There is no frame for approvals, principals or tokens.
/// Networking runs on the thread pool. Each request's message goes to <c>handle</c> as JSON text; the bridge answers
/// it on Godot's main thread through the command host.
/// </summary>
public sealed class CompanionLinkServer : IDisposable
{
    public const string Protocol = "enfractal.companion_link";
    public const int LinkVersion = 1;
    public const int MaxHandshakeFrame = 1024;
    /// <summary>A contract message is at most 65,536 bytes of canonical JSON; the headroom lets the game answer an oversized one with request_invalid.</summary>
    public const int MaxRequestFrame = 131_072;
    /// <summary>A result is at most 262,144 bytes of canonical JSON, plus an envelope of under 64.</summary>
    public const int MaxResponseFrame = 262_144 + 4_096;
    public const int MaxPendingHandshakes = 8;
    /// <summary>Connections served at once, handshakes and refusals included; any more are closed unanswered.</summary>
    public const int MaxConnections = 64;
    /// <summary>Frame nesting this reader accepts; the host refuses a message nested deeper than canonical JSON allows with request_invalid.</summary>
    public const int FrameNestingLimit = 256;
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex Hex64 = new(@"\A[0-9a-f]{64}\z", RegexOptions.Compiled);
    private static readonly byte[] ServerLabel = Encoding.ASCII.GetBytes($"{Protocol}/{LinkVersion}|server|");
    private static readonly byte[] ClientLabel = Encoding.ASCII.GetBytes($"{Protocol}/{LinkVersion}|client|");

    private readonly Func<string, Task<string>> _handle;
    private readonly Action<string>? _sessionEvent;
    private readonly byte[] _key;
    private readonly IPAddress _address;
    private readonly TimeSpan _handshakeTimeout;
    private readonly int _maxPending;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;
    private Connection? _active;
    private int _pending;
    private int _live;
    private bool _stopped;

    public string RoomId { get; }
    public string Principal { get; }
    public string Avatar { get; }
    /// <summary>The literal the session file names: 127.0.0.1 or ::1.</summary>
    public string Host { get; }
    public int Port { get; private set; }
    /// <summary>64 lowercase hex digits, new every launch. Written only to the session file; never logged and never sent.</summary>
    public string Token { get; }
    /// <summary>Whether an authenticated companion is connected now.</summary>
    public bool HasSession { get { lock (_gate) return _active != null; } }
    /// <summary>
    /// (event, detail) for the console and tests, raised on the link's threads, refusals included, so a handler must
    /// only count. Details come from a small fixed set (codes, exception type names). Never carries the token.
    /// </summary>
    public event Action<string, string>? Note;

    /// <param name="handle">Answers one enfractal.command or enfractal.query (JSON text) with enfractal.result text.</param>
    /// <param name="sessionEvent">Told "start" and "end" around each authenticated session, so the host can keep per-session state from outliving it.</param>
    public CompanionLinkServer(string roomId, Func<string, Task<string>> handle, Action<string>? sessionEvent = null,
        string host = "127.0.0.1", string principal = CommandHost.CompanionPrincipal, string avatar = CommandHost.CompanionAvatarId,
        string? token = null, TimeSpan? handshakeTimeout = null, int maxPendingHandshakes = MaxPendingHandshakes)
    {
        if (host is not ("127.0.0.1" or "::1")) throw new ArgumentException("The companion link listens on 127.0.0.1 or ::1 only.", nameof(host));
        if (token != null && !Hex64.IsMatch(token)) throw new ArgumentException("A session token is 64 lowercase hex digits.", nameof(token));
        RoomId = roomId;
        Principal = principal;
        Avatar = avatar;
        Host = host;
        _address = IPAddress.Parse(host);
        _handle = handle;
        _sessionEvent = sessionEvent;
        Token = token ?? RandomHex(32);
        _key = Convert.FromHexString(Token);
        _handshakeTimeout = handshakeTimeout ?? HandshakeTimeout;
        _maxPending = maxPendingHandshakes;
    }

    /// <summary>Start listening on an ephemeral port; returns the port.</summary>
    public int Start()
    {
        lock (_gate)
        {
            if (_stopped) throw new InvalidOperationException("The link was stopped.");
            if (_listener != null) return Port;
            _listener = new TcpListener(_address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        _ = Task.Run(AcceptLoop);
        return Port;
    }

    public void Stop()
    {
        Connection? active;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            active = _active;
        }
        _stopping.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        active?.Client.Close();
    }

    public void Dispose() => Stop();

    private void Log(string name, string detail = "")
    {
        try { Note?.Invoke(name, detail); }
        catch (Exception) { /* the console's bookkeeping never breaks the link */ }
    }

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private sealed class Connection(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = client.GetStream();
        /// <summary>Set when the handshake timed out: a handshake finishing afterwards must not take the companion's slot.</summary>
        public bool Abandoned;
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_stopping.Token); }
            catch (Exception) when (_stopping.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        // A flood of connections costs a bounded amount of work: past the cap they are closed unanswered.
        if (Interlocked.Increment(ref _live) > MaxConnections)
        {
            Interlocked.Decrement(ref _live);
            Log("dropped", "too many connections");
            client.Close();
            return;
        }
        try { await ServeOne(client); }
        finally { Interlocked.Decrement(ref _live); }
    }

    private async Task ServeOne(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address))
            {
                client.Close();
                return;
            }
            var connection = new Connection(client);
            if (Interlocked.Increment(ref _pending) > _maxPending)
            {
                Interlocked.Decrement(ref _pending);
                await Refuse(connection, "busy");
                return;
            }
            bool authenticated;
            try { authenticated = await HandshakeWithin(connection); }
            finally { Interlocked.Decrement(ref _pending); }
            if (!authenticated) return;
            Log("authenticated", Principal);
            SessionEvent("start");
            try { await Requests(connection); }
            catch (Exception error)
            {
                // A broken connection or a bug: close this connection and keep listening.
                Log("connection_failed", error.GetType().Name);
                await Refuse(connection, "frame_invalid");
            }
            finally
            {
                lock (_gate)
                {
                    if (_active == connection) _active = null;
                }
                client.Close();
                Log("closed", Principal);
                SessionEvent("end");
            }
        }
        catch (Exception error)
        {
            Log("serve_failed", error.GetType().Name);
            client.Close();
        }
    }

    private void SessionEvent(string name)
    {
        try { _sessionEvent?.Invoke(name); }
        catch (Exception error) { Log("session_hook_failed", error.GetType().Name); }
    }

    /// <summary>The whole handshake within the timeout. A malformed handshake is answered, never left hanging.</summary>
    private async Task<bool> HandshakeWithin(Connection connection)
    {
        var handshake = Handshake(connection);
        if (await Task.WhenAny(handshake, Task.Delay(_handshakeTimeout)) != handshake)
        {
            lock (_gate)
            {
                connection.Abandoned = true;
                if (_active == connection) _active = null;
            }
            _ = handshake.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            await Refuse(connection, "handshake_timeout");
            return false;
        }
        try { return await handshake; }
        catch (Exception error)
        {
            Log("handshake_failed", error.GetType().Name);
            await Refuse(connection, "handshake_invalid");
            return false;
        }
    }

    private async Task<bool> Handshake(Connection connection)
    {
        string clientNonce;
        using (var hello = await ReadFrame(connection.Stream, MaxHandshakeFrame))
        {
            var root = hello.RootElement;
            if (!HasExactly(root, "type", "protocol", "version", "client_nonce") || Str(root, "type") != "hello" || Str(root, "protocol") != Protocol
                || !IsOne(root.GetProperty("version")) || Str(root, "client_nonce") is not { } nonce || !Hex64.IsMatch(nonce))
            {
                await Refuse(connection, "hello_invalid");
                return false;
            }
            clientNonce = nonce;
        }
        var serverNonce = RandomHex(32);
        await Send(connection, new JsonObject
        {
            ["type"] = "challenge", ["protocol"] = Protocol, ["version"] = LinkVersion, ["server_nonce"] = serverNonce,
            ["server_proof"] = Proof(_key, server: true, clientNonce, serverNonce),
        });
        using (var auth = await ReadFrame(connection.Stream, MaxHandshakeFrame))
        {
            var root = auth.RootElement;
            var proof = Str(root, "client_proof");
            if (!HasExactly(root, "type", "client_proof") || Str(root, "type") != "auth" || proof == null || !Hex64.IsMatch(proof)
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(proof), Encoding.ASCII.GetBytes(Proof(_key, server: false, clientNonce, serverNonce))))
            {
                await Refuse(connection, "auth_failed");
                return false;
            }
        }
        bool busy;
        lock (_gate)
        {
            if (connection.Abandoned || _stopped) return false;
            busy = _active != null;
            if (!busy) _active = connection;
        }
        if (busy)
        {
            await Refuse(connection, "busy");
            return false;
        }
        try
        {
            await Send(connection, new JsonObject
            {
                ["type"] = "ready", ["session_id"] = RandomHex(16), ["principal"] = Principal, ["avatar"] = Avatar, ["room_id"] = RoomId,
                ["limits"] = new JsonObject { ["max_request_frame"] = MaxRequestFrame, ["max_response_frame"] = MaxResponseFrame },
            });
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (_active == connection) _active = null;
            }
            throw;
        }
        return true;
    }

    private static bool IsOne(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number == 1.0;

    private async Task Requests(Connection connection)
    {
        while (true)
        {
            JsonDocument frame;
            try { frame = await ReadFrame(connection.Stream, MaxRequestFrame); }
            catch (LinkFrameException)
            {
                Log("frame_invalid", "unreadable");
                await Refuse(connection, "frame_invalid");
                return;
            }
            catch (Exception) { return; } // the companion went away
            long seq;
            string message;
            using (frame)
            {
                var root = frame.RootElement;
                if (!HasExactly(root, "type", "seq", "message") || Str(root, "type") != "request" || !TrySeq(root.GetProperty("seq"), out seq))
                {
                    // Only requests travel this way: there is no frame for approvals, principals or tokens.
                    Log("frame_invalid", "not a request frame");
                    await Refuse(connection, "frame_invalid");
                    return;
                }
                message = root.GetProperty("message").GetRawText();
            }
            // The principal is the connection's, assigned at authentication. Nothing in the frame sets it.
            var result = await _handle(message);
            var response = ResponseFrame(seq, result);
            if (response.Length - 4 > MaxResponseFrame) response = ResponseFrame(seq, TooLarge(result));
            try
            {
                await connection.Stream.WriteAsync(response);
                await connection.Stream.FlushAsync();
            }
            catch (Exception) { return; }
        }
    }

    private static bool TrySeq(JsonElement value, out long seq)
    {
        seq = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        foreach (var c in value.GetRawText())
            if (!char.IsAsciiDigit(c)) return false;
        return value.TryGetInt64(out seq) && seq > 0 && seq < (1L << 53);
    }

    private static byte[] ResponseFrame(long seq, string result) =>
        // Canonical JSON v1: members in code-point order (message, seq, type); the host's result text is already canonical.
        WithLength(Encoding.UTF8.GetBytes("{\"message\":" + result + ",\"seq\":" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"type\":\"response\"}"));

    /// <summary>The game never sends a frame the companion would have to refuse.</summary>
    private static string TooLarge(string result)
    {
        var smaller = new JsonObject();
        try
        {
            if (JsonNode.Parse(result) is JsonObject full)
                foreach (var key in new[] { "schema", "version", "op", "action_id", "query_id", "principal", "room_id", "revision", "replayed", "preview", "at_utc" })
                    if (full[key] is { } value) smaller[key] = value.DeepClone();
        }
        catch (JsonException) { }
        smaller["ok"] = false;
        smaller["error"] = new JsonObject { ["code"] = "internal_error", ["message"] = "The answer was too large to send.", ["retryable"] = false };
        return CanonicalJson.Text(smaller);
    }

    private static async Task Send(Connection connection, JsonObject document)
    {
        await connection.Stream.WriteAsync(WithLength(CanonicalJson.Bytes(document)));
        await connection.Stream.FlushAsync();
    }

    private async Task Refuse(Connection connection, string code)
    {
        Log("refused", code);
        try { await Send(connection, new JsonObject { ["type"] = "refused", ["code"] = code }); }
        catch (Exception) { /* the peer may already be gone */ }
        connection.Client.Close();
    }

    // ---- framing ----

    private static byte[] WithLength(byte[] body)
    {
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>
    /// Read one frame. A length over the limit is refused before the body is read; the body must be one strict JSON
    /// object (no byte-order mark, duplicate key at any depth, comment, trailing comma, or number that overflows a double).
    /// </summary>
    public static async Task<JsonDocument> ReadFrame(Stream stream, int limit)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > (uint)limit) throw new LinkFrameException($"frame of {length} bytes is outside the limit of {limit}");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return ParseFrame(body);
    }

    public static JsonDocument ParseFrame(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) throw new LinkFrameException("a frame never starts with a byte-order mark");
        JsonDocument document;
        try
        {
            Prescan(body);
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = FrameNestingLimit, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException error) { throw new LinkFrameException("frame is not strict JSON: " + error.Message); }
        catch (ArgumentException error) { throw new LinkFrameException("frame is not UTF-8 JSON: " + error.Message); }
        catch (InvalidOperationException error) { throw new LinkFrameException("frame is not strict JSON: " + error.Message); }
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new LinkFrameException("a frame is one JSON object");
        }
        return document;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Invalid UTF-8 anywhere (GetRawText would quietly turn it into U+FFFD), duplicate keys at any depth and numbers
    /// that overflow a double, all of which JsonDocument would accept. Whatever else a message holds (a lone surrogate
    /// escape, a number canonical JSON cannot write) reaches the host, which answers request_invalid.
    /// </summary>
    private static void Prescan(byte[] body)
    {
        try { _ = StrictUtf8.GetCharCount(body); }
        catch (DecoderFallbackException) { throw new LinkFrameException("a frame is not UTF-8"); }
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = FrameNestingLimit });
        var scopes = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: scopes.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                case JsonTokenType.StartArray: scopes.Push(null); break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray: scopes.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (scopes.Peek() is { } names && !names.Add(reader.GetString()!)) throw new LinkFrameException("a frame has a duplicate key");
                    break;
                case JsonTokenType.Number:
                    if (!reader.TryGetDouble(out var number) || double.IsInfinity(number)) throw new LinkFrameException("a number overflows a double");
                    break;
            }
        }
    }

    private static bool HasExactly(JsonElement root, params string[] keys)
    {
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (Array.IndexOf(keys, property.Name) < 0) return false;
            count++;
        }
        return count == keys.Length;
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ---- proofs ----

    /// <summary>hex(HMAC-SHA256(key = the token's bytes, "enfractal.companion_link/1|server|" or "|client|" + client_nonce + "|" + server_nonce)).</summary>
    public static string Proof(byte[] key, bool server, string clientNonce, string serverNonce)
    {
        var label = server ? ServerLabel : ClientLabel;
        var message = Encoding.ASCII.GetBytes(clientNonce + "|" + serverNonce);
        var signed = new byte[label.Length + message.Length];
        label.CopyTo(signed, 0);
        message.CopyTo(signed, label.Length);
        return Convert.ToHexString(HMACSHA256.HashData(key, signed)).ToLowerInvariant();
    }
}
