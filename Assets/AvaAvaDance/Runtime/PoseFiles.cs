using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class SavedPose
        {
            public string format;
            public SavedPoseBone[] bones;
            public SavedPoseMorph[] morphs;
        }
        [Serializable] sealed class SavedPoseBone
        {
            public string name;
            public string[] path;
            public Vector3 position;
            public Quaternion rotation;
        }
        [Serializable] sealed class SavedPoseMorph
        {
            public string name;
            public float weight;
        }

        string[] PoseBonePath(Transform joint)
        {
            var names = new List<string>();
            for (var node = joint; node != null; node = node.parent)
            {
                if (node == actor.avatar.transform) { names.Reverse(); return names.ToArray(); }
                names.Add(node.name);
            }
            return null;
        }
        static string PosePathKey(string[] path) => path == null ? null :
            string.Join("/", path.Select(name => name.Replace("~", "~0").Replace("/", "~1")));

        void SavePoseFile(string path)
        {
            if (!path.EndsWith(".avapose", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ポーズの保存には .avapose を指定してください。");
            var pose = new SavedPose
            {
                format = "AvaAvaDance pose 1",
                bones = actor.bones.Select(b => new SavedPoseBone
                {
                    name = b.Joint.name, path = PoseBonePath(b.Joint),
                    position = b.Track.Current.Position, rotation = b.Track.Current.Rotation
                }).ToArray(),
                morphs = actor.modelTracks.OfType<MotionTrack<float>>().Select(t =>
                    new SavedPoseMorph { name = MorphName(t), weight = t.Current }).ToArray()
            };
            ValidatePoseFile(pose);
            WriteFile(path, Encoding.UTF8.GetBytes(JsonUtility.ToJson(pose, true)));
        }

        static void ValidatePoseFile(SavedPose pose)
        {
            if (pose == null || pose.format != "AvaAvaDance pose 1" || pose.bones == null && pose.morphs == null)
                throw new InvalidDataException("AvaAvaDanceのポーズ形式を選択してください。");
            foreach (var bone in pose.bones ?? Array.Empty<SavedPoseBone>())
                if (bone == null || bone.name == null || bone.path != null && bone.path.Any(n => n == null) ||
                    !ValidPose(new BonePose { Position = bone.position, Rotation = bone.rotation }))
                    throw new InvalidDataException("ポーズのボーン名または位置・回転が不正です。");
            foreach (var morph in pose.morphs ?? Array.Empty<SavedPoseMorph>())
                if (morph == null || morph.name == null || !float.IsFinite(morph.weight))
                    throw new InvalidDataException("ポーズの表情名またはウェイトが不正です。");
        }

        void LoadPoseFile(string path)
        {
            var pose = JsonUtility.FromJson<SavedPose>(File.ReadAllText(path, Encoding.UTF8));
            ValidatePoseFile(pose);
            var paths = actor.bones.ToLookup(b => PosePathKey(PoseBonePath(b.Joint)), StringComparer.Ordinal);
            var names = actor.bones.ToLookup(b => b.Joint.name, StringComparer.Ordinal);
            var morphs = actor.modelTracks.OfType<MotionTrack<float>>().ToLookup(MorphName, StringComparer.Ordinal);
            var values = new Dictionary<MotionTrack, object>();
            int bones = 0, expressions = 0, missing = 0, ambiguous = 0;
            foreach (var saved in pose.bones ?? Array.Empty<SavedPoseBone>())
            {
                var matches = saved.path == null ? Array.Empty<BoneBinding>() : paths[PosePathKey(saved.path)].ToArray();
                if (matches.Length == 0) matches = names[saved.name].ToArray();
                if (matches.Length != 1) { if (matches.Length == 0) missing++; else ambiguous++; continue; }
                var track = matches[0].Track;
                if (values.ContainsKey(track)) throw new InvalidDataException("同じボーンがポーズ内で重複しています。");
                // Native poses contain local offsets for the authored rig. Keep the
                // target's interpolation and physics flags; loading does not add keys.
                var value = track.Current; value.Position = saved.position; value.Rotation = saved.rotation;
                values.Add(track, value); bones++;
            }
            foreach (var saved in pose.morphs ?? Array.Empty<SavedPoseMorph>())
            {
                var matches = morphs[saved.name].ToArray();
                if (matches.Length != 1) { if (matches.Length == 0) missing++; else ambiguous++; continue; }
                if (values.ContainsKey(matches[0])) throw new InvalidDataException("同じ表情がポーズ内で重複しています。");
                values.Add(matches[0], saved.weight); expressions++;
            }
            if (values.Count == 0)
                throw new InvalidDataException($"一致するボーン・表情がありません。一致なし {missing}、同名で特定できない項目 {ambiguous}");
            SetDraftValues(values);
            status.text = $"ポーズ適用　{bones} ボーン・{expressions} 表情　未登録　一致なし {missing}・同名未適用 {ambiguous}";
        }
    }
}
