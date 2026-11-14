using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly List<ISurfaceProvider> surfaceProviders=new List<ISurfaceProvider>();
        readonly Dictionary<Renderer,Material[]> temporarySlots=new Dictionary<Renderer,Material[]>();
        CommandBuffer surfaceCommands,initSurfaceCommands,preSurfaceCommands,postSurfaceCommands;
        long surfaceDocument=-1;
        bool surfaceExport;
        CommandBuffer shadowSurfaces;
        Material silentSurface;
        readonly List<IntPtr> submittedPackets=new List<IntPtr>();
        readonly Queue<(GraphicsFence fence,IntPtr[] packets)> surfaceFences=new Queue<(GraphicsFence,IntPtr[])>();
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]static extern void ava_fx_cancel(IntPtr token);
        void RetirePackets(bool all)
        {
            while(surfaceFences.Count>0 && (all || surfaceFences.Peek().fence.passed))
                foreach(var token in surfaceFences.Dequeue().packets)ava_fx_cancel(token);
            if(all){foreach(var token in submittedPackets)ava_fx_cancel(token);submittedPackets.Clear();}
        }
        Mesh bakedSurface;
        readonly Vector4[] surfaceButtons=new Vector4[3];
        Vector2 surfacePointer;
        float evaluationTime,previousSample,outputDelta,renderSeconds,renderDelta,syncedSeconds,syncedDelta;
        double previousClock;
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool ava_fx_error(System.Text.StringBuilder message,int length);
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_fx_event();
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_fx_packet(IntPtr program,IntPtr target,IntPtr[] textures,int textureCount,
            float[] vertices,int count,int[] indices,int indexCount,float[] values,int valueCount,int fullscreen);

        IEnumerable<(Renderer renderer,int index,MaterialSlot slot)> EnumerateMaterials()
        {
            foreach(var target in drawOrder.Select(a=>(a.id,a.label,a.avatar.transform,a.controller?.alias??a.modelAssets.FirstOrDefault(m=>m.Guid==a.modelGuid)?.Path,a.modelSelfShadow,receivers:a.sourceShadowReceivers)).Concat(props.Select(p=>(p.Data.id,p.Data.name,p.Object.transform,p.Data.EffectFile,true,(HashSet<Renderer>)null))))
            {
                var root=target.Item3;int subset=0;
                foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var parts=new Stack<int>();
                    for(var t=renderer.transform;t!=root && t!=null;t=t.parent)parts.Push(t.GetSiblingIndex());
                    string prefix=target.Item1+"/"+string.Join(".",parts)+"/"+Array.IndexOf(renderer.GetComponents<Renderer>(),renderer);
                    var materials=renderer.sharedMaterials;
                    for(int i=0;i<materials.Length;i++)
                        if(materials[i]!=null)yield return (renderer,i,new MaterialSlot {Id=prefix+"/"+i,ActorId=target.Item1,
                            ActorName=target.Item2,SourceFile=target.Item4,MaterialName=materials[i].name,Subset=subset++,SelfShadow=(target.Item6?.Contains(renderer)??renderer.receiveShadows) && target.Item5 && showSelfShadow && shadowTrack?.Current.Mode>0,
                            Textured=TextureBound(materials[i],"_MainTex"),SphereMapped=TextureBound(materials[i],"_SphereAdd") || TextureBound(materials[i],"_SphereMul"),Toon=TextureBound(materials[i],"_ToonTex")});
                }
            }
        }
        static bool TextureBound(Material mat,string name)=>mat.HasProperty(name) && mat.GetTexture(name)!=null;
        void InitSurfaceRendering()
        {
            preSurfaceCommands=new CommandBuffer{name="前処理エフェクト"};postSurfaceCommands=new CommandBuffer{name="後処理エフェクト"};
            initSurfaceCommands=new CommandBuffer{name="エフェクト描画先の初期化"};
            previewCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,initSurfaceCommands);
            previewCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,preSurfaceCommands);
            previewCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha,postSurfaceCommands);
            surfaceCommands=new CommandBuffer{name="材質エフェクト"};
            previewCamera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha,surfaceCommands);
            shadowSurfaces=new CommandBuffer{name="材質の影"};previewLamp.AddCommandBuffer(LightEvent.BeforeShadowMapPass,shadowSurfaces);
            screenCopy=new CommandBuffer{name="スクリーンの前フレーム"};previewCamera.AddCommandBuffer(CameraEvent.AfterEverything,screenCopy);
            Camera.onPreCull+=PrepareSurfaces;Camera.onPostRender+=RestoreSurfaces;
        }
        void DisposeSurfaceRendering()
        {
            if(screenCopy!=null){if(previewCamera!=null)previewCamera.RemoveCommandBuffer(CameraEvent.AfterEverything,screenCopy);screenCopy.Release();screenCopy=null;}
            Camera.onPreCull-=PrepareSurfaces;Camera.onPostRender-=RestoreSurfaces;
            RestoreSurfaces(previewCamera);
            if(previewCamera!=null && surfaceCommands!=null)previewCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,surfaceCommands);
            if(previewCamera!=null && preSurfaceCommands!=null){
                previewCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,initSurfaceCommands);
                previewCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,preSurfaceCommands);
                previewCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha,postSurfaceCommands);
            }
            if(previewLamp!=null && shadowSurfaces!=null)previewLamp.RemoveCommandBuffer(LightEvent.BeforeShadowMapPass,shadowSurfaces);
            if(surfaceAttenuation!=null)Destroy(surfaceAttenuation);
            shadowSurfaces?.Release();RetirePackets(true);initSurfaceCommands?.Release();preSurfaceCommands?.Release();postSurfaceCommands?.Release();surfaceCommands?.Release();DisposeSurfaceTargets();
            if(bakedSurface!=null)Destroy(bakedSurface);if(silentSurface!=null)Destroy(silentSurface);
        }
        void RestoreSurfaces(Camera camera)
        {
            if(camera!=previewCamera)return;
            foreach(var p in temporarySlots)if(p.Key!=null)p.Key.sharedMaterials=p.Value;
            if(submittedPackets.Count>0){
                if(SystemInfo.supportsGraphicsFence)surfaceFences.Enqueue((Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation,SynchronisationStageFlags.AllGPUOperations),submittedPackets.ToArray()));
                submittedPackets.Clear();
            }
            RetirePackets(false);
            temporarySlots.Clear();shadowSurfaces?.Clear();surfaceCommands?.Clear();initSurfaceCommands?.Clear();preSurfaceCommands?.Clear();postSurfaceCommands?.Clear();
        }
        string lastSurfaceDiagnostic;
        void PrepareSurfaces(Camera camera)
        {
            if(camera!=previewCamera || camera.targetTexture==null)return;
            try{PrepareScreenHistory(camera);ScheduleSurfaces(camera);lastSurfaceDiagnostic=null;}
            catch(Exception ex){
                foreach(var token in submittedPackets)ava_fx_cancel(token);submittedPackets.Clear();
                RestoreSurfaces(camera);
                if(lastSurfaceDiagnostic!=ex.Message){lastSurfaceDiagnostic=ex.Message;Debug.LogWarning(ex);status.text="エフェクト描画　"+ex.Message;}
            }
        }
        void ScheduleSurfaces(Camera camera)
        {
            EmitEvaluation(EvaluationStage.BeforeRenderSnapshot,evaluationTime);
            if(!surfaceProviders.Any(p=>p.HasBindings)){RenderAvatarShadows(camera);if(surfaceTargets.Count>0 || surfaceImages.Count>0 || surfaceAnimations.Count>0)DisposeSurfaceTargets();return;}
            if(surfaceDocument!=documentGeneration || surfaceExport!=exporting){
                foreach(var rt in surfaceTargets.Values)Destroy(rt);
                surfaceTargets.Clear();surfaceTexels.Clear();surfaceDocument=documentGeneration;surfaceExport=exporting;
            }
            usedTargets.Clear();usedImages.Clear();
            double clock=Time.realtimeSinceStartupAsDouble;
            syncedSeconds=evaluationTime/30;
            syncedDelta=exporting?outputDelta:syncedSeconds-previousSample;
            renderSeconds=playing || exporting?syncedSeconds:(float)clock;
            renderDelta=playing || exporting?syncedDelta:previousClock==0?0:(float)(clock-previousClock);
            previousClock=clock;previousSample=syncedSeconds;
            UpdateSurfacePointer();
            var entries=EnumerateMaterials().ToList();
            DefineSharedTargets(camera,entries.Select(e=>e.slot));
            RenderAvatarShadows(camera,entries.ToDictionary(e=>(e.renderer,e.index),e=>e.slot));
            ScheduleOffscreen(camera,entries);
            var plans=entries.Where(e=>e.renderer.enabled && !e.renderer.forceRenderingOff && e.renderer.gameObject.activeInHierarchy)
                .Select(e=>(e.renderer,e.index,e.slot,material:e.renderer.sharedMaterials[e.index],
                    binding:surfaceProviders.Select(p=>p.Resolve(e.slot)).FirstOrDefault(b=>b!=null))).ToList();
            bool mixTransparent=plans.Any(e=>e.material.renderQueue>2500 && e.binding?.Visible==true && e.binding.Passes?.Length>0 && e.binding.Plan?.Class!="scene");
            var sorted=plans.OrderBy(e=>e.material.renderQueue).ThenBy(e=>SortingLayer.GetLayerValueFromID(e.renderer.sortingLayerID)).ThenBy(e=>e.renderer.sortingOrder)
                .ThenByDescending(e=>e.material.renderQueue>2500?(camera.orthographic?Vector3.Dot(e.renderer.bounds.center-camera.transform.position,camera.transform.forward):(e.renderer.bounds.center-camera.transform.position).sqrMagnitude):0);
            var hidden=new Dictionary<Renderer,Material[]>();
            var captured=new HashSet<int>();
            var completedScenes=new HashSet<string>();
            var preEffects=new List<Action>();
            var postEffects=new List<Action>();
            foreach(var e in sorted)
            {
                var bind=e.binding;bool unity=bind==null || bind.Visible && (bind.Passes==null || bind.Passes.Length==0);
                if(unity)
                {
                    if(!mixTransparent || e.material.renderQueue<=2500)continue;
                    DrawUnitySurface(surfaceCommands,e.renderer,e.index,e.material,captured);
                }
                else if(bind.Visible || bind.Passes!=null && bind.Plan?.Class!="scene")
                {
                    bool scene=bind.Plan?.Class=="scene";
                    if(scene && !completedScenes.Add(bind.Instance??e.slot.Id))continue;
                    Mesh mesh;
                    if(e.renderer is SkinnedMeshRenderer skin){if(bakedSurface==null)bakedSurface=new Mesh();skin.BakeMesh(bakedSurface,true);mesh=bakedSurface;}
                    else mesh=e.renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if(mesh==null || !mesh.isReadable || e.index>=mesh.subMeshCount)continue;
                    var vertices=SurfaceVertices(mesh);var indices=mesh.GetIndices(e.index);
                    var values=SurfaceConstants(camera,e.renderer.localToWorldMatrix,e.material,mesh);
                    if(bind.Plan?.Order=="postprocess"){
                        QueueSurface(bind,e.slot,camera,vertices,indices,e.material,values,postPrefix:true);
                        postEffects.Add(()=>QueueSurface(bind,e.slot,camera,vertices,indices,e.material,values,postSuffix:true));
                    }else if(bind.Plan?.Order=="preprocess"){
                        preEffects.Add(()=>QueueSurface(bind,e.slot,camera,vertices,indices,e.material,values));
                    }else QueueSurface(bind,e.slot,camera,vertices,indices,e.material,values);
                    if(scene)continue;
                    DrawSurfaceRoles(bind,e.slot,camera,e.renderer,e.material,mesh,vertices,indices,values);
                }
                if(bind?.Visible!=false && e.renderer.shadowCastingMode!=ShadowCastingMode.Off){
                    for(int p=0;p<e.material.passCount;p++){
                        var tag=e.material.shader.FindPassTagValue(p,new ShaderTagId("LightMode")).name;
                        if(string.Equals(tag,"ShadowCaster",StringComparison.OrdinalIgnoreCase) && e.material.GetShaderPassEnabled("ShadowCaster"))
                            shadowSurfaces.DrawRenderer(e.renderer,e.material,e.index,p);
                    }
                }
                if(!hidden.TryGetValue(e.renderer,out var replacements))hidden.Add(e.renderer,replacements=e.renderer.sharedMaterials);
                if(silentSurface==null)silentSurface=new Material(Resources.Load<Shader>("SurfaceMask"));
                replacements[e.index]=silentSurface;
            }
            foreach(var prepare in preEffects)prepare();
            for(int i=postEffects.Count-1;i>=0;i--)postEffects[i]();
            foreach(var e in hidden){temporarySlots[e.Key]=e.Key.sharedMaterials;e.Key.sharedMaterials=e.Value;}
            foreach(var property in captured)surfaceCommands.ReleaseTemporaryRT(property);
            ReleaseUnusedTargets();
        }
        void DrawUnitySurface(CommandBuffer commands,Renderer renderer,int slot,Material material,HashSet<int> captured)
        {
            var mode=new ShaderTagId("LightMode");
            var grabs=shaderLibrary.GrabPasses(material.shader);
            var profile=shaderLibrary.Passes(material.shader);
            for(int pass=0;pass<material.passCount;pass++)
            {
                if(grabs!=null)foreach(var grab in grabs.Where(g=>g.beforePass==pass))
                {
                    bool named=!string.IsNullOrEmpty(grab.texture);int property=Shader.PropertyToID(named?grab.texture:"_GrabTexture");
                    bool first=captured.Add(property);if(named && !first)continue;
                    if(first)commands.GetTemporaryRT(property,-1,-1,0,FilterMode.Bilinear,RenderTextureFormat.Default);
                    commands.Blit(BuiltinRenderTextureType.CameraTarget,property);
                    commands.SetGlobalTexture(property,new RenderTargetIdentifier(property));
                    commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                }
                if(profile!=null && pass<profile.Length && profile[pass].grab || grabs?.Any(g=>g.beforePass==pass)==true)continue;
                string kind=material.shader.FindPassTagValue(pass,mode).name;
                if(string.IsNullOrEmpty(kind) && profile!=null && pass<profile.Length)kind=profile[pass].lightMode;
                if(!string.IsNullOrEmpty(kind) && !material.GetShaderPassEnabled(kind))continue;
                switch(kind?.ToUpperInvariant()){
                    case "FORWARDADD":DrawAdditionalLights(commands,renderer,material,slot,pass);break;
                    case null:case "":case "ALWAYS":case "FORWARDBASE":case "VERTEX":case "SRPDEFAULTUNLIT":
                        commands.DrawRenderer(renderer,material,slot,pass);break;
                }
            }
        }

        void UpdateSurfacePointer()
        {
            if(previewImage?.panel==null)return;
            var bounds=previewImage.worldBound;
            if(bounds.width<=0 || bounds.height<=0)return;
            var pos=RuntimePanelUtils.ScreenToPanel(previewImage.panel,new Vector2(Input.mousePosition.x,Screen.height-Input.mousePosition.y));
            surfacePointer=new Vector2(2*(pos.x-bounds.xMin)/bounds.width-1,1-2*(pos.y-bounds.yMin)/bounds.height);
            for(int i=0;i<3;i++){
                int button=i==1?2:i==2?1:0;
                if(Input.GetMouseButton(button)){
                    if(surfaceButtons[i].z==0 && bounds.Contains(pos))surfaceButtons[i]=new Vector4(surfacePointer.x,surfacePointer.y,1,renderSeconds);
                }else surfaceButtons[i].z=0;
            }
        }

        const int SurfaceVertexStride=33;
        static void StoreVertex(float[] output,int start,Vector4 value,int components=4)
        {
            for(int j=0;j<components;j++)output[start+j]=value[j];
        }
        static float[] SurfaceVertices(Mesh mesh)
        {
            var positions=mesh.vertices;var normals=mesh.normals;var tangents=mesh.tangents;var colors=mesh.colors;
            var output=new float[positions.Length*SurfaceVertexStride];
            for(int i=0;i<positions.Length;i++)
            {
                int at=i*SurfaceVertexStride;
                var normal=i<normals.Length?normals[i]:Vector3.forward;
                var tangent=i<tangents.Length?tangents[i]:new Vector4(1,0,0,1);
                StoreVertex(output,at,positions[i],3);StoreVertex(output,at+3,normal,3);
                StoreVertex(output,at+6,tangent);StoreVertex(output,at+10,i<colors.Length?colors[i]:Color.white);
                StoreVertex(output,at+30,Vector3.Cross(normal,(Vector3)tangent)*tangent.w,3);
            }
            var values=new List<Vector4>(positions.Length);
            for(int channel=0;channel<4;channel++){
                mesh.GetUVs(channel,values);
                for(int i=0;i<values.Count;i++){
                    var uv=values[i];if(channel==0)uv.y=1-uv.y;
                    StoreVertex(output,i*SurfaceVertexStride+14+4*channel,uv);
                }
            }
            return output;
        }
        float[] SurfaceConstants(Camera camera,Matrix4x4 world,Material material,Mesh mesh,Matrix4x4? view=null,Matrix4x4? projection=null)
        {
            var logical=EffectWorld;
            var cameraScale=Matrix4x4.Scale(new Vector3(1,1,view.HasValue?1:-1)/SceneUnit);
            var v=cameraScale*(view??camera.worldToCameraMatrix)*logical.inverse;
            var p=(projection??GL.GetGPUProjectionMatrix(camera.projectionMatrix,true))*cameraScale.inverse;
            p.SetRow(1,-p.GetRow(1));
            if(SystemInfo.usesReversedZBuffer)p.SetRow(2,p.GetRow(3)-p.GetRow(2));
            if(!view.HasValue && !camera.orthographic){
                float clipScale=p.m32;if(Mathf.Abs(clipScale)>1e-6f)for(int i=0;i<16;i++)p[i]/=clipScale;
            }
            world=logical*world;
            var values=new float[472];int at=0;
            foreach(var matrix in new[]{world,v,p,p*v*world,v*world,p*v})
                for(int i=0;i<16;i++)values[at++]=matrix[i];
            var color=material.HasProperty("_Color")?material.color:Color.white;
            for(int i=0;i<4;i++)values[96+i]=color[i];
            values[100]=renderSeconds;values[101]=renderDelta;
            values[102]=camera.targetTexture.width;values[103]=camera.targetTexture.height;
            var eye=logical.MultiplyPoint3x4(camera.transform.position);var light=previewLamp.transform.forward;
            for(int i=0;i<3;i++){values[104+i]=eye[i];values[108+i]=light[i];}
            values[107]=1;values[112]=syncedSeconds;values[113]=syncedDelta;
            at=116;
            foreach(var matrix in new[]{world,v,p,p*v*world,v*world,p*v})
            {
                var inverse=matrix.inverse;
                foreach(var variant in new[]{inverse,matrix.transpose,inverse.transpose})
                    for(int i=0;i<16;i++)values[at++]=variant[i];
            }
            void ColorValue(int index,Color value){for(int i=0;i<4;i++)values[index+i]=value[i];}
            ColorValue(404,RenderSettings.ambientLight);ColorValue(408,material.HasProperty("_EmissionColor")?material.GetColor("_EmissionColor"):Color.black);
            ColorValue(412,material.HasProperty("_SpecColor")?material.GetColor("_SpecColor"):Color.black);
            ColorValue(416,material.HasProperty("_OutlineColor")?material.GetColor("_OutlineColor"):Color.black);
            ColorValue(420,viewOptions.shadowTint);ColorValue(424,lightTrack.Current.Color);
            ColorValue(428,Color.white);values[432]=material.HasProperty("_Shininess")?material.GetFloat("_Shininess"):0;
            if(accessoryMaterials.TryGetValue(material,out var source)){
                ColorValue(404,source.ambient);ColorValue(408,source.emissive);ColorValue(412,source.specular);values[432]=source.power;
            }
            var lamp=logical.MultiplyPoint3x4(previewLamp.transform.position);
            var cameraDirection=logical.MultiplyVector(camera.transform.forward).normalized;
            for(int i=0;i<3;i++){values[436+i]=cameraDirection[i];values[440+i]=lamp[i];}
            values[443]=1;
            // HLSL bool and int constant-buffer fields use 32-bit integer storage.
            values[444]=BitConverter.Int32BitsToSingle(shadowTrack.Current.Mode==2?1:0);
            values[445]=BitConverter.Int32BitsToSingle(viewOptions.translucent?1:0);
            values[446]=BitConverter.Int32BitsToSingle(material.HasProperty("_MainTex") && material.GetTexture("_MainTex")!=null?1:0);
            values[447]=BitConverter.Int32BitsToSingle(mesh.vertexCount);values[448]=BitConverter.Int32BitsToSingle(mesh.subMeshCount);
            values[449]=surfacePointer.x;values[450]=surfacePointer.y;
            for(int i=0;i<3;i++)StoreVertex(values,452+4*i,surfaceButtons[i]);
            bool adding=TextureBound(material,"_SphereAdd"),sphere=adding || TextureBound(material,"_SphereMul");
            values[464]=BitConverter.Int32BitsToSingle(adding?1:0);values[465]=BitConverter.Int32BitsToSingle(sphere?1:0);
            bool toon=TextureBound(material,"_ToonTex");
            values[466]=BitConverter.Int32BitsToSingle(toon?1:0);
            values[467]=BitConverter.Int32BitsToSingle(material.HasProperty("_DstBlend") && material.GetInt("_DstBlend")==1?1:0);
            ColorValue(468,toon?SurfaceToonColor(material.GetTexture("_ToonTex")):Color.white);
            return values;
        }
    }
}
