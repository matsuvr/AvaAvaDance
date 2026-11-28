using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniGLTF;
using UniJSON;
using UnityEngine;

namespace AvaAvaDance.Import
{
    // Read the semantic channels directly: a VRMA can contain just a face or gaze
    // and need not have the complete skeleton required by Unity's humanoid Animator.
    internal sealed class VrmaClip
    {
        internal sealed class Node
        {
            internal Node Parent;
            internal Matrix4x4 Local, World;
            internal Curve Rotation, Translation;

            internal Quaternion NormalizedRotation(float seconds)
            {
                var rest = World.rotation;
                return rest * Quaternion.Inverse(Local.rotation) * Rotation.SampleRotation(seconds) * Quaternion.Inverse(rest);
            }
        }

        internal sealed class Curve
        {
            internal readonly float[] Times;
            readonly Vector4[] values;
            readonly AnimationImporterUtil.TangentMode mode;
            readonly bool rotation;

            internal Curve(GltfData data, glTFAnimationSampler sampler, bool rotation, ref int budget)
            {
                this.rotation = rotation;
                mode = AnimationImporterUtil.GetTangentMode(sampler.interpolation);
                var input = data.GLTF.accessors[sampler.input];
                var output = data.GLTF.accessors[sampler.output];
                bool cubic = mode == AnimationImporterUtil.TangentMode.Cubicspline;
                if (input.type != "SCALAR" || output.type != (rotation ? "VEC4" : "VEC3") ||
                    input.componentType != glComponentType.FLOAT || output.componentType != glComponentType.FLOAT ||
                    input.count <= 0 || output.count != (long)input.count * (cubic ? 3 : 1) || output.count > budget)
                    throw new InvalidDataException("VRMA のサンプル数または型が不正です。");
                budget -= output.count;
                CheckAccessor(data, input);
                CheckAccessor(data, output);
                Times = data.GetArrayFromAccessor<float>(sampler.input).ToArray();
                for (int i = 0; i < Times.Length; i++)
                    if (!float.IsFinite(Times[i]) || Times[i] < 0 || (i > 0 && Times[i] <= Times[i-1]))
                        throw new InvalidDataException("VRMA のキー時刻が不正です。");
                values = rotation ? data.GetArrayFromAccessor<Vector4>(sampler.output).ToArray() :
                    data.GetArrayFromAccessor<Vector3>(sampler.output).ToArray().Select(v => (Vector4)v).ToArray();
                if (values.Any(v => !float.IsFinite(v.sqrMagnitude)))
                    throw new InvalidDataException("VRMA のキーに非有限値があります。");
            }

            static Quaternion AsRotation(Vector4 q)
            {
                if (q.sqrMagnitude < 1e-10f || !float.IsFinite(q.sqrMagnitude))
                    throw new InvalidDataException("VRMA の回転が不正です。");
                // VRM 1.0 and VRMA both mirror glTF's X axis in Unity.
                return new Quaternion(q.x, -q.y, -q.z, q.w).normalized;
            }

            internal Quaternion SampleRotation(float seconds) => AsRotation(Sample(seconds));

            internal Vector4 Sample(float seconds)
            {
                int i = Array.BinarySearch(Times, seconds);
                bool cubic = mode == AnimationImporterUtil.TangentMode.Cubicspline;
                Vector4 Value(int key) => values[cubic ? 3 * key + 1 : key];
                if (i >= 0) return Value(i);
                int next = ~i;
                if (next == 0) return Value(0);
                i = next - 1;
                if (next == Times.Length || mode == AnimationImporterUtil.TangentMode.Constant) return Value(i);
                float dt = Times[next] - Times[i], t = (seconds - Times[i]) / dt;
                Vector4 a = Value(i), b = Value(next);
                if (cubic)
                {
                    float t2 = t*t, t3 = t2*t;
                    return (2*t3-3*t2+1)*a + (t3-2*t2+t)*dt*values[3*i+2] +
                           (3*t2-2*t3)*b + (t3-t2)*dt*values[3*next];
                }
                if (!rotation) return Vector4.LerpUnclamped(a, b, t);
                var q = Quaternion.Slerp(AsRotation(a), AsRotation(b), t);
                return new Vector4(q.x, -q.y, -q.z, q.w);
            }

            internal void AddFrames(SortedSet<int> frames, bool coupled)
            {
                for (int i = 0; i < Times.Length; i++)
                {
                    float at = Times[i] * 30;
                    int lo = Mathf.FloorToInt(at), hi = Mathf.CeilToInt(at);
                    frames.Add(lo); frames.Add(hi);
                    if (mode == AnimationImporterUtil.TangentMode.Constant && hi > 0) frames.Add(hi-1);
                    if (i > 0 && (coupled || mode == AnimationImporterUtil.TangentMode.Cubicspline))
                        for (int f = Mathf.CeilToInt(Times[i-1]*30); f < hi; f++) frames.Add(f);
                }
            }
        }

        internal readonly Dictionary<HumanBodyBones, Node> Bones = new();
        internal readonly Dictionary<string, Node> Expressions = new();
        internal readonly Node Gaze;
        internal readonly int LastFrame;

        // NativeArray bounds checks are disabled in release players. Validate the
        // view, stride and sparse indices before UniGLTF reads an external accessor.
        static void CheckAccessor(GltfData data, glTFAccessor a)
        {
            int width = a.TypeCount * sizeof(float);
            int Range(int index, int offset, int count, int size)
            {
                var view = data.GLTF.bufferViews[index];
                int stride = view.byteStride == 0 ? size : view.byteStride;
                if (view.buffer != 0 || view.byteOffset < 0 || view.byteLength < 0 || offset < 0 || stride < size ||
                    (long)view.byteOffset + view.byteLength > data.Bin.Length ||
                    offset + (count-1L)*stride + size > view.byteLength)
                    throw new InvalidDataException("VRMA のバッファ範囲が不正です。");
                return view.byteOffset + offset;
            }
            if (a.bufferView.HasValue) Range(a.bufferView.Value, a.byteOffset ?? 0, a.count, width);
            var sparse = a.sparse;
            if (sparse == null || sparse.count == 0) return;
            int size = sparse.indices.componentType switch {
                glComponentType.UNSIGNED_BYTE => 1, glComponentType.UNSIGNED_SHORT => 2,
                glComponentType.UNSIGNED_INT => 4, _ => 0
            };
            if (size == 0 || sparse.count < 0 || sparse.count > a.count ||
                data.GLTF.bufferViews[sparse.indices.bufferView].byteStride != 0 ||
                data.GLTF.bufferViews[sparse.values.bufferView].byteStride != 0)
                throw new InvalidDataException("VRMA のスパース配列が不正です。");
            int at = Range(sparse.indices.bufferView, sparse.indices.byteOffset, sparse.count, size);
            Range(sparse.values.bufferView, sparse.values.byteOffset, sparse.count, width);
            long previous = -1;
            for (int k = 0; k < sparse.count; k++, at += size)
            {
                uint index = 0;
                for (int b = 0; b < size; b++) index |= (uint)data.Bin[at+b] << (8*b);
                if (index <= previous || index >= a.count) throw new InvalidDataException("VRMA のスパース索引が不正です。");
                previous = index;
            }
        }

        internal VrmaClip(GltfData data, int maxFrame)
        {
            var gltf = data.GLTF;
            if (gltf.animations.Count == 0 || gltf.nodes.Count > 8192 || gltf.buffers.Count != 1 || !string.IsNullOrEmpty(gltf.buffers[0].uri))
                throw new InvalidDataException("アニメーションを内蔵した VRMA を選択してください。");
            var extension = (gltf.extensions as glTFExtensionImport)?.ObjectItems()
                .Where(k => k.Key.GetString() == "VRMC_vrm_animation").Select(k => (JsonNode?)k.Value).FirstOrDefault();
            if (!extension.HasValue || extension.Value.GetObjectValueOrDefault("specVersion", "") != "1.0")
                throw new InvalidDataException("VRM Animation 1.0 形式を選択してください。");
            var nodes = gltf.nodes.Select(n => new Node { Local = RestTransform(n) }).ToArray();
            foreach (var (source, i) in gltf.nodes.Select((n, i) => (n, i)))
                foreach (int child in source.children ?? Array.Empty<int>())
                {
                    if ((uint)child >= nodes.Length || nodes[child].Parent != null)
                        throw new InvalidDataException("VRMA のノード階層が不正です。");
                    nodes[child].Parent = nodes[i];
                }
            // Resolve parents first, including intermediate nodes outside the human bone map.
            var visited = new HashSet<Node>();
            foreach (var node in nodes)
            {
                var path = new Stack<Node>();
                for (var n = node; n != null && !visited.Contains(n); n = n.Parent)
                {
                    if (path.Count >= nodes.Length) throw new InvalidDataException("VRMA のノード階層が循環しています。");
                    path.Push(n);
                }
                while (path.Count > 0)
                {
                    var n = path.Pop();
                    n.World = (n.Parent?.World ?? Matrix4x4.identity) * n.Local;
                    visited.Add(n);
                }
            }
            Node Mapped(JsonNode def) => nodes[def["node"].GetInt32()];
            var ext = extension.Value;
            if (ext.TryGet("humanoid", out var humanoid))
                foreach (var entry in humanoid["humanBones"].ObjectItems())
                {
                    string name = entry.Key.GetString();
                    if (name.Contains("Thumb")) name = name.Replace("Proximal","Intermediate").Replace("Metacarpal","Proximal");
                    if (Enum.TryParse<HumanBodyBones>(name, true, out var id) && id >= 0 && id < HumanBodyBones.LastBone)
                        Bones.Add(id, Mapped(entry.Value));
                }
            if (ext.TryGet("expressions", out var face))
                foreach (var group in new[] { "preset", "custom" })
                    if (face.TryGet(group, out var entries))
                        foreach (var entry in entries.ObjectItems()) Expressions.Add(entry.Key.GetString(), Mapped(entry.Value));
            if (ext.TryGet("lookAt", out var gaze) && gaze.ContainsKey("node")) Gaze = Mapped(gaze);

            int budget = 2_000_000;
            var animation = gltf.animations[0];
            var curves = new Dictionary<(int, bool), Curve>();
            foreach (var channel in animation.channels)
            {
                string path = channel.target.path;
                if (path != "rotation" && path != "translation") continue;
                bool rotation = path == "rotation";
                var node = nodes[channel.target.node];
                if ((rotation ? node.Rotation : node.Translation) != null) throw new InvalidDataException("VRMA のチャンネルが重複しています。");
                if (!curves.TryGetValue((channel.sampler, rotation), out var curve))
                {
                    curve = new Curve(data, animation.samplers[channel.sampler], rotation, ref budget);
                    curves.Add((channel.sampler, rotation), curve);
                }
                if (rotation) node.Rotation = curve; else node.Translation = curve;
                float end = curve.Times[curve.Times.Length-1] * 30;
                if (end > maxFrame) throw new InvalidDataException("VRMA が編集可能なフレーム数を超えています。");
                LastFrame = Mathf.Max(LastFrame, Mathf.CeilToInt(end));
            }
        }

        internal static Matrix4x4 RestTransform(glTFNode n)
        {
            if (n.matrix?.Length == 16)
                return UniGLTF.UnityExtensions.MatrixFromArray(n.matrix).ReverseX();
            var p = n.translation;
            var r = n.rotation;
            var s = n.scale;
            return Matrix4x4.TRS(p?.Length == 3 ? new Vector3(-p[0], p[1], p[2]) : Vector3.zero,
                r?.Length == 4 ? new Quaternion(r[0], -r[1], -r[2], r[3]) : Quaternion.identity,
                s?.Length == 3 ? new Vector3(s[0], s[1], s[2]) : Vector3.one);
        }
    }
}
