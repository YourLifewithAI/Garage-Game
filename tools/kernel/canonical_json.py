#!/usr/bin/env python3
"""Garage Game canonical JSON, version 1: the reference implementation.

Every fingerprint and content hash in the kernel is the SHA-256 of these bytes. GDScript
(game/scripts/creation_json.gd), C# (game/scripts/native/Kernel/CanonicalJson.cs) and this file
must agree byte for byte; the golden fixture in game/tests/fixtures/kernel/ proves it.

The rules:
  * The input is strict JSON: no duplicate keys, no NaN or Infinity, no number that overflows
    a double, no number with more than 800 significant digits, no more than 64 nested arrays and
    objects, no unpaired surrogate. Anything else is refused, never repaired. GDScript and C# refuse
    exactly the same inputs.
  * Every number is read as the nearest IEEE-754 double. A double that is a whole number with
    magnitude at most 2**53 is written as an integer (so 1, 1.0 and 1e0 are all written 1, and
    -0 is written 0). Any other double is written as Python's repr(float): the shortest digits
    that read back to the same double, positional from 1e-4 up to 1e16, otherwise exponent
    form such as 1e+16 or 1.5e-07.
  * Strings escape only the quote, the backslash and U+0000 to U+001F (\\b \\t \\n \\f \\r, else
    \\u00xx in lower case). Everything else, including '/', U+007F and U+2028, is literal UTF-8.
  * Object members are sorted by key in Unicode code point order (the same as UTF-8 byte
    order). There is no whitespace anywhere and no trailing newline.

Equivalently: json.dumps(normalized, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
encoded as UTF-8, where normalized has each number replaced as described above.

Usage:
  python tools/kernel/canonical_json.py FILE.json          print the canonical form and its SHA-256
  python tools/kernel/canonical_json.py --check DIR        verify the golden fixture in DIR
  python tools/kernel/canonical_json.py --write DIR        regenerate DIR's expected files from its input
  python tools/kernel/canonical_json.py --random           print the seeded numbers the input must contain
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import sys
from pathlib import Path

VERSION = 1
SAFE_INTEGER = 2 ** 53
MAX_DEPTH = 64
MAX_NUMBER_DIGITS = 800
INPUT_NAME = "canonical_input.json"
EXPECTED_NAME = "canonical_expected.json"
DIGEST_NAME = "canonical_expected.sha256"
RANDOM_SEED = 20261006
RANDOM_COUNT = 400


class CanonicalJsonError(ValueError):
    """The value cannot be written as canonical JSON."""


def significant_digits(literal: str) -> int:
    """Digits of the mantissa (integer and fraction parts) without leading and trailing zeros, as GDScript counts them."""
    mantissa = literal.lstrip("-").split("e")[0].split("E")[0].replace(".", "")
    return len(mantissa.strip("0"))


def _nesting_problem(text: str) -> str | None:
    """Refuse more than MAX_DEPTH nested containers before the recursive parser can overflow the stack."""
    depth = 0
    in_string = False
    escaped = False
    for character in text:
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
        elif character == '"':
            in_string = True
        elif character in "[{":
            depth += 1
            if depth > MAX_DEPTH:
                return f"JSON nests deeper than {MAX_DEPTH} levels"
        elif character in "]}":
            depth -= 1
    return None


def loads_strict(text: str):
    """Parse JSON the way the kernel does: duplicate keys, NaN, overflowing or overlong numbers and nesting
    deeper than 64 are refused with CanonicalJsonError, never another exception."""

    def unique_pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise CanonicalJsonError(f"duplicate key {key!r}")
            result[key] = value
        return result

    def reject_constant(name):
        raise CanonicalJsonError(f"non-finite number {name} is not JSON")

    def finite_float(literal):
        if significant_digits(literal) > MAX_NUMBER_DIGITS:
            raise CanonicalJsonError(f"a number has more than {MAX_NUMBER_DIGITS} significant digits")
        number = float(literal)
        if not math.isfinite(number):
            raise CanonicalJsonError(f"number {literal[:40]} overflows a double")
        return number

    def finite_int(literal):
        # float() of the text has no digit limit, so an integer beyond a double is refused before int() could fail.
        finite_float(literal)
        return int(literal)

    problem = _nesting_problem(text)
    if problem:
        raise CanonicalJsonError(problem)
    try:
        return json.loads(text, object_pairs_hook=unique_pairs, parse_constant=reject_constant, parse_float=finite_float,
                          parse_int=finite_int)
    except json.JSONDecodeError as error:
        raise CanonicalJsonError(f"invalid JSON: {error}") from None
    except (ValueError, RecursionError) as error:
        if isinstance(error, CanonicalJsonError):
            raise
        raise CanonicalJsonError(f"invalid JSON: {type(error).__name__}") from None


def normalize(value, depth: int = 0):
    """Return a copy in which every number is the int or float the canonical form writes."""
    if depth >= MAX_DEPTH and isinstance(value, (list, tuple, dict)):
        raise CanonicalJsonError(f"value nests deeper than {MAX_DEPTH} levels")
    if value is None or isinstance(value, (bool, str)):
        return value
    if isinstance(value, (int, float)):
        try:
            number = float(value)
        except OverflowError:
            raise CanonicalJsonError(f"number {value} overflows a double") from None
        if not math.isfinite(number):
            raise CanonicalJsonError("numbers must be finite")
        if number == math.floor(number) and abs(number) <= SAFE_INTEGER:
            return int(number)
        return number
    if isinstance(value, (list, tuple)):
        return [normalize(item, depth + 1) for item in value]
    if isinstance(value, dict):
        result = {}
        for key, item in value.items():
            if not isinstance(key, str):
                raise CanonicalJsonError("object keys must be strings")
            result[key] = normalize(item, depth + 1)
        return result
    raise CanonicalJsonError(f"{type(value).__name__} is not a JSON value")


def canonical_text(value) -> str:
    return json.dumps(normalize(value), sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def canonical_bytes(value) -> bytes:
    try:
        return canonical_text(value).encode("utf-8")
    except UnicodeEncodeError:
        raise CanonicalJsonError("strings must not contain unpaired surrogates") from None


def sha256_hex(value) -> str:
    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def random_numbers(count: int = RANDOM_COUNT, seed: int = RANDOM_SEED) -> list[float]:
    """Deterministic doubles of every shape the kernel meets, kept to normal magnitudes 1e-300..1e300."""
    rng = random.Random(seed)
    values: list[float] = []
    while len(values) < count:
        shape = len(values) % 4
        if shape == 0:  # any normal double
            value = rng.uniform(1.0, 10.0) * 10.0 ** rng.randint(-299, 299) * rng.choice((1, -1))
        elif shape == 1:  # coordinates and parameters with a few decimals
            value = round(rng.uniform(-1000.0, 1000.0), rng.randint(1, 9))
        elif shape == 2:  # arithmetic results that need 16 or 17 digits
            value = rng.uniform(-50.0, 50.0) * rng.choice((0.1, 0.05, 1 / 3, 0.2, 0.07))
        else:  # values far outside the coordinate range, both tiny and huge
            value = rng.uniform(-1.0, 1.0) * 10.0 ** rng.randint(-30, 30)
        if math.isfinite(value) and value != 0.0:
            values.append(value)
    return values


def check_fixture(directory: Path) -> list[str]:
    raw = (directory / INPUT_NAME).read_bytes()
    document = loads_strict(raw.decode("utf-8"))
    produced = canonical_bytes(document)
    expected = (directory / EXPECTED_NAME).read_bytes()
    digest = (directory / DIGEST_NAME).read_bytes().decode("ascii")
    problems = []
    if produced != expected:
        offset = next((i for i, (a, b) in enumerate(zip(produced, expected)) if a != b), min(len(produced), len(expected)))
        problems.append(f"canonical bytes differ from {EXPECTED_NAME} at byte {offset} ({len(produced)} produced, {len(expected)} expected)")
    if hashlib.sha256(expected).hexdigest() != digest:
        problems.append(f"{DIGEST_NAME} is not the SHA-256 of {EXPECTED_NAME}")
    numbers = document.get("numbers", {}).get("random")
    if numbers != random_numbers():
        problems.append("the seeded random numbers in the input no longer match random_numbers(); rerun --write")
    return problems


def write_fixture(directory: Path) -> None:
    """Regenerate the expected bytes from the hand-written input. The input itself is never
    rewritten: its number spellings (1E2, -0.0e5, 100.000) are part of what is tested."""
    document = loads_strict((directory / INPUT_NAME).read_bytes().decode("utf-8"))
    if document.get("numbers", {}).get("random") != random_numbers():
        raise CanonicalJsonError("numbers.random in the input does not match random_numbers(); paste the output of --random")
    produced = canonical_bytes(document)
    (directory / EXPECTED_NAME).write_bytes(produced)
    (directory / DIGEST_NAME).write_bytes(hashlib.sha256(produced).hexdigest().encode("ascii"))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--check", type=Path, metavar="DIR")
    group.add_argument("--write", type=Path, metavar="DIR")
    group.add_argument("--random", action="store_true", help="print the seeded random numbers as a JSON array")
    parser.add_argument("file", nargs="?", type=Path)
    options = parser.parse_args(argv)
    if options.random:
        print("[" + ", ".join(repr(number) for number in random_numbers()) + "]")
        return 0
    if options.check:
        problems = check_fixture(options.check)
        for problem in problems:
            print(f"FAIL {problem}")
        if not problems:
            digest = (options.check / DIGEST_NAME).read_bytes().decode("ascii")
            print(f"PASS canonical JSON v{VERSION} golden fixture reproduced by Python: sha256 {digest}")
        return 1 if problems else 0
    if options.write:
        write_fixture(options.write)
        print("wrote", options.write / EXPECTED_NAME, (options.write / DIGEST_NAME).read_bytes().decode("ascii"))
        return 0
    if options.file:
        produced = canonical_bytes(loads_strict(options.file.read_bytes().decode("utf-8")))
        sys.stdout.buffer.write(produced + b"\n")
        print(hashlib.sha256(produced).hexdigest())
        return 0
    parser.print_usage()
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
