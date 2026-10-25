"""A mock game host that implements the command contract faithfully enough for P1's host to replace it.

It loads a real room (`game/rooms/test_room` by default) through the room contract and answers
`garage_game.command` and `garage_game.query` messages (either spelling of each id: ids.py) with `garage_game.result` messages:

- the principal comes from the caller (the trusted transport), never from the message;
- every message is validated against the contract (with ECMA-262 patterns), plus the value rules
  (int64, finite numbers, no hidden characters) and the semantic rules a schema cannot express;
- commands are idempotent per principal and action_id, fingerprinted over the canonical command
  JSON exactly as received, as the contract and the kernel host do: a retry that only adds
  `"preview": false` is another command. Same content replays the receipt, different content is
  `action_id_conflict`;
- a companion may send at most 30 messages in any one second, commands and queries together,
  counted before parsing so invalid messages count too; stops are exempt and the player is never
  limited (the kernel host's limit);
- `goal.stop` and `effect.stop` always apply: they never fail on revisions, rate limits, a full
  receipt ledger or a reused action_id, and the player's stop also stops the companion;
- `expected_revision` and `expected_entities` are checked here, never by the sender;
- goals, effects and grabs get transient receipts (bounded per principal, oldest dropped first);
  everything else gets a durable receipt (at most 2,048 per room, as in the kernel host, of which
  the last 256 only the player's commands may use), and a checkpoint compacts the durable ones to
  their revision and a 64-bit fingerprint prefix, after which `receipt.lookup` answers
  `compacted: true`;
- a companion perceives only what is in line of sight of its avatar (perception.py), and
  `observe`, `entities.list`, `entity.inspect`, the counts in `room.describe` and command targets
  all use that perception;
- a companion remembers what its own avatar saw this session (perception memory): queries show
  remembered things marked `seen: "remembered"` with their age and `may_be_stale`, and goals that
  only move or turn the companion may aim at them, re-checked on arrival; anything that changes an
  entity still needs it in sight now. Memory is per companion, bounded, cleared with the session
  or the room, never saved, and never filled through another avatar;
- commands in the policy's held set wait for the player: the host mints a 128-bit `request_id`, the
  player approves or denies through `player_decide` (the game UI, never the companion's
  connection), and an approved command commits under its original principal and action_id with
  `approved_by`; approvals expire, and lapse as soon as the entities they touch change (at most 8
  wait per principal). Every safety property also holds with an empty held set;
- protection is the player's: `protect.unlock` (every op in `$defs/player_only_ops`) is refused
  from companion principals, and no companion command changes a protected entity or any protection
  state except adding a lock;
- nature (Grow, Fire and its kinds, the campfires, repair, undo; contracts/README.md "Nature") is hosted by mock_nature.py as
  the kernel's nature answers it, minus physics: the nature ops' args checked as CheckNatureArgs checks them (request_invalid,
  field_unknown), then Grow growing the island's plant copies the game side gives it (`add_plant`), Fire previewed and cast as
  exactly that preview, burning its circle by the fire rule (Creative's one ask before a build burns, the player's no sparing it),
  a Fireline once learned, Snuff (transient: no revision), the starting campfire and the manifest's `x_firepits` that Fire lights
  and 30 s of rain puts out, `player_rests` (the rest key: the first rest by the starting campfire teaches Fire, decision 72),
  `nature.repair`, `room.undo` of the latest nature steps (in Creative only: outside it, refused before anything else) and
  `nature.describe`. The box world has no water, so never a Will-o'-wisp or a waterpad. `nature.trail` went with Bloom
  (decision 52): checked the same way, then refused unsupported_capability in the kernel's words;
- the price follows the principal (mock_energy.py, the kernel's CommandHostEnergy): one pool per mode, the player's commands paying
  the pack's ordered price and the Gubble's its own-idea price, once, at commit; an empty pool refuses with energy_exhausted, its
  balance and price; the shadow pool in Creative never refuses;
- the Bubble as the founder redesigned it (decisions 55 to 60; the kernel's Kernel/Bubble) is hosted abstractly, with the
  kernel's refusals in its order and its receipts: effect.start of Bubbles casts one player-sized bubble at a spot and the
  place decides (on the player a carry, the companion's previewed first, its ghost shown and the same cast committed a moment
  later; elsewhere a bubble to walk into, `player_steps_into`, judged where it forms, resting on what is under its spot, so one
  that would form round the player is on them; the box world has no water, so never a boat); nature.cast still
  names bubble_carry (on the player naming its preview_id, or at a point) and bubble_boat (refused: no water), and refuses
  bubble_lift (Lift is a lead); one bubble at a time, a recast popping it, no pause after a pop; bubbles count toward the
  room's effect cap at any effect's start; a committed bubble is transient with an effect: id, popped by goal.stop of the
  Gubble, effect.stop, the dive key (`player_pops_bubble`) or its timer, and its receipt says carried and ended;
- goal.set lead (the smart ask's steering): the Gubble leads a bubble (target: its effect: id, never an avatar) to a spot
  (position_m inside the island: the kernel's InsideIsland, the room's bounds without a sea, the top and the bottom round an
  island), set by the player or the companion; the job ends on `goal_arrived` (the bubble at the spot), `goal_blocked`
  (target_unreachable), or the bubble popping first (target_not_found); the Gubble's own stop or a new goal pops the bubble it
  leads; the goal.set receipt says how the lead ended. A spot higher than the bubble's is a lift (data.lift, lift_price), refused
  energy_exhausted at the lead when the setter's pool cannot pay it, and charged at the rise (here, the arrival: the lead ends
  no_pips when it cannot be paid then). The mock has no physics: a bubble does not drift, and nothing falls out of one (the
  kernel's fall out of a bubble, decision 88, needs the body); the game side says whether a landing hurt (`player_lands`), and
  the fall after the Gubble's own pop that hurt is a consequence fact in the kernel's words that costs Trust (decision 96);
- unknown, removed, foreign and unperceived entity ids all fail with the same `target_not_found`;
- the island's abilities come from its rules pack, the one the kernel host loads (game/rules/storybook_wild/v5.json:
  Glow, Bubbles, Fireworks, Grow and Fire; checked fail-closed; a pinned pack's Bloom, growth.seed, is read as Grow): `capabilities.list` answers from it, and `effect.start` follows the
  kernel's rules, order of refusals, codes and result data (CommandHostEffects.cs), the same for every primitive
  (light.emit, particles.float, particles.burst). Grow and Fire are cast with nature.cast, never effect.start, and
  what needs the game itself (terrain-aware Glow, the wish queue) is answered `unavailable`. The Gubble
  carries every ability out: a companion may target only its own avatar (self) or name no targets (a point within
  reach and in the team's sight); effect ids are
  opaque; effects end at their duration, on `effect.stop` (the player any, a companion its own) and on a `goal.stop`
  that covers the Gubble. Effects are not entities: no query lists them, as on the kernel host;
- world text is emitted only in contract fields and sanitised (textsafety.py);
- every result is validated before it leaves; one that would not validate becomes `internal_error`.

The world model is deliberately simple (boxes, no physics): Run 2 replaces it with the kernel.
What must carry over is the message behaviour, which the boundary tests pin down.
"""
from __future__ import annotations

import base64
import copy
import hashlib
import logging
import math
import secrets
import threading
import time
from collections import OrderedDict, deque
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from . import eyes, ids, perception, textsafety
from .mock_energy import ENERGY_EXHAUSTED_MESSAGE, EnergyMixin  # noqa: F401
from .mock_journal import JournalMixin
from .mock_nature import (FIRE_NOT_LEARNED, FRESH_WALLET, UNDO_NOT_EARLIER, UNDO_ONLY_NATURE,  # noqa: F401
                          NatureMixin)
from .mock_inventory import InventoryMixin
from .mock_trust import TrustMixin
from .glow_progress import GlowProgress
from .canonical import CanonicalJsonError
from .contract import (
    COMMAND_SCHEMA,
    QUERY_SCHEMA,
    Contracts,
    DEFAULT_REPO_ROOT,
    creation_sources,
    find_forbidden_key,
    is_authority_key,
    value_problems,
)
from .refusals import (FRACTAL_REFUSAL, HostError, base_result, effect_args_error, failure, forbidden_key_error,  # noqa: F401
                       lead_args_error, map_schema_errors, names_a_fractal, nature_args_error, value_error)

log = logging.getLogger("garage_game.mock_host")

PLAYER = "player:local"
COMPANION = "companion:local"
OWN_AVATAR = {PLAYER: "avatar:player", COMPANION: "avatar:companion"}
STOP_OPS = frozenset({"goal.stop", "effect.stop"})
# The nature commands (contracts/README.md "Nature"): their args checked as the kernel's CheckNatureArgs checks them, then
# hosted by mock_nature.py as the kernel's nature answers them (its words in mock_nature.py, where test_kernel_alignment.py holds
# them to the kernel's).
NATURE_OPS = frozenset({"nature.cast", "nature.trail", "nature.repair"})
# The flower trail went with Bloom (decision 52): nature.trail is refused in the kernel's words (CommandHost.TrailRetired).
TRAIL_RETIRED = "The flower trail went with Bloom: Grow grows the island's own plants and leaves no trail."
# An older pack's Bloom (growth.seed) is read as Grow, as the kernel reads it (IslandRules.LegacyBloomAs).
LEGACY_BLOOM_AS = "grow"
# Outside Creative there is no undo at all (the founder, 13 October): the kernel's Unsupported, before any revision check.
UNDO_NOT_IN_THIS_MODE = "There is no undo in Challenge or Live: what happens there stays."
# The mechanics round (M0, and M1's gather): its player-only ops, and the kernel's words for what is not built yet
# (CommandHost.Unsupported: MechanicsNotYet, DifficultyNotYet) and for a Fractal the companion may not gather (FractalRefusal). The kernel hosts the player's item.pickup and item.gather since M1; the mock, which places no Fractals and
# keeps no wallet, answers the player's not available yet.
MECHANICS_PLAYER_OPS = frozenset({"item.pickup", "item.gather", "knack.set", "fractal.spend", "receipt.mark", "trust.reset",
                                  # P-Inventory: the player's use and drop need the body and the team file (the kernel hosts them);
                                  # P-Inventory-2: so does the hand's selection.
                                  "inventory.use", "inventory.drop", "inventory.select"})
BUBBLES_EXPERTISE = frozenset({"bubble_carry", "bubble_boat", "bubble_lift"})
MECHANICS_NOT_YET = "This operation is not available yet."
DIFFICULTY_NOT_YET = "Difficulties are chosen at the opening menu, which is not built yet."
# The Bubble (M9): the kernel's provisional numbers (Kernel/Bubble/BubbleLaws.cs, held equal by test_kernel_alignment.py) and
# its words for the refusals a companion meets most.
BUBBLE_CARRY_RADIUS_M = (0.07, 0.2)
BUBBLE_BOAT_RADIUS_M = (0.08, 0.3)
BUBBLE_CARRY_RADIUS_DEFAULT_M = 0.08  # the Bubble's one size, round the 10 cm body (BubbleLaws.CarryRadiusDefaultM)
# The time the player's Bubbles key names (decision 95: a bubble 8 s; decision 112: a boat 20 s; BubbleLaws.CarryDurationDefaultS and
# BoatDurationDefaultS), never past the island's duration_max_s (player_bubble).
BUBBLE_CARRY_DURATION_DEFAULT_S = 8.0
BUBBLE_BOAT_DURATION_DEFAULT_S = 20.0
BUBBLE_GHOST_LEAD_S = 1.0
BUBBLE_GHOST_LIFETIME_S = 15.0
BUBBLE_ENVELOP_REACH_M = 0.05
BUBBLE_STEP_IN_CLEARANCE_M = 0.02
BUBBLE_MAX = 1  # one bubble at a time (decision 59): a recast pops the one there
BUBBLE_MAX_GHOSTS = 8
BUBBLE_LIFT_ABOVE_DRIFT_M = 0.1  # a lead's spot this much higher than the bubble is a lift (BubbleLaws.LiftAboveDriftM)
ISLAND_SLACK_M = 0.02  # a spot this far past the room's bounds still counts as inside them (the kernel's CommandHost.IslandSlackM)
BUBBLE_GHOST_LEAD = "The player is still seeing the bubble's ghost; cast it again in a moment."
BUBBLE_PREVIEW_AGAIN = "Preview this bubble again before casting: the place, the player or the time has changed."
BUBBLE_GHOST_FIRST = "A bubble on the player is previewed first: preview this cast so the player sees its ghost, then cast it again."
BUBBLE_AT_A_SPOT = "The Bubble is cast at a spot: on the player, on water or on the ground beside them. Name no targets."
BUBBLE_LIFT_IS_A_LEAD = "Lift is leading a bubble to a higher spot: cast the Bubble, then lead it there (goal.set lead)."
BUBBLE_ON_THE_PLAYER = ("The player stands there: cast the carry on the player (it is previewed first), or beside them so they can step "
                        "in.")
BUBBLE_NO_WATER = "A Bubble boat is cast on water."
LEAD_POPPED = "The bubble popped before it got there."
LEAD_BLOCKED = "The bubble cannot get there from here."
TRANSIENT_OPS = frozenset({"entity.grab", "creation.activate", "goal.set", "goal.stop", "effect.start", "effect.stop",
                           "world.set_physics", "world.set_mode", "glow.configure", "effect.adjust", "receipt.mark", "trust.reset",
                           # P-Inventory (mock_inventory.py): a use changes no room state; a store, a take and a drop are
                           # durable room changes, as on the kernel (they are not listed here).
                           "inventory.use", "inventory.select"})
# The mechanics round's player-only ops the mock hosts (M8's Trust, mock_trust.py); the rest answer as not available yet.
MECHANICS_HOSTED = frozenset({"receipt.mark", "trust.reset"})
# Durable, but they change no world state, so they do not move the room revision.
NO_REVISION_OPS = frozenset({"room.checkpoint", "journal.note"})
# The game's world physics presets (game/scripts/world_physics_profile.gd PRESET_IDS). Player-only, not saved.
PHYSICS_PRESETS = frozenset({"room_tuned", "room_real", "room_floaty"})
LOCKABLE_KINDS = frozenset({"object", "creation"})
# A new style.set may pin only a reviewed preset. Seed and draft files still change (contracts/README.md
# "Styles are versioned files"); a retired preset stays loadable for old saves but is not offered for new pins.
PINNABLE_STATUSES = frozenset({"candidate", "approved"})
OCCLUDING_KINDS = frozenset({"shell", "object", "creation"})
# What perception memory keeps: everything but the shell, which is always in sight.
REMEMBERED_KINDS = frozenset({"object", "creation", "avatar", "effect"})
# Goals that only move or turn the companion's own avatar may aim at something it remembers but
# cannot see now (founder decision, 6 October 2026). The host re-checks when the avatar arrives.
REMEMBERED_TARGET_GOALS = frozenset({"go_to", "look_at", "point_at", "come", "fetch"})
# The goals that walk somewhere: a body blocked on them for unreachable_after_s fails them with target_unreachable
# (the kernel's WalkingGoals; come with no target and go_to a place included, which have no job to report).
WALKING_GOALS = frozenset({"come", "go_to", "fetch"})

# A come (and a fetch's walk back) ends this far from the player, centre to centre (CompanionAvatar.ComeArrivalM).
COME_ARRIVAL_M = 0.14
# The Gubble's mean height over its support (SmallPlayerController.HoverHeightM).
# This box model has no physics tick and does not simulate the real body's 4 mm bob.
HOVER_M = 0.02
# Both avatars have the 10 cm body (perception.py): radius 0.02 m, height 0.10 m, as Entity half_extents.
_BODY = [perception.BODY_RADIUS_M, perception.BODY_HEIGHT_M, perception.BODY_RADIUS_M]

DEFAULT_ROOM_DIR = DEFAULT_REPO_ROOT / "game" / "rooms" / "test_room"
DEFAULT_STYLES_DIR = DEFAULT_REPO_ROOT / "game" / "styles"

# The island's rules (contracts/island-rules.schema.json): the abilities effect.start casts and capabilities.list lists,
# read from the same pack the kernel host loads (RoomWorld.DefaultRulesId and DefaultRulesVersion, res://rules/<id>/v<N>.json).
# v4 retains Glow's earned five-light ceiling and advertises native nature abilities through their own command route.
DEFAULT_RULES_ID = "storybook_wild"
DEFAULT_RULES_VERSION = 5
DEFAULT_RULES_DIR = DEFAULT_REPO_ROOT / "game" / "rules"
# A pack is small (at most 32 abilities); anything larger is refused before it is parsed (IslandRules.MaxPackBytes).
MAX_RULES_PACK_BYTES = 65536
# capabilities.list pages this many when no limit is given (CommandHost.CapabilitiesDefaultLimit).
CAPABILITIES_DEFAULT_LIMIT = 50
# A point effect's place must be in the team's sight: a column twice EFFECT_SIGHT_HALF_WIDTH_M wide over the centre
# (CommandHost.EffectSightHalfWidthM), as tall as the wisp floats above it (GlowLook.WispLiftM).
EFFECT_SIGHT_HALF_WIDTH_M = 0.02
WISP_LIFT_M = 0.06
# Effect ids: 'effect:' and 26 lowercase base32 characters (130 random bits), opaque like job ids, never a counter.
EFFECT_ID_ALPHABET = "abcdefghijklmnopqrstuvwxyz234567"
EFFECT_ID_LENGTH = 26
# A room with the landscape sea has no walls to keep a point effect inside (RoomSea.ExtensionName).
SEA_EXTENSION = "x_landscape_sea"
# The avatar that carries out every ability the player casts (the Gubble).
GUBBLE = "avatar:companion"

RECOMMENDED_HELD_OPS = frozenset({"entity.remove", "entity.transform", "creation.revise", "room.undo", "style.set"})


@dataclass
class HostPolicy:
    """The one policy table. Values the founder decides are marked; see docs/companion/SECURITY.md.

    Where the kernel host (game/scripts/native/Kernel/CommandHost.cs and creation_authority.gd) fixes a
    number, the default here is that number, and test_kernel_alignment.py fails if the two drift apart.
    """

    # Companion commands the host holds for the player's click. The founder's goal is none
    # (live play by conversation); every safety property is tested with this set empty.
    companion_approval_ops: frozenset[str] = RECOMMENDED_HELD_OPS
    approval_ttl_s: float = 300.0  # kernel: ApprovalLifetime, 5 minutes
    max_pending_approvals: int = 8  # per principal; kernel: MaxPendingApprovals
    # The companion's rate limit, as the kernel host's (CompanionMessagesPerSecond): at most this many
    # messages from one companion principal in any one-second window, commands and queries together,
    # counted before parsing, so invalid messages count too. Stops are exempt; the player is never limited.
    # The MCP adapter keeps its own, tighter per-kind buckets in front of this (server.py).
    companion_messages_per_s: int = 30
    # Perception (founder decision 1: line of sight). See docs/companion/PERCEPTION.md. observe answers everything in
    # line of sight with no distance cut-off (the kernel's, since the 10x world); the contract bounds a given radius_m
    # to 150 m. A finite value here caps whatever radius observe asks for (tests).
    observe_max_radius_m: float = math.inf
    companion_targets_need_perception: bool = True  # commands may only name entities in sight now
    follow_player_out_of_sight: bool = True  # follow/come keep working when the player is not in sight
    # Perception memory (founder decision, 6 October 2026: the companion remembers what it saw).
    # At most this many entities per companion, least recently seen forgotten first; 0 turns it off.
    perception_memory_entries: int = 1024
    # The team's sight (kernel: SharedSight): for the companion, "in sight now" means either avatar sees it, the
    # player's avatar is always known, and both avatars' eyes fill the team's map. Tests of one avatar's sight turn it
    # off, as the kernel's own tests do.
    shared_sight: bool = True
    # A walking goal whose body reports blocked this long without a break fails (kernel: UnreachableAfterS).
    unreachable_after_s: float = 5.0
    # The host's sight sweep (kernel: DefaultTeamSightIntervalS): both avatars' eyes fill the team's map this often on
    # the host's own clock, whether or not the companion is asking. The mock sweeps when its clock has moved on this
    # far by the next request or world change (or on sight_sweep()); 0 turns it off (tests of one avatar's sight).
    team_sight_interval_s: float = 0.25
    # A remembered entity is flagged may_be_stale after this long, or as soon as it changes.
    perception_memory_stale_after_s: float = 60.0
    max_jobs_per_principal: int = 256  # goal jobs kept for jobs.status, oldest finished dropped first
    # Effects: the island's rules (the pack) bound each ability and its max_active; the engine also caps the active
    # effects in a room, whatever the ability and whoever cast them (kernel: MaxActiveEffects). Tests lower it.
    max_active_effects: int = 8
    max_creations: int = 32
    carry_limit_kg: dict[str, float] = field(default_factory=lambda: {"avatar:player": 0.5, "avatar:companion": 2.0})
    history_depth: int = 64
    max_checkpoints: int = 16  # kernel: MAX_CHECKPOINTS (room-state allows up to 64)
    # Durable receipts per room, across principals. kernel: MAX_RECEIPTS (room-state allows up to 4,096).
    max_durable_receipts: int = 2048
    # The last slots of the ledger are the player's: a companion that fills the ledger can never block
    # the player's own commands (a lock, above all) until a checkpoint compacts it. kernel: PLAYER_RECEIPT_RESERVE
    player_receipt_reserve: int = 256
    max_transient_receipts_per_principal: int = 1024  # kernel: MaxTransientPerPrincipal
    # Compacted receipts keep their revision and a 64-bit fingerprint prefix; the oldest are forgotten
    # first. kernel: MAX_COMPACTED
    max_compacted_receipts: int = 4096
    # The Gubble's eyes (view.look; kernel: CommandHostEyes.cs). The mock draws a placeholder picture; False models a
    # headless kernel run, which answers unavailable. Looks a companion may take at once, and how fast they come back.
    look_renderer: bool = True
    look_burst: int = eyes.LOOK_BURST
    look_refill_s: float = eyes.LOOK_REFILL_S


NO_HOLDS = HostPolicy(companion_approval_ops=frozenset())


class SystemClock:
    def now(self) -> float:
        return time.time()


class FakeClock:
    """Deterministic clock for tests."""

    def __init__(self, start: float = 1_790_000_000.0):
        self.t = start

    def now(self) -> float:
        return self.t

    def advance(self, seconds: float) -> None:
        self.t += seconds


def utc(ts: float) -> str:
    return datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _not_found(field_path: str) -> HostError:
    # One answer for unknown, removed, other-room and unperceived ids: it confirms nothing about what exists.
    return HostError("target_not_found", "No entity with that id is in this room.", field_path=field_path)


@dataclass
class Entity:
    id: str
    kind: str
    display_name: str
    position: list[float]
    half_extents: list[float]  # x, z half sizes and full height in y, measured from the bottom-centre pivot
    affordances: list[str]
    movable: bool
    provenance_kind: str
    revision: int = 0
    category: str | None = None
    category_group: str | None = None
    protected: bool = False
    protected_by: str | None = None
    held_by: str | None = None
    mass_kg: float | None = None
    parts: dict[str, float] = field(default_factory=dict)
    removed: bool = False
    texts: list[str] = field(default_factory=list)
    explicit_bounds: dict | None = None
    created_by: str | None = None
    # What it is made of, for nature (never emitted): a shell part's material, an asset's material roles, a creation's parts'
    # materials in order (the fire rule's flammable test, the ground's fuel, a scorched build's repair quote).
    materials: list[str] = field(default_factory=list)

    def bounds(self) -> dict:
        if self.explicit_bounds is not None:
            return self.explicit_bounds
        x, y, z = self.position
        hx, height, hz = self.half_extents
        return {"min_m": [_r(x - hx), _r(y), _r(z - hz)], "max_m": [_r(x + hx), _r(y + height), _r(z + hz)]}

    def summary(self, text: textsafety.TextRules) -> dict:
        out = {
            "id": self.id,
            "kind": self.kind,
            "display_name": text.display_text(self.display_name, 80),
            "position_m": [_r(v) for v in self.position],
            "bounds_m": self.bounds(),
            "affordances": list(dict.fromkeys(self.affordances)),
            "movable": self.movable,
            "protected": self.protected,
            "provenance_kind": self.provenance_kind,
            "revision": self.revision,
        }
        if self.category:
            out["category"] = text.display_text(self.category, 60)
        if self.category_group:
            out["category_group"] = self.category_group
        if self.held_by:
            out["held_by"] = self.held_by
        return out

    def durable_state(self) -> dict:
        return {
            "kind": self.kind, "display_name": self.display_name, "position": list(self.position),
            "half_extents": list(self.half_extents), "affordances": list(self.affordances),
            "movable": self.movable, "provenance_kind": self.provenance_kind, "revision": self.revision,
            "category": self.category, "category_group": self.category_group, "protected": self.protected,
            "protected_by": self.protected_by, "mass_kg": self.mass_kg, "parts": dict(self.parts),
            "removed": self.removed, "texts": list(self.texts), "explicit_bounds": copy.deepcopy(self.explicit_bounds),
            "created_by": self.created_by, "materials": list(self.materials),
        }


def _r(value: float) -> float:
    rounded = round(float(value), 4)
    return 0.0 if rounded == 0 else rounded


@dataclass
class Receipt:
    fingerprint: str
    result: dict
    durable: bool


@dataclass
class Compacted:
    fingerprint_prefix: str  # the first 16 hex digits (64 bits), all the kernel's save keeps
    op: str
    revision: int


COMPACTED_PREFIX_HEX = 16


@dataclass
class Approval:
    request_id: str
    principal: str
    action_id: str
    fingerprint: str
    message: dict
    expires_at: float
    touched: dict[str, int]
    room_revision: int
    pending_result: dict
    state: str = "pending"
    result: dict | None = None
    # The kernel's Hold(bindRevision): a nature change judged on the room as it is lapses as soon as the room revision moves.
    bind_revision: bool = False
    # Creative's one ask before a fire burns a build (the kernel's SparesOnDeny): the player's no lights the fire with them spared.
    spares_on_deny: bool = False


@dataclass
class Remembered:
    """One entity as a companion last saw it. Only `view` and the timing ever reach the companion."""

    view: dict  # {"summary", "parts", "protected_by"}: what was perceivable, sanitised, at the last sighting
    seen_at: float
    seen_revision: int  # the room revision at the last sighting
    mass_kg: float | None  # static asset data for the fetch check; never emitted
    changed: bool = False  # it changed in some way since (sticky until seen again); emitted only as may_be_stale


@dataclass
class PerceptionMemory:
    """What one companion has seen in this session and room, least recently seen first."""

    room_id: str
    entries: OrderedDict = field(default_factory=OrderedDict)  # entity id -> Remembered


@dataclass(frozen=True)
class Ability:
    """One ability of an island (contracts/island-rules.schema.json $defs/ability), as the kernel's IslandAbility."""

    capability: str
    category: str
    primitive: str
    cast_by: frozenset[str]  # "player" and or "companion"
    tier: str  # auto, auto_undo, preview_commit or keyed_yes
    targets: frozenset[str]  # "self" (the effect follows the Gubble) and or "point" (the effect at area.center_m)
    reach_m: float
    params: dict[str, tuple[float, float, float]]  # name -> (min, max, default), in ordinal order
    area_radius_default_m: float
    area_radius_max_m: float
    duration_default_s: float
    duration_max_s: float
    max_active: int


@dataclass(frozen=True)
class IslandRules:
    """An island's own laws as data: the abilities it grants (the kernel's IslandRules)."""

    rules_id: str
    rules_version: int
    abilities: tuple[Ability, ...]
    sha256: str
    # The pack's cost block (the mechanics round): each ability's ordered price and own-idea factor, in whole pips, read
    # from the same pack JSON the kernel loads. Empty when the pack has none: nothing is charged, today's behaviour.
    prices: tuple[tuple[str, int, float], ...] = ()
    # The pack's traits (the mechanics round), as the kernel's VitalsRules reads them for a new body and player.status:
    # hearts (health per quarter, start hearts), the bar's first band in metres, and the pool (start, expertise per pip,
    # ceiling). None when the pack leaves them out: today's 100 health, 0.45 m and no pool.
    hearts: tuple[float, int] | None = None
    stamina_start_m: float | None = None
    pips: tuple[int, int, int] | None = None
    # The pool's refill (traits.pips: refill_near_every_s, refill_near_within_m, refill_far_every_s) and a new island's wallet
    # (traits.wallet.start), read as the kernel's VitalsRules and NatureState.FreshWallet read them. None when the pack has none.
    refill: tuple[float, float, float] | None = None
    wallet: dict[str, int] | None = None
    # The pack's inventory block (P-Inventory): the kinds and the caps of the pockets and the chest, as the pack says; None without it.
    inventory: dict | None = None

    def ability(self, capability: str | None) -> Ability | None:
        return next((a for a in self.abilities if a.capability == capability), None)

    def price(self, capability: str, principal: str) -> int:
        """What casting `capability` costs `principal` outside Creative, in whole pips. The price follows the principal
        alone: the player's order pays the ordered price, every command on the Gubble's connection the own-idea price.
        An ability the pack does not price costs nothing. The pool charges it (mock_energy.py), with Trust's discount (M8)."""
        for name, ordered, factor in self.prices:
            if name == capability:
                return ordered if principal.startswith("player:") else round(ordered * factor)
        return 0


class RulesError(ValueError):
    """A rules pack that does not pass the checks. The message names the problem, never the pack's text."""


def rules_path(rules_id: str = DEFAULT_RULES_ID, rules_version: int = DEFAULT_RULES_VERSION,
               rules_dir: Path = DEFAULT_RULES_DIR) -> Path:
    return Path(rules_dir) / rules_id / f"v{rules_version}.json"


def parse_rules(contracts: Contracts, pack: Any, raw: bytes | None = None, path: Path | None = None) -> IslandRules:
    """Check a pack fail-closed, as the kernel's loader does: the island-rules schema (with each primitive's outer
    limits) and contracts/validate.py's extra checks (unique capabilities and categories, defaults inside their
    ranges, the pack at its own path). Any problem refuses the whole pack."""
    validate = contracts.validate
    if not hasattr(validate, "check_rules"):
        raise RulesError("these contracts have no island rules")
    try:
        problems = validate.schema_errors(pack, "island_rules")
        if not problems:
            problems = validate.check_rules(pack, "rules", path)
    except Exception as error:  # a validator that cannot read it refuses it
        raise RulesError(f"the pack could not be checked ({type(error).__name__})") from None
    if problems:
        raise RulesError(f"the pack does not pass its checks ({len(problems)} problems)")
    abilities = tuple(Ability(
        capability=LEGACY_BLOOM_AS if a["primitive"] == "growth.seed" else a["capability"], category=a["category"], primitive=a["primitive"], cast_by=frozenset(a["cast_by"]),
        tier=a["tier"], targets=frozenset(a["targets"]), reach_m=float(a["reach_m"]),
        params={name: (float(b["min"]), float(b["max"]), float(b["default"])) for name, b in sorted(a["params"].items())},
        area_radius_default_m=float(a["area_radius_default_m"]), area_radius_max_m=float(a["area_radius_max_m"]),
        duration_default_s=float(a["duration_default_s"]), duration_max_s=float(a["duration_max_s"]),
        max_active=int(a["max_active"])) for a in pack["abilities"])
    body = raw if raw is not None else contracts.canonical_bytes(pack)
    legacy = any(a["primitive"] == "growth.seed" for a in pack["abilities"])
    prices = tuple(sorted((LEGACY_BLOOM_AS if legacy and name == "bloom" else name, int(price["ordered"]), float(price["own_idea_factor"]))
                          for name, price in pack.get("cost", {}).get("prices", {}).items()))
    traits = pack.get("traits", {})
    hearts = (float(traits["hearts"]["health_per_quarter"]), int(traits["hearts"]["start_hearts"])) if "hearts" in traits else None
    stamina = float(traits["stamina"]["start_m"]) if "stamina" in traits else None
    pips = (int(traits["pips"]["start"]), int(traits["pips"]["expertise_per_pip"]), int(traits["pips"]["ceiling"])) if "pips" in traits else None
    refill = (float(traits["pips"]["refill_near_every_s"]), float(traits["pips"]["refill_near_within_m"]),
              float(traits["pips"]["refill_far_every_s"])) if "pips" in traits else None
    wallet = {name: int(units) for name, units in sorted(traits["wallet"]["start"].items())} if "wallet" in traits else None
    inventory = copy.deepcopy(pack["inventory"]) if "inventory" in pack else None
    return IslandRules(pack["rules_id"], int(pack["rules_version"]), abilities, hashlib.sha256(body).hexdigest(), prices, hearts, stamina, pips,
                       refill, wallet, inventory)


def load_rules(contracts: Contracts, path: Path) -> IslandRules:
    """Load and check the pack at `path` (game/rules/<rules_id>/v<N>.json). Raises RulesError for any problem."""
    path = Path(path)
    try:
        raw = path.read_bytes()
    except OSError:
        raise RulesError("there is no rules pack there") from None
    if not raw or len(raw) > MAX_RULES_PACK_BYTES:
        raise RulesError(f"a rules pack is 1 to {MAX_RULES_PACK_BYTES} bytes")
    try:
        pack = contracts.validate.load_strict(path)
    except Exception:
        raise RulesError("the rules pack is not strict JSON") from None
    return parse_rules(contracts, pack, raw, path)


@dataclass
class ActiveEffect:
    """One running effect. In memory only, like the kernel's: never an entity, a snapshot, a receipt's store or a save."""

    id: str
    capability: str
    principal: str  # who cast it: the player may stop any effect, a companion only its own
    carrier: str  # the avatar that carries it out (the Gubble)
    follows: bool  # self: the light follows its carrier; otherwise it stays at centre
    centre: list[float]
    radius_m: float
    params: dict[str, float]
    expires_at: float
    variant: str = "plain"
    touch: str = "plain"
    bubble_source: str | None = None
    connections: set[str] = field(default_factory=set)


@dataclass
class MockBubble:
    """One bubble, abstractly (the kernel's Kernel/Bubble/Bubble.cs, with no physics): it does not drift here, and no one falls out of it."""

    id: str
    capability: str
    principal: str  # who cast it: the player may pop any bubble, a companion only its own
    action_id: str
    target: str | None  # the avatar a carry was cast on, or None (a bubble at a point: the walk-in)
    centre: list[float]
    radius_m: float
    expires_at: float
    holding: bool  # it holds the player now
    carried: bool  # it has held the player
    lead: dict | None = None  # the Gubble's lead taking it to a spot (goal.set lead): job_id, principal, action_id, spot, lift


def _is_bubble(message: Any) -> bool:
    """A nature.cast of a Bubbles expertise: transient, as an effect (the kernel's IsBubbleCapability)."""
    return (isinstance(message, dict) and message.get("op") == "nature.cast" and isinstance(message.get("args"), dict)
            and message["args"].get("capability") in BUBBLES_EXPERTISE)


def _is_stop(message: Any) -> bool:
    return isinstance(message, dict) and ids.is_schema(message.get("schema"), "command") and message.get("op") in STOP_OPS


def _might_be_stop(raw: bytes) -> bool:
    """Whether raw text could be a goal.stop or effect.stop. JSON spells a letter or '.' only literally or
    with a \\u escape, so text with neither op name and no \\u escape is never a stop (the kernel's MightBeStop)."""
    return b"goal.stop" in raw or b"effect.stop" in raw or b"\\u" in raw


class MockHost(JournalMixin, TrustMixin, NatureMixin, EnergyMixin, InventoryMixin):
    """The game side of the companion boundary, minus physics."""

    def __init__(self, contracts: Contracts, room_dir: Path | None = None, styles_dir: Path | None = None,
                 policy: HostPolicy | None = None, clock=None, extra_companions: dict[str, str] | None = None,
                 rules_file: Path | None = None):
        self.contracts = contracts
        self.policy = policy or HostPolicy()
        self.clock = clock or SystemClock()
        self._lock = threading.RLock()
        self.room_dir = Path(room_dir or DEFAULT_ROOM_DIR)
        self.style_status: dict[tuple[str, int], str] = {}
        self.styles = self._index_styles(Path(styles_dir or DEFAULT_STYLES_DIR))
        # principal -> its own avatar. Tests add a second companion to prove principals stay apart.
        self.avatars: dict[str, str] = dict(OWN_AVATAR)
        self.avatars.update(extra_companions or {})
        self.text = contracts.text_rules  # the untrusted-text rule this contract can carry
        self._calls: dict[str, deque] = {}  # companion principal -> times of its counted messages, oldest first
        self._view: set[str] | None = None  # what the current requester may name; None means everything
        self.listeners: list[Callable[[str, dict], None]] = []
        self._known_ops = frozenset(contracts.command_ops) | frozenset(contracts.query_ops)
        # The island's rules (the kernel's CommandHost.Rules): None when the pack did not load, and then no abilities.
        self.rules: IslandRules | None = None
        self.rules_notice = ""
        # Team profile, separate from room state and command receipts. Room/rules reloads never erase learning.
        self.mode = "creative"
        self._glow_profiles = {mode: GlowProgress() for mode in ("creative", "challenge", "live")}
        # The box mock has no body tick: these profiles report state, never fabricate physical challenge completion. A new
        # body is the rules pack's (the kernel's VitalsRules.Fresh), set again when the rules load while it is untouched.
        self._vitals_profiles = {mode: self._fresh_vitals() for mode in ("creative", "challenge", "live")}
        # The player's own setting "Let my AI see through the Gubble's eyes" (the profile's ai_eyes, never room state):
        # only the player's control (set_gubble_eyes) changes it, never a request. Looks spent, per companion principal.
        self.gubble_eyes = eyes.DEFAULT_GUBBLE_EYES
        self._look_buckets: dict[str, eyes.LookBucket] = {}
        # Trust (M8, mock_trust.py): the team's, so a room reload keeps it; only the host's own observation and the player's
        # marks and reset move it, never a request field.
        self.trust_count = 0
        # The team's nature expertise (Fire's and Grow's; decision 72's first_fire among them: no Fire until the first rest by the
        # starting campfire, player_rests) and the player's "Let the Gubble use fire" (mock_nature.py), and the pool (mock_energy.py).
        self._init_nature_team()
        # The team's "Let the Gubble use the chest" (mock_inventory.py), off by default; only the player's control changes it.
        self._init_inventory_team()
        self._reset_energy()
        self._reset_room()
        # As RoomWorld hands the kernel host its rules (LoadRules), read before the server locks itself down.
        self.load_rules(rules_file or rules_path())

    @property
    def glow_progress(self) -> GlowProgress:
        return self._glow_profiles[self.mode]

    @glow_progress.setter
    def glow_progress(self, progress: GlowProgress) -> None:
        self._glow_profiles[self.mode] = progress

    def _reset_room(self) -> None:
        """Everything that belongs to one loaded room, perception memory included."""
        self.entities: dict[str, Entity] = {}
        self.revision = 0
        self.receipts: dict[tuple[str, str], Receipt] = {}  # durable, at most max_durable_receipts
        self.transient: OrderedDict[tuple[str, str], Receipt] = OrderedDict()
        self.compacted: OrderedDict[tuple[str, str], Compacted] = OrderedDict()
        self.terminal: dict[tuple[str, str], Receipt] = {}  # denied or lapsed approvals, replayed as-is
        self.approvals: dict[str, Approval] = {}
        self.approval_by_action: dict[tuple[str, str], str] = {}
        self.goals: dict[str, dict] = {}
        self.jobs: OrderedDict[str, dict] = OrderedDict()  # goal jobs for jobs.status, oldest first
        self.holding: dict[str, str] = {}
        self.facing: dict[str, tuple[float, float]] = {}  # avatar -> the way it faces on the floor (x, z); -Z unless set
        self.checkpoints: list[dict] = []
        self.history: dict[int, dict] = {}
        self.committed_by: dict[int, str] = {}  # revision -> the principal whose command made it
        # Perception memory, per companion principal. In memory only: never in a snapshot, a receipt or a save.
        self.memory: dict[str, PerceptionMemory] = {}
        self._last_sweep = self.clock.now()
        self._reset_journal()
        self._reset_trust_session()
        self._reset_inventory()
        self._creation_counter = 0
        # The running effects, oldest first. Effects are not entities: no query lists them (as on the kernel host).
        self.effects: OrderedDict[str, ActiveEffect] = OrderedDict()
        # The bubble (at most one), and the ghost previews of carries on the player. In memory only, like effects.
        self.bubbles: OrderedDict[str, MockBubble] = OrderedDict()
        self.bubble_ghosts: OrderedDict[str, tuple] = OrderedDict()
        self._approving = False
        self._checkpoint_counter = 0
        self._load_room()
        # The island's nature (mock_nature.py): its plants, the fire, the campfires, the weather and the undo steps.
        self._reset_nature()
        self.history[0] = self._snapshot()

    # ------------------------------------------------------------------ room loading

    def _index_styles(self, styles_dir: Path) -> dict[tuple[str, int], str]:
        found: dict[tuple[str, int], str] = {}
        if styles_dir.is_dir():
            for path in sorted(styles_dir.glob("*/v*.json")):
                try:
                    preset = self.contracts.validate.load_strict(path)
                    key = (preset["preset_id"], int(preset["preset_version"]))
                except Exception:  # an unreadable preset is simply not offered
                    continue
                found[key] = hashlib.sha256(path.read_bytes()).hexdigest()
                self.style_status[key] = preset.get("status", "seed")
        return found

    def _load_room(self) -> None:
        load = self.contracts.validate.load_strict
        room = load(self.room_dir / "room.json")
        problems = self.contracts.validate.schema_errors(room)
        if problems:
            raise ValueError(f"room {self.room_dir} does not validate: {problems[:3]}")
        self.room = room
        self.room_id = room["room_id"]
        self.room_bounds = room["bounds"]
        provenance = "hand_authored" if room["source"]["kind"] == "hand_built" else (
            "procedural" if room["source"]["kind"] == "procedural" else "captured_scanned")
        for part in room["shell"]["parts"]:
            geometry = part["geometry"]
            bounds = None
            if geometry["kind"] == "polygon":
                pts = geometry["points_m"]
                bounds = {"min_m": [_r(min(p[i] for p in pts)) for i in range(3)],
                          "max_m": [_r(max(p[i] for p in pts)) for i in range(3)]}
            centre = [(bounds["min_m"][i] + bounds["max_m"][i]) / 2 for i in range(3)] if bounds else [0.0, 0.0, 0.0]
            self.entities[part["id"]] = Entity(
                id=part["id"], kind="shell", display_name=part["role"].replace("_", " ").capitalize(),
                position=centre, half_extents=[0, 0, 0], affordances=["walkable_top"] if part["role"] in ("floor", "platform") else [],
                movable=False, provenance_kind=provenance, explicit_bounds=bounds,
                category=part["role"], category_group="structure",
                materials=[part["material_role"]] if part.get("material_role") else [])
        assets: dict[str, dict] = {}
        for instance in room["objects"]:
            if instance["asset"] not in assets:
                assets[instance["asset"]] = load(self.room_dir / instance["asset"])
            asset = assets[instance["asset"]]
            dims = asset["dimensions_m"]
            scale = instance["transform"].get("scale", 1.0)
            self.entities[instance["id"]] = Entity(
                id=instance["id"], kind="object",
                display_name=instance.get("display_name", asset["display_name"]),
                position=list(instance["transform"]["position_m"]),
                half_extents=[dims[0] * scale / 2, dims[1] * scale, dims[2] * scale / 2],
                affordances=list(asset["affordances"]), movable=bool(asset["physics"]["movable"]),
                provenance_kind=asset["provenance"]["kind"], category=asset.get("category"),
                category_group=asset.get("category_group"), mass_kg=asset["physics"].get("mass_kg"),
                parts={p["id"]: float(p.get("default", 0.0)) for p in asset.get("parts", [])},
                materials=[m["role"] for m in asset.get("materials", []) if isinstance(m, dict) and m.get("role")],
            )
        spawns = {s["role"]: s for s in room["spawns"]}
        player_spawn = spawns.get("player", {"position_m": [0.0, 0.0, 0.0]})
        companion_spawn = spawns.get("companion", player_spawn)
        self.entities["avatar:player"] = Entity(
            id="avatar:player", kind="avatar", display_name="Player", position=list(player_spawn["position_m"]),
            half_extents=list(_BODY), affordances=[], movable=False, provenance_kind="hand_authored")
        for offset, (principal, avatar) in enumerate(sorted((p, a) for p, a in self.avatars.items() if p != PLAYER)):
            position = list(companion_spawn["position_m"])
            position[0] += 0.15 * offset
            position[1] = _r(position[1] + HOVER_M)
            name = "the Gubble" if avatar == "avatar:companion" else avatar.split(":", 1)[1].capitalize()
            self.entities[avatar] = Entity(
                id=avatar, kind="avatar", display_name=name, position=position,
                half_extents=list(_BODY), affordances=[], movable=False, provenance_kind="hand_authored")
        style = room.get("default_style")
        if style is None:
            key = next(iter(sorted(self.styles)), ("storybook_painterly", 1))
            style = {"preset_id": key[0], "preset_version": key[1], "preset_sha256": self.styles.get(key, "0" * 64)}
        self.style = dict(style)
        self.physics_preset = "room_tuned"

    # ------------------------------------------------------------------ world fixtures (game side, not the wire)

    def add_world_text(self, entity_id: str, text: str) -> None:
        """A label or sign the companion can read. Untrusted: it may say anything."""
        with self._lock:
            self.entities[entity_id].texts.append(text)

    def rename_entity(self, entity_id: str, name: str) -> None:
        """A player-chosen or captured name. Untrusted: it may say anything."""
        with self._lock:
            # Match CompanionAvatar.SavedName for a former default supplied by a game-side fixture.
            # The mock has no saved companion profile reader; other names remain the player's choice.
            if entity_id == "avatar:companion" and name == "Wisp":
                name = "the Gubble"
            self.entities[entity_id].display_name = name

    def move_avatar(self, avatar_id: str, position: list[float]) -> None:
        """The avatar walks there, carrying whatever it holds."""
        with self._lock:
            self._maybe_sweep()  # what the avatars saw where they stood while the time passed
            self.entities[avatar_id].position = list(position)
            self._carry(avatar_id)

    def move_entity(self, entity_id: str, position: list[float]) -> None:
        """The world moving something by itself (physics, the player's hands) between requests."""
        with self._lock:
            self._maybe_sweep()
            self.entities[entity_id].position = list(position)

    def load_room(self, room_dir: Path) -> None:
        """The game switches rooms: a fresh room state, and every companion's perception memory goes."""
        with self._lock:
            self.room_dir = Path(room_dir)
            self._reset_room()

    def load_rules(self, path: Path) -> None:
        """Load the island's rules from a pack file (the kernel's LoadRules). A pack that fails leaves no abilities,
        never a crash, and says why in rules_notice."""
        try:
            rules, notice = load_rules(self.contracts, path), ""
        except RulesError as error:
            rules, notice = None, f"The island's rules did not load, so the Gubble has no abilities: {error}."
        self.use_rules(rules, notice)

    def use_rules(self, rules: IslandRules | dict | None, notice: str = "") -> None:
        """Use these rules (a checked IslandRules, or a pack to check; None for none). Effects started under the old
        rules end (the kernel's UseRules)."""
        with self._lock:
            if isinstance(rules, dict):
                rules = parse_rules(self.contracts, rules)
            self._end_effects(list(self.effects.values()))
            for bubble in list(self.bubbles.values()):
                self._end_bubble(bubble, "popped")
            fresh = self._fresh_vitals()
            # The materials wallet is retired (P-Inventory-2): new rules bring none (the kernel's UseRules).
            self.rules = rules
            self.rules_notice = "" if rules is not None else (notice or "This island has no rules, so the Gubble has no abilities.")
            for mode, profile in self._vitals_profiles.items():
                if profile == fresh:
                    self._vitals_profiles[mode] = self._fresh_vitals()

    def _fresh_vitals(self) -> dict:
        """A new body under the island's rules (the kernel's VitalsRules.Fresh): every heart, the first band full."""
        rules = getattr(self, "rules", None)
        hearts = rules.hearts if rules else None
        health = float(hearts[1] * 4 * hearts[0]) if hearts else 100.0
        bar = rules.stamina_start_m if rules and rules.stamina_start_m is not None else 0.45
        return {"health": health, "max_health": health, "stamina": bar, "max_stamina": bar, "completed_climbs": 0, "recoveries": 0}

    def _player_status(self) -> dict:
        """player.status as the kernel answers it: the body and, with the pack's traits, the pool (the shadow pool in
        Creative), the hearts in quarters and the bar's bands. The mock has no body tick: health and the bar stay as they are."""
        data = dict(self._vitals_profiles[self.mode], mode=self.mode, consequences=self.mode != "creative")
        rules = self.rules
        if rules and rules.pips:
            # The pool (mock_energy.py): this mode's, the shadow pool in Creative.
            data["energy"] = {"pips": self._pool_pips(), "max_pips": self._max_pips(), "shadow": self.mode == "creative"}
        if rules and rules.hearts:
            quarter = rules.hearts[0]
            data["hearts"] = {"quarters": round(data["health"] / quarter), "max_quarters": round(data["max_health"] / quarter)}
        if rules and rules.stamina_start_m is not None:
            data["bar"] = {"bands": 1, "slowed": False}
        # P-Inventory: the pockets and the chest, with the pack's block (mock_inventory.py).
        data.update(self._inventory_status() or {})
        return data

    def active_effect_ids(self) -> list[str]:
        """The running effects' ids, oldest first (the kernel's ActiveEffectIds)."""
        with self._lock:
            self._expire_effects()
            return list(self.effects)

    def session_event(self, principal: str, event: str) -> None:
        """The link reports a companion session starting or ending ("start", "end"). Since Run 2 the team's map
        is the room's (the kernel saves it with the room), so a session no longer clears it; the hook stays."""

    # ------------------------------------------------------------------ perception

    def perceived(self, principal: str) -> set[str]:
        """Ids the principal's avatar perceives now: line of sight from its eye, plus the room shell
        (it stands inside it), its own avatar and whatever it holds. Pure: it remembers nothing."""
        with self._lock:
            avatar_id = self.avatars[principal]
            avatar = self.entities[avatar_id]
            eye = perception.eye_point(avatar.position, "player" if principal.startswith("player:") else "companion")
            occluders = self._occluders()
            seen = {avatar_id}
            for entity in self.entities.values():
                if entity.removed:
                    continue
                if entity.kind == "shell" or entity.held_by == avatar_id:
                    seen.add(entity.id)
                elif entity.id != avatar_id:
                    box = entity.bounds()
                    if perception.visible(eye, entity.id, box["min_m"], box["max_m"], occluders):
                        seen.add(entity.id)
            return seen

    def _team_principal(self) -> str:
        """The companion whose team the player is on (one team in single player)."""
        return COMPANION

    def _team_principals(self) -> list[str]:
        return [COMPANION, PLAYER]

    def _eyes(self, principal: str) -> list[str]:
        """The principals whose avatars' eyes fill this companion's map: its own, and the player's for the team."""
        if principal == self._team_principal() and self.policy.shared_sight:
            return [principal, PLAYER]
        return [principal]

    def team_sight(self, principal: str) -> set[str]:
        """What a companion has in sight now: what either of the team's avatars sees, the player always known."""
        with self._lock:
            seen = self.perceived(principal)
            if principal == self._team_principal() and self.policy.shared_sight:
                seen |= self.perceived(PLAYER)
                seen.add(self.avatars[PLAYER])
            return seen

    def sight_sweep(self) -> None:
        """The host's sight sweep (CommandHost.SightSweep): what each of the team's avatars sees now goes into the
        team's map, and a place either sees empty drops what was remembered there. It needs no request."""
        with self._lock:
            team = self._team_principal()
            seen = self.perceived(team) | self.perceived(PLAYER)
            self._look(team, seen, eyes_of=[team, PLAYER])
            self._last_sweep = self.clock.now()

    def _maybe_sweep(self, now: bool = False) -> None:
        interval = self.policy.team_sight_interval_s
        if interval > 0 and (now or self.clock.now() - self._last_sweep >= interval):
            self.sight_sweep()

    def _view_for(self, principal: str, *, for_command: bool) -> set[str] | None:
        if principal.startswith("player:"):
            return None  # the player's own controls and UI see the whole room
        seen = self.team_sight(principal)
        self._look(principal, seen)
        if for_command and not self.policy.companion_targets_need_perception:
            return None
        return seen

    # ------------------------------------------------------------------ perception memory

    def _perceivable(self, entity: Entity) -> dict:
        """What a look at the entity shows: its summary, parts and who protected it, sanitised."""
        return {"summary": entity.summary(self.text), "parts": dict(entity.parts), "protected_by": entity.protected_by}

    def _memory(self, principal: str) -> PerceptionMemory:
        memory = self.memory.get(principal)
        if memory is None or memory.room_id != self.room_id:
            memory = self.memory[principal] = PerceptionMemory(self.room_id)
        return memory

    def _look(self, principal: str, seen: set[str], eyes_of: list[str] | None = None) -> None:
        """Update a companion's map from what its team's avatars see now (`seen`, the team's sight). Only the
        avatars' eyes fill it: never the camera, another team's companion or hidden state.

        - What it remembers but nobody sees now is checked against the room: any change at all (moved, edited,
          picked up, locked or gone) marks it changed, which the companion learns only as may_be_stale.
        - If the place it was last seen is in sight of either avatar now and it is not there, the team has looked
          again: it leaves the map. Until then, a thing removed out of sight is remembered as it was.
        - What is seen now is remembered afresh, nearest last. Past the bound, routine things out of sight go
          first, least recently seen, then routine things in sight; what the team built and the targets of running
          goals and open tasks are never dropped.
        """
        limit = self.policy.perception_memory_entries
        memory = self._memory(principal)
        if limit <= 0:
            memory.entries.clear()
            return
        avatar = self.entities[self.avatars[principal]]
        eyes = [perception.eye_point(self.entities[self.avatars[p]].position,
                                     "player" if p.startswith("player:") else "companion")
                for p in (eyes_of or self._eyes(principal))]
        occluders = None
        for entity_id, entry in list(memory.entries.items()):
            if entity_id in seen:
                continue
            entity = self.entities.get(entity_id)
            if not entry.changed and (entity is None or entity.removed or self._perceivable(entity) != entry.view):
                entry.changed = True
            if occluders is None:
                occluders = self._occluders()
            box = entry.view["summary"]["bounds_m"]
            if any(perception.visible(eye, entity_id, box["min_m"], box["max_m"], occluders) for eye in eyes):
                del memory.entries[entity_id]
        now = self.clock.now()
        fresh = sorted((self.entities[e] for e in seen
                        if e not in (avatar.id, self.avatars[COMPANION]) and self.entities[e].kind in REMEMBERED_KINDS),
                       key=lambda e: (-_distance_to_box(eyes[0], e.bounds()), e.id))
        for entity in fresh:
            memory.entries.pop(entity.id, None)
            memory.entries[entity.id] = Remembered(self._perceivable(entity), now, self.revision, entity.mass_kg)
        # The bound is hard (CommandHost.EvictionOrder): routine things out of sight, routine things in sight,
        # creations out of the team's sight, creations in sight, each least recently seen first; never a task target.
        kept = self._kept_targets()
        order = [i for i in memory.entries if i not in kept]
        victims = sorted(order, key=lambda i: ((2 if i.startswith("creation:") else 0) + (1 if i in seen else 0),
                                              order.index(i)))
        for victim in victims[:max(0, len(memory.entries) - limit)]:
            del memory.entries[victim]

    def _kept_targets(self) -> set[str]:
        """The targets of running goals and open tasks: kept on the team's map whatever its bound."""
        running = {g["target"] for g in self.goals.values() if g.get("target")}
        return running | self._journal_task_targets()

    def _occluders(self) -> list:
        return [(e.id, *perception.padded(e.bounds()["min_m"], e.bounds()["max_m"]))
                for e in self.entities.values() if not e.removed and e.kind in OCCLUDING_KINDS]

    def _recall(self, principal: str, entity_id: str) -> Remembered | None:
        """The companion's memory of an entity it cannot see now; None for the player, for anything in
        sight and for anything it never saw (or has seen is gone)."""
        if principal.startswith("player:") or self._view is None or entity_id in self._view:
            return None
        memory = self.memory.get(principal)
        if memory is None or memory.room_id != self.room_id:
            return None
        return memory.entries.get(entity_id)

    def _remembered(self, principal: str) -> dict[str, Remembered]:
        """Every remembered entity out of sight now, when the contract can say so in results."""
        if not self.contracts.memory_fields or principal.startswith("player:") or self._view is None:
            return {}
        memory = self.memory.get(principal)
        if memory is None or memory.room_id != self.room_id:
            return {}
        return {entity_id: entry for entity_id, entry in memory.entries.items() if entity_id not in self._view}

    def _memory_fields(self, entry: Remembered) -> dict:
        """How a remembered entity is marked. may_be_stale is one bit: old, or changed in any way. It
        never says which, or anything about the entity now."""
        age = max(0.0, self.clock.now() - entry.seen_at)
        return {"seen": "remembered", "last_seen_ago_s": round(age, 1), "last_seen_revision": entry.seen_revision,
                "may_be_stale": entry.changed or age >= self.policy.perception_memory_stale_after_s}

    def _summary(self, principal: str, entity: Entity) -> dict:
        """An entity in sight now, as the requester sees it."""
        summary = entity.summary(self.text)
        if self.contracts.memory_fields and not principal.startswith("player:"):
            summary["seen"] = "now"
        return summary

    def _remembered_summary(self, entry: Remembered) -> dict:
        return {**copy.deepcopy(entry.view["summary"]), **self._memory_fields(entry)}

    # ------------------------------------------------------------------ the Gubble's eyes

    def set_gubble_eyes(self, allowed: bool) -> None:
        """The player's control for "Let my AI see through the Gubble's eyes" (the HUD's switch). No request reaches it."""
        with self._lock:
            self.gubble_eyes = bool(allowed)

    def _view_look(self, principal: str, args: dict) -> dict:
        """view.look in the kernel's order: the actor (only a Gubble's own eyes), the player's setting, the rate, the renderer.
        A look is a companion request like any other: its sight filled the team's map before this ran, and the picture adds
        nothing to it. The mock's picture is a placeholder (eyes.placeholder_png) with the kernel's facts."""
        companion = not principal.startswith("player:")
        gubbles = {self.avatars[principal]} if companion else {a for p, a in self.avatars.items() if not p.startswith("player:")}
        actor = args.get("actor")
        if actor not in gubbles:
            raise HostError("actor_denied", eyes.LOOK_ACTOR_MESSAGE, field_path="$.args.actor")
        if companion and not self.gubble_eyes:
            raise HostError("permission_denied", eyes.EYES_OFF_MESSAGE)
        if companion:
            bucket = self._look_buckets.setdefault(principal, eyes.LookBucket(self.policy.look_burst, self.policy.look_refill_s))
            if not bucket.take(self.clock.now()):
                raise HostError("rate_limited", eyes.LOOK_RATE_MESSAGE, retryable=True)
        if not self.policy.look_renderer:
            raise HostError("unavailable", eyes.NO_RENDERER_MESSAGE)
        yaw, pitch = float(args.get("yaw_deg", 0)), float(args.get("pitch_deg", 0))
        body = self.entities[actor]
        facing_x, facing_z = self.facing.get(actor, (0.0, -1.0))
        heading = eyes.heading(facing_x, facing_z)
        picture = eyes.placeholder_png(yaw, pitch)
        return {
            "actor": actor,
            "image": eyes.image_block(picture, "image/png", eyes.PLACEHOLDER_WIDTH_PX, eyes.PLACEHOLDER_HEIGHT_PX),
            "position_m": [_r(v) for v in body.position],
            "eye_m": [_r(v) for v in perception.eye_point(body.position, "companion")],
            "heading_deg": heading,
            "view_heading_deg": eyes.round1(eyes.positive_degrees(heading + yaw)) % 360.0,
            "view_pitch_deg": eyes.round1(pitch),
            "fov_deg": eyes.horizontal_fov_deg(),
        }

    # ------------------------------------------------------------------ the wire

    def handle(self, principal: str, message: Any) -> dict:
        """Answer one parsed message from `principal`, which the trusted transport assigned."""
        if principal not in self.avatars:
            raise ValueError(f"the transport passed an unknown principal {principal!r}")
        with self._lock:
            # The rate limit comes before every other check, so a flood of invalid messages is limited too.
            if not _is_stop(message) and not self._take_token(principal):
                return self._rate_limited(principal)
            # The host's own clock first, as the kernel's frames run it between requests: the pool's refill and nature's steps.
            self._refill_pips()
            self._advance_nature()
            self._expire()
            # Trust's clock (the kernel's SettleTrust, which the companion bridge runs each frame): windows closed by now settle.
            self._settle_trust()
            # The sweep on the host's clock; a player's own request is the player looking, so it always sweeps.
            self._maybe_sweep(now=principal.startswith("player:"))
            try:
                result = self._handle(principal, message)
            except HostError as error:
                result = self._fail(principal, message, error)
            except RecursionError:
                result = self._fail(principal, None, HostError("request_invalid", "The request is nested too deeply."))
            except Exception:  # never let a bug leak a stack trace to the requester
                log.exception("mock host failed on a request")
                result = self._fail(principal, message, HostError(
                    "internal_error", "The game could not handle that request.", retryable=True))
            finally:
                self._view = None
            problems = self.contracts.schema_errors(result)
            if problems:
                log.error("mock host built an invalid result: %s", problems[:3])
                result = self._fail(principal, None, HostError(
                    "internal_error", "The game could not handle that request.", retryable=True))
            return result

    def handle_bytes(self, principal: str, raw: bytes) -> dict:
        """Parse strictly (no duplicate keys, NaN or overflow), then answer. As in the kernel host, a
        companion's message counts against its rate limit before it is parsed, and with no budget left
        only text that may be a stop is parsed at all."""
        if principal not in self.avatars:
            raise ValueError(f"the transport passed an unknown principal {principal!r}")
        with self._lock:
            if not self._tokens_left(principal) and not _might_be_stop(raw):
                return self._rate_limited(principal)
        try:
            message = self.contracts.loads_strict(raw.decode("utf-8"))
        except (UnicodeDecodeError, self.contracts.ContractError, ValueError, RecursionError):
            # ValueError: an integer literal longer than Python converts; RecursionError: absurd nesting.
            with self._lock:
                if not self._take_token(principal):
                    return self._rate_limited(principal)
                return self._fail(principal, None, HostError(
                    "request_invalid", "The request is not valid JSON (duplicate keys and non-finite numbers are refused)."))
        return self.handle(principal, message)

    # ------------------------------------------------------------------ the companion's rate limit

    def _tokens_left(self, principal: str) -> bool:
        if principal.startswith("player:"):
            return True  # the player's own controls are never limited
        calls = self._calls.setdefault(principal, deque())
        now = self.clock.now()
        while calls and now - calls[0] > 1.0:
            calls.popleft()
        return len(calls) < self.policy.companion_messages_per_s

    def _take_token(self, principal: str) -> bool:
        if not self._tokens_left(principal):
            return False
        if not principal.startswith("player:"):
            self._calls[principal].append(self.clock.now())
        return True

    def _rate_limited(self, principal: str) -> dict:
        # Refused before parsing, so nothing of the message is echoed (op "invalid", no action or query id).
        return self._fail(principal, None, HostError("rate_limited", "Too many messages; wait a moment and try again.",
                                                     retryable=True))

    # ------------------------------------------------------------------ dispatch

    def _handle(self, principal: str, message: Any) -> dict:
        if not isinstance(message, dict) or ids.kind_of(message.get("schema")) not in ("command", "query"):
            raise HostError("request_invalid", f"Send a {COMMAND_SCHEMA} or a {QUERY_SCHEMA}.", field_path="$.schema")
        is_command = ids.is_schema(message["schema"], "command")
        op = message.get("op") if isinstance(message.get("op"), str) else None
        self._check_schema(message)
        if message["room_id"] != self.room_id:
            raise HostError("room_mismatch", "That room is not the one loaded in the game.", field_path="$.room_id")
        hit = _find_authority_key(message.get("args"), ["args"])
        if hit is not None:
            raise forbidden_key_error(hit, "authority" if is_authority_key(str(hit[-1])) else "key_format")
        if op in self.contracts.player_only_ops and not principal.startswith("player:"):
            raise HostError("permission_denied", "Only the player can do that, directly in the game.", field_path="$.op")
        if is_command and not principal.startswith("player:") and names_a_fractal(message):
            # The Gubble may find a Fractal and go to it, never gather it: refused by the id's namespace before anything is
            # looked up, so the answer is the same whether or not it exists (the kernel's NamesAFractal).
            raise HostError("permission_denied", FRACTAL_REFUSAL, field_path="$.args.target")
        if is_command:
            self._view = self._view_for(principal, for_command=True)
            return self._command(principal, message)
        self._view = self._view_for(principal, for_command=False)
        return self._query(principal, message)

    def _check_schema(self, message: dict) -> None:
        problems = value_problems(message)
        if ids.is_schema(message.get("schema"), "command") and message.get("op") in STOP_OPS:
            # A stop ignores revisions, so a revision outside int64 is no reason to refuse it.
            problems = [(path, kind) for path, kind in problems
                        if not (kind == "integer_range" and path[:1] in (["expected_revision"], ["expected_entities"]))]
        if problems:
            raise value_error(problems)
        try:
            canonical_size = len(self.contracts.canonical_bytes(message))
        except CanonicalJsonError:
            raise HostError("request_invalid", "The request cannot be written as canonical JSON (too deeply nested).") from None
        limit = self.contracts.message_limits[message["schema"]]
        if canonical_size > limit:
            raise HostError("request_invalid", f"The request is larger than {limit} bytes of canonical JSON.",
                            field_path="$", allowed=limit, actual=canonical_size)
        errors = list(self.contracts.iter_errors(message))
        if errors:
            error = map_schema_errors(errors)
            if ids.is_schema(message.get("schema"), "command") and message.get("op") == "effect.start":
                error = effect_args_error(error)
            if ids.is_schema(message.get("schema"), "command") and message.get("op") in NATURE_OPS:
                error = nature_args_error(error)
            if ids.is_schema(message.get("schema"), "command"):
                error = lead_args_error(error, message)
            raise error
        for path, source in creation_sources(message):
            size = len(self.contracts.canonical_bytes(source))
            if size > self.contracts.creation_source_limit:
                raise HostError("budget_exceeded", "The creation is larger than the size limit.",
                                field_path=path, allowed=self.contracts.creation_source_limit, actual=size)

    # ------------------------------------------------------------------ results

    def _base(self, principal: str, message: Any) -> dict:
        return base_result(principal=principal, room_id=self.room_id, revision=self.revision,
                           at_utc=utc(self.clock.now()), message=message, known_ops=self._known_ops)

    def _fail(self, principal: str, message: Any, error: HostError) -> dict:
        return failure(self._base(principal, message), error)

    def _emit(self, kind: str, payload: dict) -> None:
        for listener in list(self.listeners):
            try:
                listener(kind, payload)
            except Exception:
                log.exception("listener failed")

    # ------------------------------------------------------------------ commands

    def _fingerprint(self, message: dict) -> str:
        """SHA-256 of the command exactly as received, in canonical JSON v1 (the contract's rule and the
        kernel host's form, see canonical.py). Nothing is normalised first: `"preview": false` or
        `"preview": true` is part of the content, so a retry that only adds either is another command."""
        return hashlib.sha256(self.contracts.canonical_bytes(message)).hexdigest()

    def _command(self, principal: str, message: dict) -> dict:
        op = message["op"]
        action_id = message["action_id"]
        key = (principal, action_id)
        handler = getattr(self, "_op_" + op.replace(".", "_"))
        preview = message.get("preview") is True
        fingerprint = self._fingerprint(message)
        if op in STOP_OPS:
            # A stop always applies (or, as a preview, predicts), whatever the ledger or an earlier use of
            # this action id says: it never replays and never conflicts.
            if not preview:
                result = self._commit(principal, message, handler)
                self._store_receipt(key, fingerprint, result)
                return result
        else:
            earlier = self._earlier(key, fingerprint, message)
            if earlier is not None:
                return earlier
            self._check_unsupported(message)
            self._check_revisions(principal, message)
            self._check_nature_remains(principal, message)
        prediction = handler(principal, message, False, None)
        # The host's own ask (the kernel's Hold from inside a handler: Creative's one ask before a fire burns a build, the Gubble's
        # undo of another's change or repair of the player's build, a keyed_yes nature cast): whoever sent it, never on a preview.
        ask = prediction.pop("_ask", None)
        if preview:
            result = self._base(principal, message)
            result["preview"] = True
            result.update(prediction)
            result.pop("transient", None)
            return result
        if ask is not None or self._policy_holds(principal, message):
            return self._hold(principal, message, fingerprint, prediction, ask)
        if not self._transient(message) and op != "room.checkpoint":
            self._check_receipt_room(principal)
        result = self._commit(principal, message, handler)
        self._store_receipt(key, fingerprint, result)
        return result

    def _earlier(self, key: tuple[str, str], fingerprint: str, message: dict) -> dict | None:
        """The answer for an action id this principal already used, or None if it is new."""
        # Durable first, then compacted, then transient: a stop under a used action id keeps a transient
        # receipt there, which must never hide the durable one, compacted or not (the kernel host's P2).
        if key in self.receipts:
            return self._replay(self.receipts[key], fingerprint, message)
        if key in self.compacted:
            entry = self.compacted[key]
            if entry.fingerprint_prefix != fingerprint[:COMPACTED_PREFIX_HEX]:
                raise HostError("action_id_conflict", "That action id was already used for a different command.",
                                field_path="$.action_id")
            replay = self._base(key[0], message)
            replay.update({"replayed": True, "revision": entry.revision, "transient": False})
            if message["op"] == "room.checkpoint":
                replay["data"] = {"checkpoint_revision": entry.revision}  # required on every committed answer
            return replay
        for store in (self.transient, self.terminal):
            if key in store:
                return self._replay(store[key], fingerprint, message)
        request_id = self.approval_by_action.get(key)
        if request_id is not None:
            approval = self.approvals[request_id]
            if approval.fingerprint != fingerprint:
                raise HostError("action_id_conflict", "That action id was already used for a different command.",
                                field_path="$.action_id")
            replay = copy.deepcopy(approval.pending_result)
            replay["replayed"] = True
            replay["at_utc"] = utc(self.clock.now())
            return replay
        return None

    def _policy_holds(self, principal: str, message: dict) -> bool:
        """Whether the policy's held set, or an ability of tier keyed_yes, holds a companion's command for the player's click."""
        return principal != PLAYER and not self._approving and (message["op"] in self.policy.companion_approval_ops or self._keyed_yes(message))

    def _transient(self, message: dict) -> bool:
        """A command with a transient receipt that moves no room revision: the transient ops, a bubble, and Snuff (the kernel's
        FireTransient: a simulation change, as X's)."""
        return message["op"] in TRANSIENT_OPS or _is_bubble(message) or self._is_snuff(message)

    def _commits_now(self, principal: str, message: dict) -> bool:
        """Whether this command goes on to commit now (no preview, no hold for the player's click): where the guard runs."""
        return message.get("preview") is not True and not self._policy_holds(principal, message)

    def _keyed_yes(self, message: dict) -> bool:
        """An ability of tier keyed_yes (T3) waits for the player's yes when a companion asks, whatever the policy's
        held set: the island's rules say so (the kernel's NeedsApproval)."""
        if self.rules is None:
            return False
        if _is_bubble(message):
            ability = self._bubbles_ability()
            return ability is not None and ability.tier == "keyed_yes"
        if message["op"] != "effect.start":
            return False
        ability = self.rules.ability(message["args"].get("capability"))
        return ability is not None and ability.tier == "keyed_yes"

    def _commit(self, principal: str, message: dict, handler, approved_by: str | None = None) -> dict:
        op = message["op"]
        durable = not self._transient(message)
        moves_revision = durable and op not in NO_REVISION_OPS
        new_revision = self.revision + 1 if moves_revision else None
        # The journal's facts about creations (built, changed, removed), from the team's knowledge, dated at the revision
        # the command commits. A change or a removal is judged before it commits: the thing as the team knows it now.
        target = message["args"].get("target", "") if op in ("creation.revise", "entity.remove") else ""
        known = self.entities.get(target)
        before = self._team_view(target) if known is not None and known.kind == "creation" else None
        outcome = handler(principal, message, True, new_revision)
        if op == "creation.place" and outcome.get("created"):
            # A new thing is known to the team only if either avatar's eyes reach where it stands.
            made = self.entities[outcome["created"][0]]
            box = made.bounds()
            view = (made.summary(self.text)["display_name"], [(box["min_m"][i] + box["max_m"][i]) / 2 for i in range(3)]) \
                if self._team_sees_place(box, made.id) else None
            self._creation_fact(op, principal, approved_by, made.id, view, new_revision)
        elif known is not None and known.kind == "creation":
            self._creation_fact(op, principal, approved_by, target, before, new_revision)
        if moves_revision:
            self.revision = new_revision
            self.history[new_revision] = self._snapshot()
            self.committed_by[new_revision] = principal
            for old in [r for r in self.history if r < new_revision - self.policy.history_depth]:
                del self.history[old]
                self.committed_by.pop(old, None)
        result = self._base(principal, message)
        result.update(outcome)
        result["transient"] = not durable
        if approved_by:
            result["approved_by"] = approved_by
        self._emit("committed", {"principal": principal, "op": op, "action_id": message["action_id"]})
        # M8: a command of the Gubble's that committed opens its Trust window and can be marked (mock_trust.py).
        self._observe_trust(principal, message, result)
        return result

    def _check_receipt_room(self, principal: str) -> None:
        limit = self.policy.max_durable_receipts
        if not principal.startswith("player:"):
            limit = max(0, limit - self.policy.player_receipt_reserve)
        if len(self.receipts) >= limit:
            raise HostError("receipt_limit", "The receipt ledger is full. A checkpoint compacts it.", retryable=True)

    def _store_receipt(self, key: tuple[str, str], fingerprint: str, result: dict) -> None:
        receipt = Receipt(fingerprint, copy.deepcopy(result), durable=not result.get("transient", False))
        if receipt.durable:
            self.receipts[key] = receipt
            return
        self.transient.pop(key, None)
        self.transient[key] = receipt
        mine = [k for k in self.transient if k[0] == key[0]]
        for old in mine[:max(0, len(mine) - self.policy.max_transient_receipts_per_principal)]:
            del self.transient[old]

    def _replay(self, receipt: Receipt, fingerprint: str, message: dict) -> dict:
        if receipt.fingerprint != fingerprint:
            raise HostError("action_id_conflict", "That action id was already used for a different command.",
                            field_path="$.action_id")
        replay = copy.deepcopy(receipt.result)
        replay["replayed"] = True
        return replay

    def _check_unsupported(self, message: dict) -> None:
        """What the kernel refuses before it looks at revisions (CommandHost.Unsupported), where the mock follows it."""
        if message["op"] in MECHANICS_PLAYER_OPS - MECHANICS_HOSTED:
            raise HostError("unsupported_capability", MECHANICS_NOT_YET, field_path="$.op")
        if message["op"] == "world.set_mode" and "difficulty" in message["args"]:
            raise HostError("unsupported_capability", DIFFICULTY_NOT_YET, field_path="$.op")
        if message["op"] == "room.undo" and self.mode != "creative":
            raise HostError("unsupported_capability", UNDO_NOT_IN_THIS_MODE, field_path="$.op")

    def _check_revisions(self, principal: str, message: dict) -> None:
        op = message["op"]
        if op in STOP_OPS:
            return
        if "expected_revision" in message and message["expected_revision"] != self.revision:
            raise HostError("revision_conflict", "The room changed since you looked. Observe again and retry.",
                            field_path="$.expected_revision", retryable=True,
                            allowed=self.revision, actual=message["expected_revision"])
        expected = message.get("expected_entities", {})
        for entity_id in sorted(expected):
            entity = self._entity(entity_id)
            if entity is None:
                raise _not_found("$.expected_entities")
            if entity.revision != expected[entity_id]:
                raise HostError("revision_conflict", "An entity changed since you looked. Observe again and retry.",
                                field_path=textsafety.field_path(["expected_entities", entity_id]), retryable=True,
                                allowed=entity.revision, actual=expected[entity_id])
        if op in self.contracts.destructive_ops and "expected_revision" not in message:
            for target in _targets(message["args"]):
                if target not in expected:
                    raise HostError("request_invalid",
                                    "A destructive command must name every target in expected_entities, or name expected_revision.",
                                    field_path="$.expected_entities")

    def _hold(self, principal: str, message: dict, fingerprint: str, prediction: dict, ask: dict | None = None) -> dict:
        """Holds a command for the player's click: the policy's (its reason stated as its concrete effect, lapsing when what it
        touches changes) or the host's own ask (its reason and what it touches its handler's, as the kernel's Hold)."""
        pending = [a for a in self.approvals.values() if a.principal == principal and a.state == "pending"]
        if len(pending) >= self.policy.max_pending_approvals:
            raise HostError("rate_limited", "Several changes are already waiting for the player.", retryable=True)
        request_id = secrets.token_hex(16)
        expires_at = self.clock.now() + self.policy.approval_ttl_s
        touched = {}
        if ask is not None:
            touched = dict(ask.get("touched", {}))
        for entity_id in _targets(message["args"]) + list(prediction.get("affected", [])):
            entity = self._entity(entity_id)
            if entity is not None and ask is None:
                touched[entity_id] = entity.revision
        result = self._base(principal, message)
        result["ok"] = False
        result["approval_needed"] = {
            "request_id": request_id,
            "reason": ask["reason"] if ask is not None else self._approval_reason(message),
            "expires_utc": utc(expires_at),
        }
        result["error"] = {"code": "approval_required", "message": "Waiting for the player to approve this change in the game.",
                           "retryable": True}
        approval = Approval(request_id, principal, message["action_id"], fingerprint, copy.deepcopy(message),
                            expires_at, touched, self.revision, copy.deepcopy(result),
                            bind_revision=bool(ask and ask.get("bind_revision")), spares_on_deny=bool(ask and ask.get("spares_on_deny")))
        self.approvals[request_id] = approval
        self.approval_by_action[(principal, message["action_id"])] = request_id
        self._emit("approval_requested", {"request_id": request_id, "op": message["op"], "principal": principal,
                                          "reason": result["approval_needed"]["reason"]})
        return result

    def _approval_reason(self, message: dict) -> str:
        """What the player is asked to approve, stated as its concrete effect."""
        op, args = message["op"], message["args"]

        def named(entity_id: str) -> str:
            entity = self.entities.get(entity_id)
            name = self.text.display_text(entity.display_name, 40) if entity else "unknown"
            revision = entity.revision if entity else 0
            return f'{entity_id} ("{name}", revision {revision})'

        if op == "effect.start":
            # The prompt names the capability token only, as the kernel host's does.
            return f"The companion asks to use {args['capability']}. Approve or deny it in the game."
        if op == "entity.remove":
            what = f"remove {named(args['target'])} from the room"
        elif op == "entity.transform":
            new_name = self.text.display_text(args["into"]["source"]["name"], 40)
            what = f'turn {named(args["target"])} into "{new_name}"'
        elif op == "creation.revise":
            parts = []
            if "source" in args:
                parts.append(f'rebuild it as "{self.text.display_text(args["source"]["name"], 40)}"')
            if "placement" in args:
                x, y, z = (round(v, 2) for v in args["placement"]["position_m"])
                parts.append(f"move it to ({x}, {y}, {z})")
            what = f"change {named(args['target'])}: " + " and ".join(parts)
        elif op == "style.set":
            what = (f"switch the room style from {self.style['preset_id']} v{self.style['preset_version']} "
                    f"to {args['preset_id']} v{args['preset_version']}")
        else:
            targets = ", ".join(_targets(args)) or "the room"
            what = f"{op} on {targets}"
        reason = f"Your companion wants to {what}. Approve or deny in the game."
        return reason if len(reason) <= 280 else reason[:276] + "..."

    # ------------------------------------------------------------------ the player's side (game UI only)

    def pending_approvals(self) -> list[dict]:
        with self._lock:
            self._expire()
            return [{"request_id": a.request_id, "principal": a.principal, "op": a.message["op"],
                     "reason": a.pending_result["approval_needed"]["reason"], "expires_utc": utc(a.expires_at)}
                    for a in self.approvals.values() if a.state == "pending"]

    def player_decide(self, request_id: str, approve: bool) -> dict:
        """The player's click in the game UI. Never reachable from the companion's connection."""
        with self._lock:
            self._advance_nature()
            self._expire()
            approval = self.approvals.get(request_id)
            if approval is None:
                raise KeyError("no such approval request")
            if approval.state != "pending":
                return {"state": approval.state, "result": approval.result}
            message = approval.message
            key = (approval.principal, approval.action_id)
            self._view = None
            try:
                if not approve and not approval.spares_on_deny:
                    self._finish(approval, "denied", HostError(
                        "permission_denied", "The player declined this change. To ask again, use a new action_id."))
                elif self._lapsed(approval):
                    self._finish(approval, "expired", HostError(
                        "approval_mismatch", "The room changed before the player approved. To ask again, use a new action_id."))
                elif approve:
                    try:
                        # The player's own held change (a fire near a build) needed the player's click: no one else's approval.
                        result = self._commit_held(approval, approved_by=PLAYER if approval.principal != PLAYER else None)
                        approval.state, approval.result = "approved", result
                    except HostError as error:
                        self._finish(approval, "expired", error if error.code in ("receipt_limit", "energy_exhausted") else HostError(
                            "approval_mismatch", "The change can no longer be made as approved. To ask again, use a new action_id."))
                else:
                    # The player's no to Creative's one ask (the kernel's Deny with SparesOnDeny): the fire is lit and stops at the
                    # builds, under its own principal, with no approval recorded; if it can no longer be made, it is declined.
                    try:
                        self._sparing = True
                        result = self._commit_held(approval, approved_by=None)
                        approval.state, approval.result = "denied", result
                    except HostError:
                        self._finish(approval, "denied", HostError(
                            "permission_denied", "The player declined this change. To ask again, use a new action_id."))
                    finally:
                        self._sparing = False
            finally:
                self._view = None
            del self.approval_by_action[key]
            self._emit("approval_decided", {"request_id": request_id, "state": approval.state})
            return {"state": approval.state, "result": approval.result}

    def _commit_held(self, approval: Approval, approved_by: str | None) -> dict:
        """A held command made at the player's word: every check again on the room as it is (the kernel re-executes it), then the
        commit under its own principal and action id."""
        message = approval.message
        self._check_revisions(approval.principal, message)
        self._check_nature_remains(approval.principal, message)
        handler = getattr(self, "_op_" + message["op"].replace(".", "_"))
        self._approving = approved_by is not None or approval.principal == PLAYER and not self._sparing
        try:
            handler(approval.principal, message, False, None)
            if not self._transient(message):
                self._check_receipt_room(approval.principal)
            result = self._commit(approval.principal, message, handler, approved_by=approved_by)
        finally:
            self._approving = False
        self._store_receipt((approval.principal, approval.action_id), approval.fingerprint, result)
        return result

    def player_command(self, message: dict) -> dict:
        """A manual control in the game: the same command path, under the player's principal."""
        return self.handle(PLAYER, message)

    def _finish(self, approval: Approval, state: str, error: HostError) -> None:
        result = self._fail(approval.principal, approval.message, error)
        approval.state, approval.result = state, result
        key = (approval.principal, approval.action_id)
        self.terminal[key] = Receipt(approval.fingerprint, copy.deepcopy(result), durable=False)

    def _lapsed(self, approval: Approval) -> bool:
        if (approval.bind_revision or "expected_revision" in approval.message) and self.revision != approval.room_revision:
            return True
        for entity_id, revision in approval.touched.items():
            entity = self._entity(entity_id)
            if entity is None or entity.revision != revision:
                return True
        return False

    def _expire(self) -> None:
        """Expiry, and a lapse as soon as what a request touches changes (as the kernel host's Refresh): the
        player is never offered, and approval.status never reports pending for, a request that is stale."""
        now = self.clock.now()
        view, self._view = self._view, None
        try:
            for approval in list(self.approvals.values()):
                if approval.state != "pending":
                    continue
                if now >= approval.expires_at:
                    self._finish(approval, "expired", HostError(
                        "approval_expired", "The player did not answer in time. To ask again, use a new action_id."))
                elif self._lapsed(approval):
                    self._finish(approval, "expired", HostError(
                        "approval_mismatch", "The room changed before the player approved. To ask again, use a new action_id."))
                else:
                    continue
                self.approval_by_action.pop((approval.principal, approval.action_id), None)
                self._emit("approval_decided", {"request_id": approval.request_id, "state": approval.state})
        finally:
            self._view = view
        self._expire_effects()
        self._expire_bubbles()

    # ------------------------------------------------------------------ helpers

    def _entity(self, entity_id: str) -> Entity | None:
        entity = self.entities.get(entity_id)
        if entity is None or entity.removed:
            return None
        if self._view is not None and entity_id not in self._view:
            return None  # out of sight: indistinguishable from an id that does not exist
        return entity

    def _require(self, entity_id: str, field_path: str) -> Entity:
        entity = self._entity(entity_id)
        if entity is None:
            raise _not_found(field_path)
        return entity

    def _directable(self, principal: str) -> set[str]:
        """Avatars this principal may direct: its own, and the player may direct every companion."""
        own = {self.avatars[principal]}
        if principal.startswith("player:"):
            own |= {a for p, a in self.avatars.items() if not p.startswith("player:")}
        return own

    def _principal_of(self, avatar_id: str) -> str | None:
        return next((p for p, a in self.avatars.items() if a == avatar_id), None)

    def _actor(self, principal: str, args: dict) -> str:
        actor = args.get("actor", self.avatars[principal])
        if actor not in self._directable(principal):
            raise HostError("actor_denied", "A companion acts and observes only through its own avatar.",
                            field_path="$.args.actor")
        if self.entities.get(actor) is None:
            raise _not_found("$.args.actor")
        return actor

    def _check_placement(self, placement: dict | None, field_path: str) -> None:
        if not placement:
            return
        if not _inside(placement["position_m"], self.room_bounds):
            raise HostError("out_of_bounds", "That position is outside the room.", field_path=field_path + ".position_m")
        if "on" in placement:
            support = self._require(placement["on"], field_path + ".on")
            if "walkable_top" not in support.affordances:
                raise HostError("invalid_args", "Things can only be placed on surfaces with a walkable top.",
                                field_path=field_path + ".on")
        rotation = placement.get("rotation")
        if rotation is not None and abs(math.sqrt(sum(c * c for c in rotation)) - 1.0) > 1e-3:
            raise HostError("invalid_args", "A rotation must be a unit quaternion.", field_path=field_path + ".rotation")

    def _check_changeable(self, entity: Entity, field_path: str, what: str = "changed") -> None:
        if entity.kind in ("shell", "avatar", "effect"):
            raise HostError("permission_denied", f"That cannot be {what}.", field_path=field_path)
        if entity.protected:
            raise HostError("target_protected", "That is protected. Only the player can unlock it.", field_path=field_path)

    def _touch(self, entity: Entity, new_revision: int) -> None:
        entity.revision = new_revision

    def _snapshot(self) -> dict:
        return {"entities": {k: e.durable_state() for k, e in self.entities.items() if e.kind not in ("effect", "avatar")},
                "style": dict(self.style)}

    # ------------------------------------------------------------------ op handlers
    # Each handler validates first. With apply=False it only predicts the outcome; with apply=True it
    # changes the world. Durable ops receive the new room revision to stamp on what they touch.

    def _grab_problem(self, actor: str, target_id: str, kind: str, movable: bool, protected: bool,
                      held_by: str | None, mass_kg: float | None) -> HostError | None:
        """entity.grab's checks, which a fetch applies when it is set (on what the companion sees or remembers) and
        again when it picks the thing up (on the thing as it is then). None when the avatar may pick it up."""
        if not movable or kind not in ("object", "creation"):
            return HostError("permission_denied", "That cannot be picked up.", field_path="$.args.target")
        if protected:
            return HostError("target_protected", "That is protected. Only the player can unlock it.", field_path="$.args.target")
        if held_by and held_by != actor:
            return HostError("target_busy", "Someone else is holding that.", field_path="$.args.target")
        if self.holding.get(actor) not in (None, target_id):
            return HostError("target_busy", "The avatar is already holding something.", field_path="$.args.actor")
        limit = self.policy.carry_limit_kg.get(actor, 0.0)
        if mass_kg is not None and mass_kg > limit:
            return HostError("target_too_heavy", "That is too heavy for this avatar.", field_path="$.args.target",
                             allowed=limit, actual=mass_kg)
        return None

    def _take_hold(self, actor: str, target: Entity) -> None:
        """Picking up leaves the thing where it is; from then on it moves with its holder (_carry)."""
        target.held_by = actor
        self.holding[actor] = target.id

    def _carry(self, actor: str) -> None:
        """Carry: a held thing moves with its holder (it rests where the holder stands, as a drop would leave it).
        Holding is not saved, so carrying moves no revision."""
        held = self.entities.get(self.holding.get(actor, ""))
        if held is not None and not held.removed:
            held.position = list(self.entities[actor].position)

    def _op_entity_grab(self, principal, message, apply, new_revision):
        args = message["args"]
        actor = self._actor(principal, args)
        target = self._require(args["target"], "$.args.target")
        problem = self._grab_problem(actor, target.id, target.kind, target.movable, target.protected, target.held_by,
                                     target.mass_kg)
        if problem is not None:
            raise problem
        if not apply:
            return {"affected": [target.id]}
        self._take_hold(actor, target)
        return {"affected": [target.id]}

    def _op_entity_release(self, principal, message, apply, new_revision):
        args = message["args"]
        actor = self._actor(principal, args)
        held_id = self.holding.get(actor)
        if held_id is None or self.entities.get(held_id) is None or self.entities[held_id].removed:
            raise HostError("invalid_args", "The avatar is not holding anything.", field_path="$.args")
        placement = args.get("placement")
        self._check_placement(placement, "$.args.placement")
        spot = None if placement else self._drop_spot(actor, self.entities[held_id])
        if not placement and spot is None:
            raise HostError("occupied", "There is no room to put it down here. Turn round or step back.", retryable=True)
        if not apply:
            return {"affected": [held_id]}
        held = self.entities[held_id]
        held.position = list(placement["position_m"]) if placement else spot
        held.held_by = None
        del self.holding[actor]
        self._touch(held, new_revision)
        return {"affected": [held_id]}

    def _drop_spot(self, actor: str, held: Entity) -> list[float] | None:
        """A release with no placement sets the thing down in front of the actor (SandboxPhysics.Drop). Only when an
        avatar stands there (the player a fetch came back to) does it go beside the actor, or behind it. The mock has
        no walls to refuse a drop, so only avatars are in the way here."""
        feet = self.entities[actor].position
        fx, fz = self.facing.get(actor, (0.0, -1.0))
        hx, height, hz = held.half_extents
        for cos, sin in ((1.0, 0.0), (0.0, 1.0), (0.0, -1.0), (-1.0, 0.0)):  # front, then a quarter turn each way, then behind
            dx, dz = fx * cos + fz * sin, -fx * sin + fz * cos  # turned about +Y, as Godot's Rotated(Vector3.Up, angle)
            reach = perception.BODY_RADIUS_M + abs(dx) * hx + abs(dz) * hz + 0.01
            spot = [_r(feet[0] + dx * reach), feet[1], _r(feet[2] + dz * reach)]
            box = {"min_m": [spot[0] - hx, spot[1], spot[2] - hz], "max_m": [spot[0] + hx, spot[1] + height, spot[2] + hz]}
            if not any(_box_gap(box, other.bounds()) == 0.0 for other in self.entities.values()
                       if other.kind == "avatar" and other.id != actor and not other.removed):
                return spot
        return None

    def _op_entity_place(self, principal, message, apply, new_revision):
        args = message["args"]
        target = self._require(args["target"], "$.args.target")
        self._check_changeable(target, "$.args.target", "moved")
        if not target.movable:
            raise HostError("permission_denied", "That cannot be moved.", field_path="$.args.target")
        if target.held_by and target.held_by != self.avatars[principal]:
            raise HostError("target_busy", "Someone is holding that.", field_path="$.args.target")
        self._check_placement(args["placement"], "$.args.placement")
        if not apply:
            return {"affected": [target.id]}
        target.position = list(args["placement"]["position_m"])
        self._touch(target, new_revision)
        return {"affected": [target.id]}

    def _op_entity_push(self, principal, message, apply, new_revision):
        # Not modelled yet (Run 2, P3); a companion naming any avatar but its own is still refused first.
        self._actor(principal, message["args"])
        raise HostError("unsupported_capability", "Pushing arrives with the sandbox verbs (Run 2).", field_path="$.op")

    def _op_entity_set_part(self, principal, message, apply, new_revision):
        args = message["args"]
        target = self._require(args["target"], "$.args.target")
        self._check_changeable(target, "$.args.target")
        if args["part_id"] not in target.parts:
            raise HostError("invalid_args", "That entity has no part with that id.", field_path="$.args.part_id")
        if not apply:
            return {"affected": [target.id]}
        target.parts[args["part_id"]] = float(args["value"])
        self._touch(target, new_revision)
        return {"affected": [target.id]}

    def _op_entity_remove(self, principal, message, apply, new_revision):
        target = self._require(message["args"]["target"], "$.args.target")
        self._check_changeable(target, "$.args.target", "removed")
        if not apply:
            return {"affected": [target.id]}
        target.removed = True
        self.burn_records.pop(target.id, None)  # a removed build's scorch record goes with it (the kernel's NatureWithout)
        if target.held_by:
            self.holding.pop(target.held_by, None)
            target.held_by = None
        self._touch(target, new_revision)
        return {"affected": [target.id]}

    def _op_entity_transform(self, principal, message, apply, new_revision):
        args = message["args"]
        target = self._require(args["target"], "$.args.target")
        self._check_changeable(target, "$.args.target", "transformed")
        source = args["into"]["source"]
        if not apply:
            return {"affected": [target.id]}
        target.display_name = source["name"]
        target.provenance_kind = "ai_created" if principal.startswith("companion:") else "player_created"
        self._touch(target, new_revision)
        return {"affected": [target.id]}

    def _op_creation_place(self, principal, message, apply, new_revision):
        args = message["args"]
        self._check_placement(args["placement"], "$.args.placement")
        live = sum(1 for e in self.entities.values() if e.kind == "creation" and not e.removed)
        if live >= self.policy.max_creations:
            raise HostError("budget_exceeded", "The room already holds as many creations as it can.",
                            allowed=self.policy.max_creations, actual=live)
        if not apply:
            return {}
        self._creation_counter += 1
        entity_id = f"creation:{self._creation_counter:08d}"
        source = args["source"]
        sizes = [p.get("size_m") for p in source["parts"] if isinstance(p.get("size_m"), list) and len(p["size_m"]) == 3]
        half = [0.1, 0.2, 0.1]
        if sizes and all(isinstance(v, (int, float)) for s in sizes for v in s):
            half = [max(s[0] for s in sizes) / 2, max(s[1] for s in sizes), max(s[2] for s in sizes) / 2]
            half = [min(max(v, 0.01), 5.0) for v in half]
        self.entities[entity_id] = Entity(
            id=entity_id, kind="creation", display_name=source["name"], position=list(args["placement"]["position_m"]),
            half_extents=half, affordances=[], movable=True,
            provenance_kind="ai_created" if principal.startswith("companion:") else "player_created",
            revision=new_revision, mass_kg=0.5, created_by=principal,
            materials=[p["material"] for p in source["parts"] if isinstance(p, dict) and isinstance(p.get("material"), str)])
        return {"created": [entity_id], "affected": [entity_id]}

    def _op_creation_revise(self, principal, message, apply, new_revision):
        args = message["args"]
        target = self._require(args["target"], "$.args.target")
        self._check_changeable(target, "$.args.target", "revised")
        if "placement" in args:
            self._check_placement(args["placement"], "$.args.placement")
        if not apply:
            return {"affected": [target.id]}
        if "source" in args:
            target.display_name = args["source"]["name"]
        if "placement" in args:
            target.position = list(args["placement"]["position_m"])
        self._touch(target, new_revision)
        return {"affected": [target.id]}

    def _op_creation_activate(self, principal, message, apply, new_revision):
        target = self._require(message["args"]["target"], "$.args.target")
        if target.kind != "creation":
            raise HostError("invalid_args", "Only creations can be activated.", field_path="$.args.target")
        return {"affected": [target.id]}

    def _op_protect_lock(self, principal, message, apply, new_revision):
        targets = [self._require(t, f"$.args.targets[{i}]") for i, t in enumerate(message["args"]["targets"])]
        for i, target in enumerate(targets):
            if target.kind not in LOCKABLE_KINDS:
                raise HostError("invalid_args", "Only objects and creations can be protected.", field_path=f"$.args.targets[{i}]")
            if target.held_by:
                # A held thing could be carried off and put down elsewhere after the lock. Put it down first.
                raise HostError("target_busy", "Someone is holding that; it can be protected once it is put down.",
                                field_path=f"$.args.targets[{i}]")
        if not apply:
            return {"affected": [t.id for t in targets]}
        for target in targets:
            if target.protected:
                continue  # already protected: the lock and who set it stay as they are
            target.protected, target.protected_by = True, principal
            self._touch(target, new_revision)
        return {"affected": [t.id for t in targets]}

    def _op_protect_unlock(self, principal, message, apply, new_revision):
        if not principal.startswith("player:"):  # defence in depth; _handle already refused it
            raise HostError("permission_denied", "Only the player can do that, directly in the game.", field_path="$.op")
        targets = [self._require(t, f"$.args.targets[{i}]") for i, t in enumerate(message["args"]["targets"])]
        if not apply:
            return {"affected": [t.id for t in targets]}
        for target in targets:
            target.protected, target.protected_by = False, None
            self._touch(target, new_revision)
        return {"affected": [t.id for t in targets]}

    def _op_goal_set(self, principal, message, apply, new_revision):
        args = message["args"]
        actor = self._actor(principal, args)
        if args["goal"] == "lead":
            return self._lead(principal, message, actor, apply)
        if args["goal"] in ("follow", "come") and "target" not in args and not principal.startswith("player:") \
                and not self.policy.follow_player_out_of_sight and "avatar:player" not in (self._view or {"avatar:player"}):
            raise HostError("target_not_found", "The player is not in sight.", field_path="$.args.goal")
        data = {"actor": actor, "goal": args["goal"]}
        aim = None
        if "target" in args:
            target = self._entity(args["target"])
            if target is not None:
                kind, movable, protected, mass, aim = (target.kind, target.movable, target.protected, target.mass_kg,
                                                       target.bounds())
                held_by = target.held_by
                if not principal.startswith("player:"):
                    data["target_seen"] = "now"
            else:
                # Out of sight: only a goal that moves or turns the companion's own avatar may aim at a
                # remembered thing, and it is judged on the memory alone, so the answer cannot reveal
                # what became of it (moved, locked, removed). Changing things needs them in sight now.
                entry = self._recall(principal, args["target"]) if args["goal"] in REMEMBERED_TARGET_GOALS else None
                if entry is None:
                    raise _not_found("$.args.target")
                summary = entry.view["summary"]
                kind, movable, protected, mass, aim = (summary["kind"], summary["movable"], summary["protected"],
                                                       entry.mass_kg, summary["bounds_m"])
                held_by = summary.get("held_by")
                data["target_seen"] = "remembered"
                fields = self._memory_fields(entry)
                data["last_seen_ago_s"], data["may_be_stale"] = fields["last_seen_ago_s"], fields["may_be_stale"]
            if args["goal"] == "fetch":
                # entity.grab's checks and limit, judged on what the companion sees or remembers; the pick-up on
                # arrival checks them again on the thing as it is then.
                if kind not in ("object", "creation") or not movable:
                    raise HostError("permission_denied", "That cannot be fetched.", field_path="$.args.target")
                problem = self._grab_problem(actor, args["target"], kind, movable, protected, held_by, mass)
                if problem is not None:
                    raise problem
        if "position_m" in args and not _inside(args["position_m"], self.room_bounds):
            raise HostError("out_of_bounds", "That position is outside the room.", field_path="$.args.position_m")
        if "area" in args:
            for corner in ("min_m", "max_m"):
                if not _inside(args["area"][corner], self.room_bounds):
                    raise HostError("out_of_bounds", "That area is outside the room.", field_path=f"$.args.area.{corner}")
        outcome = {"affected": [actor], "data": data}
        if apply:
            self._cancel_job(actor, by=principal)
            goal = {k: copy.deepcopy(v) for k, v in args.items() if k != "actor"}
            goal["started_utc"] = utc(self.clock.now())
            goal["set_by"] = principal
            if aim is not None:
                # A goal with a target runs as a job: the host re-checks the target when the avatar arrives.
                outcome["job_id"] = goal["job_id"] = self._start_job(principal, actor, message["action_id"],
                                                                     args["goal"], args["target"])
                goal["aim_bounds"] = copy.deepcopy(aim)
            if args["goal"] == "fetch":
                # A fetch walks to the thing, picks it up, and brings it back to the player. Already in hand, it
                # only comes back.
                goal["phase"] = "return" if self.holding.get(actor) == args["target"] else "approach"
            self.goals[actor] = goal
            if "job_id" in goal:
                self._task_started(goal["job_id"], self.jobs[goal["job_id"]])
        return outcome

    # ------------------------------------------------------------------ goal jobs (the goal runner's side)

    def _start_job(self, principal: str, actor: str, action_id: str, goal: str, target: str) -> str:
        # Opaque (contracts: common job_id): 'job-' and 128 random bits in lowercase base32, never a counter.
        job_id = "job-" + base64.b32encode(secrets.token_bytes(16)).decode("ascii").rstrip("=").lower()
        self.jobs[job_id] = {"principal": principal, "actor": actor, "action_id": action_id, "state": "running",
                             "goal": goal, "target": target}
        mine = [k for k, job in self.jobs.items() if job["principal"] == principal and job["state"] != "running"]
        excess = sum(1 for job in self.jobs.values() if job["principal"] == principal) - self.policy.max_jobs_per_principal
        for old in mine[:max(0, excess)]:
            del self.jobs[old]
        return job_id

    def _cancel_job(self, actor: str, by: str | None = None) -> None:
        goal = self.goals.get(actor)
        job = self.jobs.get(goal.get("job_id", "")) if goal else None
        if job is not None and job["state"] == "running":
            job["state"] = "cancelled"
            self._task_ended(goal["job_id"], "cancelled")
            self._trust_job_ended(goal["job_id"], "cancelled")
            # A lead's end pops the bubble it led (decision 60; the kernel's LeadCancelled), by the Gubble's own action when its own
            # new goal ended it (decision 96).
            led = self._led_bubble(goal["job_id"])
            if led is not None:
                self._update_lead_receipt(led.lead, "ended", "stopped")
                led.lead = None
                self._end_bubble(led, "popped", by_gubble=by is not None and not by.startswith("player:"))

    def goal_arrived(self, actor_id: str) -> str:
        """The game's goal runner: the avatar has reached its goal (walked there, or turned to look or
        point). The host re-checks the target from where the avatar is now, with the avatar's own line
        of sight, and finishes the goal's job honestly. Returns the job's state.

        - succeeded: the target is in sight and within reach (0.15 m) of where the goal aimed;
        - failed, revision_conflict: it is in sight, but has moved since (the companion sees where);
        - failed, target_not_found: it is not in sight. Moved out of sight and gone give the same answer.

        A fetch arrives twice (contracts/README.md, "The sandbox verbs"). At the thing, after the same re-check,
        the avatar picks it up with entity.grab's checks and limit (a refusal fails the job with that error), and
        the job keeps running ("running" is returned). Back beside the player, the job succeeds with the avatar
        still holding the thing; entity.release puts it down. The mock stands in for the walk back by placing the
        avatar beside the player, as a come would leave it.
        """
        with self._lock:
            goal = self.goals.get(actor_id)
            job = self.jobs.get(goal.get("job_id", "")) if goal else None
            if job is None or job["state"] != "running":
                raise KeyError("that avatar has no goal with a target running")
            if goal.get("phase") == "return":
                return self._fetch_returned(actor_id, goal, job)
            if goal["goal"] == "lead":
                return self._lead_arrived(actor_id, goal, job)
            principal = self._principal_of(actor_id) or job["principal"]
            # The team's sight, as the host's arrival check (the target the player alone sees is in sight now).
            seen = self.team_sight(principal)
            if not principal.startswith("player:"):
                self._look(principal, seen)  # arriving is looking: the team's map is refreshed or dropped
            target = self.entities.get(goal["target"])
            error = None
            if target is None or target.removed or goal["target"] not in seen:
                error = HostError("target_not_found", "The target is not where it was seen. Observe and try again.",
                                  field_path="$.args.target")
            elif _box_gap(target.bounds(), goal["aim_bounds"]) > perception.REACH_M:
                error = HostError("revision_conflict", "The target has moved since it was seen. Observe and try again.",
                                  field_path="$.args.target", retryable=True)
            elif goal["goal"] == "fetch":
                error = self._grab_problem(actor_id, target.id, target.kind, target.movable, target.protected,
                                           target.held_by, target.mass_kg)
                if error is None:
                    self._take_hold(actor_id, target)
                    goal["phase"] = "return"
                    goal["blocked_s"] = 0.0  # the walk back starts afresh (the host resets BlockedS at the pick-up)
                    self._emit("goal_progress", {"actor": actor_id, "job_id": goal["job_id"], "phase": "return"})
                    return job["state"]
            return self._finish_job(actor_id, goal, job, error)

    def goal_blocked(self, actor_id: str, seconds: float) -> str:
        """The game's goal runner: the avatar's body has reported blocked for `seconds` more without a break. A
        goal that walks somewhere (come, go_to, fetch; with or without a thing to walk to) blocked for
        unreachable_after_s fails with target_unreachable and the body stays where it is. Returns the job's state,
        "stopped" for a walk with no job, or "running" while it keeps trying."""
        with self._lock:
            goal = self.goals.get(actor_id)
            if goal is not None and goal["goal"] == "lead":
                return self._lead_blocked(actor_id, goal, seconds)
            if goal is None or goal["goal"] not in WALKING_GOALS:
                raise KeyError("that avatar is not walking anywhere")
            goal["blocked_s"] = goal.get("blocked_s", 0.0) + seconds
            if goal["blocked_s"] < self.policy.unreachable_after_s:
                return "running"
            job = self.jobs.get(goal.get("job_id", ""))
            if job is None:
                del self.goals[actor_id]
                return "stopped"
            return self._finish_job(actor_id, goal, job, HostError(
                "target_unreachable", "The companion cannot find a way there from here.", field_path="$.args.target"))

    def goal_moving(self, actor_id: str) -> None:
        """The body reports it is moving again: the blocked time starts over."""
        with self._lock:
            if actor_id in self.goals:
                self.goals[actor_id]["blocked_s"] = 0.0

    def _fetch_returned(self, actor_id: str, goal: dict, job: dict) -> str:
        """A fetch back beside the player: it succeeds while the avatar still holds the thing."""
        error = None
        if self.holding.get(actor_id) != goal["target"]:
            error = HostError("target_not_found", "The fetched thing is no longer in hand.", field_path="$.args.target")
        else:
            player = self.entities[self.avatars[PLAYER]].position
            here = self.entities[actor_id].position
            away = [here[0] - player[0], here[2] - player[2]]
            length = math.hypot(*away)
            if length == 0.0:
                away, length = [1.0, 0.0], 1.0
            scale = COME_ARRIVAL_M / length
            self.entities[actor_id].position = [_r(player[0] + away[0] * scale), _r(player[1] + HOVER_M),
                                                _r(player[2] + away[1] * scale)]
            self.facing[actor_id] = (-away[0] / length, -away[1] / length)  # it faces the player it came back to
            self._carry(actor_id)
        return self._finish_job(actor_id, goal, job, error)

    def _finish_job(self, actor_id: str, goal: dict, job: dict, error: HostError | None) -> str:
        del self.goals[actor_id]
        if error is None:
            job["state"] = "succeeded"
        else:
            job["state"] = "failed"
            message = {"schema": COMMAND_SCHEMA, "op": "goal.set", "action_id": job["action_id"]}
            job["result"] = self._fail(job["principal"], message, error)
        self._task_ended(goal["job_id"], job["state"])
        self._trust_job_ended(goal["job_id"], job["state"])
        self._emit("goal_finished", {"actor": actor_id, "job_id": goal["job_id"], "state": job["state"]})
        return job["state"]

    def _op_goal_stop(self, principal, message, apply, new_revision):
        # Always permitted. Without an actor it stops every actor the principal may direct and their goals; the
        # player's stop therefore stops the companion as well. The Gubble carries out the island's abilities, so a
        # stop that covers it also ends the effects this principal may stop (the kernel's StopCompanionEffects).
        args = message["args"]
        actors = [self._actor(principal, args)] if "actor" in args else sorted(self._directable(principal))
        carriers = {a for a in actors if a != self.avatars[PLAYER]}
        ending = [e for e in self._stoppable(principal) if e.carrier in carriers]
        if not apply:
            return {}  # a preview of a stop says nothing (the kernel's Previewed)
        # The bubble the Gubble leads pops with its stop (decision 60), as the player's own pop when it is the player's X.
        led = self._pop_led_bubble(GUBBLE, principal.startswith("player:")) if GUBBLE in carriers else []
        for actor in actors:
            self._cancel_job(actor)
            self.goals.pop(actor, None)
        ended = led + self._end_effects(ending)
        # Its bubbles pop too, the player's X whoever cast them (the kernel's PopBubbles).
        if carriers:
            ended += self._pop_bubbles(principal, "all")
        return {"affected": actors + ended, "data": {"effects_stopped": len(ended)}}

    # ------------------------------------------------------------------ the island's abilities (effects)
    # The kernel's CommandHostEffects.cs: the pack says which abilities exist and their bounds; effect.start casts one,
    # carried out by the Gubble, as the player or as the companion. Three primitives, one set of rules: light.emit (Glow, a
    # light), particles.burst (Fireworks, a burst with a brief flash) and particles.float (Bubbles: since the founder's redesign
    # one bubble at a spot, the place deciding, _bubble_effect_start; no stream). Glow and Fireworks follow the Gubble (targets:
    # its own avatar) or play at a spot within the ability's reach and in the team's sight. The kernel hands them to the look by
    # primitive (StartLook); the mock draws nothing, so its rules are all there is.

    def _stoppable(self, principal: str) -> list[ActiveEffect]:
        """The effects a stop by principal covers: the player's covers every effect, a companion's only its own."""
        self._expire_effects()
        return [e for e in self.effects.values() if principal.startswith("player:") or e.principal == principal]

    def _end_effects(self, ending: list[ActiveEffect]) -> list[str]:
        ended = []
        sources = {effect.id for effect in ending}
        batch = list(ending) + [effect for effect in self.effects.values()
                                if effect.bubble_source in sources and effect.id not in sources]
        for effect in batch:
            if self.effects.pop(effect.id, None) is not None:
                ended.append(effect.id)
        for effect in self.effects.values():
            effect.connections.difference_update(ended)
        return ended

    def _expire_effects(self) -> None:
        """An effect ends at its duration (the host's clock)."""
        now = self.clock.now()
        self._end_effects([e for e in self.effects.values() if now >= e.expires_at])

    def _carrier(self, principal: str) -> str:
        """The avatar that carries out what the principal casts: the Gubble for the player, a companion's own avatar."""
        return GUBBLE if principal.startswith("player:") else self.avatars[principal]

    def _team_sees_spot(self, centre: list[float], principal: str, carrier: str) -> bool:
        """Whether the place a point effect would float is in sight now: of the Gubble's eyes, or the player's (the
        team's sight, or the player's own cast). A column EFFECT_SIGHT_HALF_WIDTH_M either side of the centre, as tall
        as the wisp floats above it."""
        low = [centre[0] - EFFECT_SIGHT_HALF_WIDTH_M, centre[1], centre[2] - EFFECT_SIGHT_HALF_WIDTH_M]
        high = [centre[0] + EFFECT_SIGHT_HALF_WIDTH_M, centre[1] + WISP_LIFT_M, centre[2] + EFFECT_SIGHT_HALF_WIDTH_M]
        eyes = [(carrier, "companion")]
        if principal.startswith("player:") or (self.policy.shared_sight and principal == self._team_principal()):
            eyes.append((self.avatars[PLAYER], "player"))
        occluders = self._occluders()
        for avatar_id, kind in eyes:
            avatar = self.entities.get(avatar_id)
            if avatar is None or avatar.removed:
                continue
            if perception.visible(perception.eye_point(avatar.position, kind), "effect:place", low, high, occluders):
                return True
        return False

    def _plan_effect(self, principal: str, args: dict) -> dict:
        """effect.start checked against the rules, the caster, the targets, the bounds, the place and the caps, in that
        order, with the kernel's codes (PlanEffect). Runs before any hold, so a held request is one that can start."""
        self._expire_effects()
        player = principal.startswith("player:")
        # The companion names only what the team has in sight now (the kernel's CheckPerceived, before the rules).
        for target_id in args.get("targets", []):
            if not player and self._entity(target_id) is None:
                raise _not_found("$.args.targets")
        if self.rules is None:
            raise HostError("unsupported_capability", "This island's rules did not load, so it grants no abilities.",
                            field_path="$.args.capability")
        if args["capability"] in {"grow", "fire"}:
            raise HostError("unsupported_capability", "Use nature.cast for this ability; preview Fire first.", field_path="$.args.capability")
        ability = self.rules.ability(args["capability"])
        if ability is None:
            raise HostError("unsupported_capability", "This island has no ability by that name. Call capabilities.list.",
                            field_path="$.args.capability")
        if ("player" if player else "companion") not in ability.cast_by:
            raise HostError("permission_denied", "This island does not let the player cast that." if player
                            else "This island does not let the companion cast that.", field_path="$.args.capability")
        # Targets: the Gubble's own avatar (self, the light follows it) or none (point). Nothing else, from anyone.
        carrier = self._carrier(principal)
        targets = list(args.get("targets", []))
        own = targets != []
        if own and targets != [carrier]:
            if player:
                raise HostError("invalid_args", "This ability works on the Gubble or at a spot; it cannot be cast on that.",
                                field_path="$.args.targets")
            raise HostError("permission_denied", "A companion's ability may target only its own avatar.",
                            field_path="$.args.targets")
        if own and "self" not in ability.targets:
            raise HostError("invalid_args", "This ability cannot be cast on the Gubble itself; name no targets and a spot.",
                            field_path="$.args.targets", allowed=sorted(ability.targets))
        if not own and "point" not in ability.targets:
            raise HostError("invalid_args", "This ability works only on the Gubble itself; name its avatar in targets.",
                            field_path="$.args.targets", allowed=sorted(ability.targets))
        # The Bubble is cast at a spot, like Glow, and the place decides (decision 56): never on the Gubble itself.
        bubble = ability.primitive == "particles.float"
        if own and bubble:
            raise HostError("invalid_args", BUBBLE_AT_A_SPOT, field_path="$.args.targets")
        # Bounds: every param the ability's, within its range; omitted ones take the defaults.
        params: dict[str, float] = {}
        for name in sorted(args["params"]):
            value, path = args["params"][name], textsafety.field_path(["args", "params", name])
            if name not in ability.params:
                raise HostError("invalid_args", "This ability takes no parameter by that name.", field_path=path,
                                allowed=list(ability.params))
            if isinstance(value, bool) or not isinstance(value, (int, float)):
                raise HostError("invalid_args", "This parameter is a number.", field_path=path)
            low, high, _default = ability.params[name]
            if not low <= value <= high:
                raise HostError("invalid_args", "This parameter is out of the ability's range.", field_path=path,
                                allowed=[low, high], actual=value)
            params[name] = float(value)
        for name, (_low, _high, default) in ability.params.items():
            params.setdefault(name, default)
        radius = float(args["area"]["radius_m"])
        if radius > ability.area_radius_max_m:
            raise HostError("invalid_args", "The area is larger than the ability allows.", field_path="$.args.area.radius_m",
                            allowed=ability.area_radius_max_m, actual=radius)
        duration = float(args["duration_s"])
        if duration > ability.duration_max_s:
            raise HostError("invalid_args", "That is longer than the ability lasts.", field_path="$.args.duration_s",
                            allowed=ability.duration_max_s, actual=duration)
        # The Gubble carries every ability out.
        body = self.entities.get(carrier)
        if body is None or body.removed:
            raise HostError("not_ready", "The Gubble is not in the room.", retryable=True)
        centre = [_r(v) for v in (body.position if own else args["area"]["center_m"])]
        if not own:
            # A spot: inside the room, within the Gubble's reach, and in the team's sight now (where the wisp will float).
            if not self._inside_island(centre):
                raise HostError("out_of_bounds", "That spot is outside the room.", field_path="$.args.area.center_m")
            distance = math.dist(body.position, centre)
            if distance > ability.reach_m:
                raise HostError("out_of_bounds", "That spot is beyond the Gubble's reach; it must come closer first.",
                                field_path="$.args.area.center_m", retryable=True, allowed=ability.reach_m,
                                actual=round(distance, 3))
            if not self._team_sees_spot(centre, principal, carrier):
                raise HostError("target_not_found", "Neither of you can see that spot now. Look there first.",
                                field_path="$.args.area.center_m")
        # Caps: the team's earned Glow allowance within the pack's ceiling, then the room's fixed ceiling.
        ability_limit = min(ability.max_active, self.glow_progress.earned_light_limit) \
            if ability.capability == "glow" else ability.max_active
        mine = sum(1 for e in self.effects.values() if e.capability == ability.capability)
        if not bubble and mine >= ability_limit:
            raise HostError("budget_exceeded", "As many of these are running as the island allows; stop one first.",
                            field_path="$.args.capability", allowed=ability_limit, actual=mine)
        # The bubble counts toward the room's cap at any effect's start; a bubble's cast replaces the one there (one at a time).
        running = len(self.effects) + (0 if bubble else len(self.bubbles))
        if running >= self.policy.max_active_effects:
            raise HostError("budget_exceeded", "As many effects are running as the room allows; stop one first.",
                            field_path="$.args.capability", allowed=self.policy.max_active_effects, actual=running)
        variant, touch = args.get("variant", "auto"), args.get("touch", self.glow_progress.touch)
        bubble_source = None
        if ability.capability == "glow":
            variant = self._resolve_glow_variant(variant, own, centre)
            surface = self._glow_water_surface_at(centre) if variant == "bioluminesce" else None
            if surface is not None:
                # The kernel lifts a bioluminescent glow to the water's surface, which must still be in reach and in sight.
                centre = [centre[0], surface, centre[2]]
                if math.dist(body.position, centre) > ability.reach_m:
                    raise HostError("out_of_bounds", "The water surface is beyond the Gubble's reach.", field_path="$.args.area.center_m")
                if not self._team_sees_spot(centre, principal, carrier):
                    raise HostError("target_not_found", "Neither of you can see the water surface.", field_path="$.args.area.center_m")
            if variant == "glowing_bubbles":
                nearby = [effect for effect in self.effects.values() if effect.capability == "bubbles" and
                          (effect.follows if own else not effect.follows and math.dist(effect.centre, centre) <= 0.45)]
                if not nearby:
                    raise HostError("target_not_found", "Glow needs nearby bubbles to light.", field_path="$.args.variant")
                bubble_source = nearby[-1].id
        else:
            if "variant" in args or "touch" in args:
                raise HostError("invalid_args", "Glow touches and variants belong to Glow.", field_path="$.args")
            variant = touch = "plain"
        self._require_mock_primitive(ability)
        return {"ability": ability, "self": own, "carrier": carrier, "centre": centre, "radius_m": radius,
                "duration_s": duration, "params": params, "variant": variant, "touch": touch, "bubble_source": bubble_source}

    @staticmethod
    def _runtime_required(feature: str) -> None:
        # The contract's code for "this host cannot do that here" (the kernel's, for a game with no renderer).
        raise HostError("unavailable", f"{feature} needs the game itself; this mock host does not simulate it.", field_path="$.op")

    @staticmethod
    def _glow_water_surface_at(centre: list[float]) -> float | None:
        """The water surface a bioluminescent glow rises to (the kernel's GlowWaterSurfaceAt). The box world has no water,
        and it refuses that variant before asking (_resolve_glow_variant), so this never finds any."""
        return None

    def _require_mock_primitive(self, ability: Ability) -> None:
        if ability.primitive not in {"light.emit", "particles.float", "particles.burst"}:
            self._runtime_required("Terrain growth and fire")

    def _resolve_glow_variant(self, variant: str, own: bool, centre: list[float]) -> str:
        if own:
            if variant in {"auto", "plain", "lantern"}:
                return "lantern"
            if variant == "glowing_bubbles":
                return variant
            raise HostError("invalid_args", "That Glow needs a place on the island.", field_path="$.args.variant")
        if variant == "lantern":
            raise HostError("invalid_args", "Lantern follows the Gubble.", field_path="$.args.variant")
        if variant in {"torch", "bioluminesce", "skylight"}:
            self._runtime_required("Terrain-aware Glow")
        # Plain floor wisps are representable here; other auto classification requires real surfaces/water.
        if variant == "auto":
            if SEA_EXTENSION in self.room.get("extensions", {}) or centre[1] >= self.room_bounds["min_m"][1] + 0.2:
                self._runtime_required("Terrain-aware automatic Glow")
            return "plain"
        return variant

    @staticmethod
    def _effect_data(plan: dict, effect_id: str | None = None, expires_at: float | None = None) -> dict:
        data: dict = {}
        if effect_id is not None:
            data["effect"] = effect_id
        data.update({"capability": plan["ability"].capability, "category": plan["ability"].category,
                     "target": "self" if plan["self"] else "point", "params": dict(plan["params"]),
                     "area": {"center_m": list(plan["centre"]), "radius_m": plan["radius_m"]}, "duration_s": plan["duration_s"]})
        if expires_at is not None:
            data["expires_utc"] = utc(expires_at)
        if plan["ability"].capability == "glow":
            data.update(variant=plan["variant"], touch=plan["touch"])
        return data

    def _op_effect_start(self, principal, message, apply, new_revision):
        plan = self._plan_effect(principal, message["args"])
        if plan["ability"].primitive == "particles.float":
            return self._bubble_effect_start(principal, message, apply, plan)
        # The guard at commit (mock_energy.py, the kernel's GuardEnergy): after every other check, never on a preview or a hold.
        price = self._guard_energy(plan["ability"].capability, principal) if self._commits_now(principal, message) else 0
        if not apply:
            return {"data": self._effect_data(plan)}  # a preview says what would start
        effect_id = "effect:" + "".join(secrets.choice(EFFECT_ID_ALPHABET) for _ in range(EFFECT_ID_LENGTH))
        expires_at = self.clock.now() + plan["duration_s"]
        nearby = []
        for effect in self.effects.values():
            if (effect.capability == "glow" and not effect.follows and effect.principal == principal
                    and effect.variant in {"plain", "torch"} and 0.025 <= math.dist(effect.centre, plan["centre"]) <= 0.45
                    and all(math.dist(effect.centre, accepted.centre) >= 0.025 for accepted in nearby)):
                nearby.append(effect)
        constellation = (plan["ability"].capability == "glow" and not plan["self"]
                         and plan["variant"] in {"plain", "torch"} and len(nearby) >= 2)
        self.effects[effect_id] = ActiveEffect(
            id=effect_id, capability=plan["ability"].capability, principal=principal, carrier=plan["carrier"],
            follows=plan["self"], centre=list(plan["centre"]), radius_m=plan["radius_m"], params=dict(plan["params"]),
            expires_at=expires_at, variant=plan["variant"], touch=plan["touch"], bubble_source=plan["bubble_source"])
        if plan["ability"].capability == "glow":
            discovery = plan["variant"] if plan["variant"] in {"lantern", "torch", "bioluminesce", "skylight"} else None
            self.glow_progress.record_observed(discovery, constellation)
        if constellation:
            group = nearby + [self.effects[effect_id]]
            for effect in group:
                effect.connections.update(other.id for other in group if effect is not other)
        self._charge_energy(price)  # the kernel's ChargeEnergy, which Trust reads (decision 69)
        return {"created": [effect_id], "affected": [effect_id], "data": self._effect_data(plan, effect_id, expires_at)}

    def _op_effect_stop(self, principal, message, apply, new_revision):
        # Always permitted: one id, or all that principal may stop (the player any, a companion its own). An unknown
        # id, or another's, stops nothing and still applies.
        which = message["args"]["effect"]
        ending = [e for e in self._stoppable(principal) if which == "all" or e.id == which]
        if not apply:
            return {}  # a preview of a stop says nothing (the kernel's Previewed)
        ended = self._end_effects(ending) + self._pop_bubbles(principal, which)
        outcome: dict = {"data": {"effects_stopped": len(ended)}}
        if ended:
            outcome["affected"] = ended
        return outcome

    def _op_glow_configure(self, principal, message, apply, new_revision):
        if principal != PLAYER:
            raise HostError("permission_denied", "Only the player chooses Glow's touch and sound settings.")
        candidate = self.glow_progress.configured(**message["args"])
        if apply:
            self.glow_progress = candidate
        return {"data": {"touch": candidate.touch, "sound_volume": candidate.sound_volume, "muted": candidate.muted}}

    def _op_effect_adjust(self, principal, message, apply, new_revision):
        self._expire_effects()
        args = message["args"]
        if self.glow_progress.earned_light_limit < 5:
            raise HostError("permission_denied", "Moving constellation points and changing their links comes with the fifth light.")
        effect = self.effects.get(args["effect"])
        if effect is None or effect.capability != "glow":
            raise HostError("target_not_found", "That Glow has ended.", field_path="$.args.effect")
        if effect.principal != principal:
            raise HostError("permission_denied", "A caster can adjust only their own Glow.", field_path="$.args.effect")
        if effect.follows or effect.variant not in {"plain", "torch"}:
            raise HostError("invalid_args", "Only a free constellation point can be adjusted.", field_path="$.args.effect")
        centre = list(args.get("center_m", effect.centre))
        ability = self.rules.ability("glow") if self.rules else None
        if ability is None:
            raise HostError("unsupported_capability", "This island has no Glow.")
        body = self.entities.get(self._carrier(principal))
        if body is None or body.removed:
            raise HostError("not_ready", "The Gubble is not in the room.")
        if ((SEA_EXTENSION not in self.room.get("extensions", {}) and not _inside(centre, self.room_bounds))
                or math.dist(body.position, centre) > ability.reach_m):
            raise HostError("out_of_bounds", "The point must stay within the Gubble's reach.", field_path="$.args.center_m")
        if not self._team_sees_spot(centre, principal, self._carrier(principal)):
            raise HostError("target_not_found", "Neither of you can see that spot.", field_path="$.args.center_m")
        self._resolve_glow_variant(effect.variant, False, centre)
        links = list(args.get("connections", sorted(effect.connections)))
        for linked_id in links:
            linked = self.effects.get(linked_id)
            if (linked is None or linked.id == effect.id or linked.capability != "glow" or linked.follows
                    or linked.variant not in {"plain", "torch"}):
                raise HostError("target_not_found", "A link needs another free Glow point.", field_path="$.args.connections")
            if linked.principal != principal:
                raise HostError("permission_denied", "A caster can link only their own Glow points.", field_path="$.args.connections")
            if math.dist(linked.centre, centre) > ability.reach_m:
                raise HostError("out_of_bounds", "Those points are too far apart to connect.", field_path="$.args.connections")
        data = {"effect": effect.id, "center_m": centre, "connections": links}
        if apply:
            effect.centre = centre
            for other in self.effects.values():
                other.connections.discard(effect.id)
            effect.connections = set(links)
            for linked_id in links:
                self.effects[linked_id].connections.add(effect.id)
        return {"affected": [effect.id], "data": data} if apply else {"data": data}

    # ------------------------------------------------------------------ the Bubble (M9; the kernel's Kernel/Bubble)

    def _bubbles_ability(self) -> Ability | None:
        """The island's Bubbles (its particles.float ability), whose caster, reach and bounds every expertise uses."""
        if self.rules is None:
            return None
        return next((a for a in self.rules.abilities if a.primitive == "particles.float"), None)

    def _bubble_holding(self) -> MockBubble | None:
        return next((b for b in self.bubbles.values() if b.holding), None)

    def _inside_island(self, point: list[float]) -> bool:
        """The kernel's InsideIsland, one rule for every cast, effect and lead, the body's own: without a sea, inside the room's
        bounds; round an island the sea has no edge, so only the top and the bottom hold. Within ISLAND_SLACK_M either way."""
        if SEA_EXTENSION not in self.room.get("extensions", {}):
            return _inside(point, self.room_bounds, ISLAND_SLACK_M)
        return self.room_bounds["min_m"][1] - ISLAND_SLACK_M <= point[1] <= self.room_bounds["max_m"][1] + ISLAND_SLACK_M

    def _walk_in_centre(self, spot: list[float], radius: float) -> list[float]:
        """The kernel's WalkInCentre (ClearOfGround): a bubble to walk into rests on what is under its spot, the floor or the top of a
        thing whose footprint holds it, found as the kernel's ray finds it (from 2r over the spot down to 1 cm under it); a spot
        inside a box rests on its top."""
        start = spot[1] + 2 * radius
        tops = [self.room_bounds["min_m"][1]]
        for entity in self.entities.values():
            if entity.removed or entity.id.startswith("avatar:"):
                continue
            box = entity.bounds()
            if box["min_m"][0] <= spot[0] <= box["max_m"][0] and box["min_m"][2] <= spot[2] <= box["max_m"][2]:
                tops.append(box["max_m"][1])
        hits = [top for top in tops if spot[1] - 0.01 <= top <= start]
        y = spot[1] + radius
        if hits and max(hits) + radius + 0.005 > y:
            y = max(hits) + radius + 0.005
        return [spot[0], y, spot[2]]

    @staticmethod
    def _clear_of_the_player(centre: list[float], radius: float, feet: list[float]) -> bool:
        """The kernel's ClearOfThePlayer: the body's middle farther from the bubble's centre than its radius, the step-in clearance
        and the body's own radius; walking in is then the player's own consent."""
        middle = [feet[0], feet[1] + perception.BODY_HEIGHT_M / 2, feet[2]]
        return math.dist(middle, centre) > radius + BUBBLE_STEP_IN_CLEARANCE_M + perception.BODY_RADIUS_M

    def _bubble_cast(self, principal: str, message: dict, apply: bool) -> dict:
        """The kernel's BubbleCast, its refusals in its order. A preview of a cast on the player shows the ghost (its preview_id);
        the prediction before a commit checks the ghost (before any hold); the commit forms the bubble and answers its receipt."""
        args = message["args"]
        preview = message.get("preview") is True
        player = principal.startswith("player:")
        capability = args["capability"]
        boat = capability == "bubble_boat"
        target = args.get("target")
        # The companion names only what the team has in sight now (the kernel's CheckPerceived), before the rules.
        if target is not None and not player and self._entity(target) is None:
            raise _not_found("$.args.target")
        # Lift is no cast since the redesign (decision 58): leading a bubble to a higher spot floats it up there.
        if capability == "bubble_lift":
            raise HostError("unsupported_capability", BUBBLE_LIFT_IS_A_LEAD, field_path="$.args.capability")
        ability = self._bubbles_ability()
        if ability is None:
            raise HostError("unsupported_capability", "This island does not grant Bubbles.", field_path="$.args.capability")
        if ("player" if player else "companion") not in ability.cast_by:
            raise HostError("permission_denied", "This island does not allow this caster to use Bubbles.", field_path="$.args.capability")
        centre = [_r(v) for v in args["center_m"]]
        radius, duration = float(args["radius_m"]), float(args["duration_s"])
        if radius > ability.area_radius_max_m:
            raise HostError("invalid_args", "That radius is more than this island allows.", field_path="$.args.radius_m",
                            allowed=ability.area_radius_max_m)
        least, most = BUBBLE_BOAT_RADIUS_M if boat else BUBBLE_CARRY_RADIUS_M
        if radius < least or radius > most:
            raise HostError("invalid_args", "A Bubble boat is not that size." if boat else "A bubble that carries the player is not that size.",
                            field_path="$.args.radius_m", allowed=[least, most])
        if duration > ability.duration_max_s:
            raise HostError("invalid_args", "That duration is more than this island allows.", field_path="$.args.duration_s",
                            allowed=ability.duration_max_s)
        if not self._inside_island(centre):
            raise HostError("out_of_bounds", "Choose a point inside this island.", field_path="$.args.center_m")
        gubble = self.entities.get(GUBBLE)
        if gubble is None or gubble.removed or math.dist(gubble.position, centre) > ability.reach_m:
            raise HostError("out_of_bounds", "Bring the Gubble closer before casting.", field_path="$.args.center_m", retryable=True,
                            allowed=ability.reach_m)
        if not self._team_sees_spot(centre, principal, GUBBLE):
            raise HostError("target_not_found", "The team must see the place the Gubble will affect.", field_path="$.args.center_m")
        if target is not None and target != self.avatars[PLAYER]:
            if target == GUBBLE:
                raise HostError("invalid_args", "A Bubble carries the player; the Gubble floats on its own.", field_path="$.args.target")
            raise _not_found("$.args.target")
        body = self.entities.get(self.avatars[PLAYER])
        if body is None or body.removed:
            raise HostError("not_ready", "The player's body is not in the room yet.", retryable=True)
        feet = body.position
        if target is not None:
            if math.dist(feet, centre) > radius + BUBBLE_ENVELOP_REACH_M:
                raise HostError("invalid_args", "A carry on the player forms round them: center it where they stand.", field_path="$.args.center_m")
        elif boat:
            # The box world has no water: the kernel's ShallowWaterAt finds none either.
            raise HostError("invalid_args", BUBBLE_NO_WATER, field_path="$.args.center_m")
        elif not self._clear_of_the_player(self._walk_in_centre(centre, radius), radius, feet):
            # Judged where it forms (a point under a surface rests on it), before any price: the kernel's ClearOfThePlayer.
            raise HostError("invalid_args", BUBBLE_ON_THE_PLAYER, field_path="$.args.center_m")
        now = self.clock.now()
        # One bubble at a time: this one replaces the one there, so only the effects count toward the room's cap.
        if len(self.effects) >= self.policy.max_active_effects:
            raise HostError("budget_exceeded", "As many effects are running as the room allows; stop one first.", field_path="$.args.capability",
                            retryable=True, allowed=self.policy.max_active_effects)
        keyed = not player and ability.tier == "keyed_yes"
        signature = (capability, tuple(centre), radius, duration, target or "")
        data = {"capability": capability, "center_m": centre, "radius_m": radius, "duration_s": duration, "cell_count": 0, "cells": [],
                "cells_truncated": False, "requires_approval": keyed}
        if target is not None:
            data["target"] = target
        if preview:
            if target is not None:
                data["preview_id"] = self._show_ghost(principal, signature, now)
            return {"affected": [target], "data": data} if target is not None else {"data": data}
        preview_id = args.get("preview_id")
        # The player's click on a held cast is the consent itself (the kernel's approvedBy).
        if (preview_id is not None or target is not None) and not self._approving:
            ghost = self.bubble_ghosts.get(preview_id) if preview_id is not None else None
            if ghost is None or ghost[0] != principal or ghost[1] != signature or ghost[3] <= now:
                raise HostError("revision_conflict", BUBBLE_PREVIEW_AGAIN, field_path="$.args.preview_id", retryable=True)
            if not player and now - ghost[2] < BUBBLE_GHOST_LEAD_S:
                raise HostError("not_ready", BUBBLE_GHOST_LEAD, field_path="$.args.preview_id", retryable=True)
        # The kernel's guard charges by what the place made (BubblePriceKey): a carry on the player its own, a bubble at a point Bubbles'.
        price_key = capability if capability == "bubble_boat" or target is not None else ability.capability
        price = self._guard_energy(price_key, principal) if self._commits_now(principal, message) else 0
        if not apply:
            return {"data": data}
        if preview_id is not None:
            self.bubble_ghosts.pop(preview_id, None)
        bubble = self._form_bubble(capability, principal, message["action_id"], target, centre, radius, now + duration)
        self._charge_energy(price)  # decision 69: Trust reads the charge (mock_trust.py)
        bubble_id = bubble.id
        receipt = {"capability": capability, "bubble": bubble_id, "center_m": centre, "radius_m": radius, "duration_s": duration,
                   "expires_utc": utc(bubble.expires_at), "requires_approval": keyed, "carried": bubble.carried}
        if target is not None:
            receipt["target"] = target
        return {"created": [bubble_id], "affected": [bubble_id] + ([target] if target is not None else []), "data": receipt}

    def _form_bubble(self, capability: str, principal: str, action_id: str, target: str | None, centre: list[float], radius: float,
                     expires_at: float) -> MockBubble:
        """A new bubble; one at a time, so whatever bubble was there pops first (the kernel's FormBubble and RecastEnd: the Gubble's
        own recast is its own pop, one the player said yes to the player's, decision 115). A bubble cast on a fire teaches Snuff
        (decision 79; mock_nature.py)."""
        gubble = not principal.startswith("player:")
        approved = gubble and self._approving
        for old in list(self.bubbles.values()):
            self._end_bubble(old, "popped_by_player" if approved else "popped", by_gubble=gubble and not approved)
        bubble_id = "effect:" + "".join(secrets.choice(EFFECT_ID_ALPHABET) for _ in range(EFFECT_ID_LENGTH))
        bubble = MockBubble(bubble_id, capability, principal, action_id, target, centre, radius, expires_at,
                            holding=target is not None, carried=target is not None)
        self.bubbles[bubble_id] = bubble
        self._bubble_on_fire(centre, radius)
        return bubble

    def _bubble_effect_start(self, principal: str, message: dict, apply: bool, plan: dict) -> dict:
        """effect.start of Bubbles, checked by _plan_effect: one player-sized bubble at the spot, the place deciding what it is
        (the kernel's BubbleEffectStart). The box world has no water, so it is a carry on the player or a bubble to walk into."""
        preview = message.get("preview") is True
        player = principal.startswith("player:")
        spot = list(plan["centre"])
        body = self.entities.get(self.avatars[PLAYER])
        if body is None or body.removed:
            raise HostError("not_ready", "The player's body is not in the room yet.", retryable=True)
        radius = BUBBLE_CARRY_RADIUS_DEFAULT_M
        # On the player: their feet within reach of the spot, or a bubble to walk into that would form round them (the kernel's
        # PlaceTheBubble judges the walk-in where it forms), so a carry, the companion's previewed first.
        on_them = (math.dist(body.position, spot) <= radius + BUBBLE_ENVELOP_REACH_M
                   or not self._clear_of_the_player(self._walk_in_centre(spot, radius), radius, body.position))
        target = self.avatars[PLAYER] if on_them else None
        kind = "carry" if target is not None else "walk_in"
        duration = plan["duration_s"]
        signature = ("effect.start:" + plan["ability"].capability, tuple(spot), radius, duration, target or "")
        data = {"capability": plan["ability"].capability, "category": plan["ability"].category, "target": "point", "kind": kind,
                "params": {}, "area": {"center_m": spot, "radius_m": radius}, "duration_s": duration}
        now = self.clock.now()
        if preview:
            if target is not None:
                self._show_ghost(principal, signature, now)  # the ghost the player sees round them is the consent
            return {"data": data}
        ghost = None
        if target is not None:
            ghost = next((t for t, g in self.bubble_ghosts.items() if g[0] == principal and g[1] == signature and g[3] > now), None)
            if not player and not self._approving:
                if ghost is None:
                    raise HostError("revision_conflict", BUBBLE_GHOST_FIRST, field_path="$.args.area.center_m", retryable=True)
                if now - self.bubble_ghosts[ghost][2] < BUBBLE_GHOST_LEAD_S:
                    raise HostError("not_ready", BUBBLE_GHOST_LEAD, field_path="$.args.area.center_m", retryable=True)
        price_key = "bubble_carry" if target is not None else plan["ability"].capability
        price = self._guard_energy(price_key, principal) if self._commits_now(principal, message) else 0
        if not apply:
            return {"data": data}
        if ghost is not None:
            self.bubble_ghosts.pop(ghost, None)
        bubble = self._form_bubble("bubble_carry", principal, message["action_id"], target, spot, radius, now + duration)
        self._charge_energy(price)  # the kernel's guard charges by what the place made (BubblePriceKey); Trust reads it
        data.update(effect=bubble.id, bubble=bubble.id, expires_utc=utc(bubble.expires_at), carried=bubble.carried)
        return {"created": [bubble.id], "affected": [bubble.id] + ([target] if target is not None else []), "data": data}

    # ------------------------------------------------------------------ the Gubble's lead (goal.set lead; the kernel's CommandHostBubbleLead)

    def _lead(self, principal: str, message: dict, actor: str, apply: bool) -> dict:
        """goal.set lead: the bubble by its id (the companion names only one the team sees now), the spot inside the island; then the
        Gubble's goal is replaced and the lead starts as a job. A spot higher than the bubble is a lift, which the setter's pool must
        be able to pay (the kernel's LeadRefusal before anything moves)."""
        args = message["args"]
        player = principal.startswith("player:")
        bubble = self.bubbles.get(args["target"])
        if bubble is None or (not player and not self._approving and not self._team_sees_spot(bubble.centre, principal, GUBBLE)):
            raise _not_found("$.args.target")
        spot = [_r(v) for v in args["position_m"]]
        if not self._inside_island(spot):
            raise HostError("out_of_bounds", "That spot is outside the island.", field_path="$.args.position_m")
        lift = spot[1] - bubble.centre[1] > BUBBLE_LIFT_ABOVE_DRIFT_M
        data = {"actor": actor, "goal": "lead", "bubble": bubble.id, "position_m": spot, "lift": lift}
        if not player:
            data["target_seen"] = "now"
        if lift:
            # No pips, no rise: a lift the setter's pool cannot pay now is refused before anything moves (the kernel's LeadRefusal).
            try:
                self._guard_energy("bubble_lift", principal)
            except HostError as refusal:
                raise _lead_refusal(refusal) from None
            data["lift_price"] = self._trust_price("bubble_lift", principal)
        outcome = {"affected": [actor, bubble.id], "data": data}
        if not apply:
            return {"data": data}
        if bubble.lead is not None:
            # A new lead of the same bubble re-aims it: the old lead ends (stopped), and the bubble stays.
            self._update_lead_receipt(bubble.lead, "ended", "stopped")
            bubble.lead = None
        self._cancel_job(actor, by=principal)
        job_id = self._start_job(principal, actor, message["action_id"], "lead", bubble.id)
        self.goals[actor] = {"goal": "lead", "target": bubble.id, "position_m": spot, "job_id": job_id, "set_by": principal,
                             "started_utc": utc(self.clock.now())}
        bubble.lead = {"job_id": job_id, "principal": principal, "action_id": message["action_id"], "spot": spot, "lift": lift}
        outcome["job_id"] = job_id
        return outcome

    def _led_bubble(self, job_id: str) -> MockBubble | None:
        return next((b for b in self.bubbles.values() if b.lead is not None and b.lead["job_id"] == job_id), None)

    def _update_lead_receipt(self, lead: dict, field_name: str, value: Any) -> None:
        """How a lead went, written into its goal.set receipt (a replay and receipt.lookup answer it so)."""
        receipt = self.transient.get((lead["principal"], lead["action_id"]))
        if receipt is not None and receipt.result.get("job_id") == lead["job_id"]:
            receipt.result.setdefault("data", {})[field_name] = value

    def _end_lead(self, bubble: MockBubble, word: str, error: HostError | None) -> None:
        """A lead ends: its receipt says how, and the Gubble's job finishes (succeeded on arrival, failed with why otherwise)."""
        lead, bubble.lead = bubble.lead, None
        if lead is None:
            return
        self._update_lead_receipt(lead, "ended", word)
        goal = self.goals.get(GUBBLE)
        job = self.jobs.get(lead["job_id"])
        if goal is not None and goal.get("job_id") == lead["job_id"] and job is not None and job["state"] == "running":
            self._finish_job(GUBBLE, goal, job, error)

    def _lead_arrived(self, actor_id: str, goal: dict, job: dict) -> str:
        """The bubble reached its spot (the mock has no drift: the game's goal runner says so): it is there, and the lead succeeded."""
        bubble = self._led_bubble(goal["job_id"])
        if bubble is None:
            return self._finish_job(actor_id, goal, job, HostError("target_not_found", LEAD_POPPED, field_path="$.args.target"))
        if bubble.lead is not None and bubble.lead["lift"]:
            # The lift's charge at its rise (here, the arrival: no drift), to whoever set the lead; no pips, no rise: the lead ends
            # (no_pips) and the bubble stays where it is (the kernel's StartLift).
            try:
                price = self._guard_energy("bubble_lift", bubble.lead["principal"])
            except HostError as refusal:
                self._end_lead(bubble, "no_pips", _lead_refusal(refusal))
                return job["state"]
            self._charge_energy(price)
            self._trust_lift_charged(bubble.lead)  # decision 69: the Lift's charge at the rise pays the lead
        bubble.centre = list(goal["position_m"])
        self._end_lead(bubble, "arrived", None)
        return job["state"]

    def _lead_blocked(self, actor_id: str, goal: dict, seconds: float) -> str:
        """A led bubble that cannot get on for unreachable_after_s stops where it is: the lead fails (target_unreachable)."""
        goal["blocked_s"] = goal.get("blocked_s", 0.0) + seconds
        if goal["blocked_s"] < self.policy.unreachable_after_s:
            return "running"
        bubble = self._led_bubble(goal["job_id"])
        job = self.jobs[goal["job_id"]]
        error = HostError("target_unreachable", LEAD_BLOCKED, field_path="$.args.position_m")
        if bubble is None:
            return self._finish_job(actor_id, goal, job, error)
        self._end_lead(bubble, "blocked", error)
        return job["state"]

    def _pop_led_bubble(self, actor: str, by_player: bool) -> list[str]:
        """goal.stop of the Gubble: the bubble it leads pops before its goal is cancelled, as the player's own pop when it is the
        player's X (the kernel's PopLedBubble). Returns its id in a list, or an empty list when it leads none."""
        goal = self.goals.get(actor)
        bubble = self._led_bubble(goal.get("job_id", "")) if goal and goal.get("goal") == "lead" else None
        if bubble is None:
            return []
        self._update_lead_receipt(bubble.lead, "ended", "stopped")
        bubble.lead = None
        self._end_bubble(bubble, "popped_by_player" if by_player else "popped", by_gubble=not by_player)
        return [bubble.id]

    def _show_ghost(self, principal: str, signature: tuple, now: float) -> str:
        while len(self.bubble_ghosts) >= BUBBLE_MAX_GHOSTS:
            self.bubble_ghosts.popitem(last=False)
        token = secrets.token_hex(16)
        self.bubble_ghosts[token] = (principal, signature, now, now + BUBBLE_GHOST_LIFETIME_S)
        return token

    def _update_bubble_receipt(self, bubble: MockBubble, field_name: str, value: Any) -> None:
        """What happened to a bubble since its cast, written into its receipt (a replay and receipt.lookup answer it so)."""
        receipt = self.transient.get((bubble.principal, bubble.action_id))
        if receipt is not None and receipt.result.get("data", {}).get("bubble") == bubble.id:
            receipt.result["data"][field_name] = value

    def _end_bubble(self, bubble: MockBubble, ended: str, by_gubble: bool = False) -> None:
        """A bubble ends: its receipt says how. One the Gubble popped by its own action (its stop or effect.stop, a new goal of its
        own ending its lead, its recast) while it held the player leaves that fall pending: if its landing hurts (player_lands),
        that is the Gubble's consequence (decision 96; the kernel's NotePopFallPending)."""
        if self.bubbles.pop(bubble.id, None) is None:
            return
        self._note_pop(bubble.holding, by_gubble)
        bubble.holding = False
        self._update_bubble_receipt(bubble, "ended", ended)
        if bubble.lead is not None:
            # The lead taking it somewhere ends with it: it popped before it got there.
            self._end_lead(bubble, ended, HostError("target_not_found", LEAD_POPPED, field_path="$.args.target"))

    def _pop_bubbles(self, principal: str, which: str) -> list[str]:
        """goal.stop of the Gubble and effect.stop: the bubbles this principal may stop (the player any, a companion its own), one by
        id or all; the player's stop also turns away every ghost. Returns the ids popped (the kernel's PopBubbles)."""
        self._expire_bubbles()
        player = principal.startswith("player:")
        if which == "all":
            if player:
                self.bubble_ghosts.clear()
            else:
                for token in [t for t, ghost in self.bubble_ghosts.items() if ghost[0] == principal]:
                    del self.bubble_ghosts[token]
        popped = [b for b in self.bubbles.values() if (player or b.principal == principal) and (which == "all" or b.id == which)]
        for bubble in popped:
            self._end_bubble(bubble, "popped_by_player" if player else "popped", by_gubble=not player)
        return [b.id for b in popped]

    def _expire_bubbles(self) -> None:
        """A bubble at its duration pops, timed_out; a ghost goes at its lifetime (the host's clock)."""
        now = self.clock.now()
        for bubble in [b for b in self.bubbles.values() if now >= b.expires_at]:
            self._end_bubble(bubble, "timed_out")
        for token in [t for t, ghost in self.bubble_ghosts.items() if ghost[3] <= now]:
            del self.bubble_ghosts[token]

    def player_pops_bubble(self) -> bool:
        """The dive key in the game (the player's body, never the companion's connection): the bubble that holds the player pops,
        popped by the player, and every ghost round them goes. Returns whether a bubble popped."""
        with self._lock:
            self._expire_bubbles()
            self.bubble_ghosts.clear()
            holding = self._bubble_holding()
            if holding is None:
                return False
            self._end_bubble(holding, "popped_by_player")
            return True

    def player_bubble(self, spot: list[float]) -> dict:
        """The player's Bubbles key at a spot (the kernel's PlayerEffect): effect.start of the island's Bubbles, the pack's radius, and
        the Bubble's own time, 8 s (decision 95; the box world has no water, so never the boat's 20 s), within duration_max_s."""
        with self._lock:
            ability = self._bubbles_ability()
            capability = ability.capability if ability is not None else "bubbles"
            radius = ability.area_radius_default_m if ability is not None else 0.5
            duration = min(BUBBLE_CARRY_DURATION_DEFAULT_S, ability.duration_max_s) if ability is not None else 1.0
            self._player_controls = getattr(self, "_player_controls", 0) + 1
            message = {"schema": COMMAND_SCHEMA, "version": 1, "action_id": f"hud-mock-{self._player_controls}", "room_id": self.room_id,
                       "op": "effect.start", "args": {"capability": capability, "params": {},
                                                      "area": {"center_m": [float(v) for v in spot], "radius_m": radius}, "duration_s": duration}}
        return self.handle(PLAYER, message)

    def player_steps_into(self, bubble_id: str) -> bool:
        """The player's own step into a bubble waiting at a point (the game's body; the mock has no physics): the step-in is the
        consent, so it holds them and its receipt says carried."""
        with self._lock:
            self._expire_bubbles()
            bubble = self.bubbles.get(bubble_id)
            if bubble is None or bubble.target is not None or bubble.holding or self._bubble_holding() is not None:
                return False
            bubble.holding = bubble.carried = True
            self._update_bubble_receipt(bubble, "carried", True)
            return True

    def _op_nature_trail(self, principal, message, apply, new_revision):
        raise HostError("unsupported_capability", TRAIL_RETIRED, field_path="$.op")

    def _op_wish_submit(self, principal, message, apply, new_revision):
        if principal != PLAYER:
            raise HostError("permission_denied", "Only the player can start a wish.")
        self._runtime_required("The wish queue and player-started runner")

    def _op_wish_stop(self, principal, message, apply, new_revision):
        if principal != PLAYER:
            raise HostError("permission_denied", "Only the player can stop a wish.")
        self._runtime_required("The wish queue and player-started runner")

    def _op_style_set(self, principal, message, apply, new_revision):
        args = message["args"]
        key = (args["preset_id"], args["preset_version"])
        if key not in self.styles:
            raise HostError("invalid_args", "No style preset with that id and version is installed.", field_path="$.args.preset_id")
        if self.style_status.get(key) not in PINNABLE_STATUSES:
            # contracts/README.md "Styles are versioned files": nothing may pin a seed or draft preset.
            raise HostError("invalid_args", "That preset version is a seed, draft or retired preset and cannot be pinned.",
                            field_path="$.args.preset_version", allowed=sorted(PINNABLE_STATUSES))
        if not apply:
            return {}
        self.style = {"preset_id": key[0], "preset_version": key[1], "preset_sha256": self.styles[key]}
        return {}

    def _op_room_checkpoint(self, principal, message, apply, new_revision):
        # Records the current revision; does not move it, so a checkpoint never stales anyone's
        # expected_revision. Compacts the durable receipts it covers.
        if not apply:
            return {}
        self._checkpoint_counter += 1
        label = self.text.display_text(message["args"]["label"], 80) if message["args"].get("label") else None
        checkpoint = {"id": f"cp{self._checkpoint_counter:04d}", "revision": self.revision}
        if label:
            checkpoint["label"] = label
        self.checkpoints.append(checkpoint)
        del self.checkpoints[:max(0, len(self.checkpoints) - self.policy.max_checkpoints)]
        for key, receipt in list(self.receipts.items()):
            if receipt.result["revision"] <= self.revision:
                self.compacted[key] = Compacted(receipt.fingerprint[:COMPACTED_PREFIX_HEX], receipt.result["op"],
                                                receipt.result["revision"])
                del self.receipts[key]
        while len(self.compacted) > self.policy.max_compacted_receipts:
            self.compacted.popitem(last=False)
        # What the kernel host answers (and replays from the durable receipt): the revision, nothing else.
        return {"data": {"checkpoint_revision": self.revision}}

    def _op_item_pickup(self, principal, message, apply, new_revision):
        """A Fractal's pickup or a gather, the player's alone: refused before it reaches here (_check_unsupported) in the mock."""
        raise HostError("unsupported_capability", MECHANICS_NOT_YET, field_path="$.op")

    _op_knack_set = _op_fractal_spend = _op_item_gather = _op_item_pickup

    def _op_world_set_physics(self, principal, message, apply, new_revision):
        # Player-only: a companion is refused before any handler runs. Not saved in room state, so transient.
        preset = message["args"]["preset"]
        if preset not in PHYSICS_PRESETS:
            raise HostError("invalid_args", "The game has no world physics preset by that name.", field_path="$.args.preset",
                            allowed=sorted(PHYSICS_PRESETS))
        if apply:
            self.physics_preset = preset
        return {}

    def _op_world_set_mode(self, principal, message, apply, new_revision):
        if principal != PLAYER:
            raise HostError("permission_denied", "Only the player chooses the game mode.")
        mode = message["args"]["mode"]
        if mode not in self._glow_profiles:
            raise HostError("invalid_args", "Choose creative, challenge or live.", field_path="$.args.mode")
        if apply:
            self._end_effects(list(self.effects.values()))
            self.mode = mode
        return {}

    # ------------------------------------------------------------------ queries

    def _query(self, principal: str, message: dict) -> dict:
        op = message["op"]
        args = message["args"]
        result = self._base(principal, message)
        if op == "room.describe":
            # Counts cover what the requester perceives: a count of everything would tell a
            # companion how many things are hidden from it.
            counts = {"objects": 0, "creations": 0, "shell_parts": 0}
            for entity in self.entities.values():
                if self._entity(entity.id) is None:
                    continue
                if entity.kind == "object":
                    counts["objects"] += 1
                elif entity.kind == "creation":
                    counts["creations"] += 1
                elif entity.kind == "shell":
                    counts["shell_parts"] += 1
            result["data"] = {
                "room_id": self.room_id, "display_name": self.text.display_text(self.room["display_name"], 80),
                "revision": self.revision, "source_kind": self.room["source"]["kind"], "bounds_m": self.room_bounds,
                "style": dict(self.style), "counts": counts,
            }
        elif op == "entities.list":
            # In sight now, plus what the companion remembers (marked seen: remembered); filters apply
            # to what it saw, never to the entity's true state.
            summaries = {e.id: self._summary(principal, e) for e in self.entities.values() if self._entity(e.id) is not None}
            for entity_id, entry in self._remembered(principal).items():
                summaries[entity_id] = self._remembered_summary(entry)
            items = [summaries[k] for k in sorted(summaries) if _matches(summaries[k], args.get("filter", {}))]
            offset = _cursor(args.get("cursor"))
            limit = args.get("limit", 50)
            result["data"] = {"items": items[offset:offset + limit]}
            if offset + limit < len(items):
                result["data"]["next_cursor"] = str(offset + limit)
        elif op == "entity.inspect":
            entity = self._entity(args["target"])
            entry = self._remembered(principal).get(args["target"]) if entity is None else None
            if entity is not None:
                data: dict = {"entity": self._summary(principal, entity)}
                parts, protected_by = entity.parts, entity.protected_by
            elif entry is not None:
                data = {"entity": self._remembered_summary(entry)}
                parts, protected_by = entry.view["parts"], entry.view["protected_by"]
            else:
                raise _not_found("$.args.target")
            if parts:
                data["parts"] = dict(parts)
            if protected_by:
                data["protected_by"] = protected_by
            result["data"] = data
        elif op == "player.status":
            result["data"] = self._player_status()
        elif op == "nature.describe":
            result["data"] = self._nature_describe(principal)
        elif op == "wishes.read":
            if principal != PLAYER:
                raise HostError("permission_denied", "Only the player can read wishes.")
            self._runtime_required("The wish queue")
        elif op == "capabilities.list":
            # From the island's rules: capability_summary items by capability, filtered by category, paged by a
            # plain decimal offset (the kernel's Capabilities). A cursor it did not give is invalid_args.
            abilities = sorted(self.rules.abilities if self.rules else (), key=lambda a: a.capability)
            if "category" in args:
                abilities = [a for a in abilities if a.category == args["category"]]
            start = 0
            if "cursor" in args:
                cursor = args["cursor"]
                if not (cursor.isascii() and cursor.isdigit()) or int(cursor) > len(abilities):
                    raise HostError("invalid_args", "That cursor is not from this list.", field_path="$.args.cursor")
                start = int(cursor)
            page = abilities[start:start + args.get("limit", CAPABILITIES_DEFAULT_LIMIT)]
            result["data"] = {"items": [{
                "capability": a.capability, "category": a.category,
                "params": {name: {"min": low, "max": high} for name, (low, high, _default) in a.params.items()},
                "area_radius_max_m": a.area_radius_max_m, "duration_max_s": a.duration_max_s,
                "reach_m": a.reach_m,
                "max_active": min(a.max_active, self.policy.max_active_effects,
                                  self.glow_progress.earned_light_limit if a.capability == "glow" else a.max_active,
                                  BUBBLE_MAX if a.primitive == "particles.float" else a.max_active),
                "command_op": "nature.cast" if a.primitive in {"growth.seed", "growth.grow", "element.ignite"} else "effect.start",
                # Decision 72: Fire listed, locked, until the team learns it (the kernel's Capabilities).
                **({"locked": True} if a.primitive == "element.ignite" and not self.fire_learned else {}),
                # Decision 76: Grow is listed with the unlocks the team has not opened yet (the kernel's GrowLockedKinds).
                **({"locked_kinds": self.grow_locked_kinds()} if a.primitive == "growth.grow" and self.grow_locked_kinds() else {})} for a in page]}
            if start + len(page) < len(abilities):
                result["data"]["next_cursor"] = str(start + len(page))
            # P-Inventory: the four inventory ops on the whole list's first answer (mock_inventory.py).
            if (inventory := self._inventory_ops(args)) is not None:
                result["data"]["inventory"] = inventory
        elif op == "observe":
            actor = self._actor(principal, args)
            radius = min(float(args.get("radius_m", self.policy.observe_max_radius_m)), self.policy.observe_max_radius_m)
            # observe is the named avatar's own eyes, never the team's (the team's look for this request already
            # filled the map); the player may look through a companion's eyes too, which fills nothing.
            seen = self.perceived(self._principal_of(actor) or principal)
            origin = self.entities[actor].position
            visible = []
            for entity_id in seen:
                entity = self.entities[entity_id]
                if entity.removed or entity.kind == "shell" or entity.id == actor:
                    continue
                distance = _distance_to_box(origin, entity.bounds())
                if distance <= radius:
                    visible.append((distance, entity.id, entity))
            visible.sort(key=lambda item: (item[0], item[1]))
            visible = visible[:100]
            texts = []
            for _d, _id, entity in visible:
                for text in entity.texts:
                    if len(texts) < 50:
                        texts.append({"source": entity.id, "text": self.text.long_text(text, 500), "untrusted": True})
            result["data"] = {"actor": actor, "visible": [self._summary(principal, e) for _d, _i, e in visible],
                              "texts": texts}
            remembered = sorted((_distance_to_box(origin, entry.view["summary"]["bounds_m"]), entity_id, entry)
                                for entity_id, entry in self._remembered(principal).items())
            remembered = [(d, i, entry) for d, i, entry in remembered if d <= radius][:50]
            if remembered:
                result["data"]["remembered"] = [self._remembered_summary(entry) for _d, _i, entry in remembered]
        elif op == "jobs.status":
            job = self.jobs.get(args["job_id"])
            if job is None or job["principal"] != principal:
                # Unknown and other principals' jobs look identical.
                raise HostError("target_not_found", "No job with that id is running.", field_path="$.args.job_id")
            result["data"] = {"job_id": args["job_id"], "state": job["state"]}
            if "result" in job:
                result["data"]["result"] = copy.deepcopy(job["result"])
        elif op == "receipt.lookup":
            key = (principal, args["action_id"])
            receipt = self.receipts.get(key) or (self.transient.get(key) if key not in self.compacted else None)
            if receipt is not None:
                result["data"] = {"found": True, "receipt": copy.deepcopy(receipt.result)}
            elif key in self.compacted:
                result["data"] = {"found": True, "compacted": True}
            else:
                result["data"] = {"found": False}
        elif op == "approval.status":
            approval = self.approvals.get(args["request_id"])
            if approval is None or (approval.principal != principal and not principal.startswith("player:")):
                # Unknown and other principals' requests look identical. The player sees every request.
                raise HostError("target_not_found", "No approval request with that id is yours.",
                                field_path="$.args.request_id")
            data = {"request_id": approval.request_id, "state": approval.state}
            if approval.result is not None:
                data["result"] = copy.deepcopy(approval.result)
            result["data"] = data
        elif op == "journal.read":
            result["data"] = self._journal_read(principal, args)
        elif op == eyes.OP:
            result["data"] = self._view_look(principal, args)
        elif op == "map.find":
            result["data"] = self._map_find(principal, args)
        else:  # pragma: no cover - the schema already restricts ops
            raise HostError("request_invalid", "Unknown operation.", field_path="$.op")
        return result


def _lead_refusal(refusal: HostError) -> HostError:
    """A lift's refusal at the lead's spot (the kernel's LeadRefusal): the same code and words at $.args.position_m."""
    return HostError(refusal.code, refusal.message, field_path="$.args.position_m", retryable=True if refusal.code == "energy_exhausted"
                     else refusal.retryable, balance=refusal.balance, price=refusal.price)


def _targets(args: dict) -> list[str]:
    found = []
    if isinstance(args.get("target"), str):
        found.append(args["target"])
    for target in args.get("targets", []) if isinstance(args.get("targets"), list) else []:
        if isinstance(target, str):
            found.append(target)
    return found


def _find_authority_key(value: Any, path: list) -> list | None:
    """Path to the first key that names identity or authority, or is not a plain lowercase token."""
    found = find_forbidden_key(value, path, token_keys=True)
    return found[0] if found is not None else None


def _inside(point, bounds, tolerance: float = 1e-3) -> bool:
    return all(bounds["min_m"][i] - tolerance <= point[i] <= bounds["max_m"][i] + tolerance for i in range(3))


def _clamp(value: float) -> float:
    return _r(max(-1000.0, min(1000.0, value)))


def _box_gap(a, b) -> float:
    """The distance between two boxes (0 when they touch or overlap)."""
    total = 0.0
    for i in range(3):
        gap = max(a["min_m"][i] - b["max_m"][i], b["min_m"][i] - a["max_m"][i], 0.0)
        total += gap * gap
    return math.sqrt(total)


def _distance_to_box(point, box) -> float:
    total = 0.0
    for i in range(3):
        low, high = box["min_m"][i], box["max_m"][i]
        nearest = min(max(point[i], low), high)
        total += (point[i] - nearest) ** 2
    return math.sqrt(total)


def _matches(summary: dict, flt: dict) -> bool:
    """An entities.list filter against a summary: what is in sight now, or what was seen."""
    if "kind" in flt and summary["kind"] != flt["kind"]:
        return False
    if "category_group" in flt and summary.get("category_group") != flt["category_group"]:
        return False
    if "affordance" in flt and flt["affordance"] not in summary["affordances"]:
        return False
    if "provenance_kind" in flt and summary["provenance_kind"] != flt["provenance_kind"]:
        return False
    if "near" in flt and _distance_to_box(flt["near"]["center_m"], summary["bounds_m"]) > flt["near"]["radius_m"]:
        return False
    return True


def _cursor(cursor: str | None) -> int:
    if cursor is None:
        return 0
    if not cursor.isascii() or not cursor.isdigit() or len(cursor) > 6:
        raise HostError("invalid_args", "That cursor did not come from this game.", field_path="$.args.cursor")
    return int(cursor)
