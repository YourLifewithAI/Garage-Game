"""C1 ingest: read originals in place, write clean JPEGs, metadata, quality and a session manifest.

Layout under ``<captures>/<room>/``::

    photos/<sha16>.jpg              full-resolution JPEG, orientation applied, allow-listed EXIF only
    thumbs/<sha16>.jpg              384 px preview for reports, no EXIF
    cache/ingest-v<N>/<sha256>.json per-photo analysis, reused when the same bytes come back
    sessions/<session_id>/manifest.json   the session: every photo's name, size, SHA-256 and results
    sessions/<session_id>/ingest-report.md
    sessions/<session_id>/ingest-run.json  timings, versions, source folder (not part of the result)
    sessions/index.json, latest-session.txt

Derived files are named by the first 16 hex digits of the source's SHA-256 and nothing else, so the
same bytes always give the same file whatever the source was called when it was first converted.

A session id is derived from the sorted (name, size, SHA-256) listing together with the ingest
version and the quality thresholds, so the same photo set under the same rules always maps to the
same session, adding photos creates a new session without touching an earlier one, and a change of
rules makes a new session instead of overwriting the old one. As a second guard, an existing
session's manifest is never replaced by a different one (``SessionConflictError``).
"""

from __future__ import annotations

import concurrent.futures as cf
import hashlib
import io
import os
import platform
import re
import time
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

import numpy as np
from PIL import Image, ImageOps

from . import __version__, ids
from . import exif as exifmod
from . import quality
from .imageio import register_heif
from .jsonio import canonical_bytes, json_bytes, sha256_hex, text_bytes
from .paths import OutputGuard, check_source_and_output, room_root
from .textutil import md_text

INGEST_VERSION = 3  # 2: adds edge_sharpness; 3: derived files named by the bytes alone
PHOTO_EXTENSIONS = {".heic", ".heif", ".jpg", ".jpeg", ".png"}
JPEG_QUALITY = 92
THUMB_LONG_SIDE = 384
_COPY_SUFFIX = re.compile(r" \((\d+)\)(?=\.[^.]+$)")


@dataclass(frozen=True)
class SourcePhoto:
    name: str  # POSIX path relative to the source folder
    path: Path
    size: int


def photo_sort_key(name: str) -> tuple[str, int, str]:
    """Base names before their ' (1)' copies, so the original is the one kept on exact duplicates."""
    match = _COPY_SUFFIX.search(name)
    copy = int(match.group(1)) if match else 0
    return (_COPY_SUFFIX.sub("", name).lower(), copy, name)


def scan_source(source: Path) -> list[SourcePhoto]:
    root = Path(source).resolve()
    found: list[SourcePhoto] = []
    for path in root.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in PHOTO_EXTENSIONS:
            continue
        if any(part.startswith(".") for part in path.relative_to(root).parts):
            continue
        rel = PurePosixPath(*path.relative_to(root).parts).as_posix()
        found.append(SourcePhoto(rel, path, path.stat().st_size))
    found.sort(key=lambda p: photo_sort_key(p.name))
    return found


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _encode_jpeg(img: Image.Image, *, quality_level: int, exif: bytes | None, icc: bytes | None) -> bytes:
    buf = io.BytesIO()
    kwargs: dict[str, Any] = {"quality": quality_level}
    if exif:
        kwargs["exif"] = exif
    if icc:
        kwargs["icc_profile"] = icc
    img.save(buf, "JPEG", **kwargs)
    return buf.getvalue()


def analyse_photo(job: dict[str, Any]) -> dict[str, Any]:
    """Worker: one unique source photo -> derived files and analysis. Reads the source once."""
    register_heif()
    path = Path(job["path"])
    room_dir = Path(job["room_dir"])
    with open(path, "rb") as fh:  # read-only; the original is never reopened for writing
        data = fh.read()
    sha = sha256_hex(data)
    if sha != job["sha256"]:
        raise RuntimeError(f"{job['name']} changed while being read; rerun the ingest.")
    with Image.open(io.BytesIO(data)) as opened:
        fields = exifmod.read_fields(opened)
        had_location = exifmod.had_location(opened)
        cleaned = exifmod.clean_exif(opened).tobytes()
        icc = opened.info.get("icc_profile")
        source_orientation = fields.pop("orientation")
        # pillow-heif applies HEIF rotation itself and reports orientation 1; JPEGs rotate here.
        img = ImageOps.exif_transpose(opened)
        img = img.convert("RGB")
    stem = sha[:16]  # the bytes alone name the files, so a cache hit and a fresh conversion agree
    jpeg = _encode_jpeg(img, quality_level=JPEG_QUALITY, exif=cleaned, icc=icc)
    exifmod.assert_no_location(jpeg, f"{stem}.jpg")
    thumb_img = img.copy()
    thumb_img.thumbnail((THUMB_LONG_SIDE, THUMB_LONG_SIDE), Image.Resampling.LANCZOS)
    thumb = _encode_jpeg(thumb_img, quality_level=85, exif=None, icc=icc)
    exifmod.assert_no_location(thumb, f"{stem} thumbnail")

    guard = OutputGuard(room_dir, forbidden=Path(job["source_root"]))
    jpeg_path = guard.write_bytes(f"photos/{stem}.jpg", jpeg)
    thumb_path = guard.write_bytes(f"thumbs/{stem}.jpg", thumb)

    gray = quality.analysis_gray(img)
    analysis = {
        "ingest_version": INGEST_VERSION,
        "sha256": sha,
        "size_bytes": len(data),
        "exif": fields,
        "source_had_location": had_location,
        "source_orientation": source_orientation,
        "image": {"width": img.width, "height": img.height},
        "quality": {**quality.sharpness(gray), "edge_sharpness": quality.edge_sharpness(gray), **quality.exposure(gray)},
        "phash": f"{quality.phash(gray):016x}",
        "thumb_vector": [round(float(v), 4) for v in quality.thumb_vector(gray).flatten()],
        "thumb_shape": list(quality.thumb_vector(gray).shape),
        "derived": {
            "jpeg": guard.rel(jpeg_path),
            "jpeg_sha256": sha256_hex(jpeg),
            "thumb": guard.rel(thumb_path),
            "thumb_sha256": sha256_hex(thumb),
        },
    }
    guard.write_bytes(f"cache/ingest-v{INGEST_VERSION}/{sha}.json", json_bytes(analysis))
    return analysis


def _cached(guard: OutputGuard, sha: str) -> dict[str, Any] | None:
    import json

    cache_file = guard.path(f"cache/ingest-v{INGEST_VERSION}/{sha}.json")
    if not cache_file.is_file():
        return None
    try:
        record = json.loads(cache_file.read_bytes())
        for key in ("jpeg", "thumb"):
            target = guard.path(record["derived"][key])
            if not target.is_file() or _sha256_file(target) != record["derived"][f"{key}_sha256"]:
                return None
        return record
    except (ValueError, KeyError, OSError):
        return None


class SessionConflictError(RuntimeError):
    """A session with this id already has a different manifest; it is left as it is."""


def session_id_for(listing: list[dict[str, Any]], thresholds: quality.Thresholds = quality.Thresholds(),
                   ingest_version: int = INGEST_VERSION) -> str:
    """The same photos under the same rules, and only those, give the same session."""
    entries = sorted([p["name"], p["size_bytes"], p["sha256"]] for p in listing)
    return "s-" + sha256_hex(canonical_bytes({"photos": entries, "ingest_version": ingest_version,
                                              "thresholds": thresholds.as_dict()}))[:12]


def _near_duplicate_groups(uniques: list[dict[str, Any]], t: quality.Thresholds) -> list[list[int]]:
    hashes = [int(u["phash"], 16) for u in uniques]
    vectors = [np.asarray(u["thumb_vector"], dtype=np.float32).reshape(u["thumb_shape"]) for u in uniques]
    parent = list(range(len(uniques)))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i in range(len(uniques)):
        for j in range(i + 1, len(uniques)):
            if quality.hamming(hashes[i], hashes[j]) > t.near_dup_hash_bits:
                continue
            if quality.thumb_rms(vectors[i], vectors[j]) > t.near_dup_rms_max:
                continue
            parent[find(j)] = find(i)
    groups: dict[int, list[int]] = {}
    for i in range(len(uniques)):
        groups.setdefault(find(i), []).append(i)
    return [sorted(g) for g in groups.values() if len(g) > 1]


def _capture_key(exif: dict[str, Any] | None, name: str) -> tuple[str, str, str]:
    exif = exif or {}
    return (exif.get("datetime_original") or "9999", (exif.get("subsec_original") or "").ljust(3, "0"), name)


def ingest(
    source: Path,
    room: str,
    captures_root: Path,
    *,
    workers: int | None = None,
    thresholds: quality.Thresholds = quality.Thresholds(),
    log=print,
) -> dict[str, Any]:
    started = time.perf_counter()
    source = Path(source).resolve()
    room_dir = room_root(captures_root, room)
    check_source_and_output(source, room_dir)
    guard = OutputGuard(room_dir, forbidden=source)
    guard.mkdir()

    photos = scan_source(source)
    if not photos:
        raise ValueError(f"No photos ({', '.join(sorted(PHOTO_EXTENSIONS))}) in {source}")
    log(f"Found {len(photos)} photos in the source folder; hashing (read-only)...")
    t_hash = time.perf_counter()
    listing = []
    for p in photos:
        listing.append({"name": p.name, "size_bytes": p.size, "sha256": _sha256_file(p.path), "_path": p.path})
    hash_s = time.perf_counter() - t_hash

    first_by_sha: dict[str, dict[str, Any]] = {}
    for entry in listing:
        first_by_sha.setdefault(entry["sha256"], entry)

    analyses: dict[str, dict[str, Any]] = {}
    errors: dict[str, str] = {}
    todo = []
    for sha, entry in first_by_sha.items():
        hit = _cached(guard, sha)
        if hit is not None:
            analyses[sha] = hit
        else:
            todo.append({
                "name": entry["name"],
                "path": str(entry["_path"]),
                "sha256": sha,
                "room_dir": str(room_dir),
                "source_root": str(source),
            })
    log(f"{len(first_by_sha)} unique files; {len(analyses)} cached, {len(todo)} to convert.")
    t_conv = time.perf_counter()
    worker_count = workers or max(1, min(6, (os.cpu_count() or 2) - 2))
    def collect(done: int, job: dict[str, Any], get) -> None:
        try:
            analyses[job["sha256"]] = get()
        except Exception as exc:  # unreadable or corrupt file: record and continue
            errors[job["sha256"]] = f"{type(exc).__name__}: {exc}"
        if done % 25 == 0 or done == len(todo):
            log(f"  converted {done}/{len(todo)}")

    if todo and worker_count == 1:
        for done, job in enumerate(todo, 1):
            collect(done, job, lambda job=job: analyse_photo(job))
    elif todo:
        with cf.ProcessPoolExecutor(max_workers=worker_count) as pool:
            futures = {pool.submit(analyse_photo, job): job for job in todo}
            for done, future in enumerate(cf.as_completed(futures), 1):
                collect(done, futures[future], future.result)
    convert_s = time.perf_counter() - t_conv

    # Near-duplicates among the readable unique photos.
    unique_shas = [sha for sha in first_by_sha if sha in analyses]
    uniques = [analyses[sha] for sha in unique_shas]
    near_groups = _near_duplicate_groups(uniques, thresholds)

    records: list[dict[str, Any]] = []
    by_sha_name = {sha: entry["name"] for sha, entry in first_by_sha.items()}
    for entry in listing:
        sha = entry["sha256"]
        rec: dict[str, Any] = {
            "name": entry["name"],
            "size_bytes": entry["size_bytes"],
            "sha256": sha,
            "status": "ok",
            "duplicate_of": None,
            "near_duplicate_of": None,
        }
        if sha in errors:
            rec["status"] = "unreadable"
            rec["error"] = errors[sha]
        elif by_sha_name[sha] != entry["name"]:
            rec["status"] = "duplicate"
            rec["duplicate_of"] = by_sha_name[sha]
        a = analyses.get(sha)
        if a is not None:
            rec.update({
                "exif": a["exif"],
                "lens_kind": exifmod.lens_kind(a["exif"]),
                "source_had_location": a["source_had_location"],
                "image": a["image"],
                "quality": dict(a["quality"]),
                "phash": a["phash"],
                "derived": a["derived"],
            })
        records.append(rec)

    primary = [r for r in records if r["status"] == "ok"]
    session_median = quality.flag_photos(primary, thresholds)
    for r in records:  # duplicates share the analysis of the photo they duplicate
        if r["status"] == "duplicate":
            src = next(x for x in records if x["name"] == r["duplicate_of"])
            r["quality"] = dict(src.get("quality") or {})

    by_name = {r["name"]: r for r in records}
    near_report = []
    for group in near_groups:
        names = [by_sha_name[unique_shas[i]] for i in group]
        keeper = max(names, key=lambda n: (by_name[n]["quality"]["sharpness"], [-ord(c) for c in n]))
        for n in names:
            if n != keeper:
                by_name[n]["status"] = "near_duplicate"
                by_name[n]["near_duplicate_of"] = keeper
        near_report.append({"keep": keeper, "others": sorted((n for n in names if n != keeper), key=photo_sort_key)})
    near_report.sort(key=lambda g: photo_sort_key(g["keep"]))

    order = sorted((r for r in records if r.get("exif")), key=lambda r: _capture_key(r["exif"], r["name"]))
    for index, r in enumerate(order):
        r["capture_index"] = index

    exact_groups: dict[str, list[str]] = {}
    for r in records:
        exact_groups.setdefault(r["sha256"], []).append(r["name"])
    exact_report = [names for names in exact_groups.values() if len(names) > 1]

    session_id = session_id_for(listing, thresholds)
    counts: dict[str, int] = {}
    for r in records:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    flagged = {flag: sorted((r["name"] for r in primary if flag in r["quality"].get("flags", [])), key=photo_sort_key)
               for flag in ("blurry", "low_detail", "highlights_clipped", "shadows_crushed", "underexposed", "overexposed")}
    usable = [r["name"] for r in records if r["status"] == "ok" and "blurry" not in r["quality"].get("flags", [])]

    manifest = {
        "schema": ids.id_of("capture_session"),
        "version": 1,
        "room": room,
        "session_id": session_id,
        "pipeline": {"package": "roomscan", "ingest_version": INGEST_VERSION},
        "thresholds": thresholds.as_dict(),
        "summary": {
            "files": len(records),
            "total_bytes": sum(r["size_bytes"] for r in records),
            "status_counts": dict(sorted(counts.items())),
            "usable_for_poses": len(usable),
            "session_median_sharpness": session_median,
            "flag_counts": {k: len(v) for k, v in flagged.items()},
            "sources_with_location": sum(1 for r in records if r.get("source_had_location")),
            "lens_counts": dict(sorted(_count(r.get("lens_kind") for r in primary).items())),
        },
        "duplicates": {"exact": exact_report, "near": near_report},
        "flags": flagged,
        "photos": records,
    }
    session_dir = guard.mkdir("sessions", session_id)
    manifest_bytes = json_bytes(manifest)
    existing = guard.path("sessions", session_id, "manifest.json")
    if existing.is_file() and existing.read_bytes() != manifest_bytes:
        raise SessionConflictError(
            f"Session {session_id} already has a different manifest, and it was left as it is. The code or the "
            f"files it writes changed without INGEST_VERSION changing; raise INGEST_VERSION, or remove "
            f"{existing.parent} if the old session is no longer needed.")
    manifest_path = guard.write_bytes(Path("sessions") / session_id / "manifest.json", manifest_bytes)
    guard.write_bytes(Path("sessions") / session_id / "ingest-report.md", text_bytes(render_report(manifest)))

    total_s = time.perf_counter() - started
    run = {
        "session_id": session_id,
        "roomscan_version": __version__,
        "python": platform.python_version(),
        "source_root": str(source),
        "workers": worker_count,
        "converted": len(todo),
        "cached": len(first_by_sha) - len(todo),
        "timing_s": {"hash": round(hash_s, 2), "convert": round(convert_s, 2), "total": round(total_s, 2)},
        "finished_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    guard.write_bytes(Path("sessions") / session_id / "ingest-run.json", json_bytes(run))
    _update_index(guard, session_id, manifest)
    log(f"Session {session_id}: {counts} in {total_s:.1f}s -> {manifest_path}")
    return {"session_id": session_id, "session_dir": session_dir, "manifest": manifest, "run": run}


def _update_index(guard: OutputGuard, session_id: str, manifest: dict[str, Any]) -> None:
    import json

    index_path = guard.path("sessions", "index.json")
    index: dict[str, Any] = {"sessions": {}}
    if index_path.is_file():
        try:
            index = json.loads(index_path.read_bytes())
        except ValueError:
            index = {"sessions": {}}
    index.setdefault("sessions", {})[session_id] = {
        "files": manifest["summary"]["files"],
        "usable_for_poses": manifest["summary"]["usable_for_poses"],
    }
    index["latest"] = session_id
    guard.write_bytes(Path("sessions") / "index.json", json_bytes(index))
    guard.write_bytes("latest-session.txt", text_bytes(session_id))


def resolve_session(captures_root: Path, room: str, session: str | None) -> Path:
    room_dir = room_root(captures_root, room)
    if not session or session == "latest":
        pointer = room_dir / "latest-session.txt"
        if not pointer.is_file():
            raise FileNotFoundError(f"No ingested session for room {room!r}; run 'roomscan ingest' first.")
        session = pointer.read_text(encoding="utf-8").strip()
    path = room_dir / "sessions" / session
    if not (path / "manifest.json").is_file():
        raise FileNotFoundError(f"Session {session} has no manifest under {path}")
    return path


def render_report(m: dict[str, Any]) -> str:
    """The ingest report. File names and EXIF text are data, so they go through ``md_text``."""
    s = m["summary"]
    lines = [
        f"# Ingest report: {md_text(m['room'])} session {md_text(m['session_id'])}",
        "",
        f"- Files: {s['files']} ({s['total_bytes'] / 1e6:.1f} MB)",
        f"- Status: " + ", ".join(f"{md_text(k)} {v}" for k, v in s["status_counts"].items()),
        f"- Usable for poses (not a duplicate, not blurry): {s['usable_for_poses']}",
        f"- Session median sharpness: {s['session_median_sharpness']}",
        f"- Source files that carried GPS: {s['sources_with_location']} (stripped from every written file)",
        "",
        "## Exact duplicates (byte-identical)",
        "",
    ]
    lines += [f"- {md_text(', '.join(g))}" for g in m["duplicates"]["exact"]] or ["- none"]
    lines += ["", "## Near-duplicates (kept the sharpest of each group)", ""]
    lines += [f"- keep {md_text(g['keep'])}; skip {md_text(', '.join(g['others']))}"
              for g in m["duplicates"]["near"]] or ["- none"]
    lines += ["", "## Quality flags", "",
              "blurry: low detail and soft edges (left out of pose estimation). low_detail: a plain surface "
              "whose edges are crisp (kept). Clipping and exposure flags are reported only.", ""]
    for flag, names in m["flags"].items():
        lines.append(f"- {md_text(flag)}: {len(names)}" + (f" ({md_text(', '.join(names))})" if names else ""))
    lenses: dict[str, int] = {}
    for p in m["photos"]:
        if p["status"] == "ok" and p.get("exif"):
            key = f"{p['exif'].get('focal_length_35mm')} mm equiv ({p['exif'].get('lens_model')})"
            lenses[key] = lenses.get(key, 0) + 1
    lines += ["", "## Lenses used (unique photos)", ""]
    lines += [f"- {md_text(k)}: {v}" for k, v in sorted(lenses.items(), key=lambda kv: -kv[1])]
    return "\n".join(lines)


def _count(values) -> dict[str, int]:
    out: dict[str, int] = {}
    for v in values:
        key = v or "unknown"
        out[key] = out.get(key, 0) + 1
    return out
