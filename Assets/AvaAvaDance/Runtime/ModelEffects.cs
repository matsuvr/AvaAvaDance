using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Ava.Extensions;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.Rendering;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly Dictionary<string, IModelEffectFormat> modelEffectFormats = new();
        readonly Dictionary<Actor, IModelEffectInstance> modelEffectInstances = new();
        CommandBuffer modelEffectCommands;

        IDisposable RegisterModelEffect(string id, IModelEffectFormat format)
        {
            modelEffectFormats.Add(id, format);
            return new Registration(() => {
                modelEffectFormats.Remove(id);
                foreach (var owner in modelEffectInstances.Keys.Where(a => a.controller?.modelEffectProvider == id).ToArray())
                    ReleaseModelEffect(owner);
            });
        }

        IModelEffectFormat FindModelEffect(string path)
        {
            string suffix = Path.GetExtension(path).TrimStart('.');
            return modelEffectFormats.Values.FirstOrDefault(f => f.Extensions.Contains(suffix, StringComparer.OrdinalIgnoreCase));
        }

        void PickModelEffect(IModelEffectFormat format)
        {
            string path = PackageFileDialog.Open("エフェクトを選択", string.Join(";", format.Extensions));
            if (!string.IsNullOrEmpty(path)) ImportModelEffect(path);
        }

        void ImportModelEffect(string path)
        {
            var format = FindModelEffect(path) ?? throw new InvalidDataException("対応するエフェクト拡張がありません。");
            var source = ReadVrmFile(path, "エフェクト");
            var descriptor = Controller(format.ControllerDescription(path));
            descriptor.alias = source.Path;
            descriptor.modelEffectProvider = modelEffectFormats.First(pair => pair.Value == format).Key;
            var assets = new List<Asset> { source };
            string companion = format.CompanionModel(path);
            if (companion != null)
            {
                descriptor.markerAsset = "__controller/model.pmd";
                var marker = ReadVrmFile(companion, "制御モデル");
                assets.Add(new Asset(marker.Guid, descriptor.markerAsset, marker.Data, null));
                var directory = Path.GetDirectoryName(Path.GetFullPath(companion));
                foreach (var texture in EffectMarkerModel.Read(marker.Data).Textures)
                {
                    var bytes = ReadLocalAsset(Path.Combine(directory, texture), new[]{directory + Path.DirectorySeparatorChar});
                    assets.Add(new Asset(Guid.NewGuid().ToString("N"), "__controller/" + texture, bytes, null));
                }
            }
            var model = new Asset(Guid.NewGuid().ToString("N"), Path.ChangeExtension(source.Path, ".avaeffect"),
                Encoding.UTF8.GetBytes(JsonUtility.ToJson(descriptor)), null) { ContainerPath = source.ContainerPath };
            assets.Insert(0, model);
            long size = source.Data.Length;
            foreach (var relative in format.Dependencies(source.Data).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":"))
                    throw new InvalidDataException("エフェクトの参照先には相対パスを指定してください。");
                var file = new FileInfo(Path.Combine(Path.GetDirectoryName(source.ContainerPath), relative));
                if (!file.Exists) throw new FileNotFoundException("エフェクトの参照ファイルが見つかりません: " + relative);
                if (file.Length > UnityPackageReader.MaxFileBytes || (size += file.Length) > 512L * 1024 * 1024)
                    throw new InvalidDataException("エフェクトの素材データが大きすぎます。");
                assets.Add(new Asset(Guid.NewGuid().ToString("N"), relative, File.ReadAllBytes(file.FullName), null));
            }
            AttachModel(model, assets, null);
        }

        void ReleaseModelEffect(Actor owner)
        {
            if (!modelEffectInstances.TryGetValue(owner, out var instance)) return;
            modelEffectInstances.Remove(owner);
            instance?.Dispose();
        }

        static void BuildEffectMarker(Transform joint, float size)
        {
            var marker = new GameObject("Marker");
            marker.transform.SetParent(joint, false);
            var owned = marker.AddComponent<FbxOwnedResources>();
            var mesh = new Mesh { name = "エフェクト方向" };
            owned.Own(mesh);
            mesh.vertices = new[] {
                new Vector3(-.7f,-.7f,1)*size, new Vector3(-.7f,.7f,1)*size,
                new Vector3(.7f,.7f,1)*size, new Vector3(.7f,-.7f,1)*size, new Vector3(0,0,-1.5f)*size
            };
            mesh.triangles = new[] { 0,2,1, 0,3,2, 0,1,4, 1,2,4, 2,3,4, 3,0,4 };
            mesh.RecalculateNormals();
            marker.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("Standard")) { color = Color.gray };
            owned.Own(material);
            marker.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        void InitModelEffectRendering()
        {
            modelEffectCommands = new CommandBuffer { name = "モデルエフェクト" };
            previewCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, modelEffectCommands);
            Camera.onPreCull += PrepareModelEffects;
        }

        void DisposeModelEffectRendering()
        {
            Camera.onPreCull -= PrepareModelEffects;
            if (previewCamera != null && modelEffectCommands != null)
                previewCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, modelEffectCommands);
            modelEffectCommands?.Release();
            foreach (var owner in modelEffectInstances.Keys.ToArray()) ReleaseModelEffect(owner);
        }

        static float[] MatrixValues(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (int i = 0; i < values.Length; i++) values[i] = matrix[i];
            return values;
        }

        void PrepareModelEffects(Camera camera)
        {
            if (camera != previewCamera) return;
            modelEffectCommands.Clear();
            if (camera.targetTexture == null || modelEffectFormats.Count == 0) return;
            ModelEffectFrame sample = null;
            foreach (var owner in drawOrder)
            {
                var descriptor = owner.controller;
                if (descriptor?.modelEffectProvider == null || !owner.avatar.activeInHierarchy ||
                    !modelEffectFormats.TryGetValue(descriptor.modelEffectProvider, out var format)) continue;
                try
                {
                    if (!modelEffectInstances.TryGetValue(owner, out var instance))
                    {
                        var source = owner.modelAssets.First(a => a.Path == descriptor.alias);
                        instance = format.Open(source.Data, owner.modelAssets.Where(a => a != source && a.Guid != owner.modelGuid && !a.Path.StartsWith("__controller/",StringComparison.Ordinal))
                            .ToDictionary(a => a.Path, a => a.Data, StringComparer.OrdinalIgnoreCase));
                        modelEffectInstances.Add(owner, instance);
                    }
                    if (instance == null) continue;
                    if (sample == null)
                    {
                        sample = new ModelEffectFrame {
                            Timeline = evaluationTime, Exporting = exporting && outputDelta > 0,
                            PreviewDelta = exporting ? outputDelta * 60 : Time.unscaledDeltaTime * 60,
                            View = MatrixValues(camera.worldToCameraMatrix * EffectWorld.inverse),
                            Projection = MatrixValues(GL.GetGPUProjectionMatrix(camera.projectionMatrix, true)),
                            TargetTexture = camera.targetTexture.GetNativeTexturePtr()
                        };
                        modelEffectCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    }
                    sample.Bone = name => {
                        var bone = owner.bones.Find(b => b.Track.Name == name);
                        return MatrixValues(bone == null ? Matrix4x4.identity :
                            EffectWorld * bone.Joint.localToWorldMatrix * Matrix4x4.Scale(Vector3.one * SceneUnit));
                    };
                    sample.Morph = name => owner.modelTracks.OfType<MotionTrack<float>>()
                        .FirstOrDefault(t => MorphName(t) == name)?.Current ?? 0;
                    var eventId = instance.Prepare(sample, out var callback);
                    if (eventId != 0) modelEffectCommands.IssuePluginEvent(callback, eventId);
                }
                catch (Exception ex)
                {
                    ReleaseModelEffect(owner);
                    modelEffectInstances[owner] = null;
                    ReportActionError(owner.label + ": ", ex);
                }
            }
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [DllImport("AvaWindows", CharSet = CharSet.Unicode)]
        static extern int ava_editor_drop(StringBuilder path, int capacity);
#endif
        void PumpModelEffectDrops()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!physicalMainKeys || importing || exporting || physicsBaking || modal != null || importDialog != null) return;
            int size = ava_editor_drop(null, 0);
            while (size > 0)
            {
                var path = new StringBuilder(size);
                if (ava_editor_drop(path, size) == 0) break;
                try { if (FindModelEffect(path.ToString()) != null) ImportModelEffect(path.ToString()); }
                catch (Exception ex) { ReportActionError("エフェクト読込: ", ex); }
                size = ava_editor_drop(null, 0);
            }
#endif
        }
    }
}
