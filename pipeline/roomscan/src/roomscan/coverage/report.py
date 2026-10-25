"""The coverage report for the founder (HTML and Markdown) and the full record (coverage.json).

Plain language first: what to photograph next, then the map, then what went wrong with which
photos, then how sure the numbers are. Technical details sit at the end. The HTML embeds its
images so the one file can be opened anywhere on this machine; it stays under ``captures/``,
which Git ignores, because it shows the real room.
"""

from __future__ import annotations

import base64
import html
import time
from pathlib import Path
from typing import Any

import numpy as np

from .. import ids
from ..jsonio import text_bytes
from ..paths import OutputGuard
from ..textutil import md_text
from .grid import GOOD_VIEWS
from .objects import sector_of
from .visibility import LOW_CAMERA_M

MAP_LEGEND = ("Blue: in three or more photos taken from different spots. Amber: in one or two, or only from "
              "one spot. Red: in none. Grey: furniture stands there (floor under it, wall behind it), so I do "
              "not ask for it. Lilac: photos see past the wall there (a doorway, a window, the garage door). "
              "The three lanes around the edge are the walls: the inner lane is the bottom of the wall, the "
              "outer lane the top. Small dots are where you stood (green dots are low shots), with a tick for "
              "the way the phone faced.")
KIND_TITLES = {"wall": "Wall", "low_wall": "Wall, from low down", "floor": "Floor", "under": "Under furniture",
               "ceiling": "Ceiling", "object": "Furniture", "object_low": "Furniture, from low down",
               "low_lap": "Low lap", "bridge": "Reconnect"}


def _r(v, nd=3):
    return None if v is None else round(float(v), nd)


def coverage_record(context: dict[str, Any]) -> dict[str, Any]:
    """Every number behind the report, as plain JSON."""
    views, cams, looks = context["views"], context["cams"], context["looks"]
    graph = context["graph"]
    registered = set(context["registered"])
    photos = []
    for i, p in enumerate(views):
        rec = {"index": i, "name": p["name"], "placed": i in cams, "fitted": i in registered,
               "links": int(graph["degree"][i])}
        if i in cams:
            rec.update({"position_m": [_r(x) for x in cams[i]], "height_m": _r(cams[i][1]),
                        "looking": [_r(x) for x in looks[i]]})
        photos.append(rec)
    objects = []
    for o in context.get("objects") or []:
        objects.append({"id": o.id, "label": o.label, "min_m": [_r(x) for x in o.lo], "max_m": [_r(x) for x in o.hi],
                        "wall": o.wall, "photos": len(o.views), "detections": len(o.detections),
                        "sides_seen": sorted({sector_of(a) for a in o.view_angles_deg}),
                        "view_angles_deg": [_r(a, 1) for a in o.view_angles_deg],
                        "view_heights_m": [_r(h, 2) for h in o.view_heights_m]})
    surfaces = {"floor": context["floor"].stats()}
    surfaces.update({f"wall {k}": g.stats() for k, g in context["walls"].items()})
    if context.get("ceiling") is not None:
        surfaces["ceiling"] = context["ceiling"].stats()
    return {
        "schema": ids.id_of("coverage_report"),
        "version": 1,
        "room": context["manifest"]["room"],
        "session_id": context["manifest"]["session_id"],
        "room_frame": context["frame"].as_dict(),
        "view_graph": {k: graph[k] for k in ("min_overlap", "not_fitted", "unplaced", "weakly_linked")}
        | {"links": len(graph["edges"]), "groups": [len(c) for c in graph["components"]]},
        "scale": context["scale"],
        "surfaces": surfaces,
        "objects": objects,
        "guidance": context.get("guidance"),
        "photos": photos,
    }


# --- helpers -----------------------------------------------------------------------------------

def _thumb_uri(room_dir: Path, manifest: dict[str, Any], name: str) -> str | None:
    rec = next((p for p in manifest["photos"] if p["name"] == name), None)
    if not rec or not rec.get("derived"):
        return None
    path = Path(room_dir) / rec["derived"]["thumb"]
    if not path.is_file():
        return None
    return "data:image/jpeg;base64," + base64.b64encode(path.read_bytes()).decode("ascii")


def _png_uri(path: Path) -> str:
    return "data:image/png;base64," + base64.b64encode(Path(path).read_bytes()).decode("ascii")


def _pct(v) -> str:
    return "n/a" if v is None else f"{v:.0f}%"


def summary_numbers(context: dict[str, Any]) -> dict[str, Any]:
    m = context["manifest"]
    s = m["summary"]
    frame = context["frame"]
    walls = {k: g.stats() for k, g in context["walls"].items()}
    floor = context["floor"].stats()
    ceiling = context["ceiling"].stats() if context.get("ceiling") is not None else None
    wall_good = sum(st["good"] for st in walls.values())
    wall_seeable = sum(st["cells"] - st["blocked"] for st in walls.values())
    heights = np.array([context["cams"][v][1] for v in context["registered"]])
    pose = context.get("pose_info") or {}
    fits = [f for f in pose.get("fits", []) if f.get("batch", 0) > 0 and f.get("merged")]
    return {
        "files": s["files"],
        "unique": s["status_counts"].get("ok", 0) + s["status_counts"].get("near_duplicate", 0)
        + s["status_counts"].get("unreadable", 0),
        "exact_copies": s["status_counts"].get("duplicate", 0),
        "near_copies": s["status_counts"].get("near_duplicate", 0),
        "unreadable": s["status_counts"].get("unreadable", 0),
        "blurry": len(m["flags"].get("blurry", [])),
        "used": len(context["views"]),
        "fitted": len(context["registered"]),
        "width": frame.x_max - frame.x_min,
        "depth": frame.z_max - frame.z_min,
        "height": frame.ceiling_y,
        "walls_good_pct": 100 * wall_good / max(1, wall_seeable),
        "walls": walls,
        "floor": floor,
        "ceiling": ceiling,
        "low_share": float((heights < LOW_CAMERA_M).mean()) if len(heights) else 0.0,
        "median_height": float(np.median(heights)) if len(heights) else None,
        "join_centre_cm": 100 * float(np.median([f["median_center_error_m"] for f in fits])) if fits else None,
        "join_rot_deg": float(np.median([f["median_rotation_error_deg"] for f in fits])) if fits else None,
        "scale": context["scale"],
    }


# --- writers -----------------------------------------------------------------------------------

def write_reports(guard: OutputGuard, cov_rel: Path, context: dict[str, Any]) -> dict[str, Path]:
    md = render_markdown(context)
    page = render_html(context)
    md_path = guard.write_bytes(cov_rel / "coverage-report.md", text_bytes(md))
    html_path = guard.write_bytes(cov_rel / "coverage-report.html", text_bytes(page))
    return {"md": md_path, "html": html_path}


def _percent_words(low: float, high: float) -> str:
    """'from about 2% smaller to about 26% larger', from the signed percentages of a range."""
    smaller = f"about {abs(round(low))}% smaller" if round(low) < 0 else "no smaller"
    larger = f"about {round(high)}% larger" if round(high) > 0 else "no larger"
    return f"from {smaller} to {larger}"


def size_sentence(n: dict[str, Any]) -> str:
    """How big the room is, and how far to trust that, in one sentence for the top of the report."""
    scale = n["scale"]
    text = f"The room comes out about {n['width']:.1f} m by {n['depth']:.1f} m"
    text += f", with the ceiling about {n['height']:.1f} m up." if n["height"] else ". I could not find the ceiling."
    if scale["scale_source"] == "tape":
        checks = scale["tape"]["checks"]
        worst = max((abs(r["residual_m"]) for r in checks["rows"] if r["residual_m"] is not None and not r["check_only"]),
                    default=0.0)
        return (text + f" These sizes are scaled to your tape measurements (the model's own sizes were multiplied by "
                       f"{scale['applied_factor']:.2f}); the tape and the model still differ by up to {100 * worst:.0f} cm.")
    batch = scale["batch"]
    if batch["batches"] < 2:
        return text + " Treat these sizes as rough: nothing here checks the model's sense of scale. Measure a wall with a tape."
    return (text + f" Treat these sizes as rough: they come from the model's own sense of scale, and its other photo "
                   f"groups put the true size {_percent_words(batch['vs_used_pct']['low'], batch['vs_used_pct']['high'])} "
                   f"than drawn. A tape measurement fixes that (see How sure is this).")


def _intro_lines(n: dict[str, Any], context: dict[str, Any]) -> list[str]:
    g = context["guidance"]
    verdict = g["verdict"]
    lines = [
        f"You gave me {n['files']} files. {n['unique']} are different photos; the other {n['exact_copies']} are exact "
        f"copies of one of them.",
        f"{n['used']} photos were sharp enough to use ({n['blurry']} were blurry), and {n['fitted']} of those fitted "
        f"together into one model of the room.",
        size_sentence(n),
        (f"Coverage: {n['walls_good_pct']:.0f}% of the walls, {n['floor']['good_pct']:.0f}% of the open floor"
         + (f" and {n['ceiling']['good_pct']:.0f}% of the ceiling" if n["ceiling"] else "")
         + f" are in {GOOD_VIEWS} or more photos taken from different spots, which is what a 3D rebuild "
         f"needs."),
    ]
    if verdict["passed"]:
        lines.append("That passes the bar I use (70% of every wall and the floor, 40% of the ceiling, every piece "
                     "of furniture seen from three sides). More photos would still help the spots below.")
    else:
        lines.append(f"That is not enough yet. The {len(g['items'])} steps below ask for about "
                     f"{g['photos_suggested']} more photos and close the biggest gaps first.")
    return lines


def render_markdown(context: dict[str, Any]) -> str:
    n = summary_numbers(context)
    g = context["guidance"]
    room = context["manifest"]["room"]
    out = [f"# {md_text(room.capitalize())} photo coverage: what to photograph next", "",
           f"Session {md_text(context['manifest']['session_id'])}, {time.strftime('%d %B %Y')}.", "", "## In short", ""]
    out += [f"- {md_text(line)}" for line in _intro_lines(n, context)]
    out += ["", "## What to photograph next", "",
            "In this order. The numbers match the dark circles on the map. Wall A is at the top of the map: it is "
            "the wall you faced in your first photo. \"Left\" and \"right\" are as you stand facing the wall or "
            "the furniture.", ""]
    for it in g["items"]:
        near = f" You took {md_text(it['near_photo'])} from about there." if it.get("near_photo") else ""
        out.append(f"{it['number']}. **{md_text(KIND_TITLES.get(it['kind'], it['kind']))}.** {md_text(it['text'])} "
                   f"*Why:* {md_text(it['why'])}{near}")
    if g["more"]:
        out += ["", f"Smaller gaps, if you have time ({len(g['more'])}):", ""]
        out += [f"- {md_text(it['text'])}" for it in g["more"][:15]]
    out += ["", "## The map", "", "![Coverage map, seen from above](coverage-map.png)", "",
            MAP_LEGEND, "",
            "![The walls unfolded](walls.png)", ""]
    out += ["## Photos with problems", ""]
    for note in g["notes"]:
        names = note.get("photos_named")
        out.append(f"- {md_text(note['text'])}" + (f" ({md_text(', '.join(names))})" if names else ""))
    lost = context["graph"].get("not_fitted", []) + context["graph"].get("unplaced", [])
    if lost:
        out.append(f"- Did not fit with the others: {md_text(', '.join(context['views'][i]['name'] for i in lost))}.")
    out += ["", "## Furniture I found", ""]
    objects = context.get("objects") or []
    if objects:
        out += ["Found automatically, so expect some misses and odd names. It is a starting point for the "
                "object list, not the list itself.", "", "| Object | Photos | Sides seen | Wall |", "|---|---|---|---|"]
        for o in objects:
            sides = ", ".join(sorted({sector_of(a) for a in o.view_angles_deg})) or "none"
            out.append(f"| {md_text(o.label)} ({md_text(o.id)}) | {len(o.views)} | {md_text(sides)} | "
                       f"{md_text(o.wall or 'free-standing')} |")
    else:
        det = context.get("detection") or {}
        out.append("Furniture detection " + ("failed: " + md_text(det["error"]) if det.get("error") else "was skipped") + ".")
    out += ["", "## How sure is this?", ""]
    out += [f"- {md_text(line)}" for line in confidence_lines(n, context)]
    out += ["", "## Technical details", ""]
    out += [f"- {md_text(line)}" for line in technical_lines(context)]
    return "\n".join(out)


def _measurement_name(row: dict[str, Any]) -> str:
    between = row["between"]
    return {"wall_to_wall": f"between walls {' and '.join(between)}", "floor_to_ceiling": "floor to ceiling",
            "diagonal": "corner to corner", "opening": f"the opening on wall {''.join(between)}"}[row["kind"]]


def _measurement_label(row: dict[str, Any]) -> str:
    name = _measurement_name(row)
    place = (row.get("where") or "").strip()
    return name[0].upper() + name[1:] + (f" ({place})" if place and place.lower() != name.lower() else "")


def scale_lines(n: dict[str, Any]) -> list[str]:
    """What the room's scale rests on, in plain words, with every tape measurement against the model."""
    scale = n["scale"]
    tape = scale["tape"]
    lines: list[str] = []
    if scale["scale_source"] == "tape":
        checks = tape["checks"]
        lines.append(
            f"Scale: your tape measurements set it. Fitting one scale to them says to multiply the model's own sizes by "
            f"{scale['applied_factor']:.3f}. That was done before the room was measured, so the room sizes, the 25 cm "
            f"cells and the distances in the steps are in real metres. The fit itself is good to about "
            f"{tape['fit_sigma_pct']:.1f}%.")
        for row in checks["rows"]:
            if row["check_only"] or row["residual_m"] is None:
                continue
            cm = 100 * row["residual_m"]
            lines.append(f"{_measurement_label(row)}: tape {row['tape_m']:.3f} m, model {row['model_m']:.3f} m: "
                         f"{abs(cm):.1f} cm {'too long' if cm > 0 else 'too short'} ({row['residual_pct']:+.1f}%).")
        if checks["rms_cm"] is not None:
            lines.append(f"The differences from the tape average {checks['rms_cm']:.0f} cm (root mean square).")
        implied = [r for r in checks["rows"] if "implied_scale" in r]
        if len(implied) > 1:
            lines.append("Taken one at a time, the measurements would each ask for a different scale ("
                         + "; ".join(f"{_measurement_name(r)} x{r['implied_scale']:.3f}"
                                     for r in implied)
                         + "). That is for information only: one scale was applied to every direction, because "
                           "the model's mistake is a single overall scale, not a stretched room.")
        spread = checks["wall_copy_spread_cm"]
        for row in checks["rows"]:
            if row["flagged"] and not row["check_only"]:
                lines.append(
                    f"Check this one: {_measurement_name(row)} is {abs(100 * row['residual_m']):.0f} cm off, more "
                    f"than the {checks['flag_limit_cm']:.0f} cm limit (twice the "
                    f"{'%.0f cm' % spread if spread is not None else 'usual'} by which the groups of photos disagree "
                    f"about where a wall is). Either the tape was taken where the room is not typical, or the model "
                    f"has this wall in the wrong place.")
        for row in checks["rows"]:
            if not row["check_only"]:
                continue
            label = _measurement_label(row)
            if row["model_m"] is None:
                lines.append(f"{label} was not used for the scale (an opening is only a check), and no gap was "
                             f"found there to check it against.")
            else:
                verdict = "which does not match" if row["flagged"] else "which matches"
                lines.append(f"{label} was not used for the scale (an opening is only a check): tape "
                             f"{row['tape_m']:.2f} m, the photos see a gap about {row['model_m']:.2f} m wide, "
                             f"{verdict} to within one 25 cm cell.")
    else:
        batch = scale["batch"]
        lines.append(
            "Scale: no tape measurements were used, so the room is in the model's own scale, the one of its first group "
            "of photos. "
            + ("" if batch["batches"] < 2 else
               f"The other groups of photos put the true size {_percent_words(batch['vs_used_pct']['low'], batch['vs_used_pct']['high'])} "
               f"than drawn, so every distance in this report is only rough. ")
            + "To fix it, tape two or three lengths of the room and list them in the room's measurements.json "
              "(the roomscan README shows how).")
    if tape:
        lines += list(tape["notes"])
    return lines


def confidence_lines(n: dict[str, Any], context: dict[str, Any]) -> list[str]:
    lines = scale_lines(n)
    if n["join_centre_cm"] is not None:
        lines.append(f"The photos are placed in groups of up to 32 (what fits on the graphics card) and the groups "
                     f"are then joined. Where groups meet they agree to about {n['join_centre_cm']:.0f} cm and "
                     f"{n['join_rot_deg']:.0f} degrees, so read the map to about a cell (25 cm), not finer.")
    lines.append("A patch counts as covered when at least three photos taken from different spots show it. Floor "
                 "under furniture and wall behind it are grey and not counted against you; floor that clutter only "
                 "hides from where you stood still counts as a gap, because a photo from closer would show it.")
    lines.append("Furniture names come from an automatic detector with a fixed word list, never from text in "
                 "the photos.")
    return lines


def technical_lines(context: dict[str, Any]) -> list[str]:
    run = context.get("run") or {}
    pose = context.get("pose_info") or {}
    gpu = pose.get("gpu", {})
    det = context.get("detection") or {}
    times = ", ".join(f"{k.replace('_', ' ')} {v:.0f} s" for k, v in (run.get("timing_s") or {}).items())
    lines = [f"Stage times: {times}."]
    if gpu:
        lines.append(f"Poses: {pose.get('model')} in {len(pose.get('batches', []))} batches of up to "
                     f"{pose.get('batch_size')} photos; GPU inference {gpu.get('inference_seconds')} s, model load "
                     f"{gpu.get('load_seconds')} s, peak VRAM {gpu.get('peak_allocated_mib')} MiB allocated / "
                     f"{gpu.get('peak_reserved_mib')} MiB reserved; CPU: preparing photos "
                     f"{gpu.get('photo_prepare_seconds_cpu')} s, joining batches {gpu.get('merge_seconds_cpu')} s, "
                     f"refining {(pose.get('refinement') or {}).get('seconds')} s"
                     + (" (reused from the cache this run)" if pose.get("reused_from_cache") else "") + ".")
    if det and not det.get("skipped"):
        lines.append(f"Furniture: {det.get('detector')}; {det.get('photos')} photos, GPU {det.get('gpu_seconds')} s, "
                     f"peak {det.get('peak_allocated_mib')} MiB"
                     + (" (reused from the cache)" if det.get("reused_from_cache") else "") + ".")
    scale = context.get("scale") or {}
    if scale:
        lines.append(f"Scale source: {scale['scale_source']}; applied factor {scale['applied_factor']}; batch scales "
                     + ", ".join(f"{k}: {v}" for k, v in scale["batch"]["batch_scales"].items()) + ".")
    for model, info in (run.get("models") or {}).items():
        lines.append(f"Model {model}: licence {info.get('licence')}." + (f" Note: {info['note']}." if info.get("note") else ""))
    lines.append(f"Money spent: ${run.get('money_spent_usd', 0)}. Everything ran on this PC.")
    return lines


# --- HTML --------------------------------------------------------------------------------------

CSS = """
:root { --ink:#1f2a36; --muted:#5d6b78; --line:#d9dee3; --bg:#fbfaf7; --card:#ffffff; --accent:#3f7fbf;
        --warn:#d6544c; --amber:#f0b545; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--ink); font:16px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif; }
main { max-width: 980px; margin: 0 auto; padding: 24px 16px 64px; }
h1 { font-size: 1.7rem; margin: 0 0 4px; } h2 { font-size: 1.25rem; margin: 36px 0 12px; }
.sub { color: var(--muted); margin: 0 0 20px; }
.card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 16px 18px; }
.short li { margin: 4px 0; }
.verdict { display:inline-block; padding: 2px 10px; border-radius: 99px; font-weight:600; font-size:.9rem; }
.verdict.no { background:#fbe3e1; color:#8f2b25; } .verdict.yes { background:#e1eef9; color:#1d4f80; }
ol.steps { list-style: none; padding: 0; margin: 0; counter-reset: none; }
ol.steps li { display: grid; grid-template-columns: 44px 1fr 140px; gap: 14px; align-items: start;
              background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 14px; margin: 10px 0; }
.num { width: 34px; height: 34px; border-radius: 50%; background: var(--ink); color: #fff; display:flex;
       align-items:center; justify-content:center; font-weight:700; }
.kind { font-size: .78rem; text-transform: uppercase; letter-spacing: .06em; color: var(--muted); }
.why { color: var(--muted); font-size: .92rem; margin-top: 4px; }
.near { font-size: .78rem; color: var(--muted); text-align:center; }
.near img { width: 140px; height: 105px; object-fit: cover; border-radius: 6px; display:block; margin-bottom: 4px; }
img.map { width: 100%; height: auto; border: 1px solid var(--line); border-radius: 10px; background: #fff; }
.thumbs { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0 4px; }
.thumbs figure { margin: 0; width: 112px; font-size: .72rem; color: var(--muted); word-break: break-all; }
.thumbs img { width: 112px; height: 84px; object-fit: cover; border-radius: 6px; display:block; }
table { border-collapse: collapse; width: 100%; font-size: .92rem; }
th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
details { margin-top: 10px; } summary { cursor: pointer; color: var(--muted); }
.legend { color: var(--muted); font-size: .92rem; }
@media (max-width: 640px) { ol.steps li { grid-template-columns: 40px 1fr; } .near { grid-column: 2; text-align:left; } }
"""


def _esc(s: Any) -> str:
    return html.escape(str(s), quote=True)


def _thumbs(context: dict[str, Any], names: list[str], limit: int = 40) -> str:
    room_dir, m = context["room_dir"], context["manifest"]
    figs = []
    for name in names[:limit]:
        uri = _thumb_uri(room_dir, m, name)
        if uri:
            figs.append(f'<figure><img src="{uri}" alt="{_esc(name)}"><figcaption>{_esc(name)}</figcaption></figure>')
        else:
            figs.append(f"<figure><figcaption>{_esc(name)}</figcaption></figure>")
    more = f"<p class='legend'>... and {len(names) - limit} more.</p>" if len(names) > limit else ""
    return f"<div class='thumbs'>{''.join(figs)}</div>{more}"


def render_html(context: dict[str, Any]) -> str:
    n = summary_numbers(context)
    g = context["guidance"]
    m = context["manifest"]
    room = m["room"]
    parts = [f"<!doctype html><html lang='en'><head><meta charset='utf-8'>",
             "<meta name='viewport' content='width=device-width, initial-scale=1'>",
             f"<title>{_esc(room.capitalize())} coverage report</title><style>{CSS}</style></head><body><main>",
             f"<h1>{_esc(room.capitalize())} photos: what to photograph next</h1>",
             f"<p class='sub'>Session {_esc(m['session_id'])}, {time.strftime('%d %B %Y')}. "
             f"<span class='verdict {'yes' if g['verdict']['passed'] else 'no'}'>"
             f"{'Coverage passes' if g['verdict']['passed'] else 'More photos needed'}</span></p>",
             "<h2>In short</h2><div class='card'><ul class='short'>"]
    parts += [f"<li>{_esc(line)}</li>" for line in _intro_lines(n, context)]
    parts += ["</ul></div>", "<h2>What to photograph next</h2>",
              "<p class='legend'>In this order. The numbers match the dark circles on the map below. Wall A is at the "
              "top of the map: it is the wall you faced in your first photo. \"Left\" and \"right\" are as you stand "
              "facing the wall or the piece of furniture. The small picture is a photo you already took from about "
              "the right spot.</p><ol class='steps'>"]
    for it in g["items"]:
        near = ""
        if it.get("near_photo"):
            uri = _thumb_uri(context["room_dir"], m, it["near_photo"])
            img = f"<img src='{uri}' alt=''>" if uri else ""
            near = f"<div class='near'>{img}near {_esc(it['near_photo'])}</div>"
        parts.append(f"<li><div class='num'>{it['number']}</div><div><div class='kind'>"
                     f"{_esc(KIND_TITLES.get(it['kind'], it['kind']))} &middot; {it['photos']} "
                     f"photo{'s' if it['photos'] != 1 else ''}</div><div>{_esc(it['text'])}</div>"
                     f"<div class='why'>{_esc(it['why'])}</div></div>{near or '<div></div>'}</li>")
    parts.append("</ol>")
    if g["more"]:
        parts.append(f"<details><summary>Smaller gaps, if you have time ({len(g['more'])})</summary><ul>")
        parts += [f"<li>{_esc(it['text'])}</li>" for it in g["more"]]
        parts.append("</ul></details>")
    parts += ["<h2>The map</h2>", f"<img class='map' src='{_png_uri(context['map_path'])}' alt='Coverage map seen from above'>",
              f"<p class='legend'>{_esc(MAP_LEGEND)}</p>",
              f"<details open><summary>The walls unfolded, as you face each one</summary>"
              f"<img class='map' src='{_png_uri(context['walls_path'])}' alt='The four walls unfolded'></details>",
              "<h2>Photos with problems</h2>"]
    for note in g["notes"]:
        parts.append(f"<p>{_esc(note['text'])}</p>")
        if note.get("photos_named"):
            parts.append(_thumbs(context, note["photos_named"]))
    lost = context["graph"].get("not_fitted", []) + context["graph"].get("unplaced", [])
    if lost:
        names = [context["views"][i]["name"] for i in lost]
        parts.append(f"<p>{len(names)} usable photo{'s' if len(names) != 1 else ''} did not fit with the others "
                     f"(step {next((it['number'] for it in g['items'] if it['kind'] == 'bridge'), '-')} above):</p>")
        parts.append(_thumbs(context, names))
    if m["duplicates"]["exact"]:
        rows = "".join(f"<tr><td>{_esc(grp[0])}</td><td>{_esc(', '.join(grp[1:]))}</td></tr>" for grp in m["duplicates"]["exact"])
        parts.append(f"<details><summary>All {sum(len(x) - 1 for x in m['duplicates']['exact'])} exact copies</summary>"
                     f"<table><tr><th>Kept</th><th>Copy</th></tr>{rows}</table></details>")
    parts.append("<h2>Furniture I found</h2>")
    objects = context.get("objects") or []
    if objects:
        parts.append("<p class='legend'>Found automatically, so expect some misses and odd names. It is a starting "
                     "point for the object list, not the list itself.</p><table><tr><th>Object</th><th>Photos</th>"
                     "<th>Sides seen</th><th>Wall</th></tr>")
        for o in objects:
            sides = ", ".join(sorted({sector_of(a) for a in o.view_angles_deg})) or "none"
            parts.append(f"<tr><td>{_esc(o.label)} <span class='legend'>({_esc(o.id)})</span></td><td>{len(o.views)}</td>"
                         f"<td>{_esc(sides)}</td><td>{_esc(o.wall or 'free-standing')}</td></tr>")
        parts.append("</table>")
    else:
        det = context.get("detection") or {}
        parts.append("<p>Furniture detection " + _esc("failed: " + det["error"] if det.get("error") else "was skipped") + ".</p>")
    parts.append("<h2>How sure is this?</h2><ul>")
    parts += [f"<li>{_esc(line)}</li>" for line in confidence_lines(n, context)]
    parts.append("</ul><details><summary>Technical details</summary><ul>")
    parts += [f"<li>{_esc(line)}</li>" for line in technical_lines(context)]
    walls_rows = "".join(f"<tr><td>Wall {k}</td><td>{_pct(st['good_pct'])}</td><td>{_pct(st['thin_pct'])}</td>"
                         f"<td>{_pct(st['unseen_pct'])}</td><td>{st['blocked']}</td></tr>" for k, st in n["walls"].items())
    floor = n["floor"]
    ceil = n["ceiling"]
    extra = (f"<tr><td>Floor</td><td>{_pct(floor['good_pct'])}</td><td>{_pct(floor['thin_pct'])}</td>"
             f"<td>{_pct(floor['unseen_pct'])}</td><td>{floor['blocked']}</td></tr>")
    if ceil:
        extra += (f"<tr><td>Ceiling</td><td>{_pct(ceil['good_pct'])}</td><td>{_pct(ceil['thin_pct'])}</td>"
                  f"<td>{_pct(ceil['unseen_pct'])}</td><td>{ceil['blocked']}</td></tr>")
    parts.append(f"</ul><table><tr><th>Surface</th><th>3+ photos</th><th>1-2</th><th>none</th><th>hidden cells</th></tr>"
                 f"{walls_rows}{extra}</table></details>")
    parts.append("</main></body></html>")
    return "\n".join(parts)
