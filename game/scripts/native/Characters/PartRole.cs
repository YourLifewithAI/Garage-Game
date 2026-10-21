using System;
using System.Collections.Generic;

namespace GarageGame.Native.Characters;

/// <summary>
/// What a part is, inferred from its name by <see cref="PartRoles.Infer"/>. Nothing in the game branches on a
/// character's name: every behaviour hangs off these roles, the sides and the converter's hints.
/// The snake_case form of each value (<see cref="PartRoles.Token"/>) is the name used in <c>rig.json</c>.
/// </summary>
public enum PartRole
{
    /// <summary>Anything unrecognised: rigid on its parent.</summary>
    Decor = 0,
    Body, Head, Eye, Pupil, Lid, Brow, Mouth, Nose, Cheek,
    /// <summary>A highlight riding an eye (eye_glint, highlight, shine).</summary>
    Glint,
    Arm, Hand, Paw,
    /// <summary>Digits, claws, fingers, toes and their pads.</summary>
    Digit,
    Leg, Foot, Shoe,
    /// <summary>Ink-ish marks: straps, stripes, bands, nostrils, lines.</summary>
    Mark,
    Hat, HatBand, Hair, Horn, Ear, Tail,
    Torch, Flame, Wand, Aura, Sparkle, Calyx, Arch, Heart,
}

/// <summary>
/// The character's own side. The converter names parts from the drawing's point of view: <c>screen_left</c> is the
/// character's right, at +X (the character faces -Z).
/// </summary>
public enum BodySide
{
    Center = 0,
    /// <summary>The character's right, +X: the converter's <c>screen_left</c>.</summary>
    Right = 1,
    /// <summary>The character's left, -X: the converter's <c>screen_right</c>.</summary>
    Left = 2,
}

/// <summary>The converter's motion hint for the whole character.</summary>
public enum MotionKind { Float, Hop, Waddle, Stride }

/// <summary>The converter's part kind: <c>solid</c> or <c>effect</c>.</summary>
public enum PartKind { Solid, Effect }

/// <summary>An effect part's motion hint; <see cref="None"/> when the part has none.</summary>
public enum EffectMotion { None = 0, Twinkle, Orbit, Drift, Flicker, Pulse }

/// <summary>The role grammar: part names to roles and sides. Pure functions of the name and the converter's hints.</summary>
public static class PartRoles
{
    private static readonly string[] Tokens = BuildTokens();

    private static readonly Dictionary<string, PartRole> Words = new(StringComparer.Ordinal)
    {
        ["body"] = PartRole.Body, ["torso"] = PartRole.Body, ["belly"] = PartRole.Body,
        ["head"] = PartRole.Head,
        ["eye"] = PartRole.Eye, ["eyeball"] = PartRole.Eye,
        ["pupil"] = PartRole.Pupil, ["iris"] = PartRole.Pupil,
        ["lid"] = PartRole.Lid, ["eyelid"] = PartRole.Lid,
        ["brow"] = PartRole.Brow, ["eyebrow"] = PartRole.Brow,
        ["mouth"] = PartRole.Mouth, ["smile"] = PartRole.Mouth, ["grin"] = PartRole.Mouth,
        ["nose"] = PartRole.Nose, ["snout"] = PartRole.Nose,
        ["cheek"] = PartRole.Cheek, ["blush"] = PartRole.Cheek,
        ["glint"] = PartRole.Glint, ["highlight"] = PartRole.Glint, ["shine"] = PartRole.Glint,
        ["arm"] = PartRole.Arm,
        ["hand"] = PartRole.Hand, ["mitten"] = PartRole.Hand, ["fist"] = PartRole.Hand,
        ["paw"] = PartRole.Paw,
        ["digit"] = PartRole.Digit, ["claw"] = PartRole.Digit, ["finger"] = PartRole.Digit, ["thumb"] = PartRole.Digit,
        ["toe"] = PartRole.Digit, ["pad"] = PartRole.Digit, ["nail"] = PartRole.Digit,
        ["leg"] = PartRole.Leg,
        ["foot"] = PartRole.Foot, ["feet"] = PartRole.Foot,
        ["shoe"] = PartRole.Shoe, ["boot"] = PartRole.Shoe,
        ["strap"] = PartRole.Mark, ["stripe"] = PartRole.Mark, ["band"] = PartRole.Mark, ["line"] = PartRole.Mark,
        ["mark"] = PartRole.Mark, ["nostril"] = PartRole.Mark, ["freckle"] = PartRole.Mark, ["spot"] = PartRole.Mark,
        ["hat"] = PartRole.Hat, ["cap"] = PartRole.Hat, ["brim"] = PartRole.Hat,
        ["hair"] = PartRole.Hair, ["hairline"] = PartRole.Hair, ["curl"] = PartRole.Hair, ["fringe"] = PartRole.Hair,
        ["bangs"] = PartRole.Hair, ["tuft"] = PartRole.Hair, ["ponytail"] = PartRole.Hair,
        ["horn"] = PartRole.Horn, ["antler"] = PartRole.Horn,
        ["ear"] = PartRole.Ear,
        ["tail"] = PartRole.Tail,
        ["torch"] = PartRole.Torch, ["lantern"] = PartRole.Torch,
        ["flame"] = PartRole.Flame, ["fire"] = PartRole.Flame,
        ["wand"] = PartRole.Wand, ["staff"] = PartRole.Wand,
        ["aura"] = PartRole.Aura, ["glow"] = PartRole.Aura, ["halo"] = PartRole.Aura,
        ["sparkle"] = PartRole.Sparkle, ["star"] = PartRole.Sparkle, ["twinkle"] = PartRole.Sparkle, ["glitter"] = PartRole.Sparkle,
        ["calyx"] = PartRole.Calyx, ["leaf"] = PartRole.Calyx, ["leaves"] = PartRole.Calyx, ["stem"] = PartRole.Calyx,
        ["arch"] = PartRole.Arch, ["rainbow"] = PartRole.Arch,
        ["heart"] = PartRole.Heart,
    };

    /// <summary>Words that qualify a part without naming it: sides, colours, places. Skipped when finding the head noun.</summary>
    private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal)
    {
        "screen", "left", "right", "red", "orange", "yellow", "green", "blue", "indigo", "violet", "purple", "pink",
        "black", "white", "brown", "grey", "gray", "gold", "silver", "top", "bottom", "front", "back", "upper", "lower",
        "inner", "outer", "big", "small",
    };

    /// <summary>The snake_case token of a role, as written in rig.json (e.g. <c>hat_band</c>).</summary>
    public static string Token(PartRole role) => Tokens[(int)role];

    /// <summary>Parses a rig.json role token; false for anything unknown.</summary>
    public static bool TryParse(string token, out PartRole role)
    {
        for (var i = 0; i < Tokens.Length; i++)
        {
            if (!string.Equals(Tokens[i], token, StringComparison.Ordinal)) continue;
            role = (PartRole)i;
            return true;
        }
        role = PartRole.Decor;
        return false;
    }

    /// <summary>
    /// The role of a part from its name: the head noun is the last word that is not a side, a colour, a place or a
    /// number (<c>eye_glint_screen_left</c> is a glint, <c>arch_red</c> an arch, <c>floating_sparkle_0</c> a sparkle);
    /// <c>hat_band</c> is its own role. An effect part with no recognised noun takes its role from its motion hint.
    /// </summary>
    public static PartRole Infer(string name, PartKind kind, EffectMotion effect)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var head = -1;
        for (var i = words.Length - 1; i >= 0; i--)
        {
            if (IsNumber(words[i]) || Qualifiers.Contains(words[i])) continue;
            head = i;
            break;
        }
        var role = PartRole.Decor;
        if (head >= 0 && Lookup(words[head], out var found))
        {
            role = found;
            if (role == PartRole.Mark && words[head].StartsWith("band", StringComparison.Ordinal) && head > 0 &&
                Lookup(words[head - 1], out var before) && before == PartRole.Hat)
                role = PartRole.HatBand;
        }
        if (kind == PartKind.Effect && role is not (PartRole.Flame or PartRole.Sparkle or PartRole.Aura))
            role = effect switch
            {
                EffectMotion.Flicker => PartRole.Flame,
                EffectMotion.Pulse => PartRole.Aura,
                _ => PartRole.Sparkle,
            };
        return role;
    }

    /// <summary>
    /// The side a name states: <c>screen_left</c> or a bare <c>left</c> is the character's right (+X), following the
    /// converter's screen convention; null when the name states none (the part then takes its parent's side).
    /// </summary>
    public static BodySide? SideFromName(string name)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = words.Length - 1; i >= 0; i--)
        {
            if (words[i] == "left") return BodySide.Right;
            if (words[i] == "right") return BodySide.Left;
        }
        return null;
    }

    /// <summary>Hands of either kind (a hand or a paw).</summary>
    public static bool IsHand(PartRole role) => role is PartRole.Hand or PartRole.Paw;

    /// <summary>Feet of either kind (a foot or a shoe).</summary>
    public static bool IsFoot(PartRole role) => role is PartRole.Foot or PartRole.Shoe;

    /// <summary>Thin painted geometry the look may draw as ink: marks, brows, mouths, lids.</summary>
    public static bool IsInkLike(PartRole role) => role is PartRole.Mark or PartRole.Brow or PartRole.Mouth or PartRole.Lid;

    /// <summary>Loose effects: flames, sparkles and auras.</summary>
    public static bool IsEffect(PartRole role) => role is PartRole.Flame or PartRole.Sparkle or PartRole.Aura;

    private static bool Lookup(string word, out PartRole role)
    {
        if (Words.TryGetValue(word, out role)) return true;
        if (word.Length > 3 && word.EndsWith("es", StringComparison.Ordinal) && Words.TryGetValue(word[..^2], out role)) return true;
        if (word.Length > 2 && word.EndsWith('s') && Words.TryGetValue(word[..^1], out role)) return true;
        role = PartRole.Decor;
        return false;
    }

    private static bool IsNumber(string word)
    {
        foreach (var c in word)
            if (c is < '0' or > '9') return false;
        return word.Length > 0;
    }

    private static string[] BuildTokens()
    {
        var values = Enum.GetValues<PartRole>();
        var result = new string[values.Length];
        foreach (var value in values)
        {
            var name = value.ToString();
            var token = new System.Text.StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]) && i > 0) token.Append('_');
                token.Append(char.ToLowerInvariant(name[i]));
            }
            result[(int)value] = token.ToString();
        }
        return result;
    }
}
