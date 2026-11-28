using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        VisualElement transport,sharedBottom;
        Toggle ribbonRepeat;
        SliderInt ribbonSeek;
        MinMaxSlider playbackLimits;
        Label playbackLastLabel;
        int playbackLimitMaximum = 100;
        Button transportPlayButton;
        VisualElement playheadRuler;
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
            ribbonCollapsed=false;showRibbonCurve=true;ribbonFrameWidth=9;dopeFitSpacing=0;firstRow=0;firstFrame=Mathf.Max(0,frame-VisibleColumns+1);
            SelectRibbonTab(2);ResetAnimationLayout();PresentRibbonLayout(true);PresentTimeWindows();PresentOperationWindow();RefreshTimelineLabels();
        }
        void BuildTransport(VisualElement workspace)
        {
            transport=Box(workspace,"transport","command-bar");
            void Tool(string id,string label,System.Action action,System.Func<bool> enabled=null)=>
                transportDefaults.Add((id,label,ProfileAction(transport,label,action,enabled)));
            Tool("model","モデル読込",PickPackage);Tool("register","登録",RegisterSelection,()=>selectedTrack!=null);
            Tool("start","|◀",()=>SetFrame(0));Tool("previous","‹ キー",()=>JumpKey(false));
            transportDefaults.Last().button.tooltip = "前の登録キー";
            Tool("play","▶ 再生",TogglePlayback);
            transportPlayButton = transportDefaults.Last().button;
            Tool("play-start","先頭から",PlayFromBeginning);
            Tool("play-bookmarks","マーカー間",PlayBetweenBookmarks);
            Tool("next","キー ›",()=>JumpKey(true));
            transportDefaults.Last().button.tooltip = "次の登録キー";
            var ids=transportDefaults.Select(b=>b.id).ToArray();
            transportOrder.AddRange(WorkspacePreferences.GetString("Ribbon.Toolbar").Split(',').Where(ids.Contains).Concat(ids).Distinct());
            hiddenTransport.UnionWith(WorkspacePreferences.GetString("Ribbon.HiddenTools").Split(',').Where(ids.Contains));
            ArrangeTransport();
            var seek = Box(workspace, "time-ruler", "time-ruler");
            ribbonSeek=new SliderInt(0,100){tooltip="現在フレーム"};seek.Add(ribbonSeek);
            playheadRuler = Box(seek, "time-ruler-ticks", "time-ruler-ticks");
            playheadRuler.pickingMode = PickingMode.Ignore;
            BindDrawing(playheadRuler, DrawTimeRuler);
            ribbonSeek.RegisterValueChangedCallback(e=>SetFrame(e.newValue));
            ribbonRepeat = new Toggle("Loop") { name = "transport-loop" }; transport.Add(ribbonRepeat);
            var autoKeyToggle = new Toggle("Auto Key") { name = "auto-key",
                tooltip = "編集した値を現在フレームに登録（初期値 OFF）" };
            autoKeyToggle.RegisterValueChangedCallback(e => {
                autoKey = e.newValue;
                autoKeyToggle.EnableInClassList("active-tool", autoKey);
            });
            transport.Add(autoKeyToggle);
            showRibbonCurve=WorkspacePreferences.GetInt("Workspace.InlineCurve",1)!=0;

        }
        void BindTransport()
        {
            playFrom.RegisterValueChangedCallback(e => { transportStart = e.newValue; SyncPlaybackLimits(); });
            playTo.RegisterValueChangedCallback(e => { transportEnd = e.newValue; SyncPlaybackLimits(); });
            playbackLimits.RegisterValueChangedCallback(e => {
                transportStart = Mathf.RoundToInt(e.newValue.x * playbackLimitMaximum).ToString();
                transportEnd = Mathf.RoundToInt(e.newValue.y * playbackLimitMaximum).ToString();
                playFrom.SetValueWithoutNotify(transportStart);
                playTo.SetValueWithoutNotify(transportEnd);
            });
            repeat.RegisterValueChangedCallback(e=>{transportRepeat=e.newValue;ribbonRepeat.SetValueWithoutNotify(e.newValue);});
            ribbonRepeat.RegisterValueChangedCallback(e=>{transportRepeat=e.newValue;repeat.SetValueWithoutNotify(e.newValue);});
        }

        void RestoreTransportSettings(string first, string last, bool loop)
        {
            transportStart = first ?? ""; transportEnd = last ?? ""; transportRepeat = loop;
            playFrom.SetValueWithoutNotify(transportStart); playTo.SetValueWithoutNotify(transportEnd);
            repeat.SetValueWithoutNotify(loop); ribbonRepeat.SetValueWithoutNotify(loop);
            SyncPlaybackLimits();
        }
        void PresentRibbonLayout(bool moving)
        {
            PresentTimelineTab(moving);
            SyncRibbonSeek();
        }
        void SyncRibbonSeek()
        {
            if (ribbonSeek == null || cameraTrack == null) return;
            int high = Mathf.Max(frame, PlaybackLastFrame, 100);
            if(ribbonSeek.highValue!=high)ribbonSeek.highValue=high;
            ribbonSeek.SetValueWithoutNotify(frame);
            playheadRuler.MarkDirtyRepaint();
            SyncPlaybackLimits();
        }
        void SyncPlaybackLimits()
        {
            if (playbackLimits == null || cameraTrack == null) return;
            int last = PlaybackLastFrame;
            bool validStart = PlaybackRange.TryLimit(transportStart, 0, out int first);
            bool validEnd = PlaybackRange.TryLimit(transportEnd, last, out int end);
            playbackLimitMaximum = Mathf.Max(100, last, validStart ? first : 0, validEnd ? end : 0);
            playbackLimits.SetValueWithoutNotify(new Vector2(
                (validStart ? first : 0) / (float)playbackLimitMaximum,
                (validEnd ? end : last) / (float)playbackLimitMaximum));
            playbackLastLabel.text = last.ToString();
        }

        void DrawTimeRuler(DesktopPainter pen)
        {
            float width = playheadRuler.contentRect.width;
            if (width <= 0) return;
            int end = Mathf.Max(1, ribbonSeek.highValue);
            float X(int at) => 6 + Mathf.Max(0, width - 12) * at / end;
            var tickColor = new Color32(136, 149, 163, 255);
            for (int i = 0; i <= 20; i++)
            {
                float x = X(end * i / 20);
                Line(pen, new Vector2(x, i % 5 == 0 ? 0 : 3), new Vector2(x, 7), tickColor);
            }
            if (selectedTrack != null)
            {
                float previous = float.NegativeInfinity;
                foreach (int key in TransportKeyFrames())
                {
                    float x = X(key);
                    if (x - previous < 1) continue;
                    previous = x;
                    Line(pen, new Vector2(x, 0), new Vector2(x, 8), new Color32(232, 129, 109, 255));
                }
            }
            float cursor = X(frame);
            Line(pen, new Vector2(cursor, 0), new Vector2(cursor, 10), new Color32(255, 219, 144, 255));
        }
    }
}
