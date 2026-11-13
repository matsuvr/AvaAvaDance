using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public static partial class Sizing
    {
        static RigProfile LegControls(RigProfile rig,VmdDocument motion)
        {
            var names=motion.Records(MotionChannel.Bone).Select(r=>VmdDocument.Name(r).Normalize(System.Text.NormalizationForm.FormKC))
                .ToHashSet(StringComparer.Ordinal);
            var joints=new List<RigJoint>(rig.joints);
            foreach(string side in new[]{"left","right"}){
                string prefix=side=="left"?"左":"右";
                if(!names.Contains(prefix+"足IK") && !names.Contains(prefix+"つま先IK"))continue;
                var thigh=Joint(rig,side+"UpperLeg");var knee=Joint(rig,side+"LowerLeg");var foot=Joint(rig,side+"Foot");
                if(thigh==null || knee==null || foot==null)continue;
                RigJoint Add(string role,string name,RigJoint at,int parent,RigJoint jointRoot){
                    var joint=new RigJoint{name=name,role=role,parent=parent,x=at.x,y=at.y,z=at.z,
                        qw=1,ikRoot=jointRoot.name,origin="humanoid"};
                    joints.Add(joint);return joint;
                }
                var ik=rig.Find(side+"FootIK",prefix+"足IK",prefix+"足ＩＫ");
                if(ik==null){
                    var root=rig.Find("全ての親");
                    ik=Add(side+"FootIK",prefix+"足ＩＫ",foot,root==null?-1:joints.IndexOf(root),thigh);
                }
                var toe=Joint(rig,side+"Toes");
                if(toe!=null && rig.Find(side+"ToesIK",prefix+"つま先IK",prefix+"つま先ＩＫ")==null)
                    Add(side+"ToesIK",prefix+"つま先ＩＫ",toe,joints.IndexOf(ik),foot);
            }
            if(joints.Count==rig.joints.Length)return rig;
            return new RigProfile{format=rig.format,name=rig.name,sourceId=rig.sourceId,sourceHash=rig.sourceHash,editRest=rig.editRest,
                morphs=rig.morphs,shapes=rig.shapes,schemaVersion=rig.schemaVersion,measurementVersion=rig.measurementVersion,
                metersPerMotionUnit=rig.metersPerMotionUnit,motionEncoding=rig.motionEncoding,cameraEncoding=rig.cameraEncoding,joints=joints.ToArray()};
        }

        static void CorrectFeet(ActorInput actor,string side,CancellationToken stop)
        {
            string prefix=side=="left"?"左":"右";
            var a=actor.Source;var b=actor.Target;
            RigJoint Handle(RigProfile rig,bool toe)=>rig.Find(side+(toe?"ToesIK":"FootIK"),prefix+(toe?"つま先ＩＫ":"足ＩＫ"),prefix+(toe?"つま先IK":"足IK"));
            var sourceIK=Handle(a,false);var targetIK=Handle(b,false);
            if(sourceIK==null || targetIK==null)return;
            var thigh=Joint(a,side+"UpperLeg");var targetThigh=Joint(b,side+"UpperLeg");
            bool LegChain(RigProfile rig,RigJoint handle,RigJoint upper)=>upper!=null &&
                (!string.IsNullOrEmpty(handle.ikRoot)?rig.Find(handle.ikRoot)==upper:handle.role==side+"FootIK");
            var before=actor.Before;var after=actor.After;var options=actor.Person.StanceParts;
            var auxiliaryLeg=a.Find(prefix+"足D");var auxiliaryKnee=a.Find(prefix+"ひざD");
            bool Enabled(uint t)=>before.IkActive(sourceIK.name,t) &&
                (auxiliaryLeg==null || Math.Abs(before.LocalRotation(auxiliaryLeg,t).W)>.999999f) &&
                (auxiliaryKnee==null || Math.Abs(before.LocalRotation(auxiliaryKnee,t).W)>.999999f);
            var localParent=targetIK.parent<0?null:b.joints[targetIK.parent];
            if(options[3] && LegChain(a,sourceIK,thigh) && LegChain(b,targetIK,targetThigh)){
                var ratio=new Vector3((float)actor.Horizontal,(float)actor.Vertical,(float)actor.Horizontal);
                foreach(uint t in after.Times(targetIK).ToArray()){
                    stop.ThrowIfCancellationRequested();
                    if(!Enabled(t))continue;
                    var distance=before.World(sourceIK,t).position-before.World(thigh,t).position;
                    var goal=after.World(targetThigh,t).position+distance*ratio;
                    var difference=goal-after.World(targetIK,t).position;
                    var original=after.PositionKey(targetIK,t);
                    var motion=localParent==null?difference:LocalDelta(Vector3.Transform(difference,Quaternion.Inverse(after.WorldRotation(localParent,t))),localParent);
                    after.SetPositionKey(targetIK,t,original+new Vector3(motion.X,0,motion.Z));
                }
            }
            var sourceToe=Handle(a,true);var targetToe=Handle(b,true);
            if(options[4]){
                Vector3? Marker(RigProfile rig,bool sole){
                    var measured=sole?rig.Find(side+"Sole",prefix+"足底実体"):rig.Find(side+"ToeTip",prefix+"つま先実体");
                    var fallback=Joint(rig,side+"Toes")??Handle(rig,true);
                    if(measured==null && fallback==null)return null;
                    var v=SkeletonMotion.Rest(measured??fallback,rig);
                    if(measured==null || !sole)v.Y=0;
                    return v;
                }
                var af=Joint(a,side+"Foot");var bf=Joint(b,side+"Foot");
                var toeA=Marker(a,false);var toeB=Marker(b,false);var soleA=Marker(a,true);var soleB=Marker(b,true);
                if(af!=null && bf!=null && toeA.HasValue && toeB.HasValue && soleA.HasValue && soleB.HasValue){
                    float len=Vector3.Distance(SkeletonMotion.Rest(af,a),toeA.Value);
                    float scale=len<1e-6f?1:Vector3.Distance(SkeletonMotion.Rest(bf,b),toeB.Value)/len;
                    foreach(uint t in after.Times(targetIK).ToArray()){
                        stop.ThrowIfCancellationRequested();
                        float Height(SkeletonMotion motion,RigProfile rig,RigJoint ik,Vector3 marker){
                            var p=motion.World(ik,t).position;
                            return (p+Vector3.Transform(marker-SkeletonMotion.Rest(ik,rig),motion.WorldRotation(ik,t))).Y;
                        }
                        var sourcePivot=sourceToe??sourceIK;var targetPivot=targetToe??targetIK;
                        float st=Height(before,a,sourcePivot,toeA.Value),ss=Height(before,a,sourcePivot,soleA.Value);
                        float tt=Height(after,b,targetPivot,toeB.Value),ts=Height(after,b,targetPivot,soleB.Value);
                        float amount=0;
                        if(tt<=ts && st<len)amount=st*scale-tt;
                        else if(ts<tt && ss<len)amount=(ss-soleA.Value.Y)*scale-(ts-soleB.Value.Y);
                        if(Math.Abs(amount)>1e-8f)after.SetPositionKey(targetIK,t,after.PositionKey(targetIK,t)+new Vector3(0,amount,0));
                    }
                }
            }
            if(!options[5] || sourceToe==null || targetToe==null)return;
            var frames=Times(after,targetIK,targetToe).ToArray();
            if(frames.Length<=1)return;
            var ray=SkeletonMotion.Rest(sourceToe,a)-SkeletonMotion.Rest(sourceIK,a);
            foreach(uint t in frames){
                stop.ThrowIfCancellationRequested();
                if(!Enabled(t))continue;
                var offset=after.PositionKey(targetToe,t);
                if(Math.Round(offset.X,2)==0 && Math.Round(offset.Y,2)==0 && Math.Round(offset.Z,2)==0)continue;
                var leg=before.WorldRotation(thigh,t);
                var ikParent=sourceIK.parent>=0?a.joints[sourceIK.parent]:null;
                var basis=before.WorldRotation(ikParent,t)*leg;
                var to=before.World(sourceToe,t).position-before.World(sourceIK,t).position;
                var turn=SkeletonMotion.Between(ray,Vector3.Transform(to,Quaternion.Inverse(basis)));
                after.SetLocalRotation(targetIK,t,leg*turn);
                after.SetPositionKey(targetToe,t,Vector3.Zero);after.SetLocalRotation(targetToe,t,Quaternion.Identity);
            }
        }
    }
}
