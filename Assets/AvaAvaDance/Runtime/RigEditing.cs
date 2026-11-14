using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static Quaternion ChildBasis(Transform joint)
        {
            for(int i=0;i<joint.childCount;i++)
            {
                var direction=joint.GetChild(i).localPosition;
                if(direction.sqrMagnitude<1e-10f)continue;
                var up=direction.normalized;
                var forward=Vector3.ProjectOnPlane(Mathf.Abs(up.z)<.9f?Vector3.forward:Vector3.right,up);
                return Quaternion.LookRotation(forward.normalized,up);
            }
            return Quaternion.identity;
        }

        enum RotationGesture { Normal, CounterParent, ReverseParent }

        RotationGesture RotationMode(bool alt,bool control,bool shift)=>!ribbonProfile?RotationGesture.Normal:
            control && shift?RotationGesture.ReverseParent:alt?RotationGesture.CounterParent:RotationGesture.Normal;

        void RotateParentAndBone(BoneBinding bone,Quaternion worldStep,RotationGesture mode)
        {
            BoneBinding parent=null;
            for(var joint=bone.Joint.parent;joint!=null && parent==null;joint=joint.parent)
                parent=actor.bones.Find(b=>b.Joint==joint);
            if(parent==null)return;
            var center=mode==RotationGesture.ReverseParent?actor.bones.Find(b=>b.Track.Name=="センター"):null;
            if(mode==RotationGesture.ReverseParent && (center==null || center==bone))return;
            if(center!=null && !bone.Joint.IsChildOf(center.Joint) &&
                (actor.logicalPelvis==null || !actor.pelvisControls.Contains(center.Track) || !bone.Joint.IsChildOf(actor.logicalPelvis.Joint)))return;
            StopPlayback();
            void Include(BoneBinding b){
                if(gestureBefore!=null && !gestureBefore.ContainsKey(b.Track))gestureBefore.Add(b.Track,b.Track.CurrentValue);
                if(!boneSelection.Contains(b))boneSelection.Add(b);
                selectedRows.Add(b.Track);
            }
            Include(bone);Include(parent);if(center!=null)Include(center);
            var pivot=bone.Joint.position;var orientation=bone.Joint.rotation;
            var delta=mode==RotationGesture.CounterParent?Quaternion.Inverse(worldStep):worldStep;
            var outer=parent.Track.Current;
            var parentSpace=parent.Joint.parent!=null?parent.Joint.parent.rotation:Quaternion.identity;
            outer.Rotation=Quaternion.Inverse(parent.RestRotation)*Quaternion.Inverse(parentSpace)*delta*parent.Joint.rotation;
            parent.Track.Preview(outer);
            var child=bone.Track.Current;
            child.Rotation=Quaternion.Inverse(bone.RestRotation)*Quaternion.Inverse(bone.Joint.parent.rotation)*orientation;
            bone.Track.Preview(child);
            if(center!=null){
                var translation=center.Track.Current;
                var movement=pivot-bone.Joint.position;
                translation.Position+=center.Joint.parent!=null?center.Joint.parent.InverseTransformVector(movement):movement;
                center.Track.Preview(translation);
            }
            InvalidateDynamics();RefreshRigPreview();SyncControls();RedrawOverlay();
        }

        void PoseSelection(BonePose pose)
        {
            var lead=actor.selectedBone;
            var prior=lead.Track.Current;
            var parent=lead.Joint.parent;
            var restWorld=parent.rotation*lead.RestRotation;
            var delta=restWorld*pose.Rotation*Quaternion.Inverse(restWorld*prior.Rotation);
            var pivot=lead.Joint.position;
            var movement=parent.TransformVector(pose.Position-prior.Position);
            var selected=boneSelection.OrderBy(b=>HierarchyDepth(b.Joint))
                .Select(b=>(bone:b,position:pivot+delta*(b.Joint.position-pivot)+movement,
                    rotation:delta*b.Joint.rotation,rest:b.Joint.localPosition-b.Track.Current.Position)).ToArray();
            foreach(var item in selected)
            {
                var b=item.bone;var value=b.Track.Current;var p=b.Joint.parent;
                value.Position=p.InverseTransformPoint(item.position)-item.rest;
                value.Rotation=Quaternion.Inverse(b.RestRotation)*Quaternion.Inverse(p.rotation)*item.rotation;
                b.Track.Preview(value);
            }
        }
        static int HierarchyDepth(Transform joint){int n=0;for(var p=joint.parent;p!=null;p=p.parent)n++;return n;}

        void ReleaseIKToDraft(LimbIK chain)
        {
            var values=new Dictionary<MotionTrack,object>();
            foreach(var joint in new[]{chain.Upper,chain.Lower,chain.Tip})
            {
                var binding=actor.bones.Find(b=>b.Joint==joint);if(binding==null)continue;
                var pose=binding.Track.Current;pose.Rotation=Quaternion.Inverse(binding.RestRotation)*joint.localRotation;
                values[binding.Track]=pose;
            }
            var state=actor.modelSwitch.Current;
            state.DisabledIK=(state.DisabledIK??Array.Empty<string>()).Append(chain.Handle.Track.Name).Distinct().ToArray();
            values[actor.modelSwitch]=state;
            SetDraftValues(values);
        }

        void HandPoseDialog()
        {
            ShowDialog("手指の握り",out var body);
            var hand=new DropdownField("手",new List<string>{"左手","右手","両手"},2);body.Add(hand);
            var amount=new Slider("握り",0,1){showInputField=true};body.Add(amount);
            Action(body,"適用",true,()=>{
                var changes=new Dictionary<MotionTrack,object>();
                foreach(var part in actor.humanBones)
                {
                    string name=part.Key.ToString();
                    bool left=name.StartsWith("Left",StringComparison.Ordinal);
                    if(hand.index!=2 && left!=(hand.index==0))continue;
                    if(!name.EndsWith("Proximal") && !name.EndsWith("Intermediate") && !name.EndsWith("Distal"))continue;
                    var b=actor.bones.Find(j=>j.Joint==part.Value);
                    if(b==null)continue;
                    var p=b.Track.Current;
                    float angle=(name.Contains("Thumb")?45:75)*amount.value*(left?1:-1);
                    p.Rotation=b.EditingBasis*Quaternion.AngleAxis(angle,Vector3.forward)*Quaternion.Inverse(b.EditingBasis);
                    changes[b.Track]=p;
                }
                SetDraftValues(changes);
                if(changes.Count>0){SelectTrack(changes.Keys.First());boneSelection.AddRange(actor.bones.Where(b=>changes.ContainsKey(b.Track)));}
                CloseDialog();
            });
            Action(body,"キャンセル",true,CloseDialog);
        }

        void FocusViewportPoint(Vector2 pointer)
        {
            var hit=PickBone(pointer);
            var view=ActiveView;
            if(hit!=null)view.Center=CameraPose.Initial.Center+hit.Joint.position/SceneUnit;
            else
            {
                var ray=previewCamera.ViewportPointToRay(new Vector3(pointer.x/viewport.contentRect.width,1-pointer.y/viewport.contentRect.height,0));
                var plane=new Plane(previewCamera.transform.forward,(view.Center-CameraPose.Initial.Center)*SceneUnit);
                if(!plane.Raycast(ray,out float distance))return;
                view.Center=CameraPose.Initial.Center+ray.GetPoint(distance)/SceneUnit;
            }
            PreviewView(view);
        }
    }
}
