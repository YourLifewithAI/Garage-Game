"""Tests for the canonical JSON reference. Run: python -m unittest discover -s tools/kernel"""
from __future__ import annotations

import hashlib
import json
import math
import struct
import unittest
from pathlib import Path

import canonical_json as cj

FIXTURE = Path(__file__).resolve().parents[2] / "game" / "tests" / "fixtures" / "kernel"
# The golden digest, pinned here as in the C# and GDScript checks (the Garage Game fixture, since the rename's phase 4).
GOLDEN_SHA256 = "ad3d1a8594e808790c035c49f00fc39fd49d49325d3237bb03bba6166fbccc94"


class GoldenFixture(unittest.TestCase):
    def test_python_reproduces_the_golden_bytes(self):
        self.assertEqual(cj.check_fixture(FIXTURE), [])

    def test_expected_bytes_have_no_line_endings_to_convert(self):
        # Canonical JSON has no whitespace, so a CRLF checkout cannot alter the expected bytes.
        expected = (FIXTURE / cj.EXPECTED_NAME).read_bytes()
        self.assertNotIn(b"\n", expected)
        self.assertNotIn(b"\r", expected)
        self.assertEqual(hashlib.sha256(expected).hexdigest(), (FIXTURE / cj.DIGEST_NAME).read_bytes().decode("ascii"))
        self.assertEqual(hashlib.sha256(expected).hexdigest(), GOLDEN_SHA256)

    def test_the_fixture_names_the_garage_game(self):
        text = (FIXTURE / cj.INPUT_NAME).read_text(encoding="utf-8")
        self.assertIn('"about": "Garage Game canonical JSON v1 golden fixture.', text)
        self.assertNotIn("enfractal", text.lower())

    def test_input_whitespace_does_not_matter(self):
        text = (FIXTURE / cj.INPUT_NAME).read_bytes().decode("utf-8")
        crlf = text.replace("\n", "\r\n")
        self.assertEqual(cj.canonical_bytes(cj.loads_strict(crlf)), (FIXTURE / cj.EXPECTED_NAME).read_bytes())


class Numbers(unittest.TestCase):
    def test_integral_values_are_integers(self):
        self.assertEqual(cj.canonical_text([1.0, -0.0, 1e2, 2 ** 53, 9007199254740993]), "[1,0,100,9007199254740992,9007199254740992]")

    def test_other_values_use_python_repr(self):
        cases = {0.1: "0.1", 1e-4: "0.0001", 1e-5: "1e-05", 1e15 + 0.5: "1000000000000000.5",
                 2.0 ** 53 + 2: "9007199254740994.0", 1e16: "1e+16", 5e-324: "5e-324"}
        for value, text in cases.items():
            self.assertEqual(cj.canonical_text(value), text)

    def test_seeded_numbers_are_normal_doubles(self):
        numbers = cj.random_numbers()
        self.assertEqual(len(numbers), cj.RANDOM_COUNT)
        self.assertTrue(all(math.isfinite(n) and n != 0 and 1e-300 < abs(n) < 1e300 for n in numbers))
        self.assertEqual(numbers, cj.random_numbers(), "the generator is deterministic")

    def test_round_trip_of_random_bit_patterns(self):
        state = 0x9E3779B97F4A7C15
        for _ in range(2000):
            state = (state * 6364136223846793005 + 1442695040888963407) % 2 ** 64
            value = struct.unpack("<d", struct.pack("<Q", state))[0]
            if not math.isfinite(value):
                continue
            self.assertEqual(float(cj.canonical_text(value)), value)


class Strictness(unittest.TestCase):
    def test_refuses_what_the_kernel_refuses(self):
        for bad in ['{"a":1,"a":2}', "[NaN]", "[1e400]", "[Infinity]", "[1,]"]:
            with self.assertRaises(cj.CanonicalJsonError):
                cj.loads_strict(bad)
        with self.assertRaises(cj.CanonicalJsonError):
            cj.canonical_bytes("\ud800")
        with self.assertRaises(cj.CanonicalJsonError):
            cj.canonical_bytes({1: "integer key"})

    def test_absurd_inputs_are_refused_never_raised(self):
        # Review: a 5,000-digit integer leaked ValueError and deep nesting leaked RecursionError.
        for bad in ["[" + "9" * 5000 + "]", "[" * 100000 + "]" * 100000, "[1" + "0" * 400 + "]", "[-1" + "0" * 400 + "]"]:
            with self.assertRaises(cj.CanonicalJsonError):
                cj.loads_strict(bad)

    def test_nesting_limit_is_64_as_in_gdscript_and_csharp(self):
        self.assertEqual(cj.canonical_text(cj.loads_strict("[" * 64 + "]" * 64)), "[" * 64 + "]" * 64)
        self.assertEqual(cj.loads_strict('{"a":' * 64 + "1" + "}" * 64)["a"]["a"]["a"] is not None, True)
        for bad in ["[" * 65 + "]" * 65, '{"a":' * 65 + "1" + "}" * 65, "[" * 64 + "{}" + "]" * 64]:
            with self.assertRaises(cj.CanonicalJsonError):
                cj.loads_strict(bad)
        deep: list = []
        for _ in range(64):
            deep = [deep]
        with self.assertRaises(cj.CanonicalJsonError):
            cj.canonical_bytes(deep)
        self.assertTrue(cj.canonical_bytes(deep[0]).startswith(b"[" * 64))
        # Brackets inside strings do not count.
        self.assertEqual(cj.loads_strict('["' + "[" * 100 + '"]'), ["[" * 100])

    def test_number_digit_limit_is_800_significant_digits_as_in_gdscript_and_csharp(self):
        self.assertEqual(cj.loads_strict("[0." + "1" * 800 + "]"), [float("0." + "1" * 800)])
        self.assertEqual(cj.loads_strict("[1" + "0" * 300 + "]"), [10 ** 300])
        self.assertEqual(cj.loads_strict("[0.000" + "1" * 800 + "000e5]"), [float("0.000" + "1" * 800 + "e5")])
        for bad in ["[0." + "1" * 801 + "]", "[1" + "1" * 800 + "]", "[-1." + "2" * 800 + "e-5]"]:
            with self.assertRaises(cj.CanonicalJsonError):
                cj.loads_strict(bad)
        self.assertEqual(cj.significant_digits("-000123.4500e+7"), 5)

    def test_escapes_and_key_order(self):
        value = {"b": 1, "a": ["x\u0001\"\\/", "\u007f "], "": 0, "\U0001F600": 0}
        self.assertEqual(cj.canonical_bytes(value).decode("utf-8"),
                         '{"a":["x\\u0001\\"\\\\/","\u007f "],"b":1,"":0,"\U0001F600":0}')

    def test_matches_contract_validator_on_integer_documents(self):
        document = json.loads((FIXTURE / cj.INPUT_NAME).read_text(encoding="utf-8"))["command"]
        plain = json.dumps(document, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        self.assertEqual(cj.canonical_bytes(document), plain)


if __name__ == "__main__":
    unittest.main()
