"""Shared by the reader's tests: copies of the plays in a temporary folder, with tiny stills made at run time.

Stills are never in Git (they show real places), so no test reads a committed picture: each makes JPEGs of a few pixels,
named as the record's `still` events name them, in a temporary copy of the play. Nothing here touches the founder's records.
"""
from __future__ import annotations

import base64
import json
import re
import shutil
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOL = HERE.parent
REPO = TOOL.parent.parent
FIXTURES = HERE / "fixtures"
EXAMPLE_PLAY = REPO / "contracts" / "examples" / "playtests" / "20261015-143000-island_example"
FIXTURE_PLAY = FIXTURES / "20261016-101500-island_example"

sys.path.insert(0, str(TOOL))
sys.path.insert(0, str(HERE))

import report  # noqa: E402  (the module under test)

try:
    from PIL import Image
except ImportError:  # the reader works without Pillow; the tests that look at pictures say so
    Image = None

needs_pillow = unittest.skipUnless(Image is not None, "Pillow is not installed (pip install -r tools/playtest/requirements.txt)")


def read_events(folder: Path) -> list:
    return [json.loads(line) for line in (folder / "events.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]


def tolerant_events(folder: Path) -> list:
    """The events that parse, whatever else is in the file (a damaged record is still given stills for the events that name them)."""
    out = []
    for line in (folder / "events.jsonl").read_bytes().decode("utf-8", errors="replace").splitlines():
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if isinstance(event, dict):
            out.append(event)
    return out


def copy_play(source: Path, into: Path, stills: bool = True, skip: tuple = ()) -> Path:
    """A temporary copy of a play, with a 16x9-pixel JPEG for every still its events name (except those in `skip`)."""
    target = into / source.name
    shutil.copytree(source, target, ignore=shutil.ignore_patterns("REPORT.md", "sheet.jpg", "stills"))
    if stills:
        make_stills(target, skip)
    return target


# A 2x2 JPEG, for when Pillow is not installed (the reader only needs the files to be there; with Pillow the tests draw their own).
TINY_JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCpRRRXzB9of//Z")


def make_stills(folder: Path, skip: tuple = ()) -> None:
    (folder / "stills").mkdir(exist_ok=True)
    for index, event in enumerate(tolerant_events(folder)):
        if event.get("kind") != "still" or not isinstance(event.get("file"), str) or event["file"] in skip:
            continue
        if Image is None:
            (folder / "stills" / event["file"]).write_bytes(TINY_JPEG)
            continue
        shade = (index * 37) % 200 + 30
        Image.new("RGB", (16, 9), (shade, 255 - shade, 128)).save(folder / "stills" / event["file"], "JPEG")


# ---------------------------------------------------------------------------------------------------------------------
# Small plays made on the spot, for the tests that need one thing exactly.

RECORD = "20261016-120000-island_example"


def ev(t: float, kind: str, **fields) -> dict:
    return {"t": t, "utc": "2026-10-16T17:00:00Z", "kind": kind, **fields}


def command(t: float, op: str, ok: bool = True, code: str = "", message: str = "", principal: str = "player:local", **fields) -> dict:
    result = {"ok": True} if ok else {"ok": False, "code": code, "message": message or "Not now."}
    return ev(t, "command", principal=principal, op=op, result=result, ms=1.0, **fields)


def notice(t: float, text: str, channel: str = "hud", **fields) -> dict:
    return ev(t, "notice", channel=channel, text=text, **fields)


def body(t: float, state: str = "standing", height: float = 0.0, distance: float = 1.0, link: str = "listening", position=(0.0, 0.0, 0.0)) -> dict:
    return ev(t, "body", position_m=list(position), height_m=height, state=state, view="eye", gubble={"distance_m": distance, "link": link})


def manifest_for(events: list, ended: bool = True, **overrides) -> dict:
    last = max((e["t"] for e in events), default=0.0)
    manifest = {"schema": "garage_game.playtest_manifest", "version": 1, "record_id": RECORD, "build": "test", "room_id": "island_example",
                "room_sha256": "0" * 64, "team_id": "example_team", "mode": "challenge", "difficulty": "standard",
                "style": {"preset_id": "storybook_painterly", "version": 1}, "window": {"width": 1280, "height": 720, "fullscreen": False},
                "renderer": {"method": "forward_plus", "adapter": "Test Adapter"}, "log_file": "godot.log", "beat_s": 10,
                "started_utc": "2026-10-16T17:00:00Z"}
    if ended:
        kinds = [e.get("kind") for e in events]
        manifest.update(ended_utc="2026-10-16T17:30:00Z", played_s=last,
                        counts={"events": len(events), "stills": kinds.count("still"), "marks": kinds.count("mark")})
    manifest.update(overrides)
    return manifest


def write_play(into: Path, events, manifest="default", name: str = RECORD) -> Path:
    """A play folder with these events (a list of dicts, or raw bytes) and this manifest (a dict, raw bytes, None for none)."""
    folder = into / name
    folder.mkdir(parents=True, exist_ok=True)
    if manifest == "default":
        manifest = manifest_for(events) if isinstance(events, list) else {}
    if isinstance(manifest, dict):
        manifest = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
    if manifest is not None:
        (folder / "manifest.json").write_bytes(manifest)
    if events is not None:
        data = events if isinstance(events, bytes) else "".join(json.dumps(e) + "\n" for e in events).encode("utf-8")
        (folder / "events.jsonl").write_bytes(data)
    return folder


def load(into: Path, events, manifest="default"):
    return report.load_play(write_play(into, events, manifest))


def section(text: str, heading: str) -> str:
    """The part of a report under a '## heading', up to the next one."""
    start = text.index(heading + "\n")
    end = text.find("\n## ", start + len(heading))
    return text[start:end if end != -1 else len(text)]


def outside_code_spans(text: str) -> str:
    """The report with every `code span` taken out: record text lives only inside them, so what is left must be the reader's own words."""
    return re.sub(r"`[^`\n]*`", "", text)
