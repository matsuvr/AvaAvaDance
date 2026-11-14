using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class AssetInfo { public string guid,path; public bool metadataOnly; }
        [Serializable] class ModelState
        {
            public string id, label, modelGuid, prefabGuid;
            public List<AssetInfo> assets;
            public SavedMotion modelMotion, modelDraft;
            public bool visible, selfShadow;
            public string[] customIK, dynamicOn, dynamicOff;
            public ModelOverrides modelSettings;
            [NonSerialized] public List<Asset> payload;
        }
        [Serializable] sealed class SelectedKeyState { public string track; public int frame; }
        [Serializable] sealed class SessionSelection
        {
            public string active,bone;
            public string[] rows,bones;
            public SelectedKeyState[] keys;
            public bool range;
        }
        [Serializable] sealed class Project : ModelState
        {
            public string format="AvaAvaDance project 2", start, end, selection, soundName;
            public List<ModelState> others;
            public int schemaVersion=2, extensionStorageVersion=1;
            public int[] bookmarks;
            public BookmarkNote[] bookmarkNotes;
            public SessionSelection selected;
            [NonSerialized] public Dictionary<string,byte[]> extensionPayload;
            public string[] renderOrder, simulationOrder;
            public int shaderCount, frame, samples, rate, channels, physics, engine;
            public SavedMotion sceneMotion,sceneDraft;
            public bool modelMode, repeat, grid, fromCamera, mute, scrub, groundShadow, floor, forcePhysics, showBodies;
            public float volume,groundBrightness;
            public bool transparentGround,showSelfShadow;
            public CameraPose editorView;
            public MovieSettings output;
            public ViewOptions display;
            public List<AccessoryData> accessories;
            public BoneLink[] constraints;
            public int screenMode;
            [NonSerialized] public byte[] image, video;
        }
        const int MaxProjectJson=512*1024*1024;

        string projectPath, savedState;
        byte[] savedImage, savedVideo;
        Asset[] savedModels;
        WaveState savedWave;
        bool allowQuit,drainingExtensions;
        System.Threading.Tasks.Task shutdownTask;

        void RememberModel(Asset model,List<Asset> package,Asset prefab)
        {
            sourceRevision++;actor.modelGuid=model.Guid;
            actor.prefabGuid=prefab?.Guid;
            actor.modelAssets.AddRange(package);
        }

        void CaptureActor(ModelState state,bool contentOnly=false)
        {
            state.id=actor.avatar==null?null:actor.id;state.label=actor.label;state.modelGuid=actor.modelGuid;state.prefabGuid=actor.prefabGuid;
            state.modelSettings=actor.modelOverrides;state.visible=!contentOnly && actor.avatar!=null && actor.avatar.activeSelf;
            state.selfShadow=actor.modelSelfShadow;state.customIK=actor.customIK.ToArray();
            state.dynamicOn=actor.extraDynamic.ToArray();state.dynamicOff=actor.excludedDynamic.ToArray();
            state.assets=actor.modelAssets.Select(a=>new AssetInfo{guid=a.Guid,path=a.Path,metadataOnly=!a.HasAsset}).ToList();
            state.payload=actor.modelAssets.ToList();
            if(actor.avatar!=null){state.modelMotion=CaptureMotion(true,false,contentOnly);state.modelDraft=CaptureMotion(true,true,contentOnly);}
        }

        Project CaptureProject(bool contentOnly=false)
        {
            var clip=soundtrack.clip;
            var selectedState=contentOnly?null:new SessionSelection {
                active=selectedTrack?.Id,bone=actor.selectedBone?.Track.Id,range=rangeSelected,
                rows=selectedRows.Select(t=>t.Id).ToArray(),bones=boneSelection.Select(b=>b.Track.Id).ToArray(),
                keys=selectedKeys.Select(k=>new SelectedKeyState{track=k.track.Id,frame=k.frame}).ToArray()
            };
            var p=new Project {
                bookmarkNotes=bookmarkMemos.OrderBy(p=>p.Key).Select(p=>new BookmarkNote{frame=p.Key,memo=p.Value}).ToArray(),bookmarks=bookmarks.ToArray(),selected=selectedState, extensionPayload=new Dictionary<string,byte[]>(extraEntries),
                accessories=props.Select(a=>a.Data).ToList(),constraints=externalParents.ToArray(),screenMode=screenMode,
                display=JsonUtility.FromJson<ViewOptions>(JsonUtility.ToJson(viewOptions)), image=imageBytes, video=videoBytes,
                shaderCount=shaderLibrary.ProjectPacks.Count(), frame=frame, selection=selectedTrack?.Name,
                sceneMotion=CaptureMotion(false,false,contentOnly), sceneDraft=CaptureMotion(false,true,contentOnly),
                modelMode=modelMode,editorView=modelView,fromCamera=useCameraView,
                repeat=transportRepeat,start=transportStart,end=transportEnd,grid=gridToggle.value,
                samples=clip!=null?clip.samples:0,rate=clip!=null?clip.frequency:0,channels=clip!=null?clip.channels:0,soundName=clip!=null?clip.name:null,
                volume=soundtrack.volume,mute=muteWave,scrub=scrubSound,groundShadow=groundShadow,
                groundBrightness=groundBrightness,transparentGround=transparentGround,showSelfShadow=showSelfShadow,
                engine=(int)physicsEngine,physics=(int)physicsMode,floor=physicalFloor,forcePhysics=forceOnPlayback,showBodies=showBodies,
                output=movie, renderOrder=drawOrder.Select(a=>a.id).ToArray(),simulationOrder=calculationOrder.Select(a=>a.id).ToArray()
            };
            var selected=actor; p.others=new List<ModelState>();
            try {
                if(contentOnly && drawOrder.Count>0)actor=drawOrder[0];
                var root=actor;CaptureActor(p,contentOnly);
                foreach(var item in drawOrder)if(item!=root){actor=item;var m=new ModelState();CaptureActor(m,contentOnly);p.others.Add(m);}
            } finally {actor=selected;}
            if(contentOnly)
            {
                bool hasDraft=p.sceneDraft.tracks.Count>0 || p.modelDraft?.tracks.Count>0 || p.others.Any(m=>m.modelDraft?.tracks.Count>0);
                if(!hasDraft)p.frame=0;
                p.selection=null; p.modelMode=false; p.editorView=default; p.fromCamera=false; p.grid=false;
                p.display.information=false;p.display.follow=false;p.display.translucent=false;p.showBodies=false;
            }
            return p;
        }

        IEnumerable<Asset> CurrentModels() => drawOrder.Select(a=>a.modelAssets.FirstOrDefault(m=>m.Guid==a.modelGuid));

        void MarkSaved()
        {
            savedEntries=new Dictionary<string,byte[]>(extraEntries); savedImage=imageBytes; savedVideo=videoBytes;
            savedState=JsonUtility.ToJson(CaptureProject(true)); savedModels=CurrentModels().ToArray(); savedWave=CurrentWave;
        }

        bool ProjectChanged() => savedEntries==null || savedEntries.Count!=extraEntries.Count || extraEntries.Any(e=>!savedEntries.TryGetValue(e.Key,out var prior) || !prior.SequenceEqual(e.Value)) || savedImage!=imageBytes || savedVideo!=videoBytes || !CurrentModels().SequenceEqual(savedModels) ||
            savedWave!=CurrentWave || savedState!=JsonUtility.ToJson(CaptureProject(true));

        bool SaveProjectCommand(bool saveAs)
        {
            if (importing || loadingWave || exporting) return false;
            StopPlayback();
            try {
                string path=saveAs || projectPath==null?PackageFileDialog.Save("名前を付けて保存","avadance"):projectPath;
                if(string.IsNullOrEmpty(path))return false;
                SaveProject(path); status.text="プロジェクト保存　"+Path.GetFileName(path); return true;
            } catch(Exception ex) {ReportActionError("保存中止　",ex);return false;}
        }

        void BeforeReplace(Action proceed)
        {
            if(importing || loadingWave || exporting || importDialog!=null)return;
            StopPlayback();
            if(!ProjectChanged()){proceed();return;}
            ShowDialog("プロジェクトを保存しますか？",out var body);
            var row=Box(body,"project-confirm","row");
            Action(row,"保存",true,()=>{CloseDialog();if(SaveProjectCommand(false))proceed();});
            Action(row,"保存しない",true,()=>{CloseDialog();proceed();});
            Action(row,"キャンセル",true,CloseDialog);
        }

        bool QuitProject()
        {
            if(exporting){cancelExport=true;return false;}
            if(drainingExtensions)return shutdownTask?.IsCompleted==true;
            if(!allowQuit && !Environment.GetCommandLineArgs().Contains("-avaSmokeOutput") && ProjectChanged()){
                BeforeReplace(()=>{allowQuit=true;Application.Quit();});return false;
            }
            drainingExtensions=true;var tasks=new List<System.Threading.Tasks.Task>();
            if(operations!=null)tasks.Add(operations.CloseAsync());
            if(floatingTime!=null)tasks.Add(floatingTime.CloseAsync());if(floatingWave!=null)tasks.Add(floatingWave.CloseAsync());
            foreach(var ext in extensions){
                ext.active=false;ext.activation++;
                if(ext.plugin is Ava.Extensions.IAsyncShutdown closing)
                    try{tasks.Add(closing.CloseAsync());}catch(Exception ex){Debug.LogWarning(ex.Message);}
            }
            shutdownTask=System.Threading.Tasks.Task.WhenAll(tasks);
            if(shutdownTask.IsCompleted)return true;
            AcquireActivity();StartCoroutine(FinishShutdown());return false;
        }
        System.Collections.IEnumerator FinishShutdown()
        {
            while(!shutdownTask.IsCompleted){PumpExtensions();yield return null;}
            if(shutdownTask.IsFaulted)Debug.LogWarning(shutdownTask.Exception);
            DisposeExtensions();ReleaseActivity();Application.Quit();
        }
        void NewProjectCommand()=>BeforeReplace(NewProject);
        void OpenProjectCommand()=>BeforeReplace(()=> {
            try { string path=PackageFileDialog.Open("開く","avadance"); if(path!=null) OpenProject(path); }
            catch(Exception ex){ReportActionError("プロジェクト読込中止　",ex);}
        });

        void NewProject()
        {
            documentGeneration++; extraEntries=new Dictionary<string,byte[]>(); bookmarks.Clear();bookmarkMemos.Clear();sceneSidecar=new VmdSidecar();
            externalParents.Clear();ClearAllActors(); ChangeMode(false); RestoreAccessories(new Project());
            shaderLibrary.RestoreProjectPacks(Array.Empty<byte[]>());
            soundtrack.Stop(); var old=soundtrack.clip; soundtrack.clip=null; if(old!=null)Destroy(old);
            peaks=Array.Empty<Vector2>(); waveform.MarkDirtyRepaint();
            physicsEngine=PhysicsEngine.Bullet; physicsMode=PhysicsMode.OnOff; physicalFloor=groundShadow=actor.modelSelfShadow=true; forceOnPlayback=showBodies=false;
            groundBrightness=1;transparentGround=true;showSelfShadow=true;
            RestoreView(new Project());
            scrubSound=muteWave=false; soundVolume.value=1; gridToggle.value=true;
            playFrom.value="";playTo.value="";repeat.value=false;
            modelView=CameraPose.Initial;front=Vector3.forward;useCameraView=false;movie=new MovieSettings();
            foreach(var t in sceneTracks) {t.ClearSources();t.Layers.Clear();t.ActiveLayer=t.ActiveClip=null;foreach(int f in t.Frames.Skip(1).ToArray())t.Write(f,null);}
            cameraTrack.Write(0,CameraPose.Initial);lightTrack.Write(0,LightPose.Initial);
            shadowTrack.Write(0,ShadowPose.Initial);gravityTrack.Write(0,GravityPose.Initial);
            ResetHistory(); SelectTrack(cameraTrack); SetFrame(0); projectPath=null; NotifyProject(projectOpened,null); MarkSaved(); status.text="新規プロジェクト";
        }

        void ResetHistory()
        {
            undo.Clear();redo.Clear();keyClipboard.Clear();selectedKeys.Clear();rangeSelected=false;poseClipboard=null;
        }

        void SaveProject(string path)
        {
            var p=CaptureProject();
            string temp=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),Guid.NewGuid()+".tmp");
            try {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Create)) {
                    byte[] json=Encoding.UTF8.GetBytes(JsonUtility.ToJson(p));
                    if(json.Length>MaxProjectJson)throw new IOException("プロジェクト情報のサイズ上限を超えています。");
                    AddEntry(zip,"project.json",json);
                    foreach (var entry in p.extensionPayload) AddEntry(zip,entry.Key,entry.Value);
                    for(int n=0;n<p.accessories.Count;n++) {
                        var prop=p.accessories[n];AddEntry(zip,"prop/"+n+"/mesh",prop.mesh);
                        for(int m=0;m<prop.textures.Length;m++)AddEntry(zip,"prop/"+n+"/tex"+m,prop.images[prop.textures[m]]);
                    }
                    if(p.image!=null)AddEntry(zip,"background/image",p.image);
                    if(p.video!=null)AddEntry(zip,"background/video",p.video);
                    WriteActorAssets(zip,"",p);
                    for(int n=0;n<p.others.Count;n++)WriteActorAssets(zip,"models/"+n+"/",p.others[n]);
                    int i=0; foreach(var pack in shaderLibrary.ProjectPacks)AddEntry(zip,"shader/"+i++,pack);
                    if(soundtrack.clip!=null) {
                        using var output=zip.CreateEntry("audio.f32",CompressionLevel.Fastest).Open();
                        TransferSamples(soundtrack.clip,output,true);
                    }
                }
                PublishFile(temp,path);projectPath=path;MarkSaved(); NotifyProject(projectSaved,path);
            } finally {if(File.Exists(temp))File.Delete(temp);}
        }

        static void WriteActorAssets(ZipArchive zip,string prefix,ModelState model)
        {
            if(model.modelSettings?.toons!=null)for(int i=0;i<model.modelSettings.toons.Count;i++)
                AddEntry(zip,prefix+"toon/"+i,model.modelSettings.toons[i].data);
            foreach(var a in model.payload) {
                if(a.HasAsset)AddEntry(zip,prefix+"assets/"+a.Guid,a.Data);
                if(a.Meta!=null)AddEntry(zip,prefix+"meta/"+a.Guid,Encoding.UTF8.GetBytes(a.Meta));
            }
        }
        static void AddEntry(ZipArchive zip,string name,byte[] data)
        {
            using var entry=zip.CreateEntry(name,CompressionLevel.Fastest).Open();
            entry.Write(data,0,data.Length);
        }

        static byte[] ReadEntry(ZipArchive zip,string name,long limit)
        {
            var entry=zip.GetEntry(name)??throw new InvalidDataException("プロジェクト内に "+name+" がありません。");
            if(entry.Length<0 || entry.Length>limit)throw new InvalidDataException("プロジェクト内データが大きすぎます。");
            var bytes=new byte[(int)entry.Length];
            using var input=entry.Open(); ReadFully(input,bytes,bytes.Length);
            if(input.ReadByte()!=-1)throw new InvalidDataException("項目サイズが一致しません。");
            return bytes;
        }
        static void ReadFully(Stream input,byte[] buffer,int count)
        {
            for(int i=0;i<count;) {int n=input.Read(buffer,i,count-i);if(n==0)throw new EndOfStreamException();i+=n;}
        }
        static void TransferSamples(AudioClip clip,Stream stream,bool write)
        {
            int block=Math.Min(8192,clip.samples);
            float[] values=null;byte[] bytes=null;
            for(int at=0;at<clip.samples;at+=block) {
                int samples=Math.Min(block,clip.samples-at)*clip.channels;
                if(values==null || values.Length!=samples){values=new float[samples];bytes=new byte[samples*4];}
                if(write) {
                    if(!clip.GetData(values,at))throw new IOException("音声を保存できません。");
                    Buffer.BlockCopy(values,0,bytes,0,bytes.Length);stream.Write(bytes,0,bytes.Length);
                } else {
                    ReadFully(stream,bytes,bytes.Length); Buffer.BlockCopy(bytes,0,values,0,bytes.Length);
                    if(Array.Exists(values,v=>!float.IsFinite(v)) || !clip.SetData(values,at))throw new InvalidDataException("音声が不正です。");
                }
            }
        }

        static void ReadActorAssets(ZipArchive zip,string prefix,ModelState p)
        {
            if(p?.assets==null)throw new InvalidDataException("モデル情報が不正です。");
            if(p.modelSettings?.toons!=null) {
                if(p.modelSettings.toons.Count>1024)throw new InvalidDataException("トゥーン数が不正です。");
                for(int n=0;n<p.modelSettings.toons.Count;n++)p.modelSettings.toons[n].data=ReadEntry(zip,prefix+"toon/"+n,MaxMotionBytes);
            }
            var guids=new HashSet<string>();p.payload=new List<Asset>();
            foreach(var a in p.assets) {
                if(a==null || !Guid.TryParseExact(a.guid,"N",out _) || !guids.Add(a.guid) || string.IsNullOrEmpty(a.path))
                    throw new InvalidDataException("不正なアセットです。");
                string m=prefix+"meta/"+a.guid;
                var meta=zip.GetEntry(m)!=null?Encoding.UTF8.GetString(ReadEntry(zip,m,UnityPackageReader.MaxFileBytes)):null;
                if(a.metadataOnly && meta==null)throw new InvalidDataException("メタデータがありません。");
                p.payload.Add(new Asset(a.guid,a.path,a.metadataOnly?null:ReadEntry(zip,prefix+"assets/"+a.guid,UnityPackageReader.MaxFileBytes),meta));
            }
            if((p.payload.Count!=0 || !string.IsNullOrEmpty(p.modelGuid)) && !guids.Contains(p.modelGuid))throw new InvalidDataException("モデルがありません。");
        }

        void OpenProject(string path)
        {
            Project p; var assets=new List<Asset>(); var packs=new List<byte[]>(); AudioClip clip=null;
            try {
                using(var stream=File.OpenRead(path))
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)) {
                    if(zip.Entries.Count>UnityPackageReader.MaxMembers || zip.Entries.Sum(e=>e.Length)>2L*1024*1024*1024 ||
                        zip.Entries.Select(e=>e.FullName).Distinct().Count()!=zip.Entries.Count)throw new InvalidDataException("不正なプロジェクト構成です。");
                    p=JsonUtility.FromJson<Project>(Encoding.UTF8.GetString(ReadEntry(zip,"project.json",MaxProjectJson)));
                    if(p==null || !(p.format=="AvaAvaDance project 1" || p.format=="AvaAvaDance project 2" && p.schemaVersion==2) || p.assets==null || p.frame<0 || p.frame>MaxFrame || p.output==null || !p.output.Valid ||
                        p.engine<0 || p.engine>1 || p.physics<0 || p.physics>3 || p.shaderCount<0 || p.shaderCount>256 || !ValidPose(p.editorView) ||
                        !float.IsFinite(p.groundBrightness) || p.groundBrightness<0 || p.groundBrightness>2 || !float.IsFinite(p.volume) || p.volume<0 || p.volume>1)throw new InvalidDataException("プロジェクト設定が不正です。");
                    p.extensionPayload=zip.Entries.Where(e=>e.FullName.StartsWith("extensions/",StringComparison.Ordinal) || e.FullName.StartsWith("host/",StringComparison.Ordinal)).ToDictionary(e=>e.FullName,e=>ReadEntry(zip,e.FullName,UnityPackageReader.MaxFileBytes));
                    if (p.accessories != null) {
                        if(p.accessories.Count>255 || p.accessories.Any(a=>string.IsNullOrEmpty(a?.name) || a.textures==null || a.textures.Length>8192))throw new InvalidDataException("アクセサリ構成が不正です。");
                        for(int n=0;n<p.accessories.Count;n++) {
                            var prop=p.accessories[n];prop.mesh=ReadEntry(zip,"prop/"+n+"/mesh",MaxMotionBytes);
                            prop.images=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
                            for(int m=0;m<prop.textures.Length;m++)prop.images.Add(prop.textures[m],ReadEntry(zip,"prop/"+n+"/tex"+m,MaxMotionBytes));
                        }
                    }
                    if (!string.IsNullOrEmpty(p.display?.imageName)) p.image = ReadEntry(zip, "background/image", BackdropLimit);
                    if (!string.IsNullOrEmpty(p.display?.videoName)) p.video = ReadEntry(zip, "background/video", BackdropLimit);
                    ReadActorAssets(zip,"",p);assets=p.payload;
                    if(p.others!=null){
                        if(p.others.Count>254)throw new InvalidDataException("モデル数が多すぎます。");
                        for(int i=0;i<p.others.Count;i++)ReadActorAssets(zip,"models/"+i+"/",p.others[i]);
                    }
                    for(int i=0;i<p.shaderCount;i++)packs.Add(ReadEntry(zip,"shader/"+i,ShaderLibrary.MaxBytes));
                    if(p.samples!=0) {
                        long bytes=(long)p.samples*p.channels*4;
                        var entry=zip.GetEntry("audio.f32");
                        if(p.samples<1 || p.channels<1 || p.channels>8 || p.rate<1 || p.rate>384000 || bytes>512L*1024*1024 ||
                           entry==null || entry.Length!=bytes)throw new InvalidDataException("音声サイズが不正です。");
                        clip=AudioClip.Create(p.soundName??"WAV",p.samples,p.channels,p.rate,false);
                        using var audio=entry.Open(); TransferSamples(clip,audio,false);
                    }
                }
                var before=CaptureProject();var priorAssets=actor.modelAssets.ToList();var priorPacks=shaderLibrary.ProjectPacks.ToList();
                var oldClip=soundtrack.clip;
                var undoBefore=undo.ToArray();var redoBefore=redo.ToArray();var clipboardBefore=keyClipboard.ToArray();
                var poseBefore=poseClipboard; var selectedBefore=selectedKeys.ToArray(); bool previousRange=rangeSelected;
                var priorModelTracks=drawOrder.SelectMany(a=>a.modelTracks.Select(t=>(t,a.id))).ToDictionary(k=>k.t,k=>k.id);
                try { ApplyProject(p,assets,packs,clip); }
                catch {
                    ApplyProject(before,priorAssets,priorPacks,oldClip);
                    MotionTrack Map(MotionTrack track) => (priorModelTracks.TryGetValue(track,out string id)?drawOrder.First(a=>a.id==id).modelTracks:sceneTracks)
                        .First(t=>t.Name==track.Name && t.GetType()==track.GetType());
                    KeyChange Remap(KeyChange change) { if (change.Track!=null) change.Track=Map(change.Track); return change; }
                    foreach(var group in undoBefore.Reverse())undo.Push(group.Select(Remap).ToArray());
                    foreach(var group in redoBefore.Reverse())redo.Push(group.Select(Remap).ToArray());
                    keyClipboard.AddRange(clipboardBefore.Select(Remap));poseClipboard=poseBefore;
                    rangeSelected=previousRange; foreach(var key in selectedBefore)selectedKeys.Add((Map(key.track),key.frame));
                    RefreshKeyButtons(); throw;
                }
                if(oldClip!=null)Destroy(oldClip);clip=null;projectPath=path;NotifyProject(projectOpened,path);MarkSaved();
                status.text="プロジェクト読込　"+Path.GetFileName(path);
            } finally {if(clip!=null)Destroy(clip);}
        }

        void RestoreAccessories(Project p)
        {
            foreach(var prop in props.ToArray())RemoveProp(prop);
            screenMode=p.screenMode;
            if(p.accessories!=null)foreach(var prop in p.accessories) {
                if(sceneTracks.Any(t=>t.Name==prop.name))throw new InvalidDataException("重複したアクセサリ名です。");
                AddProp(prop);
            }
        }

        void RestoreView(Project p)
        {
            viewOptions = p.display ?? new ViewOptions();
            SetBackdrop(viewOptions.imageName,p.image,false); SetBackdrop(viewOptions.videoName,p.video,true);
            if(viewOptions.shadowTint.a<=0)viewOptions.shadowTint=Color.white;
            ForEachActor(drawOrder,ApplyModelDisplay); ResizePreview(); ApplyFiltering(); UpdateBackdrop(); UpdateViewInfo();
        }

        void RestoreActor(ModelState p,List<Asset> assets)
        {
            var model=assets.Single(a=>a.Guid==p.modelGuid);
            var prefab=IsVrm(model.Path)?null:string.IsNullOrEmpty(p.prefabGuid)?PackageAvatar.LegacyPrefab(assets,model):
                PackageAvatar.Choices(assets,new[]{model}).Single(a=>a.Prefab?.Guid==p.prefabGuid).Prefab;
            ImportModel(model,assets,prefab);
            if(!string.IsNullOrEmpty(p.id))actor.id=p.id;
            if(!string.IsNullOrEmpty(p.label))actor.label=p.label;
            foreach(string name in p.customIK??Array.Empty<string>())AddSelectedIK(actor.bones.Find(b=>b.Track.Name==name));
            foreach(string name in p.dynamicOn??Array.Empty<string>()){
                var b=actor.bones.Find(x=>x.Track.Name==name);if(b!=null && !actor.dynamicLinks.Any(d=>d.Bone==b))ToggleDynamicBone(b);
            }
            foreach(string name in p.dynamicOff??Array.Empty<string>()){
                var b=actor.bones.Find(x=>x.Track.Name==name);if(b!=null && actor.dynamicLinks.Any(d=>d.Bone==b))ToggleDynamicBone(b);
            }
            actor.modelOverrides=p.modelSettings??new ModelOverrides();
            actor.modelOverrides.toons??=new List<ToonSetting>();actor.modelOverrides.names??=new List<ModelName>();
            actor.modelOverrides.physics??=new List<DynamicSetting>();
            actor.modelOverrides.bullet??=new List<BulletSetting>();
            if(actor.modelOverrides.bullet.Any(s=>s==null || !s.Valid) ||
                actor.modelOverrides.bullet.Select(s=>s.name).Distinct().Count()!=actor.modelOverrides.bullet.Count)
                throw new InvalidDataException("Bullet剛体設定が不正です。");
            foreach(var setting in actor.modelOverrides.physics)ApplyDynamicSetting(setting);
            RestoreMotion(p.modelMotion,true,legacy:string.IsNullOrEmpty(p.id));actor.avatar.SetActive(p.visible);actor.modelSelfShadow=p.selfShadow;
            ApplyModelDisplay();
        }

        static void RestoreOrder(List<Actor> list,string[] ids)
        {
            if(ids==null || ids.Length==0)return;
            if(ids.Length!=list.Count || ids.Distinct().Count()!=ids.Length || list.Any(a=>!ids.Contains(a.id)))
                throw new InvalidDataException("モデルの順番が不正です。");
            list.Sort((a,b)=>Array.IndexOf(ids,a.id).CompareTo(Array.IndexOf(ids,b.id)));
        }

        void ApplyProject(Project p,List<Asset> assets,List<byte[]> packs,AudioClip clip)
        {
            documentGeneration++; extraEntries=p.extensionPayload ?? new Dictionary<string,byte[]>();
            bookmarks.Clear(); if(p.bookmarks!=null) bookmarks.UnionWith(p.bookmarks.Where(f=>f>=0 && f<=MaxFrame));
            bookmarkMemos.Clear();foreach(var note in p.bookmarkNotes??Array.Empty<BookmarkNote>())if(note!=null && bookmarks.Contains(note.frame) && !string.IsNullOrEmpty(note.memo))bookmarkMemos[note.frame]=note.memo;
            StopPlayback();externalParents.Clear(); ClearAllActors();
            shaderLibrary.RestoreProjectPacks(packs);
            if(p.others!=null)foreach(var extra in p.others)RestoreActor(extra,extra.payload);
            if(!string.IsNullOrEmpty(p.modelGuid))RestoreActor(p,assets);else front=Vector3.forward;
            RestoreOrder(drawOrder,p.renderOrder); RestoreOrder(calculationOrder,p.simulationOrder); ApplyDrawOrder();
            RefreshActorUI();
            RestoreAccessories(p); RestoreMotion(p.sceneMotion,false); RestoreView(p);
            physicsEngine=(PhysicsEngine)p.engine;physicsMode=(PhysicsMode)p.physics;physicalFloor=p.floor;showBodies=p.showBodies;forceOnPlayback=p.forcePhysics;
            groundBrightness=p.groundBrightness;transparentGround=p.transparentGround;showSelfShadow=p.showSelfShadow;
            groundShadow=p.groundShadow;actor.modelSelfShadow=p.selfShadow;modelView=p.editorView;useCameraView=p.fromCamera;
            soundtrack.Stop(); soundtrack.clip=clip; soundVolume.value=p.volume; muteWave=p.mute;scrubSound=p.scrub;
            peaks=clip!=null?ReadWaveform(clip):Array.Empty<Vector2>();waveform.MarkDirtyRepaint();
            movie=p.output;playFrom.value=p.start??"";playTo.value=p.end??"";repeat.value=p.repeat;gridToggle.value=p.grid;
            ChangeMode(p.modelMode);SelectTrack(VisibleTracks.Find(t=>t.Name==p.selection)??VisibleTracks.FirstOrDefault());
            BindLinks(p.constraints??Array.Empty<BoneLink>());ResetHistory();InvalidateDynamics();SetFrame(p.frame);
            RestoreMotion(p.sceneDraft,false,true);
            var current=actor;
            try {
                if(p.others!=null)foreach(var m in p.others){actor=drawOrder.First(a=>a.id==m.id);RestoreMotion(m.modelDraft,true,true);RefreshRigPreview();}
            } finally {actor=current;}
            if(actor.avatar!=null)RestoreMotion(p.modelDraft,true,true,legacy:string.IsNullOrEmpty(p.id));
            if(p.selected?.active!=null)
            {
                var tracks=VisibleTracks.ToDictionary(t=>t.Id);
                if(tracks.TryGetValue(p.selected.active,out var active))SelectTrack(active);
                var lastBone=actor.bones.Find(b=>b.Track.Id==p.selected.bone);
                if(lastBone!=null)actor.selectedBone=lastBone;
                boneSelection.Clear();
                foreach(var id in p.selected.bones??Array.Empty<string>()){var b=actor.bones.Find(j=>j.Track.Id==id);if(b!=null && !boneSelection.Contains(b))boneSelection.Add(b);}
                selectedRows.Clear();foreach(var id in p.selected.rows??Array.Empty<string>())if(tracks.TryGetValue(id,out var row))selectedRows.Add(row);
                rangeSelected=p.selected.range;
                foreach(var key in p.selected.keys??Array.Empty<SelectedKeyState>())if(key?.track!=null && tracks.TryGetValue(key.track,out var track) && track.EditKey(key.frame)!=null)selectedKeys.Add((track,key.frame));
            }
            RefreshRigPreview(); RestoreSolvedPose(); ApplyView(); SyncControls();RefreshKeyButtons();RefreshTimelineLabels();RedrawOverlay();
        }
    }
}
