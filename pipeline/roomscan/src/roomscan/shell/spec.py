"""What a person (or a player's AI) decides about a room's shell after looking at the evidence.

The planes come from the photos; which gap in a wall is a door, a window or the garage door, how
big it is, which way the room faces and where the avatars start come from a reviewer who has looked
at the rectified wall pictures. Those decisions live in ``captures/<room>/shell-spec.json`` (local:
it describes a real place), read strictly: an unknown key or an impossible number stops the export
with a message that says what to fix.

``u_m`` is the opening's centre along its wall, measured from the wall's left end as someone inside
the room sees it (wall A from x_min, B from z_min, C from x_max, D from z_max), and ``v_bottom_m``
the height of its lower edge.
"""

from __future__ import annotations

import json
import math
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .. import ids
from ..jsonio import sha256_hex

SPEC_FILE = "shell-spec.json"
OPENING_KINDS = ("door", "window", "garage_door", "archway", "vent", "pet_door")
OPENING_STATES = ("open", "closed", "fixed")
TOKEN = re.compile(r"^[a-z][a-z0-9_-]{0,63}$")
HEX = re.compile(r"^#[0-9a-f]{6}$")


class SpecError(ValueError):
    """The shell spec has a mistake; the message names it."""


@dataclass(frozen=True)
class OpeningSpec:
    id: str
    kind: str
    wall: str
    u_m: float
    width_m: float
    v_bottom_m: float
    height_m: float
    state: str
    traversable: bool
    note: str = ""


@dataclass(frozen=True)
class LampSpec:
    id: str
    position_m: tuple[float, float, float]
    color: str = "#ffe7bf"
    relative_intensity: float = 0.7


@dataclass(frozen=True)
class SpawnSpec:
    id: str
    role: str
    position_m: tuple[float, float, float]
    yaw_deg: float


@dataclass
class ShellSpec:
    room: str
    session_id: str
    display_name: str = ""
    description: str = ""
    site: dict[str, Any] | None = None
    openings: list[OpeningSpec] = field(default_factory=list)
    lamps: list[LampSpec] = field(default_factory=list)
    spawns: list[SpawnSpec] = field(default_factory=list)
    surface_colours: dict[str, str] = field(default_factory=dict)  # "floor", "ceiling", "A".."D" -> #rrggbb overrides
    thickness_m: float = 0.12
    notes: list[str] = field(default_factory=list)

    def fingerprint(self) -> str:
        return sha256_hex(json.dumps(self.__dict__, default=lambda o: o.__dict__, sort_keys=True).encode("utf-8"))


def _check_keys(value: dict[str, Any], allowed: set[str], required: set[str], where: str) -> None:
    if not isinstance(value, dict):
        raise SpecError(f"{where} must be an object")
    extra = set(value) - allowed
    if extra:
        raise SpecError(f"{where}: unknown keys {', '.join(sorted(extra))}")
    missing = required - set(value)
    if missing:
        raise SpecError(f"{where}: missing {', '.join(sorted(missing))}")


def _num(value: Any, where: str, low: float, high: float) -> float:
    if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
        raise SpecError(f"{where} must be a number between {low} and {high}")
    return float(value)


def _vec3(value: Any, where: str) -> tuple[float, float, float]:
    if not isinstance(value, list) or len(value) != 3:
        raise SpecError(f"{where} must be [x, y, z]")
    return tuple(_num(v, f"{where}[{i}]", -100, 100) for i, v in enumerate(value))  # type: ignore[return-value]


def parse_spec(document: dict[str, Any]) -> ShellSpec:
    _check_keys(document, {"schema", "version", "room", "session_id", "display_name", "description", "site", "openings",
                           "lamps", "spawns", "surface_colours", "thickness_m", "notes"},
                {"schema", "version", "room", "session_id"}, "shell spec")
    if not ids.is_schema(document["schema"], "shell_spec") or document["version"] != 1:
        raise SpecError(f"not a {ids.id_of('shell_spec')} version 1 document")
    spec = ShellSpec(room=str(document["room"]), session_id=str(document["session_id"]),
                     display_name=str(document.get("display_name", "")), description=str(document.get("description", "")))
    if not TOKEN.match(spec.room):
        raise SpecError("room must be a lowercase token")
    if "site" in document and document["site"] is not None:
        site = document["site"]
        _check_keys(site, {"latitude_deg", "neg_z_bearing_deg", "solar_noon_h"},
                    {"latitude_deg", "neg_z_bearing_deg", "solar_noon_h"}, "site")
        lat, bearing, noon = site["latitude_deg"], site["neg_z_bearing_deg"], site["solar_noon_h"]
        if type(lat) is not int or not -66 <= lat <= 66:
            raise SpecError("site.latitude_deg must be a whole number of degrees between -66 and 66")
        if type(bearing) is not int or not 0 <= bearing <= 359:
            raise SpecError("site.neg_z_bearing_deg must be a whole number of degrees from 0 to 359")
        if type(noon) not in (int, float) or not 10 <= noon <= 14 or (noon * 4) != int(noon * 4):
            raise SpecError("site.solar_noon_h must be between 10 and 14 on a quarter hour")
        spec.site = {"latitude_deg": lat, "neg_z_bearing_deg": bearing, "solar_noon_h": noon}
    spec.thickness_m = _num(document.get("thickness_m", 0.12), "thickness_m", 0.02, 0.5)
    seen: set[str] = set()
    for i, o in enumerate(document.get("openings", [])):
        where = f"openings[{i}]"
        _check_keys(o, {"id", "kind", "wall", "u_m", "width_m", "v_bottom_m", "height_m", "state", "traversable", "note"},
                    {"id", "kind", "wall", "u_m", "width_m", "v_bottom_m", "height_m", "state", "traversable"}, where)
        if not isinstance(o["id"], str) or not TOKEN.match(o["id"]) or o["id"] in seen:
            raise SpecError(f"{where}.id must be a unique lowercase token")
        seen.add(o["id"])
        if o["kind"] not in OPENING_KINDS:
            raise SpecError(f"{where}.kind must be one of {', '.join(OPENING_KINDS)}")
        if o["wall"] not in ("A", "B", "C", "D"):
            raise SpecError(f"{where}.wall must be A, B, C or D")
        if o["state"] not in OPENING_STATES:
            raise SpecError(f"{where}.state must be one of {', '.join(OPENING_STATES)}")
        if type(o["traversable"]) is not bool:
            raise SpecError(f"{where}.traversable must be true or false")
        spec.openings.append(OpeningSpec(
            o["id"], o["kind"], o["wall"], _num(o["u_m"], f"{where}.u_m", 0, 30), _num(o["width_m"], f"{where}.width_m", 0.05, 20),
            _num(o["v_bottom_m"], f"{where}.v_bottom_m", 0, 20), _num(o["height_m"], f"{where}.height_m", 0.05, 20),
            o["state"], o["traversable"], str(o.get("note", ""))))
    seen_lamps: set[str] = set()
    for i, lamp in enumerate(document.get("lamps", [])):
        where = f"lamps[{i}]"
        _check_keys(lamp, {"id", "position_m", "color", "relative_intensity"}, {"id", "position_m"}, where)
        if not isinstance(lamp["id"], str) or not TOKEN.match(lamp["id"]) or lamp["id"] in seen_lamps:
            raise SpecError(f"{where}.id must be a unique lowercase token")
        seen_lamps.add(lamp["id"])
        colour = str(lamp.get("color", "#ffe7bf")).lower()
        if not HEX.match(colour):
            raise SpecError(f"{where}.color must be #rrggbb")
        spec.lamps.append(LampSpec(lamp["id"], _vec3(lamp["position_m"], f"{where}.position_m"), colour,
                                   _num(lamp.get("relative_intensity", 0.7), f"{where}.relative_intensity", 0, 1)))
    for i, sp in enumerate(document.get("spawns", [])):
        where = f"spawns[{i}]"
        _check_keys(sp, {"id", "role", "position_m", "yaw_deg"}, {"id", "role", "position_m", "yaw_deg"}, where)
        if sp["role"] not in ("player", "companion", "any") or not TOKEN.match(str(sp["id"])):
            raise SpecError(f"{where} needs a token id and role player, companion or any")
        spec.spawns.append(SpawnSpec(sp["id"], sp["role"], _vec3(sp["position_m"], f"{where}.position_m"),
                                     _num(sp["yaw_deg"], f"{where}.yaw_deg", -180, 180)))
    for key, colour in document.get("surface_colours", {}).items():
        if key not in ("floor", "ceiling", "A", "B", "C", "D") or not HEX.match(str(colour).lower()):
            raise SpecError("surface_colours keys are floor, ceiling, A, B, C, D and values #rrggbb")
        spec.surface_colours[key] = str(colour).lower()
    spec.notes = [str(n) for n in document.get("notes", [])]
    return spec


def load_spec(path: Path) -> ShellSpec:
    try:
        text = Path(path).read_text(encoding="utf-8")
    except OSError as error:
        raise SpecError(f"cannot read {path}: {error}") from error
    try:
        document = json.loads(text)
    except json.JSONDecodeError as error:
        raise SpecError(f"{path} is not valid JSON: {error}") from error
    return parse_spec(document)
