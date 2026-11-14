using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        System.Collections.IEnumerator CheckWorkspaceLayout(string directory,Action<string> complete)
        {
            bool priorProfile=ribbonProfile,priorMode=modelMode;
            float priorScale=panel.scale;
            var priorTarget=panel.targetTexture;
            var scroll=MenuRoot.Q<ScrollView>();
            var priorOffset=scroll.scrollOffset;
            var report=new System.Text.StringBuilder();
            string error=null;
            int cases=0;
            try
            {
                foreach(var size in new[]{(1280,900,1f),(1920,1080,1.5f),(2160,1420,1.5f),(2100,700,1f),
                    (900,1400,1f),(900,900,1f),(640,480,1f),(1280,900,2f),(1024,720,1f),(1280,900,1f)})
                {
                    var target=new RenderTexture(size.Item1,size.Item2,24,RenderTextureFormat.ARGB32);
                    target.Create();panel.targetTexture=target;panel.scale=size.Item3;
                    try
                    {
                        foreach(var mode in new[]{(false,false),(false,true),(true,false),(true,true),(false,false),(false,true)})
                        {
                            bool moving=mode.Item1,model=mode.Item2;
                            ApplyProfile(moving);ChangeMode(model);
                            for(int i=0;i<6;i++)yield return null;
                            for(int i=0;i<6;i++){scroll.verticalScroller.value=scroll.verticalScroller.highValue;yield return null;}
                            yield return new WaitForEndOfFrame();
                            try
                            {
                                void Check(bool pass,string message){if(!pass)throw new InvalidOperationException(message);}
                                var area=sharedBottom.worldBound;
                                var root=MenuRoot.worldBound;
                                float horizontal=scroll.horizontalScroller.resolvedStyle.display==DisplayStyle.None?0:scroll.horizontalScroller.layout.height;
                                Check(Mathf.Abs(root.width-size.Item1/size.Item3)<1 && Mathf.Abs(root.height-size.Item2/size.Item3)<1,"Unexpected panel size: "+root+" expected="+size);
                                Check(Mathf.Abs(scroll.worldBound.yMax-root.yMax)<1,"Scroll view does not fill window height");
                                Check(Mathf.Abs(scroll.contentViewport.worldBound.yMax+horizontal-root.yMax)<1,"Content viewport leaves unused window space");
                                Check(MenuRoot.Q("workspace").worldBound.width>=scroll.contentViewport.worldBound.width-.5f,"Workspace does not fill view width");
                                Check(sharedBottom.parent==MenuRoot.Q(moving?"workspace":"right"),"Bottom panel parent");
                                Check(area.height>0 && area.yMax<=status.worldBound.yMin+.5f,"Bottom panel overlaps status");
                                var main=MenuRoot.Q("main").worldBound;
                                var transform=MenuRoot.Q("transform-bar").worldBound;
                                Check(Mathf.Abs(transform.height-32)<.5f,"Transform bar was compressed");
                                Check(viewport.worldBound.yMax<=transform.yMin+.5f,"Viewport overlaps transform bar");
                                Check(transform.yMax<=(moving?main.yMax:area.yMin)+.5f,"Transform bar overlaps bottom controls");
                                Check(main.yMax<=(moving?transport.worldBound.yMin:status.worldBound.yMin)+.5f,"Main area overlaps footer controls");
                                Check(Mathf.Abs(status.worldBound.height-22)<.5f,"Status height");
                                Check(status.worldBound.yMax<=scroll.contentViewport.worldBound.yMax+1,"Status cannot be scrolled into view: "+size+" profile="+moving+" model="+model+
                                    " status="+status.worldBound+" viewport="+scroll.contentViewport.worldBound+" offset="+scroll.scrollOffset+" high="+scroll.verticalScroller.highValue);
                                Check(status.worldBound.yMax>=root.yMax-horizontal-3,"Status does not reach bottom of window");
                                int controls=0;
                                foreach(var control in McpElements(sharedBottom).Where(e=>e is Button || e is BaseField<bool> ||
                                    e is BaseField<int> || e is BaseField<float> || e is BaseField<string>))
                                {
                                    var bounds=control.worldBound;
                                    Check(bounds.height>0 && bounds.yMin>=area.yMin-.5f && bounds.yMax<=area.yMax+.5f,
                                        "Control outside bottom panel: "+control.name+" "+ControlText(control)+" "+bounds+" panel="+area);
                                    Check(bounds.yMax<=status.worldBound.yMin+.5f,"Control covered by status: "+ControlText(control));
                                    controls++;
                                }
                                Check(controls>10 && modelMode==model,"Expected scene/model controls");
                                var sidebar=MenuRoot.Q("sidebar");
                                foreach(var control in McpElements(sidebar).OfType<Button>())
                                    Check(control.worldBound.yMax<=sidebar.worldBound.yMax+.5f,"Timeline control outside sidebar: "+control.text);
                                report.AppendLine(size+" profile="+moving+" model="+model+" controls="+controls+" root="+root+" viewport="+scroll.contentViewport.worldBound+" panel="+area+" status="+status.worldBound);
                                if(size.Item1==1920 || (size.Item1==2160 && !model) || (size.Item1==900 && !model))
                                {
                                    var previous=RenderTexture.active;
                                    var image=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
                                    try{
                                        RenderTexture.active=target;image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();
                                        File.WriteAllBytes(Path.Combine(directory,size.Item1+"x"+size.Item2+"-"+(moving?"ribbon":"classic")+(model?"-model":"-scene")+".png"),image.EncodeToPNG());
                                    }finally{RenderTexture.active=previous;Destroy(image);}
                                }
                                cases++;
                            }
                            catch(Exception ex){
                                error="FAIL "+ex;
                                foreach(var name in new[]{"workspace","main","right","sidebar"}){
                                    var element=MenuRoot.Q(name);var style=element.resolvedStyle;
                                    report.AppendLine(name+" "+element.worldBound+" height="+element.style.height+" min="+style.minHeight+" basis="+style.flexBasis+" grow="+style.flexGrow+" shrink="+style.flexShrink);
                                    foreach(var child in element.Children())report.AppendLine("  "+child.name+" "+string.Join(" ",child.GetClasses())+" "+child.worldBound+" display="+child.resolvedStyle.display);
                                }
                            }
                            if(error!=null)break;
                        }
                    }
                    finally{panel.targetTexture=priorTarget;target.Release();Destroy(target);}
                    if(error!=null)break;
                }
            }
            finally{
                panel.targetTexture=priorTarget;panel.scale=priorScale;ApplyProfile(priorProfile);ChangeMode(priorMode);scroll.scrollOffset=priorOffset;
                File.WriteAllText(Path.Combine(directory,"layout.txt"),report.ToString());
            }
            complete(error??"PASS workspace layout: "+cases+" scene/model, profile, window size and DPI combinations");
        }

        void CheckRigShapes(string path)
        {
            var live=actor.rigProfile;
            var file=System.Threading.Tasks.Task.Run(()=>RigFiles.Read(path)).GetAwaiter().GetResult().Single(r=>r.sourceId==live.sourceId);
            if(file.sourceHash!=live.sourceHash || file.shapes.Length!=live.shapes.Length || !file.shapes.Any(s=>s.shape=="box") || !file.shapes.Any(s=>s.shape=="sphere"))
                throw new InvalidDataException("リグ形状の種類またはソースが一致しません。");
            var crown=file.Find("headTop");var liveCrown=live.Find("headTop");
            if(crown?.origin!="weightedVertex" || liveCrown?.origin!="weightedVertex" || live.editRest.Length!=live.joints.Length || file.measurementVersion!=1 || live.measurementVersion!=1 ||
                Math.Abs(crown.x-liveCrown.x)+Math.Abs(crown.y-liveCrown.y)+Math.Abs(crown.z-liveCrown.z)>1e-5)throw new InvalidDataException("頭頂の測定位置が一致しません。");
            foreach(var a in file.shapes){
                var b=live.shapes.Single(s=>s.name==a.name && s.role==a.role);
                if(file.joints[a.jointIndex].name!=a.role || live.joints[b.jointIndex].name!=b.role || a.shape!=b.shape)
                    throw new InvalidDataException("形状のボーンが一致しません。");
                var x=a.center.Concat(a.axis??Array.Empty<double>()).Concat(a.boxAxes??Array.Empty<double>()).Append(a.radius).Append(a.height);
                var y=b.center.Concat(b.axis??Array.Empty<double>()).Concat(b.boxAxes??Array.Empty<double>()).Append(b.radius).Append(b.height);
                if(x.Zip(y,(v,w)=>Math.Abs(v-w)).Any(delta=>delta>1e-5))throw new InvalidDataException("形状の座標が一致しません。");
            }
        }

        void CheckSurfaceOutput(string dir)
        {
            var scope=extensions.Single(e=>e.manifest.id=="ava.mme");
            var slot=scope.Materials().First();
            const string fx="float Script:STANDARDSGLOBAL<string ScriptClass=\\\"sceneorobject\\\";>=0.8; "+
                "texture Tmp:RENDERCOLORTARGET<float2 ViewportRatio={0.5,0.5};>; "+
                "sampler2D ReadTmp=sampler_state{Texture=<Tmp>;}; "+
                "float4 Tint():SV_Target{return float4(1,0,1,1);} "+
                "float4 FromTmp(float2 uv:TEXCOORD0):SV_Target{return tex2D(ReadTmp,uv);} "+
                "technique Main<string Script=\\\"RenderColorTarget=Tmp;Pass=A;RenderColorTarget=;Pass=B;\\\";>{ "+
                "pass A<string Script=\\\"Draw=Buffer;\\\";>{PixelShader=compile ps_3_0 Tint();} pass B{PixelShader=compile ps_3_0 FromTmp();} }";
            string settings="{\"schemaVersion\":1,\"enabled\":true,\"watch\":false,\"rows\":[{\"slot\":\""+slot.Id+"\",\"visible\":true,\"mode\":\"effect\",\"source\":\""+fx+"\"}]}";
            scope.WriteState(System.Text.Encoding.UTF8.GetBytes(settings));documentGeneration++;
            string path=Path.Combine(dir,"surface.png");WritePreview(path);
            var image=new Texture2D(2,2);image.LoadImage(File.ReadAllBytes(path));
            try {
                if(!image.GetPixels32().Any(c=>c.r>220 && c.b>220 && c.g<30))
                {
                    var message=new System.Text.StringBuilder(1024);ava_fx_error(message,message.Capacity);
                    var active=RenderTexture.active;
                    try {
                        foreach(var rt in surfaceTargets.Values.Where(t=>t.format!=RenderTextureFormat.Depth))
                        {
                            var data=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false);RenderTexture.active=rt;
                            data.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);data.Apply();
                            File.WriteAllBytes(Path.Combine(dir,"target-"+rt.name+".png"),data.EncodeToPNG());Destroy(data);
                        }
                    } finally {RenderTexture.active=active;}
                    throw new InvalidOperationException("Surface pass pixels missing: "+status.text+" "+message);
                }
            }finally{Destroy(image);}
            string project=Path.Combine(dir,"effects.avadance");SaveProject(project);OpenProject(project);
            if(scope.ReadState()==null || !extraEntries.ContainsKey("extensions/index.json"))throw new Exception("Effect project state/index");
            var targetFx="texture Screen:OFFSCREENRENDERTARGET<float4 ClearColor={1,0,1,1};string DefaultEffect=\\\"*=hide;\\\";>; "+
                "sampler2D Scene=sampler_state{Texture=<Screen>;};float4 Paint(float2 uv:TEXCOORD0):SV_Target{return tex2D(Scene,uv);} "+
                "technique Main {pass P {PixelShader=compile ps_3_0 Paint();}}";
            string alternate=settings.Replace(fx,targetFx);
            scope.WriteState(System.Text.Encoding.UTF8.GetBytes(alternate));documentGeneration++;
            string offscreen=Path.Combine(dir,"offscreen.png");WritePreview(offscreen);
            var sample=new Texture2D(2,2);
            try{sample.LoadImage(File.ReadAllBytes(offscreen));if(!sample.GetPixels32().Any(c=>c.r>220 && c.g<30 && c.b>220))throw new Exception("Offscreen sampling");}
            finally{Destroy(sample);}
            scope.WriteState(null);documentGeneration++;
        }

        void CheckProfileOutput(string dir)
        {
            bool initial=ribbonProfile;var prior=previewCamera.targetTexture;float aspect=previewCamera.aspect;
            var rt=new RenderTexture(320,240,24,RenderTextureFormat.ARGB32);
            var texture=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false);
            byte[] Capture(bool ribbon,string name)
            {
                ApplyProfile(ribbon);previewCamera.targetTexture=rt;previewCamera.aspect=(float)rt.width/rt.height;
                previewCamera.Render();var old=RenderTexture.active;
                try{
                    RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);texture.Apply();
                    var png=texture.EncodeToPNG();File.WriteAllBytes(Path.Combine(dir,name+".png"),png);return png;
                }finally{RenderTexture.active=old;}
            }
            try{
                rt.Create();SetFrame(12);
                string motion=JsonUtility.ToJson(CaptureProject(true));
                if(!Capture(false,"menu-camera").SequenceEqual(Capture(true,"ribbon-camera")))throw new Exception("Camera pixels changed on workspace switch");
                if(motion!=JsonUtility.ToJson(CaptureProject(true)))throw new Exception("Pose or material state changed on workspace switch");
                if(classicTab.text!="MikuMikuDance風" || movingTab.text!="MikuMikuMoving風" ||
                    !combinedMenus.Any(e=>e.Label(false)=="AvaAvaEffect"))throw new Exception("Workspace or effect menu label");
            }finally{ApplyProfile(initial);previewCamera.targetTexture=prior;previewCamera.aspect=aspect;rt.Release();Destroy(rt);Destroy(texture);}
        }
        void CheckAutomaticEffects(string input,string dir)
        {
            // Put the same source model and a sibling material program in an isolated fixture folder.
            var folder=Directory.CreateDirectory(Path.Combine(dir,"source")).FullName;
            var archive=Path.Combine(folder,"test.unitypackage");File.Copy(input,archive,true);
            var package=Import.UnityPackageReader.Read(archive);
            var model=package.First(a=>a.HasAsset && a.Path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase));
            var effect=Path.Combine(folder,Path.GetFileNameWithoutExtension(model.Path)+".fx");
            File.WriteAllText(effect,"float4 Paint():COLOR{return float4(1,0,1,1);} technique Main{pass P{PixelShader=compile ps_3_0 Paint();}}");
            ImportModel(model,package,Import.PackageAvatar.LegacyPrefab(package,model));
            var scope=extensions.Single(p=>p.manifest.id=="ava.mme");
            WritePreview(Path.Combine(dir,"automatic.png"));
            bool Assigned()=>scope.Materials().Where(slot=>slot.ActorId==actor.id).Any(slot=>surfaceProviders.Any(p=>p.Resolve(slot)?.Passes?.Length>0));
            if(!Assigned() || scope.ReadState()==null)throw new Exception("Automatic material assignment");
            var scene=Path.Combine(dir,"automatic.avadance");SaveProject(scene);File.Delete(effect);OpenProject(scene);
            if(!Assigned())throw new Exception("Embedded automatic program");
            var editor=(Ava.Extensions.ISceneEditor)scope;var state=editor.ReadScene();
            if(state.Objects.Length<2 || state.Objects.Select(o=>o.Id).Distinct().Count()!=state.Objects.Length || editor.ReadRig(actor.id)?.joints?.Length<1)
                throw new Exception("Scene snapshot identities");
            editor.SetOrder(state.Document,state.CalculationOrder.Reverse().ToArray(),true);ApplyHistory(false);
            if(!editor.ReadScene().CalculationOrder.SequenceEqual(state.CalculationOrder))throw new Exception("Scene calculation order undo");
            var storage=scope;var raw=new byte[]{0,255,23};var priorState=storage.ReadState();
            storage.WriteBlock(priorState,new System.Collections.Generic.Dictionary<string,byte[]>{{"probe",raw}});
            var archivePath=Path.Combine(dir,"blobs.avadance");SaveProject(archivePath);OpenProject(archivePath);
            if(!storage.Blobs().Contains("probe") || !storage.ReadBlob("probe").SequenceEqual(raw))throw new Exception("Extension resource round trip");
            storage.WriteBlock(priorState,new System.Collections.Generic.Dictionary<string,byte[]>{{"probe",null}});
            if(storage.ReadBlob("probe")!=null)throw new Exception("Extension resource deletion");
        }

        void CheckHostEditing(string dir)
        {
            var scope=extensions.First();
            var invocations=new System.Collections.Generic.List<string>();string prefix=scope.manifest.id+".probe.";
            var stage=Ava.Extensions.EvaluationStage.AfterSceneAndAccessories;
            using(var second=scope.ObserveEvaluation(prefix+"b",stage,0,null,s=>invocations.Add("b")))
            using(var first=scope.ObserveEvaluation(prefix+"a",stage,-1,null,s=>invocations.Add("a")))
            using(var third=scope.ObserveEvaluation(prefix+"c",stage,-2,new[]{prefix+"b"},s=>invocations.Add("c")))
                EvaluateAnimation(frame,false);
            if(string.Join("",invocations)!="abc")throw new Exception("Evaluation hook order");
            var host=(Ava.Extensions.IMotionEditor)scope;
            var snapshot=host.ReadMotion();
            var camera=snapshot.Tracks.Single(t=>t.Channel==Ava.Extensions.TrackChannel.Camera);
            var light=snapshot.Tracks.Single(t=>t.Channel==Ava.Extensions.TrackChannel.Light);
            var draft=lightTrack.Current;draft.Direction.x+=.13f;lightTrack.Preview(draft);
            var sample=host.Sample(camera.Owner,camera.Id,0);sample.Distance=25;
            var edit=new Ava.Extensions.KeyEdit{Track=camera.Id,Owner=camera.Owner,Revision=camera.Revision,Frame=19,Value=sample};
            var bad=new Ava.Extensions.KeyEdit{Track=light.Id,Owner=light.Owner,Revision=light.Revision,Frame=19,Value=sample};
            bool rejected=false;
            try{host.ApplyMotion(snapshot.Document,new[]{edit,bad});}catch(InvalidDataException){rejected=true;}
            if(!rejected || cameraTrack.KeyAt(19)!=null)throw new Exception("Partially applied transaction");
            int before=undo.Count;
            host.ApplyMotion(snapshot.Document,new[]{edit,edit});
            if(undo.Count!=before+1 || lightTrack.Current.Direction!=draft.Direction || ((CameraPose)cameraTrack.KeyAt(19)).Distance!=25)
                throw new Exception("Motion transaction or draft preservation");
            rejected=false;try{host.ApplyMotion(snapshot.Document,new[]{edit});}catch(InvalidOperationException){rejected=true;}
            if(!rejected)throw new Exception("Stale snapshot accepted");
            ApplyHistory(false);if(cameraTrack.KeyAt(19)!=null || lightTrack.Current.Direction!=draft.Direction)throw new Exception("Transaction undo");
            string file=Path.Combine(dir,"host.avadance");SaveProject(file);OpenProject(file);
            rejected=false;try{host.ApplyMotion(snapshot.Document,Array.Empty<Ava.Extensions.KeyEdit>());}catch(InvalidOperationException){rejected=true;}
            if(!rejected)throw new Exception("Wrong document accepted");
        }

        void ClickSmokeButton(Button button)
        {
            MenuRoot.panel.Pick(Vector2.zero);
            var at=button.worldBound.center;
            using(var down=PointerDownEvent.GetPooled(new Event{type=EventType.MouseDown,mousePosition=at,button=0})){down.target=button;button.SendEvent(down);}
            using(var up=PointerUpEvent.GetPooled(new Event{type=EventType.MouseUp,mousePosition=at,button=0})){up.target=button;button.SendEvent(up);}
        }

        System.Collections.IEnumerator CheckCurveTools(string directory,Action<string> fail)
        {
            var baseline=Path.Combine(directory,"curve-start.avadance");
            SaveProject(baseline);bool initial=ribbonProfile,oldInline=showInlineCurve,oldPanel=showRibbonCurve;
            ApplyProfile(true);ChangeMode(true);
            var track=actor.bones.First().Track;track.ActiveLayer=null;track.ActiveClip=null;SelectTrack(track);
            foreach(int at in new[]{0,5,10}){var pose=BonePose.Rest;pose.Position.x=at*.01f;pose.Curves=SetAxes(null,Controls(20,20,107,107),4);track.Write(at,pose);}
            SetFrame(5);SetFirstFrame(0);
            rangeSelected=true;selectedKeys.Clear();foreach(int at in new[]{0,5,10})selectedKeys.Add((track,at));
            showInlineCurve=showRibbonCurve=true;PresentRibbonLayout(true);SyncCurve();curveChannel.index=0;
            yield return null;yield return null;
            void Check(bool ok,string label){if(!ok)throw new InvalidDataException(label);}
            void Shape(int at,int axis,EaseCurve expected){
                var pose=(BonePose)track.KeyAt(at);var actual=CurveSet.Get(pose.Curves,axis);
                Check(Vector2.Distance(actual.First,expected.First)<.0001f && Vector2.Distance(actual.Second,expected.Second)<.0001f,"Curve mismatch at "+at+" / "+axis);
            }
            void Drag(VisualElement graph,Rect area,EaseCurve from,EaseCurve to){
                Vector2 At(Vector2 v)=>graph.LocalToWorld(new Vector2(area.xMin+area.width*v.x,area.yMax-area.height*v.y));
                var first=At(from.First);var last=At(to.First);
                using(var down=PointerDownEvent.GetPooled(new Event{type=EventType.MouseDown,mousePosition=first,button=0})){down.target=graph;graph.SendEvent(down);}
                using(var move=PointerMoveEvent.GetPooled(new Event{type=EventType.MouseDrag,mousePosition=last,button=0})){move.target=graph;graph.SendEvent(move);}
                using(var up=PointerUpEvent.GetPooled(new Event{type=EventType.MouseUp,mousePosition=last,button=0})){up.target=graph;graph.SendEvent(up);}
            }
            try{
                File.WriteAllText(Path.Combine(directory,"curve-track.txt"),track.Name);
                for(int slot=1;slot<=6;slot++){
                    ClickSmokeButton(presetButtons[slot-1]);foreach(int at in new[]{0,5,10})Shape(at,0,curveSlots[slot]);
                    SaveVmd(Path.Combine(directory,"palette-"+slot+".vmd"));
                }
                ClickSmokeButton(allCurves);
                foreach(int at in new[]{0,5,10})for(int axis=0;axis<4;axis++)Shape(at,axis,curveSlots[6]);
                SaveVmd(Path.Combine(directory,"palette-all.vmd"));
                var updated=Controls(78,20,127,64);var graph=inlineCurve.Q("segment");var h=graph.contentRect.height-12;
                Check(h>0 && graph.contentRect.width>FrameWidth*5,"Inline curve bounds");
                int before=undo.Count;
                Drag(graph,new Rect((.5f-firstFrame)*FrameWidth,6,FrameWidth*5,h),curveSlots[6],updated);
                Shape(5,0,updated);Shape(0,0,curveSlots[6]);Shape(10,0,curveSlots[6]);Check(undo.Count==before+1,"Inline curve history");
                SaveVmd(Path.Combine(directory,"curve-inline.vmd"));ApplyHistory(false);
                SetFrame(5);SelectTrack(track);curveChannel.index=0;
                Drag(curveGraph,new Rect(6,6,curveGraph.contentRect.width-12,curveGraph.contentRect.height-12),curveSlots[6],updated);
                Shape(5,0,updated);SaveVmd(Path.Combine(directory,"curve-inspector.vmd"));
            }catch(Exception e){fail("FAIL "+e);}
            finally{showInlineCurve=oldInline;showRibbonCurve=oldPanel;OpenProject(baseline);ApplyProfile(initial);}
        }

        System.Collections.IEnumerator CheckCurveDefaults(string directory,Action<string> fail)
        {
            var prior=curveSlots[0];bool changed=customDefault,initial=ribbonProfile,auto=autoCurve.value;
            string path=Path.Combine(directory,"curve-start.avadance"),preference=WorkspacePreferences.GetString("Curve.Preset.0");
            ApplyProfile(true);ChangeMode(true);var track=actor.bones.First().Track;track.ActiveLayer=null;track.ActiveClip=null;SelectTrack(track);
            int history=undo.Count;EditCurvePalette();yield return null;yield return null;
            try{
                var config=modal.Query<IntegerField>().ToList();int[] points={31,21,91,101};
                for(int i=0;i<4;i++)config[i].value=points[i];
                ClickSmokeButton(modal.Query<Button>().ToList().Single(b=>b.text=="OK"));
                if(modal!=null || undo.Count!=history)throw new Exception("Palette settings changed document history");
                var saved=JsonUtility.FromJson<EaseCurve>(WorkspacePreferences.GetString("Curve.Preset.0"));
                var desired=Controls(31,21,91,101);
                if(!saved.Equals(desired))throw new Exception("Default curve preference");
                SetFrame(1234);autoCurve.value=false;var next=track.Current;next.Position.y+=.01f;track.Preview(next);RegisterKey(track);
                var stored=(BonePose)track.KeyAt(1234);
                for(int i=0;i<4;i++)if(!CurveSet.Get(stored.Curves,i).Equals(desired))throw new Exception("New key curve channel "+i);
                File.WriteAllText(Path.Combine(directory,"palette-default.txt"),string.Join(",",points));
            }catch(Exception e){fail("FAIL "+e);}
            finally{
                if(modal!=null)CloseDialog();curveSlots[0]=prior;customDefault=changed;autoCurve.value=auto;
                if(string.IsNullOrEmpty(preference))WorkspacePreferences.DeleteKey("Curve.Preset.0");else WorkspacePreferences.SetString("Curve.Preset.0",preference);
                OpenProject(path);ApplyProfile(initial);
            }
        }

        static void CheckClipTiming(string directory)
        {
            var track=new MotionTrack<BonePose>("位置",BonePose.Rest,BonePose.Blend,_=>{});
            var layer=new MotionLayer();
            for(int at=0;at<=10;at+=5){
                var pose=BonePose.Rest;pose.Position=Vector3.right*(at*.1f);layer.Write(at,pose);
            }
            track.Layers.Add(layer);
            var rows=new System.Collections.Generic.List<string>{"case,frame,x"};
            void Compare(string name,int cycles,bool infinite,bool muted,float weight,int[] frames,float[] positions){
                var clip=new ClipPlacement{start=4,sourceEnd=10,rate=.5f,cycles=cycles,repeat=infinite || cycles>1,infinite=infinite,weight=weight};
                layer.clips.Clear();layer.clips.Add(clip);layer.muted=muted;
                if(!clip.Valid)throw new Exception("Invalid placement: "+name);
                for(int i=0;i<frames.Length;i++){
                    var pose=(BonePose)track.SampleValue(frames[i]);float value=pose.Position.x;
                    if(Mathf.Abs(value-positions[i])>1e-5f)throw new Exception(name+" frame "+frames[i]+" position "+value);
                    rows.Add(name+","+frames[i]+","+value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            Compare("once",1,false,false,1,new[]{23,24,25,34},new[]{.95f,1,0,0});
            Compare("repeated",1,true,false,1,new[]{0,4,5,9,14,23,24,25,34},new[]{0f,0,.05f,.25f,.5f,.95f,0,.05f,.5f});
            Compare("twice",2,false,false,.5f,new[]{14,23,24,25,43,44,45},new[]{.25f,.475f,.5f,.025f,.475f,.5f,0});
            Compare("thrice",3,false,false,.5f,new[]{24,44,64,65},new[]{.5f,0,.5f,0});
            Compare("muted",3,false,true,.5f,new[]{14},new[]{0f});
            File.WriteAllLines(Path.Combine(directory,"clip-times.csv"),rows);
        }

        void CheckPasteBindings()
        {
            var previous=actor;bool mode=modelMode;
            var source=new Actor{avatar=new GameObject("source"),modelGuid="source"};
            var target=new Actor{avatar=new GameObject("target"),modelGuid="target"};
            BoneBinding Add(Actor a,string branch,string name){
                var parent=new GameObject(branch).transform;parent.SetParent(a.avatar.transform,false);
                var joint=new GameObject(name).transform;joint.SetParent(parent,false);
                var track=new MotionTrack<BonePose>(name,BonePose.Rest,BonePose.Blend,_=>{});
                var bone=new BoneBinding{Joint=joint,Track=track};a.bones.Add(bone);a.modelTracks.Add(track);return bone;
            }
            try{
                var sourceArm=Add(source,"sleeve","tip");var other=Add(target,"skirt","tip");
                drawOrder.Add(source);actor=target;modelMode=true;
                if(OriginalTarget(sourceArm.Track)!=null)throw new Exception("Unrelated clipboard bone matched");
                var correct=Add(target,"sleeve","tip");
                if(OriginalTarget(sourceArm.Track)!=correct.Track)throw new Exception("Clipboard hierarchy mapping");
                source.humanBones=new System.Collections.Generic.Dictionary<HumanBodyBones,Transform>{{HumanBodyBones.RightHand,sourceArm.Joint}};
                target.humanBones=new System.Collections.Generic.Dictionary<HumanBodyBones,Transform>{{HumanBodyBones.RightHand,other.Joint}};
                if(OriginalTarget(sourceArm.Track)!=other.Track)throw new Exception("Clipboard role mapping");
            }finally{
                actor=previous;modelMode=mode;drawOrder.Remove(source);Destroy(source.avatar);Destroy(target.avatar);
            }
        }

        void CheckWorkspaceInput()
        {
            void Press(KeyCode key,EventModifiers modifiers,VisualElement target=null)
            {
                target??=MenuRoot.Q("workspace");target.Focus();
                using(var down=KeyDownEvent.GetPooled(new Event{type=EventType.KeyDown,keyCode=key,modifiers=modifiers})){down.target=target;target.SendEvent(down);}
                using(var up=KeyUpEvent.GetPooled(new Event{type=EventType.KeyUp,keyCode=key,modifiers=modifiers})){up.target=target;target.SendEvent(up);}
            }
            void Click(Button button)=>ClickSmokeButton(button);
            ApplyProfile(false);Click(movingTab);
            if(!ribbonProfile)throw new Exception("Profile tab pointer activation");
            Click(waveTab);
            if(!audioDockTab || !timeline.ClassListContains("hidden"))throw new Exception("Audio panel activation: "+waveTab.worldBound+" / "+audioDockTab+" / "+timeline.ClassListContains("hidden"));
            Click(classicTab);Click(movingTab);
            if(!audioDockTab)throw new Exception("Audio panel selection lost on profile switch");
            Click(timeTab);if(audioDockTab || timeline.ClassListContains("hidden"))throw new Exception("Timeline panel activation");
            int before=frame;
            Press(KeyCode.RightArrow,EventModifiers.None,transformFields[0]);
            if(before!=frame)throw new Exception("Field input leaked to timeline");
            Press(KeyCode.M,EventModifiers.Control);
            if(!bookmarks.Contains(frame))throw new Exception("Bookmark shortcut");
            Press(KeyCode.Z,EventModifiers.Control);
            if(bookmarks.Contains(frame))throw new Exception("Bookmark undo");
            var scoped=keyOverrides[1];const string timelineKey="タイムライン|F11",viewKey="ビューポート|F11";
            bool initialAxes=localAxes;
            var oldTimeline=scoped.TryGetValue(timelineKey,out var savedTimeline);var oldView=scoped.TryGetValue(viewKey,out var savedView);
            try{
                scoped[timelineKey]="transform.space.toggle";scoped[viewKey]="timeline.bookmark.toggleAtCurrent";
                Press(KeyCode.F11,EventModifiers.None,timeline);
                if(localAxes==initialAxes || bookmarks.Contains(frame))throw new Exception("Timeline shortcut scope");
                Press(KeyCode.F11,EventModifiers.None,viewport);
                if(!bookmarks.Contains(frame))throw new Exception("Viewport shortcut scope");
                ApplyHistory(false);Press(KeyCode.F11,EventModifiers.None,timeline);
                if(localAxes!=initialAxes || bookmarks.Contains(frame))throw new Exception("Scoped shortcut restoration");
            }finally{
                if(oldTimeline)scoped[timelineKey]=savedTimeline;else scoped.Remove(timelineKey);
                if(oldView)scoped[viewKey]=savedView;else scoped.Remove(viewKey);
            }
            foreach(var pair in new[]{(0xbb,';',KeyCode.Semicolon),(0xba,':',KeyCode.Colon),(0xbb,'=',KeyCode.Semicolon),(0xba,';',KeyCode.Colon),
                (0xe2,(char)92,KeyCode.Backslash),(0xdc,(char)92,KeyCode.None)}){
                symbol=new SymbolKey{vk=pair.Item1,glyph=pair.Item2};
                var hand=MenuRoot.Q("workspace");hand.Focus();var label=symbol.Label;
                using(var down=KeyDownEvent.GetPooled(new Event{type=EventType.KeyDown,keyCode=label})){down.target=hand;hand.SendEvent(down);}
                if(transformKey!=pair.Item3)throw new Exception("Symbol routing "+symbol.Id);
                if(transformKey!=KeyCode.None){
                    var original=actor.selectedBone.Track.Current;
                    ApplyHeldDelta(transformKey,new Vector2(10,0));
                    var after=actor.selectedBone.Track.Current;
                    if(original.Position!=after.Position || original.Rotation!=after.Rotation)throw new Exception("Horizontal held gesture");
                }
                using(var up=KeyUpEvent.GetPooled(new Event{type=EventType.KeyUp,keyCode=label})){up.target=hand;hand.SendEvent(up);}
                if(transformKey!=KeyCode.None)throw new Exception("Symbol release");
                symbol=default;
            }
            RibbonShortcut(KeyCode.Period,false,false,false);RequestProfile(false);
            if(!ribbonProfile)throw new Exception("Gesture switch was not deferred");
            FinishHeldTransform();CompleteProfileSwitch();
            if(ribbonProfile)throw new Exception("Gesture switch did not finish");
            Click(movingTab); Click(classicTab);
        }

        void CheckLinkAndSource(string dir)
        {
            var b=actor.bones.LastOrDefault(j=>actor.bones.Any(p=>p!=j && !p.Joint.IsChildOf(j.Joint)));
            if(b!=null){
                var parent=actor.bones.First(p=>p!=b && !p.Joint.IsChildOf(b.Joint));var before=b.Joint.position;
                SetExternalParent(b,actor,parent);if(Vector3.Distance(before,b.Joint.position)>.001f)throw new Exception("Link bind position");
                var at=parent.Track.Current;at.Position.x+=.025f;parent.Track.Preview(at);RefreshRigPreview();
                var held=b.Joint.position;var keys=b.Track.Frames.Count;
                SetExternalParent(b,null,null);
                if(keys!=b.Track.Frames.Count || Vector3.Distance(held,b.Joint.position)>.001f)throw new Exception("Link detach draft");
                ApplyHistory(false);if(externalParents.Count!=1)throw new Exception("Link undo");
                var path=Path.Combine(dir,"constraint.avadance");SaveProject(path);OpenProject(path);
                if(externalParents.Count!=1 || Vector3.Distance(externalParents[0].child.Joint.position,held)>.001f)throw new Exception("Link archive");
                SetExternalParent(externalParents[0].child,null,null);
            }
            ChangeMode(true);
            var track=actor.bones.First().Track;var name=Ava.Extensions.MmdText.Fixed(track.Name,15);int end=Array.IndexOf(name,(byte)0);
            if(end>=0 && end<14)name[14]=0x41;
            var record=new byte[111];Array.Copy(name,record,name.Length);Array.Copy(BitConverter.GetBytes((uint)11),0,record,15,4);
            Ava.Extensions.VmdDocument.Number(record,43,1);
            var doc=new Ava.Extensions.VmdDocument("fixture");doc.Records(Ava.Extensions.MotionChannel.Bone).Add(record);
            var source=Path.Combine(dir,"key-source.vmd");doc.Save(source);LoadVmd(source);
            if(track.SourceAt(11)?.name?.SequenceEqual(name)!=true)throw new Exception("Source name import");
            track.ActiveLayer=track.ActiveClip=null;SetFrame(11);ShiftColumn(false,true,new[]{track});if(track.SourceAt(12)?.name?.SequenceEqual(name)!=true)throw new Exception("Move source name");
            ApplyHistory(false);SelectTrack(track);var project=Path.Combine(dir,"names.avadance");SaveProject(project);OpenProject(project);
            var outFile=Path.Combine(dir,"names.vmd");SaveVmd(outFile);
            if(!Ava.Extensions.VmdDocument.Read(outFile).Records(Ava.Extensions.MotionChannel.Bone).Any(row=>Ava.Extensions.VmdDocument.Frame(Ava.Extensions.MotionChannel.Bone,row)==11 && row.Take(15).SequenceEqual(name)))throw new Exception("Source name export");
        }

        void CheckParentGestures()
        {
            var center=actor.bones.Single(b=>b.Track.Name=="センター");
            var parent=actor.bones.Single(b=>b.Track.Name=="肩");
            var child=actor.bones.Single(b=>b.Track.Name=="腕");
            bool previous=localAxes;localAxes=false;RequestProfile(true);SetFrame(14);SelectTrack(child.Track);
            try{
                var start=child.Joint.position;var orientation=child.Joint.rotation;
                void Drag(RotationGesture mode){BeginDraftGesture();DragAxis(2,true,-8,mode);FinishDraftGesture();}
                Drag(RotationMode(true,false,false));
                if(Quaternion.Angle(child.Joint.rotation,orientation)>.05f || Quaternion.Angle(parent.Track.Current.Rotation,Quaternion.identity)<3.9f ||
                    Vector3.Distance(start,child.Joint.position)<.001f || !boneSelection.Contains(parent))
                    throw new InvalidOperationException("Counter rotation about the parent joint");
                ApplyHistory(false);
                if(Vector3.Distance(start,child.Joint.position)>.0001f || Quaternion.Angle(parent.Track.Current.Rotation,Quaternion.identity)>.05f)
                    throw new InvalidOperationException("Undo counter rotation");
                SelectTrack(child.Track);Drag(RotationMode(false,true,true));
                if(Vector3.Distance(start,child.Joint.position)>.0001f || Quaternion.Angle(child.Joint.rotation,orientation)>.05f ||
                    center.Track.Current.Position.sqrMagnitude<1e-7f || boneSelection.Count!=3)
                    throw new InvalidOperationException("Reverse rotation about the selected joint");
                RequestProfile(false);RegisterSelection();
                if(new[]{center,parent,child}.Any(b=>b.Track.KeyAt(14)==null))throw new InvalidOperationException("Register linked rotations");
                RequestProfile(true);ApplyHistory(false);
                if(new[]{center,parent,child}.Any(b=>b.Track.KeyAt(14)!=null))throw new InvalidOperationException("Undo linked registration");
            }finally{localAxes=previous;}
        }

        void CheckControllerIO(string dir)
        {
            var descriptor=new EffectController{schemaVersion=1,alias="control",bones=new[]{new ControlBone{name="センター"},
                new ControlBone{name="肩",parent="センター",position=new Vector3(.3f,.6f,0)},
                new ControlBone{name="腕",parent="肩",position=new Vector3(.4f,-.2f,.03f)}},
                morphs=new[]{new ControlMorph{name="発光",value=1.5f}}};
            var file=Path.Combine(dir,"control.avaeffect");File.WriteAllText(file,JsonUtility.ToJson(descriptor));
            var asset=ReadVrmFile(file);
            ImportModel(asset,new System.Collections.Generic.List<Import.UnityPackageReader.PackageAsset>{asset},null);
            CheckParentGestures();
            var morph=actor.modelTracks.OfType<MotionTrack<float>>().Single();
            morph.Preview(-.25f);RegisterKey(morph);var path=Path.Combine(dir,"controller.avadance");
            SaveProject(path);OpenProject(path);
            if(actor.controller?.alias!="control" || actor.modelTracks.OfType<MotionTrack<float>>().Single().Current!=-.25f)
                throw new InvalidOperationException("Control model or raw morph value changed");
        }

        void CheckWorkspace(string dir)
        {
            void Check(bool pass,string name) {if(!pass)throw new InvalidOperationException("Workspace: "+name);}
            bool initial=ribbonProfile;
            try
            {
                var model=actor;var bone=model.bones.First().Track;SelectTrack(bone); SetFrame(17);MarkSaved();
                var selected=selectedTrack;int history=undo.Count;
                CheckClipTiming(dir);CheckPasteBindings();CheckWorkspaceInput();
                for(int i=0;i<5;i++){RequestProfile(true);RequestProfile(false);}
                Check(!ProjectChanged() && undo.Count==history && actor==model && selectedTrack==selected && frame==17,"switch changes content");
                var pose=bone.Current;pose.Position.x+=.14f;bone.Preview(pose);
                RequestProfile(true);Check(ProjectChanged() && bone.KeyAt(17)==null && bone.Current.Position==pose.Position,"draft");
                RegisterKey(bone);RequestProfile(false);ApplyHistory(false);
                Check(bone.KeyAt(17)==null && bone.Current.Position==pose.Position,"undo register draft");
                RequestProfile(true);ApplyHistory(true);Check(bone.KeyAt(17)!=null,"redo");
                var layer=new MotionLayer{name="腕"};
                layer.Write(4,new BonePose{Position=new Vector3(.125f,0,0),Rotation=Quaternion.Euler(0,20,0)});
                layer.Write(14,new BonePose{Position=new Vector3(.25f,0,0),Rotation=Quaternion.Euler(0,40,0)});
                var clip=new ClipPlacement{start=20,length=20,sourceStart=4,sourceEnd=14,rate=.5f};
                layer.clips.Add(clip);ApplyLayerEdit(bone,new System.Collections.Generic.List<MotionLayer>{layer},layer.id,clip.id);
                SetFrame(30);var basePose=(BonePose)bone.BaseValue(30); var evaluated=bone.Current;
                Check(Mathf.Abs(evaluated.Position.x-basePose.Position.x-.1875f)<1e-4,"layer interpolation");
                var edit=evaluated;edit.Position.y+=.25f;bone.Preview(edit);
                RequestProfile(false);RegisterKey(bone);
                Check(layer.At(9).HasValue && bone.KeyAt(30)==null && Vector3.Distance(bone.Current.Position,edit.Position)<1e-5,"local registration");
                RequestProfile(true);ApplyHistory(false);Check(!layer.At(9).HasValue,"layer undo");ApplyHistory(true);
                var shared=new ClipPlacement{start=65,length=20,sourceStart=4,sourceEnd=14,rate=.5f,source=layer.id};
                var solo=new ClipPlacement{start=100,length=20,sourceStart=4,sourceEnd=14,rate=.5f};
                layer.clips.Add(shared);layer.clips.Add(solo);
                var independent=layer.DuplicateSource(solo);bone.ActiveClip=solo.id;SetFrame(110);
                var distinct=bone.Current;distinct.Position.z+=.7f;bone.Preview(distinct);RegisterKey(bone);
                Check(independent.At(9).HasValue && Mathf.Abs(independent.At(9).Value.position.z-layer.At(9).Value.position.z)>.69f,
                    "independent clip registration");
                bone.ActiveClip=shared.id;ApplyHistory(false);
                Check(Mathf.Abs(independent.At(9).Value.position.z-layer.At(9).Value.position.z)<1e-5f,"source-aware undo");
                ApplyHistory(true);
                bone.ActiveClip=clip.id;SetFrame(30);
                ToggleBookmark();
                var morph=actor.modelTracks.OfType<MotionTrack<float>>().FirstOrDefault();
                if(morph!=null)
                {
                    morph.Write(0,0f);morph.Write(20,new MorphKey{Weight=1,Curve=new EaseCurve{custom=true,a=new Vector2(.8f,0),b=new Vector2(1,.1f)}});
                    Check(morph.Sample(10)<.15f && morph.OutputFrames().Count()==21,"morph curve");
                }
                var id=bone.Id;
                var unknown=new byte[]{0,255,8,6};extraEntries["extensions/test.opaque/blobs/sample"]=unknown;
                var path=Path.Combine(dir,"workspace.avadance");SaveProject(path);
                OpenProject(path);var restored=actor.bones.Single(b=>b.Track.Id==id).Track;
                Check(restored.ActiveLayer==layer.id && restored.ActiveClip==clip.id && restored.Target.At(9).HasValue &&
                    Vector3.Distance(restored.Sample(30).Position,edit.Position)<1e-5,"composition round trip");
                Check(restored.Target.SourceFor(solo.id).id==independent.id &&
                    Mathf.Abs(restored.Target.SourceFor(solo.id).At(9).Value.position.z-independent.At(9).Value.position.z)<1e-5f &&
                    restored.Target.SourceFor(shared.id)==restored.Target,"clip source identities after reopen");
                Check(extraEntries["extensions/test.opaque/blobs/sample"].SequenceEqual(unknown) && bookmarks.Contains(30),"project extras");
                if(morph!=null)Check(actor.modelTracks.OfType<MotionTrack<float>>().First().Sample(10)<.15f,"curve round trip");
                SetFrame(0);playFrom.value="0";playTo.value="60";TogglePlayback();var clock=startedAt;
                RequestProfile(true);RequestProfile(false);Check(playing && startedAt==clock,"playback switch");StopPlayback();
                Check(combinedMenus.Length==MmdMenu.Bar.Length+2,"extension discovery");
                CheckSurfaceOutput(dir);
                CheckLinkAndSource(dir);CheckControllerIO(dir);
            }
            finally {RequestProfile(initial);}
        }
    }
}
