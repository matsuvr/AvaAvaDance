using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ava.Extensions;
using Ava.MotionSizing;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        NativeSizingOptions motionSizingOptions = new NativeSizingOptions();
        CancellationTokenSource motionSizingCancellation;
        VisualElement motionSizingDialog;

        bool CanSizeMotion() => motionSizingCancellation == null && !importing && !exporting &&
            drawOrder.Any(SizingModel);

        static bool SizingModel(Actor model) => model.avatar != null &&
            model.rigProfile?.joints?.Length > 0 && model.bones.Count > 0;

        void OpenMotionSizing()
        {
            if (!CanSizeMotion()) return;
            var models = drawOrder.Where(SizingModel).ToArray();
            int selected = Math.Max(0, Array.IndexOf(models, actor));
            var dialog = ShowDialog("モーションサイジング", out var body);
            motionSizingDialog = dialog;
            var scroll = body.GetFirstAncestorOfType<ScrollView>();
            scroll.AddToClassList("motion-sizing-dialog");
            scroll.style.width = 560;
            scroll.style.maxWidth = new Length(95, LengthUnit.Percent);
            dialog.RegisterCallback<DetachFromPanelEvent>(_ => {
                if (motionSizingDialog != dialog) return;
                motionSizingDialog = null;
                if (motionSizingCancellation != null) {
                    motionSizingCancellation.Cancel();
                    if (this != null && status != null) status.text = "サイジングを中止しました。";
                }
            });

            var fields = new VisualElement { name = "sizing-options" }; body.Add(fields);
            T Field<T>(T field) where T : VisualElement
            {
                fields.Add(field);
                var label = field.Q<Label>(className: "unity-base-field__label");
                if (label != null) { label.style.minWidth = 190; label.style.width = 190; }
                return field;
            }
            void Heading(string title)
            {
                var label = new Label(title);
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.marginTop = 7; fields.Add(label);
            }
            FloatField Number(string name, string label, float value) =>
                Field(new FloatField(label) { name = name, value = value, isDelayed = true });
            Toggle Check(string name, string label, bool value) =>
                Field(new Toggle(label) { name = name, value = value });

            var labels = models.Select(model => model.label).ToList();
            var source = Field(new DropdownField("モーションのあるモデル", labels, selected) { name = "sizing-source" });
            var target = Field(new DropdownField("適用先モデル", labels, selected) { name = "sizing-target" });
            var layer = new Label("編集対象：ベース") { name = "sizing-layer" };
            fields.Add(layer);
            int LastFrame(Actor model) => model.modelTracks.Max(track => track.Frames.Last());
            var first = Field(new IntegerField("開始フレーム") { name = "sizing-first-frame", value = 0, isDelayed = true });
            var last = Field(new IntegerField("終了フレーム") { name = "sizing-last-frame", value = LastFrame(models[selected]), isDelayed = true });
            var rangeButtons = Box(fields, "sizing-range", "row");
            Action(rangeButtons, "全フレーム", true, () => { first.value = 0; last.value = LastFrame(models[source.index]); });
            var rangeKeys = selectedKeys.Where(key => key.track.Target == null).ToArray();
            int[] SelectedFrames() => rangeKeys.Where(key => models[source.index].modelTracks.Contains(key.track))
                .Select(key => key.frame).ToArray();
            var selectedFrames = SelectedFrames();
            var selection = Action(rangeButtons, "選択範囲", rangeSelected && selectedFrames.Length > 0, () => {
                selectedFrames = SelectedFrames();
                if (selectedFrames.Length == 0) return;
                first.value = selectedFrames.Min(); last.value = selectedFrames.Max();
            });
            if (rangeSelected && selectedFrames.Length > 0) { first.value = selectedFrames.Min(); last.value = selectedFrames.Max(); }
            source.RegisterValueChangedCallback(_ => {
                first.value = 0; last.value = LastFrame(models[source.index]);
                selection.SetEnabled(rangeSelected && SelectedFrames().Length > 0);
            });

            var previous = motionSizingOptions;
            Heading("移動・足");
            var movement = Number("sizing-movement", "移動倍率（XZ）", previous.MovementScale);
            var feet = Check("sizing-feet", "足・つま先の位置を合わせる", previous.MatchFeet);
            var footOffset = Number("sizing-foot-offset", "足幅補正（片足・m）", previous.FootOffset);
            var floorHeight = Number("sizing-floor-height", "床の高さ（m）", previous.FloorHeight);
            Heading("腕・手");
            var hands = Check("sizing-hands", "手首の位置合わせ", previous.AlignHands);
            var fingers = Check("sizing-fingers", "指先の位置合わせ", previous.AlignFingers);
            var avoid = Check("sizing-avoid-body", "身体との接触回避", previous.AvoidBody);
            var twist = Check("sizing-twist", "腕・手首の捩り分散", previous.Twist);
            var distance = Number("sizing-contact-distance", "接触判定距離（m）", previous.ContactDistance);
            Heading("表情・カメラ");
            var morphs = Field(new TextField("表情対応") {
                name = "sizing-morph-rules", value = previous.MorphRules ?? "",
                tooltip = "元の表情名:先の表情名:倍率（複数は ; で区切る）"
            });
            var camera = Check("sizing-camera", "カメラも体格に合わせる", previous.Camera);
            var report = new Label { name = "sizing-report" }; report.style.whiteSpace = WhiteSpace.Normal;
            body.Add(report);
            var actions = Box(body, "sizing-actions", "row");
            Button run = null;
            var close = Action(actions, "閉じる", true, () => {
                if (motionSizingCancellation != null) {
                    motionSizingCancellation.Cancel(); status.text = "サイジングを中止しました。";
                }
                if (modal == dialog) CloseDialog();
            });
            run = Action(actions, "サイジング実行", true, Run);
            run.name = "sizing-run"; close.name = "sizing-close";

            async void Run()
            {
                if (motionSizingCancellation != null || modal != dialog) return;
                CancellationTokenSource cancellation = null;
                bool activity = false;
                try
                {
                    if (first.value < 0 || last.value < first.value || last.value > MaxFrame)
                        throw new ArgumentException("開始・終了フレームを確認してください。");
                    if (!float.IsFinite(movement.value) ||
                        !float.IsFinite(footOffset.value) || !float.IsFinite(floorHeight.value) ||
                        !float.IsFinite(distance.value))
                        throw new ArgumentException("倍率・距離・高さには有限の数値を指定してください。");
                    if (movement.value < 0 || distance.value < 0)
                        throw new ArgumentException("移動倍率と接触判定距離は0以上で指定してください。移動倍率0でXZ移動を止められます。");
                    var sourceModel = models[source.index]; var targetModel = models[target.index];
                    if (!drawOrder.Contains(sourceModel) || !drawOrder.Contains(targetModel))
                        throw new InvalidOperationException("対象モデルが変更されています。サイジングを開き直してください。");
                    var options = new NativeSizingOptions {
                        MovementScale = movement.value, FootOffset = footOffset.value, FloorHeight = floorHeight.value,
                        ContactDistance = distance.value, MatchFeet = feet.value, AlignHands = hands.value,
                        AlignFingers = fingers.value, AvoidBody = avoid.value, Twist = twist.value,
                        Camera = camera.value, MorphRules = morphs.value
                    };
                    var request = new NativeSizingRequest {
                        SourceOwner = sourceModel.id, TargetOwner = targetModel.id,
                        FirstFrame = first.value, LastFrame = last.value, Options = options,
                        SourceToScene = SizingSceneMatrix(sourceModel.rigToScene),
                        TargetToScene = SizingSceneMatrix(targetModel.rigToScene)
                    };
                    // All Unity objects are read on the UI thread. The worker owns only SDK values.
                    var snapshot = SnapshotMotion();
                    var sourceRig = JsonUtility.FromJson<RigProfile>(JsonUtility.ToJson(sourceModel.rigProfile));
                    var targetRig = JsonUtility.FromJson<RigProfile>(JsonUtility.ToJson(targetModel.rigProfile));
                    var sourceRigIdentity = sourceModel.rigProfile; var targetRigIdentity = targetModel.rigProfile;
                    motionSizingOptions = options;
                    cancellation = new CancellationTokenSource(); motionSizingCancellation = cancellation;
                    fields.SetEnabled(false); run.SetEnabled(false); close.text = "中止";
                    report.text = status.text = "モーションをサイジング中…";
                    AcquireActivity(); activity = true;
                    var result = await Task.Run(() => NativeMotionSizing.Calculate(snapshot, sourceRig, targetRig, request, cancellation.Token), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (this == null || modal != dialog || motionSizingDialog != dialog) return;
                    if (!drawOrder.Contains(sourceModel) || !drawOrder.Contains(targetModel) ||
                        sourceModel.rigProfile != sourceRigIdentity || targetModel.rigProfile != targetRigIdentity)
                        throw new InvalidOperationException("対象モデルが変更されています。もう一度実行してください。");
                    ValidateSizingInputs(snapshot, request);
                    // One application is one undo operation, including optional morph/camera edits.
                    ApplyMotionChanges(result.Document, result.Edits);
                    if (result.Edits.Length > 0) {
                        ChooseMode(targetModel.label);
                        var edited = result.Edits.FirstOrDefault(edit => edit.Owner == targetModel.id);
                        if (edited != null) SelectTrack(targetModel.modelTracks.FirstOrDefault(track => track.Id == edited.Track));
                        SetFrame(Mathf.Clamp(frame, request.FirstFrame, request.LastFrame));
                    }
                    string message = result.Edits.Length == 0 ? "サイジング　変更するキーはありません。" :
                        "サイジング　" + sourceModel.label + " → " + targetModel.label + "　" + result.Edits.Length + " キー";
                    if (result.Messages?.Length > 0) {
                        message += "　" + string.Join(" / ", result.Messages);
                        Debug.Log(string.Join("\n", result.Messages));
                    }
                    status.text = message;
                    motionSizingCancellation = null;
                    CloseDialog();
                }
                catch (OperationCanceledException)
                {
                    if (this != null && modal == dialog) report.text = status.text = "サイジングを中止しました。";
                }
                catch (Exception ex)
                {
                    if (this != null && modal == dialog) report.text = status.text = "サイジング中止　" + ex.Message;
                }
                finally
                {
                    if (motionSizingCancellation == cancellation) motionSizingCancellation = null;
                    cancellation?.Dispose();
                    if (this != null) {
                        if (activity) ReleaseActivity();
                        if (modal == dialog) { fields.SetEnabled(true); run.SetEnabled(true); close.text = "閉じる"; }
                    }
                }
            }
        }

        // Unity multiplies column vectors; System.Numerics multiplies row vectors.
        static System.Numerics.Matrix4x4 SizingSceneMatrix(Matrix4x4 matrix) => new System.Numerics.Matrix4x4(
            matrix.m00, matrix.m10, matrix.m20, matrix.m30,
            matrix.m01, matrix.m11, matrix.m21, matrix.m31,
            matrix.m02, matrix.m12, matrix.m22, matrix.m32,
            matrix.m03, matrix.m13, matrix.m23, matrix.m33);

        void ValidateSizingInputs(MotionSnapshot snapshot, NativeSizingRequest request)
        {
            if (snapshot.Document != documentGeneration)
                throw new InvalidOperationException("プロジェクトが切り替わっています。");
            var current = EditableTracks().ToDictionary(entry => (entry.owner, entry.track.Id), entry => entry.track);
            foreach (var track in snapshot.Tracks.Where(track => track.Owner == request.SourceOwner ||
                track.Owner == request.TargetOwner || request.Options.Camera && track.Channel == TrackChannel.Camera))
                if (!current.TryGetValue((track.Owner, track.Id), out var live) || live.Revision != track.Revision)
                    throw new InvalidOperationException("モーションが変更されています。もう一度実行してください。");
        }
    }
}
