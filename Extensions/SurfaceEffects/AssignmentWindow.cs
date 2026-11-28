using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    sealed class AssignmentWindow
    {
        const int Select=1007, Clear=1008, Visible=1011, Update=1005, Ok=1, Cancel=2,
            Expand=1200, Reset=1201, Load=1202, Save=1203, Target=1002, Previous=1221, Next=1222, FirstRow=2000;
        readonly HashSet<string> selectedKeys=new HashSet<string>();
        DesktopForm form;
        IDisposable handle;
        int page;
        string message;
        MaterialSlot[] slots;
        readonly Action<EffectSettings> publish;
        readonly IPluginContext context;
        EffectSettings baseline,working;
        readonly List<(string key,string name)> rows=new List<(string,string)>();
        readonly HashSet<string> expanded=new HashSet<string>();
        bool accept;
        string target="Main";
        readonly List<(string id,string label,string description)> targets=new List<(string,string,string)>();
        public Action Closed;
        public AssignmentWindow(IPluginContext context,MaterialSlot[] slots,EffectSettings settings,Action<EffectSettings> publish)
        {this.context=context;this.slots=slots;this.publish=publish;baseline=settings;working=baseline.Copy();}
        const int ModelLoad=1210,ModelSave=1211,Inspect=1212,SkipValidation=1213;
        bool skipValidation;
        CancellationTokenSource loading;
        readonly HashSet<Task> reads=new HashSet<Task>();
        public void Open(string title,int width,int height)
        {
            if(handle!=null)return;
            skipValidation=File.Exists(Path.Combine(context.SettingsDirectory,"skip-device-validation"));
            form=new DesktopForm { Name="assign",Title=title,Width=width,Height=height,Layout=Layout,
                Input=Input,DropFile=Assign,Closed=Closing };
            Refresh();
            handle=((IDesktopFormHost)context).OpenForm(form);
        }

        internal void SceneChanged(MaterialSlot[] next,EffectSettings state)
        {
            var live=new HashSet<string>(next.SelectMany(s=>new[]{s.Id,s.ActorId}));
            var removed=slots.SelectMany(s=>new[]{s.Id,s.ActorId}).Where(id=>!live.Contains(id)).ToHashSet();
            slots=next;
            CancelRead();
            foreach(var snapshot in new[]{working,baseline}){
                snapshot.rows.RemoveAll(row=>removed.Contains(row.slot));
                snapshot.defaults=state.defaults.Select(r=>r.Copy()).ToList();
            }
            expanded.RemoveWhere(removed.Contains);
            Refresh();
        }
        void ExpandSelection()
        {
            var owners=Selected().Select(i=>slots.FirstOrDefault(s=>s.Id==rows[i].key || s.ActorId==rows[i].key)?.ActorId)
                .Where(id=>id!=null).Distinct().ToArray();
            if(owners.Length==0)owners=slots.Select(s=>s.ActorId).Distinct().ToArray();
            bool collapse=owners.All(expanded.Contains);
            foreach(var id in owners){if(collapse)expanded.Remove(id);else expanded.Add(id);}
            Refresh();
        }
        void Refresh()
        {
            targets.Clear();targets.Add(("Main","Main","Main Render Target"));
            foreach(var a in working.rows.Concat(working.defaults??new List<Assignment>()).Where(r=>(r.target??"Main")=="Main" && r.mode=="effect" && r.targets!=null))
                foreach(var owner in slots.Where(s=>a.slot=="(default)" || a.slot==s.Id || a.slot==s.ActorId).Select(s=>s.ActorId).Distinct())
                    foreach(var t in a.targets){
                        string id=owner+"/"+a.slot+"/"+t.name;
                        if(!targets.Any(item=>item.id==id))targets.Add((id,t.name,t.description??t.name));
                    }
            int active=targets.FindIndex(t=>t.id==target);if(active<0){target="Main";active=0;}
            rows.Clear();
            rows.Add(("(default)","(default)"));
            foreach(var actor in slots.GroupBy(s=>s.ActorId)){
                rows.Add((actor.Key,actor.First().ActorName));
                if(expanded.Contains(actor.Key))
                    foreach(var slot in actor)rows.Add((slot.Id,"    "+slot.MaterialName));
            }
            form.Revision++;
            selectedKeys.IntersectWith(rows.Select(row=>row.key));
            message=targets[active].description;
            Layout(form.Width,form.Height);
        }

        List<int> Selected() => Enumerable.Range(0,rows.Count).Where(i=>selectedKeys.Contains(rows[i].key)).ToList();
        int PageSize => Math.Max(1,(form.Height-180)/27);
        void Layout(int w,int h)
        {
            form.Controls.Clear();
            int columns=4, width=Math.Max(1,(w-16)/columns);
            void Add(int id,string kind,string label,int x,int y,int size,bool enabled=true,bool check=false)
            {
                form.Controls.Add(new DesktopFormControl{Id=id,Name="effect-"+id,Kind=kind,Text=label,
                    X=x,Y=y,Width=Math.Max(1,size),Height=25,Hit=kind!="label",Enabled=enabled,Checked=check});
            }
            void Button(int id,string text,int column,int y,bool enabled=true)
                =>Add(id,"button",text,8+column*width,y,width-3,enabled);
            var actions=new[]{(Load,"設定を読込"),(Save,"設定を保存"),(ModelLoad,"モデル設定を読込"),(ModelSave,"モデル設定を保存"),
                (Reset,"default に戻す"),(Expand,"サブセット展開"),(Inspect,"逆アセンブル")};
            for(int i=0;i<actions.Length;i++)Button(actions[i].Item1,actions[i].Item2,i%columns,4+i/columns*27);
            Add(SkipValidation,"toggle","検証をスキップ",8+3*width,31,width,true,skipValidation);
            Add(Target,"choice",target,8,60,2*width);
            form.Controls.Last().Choices=targets.Select(t=>t.id).ToArray();
            form.Controls.Last().ChoiceLabels=targets.Select(t=>t.label).ToArray();
            Add(1004,"label",message,10+2*width,60,2*width-4);
            int maxPage=(rows.Count-1)/PageSize;
            page=Math.Max(0,Math.Min(page,maxPage));
            for(int i=page*PageSize;i<rows.Count && i<(page+1)*PageSize;i++){
                var row=rows[i];
                var entry=working.rows.Find(r=>r.slot==row.key && (r.target??"Main")==target);
                var supplied=working.defaults.Find(r=>r.slot==row.key && (r.target??"Main")==target);
                int y=90+(i%PageSize)*27;
                string path=entry?.mode=="effect"?entry.path:entry?.mode=="none"?"(none)":supplied?.path??"(default)";
                Add(FirstRow+i*2,"toggle",row.name,8,y,2*width-2,true,selectedKeys.Contains(row.key));
                Add(FirstRow+i*2+1,"toggle",path,8+2*width,y,2*width,true,entry?.visible!=false);
            }
            bool any=selectedKeys.Count>0;
            Button(Select,"ファイル選択",0,h-84,any);Button(Clear,"解除",1,h-84,any);
            Button(Visible,"非表示/表示",2,h-84,any);
            Button(Previous,"前のページ",0,h-56,page>0);Button(Next,"次のページ",1,h-56,page<maxPage);
            Button(Ok,"OK",1,h-28,loading==null);Button(Cancel,"キャンセル",2,h-28);
            Button(Update,"更新",3,h-28,loading==null && !working.Encode().SequenceEqual(baseline.Encode()));
        }

        void Input(int id,string action,string value,int clicks)
        {
            if(id==Target && action=="set"){CancelRead();target=value;page=0;Refresh();return;}
            if(id>=FirstRow && action=="set"){
                var key=rows[(id-FirstRow)/2].key;
                bool check=bool.Parse(value);
                if((id-FirstRow)%2==0){
                    CancelRead();
                    if(check)selectedKeys.Add(key);else selectedKeys.Remove(key);
                }else{Edit(key).visible=check;Publish();}
                Refresh();
                return;
            }
            if(action!="activate" && !(id==SkipValidation && action=="set"))return;
            if(id==Previous || id==Next){page+=id==Previous?-1:1;Refresh();return;}
            Command(id);
        }

        Assignment Edit(string id)
        {
            var a=working.rows.Find(r=>r.slot==id && (r.target??"Main")==target);
            if(a==null){a=new Assignment{slot=id,target=target,mode="inherit"};working.rows.Add(a);}return a;
        }
        string Pick(string title,string pattern,bool save=false) =>
            ((IDesktopFormHost)context).PickFile(title,pattern.TrimStart('*','.'),save);
        void Publish() => publish(working.Copy());
        void Assign(string file)
        {
            var selected=Selected();if(file==null || selected.Count==0)return;
            CancelRead();
            var request=loading=new CancellationTokenSource();var token=request.Token;
            var keys=selected.Select(i=>rows[i].key).ToArray();bool validate=!skipValidation;
            message="読み込み中　"+Path.GetFileName(file);Layout(form.Width,form.Height);
            var read=Task.Run(()=>{
                var loaded=new Assignment();EffectPlugin.ReadEffect(loaded,file,validate:validate,stop:token);
                return loaded;
            });
            lock(reads)reads.Add(read);
            read.ContinueWith(job=>{
                lock(reads)reads.Remove(read);
                var error=job.Exception?.GetBaseException();
                context.Post(()=>{
                    if(loading!=request)return;
                    loading=null;request.Dispose();
                    if(error!=null){ShowLog(error.Message);context.Report(error.Message);return;}
                    var loaded=job.Result;
                    foreach(string key in keys){
                        var a=Edit(key);a.mode="effect";a.path=file;a.source=loaded.source;a.images=loaded.images;a.targets=loaded.targets;a.rules=loaded.rules;a.dependencies=loaded.dependencies;
                    }
                    Refresh();Publish();
                });
            },TaskScheduler.Default);
        }
        void CancelRead()
        {
            if(loading==null)return;
            var request=loading;loading=null;request.Cancel();request.Dispose();
        }
        internal async Task CloseAndDrain()
        {
            handle?.Dispose();
            Task[] pending;lock(reads)pending=reads.ToArray();
            try{await Task.WhenAll(pending).ConfigureAwait(false);}catch(Exception){foreach(var t in pending)_=t.Exception;}
        }
        void Command(int id)
        {
            if(loading!=null && (id==Ok || id==Update))return;
            if(id==Clear || id==Visible || id==Reset || id==Load || id==ModelLoad)CancelRead();
            switch(id)
            {
                case Select:Assign(Pick("エフェクト選択","*.fx"));break;
                case Clear:case Visible:case Reset:
                    foreach(int i in Selected()){
                        var a=Edit(rows[i].key);
                        if(id==Visible)a.visible=!a.visible;
                        else if(id==Reset)working.rows.Remove(a);
                        else {a.mode="none";a.source=null;a.path=null;a.images=null;a.rules=null;a.targets=null;a.dependencies=null;}
                    }
                    Refresh();Publish();break;
                case Expand:ExpandSelection();break;
                case Update:baseline=working.Copy();Publish();Refresh();break;
                case Ok:accept=true;Publish();handle.Dispose();break;
                case Cancel:handle.Dispose();break;
                case ModelSave:case ModelLoad:ModelPreset(id==ModelSave);break;
                case Inspect:InspectShader();break;
                case SkipValidation:
                    skipValidation=!skipValidation;Refresh();
                    Directory.CreateDirectory(context.SettingsDirectory);var flag=Path.Combine(context.SettingsDirectory,"skip-device-validation");
                    if(skipValidation)File.WriteAllText(flag,"1");else if(File.Exists(flag))File.Delete(flag);break;
                case Load:
                    string path=Pick("設定読込","*.avaemm");
                    if(path!=null){working=EffectSettings.Decode(File.ReadAllBytes(path));Refresh();Publish();}break;
                case Save:
                    string output=Pick("設定保存","*.avaemm",true);
                    if(output!=null)AtomicFile.Write(output,p=>File.WriteAllBytes(p,working.Encode()));break;
            }
        }
        void ModelPreset(bool save)
        {
            var selected=Selected();if(selected.Count==0)return;
            string key=rows[selected[0]].key;var model=slots.FirstOrDefault(s=>s.Id==key || s.ActorId==key);
            if(model==null)return;
            var path=Pick(save?"モデル単位で設定保存":"モデル単位で設定読込","*.avaemd",save);
            if(path==null)return;
            if(save)
            {
                var preset=new EffectSettings{format="avaemd"};
                foreach(var a in working.rows.Where(r=>r.slot==model.ActorId || r.slot.StartsWith(model.ActorId+"/",StringComparison.Ordinal)))
                {
                    var row=a.Copy();row.slot=row.slot.Substring(model.ActorId.Length);
                    if(row.slot.Length==0)row.slot=".";preset.rows.Add(row);
                }
                AtomicFile.Write(path,tmp=>File.WriteAllBytes(tmp,preset.Encode()));
            }
            else
            {
                var preset=EffectSettings.Decode(File.ReadAllBytes(path));
                if(preset.format!="avaemd")throw new InvalidDataException("材質プリセットを選択してください。");
                foreach(var row in preset.rows){
                    row.slot=row.slot=="."?model.ActorId:model.ActorId+row.slot;
                    if(row.slot!=model.ActorId && !slots.Any(s=>s.Id==row.slot))continue;
                    working.rows.RemoveAll(a=>a.slot==row.slot && a.target==row.target);working.rows.Add(row);
                }
                Refresh();Publish();
            }
        }
        internal Action<string> InspectLog;
        void InspectShader()
        {
            var selected=Selected();if(selected.Count==0)return;
            var a=working.rows.FirstOrDefault(r=>r.slot==rows[selected[0]].key && (r.target??"Main")==target);
            if(a?.mode!="effect")return;
            var passes=EffectCompiler.Compile(a.source);
            try {string text=string.Join("\r\n",passes.Select(p=>p.Disassemble()));context.Post(()=>InspectLog?.Invoke(text));}
            finally{foreach(var p in passes)p.Dispose();}
        }
        public void ShowLog(string text){
            message=string.IsNullOrEmpty(text)?"Main Render Target":text;
            Layout(form.Width,form.Height);
        }
        void Closing(){
            handle=null;CancelRead();
            if(!accept)publish(baseline.Copy());
            Closed?.Invoke();
        }
    }
}
