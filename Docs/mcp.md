# AvaAvaDance の MCP 操作

## 接続

Windows で `uv --version` が通ることを確認します。初回接続時に uv が Python と MCP SDK などの依存パッケージを準備します。[uv のインストール](https://docs.astral.sh/uv/getting-started/installation/)

**ソースから使う場合**はリポジトリのルートで Codex または Claude Code を起動します。Codex は `.codex/config.toml`、Claude Code は `.mcp.json` を読みます。クライアントがプロジェクトの信頼や MCP の承認を求めたら、表示された `avaavadance` を確認します。登録は `codex mcp get avaavadance` または `claude mcp get avaavadance` で確認できます。

**Windows 配布版を別のプロジェクトから使う場合**は、利用したいプロジェクトのディレクトリで次の該当するコマンドを実行します。パスは実際のインストール先に合わせてください。

```powershell
codex mcp add avaavadance -- uv run --script "D:\Apps\AvaAvaDance\Mcp\ava_mcp.py"
claude mcp add --scope local --transport stdio avaavadance -- uv run --script "D:\Apps\AvaAvaDance\Mcp\ava_mcp.py"
```

**操作対象**は通常起動した Windows プレイヤー、またはこの Unity プロジェクトの Play Mode です。実行中のアプリが1つなら最初の操作で接続します。`ava_instances` で一覧を確認し、複数あれば `ava_connect(pid)` で選びます。`ava_launch` は新しいプレイヤーを起動し、そのインスタンスに接続します。再起動後は新しい PID を選びます。Codex と Claude Code は同じプレイヤーを選べます。

## できる操作

| ツール | 用途 |
| --- | --- |
| `ava_commands`、`ava_command` | 現在登録されている全メニューを列挙して実行。コアと拡張の ID、日本語名・英語名、有効状態、チェック状態を取得 |
| `ava_state` | `summary` はフレーム、選択、再生、処理中フラグ、ダイアログ名、ステータス。ほかに `document`（未保存変更）、`scene`、`timeline`、`audio`、`render`、`physics`、`extensions`、`materials`、`rig`、`project` |
| `ava_tracks`、`ava_keys`、`ava_sample`、`ava_edit_keys` | ボーン・モーフ・モデル表示/IK・カメラ・照明・重力・影・アクセサリのトラック、キー、補間値の読取と編集 |
| `ava_transport`、`ava_select`、`ava_order` | フレーム移動、再生・停止、Undo/Redo、モデル・トラック選択、描画順・物理計算順の変更 |
| `ava_ui`、`ava_control`、`ava_input` | ボタン、ドロップダウン、数値欄、確認画面、リボン、タイムライン、ビューポートを操作。フローティングパネルも対象 |
| `ava_preview` | `surface` に `camera`、`workspace`、または開いている `operations`、`timeline`、`audio` を指定して PNG を取得 |
| `ava_native_ui`、`ava_native_control`、`ava_native_input`、`ava_native_image` | VMD サイジングやエフェクト設定などの Windows ウィンドウを列挙し、項目の読取、選択、入力、クリック、画像取得 |

`ava_state(section="project")` はプロジェクトの保存形式に沿った JSON を返します。モデルや音声のバイナリを含めて復元したいときは `core.SaveAs` で `.avadance` を保存します。大きい作品は `pointer="/sceneMotion/tracks"` のように JSON Pointer で一部分を取得できます。配列は `offset` と `limit` で分割でき、`nextOffset` があれば次の呼び出しの `offset` に渡します。

## 操作例

**カメラキーを追加する。** `ava_tracks(owner="scene")` から `channel="Camera"` のトラック ID を取得します。`ava_keys` から `document`、`revision`、`current` を読み、`current` をコピーして `Distance` を変更します。`ava_edit_keys` に次の形で渡します。ベクトルや回転を含む値は、読み取ったオブジェクトの対応するフィールドを変更します。

```text
document = ava_keys が返した document
edits = [{Owner: "scene", Track: トラックID, Revision: revision,
          Frame: 30, Value: 変更したcurrent}]
```

編集後は `ava_sample` で値を読み戻し、`ava_transport(action="seek", frame=30)` と `ava_preview` で見た目を確認できます。一括編集は一度の Undo で戻せます。他のクライアントや手操作でリビジョンが変わった場合は、キーを読み直してから再編集します。

**モデルを読み込む。** `ava_ui(query="unitypackage")` でモデル読込ボタンの ID を探し、`ava_control(action="activate", id=…, paths=[絶対パス])` で押します。パッケージの検査中は `summary.importing` が真です。検査後に `ava_ui` で候補・利用確認を読み、対象を選び「読み込む」を押します。結果は `scene`、`materials`、対象モデルの `rig` で確認できます。

**作品を保存し、動画を出力する。** `core.SaveAs` に保存先の絶対パスを渡します。`core.ExportMp4` にも出力先を渡し、表示された設定画面を `ava_ui` で読み、FPS・開始終了フレーム・音声を設定して「OK」を押します。`summary.exporting` が偽に戻ったときの `exportError` と `status` で結果を確認できます。

**カスタム描画部分を操作する。** `ava_ui(all_elements=true)` で描画領域の ID と bounds を取得します。`ava_input` の座標は対象要素の左上からのパネル単位です。ドラッグは `down → drag → up` の順に送ります。操作後は `ava_preview(surface="workspace")` で画面を確認できます。Editor で画面全体を取得するときは Game ビューが開きます。

**拡張の別窓を操作する。** 例えば `ava_command(id="ava.vmd-sizing.open")` の後に `ava_native_ui()` を呼びます。返された `window` を指定して再度呼ぶとコントロール ID、現在値、対応する操作が返ります。`ava_native_control` の `set` で値を変更し、同じウィンドウを再取得すると結果を確認できます。

## ダイアログとファイル

`paths` はその操作が開くファイル選択に順番に渡す絶対パスです。未保存の作品を開き直すと、先に保存確認が表示される場合があります。`unusedPaths` はそのステップで使われなかったパスです。確認画面を読んで、続行するボタンの `ava_control` に必要なパスを渡します。読込・動画出力などの非同期処理は呼び出し後も継続するため、完了は状態を再取得して確認します。

クライアントと MCP サーバーは標準入出力で通信します。アプリ側は `127.0.0.1` の一時ポートを使用し、起動ごとの接続情報を Windows ユーザーの `%LOCALAPPDATA%\AvaAvaDance\Mcp` で共有します。状態変更は Unity のメインスレッドで実行します。接続機能を止めてプレイヤーを起動するには `-avaNoMcp` を指定してください。[MCP の標準入出力仕様](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)

## 実機テスト

Windows プレイヤーをビルドしてから `uv run --script Tests/test_ava_mcp.py` を実行します。テスト専用のプレイヤーと一時プロジェクトを使い、MCP クライアントから編集と読み戻し、読込・出力、別窓操作を確認します。
