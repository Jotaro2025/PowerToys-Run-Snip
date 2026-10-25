#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build the Snip plugin for x64 and ARM64 and compile the Inno Setup installers.

.DESCRIPTION
  For each architecture (win-x64, win-arm64):
    1. dotnet build -c Release -p:RuntimeIdentifier=<rid> into artifacts/installer/build/<arch>
    2. stage artifacts/installer/stage/<arch>-full/Snip  (no *.pdb, with wco_data)
       stage artifacts/installer/stage/<arch>-slim/Snip  (no *.pdb, without wco_data — same as the slim zip)
    3. ISCC installer/Snip.iss for both variants

  Output (artifacts/installer):
    Snip-Setup-<version>-x64-slim.exe    Snip-Setup-<version>-x64-full.exe
    Snip-Setup-<version>-arm64-slim.exe  Snip-Setup-<version>-arm64-full.exe

  The version is read from Community.PowerToys.Run.Plugin.Snip/plugin.json (the csproj has no
  <Version>) and passed to ISCC as /DAppVersion.

  On Linux/macOS the script runs ISCC.exe through wine (set -IsccPath to the Windows path of ISCC,
  for example "C:\InnoSetup6\ISCC.exe" inside your WINEPREFIX).

.PARAMETER PowerToysInstallDir
  PowerToys install (or build/PowerToysRefs) providing Wox.Plugin etc. Default: C:\Program Files\PowerToys

.PARAMETER IsccPath
  Path to ISCC.exe. Default: auto-detect (PATH, Inno Setup 6 under Program Files / LocalAppData).

.PARAMETER Architectures
  Subset of x64, arm64. Default: both.

.PARAMETER Variants
  Subset of slim, full. Default: both.

.PARAMETER OutputDir
  Default: <repo>/artifacts/installer

.PARAMETER SkipBuild
  Reuse artifacts/installer/build/<arch> from a previous run.
#>
[CmdletBinding()]
param(
    [string] $PowerToysInstallDir = "C:\Program Files\PowerToys",
    [string] $IsccPath = "",
    [ValidateSet("x64", "arm64")]
    [string[]] $Architectures = @("x64", "arm64"),
    [ValidateSet("slim", "full")]
    [string[]] $Variants = @("slim", "full"),
    [string] $Configuration = "Release",
    [string] $OutputDir = "",
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot "artifacts/installer"
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

$Project = Join-Path $RepoRoot "Community.PowerToys.Run.Plugin.Snip/Community.PowerToys.Run.Plugin.Snip.csproj"
$PluginJsonPath = Join-Path $RepoRoot "Community.PowerToys.Run.Plugin.Snip/plugin.json"
$IssPath = Join-Path $RepoRoot "installer/Snip.iss"
$RunningOnWindows = ($PSVersionTable.PSEdition -eq "Desktop") -or $IsWindows

$Version = [string](Get-Content -Raw -Path $PluginJsonPath | ConvertFrom-Json).Version
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "plugin.json Version '$Version' is not a numeric x.y.z version"
}
Write-Host "==> Snip $Version — architectures: $($Architectures -join ', '); variants: $($Variants -join ', ')"

function Find-Iscc {
    if ($IsccPath) {
        if ($RunningOnWindows -and -not (Test-Path $IsccPath)) {
            throw "ISCC not found: $IsccPath"
        }
        return $IsccPath
    }
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }
    $candidates = @()
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path ([string]$env:LOCALAPPDATA) "Programs"))) {
        if ($base) {
            $candidates += Join-Path $base "Inno Setup 6\ISCC.exe"
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            return $c
        }
    }
    throw "ISCC.exe (Inno Setup 6.5+) not found. Install it (choco install innosetup / winget install JRSoftware.InnoSetup) or pass -IsccPath."
}

function ConvertTo-IsccPath([string] $Path) {
    if ($RunningOnWindows) {
        return $Path.Replace('/', '\')
    }
    $w = & winepath -w $Path
    if ($LASTEXITCODE -ne 0) {
        throw "winepath failed for $Path"
    }
    return ($w | Select-Object -Last 1).Trim()
}

function Invoke-Iscc([string[]] $IsccArgs) {
    if ($RunningOnWindows) {
        & $script:Iscc @IsccArgs
    }
    else {
        & wine $script:Iscc @IsccArgs
    }
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed with exit code $LASTEXITCODE"
    }
}

function Assert-Exists([string] $Root, [string] $RelativePath) {
    if (-not (Test-Path (Join-Path $Root $RelativePath))) {
        throw "Self-check failed: $Root is missing $RelativePath"
    }
}

function Copy-Stage([string] $From, [string] $To, [bool] $IncludeOcr) {
    if (Test-Path $To) {
        Remove-Item -Recurse -Force $To
    }
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    Get-ChildItem -Path $From -Force | ForEach-Object {
        if ($_.Name -like "*.pdb") {
            return
        }
        if (-not $IncludeOcr -and $_.Name -eq "wco_data") {
            return
        }
        Copy-Item -Path $_.FullName -Destination (Join-Path $To $_.Name) -Recurse -Force
    }
    Get-ChildItem -Path $To -Recurse -Filter "*.pdb" -File | Remove-Item -Force

    foreach ($rel in @(
            "plugin.json",
            "Community.PowerToys.Run.Plugin.Snip.dll",
            "Images/Snip.dark.png",
            "Images/Snip.light.png",
            "WeChatOcr.dll",
            "Sdcb.HyMT2Sharp.dll",
            "zh-CN/Community.PowerToys.Run.Plugin.Snip.resources.dll")) {
        Assert-Exists $To $rel
    }
    if ($IncludeOcr) {
        Assert-Exists $To "wco_data/WeChatOCR.exe"
        $n = @(Get-ChildItem -Path (Join-Path $To "wco_data") -Recurse -File).Count
        if ($n -lt 3) {
            throw "Self-check failed: wco_data looks incomplete ($n files)"
        }
    }
    elseif (Test-Path (Join-Path $To "wco_data")) {
        throw "Self-check failed: slim stage still contains wco_data"
    }
    $staged = Get-Content -Raw -Path (Join-Path $To "plugin.json") | ConvertFrom-Json
    if ($staged.Version -ne $Version) {
        throw "Self-check failed: staged plugin.json Version $($staged.Version) != $Version"
    }
    $bytes = (Get-ChildItem -Path $To -Recurse -File | Measure-Object -Sum Length).Sum
    Write-Host ("  staged {0} ({1:N1} MB)" -f $To, ($bytes / 1MB))
}

$script:Iscc = Find-Iscc
Write-Host "==> ISCC: $script:Iscc"

$BuildRoot = Join-Path $OutputDir "build"
$StageRoot = Join-Path $OutputDir "stage"
$results = @()

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $buildOut = Join-Path $BuildRoot $arch
    if (-not $SkipBuild) {
        Write-Host "==> dotnet publish ($rid, $Configuration, R2R)"
        if (Test-Path $buildOut) {
            Remove-Item -Recurse -Force $buildOut
        }
        $buildArgs = @(
            "publish", $Project,
            "-c", $Configuration,
            "-p:RuntimeIdentifier=$rid",
            "-p:SelfContained=false",
            "-p:EnableWindowsTargeting=true",
            "-p:PowerToysInstallDir=$PowerToysInstallDir",
            # ReadyToRun: precompiled native code trims JIT time when PowerToys
            # loads the plugin. Framework-dependent, so only the plugin's own
            # assemblies grow (~20-30%... measured ~2x on this assembly);
            # accepts any runtime >= the build's. publish (not build) is
            # required — the Crossgen step only runs on publish.
            "-p:PublishReadyToRun=true",
            "-o", $buildOut,
            "-v:m"
        )
        & dotnet @buildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish ($rid) failed with exit code $LASTEXITCODE"
        }
    }
    if (-not (Test-Path (Join-Path $buildOut "Community.PowerToys.Run.Plugin.Snip.dll"))) {
        throw "Build output not found: $buildOut"
    }

    foreach ($variant in $Variants) {
        $stage = Join-Path $StageRoot "$arch-$variant/Snip"
        Write-Host "==> Stage $arch-$variant"
        Copy-Stage $buildOut $stage ($variant -eq "full")

        Write-Host "==> ISCC $arch-$variant"
        Invoke-Iscc @(
            "/Q",
            "/DAppVersion=$Version",
            "/DArch=$arch",
            "/DVariant=$variant",
            "/DStageDir=$(ConvertTo-IsccPath $stage)",
            "/DOutputDir=$(ConvertTo-IsccPath $OutputDir)",
            (ConvertTo-IsccPath $IssPath)
        )
        $exe = Join-Path $OutputDir "Snip-Setup-$Version-$arch-$variant.exe"
        if (-not (Test-Path $exe)) {
            throw "Expected installer not produced: $exe"
        }
        $size = (Get-Item $exe).Length
        Write-Host ("  -> {0} ({1:N1} MB)" -f $exe, ($size / 1MB))
        $results += $exe
    }
}

Write-Host "==> Done ($($results.Count) installers)"
$results | ForEach-Object { Write-Host "  $_" }
