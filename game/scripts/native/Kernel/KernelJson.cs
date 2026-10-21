using Godot;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>Exact conversions between contract JSON and Godot Variants for the GDScript kernel.</summary>
public static class KernelJson
{
    /// <summary>
    /// JSON to Variant; every number becomes a double, as in Godot's own parser, but exactly. The Godot values made on the way
    /// (each key, each nested dictionary and array) are freed at once, not left to the .NET finalizer (S12); the caller owns, and
    /// may dispose, the Variant returned.
    /// </summary>
    public static Variant ToVariant(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                using var map = new Godot.Collections.Dictionary();
                foreach (var property in element.EnumerateObject())
                {
                    using var key = Variant.CreateFrom(property.Name);
                    using var item = ToVariant(property.Value);
                    map[key] = item;
                }
                return Variant.CreateFrom(map);
            }
            case JsonValueKind.Array:
            {
                using var list = new Godot.Collections.Array();
                foreach (var entry in element.EnumerateArray())
                {
                    using var item = ToVariant(entry);
                    list.Add(item);
                }
                return Variant.CreateFrom(list);
            }
            case JsonValueKind.String: return element.GetString()!;
            case JsonValueKind.Number: return CanonicalJson.ReadNumber(element);
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return default;
        }
    }

    public static Variant ToVariant(JsonNode? node)
    {
        if (node == null) return default;
        using var document = JsonDocument.Parse(CanonicalJson.Text(node));
        return ToVariant(document.RootElement);
    }

    /// <summary>
    /// Variant to JSON. Only JSON-shaped Variants are accepted. Every Godot value read on the way (each array, dictionary, key and
    /// item) is freed at once, not left to the .NET finalizer (S12); the Variant given stays the caller's.
    /// </summary>
    public static JsonNode? ToJson(Variant value)
    {
        switch (value.VariantType)
        {
            case Variant.Type.Nil: return null;
            case Variant.Type.Bool: return JsonValue.Create(value.AsBool());
            case Variant.Type.Int: return JsonValue.Create(value.AsInt64());
            case Variant.Type.Float:
                var number = value.AsDouble();
                if (!double.IsFinite(number)) throw new CanonicalJsonException("numbers must be finite");
                return JsonValue.Create(number);
            case Variant.Type.String:
            case Variant.Type.StringName: return JsonValue.Create(value.AsString());
            case Variant.Type.Array:
            {
                var list = new JsonArray();
                using var array = value.AsGodotArray();
                foreach (var item in array)
                    using (item) list.Add(ToJson(item));
                return list;
            }
            case Variant.Type.PackedStringArray:
                return new JsonArray(value.AsStringArray().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            case Variant.Type.Dictionary:
            {
                var map = new JsonObject();
                using var dictionary = value.AsGodotDictionary();
                foreach (var (key, item) in dictionary)
                {
                    using (key)
                    using (item)
                    {
                        if (key.VariantType != Variant.Type.String) throw new CanonicalJsonException("object keys must be strings");
                        map[key.AsString()] = ToJson(item);
                    }
                }
                return map;
            }
            default: throw new CanonicalJsonException($"{value.VariantType} is not a JSON value");
        }
    }

    public static JsonArray Vector(Vector3 value) => new(Num(value.X), Num(value.Y), Num(value.Z));

    /// <summary>A float32 coordinate written with the digits it was given, not float32 noise (0.6f is 0.6, not 0.6000000238).</summary>
    public static JsonNode Num(float value) => JsonValue.Create(Digits(value));

    /// <summary>The double a float32 reads as when written with its own shortest digits ("R"), formatted without a string (S12: nine a summary).</summary>
    public static double Digits(float value)
    {
        Span<char> text = stackalloc char[32];
        if (!value.TryFormat(text, out var written, "R", System.Globalization.CultureInfo.InvariantCulture))
            return double.Parse(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        return double.Parse(text[..written], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static JsonObject Box(Aabb box) => new() { ["min_m"] = Vector(box.Position), ["max_m"] = Vector(box.End) };

    public static Vector3 ReadVector(JsonElement element)
    {
        var values = element.EnumerateArray().Select(CanonicalJson.ReadNumber).ToArray();
        return new Vector3((float)values[0], (float)values[1], (float)values[2]);
    }

    public static Godot.Collections.Array VariantVector(Vector3 value) => new() { (double)value.X, (double)value.Y, (double)value.Z };

    public static Godot.Collections.Dictionary VariantBox(Aabb box) => new()
    {
        ["min_m"] = VariantVector(box.Position), ["max_m"] = VariantVector(box.End)
    };

    /// <summary>
    /// Plain text that satisfies the contract's display_text rule and length: every hidden character (the
    /// project's invisible-character rule, KernelText) becomes a space, and the cut falls on a code point.
    /// </summary>
    public static string DisplayText(string text, int maxLength) => KernelText.DisplayText(text, maxLength);

    public static string Utc(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
