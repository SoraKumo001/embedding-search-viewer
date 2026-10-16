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

# 2. コード署名（フォルダの配置の前に、発行したファイルに対して行う）
if (-not $SkipSign) {
    if (Test-Path $PfxPath) {
        Write-Host "2. Signing binaries..." -ForegroundColor White
        $signScript = Join-Path $PSScriptRoot "Sign-Package.ps1"
        & $signScript -TargetPath $publishDir -PfxPath $PfxPath -Password $Password
    } else {
        Write-Host "2. Skipping code signing (Certificate '$PfxPath' not found)." -ForegroundColor Yellow
        Write-Host "   Run .\tools\New-DevCertificate.ps1 to generate a test certificate." -ForegroundColor Gray
    }
} else {
    Write-Host "2. Skipping code signing (-SkipSign specified)." -ForegroundColor Yellow
}

# 3. フォルダの配置: 一番上に、起動用の .cmd、README、LICENSE、ライセンス通知。アプリ本体の約 400 ファイルは app\ に入る
Write-Host "3. Arranging the package folder..." -ForegroundColor White
& (Join-Path $PSScriptRoot "New-PackageLayout.ps1") -PublishDir $publishDir -Destination $packageDir

# 4. ZIP アーカイブの作成
Write-Host "4. Creating zip archive..." -ForegroundColor White
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $packageDir -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
"$hash  $packageName.zip" | Set-Content -Encoding ascii (Join-Path $rootDir "artifacts\SHA256SUMS.txt")

$zipSizeMb = [Math]::Round(((Get-Item $zipPath).Length / 1MB), 2)
Write-Host "Successfully packaged: $zipPath ($zipSizeMb MB)" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Gray

# 5. インストーラー（Inno Setup が入っているときだけ。なければ、zip だけで終わる）
$hasInno = (Get-Command ISCC.exe -ErrorAction SilentlyContinue) -or
    (Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") -or (Test-Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
if ($hasInno) {
    Write-Host "5. Building the installer..." -ForegroundColor White
    $setup = & (Join-Path $PSScriptRoot "Build-Installer.ps1") -PackageDir $packageDir -Version $version -OutputDir (Join-Path $rootDir "artifacts") | Select-Object -Last 1
    if (-not $SkipSign -and (Test-Path $PfxPath)) {
        & (Join-Path $PSScriptRoot "Sign-Package.ps1") -TargetPath $setup -PfxPath $PfxPath -Password $Password
    }
    $setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
    "$setupHash  $(Split-Path -Leaf $setup)" | Add-Content -Encoding ascii (Join-Path $rootDir "artifacts\SHA256SUMS.txt")
} else {
    Write-Host "5. Skipping the installer (Inno Setup 6 is not installed: choco install innosetup)." -ForegroundColor Yellow
}
