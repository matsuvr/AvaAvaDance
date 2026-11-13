using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        bool localAxes = true;
        readonly List<BoneBinding> boneSelection = new List<BoneBinding>();
        void SyncAxes()
        {
            foreach (var handle in axisTools.Query<Label>().ToList())
                handle.tooltip = (localAxes ? "ローカル " : "グローバル ") + handle.text + "軸";
        }

        void SelectBoneSet(bool unregistered)
        {
            bool Changed(BoneBinding b) {
                var value=b.Track.Current;var saved=b.Track.Sample(frame);
                return (value.Position-saved.Position).sqrMagnitude>1e-12f || Quaternion.Angle(value.Rotation,saved.Rotation)>.001f || value.PhysicsOff!=saved.PhysicsOff;
            }
            var selected = actor.bones.Where(b => !unregistered || Changed(b)).ToArray();
            if (selected.Length > 0) SelectTrack(selected[0].Track);
            boneSelection.Clear(); foreach (var b in selected) boneSelection.Add(b);
            RedrawOverlay(); status.text = selected.Length + " ボーンを選択";
        }

        void RegisterSelection()
        {
            if (modelMode && boneSelection.Count > 0 && selectedTrack is MotionTrack<BonePose>)
                CommitKeys(boneSelection.SelectMany(b=>RegistrationEdits(b.Track)));
            else if(selectedRows.Count>1)CommitKeys(selectedRows.SelectMany(RegistrationEdits));
            else RegisterKey(selectedTrack);
        }

        MotionTrack OriginalTarget(MotionTrack source)
        {
            if(VisibleTracks.Contains(source))return source;
            if(!modelMode)return null;
            var owner=drawOrder.Find(a=>a.modelTracks.Contains(source));
            if(owner==null)return null;
            var bone=owner.bones.Find(b=>b.Track==source);
            if(bone==null || (owner.modelGuid==actor.modelGuid && owner.prefabGuid==actor.prefabGuid))
                return actor.modelTracks.Find(t=>t.GetType()==source.GetType() && t.Name==source.Name);

            if(owner.humanBones!=null)
                foreach(var role in owner.humanBones.Where(p=>p.Value==bone.Joint)){
                    if(actor.humanBones!=null && actor.humanBones.TryGetValue(role.Key,out var mapped))
                        return actor.bones.Find(b=>b.Joint==mapped)?.Track;
                    string alias=Ava.Extensions.RigProfile.MotionAlias(role.Key.ToString());
                    var candidates=actor.bones.Where(b=>b.Joint.name==alias).ToArray();
                    if(candidates.Length==1)return candidates[0].Track;
                }
            if(actor.humanBones!=null)
                foreach(var role in actor.humanBones)
                    if(Ava.Extensions.RigProfile.MotionAlias(role.Key.ToString())==bone.Joint.name)
                        return actor.bones.Find(b=>b.Joint==role.Value)?.Track;
            // Match the authored hierarchy as well as the leaf name (two outfits can both have a "tip").
            bool SameBranch(Transform a,Transform b){
                while(a!=owner.avatar.transform && b!=actor.avatar.transform){
                    if(a==null || b==null || a.name!=b.name)return false;
                    a=a.parent;b=b.parent;
                }
                return a==owner.avatar.transform && b==actor.avatar.transform;
            }
            var matches=actor.bones.Where(b=>SameBranch(bone.Joint,b.Joint)).ToArray();
            return matches.Length==1?matches[0].Track:null;
        }

        bool PreparePaste(bool mirror,List<KeyChange> edits=null,List<string> missing=null)
        {
            if(keyClipboard.Count==0)return false;
            int first=keyClipboard[0].Frame;bool found=false;
            foreach(var group in keyClipboard.GroupBy(k=>k.Track)){
                var track=OriginalTarget(group.Key);
                if(mirror && track is MotionTrack<BonePose>)track=MirroredTrack(track);
                if(track==null || !track.LocalTime(frame,out int local)){missing?.Add(group.Key.Name);continue;}
                foreach(var key in group){
                    int at=local+key.Frame-first;if(at<0 || at>MaxFrame)return false;
                    if(edits!=null){
                        var value=mirror && key.After is BonePose pose?(object)MirrorPose(pose):key.After;
                        edits.Add(new KeyChange{Track=track,Layer=track.ActiveLayer,Frame=at,DocumentFrame=frame,After=value,
                            ReplaceSource=true,AfterSource=track==key.Track?key.AfterSource:null});
                    }
                    found=true;
                }
            }
            return found;
        }
        bool CanPasteOriginal()=>PreparePaste(false);
        void PasteOriginalKeys(bool mirror)
        {
            var edits=new List<KeyChange>();var missing=new List<string>();
            if(PreparePaste(mirror,edits,missing))CommitKeys(edits);
            if(missing.Count>0)status.text="貼付先を特定できなかった行　"+missing.Count+"件　"+string.Join("、",missing);
        }

        void NumericTransform()
        {
            ShowDialog("ボーン・カメラ数値入力", out var body);
            var fields = new List<FloatField>();
            for (int i = 0; i < (modelMode ? 6 : 7); i++)
            {
                var f = new FloatField(i < 3 ? "位置 " + "XYZ"[i] : i < 6 ? "角度 " + "XYZ"[i-3] : "距離")
                    { value = transformFields[i].value };
                fields.Add(f); body.Add(f);
            }
            Action(body, "OK", true, () => {
                if (fields.Any(f => !float.IsFinite(f.value))) return;
                for (int i = 0; i < fields.Count; i++) EditTransform(i, fields[i].value);
                CloseDialog();
            });
            Action(body, "キャンセル", true, CloseDialog);
        }

        void DeleteUnusedKeys()
        {
            var edits = new List<KeyChange>();
            foreach (var track in VisibleTracks)
            {
                var keys = track.Frames;
                for (int i = 1; i < keys.Count; i++)
                {
                    var p = track.SnapshotKey(keys[i]);
                    if (Equals(track.SnapshotKey(keys[i - 1]), p) && (i + 1 == keys.Count || Equals(track.SnapshotKey(keys[i + 1]), p)))
                        edits.Add(new KeyChange { Track = track, Frame = keys[i] });
                }
            }
            CommitKeys(edits); status.text = edits.Count + " 個の不要フレームを削除";
        }

        static FloatField[] VectorFields(VisualElement body, string name, float initial)
        {
            var fields = new FloatField[3];
            for (int i = 0; i < 3; i++) {
                fields[i] = new FloatField(name + " " + "XYZ"[i]) { value = initial }; body.Add(fields[i]);
            }
            return fields;
        }
        static Vector3 Values(FloatField[] f) => new Vector3(f[0].value, f[1].value, f[2].value);

        void AdjustKeys(Command command)
        {
            var keys = EditKeys().ToArray();
            if (command == Command.CenterBias) {
                var center = actor.bones.Find(b => b.Track.Name == "センター") ?? actor.bones.Find(b => b.Track.Name.Equals("hips", StringComparison.OrdinalIgnoreCase));
                if (center == null) { status.text = "センターボーンがありません。"; return; }
                keys = center.Track.Frames.Select(f => ((MotionTrack)center.Track, f)).ToArray();
            }
            bool morphs = command == Command.AdjustMorphs, centerOnly = command == Command.CenterBias;
            var title = MmdMenu.Bar.SelectMany(g => g.Children).First(e => e.Id == command).Label(englishMenus);
            ShowDialog(title, out var body);
            var scale = morphs ? new FloatField("表情 倍率") { value = 1 } : null;
            if (morphs) body.Add(scale);
            var pos = morphs ? null : VectorFields(body, centerOnly ? "位置 加算" : "位置 倍率", centerOnly ? 0 : 1);
            var rot = morphs || centerOnly ? null : VectorFields(body, "角度 倍率", 1);
            var offset = morphs || centerOnly ? null : VectorFields(body, "位置 加算", 0);
            var angleOffset = morphs || centerOnly ? null : VectorFields(body, "角度 加算", 0);
            Action(body, "OK", true, () => {
                Vector3 Adjust(Vector3 value, FloatField[] factor, FloatField[] add) =>
                    Vector3.Scale(value, Values(factor)) + Values(add);
                var edits = new List<KeyChange>();
                foreach (var (track, f) in keys) {
                    object value = track.KeyAt(f);
                    if (value is float weight && morphs) value = track.WithCurve(f, Mathf.Clamp01(weight * scale.value));
                    else if (value is BonePose bonePose && (command == Command.AdjustBones || centerOnly)) {
                        bonePose.Position = centerOnly ? bonePose.Position + Values(pos) : Adjust(bonePose.Position, pos, offset);
                        if (!centerOnly) bonePose.Rotation = Quaternion.Euler(Adjust(SignedAngles(bonePose.Rotation), rot, angleOffset));
                        value = bonePose;
                    }
                    else if (value is CameraPose cp && command == Command.AdjustCamera) {
                        cp.Center = Adjust(cp.Center, pos, offset); cp.Angles = Adjust(cp.Angles, rot, angleOffset); value = cp;
                    } else continue;
                    if (!ValidPose(value)) { status.text = "補正値が範囲外です。"; return; }
                    edits.Add(new KeyChange { Track = track, Frame = f, After = value });
                }
                CommitKeys(edits); CloseDialog();
            });
            Action(body, "キャンセル", true, CloseDialog);
        }

        void DeleteMorphGroup(int group)
        {
            CommitKeys(morphGroups[group].Tracks.SelectMany(track => track.Frames.Select(f =>
                new KeyChange { Track = track, Frame = f, After = f == 0 ? (object)0f : null })));
        }

        void ShiftLipFrames()
        {
            ShowDialog("リップフレーム時間シフト", out var body);
            var amount = new IntegerField("移動フレーム"); body.Add(amount);
            var error = new Label(); body.Add(error);
            Action(body, "OK", true, () => {
                var edits = new List<KeyChange>();
                foreach (var track in morphGroups[1].Tracks) {
                    var keys = track.Frames.Where(f => f > 0).ToArray();
                    if (keys.Any(f => (long)f + amount.value < 1 || (long)f + amount.value > MaxFrame))
                    { error.text = "移動先が範囲外です。"; return; }
                    var result = keys.ToDictionary(f => f, _ => (object)null);
                    foreach (int f in keys) result[f + amount.value] = track.SnapshotKey(f);
                    edits.AddRange(result.Select(p => new KeyChange { Track = track, Frame = p.Key, After = p.Value,ReplaceSource=true,AfterSource=p.Value==null?null:track.SourceAt(p.Key-amount.value) }));
                }
                CommitKeys(edits); CloseDialog();
            });
            Action(body, "キャンセル", true, CloseDialog);
        }

        MotionTrack<float> BlinkTrack() => morphGroups[0].Tracks.Find(t => t.Name.Contains("まばたき") ||
            t.Name.IndexOf("blink", StringComparison.OrdinalIgnoreCase) >= 0);

        void RandomBlink()
        {
            ShowDialog("まばたきランダム登録", out var body);
            var first = new IntegerField("開始フレーム") { value = frame };
            var last = new IntegerField("終了フレーム") { value = Mathf.Max(frame, LastFrame()) };
            var interval = new IntegerField("平均間隔") { value = 105 };
            body.Add(first); body.Add(last); body.Add(interval);
            Action(body, "登録", true, () => {
                if (first.value < 0 || last.value > MaxFrame || last.value < first.value || interval.value < 5 || interval.value > MaxFrame) return;
                var track = BlinkTrack(); var edits = new List<KeyChange>(); var rand = new System.Random();
                for (int i = first.value; i + 5 <= last.value; i += rand.Next(Math.Max(6, interval.value / 2), interval.value * 2 + 1))
                    foreach (int delta in new[] { 0, 2, 5 })
                        edits.Add(new KeyChange { Track = track, Frame = i + delta, After = delta == 2 ? 1f : 0f });
                CommitKeys(edits); CloseDialog();
            });
            Action(body, "キャンセル", true, CloseDialog);
        }
    }
}
