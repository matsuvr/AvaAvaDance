using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;
namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        private ShaderLibrary shaderLibrary;
        private string materialReport = "";
        private void SetupShaders()
        {
            shaderLibrary = new ShaderLibrary();
            var menu = GetComponent<UIDocument>().rootVisualElement.Q<VisualElement>("menu");
            menu.Add(new Button(async () =>
            {
                try
                {
                    var path = PackageFileDialog.Open("シェーダーパック / lilToon VPMインストーラーを選択", "avadshader;unitypackage");
                    if(string.IsNullOrEmpty(path)) return;
                    status.text = await LoadShaderFile(path);
                }
                catch(Exception ex) { status.text="シェーダー追加中止: " + ex.Message; }
            }) { text="シェーダー追加", tooltip="lilToon VPMインストーラー (.unitypackage) またはコンパイル済み .avadshader を選択" });
        }

        private string ResolveInstaller(UnityPackageReader.PackageAsset config)
        {
            var message = VpmInstaller.Resolve(config.Data, shaderLibrary.LoadedLilToon, Application.unityVersion);
            Debug.Log("AVA_VPM " + message);
            return message;
        }

        private async System.Threading.Tasks.Task<string> LoadShaderFile(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".unitypackage", StringComparison.OrdinalIgnoreCase))
                return shaderLibrary.Load(path) + " 個のシェーダーを追加。モデルを再読込すると適用されます。";
            var assets = await System.Threading.Tasks.Task.Run(() => UnityPackageReader.Read(path));
            var config = assets.FirstOrDefault(a => a.Path == VpmInstaller.ConfigPath);
            if (config == null) throw new InvalidDataException("lilToon VPMインストーラーがありません。アバターはモデル操作の「読込」から選択してください。");
            return ResolveInstaller(config);
        }

        private int ApplyPackageMaterials(GameObject candidate, List<UnityEngine.Object> resources, UnityPackageReader.PackageAsset model, List<UnityPackageReader.PackageAsset> assets, UnityPackageReader.PackageAsset prefab)
        {
            var byGuid = assets.ToDictionary(a => a.Guid, StringComparer.OrdinalIgnoreCase);
            var bindings = AvatarMaterialBindings.Read(model, prefab);
            var materialsByGuid = new Dictionary<string,UnityMaterialData>(StringComparer.OrdinalIgnoreCase);
            var materialsByName = new Dictionary<string,UnityMaterialData>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in assets.Where(a => a.HasAsset && a.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (entry.Data.Length > UnityMaterialData.MaxUtf8Bytes) throw new InvalidDataException("Material exceeds size limit.");
                    var data = UnityMaterialData.Parse(new UTF8Encoding(false, true).GetString(entry.Data));
                    materialsByGuid.Add(entry.Guid, data);
                    var aliases = new[] { Path.GetFileNameWithoutExtension(entry.Path), data.Name };
                    foreach (string alias in aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                        if (!string.IsNullOrEmpty(alias)) materialsByName[alias] = materialsByName.ContainsKey(alias) ? null : data;
                }
                catch (Exception ex) { Debug.LogWarning("Material skipped " + entry.Path + ": " + ex.Message); }
            }

            int restored = 0, unsupported = 0;
            using var textures = new PreviewTextures(byGuid, resources);
            foreach (var renderer in candidate.GetComponentsInChildren<Renderer>())
            {
                bindings.Renderers.TryGetValue(renderer.name, out var slots);
                if (slots != null && !slots.Visible) { renderer.enabled = false; continue; }
                var materials = renderer.sharedMaterials;
                if (slots != null && materials.Length > 0 && slots.Materials.Length > materials.Length)
                {
                    int previous = materials.Length;
                    Array.Resize(ref materials, slots.Materials.Length);
                    for (int i = previous; i < materials.Length; i++)
                    {
                        materials[i] = new Material(materials[previous-1]);
                        resources.Add(materials[i]);
                    }
                    renderer.sharedMaterials = materials;
                }

                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    UnityMaterialData data = null;
                    if (slots != null && i < slots.Materials.Length)
                        materialsByGuid.TryGetValue(slots.Materials[i], out data);
                    else if (bindings.Remaps.TryGetValue(material.name, out var guid))
                    {
                        if (guid != null) materialsByGuid.TryGetValue(guid, out data);
                    }
                    else materialsByName.TryGetValue(material.name, out data);
                    if (data == null) { unsupported++; continue; }

                    var shader = shaderLibrary.Resolve(data.ShaderGuid);
                    if (shader != null && shader.isSupported) { material.shader = shader; restored++; }
                    else { unsupported++; Debug.LogWarning("Unsupported shader GUID " + data.ShaderGuid + " for " + material.name); }
                    ApplyProperties(material, data, textures);
                }
            }
            materialReport = "shader適用 " + restored + " / 未対応・材質不明 " + unsupported;
            Debug.Log("AVA_MATERIAL " + materialReport + " / textures=" + textures.Count);
            return textures.Count;
        }

        private static void ApplyProperties(Material material, UnityMaterialData data, PreviewTextures textures)
        {
            material.shaderKeywords = Array.Empty<string>();
            foreach (var name in data.Keywords)
                if (material.shader.keywordSpace.FindKeyword(name).isValid) material.EnableKeyword(name);
            foreach (var item in data.Floats)
                if (material.HasProperty(item.Key)) material.SetFloat(item.Key, item.Value);
            foreach (var item in data.Ints)
                if (material.HasProperty(item.Key)) material.SetInt(item.Key, item.Value);
            foreach (var item in data.Colors)
                if (material.HasProperty(item.Key)) material.SetColor(item.Key, new Color(item.Value.R, item.Value.G, item.Value.B, item.Value.A));
            if (data.CustomRenderQueue >= 0 && data.CustomRenderQueue <= 5000) material.renderQueue = data.CustomRenderQueue;
            foreach (var item in data.Textures)
            {
                if (!material.HasProperty(item.Key) || item.Value.Guid == null) continue;
                bool linear = item.Key.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.Key.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0;
                var texture = textures.Get(item.Value.Guid, linear);
                if (texture == null) continue;
                material.SetTexture(item.Key, texture);
                material.SetTextureScale(item.Key, new Vector2(item.Value.Scale.X, item.Value.Scale.Y));
                material.SetTextureOffset(item.Key, new Vector2(item.Value.Offset.X, item.Value.Offset.Y));
            }
        }

        private sealed class PreviewTextures : IDisposable
        {
            private readonly Dictionary<string,UnityPackageReader.PackageAsset> assets;
            private readonly List<UnityEngine.Object> owned;
            private readonly Dictionary<(string guid, bool linear), Texture2D> cache = new();
            private Texture2D linearScratch, colorScratch;
            private long remaining = 128L * 1024 * 1024;
            internal int Count => cache.Count;

            internal PreviewTextures(Dictionary<string,UnityPackageReader.PackageAsset> assets, List<UnityEngine.Object> owned)
            { this.assets = assets; this.owned = owned; }

            internal Texture2D Get(string guid, bool linear)
            {
                if (cache.TryGetValue((guid, linear), out var texture)) return texture;
                if (Count >= 32 || !assets.TryGetValue(guid, out var entry) || !entry.HasAsset) return null;
                byte[] bytes = entry.Data;
                if (bytes.Length < 33 || bytes.Length > 32*1024*1024 || bytes[0] != 137 || bytes[1] != 80 ||
                    bytes[2] != 78 || bytes[3] != 71 || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 ||
                    bytes[7] != 10 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R') return null;
                int w = PngDimension(bytes, 16), h = PngDimension(bytes, 20);
                if (w < 1 || h < 1 || w > 4096 || h > 4096) return null;
                int width = w, height = h;
                while (Math.Max(width, height) > 1024)
                { width = Math.Max(1, width/2); height = Math.Max(1, height/2); }
                long size = (long)width * height * 4;
                if (size > remaining) return null;

                if (width == w && height == h)
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
                    if (!ImageConversion.LoadImage(texture, bytes, true)) { Destroy(texture); return null; }
                }
                else
                {
                    ref var scratch = ref (linear ? ref linearScratch : ref colorScratch);
                    if (scratch == null) scratch = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
                    if (!ImageConversion.LoadImage(scratch, bytes, false)) return null;
                    texture = Downsample(scratch, width, height, linear);
                }
                remaining -= size;
                cache.Add((guid, linear), texture);
                owned.Add(texture);
                return texture;
            }

            private static int PngDimension(byte[] bytes, int offset) =>
                (bytes[offset]<<24) | (bytes[offset+1]<<16) | (bytes[offset+2]<<8) | bytes[offset+3];

            private static Texture2D Downsample(Texture2D full, int width, int height, bool linear)
            {
                var previous = RenderTexture.active;
                var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                try
                {
                    Graphics.Blit(full, target);
                    RenderTexture.active = target;
                    var preview = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
                    preview.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    preview.Apply(false, true);
                    return preview;
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
            }

            public void Dispose()
            {
                if (colorScratch != null) Destroy(colorScratch);
                if (linearScratch != null) Destroy(linearScratch);
            }
        }
    }
}
