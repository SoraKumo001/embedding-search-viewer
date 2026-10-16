# Microsoft Store 登録・申請ガイド

本プロジェクト（EmbeddingSearchViewer）を Microsoft Store（Partner Center）に登録・公開する手順です。

---

## 概要

Microsoft Store への提出には **MSIX パッケージ（推奨）** を使用します。
MSIX パッケージで提出する場合：
- Microsoft Store 側で暗号化・公的署名が行われるため、**自前での商用コード署名証明書（EV証明書等）の購入は不要**です。
- ユーザーは Microsoft Store アプリから安全にインストール・アンインストールでき、自動更新も Store 経由で行われます。

---

## 手順 1: Partner Center で「アプリ ID」を確認する

すでに Partner Center アカウントでアプリ名（予約済みアプリ）を作成されている場合、以下の手順でアプリ固有のパッケージ情報を取得します。

1. [Microsoft Partner Center (ダッシュボード)](https://partner.microsoft.com/dashboard/) にサインインします。
2. 左側メニューの **「Windows & Xbox」** > **「アプリとゲーム」** から、予約したアプリを選択します。
3. 左側メニューの **「製品管理 (Product management)」** > **「製品 ID (Product Identity / アプリ ID)」** を開きます。
4. 以下の 4 つの値を手元に控えます：
   - **パッケージ名 (Package Name / Identity Name)**（例: `12345YourName.EmbeddingSearchViewer`）
   - **発行元 ID (Publisher ID)**（例: `CN=12345678-ABCD-EF01-2345-6789ABCDEF01`）
   - **パッケージ表示名 (Package Display Name)**（例: `EmbeddingSearchViewer`）
   - **発行元表示名 (Publisher Display Name)**（例: `YourOrganization`）

---

## 手順 2: `Package.appxmanifest` を更新する

[`src/EmbeddingSearchViewer.App/Package.appxmanifest`](file:///c:/prog/apps/windows/embedding-search-viewer/src/EmbeddingSearchViewer.App/Package.appxmanifest) を開き、手順 1 で取得した値に書き換えます。

```xml
  <Identity
    Name="<Partner Center の パッケージ名>"
    Publisher="<Partner Center の 発行元 ID (CN=...)>"
    Version="1.0.0.0" />

  <Properties>
    <DisplayName><Partner Center の パッケージ表示名></DisplayName>
    <PublisherDisplayName><Partner Center の 発行元表示名></PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    ...
  </Properties>

  ...

  <Applications>
    <Application Id="App"
      Executable="$targetnametoken$.exe"
      EntryPoint="$targetentrypoint$">
      <uap:VisualElements
        DisplayName="<ユーザーに表示されるアプリ名>"
        Description="<アプリの説明>"
        ...
```

> [!TIP]
> **Visual Studio をお使いの場合の自動連携**:
> Visual Studio でソリューションを開き、`EmbeddingSearchViewer.App` プロジェクトを右クリック > **「公開」** > **「アプリ パッケージの作成」** > **「Microsoft Store」** を選択してログインすると、Partner Center と連携して `Package.appxmanifest` が自動的に同期されます。

---

## 手順 3: Store 提出用パッケージ（MSIX）のビルド

PowerShell で以下のスクリプトを実行します：

```powershell
.\tools\Build-Msix.ps1
```

- ビルドが完了すると、`artifacts/msix/` ディレクトリに **`.msixupload`**（または `.msix`）ファイルが生成されます。
- ※ Store 提出用パッケージは未署名（またはビルドツール署名）で問題ありません（Partner Center アップロード時に Store の正式証明書で自動署名されます）。

---

## 手順 4: Partner Center で提出手続きを行う

1. Partner Center のアプリ詳細画面で **「申請の開始 (Start submission)」** をクリックします。
2. 以下の各項目を入力します：
   - **価格と提供状況 (Pricing and availability)**: 無料 / 提供地域 / 提供開始日を設定
   - **プロパティ (Properties)**: カテゴリ（例: ユーティリティ、写真・ビデオ等）を選択
   - **年齢区分 (Age ratings)**: アンケートに回答して IARC 年齢レーティングを取得（AI 機能やファイル検索などの用途を入力）
   - **パッケージ (Packages)**: 手順 3 で生成した `artifacts/msix/` 内の **`.msixupload`**（または `.msix`）ファイルをドラッグ＆ドロップしてアップロード
   - **ストア登録情報 (Store listings)**:
     - アプリの説明文（機能概要、初回起動時にモデルがダウンロードされる旨など）
     - スクリーンショット（1366x768 以上を数枚）
     - アプリアイコン
3. すべての項目にチェックがついたら **「Store に提出 (Submit to the Store)」** をクリックします。

---

## 手順 5: 審査と公開

- 通常、数時間〜数日（1〜3営業日程度）で Microsoft の自動テストおよび担当者による審査が行われます。
- 審査に合格すると、Microsoft Store 上で検索・インストール可能になります。
- 今後のアップデート時は、`version.txt` / `Package.appxmanifest` のバージョンを上げて新しい `.msixupload` を提出するだけで自動更新が配信されます。
