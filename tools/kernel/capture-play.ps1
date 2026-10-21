#requires -Version 7.0
<#
.SYNOPSIS
Captures what the founder sees while playing (the companion's name tag, the running player) in a real window and measures edge sharpness.

.DESCRIPTION
Runs res://tests/native_kernel_play_capture.tscn windowed (never --headless: headless renders nothing), writes
PNG crops and a JSON report into -OutDir, then quits. A Godot window appears briefly. See
game/tests/native/Kernel/PlayCaptureHarness.cs for the scenarios and arguments.

It refuses to start while a window titled "Garage Game..." (or "EnFractal..." from an older build) is open (someone is playing on this GPU; -AllowOpenGame
overrides), and records any such window in the report's note, because timings taken beside another game
window are noisy.

.EXAMPLE
pwsh -NoProfile -File tools/kernel/capture-play.ps1 -Tag before -OutDir C:\scratch\before
pwsh -NoProfile -File tools/kernel/capture-play.ps1 -Tag notaa -OutDir C:\scratch\matrix -HarnessArgs '--taa=false','--scenarios=run_f3'
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Tag = 'capture',
    [string[]]$HarnessArgs = @(),
    [string]$Resolution = '1280x720',
    [int]$TimeoutSeconds = 240,
    [switch]$NoBuild,
    [switch]$AllowOpenGame
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repository 'tools/native-toolchain.ps1')
$toolchain = Get-EnfractalNativeToolchain
$outPath = [System.IO.Path]::GetFullPath($OutDir, $repository)
New-Item -ItemType Directory -Force $outPath | Out-Null

$openGame = @(Get-Process | Where-Object { $_.MainWindowTitle -like 'Garage Game*' -or $_.MainWindowTitle -like 'EnFractal*' })
if ($openGame.Count -gt 0 -and -not $AllowOpenGame) {
    throw "A game window is open ($(($openGame | ForEach-Object { "$($_.ProcessName) $($_.Id): $($_.MainWindowTitle)" }) -join '; ')). Close it or pass -AllowOpenGame."
}

$env:DOTNET_ROOT = Split-Path -Parent $toolchain.DotnetPath
$env:PATH = $env:DOTNET_ROOT + [System.IO.Path]::PathSeparator + $env:PATH
if (-not $NoBuild) {
    Write-Output (Build-EnfractalNativeProject -Toolchain $toolchain)
    $import = Invoke-EnfractalNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
        -Arguments @('--headless', '--editor', '--path', $toolchain.ProjectPath, '--import') -TimeoutSeconds 180
    if ($import.Stderr -match 'SCRIPT ERROR:') { throw $import.Stderr }
}

$start = [System.Diagnostics.ProcessStartInfo]::new($toolchain.EnginePath)
foreach ($argument in @('--path', $toolchain.ProjectPath, '--resolution', $Resolution, '--position', '40,40', 'res://tests/native_kernel_play_capture.tscn', '--', "--out=$outPath", "--tag=$Tag") + $HarnessArgs) {
    $start.ArgumentList.Add($argument)
}
$start.WorkingDirectory = $toolchain.ProjectPath
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['DOTNET_ROOT'] = $env:DOTNET_ROOT
# Keep the player's real user:// data (avatar preferences, rooms, saves) out of captures.
$start.Environment['APPDATA'] = Join-Path $repository '.cache/play-capture/appdata'
$start.Environment['LOCALAPPDATA'] = Join-Path $repository '.cache/play-capture/localappdata'
New-Item -ItemType Directory -Force $start.Environment['APPDATA'], $start.Environment['LOCALAPPDATA'] | Out-Null
$process = [System.Diagnostics.Process]::Start($start)
$stdoutRead = $process.StandardOutput.ReadToEndAsync()
$stderrRead = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill($true)
    throw "Play capture timed out after $TimeoutSeconds s`n$($stdoutRead.Result)`n$($stderrRead.Result)"
}
$stdout = $stdoutRead.Result
$stderr = $stderrRead.Result
Set-Content -LiteralPath (Join-Path $outPath "$Tag.log") -Value "$stdout`n--- stderr`n$stderr"
# Godot .NET sometimes crashes while freeing objects at exit, after the report is written; that is not a failed capture.
if ($stdout -notmatch 'PLAY_CAPTURE_DONE') {
    throw "Play capture failed (exit $($process.ExitCode)):`n$stdout`n$stderr"
}
if ($process.ExitCode -ne 0) { Write-Output "NOTE: the engine exited with code $($process.ExitCode) after the report was written." }
$stdout.Split("`n") | Where-Object { $_ -match '^PLAY_CAPTURE' } | ForEach-Object { Write-Output $_.TrimEnd() }
if ($openGame.Count -gt 0) { Write-Output "NOTE: a game window was open during this capture; timings are noisy." }
if ($stderr.Trim()) { Write-Output '--- engine stderr (first lines) ---'; Write-Output (($stderr.Trim() -split "`n" | Select-Object -First 8) -join "`n") }
