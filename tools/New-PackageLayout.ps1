<#
.SYNOPSIS
  Arranges the published app into the folder that is zipped (and from which the installer is built).

.DESCRIPTION
  The published app is about 400 files, so the zip is laid out to keep the top level short:

    <Destination>\
      EmbeddingSearchViewer.cmd      starts app\EmbeddingSearchViewer.App.exe (double-click this one)
      README.md  README.en.md  LICENSE  THIRD-PARTY-NOTICES.md  licenses\
      app\                           everything that was published

  The published folder is moved into <Destination>\app. The notices are generated there (the script lists the FFmpeg
  DLLs it finds next to the exe) and then moved up beside the README.

.PARAMETER PublishDir
  The output of "dotnet publish" (signed already, if it is going to be). It is moved, not copied.

.PARAMETER Destination
  The folder to create (for example artifacts\EmbeddingSearchViewer-1.2.3-win-x64).
#>
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $Destination
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exeName = 'EmbeddingSearchViewer.App.exe'

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $exeName))) {
    throw "$exeName was not found in '$PublishDir'. Publish the app first."
}
if (Test-Path -LiteralPath $Destination) { throw "'$Destination' already exists." }

New-Item -ItemType Directory -Path $Destination | Out-Null
$appDir = Join-Path $Destination 'app'
Move-Item -LiteralPath $PublishDir -Destination $appDir

# License notices of everything that is shipped (FFmpeg is LGPL: its license text must travel with the DLLs).
& (Join-Path $PSScriptRoot 'New-ThirdPartyNotices.ps1') -OutputDirectory $appDir
foreach ($name in 'THIRD-PARTY-NOTICES.md', 'licenses') {
    Move-Item -LiteralPath (Join-Path $appDir $name) -Destination (Join-Path $Destination $name)
}
Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'README.en.md'), (Join-Path $root 'LICENSE') -Destination $Destination

# The launcher. ASCII only, so that it survives any zip tool; "start" returns at once, so the window closes by itself.
$launcher = @(
    '@echo off',
    'rem Starts EmbeddingSearchViewer. The app itself is in the "app" folder next to this file.',
    "start `"`" `"%~dp0app\$exeName`""
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $Destination 'EmbeddingSearchViewer.cmd'), $launcher + "`r`n", [System.Text.Encoding]::ASCII)

$top = @(Get-ChildItem -LiteralPath $Destination | Select-Object -ExpandProperty Name)
Write-Host ("Package layout: {0} -> {1} items at the top level: {2}" -f $Destination, $top.Count, ($top -join ', '))
