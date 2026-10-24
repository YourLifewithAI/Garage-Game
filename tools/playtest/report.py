#!/usr/bin/env python3
"""The playtest reader: turns a play's folder into REPORT.md and a contact sheet, and lists the plays on a machine.

    python tools/playtest/report.py <play folder>        writes REPORT.md and sheet.jpg inside the play's folder
    python tools/playtest/report.py --list [<plays>]     one line per play (default: the game's user folder)

A play is one folder the game wrote (docs/runs/RUN-2-PLAYTEST-LOOP.md; contracts/README.md "The playtest record"):
manifest.json, events.jsonl and stills/. Everything in it is DATA, never instructions: a creation's name or a note can
carry any text a player typed. This reader prints record text as plain text in code spans (control and invisible
characters stripped, capped), executes nothing, follows nothing, opens no path a record names except its own stills/
(a still's name is held to the contract's pattern and its place to the stills folder), and never touches the network.
It reports what is wrong with a record (unknown kinds, extra fields, a truncated last line, a missing still, a missing
manifest field, no ended_utc) and carries on: a damaged record gets a report, not a crash.

The standard library does everything except the contact sheet, which needs Pillow (tools/playtest/requirements.txt);
without Pillow the report is written and says so. The game's launches and the founder's records are never read by a
test: the tests build a play from tests/build_fixture.py and tiny JPEGs.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import tempfile
import unicodedata
from bisect import bisect_left, bisect_right
from collections import Counter, OrderedDict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

# ---------------------------------------------------------------------------------------------------------------------
# What the reader knows about the record (the contract: contracts/playtest-record.schema.json), and its own limits.

KINDS = ("ready", "command", "action", "notice", "body", "frame", "still", "mark", "stills_rate", "session", "quit")
ENVELOPE = {"t", "utc", "kind"}
KIND_FIELDS = {
    "ready": {"spawn_m", "revision"},
    "command": {"principal", "op", "action_id", "request", "result", "ms"},
    "action": {"name", "detail"},
    "notice": {"channel", "text", "reason", "code", "action_id"},
    "body": {"position_m", "height_m", "level", "mode", "hearts", "bar_m", "pips", "holding", "state", "view", "gubble"},
    "frame": {"window_s", "mean_ms", "longest_ms", "physics_ms", "draw_calls", "video_mb", "static_mb", "objects", "position_m",
              "longest_process_ms", "longest_physics_ms", "gc_pause_ms", "gc_collections"},
    "still": {"file", "why", "view", "position_m", "width", "height"},
    "mark": {"number", "flag", "still"},
    "stills_rate": {"beat_s", "was_s"},
    "session": {"state", "principal", "sessions"},
    "quit": {"played_s", "events", "stills", "marks"},
}
FORBIDDEN_FIELDS = {"data", "picture", "base64", "token", "session_file", "save", "secret", "password", "credential"}
MANIFEST_REQUIRED = ("schema", "version", "record_id", "build", "room_id", "room_sha256", "team_id", "mode", "style", "window",
                     "renderer", "log_file", "beat_s", "started_utc")
MANIFEST_OPTIONAL = {"difficulty", "rules", "engine", "ended_utc", "played_s", "counts"}
QUERY_OPS = {"room.describe", "entities.list", "entity.inspect", "capabilities.list", "observe", "jobs.status", "receipt.lookup",
             "approval.status", "journal.read", "map.find", "player.status", "nature.describe", "wishes.read", "view.look"}
STILL_NAME = re.compile(r"^([0-9]{4})-(steady|mark-([1-9][0-9]{0,3})|keep-([1-9][0-9]{0,3})|refusal|home|mode|quit)\.jpg$")
RECORD_ID = re.compile(r"^([0-9]{4})([0-9]{2})([0-9]{2})-([0-9]{2})([0-9]{2})([0-9]{2})-")

MAX_MANIFEST_BYTES = 1 << 20
MAX_EVENTS_BYTES = 128 << 20  # a twenty-minute play is about 5 MB; a record past this is read this far
MAX_LINE_BYTES = 64 << 10  # the contract allows 8 KiB; a longer line is skipped, not read
MAX_STILL_BYTES = 64 << 20
MAX_STILL_PIXELS = 40_000_000

TEXT_CAP = 200  # free text in the report (a note, a notice, a refusal's words)
SUMMARY_CAP = 160  # a command's request summary
WINDOW_BEFORE_S = 20.0  # a mark's context: the twenty seconds before it...
WINDOW_AFTER_S = 5.0  # ...and five after
WINDOW_LINES = 60  # at most this many lines of context under one mark
NEAR_GUBBLE_M = 2.0
BREADCRUMB_S = 2.0  # the breadcrumb's beat; a sample holds until the next, but never longer than the cap below
BREADCRUMB_HOLD_CAP_S = 6.0
HITCH_MS = 33.3  # a frame window whose longest frame is over this is a hitch
WORST_FRAMES = 5
MAX_PLAY_S = 10_000_000.0  # a time past about 115 days is not a time: the contract's own limit is a million seconds
NOTICE_BEFORE_S = 1.0  # a refusal's notice is looked for from just before the refusal...
NOTICE_AFTER_S = 5.0  # ...to five seconds after it (by code and time, or by the same words)
NOTICE_BY_ID_AFTER_S = 30.0  # an action_id names its command exactly, so it may come later
NOTICES_SHOWN = 500
SHEET_MAX = 60
SHEET_COLUMNS = 6
THUMB = (256, 144)

# Height above the floor, in the game's own bands: a band is 0.4 m (the stamina bar's step and a climb's rise in the rules pack,
# `band_m` 0.4, docs/game/MECHANICS.md), so band 1 is 0 to 0.4 m, band 2 is 0.4 to 0.8 m, and so on up to BANDS_SHOWN.
BAND_M = 0.4
BANDS_SHOWN = 20
BELOW_FLOOR = "below the floor (diving)"


def band_label(n: int) -> str:
    if n >= BANDS_SHOWN:
        return f"band {BANDS_SHOWN} and up: {(BANDS_SHOWN - 1) * BAND_M:.1f} m and above"
    return f"band {n}: {(n - 1) * BAND_M:.1f} to {n * BAND_M:.1f} m"


HEIGHT_LABELS = [BELOW_FLOOR] + [band_label(n) for n in range(1, BANDS_SHOWN + 1)]
STATE_ORDER = ("standing", "walking", "running", "swimming", "diving", "climbing", "falling", "carried", "fading_home")
LINK_ORDER = ("offline", "listening", "planning", "acting", "waiting")
NO_BREADCRUMB = "(no breadcrumb)"


# ---------------------------------------------------------------------------------------------------------------------
# Record text is data. Everything that reaches the report goes through here.

_BLANKS = {"\t", "\n", "\r", "\x0b", "\x0c", "\x85", " ", " "}
_INVISIBLE = {0x034F, 0x115F, 0x1160, 0x3164, 0xFFA0}


def clean(value: object, cap: int = TEXT_CAP) -> str:
    """One line of plain text: control, format, private-use and unassigned characters removed, runs of blanks folded
    into one space, capped (with an ellipsis). Not a string becomes its short plain form, never an object's repr."""
    if value is None:
        return ""
    if isinstance(value, bool):
        text = "true" if value else "false"
    elif isinstance(value, (int, float)):
        text = repr(value)[:40]
    elif isinstance(value, str):
        text = value
    elif isinstance(value, list):
        text = "[a list]"
    elif isinstance(value, dict):
        text = "{an object}"
    else:
        text = "?"
    out: list[str] = []
    for ch in text:
        code = ord(ch)
        if ch in _BLANKS or unicodedata.category(ch) == "Zs":
            out.append(" ")
        elif (unicodedata.category(ch) in ("Cc", "Cf", "Cs", "Co", "Cn", "Zl", "Zp") or 0xFE00 <= code <= 0xFE0F
              or 0xE0100 <= code <= 0xE01EF or code in _INVISIBLE):
            continue
        else:
            out.append(ch)
    result = re.sub(r" {2,}", " ", "".join(out)).strip()
    return result if len(result) <= cap else result[: max(cap - 1, 0)].rstrip() + "…"


def code(value: object, cap: int = TEXT_CAP) -> str:
    """Record text for the report: cleaned, in a code span, so no markup in it can act. Backticks cannot occur inside a
    span and a pipe would end a table cell, so each is swapped for a lookalike."""
    text = clean(value, cap).replace("`", "'").replace("|", "¦")
    return f"`{text}`" if text else "_(empty)_"


def word(value: object, cap: int = 24) -> str:
    """A short token (a channel, a state, a flag) for use outside a code span: anything but letters, digits and a few
    marks becomes a question mark, so it cannot carry markup."""
    return re.sub(r"[^A-Za-z0-9_.:+ -]", "?", clean(value, cap))


def fmt_n(value: object) -> str:
    number = num(value)
    return f"{number:g}" if number is not None else "?"


def num(value: object) -> Optional[float]:
    """A finite number, or None (a bool is not a number; a huge integer is not finite)."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    try:
        number = float(value)
    except OverflowError:
        return None
    return number if math.isfinite(number) else None


def vec(value: object) -> Optional[tuple]:
    if isinstance(value, list) and len(value) == 3:
        parts = [num(v) for v in value]
        if all(p is not None for p in parts):
            return tuple(parts)
    return None


def fmt_t(t: float) -> str:
    seconds = int(max(t, 0.0))
    hours, rest = divmod(seconds, 3600)
    minutes, secs = divmod(rest, 60)
    return f"{hours}:{minutes:02d}:{secs:02d}" if hours else f"{minutes}:{secs:02d}"


def fmt_rel(dt: float) -> str:
    return f"{dt:+.1f} s"


def fmt_vec(v: Optional[tuple]) -> str:
    return "(" + ", ".join(f"{p:.2f}" for p in v) + ")" if v else "(unknown)"


def fmt_dur(seconds: float) -> str:
    tenths = int(round(seconds * 10))
    minutes, rest = divmod(tenths, 600)
    return f"{minutes} min {rest / 10:.1f} s" if minutes else f"{rest / 10:.1f} s"


def round_to_sum(values: list, total: float, step: float) -> list:
    """Round each value to a multiple of step so the rounded values add up to the rounded total (largest remainder)."""
    if not values:
        return []
    units = [v / step for v in values]
    floors = [math.floor(u) for u in units]
    short = int(round(total / step)) - sum(floors)
    by_remainder = sorted(range(len(values)), key=lambda i: units[i] - floors[i], reverse=True)
    for i in by_remainder[:max(short, 0)]:
        floors[i] += 1
    for i in reversed(by_remainder):  # float noise past the total: take it back from the smallest remainders
        if short < 0 and floors[i] > 0:
            floors[i] -= 1
            short += 1
    return [f * step for f in floors]


# ---------------------------------------------------------------------------------------------------------------------
# Loading a play. Nothing here raises for a damaged record: it writes a note.

class Notes:
    """What is wrong with a record, gathered by kind so a thousand bad lines make one sentence."""

    def __init__(self) -> None:
        self.items: "OrderedDict[str, list]" = OrderedDict()

    def add(self, key: str, text: str, line: Optional[int] = None) -> None:
        entry = self.items.setdefault(key, [text, 0, []])
        entry[1] += 1
        if line is not None and len(entry[2]) < 5:
            entry[2].append(line)

    def lines(self) -> list:
        out = []
        for text, count, lines in self.items.values():
            if lines:
                more = f" and {count - len(lines)} more" if count > len(lines) else ""
                where = f" (line{'s' if len(lines) > 1 else ''} {', '.join(str(n) for n in lines)}{more})"
            else:
                where = f" (×{count})" if count > 1 else ""
            out.append(text + where)
        return out

    def __bool__(self) -> bool:
        return bool(self.items)


@dataclass
class Ev:
    n: int  # the line in events.jsonl
    t: float
    kind: str
    d: dict


@dataclass
class Play:
    folder: Path
    name: str
    manifest: dict
    events: list
    notes: Notes
    length_s: float
    ended: bool
    timeline: list = field(default_factory=list)  # known kinds, sorted by t
    times: list = field(default_factory=list)  # the timeline's t, for bisecting
    bodies: list = field(default_factory=list)
    body_times: list = field(default_factory=list)


def _no_constant(name: str):
    raise ValueError(f"{name} is not JSON")


def parse_json(text: str):
    return json.loads(text, parse_constant=_no_constant)


def read_manifest(folder: Path, notes: Notes) -> dict:
    path = folder / "manifest.json"
    try:
        size = path.stat().st_size
        if size > MAX_MANIFEST_BYTES:
            notes.add("manifest", f"`manifest.json` is {size} bytes, over the {MAX_MANIFEST_BYTES} the reader reads; it was not read")
            return {}
        with open(path, "rb") as handle:
            data = handle.read(MAX_MANIFEST_BYTES)
    except OSError:
        notes.add("manifest", "`manifest.json` is missing or unreadable")
        return {}
    text = data.decode("utf-8", errors="replace")
    if text.startswith("﻿"):
        text = text[1:]
    try:
        manifest = parse_json(text)
    except (ValueError, RecursionError):
        notes.add("manifest", "`manifest.json` is not valid JSON")
        return {}
    if not isinstance(manifest, dict):
        notes.add("manifest", "`manifest.json` is not a JSON object")
        return {}
    missing = [name for name in MANIFEST_REQUIRED if name not in manifest]
    if missing:
        notes.add("manifest-missing", "the manifest is missing " + ", ".join(code(m, 40) for m in missing[:12]))
    extra = sorted(k for k in manifest if k not in MANIFEST_REQUIRED and k not in MANIFEST_OPTIONAL)
    if extra:
        notes.add("manifest-extra", "the manifest carries fields this reader does not know (not read): "
                  + ", ".join(code(k, 40) for k in extra[:12]) + (f" and {len(extra) - 12} more" if len(extra) > 12 else ""))
    return manifest


def read_events(folder: Path, notes: Notes) -> list:
    path = folder / "events.jsonl"
    events: list = []
    try:
        handle = open(path, "rb")
    except OSError:
        notes.add("events", "`events.jsonl` is missing or unreadable")
        return events
    extras: "dict[str, Counter]" = {}
    with handle:
        number = 0
        total = 0
        previous_t: Optional[float] = None
        while True:
            raw = handle.readline(MAX_LINE_BYTES + 1)
            if not raw:
                break
            number += 1
            total += len(raw)
            if total > MAX_EVENTS_BYTES:
                notes.add("events-cap", f"`events.jsonl` is longer than {MAX_EVENTS_BYTES >> 20} MiB; the rest was not read", number)
                break
            terminated = raw.endswith(b"\n")
            if len(raw) > MAX_LINE_BYTES and not terminated:
                notes.add("long-line", "lines longer than the reader reads (skipped)", number)
                while True:  # drop the rest of that line
                    rest = handle.readline(MAX_LINE_BYTES)
                    if not rest or rest.endswith(b"\n"):
                        break
                continue
            if number == 1 and raw.startswith(b"\xef\xbb\xbf"):
                raw = raw[3:]
            try:
                text = raw.decode("utf-8")
            except UnicodeDecodeError:
                notes.add("utf8", "lines that are not valid UTF-8 (read with replacement characters)", number)
                text = raw.decode("utf-8", errors="replace")
            text = text.rstrip("\r\n")
            if not text.strip():
                continue
            try:
                obj = parse_json(text)
            except (ValueError, RecursionError):
                if not terminated:
                    notes.add("truncated", "the last line is cut off (no newline and not valid JSON: the game was closed mid-write); it was skipped", number)
                else:
                    notes.add("badjson", "lines that are not valid JSON (skipped)", number)
                continue
            if not isinstance(obj, dict):
                notes.add("notobject", "lines that are not JSON objects (skipped)", number)
                continue
            kind = obj.get("kind")
            if not isinstance(kind, str):
                notes.add("nokind", "events with no kind (kept out of every section)", number)
                continue
            t = num(obj.get("t"))
            if t is None or t < 0 or t > MAX_PLAY_S:
                notes.add("not", "events with no usable `t` (placed at the time of the event before)", number)
                t = previous_t or 0.0
            elif previous_t is not None and t < previous_t:
                notes.add("backwards", "time running backwards (a `t` earlier than the line before)", number)
            previous_t = t
            if kind not in KIND_FIELDS:
                notes.add("kind:" + clean(kind, 40), f"events of the kind {code(kind, 40)}, which this reader does not know (kept out of every section)", number)
            else:
                known = KIND_FIELDS[kind] | ENVELOPE
                for name in obj:
                    if name in FORBIDDEN_FIELDS:
                        notes.add("forbidden:" + clean(name, 40), f"{kind} events carrying {code(name, 40)}, which the contract refuses (never read, never shown)", number)
                    elif name not in known:
                        extras.setdefault(kind, Counter())[clean(name, 40)] += 1
            events.append(Ev(number, t, kind if kind in KIND_FIELDS else "unknown:" + clean(kind, 40), obj))
    for kind, names in extras.items():
        listed = ", ".join(code(n, 40) for n, _ in names.most_common(8))
        notes.add("extra:" + kind, f"{kind} events carry fields this reader does not know (ignored, which the contract allows): {listed}")
    return events


def load_play(folder: Path) -> Play:
    folder = Path(folder)
    notes = Notes()
    manifest = read_manifest(folder, notes)
    events = read_events(folder, notes)
    if not events and "events" not in notes.items:
        notes.add("empty", "`events.jsonl` holds no events")
    ended = isinstance(manifest.get("ended_utc"), str) and bool(manifest.get("ended_utc"))
    if manifest and not ended:
        notes.add("unclean", "the manifest has no `ended_utc`: the game did not quit cleanly (closed another way, killed or crashed)")
    quits = [e for e in events if e.kind == "quit"]
    if ended and not quits:
        notes.add("no-quit", "the manifest says the game quit, but `events.jsonl` has no quit line")
    if quits and not ended and manifest:
        notes.add("quit-no-end", "`events.jsonl` has a quit line but the manifest has no `ended_utc`")
    counts = manifest.get("counts")
    if ended and isinstance(counts, dict):
        shown = Counter(e.kind for e in events)
        for key, kind in (("events", None), ("stills", "still"), ("marks", "mark")):
            expected = num(counts.get(key))
            actual = len(events) if kind is None else shown.get(kind, 0)
            if expected is not None and int(expected) != actual:
                notes.add("count:" + key, f"the manifest counts {int(expected)} {key} but the reader found {actual}")
    last_t = max((e.t for e in events), default=0.0)
    played = num(manifest.get("played_s")) if ended else None
    if played is not None and not 0 <= played <= MAX_PLAY_S:
        notes.add("played", "`played_s` is not a believable number of seconds (not used)")
        played = None
    length = max(last_t, played or 0.0)
    play = Play(folder=folder, name=folder.name, manifest=manifest, events=events, notes=notes, length_s=length, ended=ended)
    play.timeline = sorted((e for e in events if e.kind in KIND_FIELDS), key=lambda e: e.t)
    play.times = [e.t for e in play.timeline]
    play.bodies = [e for e in play.timeline if e.kind == "body"]
    play.body_times = [e.t for e in play.bodies]
    return play


def body_before(play: Play, t: float) -> Optional[Ev]:
    index = bisect_right(play.body_times, t)
    return play.bodies[index - 1] if index else None


def window(play: Play, start: float, end: float) -> list:
    return play.timeline[bisect_left(play.times, start):bisect_right(play.times, end)]


# ---------------------------------------------------------------------------------------------------------------------
# Stills: only the contract's names, only inside this play's stills/ folder.

@dataclass
class StillRec:
    n: int
    t: float
    file: str  # the contract's name, or "" if the record named something else
    why: str
    view: str
    position: Optional[tuple]
    size: Optional[tuple]
    path: Optional[Path] = None  # where it really is, if it is there and is ours to open
    note: str = ""


def still_path(folder: Path, name: object) -> Optional[Path]:
    """The still's file, if the name is the contract's and the file is a plain file inside this play's stills/ folder."""
    if not isinstance(name, str) or not STILL_NAME.match(name):
        return None
    try:
        directory = (folder / "stills").resolve(strict=True)
        path = directory / name
        if path.is_symlink():
            return None
        real = path.resolve(strict=True)
        if real.parent != directory or not real.is_file() or real.stat().st_size > MAX_STILL_BYTES:
            return None
        return real
    except OSError:
        return None


def find_stills(play: Play) -> list:
    out = []
    for e in play.timeline:
        if e.kind != "still":
            continue
        name = e.d.get("file")
        valid = isinstance(name, str) and bool(STILL_NAME.match(name))
        size = (num(e.d.get("width")), num(e.d.get("height")))
        out.append(StillRec(e.n, e.t, name if valid else "", word(e.d.get("why"), 20) or "?", word(e.d.get("view"), 20),
                            vec(e.d.get("position_m")), size if None not in size else None,
                            still_path(play.folder, name) if valid else None, "" if valid else "not a still name"))
    return out


def still_label(s: StillRec) -> str:
    match = STILL_NAME.match(s.file) if s.file else None
    if match and match.group(3):
        return f"mark {match.group(3)}"
    if match and match.group(4):
        return f"keep {match.group(4)}"
    return s.why


def spread_in_time(items: list, count: int) -> list:
    """`count` of the items (sorted by t), as evenly spread over their time span as the pictures allow, the first and
    last included: a beat quickened to one second must not crowd out the slow stretches."""
    if count <= 0 or not items:
        return []
    if count >= len(items):
        return list(items)
    if count == 1:
        return [items[0]]
    times = [s.t for s in items]
    first, last = times[0], times[-1]
    taken: set = set()
    for i in range(count):
        target = first + (last - first) * i / (count - 1)
        k = bisect_left(times, target)
        for reach in range(len(items)):
            near = [j for j in (k - reach - 1, k + reach) if 0 <= j < len(items) and j not in taken]
            if near:
                taken.add(min(near, key=lambda j: abs(times[j] - target)))
                break
    return [items[j] for j in sorted(taken)]


def sheet_selection(stills: list, limit: int = SHEET_MAX) -> list:
    """At most `limit` stills for the contact sheet: the marks and keeps first, then the refusals, then the home, mode and
    quit stills, then the steady beat spread evenly over what room is left. Shown marks and refusals first, the rest in time."""
    marks = [s for s in stills if s.why in ("mark", "keep")]
    refusals = [s for s in stills if s.why == "refusal"]
    special = [s for s in stills if s.why in ("home", "mode", "quit")]
    steady = [s for s in stills if s.why not in ("mark", "keep", "refusal", "home", "mode", "quit") and s.path is not None]
    chosen = marks[:limit]
    for group in (refusals, special):
        chosen += group[:max(limit - len(chosen), 0)]
    chosen += spread_in_time(steady, max(limit - len(chosen), 0))
    first = [s for s in chosen if s.why in ("mark", "keep", "refusal")]
    rest = sorted((s for s in chosen if s.why not in ("mark", "keep", "refusal")), key=lambda s: s.t)
    seen, out = set(), []
    for s in first + rest:
        if (s.n, s.file) not in seen:
            seen.add((s.n, s.file))
            out.append(s)
    return out


def make_sheet(items: list, out_path: Path) -> str:
    """Writes the contact sheet. Returns "" on success, or why there is none."""
    if not items:
        return "no stills"
    if not any(s.path is not None for s in items):
        return "none of the stills is on disk"
    try:
        import warnings

        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        return "Pillow is not installed (pip install -r tools/playtest/requirements.txt)"
    try:
        pad, label_h = 4, 14
        cols = min(SHEET_COLUMNS, len(items))
        rows = -(-len(items) // cols)
        cell_w, cell_h = THUMB[0] + 2 * pad, THUMB[1] + label_h + 2 * pad
        sheet = Image.new("RGB", (cols * cell_w, rows * cell_h), (24, 24, 28))
        draw = ImageDraw.Draw(sheet)
        font = ImageFont.load_default()
        Image.MAX_IMAGE_PIXELS = MAX_STILL_PIXELS
        for i, s in enumerate(items):
            x, y = (i % cols) * cell_w + pad, (i // cols) * cell_h + pad
            draw.rectangle([x, y, x + THUMB[0] - 1, y + THUMB[1] - 1], fill=(44, 44, 52))
            state = ""
            if s.path is None:
                state = "missing"
            else:
                try:
                    with warnings.catch_warnings():
                        warnings.simplefilter("error")
                        with Image.open(s.path) as picture:
                            picture.draft("RGB", (THUMB[0] * 2, THUMB[1] * 2))
                            thumb = picture.convert("RGB")
                            thumb.thumbnail(THUMB)
                    sheet.paste(thumb, (x + (THUMB[0] - thumb.width) // 2, y + (THUMB[1] - thumb.height) // 2))
                except Exception:  # an unreadable still is a finding, not a crash
                    state = "unreadable"
            if state:
                draw.text((x + 8, y + THUMB[1] // 2 - 5), f"still {state}", fill=(200, 120, 120), font=font)
            colour = (255, 210, 90) if s.why in ("mark", "keep") else (255, 150, 130) if s.why == "refusal" else (225, 225, 230)
            label = f"{fmt_t(s.t)}  {still_label(s)}"
            draw.text((x, y + THUMB[1] + 2), label[:40], fill=colour, font=font)
        write_bytes_atomic(out_path, lambda tmp: sheet.save(tmp, "JPEG", quality=82), suffix=".jpg")
    except Exception as error:  # the sheet is a convenience; the report stands without it
        return f"the sheet could not be built ({type(error).__name__})"
    return ""


# ---------------------------------------------------------------------------------------------------------------------
# The analysis: each piece reads the events and nothing else.

@dataclass
class MarkRec:
    ev: Ev
    number: str
    flag: str
    still: str
    label: str


def find_marks(play: Play) -> list:
    out = []
    for e in play.timeline:
        if e.kind != "mark":
            continue
        raw_number = e.d.get("number")
        number = str(raw_number) if isinstance(raw_number, int) and not isinstance(raw_number, bool) and 0 < raw_number < 10 ** 6 else "?"
        flag = e.d.get("flag")
        name = e.d.get("still")
        label = (f"Mark {number}, something's off" if flag == "off" else f"Keep {number}, keep this" if flag == "keep"
                 else f"Mark {number} (flag {code(flag, 12) if flag else 'missing'})")
        out.append(MarkRec(e, number, flag if flag in ("off", "keep") else "", name if isinstance(name, str) and STILL_NAME.match(name) else "", label))
    return out


def request_summary(request: object, cap: int = SUMMARY_CAP) -> str:
    def scalar(v: object) -> str:
        if isinstance(v, str):
            return '"' + clean(v, 60) + '"'
        return clean(v, 24) if not isinstance(v, (list, dict)) else clean(v)

    def one(v: object) -> str:
        if isinstance(v, list):
            return "[" + ", ".join(scalar(x) for x in v[:4]) + (", …" if len(v) > 4 else "") + "]"
        if isinstance(v, dict):
            items = list(v.items())
            return "{" + ", ".join(f"{clean(k, 20)}={scalar(x)}" for k, x in items[:4]) + (", …" if len(items) > 4 else "") + "}"
        return scalar(v)

    if not isinstance(request, dict) or not request:
        return ""
    return clean(", ".join(f"{clean(k, 24)}={one(v)}" for k, v in list(request.items())[:8]), cap)


def op_of(e: Ev) -> str:
    return clean(e.d.get("op"), 40)


def command_result(e: Ev) -> tuple:
    """(ok, code, message): ok is True, False or None when the result is unreadable."""
    result = e.d.get("result")
    if not isinstance(result, dict):
        return None, "", ""
    ok = result.get("ok")
    return (ok if isinstance(ok, bool) else None), clean(result.get("code"), 40), clean(result.get("message"))


@dataclass
class Refusal:
    ev: Ev
    code: str
    message: str
    notice: Optional[Ev] = None
    how: str = ""
    still: Optional[StillRec] = None


def match_refusals(play: Play, stills: list) -> tuple:
    """The refused commands, each with the notice that showed it: by action_id, else by code and time, else by the same
    words on the refusal channel. Returns (refusals, refusal notices that no refused command claimed)."""
    refusals = []
    for e in play.timeline:
        if e.kind == "command":
            ok, rcode, message = command_result(e)
            if ok is False:
                refusals.append(Refusal(e, rcode or "(no code)", message))
    notices = [e for e in play.timeline if e.kind == "notice"]
    used: set = set()

    def claim(refusal: Refusal, notice: Ev, how: str) -> None:
        used.add(notice.n)
        refusal.notice, refusal.how = notice, how

    for r in refusals:  # an action_id names the command exactly
        action_id = r.ev.d.get("action_id")
        if isinstance(action_id, str) and action_id:
            hits = [n for n in notices if n.n not in used and n.d.get("action_id") == action_id
                    and r.ev.t - NOTICE_BEFORE_S <= n.t <= r.ev.t + NOTICE_BY_ID_AFTER_S]
            if hits:
                claim(r, hits[0], "action_id")
    for r in refusals:  # then the same code close in time
        if r.notice is None and r.code != "(no code)":
            hits = [n for n in notices if n.n not in used and clean(n.d.get("code"), 40) == r.code
                    and r.ev.t - NOTICE_BEFORE_S <= n.t <= r.ev.t + NOTICE_AFTER_S]
            if hits:
                claim(r, min(hits, key=lambda n: abs(n.t - r.ev.t)), "code and time")
    for r in refusals:  # then the same words on the refusal channel
        if r.notice is None and r.message:
            hits = [n for n in notices if n.n not in used and n.d.get("channel") == "refusal" and clean(n.d.get("text")) == r.message
                    and r.ev.t - NOTICE_BEFORE_S <= n.t <= r.ev.t + NOTICE_AFTER_S]
            if hits:
                claim(r, min(hits, key=lambda n: abs(n.t - r.ev.t)), "the same words")
    refusal_stills = [s for s in stills if s.why == "refusal"]
    for r in refusals:
        if r.notice is not None:
            near = [s for s in refusal_stills if r.notice.t - 0.5 <= s.t <= r.notice.t + 3.0]
            r.still = near[0] if near else None
    stray = [n for n in notices if n.d.get("channel") == "refusal" and n.n not in used]
    return refusals, stray


@dataclass
class CommandRow:
    principal: str
    op: str
    ok: int = 0
    refused: int = 0
    unknown: int = 0
    slowest_ms: float = 0.0

    @property
    def total(self) -> int:
        return self.ok + self.refused + self.unknown


def principal_class(value: object) -> str:
    """player, companion, guest, guest_companion, system, or other: the kind of principal, never the record's own text."""
    match = re.match(r"^(player|companion|guest_companion|guest|system):", clean(value, 40))
    return match.group(1) if match else "other"


def is_companion(principal: str) -> bool:
    return principal.startswith("companion") or principal.startswith("guest_companion")


def command_rows(play: Play) -> list:
    rows: "dict[tuple, CommandRow]" = {}
    for e in play.timeline:
        if e.kind != "command":
            continue
        principal, op = clean(e.d.get("principal"), 40) or "(no principal)", clean(e.d.get("op"), 40) or "(no op)"
        row = rows.setdefault((principal, op), CommandRow(principal, op))
        ok, _code, _message = command_result(e)
        if ok is True:
            row.ok += 1
        elif ok is False:
            row.refused += 1
        else:
            row.unknown += 1
        row.slowest_ms = max(row.slowest_ms, num(e.d.get("ms")) or 0.0)
    return sorted(rows.values(), key=lambda r: (is_companion(r.principal), r.principal, -r.total, r.op))


def height_band(height: float) -> str:
    """The game's band (0.4 m each) a height above the floor falls in; a diver below the floor has its own."""
    if height < -0.005:
        return BELOW_FLOOR
    return band_label(max(1, int((height + 1e-9) // BAND_M) + 1) if height < BANDS_SHOWN * BAND_M else BANDS_SHOWN)


def budgets(play: Play) -> "dict[str, list]":
    """Where the play's time went: by body state, by height band, near the Gubble or not, and by companion link. A
    breadcrumb holds until the next, for at most BREADCRUMB_HOLD_CAP_S; the rest (before the first, a gap) is
    NO_BREADCRUMB, so each budget adds up to exactly the play's length."""
    names = ("state", "height", "gubble", "link")
    totals: "dict[str, dict]" = {name: {} for name in names}
    length = play.length_s
    samples = play.bodies

    def put(name: str, label: str, seconds: float) -> None:
        if seconds > 0:
            totals[name][label] = totals[name].get(label, 0.0) + seconds

    cursor = 0.0
    for i, e in enumerate(samples):
        start = min(max(e.t, cursor), length)
        put_gap = start - cursor
        end = min(samples[i + 1].t if i + 1 < len(samples) else length, length)
        end = max(end, start)
        hold = min(end - start, BREADCRUMB_HOLD_CAP_S)
        gap = put_gap + (end - start - hold)
        gubble = e.d.get("gubble") if isinstance(e.d.get("gubble"), dict) else {}
        distance, height = num(gubble.get("distance_m")), num(e.d.get("height_m"))
        labels = {
            "state": clean(e.d.get("state"), 24) or "(unknown)",
            "height": height_band(height) if height is not None else "(unknown)",
            "gubble": ("within %g m of the Gubble" % NEAR_GUBBLE_M if distance <= NEAR_GUBBLE_M else "farther than %g m from the Gubble" % NEAR_GUBBLE_M)
            if distance is not None else "(no Gubble reading)",
            "link": clean(gubble.get("link"), 24) or "(unknown)",
        }
        for name in names:
            put(name, labels[name], hold)
            put(name, NO_BREADCRUMB, gap)
        cursor = end
    for name in names:
        put(name, NO_BREADCRUMB, length - cursor)
    order = {"state": STATE_ORDER, "link": LINK_ORDER, "height": tuple(HEIGHT_LABELS),
             "gubble": ("within %g m of the Gubble" % NEAR_GUBBLE_M, "farther than %g m from the Gubble" % NEAR_GUBBLE_M)}
    out = {}
    for name in names:
        rank = {label: i for i, label in enumerate(order[name])}
        rows = sorted(totals[name].items(), key=lambda kv: (kv[0] == NO_BREADCRUMB, kv[0] == "(unknown)", rank.get(kv[0], len(rank)), kv[0]))
        out[name] = rows
    return out


@dataclass
class FrameRec:
    ev: Ev
    mean_ms: float
    longest_ms: float
    window_s: float


def frame_records(play: Play) -> list:
    out = []
    for e in play.timeline:
        if e.kind == "frame":
            longest = num(e.d.get("longest_ms"))
            if longest is not None:
                out.append(FrameRec(e, num(e.d.get("mean_ms")) or 0.0, longest, num(e.d.get("window_s")) or 0.0))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# Rendering. Each section returns lines; a section that cannot be built says so and the report goes on.

def body_summary(e: Optional[Ev], at: Optional[float] = None) -> str:
    if e is None:
        return "no breadcrumb yet"
    d = e.d
    gubble = d.get("gubble") if isinstance(d.get("gubble"), dict) else {}
    bits = [code(d.get("state"), 24) + " at " + fmt_vec(vec(d.get("position_m")))]
    height = num(d.get("height_m"))
    if height is not None:
        bits.append(f"{height:.2f} m above the floor")
    if d.get("level") not in (None, ""):
        bits.append("on " + code(d.get("level"), 60))
    distance = num(gubble.get("distance_m"))
    if distance is not None:
        bits.append(f"{distance:.1f} m from the Gubble" + (f" ({word(gubble.get('link'), 20)})" if gubble.get("link") else ""))
    if d.get("holding"):
        bits.append("holding " + code(d.get("holding"), 60))
    if d.get("view"):
        bits.append("view " + code(d.get("view"), 20))
    if at is not None:
        bits.append(f"breadcrumb {abs(at - e.t):.1f} s {'before' if e.t <= at else 'after'}")
    return ", ".join(bits)


def event_line(e: Ev, base: float) -> str:
    d, k = e.d, e.kind
    prefix = f"- `{fmt_rel(e.t - base)}` {k}: "
    if k == "command":
        ok, rcode, message = command_result(e)
        who, op = code(d.get("principal"), 40), code(op_of(e), 40)
        outcome = "ok" if ok else (f"refused {code(rcode, 40)} {code(message)}" if ok is False else "result unreadable")
        summary = request_summary(d.get("request"), 100)
        ms = num(d.get("ms"))
        return (prefix + f"{who} {op} {outcome}" + (f", asked {code(summary, 100)}" if summary else "") + (f", {ms:.1f} ms" if ms is not None else ""))
    if k == "action":
        return prefix + code(d.get("name"), 60) + (f" {code(d.get('detail'), 60)}" if d.get("detail") else "")
    if k == "notice":
        text = d.get("text")
        shown = "_line cleared_" if text in ("", None) else code(text)
        extra = (f", reason {code(d.get('reason'), 100)}" if d.get("reason") else "") + (f", code {code(d.get('code'), 40)}" if d.get("code") else "")
        return prefix + f"[{word(d.get('channel'), 12)}] {shown}{extra}"
    if k == "body":
        return prefix + body_summary(e)
    if k == "frame":
        return prefix + f"longest frame {num(d.get('longest_ms')) or 0:.1f} ms, mean {num(d.get('mean_ms')) or 0:.1f} ms at {fmt_vec(vec(d.get('position_m')))}"
    if k == "session":
        return prefix + f"companion link {code(d.get('state'), 12)}" + (f" ({code(d.get('principal'), 40)})" if d.get("principal") else "")
    if k == "stills_rate":
        beat, was = num(d.get("beat_s")), num(d.get("was_s"))
        return prefix + ("stills " + ("off" if not beat else f"every {beat:g} s") + (f" (was {'off' if not was else format(was, 'g') + ' s'})" if was is not None else ""))
    if k == "mark":
        return prefix + f"{code(d.get('flag'), 12)} mark {code(d.get('number'), 8)}"
    if k == "ready":
        return prefix + f"the room is up, spawn {fmt_vec(vec(d.get('spawn_m')))}"
    if k == "quit":
        return prefix + "the game quit"
    return prefix + "(not shown)"


def moved(a: Optional[tuple], b: Optional[tuple]) -> float:
    return math.dist(a, b) if a and b else math.inf


def thin_bodies(inside: list, at: float) -> tuple:
    """A breadcrumb every two seconds says the same thing while the body stands still: keep the first, any that differ
    from the last one kept (state, level, view, holding, link, or the body moved half a metre or the Gubble a metre),
    and the two either side of the mark. Returns (events kept, how many breadcrumbs were left out)."""
    bodies = [e for e in inside if e.kind == "body"]
    before = [e for e in bodies if e.t <= at]
    after = [e for e in bodies if e.t > at]
    always = {id(before[-1])} if before else set()
    always |= {id(after[0])} if after else set()
    keep, last = set(), None
    for e in bodies:
        d = e.d
        gubble = d.get("gubble") if isinstance(d.get("gubble"), dict) else {}
        now = (d.get("state"), d.get("level"), d.get("view"), d.get("holding") is not None, gubble.get("link"))
        distance = num(gubble.get("distance_m"))
        if (last is None or id(e) in always or now != last[0] or moved(vec(d.get("position_m")), last[1]) >= 0.5
                or (distance is not None and (last[2] is None or abs(distance - last[2]) >= 1.0))):
            keep.add(id(e))
            last = (now, vec(d.get("position_m")), distance)
    kept = [e for e in inside if e.kind != "body" or id(e) in keep]
    return kept, len(bodies) - len(keep)


def context_lines(play: Play, mark: Ev) -> list:
    """The twenty seconds before a mark and five after: commands, notices, actions, the body's breadcrumbs."""
    inside = [e for e in window(play, mark.t - WINDOW_BEFORE_S, mark.t + WINDOW_AFTER_S) if e.kind not in ("still",)
              and not (e.kind == "frame" and (num(e.d.get("longest_ms")) or 0) < HITCH_MS) and not (e.kind == "mark" and e.n == mark.n)]
    inside, left_out = thin_bodies(inside, mark.t)
    lines = [event_line(e, mark.t) for e in inside[:WINDOW_LINES]]
    if len(inside) > WINDOW_LINES:
        lines.append(f"- _{len(inside) - WINDOW_LINES} more events in this window are not shown._")
    if left_out:
        lines.append(f"- _{left_out} breadcrumb{'s' if left_out != 1 else ''} that differ little from the one before are not listed._")
    return lines or ["- _nothing recorded in this window._"]


def md_still(play: Play, name: str, alt: str, path_known: Optional[Path]) -> str:
    if not name:
        return "_No still is named for this._"
    if path_known is None:
        return f"_The still {code(name, 40)} is missing from this record._"
    return f"![{alt}](stills/{name})"


def section_line(play: Play, stills: list, marks: list, refusals: list, frames: list, tables: dict) -> list:
    m = play.manifest
    mode = word(m.get("mode"), 12)
    difficulty = word(m.get("difficulty"), 12)
    mode_text = (mode.capitalize() + (f" ({difficulty})" if difficulty else "")) if mode else "unknown mode"
    minutes = f"{play.length_s / 60:.1f} minutes"
    quit_text = "quit cleanly" if play.ended else "**did not quit cleanly**"
    room = code(m.get("room_id"), 64) if m.get("room_id") else "_unknown room_"
    line = f"**{room}**, {mode_text}, build {code(m.get('build'), 64) if m.get('build') else '_unknown_'}, {minutes}, {quit_text}."
    out = [line, ""]
    if not play.ended:
        out += [f"The last event is at {fmt_t(play.length_s)}.", ""]
    facts = []
    window_info = m.get("window") if isinstance(m.get("window"), dict) else {}
    if window_info:
        facts.append(f"Window {fmt_n(window_info.get('width'))}×{fmt_n(window_info.get('height'))}"
                     f"{', fullscreen' if window_info.get('fullscreen') is True else ', windowed' if window_info.get('fullscreen') is False else ''}")
    renderer = m.get("renderer") if isinstance(m.get("renderer"), dict) else {}
    if renderer:
        facts.append(f"Renderer {code(renderer.get('method'), 40)} on {code(renderer.get('adapter'), 120)}")
    if m.get("engine"):
        facts.append(f"Engine {code(m.get('engine'), 40)}")
    style = m.get("style") if isinstance(m.get("style"), dict) else {}
    if style:
        facts.append(f"Style {code(style.get('preset_id'), 64)} v{fmt_n(style.get('version'))}")
    rules = m.get("rules") if isinstance(m.get("rules"), dict) else {}
    if rules:
        facts.append(f"Rules {code(rules.get('rules_id'), 64)} v{fmt_n(rules.get('version'))}")
    if m.get("team_id"):
        facts.append(f"Team {code(m.get('team_id'), 64)}")
    if m.get("started_utc"):
        facts.append(f"Started {code(m.get('started_utc'), 40)} (folder {code(play.name, 80)})")
    if m.get("beat_s") is not None:
        facts.append(f"Steady stills every {fmt_n(m.get('beat_s'))} s at launch")
    kinds = Counter(e.kind for e in play.events)
    if play.events:
        facts.append(f"{len(play.events)} events: " + ", ".join(f"{n} {k}" for k, n in kinds.most_common() if not k.startswith("unknown:")))
    out += [f"- {f}" for f in facts]
    out.append("")
    return out


def section_glance(play: Play, stills: list, marks: list, refusals: list, frames: list, tables: dict) -> list:
    bullets = []
    off, keep = sum(1 for k in marks if k.flag == "off"), sum(1 for k in marks if k.flag == "keep")
    parts = ([f"{off} \"something's off\""] if off else []) + ([f"{keep} \"keep this\""] if keep else []) + ([f"{len(marks) - off - keep} other"] if len(marks) - off - keep else [])
    bullets.append(f"**Marks:** {len(marks)}" + (f" ({', '.join(parts)})" if marks else ""))
    if refusals:
        hidden = sum(1 for r in refusals if r.notice is None)
        bullets.append(f"**Refusals:** {len(refusals)}, {len(refusals) - hidden} shown on screen, {hidden} not shown on screen")
    else:
        bullets.append("**Refusals:** none")
    if frames:
        worst = max(frames, key=lambda f: (f.longest_ms, f.mean_ms))
        hitches = sum(1 for f in frames if f.longest_ms > HITCH_MS)
        bullets.append(f"**Frames:** worst window {worst.longest_ms:.1f} ms at {fmt_t(worst.ev.t)}; {hitches} of {len(frames)} windows had a frame over {HITCH_MS:g} ms")
    near = dict(tables.get("gubble", []))
    near_s = near.get("within %g m of the Gubble" % NEAR_GUBBLE_M)
    if near_s is not None and play.length_s > 0:
        bullets.append(f"**Gubble:** within {NEAR_GUBBLE_M:g} m for {100 * near_s / play.length_s:.0f}% of the play")
    sessions = [e for e in play.timeline if e.kind == "session" and e.d.get("state") == "started"]
    companion_commands = [e for e in play.timeline if e.kind == "command" and is_companion(clean(e.d.get("principal"), 40))]
    if sessions or companion_commands:
        refused = sum(1 for e in companion_commands if command_result(e)[0] is False)
        bullets.append(f"**Companion:** joined {len(sessions)} time{'s' if len(sessions) != 1 else ''}, {len(companion_commands)} commands ({refused} refused)")
    return ["## At a glance", ""] + [f"- {b}" for b in bullets] + [""]


def section_record_check(play: Play, *_ignored) -> list:
    if not play.notes:
        return []
    return ["## About this record", "", "What the reader could not use as it is, and went on without: fields and kinds it does not know "
            "(the contract allows them; a newer recorder may write them), and anything damaged or missing.", ""] + \
           [f"- {line}" for line in play.notes.lines()] + [""]


def section_marks(play: Play, stills: list, marks: list, *_ignored) -> list:
    out = ["## The marks", ""]
    if not marks:
        return out + ["No marks: the founder pressed neither F9 (something's off) nor F8 (keep this).", ""]
    by_name = {s.file: s for s in stills if s.file}
    out += [f"{len(marks)} mark{'s' if len(marks) != 1 else ''}, in order. Each shows its still, where the body was, and what happened in "
            f"the {WINDOW_BEFORE_S:g} seconds before and the {WINDOW_AFTER_S:g} after (`-` is before the mark).", ""]
    for k in marks:
        t = k.ev.t
        out += [f"### {k.label} at {fmt_t(t)}", ""]
        rec = by_name.get(k.still)
        out += [md_still(play, k.still, "Keep" if k.flag == "keep" else "Mark", rec.path if rec else still_path(play.folder, k.still)), ""]
        body = body_before(play, t)
        position = rec.position if rec else None
        out.append(f"**Where the body was:** {body_summary(body, t)}" + (f"; at the mark {fmt_vec(position)}" if position else ""))
        out += ["", f"**Around it** ({fmt_rel(-WINDOW_BEFORE_S)} to {fmt_rel(WINDOW_AFTER_S)}):", ""] + context_lines(play, k.ev) + [""]
    return out


def section_refusals(play: Play, stills: list, marks: list, refusals: list, frames: list, tables: dict, stray: list) -> list:
    out = ["## Refusals", ""]
    if not refusals:
        out.append("No command was refused.")
    else:
        groups: "dict[str, list]" = {}
        for r in refusals:
            groups.setdefault(r.code, []).append(r)
        hidden = sum(1 for r in refusals if r.notice is None)
        out += [f"{len(refusals)} refused, {len(refusals) - hidden} shown on screen and {hidden} not shown on screen. "
                "A notice is matched by `action_id`, else by code and time, else by the same words.", ""]
        for refusal_code, items in sorted(groups.items(), key=lambda kv: (-len(kv[1]), kv[0])):
            ops = Counter(op_of(r.ev) for r in items)
            out += [f"### {code(refusal_code, 40)}: {len(items)} time{'s' if len(items) != 1 else ''} ({', '.join(f'{code(o, 40)} ×{n}' for o, n in ops.most_common(4))})", ""]
            for r in items:
                d = r.ev.d
                head = f"- `{fmt_t(r.ev.t)}` {code(d.get('principal'), 40)} {code(d.get('op'), 40)}"
                head += f" {code(d.get('action_id'), 64)}" if d.get("action_id") else ""
                head += f", the host said {code(r.message)}" if r.message else ""
                if r.notice is not None:
                    n = r.notice.d
                    seen = f"shown on screen at `{fmt_t(r.notice.t)}` ([{word(n.get('channel'), 12)}] {code(n.get('text'))}), matched by {r.how}"
                    if r.still is not None:
                        seen += f"; still {code(r.still.file, 40)}"
                else:
                    seen = "**not shown on screen**"
                out.append(f"{head}: {seen}")
            out.append("")
    if stray:
        out += ["Refusal notices the screen showed that no refused command claimed:", ""]
        out += [f"- `{fmt_t(n.t)}` {code(n.d.get('text'))}" + (f" (code {code(n.d.get('code'), 40)})" if n.d.get("code") else "") for n in stray[:20]]
        out.append("")
    return out


def table(headers: list, rows: list, align: Optional[list] = None) -> list:
    align = align or ["---"] * len(headers)
    lines = ["| " + " | ".join(headers) + " |", "| " + " | ".join(align) + " |"]
    lines += ["| " + " | ".join(str(c) for c in row) + " |" for row in rows]
    return lines + [""]


def section_commands(play: Play, *_ignored) -> list:
    rows = command_rows(play)
    out = ["## Commands", ""]
    if not rows:
        return out + ["No commands went through the host.", ""]

    def render(selected: list) -> list:
        body = [[code(r.principal, 40), code(r.op, 40), r.total, r.ok, r.refused, f"{r.slowest_ms:.1f}"] for r in selected]
        body.append(["**total**", "", sum(r.total for r in selected), sum(r.ok for r in selected), sum(r.refused for r in selected),
                     f"{max((r.slowest_ms for r in selected), default=0):.1f}"])
        return table(["Principal", "Op", "Commands", "OK", "Refused", "Slowest ms"], body, ["---", "---", "---:", "---:", "---:", "---:"])

    others = [r for r in rows if not is_companion(r.principal)]
    mine = [r for r in rows if is_companion(r.principal)]
    out += ["### The player and everyone else", ""] + (render(others) if others else ["None.", ""])
    out += ["### The companion's part", ""]
    if not mine:
        return out + ["The companion sent no commands.", ""]
    queries = sum(r.total for r in mine if r.op in QUERY_OPS)
    out += [f"{sum(r.total for r in mine)} commands: {queries} queries and {sum(r.total for r in mine) - queries} that change or ask to change the room, "
            f"{sum(r.refused for r in mine)} refused.", ""] + render(mine)
    unknown = sum(r.unknown for r in rows)
    if unknown:
        out += [f"{unknown} command line{'s' if unknown != 1 else ''} had a result the reader could not read; they count in the totals only.", ""]
    return out


def section_time(play: Play, stills: list, marks: list, refusals: list, frames: list, tables: dict) -> list:
    out = ["## Where the time went", ""]
    if play.length_s <= 0 or not play.bodies:
        return out + ["No breadcrumbs, so no time budgets.", ""]
    out += [f"Breadcrumbs come every {BREADCRUMB_S:g} seconds; one holds until the next, for at most {BREADCRUMB_HOLD_CAP_S:g} s, and anything "
            f"before the first or in a gap is {code(NO_BREADCRUMB)}. Each table adds up to the play's length, {fmt_dur(play.length_s)}.", ""]
    titles = {"state": "By body state", "height": f"By height above the floor (the game's {BAND_M:g} m bands)", "gubble": f"Near the Gubble ({NEAR_GUBBLE_M:g} m)",
              "link": "By companion link"}
    for name in ("state", "height", "gubble", "link"):
        rows = tables[name]
        seconds = round_to_sum([s for _l, s in rows], play.length_s, 0.1)
        shares = round_to_sum([100 * s / play.length_s for _l, s in rows], 100.0, 1.0)
        body = [[code(label, 60) if label != NO_BREADCRUMB else code(label), fmt_dur(sec), f"{share:.0f}%"] for (label, _s), sec, share in zip(rows, seconds, shares)]
        body.append(["**total**", fmt_dur(round(play.length_s * 10) / 10), "100%"])
        out += [f"### {titles[name]}", ""] + table(["", "Time", "Share"], body, ["---", "---:", "---:"])
    return out


def section_frames(play: Play, stills: list, marks: list, refusals: list, frames: list, tables: dict) -> list:
    out = ["## Frames", ""]
    if not frames:
        return out + ["No frame windows were recorded.", ""]
    covered = sum(f.window_s for f in frames)
    mean = sum(f.mean_ms * f.window_s for f in frames) / covered if covered > 0 else sum(f.mean_ms for f in frames) / len(frames)
    hitches = sum(1 for f in frames if f.longest_ms > HITCH_MS)
    out += [f"{len(frames)} windows; mean frame {mean:.1f} ms overall; {hitches} had a frame over {HITCH_MS:g} ms. "
            f"The {min(WORST_FRAMES, len(frames))} worst by longest single frame (the stutters):", ""]
    worst = sorted(frames, key=lambda f: (-f.longest_ms, -f.mean_ms, f.ev.t))[:WORST_FRAMES]
    body = []
    for f in worst:
        d = f.ev.d
        began = f.ev.t - f.window_s
        near = body_before(play, f.ev.t)
        state = clean(near.d.get("state"), 24) if near is not None else ""
        doing = [e for e in window(play, began, f.ev.t) if e.kind == "command" and op_of(e) not in QUERY_OPS]
        ops = ", ".join(f"{code(op_of(e), 40)} ({principal_class(e.d.get('principal'))})" for e in doing[:3])
        body.append([f"{fmt_t(began)}–{fmt_t(f.ev.t)}", f"{f.longest_ms:.1f}", f"{f.mean_ms:.1f}",
                     f"{num(d.get('physics_ms')):.1f}" if num(d.get("physics_ms")) is not None else "", fmt_vec(vec(d.get("position_m"))),
                     code(state, 24) if state else "", ops or ""])
    out += table(["Window", "Longest ms", "Mean ms", "Physics ms", "Position", "Body", "Commands that changed things (not queries)"], body,
                 ["---", "---:", "---:", "---:", "---", "---", "---"])
    # Where the record says what the worst window's longest frame spent its time on (the recorder since the first recorded play's
    # fix round: the engine's process and physics monitors at that frame, the window's collections by generation and their pauses).
    split = worst[0].ev.d
    parts = []
    if num(split.get("longest_process_ms")) is not None:
        parts.append(f"process {num(split.get('longest_process_ms')):.1f} ms, physics {num(split.get('longest_physics_ms')) or 0:.1f} ms")
    if num(split.get("gc_pause_ms")) is not None:
        parts.append(f"the window's collections {code(split.get('gc_collections', ''), 16)} (gen 0/1/2) paused {num(split.get('gc_pause_ms')):.1f} ms")
    if parts:
        out += [f"The worst window's longest frame ({fmt_t(worst[0].ev.t - worst[0].window_s)}–{fmt_t(worst[0].ev.t)}), as the record splits it: " + "; ".join(parts) + ".", ""]
    slow = sorted(frames, key=lambda f: (-f.mean_ms, f.ev.t))[:3]
    out += ["Those are the biggest single stutters. The windows that were slow all the way through (the highest mean frame time): "
            + ", ".join(f"{fmt_t(f.ev.t - f.window_s)}–{fmt_t(f.ev.t)} at {f.mean_ms:.1f} ms (longest {f.longest_ms:.1f})" for f in slow) + ".", ""]
    return out


def section_notices(play: Play, *_ignored) -> list:
    notices = [e for e in play.timeline if e.kind == "notice"]
    out = ["## Notices, in order", "", "What the screen told the player: the HUD line, the Gubble's bubble, the status cue and the refusals shown.", ""]
    if not notices:
        return out + ["No notice was recorded.", ""]
    for e in notices[:NOTICES_SHOWN]:
        d = e.d
        text = d.get("text")
        shown = "_line cleared_" if text in ("", None) else code(text)
        extra = (f"; reason {code(d.get('reason'), 100)}" if d.get("reason") else "") + (f"; code {code(d.get('code'), 40)}" if d.get("code") else "")
        out.append(f"- `{fmt_t(e.t)}` [{word(d.get('channel'), 12)}] {shown}{extra}")
    if len(notices) > NOTICES_SHOWN:
        out.append(f"- _{len(notices) - NOTICES_SHOWN} more notices not shown._")
    return out + [""]


def section_stills(play: Play, stills: list, marks: list, refusals: list, frames: list, tables: dict, sheet_note: str, sheet_items: list) -> list:
    out = ["## Stills", ""]
    if not stills:
        return out + ["No stills were recorded.", ""]
    present = sum(1 for s in stills if s.path is not None)
    why = Counter(s.why for s in stills)
    out += [f"{len(stills)} stills named in the record, {present} on disk" + (f", {len(stills) - present} missing" if present != len(stills) else "")
            + ": " + ", ".join(f"{n} {w}" for w, n in why.most_common()) + ".", ""]
    stills_dir = play.folder / "stills"
    try:
        named = {s.file for s in stills if s.file}
        extra = sum(1 for p in stills_dir.iterdir() if p.name not in named) if stills_dir.is_dir() else 0
    except OSError:
        extra = 0
    if extra:
        out += [f"{extra} file{'s' if extra != 1 else ''} in `stills/` that no event names (not opened).", ""]
    if sheet_note:
        out += [f"No contact sheet: {sheet_note}.", ""]
    else:
        out += [f"The contact sheet has {len(sheet_items)} of them (at most {SHEET_MAX}): marks and refusals first, then the rest in time.", "",
                "![Contact sheet](sheet.jpg)", ""]
    return out + table(["Time", "File", "Why", "View", "Position", "Size", "On disk"], still_rows(stills))


def still_rows(stills: list) -> list:
    """One row per still, except that a run of steady stills in a row is one row (the beat's own pictures are not worth a line each)."""
    rows, i = [], 0
    while i < len(stills):
        s = stills[i]
        if s.why == "steady" and s.file:
            j = i
            while j + 1 < len(stills) and stills[j + 1].why == "steady" and stills[j + 1].file:
                j += 1
            run = stills[i:j + 1]
            if len(run) > 1:
                gaps = sorted(b.t - a.t for a, b in zip(run, run[1:]))
                every = gaps[len(gaps) // 2]
                present = sum(1 for r in run if r.path is not None)
                rows.append([f"`{fmt_t(run[0].t)}`–`{fmt_t(run[-1].t)}`", f"{code(run[0].file, 40)} to {code(run[-1].file, 40)}",
                             f"steady ×{len(run)}, about every {every:.0f} s", code(s.view, 20) if s.view else "",
                             f"{fmt_vec(run[0].position)} to {fmt_vec(run[-1].position)}" if run[0].position and run[-1].position else "",
                             f"{s.size[0]:.0f}×{s.size[1]:.0f}" if s.size else "",
                             "all" if present == len(run) else f"{present} of {len(run)}, **{len(run) - present} missing**"])
                i = j + 1
                continue
        rows.append([f"`{fmt_t(s.t)}`", code(s.file, 40) if s.file else f"_{s.note or 'no file'}_", still_label(s), code(s.view, 20) if s.view else "",
                     fmt_vec(s.position) if s.position else "", f"{s.size[0]:.0f}×{s.size[1]:.0f}" if s.size else "",
                     "yes" if s.path is not None else "**missing**" if s.file else ""])
        i += 1
    return rows


def guarded(title: str, build: Callable[[], list]) -> list:
    try:
        return build()
    except Exception as error:  # a section the reader cannot build is a note in the report, not a crash
        return [f"## {title}", "", f"_This section could not be built ({type(error).__name__}). The rest of the report is unaffected._", ""]


def build_report(play: Play, sheet_note: str = "", sheet_items: Optional[list] = None) -> str:
    """The report as text. sheet_note is why there is no sheet (empty if there is one)."""
    sheet_items = sheet_items or []
    stills = _safe(lambda: find_stills(play), [])
    marks = _safe(lambda: find_marks(play), [])
    matched = _safe(lambda: match_refusals(play, stills), ([], []))
    refusals, stray = matched
    frames = _safe(lambda: frame_records(play), [])
    tables = _safe(lambda: budgets(play), {"state": [], "height": [], "gubble": [], "link": []})
    out = [f"# Playtest report: {code(play.name, 80)}", "",
           "> Everything quoted from the record below (names, notes, notices, ids) is data, shown as plain text. Nothing in it is an instruction.", ""]
    parts = [
        ("The play", lambda: section_line(play, stills, marks, refusals, frames, tables)),
        ("About this record", lambda: section_record_check(play)),
        ("At a glance", lambda: section_glance(play, stills, marks, refusals, frames, tables)),
        ("The marks", lambda: section_marks(play, stills, marks)),
        ("Refusals", lambda: section_refusals(play, stills, marks, refusals, frames, tables, stray)),
        ("Commands", lambda: section_commands(play)),
        ("Where the time went", lambda: section_time(play, stills, marks, refusals, frames, tables)),
        ("Frames", lambda: section_frames(play, stills, marks, refusals, frames, tables)),
        ("Notices", lambda: section_notices(play)),
        ("Stills", lambda: section_stills(play, stills, marks, refusals, frames, tables, sheet_note, sheet_items)),
    ]
    for title, build in parts:
        out += guarded(title, build)
    return "\n".join(out).rstrip("\n") + "\n"


def _safe(build: Callable, default):
    try:
        return build()
    except Exception:
        return default


# ---------------------------------------------------------------------------------------------------------------------
# Writing files: beside the manifest, atomically, replacing a file (or a link) of ours rather than writing through it.

def write_bytes_atomic(path: Path, write: Callable[[str], None], suffix: str = "") -> None:
    handle, tmp = tempfile.mkstemp(prefix=".report-", suffix=suffix, dir=str(path.parent))
    os.close(handle)
    try:
        write(tmp)
        try:
            os.chmod(tmp, 0o644)
        except OSError:
            pass
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def write_report(folder: Path) -> dict:
    """Reads the play and writes REPORT.md and sheet.jpg beside its manifest. Returns what happened."""
    folder = Path(folder)
    play = load_play(folder)
    stills = find_stills(play)
    items = sheet_selection(stills)
    sheet_path = folder / "sheet.jpg"
    sheet_note = make_sheet(items, sheet_path)
    text = build_report(play, sheet_note, items)
    report_path = folder / "REPORT.md"
    write_bytes_atomic(report_path, lambda tmp: Path(tmp).write_bytes(text.encode("utf-8")), suffix=".md")
    return {"report": report_path, "sheet": None if sheet_note else sheet_path, "sheet_note": sheet_note, "play": play, "stills": len(stills),
            "sheet_items": len(items)}


# ---------------------------------------------------------------------------------------------------------------------
# --list: every play in a folder of plays.

def _user_folder(name: str) -> Path:
    if os.name == "nt":
        base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
        return Path(base) / "Godot" / "app_userdata" / name
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "Godot" / "app_userdata" / name
    base = os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local" / "share")
    return Path(base) / "godot" / "app_userdata" / name


def default_plays_folder() -> Path:
    """The game's plays: user://playtests under Garage-Game, or under the room-era EnFractal folder while the renamed
    build has not run yet on this machine (its first launch copies the plays across; docs/runs/GARAGE-GAME-RENAME.md)."""
    plays = _user_folder("Garage-Game") / "playtests"
    old = _user_folder("EnFractal") / "playtests"
    return old if not plays.is_dir() and old.is_dir() else plays


@dataclass
class PlayRow:
    name: str
    date: str
    room: str
    minutes: Optional[float]
    marks: Optional[int]
    clean_quit: bool
    has_report: bool
    unreadable: bool


def is_play_folder(path: Path) -> bool:
    return path.is_dir() and not path.is_symlink() and ((path / "manifest.json").is_file() or (path / "events.jsonl").is_file())


def play_row(folder: Path) -> PlayRow:
    notes = Notes()
    manifest = read_manifest(folder, notes)
    ended = isinstance(manifest.get("ended_utc"), str) and bool(manifest.get("ended_utc"))
    counts = manifest.get("counts") if isinstance(manifest.get("counts"), dict) else {}
    played, marks = num(manifest.get("played_s")), num(counts.get("marks"))
    if not (ended and played is not None and marks is not None):  # not a clean quit: the events say how far it got
        play = load_play(folder)
        played = play.length_s if play.events else played
        marks = float(sum(1 for e in play.events if e.kind == "mark"))
    match = RECORD_ID.match(folder.name)
    date = f"{match.group(1)}-{match.group(2)}-{match.group(3)} {match.group(4)}:{match.group(5)}" if match else clean(manifest.get("started_utc"), 20)
    report = folder / "REPORT.md"
    return PlayRow(folder.name, date or "unknown date", clean(manifest.get("room_id"), 40) or "unknown room",
                   played / 60 if played is not None else None, int(marks) if marks is not None else None, ended,
                   report.is_file(), not manifest)


def list_plays(root: Path) -> list:
    rows = []
    try:
        entries = sorted(root.iterdir(), key=lambda p: p.name, reverse=True)
    except OSError:
        return rows
    for entry in entries:
        if is_play_folder(entry):
            try:
                rows.append(play_row(entry))
            except Exception:  # a play the reader cannot summarise is still a play
                rows.append(PlayRow(clean(entry.name, 80), "unknown date", "unknown room", None, None, False, (entry / "REPORT.md").is_file(), True))
    return rows


def format_list(root: Path, rows: list) -> str:
    if not rows:
        return f"No plays under {root}\n"
    lines = [f"{len(rows)} play{'s' if len(rows) != 1 else ''} under {root}, newest first:"]
    width = max(len(r.room) for r in rows)
    for r in rows:
        minutes = f"{r.minutes:5.1f} min" if r.minutes is not None else "    ? min"
        marks = f"{r.marks} mark{'s' if r.marks != 1 else ''}" if r.marks is not None else "? marks"
        status = "quit cleanly" if r.clean_quit else "NOT QUIT CLEANLY" if not r.unreadable else "NO MANIFEST"
        lines.append(f"{r.date}  {r.room:<{width}}  {minutes}  {marks:<8}  {status:<16}  report: {'yes' if r.has_report else 'no':<3}  {clean(r.name, 80)}")
    return "\n".join(lines) + "\n"


def plural(n: int, noun: str) -> str:
    return f"{n} {noun}{'' if n == 1 else 's'}"


def main(argv: Optional[list] = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(errors="replace")
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder", nargs="?", type=Path, help="a play's folder (to report on it), or with --list a folder of plays")
    parser.add_argument("--list", action="store_true", help="name every play under the folder (default: the game's user folder)")
    args = parser.parse_args(argv)
    if args.list:
        root = args.folder or default_plays_folder()
        if not root.is_dir():
            print(f"report.py: no folder of plays at {root}", file=sys.stderr)
            return 2
        sys.stdout.write(format_list(root, list_plays(root)))
        return 0
    if args.folder is None:
        parser.error("give a play's folder, or --list")
    folder = args.folder
    if not folder.is_dir() or not ((folder / "manifest.json").exists() or (folder / "events.jsonl").exists()):
        print(f"report.py: {folder} is not a play's folder (no manifest.json or events.jsonl)", file=sys.stderr)
        return 2
    try:
        result = write_report(folder)
    except OSError as error:
        print(f"report.py: could not write beside the manifest: {type(error).__name__}", file=sys.stderr)
        return 1
    play = result["play"]
    print(f"{clean(play.name, 80)}: {play.length_s / 60:.1f} minutes, {'quit cleanly' if play.ended else 'NOT quit cleanly'}, "
          f"{plural(sum(1 for e in play.events if e.kind == 'mark'), 'mark')}, {plural(result['stills'], 'still')}")
    print(f"wrote {result['report']}")
    print(f"wrote {result['sheet']} ({result['sheet_items']} stills)" if result["sheet"] else f"no contact sheet: {result['sheet_note']}")
    if play.notes:
        print(f"{len(play.notes.items)} note{'s' if len(play.notes.items) != 1 else ''} about the record (see the report's 'About this record')")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
