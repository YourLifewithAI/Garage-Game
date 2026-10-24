extends SceneTree
## The canonical JSON golden fixture, reproduced by GDScript. Python (tools/kernel/canonical_json.py)
## and C# (tests/native/Kernel/CanonicalJsonTest.cs) reproduce the same bytes from the same input.
const GUARD = preload("res://tests/kernel_test_guard.gd")
## Fails the suite on any script or engine error (kernel_test_guard.gd).
var guard = GUARD.new()

const JSON_KERNEL = preload("res://scripts/creation_json.gd")
const COMPILER = preload("res://scripts/creation_compiler.gd")
const FIXTURE := "res://tests/fixtures/kernel/"
## The golden digest, pinned here as in the C# and Python checks (the Garage Game fixture, since the rename's phase 4).
const GOLDEN_SHA256 := "ad3d1a8594e808790c035c49f00fc39fd49d49325d3237bb03bba6166fbccc94"

var checks := 0
var failures := 0


func _initialize() -> void:
	OS.add_logger(guard)
	var source := FileAccess.get_file_as_string(FIXTURE + "canonical_input.json")
	var expected := FileAccess.get_file_as_bytes(FIXTURE + "canonical_expected.json")
	var digest := FileAccess.get_file_as_string(FIXTURE + "canonical_expected.sha256").strip_edges()
	_check(not source.is_empty() and expected.size() > 0 and digest.length() == 64, "golden fixture files are present")
	_check(digest == GOLDEN_SHA256, "canonical_expected.sha256 is the pinned golden digest " + GOLDEN_SHA256)
	var parsed: Dictionary = JSON_KERNEL.parse(source)
	_check(parsed.ok, "strict parser reads the fixture input: " + str(parsed.get("error", "")))
	if parsed.ok:
		var produced := JSON_KERNEL.canonical(parsed.value).to_utf8_buffer()
		_check(produced == expected, "canonical bytes equal canonical_expected.json byte for byte (%s)" % _first_difference(produced, expected))
		_check(JSON_KERNEL.sha256_hex(parsed.value) == digest, "SHA-256 equals canonical_expected.sha256")
		_check(COMPILER.canonical_json(parsed.value).to_utf8_buffer() == expected, "the creation compiler hashes with the same canonical form")
		var reparsed: Dictionary = JSON_KERNEL.parse(expected.get_string_from_utf8())
		_check(reparsed.ok and JSON_KERNEL.canonical(reparsed.value).to_utf8_buffer() == expected, "canonical output is a fixed point")
	for case in [
		[1.0, "1"], [-0.0, "0"], [100.0, "100"], [0.1, "0.1"], [0.30000000000000004, "0.30000000000000004"],
		[1.0 / 3.0, "0.3333333333333333"], [1e-4, "0.0001"], [1e-5, "1e-05"], [1.5e-7, "1.5e-07"],
		[1e15 + 0.5, "1000000000000000.5"], [9007199254740994.0, "9007199254740994.0"], [1e16, "1e+16"],
		[1e21, "1e+21"], [-12.75, "-12.75"], [_double(1), "5e-324"], [_double(0x7FEFFFFFFFFFFFFF), "1.7976931348623157e+308"],
		[_double(0x0010000000000000), "2.2250738585072014e-308"],
	]:
		_check(JSON_KERNEL.canonical_number(case[0]) == case[1], "number %s is written %s (got %s)" % [str(case[0]), case[1], JSON_KERNEL.canonical_number(case[0])])
	_check(JSON_KERNEL.canonical({"b": 1, "a": [true, null, "x\u0001\"\\/"]}) == "{\"a\":[true,null,\"x\\u0001\\\"\\\\/\"],\"b\":1}", "escapes and key order")
	_check(JSON_KERNEL.canonical({1: "integer key"}).is_empty(), "non-string keys cannot be written")
	_check(JSON_KERNEL.canonical([NAN]).is_empty() and JSON_KERNEL.canonical([INF]).is_empty(), "non-finite numbers cannot be written")
	_check(JSON_KERNEL.canonical(Vector3.ONE).is_empty(), "engine types are not JSON")
	for bad in [
		"{\"a\":1,\"a\":2}", "[NaN]", "[1e400]", "[-1e400]", "\"\\ud800\"", "\"\\udc00x\"", "[1,]", "{\"a\":1,}", "[1] 2",
		"\"\\u0000\"", "\"tab\there\"", "01", "-", "1.", ".5", "+1", "1e", "[", "{\"a\"}", "tru", "\"\\x41\"", "",
		"[".repeat(70) + "]".repeat(70),
	]:
		var result: Dictionary = JSON_KERNEL.parse(bad)
		_check(not result.ok and String(result.get("error", "")).length() > 0, "strict parser refuses %s" % bad.c_escape().left(40))
	for good in [["0", 0.0], ["-0", -0.0], ["1E2", 100.0], ["2.50e1", 25.0], ["0.1", 0.1], ["123456789012345678901234567890", 1.2345678901234568e+29],
			["9007199254740993", 9007199254740992.0], ["2.2250738585072014e-308", _double(0x0010000000000000)], ["4.9406564584124654e-324", _double(1)], ["1e-400", 0.0]]:
		var result: Dictionary = JSON_KERNEL.parse(good[0])
		_check(result.ok and result.value == good[1], "strict parser reads %s as the nearest double" % good[0])
	# The same limits as C# and Python: 64 nested containers and 800 significant digits (Lane P review).
	_check(JSON_KERNEL.parse("[".repeat(64) + "]".repeat(64)).ok and JSON_KERNEL.parse("{\"a\":".repeat(64) + "1" + "}".repeat(64)).ok, "64 nested containers are read")
	for deep in ["[".repeat(65) + "]".repeat(65), "{\"a\":".repeat(65) + "1" + "}".repeat(65), "[".repeat(64) + "{}" + "]".repeat(64)]:
		_check(not JSON_KERNEL.parse(deep).ok, "65 nested containers are refused")
	var nested: Variant = []
	for _level in range(63):
		nested = [nested]
	_check(JSON_KERNEL.canonical(nested) == "[".repeat(64) + "]".repeat(64) and JSON_KERNEL.canonical([nested]).is_empty(), "64 nested containers are written, 65 are not")
	_check(JSON_KERNEL.parse("[0." + "1".repeat(800) + "]").ok and JSON_KERNEL.parse("[1" + "0".repeat(300) + "]").ok, "800 significant digits are read; trailing zeros do not count")
	_check(not JSON_KERNEL.parse("[0." + "1".repeat(801) + "]").ok and not JSON_KERNEL.parse("[" + "9".repeat(5000) + "]").ok, "801 significant digits are refused")
	print("Kernel canonical JSON: golden fixture reproduced by GDScript, sha256 %s; %d checks, %d failures" % [digest, checks, failures])
	quit(guard.exit_code(failures != 0))


## GDScript's own number literals flush values near the bottom of the double range to zero.
func _double(bits: int) -> float:
	var bytes := PackedByteArray()
	bytes.resize(8)
	bytes.encode_s64(0, bits)
	return bytes.decode_double(0)


func _first_difference(produced: PackedByteArray, expected: PackedByteArray) -> String:
	for index in range(mini(produced.size(), expected.size())):
		if produced[index] != expected[index]:
			return "first difference at byte %d" % index
	return "lengths %d and %d" % [produced.size(), expected.size()]


func _check(condition: bool, label: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		push_error("Kernel canonical JSON: " + label)
