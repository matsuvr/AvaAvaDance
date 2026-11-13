using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        bool extendedHandles;
        BoneBinding handleTip,handleUpper,handleLower,handleIK;
        Vector3 handleGoal;
        Quaternion handleRotation;
        int gizmoAxis=-1;
        Vector2 gizmoDirection;

        IEnumerable<BoneBinding> PosePoints()
        {
            if(actor.humanBones==null)yield break;
            foreach(var role in new[]{HumanBodyBones.LeftHand,HumanBodyBones.RightHand,HumanBodyBones.LeftFoot,HumanBodyBones.RightFoot})
                if(actor.humanBones.TryGetValue(role,out var joint)){
                    var b=actor.bones.Find(item=>item.Joint==joint);if(b!=null)yield return b;
                }
        }
        bool ProjectPoint(Vector3 world,out Vector2 screen)
        {
            var p=previewCamera.WorldToViewportPoint(world);
            screen=new Vector2(p.x*overlay.contentRect.width,(1-p.y)*overlay.contentRect.height);
            return p.z>previewCamera.nearClipPlane;
        }
        Vector3 HandleAxis(BoneBinding b,int axis)
        {
            var v=Vector3.zero;v[axis]=1;
            return localAxes?b.Joint.TransformDirection(b.EditingBasis*v):v;
        }
        bool AxisSegment(int index,out Vector2 start,out Vector2 end)
        {
            var b=actor.selectedBone;start=end=default;
            if(b==null || !ProjectJoint(b.Joint,out start))return false;
            return ProjectPoint(b.Joint.position+HandleAxis(b,index)*WorldPerPixel()*(extendedHandles?65:43),out end) &&
                (end-start).sqrMagnitude>50;
        }
        void DrawRigHandles(Painter2D p)
        {
            for(int i=0;i<3;i++)
            {
                if(!AxisSegment(i,out var start,out var end))continue;
                Color color=i==0?Color.red:i==1?new Color(.13f,.65f,.22f):new Color(.1f,.3f,1);
                var dir=(end-start).normalized;var side=new Vector2(dir.y,-dir.x);
                Line(p,start+dir*7,end,color);Line(p,end,end-dir*6+side*3,color);Line(p,end,end-dir*6-side*3,color);
                if(extendedHandles){p.strokeColor=color;p.BeginPath();p.Arc(end,5,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();}
            }
            if(!showHandles)return;
            foreach(var b in PosePoints()){
                if(!ProjectJoint(b.Joint,out var at))continue;
                p.lineWidth=2;p.strokeColor=new Color(.1f,.65f,.6f);p.BeginPath();
                p.Arc(at,10,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
            }
        }
        bool BeginRigHandle(Vector2 point)
        {
            if(!ribbonProfile || !modelMode || playing)return false;
            if(showHandles)foreach(var tip in PosePoints())
            {
                if(!ProjectJoint(tip.Joint,out var at))continue;
                float distance=(point-at).magnitude;
                if(distance<6 || distance>13)continue;
                var lower=actor.bones.Find(b=>b.Joint==tip.Joint.parent);
                var upper=lower==null?null:actor.bones.Find(b=>b.Joint==lower.Joint.parent);
                if(upper==null)continue;
                var ik=actor.ikChains.FirstOrDefault(c=>c.Tip==tip.Joint && actor.modelSwitch.Current.Enabled(c.Handle.Track.Name));
                handleTip=tip;handleLower=lower;handleUpper=upper;handleIK=ik?.Handle;
                handleGoal=tip.Joint.position;handleRotation=tip.Joint.rotation;
                SelectTrack(handleIK?.Track??tip.Track);
                var affected=handleIK!=null?new[]{handleIK}:new[]{upper,lower,tip};
                boneSelection.AddRange(affected);BeginDraftGesture();return true;
            }
            for(int i=0;i<3;i++)
            {
                if(!AxisSegment(i,out var start,out var end))continue;
                var v=end-start;float t=Vector2.Dot(point-start,v)/v.sqrMagnitude;
                if(t<.18f || t>1.1f || Vector2.Distance(point,start+v*t)>5)continue;
                gizmoAxis=i;gizmoDirection=v.normalized;BeginDraftGesture();return true;
            }
            return false;
        }
        bool DragRigHandle(Vector2 delta,bool fine,RotationGesture gesture)
        {
            if(gizmoAxis>=0)
            {
                DragAxis(gizmoAxis,boneTool!=BoneTool.Move,Vector2.Dot(delta,gizmoDirection)*(fine && gesture==RotationGesture.Normal?.1f:1),gesture);
                return true;
            }
            if(handleTip==null)return false;
            handleGoal+=(previewCamera.transform.right*delta.x-previewCamera.transform.up*delta.y)*WorldPerPixel()*(fine?.1f:1);
            if(handleIK!=null)
            {
                var pose=handleIK.Track.Current;
                pose.Position+=handleIK.Joint.parent.InverseTransformVector(handleGoal-handleIK.Joint.position);
                handleIK.Track.Preview(pose);
            }
            else
            {
                for(int i=0;i<12;i++){
                    if((handleTip.Joint.position-handleGoal).sqrMagnitude<1e-10f)break;
                    Aim(handleLower.Joint,handleTip.Joint.position,handleGoal);Aim(handleUpper.Joint,handleTip.Joint.position,handleGoal);
                }
                handleTip.Joint.rotation=handleRotation;
                foreach(var b in new[]{handleUpper,handleLower,handleTip}){
                    var pose=b.Track.Current;pose.Rotation=Quaternion.Inverse(b.RestRotation)*b.Joint.localRotation;b.Track.Preview(pose);
                }
            }
            InvalidateDynamics();RefreshRigPreview();SyncControls();RedrawOverlay();return true;
        }
        void EndRigHandle(){gizmoAxis=-1;handleTip=handleUpper=handleLower=handleIK=null;FinishDraftGesture();}
    }
}
