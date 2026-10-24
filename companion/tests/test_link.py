"""Boundary tests for the companion link: the loopback connection with a per-session token."""
from __future__ import annotations

import asyncio
import copy
import json
import secrets
import struct
import tempfile
import unittest
from pathlib import Path

from support import COMPANION, command, contract_problems, example, new_host, query

from garage_game_companion import canonical, link
from garage_game_companion.link import (
    LinkClient, LinkError, LinkServer, SessionInfo, encode_frame, parse_session, read_frame, write_session_file,
)


async def raw_connect(info: SessionInfo, *, client_proof: str | None = None, verify_server: bool = True):
    """A hand-rolled client, so tests can misbehave in ways LinkClient never would."""
    reader, writer = await asyncio.open_connection(info.host, info.port)
    nonce = secrets.token_hex(32)
    writer.write(encode_frame({"type": "hello", "protocol": link.PROTOCOL, "version": 1, "client_nonce": nonce}))
    await writer.drain()
    challenge = await read_frame(reader, 4096)
    if challenge.get("type") != "challenge":
        return reader, writer, challenge
    if verify_server:
        assert challenge["server_proof"] == link._proof(info.token, link._SERVER_LABEL, nonce, challenge["server_nonce"])
    proof = client_proof or link._proof(info.token, link._CLIENT_LABEL, nonce, challenge["server_nonce"])
    writer.write(encode_frame({"type": "auth", "client_proof": proof}))
    await writer.drain()
    reply = await read_frame(reader, 4096)
    return reader, writer, reply


async def closed(reader: asyncio.StreamReader) -> bool:
    try:
        data = await asyncio.wait_for(reader.read(1), 2)
    except (ConnectionError, OSError):
        return True
    return data == b""


class LinkCase(unittest.IsolatedAsyncioTestCase):
    handshake_timeout_s = link.HANDSHAKE_TIMEOUT_S

    def setUp(self):
        self.host = new_host()  # loads the room from disk; keep it off the event loop

    async def asyncSetUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="enfractal-link-")
        self.session_path = Path(self.tmp.name) / "session.json"
        self.server = LinkServer(self.host.handle, self.host.room_id, handshake_timeout_s=self.handshake_timeout_s)
        self.info = await self.server.start(self.session_path)
        self.clients: list[LinkClient] = []

    async def asyncTearDown(self):
        for client in self.clients:
            await client.close()
        await self.server.close()
        self.tmp.cleanup()
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def client(self, **kwargs) -> LinkClient:
        client = LinkClient.from_file(self.session_path, **kwargs)
        self.clients.append(client)
        return client


class Handshake(LinkCase):
    async def test_authenticated_client_gets_the_companion_principal_from_the_game(self):
        client = self.client()
        ready = await client.connect()
        self.assertEqual((ready["principal"], ready["avatar"], ready["room_id"]),
                         (COMPANION, "avatar:companion", "test_room"))
        result = await client.request(query("room.describe", {}))
        self.assertTrue(result["ok"])
        self.assertEqual(result["principal"], COMPANION)

    async def test_refuses_a_client_that_does_not_know_the_token(self):
        reader, writer, reply = await raw_connect(self.info, client_proof="0" * 64)
        self.assertEqual(reply, {"type": "refused", "code": "auth_failed"})
        self.assertTrue(await closed(reader))
        writer.close()
        self.assertEqual(self.host.emitted, [])

    async def test_client_refuses_a_listener_that_cannot_prove_the_session(self):
        received: list[dict] = []

        async def squatter(reader, writer):
            hello = await read_frame(reader, 4096)
            received.append(hello)
            writer.write(encode_frame({"type": "challenge", "protocol": link.PROTOCOL, "version": 1,
                                       "server_nonce": secrets.token_hex(32), "server_proof": secrets.token_hex(32)}))
            await writer.drain()
            try:
                received.append(await read_frame(reader, 1 << 20))
            except (asyncio.IncompleteReadError, ConnectionError, LinkError):
                pass
            finally:
                writer.close()

        fake = await asyncio.start_server(squatter, "127.0.0.1", 0)
        port = fake.sockets[0].getsockname()[1]
        info = SessionInfo("127.0.0.1", port, self.info.token, "test_room")
        client = LinkClient(lambda: info)
        with self.assertRaisesRegex(LinkError, "could not prove"):
            await client.request(command("goal.set", {"actor": "avatar:companion", "goal": "follow"}, "f-1"))
        await asyncio.sleep(0.05)
        fake.close()
        await fake.wait_closed()
        self.assertEqual([frame["type"] for frame in received], ["hello"])  # no auth proof, no request

    async def test_the_token_never_crosses_the_socket(self):
        wire = bytearray()

        async def proxy(reader, writer):
            upstream_reader, upstream_writer = await asyncio.open_connection(self.info.host, self.info.port)

            async def pump(src, dst):
                try:
                    while data := await src.read(65536):
                        wire.extend(data)
                        dst.write(data)
                        await dst.drain()
                except (ConnectionError, OSError):
                    pass
                finally:
                    dst.close()

            await asyncio.gather(pump(reader, upstream_writer), pump(upstream_reader, writer))

        relay = await asyncio.start_server(proxy, "127.0.0.1", 0)
        info = SessionInfo("127.0.0.1", relay.sockets[0].getsockname()[1], self.info.token, "test_room")
        client = LinkClient(lambda: info)
        self.clients.append(client)
        await client.request(query("observe", {"actor": "avatar:companion"}))
        await client.close()
        relay.close()
        await relay.wait_closed()
        self.assertGreater(len(wire), 100)
        token = self.info.token
        for needle in (token.encode(), token.upper().encode(), bytes.fromhex(token)):
            self.assertNotIn(needle, bytes(wire))

    async def test_refuses_a_second_companion_while_one_is_connected(self):
        first = self.client()
        await first.connect()
        second = self.client()
        with self.assertRaisesRegex(LinkError, r"refused the connection \(busy\)"):
            await second.connect()
        self.assertTrue((await first.request(query("room.describe", {})))["ok"])

    async def test_failed_proofs_never_lock_out_the_real_companion(self):
        for _ in range(20):
            reader, writer, reply = await raw_connect(self.info, client_proof="f" * 64)
            self.assertEqual(reply["code"], "auth_failed")
            writer.close()
        ready = await self.client().connect()
        self.assertEqual(ready["type"], "ready")

    async def test_refuses_a_malformed_hello(self):
        for hello in ({"type": "hello", "protocol": "other", "version": 1, "client_nonce": "0" * 64},
                      {"type": "hello", "protocol": link.PROTOCOL, "version": 2, "client_nonce": "0" * 64},
                      {"type": "hello", "protocol": link.PROTOCOL, "version": 1, "client_nonce": "short"},
                      {"type": "hello", "protocol": link.PROTOCOL, "version": 1, "client_nonce": "0" * 64,
                       "principal": "player:local"}):
            with self.subTest(hello=hello):
                reader, writer = await asyncio.open_connection(self.info.host, self.info.port)
                writer.write(encode_frame(hello))
                await writer.drain()
                self.assertEqual((await read_frame(reader, 4096))["type"], "refused")
                writer.close()

    def test_refuses_to_listen_off_loopback(self):
        for host in ("0.0.0.0", "192.168.1.10", "::"):
            with self.subTest(host=host), self.assertRaises(ValueError):
                LinkServer(self.host.handle, "test_room", host=host)
        self.assertEqual(self.server._server.sockets[0].getsockname()[0], "127.0.0.1")


class SlowHandshake(LinkCase):
    handshake_timeout_s = 0.3

    async def test_refuses_a_client_that_stalls_the_handshake(self):
        reader, writer = await asyncio.open_connection(self.info.host, self.info.port)
        reply = await read_frame(reader, 4096)
        self.assertEqual(reply, {"type": "refused", "code": "handshake_timeout"})
        writer.close()


class Frames(LinkCase):
    async def authenticated(self):
        reader, writer, reply = await raw_connect(self.info)
        self.assertEqual(reply["type"], "ready")
        self.addAsyncCleanup(self._close, writer)
        return reader, writer

    @staticmethod
    async def _close(writer):
        writer.close()

    async def send_frame(self, writer, document):
        writer.write(encode_frame(document))
        await writer.drain()

    async def test_refuses_an_approval_frame_from_the_companion_connection(self):
        held = self.host.handle(COMPANION, command("entity.remove", {"target": "obj:box"}, "rm-1",
                                                   expected_entities={"obj:box": 0}))
        request_id = held["approval_needed"]["request_id"]
        reader, writer = await self.authenticated()
        await self.send_frame(writer, {"type": "player_decision", "request_id": request_id, "approve": True})
        self.assertEqual(await read_frame(reader, 4096), {"type": "refused", "code": "frame_invalid"})
        self.assertTrue(await closed(reader))
        self.assertEqual(self.host.approvals[request_id].state, "pending")
        self.assertFalse(self.host.entities["obj:box"].removed)

    async def test_refuses_a_principal_on_the_request_frame(self):
        reader, writer = await self.authenticated()
        await self.send_frame(writer, {"type": "request", "seq": 1, "principal": "player:local",
                                       "message": query("room.describe", {})})
        self.assertEqual(await read_frame(reader, 4096), {"type": "refused", "code": "frame_invalid"})
        self.assertEqual(self.host.emitted, [])

    async def test_principal_inside_the_message_is_refused_and_never_adopted(self):
        client = self.client()
        message = command("entity.grab", {"target": "obj:book"}, "grab-1")
        message["principal"] = "player:local"
        result = await client.request(message)
        self.assertEqual(result["error"]["code"], "field_unknown")
        self.assertEqual(result["principal"], COMPANION)

    async def test_refuses_an_oversized_frame_without_reading_it(self):
        reader, writer = await self.authenticated()
        writer.write(struct.pack(">I", 50_000_000))
        await writer.drain()
        self.assertEqual(await read_frame(reader, 4096), {"type": "refused", "code": "frame_invalid"})
        self.assertTrue(await closed(reader))

    async def test_refuses_a_frame_with_duplicate_keys(self):
        reader, writer = await self.authenticated()
        body = b'{"type":"request","seq":1,"seq":2,"message":{}}'
        writer.write(struct.pack(">I", len(body)) + body)
        await writer.drain()
        self.assertEqual(await read_frame(reader, 4096), {"type": "refused", "code": "frame_invalid"})

    async def test_refuses_a_message_over_the_command_limit_inside_a_legal_frame(self):
        client = self.client()
        message = query("entities.list", {"cursor": "1" * 70_000})
        result = await client.request(message)
        self.assertEqual(result["error"]["code"], "request_invalid")
        self.assertEqual(result["error"]["allowed"], 65536)

    async def test_client_refuses_to_send_a_frame_over_the_limit(self):
        client = self.client()
        with self.assertRaisesRegex(LinkError, "too large"):
            await client.request(query("entities.list", {"cursor": "1" * 200_000}))
        self.assertEqual(self.host.emitted, [])


class SessionFiles(unittest.TestCase):
    TOKEN = "ab" * 32

    def doc(self, **changes):
        document = {"schema": "enfractal.companion_session", "version": 1, "host": "127.0.0.1", "port": 40000,
                    "token": self.TOKEN, "room_id": "test_room", "pid": 1, "created_utc": "2026-10-06T00:00:00Z"}
        document.update(changes)
        return json.dumps(document).encode()

    def test_refuses_a_session_file_pointing_off_this_computer(self):
        for host in ("10.0.0.5", "example.com", "0.0.0.0", "localhost", "127.0.0.1.evil.example", "::ffff:8.8.8.8"):
            with self.subTest(host=host), self.assertRaisesRegex(LinkError, "not loopback"):
                parse_session(self.doc(host=host))

    def test_refuses_malformed_session_files(self):
        for raw in (self.doc(token="short"), self.doc(token=self.TOKEN.upper()), self.doc(port=0), self.doc(port=True),
                    self.doc(version=2), self.doc(extra="x"), self.doc(room_id="Bad Room"),
                    b'{"schema":"enfractal.companion_session","schema":"x"}', b"not json", b"[]"):
            with self.subTest(raw=raw[:60]), self.assertRaises(LinkError):
                parse_session(raw)

    def test_session_file_is_lf_bytes_and_session_info_hides_the_token(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "nested" / "session.json"
            info = SessionInfo("127.0.0.1", 41000, self.TOKEN, "test_room")
            write_session_file(path, info)
            raw = path.read_bytes()
            self.assertNotIn(b"\r", raw)
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
            self.assertEqual(parse_session(raw), info)
            self.assertNotIn(self.TOKEN, repr(info))
            self.assertEqual([p.name for p in path.parent.iterdir()], ["session.json"])  # no temp file left


class Reconnect(LinkCase):
    async def test_client_follows_a_restarted_game_to_its_new_session(self):
        client = self.client()
        self.assertTrue((await client.request(query("room.describe", {})))["ok"])
        old = self.info
        await self.server.close()
        self.server = LinkServer(self.host.handle, self.host.room_id)
        self.info = await self.server.start(self.session_path)
        self.assertNotEqual((old.port, old.token), (self.info.port, self.info.token))
        with self.assertRaises(LinkError):
            await client.request(query("room.describe", {}, "q-2"))  # the old connection is gone; outcome unknown
        self.assertTrue((await client.request(query("room.describe", {}, "q-3")))["ok"])

    async def test_link_errors_never_contain_the_token(self):
        messages = []
        for loader in (lambda: SessionInfo("127.0.0.1", 1, self.info.token, "test_room"),
                       lambda: SessionInfo(self.info.host, self.info.port, "cd" * 32, "test_room")):
            try:
                await LinkClient(loader).connect()
            except LinkError as error:
                messages.append(str(error))
        self.assertEqual(len(messages), 2)
        for message in messages:
            self.assertNotIn(self.info.token, message)
            self.assertNotIn("cd" * 32, message)


class ReviewFixes(LinkCase):
    async def test_a_proof_that_is_not_hex_is_refused_not_left_hanging(self):
        for proof in (chr(0xE9), "g" * 64, "A" * 64, "a" * 63):
            with self.subTest(proof=ascii(proof)):
                reader, writer, reply = await raw_connect(self.info, client_proof=proof)
                self.assertEqual(reply, {"type": "refused", "code": "auth_failed"})
                writer.close()

    async def test_client_refuses_a_listener_whose_proof_is_malformed(self):
        async def squatter(reader, writer):
            await read_frame(reader, 4096)
            body = ('{"type":"challenge","protocol":"enfractal.companion_link","version":1,'
                    '"server_nonce":"%s","server_proof":"%s"}' % (secrets.token_hex(32), chr(0xE9))).encode("utf-8")
            writer.write(struct.pack(">I", len(body)) + body)
            await writer.drain()
            await asyncio.sleep(0.2)
            writer.close()

        fake = await asyncio.start_server(squatter, "127.0.0.1", 0)
        info = SessionInfo("127.0.0.1", fake.sockets[0].getsockname()[1], self.info.token, "test_room")
        with self.assertRaises(LinkError):
            await LinkClient(lambda: info).connect()
        fake.close()
        await fake.wait_closed()

    async def test_a_large_non_ascii_result_fits_its_frame(self):
        for _ in range(50):
            self.host.add_world_text("obj:box", chr(0x1F600) * 500)
        result = await self.client().request(query("observe", {"actor": "avatar:companion"}))
        self.assertTrue(result["ok"])
        self.assertEqual(len(result["data"]["texts"]), 50)

    async def test_frames_are_utf8_so_their_size_tracks_canonical_json(self):
        frame = encode_frame({"text": "caf" + chr(0xE9) + " " + chr(0x1F600)})
        self.assertIn(("caf" + chr(0xE9)).encode("utf-8"), frame)
        self.assertNotIn(b"\\u", frame)

    async def test_client_refuses_to_send_non_finite_numbers(self):
        with self.assertRaisesRegex(LinkError, "cannot be encoded"):
            await self.client().request(query("observe", {"actor": "avatar:companion", "radius_m": float("nan")}))
        self.assertEqual(self.host.emitted, [])

    async def test_client_never_connects_anywhere_but_a_loopback_literal(self):
        for host in ("10.0.0.1", "127.0.0.2", "localhost"):
            with self.subTest(host=host), self.assertRaisesRegex(LinkError, "this computer"):
                await LinkClient(lambda: SessionInfo(host, self.info.port, self.info.token, "test_room")).connect()

    async def test_a_handler_failure_closes_only_that_connection(self):
        def broken(principal, message):
            raise RuntimeError("bug")

        server = LinkServer(broken, "test_room")
        info = await server.start()
        with self.assertRaises(LinkError):
            await LinkClient(lambda: info).request(query("room.describe", {}))
        client = LinkClient(lambda: info)
        ready = await client.connect()
        self.assertEqual(ready["type"], "ready")
        await client.close()
        await server.close()


class PendingHandshakes(LinkCase):
    async def test_caps_unauthenticated_connections(self):
        server = LinkServer(self.host.handle, "test_room", max_pending_handshakes=2, handshake_timeout_s=5)
        info = await server.start()
        idle = [await asyncio.open_connection(info.host, info.port) for _ in range(2)]
        await asyncio.sleep(0.05)
        reader, writer = await asyncio.open_connection(info.host, info.port)
        self.assertEqual(await read_frame(reader, 4096), {"type": "refused", "code": "busy"})
        writer.close()
        for _r, w in idle:
            w.close()
        await server.close()


class Loopback(unittest.IsolatedAsyncioTestCase):
    async def test_the_two_loopback_literals_are_the_only_hosts(self):
        host = new_host()
        for literal in ("127.0.0.2", "::ffff:127.0.0.1", "0:0:0:0:0:0:0:1", "localhost"):
            with self.subTest(host=literal):
                with self.assertRaises(ValueError):
                    LinkServer(host.handle, "test_room", host=literal)
                document = {"schema": "enfractal.companion_session", "version": 1, "host": literal, "port": 4000,
                            "token": "ab" * 32, "room_id": "test_room", "pid": 1, "created_utc": "2026-10-06T00:00:00Z"}
                with self.assertRaises(LinkError):
                    parse_session(json.dumps(document).encode())

    async def test_ipv6_loopback_works_end_to_end(self):
        host = new_host()
        server = LinkServer(host.handle, host.room_id, host="::1")
        try:
            info = await server.start()
        except OSError:
            self.skipTest("IPv6 loopback is not available here")
        client = LinkClient(lambda: info)
        result = await client.request(query("room.describe", {}))
        self.assertTrue(result["ok"])
        await client.close()
        await server.close()

    def test_a_session_token_with_a_trailing_newline_is_refused(self):
        document = {"schema": "enfractal.companion_session", "version": 1, "host": "127.0.0.1", "port": 4000,
                    "token": "ab" * 32 + "\n", "room_id": "test_room", "pid": 1, "created_utc": "2026-10-06T00:00:00Z"}
        with self.assertRaisesRegex(LinkError, "token"):
            parse_session(json.dumps(document).encode())
        document["token"] = "ab" * 32
        document["room_id"] = "test_room\n"
        with self.assertRaisesRegex(LinkError, "room_id"):
            parse_session(json.dumps(document).encode())


SAMPLE = example("command_creation_place_trigger_light")["args"]["source"]


class CanonicalFrames(LinkCase):
    """Review finding 4: a frame must never be much larger than the canonical message it carries.

    The contract limits messages in canonical JSON v1. ASCII escapes (6 or 12 bytes for one
    character) and number spellings (-0.0 is 4 bytes, canonical 0 is 1) used to make the frame
    several times larger, so a contract-valid message could exceed the frame limit."""

    @staticmethod
    def spelled_out(count: int) -> dict:
        source = copy.deepcopy(SAMPLE)
        source["parts"][0]["profile_m"] = [-0.0] * count  # canonical: 0
        return command("creation.place", {"source": source, "placement": {"position_m": [0.5, 0, 0.5]}}, "c-1")

    def test_a_frame_is_the_canonical_message_plus_a_short_envelope(self):
        tags = "".join(chr(0x1F600 + i % 50) for i in range(400))
        messages = [self.spelled_out(2000),
                    command("goal.set", {"actor": "avatar:companion", "goal": "stay"}, "g-1",
                            note="caf" + chr(0xE9) + " " + tags[:100]),
                    query("observe", {"actor": "avatar:companion", "radius_m": 2.0}),
                    {"schema": "enfractal.result", "version": 1, "ok": True, "op": "observe", "principal": COMPANION,
                     "room_id": "test_room", "revision": 3, "replayed": False, "preview": False,
                     "at_utc": "2026-10-06T00:00:00Z", "data": {"bounds": [[1.0, -0.0, 2.5]] * 500, "text": tags}}]
        for message in messages:
            with self.subTest(op=message.get("op")):
                for kind in ("request", "response"):
                    body = encode_frame({"type": kind, "seq": 2 ** 53 - 1, "message": message})[4:]
                    self.assertLessEqual(len(body) - len(canonical.canonical_bytes(message)), 64)
                    self.assertEqual(json.loads(body)["message"], canonical.loads_strict(
                        canonical.canonical_bytes(message).decode("utf-8")))

    async def test_a_contract_sized_message_crosses_a_link_sized_to_the_contract(self):
        message = self.spelled_out(15_000)
        self.assertLessEqual(len(canonical.canonical_bytes(message["args"]["source"])), 32_768)
        self.assertLessEqual(len(canonical.canonical_bytes(message)), 65_536)
        client = self.client(max_request_frame=65_536 + 64)  # the bound canonical frames guarantee
        result = await client.request(message)
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["created"], ["creation:00000001"])


class SessionFileLimits(unittest.TestCase):
    TOKEN = "ab" * 32

    def document(self, **changes) -> dict:
        document = {"schema": "enfractal.companion_session", "version": 1, "host": "127.0.0.1", "port": 40000,
                    "token": self.TOKEN, "room_id": "test_room", "pid": 1, "created_utc": "2026-10-06T00:00:00Z"}
        document.update(changes)
        return document

    def test_refuses_an_oversized_session_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "session.json"
            path.write_bytes(json.dumps(self.document(), indent=4000).encode("utf-8"))
            self.assertGreater(path.stat().st_size, link.MAX_SESSION_FILE)
            with self.assertRaisesRegex(LinkError, "too large"):
                LinkClient.from_file(path)._load()
            path.write_bytes(json.dumps(self.document()).encode("utf-8"))
            self.assertEqual(LinkClient.from_file(path)._load().port, 40000)

    def test_refuses_a_malformed_pid_or_creation_time(self):
        for changes in ({"pid": "1"}, {"pid": True}, {"pid": -1}, {"pid": 2 ** 40}, {"created_utc": 5},
                        {"created_utc": "x" * 65}):
            with self.subTest(changes=changes), self.assertRaisesRegex(LinkError, "pid or created_utc"):
                parse_session(json.dumps(self.document(**changes)).encode())


if __name__ == "__main__":
    unittest.main()
