using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ava.Extensions;

namespace Ava.MotionSizing
{
    public static partial class NativeMotionSizing
    {
        static bool Arm(Pose pose,string side,out int upper,out int lower,out int hand)
        {
            upper=pose.Rig.Find(side+"UpperArm");lower=pose.Rig.Find(side+"LowerArm");hand=pose.Rig.Find(side+"Hand");
            return pose.Rig.Chain(upper,lower,hand);
        }
        static void AlignNativeHands(Pose source,Pose target,NativeSizingOptions options,Quaternion facing)
        {
            if(!Arm(source,"left",out _,out _,out int leftSource) || !Arm(source,"right",out _,out _,out int rightSource) ||
                !Arm(target,"left",out int leftUpper,out int leftLower,out int left) || !Arm(target,"right",out int rightUpper,out int rightLower,out int right))return;
            var separation=Vector3.Transform(source.World(leftSource).p-source.World(rightSource).p,facing);
            if(options.AlignHands && separation.Length()<=options.ContactDistance){
                var middle=(target.World(left).p+target.World(right).p)*.5f;
                // Keep the small authored separation, rather than collapsing both hands into one point.
                target.Solve(leftUpper,leftLower,left,middle+separation*.5f);
                target.Solve(rightUpper,rightLower,right,middle-separation*.5f);
            }
            if(!options.AlignFingers)return;
            var pairs=new List<(int sa,int sb,int ta,int tb,float distance)>();
            var fingers=new[]{"Thumb","Index","Middle","Ring","Little"};
            foreach(string a in fingers)foreach(string b in fingers){
                int Tip(Rig rig,string side,string name){int tip=rig.Find(side+name+"Tip");return tip>=0?tip:rig.Find(side+name+"Distal");}
                int sa=Tip(source.Rig,"left",a),sb=Tip(source.Rig,"right",b);
                int ta=Tip(target.Rig,"left",a),tb=Tip(target.Rig,"right",b);
                if(sa<0 || sb<0 || ta<0 || tb<0)continue;
                float distance=Vector3.Distance(source.World(sa).p,source.World(sb).p);
                if(distance<=options.ContactDistance)pairs.Add((sa,sb,ta,tb,distance));
            }
            var used=new HashSet<int>();
            foreach(var pair in pairs.OrderBy(p=>p.distance)){
                if(used.Contains(pair.ta) || used.Contains(pair.tb))continue;used.Add(pair.ta);used.Add(pair.tb);
                var delta=Vector3.Transform(source.World(pair.sa).p-source.World(pair.sb).p,facing);
                var middle=(target.World(pair.ta).p+target.World(pair.tb).p)*.5f;
                var a=middle+delta*.5f;var b=middle-delta*.5f;
                target.Solve(leftUpper,leftLower,left,target.World(left).p+a-target.World(pair.ta).p);
                target.Solve(rightUpper,rightLower,right,target.World(right).p+b-target.World(pair.tb).p);
                ReachFinger(target,pair.ta,left,a);ReachFinger(target,pair.tb,right,b);
            }
        }
        static void ReachFinger(Pose pose,int tip,int hand,Vector3 goal)
        {
            var chain=new List<int>();
            for(int joint=pose.Rig.Joints[tip].parent;joint>=0 && joint!=hand && chain.Count<pose.Rig.Joints.Length;joint=pose.Rig.Joints[joint].parent)
                if(pose.Rig.Tracks[joint]!=null)chain.Add(joint);
            for(int iteration=0;iteration<8 && Vector3.DistanceSquared(pose.World(tip).p,goal)>1e-8f;iteration++)
                foreach(int joint in chain){var pivot=pose.World(joint);pose.SetWorldRotation(joint,Between(pose.World(tip).p-pivot.p,goal-pivot.p)*pivot.q);}
        }

        sealed class BodyShape
        {
            internal int Joint;
            internal string Kind;
            internal Vector3 Center,Axis,BoxX,BoxY,BoxZ;
            internal float Radius,HalfSegment;
        }
        static BodyShape[] BodyShapes(Rig rig)
        {
            bool Body(int joint){
                for(int n=0;joint>=0 && n<rig.Joints.Length;n++,joint=rig.Joints[joint].parent){
                    string role=Role(rig.Joints[joint].role);if(role.Length==0)continue;
                    switch(role){case "hips":case "spine":case "chest":case "upperchest":case "neck":case "head":return true;default:return false;}
                }return false;
            }
            var shapes=new List<BodyShape>();
            Vector3 V(double[] a,int i=0)=>a==null || a.Length<i+3?Vector3.Zero:new Vector3((float)a[i],(float)a[i+1],(float)a[i+2]);
            foreach(var shape in rig.Profile.shapes??Array.Empty<RigShape>()){
                if(shape==null)continue;int joint=shape.jointIndex;
                if(joint<0 || joint>=rig.Joints.Length){joint=rig.Find(shape.role);if(joint<0)joint=Array.FindIndex(rig.Joints,j=>j.name==shape.role);}
                if(joint<0 || !Body(joint))continue;
                var scale=Scale(rig.Joints[joint]);float largest=Math.Max(Math.Abs(scale.X),Math.Max(Math.Abs(scale.Y),Math.Abs(scale.Z)));
                var result=new BodyShape{Joint=joint,Kind=shape.shape??"sphere",Center=V(shape.center)*scale,Radius=(float)shape.radius*largest};
                if(result.Kind=="box"){
                    if(shape.boxAxes?.Length!=9)continue;result.BoxX=V(shape.boxAxes)*scale;result.BoxY=V(shape.boxAxes,3)*scale;result.BoxZ=V(shape.boxAxes,6)*scale;
                }else if(result.Radius>0){
                    var axis=(shape.axis?.Length==3?V(shape.axis):Vector3.UnitY)*scale;
                    result.HalfSegment=Math.Max(0,(float)shape.height*axis.Length()*.5f-result.Radius);
                    result.Axis=axis.LengthSquared()>1e-12f?Vector3.Normalize(axis):Vector3.UnitY;
                }else continue;
                shapes.Add(result);
            }
            int head=rig.Find("head"),neck=rig.Find("neck");
            if(head>=0 && !shapes.Any(s=>s.Joint==head)){
                float radius=neck>=0?Vector3.Distance(Point(rig.Joints[head]),Point(rig.Joints[neck])):rig.LegLength()*.12f;
                if(radius>0)shapes.Add(new BodyShape{Joint=head,Kind="sphere",Radius=radius,Axis=Vector3.UnitY});
            }
            return shapes.ToArray();
        }
        static Vector3 PushOut(Pose pose,BodyShape shape,Vector3 point,float clearance,Vector3 fallback)
        {
            var anchor=pose.World(shape.Joint);var center=anchor.p+Vector3.Transform(shape.Center,anchor.q);
            var local=Vector3.Transform(point-center,Quaternion.Inverse(anchor.q));Vector3 push;
            if(shape.Kind=="box"){
                var x=shape.BoxX;var y=shape.BoxY;var z=shape.BoxZ;float volume=Vector3.Dot(x,Vector3.Cross(y,z));
                if(Math.Abs(volume)<1e-12f)return Vector3.Zero;
                var coordinate=new Vector3(Vector3.Dot(local,Vector3.Cross(y,z)),Vector3.Dot(local,Vector3.Cross(z,x)),Vector3.Dot(local,Vector3.Cross(x,y)))/volume;
                if(Math.Abs(coordinate.X)>=1 || Math.Abs(coordinate.Y)>=1 || Math.Abs(coordinate.Z)>=1)return Vector3.Zero;
                var axes=new[]{x,y,z};var values=new[]{coordinate.X,coordinate.Y,coordinate.Z};int nearest=0;float distance=float.MaxValue;
                for(int i=0;i<3;i++){float d=(1-Math.Abs(values[i]))*axes[i].Length();if(d<distance){distance=d;nearest=i;}}
                float sign=values[nearest]<0?-1:1;var axis=axes[nearest];if(axis.LengthSquared()<1e-12f)return Vector3.Zero;
                push=axis*(sign-values[nearest])+Vector3.Normalize(axis)*sign*clearance;
            }else{
                var pivot=shape.Kind=="capsule"?shape.Axis*Math.Clamp(Vector3.Dot(local,shape.Axis),-shape.HalfSegment,shape.HalfSegment):Vector3.Zero;
                var ray=local-pivot;float length=ray.Length(),radius=shape.Radius+clearance;if(length>=radius)return Vector3.Zero;
                var direction=length>1e-7f?ray/length:Vector3.Transform(fallback,Quaternion.Inverse(anchor.q));
                if(direction.LengthSquared()<1e-12f)direction=Vector3.UnitX;push=Vector3.Normalize(direction)*(radius-length);
            }
            return Vector3.Transform(push,anchor.q);
        }
        static void AvoidNativeBody(Pose pose,BodyShape[] shapes,float distance)
        {
            foreach(string side in new[]{"left","right"}){
                if(!Arm(pose,side,out int upper,out int lower,out int hand))continue;
                var lateral=Point(pose.Rig.Joints[hand])-Point(pose.Rig.Joints[upper]);lateral.Y=0;
                if(lateral.LengthSquared()<1e-10f)lateral=side=="left"?Vector3.UnitX:-Vector3.UnitX;
                for(int pass=0;pass<3;pass++)foreach(var shape in shapes){
                    var wrist=pose.World(hand).p;var elbow=pose.World(lower).p;
                    var push=PushOut(pose,shape,wrist,distance*.1f,lateral);
                    var middle=PushOut(pose,shape,(wrist+elbow)*.5f,distance*.1f,lateral);
                    var elbowPush=PushOut(pose,shape,elbow,distance*.1f,lateral);
                    if(middle.LengthSquared()>push.LengthSquared())push=middle;
                    if(elbowPush.LengthSquared()>push.LengthSquared())push=elbowPush;
                    if(push.LengthSquared()>1e-12f)pose.Solve(upper,lower,hand,wrist+push);
                }
            }
        }

        static Quaternion Axial(Quaternion q,Vector3 axis)
        {
            if(axis.LengthSquared()<1e-12f)return Quaternion.Identity;axis=Vector3.Normalize(axis);
            return Unit(new Quaternion(axis*Vector3.Dot(new Vector3(q.X,q.Y,q.Z),axis),q.W));
        }
        static void DistributeNativeTwist(Pose pose)
        {
            foreach(string side in new[]{"left","right"}){
                if(!Arm(pose,side,out int arm,out int elbow,out int hand))continue;
                int roll=pose.Rig.Find(side+"ArmTwist"),wrist=pose.Rig.Find(side+"WristTwist");
                if(roll>=0 && pose.Rig.Tracks[roll]!=null && pose.Rig.Descendant(roll,arm) && pose.Rig.Descendant(elbow,roll)){
                    var a=Copy(pose.Values[arm]);var r=Copy(pose.Values[roll]);var e=pose.World(elbow);var h=pose.World(hand);
                    var rest=Basis(pose.Rig.Joints[arm]);var delta=Unit(rest*a.Rotation*Quaternion.Inverse(rest));
                    var axis=Point(pose.Rig.Joints[elbow])-Point(pose.Rig.Joints[arm]);var twist=Axial(delta,axis);
                    if(!SameRotation(twist,Quaternion.Identity)){
                        pose.Values[arm].Rotation=Unit(Quaternion.Inverse(rest)*delta*Quaternion.Inverse(twist)*rest);pose.Invalidate();
                        var correction=e.q*Quaternion.Inverse(pose.World(elbow).q);pose.SetWorldRotation(roll,correction*pose.World(roll).q);
                        // Off-axis helper bones are not roll joints. Preserve the authored contact pose in that case.
                        if(Vector3.DistanceSquared(pose.World(elbow).p,e.p)>1e-9f || Vector3.DistanceSquared(pose.World(hand).p,h.p)>1e-9f){pose.Values[arm]=a;pose.Values[roll]=r;pose.Invalidate();}
                    }
                }
                if(wrist>=0 && pose.Rig.Tracks[wrist]!=null && pose.Rig.Descendant(wrist,elbow) && pose.Rig.Descendant(hand,wrist)){
                    var w=Copy(pose.Values[wrist]);var h=Copy(pose.Values[hand]);var desired=pose.World(hand);
                    var rest=Basis(pose.Rig.Joints[hand]);var delta=Unit(rest*h.Rotation*Quaternion.Inverse(rest));
                    var axis=Point(pose.Rig.Joints[hand])-Point(pose.Rig.Joints[elbow]);var twist=Axial(delta,axis);
                    if(SameRotation(twist,Quaternion.Identity) || axis.LengthSquared()<1e-12f)continue;
                    float amount=2*(float)Math.Atan2(Vector3.Dot(new Vector3(twist.X,twist.Y,twist.Z),Vector3.Normalize(axis)),twist.W);
                    var live=desired.p-pose.World(elbow).p;if(live.LengthSquared()<1e-12f)continue;
                    pose.SetWorldRotation(wrist,Quaternion.CreateFromAxisAngle(Vector3.Normalize(live),amount)*pose.World(wrist).q);
                    pose.SetWorldRotation(hand,desired.q);
                    if(Vector3.DistanceSquared(pose.World(hand).p,desired.p)>1e-9f){pose.Values[wrist]=w;pose.Values[hand]=h;pose.Invalidate();}
                }
            }
        }

        sealed class MorphTransfer
        {
            internal Timeline target;
            internal readonly List<(Timeline track,float factor)> terms=new List<(Timeline,float)>();
        }
        static string MorphName(string name)=>name!=null && name.StartsWith("表情：",StringComparison.Ordinal)?name.Substring(3):name;
        static MorphTransfer[] MorphMapping(Timeline[] source,Timeline[] target,string text,bool same,List<string> messages)
        {
            if(same && string.IsNullOrWhiteSpace(text))return Array.Empty<MorphTransfer>();
            var rules=new List<(string from,string to,float factor)>();
            foreach(string line in (text??"").Split(new[]{';','\r','\n'},StringSplitOptions.RemoveEmptyEntries)){
                var fields=line.Split(':');
                if(fields.Length!=3 || string.IsNullOrWhiteSpace(fields[0]) || string.IsNullOrWhiteSpace(fields[1]) ||
                    !float.TryParse(fields[2],NumberStyles.Float,CultureInfo.InvariantCulture,out float factor) || !float.IsFinite(factor))
                    throw new ArgumentException("表情の置換は 元の表情:適用先の表情:倍率 の形式で指定してください。");
                var rule=(fields[0].Trim(),fields[1].Trim(),factor);if(!rules.Contains(rule))rules.Add(rule);
            }
            var transfers=new Dictionary<Timeline,MorphTransfer>();
            MorphTransfer Get(Timeline t){if(!transfers.TryGetValue(t,out var value))transfers.Add(t,value=new MorphTransfer{target=t});return value;}
            Timeline Find(string name){var all=target.Where(t=>t.Track.Channel==TrackChannel.Morph && (t.Track.Name==name || MorphName(t.Track.Name)==MorphName(name))).Take(2).ToArray();return all.Length==1?all[0]:null;}
            foreach(var track in source.Where(t=>t.Track.Channel==TrackChannel.Morph)){
                var applicable=rules.Where(r=>r.from==track.Track.Name || MorphName(r.from)==MorphName(track.Track.Name)).ToArray();
                if(applicable.Length>0){
                    bool mapped=false;
                    foreach(var rule in applicable){var to=Find(rule.to);if(to==null){messages.Add("表情「"+rule.to+"」が適用先にないため省略しました。");continue;}Get(to).terms.Add((track,rule.factor));mapped=true;}
                    if(same){var original=Find(track.Track.Name);if(original!=null){if(mapped)Get(original);else Get(original).terms.Add((track,1));}}
                }else {var to=Find(track.Track.Name);if(to!=null)Get(to).terms.Add((track,1));}
            }
            return transfers.Values.ToArray();
        }
        static void SizeNativeCamera(Pose source,Pose target,KeyValue camera,Matrix4x4 sourceToScene,Matrix4x4 targetToScene)
        {
            int a=source.Rig.Find("head"),b=target.Rig.Find("head");
            if(a<0 || b<0){a=source.Rig.Find("hips");b=target.Rig.Find("hips");}if(a<0 || b<0)return;
            float Length(Rig rig,Matrix4x4 matrix){
                float length=0;int count=0;
                foreach(string side in new[]{"left","right"}){
                    int upper=rig.Find(side+"UpperLeg"),lower=rig.Find(side+"LowerLeg"),foot=rig.Find(side+"Foot");if(upper<0 || lower<0 || foot<0)continue;
                    length+=Vector3.Distance(Vector3.Transform(Point(rig.Joints[upper]),matrix),Vector3.Transform(Point(rig.Joints[lower]),matrix))+
                        Vector3.Distance(Vector3.Transform(Point(rig.Joints[lower]),matrix),Vector3.Transform(Point(rig.Joints[foot]),matrix));count++;
                }
                return count>0?length/count:Vector3.TransformNormal(Vector3.UnitY*rig.LegLength(),matrix).Length();
            }
            float fromLength=Length(source.Rig,sourceToScene),toLength=Length(target.Rig,targetToScene),ratio=fromLength>1e-7f?toLength/fromLength:1;
            // CameraPose uses SceneUnit (.1 m) and its initial center (0,10,0)
            // is the native origin; bone offsets do not use these camera units.
            const float cameraUnit=.1f;var origin=new Vector3(0,10,0);
            var aim=(camera.Position-origin)*cameraUnit;
            camera.Position=origin+(Vector3.Transform(target.World(b).p,targetToScene)+(aim-Vector3.Transform(source.World(a).p,sourceToScene))*ratio)/cameraUnit;
            camera.Distance*=ratio;
        }
    }
}
