using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;
using UnityEngine;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // The PMD companion supplies the visible dummy, its control names and PMD morph panels.
        sealed class EffectMarkerModel
        {
            internal ControlBone[] Bones;
            internal ControlMorph[] Morphs;
            struct Surface { internal Color color; internal int count; internal string texture; }
            Surface[] surfaces;
            string[] boneNames;
            Vector3[] vertices, normals;
            Vector2[] uv;
            BoneWeight[] weights;
            int[] triangles;
            readonly Dictionary<string, (int index, Vector3 delta)[]> shapes = new();
            internal IEnumerable<string> Textures => surfaces.Select(s => s.texture).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

            internal static EffectMarkerModel Read(byte[] data)
            {
                using var input = new MemoryStream(data, false);
                using var r = new BinaryReader(input);
                if (Encoding.ASCII.GetString(r.ReadBytes(3)) != "Pmd" || r.ReadSingle() != 1)
                    throw new InvalidDataException("制御モデルの PMD ヘッダーが不正です。");
                string Name(int n)
                {
                    var bytes = r.ReadBytes(n);
                    if (bytes.Length != n) throw new EndOfStreamException();
                    return Ava.Extensions.Windows31J.Decode(bytes);
                }
                int Count(int stride, int limit)
                {
                    int n = r.ReadInt32();
                    if (n < 0 || n > limit || n > (input.Length - input.Position) / stride)
                        throw new InvalidDataException("制御モデルの要素数が不正です。");
                    return n;
                }
                Vector3 Vec() => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                input.Position = 283;
                var m = new EffectMarkerModel();
                int n = Count(38, 65536);
                m.vertices = new Vector3[n]; m.normals = new Vector3[n]; m.uv = new Vector2[n]; m.weights = new BoneWeight[n];
                for (int i = 0; i < n; i++)
                {
                    m.vertices[i] = Vec() * SceneUnit; m.normals[i] = Vec();
                    m.uv[i] = new Vector2(r.ReadSingle(), 1 - r.ReadSingle());
                    int a = r.ReadUInt16(), b = r.ReadUInt16(); float t = Mathf.Clamp01(r.ReadByte() / 100f);
                    m.weights[i] = new BoneWeight { boneIndex0 = t == 0 ? b : a, boneIndex1 = t == 1 ? a : b, weight0 = t, weight1 = 1 - t };
                    r.ReadByte();
                }
                m.triangles = new int[Count(2, 3000000)];
                for (int i = 0; i < m.triangles.Length; i++)
                {
                    int index = r.ReadUInt16();
                    if (index >= n) throw new InvalidDataException("制御モデルの頂点番号が不正です。");
                    m.triangles[i] = index;
                }
                m.surfaces = new Surface[Count(70, 65536)];
                int used = 0;
                for (int i = 0; i < m.surfaces.Length; i++)
                {
                    var color = new Color(r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
                    input.Position += 30;
                    int count = r.ReadInt32();
                    if (count < 0 || count % 3 != 0 || count > m.triangles.Length - used)
                        throw new InvalidDataException("制御モデルの材質範囲が不正です。");
                    used += count;
                    string texture = Name(20).Split('*')[0].Replace('\\','/');
                    if (texture.EndsWith(".sph",StringComparison.OrdinalIgnoreCase) || texture.EndsWith(".spa",StringComparison.OrdinalIgnoreCase)) texture = "";
                    m.surfaces[i] = new Surface { color = color, count = count, texture = texture };
                }
                if (used != m.triangles.Length) throw new InvalidDataException("制御モデルの面数が一致しません。");
                int joints = r.ReadUInt16();
                if (joints > 4096) throw new InvalidDataException("制御ボーンが多すぎます。");
                m.boneNames = new string[joints];
                var parent = new int[joints];
                var position = new Vector3[joints];
                var seen = new HashSet<string>();
                for (int i = 0; i < joints; i++)
                {
                    string label = Name(20);
                    if (label.Length == 0) label = "ボーン";
                    string name = label;
                    for (int suffix = 2; !seen.Add(name); suffix++) name = label + " (" + suffix + ")";
                    m.boneNames[i] = name;
                    parent[i] = r.ReadUInt16(); input.Position += 5;
                    position[i] = Vec() * SceneUnit;
                    if (parent[i] != ushort.MaxValue && parent[i] >= joints) throw new InvalidDataException("制御ボーンの親番号が不正です。");
                }
                if (m.weights.Any(v => v.boneIndex0 >= joints || v.boneIndex1 >= joints)) throw new InvalidDataException("制御モデルの頂点ウェイトが不正です。");
                var children = Enumerable.Range(0,joints).ToLookup(i => parent[i]);
                var queue = new Queue<int>(children[ushort.MaxValue]);
                var bones = new List<ControlBone>();
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue(), p = parent[i];
                    bones.Add(new ControlBone { name = m.boneNames[i], parent = p == ushort.MaxValue ? null : m.boneNames[p],
                        position = p == ushort.MaxValue ? position[i] : position[i] - position[p] });
                    foreach (int child in children[i]) queue.Enqueue(child);
                }
                if (bones.Count != joints) throw new InvalidDataException("制御ボーンの階層が循環しています。");
                m.Bones = bones.ToArray();
                int chains = r.ReadUInt16();
                for (int i=0;i<chains;i++) { input.Position += 4; int links=r.ReadByte(); input.Position += 6 + links*2; }
                int expressions = r.ReadUInt16();
                if (expressions > 4096) throw new InvalidDataException("制御モーフが多すぎます。");
                var morphs = new List<ControlMorph>();
                int[] indices = Array.Empty<int>();
                for (int i = 0; i < expressions; i++)
                {
                    string name = Name(20);
                    int count = Count(16, n);
                    int type = r.ReadByte();
                    if (type == 0) indices = new int[count];
                    var deltas = new (int index, Vector3 delta)[count];
                    for (int j = 0; j < count; j++)
                    {
                        int index = r.ReadInt32();
                        Vector3 delta = Vec() * SceneUnit;
                        if (index < 0 || index >= (type == 0 ? n : indices.Length)) throw new InvalidDataException("制御モーフの頂点番号が不正です。");
                        if (type == 0) indices[j] = index;
                        else deltas[j] = (indices[index], delta);
                    }
                    if (type == 0) continue;
                    morphs.Add(new ControlMorph { name = name, category = type == 1 ? 3 : type == 2 ? 1 : type == 3 ? 2 : 4 });
                    if (count > 0) m.shapes.Add(name,deltas);
                }
                m.Morphs = morphs.ToArray();
                return m;
            }

            internal void Attach(GameObject root, List<Transform> joints, List<Asset> assets)
            {
                if (vertices.Length == 0) return;
                var owner = root.AddComponent<FbxOwnedResources>();
                var mesh = new Mesh { name = "制御モデル", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                owner.Own(mesh);
                mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv;
                var jointMap = joints.ToDictionary(t => t.name);
                var ordered = boneNames.Select(name => jointMap[name]).ToArray();
                mesh.bindposes = ordered.Select(t => t.worldToLocalMatrix * root.transform.localToWorldMatrix).ToArray();
                mesh.boneWeights = weights;
                mesh.subMeshCount = surfaces.Length;
                var materials = new Material[surfaces.Length];
                var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
                int offset = 0;
                for (int i = 0; i < surfaces.Length; i++)
                {
                    var s = surfaces[i];
                    mesh.SetTriangles(triangles, offset, s.count, i); offset += s.count;
                    var mat = new Material(Shader.Find("Standard")) { color = s.color };
                    owner.Own(mat); materials[i] = mat;
                    if (s.texture.Length == 0) continue;
                    if (!textures.TryGetValue(s.texture,out var image))
                    {
                        var asset=assets.First(a => string.Equals(a.Path, "__controller/" + s.texture, StringComparison.OrdinalIgnoreCase));
                        image=AccessoryTexture(s.texture,asset.Data);
                        owner.Own(image); textures.Add(s.texture,image);
                    }
                    mat.mainTexture=image;
                }
                foreach (var shape in shapes)
                {
                    var deltas = new Vector3[vertices.Length];
                    foreach (var point in shape.Value) deltas[point.index] = point.delta;
                    mesh.AddBlendShapeFrame(shape.Key, 100, deltas, null, null);
                }
                mesh.RecalculateBounds();
                var skin = root.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh=mesh; skin.bones=ordered; skin.sharedMaterials=materials; skin.updateWhenOffscreen=true;
            }
        }
    }
}
