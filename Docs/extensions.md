# 拡張の作成

Windows 版は起動時に、実行ファイルと同じ階層の `Extensions` を読みます。拡張ごとにディレクトリを作り、`plugin.json` とエントリのアセンブリを配置してください。メニューの「プラグイン → 拡張の管理」で有効状態を変更できます。

SDK の対象は .NET Standard 2.1 です。`IAvaPlugin.Register` でコマンドとショートカットを登録し、`Activate` で受け取ったコンテキストから材質一覧、リグ、作品状態、描画、ウィンドウを利用できます。独立ウィンドウから作品を変更するときは `Post` を使います。`DocumentGeneration` は作品を開き直すと変わるため、作業開始時の値と比較して結果の適用先を識別できます。

コンテキストを `IMotionEditor` にキャストすると、`ReadMotion()` で各トラックの安定 ID、現在値、登録キー、リビジョンを取得できます。`Sample` は指定時刻を評価します。`ApplyMotion(document, edits)` は対象を検査し、すべての変更を一度の Undo に登録します。編集を始めた時点の document と各トラックの revision を渡してください。補間は既定で保持されます。

[材質一覧の例](../Examples/MaterialList/)には、拡張本体と専用の依存アセンブリが含まれています。リポジトリのルートから次のようにビルドして配置します。

```powershell
dotnet build Examples/MaterialList -c Release
$package = 'Builds/Windows/Extensions/example.material-list'
New-Item -ItemType Directory -Force "$package/managed" | Out-Null
Copy-Item Examples/MaterialList/plugin.json $package
Copy-Item Examples/MaterialList/bin/Release/netstandard2.1/MaterialList.dll "$package/managed"
Copy-Item Examples/MaterialList/bin/Release/netstandard2.1/MaterialLabels.dll "$package/managed"
```

起動後に「プラグイン → 材質一覧」を選ぶと、現在の作品にあるモデルと材質スロットがステータス欄に表示されます。SDK アセンブリはプレイヤーに同梱されます。

## エフェクトの制御モデル

`.avaeffect` は制御ボーンとモーフを持つモデルです。`coordinateSpace` に `"motion"` を指定すると、床面を原点としてモーショントラックの位置と向きを使います。ボーンの `position` は親からの距離をメートルで指定します。

## 保存データ

`.avadance` は ZIP 形式です。`project.json` の `format` は `AvaAvaDance project 2`、`schemaVersion` は `2` です。モデル、アクセサリ、トラックには個別の ID があり、描画順と物理計算順はモデル ID の列で記録します。`.avamotion` は `AvaAvaDance motion 2` の JSON です。トラックはフレームと型別の値を持ち、レイヤー、クリップ、補間曲線を一緒に保存できます。本体はバージョン 1 の作品とモーションも読み込みます。

クリップの `source` は同じレイヤーのモーションソース ID です。空の場合はレイヤー自身のキーを使います。`start` は親の時間軸での配置位置、`sourceStart` と `sourceEnd` はソース側の範囲です。`rate` は親の1フレームで進むソースのフレーム数で、画面の時間スケールの逆数です。`cycles` が正のときは再生回数から配置長を求め、ゼロのときは `length` を使います。`infinite` を有効にしたクリップは指定した範囲を繰り返します。

`ReadState` と `WriteState` は現在の作品にある自分の拡張の状態を扱います。拡張の情報は `extensions/index.json`、状態は `extensions/<id>/state.json` に格納されます。作品を別の環境で開いて保存した場合も、未導入の拡張の状態とバイナリデータを保持します。

`ISceneEditor.ReadScene()` はモデルとアクセサリの ID、表示状態、選択、材質スロット、計算順を返します。`ReadRig` は骨の階層、静止姿勢、モーフと骨に追従する衝突形状を返します。`measurementVersion` が 1 のリグでは、頭頂・足底・指先を計測した節点の `origin` に `weightedVertex` が入ります。箱の `boxAxes` は、各半軸を骨のローカル座標で XYZ 順に並べた9個の値です。足の IK ハンドルでは、`ikRoot` にチェーン先頭の骨の名前または役割を指定できます。`IProjectStorage.WriteBlock` は作品状態と指定したバイナリ資産の変更をまとめて保存します。バイナリ資産は `extensions/<id>/blobs/` に入り、`ReadBlob` で取り出せます。

`IEvaluationHost.ObserveEvaluation` では、サンプリング、IK、物理計算、シーン評価、描画開始の各段階を購読できます。通知にはフレーム、モデル ID、物理ステップかどうかが含まれます。同じ段階の先行フックを名前で指定できます。購読の解放は返された `IDisposable` で行います。
