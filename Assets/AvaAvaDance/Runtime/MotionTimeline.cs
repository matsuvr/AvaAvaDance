using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const int MaxFrame = PlaybackRange.FrameLimit, RowHeight = 19;
        int NameWidth => graphDockTab && timeline != null ? Mathf.Clamp(Mathf.RoundToInt(timeline.contentRect.width), 94, 156) : ribbonProfile ? Mathf.Clamp(Mathf.FloorToInt(timeline.contentRect.width * .34f), 94, 156) : 96;
        float FrameWidth => ribbonProfile ? (!graphDockTab && !audioDockTab && dopeFitSpacing > 0
            ? dopeFitSpacing * Mathf.Max(1, timeline.contentRect.width - NameWidth) / dopeFitWidth : ribbonFrameWidth) : 12;
        readonly List<MotionTrack> sceneTracks = new List<MotionTrack>();

        List<MotionTrack> VisibleTracks => modelMode ? actor.modelTracks : sceneTracks;
        MotionTrack selectedTrack, sceneSelection;
        readonly HashSet<(MotionTrack track, int frame)> selectedKeys = new HashSet<(MotionTrack, int)>();
        readonly List<KeyChange> keyClipboard = new List<KeyChange>();
        bool rangeSelected;
        readonly HashSet<MotionTrack> selectedRows = new HashSet<MotionTrack>();
        IntegerField rangeStart, rangeEnd;
        DropdownField rangeScope;
        int firstFrame, firstRow;
        SliderInt horizontal;
        Button undoButton, redoButton, copyButton, pasteButton, deleteButton, playButton;
        Toggle repeat;
        TextField playFrom, playTo;
        bool playing;
        int playStart, playEnd;
        PlaybackRange playbackRange;
        double startedAt;

        struct KeyChange
        {
            public MotionTrack Track;
            public int Frame;
            public object Before, After;
            public Action<MotionTrack,bool> Replay;
            public bool IsDraft;
            public string Layer,Clip,SourceId;
            public int? DocumentFrame;
            public object PreviousDraft;
            public int[] PreserveChannels;
            public object AfterDraft;
        }

        readonly Stack<KeyChange[]> undo = new Stack<KeyChange[]>();
        readonly Stack<KeyChange[]> redo = new Stack<KeyChange[]>();

        void BuildTimeline(VisualElement sidebar, VisualElement tools, VisualElement transportBar)
        {
            var history = Box(tools, "history", "row");
            undoButton = Action(history, "元に戻す", false, () => ApplyHistory(false));
            redoButton = Action(history, "やり直し", false, () => ApplyHistory(true));
            var heading = new Label("フレーム操作"){name="frame-heading"}; heading.AddToClassList("header"); tools.Add(heading);
            var row = Box(transportBar, "frame-row", "row", "frame-nav");
            Action(row, "|<", true, () => SetFrame(0)).tooltip = "先頭フレーム";
            Action(row, ".<", true, () => JumpKey(false)).tooltip = "前の登録キー";
            Action(row, "<", true, () => SetFrame(frame - 1));
            frameField = new IntegerField("Frame") { name = "current-frame", isDelayed = true, tooltip = "現在フレーム" };
            row.Add(frameField);
            frameField.RegisterValueChangedCallback(e => SetFrame(e.newValue));
            Action(row, ">", true, () => SetFrame(frame + 1));
            Action(row, ">.", true, () => JumpKey(true)).tooltip = "次の登録キー";
            Action(row, ">|", true, () => SetFrame(LastFrame())).tooltip = "最終登録フレーム";

            BuildWaveform(sidebar);
            timeline = Box(sidebar, "timeline", "timeline");
            timeline.focusable = true;
            BindDrawing(timeline, DrawTimeline);
            timeline.RegisterCallback<GeometryChangedEvent>(_ => RefreshTimelineLabels());
            timeline.RegisterCallback<PointerMoveEvent>(RibbonTimelineMove);
            timeline.RegisterCallback<PointerUpEvent>(RibbonTimelineUp);
            timeline.RegisterCallback<PointerCaptureOutEvent>(_=>CancelTimelineDrag(null));
            timeline.RegisterCallback<PointerDownEvent>(e =>
            {
                if(ribbonProfile){RibbonTimelineDown(e);return;}
                if (e.button != 0) return;
                timeline.Focus();
                int index = firstRow + Mathf.FloorToInt(e.localPosition.y / RowHeight) - 1;
                if (e.localPosition.x >= NameWidth) {
                    timeOrigin=timePoint=e.localPosition;
                    int at=TimeAt(timePoint.x,null);
                    if(timePoint.y<RowHeight) {
                        SetFrame(at);
                        timeDrag=TimeDrag.Seek;
                    } else {
                        var track=index>=0 && index<VisibleTracks.Count ? VisibleTracks[index] : null;
                        if(e.clickCount==2)SetFrame(at);
                        bool keyed=HasClassicKey(track,at,out int local);
                        extendSelection=e.shiftKey || e.ctrlKey;
                        if(!extendSelection && !(keyed && selectedKeys.Contains((track,local))))selectedKeys.Clear();
                        rangeSelected=true;
                        if(keyed && e.ctrlKey && selectedKeys.Contains((track,local)))selectedKeys.Remove((track,local));
                        else if(keyed)selectedKeys.Add((track,local));
                        timeDrag=keyed && selectedKeys.Contains((track,local))?TimeDrag.Move:TimeDrag.Select;
                        RefreshKeyButtons();timeline.MarkDirtyRepaint();
                    }
                    dragWindow=null;timeline.CapturePointer(e.pointerId);e.StopPropagation();return;
                }
                if (e.localPosition.y >= RowHeight && index >= 0 && index < VisibleTracks.Count) {
                    var row=VisibleTracks[index];
                    if(e.shiftKey && e.localPosition.x<NameWidth) {
                        var group=new HashSet<MotionTrack>(selectedRows);
                        if(!group.Add(row) && group.Count>1)group.Remove(row);
                        SelectTrack(group.Contains(row)?row:group.First());
                        selectedRows.UnionWith(group);RefreshTimelineLabels();
                    }else SelectTrack(row);
                }
                e.StopPropagation();
            });
            timeline.RegisterCallback<WheelEvent>(e =>
            {
                int delta = Math.Sign(e.delta.y);
                if (ribbonProfile && e.altKey)
                {
                    int column = Mathf.FloorToInt((e.localMousePosition.x - NameWidth) / FrameWidth);
                    int anchor = firstFrame + column;
                    if (dopeFitSpacing > 0 && !graphDockTab && !audioDockTab)
                    {
                        dopeFitSpacing = Mathf.Clamp(dopeFitSpacing * Mathf.Pow(1.35f, -delta), .000001f, 48);
                        SetFirstFrame(anchor - Mathf.FloorToInt((e.localMousePosition.x - NameWidth) / FrameWidth));
                        e.StopPropagation(); return;
                    }
                    ribbonFrameWidth = Mathf.Clamp(ribbonFrameWidth - delta * 2, 4, 48);
                    WorkspacePreferences.SetInt("Ribbon.FrameWidth", ribbonFrameWidth);
                    SetFirstFrame(anchor - Mathf.FloorToInt((e.localMousePosition.x - NameWidth) / FrameWidth));
                }
                else if (e.shiftKey) SetFirstFrame(firstFrame + 5 * delta);
                else { firstRow = Mathf.Clamp(firstRow + delta * 3, 0, MaxFirstRow()); RefreshTimelineLabels(); }
                e.StopPropagation();
            });
            var scroll = Box(sidebar, "time-scroll", "row");
            Action(scroll, "現フレーム", true, () => SetFirstFrame(frame - VisibleColumns / 2));
            horizontal = new SliderInt(0, MaxFrame) { value = 0 };
            scroll.Add(horizontal);
            horizontal.RegisterValueChangedCallback(e => SetFirstFrame(e.newValue));
            var edit = Box(tools, "key-edit", "row");
            copyButton = Action(edit, "コピー", false, CopyKey);
            pasteButton = Action(edit, "ペースト", false, () => PasteOriginalKeys(false));
            Action(edit, "縦選択", true, () => SelectKeys(frame, frame, true));
            deleteButton = Action(edit, "削除", false, DeleteKeys);
            var range = Box(tools, "key-range", "row");
            rangeStart = new IntegerField { isDelayed = true, tooltip = "選択開始フレーム" };
            rangeEnd = new IntegerField { isDelayed = true, tooltip = "選択終了フレーム" };
            range.Add(rangeStart); range.Add(new Label("～")); range.Add(rangeEnd);
            var scope = Box(tools, "key-scope", "row");
            rangeScope = Choice(scope, new[] { "選択行", "全ての行" });
            Action(scope, "範囲選択", true, () => SelectKeys(rangeStart.value, rangeEnd.value, rangeScope.index == 1));
        }

        int VisibleColumns
        {
            get
            {
                float width = graphDockTab && graphCanvas != null
                    ? graphCanvas.contentRect.width
                    : timeline.contentRect.width - NameWidth;
                return Mathf.Clamp(Mathf.FloorToInt(width / FrameWidth), 1, MaxFrame + 1);
            }
        }
        int VisibleRows => Mathf.Max(1, Mathf.FloorToInt(timeline.contentRect.height / RowHeight) - 1);
        int MaxFirstRow() => Mathf.Max(0, TimelineRows - VisibleRows);

        void SetFirstFrame(int value)
        {
            firstFrame = Mathf.Clamp(value, 0, Mathf.Max(0, MaxFrame - VisibleColumns + 1));
            horizontal?.SetValueWithoutNotify(firstFrame);
            RefreshTimelineLabels();
        }

        void SetFrame(int value)
        {
            StopPlayback();
            if(ribbonProfile){selectedKeys.Clear();rangeSelected=false;}
            Seek(value);
            if (scrubSound) PlayAudioAt(frame, true);
        }

        void Seek(int value)
        {
            int next = Mathf.Clamp(value, 0, MaxFrame);
            if (next != frame) gestureBefore = null;
            frame = next;
            frameField.SetValueWithoutNotify(frame);SyncRibbonSeek();
            UpdateViewInfo();
            EvaluateAnimation(frame, playing);
            if (frame < firstFrame || frame >= firstFrame + VisibleColumns)
                SetFirstFrame(frame / VisibleColumns * VisibleColumns);
            SyncControls();
            RefreshKeyButtons();
            timeline.MarkDirtyRepaint();
            RedrawOverlay();
        }

        void SelectTrack(MotionTrack track)
        {
            selectedKeys.Clear(); rangeSelected = false;
            boneSelection.Clear(); selectedRows.Clear();if(track!=null)selectedRows.Add(track); selectedTrack = track;
            if (track is MotionTrack<AccessoryPose>) activeProp=props.Find(p=>p.Track==track);
            if (modelMode) actor.modelSelection = track;
            if (modelMode && track is MotionTrack<BonePose>)
                actor.selectedBone = actor.bones.Find(b => b.Track == track);
            if (!modelMode) sceneSelection = track;
            int index = ribbonProfile ? ribbonRows.FindIndex(row=>track!=null && row.track==track && row.layer==track.Target) : VisibleTracks.IndexOf(track);
            if (index >= 0 && (index < firstRow || index >= firstRow + VisibleRows))
                firstRow = Mathf.Clamp(index, 0, MaxFirstRow());
            if (viewportTitle != null)
                viewportTitle.text = (modelMode ? "persp  ·  " + actor.avatar.name : "Camera") + (track == null ? "" : "  ·  " + track.Name);
            SyncControls();
            RefreshKeyButtons();
            SyncLayerSelector();
            RefreshTimelineLabels();
            RedrawOverlay();
        }

        int LastFrame()
        {
            int last = 0;
            foreach (var track in sceneTracks) last = Mathf.Max(last, track.LastFrame);
            foreach (var item in drawOrder) foreach(var track in item.modelTracks) last=Mathf.Max(last,track.LastFrame);
            return last;
        }

        void JumpKey(bool forward)
        {
            var keys = TransportKeyFrames();
            int index = Array.BinarySearch(keys, frame);
            index = index < 0 ? ~index - (forward ? 0 : 1) : index + (forward ? 1 : -1);
            if (index >= 0 && index < keys.Length) SetFrame(keys[index]);
        }

        void SelectKeys(int start, int end, bool allTracks)
        {
            StopPlayback();
            selectedKeys.Clear(); rangeSelected = true;
            foreach (var track in VisibleTracks)
            {
                if (!allTracks && track != selectedTrack) continue;
                foreach (int key in track.EditFrames.Between(start, end)) selectedKeys.Add((track, key));
            }
            RefreshKeyButtons(); timeline.MarkDirtyRepaint();
            status.text = selectedKeys.Count + " 個のキーを選択";
        }

        IEnumerable<(MotionTrack track, int frame)> EditKeys()
        {
            if (rangeSelected) return selectedKeys;
            return selectedTrack!=null && selectedTrack.LocalTime(frame,out int local) && selectedTrack.EditKey(local)!=null ? new[] { (selectedTrack, local) } :
                Array.Empty<(MotionTrack, int)>();
        }

        void CopyKey()
        {
            keyClipboard.Clear();
            foreach (var key in EditKeys().OrderBy(k => k.frame))
                keyClipboard.Add(new KeyChange { Track = key.track, Frame = key.frame, After = key.track.EditKey(key.frame) });
            RefreshKeyButtons();
        }

        bool CanPaste()
        {
            if(keyClipboard.Count==0)return false;
            bool one=keyClipboard.All(k=>k.Track==keyClipboard[0].Track);
            if(one && (selectedTrack==null || selectedTrack.GetType()!=keyClipboard[0].Track.GetType()))return false;
            return keyClipboard.All(k=>{
                var target=one?selectedTrack:k.Track;
                return (one || VisibleTracks.Contains(target)) && target.LocalTime(frame,out int local) &&
                    local+k.Frame-keyClipboard[0].Frame<=MaxFrame;
            });
        }

        void PasteKeys()
        {
            if (!CanPaste()) return;
            bool oneTrack = keyClipboard.All(k => k.Track == keyClipboard[0].Track);
            var edits=keyClipboard.Select(k=>{
                var target=oneTrack?selectedTrack:k.Track;target.LocalTime(frame,out int local);
                return new KeyChange{Track=target,Layer=target.ActiveLayer,Frame=local+k.Frame-keyClipboard[0].Frame,
                    DocumentFrame=frame,After=k.After};
            });
            CommitKeys(edits);
        }

        void DeleteKeys()
        {
            CommitKeys(EditKeys().Where(k => k.frame > 0 || k.track.Target!=null).Select(k => new KeyChange { Track = k.track, Layer=k.track.ActiveLayer, Frame = k.frame, DocumentFrame=frame }));
        }

        void CommitKeys(IEnumerable<KeyChange> edits, IEnumerable<KeyChange> draftEdits = null)
        {
            var changes = new List<KeyChange>();
            foreach (var edit in edits)
            {
                var change = edit;
                var layer=change.Layer==null?null:change.Track.Layers.Find(l=>l.id==change.Layer);
                if(layer!=null){
                    change.Clip=change.Track.ActiveClip;change.SourceId=layer.SourceFor(change.Clip).id;
                }
                change.Before=layer==null?change.Track.SnapshotKey(change.Frame):
                    layer.Source(change.SourceId).At(change.Frame)?.Into(change.Track.CurrentValue,true);
                if (!Equals(change.Before, change.After)) changes.Add(change);
            }
            if (draftEdits != null) changes.AddRange(draftEdits);
            if (changes.Count == 0) return;
            StopPlayback();
            foreach (var c in changes.Where(c => !c.IsDraft)) ApplyKeyChange(c,c.After);
            selectedKeys.Clear(); rangeSelected = false;
            var affected=InvalidateChangedActors(changes);
            foreach (var track in changes.Where(c => !c.IsDraft).Select(c => c.Track).Distinct()) track.Seek(frame);
            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.PreserveChannels == null) continue;
                var track = change.Track;
                change.AfterDraft = MergeChannels(change.PreviousDraft, track.CurrentValue, change.PreserveChannels);
                track.Restore(change.AfterDraft);
                changes[i] = change;
            }
            undo.Push(changes.ToArray()); redo.Clear();
            ForEachActor(affected,RefreshRigPreview); SyncControls(); SyncRibbonSeek(); RefreshKeyButtons(); timeline.MarkDirtyRepaint(); RedrawOverlay();
        }

        static void ApplyKeyChange(KeyChange c,object value)
        {
            if(c.Layer==null)c.Track.Write(c.Frame,value);
            else {c.Track.Revision++;c.Track.Layers.Find(l=>l.id==c.Layer).Source(c.SourceId).Write(c.Frame,value);}
        }

        void RegisterKey(MotionTrack track)
        {
            if (track == null) return;
            CommitKeys(RegistrationEdits(track)); SelectTrack(track);
        }

        void WriteKey(MotionTrack track, object value)
        {
            if (track == null || (value == null && frame == 0)) return;
            if (!track.LocalTime(frame,out int local)) return;
            CommitKeys(new[] { new KeyChange { Track = track, Layer=track.ActiveLayer, Frame=local, DocumentFrame=frame, After=value } });
            SelectTrack(track);
        }

        void ApplyHistory(bool forward)
        {
            var source = forward ? redo : undo;
            if (source.Count == 0) return;
            var changes = source.Pop();
            (forward ? undo : redo).Push(changes);
            if (changes[0].Replay != null) { changes[0].Replay(changes[0].Track,forward); RefreshKeyButtons(); return; }
            foreach (var change in changes.Where(c=>!c.IsDraft)) ApplyKeyChange(change, forward ? change.After : change.Before);
            InvalidateChangedActors(changes);
            var first = changes[0];
            int destination=first.DocumentFrame??first.Frame;
            var held=destination==frame?EditableTracks().Select(entry=>entry.track).Where(t=>!changes.Any(c=>c.Track==t) && t.HasDraft(frame))
                .ToDictionary(t=>t,t=>t.CurrentValue):null;
            var owner=drawOrder.Find(item=>item.modelTracks.Contains(first.Track));
            if(owner!=null && owner!=actor){actor=owner;RefreshActorUI();} ChangeMode(owner!=null);
            SelectTrack(first.Track);
            if(!first.IsDraft){first.Track.ActiveLayer=first.Layer;first.Track.ActiveClip=first.Clip;}
            SetFrame(destination);
            if(held!=null)foreach(var draft in held)draft.Key.Restore(draft.Value);
            foreach (var change in changes)
            {
                if (change.IsDraft) change.Track.Restore(forward ? change.After : change.Before);
                else if (!forward && change.PreviousDraft != null) change.Track.Restore(change.PreviousDraft);
                else if (forward && change.AfterDraft != null) change.Track.Restore(change.AfterDraft);
            }
            ForEachActor(calculationOrder,RefreshRigPreview); SyncControls(); RedrawOverlay();
        }

        void RefreshKeyButtons()
        {
            copyButton.SetEnabled(EditKeys().Any());
            pasteButton.SetEnabled(CanPasteOriginal());
            deleteButton.SetEnabled(EditKeys().Any(k => k.frame > 0 || k.track.Target!=null));
            undoButton.SetEnabled(undo.Count != 0);
            redoButton.SetEnabled(redo.Count != 0);
        }

        void RefreshTimelineLabels()
        {
            timelineRevision++;if(ribbonProfile)BuildRibbonRows();RefreshCurveViews();
            playheadRuler?.MarkDirtyRepaint(); RefreshOutliner();
            waveform?.MarkDirtyRepaint();
            timeline.Clear();
            if (!(timeline.contentRect.height > 0) || !(timeline.contentRect.width > 0)) return;
            firstRow = Mathf.Clamp(firstRow, 0, MaxFirstRow());
            LayoutTimeLabels(timeline,firstFrame,firstRow,VisibleColumns,VisibleRows,FrameWidth);
        }
        int timelineRevision;
        void LayoutTimeLabels(VisualElement board,int first,int top,int columns,int rows,float spacing)
        {
            var ruler = Box(board, "ruler", "ruler");
            ruler.pickingMode = PickingMode.Ignore;
            int step = TimelineTickStep(spacing);
            for (int i = 0; (board != timeline || !graphDockTab) && i < columns && first + i <= MaxFrame;
                i += step - (first + i) % step)
            {
                int n = first + i;
                var tick = new Label(n.ToString()) { pickingMode = PickingMode.Ignore };
                tick.style.left = NameWidth + i * spacing;
                ruler.Add(tick);
            }
            for (int i = top; i < Mathf.Min(TimelineRows, top + rows); ++i)
            {
                var line=ribbonProfile?ribbonRows[i]:null;
                var track = line==null?VisibleTracks[i]:line.track;
                var label = new Label(line?.label??DisplayName(track)) { tooltip = track?.Name??line?.label, pickingMode = PickingMode.Ignore };
                label.AddToClassList("track-name");
                label.EnableInClassList("layer-group", line?.layerGroup==true);
                label.style.width = NameWidth;
                label.EnableInClassList("selected-track", track!=null && selectedRows.Contains(track) && (line==null || line.layer==track.Target));
                board.Add(label);
            }
            board.MarkDirtyRepaint();
        }

        static bool HasClassicKey(MotionTrack track,int at,out int local)
        {
            local=at;
            if(track==null)return false;
            if(string.IsNullOrEmpty(track.Target?.group))return track.KeyAt(at)!=null;
            return track.LocalTime(at,out local) && track.EditKey(local)!=null;
        }

        void DrawTimeline(DesktopPainter p)
        {
            if (!graphDockTab) DrawTimeGrid(p, timeline, firstFrame, firstRow, VisibleColumns, VisibleRows, FrameWidth, null);
        }
        void DrawTimeGrid(DesktopPainter p,VisualElement board,int first,int top,int columns,int rows,float spacing,TimeWindow window)
        {
            float w = board.contentRect.width, h = board.contentRect.height;
            if(ribbonProfile)h=Mathf.Min(h,(TimelineRows-top+1)*RowHeight);

            var grid = new Color32(56, 60, 65, 255);
            var majorGrid = new Color32(79, 84, 92, 255);
            var bookmarkColor = new Color32(231, 166, 91, 255);
            var cursorColor = new Color32(238, 100, 107, 255);
            var keyColor = new Color32(222, 117, 96, 255);
            var selectedKeyColor = new Color32(255, 215, 124, 255);
            var disabledKeyColor = new Color32(143, 151, 164, 255);
            for (int row = 0; row < rows && (row + 1) * RowHeight < h; row++)
            {
                var item = top + row < TimelineRows && ribbonProfile ? ribbonRows[top + row] : null;
                p.fillColor = item?.track == selectedTrack && selectedTrack != null ? new Color32(51, 68, 83, 255) :
                    row % 2 == 0 ? new Color32(43, 46, 51, 255) : new Color32(49, 52, 58, 255);
                float y = (row + 1) * RowHeight;
                p.BeginPath(); p.MoveTo(new Vector2(NameWidth, y)); p.LineTo(new Vector2(w, y));
                p.LineTo(new Vector2(w, y + RowHeight)); p.LineTo(new Vector2(NameWidth, y + RowHeight)); p.ClosePath(); p.Fill();
            }
            int step = TimelineTickStep(spacing);
            int minorStep = spacing >= 4 ? 1 : step;
            for (int i = (minorStep - first % minorStep) % minorStep; i < columns; i += minorStep)
            {
                float x = NameWidth + (i + .5f) * spacing;
                Line(p, new Vector2(x, RowHeight), new Vector2(x,h), (first+i)%step==0 ? majorGrid : grid);
            }
            for (int y = RowHeight; y < h; y += RowHeight)
                Line(p, new Vector2(0,y),new Vector2(w,y),grid);
            foreach (int mark in bookmarks.GetViewBetween(first, first + columns))
            {
                float x = NameWidth + (mark - first + .5f) * spacing;
                Line(p, new Vector2(x, 0), new Vector2(x, RowHeight), bookmarkColor);
            }
            float cursor = NameWidth + (frame - first + .5f) * spacing;
            if (cursor >= NameWidth && cursor < w)
            {
                p.strokeColor = cursorColor; p.lineWidth = 2;
                p.BeginPath(); p.MoveTo(new Vector2(cursor, RowHeight)); p.LineTo(new Vector2(cursor, h)); p.Stroke();
            }
            for (int i = top; i < Mathf.Min(TimelineRows, top + rows); ++i)
            {
                var line=ribbonProfile?ribbonRows[i]:null;
                var track=line==null?VisibleTracks[i]:line.track;
                float y = (i - top + 1.5f) * RowHeight;
                int[] selected = track != null && (line == null || line.layer == track.Target)
                    ? selectedKeys.Where(key => key.track == track).Select(key => key.frame).ToArray() : Array.Empty<int>();
                int end = Mathf.Min(MaxFrame, first + columns - 1);
                var positions = TimelineRowKeys(line, track, first, end);
                int Local(int at)
                {
                    if (line?.layer == null) return at;
                    return line.layer == track.Target && track.LocalTime(at, out int local) ? local : -1;
                }
                float X(int at) => NameWidth + (at - first + .5f) * spacing;
                if (selected.Length > 0)
                {
                    var span = line?.layer?.clips.Count > 0 ? positions.Where(at => selectedKeys.Contains((track, Local(at)))).ToArray() : selected;
                    if (span.Length > 0) {
                        float left = X(span.Min()) - 4, right = X(span.Max()) + 4;
                        DrawSelectedKeySpan(p, w, y, left, right);
                        float shift = TimeMovePreview(window) * spacing;
                        if (shift != 0) DrawSelectedKeySpan(p, w, y, left + shift, right + shift);
                    }
                }
                float previous = float.NegativeInfinity;
                foreach (int at in positions)
                {
                    int local = Local(at);
                    float x = X(at);
                    bool picked = selectedKeys.Contains((track, local)) && (line == null || line.layer == track?.Target);
                    if (!picked && x - previous < 3) continue;
                    previous = x;
                    p.fillColor = (picked || (!rangeSelected && track == selectedTrack && frame == at)) ? selectedKeyColor : keyColor;
                    if (track?.KeyAt(at) is BonePose bp && !bp.PhysicsOff && actor.dynamicLinks.Any(d=>d.Bone.Track==track)) {
                        Line(p,new Vector2(x-4,y-4),new Vector2(x+4,y+4),p.fillColor);
                        Line(p,new Vector2(x-4,y+4),new Vector2(x+4,y-4),p.fillColor);
                        continue;
                    }
                    if(track!=null && !track.EnabledKey(local))p.fillColor=disabledKeyColor;
                    p.BeginPath(); p.MoveTo(new Vector2(x,y-5)); p.LineTo(new Vector2(x+5,y));
                    p.LineTo(new Vector2(x,y+5)); p.LineTo(new Vector2(x-5,y)); p.ClosePath(); p.Fill();
                }
            }
            DrawTimeDrag(p, window);
        }

        IEnumerable<MotionTrack> GroupTracks(bool secondary) => VisibleTracks.Where(t =>
            modelMode ? (secondary ? t is MotionTrack<float> : t is MotionTrack<BonePose>) :
                (secondary ? t==lightTrack : t==cameraTrack));

        void SelectGroup(bool secondary)
        {
            selectedKeys.Clear(); rangeSelected = true;
            foreach (var track in GroupTracks(secondary))
                foreach (int f in string.IsNullOrEmpty(track.Target?.group) ? new KeyFrames(track.Frames) : track.EditFrames) selectedKeys.Add((track, f));
            RefreshKeyButtons(); timeline.MarkDirtyRepaint();
            status.text = selectedKeys.Count + " 個のキーを選択";
        }

        void ShiftColumn(bool secondary, bool insert, IEnumerable<MotionTrack> targets = null)
        {
            if (frame == 0 && !insert) { status.text = "先頭フレームは削除できません。"; return; }
            var edits = new List<KeyChange>();
            foreach (var track in targets ?? GroupTracks(secondary))
            {
                if(!track.LocalTime(frame,out int local))continue;
                int start=Math.Max(track.Target==null?1:0,local);
                var keys = track.EditFrames;
                if (insert && keys.Count > 0 && keys[keys.Count - 1] == MaxFrame) { status.text = "移動先がフレーム上限を超えます。"; return; }
                var result = new Dictionary<int, object>();
                foreach (int f in keys.Between(start, MaxFrame)) result[f] = null;
                foreach (int f in keys.Between(insert ? start : start + 1, MaxFrame))
                    result[f + (insert ? 1 : -1)] = track.EditKey(f);
                foreach (var entry in result)
                    edits.Add(new KeyChange { Track = track, Layer=track.ActiveLayer, Frame = entry.Key, DocumentFrame=frame, After = entry.Value});
            }
            CommitKeys(edits);
        }

        void BuildPlayback(VisualElement parent)
        {
            var heading = new Label("再生"); heading.AddToClassList("header"); parent.Add(heading);
            var buttons = Box(parent, "play-actions", "row");
            playButton = transportPlayButton;
            repeat = new Toggle("くり返し"); buttons.Add(repeat);
            var range = Box(parent, "playback-range", "row");
            playFrom = new TextField { tooltip = "再生開始フレーム（両欄が空欄なら現在位置から再生）" };
            playTo = new TextField { tooltip = "終了フレーム（空欄は最後の登録キー）" };
            range.Add(new Label("0")); range.Add(playFrom);
            playbackLimits = new MinMaxSlider(0, 1, 0, 1) { name = "range-slider", tooltip = "再生範囲をドラッグして指定" };
            range.Add(playbackLimits);
            range.Add(playTo); playbackLastLabel = new Label("0"); range.Add(playbackLastLabel);
            Action(parent, "現フレーム", true, () => SetFirstFrame(frame - VisibleColumns / 2));
            parent.Add(new Label("30 fps"));
            BindTransport();
        }

        void TogglePlayback()
        {
            if (playing) { StopPlayback(); return; }
            if (PlaybackRange.TryTransport(transportStart, transportEnd, frame, PlaybackLastFrame, out var range))
                StartPlayback(range);
            else status.text = "再生範囲には開始 ≦ 終了のフレーム番号を入力してください。";
        }

        int PlaybackLastFrame
        {
            get
            {
                int last = Mathf.Max(LastFrame(), AudioLastFrame);
                foreach (var overlay in frameOverlays) last = Mathf.Max(last, Mathf.Clamp(overlay.LastFrame, 0, MaxFrame));
                return last;
            }
        }

        void PlayFromBeginning()
        {
            if (playing) { StopPlayback(); return; }
            StartPlayback(PlaybackRange.FromBeginning(PlaybackLastFrame));
        }

        void PlayBetweenBookmarks()
        {
            if (playing) { StopPlayback(); return; }
            StartPlayback(PlaybackRange.BetweenBookmarks(frame, PlaybackLastFrame, bookmarks));
        }

        void StartPlayback(PlaybackRange range)
        {
            playbackRange = range; playStart = range.First; playEnd = range.Last;
            playing = true; InvalidateDynamics();
            Seek(playStart);
            startedAt = Time.unscaledTimeAsDouble;
            playButton.text = "■ 停止";
            audioCycle = 0; PlayAudioAt(playStart);
            ApplyView();
            RedrawOverlay();
        }

        void Update()
        {
            PumpMcp();
            PumpModelEffectDrops();
            PumpExtensions(); PumpSymbolKeys(); UpdateDock(); CompleteProfileSwitch();
            if (!physicsBaking)
            {
                if (playing) AdvancePlayback(Time.unscaledTimeAsDouble);
                else if (physicsMode == PhysicsMode.Always && modal == null && !exporting)
                {
                    ForEachActor(calculationOrder, IdleDynamics);
                }
            }
            if (viewOptions.information) UpdateViewInfo();
            // Visibility changes update the backdrop immediately. Visible media can
            // still change texture or aspect as video prepares or the view resizes.
            if (viewOptions.showVideo || viewOptions.showImage) UpdateBackdrop();
        }

        void AdvancePlayback(double now)
        {
            long offset = (long)Math.Floor(Math.Max(0, now - startedAt) * 30);
            var step = playbackRange.At(offset, transportRepeat);
            if (step.Finished) { Seek(step.Frame); StopPlayback(); return; }
            if (step.Cycle != audioCycle) { audioCycle = step.Cycle; PlayAudioAt(step.Frame); }
            if (step.Frame != frame) Seek(step.Frame);
        }

        void StopPlayback()
        {
            if (!playing) return;
            playing = false;
            playButton.text = "▶ 再生";
            soundtrack.Stop();
            ApplyView();
            RedrawOverlay();
        }
    }
}
