using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvaAvaDance.Import;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static bool IsVrm(string path) => path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase);

        static Asset ReadVrmFile(string path)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length == 0 || stream.Length > UnityPackageReader.MaxFileBytes)
                throw new InvalidDataException("VRM のサイズが範囲外です。");
            var bytes = new byte[(int)stream.Length];
            ReadFully(stream, bytes, bytes.Length);
            return new Asset(Guid.NewGuid().ToString("N"), Path.GetFileName(path), bytes, null){ContainerPath=Path.GetFullPath(path)};
        }

        Vrm10Instance LoadVrm(byte[] bytes,List<MissingMaterialShader> missing)
        {
            // Coordinate conversion during import can modify the GLB buffer in place.
            using var data = new GlbBinaryParser((byte[])bytes.Clone(), "avatar").Parse();
            if (data.GLTF.buffers.Any(b => !Embedded(b.uri)) || data.GLTF.images.Any(i => !Embedded(i.uri)))
                throw new InvalidDataException("画像とバッファを内蔵した VRM を選択してください。");
            // Use the original skeleton as the editable rig. A generated control rig
            // would overwrite the local rotations applied by the timeline each frame.
            var vrm = Vrm10.LoadGltfDataAsync(data, canLoadVrm0X: true,
                controlRigGenerationOption: ControlRigGenerationOption.None, awaitCaller: new ImmediateCaller(),
                materialGenerator: new VrmMaterialGenerator(data,shaderLibrary,missing))
                .GetAwaiter().GetResult();
            if (vrm == null) throw new InvalidDataException("VRM モデルを読み込めません。");
            if(missing.Count>0)
            {
                var used=vrm.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).ToHashSet();
                missing.RemoveAll(request=>!used.Contains(request.Material));
            }
            if (vrm.TryGetComponent<Animator>(out var animator)) animator.enabled = false;
            return vrm;
        }

        static bool Embedded(string uri) => string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.Ordinal);

        static Dictionary<HumanBodyBones, Transform> MapHumanBones(GameObject root, Vrm10Instance vrm, string meta)
        {
            var result = new Dictionary<HumanBodyBones, Transform>();
            if (vrm == null)
            {
                var nodes = root.GetComponentsInChildren<Transform>(true);
                return HumanoidSkeletonMotion.Map(nodes.Select(t=>t.name).ToArray(),HumanoidPreview.BoneMap(meta))
                    .ToDictionary(k=>k.Key,k=>nodes[k.Value]);
            }
            for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++)
            {
                if (vrm.TryGetBoneTransform(id, out var t)) result.Add(id, t);
            }
            return result;
        }

        static Quaternion HumanoidFacing(Dictionary<HumanBodyBones, Transform> bones, Vector3 forward)
        {
            if (bones.TryGetValue(HumanBodyBones.RightUpperArm, out var right) &&
                bones.TryGetValue(HumanBodyBones.LeftUpperArm, out var left))
            {
                var facing = Vector3.Cross(right.position - left.position, Vector3.up);
                if (facing.sqrMagnitude > 1e-10f) forward = facing.normalized;
            }
            return Quaternion.LookRotation(forward, Vector3.up);
        }
    }
}
