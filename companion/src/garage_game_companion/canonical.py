"""EnFractal canonical JSON, version 1, as Lane P defined it for the kernel host.

Receipt fingerprints are SHA-256 over these bytes, and the mock host must fingerprint exactly as
the real C# command host will. This follows the Python reference `tools/kernel/canonical_json.py`
on branch run1/play (commit d0fb843), checked against its golden fixture by
companion/tests/test_canonical_json.py:

- the input is strict JSON: no duplicate keys, NaN, Infinity, overflowing numbers or unpaired
  surrogates;
- every number is read as the nearest double, then written as an integer when it is whole and
  within 2**53, otherwise as Python's repr(float);
- strings escape only the quote, the backslash and U+0000-U+001F;
- object members are sorted by Unicode code point; no whitespace; nesting at most 128 levels.

`contracts/validate.py`'s canonical_bytes writes 2.0 where v1 writes 2; the companion uses v1 for
fingerprints and for the size limits.
"""
from __future__ import annotations

import hashlib
import json
import math

VERSION = 1
SAFE_INTEGER = 2 ** 53
MAX_DEPTH = 128


class CanonicalJsonError(ValueError):
    """The value cannot be written as canonical JSON."""


def loads_strict(text: str):
    """Parse JSON the way the kernel does: duplicate keys, NaN and overflowing numbers are refused."""

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
        number = float(literal)
        if not math.isfinite(number):
            raise CanonicalJsonError(f"number {literal} overflows a double")
        return number

    try:
        return json.loads(text, object_pairs_hook=unique_pairs, parse_constant=reject_constant, parse_float=finite_float)
    except json.JSONDecodeError as error:
        raise CanonicalJsonError(f"invalid JSON: {error}") from None
    except CanonicalJsonError:
        raise
    except ValueError:  # an integer literal longer than Python converts (4,300 digits by default)
        raise CanonicalJsonError("a number is too long to read") from None
    except RecursionError:
        raise CanonicalJsonError("the value nests too deeply to read") from None


def normalize(value, depth: int = 0):
    """A copy in which every number is the int or float the canonical form writes."""
    if depth > MAX_DEPTH:
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


def canonical_bytes(value) -> bytes:
    text = json.dumps(normalize(value), sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    try:
        return text.encode("utf-8")
    except UnicodeEncodeError:
        raise CanonicalJsonError("strings must not contain unpaired surrogates") from None


def sha256_hex(value) -> str:
    return hashlib.sha256(canonical_bytes(value)).hexdigest()
