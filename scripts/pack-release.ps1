#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build the Snip PowerToys Run plugin (Release) and zip it for distribution.

.DESCRIPTION
  Produces artifacts/Snip-<version>.zip whose root folder is "Snip\", matching
  %LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\.

.PARAMETER PowerToysInstallDir
  Path to a PowerToys install that provides Wox.Plugin etc. Default: C:\Program Files\PowerToys

.PARAMETER Configuration
  Build configuration. Default: Release

.PARAMETER OutputDir
  Directory for the zip and staging folder. Default: <repo>/artifacts
#>
[CmdletBinding()]
param(
    [string] $PowerToysInstallDir = "C:\Program Files\PowerToys",
    [string] $Configuration = "Release",
    [string] $OutputDir = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot "artifacts"
}

$Project = Join-Path $RepoRoot "Community.PowerToys.Run.Plugin.Snip\Community.PowerToys.Run.Plugin.Snip.csproj"
$PluginJsonPath = Join-Path $RepoRoot "Community.PowerToys.Run.Plugin.Snip\plugin.json"
if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

$pluginJson = Get-Content -Raw -Path $PluginJsonPath | ConvertFrom-Json
$Version = [string]$pluginJson.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "plugin.json Version is empty"
}

Write-Host "==> Building $Configuration (version $Version)"
$buildArgs = @(
    "build", $Project,
    "-c", $Configuration,
    "-p:EnableWindowsTargeting=true",
    "-p:PowerToysInstallDir=$PowerToysInstallDir",
    "-v:m"
)
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$BuildOut = Join-Path $RepoRoot "Community.PowerToys.Run.Plugin.Snip\bin\$Configuration"
if (-not (Test-Path $BuildOut)) {
    throw "Build output not found: $BuildOut"
}

$StageRoot = Join-Path $OutputDir "staging"
$StagePlugin = Join-Path $StageRoot "Snip"
if (Test-Path $StageRoot) {
    Remove-Item -Recurse -Force $StageRoot
}
New-Item -ItemType Directory -Path $StagePlugin -Force | Out-Null

Write-Host "==> Staging plugin files from $BuildOut"
# Copy everything except symbols (optional) and any accidental host refs
$excludeNames = @("*.pdb")
Get-ChildItem -Path $BuildOut -Force | ForEach-Object {
    $name = $_.Name
    if ($name -like "*.pdb") {
        return
    }
    Copy-Item -Path $_.FullName -Destination (Join-Path $StagePlugin $name) -Recurse -Force
}

function Assert-Exists([string] $RelativePath) {
    $full = Join-Path $StagePlugin $RelativePath
    if (-not (Test-Path $full)) {
        throw "Self-check failed: missing $RelativePath"
    }
    Write-Host "  OK  $RelativePath"
}

Write-Host "==> Self-check"
Assert-Exists "plugin.json"
Assert-Exists "Community.PowerToys.Run.Plugin.Snip.dll"
Assert-Exists "Images\Snip.dark.png"
Assert-Exists "Images\Snip.light.png"
Assert-Exists "WeChatOcr.dll"
Assert-Exists "Sdcb.HyMT2Sharp.dll"
Assert-Exists "wco_data"
Assert-Exists "wco_data\WeChatOCR.exe"
$wcoFiles = @(Get-ChildItem -Path (Join-Path $StagePlugin "wco_data") -Recurse -File -ErrorAction SilentlyContinue)
if ($wcoFiles.Count -lt 3) {
    throw "Self-check failed: wco_data looks incomplete ($($wcoFiles.Count) files)"
}
Write-Host "  OK  wco_data ($($wcoFiles.Count) files)"
Assert-Exists "zh-CN\Community.PowerToys.Run.Plugin.Snip.resources.dll"

$stagedPluginJson = Get-Content -Raw -Path (Join-Path $StagePlugin "plugin.json") | ConvertFrom-Json
if ($stagedPluginJson.Author -eq "Microsoft Corporation" -or ($stagedPluginJson.Website -like "*aka.ms*")) {
    throw "Self-check failed: plugin.json still has Microsoft / aka.ms metadata"
}
if ($stagedPluginJson.Version -ne $Version) {
    throw "Self-check failed: staged Version mismatch"
}
Write-Host "  OK  plugin.json Author=$($stagedPluginJson.Author) Version=$($stagedPluginJson.Version)"

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$ZipPath = Join-Path $OutputDir "Snip-$Version.zip"
if (Test-Path $ZipPath) {
    Remove-Item -Force $ZipPath
}

Write-Host "==> Zipping $ZipPath"
Compress-Archive -Path $StagePlugin -DestinationPath $ZipPath -Force

$zipSize = (Get-Item $ZipPath).Length
Write-Host "==> Done: $ZipPath ($([math]::Round($zipSize/1MB, 1)) MB)"
Write-Host "Unzip into: %LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\"
Write-Host "Then restart PowerToys. Folder name inside the zip is Snip\."
