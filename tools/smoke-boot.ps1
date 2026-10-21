#requires -Version 7.0
<#
.SYNOPSIS
A safe smoke boot: build, then boot a room headlessly through the game's main scene and quit once it is ready, without
touching the player's own saves, profile or companion session.

.DESCRIPTION
Godot never compiles C# when it runs a game: it runs the assembly last built into game/.godot. Booting a checkout that was
not rebuilt since a merge or a pull runs old code (on 13 October an assembly older than -- --quit-after-ready ignored it and
never quit). So this builds first, every time (a second or two when nothing changed).

The game's user folder is a scratch profile, .cache/smoke-boot/<name> in this worktree, emptied each time. A room given by id
is copied into it from the player's own installed rooms (read, never written), so the boot opens it as the game does
(user://rooms/<id>). The boot goes through scenes/native_boot.tscn, which reads --room (passing res://scenes/room.tscn skips
it and silently loads test_room), with "-- --quit-after-ready=N". It passes on exit 0 with ROOM_WORLD_READY naming the room
and ROOM_WORLD_QUIT, and nothing on stderr; a wall-clock limit ends a boot that never quits.

.EXAMPLE
pwsh -NoProfile -File tools/smoke-boot.ps1 -Room landscape_garage_nominal
pwsh -NoProfile -File tools/smoke-boot.ps1 -Room landscape          # the 1x garage from the shared fixture cache
pwsh -NoProfile -File tools/smoke-boot.ps1 -Room landscape10        # the 10x island (built once, about 4 min, if missing)
pwsh -NoProfile -File tools/smoke-boot.ps1 -Room C:\path\to\an\exported\room
#>
param(
    [string]$Room = 'landscape',
    [int]$Frames = 300,
    [int]$TimeoutSeconds = 180,
    # The scratch profile's name under .cache/smoke-boot.
    [string]$Profile = 'default'
)
$ErrorActionPreference = 'Stop'
if ($Profile -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$') { throw '-Profile takes a short name of letters, digits, - and _.' }
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')
. (Join-Path $PSScriptRoot 'landscape-fixture.ps1')
$toolchain = Get-GarageGameNativeToolchain
Build-GarageGameNativeProject -Toolchain $toolchain | Select-Object -Last 1
# The import below launches the editor directly: without these a modal .NET dialog can block the screen.
$env:DOTNET_ROOT = Split-Path -Parent $toolchain.DotnetPath
$env:PATH = $env:DOTNET_ROOT + [System.IO.Path]::PathSeparator + $env:PATH
& (Join-Path $PSScriptRoot 'ensure-godot-art-imports.ps1') -EnginePath $toolchain.EnginePath -ProjectPath $toolchain.ProjectPath

$profileRoot = [System.IO.Path]::GetFullPath((Join-Path $toolchain.RepositoryPath ".cache/smoke-boot/$Profile"))
if (Test-Path -LiteralPath $profileRoot) { Remove-Item -LiteralPath $profileRoot -Recurse -Force }
$roaming = Join-Path $profileRoot 'roaming'
$local = Join-Path $profileRoot 'local'
New-Item -ItemType Directory -Force $roaming, $local | Out-Null
$environment = @{ APPDATA = $roaming; LOCALAPPDATA = $local; XDG_DATA_HOME = (Join-Path $profileRoot 'data') }

$roomId = $null
if ($Room -in @('landscape', 'landscape10')) {
    $Room = (Get-GarageGameLandscapeFixture -Toolchain $toolchain -Scale ($Room -eq 'landscape10' ? 10 : 1)).Room
} elseif ($Room -notmatch '[\\/]') {
    # An id: install a copy of the player's own room into the scratch profile, as the game finds it.
    $roomId = $Room
    $installed = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Godot/app_userdata/Garage-Game/rooms/$Room"
    if (Test-Path -LiteralPath (Join-Path $installed 'room.json')) {
        $copy = Join-Path $roaming "Godot/app_userdata/Garage-Game/rooms/$Room"
        New-Item -ItemType Directory -Force (Split-Path -Parent $copy) | Out-Null
        Copy-Item -LiteralPath $installed -Destination $copy -Recurse
        Get-ChildItem -LiteralPath $copy -Recurse -File | Where-Object IsReadOnly | ForEach-Object { $_.IsReadOnly = $false }
    }
} else {
    $Room = [System.IO.Path]::GetFullPath($Room)
}
if (-not $roomId) {
    $manifest = Join-Path $Room 'room.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "No room.json in $Room." }
    $roomId = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).room_id
}
$boot = Invoke-GarageGameRoomBoot -Toolchain $toolchain -Room $Room -RoomId $roomId -Frames $Frames -TimeoutSeconds $TimeoutSeconds -Environment $environment
Write-Output (($boot.Stdout -split "`r?`n" | Where-Object { $_ -match '^(ROOM_|POND_LIFE|COMMAND_HOST|LOOK:)' }) -join "`n")
Write-Output ("SMOKE_BOOT passed: {0} loaded and quit after {1} frames, exit 0, {2:0.0} s in all (profile {3})" -f $roomId, $Frames, $boot.Seconds, $profileRoot)
