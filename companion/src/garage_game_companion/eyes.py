"""The Gubble's eyes (view.look) on the mock host and at the adapter: the kernel's numbers and words, and a placeholder.

The kernel host (game/scripts/native/Kernel/CommandHostEyes.cs) draws a real picture from the Gubble's eye in the shared
world; the mock has no renderer, so it answers with a small generated placeholder (a sky over ground, the horizon moved
by the eye's pitch and a mark by its yaw) carrying the same facts. test_kernel_alignment.py reads the kernel's constants
and refusal words and fails when the two drift apart.
"""
from __future__ import annotations

import base64
import math
import struct
import zlib

GUBBLE = "avatar:companion"
OP = "view.look"
TOOL = "view_look"

# The kernel's numbers (CommandHostEyes.cs).
LOOK_BURST = 3  # looks a companion may take at once
LOOK_REFILL_S = 1.0  # one more look comes back this often
LOOK_IMAGE_MAX_BYTES = 98304  # the encoded picture's hard cap, 96 KiB
LOOK_WIDTH_PX = 512
LOOK_HEIGHT_PX = 288
LOOK_MAX_YAW_DEG = 90.0
LOOK_MAX_PITCH_DEG = 60.0
EYES_PROFILE_KEY = "ai_eyes"
DEFAULT_GUBBLE_EYES = True
# The body's eye camera (SmallPlayerController: EyeCamera Fov = 72.0f), vertical, as Godot's cameras keep the height.
EYE_VERTICAL_FOV_DEG = 72.0
# The mock's placeholder is smaller than the kernel's picture: it only has to be a valid, honest stand-in.
PLACEHOLDER_WIDTH_PX = 128
PLACEHOLDER_HEIGHT_PX = 72

# The kernel's words, letter for letter.
EYES_OFF_MESSAGE = "The player has turned off \"Let my AI see through the Gubble's eyes\". Use observe instead."
NO_RENDERER_MESSAGE = "This game runs without a renderer (headless), so the Gubble cannot take a picture. Use observe instead."
LOOK_RATE_MESSAGE = "The Gubble looks about once a second; wait a moment and look again."
LOOK_ACTOR_MESSAGE = "Only the Gubble's own eyes can be looked through."


class LookBucket:
    """The kernel's token bucket (TakeLookToken): LOOK_BURST at most, one more every LOOK_REFILL_S, on the host's clock."""

    def __init__(self, burst: int = LOOK_BURST, refill_s: float = LOOK_REFILL_S):
        self.burst, self.refill_s = burst, refill_s
        self.tokens = float(burst)
        self.at: float | None = None

    def take(self, now: float) -> bool:
        if self.at is not None:
            self.tokens = min(float(self.burst), self.tokens + max(0.0, now - self.at) / self.refill_s)
        self.at = now
        if self.tokens < 1.0:
            return False
        self.tokens -= 1.0
        return True


def round1(value: float) -> float:
    """One decimal, halves away from zero (C#'s MidpointRounding.AwayFromZero), never -0."""
    rounded = math.copysign(math.floor(abs(value) * 10 + 0.5) / 10, value)
    return 0.0 if rounded == 0 else rounded


def positive_degrees(degrees: float) -> float:
    return degrees % 360.0


def heading(x: float, z: float) -> float:
    """A facing on the floor (x, z) as degrees from the room's -Z turning toward +X: 0 is -Z, 90 is +X; 0 to 360."""
    if x * x + z * z < 1e-12:
        return 0.0
    value = round1(positive_degrees(math.degrees(math.atan2(x, -z))))
    return 0.0 if value >= 360.0 else value


def horizontal_fov_deg(vertical_deg: float = EYE_VERTICAL_FOV_DEG, width: int = LOOK_WIDTH_PX,
                       height: int = LOOK_HEIGHT_PX) -> float:
    return round1(2 * math.degrees(math.atan(math.tan(math.radians(vertical_deg) / 2) * width / height)))


def day_phase(hour: float, sun_elevation_deg: float) -> str:
    """The island's light in a word (CommandHost.DayPhase)."""
    if sun_elevation_deg <= -6:
        return "night"
    if sun_elevation_deg < 3:
        return "dawn" if hour < 12 else "dusk"
    if sun_elevation_deg < 12:
        return "morning" if hour < 12 else "evening"
    return "morning" if hour < 11 else "noon" if hour < 13 else "afternoon"


def placeholder_png(yaw_deg: float = 0.0, pitch_deg: float = 0.0, width: int = PLACEHOLDER_WIDTH_PX,
                    height: int = PLACEHOLDER_HEIGHT_PX) -> bytes:
    """A small honest stand-in for a picture: sky over ground, the horizon lower as the eye tilts up, a pale mark that
    moves left as the eye turns right (the way the room seems to move). Deterministic, so results compare."""
    horizon = height / 2 + pitch_deg / LOOK_MAX_PITCH_DEG * (height / 2)
    mark = width / 2 - yaw_deg / LOOK_MAX_YAW_DEG * (width / 2)
    rows = bytearray()
    for y in range(height):
        rows.append(0)  # filter: none
        for x in range(width):
            if abs(x - mark) < 1.5:
                pixel = (236, 226, 196)
            elif y < horizon:
                t = y / max(1.0, horizon)
                pixel = (int(150 + 60 * t), int(190 + 40 * t), int(225 + 20 * t))
            else:
                t = (y - horizon) / max(1.0, height - horizon)
                pixel = (int(120 - 40 * t), int(150 - 50 * t), int(90 - 30 * t))
            rows.extend(pixel)

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)  # 8-bit RGB
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")


def image_block(data: bytes, media_type: str, width: int, height: int) -> dict:
    """The result's image object: the bytes as standard base64, marked untrusted (anything legible in it is world text)."""
    return {"media_type": media_type, "width_px": width, "height_px": height, "bytes": len(data),
            "data_base64": base64.b64encode(data).decode("ascii"), "untrusted": True}
