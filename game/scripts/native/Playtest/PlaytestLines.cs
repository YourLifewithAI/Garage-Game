using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Playtest;

/// <summary>
/// The lines of a play's events.jsonl and its manifest, as bytes (contracts/playtest-record.schema.json): strict JSON, UTF-8 without a
/// byte-order mark, one object per line ending in LF. Every name and free text goes through <see cref="KernelText.DisplayText"/> with a
/// short cap; a request is summarised flat (ids, numbers, points, short text) and never whole; the keys a record never carries
/// (data, picture, base64, token, session_file, save, secret, password, credential) are never written, at any depth. Times: t is the
/// seconds since the engine started, and utc is the launch's moment plus t, so neither ever runs backwards. Main thread only.
/// </summary>
internal sealed class PlaytestLines
{
    /// <summary>A line of events.jsonl is at most this many bytes (contracts: 8 KiB), its LF included.</summary>
    public const int MaxLineBytes = 8192;
    /// <summary>The caps on text in a line, in code points: a request's strings, a notice, the Gubble's reason, an action's detail.</summary>
    public const int RequestTextCap = 80;
    public const int NoticeTextCap = 240;
    public const int ReasonTextCap = 120;
    public const int DetailTextCap = 80;
    /// <summary>A request's summary holds at most this many fields, and a list in it at most this many items (the contract's bounds).</summary>
    public const int MaxRequestFields = 24;
    public const int MaxListItems = 16;

    /// <summary>The keys a record never carries (contracts/validate.py PLAYTEST_FORBIDDEN_KEYS).</summary>
    public static readonly HashSet<string> ForbiddenKeys = new(StringComparer.Ordinal)
    {
        "data", "picture", "base64", "token", "session_file", "save", "secret", "password", "credential",
    };

    /// <summary>
    /// Parts of a key that keep a request's field out of the summary, whatever it holds: a picture, a token, a save or a session
    /// in any spelling. And the host's own unguessable ids (an approval's, a preview's, a job's), which say nothing to a debrief.
    /// </summary>
    private static readonly string[] SecretParts = { "data", "picture", "image", "base64", "token", "session", "save", "secret", "password", "credential" };
    private static readonly HashSet<string> HostIds = new(StringComparer.Ordinal) { "request_id", "preview_id", "approval_id", "job_id" };

    /// <summary>contracts/game-command.schema.json $defs/error_code.</summary>
    public static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal)
    {
        "request_invalid", "version_unsupported", "field_unknown", "action_id_invalid", "action_id_conflict", "revision_conflict", "room_mismatch",
        "not_ready", "principal_unknown", "permission_denied", "actor_denied", "approval_required", "approval_mismatch", "approval_expired",
        "target_not_found", "target_protected", "target_busy", "target_too_heavy", "out_of_bounds", "target_unreachable", "occupied",
        "unsupported_capability", "invalid_args", "budget_exceeded", "rate_limited", "receipt_limit", "state_limit", "persistence_uncertain",
        "internal_error", "unavailable", "energy_exhausted",
    };

    // \A and \z, never ^ and $ (in .NET, $ also matches before a final newline).
    public static readonly Regex FieldName = new(@"\A[a-z][a-z0-9_]{0,31}\z", RegexOptions.Compiled);
    public static readonly Regex ActionName = new(@"\A[a-z][a-z0-9_]{0,63}\z", RegexOptions.Compiled);
    public static readonly Regex ActionId = new(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    public static readonly Regex Principal = new(@"\A(player|companion|guest|guest_companion|system):[a-z0-9_-]{1,32}\z", RegexOptions.Compiled);
    public static readonly Regex EntityId = new(@"\A((shell|obj|creation|avatar|effect|edit):[A-Za-z0-9_-]{1,64}|plant:[a-z][a-z0-9_]{0,31}-(0|[1-9][0-9]{0,7})|fractal:[a-z0-9]{1,32})\z", RegexOptions.Compiled);

    private static readonly JsonWriterOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false, SkipValidation = false };
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private readonly Utf8JsonWriter _json;
    private double _lastT;

    /// <summary>The t of the line last begun, as written.</summary>
    public double LastT => _lastT;

    /// <summary>The launch's moment (the engine's start), to the millisecond: started_utc, and what every line's utc counts from.</summary>
    public DateTime StartedUtc { get; }

    public PlaytestLines(DateTime startedUtc)
    {
        StartedUtc = startedUtc;
        _json = new Utf8JsonWriter(_buffer, LineOptions);
    }

    /// <summary>A UTC moment as the contract writes it, to the millisecond (2026-10-15T19:30:04.100Z).</summary>
    public static string Utc(DateTime moment) => moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The moment t seconds after the launch: the same t the line carries, so utc and t agree.</summary>
    public string UtcAt(double t) => Utc(StartedUtc.AddTicks((long)Math.Round(t * 1000) * TimeSpan.TicksPerMillisecond));

    /// <summary>Starts a line: t (to the millisecond), utc and kind. Write the kind's fields to the writer it returns, then <see cref="End"/>.</summary>
    public Utf8JsonWriter Begin(double t, string kind)
    {
        // Never before the line before (the contract's rule), whichever clock reading a caller brought.
        t = Math.Max(Math.Round(t, 3), _lastT);
        _lastT = t;
        _buffer.Clear();
        _json.Reset(_buffer);
        _json.WriteStartObject();
        _json.WriteNumber("t", t);
        _json.WriteString("utc", UtcAt(t));
        _json.WriteString("kind", kind);
        return _json;
    }

    /// <summary>The line's bytes with its LF; null when it would be longer than the contract allows (the caller writes a shorter one).</summary>
    public byte[]? End()
    {
        _json.WriteEndObject();
        _json.Flush();
        if (_buffer.WrittenCount + 1 > MaxLineBytes) return null;
        var line = new byte[_buffer.WrittenCount + 1];
        _buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }

    // ---- values ----

    /// <summary>Text as a record shows it: hidden characters gone, cut to cap code points; "" for nothing to show (a cleared line).</summary>
    public static string Shown(string? text, int cap)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return KernelText.DisplayText(text, cap);
    }

    /// <summary>A finite number rounded to digits, inside bounds; null for one that is not finite.</summary>
    public static double? Number(double value, int digits, double bound = 1e9)
    {
        if (!double.IsFinite(value)) return null;
        return Math.Round(Math.Clamp(value, -bound, bound), digits);
    }

    /// <summary>A point in metres, each coordinate to the millimetre and inside the contract's ±1000 m.</summary>
    public static void WritePoint(Utf8JsonWriter json, string name, Vector3 point)
    {
        json.WriteStartArray(name);
        foreach (var value in new[] { point.X, point.Y, point.Z }) json.WriteNumberValue(Number(value, 3, 1000) ?? 0);
        json.WriteEndArray();
    }

    // ---- a request's summary ----

    /// <summary>Whether a request's field may go into its summary: a plain name, not a secret's, not the host's own unguessable id.</summary>
    public static bool SummaryKey(string name)
    {
        if (!FieldName.IsMatch(name) || ForbiddenKeys.Contains(name) || HostIds.Contains(name)) return false;
        foreach (var part in SecretParts)
            if (name.Contains(part, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// A command's args as a flat summary: each field that is a short value (text through DisplayText, a number, a boolean, null) or a
    /// list of them (a point, a few ids), and the same one level down as parent_child (a placement's point, a creation's name). Deeper
    /// structure (a creation's parts, its nodes) is left out, so a whole message can never ride in as a summary. At most
    /// MaxRequestFields fields; false when there was nothing to summarise.
    /// </summary>
    public static bool WriteRequest(Utf8JsonWriter json, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        var written = new HashSet<string>(StringComparer.Ordinal);
        var started = false;
        void Field(string name, JsonElement value)
        {
            if (written.Count >= MaxRequestFields || !SummaryKey(name) || written.Contains(name) || !Summarisable(value)) return;
            if (!started) { json.WriteStartObject("request"); started = true; }
            written.Add(name);
            json.WritePropertyName(name);
            WriteSummaryValue(json, value);
        }
        foreach (var field in args.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.Object)
            {
                if (!SummaryKey(field.Name)) continue;
                foreach (var inner in field.Value.EnumerateObject()) Field(field.Name + "_" + inner.Name, inner.Value);
            }
            else Field(field.Name, field.Value);
        }
        if (started) json.WriteEndObject();
        return started;
    }

    private static bool Scalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null ||
        (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && Math.Abs(number) <= 1e9);

    private static bool Summarisable(JsonElement value)
    {
        if (Scalar(value)) return true;
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in value.EnumerateArray())
            if (!Scalar(item)) return false;
        return true;
    }

    private static void WriteSummaryValue(Utf8JsonWriter json, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            json.WriteStartArray();
            var count = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (count++ >= MaxListItems) break;
                WriteScalar(json, item);
            }
            json.WriteEndArray();
            return;
        }
        WriteScalar(json, value);
    }

    private static void WriteScalar(Utf8JsonWriter json, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String: json.WriteStringValue(Shown(value.GetString(), RequestTextCap)); break;
            case JsonValueKind.True: json.WriteBooleanValue(true); break;
            case JsonValueKind.False: json.WriteBooleanValue(false); break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var whole)) json.WriteNumberValue(whole);
                else json.WriteNumberValue(Number(value.GetDouble(), 4) ?? 0);
                break;
            default: json.WriteNullValue(); break;
        }
    }

    // ---- the manifest ----

    /// <summary>The manifest as indented JSON with LF line ends and a final LF.</summary>
    public static byte[] Document(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true }))
        {
            json.WriteStartObject();
            write(json);
            json.WriteEndObject();
        }
        var bytes = new List<byte>(buffer.WrittenCount + 1);
        foreach (var b in buffer.WrittenSpan)
            if (b != (byte)'\r') bytes.Add(b);
        bytes.Add((byte)'\n');
        return bytes.ToArray();
    }
}
