#!/usr/bin/env bash
# Pictures of the play HUD from a cloud container, so an agent that changes the HUD can look at what it made.
# Runs the game's own HUD capture scene (res://tests/native_kernel_play_hud_capture.tscn) in a virtual display (Xvfb),
# drawn by Mesa's software OpenGL. Nothing is installed: it needs xvfb-run and libgl1-mesa-dri, which the cloud image has.
#
# What the pictures are good for: the HUD's layout, words, icons and sizes at 1280x720.
# What they are not: the look. Software OpenGL means the Compatibility renderer, so there is no GI, no depth of field and
# no post effect, and the land's colours are off. The look and every frame time still need a GPU (tools/look/capture-look.ps1).
#
# Usage: tools/linux/capture-hud.sh OUT_DIR [SCENARIOS] [ROOM_DIR]
#   SCENARIOS  comma-separated, as PlayHudTestCapture.cs lists them (default: play_challenge)
#   ROOM_DIR   an exported room; the test room without it
# Measured 19 November 2026 on a four-core container: the test room about 70 s for one scenario; the 10x garage island about
# 9 minutes for one, so keep island captures to the one view in question.
set -euo pipefail
[ $# -ge 1 ] || { sed -n '2,15p' "$0" >&2; exit 2; }
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LINUX="$REPO/.cache/linux"
GODOT="$LINUX/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64"
[ -x "$GODOT" ] && [ -x "$LINUX/dotnet/dotnet" ] || { echo "Run tools/linux/setup-toolchain.sh first." >&2; exit 2; }
command -v xvfb-run >/dev/null || { echo "xvfb-run is not on this machine; the capture needs a virtual display." >&2; exit 2; }
OUT="$(mkdir -p "$1" && cd "$1" && pwd)"; SCENARIOS="${2:-play_challenge}"; ROOM="${3:-}"
export DOTNET_ROOT="$LINUX/dotnet" PATH="$LINUX/dotnet:$PATH" DOTNET_CLI_HOME="$LINUX/dotnet-home" NUGET_PACKAGES="$LINUX/nuget" \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 GODOT_SILENCE_ROOT_WARNING=1
# A scratch profile, apart from the tests' and from any real player data.
export XDG_DATA_HOME="$LINUX/capture-data" XDG_CONFIG_HOME="$LINUX/capture-config"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME"
dotnet build "$REPO/game/GarageGame.csproj" -nologo -v:q -warnaserror > "$OUT/build.log" 2>&1 || { cat "$OUT/build.log" >&2; exit 1; }
[ -d "$REPO/game/.godot/imported" ] || timeout 600 "$GODOT" --headless --editor --path "$REPO/game" --import > "$OUT/import.log" 2>&1 || true
args=(--out="$OUT" --tag=cloud --scenarios="$SCENARIOS" --no-intro)
[ -n "$ROOM" ] && args+=(--room="$(cd "$ROOM" && pwd)")
code=0
timeout "${CAPTURE_TIMEOUT_S:-900}" xvfb-run -a -s "-screen 0 1280x720x24" "$GODOT" --path "$REPO/game" --rendering-driver opengl3 \
  --resolution 1280x720 res://tests/native_kernel_play_hud_capture.tscn -- "${args[@]}" > "$OUT/run.out" 2> "$OUT/run.err" || code=$?
grep -E '^PLAY_HUD_CAPTURE' "$OUT/run.out" || true
[ $code -eq 0 ] || { echo "capture ended with exit $code (124 is the timeout); see $OUT/run.out and run.err" >&2; exit $code; }
