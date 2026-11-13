using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public sealed class SizingOptions
    {
        public bool Avoid,Align,Fingers,Floor,SkipArmCheck,CameraOnly;
        public double WristDistance=1.7,FingerDistance=1.4,FloorDistance=1.2,DistanceLimit=5;
        public string Camera,CameraOutput;
    }
    public static partial class Sizing
    {
        static Dictionary<string,(RigJoint source,RigJoint target)> MotionMapping(RigProfile source,RigProfile target)
        {
            var map=new Dictionary<string,(RigJoint,RigJoint)>(StringComparer.OrdinalIgnoreCase);
            foreach(var joint in source.joints)
            {
                var dest=target.Find(joint.role??joint.name,joint.name);
                if(dest==null)continue;
                foreach(var alias in new[]{joint.name,joint.role,RigProfile.MotionAlias(joint.role)})
                    if(!string.IsNullOrEmpty(alias) && !map.ContainsKey(alias))map.Add(alias,(joint,dest));
            }
            return map;
        }
        static Vector3 WorldDelta(Vector3 local,RigJoint parent)=>parent==null?local:Vector3.Transform(
            local*new Vector3(parent.scaleX,parent.scaleY,parent.scaleZ),SkeletonMotion.RestRotation(parent));
        static Vector3 LocalDelta(Vector3 world,RigJoint parent)
        {
            if(parent==null)return world;
            var local=Vector3.Transform(world,Quaternion.Inverse(SkeletonMotion.RestRotation(parent)));
            float Inverse(float v)=>Math.Abs(v)>1e-8?1/v:0;
            return local*new Vector3(Inverse(parent.scaleX),Inverse(parent.scaleY),Inverse(parent.scaleZ));
        }
        static Vector3 MovementOffset(ActorInput input)
        {
            var a=input.Source;var b=input.Target;
            var leg=Joint(a,"leftUpperLeg");var knee=Joint(a,"leftLowerLeg");var foot=Joint(a,"leftFoot");
            var targetLeg=Joint(b,"leftUpperLeg");var targetKnee=Joint(b,"leftLowerLeg");var targetFoot=Joint(b,"leftFoot");
            if(leg==null || knee==null || foot==null || targetLeg==null || targetKnee==null || targetFoot==null)return Vector3.Zero;
            var upper=SkeletonMotion.Rest(leg,a);var middle=SkeletonMotion.Rest(knee,a);var ankle=SkeletonMotion.Rest(foot,a);
            var convertedUpper=SkeletonMotion.Rest(targetLeg,b);var convertedKnee=SkeletonMotion.Rest(targetKnee,b);
            var convertedFoot=SkeletonMotion.Rest(targetFoot,b);var offsetFoot=convertedFoot-Vector3.UnitX*(float)input.Person.FootOffset;
            double length=Vector3.Distance(upper,middle)+Vector3.Distance(middle,offsetFoot);
            double targetLength=Vector3.Distance(convertedUpper,convertedKnee)+Vector3.Distance(convertedKnee,convertedFoot);
            double y=length<1e-8?0:Math.Min(0,(upper.Y-offsetFoot.Y)*targetLength/length-(convertedUpper.Y-convertedFoot.Y));
            var toe=Joint(a,"leftToes");var targetToe=Joint(b,"leftToes");double z=0;
            if(toe!=null && targetToe!=null && a.Find("左つま先ＩＫ","左つま先IK")!=null && b.Find("左つま先ＩＫ","左つま先IK")!=null){
                double span=ankle.Z-SkeletonMotion.Rest(toe,a).Z;double targetSpan=convertedFoot.Z-SkeletonMotion.Rest(targetToe,b).Z;
                if(Math.Abs(span)>1e-8 && Math.Abs(targetSpan)>1e-8)
                    z=((convertedFoot.Z-convertedUpper.Z)/targetSpan-(ankle.Z-upper.Z)/span)*targetSpan/span;
            }
            return new Vector3(0,(float)y,(float)z);
        }
        static bool MovementBone(string name)
        {
            switch(name.Normalize(System.Text.NormalizationForm.FormKC)){
                case "全ての親":case "センター":case "グルーブ":case "左足IK親":case "右足IK親":
                case "左足IK":case "右足IK":case "左つま先IK":case "右つま先IK":return true;
                default:return false;
            }
        }
        static void TransformRecord(byte[] row,ActorInput input,Dictionary<string,(RigJoint source,RigJoint target)> map,float horizontal)
        {
            string name=VmdDocument.Name(row);
            var delta=new Vector3(BitConverter.ToSingle(row,19),BitConverter.ToSingle(row,23),BitConverter.ToSingle(row,27));
            map.TryGetValue(name,out var match);
            string logical=MovementBone(name)?name:RigProfile.MotionAlias(match.source?.role);
            if(logical=="センター" && name!=logical && input.Source.Find("センター") is RigJoint center && center!=match.source)
                logical="";
            bool movement=MovementBone(logical);
            var scale=movement?new Vector3(horizontal,(float)input.Vertical,horizontal):Vector3.One;
            var offset=logical=="センター"?input.CenterOffset:Vector3.Zero;
            if(map.TryGetValue(name,out var pair))
            {
                var a=pair.source;var b=pair.target;
                var src=a.parent<0?null:input.Source.joints[a.parent];var dst=b.parent<0?null:input.Target.joints[b.parent];
                delta=LocalDelta(WorldDelta(delta,src)*scale+offset,dst);
                var basis=Quaternion.Inverse(SkeletonMotion.RestRotation(b))*SkeletonMotion.RestRotation(a);
                if(Math.Abs(basis.W)<.999999f){
                    var q=new Quaternion(BitConverter.ToSingle(row,31),BitConverter.ToSingle(row,35),BitConverter.ToSingle(row,39),BitConverter.ToSingle(row,43));
                    q=Quaternion.Normalize(basis*q*Quaternion.Inverse(basis));
                    VmdDocument.Number(row,31,q.X);VmdDocument.Number(row,35,q.Y);VmdDocument.Number(row,39,q.Z);VmdDocument.Number(row,43,q.W);
                }
                if(name==a.name && name!=b.name)Array.Copy(MmdText.Fixed(b.name,15),row,15);
            }
            else delta=delta*scale+offset;
            VmdDocument.Number(row,19,delta.X);VmdDocument.Number(row,23,delta.Y);VmdDocument.Number(row,27,delta.Z);
        }
        static RigJoint Joint(RigProfile rig,string role) => rig.Find(role,SkeletonMotion.Alias(role));
        static IEnumerable<uint> Times(SkeletonMotion motion,params RigJoint[] joints) =>
            joints.SelectMany(motion.Times).Distinct().OrderBy(t=>t);
        static void CorrectStance(ActorInput actor,SizingOptions options,Action<string> log,CancellationToken stop)
        {
            var person=actor.Person;
            if(person.Stance && person.StanceParts[0])CorrectCenterPlane(actor,stop);
            if(person.Stance && person.StanceParts[1]){
                CorrectTrunk(actor,"spine",true,stop);
                CorrectTrunk(actor,"chest",true,stop);
            }
            if(person.Stance && person.StanceParts[2])CorrectTrunk(actor,"hips",false,stop);
            foreach(string side in new[]{"left","right"})
            {
                string jp=side=="left"?"左":"右";
                if(person.Stance)CorrectFeet(actor,side,stop);
                var foot=Joint(actor.Target,side+"Foot");var other=Joint(actor.Target,(side=="left"?"right":"left")+"Foot");
                if(foot==null || other==null)continue;
                var outwards=SkeletonMotion.Rest(foot,actor.Target)-SkeletonMotion.Rest(other,actor.Target);
                outwards.Y=0;if(outwards.LengthSquared()<1e-8f)continue;outwards=Vector3.Normalize(outwards)*(float)person.FootOffset;
                foreach(var row in actor.Motion.Records(MotionChannel.Bone))
                {
                    var name=VmdDocument.Name(row).Normalize(System.Text.NormalizationForm.FormKC);
                    if(name!=jp+"足IK" && name!=jp+"つま先IK")continue;
                    var offset=outwards;
                    VmdDocument.Number(row,19,BitConverter.ToSingle(row,19)+offset.X);
                    VmdDocument.Number(row,23,BitConverter.ToSingle(row,23)+offset.Y);
                    VmdDocument.Number(row,27,BitConverter.ToSingle(row,27)+offset.Z);
                }
            }
            bool arms=options.SkipArmCheck || ArmStructure(actor.Source) && ArmStructure(actor.Target);
            if(!arms){log(actor.Target.name+" 腕の基準ボーンが揃っていないため腕のスタンス補正をスキップ");return;}
            foreach(string side in new[]{"left","right"})
            {
                string jp=side=="left"?"左":"右";
                bool armIK=actor.Original.Records(MotionChannel.Bone).Any(r=>VmdDocument.Name(r).Normalize(System.Text.NormalizationForm.FormKC).StartsWith(jp+"腕IK",StringComparison.Ordinal));
                bool armReady=options.SkipArmCheck || !armIK;
                if(!armReady)log(actor.Target.name+" "+jp+"腕IKのスタンス補正をスキップ");
                if(armReady && person.Stance && person.StanceParts[6])CorrectShoulder(actor,side,stop);
                if(armReady){
                    var reference=side=="left"?Vector3.UnitX:-Vector3.UnitX;
                    var prior=Quaternion.Identity;
                    var chain=new[]{"UpperArm","LowerArm","Hand","MiddleProximal"};
                    for(int i=0;i+1<chain.Length;i++){
                        var a=Joint(actor.Source,side+chain[i]);var b=Joint(actor.Source,side+chain[i+1]);
                        var c=Joint(actor.Target,side+chain[i]);var d=Joint(actor.Target,side+chain[i+1]);
                        if(a==null || b==null || c==null || d==null)break;
                        var from=SkeletonMotion.Between(reference,SkeletonMotion.Rest(b,actor.Source)-SkeletonMotion.Rest(a,actor.Source));
                        var to=SkeletonMotion.Between(reference,SkeletonMotion.Rest(d,actor.Target)-SkeletonMotion.Rest(c,actor.Target));
                        var difference=Quaternion.Inverse(to)*from;
                        actor.After.CorrectRestAxes(c,prior,difference);prior=difference;
                    }
                }
                if(person.Twist && armReady)
                {
                    actor.After.DistributeTwist(Joint(actor.Target,side+"UpperArm"),actor.Target.Find(side+"ArmTwist",jp+"腕捩",jp+"腕捩り"),
                        Joint(actor.Target,side+"LowerArm"),actor.Target.Find(side+"WristTwist",jp+"手捩",jp+"手捩り"),Joint(actor.Target,side+"Hand"),stop);
                }
            }
            if(person.Stance && person.StanceParts[7])CorrectCenterHeight(actor,stop);
        }
        sealed class Hand
        {
            internal ActorInput actor;
            internal RigJoint source,sourceShoulder,shoulder,sourceElbow,elbow,target,sourceTip,targetTip;
            internal float reachRatio;
            internal (RigJoint source,RigJoint target)[] fingers;
            internal float palm;
            internal readonly HashSet<uint> holdShoulder=new HashSet<uint>(),holdElbow=new HashSet<uint>();
        }
        const string HeadShape="頭接触回避";
        internal static bool ArmStructure(RigProfile rig)
        {
            if(rig.joints.Any(j=>(j.name??"").Normalize(System.Text.NormalizationForm.FormKC).Contains("腕IK")))return false;
            foreach(string side in new[]{"left","right"}){
                var arm=Joint(rig,side+"UpperArm");var elbow=Joint(rig,side+"LowerArm");var wrist=Joint(rig,side+"Hand");
                bool Below(RigJoint child,RigJoint parent){
                    for(int i=0;child!=null && i<rig.joints.Length;i++){
                        if(child==parent)return true;child=child.parent<0?null:rig.joints[child.parent];
                    }return false;
                }
                if(arm==null || elbow==null || wrist==null || !Below(elbow,arm) || !Below(wrist,elbow))return false;
            }
            return true;
        }
        static float AlignmentLength(RigJoint from,RigJoint to,RigProfile rig)
        {
            if(from==null || to==null)return 0;
            var span=SkeletonMotion.Rest(to,rig)-SkeletonMotion.Rest(from,rig);
            // Each absent axis contributes one motion unit to the alignment scale.
            if(Math.Abs(span.X)<1e-6f)span.X=1;
            if(Math.Abs(span.Y)<1e-6f)span.Y=1;
            if(Math.Abs(span.Z)<1e-6f)span.Z=1;
            return span.Length();
        }
        static void AlignHands(List<ActorInput> actors,SizingOptions options,CancellationToken stop,Action<string> log)
        {
            if(!options.Avoid && !options.Align)return;
            string jpSide(string side)=>side=="left"?"左":"右";
            var hands=new List<Hand>();
            foreach(var actor in actors.Where(a=>options.SkipArmCheck || ArmStructure(a.Source) && ArmStructure(a.Target)))foreach(var side in new[]{"left","right"})
            {
                var source=Joint(actor.Source,side+"Hand");var target=Joint(actor.Target,side+"Hand");
                var shoulder=Joint(actor.Target,side+"UpperArm");var elbow=Joint(actor.Target,side+"LowerArm");
                if(source==null || target==null || shoulder==null || elbow==null){log(actor.Target.name+" "+side+" 腕の対応ボーン不足");continue;}
                var finger=Joint(actor.Source,side+"MiddleProximal");
                var wrist=SkeletonMotion.Rest(source,actor.Source);
                var palm=finger!=null?Vector3.Distance(wrist,SkeletonMotion.Rest(finger,actor.Source)):
                    Vector3.Distance(SkeletonMotion.Rest(elbow,actor.Target),SkeletonMotion.Rest(target,actor.Target))*.35f;
                var sourceArm=Joint(actor.Source,side+"UpperArm");
                float sourceLength=AlignmentLength(sourceArm,source,actor.Source);
                float ratio=sourceLength<=1e-6f?1:AlignmentLength(shoulder,target,actor.Target)/sourceLength;
                hands.Add(new Hand {actor=actor,source=source,target=target,sourceShoulder=sourceArm,sourceElbow=Joint(actor.Source,side+"LowerArm"),elbow=elbow,shoulder=shoulder,palm=Math.Max(.01f,palm),reachRatio=ratio,
                    sourceTip=actor.Source.Find(side+"IndexTip",jpSide(side)+"人指先実体")??Joint(actor.Source,side+"IndexDistal"),
                    targetTip=actor.Target.Find(side+"IndexTip",jpSide(side)+"人指先実体")??Joint(actor.Target,side+"IndexDistal"),
                    fingers=new[]{"Thumb","Index","Middle","Ring","Little"}.Select(digit=>(source:actor.Source.Find(side+digit+"Tip")??Joint(actor.Source,side+digit+"Distal"),target:actor.Target.Find(side+digit+"Tip")??Joint(actor.Target,side+digit+"Distal")))
                        .Where(p=>p.source!=null && p.target!=null).ToArray()});
            }
            var times=actors.SelectMany(a=>a.Original.Records(MotionChannel.Bone)).Select(r=>VmdDocument.Frame(MotionChannel.Bone,r)).Distinct().OrderBy(n=>n).ToArray();
            var wrists=hands.Select(h=>(hand:h,source:h.source,target:h.target,ratio:h.reachRatio)).ToArray();
            var tips=hands.Where(h=>h.fingers.Length==5).SelectMany(h=>h.fingers.Select(p=>{
                var before=AlignmentLength(h.sourceShoulder,p.source,h.actor.Source);
                var after=AlignmentLength(h.shoulder,p.target,h.actor.Target);
                return (hand:h,p.source,p.target,ratio:before>1e-6f?after/before:1);
            })).ToArray();
            if(options.Avoid)foreach(var h in hands)AvoidArm(h,times,stop);
            if(!options.Align)return;
            bool Within(float distance,double limit)=>limit==10 || distance>1e-6f && distance<=limit;
            foreach(uint time in times)
            {
                stop.ThrowIfCancellationRequested();
                var goals=new Dictionary<Hand,(RigJoint end,Vector3 goal)>();
                void Match((Hand hand,RigJoint source,RigJoint target,float ratio)[] ends,double limit){
                    var pairs=new List<(int a,int b,float distance)>();
                    var before=ends.Select(e=>e.hand.actor.Before.World(e.source,time).position).ToArray();
                    var after=ends.Select(e=>e.hand.actor.After.World(e.target,time).position).ToArray();
                    for(int a=0;a<ends.Length;a++)for(int b=a+1;b<ends.Length;b++){
                        if(ends[a].hand==ends[b].hand)continue;
                        float distance=Vector3.Distance(before[a],before[b])/Math.Max(1e-6f,(ends[a].ratio+ends[b].ratio)*.5f);
                        if(Within(distance,limit))pairs.Add((a,b,distance));
                    }
                    foreach(var pair in pairs.OrderBy(p=>p.distance)){
                        if(goals.ContainsKey(ends[pair.a].hand) || goals.ContainsKey(ends[pair.b].hand))continue;
                        var midpoint=(after[pair.a]+after[pair.b])*.5f;
                        var offset=(before[pair.a]-before[pair.b])*.5f;
                        void Set(int index,Vector3 delta){
                            var e=ends[index];var person=e.hand.actor;
                            delta=Vector3.Transform(delta,Quaternion.Inverse(person.Before.TrunkBasis(time)));
                            if(actors.Count>1)delta*=Math.Min(1,(float)person.Horizontal);
                            else {delta.X*=Math.Min(1,e.ratio);delta.Y*=e.ratio;}
                            delta=Vector3.Transform(delta,person.After.TrunkBasis(time));
                            goals[e.hand]=(e.target,midpoint+delta);
                        }
                        Set(pair.a,offset);Set(pair.b,-offset);
                    }
                }
                Match(wrists,options.WristDistance);
                if(goals.Count==0 && options.Floor)foreach(var person in hands.GroupBy(h=>h.actor)){
                    var floor=person.Select(h=>(hand:h,height:h.actor.Before.World(h.source,time).position.Y))
                        .Where(p=>Within(Math.Abs(p.height)/Math.Max(1e-6f,(p.hand.reachRatio+(float)p.hand.actor.Horizontal)*.5f),options.FloorDistance))
                        .OrderBy(p=>Math.Abs(p.height)).FirstOrDefault();
                    if(floor.hand==null)continue;
                    var h=floor.hand;var goal=h.actor.After.World(h.target,time).position;goal.Y=floor.height;goals[h]=(h.target,goal);
                }
                if(goals.Count==0 && options.Fingers)Match(tips,options.FingerDistance);
                foreach(var item in goals){
                    var h=item.Key;var actor=h.actor;var (end,goal)=item.Value;
                    if(h.holdElbow.Contains(time))continue;
                    if(Vector3.DistanceSquared(goal,actor.After.World(end,time).position)>1e-9f)
                        actor.After.Reach(h.shoulder,h.elbow,end,time,goal,actor.Before.Bent(h.sourceElbow,time),
                            actor.Horizontal>.5?.7f:.3f,!h.holdShoulder.Contains(time));
                    if(end!=h.target || h.sourceTip==null || h.targetTip==null)continue;
                    var src=actor.Before.TrunkBasis(time);var dst=actor.After.TrunkBasis(time);
                    var sourceRay=actor.Before.World(h.sourceTip,time).position-actor.Before.World(h.source,time).position;
                    var origin=actor.After.World(h.target,time).position;
                    var ray=Vector3.Transform(actor.After.World(h.targetTip,time).position-origin,Quaternion.Inverse(dst));
                    ray.X=Vector3.Transform(sourceRay,Quaternion.Inverse(src)).X;
                    actor.After.Aim(h.target,h.targetTip,time,origin+Vector3.Transform(ray,dst));
                }
            }
        }


        static string CameraPath(List<ActorInput> inputs,SizingOptions options)
        {
            string path=options.CameraOutput;
            if(string.IsNullOrWhiteSpace(path))path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Camera)),
                Path.GetFileNameWithoutExtension(options.Camera)+"_"+Path.GetFileNameWithoutExtension(inputs[0].Person.Target.Split('|')[0])+
                "("+options.DistanceLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)+")_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".vmd");
            return path;
        }
    }
}
