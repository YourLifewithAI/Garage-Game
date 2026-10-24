"""C3 end to end: the scaled scene, the shell planes, the reviewer's spec, the manifest, the contract check.

Writes under ``captures/<room>/shell/`` (the plan with its evidence, and rectified wall pictures for
the reviewer) and the room manifest to the player's user data. Nothing goes into the repository.
"""

from __future__ import annotations

import importlib.util
import json
import math
import time
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image

from .. import ids
from ..jsonio import json_bytes
from ..coverage.grid import CELL_M, floor_grid
from ..ortho import rectify_plane
from ..paths import OutputGuard, find_repo_root
from ..scene import Scene, load_scene
from .manifest import ShellError, build_manifest, default_rooms_dir, free_spawns, wall_frame, write_room
from .planes import WALL_KEYS, ShellPlan, plan_shell
from .spec import SPEC_FILE, ShellSpec, SpecError, load_spec


def check_with_contract(room_dir: Path) -> list[str]:
    """Problems ``contracts/validate.py`` finds in a room folder (empty when it validates), or a note if it cannot run."""
    repo = find_repo_root()
    path = (repo / "contracts" / "validate.py") if repo else None
    if path is None or not path.is_file():
        return ["contracts/validate.py not found"]
    spec = importlib.util.spec_from_file_location("garage_game_contract_validate", path)
    module = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
    try:
        spec.loader.exec_module(module)  # type: ignore[union-attr]
    except ImportError as error:  # jsonschema is not installed in this environment
        return [f"contract validator could not run: {error}"]
    return list(module.check_room(Path(room_dir)))


def yaw_toward(dx: float, dz: float) -> float:
    """Godot yaw in degrees (0 faces -Z, positive turns left) for a heading (dx, dz) on the floor."""
    return round(math.degrees(math.atan2(-dx, -dz)), 1)


def default_spawns(scene: Scene, plan: ShellPlan, avoid: list[tuple[float, float, float, float]] | None = None
                   ) -> list[dict[str, Any]]:
    grid = floor_grid(scene.coverage_views(), np.zeros((0, 3)), scene.bounds)
    visible = (grid.views >= 2) & ~grid.blocked & (grid.hidden_by <= 1)
    spots = free_spawns(visible, (scene.bounds[0], scene.bounds[2]), CELL_M, scene.bounds, avoid=avoid)
    if len(spots) < 2:
        raise ShellError("could not find two clear spots of floor for the player and the companion; put spawns in the shell spec")
    cx, cz = (plan.x_min + plan.x_max) / 2, (plan.z_min + plan.z_max) / 2
    out = []
    for (x, z, _), (sid, role) in zip(spots[:2], (("player_start", "player"), ("companion_start", "companion"))):
        out.append({"id": sid, "role": role, "position_m": [round(x, 3), 0.0, round(z, 3)],
                    "yaw_deg": yaw_toward(cx - x, cz - z) if math.hypot(cx - x, cz - z) > 0.3 else 0.0})
    return out


def picked_footprints(room_dir: Path) -> list[tuple[float, float, float, float]]:
    """Footprints (x0, z0, x1, z1) of the floor objects picked in the room's inventory, so spawns stay off them; none without an inventory."""
    path = Path(room_dir) / "inventory.json"
    if not path.is_file():
        return []
    inventory = json.loads(path.read_bytes())
    ids = {p["id"] for p in inventory.get("picks", [])}
    return [tuple(o["box"]["footprint_m"]) for o in inventory.get("objects", [])
            if o["id"] in ids and o["placement"]["support"]["kind"] == "floor"]


def wall_pictures(scene: Scene, plan: ShellPlan, guard: OutputGuard, rel: Path, *, res_m: float = 0.01) -> list[str]:
    """Rectified picture of each wall for the reviewer who decides where the openings are."""
    written = []
    for key in WALL_KEYS:
        f = wall_frame(plan, key)
        pic = rectify_plane(scene, f["origin"], f["u"], (0.0, 1.0, 0.0), (f["length"], plan.height), res_m=res_m,
                            recess_m=0.3, depth_tol_m=0.06)
        import io
        buf = io.BytesIO()
        Image.fromarray(pic.rgb).save(buf, format="PNG")
        written.append(guard.rel(guard.write_bytes(rel / f"wall_{key.lower()}.png", buf.getvalue())))
    return written


def plan_record(plan: ShellPlan, spec: ShellSpec, scene: Scene) -> dict[str, Any]:
    return {
        "schema": ids.id_of("shell_plan"), "version": 1, "room": scene.room, "session_id": scene.session_id,
        "scale": {"factor": round(plan.scale_factor, 5), "source": plan.scale_source},
        "box_m": {"x_min": round(plan.x_min, 3), "x_max": round(plan.x_max, 3), "z_min": round(plan.z_min, 3),
                  "z_max": round(plan.z_max, 3), "height": round(plan.height, 3)},
        "walls": {"A": "z_min, faced by the first photo", "B": "x_max", "C": "z_max", "D": "x_min"},
        "surfaces": [{"id": s.id, "role": s.role, "colours": [[h, round(share, 3)] for h, share in s.colours],
                      "evidence": s.evidence} for s in plan.surfaces],
        "spec_fingerprint": spec.fingerprint(),
        "openings": [o.__dict__ for o in spec.openings],
    }


def export_shell(captures_root: Path, room: str, *, session: str | None = "latest", rooms_dir: Path | None = None,
                 created_utc: str | None = None, spec_path: Path | None = None, pictures: bool = True,
                 inventory_footprints: list[tuple[float, float, float, float]] | None = None,
                 log=print) -> dict[str, Any]:
    t0 = time.perf_counter()
    scene = load_scene(captures_root, room, session)
    room_dir = scene.room_dir
    guard = OutputGuard(room_dir)
    spec_file = Path(spec_path) if spec_path else room_dir / SPEC_FILE
    if spec_file.is_file():
        spec = load_spec(spec_file)
        if spec.room != room or spec.session_id != scene.session_id:
            raise SpecError(f"{spec_file.name} is for room {spec.room}, session {spec.session_id}; this is {room}, "
                            f"{scene.session_id}. The wall letters follow the first photo, so a spec belongs to its session.")
    else:
        log(f"No {SPEC_FILE}: exporting the bare shell (no openings, no site). Review the wall pictures and write one.")
        spec = ShellSpec(room=room, session_id=scene.session_id)
    plan = plan_shell(scene)
    log(f"Shell planes checked in {time.perf_counter() - t0:.1f}s: "
        + ", ".join(f"{s.key} {s.evidence['median_offset_cm']} cm" for s in plan.surfaces))
    rel = Path("shell")
    guard.mkdir(rel)
    pictures_written = wall_pictures(scene, plan, guard, rel) if pictures else []
    spawns = [dict(id=s.id, role=s.role, position_m=list(s.position_m), yaw_deg=s.yaw_deg) for s in spec.spawns] \
        or default_spawns(scene, plan, inventory_footprints if inventory_footprints is not None else picked_footprints(room_dir))
    created = created_utc or time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    manifest = build_manifest(plan, spec, scene, created_utc=created, spawns=spawns)
    guard.write_bytes(rel / "plan.json", json_bytes(plan_record(plan, spec, scene)))
    target_root = Path(rooms_dir) if rooms_dir else default_rooms_dir()
    written = write_room(target_root, manifest, check=check_with_contract)
    problems = written.problems
    if written.published:
        log(f"Room manifest {written.path} ({written.sha256[:12]}): validates and replaces any earlier one")
    else:
        log(f"Room manifest NOT published, the room folder is as it was ({len(problems)} problem(s)): " + "; ".join(problems[:3]))
    return {"manifest": written.path if written.path.is_file() else None, "published": written.published,
            "sha256": written.sha256, "problems": problems, "pictures": pictures_written, "plan": plan, "spec": spec,
            "spawns": spawns, "seconds": round(time.perf_counter() - t0, 1)}

