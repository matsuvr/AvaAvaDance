# VSQ / VSQXから口の動きを生成

モデルを選択し、タイムラインを生成開始フレームに移動して、**表情 > VSQ / VSQXから口の動きを生成**でファイルを開きます。対応する口のモーフにキーを追加します。生成全体を1回の「元に戻す」で取り消せます。同じフレームに既存キーがある場合は置き換えます。

- `.vsq`: 従来のVOCALOID 2形式。
- `.vsqx`: VOCALOID 3の`vsq3`とVOCALOID 4の`vsq4`。XMLの名前空間を識別し、UTF-8、BOM、名前空間プレフィックスを扱います。
- VSQXは音符がある最初の歌唱トラックの全パートを使います。後続の歌唱トラック、オーディオトラック、ピッチ・歌唱表現は取り込みません。
- 発音記号の末尾の母音を使い、母音が取れない場合は歌詞のかなから推定します。対応モーフ名は`あ/い/う/え/お`、`a/i/u/e/o`、VRMの`aa/ih/ou/ee/oh`、`vrc.v_aa/vrc.v_ih/vrc.v_ou/vrc.v_e/vrc.v_oh`です。対応しない発音はスキップします。
- ノート開始時に強さ1、終了時に0のキーを登録します。子音ごとの口形や強さの推定は行いません。音声は別途WAVとして読み込みます。

## 実装で参照した公開資料

ヤマハのXSD本体は参照先URLから取得できなかったため、公開されている形式変換実装とVSQXのサンプルを照合しました。

- [OpenUtauのVSQXインポーター](https://github.com/openutau/OpenUtau/blob/master/OpenUtau.Core/Format/VSQx.cs): V3/V4の名前空間、項目名、BPMの100倍表現、パート相対時刻。
- [UtaFormatixのVSQXインポーター](https://github.com/sdercolin/utaformatix3/blob/master/core/src/main/kotlin/core/io/Vsqx.kt): プリメジャー内の拍子変更を含む開始時刻補正。
- [vsqxクレートのV3サンプル](https://docs.rs/crate/vsqx/0.1.0/source/src/test/v3.vsqx): VOCALOID 3のXML構造。

| 対象 | VOCALOID 3 | VOCALOID 4 |
| --- | --- | --- |
| ルート・名前空間末尾 | `vsq3` / `vsq3/` | `vsq4` / `vsq4/` |
| パート | `musicalPart` | `vsPart` |
| ティック位置 | `posTick` | `t` |
| ノート長 | `durTick` | `dur` |
| 歌詞・発音記号 | `lyric` / `phnms` | `y` / `p` |
| テンポ | `tempo/posTick`, `bpm` | `tempo/t`, `v` |
| 拍子の小節位置・分子・分母 | `posMes`, `nume`, `denomi` | `m`, `nu`, `de` |

名前空間は`http://www.yamaha.co.jp/vocaloid/schema/vsq3/`または`http://www.yamaha.co.jp/vocaloid/schema/vsq4/`です。`masterTrack/resolution`を四分音符あたりのティック数として、テンポ変更を区間積分し、`preMeasure`の終了時刻を引いて30fpsのフレームに変換します。パート位置はプロジェクト相対、ノート位置はパート相対として加算します。

インポーターの方針として、ノートが`playTime`を越える部分は切り、パート外から始まるノートは無視します。生成位置が負のフレームにかかる音符はフレーム0で切り、フレーム0以前に終了する音符は無視します。壊れたXML、不正な数値、フレーム上限超過などはキーを変更する前に中止します。DTD・外部参照を禁止し、ファイルは16MiBまでです。全XSDの検証は行いません。

## 検証

```powershell
dotnet run --project Tests/VsqxHarness/VsqxHarness.csproj
Builds/Windows/AvaAvaDance.exe -batchmode -avaSmokePackage Tests/triangle.unitypackage -avaSmokeOutput Builds/Checks/Vsqx -avaMenuSmoke
```

前者はV3/V4の時刻、拍子変更、テンポ変更、複数パート、文字コード、壊れた入力を検査します。後者は既存VSQの回帰確認と、VSQXのメニューからの読み込み、5母音、開始フレーム、Undo/Redo、失敗時のキー不変を検査します。
