<#
.SYNOPSIS
    ローカル開発・テスト用の自己署名コード署名証明書（.pfx, .cer）を生成します。

.DESCRIPTION
    Windows の New-SelfSignedCertificate を用いて CodeSigning EKU を持つ自己署名証明書を
    Cert:\CurrentUser\My に作成し、指定されたディレクトリ（デフォルト: certs/）へエクスポートします。
    -TrustLocally を指定すると、現在のユーザーの「信頼されたルート証明機関」に .cer を自動インポートし、
    ローカル PC 上で署名検証エラー（不明な発行元）が出ないように設定できます。

.EXAMPLE
    .\tools\New-DevCertificate.ps1 -TrustLocally
#>

[CmdletBinding()]
param(
    [string]$Subject = "CN=EmbeddingSearchViewer Dev",
    [string]$OutDirectory = "$PSScriptRoot\..\certs",
    [string]$PfxName = "dev-cert.pfx",
    [string]$CerName = "dev-cert.cer",
    [string]$Password = "devpassword",
    [int]$ValidYears = 3,
    [switch]$TrustLocally
)

$ErrorActionPreference = 'Stop'

# 出力先ディレクトリの確保
if (-not (Test-Path $OutDirectory)) {
    New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null
}

$pfxPath = Join-Path (Resolve-Path $OutDirectory) $PfxName
$cerPath = Join-Path (Resolve-Path $OutDirectory) $CerName

Write-Host "Creating self-signed code-signing certificate: $Subject" -ForegroundColor Cyan

# 自己署名証明書の作成 (CodeSigningCert)
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -KeyLength 2048 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears($ValidYears)

try {
    # PFX にエクスポート
    $securePassword = ConvertTo-SecureString -String $Password -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null
    Write-Host "Saved PFX to: $pfxPath" -ForegroundColor Green

    # CER (公開鍵) にエクスポート
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    Write-Host "Saved CER to: $cerPath" -ForegroundColor Green

    # ローカル PC の「信頼されたルート証明機関」に登録
    if ($TrustLocally) {
        Write-Host "Importing certificate into Cert:\CurrentUser\Root to trust it locally..." -ForegroundColor Cyan
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
        Write-Host "Successfully trusted the certificate for current user." -ForegroundColor Green
    } else {
        Write-Host "Tip: To trust this certificate on this machine, run:" -ForegroundColor Yellow
        Write-Host "     Import-Certificate -FilePath '$cerPath' -CertStoreLocation 'Cert:\CurrentUser\Root'" -ForegroundColor Gray
    }

    # GitHub Actions 用の Base64 文字列生成の案内
    $base64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($pfxPath))
    Write-Host ""
    Write-Host "=== GitHub Actions Secrets Settings ===" -ForegroundColor Cyan
    Write-Host "GitHub Repository > Settings > Secrets and variables > Actions に以下を設定してください:"
    Write-Host "  Secret Name: CODE_SIGNING_PFX_BASE64" -ForegroundColor White
    Write-Host "  Secret Name: CODE_SIGNING_PASSWORD" -ForegroundColor White
    Write-Host "               値: $Password" -ForegroundColor Gray
    Write-Host ""
    Write-Host "※ Base64 文字列（先頭50文字表示）: $($base64.Substring(0, [Math]::Min(50, $base64.Length)))..." -ForegroundColor Gray
    Write-Host "  クリップボードに Base64 文字列をコピーするには以下のコマンドを実行してください:" -ForegroundColor Yellow
    Write-Host "  [Convert]::ToBase64String([System.IO.File]::ReadAllBytes('$pfxPath')) | Set-Clipboard" -ForegroundColor White
}
finally {
    # 証明書ストア(My)に一時登録されたものをクリーンアップ
    Remove-Item -Path $cert.PSPath -Force -ErrorAction SilentlyContinue
}
