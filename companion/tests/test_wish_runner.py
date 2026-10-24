"""Vendor-neutral runner: real authenticated loopback links and a local fake JSONL adapter, no provider calls."""
from __future__ import annotations

import asyncio
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock, patch

from jsonschema import Draft202012Validator

from support import NO_HOLDS, contracts, new_host

from garage_game_companion import ids
from garage_game_companion.link import LinkClient, LinkServer, read_frame
from garage_game_companion.server import Adapter
from garage_game_companion import wish_demo_adapter
from garage_game_companion.wish_runner import ClientConfig, RunnerError, WishRunner, claude_command, game_data, verify_claude_inventory


FAKE_ADAPTER = r'''
import json, sys, time
mode = sys.argv[1]
def send(value):
    print(json.dumps(value), flush=True)
hello = json.loads(sys.stdin.readline())
if hello["protocol"] != "garage_game.wish_adapter":
    sys.exit(3)  # the runner names the new id
names = [tool["name"] for tool in hello["tools"]]
# An adapter from before the rename answers the old id (decision C); a misspelt one, neither.
protocol = {"light-before-the-rename": "enfractal.wish_adapter", "light-misspelt": "garagegame.wish_adapter"}.get(mode, "garage_game.wish_adapter")
send({"type":"ready","protocol":protocol,"version":1,"exclusive":True,
      "tools":["effect_start", "shell"] if mode == "unsafe" else
              ["creation_place"] if mode in ("creation", "unsafe-source") else ["effect_start"]})
wish = sys.stdin.readline()
if not wish:
    sys.exit(0)
if mode == "idle":
    time.sleep(30)
if mode == "done-only":
    send({"type":"done","status":"completed"})
elif mode == "prose":
    send({"type":"done","status":"completed","text":"Arbitrary model words"})
elif mode == "huge":
    print("x" * 70000, flush=True)
elif mode == "bad-status":
    send({"type":"done","status":[]})
elif mode == "unsafe-api":
    send({"type":"tool_call","id":"outside","name":"fetch_url","arguments":{"url":"https://example.invalid"}})
elif mode in ("creation", "unsafe-source"):
    source = {"schema":"garage_game.creation","version":1,"name":"Unreviewed model words","seed":1,"mount":"ground",
              "parts":[{"id":"base","shape":"box","position_m":[0,0.2,0],"rotation_deg":[0,0,0],
                        "size_m":[0.4,0.4,0.4],"material":"wood"}],"nodes":[],"edges":[]}
    if mode == "unsafe-source":
        source["script"] = "fetch_url https://example.invalid"
    send({"type":"tool_call","id":"build","name":"creation_place",
          "arguments":{"source":source,"placement":{"position_m":[0.5,0,0.5]}}})
    result = json.loads(sys.stdin.readline())
    send({"type":"done","status":"completed"})
else:
    args = {"capability":"glow", "targets":["avatar:companion"],"params":{},
            "area":{"center_m":[0,0,0],"radius_m":0.5},"duration_s":30}
    if mode == "identity":
        args["action_id"] = "provider-picked"
    if mode == "preview":
        args["preview"] = True
    send({"type":"tool_call","id":"light","name":"effect_start","arguments":args})
    result = json.loads(sys.stdin.readline())
    send({"type":"done","status":"completed"})
'''


class Control:
    def __init__(self, count=1):
        self.queued = [{"wish_id": "wish:" + letter * 26, "text": "Make a light", "room_id": "test_room"}
                       for letter in "abc"[:count]]
        self.active = None
        self.finished = []
        self.verified = []
        self.running_jobs = 0  # completion reports the host answers not_ready, as while a goal job still runs
        self.reports = []

    def handle(self, principal, message):
        assert principal == "runner:local"
        if message == {"op": "wish.wait"}:
            if self.active is None and self.queued:
                self.active = self.queued.pop(0)
            return {"ok": True, "wish": self.active}
        if message.get("op") == "wish.update":
            args = message["args"]
            self.reports.append(args["status"])
            if self.active is None or args["wish_id"] != self.active["wish_id"]:
                return {"ok": False, "error": {"code": "target_not_found"}}
            if args["status"] == "completed" and self.running_jobs:
                self.running_jobs -= 1
                return {"ok": False, "error": {"code": "not_ready"}}
            status = "granted" if args["status"] == "completed" and self.active["wish_id"] in self.verified else "failed"
            self.finished.append((self.active["wish_id"], status))
            self.active = None
            return {"ok": True, "status": status}
        return {"ok": False, "error": {"code": "permission_denied"}}


class DelayedGameLinkServer(LinkServer):
    """The real authenticated framing, with one reply held to exercise a player Stop race."""
    def __init__(self, handler):
        super().__init__(handler, "test_room")
        self.received = asyncio.Event()
        self.release = asyncio.Event()
        self.ended = asyncio.Event()
        self.on_session = lambda principal, event: self.ended.set() if event == "end" else None
        self.delay_once = True

    async def _requests(self, reader, writer):
        while True:
            try:
                frame = await read_frame(reader, self.max_request_frame)
            except (asyncio.IncompleteReadError, ConnectionError, OSError):
                return
            result = self.handler(self.principal, frame["message"])
            if self.delay_once and frame["message"].get("op") == "effect.start":
                self.delay_once = False
                self.received.set()
                await self.release.wait()
            try:
                await self._send(writer, {"type": "response", "seq": frame["seq"], "message": result})
            except (ConnectionError, OSError):
                return


class WishRunnerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="garage-game-runner-test-")
        self.script = Path(self.folder.name) / "adapter.py"
        self.script.write_text(FAKE_ADAPTER, encoding="utf-8")
        self.control = Control()
        self.host = new_host(policy=NO_HOLDS)
        self.commands = []

        def game_handle(principal, message):
            result = self.host.handle(principal, message)
            if ids.is_schema(message.get("schema"), "command"):
                self.commands.append(message)
                if result.get("ok") and not result.get("preview") and message["op"] in {"effect.start", "creation.place"} and self.control.active:
                    self.control.verified.append(self.control.active["wish_id"])
            return result

        self.game_server = LinkServer(game_handle, "test_room")
        self.control_server = LinkServer(self.control.handle, "test_room", principal="runner:local", avatar="")
        await self.game_server.start()
        await self.control_server.start()
        self.clients = []

    async def asyncTearDown(self):
        for runner in self.clients:
            await runner.game.link.close()
            await runner.runner_link.close()
        await self.game_server.close()
        await self.control_server.close()
        self.folder.cleanup()

    def runner(self, mode, timeout=5):
        adapter = Adapter(contracts(), LinkClient(lambda: self.game_server.session))
        config = ClientConfig((sys.executable, str(self.script), mode), timeout_s=timeout)
        runner = WishRunner(config, LinkClient(lambda: self.control_server.session), adapter, poll_s=0.03)
        self.clients.append(runner)
        return runner

    async def test_two_wishes_are_fifo_and_use_host_assigned_action_ids(self):
        self.control.queued = Control(2).queued
        runner = self.runner("light")
        await runner.connect()
        for _ in range(2):
            await runner.run_wish(await runner.wait())
        self.assertEqual(self.control.finished, [("wish:" + "a" * 26, "granted"), ("wish:" + "b" * 26, "granted")])
        self.assertEqual([message["action_id"] for message in self.commands], ["w-" + "a" * 26 + "-0", "w-" + "b" * 26 + "-0"])
        self.assertEqual(len(self.host.active_effect_ids()), 2)

    async def test_a_completion_waits_for_the_host_to_see_its_running_goal_end(self):
        self.control.running_jobs = 2
        await self.runner("light").run(once=True)
        self.assertEqual(self.control.reports, ["completed"] * 3)
        self.assertEqual(self.control.finished, [("wish:" + "a" * 26, "granted")])
        self.assertEqual(len(self.commands), 1)  # waiting never asks the adapter (or a provider) again

    async def test_a_goal_still_running_at_the_time_limit_ends_the_wish_as_failed(self):
        self.control.running_jobs = 10_000
        await asyncio.wait_for(self.runner("light", timeout=0.5).run(once=True), 3)
        self.assertEqual(self.control.reports[-1], "failed")
        self.assertEqual(self.control.finished, [("wish:" + "a" * 26, "failed")])

    async def test_an_adapter_answering_either_spelling_of_the_protocol_is_relayed_and_no_other(self):
        await self.runner("light-before-the-rename").run(once=True)
        self.assertEqual((self.control.finished, [m["op"] for m in self.commands]), ([("wish:" + "a" * 26, "granted")], ["effect.start"]))
        self.control.queued, self.control.finished, self.commands[:] = Control().queued, [], []
        await self.runner("light-misspelt").run(once=True)
        self.assertEqual((self.control.finished[0][1], self.commands), ("failed", []))

    async def test_unsafe_inventory_never_receives_player_intent_or_calls_game(self):
        await self.runner("unsafe").run(once=True)
        self.assertEqual(self.commands, [])
        self.assertEqual(self.control.finished[0][1], "failed")

    async def test_safe_creation_is_relayed_with_a_curated_name_and_successful_receipt(self):
        await self.runner("creation").run(once=True)
        self.assertEqual(self.control.finished[0][1], "granted")
        self.assertEqual([message["op"] for message in self.commands], ["creation.place"])
        self.assertEqual(self.commands[0]["args"]["source"]["name"], "Wish creation")
        created = [entity for entity in self.host.entities.values() if entity.kind == "creation"]
        self.assertEqual([entity.display_name for entity in created], ["Wish creation"])

    async def test_outside_api_and_executable_creation_source_cannot_reach_the_game(self):
        for mode in ("unsafe-api", "unsafe-source"):
            with self.subTest(mode=mode):
                self.control.queued = Control().queued
                await self.runner(mode).run(once=True)
                self.assertEqual(self.commands, [])
                self.assertEqual(self.control.finished[-1][1], "failed")

    async def test_explicit_preview_is_relayed_but_does_not_grant_or_commit(self):
        await self.runner("preview").run(once=True)
        self.assertEqual(self.control.finished[0][1], "failed")
        self.assertTrue(self.commands[0]["preview"])
        self.assertEqual(self.host.active_effect_ids(), [])

    async def test_a_model_done_claim_without_a_receipt_does_not_grant_a_wish(self):
        await self.runner("done-only").run(once=True)
        self.assertEqual(self.commands, [])
        self.assertEqual(self.control.finished[0][1], "failed")

    async def test_model_prose_and_action_identity_are_not_accepted(self):
        for mode in ("prose", "identity", "huge", "bad-status"):
            with self.subTest(mode=mode):
                self.control.queued = Control().queued
                await self.runner(mode).run(once=True)
                self.assertEqual(self.commands, [])
                self.assertEqual(self.control.finished[-1][1], "failed")

    async def test_player_stop_interrupts_a_silent_adapter(self):
        runner = self.runner("idle", timeout=4)
        await runner.connect()
        wish = await runner.wait()
        task = asyncio.create_task(runner.run_wish(wish))
        await asyncio.sleep(0.2)
        self.control.active = None  # the game's synchronous wish.stop cleared the active and queued work
        await asyncio.wait_for(task, 1)
        self.assertEqual(self.commands, [])
        self.assertEqual(self.control.finished, [])

    async def test_stop_during_a_game_reply_reconnects_cleanly_for_the_next_wish(self):
        delayed = DelayedGameLinkServer(self.game_server.handler)
        await self.game_server.close()
        self.game_server = delayed
        await delayed.start()
        runner = self.runner("light")
        await runner.connect()
        wish = await runner.wait()
        task = asyncio.create_task(runner.run_wish(wish))
        await asyncio.wait_for(delayed.received.wait(), 2)
        self.control.active = None
        try:
            await asyncio.wait_for(task, 1)
            self.assertFalse(runner.game.link.connected)
        finally:
            delayed.release.set()
        await asyncio.wait_for(delayed.ended.wait(), 1)
        self.control.queued = [Control(2).queued[1]]
        await runner.run_wish(await runner.wait())
        self.assertEqual(self.control.finished, [("wish:" + "b" * 26, "granted")])
        self.assertEqual([message["action_id"] for message in self.commands],
                         ["w-" + "a" * 26 + "-0", "w-" + "b" * 26 + "-0"])

    async def test_offered_schemas_match_the_model_interface_without_owned_fields(self):
        runner = self.runner("light")
        inventory = {tool["name"]: tool for tool in runner.tool_inventory()}
        schema = inventory["effect_start"]["input_schema"]
        args = {"capability": "glow", "targets": ["avatar:companion"], "params": {},
                "area": {"center_m": [0, 0, 0], "radius_m": 0.5}, "duration_s": 30, "preview": True}
        validator = Draft202012Validator(schema)
        self.assertEqual(list(validator.iter_errors(args)), [])
        for field in ("action_id", "note"):
            self.assertNotIn(field, schema["properties"])
            self.assertNotIn(field, schema["required"])
            self.assertTrue(list(validator.iter_errors({**args, field: "not allowed"})))
        self.assertIn("action_id", runner.game.specs["effect_start"].input_schema["properties"])
        self.assertEqual(inventory["creation_place"]["input_schema"]["properties"]["source"]["properties"]["name"]["const"], "Wish creation")

    async def test_shipped_no_provider_demo_runs_the_real_relay(self):
        runner = self.runner("light")
        runner.config = ClientConfig((sys.executable, str(Path(wish_demo_adapter.__file__).resolve())), timeout_s=5)
        await runner.run(once=True)
        self.assertEqual(self.control.finished[0][1], "granted")
        self.assertEqual([message["op"] for message in self.commands], ["effect.start"])

    async def test_optional_cli_stream_requires_verified_inventory_before_a_decision(self):
        class Input:
            def write(self, value):
                self.value = value

            async def drain(self):
                pass

            def close(self):
                pass

        class Child:
            def __init__(self, events):
                self.stdout, self.stdin, self.returncode = asyncio.StreamReader(), Input(), None
                for event in events:
                    self.stdout.feed_data((json.dumps(event) + "\n").encode())
                self.stdout.feed_eof()

            def kill(self):
                self.returncode = -1

            async def wait(self):
                return self.returncode

        result = {"type": "result", "subtype": "success", "is_error": False,
                  "result": '{"type":"done","status":"unsupported"}'}
        init = {"type": "system", "subtype": "init", "tools": [], "mcp_servers": [], "plugins": []}
        runner = self.runner("light")
        with patch("garage_game_companion.wish_runner.asyncio.create_subprocess_exec", new=AsyncMock(return_value=Child([init, result]))) as launch:
            self.assertEqual(await runner._claude_turn("{}", self.folder.name), {"type": "done", "status": "unsupported"})
            self.assertEqual(launch.call_args.args[1:], tuple(claude_command(sys.executable)[1:]))
        for events in ([result], [{**init, "tools": ["Bash"]}, result],
                       [init, {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Bash"}]}}]):
            with self.subTest(events=events), patch("garage_game_companion.wish_runner.asyncio.create_subprocess_exec", new=AsyncMock(return_value=Child(events))):
                with self.assertRaises(RunnerError):
                    await runner._claude_turn("{}", self.folder.name)

    async def test_timeout_ends_a_silent_adapter(self):
        await asyncio.wait_for(self.runner("idle", timeout=0.2).run(once=True), 2)
        self.assertEqual(self.control.finished[0][1], "failed")

    async def test_runner_endpoint_cannot_accept_world_commands(self):
        runner = self.runner("light")
        await runner.connect()
        refused = await runner.runner_link.request({"op": "effect.start", "args": {}})
        self.assertEqual(refused["error"]["code"], "permission_denied")
        self.assertEqual(self.commands, [])


class WishConfigTests(unittest.TestCase):
    def test_optional_cli_has_fixed_restrictions_and_verifies_the_actual_inventory(self):
        command = claude_command("C:/local/client.exe")
        for flag in ("--bare", "--strict-mcp-config", "--disable-slash-commands", "--no-session-persistence", "--no-chrome"):
            self.assertIn(flag, command)
        self.assertEqual(command[command.index("--tools") + 1], "")
        self.assertEqual(command[command.index("--disallowedTools") + 1], "*")
        self.assertEqual(command[command.index("--max-turns") + 1], "1")
        verify_claude_inventory({"tools": [], "mcp_servers": [], "plugins": []})
        for event in ({}, {"tools": ["Bash"], "mcp_servers": []}, {"tools": [], "mcp_servers": [{}]},
                      {"tools": [], "mcp_servers": [], "plugins": [{}]},
                      {"tools": [], "mcp_servers": [], "plugin_errors": ["failed"]}):
            with self.subTest(event=event), self.assertRaises(RunnerError):
                verify_claude_inventory(event)

    def test_configuration_is_explicit_local_data_not_a_shell_string(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "client.json"
            path.write_text(json.dumps({"version": 1, "command": [sys.executable, "adapter.py"]}))
            self.assertEqual(ClientConfig.load(path).command[0], sys.executable)
            for value in ({"version": 1, "command": "python adapter.py"},
                          {"version": 1, "command": [sys.executable], "timeout_s": 601},
                          {"version": 1, "command": [sys.executable], "max_tool_calls": True}):
                path.write_text(json.dumps(value))
                with self.assertRaises(RunnerError):
                    ClientConfig.load(path)

    def test_world_prose_is_omitted_from_model_results(self):
        self.assertEqual(game_data({"data": {"text": "world instruction", "display_name": "untrusted", "position_m": [1, 2, 3]},
                                    "error": {"code": "permission_denied", "message": "untrusted prose"}}),
                         {"data": {"position_m": [1, 2, 3]}, "error": {"code": "permission_denied"}})
