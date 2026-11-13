using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        VisualElement transport,sharedBottom,legacySpacer,panelStrip;
        Toggle ribbonRepeat;
        TextField ribbonStart,ribbonEnd;
        SliderInt ribbonSeek;
        string transportStart="",transportEnd="";
        bool transportRepeat,showRibbonCurve;

        readonly List<(string id,string label,Button button)> transportDefaults=new List<(string,string,Button)>();
        readonly List<string> transportOrder=new List<string>();
        readonly HashSet<string> hiddenTransport=new HashSet<string>();

        void ArrangeTransport()
        {
            for(int i=0;i<transportOrder.Count;i++){
                var entry=transportDefaults.First(b=>b.id==transportOrder[i]);
                entry.button.RemoveFromHierarchy();transport.Insert(i,entry.button);
                entry.button.EnableInClassList("hidden",hiddenTransport.Contains(entry.id));
            }
        }
        void ConfigureTransport()
        {
            ShowDialog("コマンドバーの設定",out var body);
            var order=transportOrder.ToList();var hidden=new HashSet<string>(hiddenTransport);var list=new VisualElement();body.Add(list);
            void Draw(){
                list.Clear();
                foreach(var key in order.ToArray()){
                    var row=new VisualElement();row.style.flexDirection=FlexDirection.Row;list.Add(row);
                    var check=new Toggle(transportDefaults.First(b=>b.id==key).label){value=!hidden.Contains(key)};check.style.flexGrow=1;row.Add(check);
                    check.RegisterValueChangedCallback(e=>{if(e.newValue)hidden.Remove(key);else hidden.Add(key);});
                    void Move(int step){int index=order.IndexOf(key);order.RemoveAt(index);order.Insert(index+step,key);Draw();}
                    Action(row,"上へ",order.IndexOf(key)>0,()=>Move(-1)).SetEnabled(order.IndexOf(key)>0);
                    Action(row,"下へ",order.IndexOf(key)<order.Count-1,()=>Move(1)).SetEnabled(order.IndexOf(key)<order.Count-1);
                }
            }
            Draw();
            Action(body,"初期配置",true,()=>{order=transportDefaults.Select(b=>b.id).ToList();hidden.Clear();Draw();});
            Action(body,"OK",true,()=>{transportOrder.Clear();transportOrder.AddRange(order);hiddenTransport.Clear();hiddenTransport.UnionWith(hidden);
                WorkspacePreferences.SetString("Ribbon.Toolbar",string.Join(",",order));WorkspacePreferences.SetString("Ribbon.HiddenTools",string.Join(",",hidden));
                ArrangeTransport();CloseDialog();});
            Action(body,"キャンセル",true,CloseDialog);
        }
        void ResetRibbonLayout()
        {
            foreach(var key in new[]{"Ribbon.Toolbar","Ribbon.HiddenTools","Ribbon.Collapsed","Ribbon.ShowCurve","Ribbon.FrameWidth","Ribbon.DockTab","Ribbon.AudioWindow","Ribbon.TimeWindow","Ribbon.OperationWindow","Workspace.Ribbon.Row","Workspace.Ribbon.Frame"})
                WorkspacePreferences.DeleteKey(key);
            transportOrder.Clear();transportOrder.AddRange(transportDefaults.Select(b=>b.id));hiddenTransport.Clear();ArrangeTransport();
            ribbonCollapsed=false;showRibbonCurve=false;ribbonFrameWidth=12;firstRow=0;firstFrame=Mathf.Max(0,frame-VisibleColumns+1);
            SelectRibbonTab(0);PresentRibbonLayout(true);PresentTimeWindows();PresentOperationWindow();RefreshTimelineLabels();
        }
        void BuildTransport(VisualElement workspace,VisualElement bottom)
        {
            sharedBottom=bottom;legacySpacer=Box(bottom.parent,"panel-space","panel-space");
            transport=Box(workspace,"command-bar","ribbon-transport");
            void Tool(string id,string label,System.Action action,System.Func<bool> enabled=null)=>
                transportDefaults.Add((id,label,ProfileAction(transport,label,action,enabled)));
            Tool("model","モデル読込",PickPackage);Tool("register","登録",RegisterSelection,()=>selectedTrack!=null);
            Tool("start","|◀",()=>SetFrame(0));Tool("previous","◀",()=>JumpKey(false));
            Tool("play","再生 / 停止",TogglePlayback);Tool("next","▶",()=>JumpKey(true));
            var ids=transportDefaults.Select(b=>b.id).ToArray();
            transportOrder.AddRange(WorkspacePreferences.GetString("Ribbon.Toolbar").Split(',').Where(ids.Contains).Concat(ids).Distinct());
            hiddenTransport.UnionWith(WorkspacePreferences.GetString("Ribbon.HiddenTools").Split(',').Where(ids.Contains));
            ArrangeTransport();
            ribbonSeek=new SliderInt(0,100){tooltip="現在フレーム"};transport.Add(ribbonSeek);
            ribbonSeek.RegisterValueChangedCallback(e=>SetFrame(e.newValue));
            ribbonStart=new TextField{tooltip="再生開始"}; ribbonEnd=new TextField{tooltip="再生終了"};
            transport.Add(ribbonStart);transport.Add(new Label("～"));transport.Add(ribbonEnd);
            ribbonRepeat=new Toggle("くり返し");transport.Add(ribbonRepeat);
            workspace.Add(bottom);
            panelStrip=Box(bottom,"panel-targets","panel-targets");
            Action(panelStrip,"モデル",true,()=>{if(drawOrder.Count>0)ChooseMode((actor.avatar!=null?actor:drawOrder[0]).label);else PickPackage();});
            Action(panelStrip,"アクセサリ",true,()=>{ChangeMode(false);if(activeProp!=null)SelectTrack(activeProp.Track);else FileAction("アクセサリ読込","x;vac",false,LoadAccessory);});
            Action(panelStrip,"カメラ",true,()=>{ChangeMode(false);SelectTrack(cameraTrack);});
            Action(panelStrip,"照明",true,()=>{ChangeMode(false);SelectTrack(lightTrack);});
            showRibbonCurve=WorkspacePreferences.GetInt("Ribbon.ShowCurve")!=0;
            Action(panelStrip,"補間",true,()=>{showRibbonCurve=!showRibbonCurve;WorkspacePreferences.SetInt("Ribbon.ShowCurve",showRibbonCurve?1:0);PresentRibbonLayout(ribbonProfile);});
        }
        void BindTransport()
        {
            void BindText(TextField field,TextField other,bool start)
            {
                field.RegisterValueChangedCallback(e=>{
                    if(start)transportStart=e.newValue;else transportEnd=e.newValue;
                    other.SetValueWithoutNotify(e.newValue);
                });
            }
            BindText(playFrom,ribbonStart,true);BindText(ribbonStart,playFrom,true);
            BindText(playTo,ribbonEnd,false);BindText(ribbonEnd,playTo,false);
            repeat.RegisterValueChangedCallback(e=>{transportRepeat=e.newValue;ribbonRepeat.SetValueWithoutNotify(e.newValue);});
            ribbonRepeat.RegisterValueChangedCallback(e=>{transportRepeat=e.newValue;repeat.SetValueWithoutNotify(e.newValue);});
        }
        void PresentRibbonLayout(bool moving)
        {
            transport.EnableInClassList("hidden",!moving);panelStrip.EnableInClassList("hidden",!moving);
            legacySpacer.EnableInClassList("hidden",moving);
            sharedBottom.EnableInClassList("classic-bottom",!moving);
            var curve=MenuRoot.Q("interpolation");
            if(curve!=null){
                curve.EnableInClassList("hidden",moving && !showRibbonCurve);
                MenuRoot.Q("curve-heading").EnableInClassList("hidden",moving && !showRibbonCurve);
            }
            ribbonPalette?.EnableInClassList("hidden",!moving);
            PresentTimelineTab(moving);
            if(moving)SyncRibbonSeek();
        }
        void SyncRibbonSeek()
        {
            if(!ribbonProfile || ribbonSeek==null)return;
            int high=Mathf.Max(frame,LastFrame(),AudioLastFrame,100);
            if(ribbonSeek.highValue!=high)ribbonSeek.highValue=high;
            ribbonSeek.SetValueWithoutNotify(frame);
        }
    }
}
