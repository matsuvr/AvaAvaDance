# 外部シェーダーパック

右上の「シェーダー追加」には **lilToon を導入**、**VRM (MToon / MToon10) を導入**、**その他のシェーダー（UnityPackage / パック）** があります。lilToon・VRMはライセンス全文と第三者の権利表示を確認し、同意チェックを入れて「同意してダウンロード・適用」を押します。同意前にはネットワークアクセスしません。閉じる／キャンセル／Escで中止できます。

lilToon 2.4.1 は[公式VPM](https://lilxyzw.github.io/vpm-repos/vpm.json)の配布情報、VRM 0.131.4 は[UniVRM GitHubリリース](https://github.com/vrm-c/UniVRM/releases/tag/v0.131.4)のVRM 0.x・1.0配布物を取得します。配布物の固定SHA-256を検証した後、同じソースからビルドしてアプリに同梱した描画用パックを有効化します。Unity Playerではソースをコンパイルできないため、この2種類の導入では利用者の端末にUnity Editorを必要としません。導入済みの配布物と同意記録はローカルに保存し、次回起動時に照合して復元します。アプリのフォルダー全体と `ShaderPackages` を一緒に配布してください。

パックは Unity 2022.3.22f1 の Windows x64、Built-in Render Pipeline、Direct3D 11 用にコンパイルした AssetBundle です。登録したシェーダーは、元の材質が参照するGUIDで照合します。未登録のGUIDにはアプリのプレビュー材質を使い、件数を表示します。追加直後には読み込み済みの全モデルの対応する材質も更新し、モデル・ポーズ・タイムラインは保持します。

## アプリ同梱用の公式パックを作成する

1. `python Tools/restore_official_shaders.py` を実行します。配布物のSHA-256を照合し、lilToon・VRMそれぞれの作業用プロジェクトと、アプリのライセンス表示文書を生成します。
2. Unity 2022.3.22f1で次の2つを実行します（Unity CLIを使用）。

   ```powershell
   unity run Builds/LilToonPackProject --editor-version 2022.3.22f1 -- -buildTarget Win64 -executeMethod AvaAvaDance.Editor.LilToonPackBuilder.Build
   unity run Builds/VrmShaderPackProject --editor-version 2022.3.22f1 -- -buildTarget Win64 -executeMethod AvaPackageShaderBuilder.Build
   ```

3. アプリを通常の `AvaAvaDance.Editor.BuildApp.Windows` でビルドします。両方のパックとライセンス、ソースと描画用パックのSHA-256を記録した `install.json` が `ShaderPackages` に配置されます。対応する描画用パックがない場合はビルドを中止します。

## lilToon 2.4.1 のパックを作成する

1. プロジェクトのルートで `python Tools/restore_liltoon.py` を実行します。スクリプトは公式VPMから `jp.lilxyzw.liltoon` 2.4.1 を取得し、配布ZIPのSHA-256を照合します。作成先は `Builds/LilToonPackProject/` です。[公式VPMリポジトリ](https://lilxyzw.github.io/vpm-repos/vpm.json)
2. Unity 2022.3.22f1で **そのパック作成用プロジェクト** を開き、**AvaAvaDance > Build external lilToon pack** を実行します。バッチで作成する場合は、アプリ本体のリポジトリのルートから次を実行します。

   ```powershell
   Unity.exe -batchmode -quit -buildTarget Win64 -projectPath Builds/LilToonPackProject -executeMethod AvaAvaDance.Editor.LilToonPackBuilder.Build -logFile pack.log
   ```

3. アプリの「シェーダー追加 → その他のシェーダー」で `Builds/LilToonPackProject/Output/liltoon.avadshader` を選べます。通常は先述の公式導入メニューを使用します。

パックには通常・Lite・Multi、Cutout・Transparent、Outline、Refraction、Fur、Gem、Tessellation、OverlayとUsePassの参照先を含む61シェーダーを収録します。Editorのベイク・プロパティ表示専用シェーダーは除きます。配布する場合は、`Output/` 内に生成される権利表示をパックに添付してください。[lilToon 2.4.1 のMIT](https://github.com/lilxyzw/lilToon/blob/2.4.1/Assets/lilToon/LICENSE)・[第三者の権利表示](https://github.com/lilxyzw/lilToon/blob/2.4.1/Assets/lilToon/Third%20Party%20Notices.md)・[VRC Light VolumesのMIT](https://github.com/lilxyzw/lilToon/blob/2.4.1/Assets/lilToon/Shader/Includes/VRC%20Light%20Volumes/license.txt)

公式VPMインストーラーの `.unitypackage` を選ぶと、未導入の場合はlilToonの同意画面へ案内します。導入済みの場合は `config.json` の要求バージョンを読込済みのパックと照合します。

## その他のUnityPackageを読み込む

「その他のシェーダー」で `.unitypackage` を選ぶと、端末のUnity 2022.3.22f1でShaderLabと依存画像を別の作業用プロジェクトへ展開し、描画用パックに変換します。パッケージのC#・DLLは展開しません。シェーダー生成に独自のEditorスクリプトが必要な製品は、作者の手順でパックを作成してください。Unity Hubの標準導入先以外では環境変数 `AVA_UNITY_EDITOR` にEditorの `Unity.exe` を設定できます。失敗時は変換ログの場所を表示します。

## 独自のシェーダーパックを作成する

1. 配布条件を確認したシェーダーと依存ファイルを作業用のUnityプロジェクトに取り込みます。このリポジトリのプロジェクトを作業用に複製すると、パック作成メニューを利用できます。
2. Windows x64、Built-in Render Pipeline、Direct3D 11を選択します。
3. Projectウィンドウで対象のシェーダーとUsePassの参照先を選び、**AvaAvaDance > Shader packs > Build selected shaders for Windows** を実行します。
4. 出力した `.avadshader` と元のGUIDを参照する材質を使い、Windows版で描画を確認します。

プレイヤーはマニフェストに記録された形式とビルド環境を検査し、パックからShaderを読み込みます。上限は1パック当たりシェーダー64個、256 MiBです。シェーダーのコンパイルはパックの作成時に行います。[Unity 2022.3 のシェーダーコンパイル](https://docs.unity3d.com/2022.3/Documentation/Manual/shader-compilation.html)

## 材質とプロジェクト保存

VRM 1.0 は各glTF材質の拡張に従い、MToon、Unlit、PBRを読み分けます。両面のPBR材質は裏面の法線を反転して照明を計算します。VRM 0.x の `materialProperties` が元のUnityシェーダーを指定している場合は、ShaderLab名で照合し、保存された色・数値・テクスチャ・キーワード・描画順を適用します。旧Unlit形式にはUniVRMの互換変換を使用します。

prefabでFBXを直接参照するSkinnedMeshRenderer、または同じGameObjectのMeshFilterとMeshRendererから、材質スロット、表示、影を落とす／受ける設定を読み取ります。FBXの `.meta` にある材質マッピングと、一意に特定できた材質名も照合に使用します。

パックのシェーダーの既定値を基に、材質の数値、色・ベクトル、テクスチャのGUID・スケール・オフセット、renderQueue、宣言済みキーワード、タグ、無効なパスを反映します。PNGはTextureImporterのsRGB・法線マップ指定、ミップマップ、フィルタ、ラップ、解像度（Standaloneの有効な上書きを優先）を使用します。テクスチャ圧縮やストリーミングは再現しません。

セルフ影はUnityのライトからシェーダーに渡します。たとえばlilToonでは元の材質が指定した影色・影の境界・受け影の強さが効き、ステンシルを使う追加のFakeShadow材質も元のスロットで描画します。撮影時の照明やカメラはprefabの材質とは別の設定です。

保存した `.avadance` には、そのプロジェクトで読み込んだパックのデータが入ります。ファイルを第三者に渡す際は、モデルやパックごとの再配布条件を確認してください。

Windowsプレイヤーの `-avaSmokePackage` テストで `-avaSmokeFbx <FBX名> -avaSmokePrefab <prefab名> -avaMaterialSmoke` を指定すると、元の材質と描画中のスロット・シェーダー・色・数値・PNG割り当てを照合し、出力先に `materials.txt` と `preview.png` を保存します。`-avaLateShaderPack -avaShaderPack <パックのパス>` を併用すると、モデルを読み込んだ後にシェーダーを導入する経路を検査できます。
