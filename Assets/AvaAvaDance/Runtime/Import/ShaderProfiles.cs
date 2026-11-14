using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityEngine;

namespace AvaAvaDance.Import
{
    public sealed partial class ShaderLibrary
    {
        sealed class CompiledPass
        {
            public int kind;
            public string name,mode,source,grab;
        }
        partial void ReadCompiledPasses(ShaderPackManifest manifest,byte[] data)
        {
            var manager=new AssetsManager();
            try{
                using var stream=new MemoryStream(data,false);
                var bundle=manager.LoadBundleFile(stream,"shader-pack",false);
                var shaders=new Dictionary<string,List<CompiledPass>>(StringComparer.Ordinal);
                foreach(var name in bundle.file.GetAllFileNames()){
                    if(name.EndsWith(".resS",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".resource",StringComparison.OrdinalIgnoreCase))continue;
                    var assets=manager.LoadAssetsFileFromBundle(bundle,name,false);
                    if(!assets.file.Metadata.TypeTreeEnabled)continue;
                    foreach(var shader in assets.file.GetAssetsOfType(AssetClassID.Shader)){
                        var root=manager.GetBaseField(assets,shader)["m_ParsedForm"];
                        var sub=root["m_SubShaders"]["Array"];if(sub.IsDummy || sub.Children.Count==0)continue;
                        var passes=new List<CompiledPass>();
                        foreach(var pass in sub[0]["m_Passes"]["Array"].Children){
                            var state=pass["m_State"];var tags=state["m_Tags"]["tags"]["Array"];
                            var light=tags.IsDummy?null:tags.Children.FirstOrDefault(t=>t[0].AsString.Equals("LightMode",StringComparison.OrdinalIgnoreCase));
                            passes.Add(new CompiledPass{kind=pass["m_Type"].AsInt,name=state["m_Name"].AsString,mode=light==null?null:light[1].AsString,
                                source=pass["m_UseName"].AsString,grab=pass["m_TextureName"].AsString});
                        }
                        shaders[root["m_Name"].AsString]=passes;
                    }
                }
                var named=shaders.SelectMany(s=>s.Value.Where(p=>p.kind==0 && !string.IsNullOrEmpty(p.name)).Select(p=>(key:s.Key+"/"+p.name,pass:p)))
                    .GroupBy(p=>p.key,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First().pass,StringComparer.OrdinalIgnoreCase);
                foreach(var entry in manifest.shaders){
                    if(!shaders.TryGetValue(entry.name,out var compiled))continue;
                    var grabs=new List<GrabProfile>();var stages=new List<ShaderPassProfile>();
                    foreach(var pass in compiled){
                        if(pass.kind==2){grabs.Add(new GrabProfile{beforePass=stages.Count,texture=pass.grab});stages.Add(new ShaderPassProfile{grab=true});continue;}
                        var body=pass.kind==1 && named.TryGetValue(pass.source,out var reference)?reference:pass;
                        stages.Add(new ShaderPassProfile{name=body.name,lightMode=body.mode,usePass=pass.source});
                    }
                    entry.grabPasses=grabs.ToArray();entry.passes=stages.ToArray();
                }
            }catch(Exception ex){Debug.LogWarning("シェーダーの描画情報を取得できません: "+ex.Message);}
            finally{manager.UnloadAll();}
        }
    }
}
