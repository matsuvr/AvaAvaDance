using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        enum TimeDrag { None, Pan, Seek, Select, Move, Weight, Bookmark }
        TimeDrag timeDrag;
        Vector2 timeOrigin,timePoint;
        int scrollOrigin,rowOrigin,clickFrame;
        MotionTrack clickTrack;
        bool extendSelection;
        VisualElement contextLayer;

        TimeWindow dragWindow;
        int TimeAt(float x,TimeWindow window)=>Mathf.Clamp((window?.First??firstFrame)+Mathf.FloorToInt((x-NameWidth)/(window?.Spacing??FrameWidth)),0,MaxFrame);
        int RowAt(float y,TimeWindow window)=>(window?.Top??firstRow)+Mathf.FloorToInt(y/RowHeight)-1;
        void CancelTimelineDrag(TimeWindow window){if(dragWindow==window){timeDrag=TimeDrag.None;dragWindow=null;}}
        void CloseTimelineMenu(){contextLayer?.RemoveFromHierarchy();contextLayer=null;}
        void TimelineMenu(Vector2 world,bool row,VisualElement host=null)
        {
            CloseTimelineMenu();
            host??=MenuRoot;contextLayer=Box(host,"timeline-commands","mmd-menu-overlay");
            contextLayer.focusable=true;
            var list=Box(contextLayer,"commands","mmd-dropdown");
            list.style.left=Mathf.Clamp(world.x,0,Mathf.Max(0,host.contentRect.width-160));
            list.style.top=Mathf.Clamp(world.y,0,Mathf.Max(0,host.contentRect.height-180));list.style.width=160;
            void Add(string label,Action action,bool enabled=true)=>Action(list,label,enabled,()=>{CloseTimelineMenu();action();});
            if(row && selectedTrack?.SupportsLayers==true){Add("レイヤー追加",()=>EditLayer(true));Add("レイヤー設定",()=>EditLayer(false),selectedTrack.Target!=null);}
            Add("登録",RegisterSelection);Add("切り取り",CutKeys,EditKeys().Any());Add("コピー",CopyKey,EditKeys().Any());
            Add("貼り付け",()=>PasteOriginalKeys(false),CanPasteOriginal());Add("削除",DeleteKeys,EditKeys().Any());
            Add("ブックマーク",ToggleBookmark);
            contextLayer.RegisterCallback<PointerDownEvent>(e=>{if(e.target==contextLayer){CloseTimelineMenu();e.StopImmediatePropagation();}});
            contextLayer.Focus();
        }

        void RibbonTimelineDown(PointerDownEvent e,TimeWindow window=null)
        {
            if(timeDrag!=TimeDrag.None && dragWindow!=window)return;
            var board=window?.Board??timeline;
            if(e.button==3 || e.button==4){JumpKey(e.button==4);e.StopPropagation();return;}
            timeOrigin=timePoint=e.localPosition;scrollOrigin=window?.First??firstFrame;rowOrigin=window?.Top??firstRow;
            int row=RowAt(timePoint.y,window);var line=row>=0 && row<ribbonRows.Count?ribbonRows[row]:null;
            var track=line?.track;
            var at=TimeAt(timePoint.x,window); bool name=timePoint.x<NameWidth;
            if(line?.members!=null && e.button==0){ChooseGroup(line,name,at,name && timePoint.x<17);e.StopPropagation();return;}
            if(track!=null && (e.button==0 || e.button==1)){
                var layer=line.layer;
                var active=layer?.clips.Find(c=>c.id==track.ActiveClip);
                if(!name && layer!=null && (active==null || !layer.Map(active,at,out _,out _)))
                    active=layer.clips.LastOrDefault(c=>layer.Map(c,at,out _,out _));
                active??=layer?.clips.FirstOrDefault();
                if(track.ActiveLayer!=layer?.id || track.ActiveClip!=active?.id){
                    track.ActiveLayer=layer?.id;track.ActiveClip=active?.id;ChangeLayerContext();
                }
            }
            if(e.button==1)
            {
                if(track!=null && selectedTrack!=track)SelectTrack(track);
                TimelineMenu(e.position,name,window?.Root);e.StopPropagation();return;
            }
            board.Focus();
            if(e.button==2)timeDrag=TimeDrag.Pan;
            else if(e.button!=0)return;
            else if(timePoint.y<RowHeight){
                if(bookmarks.Contains(at) && timePoint.y<RowHeight*.5f){if(e.clickCount==2){EditBookmark(at);e.StopPropagation();return;}timeDrag=TimeDrag.Bookmark;clickFrame=at;}
                else timeDrag=TimeDrag.Seek;
                if(e.shiftKey)SeekWithDraft(at);else SetFrame(at);
            }
            else if(track==null)return;
            else if(name)
            {
                var old=new HashSet<MotionTrack>(selectedRows);var anchor=selectedTrack;
                if(e.shiftKey && anchor!=null)
                {
                    int first=ribbonRows.FindIndex(l=>l.track==anchor);
                    for(int i=Math.Max(0,Math.Min(first,row));i<=Math.Max(first,row);i++)if(ribbonRows[i].track!=null)old.Add(ribbonRows[i].track);
                }
                else if(e.ctrlKey){if(!old.Add(track))old.Remove(track);}
                else {old.Clear();old.Add(track);}
                SelectTrack(old.Contains(track)?track:VisibleTracks.LastOrDefault(old.Contains));
                selectedRows.Clear();selectedRows.UnionWith(old);
                boneSelection.AddRange(actor.bones.Where(b=>old.Contains(b.Track)));
                RefreshTimelineLabels();RedrawOverlay();e.StopPropagation();return;
            }
            else
            {
                bool hasTime=track.LocalTime(at,out int local);
                var before=selectedKeys.ToArray();
                int anchor=frame; int anchorRow=ribbonRows.FindIndex(l=>l.track==selectedTrack && l.layer==selectedTrack?.Target);
                bool hasKey=hasTime && track.EditKey(local)!=null;
                if(!e.ctrlKey && !e.shiftKey && hasKey && before.Contains((track,local)))extendSelection=true;
                else extendSelection=e.ctrlKey || e.shiftKey;
                if(selectedTrack!=track)SelectTrack(track);
                if(e.clickCount==2)
                {
                    if(frame!=at){if(e.shiftKey)SeekWithDraft(at);else SetFrame(at);}
                    RegisterKey(track);e.StopPropagation();return;
                }
                rangeSelected=true;selectedKeys.Clear();
                if(extendSelection)selectedKeys.UnionWith(before);
                if(hasKey)
                {
                    if(e.shiftKey && anchorRow>=0)AddRectangle(anchorRow,row,anchor,at);
                    else if(e.ctrlKey && selectedKeys.Contains((track,local)))selectedKeys.Remove((track,local));
                    else selectedKeys.Add((track,local));
                }
                clickTrack=track;clickFrame=local;
                timeDrag=hasKey && selectedKeys.Contains((track,local)) ? (track is MotionTrack<float> && !e.altKey ? TimeDrag.Weight:TimeDrag.Move) : TimeDrag.Select;
                RefreshKeyButtons();SyncCurve();board.MarkDirtyRepaint();
            }
            dragWindow=window;board.CapturePointer(e.pointerId);e.StopPropagation();
        }
        void AddRectangle(int row1,int row2,int first,int last)
        {
            for(int i=Math.Max(0,Math.Min(row1,row2));i<=Math.Min(TimelineRows-1,Math.Max(row1,row2));i++)
            {
                var t=ribbonProfile?ribbonRows[i].track:VisibleTracks[i];
                if(t==null || (ribbonProfile && ribbonRows[i].layer!=t.Target))continue;
                if(!t.LocalTime(Math.Min(first,last),out int lo) || !t.LocalTime(Math.Max(first,last),out int hi))continue;
                foreach(int f in t.EditFrames)if(f>=Math.Min(lo,hi) && f<=Math.Max(lo,hi))selectedKeys.Add((t,f));
            }
        }
        void RibbonTimelineMove(PointerMoveEvent e)=>RibbonTimelineMove(e,null);
        void RibbonTimelineMove(PointerMoveEvent e,TimeWindow window)
        {
            if(timeDrag==TimeDrag.None || window!=dragWindow)return;
            timePoint=e.localPosition;var delta=timePoint-timeOrigin;
            if(timeDrag==TimeDrag.Pan)
            {
                int row=rowOrigin-Mathf.RoundToInt(delta.y/RowHeight);
                int first=scrollOrigin-Mathf.RoundToInt(delta.x/(window?.Spacing??FrameWidth));
                if(window!=null)window.ScrollTo(first,row);else{firstRow=Mathf.Clamp(row,0,MaxFirstRow());SetFirstFrame(first);}
            }
            else if(timeDrag==TimeDrag.Seek && frame!=TimeAt(timePoint.x,window)){if(e.shiftKey)SeekWithDraft(TimeAt(timePoint.x,window));else SetFrame(TimeAt(timePoint.x,window));}
            (window?.Board??timeline).MarkDirtyRepaint();e.StopPropagation();
        }
        void RibbonTimelineUp(PointerUpEvent e)=>RibbonTimelineUp(e,null);
        void RibbonTimelineUp(PointerUpEvent e,TimeWindow window)
        {
            if(timeDrag==TimeDrag.None || window!=dragWindow)return;
            var board=window?.Board??timeline;
            timePoint=e.localPosition;
            if((timePoint-timeOrigin).sqrMagnitude>9)
            {
                if(timeDrag==TimeDrag.Select)
                {
                    if(!extendSelection)selectedKeys.Clear();rangeSelected=true;
                    AddRectangle(RowAt(timeOrigin.y,window),RowAt(timePoint.y,window),TimeAt(timeOrigin.x,window),TimeAt(timePoint.x,window));
                }
                else if(timeDrag==TimeDrag.Weight && clickTrack.KeyAt(clickFrame) is float weight)
                {
                    var value=Mathf.Clamp01(weight+(timeOrigin.y-timePoint.y)/100);
                    CommitKeys(new[]{new KeyChange{Track=clickTrack,Frame=clickFrame,After=clickTrack.WithCurve(clickFrame,value)}});
                }
                else if(timeDrag==TimeDrag.Bookmark)ChangeBookmark(clickFrame,TimeAt(timePoint.x,window),bookmarkMemos.TryGetValue(clickFrame,out var memo)?memo:null);
                else if(timeDrag==TimeDrag.Move)MoveSelectedKeys(TimeAt(timeOrigin.x,window),TimeAt(timePoint.x,window));
            }
            timeDrag=TimeDrag.None;dragWindow=null;board.ReleasePointer(e.pointerId);
            RefreshKeyButtons();board.MarkDirtyRepaint();timeline.MarkDirtyRepaint();e.StopPropagation();
        }
        void MoveSelectedKeys(int from,int to)
        {
            if(from==to || selectedKeys.Count==0)return;
            var offsets=new Dictionary<MotionTrack,int>();
            foreach(var track in selectedKeys.Select(k=>k.track).Distinct()){
                if(!track.LocalTime(from,out int a) || !track.LocalTime(to,out int b))return;
                offsets.Add(track,b-a);
            }
            if(offsets.Values.All(offset=>offset==0))return;
            var changes=new Dictionary<(MotionTrack track,int frame),KeyChange>();
            var destinations=new List<(MotionTrack track,int frame)>();
            foreach(var k in selectedKeys){
                int dest=k.frame+offsets[k.track];
                if(dest<0 || dest>MaxFrame || k.track.Target==null && (k.frame==0 || dest==0))return;
                changes[k]=new KeyChange{Track=k.track,Layer=k.track.ActiveLayer,Frame=k.frame,DocumentFrame=frame,ReplaceSource=true};
                destinations.Add((k.track,dest));
            }
            foreach(var k in selectedKeys){
                int dest=k.frame+offsets[k.track];
                changes[(k.track,dest)]=new KeyChange{Track=k.track,Layer=k.track.ActiveLayer,Frame=dest,
                    After=k.track.EditKey(k.frame),DocumentFrame=frame,ReplaceSource=true,
                    AfterSource=k.track.Target==null?k.track.SourceAt(k.frame):null};
            }
            CommitKeys(changes.Values);selectedKeys.Clear();selectedKeys.UnionWith(destinations);rangeSelected=true;
        }
        void DrawTimeDrag(Painter2D p,TimeWindow window)
        {
            if(timeDrag!=TimeDrag.Select || dragWindow!=window)return;
            var a=timeOrigin;var b=timePoint;var c=new Vector2(a.x,b.y);var d=new Vector2(b.x,a.y);
            Line(p,a,c,Color.cyan);Line(p,c,b,Color.cyan);Line(p,b,d,Color.cyan);Line(p,d,a,Color.cyan);
        }
    }
}
