using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public sealed class MotionPlugin : IAvaPlugin, IFileProcessor, IAsyncShutdown
    {
        MotionWindow window;
        IPluginContext host;
        public string[] Process(string requestFile,string outputDirectory,CancellationToken cancellation)
            =>SizingRequest.Run(host,requestFile,outputDirectory,cancellation);
        public void Register(IPluginRegistrar registrar) => registrar.AddCommand("ava.vmd-sizing.open","プラグイン(&E)","VMDサイジング(&S)",
            ()=>window.Open("VMDサイジング",600,650));
        public void Activate(IPluginContext context) {host=context;window=new MotionWindow(context);}
        public Task CloseAsync()=>window?.CloseAsync()??Task.CompletedTask;
        public void Deactivate(){window?.Dispose();host=null;}
        public void Dispose() {}
    }

    sealed partial class MotionWindow : ClassicWindow
    {
        const int Tab=10, Run=11, Check=12, Log=13, Progress=14, Extra=15;
        const uint EditStyle=0x50810080;
        static readonly string[] Pages={"ファイル","複数","モーフ","腕","足","カメラ","一括","CSV","VMD"};
        readonly IPluginContext context;
        readonly List<(int page,int id,string caption,string pattern)> fields=new List<(int,int,string,string)>();
        readonly List<(int page,int id)> checks=new List<(int,int)>();
        readonly List<int> addedSets=new List<int>();
        int groupOffset;
        CancellationTokenSource job;
        readonly List<Task> workers=new List<Task>();
        int page,width,height;
        bool ready;
        volatile int generation;
        public MotionWindow(IPluginContext context):base(context) {this.context=context;}
        protected override void Build()
        {
            ready=false;page=0; generation++; fields.Clear(); checks.Clear(); addedSets.Clear();groupOffset=0;
            Tabs(Tab,Pages); Add(Run,"BUTTON","実行",0x10000);Add(Check,"BUTTON","変換前チェック",0x10000);
            Add(Log,"EDIT","",0x50a10844);Add(Progress,"msctls_progress32","",8);Add(Extra,"BUTTON","",0x10000);
            Field(0,100,"調整対象モーションVMD/VPD","*.vmd;*.vpd");Field(0,110,"モーション作成元モデル","*.vrm;*.unitypackage;*.avarig");
            Field(0,120,"モーション変換先モデル","*.vrm;*.unitypackage;*.avarig"); Field(0,130,"出力VMD","*.vmd");
            BuildMappingTable();
            Tick(0,810,"スタンス追加補正");Tick(0,811,"捩り分散あり");
            Add(884,"BUTTON","接触回避剛体選択",0x10000);Add(885,"STATIC","");contactSelections.Clear();Tick(3,820,"接触回避");Tick(3,821,"位置合わせ");Tick(3,822,"指の位置で位置合わせを行う");
            Tick(3,823,"床との位置合わせも一緒に行う");Tick(3,824,"腕構造チェックをスキップ");
            Field(3,700,"手首間の距離",null);Text(700,"1.7");
            Field(3,710,"指間の距離",null);Text(710,"1.4");
            Field(3,720,"手首と床との距離",null);Text(720,"1.2");
            BuildLegControls();
            Field(5,430,"カメラモーションVMD","*.vmd");Field(5,440,"出力カメラVMD","*.vmd");
            Field(5,450,"距離可動範囲",null);Text(450,"5");BuildCameraControls();
            Field(5,460,"カメラ作成元モデル","*.vrm;*.unitypackage;*.avarig");Field(5,470,"全長Yオフセット",null);Text(470,"0");
            Tick(5,830,"カメラサイジングのみ実行");
            Field(6,480,"一括処理用CSV","*.csv");
            Field(4,400,"全体移動量補正（0.5 ～ 1.5）",null);Text(400,"1");
            Field(7,500,"VMDファイル","*.vmd");Field(8,600,"CSVファイル（ボーン）","*.csv");
            Field(8,610,"CSVファイル（モーフ）","*.csv");Field(8,620,"CSVファイル（カメラ）","*.csv");
            for(int i=0;i<3;i++)Add(890+i,"STATIC","（未設定）");
            Add(812,"BUTTON","＊",0x10000);
            Add(850,"BUTTON","インポート…",0x10000);Add(851,"BUTTON","エクスポート…",0x10000);Add(852,"BUTTON","一括用コピー",0x10000);
            stanceMask=247;autoName.Clear(); ready=true;Refresh();
        }
        void Field(int p,int id,string caption,string pattern)
        {
            fields.Add((p,id,caption,pattern));Add(id-1,"STATIC",caption);
            Add(id,"EDIT","",EditStyle); if(pattern!=null) {Add(id+1,"BUTTON","開く…",0x10000);if(!IsOutput(id))Add(id+2,"BUTTON","履歴",0x10000);}
        }
        void Tick(int p,int id,string caption){checks.Add((p,id));Add(id,"BUTTON",caption,0x10003);}
        bool Checked(int id)=>SendMessageW(Control(id),0xf0,IntPtr.Zero,IntPtr.Zero)!=IntPtr.Zero;
        double Value(int id,double min,double max)
        {
            double value=double.Parse(Text(id),NumberStyles.Float,CultureInfo.InvariantCulture);
            if(!(value>=min && value<=max))throw new FormatException("入力範囲: "+min+" ～ "+max);
            return value;
        }
        protected override void Layout(int w,int h)
        {
            width=w;height=h; Place(Tab,6,6,w-12,28);if(page==1){LayoutGroups(w,h);return;}if(page==2){LayoutMappings(w,h);return;}ScrollRange(0,Math.Max(1,h),0);int y=48;
            if(page==0){LayoutFiles(w,h);return;}
            if(page==4){LayoutLegs(w,h);return;}
            if(page==5){LayoutCamera(w,h);return;}
            foreach(var f in fields.Where(f=>f.page==page))
            {
                Place(f.id-1,14,y,w-28,20);Place(f.id,14,y+21,w-(f.pattern==null?28:IsOutput(f.id)?110:161),24);
                if(f.pattern!=null){Place(f.id+1,w-(IsOutput(f.id)?88:139),y+21,74,24);if(!IsOutput(f.id))Place(f.id+2,w-60,y+21,47,24);}y+=57;
            }
            foreach(var c in checks.Where(c=>c.page==page)){Place(c.id,14,y,w-28,21);y+=22;}
            if(page==3){Place(884,14,y,w/3,26);Place(885,w/3+22,y,w*2/3-36,60);y+=65;}
            Place(812,190,y-44,36,22);for(int i=0;i<3;i++)Place(850+i,14+i*130,y+6,125,28);
            Place(Check,14,y+6,150,40);Place(Run,page==0 || page==6?166:14,y+6,175,40);Place(Extra,page==6?345:200,y+6,140,40);y+=58;
            Place(Log,14,y,w-28,h-y-40);Place(Progress,14,h-32,w-28,18);
        }
        void LayoutFiles(int w,int h)
        {
            for(int i=0;i<4;i++){
                int id=100+10*i,y=29+63*i;Place(id-1,9,y,150,18);
                Place(id,9,y+30,w-(i==3?98:183),23);Place(id+1,w-(i==3?84:169),y+30,75,23);
                if(i<3){Place(id+2,w-84,y+30,75,23);Place(890+i,Math.Min(w-115,310),y,w-Math.Min(w-115,310)-9,17);}
            }
            Place(810,151,92,110,18);Place(812,267,92,21,21);Place(811,151,155,110,18);
            Place(Check,w/2-205,282,200,50);Place(Run,w/2+5,282,200,50);
            Place(Log,9,342,w-18,h-369);Place(Progress,9,h-20,w-18,15);
        }
        void Refresh()
        {
            foreach(var f in fields) {
                Show(f.id-1,f.page==page);Show(f.id,f.page==page);if(f.pattern!=null)Show(f.id+1,f.page==page);
            }
            for(int i=0;i<3;i++){Show(890+i,page==0);if(page==0)FileLabel(i);}
            ShowSizingControls();ShowMappings(page==2);Show(884,page==3);Show(885,page==3);
            Show(Log,page!=1);Show(Progress,page!=1);
            foreach(int first in addedSets){Show(first-4,page==1);Show(first+43,page==1);}
            Show(812,page==0);for(int i=0;i<3;i++)Show(850+i,page==2);
            foreach(var f in fields)if(f.pattern!=null && !IsOutput(f.id))Show(f.id+2,f.page==page);
            foreach(var c in checks)Show(c.id,c.page==page);
            Show(Check,page==0 || page==6);Show(Run,page==0 || page==1 || page>=6);
            Show(Extra,page==1 || page==6);Text(Extra,page==1?"ファイルセットクリア":"一括サイジング保存");
            Text(Check,page==6?"一括サイジング確認":"変換前チェック");
            Text(Run,page==0?"サイジング実行":page==1?"ファイルセット追加":page==6?"一括サイジング実行":page==7?"CSV変換実行":"VMD変換実行");
            Layout(width,height);
        }
        protected override void Notify(int id,int code)
        {
            if(id==Tab && code==-551){page=SendMessageW(Control(Tab),0x130b,IntPtr.Zero,IntPtr.Zero).ToInt32(); Refresh();if(page==3)CheckArmInput();}
        }
        void FileLabel(int index)
        {
            string path=PathField(100+index*10).Split('|')[0];
            Text(890+index,string.IsNullOrEmpty(path)?"（未設定）":Path.GetFileNameWithoutExtension(path));
        }
        string PathField(int id)=>Text(id).Trim().Trim('"').Trim();
        Person ReadPerson(int start)=>new Person{Motion=PathField(start),Source=PathField(start+10),Target=PathField(start+20),Output=PathField(start+30),AvoidShapes=contactSelections.TryGetValue(start,out var shapes)?shapes:null};
        protected override void Command(int id,int code)
        {
            if(!ready || MappingCommand(id,code) || SizingControlCommand(id,code))return;
            if(code==0x200 && PersonField(id,out int first,out int offset) && offset<=20){if(first==100)FileLabel(offset/10);CheckMappingSource(first);UpdateOutputName(first);return;}
            if(code!=0) return;
            if(id>=850 && id<=852){EditMappings(id);return;}
            if(id==884){ChooseContacts();return;}
            int set=addedSets.FirstOrDefault(start=>id==start+43);
            if(id==812 || set!=0){
                string[] partNames={"センターXZ補正","上半身補正","下半身補正","足IK補正","つま先補正","つま先IK補正","肩補正","センターY補正"};
                int selected=Choose("スタンス追加補正選択",partNames,set==0?stanceMask:ExtraStance(set),true);
                if(selected>=0){if(set==0)stanceMask=selected;else extraStance[set]=selected;SendMessageW(Control(set==0?810:set+40),0xf1,new IntPtr(selected!=0?1:0),IntPtr.Zero);UpdateOutputName(set==0?100:set);}
                return;
            }
            foreach(var f in fields)
            {
                if(f.pattern==null)continue;
                if(id==f.id+2 && !IsOutput(f.id)){var items=History(f.id);if(items.Length>0){int choice=Choose(f.caption,items);if(choice>=0)SelectPath(f.id,items[choice]);}return;}
                if(id!=f.id+1)continue;
                var latest=IsOutput(f.id)?null:History(f.id).FirstOrDefault();
                var result=Pick(f.caption,f.pattern,IsOutput(f.id) && (f.id==440 || !Checked(830)),string.IsNullOrEmpty(latest)?null:Path.GetDirectoryName(latest.Split('|')[0]));
                if(result!=null)SelectPath(f.id,result);return;
            }
            if(id==822 && Checked(id) && addedSets.Count>0 && !ConfirmFingers()){SendMessageW(Control(822),0xf1,IntPtr.Zero,IntPtr.Zero);return;}
            if(id==822 || id==823){if(Checked(id))SendMessageW(Control(821),0xf1,new IntPtr(1),IntPtr.Zero);return;}
            if(id!=Run && id!=Check && id!=Extra)return;
            if(job!=null){if(page==0)job.Cancel();return;}
            if(page==1)
            {
                if(id==Extra){foreach(var f in fields.Where(f=>f.page==1))Text(f.id,"");autoName.Clear();Text(Log,"");return;}
                AddPersonSet();return;
            }
            if(page<7)foreach(int person in MappingPeople())CheckPersonContent(person);
            var person1=ReadPerson(100);person1.MorphRules=MappingText(100);
            if(page<7 && (!double.TryParse(Text(400),NumberStyles.Float,CultureInfo.InvariantCulture,out person1.Movement) ||
                !(person1.Movement>=.5 && person1.Movement<=1.5))) {Text(Log,"全体移動量補正は0.5～1.5です。");return;}
            SizingOptions options;
            if(page>=7)options=null;else try {
                ApplyPersonOptions(person1,100);
                person1.Stance=Checked(810);person1.Twist=Checked(811);
                person1.StanceParts=Enumerable.Range(0,8).Select(i=>(stanceMask&(1<<i))!=0).ToArray();
                options=new SizingOptions{Camera=PathField(430),CameraOutput=PathField(440),CameraOnly=Checked(830),DistanceLimit=Value(450,1,5),
                    Avoid=Checked(820),Align=Checked(821),Fingers=Checked(822),Floor=Checked(823),SkipArmCheck=Checked(824),
                    WristDistance=Value(700,0,10),FingerDistance=Value(710,0,10),FloorDistance=Value(720,0,10)};
            }catch(Exception ex){Text(Log,ex.Message);return;}
            var people=new[]{person1}.Concat(addedSets.Select(firstId=>{
                var p=ReadPerson(firstId);ApplyPersonOptions(p,firstId);p.MorphRules=MappingText(firstId);p.Stance=Checked(firstId+40);p.Twist=Checked(firstId+41);p.StanceParts=Enumerable.Range(0,8).Select(i=>(ExtraStance(firstId)&(1<<i))!=0).ToArray();p.Movement=person1.Movement;return p;
            }).Where(p=>new[]{p.Motion,p.Source,p.Target,p.Output}.Any(s=>!string.IsNullOrWhiteSpace(s)))).ToArray();string batch=PathField(480);
            string vmd=PathField(500),bone=PathField(600),morph=PathField(610),camera=PathField(620);
            var cancellation=job=new CancellationTokenSource();var token=cancellation.Token;
            int version=generation,activePage=page;var timestamp=DateTime.Now;
            Text(Log,"");Enable(Tab,false);Enable(Check,false);Enable(Extra,false);
            foreach(var c in checks)Enable(c.id,false);
            Enable(812,false); foreach(var f in fields)if(f.pattern!=null && !IsOutput(f.id))Enable(f.id+2,false);
            foreach(var f in fields){Enable(f.id,false);if(f.pattern!=null)Enable(f.id+1,false);}
            Enable(Run,page==0);if(page==0)Text(Run,"処理停止");
            SendMessageW(Control(Progress),0x040a,new IntPtr(1),new IntPtr(40));
            var messages=new List<string>();
            workers.RemoveAll(t=>t.IsCompleted);
            workers.Add(Task.Run(()=>{
                try {
                    if(activePage==7) messages.AddRange(MotionCsv.Export(vmd,timestamp));
                    else if(activePage==8) messages.AddRange(MotionCsv.Import(bone,morph,camera,timestamp));
                    else if(activePage==6){if(id==Extra)messages.Add(SizingBatch.Save(people,options));else SizingBatch.Run(context,batch,id==Check,messages.Add,token);}
                    else Sizing.Run(context,people,id==Check,messages.Add,token,options);
                    messages.Add("完了");
                }catch(OperationCanceledException){messages.Add("停止しました。");}catch(Exception ex){messages.Add(ex.Message);}
                OnWindow(()=>{
                    if(version!=generation)return;
                    Text(Log,string.Join("\r\n",messages));job=null;
                    Enable(Tab,true);Enable(Check,true);Enable(Run,true);Enable(Extra,true);
                    foreach(var c in checks)Enable(c.id,true);
                    Enable(812,true); foreach(var f in fields)if(f.pattern!=null && !IsOutput(f.id))Enable(f.id+2,true);
                    foreach(var f in fields){Enable(f.id,true);if(f.pattern!=null)Enable(f.id+1,true);}
                    SendMessageW(Control(Progress),0x040a,IntPtr.Zero,IntPtr.Zero);Refresh();
                    cancellation.Dispose();
                });
            }));
        }
        protected override void Closing()
        {
            generation++; var cancellation=job;job=null;
            cancellation?.Cancel();
            if(workers.Count>0)Task.WaitAll(workers.ToArray());
            workers.Clear();cancellation?.Dispose();
        }
    }
}
