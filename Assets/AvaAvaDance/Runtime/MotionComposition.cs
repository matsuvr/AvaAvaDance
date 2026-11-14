using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    [Serializable] internal struct LayerKey
    {
        public int frame;
        public Vector3 position;
        public Quaternion rotation;
        public CurveSet curves;
        public static LayerKey Identity => new LayerKey{rotation=Quaternion.identity};
        public static LayerKey From(object value)
        {
            if(value is BonePose b) return new LayerKey{position=b.Position, rotation=b.Rotation, curves=b.Curves};
            if(value is CameraPose c) return new LayerKey{position=c.Center,rotation=Quaternion.Euler(c.Angles),curves=c.Curves};
            var a=(AccessoryPose)value; return new LayerKey{position=a.Position,rotation=Quaternion.Euler(a.Rotation)};
        }
        public object Into(object value,bool writeCurve=false)
        {
            if(value is BonePose b){b.Position=position;b.Rotation=rotation;if(writeCurve)b.Curves=curves;return b;}
            Vector3 angles=rotation.eulerAngles;
            for(int i=0;i<3;i++) angles[i]=Mathf.DeltaAngle(0,angles[i]);
            if(value is CameraPose c){c.Center=position;c.Angles=angles;if(writeCurve)c.Curves=curves;return c;}
            var a=(AccessoryPose)value;a.Position=position;a.Rotation=angles;return a;
        }
        public void Append(LayerKey other,float weight)
        {
            position+=other.position*weight;rotation*=Quaternion.SlerpUnclamped(Quaternion.identity,other.rotation,weight);
        }
    }

    [Serializable] internal sealed class ClipPlacement
    {
        public string id=Guid.NewGuid().ToString("N"), parent, source;
        public int start,length=30,sourceStart,sourceEnd=30,cycles;
        public float rate=1,weight=1,fadeIn,fadeOut;
        public bool repeat,muted,infinite;
        public EaseCurve warp;

        float CycleLength=>(sourceEnd-sourceStart)/rate;
        float Duration=>cycles>0?CycleLength*cycles:length;
        internal int EndFrame=>Mathf.Min(600000,start+Mathf.CeilToInt(infinite?CycleLength:Duration));
        internal string RangeLabel=>start+"～"+(infinite?"∞":EndFrame.ToString());

        public bool Map(float parentTime,out float local,out float influence)
        {
            float offset=parentTime-start; local=sourceStart;influence=0;
            float duration=Duration;
            if(muted || offset<0 || (!infinite && offset>duration)) return false;
            float span=sourceEnd-sourceStart;
            float progress=offset*rate/span;
            float cycle=repeat && (infinite || progress>1) ? progress-Mathf.Floor(progress) : Mathf.Clamp01(progress);
            if(repeat && !infinite && offset==duration && progress>0 && (cycles>0 || cycle==0)) cycle=1;
            local=sourceStart+span*warp.Evaluate(cycle);
            influence=weight*Mathf.Min(fadeIn>0?Mathf.Clamp01(offset/fadeIn):1,!infinite && fadeOut>0?Mathf.Clamp01((duration-offset)/fadeOut):1);
            return true;
        }
        internal bool Valid => !string.IsNullOrEmpty(id) && start>=0 && start<=600000 && (cycles>0 || infinite || length>0 && (long)start+length<=600000) &&
            sourceStart>=0 && sourceEnd>sourceStart && sourceEnd<=600000 && rate>0 && rate<=1000 &&
            cycles>=0 && cycles<=600000 && (!infinite || repeat) && (cycles<=1 || repeat) && (cycles==0 && !infinite || CycleLength<=600000) &&
            (infinite || start+Duration<=600000) && weight>=0 && weight<=1 &&
            fadeIn>=0 && (infinite || fadeIn<=Duration) && fadeOut>=0 && (infinite || fadeOut<=Duration) && warp.Valid;
    }

    [Serializable] internal class LayerSource
    {
        public string id=Guid.NewGuid().ToString("N"), name;
        public List<LayerKey> keys=new List<LayerKey>();
        public int Index(int f)
        {
            int lo=0,hi=keys.Count;
            while(lo<hi){int mid=(lo+hi)/2;if(keys[mid].frame<f)lo=mid+1;else hi=mid;}
            return lo;
        }
        public LayerKey? At(int f){int i=Index(f);return i<keys.Count && keys[i].frame==f ? keys[i] : (LayerKey?)null;}
        public void Write(int frame,object pose)
        {
            int i=Index(frame); bool exists=i<keys.Count && keys[i].frame==frame;
            if(pose==null){if(exists)keys.RemoveAt(i);return;}
            var key=LayerKey.From(pose);key.frame=frame;
            if(exists)keys[i]=key;else keys.Insert(i,key);
        }
        internal LayerKey Sample(float time)
        {
            if(keys.Count==0)return LayerKey.Identity;
            int i=Index(Mathf.CeilToInt(time));
            if(i==0)return keys[0]; if(i==keys.Count)return keys[i-1];
            var a=keys[i-1];var b=keys[i];float t=(time-a.frame)/(b.frame-a.frame);
            return new LayerKey{position=CurveSet.Position(a.position,b.position,b.curves,t),
                rotation=Quaternion.Slerp(a.rotation,b.rotation,CurveSet.Get(b.curves,3).Evaluate(t)),curves=b.curves};
        }
        internal void ValidateKeys()
        {
            if(string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || keys==null)throw new InvalidDataException("モーションソースが不正です。");
            int prior=-1;
            foreach(var key in keys){
                if(key.frame<=prior || key.frame>600000 || !float.IsFinite(key.position.sqrMagnitude) ||
                    !(Mathf.Abs(Quaternion.Dot(key.rotation,key.rotation)-1)<.001f) || !CurveSet.Valid(key.curves))
                    throw new InvalidDataException("モーションソースのキーが不正です。");
                prior=key.frame;
            }
        }
    }

    [Serializable] internal sealed class MotionLayer : LayerSource
    {
        public bool muted;
        public List<ClipPlacement> clips=new List<ClipPlacement>();
        public List<LayerSource> sources=new List<LayerSource>();
        internal LayerSource Source(string sourceId)=>string.IsNullOrEmpty(sourceId) || sourceId==id?this:sources?.Find(s=>s.id==sourceId);
        internal LayerSource SourceFor(string clipId)=>Source(clips.Find(c=>c.id==clipId)?.source);
        internal LayerSource DuplicateSource(ClipPlacement placement){
            var prior=Source(placement.source);
            sources??=new List<LayerSource>();
            var copy=new LayerSource{name=prior.name+" のコピー",keys=new List<LayerKey>(prior.keys)};
            sources.Add(copy);placement.source=copy.id;return copy;
        }
        public bool Map(ClipPlacement clip,float time,out float local,out float weight)
        {
            float parentWeight=1;local=time; weight=0;
            if(!string.IsNullOrEmpty(clip.parent))
            {
                var parent=clips.Find(c=>c.id==clip.parent);
                if(parent==null || !Map(parent,time,out time,out parentWeight))return false;
            }
            if(!clip.Map(time,out local,out weight))return false;
            weight*=parentWeight; return true;
        }
        public LayerKey Contribution(float time,LayerSource editing=null,LayerSource trial=null)
        {
            if(muted)return LayerKey.Identity;
            if(clips.Count==0)return Sample(time);
            var result=LayerKey.Identity;
            foreach(var clip in clips){
                if(!Map(clip,time,out float local,out float weight))continue;
                var source=Source(clip.source);
                result.Append((source==editing?trial:source).Sample(local),weight);
            }
            return result;
        }

        public bool Solve(float time,int local,LayerKey goal,out LayerKey key,LayerSource source=null)
        {
            source??=this;
            var trial=new LayerSource{keys=new List<LayerKey>(source.keys)};
            int at=trial.Index(local);key=trial.At(local)??trial.Sample(local);
            key.frame=local;
            if(!trial.At(local).HasValue)trial.keys.Insert(at,key);
            LayerKey Evaluate(LayerKey v){trial.keys[at]=v;return Contribution(time,source,trial);}
            var value=Evaluate(key);
            var varied=key;varied.position+=Vector3.one;
            var gain=Evaluate(varied).position-value.position;
            for(int axis=0;axis<3;axis++){
                float error=goal.position[axis]-value.position[axis];
                if(Mathf.Abs(gain[axis])>1e-6f)key.position[axis]+=error/gain[axis];
                else if(Mathf.Abs(error)>1e-5f)return false;
            }
            Vector3 Log(Quaternion q){
                if(q.w<0)q=new Quaternion(-q.x,-q.y,-q.z,-q.w);
                var v=new Vector3(q.x,q.y,q.z);float len=v.magnitude;
                return len>1e-8f?v*(2*Mathf.Atan2(len,q.w)/len):2*v;
            }
            Quaternion Rotate(Vector3 d)=>d.sqrMagnitude>1e-14f?Quaternion.AngleAxis(d.magnitude*Mathf.Rad2Deg,d.normalized):Quaternion.identity;
            for(int n=0;n<10;n++){
                var q=Evaluate(key).rotation;
                var error=Log(Quaternion.Inverse(q)*goal.rotation);
                if(error.sqrMagnitude<1e-10f)return true;
                var jacobian=Matrix4x4.identity;
                for(int axis=0;axis<3;axis++){
                    Vector3 offset=Vector3.zero;offset[axis]=.002f;
                    var perturbed=key;perturbed.rotation*=Rotate(offset);
                    var derivative=Log(Quaternion.Inverse(q)*Evaluate(perturbed).rotation)/.002f;
                    jacobian.SetColumn(axis,new Vector4(derivative.x,derivative.y,derivative.z,0));
                }
                if(Mathf.Abs(jacobian.determinant)<1e-9f)return false;
                key.rotation*=Rotate(Vector3.ClampMagnitude(jacobian.inverse.MultiplyVector(error),Mathf.PI));
            }
            return Quaternion.Angle(Evaluate(key).rotation,goal.rotation)<.01f;
        }

        public void Validate()
        {
            ValidateKeys();sources??=new List<LayerSource>();
            if(clips==null || clips.Count>256 || sources.Count>256)throw new InvalidDataException("レイヤー情報が不正です。");
            var ids=new HashSet<string>{id};
            foreach(var source in sources){
                if(source==null || !ids.Add(source.id))throw new InvalidDataException("モーションソースの識別子が重複しています。");
                source.ValidateKeys();
            }
            var seen=new HashSet<string>();
            foreach(var c in clips) {
                if(c==null || !c.Valid || Source(c.source)==null || (!string.IsNullOrEmpty(c.parent) && !seen.Contains(c.parent)) || !seen.Add(c.id))
                    throw new InvalidDataException("クリップの配置が不正です。");
            }
        }
    }

    internal abstract partial class MotionTrack
    {
        internal string Id=Guid.NewGuid().ToString("N");
        internal readonly List<MotionLayer> Layers=new List<MotionLayer>();
        internal string ActiveLayer,ActiveClip;
        internal MotionLayer Target => ActiveLayer==null?null:Layers.Find(l=>l.id==ActiveLayer);
        internal bool SupportsLayers => this is MotionTrack<BonePose> || this is MotionTrack<AccessoryPose> || this is MotionTrack<CameraPose>;
        internal abstract object BaseValue(float time);
        internal LayerSource EditSource=>Target?.SourceFor(ActiveClip);
        internal IEnumerable<int> EditFrames => EditSource?.keys.Select(k=>k.frame) ?? Frames;
        internal object EditKey(int f) => EditSource is LayerSource source?source.At(f)?.Into(CurrentValue,true):SnapshotKey(f);
        internal bool LocalTime(int frame,out int local)
        {
            local=frame;
            var target=Target;
            if(target==null || target.clips.Count==0)return true;
            var clip=target.clips.Find(c=>c.id==ActiveClip);
            if(clip==null || !target.Map(clip,frame,out float mapped,out _))return false;
            local=Mathf.RoundToInt(mapped);return true;
        }
        internal object Compose(object baseValue,float time)
        {
            var result=LayerKey.From(baseValue);
            foreach(var layer in Layers)result.Append(layer.Contribution(time),1);
            return result.Into(baseValue);
        }
        internal object RegistrationValue(int frame)
        {
            if(Layers.Count==0) return CurrentValue;
            var desired=LayerKey.From(CurrentValue);
            var baseValue=BaseValue(frame);
            var before=Target==null ? LayerKey.Identity : LayerKey.From(baseValue);
            var after=LayerKey.Identity;
            bool found=Target==null;
            foreach(var layer in Layers)
            {
                if(layer==Target){found=true;continue;}
                var delta=layer.Contribution(frame);
                if(found)after.Append(delta,1);else before.Append(delta,1);
            }
            desired.position-=before.position+after.position;
            desired.rotation=Quaternion.Inverse(before.rotation)*desired.rotation*Quaternion.Inverse(after.rotation);
            if(Target!=null && Target.clips.Count>0)
            {
                if(!LocalTime(frame,out int local) || !Target.Solve(frame,local,desired,out var solved,EditSource))return null;
                desired=solved;
            }
            return desired.Into(CurrentValue);
        }
        internal int CompositionEnd => Layers.SelectMany(l=>l.clips.Count==0 ? l.keys.Select(k=>k.frame) :
            l.clips.Where(c=>string.IsNullOrEmpty(c.parent)).Select(c=>c.EndFrame)).Append(Frames[Frames.Count-1]).Max();
    }
}
