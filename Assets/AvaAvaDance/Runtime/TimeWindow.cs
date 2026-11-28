using System;
using UnityEngine.UIElements;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        TimeWindow floatingTime,floatingWave;
        void ToggleTimeWindow(bool wave)
        {
            if (!NativeDesktopActive) { status.text = "独立ウィンドウはデスクトップ版で利用できます。"; return; }
            var current=wave?floatingWave:floatingTime;
            WorkspacePreferences.SetInt(wave?"Ribbon.AudioWindow":"Ribbon.TimeWindow",current?.IsOpen==true?0:1);
            PresentTimeWindows();
        }
        void PresentTimeWindows()
        {
            void Set(ref TimeWindow window,bool wave,string preference){
                bool show=NativeDesktopActive && ribbonProfile && WorkspacePreferences.GetInt(preference)==1;
                if(!show){var old=window;window=null;old?.Dispose();return;}
                if(window?.IsOpen==true)return;
                window=new TimeWindow(this,wave);window.OpenEditorWindow(wave?"オーディオ":"タイムライン",wave?660:800,wave?220:450);
            }
            Set(ref floatingTime,false,"Ribbon.TimeWindow");Set(ref floatingWave,true,"Ribbon.AudioWindow");
        }
        sealed class TimeWindow : FloatingPanel
        {
            readonly bool wave;
            readonly string pref;
            internal int First {get;private set;}
            internal int Top {get;private set;}
            internal float Spacing {get;private set;}
            internal VisualElement Board {get;private set;}
            internal VisualElement CurveBox {get;private set;}
            int columns=1,rows=1,lastRevision=-1;
            int shownFrame=-1;
            bool scrub;
            protected override bool Current=>app.ribbonProfile && (wave?app.floatingWave:app.floatingTime)==this;
            internal TimeWindow(DanceApp app,bool wave):base(app){
                this.wave=wave;pref=wave?"Ribbon.AudioBoard":"Ribbon.TimeBoard";
                First=WorkspacePreferences.GetInt(pref+".Frame");Top=WorkspacePreferences.GetInt(pref+".Row");
                Spacing=Mathf.Clamp(WorkspacePreferences.GetInt(pref+".Step",12),4,48);
            }
            protected override void Populate(VisualElement content){
                Board=Box(content,wave?"floating-wave":"floating-timeline","floating-board");Board.focusable=true;
                BindDrawing(Board, p => {
                    if(wave)DrawWave(p);else app.DrawTimeGrid(p,Board,First,Top,columns,rows,Spacing,this);
                });
                Board.RegisterCallback<GeometryChangedEvent>(e=>{if(e.newRect.size!=e.oldRect.size)lastRevision=-1;});
                Board.RegisterCallback<PointerDownEvent>(e=>{
                    if(!CanEdit)return;
                    if(!wave){app.RibbonTimelineDown(e,this);return;}
                    if(e.button!=0)return;Board.Focus();scrub=true;Board.CapturePointer(e.pointerId);Seek(e.localPosition.x,e.shiftKey);
                });
                Board.RegisterCallback<PointerMoveEvent>(e=>{
                    if(!CanEdit)return;
                    if(!wave)app.RibbonTimelineMove(e,this);else if(scrub)Seek(e.localPosition.x,e.shiftKey);
                });
                Board.RegisterCallback<PointerUpEvent>(e=>{
                    if(!wave && CanEdit)app.RibbonTimelineUp(e,this);
                    else if(scrub){scrub=false;Board.ReleasePointer(e.pointerId);}
                });
                if(!wave)CurveBox=app.CreateInlineCurve(content,this,()=>CanEdit);
                Board.RegisterCallback<PointerCaptureOutEvent>(_=>{scrub=false;app.CancelTimelineDrag(this);});
                Board.RegisterCallback<WheelEvent>(e=>{
                    int delta=Math.Sign(e.delta.y);
                    if(e.ctrlKey){Spacing=Mathf.Clamp(Spacing-2*delta,4,48);WorkspacePreferences.SetInt(pref+".Step",Mathf.RoundToInt(Spacing));lastRevision=-1;}
                    else if(wave || e.shiftKey)ScrollTo(First+4*delta,Top);else ScrollTo(First,Top+3*delta);
                    e.StopPropagation();
                });
                var bar=Box(content,"transport","row");
                void Tool(string name,Action run)=>Action(bar,name,true,()=>{if(CanEdit)run();});
                Tool("前のキー",()=>app.JumpKey(false));Tool("次のキー",()=>app.JumpKey(true));Tool("再生 / 停止",app.TogglePlayback);
                if(!wave){Tool("登録",app.RegisterSelection);Tool("元に戻す",()=>app.ApplyHistory(false));Tool("やり直し",()=>app.ApplyHistory(true));}
            }
            void Seek(float x,bool shift){
                int at=Mathf.Clamp(First+Mathf.FloorToInt(x/Spacing),0,MaxFrame);
                if(shift)app.SeekWithDraft(at);else app.SetFrame(at);
            }
            void DrawWave(DesktopPainter p){
                float h=Board.contentRect.height,w=Board.contentRect.width;
                float mid=(h+RowHeight)/2,scale=Mathf.Max(1,(h-RowHeight)*.46f);
                Line(p,new Vector2(0,mid),new Vector2(w,mid),new Color32(197, 198, 208, 255));
                for(int c=0;c<columns && c+First<app.peaks.Length;c++){
                    var amp=app.peaks[c+First];float x=(c+.5f)*Spacing;
                    Line(p,new Vector2(x,mid-amp.y*scale),new Vector2(x,mid-amp.x*scale),new Color32(63, 90, 169, 255));
                }
                float now=(app.frame-First+.5f)*Spacing;
                if(now>=0 && now<w)Line(p,new Vector2(now,0),new Vector2(now,h),new Color32(186, 26, 26, 255));
            }
            internal void FrameRange(int first, int last, int top, float spacing)
            {
                Spacing = spacing;
                int visible = Mathf.Max(1, Mathf.FloorToInt((Board.contentRect.width - app.NameWidth) / spacing));
                ScrollTo(first - (visible - (last - first + 1)) / 2, top);
            }
            internal void ScrollTo(int first,int top){
                First=Mathf.Clamp(first,0,MaxFrame);Top=Mathf.Clamp(top,0,Mathf.Max(0,app.ribbonRows.Count-rows));
                SavePosition();lastRevision=-1;app.RefreshCurveViews();
            }
            void SavePosition(){WorkspacePreferences.SetInt(pref+".Frame",First);WorkspacePreferences.SetInt(pref+".Row",Top);}
            protected override void Refresh(){
                if(Board.contentRect.width<=0 || Board.contentRect.height<=0)return;
                if(app.playing && (app.frame<First || app.frame>=First+columns))ScrollTo(app.frame-columns/2,Top);
                if(lastRevision==app.timelineRevision && shownFrame==app.frame)return;
                columns=Mathf.Clamp(Mathf.CeilToInt((Board.contentRect.width-(wave?0:app.NameWidth))/Spacing), 1, MaxFrame + 1);
                rows=Mathf.Clamp(Mathf.FloorToInt(Board.contentRect.height/RowHeight)-1,1,512);
                Top=Mathf.Clamp(Top,0,Mathf.Max(0,app.ribbonRows.Count-rows));
                Board.Clear();
                if(wave){
                    var ruler=Box(Board,"audio-ruler","ruler");ruler.pickingMode=PickingMode.Ignore;
                    for(int c=0;c<columns;c++)if((First+c)%5==0){
                        var label=new Label((First+c).ToString()){pickingMode=PickingMode.Ignore};label.style.left=c*Spacing;ruler.Add(label);
                    }
                    Board.MarkDirtyRepaint();
                }else app.LayoutTimeLabels(Board,First,Top,columns,rows,Spacing);
                lastRevision=app.timelineRevision;shownFrame=app.frame;
            }
            protected override void UserClosed(){
                app.CancelTimelineDrag(this);SavePosition();if(wave)app.floatingWave=null;else app.floatingTime=null;
                WorkspacePreferences.SetInt(wave?"Ribbon.AudioWindow":"Ribbon.TimeWindow",0);
            }
            public override void Dispose(){app.CancelTimelineDrag(this);SavePosition();base.Dispose();}
        }
    }
}
