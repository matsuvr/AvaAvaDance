using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static string DefaultRule(MaterialSlot owner,MaterialSlot candidate,string rules)
        {
            if(string.IsNullOrEmpty(rules))return null;
            foreach(string entry in rules.Split(';'))
            {
                int at=entry.IndexOf('=');if(at<0)continue;
                string pattern=entry.Substring(0,at).Trim(),effect=entry.Substring(at+1).Trim();
                bool matches=pattern.Equals("self",StringComparison.OrdinalIgnoreCase)?candidate.ActorId==owner.ActorId:
                    Regex.IsMatch(Path.GetFileName(candidate.SourceFile??candidate.ActorName),"^"+
                        Regex.Escape(pattern).Replace("\\*",".*").Replace("\\?",".")+"$",RegexOptions.IgnoreCase);
                if(matches)return effect;
            }
            return null;
        }
        sealed class OffscreenPass
        {
            public RenderTexture target;
            public SurfaceTexture definition;
            public readonly HashSet<RenderTexture> reads=new HashSet<RenderTexture>(),writes=new HashSet<RenderTexture>();
            public readonly List<(Renderer renderer,int index,MaterialSlot slot,SurfaceBinding binding)> draws=
                new List<(Renderer,int,MaterialSlot,SurfaceBinding)>();
        }

        void GatherSurfaceDependencies(OffscreenPass stage,SurfaceBinding bind,MaterialSlot slot)
        {
            if(bind?.Plan==null || bind.Passes==null)return;
            var plan=bind.Plan;
            var outputs=new RenderTexture[4];outputs[0]=stage.target;
            var resources=new RenderTexture[plan.Textures.Length];
            for(int i=0;i<resources.Length;i++){
                var t=plan.Textures[i];
                if(t.Semantic.Contains("TARGET"))resources[i]=SurfaceTarget(t,bind.Instance??slot.Id,stage.target);
            }
            foreach(var step in plan.Steps){
                if(step.Operation==SurfaceOperation.Target){
                    int idx=Array.FindIndex(plan.Textures,t=>t.Name==step.Name);
                    outputs[step.Slot]=idx>=0?resources[idx]:step.Slot==0?stage.target:null;
                }else if(step.Operation==SurfaceOperation.Buffer || step.Operation==SurfaceOperation.Geometry){
                    foreach(int i in bind.Passes[step.Pass].TextureInputs)
                        if(i<resources.Length && resources[i]!=null && !outputs.Contains(resources[i]))stage.reads.Add(resources[i]);
                    foreach(var output in outputs)if(output!=null)stage.writes.Add(output);
                }else if(step.Operation==SurfaceOperation.ClearColor){
                    foreach(var output in outputs)if(output!=null)stage.writes.Add(output);
                }
            }
        }

        void DrawOffscreen(OffscreenPass stage,Camera camera)
        {
            var rt=stage.target;var color=stage.definition.ClearColor;
            preSurfaceCommands.SetRenderTarget(rt);
            preSurfaceCommands.ClearRenderTarget(true,true,color?.Length==4?new Color(color[0],color[1],color[2],color[3]):Color.clear,stage.definition.ClearDepth);
            foreach(var e in stage.draws){
                var material=e.renderer.sharedMaterials[e.index];
                if(e.binding?.Passes==null){
                    int pass=material.FindPass("FORWARD");if(pass<0)pass=material.FindPass("ForwardBase");
                    preSurfaceCommands.DrawRenderer(e.renderer,material,e.index,Mathf.Max(0,pass));continue;
                }
                Mesh mesh;
                if(e.renderer is SkinnedMeshRenderer skin){if(bakedSurface==null)bakedSurface=new Mesh();skin.BakeMesh(bakedSurface,true);mesh=bakedSurface;}
                else mesh=e.renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if(mesh==null || !mesh.isReadable || e.index>=mesh.subMeshCount)continue;
                var values=SurfaceConstants(camera,e.renderer.localToWorldMatrix,material,mesh);values[102]=rt.width;values[103]=rt.height;
                QueueSurface(e.binding,e.slot,camera,SurfaceVertices(mesh),mesh.GetIndices(e.index),material,values,rt);
            }
            preSurfaceCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }

        void ScheduleOffscreen(Camera camera,List<(Renderer renderer,int index,MaterialSlot slot)> slots)
        {
            var prepared=new HashSet<RenderTexture>();var pending=new List<OffscreenPass>();
            var plans=slots.Select(e=>(owner:e.slot,bind:surfaceProviders.Select(p=>p.Resolve(e.slot)).FirstOrDefault(b=>b!=null)))
                .Where(e=>e.bind?.Plan!=null && e.bind.Visible);
            foreach(var item in plans)
            {
                foreach(var target in item.bind.Plan.Textures.Where(t=>t.Semantic=="OFFSCREENRENDERTARGET"))
                {
                    var rt=SurfaceTarget(target,item.bind.Instance,camera.targetTexture);
                    if(!prepared.Add(rt))continue;
                    var stage=new OffscreenPass{target=rt,definition=target};stage.writes.Add(rt);
                    pending.Add(stage);
                    foreach(var e in slots)
                    {
                        var renderer=e.renderer;
                        if(!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)continue;
                        var sub=new MaterialSlot{Id=e.slot.Id,ActorId=e.slot.ActorId,ActorName=e.slot.ActorName,
                            SourceFile=e.slot.SourceFile,MaterialName=e.slot.MaterialName,Subset=e.slot.Subset,Textured=e.slot.Textured,SphereMapped=e.slot.SphereMapped,Toon=e.slot.Toon,SelfShadow=e.slot.SelfShadow,Target=item.bind.Instance+"/"+target.Name,
                            DefaultEffect=DefaultRule(item.owner,e.slot,target.DefaultEffect),OwnerSlot=item.owner.Id};
                        var bind=surfaceProviders.Select(p=>p.Resolve(sub)).FirstOrDefault(b=>b!=null);
                        if(bind?.Visible==false || bind==null && string.Equals(sub.DefaultEffect,"hide",StringComparison.OrdinalIgnoreCase))continue;
                        stage.draws.Add((renderer,e.index,sub,bind));
                        GatherSurfaceDependencies(stage,bind,sub);
                    }
                }
            }
            while(pending.Count>0){
                int index=pending.FindIndex(stage=>!pending.Any(other=>other!=stage && stage.reads.Overlaps(other.writes)));
                // Feedback cycles retain declaration order for the previous-frame inputs.
                if(index<0)index=0;
                var next=pending[index];pending.RemoveAt(index);DrawOffscreen(next,camera);
            }
        }
    }
}
