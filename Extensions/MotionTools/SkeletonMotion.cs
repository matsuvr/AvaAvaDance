using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ava.Extensions;

namespace Ava.MotionTools
{
    // Samples the authored hierarchy in motion units. Preview framing never enters this transform.
    internal sealed class SkeletonMotion
    {
        readonly RigProfile rig;
        readonly VmdDocument motion;
        readonly Dictionary<RigJoint,SortedList<uint,byte[]>> keys=new Dictionary<RigJoint,SortedList<uint,byte[]>>();
        readonly Dictionary<RigJoint,(Vector3 position,Quaternion rotation)> cache=new Dictionary<RigJoint,(Vector3,Quaternion)>();
        readonly Dictionary<string,SortedList<uint,bool>> switches=new Dictionary<string,SortedList<uint,bool>>(StringComparer.Ordinal);
        uint cachedFrame=uint.MaxValue;
        internal static string Alias(string role)=>RigProfile.MotionAlias(role);
        internal SkeletonMotion(RigProfile rig,VmdDocument motion)
        {
            this.rig=rig;this.motion=motion;
            foreach(var row in motion.Records(MotionChannel.Visibility)){
                uint t=VmdDocument.Frame(MotionChannel.Visibility,row);
                for(int at=9;at+21<=row.Length;at+=21){
                    var text=new byte[20];Buffer.BlockCopy(row,at,text,0,20);
                    string key=MmdText.Decode(text).Normalize(System.Text.NormalizationForm.FormKC);
                    if(!switches.TryGetValue(key,out var values))switches.Add(key,values=new SortedList<uint,bool>());
                    values[t]=row[at+20]!=0;
                }
            }
            var names=new Dictionary<string,RigJoint>(StringComparer.OrdinalIgnoreCase);
            foreach(var j in rig.joints)foreach(string name in new[]{j.name,j.role,Alias(j.role)})
                if(!string.IsNullOrEmpty(name) && !names.ContainsKey(name))names.Add(name,j);
            foreach(var row in motion.Records(MotionChannel.Bone))
            {
                if(!names.TryGetValue(VmdDocument.Name(row),out var joint))continue;
                if(!keys.TryGetValue(joint,out var list))keys.Add(joint,list=new SortedList<uint,byte[]>());
                list[VmdDocument.Frame(MotionChannel.Bone,row)]=row;
            }
        }
        static Quaternion Normalize(Quaternion q)=>q.LengthSquared()>1e-9f ? Quaternion.Normalize(q):Quaternion.Identity;
        static Vector3 Translation(byte[] r)=>new Vector3(BitConverter.ToSingle(r,19),BitConverter.ToSingle(r,23),BitConverter.ToSingle(r,27));
        static Quaternion Rotation(byte[] r)=>Normalize(new Quaternion(BitConverter.ToSingle(r,31),BitConverter.ToSingle(r,35),BitConverter.ToSingle(r,39),BitConverter.ToSingle(r,43)));
        internal static Vector3 Rest(RigJoint j,RigProfile rig)=>new Vector3((float)(j.x/rig.metersPerMotionUnit),(float)(j.y/rig.metersPerMotionUnit),(float)(j.z/rig.metersPerMotionUnit));
        internal static Quaternion RestRotation(RigJoint j)=>Normalize(new Quaternion(j.qx,j.qy,j.qz,j.qw));

        static float Ease(byte[] row,int channel,float t)
        {
            return SplitCurves.Ease(row,channel,t);
        }

        (Vector3,Quaternion) Sample(RigJoint joint,uint frame)
        {
            if(!keys.TryGetValue(joint,out var list))return (Vector3.Zero,Quaternion.Identity);
            int lo=0,hi=list.Count;
            while(lo<hi){int mid=(lo+hi)/2;if(list.Keys[mid]<frame)lo=mid+1;else hi=mid;}
            if(lo==0 || lo==list.Count || list.Keys[lo]==frame)
            {var row=list.Values[Math.Min(lo,list.Count-1)];return (Translation(row),Rotation(row));}
            var a=list.Values[lo-1];var b=list.Values[lo];float t=(frame-list.Keys[lo-1])/(float)(list.Keys[lo]-list.Keys[lo-1]);
            var start=Translation(a);var delta=Translation(b)-start;
            return (start+delta*new Vector3(Ease(b,0,t),Ease(b,1,t),Ease(b,2,t)),Quaternion.Slerp(Rotation(a),Rotation(b),Ease(b,3,t)));
        }

        internal (Vector3 position,Quaternion rotation) World(RigJoint j,uint frame)
        {
            if(cachedFrame!=frame){cache.Clear();cachedFrame=frame;}
            if(cache.TryGetValue(j,out var known))return known;
            var chain=new Stack<RigJoint>();
            for(var n=j;n!=null && !cache.ContainsKey(n);n=n.parent<0?null:rig.joints[n.parent])
            {
                if(chain.Count>=rig.joints.Length)throw new InvalidOperationException("リグの親子関係が循環しています。");
                chain.Push(n);
            }
            while(chain.Count>0)
            {
                var n=chain.Pop();var (delta,q)=Sample(n,frame);
                var rest=Rest(n,rig);var rot=RestRotation(n);
                if(n.parent<0)cache[n]=(rest+delta,Normalize(rot*q));
                else
                {
                    var p=rig.joints[n.parent];var parent=cache[p];var inv=Quaternion.Inverse(RestRotation(p));
                    var local=Vector3.Transform(rest-Rest(p,rig),inv);
                    var scale=new Vector3(p.scaleX,p.scaleY,p.scaleZ);
                    cache[n]=(parent.position+Vector3.Transform(local+delta*scale,parent.rotation),Normalize(parent.rotation*inv*rot*q));
                }
            }
            return cache[j];
        }

        internal Quaternion TrunkBasis(uint time)
        {
            var trunk=rig.Find("neckRoot","首根元");
            var chest=rig.Find("chest","上半身2")??rig.Find("spine","上半身");
            var neck=trunk??rig.Find("neck","首");
            var parent=trunk!=null && trunk.parent>=0?rig.joints[trunk.parent]:chest;
            var forward=neck!=null && parent!=null?Rest(neck,rig)-Rest(parent,rig):Vector3.UnitY;
            forward=forward.LengthSquared()>1e-9f?Vector3.Normalize(forward):Vector3.UnitY;
            var up=Math.Abs(Vector3.Dot(forward,Vector3.UnitZ))>.999f?Vector3.UnitY:Vector3.UnitZ;
            var right=Vector3.Normalize(Vector3.Cross(up,forward));up=Vector3.Cross(forward,right);
            var basis=Quaternion.CreateFromRotationMatrix(new Matrix4x4(right.X,right.Y,right.Z,0,up.X,up.Y,up.Z,0,forward.X,forward.Y,forward.Z,0,0,0,0,1));
            trunk??=chest??neck;
            return trunk==null?basis:Normalize(World(trunk,time).rotation*Quaternion.Inverse(RestRotation(trunk))*basis);
        }
        internal void InvalidatePose()=>cache.Clear();

        internal static Quaternion Between(Vector3 from,Vector3 to)
        {
            if(from.LengthSquared()<1e-10f || to.LengthSquared()<1e-10f)return Quaternion.Identity;
            from=Vector3.Normalize(from);to=Vector3.Normalize(to);float dot=Vector3.Dot(from,to);
            if(dot<-.99999f){var axis=Vector3.Cross(from,Math.Abs(from.Y)<.9?Vector3.UnitY:Vector3.UnitX);return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),(float)Math.PI);}
            return Normalize(new Quaternion(Vector3.Cross(from,to),1+dot));
        }

        internal IEnumerable<uint> Times(RigJoint j)=>j!=null && keys.TryGetValue(j,out var list)?list.Keys:Array.Empty<uint>();
        byte[] EditableKey(RigJoint joint,uint time)
        {
            if(!keys.TryGetValue(joint,out var list))keys[joint]=list=new SortedList<uint,byte[]>();
            if(!list.TryGetValue(time,out var row))
            {
                var sampled=list.Count>0?Sample(joint,time):(Vector3.Zero,Quaternion.Identity);

                row=new byte[111];
                if(list.Count>0)Array.Copy(list.Values[0],row,15);else Array.Copy(MmdText.Fixed(joint.name,15),row,15);
                Array.Copy(BitConverter.GetBytes(time),0,row,15,4);
                VmdDocument.Number(row,19,sampled.Item1.X);VmdDocument.Number(row,23,sampled.Item1.Y);VmdDocument.Number(row,27,sampled.Item1.Z);
                VmdDocument.Number(row,31,sampled.Item2.X);VmdDocument.Number(row,35,sampled.Item2.Y);
                VmdDocument.Number(row,39,sampled.Item2.Z);VmdDocument.Number(row,43,sampled.Item2.W);
                for(int i=0;i<16;i++)row[47+i]=(byte)(i<8?20:107);
                for(int n=1;n<4;n++)Array.Copy(row,47+n,row,47+16*n,16-n);
                row[49]=row[50]=0;
                if(list.Count>1 && list.Keys[0]<time && time<list.Keys[list.Count-1]){
                    int lo=0,hi=list.Count-1;while(lo+1<hi){int mid=(lo+hi)/2;if(list.Keys[mid]<time)lo=mid;else hi=mid;}
                    var prior=list.Values[lo];SplitCurves.Divide(row,list.Values[hi],(time-list.Keys[lo])/(float)(list.Keys[hi]-list.Keys[lo]),prior[49]==99 && prior[50]==15);
                }
                list.Add(time,row);motion.Records(MotionChannel.Bone).Add(row);
            }
            return row;
        }
        internal void TranslateWorld(RigJoint joint,uint time,Vector3 offset)
        {
            if(joint==null || offset.LengthSquared()<1e-10f)return;
            if(joint.parent>=0){
                var parent=rig.joints[joint.parent];offset=Vector3.Transform(offset,Quaternion.Inverse(World(parent,time).rotation));
                float Divide(float v,float scale)=>Math.Abs(scale)>1e-8f?v/scale:0;
                offset=new Vector3(Divide(offset.X,parent.scaleX),Divide(offset.Y,parent.scaleY),Divide(offset.Z,parent.scaleZ));
            }
            var row=EditableKey(joint,time);var point=Translation(row)+offset;
            VmdDocument.Number(row,19,point.X);VmdDocument.Number(row,23,point.Y);VmdDocument.Number(row,27,point.Z);cache.Clear();
        }
        void WriteRotation(RigJoint joint,uint time,Quaternion q)
        {
            q=Normalize(q);var prior=Sample(joint,time).Item2;
            if(Math.Min((q-prior).LengthSquared(),(q+prior).LengthSquared())<1e-14f)return;
            var row=EditableKey(joint,time);VmdDocument.Number(row,31,q.X);VmdDocument.Number(row,35,q.Y);VmdDocument.Number(row,39,q.Z);VmdDocument.Number(row,43,q.W);cache.Clear();
        }
        internal void Aim(RigJoint pivot,RigJoint end,uint frame,Vector3 goal)
        {
            var p=World(pivot,frame);Vector3 ray=World(end,frame).position-p.position;
            var turn=Between(ray,goal-p.position);
            if(turn.X*turn.X+turn.Y*turn.Y+turn.Z*turn.Z<2.5e-9f)return;
            var (_,local)=Sample(pivot,frame);
            WriteRotation(pivot,frame,local*Quaternion.Inverse(p.rotation)*turn*p.rotation);
        }
        internal bool IkActive(string name,uint frame)
        {
            if(!switches.TryGetValue(name.Normalize(System.Text.NormalizationForm.FormKC),out var values))return true;
            int lo=0,hi=values.Count;
            while(lo<hi){int mid=lo+(hi-lo)/2;if(values.Keys[mid]<=frame)lo=mid+1;else hi=mid;}
            return lo==0 || values.Values[lo-1];
        }

        internal Vector3 PositionKey(RigJoint joint,uint frame)=>Sample(joint,frame).Item1;
        internal void SetPositionKey(RigJoint joint,uint frame,Vector3 position)
        {
            var row=EditableKey(joint,frame);
            VmdDocument.Number(row,19,position.X);VmdDocument.Number(row,23,position.Y);VmdDocument.Number(row,27,position.Z);
            InvalidatePose();
        }

        internal Quaternion LocalRotation(RigJoint joint,uint frame)
        {
            var basis=RestRotation(joint);
            return Normalize(basis*Sample(joint,frame).Item2*Quaternion.Inverse(basis));
        }

        internal Quaternion WorldRotation(RigJoint joint,uint frame)=>joint==null?Quaternion.Identity:
            Normalize(World(joint,frame).rotation*Quaternion.Inverse(RestRotation(joint)));

        internal void SetLocalRotation(RigJoint joint,uint frame,Quaternion value)
        {
            var basis=RestRotation(joint);
            WriteRotation(joint,frame,Quaternion.Inverse(basis)*value*basis);
        }

        internal void CorrectRestAxes(RigJoint joint,Quaternion previous,Quaternion next)
        {
            if(Math.Abs(previous.W)>1-1e-7f && Math.Abs(next.W)>1-1e-7f)return;
            var basis=RestRotation(joint);var inv=Quaternion.Inverse(basis);
            var before=Quaternion.Inverse(inv*previous*basis);var after=inv*next*basis;
            foreach(uint frame in Times(joint).ToArray())WriteRotation(joint,frame,before*Sample(joint,frame).Item2*after);
        }
        internal bool Bent(RigJoint joint,uint time)=>joint!=null && Math.Abs(Sample(joint,time).Item2.W)<.9659258f;

        internal bool Reach(RigJoint shoulder,RigJoint elbow,RigJoint hand,uint time,Vector3 goal,bool useElbow,float tolerance,bool useShoulder=true)
        {
            if(shoulder==null || elbow==null || hand==null)return false;
            return ReachPoint(shoulder,elbow,World(hand,time).position,time,goal,useElbow,tolerance,useShoulder);
        }
        internal bool ReachPoint(RigJoint shoulder,RigJoint elbow,Vector3 tip,uint time,Vector3 goal,bool useElbow,float tolerance,bool useShoulder=true)
        {
            if(!useElbow && !useShoulder)return false;
            var root=World(shoulder,time);var forearm=World(elbow,time);
            var armLocal=Sample(shoulder,time).Item2;var elbowLocal=Sample(elbow,time).Item2;
            Quaternion Step(Vector3 ray,Vector3 toward){
                var q=Between(ray,toward);float sine=(float)Math.Sqrt(q.X*q.X+q.Y*q.Y+q.Z*q.Z);
                float angle=2*(float)Math.Atan2(sine,Math.Abs(q.W));if(angle<=.0001f)return Quaternion.Identity;
                return angle>1?Quaternion.Slerp(Quaternion.Identity,q,1/angle):q;
            }
            for(int i=0;i<50;i++){
                if(Vector3.DistanceSquared(tip,goal)<1e-6f)break;
                var prior=tip;
                if(useElbow){
                    var turn=Step(tip-forearm.position,goal-forearm.position);
                    elbowLocal=Normalize(elbowLocal*Quaternion.Inverse(forearm.rotation)*turn*forearm.rotation);
                    forearm.rotation=Normalize(turn*forearm.rotation);
                    tip=forearm.position+Vector3.Transform(tip-forearm.position,turn);
                }
                if(useShoulder){
                    var swing=Step(tip-root.position,goal-root.position);
                    armLocal=Normalize(armLocal*Quaternion.Inverse(root.rotation)*swing*root.rotation);
                    root.rotation=Normalize(swing*root.rotation);
                    forearm.position=root.position+Vector3.Transform(forearm.position-root.position,swing);
                    forearm.rotation=Normalize(swing*forearm.rotation);
                    tip=root.position+Vector3.Transform(tip-root.position,swing);
                }
                if(Vector3.DistanceSquared(tip,prior)<1e-12f)break;
            }
            var residual=Vector3.Abs(tip-goal);
            if(Math.Max(residual.X,Math.Max(residual.Y,residual.Z))>tolerance)return false;
            if(useShoulder)WriteRotation(shoulder,time,armLocal);
            if(useElbow)WriteRotation(elbow,time,elbowLocal);
            return true;
        }

        internal void DistributeTwist(RigJoint arm,RigJoint armTwist,RigJoint elbow,RigJoint wristTwist,RigJoint hand,
            System.Threading.CancellationToken stop)
        {
            var joints=new[]{arm,armTwist,elbow,wristTwist,hand};
            if(joints.Any(j=>j==null))return;
            var upper=Rest(elbow,rig)-Rest(arm,rig);
            var lower=Rest(hand,rig)-Rest(elbow,rig);
            if(upper.LengthSquared()<1e-8f || lower.LengthSquared()<1e-8f)return;
            upper=Vector3.Normalize(upper);lower=Vector3.Normalize(lower);
            var palm=lower;
            bool right=arm.role?.StartsWith("right",StringComparison.OrdinalIgnoreCase)==true ||
                arm.name?.StartsWith("右",StringComparison.Ordinal)==true;
            var middle=rig.Find(right?"rightMiddleProximal":"leftMiddleProximal",right?"右中指１":"左中指１");
            if(middle!=null){
                var direction=Rest(middle,rig)-Rest(hand,rig);
                if(direction.LengthSquared()>1e-8f)palm=Vector3.Normalize(direction);
            }
            var authored=joints.SelectMany(Times).Distinct().OrderBy(frame=>frame).ToArray();
            var frames=new SortedSet<uint>(authored);
            void Divide(uint first,uint last)
            {
                if(last-first<=1)return;
                // Split large rotations before factoring them into independently interpolated channels.
                const float cosHalf70=.819152f;
                if(!joints.Any(j=>Math.Abs(Quaternion.Dot(Sample(j,first).Item2,Sample(j,last).Item2))<cosHalf70))return;
                uint middle=first+(last-first)/2;frames.Add(middle);
                Divide(first,middle);Divide(middle,last);
            }
            for(int i=1;i<authored.Length;i++)Divide(authored[i-1],authored[i]);

            void AddTwistKey(RigJoint twist,byte[] reference,uint time)
            {
                if(keys.TryGetValue(twist,out var list) && list.ContainsKey(time))return;
                var row=EditableKey(twist,time);
                for(int component=0;component<4;component++){
                    int slot=47+3+component*4;
                    byte value=reference[slot+45];
                    for(int copy=0;copy<4;copy++)row[slot+15*copy]=value;
                }
            }
            Quaternion Read(RigJoint j,uint time){
                var basis=RestRotation(j);return Normalize(basis*Sample(j,time).Item2*Quaternion.Inverse(basis));
            }
            void Write(RigJoint j,uint time,Quaternion rotation){
                var basis=RestRotation(j);WriteRotation(j,time,Quaternion.Inverse(basis)*rotation*basis);
            }
            foreach(uint time in frames){
                stop.ThrowIfCancellationRequested();
                var armKey=EditableKey(arm,time);EditableKey(elbow,time);var handKey=EditableKey(hand,time);
                AddTwistKey(armTwist,armKey,time);AddTwistKey(wristTwist,handKey,time);
            }
            foreach(uint time in frames){
                stop.ThrowIfCancellationRequested();
                var pose=TwistPose.Solve(upper,lower,palm,Read(arm,time),Read(armTwist,time),
                    Read(elbow,time),Read(wristTwist,time),Read(hand,time));
                Write(arm,time,pose.arm);Write(armTwist,time,pose.armRoll);Write(elbow,time,pose.elbow);
                Write(wristTwist,time,pose.wristRoll);Write(hand,time,pose.wrist);
            }
        }
    }
}
