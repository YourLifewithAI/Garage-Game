using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>A message or value that is not strict JSON, or that canonical JSON cannot write.</summary>
public sealed class CanonicalJsonException(string message) : Exception(message);

/// <summary>
/// Strict JSON in, Garage Game canonical JSON v1 out. Fingerprints and content hashes are the SHA-256
/// of these bytes, and GDScript (scripts/creation_json.gd) and Python (tools/kernel/canonical_json.py)
/// produce the same bytes; tests/fixtures/kernel/ is the shared golden fixture. Rules: every number is
/// the nearest double, written as an integer when whole and within 2^53, otherwise as Python's
/// repr(float); strings escape only the quote, the backslash and U+0000..U+001F; object members are
/// sorted by Unicode code point; no whitespace.
/// </summary>
public static class CanonicalJson
{
    public const int Version = 1;
    public const double SafeInteger = 9007199254740992.0;
    /// <summary>At most this many nested arrays and objects (the root container is level 1), as in GDScript and Python.</summary>
    public const int MaxDepth = 64;
    /// <summary>A number literal with more significant digits than this is refused, as in GDScript and Python.</summary>
    public const int MaxNumberDigits = 800;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaxDepth + 1 };

    /// <summary>Orders keys by Unicode code point (UTF-16 order differs for astral characters).</summary>
    public static readonly IComparer<string> KeyOrder = Comparer<string>.Create(CompareCodePoints);

    public static JsonDocument Parse(string text)
    {
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw new CanonicalJsonException("text contains an unpaired surrogate"); }
        return Parse(bytes);
    }

    /// <summary>
    /// Strict parse: no byte-order mark, duplicate key, comment, trailing comma, non-finite number, number with
    /// more than 800 significant digits, nesting deeper than 64 or unpaired surrogate.
    /// </summary>
    public static JsonDocument Parse(byte[] utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
            throw new CanonicalJsonException("JSON must not start with a byte-order mark");
        Prescan(utf8);
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8, Options); }
        catch (JsonException error) { throw new CanonicalJsonException("not valid JSON: " + error.Message); }
        try
        {
            // Writing it once proves every number is finite and every string well formed.
            Write(document.RootElement, new StringBuilder(), 0);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static string Text(JsonElement element)
    {
        var output = new StringBuilder();
        Write(element, output, 0);
        return output.ToString();
    }

    public static string Text(JsonNode? node)
    {
        var output = new StringBuilder();
        Write(node, output, 0);
        return output.ToString();
    }

    /// <summary>The canonical text of an object without some of its own keys: Text of a copy with them removed, made without the copy.</summary>
    public static string TextWithout(JsonObject map, IReadOnlyCollection<string> leaveOut)
    {
        var keys = new List<string>(map.Count);
        foreach (var (key, _) in map)
            if (!leaveOut.Contains(key)) keys.Add(key);
        keys.Sort(CompareCodePoints);
        var output = new StringBuilder();
        output.Append('{');
        for (var index = 0; index < keys.Count; index++)
        {
            if (index > 0) output.Append(',');
            Quote(keys[index], output);
            output.Append(':');
            Write(map[keys[index]], output, 1);
        }
        output.Append('}');
        return output.ToString();
    }

    public static byte[] Bytes(JsonElement element) => StrictUtf8.GetBytes(Text(element));
    public static byte[] Bytes(JsonNode? node) => StrictUtf8.GetBytes(Text(node));
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Sha256Hex(JsonElement element) => Sha256Hex(Bytes(element));
    public static string Sha256Hex(JsonNode? node) => Sha256Hex(Bytes(node));

    /// <summary>The canonical spelling of a number: an integer when whole and within 2^53, otherwise Python's repr.</summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value)) throw new CanonicalJsonException("numbers must be finite");
        if (Math.Floor(value) == value && Math.Abs(value) <= SafeInteger)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        // .NET's round-trip format gives the shortest digits; only the layout differs from Python.
        var (digits, point) = Split(Math.Abs(value).ToString("R", CultureInfo.InvariantCulture));
        string text;
        if (point <= -4 || point > 16)
        {
            var exponent = point - 1;
            text = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + (exponent >= 0 ? "e+" : "e-") +
                Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (point <= 0) text = "0." + new string('0', -point) + digits;
        else if (point < digits.Length) text = digits[..point] + "." + digits[point..];
        else text = digits + new string('0', point - digits.Length) + ".0";
        return value < 0 ? "-" + text : text;
    }

    /// <summary>Significant digits and decimal point position (value = 0.DIGITS x 10^point) of a formatted positive number.</summary>
    private static (string Digits, int Point) Split(string text)
    {
        var exponent = 0;
        var marker = text.IndexOfAny(new[] { 'E', 'e' });
        if (marker >= 0)
        {
            exponent = int.Parse(text[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..marker];
        }
        var dot = text.IndexOf('.');
        var digits = dot < 0 ? text : text.Remove(dot, 1);
        var point = dot < 0 ? text.Length : dot;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..].TrimEnd('0');
        return (digits.Length == 0 ? "0" : digits, point - lead + exponent);
    }

    public static void Quote(string text, StringBuilder output)
    {
        output.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                output.Append(c).Append(text[++index]);
                continue;
            }
            if (char.IsSurrogate(c)) throw new CanonicalJsonException("strings must not contain unpaired surrogates");
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\t': output.Append("\\t"); break;
                case '\n': output.Append("\\n"); break;
                case '\f': output.Append("\\f"); break;
                case '\r': output.Append("\\r"); break;
                default:
                    if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }

    public static int CompareCodePoints(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        var length = Math.Min(a.Length, b.Length);
        for (var index = 0; index < length; index++)
        {
            var x = a[index];
            var y = b[index];
            if (x == y) continue;
            var xs = char.IsSurrogate(x);
            // A surrogate encodes a code point above U+FFFF, which sorts after every BMP character.
            if (xs == char.IsSurrogate(y)) return x.CompareTo(y);
            return xs ? 1 : -1;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static string ReadString(JsonElement element)
    {
        try { return element.GetString()!; }
        catch (InvalidOperationException) { throw new CanonicalJsonException("strings must not contain unpaired surrogates"); }
    }

    private static string ReadName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { throw new CanonicalJsonException("keys must not contain unpaired surrogates"); }
    }

    public static double ReadNumber(JsonElement element)
    {
        if (!double.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new CanonicalJsonException("numbers must be finite doubles");
        return value;
    }

    private static void Write(JsonElement element, StringBuilder output, int depth)
    {
        if (depth >= MaxDepth && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            throw new CanonicalJsonException($"JSON nests deeper than {MaxDepth} levels");
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = element.EnumerateObject().Select(p => (Name: ReadName(p), p.Value)).ToList();
                members.Sort((x, y) => CompareCodePoints(x.Name, y.Name));
                output.Append('{');
                for (var index = 0; index < members.Count; index++)
                {
                    if (index > 0) output.Append(',');
                    Quote(members[index].Name, output);
                    output.Append(':');
                    Write(members[index].Value, output, depth + 1);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) output.Append(',');
                    first = false;
                    Write(item, output, depth + 1);
                }
                output.Append(']');
                break;
            case JsonValueKind.String: Quote(ReadString(element), output); break;
            case JsonValueKind.Number: output.Append(Number(ReadNumber(element))); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new CanonicalJsonException("not a JSON value");
        }
    }

    private static void Write(JsonNode? node, StringBuilder output, int depth)
    {
        if (depth >= MaxDepth && node is JsonObject or JsonArray)
            throw new CanonicalJsonException($"JSON nests deeper than {MaxDepth} levels");
        switch (node)
        {
            case null: output.Append("null"); break;
            case JsonObject map:
                var keys = map.Select(p => p.Key).ToList();
                keys.Sort(CompareCodePoints);
                output.Append('{');
                for (var index = 0; index < keys.Count; index++)
                {
                    if (index > 0) output.Append(',');
                    Quote(keys[index], output);
                    output.Append(':');
                    Write(map[keys[index]], output, depth + 1);
                }
                output.Append('}');
                break;
            case JsonArray list:
                output.Append('[');
                for (var index = 0; index < list.Count; index++)
                {
                    if (index > 0) output.Append(',');
                    Write(list[index], output, depth + 1);
                }
                output.Append(']');
                break;
            case JsonValue value:
                if (value.TryGetValue<JsonElement>(out var element)) Write(element, output, depth);
                else if (value.TryGetValue<string>(out var text)) Quote(text, output);
                else if (value.TryGetValue<bool>(out var flag)) output.Append(flag ? "true" : "false");
                else if (value.TryGetValue<long>(out var whole)) output.Append(Number(whole));
                else if (value.TryGetValue<int>(out var small)) output.Append(Number(small));
                else if (value.TryGetValue<double>(out var number)) output.Append(Number(number));
                else if (value.TryGetValue<float>(out var single)) output.Append(Number(single));
                else throw new CanonicalJsonException("unsupported JSON value");
                break;
        }
    }

    /// <summary>
    /// JsonDocument keeps the last of duplicate keys and accepts any number of digits; the contracts refuse
    /// duplicates, and the three runtimes refuse the same overlong numbers and the same nesting, so scan first.
    /// </summary>
    private static void Prescan(byte[] utf8)
    {
        // The reader's own limit is one level deeper than the containers it allows, so count containers here.
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaxDepth + 1 });
        var scopes = new Stack<HashSet<string>>();
        var containers = 0;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        if (++containers > MaxDepth) throw new CanonicalJsonException($"JSON nests deeper than {MaxDepth} levels");
                        if (reader.TokenType == JsonTokenType.StartObject) scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject: containers--; scopes.Pop(); break;
                    case JsonTokenType.EndArray: containers--; break;
                    case JsonTokenType.Number:
                        if (SignificantDigits(reader.ValueSpan) > MaxNumberDigits)
                            throw new CanonicalJsonException($"a number has more than {MaxNumberDigits} significant digits");
                        break;
                    case JsonTokenType.PropertyName:
                        string name;
                        try { name = reader.GetString()!; }
                        catch (InvalidOperationException) { throw new CanonicalJsonException("keys must not contain unpaired surrogates"); }
                        if (!scopes.Peek().Add(name)) throw new CanonicalJsonException($"duplicate key '{name}'");
                        break;
                }
            }
        }
        catch (JsonException error) { throw new CanonicalJsonException("not valid JSON: " + error.Message); }
    }

    /// <summary>Digits of the mantissa (integer and fraction parts) without leading and trailing zeros, as GDScript counts them.</summary>
    public static int SignificantDigits(ReadOnlySpan<byte> literal)
    {
        int first = -1, last = -1, count = 0;
        foreach (var b in literal)
        {
            if (b is (byte)'e' or (byte)'E') break;
            if (b is < (byte)'0' or > (byte)'9') continue;
            if (b != (byte)'0')
            {
                if (first < 0) first = count;
                last = count;
            }
            count++;
        }
        return first < 0 ? 0 : last - first + 1;
    }
}
