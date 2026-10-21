#requires -Version 7.0
param([string]$EnginePath, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')
. (Join-Path $PSScriptRoot 'landscape-fixture.ps1')
$nativeToolchain = Get-EnfractalNativeToolchain -EnginePath $EnginePath -DotnetPath $DotnetPath
$template = Join-Path $nativeToolchain.RepositoryPath '.cache/godot-export/templates/windows_release_x86_64.exe'
if (-not (Test-Path -LiteralPath $template -PathType Leaf)) {
    throw 'Missing .NET export templates. Run tools/bootstrap-native.ps1 -IncludeExportTemplates.'
}
Build-EnfractalNativeProject -Toolchain $nativeToolchain
$outputPath = Join-Path $nativeToolchain.RepositoryPath '.cache/releases/windows/GarageGame.exe'
New-Item -ItemType Directory -Force (Split-Path -Parent $outputPath) | Out-Null
$export = Invoke-EnfractalNativeProcess -Toolchain $nativeToolchain -FilePath $nativeToolchain.EnginePath `
    -Arguments @('--headless', '--path', $nativeToolchain.ProjectPath, '--export-release', 'Windows Native', $outputPath) -TimeoutSeconds 240
if ($export.Stderr -match 'ERROR:|SCRIPT ERROR:') { throw "Export reported an error:`n$($export.Stderr)" }
$probeWorkingPath = Join-Path $nativeToolchain.RepositoryPath '.cache/export-probe-empty'
New-Item -ItemType Directory -Force $probeWorkingPath | Out-Null
$probe = Invoke-EnfractalNativeProcess -Toolchain $nativeToolchain -FilePath $outputPath `
    -Arguments @('--headless', '--', '--native-contract-probe') -TimeoutSeconds 30 `
    -WorkingDirectory $probeWorkingPath -WithoutDotnetRuntime
if ($probe.Stderr -or $probe.Stdout -notmatch 'Native release probe passed:') {
    throw "Exported native runtime failed its contract probe:`n$($probe.Stdout)`n$($probe.Stderr)"
}
Write-Output $probe.Stdout.Trim()
$startup = Invoke-EnfractalNativeProcess -Toolchain $nativeToolchain -FilePath $outputPath `
    -Arguments @('--headless', '--fixed-fps', '60', '--', '--quit-after-ready=240') -TimeoutSeconds 60 `
    -WorkingDirectory $probeWorkingPath -WithoutDotnetRuntime
if ($startup.Stderr -or $startup.Stdout -notmatch 'ROOM_WORLD_READY' -or $startup.Stdout -notmatch 'ROOM_WORLD_QUIT') {
    throw "Exported game failed to load its default room scene:`n$($startup.Stdout)`n$($startup.Stderr)"
}
Write-Output $startup.Stdout.Trim()
# The 1x garage landscape through the exported game's main scene (--room), as a player's installed room opens: it loads and
# quits by --quit-after-ready (tools/smoke-boot.ps1 is the same boot on the editor build).
$landscapeRoom = (Get-EnfractalLandscapeFixture -Toolchain $nativeToolchain -Scale 1).Room
$landscapeBoot = Invoke-EnfractalRoomBoot -Toolchain $nativeToolchain -FilePath $outputPath -Room $landscapeRoom -Frames 240 -TimeoutSeconds 120 `
    -WorkingDirectory $probeWorkingPath -WithoutDotnetRuntime
Write-Output ("{0}`n{1}`nExported game booted the landscape through its main scene in {2:0.0} s" -f $landscapeBoot.Ready, $landscapeBoot.Quit, $landscapeBoot.Seconds)
Copy-Item -LiteralPath (Join-Path $nativeToolchain.ProjectPath 'RELEASE-CREDITS.md') -Destination (Join-Path (Split-Path -Parent $outputPath) 'CREDITS.md')
Write-Output "Native Windows build: $outputPath"
