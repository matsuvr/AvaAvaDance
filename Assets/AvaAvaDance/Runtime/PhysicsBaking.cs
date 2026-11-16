using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        bool physicsBaking, cancelPhysicsBake;
        const long MaxBakeKeys = 1000000;

        bool CanBakePhysics() => modelMode && actor?.avatar != null && actor.dynamicLinks.Count > 0 &&
            !physicsBaking && !exporting && !playing;

        void ShowPhysicsBakeDialog()
        {
            if (!CanBakePhysics()) return;
            ShowDialog("物理演算をキーフレームへ焼き込み", out var body);
            var first = new IntegerField("開始フレーム") { value = 0, isDelayed = true };
            var last = new IntegerField("終了フレーム") { value = LastFrame(), isDelayed = true };
            body.Add(first); body.Add(last);
            body.Add(new Label("現在モデルの物理ボーンを30 fpsのベースキーへ焼き込みます。\n0フレームから順次演算し、範囲内のキーを上書きします。\n焼き込みキーは物理OFF。境界の前後1フレームに復帰キーを追加します。\n既存の範囲外キーは変更しません（境界キー追加により周辺の補間は変わり得ます）。\nレイヤー併用・物理とIKの重複は対象外です。"));
            var error = new Label(); body.Add(error);
            Action(body, "焼き込み", true, () => {
                try {
                    var bones = PhysicsBakeBones(actor, first.value, last.value);
                    StartCoroutine(BakePhysics(actor, bones, first.value, last.value));
                } catch (Exception ex) { error.text = ex.Message; }
            });
            Action(body, "キャンセル", true, CloseDialog);
        }

        BoneBinding[] PhysicsBakeBones(Actor owner, int first, int last)
        {
            var joints = new HashSet<Transform>(owner.dynamicLinks.Select(link => link.Bone.Joint));
            foreach (var link in owner.dynamicLinks)
                foreach (var child in link.Children ?? Array.Empty<Transform>()) joints.Add(child);
            // Spring stretch can move a terminal child which has no dynamic link of its own.
            var bones = owner.bones.Where(b => joints.Contains(b.Joint)).Distinct().ToArray();
            if (bones.Length == 0) throw new ArgumentException("物理ボーンがありません。");
            PhysicsBakeRange.Validate(first, last, MaxFrame, bones.Length, MaxBakeKeys);
            if (bones.Any(b => b.Track.Layers.Count > 0))
                throw new ArgumentException("物理ボーンのレイヤーを先に統合してください。ベースへの二重適用を防ぐため処理を中止しました。");
            if (owner.ikChains.Any(c => bones.Any(b => b.Joint == c.Upper || b.Joint == c.Lower || b.Joint == c.Tip || b == c.Handle)))
                throw new ArgumentException("物理ボーンとIKチェーンが重複しています。IK構成を分離してから焼き込んでください。");
            return bones;
        }

        IEnumerator BakePhysics(Actor owner, BoneBinding[] bones, int first, int last)
        {
            if (physicsBaking || exporting) yield break;
            StopPlayback();
            ShowDialog("物理焼き込み中", out var body);
            modal.userData = (Action)(() => cancelPhysicsBake = true);
            var progress = new Label(); body.Add(progress);
            cancelPhysicsBake = false;
            Action(body, "キャンセル", true, () => cancelPhysicsBake = true);
            var tracks = sceneTracks.Concat(drawOrder.SelectMany(a => a.modelTracks)).Distinct().ToArray();
            var revisions = tracks.Select(t => t.Revision).ToArray();
            var drafts = tracks.Select(t => t.CurrentValue).ToArray();
            var actors = drawOrder.ToArray();
            var transforms = actors.Where(a => a.avatar != null).SelectMany(a => a.avatar.GetComponentsInChildren<Transform>(true))
                .Distinct().Select(t => (joint: t, position: t.localPosition, rotation: t.localRotation, scale: t.localScale)).ToArray();
            var oldClock = (previousClock, previousSample, renderSeconds, renderDelta, syncedSeconds, syncedDelta, outputDelta);
            float oldEvaluationTime = evaluationTime;
            var oldMode = physicsMode;
            int oldFrame = frame;
            var edits = new List<KeyChange>();
            var restPositions = new Dictionary<BoneBinding, Vector3>();
            bool complete = false;
            string failure = null;
            physicsBaking = true;
            try {
                // Read the track's immutable bind origin rather than deriving it from a
                // currently physics-displaced joint. Restore every preview immediately.
                foreach (var bone in bones) {
                    var value = bone.Track.Current;
                    bone.Track.Preview(BonePose.Rest);
                    restPositions.Add(bone, bone.Joint.localPosition);
                    bone.Track.Preview(value);
                    foreach (int boundary in PhysicsBakeRange.Guards(first, last, MaxFrame))
                        if (bone.Track.KeyAt(boundary) == null)
                            edits.Add(new KeyChange { Track = bone.Track, Frame = boundary, DocumentFrame = oldFrame,
                                After = bone.Track.SampleValue(boundary), ReplaceSource = true });
                }
                physicsMode = PhysicsMode.Trace;
                ClearBullet(); foreach (var model in actors) ClearSimulation(model);
                // Both backends integrate at 60 Hz. Visiting every frame also makes the
                // warm-up cancellable instead of seeking once across a large interval.
                for (int at = 0; at <= last && !cancelPhysicsBake; at++) {
                    try {
                        if (actors.Length != drawOrder.Count || !actors.SequenceEqual(drawOrder) ||
                            tracks.Where((t, i) => t.Revision != revisions[i]).Any())
                            throw new InvalidOperationException("演算中にモデルまたはモーションが変更されました。焼き込みは適用しません。");
                        EvaluateAnimation(at, true);
                        if (at >= first)
                            foreach (var bone in bones) {
                                var pose = new BonePose {
                                    Position = bone.Joint.localPosition - restPositions[bone],
                                    Rotation = Quaternion.Inverse(bone.RestRotation) * bone.Joint.localRotation,
                                    PhysicsOff = true
                                };
                                if (!FinitePose(pose)) throw new InvalidOperationException("物理演算の結果が不正です。焼き込みは適用しません。");
                                edits.Add(new KeyChange { Track = bone.Track, Frame = at, DocumentFrame = oldFrame,
                                    After = pose, ReplaceSource = true });
                            }
                        progress.text = (at < first ? "事前演算 " : "焼き込み ") + at + " / " + last;
                    } catch (Exception ex) { failure = ex.Message; break; }
                    if (at == last) complete = true;
                    if (at % 4 == 0) yield return null;
                }
            } finally {
                physicsMode = oldMode;
                ClearBullet(); foreach (var model in actors) if (model.avatar != null) ClearSimulation(model);
                // Evaluation does not change keys. Cancellation restores the original
                // frame and drafts; no partial bake is ever committed.
                for (int i = 0; i < tracks.Length; i++) tracks[i].Restore(drafts[i]);
                foreach (var item in transforms) if (item.joint != null) {
                    item.joint.localPosition = item.position; item.joint.localRotation = item.rotation; item.joint.localScale = item.scale;
                }
                evaluationTime = oldEvaluationTime;
                (previousClock, previousSample, renderSeconds, renderDelta, syncedSeconds, syncedDelta, outputDelta) = oldClock;
                physicsBaking = false;
                SyncVideo(oldFrame); ApplyView(); SyncControls(); RedrawOverlay();
                CloseDialog();
            }
            if (complete && !cancelPhysicsBake && failure == null) {
                if (!actors.SequenceEqual(drawOrder) || tracks.Where((t, i) => t.Revision != revisions[i]).Any()) {
                    status.text = "演算中にデータが変更されたため焼き込みを中止しました。"; yield break;
                }
                CommitKeys(edits);
                status.text = bones.Length + " 本の物理ボーンを " + first + "～" + last + " フレームへ焼き込みました（1回のUndoで復元）。";
            } else status.text = failure ?? "物理焼き込みをキャンセルしました。キーは変更していません。";
        }

        static bool FinitePose(BonePose pose) => FiniteVector(pose.Position) &&
            !float.IsNaN(pose.Rotation.x) && !float.IsInfinity(pose.Rotation.x) &&
            !float.IsNaN(pose.Rotation.y) && !float.IsInfinity(pose.Rotation.y) &&
            !float.IsNaN(pose.Rotation.z) && !float.IsInfinity(pose.Rotation.z) &&
            !float.IsNaN(pose.Rotation.w) && !float.IsInfinity(pose.Rotation.w);
        static bool FiniteVector(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x) &&
            !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
    }
}
