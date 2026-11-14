using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class PluginManifest
        {
            public int manifestVersion, stateSchema;
            public bool developmentOnly;
            public string id, version,displayName;
            public SdkVersion sdk;
            public EntryPoint entry;
            public string[] dependencies;
        }
        [Serializable] sealed class SdkVersion { public int major, minMinor; }
        [Serializable] sealed class EntryPoint { public string assembly, type; }

        sealed class Registration : IDisposable
        {
            Action remove;
            public Registration(Action remove) { this.remove=remove; }
            public void Dispose() => System.Threading.Interlocked.Exchange(ref remove,null)?.Invoke();
        }

        sealed class Extension : IPluginRegistrar, IPluginContext, IMotionEditor, IEvaluationHost, IAssetCatalog, IProjectStorage, ISceneEditor, IDisposable
        {
            internal PluginManifest manifest;
            internal IAvaPlugin plugin;
            internal DanceApp app;
            internal bool active;
            internal readonly List<(string id, string menu, string caption, Binding binding)> items = new List<(string,string,string,Binding)>();
            internal ResolveEventHandler resolver;
            readonly List<IDisposable> services=new List<IDisposable>();
            public IDisposable ObserveProjects(Action<string> opened,Action<string> saved)
            {
                app.projectOpened+=opened;app.projectSaved+=saved;
                var registration=new Registration(()=>{app.projectOpened-=opened;app.projectSaved-=saved;});
                services.Add(registration);return registration;
            }
            public MaterialSlot[] Materials() => app.EnumerateMaterials().Select(e=>e.slot).ToArray();
            public IDisposable AddSurfaceProvider(ISurfaceProvider provider)
            {
                app.surfaceProviders.Add(provider);
                var registration=new Registration(()=>app.surfaceProviders.Remove(provider));
                services.Add(registration);return registration;
            }
            public string PackageDirectory { get; internal set; }
            public string SettingsDirectory { get; internal set; }
            public IntPtr MainWindow => app.mainWindowHandle;
            public long DocumentGeneration => app.documentGeneration;
            internal readonly Dictionary<string,string> shortcuts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            public IDisposable AddShortcut(string command,string chord)
            {
                if(!items.Any(i=>i.id==command))throw new ArgumentException(command);
                if(string.IsNullOrWhiteSpace(chord) || coreChords.Contains(chord) || app.extensions.Any(p=>p.shortcuts.ContainsKey(chord)) || shortcuts.ContainsKey(chord))
                    throw new ArgumentException("使用中または空のショートカットです: "+chord);
                shortcuts.Add(chord,command);return new Registration(()=>shortcuts.Remove(chord));
            }
            public IDisposable AddCommand(string id, string menu, string caption, Action execute, Func<bool> available, Func<bool> check)
            {
                if (!id.StartsWith(manifest.id+".",StringComparison.Ordinal) || items.Any(i=>i.id==id) ||
                    string.IsNullOrEmpty(menu) || string.IsNullOrEmpty(caption) || execute==null)
                    throw new InvalidDataException("拡張コマンドが不正です: "+id);
                var item=(id,menu,caption,new Binding {Run=execute,Available=available,Checked=check});
                items.Add(item); return new Registration(()=>items.Remove(item));
            }
            public void Post(Action operation) { int version=activation; app.pluginQueue.Enqueue(()=>{if(active && activation==version) operation();}); }
            internal int activation;
            public IDisposable KeepActive()
            {
                app.AcquireActivity();
                var host=app; return new Registration(()=>host.pluginQueue.Enqueue(host.ReleaseActivity));
            }
            string StateKey => "extensions/"+manifest.id+"/state.json";
            public bool CanExecute(string command) => active && app.FindCommand(command,out var id) && app.Available(id);
            public bool Execute(string command) => active && app.FindCommand(command,out var id) && app.Execute(id);
            public IDisposable ObserveEvaluation(string id,EvaluationStage stage,int order,string[] after,Action<EvaluationSample> callback)
            {
                if(!id.StartsWith(manifest.id+".",StringComparison.Ordinal))throw new ArgumentException(nameof(id));
                var hook=app.ObserveStage(id,stage,order,after,callback);services.Add(hook);return hook;
            }
            public SceneSnapshot ReadScene()=>app.SceneState();
            public RigProfile ReadRig(string owner)
            {
                var rig=app.drawOrder.First(a=>a.id==owner).rigProfile;
                return rig==null?null:JsonUtility.FromJson<RigProfile>(JsonUtility.ToJson(rig));
            }
            public void Select(string owner,string track=null){if(active)app.SelectSceneItem(owner,track);}
            public void SetOrder(long document,string[] owners,bool physics){if(active)app.EditSceneOrder(document,owners,physics);}
            public long SourceRevision=>app.sourceRevision;
            public SourceFiles[] Sources()=>app.SourceFiles().ToArray();
            public MotionSnapshot ReadMotion() => app.SnapshotMotion();
            public KeyValue Sample(string owner,string track,float frame) => app.SampleMotion(owner,track,frame);
            public void ApplyMotion(long document,KeyEdit[] edits)
            {
                if(!active)throw new InvalidOperationException("拡張が停止しています。");
                app.ApplyMotionChanges(document,edits);
            }
            public RigProfile[] ReadRigs(string path) => RigFiles.Read(path);
            public byte[] ReadState() => app.extraEntries.TryGetValue(StateKey,out var data) ? (byte[])data.Clone() : null;
            public string[] Blobs()
            {
                string prefix="extensions/"+manifest.id+"/blobs/";
                return app.extraEntries.Keys.Where(k=>k.StartsWith(prefix,StringComparison.Ordinal)).Select(k=>k.Substring(prefix.Length)).OrderBy(k=>k,StringComparer.Ordinal).ToArray();
            }
            public byte[] ReadBlob(string id)
            {
                var entry=BlobPath(manifest,id);return app.extraEntries.TryGetValue(entry,out var data)?(byte[])data.Clone():null;
            }
            public void WriteBlock(byte[] state,IReadOnlyDictionary<string,byte[]> blobChanges)
            {
                if(!active)throw new InvalidOperationException("拡張が停止しています。");
                app.StoreExtension(manifest,state,blobChanges);
            }
            public void WriteState(byte[] bytes)
            {
                if (!active) return;
                app.StoreExtension(manifest,bytes);
            }
            public void Report(string message) { app.status.text=message; Debug.Log(message); }
            public void Dispose()
            {
                active=false; activation++;
                try { plugin?.Deactivate(); }
                finally {
                    try { plugin?.Dispose(); }
                    finally { foreach(var service in services) service.Dispose(); services.Clear(); if(resolver!=null) AppDomain.CurrentDomain.AssemblyResolve-=resolver; items.Clear();shortcuts.Clear(); }
                }
            }
        }

        event Action<string> projectOpened,projectSaved;
        static void NotifyProject(Action<string> handlers,string path)
        {
            if(handlers==null)return;
            foreach(Action<string> action in handlers.GetInvocationList())
                try{action(path);}catch(Exception ex){Debug.LogWarning(ex.Message);}
        }
        readonly List<Extension> packages=new List<Extension>();
        readonly List<Extension> extensions = new List<Extension>();
        readonly ConcurrentQueue<Action> pluginQueue = new ConcurrentQueue<Action>();
        Dictionary<string,byte[]> extraEntries = new Dictionary<string,byte[]>();
        Dictionary<string,byte[]> savedEntries;
        long documentGeneration,sourceRevision;
        IntPtr mainWindowHandle;
        bool previousBackground;
        int activityCount, backgroundUsers;
        readonly Dictionary<string,Command> extensionCommands=new Dictionary<string,Command>();
        bool FindCommand(string name,out Command id)
        {
            if(extensionCommands.TryGetValue(name,out id))return true;
            return name.StartsWith("core.",StringComparison.Ordinal) && Enum.TryParse(name.Substring(5),out id);
        }
        MmdMenu.Entry[] combinedMenus = MmdMenu.Bar;

        void AcquireBackground()
        {
            if(backgroundUsers++==0) {
                previousBackground=Application.runInBackground;
                if(!previousBackground) Application.runInBackground=true;
            }
        }
        void ReleaseBackground() { if(backgroundUsers>0 && --backgroundUsers==0 && !previousBackground) Application.runInBackground=false; }
        void AcquireActivity() { activityCount++; AcquireBackground(); }
        void ReleaseActivity() { if(activityCount>0) { activityCount--; ReleaseBackground(); } }
        void PumpExtensions()
        {
            while(pluginQueue.TryDequeue(out var action))
                try { action(); } catch(Exception ex) { Debug.LogException(ex); status.text=ex.Message; }
        }

        void LoadExtensions()
        {
            using(var process=System.Diagnostics.Process.GetCurrentProcess()) mainWindowHandle=process.MainWindowHandle;
            var baseDir=Path.GetDirectoryName(Application.dataPath);
            string root=Path.Combine(baseDir, Application.isEditor ? "Builds/Windows/Extensions" : "Extensions");
            if(!Directory.Exists(root)) return;
            packages.Clear();
            foreach(var dir in Directory.GetDirectories(root).OrderBy(d=>d,StringComparer.Ordinal))
            {
                try {
                    var m=JsonUtility.FromJson<PluginManifest>(File.ReadAllText(Path.Combine(dir,"plugin.json")));
                    if(m==null || m.manifestVersion!=1 || m.sdk?.major!=1 || m.sdk.minMinor>0 || m.entry==null ||
                        !Regex.IsMatch(m.id??"","^[a-z0-9][a-z0-9.-]*$")) throw new InvalidDataException("拡張形式またはSDK版が不正です。");
                    if(m.developmentOnly && !Application.isEditor && !Debug.isDebugBuild) continue;
                    packages.Add(new Extension {app=this,manifest=m,PackageDirectory=dir,
                        SettingsDirectory=Path.Combine(WorkspacePreferences.ExtensionRoot,"Extensions",m.id)});
                } catch(Exception ex) {Debug.LogWarning(dir+": "+ex.Message);}
            }
            var candidates=packages.GroupBy(p=>p.manifest.id).Where(group=>{
                if(group.Count()==1) return true;
                Debug.LogWarning("重複した拡張ID: "+group.Key); return false;
            }).Select(g=>g.Single()).ToList();
            packages.Clear();packages.AddRange(candidates);
            var loaded=new HashSet<string>();
            while(candidates.Count>0)
            {
                var ext=candidates.Find(e=>e.manifest.dependencies==null || e.manifest.dependencies.All(loaded.Contains));
                if(ext==null){Debug.LogWarning("拡張の依存関係を解決できません: "+string.Join(", ",candidates.Select(p=>p.manifest.id)));break;}
                candidates.Remove(ext);
                if(WorkspacePreferences.GetInt("Extension.Enabled."+ext.manifest.id,1)==0) continue;
                if(ActivatePackage(ext)) loaded.Add(ext.manifest.id);
            }
        }

        bool ActivatePackage(Extension ext)
        {
                try {
                    var dir=Path.GetFullPath(Path.Combine(ext.PackageDirectory,"managed"))+Path.DirectorySeparatorChar;
                    var file=Path.GetFullPath(Path.Combine(ext.PackageDirectory,ext.manifest.entry.assembly));
                    if(!file.StartsWith(dir,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("拡張のassemblyパスが不正です。");
                    ext.resolver=(_,args)=>{
                        var request=new AssemblyName(args.Name);
                        if(args.RequestingAssembly==null || !args.RequestingAssembly.Location.StartsWith(dir,StringComparison.OrdinalIgnoreCase))return null;
                        if(request.Name==typeof(IAvaPlugin).Assembly.GetName().Name) return typeof(IAvaPlugin).Assembly;
                        var dependency=Path.Combine(dir,request.Name+".dll");
                        return File.Exists(dependency) && AssemblyName.GetAssemblyName(dependency).FullName==request.FullName ?
                            Assembly.LoadFrom(dependency) : null;
                    };
                    AppDomain.CurrentDomain.AssemblyResolve+=ext.resolver;
                    var type=Assembly.LoadFrom(file).GetType(ext.manifest.entry.type,true);
                    ext.plugin=(IAvaPlugin)Activator.CreateInstance(type);
                    ext.plugin.Register(ext); ext.active=true; ext.plugin.Activate(ext);
                    extensions.Add(ext);return true;
                } catch(Exception ex) { try {ext.Dispose();}catch(Exception cleanup){Debug.LogWarning(cleanup.Message);} Debug.LogWarning(ext.manifest.id+": "+ex); return false; }
        }

        void ComposeExtensionMenus()
        {
            var menus=MmdMenu.Bar.ToList();
            foreach(var old in commands.Keys.Where(c=>(int)c>=10000).ToArray())commands.Remove(old);
            extensionCommands.Clear();int dynamicId=10000;
            foreach(var group in extensions.SelectMany(e=>e.items).GroupBy(item=>item.menu).OrderBy(g=>g.Key=="プラグイン(&E)"?0:1))
            {
                var children=new List<MmdMenu.Entry>();
                foreach(var item in group)
                {
                    var id=(Command)dynamicId++;commands.Add(id,item.binding);extensionCommands.Add(item.id,id);
                    children.Add(new MmdMenu.Entry(id,item.caption,item.caption));
                }
                menus.Add(new MmdMenu.Entry((Command)dynamicId++,group.Key,group.Key,children.ToArray()));
            }
            if(packages.Count>0)
            {
                var settings=(Command)dynamicId++;
                Bind(settings,ManageExtensions);
                var item=new MmdMenu.Entry(settings,"拡張の管理","Manage extensions");
                int index=menus.FindIndex(g=>g.Japanese=="プラグイン(&E)");
                if(index<0)menus.Insert(MmdMenu.Bar.Length,new MmdMenu.Entry((Command)dynamicId++,"プラグイン(&E)","Plugins(&E)",item));
                else {
                    var group=menus[index];
                    menus[index]=new MmdMenu.Entry(group.Id,group.Japanese,group.English,group.Children.Append(item).ToArray());
                }
            }
            combinedMenus=menus.ToArray();
        }

        void ManageExtensions()
        {
            ShowDialog("拡張の管理",out var body);
            foreach(var package in packages)
            {
                var toggle=new Toggle((package.manifest.displayName??package.manifest.id)+"   "+package.manifest.version){value=package.active};body.Add(toggle);
                toggle.RegisterValueChangedCallback(e=>{
                    if(e.newValue)
                    {
                        if(package.manifest.dependencies?.Any(id=>!extensions.Any(p=>p.manifest.id==id))==true || !ActivatePackage(package))
                        {toggle.SetValueWithoutNotify(false);return;}
                    }
                    else
                    {
                        if(extensions.Any(p=>p.manifest.dependencies?.Contains(package.manifest.id)==true))
                        {toggle.SetValueWithoutNotify(true);return;}
                        package.Dispose();extensions.Remove(package);
                    }
                    WorkspacePreferences.SetInt("Extension.Enabled."+package.manifest.id,e.newValue?1:0);
                    PresentMenus(MenuRoot.Q("menu"));SelectRibbonTab(ribbonTab);
                });
            }
            Action(body,"閉じる",true,CloseDialog);
        }

        void PopulateExtensionRibbon(VisualElement tools)
        {
            foreach(var menu in combinedMenus.Skip(MmdMenu.Bar.Length))
                foreach(var child in menu.Children)
                    ProfileAction(tools,child.Label(false),()=>Execute(child.Id),()=>Available(child.Id));
        }

        void DisposeExtensions()
        {
            foreach(var e in extensions.AsEnumerable().Reverse()) try {e.Dispose();} catch(Exception ex){Debug.LogWarning(ex.Message);}
            extensions.Clear(); PumpExtensions();
        }
    }
}
