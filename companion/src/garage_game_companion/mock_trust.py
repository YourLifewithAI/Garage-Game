"""Trust for the mock host: the kernel's Kernel/Trust.cs and Kernel/CommandHostTrust.cs (M8; docs/game/MECHANICS.md section 5).

- The count is the team's (the kernel keeps it in team.json; the mock keeps it with the host, so a room reload never erases it),
  computed only from what happened, and counted in Challenge and Live only.
- Clean initiative, +1, for paid actions only (decision 69, the founder, 16 October: "Only paid actions and consequences directly
  related to those actions earn trust"): a committed command of the Gubble's that the guard would charge pips for (paid: the
  price above zero where the kernel's ChargeEnergy charges, read from the pack's prices, never a list of ops; the mock keeps no
  pool, so it notes the price it would charge) opens a 60 s window; a window that closes with no consequence fact in it gains, at
  most once in two minutes. A goal's window opens only when its job succeeds, and a lead is paid only once its Lift is charged
  (the rise, which on the mock is its arrival); a lead that is no lift, a fetch, or one that fails gains nothing. Free commands
  (grabs, pushes, releases, free builds, goals, stops, notes, checkpoints) never gain. The mock simulates no nature, so it writes
  no consequence facts of its own; the -3, and its trace to a paid action, are the kernel's.
- receipt.mark and trust.reset are the player's alone: the companion is refused before any handler runs (player-only ops). A
  mark names a receipt of the Gubble's from this room's session, each once: +2 for yes, -2 for not that.
- Readable, never writable: the journal keeps the marks, a tier's change and a reset as lessons (kind consequence), in the
  kernel's words, word for word. No request field moves Trust.

test_kernel_alignment.py holds these numbers and words equal to the kernel's.
"""
from __future__ import annotations

import re
from collections import OrderedDict
from dataclasses import dataclass

from .refusals import HostError

CLEAN_INITIATIVE = 1  # kernel: TrustLaws.CleanInitiative
CLEAN_WINDOW_S = 60.0  # kernel: TrustLaws.CleanWindowS
GAIN_SPACING_S = 120.0  # kernel: TrustLaws.GainSpacingS
PER_CONSEQUENCE = -3  # kernel: TrustLaws.PerConsequence
MARK_YES = 2  # kernel: TrustLaws.MarkYes
MARK_NOT_THAT = -2  # kernel: TrustLaws.MarkNotThat
BAND_AT = (5, 15, 30)  # kernel: TrustLaws.BandAt
OWN_IDEA_SHARE = (1.0, 0.75, 0.5, 0.25)  # kernel: TrustLaws.OwnIdeaShare
MAX_REMEMBERED = 1024  # kernel: TrustLaws.MaxRemembered
TRUST_BOUND = 1_000_000  # kernel: Trust.Bound
MARK_NOT_FOUND = "There is no receipt of the Gubble's from this session with that action id."  # kernel: CommandHost.MarkNotFound
MARKED_ONCE = "That receipt is already marked: each receipt is marked once."  # kernel: CommandHost.MarkedOnce
CONSEQUENCE_PLACE_M = 1.5  # kernel: CommandHost.ConsequencePlaceM
_NOT_ACTIONS = frozenset({"goal.stop", "effect.stop", "wish.stop", "journal.note", "room.checkpoint"})
_KNOWN_ID = re.compile(r"(obj|creation):[A-Za-z0-9_-]{1,64}")


def paid(charged_pips: int) -> bool:
    """The kernel's Trust.Paid (decision 69): a command is a paid action when the host charged it pips, and only then."""
    return charged_pips > 0


def band(count: int) -> int:
    return sum(1 for at in BAND_AT if count >= at)


def tier(count: int) -> int:
    return -1 if count < 0 else band(count)


def phrase(level: int) -> str:
    return {-1: "the Gubble has your trust to win back", 0: "the Gubble is earning your trust", 1: "you trust the Gubble a little",
            2: "you trust the Gubble"}.get(level, "you trust the Gubble deeply")


def change_line(before: int, after: int) -> str:
    return ("Trust grew: " if after > before else "Trust fell: ") + phrase(after) + "."


def own_idea_price(ordered: int, own_idea_factor: float, level: int) -> int:
    """The kernel's Trust.OwnIdeaPrice: the pack's own-idea price times the band's share, whole pips (half away from zero), at
    least one for a priced ability."""
    share = OWN_IDEA_SHARE[max(0, min(level, len(OWN_IDEA_SHARE) - 1))]
    value = ordered * own_idea_factor * share
    rounded = int(value + 0.5) if value >= 0 else -int(-value + 0.5)
    return max(1, rounded) if ordered > 0 else 0


@dataclass
class TrustEntry:
    action_id: str
    op: str
    what: str
    place: list[float] | None
    at: float
    counted: bool
    paid: bool = False  # the guard charged it pips (the kernel's Entry.Paid): only a paid window can gain; any receipt can be marked
    spoiled: bool = False
    settled: bool = False
    mark: str | None = None
    job_id: str | None = None  # a goal's job: its window waits for it (the kernel's Entry.JobId)
    pending: bool = False  # its job still runs: the window has not opened
    job_failed: bool = False  # its job ended without succeeding: it gains nothing
    opened_at: float | None = None  # when its window opened: its commit, or its job's success

    @property
    def closes(self) -> float:
        return (self.at if self.opened_at is None else self.opened_at) + CLEAN_WINDOW_S


def _what(op: str, capability: str | None, rules) -> str:
    """The kernel's TrustWhat: what a mark's line calls a command."""
    if op in ("nature.cast", "effect.start"):
        named = {"fire": "fire", "grow": "Grow", "bubble_carry": "bubble carry", "bubble_boat": "bubble boat", "bubble_lift": "bubble lift"}
        if capability in named:
            return named[capability]
        ability = rules.ability(capability) if rules is not None and capability else None
        if ability is not None:
            # The kernel names it by the pack's display name; the shipped packs' names are their capabilities.
            return "bubble" if ability.primitive == "particles.float" else ability.capability.replace("_", " ")
        return "magic"
    if op == "goal.set":
        return {"lead": "bubble lead", "fetch": "fetch", "go_to": "walk", "follow": "follow", "come": "coming to you"}.get(capability, "move")
    return {"effect.adjust": "change to a glow", "creation.place": "build", "creation.revise": "change to a build", "entity.remove": "removal",
            "entity.grab": "move", "entity.release": "move", "entity.place": "move", "entity.push": "move", "creation.activate": "use of a build",
            "protect.lock": "lock", "nature.trail": "flower trail", "nature.repair": "repair", "room.undo": "undo"}.get(op, "action")


def _place(args: dict) -> list[float] | None:
    for value in (args.get("center_m"), (args.get("area") or {}).get("center_m"), args.get("position_m"), (args.get("placement") or {}).get("position_m")):
        if isinstance(value, list) and len(value) == 3:
            return [float(v) for v in value]
    return None


class TrustMixin:
    """Mixed into MockHost. The count is the host's (the team's); the remembered commands belong to the room's session."""

    def _reset_trust_session(self) -> None:
        self._trust_entries: OrderedDict[str, TrustEntry] = OrderedDict()
        self._trust_last_gain: float | None = None
        self._trust_charged = 0  # the pips the guard would have charged since Trust last looked (the kernel's _trustCharged)

    @property
    def trust_band(self) -> int:
        return band(self.trust_count)

    def _trust_counts(self) -> bool:
        return self.mode != "creative"

    def _trust_price(self, capability: str, principal: str) -> int:
        """The kernel's PriceFor with Trust's discount: the Gubble's own-idea price at its band (every ability alike)."""
        if self.rules is None:
            return 0
        price = self.rules.price(capability, principal)
        level = self.trust_band
        if principal.startswith("player:") or price <= 0 or level == 0:
            return price
        listed = next(((ordered, factor) for name, ordered, factor in self.rules.prices if name == capability), None)
        return own_idea_price(listed[0], listed[1], level) if listed else price

    # ------------------------------------------------------------------ what the guard charges (decision 69)

    def _charge_price(self, capability: str, principal: str) -> int:
        """What the kernel's guard charges for this at a commit (GuardEnergy's price): nothing without a pool or a price, else the
        price by principal with Trust's discount. In Creative the kernel's shadow pool is charged it too (Trust counts nothing there)."""
        if self.rules is None or self.rules.pips is None:
            return 0
        return self._trust_price(capability, principal)

    def _trust_charge(self, capability: str, principal: str) -> None:
        """The hook where the kernel's ChargeEnergy charges (an effect's start, a bubble's cast, a Lift's rise): the price it would
        charge, noted for Trust. The mock keeps no pool, so nothing else moves."""
        self._trust_charged += max(0, self._charge_price(capability, principal))

    def _take_trust_charge(self) -> int:
        charged, self._trust_charged = self._trust_charged, 0
        return charged

    def _trust_lift_charged(self, lead: dict) -> None:
        """The kernel's TrustLiftCharged: a lift's charge at its rise (on the mock, its arrival: no drift) makes the Gubble's lead a
        paid action, its window gaining once the arrival's window closes clean. A lead the player set is theirs."""
        self._trust_charge("bubble_lift", lead["principal"])
        entry = self._trust_entries.get(lead["action_id"])
        if paid(self._take_trust_charge()) and not lead["principal"].startswith("player:") and entry is not None:
            entry.paid = True

    # ------------------------------------------------------------------ the Gubble's commands

    def _observe_trust(self, principal: str, message: dict, result: dict) -> None:
        """After a commit: a command of the Gubble's opens its window when the guard charged it pips, and every one can be marked
        (the kernel's ObserveTrustCommand). The charge is taken first, whoever's command it was."""
        charged = self._take_trust_charge()
        if principal.startswith("player:") or message["op"] in _NOT_ACTIONS or not result.get("ok"):
            return
        args = message.get("args", {})
        op = message["op"]
        data = result.get("data") if isinstance(result.get("data"), dict) else {}
        capability = (data.get("capability") or args.get("capability")) if op in ("nature.cast", "effect.start") else \
            args.get("goal") if op == "goal.set" else None
        self._settle_trust()
        entries = self._trust_entries
        entries.pop(message["action_id"], None)
        # A goal's job decides whether it did anything: its window waits for the job (_trust_job_ended); a lead is paid at its Lift.
        job = result.get("job_id") if op == "goal.set" else None
        entries[message["action_id"]] = TrustEntry(message["action_id"], op, _what(op, capability, self.rules), _place(args),
                                                   self.clock.now(), self._trust_counts(), paid(charged), job_id=job, pending=job is not None)
        while len(entries) > MAX_REMEMBERED:
            entries.popitem(last=False)
        ended = self.jobs.get(job) if job is not None else None
        if ended is not None and ended["state"] != "running":
            self._trust_job_ended(job, ended["state"])

    def _trust_job_ended(self, job_id: str, state: str) -> None:
        """A goal's job ended (the kernel's TrustLedger.JobEnded): its window opens now if it succeeded (gaining only if it was
        paid), and it gains nothing if it failed or was stopped."""
        for entry in self._trust_entries.values():
            if entry.pending and entry.job_id == job_id:
                entry.pending = False
                if state == "succeeded":
                    entry.opened_at = self.clock.now()
                else:
                    entry.job_failed = True

    def _settle_trust(self) -> None:
        """Every window closed by now settles once: clean, counted and paid, it gains, at most once in GAIN_SPACING_S."""
        now = self.clock.now()
        closed = sorted((e for e in self._trust_entries.values() if not e.settled and not e.pending and e.closes <= now), key=lambda e: e.closes)
        for entry in closed:
            entry.settled = True
            if entry.spoiled or not entry.counted or not entry.paid or entry.job_failed:
                continue
            if self._trust_last_gain is None or entry.closes - self._trust_last_gain >= GAIN_SPACING_S:
                self._trust_last_gain = entry.closes
                if self._trust_counts():
                    self._move_trust(CLEAN_INITIATIVE, "companion:local")

    def _move_trust(self, delta: int, actor: str) -> None:
        before = self.trust_count
        after = max(-TRUST_BOUND, min(TRUST_BOUND, before + delta))
        if after == before:
            return
        self.trust_count = after
        if tier(before) != tier(after):
            self._add_history(self._fact("consequence", change_line(tier(before), tier(after)), actor, actor, None, None, None))

    def _nearest_known_place(self, point: list[float]) -> str | None:
        """The kernel's NearestKnownPlace: the name of what the team's map holds nearest the point, within 1.5 m."""
        best, name = CONSEQUENCE_PLACE_M, None
        for entity_id, entry in self._memory(self._team_principal()).entries.items():
            if not _KNOWN_ID.fullmatch(entity_id):
                continue
            box = entry.view["summary"]["bounds_m"]
            gap = sum((point[i] - min(max(point[i], box["min_m"][i]), box["max_m"][i])) ** 2 for i in range(3)) ** 0.5
            if gap <= best:
                best, name = gap, entry.view["summary"]["display_name"]
        return name

    # ------------------------------------------------------------------ the player's commands

    def _op_receipt_mark(self, principal, message, apply, new_revision):
        if not principal.startswith("player:"):
            raise HostError("permission_denied", "Only the player can do that, directly in the game.", field_path="$.op")
        self._settle_trust()
        entry = self._trust_entries.get(message["args"]["action_id"])
        if entry is None:
            raise HostError("target_not_found", MARK_NOT_FOUND, field_path="$.args.action_id")
        if entry.mark is not None:
            raise HostError("invalid_args", MARKED_ONCE, field_path="$.args.action_id")
        if apply:
            mark = message["args"]["mark"]
            entry.mark = mark
            place = self._nearest_known_place(entry.place) if entry.place is not None else None
            line = f"You marked the Gubble's {entry.what}" + ("" if place is None else " near " + self._quoted(place)) + \
                (": yes, like that." if mark == "yes" else ": not that.")
            self._add_history(self._fact("consequence", line, principal, principal, None, None, entry.place))
            if self._trust_counts():
                self._move_trust(MARK_YES if mark == "yes" else MARK_NOT_THAT, principal)
        return {}

    def _op_trust_reset(self, principal, message, apply, new_revision):
        if not principal.startswith("player:"):
            raise HostError("permission_denied", "Only the player can do that, directly in the game.", field_path="$.op")
        if apply:
            for entry in self._trust_entries.values():
                entry.settled = True
            self._trust_last_gain = None
            self.trust_count = 0
            self._add_history(self._fact("consequence", "You reset the Gubble's Trust: " + phrase(0) + ".", principal, principal, None, None, None))
        return {}
