<#
.SYNOPSIS
  Builds the Windows installer (setup.exe) with Inno Setup from a folder made by New-PackageLayout.ps1.

.PARAMETER PackageDir
  The package folder (it has app\, README.md, LICENSE, THIRD-PARTY-NOTICES.md and licenses\).

.PARAMETER Version
  The version to put in the installer, for example 1.2.3 or 1.2.3-beta.1.

.PARAMETER OutputDir
  Where setup.exe is written (default: the folder that contains the package folder).

.PARAMETER IsccPath
  Path of ISCC.exe. Found automatically (PATH, Program Files, LocalAppData) when omitted.

.OUTPUTS
  The path of the installer.
#>
param(
    [Parameter(Mandatory)] [string] $PackageDir,
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDir = '',
    [string] $IsccPath = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PackageDir = (Resolve-Path -LiteralPath $PackageDir).Path
if (-not $OutputDir) { $OutputDir = Split-Path -Parent $PackageDir }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.]+)?$') { throw "Unexpected version '$Version'." }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

if (-not $IsccPath) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    $IsccPath = $candidates | Select-Object -First 1
}
if (-not $IsccPath) {
    throw 'ISCC.exe (Inno Setup 6) was not found. Install it (choco install innosetup, or https://jrsoftware.org/isinfo.php) or pass -IsccPath.'
}

$script = Join-Path $root 'installer\EmbeddingSearchViewer.iss'
$appDir = Join-Path $PackageDir 'app'
if (-not (Test-Path -LiteralPath (Join-Path $appDir 'EmbeddingSearchViewer.App.exe'))) {
    throw "'$appDir' does not contain EmbeddingSearchViewer.App.exe."
}

& $IsccPath "/DAppVersion=$Version" "/DNumericVersion=$numeric" "/DAppDir=$appDir" "/DDocsDir=$PackageDir" "/DOutputDir=$OutputDir" /Q $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$setup = Join-Path $OutputDir "EmbeddingSearchViewer-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "The installer was not created at '$setup'." }
Write-Host ("Installer: {0} ({1:N0} MB)" -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
$setup
