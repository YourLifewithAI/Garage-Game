"""The room manifest (``room.json``) for a scanned room's shell, and where it is written.

Shell parts are single planar polygons: the floor and ceiling slabs reach to the outer face of the
walls, and walls A and C run past walls B and D, so no corner shows a gap (the same layout as the
contract's garage example). A wall with a window or a door is still one polygon: ``shell.openings``
says where the holes are and ``RoomBuilder`` cuts them.

The manifest is a pinned file: UTF-8, LF line endings, the same bytes for the same inputs. It never
goes into the repository; ``write_room`` refuses a folder inside one and writes to the player's user
data (``user://rooms/<room_id>/``, on Windows ``%APPDATA%\\Godot\\app_userdata\\Garage-Game\\rooms``).
"""

from __future__ import annotations

import json
import os
import re
import shutil
import sys
from pathlib import Path
from typing import Any, Callable, NamedTuple

import numpy as np

from .. import __version__
from ..coverage.backend import MODEL_REGISTRY
from ..jsonio import sha256_hex
from ..paths import git_checkout_of
from ..scene import Scene
from .planes import ShellPlan, WALL_KEYS
from .spec import OpeningSpec, ShellSpec

GAME_NAME = "Garage-Game"
SKY_COLOUR = "#d6e2f5"
SUN_COLOUR = "#fff1d2"


class ShellError(ValueError):
    """The shell cannot be exported as described; the message says why."""


def wall_frame(plan: ShellPlan, key: str) -> dict[str, Any]:
    """Origin (the wall's left-hand floor corner seen from inside), width direction, inward normal and length."""
    x0, x1, z0, z1 = plan.x_min, plan.x_max, plan.z_min, plan.z_max
    return {
        "A": {"origin": (x0, 0.0, z0), "u": (1.0, 0.0, 0.0), "n": (0.0, 0.0, 1.0), "length": x1 - x0},
        "B": {"origin": (x1, 0.0, z0), "u": (0.0, 0.0, 1.0), "n": (-1.0, 0.0, 0.0), "length": z1 - z0},
        "C": {"origin": (x1, 0.0, z1), "u": (-1.0, 0.0, 0.0), "n": (0.0, 0.0, -1.0), "length": x1 - x0},
        "D": {"origin": (x0, 0.0, z1), "u": (0.0, 0.0, -1.0), "n": (1.0, 0.0, 0.0), "length": z1 - z0},
    }[key]


def _r(v: float) -> float:
    return round(float(v), 3) + 0.0  # + 0.0 turns -0.0 into 0.0


def _pt(p) -> list[float]:
    return [_r(p[0]), _r(p[1]), _r(p[2])]


def opening_centre(plan: ShellPlan, o: OpeningSpec) -> list[float]:
    f = wall_frame(plan, o.wall)
    c = np.array(f["origin"]) + np.array(f["u"]) * o.u_m + np.array([0.0, 1.0, 0.0]) * (o.v_bottom_m + o.height_m / 2)
    return _pt(c)


def check_openings(plan: ShellPlan, openings: list[OpeningSpec]) -> None:
    """Every opening inside its wall, and none overlapping another on the same wall."""
    for o in openings:
        length = wall_frame(plan, o.wall)["length"]
        if o.u_m - o.width_m / 2 < -1e-6 or o.u_m + o.width_m / 2 > length + 1e-6:
            raise ShellError(f"opening {o.id} runs off the end of wall {o.wall} (u {o.u_m} +- {o.width_m / 2}, wall {length:.3f} m long)")
        if o.v_bottom_m + o.height_m > plan.height + 1e-6:
            raise ShellError(f"opening {o.id} is taller than the room ({o.v_bottom_m + o.height_m:.3f} m against {plan.height:.3f} m)")
    for i, a in enumerate(openings):
        for b in openings[i + 1:]:
            if a.wall != b.wall:
                continue
            across = min(a.u_m + a.width_m / 2, b.u_m + b.width_m / 2) - max(a.u_m - a.width_m / 2, b.u_m - b.width_m / 2)
            up = min(a.v_bottom_m + a.height_m, b.v_bottom_m + b.height_m) - max(a.v_bottom_m, b.v_bottom_m)
            if across > 1e-6 and up > 1e-6:
                raise ShellError(f"openings {a.id} and {b.id} overlap on wall {a.wall}")


def _polygon(part_id: str, role: str, points, thickness: float, material_role: str, colour: str) -> dict[str, Any]:
    return {"id": part_id, "role": role,
            "geometry": {"kind": "polygon", "points_m": [_pt(p) for p in points], "thickness_m": thickness},
            "collides": True, "material_role": material_role, "base_color": colour}


def shell_parts(plan: ShellPlan, spec: ShellSpec) -> list[dict[str, Any]]:
    t = spec.thickness_m
    x0, x1, z0, z1, H = plan.x_min, plan.x_max, plan.z_min, plan.z_max, plan.height
    colour = {s.key: spec.surface_colours.get(s.key, s.base_color) for s in plan.surfaces}
    parts = [
        _polygon("shell:floor", "floor", [(x0 - t, 0, z0 - t), (x0 - t, 0, z1 + t), (x1 + t, 0, z1 + t), (x1 + t, 0, z0 - t)],
                 t, plan.surface("floor").material_role, colour["floor"]),
        _polygon("shell:ceiling", "ceiling", [(x0 - t, H, z0 - t), (x1 + t, H, z0 - t), (x1 + t, H, z1 + t), (x0 - t, H, z1 + t)],
                 t, plan.surface("ceiling").material_role, colour["ceiling"]),
    ]
    for key in WALL_KEYS:
        f = wall_frame(plan, key)
        o, u, L = np.array(f["origin"]), np.array(f["u"]), f["length"]
        reach = t if key in ("A", "C") else 0.0  # A and C run past B and D, which stop at the inside corner
        a, b = -reach, L + reach
        pts = [o + u * a, o + u * b, o + u * b + np.array([0, H, 0]), o + u * a + np.array([0, H, 0])]
        parts.append(_polygon(f"shell:wall_{key.lower()}", "wall", pts, t, plan.surface(key).material_role, colour[key]))
    return parts


def build_openings(plan: ShellPlan, spec: ShellSpec) -> list[dict[str, Any]]:
    check_openings(plan, spec.openings)
    return [{"id": o.id, "kind": o.kind, "host_part_id": f"shell:wall_{o.wall.lower()}", "center_m": opening_centre(plan, o),
             "size_m": [_r(o.width_m), _r(o.height_m)], "state": o.state, "traversable": o.traversable}
            for o in spec.openings]


def light_hints(plan: ShellPlan, spec: ShellSpec) -> list[dict[str, Any]]:
    """Window light just outside each window, aimed into the room; the lamps the reviewer placed; and the sun."""
    hints: list[dict[str, Any]] = []
    for o in spec.openings:
        if o.kind != "window":
            continue
        f = wall_frame(plan, o.wall)
        n = np.array(f["n"])
        centre = np.array(opening_centre(plan, o))
        outside = centre - n * (spec.thickness_m + 0.05)
        d = n + np.array([0.0, -0.3, 0.0])
        hints.append({"id": f"{o.id}_light", "kind": "window", "position_m": _pt(outside), "direction": [_r(v) for v in d],
                      "color": SKY_COLOUR, "relative_intensity": 0.7, "estimated": True})
    for lamp in spec.lamps:
        hints.append({"id": lamp.id, "kind": "ceiling_lamp", "position_m": _pt(lamp.position_m), "color": lamp.color,
                      "relative_intensity": lamp.relative_intensity, "estimated": True})
    hints.append({"id": "sun", "kind": "sun", "color": SUN_COLOUR, "relative_intensity": 1.0, "estimated": True})
    return hints


def source_block(scene: Scene) -> dict[str, Any]:
    photos = [p for p in scene.manifest["photos"] if p.get("status") == "ok"]
    devices = sorted({f"{(p.get('exif') or {}).get('make', '')} {(p.get('exif') or {}).get('model', '')}".strip()
                      for p in photos} - {""})
    poses = MODEL_REGISTRY["facebook/map-anything-apache"]
    return {
        "kind": "capture",
        "capture": {
            "session_id": scene.session_id,
            "photo_count": len(scene.registered),
            "devices": devices[:8],
            "privacy": {
                "location_metadata_removed": True,
                "people_visible": True,
                "shareable": False,
                "notes": "Source photos carried no location data. A few frames show the photographer; none of that is in the room data. Not shareable until the owner opts in.",
            },
        },
        "pipeline": {
            "name": "enfractal-roomscan", "version": __version__,
            "backends": [
                {"stage": "poses", "name": "facebook/map-anything-apache", "version": poses["revision"][:7], "license": "Apache-2.0", "hosted": False},
                {"stage": "layout", "name": "roomscan box fit with tape-fitted scale", "version": __version__, "license": "Apache-2.0", "hosted": False},
                {"stage": "export", "name": "roomscan shell export", "version": __version__, "license": "Apache-2.0", "hosted": False},
            ],
        },
    }


def free_spawns(visible_floor: np.ndarray, origin: tuple[float, float], cell_m: float,
                bounds: tuple[float, float, float, float], *, keep_clear_m: float = 0.3, wall_margin_m: float = 0.5,
                avoid: list[tuple[float, float, float, float]] | None = None) -> list[tuple[float, float, float]]:
    """Floor spots with clear floor all round, best first.

    ``visible_floor`` is a boolean grid (rows along z, columns along x, cells of ``cell_m`` from ``origin``) that is
    True where photos saw the bare floor with nothing standing on it. A spot is at least ``keep_clear_m`` from the
    nearest cell that is not, and ``wall_margin_m`` from the walls; ``avoid`` holds footprints (x0, z0, x1, z1) to stay
    off. Returns (x, z, clearance_m) triples, spread out: a later spot is at least a metre from an earlier one.
    """
    from scipy import ndimage  # CPU-only dependency already in the project

    x0, x1, z0, z1 = bounds
    rows, cols = visible_floor.shape
    xs = origin[0] + (np.arange(cols) + 0.5) * cell_m
    zs = origin[1] + (np.arange(rows) + 0.5) * cell_m
    X, Z = np.meshgrid(xs, zs)
    free = visible_floor.copy()
    for ax0, az0, ax1, az1 in avoid or []:
        free &= ~((X > ax0 - 0.1) & (X < ax1 + 0.1) & (Z > az0 - 0.1) & (Z < az1 + 0.1))
    # A cell counts as clear to the edge of the room even though nothing is photographed beyond it.
    padded = np.pad(free, 1, constant_values=True)
    clearance = ndimage.distance_transform_edt(padded)[1:-1, 1:-1] * cell_m
    clearance = np.where((X > x0 + wall_margin_m) & (X < x1 - wall_margin_m) & (Z > z0 + wall_margin_m) & (Z < z1 - wall_margin_m) & free,
                         clearance, 0.0)
    picks: list[tuple[float, float, float]] = []
    for flat in np.argsort(-clearance, axis=None):
        r, c = divmod(int(flat), cols)
        if clearance[r, c] < keep_clear_m:
            break
        x, z = float(xs[c]), float(zs[r])
        if all(np.hypot(x - px, z - pz) >= 1.0 for px, pz, _ in picks):
            picks.append((x, z, float(clearance[r, c])))
        if len(picks) == 4:
            break
    return picks


def build_manifest(plan: ShellPlan, spec: ShellSpec, scene: Scene, *, created_utc: str,
                   spawns: list[dict[str, Any]], objects: list[dict[str, Any]] | None = None,
                   files: list[dict[str, Any]] | None = None) -> dict[str, Any]:
    if not spawns:
        raise ShellError("a room needs player and companion spawns")
    room: dict[str, Any] = {
        "schema": "enfractal.room", "version": 1,
        "room_id": spec.room,
        "display_name": spec.display_name or spec.room.replace("_", " ").title(),
        "description": spec.description or "A scanned room: shell only.",
        "created_utc": created_utc, "units": "m", "axes": "y_up_neg_z_forward",
        "source": source_block(scene),
        "bounds": {"min_m": [_r(plan.x_min), 0.0, _r(plan.z_min)], "max_m": [_r(plan.x_max), _r(plan.height), _r(plan.z_max)]},
        "shell": {"parts": shell_parts(plan, spec), "openings": build_openings(plan, spec)},
        "objects": objects or [],
        "spawns": spawns,
    }
    if spec.site:
        room["site"] = dict(spec.site)
    room["light_hints"] = light_hints(plan, spec)
    room["files"] = sorted(files or [], key=lambda f: f["path"])
    return room


def dump(document: Any) -> bytes:
    return (json.dumps(document, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")


def default_rooms_dir() -> Path:
    """Where the game looks for a player's captured rooms (``user://rooms``), by platform."""
    if sys.platform == "win32":
        base = Path(os.environ.get("APPDATA") or Path.home() / "AppData" / "Roaming") / "Godot" / "app_userdata"
    elif sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support" / "Godot" / "app_userdata"
    else:
        base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / "godot" / "app_userdata"
    return base / GAME_NAME / "rooms"


ROOM_ID = re.compile(r"^[a-z][a-z0-9_-]{0,63}$")


class RoomWrite(NamedTuple):
    path: Path  # where the room's manifest is (or would be): <rooms_dir>/<room_id>/room.json
    sha256: str  # of the manifest's bytes, the room's identity pin
    published: bool  # False when the check failed or could not run: the room folder is as it was
    problems: list[str]


def room_target(rooms_dir: Path, room_id: str) -> Path:
    """Where a room's manifest goes, checked on the *resolved* path: never inside any Git checkout, never outside ``rooms_dir``.

    Every checkout counts, not only the one the code runs from (another clone's tracked tree is as bad), and links are
    followed first, so a ``rooms/garage`` that points into a checkout is refused.
    """
    if not ROOM_ID.match(room_id):
        raise ShellError(f"{room_id!r} is not a room id (a lowercase token)")
    rooms = Path(rooms_dir).resolve()
    folder = (rooms / room_id).resolve()
    if folder.parent != rooms:
        raise ShellError(f"the room folder {folder} resolves outside the rooms folder {rooms}")
    for place in (rooms, folder):
        checkout = git_checkout_of(place)
        if checkout is not None:
            raise ShellError(f"{place} is inside the Git checkout {checkout} (the repository or another clone); "
                             "a captured room goes to the player's user data, never where Git can track it")
    return folder / "room.json"


def write_room(rooms_dir: Path, manifest: dict[str, Any], *, check: Callable[[Path], list[str]] | None = None) -> RoomWrite:
    """Stage ``<rooms_dir>/<room_id>/room.json``, check the staged room, and only then replace the one that is there.

    ``check`` takes the staged room folder and returns problems (``export.check_with_contract``); with any problem, or when
    it could not run, the room that was playable stays exactly as it was and nothing is published. Without ``check`` the
    manifest is published as it is.
    """
    target = room_target(rooms_dir, manifest["room_id"])
    data = dump(manifest)
    sha = sha256_hex(data)
    staging = target.parent.parent / ".staging" / f"{manifest['room_id']}-{os.getpid()}" / manifest["room_id"]
    staging.mkdir(parents=True, exist_ok=True)
    staged = staging / "room.json"
    try:
        staged.write_bytes(data)
        problems = check(staging) if check is not None else []
        if problems:
            return RoomWrite(target, sha, False, list(problems))
        target.parent.mkdir(parents=True, exist_ok=True)
        os.replace(staged, target)
        return RoomWrite(target, sha, True, [])
    finally:
        shutil.rmtree(staging.parent, ignore_errors=True)
        try:
            staging.parent.parent.rmdir()  # the .staging folder, when nothing else is staging in it
        except OSError:
            pass
