"""Untrusted world text: what may reach a reader, applied before text leaves the host.

Names, categories, labels and sign text come from captures and players, so they may try to hide
things. The contract forbids control, line-separator, zero-width and bidirectional-override
characters in single-line `display_text`, and allows only newline and tab in multi-line
`long_text`. That list misses other characters that render as nothing: every Unicode format
character (category Cf, which includes the Arabic letter mark U+061C, the soft hyphen U+00AD and
the interlinear annotation marks U+FFF9-U+FFFB), the Hangul fillers (U+3164 and its kin), the
variation selectors, and the whole Supplementary Special-purpose Plane U+E0000-U+EFFFF: the TAG
block U+E0000-U+E007F that can spell a hidden message, including its unassigned code points
U+E0000 and U+E0002-U+E001F, and the variation selector supplement. No visible text lives in that
plane. The host replaces all of these with a space and truncates, so an emitted string can never
fake a new line, carry invisible text or reorder what a reader sees, whichever view of the result
(escaped text or structured JSON) a client hands its model. Requests that carry them are refused
(contract.value_problems).

**Emoji markers (founder decision, 6 October 2026).** Four invisible marks are part of ordinary
emoji, so they are allowed, but only where an emoji puts them, never alone and never in runs:

- VS15 U+FE0E and VS16 U+FE0F (text and emoji presentation) directly after a base that can take
  emoji presentation: an Extended_Pictographic character (UTS #51) or a keycap base 0-9, # or *.
  At most one per base, because a selector is never a base.
- ZWJ U+200D between two emoji, as in an emoji ZWJ sequence: the character before it is
  Extended_Pictographic, optionally followed by one VS16 or one skin-tone modifier, and the character
  after it is Extended_Pictographic. (This is the grapheme-cluster rule GB11 of UAX #29, so an
  allowed joiner never stands between two separate user-perceived characters.)
- The keycap combiner U+20E3 directly after 0-9, # or *, optionally with one VS16 between them.

Skin-tone modifiers U+1F3FB-U+1F3FF are visible and need no rule. Everything else stays hidden:
the other selectors U+FE00-U+FE0D, plane 14 (so tag-sequence flags such as England's are not
supported; they lose their tags and show as a black flag), every other format character, and any
marker out of place. That defeats "variation-selector smuggling", which hides bytes in runs of
selectors after one emoji: a run leaves at most its first selector. What remains is at most one
optional marker per visible emoji, about one bit each, and never a run.

Code points are written as numbers on purpose: the source file stays plain ASCII and no invisible
character can hide in it.
"""
from __future__ import annotations

import bisect
import re
import unicodedata

# Letters and symbols outside the format category that still render as nothing: Hangul fillers
# (U+115F, U+1160, U+3164, U+FFA0), the braille blank (U+2800) and the combining grapheme joiner (U+034F).
_BLANK_CODE_POINTS = frozenset({0x115F, 0x1160, 0x3164, 0xFFA0, 0x2800, 0x034F})
# Variation selectors (used to smuggle bytes after an emoji) and the Mongolian free variation
# selectors, plus the whole Supplementary Special-purpose Plane: the TAG block (assigned or not)
# and the variation selector supplement live there, and nothing in it is visible text. VS15 and
# VS16 sit in the first range; only the emoji rule below lets them through, and only in place.
_INVISIBLE_RANGES = ((0xFE00, 0xFE0F), (0x180B, 0x180F), (0xE0000, 0xEFFFF))
# Controls, format characters, surrogates, line and paragraph separators.
_HIDDEN_CATEGORIES = frozenset({"Cc", "Cf", "Cs", "Zl", "Zp"})
_NEWLINE_AND_TAB = frozenset({10, 9})

VS15, VS16, ZWJ, KEYCAP = 0xFE0E, 0xFE0F, 0x200D, 0x20E3
EMOJI_MARKERS = frozenset({VS15, VS16, ZWJ, KEYCAP})
KEYCAP_BASES = frozenset(map(ord, "0123456789#*"))
SKIN_TONES = range(0x1F3FB, 0x1F400)

# Extended_Pictographic, Unicode 15.1 (emoji-data.txt), merged into ranges. The property reserves
# whole blocks for future emoji, so new emoji are covered without a table update.
EXTENDED_PICTOGRAPHIC = (
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
)
_PICTOGRAPHIC_STARTS = tuple(low for low, _high in EXTENDED_PICTOGRAPHIC)


def is_pictographic(code: int) -> bool:
    """Extended_Pictographic (UTS #51): the characters emoji are made of."""
    index = bisect.bisect_right(_PICTOGRAPHIC_STARTS, code) - 1
    return index >= 0 and code <= EXTENDED_PICTOGRAPHIC[index][1]


def is_hidden(ch: str) -> bool:
    """True for a character a reader cannot see that could still change meaning or layout, judged
    alone. The four emoji markers count as hidden here; `TextRules` lets them through in place."""
    code = ord(ch)
    if code in EMOJI_MARKERS:
        return True
    if code in _BLANK_CODE_POINTS or any(low <= code <= high for low, high in _INVISIBLE_RANGES):
        return True
    return unicodedata.category(ch) in _HIDDEN_CATEGORIES


class TextRules:
    """The untrusted-text rule with a set of emoji markers allowed in place (all four by default).

    The mock host emits text with the rules its loaded contract can carry (contract.Contracts
    probes the contract's text patterns): a contract whose patterns still refuse the joiner gets
    rules without it, so emitted text never fails the contract it is checked against.
    """

    def __init__(self, markers=EMOJI_MARKERS):
        self.markers = frozenset(markers) & EMOJI_MARKERS

    def marker_in_place(self, text: str, i: int) -> bool:
        """True when the emoji marker at text[i] is one this rule allows, where an emoji puts it."""
        code = ord(text[i])
        if code not in self.markers:
            return False
        before = ord(text[i - 1]) if i > 0 else -1
        if code in (VS15, VS16):
            return is_pictographic(before) or before in KEYCAP_BASES
        if code == KEYCAP:
            if before in KEYCAP_BASES:
                return True
            return before == VS16 and VS16 in self.markers and i >= 2 and ord(text[i - 2]) in KEYCAP_BASES
        # ZWJ: an emoji (with at most one VS16 or skin tone) before it and an emoji right after it.
        if i + 1 >= len(text) or not is_pictographic(ord(text[i + 1])):
            return False
        j = i - 1
        if j >= 0 and ((ord(text[j]) == VS16 and VS16 in self.markers) or ord(text[j]) in SKIN_TONES):
            j -= 1
        return j >= 0 and is_pictographic(ord(text[j]))

    def hidden_at(self, text: str, i: int, allow_newlines: bool = False) -> bool:
        code = ord(text[i])
        if allow_newlines and code in _NEWLINE_AND_TAB:
            return False
        if code in EMOJI_MARKERS:
            return not self.marker_in_place(text, i)
        return is_hidden(text[i])

    def hidden_characters(self, text: str, allow_newlines: bool = False) -> list[str]:
        """The hidden characters in `text`, as U+XXXX labels. Newline and tab are allowed in long text."""
        return [f"U+{ord(ch):04X}" for i, ch in enumerate(text) if self.hidden_at(text, i, allow_newlines)]

    def clean(self, text: str, keep_newlines: bool) -> str:
        """Every hidden character becomes a space, each judged against the original neighbours."""
        return "".join(" " if self.hidden_at(text, i, keep_newlines) else ch for i, ch in enumerate(text))

    def display_text(self, value: object, max_length: int) -> str:
        """Single-line text: every hidden character becomes a space, runs of spaces collapse."""
        text = value if isinstance(value, str) else ""
        text = re.sub(" {2,}", " ", self.clean(text, keep_newlines=False)).strip()[:max_length]
        # Truncation can cut an emoji sequence and strand its joiner: clean once more.
        text = re.sub(" {2,}", " ", self.clean(text, keep_newlines=False)).strip()
        return text or "unnamed"[:max_length]

    def long_text(self, value: object, max_length: int) -> str:
        """Multi-line text: newline and tab survive, every other hidden character becomes a space."""
        text = value if isinstance(value, str) else ""
        return self.clean(self.clean(text, keep_newlines=True)[:max_length], keep_newlines=True)


RULES = TextRules()


def hidden_characters(text: str, allow_newlines: bool = False) -> list[str]:
    """The hidden characters in `text` under the full rule (all four emoji markers allowed in place)."""
    return RULES.hidden_characters(text, allow_newlines)


def display_text(value: object, max_length: int) -> str:
    return RULES.display_text(value, max_length)


def long_text(value: object, max_length: int) -> str:
    return RULES.long_text(value, max_length)


_SAFE_PATH_PART = re.compile(r"[A-Za-z0-9_.:-]{1,64}")


def field_path(parts) -> str:
    """A JSON path for error reports. A segment that is not a short plain token is shown as '?', so a
    field path never carries arbitrary text the requester chose (plain tokens such as 'principal' do echo)."""
    path = "$"
    for part in parts:
        if isinstance(part, int):
            path += f"[{part}]"
        elif isinstance(part, str) and _SAFE_PATH_PART.fullmatch(part):
            path += f".{part}"
        else:
            path += ".?"
    return path[:200]
