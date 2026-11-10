using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvaAvaDance
{
    internal abstract class MotionTrack
    {
        public readonly string Name;
        protected MotionTrack(string name) { Name = name; }
        public abstract IList<int> Frames { get; }
        public abstract void Seek(float frame);
        public abstract object KeyAt(int frame);
        public abstract void Restore(object value);
        public abstract object CurrentValue { get; }
        public abstract void Write(int frame, object value);
        public int LastFrame => Frames[Frames.Count - 1];
    }

    internal sealed class MotionTrack<T> : MotionTrack where T : struct
    {
        readonly SortedList<int, T> keys = new SortedList<int, T>();
        readonly Func<T, T, float, T> blend;
        readonly Action<T> apply;

        public T Current { get; private set; }
        public override object CurrentValue => Current;
        public override IList<int> Frames => keys.Keys;

        public MotionTrack(string name, T initial, Func<T, T, float, T> blend, Action<T> apply) : base(name)
        {
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

        internal T Sample(float frame)
        {
            int low = 0, high = keys.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (keys.Keys[mid] <= frame) low = mid;
                else high = mid - 1;
            }
            T value = keys.Values[low];
            if (low + 1 < keys.Count && frame > keys.Keys[low])
            {
                float t = (float)(frame - keys.Keys[low]) / (keys.Keys[low + 1] - keys.Keys[low]);
                value = blend(value, keys.Values[low + 1], t);
            }
            return value;
        }

        public override object KeyAt(int frame) => keys.TryGetValue(frame, out var value) ? (object)value : null;
        public override void Write(int frame, object value)
        {
            if (value != null) keys[frame] = (T)value;
            else if (frame != 0) keys.Remove(frame);
        }
    }

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
        public bool Perspective;
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
