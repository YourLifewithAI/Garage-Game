#!/usr/bin/env bash
# Headless Linux mirror of run-engine-tests.ps1 and tools/test-room.ps1, plus the contract and companion tests.
# Run tools/linux/setup-toolchain.sh first. Exits non-zero if anything fails; stderr output from
# Godot counts as a failure, exactly as in the Windows runners.
set -u
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LINUX="$REPO/.cache/linux"
GODOT="$LINUX/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64"
[ -x "$GODOT" ] && [ -x "$LINUX/dotnet/dotnet" ] && [ -x "$LINUX/companion-venv/bin/python" ] || { echo "Run tools/linux/setup-toolchain.sh first." >&2; exit 2; }
export DOTNET_ROOT="$LINUX/dotnet" PATH="$LINUX/dotnet:$PATH" DOTNET_CLI_HOME="$LINUX/dotnet-home" NUGET_PACKAGES="$LINUX/nuget" \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# Keep test saves away from any real player data.
export XDG_DATA_HOME="$LINUX/test-data" XDG_CONFIG_HOME="$LINUX/test-config"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME"
P="$REPO/game"; LOG="$LINUX/test-logs"; mkdir -p "$LOG"; failed=0
# --scale runs the scale suite instead, and only it (tools/test-room.ps1 -Scale): the 10x garage, built once into the fixture cache.
SCALE=0; [ "${1:-}" = "--scale" ] && SCALE=1

# The garage landscape fixture, shared by every worktree of this clone (tools/landscape-fixture.ps1 on Windows, the same key): derived
# data never committed, under the main worktree's .cache/landscape-fixtures (or $GARAGE_GAME_FIXTURE_CACHE), x1/<key> and x10/<key>,
# keyed by the generator, the exporter, the harness and the corpus room (line endings normalised where Git converts them), built by
# one checkout at a time under flock into <key>.partial and moved into place; its files are read-only for readers.
landscape_key() {
  (cd "$REPO" && find pipeline/landscape/generator pipeline/landscape/export pipeline/landscape/harness pipeline/landscape/corpus/rooms/garage_nominal -type f \( -name '*.py' -o -name '*.json' \) -not -path '*/tests/*' -not -path '*/renders/*' -not -path '*/__pycache__/*' | LC_ALL=C sort |
    while IFS= read -r f; do
      case "$f" in pipeline/landscape/generator/*|pipeline/landscape/export/*) h=$(sed 's/\r$//' "$f" | sha256sum | cut -d' ' -f1);; *) h=$(sha256sum < "$f" | cut -d' ' -f1);; esac
      printf '%s  %s\n' "$h" "$f"
    done | sha256sum | cut -c1-16)
}
fixture_cache() {
  if [ -n "${GARAGE_GAME_FIXTURE_CACHE:-}" ]; then echo "$GARAGE_GAME_FIXTURE_CACHE"; return; fi
  local common; common=$(git -C "$REPO" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)
  if [ -n "$common" ] && [ "$(basename "$common")" = .git ]; then echo "$(dirname "$common")/.cache/landscape-fixtures"; else echo "$REPO/.cache/landscape-fixtures"; fi
}
landscape_fixture() { # 1 or 10; prints the exported room's folder, built first if missing (its log: $LOG/landscape_fixture_x<N>.log)
  local scale="$1" key variant final part source=pipeline/landscape/corpus/rooms/garage_nominal start=$SECONDS
  key=$(landscape_key); variant="$(fixture_cache)/x$scale"; final="$variant/$key"
  if [ ! -f "$final/fixture.json" ]; then
    mkdir -p "$variant"
    exec 9>"$variant/$key.lock"; flock 9   # waits while another checkout builds the same fixture
    if [ ! -f "$final/fixture.json" ]; then
      part="$variant/$key.partial"; rm -rf "$part"; mkdir -p "$part"
      if ! (cd "$REPO" && if [ "$scale" != 1 ]; then
              python3 -B -m pipeline.landscape.generator.generate --room "$source" --world-scale "$scale" --out "$part/package" --tutorial
            else python3 -B -m pipeline.landscape.generator.generate --room "$source" --out "$part/package"; fi &&
            python3 -B -S -m pipeline.landscape.export --package "$part/package" --room "$source" --room-id landscape_garage_nominal --out "$part/landscape_garage_nominal") \
          > "$LOG/landscape_fixture_x$scale.log" 2>&1; then flock -u 9; exec 9>&-; return 1; fi
      rm -rf "$part/package"
      printf '{"key":"%s","world_scale":%s,"seconds":%s,"room_bytes":%s,"built_in":"%s"}\n' "$key" "$scale" "$((SECONDS - start))" \
        "$(du -sb "$part/landscape_garage_nominal" | cut -f1)" "$REPO" > "$part/fixture.json"
      find "$part" -type f -exec chmod a-w {} +
      mv "$part" "$final"
      # Older keys go once four newer exist and they are three days old.
      ls -1dt "$variant"/???????????????? 2>/dev/null | tail -n +5 | while IFS= read -r old; do
        if [ -n "$(find "$old" -maxdepth 0 -mtime +3)" ]; then chmod -R u+w "$old"; rm -rf "$old"; fi
      done
    fi
    flock -u 9; exec 9>&-
  fi
  echo "$final/landscape_garage_nominal"
}
# Godot's Jolt job system warns on stderr when its job queue fills under load and then waits (seen on a four-core cloud container,
# 24 October, in the body knacks scene, with every check passing); the game wrote nothing, so those two lines alone are not a
# failure. Anything else on stderr still is, exactly as in the Windows runners.
stderr_counts() { grep -v -E 'Jolt Physics job system exceeded the maximum number of jobs|at: CreateJob \(modules/jolt_physics/spaces/jolt_job_system\.cpp' "$1" | grep -q '[^[:space:]]'; }
# Containers and WSL often run as root, and Godot then warns on stderr, which every check here treats as a failure.
export GODOT_SILENCE_ROOT_WARNING=1

echo "== C# build"
if ! dotnet build "$P/GarageGame.csproj" -nologo -v:q -warnaserror > "$LOG/build.log" 2>&1; then cat "$LOG/build.log"; failed=1; else echo "PASS build (warnings are errors)"; fi
echo "== import"
timeout 300 "$GODOT" --headless --editor --path "$P" --import > "$LOG/import.out" 2> "$LOG/import.err" || true
if grep -qE "ERROR|SCRIPT ERROR" "$LOG/import.err"; then grep -E "ERROR" "$LOG/import.err" | head -20; failed=1; else echo "PASS import"; fi

if [ $SCALE -eq 1 ]; then
  echo "== scale suite (the 10x garage)"
  if ! LR10=$(landscape_fixture 10); then echo "FAIL 10x landscape fixture"; tail -15 "$LOG/landscape_fixture_x10.log"; echo "== RED"; exit 1; fi
  timeout 1500 "$GODOT" --headless --path "$P" --fixed-fps 60 res://tests/native_scale_suite.tscn -- --landscape="$LR10" > "$LOG/scale_suite.out" 2> "$LOG/scale_suite.err"; code=$?
  if [ $code -ne 0 ] || stderr_counts "$LOG/scale_suite.err" || ! grep -qE 'NATIVE_SCALE_SUITE: [0-9]+/[0-9]+ checks passed' "$LOG/scale_suite.out"; then
    echo "FAIL scale suite (exit $code)"; tail -20 "$LOG/scale_suite.out"; echo "--- stderr"; head -30 "$LOG/scale_suite.err"; echo "== RED"; exit 1
  fi
  grep -E '^(SCALE_|NATIVE_SCALE_SUITE)' "$LOG/scale_suite.out"; echo "== GREEN"; exit 0
fi

run() { # name, expected stdout regex, godot args...
  local name="$1" expect="$2"; shift 2
  timeout 180 "$GODOT" "$@" > "$LOG/$name.out" 2> "$LOG/$name.err"; local code=$?
  if [ $code -ne 0 ] || stderr_counts "$LOG/$name.err" || ! grep -qE "$expect" "$LOG/$name.out"; then
    echo "FAIL $name (exit $code)"; tail -15 "$LOG/$name.out"; echo "--- stderr"; head -30 "$LOG/$name.err"; failed=1
  else echo "PASS $name: $(grep -E "$expect" "$LOG/$name.out" | tail -1 | cut -c1-150)"; fi
}

echo "== kernel suites (list from run-engine-tests.ps1)"
for t in $(grep -oE "'[a-z_0-9]+\.gd'" "$REPO/run-engine-tests.ps1" | tr -d "'"); do
  run "${t%.gd}" "." --headless --path "$P" --script "res://tests/$t"
done
echo "== native fixtures and room boot"
run native_contract_probe "Native release probe passed" --headless --path "$P" res://scenes/native_contract_probe.tscn
run native_small_avatar "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_small_avatar.tscn
run native_characters "NATIVE_CHARACTERS: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_characters.tscn -- --skip-budget
run native_body_knacks "NATIVE_BODY_KNACKS: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_body_knacks.tscn
run native_kernel_bubble "NATIVE_KERNEL_BUBBLE: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_bubble.tscn
run native_playtest "NATIVE_KERNEL_PLAYTEST: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_playtest.tscn
run native_body_knacks_boot "NATIVE_BODY_KNACKS_BOOT: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_body_knacks.tscn -- --boot-room --knack=quick_feet:3 --knack=cat_feet:2
run native_team_saves "NATIVE_TEAM_SAVES: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_team_saves.tscn
run native_menu "NATIVE_MENU: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native/Menu/native_menu.tscn
run native_character_gaits "NATIVE_CHARACTER_GAITS: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_character_gaits.tscn -- --skip-budget
run native_character_contact "NATIVE_CHARACTER_CONTACT: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_character_contact.tscn
# 40 seeded tumbles per character and ground (the Windows runner takes all 200, about 4 minutes) to fit run()'s 180 s.
run native_character_tumble "NATIVE_CHARACTER_TUMBLE: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_character_tumble.tscn -- --tumbles=40 --skip-budget
run native_room_navigation "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_room_navigation.tscn
# Play on the land: the 1x garage landscape from the shared fixture cache (above; built there first when missing).
if ! LR=$(landscape_fixture 1); then echo "FAIL landscape fixture"; tail -15 "$LOG/landscape_fixture_x1.log"; failed=1; fi
run native_kernel_landscape "NATIVE_KERNEL_LANDSCAPE: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_landscape.tscn -- --landscape="$LR"
run native_room_data "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_room_data.tscn
run native_island_loading "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_island_loading.tscn
run native_look_preset "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_look_preset.tscn -- --landscape="$LR"
if grep -q "checks were skipped" "$LOG/native_look_preset.out"; then echo "FAIL native_look_preset skipped its real-garage checks"; grep "checks were skipped" "$LOG/native_look_preset.out"; failed=1; fi
run native_look_characters "NATIVE_LOOK_CHARACTERS: [0-9]+/[0-9]+ checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_look_characters.tscn
# The boot carries a team, a mode and a difficulty as run-room.ps1 passes them, honoured since M3.
run room_boot "ROOM_WORLD_QUIT" --headless --path "$P" --fixed-fps 60 res://scenes/room.tscn -- --quit-after-ready=240 --team=boot_probe --mode=challenge --difficulty=gentle
# The landscape as the player opens it: installed in the user folder and booted by id through the main scene (which reads --room),
# quitting by --quit-after-ready (a smoke boot of 13 October never quit: it ran an assembly built before the option existed).
UR="$XDG_DATA_HOME/godot/app_userdata/Garage-Game/rooms/landscape_garage_nominal"
rm -rf "$UR"; mkdir -p "$(dirname "$UR")"; cp -r "$LR" "$UR"; chmod -R u+w "$UR"
run main_scene_landscape_boot "ROOM_WORLD_QUIT" --headless --path "$P" --fixed-fps 60 -- --room=landscape_garage_nominal --quit-after-ready=240
if ! grep -q "^ROOM_WORLD_READY room=landscape_garage_nominal " "$LOG/main_scene_landscape_boot.out"; then echo "FAIL main_scene_landscape_boot did not open the landscape"; failed=1; fi
echo "== C# kernel"
run native_kernel_canonical_json "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_canonical_json.tscn
HOST_DUMP="$LOG/command_host_messages"; rm -rf "$HOST_DUMP"
run native_kernel_command_host "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_command_host.tscn -- --dump="$HOST_DUMP"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -I contracts/validate.py "$HOST_DUMP"/*.json > "$LOG/host_messages.log" 2>&1); then
  echo "PASS command host messages validate: $(tail -1 "$LOG/host_messages.log")"
else cat "$LOG/host_messages.log"; failed=1; fi
run native_kernel_play_hud "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_play_hud.tscn
SANDBOX_DUMP="$LOG/sandbox_messages"; rm -rf "$SANDBOX_DUMP"
run native_kernel_sandbox "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_sandbox.tscn -- --dump="$SANDBOX_DUMP"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -I contracts/validate.py "$SANDBOX_DUMP"/*.json > "$LOG/sandbox_messages.log" 2>&1); then
  echo "PASS sandbox messages validate: $(tail -1 "$LOG/sandbox_messages.log")"
else cat "$LOG/sandbox_messages.log"; failed=1; fi
JOURNAL_DUMP="$LOG/journal_messages"; rm -rf "$JOURNAL_DUMP"
run native_kernel_journal "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_journal.tscn -- --dump="$JOURNAL_DUMP"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -I contracts/validate.py "$JOURNAL_DUMP"/*.json > "$LOG/journal_messages.log" 2>&1); then
  echo "PASS journal messages validate: $(tail -1 "$LOG/journal_messages.log")"
else cat "$LOG/journal_messages.log"; failed=1; fi
REBUILD_DUMP="$LOG/rebuild_states"; rm -rf "$REBUILD_DUMP"
run native_kernel_rebuild "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_rebuild.tscn -- --dump="$REBUILD_DUMP"
if (cd "$REPO" && for s in room_state_before room_state_after; do "$LINUX/contracts-venv/bin/python" -I contracts/validate.py --state "$REBUILD_DUMP/$s.json" --room game/rooms/test_room || exit 1; done > "$LOG/rebuild_states.log" 2>&1); then
  echo "PASS rebuilt room states validate against the test room: $(tail -1 "$LOG/rebuild_states.log")"
else cat "$LOG/rebuild_states.log"; failed=1; fi
NATURE_DUMP="$LOG/nature_messages"; rm -rf "$NATURE_DUMP"
run native_kernel_nature "checks passed" --headless --path "$P" --fixed-fps 60 res://tests/native_kernel_nature.tscn -- --dump="$NATURE_DUMP"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -I contracts/validate.py "$NATURE_DUMP"/*.json > "$LOG/nature_messages.log" 2>&1); then
  echo "PASS nature messages validate: $(tail -1 "$LOG/nature_messages.log")"
else cat "$LOG/nature_messages.log"; failed=1; fi
echo "== kernel canonical JSON (Python reference)"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" tools/kernel/canonical_json.py --check game/tests/fixtures/kernel > "$LOG/canonical.log" 2>&1 \
    && "$LINUX/contracts-venv/bin/python" -m unittest discover -s tools/kernel >> "$LOG/canonical.log" 2>&1); then
  echo "PASS $(head -1 "$LOG/canonical.log" | cut -c1-150)"
else cat "$LOG/canonical.log"; failed=1; fi
echo "== contracts"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -m unittest discover -s contracts/tests > "$LOG/contracts.log" 2>&1); then
  echo "PASS contracts: $(grep -E '^Ran' "$LOG/contracts.log")"
else cat "$LOG/contracts.log"; failed=1; fi
echo "== playtest reader (R2; the contact sheet's tests skip without Pillow)"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -m unittest discover -s tools/playtest/tests > "$LOG/playtest.log" 2>&1); then
  echo "PASS playtest reader: $(grep -E '^Ran' "$LOG/playtest.log")"
else cat "$LOG/playtest.log"; failed=1; fi
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -I contracts/validate.py game/rooms/test_room game/styles/*/v*.json game/rules/*/v*.json > "$LOG/validate.log" 2>&1); then
  echo "PASS shipped rooms, presets and island rules validate"
else cat "$LOG/validate.log"; failed=1; fi
echo "== companion"
if (cd "$REPO" && PYTHONPATH="$REPO/companion/src" "$LINUX/companion-venv/bin/python" -m unittest discover -s companion/tests > "$LOG/companion.log" 2>&1); then
  echo "PASS companion: $(grep -E '^Ran' "$LOG/companion.log")"
else cat "$LOG/companion.log"; failed=1; fi
echo "== roomscan (capture pipeline, CPU only)"
if (cd "$REPO/pipeline/roomscan" && uv run --locked pytest -q > "$LOG/roomscan.log" 2>&1); then
  echo "PASS roomscan: $(tail -n 1 "$LOG/roomscan.log")"
else cat "$LOG/roomscan.log"; failed=1; fi
echo "== landscape corpus (synthetic rooms, read through roomscan)"
if (cd "$REPO/pipeline/roomscan" && uv run --locked python -B -m unittest discover -s "$REPO/pipeline/landscape/corpus/tests" -t "$REPO" > "$LOG/corpus.log" 2>&1); then
  echo "PASS landscape corpus: $(grep -E '^Ran' "$LOG/corpus.log")"
else cat "$LOG/corpus.log"; failed=1; fi
echo "== landscape exporter (a package to a game room, validated against the contracts)"
if (cd "$REPO" && "$LINUX/contracts-venv/bin/python" -B -m unittest pipeline.landscape.export.tests.test_export pipeline.landscape.export.tests.test_loose pipeline.landscape.export.tests.test_fractals > "$LOG/landscape_export.log" 2>&1); then
  echo "PASS landscape exporter: $(grep -E '^Ran' "$LOG/landscape_export.log") $(grep -E '^OK' "$LOG/landscape_export.log")"
else echo "FAIL landscape exporter"; tail -20 "$LOG/landscape_export.log"; failed=1; fi
echo "== landscape harness (package format and views; its Blender tests skip without Blender)"
if (cd "$REPO" && python3 -B -S -m unittest pipeline.landscape.harness.tests.test_harness > "$LOG/harness.log" 2>&1); then
  echo "PASS landscape harness: $(grep -E '^Ran' "$LOG/harness.log") $(grep -E '^OK' "$LOG/harness.log")"
else cat "$LOG/harness.log"; failed=1; fi
echo "== landscape generator (determinism and the design's principles on corpus rooms)"
if (cd "$REPO" && python3 -B -m unittest pipeline.landscape.generator.tests.test_generator pipeline.landscape.generator.tests.test_firepits > "$LOG/landscape_generator.log" 2>&1); then
  echo "PASS landscape generator: $(grep -E '^Ran' "$LOG/landscape_generator.log") $(grep -E '^OK' "$LOG/landscape_generator.log")"
else echo "FAIL landscape generator"; tail -20 "$LOG/landscape_generator.log"; failed=1; fi

echo "== $([ $failed -eq 0 ] && echo GREEN || echo RED)"
exit $failed
