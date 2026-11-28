using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const int DistanceChannel = 6, ScaleChannel = 7;
        readonly HashSet<int> channelSelection = new();
        MotionTrack channelOwner;
        VisualElement channelStatus;
        int channelAnchor = -1;

        static bool HasNumericChannels(MotionTrack track) => track is MotionTrack<BonePose> ||
            track is MotionTrack<CameraPose> || track is MotionTrack<AccessoryPose>;

        bool ChannelIsEditable(MotionTrack track, int axis) =>
            HasNumericChannels(track) && (axis < 6 && CanRegisterAt(track, frame) ||
            track.Target == null && (axis == DistanceChannel && track == cameraTrack ||
                axis == ScaleChannel && track is MotionTrack<AccessoryPose>));

        static bool CanRegisterAt(MotionTrack track, int at)
        {
            if (track == null || track.Target?.muted == true || !track.LocalTime(at, out _)) return false;
            var layer = track.Target;
            if (layer == null || layer.clips.Count == 0) return true;
            var clip = layer.clips.Find(c => c.id == track.ActiveClip);
            return clip != null && layer.Map(clip, at, out _, out float weight) && weight > .000001f;
        }

        float ChannelValue(object pose, int axis) => pose switch {
            BonePose bone => axis < 3 ? bone.Position[axis] / BonePositionUnit : SignedAngles(bone.Rotation)[axis - 3],
            CameraPose cam => axis < 3 ? cam.Center[axis] : axis < 6 ? cam.Angles[axis - 3] : cam.Distance,
            AccessoryPose prop => axis < 3 ? prop.Position[axis] : axis < 6 ? prop.Rotation[axis - 3] : prop.Scale,
            _ => 0
        };

        object MergeChannels(object pose, object values, IEnumerable<int> axes)
        {
            var rotation = pose is BonePose bone ? SignedAngles(bone.Rotation) : Vector3.zero;
            bool rotateBone = false;
            foreach (int axis in axes)
            {
                float value = values is float number ? number : ChannelValue(values, axis);
                switch (pose)
                {
                    case BonePose b:
                        if (axis < 3) b.Position[axis] = value * BonePositionUnit;
                        else { rotation[axis - 3] = value; rotateBone = true; }
                        pose = b;
                        break;
                    case CameraPose c:
                        if (axis < 3) c.Center[axis] = value;
                        else if (axis < 6) c.Angles[axis - 3] = value;
                        else c.Distance = value;
                        pose = c;
                        break;
                    case AccessoryPose a:
                        if (axis < 3) a.Position[axis] = value;
                        else if (axis < 6) a.Rotation[axis - 3] = value;
                        else a.Scale = value;
                        pose = a;
                        break;
                }
            }
            if (rotateBone && pose is BonePose updated) { updated.Rotation = Quaternion.Euler(rotation); return updated; }
            return pose;
        }

        void BindChannelField(FloatField field, int axis)
        {
            field.RegisterValueChangedCallback(e => {
                var track = selectedTrack;
                if (!float.IsFinite(e.newValue) || !CanEditChannels || !ChannelIsEditable(track, axis))
                { SyncControls(); return; }
                float value = Mathf.Clamp(e.newValue, axis == ScaleChannel ? 0 : -100000, 100000);
                if (track is MotionTrack<BonePose>)
                {
                    BeginDraftGesture();
                    EditTransform(axis, value);
                    FinishDraftGesture();
                }
                else {
                    if (track == cameraTrack) navigationView = null;
                    SetDraftValues(new Dictionary<MotionTrack, object> {
                    [track] = MergeChannels(track.CurrentValue, value, new[] { axis })
                    }, true);
                }
            });
            field.RegisterCallback<PointerDownEvent>(e => {
                if (e.button > 1 || !CanEditChannels || !ChannelIsEditable(selectedTrack, axis)) return;
                if (e.shiftKey && channelAnchor >= 0 && e.button == 0)
                {
                    for (int i = Math.Min(channelAnchor, axis); i <= Math.Max(channelAnchor, axis); i++)
                        if (ChannelIsEditable(selectedTrack, i)) channelSelection.Add(i);
                }
                else if (e.button == 0 && (e.ctrlKey || e.commandKey))
                {
                    if (!channelSelection.Add(axis)) channelSelection.Remove(axis);
                    channelAnchor = axis;
                }
                else if (e.button == 0 || !channelSelection.Contains(axis))
                {
                    channelSelection.Clear(); channelSelection.Add(axis); channelAnchor = axis;
                }
                SyncChannelSelection();
                if (e.button == 1)
                {
                    var track = selectedTrack;
                    var menu = new GenericDropdownMenu();
                    var axes = channelSelection.Where(i => ChannelIsEditable(track, i)).OrderBy(i => i).ToArray();
                    if (axes.Length == 0) menu.AddDisabledItem("編集不可", false);
                    else {
                        menu.AddItem("選択チャネルを登録", false, () => RegisterChannels(track, axes));
                        menu.AddItem("選択チャネルを評価値にリセット", false, () => {
                            if (!CanEditChannels || selectedTrack != track || axes.Any(i => !ChannelIsEditable(track, i))) return;
                            SetDraftValues(new Dictionary<MotionTrack, object> {
                                [track] = MergeChannels(track.CurrentValue, track.SampleValue(frame), axes)
                            });
                        });
                    }
                    var point = (Vector2)e.position;
                    menu.DropDown(new Rect(point, Vector2.zero), field, false);
                }
                if (e.button == 1 || e.ctrlKey || e.commandKey || e.shiftKey)
                {
                    e.StopImmediatePropagation(); e.PreventDefault();
                }
            }, TrickleDown.TrickleDown);
        }

        bool CanEditChannels => !playing && !importing && !exporting && modal == null && importDialog == null && !KeysCaptured();

        void RegisterChannels(MotionTrack track, int[] axes)
        {
            if (!CanEditChannels || selectedTrack != track || axes.Any(i => !ChannelIsEditable(track, i))) return;
            object draft = track.CurrentValue;
            object sample = track.SampleValue(frame);
            var desired = MergeChannels(sample, draft, axes);
            // The motion format stores a pose per frame. Merge the selected axes
            // into that pose and restore any remaining unregistered edits.
            var edits = RegistrationEditsForDraft(track, desired).Select(change => {
                if (change.Frame == frame && change.Layer == null ||
                    change.Layer != null && track.LocalTime(frame, out int local) && change.Frame == local)
                {
                    change.PreviousDraft = draft;
                    change.PreserveChannels = axes;
                }
                return change;
            }).ToArray();
            CommitKeys(edits);
        }

        void SyncChannelSelection()
        {
            for (int i = 0; i < transformFields.Length; i++)
                transformFields[i].EnableInClassList("channel-selected", channelSelection.Contains(i));
        }

        void SyncChannelBox()
        {
            var track = selectedTrack;
            if (track != channelOwner)
            {
                channelOwner = track; channelSelection.Clear(); channelAnchor = -1;
                SyncChannelSelection();
            }
            bool numeric = HasNumericChannels(track);
            bool hasKey = numeric && track.LocalTime(frame, out int local) && track.EditKey(local) != null &&
                (track.Target != null || track.EnabledKey(local));
            var value = numeric ? track.CurrentValue : null;
            var sampled = numeric ? track.SampleValue(frame) : null;
            for (int i = 0; i < transformFields.Length; i++)
            {
                var field = transformFields[i];
                bool visible = numeric && (i < 6 || i == DistanceChannel && track == cameraTrack ||
                    i == ScaleChannel && track is MotionTrack<AccessoryPose>);
                field.EnableInClassList("hidden", !visible);
                if (!visible) continue;
                bool editable = ChannelIsEditable(track, i);
                float current = ChannelValue(value, i), saved = ChannelValue(sampled, i);
                float difference = i >= 3 && i < 6 ? Mathf.DeltaAngle(saved, current) : saved - current;
                bool changed = Mathf.Abs(difference) > .00001f;
                field.EnableInClassList("channel-draft", editable && changed);
                field.EnableInClassList("channel-key", editable && !changed && hasKey);
                field.EnableInClassList("channel-locked", !editable);
                field.tooltip = !editable ? "編集不可（現在の登録先）" : changed ? "未登録の変更" :
                    hasKey ? "現在フレームに登録済み" : "補間値（現在フレームにキーなし）";
                field.SetEnabled(editable);
                field.SetValueWithoutNotify(current);
            }
            channelStatus.EnableInClassList("hidden", !numeric);
        }
    }
}
