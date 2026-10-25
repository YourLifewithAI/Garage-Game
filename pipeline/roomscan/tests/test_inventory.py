"""C4, the inventory, on a synthetic room: lifting, clusters, masks, boxes, colours, recipes, picks, curation.

No GPU, no model and no real photo: the point maps are ray-cast from a known room (tests/inv_scene.py), the
segmenter is a fake that returns the true object's pixels (or deliberately wrong ones), and every number is
checked against the truth the room was built from.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pytest

import inv_scene
from roomscan.colours import dominant_colours, hex_of
from roomscan.inventory import picks, recipes, vocabulary
from roomscan.inventory.build import (Candidate, Fit, apply_override, candidates_from_additions, candidates_from_clusters,
                                      confidence, drop_overlapping, entry_for, fit_candidate, link_supports, merge_duplicates,
                                      nearest_wall, yaw_quaternion)
from roomscan.inventory.cluster import build_clusters, group_by_kind
from roomscan.inventory.curation import Curation, CurationError, Near, Override, parse_curation
from roomscan.inventory.fit import Box
from roomscan.inventory.lift import lift_all, lift_sighting
from roomscan.inventory.masks import MaskResult, MaskStore, judge_mask, prompt_key, segment_prompts
from roomscan.inventory.photos import photo_to_stored_xy, stored_to_photo, stored_to_photo_xy
from roomscan.inventory.track import pick_prompts, projected_box, visible_views
from roomscan.paths import OutputGuard

TABLE_LO, TABLE_HI = (0.2, 0.0, 0.6), (1.0, 0.75, 1.4)


@pytest.fixture(scope="module")
def scene(tmp_path_factory):
    return inv_scene.build_scene(tmp_path_factory.mktemp("inv"))


def box_stored(scene, view, lo, hi):
    mask = inv_scene.true_box_mask(scene, view, lo, hi)
    ys, xs = np.nonzero(mask)
    return (float(xs.min()), float(ys.min()), float(xs.max() + 1), float(ys.max() + 1)) if len(xs) else None


def true_results(scene, lo, hi, views=None, accepted=True):
    out = []
    for v in views if views is not None else scene.registered:
        mask = inv_scene.true_box_mask(scene, v, lo, hi)
        if mask.sum() < 30:
            continue
        ys, xs = np.nonzero(mask)
        box = (float(xs.min()), float(ys.min()), float(xs.max() + 1), float(ys.max() + 1))
        pixels = np.tile(np.array([[160, 110, 70]], np.uint8), (200, 1))
        out.append(MaskResult(v, box, accepted, "ok", mask, pixels, b""))
    return out


# --- the vocabulary and the recipe interface ---------------------------------------------------------------

def test_every_recipe_kind_names_a_recipe_the_request_validator_knows():
    assert set(vocabulary.RECIPE_KINDS.values()) <= set(recipes.SLOTS)
    assert {"cardboard box", "couch", "laptop", "jar", "french press"} == set(vocabulary.RECIPE_KINDS)
    # No phrase is asked for by two kinds, or the detector's answer would not say which.
    queries = [q for k in vocabulary.KINDS for q in k.queries]
    assert len(queries) == len(set(queries))
    assert vocabulary.plausible("laptop", 0.36) and not vocabulary.plausible("jar", 3.0)


def test_a_recipe_request_has_exactly_the_four_keys_and_refuses_what_the_recipes_refuse():
    req = recipes.request_for("couch", (1.85, 0.78, 0.95), {"upholstery": "#978e84"})
    assert set(req) == {"recipe", "size_m", "colours", "params"}
    assert req["params"] == {"cushion_count": 3} and req["size_m"] == [1.85, 0.78, 0.95]
    for bad in ({"recipe": "couch", "size_m": [1, 1], "colours": {}, "params": {}},
                {"recipe": "couch", "size_m": [1, 1, 1], "colours": {"seat": "#aabbcc"}, "params": {}},
                {"recipe": "couch", "size_m": [1, 1, 1], "colours": {"legs": "#AABBCC"}, "params": {}},
                {"recipe": "couch", "size_m": [1, 1, 1], "colours": {}, "params": {"cushion_count": 9}},
                {"recipe": "couch", "size_m": [1, 0.001, 1], "colours": {}, "params": {}},
                {"recipe": "couch", "size_m": [1, 1, 1], "colours": {}, "params": {}, "extra": 1},
                {"recipe": "sofa", "size_m": [1, 1, 1], "colours": {}, "params": {}}):
        with pytest.raises(recipes.RequestError):
            recipes.validate_request(bad)
    assert recipes.cushion_count(0.3) == 1 and recipes.cushion_count(1.85) == 3 and recipes.cushion_count(9) == 6


def test_slot_colours_come_from_the_object_and_are_left_out_when_the_scan_cannot_show_them():
    rng = np.random.default_rng(0)
    pts = np.stack([rng.uniform(0, 1, 600), rng.uniform(0, 0.8, 600), rng.uniform(0, 1, 600)], -1)
    grey = np.tile([[150, 142, 132]], (600, 1))
    wood = np.tile([[120, 80, 45]], (600, 1))
    low = pts[:, 1] < 0.08
    rgb = np.where(low[:, None], wood, grey).astype(np.uint8)
    colours, source = recipes.slots_for("couch", recipes.Evidence(pts, rgb, 0.0, 0.8))
    assert colours["upholstery"] == hex_of([150, 142, 132]) and colours["legs"] == hex_of([120, 80, 45])
    # Legs the scan does not show as wood are left to the recipe's default.
    colours, _ = recipes.slots_for("couch", recipes.Evidence(pts, np.tile([[40, 40, 70]], (600, 1)).astype(np.uint8), 0.0, 0.8))
    assert "legs" not in colours and "upholstery" in colours
    # A closed laptop shows only its lid: the keyboard and the screen are not guessed.
    laptop, _ = recipes.slots_for("gaming_laptop", recipes.Evidence(pts, np.tile([[20, 20, 24]], (600, 1)).astype(np.uint8), 0.0, 0.02))
    assert set(laptop) == {"shell"}
    # A brown box in a dark corner is mostly shadow: the cardboard is the brown, not the black.
    shadow = np.concatenate([np.tile([[10, 8, 6]], (400, 1)), np.tile([[170, 125, 85]], (300, 1))]).astype(np.uint8)
    box_pts = pts[:700]
    box, _ = recipes.slots_for("cardboard_box", recipes.Evidence(box_pts, shadow, 0.0, 0.3, shadow))
    assert box["cardboard"] == hex_of([170, 125, 85]) and box["edges"] != box["cardboard"]


def test_colours_are_broad_and_deterministic():
    pix = np.concatenate([np.tile([[200, 30, 30]], (700, 1)), np.tile([[30, 30, 200]], (250, 1)), np.tile([[250, 250, 250]], (20, 1))])
    first = dominant_colours(pix, 3)
    assert first == dominant_colours(pix, 3)
    assert first[0][0] == "#c81e1e" and first[1][0] == "#1e1ec8" and len(first) == 2  # the 2 per cent of white is folded away
    assert sum(s for _, s in first) == pytest.approx(1.0)


# --- curation ----------------------------------------------------------------------------------------------

def test_curation_is_read_strictly_and_names_places_not_ids():
    doc = {"schema": "garage_game.inventory_curation", "version": 1,
           "drop": [{"at_m": [1, 0, 1], "radius_m": 0.5, "kind": "backpack"}],
           "relabel": [{"near": {"at_m": [0, 0, 0], "radius_m": 0.4}, "kind": "couch"}],
           "add": [{"kind": "jar", "at_m": [0.3, 0.8, 1.6], "size_hint_m": 0.15}],
           "override": [{"near": {"at_m": [0, 0, 0], "radius_m": 1}, "size_m": [1, 0.5, 0.5], "yaw_deg": 90}]}
    c = parse_curation(doc)
    assert c.dropped("backpack", np.array([1.2, 0, 1.1])) and not c.dropped("couch", np.array([1.2, 0, 1.1]))
    assert c.new_kind("table", np.array([0.1, 0, 0])) == "couch" and c.new_kind("table", np.array([3, 0, 0])) == "table"
    assert c.override_for("couch", np.array([0.2, 0, 0])).size_m == (1.0, 0.5, 0.5)
    for key, value in [("drop", [{"at_m": [1, 0], "radius_m": 1}]), ("add", [{"kind": "unicorn", "at_m": [0, 0, 0], "size_hint_m": 1}]),
                       ("override", [{"near": {"at_m": [0, 0, 0], "radius_m": 1}, "size_m": [1, 2]}]),
                       ("mystery", [])]:
        with pytest.raises(CurationError):
            parse_curation({"schema": "garage_game.inventory_curation", "version": 1, key: value})
    for wrong in ("something else", "garagegame.inventory_curation", "enfractal.inventory"):
        with pytest.raises(CurationError):
            parse_curation({"schema": wrong, "version": 1})
    # The founder's curation written before the rename (decision C) names its kind the old way: read as it is.
    old = parse_curation({**doc, "schema": "enfractal.inventory_curation"})
    assert old.new_kind("table", np.array([0.1, 0, 0])) == "couch"


# --- lifting, tracking, clusters ----------------------------------------------------------------------------

def test_a_detection_box_lifts_to_the_object_and_not_to_the_wall_behind_it(scene):
    best = max(scene.registered, key=lambda v: inv_scene.true_box_mask(scene, v, TABLE_LO, TABLE_HI).sum())
    box = box_stored(scene, best, TABLE_LO, TABLE_HI)
    s = lift_sighting(scene, best, "table", 0.9, box)
    assert s is not None
    # One photo sees one side: the centre is on the table's visible surface, so inside its box, not at its middle.
    assert np.all(s.centre >= np.array(TABLE_LO) - 0.05) and np.all(s.centre <= np.array(TABLE_HI) + 0.05)
    assert s.extent_m == pytest.approx(0.8, abs=0.25)
    assert len(s.points) <= 600 and s.distance_m > 0.5
    # A box on bare wall still lifts, but to the wall: nothing here claims to be an object.
    assert lift_sighting(scene, 0, "table", 0.9, (0, 0, 1.5, 1.5)) is None


def test_clusters_need_the_same_kind_in_several_photos_and_fold_duplicates(scene):
    detections = {}
    for v in scene.registered:
        found = []
        for label, lo, hi in (("table", TABLE_LO, TABLE_HI), ("shelving unit", (-2.0, 0.0, -1.0), (-1.55, 1.8, 0.4))):
            b = box_stored(scene, v, lo, hi)
            if b:
                found.append([label, 0.8, list(b)])
        if found:
            detections[v] = found
    # The same table is also called a desk by one photo's detector: one object, with the alias recorded.
    first = next(v for v in detections if any(k == "table" for k, _, _ in detections[v]))
    detections[first].append(["desk", 0.4, detections[first][0][2]])
    sightings = lift_all(scene, detections)
    clusters = build_clusters(group_by_kind(sightings))
    kinds = sorted(c.kind for c in clusters)
    assert kinds == ["shelving unit", "table"]
    table = next(c for c in clusters if c.kind == "table")
    assert len(table.views) >= 3 and table.mean_score == pytest.approx(0.8, abs=0.05)
    # A single weak sighting is not an object.
    one = [s for s in sightings if s.kind == "shelving unit"][:1]
    assert build_clusters(group_by_kind(one)) == []


def test_tracking_follows_an_object_into_every_photo_that_sees_it(scene):
    centre = np.array([0.6, 0.4, 1.0])
    seen = visible_views(scene, centre, 0.8)
    assert len(seen) >= 8
    prompts = pick_prompts(scene, centre, 0.8, [], max_views=6)
    assert len(prompts) == 6 and len({p.view for p in prompts}) == 6
    # Spread round the object, not six neighbours.
    az = sorted(p.azimuth_deg for p in prompts)
    assert max(np.diff(az + [az[0] + 360])) < 200
    assert all(p.source == "projection" for p in prompts)
    box = projected_box(scene, prompts[0].view, np.array([98.0, 130.0]), 2.0, 0.8)
    assert box[2] - box[0] == pytest.approx(box[3] - box[1])


def test_the_photo_and_the_stored_map_agree_on_where_a_pixel_is(scene):
    box = (60.0, 100.0, 120.0, 180.0)
    x0, y0, x1, y1 = stored_to_photo(scene, 0, box)
    assert 0 <= x0 < x1 <= inv_scene.PHOTO_W and 0 <= y0 < y1 <= inv_scene.PHOTO_H
    xy = np.array([[80.5, 120.5], [100.0, 200.0]])
    assert np.allclose(photo_to_stored_xy(scene, 0, stored_to_photo_xy(scene, 0, xy)), xy, atol=1e-6)


# --- masks ---------------------------------------------------------------------------------------------------

def test_a_mask_is_judged_by_its_size_its_place_and_its_overlap_with_the_box():
    box = (40.0, 40.0, 140.0, 120.0)
    good = np.zeros((200, 200), bool)
    good[45:115, 45:135] = True
    assert judge_mask(good, box) == (True, "ok")
    tiny = np.zeros((200, 200), bool)
    tiny[78:82, 88:92] = True
    assert not judge_mask(tiny, box)[0]
    huge = np.ones((200, 200), bool)
    assert "larger" in judge_mask(huge, box)[1]
    elsewhere = np.zeros((200, 200), bool)
    elsewhere[150:195, 150:195] = True
    assert not judge_mask(elsewhere, box)[0]


class TrueSegmenter:
    """Hands back the pixels of the true table whatever box it is asked about, in crop coordinates, via the scene."""

    def __init__(self, fraction: float = 0.8):
        self.fraction, self.seconds, self.peak_mib, self.calls = fraction, 0.0, 0.0, 0

    def mask_in_crop(self, crop, box):
        self.calls += 1
        x0, y0, x1, y1 = box
        mask = np.zeros((crop.height, crop.width), bool)
        w, h = (x1 - x0) * self.fraction, (y1 - y0) * self.fraction
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        mask[int(cy - h / 2):int(cy + h / 2), int(cx - w / 2):int(cx + w / 2)] = True
        return mask

    def close(self):
        pass


def test_masks_are_cached_so_a_second_run_costs_no_gpu_time(scene, tmp_path):
    guard = OutputGuard(tmp_path)
    rel = Path("inventory")
    store = MaskStore(guard, rel, "s-synthetic")
    prompts = {v: [box_stored(scene, v, TABLE_LO, TABLE_HI)] for v in scene.registered[:4] if box_stored(scene, v, TABLE_LO, TABLE_HI)}
    made = []

    def factory():
        made.append(TrueSegmenter())
        return made[-1]

    info = segment_prompts(scene, prompts, store, factory, log=lambda *_: None)
    assert info["new"] == len(prompts) and made[0].calls == len(prompts)
    accepted = [r for r in store.results.values() if r.accepted]
    assert accepted and all(r.mask.shape == scene.map_shape and r.mask.any() and r.pixels.shape[1] == 3 for r in accepted)
    again = MaskStore(guard, rel, "s-synthetic")  # a new run reads the file
    assert set(again.results) == set(store.results)
    info = segment_prompts(scene, prompts, again, factory, log=lambda *_: None)
    assert info["new"] == 0 and len(made) == 1  # no segmenter was even created
    other = MaskStore(guard, rel, "s-another-session")
    assert other.results == {}  # a different session never reuses them


# --- boxes ---------------------------------------------------------------------------------------------------

def make_candidate(kind, lo, hi, hint=None):
    centre = (np.array(lo) + np.array(hi)) / 2
    return Candidate(kind, centre, hint or float(max(np.array(hi) - np.array(lo))), [], "added")


def test_a_perfect_mask_gives_back_the_tables_size_and_place(scene):
    results = true_results(scene, TABLE_LO, TABLE_HI)
    cand = make_candidate("table", TABLE_LO, TABLE_HI)
    fit = fit_candidate(scene, cand, results, [])
    assert fit is not None and len(fit.accepted) >= 6
    w, h, d = sorted(fit.box.size_m)[0], fit.box.size_m[1], max(fit.box.size_m)
    assert fit.box.size_m[1] == pytest.approx(0.75, abs=0.06)
    assert sorted((fit.box.size_m[0], fit.box.size_m[2])) == pytest.approx([0.8, 0.8], abs=0.1)
    assert fit.box.centre_m[0] == pytest.approx(0.6, abs=0.06) and fit.box.centre_m[2] == pytest.approx(1.0, abs=0.06)
    assert fit.box.base_y == 0.0  # a table stands on the floor whatever its lowest visible point says
    assert 0.5 < confidence(scene, fit) <= 1.0


def test_a_small_thing_on_the_table_rests_on_it_and_a_reviewer_can_correct_it(scene):
    lo, hi = (0.45, 0.75, 0.9), (0.75, 0.87, 1.15)
    results = true_results(scene, lo, hi)
    cand = make_candidate("laptop", lo, hi, 0.35)
    fit = fit_candidate(scene, cand, results, [])
    assert fit is not None
    assert fit.box.base_y == pytest.approx(0.75, abs=0.03) and fit.box.size_m[1] == pytest.approx(0.12, abs=0.05)
    fixed = apply_override(fit.box, Override(Near((0.6, 0.8, 1.0), 0.5), size_m=(0.30, 0.02, 0.22), yaw_deg=90.0, note="measured"))
    assert fixed.size_m == (0.30, 0.02, 0.22) and fixed.yaw_deg == 90.0 and fixed.base_y == fit.box.base_y
    assert any("reviewer" in n and "measured" in n for n in fixed.notes)
    # What the fit said about the parts the reviewer measured is dropped: a box set on the floor does not "rest on a surface".
    on_desk = Box((0.6, 0.8, 1.0), (0.3, 0.1, 0.2), 20.0, 0.75, True, ["rests on a surface 0.75 m up", "front away from wall A", "kept"])
    floor = apply_override(on_desk, Override(Near((0.6, 0.8, 1.0), 0.5), base_y=0.0, yaw_deg=0.0))
    assert "kept" in floor.notes and not any(n.startswith(("rests on", "front ")) for n in floor.notes)


def test_too_few_masks_or_points_give_no_box(scene):
    results = true_results(scene, TABLE_LO, TABLE_HI)
    cand = make_candidate("table", TABLE_LO, TABLE_HI)
    assert fit_candidate(scene, cand, results[:2], []) is None
    bad = true_results(scene, TABLE_LO, TABLE_HI, accepted=False)
    assert fit_candidate(scene, cand, bad, []) is None
    # A small thing is allowed two photos, and says so.
    lo, hi = (0.45, 0.75, 0.9), (0.75, 0.87, 1.15)
    two = fit_candidate(scene, make_candidate("jar", lo, hi, 0.2), true_results(scene, lo, hi)[:2], [])
    assert two is not None and any("only 2 photos" in n for n in two.notes)


def test_a_sofa_faces_away_from_its_back_and_a_thing_against_a_wall_faces_into_the_room(scene):
    shelf_lo, shelf_hi = (-2.0, 0.0, -1.0), (-1.55, 1.8, 0.4)
    fit = fit_candidate(scene, make_candidate("shelving unit", shelf_lo, shelf_hi), true_results(scene, shelf_lo, shelf_hi), [])
    assert fit is not None
    wall = nearest_wall(fit.box, scene)
    assert wall is not None and wall[0] == "D"
    from roomscan.inventory.fit import forward_xz
    assert float(forward_xz(fit.box.yaw_deg) @ np.array([1.0, 0.0])) > 0.9  # wall D is at x min: it faces +x
    assert nearest_wall(Box((0.6, 0.4, 1.0), (0.8, 0.8, 0.8), 0.0, 0.0), scene) is None


# --- entries, supports, duplicates, picks ---------------------------------------------------------------------

def entry(ident, kind, conf, *, support="floor", masks=5, size=(0.4, 0.3, 0.3), centre=(0, 0.15, 0), base=0.0):
    return {"id": ident, "kind": kind, "confidence": conf, "evidence": {"masks_accepted": masks},
            "box": {"centre_m": list(centre), "size_m": list(size), "yaw_deg": 0.0,
                    "footprint_m": [centre[0] - size[0] / 2, centre[2] - size[2] / 2, centre[0] + size[0] / 2, centre[2] + size[2] / 2]},
            "placement": {"position_m": [centre[0], base, centre[2]], "support": {"kind": support, "height_m": base}}}


def test_the_five_picks_prefer_the_founders_kinds_then_vary_and_name_what_is_missing():
    entries = [entry("box_floor", "cardboard box", 0.62), entry("box_shelf", "cardboard box", 0.70, support="surface", base=1.9),
               entry("couch_1", "couch", 0.6), entry("laptop_1", "laptop", 0.7, support="surface", base=0.85),
               entry("jar_1", "jar", 0.5, support="surface", base=0.78),
               entry("ac_1", "air conditioner", 0.8), entry("bin_1", "bin", 0.9), entry("chair_1", "office chair", 0.45),
               entry("weak_1", "kettle", 0.2)]
    chosen = picks.pick_five(entries)
    assert [p["kind"] for p in chosen] == ["cardboard box", "couch", "laptop", "jar", "air conditioner"]
    assert chosen[0]["id"] == "box_floor"  # on the floor beats a shelf when they are close
    assert "no french press" in chosen[4]["why"] and "recipe of its own" in chosen[4]["why"]
    # With a French press in the room it is picked, and the fifth is its own kind of thing.
    chosen = picks.pick_five(entries + [entry("press_1", "french press", 0.55, support="surface", base=0.8)])
    assert [p["kind"] for p in chosen] == ["cardboard box", "couch", "laptop", "jar", "french press"]
    # Too little evidence is never picked.
    assert "kettle" not in {p["kind"] for p in picks.pick_five(entries, count=9)}
    # A room with fewer usable things gives fewer picks, not padding.
    assert len(picks.pick_five(entries[1:3])) == 2


def test_a_thing_above_the_floor_sits_on_the_object_under_it():
    desk = entry("desk_1", "desk", 0.7, size=(1.2, 0.75, 0.6), centre=(0, 0.375, 0))
    laptop = entry("laptop_1", "laptop", 0.7, support="surface", size=(0.3, 0.02, 0.2), centre=(0.1, 0.76, 0.0), base=0.75)
    laptop["placement"]["support"]["target_id"] = None
    far = entry("jar_1", "jar", 0.5, support="surface", size=(0.1, 0.1, 0.1), centre=(3, 0.8, 3), base=0.75)
    far["placement"]["support"]["target_id"] = None
    link_supports([desk, laptop, far])
    assert laptop["placement"]["support"] == {"kind": "object", "height_m": 0.75, "target_id": "desk_1"}
    assert far["placement"]["support"]["kind"] == "surface"


def test_overlapping_fits_of_one_object_keep_the_better_evidenced(scene):
    fit_a = fit_candidate(scene, make_candidate("table", TABLE_LO, TABLE_HI), true_results(scene, TABLE_LO, TABLE_HI), [])
    fit_b = fit_candidate(scene, make_candidate("desk", TABLE_LO, TABLE_HI), true_results(scene, TABLE_LO, TABLE_HI)[:5], [])
    kept = drop_overlapping([fit_b, fit_a])
    assert len(kept) == 1 and kept[0].candidate.kind == "table"
    assert "folded" in fit_b.candidate.note


def test_the_inventory_entry_carries_everything_a_stand_in_builder_needs(scene):
    fit = fit_candidate(scene, make_candidate("table", TABLE_LO, TABLE_HI), true_results(scene, TABLE_LO, TABLE_HI), [])
    e = entry_for(scene, fit, "table_1", {})
    assert e["kind"] == "table" and e["recipe"] is None and e["recipe_needed"]["for_kind"] == "table"
    assert set(e["placement"]) >= {"position_m", "yaw_deg", "rotation", "support"} and len(e["placement"]["rotation"]) == 4
    assert abs(np.linalg.norm(e["placement"]["rotation"]) - 1) < 1e-4
    assert e["colours"] and e["colours"][0]["hex"].startswith("#")
    laptop_lo, laptop_hi = (0.45, 0.75, 0.9), (0.75, 0.87, 1.15)
    lap = fit_candidate(scene, make_candidate("laptop", laptop_lo, laptop_hi, 0.35), true_results(scene, laptop_lo, laptop_hi), [])
    e = entry_for(scene, lap, "laptop_1", {})
    recipes.validate_request(e["recipe"])
    assert e["recipe"]["recipe"] == "gaming_laptop" and e["recipe"]["params"] == {"lid_angle_deg": 0.0}
    assert "shell" in e["recipe"]["colours"] and "recipe_needed" not in e
    assert yaw_quaternion(90.0) == pytest.approx([0.0, 0.707107, 0.0, 0.707107], abs=1e-6)


def test_additions_replace_detector_proposals_of_the_same_kind_at_the_same_place(scene):
    from roomscan.inventory.curation import Addition

    detected = Candidate("couch", np.array([0.0, 0.4, 0.0]), 1.8, [(1, 0.9, (0, 0, 10, 10))], "detector", evidence=9.0, mean_score=0.9)
    added = candidates_from_additions([Addition("couch", (0.2, 0.4, 0.1), 1.9, "by the reviewer")])
    merged = merge_duplicates([detected] + added)
    assert len(merged) == 1 and merged[0].source == "added"


# --- review fixes: separate rotated boxes, supports on a turned desk, and the recipe library's own input check -----------------

def make_fit(kind, box, views=5):
    cand = Candidate(kind, np.array(box.centre_m), float(max(box.size_m)), [], "added")
    return Fit(cand, box, np.zeros((0, 3)), np.zeros((0, 3)), np.zeros((0, 3), np.uint8), [object()] * views, [])


def test_separate_rotated_boxes_are_not_folded_into_one():
    from roomscan.inventory.fit import box_overlap_volume, footprint_overlap_area, forward_xz

    yaw = 45.0
    back = -forward_xz(yaw)  # along the box's depth
    a = Box((0.0, 0.25, 0.0), (1.0, 0.5, 0.1), yaw, 0.0)
    gap = Box((0.2 * back[0], 0.25, 0.2 * back[1]), (1.0, 0.5, 0.1), yaw, 0.0)  # 0.2 m along the depth: 0.1 m of daylight between
    assert footprint_overlap_area(a, gap) == 0.0 and box_overlap_volume(a, gap) == 0.0
    # Their bounding squares overlap heavily, which is what the old test measured: over four times the smaller volume.
    ax0, az0, ax1, az1 = a.footprint
    bx0, bz0, bx1, bz1 = gap.footprint
    squares = max(0, min(ax1, bx1) - max(ax0, bx0)) * max(0, min(az1, bz1) - max(az0, bz0)) * 0.5
    assert squares / (1.0 * 0.5 * 0.1) > 4.0
    kept = drop_overlapping([make_fit("table", a), make_fit("table", gap)])
    assert len(kept) == 2
    # Boxes that really share their space are still one object: identical, and half-shifted along the depth.
    assert box_overlap_volume(a, a) == pytest.approx(0.05)
    half = Box((0.05 * back[0], 0.25, 0.05 * back[1]), (1.0, 0.5, 0.1), yaw, 0.0)
    assert box_overlap_volume(a, half) == pytest.approx(0.025)
    near = Box((0.02 * back[0], 0.25, 0.02 * back[1]), (1.0, 0.5, 0.1), yaw, 0.0)  # 80 per cent of the smaller box shared
    assert box_overlap_volume(a, near) == pytest.approx(0.04)
    assert len(drop_overlapping([make_fit("table", a), make_fit("desk", near, views=3)])) == 1
    assert len(drop_overlapping([make_fit("table", a), make_fit("desk", half, views=3)])) == 2  # half shared: two objects side by side
    # Stacked, not shared: a box sitting on another has no volume in common.
    on_top = Box((0.0, 0.75, 0.0), (1.0, 0.5, 0.1), yaw, 0.5)
    assert box_overlap_volume(a, on_top) == 0.0 and len(drop_overlapping([make_fit("table", a), make_fit("box", on_top)])) == 2


def test_a_thing_sits_on_a_turned_desk_only_where_the_desk_really_is():
    desk = entry("desk_1", "desk", 0.7, size=(1.2, 0.75, 0.6), centre=(0.0, 0.375, 0.0))
    desk["box"]["yaw_deg"] = 45.0
    inside = entry("laptop_1", "laptop", 0.7, support="surface", size=(0.3, 0.02, 0.2), centre=(0.2, 0.76, 0.0), base=0.75)
    corner = entry("jar_1", "jar", 0.5, support="surface", size=(0.1, 0.1, 0.1), centre=(0.6, 0.8, 0.0), base=0.75)  # in the bounding square, off the desk
    for e in (inside, corner):
        e["placement"]["support"]["target_id"] = None
    link_supports([desk, inside, corner])
    assert inside["placement"]["support"]["target_id"] == "desk_1"
    assert corner["placement"]["support"]["kind"] == "surface" and corner["placement"]["support"]["target_id"] is None


def recipe_library():
    """The recipe library's own input validation (pipeline/recipes/specs.py, standard library only), loaded without Blender."""
    import importlib.util

    from roomscan.paths import find_repo_root

    path = find_repo_root() / "pipeline" / "recipes" / "specs.py"
    assert path.is_file(), "pipeline/recipes/specs.py is missing: merge run2/integration into this branch"
    spec = importlib.util.spec_from_file_location("garage_game_recipe_specs", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_the_requests_pass_the_recipe_librarys_own_input_check(scene):
    library = recipe_library()
    samples = {
        "cardboard_box": ((0.42, 0.34, 0.32), {"cardboard": "#9c7958", "edges": "#a8896c"}),
        "couch": ((1.85, 0.78, 0.95), {"upholstery": "#9a9287", "legs": "#775035"}),
        "gaming_laptop": ((0.30, 0.022, 0.22), {"shell": "#161411"}),
        "jam_jar": ((0.10, 0.14, 0.10), {}),
        "french_press": ((0.12, 0.25, 0.11), {"frame": "#aeb5bd"}),
    }
    assert set(samples) == set(recipes.SLOTS) == set(library.RECIPES)
    for name, (size, colours) in samples.items():
        request = recipes.request_for(name, size, colours)
        used = library.resolve(json.loads(json.dumps(request)))
        values = library.values(used)
        assert values["recipe"] == name and values["size_m"] == list(size) and used["style"]["source"] == "default"
        assert all(used["colours"][slot]["source"] == "given" for slot in colours)
        assert all(used["params"][p]["source"] == "given" for p in request["params"])
    # A laptop entry made from a fitted box, end to end, is accepted too.
    lo, hi = (0.45, 0.75, 0.9), (0.75, 0.87, 1.15)
    fit = fit_candidate(scene, make_candidate("laptop", lo, hi, 0.35), true_results(scene, lo, hi), [])
    assert library.resolve(entry_for(scene, fit, "laptop_1", {})["recipe"])


def test_our_copy_of_the_recipe_tables_agrees_with_the_library_and_both_refuse_the_same_bad_input():
    library = recipe_library()
    for name, slots in recipes.SLOTS.items():
        assert set(slots) == set(library.RECIPES[name]["colours"]), name
        for key, rule in recipes.PARAMS[name].items():
            theirs = library.RECIPES[name]["params"][key]
            if isinstance(rule[0], str):
                assert tuple(rule) == tuple(theirs[1]), (name, key)
            else:
                assert (rule[0], rule[1]) == (theirs[1], theirs[2]) and rule[2] == (type(theirs[0]) is int), (name, key)
    good = {"recipe": "couch", "size_m": [1.8, 0.8, 0.9], "colours": {"upholstery": "#aabbcc"}, "params": {"cushion_count": 3}}
    bad = [dict(good, size_m=[1.8, 0.8]), dict(good, size_m=[1.8, 0.001, 0.9]), dict(good, size_m=[1.8, 0.8, 99]),
           dict(good, colours={"seat": "#aabbcc"}), dict(good, colours={"legs": "blue"}),
           dict(good, params={"cushion_count": 9}), dict(good, params={"cushion_count": 2.5}), dict(good, params={"mystery": 1}),
           dict(good, extra=1), dict(good, recipe="sofa")]
    for request in bad:
        with pytest.raises(recipes.RequestError):
            recipes.validate_request(request)
        with pytest.raises(ValueError):
            library.resolve(request)
    recipes.validate_request(good)
    library.resolve(good)
