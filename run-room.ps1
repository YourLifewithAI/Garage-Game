#requires -Version 7.0
# -Style v2 plays the default style's v2 (or -Style <preset_id>/v<N>) instead of the room's pinned look; -Room <id> picks the room,
# -Room landscape the garage island from the shared fixture cache, and -Room landscape10 the same island at the 10x world's scale. -Profile <name> keeps this play's saves, settings and
# companion session under .cache/profiles/<name> instead of your own user folders, so a playtest can be repeated or reset.
# -Team <team id>, -Mode creative|challenge and -Difficulty gentle|standard|brave (with -Mode challenge) pass the team, the mode and
# the difficulty to the game as --team=, --mode= and --difficulty= (the teams packet, M3; contracts/README.md "Teams and the save
# layout"): the team's folder under user://teams/<team id>/ (the default team, local, when none is named), the island save of that
# mode, and in Challenge the difficulty, which a save keeps once it is made. With no -Mode the team plays the mode it played last.
# Live is not available yet. The first launch moves the saves from before teams under the default team; a later launch removes the
# old files (Saves/SaveMigration.cs).
# The game opens its opening menu (game/scenes/menu.tscn, M7) when it starts without a -Room; -Menu opens it with a room named too, and the
# -Room, -Team, -Mode and -Difficulty named beside it are the menu's first choices. With a -Room and no -Menu the game goes straight into the room, as before.
# -Knack quick_feet:3 (or -Knack quick_feet:3,cat_feet:2, up to three; the rank 1 to 3, 1 when left off) overrides the team file's
# knacks for this play only, as --knack= (a test's override, M5); the team file keeps the knacks knack.set chose.
param([string]$Style, [string]$Room, [string]$Profile, [string]$Team, [string]$Mode, [string]$Difficulty, [string[]]$Knack, [switch]$NoRecord, [switch]$Fullscreen, [switch]$Menu)
$ErrorActionPreference = 'Stop'
if ($Profile -and $Profile -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$') { throw '-Profile takes a short name of letters, digits, - and _.' }
if ($Team -and $Team -cnotmatch '^[a-z][a-z0-9_-]{0,63}$') { throw '-Team takes a team id: lowercase letters, digits, - and _, starting with a letter.' }
if ($Mode -ceq 'live') { throw '-Mode live: Live is not available yet. Choose creative or challenge.' }
if ($Mode -and $Mode -cnotin @('creative', 'challenge')) { throw '-Mode is creative or challenge.' }
if ($Difficulty -and ($Difficulty -cnotin @('gentle', 'standard', 'brave') -or $Mode -cne 'challenge')) { throw '-Difficulty is gentle, standard or brave, with -Mode challenge.' }
$knackArgs = @($Knack | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($knackArgs.Count -gt 3) { throw '-Knack takes up to three knacks, one for each slot.' }
foreach ($knackArg in $knackArgs) { if ($knackArg -cnotmatch '^[a-z][a-z0-9_-]{0,63}(:[1-3])?$') { throw "-Knack takes a knack id and a rank, like quick_feet:3 (rank 1 to 3, 1 when left off): '$knackArg'." } }
. (Join-Path $PSScriptRoot 'tools/native-toolchain.ps1')
$nativeToolchain = Get-GarageGameNativeToolchain
if ($Room -eq 'landscape' -or $Room -eq 'landscape10') {
    # The garage island from the shared fixture cache (tools/landscape-fixture.ps1), built there first if it is missing.
    . (Join-Path $PSScriptRoot 'tools/landscape-fixture.ps1')
    $scale = if ($Room -eq 'landscape10') { 10 } else { 1 }
    $Room = (Get-GarageGameLandscapeFixture -Toolchain $nativeToolchain -Scale $scale).Room.Replace('\', '/')
}
Build-GarageGameNativeProject -Toolchain $nativeToolchain
$env:DOTNET_ROOT = Split-Path -Parent $nativeToolchain.DotnetPath
$env:PATH = $env:DOTNET_ROOT + [System.IO.Path]::PathSeparator + $env:PATH
$userArgs = @()
# The playtest record (R1; docs/runs/RUN-2-PLAYTEST-LOOP.md): every windowed launch records itself under user://playtests/ unless
# -NoRecord; its manifest names the build as the short commit, +dirty when tracked files have changed.
$commit = & git -C $PSScriptRoot rev-parse --short HEAD 2>$null
if ($LASTEXITCODE -eq 0 -and $commit) { $userArgs += "--build=$commit$(if (& git -C $PSScriptRoot status --porcelain --untracked-files=no) { '+dirty' })" }
if ($NoRecord) { $userArgs += '--no-playtest-record' }
# -Fullscreen plays this launch in full screen (the game reads --fullscreen); the full-screen key, F11, remembers the choice for every launch.
if ($Fullscreen) { $userArgs += '--fullscreen' }
if ($Menu) { $userArgs += '--menu' }
if ($Style) { $userArgs += "--style=$Style" }
if ($Room) { $userArgs += "--room=$Room" }
if ($Team) { $userArgs += "--team=$Team" }
if ($Mode) { $userArgs += "--mode=$Mode" }
if ($Difficulty) { $userArgs += "--difficulty=$Difficulty" }
foreach ($knackArg in $knackArgs) { $userArgs += "--knack=$knackArg" }
if ($userArgs) { $userArgs = @('--') + $userArgs }
$priorRoaming = $env:APPDATA
$priorLocal = $env:LOCALAPPDATA
$exitCode = 1
try {
    if ($Profile) {
        # Godot's user:// (saves, wishes, the companion's session file) follows APPDATA on Windows.
        $profileRoot = Join-Path $PSScriptRoot ".cache/profiles/$Profile"
        $env:APPDATA = Join-Path $profileRoot 'roaming'
        $env:LOCALAPPDATA = Join-Path $profileRoot 'local'
        New-Item -ItemType Directory -Force $env:APPDATA, $env:LOCALAPPDATA | Out-Null
    }
    & (Join-Path $PSScriptRoot 'tools/ensure-godot-art-imports.ps1') -EnginePath $nativeToolchain.EnginePath -ProjectPath $nativeToolchain.ProjectPath
    & $nativeToolchain.EnginePath --path $nativeToolchain.ProjectPath @userArgs
    $exitCode = $LASTEXITCODE
} finally {
    $env:APPDATA = $priorRoaming
    $env:LOCALAPPDATA = $priorLocal
}
exit $exitCode
