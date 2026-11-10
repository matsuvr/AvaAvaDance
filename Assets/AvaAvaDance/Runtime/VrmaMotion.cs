using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvaAvaDance.Import;
using UniGLTF;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        void LoadVrma(string path)
        {
            if (!modelMode || actor.avatar == null) throw new InvalidDataException("モデル操作でモーションを読み込むモデルを選択してください。");
            using var data = new GlbBinaryParser(ReadMmdFile(path), Path.GetFileName(path)).Parse();
            int start = frame;
            var clip = new VrmaClip(data, MaxFrame - start);
            int end = start + clip.LastFrame;
            var changes = new List<KeyChange>();
            var driven = new HashSet<Transform>();
            var sourceMapped = clip.Bones.Where(k => actor.humanBones.ContainsKey(k.Key)).Select(k => k.Value).ToHashSet();
            var bindings = actor.bones.ToDictionary(b => b.Joint);

            void Add(MotionTrack track, int f, object value)
            {
                if (!ValidPose(value)) throw new InvalidDataException("変換した VRMA キーが編集可能範囲外です。");
                if (changes.Count >= 250000) throw new InvalidDataException("VRMA の変換キー数が上限を超えています。");
                changes.Add(new KeyChange { Track = track, Frame = start + f, After = value });
            }

            // Combine rotations from optional source joints missing on this avatar
            // (e.g. upperChest) into the next mapped joint in each branch.
            foreach (var (id, source) in clip.Bones)
            {
                if (!actor.humanBones.TryGetValue(id, out var target) || !bindings.TryGetValue(target, out var bone)) continue;
                var chain = new Stack<VrmaClip.Node>();
                for (var n = source; n != null; n = n.Parent)
                {
                    if (n != source && sourceMapped.Contains(n)) break;
                    if (n.Rotation != null) chain.Push(n);
                }
                var translation = id == HumanBodyBones.Hips ? source.Translation : null;
                if (chain.Count == 0 && translation == null) continue;
                var curves = chain.Select(n => n.Rotation).ToList();
                if (translation != null) curves.Add(translation);
                var bind = actor.bindPose[target];
                var basis = Quaternion.Inverse(actor.humanForward) * bind.InitialGlobalRotation;
                var restOffset = Quaternion.Inverse(bone.RestRotation) * bind.InitialLocalRotation;
                float height = source.World.m13;
                if (translation != null && (height < 1e-5f || actor.hipsHeight < 1e-5f))
                    throw new InvalidDataException("腰の基準高を取得できません。");
                foreach (int f in VrmaFrames(bone.Track, curves, start, end, chain.Count > 1))
                {
                    float seconds = f / 30f;
                    var pose = bone.Track.Sample(start + f);
                    if (chain.Count > 0)
                    {
                        var q = Quaternion.identity;
                        foreach (var n in chain) q *= n.NormalizedRotation(seconds);
                        pose.Rotation = (restOffset * Quaternion.Inverse(basis) * q * basis).normalized;
                    }
                    if (translation != null)
                    {
                        var v = translation.Sample(seconds);
                        var local = new Vector3(-v.x, v.y, v.z) - (Vector3)source.Local.GetColumn(3);
                        var delta = (source.Parent?.World ?? Matrix4x4.identity).MultiplyVector(local);
                        pose.Position = actor.hipsParentInverse.MultiplyVector(actor.humanForward * delta * (actor.hipsHeight / height));
                    }
                    pose.Curves = null;
                    Add(bone.Track, f, pose);
                }
                driven.Add(target);
            }

            var face = actor.modelTracks.OfType<MotionTrack<float>>().ToList();
            int expressions = 0;
            foreach (var (name, source) in clip.Expressions)
            {
                if (source.Translation == null) continue;
                var target = FindVrmaExpression(face, name);
                if (target == null) continue;
                foreach (int f in VrmaFrames(target, new[] { source.Translation }, start, end))
                    Add(target, f, Mathf.Clamp01(source.Translation.Sample(f/30f).x));
                expressions++;
            }
            bool gaze = false;
            if (clip.Gaze?.Rotation != null)
            {
                var eyes = actor.gaze != null ? new[] { actor.gaze } :
                    actor.humanBones.Where(h => h.Key == HumanBodyBones.LeftEye || h.Key == HumanBodyBones.RightEye).Select(h => h.Value);
                foreach (var eye in eyes)
                {
                    if (!bindings.TryGetValue(eye, out var b)) continue;
                    var basis = eye == actor.gaze ? Quaternion.identity : Quaternion.Inverse(actor.humanForward) * actor.bindPose[eye].InitialGlobalRotation;
                    foreach (int f in VrmaFrames(b.Track, new[] { clip.Gaze.Rotation }, start, end))
                    {
                        var p = b.Track.Sample(start + f);
                        p.Rotation = Quaternion.Inverse(basis) * clip.Gaze.Rotation.SampleRotation(f/30f) * basis;
                        p.Curves = null;
                        Add(b.Track, f, p);
                    }
                    gaze = true;
                }
            }
            if (changes.Count == 0) throw new InvalidDataException("モデルに対応するボーン・表情・視線がありません。");

            // MMD's default foot IK would pin the feet to their original positions.
            // Only disable chains affected by FK, preserving visibility and other IK keys.
            var ik = actor.ikChains.Where(c => driven.Any(t => c.Tip.IsChildOf(t))).Select(c => c.Handle.Track.Name).ToArray();
            if (ik.Length > 0)
            {
                foreach (int f in actor.modelSwitch.Frames.Where(f => f > start && f <= end).Prepend(start))
                {
                    var state = actor.modelSwitch.Sample(f);
                    state.DisabledIK = (state.DisabledIK ?? Array.Empty<string>()).Union(ik).ToArray();
                    Add(actor.modelSwitch, f-start, state);
                }
                if (end < MaxFrame && actor.modelTracks.Any(t => t.LastFrame > end))
                    Add(actor.modelSwitch, clip.LastFrame+1, actor.modelSwitch.Sample(end+1));
            }
            CommitKeys(changes);
            SetFrame(frame);
            status.text = $"VRMA 読込　{driven.Count} ボーン・{expressions} 表情" +
                (gaze ? "・視線" : "") + $"　{start}～{end} フレーム";
        }

        static SortedSet<int> VrmaFrames(MotionTrack track, IEnumerable<VrmaClip.Curve> curves, int start, int end, bool coupled = false)
        {
            var frames = new SortedSet<int>(track.Frames.Where(f => f > start && f < end).Select(f => f-start)) { 0, end-start };
            foreach (var curve in curves) curve.AddFrames(frames, coupled);
            return frames;
        }

        static MotionTrack<float> FindVrmaExpression(List<MotionTrack<float>> tracks, string name)
        {
            var exact = tracks.Find(t => MorphName(t) == name);
            if (exact != null) return exact;
            string[] aliases = name switch {
                "aa" => new[] { "あ", "A", "vrc.v_aa" }, "ih" => new[] { "い", "I", "vrc.v_ih" },
                "ou" => new[] { "う", "U", "vrc.v_ou" }, "ee" => new[] { "え", "E", "vrc.v_e" },
                "oh" => new[] { "お", "O", "vrc.v_oh" }, "blink" => new[] { "まばたき", "Blink" },
                "happy" => new[] { "笑い", "Joy" }, _ => Array.Empty<string>()
            };
            return aliases.Select(alias => tracks.Find(t => string.Equals(MorphName(t), alias, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(t => t != null);
        }
    }
}
