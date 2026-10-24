"""The mock host fingerprints receipts with Lane P's canonical JSON v1, byte for byte.

The golden fixture is the kernel's own, game/tests/fixtures/kernel/, wherever it exists: on the
integration branch, or in another checkout named by GARAGE_GAME_KERNEL_FIXTURE_DIR. On this lane's
branch, which predates it, the suite uses the byte-identical copy in fixtures/canonical_json_v1
(see SOURCE.txt). The copy is not pinned against line-ending conversion, so a Windows checkout may
hold its input with CRLF; that changes no parsed value, and the expected bytes have no line ending.
"""
from __future__ import annotations

import hashlib
import os
import unittest
from pathlib import Path

from support import COMPANION, REPO, command, new_host

from garage_game_companion import canonical

GOLDEN_SHA256 = "ad3d1a8594e808790c035c49f00fc39fd49d49325d3237bb03bba6166fbccc94"  # the Garage Game golden fixture (the rename, 24 October 2026)
_SHARED = Path(os.environ.get("GARAGE_GAME_KERNEL_FIXTURE_DIR") or REPO / "game" / "tests" / "fixtures" / "kernel")
FIXTURE = _SHARED if (_SHARED / "canonical_expected.json").is_file() else \
    Path(__file__).resolve().parent / "fixtures" / "canonical_json_v1"


class CanonicalJsonV1(unittest.TestCase):
    def test_reproduces_the_golden_fixture_byte_for_byte(self):
        document = canonical.loads_strict((FIXTURE / "canonical_input.json").read_bytes().decode("utf-8"))
        produced = canonical.canonical_bytes(document)
        expected = (FIXTURE / "canonical_expected.json").read_bytes()
        self.assertEqual(produced, expected)
        digest = (FIXTURE / "canonical_expected.sha256").read_bytes().decode("ascii").strip()
        self.assertEqual(digest, GOLDEN_SHA256)
        self.assertEqual(hashlib.sha256(produced).hexdigest(), digest)
        self.assertEqual(canonical.sha256_hex(document), digest)

    def test_a_link_frame_carries_the_golden_canonical_bytes_verbatim(self):
        from garage_game_companion.link import encode_frame
        document = canonical.loads_strict((FIXTURE / "canonical_input.json").read_bytes().decode("utf-8"))
        expected = (FIXTURE / "canonical_expected.json").read_bytes()
        body = encode_frame({"type": "request", "seq": 7, "message": document})[4:]
        self.assertEqual(body, b'{"message":' + expected + b',"seq":7,"type":"request"}')

    def test_a_named_shared_fixture_is_the_one_checked(self):
        if os.environ.get("GARAGE_GAME_KERNEL_FIXTURE_DIR"):
            self.assertEqual(FIXTURE, _SHARED)  # a typo in the path must not fall back to the copy silently

    def test_the_contract_layer_measures_and_fingerprints_with_v1(self):
        from support import contracts
        self.assertEqual(contracts().canonical_bytes({"b": 2.0, "a": [1e0, -0.0, 0.5]}), b'{"a":[1,0,0.5],"b":2}')

    def test_refuses_what_v1_refuses(self):
        for text in ('{"a":1,"a":2}', '{"a":NaN}', '{"a":1e999}'):
            with self.subTest(text=text), self.assertRaises(canonical.CanonicalJsonError):
                canonical.loads_strict(text)
        with self.assertRaises(canonical.CanonicalJsonError):
            canonical.canonical_bytes({"s": "lone " + chr(0xD800)})
        deep: dict = {}
        node = deep
        for _ in range(130):
            node["x"] = {}
            node = node["x"]
        with self.assertRaises(canonical.CanonicalJsonError):
            canonical.canonical_bytes(deep)

    def test_receipts_match_on_v1_so_1_0_and_1_are_the_same_command(self):
        host = new_host()
        first = host.handle(COMPANION, command("entity.place", {"target": "obj:book", "placement": {"position_m": [1, 0, 1]}},
                                               "place-1"))
        again = host.handle(COMPANION, command("entity.place", {"target": "obj:book",
                                                                "placement": {"position_m": [1.0, 0.0, 1e0]}}, "place-1"))
        self.assertTrue(first["ok"])
        self.assertTrue(again["replayed"], again)
        receipt = host.receipts[(COMPANION, "place-1")]
        expected = canonical.sha256_hex(command("entity.place", {"target": "obj:book", "placement": {"position_m": [1, 0, 1]}},
                                                "place-1"))
        self.assertEqual(receipt.fingerprint, expected)


if __name__ == "__main__":
    unittest.main()
