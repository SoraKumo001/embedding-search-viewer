<#
.SYNOPSIS
    Windows アプリケーションファイル（.exe, .dll など）にコード署名を付与します。

.DESCRIPTION
    Windows SDK に付属する signtool.exe を検出し、指定された PFX 証明書を使用して
    SHA256 Authenticode 署名および RFC 3161 タイムスタンプを付与します。

.PARAMETER TargetPath
    署名対象のファイルパス、またはディレクトリパス（ディレクトリの場合は内部の .exe とプロジェクト自作 .dll が対象）。

.PARAMETER PfxPath
    署名に使用する PFX 証明書ファイルのパス。

.PARAMETER Password
    PFX のパスワード。

.PARAMETER TimestampServer
    RFC 3161 タイムスタンプサーバー URL（デフォルト: http://timestamp.digicert.com）。

.PARAMETER SkipTimestamp
    タイムスタンプ付与をスキップする場合に指定します（オフライン環境など）。

.PARAMETER SignToolPath
    signtool.exe の明示的なパス。省略時は自動検出します。

.EXAMPLE
    .\tools\Sign-Package.ps1 -TargetPath "artifacts/publish/EmbeddingSearchViewer.App.exe" -PfxPath "certs/dev-cert.pfx" -Password "devpassword"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $false)]
    [string]$Password = "",

    [string]$TimestampServer = "http://timestamp.digicert.com",

    [switch]$SkipTimestamp,

    [string]$SignToolPath = ""
)

$ErrorActionPreference = 'Stop'

# PFX の存在確認
if (-not (Test-Path $PfxPath)) {
    throw "PFX certificate not found at '$PfxPath'."
}
$resolvedPfxPath = (Resolve-Path $PfxPath).Path

# signtool.exe の解決
if (-not $SignToolPath) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $SignToolPath = $cmd.Source
    } else {
        $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        if (Test-Path $kitsRoot) {
            $found = Get-ChildItem -Path $kitsRoot -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1

            if ($found) {
                $SignToolPath = $found.FullName
            }
        }
    }
}

if (-not $SignToolPath -or -not (Test-Path $SignToolPath)) {
    throw "signtool.exe could not be found. Please install the Windows SDK or specify -SignToolPath explicitly."
}

Write-Host "Using SignTool: $SignToolPath" -ForegroundColor DarkGray

# 署名対象ファイルの収集
$filesToSign = @()
if (Test-Path -Path $TargetPath -PathType Leaf) {
    $filesToSign += (Resolve-Path $TargetPath).Path
} elseif (Test-Path -Path $TargetPath -PathType Container) {
    # ディレクトリ内の exe および自前プロジェクト dll
    $filesToSign += Get-ChildItem -Path $TargetPath -Filter "*.exe" -Recurse | Select-Object -ExpandProperty FullName
    $filesToSign += Get-ChildItem -Path $TargetPath -Filter "EmbeddingSearchViewer*.dll" -Recurse | Select-Object -ExpandProperty FullName
} else {
    throw "Target path '$TargetPath' does not exist."
}

if ($filesToSign.Count -eq 0) {
    Write-Warning "No files to sign found at '$TargetPath'."
    return
}

Write-Host "Signing $($filesToSign.Count) file(s)..." -ForegroundColor Cyan

foreach ($file in $filesToSign) {
    Write-Host "  -> Signing: $file" -ForegroundColor White

    $signArgs = @(
        "sign",
        "/fd", "SHA256",
        "/f", $resolvedPfxPath
    )

    if ($Password) {
        $signArgs += @("/p", $Password)
    }

    if (-not $SkipTimestamp -and $TimestampServer) {
        $signArgs += @("/tr", $TimestampServer, "/td", "SHA256")
    }

    $signArgs += $file

    & $SignToolPath @signArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to sign '$file' (ExitCode: $LASTEXITCODE)."
    }

    # 署名の検証
    Write-Host "     Verifying signature..." -ForegroundColor DarkGray
    & $SignToolPath verify /pa $file
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Verification returned exit code $LASTEXITCODE for '$file'. (Note: Self-signed certificates require importing into Trusted Root to verify without warnings.)"
    } else {
        Write-Host "     Signature verified successfully." -ForegroundColor Green
    }
}

Write-Host "Code signing completed successfully." -ForegroundColor Green
