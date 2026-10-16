# Embedding Search Viewer

ローカル AI モデル **EmbeddingGemma 2** を、**ONNX Runtime + DirectML** で PC 内の GPU 上で動かす、マルチモーダル（画像・PDF・動画・テキスト）ハイブリッド検索の Windows デスクトップアプリケーションです。

---

## 主な特徴 (Features)

- **完全ローカル & プライバシー保護**
  - 推論はすべて PC 内で実行します。クラウドや外部 API は使いません（通信するのは、初回のモデルのダウンロードだけです）。
- **マルチモーダル・ハイブリッド検索**
  - **自然言語検索**: 「赤いスポーツカー」「グラフのあるスライド」などの文章で、画像・PDF のページ・動画の場面を横断して検索します。
  - **画像による類似検索**: 画像を指定（選択・ドラッグ&ドロップ・結果の右クリック）して、似た画像を検索します。
  - **ハイブリッド**: ベクトル類似度（コサイン類似度）と、SQLite FTS5 のキーワード全文検索を、RRF (Reciprocal Rank Fusion) で統合します。
  - **絞り込みと並べ替え**: 最小類似度、期間、サイズ、フォルダ、メディア種別、並べ替え（関連度 / 類似度 / ファイル名）、検索履歴。
- **多様なメディア対応**
  - 📷 **静止画**: JPEG / PNG / WebP / BMP。EXIF の向きを反映します。ほぼ一色の画像（白紙など）はインデックスしません。
  - 📄 **PDF**: ページごとに、画像（150 DPI）とテキスト層を埋め込みます（Windows 標準の PDF レンダラ + PdfPig）。
  - 🎥 **動画**: 15 秒ごと（3 秒の重なり）に、区間の中央のフレームを埋め込みます。FFmpeg（webm / mkv / flv / HEVC なども可）を優先し、読めなければ Windows 標準の Media Foundation を使います。再生時は、一致した場面へジャンプします。
- **ライブラリ管理**
  - フォルダの追加（ドラッグ&ドロップ可）、増分インデックス、フォルダ監視による自動更新、削除済みファイルの除去、白紙画像の除去、インデックスのクリア。
  - 同一の埋め込みを持つ画像は、検索結果で 1 件にまとめ、「同一画像 他 N 件」と表示します。
- **WinUI 3 の UI**
  - プレビューは、ホイールでズーム、ドラッグで移動、ダブルクリックで拡大の切り替えができます。
  - 検索結果の右クリックメニュー（外部アプリで開く / フォルダを開く / パスをコピー / この画像に似たものを検索）。

---

## アーキテクチャ (Architecture)

```
embedding-search-viewer/
├── src/
│   ├── EmbeddingSearchViewer.App/             # WinUI 3 の UI (Views, Controls, ViewModels)、FFmpeg フレーム取得
│   ├── EmbeddingSearchViewer.Core/            # ドメインモデル、ハイブリッド検索エンジン、インターフェース
│   └── EmbeddingSearchViewer.Infrastructure/  # ONNX 推論、前処理、取り込みパイプライン、SQLite、モデルのダウンロード
├── tests/
│   └── EmbeddingSearchViewer.Tests/           # xUnit（単体テスト + モデルがあるときだけ動く統合テスト）
├── models/                                    # （任意・開発用）モデルの置き場所。.gitignore 済み
└── sidecar/                                   # 旧 Python サイドカー。現在のアプリは使用しません
```

### 主要コンポーネント

| モジュール | 役割 |
|---|---|
| **OnnxDirectMlEmbeddingService** | テキストと画像の埋め込み。ビジョンエンコーダと、画像用の言語モデルは GPU（DirectML）、短いテキストのクエリは CPU で実行します。GPU が失敗したときは CPU に切り替えます。MRL（768 / 512 / 256 / 128 次元）に対応。 |
| **Gemma4ImagePreprocessor** | 公式実装（transformers.js）から移植した画像の前処理（リサイズ、16×16 パッチ、位置 ID）と、EXIF の向き、白紙の判定。 |
| **OnnxModelPatches** | 配布されている ONNX を DirectML で全面的に GPU 実行できるよう、グラフを直したコピーを作ります（`allowzero` と `Gelu` → `FastGelu`）。元のファイルは変更しません。 |
| **EmbeddingServiceHost / ModelDownloader** | モデルがないときも起動し、ダウンロード完了後に再起動なしで切り替えます。ダウンロードは、固定リビジョン、SHA-256 検証、Range による再開に対応します。 |
| **IngestionPipelineWorker** | フォルダのスキャン、メディア別の取り込み、画像の並行処理、DB 保存を行うバックグラウンドのワーカー。 |
| **FolderWatcher** | フォルダを監視し、追加・変更・削除をインデックスに反映します（既定はオフ）。 |
| **HybridSearchEngine** | ベクトル + FTS5 を RRF で統合。インデックスのメモリキャッシュ、メタデータの絞り込み、同一埋め込みの集約。 |
| **SqliteMediaRepository** | WAL モードの SQLite。FTS5 の仮想テーブルを含みます。 |

### 保存先

すべて `%LOCALAPPDATA%\EmbeddingSearchViewer\` 以下です。

| パス | 内容 |
|---|---|
| `models\embeddinggemma-2-ONNX\` | モデル（fp32 で約 1.76 GB） |
| `media_index.db` | インデックス（ベクトルと FTS5） |
| `library.json` | 対象フォルダ、自動更新の設定、インデックスを作ったモデルの識別子 |
| `search_history.json` | 検索履歴 |
| `cache\` | PDF のページ画像、動画のサムネイル |

---

## モデル (Model)

- 使用モデル: [`onnx-community/embeddinggemma-2-ONNX`](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX)（Apache-2.0）
- **初回起動時に、アプリの「モデル」画面からダウンロード**します（進捗表示、中断・再開、チェックサム検証つき）。
- 手動で配置する場合は、上の保存先と同じ構成（`onnx/model.onnx`、`onnx/model.onnx_data`、`onnx/vision_encoder.onnx`、`onnx/vision_encoder.onnx_data`、`tokenizer.json`）で置いてください。開発用に、リポジトリの `models/embeddinggemma-2-ONNX/` でも探します。
- モデルを変更したときは、インデックスを作り直す必要があります。アプリは、別のモデルで作ったインデックスを検出して警告します。

---

## 動作要件 (Prerequisites)

- **OS**: Windows 11 または Windows 10（1809 以降、x64）
- **ランタイム / SDK**: .NET 11 SDK（または最新のプレビュー版）、Windows App SDK
- **ハードウェア**: DirectML 対応の GPU を推奨（開発・計測は NVIDIA GeForce RTX 4070 Ti）。GPU がなくても CPU で動作しますが、画像の取り込みは遅くなります。
- **ディスク**: モデル用に約 2 GB

---

## ビルドと実行 (Getting Started)

```powershell
# ビルド（ソリューション全体）
dotnet build EmbeddingSearchViewer.slnx

# テスト
dotnet test tests/EmbeddingSearchViewer.Tests/EmbeddingSearchViewer.Tests.csproj

# 起動（ビルド後の実行ファイル）
.\src\EmbeddingSearchViewer.App\bin\x64\Debug\net11.0-windows10.0.26100.0\win-x64\EmbeddingSearchViewer.App.exe
```

- モデルがインストールされていない環境では、モデルが必要な統合テストは自動的にスキップされ、そのほかのテストが実行されます。
- Visual Studio で開く場合は、`EmbeddingSearchViewer.slnx` を開き、プラットフォームに **x64**（または Any CPU）を選びます。

---

## 使い方 (Usage)

1. **モデルの準備**: 初回は「モデル」画面に切り替わります。「ダウンロードを開始」をクリックします（約 1.76 GB）。
2. **メディアフォルダの追加とインデックス**:
   - 「**ライブラリ管理**」を開き、「フォルダを追加」（またはドラッグ&ドロップ）で、画像・PDF・動画のあるフォルダを登録します。
   - 「インデックス作成を開始」をクリックします。2 回目以降は、変更されたファイルだけが処理されます。
   - 「フォルダを監視して自動でインデックスを更新」をオンにすると、追加・変更・削除が自動で反映されます。
3. **検索とプレビュー**:
   - 「**検索 & ビューア**」を開き、検索ボックスに文章を入力して Enter を押します（時計のアイコンから検索履歴）。
   - 画像で検索するときは、「画像で検索」をクリックするか、画像をウィンドウにドラッグ&ドロップします。
   - 左の結果をクリックすると、右にプレビューが表示されます。

---

## 性能の目安 (Performance)

RTX 4070 Ti、CPU 24 スレッドでの計測値です（環境やデータで変わります）。

| 処理 | 時間 |
|---|---|
| 画像 967 枚（漫画のスキャン、最大 4,300×6,000 px 程度）のインデックス | 約 155 秒（1 枚あたり約 145 ms、GPU 使用率は平均 87%） |
| PDF 349 ページ（画像 + テキスト）のインデックス | 約 109 秒 |
| 検索（テキスト） | 約 50〜125 ms |

実装の詳細（前処理の移植、DirectML で動かすためのグラフの修正、計測方法）は、技術記事にまとめています: `embedding-gemma-2-onnx.md`（Zenn）。

---

## 既知の制限 (Limitations)

- 対象の画像形式は、JPEG / PNG / WebP / BMP です（GIF / HEIC / AVIF は未対応）。
- パスワード付きの PDF は、ファイル名だけでインデックスします。
- アーカイブ（zip / cbz など）の中身は、インデックスの対象外です。
- 検索精度の定量的な評価は、まだ行っていません。

---

## リリース (Release)

バージョンは **`version.txt` だけ**で管理します（アセンブリのバージョンと、設定画面の表示にも使われます）。リリースは GitHub Actions（`.github/workflows/release.yml`）が自動で行います。

1. `version.txt` を書き換えます（例: `0.2.0`、プレリリースは `0.2.0-beta.1`）。
2. コミットして、既定ブランチ（`main` / `master`）に push します。
3. そのバージョンがまだリリースされていなければ、ワークフローが次を行います。
   - ビルドとテスト
   - タグ `v0.2.0` の作成
   - アプリの公開（`win-x64`、自己完結）と、zip + `SHA256SUMS.txt` の作成
   - GitHub のリリースへの登録（変更点のノートは自動生成。`-` を含むバージョンはプレリリース）

すでにタグかリリースがあるバージョンは、何もせずに終了します。別のバージョンを出したいときは、`version.txt` を更新してください。

**初回の動作確認**: GitHub の Actions タブで「Release」を手動実行（Run workflow）し、`dry_run` にチェックを入れると、タグもリリースも作らずに、ビルドからパッケージまでを確認できます（zip は実行結果の成果物として 7 日間残ります）。

**メモ**
- zip には、モデルは含まれません。初回起動時にアプリ内でダウンロードします。
- zip には、サードパーティのライセンス（`THIRD-PARTY-NOTICES.md` と `licenses/`）が入ります。ビルド時の依存関係から `tools/New-ThirdPartyNotices.ps1` が自動で作るので、手で更新する必要はありません（FFmpeg は LGPL のため、ライセンス文を DLL と一緒に配布します）。
- 公開物は IL トリミングなし、`EmbeddingSearchViewer.App.pri` と XAML（`*.xbf`）を含めた構成です（`EmbeddingSearchViewer.App.csproj` に理由を記載）。
- リポジトリの Settings > Actions > General > Workflow permissions が読み取り専用に制限されていても、ワークフロー側で `contents: write` を指定しているため動作します（組織のポリシーで禁止されている場合を除く）。

---

## ライセンス (License)

本プロジェクトはプライベート開発プロジェクトです。
利用しているコンポーネントのライセンス条件に準拠します（ONNX Runtime、Windows App SDK、SQLite、SkiaSharp、PdfPig、FFmpegInteropX / FFmpeg（LGPL）、Hugging Face のモデル（Apache-2.0））。
