using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF;
using UniJSON;
using UniVRM10;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace AvaAvaDance.Import
{
    // UniVRM supplies the glTF/MToon descriptors and their render-state validation.
    // VRM 0 can additionally store the original Unity shader and its property maps.
    internal sealed class VrmMaterialGenerator : IMaterialDescriptorGenerator
    {
        readonly BuiltInVrm10MaterialDescriptorGenerator defaults = new();
        readonly ShaderLibrary library;
        readonly List<MissingMaterialShader> missing;
        readonly JsonNode[] legacy = Array.Empty<JsonNode>();
        readonly Shader twoSidedPbr = Resources.Load<Shader>("GltfDoubleSided");

        internal VrmMaterialGenerator(GltfData source, ShaderLibrary library, List<MissingMaterialShader> missing)
        {
            this.library=library;
            this.missing=missing;
            if (source.GLTF.extensions is glTFExtensionImport extensions)
                foreach(var pair in extensions.ObjectItems())
                    if(pair.Key.GetString()=="VRM" && pair.Value.TryGet("materialProperties",out var properties))
                        legacy=properties.ArrayItems().ToArray();
        }

        public MaterialDescriptor GetGltfDefault(string name=null) => defaults.GetGltfDefault(name);

        public MaterialDescriptor Get(GltfData data,int index)
        {
            var desc=defaults.Get(data,index);
            var shader=desc.Shader;
            if(shader==defaults.PbrMaterialImporter.Shader && data.GLTF.materials[index].doubleSided)
                shader=twoSidedPbr;
            var actions=desc.AsyncActions;
            if(index<legacy.Length && legacy[index].TryGet("shader",out var field))
            {
                var name=field.GetString();
                if(!string.IsNullOrEmpty(name) && name!="VRM_USE_GLTFSHADER" && !IsMigratedUnlit(name))
                {
                    var saved=legacy[index];
                    var extended=new List<MaterialDescriptor.MaterialGenerateAsyncFunc>(actions);
                    extended.Add(async (material,getTexture,caller) =>
                    {
                        var textures=new Dictionary<string,Texture>();
                        if(saved.TryGet("textureProperties",out var slots))
                        {
                            foreach(var slot in slots.ObjectItems())
                            {
                                int id=slot.Value.GetInt32();
                                if((uint)id >= data.GLTF.textures.Count)continue;
                                string property=slot.Key.GetString();
                                var source=data.GLTF.materials[index];
                                TextureDescriptor tex;
                                bool found;
                                if(property=="_BumpMap" || property=="_NormalMap" || source.normalTexture?.index==id)
                                    found=GltfTextureImporter.TryCreateNormal(data,id,Vector2.zero,Vector2.one,out _,out tex);
                                else if(source.occlusionTexture?.index==id || source.pbrMetallicRoughness?.metallicRoughnessTexture?.index==id)
                                    found=GltfTextureImporter.TryCreateLinear(data,id,Vector2.zero,Vector2.one,out _,out tex);
                                else found=GltfTextureImporter.TryCreateSrgb(data,id,Vector2.zero,Vector2.one,out _,out tex);
                                if(found)textures[property]=await getTexture(tex,caller);
                            }
                        }
                        var resolved=library.ResolveMaterial(null,0,name);
                        if(resolved!=null)ApplyLegacy(material,resolved,saved,textures);
                        else missing.Add(Deferred(material,name,saved.Value.Segment.ToString(),textures));
                    });
                    actions=extended;
                }
            }
            if(shader==desc.Shader && actions==desc.AsyncActions)return desc;
            return new MaterialDescriptor(desc.Name,shader,desc.RenderQueue,desc.TextureSlots,desc.FloatValues,desc.Colors,
                desc.Vectors,desc.Actions,actions);
        }

        // UniVRM translates these historical render modes to supported native descriptors.
        static bool IsMigratedUnlit(string name) => name=="VRM/UnlitTexture" || name=="VRM/UnlitCutout" ||
            name=="VRM/UnlitTransparent" || name=="VRM/UnlitTransparentZWrite";

        // The importer disposes its GLB when loading finishes. Retain just this declaration
        // and the textures already owned by the runtime VRM instance.
        static MissingMaterialShader Deferred(Material material,string name,string declaration,Dictionary<string,Texture> textures) =>
            new MissingMaterialShader { Material=material, ExactName=name, Label=material.name+" → "+name,
                Apply=(available,_)=>ApplyLegacy(material,available,JsonParser.Parse(declaration),textures) };

        static void ApplyLegacy(Material target,Shader shader,JsonNode saved,Dictionary<string,Texture> textures)
        {
            var authored=new Material(shader);
            try
            {
                if(saved.TryGet("floatProperties",out var floats))
                    foreach(var pair in floats.ObjectItems())
                    {
                        string key=pair.Key.GetString();
                        if(authored.HasProperty(key))authored.SetFloat(key,pair.Value.GetSingle());
                    }
                foreach(var tex in textures)
                    if(authored.HasProperty(tex.Key))authored.SetTexture(tex.Key,tex.Value);
                if(saved.TryGet("vectorProperties",out var vectors))
                    foreach(var pair in vectors.ObjectItems())
                    {
                        string key=pair.Key.GetString();int property=shader.FindPropertyIndex(key);
                        if(property<0 || pair.Value.GetArrayCount()!=4)continue;
                        var v=new Vector4(pair.Value[0].GetSingle(),pair.Value[1].GetSingle(),pair.Value[2].GetSingle(),pair.Value[3].GetSingle());
                        switch(shader.GetPropertyType(property))
                        {
                            case ShaderPropertyType.Texture:
                                authored.SetTextureOffset(key,new Vector2(v.x,v.y));
                                authored.SetTextureScale(key,new Vector2(v.z,v.w));
                                break;
                            case ShaderPropertyType.Color: authored.SetColor(key,v);break;
                            case ShaderPropertyType.Vector: authored.SetVector(key,v);break;
                        }
                    }
                if(saved.TryGet("keywordMap",out var keywords))
                    foreach(var pair in keywords.ObjectItems())
                    {
                        string key=pair.Key.GetString();
                        if(pair.Value.GetBoolean())authored.EnableKeyword(key);else authored.DisableKeyword(key);
                    }
                if(saved.TryGet("renderQueue",out var queue))authored.renderQueue=queue.GetInt32();
                // Keep the material object that UniVRM's expression bindings reference.
                target.shader=shader;
                target.CopyPropertiesFromMaterial(authored);
                target.SetOverrideTag("RenderType","");
                if(saved.TryGet("tagMap",out var tags))
                    foreach(var pair in tags.ObjectItems()) target.SetOverrideTag(pair.Key.GetString(),pair.Value.GetString());
            }
            finally { UnityEngine.Object.Destroy(authored); }
        }
    }
}
