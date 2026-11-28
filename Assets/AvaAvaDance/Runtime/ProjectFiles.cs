using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvaAvaDance.Import;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UniGLTF;
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
            public Vector3 placementPosition, placementAngles;
            public float placementScale;
            public List<AssetInfo> assets;
            public SavedMotion modelMotion, modelDraft;
            public bool visible, selfShadow;
            public string[] customIK, dynamicOff;
            public SpringBoneLimit[] springBoneLimits;
            public ModelOverrides modelSettings;
            [NonSerialized] public List<Asset> payload;

            public ModelState WithoutMotionResource()
            {
                var copy = (ModelState)MemberwiseClone();
                copy.modelMotion = null;
                return copy;
            }
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
            public string start, end, selection, soundName;
            public List<ModelState> others;
            public int extensionStorageVersion=1;
            public int audioStartFrame;
            public int[] bookmarks;
            public string[] additionalLights;
            public BookmarkNote[] bookmarkNotes;
            public SessionSelection selected;
            [NonSerialized] public Dictionary<string,byte[]> extensionPayload;
            public string[] renderOrder, simulationOrder;
            public int shaderCount, frame, samples, rate, channels, physics;
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
        const string ProjectExtension = "avaproject";

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

        async Task CaptureActorAsync(Actor owner, ModelState state, bool contentOnly, IAwaitCaller caller)
        {
            state.placementPosition=owner.placementPosition;state.placementAngles=owner.placementAngles;
            state.placementScale = owner.placementScale;
            state.id=owner.avatar==null?null:owner.id;state.label=owner.label;state.modelGuid=owner.modelGuid;state.prefabGuid=owner.prefabGuid;
            state.modelSettings=owner.modelOverrides;state.visible=!contentOnly && owner.avatar!=null && owner.avatar.activeSelf;
            state.selfShadow=owner.modelSelfShadow;state.customIK=owner.customIK.ToArray();
            state.dynamicOff=owner.excludedDynamic.ToArray();
            state.springBoneLimits=owner.springBoneLimits;
            state.assets=owner.modelAssets.Select(a=>new AssetInfo{guid=a.Guid,path=a.Path,metadataOnly=!a.HasAsset}).ToList();
            state.payload=owner.modelAssets.ToList();
            if (owner.avatar != null) {
                state.modelMotion = await CaptureMotionAsync(owner, false, contentOnly, caller);
                state.modelDraft = await CaptureMotionAsync(owner, true, contentOnly, caller);
            }
        }

        Project CaptureProject(bool contentOnly = false) =>
            CaptureProjectAsync(contentOnly, new ImmediateCaller()).GetAwaiter().GetResult();

        async Task<Project> CaptureProjectAsync(bool contentOnly, IAwaitCaller caller)
        {
            var clip=soundtrack.clip;
            var selectedState=contentOnly?null:new SessionSelection {
                active=selectedTrack?.Id,bone=actor.selectedBone?.Track.Id,range=rangeSelected,
                rows=selectedRows.Select(t=>t.Id).ToArray(),bones=boneSelection.Select(b=>b.Track.Id).ToArray(),
                keys=selectedKeys.Select(k=>new SelectedKeyState{track=k.track.Id,frame=k.frame}).ToArray()
            };
            var p=new Project {
                bookmarkNotes=bookmarkMemos.OrderBy(p=>p.Key).Select(p=>new BookmarkNote{frame=p.Key,memo=p.Value}).ToArray(),bookmarks=bookmarks.ToArray(),selected=selectedState, extensionPayload=new Dictionary<string,byte[]>(extraEntries),
                additionalLights=CaptureAdditionalLights(),accessories=props.Select(a=>a.Data).ToList(),constraints=externalParents.ToArray(),screenMode=screenMode,
                display=JsonUtility.FromJson<ViewOptions>(JsonUtility.ToJson(viewOptions)), image=imageBytes, video=videoBytes,
                shaderCount=shaderLibrary.ProjectPacks.Count(), frame=frame, selection=selectedTrack?.Name,
                modelMode=modelMode,editorView=modelView,fromCamera=useCameraView,
                repeat=transportRepeat,start=transportStart,end=transportEnd,grid=gridToggle.value,
                samples=clip!=null?clip.samples:0,rate=clip!=null?clip.frequency:0,channels=clip!=null?clip.channels:0,soundName=clip!=null?clip.name:null,
                audioStartFrame=audioStartFrame,volume=soundtrack.volume,mute=muteWave,scrub=scrubSound,groundShadow=groundShadow,
                groundBrightness=groundBrightness,transparentGround=transparentGround,showSelfShadow=showSelfShadow,
                physics=(int)physicsMode,floor=physicalFloor,forcePhysics=forceOnPlayback,showBodies=showBodies,
                output=movie, renderOrder=drawOrder.Select(a=>a.id).ToArray(),simulationOrder=calculationOrder.Select(a=>a.id).ToArray()
            };
            p.sceneMotion = await CaptureMotionAsync(null, false, contentOnly, caller);
            p.sceneDraft = await CaptureMotionAsync(null, true, contentOnly, caller);
            var root = contentOnly && drawOrder.Count > 0 ? drawOrder[0] : actor;
            await CaptureActorAsync(root, p, contentOnly, caller);
            p.others = new List<ModelState>();
            foreach (var item in drawOrder) {
                if (item == root) continue;
                var model = new ModelState();
                await CaptureActorAsync(item, model, contentOnly, caller);
                p.others.Add(model);
            }
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

        void MarkSaved() => MarkSavedAsync(new ImmediateCaller()).GetAwaiter().GetResult();

        async Task MarkSavedAsync(IAwaitCaller caller, Dictionary<string, byte[]> writtenEntries = null)
        {
            var content = await CaptureProjectAsync(true, caller);
            savedState = await caller.Run(() => JsonUtility.ToJson(content));
            savedEntries=writtenEntries ?? content.extensionPayload; savedImage=imageBytes; savedVideo=videoBytes;
            savedModels=CurrentModels().ToArray(); savedWave=CurrentWave;
        }

        bool ProjectChanged() => ProjectChangedAsync(new ImmediateCaller()).GetAwaiter().GetResult();

        async Task<bool> ProjectChangedAsync(IAwaitCaller caller)
        {
            if (savedEntries == null || savedEntries.Count != extraEntries.Count || savedImage != imageBytes ||
                savedVideo != videoBytes || !CurrentModels().SequenceEqual(savedModels) || savedWave != CurrentWave) return true;
            foreach (var entry in extraEntries.ToArray()) {
                if (!savedEntries.TryGetValue(entry.Key, out var prior)) return true;
                if (prior != entry.Value && !await caller.Run(() => prior.SequenceEqual(entry.Value))) return true;
            }
            var content = await CaptureProjectAsync(true, caller);
            return await caller.Run(() => savedState != JsonUtility.ToJson(content));
        }

        IDisposable BeginProjectOperation()
        {
            if (importing) throw new InvalidOperationException("他の読込・保存処理が進行中です。");
            var workspace = GetComponent<UIDocument>().rootVisualElement.Q("workspace");
            bool wasEnabled = workspace.enabledSelf;
            importing = true;
            workspace.SetEnabled(false);
            AcquireActivity();
            return new Registration(() => {
                importing = false;
                if (this != null) workspace.SetEnabled(wasEnabled);
                ReleaseActivity();
            });
        }

        async Task<bool> SaveProjectCommand(bool saveAs)
        {
            if (importing || loadingWave || exporting) return false;
            StopPlayback();
            try {
                string path=saveAs || projectPath==null?PackageFileDialog.Save("名前を付けて保存",ProjectExtension):projectPath;
                if(string.IsNullOrEmpty(path))return false;
                status.text="プロジェクト保存中…";
                await SaveProjectAsync(path, new RuntimeOnlyAwaitCaller());
                status.text="プロジェクト保存　"+Path.GetFileName(path); return true;
            } catch(Exception ex) {if(this!=null)ReportActionError("保存中止　",ex);return false;}
        }

        async Task BeforeReplace(Func<Task> proceed)
        {
            if(importing || loadingWave || exporting || importDialog!=null)return;
            StopPlayback();
            bool changed;
            try {
                using (BeginProjectOperation()) changed = await ProjectChangedAsync(new RuntimeOnlyAwaitCaller());
            } catch (Exception ex) { if (this != null) ReportActionError("プロジェクトの確認中止　", ex); return; }
            if (!changed) { await proceed(); return; }
            ShowDialog("プロジェクトを保存しますか？",out var body);
            var row=Box(body,"project-confirm","row");
            async Task SaveThenProceed()
            {
                CloseDialog();
                if (await SaveProjectCommand(false)) await proceed();
            }
            Action(row,"保存",true,()=>RunProjectAction(SaveThenProceed()));
            Action(row,"保存しない",true,()=>{CloseDialog();RunProjectAction(proceed());});
            Action(row,"キャンセル",true,CloseDialog);
        }

        bool QuitProject()
        {
            if(exporting){cancelExport=true;return false;}
            if(importing)return false;
            if(drainingExtensions)return shutdownTask?.IsCompleted==true;
            if (!allowQuit && DeferMacQuitUntilEditsArrive()) return false;
            if(!allowQuit && !Environment.GetCommandLineArgs().Contains("-avaSmokeOutput")){
                RunProjectAction(BeforeReplace(()=>{allowQuit=true;Application.Quit();return Task.CompletedTask;}));return false;
            }
            drainingExtensions=true;var tasks=new List<System.Threading.Tasks.Task>();
            operations?.Dispose();floatingTime?.Dispose();floatingWave?.Dispose();
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
        void NewProjectCommand()=>RunProjectAction(BeforeReplace(()=>{NewProject();return Task.CompletedTask;}));
        void OpenProjectCommand()=>RunProjectAction(BeforeReplace(async ()=> {
            try { string path=PackageFileDialog.Open("開く",ProjectExtension); if(path!=null) await OpenProjectAsync(path); }
            catch(Exception ex){if(this!=null)ReportActionError("プロジェクト読込中止　",ex);}
        }));

        void NewProject()
        {
            documentGeneration++; extraEntries=new Dictionary<string,byte[]>(); bookmarks.Clear();bookmarkMemos.Clear();
            externalParents.Clear();ClearAllActors(); ChangeMode(false); RestoreAccessories(new Project());
            shaderLibrary.RestoreProjectPacks(Array.Empty<byte[]>());
            audioStartFrame=0;
            soundtrack.Stop(); var old=soundtrack.clip; soundtrack.clip=null; if(old!=null)Destroy(old);
            peaks=Array.Empty<Vector2>(); waveform.MarkDirtyRepaint();
            physicsMode=PhysicsMode.OnOff; physicalFloor=groundShadow=actor.modelSelfShadow=true; forceOnPlayback=showBodies=false;
            groundBrightness=1;transparentGround=true;showSelfShadow=true;
            RestoreView(new Project());
            scrubSound=muteWave=false; soundtrack.volume=1;soundVolume.SetValueWithoutNotify(1);gridToggle.value=true;
            RestoreTransportSettings("", "", false);
            modelView=CameraPose.Initial;front=Vector3.forward;useCameraView=false;movie=new MovieSettings();
            RestoreAdditionalLights(null);
            foreach(var t in sceneTracks) {t.Layers.Clear();t.ActiveLayer=t.ActiveClip=null;foreach(int f in t.Frames.Skip(1).ToArray())t.Write(f,null);}
            cameraTrack.Write(0,CameraPose.Initial);lightTrack.Write(0,LightPose.Initial);
            shadowTrack.Write(0,ShadowPose.Initial);gravityTrack.Write(0,GravityPose.Initial);
            ResetHistory();ReleaseInactiveSceneLights(); SelectTrack(cameraTrack); SetFrame(0); projectPath=null; NotifyProject(projectOpened,null); MarkSaved(); status.text="新規プロジェクト";
        }

        void ResetHistory()
        {
            undo.Clear();redo.Clear();keyClipboard.Clear();selectedKeys.Clear();rangeSelected=false;poseClipboard=null;
        }

        // Synchronous smoke scenarios use the same writer with an immediate scheduler.
        void SaveProject(string path) => SaveProjectAsync(path, new ImmediateCaller()).GetAwaiter().GetResult();

        async Task SaveProjectAsync(string path, IAwaitCaller caller)
        {
            using var operation = BeginProjectOperation();
            var cancellation = destroyCancellationToken;
            await caller.NextFrame();
            cancellation.ThrowIfCancellationRequested();
            var project = await CaptureProjectAsync(false, caller);
            await WriteProjectFileAsync(path, project, caller, cancellation);
            projectPath = path;
            await MarkSavedAsync(caller, project.extensionPayload);
            NotifyProject(projectSaved, path);
        }

        void WriteProjectFile(string path, Project p) =>
            WriteProjectFileAsync(path, p, new ImmediateCaller(), destroyCancellationToken).GetAwaiter().GetResult();

        async Task WriteProjectFileAsync(string path, Project p, IAwaitCaller caller, CancellationToken cancellation)
        {
            if (!string.Equals(Path.GetExtension(path), "." + ProjectExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("プロジェクトの保存には .avaproject を指定してください。");
            var resources = new ProjectResourceWriter();
            resources.Add("scene/motion", await EncodeProjectMotion(p.sceneMotion, "Scene", null, caller, cancellation));
            foreach (var entry in p.extensionPayload) resources.Add(entry.Key, entry.Value);
            for (int n = 0; n < p.accessories.Count; n++)
            {
                var prop = p.accessories[n];
                resources.Add("prop/" + n + "/mesh", prop.mesh);
                for (int m = 0; m < prop.textures.Length; m++)
                    resources.Add("prop/" + n + "/tex" + m, prop.images[prop.textures[m]]);
            }
            if (p.image != null) resources.Add("background/image", p.image);
            if (p.video != null) resources.Add("background/video", p.video);

            async Task AddActor(string prefix, ModelState model)
            {
                if (!string.IsNullOrEmpty(model.modelGuid)) {
                    var owner = drawOrder.First(item => item.id == model.id);
                    resources.Add(prefix + "motion", await EncodeProjectMotion(model.modelMotion, model.label, owner, caller, cancellation));
                }
                WriteActorResources(resources, prefix, model);
            }

            await AddActor("", p);
            for (int i = 0; i < p.others.Count; i++) await AddActor("models/" + i + "/", p.others[i]);
            int index = 0;
            foreach (var pack in shaderLibrary.ProjectPacks) resources.Add("shader/" + index++, pack);
            var clip = soundtrack.clip;
            long audioBytes = (long)p.samples * p.channels * sizeof(float);
            if (audioBytes > 512L * 1024 * 1024) throw new IOException("音声サイズが大きすぎます。");

            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Guid.NewGuid() + ".tmp");
            try
            {
                using (var stream = await caller.Run(() => new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                {
                    await caller.Run(() => {
                        cancellation.ThrowIfCancellationRequested();
                        var metadata = (Project)p.WithoutMotionResource();
                        metadata.sceneMotion = null;
                        metadata.others = p.others.Select(m => m.WithoutMotionResource()).ToList();
                        var state = AvaGlb.ParseJson(JsonUtility.ToJson(metadata));
                        state.Remove("sceneMotion"); state.Remove("modelMotion");
                        foreach (JObject other in (JArray)state["others"]) other.Remove("modelMotion");
                        resources.WritePrefix(stream, state, MaxProjectJson, (int)audioBytes, cancellation);
                    });
                    if (clip != null) await TransferProjectAudio(clip, stream, true, null, caller, cancellation);
                    await caller.Run(stream.Flush);
                }
                await caller.Run(() => {
                    cancellation.ThrowIfCancellationRequested();
                    PublishFile(temp, path);
                });
            }
            catch { await caller.Run(() => File.Delete(temp)); throw; }
        }

        static void WriteActorResources(ProjectResourceWriter resources, string prefix, ModelState model)
        {
            if(model.modelSettings?.toons!=null)for(int i=0;i<model.modelSettings.toons.Count;i++)
                resources.Add(prefix+"toon/"+i,model.modelSettings.toons[i].data);
            foreach(var a in model.payload) {
                if(a.HasAsset)resources.Add(prefix+"assets/"+a.Guid,a.Data);
                if(a.Meta!=null)resources.Add(prefix+"meta/"+a.Guid,Encoding.UTF8.GetBytes(a.Meta));
            }
        }
        static void ReadFully(Stream input,byte[] buffer,int count)
        {
            for(int i=0;i<count;) {int n=input.Read(buffer,i,count-i);if(n==0)throw new EndOfStreamException();i+=n;}
        }
        // Stream PCM in bounded blocks. Only GetData/SetData need the Unity thread.
        static async Task TransferProjectAudio(AudioClip clip, Stream stream, bool writing, Vector2[] amplitudes,
            IAwaitCaller caller, CancellationToken cancellation)
        {
            int channels = clip.channels, samples = clip.samples, rate = clip.frequency;
            int block = Math.Min(samples, 1048576 / channels);
            float[] values = new float[block * channels];
            byte[] bytes = new byte[values.Length * sizeof(float)];
            for (int at = 0; at < samples; at += block)
            {
                cancellation.ThrowIfCancellationRequested();
                int first = at, frames = Math.Min(block, samples - at), count = frames * channels;
                if (values.Length != count) values = new float[count];
                if (writing && !clip.GetData(values, at)) throw new IOException("音声を保存できません。");
                await caller.Run(() => {
                    int length = count * sizeof(float);
                    if (writing) {
                        Buffer.BlockCopy(values, 0, bytes, 0, length);
                        stream.Write(bytes, 0, length);
                    } else {
                        ReadFully(stream, bytes, length);
                        Buffer.BlockCopy(bytes, 0, values, 0, length);
                        for (int i = 0; i < frames; i++) {
                            int f = (int)((long)(first + i) * 30 / rate);
                            for (int ch = 0; ch < channels; ch++) {
                                float value = values[i * channels + ch];
                                if (!float.IsFinite(value)) throw new InvalidDataException("音声が不正です。");
                                if (f < amplitudes.Length) {
                                    amplitudes[f].x = Math.Min(amplitudes[f].x, value);
                                    amplitudes[f].y = Math.Max(amplitudes[f].y, value);
                                }
                            }
                        }
                    }
                });
                cancellation.ThrowIfCancellationRequested();
                if (!writing && !clip.SetData(values, at)) throw new InvalidDataException("音声が不正です。");
            }
        }

        static SavedMotion ReadProjectMotion(ProjectResourceReader resources, string name)
        {
            using var motion = new MemoryStream(resources.Read(name, MaxAvaMotionBytes), writable: false);
            return DecodeMotion(motion);
        }

        static void ReadActorAssets(ProjectResourceReader resources,string prefix,ModelState p, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(p?.assets==null)throw new InvalidDataException("モデル情報が不正です。");
            if(p.modelSettings?.toons!=null) {
                if(p.modelSettings.toons.Count>1024)throw new InvalidDataException("トゥーン数が不正です。");
                for(int n=0;n<p.modelSettings.toons.Count;n++)p.modelSettings.toons[n].data=resources.Read(prefix+"toon/"+n,MaxMotionBytes);
            }
            var guids=new HashSet<string>();p.payload=new List<Asset>();
            foreach(var a in p.assets) {
                cancellation.ThrowIfCancellationRequested();
                if(a==null || !Guid.TryParseExact(a.guid,"N",out _) || !guids.Add(a.guid) || string.IsNullOrEmpty(a.path))
                    throw new InvalidDataException("不正なアセットです。");
                string m=prefix+"meta/"+a.guid;
                var meta=resources.Contains(m)?Encoding.UTF8.GetString(resources.Read(m,UnityPackageReader.MaxFileBytes)):null;
                if(a.metadataOnly && meta==null)throw new InvalidDataException("メタデータがありません。");
                p.payload.Add(new Asset(a.guid,a.path,a.metadataOnly?null:resources.Read(prefix+"assets/"+a.guid,UnityPackageReader.MaxFileBytes),meta));
            }
            if((p.payload.Count!=0 || !string.IsNullOrEmpty(p.modelGuid)) && !guids.Contains(p.modelGuid))throw new InvalidDataException("モデルがありません。");
            if (!string.IsNullOrEmpty(p.modelGuid)) p.modelMotion = ReadProjectMotion(resources, prefix + "motion");
        }

        static Project ReadArchive(Stream stream, List<byte[]> packs, CancellationToken cancellation)
        {
            var resources = new ProjectResourceReader(stream, MaxProjectJson);
            var p=JsonUtility.FromJson<Project>(resources.State.ToString(Formatting.None));
            if(p==null || p.assets==null || p.frame<0 || p.frame>MaxFrame || p.output==null || !p.output.Valid ||
                p.physics<0 || p.physics>3 || p.shaderCount<0 || p.shaderCount>256 || !ValidPose(p.editorView) ||
                !float.IsFinite(p.groundBrightness) || p.groundBrightness<0 || p.groundBrightness>2 || !float.IsFinite(p.volume) || p.volume<0 || p.volume>1)throw new InvalidDataException("プロジェクト設定が不正です。");
            p.sceneMotion = ReadProjectMotion(resources, "scene/motion");
            p.extensionPayload=resources.Names.Where(e=>e.StartsWith("extensions/",StringComparison.Ordinal) || e.StartsWith("host/",StringComparison.Ordinal)).ToDictionary(e=>e,e=>resources.Read(e,UnityPackageReader.MaxFileBytes));
            if (p.accessories != null) {
                if(p.accessories.Count>255 || p.accessories.Any(a=>string.IsNullOrEmpty(a?.name) || a.textures==null || a.textures.Length>8192))throw new InvalidDataException("アクセサリ構成が不正です。");
                for(int n=0;n<p.accessories.Count;n++) {
                    var prop=p.accessories[n];prop.mesh=resources.Read("prop/"+n+"/mesh",MaxMotionBytes);
                    prop.images=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
                    for(int m=0;m<prop.textures.Length;m++)prop.images.Add(prop.textures[m],resources.Read("prop/"+n+"/tex"+m,MaxMotionBytes));
                }
            }
            if (!string.IsNullOrEmpty(p.display?.imageName)) p.image = resources.Read("background/image", BackdropLimit);
            if (!string.IsNullOrEmpty(p.display?.videoName)) p.video = resources.Read("background/video", BackdropLimit);
            if (p.others?.Count > 254) throw new InvalidDataException("モデル数が多すぎます。");
            ReadActorAssets(resources, "", p, cancellation);
            if (p.others != null)
                for (int i = 0; i < p.others.Count; i++)
                    ReadActorAssets(resources, "models/" + i + "/", p.others[i], cancellation);
            cancellation.ThrowIfCancellationRequested();
            for(int i=0;i<p.shaderCount;i++)packs.Add(resources.Read("shader/"+i,ShaderLibrary.MaxBytes));
            if(p.samples!=0) {
                long bytes=(long)p.samples*p.channels*4;
                if(p.samples<1 || p.channels<1 || p.channels>8 || p.rate<1 || p.rate>384000 || bytes>512L*1024*1024 ||
                   resources.Seek("audio.f32",512L*1024*1024)!=bytes)throw new InvalidDataException("音声サイズが不正です。");
            }
            return p;
        }

        async Task OpenProjectAsync(string path, IAwaitCaller caller = null)
        {
            using var operation = BeginProjectOperation();
            var cancellation = destroyCancellationToken;
            caller ??= new RuntimeOnlyAwaitCaller();
            Project p; var packs = new List<byte[]>(); AudioClip clip = null;
            Vector2[] amplitudes = Array.Empty<Vector2>();
            try {
                status.text = "プロジェクト読込中…";
                using(var stream=await caller.Run(() => File.OpenRead(path))) {
                    p = await caller.Run(() => ReadArchive(stream, packs, cancellation));
                    cancellation.ThrowIfCancellationRequested();
                    if (p.samples != 0) {
                        clip = AudioClip.Create(p.soundName ?? "WAV", p.samples, p.channels, p.rate, false);
                        amplitudes = new Vector2[Math.Min(MaxFrame + 1, (int)Math.Ceiling(p.samples * 30.0 / p.rate))];
                        await TransferProjectAudio(clip, stream, false, amplitudes, caller, cancellation);
                    }
                }
                var before=await CaptureProjectAsync(false, caller);var priorAssets=actor.modelAssets.ToList();var priorPacks=shaderLibrary.ProjectPacks.ToList();
                var oldClip=soundtrack.clip; var oldPeaks=peaks;
                var undoBefore=undo.ToArray();var redoBefore=redo.ToArray();var clipboardBefore=keyClipboard.ToArray();
                var poseBefore=poseClipboard; var selectedBefore=selectedKeys.ToArray(); bool previousRange=rangeSelected;
                var priorModelTracks=drawOrder.SelectMany(a=>a.modelTracks.Select(t=>(t,a.id))).ToDictionary(k=>k.t,k=>k.id);
                try { await ApplyProjectAsync(p,p.payload,packs,clip,amplitudes,caller); }
                catch when (!cancellation.IsCancellationRequested) {
                    await ApplyProjectAsync(before,priorAssets,priorPacks,oldClip,oldPeaks,caller);
                    MotionTrack Map(MotionTrack track) => (priorModelTracks.TryGetValue(track,out string id)?drawOrder.First(a=>a.id==id).modelTracks:sceneTracks.Concat(retainedLights.Select(l=>l.Track)))
                        .First(t=>t.Name==track.Name && t.GetType()==track.GetType());
                    KeyChange Remap(KeyChange change) { if (change.Track!=null) change.Track=Map(change.Track); return change; }
                    foreach(var group in undoBefore.Reverse())undo.Push(group.Select(Remap).ToArray());
                    foreach(var group in redoBefore.Reverse())redo.Push(group.Select(Remap).ToArray());
                    keyClipboard.AddRange(clipboardBefore.Select(Remap));poseClipboard=poseBefore;
                    rangeSelected=previousRange; foreach(var key in selectedBefore)selectedKeys.Add((Map(key.track),key.frame));
                    RefreshKeyButtons(); throw;
                }
                ReleaseInactiveSceneLights();
                if(oldClip!=null)Destroy(oldClip);clip=null;projectPath=path;NotifyProject(projectOpened,path);await MarkSavedAsync(caller);
                status.text="プロジェクト読込　"+Path.GetFileName(path);
            } finally {
                if(clip!=null)Destroy(clip);
            }
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

        async Task RestoreActorAsync(ModelState p,List<Asset> assets, IAwaitCaller caller)
        {
            var model=assets.Single(a=>a.Guid==p.modelGuid);
            var prefab=IsVrm(model.Path)||IsController(model.Path)?null:string.IsNullOrEmpty(p.prefabGuid)?PackageAvatar.LegacyPrefab(assets,model):
                PackageAvatar.Choices(assets,new[]{model}).Single(a=>a.Prefab?.Guid==p.prefabGuid).Prefab;
            await ImportModelAsync(model,assets,prefab,caller);
            RestoreSpringBoneLimits(actor,p.springBoneLimits);
            if(!string.IsNullOrEmpty(p.id))actor.id=p.id;
            if(!string.IsNullOrEmpty(p.label))actor.label=p.label;
            foreach(string name in p.customIK??Array.Empty<string>())AddSelectedIK(actor.bones.Find(b=>b.Track.Name==name));
            foreach(string name in p.dynamicOff??Array.Empty<string>()){
                var b=actor.bones.Find(x=>x.Track.Name==name);if(b!=null && actor.dynamicLinks.Any(d=>d.Bone==b))ToggleDynamicBone(b);
            }
            actor.modelOverrides=p.modelSettings??new ModelOverrides();
            actor.modelOverrides.toons??=new List<ToonSetting>();actor.modelOverrides.names??=new List<ModelName>();
            actor.modelOverrides.physics??=new List<DynamicSetting>();
            foreach(var setting in actor.modelOverrides.physics)ApplyDynamicSetting(setting);
            RestoreModelPlacement(actor,p.placementPosition,p.placementAngles,p.placementScale == 0 ? 1 : p.placementScale);
            await RestoreMotionAsync(p.modelMotion,true,caller);actor.avatar.SetActive(p.visible);actor.modelSelfShadow=p.selfShadow;
            ApplyModelDisplay();
        }

        static void RestoreOrder(List<Actor> list,string[] ids)
        {
            if(ids==null || ids.Length==0)return;
            if(ids.Length!=list.Count || ids.Distinct().Count()!=ids.Length || list.Any(a=>!ids.Contains(a.id)))
                throw new InvalidDataException("モデルの順番が不正です。");
            list.Sort((a,b)=>Array.IndexOf(ids,a.id).CompareTo(Array.IndexOf(ids,b.id)));
        }

        async Task ApplyProjectAsync(Project p,List<Asset> assets,List<byte[]> packs,AudioClip clip, Vector2[] amplitudes, IAwaitCaller caller)
        {
            documentGeneration++; extraEntries=p.extensionPayload ?? new Dictionary<string,byte[]>();
            bookmarks.Clear(); if(p.bookmarks!=null) bookmarks.UnionWith(p.bookmarks.Where(f=>f>=0 && f<=MaxFrame));
            bookmarkMemos.Clear();foreach(var note in p.bookmarkNotes??Array.Empty<BookmarkNote>())if(note!=null && bookmarks.Contains(note.frame) && !string.IsNullOrEmpty(note.memo))bookmarkMemos[note.frame]=note.memo;
            StopPlayback();externalParents.Clear(); ClearAllActors();
            await shaderLibrary.RestoreProjectPacksAsync(packs, !(caller is ImmediateCaller));
            if(p.others!=null)foreach(var extra in p.others)await RestoreActorAsync(extra,extra.payload,caller);
            if(!string.IsNullOrEmpty(p.modelGuid))await RestoreActorAsync(p,assets,caller);else front=Vector3.forward;
            RestoreOrder(drawOrder,p.renderOrder); RestoreOrder(calculationOrder,p.simulationOrder); ApplyDrawOrder();
            RefreshActorUI();
            RestoreAdditionalLights(p.additionalLights); RestoreAccessories(p); await RestoreMotionAsync(p.sceneMotion,false,caller); RestoreView(p);
            physicsMode=(PhysicsMode)p.physics;physicalFloor=p.floor;showBodies=p.showBodies;forceOnPlayback=p.forcePhysics;
            groundBrightness=p.groundBrightness;transparentGround=p.transparentGround;showSelfShadow=p.showSelfShadow;
            groundShadow=p.groundShadow;actor.modelSelfShadow=p.selfShadow;modelView=p.editorView;useCameraView=p.fromCamera;
            audioStartFrame=Mathf.Clamp(p.audioStartFrame,0,MaxFrame);
            soundtrack.Stop(); soundtrack.clip=clip; soundtrack.volume=p.volume;soundVolume.SetValueWithoutNotify(p.volume);muteWave=p.mute;scrubSound=p.scrub;
            peaks=amplitudes;waveform.MarkDirtyRepaint();
            movie=p.output;RestoreTransportSettings(p.start,p.end,p.repeat);gridToggle.value=p.grid;
            ChangeMode(p.modelMode);SelectTrack(VisibleTracks.Find(t=>t.Name==p.selection)??VisibleTracks.FirstOrDefault());
            BindLinks(p.constraints??Array.Empty<BoneLink>());ResetHistory();InvalidateDynamics();SetFrame(p.frame);
            RestoreMotion(p.sceneDraft,false,true);
            var current=actor;
            try {
                if(p.others!=null)foreach(var m in p.others){actor=drawOrder.First(a=>a.id==m.id);RestoreMotion(m.modelDraft,true,true);RefreshRigPreview();}
            } finally {actor=current;}
            if(actor.avatar!=null)RestoreMotion(p.modelDraft,true,true);
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
