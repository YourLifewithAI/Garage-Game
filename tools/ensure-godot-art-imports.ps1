param(
    [Parameter(Mandatory = $true)][string]$EnginePath,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

function Get-ArtImportSettingsHash {
    param([string]$Sidecar)
    $section = [regex]::Match($Sidecar, '(?ms)^\[params\]\s*\r?\n(.*?)(?=^\[|\z)')
    if (-not $section.Success) {
        return ''
    }
    # Whitespace outside quoted values is not a setting. Godot can rewrap
    # dictionaries and arrays without changing their meaning; preserve string
    # contents and escapes while ignoring that formatting and line comments.
    $normalized = [System.Text.StringBuilder]::new()
    $quoted = $false
    $escaped = $false
    $comment = $false
    foreach ($character in $section.Groups[1].Value.ToCharArray()) {
        if ($comment) {
            if ($character -eq "`n") { $comment = $false }
            continue
        }
        if ($quoted) {
            [void]$normalized.Append($character)
            if ($escaped) { $escaped = $false }
            elseif ($character -eq '\') { $escaped = $true }
            elseif ($character -eq '"') { $quoted = $false }
            continue
        }
        if ($character -eq ';' -or $character -eq '#') {
            $comment = $true
        } elseif ($character -eq '"') {
            $quoted = $true
            [void]$normalized.Append($character)
        } elseif (-not [char]::IsWhiteSpace($character)) {
            [void]$normalized.Append($character)
        }
    }
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $algorithm.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized.ToString()))
        return ([System.BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
    }
}

function Get-ArtImportResourceKey {
    param([string]$Root, [string]$ResourcePath)
    $prefix = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return $ResourcePath.Substring($prefix.Length).Replace('\', '/')
}

function Get-PendingArtImport {
    param([string]$Root, [hashtable]$SettingsHashes = @{}, [switch]$SkipSettingsCheck)
    $artPath = Join-Path $Root 'assets\art\painterly'
    if (-not (Test-Path -LiteralPath $artPath -PathType Container)) {
        throw 'The painterly reference assets are missing from this checkout.'
    }
    $resources = @(Get-ChildItem -LiteralPath $artPath -Recurse -File | Where-Object Extension -In @('.png', '.glb'))
    if ($resources.Count -eq 0) {
        throw 'No painterly PNG or GLB reference assets were found for import.'
    }
    foreach ($resource in $resources) {
        $sidecarPath = $resource.FullName + '.import'
        if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) {
            $resource.FullName
            continue
        }
        $sidecar = Get-Content -LiteralPath $sidecarPath -Raw
        $remaps = [regex]::Matches($sidecar, '(?m)^path(?:\.[^=]+)?="(res://[^"]+)"')
        if ($remaps.Count -eq 0) {
            $resource.FullName
            continue
        }
        $cachePending = $false
        foreach ($remap in $remaps) {
            $cachePath = Join-Path $Root $remap.Groups[1].Value.Substring(6)
            if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) {
                $resource.FullName
                $cachePending = $true
                break
            }
            if ((Get-Item -LiteralPath $cachePath).LastWriteTimeUtc -lt $resource.LastWriteTimeUtc) {
                # A checkout can update source timestamps without changing its
                # content. Godot correctly reuses that cache; compare its source
                # digest rather than requiring an unnecessary cache rewrite.
                # Texture caches include a platform suffix (for example
                # .s3tc.ctex), while Godot's digest is attached to the shared
                # source-name/hash prefix, without that suffix.
                $cachePrefix = [regex]::Match($cachePath, '^(.*-[a-fA-F0-9]{32})(?:\.[^\\/.]+)+$')
                $digestPath = if ($cachePrefix.Success) { $cachePrefix.Groups[1].Value + '.md5' } else { [System.IO.Path]::ChangeExtension($cachePath, '.md5') }
                $digest = if (Test-Path -LiteralPath $digestPath -PathType Leaf) { Get-Content -LiteralPath $digestPath -Raw } else { '' }
                $recorded = [regex]::Match($digest, 'source_md5="([a-fA-F0-9]{32})"')
                $current = (Get-FileHash -LiteralPath $resource.FullName -Algorithm MD5).Hash
                if (-not $recorded.Success -or $recorded.Groups[1].Value -ne $current) {
                    $resource.FullName
                    $cachePending = $true
                    break
                }
            }
        }
        if (-not $cachePending -and -not $SkipSettingsCheck) {
            $key = Get-ArtImportResourceKey -Root $Root -ResourcePath $resource.FullName
            $currentSettings = Get-ArtImportSettingsHash -Sidecar $sidecar
            if ($currentSettings.Length -eq 0 -or -not $SettingsHashes.ContainsKey($key) -or $SettingsHashes[$key] -ne $currentSettings) {
                $resource.FullName
            }
        }
    }
}

$settingsStampPath = Join-Path $ProjectPath '.godot\garage-game-art-import-settings.json'
$settingsHashes = @{}
if (Test-Path -LiteralPath $settingsStampPath -PathType Leaf) {
    try {
        $stamp = Get-Content -LiteralPath $settingsStampPath -Raw | ConvertFrom-Json
        if ($stamp.version -eq 1 -and $null -ne $stamp.resources) {
            foreach ($property in $stamp.resources.PSObject.Properties) {
                $settingsHashes[$property.Name] = [string]$property.Value
            }
        }
    } catch {
        # The stamp is disposable local cache state. An unreadable stamp must
        # trigger one import, never bless unknown settings as current.
        $settingsHashes = @{}
    }
}
$pending = @(Get-PendingArtImport -Root $ProjectPath -SettingsHashes $settingsHashes)
if ($pending.Count -eq 0) {
    return
}

Write-Output "Preparing $($pending.Count) new or changed painterly assets for Godot."
$stdoutPath = Join-Path $env:TEMP "garage-game-art-import-$PID.out.txt"
$stderrPath = Join-Path $env:TEMP "garage-game-art-import-$PID.err.txt"
$process = Start-Process -FilePath $EnginePath -ArgumentList @('--headless', '--editor', '--path', ('"' + $ProjectPath + '"'), '--import') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    Stop-Process -Id $process.Id -Force
    throw "Godot asset preparation timed out after $TimeoutSeconds seconds. Logs: $stderrPath"
}
$process.Refresh()
$stdout = Get-Content -LiteralPath $stdoutPath -Raw
$stderr = Get-Content -LiteralPath $stderrPath -Raw
if ($null -eq $process.ExitCode -or $process.ExitCode -ne 0 -or $stderr -match '(?m)^(?:SCRIPT )?ERROR:') {
    Write-Output $stdout
    Write-Output $stderr
    throw "Godot could not prepare the painterly assets (exit $($process.ExitCode))."
}
$remaining = @(Get-PendingArtImport -Root $ProjectPath -SkipSettingsCheck)
if ($remaining.Count -gt 0) {
    throw "Godot left $($remaining.Count) painterly assets without a current imported resource: $($remaining -join ', ')"
}
# Capture the settings after import, because Godot may normalize the sidecar.
# Only a successful process and verified resources may update this stamp.
$importedSettings = [ordered]@{}
$artPath = Join-Path $ProjectPath 'assets\art\painterly'
foreach ($resource in @(Get-ChildItem -LiteralPath $artPath -Recurse -File | Where-Object Extension -In @('.png', '.glb') | Sort-Object FullName)) {
    $sidecar = Get-Content -LiteralPath ($resource.FullName + '.import') -Raw
    $settingsHash = Get-ArtImportSettingsHash -Sidecar $sidecar
    if ($settingsHash.Length -eq 0) {
        throw "Godot asset sidecar lacks import settings: $($resource.FullName)"
    }
    $key = Get-ArtImportResourceKey -Root $ProjectPath -ResourcePath $resource.FullName
    $importedSettings[$key] = $settingsHash
}
$stampJson = [ordered]@{ version = 1; resources = $importedSettings } | ConvertTo-Json -Depth 3
$temporaryStamp = $settingsStampPath + ".$PID.tmp"
[System.IO.File]::WriteAllText($temporaryStamp, $stampJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporaryStamp -Destination $settingsStampPath -Force
Write-Output 'Painterly asset preparation completed.'
