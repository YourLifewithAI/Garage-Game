"""The team's journal for the mock host: the kernel host's journal writer, journal.note, journal.read and map.find
(game/scripts/native/Kernel/CommandHostJournal.cs; contracts/README.md "The journal and the map").

- Facts are the host's: tasks for fetch, follow, come and go_to (one entry per task, updated in place), a finished
  fetch kept in the history, and creations built, changed and removed. Each says who acted and at whose direction as
  far as the host can verify it. Lines come from templates and sanitised names, so a name can never write an entry.
- A task about something neither avatar has seen says "something", with no id and no pin, until the team sees it.
- journal.note is the sender's own words, marked untrusted, never a fact. journal.read and map.find answer only from
  the journal and the team's map, so nothing undiscovered can leak through them.

The mock keeps the journal in memory with the room (the kernel host saves it with the room; the mock saves nothing).
"""
from __future__ import annotations

import base64
import copy
import re
import secrets
from datetime import datetime, timezone

from . import perception

MAX_OPEN_TASKS = 32  # kernel: MaxOpenTasks
MAX_HISTORY = 500  # kernel: MaxHistory
MAX_NOTES = 100  # kernel: MaxNotes
# Goals that show as a task while they run, and the ones whose task stays in the history when it ends.
TASK_GOALS = frozenset({"fetch", "follow", "come", "go_to"})
RECORDED_GOALS = frozenset({"fetch"})
_KNOWN_ID = re.compile(r"(obj|creation):[A-Za-z0-9_-]{1,64}")


def new_entry_id() -> str:
    """'entry-' and 26 lowercase base32 characters (128 random bits): the contract's opaque form, never a counter."""
    return "entry-" + base64.b32encode(secrets.token_bytes(16)).decode("ascii").rstrip("=").lower()


def entry_time(ts: float) -> str:
    """A journal entry's real time, to the millisecond, as the kernel host writes it."""
    moment = datetime.fromtimestamp(ts, timezone.utc)
    return moment.strftime("%Y-%m-%dT%H:%M:%S.") + f"{moment.microsecond // 1000:03d}Z"


class JournalMixin:
    """Mixed into MockHost: the journal's state lives with the room and is reset with it."""

    def _reset_journal(self) -> None:
        self.open_tasks: list[dict] = []
        self.journal_history: list[dict] = []
        self.notes: list[dict] = []
        self._task_goals: dict[str, str] = {}  # entry id -> goal
        self._task_targets: dict[str, str] = {}  # entry id -> target id ("" for none)
        self._task_of_job: dict[str, str] = {}  # job id -> entry id

    # ------------------------------------------------------------------ text, as the kernel's templates write it

    def _plain(self, name, limit: int) -> str:
        text = name if isinstance(name, str) else ""
        text = re.sub(" {2,}", " ", self.text.clean(text, keep_newlines=False)).strip()[:limit]
        return re.sub(" {2,}", " ", self.text.clean(text, keep_newlines=False)).strip()

    def _quoted(self, name) -> str:
        clean = self._plain(name, 60).replace('"', "'").strip()
        return '"' + (clean or "something") + '"'

    def _subject_name(self, name) -> str:
        return self._plain(name, 80) or "something"

    @staticmethod
    def _direction(actor: str, directed_by: str) -> str:
        if actor.startswith("player:"):
            return ""
        return ", at the player's direction" if directed_by.startswith("player:") else ", on its own initiative"

    def _fact(self, kind: str, line: str, actor: str, directed_by: str, subject_id: str | None, subject_name,
              pin: list[float] | None, state: str | None = None, revision: int | None = None) -> dict:
        entry = {"entry_id": new_entry_id(), "kind": kind, "at_utc": entry_time(self.clock.now()),
                 "revision": self.revision if revision is None else revision, "line": self._plain(line, 200),
                 "actor": actor, "directed_by": directed_by}
        if subject_id is not None:
            entry["subject"] = {"entities": [subject_id], "name": self._subject_name(subject_name)}
        if pin is not None:
            entry["pin_m"] = [round(float(v), 4) for v in pin]
        if state is not None:
            entry["state"] = state
        return entry

    def _add_history(self, entry: dict) -> None:
        self.journal_history.append(entry)
        while len(self.journal_history) > MAX_HISTORY:
            kinds = [e["kind"] for e in self.journal_history]
            drop = next((i for i, k in enumerate(kinds) if k in ("found", "gathered")), None)
            if drop is None:
                drop = next((i for i, k in enumerate(kinds) if k not in ("built", "changed")), 0)
            del self.journal_history[drop]

    # ------------------------------------------------------------------ what the team knows

    def _known_to_team(self, entity_id: str) -> bool:
        """The avatars always; otherwise in sight of either avatar now or on the team's map."""
        if entity_id in (self.avatars.get(self._team_principal()), "avatar:player", "avatar:companion"):
            return True
        return self._team_view(entity_id) is not None

    def _team_sees(self, entity_id: str) -> bool:
        """Whether either avatar's eyes see a thing now (the team's sight, whatever shared_sight says for queries)."""
        return any(entity_id in self.perceived(p) for p in self._team_principals())

    def _team_sees_place(self, box: dict, standing: str) -> bool:
        """Whether either avatar's eyes reach a place now, for a thing about to stand there (`standing` is its id, which
        never hides its own place)."""
        occluders = [o for o in self._occluders() if o[0] != standing]
        for principal in self._team_principals():
            eye = perception.eye_point(self.entities[self.avatars[principal]].position,
                                       "player" if principal.startswith("player:") else "companion")
            if perception.visible(eye, standing, box["min_m"], box["max_m"], occluders):
                return True
        return False

    def _team_view(self, entity_id: str):
        """(name, pin) as the team knows it (the kernel's TeamView): in either avatar's sight now, as it is; else as last
        seen on the map; None when neither avatar has seen it."""
        entity = self.entities.get(entity_id)
        if entity is not None and not entity.removed and self._team_sees(entity_id):
            summary = entity.summary(self.text)
            return summary["display_name"], _centre(summary["bounds_m"])
        entry = self._memory(self._team_principal()).entries.get(entity_id)
        if entry is not None:
            summary = entry.view["summary"]
            return summary["display_name"], _centre(summary["bounds_m"])
        return None

    # ------------------------------------------------------------------ tasks

    @staticmethod
    def _task_line(goal: str, name, quoted, direction: str) -> str:
        if goal == "fetch":
            return "Fetching " + (quoted if name is not None else "something") + direction
        if goal == "follow":
            return "Following you"
        if goal == "come":
            return "Coming to you"
        return "Going to " + (quoted if name is not None else "somewhere")

    def _task_started(self, job_id: str, job: dict) -> None:
        if job["goal"] not in TASK_GOALS or not job.get("exposed", True):
            return
        actor = "player:local" if job["actor"] == "avatar:player" else (self._principal_of(job["actor"]) or job["principal"])
        target = job["target"]
        view = ("Player", None) if target == "avatar:player" else self._team_view(target)
        name = view[0] if view else None
        line = self._task_line(job["goal"], name, self._quoted(name), self._direction(actor, job["principal"]))
        pin = None if view is None or target == "avatar:player" else view[1]
        task = self._fact("task", line, actor, job["principal"], target if view else None, name, pin, "active")
        while len(self.open_tasks) >= MAX_OPEN_TASKS:
            self._close_task(self.open_tasks[0], "cancelled")
        self.open_tasks.append(task)
        self._task_goals[task["entry_id"]] = job["goal"]
        self._task_targets[task["entry_id"]] = target
        self._task_of_job[job_id] = task["entry_id"]

    def _name_seen_tasks(self) -> None:
        """Open tasks about something the team had not seen get their subject once it has (updated in place)."""
        for task in [t for t in self.open_tasks if "subject" not in t]:
            target = self._task_targets.get(task["entry_id"], "")
            view = self._team_view(target) if target else None
            if view is None:
                continue
            task["subject"] = {"entities": [target], "name": self._subject_name(view[0])}
            task["pin_m"] = [round(float(v), 4) for v in view[1]]
            goal = self._task_goals.get(task["entry_id"], "go_to")
            task["line"] = self._plain(self._task_line(goal, view[0], self._quoted(view[0]),
                                                       self._direction(task["actor"], task["directed_by"])), 200)

    def _task_ended(self, job_id: str, state: str) -> None:
        entry_id = self._task_of_job.pop(job_id, None)
        task = next((t for t in self.open_tasks if t["entry_id"] == entry_id), None)
        if task is None:
            return
        self._name_seen_tasks()
        self._close_task(task, {"succeeded": "done", "failed": "failed"}.get(state, "cancelled"))

    def _close_task(self, task: dict, state: str) -> None:
        entry_id = task["entry_id"]
        self.open_tasks.remove(task)
        goal = self._task_goals.pop(entry_id, None)
        self._task_targets.pop(entry_id, None)
        if goal not in RECORDED_GOALS:
            return
        name = self._quoted(task["subject"]["name"]) if "subject" in task else "something"
        verb = {"done": "Fetched ", "failed": "Could not fetch "}.get(state, "Stopped fetching ")
        closed = copy.deepcopy(task)
        closed["line"] = self._plain(verb + name + self._direction(task["actor"], task["directed_by"]), 200)
        closed["state"] = state
        closed["at_utc"] = entry_time(self.clock.now())
        closed["revision"] = self.revision
        self._add_history(closed)

    def _journal_task_targets(self) -> set[str]:
        return {t for t in self._task_targets.values() if t}

    # ------------------------------------------------------------------ creation facts

    def _creation_fact(self, op: str, principal: str, approved_by: str | None, entity_id: str, view,
                       revision: int) -> None:
        """A built, changed or removed fact. `view` is (name, pin) as the team knows the thing when the fact is
        written (CommandHost.CreationFact): None, for a thing neither avatar has seen, is "something" with no id and
        no pin."""
        directed_by = approved_by or principal
        kind, verb = {"creation.place": ("built", "built"), "creation.revise": ("changed", "changed")}.get(
            op, ("removed", "removed"))
        what = "something" if view is None else self._quoted(view[0])
        if principal.startswith("player:"):
            line = f"You {verb} {what}"
        else:
            line = f"{verb.capitalize()} {what}{self._direction(principal, directed_by)}"
        self._add_history(self._fact(kind, line, principal, directed_by, None if view is None else entity_id,
                                     None if view is None else view[0], None if view is None else view[1],
                                     revision=revision))

    # ------------------------------------------------------------------ journal.note, journal.read, map.find

    def _op_journal_note(self, principal, message, apply, new_revision):
        if not apply:
            return {"affected": []}
        entry = {"entry_id": new_entry_id(), "kind": "note", "at_utc": entry_time(self.clock.now()),
                 "revision": self.revision, "author": principal, "text": message["args"]["text"], "untrusted": True}
        self.notes.append(entry)
        del self.notes[:max(0, len(self.notes) - MAX_NOTES)]
        return {"affected": [], "data": {"entry_id": entry["entry_id"]}}

    def _journal_read(self, principal: str, args: dict) -> dict:
        kind, about, since = args.get("kind"), args.get("about"), args.get("since_utc")
        # An instant, however many fractional digits either side writes (CommandHost.ParseUtc).
        since_at = None if since is None else parse_utc(since)
        if since is not None and since_at is None:
            from .mock_host import HostError
            raise HostError("request_invalid", "since_utc is a UTC time.", field_path="$.args.since_utc")

        def match(entry: dict) -> bool:
            at = parse_utc(entry["at_utc"])
            return ((kind is None or entry["kind"] == kind)
                    and (about is None or about in entry.get("subject", {}).get("entities", []))
                    and (since_at is None or (at is not None and at >= since_at)))

        self._name_seen_tasks()
        job_of_task = {entry: job for job, entry in self._task_of_job.items()}
        open_tasks = []
        for task in reversed([t for t in self.open_tasks if match(t)]):
            item = copy.deepcopy(task)
            job_id = job_of_task.get(task["entry_id"])
            job = self.jobs.get(job_id) if job_id else None
            # A task's job id only for the principal whose job it is, while it runs.
            if job is not None and job["principal"] == principal and job["state"] == "running":
                item["job_id"] = job_id
            open_tasks.append(item)
        others = [(e, i) for i, e in enumerate(self.journal_history) if match(e)]
        others += [(e, i) for i, e in enumerate(self.notes) if match(e)]
        others.sort(key=lambda p: (p[0]["revision"], p[0]["at_utc"], p[1]), reverse=True)
        cursor = args.get("cursor")
        start = 0
        if cursor is not None:
            if not (cursor.isascii() and cursor.isdigit()) or int(cursor) > len(others):
                from .mock_host import HostError
                raise HostError("invalid_args", "That cursor is not from this journal.", field_path="$.args.cursor")
            start = int(cursor)
        limit = args.get("limit", 20)
        page = [copy.deepcopy(e) for e, _i in others[start:start + limit]]
        data = {"open_tasks": open_tasks, "entries": page}
        if start + len(page) < len(others):
            data["next_cursor"] = str(start + len(page))
        return data

    def _map_find(self, principal: str, args: dict) -> dict:
        """Only from the team's map, nearest first; an empty answer looks the same whether or not such a thing exists."""
        group = args.get("category_group")
        name = args.get("name", "").lower() if "name" in args else None
        own = self.entities[self.avatars[principal]].position
        origin = args.get("near_m", own)
        team = self._team_principal()
        memory = self._memory(team) if principal == team or principal.startswith("player:") else self._memory(principal)
        items = []
        for entity_id, entry in memory.entries.items():
            if not _KNOWN_ID.fullmatch(entity_id):
                continue
            live = self.entities.get(entity_id)
            if principal.startswith("player:"):
                summary = live.summary(self.text) if live is not None and not live.removed else self._remembered_summary(entry)
            elif self._view is not None and entity_id in self._view and live is not None and not live.removed:
                summary = self._summary(principal, live)
            else:
                summary = self._remembered_summary(entry)
            if group is not None and summary.get("category_group") != group:
                continue
            if name is not None and name not in summary["display_name"].lower() \
                    and name not in summary.get("category", "").lower():
                continue
            items.append((_distance(summary["bounds_m"], origin), entity_id, summary))
        items.sort(key=lambda item: (item[0], item[1]))
        limit = args.get("limit", 5)
        return {"items": [{"entity": s, "distance_m": min(3500.0, round(d, 3))} for d, _i, s in items[:limit]]}


_UTC = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,6}))?Z")


def parse_utc(text: str) -> datetime | None:
    """A UTC time in the contract's form as an instant (microseconds), or None when it is not a real time."""
    match = _UTC.fullmatch(text) if isinstance(text, str) else None
    if match is None:
        return None
    year, month, day, hour, minute, second, fraction = match.groups()
    try:
        return datetime(int(year), int(month), int(day), int(hour), int(minute), int(second),
                        int((fraction or "").ljust(6, "0")), tzinfo=timezone.utc)
    except ValueError:
        return None


def _centre(box: dict) -> list[float]:
    return [(box["min_m"][i] + box["max_m"][i]) / 2 for i in range(3)]


def _distance(box: dict, point) -> float:
    total = 0.0
    for i in range(3):
        nearest = min(max(point[i], box["min_m"][i]), box["max_m"][i])
        total += (point[i] - nearest) ** 2
    return total ** 0.5
