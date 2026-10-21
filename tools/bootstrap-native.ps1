#requires -Version 7.0
param([switch]$IncludeExportTemplates)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'This bootstrap is pinned for Windows x86-64; other hosts need separately verified packages.'
}
$repositoryPath = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-toolchain.lock.json') -Raw | ConvertFrom-Json
$downloadPath = Join-Path $repositoryPath '.cache/downloads'
New-Item -ItemType Directory -Force $downloadPath | Out-Null
$packages = @($lock.engine, $lock.sdk)
if ($IncludeExportTemplates) { $packages += $lock.templates }
foreach ($package in $packages) {
    $archivePath = Join-Path $downloadPath ([Uri]$package.url).Segments[-1]
    if (Test-Path -LiteralPath (Join-Path $repositoryPath $package.marker)) {
        Write-Output "Already extracted: $($package.marker)"
        continue
    }
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Output "Downloading $(([Uri]$package.url).Segments[-1])"
        Invoke-WebRequest -Uri $package.url -OutFile ($archivePath + '.partial')
        if ((Get-FileHash -LiteralPath ($archivePath + '.partial') -Algorithm SHA512).Hash -ne $package.sha512) {
            throw 'Downloaded archive checksum mismatch; nothing was extracted.'
        }
        Move-Item -LiteralPath ($archivePath + '.partial') -Destination $archivePath
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ne $package.sha512) {
        throw "Cached archive checksum mismatch: $archivePath"
    }
    $destinationPath = Join-Path $repositoryPath $package.destination
    New-Item -ItemType Directory -Force $destinationPath | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $destinationPath, $true)
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryPath $package.marker))) {
        throw "Expected tool missing after extraction: $($package.marker)"
    }
}
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')
$toolchain = Get-GarageGameNativeToolchain
Write-Output "Ready: $($toolchain.EngineVersion), .NET SDK $($toolchain.SdkVersion)"
