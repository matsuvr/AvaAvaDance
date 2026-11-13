using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    [DataContract] public sealed class Assignment
    {
        [DataMember] public string slot,mode,path,source,target;
        [DataMember] public TargetInfo[] targets;
        [DataMember] public RuleEffect[] rules;
        [DataMember] public ImageAsset[] images;
        [DataMember] public string[] dependencies;
        [DataMember] public bool visible=true;
        public Assignment Copy()=>(Assignment)MemberwiseClone();
    }
    [DataContract] public sealed class RuleEffect
    {
        [DataMember] public string file,path,source;
        [DataMember] public ImageAsset[] images;
    }
    [DataContract] public sealed class TargetInfo
    {
        [DataMember]public string name,description;
    }
    [DataContract] public sealed class ImageAsset
    {
        [DataMember] public string resource;
        [DataMember] public byte[] bytes;
    }
    [DataContract] public sealed class EffectSettings
    {
        [DataMember] public int schemaVersion=1;
        [DataMember] public string format="avaemm";
        [DataMember] public bool enabled=true,watch=true,autoSave=true;
        [DataMember] public List<Assignment> rows=new List<Assignment>();
        [DataMember] public List<Assignment> defaults=new List<Assignment>();
        public EffectSettings Copy()
        {
            var result=(EffectSettings)MemberwiseClone();
            result.rows=rows.Select(r=>r.Copy()).ToList();result.defaults=(defaults??new List<Assignment>()).Select(r=>r.Copy()).ToList();return result;
        }
        public byte[] Encode()
        {
            using var output=new MemoryStream();
            new DataContractJsonSerializer(typeof(EffectSettings)).WriteObject(output,this);return output.ToArray();
        }
        public static EffectSettings Decode(byte[] bytes)
        {
            if(bytes==null)return new EffectSettings();
            using var input=new MemoryStream(bytes);
            var state=(EffectSettings)new DataContractJsonSerializer(typeof(EffectSettings)).ReadObject(input);
            if(state==null || state.schemaVersion!=1 || state.rows==null || state.rows.Any(r=>string.IsNullOrEmpty(r?.slot)))
                throw new InvalidDataException("エフェクト設定の版または割当が不正です。");
            state.defaults??=new List<Assignment>();return state;
        }
    }

    public sealed partial class EffectPlugin : IAvaPlugin,ISurfaceProvider,IAsyncShutdown
    {
        IPluginContext context;
        AssignmentWindow window;
        readonly List<System.Threading.Tasks.Task> closingWindows=new List<System.Threading.Tasks.Task>();
        LogWindow logWindow;
        EffectSettings state=new EffectSettings();
        Dictionary<string,SurfacePass[]> programs=new Dictionary<string,SurfacePass[]>();
        Dictionary<string,SurfacePlan> plans=new Dictionary<string,SurfacePlan>();
        readonly Dictionary<string,DateTime> checkedFiles=new Dictionary<string,DateTime>();
        readonly Dictionary<(string module,int subset,int flags,string role),SurfaceBinding> variants=new Dictionary<(string,int,int,string),SurfaceBinding>();
        void ReleaseVariants()
        {
            foreach(var binding in variants.Values)foreach(var pass in binding.Passes??Array.Empty<SurfacePass>())pass.Dispose();
            variants.Clear();
        }
        DateTime nextScan;
        string diagnostics="";
        long generation=-1;
        bool writable;
        public bool HasBindings {get{Synchronize();DiscoverDefaults();return state.enabled && (state.rows.Count>0 || state.defaults.Count>0);}}

        public void Register(IPluginRegistrar registrar)
        {
            void Add(string id,string label,Action action,Func<bool> check=null) =>
                registrar.AddCommand("ava.mme."+id,"AvaAvaEffect",label,()=>{Synchronize();action();},null,check);
            Add("assign","エフェクト割当(&M)",Open);
            Add("enabled","エフェクト使用(&E)",()=>{state.enabled=!state.enabled;Persist();},()=>state.enabled);
            registrar.AddShortcut("ava.mme.enabled","Ctrl+Shift+E");
            Add("watch","自動更新(&O)",()=>{state.watch=!state.watch;Persist();},()=>state.watch);
            Add("save","割当ファイル自動保存(&S)",()=>{state.autoSave=!state.autoSave;Persist();},()=>state.autoSave);
            Add("reload","全て更新(&R)",()=>Reload(true));
            Add("log","ログ(&L)",()=>OpenLog(diagnostics));
            Add("about","バージョン情報(&A)",()=>context.Report("AvaAvaEffect 1.0"));
        }
        public void Activate(IPluginContext host)
        {
            context=host;host.AddSurfaceProvider(this);
            host.ObserveProjects(path=>{
                Synchronize();
                var sidecar=Path.ChangeExtension(path,".avaemm");
                if(context.ReadState()==null && File.Exists(sidecar))Apply(EffectSettings.Decode(File.ReadAllBytes(sidecar)));
            },path=>{
                Synchronize();
                if(!writable || !state.autoSave)return;
                var sidecar=Path.ChangeExtension(path,".avaemm");
                if(state.rows.Count>0 || state.defaults.Count>0 || File.Exists(sidecar))
                    VmdDocument.AtomicFile(sidecar,temp=>File.WriteAllBytes(temp,state.Encode()));
            });
        }
        public System.Threading.Tasks.Task CloseAsync()=>System.Threading.Tasks.Task.WhenAll(closingWindows.Concat(new[]{window?.CloseAndDrain(),logWindow?.CloseAsync()}).Where(t=>t!=null));
        public void Deactivate(){CloseAsync().GetAwaiter().GetResult();closingWindows.Clear();window=null;logWindow=null;Release();}
        public void Dispose(){}
        void OpenLog(string text)
        {
            if(logWindow==null)logWindow=new LogWindow(context);
            logWindow.Content=text;logWindow.Open("AvaAvaEffect ログ",620,480,true);logWindow.Refresh();
        }
        sealed class LogWindow:ClassicWindow
        {
            public string Content="";
            public LogWindow(IPluginContext context):base(context){}
            protected override void Build(){Add(20,"EDIT",Content,0x50b108c4);}
            protected override void Layout(int w,int h){Place(20,4,4,w-8,h-8);}
            protected override void Command(int id,int code){}
            internal void Refresh()=>OnWindow(()=>Text(20,Content));
        }
        void Release(){ReleaseVariants();foreach(var pass in programs.Values.SelectMany(p=>p))pass.Dispose();programs.Clear();plans.Clear();checkedFiles.Clear();}

        void Synchronize()
        {
            if(generation==context.DocumentGeneration)return;
            generation=context.DocumentGeneration;sourceVersion=-1;
            var previous=window;window=null;if(previous!=null)closingWindows.Add(previous.CloseAndDrain());
            closingWindows.RemoveAll(t=>t.IsCompleted);Release();
            state=new EffectSettings();writable=true;
            try {state=EffectSettings.Decode(context.ReadState());BuildPrograms(state);}
            catch(Exception ex){writable=false;diagnostics=ex.Message;context.Report(diagnostics);}
        }
        void Persist(){if(writable)context.WriteState(state.enabled && state.watch && state.autoSave && state.rows.Count==0 && state.defaults.Count==0 ? null : state.Encode());}
        internal static void ReadEffect(Assignment row,string path,Func<string,byte[]> read=null,bool validate=false,System.Threading.CancellationToken stop=default)
        {
            stop.ThrowIfCancellationRequested();var files=new List<string>();
            var source=EffectCompiler.Source(path,files,read);stop.ThrowIfCancellationRequested();
            var passes=EffectCompiler.Compile(source,out var plan);
            try{if(validate)foreach(var pass in passes)pass.ValidateDevice();}finally{foreach(var pass in passes)pass.Dispose();}
            var images=plan.Textures.Where(t=>!string.IsNullOrWhiteSpace(t.Resource)).Select(t=>t.Resource).Distinct().Select(resource=>{
                string file=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path),resource));files.Add(file);
                var data=read==null?File.ReadAllBytes(file):read(file);
                if(data.Length>64*1024*1024)throw new IOException("参照画像が大きすぎます: "+resource);
                return new ImageAsset{resource=resource,bytes=data};
            }).ToArray();
            var effects=new List<RuleEffect>();
            foreach(var tex in plan.Textures.Where(t=>t.DefaultEffect!=null))
                foreach(var rule in tex.DefaultEffect.Split(';'))
                {
                    int sep=rule.IndexOf('=');if(sep<0)continue;var file=rule.Substring(sep+1).Trim();
                    if(!file.EndsWith(".fx",StringComparison.OrdinalIgnoreCase) || effects.Any(e=>e.file==file))continue;
                    string other=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path),file));
                    stop.ThrowIfCancellationRequested();var childSource=EffectCompiler.Source(other,files,read);
                    var stages=EffectCompiler.Compile(childSource,out var childPlan);
                    foreach(var stage in stages)stage.Dispose();
                    var childImages=childPlan.Textures.Where(t=>!string.IsNullOrWhiteSpace(t.Resource)).Select(t=>t.Resource).Distinct().Select(resource=>{
                        string image=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(other),resource));files.Add(image);
                        var bytes=read==null?File.ReadAllBytes(image):read(image);
                        if(bytes.Length>64*1024*1024)throw new IOException("参照画像が大きすぎます: "+resource);
                        return new ImageAsset{resource=resource,bytes=bytes};
                    }).ToArray();
                    effects.Add(new RuleEffect {file=file,path=other,source=childSource,images=childImages});
                }
            stop.ThrowIfCancellationRequested();row.rules=effects.ToArray();
            row.targets=plan.Textures.Where(t=>t.Semantic=="OFFSCREENRENDERTARGET").Select(t=>new TargetInfo{name=t.Name,description=t.Description}).ToArray();
            row.path=path;row.source=source;row.mode="effect";row.images=images;row.dependencies=files.Distinct().ToArray();
        }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string,string> sourceHashes=new System.Runtime.CompilerServices.ConditionalWeakTable<string,string>();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageAsset[],string> imageHashes=new System.Runtime.CompilerServices.ConditionalWeakTable<ImageAsset[],string>();
        static string Hash(byte[] bytes){
            using var sha=System.Security.Cryptography.SHA256.Create();return Convert.ToBase64String(sha.ComputeHash(bytes));
        }
        static string ModuleKey(Assignment a)
        {
            var source=sourceHashes.GetValue(a.source??"",text=>Hash(System.Text.Encoding.UTF8.GetBytes(text)));
            var images=a.images==null?"":imageHashes.GetValue(a.images,list=>{
                using var buffer=new MemoryStream();using var data=new BinaryWriter(buffer);
                foreach(var image in list){data.Write(image.resource);data.Write(Hash(image.bytes));}
                data.Flush();return Hash(buffer.ToArray());
            });
            return a.path+"\n"+source+"\n"+images;
        }
        void BuildPrograms(EffectSettings next)
        {
            var active=new Dictionary<string,SurfacePass[]>();var nextPlans=new Dictionary<string,SurfacePlan>();
            try {
                var sources=next.rows.Concat(next.defaults).SelectMany(a=>new[]{a}.Concat((a.rules??Array.Empty<RuleEffect>()).Select(e=>new Assignment{mode="effect",source=e.source,path=e.path,images=e.images})));
                foreach(var a in sources)
                {
                    if(a.mode!="effect")continue;
                    var key=ModuleKey(a);if(active.ContainsKey(key))continue;
                    SurfacePlan plan;
                    if(!programs.TryGetValue(key,out var pass))pass=EffectCompiler.Compile(a.source,out plan);else plan=plans[key];
                    foreach(var tex in plan.Textures)tex.Data=a.images?.FirstOrDefault(image=>image.resource==tex.Resource)?.bytes;
                    active.Add(key,pass);nextPlans.Add(key,plan);
                }
            } catch {
                foreach(var pair in active.Where(p=>!programs.ContainsKey(p.Key)))foreach(var pass in pair.Value)pass.Dispose();
                throw;
            }
            foreach(var pair in programs.Where(p=>!active.ContainsKey(p.Key)))foreach(var pass in pair.Value)pass.Dispose();
            ReleaseVariants();programs=active;plans=nextPlans;
        }
        void Apply(EffectSettings next)
        {
            BuildPrograms(next);state=next;Persist();
        }
        void Open()
        {
            if(!writable)throw new InvalidDataException(diagnostics);
            if(window==null)
            {
                long doc=generation;
                var created=new AssignmentWindow(context,context.Materials(),state.Copy(),next=>{
                    if(context.DocumentGeneration!=doc)return;
                    try{Apply(next);}catch(Exception ex){diagnostics=ex.Message;context.Report(diagnostics);}
                });
                created.InspectLog=OpenLog;window=created;created.Closed=()=>context.Post(()=>{if(window!=created)return;window=null;closingWindows.RemoveAll(t=>t.IsCompleted);closingWindows.Add(created.CloseAndDrain());});
            }
            window.Open("AvaAvaEffect - エフェクトファイル割り当て",576,408,true);
        }
        void Reload(bool force)
        {
            var next=state.Copy();bool changed=false;
            foreach(var group in next.rows.Concat(next.defaults).Where(a=>a.mode=="effect" && File.Exists(a.path)).GroupBy(a=>a.path))
            {
                var first=group.First();var deps=first.dependencies??new[]{first.path};bool different=force;
                foreach(var file in deps){
                    var last=File.GetLastWriteTimeUtc(file);
                    if(checkedFiles.TryGetValue(file,out var prior) && prior!=last)different=true;
                    checkedFiles[file]=last;
                }
                if(!different)continue;
                ReadEffect(first,first.path);changed=true;
                foreach(var item in group.Skip(1)){item.source=first.source;item.targets=first.targets;item.rules=first.rules;item.dependencies=first.dependencies;item.images=first.images;}
            }
            if(changed)Apply(next);
        }
        public SurfaceBinding Resolve(MaterialSlot slot)
        {
            Synchronize();
            if(state.watch && DateTime.UtcNow>=nextScan)
            {
                nextScan=DateTime.UtcNow.AddSeconds(1);
                try{Reload(false);}catch(Exception ex){diagnostics=ex.Message;context.Report(diagnostics);}
            }
            if(!state.enabled)return null;
            var entries=new[]{slot.Id,slot.ActorId,"(default)"}.Select(key=>state.rows.Find(r=>r.slot==key && (r.target??"Main")== (slot.Target??"Main"))).Where(r=>r!=null).ToList();
            if(entries.Any(a=>!a.visible))return new SurfaceBinding{Visible=false};
            var effect=entries.FirstOrDefault(a=>a.mode!="inherit");
            bool useMain=slot.Target==null || slot.DefaultEffect=="main_default";
            if(effect==null && useMain)effect=state.defaults.FirstOrDefault(a=>a.slot==slot.Id)??state.defaults.FirstOrDefault(a=>a.slot==slot.ActorId);
            if(effect==null && slot.Target!=null && slot.DefaultEffect!=null)
            {
                if(slot.DefaultEffect.Equals("hide",StringComparison.OrdinalIgnoreCase))return new SurfaceBinding{Visible=false};
                foreach(var assignment in state.rows.Concat(state.defaults).Where(a=>a.mode=="effect" && a.rules!=null))
                {
                    if(!slot.Target.Contains("/"+assignment.slot+"/"))continue;
                    var rule=assignment.rules.FirstOrDefault(r=>r.file==slot.DefaultEffect);
                    if(rule!=null){effect=new Assignment{slot=slot.Target,mode="effect",source=rule.source,path=rule.path,images=rule.images};break;}
                }
            }
            if(effect==null)return null;
            if(effect.mode!="effect")return new SurfaceBinding();
            string module=ModuleKey(effect);if(!programs.TryGetValue(module,out var passes))return new SurfaceBinding();
            var plan=plans[module];
            if(plan.SelectPerMaterial || slot.DrawRole!=null){
                int flags=(slot.Textured?1:0)|(slot.SphereMapped?2:0)|(slot.Toon?4:0)|(slot.SelfShadow?8:0);
                var variantKey=(module,plan.SubsetSensitive?slot.Subset:-1,flags,slot.DrawRole);
                if(!variants.TryGetValue(variantKey,out var choice)){
                    try{
                        passes=EffectCompiler.Compile(effect.source,slot,out var variantPlan);
                        foreach(var tex in variantPlan.Textures)tex.Data=plan.Textures.FirstOrDefault(t=>t.Name==tex.Name)?.Data;
                        choice=new SurfaceBinding{Visible=passes==null || passes.Length>0,Passes=passes,Plan=variantPlan};
                    }catch(Exception ex){diagnostics=ex.Message;context.Report(diagnostics);choice=new SurfaceBinding();}
                    variants.Add(variantKey,choice);
                }
                if(choice.Passes==null)return new SurfaceBinding();
                passes=choice.Passes;plan=choice.Plan;
            }
            return new SurfaceBinding{Visible=passes.Length>0,Passes=passes,Plan=plan,Instance=slot.ActorId+"/"+effect.slot};
        }
    }
}
