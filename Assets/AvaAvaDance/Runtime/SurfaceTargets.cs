using System;
using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly Dictionary<string,RenderTexture> surfaceTargets=new Dictionary<string,RenderTexture>();
        readonly HashSet<string> usedTargets=new HashSet<string>();
        readonly HashSet<byte[]> usedImages=new HashSet<byte[]>();
        readonly Dictionary<(byte[] bytes,string dimension,bool mips),Texture> surfaceImages=new Dictionary<(byte[],string,bool),Texture>();
        readonly Dictionary<byte[],SurfaceAnimation> surfaceAnimations=new Dictionary<byte[],SurfaceAnimation>();
        static readonly float[] BufferVertices=FullscreenVertices();
        static float[] FullscreenVertices()
        {
            var data=new float[4*SurfaceVertexStride];
            for(int i=0;i<4;i++){
                int at=i*SurfaceVertexStride;float x=i>=2?1:-1,y=i==0 || i==3?-1:1;
                StoreVertex(data,at,new Vector3(x,y,0),3);StoreVertex(data,at+3,Vector3.back,3);
                StoreVertex(data,at+6,new Vector4(1,0,0,1));StoreVertex(data,at+10,Vector4.one);
                StoreVertex(data,at+14,new Vector4((x+1)/2,(1-y)/2,0,0));StoreVertex(data,at+30,Vector3.down,3);
            }
            return data;
        }
        static readonly int[] BufferIndices={0,1,2,0,2,3};

        readonly Dictionary<(string name,string semantic,string type),(SurfaceTexture spec,RenderTexture output)> sharedTargetSpecs
            =new Dictionary<(string,string,string),(SurfaceTexture,RenderTexture)>();
        void DefineSharedTargets(Camera camera,IEnumerable<MaterialSlot> materials)
        {
            sharedTargetSpecs.Clear();
            int Detail(SurfaceTexture s)=>(s.Format!=null?4:0)+(s.Width>0 || s.Height>0 || s.RatioX!=1 || s.RatioY!=1?2:0)+(s.Levels!=1 || s.AntiAlias?1:0);
            foreach(var slot in materials){
                var plan=surfaceProviders.Select(p=>p.Resolve(slot)).FirstOrDefault(b=>b!=null)?.Plan;
                if(plan==null)continue;
                foreach(var texture in plan.Textures.Where(t=>t.Shared && t.Semantic.Contains("TARGET"))){
                    var key=(texture.Name,texture.Semantic,texture.Dimension);
                    if(!sharedTargetSpecs.TryGetValue(key,out var current) || Detail(texture)>Detail(current.spec))
                        sharedTargetSpecs[key]=(texture,camera.targetTexture);
                }
            }
        }

        RenderTexture SurfaceTarget(SurfaceTexture spec,string instance,RenderTexture output)
        {
            if(spec.Shared && sharedTargetSpecs.TryGetValue((spec.Name,spec.Semantic,spec.Dimension),out var definition)){
                spec=definition.spec;output=definition.output;
            }
            int width=spec.Width>0?spec.Width:Mathf.RoundToInt(output.width*spec.RatioX);
            int height=spec.Height>0?spec.Height:Mathf.RoundToInt(output.height*spec.RatioY);
            width=Mathf.Clamp(width,1,SystemInfo.maxTextureSize);height=Mathf.Clamp(height,1,SystemInfo.maxTextureSize);
            var name=(spec.Format??"").ToUpperInvariant();
            var format=spec.Semantic=="RENDERDEPTHSTENCILTARGET"?RenderTextureFormat.Depth:name switch {
                "R32F"=>RenderTextureFormat.RFloat,"G32R32F"=>RenderTextureFormat.RGFloat,"A32B32G32R32F"=>RenderTextureFormat.ARGBFloat,
                "R16F"=>RenderTextureFormat.RHalf,"G16R16F"=>RenderTextureFormat.RGHalf,"A16B16G16R16F"=>RenderTextureFormat.ARGBHalf,
                "A16B16G16R16"=>RenderTextureFormat.ARGB64,"A2B10G10R10" or "A2R10G10B10"=>RenderTextureFormat.ARGB2101010,
                "R5G6B5"=>RenderTextureFormat.RGB565,"A4R4G4B4"=>RenderTextureFormat.ARGB4444,"A1R5G5B5"=>RenderTextureFormat.ARGB1555,_=>RenderTextureFormat.ARGB32};
            var desc=new RenderTextureDescriptor(width,height,format,name=="D16"?16:24){useMipMap=spec.Levels!=1 && format!=RenderTextureFormat.Depth,
                autoGenerateMips=spec.Levels!=1 && format!=RenderTextureFormat.Depth,msaaSamples=spec.AntiAlias?Mathf.Max(1,QualitySettings.antiAliasing):1};
            int maxLevels=1+Mathf.FloorToInt(Mathf.Log(Mathf.Max(width,height),2));
            desc.mipCount=desc.useMipMap?(spec.Levels<=0?maxLevels:Mathf.Clamp(spec.Levels,1,maxLevels)):1;
            desc.msaaSamples=SystemInfo.GetRenderTextureSupportedMSAASampleCount(desc);
            string key=(spec.Shared?"shared/"+spec.Semantic+"/"+spec.Dimension:instance)+"/"+spec.Name+"/"+width+"x"+height+"/"+format+"/"+desc.mipCount+"/"+desc.msaaSamples+"/"+desc.depthBufferBits;
            usedTargets.Add(key);
            if(surfaceTargets.TryGetValue(key,out var target)){
                if(target!=null && target.IsCreated())return target;
                if(target!=null){surfaceTexels.Remove(target);Destroy(target);}
                surfaceTargets.Remove(key);
            }
            target=new RenderTexture(desc){name=spec.Name};
            if(!target.Create()){Destroy(target);throw new InvalidOperationException("描画ターゲットを作成できません: "+spec.Name);}
            initSurfaceCommands.SetRenderTarget(target);
            initSurfaceCommands.ClearRenderTarget(true,format!=RenderTextureFormat.Depth,Color.clear,1);
            initSurfaceCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            surfaceTargets.Add(key,target);return target;
        }
        void ReleaseUnusedTargets()
        {
            foreach(var key in surfaceAnimations.Keys.Where(k=>!usedImages.Contains(k)).ToArray()){surfaceAnimations[key].Dispose();surfaceAnimations.Remove(key);}
            foreach(var key in surfaceImages.Keys.Where(k=>!usedImages.Contains(k.bytes)).ToArray()){Destroy(surfaceImages[key]);surfaceImages.Remove(key);}
            foreach(var key in surfaceTargets.Keys.Where(k=>!usedTargets.Contains(k)).ToArray())
            {Destroy(surfaceTargets[key]);surfaceTargets.Remove(key);}
        }
        void DisposeSurfaceTargets()
        {
            sharedTargetSpecs.Clear();surfaceTexels.Clear();usedImages.Clear();usedTargets.Clear();ReleaseUnusedTargets();foreach(var tex in surfaceImages.Values)Destroy(tex);surfaceImages.Clear();
        }

        readonly Dictionary<Texture,Color[]> surfaceTexels=new Dictionary<Texture,Color[]>();
        long texelDocument=-1;
        float texelSample=float.NaN;
        bool texelsForOutput;
        sealed class ToonSample { public Color Color; }
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture,ToonSample> toonSamples=new System.Runtime.CompilerServices.ConditionalWeakTable<Texture,ToonSample>();
        Color SurfaceToonColor(Texture texture)
        {
            if(toonSamples.TryGetValue(texture,out var known))return known.Color;
            var sample=new ToonSample();
            if(texture is Texture2D source && source.isReadable)sample.Color=source.GetPixel(0,0);
            else{
                var prior=RenderTexture.active;var rt=RenderTexture.GetTemporary(texture.width,texture.height,0,RenderTextureFormat.ARGB32);
                var pixel=new Texture2D(1,1,TextureFormat.RGBA32,false);
                try{
                    Graphics.Blit(texture,rt);RenderTexture.active=rt;
                    pixel.ReadPixels(new Rect(0,0,1,1),0,0,false);sample.Color=pixel.GetPixel(0,0);
                }finally{RenderTexture.active=prior;RenderTexture.ReleaseTemporary(rt);Destroy(pixel);}
            }
            toonSamples.Add(texture,sample);return sample.Color;
        }
        Color[] ReadSurfaceTexels(Texture source,int count)
        {
            if(source==null || source.width==0 || source.height==0)return Array.Empty<Color>();
            if(texelDocument!=documentGeneration || texelSample!=evaluationTime || texelsForOutput!=exporting){
                surfaceTexels.Clear();texelDocument=documentGeneration;texelSample=evaluationTime;texelsForOutput=exporting;
            }
            count=Math.Min(Math.Max(4096,count),source.width*source.height);
            if(surfaceTexels.TryGetValue(source,out var stored) && stored.Length>=count)return stored;
            int width=source.width,height=Math.Min(source.height,Mathf.CeilToInt(count/(float)width));
            var prior=RenderTexture.active;RenderTexture temporary=null;Texture2D copy=null;
            try{
                var rt=source as RenderTexture;
                if(rt==null){temporary=RenderTexture.GetTemporary(width,source.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);Graphics.Blit(source,temporary);rt=temporary;}
                RenderTexture.active=rt;copy=new Texture2D(width,height,TextureFormat.RGBAFloat,false,true);
                copy.ReadPixels(new Rect(0,source.height-height,width,height),0,0,false);
                var read=copy.GetPixels();stored=new Color[read.Length];
                for(int y=0;y<height;y++)Array.Copy(read,(height-1-y)*width,stored,y*width,width);
                surfaceTexels[source]=stored;return stored;
            }finally{RenderTexture.active=prior;if(temporary!=null)RenderTexture.ReleaseTemporary(temporary);if(copy!=null)Destroy(copy);}
        }
        void QueueSurface(SurfaceBinding binding,MaterialSlot slot,Camera camera,float[] vertices,int[] indices,Material material,float[] constants,RenderTexture destination=null,bool postPrefix=false,bool postSuffix=false,CommandBuffer commandOverride=null)
        {
            var plan=binding.Plan;
            var commands=commandOverride??(postPrefix || destination!=null || plan?.Order=="preprocess"?preSurfaceCommands:plan?.Order=="postprocess"?postSurfaceCommands:surfaceCommands);
            var targets=new Dictionary<string,RenderTexture>(StringComparer.Ordinal);
            var textures=new List<Texture>();
            var animations=new Dictionary<int,(SurfaceTexture spec,SurfaceAnimation image)>();
            Texture diffuse=material.HasProperty("_MainTex")?material.GetTexture("_MainTex"):null;
            foreach(var resource in plan?.Textures??Array.Empty<SurfaceTexture>())
            {
                Texture texture=null;
                if(resource.Semantic.Contains("TARGET"))
                {
                    var rt=SurfaceTarget(resource,binding.Instance??slot.Id,destination!=null?destination:camera.targetTexture);targets.Add(resource.Name,rt);texture=rt;
                }
                else if(resource.Data!=null && resource.Semantic=="ANIMATEDTEXTURE"){
                    usedImages.Add(resource.Data);
                    if(!surfaceAnimations.TryGetValue(resource.Data,out var animation)){
                        animation=SurfaceAnimation.Load(resource.Data,resource.Levels!=1);surfaceAnimations.Add(resource.Data,animation);
                    }
                    animations.Add(textures.Count,(resource,animation));
                }
                else if(resource.Data!=null){
                    usedImages.Add(resource.Data);
                    var imageKey=(resource.Data,resource.Dimension,resource.Levels!=1);
                    if(!surfaceImages.TryGetValue(imageKey,out var image)){
                        image=Import.DirectXImage.ReadSurface(resource.Data,resource.Dimension,resource.Levels!=1);
                        surfaceImages.Add(imageKey,image);
                    }
                    texture=image;
                }
                else if(resource.Semantic=="MATERIALTOONTEXTURE")texture=TextureBound(material,"_ToonTex")?material.GetTexture("_ToonTex"):Texture2D.whiteTexture;
                else if(resource.Semantic=="MATERIALSPHEREMAP")texture=TextureBound(material,"_SphereAdd")?material.GetTexture("_SphereAdd"):TextureBound(material,"_SphereMul")?material.GetTexture("_SphereMul"):null;
                else if(resource.Semantic=="MATERIALTEXTURE")texture=diffuse!=null?diffuse:Texture2D.whiteTexture;
                textures.Add(texture);
            }
            if(textures.Count==0)textures.Add(diffuse);
            IntPtr deviceSource=camera.targetTexture.GetNativeTexturePtr();
            var cameraTarget=destination!=null?new RenderTargetIdentifier(destination):new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            var colors=new RenderTexture[4];RenderTexture depth=null;
            var parameters=new Dictionary<string,float[]>(StringComparer.Ordinal);
            bool targetChanged=false,pendingTarget=false,emit=!postSuffix;
            void BindTargets()
            {
                if(!emit || !pendingTarget)return;
                pendingTarget=false;
                int n=colors.Length;while(n>1 && colors[n-1]==null)n--;
                if(n==1 && colors[0]==null && depth==null)commands.SetRenderTarget(cameraTarget);
                else {
                    var active=new RenderTargetIdentifier[n];
                    for(int i=0;i<n;i++)active[i]=colors[i]!=null?new RenderTargetIdentifier(colors[i]):i==0?cameraTarget:new RenderTargetIdentifier(BuiltinRenderTextureType.None);
                    var color=colors[0]!=null?colors[0]:destination!=null?destination:camera.targetTexture;
                    bool compatible=depth!=null && depth.width==color.width && depth.height==color.height && depth.antiAliasing==color.antiAliasing;
                    var z=compatible?new RenderTargetIdentifier(depth):active[0];
                    commands.SetRenderTarget(active,z);
                }
                targetChanged=true;
            }
            void Draw(int passIndex,bool buffer)
            {
                BindTargets();
                foreach(var item in animations){
                    var spec=item.Value.spec;
                    double time=syncedSeconds;
                    if(!string.IsNullOrEmpty(spec.SeekVariable)){
                        if(parameters.TryGetValue(spec.SeekVariable,out var value))time=value[0];
                        else if(spec.SeekControl!=null)time=ControlValue(slot,spec.SeekControl)[0];
                        else time=spec.SeekSemantic switch {"TIME"=>renderSeconds,"ELAPSEDTIME"=>renderDelta,
                            "SYNCTIME"=>syncedSeconds,"SYNCELAPSEDTIME"=>syncedDelta,_=>spec.SeekDefault};
                    }
                    textures[item.Key]=item.Value.image.At(time*spec.Speed+spec.Offset);
                }
                var pass=binding.Passes[passIndex];var streams=textures.Select(t=>t!=null && !colors.Any(c=>c!=null && t==c) && t!=depth?t.GetNativeTexturePtr():IntPtr.Zero).ToArray();
                var data=constants;
                if(pass.Controls.Length>0)
                {
                    data=new float[constants.Length+16*pass.Controls.Length];Array.Copy(constants,data,constants.Length);
                    for(int i=0;i<pass.Controls.Length;i++)
                    {
                        var control=pass.Controls[i];int at=constants.Length+i*16;
                        if(control==null)continue;
                        if(control.Texture!=null){
                            int textureIndex=Array.FindIndex(plan.Textures,t=>t.Name==control.Texture);
                            if(textureIndex<0)continue;
                            var rgba=ReadSurfaceTexels(textures[textureIndex],control.Texels);
                            for(int j=0;j<Math.Min(rgba.Length,control.Texels);j++)StoreVertex(data,at+4*j,rgba[j]);
                        }
                        else if(control.Parameter==null)Array.Copy(ControlValue(slot,control),0,data,at,16);
                        else if(parameters.TryGetValue(control.Parameter,out var val))data[at]=control.Type=="int"?BitConverter.Int32BitsToSingle((int)val[0]):val[0];
                    }
                }
                var verts=buffer?BufferVertices:vertices;var tris=buffer?BufferIndices:indices;
                int cull=1;
                if(!buffer){
                    if(material.HasProperty("_Cull"))cull=1+Mathf.Clamp(material.GetInt("_Cull"),0,2);
                    else if(string.Equals(System.IO.Path.GetExtension(slot.SourceFile),".x",StringComparison.OrdinalIgnoreCase))cull=3;
                }
                int flags=(cull<<1)|(buffer?1:0)|(SystemInfo.usesReversedZBuffer?8:0);
                var packet=ava_fx_packet(pass.Handle,deviceSource,streams,streams.Length,verts,verts.Length/SurfaceVertexStride,tris,tris.Length,data,data.Length,flags);
                if(packet!=IntPtr.Zero){submittedPackets.Add(packet);commands.IssuePluginEventAndData(ava_fx_event(),0,packet);}
            }
            if(plan==null){for(int i=0;i<binding.Passes.Length;i++)Draw(i,false);return;}
            foreach(var step in plan.Steps)
            {
                if(step.Operation==SurfaceOperation.SceneColor){
                    if(postPrefix){
                        // Continue scene rendering over the color prepared by this outer pass.
                        // The camera draws its geometry between the two command buffers.
                        if(colors[0]!=null){
                            commands.Blit(colors[0],cameraTarget);
                            commands.SetRenderTarget(cameraTarget);
                        }
                        break;
                    }
                    emit=true;
                    if(colors[0]!=null){commands.Blit(cameraTarget,colors[0]);pendingTarget=true;BindTargets();}
                    continue;
                }
                if(!emit && step.Operation!=SurfaceOperation.Target && step.Operation!=SurfaceOperation.Depth && step.Operation!=SurfaceOperation.Parameter)continue;
                switch(step.Operation)
                {
                    case SurfaceOperation.Target:colors[step.Slot]=string.IsNullOrEmpty(step.Name)?null:targets[step.Name];pendingTarget=true;break;
                    case SurfaceOperation.Depth:depth=string.IsNullOrEmpty(step.Name)?null:targets[step.Name];pendingTarget=true;break;
                    case SurfaceOperation.ClearColor:BindTargets();var c=step.Value;commands.ClearRenderTarget(false,true,new Color(c[0],c[1],c[2],c[3]));break;
                    case SurfaceOperation.ClearDepth:BindTargets();commands.ClearRenderTarget(RTClearFlags.Depth,Color.clear,step.Value[0],0);break;
                    case SurfaceOperation.ClearStencil:BindTargets();commands.ClearRenderTarget(RTClearFlags.Stencil,Color.clear,1,(uint)Mathf.Clamp(step.Value[0],0,255));break;
                    case SurfaceOperation.Parameter:parameters[step.Name]=step.Value;break;
                    case SurfaceOperation.Buffer:Draw(step.Pass,true);break;
                    case SurfaceOperation.Geometry:Draw(step.Pass,false);break;
                }
            }
            if(targetChanged)commands.SetRenderTarget(cameraTarget);
        }
    }
}
