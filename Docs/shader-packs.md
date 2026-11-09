# シェーダーパックと lilToon 対応

## 読み方

同梱の **lilToon 2.4.1** は起動時に自動ロードします。元 `.mat` の `m_Shader.guid` をパックの原本GUIDと照合し、コンパイル済みShaderへ設定を復元します。Opaque/Cutout/Transparentと各Outlineの6種、およびUsePass依存3種を含みます。Light/Multi/Tessellation/Fur等は今回のパックにありません。

**unitypackage内の.shader/HLSLをその場でコンパイルする機能ではありません。** 改変されたshaderソースが同じGUIDのまま入っていても、パックにビルド済みの版が使われます。未知GUIDはプレビュー材質へfallbackし、未対応件数を表示します。

画面上の「シェーダー追加」から信頼できる `.avadshader` を選ぶと、独自Shaderも追加できます。現在の起動セッションだけで有効です。モデル読込後に追加した場合はモデルを再読込してください。同じGUID/名前の二重登録は拒否します。

## 対応環境

- Unity **2022.3.22f1** / Windows x64
- **Built-in Render Pipeline / Direct3D 11** の組合せ
- schema / Unity版 / platform / pipeline / API が違うパックは拒否
- 1パック64 shader・256 MiBまで。Shaderとmanifest TextAssetのみをロードし、GameObject/MonoBehaviourは生成しない

GPUプログラムやAssetBundleのネイティブ処理をOSサンドボックスで隔離する機能ではありません。信頼できる配布者のパックのみ利用してください。

## 独自Shaderをパック化する（制作者向け）

1. このプロジェクトをUnity 2022.3.22f1で開き、配布・コンパイルが許可されたShaderを取り込む。
2. Windows64 graphics APIをDirect3D11だけに設定（アプリのBuildAppもこの設定を使用）。
3. ProjectでShaderを選択し、**AvaAvaDance > Shader packs > Build selected shaders for Windows**。
4. `.avadshader` を保存して配布する。GUIDを維持した元`.mat`がアバター側に必要。
5. 実際のアプリで色・テクスチャ・透明・outline等を検証する。作者が必要なvariantを保持する責任がある。

ShaderLabのUsePass参照など、文字列参照の依存が自動では残らない場合があります。参照先のShaderもまとめて選択してください。shader_feature variantのstripはShaderごとに異なります。未知Shaderの全機能を自動で保証するexporterではありません。URP/HDRP向けShaderは対象外です。

受け取ったユーザーはUnity Editor不要です。パックを作る側はUnityでの事前ビルドが必要です。

## lilToonパック再生成

公式ソース: `Assets/lilToon`。版2.4.1、原本commit `e698a2f791bd28dd1aabf227c74f14bedeca3587`。
ビルド時は公式public生成APIで `.lilinternal` から `.shader` を生成するため、Shader配下は生成済みソースです。

- `LILTOON_DISABLE_OPTIMIZATION` define を設定し、domain reload後に生成
- `ShaderPackBuilder.PrepareLilToon()` で全機能設定を有効化
- `ShaderPackBuilder.BuildLilToon()` で `Assets/StreamingAssets/liltoon.avadshader` を生成
- `BuildApp.Windows()` でアプリをビルド

サンプル材質しか存在しないビルド時に、後から読み込む材質の機能が削除されないよう最適化を無効化しています。UsePass依存も明示的に含みます。全lilToon機能の見た目一致を実証した意味ではありません。

## 材質の復元と未対応

復元: `m_Floats` / `m_Ints` / `m_Colors` / `m_TexEnvs`（PNG、scale/offset）/ renderQueue / 宣言済みkeyword。材質名の重複候補は勝手に選びません。

未対応: material variant/親材質の継承、FBX.meta externalObjectsやprefabからの材質割当て、画像import設定の完全再現（normal map圧縮/色空間等）、JPEG/TGA、Cubemap、アニメ制御、AudioLink等の外部連携。全shader featureの互換性や市販アバターを検証したものではありません。

## 確認した一次資料・方式の区別

- [Warudo Mod SDK](https://docs.warudo.app/docs/modding/mod-sdk): Unity側で作るmodでlilToon等を利用。
- [VSeeFace VSFAvatar](https://www.vseeface.icu/#vsfavatar): Unity AssetBundleを使う方式。
- [Unity 2022.3 Shader compilation](https://docs.unity3d.com/2022.3/Documentation/Manual/shader-compilation.html)
- [lilToon 2.4.1](https://github.com/lilxyzw/lilToon/tree/2.4.1)

これらと同様に、Unityで事前コンパイルしたShaderを実行アプリがロードする設計です。各アプリが生のunitypackage内のShaderソースをランタイムコンパイルしていると主張するものではありません。

## ライセンス

lilToonはMIT。配布アプリには `Assets/lilToon/LICENSE` と `Assets/lilToon/Third Party Notices.md` の両方を同梱します。Shaderの利用権とアバターの利用権は別です。独自パックにもそれぞれの配布条件を適用してください。

## Verification (2026-11-09)
Unity 2022.3.22f1 Windows x64 standalone build succeeded. Six lilToon variants and a separately loaded custom shader pack passed runtime GUID/material/renderer smoke tests. The fixture is an original sphere, not a commercial avatar. Actual offscreen rendering showed a blue body and toon shading, with no error-pink output. Outline/cutout/transparent shader selection was checked, but outline fidelity and alpha edge behavior are not certified by this opaque-colored fixture. Real avatar prefab composition, all texture importer options, and every shader feature remain unverified. Material parser: 49 checks; archive reader: 33 checks; pack manifest: 13 cases; native sphere winding: 528 nondegenerate triangles agree with normals.
