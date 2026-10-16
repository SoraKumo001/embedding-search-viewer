# コード署名ガイド (Code Signing Guide)

本プロジェクト（EmbeddingSearchViewer）のバイナリ（`EmbeddingSearchViewer.App.exe` 等）に対するコード署名の設定・運用手順です。

---

## 概要

Windows アプリケーションに Authenticode コード署名を付与することで、改ざん防止および Windows SmartScreen やセキュリティソフトによる警告を低減できます。

本プロジェクトでは以下の 2 つの署名フローに対応しています：

1. **ローカル環境での署名**: 開発用自己署名証明書（または手持ちの証明書）を使用したローカルビルド＆パッケージング
2. **GitHub Actions での署名**: リリースワークフロー（`release.yml`）での自動署名

---

## 1. 開発用自己署名証明書の作成

テスト・開発用の自己署名証明書（`.pfx` / `.cer`）を作成するスクリプトが用意されています。

PowerShell で以下のコマンドを実行します：

```powershell
.\tools\New-DevCertificate.ps1
```

※ ローカル PC で SmartScreen や「信頼できない発行元」の警告を解除したい場合は、`-TrustLocally` を付与して現在のユーザーの「信頼されたルート証明機関」にインポートします：

```powershell
.\tools\New-DevCertificate.ps1 -TrustLocally
```

- **生成場所**: `certs/dev-cert.pfx` および `certs/dev-cert.cer`（`.gitignore` で除外されています）
- **デフォルトパスワード**: `devpassword`

---

## 2. ローカルでの署名とパッケージング

### バイナリへの個別署名

```powershell
.\tools\Sign-Package.ps1 -TargetPath "artifacts/publish/EmbeddingSearchViewer.App.exe" -PfxPath "certs/dev-cert.pfx" -Password "devpassword"
```

※ フォルダを指定すると、その直下およびサブフォルダ内の `.exe` および `EmbeddingSearchViewer*.dll` が一括で署名されます。

### ビルドからパッケージング（zip作成）まで一括実行

```powershell
.\tools\Build-Package.ps1
```

- `certs/dev-cert.pfx` が存在する場合、自動的に署名を行ってから `artifacts/EmbeddingSearchViewer-<version>-win-x64.zip` を作成します。
- 署名をスキップしたい場合は `-SkipSign` を指定します。

---

## 3. GitHub Actions (CI/CD) での署名設定

GitHub Actions のリリースワークフロー（`.github/workflows/release.yml`）は、リポジトリの Secrets に証明書が登録されている場合に自動でコード署名を適用します。

### 設定手順

1. **証明書（PFX）の Base64 文字列を取得**
   PowerShell で以下を実行してクリップボードにコピーします：
   ```powershell
   [Convert]::ToBase64String([System.IO.File]::ReadAllBytes('certs/dev-cert.pfx')) | Set-Clipboard
   ```

2. **GitHub リポジトリの Secrets に登録**
   - GitHub リポジトリの **Settings** > **Secrets and variables** > **Actions** を開く
   - **New repository secret** をクリックして以下を登録：
     - `CODE_SIGNING_PFX_BASE64`: クリップボードにコピーした Base64 文字列
     - `CODE_SIGNING_PASSWORD`: 証明書のパスワード（テスト用なら `devpassword`）

3. **リリースの実行**
   `version.txt` を更新して push するか、Actions タブから手動で Release ワークフロー（dry-run または通常リリース）を実行すると、自動的に署名された exe が zip に同梱されます。
   ※ Secrets が設定されていない場合は、ビルドを中断せずに署名ステップのみスキップされます。

---

## 4. 本番用証明書（公的認証局・商用証明書）への移行

将来、DigiCert / Sectigo などの商用コード署名証明書（PFX）を取得した場合：

- **ローカル**: スクリプトの `-PfxPath` と `-Password` を商用証明書のものに変更して実行します。
- **GitHub Actions**: Secrets の `CODE_SIGNING_PFX_BASE64` と `CODE_SIGNING_PASSWORD` を商用証明書の値に更新するだけで、ワークフロー側の変更なしで本番署名に切り替わります。
