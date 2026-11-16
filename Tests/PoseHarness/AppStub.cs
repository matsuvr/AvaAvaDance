// Host adapters only: the production pose reader/writer and draft undo capture
// are compiled unchanged. Unity rendering and key registration are not simulated.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    [Serializable] struct Vector3 { public float x, y, z; public float sqrMagnitude => x*x+y*y+z*z; }
    [Serializable] struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => new Quaternion { w = 1 };
        public static float Dot(Quaternion a, Quaternion b) => a.x*b.x+a.y*b.y+a.z*b.z+a.w*b.w;
    }
    sealed class Transform { public string name; public Transform parent; }
    sealed class GameObject { public Transform transform; }
    enum HumanBodyBones { LeftHand, RightHand }
    static class JsonUtility
    {
        static JsonSerializerOptions Options(bool pretty = false) => new JsonSerializerOptions
            { IncludeFields = true, IgnoreReadOnlyProperties = true, WriteIndented = pretty };
        public static string ToJson(object value, bool pretty = false) => JsonSerializer.Serialize(value, value.GetType(), Options(pretty));
        public static T FromJson<T>(string source) => JsonSerializer.Deserialize<T>(source, Options());
    }
}
namespace AvaAvaDance
{
    using UnityEngine;
    [Serializable] struct BonePose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public bool PhysicsOff;
        public object Curves;
    }
    class AccessoryPose { }
    class MotionTrack
    {
        public string Name;
        public object CurrentValue;
        public virtual void Restore(object value) { CurrentValue = value; }
    }
    sealed class MotionTrack<T> : MotionTrack
    {
        public T Current => (T)CurrentValue;
    }
    public sealed partial class DanceApp
    {
        sealed class BoneBinding { public Transform Joint; public MotionTrack<BonePose> Track; }
        sealed class Actor
        {
            public GameObject avatar = new GameObject { transform = new Transform { name = "Avatar" } };
            public List<BoneBinding> bones = new List<BoneBinding>();
            public List<MotionTrack> modelTracks = new List<MotionTrack>();
            public Dictionary<HumanBodyBones, Transform> humanBones;
            public BoneBinding selectedBone;
        }
        sealed class KeyChange { public MotionTrack Track; public int Frame; public object Before, After; public bool IsDraft; public Action<MotionTrack, bool> Replay; }
        sealed class Label { public string text; }
        Actor actor = new Actor();
        readonly List<BoneBinding> boneSelection = new List<BoneBinding>();
        readonly Stack<KeyChange[]> undo = new Stack<KeyChange[]>(), redo = new Stack<KeyChange[]>();
        readonly Label status = new Label();
        bool modelMode = true;
        int frame = 42;
        MotionTrack selectedTrack, cameraTrack;
        static string MorphName(MotionTrack<float> t) => t.Name.StartsWith("表情：", StringComparison.Ordinal) ? t.Name.Substring(3) : t.Name;
        static void WriteFile(string path, byte[] bytes) => System.IO.File.WriteAllBytes(path, bytes);
        static bool ValidPose(object value)
        {
            var p = (BonePose)value;
            return float.IsFinite(p.Position.sqrMagnitude) && p.Position.sqrMagnitude <= 1e12f && Math.Abs(Quaternion.Dot(p.Rotation, p.Rotation)-1) < .001f;
        }
        void StopPlayback() { }
        void RefreshKeyButtons() { }
        void InvalidateDynamics() { }
        void RefreshRigPreview() { }
        void SyncControls() { }
        void RedrawOverlay() { }
    }
}
