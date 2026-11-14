using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        Dictionary<MotionTrack,object> gestureBefore;
        int gestureFrame;

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
            gestureFrame = frame;
            IEnumerable<MotionTrack> tracks;
            if (!modelMode) tracks = new[]{selectedTrack is MotionTrack<AccessoryPose> ? selectedTrack : cameraTrack};
            else tracks = boneSelection.Select(b=>(MotionTrack)b.Track).Append(actor.selectedBone?.Track);
            gestureBefore = tracks.Where(t=>t!=null).Distinct().ToDictionary(t=>t,t=>t.CurrentValue);
        }

        void FinishDraftGesture()
        {
            if (gestureBefore == null) return;
            var changes = gestureBefore.Where(p=>!Equals(p.Value,p.Key.CurrentValue))
                .Select(p=>new KeyChange {Track=p.Key,Frame=gestureFrame,Before=p.Value,After=p.Key.CurrentValue,IsDraft=true}).ToArray();
            gestureBefore=null;
            if (changes.Length == 0) return;
            undo.Push(changes); redo.Clear(); RefreshKeyButtons();
        }

        void SetDraftValues(Dictionary<MotionTrack,object> values)
        {
            StopPlayback();
            gestureBefore=values.Keys.ToDictionary(t=>t,t=>t.CurrentValue); gestureFrame=frame;
            foreach (var pair in values) pair.Key.Restore(pair.Value);
            FinishDraftGesture(); InvalidateDynamics(); RefreshRigPreview(); SyncControls(); RedrawOverlay();
        }
    }
}
