# Shared discovery/process helpers. Dot-source this file; it does not install tools.
# Native tools and their caches stay inside the checkout unless explicitly supplied.

function Get-GarageGameNativeToolchain {
    [CmdletBinding()]
    param([string]$EnginePath, [string]$DotnetPath)

    $repositoryPath = Split-Path -Parent $PSScriptRoot
    $projectPath = Join-Path $repositoryPath 'game'
    $requiredSdk = (Get-Content -LiteralPath (Join-Path $projectPath 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $requiredEngine = ([xml](Get-Content -LiteralPath (Join-Path $projectPath 'GarageGame.csproj') -Raw)).Project.Sdk.Split('/')[1]

    if (-not $EnginePath) { $EnginePath = $env:GARAGE_GAME_GODOT_DOTNET }
    if (-not $EnginePath) { $EnginePath = $env:GARAGE_GAME_GODOT }
    if (-not $EnginePath) {
        foreach ($cacheName in @('godot', 'godot-dotnet')) {
            $cachePath = Join-Path $repositoryPath ".cache/$cacheName"
            if (Test-Path -LiteralPath $cachePath -PathType Container) {
                $candidate = Get-ChildItem -LiteralPath $cachePath -Recurse -File |
                    Where-Object { $_.Name -eq "Godot_v$requiredEngine-stable_mono_win64_console.exe" } |
                    Sort-Object FullName | Select-Object -First 1
                if ($candidate) { $EnginePath = $candidate.FullName; break }
            }
        }
    }
    if (-not $EnginePath) {
        $candidate = Get-Command godot-mono,godot,godot4 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) { $EnginePath = $candidate.Source }
    }
    if (-not $EnginePath -or -not (Test-Path -LiteralPath $EnginePath -PathType Leaf)) {
        throw "Godot .NET $requiredEngine is missing. Extract its full mono archive under .cache/godot or set GARAGE_GAME_GODOT_DOTNET. See docs/NATIVE-BUILD.md."
    }
    $EnginePath = (Resolve-Path -LiteralPath $EnginePath).Path
    $engineVersion = (& $EnginePath --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $engineVersion -notmatch ('^' + [regex]::Escape($requiredEngine) + '\.stable\.mono(?:\.|$)')) {
        throw "Expected Godot .NET $requiredEngine; '$EnginePath' reported '$engineVersion'. A standard Godot build cannot run the C# project."
    }
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $EnginePath) 'GodotSharp') -PathType Container)) {
        throw 'GodotSharp is missing beside the .NET executable. Extract the complete archive, not only the executable.'
    }

    if (-not $DotnetPath) { $DotnetPath = $env:GARAGE_GAME_DOTNET }
    if (-not $DotnetPath) {
        $portableDotnet = Join-Path $repositoryPath '.cache/dotnet/dotnet.exe'
        if (Test-Path -LiteralPath $portableDotnet -PathType Leaf) { $DotnetPath = $portableDotnet }
    }
    if (-not $DotnetPath) {
        $candidate = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
        if ($candidate) { $DotnetPath = $candidate.Source }
    }
    if (-not $DotnetPath -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        throw ".NET SDK $requiredSdk is missing. Extract it under .cache/dotnet or set GARAGE_GAME_DOTNET. A runtime-only installation is insufficient."
    }
    $DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path
    $installedSdks = (& $DotnetPath --list-sdks | Out-String)
    if ($LASTEXITCODE -ne 0 -or $installedSdks -notmatch ('(?m)^' + [regex]::Escape($requiredSdk) + '\s')) {
        throw "The selected dotnet host does not contain pinned SDK $requiredSdk. See game/global.json and docs/NATIVE-BUILD.md."
    }
    return [pscustomobject]@{
        RepositoryPath = $repositoryPath
        ProjectPath = $projectPath
        EnginePath = $EnginePath
        EngineVersion = $engineVersion
        DotnetPath = $DotnetPath
        SdkVersion = $requiredSdk
    }
}

function Invoke-GarageGameNativeProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Toolchain,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [int]$TimeoutSeconds = 120,
        [string]$WorkingDirectory,
        [switch]$WithoutDotnetRuntime,
        # Set last, over the defaults below (a smoke boot's own APPDATA and LOCALAPPDATA).
        [hashtable]$Environment = @{}
    )
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Native helpers require PowerShell 7 or newer.' }
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    if (-not $WorkingDirectory) { $WorkingDirectory = $Toolchain.ProjectPath }
    if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
        throw "Native working directory does not exist: $WorkingDirectory"
    }
    $start.WorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($WithoutDotnetRuntime) {
        # Release probes must find their own bundled runtime, not developer tools.
        # This removes inherited host configuration as well as our normal SDK setup.
        foreach ($key in @($start.Environment.Keys)) {
            if ($key -match '^(DOTNET_|COREHOST_|COMPlus_|MSBUILD)' -or $key -in @('NUGET_PACKAGES', 'GARAGE_GAME_DOTNET')) {
                [void]$start.Environment.Remove($key)
            }
        }
        $start.Environment['PATH'] = (Join-Path $env:SystemRoot 'System32') + [System.IO.Path]::PathSeparator + $env:SystemRoot
    } else {
        $dotnetRoot = Split-Path -Parent $Toolchain.DotnetPath
        $start.Environment['DOTNET_ROOT'] = $dotnetRoot
        $start.Environment['PATH'] = $dotnetRoot + [System.IO.Path]::PathSeparator + $start.Environment['PATH']
        $start.Environment['DOTNET_CLI_HOME'] = Join-Path $Toolchain.RepositoryPath '.cache/dotnet-home'
        $start.Environment['NUGET_PACKAGES'] = Join-Path $Toolchain.RepositoryPath '.cache/nuget/packages'
        $start.Environment['DOTNET_NOLOGO'] = '1'
        $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        $start.Environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'
        $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    }
    $start.Environment['APPDATA'] = Join-Path $Toolchain.RepositoryPath '.cache/native-test/appdata'
    $start.Environment['LOCALAPPDATA'] = Join-Path $Toolchain.RepositoryPath '.cache/native-test/localappdata'
    $start.Environment['XDG_DATA_HOME'] = Join-Path $Toolchain.RepositoryPath '.cache/native-test/data'
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = [string]$Environment[$key] }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdoutRead = $process.StandardOutput.ReadToEndAsync()
        $stderrRead = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $timedOutStdout = $stdoutRead.GetAwaiter().GetResult()
            $timedOutStderr = $stderrRead.GetAwaiter().GetResult()
            throw "Native command timed out after $TimeoutSeconds seconds: $FilePath`n$timedOutStdout`n$timedOutStderr"
        }
        $stdout = $stdoutRead.GetAwaiter().GetResult()
        $stderr = $stderrRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Native command failed ($($process.ExitCode)): $FilePath`n$stdout`n$stderr"
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
    } finally {
        $process.Dispose()
    }
}

function Get-GarageGameCountedStderr {
    <#
    .SYNOPSIS
    A headless step's stderr as a runner counts it: the text as it came, less the two lines of one engine warning that a busy
    machine prints. An empty answer is a clean stderr; anything else fails the step.
    .DESCRIPTION
    When other suites load the machine, Godot's Jolt job system prints
      WARNING: Jolt Physics job system exceeded the maximum number of jobs. ... Waiting for jobs to become available...
         at: CreateJob (modules/jolt_physics/spaces/jolt_job_system.cpp:<line>)
    and waits. The step's results are the same; the warning says only that the machine was busy, and a runner that fails on it
    fails for the other work on the machine. tools/linux/test-all.sh has ignored these two lines since its stderr_counts;
    tools/test-room.ps1 and run-engine-tests.ps1 follow the same rule through this one function, with its pattern character
    for character. What the pattern drops is any line that holds, anywhere in it and in exactly these capitals, either
      Jolt Physics job system exceeded the maximum number of jobs
    or
      at: CreateJob (modules/jolt_physics/spaces/jolt_job_system.cpp
    whatever the line begins with: a line of another kind that quotes the first phrase goes with it, on Linux too. Nothing else
    is dropped: a stderr with no such line comes back unchanged (one of blanks only as well), and every other line (an ERROR, a
    SCRIPT ERROR, another warning, the "at:" line of a warning raised anywhere else) is kept and fails the step that printed it.
    tools/smoke-boot.ps1 and tools/export-native.ps1 do not use it. Assert-GarageGameCountedStderr, below, holds the rule.
    #>
    [CmdletBinding()]
    param([Parameter(Position = 0)][AllowNull()][AllowEmptyString()][string]$Stderr)
    if (-not $Stderr) { return '' }
    $lines = $Stderr -split "`r?`n"
    $kept = @($lines | Where-Object { $_ -cnotmatch 'Jolt Physics job system exceeded the maximum number of jobs|at: CreateJob \(modules/jolt_physics/spaces/jolt_job_system\.cpp' })
    if ($kept.Count -eq $lines.Count) { return $Stderr }
    if (-not ($kept | Where-Object { $_ -match '\S' })) { return '' }
    return ($kept -join "`n")
}

function Assert-GarageGameCountedStderr {
    # The rule's check of itself, run by each runner before a step leans on it: the busy machine's two lines alone are a clean
    # stderr, and whatever stands beside them, or only looks like them, still counts. A pattern made wider or narrower throws here.
    $busy = "WARNING: Jolt Physics job system exceeded the maximum number of jobs. Waiting for jobs to become available...`n   at: CreateJob (modules/jolt_physics/spaces/jolt_job_system.cpp:123)"
    $other = "WARNING: another warning`n   at: CreateJob (modules/other/file.cpp:3)"
    $cases = @(@('', ''), @($busy, ''), @("$busy`r`n", ''), @($other, $other), @("$busy`n$other", $other), @("ERROR: a real error`n$busy", 'ERROR: a real error'),
        @($busy.ToLowerInvariant(), $busy.ToLowerInvariant()), @(" `n", " `n"))
    foreach ($case in $cases) {
        if ((Get-GarageGameCountedStderr $case[0]) -cne $case[1]) { throw "Get-GarageGameCountedStderr no longer counts a stderr as the runners rely on it to: '$($case[0])'" }
    }
}

function Build-GarageGameNativeProject {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Toolchain)
    $result = Invoke-GarageGameNativeProcess -Toolchain $Toolchain -FilePath $Toolchain.DotnetPath `
        -Arguments @('build', 'GarageGame.csproj', '--configuration', 'Debug', '--nologo') -TimeoutSeconds 180
    if (-not [string]::IsNullOrWhiteSpace($result.Stderr)) { Write-Warning $result.Stderr.Trim() }
    Write-Output $result.Stdout.Trim()
}

function Invoke-GarageGameRoomBoot {
    <#
    .SYNOPSIS
    Boots a room through the game's main scene (scenes/native_boot.tscn reads --room) and quits by RoomWorld's
    "-- --quit-after-ready=N"; throws unless it exits 0 with "ROOM_WORLD_READY room=<id>", "ROOM_WORLD_QUIT" and nothing on stderr
    (with -BusyMachineWarningAllowed, nothing but the busy machine's warning: Get-GarageGameCountedStderr).
    .DESCRIPTION
    Godot never compiles C# when it runs a game: it runs the assembly last built into game/.godot. Build first (the runners
    and tools/smoke-boot.ps1 do). An assembly built before an option existed ignores it: on 13 October a smoke boot on a
    checkout not rebuilt since its merge ran an assembly older than --quit-after-ready and never quit.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Toolchain,
        # A room id (the player's user://rooms first, then res://rooms) or a room folder.
        [Parameter(Mandatory = $true)][string]$Room,
        # The id ROOM_WORLD_READY must name; by default the folder's name or the id.
        [string]$RoomId,
        [int]$Frames = 120,
        [int]$TimeoutSeconds = 120,
        # An exported game instead of the engine and the project.
        [string]$FilePath,
        [string]$WorkingDirectory,
        [switch]$WithoutDotnetRuntime,
        [hashtable]$Environment = @{},
        # The runner's rule (tools/test-room.ps1): the Jolt job system's warning under load is not a failure. Off, any stderr fails.
        [switch]$BusyMachineWarningAllowed
    )
    $arguments = @('--headless')
    if (-not $FilePath) {
        $FilePath = $Toolchain.EnginePath
        $arguments += @('--path', $Toolchain.ProjectPath)
    }
    $arguments += @('--fixed-fps', '60', '--', "--room=$($Room.Replace('\', '/'))", "--quit-after-ready=$Frames")
    if (-not $RoomId) { $RoomId = Split-Path -Leaf $Room.TrimEnd('\', '/') }
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $run = Invoke-GarageGameNativeProcess -Toolchain $Toolchain -FilePath $FilePath -Arguments $arguments -TimeoutSeconds $TimeoutSeconds `
        -WorkingDirectory $WorkingDirectory -WithoutDotnetRuntime:$WithoutDotnetRuntime -Environment $Environment
    $ready = $run.Stdout -split "`r?`n" | Where-Object { $_ -match "^ROOM_WORLD_READY room=$([regex]::Escape($RoomId))\b" } | Select-Object -First 1
    $quit = $run.Stdout -split "`r?`n" | Where-Object { $_ -match '^ROOM_WORLD_QUIT ' } | Select-Object -First 1
    $stderr = if ($BusyMachineWarningAllowed) { Get-GarageGameCountedStderr $run.Stderr } else { $run.Stderr }
    if ($stderr -or -not $ready -or -not $quit) {
        throw "Booting $RoomId through the main scene did not load and quit cleanly:`n$($run.Stdout)`n$($run.Stderr)"
    }
    return [pscustomobject]@{ Seconds = $clock.Elapsed.TotalSeconds; Ready = $ready; Quit = $quit; Stdout = $run.Stdout }
}
