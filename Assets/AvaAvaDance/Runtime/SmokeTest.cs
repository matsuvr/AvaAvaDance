using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;
namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        private IEnumerator Start()
        {
            var args=Environment.GetCommandLineArgs();
            int input=Array.IndexOf(args,"-avaSmokePackage"), output=Array.IndexOf(args,"-avaSmokeOutput");
            int projectArg=Array.IndexOf(args,"-avaProjectSmoke");
            if(output>=0 && output+1<args.Length){
                string destination=args[output+1];Directory.CreateDirectory(destination);
                int humanoid=Array.IndexOf(args,"-avaHumanoidMotionSmoke");
                int skeleton=Array.IndexOf(args,"-avaSkeletonSamples");
                if (humanoid>=0 || skeleton>=0)
                {
                    Application.runInBackground=true;
                    for (int i=0;i<10;i++) yield return null;
                    string verdict;
                    try
                    {
                        verdict=skeleton>=0 ? SampleHumanoidSkeleton(args[skeleton+1],args[skeleton+2],destination)
                            : CheckHumanoidMotion(args[humanoid+1],args[humanoid+2],destination);
                    }
                    catch(Exception ex) { verdict="FAIL "+ex; Debug.LogException(ex); }
                    File.WriteAllText(Path.Combine(destination,"result.txt"),verdict);
                    Application.Quit(verdict.StartsWith("PASS")?0:1); yield break;
                }
                int mixed=Array.IndexOf(args,"-avaMixedShaderSmoke");
                if(mixed>=0 && mixed+5<args.Length)
                {
                    Application.runInBackground=true; for(int i=0;i<12;i++)yield return null;
                    string verdict;
                    var check=CheckMixedShaders(args[mixed+1],args[mixed+2],args[mixed+3],args[mixed+4],args[mixed+5],destination);
                    while(!check.IsCompleted)yield return null;
                    try { verdict=check.GetAwaiter().GetResult(); }
                    catch(Exception ex){Debug.LogException(ex);verdict="FAIL "+ex;}
                    SaveSmokeVerdict(destination,"-avaMixedShaderSmoke",args[mixed+1],verdict);
                    Application.Quit(verdict.StartsWith("PASS")?0:1);yield break;
                }
                int shaderSmoke=Array.IndexOf(args,"-avaOfficialShaderSmoke");
                if(shaderSmoke>=0 && shaderSmoke+3<args.Length){
                    Application.runInBackground=true;for(int i=0;i<12;i++)yield return null;
                    var verification=CheckOfficialShaders(destination,args[shaderSmoke+1],args[shaderSmoke+2],args[shaderSmoke+3]);
                    while(!verification.IsCompleted)yield return null;
                    string verdict=verification.GetAwaiter().GetResult();
                    SaveSmokeVerdict(destination,"-avaOfficialShaderSmoke",null,verdict);
                    Application.Quit(verdict.StartsWith("PASS")?0:1);yield break;
                }
                if(args.Contains("-avaLayoutSmoke")){
                    Application.runInBackground=true;for(int i=0;i<12;i++)yield return null;
                    string error=null;yield return CheckSymbolLayouts(destination,s=>error=s);
                    SaveSmokeVerdict(destination,"-avaLayoutSmoke",null,error??"PASS keyboard layout and held transform");
                    Application.Quit(error==null?0:1);yield break;
                }
                if(args.Contains("-avaImeSmoke") || args.Contains("-avaDpiSmoke")){
                    Application.runInBackground=true;for(int i=0;i<12;i++)yield return null;
                    string imeVerdict=null;yield return CheckDetachedEditor(destination,error=>imeVerdict=error);
                    imeVerdict??="PASS independent window input and display";
                    SaveSmokeVerdict(destination,string.Join(" ",args.Where(v=>v=="-avaImeSmoke" || v=="-avaDpiSmoke")),null,imeVerdict);
                    Application.Quit(imeVerdict.StartsWith("PASS")?0:1);yield break;
                }
                if(args.Contains("-avaExtensionUiSmoke")){yield return CheckExtensionWindows(destination);yield break;}
                int sizing=Array.IndexOf(args,"-avaSizingSmoke"),effects=Array.IndexOf(args,"-avaEffectSmoke");
                if(effects<0)effects=Array.IndexOf(args,"-avaMmeSmoke");
                int config=sizing>=0?sizing:effects;
                if(config>=0){
                    Application.runInBackground=true;for(int i=0;i<20;i++)yield return null;
                    string verdict;
                    try{verdict=RunFileSmoke(sizing>=0,args[config+1],destination);}
                    catch(Exception ex){verdict="FAIL "+ex;Debug.LogException(ex);}
                    SaveSmokeVerdict(destination,args[config],args.ElementAtOrDefault(config+1),verdict);
                    Application.Quit(verdict.StartsWith("PASS")?0:1);yield break;
                }
            }
            int vrmArg = Array.IndexOf(args, "-avaVrmSmoke");
            if (vrmArg >= 0 && output >= 0)
            {
                Application.runInBackground = true;
                for (int i = 0; i < 10; i++) yield return null;
                string dir = args[output+1];
                Directory.CreateDirectory(dir);
                string verdict;
                try { CheckVrmWorkflow(args[vrmArg+1], args[vrmArg+2], args[vrmArg+3], dir); verdict = "PASS VRM0/1, VRMA import/edit/export, FK/IK, face/gaze and project round trip"; }
                catch (Exception ex) { verdict = "FAIL " + ex; Debug.LogException(ex); }
                File.WriteAllText(Path.Combine(dir,"result.txt"), verdict);
                Application.Quit(verdict.StartsWith("PASS") ? 0 : 1);
                yield break;
            }
            if(projectArg>=0 && output>=0 && output+1<args.Length) {
                Application.runInBackground=true; yield return null;
                string dir=args[output+1];Directory.CreateDirectory(dir);string verdict=null;
                try {
                    OpenProject(args[projectArg+1]);
                    if(args.Contains("-avaPhysBoneSmoke"))CheckPhysBones();
                    if(args.Contains("-avaSwitchToBullet")){Execute(Command.BulletEngine);CheckBulletSeek();}
                    if(args.Contains("-avaExpectLegacy") && physicsEngine!=PhysicsEngine.PhysBone)throw new Exception("Legacy project backend changed");
                }
                catch(Exception ex) {verdict="FAIL "+ex;}
                if(verdict==null) {
                    yield return ExportMovie(Path.Combine(dir,"reopened.mp4"),movie);
                    verdict=movieError==null?"PASS cold project open and export":"FAIL "+movieError;
                }
                File.WriteAllText(Path.Combine(dir,"result.txt"),verdict);Application.Quit(verdict.StartsWith("PASS")?0:1);yield break;
            }
            if(input<0 || input+1>=args.Length || output<0 || output+1>=args.Length) yield break;
            Application.runInBackground=true;
            string directory=args[output+1]; Directory.CreateDirectory(directory);
            for(int i=0;i<20;i++) yield return null;
            string result;
            int packArg=Array.IndexOf(args,"-avaShaderPack");
            bool latePack=args.Contains("-avaLateShaderPack");
            if(latePack){shaderLibrary.Dispose();shaderLibrary=new ShaderLibrary();}
            var loadPack = !latePack && packArg>=0 && packArg+1<args.Length ? LoadShaderFile(args[packArg+1]) : null;
            while (loadPack != null && !loadPack.IsCompleted) yield return null;
            int wavArg = Array.IndexOf(args, "-avaSmokeWav");
            if (wavArg >= 0) yield return LoadWave(args[wavArg + 1]);
            try
            {
                if (wavArg >= 0 && (soundtrack.clip == null || AudioLastFrame < 1 || !peaks.Any(p => p.y > .01f)))
                    throw new Exception("WAV load or waveform sampling failed: " + status.text);
                if(loadPack != null) Debug.Log(loadPack.GetAwaiter().GetResult());
                var package=UnityPackageReader.Read(args[input+1]);
                int fbxArg = Array.IndexOf(args, "-avaSmokeFbx");
                var model = package.First(a => a.HasAsset && a.Path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                    && (fbxArg < 0 || Path.GetFileName(a.Path).Equals(args[fbxArg + 1], StringComparison.OrdinalIgnoreCase)));
                int prefabArg=Array.IndexOf(args,"-avaSmokePrefab");
                var prefab=prefabArg>=0 ? PackageAvatar.Choices(package,new[]{model})
                    .Single(a=>a.Prefab!=null && Path.GetFileName(a.Prefab.Path)==args[prefabArg+1]).Prefab
                    : PackageAvatar.LegacyPrefab(package,model);
                ImportModel(model,package,prefab);
                if(latePack){shaderLibrary.Load(args[packArg+1]);ReapplyPackageShaders();}
                if(args.Contains("-avaMaterialSmoke"))CheckPrefabMaterials(model,prefab,package,directory);
                if(args.Contains("-avaPhysBoneSmoke"))CheckPhysBones();
                if(args.Contains("-avaBulletSmoke"))CheckBulletWorkflow(directory);
                Debug.Log("AVA_ROOT_RECT " + GetComponent<UnityEngine.UIElements.UIDocument>().rootVisualElement.worldBound + " VIEW=" + viewport.worldBound);
                Debug.Log("AVA_MOTION_TRACKS bones=" + actor.bones.Count + " morphs=" + morphGroups.Sum(g => g.Tracks.Count) +
                    " skippedShapes=" + actor.avatar.GetComponent<FbxOwnedResources>().SkippedShapes);
                int expected=Array.IndexOf(args,"-avaExpectedShader");
                if(expected>=0 && expected+1<args.Length) { var mat=actor.avatar.GetComponentInChildren<Renderer>().sharedMaterial; if(mat.shader.name!=args[expected+1]) throw new Exception("Unexpected shader: "+mat.shader.name); if(mat.shader.name=="lilToon" && mat.FindPass("FORWARD")<0) throw new Exception("lilToon FORWARD pass missing"); Debug.Log("AVA_SHADER_VALIDATED "+mat.shader.name+" passes="+mat.passCount+" color="+mat.GetColor("_Color")); }
                int renderers=actor.avatar.GetComponentsInChildren<Renderer>().Length;
                if(renderers<1) throw new Exception("No renderers");
                var skin=actor.avatar.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(s => s.bones.Length > 1);
                if(skin!=null) { var before=new Mesh(); var after=new Mesh(); skin.BakeMesh(before); var rotation=skin.bones[0].localRotation; skin.bones[0].localRotation=Quaternion.Euler(0,0,25)*rotation; skin.BakeMesh(after); bool changed=false; for(int v=0;v<before.vertexCount;v++) if(Vector3.Distance(before.vertices[v],after.vertices[v])>.001f) changed=true; skin.bones[0].localRotation=rotation; Destroy(before); Destroy(after); if(!changed) throw new Exception("Skin did not deform when bone rotated"); }
                bool motionTest = Array.IndexOf(args, "-avaMotionSmoke") >= 0;
                if (args.Contains("-avaProfileOutputSmoke")) CheckProfileOutput(directory);
                if (args.Contains("-avaWorkspaceSmoke")) CheckWorkspace(directory);
                if (args.Contains("-avaSurfaceSmoke")) CheckSurfaceOutput(directory);
                if (args.Contains("-avaScreenHistorySmoke")) CheckScreenHistory(directory);
                if (args.Contains("-avaGrabSmoke")) CheckGrabSequence(directory);
                if (args.Contains("-avaExtensionSmoke")){CheckHostEditing(directory);CheckAutomaticEffects(args[input+1],directory);}
                int commandIndex=Array.IndexOf(args,"-avaSmokeCommand");
                if(commandIndex>=0){
                    var command=args[commandIndex+1];
                    if(!FindCommand(command,out var id) || !Execute(id))throw new Exception("Command failed: "+command);
                    File.WriteAllText(Path.Combine(directory,"command.txt"),status.text);
                }
                if(args.Contains("-avaRigShapesSmoke"))CheckRigShapes(args[input+1]);
                if (motionTest) CheckMotionWorkflow(directory, skin);
                if (args.Contains("-avaMenuSmoke")) CheckMenuCompatibility(directory);
                SetFrame(12);
                result="PASS standalone package->FBX->Renderer; renderers="+renderers+"; skins="+actor.avatar.GetComponentsInChildren<SkinnedMeshRenderer>().Length+"; frame=12" + (motionTest ? "; registered bone/morph/camera/light, playback, history, files" : "");
            }
            catch(Exception ex) { result="FAIL "+ex; Debug.LogException(ex); }
            if(result.StartsWith("PASS") && Array.IndexOf(args,"-avaProductionSmoke")>=0) {
                try { CheckProduction(directory); } catch(Exception ex) { result="FAIL "+ex;Debug.LogException(ex); }
                if(result.StartsWith("PASS")) {
                    yield return ExportMovie(Path.Combine(directory,"dance.mp4"),movie);
                    if(movieError!=null)result="FAIL "+movieError;
                    else result+="; project/IK/physics/shadows/MP4";
                    if(result.StartsWith("PASS") && Array.IndexOf(args,"-avaMovieEdgeSmoke")>=0) {
                        var config=new MovieSettings {first="6",last="10",width=320,height=240,fps=60,audio=false};
                        string segment=Path.Combine(directory,"segment.mp4");
                        yield return ExportMovie(segment,config);
                        if(movieError!=null)result="FAIL "+movieError;
                        var before=File.ReadAllBytes(segment); int previous=frame;
                        var job=ExportMovie(segment,config); job.MoveNext(); cancelExport=true;
                        while(job.MoveNext())yield return job.Current;
                        if(!File.ReadAllBytes(segment).SequenceEqual(before) || exporting || frame!=previous)
                            result="FAIL cancel/restore";
                    }
                }
            }
            if (result.StartsWith("PASS") && args.Contains("-avaMenuSmoke")) {
                yield return ExportMovie(Path.Combine(directory,"menu.avi"),new MovieSettings {width=160,height=120,first="0",last="3",audio=true});
                if(movieError!=null)result="FAIL AVI: "+movieError;
                else {
                    LoadBackdrop(Path.Combine(directory,"menu.avi"),true);
                    double limit=Time.realtimeSinceStartupAsDouble+8;
                    while(videoPlayer!=null && videoPlayer.texture==null && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                    if(videoPlayer==null || videoPlayer.texture==null)result="FAIL background AVI: "+status.text;
                    else result+="; MMD menus/keys/VMD/VPD/AVI";
                    SetBackdrop(null,null,true);viewOptions.showVideo=false;UpdateBackdrop();
                }
            }
            if (result.StartsWith("FAIL")) { SaveSmokeVerdict(directory,"player",args[input+1],result); Application.Quit(1); yield break; }
            if(args.Contains("-avaCurveSmoke")){
                yield return CheckCurveTools(directory,error=>result=error);
                if(result.StartsWith("PASS"))yield return CheckCurveDefaults(directory,error=>result=error);
                if(!result.StartsWith("PASS")){SaveSmokeVerdict(directory,"-avaCurveSmoke",args[input+1],result);Application.Quit(1);yield break;}
            }
            for(int i=0;i<20;i++) yield return null;
            yield return new WaitForEndOfFrame();
            bool captureBack = Array.IndexOf(args,"-avaSmokeBack")>=0;
            try
            {
                var labels = timeline.Query<Label>(className: "track-name").ToList();
                for (int i = 0; i < labels.Count; i++)
                {
                    float center = timeline.WorldToLocal(labels[i].worldBound.center).y;
                    if (Mathf.Abs(center - (i + 1.5f) * RowHeight) > 1)
                        throw new Exception("Timeline row label is offset from its keys at row " + i + ": " + center);
                }
                WritePreview(Path.Combine(directory,"preview.png"));
                if (captureBack) SetViewYaw(180);
            }
            catch (Exception ex) { result="FAIL "+ex; }
            if (captureBack && result.StartsWith("PASS"))
            {
                yield return null;
                yield return new WaitForEndOfFrame();
                try { WritePreview(Path.Combine(directory,"back.png")); }
                catch (Exception ex) { result="FAIL "+ex; }
                SetViewYaw(0);
            }
            if((args.Contains("-avaMenuSmoke") || args.Contains("-avaWorkspaceSmoke")) && result.StartsWith("PASS")) {
                bool captureRibbon=args.Contains("-avaWorkspaceSmoke"),priorProfile=ribbonProfile;
                if(captureRibbon){ApplyProfile(true);SelectRibbonTab(1);if(drawOrder.Count>1)ChooseMode(drawOrder[0].label);} else OpenGroup(MmdMenu.Bar[1]);
                var rt=new RenderTexture(Screen.width,Screen.height,24,RenderTextureFormat.ARGB32);
                var prior=panel.targetTexture;rt.Create();panel.targetTexture=rt;
                try {
                    for(int i=0;i<4;i++)yield return null;
                    yield return new WaitForEndOfFrame();
                    var previous=RenderTexture.active;var image=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
                    try {
                        RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);image.Apply();
                        if(!image.GetPixels32().Any(c=>c.r>120 && c.a>240))throw new IOException("Blank menu panel.");
                        File.WriteAllBytes(Path.Combine(directory,captureRibbon?"ribbon.png":"mmd-menu.png"),image.EncodeToPNG());
                    } catch(Exception ex){result="FAIL "+ex;} finally {RenderTexture.active=previous;Destroy(image);}
                } finally {CloseMenu();panel.targetTexture=prior;rt.Release();Destroy(rt);if(captureRibbon)ApplyProfile(priorProfile);}
            }
            // A batch player has no screen backbuffer. Its actual camera render is captured above.
            if (!Application.isBatchMode && result.StartsWith("PASS"))
            {
                while (!UnityEngine.Rendering.SplashScreen.isFinished) yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                Texture2D image=null;
                try
                {
                    image=ScreenCapture.CaptureScreenshotAsTexture();
                    if (image==null) throw new IOException("Window capture failed.");
                    File.WriteAllBytes(Path.Combine(directory,"standalone.png"),image.EncodeToPNG());
                }
                catch (Exception ex) { result="FAIL "+ex; }
                finally { if(image!=null) Destroy(image); }
            }
            SaveSmokeVerdict(directory,string.Join(" ",args.Where(a=>a.StartsWith("-ava") && a.EndsWith("Smoke"))),args[input+1],result);
            Debug.Log(result); Application.Quit(result.StartsWith("PASS")?0:1);
        }

        void CheckPrefabMaterials(UnityPackageReader.PackageAsset model, UnityPackageReader.PackageAsset prefab,
            System.Collections.Generic.List<UnityPackageReader.PackageAsset> assets, string directory)
        {
            if(prefab==null)throw new InvalidDataException("Expected a prefab for the material comparison.");
            var bindings=AvatarMaterialBindings.Read(model,prefab);
            var source=assets.ToDictionary(a=>a.Guid,StringComparer.OrdinalIgnoreCase);
            var report=new StringBuilder();
            int checkedSlots=0;
            void Require(bool ok,string property){if(!ok)throw new InvalidDataException("Material mismatch: "+property);}
            foreach(var r in actor.avatar.GetComponentsInChildren<Renderer>())
            {
                if(!bindings.Renderers.TryGetValue(r.name,out var slots) || slots==null)continue;
                Require(r.enabled==slots.Visible,r.name+" visibility");
                if(!slots.Visible)continue;
                var mats=r.sharedMaterials;
                Require(mats.Length==slots.Materials.Length && (int)r.shadowCastingMode==slots.CastShadows && r.receiveShadows==slots.ReceiveShadows,r.name+" renderer");
                for(int i=0;i<mats.Length;i++)
                {
                    var guid=slots.Materials[i];
                    if(guid==null){Require(mats[i]==null,r.name+" empty slot");continue;}
                    var entry=source[guid];var data=UnityMaterialData.Parse(Encoding.UTF8.GetString(entry.Data));
                    var mat=mats[i];var expected=shaderLibrary.ResolveMaterial(data.ShaderGuid,data.ShaderFileId,null);
                    Require(expected!=null && mat.shader==expected && mat.name==data.Name,entry.Path+" material");
                    Require(mat.renderQueue==(data.CustomRenderQueue<0?expected.renderQueue:data.CustomRenderQueue),entry.Path+" renderQueue");
                    foreach(var p in data.Floats.Where(p=>mat.HasProperty(p.Key)))
                        Require(Mathf.Approximately(mat.GetFloat(p.Key),p.Value),entry.Path+" "+p.Key);
                    foreach(var p in data.Colors.Where(p=>mat.HasProperty(p.Key)))
                    {
                        var v=p.Value;
                        Require(Vector4.Distance(mat.GetVector(p.Key),new Vector4(v.R,v.G,v.B,v.A))<.0001f,entry.Path+" "+p.Key);
                    }
                    foreach(var p in data.Textures.Where(p=>mat.HasProperty(p.Key)))
                    {
                        var v=p.Value;
                        Require(mat.GetTextureScale(p.Key)==new Vector2(v.Scale.X,v.Scale.Y) && mat.GetTextureOffset(p.Key)==new Vector2(v.Offset.X,v.Offset.Y),entry.Path+" "+p.Key+" UV");
                        if(v.Guid==null || !source.TryGetValue(v.Guid,out var image) || !image.Path.EndsWith(".png",StringComparison.OrdinalIgnoreCase))continue;
                        var tex=mat.GetTexture(p.Key) as Texture2D;
                        Require(tex!=null && tex.name==Path.GetFileNameWithoutExtension(image.Path),entry.Path+" "+p.Key+" texture");
                    }
                    Require(shadowDraws.Any(d=>d.renderer==r && d.slot==i && d.native),entry.Path+" shadow routing");
                    report.AppendLine(r.name+"["+i+"] "+entry.Path+" -> "+mat.shader.name);
                    checkedSlots++;
                }
            }
            Require(checkedSlots>0 && previewLamp.shadows!=LightShadows.None,"no native shadow receivers");
            var pose=shadowTrack.Current;var off=pose;off.Mode=0;ApplyShadow(off);
            Require(previewLamp.shadows==LightShadows.None && actor.shadowCasters.All(r=>!r.receiveShadows),"shadow off");
            ApplyShadow(pose);
            foreach(var t in actor.avatar.GetComponent<FbxOwnedResources>().PackageTextures.Values)
                report.AppendLine(t.name+" "+t.width+"x"+t.height+" mips="+t.mipmapCount+" "+t.graphicsFormat);
            File.WriteAllText(Path.Combine(directory,"materials.txt"),report.ToString());
            Debug.Log("AVA_PREFAB_MATERIALS_VERIFIED slots="+checkedSlots);
        }

        void CheckBulletSeek()
        {
            if(physicsEngine!=PhysicsEngine.Bullet || BulletWorld.ava_bullet_version()!=325)
                throw new Exception("Bullet engine unavailable");
            physicsMode=PhysicsMode.Trace;InvalidateDynamics();
            SetFrame(30);
            if(bulletLinks.Count==0)throw new Exception("Missing Bullet bodies");
            var first=bulletLinks.Select(b=>b.Solved).ToArray();
            SetFrame(10);SetFrame(30);
            float error=0;
            for(int i=0;i<first.Length;i++){
                float distance=Vector3.Distance(first[i].Position,bulletLinks[i].Solved.Position);
                if(!float.IsFinite(distance) || Quaternion.Angle(first[i].Rotation,bulletLinks[i].Solved.Rotation)>.1f)
                    throw new Exception("Non-reproducible Bullet pose");
                error=Mathf.Max(error,distance);
            }
            if(error>.0001f)throw new Exception("Bullet seek error: "+error);
            Debug.Log("AVA_BULLET_SEEK bodies="+first.Length+" maxPositionError="+error);
        }
        void CheckBulletWorkflow(string dir)
        {
            void Check(bool ok,string msg){if(!ok)throw new Exception("Bullet: "+msg);}
            Check(physicsEngine==PhysicsEngine.Bullet,"new project default");
            CheckBulletSeek();SetFrame(0);
            var link=actor.dynamicLinks.Last();var name=link.Bone.Track.Name;
            SelectTrack(link.Bone.Track);
            Execute(Command.EditPhysics);
            var panel=modal.Q("bullet-properties");
            Check(panel!=null && panel.enabledInHierarchy,"body editor");
            var shape=panel.Query<DropdownField>().ToList()[0];var mode=panel.Query<DropdownField>().ToList()[1];
            shape.index=1;mode.index=1;
            panel.Q<FloatField>().value=2.5f;panel.Query<IntegerField>().ToList()[1].value=0;
            panel.Q<Toggle>().value=true;CloseDialog();
            Check(actor.modelOverrides.bullet.Single(b=>b.name==name).free,"body editor persisted");
            SetFrame(0);float initial=link.Bone.Joint.position.y;var local=link.Bone.Joint.localPosition;
            SetFrame(30);Check(link.Bone.Joint.position.y<initial-.03f,"free body gravity");
            var off=(BonePose)link.Bone.Track.KeyAt(0);off.PhysicsOff=true;
            link.Bone.Track.Write(30,off);InvalidateDynamics();SetFrame(30);
            Check(Vector3.Distance(link.Bone.Joint.localPosition,local)<.0001f,"keyed physics off");
            link.Bone.Track.Write(30,null);InvalidateDynamics();SetFrame(30);
            var original=link.Bone.Joint.position;
            SaveProject(Path.Combine(dir,"bullet-settings.avadance"));
            NewProject();OpenProject(Path.Combine(dir,"bullet-settings.avadance"));
            var restored=actor.modelOverrides.bullet.Single(s=>s.name==name);
            Check(restored.shape==1 && restored.mode==1 && restored.mass==2.5f && restored.mask==0 && restored.free,"saved body settings");
            SetFrame(30);Check(Vector3.Distance(original,actor.dynamicLinks.Single(d=>d.Bone.Track.Name==name).Bone.Joint.position)<.0001f,"saved world");
            Execute(Command.PhysBoneEngine);Check(physicsEngine==PhysicsEngine.PhysBone && bullet==null,"legacy backend selection");
            Execute(Command.BulletEngine);Check(physicsEngine==PhysicsEngine.Bullet && bullet!=null,"Bullet menu selection");
            Execute(Command.PhysicsOff);Check(bullet==null,"physics disabled");Execute(Command.PhysicsAlways);
            IdleBullet();Execute(Command.ResetBodies);Check(bullet!=null,"reset");
            Debug.Log("AVA_BULLET_WORKFLOW native version=325 gravity, keyed follow, settings, menu and project roundtrip");
        }

        void CheckPhysBones()
        {
            void Check(bool ok,string message) {if(!ok)throw new Exception(message);}
            Check(actor.authoredLinks.Count>0 && actor.dynamicLinks.Count>0,"Missing prefab PhysBones");
            if(actor.avatar.name=="Rig") {
                Check(actor.dynamicLinks.Count==2,"Ignored branch received physics");
                var a=actor.dynamicLinks.Single(d=>d.Bone.Joint.name=="Pendant");
                var b=actor.dynamicLinks.Single(d=>d.Bone.Joint.name=="TestBone");
                Check(a.Pull==.34f && b.Pull==.68f && a.Pitch==12 && a.Gravity==.65f && a.Colliders.Length==1 &&
                    b.Children.Length==0 && b.Endpoint.sqrMagnitude>0,"Serialized PhysBone values differ");
                Check(actor.modelAssets.Any(asset=>asset.Path.EndsWith(".controller")) && actor.modelAssets.Any(asset=>!asset.HasAsset),"Package data discarded");
            }
            int savedFrame=frame;
            var root=actor.avatar.transform;Vector3 start=root.position;
            Quaternion startRotation=root.rotation;
            var previous=new Vector3[actor.dynamicLinks.Count];
            float drift=0,worstLimitError=0;
            string settlingBone="";
            try {
                foreach(var b in actor.bones)b.Track.Seek(0);
                InvalidateDynamics();
                // A quick turn and lateral movement followed by four seconds at rest.
                for(int tick=0;tick<=300;tick++) {
                    float wave=tick<=60 ? Mathf.Sin(tick*Mathf.PI/30) : 0;
                    root.SetPositionAndRotation(start+Vector3.right*(wave*.15f),Quaternion.AngleAxis(wave*45,Vector3.up)*startRotation);
                    foreach(var b in actor.bones)b.Track.Preview(b.Track.Current);
                    SolveIK();StepDynamics(tick,true);
                    for(int i=0;i<actor.dynamicLinks.Count;i++) {
                        var d=actor.dynamicLinks[i]; Vector3 tip=d.TipPosition;
                        Check(float.IsFinite(tip.sqrMagnitude),"Non-finite PhysBone position");
                        float travel=Vector3.Distance(tip,previous[i]);
                        if(tick>280 && travel>drift){drift=travel;settlingBone=d.Bone.Track.Name;}
                        previous[i]=tip;
                        if(!d.State.Ready || d.Limit==0)continue;
                        var joint=d.Bone.Joint;
                        var solved=joint.localRotation;
                        joint.localRotation=d.Animated ? d.BindRotation*d.Bone.Track.Current.Rotation : d.BindRotation;
                        Vector3 rest=(d.TipPosition-joint.position).normalized;
                        var basis=LimitFrame(d,rest);
                        joint.localRotation=solved;
                        var direction=(tip-joint.position).normalized;
                        if(rest!=Vector3.zero && direction!=Vector3.zero)
                            worstLimitError=Mathf.Max(worstLimitError,Vector3.Angle(direction,LimitDirection(d,basis,direction)));
                    }
                }
                Debug.Log($"AVA_PHYSBONE_STABILITY joints={actor.dynamicLinks.Count} settledMotion={drift:G4} bone={settlingBone} maxLimitErrorDegrees={worstLimitError:G4}");
                Check(drift<.001f,"Stationary hair/skirt kept oscillating");
                Check(worstLimitError<.15f,"Authored angular limits exceeded");
                InvalidateDynamics();EvaluateAnimation(75,true);
                var first=actor.dynamicLinks.Select(d=>d.TipPosition).ToArray();
                EvaluateAnimation(30,true);EvaluateAnimation(75,true);
                Check(actor.dynamicLinks.Select((d,i)=>Vector3.Distance(d.TipPosition,first[i])).Max()<.0001f,"Cached seek changed PhysBone positions");
            } finally {
                root.SetPositionAndRotation(start,startRotation);
                InvalidateDynamics(); SetFrame(savedFrame);
            }
        }

        void CheckProduction(string directory)
        {
            void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
            Vector3[] SampleDynamics() {
                InvalidateDynamics();
                var samples=new Vector3[60*actor.dynamicLinks.Count];
                for(int f=0;f<60;f++) {
                    EvaluateAnimation(f,true);
                    for(int i=0;i<actor.dynamicLinks.Count;i++)samples[f*actor.dynamicLinks.Count+i]=actor.dynamicLinks[i].TipPosition;
                }
                return samples;
            }
            if(CurrentWave!=null){
                var originalWave=CurrentWave;bool priorProfile=ribbonProfile;ApplyProfile(true);
                var before=undo.Count;DeletePanelSelection(waveform);
                Check(CurrentWave==null && undo.Count==before+1,"Audio panel selection delete");
                ApplyProfile(false);ApplyHistory(false);Check(CurrentWave==originalWave && soundtrack.clip.samples==originalWave.samples,"Audio deletion undo");
                ApplyHistory(true);Check(soundtrack.clip==null,"Audio deletion redo");
                ApplyHistory(false);ApplyProfile(priorProfile);
            }
            Check(actor.ikChains.Count>0,"No foot IK chain"); Check(actor.dynamicLinks.Count>0,"No dynamic chain");
            var ik=actor.ikChains.First(c=>c.Lower!=null);
            var original=ik.Tip.position; float length=Vector3.Distance(ik.Upper.position,original);
            var displaced=original+front*(length*.14f)+Vector3.up*(length*.16f);
            ChangeMode(true);SetFrame(24);SelectTrack(ik.Handle.Track);
            var pose=BonePose.Rest; pose.Position=ik.Handle.Joint.parent.InverseTransformVector(displaced-original);
            PreviewBone(pose); RegisterKey(ik.Handle.Track);
            Check(Vector3.Distance(ik.Tip.position,displaced)<length*.02f,"IK did not reach target");
            SetIKEnabled(ik,false);RefreshRigPreview();
            Check(Vector3.Distance(ik.Tip.position,original)<.01f,"IK off did not restore FK");
            SetIKEnabled(ik,true);RegisterKey(actor.modelSwitch);
            SetFrame(48);RegisterKey(ik.Handle.Track);SetFrame(60);
            ik.Handle.Track.Preview(BonePose.Rest);RegisterKey(ik.Handle.Track);
            var shoulder=actor.bones.Find(b=>b.Joint.name=="UpperArm_L" || b.Joint.name=="LeftUpperArm");
            var hips=actor.bones.Find(b=>b.Joint.name=="Hips");
            if(shoulder!=null || hips!=null) {
                for(int i=0;i<3;i++) {
                    SetFrame(15+18*i);
                    if(shoulder!=null) {
                        var arm=BonePose.Rest;arm.Rotation=Quaternion.Euler(12,i==1?-14:18,i==1?25:52);
                        shoulder.Track.Preview(arm);RegisterKey(shoulder.Track);
                    }
                    if(hips!=null) {
                        var poseHip=BonePose.Rest;
                        poseHip.Position=hips.Joint.parent.InverseTransformVector(Vector3.right*(i==1?-.035f:.035f));
                        hips.Track.Preview(poseHip);RegisterKey(hips.Track);
                    }
                }
            }
            var face=actor.modelTracks.OfType<MotionTrack<float>>().FirstOrDefault(t=>t.Name.ToLowerInvariant().Contains("blink"));
            if(face!=null){SetFrame(20);face.Preview(1);RegisterKey(face);SetFrame(25);face.Preview(0);RegisterKey(face);}
            ChangeMode(false); SetFrame(60);
            var cam=CameraPose.Initial;cam.Angles.y=22;cam.Distance=42;PreviewCameraPose(cam);RegisterKey(cameraTrack);
            var g=GravityPose.Initial;g.Direction=new Vector3(.6f,-.7f,.25f);g.Acceleration=14;
            gravityTrack.Preview(g);RegisterKey(gravityTrack);
            shadowTrack.Preview(new ShadowPose {Mode=2,Range=7800});RegisterKey(shadowTrack);
            physicsMode=PhysicsMode.Trace;SetFrame(24);
            var simulatedTips=actor.dynamicLinks.Select(d=>d.TipPosition).ToArray(); RefreshRigPreview();
            int sampleIndex=Enumerable.Range(0,actor.dynamicLinks.Count).OrderByDescending(i=>(actor.dynamicLinks[i].TipPosition-simulatedTips[i]).sqrMagnitude).First();
            var link=actor.dynamicLinks[sampleIndex];var simulated=simulatedTips[sampleIndex];string sampledBone=link.Bone.Track.Name;
            float motionError=Vector3.Distance(simulated,link.TipPosition);
            Check(motionError>1e-5f,"Authored dynamics did not respond to the motion");
            SetFrame(5);SetFrame(24);Check(Vector3.Distance(simulated,link.TipPosition)<.0005f,"Backward seek changed simulation");
            ChangeMode(true);
            int width=actor.bones.Count>50?1280:640;
            movie=new MovieSettings {width=width,height=width*9/16,fps=30,first="0",last="59",audio=true};
            var beforeSave=Environment.GetCommandLineArgs().Contains("-avaPhysBoneSmoke") ? SampleDynamics() : null;
            SetFrame(12); var name=actor.avatar.name; var ikName=ik.Handle.Track.Name;
            playFrom.value="4";playTo.value="50";repeat.value=true;
            int keys=actor.modelTracks.Sum(t=>t.Frames.Count); int samples=soundtrack.clip.samples;
            string project=Path.Combine(directory,"dance.avadance"); SaveProject(project);
            NewProject(); Check(actor.avatar==null && soundtrack.clip==null,"New project retained previous content");
            if(Environment.GetCommandLineArgs().Contains("-avaMovieEdgeSmoke")) {
                string empty=Path.Combine(directory,"empty.avadance");SaveProject(empty);OpenProject(empty);
                Check(actor.avatar==null && soundtrack.clip==null && LastFrame()==0,"Empty project round trip");
            }
            OpenProject(project);
            Check(actor.avatar!=null && actor.avatar.name==name && samples==soundtrack.clip.samples && frame==12 && modelMode && repeat.value &&
                playFrom.value=="4" && playTo.value=="50" && actor.modelTracks.Sum(t=>t.Frames.Count)==keys && shadowTrack.KeyAt(60)!=null &&
                gravityTrack.KeyAt(60)!=null && movie.width==width && movie.fps==30,"Project restoration failed");
            if(beforeSave!=null) {
                var restored=SampleDynamics();
                Check(beforeSave.Length==restored.Length,"Project changed the number of simulated joints");
                float error=restored.Select((point,i)=>Vector3.Distance(point,beforeSave[i])).Max();
                Debug.Log($"AVA_PHYSBONE_ROUNDTRIP samples={restored.Length} maxPositionError={error:G4}");
                Check(error<.0001f,"Project changed PhysBone trajectories");
            }
            SetFrame(24); Check(Vector3.Distance(actor.ikChains.First(c=>c.Handle.Track.Name==ikName).Tip.position,displaced)<length*.02f,"Restored IK incorrect");
            playFrom.value="0";playTo.value="59";TogglePlayback();AdvancePlayback(startedAt+24.01/30);
            Check(Vector3.Distance(actor.dynamicLinks.Single(d=>d.Bone.Track.Name==sampledBone).TipPosition,simulated)<.0005f,"Playback and seek physics differ");
            StopPlayback();SetFrame(12);
            if(Environment.GetCommandLineArgs().Contains("-avaMovieEdgeSmoke"))CheckBrokenProject(project,directory);
            groundShadow=true; ApplyShadow(shadowTrack.Current); WritePreview(Path.Combine(directory,"ground-on.png"));
            groundShadow=false; ApplyShadow(shadowTrack.Current); WritePreview(Path.Combine(directory,"ground-off.png"));
            actor.modelSelfShadow=false; WritePreview(Path.Combine(directory,"self-off.png"));
            groundShadow=actor.modelSelfShadow=true; ApplyShadow(shadowTrack.Current);
            Debug.Log("AVA_PRODUCTION_OK IK="+actor.ikChains.Count+" dynamic="+actor.dynamicLinks.Count+" displacement="+motionError+" audio="+samples);
        }

        void CheckBrokenProject(string project,string directory)
        {
            string bad=Path.Combine(directory,"invalid.avadance");
            File.Copy(project,bad,true);
            try {
                using(var file=File.Open(bad,FileMode.Open,FileAccess.ReadWrite))
                using(var zip=new ZipArchive(file,ZipArchiveMode.Update)) {
                    var entry=zip.GetEntry("project.json");
                    Project invalid;
                    using(var input=new StreamReader(entry.Open()))invalid=JsonUtility.FromJson<Project>(input.ReadToEnd());
                    invalid.sceneMotion.format="invalid";entry.Delete();
                    AddEntry(zip,"project.json",Encoding.UTF8.GetBytes(JsonUtility.ToJson(invalid)));
                }
                ChangeMode(false);SetFrame(7);RegisterKey(lightTrack);
                int count=undo.Count;string current=JsonUtility.ToJson(CaptureProject());
                try {OpenProject(bad);throw new Exception("Invalid project accepted");}
                catch(InvalidDataException) { }
                if(undo.Count!=count || JsonUtility.ToJson(CaptureProject())!=current)throw new Exception("Failed open changed current project");
                ApplyHistory(false);
                if(lightTrack.KeyAt(7)!=null)throw new Exception("History lost after failed open");
                ChangeMode(true);SetFrame(12);
            } finally {File.Delete(bad);}
        }

        private void CheckMotionWorkflow(string directory, SkinnedMeshRenderer skin)
        {
            void Check(bool condition, string message)
            { if (!condition) throw new InvalidOperationException(message); }
            Check(skin != null, "Motion fixture requires a skinned mesh");
            var joint = actor.bones.First(b => b.Joint == skin.bones[0]);
            SelectTrack(joint.Track);
            var rest = joint.Joint.localRotation;
            SetFrame(24);
            DragAxis(2, true, 120);
            Check(Mathf.Abs(transformFields[5].value - 60) < .1f, "Local Z rotation handle");
            RegisterKey(joint.Track);
            SetFrame(12);
            Check(Mathf.Abs(Quaternion.Angle(rest, joint.Joint.localRotation) - 30) < .1f, "Interpolated bone rotation");
            JumpKey(true);
            Check(frame == 24, "Next key on scrolled timeline");
            curveChannel.index = 3;
            WriteCurve(new EaseCurve { custom = true, a = new Vector2(.7f,0), b = new Vector2(1,.2f) });
            SetFrame(12);
            Check(Quaternion.Angle(rest, joint.Joint.localRotation) < 15, "Rotation curve did not change midpoint");
            SetFrame(24);
            ApplyHistory(false); SetFrame(12);
            Check(Mathf.Abs(Quaternion.Angle(rest, joint.Joint.localRotation) - 30) < .1f, "Undo curve");
            SetFrame(24); CopyKey(); SetFrame(48); PasteKeys();
            WriteKey(joint.Track, null); ApplyHistory(false);
            Check(joint.Track.KeyAt(48) != null, "Undo key deletion");
            ApplyHistory(true); Check(joint.Track.KeyAt(48) == null, "Redo key deletion");
            var face = actor.modelTracks.OfType<MotionTrack<float>>().First();
            var group = morphGroups.First(g => g.Tracks.Contains(face));
            SetFrame(24); group.Weight.value = 1; RegisterKey(face);
            SetFrame(12);
            int shapeIndex = skin.sharedMesh.GetBlendShapeIndex(face.Name.Substring(3));
            Check(shapeIndex >= 0 && Mathf.Abs(skin.GetBlendShapeWeight(shapeIndex) - 50) < .01f, "Morph midpoint");
            var shaped = new Mesh(); var neutral = new Mesh(); skin.BakeMesh(shaped);
            skin.SetBlendShapeWeight(shapeIndex, 0); skin.BakeMesh(neutral);
            Check(shaped.vertices.Zip(neutral.vertices, Vector3.Distance).Max() > .01f, "Morph changes skinned geometry");
            Destroy(shaped); Destroy(neutral);
            SetFrame(24);
            string bonePath = Path.Combine(directory, "bone.avamotion");
            SaveMotion(bonePath);
            SetFrame(24); WriteKey(joint.Track, null);
            LoadMotion(bonePath);
            Check(joint.Track.KeyAt(24) != null && face.Current == 1 && Quaternion.Angle(rest, joint.Joint.localRotation) > 59, "Bone file round trip");

            ChangeMode(false); SetFrame(24);
            transformFields[4].value = 90;
            fov.value = 40; perspective.value = false; RegisterKey(cameraTrack);
            lightSliders[0].value = 54; RegisterKey(lightTrack);
            SetFrame(12);
            Check(Mathf.Abs(cameraTrack.Current.Angles.y - 45) < .01f && previewCamera.fieldOfView == 35 &&
                !previewCamera.orthographic, "Camera midpoint");
            Check(Mathf.Abs(previewLamp.color.r * 255 - 104) < .01f, "Light midpoint");
            Check(floor.transform.position == Vector3.zero && previewLamp.transform.parent != previewCamera.transform, "Scene follows camera");
            SelectKeys(24, 24, true); CopyKey(); SetFrame(48); PasteKeys();
            Check(lightTrack.KeyAt(48) != null && cameraTrack.KeyAt(48) != null, "Vertical key paste");
            ApplyHistory(false);
            Check(lightTrack.KeyAt(48) == null && cameraTrack.KeyAt(48) == null, "Grouped undo");
            SetFrame(24); ShiftColumn(false, true);
            Check(cameraTrack.KeyAt(25) != null && cameraTrack.KeyAt(24) == null && lightTrack.KeyAt(24) != null, "Insert camera column");
            ShiftColumn(false, false);
            Check(cameraTrack.KeyAt(24) != null && cameraTrack.KeyAt(25) == null, "Delete camera column");
            string scenePath = Path.Combine(directory, "scene.avamotion");
            SaveMotion(scenePath); SetFrame(24); WriteKey(cameraTrack, null); LoadMotion(scenePath);
            Check(previewCamera.orthographic && cameraTrack.Current.Angles.y == 90, "Camera file round trip");
            string badPath = Path.Combine(directory, "bad.avamotion");
            File.WriteAllText(badPath, "{}");
            try { LoadMotion(badPath); throw new Exception("Accepted invalid motion"); }
            catch (InvalidDataException) { Check(cameraTrack.KeyAt(24) != null, "Failed load damaged motion"); }

            ChangeMode(true);
            playFrom.value = "8"; playTo.value = "15"; repeat.value = false;
            TogglePlayback(); Check(playing && frame == 8, "Playback start");
            AdvancePlayback(startedAt + 4.1 / 30);
            Check(frame == 12 && Mathf.Abs(Quaternion.Angle(rest, joint.Joint.localRotation) - 30) < .1f, "Playback midpoint");
            AdvancePlayback(startedAt + 9.0 / 30);
            Check(!playing && frame == 15, "Playback end");
            repeat.value = true; TogglePlayback(); AdvancePlayback(startedAt + 8.1 / 30);
            Check(playing && frame == 8, "Playback repeat");
            StopPlayback();
            repeat.value = false; playFrom.value = ""; playTo.value = "";
            SetFrame(24);
            ExportImage(Path.Combine(directory, "frame24.png"));
        }

    }
}
