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
            return new Asset(Guid.NewGuid().ToString("N"), Path.GetFileName(path), bytes, null);
        }

        static Vrm10Instance LoadVrm(byte[] bytes)
        {
            using var data = new GlbBinaryParser(bytes, "avatar").Parse();
            if (data.GLTF.buffers.Any(b => !Embedded(b.uri)) || data.GLTF.images.Any(i => !Embedded(i.uri)))
                throw new InvalidDataException("画像とバッファを内蔵した VRM を選択してください。");
            // Use the original skeleton as the editable rig. A generated control rig
            // would overwrite the local rotations applied by the timeline each frame.
            var vrm = Vrm10.LoadGltfDataAsync(data, canLoadVrm0X: true,
                controlRigGenerationOption: ControlRigGenerationOption.None, awaitCaller: new ImmediateCaller())
                .GetAwaiter().GetResult();
            if (vrm == null) throw new InvalidDataException("VRM モデルを読み込めません。");
            if (vrm.TryGetComponent<Animator>(out var animator)) animator.enabled = false;
            return vrm;
        }

        static bool Embedded(string uri) => string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.Ordinal);

        static Dictionary<HumanBodyBones, Transform> MapHumanBones(GameObject root, Vrm10Instance vrm, string meta)
        {
            var result = new Dictionary<HumanBodyBones, Transform>();
            var names = HumanoidPreview.BoneMap(meta);
            var nodes = root.GetComponentsInChildren<Transform>(true).ToLookup(t => t.name, StringComparer.OrdinalIgnoreCase);
            for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++)
            {
                if (vrm != null)
                {
                    if (vrm.TryGetBoneTransform(id, out var t)) result.Add(id, t);
                    continue;
                }
                string name = id.ToString();
                if (!names.TryGetValue(name, out var mapped)) names.TryGetValue(HumanTrait.BoneName[(int)id], out mapped);
                var matches = nodes[mapped ?? name];
                if (matches.Count() == 1) result.Add(id, matches.First());
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
