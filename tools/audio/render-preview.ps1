# Renders every sound of the game to a WAV, for listening. Headless: nothing plays through the speakers.
# Default output: .cache\scratch\preview in this worktree (never committed). Loops are written three times over so the seam can be heard.
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'tools/native-toolchain.ps1')
$toolchain = Get-GarageGameNativeToolchain
Build-GarageGameNativeProject -Toolchain $toolchain
if (-not $Out) { $Out = Join-Path $root '.cache\scratch\preview' }
$Out = [System.IO.Path]::GetFullPath($Out)
$env:DOTNET_ROOT = Split-Path -Parent $toolchain.DotnetPath
$env:PATH = $env:DOTNET_ROOT + [System.IO.Path]::PathSeparator + $env:PATH
$env:APPDATA = Join-Path $root '.cache/engine-tests/roaming'
$env:LOCALAPPDATA = Join-Path $root '.cache/engine-tests/local'
New-Item -ItemType Directory -Force $env:APPDATA, $env:LOCALAPPDATA | Out-Null
$result = Invoke-GarageGameNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--path', $toolchain.ProjectPath, 'res://tests/native_audio.tscn', '--', "--preview=$Out") -TimeoutSeconds 120
Write-Output ($result.Stdout.Trim().Split("`n") | Select-Object -Last 1)
