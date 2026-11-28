using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class TimeScaleTrack
        {
            internal MotionTrack Track;
            internal string Layer;
            internal KeyRetiming.Plan<object> Plan;
        }

        sealed class TimeScaleBatch
        {
            internal readonly List<TimeScaleTrack> Tracks = new List<TimeScaleTrack>();
            internal int InputCount => Tracks.Sum(t => t.Plan.InputCount);
            internal int OutputCount => Tracks.Sum(t => t.Plan.Destinations.Length);
            internal int MergedCount => Tracks.Sum(t => t.Plan.MergedCount);
            internal int OverwrittenCount => Tracks.Sum(t => t.Plan.OverwrittenCount);
            internal bool Changed => Tracks.Any(t => t.Plan.Changes.Length > 0);
            internal string Summary => "時間伸縮　" + InputCount + " 個 → " + OutputCount + " 個のキー　同位置で統合 " +
                MergedCount + " 個／選択外への上書き " + OverwrittenCount + " 個";
        }

        bool CanScaleKeyframes() => VisibleTracks.Any(t => t.EditFrames.Count > 0);

        TimeScaleBatch PlanTimeScale(IEnumerable<(MotionTrack track, int frame)> selection, int origin, double factor)
        {
            var result = new TimeScaleBatch();
            foreach (var group in selection.GroupBy(k => k.track))
            {
                var track = group.Key;
                bool baseTrack = track.Target == null;
                bool ReadKey(int at, out object key)
                {
                    key = track.EditKey(at);
                    return key != null;
                }
                result.Tracks.Add(new TimeScaleTrack { Track = track, Layer = track.ActiveLayer,
                    Plan = KeyRetiming.Scale<object>(ReadKey, group.Select(k => k.frame), origin, factor, MaxFrame, baseTrack) });
            }
            if (result.InputCount == 0) throw new ArgumentException("伸縮するキーを選択してください。");
            return result;
        }

        void ApplyTimeScale(TimeScaleBatch batch)
        {
            if (!batch.Changed) { status.text = "キーの時刻に変更はありません。"; return; }
            var edits = batch.Tracks.SelectMany(item => item.Plan.Changes.Select(change => new KeyChange {
                Track = item.Track, Layer = item.Layer, Frame = change.Frame, DocumentFrame = frame,
                After = change.HasAfter ? change.After : null
            })).ToArray();
            // Every destination is unique within a track. CommitKeys captures the whole
            // pre-edit state before writing, including keys overwritten by compression.
            CommitKeys(edits);
            selectedKeys.Clear();
            foreach (var item in batch.Tracks)
                foreach (int at in item.Plan.Destinations) selectedKeys.Add((item.Track, at));
            rangeSelected = true;
            RefreshTimelineLabels(); RefreshKeyButtons(); SyncCurve();
            status.text = batch.Summary;
        }

        void ShowTimeScaleDialog()
        {
            if (!CanScaleKeyframes()) return;
            StopPlayback();
            var selection = EditKeys().ToArray();
            var target = selectedTrack;
            var visible = VisibleTracks.ToArray();
            ShowDialog("フレームの時間伸縮", out var body);
            var scope = new DropdownField("対象", new List<string> {
                "選択キー", "選択トラックの編集対象", modelMode ? "現在モデルの全トラックの編集対象" : "現在シーンの全トラックの編集対象"
            }, rangeSelected && selection.Length > 0 ? 0 : 2) { name = "retime-scope" };
            var origin = new IntegerField("基準フレーム") { name = "retime-origin", isDelayed = true };
            var factor = new FloatField("時間倍率") { name = "retime-factor", value = 1, isDelayed = true };
            scope.tooltip = "各行の「編集対象」に選ばれたベース／レイヤーのキーを伸縮します。シーンにはカメラ・照明・影・重力・アクセサリを含みます。";
            origin.tooltip = "このフレームを中心に時間を伸縮します。レイヤー／クリップはソース内のフレーム番号です。";
            factor.tooltip = "2で時間が2倍、0.5で半分になります。";
            body.Add(scope); body.Add(origin); body.Add(factor);
            Label Note(string text) { var label = new Label(text); label.style.whiteSpace = WhiteSpace.Normal; body.Add(label); return label; }
            Note("ベースの0フレームは移動しません。同じ位置に重なるキーは、元の時刻が後のキーを使います。");
            if (visible.Any(t => t.Target != null))
                Note("レイヤーはソースのキーを伸縮します。共有するクリップにも反映されます。");
            var preview = Note(""); preview.name = "retime-preview";

            IEnumerable<(MotionTrack track, int frame)> Keys() => scope.index == 0 ? selection :
                (scope.index == 1 ? target == null ? Array.Empty<MotionTrack>() : new[] { target } : visible)
                    .SelectMany(t => t.EditFrames.Select(at => (t, at)));
            void DefaultOrigin() => origin.SetValueWithoutNotify(scope.index == 0 && selection.Length > 0 ? selection.Min(k => k.frame) : 0);

            var actions = Box(body, "actions", "row");
            var apply = Action(actions, "適用", true, () => {
                try {
                    // Rebuild from the current keys so preview never becomes an edit of
                    // stale values. The complete plan is validated before any mutation.
                    var batch = PlanTimeScale(Keys(), origin.value, factor.value);
                    ApplyTimeScale(batch); CloseDialog();
                } catch (ArgumentException error) { preview.text = error.Message; }
            });
            apply.name = "retime-apply";
            Action(actions, "キャンセル", true, CloseDialog);
            void Preview()
            {
                try {
                    var batch = PlanTimeScale(Keys(), origin.value, factor.value);
                    int first = batch.Tracks.Min(t => t.Plan.Destinations[0]);
                    int last = batch.Tracks.Max(t => t.Plan.Destinations[t.Plan.Destinations.Length - 1]);
                    preview.text = batch.Summary + "\n伸縮後 " + first + "～" + last + " フレーム";
                    apply.SetEnabled(true);
                } catch (ArgumentException error) { preview.text = error.Message; apply.SetEnabled(false); }
            }
            scope.RegisterValueChangedCallback(_ => { DefaultOrigin(); Preview(); });
            origin.RegisterValueChangedCallback(_ => Preview()); factor.RegisterValueChangedCallback(_ => Preview());
            DefaultOrigin(); Preview();
        }
    }
}
