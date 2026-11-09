# AvaAvaDance

Unity **2022.3.22f1** / Windows x64 のスタンドアロンアプリ試作。
MikuMikuDance の左タイムライン、右ビューポート、下部操作パネルという配置をもとに、VRChat アバター用の制作 UI を作っています。

## ビルド済みアプリ

配布 ZIP を全て展開し **AvaAvaDance.exe** を実行してください。Unity Editor / VRChat SDK のインストールや、Unity 上での事前変換は不要です。
UnityPlayer.dll / AvaAvaDance_Data / MonoBleedingEdge などは exe と同梱が必要です。「1つのアプリ」であり、1ファイルだけの exe ではありません。

1. 下段「モデル操作」の **読込** で `.unitypackage` を選択。
2. パッケージ内の FBX を選び、そのアバターを本アプリで利用できることを確認して読み込む。
3. ビューポートのドラッグで全体回転、ホイールで距離変更。削除で表示モデルを解除。

**まだ VRChat の完成 prefab を完全再現する製品ではありません。** 初期 UI と、FBX ベースの直接読込経路を検証する試作です。

## 直接読込の対応範囲

- gzip/tar と GUID/pathname をアプリ内で読み取り、FBX を ufbx で解析。
- FBX の階層 / TRS / mesh / normal / UV / 材質スロット / skin / bindpose / 最大4 weight を Unity ランタイムオブジェクトへ再構築。
- FBX 材質名と同名の `.mat` の `_MainTex` / `_BaseMap` GUID を照合し、パッケージ内 PNG を割当て。名前不一致、JPEG/TGA、embedded texture 等は未対応。
- 内蔵の両面プレビュー用シェーダーを使用。VRChat 内と同じ見た目ではありません。
- OS に依存せず日本語を表示できるよう、OFL の M PLUS 1p を同梱。

**未対応:** prefab / prefab variant の組立や衣装設定、Modular Avatar / NDMF、lilToon / Poiyomi、PhysBone、blendshape、Humanoid Avatar 生成、アニメーション取込み、ポーズ編集 UI、キーフレーム保存・再生・書出し、Undo、VMD。
タイムライン操作は現在フレームの UI 選択のみです。未実装のボタンは無効化しています。

## 安全性と利用条件

- アバターの外部送信・元ファイル変更・パッケージ内コード実行はしません。
- C# / DLL / shader source / Editor script をロード・コンパイルしません。外部参照ファイルも読みません。
- tar の絶対パス・traversal・リンク・重複・不正 checksum 等を拒否。1 member 256 MiB / 全展開 1 GiB / 20,000 members 上限。
- FBX にメモリ・頂点・階層・処理時間の制限。PNG は最大4096四方、32枚、合計128 MiBの展開画素上限。
- ネイティブパーサを OS サンドボックスで隔離しているわけではありません。信頼できる入手元のファイルを使用してください。
- アバターごとの他アプリ利用・形式変換等の規約を守ってください。読めることは利用許諾を意味しません。

## ソースから起動 / ビルド

Unity Hub から **2022.3.22f1** で開き、**AvaAvaDance > Open UI prototype** で Play。
Windows ビルドは次の batchmode コマンド、または `AvaAvaDance.Editor.BuildApp.Windows` を実行します。

```powershell
Unity.exe -batchmode -quit -projectPath <repo> -executeMethod AvaAvaDance.Editor.BuildApp.Windows -logFile build.log
```

出力は `Builds/Windows/AvaAvaDance.exe`。Windows Build Support が必要です。既定1280×900、狭い画面ではスクロールします。
`Native/build.ps1` で同梱 native DLL を再生成できます（MSVC x64）。詳細は `Native/README.md`。

## 検証（2026-11-09）

- Unity **2022.3.22f1** で Windows x64 standalone ビルド成功。
- 実際の exe で自作 unitypackage を直接読み、SkinnedMeshRenderer の生成、骨回転による変形、フレーム UI を検査。
- スタンドアロン画面のスクリーンショットを確認。自作の三角形 fixture を使った検証であり、市販アバターの互換性確認ではありません。
- .NET reader の adversarial checks、native FBX fixture tests、静的構造チェックを実施。

```powershell
dotnet run --project Tests/ReaderHarness/ReaderHarness.csproj
powershell -File Native/test.ps1
python Tests/check_project.py
python Tests/make_smoke_package.py
Builds/Windows/AvaAvaDance.exe -avaSmokePackage <repo>/Tests/triangle.unitypackage -avaSmokeOutput <output-dir>
```

## 参考資料・ライセンス

- [MMD 作者配布ページ](https://sites.google.com/view/vpvp/) / [日本語UI参考資料](https://w.atwiki.jp/mmpp/pages/19.html)。古い版の配置資料であり、最新版とのピクセル単位一致は未達です。MMD の素材・コードは同梱していません。
- [Unity 非依存の直接読込実例](https://extractunitypackage.com/unitypackage-to-vrm) / [作者による仕組みの解説](https://zenn.dev/kappatich/articles/722e86b6ed12a4)。当該ツールの実装はコピーしていません。
- [ufbx](https://github.com/ufbx/ufbx) v0.21.3、MIT 選択。`Native/LICENSE` 参照。
- [M PLUS 1p](https://github.com/google/fonts/tree/main/ofl/mplus1p)、OFL。`ThirdPartyNotices/MPLUS1p-OFL.txt` 参照。

配布時はこれらのライセンスを同梱してください。
