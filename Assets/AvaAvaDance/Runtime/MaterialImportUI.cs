using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.Rendering;
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
            var bar = GetComponent<UIDocument>().rootVisualElement.Q<VisualElement>("profile-bar");
            Box(bar,"shader-menu-space","menu-space");
            shaderAddButton = new Button(OpenShaderMenu) { name="shader-add", text="シェーダー追加", tooltip=ShaderButtonHint };
            bar.Add(shaderAddButton);
            foreach (var release in OfficialShaderCatalog.All)
            {
                try { if (OfficialShaderStore.HasConsent(release)) shaderLibrary.Load(OfficialShaderStore.ValidatePayload(release)); }
                catch (Exception ex) { Debug.LogWarning("保存済みシェーダーを復元できません: " + ex.Message); }
            }
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
            {
                int count = shaderLibrary.Load(path);
                int applied = ReapplyPackageShaders();
                return ShaderInstallResult(count + " 個のシェーダーを読み込みました。", applied);
            }
            var assets = await System.Threading.Tasks.Task.Run(() => UnityPackageReader.Read(path));
            var config = assets.FirstOrDefault(a => a.Path == VpmInstaller.ConfigPath);
            if (config != null)
            {
                if (shaderLibrary.LoadedLilToon == null)
                {
                    ShowShaderLicense(OfficialShaderCatalog.LilToon);
                    return "lilToon のライセンスを確認して導入してください。";
                }
                return ResolveInstaller(config);
            }
            string compiler = Resources.Load<TextAsset>("PackageShaderBuilder")?.text;
            string output = await PackageShaderCompiler.Compile(assets, compiler, Application.persistentDataPath, message => status.text = message);
            return await LoadShaderFile(output);
        }

        private int ApplyPackageMaterials(GameObject candidate, List<UnityEngine.Object> resources, UnityPackageReader.PackageAsset model, List<UnityPackageReader.PackageAsset> assets, UnityPackageReader.PackageAsset prefab, List<MissingMaterialShader> missing)
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
            var owner = candidate.GetComponent<FbxOwnedResources>();
            var textureCache = owner.PackageTextures;
            textureCache.Clear();
            missing.Clear();
            using var textures = new PreviewTextures(byGuid, resources, textureCache);
            foreach (var renderer in candidate.GetComponentsInChildren<Renderer>(true))
            {
                bindings.Renderers.TryGetValue(renderer.name, out var slots);
                if (slots != null)
                {
                    if (!slots.Visible) { renderer.enabled = false; continue; }
                    renderer.shadowCastingMode = (ShadowCastingMode)slots.CastShadows;
                    renderer.receiveShadows = slots.ReceiveShadows;
                }
                var materials = renderer.sharedMaterials;
                if (!owner.FbxMaterialNames.TryGetValue(renderer, out var names))
                    owner.FbxMaterialNames.Add(renderer, names = materials.Select(m => m?.name).ToArray());
                if (slots != null && slots.Materials.Length != materials.Length)
                {
                    int previous = materials.Length;
                    Array.Resize(ref materials, slots.Materials.Length);
                    for (int i = previous; i < materials.Length; i++)
                    {
                        materials[i] = new Material(Shader.Find("AvaAvaDance/Preview")) { name = renderer.name };
                        resources.Add(materials[i]);
                    }
                }

                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (slots != null && slots.Materials[i] == null) { materials[i] = null; continue; }
                    UnityMaterialData data = null;
                    if (slots != null && i < slots.Materials.Length)
                        materialsByGuid.TryGetValue(slots.Materials[i], out data);
                    else if (names[i] != null && bindings.Remaps.TryGetValue(names[i], out var guid))
                    {
                        if (guid != null) materialsByGuid.TryGetValue(guid, out data);
                    }
                    else if (names[i] != null) materialsByName.TryGetValue(names[i], out data);
                    if (data == null) { unsupported++; continue; }

                    var shader = shaderLibrary.ResolveMaterial(data.ShaderGuid,data.ShaderFileId,null);
                    if (shader != null)
                    {
                        // Switching the fallback shader in place retains its gray _Color and other
                        // overrides when the source .mat relies on a shader default.
                        if (material.shader != shader)
                        {
                            materials[i] = material = new Material(shader) { name = material.name };
                            resources.Add(material);
                        }
                        restored++;
                    }
                    else
                    {
                        unsupported++;
                        if (data.ShaderFileId != 0)
                        {
                            string identity = data.ShaderGuid ?? ("fileID " + data.ShaderFileId);
                            if (data.ShaderGuid != null && byGuid.TryGetValue(data.ShaderGuid,out var shaderAsset)) identity = shaderAsset.Path;
                            int slot = i;
                            missing.Add(new MissingMaterialShader { Guid=data.ShaderGuid, FileId=data.ShaderFileId, Material=material,
                                Label=(data.Name ?? material.name) + " → " + identity,
                                Apply=(resolved,owned) =>
                                {
                                    using var loaded = new PreviewTextures(byGuid,owned,textureCache);
                                    var replacement = new Material(resolved) { name=data.Name ?? material.name };
                                    try
                                    {
                                        ApplyProperties(replacement,data,loaded);
                                        var slotsNow=renderer.sharedMaterials;
                                        slotsNow[slot]=replacement;
                                        renderer.sharedMaterials=slotsNow;
                                        owned.Add(replacement);
                                    }
                                    catch { Destroy(replacement); throw; }
                                }
                            });
                        }
                    }
                    if (!string.IsNullOrEmpty(data.Name)) material.name = data.Name;
                    ApplyProperties(material, data, textures);
                }
                renderer.sharedMaterials = materials;
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
                if (material.HasInteger(item.Key)) material.SetInteger(item.Key, item.Value);
                else if (material.HasProperty(item.Key)) material.SetInt(item.Key, item.Value);
            foreach (var item in data.Colors)
                if (material.HasProperty(item.Key))
                {
                    var v = item.Value;
                    if (material.shader.GetPropertyType(material.shader.FindPropertyIndex(item.Key)) == ShaderPropertyType.Vector)
                        material.SetVector(item.Key, new Vector4(v.R,v.G,v.B,v.A));
                    else material.SetColor(item.Key, new Color(v.R,v.G,v.B,v.A));
                }
            foreach (string pass in data.DisabledPasses) material.SetShaderPassEnabled(pass, false);
            foreach (var tag in data.Tags) material.SetOverrideTag(tag.Key, tag.Value);
            material.renderQueue = data.CustomRenderQueue >= 0 && data.CustomRenderQueue <= 5000 ? data.CustomRenderQueue : -1;
            foreach (var item in data.Textures)
            {
                if (!material.HasProperty(item.Key)) continue;
                material.SetTextureScale(item.Key, new Vector2(item.Value.Scale.X, item.Value.Scale.Y));
                material.SetTextureOffset(item.Key, new Vector2(item.Value.Offset.X, item.Value.Offset.Y));
                if (item.Value.Guid == null || item.Value.FileId == 0) continue;
                bool linear = item.Key.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.Key.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0;
                var texture = textures.Get(item.Value.Guid, linear);
                if (texture == null) continue;
                material.SetTexture(item.Key, texture);
            }
        }

        private sealed class PreviewTextures : IDisposable
        {
            private readonly Dictionary<string,UnityPackageReader.PackageAsset> assets;
            private readonly List<UnityEngine.Object> owned;
            private readonly Dictionary<(string guid, bool linear), Texture2D> cache;
            private Texture2D linearScratch, colorScratch;
            private long remaining = 512L * 1024 * 1024;
            internal int Count => cache.Count;

            internal PreviewTextures(Dictionary<string,UnityPackageReader.PackageAsset> assets, List<UnityEngine.Object> owned,
                Dictionary<(string guid, bool linear), Texture2D> cache)
            {
                this.assets = assets; this.owned = owned; this.cache = cache;
                foreach (var t in cache.Values) remaining -= ByteSize(t.width,t.height,t.mipmapCount > 1);
            }
            static long ByteSize(int w,int h,bool mips)
            {
                long bytes=(long)w*h*4;
                while (mips && (w>1 || h>1))
                { w=Math.Max(1,w/2); h=Math.Max(1,h/2); bytes+=(long)w*h*4; }
                return bytes;
            }

            internal Texture2D Get(string guid, bool linear)
            {
                if (!assets.TryGetValue(guid, out var entry) || !entry.HasAsset) return null;
                var settings = PackageTextureSettings.Read(entry.Meta, linear);
                linear = settings.Linear;
                if (cache.TryGetValue((guid, linear), out var texture)) return texture;
                if (Count >= 128) { Debug.LogWarning("Texture count limit: " + entry.Path); return null; }
                byte[] bytes = entry.Data;
                if (bytes.Length < 33 || bytes.Length > 32*1024*1024 || bytes[0] != 137 || bytes[1] != 80 ||
                    bytes[2] != 78 || bytes[3] != 71 || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 ||
                    bytes[7] != 10 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R') return null;
                int w = PngDimension(bytes, 16), h = PngDimension(bytes, 20);
                if (w < 1 || h < 1 || w > 4096 || h > 4096) return null;
                int width = w, height = h;
                while (Math.Max(width, height) > settings.MaxSize || ByteSize(width,height,settings.Mipmaps) > remaining)
                {
                    if (width == 1 && height == 1) { Debug.LogWarning("Texture memory limit: " + entry.Path); return null; }
                    width = Math.Max(1,width/2); height = Math.Max(1,height/2);
                }
                long size = ByteSize(width,height,settings.Mipmaps);

                if (width == w && height == h && !settings.NormalMap && !settings.IgnoreAlpha)
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, settings.Mipmaps, linear);
                    if (!ImageConversion.LoadImage(texture, bytes, true)) { Destroy(texture); return null; }
                }
                else
                {
                    ref var scratch = ref (linear ? ref linearScratch : ref colorScratch);
                    if (scratch == null) scratch = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { filterMode = FilterMode.Trilinear };
                    if (!ImageConversion.LoadImage(scratch, bytes, false)) return null;
                    texture = Downsample(scratch, width, height, settings);
                }
                texture.name = Path.GetFileNameWithoutExtension(entry.Path);
                texture.filterMode = (FilterMode)settings.Filter;
                texture.wrapModeU = (TextureWrapMode)settings.WrapU;
                texture.wrapModeV = (TextureWrapMode)settings.WrapV;
                texture.anisoLevel = settings.Aniso;
                texture.mipMapBias = settings.MipBias;
                remaining -= size;
                cache.Add((guid, linear), texture);
                owned.Add(texture);
                return texture;
            }

            private static int PngDimension(byte[] bytes, int offset) =>
                (bytes[offset]<<24) | (bytes[offset+1]<<16) | (bytes[offset+2]<<8) | bytes[offset+3];

            private static Texture2D Downsample(Texture2D full, int width, int height, PackageTextureSettings settings)
            {
                var previous = RenderTexture.active;
                bool linear = settings.Linear, previousWrite = GL.sRGBWrite;
                var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                try
                {
                    GL.sRGBWrite = !linear && QualitySettings.activeColorSpace == ColorSpace.Linear;
                    Graphics.Blit(full, target);
                    RenderTexture.active = target;
                    var preview = new Texture2D(width, height, TextureFormat.RGBA32, settings.Mipmaps, linear);
                    preview.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    if (settings.NormalMap || settings.IgnoreAlpha)
                    {
                        var pixels = preview.GetPixelData<Color32>(0);
                        for (int i=0; i<pixels.Length; i++)
                        {
                            var c = pixels[i];
                            // Windows shaders unpack X from R*A, Y from G; source alpha is not a normal component.
                            pixels[i] = settings.NormalMap ? new Color32(255,settings.FlipGreen?(byte)(255-c.g):c.g,255,c.r)
                                                          : new Color32(c.r,c.g,c.b,255);
                        }
                    }
                    preview.Apply(settings.Mipmaps, true);
                    return preview;
                }
                finally { GL.sRGBWrite = previousWrite; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
            }

            public void Dispose()
            {
                if (colorScratch != null) Destroy(colorScratch);
                if (linearScratch != null) Destroy(linearScratch);
            }
        }
    }
}
