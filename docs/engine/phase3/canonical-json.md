# Canonical JSON, version 1

> **Kernel record, 6 October 2026.** Every fingerprint and content hash in the kernel is the SHA-256 of canonical JSON. Three runtimes compute it, so all three must write the same bytes.

| Runtime | Implementation | Golden-fixture check |
|---|---|---|
| Python (reference) | `tools/kernel/canonical_json.py` | `python tools/kernel/canonical_json.py --check game/tests/fixtures/kernel` and `python -m unittest discover -s tools/kernel` |
| C# (command host fingerprints) | `game/scripts/native/Kernel/CanonicalJson.cs` | `game/tests/native_kernel_canonical_json.tscn` |
| GDScript (creation hashes, authority and world-state saves) | `game/scripts/creation_json.gd`; `creation_compiler.gd canonical_json` delegates to it | `game/tests/kernel_canonical_json_smoke.gd` |

## Rules

1. The input is strict JSON: no duplicate keys, NaN, Infinity, number that overflows a double, number with more than **800 significant digits** (digits of the integer and fraction parts without leading and trailing zeros), more than **64 nested arrays and objects** (the root container is level 1), or unpaired surrogate. These are refused, never repaired, and all three runtimes refuse exactly the same inputs (Lane P review: C# allowed 64 levels, GDScript 65 and Python 70 and up; GDScript alone refused 801 digits; Python leaked `ValueError` for a 5,000-digit integer and `RecursionError` for deep nesting, where it now raises `CanonicalJsonError`). Writing refuses the same nesting.
2. Every number is read as the nearest IEEE-754 double (ties to even). A double that is a whole number with magnitude at most 2^53 is written as an integer: `1`, `1.0`, `1e0` and `100.000E-2` are all written `1`, and `-0` is written `0`. Any other double is written as Python's `repr(float)`: the shortest digits that read back to the same double, positional from 1e-4 up to (not including) 1e16, with at least one digit after the point, otherwise exponent form with a sign and at least two exponent digits (`1e+16`, `1.5e-07`, `9007199254740994.0`).
3. Strings escape only `"`, `\` and U+0000 to U+001F (`\b \t \n \f \r`, otherwise `\u00xx` in lower case). Everything else, including `/`, U+007F, U+2028 and characters beyond U+FFFF, is written as literal UTF-8.
4. Object members are sorted by key in Unicode code point order, which is UTF-8 byte order (UTF-16 order differs for characters beyond U+FFFF).
5. No whitespace and no trailing newline; the bytes are UTF-8 without a byte-order mark.

Equivalently, in Python: `json.dumps(v, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()` after replacing each number by the int or float of rule 2. `contracts/validate.py canonical_bytes` agrees whenever a document's whole numbers are written without a fraction, which the contracts require for integers.

## Why GDScript has its own JSON

Measured on Godot 4.7.2 with 17,844 doubles (random bit patterns over ±1e300, coordinates with up to nine decimals, 17-digit values, powers of ten):

- `JSON.parse_string` returns every number as a float and is not correctly rounded: 4,007 of the 17,844 decimals (22 %) came back one unit in the last place off, including short ones such as `-696.09769`. It also flushes values at or below the smallest normal double (2.2e-308) to zero, and replaces `\u0000` with U+FFFD while logging an error.
- `JSON.stringify` leaves U+0001 to U+001F unescaped (invalid JSON) and uses its own exponent layout.
- `String.num_scientific` gives the shortest digits for most doubles, but not for 32 of the 17,844 (all 17-digit cases).

`creation_json.gd` therefore parses and writes exactly: a decimal is converted by Clinger's fast path when single IEEE operations prove the result (at most 15 digits and an exact power of ten), otherwise by exact long division with arbitrary-precision integers; a double is written from `num_scientific`'s candidate only when the fast path proves it shortest, otherwise by Dragon4 with David Gay's tie rules (Python's). Against Python on the same 17,844 values it has zero parse and zero format mismatches (2.5 s to parse them all, 4.4 s to write them, almost all of it the extreme-exponent third). C# (`double.Parse` and the round-trip format) also has zero mismatches. U+0000 cannot live in a Godot String, so GDScript refuses it; the contracts never allow it in a message.

## The golden fixture

`game/tests/fixtures/kernel/canonical_input.json` is hand-written and never re-serialized: its number spellings (`1E2`, `-0.0e5`, `100.000`, `9007199254740993`, `4.9406564584124654e-324`) are part of the test. It holds string escapes, astral characters, keys whose code point and UTF-16 orders differ, a contract command, and 400 seeded doubles from `canonical_json.py random_numbers()`. `canonical_expected.json` is the Python output (no newline anywhere, so a CRLF checkout cannot change it) and `canonical_expected.sha256` its digest, `ad3d1a8594e808790c035c49f00fc39fd49d49325d3237bb03bba6166fbccc94` (since the rename's phase 4, 24 October 2026, when the "about" came to name the Garage Game canonical JSON v1 and the command `garage_game.command`; it was `a08519e8874d0895f7d9fded02fccb92ebf508101829de3db7c6ad9df2c85589`). The C#, GDScript and Python checks each pin that digest besides reading the file. Regenerate the expected files with `--write` after a deliberate input change; `--check` refuses an input whose random block no longer matches the generator.
