#requires -Version 7.0
<#
.SYNOPSIS
Captures the five fixed look-review cameras at 1920x1080 on this machine's GPU and measures frame time.

.DESCRIPTION
Runs res://tests/native_look_capture.tscn in a real window (never --headless: headless renders nothing),
writes one PNG per camera plus timings.json into -OutDir, then quits. A Godot window appears briefly.
With -Baseline <commit>, the game folder of that commit is exported under .cache/look-baseline/ and the
current harness is copied into it, so "before" captures use exactly that commit's look code.

Before launching, the script refuses to start while a game window titled "Garage Game..." (or "EnFractal..." from an older build) is open (someone is
playing on this GPU; pass -AllowOpenGame to override), waits until nvidia-smi shows the GPU quiet for six
seconds, samples GPU memory during the capture, records both in timings.json, and retries (twice by default)
if another job (for example pose estimation) used the GPU meanwhile, so timings are not taken under contention.

The harness writes the look's self-check, a pixel check of the grain and vignette effect, and pixel checks of
light from real sources (the sun lands only through the window; the night with the lamps off and on, saved as
night_*.png) to timings.json (look_problems, post_effect_check, light_checks), and saves close crops of the
avatars' feet (contact_*.png) for the cameras the cameras file names. A problem, such as a renderer fallback,
fails the capture after the files are written; pass -AllowProblems to keep the exit code at zero.

-Style PATH renders with a preset variant instead of the room's style (for tuning; never a review of record), and a draft under review such as game/styles/storybook_painterly/v2.json; -Only ID,ID renders just those cameras and -NoLightChecks skips the light checks, which is all a probe or a tuning variant needs (Run 2's capture budget).

-Room DIR reviews a room other than the test room (a landscape export; -Room landscape takes the 1x garage and -Room landscape10 the 10x island from the fixture cache every worktree shares, tools/landscape-fixture.ps1, building it first if missing) with -Cameras tools/look/landscape_cameras.json; -Jpg QUALITY writes JPEGs of that quality instead of PNGs (the landscape pictures are committed and each stays under 1 MB).

-Probe NAME runs a GPU probe instead of the review captures (rooms, window, free-viewport, shimmer, island-memory with -Room: see
LookCaptureHarness.Probes.cs) and writes probe_NAME.json (and a few images) into -OutDir. With -Baseline the same probe code
runs against that commit, which is how before and after evidence is made.

.EXAMPLE
pwsh -NoProfile -File tools/look/capture-look.ps1 -Label after -OutDir docs/look/reviews/run1/after -Sweep
pwsh -NoProfile -File tools/look/capture-look.ps1 -Label before -OutDir docs/look/reviews/run1/before -Baseline 28fc364
#>
param(
    [string]$Label = 'after',
    [string]$OutDir = 'docs/look/reviews/run1/after',
    [string]$Baseline = '',
    [string]$Cameras = 'tools/look/review_cameras.json',
    [int]$WarmupFrames = 120,
    [int]$MeasureFrames = 300,
    [string]$Only = '',
    [switch]$Sweep,
    [switch]$RootViewport,
    [int]$MaxBusyVramMiB = 3072,
    [int]$MaxBusyUtilisation = 60,
    [int]$ContentionRetries = 2,
    [int]$TimeoutSeconds = 300,
    [switch]$AllowOpenGame,
    [switch]$AllowProblems,
    [switch]$NoLightChecks,
    [string]$Style = '',
    [string]$Probe = '',
    [string]$Room = '',
    [int]$Jpg = 0
)
$ErrorActionPreference = 'Stop'

# Someone playing the game on this GPU: never capture or time over them.
$openGame = Get-Process | Where-Object { $_.MainWindowTitle -like 'Garage Game*' -or $_.MainWindowTitle -like 'EnFractal*' }
if ($openGame -and -not $AllowOpenGame) {
    throw "A game window is open ($(($openGame | ForEach-Object { "$($_.ProcessName) $($_.Id): $($_.MainWindowTitle)" }) -join '; ')). Captures and timings wait for a quiet GPU; close the game or pass -AllowOpenGame."
}
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repository 'tools/native-toolchain.ps1')
$toolchain = Get-EnfractalNativeToolchain
$camerasPath = (Resolve-Path -LiteralPath (Join-Path $repository $Cameras)).Path
$outPath = [System.IO.Path]::GetFullPath($OutDir, $repository)
New-Item -ItemType Directory -Force $outPath | Out-Null

$env:DOTNET_ROOT = Split-Path -Parent $toolchain.DotnetPath
$env:DOTNET_CLI_HOME = Join-Path $repository '.cache/dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        $output = & $File @Arguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed ($LASTEXITCODE):`n$output" }
        return $output
    } finally { Pop-Location }
}

$projectPath = $toolchain.ProjectPath
$commit = (git -C $repository rev-parse --short HEAD).Trim()
$dirty = [bool](git -C $repository status --porcelain -- game tools/look)
if ($Baseline) {
    $commit = (git -C $repository rev-parse --short $Baseline).Trim()
    $dirty = $false
    $exportRoot = Join-Path $repository ".cache/look-baseline/$commit"
    if (Test-Path -LiteralPath $exportRoot) { Remove-Item -LiteralPath $exportRoot -Recurse -Force }
    New-Item -ItemType Directory -Force $exportRoot | Out-Null
    $archive = Join-Path $exportRoot 'game.tar'
    git -C $repository archive --format=tar -o $archive $commit game
    if ($LASTEXITCODE -ne 0) { throw "git archive $commit failed" }
    tar -xf $archive -C $exportRoot
    if ($LASTEXITCODE -ne 0) { throw "could not extract $archive" }
    Remove-Item -LiteralPath $archive
    $projectPath = Join-Path $exportRoot 'game'
    # The harness only uses RoomWorld's public surface, so the current file runs against older commits.
    New-Item -ItemType Directory -Force (Join-Path $projectPath 'tests/native/Look') | Out-Null
    foreach ($file in @('LookCaptureHarness.cs', 'LookCaptureHarness.Probes.cs', 'LookCaptureHarness.Island.cs', 'LookCaptureHarness.MemoryProbe.cs', 'IslandCameras.cs', 'LookFixtureRooms.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository "game/tests/native/Look/$file") -Destination (Join-Path $projectPath 'tests/native/Look/')
    }
    Copy-Item -LiteralPath (Join-Path $repository 'game/tests/native_look_capture.tscn') -Destination (Join-Path $projectPath 'tests/')
    Write-Output "Exported $commit to $projectPath"
}

Write-Output (Invoke-Checked $toolchain.DotnetPath @('build', 'GarageGame.csproj', '--configuration', 'Debug', '--nologo', '-v:q') $projectPath).Trim()
$import = Invoke-EnfractalNativeProcess -Toolchain $toolchain -FilePath $toolchain.EnginePath `
    -Arguments @('--headless', '--editor', '--path', $projectPath, '--import') -TimeoutSeconds 180
if ($import.Stderr -match 'SCRIPT ERROR:') { throw $import.Stderr }

$haveSmi = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)

function Get-GpuSample {
    $fields = (nvidia-smi --query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits | Select-Object -First 1).Split(',').ForEach({ $_.Trim() })
    [pscustomobject]@{ Name = $fields[0]; Used = [int]$fields[1]; Total = [int]$fields[2]; Utilisation = [int]$fields[3] }
}

# Another lane may run GPU jobs (pose estimation) in bursts. Wait until the card has been quiet for six
# seconds: no large allocation and no saturating job (ordinary desktop use stays below the thresholds).
function Wait-QuietGpu {
    if (-not $haveSmi) { return 'nvidia-smi unavailable' }
    $quiet = 0
    for ($sample = 1; $sample -le 300; $sample++) {
        $gpu = Get-GpuSample
        if ($gpu.Used -le $MaxBusyVramMiB -and $gpu.Utilisation -le $MaxBusyUtilisation) { $quiet++ } else { $quiet = 0 }
        if ($quiet -ge 3) {
            return "$($gpu.Name); $($gpu.Used) of $($gpu.Total) MiB in use and $($gpu.Utilisation)% utilisation before launch (quiet for 6 s)"
        }
        if ($quiet -eq 0 -and $sample % 15 -eq 1) { Write-Output "GPU busy ($($gpu.Used) MiB, $($gpu.Utilisation)%); waiting for it to go quiet" | Out-Host }
        Start-Sleep -Seconds 2
    }
    throw 'The GPU did not go quiet within 10 minutes; another job is using it. Retry later.'
}

$userArgs = @("--cameras=$camerasPath", "--out=$outPath", "--label=$Label", "--commit=$commit$(if ($dirty) { '+uncommitted' })",
    "--warmup=$WarmupFrames", "--frames=$MeasureFrames")
$userArgs += '--no-playtest-record'  # a capture is not a play: the playtest recorder (R1) stays off in this window
if ($Only) { $userArgs += "--only=$Only" }
if ($Sweep) { $userArgs += '--sweep' }
if ($Probe) {
    $userArgs += "--probe=$Probe"
    $userArgs += "--garage=$((Join-Path $repository 'contracts/examples/rooms/garage_example') -replace '\\', '/')"
}
if ($RootViewport) { $userArgs += '--root-viewport' }
if ($AllowProblems) { $userArgs += '--allow-problems' }
if ($NoLightChecks) { $userArgs += '--light-checks=false' }
if ($Style) { $userArgs += "--style=$(([System.IO.Path]::GetFullPath($Style, $repository)) -replace '\\', '/')" }
if ($Room) {
    if ($Room -in @('landscape', 'landscape10')) {
        . (Join-Path $repository 'tools/landscape-fixture.ps1')
        $Room = (Get-EnfractalLandscapeFixture -Toolchain $toolchain -Scale ($Room -eq 'landscape10' ? 10 : 1)).Room
    }
    $userArgs += "--room=$(([System.IO.Path]::GetFullPath($Room, $repository)) -replace '\\', '/')"
}
if ($Jpg -gt 0) { $userArgs += "--jpg=$Jpg" }
$engineArgs = @('--path', $projectPath, '--disable-vsync')
if ($RootViewport) { $engineArgs += @('--resolution', '1920x1080', '--position', '0,0') }
else { $engineArgs += @('--resolution', '960x540', '--position', '40,40') }
$engineArgs += @('res://tests/native_look_capture.tscn', '--') + $userArgs

for ($attempt = 1; ; $attempt++) {
    $gpuNote = Wait-QuietGpu
    Write-Output "GPU: $gpuNote"
    # A visible window is required, so this does not use the hidden-window helper the test runners use.
    $start = [System.Diagnostics.ProcessStartInfo]::new($toolchain.EnginePath)
    foreach ($argument in $engineArgs + @("--note=$gpuNote")) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $projectPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOTNET_ROOT'] = $env:DOTNET_ROOT
    # Keep the player's real user:// data (avatar preferences, rooms) out of review captures.
    $start.Environment['APPDATA'] = Join-Path $repository '.cache/look-capture/appdata'
    $start.Environment['LOCALAPPDATA'] = Join-Path $repository '.cache/look-capture/localappdata'
    New-Item -ItemType Directory -Force $start.Environment['APPDATA'], $start.Environment['LOCALAPPDATA'] | Out-Null
    $process = [System.Diagnostics.Process]::Start($start)
    $stdoutRead = $process.StandardOutput.ReadToEndAsync()
    $stderrRead = $process.StandardError.ReadToEndAsync()
    # Sample the GPU while the capture runs.
    $samples = [System.Collections.Generic.List[object]]::new()
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $finished = $false
    while (-not ($finished = $process.WaitForExit(500)) -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($haveSmi) { $samples.Add((Get-GpuSample)) }
    }
    if (-not $finished) {
        $process.Kill($true)
        throw "Look capture timed out after $TimeoutSeconds s`n$($stdoutRead.Result)`n$($stderrRead.Result)"
    }
    $stdout = $stdoutRead.Result
    $stderr = $stderrRead.Result
    Set-Content -LiteralPath (Join-Path $repository ".cache/look-capture-$Label.log") -Value "$stdout`n--- stderr`n$stderr"
    $problems = ($stdout.Split("`n") | Where-Object { $_ -match '^LOOK_CAPTURE_PROBLEM ' }) -join "`n"
    if ($Probe) {
        if ($stdout -notmatch 'LOOK_PROBE_DONE' -or $process.ExitCode -ne 0) { throw "Look probe $Probe failed (exit $($process.ExitCode)):`n$stdout`n$stderr" }
        $stdout.Split("`n") | Where-Object { $_ -match '^LOOK_PROBE' } | ForEach-Object { Write-Output $_.TrimEnd() }
        if ($stderr.Trim()) { Write-Output "--- engine stderr (warnings) ---"; Write-Output $stderr.Trim() }
        Write-Output "Probe written to $outPath"
        return
    }
    if ($stdout -notmatch 'LOOK_CAPTURE_DONE' -or ($process.ExitCode -ne 0 -and -not ($process.ExitCode -eq 3 -and $problems))) {
        throw "Look capture failed (exit $($process.ExitCode)):`n$stdout`n$stderr"
    }
    # The capture itself uses well under 1.5 GiB; more than that on top of the idle baseline means another job ran.
    $monitorNote = 'not sampled'
    $contended = $false
    if ($samples.Count -gt 0) {
        $baseline = [int]([regex]::Match($gpuNote, '(\d+) of').Groups[1].Value)
        $peak = ($samples | Measure-Object Used -Maximum).Maximum
        $contended = $peak - $baseline -gt 1536
        $monitorNote = "GPU memory during capture $(($samples | Measure-Object Used -Minimum).Minimum)-$peak MiB over $($samples.Count) samples$(if ($contended) { '; another job likely used the GPU during this capture' })"
    }
    $timingsPath = Join-Path $outPath 'timings.json'
    $report = Get-Content -LiteralPath $timingsPath -Raw | ConvertFrom-Json
    $report | Add-Member -NotePropertyName 'gpu_during_capture' -NotePropertyValue $monitorNote -Force
    [System.IO.File]::WriteAllText($timingsPath, (($report | ConvertTo-Json -Depth 8) -replace "`r`n", "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Output "GPU: $monitorNote"
    if (-not $contended -or $attempt -gt $ContentionRetries) { break }
    Write-Output "Retrying the capture (attempt $($attempt + 1)) because another job used the GPU."
}
$stdout.Split("`n") | Where-Object { $_ -match '^(LOOK|Godot Engine|Vulkan|D3D12|OpenGL)' } | ForEach-Object { Write-Output $_.TrimEnd() }
if ($stderr.Trim()) { Write-Output "--- engine stderr (warnings) ---"; Write-Output $stderr.Trim() }
Write-Output "Captures and timings.json written to $outPath"
if ($problems) { throw "The capture finished with look problems (see look_problems in timings.json):`n$problems" }
