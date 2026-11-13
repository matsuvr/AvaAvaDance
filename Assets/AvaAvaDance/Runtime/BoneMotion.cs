using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class BoneBinding
        {
            public Transform Joint;
            public MotionTrack<BonePose> Track;
            public Quaternion RestRotation, EditingBasis;
        }
        enum BoneTool { Select, Rotate, Move }


        BoneTool boneTool;
        DropdownField boneList;
        Button[] toolButtons;
        Button posePaste;
        BonePose? poseClipboard;
        VisualElement overlay, axisTools;
        bool dragging;
        int dragButton;
        Vector2 lastPoint;

        void BuildBoneControls(VisualElement parent)
        {
            bone = Panel(parent, "bone", "ボーン操作");
            boneList = Choice(bone, new[] { "ボーンなし" });
            boneList.RegisterValueChangedCallback(_ =>
            {
                if (boneList.index >= 0 && boneList.index < actor.bones.Count) SelectTrack(actor.bones[boneList.index].Track);
            });
            var tools = Box(bone, "bone-tools", "row");
            string[] names = { "選択", "回転", "移動" };
            toolButtons = new Button[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                BoneTool tool = (BoneTool)i;
                toolButtons[i] = Action(tools, names[i], true, () => { boneTool = tool; SyncBoneControls(); });
            }
            var copy = Box(bone, "pose-copy", "row");
            Action(copy, "コピー", true, () => { poseClipboard = actor.selectedBone.Track.Current; SyncBoneControls(); });
            posePaste = Action(copy, "ペースト", false, () => { if (poseClipboard.HasValue) PreviewBone(poseClipboard.Value); });
            var actions = Box(bone, "bone-actions", "row");
            Action(actions, "登録", true, () => RegisterKey(actor.selectedBone?.Track));
            Action(actions, "初期化", true, () => PreviewBone(BonePose.Rest));
            bonePhysicsButton = Action(bone, "物理", false, () => {
                var p=actor.selectedBone.Track.Current; p.PhysicsOff=!p.PhysicsOff; PreviewBone(p);
            });
        }

        void BindBones(IEnumerable<Transform> dynamicJoints)
        {
            var joints = new HashSet<Transform>(dynamicJoints);
            foreach (var skin in actor.avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var joint in skin.bones)
                {
                    if (joint == null || joint == skin.transform) continue;
                    for (var t = joint; t != null && t != actor.avatar.transform; t = t.parent) joints.Add(t);
                }
            var labels = new HashSet<string>();
            foreach (var joint in actor.avatar.GetComponentsInChildren<Transform>(true))
            {
                if (!joints.Contains(joint)) continue;
                Vector3 restPosition = joint.localPosition;
                Quaternion restRotation = joint.localRotation;
                var vrmGaze = joint == actor.gaze ? actor.vrm : null;
                string name = string.IsNullOrWhiteSpace(joint.name) ? "ボーン" : joint.name;
                string label = name;
                for (int n = 2; !labels.Add(label); n++) label = name + " (" + n + ")";
                var track = new MotionTrack<BonePose>(label, BonePose.Rest, BonePose.Blend, pose =>
                {
                    joint.localPosition = restPosition + pose.Position;
                    joint.localRotation = restRotation * pose.Rotation;
                    if (vrmGaze != null)
                    {
                        var angles = SignedAngles(pose.Rotation);
                        vrmGaze.Runtime.LookAt.SetYawPitchManually(angles.y, -angles.x);
                    }
                });
                actor.bones.Add(new BoneBinding { Joint = joint, Track = track, RestRotation = restRotation, EditingBasis=ChildBasis(joint) }); actor.modelTracks.Add(track);
            }
            var weighted = new HashSet<Transform>(actor.avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(s => s.bones.Where(j => j != null && j != s.transform)));
            actor.selectedBone = (actor.vrm != null ? actor.bones.Find(b => b.Joint == actor.humanBones[HumanBodyBones.Hips]) : null) ??
                actor.bones.Find(b => b.Joint.name == "センター") ??
                actor.bones.Find(b => b.Joint.name.Equals("Hips", System.StringComparison.OrdinalIgnoreCase)) ??
                actor.bones.Find(b => weighted.Contains(b.Joint)) ?? actor.bones.FirstOrDefault();
            boneList.choices = actor.bones.Select(b => b.Track.Name).ToList();
            boneTool = BoneTool.Select;
        }

        void ReleaseBones()
        {
            // Model tracks refer to this particular imported hierarchy.
            if (actor.modelTracks.Count > 0)
            {
                DiscardHistory(undo, actor.modelTracks.Contains); DiscardHistory(redo, actor.modelTracks.Contains);
                if (keyClipboard.Any(k => actor.modelTracks.Contains(k.Track))) keyClipboard.Clear();
                selectedKeys.Clear();
            }
            foreach (var group in morphGroups) group?.Tracks.Clear();
            boneSelection.Clear(); actor.modelSelection = null; actor.modelTracks.Clear(); actor.bones.Clear(); actor.selectedBone = null; poseClipboard = null;
        }

        static void DiscardHistory(Stack<KeyChange[]> history, System.Predicate<MotionTrack> discard)
        {
            var retained = history.Where(group => !group.Any(change => change.Track != null && discard(change.Track))).Reverse().ToArray();
            history.Clear(); foreach (var change in retained) history.Push(change);
        }

        void SyncBoneControls()
        {
            bone.SetEnabled(actor.selectedBone != null);
            boneList.SetEnabled(actor.bones.Count > 1);
            boneList.SetValueWithoutNotify(actor.selectedBone?.Track.Name ?? "ボーンなし");
            for (int i = 0; i < toolButtons.Length; i++)
                toolButtons[i].EnableInClassList("active-tool", i == (int)boneTool);
            posePaste.SetEnabled(poseClipboard.HasValue);
        }

        static Vector3 SignedAngles(Quaternion rotation)
        {
            var a = rotation.eulerAngles;
            return new Vector3(Mathf.DeltaAngle(0,a.x), Mathf.DeltaAngle(0,a.y), Mathf.DeltaAngle(0,a.z));
        }

        void PreviewBone(BonePose pose)
        {
            if (actor.selectedBone == null) return;
            StopPlayback();
            var before=actor.selectedBone.Track.Current;
            bool group=ribbonProfile && boneSelection.Contains(actor.selectedBone) && boneSelection.Count>1;
            if(group)PoseSelection(pose);
            else if(boneSelection.Contains(actor.selectedBone))foreach(var other in boneSelection) {
                if(other==actor.selectedBone)continue;
                var value=other.Track.Current;value.Position+=pose.Position-before.Position;
                value.Rotation*=Quaternion.Inverse(before.Rotation)*pose.Rotation;other.Track.Preview(value);
            }
            if(!group)actor.selectedBone.Track.Preview(pose); InvalidateDynamics(); RefreshRigPreview();
            if (selectedTrack != actor.selectedBone.Track) SelectTrack(actor.selectedBone.Track);
            else { SyncControls(); RedrawOverlay(); }
        }

        void SetupViewportInput(VisualElement target)
        {
            target.focusable=true;
            overlay = Box(target, "bones-overlay", "bones-overlay");
            overlay.pickingMode = PickingMode.Ignore;
            overlay.generateVisualContent += DrawBones;
            BuildAxisTools(target);
            target.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button < 0 || e.button > 2) return;
                target.Focus();StopPlayback();
                if(ribbonProfile && e.button==1 && e.clickCount==2){FocusViewportPoint(e.localPosition);e.StopPropagation();return;}
                if(e.button==0 && BeginRigHandle(e.localPosition)){
                    dragging=true;dragButton=0;lastPoint=e.position;target.CapturePointer(e.pointerId);e.StopPropagation();return;
                }
                if (modelMode && e.button == 0 && boneTool == BoneTool.Select)
                {
                    var hit = PickBone(e.localPosition);
                    if (hit != null) {
                        if(ribbonProfile ? e.ctrlKey : e.shiftKey){
                            var prior=new List<BoneBinding>(boneSelection);
                            if(prior.Count==0 && actor.selectedBone!=null)prior.Add(actor.selectedBone);
                            if(prior.Contains(hit))prior.Remove(hit);else prior.Add(hit);
                            var lead=prior.LastOrDefault();
                            SelectTrack(lead?.Track);
                            boneSelection.Clear();boneSelection.AddRange(prior);RedrawOverlay();
                        }else SelectTrack(hit.Track);
                        e.StopPropagation();return;
                    }
                }
                if (ribbonProfile && modelMode && e.button == 0 && boneTool != BoneTool.Select) BeginDraftGesture();
                dragging = true; dragButton = e.button; lastPoint = e.position;
                target.CapturePointer(e.pointerId); e.StopPropagation();
            });
            target.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                Vector2 now = e.position;
                var delta = now - lastPoint; lastPoint = now;
                if (delta.sqrMagnitude == 0) return;
                if(DragRigHandle(delta,e.ctrlKey && !e.shiftKey,RotationMode(e.altKey,e.ctrlKey,e.shiftKey))){e.StopPropagation();return;}
                if (modelMode && dragButton == 0 && boneTool != BoneTool.Select && actor.selectedBone != null)
                    DragBone(delta,RotationMode(e.altKey,e.ctrlKey,e.shiftKey));
                else
                {
                    var pose = ActiveView;
                    if (dragButton == 1 && e.ctrlKey) pose.Distance = Mathf.Clamp(pose.Distance + delta.y * .2f, .1f, 10000);
                    else if (dragButton == 2 || dragButton == 1 && e.shiftKey)
                    {
                        var move = (-previewCamera.transform.right * delta.x + previewCamera.transform.up * delta.y);
                        pose.Center += move * WorldPerPixel() / SceneUnit;
                    }
                    else { pose.Angles.y += delta.x * .5f; pose.Angles.x = Mathf.Clamp(pose.Angles.x - delta.y * .5f, -90, 90); }
                    PreviewView(pose);
                }
                e.StopPropagation();
            });
            target.RegisterCallback<PointerUpEvent>(e =>
            {
                if (target.HasPointerCapture(e.pointerId)) target.ReleasePointer(e.pointerId);
                dragging = false;
            });
            target.RegisterCallback<PointerCaptureOutEvent>(_ => { dragging = false; EndRigHandle(); });
            target.RegisterCallback<WheelEvent>(e =>
            {
                var pose = ActiveView;
                pose.Distance = Mathf.Clamp(pose.Distance * Mathf.Exp(e.delta.y * .04f), .1f, 950);
                PreviewView(pose); e.StopPropagation();
            });
        }

        void BuildAxisTools(VisualElement target)
        {
            axisTools = Box(target, "axis-tools", "axis-tools");
            for (int row = 0; row < 2; row++)
            {
                bool rotate = row == 0;
                var group = Box(axisTools, rotate ? "rotate-axis" : "move-axis", "row");
                group.Add(new Label(rotate ? "回転" : "移動"));
                for (int axis = 0; axis < 3; axis++)
                {
                    int index = axis;
                    var handle = new Label("XYZ"[axis].ToString());
                    handle.AddToClassList("axis-handle"); handle.AddToClassList("axis-" + axis);
                    handle.tooltip = handle.text + "軸" + (rotate ? "回転" : "移動") + "をドラッグ";
                    group.Add(handle);
                    Vector2 start = default;
                    handle.RegisterCallback<PointerDownEvent>(e =>
                    {
                        if (e.button != 0) return;
                        StopPlayback(); if (ribbonProfile) BeginDraftGesture(); start = e.position; handle.CapturePointer(e.pointerId); e.StopPropagation();
                    });
                    handle.RegisterCallback<PointerMoveEvent>(e =>
                    {
                        if (!handle.HasPointerCapture(e.pointerId)) return;
                        Vector2 now = e.position; var delta = now - start; start = now;
                        float pixels = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y) ? delta.x : -delta.y;
                        var gesture=rotate?RotationMode(e.altKey,e.ctrlKey,e.shiftKey):RotationGesture.Normal;
                        float scale=gesture==RotationGesture.Normal?(e.shiftKey?10:e.ctrlKey?.1f:1):1;
                        if(pixels!=0)DragAxis(index,rotate,pixels*scale,gesture);
                        e.StopPropagation();
                    });
                    handle.RegisterCallback<PointerCaptureOutEvent>(_ => FinishDraftGesture());
                    handle.RegisterCallback<PointerUpEvent>(e =>
                    {
                        if (handle.HasPointerCapture(e.pointerId)) handle.ReleasePointer(e.pointerId);
                        e.StopPropagation();
                    });
                }
            }
        }

        void DragAxis(int index, bool rotate, float pixels,RotationGesture gesture=RotationGesture.Normal,bool keyboard=false)
        {
            float degrees=pixels * (keyboard ? .025f*Mathf.Rad2Deg : .5f);
            float distance=rotate?0:pixels*(keyboard?HeldTranslationScale():WorldPerPixel());
            Vector3 axis = Vector3.zero; axis[index] = 1;
            if (modelMode && actor.selectedBone != null)
            {
                var pose = actor.selectedBone.Track.Current;
                var editAxis=ribbonProfile?actor.selectedBone.EditingBasis*axis:axis;
                if(rotate && gesture!=RotationGesture.Normal){
                    var worldAxis=localAxes?actor.selectedBone.Joint.TransformDirection(editAxis):axis;
                    RotateParentAndBone(actor.selectedBone,Quaternion.AngleAxis(degrees,worldAxis),gesture);return;
                }
                if (rotate) pose.Rotation *= Quaternion.AngleAxis(degrees, localAxes ? editAxis : actor.selectedBone.Joint.InverseTransformDirection(axis));
                else
                {
                    var joint = actor.selectedBone.Joint;
                    var world = (localAxes ? joint.TransformDirection(editAxis) : axis) * distance;
                    pose.Position += joint.parent.InverseTransformVector(world);
                }
                PreviewBone(pose);
            }
            else if (!modelMode && ribbonProfile && selectedTrack is MotionTrack<AccessoryPose>)
            {
                EditProp(p => { if (rotate) p.Rotation += axis * degrees; else p.Position += axis * (distance / SceneUnit); return p; });
            }
            else if (!modelMode)
            {
                var pose = cameraTrack.Current;
                if (rotate) pose.Angles += axis * degrees;
                else pose.Center += axis * (distance / SceneUnit);
                PreviewCameraPose(pose);
            }
        }

        float HeldTranslationScale()
        {
            Transform target=modelMode?actor.selectedBone?.Joint:selectedTrack==activeProp?.Track?activeProp?.Object.transform:null;
            float distance=target==null?Mathf.Abs(ActiveView.Distance)*SceneUnit:Vector3.Distance(previewCamera.transform.position,target.position);
            return distance*.0025f;
        }

        float WorldPerPixel()
        {
            float height = previewCamera.orthographic ? previewCamera.orthographicSize :
                Mathf.Abs(ActiveView.Distance * SceneUnit) * Mathf.Tan(previewCamera.fieldOfView * Mathf.Deg2Rad * .5f);
            return 2 * height / Mathf.Max(1, viewport.contentRect.height);
        }

        void DragBone(Vector2 delta,RotationGesture gesture=RotationGesture.Normal)
        {
            var pose = actor.selectedBone.Track.Current;
            var joint = actor.selectedBone.Joint;
            if(boneTool==BoneTool.Rotate){
                var turn=Quaternion.Euler(-delta.y*.5f,delta.x*.5f,0);
                if(gesture!=RotationGesture.Normal){
                    RotateParentAndBone(actor.selectedBone,joint.rotation*turn*Quaternion.Inverse(joint.rotation),gesture);return;
                }
                pose.Rotation*=turn;
            }
            else
            {
                Vector3 offset = (previewCamera.transform.right * delta.x - previewCamera.transform.up * delta.y) * WorldPerPixel();
                pose.Position += joint.parent.InverseTransformVector(offset);
            }
            PreviewBone(pose);
        }

        bool ProjectJoint(Transform joint, out Vector2 point)
        {
            var v = previewCamera.WorldToViewportPoint(joint.position);
            point = new Vector2(v.x * overlay.contentRect.width, (1 - v.y) * overlay.contentRect.height);
            return v.z > previewCamera.nearClipPlane && v.x >= 0 && v.x <= 1 && v.y >= 0 && v.y <= 1;
        }

        BoneBinding PickBone(Vector2 point)
        {
            if (actor.avatar == null || !actor.avatar.activeSelf || playing) return null;
            BoneBinding hit = null;
            float closest = 10 * 10;
            foreach (var boneBinding in actor.bones)
            {
                if (!ProjectJoint(boneBinding.Joint, out var projected)) continue;
                float d = (projected - point).sqrMagnitude;
                if (d < closest) { closest = d; hit = boneBinding; }
            }
            return hit;
        }

        void RedrawOverlay()
        {
            overlay?.MarkDirtyRepaint();
            axisTools?.EnableInClassList("hidden", playing || (modelMode && actor.selectedBone == null));
        }

        void DrawBones(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            if (actor.avatar == null || !actor.avatar.activeSelf || previewCamera == null) return; DrawBodies(p);
            if(!modelMode || playing)return;
            if(ribbonProfile){DrawRigHandles(p);if(!showMarkers)return;}
            foreach (var binding in actor.bones)
            {
                if (!ProjectJoint(binding.Joint, out var point)) continue;
                bool selected = actor.selectedBone == binding || boneSelection.Contains(binding);
                var color = showBodies && actor.dynamicLinks.Any(d => d.Bone == binding) ? Color.green : actor.ikChains.Any(c => c.Handle == binding) && !selected ? Color.yellow : selected ? new Color(.95f,.12f,.25f) : new Color(.25f,.29f,.9f,.78f);
                if (binding.Joint.parent != actor.avatar.transform && ProjectJoint(binding.Joint.parent, out var parent))
                    Line(p, point, parent, color);
                p.strokeColor = color; p.lineWidth = selected ? 3 : 1.5f;
                p.BeginPath(); p.Arc(point, selected ? 5 : 3.5f, Angle.Degrees(0), Angle.Degrees(360)); p.Stroke();
            }
        }
    }
}
