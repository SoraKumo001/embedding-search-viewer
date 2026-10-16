<#
.SYNOPSIS
    Microsoft Store 提出用（またはローカルテスト用）の MSIX パッケージをビルドします。

.DESCRIPTION
    Windows App SDK の Single-project MSIX ツールを使用して、Package.appxmanifest に基づく
    .msix / .msixupload パッケージを生成します。

.PARAMETER Configuration
    ビルド構成（Release または Debug。デフォルト: Release）。

.PARAMETER Platform
    対象プラットフォーム（x64 または ARM64。デフォルト: x64）。

.PARAMETER OutputDirectory
    MSIX パッケージの出力先フォルダ（デフォルト: artifacts/msix）。

.PARAMETER ForStore
    Microsoft Store 提出用パッケージ（.msixupload）を作成します。
    Store が自動署名するため、ローカル証明書署名は無効化されます（デフォルト: true）。

.PARAMETER PfxPath
    ローカルテスト用（-ForStore:$false）にサイドロード可能な署名付き MSIX を作成する場合の PFX パス。

.PARAMETER Password
    PFX のパスワード。

.EXAMPLE
    # Microsoft Store 提出用パッケージのビルド
    .\tools\Build-Msix.ps1

    # ローカル検証用（自己署名付きで PC にインストール可能）のビルド
    .\tools\Build-Msix.ps1 -ForStore:$false -PfxPath "certs/dev-cert.pfx" -Password "devpassword"
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\msix",
    [switch]$ForStore = $true,
    [string]$PfxPath = "$PSScriptRoot\..\certs\dev-cert.pfx",
    [string]$Password = "devpassword"
)

$ErrorActionPreference = 'Stop'

$rootDir = (Resolve-Path "$PSScriptRoot\..").Path
$projPath = Join-Path $rootDir "src\EmbeddingSearchViewer.App\EmbeddingSearchViewer.App.csproj"
$manifestPath = Join-Path $rootDir "src\EmbeddingSearchViewer.App\Package.appxmanifest"

# マニフェストのチェック
$manifestXml = [xml](Get-Content -Path $manifestPath -Raw)
$identityName = $manifestXml.Package.Identity.Name
$publisher = $manifestXml.Package.Identity.Publisher

Write-Host "=== Building MSIX Package for EmbeddingSearchViewer ===" -ForegroundColor Cyan
Write-Host "Manifest Identity Name: $identityName" -ForegroundColor DarkGray
Write-Host "Manifest Publisher    : $publisher" -ForegroundColor DarkGray

if ($identityName -like "A6B315D1-*" -or $publisher -eq "CN=AppPublisher") {
    Write-Warning "Package.appxmanifest にデフォルトの GUID やプレースホルダーが設定されています。"
    Write-Warning "Partner Center に提出する前に、Partner Center の「アプリ ID」から取得した値に更新してください。"
}

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}
$resolvedOutDir = (Resolve-Path $OutputDirectory).Path

$buildArgs = @(
    "publish",
    $projPath,
    "-c", $Configuration,
    "-p:Platform=$Platform",
    "-r", "win-$Platform",
    "-p:WindowsPackageType=MSIX",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:AppxPackageDir=$resolvedOutDir\",
    "-p:AppxSymbolPackageEnabled=false"
)

if ($ForStore) {
    Write-Host "Mode: Microsoft Store submission package (.msix)" -ForegroundColor Green
    $buildArgs += "-p:AppxPackageSigningEnabled=false"
} else {
    Write-Host "Mode: Local test package (sideloadable signed .msix)" -ForegroundColor Yellow
    if (Test-Path $PfxPath) {
        $resolvedPfx = (Resolve-Path $PfxPath).Path
        $buildArgs += "-p:AppxPackageSigningEnabled=true"
        $buildArgs += "-p:PackageCertificateKeyFile=$resolvedPfx"
        if ($Password) {
            $buildArgs += "-p:PackageCertificatePassword=$Password"
        }
    } else {
        Write-Warning "Certificate '$PfxPath' not found. Sideload package will not be signed."
        $buildArgs += "-p:AppxPackageSigningEnabled=false"
    }
}

Write-Host "Running: dotnet $($buildArgs -join ' ')" -ForegroundColor DarkGray
dotnet @buildArgs

if ($LASTEXITCODE -ne 0) {
    throw "MSIX package build failed (ExitCode: $LASTEXITCODE)."
}

Write-Host ""
Write-Host "MSIX package build completed successfully!" -ForegroundColor Green
Write-Host "Artifacts are located in: $resolvedOutDir" -ForegroundColor Cyan
Get-ChildItem -Path $resolvedOutDir -Recurse -Include *.msix,*.msixupload,*.msixbundle | ForEach-Object {
    Write-Host "  -> $($_.FullName) ($([Math]::Round($_.Length / 1MB, 2)) MB)" -ForegroundColor White
}
