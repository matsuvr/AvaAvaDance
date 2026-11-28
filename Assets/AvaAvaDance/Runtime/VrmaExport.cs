using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            if (!string.Equals(Path.GetExtension(path), ".vrma", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("VRMA の保存には .vrma を指定してください。");
            var data = BuildAnimationGltf(Path.GetFileNameWithoutExtension(path), true, true);
            var bytes = data.ToGlbBytes();
            if (bytes.Length > MaxMotionBytes) throw new InvalidDataException("VRMA のサイズ上限を超えています。");
            WriteFile(path, bytes);
            status.text = $"VRMA 保存　{data.Gltf.animations[0].channels.Count} チャンネル　{Path.GetFileName(path)}";
        }

        static void AnimationChannel<T>(ExportingGltfData data, glTFAnimation animation, int node, string property,
            IList<int> frames, T[] samples) where T : struct
        {
            var times = frames.Select(f => f / 30f).ToArray();
            int input = data.ExtendBufferAndGetAccessorIndex(times);
            data.Gltf.accessors[input].min = new[] { times[0] };
            data.Gltf.accessors[input].max = new[] { times[times.Length - 1] };
            var sampler = new glTFAnimationSampler
            {
                input = input, output = data.ExtendBufferAndGetAccessorIndex(samples), interpolation = "LINEAR"
            };
            animation.channels.Add(new glTFAnimationChannel
            {
                sampler = animation.samplers.Count, target = new glTFAnimationTarget { node = node, path = property }
            });
            animation.samplers.Add(sampler);
        }

        ExportingGltfData BuildAnimationGltf(string name, bool model, bool requireVrma = false) =>
            BuildAnimationGltfAsync(name, model ? actor : null, requireVrma, new ImmediateCaller(), default).GetAwaiter().GetResult();

        async Task<ExportingGltfData> BuildAnimationGltfAsync(string name, Actor owner, bool requireVrma,
            IAwaitCaller caller, CancellationToken cancellation)
        {
            var data = new ExportingGltfData(4096);
            var gltf = data.Gltf;
            gltf.asset.version = "2.0"; gltf.asset.generator = "AvaAvaDance";
            var animation = new glTFAnimation { name = name };
            var roots = new List<int>();
            if (owner == null) await AppendSceneCamera(data, animation, roots, caller, cancellation);
            else if (!await AppendVrma(data, animation, roots, requireVrma, owner, caller, cancellation) && requireVrma)
                throw new InvalidDataException("ヒューマノイドの対応表、または登録した表情が必要です。");
            if (animation.channels.Count == 0) return null;
            gltf.animations.Add(animation);
            gltf.scenes.Add(new gltfScene { nodes = roots.ToArray() });
            return data;
        }

        async Task AppendSceneCamera(ExportingGltfData data, glTFAnimation animation, List<int> roots, IAwaitCaller caller, CancellationToken cancellation)
        {
            var initial = cameraTrack.Sample(0);
            var gltf = data.Gltf;
            var camera = new glTFCamera { name = cameraTrack.Name, type = initial.Perspective ? ProjectionType.Perspective : ProjectionType.Orthographic };
            float yfov = initial.ViewAngle * Mathf.Deg2Rad;
            if (initial.Perspective)
                camera.perspective = new glTFPerspective { yfov = yfov, znear = .01f, zfar = 100 };
            else
            {
                float ymag = Mathf.Max(.01f, Mathf.Abs(initial.Distance * SceneUnit) * Mathf.Tan(yfov * .5f));
                camera.orthographic = new glTFOrthographic { xmag = ymag * previewCamera.aspect, ymag = ymag, znear = .01f, zfar = 100 };
            }
            gltf.cameras.Add(camera);
            roots.Add(gltf.nodes.Count);
            gltf.nodes.Add(new glTFNode { name = cameraTrack.Name, camera = 0 });
            int count = cameraTrack.LastFrame + 1;
            var positions = new Vector3[count];
            var rotations = new RotationSamples();
            var basis = Quaternion.LookRotation(-front, Vector3.up);
            var opticalBasis = Quaternion.Euler(0, 180, 0);
            for (int f = 0; f < count; f++)
            {
                if ((f & 255) == 0) {
                    await caller.NextFrameIfTimedOut();
                    cancellation.ThrowIfCancellationRequested();
                }
                var pose = cameraTrack.Sample(f);
                var rotation = basis * Quaternion.Euler(pose.Angles);
                var center = (pose.Center - CameraPose.Initial.Center) * SceneUnit;
                var position = center - rotation * Vector3.forward * (pose.Distance * SceneUnit);
                positions[f] = new Vector3(-position.x, position.y, position.z);
                rotations.Add(f, rotation * opticalBasis);
            }
            await caller.Run(() => {
                AnimationChannel(data, animation, roots[0], "translation", Enumerable.Range(0, count).ToArray(), positions);
                AnimationChannel(data, animation, roots[0], "rotation", rotations.Frames, rotations.Values.ToArray());
            });
        }

        async Task<bool> AppendVrma(ExportingGltfData data, glTFAnimation animation, List<int> roots, bool requireVrma,
            Actor owner, IAwaitCaller caller, CancellationToken cancellation)
        {
            var gltf = data.Gltf;
            int Node(string name, Vector3 position)
            {
                int index = gltf.nodes.Count;
                gltf.nodes.Add(new glTFNode { name = name, translation = new[] { -position.x, position.y, position.z } });
                return index;
            }
            var ext = new JsonFormatter();
            ext.BeginMap(); ext.Key("specVersion"); ext.Value("1.0");
            void NodeRef(string name, int i) { ext.Key(name); ext.BeginMap(); ext.Key("node"); ext.Value(i); ext.EndMap(); }

            var human = owner.humanBones.Where(h => h.Key != HumanBodyBones.LeftEye && h.Key != HumanBodyBones.RightEye).ToArray();
            bool hasRig = Enumerable.Range(0, (int)HumanBodyBones.LastBone)
                .Where(HumanTrait.RequiredBone).All(i => owner.humanBones.ContainsKey((HumanBodyBones)i));
            int end = owner.modelTracks.Max(t => t.LastFrame);
            if (hasRig && (long)(end + 1) * human.Length > 3_000_000)
            {
                if (requireVrma) throw new InvalidDataException("書き出すモーションが長すぎます。");
                return false;
            }
            if (hasRig)
            {
                var facing = Quaternion.Inverse(owner.humanForward);
                var root = owner.avatar.transform;
                float unit = owner.vrm != null ? 1f/root.lossyScale.y : 1f/owner.hipsHeight;
                var indices = human.Select((h, i) => (h.Value, i)).ToDictionary(k => k.Value, k => k.i);
                var parents = new int[human.Length];
                var rotations = human.Select(_ => new RotationSamples()).ToArray();
                var inverseBind = human.Select(h => Quaternion.Inverse(owner.bindPose[h.Value].WorldBasis) * owner.humanForward).ToArray();
                var deltas = new Quaternion[human.Length];
                var hip = owner.humanBones[HumanBodyBones.Hips];
                int count = end+1;
                var positions = new Vector3[count];
                ext.Key("humanoid"); ext.BeginMap(); ext.Key("humanBones"); ext.BeginMap();
                for (int i = 0; i < human.Length; i++)
                {
                    var (id, joint) = human[i];
                    var parent = joint.parent;
                    while (parent != null && !indices.ContainsKey(parent)) parent = parent.parent;
                    parents[i] = parent == null ? -1 : indices[parent];
                    var rest = owner.bindPositions[joint] - (parent == null ? Vector3.zero : owner.bindPositions[parent]);
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
                var original = owner.bones.Select(b => (b, b.Track.Current, b.Joint.localRotation, b.Joint.localPosition)).ToArray();
                var switches = owner.modelSwitch.Current;
                bool visible = owner.avatar.activeSelf;
                int f = 0;
                while (f < count)
                {
                    var previous = actor;
                    Task nextFrame = Task.CompletedTask;
                    try
                    {
                        actor = owner;
                        do
                        {
                            foreach (var b in owner.bones) b.Track.Seek(f);
                            owner.modelSwitch.Seek(f);
                            SolveIK();
                            for (int i = 0; i < human.Length; i++) deltas[i] = facing * HumanoidBindRotation.RotationBasis(human[i].Value) * inverseBind[i];
                            for (int i = 0; i < human.Length; i++)
                                rotations[i].Add(f, parents[i] < 0 ? deltas[i] : Quaternion.Inverse(deltas[parents[i]]) * deltas[i]);
                            var p = facing * (hip.position - root.position) * unit;
                            positions[f] = new Vector3(-p.x, p.y, p.z);
                            f++;
                            nextFrame = caller.NextFrameIfTimedOut();
                        } while (f < count && nextFrame.IsCompleted);
                    }
                    finally
                    {
                        foreach (var (b, pose, rotation, position) in original)
                        {
                            b.Track.Preview(pose); b.Joint.localRotation = rotation; b.Joint.localPosition = position;
                        }
                        owner.modelSwitch.Preview(switches); owner.avatar.SetActive(visible);
                        actor = previous;
                    }
                    await nextFrame;
                    cancellation.ThrowIfCancellationRequested();
                }
                int hipNode = indices[hip];
                await caller.Run(() => {
                    AnimationChannel(data, animation, hipNode, "translation", Enumerable.Range(0, count).ToArray(), positions);
                    for (int i = 0; i < rotations.Length; i++)
                        AnimationChannel(data, animation, i, "rotation", rotations[i].Frames, rotations[i].Values.ToArray());
                });
            }

            var allFaces = owner.modelTracks.OfType<MotionTrack<float>>().ToList();
            var faceNames = allFaces.ToDictionary(t => t, MorphName);
            if (owner.vrm == null)
                foreach (ExpressionPreset preset in Enum.GetValues(typeof(ExpressionPreset)))
                {
                    if (preset == ExpressionPreset.custom) continue;
                    var mapped = FindVrmaExpression(allFaces, preset.ToString());
                    if (mapped != null) faceNames[mapped] = preset.ToString();
                }
            var faces = allFaces.Where(t => t.Frames.Count > 1 || (float)t.KeyAt(0) != 0)
                .Where(t => faceNames[t] is not ("lookLeft" or "lookRight" or "lookUp" or "lookDown"))
                .GroupBy(t => faceNames[t], StringComparer.Ordinal).Select(g => g.First()).ToArray();
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
                        int node = Node("expression_" + name, Vector3.zero); roots.Add(node); NodeRef(name, node);
                        var times = new List<int>();
                        var samples = new List<Vector3>();
                        foreach (int f in track.OutputFrames()) {
                            if ((times.Count & 255) == 0) {
                                await caller.NextFrameIfTimedOut();
                                cancellation.ThrowIfCancellationRequested();
                            }
                            times.Add(f);
                            samples.Add(new Vector3(Mathf.Clamp01(track.Sample(f)), 0, 0));
                        }
                        if (times.Count < 1024) AnimationChannel(data, animation, node, "translation", times, samples.ToArray());
                        else await caller.Run(() => AnimationChannel(data, animation, node, "translation", times, samples.ToArray()));
                    }
                    ext.EndMap();
                }
                ext.EndMap();
            }
            var eye = owner.gaze;
            if (eye == null && !owner.humanBones.TryGetValue(HumanBodyBones.LeftEye, out eye))
                owner.humanBones.TryGetValue(HumanBodyBones.RightEye, out eye);
            var gaze = owner.bones.Find(b => b.Joint == eye);
            if (gaze != null && (gaze.Track.LastFrame > 0 || Quaternion.Angle(gaze.Track.Sample(0).Rotation, Quaternion.identity) > .001f))
            {
                int node = Node("lookAt", Vector3.zero); roots.Add(node); NodeRef("lookAt", node);
                var samples = new RotationSamples();
                var basis = eye == owner.gaze ? Quaternion.identity : Quaternion.Inverse(owner.humanForward) * owner.bindPose[eye].WorldBasis;
                for (int f = 0; f <= gaze.Track.LastFrame; f++) {
                    if ((f & 255) == 0) {
                        await caller.NextFrameIfTimedOut();
                        cancellation.ThrowIfCancellationRequested();
                    }
                    samples.Add(f, basis * gaze.Track.Sample(f).Rotation * Quaternion.Inverse(basis));
                }
                await caller.Run(() => AnimationChannel(data, animation, node, "rotation", samples.Frames, samples.Values.ToArray()));
            }
            if (animation.channels.Count == 0) return false;
            ext.EndMap();
            gltf.extensions = new glTFExtensionExport().Add(AvaGlb.VrmaExtension, ext.GetStoreBytes());
            return true;
        }
    }
}
