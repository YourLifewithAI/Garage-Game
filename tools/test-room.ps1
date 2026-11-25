#requires -Version 7.0
# Runs the small-avatar fixture, the room navigation fixture and the room data/builder fixture, checks the pinned plants, plays the
# 1x garage landscape, then boots the default room headlessly and the landscape through the game's main scene.
# -Scale runs the scale suite instead, and only it: the 10x garage (built once into the shared fixture cache, about 4 min), its
# load, navigation, memory, a long walk across it and a come from far away, one SCALE_* line each (tests/native_scale_suite.tscn).
param([switch]$Scale)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')
. (Join-Path $PSScriptRoot 'landscape-fixture.ps1')
$toolchain = Get-GarageGameNativeToolchain
Build-GarageGameNativeProject -Toolchain $toolchain
$import = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--editor', '--path', $toolchain.ProjectPath, '--import') -TimeoutSeconds 120
if ($import.Stderr -match 'ERROR:|SCRIPT ERROR:') { throw $import.Stderr }
if ($Scale) {
    $island = Get-GarageGameLandscapeFixture -Toolchain $toolchain -Scale 10
    $suite = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath -TimeoutSeconds 1200 `
        -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_scale_suite.tscn', '--', "--landscape=$($island.Room)")
    if ((Get-GarageGameCountedStderr $suite.Stderr) -or $suite.Stdout -notmatch 'NATIVE_SCALE_SUITE: \d+/\d+ checks passed') { throw "$($suite.Stdout)`n$($suite.Stderr)" }
    Write-Output "SCALE_FIXTURE x10 $($island.Key): generated in $($island.Record.seconds.generate) s, exported in $($island.Record.seconds.export) s, $([math]::Round($island.Record.room_bytes / 1MB)) MB"
    Write-Output (($suite.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(SCALE_|NATIVE_SCALE_SUITE)' }) -join "`n")
    return
}
$avatar = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_small_avatar.tscn') -TimeoutSeconds 90
if ((Get-GarageGameCountedStderr $avatar.Stderr) -or $avatar.Stdout -notmatch 'checks passed') { throw "$($avatar.Stdout)`n$($avatar.Stderr)" }
Write-Output $avatar.Stdout.Trim()
$characters = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_characters.tscn') -TimeoutSeconds 120
if ((Get-GarageGameCountedStderr $characters.Stderr) -or $characters.Stdout -notmatch 'NATIVE_CHARACTERS: \d+/\d+ checks passed') { throw "$($characters.Stdout)`n$($characters.Stderr)" }
Write-Output (($characters.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(CHARACTERS_LOAD|NATIVE_CHARACTERS)' }) -join "`n")
# The knacks and the body (the mechanics round, M5): every knack, rank and costume diffed on reach, hop distance, dive length and
# climbable height; then the real game's boot with --knack= arguments, headless, reaching the body.
$knacks = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_body_knacks.tscn') -TimeoutSeconds 120
if ((Get-GarageGameCountedStderr $knacks.Stderr) -or $knacks.Stdout -notmatch 'NATIVE_BODY_KNACKS: \d+/\d+ checks passed') { throw "$($knacks.Stdout)`n$($knacks.Stderr)" }
Write-Output (($knacks.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(KNACK_TABLE|NATIVE_BODY_KNACKS)' }) -join "`n")
$knackBoot = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_body_knacks.tscn', '--', '--boot-room', '--knack=quick_feet:3', '--knack=cat_feet:2') -TimeoutSeconds 120
if ((Get-GarageGameCountedStderr $knackBoot.Stderr) -or $knackBoot.Stdout -notmatch 'NATIVE_BODY_KNACKS_BOOT: \d+/\d+ checks passed') { throw "$($knackBoot.Stdout)`n$($knackBoot.Stderr)" }
Write-Output (($knackBoot.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^NATIVE_BODY_KNACKS_BOOT' }) -join "`n")
# Teams and save paths (the mechanics round, M3): the layout, the team file, the expertise store, the migration from before teams,
# and three game boots (team, mode and difficulty honoured; set-mode and Live refused once the room is up; knack.set at the camp).
$teams = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_team_saves.tscn') -TimeoutSeconds 180
if ((Get-GarageGameCountedStderr $teams.Stderr) -or $teams.Stdout -notmatch 'NATIVE_TEAM_SAVES: \d+/\d+ checks passed') { throw "$($teams.Stdout)`n$($teams.Stderr)" }
Write-Output (($teams.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(TEAM_SAVES_MIGRATION|NATIVE_TEAM_SAVES)' }) -join "`n")
# The opening menu (the mechanics round, M7): the islands, the teams made in the New team panel, the mode and difficulty the island rules offer,
# the Creative copy, Continue, and the walk from the menu to the fixture room with its team, island, mode and difficulty.
$menu = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native/Menu/native_menu.tscn') -TimeoutSeconds 240
if ((Get-GarageGameCountedStderr $menu.Stderr) -or $menu.Stdout -notmatch 'NATIVE_MENU: \d+/\d+ checks passed') { throw "$($menu.Stdout)`n$($menu.Stderr)" }
Write-Output (($menu.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^NATIVE_MENU' }) -join "`n")
# New game's put-away and the team's play lease (Run 4, MENU-2): a new start no longer opens the old game; the put-away's bytes by
# SHA-256; nothing brings a put-away game back; a held lease refuses; a fault part-way leaves the old game whole.
$newGame = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native/Saves/native_new_game.tscn') -TimeoutSeconds 240
if ((Get-GarageGameCountedStderr $newGame.Stderr) -or $newGame.Stdout -notmatch 'NATIVE_NEW_GAME: \d+/\d+ checks passed') { throw "$($newGame.Stdout)`n$($newGame.Stderr)" }
Write-Output (($newGame.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^NATIVE_NEW_GAME' }) -join "`n")
$gaits = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_character_gaits.tscn') -TimeoutSeconds 300
if ((Get-GarageGameCountedStderr $gaits.Stderr) -or $gaits.Stdout -notmatch 'NATIVE_CHARACTER_GAITS: \d+/\d+ checks passed') { throw "$($gaits.Stdout)`n$($gaits.Stderr)" }
Write-Output (($gaits.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(GAITS_CPU|GAITS_BUDGET|NATIVE_CHARACTER_GAITS)' }) -join "`n")
$contact = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_character_contact.tscn') -TimeoutSeconds 120
if ((Get-GarageGameCountedStderr $contact.Stderr) -or $contact.Stdout -notmatch 'NATIVE_CHARACTER_CONTACT: \d+/\d+ checks passed') { throw "$($contact.Stdout)`n$($contact.Stderr)" }
Write-Output (($contact.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(CHARACTERS_INTRUSION|NATIVE_CHARACTER_CONTACT)' }) -join "`n")
$tumble = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_character_tumble.tscn') -TimeoutSeconds 600
if ((Get-GarageGameCountedStderr $tumble.Stderr) -or $tumble.Stdout -notmatch 'NATIVE_CHARACTER_TUMBLE: \d+/\d+ checks passed') { throw "$($tumble.Stdout)`n$($tumble.Stderr)" }
Write-Output (($tumble.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(TUMBLE_COST|NATIVE_CHARACTER_TUMBLE)' }) -join "`n")
$navigation = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_room_navigation.tscn') -TimeoutSeconds 90
if ((Get-GarageGameCountedStderr $navigation.Stderr) -or $navigation.Stdout -notmatch 'checks passed') { throw "$($navigation.Stdout)`n$($navigation.Stderr)" }
Write-Output $navigation.Stdout.Trim()
# The plant maker (Run 4, FLORA-1): its suite, and the pinned plants made again and compared byte for byte, so a stale pinned
# plant fails here. The system python the fixture uses, no install and no network, about ten seconds. The generator's long
# suites are not in this runner: pipeline/landscape/generator/REPORT.md says how they are run by hand.
$python = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$floraSuite = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $python -WorkingDirectory $toolchain.RepositoryPath -TimeoutSeconds 300 `
    -Arguments @('-B', '-m', 'unittest', 'pipeline.landscape.generator.flora.tests.test_flora')
if ("$($floraSuite.Stdout)`n$($floraSuite.Stderr)" -notmatch '(?m)^OK\s*$') { throw "The flora suite did not pass:`n$($floraSuite.Stdout)`n$($floraSuite.Stderr)" }
$floraPinned = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $python -WorkingDirectory $toolchain.RepositoryPath -TimeoutSeconds 300 `
    -Arguments @('-B', '-m', 'pipeline.landscape.generator.flora.make', '--check')
if ($floraPinned.Stdout -notmatch '(?m)^FLORA_PINNED: [1-9]\d* plants, 0 stale\s*$') { throw "A pinned plant is stale:`n$($floraPinned.Stdout)`n$($floraPinned.Stderr)" }
Write-Output ("FLORA_SUITE: {0}" -f ((("$($floraSuite.Stdout)`n$($floraSuite.Stderr)".Split("`n") | Where-Object { $_ -match '^Ran \d+ tests' }) -join ' ').Trim()))
Write-Output (($floraPinned.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^FLORA_PINNED' }) -join "`n").Trim()
# Play on the land: the 1x garage landscape, derived data never committed, from the fixture cache every worktree shares
# (tools/landscape-fixture.ps1; built there first, about 35 s, when the generator, exporter, harness or corpus room changed).
$landscapeRoom = (Get-GarageGameLandscapeFixture -Toolchain $toolchain -Scale 1).Room
$landscape = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_kernel_landscape.tscn', '--', "--landscape=$landscapeRoom") -TimeoutSeconds 300
if ((Get-GarageGameCountedStderr $landscape.Stderr) -or $landscape.Stdout -notmatch 'NATIVE_KERNEL_LANDSCAPE: \d+/\d+ checks passed') { throw "$($landscape.Stdout)`n$($landscape.Stderr)" }
Write-Output (($landscape.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(LANDSCAPE_|NATIVE_KERNEL_LANDSCAPE)' }) -join "`n")
# P-Food: the food's layout on the same garage (its garage checks are skipped without --landscape, which counts as a failure here).
$food = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native/Kernel/native_kernel_food.tscn', '--', "--landscape=$landscapeRoom") -TimeoutSeconds 180
if ((Get-GarageGameCountedStderr $food.Stderr) -or $food.Stdout -match 'FOOD_GARAGE skipped' -or $food.Stdout -notmatch 'NATIVE_KERNEL_FOOD: \d+/\d+ checks passed') { throw "$($food.Stdout)`n$($food.Stderr)" }
Write-Output (($food.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(FOOD_|NATIVE_KERNEL_FOOD)' }) -join "`n")
# LOOK-3: the nature step's measure and audit on the same land: three fires of 1.5 m in a world that changes (about 45 s).
$natureStep = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native/Kernel/native_kernel_nature_step_probe.tscn', '--', "--room=$landscapeRoom", '--audit') -TimeoutSeconds 300
if ((Get-GarageGameCountedStderr $natureStep.Stderr) -or $natureStep.Stdout -notmatch 'NATURE_STEP_DONE') { throw "$($natureStep.Stdout)`n$($natureStep.Stderr)" }
Write-Output (($natureStep.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^NATURE_STEP' }) -join "`n")
$roomData = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_room_data.tscn') -TimeoutSeconds 90
if ((Get-GarageGameCountedStderr $roomData.Stderr) -or $roomData.Stdout -notmatch 'checks passed') { throw "$($roomData.Stdout)`n$($roomData.Stderr)" }
Write-Output $roomData.Stdout.Trim()
# The 10x world's loading (S6): plant copies and terrain tiles read, drawn per tile, built per frame, collided from their rows.
$island = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_island_loading.tscn') -TimeoutSeconds 180
if ((Get-GarageGameCountedStderr $island.Stderr) -or $island.Stdout -notmatch 'NATIVE_ISLAND_LOADING: \d+/\d+ checks passed') { throw "$($island.Stdout)`n$($island.Stderr)" }
Write-Output (($island.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(ISLAND_|NATIVE_ISLAND)' }) -join "`n")
# The look on the real garage too (its landscape and open-sea checks skip without --landscape).
$look = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_look_preset.tscn', '--', "--landscape=$landscapeRoom") -TimeoutSeconds 90
if ((Get-GarageGameCountedStderr $look.Stderr) -or $look.Stdout -notmatch 'checks passed' -or $look.Stdout -match 'checks were skipped') { throw "$($look.Stdout)`n$($look.Stderr)" }
Write-Output $look.Stdout.Trim()
$characters = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native_look_characters.tscn') -TimeoutSeconds 90
if ((Get-GarageGameCountedStderr $characters.Stderr) -or $characters.Stdout -notmatch 'NATIVE_LOOK_CHARACTERS: \d+/\d+ checks passed') { throw "$($characters.Stdout)`n$($characters.Stderr)" }
Write-Output (($characters.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^NATIVE_LOOK_CHARACTERS' }) -join "`n")
$sheetMotion = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://tests/native/Look/character_sheet_capture.tscn', '--', '--motion-only=true', "--assets=$((Join-Path $toolchain.RepositoryPath 'assets/characters') -replace '\\', '/')") -TimeoutSeconds 180
if ((Get-GarageGameCountedStderr $sheetMotion.Stderr) -or $sheetMotion.Stdout -notmatch 'CHARACTER_SHEET_MOTION: (\d+)/\1 tiles show their motion') { throw "$($sheetMotion.Stdout)`n$($sheetMotion.Stderr)" }
Write-Output (($sheetMotion.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^CHARACTER_SHEET_MOTION:' }) -join "`n")
# The boot carries a team, a mode and a difficulty as run-room.ps1 passes them (the mechanics round, honoured since M3: the
# team's folder in the runner's user folder, Challenge at Gentle), and they are never a reason not to boot.
$room = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60', 'res://scenes/room.tscn', '--', '--quit-after-ready=240', '--team=boot_probe', '--mode=challenge', '--difficulty=gentle') -TimeoutSeconds 60
if ((Get-GarageGameCountedStderr $room.Stderr) -or $room.Stdout -notmatch 'ROOM_WORLD_READY' -or $room.Stdout -notmatch 'ROOM_WORLD_QUIT') { throw "Placeholder room failed to load:`n$($room.Stdout)`n$($room.Stderr)" }
Write-Output $room.Stdout.Trim()
# The landscape as the player opens it: installed in the (runner's) user folder and booted by id through the main scene, which
# reads --room, quitting by --quit-after-ready (a smoke boot of 13 October never quit; tools/smoke-boot.ps1).
$installed = Join-Path $toolchain.RepositoryPath '.cache/native-test/appdata/Godot/app_userdata/Garage-Game/rooms/landscape_garage_nominal'
if (Test-Path -LiteralPath $installed) { Remove-Item -LiteralPath $installed -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path -Parent $installed) | Out-Null
Copy-Item -LiteralPath $landscapeRoom -Destination $installed -Recurse
$boot = Invoke-GarageGameRoomBoot -Toolchain $toolchain -Room 'landscape_garage_nominal' -Frames 240 -TimeoutSeconds 120 -BusyMachineWarningAllowed
Write-Output "$($boot.Ready)`n$($boot.Quit)"
Write-Output ("MAIN_SCENE_BOOT landscape_garage_nominal from the user folder: ready, quit, exit 0 in {0:0.0} s" -f $boot.Seconds)
# The playtest recorder (R1) is off in a boot check: no record may appear in the runner's own user folder.
$records = Join-Path $toolchain.RepositoryPath '.cache/native-test/appdata/Godot/app_userdata/Garage-Game/playtests'
if (Test-Path -LiteralPath $records) { throw "A boot check wrote a playtest record: $records" }
