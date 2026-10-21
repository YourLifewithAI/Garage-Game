"""Player-started vendor-neutral wish runner. The game never launches this program.

A configured, trusted local adapter speaks bounded JSON lines. Its model receives only game tools;
all requested calls pass through the existing MCP Adapter and the game's normal command authority.
No shell, filesystem, browser, arbitrary model prose, or provider credentials travel this protocol.
"""
from __future__ import annotations

import argparse
import asyncio
import contextlib
import copy
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass

from . import canonical
from .contract import Contracts, DEFAULT_REPO_ROOT
from .link import LinkClient, LinkError, default_session_path
from .server import Adapter

PROTOCOL = "enfractal.wish_adapter"
MAX_LINE_BYTES = 65_536
MAX_TOOL_CALLS = 32
# Only implemented game verbs are relayed. Creations remain data for the host's bounded compiler;
# their displayed name is curated below. Journal prose and outside-computer APIs are never offered.
GAME_TOOLS = frozenset({
    "observe", "entities_list", "entity_inspect", "room_describe", "map_find", "capabilities_list",
    "receipt_lookup", "jobs_status", "approval_status", "goal_set", "goal_stop", "effect_start", "effect_stop",
    "entity_grab", "entity_release", "entity_place", "entity_push", "entity_remove", "protect_lock", "room_undo",
    "effect_adjust", "nature_cast", "nature_trail", "nature_repair", "nature_describe", "player_status",
    "creation_place", "creation_revise", "creation_activate",
})
_WORLD_WORDS = frozenset({"text", "texts", "display_name", "description", "line", "message", "reason", "name", "category"})
_CALL_ID = re.compile(r"[A-Za-z0-9_-]{1,48}\Z")
_WISH_ID = re.compile(r"wish:[a-z2-7]{26}\Z")


class RunnerError(Exception):
    """Fixed diagnostics only; never relay subprocess output into the game or terminal."""


class WishCancelled(RunnerError):
    pass


@dataclass(frozen=True)
class ClientConfig:
    command: tuple[str, ...]
    timeout_s: float = 120
    max_tool_calls: int = MAX_TOOL_CALLS
    kind: str = "jsonl"

    @classmethod
    def load(cls, path: Path) -> "ClientConfig":
        try:
            raw = path.read_bytes()
            if len(raw) > 16_384:
                raise ValueError()
            value = json.loads(raw, object_pairs_hook=_unique)
            if not isinstance(value, dict) or set(value) - {"version", "kind", "command", "timeout_s", "max_tool_calls"} or value.get("version") != 1:
                raise ValueError()
            command = value["command"]
            if not isinstance(command, list) or not 1 <= len(command) <= 32 or any(not isinstance(part, str) or not part or len(part) > 4096 or "\0" in part for part in command):
                raise ValueError()
            executable = Path(command[0])
            if not executable.is_absolute() or not executable.is_file() or executable.stem.lower() in {"cmd", "powershell", "pwsh", "bash", "sh", "zsh"}:
                raise ValueError()
            timeout, calls = value.get("timeout_s", 120), value.get("max_tool_calls", MAX_TOOL_CALLS)
            kind = value.get("kind", "jsonl")
            if type(timeout) not in (int, float) or not 1 <= timeout <= 600 or type(calls) is not int or not 1 <= calls <= MAX_TOOL_CALLS:
                raise ValueError()
            if kind not in {"jsonl", "claude-cli"} or kind == "claude-cli" and len(command) != 1:
                raise ValueError()
            return cls(tuple(command), float(timeout), calls, kind)
        except (OSError, ValueError, KeyError, TypeError):
            raise RunnerError("The client configuration is invalid. Use an absolute adapter executable and bounded options.") from None


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate key")
        value[key] = item
    return value


def game_data(value):
    """Keep typed game state, while omitting free prose from observations and refusal text."""
    if isinstance(value, dict):
        return {key: game_data(item) for key, item in value.items() if key not in _WORLD_WORDS}
    if isinstance(value, list):
        return [game_data(item) for item in value]
    return value


class WishRunner:
    def __init__(self, config: ClientConfig, runner_link: LinkClient, game: Adapter, *, poll_s: float = 0.25):
        self.config, self.runner_link, self.game = config, runner_link, game
        self.poll_s = poll_s
        self.tools = {name: spec for name, spec in game.specs.items() if name in GAME_TOOLS}

    def tool_inventory(self):
        offered = []
        for name, spec in self.tools.items():
            schema = copy.deepcopy(spec.input_schema)
            # Advertise the interface the model actually uses, before the runner injects identity.
            for owned in ("action_id", "note"):
                schema.get("properties", {}).pop(owned, None)
            schema["required"] = [field for field in schema.get("required", []) if field not in {"action_id", "note"}]
            description = spec.description
            source = schema.get("properties", {}).get("source")
            if source is not None:
                source.get("properties", {}).get("name", {}).update({"const": "Wish creation"})
                description += (
                    ' The displayed source.name is always "Wish creation". For a static structure, use nodes:[] and edges:[]. '
                    'Each part has id, shape, position_m:[x,y,z], rotation_deg:[x,y,z], size_m:[x,y,z] and material. '
                    'Shapes: box, sphere, cylinder, wing, rotor. Materials: stone, wood, copper, leaf. '
                    'Part positions are local metres in [-4,4], sizes in [0.1,4], rotations in [-180,180]. '
                    'A ground creation must keep every rotated part above its local origin. '
                    'The host recompiles and checks source, placement, visibility, costs and approvals; source is never code.'
                )
            offered.append({"name": name, "description": description, "input_schema": schema})
        return offered

    async def connect(self):
        ready = await self.runner_link.connect()
        if ready.get("principal") != "runner:local":
            await self.runner_link.close()
            raise RunnerError("This is not the game's wish-runner endpoint.")

    async def wait(self):
        answer = await self.runner_link.request({"op": "wish.wait"})
        if answer.get("ok") is not True:
            raise RunnerError("The game's wish journal is not ready.")
        wish = answer.get("wish")
        if wish is not None:
            if not isinstance(wish, dict) or set(wish) - {"wish_id", "text", "room_id", "aim_id"} or not _WISH_ID.fullmatch(wish.get("wish_id", "")) or not isinstance(wish.get("text"), str):
                raise RunnerError("The game sent an invalid wish.")
        return wish

    async def update(self, wish_id: str, status: str):
        return await self.runner_link.request({"op": "wish.update", "args": {"wish_id": wish_id, "status": status, "result_code": status}})

    async def run(self, *, once=False):
        await self.connect()
        try:
            while True:
                wish = await self.wait()
                if wish is None:
                    if once:
                        return
                    await asyncio.sleep(self.poll_s)
                    continue
                await self.run_wish(wish)
                if once:
                    return
        finally:
            await self.game.link.close()
            await self.runner_link.close()

    async def run_wish(self, wish: dict):
        deadline = asyncio.get_running_loop().time() + self.config.timeout_s
        # A watcher notices the player's stop even while the AI process is thinking without output.
        work = asyncio.create_task(self._invoke(wish))
        watch = asyncio.create_task(self._watch(wish["wish_id"]))
        status = "failed"
        try:
            finished, _ = await asyncio.wait({work, watch}, timeout=self.config.timeout_s, return_when=asyncio.FIRST_COMPLETED)
            if watch in finished:
                await watch
            if work not in finished:
                raise RunnerError("The wish adapter exceeded its time limit.")
            status = await work
        except WishCancelled:
            return
        except (RunnerError, LinkError, OSError):
            await self._stop_work(wish["wish_id"])
        finally:
            for task in (work, watch):
                if not task.done():
                    task.cancel()
            await asyncio.gather(work, watch, return_exceptions=True)
        await self._report(wish["wish_id"], status, deadline)

    async def _report(self, wish_id, status, deadline):
        """The host accepts a completion only after it has seen every accepted action end (a goal job may still be
        walking); until then it answers not_ready. Ask again until the wish's time limit, then report a failure, which
        ends the still-running goal. A player Stop meanwhile makes the wish unknown to the host, which also ends this."""
        loop = asyncio.get_running_loop()
        while True:
            answer = await self.update(wish_id, status)
            error = answer.get("error") if isinstance(answer, dict) else None
            if status != "completed" or not isinstance(error, dict) or error.get("code") != "not_ready":
                return answer
            if loop.time() >= deadline:
                return await self.update(wish_id, "failed")
            await asyncio.sleep(self.poll_s)

    async def _watch(self, wish_id):
        while True:
            await asyncio.sleep(self.poll_s)
            # Do not abandon an in-flight framed request: its reply would otherwise be mistaken for
            # the following update. Link requests already have a bounded timeout.
            request = asyncio.create_task(self.wait())
            try:
                current = await asyncio.shield(request)
            except asyncio.CancelledError:
                with contextlib.suppress(RunnerError, LinkError):
                    await request
                raise
            if current is None or current["wish_id"] != wish_id:
                raise WishCancelled("The player stopped this wish.")

    async def _stop_work(self, wish_id):
        if not self.game.link.connected:
            return
        with contextlib.suppress(Exception):
            await self.game.call("goal_stop", {"action_id": "w-" + wish_id[5:] + "-stop"})

    async def _invoke(self, wish):
        if self.config.kind == "claude-cli":
            return await self._invoke_claude(wish)
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        # The adapter starts in an empty scratch folder, never in the game repository. Its executable and
        # arguments come only from the player's local configuration, never from a wish or model output.
        with tempfile.TemporaryDirectory(prefix="garage-game-wish-") as folder:
            child = await asyncio.create_subprocess_exec(*self.config.command, stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL, cwd=folder, limit=MAX_LINE_BYTES,
                creationflags=flags)
            try:
                await self._send(child, {"type": "initialize", "protocol": PROTOCOL, "version": 1,
                    "tools": self.tool_inventory()})
                ready = await self._read(child)
                advertised = ready.get("tools")
                if set(ready) != {"type", "protocol", "version", "exclusive", "tools"} or ready.get("type") != "ready" or ready.get("protocol") != PROTOCOL or ready.get("version") != 1 or ready.get("exclusive") is not True or not isinstance(advertised, list) or not advertised or any(not isinstance(name, str) or name not in self.tools for name in advertised) or len(set(advertised)) != len(advertised):
                    raise RunnerError("The adapter did not verify an exclusive game-only tool inventory.")
                # No player intent is sent before the adapter's tool inventory has passed inspection.
                await self._send(child, {"type": "wish", "wish": {key: value for key, value in wish.items() if key in {"text", "aim_id"}}})
                calls = set()
                for index in range(self.config.max_tool_calls + 1):
                    frame = await self._read(child)
                    if frame.get("type") == "done" and set(frame) == {"type", "status"} and isinstance(frame["status"], str) and frame["status"] in {"completed", "failed", "unsupported"}:
                        return frame["status"]
                    if index == self.config.max_tool_calls:
                        raise RunnerError("This wish reached its tool-call limit.")
                    if set(frame) != {"type", "id", "name", "arguments"} or frame.get("type") != "tool_call" or not isinstance(frame.get("id"), str) or not _CALL_ID.fullmatch(frame["id"]) or frame["id"] in calls or frame.get("name") not in advertised or not isinstance(frame.get("arguments"), dict):
                        raise RunnerError("The adapter sent an invalid tool call.")
                    calls.add(frame["id"])
                    await self._send(child, await self._relay(wish, frame, index))
            finally:
                if child.returncode is None:
                    with contextlib.suppress(ProcessLookupError):
                        child.kill()
                await child.wait()

    async def _relay(self, wish, frame, index):
        name, args = frame["name"], copy.deepcopy(frame["arguments"])
        if any(field in args for field in ("action_id", "note")):
            raise RunnerError("The runner owns action identities and accepts no model prose.")
        if self.tools[name].kind == "command":
            args["action_id"] = "w-" + wish["wish_id"][5:] + f"-{index}"
        if name in {"creation_place", "creation_revise"} and isinstance(args.get("source"), dict):
            # All remaining creation source is checked by the existing contract/compiler. The only
            # display-text field is fixed here, so model words cannot become a name in a child's game.
            args["source"]["name"] = "Wish creation"
        try:
            result = await self.game.call(name, args)
        except asyncio.CancelledError:
            # A Stop can arrive while the host's framed reply is still in flight. This connection
            # cannot be reused with that unread reply; the next wish gets a fresh authenticated one.
            await self.game.link.close()
            raise
        return {"type": "tool_result", "id": frame["id"], "result": game_data(result)}

    async def _invoke_claude(self, wish):
        # Optional native CLI path, API/provider authentication only: documented --bare intentionally
        # does not read subscription OAuth. The runner never reads or copies any credential.
        transcript = [{"type": "wish", "wish": {key: value for key, value in wish.items() if key in {"text", "aim_id"}}}]
        tools = self.tool_inventory()
        calls = set()
        with tempfile.TemporaryDirectory(prefix="enfractal-claude-") as folder:
            for index in range(self.config.max_tool_calls + 1):
                payload = json.dumps({"tools": tools, "conversation": transcript}, ensure_ascii=True)
                if len(payload.encode("utf-8")) > 262_144:
                    raise RunnerError("This wish exceeded its conversation limit.")
                frame = await self._claude_turn(payload, folder)
                if frame.get("type") == "done" and set(frame) == {"type", "status"} and isinstance(frame["status"], str) and frame["status"] in {"completed", "failed", "unsupported"}:
                    return frame["status"]
                if index == self.config.max_tool_calls or set(frame) != {"type", "id", "name", "arguments"} or frame.get("type") != "tool_call" or not isinstance(frame.get("id"), str) or not _CALL_ID.fullmatch(frame["id"]) or frame["id"] in calls or not isinstance(frame.get("name"), str) or frame["name"] not in self.tools or not isinstance(frame.get("arguments"), dict):
                    raise RunnerError("The CLI returned an invalid game action.")
                calls.add(frame["id"])
                transcript.extend([frame, await self._relay(wish, frame, index)])

    async def _claude_turn(self, payload, folder):
        child = await asyncio.create_subprocess_exec(*claude_command(self.config.command[0]), stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL, cwd=folder, limit=MAX_LINE_BYTES,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        verified = False
        try:
            child.stdin.write(payload.encode("utf-8"))
            await child.stdin.drain()
            child.stdin.close()
            while True:
                event = await self._read(child)
                if event.get("type") == "system" and event.get("subtype") == "init":
                    verify_claude_inventory(event)
                    verified = True
                elif event.get("type") == "assistant":
                    message = event.get("message")
                    if not isinstance(message, dict) or not isinstance(message.get("content"), list):
                        raise RunnerError("The CLI sent an invalid message.")
                    content = message["content"]
                    if any(part.get("type") == "tool_use" for part in content if isinstance(part, dict)):
                        raise RunnerError("The CLI attempted a tool outside the game relay.")
                elif event.get("type") == "result":
                    if not verified or event.get("is_error") is not False or event.get("subtype") != "success" or not isinstance(event.get("result"), str):
                        raise RunnerError("The CLI did not return a verified game decision.")
                    try:
                        frame = canonical.loads_strict(event["result"])
                        if not isinstance(frame, dict):
                            raise ValueError()
                        return frame
                    except (ValueError, canonical.CanonicalJsonError):
                        raise RunnerError("The CLI did not return one game protocol object.") from None
        finally:
            if child.returncode is None:
                with contextlib.suppress(ProcessLookupError):
                    child.kill()
            await child.wait()

    @staticmethod
    async def _send(child, frame):
        data = (json.dumps(frame, ensure_ascii=True, separators=(",", ":")) + "\n").encode("utf-8")
        if len(data) > 262_144:
            raise RunnerError("The adapter frame is too large.")
        child.stdin.write(data)
        await child.stdin.drain()

    @staticmethod
    async def _read(child):
        try:
            raw = await child.stdout.readline()
            if not raw or len(raw) > MAX_LINE_BYTES:
                raise ValueError()
            frame = json.loads(raw, object_pairs_hook=_unique, parse_constant=lambda value: (_ for _ in ()).throw(ValueError()))
            if not isinstance(frame, dict):
                raise ValueError()
            canonical.canonical_bytes(frame)
            return frame
        except (ValueError, TypeError, canonical.CanonicalJsonError):
            raise RunnerError("The adapter sent an invalid JSON frame.") from None


def main(argv=None):
    parser = argparse.ArgumentParser(description="Run queued game wishes through a player-configured, game-only AI adapter.")
    client_options = parser.add_mutually_exclusive_group(required=True)
    client_options.add_argument("--client", type=Path, help="Local JSON adapter configuration; never a provider credential file")
    client_options.add_argument("--write-demo-client", type=Path, help="Write a local no-provider demo configuration, without starting anything")
    parser.add_argument("--session", type=Path, default=default_session_path(), help="The game's ordinary companion session file")
    parser.add_argument("--runner-session", type=Path, help="Defaults to wish-runner.json beside --session")
    parser.add_argument("--once", action="store_true", help="Process at most one queued wish, then exit")
    args = parser.parse_args(argv)
    try:
        if args.write_demo_client:
            args.write_demo_client.parent.mkdir(parents=True, exist_ok=True)
            args.write_demo_client.write_text(json.dumps({"version": 1, "kind": "jsonl", "command": [sys.executable,
                str(Path(__file__).with_name("wish_demo_adapter.py"))], "timeout_s": 20, "max_tool_calls": 4}, indent=2) + "\n", encoding="utf-8")
            return 0
        config = ClientConfig.load(args.client)
        runner = LinkClient.from_file(args.runner_session or args.session.with_name("wish-runner.json"))
        game = Adapter(Contracts(DEFAULT_REPO_ROOT / "contracts"), LinkClient.from_file(args.session))
        asyncio.run(WishRunner(config, runner, game).run(once=args.once))
    except (RunnerError, LinkError, OSError) as error:
        print(str(error) if isinstance(error, (RunnerError, LinkError)) else "The local wish runner could not start.", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 0
    return 0


def claude_command(executable):
    """Fixed flags from official CLI/headless docs, checked 2026-10-11; no caller-supplied CLI flags."""
    return [executable, "--bare", "-p", "--tools", "", "--disallowedTools", "*", "--strict-mcp-config",
            "--disable-slash-commands", "--setting-sources", "", "--no-session-persistence", "--no-chrome",
            "--max-turns", "1", "--output-format", "stream-json", "--verbose", "--system-prompt",
            'Choose one next Garage Game action using only the supplied game tool schemas and typed results. '
            'Return exactly one JSON object: {"type":"tool_call","id":"unique-short-id","name":"tool_name","arguments":{...}} '
            'or {"type":"done","status":"completed|failed|unsupported"}. Use one of those three status values. '
            'Never include action_id, note, prose, markdown, shell commands, files or URLs. '
            'Use preview:true to inspect a proposed action without committing. Fire requires a preview, then a separate '
            'decision to commit with the returned preview_id; a preview is never success or player approval. '
            'Do not claim completion until game receipts and any goal job status verify the intended action. '
            'Unknown capabilities are unsupported. Only the player approves held actions in the game.']


def verify_claude_inventory(event):
    if event.get("tools") != [] or event.get("mcp_servers") != [] or event.get("plugins", []) != [] or event.get("plugin_errors") or event.get("mcp_server_errors"):
        raise RunnerError("The CLI did not verify an empty native tool inventory.")


if __name__ == "__main__":
    raise SystemExit(main())
