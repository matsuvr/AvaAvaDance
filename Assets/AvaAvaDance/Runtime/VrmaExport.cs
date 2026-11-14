using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniGLTF;
using UniJSON;
using UniVRM10;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Keep both ends of a hold. Otherwise linear interpolation would start
        // moving a stationary joint before the frame where it actually moves.
        sealed class RotationSamples
        {
            internal readonly List<int> Frames = new();
            internal readonly List<Vector4> Values = new();
            bool holding;

            internal void Add(int frame, Quaternion rotation)
            {
                var q = new Vector4(rotation.x, -rotation.y, -rotation.z, rotation.w).normalized;
                if (!float.IsFinite(q.sqrMagnitude) || q.sqrMagnitude < .99f)
                    throw new InvalidDataException("ボーンの回転が不正です。");
                if (Values.Count > 0 && Vector4.Dot(Values[Values.Count-1], q) < 0) q = -q;
                bool same = Values.Count > 0 && (q - Values[Values.Count-1]).sqrMagnitude < 1e-12f;
                if (same && holding) Frames[Frames.Count-1] = frame;
                else { Frames.Add(frame); Values.Add(q); }
                holding = same;
            }
        }

        void SaveVrma(string path)
        {
            if (!modelMode || actor.avatar == null) throw new InvalidDataException("書き出すモデルを選択してください。");
            var data = new ExportingGltfData(4096);
            var gltf = data.Gltf;
            gltf.asset.version = "2.0"; gltf.asset.generator = "AvaAvaDance";
            var animation = new glTFAnimation { name = Path.GetFileNameWithoutExtension(path) };
            gltf.animations.Add(animation);
            var roots = new List<int>();
            int Node(string name, Vector3 position)
            {
                int index = gltf.nodes.Count;
                gltf.nodes.Add(new glTFNode { name = name, translation = new[] { -position.x, position.y, position.z } });
                return index;
            }
            void Channel<T>(int node, string property, IList<int> frames, T[] samples) where T : struct
            {
                var times = frames.Select(f => f/30f).ToArray();
                int input = data.ExtendBufferAndGetAccessorIndex(times);
                gltf.accessors[input].min = new[] { times[0] };
                gltf.accessors[input].max = new[] { times[times.Length-1] };
                var sampler = new glTFAnimationSampler { input = input, output = data.ExtendBufferAndGetAccessorIndex(samples), interpolation = "LINEAR" };
                animation.channels.Add(new glTFAnimationChannel { sampler = animation.samplers.Count, target = new glTFAnimationTarget { node = node, path = property } });
                animation.samplers.Add(sampler);
            }
            var ext = new JsonFormatter();
            ext.BeginMap(); ext.Key("specVersion"); ext.Value("1.0");
            void NodeRef(string name, int i) { ext.Key(name); ext.BeginMap(); ext.Key("node"); ext.Value(i); ext.EndMap(); }

            var human = actor.humanBones.Where(h => h.Key != HumanBodyBones.LeftEye && h.Key != HumanBodyBones.RightEye).ToArray();
            bool hasRig = Enumerable.Range(0, (int)HumanBodyBones.LastBone)
                .Where(HumanTrait.RequiredBone).All(i => actor.humanBones.ContainsKey((HumanBodyBones)i));
            int end = actor.modelTracks.Max(t => t.LastFrame);
            if (hasRig)
            {
                var facing = Quaternion.Inverse(actor.humanForward);
                var root = actor.avatar.transform;
                float unit = actor.vrm != null ? 1f/root.lossyScale.y : 1f/actor.hipsHeight;
                var indices = human.Select((h, i) => (h.Value, i)).ToDictionary(k => k.Value, k => k.i);
                var parents = new int[human.Length];
                var rotations = human.Select(_ => new RotationSamples()).ToArray();
                var inverseBind = human.Select(h => Quaternion.Inverse(actor.bindPose[h.Value].WorldBasis) * actor.humanForward).ToArray();
                var deltas = new Quaternion[human.Length];
                var hip = actor.humanBones[HumanBodyBones.Hips];
                int count = end+1;
                if ((long)count * human.Length > 3_000_000) throw new InvalidDataException("書き出すモーションが長すぎます。");
                var positions = new Vector3[count];
                ext.Key("humanoid"); ext.BeginMap(); ext.Key("humanBones"); ext.BeginMap();
                for (int i = 0; i < human.Length; i++)
                {
                    var (id, joint) = human[i];
                    var parent = joint.parent;
                    while (parent != null && !indices.ContainsKey(parent)) parent = parent.parent;
                    parents[i] = parent == null ? -1 : indices[parent];
                    var rest = actor.bindPositions[joint] - (parent == null ? Vector3.zero : actor.bindPositions[parent]);
                    var name = id.ToString().Replace("ThumbProximal", "ThumbMetacarpal").Replace("ThumbIntermediate", "ThumbProximal");
                    name = char.ToLowerInvariant(name[0]) + name.Substring(1);
                    NodeRef(name, Node(name, facing * root.TransformVector(rest) * unit));
                    if (parents[i] < 0) roots.Add(i);
                }
                ext.EndMap(); ext.EndMap();
                for (int i = 0; i < human.Length; i++)
                {
                    var children = Enumerable.Range(0, human.Length).Where(n => parents[n] == i).ToArray();
                    if (children.Length > 0) gltf.nodes[i].children = children;
                }

                // Sample registered FK and solve IK in exactly the timeline's order.
                // Restore live edits and the solved joint transforms even on failure.
                var original = actor.bones.Select(b => (b, b.Track.Current, b.Joint.localRotation, b.Joint.localPosition)).ToArray();
                var switches = actor.modelSwitch.Current;
                bool visible = actor.avatar.activeSelf;
                try
                {
                    for (int f = 0; f < count; f++)
                    {
                        foreach (var b in actor.bones) b.Track.Seek(f);
                        actor.modelSwitch.Seek(f);
                        SolveIK();
                        for (int i = 0; i < human.Length; i++) deltas[i] = facing * HumanoidBindRotation.RotationBasis(human[i].Value) * inverseBind[i];
                        for (int i = 0; i < human.Length; i++)
                            rotations[i].Add(f, parents[i] < 0 ? deltas[i] : Quaternion.Inverse(deltas[parents[i]]) * deltas[i]);
                        var p = facing * (hip.position - root.position) * unit;
                        positions[f] = new Vector3(-p.x, p.y, p.z);
                    }
                }
                finally
                {
                    foreach (var (b, pose, rotation, position) in original)
                    {
                        b.Track.Preview(pose); b.Joint.localRotation = rotation; b.Joint.localPosition = position;
                    }
                    actor.modelSwitch.Preview(switches); actor.avatar.SetActive(visible);
                }
                Channel(indices[hip], "translation", Enumerable.Range(0, count).ToArray(), positions);
                for (int i = 0; i < rotations.Length; i++) Channel(i, "rotation", rotations[i].Frames, rotations[i].Values.ToArray());
            }

            var allFaces = actor.modelTracks.OfType<MotionTrack<float>>().ToList();
            var faceNames = allFaces.ToDictionary(t => t, MorphName);
            if (actor.vrm == null)
                foreach (ExpressionPreset preset in Enum.GetValues(typeof(ExpressionPreset)))
                {
                    if (preset == ExpressionPreset.custom) continue;
                    var mapped = FindVrmaExpression(allFaces, preset.ToString());
                    if (mapped != null) faceNames[mapped] = preset.ToString();
                }
            var faces = allFaces.Where(t => t.Frames.Count > 1 || (float)t.KeyAt(0) != 0).ToArray();
            var groups = faces.GroupBy(t => Enum.TryParse<ExpressionPreset>(faceNames[t], out var key) && key != ExpressionPreset.custom ? "preset" : "custom");
            if (faces.Length > 0)
            {
                ext.Key("expressions"); ext.BeginMap();
                foreach (var group in groups)
                {
                    ext.Key(group.Key); ext.BeginMap();
                    foreach (var track in group)
                    {
                        var name = faceNames[track];
                        if (name == "lookLeft" || name == "lookRight" || name == "lookUp" || name == "lookDown") continue;
                        int node = Node("expression_" + name, Vector3.zero); roots.Add(node); NodeRef(name, node);
                        var times = track.OutputFrames().ToArray();
                        Channel(node, "translation", times, times.Select(f => new Vector3(track.Sample(f), 0, 0)).ToArray());
                    }
                    ext.EndMap();
                }
                ext.EndMap();
            }
            var eye = actor.gaze;
            if (eye == null && !actor.humanBones.TryGetValue(HumanBodyBones.LeftEye, out eye))
                actor.humanBones.TryGetValue(HumanBodyBones.RightEye, out eye);
            var gaze = actor.bones.Find(b => b.Joint == eye);
            if (gaze != null && (gaze.Track.LastFrame > 0 || Quaternion.Angle(gaze.Track.Sample(0).Rotation, Quaternion.identity) > .001f))
            {
                int node = Node("lookAt", Vector3.zero); roots.Add(node); NodeRef("lookAt", node);
                var samples = new RotationSamples();
                var basis = eye == actor.gaze ? Quaternion.identity : Quaternion.Inverse(actor.humanForward) * actor.bindPose[eye].WorldBasis;
                for (int f = 0; f <= gaze.Track.LastFrame; f++)
                    samples.Add(f, basis * gaze.Track.Sample(f).Rotation * Quaternion.Inverse(basis));
                Channel(node, "rotation", samples.Frames, samples.Values.ToArray());
            }
            if (animation.channels.Count == 0) throw new InvalidDataException("ヒューマノイドの対応表、または登録した表情が必要です。");
            ext.EndMap();
            gltf.extensions = new glTFExtensionExport().Add("VRMC_vrm_animation", ext.GetStoreBytes());
            gltf.scenes.Add(new gltfScene { nodes = roots.ToArray() });
            var bytes = data.ToGlbBytes();
            if (bytes.Length > MaxMotionBytes) throw new InvalidDataException("VRMA のサイズ上限を超えています。");
            WriteFile(path, bytes);
            status.text = $"VRMA 保存　{animation.channels.Count} チャンネル　{Path.GetFileName(path)}";
        }
    }
}
