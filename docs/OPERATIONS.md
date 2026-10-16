# 運用ノート (Operations)

開発者・メンテナ向けの文書です。利用者向けの説明は、[README.md](../README.md) にあります。

最終更新: 2026-10-16（バージョン 0.1.0 のリリース時点）

---

## 1. 現状

| 項目 | 内容 |
|---|---|
| リポジトリ | https://github.com/SoraKumo001/embedding-search-viewer（公開） |
| 既定ブランチ | `master` |
| 最新リリース | `v0.1.0`（タグはコミット `ad1948f`） |
| 配布物 | `EmbeddingSearchViewer-<version>-win-x64.zip`（約 164 MB）と `SHA256SUMS.txt` |
| 対応環境 | Windows x64 のみ（ARM64 は未対応。理由は 12 章） |
| ログ | ログファイルはありません（`Console` への出力のみで、GUI では見えません） |

---

## 2. リリース手順

### 2.1 通常のリリース

1. `version.txt` を書き換えます。`0.2.0` のような通常のバージョンと、`0.2.0-beta.1` のようなプレリリース（`-` を含む）が使えます。
2. コミットして、`master` に push します。

```bash
git add version.txt
git commit -m "chore: バージョンを 0.2.0 に更新"
git push
```

3. GitHub Actions の「Release」が起動し、次を自動で行います（所要は約 5 分）。
   - ビルドとテスト
   - タグ `v0.2.0` の作成
   - 公開（publish）、zip と `SHA256SUMS.txt` の作成
   - GitHub のリリースへの登録（変更点のノートは自動生成。`-` を含むバージョンはプレリリースになる）

4. 結果の確認:

```bash
gh release view v0.2.0
```

### 2.2 ドライラン（リリースを作らずに確認する）

ビルド、テスト、パッケージ作成までを実行し、タグもリリースも作りません。zip は、実行結果の成果物として 7 日間残ります。ワークフローを変更したときや、CI の環境が変わったときに使います。

```bash
gh workflow run release.yml -f dry_run=true
```

GitHub の画面では、Actions → Release → Run workflow → `dry_run` にチェックです。実行名に「(dry run)」と表示されます。成果物は、次のコマンドで取得できます。

```bash
gh run download <run-id>
```

### 2.3 手動で本番のリリースを実行する

`version.txt` を変えずに、現在のバージョンでリリースします（まだそのバージョンのタグがない場合だけ動きます）。

```bash
gh workflow run release.yml
```

### 2.4 取り消し・やり直し

```bash
gh release delete v0.1.0 --cleanup-tag --yes
```

リリースとタグを消すと、同じバージョンで、もう一度リリースできます。**タグだけが残っている**と、ワークフローは警告を出して何もしないので、`--cleanup-tag` を付けてください。

### 2.5 同じバージョンは出せない

タグかリリースがあるバージョンは、ワークフローが何もせずに成功で終了します（`version.txt` の無関係な編集で、二重リリースにならないため）。新しいリリースには、必ずバージョンを上げてください。

---

## 3. ワークフローの仕組み

ファイル: `.github/workflows/release.yml`

| 項目 | 内容 |
|---|---|
| トリガ | `master` / `main` への push のうち `version.txt` を含むもの、および手動実行（`workflow_dispatch`、入力は `dry_run`） |
| 実行環境 | `windows-latest`、タイムアウト 60 分 |
| 権限 | `contents: write`（タグとリリースの作成）。リポジトリ側の既定は読み取りだが、ワークフローで指定しているので問題ない |
| 同時実行 | `concurrency: release`（実行中のものは、キャンセルしない） |

| ステップ | 内容 |
|---|---|
| Read and validate the version | `version.txt` を読み、`1.2.3` または `1.2.3-beta.1` の形式かを検証する。空や不正な値は、分かりやすいメッセージで失敗する |
| Skip if this version is already released | タグまたはリリースがあれば、以降のステップをスキップする |
| Set up .NET | `11.0.x`（`dotnet-quality: preview`）。.NET 11 が正式版になったら、`dotnet-quality` の行を削除する |
| Cache NuGet packages | `~/.nuget/packages`。キーは `*.csproj` と `Directory.Build.props` のハッシュ |
| Build | `dotnet build EmbeddingSearchViewer.slnx -c Release` |
| Test | `--filter "Requires!=WindowsMedia"`（下記） |
| Publish | `-c Release -p:Platform=x64 -r win-x64 --self-contained true` |
| Package | フォルダ名にバージョンを入れ、`README.md`、`LICENSE`、サードパーティのライセンス（7 章）を加えて zip にし、SHA-256 を作る |
| Keep the package | ドライランのときだけ、zip を成果物として残す |
| Create the tag and the release | `gh release create`。`--target $GITHUB_SHA` でタグも作る。`-` を含むバージョンは `--prerelease`、そうでなければ `--latest` |

**テストの除外**: `PDF の描画` と `Media Foundation` に依存するテスト（`WindowsMediaExtractionTests`、`[Trait("Requires", "WindowsMedia")]`）は、サーバー用の環境で動かない可能性があるので、CI では除外しています。モデルが必要なテストは、モデルがないと自動でスキップされます（CI の結果: 145 件中、合格 132、スキップ 13、失敗 0）。

### 3.1 初回に分かったこと（同じ状況になったときのために）

- **最初の push で、本番のリリースが走ってしまう**: `version.txt` が含まれる最初の push は、条件に一致します。最初のコミットに `[skip ci]` を付けて、これを避けました。
- **ただし `[skip ci]` の push では、ワークフローが GitHub に認識されません**。手動実行（`gh workflow run`）が `HTTP 404: workflow release.yml not found` になります。**ワークフローのファイルを変更する push** をすると認識されます（ファイルの変更を含まない空のコミットでは、認識されませんでした）。
- 以降のワークフローの変更でも、`version.txt` を含めなければ、リリースは起動しません。

---

## 4. バージョン管理

バージョンの元は **`version.txt` だけ**です。

```
version.txt ──→ Directory.Build.props ──→ 全プロジェクトの Version
            └─→ release.yml（タグ名、zip の名前）
```

- `Directory.Build.props` が、`version.txt` を読み込んで `<Version>` に設定します。
- 設定画面の表示は、アセンブリのバージョンから取ります。リリースしたビルドの製品バージョンは、`0.1.0+<コミットのハッシュ>` のように、コミットが付きます（表示では `+` 以降を除きます）。
- ビルドが古い出力を使っていると、古いバージョンが見えることがあります（`bin/` の古いフォルダに注意）。

---

## 5. ビルド・テスト・実行

```powershell
dotnet build EmbeddingSearchViewer.slnx
dotnet test tests/EmbeddingSearchViewer.Tests/EmbeddingSearchViewer.Tests.csproj
.\src\EmbeddingSearchViewer.App\bin\x64\Debug\net11.0-windows10.0.26100.0\win-x64\EmbeddingSearchViewer.App.exe
```

- 必要なもの: .NET 11 SDK、Windows App SDK（NuGet で復元される）。
- モデルが `%LOCALAPPDATA%\EmbeddingSearchViewer\models\embeddinggemma-2-ONNX\` にあれば、モデルが必要なテスト（13 件）も実行されます。なければ、自動でスキップされます。
- テストプロジェクトは、アプリ（WinUI）を参照できないので、UI に依存しない ViewModel（`SearchViewModel`、`ModelSetupViewModel`）のソースを、リンクして使っています。
- `Infrastructure` と `Tests` は、`net11.0-windows10.0.26100.0` です（Windows の PDF レンダラや Media Foundation を使うため）。

---

## 6. 公開物（publish）の注意点

`EmbeddingSearchViewer.App.csproj` に、理由つきで設定してあります。変更するときは、**必ず公開した版を起動して確認**してください（ビルド出力からの起動では、問題が見えません）。

| 設定 | 理由 |
|---|---|
| `PublishTrimmed=False` | DI、MVVM のソースジェネレータ、JSON、ONNX Runtime、SkiaSharp、WinRT コンポーネントなどがリフレクションに頼っていて、IL トリミングで壊れる恐れがあるため |
| `IncludeWinUIResourcesInPublish` ターゲット | 非パッケージの WinUI 3 を `dotnet publish` すると、コンパイル済み XAML（`*.xbf`）とリソースの索引（`*.pri`）が含まれず、**起動直後に異常終了**する（終了コード `0xC000027B`）。ビルド出力にある両方を、公開物にコピーしている |
| パッケージのバージョンの固定 | `Microsoft.WindowsAppSDK`、`Microsoft.Windows.SDK.BuildTools(.WinApp)` が `Version="*"` だったので固定した（リリースのたびに中身が変わらないように） |
| `PublishReadyToRun=True`（Release） | 起動を速くするため。サイズが大きくなる（展開後 約 470 MB、zip で約 164 MB） |

公開した版の確認方法（手元）:

```powershell
dotnet publish src/EmbeddingSearchViewer.App/EmbeddingSearchViewer.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/publish
.\artifacts\publish\EmbeddingSearchViewer.App.exe
```

`artifacts/` は `.gitignore` 済みです。

---

## 7. サードパーティのライセンス

リリースの zip には、`THIRD-PARTY-NOTICES.md` と `licenses/` が入ります。

- 作成: `tools/New-ThirdPartyNotices.ps1`（ワークフローの Package ステップが呼ぶ）。ビルド後の `obj/project.assets.json` から、実際に使われているパッケージを列挙するので、手で更新する必要はありません。
- 各パッケージのライセンス文を、パッケージから集めます。文書を持たない MIT / Apache-2.0 / BSD-3-Clause のパッケージには、`tools/license-texts/` の標準全文（SPDX の公式テキスト）を添えます。
- **FFmpeg は LGPL-2.1-or-later**です。FFmpeg の DLL は別ファイルとして同梱し、ライセンス文と、配布物に入っている DLL の名前を、ノートに載せています。
- 新しい種類のライセンス（例: ISC、MPL-2.0）のパッケージを追加したときは、`tools/license-texts/` にその全文を置き、スクリプトの正規表現（`MIT|Apache-2\.0|BSD-3-Clause`）に加えてください。そうしないと、そのパッケージには、名前と URL だけが載ります。
- ビルド専用のパッケージ（`Microsoft.Windows.SDK.BuildTools*`）は、スクリプトの `$buildOnly` で除外しています。

手元での確認:

```powershell
pwsh tools/New-ThirdPartyNotices.ps1 -OutputDirectory <出力先フォルダ>
```

---

## 8. モデルの運用

使用モデル: `onnx-community/embeddinggemma-2-ONNX`（Apache-2.0 と表示、gated ではない）。既定の精度は fp32（テキスト 約 1.08 GB + ビジョン 約 0.67 GB）。

### 8.1 配置と取得

| 場所 | 用途 |
|---|---|
| `%LOCALAPPDATA%\EmbeddingSearchViewer\models\embeddinggemma-2-ONNX\` | アプリが、初回にダウンロードして使う場所 |
| `<リポジトリ>\models\embeddinggemma-2-ONNX\` | 開発用の置き場所（`models/` は `.gitignore` 済み）。アプリの親フォルダを辿って探す |

ダウンロードは、`ModelDownloader` が行います。リビジョンの固定、SHA-256 とサイズの検証、`.part` ファイルと Range による再開に対応しています。

### 8.2 モデルのバージョンを上げる手順

1. 新しいコミットのハッシュを決める（Hugging Face のリポジトリの履歴）。
2. `EmbeddingModelLocation.Revision` を、そのハッシュにする。
3. `ModelManifest.cs` の、サイズと SHA-256 を更新する。すべてのファイルに `lfs.sha256` があります（API で確認済み）。

```python
import json, urllib.request
repo, rev = "onnx-community/embeddinggemma-2-ONNX", "<新しいコミットのハッシュ>"
d = json.load(urllib.request.urlopen(f"https://huggingface.co/api/models/{repo}/revision/{rev}?blobs=true"))
for s in d["siblings"]:
    if s["rfilename"].startswith(("onnx/model.", "onnx/vision_encoder.")) or s["rfilename"] == "tokenizer.json":
        print(s["rfilename"], s["size"], s["lfs"]["sha256"])
```

4. **`EmbeddingModelLocation.ModelTag` の値を変える**（またはモデルの識別が変わるようにする）。インデックスは、別のモデルで作った埋め込みと比較できないので、利用者に再作成を求める必要があります。
5. DirectML 用のグラフの修正（10.2）がまだ効くかを確認する。プロファイラで、CPU に回る演算が増えていないかを見る（10.3）。
6. 実際のモデルを使うテストを実行し、公開した版でも確認する。

### 8.3 精度を変える

`EmbeddingModelLocation.DefaultPrecision`（`Fp32` / `Fp16`）で選びます。精度が変わると、埋め込みが微妙に変わるので、`ModelTag` も変わり、インデックスの再作成が必要になります。

CPU での比較（同じ 8 枚の画像と 10 個のテキスト）では、fp16 は fp32 とほぼ同じ（類似度 1.0000）でしたが、Gemma 系の fp16 には、中間値のオーバーフローの報告があるので、既定は fp32 にしています。q8（量子化）は、CPU でも遅く（テキスト 約 600 ms）、精度も下がったので外しました。

---

## 9. データの保存場所と初期化

すべて `%LOCALAPPDATA%\EmbeddingSearchViewer\` 以下です。

| パス | 内容 | 消すと |
|---|---|---|
| `models\` | モデル | 次の起動で、モデルの準備画面になる |
| `media_index.db`（`-wal`、`-shm`） | インデックス（ベクトルと FTS5） | インデックスを作り直す必要がある |
| `library.json` | 対象フォルダ、自動更新の設定、インデックスを作ったモデルの識別子 | フォルダの登録がなくなる |
| `search_history.json` | 検索履歴 | 履歴が消える |
| `cache\` | PDF のページ画像、動画のサムネイル（検索結果のプレビューが参照する） | PDF のページと動画のプレビュー・サムネイルが表示できなくなる。該当ファイルのインデックスを作り直す（ライブラリ管理で「クリア」して再作成）まで戻らない |

アプリが動いている間は、`media_index.db` を消さないでください（アプリを終了してから）。

インデックスだけをやり直す場合は、アプリの「ライブラリ管理」の「クリア」を使うのが安全です。

---

## 10. 性能と GPU（DirectML）

### 10.1 構成

| 処理 | 実行場所 |
|---|---|
| ビジョンエンコーダ | GPU（DirectML） |
| 画像用の言語モデル（約 264 トークン、284 に固定してパディング） | GPU |
| 文書テキストのバッチ（固定形状の 5 種類に振り分け） | GPU（4 件未満は CPU） |
| 短いテキストのクエリ | CPU（約 40 ms） |
| 画像のデコード・リサイズ・パッチ分割、トークナイザー | CPU |

GPU が使えない（作成に失敗、実行中に失敗）ときは、自動で CPU に切り替わります。

### 10.2 配布されている ONNX に対する修正（`OnnxModelPatches`）

元のファイルは変更せず、隣に `*.dml<Version>.onnx` を作って、DirectML のときだけ使います。

| 修正 | 理由 |
|---|---|
| `Reshape` の `allowzero=1` → `0`（128 か所、同じサイズのバイト列の書き換え） | DirectML が `allowzero=1` を受け付けず、ビジョンが `E_INVALIDARG` で失敗するため |
| `Gelu(approximate="tanh")` → `com.microsoft::FastGelu`（protobuf の書き換え） | DirectML が `Gelu` に非対応で CPU に回り、層ごとに GPU↔CPU のコピーが往復して、時間の約 75% を使っていたため |

**修正のロジックを変えたときは、`OnnxModelPatches.Version` を上げてください**（古いコピーが、タイムスタンプで再利用されるのを防ぐため）。

### 10.3 計測のしかた

- **ONNX Runtime のプロファイラ**: `SessionOptions.EnableProfiling = true`、`ProfileOutputPathPrefix` を設定し、`session.EndProfiling()` で JSON を得る。`cat == "Node"` のイベントの `args.provider`、`args.op_name`、`dur`（µs）を集計する。「GPU のはずなのに遅い」ときは、まず、CPU に回っている演算と `MemcpyToHost` / `MemcpyFromHost` を探す。
- **GPU 使用率**: `nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits -lms 150`。

### 10.4 計測値（RTX 4070 Ti、CPU 24 スレッド、ORT 1.24.4 DirectML）

| 処理 | 値 |
|---|---|
| 画像 1 枚（最初の実装、直列） | 842 ms（GPU 使用率 23%） |
| 画像 1 枚（現在、4 並列） | 145 ms（GPU 使用率 87%） |
| 画像 967 枚の再インデックス | 822 秒 → 155 秒 |
| PDF 349 ページ（画像 + テキスト） | 263 秒 → 109 秒（文書テキストの GPU 化後） |
| 文書テキスト（ページ相当、1 件あたり） | CPU 684 ms → GPU 55 ms |

### 10.5 守ること

- **DirectML のセッションは、同時に `Run` しない**（失敗する）。セッションごとにロックがある。
- **トークナイザー（`Tokenizers.DotNet`）は、同時に呼ばない**（プロセスごと異常終了する）。ロックがある。
- **DirectML は、入力の形状が変わるたびに、グラフを準備し直す**（約 300 ms）。GPU に渡す形状は、固定にする。
- 言語モデルは、1 回の実行のトークン数に上限が必要（1 トークンあたり、24 層分のバッファを持つ）。`MaxTokensPerTextRun = 4096`、1 回 32 件まで。

---

## 11. トラブルシューティング

| 症状 | 原因 | 対処 |
|---|---|---|
| 公開した版が、起動直後に終了する（`0xC000027B`） | `.pri` / `.xbf` が公開物にない、または IL トリミングで壊れている | 6 章。`IncludeWinUIResourcesInPublish` と `PublishTrimmed=False` を確認する |
| 手動実行で `workflow release.yml not found` | `[skip ci]` の push で、ワークフローが認識されていない | ワークフローのファイルを変更する push をする（3.1） |
| push しても、リリースが作られない | そのバージョンのタグまたはリリースが、すでにある | `version.txt` を上げる。または 2.4 で消す |
| タグだけが残って、リリースがない | ワークフローが、途中で失敗した | `gh release delete <tag> --cleanup-tag --yes` で、タグを消してから再実行する |
| ビジョンが DirectML で `Reshape` のエラー | `allowzero=1` | `OnnxModelPatches` が効いているか確認する（10.2） |
| GPU 使用率が低い、画像が遅い | CPU に回っている演算がある、または GPU↔CPU のコピー | プロファイラで確認する（10.3） |
| 長い PDF で `bad allocation` | 全ページのテキストを、1 回で実行した | `PlanTextRuns` のトークン予算を確認する（10.5） |
| PDF のページ画像が、環境で 1.25 倍になる | `RenderToStreamAsync` が、表示倍率を掛ける | `WindowsPdfPageExtractor` が、最初のページで測って補正している |
| 検索結果の上位が、同じスコアで並ぶ | 同一の埋め込み（白紙や、同じ画像のコピー） | 白紙は、インデックスしない。同一の埋め込みは、検索結果で 1 件にまとめられる |
| 動画が取り込めない | FFmpeg も Media Foundation も、そのコーデックを読めない | ファイル名だけでインデックスされる。コーデックを確認する |
| 「別の埋め込みモデルで作成されています」と出る | インデックスを作ったモデルと、今のモデルが違う | ライブラリ管理で「クリア」してから、作り直す |

---

## 12. 既知の制限と今後の課題

**制限**
- **ARM64 は未対応**: `Tokenizers.DotNet.runtime.win-x64` しか使っていません（csproj の `Platforms` に ARM64 はありますが、実行できません）。
- 対応する画像形式は、JPEG / PNG / WebP / BMP です（GIF / HEIC / AVIF は未対応）。
- パスワード付きの PDF は、ファイル名だけでインデックスされます。
- アーカイブ（zip / cbz など）の中身は、対象外です。
- アプリのコードの署名はしていません（Windows の SmartScreen の警告が出ることがあります）。
- 検索精度の定量的な評価は、していません。

**課題（優先度の高い順の目安）**
1. 検索精度の評価（正解データを作って、再現率などを測る）
2. ビジョンの fp16 / 解像度の予算（70 / 140 / 280 / 560 / 1120）の、速度と精度のトレードオフの測定
3. 画像形式の追加（`Magick.NET` による GIF / HEIC / AVIF など）、アーカイブ内の画像のインデックス
4. DB のバックアップ・エクスポート
5. `LibraryViewModel` / `SettingsViewModel` のテスト
6. 使っていない旧サイドカー（`sidecar/`、`Infrastructure/Sidecar/`）の整理
7. アプリのコードの署名

---

## 13. 関連ドキュメント

- [README.md](../README.md): 利用者向けの説明
- 技術記事: `embedding-gemma-2-onnx.md`（Zenn。前処理の移植、DirectML のための修正、計測）
- サードパーティのライセンス（`THIRD-PARTY-NOTICES.md`）: リリースの zip に入る。ワークフローが自動で作るので、リポジトリには置かない（7 章）
