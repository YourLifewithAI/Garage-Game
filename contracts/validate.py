#!/usr/bin/env python3
"""Validate the Garage Game's contract documents.

Usage:
  python contracts/validate.py FILE_OR_ROOM_DIR [...]
  python contracts/validate.py --room game/rooms/test_room
  python contracts/validate.py --state path/to/state.json --room path/to/room_dir
  python contracts/validate.py --check-style-pins game/rooms/<room>     # also verify style pins
  python contracts/validate.py game/rules/storybook_wild/v1.json        # an island rules pack
  python contracts/validate.py contracts/examples/playtests/<record>    # a playtest record's folder
  python contracts/validate.py --pin FILE                                # print a file's SHA-256 pin

A directory containing room.json is validated as a room: the manifest, every asset.json it
references, every listed file (hash, size, self-contained GLB), its plant tables and tiled terrain
GLBs (the 10x world), and the semantic rules JSON Schema cannot express. A directory containing manifest.json
is validated as a playtest record: the manifest, then every line of events.jsonl. A .json file is validated by its own 'schema' field (a style preset or an
island rules pack is one such file). A document may name its kind in either spelling of its id (contracts/schema_ids.py:
garage_game.<kind>, or enfractal.<kind> as files written before the rename and pinned files say). Exit status is 0 only when
everything passes. Requires the packages in contracts/requirements.txt.
"""
from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import math
import re
import bisect
import functools
import importlib.util
import struct
import sys
from pathlib import Path, PurePosixPath

from jsonschema import Draft202012Validator, ValidationError, validators
from jsonschema.exceptions import best_match
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT202012

CONTRACTS_DIR = Path(__file__).resolve().parent
REPO_ROOT = CONTRACTS_DIR.parent
STYLES_DIR = REPO_ROOT / "game" / "styles"
RULES_DIR = REPO_ROOT / "game" / "rules"


def _load_schema_ids():
    """contracts/schema_ids.py, the one id table, loaded by its path (this file is itself often loaded by path)."""
    spec = importlib.util.spec_from_file_location("garage_game_schema_ids", CONTRACTS_DIR / "schema_ids.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


SCHEMA_IDS = _load_schema_ids()
id_of, is_schema, kind_of = SCHEMA_IDS.id_of, SCHEMA_IDS.is_schema, SCHEMA_IDS.kind_of
# The document kinds this validator checks on their own, and their schema files. A document names its kind in either
# spelling (SCHEMA_IDS), so SCHEMA_FILES and MESSAGE_LIMITS are keyed by both.
KIND_FILES = {
    "room": "room-manifest.schema.json",
    "asset": "asset.schema.json",
    "style": "style-preset.schema.json",
    "command": "game-command.schema.json",
    "query": "game-command.schema.json",
    "result": "game-command.schema.json",
    "room_state": "room-state.schema.json",
    "island_rules": "island-rules.schema.json",
    # A team's folder (the mechanics round): the team file, the vitals store (version 2) and the expertise store (version 3).
    "team": "team.schema.json",
    "player_modes": "team.schema.json",
    "glow_progress": "team.schema.json",
    # The playtest record (R0, 15 October): one folder per play under user://playtests/<record_id>/ (manifest.json,
    # events.jsonl, stills/), docs/runs/RUN-2-PLAYTEST-LOOP.md. The manifest is a document of its own; each line of
    # events.jsonl is the schema's $defs/event, checked here line by line.
    "playtest_manifest": "playtest-record.schema.json",
}
SCHEMA_FILES = {spelling: file for kind, file in KIND_FILES.items() for spelling in SCHEMA_IDS.ids_of(kind)}
TEAM_DOCUMENTS = ("team", "player_modes", "glow_progress")  # kinds
PLAYTEST_MANIFEST = id_of("playtest_manifest")
PLAYTEST_MANIFEST_LIMIT = 16384  # bytes of canonical JSON
PLAYTEST_EVENT_LINE_LIMIT = 8192  # bytes of one line of events.jsonl
PLAYTEST_STILLS_LIMIT = 600  # stills per play (the recorder's cap)
# Never in a record, at any depth: result data, a picture, a token, a session file, a save, a secret. The schema refuses
# these at the top of a line and in a result; the check below refuses them wherever they are.
PLAYTEST_FORBIDDEN_KEYS = frozenset({"data", "picture", "base64", "token", "session_file", "save", "secret", "password", "credential"})
PLAYTEST_STILL_FILE = re.compile(r"^([0-9]{4})-(steady|mark|keep|refusal|home|mode|quit)(?:-([1-9][0-9]{0,3}))?\.jpg\Z")
PLAYTEST_UTC = re.compile(r"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,6}))?Z\Z")
MESSAGE_LIMITS = {spelling: limit for kind, limit in (("command", 65536), ("query", 65536), ("result", 262144))
                  for spelling in SCHEMA_IDS.ids_of(kind)}
CREATION_SOURCE_LIMIT = 32768
QUAT_TOLERANCE = 1e-3
PLANE_TOLERANCE_M = 1e-3
BOUNDS_TOLERANCE_M = 1e-3
WALL_PROBE_M = 0.05
STATE_KINDS = {"obj": "object", "creation": "creation", "avatar": "avatar"}


class ContractError(Exception):
    """A document cannot be loaded or violates a contract rule."""


def sha256_file(path: Path) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def canonical_bytes(document) -> bytes:
    return json.dumps(document, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def load_strict(path: Path):
    """Parse JSON the way pinned files must be written: UTF-8, LF, no duplicate keys, finite numbers."""
    raw = Path(path).read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raise ContractError("UTF-8 byte-order mark is not allowed")
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ContractError(f"not UTF-8: {error}") from None
    if "\r" in text:
        raise ContractError("CR line endings are not allowed; pins hash LF bytes")
    return loads_strict(text)


def loads_strict(text: str):
    def unique_pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ContractError(f"duplicate key {key!r}")
            result[key] = value
        return result

    def reject_constant(name):
        raise ContractError(f"non-finite number {name} is not allowed")

    def finite_float(text_value):
        value = float(text_value)
        if not math.isfinite(value):
            raise ContractError(f"number {text_value} overflows to a non-finite value")
        return value

    try:
        return json.loads(text, object_pairs_hook=unique_pairs, parse_constant=reject_constant, parse_float=finite_float)
    except json.JSONDecodeError as error:
        raise ContractError(f"invalid JSON: {error}") from None
    except ContractError:
        raise
    except ValueError:  # an integer literal longer than Python converts (4,300 digits by default)
        raise ContractError("a number is too long to read") from None
    except RecursionError:
        raise ContractError("the document nests too deeply to read") from None


# 'integer' means a JSON number written without a fraction, as the C# and GDScript readers require.
_STRICT_TYPES = Draft202012Validator.TYPE_CHECKER.redefine(
    "integer", lambda _checker, instance: isinstance(instance, int) and not isinstance(instance, bool))


def ecma_pattern(pattern: str) -> str:
    """Translate an ECMA-262 pattern (JSON Schema's dialect, no flags) for Python's re.

    ECMA-262's '$' matches only at the very end of the input; Python's (and .NET's) '$' also
    matches just before a final newline, so "approved\\n" passed '^[^...]*$'. Every '$' that is
    neither escaped nor inside a character class becomes '\\Z'. The contract's patterns use no
    other construct whose meaning differs."""
    out, in_class, i = [], False, 0
    while i < len(pattern):
        ch = pattern[i]
        if ch == "\\" and i + 1 < len(pattern):
            out.append(pattern[i:i + 2])
            i += 2
            continue
        if in_class:
            in_class = ch != "]"
        elif ch == "[":
            in_class = True
            prefix = "[^" if pattern.startswith("[^", i) else "["
            out.append(prefix)
            i += len(prefix)
            if i < len(pattern) and pattern[i] == "]":  # a ']' first in a class is a member
                out.append("]")
                i += 1
            continue
        elif ch == "$":
            out.append(r"\Z")
            i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)


@functools.lru_cache(maxsize=512)
def _compiled(pattern: str):
    return re.compile(ecma_pattern(pattern))


def _ecma_pattern_keyword(validator, pattern, instance, schema):
    if validator.is_type(instance, "string") and not _compiled(pattern).search(instance):
        yield ValidationError(f"{instance!r} does not match {pattern!r}")


StrictValidator = validators.extend(Draft202012Validator, {"pattern": _ecma_pattern_keyword}, type_checker=_STRICT_TYPES)


# Extended_Pictographic (UTS #51, Unicode 15.1 emoji-data.txt), merged into ranges. It reserves whole
# blocks for future emoji, so new emoji need no table update.
_EXTENDED_PICTOGRAPHIC = (
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
_PICTOGRAPHIC_STARTS = tuple(low for low, _high in _EXTENDED_PICTOGRAPHIC)
_KEYCAP_BASES = frozenset(map(ord, "0123456789#*"))
_VS15, _VS16, _ZWJ, _KEYCAP = 0xFE0E, 0xFE0F, 0x200D, 0x20E3


def _pictographic(code: int) -> bool:
    index = bisect.bisect_right(_PICTOGRAPHIC_STARTS, code) - 1
    return index >= 0 and code <= _EXTENDED_PICTOGRAPHIC[index][1]


def _misplaced_marker(text: str) -> str | None:
    """The first emoji marker out of place. The text patterns let VS15, VS16, the zero-width joiner and
    the keycap combiner through because emoji use them; they are allowed only where an emoji puts them:
    a selector right after an Extended_Pictographic character or a keycap base (0-9, # or *), so at most
    one per base; the joiner between two emoji (the one before may carry one VS16 or skin tone); the
    keycap after a keycap base, optionally with VS16. Alone or in runs they could carry hidden data."""
    for i, ch in enumerate(text):
        code = ord(ch)
        if code not in (_VS15, _VS16, _ZWJ, _KEYCAP):
            continue
        before = ord(text[i - 1]) if i else -1
        if code in (_VS15, _VS16):
            in_place = _pictographic(before) or before in _KEYCAP_BASES
        elif code == _KEYCAP:
            in_place = before in _KEYCAP_BASES or (before == _VS16 and i >= 2 and ord(text[i - 2]) in _KEYCAP_BASES)
        else:
            j = i - 1
            if j >= 0 and (ord(text[j]) == _VS16 or 0x1F3FB <= ord(text[j]) <= 0x1F3FF):
                j -= 1
            in_place = (i + 1 < len(text) and _pictographic(ord(text[i + 1])) and j >= 0
                        and _pictographic(ord(text[j])))
        if not in_place:
            return f"U+{code:04X}"
    return None


def _hidden_code_point(text: str) -> str | None:
    """Plane 14 (U+E0000-U+EFFFF: the TAG block, which can spell a hidden message, and the variation
    selector supplement), unpaired surrogates and misplaced emoji markers. The text patterns refuse
    plane 14 only in UTF-16 readers, through \\uDB40-\\uDB7F, and cannot see a marker's context;
    Python matches code points, so it checks here, in every string."""
    for ch in text:
        code = ord(ch)
        if 0xE0000 <= code <= 0xEFFFF or 0xD800 <= code <= 0xDFFF:
            return f"U+{code:04X}"
    return _misplaced_marker(text)


def _text_problems(value, path: str = "$") -> list[str]:
    if isinstance(value, str):
        found = _hidden_code_point(value)
        return [f"{path}: contains the invisible, unpaired or misplaced character {found}"] if found else []
    problems = []
    if isinstance(value, dict):
        for key, item in value.items():
            found = _hidden_code_point(key) if isinstance(key, str) else None
            if found:
                problems.append(f"{path}: a field name contains the invisible, unpaired or misplaced character {found}")
            problems += _text_problems(item, f"{path}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            problems += _text_problems(item, f"{path}[{index}]")
    return problems


def _registry() -> Registry:
    resources = []
    for path in sorted(CONTRACTS_DIR.glob("*.schema.json")):
        contents = load_strict(path)
        Draft202012Validator.check_schema(contents)
        resources.append((contents["$id"], Resource.from_contents(contents, default_specification=DRAFT202012)))
    return Registry().with_resources(resources)


_REGISTRY: Registry | None = None
_VALIDATORS: dict[str, Draft202012Validator] = {}


def validator_for(schema_name: str):
    global _REGISTRY
    if schema_name not in SCHEMA_FILES:
        raise ContractError(f"unknown schema {schema_name!r}; expected one of {sorted(SCHEMA_FILES)}")
    file_name = SCHEMA_FILES[schema_name]
    if file_name not in _VALIDATORS:
        if _REGISTRY is None:
            _REGISTRY = _registry()
        _VALIDATORS[file_name] = StrictValidator(load_strict(CONTRACTS_DIR / file_name), registry=_REGISTRY)
    return _VALIDATORS[file_name]


def _json_path(error) -> str:
    path = "$"
    for part in error.absolute_path:
        path += f"[{part}]" if isinstance(part, int) else f".{part}"
    return path


def _schema_messages(validator, document) -> list[str]:
    messages = []
    for error in sorted(validator.iter_errors(document), key=lambda e: list(map(str, e.absolute_path))):
        detail = error.message
        if error.validator == "not":
            detail = f"must not match {json.dumps(error.validator_value, sort_keys=True)}"
        if error.context:
            deepest = best_match(error.context)
            detail = f"{detail} (closest: {_json_path(deepest)}: {deepest.message})"
        messages.append(f"{_json_path(error)}: {detail}")
    return messages


def schema_errors(document, expected: str | None = None) -> list[str]:
    """Schema problems of a document. `expected` names the kind it must be, as a kind ("room") or either id."""
    if not isinstance(document, dict) or not isinstance(document.get("schema"), str):
        return ["$: document must be an object with a string 'schema' field"]
    if expected is not None:
        kind = kind_of(expected) or expected
        if not is_schema(document["schema"], kind):
            return [f"$.schema: expected {id_of(kind)!r} (or {SCHEMA_IDS.ids_of(kind)[1]!r}), found {document['schema']!r}"]
    if document["schema"] not in SCHEMA_FILES:
        return [f"$.schema: unknown schema {document['schema']!r}"]
    messages = _schema_messages(validator_for(document["schema"]), document)
    hidden = _text_problems(document)
    if hidden:
        return messages + hidden  # an unpaired surrogate cannot be measured in UTF-8 either
    limit = MESSAGE_LIMITS.get(document["schema"])
    if limit and len(canonical_bytes(document)) > limit:
        messages.append(f"$: message is {len(canonical_bytes(document))} bytes of canonical JSON; the limit is {limit}")
    for source in _creation_sources(document):
        if len(canonical_bytes(source)) > CREATION_SOURCE_LIMIT:
            messages.append(f"$: a creation source exceeds {CREATION_SOURCE_LIMIT} bytes of canonical JSON")
    return messages


def _creation_sources(document):
    if not is_schema(document.get("schema"), "command"):
        return []
    args = document.get("args") if isinstance(document.get("args"), dict) else {}
    found = [args.get("source"), (args.get("into") or {}).get("source") if isinstance(args.get("into"), dict) else None]
    return [s for s in found if isinstance(s, dict)]


# ---------- binary checks ----------

def check_glb(data: bytes, label: str) -> list[str]:
    """A pinned GLB must be self-contained: every buffer is the embedded BIN chunk or a data: URI."""
    if len(data) < 20 or data[:4] != b"glTF":
        return [f"{label}: not a binary glTF file"]
    _, version, length = struct.unpack_from("<4sII", data, 0)
    if version != 2 or length != len(data):
        return [f"{label}: GLB header says version {version} and {length} bytes; file is {len(data)} bytes"]
    chunk_length, chunk_type = struct.unpack_from("<I4s", data, 12)
    if chunk_type != b"JSON" or 20 + chunk_length > len(data):
        return [f"{label}: GLB does not start with a JSON chunk"]
    try:
        gltf = loads_strict(data[20:20 + chunk_length].decode("utf-8").rstrip(" \x00"))
    except (UnicodeDecodeError, ContractError) as error:
        return [f"{label}: GLB JSON chunk is invalid: {error}"]
    if not isinstance(gltf, dict):
        return [f"{label}: GLB JSON chunk is not an object"]
    problems = []
    for kind in ("buffers", "images"):
        items = gltf.get(kind, [])
        if not isinstance(items, list):
            problems.append(f"{label}: GLB {kind!r} entry is not an array")
            continue
        for index, item in enumerate(items):
            uri = item.get("uri") if isinstance(item, dict) else None
            if uri is not None and not (isinstance(uri, str) and uri.startswith("data:")):
                problems.append(f"{label}: {kind}[{index}] references external file {uri!r}; meshes must be self-contained")
    return problems


# ---------- geometry helpers ----------

def _quat_ok(q) -> bool:
    return abs(math.sqrt(sum(c * c for c in q)) - 1.0) <= QUAT_TOLERANCE


def _sub(a, b):
    return [a[i] - b[i] for i in range(3)]


def _dot(a, b):
    return sum(a[i] * b[i] for i in range(3))


def _newell_normal(points):
    n = [0.0, 0.0, 0.0]
    for i, p in enumerate(points):
        q = points[(i + 1) % len(points)]
        n[0] += (p[1] - q[1]) * (p[2] + q[2])
        n[1] += (p[2] - q[2]) * (p[0] + q[0])
        n[2] += (p[0] - q[0]) * (p[1] + q[1])
    length = math.sqrt(_dot(n, n))
    return [c / length for c in n] if length > 1e-12 and math.isfinite(length) else None


def _inside(point, bounds, tolerance=BOUNDS_TOLERANCE_M) -> bool:
    return all(bounds["min_m"][i] - tolerance <= point[i] <= bounds["max_m"][i] + tolerance for i in range(3))


def _inside_xz_polygon(x: float, z: float, polygon) -> bool:
    inside = False
    for i, a in enumerate(polygon):
        b = polygon[(i + 1) % len(polygon)]
        if (a[2] > z) != (b[2] > z):
            crossing = a[0] + (z - a[2]) * (b[0] - a[0]) / (b[2] - a[2])
            if x < crossing:
                inside = not inside
    return inside


# ---------- files ----------

def _check_listed_files(base: Path, files: list, referenced: set[str], label: str) -> list[str]:
    problems = []
    listed = {}
    for entry in files:
        if entry["path"] in listed:
            problems.append(f"{label}: file {entry['path']} is listed twice")
        listed[entry["path"]] = entry
    for path in sorted(referenced):
        if path not in listed:
            problems.append(f"{label}: referenced file {path} is not in the files list")
    root = base.resolve()
    for path, entry in sorted(listed.items()):
        target = (base / PurePosixPath(path)).resolve()
        if root not in target.parents:
            problems.append(f"{label}: file {path} escapes its directory")
            continue
        if not target.is_file():
            problems.append(f"{label}: listed file {path} does not exist")
            continue
        data = target.read_bytes()
        if len(data) != entry["bytes"]:
            problems.append(f"{label}: {path} is {len(data)} bytes, files list says {entry['bytes']}")
        if hashlib.sha256(data).hexdigest() != entry["sha256"]:
            problems.append(f"{label}: {path} hash does not match the files list")
        if path.endswith(".glb"):
            problems += check_glb(data, f"{label}: {path}")
    return problems


# ---------- assets and rooms ----------

def check_asset(asset: dict, asset_dir: Path, label: str) -> list[str]:
    problems = []
    referenced = set()
    geometry = asset["geometry"]
    if geometry["kind"] == "mesh":
        referenced.add(geometry["mesh"])
        for lod in geometry.get("lods", []):
            referenced.add(lod["mesh"])
            if lod["triangle_count"] >= geometry["triangle_count"]:
                problems.append(f"{label}: LOD {lod['mesh']} is not lighter than the base mesh")
    elif [m["slot"] for m in asset["materials"]] != ["body"]:
        problems.append(f"{label}: a primitive has exactly one material slot named 'body'")
    if "file" in asset["collision"]:
        referenced.add(asset["collision"]["file"])
    for variant in asset.get("styles", {}).values():
        if "mesh" in variant:
            referenced.add(variant["mesh"])
        referenced.update(variant.get("textures", []))
    slots = [m["slot"] for m in asset["materials"]]
    if len(slots) != len(set(slots)):
        problems.append(f"{label}: material slot names must be unique")
    part_ids = [p["id"] for p in asset.get("parts", [])]
    if len(part_ids) != len(set(part_ids)):
        problems.append(f"{label}: part ids must be unique")
    if "openable" in asset["affordances"] and not part_ids:
        problems.append(f"{label}: an openable asset needs at least one part")
    problems += _check_listed_files(asset_dir, asset["files"], referenced, label)
    return problems


def check_room(room_dir: Path, check_style_pins: bool = False, manifest_name: str = "room.json", styles_dir: Path = STYLES_DIR) -> list[str]:
    """Validate a room manifest, its assets and files. Returns a list of problems (empty when valid)."""
    room_dir = Path(room_dir)
    label = f"{room_dir.name}/{manifest_name}"
    try:
        room = load_strict(room_dir / manifest_name)
    except (OSError, ContractError) as error:
        return [f"{label}: {error}"]
    problems = [f"{label} {message}" for message in schema_errors(room, "room")]
    if problems:
        return problems
    if room["room_id"] != room_dir.name:
        problems.append(f"{label}: room_id {room['room_id']!r} must match its directory name {room_dir.name!r}")
    bounds = room["bounds"]
    if any(bounds["min_m"][i] >= bounds["max_m"][i] for i in range(3)):
        return problems + [f"{label}: bounds min must be below max on every axis"]

    ids, referenced, shell_roles = [], set(), {}
    footprints, walls = [], []
    for part in room["shell"]["parts"]:
        ids.append(part["id"])
        shell_roles[part["id"]] = part["role"]
        if part.get("drawn") is False and not part["collides"]:
            problems.append(f"{label}: {part['id']} is neither drawn nor collides")
        geometry = part["geometry"]
        if "texture" in part:
            referenced.add(part["texture"])
        if geometry["kind"] == "mesh":
            referenced.add(geometry["mesh"])
            continue
        points = geometry["points_m"]
        normal = _newell_normal(points)
        if normal is None:
            problems.append(f"{label}: {part['id']} polygon is degenerate")
            continue
        if any(abs(_dot(_sub(p, points[0]), normal)) > PLANE_TOLERANCE_M for p in points):
            problems.append(f"{label}: {part['id']} polygon is not planar")
        role = part["role"]
        if role in ("floor", "ground", "platform", "stair") and normal[1] <= 0.5:
            problems.append(f"{label}: {part['id']} must face upward (its winding points its normal down or sideways)")
        elif role == "ceiling" and normal[1] >= -0.5:
            problems.append(f"{label}: {part['id']} must face downward into the room")
        if role in ("floor", "ground"):
            footprints.append(points)
        if role == "wall":
            walls.append((part["id"], points, normal, geometry["thickness_m"]))
    # A wall faces the room when a point just beyond its own thickness, along its normal, is inside the
    # room: within the bounds and over a floor polygon. This holds for L-shaped rooms and dividers.
    for part_id, points, normal, thickness in walls:
        centre = [sum(p[i] for p in points) / len(points) for i in range(3)]
        probe = [centre[i] + normal[i] * (thickness + WALL_PROBE_M) for i in range(3)]
        inside_bounds = all(bounds["min_m"][i] - BOUNDS_TOLERANCE_M <= probe[i] <= bounds["max_m"][i] + BOUNDS_TOLERANCE_M for i in (0, 2))
        over_floor = not footprints or any(_inside_xz_polygon(probe[0], probe[2], f) for f in footprints)
        if not (inside_bounds and over_floor):
            problems.append(f"{label}: {part_id} winding faces away from the room interior")

    opening_ids = []
    for opening in room["shell"].get("openings", []):
        opening_ids.append(opening["id"])
        if opening["host_part_id"] not in shell_roles:
            problems.append(f"{label}: opening {opening['id']} hosts on unknown part {opening['host_part_id']}")
        elif shell_roles[opening["host_part_id"]] not in ("wall", "ceiling", "floor", "ground"):
            problems.append(f"{label}: opening {opening['id']} must sit in a wall, ceiling, floor or ground part")
        if not _inside(opening["center_m"], bounds, 0.5):
            problems.append(f"{label}: opening {opening['id']} centre is outside the room")
    if len(opening_ids) != len(set(opening_ids)):
        problems.append(f"{label}: opening ids must be unique")

    asset_cache: dict[str, dict] = {}
    for instance in room["objects"]:
        ids.append(instance["id"])
        transform = instance["transform"]
        if not _quat_ok(transform["rotation"]):
            problems.append(f"{label}: {instance['id']} rotation is not a unit quaternion")
        if not _inside(transform["position_m"], bounds):
            problems.append(f"{label}: {instance['id']} pivot is outside the room bounds")
        referenced.add(instance["asset"])
        if instance["asset"] not in asset_cache:
            asset_path = room_dir / PurePosixPath(instance["asset"])
            asset_label = f"{room_dir.name}/{instance['asset']}"
            try:
                asset = load_strict(asset_path)
            except (OSError, ContractError) as error:
                problems.append(f"{asset_label}: {error}")
                asset_cache[instance["asset"]] = {}
                continue
            asset_problems = [f"{asset_label} {message}" for message in schema_errors(asset, "asset")]
            if not asset_problems:
                asset_problems = check_asset(asset, asset_path.parent, asset_label)
                if asset["asset_id"] != asset_path.parent.name:
                    asset_problems.append(f"{asset_label}: asset_id must match its directory name")
            problems += asset_problems
            asset_cache[instance["asset"]] = asset if not asset_problems else {}
        asset = asset_cache[instance["asset"]]
        for part_id in instance.get("initial_parts", {}):
            if asset and part_id not in {p["id"] for p in asset.get("parts", [])}:
                problems.append(f"{label}: {instance['id']} sets unknown part {part_id}")

    if len(ids) != len(set(ids)):
        problems.append(f"{label}: shell part and object ids must be unique")
    known = set(ids)
    for instance in room["objects"]:
        target = instance["support"].get("target_id")
        if target is not None and target.startswith("plant:"):
            # Copies burn and regrow, so nothing in the baseline rests on one.
            problems.append(f"{label}: {instance['id']} rests on the plant copy {target}; no manifest object rests on a copy")
        elif target is not None and target not in known:
            problems.append(f"{label}: {instance['id']} rests on unknown entity {target}")
        if target == instance["id"]:
            problems.append(f"{label}: {instance['id']} cannot rest on itself")

    spawn_ids = []
    for spawn in room["spawns"]:
        spawn_ids.append(spawn["id"])
        if not _inside(spawn["position_m"], bounds):
            problems.append(f"{label}: spawn {spawn['id']} is outside the room bounds")
    if len(spawn_ids) != len(set(spawn_ids)):
        problems.append(f"{label}: spawn ids must be unique")
    hint_ids = [hint["id"] for hint in room.get("light_hints", [])]
    if len(hint_ids) != len(set(hint_ids)):
        problems.append(f"{label}: light hint ids must be unique")

    reference = room.get("reference", {})
    if "splat" in reference:
        referenced.add(reference["splat"])
    for view in reference.get("views", []):
        referenced.add(view["image"])
        if not _quat_ok(view["camera"]["transform"]["rotation"]):
            problems.append(f"{label}: reference view {view['id']} rotation is not a unit quaternion")

    fractals = room.get("extensions", {}).get("x_fractals")
    if fractals is not None:
        # The mechanics round (M0): each Fractal listed once, inside the island.
        fractal_ids = [item["id"] for item in fractals["items"]]
        for name in sorted({i for i in fractal_ids if fractal_ids.count(i) > 1}):
            problems.append(f"{label}: x_fractals lists {name} more than once")
        for item in fractals["items"]:
            if not _inside(item["position_m"], bounds):
                problems.append(f"{label}: x_fractals {item['id']} is outside the room bounds")
    summit = room.get("extensions", {}).get("x_summit")
    if summit is not None and not _inside(summit["center_m"], bounds):
        # The second contract round (15 October): the summit inside the island.
        problems.append(f"{label}: x_summit is outside the room bounds")
    firepits = room.get("extensions", {}).get("x_firepits")
    for number, item in enumerate(firepits["items"] if firepits is not None else [], 1):
        # Decision 74 (24 October): each firepit inside the island, numbered as the kernel numbers them.
        if not _inside(item["position_m"], bounds):
            problems.append(f"{label}: x_firepits {number} is outside the room bounds")

    listed = {entry["path"] for entry in room["files"]}
    if "plant_copies" in room:
        problems += check_plant_copies(room, room_dir, listed, referenced, label)
    if "terrain_tiles" in room:
        problems += check_terrain_tiles(room, room_dir, listed, label)
    problems += _check_listed_files(room_dir, room["files"], referenced, label)
    if check_style_pins and "default_style" in room:
        problems += check_style_pin(room["default_style"], label, styles_dir)
    return problems


# ---------- plant copies and terrain tiles (the 10x world) ----------

# A plant table (README, "Plant copies"): a 40-byte header, the tile index, then fixed-width rows, all little-endian.
PLANT_TABLE_MAGIC = b"EFPLANTS"
PLANT_TABLE_VERSION = 1
PLANT_TABLE_HEADER = struct.Struct("<8s8I")  # magic, version, kind, row_bytes, row_count, tile_count, cap_slots, tile_m, reserved
PLANT_TILE_ENTRY = struct.Struct("<iiII")  # tx, tz, first row, row count
PLANT_ROW = struct.Struct("<11f3I")  # x, y, z, yaw_deg, scale x, y, z, tint r, g, b, top_m; flags, group, variant_seed
PLANT_TREE_TAIL = struct.Struct("<6fI")  # trunk x, z, trunk_radius_m, pole_radius_m, trunk_top_m, pole_top_m; cap_count
PLANT_CAP = struct.Struct("<8f")  # x, z, rim_half_x_m, rim_half_z_m, rim_m, top_m, top_half_x_m, top_half_z_m
PLANT_KIND_CODES = {"plant": 0, "tree": 1, "stone": 2}
PLANT_TILE_M = 8
PLANT_FLAGS = {"submerged": 1}  # bit 0: the copy's base is under water; every other bit is 0
# Copies per square metre of the bounds' footprint, all sets together: a limit that grows with the land, not with a
# scale factor (today about 60 per square metre at 1x and at 10x).
PLANT_DENSITY_PER_M2 = 256
TERRAIN_TILE_M = 16
TERRAIN_NODE = re.compile(r"tile_(0|-?[1-9][0-9]{0,4})_(0|-?[1-9][0-9]{0,4})_lod([0-9])\Z")
PIVOT_TOLERANCE_M = 2e-3


def plant_row_bytes(kind: str, cap_slots: int = 0) -> int:
    """The width of one row: 56 bytes, and for a tree 28 more and 32 per cap slot."""
    if kind == "tree":
        return PLANT_ROW.size + PLANT_TREE_TAIL.size + PLANT_CAP.size * cap_slots
    return PLANT_ROW.size


def plant_tile(x: float, z: float) -> tuple[int, int]:
    """The 8 m tile of a copy, from its stored float32 coordinates (exact: 8 is a power of two)."""
    return math.floor(x / PLANT_TILE_M), math.floor(z / PLANT_TILE_M)


def read_plant_table(data: bytes, kind: str, cap_slots: int = 0):
    """Split a plant table into its header, tile index and rows. Each row is a tuple: the 14 common fields, then for a
    tree the 7 tail fields and cap_slots groups of 8 cap fields. Raises ContractError when the bytes cannot be split."""
    if len(data) < PLANT_TABLE_HEADER.size:
        raise ContractError(f"is {len(data)} bytes, shorter than the {PLANT_TABLE_HEADER.size}-byte header")
    magic, version, kind_code, row_bytes, row_count, tile_count, slots, tile_m, reserved = PLANT_TABLE_HEADER.unpack_from(data, 0)
    header = {"magic": magic, "version": version, "kind": kind_code, "row_bytes": row_bytes, "row_count": row_count,
              "tile_count": tile_count, "cap_slots": slots, "tile_m": tile_m, "reserved": reserved}
    expected = {"magic": PLANT_TABLE_MAGIC, "version": PLANT_TABLE_VERSION, "kind": PLANT_KIND_CODES[kind],
                "row_bytes": plant_row_bytes(kind, cap_slots), "cap_slots": cap_slots if kind == "tree" else 0,
                "tile_m": PLANT_TILE_M, "reserved": 0}
    for name, value in expected.items():
        if header[name] != value:
            raise ContractError(f"header {name} is {header[name]!r}; expected {value!r}")
    size = PLANT_TABLE_HEADER.size + PLANT_TILE_ENTRY.size * tile_count + row_bytes * row_count
    if len(data) != size:
        raise ContractError(f"is {len(data)} bytes; its header's {tile_count} tiles and {row_count} rows of {row_bytes} bytes make {size}")
    start = PLANT_TABLE_HEADER.size
    tiles = list(PLANT_TILE_ENTRY.iter_unpack(data[start:start + PLANT_TILE_ENTRY.size * tile_count]))
    row_format = struct.Struct("<11f3I" + ("6fI" + "8f" * cap_slots if kind == "tree" else ""))
    rows = list(row_format.iter_unpack(data[start + PLANT_TILE_ENTRY.size * tile_count:]))
    return header, tiles, rows


def _glb_document(data: bytes) -> dict | None:
    """The JSON chunk of a GLB check_glb accepts, or None."""
    if check_glb(data, "mesh"):
        return None
    length = struct.unpack_from("<I", data, 12)[0]
    return loads_strict(data[20:20 + length].decode("utf-8").rstrip(" \x00"))


def _glb_mesh_extent(gltf: dict, mesh_index: int) -> tuple[list[float], list[float]] | None:
    """The union of a mesh's POSITION accessor bounds (glTF requires min and max on them), or None when missing."""
    try:
        low, high = [math.inf] * 3, [-math.inf] * 3
        for primitive in gltf["meshes"][mesh_index]["primitives"]:
            accessor = gltf["accessors"][primitive["attributes"]["POSITION"]]
            for i in range(3):
                low[i] = min(low[i], float(accessor["min"][i]))
                high[i] = max(high[i], float(accessor["max"][i]))
        return (low, high) if all(math.isfinite(v) for v in low + high) else None
    except (KeyError, IndexError, TypeError, ValueError):
        return None


def _glb_triangles(gltf: dict) -> int | None:
    """Triangles a GLB's meshes hold (indexed or not, mode 4 only), or None when it holds anything else."""
    try:
        total = 0
        for mesh in gltf.get("meshes", []):
            for primitive in mesh["primitives"]:
                if primitive.get("mode", 4) != 4:
                    return None
                accessor = gltf["accessors"][primitive["indices"] if "indices" in primitive else primitive["attributes"]["POSITION"]]
                total += int(accessor["count"]) // 3
        return total
    except (KeyError, IndexError, TypeError, ValueError):
        return None


def _read_listed(room_dir: Path, listed: set[str], path: str) -> bytes | None:
    """A listed file's bytes for the semantic checks; unlisted or missing files are reported by the files check."""
    target = room_dir / PurePosixPath(path)
    if path not in listed or not target.is_file():
        return None
    return target.read_bytes()


def check_plant_copies(room: dict, room_dir: Path, listed: set[str], referenced: set[str], label: str) -> list[str]:
    """What the schema cannot say about plant_copies: each set's meshes and table against its manifest record, every
    row's numbers, the sort order and the tile index, the group numbering and the density limit."""
    copies, bounds = room["plant_copies"], room["bounds"]
    problems: list[str] = []
    names = [s["set"] for s in copies["sets"]]
    for name in sorted({n for n in names if names.count(n) > 1}):
        problems.append(f"{label}: plant set {name} is listed twice")
    tables = [s["table"] for s in copies["sets"]]
    for path in sorted({t for t in tables if tables.count(t) > 1}):
        problems.append(f"{label}: plant table {path} belongs to more than one set")
    footprint = (bounds["max_m"][0] - bounds["min_m"][0]) * (bounds["max_m"][2] - bounds["min_m"][2])
    total = sum(s["count"] for s in copies["sets"])
    if total > PLANT_DENSITY_PER_M2 * footprint:
        problems.append(f"{label}: {total} plant copies on {footprint:.0f} m2 of bounds; at most {PLANT_DENSITY_PER_M2} per square metre")
    groups_used: set[int] = set()
    complete = True  # every table read: only then can a group be known to be unused
    for record in copies["sets"]:
        where = f"{label}: plant set {record['set']}"
        meshes = [(record["mesh"], record["triangle_count"])] + [(lod["mesh"], lod["triangle_count"]) for lod in record.get("lods", [])]
        referenced.update(path for path, _ in meshes)
        referenced.add(record["table"])
        for (path, triangles), (_previous, lighter_than) in zip(meshes[1:], meshes):
            if triangles >= lighter_than:
                problems.append(f"{where}: {path} is not lighter than the mesh before it")
        height = None
        for index, (path, triangles) in enumerate(meshes):
            data = _read_listed(room_dir, listed, path)
            gltf = _glb_document(data) if data is not None else None
            if gltf is None:
                continue  # missing, unlisted or not self-contained: the files check reports it
            if _glb_triangles(gltf) != triangles:
                problems.append(f"{where}: {path} holds {_glb_triangles(gltf)} triangles; the manifest says {triangles}")
            extents = [_glb_mesh_extent(gltf, m) for m in range(len(gltf.get("meshes", [])))]
            if not extents or None in extents:
                problems.append(f"{where}: {path} has no mesh with POSITION bounds")
                continue
            low = [min(e[0][i] for e in extents) for i in range(3)]
            high = [max(e[1][i] for e in extents) for i in range(3)]
            if (abs(low[1]) > PIVOT_TOLERANCE_M or abs(low[0] + high[0]) > PIVOT_TOLERANCE_M
                    or abs(low[2] + high[2]) > PIVOT_TOLERANCE_M):
                problems.append(f"{where}: {path} is not pivoted at its bottom centre")
            if index == 0:
                height = high[1]
        data = _read_listed(room_dir, listed, record["table"])
        if data is None:
            complete = False
            continue
        try:
            _header, tiles, rows = read_plant_table(data, record["kind"], record.get("cap_slots", 0))
        except ContractError as error:
            problems.append(f"{where}: table {record['table']} {error}")
            complete = False
            continue
        if len(rows) != record["count"] or len(tiles) != record["tiles"]:
            problems.append(f"{where}: table holds {len(rows)} rows in {len(tiles)} tiles; the manifest says {record['count']} in {record['tiles']}")
            complete = False
            continue
        problems += _check_plant_rows(record, tiles, rows, bounds, copies["groups"], height, groups_used, where)
    missing = sorted(set(range(1, copies["groups"] + 1)) - groups_used)
    if complete and missing:
        problems.append(f"{label}: plant group {missing[0]} has no copy; groups are numbered 1 to {copies['groups']} with none unused")
    return problems


def _check_plant_rows(record: dict, tiles: list, rows: list, bounds: dict, groups: int, height: float | None,
                      groups_used: set[int], where: str, limit: int = 8) -> list[str]:
    """The tile index, the sort order and every row's numbers. Stops after `limit` problems: a broken table repeats."""
    problems: list[str] = []
    low, high = bounds["min_m"], bounds["max_m"]
    tol = BOUNDS_TOLERANCE_M
    expected_first, previous_tile = 0, None
    for index, (tx, tz, first, count) in enumerate(tiles):
        if previous_tile is not None and (tx, tz) <= previous_tile:
            problems.append(f"{where}: tile index entry {index} ({tx}, {tz}) is not after ({previous_tile[0]}, {previous_tile[1]}); tiles are sorted by tx, then tz")
        if count < 1 or first != expected_first:
            problems.append(f"{where}: tile index entry {index} ({tx}, {tz}) starts at row {first} with {count}; expected row {expected_first} and at least one")
        previous_tile, expected_first = (tx, tz), first + count
        if problems:
            return problems
    if expected_first != len(rows):
        return [f"{where}: the tile index covers {expected_first} rows; the table has {len(rows)}"]
    tree = record["kind"] == "tree"
    slots = record.get("cap_slots", 0)
    for tx, tz, first, count in tiles:
        previous = None
        for row in range(first, first + count):
            values = rows[row]
            x, y, z, yaw, sx, sy, sz, r, g, b, top, flags, group, _seed = values[:14]
            at = f"{where}: row {row} (plant:{record['set']}-{row})"
            floats = [v for v in values if isinstance(v, float)]
            if not all(math.isfinite(v) for v in floats):
                problems.append(f"{at} has a non-finite number")
            elif plant_tile(x, z) != (tx, tz):
                problems.append(f"{at} at ({x}, {z}) lies in tile {plant_tile(x, z)}, not in its index entry ({tx}, {tz})")
            elif previous is not None and (x, z) < previous:
                problems.append(f"{at} is out of order: rows in a tile are sorted by x, then z")
            elif not all(low[i] - tol <= (x, y, z)[i] <= high[i] + tol for i in range(3)):
                problems.append(f"{at} is outside the room bounds")
            elif not -180 <= yaw <= 180:
                problems.append(f"{at} has yaw {yaw}; it is between -180 and 180 degrees")
            elif not all(0 < s <= 100 for s in (sx, sy, sz)):
                problems.append(f"{at} has a scale outside (0, 100]")
            elif not all(0 <= c <= 1 for c in (r, g, b)):
                problems.append(f"{at} has a tint outside [0, 1]")
            elif height is not None and abs(top - (y + height * sy)) > 1e-3:
                problems.append(f"{at} has top_m {top}; its y plus its mesh's height times its y scale is {y + height * sy:.4f}")
            elif top > high[1] + tol:
                problems.append(f"{at} reaches above the room bounds")
            elif flags & ~sum(PLANT_FLAGS.values()):
                problems.append(f"{at} sets flag bits {flags:#x}; only {sum(PLANT_FLAGS.values()):#x} are defined")
            elif group > groups:
                problems.append(f"{at} is in group {group}; the room has {groups}")
            elif tree:
                problem = _tree_row_problem(values[14:], slots, y, low, high)
                if problem:
                    problems.append(f"{at} {problem}")
            groups_used.add(group)
            previous = (x, z)
            if len(problems) >= limit:
                return problems + [f"{where}: stopped after {limit} problems"]
    return problems


def _tree_row_problem(tail, slots: int, y: float, low, high) -> str | None:
    """A tree row's climbing collision (pipeline/landscape/export/trees.py fits it to the ground): a trunk from the
    copy's base to trunk_top_m, a pole of pole_radius_m from there to pole_top_m, and cap_count caps, the highest
    reaching at least the pole's top. Nothing finer is checked: the exporter fits them and the walk checks prove them."""
    tol = BOUNDS_TOLERANCE_M
    tx, tz, trunk_radius, pole_radius, trunk_top, pole_top, cap_count = tail[:7]
    if not 1 <= cap_count <= slots:
        return f"has cap_count {cap_count}; it is 1 to the set's {slots} cap slots"
    if trunk_radius <= 0 or pole_radius <= 0:
        return "has a trunk or pole radius that is not positive"
    if not y < trunk_top < pole_top <= high[1] + tol:
        return "needs its base below trunk_top_m, below pole_top_m, inside the bounds"
    if not (low[0] - tol <= tx <= high[0] + tol and low[2] - tol <= tz <= high[2] + tol):
        return "has its trunk outside the room bounds"
    caps = [tail[7 + 8 * k:15 + 8 * k] for k in range(slots)]
    if any(v != 0 for cap in caps[cap_count:] for v in cap):
        return "has an unused cap slot that is not all zero"
    for k, (cx, cz, rim_x, rim_z, rim, top, top_x, top_z) in enumerate(caps[:cap_count]):
        if not (0 < top_x <= rim_x and 0 < top_z <= rim_z):
            return f"has cap {k} with a top wider than its rim or a size that is not positive"
        if not (y < rim < top <= high[1] + tol):
            return f"has cap {k} whose rim is not between the copy's base and the cap's top, inside the bounds"
        if not (low[0] - tol <= cx <= high[0] + tol and low[2] - tol <= cz <= high[2] + tol):
            return f"has cap {k} outside the room bounds"
    if pole_top > max(cap[5] for cap in caps[:cap_count]):
        return "has its pole above its highest cap: a climber comes up through a cap"
    return None


def check_terrain_tiles(room: dict, room_dir: Path, listed: set[str], label: str) -> list[str]:
    """Each tiled part's GLB: tile nodes only, named for their tile and level, every tile at every level, inside its
    square, as many tiles as the manifest says."""
    tiles_block = room["terrain_tiles"]
    problems: list[str] = []
    grids = [level["grid_m"] for level in tiles_block["levels"]]
    if any(b <= a for a, b in zip(grids, grids[1:])):
        problems.append(f"{label}: terrain levels must be finest first, each coarser than the one before")
    parts = {p["id"]: p for p in room["shell"]["parts"]}
    named = [entry["part"] for entry in tiles_block["parts"]]
    for part_id in sorted({p for p in named if named.count(p) > 1}):
        problems.append(f"{label}: terrain part {part_id} is listed twice")
    for entry in tiles_block["parts"]:
        where = f"{label}: terrain part {entry['part']}"
        part = parts.get(entry["part"])
        if part is None:
            problems.append(f"{where} is not a shell part")
            continue
        if part["geometry"]["kind"] != "mesh":
            problems.append(f"{where} is not a mesh part")
            continue
        data = _read_listed(room_dir, listed, part["geometry"]["mesh"])
        gltf = _glb_document(data) if data is not None else None
        if gltf is None:
            continue  # the files check reports a missing or broken mesh
        problems += _check_tile_nodes(gltf, len(grids), entry["tiles"], where)
    return problems


def _check_tile_nodes(gltf: dict, levels: int, tiles: int, where: str) -> list[str]:
    nodes = gltf.get("nodes", [])
    scenes = gltf.get("scenes", [])
    scene = gltf.get("scene", 0)
    roots = scenes[scene].get("nodes", []) if isinstance(scenes, list) and isinstance(scene, int) and 0 <= scene < len(scenes) else []
    if not isinstance(nodes, list) or not nodes or sorted(roots) != list(range(len(nodes))):
        return [f"{where}: every node of a tiled GLB is a root of its scene"]
    found: dict[tuple[int, int], set[int]] = {}
    for index, node in enumerate(nodes):
        name = node.get("name") if isinstance(node, dict) else None
        match = TERRAIN_NODE.match(name) if isinstance(name, str) else None
        if match is None:
            return [f"{where}: node {index} ({name!r}) is not named tile_<tx>_<tz>_lod<level>"]
        tx, tz, level = int(match.group(1)), int(match.group(2)), int(match.group(3))
        if level >= levels:
            return [f"{where}: node {name} names level {level}; the room has {levels}"]
        if any(key in node for key in ("children", "matrix", "translation", "rotation", "scale")) or "mesh" not in node:
            return [f"{where}: node {name} must hold a mesh with no transform and no children"]
        if level in found.setdefault((tx, tz), set()):
            return [f"{where}: node {name} appears twice"]
        found[(tx, tz)].add(level)
        extent = _glb_mesh_extent(gltf, node["mesh"]) if isinstance(node["mesh"], int) else None
        if extent is None:
            return [f"{where}: node {name} has no mesh with POSITION bounds"]
        (x0, _y0, z0), (x1, _y1, z1) = extent
        square = (tx * TERRAIN_TILE_M, tz * TERRAIN_TILE_M)
        if (x0 < square[0] - BOUNDS_TOLERANCE_M or x1 > square[0] + TERRAIN_TILE_M + BOUNDS_TOLERANCE_M
                or z0 < square[1] - BOUNDS_TOLERANCE_M or z1 > square[1] + TERRAIN_TILE_M + BOUNDS_TOLERANCE_M):
            return [f"{where}: node {name} reaches outside its tile's square"]
    for (tx, tz), have in sorted(found.items()):
        if have != set(range(levels)):
            missing = sorted(set(range(levels)) - have)
            return [f"{where}: tile ({tx}, {tz}) has no level {missing[0]}; every tile has every level"]
    if len(found) != tiles:
        return [f"{where}: its GLB holds {len(found)} tiles; the manifest says {tiles}"]
    return []


# ---------- styles ----------

def preset_path(preset_id: str, version: int, styles_dir: Path = STYLES_DIR) -> Path:
    return styles_dir / preset_id / f"v{version}.json"


def check_style_pin(pin: dict, label: str, styles_dir: Path = STYLES_DIR) -> list[str]:
    path = preset_path(pin["preset_id"], pin["preset_version"], styles_dir)
    if not path.is_file():
        return [f"{label}: style {pin['preset_id']} v{pin['preset_version']} not found at {path}"]
    problems = []
    preset = load_strict(path)
    if preset.get("preset_id") != pin["preset_id"] or preset.get("preset_version") != pin["preset_version"]:
        problems.append(f"{label}: {path} does not declare {pin['preset_id']} v{pin['preset_version']}")
    if sha256_file(path) != pin["preset_sha256"]:
        problems.append(f"{label}: style pin hash does not match {path.name}; a pinned preset version must never change")
    return problems


def check_style(preset: dict, label: str, path: Path | None = None) -> list[str]:
    hours = [key["hour"] for key in preset["time_of_day"]["keys"]]
    problems = []
    if hours != sorted(hours) or len(hours) != len(set(hours)):
        problems.append(f"{label}: time_of_day keys must be in strictly increasing hour order")
    if preset["time_of_day"]["enabled"] and not hours:
        problems.append(f"{label}: time_of_day is enabled but has no keys")
    if path is not None and path.parent.parent.name == "styles":
        if path.parent.name != preset["preset_id"] or path.name != f"v{preset['preset_version']}.json":
            problems.append(f"{label}: a preset lives at styles/<preset_id>/v<preset_version>.json")
    return problems


# ---------- island rules ----------

def rules_path(rules_id: str, version: int, rules_dir: Path = RULES_DIR) -> Path:
    return rules_dir / rules_id / f"v{version}.json"


# The mechanics round (M0, 14 October 2026). The Bubbles expertise, priced in a pack's cost block and cast with
# nature.cast, each needs an ability of the pack carried out by its primitive; so do Fire's kinds (decisions 78 to 80).
EXPERTISE_ABILITIES = {"bubble_carry": "particles.float", "bubble_boat": "particles.float", "bubble_lift": "particles.float",
                       "fireline": "element.ignite", "snuff": "element.ignite", "will_o_wisp": "element.ignite"}
# The command contract's bounds on the player's body (game-command.schema.json $defs/data/player.status): 30 hearts of
# 20 health with Big heart at full rank, and ten bands to 4.0 m with Big bar at full rank.
MAX_HEALTH = 720
MAX_STAMINA_M = 4.8
# Knacks (MECHANICS section 7): at most a fifth at full rank, always upside. These numbers fall to be better; every
# other factor rises; the safe drop is in metres.
KNACK_LOWERS = frozenset({"breath", "build_time"})
KNACK_IN_METRES = frozenset({"safe_drop"})
KNACK_MOST = 0.2


def _check_mechanics(pack: dict, label: str) -> list[str]:
    """The mechanics blocks against each other and the command contract: prices name the pack's abilities and halve to
    whole pips, the most health and stamina the pack allows fit player.status's bounds, the mode rows are unique, knacks
    stay within a fifth and upside, names name the pack's knacks, and the Fractal budget is ordered."""
    problems = []
    capabilities = {ability["capability"] for ability in pack["abilities"]}
    primitives = {ability["primitive"] for ability in pack["abilities"]}
    for name, price in pack.get("cost", {}).get("prices", {}).items():
        if name in EXPERTISE_ABILITIES:
            if EXPERTISE_ABILITIES[name] not in primitives:
                kind = "a Fire kind" if EXPERTISE_ABILITIES[name] == "element.ignite" else "a Bubbles expertise"
                problems.append(f"{label}: cost prices {name}, {kind}, but the pack grants no {EXPERTISE_ABILITIES[name]} ability")
        elif name not in capabilities:
            problems.append(f"{label}: cost prices {name!r}, which is no ability of this pack")
        own = price["ordered"] * price["own_idea_factor"]
        if abs(own - round(own)) > 1e-9:
            problems.append(f"{label}: cost of {name}: ordered {price['ordered']} times own_idea_factor {price['own_idea_factor']} is not a whole number of pips")
    table = pack.get("knacks", {}).get("table", [])

    def best(moves: str) -> float:
        return max((rank for knack in table if knack["moves"] == moves for rank in knack["ranks"]), default=1.0)

    traits = pack.get("traits", {})
    hearts = traits.get("hearts")
    if hearts is not None:
        if hearts["start_hearts"] > hearts["cap_hearts"]:
            problems.append(f"{label}: traits.hearts start_hearts {hearts['start_hearts']} is above cap_hearts {hearts['cap_hearts']}")
        most = hearts["cap_hearts"] * 4 * hearts["health_per_quarter"] * best("health_per_quarter")
        if most > MAX_HEALTH + 1e-9:
            problems.append(f"{label}: the most health this pack allows ({most:g}: cap_hearts x 4 x health_per_quarter x the best health_per_quarter knack) is above player.status's {MAX_HEALTH}")
    stamina = traits.get("stamina")
    if stamina is not None:
        most = (stamina["start_m"] + stamina["band_m"] * (stamina["bands"] - 1)) * best("stamina_per_bar")
        if most > MAX_STAMINA_M + 1e-9:
            problems.append(f"{label}: the most stamina this pack allows ({most:g} m: the top band x the best stamina_per_bar knack) is above player.status's {MAX_STAMINA_M} m")
    pips = traits.get("pips")
    if pips is not None:
        if pips["start"] > pips["ceiling"]:
            problems.append(f"{label}: traits.pips start {pips['start']} is above its ceiling {pips['ceiling']}")
        if pips["refill_near_every_s"] > pips["refill_far_every_s"]:
            problems.append(f"{label}: traits.pips refills slower near the player ({pips['refill_near_every_s']} s) than apart ({pips['refill_far_every_s']} s)")
    rows = pack.get("modes", {}).get("rows", [])
    keys = [(row["mode"], row.get("difficulty"), row.get("variant")) for row in rows]
    for key in sorted({k for k in keys if keys.count(k) > 1}, key=str):
        problems.append(f"{label}: modes has the row {'/'.join(part for part in key if part)} more than once")
    if rows and sum(1 for row in rows if row["mode"] == "creative") != 1:
        problems.append(f"{label}: modes needs exactly one creative row")
    if rows and not any(row["mode"] == "challenge" for row in rows):
        problems.append(f"{label}: modes needs a challenge row")
    knacks = [knack["knack"] for knack in table]
    for name in sorted({k for k in knacks if knacks.count(k) > 1}):
        problems.append(f"{label}: knack {name!r} appears more than once")
    slots = [slot["slot"] for slot in pack.get("knacks", {}).get("slots", [])]
    if slots and sorted(slots) != list(range(1, len(slots) + 1)):
        problems.append(f"{label}: knacks.slots number the slots 1 to {len(slots)}, each once")
    for slot in pack.get("knacks", {}).get("slots", []):
        if slot["slot"] == 1 and slot["opens"] != "creation":
            problems.append(f"{label}: knack slot 1 opens at creation")
    for knack in table:
        where = f"{label}: knack {knack['knack']}"
        ranks = knack["ranks"]
        if (knack["unit"] == "m") != (knack["moves"] in KNACK_IN_METRES):
            problems.append(f"{where} moves {knack['moves']}, which is in {'metres' if knack['moves'] in KNACK_IN_METRES else 'a factor'}")
            continue
        if knack["unit"] == "m":
            if any(later <= earlier for earlier, later in zip(ranks, ranks[1:])):
                problems.append(f"{where}: its ranks must rise")
            continue
        lowers = knack["moves"] in KNACK_LOWERS
        for rank in ranks:
            if abs(rank - 1) > KNACK_MOST + 1e-9:
                problems.append(f"{where}: rank {rank:g} moves its number by more than a fifth")
            if (rank >= 1) if lowers else (rank <= 1):
                problems.append(f"{where}: rank {rank:g} is not upside ({knack['moves']} must {'fall' if lowers else 'rise'})")
        steps = [abs(rank - 1) for rank in ranks]
        if any(later <= earlier for earlier, later in zip(steps, steps[1:])):
            problems.append(f"{where}: each rank must move its number further than the one before")
    named = pack.get("names", {}).get("knacks", {})
    for name in sorted(set(named) - set(knacks)):
        problems.append(f"{label}: names.knacks names {name!r}, which is no knack of this pack")
    fractals = pack.get("fractals")
    if fractals is not None:
        if fractals["budget_min"] > fractals["budget_max"]:
            problems.append(f"{label}: fractals budget_min {fractals['budget_min']} is above budget_max {fractals['budget_max']}")
        if fractals.get("reachable_first_run", 0) > fractals["budget_max"]:
            problems.append(f"{label}: fractals reachable_first_run {fractals['reachable_first_run']} is above budget_max {fractals['budget_max']}")
    # P-Inventory (25 October): each kind once, and an asset a loose unit of one kind at most.
    inventory = pack.get("inventory")
    if inventory is not None:
        ids = [kind["id"] for kind in inventory["kinds"]]
        for name in sorted({i for i in ids if ids.count(i) > 1}):
            problems.append(f"{label}: inventory kind {name!r} appears more than once")
        assets = [asset for kind in inventory["kinds"] for asset in kind.get("assets", [])]
        for name in sorted({a for a in assets if assets.count(a) > 1}):
            problems.append(f"{label}: asset {name!r} is a unit of more than one inventory kind")
    return problems


def check_rules(pack: dict, label: str, path: Path | None = None) -> list[str]:
    """What the island rules schema cannot express. The schema holds each primitive's outer limits; this checks
    that a pack is consistent inside them: one ability per capability and per category, each default inside its
    range, and each default radius and duration at most its maximum; and the mechanics blocks against each other
    (_check_mechanics). Text rules ran with the schema."""
    problems = _check_mechanics(pack, label)
    capabilities = [ability["capability"] for ability in pack["abilities"]]
    for name in sorted({c for c in capabilities if capabilities.count(c) > 1}):
        problems.append(f"{label}: capability {name!r} appears more than once; capabilities are unique within a pack")
    categories = [ability["category"] for ability in pack["abilities"]]
    for name in sorted({c for c in categories if categories.count(c) > 1}):
        problems.append(f"{label}: category {name!r} appears more than once; categories are unique within a pack")
    for index, ability in enumerate(pack["abilities"]):
        where = f"{label}: abilities[{index}] ({ability['capability']})"
        for name, bounds in ability["params"].items():
            if bounds["min"] > bounds["max"]:
                problems.append(f"{where}: param {name} has min {bounds['min']} above max {bounds['max']}")
            elif not bounds["min"] <= bounds["default"] <= bounds["max"]:
                problems.append(f"{where}: param {name} default {bounds['default']} is outside its min {bounds['min']} and max {bounds['max']}")
        if ability["area_radius_default_m"] > ability["area_radius_max_m"]:
            problems.append(f"{where}: area_radius_default_m {ability['area_radius_default_m']} is above area_radius_max_m {ability['area_radius_max_m']}")
        if ability["duration_default_s"] > ability["duration_max_s"]:
            problems.append(f"{where}: duration_default_s {ability['duration_default_s']} is above duration_max_s {ability['duration_max_s']}")
    if path is not None and path.parent.parent.name == "rules":
        if path.parent.name != pack["rules_id"] or path.name != f"v{pack['rules_version']}.json":
            problems.append(f"{label}: a rules pack lives at rules/<rules_id>/v<rules_version>.json")
    return problems


# ---------- room state ----------

def check_state(state: dict, label: str, room_dir: Path | None = None, check_style_pins: bool = False, styles_dir: Path = STYLES_DIR) -> list[str]:
    problems = []
    store = state["store_revision"]
    manifest_objects, bounds, shell_ids = None, None, None
    if room_dir is not None:
        manifest = Path(room_dir) / "room.json"
        if not manifest.is_file():
            problems.append(f"{label}: room {room_dir} has no room.json")
        elif sha256_file(manifest) != state["room_pin"]["manifest_sha256"]:
            problems.append(f"{label}: room_pin does not match {manifest}")
        else:
            room = load_strict(manifest)
            if room["room_id"] != state["room_id"]:
                problems.append(f"{label}: room_id does not match the pinned room")
            manifest_objects = {o["id"] for o in room["objects"]}
            shell_ids = {p["id"] for p in room["shell"]["parts"]}
            bounds = room["bounds"]
    for key, entity in state["entities"].items():
        namespace = key.split(":", 1)[0]
        if entity["id"] != key:
            problems.append(f"{label}: entity key {key} does not match its id {entity['id']}")
        if STATE_KINDS.get(namespace) != entity["kind"]:
            problems.append(f"{label}: {key} cannot be saved as kind {entity['kind']}; saved entities are obj: objects, creation: creations and avatar: avatars")
        if entity["revision"] > max(store, 1):
            problems.append(f"{label}: {key} revision is newer than the store revision")
        if entity["provenance"]["created_revision"] > store:
            problems.append(f"{label}: {key} was created after the store revision")
        locked = entity["protection"].get("locked_revision")
        if locked is not None and locked > store:
            problems.append(f"{label}: {key} lock is newer than the store revision")
        if "transform" in entity:
            if not _quat_ok(entity["transform"]["rotation"]):
                problems.append(f"{label}: {key} rotation is not a unit quaternion")
            if bounds is not None and not entity.get("removed") and not _inside(entity["transform"]["position_m"], bounds):
                problems.append(f"{label}: {key} is outside the room bounds")
        if manifest_objects is not None and namespace == "obj" and key not in manifest_objects:
            problems.append(f"{label}: {key} is not an object in the pinned room")
    for key, receipt in state["receipts"].items():
        principal, action_id = key.split("|", 1)
        result = receipt["result"]
        if result["principal"] != principal or result.get("action_id") != action_id:
            problems.append(f"{label}: receipt {key} does not match its result's principal and action_id")
        if result["revision"] > store:
            problems.append(f"{label}: receipt {key} is newer than the store revision")
        if result["room_id"] != state["room_id"]:
            problems.append(f"{label}: receipt {key} belongs to another room")
    checkpoint_ids = [c["id"] for c in state.get("checkpoints", [])]
    if len(checkpoint_ids) != len(set(checkpoint_ids)):
        problems.append(f"{label}: checkpoint ids must be unique")
    for checkpoint in state.get("checkpoints", []):
        if checkpoint["revision"] > store:
            problems.append(f"{label}: checkpoint {checkpoint['id']} is newer than the store revision")
    # P-Inventory (25 October): a thing in the island's chest is away from the island, so room state keeps it as removed.
    chest = state.get("extensions", {}).get("x_play_camp", {}).get("chest", {})
    for thing in chest.get("things", []):
        if not state["entities"].get(thing, {}).get("removed"):
            problems.append(f"{label}: {thing} is in the chest, but room state does not keep it as removed")
        elif manifest_objects is not None and thing not in manifest_objects:
            problems.append(f"{label}: {thing} in the chest is not an object in the pinned room")
    if "journal" in state:
        problems += _check_journal(state["journal"], store, label)
    if "discovered" in state:
        problems += _check_discovered(state["discovered"], store, label, manifest_objects, shell_ids, bounds)
    if check_style_pins:
        problems += check_style_pin(state["style_pin"], label, styles_dir)
    return problems


def _check_journal(journal: dict, store: int, label: str) -> list[str]:
    """Saved journal entries: one id each, none newer than the store, each list oldest first by revision (the room
    revision is monotonic; a wall clock may step back)."""
    problems, seen = [], set()
    for part in ("open_tasks", "history", "notes"):
        previous = -1
        for entry in journal[part]:
            if entry["entry_id"] in seen:
                problems.append(f"{label}: journal entry {entry['entry_id']} appears twice")
            seen.add(entry["entry_id"])
            if entry["revision"] > store:
                problems.append(f"{label}: journal entry {entry['entry_id']} is newer than the store revision")
            if entry["revision"] < previous:
                problems.append(f"{label}: journal {part} must be oldest first; {entry['entry_id']} is out of order")
            previous = max(previous, entry["revision"])
    return problems


def _check_discovered(discovered: dict, store: int, label: str, manifest_objects: set | None,
                      shell_ids: set | None, bounds: dict | None) -> list[str]:
    problems = []
    for key, item in discovered["entities"].items():
        entity = item["entity"]
        if entity["id"] != key:
            problems.append(f"{label}: discovered {key} does not match its summary's id {entity['id']}")
        if STATE_KINDS.get(key.split(":", 1)[0]) != entity["kind"]:
            problems.append(f"{label}: discovered {key} cannot be of kind {entity['kind']}")
        if item["last_seen_revision"] > store:
            problems.append(f"{label}: discovered {key} was seen after the store revision")
        if manifest_objects is not None and key.startswith("obj:") and key not in manifest_objects:
            problems.append(f"{label}: discovered {key} is not an object in the pinned room")
    cell = discovered["cell_m"]
    levels = set()
    for index, level in enumerate(discovered["levels"]):
        where = f"{label}: discovered level {index} ({level['support']})"
        identity = (level["support"], level.get("surface"))
        if identity in levels:
            problems.append(f"{where} is listed twice")
        levels.add(identity)
        support = level["support"]
        if support.startswith("shell:") and shell_ids is not None and support not in shell_ids:
            problems.append(f"{where} rests on a shell part the pinned room does not have")
        if support.startswith("obj:") and manifest_objects is not None and support not in manifest_objects:
            problems.append(f"{where} rests on an object the pinned room does not have")
        try:
            cells = base64.b64decode(level["cells"], validate=True)
        except (binascii.Error, ValueError):
            problems.append(f"{where}: cells is not standard base64")
            continue
        count = level["columns"] * level["rows"]
        if len(cells) != (count + 7) // 8:
            problems.append(f"{where}: cells holds {len(cells)} bytes; {level['columns']} x {level['rows']} cells need {(count + 7) // 8}")
        elif count % 8 and cells[-1] >> (count % 8):
            problems.append(f"{where}: the unused bits after the last cell must be 0")
        if bounds is not None:
            low = [level["min_xz_m"][0], level["min_xz_m"][1]]
            high = [low[0] + level["columns"] * cell, low[1] + level["rows"] * cell]
            for axis, i, j in (("x", 0, 0), ("z", 2, 1)):
                if low[j] < bounds["min_m"][i] - cell or high[j] > bounds["max_m"][i] + cell:
                    problems.append(f"{where}: its grid reaches beyond the room bounds along {axis}")
    return problems


def check_team_document(document: dict, label: str) -> list[str]:
    """What the team schema cannot express: knack slots open and unique, the body inside its own maxima, and Fractals in
    hand no more than were found."""
    problems = []
    if is_schema(document["schema"], "team"):
        knacks = document["knacks"]
        slots = [entry["slot"] for entry in knacks["chosen"]]
        if len(slots) != len(set(slots)):
            problems.append(f"{label}: knacks.chosen fills a slot twice")
        if any(slot > knacks["slots_open"] for slot in slots):
            problems.append(f"{label}: knacks.chosen fills a slot past slots_open {knacks['slots_open']}")
        names = [entry["knack"] for entry in knacks["chosen"]]
        if len(names) != len(set(names)):
            problems.append(f"{label}: knacks.chosen picks one knack twice")
        # M3's fix round (15 October): a rank set aside is for a knack in no slot.
        for name in sorted(set(knacks.get("earned", {})) & set(names)):
            problems.append(f"{label}: knacks.earned keeps a rank for {name}, which is in a slot")
        # P-Inventory (25 October): a mode's pockets hold at most 16 entries, and a thing is in them once.
        for mode, pockets in document.get("pockets", {}).items():
            things = [(thing["room_id"], thing["manifest_prefix"], thing["id"]) for thing in pockets.get("things", [])]
            if len(pockets.get("stacks", {})) + len(things) > 16:
                problems.append(f"{label}: the {mode} pockets hold more than 16 entries")
            if len(things) != len(set(things)):
                problems.append(f"{label}: the {mode} pockets hold one thing twice")
    elif is_schema(document["schema"], "player_modes"):
        vitals = document["vitals"]
        if vitals["health"] > vitals["max_health"]:
            problems.append(f"{label}: health {vitals['health']} is above max_health {vitals['max_health']}")
        if vitals["stamina"] > vitals["max_stamina"] + 1e-6:
            problems.append(f"{label}: stamina {vitals['stamina']} is above max_stamina {vitals['max_stamina']}")
        fractals = vitals["fractals"]
        if fractals["unspent"] > fractals["found"]:
            problems.append(f"{label}: {fractals['unspent']} Fractals in hand but only {fractals['found']} found")
        if "typed" in fractals and fractals["typed"]["heart"] + fractals["typed"]["band"] != fractals["unspent"]:
            problems.append(f"{label}: typed Fractals in hand ({fractals['typed']['heart']} heart, {fractals['typed']['band']} band) are not the {fractals['unspent']} unspent")
    return problems


def check_message(document: dict, label: str) -> list[str]:
    """Rules a schema cannot express for results: the journal newest first with one id per entry, and map.find
    answers nearest first."""
    if not is_schema(document.get("schema"), "result") or document.get("ok") is not True:
        return []
    problems = []
    data = document.get("data") or {}
    if document["op"] == "journal.read":
        seen = set()
        for part in ("open_tasks", "entries"):
            previous = None
            for entry in data[part]:
                if entry["entry_id"] in seen:
                    problems.append(f"{label}: journal entry {entry['entry_id']} appears twice")
                seen.add(entry["entry_id"])
                if previous is not None and entry["revision"] > previous:
                    problems.append(f"{label}: journal {part} must be newest first; {entry['entry_id']} is out of order")
                previous = entry["revision"]
    elif document["op"] == "map.find":
        distances = [item["distance_m"] for item in data["items"]]
        if distances != sorted(distances):
            problems.append(f"{label}: map.find items must be nearest first")
        ids = [item["entity"]["id"] for item in data["items"]]
        if len(ids) != len(set(ids)):
            problems.append(f"{label}: map.find lists an entity twice")
    return problems


_EVENT_VALIDATOR: Draft202012Validator | None = None


def _event_validator():
    """One line of events.jsonl is the record schema's $defs/event, not a document with a schema field of its own."""
    global _REGISTRY, _EVENT_VALIDATOR
    if _EVENT_VALIDATOR is None:
        if _REGISTRY is None:
            _REGISTRY = _registry()
        root = load_strict(CONTRACTS_DIR / SCHEMA_FILES[PLAYTEST_MANIFEST])["$id"]
        _EVENT_VALIDATOR = StrictValidator({"$ref": f"{root}#/$defs/event"}, registry=_REGISTRY)
    return _EVENT_VALIDATOR


def _forbidden_keys(value, path: str = "$") -> list[str]:
    """The keys a record never carries, at any depth: a summary cannot smuggle a picture under a nested name."""
    problems = []
    if isinstance(value, dict):
        for key, item in value.items():
            if key in PLAYTEST_FORBIDDEN_KEYS:
                problems.append(f"{path}.{key}: a record never carries '{key}'")
            problems += _forbidden_keys(item, f"{path}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            problems += _forbidden_keys(item, f"{path}[{index}]")
    return problems


def event_errors(event) -> list[str]:
    """One event of a playtest record: the envelope and its kind's fields, the text rules, and the keys never carried."""
    if not isinstance(event, dict):
        return ["$: an event must be an object"]
    messages = _schema_messages(_event_validator(), event)
    hidden = _text_problems(event)
    if hidden:
        return messages + hidden
    return messages + _forbidden_keys(event)


def _utc_key(text: str) -> tuple:
    """A utc_timestamp as a tuple that orders correctly whether or not a fraction is written."""
    match = PLAYTEST_UTC.match(text)
    parts = tuple(int(group) for group in match.groups()[:6])
    return parts + (int((match.group(7) or "0").ljust(6, "0")),)


def check_playtest(record_dir: Path, manifest_name: str = "manifest.json") -> list[str]:
    """A play's record: the manifest, then events.jsonl line by line (a line that is not strict JSON, a truncated last line,
    a line past its limit), then what the schema cannot say: the folder is named for the record, t and utc never run
    backwards, at most one ready and the quit last, the stills numbered in order and named for their reason, a mark's still
    the one it took, no more stills than the cap, and at a clean quit the manifest's counts, the quit line's totals and the
    file agreeing. The stills themselves are never read: a missing one is the reader's finding, not the contract's."""
    record_dir = Path(record_dir)
    label = f"{record_dir.name}/{manifest_name}"
    try:
        manifest = load_strict(record_dir / manifest_name)
    except (OSError, ContractError) as error:
        return [f"{label}: {error}"]
    problems = [f"{label} {message}" for message in schema_errors(manifest, PLAYTEST_MANIFEST)]
    if problems:
        return problems
    if len(canonical_bytes(manifest)) > PLAYTEST_MANIFEST_LIMIT:
        problems.append(f"{label}: the manifest is {len(canonical_bytes(manifest))} bytes of canonical JSON; the limit is {PLAYTEST_MANIFEST_LIMIT}")
    if manifest["record_id"] != record_dir.name:
        problems.append(f"{label}: record_id {manifest['record_id']!r} must match its directory name {record_dir.name!r}")
    if not manifest["record_id"].endswith("-" + manifest["room_id"]):
        problems.append(f"{label}: record_id {manifest['record_id']!r} does not end with its room_id {manifest['room_id']!r}")

    events_label = f"{record_dir.name}/events.jsonl"
    try:
        data = (record_dir / "events.jsonl").read_bytes()
    except OSError as error:
        return problems + [f"{events_label}: {error}"]
    if data.startswith(b"\xef\xbb\xbf"):
        problems.append(f"{events_label}: starts with a byte-order mark")
        data = data[3:]
    truncated = bool(data) and not data.endswith(b"\n")
    lines = data.split(b"\n")
    if lines and lines[-1] == b"":
        lines.pop()
    events: list[tuple[int, dict]] = []
    for number, raw in enumerate(lines, 1):
        where = f"{events_label} line {number}"
        last = number == len(lines)
        if raw.endswith(b"\r"):
            problems.append(f"{where}: ends in CR LF; a line ends in LF alone")
            raw = raw[:-1]
        if not raw.strip():
            problems.append(f"{where}: blank")
            continue
        if len(raw) > PLAYTEST_EVENT_LINE_LIMIT:
            problems.append(f"{where}: {len(raw)} bytes; the limit is {PLAYTEST_EVENT_LINE_LIMIT}")
        try:
            event = loads_strict(raw.decode("utf-8"))
        except UnicodeDecodeError:
            problems.append(f"{where}: not UTF-8" + (" (a truncated last line?)" if last and truncated else ""))
            continue
        except ContractError as error:
            problems.append(f"{where}: {error}" + (" (a truncated last line?)" if last and truncated else ""))
            continue
        errors = event_errors(event)
        if errors:
            problems += [f"{where} {message}" for message in errors]
            continue
        events.append((number, event))
    if truncated:
        problems.append(f"{events_label}: no newline after the last line (a truncated last line?)")

    started = _utc_key(manifest["started_utc"])
    previous_t = previous_utc = None
    stills: dict[str, tuple[str, str | None]] = {}
    last_still_number = None
    marks = {"off": 0, "keep": 0}
    mark_events = ready_line = quit_line = 0
    for number, event in events:
        where = f"{events_label} line {number}"
        if quit_line:
            problems.append(f"{where}: after the quit on line {quit_line}; quit is the last line")
            break
        utc = _utc_key(event["utc"])
        if previous_t is not None and event["t"] < previous_t:
            problems.append(f"{where}: t {event['t']} is before the line before ({previous_t})")
        if utc < started:
            problems.append(f"{where}: utc {event['utc']} is before started_utc {manifest['started_utc']}")
        elif previous_utc is not None and utc < previous_utc:
            problems.append(f"{where}: utc {event['utc']} is before the line before")
        previous_t, previous_utc = event["t"], utc
        kind = event["kind"]
        if kind == "ready":
            if ready_line:
                problems.append(f"{where}: a second ready (the first is line {ready_line})")
            ready_line = ready_line or number
        elif kind == "still":
            still_number, why, mark_number = PLAYTEST_STILL_FILE.match(event["file"]).groups()
            if why != event["why"]:
                problems.append(f"{where}: still {event['file']} is named for {why!r} but why is {event['why']!r}")
            if event["file"] in stills:
                problems.append(f"{where}: still {event['file']} was already taken")
            if last_still_number is not None and int(still_number) <= last_still_number:
                problems.append(f"{where}: still {event['file']} is not numbered after the still before ({last_still_number:04d})")
            last_still_number = int(still_number)
            stills[event["file"]] = (why, mark_number)
            if len(stills) == PLAYTEST_STILLS_LIMIT + 1:
                problems.append(f"{where}: more than {PLAYTEST_STILLS_LIMIT} stills in one play")
        elif kind == "mark":
            mark_events += 1
            flag, expected = event["flag"], marks[event["flag"]] + 1
            if event["number"] != expected:
                problems.append(f"{where}: {flag} mark {event['number']} out of order; the next {flag} mark is {expected}")
            marks[flag] = max(marks[flag], event["number"])
            want = "mark" if flag == "off" else "keep"
            if event["still"] not in stills:
                problems.append(f"{where}: the mark's still {event['still']} was never taken (no still event names it)")
            elif stills[event["still"]] != (want, str(event["number"])):
                problems.append(f"{where}: the mark's still {event['still']} is not the {want} still numbered {event['number']}")
        elif kind == "quit":
            quit_line = number

    counted = {"events": len(lines), "stills": len(stills), "marks": mark_events}
    if "ended_utc" in manifest:
        ended = _utc_key(manifest["ended_utc"])
        if ended < started:
            problems.append(f"{label}: ended_utc {manifest['ended_utc']} is before started_utc {manifest['started_utc']}")
        elif previous_utc is not None and ended < previous_utc:
            problems.append(f"{label}: ended_utc {manifest['ended_utc']} is before the last event")
        if not quit_line:
            problems.append(f"{label}: ended_utc is set but events.jsonl has no quit line")
        elif quit_line == len(lines):
            quit = events[-1][1]
            for name, count in counted.items():
                if manifest["counts"][name] != count:
                    problems.append(f"{label}: counts.{name} is {manifest['counts'][name]} but the events file holds {count}")
                if quit[name] != manifest["counts"][name]:
                    problems.append(f"{events_label} line {quit_line}: the quit's {name} {quit[name]} disagrees with the manifest's counts.{name} {manifest['counts'][name]}")
            if quit["played_s"] != manifest["played_s"]:
                problems.append(f"{events_label} line {quit_line}: the quit's played_s {quit['played_s']} disagrees with the manifest's {manifest['played_s']}")
            if manifest["played_s"] < quit["t"]:
                problems.append(f"{label}: played_s {manifest['played_s']} is before the quit's t {quit['t']}")
    elif quit_line:
        problems.append(f"{events_label} line {quit_line}: a quit line, but the manifest has no ended_utc (a clean quit rewrites the manifest)")
    return problems


def check_path(path: Path, check_style_pins: bool = False) -> list[str]:
    """A directory is a room (room.json) or a playtest record (manifest.json); a file is validated as what its schema says."""
    path = Path(path)
    if not path.is_dir():
        return check_document(path, check_style_pins)
    if (path / "manifest.json").is_file() and not (path / "room.json").is_file():
        return check_playtest(path)
    return check_room(path, check_style_pins)


def check_document(path: Path, check_style_pins: bool = False) -> list[str]:
    """Schema plus per-kind semantic checks for one file, validated as that file."""
    path = Path(path)
    label = str(path)
    try:
        document = load_strict(path)
    except (OSError, ContractError) as error:
        return [f"{label}: {error}"]
    problems = [f"{label} {message}" for message in schema_errors(document)]
    if problems:
        return problems
    kind = kind_of(document["schema"])
    if kind == "room":
        return check_room(path.parent, check_style_pins, manifest_name=path.name)
    if kind == "asset":
        return check_asset(document, path.parent, label)
    if kind == "style":
        return check_style(document, label, path)
    if kind == "island_rules":
        return check_rules(document, label, path)
    if kind == "room_state":
        return check_state(document, label, check_style_pins=check_style_pins)
    if kind in TEAM_DOCUMENTS:
        return check_team_document(document, label)
    if kind == "playtest_manifest":
        return check_playtest(path.parent, manifest_name=path.name)
    return check_message(document, label)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="*", type=Path)
    parser.add_argument("--room", type=Path, help="room directory (with --state: the room the state must pin)")
    parser.add_argument("--state", type=Path, help="room state file to validate against --room")
    parser.add_argument("--check-style-pins", action="store_true", help="also verify style pins against game/styles/<id>/v<N>.json")
    parser.add_argument("--pin", type=Path, help="print the SHA-256 pin of a file and exit")
    args = parser.parse_args(argv)
    if args.pin:
        print(sha256_file(args.pin))
        return 0
    problems: list[str] = []
    checked = 0
    if args.state:
        checked += 1
        try:
            state = load_strict(args.state)
            errors = [f"{args.state} {m}" for m in schema_errors(state, "room_state")]
            problems += errors or check_state(state, str(args.state), args.room, args.check_style_pins)
        except (OSError, ContractError) as error:
            problems.append(f"{args.state}: {error}")
    elif args.room:
        checked += 1
        problems += check_room(args.room, args.check_style_pins)
    for path in args.paths:
        checked += 1
        problems += check_path(path, args.check_style_pins)
    if not checked:
        parser.print_usage()
        return 2
    for problem in problems:
        print(f"FAIL {problem}")
    print(f"{'OK' if not problems else 'FAILED'}: {checked} item(s) checked, {len(problems)} problem(s)")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
