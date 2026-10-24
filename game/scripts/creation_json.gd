extends RefCounted
## Strict JSON and Garage Game canonical JSON v1 for the GDScript kernel.
##
## Every fingerprint and content hash is the SHA-256 of canonical JSON, and C#, Python and
## GDScript must produce the same bytes. Godot's own JSON parser reads roughly one decimal in
## five one unit in the last place off, and JSON.stringify leaves control characters unescaped,
## so the kernel parses and writes JSON here instead. The rules are written out in
## tools/kernel/canonical_json.py; game/tests/fixtures/kernel/ is the shared golden fixture.
##
## Numbers are exact: a decimal is read as the nearest double (ties to even) and a double is
## written with the shortest digits that read back to it (Python's repr). Short decimals take a
## fast path proved exact by single IEEE operations; the rest use arbitrary-precision integers.
## U+0000 cannot live in a Godot String, so it is refused rather than altered.

const VERSION := 1
const SAFE_INTEGER := 9007199254740992.0
const MAX_DEPTH := 64
const MAX_NUMBER_DIGITS := 800
const _LIMB := 4294967296
const _LIMB_MASK := 4294967295
const _POW5 := [1, 5, 25, 125, 625, 3125, 15625, 78125, 390625, 1953125, 9765625, 48828125, 244140625]
static var _pow10: Array = []


## Parse strict JSON: no duplicate keys, no NaN or Infinity, no number beyond a double, no
## unpaired surrogate, no U+0000 and no trailing data. Every number becomes a float, as with
## Godot's parser. Returns {ok: true, value} or {ok: false, error}.
static func parse(text: String) -> Dictionary:
	var cursor := _Cursor.new(text)
	var value: Variant = _read_value(cursor, 0)
	if cursor.error.is_empty():
		cursor.skip_space()
		if cursor.at < cursor.size:
			cursor.fail("unexpected data after the JSON value")
	if not cursor.error.is_empty():
		return {"ok": false, "error": cursor.error}
	return {"ok": true, "value": value}


## The canonical text of a JSON-compatible value, or "" when the value cannot be written
## (non-finite number, non-string key, unpaired surrogate, unsupported type, nesting over 64).
static func canonical(value: Variant) -> String:
	var parts := PackedStringArray()
	if not _emit(value, parts, 0):
		return ""
	return "".join(parts)


static func sha256_hex(value: Variant) -> String:
	var text := canonical(value)
	return "" if text.is_empty() else text.sha256_text()


static func canonical_number(value: float) -> String:
	if is_nan(value) or is_inf(value):
		return ""
	if value == floor(value) and absf(value) <= SAFE_INTEGER:
		return str(int(value))
	var shortest := _shortest_digits(absf(value))
	var digits: String = shortest[0]
	var point: int = shortest[1]
	var text := ""
	if point <= -4 or point > 16:
		var exponent := point - 1
		text = digits.substr(0, 1) + ("." + digits.substr(1) if digits.length() > 1 else "") \
			+ ("e+" if exponent >= 0 else "e-") + str(absi(exponent)).pad_zeros(2)
	elif point <= 0:
		text = "0." + "0".repeat(-point) + digits
	elif point < digits.length():
		text = digits.substr(0, point) + "." + digits.substr(point)
	else:
		text = digits + "0".repeat(point - digits.length()) + ".0"
	return ("-" + text) if value < 0.0 else text


static func quote(text: String) -> String:
	var parts := PackedStringArray()
	return "".join(parts) if _quote_into(text, parts) else ""


static func _emit(value: Variant, parts: PackedStringArray, depth: int) -> bool:
	if depth >= MAX_DEPTH and typeof(value) in [TYPE_ARRAY, TYPE_DICTIONARY]:
		return false
	match typeof(value):
		TYPE_NIL:
			parts.append("null")
		TYPE_BOOL:
			parts.append("true" if value else "false")
		TYPE_INT, TYPE_FLOAT:
			var number := canonical_number(float(value))
			if number.is_empty():
				return false
			parts.append(number)
		TYPE_STRING:
			return _quote_into(value, parts)
		TYPE_ARRAY:
			parts.append("[")
			var first := true
			for item in value:
				if not first:
					parts.append(",")
				first = false
				if not _emit(item, parts, depth + 1):
					return false
			parts.append("]")
		TYPE_DICTIONARY:
			var keys: Array = value.keys()
			for key in keys:
				if typeof(key) != TYPE_STRING:
					return false
			# Godot orders Strings by code point, which is the canonical order.
			keys.sort()
			parts.append("{")
			var first := true
			for key in keys:
				if not first:
					parts.append(",")
				first = false
				if not _quote_into(key, parts):
					return false
				parts.append(":")
				if not _emit(value[key], parts, depth + 1):
					return false
			parts.append("}")
		_:
			return false
	return true


static func _quote_into(text: String, parts: PackedStringArray) -> bool:
	parts.append("\"")
	var start := 0
	for index in range(text.length()):
		var code := text.unicode_at(index)
		if code >= 0xD800 and code <= 0xDFFF:
			return false
		if code >= 0x20 and code != 0x22 and code != 0x5C:
			continue
		if code == 0:
			return false
		parts.append(text.substr(start, index - start))
		start = index + 1
		match code:
			0x22: parts.append("\\\"")
			0x5C: parts.append("\\\\")
			0x08: parts.append("\\b")
			0x09: parts.append("\\t")
			0x0A: parts.append("\\n")
			0x0C: parts.append("\\f")
			0x0D: parts.append("\\r")
			_: parts.append("\\u%04x" % code)
	parts.append(text.substr(start))
	parts.append("\"")
	return true


## Shortest digits and decimal point position (value = 0.DIGITS x 10^point) of a positive double.
static func _shortest_digits(value: float) -> Array:
	var fast := _fast_shortest(value)
	return fast if not fast.is_empty() else _dragon4(value)


static func _fast_shortest(value: float) -> Array:
	# Godot's num_scientific is shortest for almost every double but not all. Its candidate is
	# accepted only when single IEEE operations prove it (Clinger's fast path: at most 15 digits
	# and a power of ten that is itself an exact double), and only after no shorter one works.
	var parsed := _split_decimal(String.num_scientific(value))
	var digits: String = parsed[0]
	var point: int = parsed[1]
	if _fast_matches(digits, point, value) != 1:
		return []
	while digits.length() > 1:
		var shorter := digits.substr(0, digits.length() - 1)
		var down := _fast_matches(shorter, point, value)
		if down == -1:
			return []
		if down == 1:
			digits = shorter
		else:
			var raised := str(shorter.to_int() + 1)
			var raised_point := point + (raised.length() - shorter.length())
			var up := _fast_matches(raised, raised_point, value)
			if up == -1:
				return []
			if up == 0:
				break
			digits = raised
			point = raised_point
		while digits.length() > 1 and digits.ends_with("0"):
			digits = digits.substr(0, digits.length() - 1)
	return [digits, point]


## 1 when 0.DIGITS x 10^point reads as exactly value, 0 when it reads as another double, -1 when
## the fast path cannot decide.
static func _fast_matches(digits: String, point: int, value: float) -> int:
	if digits.length() > 15:
		return -1
	var exponent := point - digits.length()
	if absi(exponent) > 22:
		return -1
	var mantissa := float(digits.to_int())
	var produced: float = mantissa * _powers()[exponent] if exponent >= 0 else mantissa / _powers()[-exponent]
	return 1 if produced == value else 0


## Splits Godot's num_scientific text into significant digits and the decimal point position.
static func _split_decimal(text: String) -> Array:
	var exponent := 0
	var marker := text.find("e")
	if marker >= 0:
		exponent = text.substr(marker + 1).to_int()
		text = text.substr(0, marker)
	var dot := text.find(".")
	var digits := text if dot < 0 else text.substr(0, dot) + text.substr(dot + 1)
	var point := text.length() if dot < 0 else dot
	var lead := 0
	while lead < digits.length() - 1 and digits.unicode_at(lead) == 0x30:
		lead += 1
	digits = digits.substr(lead)
	point -= lead
	while digits.length() > 1 and digits.ends_with("0"):
		digits = digits.substr(0, digits.length() - 1)
	return [digits, point + exponent]


## Shortest round-trip digits by exact arithmetic (Steele and White, Burger and Dybvig), with the
## termination and tie rules of David Gay's dtoa mode 0, which Python's repr uses.
static func _dragon4(value: float) -> Array:
	var bits := _bits_of(value)
	var biased := bits >> 52
	var fraction := bits & 0xFFFFFFFFFFFFF
	var mantissa := fraction if biased == 0 else fraction | (1 << 52)
	var exponent := -1074 if biased == 0 else biased - 1075
	var even := (mantissa & 1) == 0
	# At a power of two (other than the smallest exponent) the gap below is half the gap above.
	var unequal := fraction == 0 and biased > 1
	var r: Array
	var s: Array
	var high: Array
	var low: Array
	if exponent >= 0:
		var gap := _big_shl(_big(1), exponent)
		if unequal:
			r = _big_shl(_big(mantissa), exponent + 2)
			s = _big(4)
			high = _big_shl(gap, 1)
		else:
			r = _big_shl(_big(mantissa), exponent + 1)
			s = _big(2)
			high = gap
		low = gap
	else:
		if unequal:
			r = _big(mantissa * 4)
			s = _big_shl(_big(1), 2 - exponent)
			high = _big(2)
		else:
			r = _big(mantissa * 2)
			s = _big_shl(_big(1), 1 - exponent)
			high = _big(1)
		low = _big(1)
	var point := int(ceil(log(value) / log(10.0) - 1e-10))
	if point >= 0:
		s = _big_mul_pow10(s, point)
	else:
		r = _big_mul_pow10(r, -point)
		high = _big_mul_pow10(high, -point)
		low = _big_mul_pow10(low, -point)
	# Exact correction of the estimate: the upper boundary must lie below 10^point and reach 10^(point-1).
	while true:
		var reach := _big_cmp(_big_add(r, high), s)
		if reach > 0 or (reach == 0 and even):
			s = _big_mul_small(s, 10)
			point += 1
			continue
		var scaled := _big_cmp(_big_mul_small(_big_add(r, high), 10), s)
		if scaled < 0 or (scaled == 0 and not even):
			r = _big_mul_small(r, 10)
			high = _big_mul_small(high, 10)
			low = _big_mul_small(low, 10)
			point -= 1
			continue
		break
	var digits := ""
	while true:
		r = _big_mul_small(r, 10)
		high = _big_mul_small(high, 10)
		low = _big_mul_small(low, 10)
		var digit := 0
		while _big_cmp(r, s) >= 0:
			r = _big_sub(r, s)
			digit += 1
		var to_low := _big_cmp(r, low)
		var to_high := _big_cmp(_big_add(r, high), s)
		if to_high == 0 and even:
			if digit == 9:
				return _round_nines_up(digits + "9", point)
			if to_low > 0:
				digit += 1
			return [digits + str(digit), point]
		if to_low < 0 or (to_low == 0 and even):
			if not r.is_empty() and to_high > 0:
				var doubled := _big_cmp(_big_mul_small(r, 2), s)
				if doubled > 0 or (doubled == 0 and digit % 2 == 1):
					if digit == 9:
						return _round_nines_up(digits + "9", point)
					digit += 1
			return [digits + str(digit), point]
		if to_high > 0:
			if digit == 9:
				return _round_nines_up(digits + "9", point)
			return [digits + str(digit + 1), point]
		digits += str(digit)
	return []


static func _round_nines_up(digits: String, point: int) -> Array:
	while digits.ends_with("9"):
		digits = digits.substr(0, digits.length() - 1)
	if digits.is_empty():
		return ["1", point + 1]
	return [digits.substr(0, digits.length() - 1) + str(digits.right(1).to_int() + 1), point]


## The double nearest to DIGITS x 10^exponent, ties to even, or null when it overflows.
static func decimal_to_double(digits: String, exponent: int, negative: bool) -> Variant:
	var lead := 0
	while lead < digits.length() and digits.unicode_at(lead) == 0x30:
		lead += 1
	digits = digits.substr(lead)
	while digits.length() > 0 and digits.ends_with("0"):
		digits = digits.substr(0, digits.length() - 1)
		exponent += 1
	if digits.is_empty():
		return -0.0 if negative else 0.0
	var magnitude := digits.length() + exponent - 1
	var result := 0.0
	if magnitude > 309:
		return null
	if magnitude < -325:
		result = 0.0
	elif digits.length() <= 15 and absi(exponent) <= 22:
		# Clinger's fast path: both operands are exact doubles, so one IEEE operation rounds once.
		var mantissa := float(digits.to_int())
		result = mantissa * _powers()[exponent] if exponent >= 0 else mantissa / _powers()[-exponent]
	else:
		var decimal := _big_from_digits(digits)
		var bits: Variant
		if exponent >= 0:
			bits = _ratio_bits(_big_mul_pow5(decimal, exponent), _big(1), exponent)
		else:
			bits = _ratio_bits(decimal, _big_mul_pow5(_big(1), -exponent), exponent)
		if bits == null:
			return null
		result = _double_of(bits)
	return -result if negative else result


## Bits of the double nearest to NUMERATOR / DENOMINATOR x 2^power (ties to even), or null on
## overflow. Exact long division to 55 or 56 quotient bits plus a sticky remainder.
static func _ratio_bits(numerator: Array, denominator: Array, power: int) -> Variant:
	var shift := 55 - (_big_bit_length(numerator) - _big_bit_length(denominator))
	if shift >= 0:
		numerator = _big_shl(numerator, shift)
	else:
		denominator = _big_shl(denominator, -shift)
	power -= shift
	var quotient := 0
	for bit in range(56, -1, -1):
		var step := _big_shl(denominator, bit)
		if _big_cmp(numerator, step) >= 0:
			numerator = _big_sub(numerator, step)
			quotient |= 1 << bit
	var sticky := not numerator.is_empty()
	var length := _bit_length(quotient)
	var drop := length - 53
	var exponent := power + drop
	if exponent < -1074:
		drop = -1074 - power
		exponent = -1074
	if drop >= 63:
		return 0
	var mantissa := quotient >> drop
	if drop > 0:
		var rest := quotient - (mantissa << drop)
		var half := 1 << (drop - 1)
		if rest > half or (rest == half and (sticky or (mantissa & 1) == 1)):
			mantissa += 1
	if mantissa == (1 << 53):
		mantissa = 1 << 52
		exponent += 1
	if mantissa < (1 << 52):
		return mantissa  # subnormal (exponent is -1074); 2^52 itself is the smallest normal
	var biased := exponent + 1075
	if biased >= 2047:
		return null
	return (biased << 52) | (mantissa - (1 << 52))


static func _bit_length(value: int) -> int:
	var length := 0
	while value > 0:
		value >>= 1
		length += 1
	return length


static func _bits_of(value: float) -> int:
	var bytes := PackedByteArray()
	bytes.resize(8)
	bytes.encode_double(0, value)
	return bytes.decode_s64(0)


static func _double_of(bits: int) -> float:
	var bytes := PackedByteArray()
	bytes.resize(8)
	bytes.encode_s64(0, bits)
	return bytes.decode_double(0)


static func _powers() -> Array:
	if _pow10.is_empty():
		var power := 1.0
		for _index in range(23):
			_pow10.append(power)
			power *= 10.0
	return _pow10


# Non-negative integers of any size: little-endian Arrays of 32-bit limbs, [] is zero.
static func _big(value: int) -> Array:
	var result: Array = []
	while value > 0:
		result.append(value & _LIMB_MASK)
		value >>= 32
	return result


static func _big_from_digits(digits: String) -> Array:
	var head := digits.length() % 9
	if head == 0:
		head = 9
	var result := _big(digits.substr(0, head).to_int())
	var index := head
	while index < digits.length():
		result = _big_add(_big_mul_small(result, 1000000000), _big(digits.substr(index, 9).to_int()))
		index += 9
	return result


static func _big_mul_small(value: Array, factor: int) -> Array:
	if factor == 0 or value.is_empty():
		return []
	var result: Array = []
	result.resize(value.size())
	var carry := 0
	for index in range(value.size()):
		var product: int = value[index] * factor + carry
		result[index] = product & _LIMB_MASK
		carry = product >> 32
	if carry > 0:
		result.append(carry)
	return result


static func _big_add(a: Array, b: Array) -> Array:
	var size := maxi(a.size(), b.size())
	var result: Array = []
	result.resize(size)
	var carry := 0
	for index in range(size):
		var total: int = (a[index] if index < a.size() else 0) + (b[index] if index < b.size() else 0) + carry
		result[index] = total & _LIMB_MASK
		carry = total >> 32
	if carry > 0:
		result.append(carry)
	return result


static func _big_sub(a: Array, b: Array) -> Array:
	var result: Array = a.duplicate()
	var borrow := 0
	for index in range(result.size()):
		var difference: int = result[index] - (b[index] if index < b.size() else 0) - borrow
		borrow = 0
		if difference < 0:
			difference += _LIMB
			borrow = 1
		result[index] = difference
	while not result.is_empty() and result.back() == 0:
		result.pop_back()
	return result


static func _big_cmp(a: Array, b: Array) -> int:
	if a.size() != b.size():
		return -1 if a.size() < b.size() else 1
	for index in range(a.size() - 1, -1, -1):
		if a[index] != b[index]:
			return -1 if a[index] < b[index] else 1
	return 0


static func _big_shl(value: Array, count: int) -> Array:
	if value.is_empty():
		return []
	var result: Array = []
	result.resize(count / 32)
	result.fill(0)
	var shift := count % 32
	var carry := 0
	for limb in value:
		var moved: int = (limb << shift) | carry
		result.append(moved & _LIMB_MASK)
		carry = moved >> 32
	if carry > 0:
		result.append(carry)
	return result


static func _big_mul_pow5(value: Array, count: int) -> Array:
	var result := value
	var remaining := count
	while remaining >= 13:
		result = _big_mul_small(result, 1220703125)
		remaining -= 13
	if remaining > 0:
		result = _big_mul_small(result, _POW5[remaining])
	return result


static func _big_mul_pow10(value: Array, count: int) -> Array:
	return _big_shl(_big_mul_pow5(value, count), count)


static func _big_bit_length(value: Array) -> int:
	if value.is_empty():
		return 0
	return (value.size() - 1) * 32 + _bit_length(value.back())


## Parser position and first error. Parsing lives in the static functions below because inner
## classes cannot call this script's static functions.
class _Cursor:
	var text: String
	var size: int
	var at := 0
	var error := ""

	func _init(source: String) -> void:
		text = source
		size = source.length()

	func fail(message: String) -> Variant:
		if error.is_empty():
			error = "%s at character %d" % [message, at]
		return null

	func skip_space() -> void:
		while at < size:
			var code := text.unicode_at(at)
			if code != 0x20 and code != 0x09 and code != 0x0A and code != 0x0D:
				return
			at += 1

	func next_is(code: int) -> bool:
		return at < size and text.unicode_at(at) == code

	func skip_digits() -> int:
		var start := at
		while at < size and text.unicode_at(at) >= 0x30 and text.unicode_at(at) <= 0x39:
			at += 1
		return at - start


static func _read_value(cursor: _Cursor, depth: int) -> Variant:
	cursor.skip_space()
	if cursor.at >= cursor.size:
		return cursor.fail("unexpected end of JSON")
	var code := cursor.text.unicode_at(cursor.at)
	# At most MAX_DEPTH nested arrays and objects, as in C# and Python: the root container is level 1.
	if (code == 0x7B or code == 0x5B) and depth >= MAX_DEPTH:
		return cursor.fail("JSON nests deeper than %d levels" % MAX_DEPTH)
	if code == 0x7B:
		return _read_object(cursor, depth)
	if code == 0x5B:
		return _read_array(cursor, depth)
	if code == 0x22:
		return _read_string(cursor)
	if code == 0x2D or (code >= 0x30 and code <= 0x39):
		return _read_number(cursor)
	for word in ["true", "false", "null"]:
		if cursor.text.substr(cursor.at, word.length()) == word:
			cursor.at += word.length()
			return true if word == "true" else (false if word == "false" else null)
	return cursor.fail("unexpected character")


static func _read_object(cursor: _Cursor, depth: int) -> Variant:
	cursor.at += 1
	var result := {}
	cursor.skip_space()
	if cursor.next_is(0x7D):
		cursor.at += 1
		return result
	while true:
		cursor.skip_space()
		if not cursor.next_is(0x22):
			return cursor.fail("expected a string key")
		var key: Variant = _read_string(cursor)
		if not cursor.error.is_empty():
			return null
		if result.has(key):
			return cursor.fail("duplicate key")
		cursor.skip_space()
		if not cursor.next_is(0x3A):
			return cursor.fail("expected ':'")
		cursor.at += 1
		var item: Variant = _read_value(cursor, depth + 1)
		if not cursor.error.is_empty():
			return null
		result[key] = item
		cursor.skip_space()
		if cursor.next_is(0x2C):
			cursor.at += 1
			continue
		if cursor.next_is(0x7D):
			cursor.at += 1
			return result
		return cursor.fail("expected ',' or '}'")
	return null


static func _read_array(cursor: _Cursor, depth: int) -> Variant:
	cursor.at += 1
	var result: Array = []
	cursor.skip_space()
	if cursor.next_is(0x5D):
		cursor.at += 1
		return result
	while true:
		var item: Variant = _read_value(cursor, depth + 1)
		if not cursor.error.is_empty():
			return null
		result.append(item)
		cursor.skip_space()
		if cursor.next_is(0x2C):
			cursor.at += 1
			continue
		if cursor.next_is(0x5D):
			cursor.at += 1
			return result
		return cursor.fail("expected ',' or ']'")
	return null


static func _read_string(cursor: _Cursor) -> Variant:
	var text := cursor.text
	cursor.at += 1
	var parts := PackedStringArray()
	var start := cursor.at
	while true:
		if cursor.at >= cursor.size:
			return cursor.fail("unterminated string")
		var code := text.unicode_at(cursor.at)
		if code == 0x22:
			parts.append(text.substr(start, cursor.at - start))
			cursor.at += 1
			return "".join(parts)
		if code < 0x20:
			return cursor.fail("control character in a string")
		if code >= 0xD800 and code <= 0xDFFF:
			return cursor.fail("unpaired surrogate in a string")
		if code != 0x5C:
			cursor.at += 1
			continue
		parts.append(text.substr(start, cursor.at - start))
		cursor.at += 1
		if cursor.at >= cursor.size:
			return cursor.fail("unterminated escape")
		var escape := text.unicode_at(cursor.at)
		cursor.at += 1
		match escape:
			0x22: parts.append("\"")
			0x5C: parts.append("\\")
			0x2F: parts.append("/")
			0x62: parts.append(char(0x08))
			0x66: parts.append(char(0x0C))
			0x6E: parts.append("\n")
			0x72: parts.append("\r")
			0x74: parts.append("\t")
			0x75:
				var unit := _read_hex4(cursor)
				if unit < 0:
					return cursor.fail("bad \\u escape")
				if unit >= 0xDC00 and unit <= 0xDFFF:
					return cursor.fail("unpaired surrogate in a string")
				if unit >= 0xD800 and unit <= 0xDBFF:
					if text.substr(cursor.at, 2) != "\\u":
						return cursor.fail("unpaired surrogate in a string")
					cursor.at += 2
					var second := _read_hex4(cursor)
					if second < 0xDC00 or second > 0xDFFF:
						return cursor.fail("unpaired surrogate in a string")
					unit = 0x10000 + ((unit - 0xD800) << 10) + (second - 0xDC00)
				if unit == 0:
					return cursor.fail("U+0000 cannot be represented")
				parts.append(char(unit))
			_:
				return cursor.fail("unknown escape")
		start = cursor.at
	return null


static func _read_hex4(cursor: _Cursor) -> int:
	var digits := cursor.text.substr(cursor.at, 4)
	if digits.length() != 4 or not digits.is_valid_hex_number(false):
		return -1
	cursor.at += 4
	return digits.hex_to_int()


static func _read_number(cursor: _Cursor) -> Variant:
	var text := cursor.text
	var negative := cursor.next_is(0x2D)
	if negative:
		cursor.at += 1
	var whole_start := cursor.at
	if cursor.next_is(0x30):
		cursor.at += 1
	elif cursor.at < cursor.size and text.unicode_at(cursor.at) >= 0x31 and text.unicode_at(cursor.at) <= 0x39:
		cursor.skip_digits()
	else:
		return cursor.fail("malformed number")
	var digits := text.substr(whole_start, cursor.at - whole_start)
	var exponent := 0
	if cursor.next_is(0x2E):
		cursor.at += 1
		var fraction_start := cursor.at
		var count := cursor.skip_digits()
		if count == 0:
			return cursor.fail("malformed number")
		digits += text.substr(fraction_start, count)
		exponent -= count
	if cursor.next_is(0x65) or cursor.next_is(0x45):
		cursor.at += 1
		var sign := 1
		if cursor.next_is(0x2B) or cursor.next_is(0x2D):
			sign = -1 if cursor.next_is(0x2D) else 1
			cursor.at += 1
		var exponent_start := cursor.at
		var count := cursor.skip_digits()
		if count == 0:
			return cursor.fail("malformed number")
		var written := text.substr(exponent_start, count).lstrip("0")
		exponent += sign * (100000 if written.length() > 6 else written.to_int())
	if digits.lstrip("0").rstrip("0").length() > MAX_NUMBER_DIGITS:
		return cursor.fail("number has more than %d significant digits" % MAX_NUMBER_DIGITS)
	var value: Variant = decimal_to_double(digits, exponent, negative)
	if value == null:
		return cursor.fail("number overflows a double")
	return value
