$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $modernShell = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue
    if (-not $modernShell) {
        throw 'PowerShell 7 or newer is required for reliable Godot process exit-code reporting.'
    }
    & $modernShell.Source -NoProfile -File $PSCommandPath
    exit $LASTEXITCODE
}

# Native baseline: build C# before running the retained GDScript kernel regressions.
. (Join-Path $PSScriptRoot 'tools/native-toolchain.ps1')
$nativeToolchain = Get-GarageGameNativeToolchain
Build-GarageGameNativeProject -Toolchain $nativeToolchain
$projectPath = $nativeToolchain.ProjectPath
$enginePath = $nativeToolchain.EnginePath
# Keep regression saves independent from the player's worlds on either machine.
$env:DOTNET_ROOT = Split-Path -Parent $nativeToolchain.DotnetPath
$env:PATH = $env:DOTNET_ROOT + [System.IO.Path]::PathSeparator + $env:PATH
$env:APPDATA = Join-Path $PSScriptRoot '.cache/engine-tests/roaming'
$env:LOCALAPPDATA = Join-Path $PSScriptRoot '.cache/engine-tests/local'
New-Item -ItemType Directory -Force $env:APPDATA,$env:LOCALAPPDATA | Out-Null

& (Join-Path $PSScriptRoot 'tools\ensure-godot-art-imports.ps1') -EnginePath $enginePath -ProjectPath $projectPath

$tests = @(
    'native_interop_smoke.gd',
    'test_world_state.gd',
    'world_physics_smoke.gd',
    'creation_compiler_smoke.gd',
    'creation_authority_smoke.gd',
    'creation_visuals_smoke.gd',
    'invention_runtime_smoke.gd',
    'durable_creation_smoke.gd',
    'kernel_canonical_json_smoke.gd'
)
foreach ($test in $tests) {
    $result = Invoke-GarageGameNativeProcess -Toolchain $nativeToolchain -FilePath $enginePath `
        -Arguments @('--headless', '--path', $projectPath, '--script', "res://tests/$test") -TimeoutSeconds 30
    $exitCode = $result.ExitCode
    $stdout = $result.Stdout
    $stderr = $result.Stderr
    if ($null -eq $exitCode -or $exitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Output $stdout
        Write-Output $stderr
        throw "$test failed with exit code $exitCode."
    }
    Write-Output ($stdout.Trim().Split("`n") | Select-Object -Last 1)
}

$probe = Invoke-GarageGameNativeProcess -Toolchain $nativeToolchain -FilePath $enginePath `
    -Arguments @('--headless', '--path', $projectPath, 'res://scenes/native_contract_probe.tscn') -TimeoutSeconds 30
if ($probe.Stderr -or $probe.Stdout -notmatch 'Native release probe passed:') {
    throw "Native compiler probe failed:`n$($probe.Stdout)`n$($probe.Stderr)"
}
Write-Output ($probe.Stdout.Trim().Split("`n") | Select-Object -Last 1)

# C# kernel: canonical JSON golden fixture (C# and GDScript), the garage_game.command host, the play HUD and the sandbox verbs.
foreach ($scene in @('native_kernel_canonical_json.tscn', 'native_kernel_command_host.tscn', 'native_kernel_play_hud.tscn', 'native_kernel_sandbox.tscn', 'native_kernel_journal.tscn', 'native_kernel_rebuild.tscn', 'native_kernel_nature.tscn', 'native/Kernel/native_kernel_inventory.tscn', 'native_kernel_bubble.tscn', 'native_playtest.tscn')) {
    $kernel = Invoke-GarageGameNativeProcess -Toolchain $nativeToolchain -FilePath $enginePath `
        -Arguments @('--headless', '--path', $projectPath, '--fixed-fps', '60', "res://tests/$scene") -TimeoutSeconds 120
    if ($kernel.Stderr -or $kernel.Stdout -notmatch 'NATIVE_KERNEL_[A-Z_]+: \d+/\d+ checks passed') {
        throw "$scene failed:`n$($kernel.Stdout)`n$($kernel.Stderr)"
    }
    Write-Output ($kernel.Stdout.Trim().Split("`n") | Select-Object -Last 1)
}

# Companion (A1): MCP surface, mock host, link and boundary tests in the pinned companion environment.
# The first run needs network once to fill companion/.venv from companion/uv.lock.
$uv = Get-Command uv -CommandType Application -ErrorAction SilentlyContinue
if (-not $uv) { throw 'uv is required for the companion tests (companion/uv.lock); see docs/companion/README.md.' }
$companionOutput = & $uv.Source run --project (Join-Path $PSScriptRoot 'companion') --locked --quiet `
    python -m unittest discover -s (Join-Path $PSScriptRoot 'companion/tests') 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) {
    Write-Output $companionOutput
    throw "Companion tests failed with exit code $LASTEXITCODE."
}
Write-Output ("companion: " + (($companionOutput | Select-String -Pattern '^Ran \d+ tests' | Select-Object -Last 1).Line))

# The playtest reader (R2): report.py against its fixture and the contract's example play, in a throwaway environment pinned by
# tools/playtest/requirements.txt (Pillow for the contact sheet; the tests skip the sheet without it).
$playtestOutput = & $uv.Source run --no-project --quiet --with-requirements (Join-Path $PSScriptRoot 'tools/playtest/requirements.txt') `
    python -m unittest discover -s (Join-Path $PSScriptRoot 'tools/playtest/tests') 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) {
    Write-Output $playtestOutput
    throw "Playtest reader tests failed with exit code $LASTEXITCODE."
}
Write-Output ("playtest reader: " + (($playtestOutput | Select-String -Pattern '^Ran \d+ tests' | Select-Object -Last 1).Line))
