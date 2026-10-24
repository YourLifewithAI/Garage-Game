"""A reviewer's corrections to the automatic inventory, kept as data next to the capture.

The detector proposes and a reviewer (a person, or a vision-capable AI looking at the evidence
sheets) disposes. Corrections name a *place*, never an id, because ids come from the order the
detector's clusters happen to come in and change when anything upstream does:

- ``drop``: a proposal near a point is not an object (a pattern on a wall, a pile of clutter);
- ``relabel``: the proposal near a point is another kind of thing;
- ``add``: an object the detector did not find, from a kind and a point on it, which the photos then follow.

``captures/<room>/inventory-curation.json``; read strictly, local only.
"""

from __future__ import annotations

import json
import math
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np

from .. import ids
from .vocabulary import BY_NAME

CURATION_FILE = "inventory-curation.json"


class CurationError(ValueError):
    """The curation file has a mistake; the message names it."""


@dataclass(frozen=True)
class Near:
    at_m: tuple[float, float, float]
    radius_m: float
    kind: str | None = None  # only proposals of this kind

    def matches(self, kind: str, centre: np.ndarray) -> bool:
        if self.kind is not None and self.kind != kind:
            return False
        return float(np.linalg.norm(np.asarray(centre) - np.asarray(self.at_m))) <= self.radius_m


@dataclass(frozen=True)
class Relabel:
    near: Near
    kind: str


@dataclass(frozen=True)
class Addition:
    kind: str
    at_m: tuple[float, float, float]
    size_hint_m: float
    note: str = ""


@dataclass(frozen=True)
class Override:
    """The reviewer's own measurement of an object's box, replacing parts of the fitted one."""
    near: Near
    size_m: tuple[float, float, float] | None = None
    centre_xz_m: tuple[float, float] | None = None
    yaw_deg: float | None = None
    base_y: float | None = None
    note: str = ""


@dataclass
class Curation:
    drop: list[Near] = field(default_factory=list)
    relabel: list[Relabel] = field(default_factory=list)
    add: list[Addition] = field(default_factory=list)
    override: list[Override] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    def override_for(self, kind: str, centre: np.ndarray) -> Override | None:
        return next((o for o in self.override if o.near.matches(kind, centre)), None)

    def dropped(self, kind: str, centre: np.ndarray) -> bool:
        return any(n.matches(kind, centre) for n in self.drop)

    def new_kind(self, kind: str, centre: np.ndarray) -> str:
        for r in self.relabel:
            if r.near.matches(kind, centre):
                return r.kind
        return kind


def _point(value: Any, where: str) -> tuple[float, float, float]:
    if not isinstance(value, list) or len(value) != 3 or any(type(v) not in (int, float) or not math.isfinite(v) or abs(v) > 100 for v in value):
        raise CurationError(f"{where} must be [x, y, z] in metres")
    return (float(value[0]), float(value[1]), float(value[2]))


def _keys(value: Any, allowed: set[str], required: set[str], where: str) -> None:
    if not isinstance(value, dict):
        raise CurationError(f"{where} must be an object")
    if set(value) - allowed:
        raise CurationError(f"{where}: unknown keys {', '.join(sorted(set(value) - allowed))}")
    if required - set(value):
        raise CurationError(f"{where}: missing {', '.join(sorted(required - set(value)))}")


def _kind(value: Any, where: str) -> str:
    if value not in BY_NAME:
        raise CurationError(f"{where}: {value!r} is not a kind in the vocabulary")
    return value


def _near(value: Any, where: str) -> Near:
    _keys(value, {"at_m", "radius_m", "kind"}, {"at_m", "radius_m"}, where)
    radius = value["radius_m"]
    if type(radius) not in (int, float) or not 0 < radius <= 5:
        raise CurationError(f"{where}.radius_m must be between 0 and 5")
    return Near(_point(value["at_m"], f"{where}.at_m"), float(radius), _kind(value["kind"], f"{where}.kind") if "kind" in value else None)


def parse_curation(document: dict[str, Any]) -> Curation:
    _keys(document, {"schema", "version", "room", "drop", "relabel", "add", "override", "notes"}, {"schema", "version"}, "curation")
    if not ids.is_schema(document["schema"], "inventory_curation") or document["version"] != 1:
        raise CurationError(f"not a {ids.id_of('inventory_curation')} version 1 document")
    c = Curation()
    for i, v in enumerate(document.get("drop", [])):
        c.drop.append(_near(v, f"drop[{i}]"))
    for i, v in enumerate(document.get("relabel", [])):
        _keys(v, {"near", "kind"}, {"near", "kind"}, f"relabel[{i}]")
        c.relabel.append(Relabel(_near(v["near"], f"relabel[{i}].near"), _kind(v["kind"], f"relabel[{i}].kind")))
    for i, v in enumerate(document.get("add", [])):
        _keys(v, {"kind", "at_m", "size_hint_m", "note"}, {"kind", "at_m", "size_hint_m"}, f"add[{i}]")
        hint = v["size_hint_m"]
        if type(hint) not in (int, float) or not 0.03 <= hint <= 4:
            raise CurationError(f"add[{i}].size_hint_m must be between 0.03 and 4")
        c.add.append(Addition(_kind(v["kind"], f"add[{i}].kind"), _point(v["at_m"], f"add[{i}].at_m"), float(hint), str(v.get("note", ""))))
    for i, v in enumerate(document.get("override", [])):
        where = f"override[{i}]"
        _keys(v, {"near", "size_m", "centre_xz_m", "yaw_deg", "base_y", "note"}, {"near"}, where)
        size = None
        if "size_m" in v:
            s = v["size_m"]
            if not isinstance(s, list) or len(s) != 3 or any(type(x) not in (int, float) or not 0.01 <= x <= 10 for x in s):
                raise CurationError(f"{where}.size_m must be [width, height, depth] between 0.01 and 10 metres")
            size = (float(s[0]), float(s[1]), float(s[2]))
        centre = None
        if "centre_xz_m" in v:
            cxz = v["centre_xz_m"]
            if not isinstance(cxz, list) or len(cxz) != 2 or any(type(x) not in (int, float) or abs(x) > 100 for x in cxz):
                raise CurationError(f"{where}.centre_xz_m must be [x, z]")
            centre = (float(cxz[0]), float(cxz[1]))
        yaw = v.get("yaw_deg")
        if yaw is not None and (type(yaw) not in (int, float) or not -180 <= yaw <= 180):
            raise CurationError(f"{where}.yaw_deg must be between -180 and 180")
        base = v.get("base_y")
        if base is not None and (type(base) not in (int, float) or not 0 <= base <= 10):
            raise CurationError(f"{where}.base_y must be between 0 and 10")
        c.override.append(Override(_near(v["near"], f"{where}.near"), size, centre, None if yaw is None else float(yaw),
                                   None if base is None else float(base), str(v.get("note", ""))))
    c.notes = [str(n) for n in document.get("notes", [])]
    return c


def load_curation(path: Path) -> Curation:
    path = Path(path)
    if not path.is_file():
        return Curation()
    try:
        return parse_curation(json.loads(path.read_text(encoding="utf-8")))
    except json.JSONDecodeError as error:
        raise CurationError(f"{path} is not valid JSON: {error}") from error


SLUG = re.compile(r"[^a-z0-9]+")


def slug(text: str) -> str:
    return SLUG.sub("_", text.lower()).strip("_")
