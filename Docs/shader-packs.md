# 外部シェーダーパック

「シェーダー追加」で信頼できる配布元の `.avadshader` を選び、モデルを読み込みます。パックは Unity 2022.3.22f1 の Windows x64、Built-in Render Pipeline、Direct3D 11 用にコンパイルした AssetBundle です。登録したシェーダーは、元の材質が参照するGUIDで照合します。未登録のGUIDにはアプリのプレビュー材質を使い、件数を表示します。

## lilToon 2.4.1 のパックを作成する

1. プロジェクトのルートで `python Tools/restore_liltoon.py` を実行します。スクリプトは公式VPMから `jp.lilxyzw.liltoon` 2.4.1 を取得し、配布ZIPのSHA-256を照合します。作成先は `Builds/LilToonPackProject/` です。[公式VPMリポジトリ](https://lilxyzw.github.io/vpm-repos/vpm.json)
2. Unity 2022.3.22f1で **そのパック作成用プロジェクト** を開き、**AvaAvaDance > Build external lilToon pack** を実行します。バッチで作成する場合は、アプリ本体のリポジトリのルートから次を実行します。

   ```powershell
   Unity.exe -batchmode -quit -buildTarget Win64 -projectPath Builds/LilToonPackProject -executeMethod AvaAvaDance.Editor.LilToonPackBuilder.Build -logFile pack.log
   ```

3. アプリの「シェーダー追加」で `Builds/LilToonPackProject/Output/liltoon.avadshader` を選びます。モデルを既に開いている場合は再読込します。

パックには Opaque・Cutout・Transparent、OnePass／TwoPass Transparent、Multi と各 Outline 版、FakeShadow、UsePass の参照先3種の計16シェーダーを収録します。配布する場合は、`Output/` 内に生成される権利表示をパックに添付してください。[lilToon 2.4.1 のMIT](https://github.com/lilxyzw/lilToon/blob/2.4.1/Assets/lilToon/LICENSE)・[第三者の権利表示](https://github.com/lilxyzw/lilToon/blob/2.4.1/Assets/lilToon/Third%20Party%20Notices.md)・[VRC Light VolumesのMIT](https://github.com/lilxyzw/lilToon/blob/2.4.1/Assets/lilToon/Shader/Includes/VRC%20Light%20Volumes/license.txt)

公式VPMインストーラーの `.unitypackage` を選ぶと、アプリはその `config.json` を読み、要求バージョンを読込済みのパックと照合します。描画に使用するファイルは `.avadshader` です。

## 独自のシェーダーパックを作成する

1. 配布条件を確認したシェーダーと依存ファイルを作業用のUnityプロジェクトに取り込みます。このリポジトリのプロジェクトを作業用に複製すると、パック作成メニューを利用できます。
2. Windows x64、Built-in Render Pipeline、Direct3D 11を選択します。
3. Projectウィンドウで対象のシェーダーとUsePassの参照先を選び、**AvaAvaDance > Shader packs > Build selected shaders for Windows** を実行します。
4. 出力した `.avadshader` と元のGUIDを参照する材質を使い、Windows版で描画を確認します。

プレイヤーはマニフェストに記録された形式とビルド環境を検査し、パックからShaderを読み込みます。上限は1パック当たりシェーダー64個、256 MiBです。シェーダーのコンパイルはパックの作成時に行います。[Unity 2022.3 のシェーダーコンパイル](https://docs.unity3d.com/2022.3/Documentation/Manual/shader-compilation.html)

## 材質とプロジェクト保存

prefabでFBXを直接参照するSkinnedMeshRendererから、材質スロットと表示設定を読み取ります。FBXの `.meta` にある材質マッピングと、一意に特定できた材質名も照合に使用します。

パックのシェーダーには `m_Floats`、`m_Ints`、`m_Colors`、`m_TexEnvs` の値を設定します。テクスチャはPNGのGUID、スケール、オフセットを使用します。renderQueueと宣言済みキーワードも反映します。

保存した `.avadance` には、そのプロジェクトで読み込んだパックのデータが入ります。ファイルを第三者に渡す際は、モデルやパックごとの再配布条件を確認してください。
