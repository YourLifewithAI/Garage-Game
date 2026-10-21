"""Objects, guidance wording and the GPU-free parts of the backend, without a model."""

from __future__ import annotations

import numpy as np
import pytest
from PIL import Image

from roomscan import exif as exifmod
from roomscan.coverage import backend as be
from roomscan.coverage import guidance as gd
from roomscan.coverage.detect import extend_to_wall
from roomscan.coverage.grid import wall_axes
from roomscan.coverage.objects import Detection, ObjectInstance, build_instances, cluster, measure_views, sector_of

BOUNDS = (-2.0, 2.0, -2.5, 2.5)


def _obj(label, lo, hi, wall=None, oid=None):
    o = ObjectInstance(oid or label.replace(" ", "_"), label, [], np.array(lo, float), np.array(hi, float))
    o.wall = wall
    return o


def test_sectors_follow_the_viewer_facing_the_object():
    assert sector_of(0) == "front" and sector_of(50) == "front-left" and sector_of(-90) == "right side"
    assert sector_of(180) == "back" and sector_of(-170) == "back" and sector_of(400) == "front-left"
    # An object against wall D (x min) faces +x. A camera to its +z side is on the viewer's left
    # when they face the object (looking toward -x, their left is +z).
    o = _obj("shelving unit", (-2.0, 0.0, -0.5), (-1.6, 1.8, 0.5))
    pts = {0: np.array([[-1.7, 1.0, 0.0]] * 80), 1: np.array([[-1.7, 1.0, 0.0]] * 80)}
    cams = {0: np.array([-0.5, 1.5, 0.0]), 1: np.array([-1.0, 1.5, 1.4])}
    measure_views(o, pts, cams, np.array([0.0, 1.0, 0.0]), wall_axes("D", BOUNDS), min_points=30)
    assert [sector_of(a) for a in o.view_angles_deg] == ["front", "front-left"]


def test_only_well_evidenced_objects_are_kept_once():
    def det(view, label, score, c, half=0.4):
        d = Detection(view, label, score, (0, 0, 1, 1))
        d.centre = np.array(c, float)
        d.lo, d.hi = d.centre - half, d.centre + half
        return d

    dets = ([det(v, "table", 0.3, (0.05 * v, 0.4, 0)) for v in range(5)]  # many weak sightings: kept
            + [det(v, "desk", 0.25, (0.05 * v, 0.45, 0.02)) for v in range(3)]  # same place, other name: merged
            + [det(7, "sofa", 0.6, (2.0, 0.4, 2.0)), det(8, "sofa", 0.55, (2.05, 0.4, 2.0))]  # two confident photos
            + [det(9, "chair", 0.25, (-1, 0.5, -1))]  # one weak sighting: dropped
            + [det(10, "bin", 0.9, (-1.5, 0.3, 1.5))])  # one photo, however confident: dropped
    objs = build_instances(cluster(dets))
    assert sorted(o.label for o in objs) == ["sofa", "table"]
    assert [o.id for o in objs if o.label == "table"] == ["table"]  # a single table is not numbered


def test_tall_furniture_near_a_wall_is_extended_to_it():
    shelf = _obj("shelving unit", (-1.55, 0.0, -1.0), (-1.5, 1.8, 0.4), wall="D")
    extend_to_wall(shelf, BOUNDS)
    assert shelf.lo[0] == -2.0
    table = _obj("table", (-1.4, 0.0, -1.0), (-0.6, 0.75, 0.4), wall="D")
    extend_to_wall(table, BOUNDS)
    assert table.lo[0] == -1.4  # low and 0.6 m from the wall: left where it is


def test_words_and_distances_read_naturally():
    assert gd.words(3) == "three" and gd.words(21) == "21"
    assert gd.metres(0.9) == "one metre" and gd.metres(1.4) == "one and a half metres" and gd.metres(0.2) == "half a metre"
    assert gd._join(["a", "b", "c"]) == "a, b and c"


def test_span_phrases_name_furniture_on_the_wall():
    axes = wall_axes("D", BOUNDS)
    shelf = _obj("shelving unit", (-2.0, 0, -0.5), (-1.6, 1.8, 0.5), wall="D")
    door = _obj("door", (-2.0, 0, 1.5), (-1.9, 2.0, 2.3), wall="D")
    prof = {"objects": [shelf, door]}
    lo_s, hi_s = gd.object_u_range(shelf, axes)
    lo_d, hi_d = gd.object_u_range(door, axes)
    assert hi_s <= lo_d or hi_d <= lo_s
    left, right = sorted([(lo_s, hi_s, "shelving unit"), (lo_d, hi_d, "door")])
    between = gd.span_phrase(left[1] + 0.1, right[0] - 0.1, prof, axes)
    assert between.startswith(f"between the {left[2]} and the {right[2]}")
    assert gd.span_phrase(0.0, axes["length"], {"objects": []}, axes) == "along its whole length"
    assert "around the shelving unit" in gd.span_phrase(lo_s, hi_s, prof, axes)


def test_low_lap_is_asked_for_only_when_few_photos_are_low():
    looks = {i: np.array([0, 0, -1.0]) for i in range(10)}
    high = {i: np.array([0, 1.5, 0]) for i in range(10)}
    ctx = {"cams": high, "registered": list(range(10)), "looks": looks}
    item = gd.height_item(ctx)
    assert item and "knee height" in item[0]["text"] and "10 cm" in item[0]["why"]
    mixed = {i: np.array([0, 0.4 if i < 3 else 1.5, 0]) for i in range(10)}
    assert gd.height_item({"cams": mixed, "registered": list(range(10)), "looks": looks}) == []


def test_notes_flag_lenses_blur_and_copies():
    m = {"flags": {"blurry": ["a.jpg"]}, "summary": {"files": 4},
         "duplicates": {"exact": [["b.jpg", "b (1).jpg"]]},
         "photos": [{"name": "c.jpg", "status": "ok", "lens_kind": "front"},
                    {"name": "d.jpg", "status": "ok", "lens_kind": "ultra_wide"}]}
    kinds = [n["kind"] for n in gd.notes({"manifest": m})]
    assert kinds == ["retake", "lens", "lens", "duplicates"]


def test_model_input_geometry_and_exif_intrinsics():
    lay = be.input_layout(4284, 5712)  # an iPhone 3:4 portrait: scaled to cover and trimmed by a pixel or two
    assert (lay.pad_x, lay.pad_y) == (0, 0) and lay.left <= 1 and lay.top <= 3
    K = be.exif_intrinsics({"exif": {"focal_length_35mm": 26}, "image": {"width": 4284, "height": 5712}})
    w, h = be.TARGET_SIZE
    assert K[0, 2] == pytest.approx(w / 2, abs=1.0) and K[1, 2] == pytest.approx(h / 2, abs=1.0)
    # 26 mm equivalent on a 3:4 portrait frame: about 53 degrees across the short side.
    fov = 2 * np.degrees(np.arctan(w / 2 / K[0, 0]))
    assert fov == pytest.approx(53.0, abs=1.5)
    assert be.exif_intrinsics({"exif": {}, "image": {"width": 10, "height": 10}}) is None
    assert be.batch_size_for(8192) == 32 and be.batch_size_for(5000) == 8


def test_digitally_zoomed_photos_get_no_exif_intrinsics():
    # What the garage set records: 1.42 with the unchanged 26 mm, and 1.71 with 26 x 1.71 = 44 mm.
    # The two disagree about whether the equivalent includes the zoom, so neither is trusted.
    shot = {"image": {"width": 4284, "height": 5712}}
    plain = {**shot, "exif": {"focal_length_35mm": 26, "digital_zoom": 1.001}}
    cropped = {**shot, "exif": {"focal_length_35mm": 26, "digital_zoom": 1.417}}
    pinched = {**shot, "exif": {"focal_length_35mm": 44, "digital_zoom": 1.711}}
    unrecorded = {**shot, "exif": {"focal_length_35mm": 26, "digital_zoom": None}}
    assert be.exif_intrinsics(plain) is not None and be.exif_intrinsics(unrecorded) is not None
    assert be.exif_intrinsics(cropped) is None and be.exif_intrinsics(pinched) is None
    assert exifmod.digitally_zoomed({"digital_zoom": 1.091}) and not exifmod.digitally_zoomed({"digital_zoom": 1.001})
    assert not exifmod.digitally_zoomed(None) and not exifmod.digitally_zoomed({})


def test_landscape_and_tall_photos_are_padded_not_cropped():
    tw, th = be.TARGET_SIZE
    # A landscape photo cropped to 3:4 portrait would keep only 57% of its width; padded, it keeps it all.
    lay = be.input_layout(5712, 4284)
    assert lay.scale == pytest.approx(tw / 5712, rel=1e-3)
    assert (lay.left, lay.top) == (0, 0) and lay.content_w == tw and lay.content_h < th and lay.pad_y > 0
    tall = be.input_layout(2268, 4032)  # the front camera's 9:16 frame
    assert tall.content_h == th and tall.content_w < tw and tall.pad_x > 0
    # The photo's own pixels sit in the middle of the input, and the mask says so at the stored size.
    mask = be.content_mask(lay)
    stride = be.STORE_STRIDE
    assert mask.shape == (th // stride, tw // stride)
    rows = np.flatnonzero(mask.any(1))
    assert rows[0] * stride == pytest.approx(lay.pad_y, abs=stride) and mask[:, :].any(0).all()
    assert mask.sum() * stride * stride == pytest.approx(lay.content_w * lay.content_h, rel=0.03)
    assert be.content_mask(be.input_layout(4284, 5712)).all()  # a 3:4 photo has no padding to hide


def test_a_landscape_photo_keeps_its_edges_in_the_model_input(tmp_path):
    # Bright bars at the far left and far right of a 4:3 landscape photo must both survive.
    img = Image.new("RGB", (640, 480), (90, 90, 90))
    img.paste((255, 0, 0), (0, 0, 8, 480))
    img.paste((0, 0, 255), (632, 0, 640, 480))
    path = tmp_path / "wide.jpg"
    img.save(path, quality=95)
    canvas, layout = be.load_model_view(path)
    arr = np.asarray(canvas).astype(int)
    assert canvas.size == be.TARGET_SIZE
    mid = layout.pad_y + layout.content_h // 2
    assert arr[mid, 1, 0] > 200 and arr[mid, 1, 2] < 80  # red at the left edge
    assert arr[mid, -2, 2] > 200 and arr[mid, -2, 0] < 80  # blue at the right edge
    assert tuple(arr[2, 100]) == be.PAD_COLOUR  # above the photo: padding, not picture
    # The principal point of EXIF intrinsics follows the photo, not the padded frame's corner.
    K = be.exif_intrinsics({"exif": {"focal_length_35mm": 26}, "image": {"width": 5712, "height": 4284}})
    assert K[0, 2] == pytest.approx(be.TARGET_SIZE[0] / 2, abs=1.0) and K[1, 2] == pytest.approx(be.TARGET_SIZE[1] / 2, abs=1.0)


def test_detection_image_is_fitted_like_the_model_input(tmp_path):
    from roomscan.coverage.objects import detection_factor, detection_image

    Image.new("RGB", (800, 600), (120, 120, 120)).save(tmp_path / "wide.jpg")
    canvas, factor = detection_image(tmp_path / "wide.jpg")
    assert canvas.size[0] / canvas.size[1] == pytest.approx(be.TARGET_SIZE[0] / be.TARGET_SIZE[1], abs=0.01)
    assert factor == detection_factor()
    assert tuple(np.asarray(canvas)[2, 5]) == be.PAD_COLOUR  # the landscape photo is padded here too


def test_draft_decoding_keeps_enough_pixels(tmp_path):
    big = Image.fromarray(np.random.default_rng(0).integers(0, 255, (3200, 2400, 3), dtype=np.uint8))
    path = tmp_path / "big.jpg"
    big.save(path, quality=80)
    with be.open_reduced(path, be.TARGET_SIZE) as im:
        assert im.size[0] >= 2 * be.TARGET_SIZE[0] - 1 and im.size[0] < 2400
    assert be.load_model_view(path)[0].size == be.TARGET_SIZE


def test_game_window_check_needs_the_game_program():
    playing = [("EnFractal (DEBUG)", "Godot_v4.7.2-stable_mono_win64.exe"), ("Inbox", "outlook.exe")]
    assert be.game_window_open(playing) is True
    assert be.game_window_open([("EnFractal", "EnFractal.exe")]) is True
    assert be.game_window_open([("Garage Game - The garage", "Godot_v4.7.2-stable_mono_win64.exe")]) is True
    # A folder or an editor named after the project is not the game.
    assert be.game_window_open([("EnFractal", "explorer.exe"), ("EnFractal - VS Code", "Code.exe")]) is False
    if be.sys.platform.startswith("win"):
        import time

        t0 = time.perf_counter()
        windows = be.visible_windows()  # the real call: fast, and returns (title, program) pairs
        assert time.perf_counter() - t0 < 2.0
        assert all(isinstance(t, str) and isinstance(e, str) for t, e in windows)


def test_torch_hub_is_pinned_to_a_commit():
    torch = pytest.importorskip("torch")
    calls = []
    real = torch.hub.load
    torch.hub.load = lambda repo, model, *a, **k: calls.append((repo, model, k)) or "model"
    try:
        with be.pinned_torch_hub():
            torch.hub.load("facebookresearch/dinov2", "dinov2_vitg14", pretrained=False)
            torch.hub.load("someone/else", "x")
    finally:
        torch.hub.load = real
    ref = be.TORCH_HUB_PINS["facebookresearch/dinov2"]["ref"]
    assert calls[0][0] == f"facebookresearch/dinov2:{ref}" and calls[0][2]["skip_validation"] is True
    assert calls[0][2]["force_reload"] is False
    assert calls[1][0] == "someone/else"


def test_every_downloaded_model_has_a_recorded_licence():
    for model, info in be.MODEL_REGISTRY.items():
        assert info["licence"] and info["revision"] and "NC" not in info["licence"], model
    assert all("NC" in why or "gated" in why for why in be.REJECTED_MODELS.values())
    # The pinned DINOv2 checkout is Apache-2.0, but its hubconf.py imports the Cell-DINO (CC BY 4.0 code)
    # and X-Ray-DINO modules, whose weights are non-commercial. None are fetched; the record says so.
    for pin in be.TORCH_HUB_PINS.values():
        assert pin["ref"] and pin["licence"]
    note = be.TORCH_HUB_PINS["facebookresearch/dinov2"]["note"]
    assert "Cell-DINO" in note and "X-Ray-DINO" in note and "non-commercial" in note and "no weights are fetched" in note


def test_walls_a_and_d_put_the_left_object_on_the_left():
    # Facing wall A (the top of the map) the viewer's right is +x; facing wall D it is -z. The old span
    # test sorted both objects, so it could not tell a flipped wall from a correct one.
    bounds = (-2.0, 2.0, -2.5, 2.5)
    axes_a = wall_axes("A", bounds)  # 4 m long, left end at x = -2
    shelf = _obj("shelving unit", (-2.0, 0.0, -2.5), (-1.4, 1.8, -2.0), wall="A")
    door = _obj("door", (1.2, 0.0, -2.5), (1.9, 2.0, -2.4), wall="A")
    assert gd.object_u_range(shelf, axes_a) == pytest.approx((0.0, 0.6))
    assert gd.object_u_range(door, axes_a) == pytest.approx((3.2, 3.9))
    both = {"objects": [door, shelf]}  # the order of the list must not matter
    assert gd.span_phrase(0.8, 3.0, both, axes_a).startswith("between the shelving unit and the door")
    assert gd.span_phrase(0.8, 3.0, {"objects": [shelf]}, axes_a).startswith("to the right of the shelving unit")
    assert gd.span_phrase(0.8, 3.0, {"objects": [door]}, axes_a).startswith("to the left of the door")

    axes_d = wall_axes("D", bounds)  # 5 m long, left end at z = +2.5
    bench = _obj("workbench", (-2.0, 0.0, 1.5), (-1.5, 0.9, 2.3), wall="D")
    garage_door = _obj("garage door", (-2.0, 0.0, -2.3), (-1.9, 2.1, -1.5), wall="D")
    assert gd.object_u_range(bench, axes_d) == pytest.approx((0.2, 1.0))
    assert gd.object_u_range(garage_door, axes_d) == pytest.approx((4.0, 4.8))
    assert gd.span_phrase(1.4, 3.6, {"objects": [garage_door, bench]}, axes_d).startswith(
        "between the workbench and the garage door")
    assert gd.span_phrase(1.4, 3.6, {"objects": [bench]}, axes_d).startswith("to the right of the workbench")
    assert gd.span_phrase(1.4, 3.6, {"objects": [garage_door]}, axes_d).startswith("to the left of the garage door")


def test_around_is_said_only_of_an_object_over_the_middle_of_the_stretch():
    # The garage's step 5: a suitcase covering 2.30 to 3.21 m of a stretch from 3.0 m to the right-hand
    # corner. It only touches the stretch, so the stretch is to its right, "up to the corner", not "around" it.
    bounds = (-2.0, 3.1, -2.0, 2.0)  # wall A is 5.1 m long
    axes = wall_axes("A", bounds)
    suitcase = _obj("suitcase", (0.3, 0.0, -2.0), (1.21, 0.4, -1.5), wall="A")
    assert gd.object_u_range(suitcase, axes) == pytest.approx((2.3, 3.21))
    phrase = gd.span_phrase(3.0, 5.1, {"objects": [suitcase]}, axes)
    assert phrase.startswith("to the right of the suitcase, up to the right-hand corner")
    assert "around" not in phrase
    # An object over the middle is the one the stretch is around, and one at its end is not.
    box = _obj("box", (1.5, 0.0, -2.0), (2.5, 0.5, -1.5), wall="A")  # u 3.5 to 4.5, over the middle (4.0)
    assert gd.span_phrase(3.0, 5.0, {"objects": [box]}, axes).startswith("around the box")
    left_end = _obj("box", (-2.0, 0.0, -2.0), (-0.9, 0.5, -1.5), wall="A")  # u 0 to 1.1, a stretch of 1.0 to 3.0
    assert gd.span_phrase(1.0, 3.0, {"objects": [left_end]}, axes).startswith("to the right of the box")
    assert "up to" not in gd.span_phrase(1.0, 3.0, {"objects": [left_end]}, axes)  # that stretch is not at a corner


def _look(horizontal_toward: np.ndarray, down: float = 0.998) -> np.ndarray:
    """A unit view direction pointing nearly straight down, with a sliver of level direction."""
    sliver = np.sqrt(1 - down ** 2)
    return np.array([horizontal_toward[0] * sliver, -down, horizontal_toward[1] * sliver])


def test_a_photo_of_the_floor_is_not_cited_for_a_wall():
    # IMG_2834 points straight down; the level part of its view, normalised, scored as facing wall D.
    toward_wall = np.array([-1.0, 0.0])  # facing wall D, which is at x min
    context = {
        "registered": [0, 1], "views": [{"name": "IMG_2834"}, {"name": "IMG_2801"}],
        "cams": {0: np.array([-0.8, 1.5, 0.0]), 1: np.array([0.4, 1.4, 0.2])},
        "looks": {0: _look(toward_wall), 1: np.array([-1.0, 0.0, 0.0])},
    }
    stand = np.array([-0.8, 0.0])
    assert gd.nearest_photo(context, stand) == "IMG_2834"  # with no direction asked for, it is the closest
    assert gd.nearest_photo(context, stand, toward_wall) == "IMG_2801"  # for a wall, the one that faced it
    assert gd.nearest_photo({**context, "registered": [0]}, stand, toward_wall) is None  # nothing else to cite
    # A photo that looks mostly level, a little downward, still counts.
    tilted = {**context, "looks": {**context["looks"], 0: np.array([-0.95, -0.3, 0.0])}}
    assert gd.nearest_photo(tilted, stand, toward_wall) == "IMG_2834"
    assert gd.MIN_HORIZONTAL_LOOK == 0.5


def test_there_is_one_low_photo_threshold():
    from roomscan.coverage import render, report, visibility

    assert visibility.LOW_CAMERA_M == 0.8
    assert gd.LOW_CAMERA_M == render.LOW_CAMERA_M == report.LOW_CAMERA_M == visibility.LOW_CAMERA_M
    # A photo at 0.75 m is low everywhere: it counts toward the low lap and the text says 0.8 m.
    looks = {i: np.array([0, 0, -1.0]) for i in range(10)}
    at_075 = {i: np.array([0, 0.75 if i < 3 else 1.5, 0]) for i in range(10)}
    assert gd.height_item({"cams": at_075, "registered": list(range(10)), "looks": looks}) == []
    all_high = {i: np.array([0, 1.5, 0]) for i in range(10)}
    item = gd.height_item({"cams": all_high, "registered": list(range(10)), "looks": looks})
    assert "below 0.8 m" in item[0]["why"]


def test_every_wall_step_gets_a_marker_on_the_unfolded_walls():
    from roomscan.coverage import render

    guidance = {"items": [
        {"kind": "wall", "wall": "D", "span_m": [1.0, 2.0], "bands": ["low", "middle"], "number": 1},
        {"kind": "low_wall", "wall": "D", "span_m": [3.0, 4.0], "bands": ["low"], "number": 2},
        {"kind": "wall", "wall": "B", "span_m": [0.0, 1.0], "bands": ["high"], "number": 3},
        {"kind": "floor", "number": 4, "target": [0, 0]}]}
    d = render.wall_markers(guidance, "D", 2.5)
    assert [(u, n) for u, _, n in d] == [(1.5, 1), (3.5, 2)]  # the low-wall step has its marker too
    assert d[0][1] == pytest.approx((0.3 + 1.1) / 2) and d[1][1] == pytest.approx(0.3)
    assert render.wall_markers(guidance, "B", 2.5)[0][2] == 3 and render.wall_markers(guidance, "A", 2.5) == []
    # The ceiling grid is drawn like the map, which is the view from above; it must not say "from below".
    assert "below" not in render.CEILING_TITLE.lower() and "from above" in render.CEILING_TITLE


def test_notes_say_which_photos_were_zoomed():
    m = {"flags": {"blurry": []}, "summary": {"files": 3}, "duplicates": {"exact": []},
         "photos": [{"name": "a.jpg", "status": "ok", "exif": {"digital_zoom": 1.417}},
                    {"name": "b.jpg", "status": "ok", "exif": {"digital_zoom": 1.001}},
                    {"name": "c.jpg", "status": "duplicate", "exif": {"digital_zoom": 1.417}},
                    {"name": "d.jpg", "status": "ok", "exif": {"digital_zoom": 1.711}, "quality": {"flags": ["blurry"]}}]}
    (note,) = gd.notes({"manifest": m})  # d.jpg was zoomed too, but it was left out as blurry, so it is not listed
    assert note["kind"] == "zoom" and note["photos_named"] == ["a.jpg"] and "without their lens data" in note["text"]
