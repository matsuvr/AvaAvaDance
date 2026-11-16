# MMD / MMM 操作・編集・出力機能の比較

調査日: 2026-11-16。比較基点: `5c3dd02d27f861ca6bda71bc53fe8261417f0b81`。

VRM / VRChat アバターと内部モーションに適用できるユーザー操作を比較した。PMD / PMX / VMD / VPD / VAC / MVD / PMM / MPJ などの独自形式は公開対応の対象外。原本側の細かな操作は、公開されている **MikuMikuMoving 作者のマニュアル**を根拠とする。MMD と MMM の機能がすべて同一であるという意味ではない。

「実装済」は比較基点のコードに操作入口と処理があることを表す。Unity 上で原本との完全な操作一致を確認したという意味ではない。「部分実装」は似た機能があっても、対象範囲・操作方法・挙動に不足があるもの。

今回確認した27候補についての比較であり、MMD / MMM の全機能を網羅した一覧ではない。

## 今回導入する項目

1. **キーフレームの時間拡大・縮小**: 選択したキーまたは編集対象のキーについて、基準フレームと倍率を指定して時間位置を変える。各行で選択中のベース／レイヤー／クリップソースが対象。レイヤーの再生速度だけを変える既存機能とは別の編集操作。内部トラックを操作し、Undo / Redo の対象にする。`TimelineRetiming.cs` / `KeyRetiming.cs`。
2. **MMM モードの再生3種**: 現在位置から再生、最初から再生、ブックマーク間を再生。ブックマーク間ではその区間を繰り返す。MMD モードの開始・終了欄による再生も維持する。`MotionTimeline.cs` / `PlaybackRange.cs` / `RibbonLayout.cs`。
3. **出力プリセットと指定解像度の静止画**: 名前・幅・高さ・動画 FPS を保存して選択できるようにし、静止画もプレビュー窓の大きさに依存せず指定解像度で書き出す。`OutputPreset.cs` / `OutputPresets.cs` / `ImageOutput.cs` / `MovieOutput.cs`。
4. **公開ポーズ操作の形式修正**: 基点ではポーズ読込・保存が VPD に接続されていたため、AvaAvaDance の `.avapose` へ置換する。ボーン姿勢と表情を保存し、読込は現在フレームの未登録姿勢へ一括適用する。併せて独自の VAC をアクセサリの公開読込候補から外し、標準の DirectX `.x` を扱う。`PoseFiles.cs` / `MenuCommands.cs` / `Accessories.cs`。

MMM マニュアルは、ブックマーク間再生の境界と反復を説明しているが、マーカーとブックマークが同位置の場合や、ブックマークが足りない場合のすべての境界条件は明記していない。そうした詳細は本アプリの実装仕様として扱い、原本と一致すると断定しない。

この実装では次のブックマークの直前までを区間とし、マーカー上ではそこから始まる区間を選ぶ。外側のブックマークがなければ0／作品末尾を補い、現在位置が末尾より後なら現在位置までを扱う。時間伸縮は非選択レイヤーやクリップ配置を変更せず、圧縮時のキー統合と選択外への上書き数を表示する。画像の寸法には動画用の偶数条件を適用せず、画像専用のサイズを作品に保存する。

## 機能比較

コード欄のファイルは `Assets/AvaAvaDance/Runtime/` にある。状態は上記基点時点。

| # | 原本の機能 | 基点の状態 | コードの根拠・不足内容 | 今回 |
|---|---|---|---|---|
| 1 | 現フレーム・グループの縦選択 [A §3.3] | 実装済 | `MotionTimeline.BuildTimeline` の「縦選択」、`RibbonRows.ChooseGroup` | 既存を利用 |
| 2 | Shift 範囲選択・Ctrl 個別解除 [A §3.3] | 実装済 | `RibbonTimelineDown` / `AddRectangle` | 既存を利用 |
| 3 | 空フレーム・列挿入 [A §3.8] | 実装済 | `MotionTimeline.ShiftColumn`、`EditorWorkspace.ShiftRibbonColumn` | 既存を利用 |
| 4 | 列削除と後続キーの前詰め [A §3.9] | 実装済 | 同上。基礎トラックの0フレームは保持する | 既存を利用 |
| 5 | キー時間の拡大・縮小 [A §3.10] | 部分実装 | `LayerEditing.EditClip` にクリップの時間スケールはあるが、選択キーのフレーム位置を直接伸縮する操作はない | 追加 |
| 6 | ブックマーク追加・削除・前後移動・全削除 [A §3.11] | 部分実装 | `TimelineBookmarks` と `EditorWorkspace.ToggleBookmark` / `JumpBookmark` に追加・個別削除・移動・メモあり。全削除操作なし | 全削除は今回未実装 |
| 7 | 反転ペースト [A §3.7] | 実装済 | `MenuEditing.PasteOriginalKeys(true)`、`DraftEditing.MirroredTrack` / `MirrorPose`。Humanoid 左右と名前を使用 | 既存を利用 |
| 8 | 補間曲線コピー・ペースト・ALL [B §4.4] | 実装済 | `Interpolation.BuildInterpolation`、`CurvePalette.BuildCurvePalette` | 既存を利用 |
| 9 | 補間パレット・新規キーの既定曲線 [B §4.4] | 実装済 | `CurvePalette.EditCurvePalette` / `ApplyDefaultCurve`、設定保存 | 既存を利用 |
| 10 | 未登録ボーン選択 [B §4.7] | 実装済 | `MenuEditing.SelectBoneSet(true)` が現在値とサンプル値を比較 | 既存を利用 |
| 11 | ボーン位置・角度・表情の一括補正 [B §4.8] | 実装済 | `MenuEditing.AdjustKeys`、`AdjustBones` / `AdjustMorphs` の公開入口 | 既存を利用 |
| 12 | 別ボーンへのキー貼付 [B §4.9] | 実装済 | `MenuCommands` の `PasteAt`、`MotionTimeline.PasteKeys` | 既存を利用 |
| 13 | キーを変えないモデル全体のセンターバイアス [B §4.10] | 部分実装 | `AdjustKeys(CenterBias)` はセンターボーンの既存キー位置を加算する処理。MMM の恒久的な位置・回転オフセットとは異なる | 差分は今回未実装 |
| 14 | モーションレイヤーの追加・削除・改名・合成 [B §4.5] | 実装済 | `LayerEditing.EditLayer` / `ApplyLayerEdit`、`MotionComposition` | 既存を利用 |
| 15 | モーフの上下ドラッグ・ダブルクリック登録・時間移動 [C §5.1] | 部分実装 | `RibbonTimelineDown/Up` にウェイト編集・登録あり。時間だけの移動は Alt、MMM の説明は Ctrl | 操作差分は今回未実装 |
| 16 | モーフの補間曲線 [C §5.3] | 実装済 | `Interpolation.MorphChannels`、`WriteCurve`、`CurvePalette`、`MotionTrack` | 既存を利用 |
| 17 | 現在位置から再生／最初から再生 [D §11.1] | 部分実装 | `TogglePlayback` は両モード共通で開始・終了欄を使用。MMM の2コマンドに分かれていない | 追加 |
| 18 | ブックマーク間再生・区間ループ [D §11.1、11.4] | 未実装 | ブックマークは存在するが、`TogglePlayback` / `AdvancePlayback` は参照していない | 追加 |
| 19 | 設定したサイズで静止画出力 [D §11.6、11.7] | 部分実装 | `MotionFiles.WritePreview` は `previewTexture` の幅・高さを使用。`movie.width/height` と独立 | 指定解像度出力を追加 |
| 20 | 出力サイズ・FPS の名前付きプリセット [D §11.7] | 未実装 | `MovieOutput.MovieSettings` に単一設定はあるが、プリセット一覧・追加・編集なし | 追加 |
| 21 | 編集モデル・選択ボーンへの視点追従 [E §7.8] | 未実装 | `SceneMotion.ActiveView` / `ApplyView` にモデル・ボーン位置を連続追従する選択肢なし | 今回未実装 |
| 22 | モデル編集中のカメラモーション視点 [E §7.8] | 実装済 | `MenuView.FollowCamera`、`SceneMotion.ActiveView` の `viewOptions.follow` | 既存を利用 |
| 23 | 複数ライトの選択・設定・キー登録 [E §7.3、7.5] | 部分実装 | `SurfaceLighting.DrawAdditionalLights` は追加ライトを描画できるが、`SceneMotion.BuildSceneTracks` の編集ライトは1本 | 複数灯編集は今回未実装 |
| 24 | 音声クリップの開始フレーム変更 [F §10.2] | 未実装 | `WaveAudio` は単一 `soundtrack`。`PlayAudioAt` は作品フレームをそのままサンプル位置へ変換し、開始オフセットを持たない | 今回未実装 |
| 25 | 背景画像・動画の移動拡縮と半透明トレース [G §12.2、12.3、12.5] | 部分実装 | `MenuView` に背景読込・表示・モデル半透明あり。背景だけを移動・拡縮するパラメータ／操作なし | 差分は今回未実装 |
| 26 | モデル・アクセサリ・エフェクトの描画順と自動 Z ソート [G §12.9] | 部分実装 | `DanceModels.ModelOrder` / `ApplyDrawOrder` にモデル順序変更あり。全対象の統合順序・自動 Z ソートなし | 差分は今回未実装 |
| 27 | 物理結果を毎フレームのキーへ焼き込み [H §13.7] | 未実装 | `RigSimulation.ConvertPhysicsKeys` は選択キーの物理 ON/OFF 変換で、シミュレーション結果のベイクではない | 今回未実装 |

## `.avapose` の扱い

公開のポーズ保存先は UTF-8 JSON の `AvaAvaDance pose 1`。モデル内の階層パスと実ボーン名、位置差分・回転差分、および名前付き表情ウェイトを保存する。AvaAvaDance の内部トラックの値を使い、MMD の座標単位・名前長・形式へ変換しない。

読込は階層パスで照合し、見つからないときだけ一意な実ボーン名で照合する。同名の候補が複数あるときは選ばず、未適用件数を表示する。表情も一意名で照合する。全値を適用前に検証し、一致する対象を `SetDraftValues` でまとめて適用するため、1回の Undo でボーンと表情を戻せる。キーフレームは自動登録せず、読込先の補間曲線と物理フラグを保つ。

保存する姿勢はモデル固有のローカル基準姿勢に対する差分であり、異なる骨格の自動リターゲットや体格サイジングは行わない。Humanoid の役割名が一致するだけで回転を別の骨軸へ転写する処理も追加していない。

既存の開発内部用 VMD / VPD / VAC 処理を、公開のポーズ・モーション・アクセサリ入口へ戻さない。

## 検証状況

この変更の作成時点で、Unityなしの時間伸縮ハーネス（14ケース・33,324アサーション）、再生範囲ハーネス（20ケース・103アサーション）、ポーズハーネス（18チェック）、`Tests/check_project.py` が通過した。時間伸縮の計算コードは netstandard2.1 / C#9 でコンパイルしている。ポーズ検査では実運用の処理にUnity代替型と.NETのJSON代替実装を組み合わせている。

追加した Unity / Windows プレイヤーの `-avaMenuSmoke` は未実行。この環境にはUnity Editorがなく、Unityによるコンパイル・画面表示・GPU描画・Windows音声デバイスの動作確認は含まない。C#9の構文解析と代替型による検査だけで、これらの成功を判断していない。

## 参照資料

公開された操作説明を比較したもので、原本のプログラムコードやバイナリ解析結果を実装に使用していない。

- [A: MMM 03.編集](https://sites.google.com/site/mikumikumoving/マニュアル/03-編集)
- [B: MMM 04.ボーン操作](https://sites.google.com/site/mikumikumoving/マニュアル/04-ボーン操作)
- [C: MMM 05.モーフ操作](https://sites.google.com/site/mikumikumoving/マニュアル/05-モーフ操作)
- [D: MMM 11.再生とAVI出力](https://sites.google.com/site/mikumikumoving/マニュアル/11-再生とavi出力)
- [E: MMM 07.カメラとライト](https://sites.google.com/site/mikumikumoving/マニュアル/07-カメラとライト)
- [F: MMM 10.オーディオ操作](https://sites.google.com/site/mikumikumoving/マニュアル/10-オーディオ操作)
- [G: MMM 12.表示関連](https://sites.google.com/site/mikumikumoving/マニュアル/12-表示関連)
- [H: MMM 13.物理演算](https://sites.google.com/site/mikumikumoving/マニュアル/13-物理演算)
- [MMD 公式 VPVP](https://sites.google.com/view/vpvp/): 配布、作者の解説動画、チュートリアルへの導線。詳細な最新メニュー全件の仕様書としては扱わない。

連番画像や透過アルファ出力については、この調査で参照した MMM マニュアル本文に明記がないため、「本家にある不足機能」という確定項目には数えていない。

## Follow-up: remaining editing tools

The follow-up implementation adds the seven gaps above without adding public MMD file formats:

- Audio start frame: File / Audio start frame (MMM File ribbon). Nonnegative project-frame offset, waveform placement, playback and AVI/MP4 audio alignment. Existing projects default to frame 0.
- Continuous viewport follow: View / Follow model or bone. Keeps the initial composition and follows the selected target's displacement; target stays fixed when editing selection changes. This is an editing view aid, not a camera animation. Movie export ignores it; still-image export retains the current view.
- Multiple lights: light panel selector, add/remove, per-light color and direction key registration. Up to eight directional lights including the original main light. Shadows remain owned by the main light; this does not introduce point/spot lights.
- Physics bake: Physics / Bake physics to keys (MMM Physics ribbon), current model, explicit inclusive range, 30 fps base keys, sequential warm-up from frame zero. Baked keys disable physics; missing boundary guard keys are added one frame outside the range to restore animation/physics state. Existing outside keys remain unchanged. Layers on target bones and overlapping IK chains are rejected rather than silently composed twice. Cancellation does not commit partial keys. The key budget is bounded and the whole edit is one Undo operation.
- Independent backdrop placement: Background / Background placement, image and video independently, viewport-relative translation and uniform scale. Stored with the project; camera/model keys are unchanged.
- Clear bookmarks: Edit menu, MMM bookmark ribbon, timeline menu and optional shortcut. Confirms removal, clears notes too, one Undo/Redo, empty set is a no-op.
- Permanent model placement: Edit / Model placement. A parent transform outside the animated hierarchy applies position (metres) and rotation (degrees), without rewriting existing keys. Saved per model, reversible with Undo/Redo; no automatic rig retargeting.

### Verification boundaries

Pure C# calculations are covered by `Tests/MediaPlacementHarness` and `Tests/PhysicsBakeHarness`; existing playback, retiming and pose harnesses remain applicable. `-avaMenuSmoke` also invokes additional-light, model-placement/follow and bookmark-clear checks using real Unity objects. These are not substitutes for a Unity compilation or interactive rendering/audio test. In particular, validate audio scheduling across range loops, additional lights with each avatar shader, physics bake with Bullet and PhysBone, backdrop export at different aspect ratios, save/reopen and Undo/Redo on the target Unity 2022.3.22f1 build.
