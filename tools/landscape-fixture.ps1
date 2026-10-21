# The garage landscape fixture, shared by every worktree on a machine (packet S10). Dot-source after native-toolchain.ps1.
#
# The exported garage is derived data: generated and exported at test time, never committed. Every worktree of a checkout
# reads one cache, under the integrator's checkout (the main worktree, found through git's common directory) at
# .cache/landscape-fixtures, or wherever GARAGE_GAME_FIXTURE_CACHE names:
#   <cache>/x1/<key>/landscape_garage_nominal    the quick fixture every run of tools/test-room.ps1 plays on
#   <cache>/x10/<key>/landscape_garage_nominal   the 10x island, built only for the scale suite (test-room.ps1 -Scale)
#   <cache>/x10/<key>/room                       the garage magnified ten times, which the 10x package was generated from
#   <cache>/x<N>/<key>/fixture.json              written last: the fixture is complete (its timings and size)
# <key> hashes the generator, the exporter, the harness and the corpus room (line endings normalised where Git converts
# them, so worktrees checked out at different times agree). A missing fixture is built by one process at a time, under
# <cache>/x<N>/<key>.lock (held with no sharing; the system frees it if the builder dies), into <key>.partial, then moved
# into place in one rename. Readers never write: the fixture's files are made read-only once built.

function Get-GarageGameLandscapeFixtureKey {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$RepositoryPath)
    # The same key as tools/linux/test-all.sh computes: sha256sum's lines ("<hash>  <path>"), in byte order of the paths, hashed.
    $files = [System.Collections.Generic.List[string]]::new()
    foreach ($folder in @('pipeline/landscape/generator', 'pipeline/landscape/export', 'pipeline/landscape/harness', 'pipeline/landscape/corpus/rooms/garage_nominal')) {
        Get-ChildItem -LiteralPath (Join-Path $RepositoryPath $folder) -Recurse -File -Include '*.py', '*.json' |
            ForEach-Object { [System.IO.Path]::GetRelativePath($RepositoryPath, $_.FullName).Replace('\', '/') } |
            Where-Object { $_ -notmatch '/(tests|renders|__pycache__)/' } | ForEach-Object { $files.Add($_) }
    }
    $files.Sort([System.StringComparer]::Ordinal)
    $listing = [System.Text.StringBuilder]::new()
    foreach ($file in $files) {
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $RepositoryPath $file))
        # Git converts line endings on checkout in the generator and the exporter (the harness and the corpus rooms are -text).
        if ($file -match '^pipeline/landscape/(generator|export)/') { $bytes = [System.Text.Encoding]::Latin1.GetBytes([System.Text.Encoding]::Latin1.GetString($bytes).Replace("`r`n", "`n")) }
        [void]$listing.Append([System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()).Append('  ').Append($file).Append("`n")
    }
    $digest = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($listing.ToString()))
    return [System.Convert]::ToHexString($digest).Substring(0, 16).ToLowerInvariant()
}

function Get-GarageGameFixtureCacheRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$RepositoryPath)
    if ($env:GARAGE_GAME_FIXTURE_CACHE) { return [System.IO.Path]::GetFullPath($env:GARAGE_GAME_FIXTURE_CACHE) }
    # The integrator's checkout: the main worktree owns the repository's common git directory.
    $common = (& git -C $RepositoryPath rev-parse --path-format=absolute --git-common-dir 2>$null | Out-String).Trim()
    $main = if ($LASTEXITCODE -eq 0 -and $common -and (Split-Path -Leaf $common) -eq '.git') { Split-Path -Parent $common } else { $RepositoryPath }
    return [System.IO.Path]::GetFullPath((Join-Path $main '.cache/landscape-fixtures'))
}

function Get-GarageGameLandscapeFixture {
    <#
    .SYNOPSIS
    The exported garage at world scale 1 or 10 from the shared cache, built first if it is missing. Returns its room folder,
    key, whether this call built it, and the fixture's record (fixture.json).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Toolchain,
        [ValidateSet(1, 10)][int]$Scale = 1,
        # How long to wait for another worktree building the same fixture.
        [int]$WaitSeconds = 1800
    )
    $repository = $Toolchain.RepositoryPath
    $key = Get-GarageGameLandscapeFixtureKey -RepositoryPath $repository
    $variant = Join-Path (Get-GarageGameFixtureCacheRoot -RepositoryPath $repository) "x$Scale"
    $final = Join-Path $variant $key
    $record = Join-Path $final 'fixture.json'
    $result = { param($built) [pscustomobject]@{
        Room = (Join-Path $final 'landscape_garage_nominal'); Key = $key; Scale = $Scale; Built = $built
        Record = (Get-Content -LiteralPath $record -Raw | ConvertFrom-Json) } }
    if (Test-Path -LiteralPath $record -PathType Leaf) { return (& $result $false) }

    New-Item -ItemType Directory -Force $variant | Out-Null
    $lockPath = Join-Path $variant "$key.lock"
    $lock = $null
    $waited = [System.Diagnostics.Stopwatch]::StartNew()
    $told = $false
    while (-not $lock) {
        try {
            $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        } catch [System.IO.IOException] {
            if (Test-Path -LiteralPath $record -PathType Leaf) { return (& $result $false) }
            if ($waited.Elapsed.TotalSeconds -gt $WaitSeconds) { throw "Waited $WaitSeconds s for another worktree building the x$Scale landscape fixture ($lockPath)." }
            if (-not $told) { Write-Host "Another worktree is building the x$Scale landscape fixture $key; waiting for it."; $told = $true }
            Start-Sleep -Seconds 2
        }
    }
    try {
        # Built while this one waited for the lock.
        if (Test-Path -LiteralPath $record -PathType Leaf) { return (& $result $false) }
        $partial = Join-Path $variant "$key.partial"
        # A build that died part-way (the lock is ours, so nobody else is writing it).
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Recurse -Force }
        New-Item -ItemType Directory -Force $partial | Out-Null
        $python = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $corpus = 'pipeline/landscape/corpus/rooms/garage_nominal'
        $package = Join-Path $partial 'package'
        $exported = Join-Path $partial 'landscape_garage_nominal'
        $steps = [ordered]@{}
        if ($Scale -eq 1) {
            $source = $corpus
            $steps.generate = @('-B', '-m', 'pipeline.landscape.generator.generate', '--room', $corpus, '--out', $package)
        } else {
            # The garage magnified ten times around today's body (RUN-2-TEN-TIMES.md). Since S4 the generator magnifies the
            # true-size room itself (a 10 cm base grid with 2.5 cm detail where the body walks), and the exporter reads the
            # package's world scale, so both take the corpus room as it is.
            $source = $corpus
            $steps.generate = @('-B', '-m', 'pipeline.landscape.generator.generate', '--room', $corpus, '--world-scale', "$Scale", '--out', $package, '--tutorial')
        }
        $steps.export = @('-B', '-S', '-m', 'pipeline.landscape.export', '--package', $package, '--room', $source, '--room-id', 'landscape_garage_nominal', '--out', $exported)
        $seconds = [ordered]@{}
        foreach ($step in $steps.Keys) {
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            $run = Invoke-GarageGameNativeProcess -Toolchain $Toolchain -FilePath $python -WorkingDirectory $repository -TimeoutSeconds 1800 -Arguments $steps[$step]
            $seconds[$step] = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            Write-Host (($run.Stdout.Trim().Split("`n") | Where-Object { $_ -match '^(LANDSCAPE_PACKAGE|ROOM_EXPORTED|SCALED_ROOM)' }) -join "`n")
        }
        # The package is only the export's input: the cache keeps what the game loads.
        Remove-Item -LiteralPath $package -Recurse -Force
        $bytes = (Get-ChildItem -LiteralPath $exported -Recurse -File | Measure-Object -Sum Length).Sum
        [ordered]@{
            key = $key; world_scale = $Scale; seconds = $seconds; room_bytes = [long]$bytes
            built_utc = [DateTime]::UtcNow.ToString('o'); built_in = $repository
        } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $partial 'fixture.json') -Encoding utf8NoBOM
        # Readers never write into the cache.
        Get-ChildItem -LiteralPath $partial -Recurse -File | ForEach-Object { $_.IsReadOnly = $true }
        [System.IO.Directory]::Move($partial, $final)
        Write-Host ("LANDSCAPE_FIXTURE x{0} {1} built in {2:0} s ({3}), {4:0.0} MB: {5}" -f $Scale, $key, ($seconds.Values | Measure-Object -Sum).Sum,
            (($seconds.Keys | ForEach-Object { "$_ $($seconds[$_]) s" }) -join ', '), ($bytes / 1MB), $final)
        Remove-GarageGameStaleLandscapeFixtures -Variant $variant -Keep $key
        return (& $result $true)
    } finally {
        $lock.Dispose()
    }
}

function Remove-GarageGameStaleLandscapeFixtures {
    # Older keys go once four newer ones exist and they are three days old, so a worktree still on an older generator keeps
    # its fixture for a while. A fixture another process holds open stays; the next build tries again.
    param([string]$Variant, [string]$Keep)
    $fixtures = Get-ChildItem -LiteralPath $Variant -Directory | Where-Object { $_.Name -match '^[0-9a-f]{16}$' -and $_.Name -ne $Keep } |
        Sort-Object CreationTimeUtc -Descending
    foreach ($old in ($fixtures | Select-Object -Skip 3 | Where-Object { $_.CreationTimeUtc -lt [DateTime]::UtcNow.AddDays(-3) })) {
        try { Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction Stop } catch { Write-Warning "Kept the old landscape fixture $($old.FullName): $($_.Exception.Message)" }
    }
}
