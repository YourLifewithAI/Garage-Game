#requires -Version 7.0
param([string]$EnginePath, [string]$DotnetPath, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')
$nativeToolchain = Get-GarageGameNativeToolchain -EnginePath $EnginePath -DotnetPath $DotnetPath
if (-not $SkipBuild) { Build-GarageGameNativeProject -Toolchain $nativeToolchain }
$result = Invoke-GarageGameNativeProcess -Toolchain $nativeToolchain -FilePath $nativeToolchain.EnginePath `
    -Arguments @('--headless', '--path', $nativeToolchain.ProjectPath, '--script', 'res://tests/native_interop_smoke.gd') -TimeoutSeconds 30
if (-not [string]::IsNullOrWhiteSpace($result.Stderr) -or $result.Stdout -notmatch 'Native interop smoke passed:') {
    throw "Native interop did not pass cleanly.`n$($result.Stdout)`n$($result.Stderr)"
}
Write-Output $result.Stdout.Trim()
