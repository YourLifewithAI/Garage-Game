#requires -Version 7.0
<#
.SYNOPSIS
  Read-only check of what this machine has for Run 1. Installs nothing and changes nothing.
.DESCRIPTION
  Reports PASS / WARN / FAIL per check, which lanes need it (L Look, P Play, C Capture, A AI companion),
  and the exact next step. Exit code is 1 when any check fails.
.PARAMETER PhotosPath
  Optional folder holding a room's photos. By default the check looks for the garage set in the
  Google Drive for desktop folder, My Drive\Enfractal\Photos for space generation\Garage. Photos stay
  in Google Drive and are read in place; never copy them anywhere Git tracks.
.EXAMPLE
  pwsh -NoProfile -File tools/check-run1-readiness.ps1
.EXAMPLE
  pwsh -NoProfile -File tools/check-run1-readiness.ps1 -PhotosPath "G:\My Drive\Enfractal\Photos for space generation\Garage"
#>
param([string]$PhotosPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Status, [string]$Check, [string]$Lanes, [string]$Detail) {
    $results.Add([pscustomobject]@{ Status = $Status; Check = $Check; Lanes = $Lanes; Detail = $Detail })
}
function Find-App([string[]]$Names) {
    foreach ($name in $Names) {
        $found = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.Source }
    }
    return $null
}
function Invoke-Quiet([string]$File, [string[]]$Arguments) {
    try {
        $text = (& $File @Arguments 2>&1 | Out-String).Trim()
        return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Text = $text }
    } catch {
        return [pscustomobject]@{ Ok = $false; Text = $_.Exception.Message }
    }
}

# --- shell, OS, git ----------------------------------------------------------------------------
Add-Result 'PASS' 'PowerShell 7+' 'all' "PowerShell $($PSVersionTable.PSVersion)"
if (-not $IsWindows) {
    Add-Result 'INFO' 'Operating system' 'all' 'This checker targets the Windows development machine. In a Linux cloud session run tools/linux/setup-toolchain.sh and tools/linux/test-all.sh instead.'
}
$git = Find-App @('git')
if ($git) {
    $branch = Invoke-Quiet $git @('-C', $repo, 'rev-parse', '--abbrev-ref', 'HEAD')
    $dirty = Invoke-Quiet $git @('-C', $repo, 'status', '--porcelain')
    $changes = if ($dirty.Text) { ($dirty.Text -split "`n").Count } else { 0 }
    Add-Result 'PASS' 'Git' 'all' "$((Invoke-Quiet $git @('--version')).Text); branch $($branch.Text); $changes uncommitted change(s)"
} else {
    Add-Result 'FAIL' 'Git' 'all' 'Install Git for Windows: winget install --id Git.Git -e'
}

# --- Godot .NET and .NET SDK -------------------------------------------------------------------
$toolchainOk = $false
try {
    . (Join-Path $PSScriptRoot 'native-toolchain.ps1')
    $toolchain = Get-GarageGameNativeToolchain
    $toolchainOk = $true
    Add-Result 'PASS' 'Godot .NET 4.7.2' 'L P' "$($toolchain.EngineVersion) at $($toolchain.EnginePath)"
    Add-Result 'PASS' '.NET SDK 8.0.425' 'L P' "$($toolchain.DotnetPath)"
} catch {
    Add-Result 'FAIL' 'Pinned Godot .NET and .NET SDK' 'L P' "$($_.Exception.Message) Fix: pwsh -NoProfile -File tools/bootstrap-native.ps1 (downloads both, checksum-verified, into .cache/ with no installer)."
}

# A copy on the desktop is usable only if it is the .NET build of exactly 4.7.2 with GodotSharp beside it.
$desktops = @([Environment]::GetFolderPath('Desktop'))
if ($env:OneDrive) { $desktops += (Join-Path $env:OneDrive 'Desktop') }
$candidates = foreach ($desktop in $desktops | Where-Object { $_ -and (Test-Path -LiteralPath $_) }) {
    Get-ChildItem -LiteralPath $desktop -Recurse -Depth 3 -File -Filter 'Godot*.exe' -ErrorAction SilentlyContinue
}
foreach ($exe in $candidates | Sort-Object FullName -Unique) {
    $isMono = $exe.Name -match '_mono_'
    $hasSharp = Test-Path -LiteralPath (Join-Path $exe.DirectoryName 'GodotSharp') -PathType Container
    if (-not $isMono) {
        Add-Result 'WARN' "Desktop Godot: $($exe.Name)" 'L P' 'This is the standard build; it has no C# support and cannot build this project. Use tools/bootstrap-native.ps1, or download the .NET build of 4.7.2.'
    } elseif (-not $hasSharp) {
        Add-Result 'WARN' "Desktop Godot: $($exe.Name)" 'L P' 'The .NET build needs its GodotSharp folder beside the executable. Extract the whole archive, not just the .exe.'
    } elseif ($exe.Name -match '_console\.exe$') {
        $version = Invoke-Quiet $exe.FullName @('--version')
        if ($version.Text -match '^4\.7\.2\.stable\.mono') {
            $status = if ($toolchainOk) { 'INFO' } else { 'PASS' }
            Add-Result $status "Desktop Godot: $($exe.Name)" 'L P' "Usable. To use it instead of the .cache copy: `$env:GARAGE_GAME_GODOT_DOTNET = '$($exe.FullName)'"
        } else {
            Add-Result 'WARN' "Desktop Godot: $($exe.Name)" 'L P' "Reports '$($version.Text)'; the project pins 4.7.2 .NET."
        }
    }
}

$templates = Join-Path $repo '.cache/godot-export/templates/windows_release_x86_64.exe'
if (Test-Path -LiteralPath $templates) { Add-Result 'PASS' 'Export templates' 'integrator' 'Present' }
else { Add-Result 'INFO' 'Export templates' 'integrator' 'Only needed to build a release: tools/bootstrap-native.ps1 -IncludeExportTemplates' }

# --- GPU -------------------------------------------------------------------------------------
$smi = Find-App @('nvidia-smi')
if ($smi) {
    $query = Invoke-Quiet $smi @('--query-gpu=name,driver_version,memory.total', '--format=csv,noheader,nounits')
    $banner = Invoke-Quiet $smi @()
    $cuda = if ($banner.Text -match 'CUDA Version:\s*([\d.]+)') { $Matches[1] } else { 'unknown' }
    foreach ($line in ($query.Text -split "`n" | Where-Object { $_.Trim() })) {
        $parts = $line.Split(',').ForEach({ $_.Trim() })
        $vram = [int]$parts[2]
        $status = if ($vram -ge 7900) { 'PASS' } else { 'WARN' }
        Add-Result $status "GPU: $($parts[0])" 'L C' "$vram MiB VRAM, driver $($parts[1]), CUDA $cuda. Look captures and pose estimation run here; generation models above 8 GB use the rented or hosted options in docs/pipeline/COMPUTE-OPTIONS.md."
    }
} elseif ($IsWindows) {
    $gpus = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
    Add-Result 'WARN' 'NVIDIA driver' 'L C' "nvidia-smi not found (GPUs seen: $($gpus -join ', ')). Install or update the NVIDIA driver; the Capture lane needs CUDA and the Look lane needs the GPU for captures."
} else {
    Add-Result 'INFO' 'GPU' 'L C' 'No NVIDIA tools here. Look captures and pose estimation need the Windows machine or a rented GPU.'
}

# --- Python, uv, Blender ---------------------------------------------------------------------
$pythonVersion = $null
foreach ($candidate in @(@('py', '-3', '--version'), @('python', '--version'), @('python3', '--version'))) {
    $app = Find-App @($candidate[0])
    if (-not $app) { continue }
    $output = Invoke-Quiet $app $candidate[1..($candidate.Count - 1)]
    if ($output.Text -match 'Python (\d+)\.(\d+)\.(\d+)') {
        $pythonVersion = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
        $pythonCommand = "$($candidate[0..($candidate.Count - 2)] -join ' ')"
        break
    }
}
if ($pythonVersion -and $pythonVersion -ge [version]'3.11') { Add-Result 'PASS' 'Python 3.11+' 'C A' "Python $pythonVersion via '$pythonCommand'" }
elseif ($pythonVersion) { Add-Result 'FAIL' 'Python 3.11+' 'C A' "Found $pythonVersion. Install 3.12: winget install --id Python.Python.3.12 -e" }
else { Add-Result 'FAIL' 'Python 3.11+' 'C A' 'Not found. Install 3.12: winget install --id Python.Python.3.12 -e' }

$uv = Find-App @('uv')
if ($uv) { Add-Result 'PASS' 'uv' 'C A' (Invoke-Quiet $uv @('--version')).Text }
else { Add-Result 'WARN' 'uv' 'C A' 'Recommended for pinned, project-local Python environments: winget install --id astral-sh.uv -e' }

$blender = Find-App @('blender')
if (-not $blender -and $IsWindows) {
    $blender = Get-ChildItem 'C:\Program Files\Blender Foundation' -Recurse -Depth 2 -Filter 'blender.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if ($blender) { Add-Result 'PASS' 'Blender' 'L C (Run 2)' $blender }
else { Add-Result 'INFO' 'Blender' 'L C (Run 2)' 'Not needed for Run 1. For Run 2 mesh treatment: winget install --id BlenderFoundation.Blender.LTS.4.5 -e' }

# --- disk, memory, network -------------------------------------------------------------------
$root = [System.IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $repo).Path)
$drive = [System.IO.DriveInfo]::new($root)
$freeGb = [math]::Round($drive.AvailableFreeSpace / 1GB)
$diskStatus = if ($freeGb -ge 40) { 'PASS' } elseif ($freeGb -ge 20) { 'WARN' } else { 'FAIL' }
Add-Result $diskStatus 'Free disk space' 'C L' "$freeGb GB free on $root (model weights, splats and caches need roughly 20 to 40 GB)"
if ($IsWindows) {
    $ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    $ramStatus = if ($ramGb -ge 16) { 'PASS' } else { 'WARN' }
    Add-Result $ramStatus 'Memory' 'C' "$ramGb GB"
}
foreach ($site in @('https://github.com', 'https://api.nuget.org/v3/index.json', 'https://pypi.org/simple/', 'https://huggingface.co')) {
    try {
        $null = Invoke-WebRequest -Uri $site -Method Head -TimeoutSec 10 -UseBasicParsing
        Add-Result 'PASS' "Reach $(([uri]$site).Host)" 'all' 'Reachable'
    } catch {
        # Any HTTP answer, even an error status, proves the host is reachable; only connection failures matter.
        if ($_.Exception.Response) { Add-Result 'PASS' "Reach $(([uri]$site).Host)" 'all' "Reachable (HTTP $([int]$_.Exception.Response.StatusCode) to a HEAD probe)" }
        else { Add-Result 'WARN' "Reach $(([uri]$site).Host)" 'all' "Not reachable: $($_.Exception.Message)" }
    }
}

# --- Google Drive, photos and privacy ---------------------------------------------------------
# Photos and art references stay in the founder's Google Drive. Google Drive for desktop shows
# My Drive as a local folder: on its own drive letter when streaming (G: by default), or inside the
# user profile when mirroring. Agents read these files in place and never write into them.
function Find-EnfractalFolder {
    $roots = @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | ForEach-Object Root)
    if ($env:USERPROFILE) { $roots += $env:USERPROFILE }
    foreach ($root in $roots) {
        $candidate = Join-Path (Join-Path $root 'My Drive') 'Enfractal'
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    return $null
}
$ignore = Get-Content -LiteralPath (Join-Path $repo '.gitignore') -Raw
if ($ignore -match '(?m)^captures/') { Add-Result 'PASS' 'captures/ is ignored by Git' 'C' 'Working copies the Capture lane writes to captures/ cannot be committed by accident' }
else { Add-Result 'FAIL' 'captures/ is ignored by Git' 'C' 'Add captures/ to .gitignore before the Capture lane writes working copies there' }
$enfractal = Find-EnfractalFolder
if ($enfractal) {
    Add-Result 'PASS' 'Google Drive for desktop' 'C L' "Enfractal folder at $enfractal. Keep it available offline; agents read it in place and never write into it."
    $art = Join-Path $enfractal 'Art inspiration'
    if (Test-Path -LiteralPath $art -PathType Container) {
        $references = @(Get-ChildItem -LiteralPath $art -Recurse -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|webp|heic|heif)$' })
        $status = if ($references.Count -gt 0) { 'PASS' } else { 'WARN' }
        Add-Result $status 'Art references' 'L' "$($references.Count) images in $art. The look bible starts from them."
    } else {
        Add-Result 'WARN' 'Art references' 'L' "No 'Art inspiration' folder in $enfractal. The look bible starts from it."
    }
    if (-not $PhotosPath) { $PhotosPath = Join-Path (Join-Path $enfractal 'Photos for space generation') 'Garage' }
} else {
    Add-Result 'WARN' 'Google Drive for desktop' 'C L' 'No My Drive\Enfractal folder found. Install Google Drive for desktop, sign in with the account that owns Enfractal, right-click the Enfractal folder and choose Offline access, then Available offline. Then rerun this check.'
}
if ($PhotosPath) {
    if (Test-Path -LiteralPath $PhotosPath -PathType Container) {
        $full = (Resolve-Path -LiteralPath $PhotosPath).Path
        $photos = @(Get-ChildItem -LiteralPath $full -Recurse -File | Where-Object { $_.Extension -match '^\.(heic|heif|jpe?g|png)$' })
        $status = if ($photos.Count -ge 60) { 'PASS' } else { 'WARN' }
        Add-Result $status 'Room photos' 'C' "$($photos.Count) photos in $full. Coverage, not count, decides; the Capture lane's report will ask for what is missing."
        $repoFull = (Resolve-Path -LiteralPath $repo).Path
        if ($full.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase) -and -not $full.StartsWith((Join-Path $repoFull 'captures'), [StringComparison]::OrdinalIgnoreCase)) {
            Add-Result 'FAIL' 'Photo location' 'C' 'Photos are inside the repository but outside captures/. Move them to captures/<room>/ so Git ignores them.'
        }
    } else {
        Add-Result 'FAIL' 'Room photos' 'C' "Folder not found: $PhotosPath"
    }
} else {
    Add-Result 'INFO' 'Room photos' 'C' 'Set up Google Drive for desktop as above, or rerun with -PhotosPath pointing at the garage photos.'
}

# --- report ----------------------------------------------------------------------------------
$results | Format-Table -AutoSize -Wrap Status, Check, Lanes, Detail | Out-String -Width 220 | Write-Output
$failures = @($results | Where-Object Status -eq 'FAIL')
$warnings = @($results | Where-Object Status -eq 'WARN')
foreach ($lane in @(@('L', 'Look'), @('P', 'Play'), @('C', 'Capture'), @('A', 'AI companion'))) {
    $blocking = @($failures | Where-Object { $_.Lanes -eq 'all' -or ($_.Lanes -split '\s+') -contains $lane[0] })
    $state = if ($blocking.Count -eq 0) { 'ready' } else { "blocked by: $(($blocking | ForEach-Object Check) -join '; ')" }
    Write-Output ("Lane {0} ({1}): {2}" -f $lane[0], $lane[1], $state)
}
Write-Output "$($failures.Count) failure(s), $($warnings.Count) warning(s)."
exit ($(if ($failures.Count) { 1 } else { 0 }))
