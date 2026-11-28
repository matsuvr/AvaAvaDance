using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class BoneLink
        {
            public string owner,track,parentOwner,parentTrack;
            public Vector3 anchor,rest,bindPosition;
            public Quaternion orientation,bindRotation;
            public Matrix4x4 translationBasis;
            [NonSerialized] public BoneBinding child,parent;
        }

        readonly List<BoneLink> externalParents=new List<BoneLink>();
        static bool ValidRotation(Quaternion q)=>Math.Abs(Quaternion.Dot(q,q)-1)<.001f;
        static bool Finite(Vector3 v)=>float.IsFinite(v.sqrMagnitude);

        void BindLinks(IEnumerable<BoneLink> links)
        {
            var joints=drawOrder.SelectMany(a=>a.bones.Select(b=>(a.id,b))).ToDictionary(entry=>(entry.id,entry.b.Track.Id),entry=>entry.b);
            var pending=links.ToList();
            if(pending.Any(l=>l==null) || pending.GroupBy(l=>(l.owner,l.track)).Any(g=>g.Count()>1))throw new InvalidDataException("外部親が重複しています。");
            foreach(var link in pending){
                if(!joints.TryGetValue((link.owner,link.track),out link.child) || !joints.TryGetValue((link.parentOwner,link.parentTrack),out link.parent))
                    throw new InvalidDataException("外部親のボーンが見つかりません。");
                if(!Finite(link.anchor) || !Finite(link.bindPosition) || !Finite(link.rest) || !ValidRotation(link.orientation) || !ValidRotation(link.bindRotation))
                    throw new InvalidDataException("外部親の姿勢が不正です。");
                for(int i=0;i<16;i++)if(!float.IsFinite(link.translationBasis[i]))throw new InvalidDataException("外部親の変換が不正です。");
            }
            var sorted=new List<BoneLink>();
            while(pending.Count>0){
                int next=pending.FindIndex(l=>!pending.Any(other=>l.parent.Joint.IsChildOf(other.child.Joint) ||
                    other!=l && l.child.Joint.IsChildOf(other.child.Joint)));
                if(next<0)throw new InvalidDataException("外部親が循環しています。");
                sorted.Add(pending[next]);pending.RemoveAt(next);
            }
            externalParents.Clear();externalParents.AddRange(sorted);
        }
        void ApplyLinks()
        {
            foreach(var link in externalParents){
                var pose=link.child.Track.Current;var parent=link.parent.Joint;
                var position=link.anchor+link.translationBasis.MultiplyVector(pose.Position-link.bindPosition);
                link.child.Joint.SetPositionAndRotation(parent.TransformPoint(position),
                    parent.rotation*link.orientation*Quaternion.Inverse(link.bindRotation)*pose.Rotation);
            }
        }

        void SetExternalParent(BoneBinding child,Actor parentActor,BoneBinding parent)
        {
            var owner=drawOrder.First(a=>a.bones.Contains(child));
            var previous=externalParents.FirstOrDefault(l=>l.child==child);
            if(previous?.parent==parent)return;
            var before=child.Track.Current;var after=before;
            var world=child.Joint.position;var rotation=child.Joint.rotation;var natural=child.Joint.parent;
            var rest=previous?.rest??child.Joint.localPosition-before.Position;
            BoneLink link=null;
            if(parent!=null){
                link=new BoneLink{owner=owner.id,track=child.Track.Id,parentOwner=parentActor.id,parentTrack=parent.Track.Id,
                    anchor=parent.Joint.InverseTransformPoint(world),orientation=Quaternion.Inverse(parent.Joint.rotation)*rotation,
                    translationBasis=parent.Joint.worldToLocalMatrix*natural.localToWorldMatrix,bindPosition=before.Position,bindRotation=before.Rotation,rest=rest};
            }else{
                after.Position=natural.InverseTransformPoint(world)-rest;
                after.Rotation=Quaternion.Inverse(child.RestRotation)*Quaternion.Inverse(natural.rotation)*rotation;
            }
            void Change(MotionTrack target,bool forward){
                var next=externalParents.Where(l=>l.child.Track!=target).ToList();var replacement=forward?link:previous;
                if(replacement!=null)next.Add(replacement);
                BindLinks(next);target.Restore(forward?after:before);InvalidateDynamics();RefreshRigPreview();SyncControls();
            }
            Change(child.Track,true);PushOperation(child.Track,Change);
        }
        void ParentDialog()
        {
            var child=actor.selectedBone;if(child==null)return;
            var options=drawOrder.SelectMany(a=>a.bones.Where(b=>!b.Joint.IsChildOf(child.Joint)).Select(b=>(a,b))).ToArray();
            var current=externalParents.Find(l=>l.child==child);
            var labels=new List<string>{"解除"};labels.AddRange(options.Select(o=>o.a.label+" / "+o.b.Track.Name));
            ShowDialog("外部親",out var body);
            var pick=new DropdownField("親ボーン",labels,Array.FindIndex(options,o=>o.b==current?.parent)+1);body.Add(pick);
            var error=new Label();body.Add(error);
            Action(body,"OK",true,()=>{
                try{var choice=pick.index>0?options[pick.index-1]:default;SetExternalParent(child,choice.a,choice.b);CloseDialog();}
                catch(InvalidDataException ex){error.text=ex.Message;}
            });
            Action(body,"キャンセル",true,CloseDialog);
        }
    }
}
