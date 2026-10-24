"""C3, the shell, on synthetic rooms: planes checked against points, the manifest, its openings, spawns and the contract.

Everything is made from a ray-cast box room (tests/scene.py, tests/inv_scene.py). No real photo or place is involved.
"""

from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

import inv_scene
import scene as sc
from roomscan.coverage.run import run_coverage
from roomscan.paths import find_repo_root
from roomscan.scene import SceneMismatch, load_scene
from roomscan.shell import manifest as mf
from roomscan.shell.export import check_with_contract, export_shell, yaw_toward
from roomscan.shell.planes import ShellPlan, Surface, WALL_KEYS, plan_shell
from roomscan.shell.spec import SpecError, parse_spec

X0, X1, Z0, Z1, H = -2.0, 2.0, -2.5, 2.5, 2.5


def surfaces():
    out = [Surface("shell:floor", "floor", "floor", [("#8c8a86", 1.0)]), Surface("shell:ceiling", "ceiling", "ceiling", [("#e8e6e0", 1.0)])]
    out += [Surface(f"shell:wall_{k.lower()}", "wall", k, [("#b8b0a4", 0.7), ("#6a645c", 0.3)]) for k in WALL_KEYS]
    return out


@pytest.fixture()
def plan():
    return ShellPlan(X0, X1, Z0, Z1, H, surfaces(), 1.0, "seed_batch")


def spec_doc(**extra):
    doc = {"schema": "garage_game.shell_spec", "version": 1, "room": "garage", "session_id": "s-synthetic",
           "site": {"latitude_deg": 30, "neg_z_bearing_deg": 0, "solar_noon_h": 12},
           "openings": [
               {"id": "window_a", "kind": "window", "wall": "A", "u_m": 2.0, "width_m": 1.0, "v_bottom_m": 0.9, "height_m": 1.2,
                "state": "fixed", "traversable": False},
               {"id": "front_door", "kind": "door", "wall": "B", "u_m": 1.5, "width_m": 0.8, "v_bottom_m": 0.0, "height_m": 2.0,
                "state": "closed", "traversable": False},
               {"id": "big_door", "kind": "garage_door", "wall": "D", "u_m": 2.5, "width_m": 3.0, "v_bottom_m": 0.0, "height_m": 2.1,
                "state": "closed", "traversable": False}],
           "lamps": [{"id": "ceiling_light", "position_m": [0.0, 2.4, 0.0]}]}
    doc.update(extra)
    return doc


def fake_scene():
    photos = [{"status": "ok", "exif": {"make": "Apple", "model": "Synthetic phone"}} for _ in range(5)]
    return SimpleNamespace(session_id="s-synthetic", registered=[0, 1, 2], manifest={"photos": photos})


def newell(points):
    n = np.zeros(3)
    for i, p in enumerate(points):
        q = points[(i + 1) % len(points)]
        n += [(p[1] - q[1]) * (p[2] + q[2]), (p[2] - q[2]) * (p[0] + q[0]), (p[0] - q[0]) * (p[1] + q[1])]
    return n / np.linalg.norm(n)


# --- the spec --------------------------------------------------------------------------------------------------

def test_the_spec_is_read_strictly():
    spec = parse_spec(spec_doc())
    assert [o.id for o in spec.openings] == ["window_a", "front_door", "big_door"] and spec.site["latitude_deg"] == 30
    bad = [
        spec_doc(site={"latitude_deg": 30.5, "neg_z_bearing_deg": 0, "solar_noon_h": 12}),  # a finer latitude than the contract allows
        spec_doc(site={"latitude_deg": 30, "neg_z_bearing_deg": 0, "solar_noon_h": 12.1}),  # not a quarter hour
        spec_doc(site={"latitude_deg": 30, "neg_z_bearing_deg": 0, "solar_noon_h": 12, "longitude_deg": -97}),  # a longitude
        spec_doc(openings=[dict(spec_doc()["openings"][0], kind="skylight")]),
        spec_doc(openings=[dict(spec_doc()["openings"][0], wall="E")]),
        spec_doc(openings=[dict(spec_doc()["openings"][0], extra=1)]),
        spec_doc(openings=[spec_doc()["openings"][0]] * 2),  # the same id twice
        spec_doc(lamps=[{"id": "x", "position_m": [0, 2]}]),
        spec_doc(surface_colours={"A": "red"}),
        spec_doc(mystery=True),
        {"schema": "other", "version": 1, "room": "garage", "session_id": "s-x"},
        spec_doc(schema="garagegame.shell_spec"),
    ]
    # The founder's spec written before the rename (decision C) names its kind the old way: read as it is.
    assert parse_spec(spec_doc(schema="enfractal.shell_spec")) == spec
    for doc in bad:
        with pytest.raises(SpecError):
            parse_spec(doc)


# --- the manifest ------------------------------------------------------------------------------------------------

def test_every_shell_polygon_faces_into_the_room_and_walls_run_past_each_other_at_the_corners(plan):
    parts = {p["id"]: p for p in mf.shell_parts(plan, parse_spec(spec_doc()))}
    assert set(parts) == {"shell:floor", "shell:ceiling", *(f"shell:wall_{k.lower()}" for k in WALL_KEYS)}
    assert np.allclose(newell(parts["shell:floor"]["geometry"]["points_m"]), [0, 1, 0])
    assert np.allclose(newell(parts["shell:ceiling"]["geometry"]["points_m"]), [0, -1, 0])
    for key in WALL_KEYS:
        points = parts[f"shell:wall_{key.lower()}"]["geometry"]["points_m"]
        assert np.allclose(newell(points), mf.wall_frame(plan, key)["n"]), key
        ys = [p[1] for p in points]
        assert min(ys) == 0.0 and max(ys) == H
    # A and C (the z walls) run 12 cm past the inside corner; B and D stop at it.
    a = parts["shell:wall_a"]["geometry"]["points_m"]
    assert min(p[0] for p in a) == pytest.approx(X0 - 0.12) and max(p[0] for p in a) == pytest.approx(X1 + 0.12)
    b = parts["shell:wall_b"]["geometry"]["points_m"]
    assert min(p[2] for p in b) == pytest.approx(Z0) and max(p[2] for p in b) == pytest.approx(Z1)
    assert parts["shell:wall_a"]["base_color"] == "#b8b0a4" and parts["shell:floor"]["material_role"] == "concrete"


def test_an_opening_sits_on_its_wall_plane_at_the_height_the_spec_says(plan):
    spec = parse_spec(spec_doc())
    openings = {o["id"]: o for o in mf.build_openings(plan, spec)}
    window = openings["window_a"]
    assert window["host_part_id"] == "shell:wall_a" and window["center_m"][2] == pytest.approx(Z0)
    assert window["center_m"][0] == pytest.approx(X0 + 2.0) and window["center_m"][1] == pytest.approx(0.9 + 0.6)
    assert window["size_m"] == [1.0, 1.2]
    door = openings["front_door"]  # wall B stands at x max; u runs along +z from z min
    assert door["center_m"][0] == pytest.approx(X1) and door["center_m"][2] == pytest.approx(Z0 + 1.5) and door["center_m"][1] == pytest.approx(1.0)
    big = openings["big_door"]  # wall D stands at x min; u runs along -z from z max
    assert big["center_m"][0] == pytest.approx(X0) and big["center_m"][2] == pytest.approx(Z1 - 2.5)


def test_openings_that_run_off_a_wall_or_overlap_are_refused(plan):
    window = spec_doc()["openings"][0]
    for opening, why in [(dict(window, u_m=0.2), "runs off"), (dict(window, v_bottom_m=1.8), "taller than the room"),
                         (dict(window, u_m=4.9, width_m=0.5, id="far"), "runs off")]:
        with pytest.raises(mf.ShellError, match=why):
            mf.build_openings(plan, parse_spec(spec_doc(openings=[opening])))
    with pytest.raises(mf.ShellError, match="overlap"):
        mf.build_openings(plan, parse_spec(spec_doc(openings=[window, dict(window, id="window_b", u_m=2.4)])))
    mf.build_openings(plan, parse_spec(spec_doc(openings=[window, dict(window, id="window_b", u_m=3.1)])))  # side by side: fine


def test_windows_light_the_room_and_a_sun_is_always_hinted(plan):
    hints = {h["id"]: h for h in mf.light_hints(plan, parse_spec(spec_doc()))}
    assert set(hints) == {"window_a_light", "ceiling_light", "sun"}  # a door gives no light
    outside = hints["window_a_light"]["position_m"]
    assert outside[2] < Z0  # outside the wall, aimed into the room (+z) and a little down
    assert hints["window_a_light"]["direction"][2] > 0 > hints["window_a_light"]["direction"][1]
    assert all(h["estimated"] for h in hints.values())


def build(plan, **extra):
    spec = parse_spec(spec_doc(**extra))
    spawns = [{"id": "player_start", "role": "player", "position_m": [0.5, 0.0, 0.5], "yaw_deg": 45.0},
              {"id": "companion_start", "role": "companion", "position_m": [1.0, 0.0, 0.5], "yaw_deg": 45.0}]
    return mf.build_manifest(plan, spec, fake_scene(), created_utc="2026-10-08T00:00:00Z", spawns=spawns)


def test_the_manifest_validates_against_the_contract_and_is_the_same_bytes_every_time(plan, tmp_path):
    room = build(plan)
    written = mf.write_room(tmp_path / "rooms", room)
    path, sha = written.path, written.sha256
    assert written.published and written.problems == []
    assert path == tmp_path / "rooms" / "garage" / "room.json"
    assert check_with_contract(path.parent) == []
    again_written = mf.write_room(tmp_path / "rooms2", build(plan))
    again, sha2 = again_written.path, again_written.sha256
    assert sha == sha2 and path.read_bytes() == again.read_bytes()
    raw = path.read_bytes()
    assert raw.endswith(b"\n") and b"\r" not in raw and not raw.startswith(b"\xef\xbb\xbf")
    document = json.loads(raw)
    assert document["site"] == {"latitude_deg": 30, "neg_z_bearing_deg": 0, "solar_noon_h": 12}
    assert document["objects"] == [] and document["files"] == [] and document["source"]["capture"]["privacy"]["shareable"] is False
    assert (document["schema"], document["source"]["pipeline"]["name"]) == ("garage_game.room", "garage-game-roomscan")  # the new ids
    assert document["source"]["capture"]["devices"] == ["Apple Synthetic phone"]
    # The location rules: no key of the manifest holds a longitude, a place or GPS.
    text = raw.decode().lower()
    assert "longitude" not in text and "gps" not in text and "address" not in text


def test_a_shell_the_contract_would_refuse_is_caught(plan, tmp_path):
    room = build(plan)
    room["shell"]["openings"][0]["host_part_id"] = "shell:wall_z"
    path = mf.write_room(tmp_path / "rooms", room).path
    problems = check_with_contract(path.parent)
    assert problems and any("wall_z" in p for p in problems)
    room = build(plan)
    room["spawns"][0]["position_m"] = [9.0, 0.0, 0.0]  # outside the bounds
    path = mf.write_room(tmp_path / "rooms3", room).path
    assert any("outside the room bounds" in p for p in check_with_contract(path.parent))


def test_a_captured_room_is_never_written_into_the_repository(plan):
    repo = find_repo_root()
    assert repo is not None
    inside = repo / "game" / "rooms" / "never_written"
    with pytest.raises(mf.ShellError, match="Git checkout"):
        mf.write_room(inside, build(plan))
    assert not inside.exists()


def test_the_default_folder_is_the_games_user_data(monkeypatch, tmp_path):
    monkeypatch.setattr(mf.sys, "platform", "win32")
    monkeypatch.setenv("APPDATA", str(tmp_path / "Roaming"))
    assert mf.default_rooms_dir() == tmp_path / "Roaming" / "Godot" / "app_userdata" / "Garage-Game" / "rooms"
    monkeypatch.setattr(mf.sys, "platform", "linux")
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "share"))
    assert mf.default_rooms_dir() == tmp_path / "share" / "godot" / "app_userdata" / "Garage-Game" / "rooms"


def test_spawns_are_on_clear_floor_away_from_walls_and_picked_objects():
    clear = np.zeros((20, 20), bool)
    clear[2:18, 2:18] = True
    clear[8:12, 8:12] = False  # something stands in the middle
    bounds = (0.0, 5.0, 0.0, 5.0)
    spots = mf.free_spawns(clear, (0.0, 0.0), 0.25, bounds)
    assert len(spots) >= 2
    for x, z, c in spots:
        assert 0.5 < x < 4.5 and 0.5 < z < 4.5 and c >= 0.3
        assert not (2.0 <= x <= 3.0 and 2.0 <= z <= 3.0)
    assert all(np.hypot(a[0] - b[0], a[1] - b[1]) >= 1.0 for i, a in enumerate(spots) for b in spots[i + 1:])
    first = mf.free_spawns(clear, (0.0, 0.0), 0.25, bounds, avoid=[(spots[0][0] - 0.5, spots[0][1] - 0.5, spots[0][0] + 0.5, spots[0][1] + 0.5)])
    assert (first[0][0], first[0][1]) != (spots[0][0], spots[0][1])
    assert mf.free_spawns(np.zeros((20, 20), bool), (0.0, 0.0), 0.25, bounds) == []
    assert yaw_toward(0, -1) == 0.0 and yaw_toward(-1, 0) == 90.0 and yaw_toward(1, 0) == -90.0


# --- planes from points, and the whole export, on the synthetic garage -----------------------------------------------------

def outward_cameras():
    cams = []
    for k in range(16):
        a = np.radians(k * 22.5)
        eye = np.array([-0.3, 1.4, -0.3])
        look = eye + np.array([np.cos(a), -0.15, np.sin(a)])
        cams.append(sc.look_at(eye, look))
    for k in range(8):
        a = np.radians(k * 45 + 20)
        eye = np.array([-0.3, 1.2, -0.3])
        cams.append(sc.look_at(eye, eye + np.array([np.cos(a), -1.0, np.sin(a)])))  # at the floor
        cams.append(sc.look_at(eye, eye + np.array([np.cos(a), 1.2, np.sin(a)])))  # at the ceiling
    return cams


def test_the_planes_are_checked_against_the_points_and_coloured_from_them(tmp_path):
    scene = inv_scene.build_scene(tmp_path, inv_scene.room_with_boxes(), outward_cameras())
    plan = plan_shell(scene)
    assert (plan.x_min, plan.x_max, plan.z_min, plan.z_max, plan.height) == (X0, X1, Z0, Z1, H)
    by = {s.key: s for s in plan.surfaces}
    assert set(by) == {"floor", "ceiling", *WALL_KEYS}
    for key, surface in by.items():
        ev = surface.evidence
        assert ev["points_within_25cm"] > 500, key
        assert abs(ev["median_offset_cm"]) < 1.0 and ev["spread_cm"] < 3.0, (key, ev)
        assert surface.colours and surface.base_color.startswith("#") and len(surface.base_color) == 7
        assert sum(s for _, s in surface.colours) == pytest.approx(1.0)


def test_export_end_to_end_makes_a_manifest_the_contract_accepts(garage_session, tmp_path):
    run_coverage(garage_session.captures, "garage", chunk_size=10, anchors=3, backend_factory=garage_session.backend(),
                 detector_factory=garage_session.detector(), log=lambda *_: None)
    captures = garage_session.captures
    scene = load_scene(captures, "garage")
    assert scene.scale_source == "seed_batch" and len(scene.registered) >= 20
    dims = sorted([scene.bounds[1] - scene.bounds[0], scene.bounds[3] - scene.bounds[2]])
    assert dims == pytest.approx([4.0, 5.0], abs=0.15)
    room_dir = captures / "garage"
    spec = spec_doc(session_id=garage_session.session_id, lamps=[])
    spec["openings"] = [dict(spec["openings"][0], u_m=1.5)]  # one window in wall A
    (room_dir / "shell-spec.json").write_text(json.dumps(spec), encoding="utf-8")
    result = export_shell(captures, "garage", rooms_dir=tmp_path / "rooms", created_utc="2026-10-08T00:00:00Z", log=lambda *_: None)
    assert result["problems"] == [], result["problems"]
    assert (room_dir / "shell" / "plan.json").is_file() and len(result["pictures"]) == 4
    document = json.loads(result["manifest"].read_bytes())
    lo, hi = document["bounds"]["min_m"], document["bounds"]["max_m"]
    spawns = {s["role"]: s for s in document["spawns"]}
    assert set(spawns) == {"player", "companion"}
    for s in spawns.values():
        x, y, z = s["position_m"]
        assert lo[0] < x < hi[0] and lo[2] < z < hi[2] and y == 0.0
        # Not inside the synthetic furniture (a table and a shelving unit), which the photos show as blocked floor.
        assert not (0.2 <= x <= 1.0 and 0.6 <= z <= 1.4) and not (-2.0 <= x <= -1.55 and -1.0 <= z <= 0.4)
    # The wall letters follow the first photo, so a spec made for another session is refused.
    (room_dir / "shell-spec.json").write_text(json.dumps(dict(spec, session_id="s-someone-else")), encoding="utf-8")
    with pytest.raises(SpecError, match="belongs to its session"):
        export_shell(captures, "garage", rooms_dir=tmp_path / "rooms", log=lambda *_: None)
    # And a pose cache that no longer gives the recorded room is refused rather than exported.
    cov = room_dir / "sessions" / garage_session.session_id / "coverage" / "coverage.json"
    record = json.loads(cov.read_bytes())
    record["room_frame"]["x_max_m"] += 0.5
    cov.write_text(json.dumps(record), encoding="utf-8")
    with pytest.raises(SceneMismatch):
        load_scene(captures, "garage")


# --- the export at the tape scale, checked against the true room; a bad export keeps the old room ----------------------------

TAPE_FACTOR = 0.926


class ModelTooLargeBackend(sc.FakeBackend):
    """The pose model returns the room 1 / 0.926 too large in its seed batch, as it did for the real garage: the tape factor is 0.926."""

    def batch_scale(self) -> float:
        return 1 / TAPE_FACTOR if self.calls == 0 else 1.0


@pytest.fixture(scope="module")
def scaled_garage(garage_master, tmp_path_factory):
    """The synthetic garage covered with a floor-to-ceiling and a diagonal tape measurement of the TRUE room (4 x 2.5 x 5 m)."""
    import dataclasses
    import shutil

    captures = tmp_path_factory.mktemp("scaled") / "repo" / "captures"
    shutil.copytree(garage_master.captures, captures)
    session = dataclasses.replace(garage_master, captures=captures)
    sid = session.session_id
    tape = {"room": "garage", "measurements": [
        {"id": "up", "kind": "floor_to_ceiling", "between": [], "session_id": sid, "value_m": 2.5, "sigma_m": 0.01},
        {"id": "diag", "kind": "diagonal", "between": ["AB", "CD"], "session_id": sid, "value_m": float(np.hypot(4.0, 5.0)), "sigma_m": 0.01}]}
    (captures / "garage" / "measurements.json").write_text(json.dumps(tape), encoding="utf-8")
    result = run_coverage(captures, "garage", chunk_size=10, anchors=3, skip_detection=True, log=lambda *_: None,
                          backend_factory=lambda: ModelTooLargeBackend(session.room, session.cams, names=session.names, seed=3))
    spec = spec_doc(session_id=sid)
    (captures / "garage" / "shell-spec.json").write_text(json.dumps(spec), encoding="utf-8")
    return session, result, spec


def true_to_room(session, scene):
    """The similarity (scale, rotation, translation) from the synthetic room's own coordinates to the pipeline's room frame,
    found from where the cameras really were: truth that does not come from any plane the pipeline fitted."""
    from roomscan.coverage.geometry import umeyama

    truth = np.array([session.cams[session.names[Path(scene.views[i]["derived"]["jpeg"]).name]][:3, 3] for i in scene.registered])
    room = np.array([scene.c2w[i][:3, 3] for i in scene.registered])
    return umeyama(truth, room)


def true_box_corners():
    return np.array([[x, y, z] for x in (-2.0, 2.0) for y in (0.0, 2.5) for z in (-2.5, 2.5)])


def test_the_export_at_the_tape_scale_lands_on_the_true_room(scaled_garage, tmp_path):
    session, result, spec = scaled_garage
    assert result["run"]["scale"]["scale_source"] == "tape"
    assert result["run"]["scale"]["applied_factor"] == pytest.approx(TAPE_FACTOR, abs=0.015)  # not the unit scale
    scene = load_scene(session.captures, "garage")
    assert scene.factor == pytest.approx(TAPE_FACTOR, abs=0.015)
    s, R, t = true_to_room(session, scene)
    assert s == pytest.approx(1.0, abs=0.03)  # true metres are the tape's metres: the room is not 8 per cent too large
    mapped = true_box_corners() @ (s * R).T + t
    exported = export_shell(session.captures, "garage", rooms_dir=tmp_path / "rooms", created_utc="2026-10-08T00:00:00Z", log=lambda *_: None)
    assert exported["problems"] == [], exported["problems"]
    document = json.loads(exported["manifest"].read_bytes())
    lo, hi = np.array(document["bounds"]["min_m"]), np.array(document["bounds"]["max_m"])
    assert np.allclose(mapped.min(0), lo, atol=0.05) and np.allclose(mapped.max(0), hi, atol=0.05), (mapped.min(0), lo, mapped.max(0), hi)
    # Every shell polygon lies on a face of the true room (to 5 cm), at the tape scale.
    faces = [(axis, float(v)) for axis in (0, 1, 2) for v in (mapped[:, axis].min(), mapped[:, axis].max())]
    for part in document["shell"]["parts"]:
        points = np.array(part["geometry"]["points_m"])
        flat = [axis for axis in (0, 1, 2) if np.ptp(points[:, axis]) < 1e-6]
        assert len(flat) == 1, part["id"]  # each is a flat plate
        axis = flat[0]
        assert min(abs(points[0, axis] - v) for a, v in faces if a == axis) < 0.05, part["id"]


def test_openings_come_out_as_holes_inside_their_walls_at_true_places(scaled_garage, tmp_path):
    session, _, spec = scaled_garage
    scene = load_scene(session.captures, "garage")
    s, R, t = true_to_room(session, scene)
    mapped = true_box_corners() @ (s * R).T + t
    planes = {axis: (mapped[:, axis].min(), mapped[:, axis].max()) for axis in (0, 2)}
    exported = export_shell(session.captures, "garage", rooms_dir=tmp_path / "rooms", created_utc="2026-10-08T00:00:00Z", log=lambda *_: None)
    document = json.loads(exported["manifest"].read_bytes())
    parts = {p["id"]: p for p in document["shell"]["parts"]}
    assert len(document["shell"]["openings"]) == len(spec["openings"]) == 3
    for opening in document["shell"]["openings"]:
        host = np.array(parts[opening["host_part_id"]]["geometry"]["points_m"])
        centre, (width, height) = np.array(opening["center_m"]), opening["size_m"]
        axis = next(a for a in (0, 2) if np.ptp(host[:, a]) < 1e-6)  # the wall's normal axis
        along = 2 if axis == 0 else 0
        # On the wall's plane, which is one of the true room's walls.
        assert abs(centre[axis] - host[0, axis]) < 1e-3
        assert min(abs(centre[axis] - v) for v in planes[axis]) < 0.05
        # Wholly inside the wall's polygon, with wall left on both sides: a hole, not a notch at an edge.
        lo_a, hi_a = host[:, along].min(), host[:, along].max()
        assert lo_a + 1e-6 < centre[along] - width / 2 and centre[along] + width / 2 < hi_a - 1e-6, opening["id"]
        assert centre[1] + height / 2 < host[:, 1].max() - 1e-6 and centre[1] - height / 2 > host[:, 1].min() - 1e-6, opening["id"]
    window = next(o for o in document["shell"]["openings"] if o["kind"] == "window")
    assert window["center_m"][1] - window["size_m"][1] / 2 > 0.5  # a sill: wall below it, so a hole and not a doorway


def test_a_failed_validation_keeps_the_previous_playable_manifest(scaled_garage, tmp_path, monkeypatch):
    session, _, spec = scaled_garage
    rooms = tmp_path / "rooms"
    first = export_shell(session.captures, "garage", rooms_dir=rooms, created_utc="2026-10-08T00:00:00Z", log=lambda *_: None)
    assert first["problems"] == []
    path = first["manifest"]
    kept = path.read_bytes()
    spec_path = session.captures / "garage" / "shell-spec.json"
    # A spec with one player spawn and no companion breaks the contract's two-spawn rule.
    broken = dict(spec, spawns=[{"id": "player_start", "role": "player", "position_m": [0.0, 0.0, 0.0], "yaw_deg": 0.0}])
    spec_path.write_text(json.dumps(broken), encoding="utf-8")
    try:
        bad = export_shell(session.captures, "garage", rooms_dir=rooms, created_utc="2026-10-09T00:00:00Z", log=lambda *_: None)
        assert path.read_bytes() == kept, "the playable room was replaced by one that fails validation"
        assert bad["problems"] and any("spawns" in p for p in bad["problems"])
        assert bad["published"] is False
        assert sorted(p.name for p in (rooms / "garage").iterdir()) == ["room.json"]  # no staged copy is left behind
        assert not (rooms / ".staging").exists() or not any((rooms / ".staging").iterdir())
        # When the validator cannot run at all, nothing is published either, and the old room stays.
        spec_path.write_text(json.dumps(dict(spec, display_name="Another name")), encoding="utf-8")
        from roomscan.shell import export as export_module
        monkeypatch.setattr(export_module, "check_with_contract", lambda room_dir: ["contract validator could not run: no jsonschema"])
        unchecked = export_shell(session.captures, "garage", rooms_dir=rooms, created_utc="2026-10-09T00:00:00Z", log=lambda *_: None)
        assert path.read_bytes() == kept and unchecked["published"] is False and "could not run" in unchecked["problems"][0]
        monkeypatch.undo()
        # A valid export does replace it.
        good = export_shell(session.captures, "garage", rooms_dir=rooms, created_utc="2026-10-09T00:00:00Z", log=lambda *_: None)
        assert good["problems"] == [] and good["published"] is True and path.read_bytes() != kept
        assert json.loads(path.read_bytes())["display_name"] == "Another name"
    finally:
        spec_path.write_text(json.dumps(spec), encoding="utf-8")
