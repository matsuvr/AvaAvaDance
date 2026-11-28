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
            shaderNames = PackageDependency.ReadNames(Resources.Load<TextAsset>("ShaderLicenses/ShaderNames")?.text ?? "");
            var root = MenuRoot;
            shaderAddButton = new Button(OpenShaderMenu) { name="shader-add", text="シェーダー追加", tooltip=ShaderButtonHint };
            root.Q("workspace-tools").Insert(0, shaderAddButton);
            SetupDependencyNotice(root.Q("status-line"));
            try { OfficialShaderStore.Restore(OfficialShaderCatalog.Vrm, shaderLibrary); }
            catch (Exception ex) { Debug.LogWarning("保存済みシェーダーを復元できません: " + ex.Message); }
        }

        private string ResolveInstaller(UnityPackageReader.PackageAsset config, Action requestLilToon = null)
        {
            var loaded = shaderLibrary.LoadedLilToon;
            string message = VpmInstaller.Resolve(config.Data, loaded ?? VpmInstaller.SupportedLilToon, Application.unityVersion);
            if (loaded != null)
            {
                Debug.Log("AVA_VPM " + message);
                return message;
            }
            (requestLilToon ?? ShowLilToonConsent)();
            return "lilToon の公式VPMインストーラーを確認しました。";
        }

        private async System.Threading.Tasks.Task<string> LoadShaderFile(string path, Action requestLilToon=null)
        {
            if (!string.Equals(Path.GetExtension(path), ".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                int count = shaderLibrary.Load(path);
                int applied = ReapplyPackageShaders();
                return ShaderInstallResult(count + " 個のシェーダーを読み込みました。", applied);
            }
            var assets = await System.Threading.Tasks.Task.Run(() => UnityPackageReader.Read(path));
            var config = assets.FirstOrDefault(a => a.Path == VpmInstaller.ConfigPath);
            bool hasShaders = assets.Any(a => a.HasAsset && a.Path.EndsWith(".shader",StringComparison.OrdinalIgnoreCase));
            string message = null;
            if (config!=null)
                message = ResolveInstaller(config, requestLilToon);
            else if (hasShaders)
            {
                string compiler = Resources.Load<TextAsset>("PackageShaderBuilder")?.text;
                string output = await PackageShaderCompiler.Compile(assets,compiler,Application.persistentDataPath,progress => status.text=progress);
                message = await LoadShaderFile(output);
            }
            int added = ApplyDependencyAssets(assets);
            if (added > 0) return (message == null ? "" : message+" ") + "不足していた素材 "+added+" 件をモデルに追加しました。";
            return message ?? "このパッケージには、読込中のモデルが必要とするマテリアル・PNG、または ShaderLab シェーダーがありません。";
        }

        // Keep only referenced payloads with the actor, so its normal project save/open
        // path also restores supplements. A supplied material can introduce new textures.
        private int ApplyDependencyAssets(List<UnityPackageReader.PackageAsset> package)
        {
            var incoming = package.Where(a=>a.HasAsset).ToDictionary(a=>a.Guid,StringComparer.OrdinalIgnoreCase);
            var changes = new List<(Actor target,List<UnityPackageReader.PackageAsset> previous,List<UnityPackageReader.PackageAsset> next)>();
            int count = 0;
            foreach (var target in drawOrder.Where(a=>a.missingAssets.Count!=0))
            {
                var additions = new Dictionary<string,UnityPackageReader.PackageAsset>(StringComparer.OrdinalIgnoreCase);
                var known = new HashSet<string>(target.modelAssets.Where(a=>a.HasAsset).Select(a=>a.Guid),StringComparer.OrdinalIgnoreCase);
                void AddTexture(string guid)
                {
                    if (!PackageDependency.External(guid) || known.Contains(guid) || !incoming.TryGetValue(guid,out var texture)) return;
                    if (!texture.Path.EndsWith(".png",StringComparison.OrdinalIgnoreCase) || !PreviewTextures.SupportedPng(texture.Data))
                        throw new InvalidDataException("追加テクスチャには 4096×4096 以下・32 MiB 以下の PNG が必要です: "+texture.Path);
                    additions[guid]=texture;
                }
                foreach (var request in target.missingAssets)
                {
                    if (request.Kind==DependencyKind.Texture) { AddTexture(request.Guid); continue; }
                    if (additions.ContainsKey(request.Guid) || !incoming.TryGetValue(request.Guid,out var material) ||
                        !material.Path.EndsWith(".mat",StringComparison.OrdinalIgnoreCase)) continue;
                    var parsed = ParsePackageMaterial(material);
                    additions.Add(request.Guid,material);
                    var shader = shaderLibrary.ResolveMaterial(parsed.ShaderGuid,parsed.ShaderFileId,null);
                    foreach (var texture in parsed.Textures)
                        if (texture.Value.FileId!=0 && (shader==null || TextureProperty(shader,texture.Key)))
                            AddTexture(texture.Value.Guid);
                }
                if (additions.Count==0) continue;
                var merged = target.modelAssets.Where(a=>!additions.ContainsKey(a.Guid)).Concat(additions.Values).ToList();
                changes.Add((target,target.modelAssets,merged));
                count += additions.Count;
            }
            var previousActor=actor;
            int started=0;
            try
            {
                foreach (var change in changes)
                {
                    actor=change.target;
                    actor.modelAssets=change.next;
                    started++;
                    ReloadTextures();
                }
                if (count>0) sourceRevision++;
            }
            catch
            {
                foreach (var change in changes.Take(started)) change.target.modelAssets=change.previous;
                foreach (var change in changes.Take(started)) { actor=change.target; ReloadTextures(); }
                throw;
            }
            finally { actor=previousActor; RefreshMissingShaderButton(); }
            return count;
        }

        private Dictionary<string,(string pack,string name)> shaderNames;

        private static UnityMaterialData ParsePackageMaterial(UnityPackageReader.PackageAsset entry)
        {
            if (entry.Data.Length > UnityMaterialData.MaxUtf8Bytes) throw new InvalidDataException("Material exceeds size limit.");
            return UnityMaterialData.Parse(new UTF8Encoding(false,true).GetString(entry.Data));
        }

        private int ApplyPackageMaterials(GameObject candidate, List<UnityEngine.Object> resources, UnityPackageReader.PackageAsset model,
            List<UnityPackageReader.PackageAsset> assets, UnityPackageReader.PackageAsset prefab, List<MissingMaterialShader> missing,
            List<PackageDependency> missingAssets)
        {
            var byGuid = assets.ToDictionary(a => a.Guid, StringComparer.OrdinalIgnoreCase);
            var bindings = AvatarMaterialBindings.Read(model, prefab);
            var materialsByGuid = new Dictionary<string,UnityMaterialData>(StringComparer.OrdinalIgnoreCase);
            var materialsByName = new Dictionary<string,UnityMaterialData>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in assets.Where(a => a.HasAsset && a.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var data = ParsePackageMaterial(entry);
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
            missingAssets.Clear();
            bool Absent(string guid) => PackageDependency.External(guid) && (!byGuid.TryGetValue(guid,out var asset) || !asset.HasAsset);
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
                    string name = i < names.Length ? names[i] : null;
                    string materialGuid = null;
                    if (slots != null) materialGuid = slots.Materials[i];
                    else if (name != null && !bindings.Remaps.TryGetValue(name,out materialGuid))
                    {
                        materialsByName.TryGetValue(name,out data);
                        // FBX can append a numeric duplicate suffix to an importer remap name.
                        if (data == null && System.Text.RegularExpressions.Regex.IsMatch(name,@"\.[0-9]{3}$") &&
                            bindings.Remaps.TryGetValue(name.Substring(0,name.Length-4),out materialGuid))
                            name=name.Substring(0,name.Length-4);
                    }
                    if (materialGuid != null) materialsByGuid.TryGetValue(materialGuid,out data);
                    if (data == null)
                    {
                        unsupported++;
                        if (Absent(materialGuid)) missingAssets.Add(new PackageDependency { Kind=DependencyKind.Material, Guid=materialGuid,
                            Name=slots==null ? name : null, UsedBy=renderer.name+" ["+(i+1)+"]"+(slots==null || name==null ? "" : " "+name) });
                        continue;
                    }
                    string usedBy = data.Name ?? name ?? renderer.name;
                    var shader = shaderLibrary.ResolveMaterial(data.ShaderGuid,data.ShaderFileId,null);
                    var textureRequests = new List<(string property,PackageDependency dependency)>();
                    foreach (var property in data.Textures)
                        if (property.Value.FileId!=0 && Absent(property.Value.Guid) &&
                            (shader==null || TextureProperty(shader,property.Key)))
                        {
                            var request = new PackageDependency { Kind=DependencyKind.Texture, Guid=property.Value.Guid,
                                UsedBy=usedBy+" / "+property.Key };
                            textureRequests.Add((property.Key,request));
                            missingAssets.Add(request);
                        }

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
                            UnityPackageReader.PackageAsset shaderAsset = null;
                            if (data.ShaderGuid != null) byGuid.TryGetValue(data.ShaderGuid,out shaderAsset);
                            var dependency = PackageDependency.Shader(data.ShaderGuid,usedBy,shaderAsset,shaderNames);
                            if (dependency.Name==null && data.Tags.TryGetValue("OriginalShader",out var declared) &&
                                !string.IsNullOrWhiteSpace(declared) && declared.Length<=256) dependency.Name=declared;
                            string identity = dependency.Name ?? data.ShaderGuid ?? ("fileID " + data.ShaderFileId);
                            int slot = i;
                            missing.Add(new MissingMaterialShader { Guid=data.ShaderGuid, FileId=data.ShaderFileId, Material=material,
                                Label=usedBy + " → " + identity, Dependency=dependency,
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
                                        foreach (var request in textureRequests)
                                            if (!TextureProperty(resolved,request.property)) missingAssets.Remove(request.dependency);
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

        private static bool TextureProperty(Shader shader, string name)
        {
            int index = shader.FindPropertyIndex(name);
            return index >= 0 && shader.GetPropertyType(index) == ShaderPropertyType.Texture;
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
                if (!TextureProperty(material.shader,item.Key)) continue;
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

            internal static bool SupportedPng(byte[] bytes)
            {
                if (bytes.Length < 33 || bytes.Length > 32*1024*1024 || bytes[0] != 137 || bytes[1] != 80 ||
                    bytes[2] != 78 || bytes[3] != 71 || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 ||
                    bytes[7] != 10 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R') return false;
                int w=PngDimension(bytes,16), h=PngDimension(bytes,20);
                return w >= 1 && h >= 1 && w <= 4096 && h <= 4096;
            }

            internal Texture2D Get(string guid, bool linear)
            {
                if (!assets.TryGetValue(guid, out var entry) || !entry.HasAsset) return null;
                var settings = PackageTextureSettings.Read(entry.Meta, linear);
                linear = settings.Linear;
                if (cache.TryGetValue((guid, linear), out var texture)) return texture;
                if (Count >= 128) { Debug.LogWarning("Texture count limit: " + entry.Path); return null; }
                byte[] bytes = entry.Data;
                if (!SupportedPng(bytes)) return null;
                int w = PngDimension(bytes, 16), h = PngDimension(bytes, 20);
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
