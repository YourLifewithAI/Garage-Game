#requires -Version 7.0
<#
.SYNOPSIS
Takes the title page's pictures (Run 4, MENU-4) on this machine's GPU: one PNG per state of the page, at 1280x720.

.DESCRIPTION
Runs res://tests/native/Menu/native_menu_title_capture.tscn (MenuTitleCapture.cs) in a real window, which writes one PNG per
state of the title page into -OutDir and quits. A Godot window appears for a few seconds.

The before picture is 00-before-todays-page.png in the same folder: the Choose page, which is the page the game opened on
before the title page, with the same teams on the same land. (Do not take it with native_menu_capture.tscn or by launching the
game with --choose: both read the player's real user folder.)

Nothing of the player's is read or written. The run has its own APPDATA and LOCALAPPDATA under .cache/title-capture, the teams
and saves are made up by the scene, and the land is a stand-in:
  -Room landscape   the garage stand-in from the fixture cache every worktree shares (tools/landscape-fixture.ps1; built first
                    if it is missing). The default.
  -Room sample      the scene's own made-up land (a room six metres by seven with a dozen things). Needs no fixture.
  -Room DIR         a fixture room's folder. A folder under the real user folder or under captures/ is refused.

The script refuses to start while a game window is open (someone is playing on this GPU; -AllowOpenGame overrides), builds the
C# project first (-NoBuild skips it) and sets DOTNET_ROOT for the engine. -Headless is a dry run: the same states are walked
and their words printed, no window opens and no picture is taken (headless renders nothing).

.EXAMPLE
pwsh -NoProfile -File game/tests/native/Menu/capture-title.ps1 -OutDir .cache/title-capture/out
pwsh -NoProfile -File game/tests/native/Menu/capture-title.ps1 -Room sample -Headless
#>
param(
    [string]$OutDir = '.cache/title-capture/out',
    [string]$Room = 'landscape',
    [string]$Resolution = '1280x720',
    [switch]$Headless,
    [switch]$NoBuild,
    [switch]$AllowOpenGame,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
. (Join-Path $repository 'tools/native-toolchain.ps1')
$toolchain = Get-GarageGameNativeToolchain
if ($toolchain.RepositoryPath -ne $repository) { throw "The toolchain is another checkout's ($($toolchain.RepositoryPath)); run this from its own worktree." }

# Someone playing the game on this GPU: never open a window over them.
if (-not $Headless) {
    $openGame = Get-Process | Where-Object { $_.MainWindowTitle -like 'Garage-Game*' -or $_.MainWindowTitle -like 'Garage Game*' -or $_.MainWindowTitle -like 'EnFractal*' }
    if ($openGame -and -not $AllowOpenGame) {
        throw "A game window is open ($(($openGame | ForEach-Object { "$($_.ProcessName) $($_.Id): $($_.MainWindowTitle)" }) -join '; ')). Pictures wait for a quiet GPU; close the game or pass -AllowOpenGame."
    }
}

$outPath = [System.IO.Path]::GetFullPath($OutDir, $repository)
$scratch = Join-Path $repository '.cache/title-capture'
New-Item -ItemType Directory -Force $outPath, (Join-Path $scratch 'appdata'), (Join-Path $scratch 'localappdata') | Out-Null

function Test-Under([string]$Path, [string]$Folder) {
    if (-not $Folder) { return $false }
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $base = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return $full.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase)
}

$userArgs = @("--out=$($outPath -replace '\\', '/')", '--no-playtest-record')
$land = 'the scene''s own made-up land'
if ($Room -and $Room -ne 'sample') {
    if ($Room -eq 'landscape') {
        . (Join-Path $repository 'tools/landscape-fixture.ps1')
        $roomPath = (Get-GarageGameLandscapeFixture -Toolchain $toolchain -Scale 1).Room
    } else {
        $roomPath = [System.IO.Path]::GetFullPath($Room, $repository)
        # A real room is never pictured here: the player's own rooms live in the user folder, and capture data in captures/.
        foreach ($private in @($env:APPDATA, $env:LOCALAPPDATA, (Join-Path $repository 'captures'))) {
            if (Test-Under $roomPath $private) { throw "-Room $roomPath is under $private, where real rooms are kept. Name a fixture room (or -Room landscape, -Room sample)." }
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $roomPath 'room.json') -PathType Leaf)) { throw "No room.json in $roomPath" }
    $userArgs += "--room=$($roomPath -replace '\\', '/')"
    $land = $roomPath
}

if (-not $NoBuild) {
    Build-GarageGameNativeProject -Toolchain $toolchain | Select-String -Pattern 'Build succeeded|error|Elapsed' | ForEach-Object { $_.Line.Trim() }
    $import = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
        -Arguments @('--headless', '--editor', '--path', $toolchain.ProjectPath, '--import') -TimeoutSeconds 180
    if ($import.Stderr -match 'SCRIPT ERROR:') { throw $import.Stderr }
}

$scene = 'res://tests/native/Menu/native_menu_title_capture.tscn'
$dotnetRoot = Split-Path -Parent $toolchain.DotnetPath
# The page is silent, but the Choose page behind it may not always be: a capture has no one listening.
$engineArgs = if ($Headless) { @('--headless', '--path', $toolchain.ProjectPath, '--fixed-fps', '60') }
              else { @('--path', $toolchain.ProjectPath, '--audio-driver', 'Dummy', '--resolution', $Resolution, '--position', '40,40') }
$engineArgs += @($scene, '--') + $userArgs

# A visible window is needed for a picture, so this does not use the hidden-window helper the test runners use.
$start = [System.Diagnostics.ProcessStartInfo]::new($toolchain.EnginePath)
foreach ($argument in $engineArgs) { $start.ArgumentList.Add($argument) }
$start.WorkingDirectory = $toolchain.ProjectPath
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['DOTNET_ROOT'] = $dotnetRoot
$start.Environment['PATH'] = $dotnetRoot + [System.IO.Path]::PathSeparator + $start.Environment['PATH']
# The player's real user:// data (teams, saves, rooms, the avatar profile) stays out of the pictures.
$start.Environment['APPDATA'] = Join-Path $scratch 'appdata'
$start.Environment['LOCALAPPDATA'] = Join-Path $scratch 'localappdata'
# The page's hour is the one each picture names: a pin left in this shell would not change it, and is not passed on.
[void]$start.Environment.Remove('GARAGE_GAME_LOOK_CLOCK')
[void]$start.Environment.Remove('GARAGE_GAME_LOOK_DATE')
$process = [System.Diagnostics.Process]::Start($start)
try {
    $stdoutRead = $process.StandardOutput.ReadToEndAsync()
    $stderrRead = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "The title capture timed out after $TimeoutSeconds s`n$($stdoutRead.GetAwaiter().GetResult())`n$($stderrRead.GetAwaiter().GetResult())"
    }
    $stdout = $stdoutRead.GetAwaiter().GetResult()
    $stderr = $stderrRead.GetAwaiter().GetResult()
    $exit = $process.ExitCode
} finally { $process.Dispose() }

[System.IO.File]::WriteAllText((Join-Path $scratch 'last.log'), "$stdout`n--- stderr`n$stderr", [System.Text.UTF8Encoding]::new($false))
$stdout.Split("`n") | Where-Object { $_ -match '^TITLE_CAPTURE' } | ForEach-Object { Write-Output $_.TrimEnd() }
if ($stderr.Trim()) { Write-Output '--- engine stderr ---'; Write-Output $stderr.Trim() }
if ($exit -ne 0 -or $stdout -notmatch 'TITLE_CAPTURE done') { throw "The title capture failed (exit $exit); see $(Join-Path $scratch 'last.log')" }
if ($Headless) { Write-Output "Dry run (headless): every state walked on $land, no picture taken." }
else {
    $taken = [int]([regex]::Match($stdout, 'TITLE_CAPTURE done: (\d+) pictures').Groups[1].Value)
    if ($taken -eq 0) { throw "The window gave no picture; see $(Join-Path $scratch 'last.log')" }
    Write-Output "$taken pictures of the title page on $land in $outPath (the before picture is 00-before-todays-page.png)."
}
