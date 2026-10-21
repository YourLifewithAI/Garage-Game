"""The companion link: a loopback TCP connection to the running game, authenticated per session.

Specified in docs/companion/TRANSPORT.md; the frame shapes are in `schemas/companion-link.schema.json`.

- The game listens on 127.0.0.1 (or ::1) only, on an ephemeral port, and writes a session file
  (`user://companion/session.json`) holding the port and a fresh 256-bit token. The token changes
  every time the game starts and is readable only by the player's own account.
- Both sides prove they know the token with HMAC-SHA256 over two fresh nonces, so the token never
  crosses the socket: a process squatting the port learns nothing, and a stale session file
  pointing at someone else's listener is detected before any request is sent.
- The game assigns the principal to the authenticated connection (`companion:local`). Requests
  carry no principal, no approval and no token.
- Frames are a 4-byte big-endian length and one JSON object, written as canonical JSON v1
  (canonical.py: UTF-8, not ASCII-escaped, numbers in their shortest canonical form). A frame is
  therefore exactly a short envelope plus the canonical bytes of the message it carries, so the
  contract's size limits bound the frame size whatever characters or number spellings the
  message holds. Readers accept any strict JSON within the limit. Oversized frames, unknown frame
  types, duplicate keys and non-finite numbers close the connection.
- There is no lockout: a wrong proof only closes that connection, so no local process can lock the
  real companion out, and guessing a 256-bit token is hopeless anyway. At most a few handshakes
  may be pending at once.
"""
from __future__ import annotations

import asyncio
import hashlib
import hmac
import ipaddress
import json
import logging
import os
import re
import secrets
import struct
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from . import canonical

log = logging.getLogger("enfractal.link")

PROTOCOL = "enfractal.companion_link"
LINK_VERSION = 1
SESSION_SCHEMA = "enfractal.companion_session"
LOOPBACK_HOSTS = ("127.0.0.1", "::1")  # the only spellings a session file or a listener may use
MAX_HANDSHAKE_FRAME = 1024
# A command or query is at most 65,536 bytes of canonical JSON, and a frame is that plus an envelope
# of under 64 bytes. The request limit leaves room for the game to answer an oversized message with
# request_invalid instead of closing the connection.
MAX_REQUEST_FRAME = 131_072
# A result is at most 262,144 bytes of canonical JSON; the envelope adds under 64.
MAX_RESPONSE_FRAME = 262_144 + 4_096
MAX_SESSION_FILE = 4_096  # a real session file is about 300 bytes
HANDSHAKE_TIMEOUT_S = 5.0
CONNECT_TIMEOUT_S = 3.0
REQUEST_TIMEOUT_S = 10.0
MAX_PENDING_HANDSHAKES = 8
COMPANION_PRINCIPAL = "companion:local"
COMPANION_AVATAR = "avatar:companion"

_HEX64 = re.compile(r"[0-9a-f]{64}")
_TOKEN = re.compile(r"[a-z][a-z0-9_-]{0,63}")
_CODE = re.compile(r"[a-z_]{1,32}")
_SERVER_LABEL = f"{PROTOCOL}/{LINK_VERSION}|server|".encode("ascii")
_CLIENT_LABEL = f"{PROTOCOL}/{LINK_VERSION}|client|".encode("ascii")


class LinkError(Exception):
    """The link to the game failed. The message is safe to show: it never contains the token."""


def default_session_path() -> Path:
    """Where the game writes its session file: Godot's user:// folder for the EnFractal project."""
    if os.name == "nt":
        base = Path(os.environ.get("APPDATA") or Path.home() / "AppData" / "Roaming") / "Godot"
    elif os.uname().sysname == "Darwin":  # pragma: no cover - not exercised on the founder's machine
        base = Path.home() / "Library" / "Application Support" / "Godot"
    else:
        base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / "godot"
    return base / "app_userdata" / "EnFractal" / "companion" / "session.json"


# ---------------------------------------------------------------------------- framing

def encode_frame(document: dict) -> bytes:
    """4-byte big-endian length, then the frame object as canonical JSON v1.

    Each value (the message included) is written exactly as canonical.canonical_bytes writes it, so
    the body is the canonical message plus the envelope keys. ASCII escapes (6 or 12 bytes for one
    character) and number spellings such as -0.0 or 1.0e0 can no longer make a frame several times
    larger than the message the contract limits. Raises ValueError (CanonicalJsonError) for
    non-finite numbers, unpaired surrogates and nesting deeper than canonical JSON allows.
    """
    normalized = {key: canonical.normalize(value) for key, value in document.items()}
    text = json.dumps(normalized, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    try:
        body = text.encode("utf-8")
    except UnicodeEncodeError:
        raise canonical.CanonicalJsonError("strings must not contain unpaired surrogates") from None
    return struct.pack(">I", len(body)) + body


def _strict_object(raw: bytes) -> dict:
    def pairs(items):
        out = {}
        for key, value in items:
            if key in out:
                raise ValueError("duplicate key")
            out[key] = value
        return out

    def no_constants(name):
        raise ValueError("non-finite number")

    def finite(text):
        value = float(text)
        if value != value or value in (float("inf"), float("-inf")):
            raise ValueError("non-finite number")
        return value

    document = json.loads(raw.decode("utf-8"), object_pairs_hook=pairs, parse_constant=no_constants, parse_float=finite)
    if not isinstance(document, dict):
        raise ValueError("a frame is one JSON object")
    return document


async def read_frame(reader: asyncio.StreamReader, limit: int) -> dict:
    header = await reader.readexactly(4)
    (length,) = struct.unpack(">I", header)
    if length == 0 or length > limit:
        raise LinkError(f"frame of {length} bytes is outside the limit of {limit}")
    raw = await reader.readexactly(length)
    try:
        return _strict_object(raw)
    except (UnicodeDecodeError, ValueError, RecursionError) as error:
        raise LinkError(f"frame is not a strict JSON object: {error}") from None


def _is_hex64(value: Any) -> bool:
    return isinstance(value, str) and _HEX64.fullmatch(value) is not None


def _proof(token_hex: str, label: bytes, client_nonce: str, server_nonce: str) -> str:
    message = label + client_nonce.encode("ascii") + b"|" + server_nonce.encode("ascii")
    return hmac.new(bytes.fromhex(token_hex), message, hashlib.sha256).hexdigest()


def _is_loopback(host: str) -> bool:
    """Any loopback address: used for the peer of an accepted connection."""
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def _safe_code(code: Any) -> str:
    return code if isinstance(code, str) and _CODE.fullmatch(code) else "unknown"


# ---------------------------------------------------------------------------- session file

@dataclass(frozen=True)
class SessionInfo:
    host: str
    port: int
    token: str
    room_id: str

    def __repr__(self) -> str:  # never print the token
        return f"SessionInfo(host={self.host!r}, port={self.port}, room_id={self.room_id!r}, token=<hidden>)"


def parse_session(raw: bytes) -> SessionInfo:
    """Read a session file, refusing anything that is not a loopback endpoint with a well-formed token."""
    try:
        document = _strict_object(raw)
    except (UnicodeDecodeError, ValueError, RecursionError) as error:
        raise LinkError(f"session file is not valid JSON: {error}") from None
    expected = {"schema", "version", "host", "port", "token", "room_id", "pid", "created_utc"}
    if set(document) - expected:
        raise LinkError("session file has fields this companion does not understand")
    if document.get("schema") != SESSION_SCHEMA or document.get("version") != LINK_VERSION:
        raise LinkError("session file is not an enfractal.companion_session version 1")
    host, port, token, room_id = (document.get(k) for k in ("host", "port", "token", "room_id"))
    pid, created = document.get("pid"), document.get("created_utc")
    if (pid is not None and (not isinstance(pid, int) or isinstance(pid, bool) or not 0 < pid < 2 ** 32)) \
            or (created is not None and (not isinstance(created, str) or len(created) > 64)):
        raise LinkError("session file pid or created_utc is malformed")
    if host not in LOOPBACK_HOSTS:
        raise LinkError("session file names a host that is not loopback; the companion only connects to this computer")
    if not isinstance(port, int) or isinstance(port, bool) or not 1 <= port <= 65535:
        raise LinkError("session file port is invalid")
    if not _is_hex64(token):
        raise LinkError("session file token is malformed")
    if not isinstance(room_id, str) or not _TOKEN.fullmatch(room_id):
        raise LinkError("session file room_id is malformed")
    return SessionInfo(host, port, token, room_id)


def write_session_file(path: Path, info: SessionInfo) -> None:
    """Atomically write the session file as LF bytes, readable only by this user where the OS allows."""
    path.parent.mkdir(parents=True, exist_ok=True)
    document = {
        "schema": SESSION_SCHEMA, "version": LINK_VERSION, "host": info.host, "port": info.port,
        "token": info.token, "room_id": info.room_id, "pid": os.getpid(),
        "created_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    data = (json.dumps(document, indent=2) + "\n").encode("utf-8")
    temp = path.with_name(path.name + f".{secrets.token_hex(4)}.tmp")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_BINARY", 0)
    fd = os.open(temp, flags, 0o600)
    try:
        os.write(fd, data)
    finally:
        os.close(fd)
    os.replace(temp, path)


class SessionLock:
    """The account's ownership of the companion link: `session.lock` beside the session file, held exclusively by the
    one game that serves the link, from before it checks or writes the session file until after it removes it.

    The game (C#, CompanionBridge) holds it open with no sharing; this side takes a byte-range lock on Windows and a
    flock elsewhere, which conflict with the game's hold either way. The operating system frees it when its holder
    ends, a crash included, so a stale session file never decides who owns the link. The mock game takes it too.
    """

    def __init__(self, session_path: Path):
        self.path = Path(session_path).with_name("session.lock")
        self._fd: int | None = None

    def acquire(self) -> bool:
        if self._fd is not None:
            return True
        self.path.parent.mkdir(parents=True, exist_ok=True)
        try:
            fd = os.open(self.path, os.O_RDWR | os.O_CREAT | getattr(os, "O_BINARY", 0), 0o600)
        except OSError:  # on Windows: the game holds it with no sharing
            return False
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            os.close(fd)
            return False
        self._fd = fd
        return True

    def release(self) -> None:
        if self._fd is None:
            return
        fd, self._fd = self._fd, None
        try:
            if os.name == "nt":
                import msvcrt
                os.lseek(fd, 0, os.SEEK_SET)
                msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
        except OSError:
            pass
        finally:
            os.close(fd)


# ---------------------------------------------------------------------------- game side

Handler = Callable[[str, dict], dict]


class LinkServer:
    """The game's end of the link. In Run 1 it fronts the mock host; in Run 2 the kernel host."""

    def __init__(self, handler: Handler, room_id: str, *, host: str = "127.0.0.1", token: str | None = None,
                 principal: str = COMPANION_PRINCIPAL, avatar: str = COMPANION_AVATAR,
                 max_request_frame: int = MAX_REQUEST_FRAME, handshake_timeout_s: float = HANDSHAKE_TIMEOUT_S,
                 max_pending_handshakes: int = MAX_PENDING_HANDSHAKES,
                 on_session: Callable[[str, str], None] | None = None):
        if host not in LOOPBACK_HOSTS:
            raise ValueError("the companion link listens on 127.0.0.1 or ::1 only")
        self.handler = handler
        self.room_id = room_id
        self.host = host
        self.token = token or secrets.token_hex(32)
        self.principal = principal
        self.avatar = avatar
        self.max_request_frame = max_request_frame
        self.handshake_timeout_s = handshake_timeout_s
        self.max_pending_handshakes = max_pending_handshakes
        # Told (principal, "start" | "end") around each authenticated session, so the host can keep
        # per-session state, such as perception memory, from outliving it.
        self.on_session = on_session
        self.port = 0
        self._server: asyncio.base_events.Server | None = None
        self._active: asyncio.StreamWriter | None = None
        self._pending = 0
        self.events: list[tuple[str, str]] = []  # (event, detail) for tests and the console; never the token

    @property
    def session(self) -> SessionInfo:
        return SessionInfo(self.host, self.port, self.token, self.room_id)

    async def start(self, session_path: Path | None = None) -> SessionInfo:
        self._server = await asyncio.start_server(self._serve, host=self.host, port=0, limit=MAX_REQUEST_FRAME)
        self.port = self._server.sockets[0].getsockname()[1]
        if session_path is not None:
            write_session_file(session_path, self.session)
        return self.session

    async def close(self) -> None:
        if self._active is not None:
            self._active.close()
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()

    def _note(self, event: str, detail: str = "") -> None:
        self.events.append((event, detail))
        log.info("link %s %s", event, detail)

    async def _send(self, writer: asyncio.StreamWriter, document: dict) -> None:
        writer.write(encode_frame(document))
        await writer.drain()

    async def _refuse(self, writer: asyncio.StreamWriter, code: str) -> None:
        self._note("refused", code)
        try:
            await self._send(writer, {"type": "refused", "code": code})
        except (ConnectionError, OSError):
            pass
        writer.close()

    async def _serve(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        peer = writer.get_extra_info("peername")
        if not peer or not _is_loopback(peer[0]):
            writer.close()
            return
        if self._pending >= self.max_pending_handshakes:
            await self._refuse(writer, "busy")
            return
        self._pending += 1
        try:
            authenticated = await asyncio.wait_for(self._handshake(reader, writer), self.handshake_timeout_s)
        except asyncio.TimeoutError:
            await self._refuse(writer, "handshake_timeout")
            return
        except Exception as error:  # any malformed handshake is answered, never left hanging
            self._note("handshake_failed", type(error).__name__)
            await self._refuse(writer, "handshake_invalid")
            return
        finally:
            self._pending -= 1
        if not authenticated:
            return
        self._active = writer
        self._session_event("start")
        try:
            await self._requests(reader, writer)
        except Exception as error:  # a broken connection or a bug: close this connection, keep listening
            self._note("connection_failed", type(error).__name__)
            await self._refuse(writer, "frame_invalid")
        finally:
            if self._active is writer:
                self._active = None
            writer.close()
            self._session_event("end")

    def _session_event(self, event: str) -> None:
        if self.on_session is None:
            return
        try:
            self.on_session(self.principal, event)
        except Exception:  # the host's bookkeeping never breaks the link
            log.exception("session %s hook failed", event)

    async def _handshake(self, reader, writer) -> bool:
        hello = await read_frame(reader, MAX_HANDSHAKE_FRAME)
        if (set(hello) != {"type", "protocol", "version", "client_nonce"} or hello.get("type") != "hello"
                or hello.get("protocol") != PROTOCOL or hello.get("version") != LINK_VERSION
                or not _is_hex64(hello.get("client_nonce"))):
            await self._refuse(writer, "hello_invalid")
            return False
        client_nonce = hello["client_nonce"]
        server_nonce = secrets.token_hex(32)
        await self._send(writer, {
            "type": "challenge", "protocol": PROTOCOL, "version": LINK_VERSION, "server_nonce": server_nonce,
            "server_proof": _proof(self.token, _SERVER_LABEL, client_nonce, server_nonce),
        })
        auth = await read_frame(reader, MAX_HANDSHAKE_FRAME)
        proof = auth.get("client_proof")
        if set(auth) != {"type", "client_proof"} or auth.get("type") != "auth" or not _is_hex64(proof) \
                or not hmac.compare_digest(proof, _proof(self.token, _CLIENT_LABEL, client_nonce, server_nonce)):
            await self._refuse(writer, "auth_failed")
            return False
        if self._active is not None and not self._active.is_closing():
            await self._refuse(writer, "busy")
            return False
        self._note("authenticated", self.principal)
        await self._send(writer, {
            "type": "ready", "session_id": secrets.token_hex(16), "principal": self.principal,
            "avatar": self.avatar, "room_id": self.room_id,
            "limits": {"max_request_frame": self.max_request_frame, "max_response_frame": MAX_RESPONSE_FRAME},
        })
        return True

    async def _requests(self, reader, writer) -> None:
        while True:
            try:
                frame = await read_frame(reader, self.max_request_frame)
            except (asyncio.IncompleteReadError, ConnectionError, OSError):
                return
            except LinkError as error:
                self._note("frame_invalid", str(error)[:120])
                await self._refuse(writer, "frame_invalid")
                return
            seq = frame.get("seq")
            if set(frame) != {"type", "seq", "message"} or frame.get("type") != "request" \
                    or not isinstance(seq, int) or isinstance(seq, bool) or not 0 < seq < 2 ** 53:
                # Only requests travel this way. There is no frame for approvals, principals or tokens.
                self._note("frame_invalid", "not a request frame")
                await self._refuse(writer, "frame_invalid")
                return
            # The principal is the connection's, assigned at authentication. Nothing in the frame sets it.
            result = self.handler(self.principal, frame["message"])
            response = encode_frame({"type": "response", "seq": seq, "message": result})
            if len(response) - 4 > MAX_RESPONSE_FRAME:
                # The host never sends a frame the companion would have to refuse.
                response = encode_frame({"type": "response", "seq": seq, "message": _too_large(result)})
            try:
                writer.write(response)
                await writer.drain()
            except (ConnectionError, OSError):
                return


def _too_large(result: dict) -> dict:
    keep = ("schema", "version", "op", "action_id", "query_id", "principal", "room_id", "revision", "replayed",
            "preview", "at_utc")
    smaller = {k: result[k] for k in keep if k in result}
    smaller["ok"] = False
    smaller["error"] = {"code": "internal_error", "message": "The answer was too large to send.", "retryable": False}
    return smaller


# ---------------------------------------------------------------------------- companion side

class LinkClient:
    """The MCP server's end of the link."""

    def __init__(self, session_loader: Callable[[], SessionInfo], *, request_timeout_s: float = REQUEST_TIMEOUT_S,
                 max_request_frame: int = MAX_REQUEST_FRAME):
        self._load = session_loader
        self.request_timeout_s = request_timeout_s
        self.max_request_frame = max_request_frame
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._seq = 0
        self._lock = asyncio.Lock()
        self.ready: dict | None = None

    @staticmethod
    def from_file(path: Path, **kwargs) -> "LinkClient":
        path = Path(path)

        def load() -> SessionInfo:
            try:
                with path.open("rb") as handle:
                    raw = handle.read(MAX_SESSION_FILE + 1)
            except FileNotFoundError:
                raise LinkError("the game is not running (no session file yet)") from None
            except OSError:
                raise LinkError("the game's session file cannot be read") from None
            if len(raw) > MAX_SESSION_FILE:
                raise LinkError("the game's session file is too large to be one")
            return parse_session(raw)

        return LinkClient(load, **kwargs)

    @property
    def connected(self) -> bool:
        return self._writer is not None and not self._writer.is_closing()

    async def close(self) -> None:
        if self._writer is not None:
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except (ConnectionError, OSError):
                pass
        self._reader = self._writer = None
        self.ready = None

    async def connect(self) -> dict:
        await self.close()
        info = self._load()
        # Connect only to a loopback literal, whatever the loader returned (defence in depth: the
        # server process's sandbox does not see connects made through the Windows event loop).
        if info.host not in LOOPBACK_HOSTS:
            raise LinkError("refusing to connect anywhere but this computer")
        try:
            reader, writer = await asyncio.wait_for(
                asyncio.open_connection(info.host, info.port, limit=MAX_RESPONSE_FRAME), CONNECT_TIMEOUT_S)
        except (OSError, asyncio.TimeoutError):
            raise LinkError("the game is not accepting companion connections") from None
        try:
            ready = await asyncio.wait_for(self._handshake(reader, writer, info), HANDSHAKE_TIMEOUT_S)
        except asyncio.TimeoutError:
            writer.close()
            raise LinkError("the game did not finish the handshake") from None
        except LinkError:
            writer.close()
            raise
        except (asyncio.IncompleteReadError, ConnectionError, OSError):
            writer.close()
            raise LinkError("the game closed the connection during the handshake") from None
        except Exception:  # anything malformed from the listener is a failed handshake, never a crash
            writer.close()
            raise LinkError("the listener sent a malformed handshake; refusing to continue") from None
        self._reader, self._writer, self.ready = reader, writer, ready
        return ready

    async def _handshake(self, reader, writer, info: SessionInfo) -> dict:
        client_nonce = secrets.token_hex(32)
        writer.write(encode_frame({"type": "hello", "protocol": PROTOCOL, "version": LINK_VERSION,
                                   "client_nonce": client_nonce}))
        await writer.drain()
        challenge = await read_frame(reader, MAX_HANDSHAKE_FRAME)
        if challenge.get("type") == "refused":
            raise LinkError(f"the game refused the connection ({_safe_code(challenge.get('code'))})")
        server_nonce = challenge.get("server_nonce")
        server_proof = challenge.get("server_proof")
        if (challenge.get("type") != "challenge" or challenge.get("protocol") != PROTOCOL
                or challenge.get("version") != LINK_VERSION or not _is_hex64(server_nonce)
                or not _is_hex64(server_proof)
                or not hmac.compare_digest(server_proof, _proof(info.token, _SERVER_LABEL, client_nonce, server_nonce))):
            raise LinkError("the listener could not prove it is this game session; refusing to send anything")
        writer.write(encode_frame({"type": "auth",
                                   "client_proof": _proof(info.token, _CLIENT_LABEL, client_nonce, server_nonce)}))
        await writer.drain()
        ready = await read_frame(reader, MAX_HANDSHAKE_FRAME)
        if ready.get("type") == "refused":
            raise LinkError(f"the game refused the connection ({_safe_code(ready.get('code'))})")
        if ready.get("type") != "ready" or ready.get("room_id") != info.room_id \
                or not isinstance(ready.get("principal"), str) or not isinstance(ready.get("avatar"), str):
            raise LinkError("the game sent an unexpected handshake reply")
        return ready

    async def request(self, message: dict) -> dict:
        """Send one command or query and return the game's result. Reconnects once if needed."""
        async with self._lock:
            self._seq += 1
            seq = self._seq
            try:
                frame_bytes = encode_frame({"type": "request", "seq": seq, "message": message})
            except (TypeError, ValueError):
                raise LinkError("the request cannot be encoded as strict JSON") from None
            if len(frame_bytes) - 4 > self.max_request_frame:
                raise LinkError("the request is too large to send")
            if not self.connected:
                await self.connect()
            assert self._reader is not None and self._writer is not None
            try:
                self._writer.write(frame_bytes)
                await self._writer.drain()
                reply = await asyncio.wait_for(read_frame(self._reader, MAX_RESPONSE_FRAME), self.request_timeout_s)
            except asyncio.TimeoutError:
                await self.close()
                raise LinkError("the game did not answer in time; the outcome is unknown") from None
            except (asyncio.IncompleteReadError, ConnectionError, OSError, LinkError):
                await self.close()
                raise LinkError("the connection to the game was lost; the outcome is unknown") from None
            if reply.get("type") != "response" or reply.get("seq") != seq or not isinstance(reply.get("message"), dict):
                await self.close()
                raise LinkError("the game sent an unexpected reply; the outcome is unknown")
            return reply["message"]
