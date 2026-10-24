"""C4 end to end: detections, candidates, masks across the photos, boxes and colours, and the inventory file.

    detections (OWLv2, whole and tiled)  ->  lifted sightings  ->  clusters (candidate objects)
        -> a reviewer's corrections (drop, relabel, add by place)
        -> each candidate followed through the photos, SAM masks in up to ten of them
        -> pooled points of the object -> a box (size, place, turn, front) and broad colours
        -> inventory.json (local), evidence sheets for the reviewer, and a recipe request for the kinds that have a recipe

Everything is written under ``captures/<room>/``. The GPU is used only by detection and segmentation,
both cached; changing how boxes or colours are worked out reruns only the CPU part.
"""

from __future__ import annotations

import io
import math
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

import numpy as np
from PIL import Image

from .. import ids
from ..colours import dominant_colours
from ..coverage.run import select_views
from ..jsonio import json_bytes
from ..paths import OutputGuard
from ..scene import Scene, load_scene
from .cluster import Cluster, build_clusters, group_by_kind
from .curation import CURATION_FILE, Addition, Curation, Override, load_curation, slug
from .detection import run_inventory_detector
from .fit import (Box, box_contains_xz, box_overlap_volume, face_away_from, fit_box, front_from_height_profile,
                  front_from_viewers)
from .lift import lift_all
from .picks import pick_five
from .masks import MaskResult, MaskStore, prompt_key, segment_prompts
from .overlay import entry_box
from .recipes import Evidence, request_for, slots_for
from .track import Prompt, pick_prompts
from .vocabulary import BY_NAME, SHELL_KINDS, plausible

MIN_EVIDENCE = 1.8
MAX_PROMPTS = 10
INVENTORY_FILE = "inventory.json"


@dataclass
class Candidate:
    kind: str
    centre: np.ndarray
    size_hint_m: float
    detections: list[tuple[int, float, tuple[float, float, float, float]]]
    source: str  # "detector" or "added"
    evidence: float = 0.0
    mean_score: float = 0.0
    aliases: dict[str, float] = field(default_factory=dict)
    note: str = ""
    prompts: list[Prompt] = field(default_factory=list)


@dataclass
class Fit:
    candidate: Candidate
    box: Box
    points: np.ndarray
    rgb: np.ndarray
    pixels: np.ndarray
    accepted: list[MaskResult]
    prompts: list[Prompt]
    notes: list[str] = field(default_factory=list)


def size_hint(kind: str, extents: list[float]) -> float:
    lo, hi = BY_NAME[kind].longest_m
    return float(np.clip(np.median(extents) if extents else (lo * hi) ** 0.5, lo, hi))


def candidates_from_clusters(clusters: list[Cluster], curation: Curation, *, min_evidence: float = MIN_EVIDENCE) -> list[Candidate]:
    out: list[Candidate] = []
    for c in clusters:
        if c.kind in SHELL_KINDS or BY_NAME[c.kind].support == "wall" or c.evidence < min_evidence:
            continue
        centre = np.median(np.stack([s.centre for s in c.sightings]), axis=0)
        if curation.dropped(c.kind, centre):
            continue
        kind = curation.new_kind(c.kind, centre)
        out.append(Candidate(kind, centre, size_hint(kind, [s.extent_m for s in c.sightings]),
                             [(s.view, s.score, s.box) for s in c.sightings], "detector", c.evidence, c.mean_score, dict(c.aliases)))
    return out


def candidates_from_additions(additions: list[Addition]) -> list[Candidate]:
    return [Candidate(a.kind, np.array(a.at_m, float), a.size_hint_m, [], "added", note=a.note) for a in additions]


def merge_duplicates(cands: list[Candidate]) -> list[Candidate]:
    """Two proposals of the same kind a few centimetres apart are one (the detector split a cluster); keep the stronger."""
    cands = sorted(cands, key=lambda c: (c.source != "added", -c.evidence, c.kind))  # the reviewer's seeds first
    kept: list[Candidate] = []
    for c in cands:
        if any(k.kind == c.kind and np.linalg.norm(k.centre - c.centre) < 0.5 * max(k.size_hint_m, c.size_hint_m) for k in kept):
            continue
        kept.append(c)
    return kept


def object_points(scene: Scene, cand: Candidate, results: list[MaskResult]
                  ) -> tuple[np.ndarray, np.ndarray, np.ndarray, list[MaskResult], list[str], list[np.ndarray]]:
    """Pool the points and colours of an object from its accepted masks, dropping what is not near it.

    The last item is each photo's own points, for working out the height photo by photo.
    """
    pts, rgb, pix, used = [], [], [], []
    notes: list[str] = []
    band = max(0.10, 0.7 * cand.size_hint_m)
    for r in results:
        if not r.accepted:
            continue
        sel = r.mask & scene.valid[r.view]
        if int(sel.sum()) < 4:
            continue
        rr, cc = np.nonzero(sel)
        p = scene.pts[r.view][rr, cc]
        d = np.linalg.norm(p - scene.camera_centre(r.view), axis=1)
        near = np.abs(d - np.median(d)) <= band
        near &= np.linalg.norm(p - cand.centre, axis=1) <= max(0.35, 1.6 * cand.size_hint_m)
        if int(near.sum()) < 4:
            continue
        pts.append(p[near])
        rgb.append(scene.pixel_colours(r.view, rr[near], cc[near]))
        pix.append(r.pixels)
        used.append(r)
    if not pts:
        return np.zeros((0, 3)), np.zeros((0, 3), np.uint8), np.zeros((0, 3), np.uint8), [], ["no usable masks"], []
    P = np.concatenate(pts)
    C = np.concatenate(rgb)
    # Points far from the pooled middle are another object's or depth-edge flyers.
    med = np.median(P, axis=0)
    keep = np.linalg.norm(P - med, axis=1) <= max(0.3, 1.2 * cand.size_hint_m)
    keep &= (P[:, 1] > -0.1) & (P[:, 1] < scene.height_m + 0.1)
    if int(keep.sum()) < len(P):
        notes.append(f"dropped {len(P) - int(keep.sum())} stray points")
    per_view = [p[(np.linalg.norm(p - med, axis=1) <= max(0.3, 1.2 * cand.size_hint_m)) & (p[:, 1] > -0.1)] for p in pts]
    return P[keep], C[keep], (np.concatenate(pix) if pix else np.zeros((0, 3), np.uint8)), used, notes, per_view


def nearest_wall(box: Box, scene: Scene, *, within_m: float = 0.3) -> tuple[str, np.ndarray, float] | None:
    """The wall a box stands against: (letter, inward direction (x, z), gap in metres), if closer than ``within_m``."""
    x0, x1, z0, z1 = scene.bounds
    fx0, fz0, fx1, fz1 = box.footprint
    gaps = {"A": (fz0 - z0, np.array([0.0, 1.0])), "B": (x1 - fx1, np.array([-1.0, 0.0])),
            "C": (z1 - fz1, np.array([0.0, -1.0])), "D": (fx0 - x0, np.array([1.0, 0.0]))}
    key = min(gaps, key=lambda k: gaps[k][0])
    gap, inward = gaps[key]
    return (key, inward, float(gap)) if gap < within_m else None


def robust_heights(per_view: list[np.ndarray]) -> tuple[float, float] | None:
    """The object's lowest and highest points as the middle of what each photo says, so one photo's flyers do not set them."""
    spans = [(float(np.percentile(p[:, 1], 5)), float(np.percentile(p[:, 1], 95))) for p in per_view if len(p) >= 12]
    if len(spans) < 3:
        return None
    return float(np.median([s[0] for s in spans])), float(np.median([s[1] for s in spans]))


def apply_override(box: Box, o: Override) -> Box:
    w, h, d = o.size_m if o.size_m else box.size_m
    cx, cz = o.centre_xz_m if o.centre_xz_m else (box.centre_m[0], box.centre_m[2])
    base = box.base_y if o.base_y is None else o.base_y
    top = box.base_y + box.size_m[1]
    if o.size_m is None and o.base_y is not None:
        h = max(top - base, 0.01)
    # What the fit said about the parts the reviewer measured no longer holds.
    kept = [n for n in box.notes if not (o.base_y is not None and n.startswith("rests on a surface"))
            and not (o.yaw_deg is not None and n.startswith("front "))]
    notes = kept + [f"reviewer's measurement: {', '.join(k for k, v in (('size', o.size_m), ('place', o.centre_xz_m), ('turn', o.yaw_deg), ('base', o.base_y)) if v is not None)}"
                         + (f" ({o.note})" if o.note else "")]
    return Box((cx, base + h / 2, cz), (w, h, d), box.yaw_deg if o.yaw_deg is None else o.yaw_deg, base, box.front_known, notes)


def fit_candidate(scene: Scene, cand: Candidate, results: list[MaskResult], prompts: list[Prompt],
                  override: Override | None = None) -> Fit | None:
    kind = BY_NAME[cand.kind]
    P, C, pix, used, notes, per_view = object_points(scene, cand, results)
    min_points = 40 if kind.small else 80
    # A small thing is photographed from few places (a jar on a far table): two good masks will do, and the entry says so.
    if len(used) < (2 if kind.small else 3) or len(P) < min_points:
        return None
    if len(used) < 3:
        notes.append(f"only {len(used)} photos with a usable mask: a rough size")
    box = fit_box(P, round_footprint=kind.footprint == "round", on_floor=kind.support == "floor",
                  y_range=robust_heights(per_view))
    # Which way it faces: a thing with a back by its height profile, a thing against a wall away from it, else toward its viewers.
    cams = np.array([[scene.camera_centre(r.view)[0], scene.camera_centre(r.view)[2]] for r in used])
    wall = nearest_wall(box, scene)
    front = None
    why = ""
    if cand.kind in ("couch", "office chair", "bean bag") and kind.footprint != "round":
        front = front_from_height_profile(P, box)
        why = "from where it is taller (its back)"
    if front is None and wall is not None and cand.kind not in ("laptop", "jar", "mug", "bottle"):
        front, why = wall[1], f"away from wall {wall[0]}"
    if front is None:
        front, why = front_from_viewers(box, cams), "toward the photos that saw it"
    if kind.footprint != "round":
        box = face_away_from(box, front)
        box.notes.append(f"front {why}")
    if override is not None:
        box = apply_override(box, override)
    return Fit(cand, box, P, C, pix, used, prompts, notes)


def confidence(scene: Scene, fit: Fit) -> float:
    """0 to 1, from how many photos agreed, how sure the detector was, whether the size is plausible, and how much of it was seen."""
    views = len(fit.accepted)
    az = sorted(((np.degrees(np.arctan2(scene.camera_centre(r.view)[2] - fit.box.centre_m[2], scene.camera_centre(r.view)[0] - fit.box.centre_m[0])) + 360) % 360) for r in fit.accepted)
    gaps = np.diff(az + [az[0] + 360]) if len(az) > 1 else np.array([360.0])
    around = 1.0 - float(gaps.max()) / 360.0  # how much of the circle round the object has a photo in it
    longest = max(fit.box.size_m[0], fit.box.size_m[1], fit.box.size_m[2])
    ok_size = 1.0 if plausible(fit.candidate.kind, longest) else 0.2
    detector = min(1.0, fit.candidate.mean_score / 0.5) if fit.candidate.source == "detector" else 0.6
    accept_rate = views / max(1, len(fit.prompts))
    return round(float(np.clip(0.30 * min(views / 8, 1) + 0.20 * detector + 0.20 * ok_size + 0.15 * around + 0.15 * accept_rate, 0, 1)), 2)


def entry_for(scene: Scene, fit: Fit, ident: str, boxes: dict[str, Fit]) -> dict[str, Any]:
    kind = BY_NAME[fit.candidate.kind]
    box = fit.box
    colours = dominant_colours(fit.pixels if len(fit.pixels) >= 100 else fit.rgb, 3)
    evidence = Evidence(fit.points, fit.rgb, box.base_y, box.size_m[1], fit.pixels)
    recipe_colours, sources = slots_for(kind.recipe, evidence) if kind.recipe else ({}, {})
    # Support: on the floor, or on the top of another object, or on something not in the inventory.
    support: dict[str, Any] = {"kind": "floor", "height_m": 0.0}
    if box.base_y > 0.0:
        support = {"kind": "surface", "height_m": round(box.base_y, 3), "target_id": None}
    entry: dict[str, Any] = {
        "id": ident,
        "kind": fit.candidate.kind,
        "confidence": confidence(scene, fit),
        "box": {
            "centre_m": [round(v, 3) for v in box.centre_m],
            "size_m": [round(v, 3) for v in box.size_m],
            "yaw_deg": round(box.yaw_deg, 1),
            "footprint_m": [round(v, 3) for v in box.footprint],
        },
        "placement": {"position_m": [round(v, 3) for v in box.position_m], "yaw_deg": round(box.yaw_deg, 1),
                      "rotation": yaw_quaternion(box.yaw_deg), "support": support, "front_known": box.front_known},
        "colours": [{"hex": h, "share": round(s, 3)} for h, s in colours],
        "evidence": {
            "photos_followed": len(fit.prompts),
            "masks_accepted": len(fit.accepted),
            "points": int(len(fit.points)),
            "detector_views": len({v for v, _, _ in fit.candidate.detections}),
            "detector_mean_score": round(fit.candidate.mean_score, 3),
            "source": fit.candidate.source,
            "also_called": {k: round(v, 1) for k, v in sorted(fit.candidate.aliases.items(), key=lambda t: -t[1])[:4]},
        },
        "notes": fit.candidate.note and [fit.candidate.note] or [],
    }
    entry["notes"] += list(box.notes) + list(fit.notes)
    if kind.recipe:
        request = request_for(kind.recipe, box.size_m, recipe_colours)
        entry["recipe"] = request
        entry["recipe_slots"] = {"from_scan": sources, "defaulted": [s for s in ("cardboard", "edges", "upholstery", "legs", "shell", "keyboard", "screen", "glass", "lid", "beaker", "frame", "handle") if s in _slots(kind.recipe) and s not in recipe_colours]}
    else:
        entry["recipe"] = None
        entry["recipe_needed"] = {"for_kind": fit.candidate.kind, "size_m": [round(v, 3) for v in box.size_m],
                                  "colours": [h for h, _ in colours[:3]]}
    return entry


def yaw_quaternion(yaw_deg: float) -> list[float]:
    """The unit quaternion (x, y, z, w) of a turn about +Y, as a room manifest object's ``transform.rotation``."""
    half = math.radians(yaw_deg) / 2
    return [0.0, round(math.sin(half), 6), 0.0, round(math.cos(half), 6)]


def _slots(recipe: str) -> tuple[str, ...]:
    from .recipes import SLOTS
    return SLOTS[recipe]


def link_supports(entries: list[dict[str, Any]]) -> None:
    """An object that rests above the floor sits on the object under it, when the inventory has one whose top is there."""
    for e in entries:
        s = e["placement"]["support"]
        if s["kind"] != "surface":
            continue
        cx, _, cz = e["box"]["centre_m"]
        best = None
        for other in entries:
            if other is e:
                continue
            top = other["box"]["centre_m"][1] + other["box"]["size_m"][1] / 2
            # Over the other object's turned footprint, not its bounding square.
            if box_contains_xz(entry_box(other), cx, cz, 0.05) and abs(top - s["height_m"]) < 0.12:
                if best is None or abs(top - s["height_m"]) < best[0]:
                    best = (abs(top - s["height_m"]), other["id"])
        if best:
            s["kind"], s["target_id"] = "object", best[1]


def drop_overlapping(fits: list[Fit]) -> list[Fit]:
    """Two fits sharing most of their box are one object called two names: keep the one with more photos behind it."""
    def volume(f: Fit) -> float:
        return float(np.prod(f.box.size_m))

    def overlap(a: Fit, b: Fit) -> float:
        """The share of the smaller box that the other covers, with the turned boxes intersected as they are (their
        bounding squares overlap far more than the boxes do, and two separate rotated objects would be folded into one)."""
        small = min(volume(a), volume(b))
        return box_overlap_volume(a.box, b.box) / small if small > 1e-6 else 0.0

    kept: list[Fit] = []
    for f in sorted(fits, key=lambda f: (-len(f.accepted), f.candidate.kind)):
        if any(overlap(f, k) > 0.6 for k in kept):
            f.candidate.note += " (folded into a stronger neighbour)"
            continue
        kept.append(f)
    return kept


def evidence_sheet(store: MaskStore, fit: Fit, path: Path, guard: OutputGuard) -> str:
    thumbs = []
    for r in fit.accepted[:8]:
        thumbs.append(Image.open(io.BytesIO(r.thumb)).convert("RGB"))
    cell = 200
    out = Image.new("RGB", (cell * 4, cell * max(1, -(-len(thumbs) // 4))), (24, 24, 24))
    for i, im in enumerate(thumbs):
        im.thumbnail((cell, cell))
        out.paste(im, ((i % 4) * cell, (i // 4) * cell))
    buf = io.BytesIO()
    out.save(buf, format="JPEG", quality=85)
    return guard.rel(guard.write_bytes(path, buf.getvalue()))


def build_inventory(captures_root: Path, room: str, *, session: str | None = "latest",
                    detector_factory: Callable[[], Any] | None = None, segmenter_factory: Callable[[], Any] | None = None,
                    curation_path: Path | None = None, min_evidence: float = MIN_EVIDENCE, log=print) -> dict[str, Any]:
    t0 = time.perf_counter()
    scene = load_scene(captures_root, room, session)
    guard = OutputGuard(scene.room_dir)
    rel = Path("sessions") / scene.session_id / "inventory"
    guard.mkdir(rel)
    views = select_views(scene.manifest)
    detections, det_info = run_inventory_detector(guard, rel, scene.room_dir, views, scene.registered,
                                                  detector_factory=detector_factory, log=log)
    sightings = lift_all(scene, detections)
    clusters = build_clusters(group_by_kind(sightings))
    curation = load_curation(Path(curation_path) if curation_path else scene.room_dir / CURATION_FILE)
    cands = merge_duplicates(candidates_from_clusters(clusters, curation, min_evidence=min_evidence)
                             + candidates_from_additions(curation.add))
    log(f"{len(sightings)} sightings, {len(clusters)} clusters, {len(cands)} candidates to follow")
    by_view: dict[int, list[tuple[float, float, float, float]]] = {}
    for c in cands:
        c.prompts = pick_prompts(scene, c.centre, c.size_hint_m, c.detections,
                                 max_views=MAX_PROMPTS + (6 if c.size_hint_m > 0.8 else 0))
        for p in c.prompts:
            by_view.setdefault(p.view, []).append(p.box_stored)
    store = MaskStore(guard, rel, scene.session_id)
    if segmenter_factory is None:
        from ..coverage.backend import wait_for_vram
        from .segment import Segmenter

        wait_for_vram(1500, log=log)
        segmenter_factory = lambda: Segmenter(log=log)  # noqa: E731
    seg_info = segment_prompts(scene, by_view, store, segmenter_factory, log=log)
    fits: list[Fit] = []
    for c in cands:
        results = [store.results[k] for p in c.prompts if (k := prompt_key(p.view, p.box_stored)) in store.results]
        f = fit_candidate(scene, c, results, c.prompts, curation.override_for(c.kind, c.centre))
        if f is None:
            continue
        longest = max(f.box.size_m)
        if not plausible(c.kind, longest):
            c.note += f" (box {longest:.2f} m is not a plausible {c.kind}; left out)"
            continue
        fits.append(f)
    fits = drop_overlapping(fits)
    counts: dict[str, int] = {}
    entries: list[dict[str, Any]] = []
    for f in sorted(fits, key=lambda f: (f.candidate.kind, f.box.centre_m[0], f.box.centre_m[2])):
        counts[f.candidate.kind] = counts.get(f.candidate.kind, 0) + 1
        entries.append((f, f"{slug(f.candidate.kind)}_{counts[f.candidate.kind]}"))
    ids = [i for _, i in entries]
    docs = [entry_for(scene, f, i, {}) for f, i in entries]
    link_supports(docs)
    sheets = {}
    for (f, i) in entries:
        sheets[i] = evidence_sheet(store, f, rel / "evidence" / f"{i}.jpg", guard)
    for d in docs:
        d["evidence"]["sheet"] = sheets[d["id"]]
    picks = pick_five(docs)
    for rank, p in enumerate(picks, 1):
        entry = next(d for d in docs if d["id"] == p["id"])
        entry["pick"] = {"rank": rank, "why": p["why"]}
    inventory = {
        "schema": ids.id_of("inventory"), "version": 1, "room": scene.room, "session_id": scene.session_id,
        "frame": "the shell's room frame: metres, +Y up, floor at y = 0, -Z up the page; walls A at z_min, B at x_max, C at z_max, D at x_min",
        "scale": {"factor": round(scene.factor, 5), "source": scene.scale_source},
        "box_convention": "size_m is [width (local X), height, depth (local Z)]; yaw_deg turns local -Z (the front) to the direction it faces, positive turning left seen from above; placement.position_m is the bottom centre",
        "detector": det_info, "segmenter": seg_info,
        "objects": docs,
        "picks": picks,
        "dropped": [{"kind": c.kind, "centre_m": [round(float(v), 2) for v in c.centre], "note": c.note.strip()}
                    for c in cands if c.note and not any(c is f.candidate for f in fits)],
    }
    guard.write_bytes(INVENTORY_FILE, json_bytes(inventory))
    store.save()
    log(f"Inventory: {len(docs)} objects in {time.perf_counter() - t0:.0f}s")
    return {"inventory": inventory, "scene": scene, "fits": [f for f, _ in entries], "path": guard.path(INVENTORY_FILE)}
