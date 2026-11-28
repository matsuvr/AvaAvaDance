using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance.Import
{
    // BVH and FBX supply transforms in a source skeleton, including unmapped helper joints.
    // Decode these on import only. The timeline receives ordinary target-local BonePose keys.
    internal sealed class HumanoidSkeletonMotion
    {
        internal readonly int[] Parents;
        internal readonly Dictionary<HumanBodyBones, int> Bones;
        internal readonly Matrix4x4[] Rest;
        internal readonly Matrix4x4[] World;
        internal readonly Quaternion Facing;
        internal readonly float Height;
        internal readonly double Duration;
        readonly Action<double, Matrix4x4[]> sample;

        internal HumanoidSkeletonMotion(string[] names, int[] parents, Matrix4x4[] local, double duration,
            Action<double, Matrix4x4[]> sample, Dictionary<string,string> meta = null)
        {
            Parents = parents;
            Bones = Map(names, meta);
            Rest = local;
            ToWorld(Rest);
            World = new Matrix4x4[names.Length];
            Duration = duration;
            this.sample = sample;
            if (!Bones.TryGetValue(HumanBodyBones.Hips, out int hip))
                throw new InvalidDataException("Humanoid の腰ボーンを特定できません。");
            Vector3 Point(int i) => Rest[i].GetColumn(3);
            Height = Point(hip).y;
            if (Height < 1e-5f && Bones.TryGetValue(HumanBodyBones.LeftFoot, out int foot))
                Height = Point(hip).y - Point(foot).y;
            if (!(Height > 1e-5f)) throw new InvalidDataException("腰の基準高を取得できません。");
            Facing = Quaternion.identity;
            if ((Bones.TryGetValue(HumanBodyBones.LeftUpperArm, out int l) && Bones.TryGetValue(HumanBodyBones.RightUpperArm, out int r)) ||
                (Bones.TryGetValue(HumanBodyBones.LeftUpperLeg, out l) && Bones.TryGetValue(HumanBodyBones.RightUpperLeg, out r)))
            {
                var forward = Vector3.Cross(Point(r) - Point(l), Vector3.up);
                if (forward.sqrMagnitude > 1e-10f) Facing = Quaternion.LookRotation(forward);
            }
        }

        void ToWorld(Matrix4x4[] matrices)
        {
            for (int i = 0; i < matrices.Length; i++)
            {
                int p = Parents[i];
                if (p >= i || p < -1) throw new InvalidDataException("モーションの親子関係が不正です。");
                if (p >= 0) matrices[i] = matrices[p] * matrices[i];
            }
        }

        internal void Sample(double seconds)
        {
            sample(Math.Min(seconds, Duration), World);
            ToWorld(World);
        }

        static readonly Dictionary<string,HumanBodyBones> aliases = BoneAliases();
        static Dictionary<string,HumanBodyBones> BoneAliases()
        {
            var names = new Dictionary<string,HumanBodyBones>(StringComparer.OrdinalIgnoreCase);
            for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++) names[id.ToString()] = id;
            void Alias(string name, string role) => names[name] = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), role);
            Alias("Pelvis","Hips"); Alias("Spine1","Chest"); Alias("Spine2","UpperChest");
            foreach (string side in new[] {"Left","Right"})
            {
                foreach (var (source,role) in new[] {("UpLeg","UpperLeg"),("Leg","LowerLeg"),("ToeBase","Toes"),
                    ("Arm","UpperArm"),("ForeArm","LowerArm"),("Collar","Shoulder"),("Thigh","UpperLeg"),("Shin","LowerLeg")})
                    Alias(side+source, side+role);
                foreach (string finger in new[] {"Thumb","Index","Middle","Ring","Little","Pinky"})
                    for (int n=0; n<3; n++)
                        Alias(side+"Hand"+finger+(n+1), side+(finger=="Pinky"?"Little":finger)+new[]{"Proximal","Intermediate","Distal"}[n]);
            }
            return names;
        }

        internal static Dictionary<HumanBodyBones,int> Map(string[] names, Dictionary<string,string> meta)
        {
            var result = new Dictionary<HumanBodyBones,int>();
            if (meta != null)
                foreach (var pair in meta)
                    if (aliases.TryGetValue(pair.Key.Replace(" ",""), out var role))
                    {
                        int i = Array.IndexOf(names, pair.Value);
                        if (i >= 0) result[role] = i;
                    }
            var explicitRoles = result.Keys.ToHashSet();
            var explicitNodes = result.Values.ToHashSet();
            var ambiguous = new HashSet<HumanBodyBones>();
            for (int i=0; i<names.Length; i++)
            {
                string name = names[i].Substring(names[i].LastIndexOf(':')+1).Replace("_","").Replace(" ","");
                if (!aliases.TryGetValue(name, out var id) || explicitRoles.Contains(id) || explicitNodes.Contains(i)) continue;
                if (!result.TryAdd(id, i)) ambiguous.Add(id);
            }
            foreach (var id in ambiguous) result.Remove(id);
            return result;
        }
    }
}
