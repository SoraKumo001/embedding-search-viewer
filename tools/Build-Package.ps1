<#
.SYNOPSIS
    ローカル環境でアプリケーションをビルド・発行し、署名およびパッケージング（zip作成）を行います。

.DESCRIPTION
    dotnet publish を実行し、ライセンス表示（THIRD-PARTY-NOTICES.md）を同梱した上で、
    PFX 証明書が存在する場合はコード署名を行い、配布用 zip アーカイブを出力します。

.PARAMETER Configuration
    ビルド構成（Release または Debug。デフォルト: Release）。

.PARAMETER PfxPath
    コード署名に使用する PFX 証明書のパス（デフォルト: certs/dev-cert.pfx）。
    ファイルが存在しない場合は署名をスキップします。

.PARAMETER Password
    PFX 証明書のパスワード（デフォルト: devpassword）。

.PARAMETER SkipSign
    署名ステップを強制的にスキップします。

.EXAMPLE
    .\tools\Build-Package.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$Runtime = "win-x64",
    [string]$PfxPath = "$PSScriptRoot\..\certs\dev-cert.pfx",
    [string]$Password = "devpassword",
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

$rootDir = (Resolve-Path "$PSScriptRoot\..").Path
$versionFile = Join-Path $rootDir "version.txt"
$version = ((Get-Content -Raw $versionFile) ?? '').Trim()
if (-not $version) {
    $version = "0.0.0"
}

$packageName = "EmbeddingSearchViewer-$version-$Runtime"
$publishDir = Join-Path $rootDir "artifacts\publish"
$packageDir = Join-Path $rootDir "artifacts\$packageName"
$zipPath = Join-Path $rootDir "artifacts\$packageName.zip"

Write-Host "=== Packaging EmbeddingSearchViewer v$version ($Runtime) ===" -ForegroundColor Cyan

# 出力ディレクトリのクリーンアップ
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }

# 1. dotnet publish
Write-Host "1. Running dotnet publish..." -ForegroundColor White
$projPath = Join-Path $rootDir "src\EmbeddingSearchViewer.App\EmbeddingSearchViewer.App.csproj"
dotnet publish $projPath -c $Configuration -p:Platform=$Platform -r $Runtime --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

# フォルダ移動
Move-Item -Path $publishDir -Destination $packageDir

# 2. README / LICENSE / サードパーティライセンス通知の配置
Write-Host "2. Copying licenses and notices..." -ForegroundColor White
Copy-Item (Join-Path $rootDir "README.md") $packageDir -Force
Copy-Item (Join-Path $rootDir "LICENSE") $packageDir -Force
& (Join-Path $PSScriptRoot "New-ThirdPartyNotices.ps1") -OutputDirectory $packageDir

# 3. コード署名
if (-not $SkipSign) {
    if (Test-Path $PfxPath) {
        Write-Host "3. Signing binaries..." -ForegroundColor White
        $signScript = Join-Path $PSScriptRoot "Sign-Package.ps1"
        & $signScript -TargetPath $packageDir -PfxPath $PfxPath -Password $Password
    } else {
        Write-Host "3. Skipping code signing (Certificate '$PfxPath' not found)." -ForegroundColor Yellow
        Write-Host "   Run .\tools\New-DevCertificate.ps1 to generate a test certificate." -ForegroundColor Gray
    }
} else {
    Write-Host "3. Skipping code signing (-SkipSign specified)." -ForegroundColor Yellow
}

# 4. ZIP アーカイブの作成
Write-Host "4. Creating zip archive..." -ForegroundColor White
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $packageDir -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
"$hash  $packageName.zip" | Set-Content -Encoding ascii (Join-Path $rootDir "artifacts\SHA256SUMS.txt")

$zipSizeMb = [Math]::Round(((Get-Item $zipPath).Length / 1MB), 2)
Write-Host "Successfully packaged: $zipPath ($zipSizeMb MB)" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Gray
