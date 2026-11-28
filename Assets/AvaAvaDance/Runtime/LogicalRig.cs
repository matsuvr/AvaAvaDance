using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        void BindLogicalRig()
        {
            if(actor.controller!=null || actor.humanBones==null || !actor.humanBones.TryGetValue(HumanBodyBones.Hips,out var hips))return;
            var pelvis=actor.bones.Find(b=>b.Joint==hips);if(pelvis==null)return;
            var owner=actor;var root=owner.avatar.transform;var rest=root.localPosition;var rotation=root.localRotation;var scale=root.localScale;
            owner.logicalPelvis=pelvis;
            var center=root.InverseTransformPoint(hips.position);
            void Add(string name,bool moveRoot)
            {
                if(owner.bones.Any(b=>b.Track.Name==name))return;
                var handle=new GameObject(name).transform;handle.SetParent(root,false);
                handle.localPosition=moveRoot?Vector3.zero:center;
                var track=new MotionTrack<BonePose>(name,BonePose.Rest,BonePose.Blend,pose=>{
                    if(moveRoot){root.localPosition=rest+rotation*Vector3.Scale(scale,pose.Position);root.localRotation=rotation*pose.Rotation;}
                    else{handle.localPosition=center+pose.Position;handle.localRotation=pose.Rotation;}
                });
                var b=new BoneBinding{Track=track,Joint=handle,RestRotation=Quaternion.identity,EditingBasis=Quaternion.identity};
                owner.bones.Add(b);owner.modelTracks.Add(track);owner.logicalControls.Add(track);
                if(!moveRoot)owner.pelvisControls.Add(track);
            }
            Add("全ての親",true);Add("センター",false);Add("グルーブ",false);
        }

        void ApplyPelvisControls()
        {
            var pelvis=actor.logicalPelvis;
            if(pelvis==null || actor.pelvisControls.Count==0)return;
            Vector3 move=Vector3.zero;Quaternion rotation=Quaternion.identity;
            foreach(var control in actor.pelvisControls){move+=rotation*control.Current.Position;rotation*=control.Current.Rotation;}
            if(move==Vector3.zero && rotation==Quaternion.identity)return;
            pelvis.Track.Preview(pelvis.Track.Current);
            var root=actor.avatar.transform;pelvis.Joint.position+=root.TransformVector(move);
            pelvis.Joint.rotation=root.rotation*rotation*Quaternion.Inverse(root.rotation)*pelvis.Joint.rotation;
        }
    }
}
