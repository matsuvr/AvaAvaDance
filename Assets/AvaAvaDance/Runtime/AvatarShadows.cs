using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly List<(Renderer renderer, int slot, int submesh, Material material, bool native, bool caster)> shadowDraws = new();
        readonly HashSet<Renderer> nativeShadowReceivers = new();
        readonly Dictionary<Material,Material> shadowMaterials = new Dictionary<Material,Material>();
        RenderTexture shadowDepth,groundDepth;
        CommandBuffer depthCommands, selfCommands;

        void InitShadowRendering()
        {
            depthCommands=new CommandBuffer {name="アバター影深度"};
            selfCommands=new CommandBuffer {name="セルフ影"};
            previewCamera.AddCommandBuffer(CameraEvent.BeforeImageEffects,selfCommands);
        }

        void BindShadowRenderers()
        {
            ClearShadowRenderers();
            var shader=Resources.Load<Shader>("AvatarShadow");
            foreach(var model in drawOrder)
            foreach(var r in model.shadowCasters) {
                if(r==null)continue;
                var materials=model.originals.TryGetValue(r,out var source)?source:r.sharedMaterials;
                var mesh=r is SkinnedMeshRenderer skin?skin.sharedMesh:r.GetComponent<MeshFilter>()?.sharedMesh;
                if(mesh==null || mesh.subMeshCount==0)continue;
                for(int i=0;i<materials.Length;i++) {
                    var original=materials[i];
                    if(original==null)continue;
                    if(!shadowMaterials.TryGetValue(original,out var material)) {
                        material=new Material(shader);
                        if(original.HasProperty("_MainTex")) {
                            material.mainTexture=original.GetTexture("_MainTex");
                            material.mainTextureScale=original.GetTextureScale("_MainTex");
                            material.mainTextureOffset=original.GetTextureOffset("_MainTex");
                        }
                        float cutoff=original.HasProperty("_Cutoff")?original.GetFloat("_Cutoff"):0;
                        material.SetFloat("_Cutoff",Mathf.Clamp(cutoff,.03f,.99f));
                        material.SetFloat("_Opacity",original.HasProperty("_Color")?original.GetColor("_Color").a:1);
                        shadowMaterials.Add(original,material);
                    }
                    bool native=model.vrm!=null || shaderLibrary.IsAvailableMaterialShader(original.shader);
                    if(native)nativeShadowReceivers.Add(r);
                    bool caster = !native || (original.GetShaderPassEnabled("ShadowCaster") && HasCasterPass(original));
                    shadowDraws.Add((r,i,Mathf.Min(i,mesh.subMeshCount-1),material,native,caster));
                }
            }
            if(shadowTrack!=null)ApplyShadow(shadowTrack.Current);
        }
        static bool HasCasterPass(Material material)
        {
            var mode=new ShaderTagId("LightMode");
            for(int p=0;p<material.passCount;p++)
                if(string.Equals(material.shader.FindPassTagValue(p,mode).name,"ShadowCaster",System.StringComparison.OrdinalIgnoreCase))return true;
            return false;
        }
        void ClearShadowRenderers()
        {
            selfCommands?.Clear();depthCommands?.Clear();shadowDraws.Clear();nativeShadowReceivers.Clear();
            foreach(var mat in shadowMaterials.Values)Destroy(mat); shadowMaterials.Clear();
        }

        void RenderAvatarShadows(Camera camera,Dictionary<(Renderer,int),MaterialSlot> slots=null)
        {
            if(camera!=previewCamera || shadowTrack==null)return;
            selfCommands.Clear();depthCommands.Clear();
            var pose=shadowTrack.Current;
            int resolution=pose.Mode==2?2048:1024;
            if(shadowDepth==null || shadowDepth.width!=resolution) {
                if(shadowDepth!=null){shadowDepth.Release();Destroy(shadowDepth);}
                shadowDepth=new RenderTexture(resolution,resolution,24,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear) {
                    filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,name="アバター影深度"
                };
                shadowDepth.Create();
            }
            bool self=pose.Mode!=0 && showSelfShadow;
            var bounds=new Bounds(Vector3.zero,Vector3.one*.1f);
            bool first=true;
            foreach(var item in drawOrder) foreach(var renderer in item.shadowCasters) {
                if(!renderer.enabled || !renderer.gameObject.activeInHierarchy)continue;
                if(first){bounds=renderer.bounds;first=false;}else bounds.Encapsulate(renderer.bounds);
            }
            float radius=Mathf.Max(.25f,bounds.extents.magnitude);
            float extent=radius*Mathf.Lerp(1.02f,1.8f,pose.Range/9999f);
            var light=previewLamp.transform;
            depthCommands.SetGlobalVector("_AvaShadowOrigin",bounds.center);
            depthCommands.SetGlobalVector("_AvaLightRight",light.right);depthCommands.SetGlobalVector("_AvaLightUp",light.up);
            depthCommands.SetGlobalVector("_AvaLightForward",light.forward);
            depthCommands.SetGlobalVector("_AvaShadowScale",new Vector4(.5f/extent,.5f/(radius+2),1f/resolution,0));
            var lightView=Matrix4x4.TRS(bounds.center,light.rotation,Vector3.one).inverse;
            float flip=SystemInfo.usesReversedZBuffer?-1:1;
            var projection=Matrix4x4.Scale(new Vector3(1/extent,-1/extent,flip*.5f/(radius+2)));
            projection.m23=.5f;
            var separateGround=false;
            var groundOverrides=new HashSet<(Renderer,int)>();
            var omitted=new HashSet<(Renderer,int)>();
            depthCommands.SetRenderTarget(shadowDepth);
            depthCommands.ClearRenderTarget(true,true,Color.white);
            if(!viewOptions.hidden && (groundShadow || self)) {
                foreach(var draw in shadowDraws) {
                    if(!draw.renderer.enabled || !draw.renderer.gameObject.activeInHierarchy)continue;
                    var key=(draw.renderer,draw.slot);
                    SurfaceBinding surface=null;MaterialSlot slot=null;
                    if(slots!=null && slots.TryGetValue(key,out slot))
                        surface=surfaceProviders.Select(p=>p.Resolve(slot)).FirstOrDefault(b=>b!=null);
                    if(surface?.Visible==false && surface.Passes==null){omitted.Add(key);continue;}
                    var roles=surface?.Plan?.Roles;
                    bool ground=roles?.Contains("shadow")==true && ResolveRole(slot,"shadow")?.Passes!=null;
                    if(ground && groundShadow){separateGround=true;groundOverrides.Add(key);}
                    var zplot=roles?.Contains("zplot")==true?ResolveRole(slot,"zplot"):null;
                    bool casts=draw.renderer.shadowCastingMode!=ShadowCastingMode.Off && (draw.caster || zplot?.Passes!=null);
                    if(!casts)omitted.Add(key);
                    else if(zplot?.Passes==null)depthCommands.DrawRenderer(draw.renderer,draw.material,draw.submesh,0);
                    else if(zplot.Visible){
                        var mesh=CurrentSurfaceMesh(draw.renderer);
                        if(mesh!=null && mesh.isReadable && draw.submesh<mesh.subMeshCount){
                            var mat=draw.renderer.sharedMaterials[draw.slot];
                            var values=SurfaceConstants(camera,draw.renderer.localToWorldMatrix,mat,mesh,lightView,projection);
                            values[102]=values[103]=resolution;
                            QueueSurface(zplot,slot,camera,SurfaceVertices(mesh),mesh.GetIndices(draw.submesh),mat,values,shadowDepth,commandOverride:depthCommands);
                        }
                    }
                    // Package materials shade Unity's light inside their own forward pass, including
                    // authored shadow colors, masks, stencil and intentional unlit passes (e.g. eyes).
                    if(self && (!draw.native || surface?.Passes?.Length>0) && surface?.Visible!=false &&
                       draw.renderer.shadowCastingMode!=ShadowCastingMode.ShadowsOnly &&
                       !(slot?.SelfShadow==true && roles?.Contains("object_ss")==true) &&
                       drawOrder.Exists(a=>a.modelSelfShadow && a.sourceShadowReceivers.Contains(draw.renderer)))
                        selfCommands.DrawRenderer(draw.renderer,draw.material,draw.submesh,1);
                }
            }
            if(separateGround){
                if(groundDepth==null || groundDepth.width!=resolution){
                    if(groundDepth!=null)Destroy(groundDepth);
                    groundDepth=new RenderTexture(shadowDepth.descriptor){name="地面影深度",filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};groundDepth.Create();
                }
                depthCommands.SetRenderTarget(groundDepth);depthCommands.ClearRenderTarget(true,true,Color.white);
                foreach(var draw in shadowDraws){
                    var key=(draw.renderer,draw.slot);
                    if(draw.renderer.enabled && draw.renderer.gameObject.activeInHierarchy && !groundOverrides.Contains(key) && !omitted.Contains(key))
                        depthCommands.DrawRenderer(draw.renderer,draw.material,draw.submesh,0);
                }
            }
            groundMaterial.SetTexture("_AvaGroundDepth",separateGround?groundDepth:shadowDepth);
            depthCommands.SetGlobalTexture("_AvaShadowDepth",shadowDepth);
            Graphics.ExecuteCommandBuffer(depthCommands);
        }

        void DisposeShadowRendering()
        {
            if(previewCamera!=null && selfCommands!=null)previewCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects,selfCommands);
            ClearShadowRenderers();depthCommands?.Release();selfCommands?.Release();
            if(groundDepth!=null){groundDepth.Release();Destroy(groundDepth);}
            if(shadowDepth!=null){shadowDepth.Release();Destroy(shadowDepth);}
        }
    }
}
