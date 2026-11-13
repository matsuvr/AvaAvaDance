using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvaAvaDance
{
    internal abstract partial class MotionTrack
    {
        public readonly string Name;
        internal long Revision;
        Dictionary<int,KeySource> sources;
        internal KeySource SourceAt(int at)=>sources!=null && sources.TryGetValue(at,out var source)?source:null;
        internal void SetSource(int at,KeySource source)
        {
            if(source==null)sources?.Remove(at);else (sources??=new Dictionary<int,KeySource>())[at]=source;
        }
        internal void ClearSources()=>sources?.Clear();
        protected MotionTrack(string name) { Name = name; }
        public abstract IList<int> Frames { get; }
        internal virtual bool EnabledKey(int frame)=>true;
        public abstract void Seek(float frame);
        public abstract object SampleValue(float frame);
        public abstract object KeyAt(int frame);
        Dictionary<int, EaseCurve> morphCurves;
        internal EaseCurve CurveAt(int frame) => morphCurves != null && morphCurves.TryGetValue(frame,out var c) ? c : default;
        internal object WithCurve(int frame, object value) => value is float weight && CurveAt(frame).custom
            ? (object)new MorphKey {Weight=weight, Curve=CurveAt(frame)} : value;
        internal bool HasDraft(float frame)
        {
            var value=CurrentValue;var sampled=SampleValue(frame);
            if(value is BonePose b && sampled is BonePose bs)
                return b.Position!=bs.Position || Quaternion.Angle(b.Rotation,bs.Rotation)>.001f || b.PhysicsOff!=bs.PhysicsOff;
            if(value is CameraPose c && sampled is CameraPose cs)
                return c.Center!=cs.Center || c.Angles!=cs.Angles || c.Distance!=cs.Distance || c.ViewAngle!=cs.ViewAngle || c.Perspective!=cs.Perspective;
            if(value is SwitchPose s && sampled is SwitchPose ss)
                return s.Hidden!=ss.Hidden || !System.Linq.Enumerable.SequenceEqual(s.DisabledIK??Array.Empty<string>(),ss.DisabledIK??Array.Empty<string>());
            return !Equals(value,sampled);
        }
        internal object SnapshotKey(int frame) => WithCurve(frame, KeyAt(frame));
        internal IEnumerable<int> OutputFrames()
        {
            if (Layers.Count>0)
            {
                int end=LastFrame;
                for(int f=0;f<=end;f++)yield return f;
                yield break;
            }
            for (int i=0; i<Frames.Count; i++)
            {
                int end = Frames[i];
                if (i>0 && CurveAt(end).custom)
                    for (int f=Frames[i-1]+1; f<end; f++) yield return f;
                if(EnabledKey(end))yield return end;
            }
        }
        protected object StoreCurve(int frame, object value)
        {
            if (value is MorphKey key)
            {
                if (key.Curve.custom) (morphCurves ??= new Dictionary<int,EaseCurve>())[frame]=key.Curve;
                else morphCurves?.Remove(frame);
                return key.Weight;
            }
            morphCurves?.Remove(frame); return value;
        }
        public abstract void Restore(object value);
        public abstract object CurrentValue { get; }
        public abstract void Write(int frame, object value);
        public int LastFrame => Layers.Count==0 ? Frames[Frames.Count - 1] : CompositionEnd;
    }

    internal sealed class MotionTrack<T> : MotionTrack where T : struct
    {
        readonly SortedList<int, T> keys = new SortedList<int, T>();
        readonly Func<T, T, float, T> blend;
        readonly Action<T> apply;
        int inactiveKeys;
        readonly T initial;
        internal override bool EnabledKey(int frame) => inactiveKeys==0 || !(KeyAt(frame) is CameraPose p && p.Inactive);

        public T Current { get; private set; }
        public override object CurrentValue => Current;
        public override IList<int> Frames => keys.Keys;

        public MotionTrack(string name, T initial, Func<T, T, float, T> blend, Action<T> apply) : base(name)
        {
            this.initial=initial;
            keys.Add(0, initial);
            Current = initial;
            this.blend = blend;
            this.apply = apply;
        }

        public override void Restore(object value) => Preview((T)value);

        public void Preview(T value)
        {
            Current = value;
            apply(value);
        }

        public override void Seek(float frame) => Preview(Sample(frame));

        public override object SampleValue(float frame) => Sample(frame);

        internal T Sample(float frame) => Layers.Count==0 ? SampleBase(frame) : (T)Compose(SampleBase(frame),frame);
        internal override object BaseValue(float time) => SampleBase(time);
        T SampleBase(float frame)
        {
            int low = 0, high = keys.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (keys.Keys[mid] <= frame) low = mid;
                else high = mid - 1;
            }
            int next=low+1;
            if(inactiveKeys>0)
            {
                while(low>=0 && !EnabledKey(keys.Keys[low]))low--;
                while(next<keys.Count && !EnabledKey(keys.Keys[next]))next++;
                if(low<0)return next==keys.Count?initial:keys.Values[next];
            }
            T value = keys.Values[low];
            if (next < keys.Count && frame > keys.Keys[low])
            {
                float t = (float)(frame - keys.Keys[low]) / (keys.Keys[next] - keys.Keys[low]);
                value = blend(value, keys.Values[next], CurveAt(keys.Keys[next]).Evaluate(t));
            }
            return value;
        }

        public override object KeyAt(int frame) => keys.TryGetValue(frame, out var value) ? (object)value : null;
        public override void Write(int frame, object value)
        {
            if (frame == 0 && value == null) return;
            Revision++;if(value==null)SetSource(frame,null);
            if(typeof(T)==typeof(CameraPose))
            {
                if(KeyAt(frame) is CameraPose old && old.Inactive)inactiveKeys--;
                if(value is CameraPose pose && pose.Inactive)inactiveKeys++;
            }
            value = StoreCurve(frame,value);
            if (value != null) keys[frame] = (T)value;
            else if (frame != 0) keys.Remove(frame);
        }
    }

    [Serializable] internal sealed class KeySource
    {
        public byte[] name,ik;
    }

    internal struct MorphKey { public float Weight; public EaseCurve Curve; }

    [Serializable]
    internal struct BonePose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public bool PhysicsOff;
        public CurveSet Curves;
        public static BonePose Rest => new BonePose { Rotation = Quaternion.identity };
        public static BonePose Blend(BonePose a, BonePose b, float t) => new BonePose
        {
            Curves = b.Curves,
            PhysicsOff = a.PhysicsOff,
            Position = CurveSet.Position(a.Position, b.Position, b.Curves, t),
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, CurveSet.Get(b.Curves,3).Evaluate(t))
        };
    }

    [Serializable]
    internal struct CameraPose
    {
        public Vector3 Center, Angles;
        public float Distance, ViewAngle;
        public bool Perspective,Inactive;
        public CurveSet Curves;
        public static CameraPose Initial => new CameraPose
        {
            Center = new Vector3(0, 10, 0), Distance = 45, ViewAngle = 30, Perspective = true
        };
        public static CameraPose Blend(CameraPose a, CameraPose b, float t) => new CameraPose
        {
            Curves = b.Curves,
            Center = CurveSet.Position(a.Center, b.Center, b.Curves, t),
            Angles = Vector3.Lerp(a.Angles, b.Angles, CurveSet.Get(b.Curves,3).Evaluate(t)),
            Distance = Mathf.Lerp(a.Distance, b.Distance, CurveSet.Get(b.Curves,4).Evaluate(t)),
            ViewAngle = Mathf.Lerp(a.ViewAngle, b.ViewAngle, CurveSet.Get(b.Curves,5).Evaluate(t)),
            Perspective = a.Perspective
        };
    }

    [Serializable]
    internal struct LightPose
    {
        public Color Color;
        public Vector3 Direction;
        public static LightPose Initial => new LightPose
        {
            Color = new Color32(154, 154, 154, 255), Direction = new Vector3(-.5f, -1, .5f)
        };
        public static LightPose Blend(LightPose a, LightPose b, float t) => new LightPose
        {
            Color = Color.Lerp(a.Color, b.Color, t),
            Direction = Vector3.Lerp(a.Direction, b.Direction, t)
        };
    }
}
