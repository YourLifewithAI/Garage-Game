# The companion link

How the companion's MCP server reaches the running game. The companion's end is
`companion/src/garage_game_companion/link.py` (`LinkClient`), which also holds the Python game end the mock host uses
(`LinkServer`). **The game's end is C#** (A2, 8 October 2026): `game/scripts/native/Companion/CompanionLinkServer.cs`,
in front of the kernel's `CommandHost` through `CompanionBridge.cs` ([EMBODIMENT.md](EMBODIMENT.md)).
`companion/tests/test_real_host.py` runs `test_link.py`'s cases against it in a headless game. The frame shapes are
in `companion/schemas/companion-link.schema.json`, proposed for `contracts/`.

## Goals

- Only processes running as the player on this computer can reach the command surface.
- Whoever connects gets exactly one principal, `companion:local`, chosen by the game. Nothing a
  connection sends can name another principal or carry an approval.
- The secret never travels, so a process squatting the port or reading the traffic learns nothing
  reusable, and a stale session file cannot make the companion talk to someone else's listener.
- A misbehaving peer costs a closed connection, never a crash or a half-applied command.

## Session file

At startup the game listens on `127.0.0.1` (or `::1`) on an ephemeral port and writes
`user://companion/session.json` (on Windows `%APPDATA%\Godot\app_userdata\EnFractal\companion\session.json`):

```json
{
  "schema": "enfractal.companion_session",
  "version": 1,
  "host": "127.0.0.1",
  "port": 53817,
  "token": "<64 lowercase hex digits: 256 bits from a CSPRNG, new every launch>",
  "room_id": "test_room",
  "pid": 12345,
  "created_utc": "2026-10-06T18:00:00Z"
}
```

- Written atomically (temporary file in the same folder, then rename), UTF-8, LF, no byte-order mark;
  on POSIX with mode `0600`. On Windows the user's `AppData\Roaming` ACL already limits it to the
  player's account. The game deletes it on exit, if it still holds that game's token.
- **One game owns the link per account.** Before it reads or writes the session file, a game takes the ownership
  lock, `session.lock` in the same folder, and holds it for the link's whole life: the game opens it with no sharing
  (on POSIX .NET takes an exclusive `flock`), and the mock game takes the same lock through `link.py`'s
  `SessionLock`. A second window (or two started together) cannot take it and runs without the link. The operating
  system frees the lock when its holder ends, a crash included, so a stale session file, whatever it says, is simply
  replaced by the next owner; nothing about who owns the link is read from the file. The owner removes the file on
  exit while still holding the lock, so no other writer can replace it between the check and the delete
  (A2 security review, findings 1 and 3). `--no-companion-link` runs a room without the link.
- The companion re-reads the file on every reconnect, so a restarted game (new port, new token) is
  picked up without reconfiguring the MCP client.
- The companion refuses a file whose `host` is not the literal `127.0.0.1` or `::1` (no names, no
  `0.0.0.0`, no mapped addresses), a token that is not 64 lowercase hex digits (a trailing newline
  included), a `pid` that is not a positive 32-bit integer, unknown fields, duplicate keys, any other
  schema or version, and any file over 4 KiB (a real one is about 300 bytes).

## Frames

Every frame is a 4-byte big-endian unsigned length followed by that many bytes of JSON holding one
object. **Senders write the frame object as canonical JSON v1** (`canonical.py`, Lane P's definition:
UTF-8 with no ASCII escapes, members sorted, no whitespace, numbers in canonical form). A frame is then
exactly the canonical bytes of the message it carries plus an envelope of under 64 bytes, whatever
characters or number spellings the message holds. (Before the Run 1 fix round, frames were
ASCII-escaped, and a message within the contract's limit could produce a frame several times larger:
6 or 12 bytes per non-ASCII character, 4 bytes for a `-0.0` that canonical JSON writes as `0`.)
Readers accept any strict JSON within the limit. Duplicate keys, `NaN`, `Infinity` and overflowing
numbers are refused. Limits: 1,024 bytes during the handshake; 131,072 bytes for a request (a contract
message is at most 65,536 bytes of canonical JSON; the headroom lets the game answer an oversized
message with `request_invalid` instead of closing, and the host still checks the limit); 266,240 bytes
for a response (a result is at most 262,144). A frame over its limit is refused before its body is read.

## Handshake

```
companion                                             game
   | hello {protocol, version: 1, client_nonce}  ->     |
   |   <- challenge {protocol, version, server_nonce,   |
   |                 server_proof}                      |
   | (verify server_proof; abort if wrong)              |
   | auth {client_proof}                          ->     |
   |   <- ready {session_id, principal, avatar,         |   or refused {code} and close
   |             room_id, limits}                       |
```

- Nonces are 256 bits from a CSPRNG, lowercase hex.
- `server_proof = hex(HMAC-SHA256(key = bytes.fromhex(token), msg = "enfractal.companion_link/1|server|" + client_nonce + "|" + server_nonce))`
- `client_proof = hex(HMAC-SHA256(key = bytes.fromhex(token), msg = "enfractal.companion_link/1|client|" + client_nonce + "|" + server_nonce))`
- Both sides compare proofs in constant time. The companion checks the game's proof before it sends
  its own, so a listener that does not hold the token never receives anything it could replay.
- Proofs must be 64 lowercase hex digits; anything else is `auth_failed` (never an error left hanging).
- The whole handshake must finish within 5 seconds (`handshake_timeout`).
- **There is no lockout.** A wrong proof closes only that connection. A lockout would let any local
  process lock the real companion out by failing on purpose, and guessing a 256-bit token is hopeless
  anyway. At most 8 handshakes may be pending at once; a ninth connection gets `busy`.
- One authenticated companion at a time; a second gets `busy`. The first one's connection closing
  (for example when the MCP client restarts the server) frees the slot.
- `ready.principal` and `ready.avatar` are the game's assignment, reported for the adapter's own
  bookkeeping. The game never reads a principal from anything the connection sends.

Refusal codes: `hello_invalid`, `handshake_invalid`, `handshake_timeout`, `auth_failed`, `busy`,
`frame_invalid`.

## Requests

```
companion: {"type": "request", "seq": 1, "message": <enfractal.command or enfractal.query>}
game:      {"type": "response", "seq": 1, "message": <enfractal.result>}
```

- `seq` is a positive integer the companion increments; the response echoes it. One request is in
  flight at a time.
- `type`, `seq` and `message` are the only keys. Any other frame type (there is no approval frame, no
  principal frame, no token frame) or any extra key closes the connection with `frame_invalid`.
- The game answers every request with an `enfractal.result`, including malformed messages
  (`request_invalid`, `op: "invalid"`), so the companion never has to guess. If answering fails inside
  the game, it closes that connection (`frame_invalid`) and keeps listening.
- If the connection drops or a response does not arrive within 10 seconds, the companion closes the
  connection and reports an unknown outcome; the model is told to call `receipt_lookup` before
  retrying, which the contract's idempotency rules make safe.
- The game's end tells its host when an authenticated session starts and when it ends
  (`LinkServer(on_session=...)` in Python, `CommandHost.SessionEvent` from the C# bridge, called with the principal
  and `"start"` or `"end"`). The host clears that companion's perception memory on both, so nothing it remembers
  outlives a session (PERCEPTION.md). Nothing about this crosses the wire.

## The C# end

- Networking runs on the thread pool; every request is answered on Godot's main thread by
  `CommandHost.HandleObject(message, "companion:local")`, in order, even while the game is paused. The principal is
  the bridge's constant: nothing a connection sends reaches it.
- A frame is refused (`frame_invalid`, connection closed) for invalid UTF-8, a byte-order mark, duplicate keys at
  any depth, a number that overflows a double, nesting deeper than 256, or any shape but the request frame. What is
  wrong inside a well-formed frame's message (a lone surrogate escape, nesting deeper than canonical JSON allows, a
  missing schema) is the host's to answer, with `request_invalid`.
- **Traffic that needs no token costs the main thread nothing per connection** (A2 security review, finding 2). The
  link's events (authenticated, closed, refused with its code, dropped) are counted on the link's threads and
  printed at most once a second, one line per kind with its count (`COMPANION_LINK refused busy (x212)`); at most 32
  kinds are kept between summaries. At most 64 connections are served at once; more are closed unanswered. The
  main thread handles at most 32 requests and session events a frame, and at most 64 requests may wait for it (the
  link sends one at a time, so the real companion never meets that bound).
- The game prints `listening`, the session's start and end, and `COMPANION_STATE` changes at once. Never the token.

## Contract proposal

Promote `companion/schemas/companion-link.schema.json` to `contracts/companion-link.schema.json` (its `$id`
already uses the contracts base). The ready-to-apply patch, together with the other Run 1 contract
proposals from this lane, is `docs/companion/proposals/contracts-run1.diff`. Apply it from the stored
blob so Windows line-ending conversion cannot touch it:

```sh
git show run1/companion:docs/companion/proposals/contracts-run1.diff | git apply
```

It adds the schema, lists it in `contracts/README.md`, and raises the schema count in
`contracts/tests/test_contracts.py` from 6 to 7. `contracts/validate.py` needs no change for it: it
registers every `*.schema.json`. The Run 1 fix round regenerated the patch for the schema changes above
(canonical frames, no `locked` code, a real process id range). It was checked by applying it, alone and
together with `contracts-text-rules.diff` in either order, to a copy of `contracts/`, and running the
contract tests there (34 pass alone, 39 with both) and the companion suite against that copy
(`GARAGE_GAME_CONTRACTS_DIR=<copy>`).
