#requires -Version 7.0
param([string]$EnginePath, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')
$nativeToolchain = Get-GarageGameNativeToolchain -EnginePath $EnginePath -DotnetPath $DotnetPath
Write-Output "Godot $($nativeToolchain.EngineVersion); .NET SDK $($nativeToolchain.SdkVersion)"
Build-GarageGameNativeProject -Toolchain $nativeToolchain
