using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// Where a value sits in a JSON file, for refusal reasons. Built without allocating and turned into text only when a
/// check fails, so validating thousands of GLB accessors costs no strings. A plain string converts implicitly.
/// </summary>
internal readonly struct JsonPath
{
    private readonly string _head;
    private readonly int _index;
    private readonly string? _key;
    private readonly int _element;

    public JsonPath(string head, int index = -1, string? key = null, int element = -1)
    {
        _head = head;
        _index = index;
        _key = key;
        _element = element;
    }

    public static implicit operator JsonPath(string path) => new(path);

    /// <summary>This path's member <paramref name="key"/> (one level; deeper paths are built as strings).</summary>
    public JsonPath Key(string key) => _key == null && _element < 0 ? new JsonPath(_head, _index, key) : new JsonPath(ToString(), -1, key);

    /// <summary>This path's array element <paramref name="element"/>.</summary>
    public JsonPath Element(int element) => _element < 0 ? new JsonPath(_head, _index, _key, element) : new JsonPath(ToString(), -1, null, element);

    public override string ToString()
    {
        var text = _index < 0 ? _head : _head + "[" + _index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
        if (_key != null) text += "." + _key;
        if (_element >= 0) text += "[" + _element.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
        return text;
    }
}

/// <summary>Strict reading of the character files: exact keys, exact types, finite numbers, no duplicate keys.</summary>
internal static class StrictJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 };
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 };

    public static JsonDocument Parse(byte[] bytes, string label, int maxBytes) => Parse(bytes, 0, bytes.Length, label, maxBytes);

    /// <summary>Parses <paramref name="count"/> bytes at <paramref name="offset"/> without copying them (a UTF-8 byte-order mark is skipped).</summary>
    public static JsonDocument Parse(byte[] bytes, int offset, int count, string label, int maxBytes, bool rejectDuplicateKeys = true)
    {
        if (count > maxBytes) throw new CharacterLoadException($"{label} is {count} bytes, over the {maxBytes}-byte limit");
        if (count >= 3 && bytes[offset] == 0xEF && bytes[offset + 1] == 0xBB && bytes[offset + 2] == 0xBF)
        {
            offset += 3;
            count -= 3;
        }
        var memory = new ReadOnlyMemory<byte>(bytes, offset, count);
        if (rejectDuplicateKeys) RejectDuplicateKeys(memory.Span, label);
        try
        {
            return JsonDocument.Parse(memory, DocumentOptions);
        }
        catch (JsonException error)
        {
            throw new CharacterLoadException($"{label} is malformed JSON ({error.Message})");
        }
    }

    /// <summary>JsonDocument keeps the last of two equal keys; the files must not have them. Keys compare by their UTF-8 bytes (unescaped when escaped).</summary>
    private static void RejectDuplicateKeys(ReadOnlySpan<byte> bytes, string label)
    {
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        var keys = new List<HashSet<string>>();
        var depth = -1;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        depth++;
                        while (keys.Count <= depth) keys.Add(new HashSet<string>(StringComparer.Ordinal));
                        keys[depth].Clear();
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                        var key = reader.GetString()!;
                        if (!keys[depth].Add(key)) throw new CharacterLoadException($"{label} repeats the key \"{Clip(key)}\"");
                        break;
                }
            }
        }
        catch (JsonException error)
        {
            throw new CharacterLoadException($"{label} is malformed JSON ({error.Message})");
        }
    }

    public static void ExpectObject(JsonElement element, JsonPath path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new CharacterLoadException($"{path} must be an object");
    }

    /// <summary>Refuses any key outside <paramref name="required"/> and <paramref name="optional"/>, and any missing required key.</summary>
    public static void Keys(JsonElement element, JsonPath path, IReadOnlyCollection<string> required, IReadOnlyCollection<string> optional)
    {
        ExpectObject(element, path);
        foreach (var property in element.EnumerateObject())
        {
            if (!Contains(required, property.Name) && !Contains(optional, property.Name))
                throw new CharacterLoadException($"{path} has an unknown key \"{Clip(property.Name)}\"");
        }
        foreach (var key in required)
            if (!element.TryGetProperty(key, out _)) throw new CharacterLoadException($"{path} is missing \"{key}\"");
    }

    public static JsonElement Get(JsonElement element, string key, JsonPath path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new CharacterLoadException($"{path} must be an object");
        if (!element.TryGetProperty(key, out var value)) throw new CharacterLoadException($"{path} is missing \"{key}\"");
        return value;
    }

    public static string String(JsonElement element, JsonPath path, int maxLength)
    {
        if (element.ValueKind != JsonValueKind.String) throw new CharacterLoadException($"{path} must be a string");
        var text = element.GetString()!;
        if (text.Length > maxLength) throw new CharacterLoadException($"{path} is longer than {maxLength} characters");
        return text;
    }

    public static bool Bool(JsonElement element, JsonPath path)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CharacterLoadException($"{path} must be true or false"),
        };
    }

    /// <summary>A finite number. JSON has no NaN or infinity, but 1e999 parses to infinity, so check.</summary>
    public static double Number(JsonElement element, JsonPath path)
    {
        if (element.ValueKind != JsonValueKind.Number) throw new CharacterLoadException($"{path} must be a number");
        if (!element.TryGetDouble(out var value) || !double.IsFinite(value)) throw new CharacterLoadException($"{path} is not a finite number");
        return value;
    }

    public static double Number(JsonElement element, JsonPath path, double min, double max)
    {
        var value = Number(element, path);
        if (value < min || value > max) throw new CharacterLoadException(FormattableString.Invariant($"{path} is {value:G6}, outside {min:G6} to {max:G6}"));
        return value;
    }

    public static int Integer(JsonElement element, JsonPath path, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value)) throw new CharacterLoadException($"{path} must be an integer");
        if (value < min || value > max) throw new CharacterLoadException(FormattableString.Invariant($"{path} is {value}, outside {min} to {max}"));
        return value;
    }

    public static Vector3 Vector(JsonElement element, JsonPath path, double limit)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3) throw new CharacterLoadException($"{path} must be three numbers");
        var x = Number(element[0], path.Element(0), -limit, limit);
        var y = Number(element[1], path.Element(1), -limit, limit);
        var z = Number(element[2], path.Element(2), -limit, limit);
        return new Vector3((float)x, (float)y, (float)z);
    }

    public static double[] Doubles(JsonElement element, JsonPath path, int count, double limit)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count) throw new CharacterLoadException($"{path} must be {count} numbers");
        var result = new double[count];
        for (var i = 0; i < count; i++) result[i] = Number(element[i], path.Element(i), -limit, limit);
        return result;
    }

    public static JsonElement Array(JsonElement element, JsonPath path, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new CharacterLoadException($"{path} must be an array");
        var length = element.GetArrayLength();
        if (length < min || length > max) throw new CharacterLoadException(FormattableString.Invariant($"{path} has {length} entries, outside {min} to {max}"));
        return element;
    }

    public static void Strings(JsonElement element, JsonPath path, int maxCount, int maxLength, bool nonEmpty = false)
    {
        Array(element, path, 0, maxCount);
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            var text = String(item, path.Element(i), maxLength);
            if (nonEmpty && string.IsNullOrWhiteSpace(text)) throw new CharacterLoadException($"{path.Element(i)} must not be empty");
            i++;
        }
    }

    /// <summary>A short, printable excerpt of untrusted text for a refusal reason.</summary>
    public static string Clip(string text)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (builder.Length >= 40) { builder.Append('…'); break; }
            builder.Append(char.IsControl(c) ? '?' : c);
        }
        return builder.ToString();
    }

    private static bool Contains(IReadOnlyCollection<string> keys, string key)
    {
        foreach (var candidate in keys)
            if (string.Equals(candidate, key, StringComparison.Ordinal)) return true;
        return false;
    }
}
