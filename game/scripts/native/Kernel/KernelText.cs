using System;
using System.Collections.Generic;
using System.Text;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The project's invisible-character rule for untrusted world text (names, labels, notes), in C#.
/// scripts/creation_text.gd is the same rule in GDScript, line for line; Lane A's is
/// companion/src/garage_game_companion/textsafety.py. Requests that carry a hidden character are refused;
/// text the host emits has every hidden character replaced by a space.
///
/// Hidden, judged one code point at a time: controls (U+0000-U+001F, U+007F-U+009F), lone surrogates,
/// every Unicode format character (category Cf, the fixed Unicode 15.1/16.0 table below), U+034F, U+115F,
/// U+1160, U+180B-U+180F, U+2028-U+202E, U+2060-U+206F (U+2065 too, though unassigned), U+2800, U+3164,
/// U+FFA0, U+FFF9-U+FFFB, the variation selectors U+FE00-U+FE0F and all of plane 14 (U+E0000-U+EFFFF).
///
/// Emoji markers (founder decision, 6 October 2026) are allowed only where an emoji puts them: VS15 and VS16
/// directly after an Extended_Pictographic character or a keycap base (0-9, # or *), at most one per base;
/// ZWJ between two Extended_Pictographic characters, the first optionally followed by one VS16 or one
/// skin-tone modifier; the keycap U+20E3 directly after 0-9, # or *, optionally with one VS16 between.
/// Runs or any other placement of these markers are hidden.
/// </summary>
public static class KernelText
{
    public const int Vs15 = 0xFE0E;
    public const int Vs16 = 0xFE0F;
    public const int Zwj = 0x200D;
    public const int Keycap = 0x20E3;

    /// <summary>Unicode general category Cf (Unicode 15.1; unchanged in 16.0).</summary>
    public static readonly (int Low, int High)[] Format =
    {
        (0x00AD, 0x00AD), (0x0600, 0x0605), (0x061C, 0x061C), (0x06DD, 0x06DD), (0x070F, 0x070F), (0x0890, 0x0891),
        (0x08E2, 0x08E2), (0x180E, 0x180E), (0x200B, 0x200F), (0x202A, 0x202E), (0x2060, 0x2064), (0x2066, 0x206F),
        (0xFEFF, 0xFEFF), (0xFFF9, 0xFFFB), (0x110BD, 0x110BD), (0x110CD, 0x110CD), (0x13430, 0x1343F),
        (0x1BCA0, 0x1BCA3), (0x1D173, 0x1D17A), (0xE0001, 0xE0001), (0xE0020, 0xE007F),
    };

    /// <summary>Blank and invisible characters outside Cf, the variation selectors and plane 14.</summary>
    public static readonly (int Low, int High)[] Blank =
    {
        (0x034F, 0x034F), (0x115F, 0x1160), (0x180B, 0x180F), (0x2028, 0x202E), (0x2060, 0x206F), (0x2800, 0x2800),
        (0x3164, 0x3164), (0xFE00, 0xFE0F), (0xFFA0, 0xFFA0), (0xFFF9, 0xFFFB), (0xE0000, 0xEFFFF),
    };

    /// <summary>Extended_Pictographic (UTS #51, emoji-data.txt for Unicode 15.1), merged into ranges: the table Lane A uses.</summary>
    public static readonly (int Low, int High)[] Pictographic =
    {
        (0x00A9, 0x00A9), (0x00AE, 0x00AE), (0x203C, 0x203C), (0x2049, 0x2049), (0x2122, 0x2122), (0x2139, 0x2139),
        (0x2194, 0x2199), (0x21A9, 0x21AA), (0x231A, 0x231B), (0x2328, 0x2328), (0x2388, 0x2388), (0x23CF, 0x23CF),
        (0x23E9, 0x23F3), (0x23F8, 0x23FA), (0x24C2, 0x24C2), (0x25AA, 0x25AB), (0x25B6, 0x25B6), (0x25C0, 0x25C0),
        (0x25FB, 0x25FE), (0x2600, 0x2605), (0x2607, 0x2612), (0x2614, 0x2685), (0x2690, 0x2705), (0x2708, 0x2712),
        (0x2714, 0x2714), (0x2716, 0x2716), (0x271D, 0x271D), (0x2721, 0x2721), (0x2728, 0x2728), (0x2733, 0x2734),
        (0x2744, 0x2744), (0x2747, 0x2747), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757),
        (0x2763, 0x2767), (0x2795, 0x2797), (0x27A1, 0x27A1), (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2934, 0x2935),
        (0x2B05, 0x2B07), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x3030, 0x3030), (0x303D, 0x303D),
        (0x3297, 0x3297), (0x3299, 0x3299), (0x1F000, 0x1F0FF), (0x1F10D, 0x1F10F), (0x1F12F, 0x1F12F), (0x1F16C, 0x1F171),
        (0x1F17E, 0x1F17F), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F1AD, 0x1F1E5), (0x1F201, 0x1F20F), (0x1F21A, 0x1F21A),
        (0x1F22F, 0x1F22F), (0x1F232, 0x1F23A), (0x1F23C, 0x1F23F), (0x1F249, 0x1F3FA), (0x1F400, 0x1F53D), (0x1F546, 0x1F64F),
        (0x1F680, 0x1F6FF), (0x1F774, 0x1F77F), (0x1F7D5, 0x1F7FF), (0x1F80C, 0x1F80F), (0x1F848, 0x1F84F), (0x1F85A, 0x1F85F),
        (0x1F888, 0x1F88F), (0x1F8AE, 0x1F8FF), (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1FAFF), (0x1FC00, 0x1FFFD),
    };

    private static bool In((int Low, int High)[] ranges, int code)
    {
        int low = 0, high = ranges.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (code < ranges[middle].Low) high = middle - 1;
            else if (code > ranges[middle].High) low = middle + 1;
            else return true;
        }
        return false;
    }

    public static bool IsPictographic(int code) => In(Pictographic, code);

    private static bool IsKeycapBase(int code) => code is '#' or '*' or (>= '0' and <= '9');

    private static bool IsSkinTone(int code) => code is >= 0x1F3FB and <= 0x1F3FF;

    /// <summary>True for a code point a reader cannot see, judged alone. The emoji markers count as hidden here.</summary>
    public static bool IsHidden(int code) =>
        code < 0x20 || code is >= 0x7F and <= 0x9F || code is >= 0xD800 and <= 0xDFFF || code == Keycap || In(Format, code) || In(Blank, code);

    /// <summary>Code points of a .NET string; a lone surrogate stays as its own (hidden) code unit.</summary>
    public static int[] CodePoints(string text)
    {
        var codes = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                codes.Add(char.ConvertToUtf32(c, text[++index]));
                continue;
            }
            codes.Add(c);
        }
        return codes.ToArray();
    }

    /// <summary>True when the emoji marker at index is where an emoji puts it.</summary>
    public static bool MarkerInPlace(int[] codes, int index)
    {
        var code = codes[index];
        var before = index > 0 ? codes[index - 1] : -1;
        switch (code)
        {
            case Vs15 or Vs16:
                return IsPictographic(before) || IsKeycapBase(before);
            case Keycap:
                return IsKeycapBase(before) || (before == Vs16 && index >= 2 && IsKeycapBase(codes[index - 2]));
            case Zwj:
                if (index + 1 >= codes.Length || !IsPictographic(codes[index + 1])) return false;
                var at = index - 1;
                if (at >= 0 && (codes[at] == Vs16 || IsSkinTone(codes[at]))) at--;
                return at >= 0 && IsPictographic(codes[at]);
            default:
                return false;
        }
    }

    public static bool HiddenAt(int[] codes, int index, bool allowNewlines = false)
    {
        var code = codes[index];
        if (allowNewlines && code is '\n' or '\t') return false;
        if (code is Vs15 or Vs16 or Zwj or Keycap) return !MarkerInPlace(codes, index);
        return IsHidden(code);
    }

    public static bool HasHidden(string text, bool allowNewlines = false)
    {
        var codes = CodePoints(text);
        for (var index = 0; index < codes.Length; index++)
            if (HiddenAt(codes, index, allowNewlines)) return true;
        return false;
    }

    /// <summary>Every hidden character becomes a space, each judged against its original neighbours.</summary>
    public static int[] Clean(int[] codes)
    {
        var result = new int[codes.Length];
        for (var index = 0; index < codes.Length; index++) result[index] = HiddenAt(codes, index) ? ' ' : codes[index];
        return result;
    }

    /// <summary>
    /// Single-line text as the kernel emits it: hidden characters become spaces, runs of spaces collapse, the
    /// ends lose characters up to U+0020, and it is cut to maxLength code points (never inside a surrogate
    /// pair), then cleaned again in case the cut stranded an emoji marker.
    /// </summary>
    public static string DisplayText(string text, int maxLength)
    {
        // The rule is a pure function of the text and the length, asked for every name of every summary several times a second on
        // the 10x island (S12: some 4 KB of garbage each time); the answers are kept, a bounded number of them.
        var key = (text, maxLength);
        if (DisplayTexts.TryGetValue(key, out var known)) return known;
        var shown = DisplayTextOf(text, maxLength);
        if (DisplayTexts.Count >= MaxKeptDisplayTexts) DisplayTexts.Clear();
        DisplayTexts[key] = shown;
        return shown;
    }

    private const int MaxKeptDisplayTexts = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, int MaxLength), string> DisplayTexts = new();

    private static string DisplayTextOf(string text, int maxLength)
    {
        var codes = Trim(Collapse(Clean(CodePoints(text))));
        if (codes.Length > maxLength) codes = codes[..maxLength];
        codes = Trim(Collapse(Clean(codes)));
        return codes.Length == 0 ? "Refused." : Text(codes);
    }

    public static string Text(IEnumerable<int> codes)
    {
        var output = new StringBuilder();
        foreach (var code in codes) output.Append(char.ConvertFromUtf32(code));
        return output.ToString();
    }

    private static int[] Collapse(int[] codes)
    {
        var result = new List<int>(codes.Length);
        foreach (var code in codes)
            if (code != ' ' || result.Count == 0 || result[^1] != ' ') result.Add(code);
        return result.ToArray();
    }

    private static int[] Trim(int[] codes)
    {
        int start = 0, end = codes.Length;
        while (start < end && codes[start] <= 0x20) start++;
        while (end > start && codes[end - 1] <= 0x20) end--;
        return codes[start..end];
    }
}
