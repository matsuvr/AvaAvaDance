using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        Dictionary<MotionTrack,object> gestureBefore;
        int gestureFrame;
        long gestureDocument;
        bool gestureAutoKey, autoKey;
        VisualElement draftSlider;
        MotionTrack draftSliderTrack;
        int draftSliderPointer;
        readonly Dictionary<MotionTrack, (long revision, string layer, string clip)> gestureContexts = new();

        MotionTrack<BonePose> MirroredTrack(MotionTrack source)
        {
            var mapped=actor.humanBones?.FirstOrDefault(p=>actor.bones.Any(b=>b.Track==source && b.Joint==p.Value));
            if(mapped.HasValue && mapped.Value.Value!=null)
            {
                string role=mapped.Value.Key.ToString();
                string other=role.StartsWith("Left")?"Right"+role.Substring(4):role.StartsWith("Right")?"Left"+role.Substring(5):role;
                if(Enum.TryParse<HumanBodyBones>(other,out var id) && actor.humanBones.TryGetValue(id,out var joint))
                    return actor.bones.Find(b=>b.Joint==joint)?.Track;
            }
            string name = source.Name;
            string target = name.StartsWith("左") ? "右"+name.Substring(1) : name.StartsWith("右") ? "左"+name.Substring(1) :
                name.StartsWith("Left",StringComparison.OrdinalIgnoreCase) ? "Right"+name.Substring(4) :
                name.StartsWith("Right",StringComparison.OrdinalIgnoreCase) ? "Left"+name.Substring(5) : name;
            return actor.bones.Find(b=>b.Track.Name.Equals(target,StringComparison.OrdinalIgnoreCase))?.Track;
        }

        static BonePose MirrorPose(BonePose pose)
        {
            pose.Position.x *= -1; pose.Rotation.y *= -1; pose.Rotation.z *= -1;
            return pose;
        }

        void PushOperation(MotionTrack owner, Action<MotionTrack,bool> apply)
        {
            undo.Push(new[]{new KeyChange {Track=owner, Frame=frame, Replay=apply}});
            redo.Clear(); RefreshKeyButtons();
        }

        void BeginDraftGesture()
        {
            IEnumerable<MotionTrack> tracks;
            if (!modelMode) tracks = new[]{selectedTrack is MotionTrack<AccessoryPose> ? selectedTrack : cameraTrack};
            else tracks = boneSelection.Select(b=>(MotionTrack)b.Track).Append(actor.selectedBone?.Track);
            BeginDraftGesture(tracks, true);
        }

        void BeginDraftGesture(IEnumerable<MotionTrack> tracks, bool allowAutoKey)
        {
            gestureFrame = frame; gestureDocument = documentGeneration;
            gestureAutoKey = autoKey && allowAutoKey;
            gestureBefore = new Dictionary<MotionTrack, object>();
            gestureContexts.Clear();
            foreach (var track in tracks.Where(t => t != null)) CaptureGestureTrack(track);
        }

        void CaptureGestureTrack(MotionTrack track)
        {
            if (gestureBefore == null || gestureBefore.ContainsKey(track)) return;
            gestureBefore.Add(track, track.CurrentValue);
            gestureContexts.Add(track, (track.Revision, track.ActiveLayer, track.ActiveClip));
        }

        void FinishDraftGesture()
        {
            if (gestureBefore == null) return;
            var before = gestureBefore;
            gestureBefore = null;
            if (gestureDocument != documentGeneration || gestureFrame != frame) return;
            var changed = before.Where(p => !Equals(p.Value, p.Key.CurrentValue)).ToArray();
            if (gestureAutoKey && !importing && !exporting && !physicsBaking)
            {
                var edits = new List<KeyChange>();
                var drafts = new List<KeyChange>();
                foreach (var pair in changed)
                {
                    var track = pair.Key;
                    if (gestureContexts[track] != (track.Revision, track.ActiveLayer, track.ActiveClip)) continue;
                    KeyChange[] keys = CanRegisterAt(track, frame) && track.HasDraft(frame)
                        ? RegistrationEdits(track).ToArray() : Array.Empty<KeyChange>();
                    if (keys.Length == 0) drafts.Add(new KeyChange {
                        Track = track, Frame = frame, Before = pair.Value, After = track.CurrentValue, IsDraft = true
                    });
                    else foreach (var key in keys)
                    {
                        var edit = key;
                        edit.DocumentFrame = frame;
                        if (edit.PreviousDraft != null) edit.PreviousDraft = pair.Value;
                        edits.Add(edit);
                    }
                }
                CommitKeys(edits, drafts);
                return;
            }
            var changes = changed
                .Select(p=>new KeyChange {Track=p.Key,Frame=gestureFrame,Before=p.Value,After=p.Key.CurrentValue,IsDraft=true}).ToArray();
            if (changes.Length == 0) return;
            undo.Push(changes); redo.Clear(); RefreshKeyButtons();
        }

        void BindDraftSlider(VisualElement control, Func<MotionTrack> getTrack, Func<bool> allowAutoKey = null)
        {
            void Begin(int pointerId)
            {
                var track = getTrack();
                if (track == null || !CanEditChannels) return;
                StopPlayback();
                draftSlider = control; draftSliderTrack = track; draftSliderPointer = pointerId;
                BeginDraftGesture(new[] { track }, allowAutoKey?.Invoke() != false);
            }
            control.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) Begin(e.pointerId); }, TrickleDown.TrickleDown);
            if (NativeDesktopActive) desktopDraftGestures.Add(control, () => Begin(PointerId.mousePointerId));
            void Release(int id)
            {
                if (draftSlider != control || draftSliderPointer != id) return;
                control.schedule.Execute(() => {
                    if (draftSlider == control && draftSliderPointer == id) FinishDraftSlider();
                });
            }
            control.RegisterCallback<PointerUpEvent>(e => Release(e.pointerId), TrickleDown.TrickleDown);
            control.RegisterCallback<PointerCaptureOutEvent>(e => Release(e.pointerId));
            control.RegisterCallback<DetachFromPanelEvent>(_ => {
                if (draftSlider != control) return;
                draftSlider = null; draftSliderTrack = null; gestureBefore = null;
                gestureContexts.Clear();
            });
        }

        void FinishDraftSlider()
        {
            if (draftSlider == null) return;
            draftSlider = null; draftSliderTrack = null;
            FinishDraftGesture();
        }

        void EditDraftSlider(VisualElement control, MotionTrack track, object value, bool allowAutoKey = true)
        {
            if (draftSlider != control)
            {
                SetDraftValues(new Dictionary<MotionTrack, object> { [track] = value }, allowAutoKey);
                return;
            }
            if (draftSliderTrack != track || gestureBefore == null || frame != gestureFrame ||
                documentGeneration != gestureDocument || !gestureContexts.TryGetValue(track, out var context) ||
                context != (track.Revision, track.ActiveLayer, track.ActiveClip)) return;
            track.Restore(value);
            RefreshDraftPreview();
        }

        void SetDraftValues(Dictionary<MotionTrack,object> values, bool allowAutoKey = false)
        {
            StopPlayback();
            BeginDraftGesture(values.Keys, allowAutoKey);
            foreach (var pair in values) pair.Key.Restore(pair.Value);
            FinishDraftGesture();
            RefreshDraftPreview();
        }

        void RefreshDraftPreview()
        {
            InvalidateDynamics(); RefreshRigPreview(); SyncControls(); RedrawOverlay();
        }
    }
}
