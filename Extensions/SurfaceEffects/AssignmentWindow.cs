using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    sealed class AssignmentWindow : ClassicWindow
    {
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern void ava_ui_list_setup(IntPtr list,string left,string right);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern void ava_ui_list_row(IntPtr list,int index,string left,string right,bool visible);
        [DllImport("shell32",CharSet=CharSet.Unicode)] static extern uint DragQueryFileW(IntPtr drop,uint index,StringBuilder text,uint size);
        const int List=1003, Select=1007, Clear=1008, Visible=1011, Update=1005, Ok=1, Cancel=2, Expand=1200, Reset=1201, Load=1202, Save=1203;
        MaterialSlot[] slots;
        readonly Action<EffectSettings> publish;
        readonly IPluginContext context;
        EffectSettings baseline,working;
        readonly List<(string key,string name)> rows=new List<(string,string)>();
        readonly HashSet<string> expanded=new HashSet<string>();
        bool accept,refreshing;
        string target="Main";
        readonly List<(string id,string label,string description)> targets=new List<(string,string,string)>();
        public Action Closed;
        public AssignmentWindow(IPluginContext context,MaterialSlot[] slots,EffectSettings settings,Action<EffectSettings> publish):base(context)
        {this.context=context;this.slots=slots;this.publish=publish;baseline=settings;working=baseline.Copy();}
        const int ModelLoad=1210,ModelSave=1211,Inspect=1212,SkipValidation=1213;
        bool skipValidation;
        CancellationTokenSource loading;
        string[] loadingSlots=Array.Empty<string>();
        readonly HashSet<Task> reads=new HashSet<Task>();
        protected override void Build()
        {
            Menu("ファイル(&F)",(Load,"設定を読込(&O)"),(Save,"設定を保存(&S)"),(0,null),(ModelLoad,"モデル単位で設定読込(&M)"),
                (ModelSave,"モデル単位で設定保存(&N)"),(0,null),(Ok,"閉じる(&C)"));
            Menu("編集(&E)",(Select,"ファイル選択(&F)"),(Clear,"解除(&N)"),(Visible,"非表示/表示(&H)"),(Reset,"default設定でリセット(&R)"),(0,null),(Expand,"サブセット展開(&E)"));
            Menu("ツール(&T)",(Inspect,"エフェクトを逆アセンブル(&D)"),(0,null),(SkipValidation,"デバイス検証をスキップ(&S)"));
            skipValidation=File.Exists(Path.Combine(context.SettingsDirectory,"skip-device-validation"));MenuChecked(SkipValidation,skipValidation);
            Tabs(1002,"Main");Add(1004,"STATIC","Main Render Target");
            var list=Add(List,"SysListView32","",0x10009);ava_ui_list_setup(list,"Object","Effect File");
            void Button(int id,string label)=>Add(id,"BUTTON",label,0x10000);
            Button(Select,"ファイル選択");Button(Clear,"解除");Button(Visible,"非表示/表示");Add(1220,"BUTTON","選択項目の一括操作",7);
            Button(Update,"更新");Button(Ok,"OK");Button(Cancel,"キャンセル");
            Refresh();
        }
        internal void SceneChanged(MaterialSlot[] next,EffectSettings state)
        {
            OnWindow(()=>{
                var live=new HashSet<string>(next.SelectMany(s=>new[]{s.Id,s.ActorId}));
                var removed=slots.SelectMany(s=>new[]{s.Id,s.ActorId}).Where(id=>!live.Contains(id)).ToHashSet();
                slots=next;
                foreach(var snapshot in new[]{working,baseline}){
                    snapshot.rows.RemoveAll(row=>removed.Contains(row.slot));
                    snapshot.defaults=(state.defaults??new List<Assignment>()).Select(r=>r.Copy()).ToList();
                }
                expanded.RemoveWhere(removed.Contains);Refresh();
            });
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
            SetTabs(1002,targets.Select(t=>t.label).ToArray(),active);Text(1004,targets[active].description);
            var selected=Selected().Select(i=>rows[i].key).ToHashSet();
            refreshing=true;
            try {
                SendMessageW(Control(List),0x1009,IntPtr.Zero,IntPtr.Zero);rows.Clear();rows.Add(("(default)","(default)"));
                foreach(var actor in slots.GroupBy(s=>s.ActorId))
                {
                    rows.Add((actor.Key,actor.First().ActorName));
                    if(expanded.Contains(actor.Key))foreach(var slot in actor)rows.Add((slot.Id,"    "+slot.MaterialName));
                }
                for(int i=0;i<rows.Count;i++){
                    var entry=working.rows.Find(r=>r.slot==rows[i].key && (r.target??"Main")==target);
                    var supplied=working.defaults?.Find(r=>r.slot==rows[i].key && (r.target??"Main")==target);
                    ava_ui_list_row(Control(List),i,rows[i].name,entry?.mode=="effect"?entry.path:entry?.mode=="none"?"(none)":supplied?.path??"(default)",entry?.visible!=false);
                    if(selected.Contains(rows[i].key))ava_ui_select(Control(List),i);
                }
            }finally{refreshing=false;}
            EnableActions();Enable(Update,loading==null && !working.Encode().SequenceEqual(baseline.Encode()));
        }
        [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)]static extern void ava_ui_select(IntPtr list,int index);
        List<int> Selected()
        {
            var selected=new List<int>();int i=-1;
            while((i=SendMessageW(Control(List),0x100c,new IntPtr(i),new IntPtr(2)).ToInt32())>=0)
                if(i<rows.Count)selected.Add(i);
            return selected;
        }
        void EnableActions(){bool any=Selected().Count>0;foreach(int id in new[]{Select,Clear,Visible})Enable(id,any);}
        protected override void Layout(int w,int h)
        {
            Place(1002,11,11,w-22,h-100);Place(1004,30,46,w-59,27);Place(List,21,76,w-42,h-173);
            Place(1220,14,h-70,261,55);Place(Select,26,h-51,75,23);Place(Clear,107,h-51,75,23);Place(Visible,188,h-51,75,23);
            Place(Ok,w-245,h-30,75,23);Place(Cancel,w-164,h-30,75,23);Place(Update,w-86,h-30,75,23);
        }
        Assignment Edit(string id)
        {
            var a=working.rows.Find(r=>r.slot==id && (r.target??"Main")==target);
            if(a==null){a=new Assignment{slot=id,target=target,mode="inherit"};working.rows.Add(a);}return a;
        }
        void Publish(){var snapshot=working.Copy();Enable(Update,!working.Encode().SequenceEqual(baseline.Encode()));context.Post(()=>publish(snapshot));}
        void Assign(string file)
        {
            targets.Clear();targets.Add(("Main","Main","Main Render Target"));
            foreach(var a in working.rows.Concat(working.defaults??new List<Assignment>()).Where(r=>(r.target??"Main")=="Main" && r.mode=="effect" && r.targets!=null))
                foreach(var owner in slots.Where(s=>a.slot=="(default)" || a.slot==s.Id || a.slot==s.ActorId).Select(s=>s.ActorId).Distinct())
                    foreach(var t in a.targets){
                        string id=owner+"/"+a.slot+"/"+t.name;
                        if(!targets.Any(item=>item.id==id))targets.Add((id,t.name,t.description??t.name));
                    }
            int active=targets.FindIndex(t=>t.id==target);if(active<0){target="Main";active=0;}
            SetTabs(1002,targets.Select(t=>t.label).ToArray(),active);Text(1004,targets[active].description);
            var selected=Selected();if(file==null || selected.Count==0)return;
            CancelRead();
            var request=loading=new CancellationTokenSource();var token=request.Token;
            var keys=loadingSlots=selected.Select(i=>rows[i].key).ToArray();bool validate=!skipValidation;
            Enable(Ok,false);Enable(Update,false);Text(1004,"読み込み中　"+Path.GetFileName(file));
            var read=Task.Run(()=>{
                var loaded=new Assignment();EffectPlugin.ReadEffect(loaded,file,validate:validate,stop:token);
                return loaded;
            });
            lock(reads)reads.Add(read);
            read.ContinueWith(job=>{
                lock(reads)reads.Remove(read);
                var error=job.Exception?.GetBaseException();
                OnWindow(()=>{
                    if(loading!=request)return;
                    loading=null;request.Dispose();Enable(Ok,true);
                    if(error!=null){Refresh();Text(1004,error.Message);context.Post(()=>context.Report(error.Message));return;}
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
            Enable(Ok,true);Enable(Update,!working.Encode().SequenceEqual(baseline.Encode()));
        }
        internal async Task CloseAndDrain()
        {
            await CloseAsync().ConfigureAwait(false);
            Task[] pending;lock(reads)pending=reads.ToArray();
            try{await Task.WhenAll(pending).ConfigureAwait(false);}catch(Exception){foreach(var t in pending)_=t.Exception;}
        }
        protected override void Command(int id,int notification)
        {
            if(notification!=0)return;
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
                case Update:baseline=working.Copy();Publish();break;
                case Ok:accept=true;Publish();Close();break;
                case Cancel:Close();break;
                case ModelSave:case ModelLoad:ModelPreset(id==ModelSave);break;
                case Inspect:InspectShader();break;
                case SkipValidation:
                    skipValidation=!skipValidation;MenuChecked(SkipValidation,skipValidation);
                    Directory.CreateDirectory(context.SettingsDirectory);var flag=Path.Combine(context.SettingsDirectory,"skip-device-validation");
                    if(skipValidation)File.WriteAllText(flag,"1");else if(File.Exists(flag))File.Delete(flag);break;
                case Load:
                    string path=Pick("設定読込","*.avaemm");
                    if(path!=null){working=EffectSettings.Decode(File.ReadAllBytes(path));Refresh();Publish();}break;
                case Save:
                    string output=Pick("設定保存","*.avaemm",true);
                    if(output!=null)VmdDocument.AtomicFile(output,p=>File.WriteAllBytes(p,working.Encode()));break;
            }
        }
        protected override void Notify(int id,int code)
        {
            if(refreshing)return;
            if(id==1002 && code==-551){int t=SendMessageW(Control(1002),0x130b,IntPtr.Zero,IntPtr.Zero).ToInt32();if(t>=0 && t<targets.Count){CancelRead();target=targets[t].id;Refresh();}return;}
            if(id!=List)return;
            if(code==-5){Popup((Select,"ファイル選択"),(Clear,"解除"),(Visible,"非表示/表示"),(Reset,"default設定でリセット"),(0,null),(Expand,"サブセット展開"));return;}
            if(code==-3){Assign(Pick("エフェクト選択","*.fx"));return;}
            if(code!=-101)return;
            if(loading!=null && !Selected().Select(i=>rows[i].key).SequenceEqual(loadingSlots))CancelRead();
            EnableActions();bool changed=false;
            for(int i=0;i<rows.Count;i++)
            {
                bool visible=(SendMessageW(Control(List),0x102c,new IntPtr(i),new IntPtr(0xf000)).ToInt32()>>12)==2;
                if(visible!=(working.rows.Find(a=>a.slot==rows[i].key && (a.target??"Main")==target)?.visible??true)){Edit(rows[i].key).visible=visible;changed=true;}
            }
            if(changed)Publish();
        }
        protected override void Drop(IntPtr files)
        {
            try{var path=new StringBuilder(32768);if(DragQueryFileW(files,0,path,32768)>0)Assign(path.ToString());}finally{DragFinish(files);}
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
                VmdDocument.AtomicFile(path,tmp=>File.WriteAllBytes(tmp,preset.Encode()));
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
        public void ShowLog(string text)=>OnWindow(()=>Text(1004,string.IsNullOrEmpty(text)?"Main Render Target":text));
        protected override void Closing(){CancelRead();if(!accept){working=baseline.Copy();Publish();}Closed?.Invoke();}
    }
}
