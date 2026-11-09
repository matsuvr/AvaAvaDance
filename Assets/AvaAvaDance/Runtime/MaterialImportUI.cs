using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            try { shaderLibrary.LoadBundled(); }
            catch(Exception ex) { status.text = "同梱シェーダー読込: " + ex.Message; Debug.LogWarning(status.text); }
            var menu = GetComponent<UIDocument>().rootVisualElement.Q<VisualElement>("menu");
            menu.Add(new Button(() =>
            {
                try
                {
                    var path = PackageFileDialog.Open("追加シェーダーパックを選択", "avadshader");
                    if(string.IsNullOrEmpty(path)) return;
                    int count=shaderLibrary.Load(path);
                    status.text = count + " 個のコンパイル済みシェーダーを追加。モデルを再読込すると適用されます。";
                }
                catch(Exception ex) { status.text="シェーダー追加中止: " + ex.Message; }
            }) { text="シェーダー追加", tooltip="信頼できる配布者の Windows / Unity 2022.3.22f1 用 .avadshader のみ。shaderソースは実行しません。" });
        }

        private int ApplyPackageMaterials(List<UnityPackageReader.PackageAsset> assets)
        {
            var byGuid=assets.ToDictionary(a=>a.Guid,StringComparer.OrdinalIgnoreCase);
            var sources=new List<KeyValuePair<UnityPackageReader.PackageAsset,UnityMaterialData>>();
            int unsupported=0,restored=0,textures=0; long budget=128L*1024*1024;
            foreach(var entry in assets.Where(a=>a.HasAsset&&a.Path.EndsWith(".mat",StringComparison.OrdinalIgnoreCase)))
            {
                try { sources.Add(new KeyValuePair<UnityPackageReader.PackageAsset,UnityMaterialData>(entry,UnityMaterialData.Parse(new UTF8Encoding(false,true).GetString(entry.Data)))); }
                catch(Exception ex) { Debug.LogWarning("Material skipped " + entry.Path + ": " + ex.Message); }
            }
            var cache=new Dictionary<string,Texture2D>();
            foreach(var renderer in avatar.GetComponentsInChildren<Renderer>()) foreach(var material in renderer.sharedMaterials)
            {
                var matches=sources.Where(s=>string.Equals(Path.GetFileNameWithoutExtension(s.Key.Path),material.name,StringComparison.OrdinalIgnoreCase)||string.Equals(s.Value.Name,material.name,StringComparison.OrdinalIgnoreCase)).ToList();
                if(matches.Count!=1) { unsupported++; continue; }
                var data=matches[0].Value;
                // Only declared original GUIDs select compiled code; package source is never executed.
                var shader=shaderLibrary.Resolve(data.ShaderGuid);
                if(shader!=null && shader.isSupported) { material.shader=shader; restored++; }
                else { unsupported++; Debug.LogWarning("Unsupported shader GUID " + data.ShaderGuid + " for " + material.name); }
                material.shaderKeywords = Array.Empty<string>();
                foreach (var name in data.Keywords) { var keyword = material.shader.keywordSpace.FindKeyword(name); if (keyword.isValid) material.EnableKeyword(keyword); }
                foreach(var item in data.Floats) if(material.HasProperty(item.Key)) material.SetFloat(item.Key,item.Value);
                foreach(var item in data.Ints) if(material.HasProperty(item.Key)) material.SetInt(item.Key,item.Value);
                foreach(var item in data.Colors) if(material.HasProperty(item.Key)) material.SetColor(item.Key,new Color(item.Value.R,item.Value.G,item.Value.B,item.Value.A));
                if(data.CustomRenderQueue>=0 && data.CustomRenderQueue<=5000) material.renderQueue=data.CustomRenderQueue;
                foreach(var item in data.Textures)
                {
                    if(!material.HasProperty(item.Key) || item.Value.Guid==null || !byGuid.TryGetValue(item.Value.Guid,out var entry) || !entry.HasAsset) continue;
                    bool linear=item.Key.IndexOf("Bump",StringComparison.OrdinalIgnoreCase)>=0 || item.Key.IndexOf("Mask",StringComparison.OrdinalIgnoreCase)>=0;
                    string key=entry.Guid+":"+linear;
                    if(!cache.TryGetValue(key,out var texture))
                    {
                        byte[] bytes=entry.Data;
                        if(bytes.Length<24 || bytes.Length>32*1024*1024 || bytes[0]!=137 || bytes[1]!=80 || bytes[2]!=78 || bytes[3]!=71 || cache.Count>=32) continue;
                        long w=((long)bytes[16]<<24)|((long)bytes[17]<<16)|((long)bytes[18]<<8)|bytes[19];
                        long h=((long)bytes[20]<<24)|((long)bytes[21]<<16)|((long)bytes[22]<<8)|bytes[23];
                        if(w<1||h<1||w>4096||h>4096||w*h*4>budget) continue;
                        budget-=w*h*4;
                        texture=new Texture2D(2,2,TextureFormat.RGBA32,false,linear);
                        if(!ImageConversion.LoadImage(texture,bytes,true)) { Destroy(texture); continue; }
                        cache.Add(key,texture); importedResources.Add(texture); textures++;
                    }
                    material.SetTexture(item.Key,texture);
                    material.SetTextureScale(item.Key,new Vector2(item.Value.Scale.X,item.Value.Scale.Y));
                    material.SetTextureOffset(item.Key,new Vector2(item.Value.Offset.X,item.Value.Offset.Y));
                }
            }
            materialReport="shader適用 " + restored + " / 未対応・材質不明 " + unsupported;
            Debug.Log("AVA_MATERIAL " + materialReport);
            return textures;
        }
    }
}
