using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace GarageGame.Native.Look;

/// <summary>How a surface of a drawn character is drawn (docs/look/CHARACTERS.md).</summary>
public enum CharacterSurfaceRole
{
    /// <summary>A felt figurine piece in exactly its GLB colour (MaterialLibrary.ForCharacterFelt).</summary>
    Plain,
    /// <summary>A drawn line or mark: unshaded, in its own dark colour, so it reads as ink at any hour.</summary>
    Ink,
    /// <summary>A sparkle, a flame, a glow: emissive, so the bloom catches it; tinted by the aura when the character has one.</summary>
    Effect,
    /// <summary>A see-through soap-bubble film (bubble.gdshader): the material hint "bubble".</summary>
    Bubble,
}

/// <summary>A part's material hint, as the converter writes it (character.json's material_hint, a GLB material's extras.enfractal_material).</summary>
public sealed record CharacterMaterialHint(string Kind, bool SeeThrough = false, string? Rim = null, Color? AuraColour = null, Color? FallbackColour = null);

/// <summary>
/// What the look needs to know about one part of a drawn character: its name (the GLB node's, and the rig's bone's), its kind
/// (solid or effect), its motion hint, its material hint, and optionally a role. A role of "ink", "effect", "bubble" or "plain"
/// decides the treatment outright (an "ink" role still draws a light surface, such as an eye's white, as felt); any other role
/// (a rig role such as "eye" or "mouth") is read like a word of the part's name. Everything is optional but the name: the look
/// infers the rest from the GLB's node and material names and extras, never from the character's name. Kind null means unknown
/// (a part named only by its mesh): its name then decides whether it is an effect.
/// </summary>
public sealed record CharacterLookPart(string Name, string? Kind = "solid", string? MotionHint = null, CharacterMaterialHint? MaterialHint = null, string? Role = null)
{
    public bool IsEffect => string.Equals(Kind, "effect", StringComparison.OrdinalIgnoreCase);
}

public static partial class CharacterLook
{
    /// <summary>The parts of a character.json (the converter's format): name, kind, motion hint and material hint of each.</summary>
    public static IReadOnlyList<CharacterLookPart> PartsFromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new FormatException("character.json has no parts array");
        var result = new List<CharacterLookPart>();
        foreach (var part in parts.EnumerateArray())
        {
            var name = part.GetProperty("name").GetString() ?? throw new FormatException("a part has no name");
            var kind = part.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "solid";
            string? motion = part.TryGetProperty("motion_hint", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("kind", out var mk)
                ? mk.GetString() : null;
            var hint = part.TryGetProperty("material_hint", out var h) && h.ValueKind == JsonValueKind.Object ? HintFrom(h) : null;
            result.Add(new CharacterLookPart(name, kind, motion, hint));
        }
        return result;
    }

    private static CharacterMaterialHint? HintFrom(JsonElement hint)
    {
        if (!hint.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;
        return new CharacterMaterialHint(
            kind.GetString()!,
            hint.TryGetProperty("see_through", out var s) && s.ValueKind == JsonValueKind.True,
            hint.TryGetProperty("rim", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            hint.TryGetProperty("aura_colour", out var a) && a.ValueKind == JsonValueKind.String ? ParseColour(a.GetString()) : null,
            hint.TryGetProperty("fallback_colour", out var f) && f.ValueKind == JsonValueKind.String ? ParseColour(f.GetString()) : null);
    }

    /// <summary>A "#RRGGBB" colour, or null when it is not one.</summary>
    public static Color? ParseColour(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#') return null;
        return uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f)
            : null;
    }

    /// <summary>
    /// The parts a loaded GLB names itself: one per node that carries a mesh or the converter's extras (enfractal_part, kept by
    /// Godot's glTF import as the node's "extras" meta where the engine keeps it), with the kind and hints those extras give.
    /// </summary>
    public static IReadOnlyList<CharacterLookPart> PartsFromNodes(Node root)
    {
        var result = new List<CharacterLookPart>();
        var seen = new HashSet<string>();
        foreach (var node in Descendants(root))
        {
            var extras = PartExtras(node);
            if (extras == null && node is not MeshInstance3D { Mesh: not null }) continue;
            var name = node.Name.ToString();
            if (!seen.Add(name)) continue;
            // FORMAT.md: kind defaults to solid in the converter's extras; with no extras the kind is unknown.
            var kind = extras is { } e ? (e.TryGetValue("kind", out var k) && k.VariantType == Variant.Type.String ? k.AsString() : "solid") : null;
            string? motion = extras is { } e2 && e2.TryGetValue("motion_hint", out var m) && m.VariantType == Variant.Type.Dictionary
                && m.AsGodotDictionary().TryGetValue("kind", out var mk) ? mk.AsString() : null;
            var hint = extras is { } e3 && e3.TryGetValue("material_hint", out var h) && h.VariantType == Variant.Type.Dictionary ? HintFrom(h.AsGodotDictionary()) : null;
            result.Add(new CharacterLookPart(name, kind, motion, hint));
        }
        return result;
    }

    private static Godot.Collections.Dictionary? PartExtras(Node node)
    {
        if (!node.HasMeta("extras")) return null;
        var extras = node.GetMeta("extras");
        if (extras.VariantType != Variant.Type.Dictionary) return null;
        var dictionary = extras.AsGodotDictionary();
        return dictionary.TryGetValue("enfractal_part", out var part) && part.VariantType == Variant.Type.Dictionary ? part.AsGodotDictionary() : null;
    }

    /// <summary>A GLB material's own hint: its extras.enfractal_material where the import kept it, else what its name says (bubble__part__colour).</summary>
    public static CharacterMaterialHint? HintOf(Material? material)
    {
        if (material == null) return null;
        if (material.HasMeta("extras") && material.GetMeta("extras").VariantType == Variant.Type.Dictionary
            && material.GetMeta("extras").AsGodotDictionary().TryGetValue("enfractal_material", out var hint) && hint.VariantType == Variant.Type.Dictionary
            && HintFrom(hint.AsGodotDictionary()) is { } fromExtras)
            return fromExtras;
        // FORMAT.md: a hinted material is named <kind>__<part>__<colour>.
        var pieces = (material.ResourceName ?? "").Split("__");
        return pieces.Length == 3 && pieces.All(p => p.Length > 0) ? new CharacterMaterialHint(pieces[0]) : null;
    }

    private static CharacterMaterialHint? HintFrom(Godot.Collections.Dictionary hint)
    {
        if (!hint.TryGetValue("kind", out var kind) || kind.VariantType != Variant.Type.String) return null;
        return new CharacterMaterialHint(
            kind.AsString(),
            hint.TryGetValue("see_through", out var s) && s.VariantType == Variant.Type.Bool && s.AsBool(),
            hint.TryGetValue("rim", out var r) && r.VariantType == Variant.Type.String ? r.AsString() : null,
            hint.TryGetValue("aura_colour", out var a) && a.VariantType == Variant.Type.String ? ParseColour(a.AsString()) : null,
            hint.TryGetValue("fallback_colour", out var f) && f.VariantType == Variant.Type.String ? ParseColour(f.AsString()) : null);
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    // ---------- the name grammar (the converter's part and palette words; never a character's name) ----------

    /// <summary>Words of a part's name (or a rig role) that make it a drawn mark: a dark surface on one draws as ink.</summary>
    public static readonly IReadOnlySet<string> MarkWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "eye", "eyes", "pupil", "pupils", "iris", "lid", "lids", "eyelid", "eyelids", "lash", "lashes", "brow", "brows", "eyebrow", "eyebrows",
        "mouth", "smile", "lip", "lips", "nostril", "nostrils", "stripe", "stripes", "band", "bands", "strap", "straps",
        "mark", "marks", "line", "lines", "outline", "whisker", "whiskers", "freckle", "freckles", "glint", "highlight", "cheek", "cheeks",
    };

    /// <summary>Words of a part's name that make it an effect when nothing else says its kind (an effect part's own kind wins).</summary>
    public static readonly IReadOnlySet<string> EffectWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "sparkle", "sparkles", "spark", "sparks", "flame", "flames", "twinkle", "glow", "aura",
    };

    /// <summary>The palette word the converter uses for drawn lines (FORMAT.md's "ink").</summary>
    public const string InkWord = "ink";

    /// <summary>The words of a name: split at underscores, digits dropped ("eye_screen_left" is eye, screen, left).</summary>
    public static IEnumerable<string> Words(string? name) =>
        (name ?? "").ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.All(char.IsDigit));

    public static bool IsMarkName(string? name) => Words(name).Any(MarkWords.Contains);

    public static bool IsEffectName(string? name) => Words(name).Any(EffectWords.Contains);

    /// <summary>Whether a colour is dark enough to be ink: relative luminance (linear) under DarkLuminance.</summary>
    public static bool IsDark(Color colour)
    {
        var linear = colour.SrgbToLinear();
        return 0.2126f * linear.R + 0.7152f * linear.G + 0.0722f * linear.B < DarkLuminance;
    }

    /// <summary>The darkest a mark may be drawn as felt: the converter's inks are 0.009 to 0.045, its darkest fills (a hat, a horn) as dark.</summary>
    public const float DarkLuminance = 0.06f;
}
