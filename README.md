# AvaAvaDance

AvaAvaDance は Windows x64 用のアバターアニメーション編集アプリです。Unity 2022.3.22f1 で開発しています。VRM または unitypackage からモデルを読み込み、タイムラインでポーズ・表情・カメラを編集し、画像や動画に書き出せます。

画面は MikuMikuDance を参考に、左にタイムライン、右にビューポート、下に操作パネルを配置しています。[MikuMikuDance 作者配布ページ](https://sites.google.com/view/vpvp/)

## Windows版の起動

ビルド出力フォルダーにある `AvaAvaDance.exe` を起動します。アプリを別の場所に移すときは、`UnityPlayer.dll`、`AvaAvaDance_Data`、`MonoBleedingEdge`、ライセンス文書を含むフォルダー全体をコピーしてください。

1. 下段「モデル操作」の **読込** で `.vrm`（VRM 0.x / 1.0）または `.unitypackage` を選択します。VRM は選んだモデルがそのまま表示されます。
2. unitypackage の場合は、一覧からアバターのprefabと表示するFBXの組を選びます。複数のprefabが同じFBXを使う場合も個別に選択できます。「FBXのみ」は対応するprefabを検出できなかったモデルです。
3. ビューポートのボーン、左の行、または「ボーン操作」のリストから編集対象を選びます。

外部シェーダーを使う場合は、先に「シェーダー追加」で `.avadshader` を選び、その後「モデル操作 → 読込」でアバターを開きます。lilToon用のパックを公式配布物から作る手順は[シェーダーパック仕様](Docs/shader-packs.md)に記載しています。

## ポーズを付けて再生する

1. 0フレームから、左上のフレーム欄に `24` と入力して Enter を押します。タイムラインのクリック、左右キーでも移動できます。
2. ボーン操作の「回転」または「移動」で左ドラッグします。右下のX・Y・Zをドラッグすると1軸だけ操作できます。ビューポート下の数値欄にも直接入力できます。「登録」で選択ボーンの行に菱形が付きます。表情は「目」「リップ」「まゆ」「その他」から選び、スライダーで量を決めてその欄の「登録」を押します。
3. 12フレームに戻ると、0と24の中間の姿勢になります。24のボーンキーを選んで左下の補間曲線の赤い点をドラッグすると、移動または回転の速さを調整できます。
4. 「カメラ編」でモデルを表示したままカメラと照明の編集に切り替えます。カメラ中心、角度、距離、視野角、パース、照明の色・向きを変えて各パネルの「登録」を押します。「モデル編」でボーンと表情の編集に戻ります。
5. 「再生」でカメラ、照明、ボーン、表情を一緒に30 fpsで再生します。開始・終了欄で範囲を指定できます。空欄なら先頭から最後のキーまたはWAVの終端まで再生します。「くり返し」で範囲をループします。

### キーの編集とファイル

- 左のフレーム操作で先頭、前後の登録キー、最終キーに移動できます。タイムライン上のホイールで行、Shift＋ホイールで表示フレームをスクロールします。
- 「縦選択」は同じフレームにあるキーをまとめて選択します。「範囲選択」は指定範囲の登録キーを対象にします。コピー、ペースト、削除、「元に戻す」「やり直し」はまとめて操作できます。フレーム0のキーは保持されます。
- 「編集」の空フレーム挿入・列フレーム削除は、ボーン／表情またはカメラ／照明を個別にずらします。挿入／削除は `I`・`K` と `U`・`J`、全表情登録は `H` です。
- **VRMAを読み込む**には、モデル編で開始フレームに移動し、「ファイル → モーションデータ読込」から `.vrma` を選びます。モデルの体格に合わせて腰移動とボーンの回転を変換し、対応する表情と視線も30 fpsのタイムラインに入ります。たとえば42フレームで1秒のモーションを開くと、42～72フレームを編集できます。「両目」の回転でVRMの視線を変えられます。足の動きに関係するIKは「表示・IK」キーで自動的にOFFになります。
- **VRMAを書き出す**には、モデル編でポーズを「登録」し、「ファイル → モーションデータ保存」のファイルの種類で **VRMA** を選びます。選択中のモデルの登録済みモーションを先頭から書き出します。ボーンと足IKの結果は30 fpsでサンプリングし、表情と視線も格納します。カメラ・音声・アクセサリは `.avadance` で保存できます。
- 同じ「モーションデータ読込／保存」でVMD、「ポーズデータ読込／保存」でVPDを扱います。VMDはモデル編では名前が一致するボーン・表情・表示／IK、カメラ編ではカメラ・照明・セルフ影のキーを現在フレームから読み込みます。VPDは現在のポーズに適用します。独自形式の `.avamotion` はファイルメニューの「AvaAvaDanceモーション読込／保存」で扱います。
- ファイル、編集、表示、背景、表情、物理演算、ヘルプの各メニューは、順に `Alt+F/D/V/B/M/P/H` で開きます。括弧内のキーで項目を選択できます。「ヘルプ → モデル拡張」にはモデル別の設定をまとめています。「ヘルプ → English Mode」でメニューを英語に切り替え、各トラックに設定した英語名を表示できます。
- `P` で再生／停止、`Ctrl+Z/X` で元に戻す／やり直し、`Ctrl+←/→` で前後の登録キーに移動します。`Tab`／`Shift+Tab` はカメラ編と各モデルを順送り／逆送り、`Z/X/C` は移動／回転／選択、`L` は座標系、`G` は「セット」したフレームへの移動です。
- モデルを追加して「背景 → モデル描画順／モデル計算順」から順番を設定できます。アクセサリ操作でDirectXの `.x` または `.vac` を開き、親ボーン・位置・角度・サイズ・透明度を登録できます。X読込にはWindowsの `d3dx9_43.dll` が必要です。
- 「ファイル → WAVファイル読込」で波形を表示し、再生範囲に合わせて音声を再生できます。「画像ファイルに出力」は現在のビューポートをBMP・JPEG・PNG・TGA・DDS・DIB・PFM・HDRで保存できます。一部形式ではDirectX 9ランタイムを使います。

## プロジェクト保存・IK・物理・影・動画出力

- **ファイル → 名前を付けて保存／上書き保存／開く** で `.avadance` プロジェクトを扱います。VRMモデル本体、選択したprefab・FBXのGUID、元のunitypackageに含まれるアセットとメタデータ、追加シェーダーパック、読み込んだ音声を格納します。登録キー、編集中の姿勢、現在フレーム、モデル表示、視点、再生範囲、物理・影・出力設定も復元します。
- **足IK** はボーン操作で「左足ＩＫ」または「右足ＩＫ」を選び、「移動」で目標を動かして「登録」します。モデル操作のIKリストとON／OFF、登録ボタンで有効状態をキーフレームにできます。Humanoidの対応表または骨名から脚の関節を検出し、つま先があるモデルにはつま先IKも作成します。任意の2関節チェーンは末端のボーンを選び、「物理演算 → 選択ボーンにＩＫを追加」で設定できます。
- 新規プロジェクトの「物理演算 → Bullet物理」はBullet 3.25で剛体とばね付き6自由度ジョイントを毎秒60回計算します。アバターの有効なPhysBoneからカプセルとジョイントを作ります。手動で追加するには、子ボーンのあるボーンを選んで「物理演算 → 選択ボーンの物理演算」を使います。
- 「ヘルプ → モデル拡張 → 物理演算編集」で球・箱・カプセル、ボーン追従・物理演算・位置合わせ、質量、摩擦、反発、減衰、衝突グループ／マスク、親ジョイントの有無を設定できます。グループ1はマスクの下位1ビットに対応します。床とprefabのコライダーはグループ1、自動生成した剛体はグループ2です。初期マスク65533で、髪や衣装の剛体が重なった状態から互いを押し出すのを防ぎます。グループ2同士も衝突させる場合は双方のマスクを65535にします。「トレースモード」でフレームを移動すると再生と同じ時間刻みで計算します。巻き戻し時は先頭から再計算します。
- 以前のプロジェクトは「PhysBone互換物理」で開きます。「物理演算 → Bullet物理」で切り替え、保存すると次回もその設定で開きます。PhysBoneの伸縮、球・カプセルの内側に閉じ込めるコライダーなど、元のリグの挙動を使う場合は「PhysBone互換物理」を選びます。
- **影** は「カメラ編 → セルフ影操作」で「影なし／モード1／モード2」と影範囲を選び「登録」します。表示メニューに地面影表示、明るさ、透明化、セルフシャドウ表示があります。モデル操作の「セルフ影」はそのモデルの受け影を切り替えます。
- **AVI** は無圧縮RGBとオプションのステレオPCMで出力します。AVI 1.0形式のため1ファイル2 GBまでです。
- **モデル拡張** では英語名、トゥーンテクスチャ、関節の物理設定を編集できます。変更はプロジェクトに保存します。
- **MP4** は「表示 → 出力サイズ」で録画サイズを決め、「ファイル → MP4ファイルに出力」で保存先を選びます。続く「MP4出力設定」でフレームレート、録画フレームの開始・終了、WAVE出力を指定して「OK」を押します。両端を含む30 fpsタイムラインから指定fpsでサンプリングし、録画用カメラの絵をH.264、音声を48 kHzステレオAACで書き出します。録画用カメラにはモデル、背景、照明、影が映ります。出力をキャンセルすると元のファイルが残ります。Windows Media Foundationを使用し、通常の出力操作に外部エンコーダーのインストールは不要です。

## 直接読込の対応範囲

- gzip/tar と GUID/pathname をアプリ内で読み取り、FBX を ufbx で解析。
- FBX の階層 / TRS / mesh / normal / UV / 材質スロット / skin / bindpose / 最大4 weight、FBX blend shape の頂点位置差分を Unity ランタイムオブジェクトへ再構築。
- 選択したprefabでFBXを参照するSkinnedMeshRendererの材質スロット・表示設定を読み、FBXの `.meta` にある材質マッピング、材質名の順に照合します。PNGをGUIDで割り当てます。
- 材質のシェーダーGUIDを利用者が読み込んだパックと照合します。未登録のGUIDにはプレビュー材質を割り当て、件数を表示します。
- Windows版のUIにはビルド時に取得した M PLUS 1p Regular を使用します。

VRMA 1.0 はファイル内の先頭のアニメーションを読み込みます。VRM 0.x / 1.0 ではモデルに定義されたヒューマノイド・表情の対応表を使います。unitypackage のFBXには、Humanoidの対応表または標準ボーン名で特定できた関節と、名前が対応する表情を適用します。全身のVRMA書き出しには必須のヒューマノイドボーンが必要です。

表情の4分類はFBXのチャンネル名から判定し、その他の名前は「その他」に表示します。同名の表情が複数メッシュにあれば同時に動かします。FBXの形状キーは1メッシュ512チャンネル、1チャンネル128段階、モデル全体で展開頂点×段階の合計6400万を上限とし、省略があれば読込結果に表示します。

**unitypackage の対応範囲** は、選択したFBXのメッシュを直接参照するUnityテキストprefabです。VMD／VPDは名前が一致するトラックに読み込みます。プロジェクトは `.avadance` 形式で保存します。

## 読み込むデータと利用条件

- アバターの読込は端末上で行います。パッケージからFBX、prefabの設定、材質、画像を読み取り、シェーダーはビルド済みパックから適用します。Xアクセサリの相対参照画像は、Xファイルのあるフォルダーとその配下から読み込みます。
- tarは相対パス、ファイル種別、重複、チェックサムを検査します。上限は1エントリー256 MiB、展開合計1 GiB、20,000エントリーです。
- FBXの解析とシェーダーパックの読込はプレイヤーのプロセス内で行います。入手元を確認したファイルを選んでください。PNGは最大4096四方を受け付け、プレビュー用に長辺1024以下に縮小します。保持するテクスチャは32枚・128 MiBまでです。
- `.avadance` には読み込んだモデルや音声のデータが入ります。プロジェクトファイルを渡す場合は、収録した各素材の再配布条件を確認してください。動画や画像の公開には各素材の利用条件が適用されます。

## ソースから起動 / ビルド

Windows x64、Unity 2022.3.22f1（Windows Build Support）、Python 3、Git、CMake、MSVCのC++ビルドツールとWindows SDKを使用します。Editorを開く前に、リポジトリのルートで次の2行を実行します。

```powershell
python Tools/restore_inputs.py
powershell -File Native/build.ps1
```

1行目はufbxとM PLUS 1pの固定リビジョンを取得し、SHA-256を照合します。2行目はBulletの固定リビジョンを取得し、Windows用プラグイン3個をコンパイルします。Unity Package Managerはプロジェクトを開く際にUniVRMなどを取得します。[ネイティブプラグイン](Native/README.md)・[UniVRM v0.131.4](https://github.com/vrm-c/UniVRM/releases/tag/v0.131.4)

Unity Hubでプロジェクトを開き、**AvaAvaDance > Open UI prototype** を選ぶとEditor内で起動します。スタンドアロン版は同じEditorの `Unity.exe` で次のコマンドを実行します。

```powershell
Unity.exe -batchmode -quit -projectPath <repo> -executeMethod AvaAvaDance.Editor.BuildApp.Windows -logFile build.log
```

出力は `Builds/Windows/AvaAvaDance.exe`。Windows Build Support が必要です。既定のウィンドウサイズは1280×900です。文字や操作パネルはWindowsのディスプレイ設定の表示倍率に従い、別のモニターへ移すと自動で切り替わります。画面に収まらない部分はスクロールできます。

## 検証手順

ソースに含まれる三角形のFBXと、スクリプトで生成するモデル・音声を使用します。以下のコマンドはリポジトリのルートから実行します。

```powershell
dotnet run --project Tests/ReaderHarness/ReaderHarness.csproj
dotnet run --project Tests/MaterialHarness/MaterialHarness.csproj
powershell -File Native/build.ps1
powershell -File Native/test.ps1
python Native/test_bullet.py
python Tests/check_project.py
python Tests/make_smoke_package.py
Builds/Windows/AvaAvaDance.exe -avaSmokePackage <repo>/Tests/triangle.unitypackage -avaSmokeOutput <output-dir> -avaMotionSmoke
```

自作のIK・物理・WAV検証素材で、プロジェクトの新規／復元とMP4出力を確認できます。

```powershell
python Tests/make_production_fixture.py
Builds/Windows/AvaAvaDance.exe -batchmode -avaSmokePackage Builds/ProductionTest/rig.unitypackage -avaSmokeWav Builds/ProductionTest/beat.wav -avaSmokeOutput Builds/ProductionTest -avaPhysBoneSmoke -avaProductionSmoke -avaMovieEdgeSmoke
Builds/Windows/AvaAvaDance.exe -batchmode -avaProjectSmoke Builds/ProductionTest/dance.avadance -avaSmokeOutput Builds/ProductionTest/Cold
```

メニュー互換テストは、上のWindowsプレイヤーを `-avaMenuSmoke`、`-avaSmokePackage Tests/triangle.unitypackage`、`-avaSmokeWav Builds/ProductionTest/beat.wav`、`-avaSmokeOutput <出力先>` で起動します。アクセスキー、モデル切替、VSQ、VPD、VMD、X、複数モデルのプロジェクト再読込、AVI出力を検査します。

VRMとVRMAのテストには、頭・腰・足IK・まばたき・視線を持つVRM 0.xと1.0を各1体使います。次のテストはボーンの基準回転、腰移動の体格補正、補間、表情・視線、Undo/Redo、プロジェクト復元と、編集済みVRMAを別の開始フレームに再読込した結果を検査します。

```powershell
python Tests/make_vrma_fixture.py Builds/test.vrma
Builds/Windows/AvaAvaDance.exe -batchmode -avaVrmSmoke <VRM0.vrm> <VRM1.vrm> Builds/test.vrma -avaSmokeOutput Builds/VrmTest
```

## ライセンス

AvaAvaDance 独自のアプリコード、ネイティブプラグインのラッパー、テスト素材・生成スクリプト、ドキュメントは [MIT License](LICENSE) で公開しています。

| 第三者のソフトウェア・素材 | 使用箇所 | ライセンス・権利表示 |
| --- | --- | --- |
| ufbx 0.21.3 | ビルド準備で取得し、AvaFbx.dll に組み込み | [MITを選択](ThirdPartyNotices/ufbx-LICENSE.txt) |
| Bullet 3.25 | AvaBullet.dll の剛体・衝突計算 | [zlib](ThirdPartyNotices/Bullet-LICENSE.txt)・[Discregrid由来の部分のMIT](ThirdPartyNotices/Discregrid-LICENSE.txt) |
| M PLUS 1p Regular | ビルド準備で取得する日本語フォント | [SIL Open Font License 1.1](ThirdPartyNotices/MPLUS1p-OFL.txt) |
| UniVRM 0.131.4・UniGLTF | Unity Package Managerで取得し、VRM・VRMAの読込と出力に使用 | [UniVRM・MToonのMIT](ThirdPartyNotices/UniVRM-LICENSE.txt)・[UniGLTFのMIT](ThirdPartyNotices/UniGLTF-LICENSE.txt) |
| Unity Mathematics 1.2.6・Timeline 1.7.6 | Unity Package Managerで取得し、Windows版に組み込み | [Unity Companion License](ThirdPartyNotices/Unity-Companion-License.txt)・[Mathematicsのノイズ関数のMIT](ThirdPartyNotices/Unity-Mathematics-Noise-LICENSE.txt) |

Windows版のビルドは実行ファイルと同じフォルダーに `LICENSE` と `ThirdPartyNotices/` を配置します。Unity Editor と Player は Unity の利用条件に従います。ネイティブDLLは MSVC ランタイムを静的リンクし、MP4出力は Windows の Media Foundation を使用します。[Unity の利用条件](https://unity.com/legal/editor-terms-of-service/software)・[Microsoft のランタイム配布資料](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)

`.gitignore` のUnity用テンプレートは [github/gitignore（CC0 1.0）](https://github.com/github/gitignore/blob/main/LICENSE) を使用しています。

## lilToon・追加シェーダー

lilToonを使う場合は、公式VPM配布物から別のUnityプロジェクトでパックを作成します。出力フォルダーには `.avadshader` と、lilToon、VRC Light Volumesなどの権利表示が入ります。[外部パックの作成手順](Docs/shader-packs.md)
