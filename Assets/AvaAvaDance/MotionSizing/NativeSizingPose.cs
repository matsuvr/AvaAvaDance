using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ava.Extensions;

namespace Ava.MotionSizing
{
    // All bone coordinates are native local offsets and all rig measurements are metres.
    // This code deliberately does not use a file-format representation of a motion.
    public static partial class NativeMotionSizing
    {
        static Quaternion Unit(Quaternion q) => q.LengthSquared()>1e-12f?Quaternion.Normalize(q):Quaternion.Identity;
        static Vector3 Point(RigJoint j)=>new Vector3((float)j.x,(float)j.y,(float)j.z);
        static Quaternion Basis(RigJoint j)=>Unit(new Quaternion(j.qx,j.qy,j.qz,j.qw));
        static Vector3 Scale(RigJoint j)=>new Vector3(j.scaleX,j.scaleY,j.scaleZ);
        static string Role(string value)=>(value??"").Replace(" ","").ToLowerInvariant();
        static Vector3 Divide(Vector3 a,Vector3 b)=>new Vector3(Math.Abs(b.X)>1e-9f?a.X/b.X:0,Math.Abs(b.Y)>1e-9f?a.Y/b.Y:0,Math.Abs(b.Z)>1e-9f?a.Z/b.Z:0);
        static Quaternion Between(Vector3 from,Vector3 to)
        {
            if(from.LengthSquared()<1e-14f || to.LengthSquared()<1e-14f)return Quaternion.Identity;
            from=Vector3.Normalize(from);to=Vector3.Normalize(to);float dot=Vector3.Dot(from,to);
            if(dot<-.999999f){var axis=Vector3.Cross(from,Math.Abs(from.Y)<.9f?Vector3.UnitY:Vector3.UnitX);return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),(float)Math.PI);}
            return Unit(new Quaternion(Vector3.Cross(from,to),1+dot));
        }
        static KeyValue Copy(KeyValue k)=>new KeyValue{
            Channel=k.Channel,Position=k.Position,Euler=k.Euler,Direction=k.Direction,Rotation=k.Rotation,Color=k.Color,
            Weight=k.Weight,Distance=k.Distance,ViewAngle=k.ViewAngle,Scale=k.Scale,Opacity=k.Opacity,Acceleration=k.Acceleration,Noise=k.Noise,Range=k.Range,
            ShadowMode=k.ShadowMode,PhysicsOff=k.PhysicsOff,Perspective=k.Perspective,Disabled=k.Disabled,Hidden=k.Hidden,Noisy=k.Noisy,
            ParentBone=k.ParentBone,ParentActor=k.ParentActor,ParentBoneId=k.ParentBoneId,DisabledIK=k.DisabledIK?.ToArray(),
            Curves=k.Curves?.ToArray(),RawInterpolation=k.RawInterpolation?.ToArray(),SourceName=k.SourceName?.ToArray(),SourceIK=k.SourceIK?.ToArray(),ModifiedChannels=k.ModifiedChannels};
        static KeyValue Rest()=>new KeyValue{Channel=TrackChannel.Bone};
        static void Linear(KeyValue k){k.Curves=null;k.RawInterpolation=null;k.ModifiedChannels=0;}
        static float Cubic(float a,float b,float t){float s=1-t;return 3*s*s*t*a+3*s*t*t*b+t*t*t;}
        static float Parameter(BezierCurve c,float x)
        {
            float lo=0,hi=1;for(int i=0;i<26;i++){float m=(lo+hi)*.5f;if(Cubic(c.First.X,c.Second.X,m)<x)lo=m;else hi=m;}return (lo+hi)*.5f;
        }
        static float Ease(KeyValue key,int channel,float t)
        {
            if(key.Curves==null || channel>=key.Curves.Length || !key.Curves[channel].Custom)return t;
            var c=key.Curves[channel];return Cubic(c.First.Y,c.Second.Y,Parameter(c,t));
        }
        static bool Near(Vector3 a,Vector3 b)=>Vector3.DistanceSquared(a,b)<1e-12f;
        static bool SameRotation(Quaternion a,Quaternion b)=>1-Math.Abs(Quaternion.Dot(Unit(a),Unit(b)))<1e-7f;
        static bool EqualValue(KeyValue a,KeyValue b)
        {
            if(a.Channel!=b.Channel)return false;
            if(a.Channel==TrackChannel.Bone)return Near(a.Position,b.Position) && SameRotation(a.Rotation,b.Rotation) && a.PhysicsOff==b.PhysicsOff;
            if(a.Channel==TrackChannel.Morph)return Math.Abs(a.Weight-b.Weight)<1e-7f;
            if(a.Channel==TrackChannel.Camera)return Near(a.Position,b.Position) && Near(a.Euler,b.Euler) && Math.Abs(a.Distance-b.Distance)<1e-6f &&
                a.ViewAngle==b.ViewAngle && a.Perspective==b.Perspective && a.Disabled==b.Disabled;
            if(a.Channel==TrackChannel.ModelSwitch)return a.Hidden==b.Hidden && (a.DisabledIK??Array.Empty<string>()).SequenceEqual(b.DisabledIK??Array.Empty<string>());
            return false;
        }
        sealed class Timeline
        {
            internal readonly TrackSnapshot Track;
            internal readonly int[] Frames;
            readonly KeyValue[] values;
            readonly HashSet<int> inactive;
            internal Timeline(TrackSnapshot track)
            {
                Track=track;
                if(track.Frames==null || track.Values==null || track.Frames.Length!=track.Values.Length || track.Values.Any(v=>v==null))
                    throw new ArgumentException("モーションのキーとフレームが一致しません。");
                inactive=new HashSet<int>(Enumerable.Range(0,track.Frames.Length).Where(i=>track.Channel==TrackChannel.Camera && track.Values[i].Disabled).Select(i=>track.Frames[i]));
                var order=Enumerable.Range(0,track.Frames.Length).Where(i=>track.Channel!=TrackChannel.Camera || !track.Values[i].Disabled).OrderBy(i=>track.Frames[i]).ToArray();
                Frames=order.Select(i=>track.Frames[i]).ToArray();values=order.Select(i=>track.Values[i]).ToArray();
            }
            internal int End=>Frames.Length==0?0:Frames[Frames.Length-1];
            internal bool Has(int frame)=>Array.BinarySearch(Frames,frame)>=0;
            internal bool Inactive(int frame)=>inactive.Contains(frame);
            internal KeyValue Sample(float frame)
            {
                if(Frames.Length==0)return Copy(Track.Channel==TrackChannel.Camera?new KeyValue{Channel=TrackChannel.Camera,Disabled=true}:Track.Current??new KeyValue{Channel=Track.Channel});
                int low=0,high=Frames.Length;
                while(low<high){int mid=(low+high)/2;if(Frames[mid]<frame)low=mid+1;else high=mid;}
                if(low==0)return Copy(values[0]);
                if(low==Frames.Length)return Copy(values[values.Length-1]);
                if(Frames[low]==frame)return Copy(values[low]);
                var a=values[low-1];var b=values[low];float t=(frame-Frames[low-1])/(Frames[low]-Frames[low-1]);
                var k=Copy(a);
                if(k.Channel==TrackChannel.Bone || k.Channel==TrackChannel.Camera){
                    var d=b.Position-a.Position;k.Position=a.Position+d*new Vector3(Ease(b,0,t),Ease(b,1,t),Ease(b,2,t));
                    if(k.Channel==TrackChannel.Bone)k.Rotation=Unit(Quaternion.Slerp(Unit(a.Rotation),Unit(b.Rotation),Ease(b,3,t)));
                    else {k.Euler=Vector3.Lerp(a.Euler,b.Euler,Ease(b,3,t));k.Distance=a.Distance+(b.Distance-a.Distance)*Ease(b,4,t);k.ViewAngle=a.ViewAngle+(b.ViewAngle-a.ViewAngle)*Ease(b,5,t);}
                }else if(k.Channel==TrackChannel.Morph)k.Weight=a.Weight+(b.Weight-a.Weight)*Ease(b,0,t);
                // Curves describe the interval ending at a key, not the sampled pose.
                Linear(k);return k;
            }
            internal void SplitAt(int frame,out KeyValue at,out int following,out KeyValue after)
            {
                at=Sample(frame);following=-1;after=null;
                int index=Array.BinarySearch(Frames,frame);if(index>=0)return;
                index=~index;if(index==0 || index==Frames.Length)return;
                following=Frames[index];after=Copy(values[index]);
                var curves=values[index].Curves;if(curves==null)return;
                float x=(frame-Frames[index-1])/(float)(Frames[index]-Frames[index-1]);
                at.Curves=new BezierCurve[curves.Length];after.Curves=new BezierCurve[curves.Length];
                for(int i=0;i<curves.Length;i++){
                    if(!curves[i].Custom)continue;var c=curves[i];float u=Parameter(c,x);
                    var a=Vector2.Lerp(Vector2.Zero,c.First,u);var b=Vector2.Lerp(c.First,c.Second,u);var d=Vector2.Lerp(c.Second,Vector2.One,u);
                    var ab=Vector2.Lerp(a,b,u);var bd=Vector2.Lerp(b,d,u);var p=Vector2.Lerp(ab,bd,u);
                    Vector2 Normalize(Vector2 v,Vector2 origin,Vector2 span)=>new Vector2(span.X>1e-9f?(v.X-origin.X)/span.X:0,Math.Abs(span.Y)>1e-9f?(v.Y-origin.Y)/span.Y:0);
                    if(Math.Abs(p.Y)>1e-9f)at.Curves[i]=new BezierCurve{Custom=true,First=Normalize(a,Vector2.Zero,p),Second=Normalize(ab,Vector2.Zero,p)};
                    var rest=Vector2.One-p;
                    if(Math.Abs(rest.Y)>1e-9f)after.Curves[i]=new BezierCurve{Custom=true,First=Normalize(bd,p,rest),Second=Normalize(d,p,rest)};
                }
                at.RawInterpolation=null;after.RawInterpolation=null;at.ModifiedChannels=after.ModifiedChannels=0;
            }
        }
        sealed class Rig
        {
            internal readonly RigProfile Profile;
            internal readonly RigJoint[] Joints;
            internal readonly Timeline[] Tracks;
            internal readonly Dictionary<string,Timeline> Controls=new Dictionary<string,Timeline>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string,int> roles=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            internal Rig(RigProfile profile,IEnumerable<Timeline> tracks)
            {
                Profile=profile??throw new ArgumentNullException(nameof(profile));Joints=profile.editRest??profile.joints;
                if(Joints==null || Joints.Length==0)throw new ArgumentException("モデルの編集用リグがありません。");
                Tracks=new Timeline[Joints.Length];
                for(int i=0;i<Joints.Length;i++){
                    if(Joints[i]==null || Joints[i].parent>=Joints.Length || Joints[i].parent< -1)throw new ArgumentException("リグの親子関係が不正です。");
                    string role=Role(Joints[i].role??profile.joints?[i]?.role);if(role.Length>0 && !roles.ContainsKey(role))roles.Add(role,i);
                }
                foreach(var t in tracks.Where(t=>t.Track.Channel==TrackChannel.Bone)){
                    int i=t.Track.RigJointIndex;
                    if(i<0 || i>=Joints.Length){
                        string role=Role(t.Track.Role);
                        if(role.Length>0 && roles.TryGetValue(role,out int found))i=found;
                        else {var matches=Enumerable.Range(0,Joints.Length).Where(n=>Joints[n].name==t.Track.Name).Take(2).ToArray();i=matches.Length==1?matches[0]:-1;}
                    }
                    if(i>=0 && Tracks[i]==null)Tracks[i]=t;
                    else if(i<0 && !string.IsNullOrEmpty(t.Track.Role) && !Controls.ContainsKey(Role(t.Track.Role)))Controls.Add(Role(t.Track.Role),t);
                }
            }
            internal int Find(string role)
            {
                string key=Role(role);if(key.Length==0)return -1;if(roles.TryGetValue(key,out int known))return known;
                var aliases=new List<string>{role,RigProfile.MotionAlias(role)};
                if(key.EndsWith("armtwist",StringComparison.Ordinal) || key.EndsWith("wristtwist",StringComparison.Ordinal)){
                    bool left=key.StartsWith("left",StringComparison.Ordinal),arm=key.EndsWith("armtwist",StringComparison.Ordinal);string jp=left?"左":"右",en=left?"Left":"Right",shortSide=left?"L":"R";
                    aliases.Add(jp+(arm?"腕":"手")+"捩り");aliases.Add(en+(arm?"UpperArm":"Forearm")+"Twist");
                    aliases.Add((arm?"UpperArm":"Forearm")+"Twist_"+shortSide);aliases.Add((arm?"UpperArm":"Forearm")+"Twist."+shortSide);
                }
                var names=aliases.Where(s=>!string.IsNullOrEmpty(s)).Select(s=>s.Normalize(System.Text.NormalizationForm.FormKC)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var matches=Enumerable.Range(0,Joints.Length).Where(i=>names.Contains((Joints[i].name??"").Normalize(System.Text.NormalizationForm.FormKC))).Take(2).ToArray();
                int result=matches.Length==1?matches[0]:-1;roles[key]=result;return result;
            }
            internal int Match(Rig from,int joint)
            {
                string role=from.Joints[joint].role??from.Profile.joints?[joint]?.role;int found=Find(role);
                if(found>=0)return found;
                string name=from.Joints[joint].name;if(string.IsNullOrEmpty(name))return -1;
                var choices=Enumerable.Range(0,Joints.Length).Where(i=>Joints[i].name==name).Take(2).ToArray();return choices.Length==1?choices[0]:-1;
            }
            // Imported keys are relative to editing rest, but their anatomical rotation
            // reference is the authored bind pose (FBX editing arms can be relaxed).
            internal Quaternion Calibration(int joint)=>Basis(Profile.joints!=null && joint<Profile.joints.Length?Profile.joints[joint]:Joints[joint]);
            internal float LegLength()
            {
                float total=0;int count=0;
                foreach(string side in new[]{"left","right"}){
                    int a=Find(side+"UpperLeg"),b=Find(side+"LowerLeg"),c=Find(side+"Foot");
                    if(a>=0 && b>=0 && c>=0){total+=Vector3.Distance(Point(Joints[a]),Point(Joints[b]))+Vector3.Distance(Point(Joints[b]),Point(Joints[c]));count++;}
                }
                if(count>0 && total>1e-7f)return total/count;
                int hips=Find("hips");return hips>=0?Math.Max(.000001f,Math.Abs(Point(Joints[hips]).Y)):1;
            }
            internal Quaternion Facing()
            {
                foreach(string part in new[]{"UpperLeg","Shoulder","UpperArm","Foot"}){
                    int left=Find("left"+part),right=Find("right"+part);if(left<0 || right<0)continue;
                    var lateral=Point(Joints[left])-Point(Joints[right]);lateral.Y=0;if(lateral.LengthSquared()<1e-10f)continue;
                    return Quaternion.CreateFromAxisAngle(Vector3.UnitY,(float)Math.Atan2(-lateral.Z,lateral.X));
                }
                return Quaternion.Identity;
            }
            internal bool Descendant(int child,int ancestor){for(int n=0;child>=0 && n<Joints.Length;n++,child=Joints[child].parent)if(child==ancestor)return true;return false;}
            internal bool Chain(int a,int b,int c)=>a>=0 && b>=0 && c>=0 && Tracks[a]!=null && Tracks[b]!=null && Tracks[c]!=null && Descendant(b,a) && Descendant(c,b);
        }
        sealed class Pose
        {
            internal readonly Rig Rig;
            internal readonly KeyValue[] Values;
            internal readonly Dictionary<string,KeyValue> Controls=new Dictionary<string,KeyValue>(StringComparer.OrdinalIgnoreCase);
            readonly (Vector3 p,Quaternion q)[] world;
            readonly bool[] ready,visiting;
            readonly int rootIndex;
            internal Pose(Rig rig,int frame)
            {
                Rig=rig;Values=rig.Tracks.Select(t=>t?.Sample(frame)??Rest()).ToArray();
                rootIndex=Array.FindIndex(rig.Joints,j=>j.parent<0);
                foreach(var c in rig.Controls)Controls.Add(c.Key,c.Value.Sample(frame));
                world=new (Vector3,Quaternion)[Values.Length];ready=new bool[Values.Length];visiting=new bool[Values.Length];
            }
            internal void Invalidate()=>Array.Clear(ready,0,ready.Length);
            Quaternion RootBasis(){var q=Basis(Rig.Joints[rootIndex]);return Controls.TryGetValue("root",out var root)?Unit(q*root.Rotation):q;}
            internal (Vector3 p,Quaternion q) World(int i)
            {
                if(i<0)return (Vector3.Zero,Quaternion.Identity);if(ready[i])return world[i];
                if(visiting[i])throw new ArgumentException("リグの親子関係が循環しています。");visiting[i]=true;
                var j=Rig.Joints[i];var k=Values[i];Vector3 p;Quaternion q;
                if(j.parent<0){p=Point(j)+k.Position;q=Unit(Basis(j)*k.Rotation);
                    if(Controls.TryGetValue("root",out var root)){
                        var turn=Unit(Basis(j)*root.Rotation*Quaternion.Inverse(Basis(j)));
                        p=Point(j)+Vector3.Transform(root.Position*Scale(j),Basis(j))+Vector3.Transform(p-Point(j),turn);q=Unit(turn*q);}}
                else {var parent=Rig.Joints[j.parent];var w=World(j.parent);var inv=Quaternion.Inverse(Basis(parent));
                    p=w.p+Vector3.Transform(Vector3.Transform(Point(j)-Point(parent),inv)+k.Position*Scale(parent),w.q);
                    q=Unit(w.q*inv*Basis(j)*k.Rotation);}
                if(Role(j.role)=="hips"){
                    Vector3 move=Vector3.Zero;Quaternion turn=Quaternion.Identity;
                    foreach(string control in new[]{"center","groove"})if(Controls.TryGetValue(control,out var value)){move+=Vector3.Transform(value.Position,turn);turn=Unit(turn*value.Rotation);}
                    var basis=RootBasis();p+=Vector3.Transform(move*Scale(Rig.Joints[rootIndex]),basis);q=Unit(basis*turn*Quaternion.Inverse(basis)*q);
                }
                visiting[i]=false;ready[i]=true;return world[i]=(p,q);
            }
            internal void SetWorldRotation(int i,Quaternion q)
            {
                var j=Rig.Joints[i];
                if(Role(j.role)=="hips"){
                    Quaternion turn=Quaternion.Identity;foreach(string c in new[]{"center","groove"})if(Controls.TryGetValue(c,out var value))turn=Unit(turn*value.Rotation);
                    var basis=RootBasis();q=Quaternion.Inverse(Unit(basis*turn*Quaternion.Inverse(basis)))*q;
                }
                if(j.parent<0){if(Controls.TryGetValue("root",out var root))q=Quaternion.Inverse(Unit(Basis(j)*root.Rotation*Quaternion.Inverse(Basis(j))))*q;Values[i].Rotation=Unit(Quaternion.Inverse(Basis(j))*q);}
                else Values[i].Rotation=Unit(Quaternion.Inverse(Basis(j))*Basis(Rig.Joints[j.parent])*Quaternion.Inverse(World(j.parent).q)*q);
                Invalidate();
            }
            internal bool Solve(int upper,int lower,int tip,Vector3 goal)
            {
                if(!Rig.Chain(upper,lower,tip))return false;
                var a=World(upper);var b=World(lower);var c=World(tip);var originalOrientation=c.q;
                float first=Vector3.Distance(a.p,b.p),second=Vector3.Distance(b.p,c.p);var ray=goal-a.p;float distance=ray.Length();
                if(first<1e-7f || second<1e-7f || distance<1e-7f)return false;
                var forward=ray/distance;float reach=Math.Clamp(distance,Math.Abs(first-second)+1e-6f,Math.Max(Math.Abs(first-second)+1e-6f,first+second-1e-6f));
                var bend=b.p-a.p-forward*Vector3.Dot(b.p-a.p,forward);
                if(bend.LengthSquared()<1e-10f){bend=Vector3.UnitZ-forward*Vector3.Dot(Vector3.UnitZ,forward);if(bend.LengthSquared()<1e-10f)bend=Vector3.UnitX-forward*Vector3.Dot(Vector3.UnitX,forward);}
                bend=Vector3.Normalize(bend);float along=(first*first-second*second+reach*reach)/(2*reach);
                var knee=a.p+forward*along+bend*(float)Math.Sqrt(Math.Max(0,first*first-along*along));
                SetWorldRotation(upper,Between(b.p-a.p,knee-a.p)*a.q);
                b=World(lower);c=World(tip);var destination=a.p+forward*reach;
                SetWorldRotation(lower,Between(c.p-b.p,destination-b.p)*b.q);
                SetWorldRotation(tip,originalOrientation);
                return Vector3.Distance(World(tip).p,goal)<.002f;
            }
        }
    }
}
