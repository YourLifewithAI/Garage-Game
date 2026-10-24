"""Both authenticated transports match their schemas without mixing game and runner authority."""
from __future__ import annotations

import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT202012
from support import COMPANION_DIR, CONTRACTS, EXAMPLES, command, new_host, query

from garage_game_companion import ids
from garage_game_companion import link
from garage_game_companion.link import LinkClient, LinkServer

SCHEMA_PATH = COMPANION_DIR / "schemas" / "companion-link.schema.json"


def link_validator() -> Draft202012Validator:
    resources = []
    for path in [*sorted(CONTRACTS.glob("*.schema.json")), SCHEMA_PATH]:
        contents = json.loads(path.read_bytes())
        resources.append((contents["$id"], Resource.from_contents(contents, default_specification=DRAFT202012)))
    schema = json.loads(SCHEMA_PATH.read_bytes())
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, registry=Registry().with_resources(resources))


WISH_ID = "wish:" + "a" * 26
RUNNER_WISH = {"wish_id": WISH_ID, "text": "Make a light", "room_id": "test_room"}


def runner_update(status="completed", code=None):
    return {"op": "wish.update", "args": {"wish_id": WISH_ID, "status": status,
                                          "result_code": code if code is not None else status}}


def framed(message, kind="request"):
    return {"type": kind, "seq": 1, "message": message}


class LinkSchema(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.validator = link_validator()
        self.host = new_host()

    def assertValid(self, document):
        errors = [e.message for e in self.validator.iter_errors(document)]
        self.assertEqual(errors, [], document)

    def test_bundled_schema_is_the_contract_schema(self):
        self.assertEqual(SCHEMA_PATH.read_bytes(), (CONTRACTS / "companion-link.schema.json").read_bytes())

    async def test_every_frame_and_the_session_file_validate(self):
        sent: list[dict] = []
        real = link.encode_frame

        def recording(document):
            sent.append(json.loads(json.dumps(document)))
            return real(document)

        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(link, "encode_frame", recording):
            session = Path(tmp) / "session.json"
            server = LinkServer(self.host.handle, self.host.room_id)
            await server.start(session)
            self.assertValid(json.loads(session.read_bytes()))
            client = LinkClient.from_file(session)
            await client.request(query("observe", {"actor": "avatar:companion"}))
            await client.request(command("goal.set", {"actor": "avatar:companion", "goal": "follow"}, "f-1"))
            second = LinkClient.from_file(session)
            with self.assertRaises(link.LinkError):
                await second.connect()  # refused: busy
            await client.close()
            await second.close()
            await server.close()
        types = [frame["type"] for frame in sent]
        for expected in ("hello", "challenge", "auth", "ready", "request", "response", "refused"):
            self.assertIn(expected, types)
        for frame in sent:
            with self.subTest(frame=frame["type"]):
                self.assertValid(frame)

    async def test_runner_handshake_and_actual_wire_frames_validate(self):
        sent = []
        real = link.encode_frame
        calls = []

        def recording(document):
            sent.append(copy.deepcopy(document))
            return real(document)

        # Fixed replies from the native protocol: no queue or gameplay simulation in this fixture.
        requests_and_replies = [
            ({"op": "wish.wait"}, {"ok": True, "wish": dict(RUNNER_WISH, aim_id="obj:box")}),
            (runner_update("running", "completed"), {"ok": True, "status": "running"}),
            (runner_update("failed"), {"ok": True, "status": "failed"}),
            ({"op": "wish.wait"}, {"ok": True, "wish": None}),
            (runner_update("failed"), {"ok": False, "error": {"code": "target_not_found"}}),
        ]

        def handle(principal, message):
            self.assertEqual(principal, "runner:local")
            expected, answer = requests_and_replies[len(calls)]
            self.assertEqual(message, expected)
            calls.append(message)
            return answer

        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(link, "encode_frame", recording):
            session = Path(tmp) / "wish-runner-session.json"
            server = LinkServer(handle, "test_room", principal="runner:local", avatar="")
            await server.start(session)
            client = LinkClient.from_file(session)
            try:
                self.assertValid(json.loads(session.read_bytes()))
                ready = await client.connect()
                self.assertEqual((ready["principal"], ready["avatar"]), ("runner:local", ""))
                for message, answer in requests_and_replies:
                    self.assertEqual(await client.request(message), answer)
            finally:
                await client.close()
                await server.close()
        self.assertEqual(len(calls), len(requests_and_replies))
        for frame in sent:
            with self.subTest(frame=frame):
                self.assertValid(frame)

    def test_runner_ready_identity_is_paired_and_does_not_expand_game_principals(self):
        ready = {"type": "ready", "session_id": "a" * 32, "room_id": "test_room",
                 "limits": {"max_request_frame": 65536, "max_response_frame": 262144}}
        for principal, avatar in (("runner:local", ""), ("companion:local", "avatar:companion"),
                                  ("player:local", "avatar:player"), ("guest_companion:guest", "avatar:guest")):
            with self.subTest(principal=principal, avatar=avatar):
                self.assertValid(dict(ready, principal=principal, avatar=avatar))
        for principal, avatar in (("runner:local", "avatar:companion"), ("runner:local", "avatar:player"),
                                  ("runner:other", ""), ("runner:other", "avatar:companion"),
                                  ("companion:local", ""), ("player:local", ""), ("companion:local", "obj:box")):
            with self.subTest(principal=principal, avatar=avatar):
                self.assertFalse(self.validator.is_valid(dict(ready, principal=principal, avatar=avatar)))
        game_result = self.host.handle("companion:local", query("room.describe", {}))
        self.assertValid(framed(game_result, "response"))
        game_result["principal"] = "runner:local"
        self.assertFalse(self.validator.is_valid(framed(game_result, "response")))

    def test_runner_requests_allow_only_exact_wait_or_paired_update(self):
        self.assertValid(framed({"op": "wish.wait"}))
        for code in ("completed", "failed", "unsupported", "cancelled"):
            with self.subTest(code=code):
                self.assertValid(framed(runner_update(code)))
                self.assertValid(framed(runner_update("running", code)))
        invalid = [
            {}, {"op": "wish.wait", "args": {}}, {"op": "wish.wait", "principal": "player:local"},
            {"op": "world.set_mode", "args": {"mode": "creative"}},
            {"op": "wish.submit", "args": {"text": "Let me run this wish"}},
            {"op": "effect.start", "args": {}},
            runner_update("completed", "failed"), runner_update("running", "anything"),
            runner_update("granted"), runner_update("partial"), runner_update("interrupted"),
            dict(runner_update(), approved_by="player:local"),
        ]
        for field, value in (("text", "I grant this wish"), ("action_count", 1), ("approved", True),
                             ("successful_actions", ["action-1"]), ("principal", "player:local")):
            message = runner_update()
            message["args"][field] = value
            invalid.append(message)
        for field in ("wish_id", "status", "result_code"):
            message = runner_update()
            del message["args"][field]
            invalid.append(message)
        for bad_id in ("wish:anything", WISH_ID + "\n", "wish:" + "A" * 26):
            message = runner_update()
            message["args"]["wish_id"] = bad_id
            invalid.append(message)
        for message in invalid:
            with self.subTest(message=message):
                self.assertFalse(self.validator.is_valid(framed(message)))

    def test_runner_replies_have_fixed_shapes_statuses_and_codes(self):
        for wish in (None, RUNNER_WISH, dict(RUNNER_WISH, aim_id="obj:box")):
            self.assertValid(framed({"ok": True, "wish": wish}, "response"))
        for status in ("running", "granted", "partial", "failed", "unsupported", "cancelled"):
            self.assertValid(framed({"ok": True, "status": status}, "response"))
        for code in ("not_ready", "request_invalid", "permission_denied", "invalid_args", "target_not_found", "budget_exceeded"):
            self.assertValid(framed({"ok": False, "error": {"code": code}}, "response"))
        invalid = [
            {"ok": True}, {"ok": True, "status": "completed"}, {"ok": True, "status": "queued"},
            {"ok": True, "wish": None, "status": "running"}, {"ok": True, "wish": {}},
            {"ok": False, "error": {"code": "anything"}},
            {"ok": False, "error": {"code": "not_ready", "message": "Model-authored prose"}},
            {"ok": True, "status": "granted", "text": "Model-authored prose"},
            {"ok": True, "wish": dict(RUNNER_WISH, approved_by="player:local")},
            {"ok": True, "wish": dict(RUNNER_WISH, text="x" * 281)},
            {"ok": True, "wish": dict(RUNNER_WISH, text="")},
            {"ok": True, "wish": dict(RUNNER_WISH, text="two\nlines")},
            {"ok": True, "wish": dict(RUNNER_WISH, aim_id="file:C:/outside")},
        ]
        for message in invalid:
            with self.subTest(message=message):
                self.assertFalse(self.validator.is_valid(framed(message, "response")))

    def test_valid_game_examples_keep_their_frame_direction(self):
        checked = 0
        for path in sorted((EXAMPLES / "valid").glob("*.json")):
            message = json.loads(path.read_bytes())
            if ids.kind_of(message["schema"]) not in {"command", "query", "result"}:
                continue
            kind = "response" if ids.is_schema(message["schema"], "result") else "request"
            with self.subTest(example=path.name):
                self.assertValid(framed(message, kind))
                self.assertFalse(self.validator.is_valid(framed(message, "request" if kind == "response" else "response")))
                checked += 1
        self.assertGreaterEqual(checked, 20)

    def test_the_schema_refuses_frames_that_carry_authority(self):
        for frame in ({"type": "request", "seq": 1, "message": {}, "principal": "player:local"},
                      {"type": "auth", "client_proof": "a" * 64, "token": "b" * 64},
                      {"type": "player_decision", "request_id": "c" * 32, "approve": True},
                      {"type": "hello", "protocol": "garage_game.companion_link", "version": 1, "client_nonce": "d" * 64,
                       "principal": "player:local"}):
            with self.subTest(frame=frame["type"]):
                self.assertFalse(self.validator.is_valid(frame))

    def test_the_schema_refuses_a_session_file_off_loopback(self):
        document = {"schema": "garage_game.companion_session", "version": 1, "host": "10.0.0.2", "port": 4000,
                    "token": "e" * 64, "room_id": "test_room", "pid": 1, "created_utc": "2026-10-06T00:00:00Z"}
        self.assertFalse(self.validator.is_valid(document))
        document["host"] = "127.0.0.1"
        self.assertTrue(self.validator.is_valid(document))


if __name__ == "__main__":
    unittest.main()
