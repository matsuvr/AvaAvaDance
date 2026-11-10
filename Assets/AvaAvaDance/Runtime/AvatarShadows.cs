using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly List<(Renderer renderer, int slot, Material material)> shadowDraws = new List<(Renderer,int,Material)>();
        readonly Dictionary<Material,Material> shadowMaterials = new Dictionary<Material,Material>();
        RenderTexture shadowDepth;
        CommandBuffer depthCommands, selfCommands;

        void InitShadowRendering()
        {
            depthCommands=new CommandBuffer {name="アバター影深度"};
            selfCommands=new CommandBuffer {name="セルフ影"};
            previewCamera.AddCommandBuffer(CameraEvent.BeforeImageEffects,selfCommands);
            Camera.onPreCull+=RenderAvatarShadows;
        }

        void BindShadowRenderers()
        {
            ClearShadowRenderers();
            var shader=Resources.Load<Shader>("AvatarShadow");
            foreach(var r in System.Linq.Enumerable.SelectMany(drawOrder,a=>a.shadowCasters)) {
                if(r==null)continue;
                var materials=r.sharedMaterials;
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
                    shadowDraws.Add((r,i,material));
                }
            }
        }
        void ClearShadowRenderers()
        {
            selfCommands?.Clear();depthCommands?.Clear();shadowDraws.Clear();
            foreach(var mat in shadowMaterials.Values)Destroy(mat); shadowMaterials.Clear();
        }

        void RenderAvatarShadows(Camera camera)
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
            depthCommands.SetRenderTarget(shadowDepth);
            depthCommands.ClearRenderTarget(true,true,Color.white);
            if(!viewOptions.hidden && (groundShadow || self)) {
                foreach(var draw in shadowDraws) {
                    if(!draw.renderer.enabled || !draw.renderer.gameObject.activeInHierarchy)continue;
                    depthCommands.DrawRenderer(draw.renderer,draw.material,draw.slot,0);
                    if(self && drawOrder.Exists(a=>a.modelSelfShadow && System.Array.IndexOf(a.shadowCasters,draw.renderer)>=0))selfCommands.DrawRenderer(draw.renderer,draw.material,draw.slot,1);
                }
            }
            depthCommands.SetGlobalTexture("_AvaShadowDepth",shadowDepth);
            Graphics.ExecuteCommandBuffer(depthCommands);
        }

        void DisposeShadowRendering()
        {
            Camera.onPreCull-=RenderAvatarShadows;
            if(previewCamera!=null && selfCommands!=null)previewCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects,selfCommands);
            ClearShadowRenderers();depthCommands?.Release();selfCommands?.Release();
            if(shadowDepth!=null){shadowDepth.Release();Destroy(shadowDepth);}
        }
    }
}
