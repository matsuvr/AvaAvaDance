using System;
using System.Numerics;

namespace Ava.Extensions
{
    public enum TrackChannel { Bone, Morph, Camera, Light, Shadow, Gravity, ModelSwitch, Accessory }
    public enum CurveAction { Preserve, Replace, Clear }
    public struct BezierCurve
    {
        public bool Custom;
        public Vector2 First, Second;
    }
    public sealed class KeyValue
    {
        public TrackChannel Channel;
        public Vector3 Position, Euler, Direction;
        public Quaternion Rotation=Quaternion.Identity;
        public Vector4 Color;
        public float Weight, Distance, ViewAngle, Scale, Opacity, Acceleration, Noise, Range;
        public int ShadowMode;
        public bool PhysicsOff, Perspective, Disabled, Hidden, Noisy;
        public string ParentBone, ParentActor, ParentBoneId;
        public string[] DisabledIK;
        public BezierCurve[] Curves;
    }
    public sealed class TrackSnapshot
    {
        public string Owner, Id, Name;
        // Bone identity in the owner's RigProfile.joints/editRest arrays. Display names
        // may be duplicated or localized and must not be used as hierarchy identity.
        public int RigJointIndex=-1;
        public string Role;
        public TrackChannel Channel;
        public long Revision;
        public int[] Frames;
        public KeyValue[] Values;
        public KeyValue Current;
    }
    public sealed class MotionSnapshot
    {
        public long Document;
        public int Frame;
        public string Selection;
        public TrackSnapshot[] Tracks;
        public string[] DrawOrder, CalculationOrder;
    }
    public sealed class KeyEdit
    {
        public string Owner, Track;
        public long Revision;
        public int Frame;
        // A null value removes the key.
        public KeyValue Value;
        public CurveAction Curves;
    }
    public interface IMotionEditor
    {
        MotionSnapshot ReadMotion();
        KeyValue Sample(string owner,string track,float frame);
        void ApplyMotion(long document,KeyEdit[] edits);
    }
}
