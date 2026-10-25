"""Shared test helpers. Results are checked with the integrator's own validator, imported by path."""
from __future__ import annotations

import asyncio
import atexit
import copy
import json
import logging
import os
import shutil
import sys
import tempfile
import threading
from pathlib import Path

COMPANION_DIR = Path(__file__).resolve().parents[1]
REPO = COMPANION_DIR.parent
# GARAGE_GAME_CONTRACTS_DIR points the suite at another copy of contracts/, to check a proposed change.
CONTRACTS = Path(os.environ.get("GARAGE_GAME_CONTRACTS_DIR") or REPO / "contracts").resolve()
EXAMPLES = CONTRACTS / "examples" / "messages"
SRC = COMPANION_DIR / "src"

for path in (str(SRC), str(CONTRACTS)):
    if path not in sys.path:
        sys.path.insert(0, path)

import validate as contract_validate  # noqa: E402  (contracts/validate.py, the integrator's validator)

from garage_game_companion.contract import MEMORY_SUMMARY_FIELDS, Contracts  # noqa: E402
from garage_game_companion.link import LinkServer  # noqa: E402
from garage_game_companion.mock_host import COMPANION, NO_HOLDS, PLAYER, FakeClock, HostPolicy, MockHost  # noqa: E402

_CONTRACTS: Contracts | None = None
_MEMORY_CONTRACTS: Contracts | None = None
# The perception-memory result fields proposed in docs/companion/proposals/contracts-run1.diff.
MEMORY_EXTENSION = COMPANION_DIR / "tests" / "fixtures" / "contract_memory_v1.json"
contract_validate.validator_for("garage_game.result")  # warm the validator once, off any event loop
logging.getLogger("garage_game").setLevel(logging.CRITICAL)  # refusals are asserted, not logged


def contracts() -> Contracts:
    global _CONTRACTS
    if _CONTRACTS is None:
        _CONTRACTS = Contracts(CONTRACTS)
    return _CONTRACTS


def contract_problems(document) -> list[str]:
    """Problems according to contracts/validate.py, independent of the companion package."""
    return contract_validate.schema_errors(document)


def has_memory_fields(command_schema: dict) -> bool:
    properties = command_schema["$defs"]["entity_summary"].get("properties", {})
    return all(key in properties for key in MEMORY_SUMMARY_FIELDS)


def merge_memory_fields(command_schema: dict) -> dict:
    """game-command.schema.json with the proposed perception-memory fields (the same change as the patch)."""
    extension = json.loads(MEMORY_EXTENSION.read_bytes())
    schema = copy.deepcopy(command_schema)
    summary = schema["$defs"]["entity_summary"]
    summary["properties"].update(extension["entity_summary"]["properties"])
    summary["allOf"] = summary.get("allOf", []) + extension["entity_summary"]["allOf"]
    schema["$defs"]["data"]["observe"]["properties"].update(extension["observe"]["properties"])
    return schema


def memory_contracts_dir() -> Path:
    """contracts/ itself once it has the perception-memory fields; until then a temporary copy of its
    schemas and validator with them merged in, so the memory paths are tested on every run."""
    command_schema = json.loads((CONTRACTS / "game-command.schema.json").read_bytes())
    if has_memory_fields(command_schema):
        return CONTRACTS
    copy_dir = Path(tempfile.mkdtemp(prefix="garage-game-memory-contracts-"))
    atexit.register(shutil.rmtree, copy_dir, True)
    for path in list(CONTRACTS.glob("*.schema.json")) + [CONTRACTS / "validate.py"]:
        shutil.copyfile(path, copy_dir / path.name)
    text = json.dumps(merge_memory_fields(command_schema), indent=2, ensure_ascii=False) + "\n"
    (copy_dir / "game-command.schema.json").write_bytes(text.encode("utf-8"))
    return copy_dir


def memory_contracts() -> Contracts:
    """The contracts with the perception-memory result fields (see memory_contracts_dir)."""
    global _MEMORY_CONTRACTS
    if _MEMORY_CONTRACTS is None:
        _MEMORY_CONTRACTS = Contracts(memory_contracts_dir())
    return _MEMORY_CONTRACTS


def memory_contract_problems(document) -> list[str]:
    """Problems according to the integrator's validate.py, loaded from the memory contracts."""
    return memory_contracts().validate.schema_errors(document)


class RecordingHost(MockHost):
    """A mock host that keeps every result it emits, so tests can prove all of them are contract-valid."""

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.emitted: list[dict] = []

    def handle(self, principal, message):
        result = super().handle(principal, message)
        self.emitted.append(copy.deepcopy(result))
        return result


def new_host(policy: HostPolicy | None = None, clock: FakeClock | None = None, *, using: Contracts | None = None,
             **kwargs) -> RecordingHost:
    """A recording mock host on today's contracts, or on `using` (for example memory_contracts())."""
    return RecordingHost(using or contracts(), policy=policy, clock=clock or FakeClock(), **kwargs)


def command(op: str, args: dict, action_id: str, room_id: str = "test_room", **extra) -> dict:
    message = {"schema": "garage_game.command", "version": 1, "action_id": action_id, "room_id": room_id,
               "op": op, "args": args}
    message.update(extra)
    return message


def query(op: str, args: dict, query_id: str = "q-1", room_id: str = "test_room") -> dict:
    return {"schema": "garage_game.query", "version": 1, "query_id": query_id, "room_id": room_id, "op": op, "args": args}


GUBBLE = "avatar:companion"
# Spots in the test room for point glows, from the companion beside the player or at its spawn (both hosts):
IN_THE_OPEN = [0.6, 0.0, 1.0]  # on the rug, in plain sight and within the Gubble's 2 m reach
BEHIND_BOX = [1.45, 0.0, 0.15]  # within reach, but the box hides it from both avatars
OUT_OF_REACH = [-1.6, 0.0, -1.2]  # inside the room, more than 2 m from the Gubble
OUTSIDE_ROOM = [0.6, 0.0, 1.6]  # past the room's far wall (z 1.5)
FAR_IN_SIGHT = [1.7, 0.0, -1.3]  # in plain sight, beyond Glow's and Bubbles' 2 m reach but inside Fireworks' 4 m


def glow(action_id: str, *, at: list[float] | None = None, params: dict | None = None, radius: float = 0.5,
         duration: float = 60, targets: list[str] | None = None, capability: str = "glow", **extra) -> dict:
    """An effect.start of the shipped pack's glow: on the Gubble (targets its own avatar: self) unless `at` names a spot
    (no targets: a point there)."""
    args = {"capability": capability, "params": {"intensity": 0.6} if params is None else params,
            "area": {"center_m": list(at or [0.0, 0.0, 0.0]), "radius_m": radius}, "duration_s": duration}
    if targets is not None:
        args["targets"] = targets
    elif at is None:
        args["targets"] = [GUBBLE]
    return command("effect.start", args, action_id, **extra)


# A duration each of storybook_wild v2's abilities allows (Fireworks lasts at most 6 s).
CAST_DURATION_S = {"glow": 60, "bubbles": 20, "fireworks": 4}


def cast(capability: str, action_id: str, *, duration: float | None = None, **kwargs) -> dict:
    """An effect.start of any of the shipped pack's abilities, shaped as glow() shapes it: on the Gubble unless `at`
    names a spot. The duration defaults to one the ability allows."""
    return glow(action_id, capability=capability, duration=CAST_DURATION_S.get(capability, 4) if duration is None else duration,
                **kwargs)


def example(name: str, kind: str = "valid") -> dict:
    return json.loads((EXAMPLES / kind / f"{name}.json").read_bytes())


def retarget(message: dict, mapping: dict[str, str]) -> dict:
    """Point a garage example at the test room: room id and entity ids."""
    text = json.dumps(message)
    for old, new in mapping.items():
        text = text.replace(old, new)
    return json.loads(text)


GARAGE_TO_TEST_ROOM = {"garage_example": "test_room", "obj:paint_clutter": "obj:box", "obj:bean_bag": "obj:box",
                       "obj:shelving_right": "obj:table", "obj:shelving_left": "obj:table"}


class ThreadedGame:
    """The mock game in its own thread and event loop, behind a real loopback link and session file."""

    def __init__(self, host: MockHost, session_dir: Path | None = None):
        self.host = host
        self._tmp = None
        if session_dir is None:
            self._tmp = tempfile.TemporaryDirectory(prefix="garage-game-companion-")
            session_dir = Path(self._tmp.name)
        self.session_path = Path(session_dir) / "session.json"
        self.loop = asyncio.new_event_loop()
        self.server: LinkServer | None = None
        self._thread = threading.Thread(target=self.loop.run_forever, daemon=True)

    def __enter__(self) -> "ThreadedGame":
        self._thread.start()
        self.server = LinkServer(self.host.handle, self.host.room_id, on_session=self.host.session_event)
        asyncio.run_coroutine_threadsafe(self.server.start(self.session_path), self.loop).result(10)
        return self

    def __exit__(self, *exc) -> None:
        asyncio.run_coroutine_threadsafe(self.server.close(), self.loop).result(10)
        self.loop.call_soon_threadsafe(self.loop.stop)
        self._thread.join(10)
        self.loop.close()
        if self._tmp is not None:
            self._tmp.cleanup()
