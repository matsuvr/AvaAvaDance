using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public static partial class Sizing
    {
        // The chest and pelvis each define a frame from a longitudinal landmark and the opposite limb roots.
        static Quaternion TorsoFrame(Vector3 forward,Vector3 across)
        {
            if(forward.LengthSquared()<1e-9f)return Quaternion.Identity;
            var z=Vector3.Normalize(forward);
            var normal=Vector3.Cross(z,across);
            if(normal.LengthSquared()<1e-9f)return SkeletonMotion.Between(Vector3.UnitZ,z);
            var y=Vector3.Normalize(normal);var x=Vector3.Cross(y,z);
            return Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1));
        }

        static void CorrectTrunk(ActorInput actor,string role,bool upper,CancellationToken stop)
        {
            var src=actor.Source;var dst=actor.Target;
            var a=Joint(src,role);var b=Joint(dst,role);
            if(a==null || b==null || !actor.After.Times(b).Any())return;
            bool Landmarks(RigProfile rig,RigJoint pivot,out Vector3 ray,out Vector3 across)
            {
                ray=across=Vector3.Zero;
                var limb=upper?"UpperArm":"UpperLeg";
                var left=Joint(rig,"left"+limb);var right=Joint(rig,"right"+limb);
                if(left==null || right==null)return false;
                var l=SkeletonMotion.Rest(left,rig);var r=SkeletonMotion.Rest(right,rig);
                across=r-l;
                var end=upper?Joint(rig,"head"):rig.Find("legMidpoint","足中間");
                if(upper && (end==null || Joint(rig,"neck")==null))return false;
                ray=(end!=null?SkeletonMotion.Rest(end,rig):(l+r)*.5f)-SkeletonMotion.Rest(pivot,rig);
                return true;
            }
            if(!Landmarks(src,a,out var sourceAim,out var sourceAcross) || !Landmarks(dst,b,out var targetAim,out var targetAcross))return;
            float RatioAxis(float after,float before)=>after==0?1:after/(before==0?1:before);
            float width=RatioAxis(targetAcross.X,sourceAcross.X);
            var scale=new Vector3(width,RatioAxis(targetAim.Y,sourceAim.Y),width);
            bool similar=sourceAim.LengthSquared()>1e-9f && targetAim.LengthSquared()>1e-9f &&
                Vector3.Dot(Vector3.Normalize(sourceAim),Vector3.Normalize(targetAim))>=.8f;
            var neutral=similar?TorsoFrame(sourceAim*scale,sourceAcross):TorsoFrame(targetAim,-Vector3.UnitX);
            var reference=Quaternion.Inverse(neutral);
            var parent=b.parent>=0?dst.joints[b.parent]:null;
            foreach(uint t in actor.After.Times(b).ToArray()){
                stop.ThrowIfCancellationRequested();
                var pose=actor.Before.WorldRotation(a,t);
                var aim=Vector3.Transform(sourceAim,pose)*scale;
                var side=Vector3.Transform(sourceAcross,pose);
                var rotation=Quaternion.Normalize(Quaternion.Inverse(actor.After.WorldRotation(parent,t))*TorsoFrame(aim,side)*reference);
                if(!similar || Math.Abs(Quaternion.Dot(rotation,actor.Before.LocalRotation(a,t)))>=.9f)
                    actor.After.SetLocalRotation(b,t,rotation);
            }
            foreach(string child in upper?new[]{"neck","leftUpperArm","rightUpperArm"}:new[]{"leftUpperLeg","rightUpperLeg"}){
                var target=Joint(dst,child);if(target==null)continue;
                string ik=child.StartsWith("left",StringComparison.Ordinal)?"左足ＩＫ":"右足ＩＫ";
                var handle=upper?null:dst.Find(ik,ik.Normalize(System.Text.NormalizationForm.FormKC),
                    child.StartsWith("left",StringComparison.Ordinal)?"leftFootIK":"rightFootIK");
                foreach(uint t in actor.After.Times(target).ToArray()){
                    stop.ThrowIfCancellationRequested();
                    if(handle!=null && actor.After.IkActive(ik,t))continue;
                    var compensate=Quaternion.Inverse(actor.After.LocalRotation(b,t))*actor.Before.LocalRotation(a,t);
                    actor.After.SetLocalRotation(target,t,compensate*actor.After.LocalRotation(target,t));
                }
            }
        }

        static void CorrectShoulder(ActorInput actor,string side,CancellationToken stop)
        {
            var source=actor.Source;var target=actor.Target;var prefix=side=="left"?"左":"右";
            var shoulder=Joint(source,side+"Shoulder");var arm=Joint(source,side+"UpperArm");
            var targetShoulder=Joint(target,side+"Shoulder");var targetArm=Joint(target,side+"UpperArm");
            if(shoulder==null || arm==null || targetShoulder==null || targetArm==null ||
                !actor.After.Times(targetShoulder).Any())return;
            var before=actor.Before;var after=actor.After;
            var sourceRay=SkeletonMotion.Rest(arm,source)-SkeletonMotion.Rest(shoulder,source);
            var targetRay=SkeletonMotion.Rest(targetArm,target)-SkeletonMotion.Rest(targetShoulder,target);
            if(sourceRay.LengthSquared()<1e-9f || targetRay.LengthSquared()<1e-9f)return;
            double alignment=Vector3.Dot(Vector3.Normalize(sourceRay),Vector3.Normalize(targetRay));
            bool near=alignment>=.82;
            Vector3 Base(RigProfile rig){
                var left=Joint(rig,near?"leftShoulder":"leftUpperArm");var right=Joint(rig,near?"rightShoulder":"rightUpperArm");
                var marker=rig.Find(near?"neckRoot":"armMidpoint",near?"首根元":"首根元2");
                if(marker!=null)return SkeletonMotion.Rest(marker,rig);
                if(left==null || right==null)return new Vector3(float.NaN);
                var mid=(SkeletonMotion.Rest(left,rig)+SkeletonMotion.Rest(right,rig))*.5f;mid.X=0;return mid;
            }
            float Width(RigProfile rig){
                var l=Joint(rig,"leftUpperArm");var r=Joint(rig,"rightUpperArm");
                return l!=null && r!=null?SkeletonMotion.Rest(l,rig).X-SkeletonMotion.Rest(r,rig).X:0;
            }
            var srcBase=Base(source);var dstBase=Base(target);if(float.IsNaN(srcBase.X+dstBase.X))return;
            float width=Width(source);float x=Math.Abs(width)>1e-6f?Width(target)/width:1;
            float sy=SkeletonMotion.Rest(arm,source).Y-srcBase.Y,dy=SkeletonMotion.Rest(targetArm,target).Y-dstBase.Y;
            if(Math.Round(sy,1)==0)sy=1;if(Math.Round(dy,1)==0)dy=1;
            var scale=new Vector3(x,dy/sy,x);
            var upper=source.Find(side+"ShoulderParent",prefix+"肩P");var destUpper=target.Find(side+"ShoulderParent",prefix+"肩P");
            var boundary=upper??shoulder;var parent=boundary.parent<0?null:source.joints[boundary.parent];
            var targetBoundary=destUpper??targetShoulder;
            var targetParent=targetBoundary.parent<0?null:target.joints[targetBoundary.parent];
            Vector3 WorldMarker(SkeletonMotion motion,RigProfile rig,RigJoint anchor,Vector3 restPoint,uint t)=>
                anchor==null?restPoint:motion.World(anchor,t).position+
                    Vector3.Transform(restPoint-SkeletonMotion.Rest(anchor,rig),motion.WorldRotation(anchor,t));
            var rest=SkeletonMotion.Rest(targetShoulder,target);
            var neutralAim=dstBase-rest+(SkeletonMotion.Rest(arm,source)-srcBase)*scale;
            var targetBasis=TorsoFrame(targetRay,new Vector3(1,-1,0));
            var neutral=TorsoFrame(neutralAim,-Vector3.UnitY)*Quaternion.Inverse(targetBasis);
            var frames=Times(after,targetShoulder,destUpper).ToArray();
            foreach(uint t in frames){
                stop.ThrowIfCancellationRequested();
                var local=Quaternion.Inverse(before.WorldRotation(parent,t))*before.WorldRotation(shoulder,t);
                var sourceShoulder=parent==null?before.World(shoulder,t).position:SkeletonMotion.Rest(parent,source)+
                    Vector3.Transform(before.World(shoulder,t).position-before.World(parent,t).position,Quaternion.Inverse(before.WorldRotation(parent,t)));
                var desired=dstBase-rest+(sourceShoulder-srcBase+Vector3.Transform(sourceRay,local))*scale;
                Quaternion result;
                if(near)result=Quaternion.Normalize(Quaternion.Inverse(neutral)*TorsoFrame(desired,Vector3.Transform(-Vector3.UnitY,local))*Quaternion.Inverse(targetBasis));
                else{
                    var srcOrigin=WorldMarker(before,source,parent,srcBase,t);
                    var dstOrigin=WorldMarker(after,target,targetParent,dstBase,t);
                    var sourceArm=before.World(arm,t).position-srcOrigin;
                    var destination=dstOrigin+sourceArm*scale-WorldMarker(after,target,targetParent,rest,t);
                    result=SkeletonMotion.Between(Vector3.Transform(targetRay,after.WorldRotation(targetParent,t)),destination);
                }
                float minimum=near?.9f:alignment>=.5?.4f:0;
                if(Math.Abs(Quaternion.Dot(result,before.LocalRotation(shoulder,t)))<minimum)result=before.LocalRotation(shoulder,t);
                if(destUpper!=null && upper!=null && after.Times(destUpper).Contains(t))after.SetLocalRotation(destUpper,t,Quaternion.Identity);
                after.SetLocalRotation(targetShoulder,t,result);
            }
            foreach(uint t in after.Times(targetArm).ToArray()){
                stop.ThrowIfCancellationRequested();
                var preserve=Quaternion.Inverse(after.LocalRotation(targetShoulder,t))*before.LocalRotation(shoulder,t);
                after.SetLocalRotation(targetArm,t,preserve*after.LocalRotation(targetArm,t));
            }
        }

        static void CorrectCenterHeight(ActorInput actor,CancellationToken stop)
        {
            var center=actor.Target.Find("センター")??Joint(actor.Target,"hips");
            var groove=actor.Target.Find("グルーブ");
            if(center==null || groove==null)return;
            var hands=new[]{("left","左"),("right","右")}.Select(s=>(
                wrist:Joint(actor.Source,s.Item1+"Hand"),target:Joint(actor.Target,s.Item1+"Hand"),
                tip:actor.Source.Find(s.Item1+"IndexTip",s.Item2+"人指先実体"),
                leg:Joint(actor.Source,s.Item1+"UpperLeg"),ik:actor.Target.Find(s.Item1+"FootIK",s.Item2+"足IK",s.Item2+"足ＩＫ"))).ToArray();
            if(hands.Any(h=>h.wrist==null || h.target==null || h.leg==null))return;
            float threshold=1.5f*hands.Max(h=>h.tip==null?0:Vector3.Distance(SkeletonMotion.Rest(h.wrist,actor.Source),SkeletonMotion.Rest(h.tip,actor.Source)));
            foreach(uint t in Times(actor.After,center,groove).ToArray()){
                stop.ThrowIfCancellationRequested();
                var g=actor.After.PositionKey(groove,t);var c=actor.After.PositionKey(center,t);
                if(g.Y==0 || (Math.Abs(c.Y)>Math.Abs(g.Y)?c.Y:g.Y)>=0)continue;
                var which=c.Y==0 || g.Y>=c.Y?groove:center;
                var position=actor.After.PositionKey(which,t);
                float correction=0;
                foreach(var hand in hands){
                    float y=actor.Before.World(hand.wrist,t).position.Y;if(y>=threshold)continue;
                    float candidate=y*(float)actor.Vertical-actor.After.World(hand.target,t).position.Y;
                    if(candidate!=0 && (correction==0 || Math.Abs(candidate)<Math.Abs(correction)))correction=candidate;
                }
                actor.After.SetPositionKey(which,t,position+new Vector3(0,correction,0));
                foreach(var h in hands)
                    if(h.ik!=null && actor.Before.World(h.leg,t).position.Y>=threshold)
                        actor.After.SetPositionKey(h.ik,t,actor.After.PositionKey(h.ik,t));
            }
        }

        static void CorrectCenterPlane(ActorInput actor,CancellationToken stop)
        {
            RigJoint Center(RigProfile rig)=>rig.Find("センター")??Joint(rig,"hips");
            var a=Center(actor.Source);var b=Center(actor.Target);
            if(a==null || b==null)return;
            bool Offset(RigProfile rig,SkeletonMotion pose,RigJoint center,uint frame,out Vector3 offset){
                offset=Vector3.Zero;
                var left=rig.Find("leftFootIK","左足ＩＫ","左足IK");var right=rig.Find("rightFootIK","右足ＩＫ","右足IK");
                var upper=Joint(rig,"spine");var lower=rig.Find("下半身")??Joint(rig,"hips");
                if(left==null || right==null || upper==null || lower==null)return false;
                var pivot=rig.Find("グルーブ")??center;
                var feet=(pose.World(left,frame).position+pose.World(right,frame).position)*.5f;
                var trunk=(SkeletonMotion.Rest(upper,rig)+SkeletonMotion.Rest(lower,rig))*.5f;
                offset=feet-pose.World(pivot,frame).position+SkeletonMotion.Rest(center,rig)-trunk;
                offset.Y=0;return true;
            }
            foreach(var row in actor.Motion.Records(MotionChannel.Bone)){
                if(VmdDocument.Name(row)!=b.name && VmdDocument.Name(row)!="センター")continue;
                stop.ThrowIfCancellationRequested();uint t=VmdDocument.Frame(MotionChannel.Bone,row);
                if(!Offset(actor.Source,actor.Before,a,t,out var before) || !Offset(actor.Target,actor.After,b,t,out var after))continue;
                var delta=after-before*(float)actor.Horizontal;
                if(b.parent>=0)delta=LocalDelta(delta,actor.Target.joints[b.parent]);
                for(int j=0;j<3;j++){int at=19+j*4;VmdDocument.Number(row,at,BitConverter.ToSingle(row,at)+(j==0?delta.X:j==1?delta.Y:delta.Z));}
                actor.After.InvalidatePose();
            }
        }
    }
}
