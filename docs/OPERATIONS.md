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
| 配布物 | `EmbeddingSearchViewer-<version>-win-x64-setup.exe`（インストーラー）、`EmbeddingSearchViewer-<version>-win-x64.zip`（約 164 MB）、`SHA256SUMS.txt` |
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
   - 公開（publish）、フォルダの配置、インストーラーと zip、`SHA256SUMS.txt` の作成
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
| Code Sign | 署名用の証明書（Secrets）があるときだけ、`artifacts/publish` の exe とアプリの DLL に署名する |
| Arrange the package folder | `tools/New-PackageLayout.ps1`。一番上は、`EmbeddingSearchViewer.cmd`（起動用）、`README.md`、`README.en.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`licenses\`。アプリ本体の約 400 ファイルは、`app\` に入る（7 章） |
| Install Inno Setup | `choco install innosetup`（入っていなければ） |
| Build the installer | `tools/Build-Installer.ps1`（`installer/EmbeddingSearchViewer.iss`）。`EmbeddingSearchViewer-<version>-win-x64-setup.exe` を作る |
| Code Sign the installer | 証明書があるときだけ、インストーラーに署名する |
| Package | 配置したフォルダを zip にし、zip とインストーラーの SHA-256 を `SHA256SUMS.txt` に書く |
| Keep the package | ドライランのときだけ、zip とインストーラーを成果物として残す |

**配布物の形**: zip の直下を、短く保つために、`app\` と、起動用の `.cmd` に分けています（約 400 個のファイルの中から、exe を探さなくて済むように）。`.cmd` は、`start "" "%~dp0app\EmbeddingSearchViewer.App.exe"` だけで、一瞬、黒い画面が出ます。インストーラーは、同じフォルダの `app\` の中身を、`{app}` に入れ、README と、ライセンス類も、あわせて入れます。

**インストーラー（Inno Setup）**
- ユーザー単位で、`%LOCALAPPDATA%\Programs\EmbeddingSearchViewer` に入ります（`PrivilegesRequired=lowest`。管理者権限は不要。ダイアログで、全ユーザーを選ぶこともできます）。スタートメニューに登録され（デスクトップは、任意）、「アプリ」から、アンインストールできます。
- **`AppId` の GUID は、変えないでください**（同じ GUID で、上書きの更新になります）。
- アンインストールのとき、`%LOCALAPPDATA%\EmbeddingSearchViewer`（インデックス、履歴、AI モデル）を消すかどうかを、聞きます（既定は「いいえ」。`/SILENT` では聞かず、消しません）。
- スクリプト（`.iss`）は、日本語を含むので、**BOM つきの UTF-8** で保存します（BOM がないと、Inno Setup は、ANSI として読みます）。
- ローカルで作るには、Inno Setup 6 を入れて（`choco install innosetup`）、`tools/Build-Package.ps1` を実行します（入っていなければ、zip だけを作ります）。
- 署名がないと、インストーラーにも、SmartScreen の警告が出ます（README に対処があります）。
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

リリースの zip（の一番上）と、インストーラーには、`THIRD-PARTY-NOTICES.md` と `licenses/` が入ります。

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
| `models\` | モデル（全プロファイルで共通） | 次の起動で、モデルの準備画面になる |
| `profiles.json` | プロファイルの一覧（`Id`、`Name`）と、最後に使ったもの（`CurrentId`） | 壊れている・ないときは、「標準」（`default`）だけになる。各プロファイルのフォルダは消えない |
| `window.json` | メインウィンドウの位置と大きさ（`X`、`Y`、`Width`、`Height`、`IsMaximized`。画面のピクセル）。通常（最大化でない）の状態の値。プロファイルに依存しない | 壊れている・ないときは、標準の大きさ（1280 × 840）で、Windows が決めた位置に開く。次の終了で、また保存される |
| `profiles\<id>\media_index.db`（`-wal`、`-shm`） | インデックス（ベクトルと FTS5） | そのプロファイルのインデックスを作り直す必要がある |
| `profiles\<id>\library.json` | 対象フォルダ、自動更新の設定、インデックスを作ったモデルの識別子 | フォルダの登録がなくなる |
| `profiles\<id>\search_history.json` | 検索履歴 | 履歴が消える |
| `profiles\<id>\excluded_files.json` | 読み込みに失敗し続けて、取り込みの対象から外したファイル（パス、サイズ、更新時刻、理由、時刻） | 外していたファイルを、次回、もう一度、試す（また失敗すれば、また外れる） |
| `profiles\<id>\cache\` | PDF のページ画像、動画のサムネイル（検索結果のプレビューが参照する） | PDF のページと動画のプレビュー・サムネイルが表示できなくなる。該当ファイルのインデックスを作り直す（ライブラリ管理で「クリア」して再作成）まで戻らない |

アプリが動いている間は、`media_index.db` を消さないでください（アプリを終了してから）。

インデックスだけをやり直す場合は、アプリの「ライブラリ管理」の「クリア」を使うのが安全です。

### 9.1 プロファイル（仕事用・趣味用などの切り替え）

**設計**
- プロファイルごとに、DB、設定、履歴、キャッシュを**丸ごと分けています**。1 つの DB に、プロファイルの列を足して絞り込む方式では、絞り込みを 1 か所入れ忘れると、他のプロファイルの内容が検索に混ざります。分ければ、混ざりようがなく、削除も、フォルダごとの削除で済みます。代わりに、ディスクの使用量は、プロファイルの数だけ増えます。
- `ProfileManager`（`Infrastructure/Profiles`）が、一覧、現在のプロファイル、追加、名前の変更、削除、切り替え（`Changed` イベント）、各パスを持ちます。ID は、作成時に決める 12 文字の英数字で、名前を変えても変わりません。名前は、重複できません（大文字小文字を区別しない）。
- 実行中に切り替えるため、DB を持つ側を差し替え可能にしています。`ProfileScopedRepository` が `IMediaRepository` を実装して、現在のプロファイルの `SqliteMediaRepository` に処理を渡します。`LibrarySettingsStore`、`SearchHistoryStore` は、呼ぶたびに、現在のパスを引きます。`IngestionPipelineWorker` のキャッシュのフォルダも、同じです（`cacheDirectoryProvider`）。
- **`DataVersion`**: 検索エンジンは、`DataVersion` をキーにして、メモリ上のスナップショットを使い回します。`ProfileScopedRepository` は、切り替えのたびに増える「世代」を、`DataVersion` の上位に載せています（世代 << 40 + 各 DB の版）。これがないと、切り替え後に、前のプロファイルのスナップショットが、検索に使われます。
- 切り替えのとき、`FolderWatcher` の監視を作り直し、未処理の変更の通知を破棄します（`Refresh(discardPending: true)`）。切り替えたあとに、前のプロファイルの変更が、新しい DB へ書かれないようにするためです。SQLite の接続プールは、切り替えで解放します（削除するプロファイルのフォルダを、消せるようにするため）。
- **インデックス作成中は、切り替え・追加ができません**（`ProfileViewModel` が `IngestionPipelineWorker.IsRunning` を見て断ります。UI も、作成中は無効にします）。実行中のワーカーが、別の DB に書き込むのを避けるためです。

**以前のバージョンからの移行**
- `profiles.json` がないとき（初回の起動）に、`%LOCALAPPDATA%\EmbeddingSearchViewer\` の直下にある `media_index.db`（`-wal`、`-shm`）、`library.json`、`search_history.json`、`cache\` を、`profiles\default\` に移します。2 回目以降は、何もしません。
- **DB は、`-wal`、`-shm` といっしょに移すか、まったく移さないか、のどちらかです**。古いバージョンのアプリが起動したままで、DB を開いていると、移動できません（5 回、0.3 秒おきに試します）。このときは、`IOException`（「既存のインデックスを新しい保存場所へ移動できませんでした...」）を出して起動を止め、何も移さず、`profiles.json` も作りません。止めない場合、「標準」が空の DB で始まって、既存のインデックスが、なくなったように見えます。
- 古いバージョンが動いている間に、新しいバージョンを起動すると、起こります。開発中に、実際に起きました（`-wal` と `-shm` だけが移って、DB が残りました）。起きたら、すべてのウィンドウを閉じてから、もう一度起動します。それでも直らないときは、`profiles\default\` の空の DB（`media_index.db*`）を消して、元の `media_index.db` を、そこへ移します。
- `library.json`、`search_history.json`、`cache\` の移動に失敗しても、起動は止めません（その場に残り、「標準」は、それらなしで始まります）。

**検証の範囲**
- `ProfileTests`（移行、ロックされた DB、追加・名前の変更・切り替え・削除、壊れた一覧、ストアの切り替え、`DataVersion`、削除）で確認しています。
- 切り替えの画面（コンボボックス、追加・名前の変更・削除のダイアログ）は、起動までは確認しましたが、実際に操作しての確認は、していません。リリースの前に、ドライランではなく、手元で操作して確認してください。

### 9.2 ウィンドウの位置と大きさ

- 終了時（`AppWindow.Closing`）に、通常の状態の位置と大きさを、`window.json` に保存します。最大化中は、最大化する前の値（`AppWindow.Changed` で、通常の状態のときに記録）を保存し、`IsMaximized` を立てます。最小化中の座標（-32000）は、記録しません。
- 起動時（`MainWindow` の生成時）に、`WindowPlacementValidator.Validate` で、保存された値を、いまのモニターの作業領域（`DisplayArea.FindAll()` の `WorkArea`）と照らします。**次のどれかなら、保存値を捨てて、標準（1280 × 840、位置は Windows 任せ）にします**。
  - 幅 640 未満、または、高さ 400 未満。
  - すべてのモニターを合わせた領域より、64 ピクセル以上、大きい。
  - タイトルバー（上の 32 ピクセル）のうち、どれかのモニターに乗っている部分が、幅 160 ピクセル未満、または、高さ 24 ピクセル未満（つかんで動かせない）。
- `DisplayArea.FindAll()` が返す一覧は、WinRT のベクターなので、LINQ を使わず、添字で読みます（LINQ を使うと、起動時に、`E_NOINTERFACE`（0xC000027B）で落ちました）。
- 検証: `WindowPlacementTests`（判定とストア）と、実際のアプリを起動して、ウィンドウを動かし、閉じ、開き直して確かめました（移動・サイズ変更・最大化の保存と復元、画面外・極端な大きさ・壊れたファイルでの標準への復帰）。複数モニターと、実行の間に変わる表示倍率（DPI）は、実機では確かめていません。

---

### 9.3 読めない画像の除外

- 画像は、16 枚ずつ、まとめて埋め込みます。まとめた処理が失敗すると、`ImageMediaIngestor` は、1 枚ずつやり直します。ファイルの読み込みに関する失敗（`IOException`、`UnauthorizedAccessException`、見つからない、など）は、最大 3 回まで、待ち時間を伸ばしながら（既定 300 ms、600 ms）、試します。壊れた画像（`InvalidDataException` など）は、やり直しません。
- それでも失敗した画像は、`ImageBatchResult.Failed` に入り、`IngestionPipelineWorker` が、保存せずに、`IndexExclusionStore` に記録します（パス、サイズ、更新時刻、理由、時刻）。保存先は、`profiles\<id>\excluded_files.json` です。
- 次の取り込みから、パスと、サイズと、更新時刻が、すべて同じ画像は、読み込みを試さずに、飛ばします（進み具合には、数えます。完了のメッセージに、件数を出します）。**サイズか更新時刻が変わった（直した、置き換えた）画像は、自動で、対象に戻ります**。
- 一時的な失敗（長いロックなど）で、正常な画像を外してしまうことがあるので、「ライブラリ管理」の「インデックスから外したファイル」から、1 件ずつ、またはまとめて、対象に戻せます。
- 対象は、画像だけです。PDF と動画は、読めなくても、ファイル名で検索できる形で、取り込まれます（外しません）。
- 「クリア」（インデックスを消す）は、この一覧を消しません。

### 9.4 表示言語（日本語 / English）

**仕組み**
- 文言は、`src/EmbeddingSearchViewer.Core/Resources/Strings.resx`（日本語。既定の言語）と `Strings.en.resx`（English）にあります。ある言語に文言がないときは、日本語に戻ります。コードからは、`Loc.T("キー", 引数...)`（`Core/Localization/Loc.cs`）で取り出します（書式は、言語の文化圏で整形します。`{0:N0}` なら、英語は `1,234`）。キーがないときは、`[キー]` と表示されて、気づけます。
- XAML は、`x:Uid` ではなく、添付プロパティ `loc:L.Uid="Search.TextBlock01"`（`App/Localization/L.cs`）を使います。`<Uid>.<プロパティ名>` という形のキー（`Search.TextBlock01.Text`、`....Content`、`....Header`、`....PlaceholderText`、`....ToolTip`、`....OnContent`、`....OffContent`、`....Title`、`....Message`）の値を、その要素のプロパティに設定します（`x:Uid` と `.resw` は、配布が展開型（unpackaged）のため、言語の切り替えに制約があるので、避けました）。
- 言語の選択は、`%LOCALAPPDATA%\EmbeddingSearchViewer\settings.json` の `Language`（`auto` / `ja` / `en`。`AppSettingsStore`）です。`App` のコンストラクターの先頭で、`Loc.SetLanguage` を呼びます。`auto` は、Windows の表示言語が日本語なら日本語、それ以外は English です。**変更は、再起動で反映**します（実行中に切り替えると、すでに作った文字列が、変わらないため）。設定画面の「今すぐ再起動」は、新しいプロセスを起動して、自分を終了します。
- 起動前（またはテスト）の言語は、**日本語**です。OS の言語には、依存しません。だから、日本語の文言を確かめている既存のテストは、どの環境でも通ります。テストで、別の言語を使うときは、`using (Loc.UseLanguage("en")) { ... }`（その呼び出しの流れの中だけに効く `AsyncLocal`）を使います。並列に動いているほかのテストには、影響しません。

**文言を足す・直す**
1. `Strings.resx`（日本語）と `Strings.en.resx`（English）の、**両方**に、同じキーで書きます。プレースホルダー（`{0}`、`{1:N0}`）は、両方で同じ番号にします。
2. XAML なら、要素に `loc:L.Uid="ページ.名前"` を付け、`ページ.名前.プロパティ` のキーを足します。ファイルの先頭の `xmlns:loc="using:EmbeddingSearchViewer_App.Localization"` も、必要です。
3. システムのコントロールの既定の文言（`ToggleSwitch` の「オン / オフ」など）は、Windows の言語で出ます。気になる場合は、`OnContent` と `OffContent` を、キーで指定してください。
4. `LocalizationTests` が、次を確かめます: 日本語と English のキーが、過不足なく揃っている / プレースホルダーが同じ / English に、日本語が残っていない（`Settings.LanguageTitle.Text` は、意図して両方）/ 翻訳が、日本語のままではない / コードの `Loc.T("...")` と、XAML の `loc:L.Uid` のキーが、実在する / どの XAML のキーも、使われている。

**言語を足す**: `Strings.<言語コード>.resx` を足し、`Loc.Supported` と `Resolve`、設定画面の一覧（`SettingsPage.xaml` の `ComboBox`、`SettingsViewModel.LanguageSettings`）に加えます。

**そのままにしているもの**: ログ、例外の内部メッセージ（開発者向け）、運用ノート（日本語）、`AutomationProperties.Name`（すでに English）。プロファイルの名前は、データなので、翻訳されません（最初に作る「標準」だけが、最初の起動時の言語で、`Default` になります）。

**検証の範囲**: 日本語と English で、アプリを起動し、すべてのページを開いて、English の見た目と、文字の欠けがないことを、画面で確かめました。表示が長くなる箇所（ボタン、ダイアログ）の崩れは、ウィンドウの幅によって、変わるので、リリース前に、狭い幅でも見てください。

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

### 10.4.1 動画のインデックス

動画は、フレームの取り出し（CPU でのデコード）が時間の大半を占め、GPU の埋め込みは短い時間です。そこで、次の 2 つで、待ち時間を重ねています。

- **取り出しと埋め込みの重ね合わせ**: デコーダーが 1 場面のフレームを書き出すたびに通知し（`IStreamingVideoSegmentExtractor`）、`VideoMediaIngestor` が、8 場面ずつ、埋め込みに回します。チャンクは、場面の順に並び、取り出し終えてから埋め込む場合と、同じ結果になります。
- **動画の並列処理**: `IngestionPipelineWorker` が、最大 3 本（`DefaultVideoParallelism`）を同時に処理します。保存は、ロックで 1 件ずつです。インデックス作成中は、画面の最後の通知に、フレームの取り出しと埋め込みの時間（`VideoStats`）が表示されます。

計測（動画 4 本の先頭 8 分ずつ、各 40 場面、取り出しは Media Foundation）:

| 構成 | 全体の時間 |
|---|---|
| 従来（1 本ずつ、取り出してから埋め込む） | 107.8 秒 |
| 重ね合わせのみ | 87.6 秒 |
| 重ね合わせ + 3 本並列 | 56 秒（約 1.9 倍） |

**途中からの再開**: 終わった場面の埋め込みは、`cache\video\<キー>\checkpoint.bin` に、順に追記されます（場面の番号と、埋め込みのベクトル。白紙のフレームは、長さ 0）。キーは、動画のパス・サイズ・更新時刻・場面の長さ・重なり・埋め込みの次元・モデルから作るので、どれかが変わると、別の動画として、最初からやり直します。次の取り込みでは、終わった場面のフレームは、デコードも埋め込みもせず、残りだけを処理します。動画が完了すると、`checkpoint.bin` は削除され、フレーム（JPEG）は、プレビュー用に残ります。書き込みの途中で落ちたレコードは、読み飛ばされます。フレームのファイルが消えた場面は、やり直します。放棄された動画の `cache\video\<キー>\` は、自動では消えません。

**進み具合の表示**: 抽出器が、1 場面ごとに「済んだ数 / 全体の数」を報告し、`IngestionPipelineWorker` が、0.5 秒に 1 回まで、`IngestionProgress.Detail` として、画面に渡します。

**場面の長さ**: `library.json` の `VideoSegmentSeconds`（既定 15。画面で、15・30・60・120 から選べる）。重なりは、`min(3, 長さ / 4)` 秒です。変更は、これから取り込む動画にだけ適用され、取り込み済みの動画は、「クリア」するまで、そのままです。

従来の内訳は、取り出し 約 85 秒、埋め込み 約 23 秒です。取り出しが、まだ支配的なので、これ以上は、デコードを軽くする（`maxFrameSkip`、縮小の位置）必要があります。ただし、場面の位置や画質が変わるので、検索結果への影響を確認してから行ってください。

### 10.4.2 動画の検索結果のまとめ方

- `SearchQuery.GroupVideoSegments`（画面の「動画を 1 件にまとめる」、既定はオン）が真のとき、`HybridSearchEngine` は、類似度の計算と、同一埋め込みのまとめ（`CollapseDuplicates`）のあとで、同じ動画の場面を 1 件にします（`GroupByVideo`）。代表は、最も順位の高い場面で、まとめた結果は、その場面の順位に置かれます。代表の `SearchResultItem.Scenes` に、その動画の一致した場面が、順位の高い順に入ります（代表自身を含む）。画像と PDF は、そのままです。
- 候補は、最小類似度を超えたものが、すべて対象です（上限の `TopK` は、まとめた**あと**に適用）。だから、長い動画 1 本が、上位 50 件を埋めても、ほかの動画は、結果に出ます。
- `SearchQuery.AssetIdFilter` は、その動画のチャンクだけを対象にします（動画内の検索。`PassesFilters` で、ほかの絞り込みと同じ扱い）。`SearchViewModel.SearchInVideoAsync` が、最小類似度などは、メインの検索と同じ値を使って、最大 200 場面を返します。
- 結果を、まとめない形に戻すと、場面の一覧は、その検索結果のうち、同じ動画のものから作ります。

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
| 起動時に「既存のインデックスを新しい保存場所へ移動できませんでした」と出る | 古いバージョンのアプリが、起動したまま、DB を開いている | すべてのウィンドウを閉じてから、起動し直す（9.1） |
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
