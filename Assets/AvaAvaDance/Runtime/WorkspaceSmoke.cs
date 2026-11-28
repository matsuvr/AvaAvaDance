using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        System.Collections.IEnumerator CheckWorkspaceEditing(string directory, Action<string> complete)
        {
            var surface = new RenderTexture(1440, 960, 24, RenderTextureFormat.ARGB32);
            surface.Create();
            var previousTarget = panel.targetTexture;
            panel.targetTexture = surface;
            try
            {
                outlinerOpen = true; ApplyWorkspaceSizing();
                for (int i = 0; i < 6; i++) yield return null;
                string error = null;
                try
                {
                    void Check(bool condition, string message)
                    {
                        if (!condition) throw new InvalidOperationException(message);
                    }
                    var owner = actor;
                    var bones = owner.bones.Take(2).ToArray();
                    Check(bones.Length == 2, "Fixture must contain two selectable bones");
                    outlinerSearch.value = bones[1].Track.Name;
                    var item = outlinerRows.First(row => row.item.Track == bones[1].Track);
                    ClickSmokeButton(item.name);
                    Check(modelMode && selectedTrack == bones[1].Track, "Outliner search and click");
                    outlinerSearch.value = "";
                    var root = outlinerRoots.First(node => node.Owner == owner);
                    SelectOutlinerItem(root, true, false);
                    Check(selectedRows.Contains(root.Track) && selectedRows.Contains(bones[1].Track), "Outliner multi-selection");
                    var scale = owner.placementScale;
                    modelScaleField.value = 1.5f;
                    Check(Mathf.Abs(owner.placementRoot.localScale.x - 1.5f) < .001f &&
                        CaptureProject().placementScale == 1.5f, "Model scale reaches scene and project");
                    ApplyHistory(false);
                    Check(owner.placementScale == scale, "Model scale undo");
                    ApplyHistory(true);
                    Check(owner.placementScale == 1.5f, "Model scale redo");

                    var track = bones[1].Track;
                    SetFrame(17); SelectTrack(track);
                    var original = track.Current;
                    var toggle = MenuRoot.Q<Toggle>("auto-key");
                    Check(!toggle.value && track.KeyAt(17) == null, "Auto Key starts disabled");
                    var checkmark = toggle.Q(className: "unity-toggle__checkmark");
                    Check(checkmark != null && checkmark.worldBound.width >= 14 &&
                        toggle.worldBound.Contains(checkmark.worldBound.center), "Auto Key checkbox is visible");
                    transformFields[0].value += .25f;
                    transformFields[1].value += .5f;
                    Check(transformFields[0].ClassListContains("channel-draft") && track.KeyAt(17) == null,
                        "Edited channels remain draft with Auto Key disabled");
                    RegisterChannels(track, new[] { 0 });
                    Check(track.KeyAt(17) is BonePose registered &&
                        registered.Position.x != original.Position.x && registered.Position.y == original.Position.y &&
                        track.Current.Position.y != original.Position.y &&
                        transformFields[0].ClassListContains("channel-key") &&
                        transformFields[1].ClassListContains("channel-draft"), "Channel registration preserves other drafts");
                    ApplyHistory(false);
                    Check(track.KeyAt(17) == null && track.Current.Position.x != original.Position.x, "Channel registration undo");
                    ApplyHistory(true);
                    Check(track.KeyAt(17) != null && track.Current.Position.y != original.Position.y, "Channel registration redo");

                    SetFrame(24);
                    var before = track.Current;
                    toggle.value = true;
                    transformFields[0].value += .75f;
                    Check(track.KeyAt(24) != null && !track.HasDraft(24), "Auto Key writes and evaluates at the current frame");
                    ApplyHistory(false);
                    Check(track.KeyAt(24) == null && track.Current.Position == before.Position, "Auto Key is a single undo");
                    ApplyHistory(true);
                    Check(track.KeyAt(24) != null && !track.HasDraft(24), "Auto Key redo");
                    BeginDraftGesture(); Seek(25);
                    track.Preview(BonePose.Rest); FinishDraftGesture();
                    Check(track.KeyAt(25) == null, "A stale gesture cannot write to another frame");
                    toggle.value = false;

                    SetFrame(0); JumpKey(true);
                    Check(frame == 17, "Selected-track next key");
                    SelectKeys(17, 24, false);
                    FrameDopeSheet(true);
                    Check(firstFrame <= 17 && firstFrame + VisibleColumns > 24, "Frame selected keys");
                    MoveSelectedKeys(17, 19);
                    Check(track.KeyAt(19) != null && track.KeyAt(26) != null && track.KeyAt(17) == null,
                        "Move selected keys as a group");
                    FrameDopeSheet(false);
                    Check(firstFrame == 0 && firstFrame + VisibleColumns > 26, "Frame all keys");

                    var camera = cameraTrack.Current;
                    FrameViewport(true);
                    var orbit = ViewportView;
                    DragViewportCamera(new Vector2(16, 5), 0, true, false, false);
                    Check(ViewportView.Angles != orbit.Angles, "Alt orbit");
                    var center = ViewportView.Center;
                    DragViewportCamera(new Vector2(10, -5), 2, true, false, false);
                    Check(ViewportView.Center != center, "Alt pan");
                    var distance = ViewportView.Distance;
                    DragViewportCamera(new Vector2(20, 0), 1, true, false, false);
                    Check(ViewportView.Distance != distance && cameraTrack.Current.Equals(camera), "Alt zoom preserves camera data");
                    FrameViewport(false);
                    outlinerSearch.value = track.Name;
                    SetKeyframeTab(0);
                }
                catch (Exception ex) { error = "FAIL workspace editing: " + ex; }
                if (error != null) { complete(error); yield break; }

                for (int i = 0; i < 5; i++) yield return null;
                yield return new WaitForEndOfFrame();
                CaptureLayout(surface, Path.Combine(directory, "editing-workspace.png"));
                SetKeyframeTab(1);
                SelectInspector(InspectorTab.Curve);
                for (int i = 0; i < 5; i++) yield return null;
                yield return new WaitForEndOfFrame();
                CaptureLayout(surface, Path.Combine(directory, "interpolation-curves.png"));

                try
                {
                    string path = Path.Combine(directory, "scaled.avaproject");
                    SaveProject(path); OpenProject(path);
                    if (Mathf.Abs(actor.placementScale - 1.5f) > .001f ||
                        Mathf.Abs(actor.placementRoot.localScale.x - 1.5f) > .001f)
                        throw new InvalidOperationException("Model scale save and reopen");
                }
                catch (Exception ex) { error = "FAIL workspace editing round trip: " + ex; }
                complete(error ?? "PASS workspace editing (Outliner, channels, Auto Key, Dope Sheet, viewport, scale round trip)");
            }
            finally
            {
                panel.targetTexture = previousTarget;
                surface.Release(); Destroy(surface);
            }
        }

        System.Collections.IEnumerator CheckWorkspaceLayout(string directory,Action<string> complete)
        {
            bool priorMode = modelMode;
            float priorScale = panel.scale;
            var priorTarget = panel.targetTexture;
            var scroll = MenuRoot.Q<ScrollView>();
            var priorOffset = scroll.scrollOffset;
            var priorInspector = inspectorTab;
            var report = new System.Text.StringBuilder();
            string error = null;
            int cases = 0;
            SetKeyframeTab(0);
            try
            {
                foreach (var size in new[] { (1280, 900, 1f), (1920, 1080, 1f), (1920, 1080, 1.5f),
                    (2160, 1420, 1.5f), (2100, 700, 1f), (900, 1400, 1f), (900, 900, 1f), (640, 480, 1f),
                    (1280, 900, 2f), (360, 640, 1f), (1575, 800, 1.5f), (256, 240, 1f), (1024, 720, 1f) })
                {
                    var target = new RenderTexture(size.Item1, size.Item2, 24, RenderTextureFormat.ARGB32);
                    target.Create(); panel.targetTexture = target; panel.scale = size.Item3;
                    try
                    {
                        foreach (bool model in new[] { false, true })
                        foreach (InspectorTab tab in Enum.GetValues(typeof(InspectorTab)))
                        {
                            if (error != null) break;
                            ChangeMode(model); SelectInspector(tab);
                            scroll.scrollOffset = Vector2.zero;
                            for (int i = 0; i < 5; i++) yield return null;
                            yield return new WaitForEndOfFrame();
                            try
                            {
                                void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
                                var root = MenuRoot.worldBound;
                                var view = viewport.worldBound;
                                var sheet = timeline.worldBound;
                                var stage = workspaceStage.worldBound;
                                var dock = rightDock.worldBound;
                                bool wide = !workspaceBody.ClassListContains("stacked-workspace");
                                Check(Mathf.Abs(root.width - size.Item1 / size.Item3) < 1 &&
                                      Mathf.Abs(root.height - size.Item2 / size.Item3) < 1, "Unexpected panel size " + root);
                                Check(view.height >= 100 && sheet.height > 0 && view.yMax <= sheet.yMin, "Viewport/Dope Sheet overlap or zero height");
                                Check(wide ? stage.xMax <= dock.xMin + 1 : stage.yMax <= dock.yMin + 1, "Dock overlaps editing region");
                                Check(dock.yMax <= status.worldBound.yMin + 1 && dock.width > 0, "Dock exceeds workspace");
                                Check(modelMode == model && inspectorTabs[(int)tab].ClassListContains("active-tool") &&
                                    inspectorPages[(int)tab].worldBound.height > 0, "Inspector selection or model context lost");
                                foreach (var button in inspectorTabs)
                                    Check(button.worldBound.height > 0 && button.worldBound.xMax <= dock.xMax + 1, "Inspector tab unreachable");
                                if (size == (1280, 900, 1f) || size == (1920, 1080, 1f))
                                {
                                    Check(sheet.yMax <= root.yMax && view.yMin >= root.yMin && Mathf.Abs(status.worldBound.yMax - root.yMax) <= 3,
                                        "Initial viewport, timeline, and status must fill the workspace");
                                    if (shaderAddButton.ClassListContains("missing-shaders"))
                                    {
                                        Color32 color = shaderAddButton.resolvedStyle.backgroundColor;
                                        Check(color.r == 255 && color.g == 213 && color.b == 111 &&
                                            shaderAddButton.worldBound.xMax <= root.xMax + 1, "Missing shader warning must stay visible");
                                    }
                                }
                                foreach (var row in new[] { MenuRoot.Q("menu"), MenuRoot.Q("keyframe-header"),
                                    MenuRoot.Q("viewport-menu"), MenuRoot.Q("viewport-toolbar"), MenuRoot.Q("range-slider-bar") })
                                    CheckLayoutChildren(row);
                                report.AppendLine($"{size} model={model} tab={tab} viewport={view} timeline={sheet} dock={dock} status={status.worldBound}");
                                if ((size == (1280, 900, 1f) && model) || (size == (360, 640, 1f) && model && tab == InspectorTab.Channels))
                                    CaptureLayout(target, Path.Combine(directory, $"{size.Item1}x{size.Item2}-{tab}.png"));
                                cases++;
                            }
                            catch (Exception ex) { error = $"FAIL {size} model={model} tab={tab}: {ex}"; }
                            if (error != null) break;
                            if (workspaceBody.ClassListContains("stacked-workspace"))
                            {
                                scroll.ScrollTo(rightDock);
                                yield return null;
                                if (!rightDock.worldBound.Overlaps(scroll.contentViewport.worldBound))
                                    error = "FAIL: compact inspector cannot be scrolled into view";
                            }
                        }
                        if (error == null && size == (1280, 900, 1f))
                        {
                            SelectInspector(InspectorTab.Scene);
                            SetKeyframeTab(1);
                            for (int i = 0; i < 3; i++) yield return null;
                            yield return new WaitForEndOfFrame();
                            if (!graphDockTab || graphCanvas.worldBound.height <= 0 || VisibleColumns <= 1)
                                error = "FAIL: Graph Editor is not visible";
                            else CaptureLayout(target, Path.Combine(directory, "1280x900-Graph.png"));
                            SetKeyframeTab(2);
                            for (int i = 0; i < 3; i++) yield return null;
                            if (!audioDockTab || waveform.worldBound.height < 60 || !timeline.ClassListContains("hidden"))
                                error = "FAIL: Audio editor is not visible";
                            SetKeyframeTab(0);
                        }
                    }
                    finally { panel.targetTexture = priorTarget; target.Release(); Destroy(target); }
                    if (error != null) break;
                }
            }
            finally
            {
                panel.targetTexture = priorTarget; panel.scale = priorScale;
                SelectInspector(priorInspector); ChangeMode(priorMode); scroll.scrollOffset = priorOffset;
                File.WriteAllText(Path.Combine(directory, "layout.txt"), report.ToString());
            }
            complete(error ?? $"PASS workspace layout: {cases} inspector, scene/model, size and DPI combinations");
        }

        static void CaptureLayout(RenderTexture target,string path)
        {
            var previous=RenderTexture.active;
            var image=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            try{
                RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);
                image.Apply();
                File.WriteAllBytes(path,image.EncodeToPNG());
            }finally{RenderTexture.active=previous;Destroy(image);}
        }

        static void CheckLayoutChildren(VisualElement parent)
        {
            var bounds=parent.worldBound;
            var children=parent.Children().Where(e=>e.resolvedStyle.display!=DisplayStyle.None && e.worldBound.width>0).ToArray();
            for(int i=0;i<children.Length;i++){
                var child=children[i];var rect=child.worldBound;
                if(rect.xMin<bounds.xMin-1 || rect.xMax>bounds.xMax+1 || rect.yMin<bounds.yMin-1 || rect.yMax>bounds.yMax+1)
                    throw new InvalidOperationException($"Outside {parent.name} {bounds}: {child.name} {ControlText(child)} {rect}");
                for(int j=0;j<i;j++){
                    var other=children[j].worldBound;
                    if(Math.Min(rect.xMax,other.xMax)-Math.Max(rect.xMin,other.xMin)>1 &&
                       Math.Min(rect.yMax,other.yMax)-Math.Max(rect.yMin,other.yMin)>1)
                        throw new InvalidOperationException($"Overlap in {parent.name}: {ControlText(child)} and {ControlText(children[j])}");
                }
            }
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
            string project=Path.Combine(dir,"effects.avaproject");SaveProject(project);OpenProject(project);
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
                if(workspaceChoice.choices.Count != 3)throw new Exception("Workspace presets");
                var plugins=combinedMenus.Single(e=>e.Japanese=="プラグイン(&E)");
                var effects=plugins.Children.Single(e=>e.Japanese=="AvaAvaEffect");
                var effectCommands=extensions.Single(e=>e.manifest.id=="ava.mme").items.Select(item=>extensionCommands[item.id]);
                if(!effects.Children.Select(e=>e.Id).SequenceEqual(effectCommands))throw new Exception("Effect submenu commands");
                int previousTab=ribbonTab;
                try
                {
                    SelectRibbonTab(6);
                    var button=ribbonTools.Query<Button>().ToList().Single(b=>b.tooltip==effects.Japanese);
                    using(var open=NavigationSubmitEvent.GetPooled()) { open.target=button;button.SendEvent(open); }
                    var popup=MenuRoot.panel.visualTree.Q(className:GenericDropdownMenu.ussClassName);
                    var labels=popup.Query<Label>(className:GenericDropdownMenu.labelUssClassName).ToList();
                    if(!labels.Select(l=>l.text).SequenceEqual(effects.Children.Select(e=>e.Label(false))))
                        throw new Exception("Ribbon effect menu commands");
                    var target=labels.Last();
                    using(var end=KeyDownEvent.GetPooled(new Event{type=EventType.KeyDown,keyCode=KeyCode.End}))
                    { end.target=target;target.SendEvent(end); }
                    using(var submit=NavigationSubmitEvent.GetPooled()) { submit.target=target;target.SendEvent(submit); }
                    if(!status.text.StartsWith("AvaAvaEffect ",StringComparison.Ordinal) || popup.panel!=null)
                        throw new Exception("Ribbon effect menu selection");
                }
                finally { SelectRibbonTab(previousTab); }
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
            var scene=Path.Combine(dir,"automatic.avaproject");SaveProject(scene);File.Delete(effect);OpenProject(scene);
            if(!Assigned())throw new Exception("Embedded automatic program");
            var editor=(Ava.Extensions.ISceneEditor)scope;var state=editor.ReadScene();
            if(state.Objects.Length<2 || state.Objects.Select(o=>o.Id).Distinct().Count()!=state.Objects.Length || editor.ReadRig(actor.id)?.joints?.Length<1)
                throw new Exception("Scene snapshot identities");
            editor.SetOrder(state.Document,state.CalculationOrder.Reverse().ToArray(),true);ApplyHistory(false);
            if(!editor.ReadScene().CalculationOrder.SequenceEqual(state.CalculationOrder))throw new Exception("Scene calculation order undo");
            var storage=scope;var raw=new byte[]{0,255,23};var priorState=storage.ReadState();
            storage.WriteBlock(priorState,new System.Collections.Generic.Dictionary<string,byte[]>{{"probe",raw}});
            var archivePath=Path.Combine(dir,"blobs.avaproject");SaveProject(archivePath);OpenProject(archivePath);
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
            string file=Path.Combine(dir,"host.avaproject");SaveProject(file);OpenProject(file);
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
            var baseline=Path.Combine(directory,"curve-start.avaproject");
            SaveProject(baseline);bool initial=ribbonProfile,oldInline=showInlineCurve,oldPanel=showRibbonCurve;
            int priorEditor = graphDockTab ? 1 : audioDockTab ? 2 : 0;
            ApplyProfile(true);ChangeMode(true);
            var track=actor.bones.First().Track;track.ActiveLayer=null;track.ActiveClip=null;SelectTrack(track);
            foreach(int at in new[]{0,5,10}){var pose=BonePose.Rest;pose.Position.x=at*.01f;pose.Curves=SetAxes(null,Controls(20,20,107,107),4);track.Write(at,pose);}
            SetFrame(5);SetFirstFrame(0);
            rangeSelected=true;selectedKeys.Clear();foreach(int at in new[]{0,5,10})selectedKeys.Add((track,at));
            var priorInspector = inspectorTab;
            SelectInspector(InspectorTab.Curve);
            SetKeyframeTab(1);
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
                    SaveMotion(Path.Combine(directory,"palette-"+slot+".avamotion"));
                }
                ClickSmokeButton(allCurves);
                foreach(int at in new[]{0,5,10})for(int axis=0;axis<4;axis++)Shape(at,axis,curveSlots[6]);
                SaveMotion(Path.Combine(directory,"palette-all.avamotion"));
                var updated=Controls(78,20,127,64);var graph=inlineCurve.Q("segment");var h=graph.contentRect.height-12;
                Check(h>0 && graph.contentRect.width>FrameWidth*5,"Inline curve bounds");
                int before=undo.Count;
                Drag(graph,new Rect((.5f-firstFrame)*FrameWidth,6,FrameWidth*5,h),curveSlots[6],updated);
                Shape(5,0,updated);Shape(0,0,curveSlots[6]);Shape(10,0,curveSlots[6]);Check(undo.Count==before+1,"Inline curve history");
                SaveMotion(Path.Combine(directory,"curve-inline.avamotion"));ApplyHistory(false);
                SetFrame(5);SelectTrack(track);curveChannel.index=0;
                Drag(curveGraph,new Rect(6,6,curveGraph.contentRect.width-12,curveGraph.contentRect.height-12),curveSlots[6],updated);
                Shape(5,0,updated);SaveMotion(Path.Combine(directory,"curve-inspector.avamotion"));
            }catch(Exception e){fail("FAIL "+e);}
            finally{SetKeyframeTab(priorEditor);SelectInspector(priorInspector);showInlineCurve=oldInline;showRibbonCurve=oldPanel;OpenProject(baseline);ApplyProfile(initial);}
        }

        System.Collections.IEnumerator CheckCurveDefaults(string directory,Action<string> fail)
        {
            var prior=curveSlots[0];bool changed=customDefault,initial=ribbonProfile,auto=autoCurve.value;
            string path=Path.Combine(directory,"curve-start.avaproject"),preference=WorkspacePreferences.GetString("Curve.Preset.0");
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
            if (track.LastFrame != 10 || track.EditFrames.Between(0, 10).Single() != 0)
                throw new Exception("Base keys and layer duration");
            track.ActiveLayer = layer.id;
            var frames = track.EditFrames;
            if (frames.LowerBound(4) != 1 || frames.LowerBound(11) != frames.Count ||
                !frames.Between(5, 10).SequenceEqual(new[] { 5, 10 }) || frames.Between(6, 9).Any())
                throw new Exception("Layer key range boundaries");
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
            var parent = layer.clips.Single();
            layer.clips.Add(new ClipPlacement { parent = parent.id, start = 100, length = 30 });
            if (track.LastFrame != parent.EndFrame) throw new Exception("Nested clips use parent time");
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
                var shell = MenuRoot.Q("workspace"); shell.Focus();
                target ??= shell; target.Focus();
                using(var down=KeyDownEvent.GetPooled(new Event{type=EventType.KeyDown,keyCode=key,modifiers=modifiers})){down.target=target;target.SendEvent(down);}
                using(var up=KeyUpEvent.GetPooled(new Event{type=EventType.KeyUp,keyCode=key,modifiers=modifiers})){up.target=target;target.SendEvent(up);}
            }
            void Click(Button button)=>ClickSmokeButton(button);
            ApplyProfile(true); Click(graphTab);
            if(!graphDockTab)throw new Exception("Graph tab pointer activation");
            Click(waveTab);
            if(!audioDockTab || !timeline.ClassListContains("hidden"))throw new Exception("Audio panel activation: "+waveTab.worldBound+" / "+audioDockTab+" / "+timeline.ClassListContains("hidden"));
            ChooseWorkspace(WorkspacePreset.Rigging); ChooseWorkspace(WorkspacePreset.Animation);
            if(!audioDockTab)throw new Exception("Audio panel selection lost on profile switch");
            Click(timeTab);if(audioDockTab || timeline.ClassListContains("hidden"))throw new Exception("Timeline panel activation");
            MenuRoot.panel.Pick(Vector2.zero);
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
                MenuRoot.Q("workspace").Focus();
                using (var probe = KeyDownEvent.GetPooled(new Event{type=EventType.KeyDown,keyCode=KeyCode.F11}))
                {
                    probe.target = timeline;
                    if (!RouteNamedKey(probe))
                        throw new Exception("Timeline shortcut routing: area="+CurrentKeyArea(timeline)+", key="+Chord(probe)+
                            ", input="+EditingField(timeline,probe.keyCode)+", focus="+MenuRoot.focusController.focusedElement);
                }
                if(localAxes==initialAxes || bookmarks.Contains(frame))throw new Exception("Timeline shortcut scope: axes changed="+(localAxes!=initialAxes)+" bookmark="+bookmarks.Contains(frame));
                Press(KeyCode.F11,EventModifiers.None,viewport);
                if(!bookmarks.Contains(frame))throw new Exception("Viewport shortcut scope");
                ApplyHistory(false);
                MenuRoot.panel.Pick(Vector2.zero);
                Press(KeyCode.F11,EventModifiers.None,timeline);
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
            ApplyProfile(false);
        }

        void CheckLink(string dir)
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
                var path=Path.Combine(dir,"constraint.avaproject");SaveProject(path);OpenProject(path);
                if(externalParents.Count!=1 || Vector3.Distance(externalParents[0].child.Joint.position,held)>.001f)throw new Exception("Link archive");
                SetExternalParent(externalParents[0].child,null,null);
            }
            ChangeMode(true);
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
            morph.Preview(-.25f);RegisterKey(morph);var path=Path.Combine(dir,"controller.avaproject");
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
                var path=Path.Combine(dir,"workspace.avaproject");SaveProject(path);
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
                Check(!combinedMenus.Any(e=>e.Japanese=="AvaAvaEffect") && FindCommand("ava.mme.assign",out _),"effect menu under Plugins");
                CheckSurfaceOutput(dir);
                CheckLink(dir);CheckControllerIO(dir);
            }
            finally {RequestProfile(initial);}
        }
    }
}
