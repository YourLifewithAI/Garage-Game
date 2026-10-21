extends RefCounted
## The project's invisible-character rule for untrusted world text (names, labels, notes), in GDScript.
## Kernel/KernelText.cs is the same rule in C#, line for line; companion/src/garage_game_companion/
## textsafety.py is Lane A's. Requests that carry a hidden character are refused; text the host emits
## has every hidden character replaced by a space.
##
## Hidden, judged one code point at a time:
## - controls (U+0000-U+001F, U+007F-U+009F), lone surrogates, U+2028 and U+2029;
## - every Unicode format character (category Cf, the fixed Unicode 15.1/16.0 table below);
## - characters that render as nothing: U+034F, U+115F, U+1160, U+180B-U+180F, U+2028-U+202E,
##   U+2060-U+206F (U+2065 too, though unassigned), U+2800, U+3164, U+FFA0, U+FFF9-U+FFFB;
## - the variation selectors U+FE00-U+FE0F and all of plane 14 (U+E0000-U+EFFFF).
##
## Emoji markers (founder decision, 6 October 2026) are allowed only where an emoji puts them:
## - VS15 U+FE0E and VS16 U+FE0F directly after an Extended_Pictographic character or a keycap base
##   (0-9, # or *); a selector is never a base, so at most one per base;
## - ZWJ U+200D between two Extended_Pictographic characters, the first optionally followed by one
##   VS16 or one skin-tone modifier (U+1F3FB-U+1F3FF), as in an emoji ZWJ sequence;
## - the keycap U+20E3 directly after 0-9, # or *, optionally with one VS16 between.
## Runs or any other placement of these markers are hidden.

const VS15 := 0xFE0E
const VS16 := 0xFE0F
const ZWJ := 0x200D
const KEYCAP := 0x20E3
const MARKERS := [VS15, VS16, ZWJ, KEYCAP]
const KEYCAP_BASES := [0x23, 0x2A, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39]

## Unicode general category Cf (Unicode 15.1; unchanged in 16.0).
const FORMAT := [
	[0x00AD, 0x00AD], [0x0600, 0x0605], [0x061C, 0x061C], [0x06DD, 0x06DD], [0x070F, 0x070F], [0x0890, 0x0891],
	[0x08E2, 0x08E2], [0x180E, 0x180E], [0x200B, 0x200F], [0x202A, 0x202E], [0x2060, 0x2064], [0x2066, 0x206F],
	[0xFEFF, 0xFEFF], [0xFFF9, 0xFFFB], [0x110BD, 0x110BD], [0x110CD, 0x110CD], [0x13430, 0x1343F],
	[0x1BCA0, 0x1BCA3], [0x1D173, 0x1D17A], [0xE0001, 0xE0001], [0xE0020, 0xE007F],
]
## Blank and invisible characters outside Cf, the variation selectors and plane 14.
const BLANK := [
	[0x034F, 0x034F], [0x115F, 0x1160], [0x180B, 0x180F], [0x2028, 0x202E], [0x2060, 0x206F], [0x2800, 0x2800],
	[0x3164, 0x3164], [0xFE00, 0xFE0F], [0xFFA0, 0xFFA0], [0xFFF9, 0xFFFB], [0xE0000, 0xEFFFF],
]
## Extended_Pictographic (UTS #51, emoji-data.txt for Unicode 15.1), merged into ranges: the table Lane A uses.
const PICTOGRAPHIC := [
	[0x00A9, 0x00A9], [0x00AE, 0x00AE], [0x203C, 0x203C], [0x2049, 0x2049], [0x2122, 0x2122], [0x2139, 0x2139],
	[0x2194, 0x2199], [0x21A9, 0x21AA], [0x231A, 0x231B], [0x2328, 0x2328], [0x2388, 0x2388], [0x23CF, 0x23CF],
	[0x23E9, 0x23F3], [0x23F8, 0x23FA], [0x24C2, 0x24C2], [0x25AA, 0x25AB], [0x25B6, 0x25B6], [0x25C0, 0x25C0],
	[0x25FB, 0x25FE], [0x2600, 0x2605], [0x2607, 0x2612], [0x2614, 0x2685], [0x2690, 0x2705], [0x2708, 0x2712],
	[0x2714, 0x2714], [0x2716, 0x2716], [0x271D, 0x271D], [0x2721, 0x2721], [0x2728, 0x2728], [0x2733, 0x2734],
	[0x2744, 0x2744], [0x2747, 0x2747], [0x274C, 0x274C], [0x274E, 0x274E], [0x2753, 0x2755], [0x2757, 0x2757],
	[0x2763, 0x2767], [0x2795, 0x2797], [0x27A1, 0x27A1], [0x27B0, 0x27B0], [0x27BF, 0x27BF], [0x2934, 0x2935],
	[0x2B05, 0x2B07], [0x2B1B, 0x2B1C], [0x2B50, 0x2B50], [0x2B55, 0x2B55], [0x3030, 0x3030], [0x303D, 0x303D],
	[0x3297, 0x3297], [0x3299, 0x3299], [0x1F000, 0x1F0FF], [0x1F10D, 0x1F10F], [0x1F12F, 0x1F12F], [0x1F16C, 0x1F171],
	[0x1F17E, 0x1F17F], [0x1F18E, 0x1F18E], [0x1F191, 0x1F19A], [0x1F1AD, 0x1F1E5], [0x1F201, 0x1F20F], [0x1F21A, 0x1F21A],
	[0x1F22F, 0x1F22F], [0x1F232, 0x1F23A], [0x1F23C, 0x1F23F], [0x1F249, 0x1F3FA], [0x1F400, 0x1F53D], [0x1F546, 0x1F64F],
	[0x1F680, 0x1F6FF], [0x1F774, 0x1F77F], [0x1F7D5, 0x1F7FF], [0x1F80C, 0x1F80F], [0x1F848, 0x1F84F], [0x1F85A, 0x1F85F],
	[0x1F888, 0x1F88F], [0x1F8AE, 0x1F8FF], [0x1F90C, 0x1F93A], [0x1F93C, 0x1F945], [0x1F947, 0x1FAFF], [0x1FC00, 0x1FFFD],
]


static func _in(ranges: Array, code: int) -> bool:
	var low := 0
	var high := ranges.size() - 1
	while low <= high:
		var middle := (low + high) >> 1
		var item: Array = ranges[middle]
		if code < int(item[0]):
			high = middle - 1
		elif code > int(item[1]):
			low = middle + 1
		else:
			return true
	return false


static func is_pictographic(code: int) -> bool:
	return _in(PICTOGRAPHIC, code)


## True for a code point a reader cannot see, judged alone. The emoji markers count as hidden here;
## hidden_at lets them through where an emoji puts them.
static func is_hidden(code: int) -> bool:
	if code < 0x20 or (code >= 0x7F and code <= 0x9F) or (code >= 0xD800 and code <= 0xDFFF):
		return true
	if code == KEYCAP:
		return true
	return _in(FORMAT, code) or _in(BLANK, code)


static func _skin_tone(code: int) -> bool:
	return code >= 0x1F3FB and code <= 0x1F3FF


## True when the emoji marker at index is where an emoji puts it.
static func marker_in_place(text: String, index: int) -> bool:
	var code := text.unicode_at(index)
	var before := text.unicode_at(index - 1) if index > 0 else -1
	if code == VS15 or code == VS16:
		return is_pictographic(before) or before in KEYCAP_BASES
	if code == KEYCAP:
		if before in KEYCAP_BASES:
			return true
		return before == VS16 and index >= 2 and text.unicode_at(index - 2) in KEYCAP_BASES
	if code == ZWJ:
		if index + 1 >= text.length() or not is_pictographic(text.unicode_at(index + 1)):
			return false
		var at := index - 1
		if at >= 0 and (text.unicode_at(at) == VS16 or _skin_tone(text.unicode_at(at))):
			at -= 1
		return at >= 0 and is_pictographic(text.unicode_at(at))
	return false


static func hidden_at(text: String, index: int, allow_newlines := false) -> bool:
	var code := text.unicode_at(index)
	if allow_newlines and (code == 0x0A or code == 0x09):
		return false
	if code in MARKERS:
		return not marker_in_place(text, index)
	return is_hidden(code)


static func has_hidden(text: String, allow_newlines := false) -> bool:
	for index in range(text.length()):
		if hidden_at(text, index, allow_newlines):
			return true
	return false


## Every hidden character becomes a space, each judged against its original neighbours.
static func clean(text: String) -> String:
	var parts := PackedStringArray()
	for index in range(text.length()):
		parts.append(" " if hidden_at(text, index) else text.substr(index, 1))
	return "".join(parts)


## Single-line text as the kernel emits it: hidden characters become spaces, runs of spaces collapse, the
## ends lose characters up to U+0020, and it is cut to max_length code points (never inside a character),
## then cleaned again in case the cut stranded an emoji marker.
static func display_text(text: String, max_length: int) -> String:
	var result := _collapse(clean(text)).strip_edges().left(max_length)
	result = _collapse(clean(result)).strip_edges()
	return result if not result.is_empty() else "Refused."


static func _collapse(text: String) -> String:
	while text.contains("  "):
		text = text.replace("  ", " ")
	return text
