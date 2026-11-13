using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const int MaxFrame = 600000, RowHeight = 19, NameWidth = 96;
        int FrameWidth => ribbonProfile ? ribbonFrameWidth : 12;
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
        double startedAt;

        struct KeyChange
        {
            public MotionTrack Track;
            public int Frame;
            public object Before, After;
            public KeySource BeforeSource,AfterSource;
            public bool ReplaceSource;
            public Action<MotionTrack,bool> Replay;
            public bool IsDraft;
            public string Layer,Clip,SourceId;
            public VmdSidecar SidecarBefore,SidecarAfter;
            public int? DocumentFrame;
            public object PreviousDraft;
        }

        readonly Stack<KeyChange[]> undo = new Stack<KeyChange[]>();
        readonly Stack<KeyChange[]> redo = new Stack<KeyChange[]>();

        void BuildTimeline(VisualElement sidebar)
        {
            var history = Box(sidebar, "history", "row");
            undoButton = Action(history, "元に戻す", false, () => ApplyHistory(false));
            redoButton = Action(history, "やり直し", false, () => ApplyHistory(true));
            var heading = new Label("フレーム操作"){name="frame-heading"}; heading.AddToClassList("header"); sidebar.Add(heading);
            var row = Box(sidebar, "frame-row", "row", "frame-nav");
            Action(row, "|<", true, () => SetFrame(0)).tooltip = "先頭フレーム";
            Action(row, ".<", true, () => JumpKey(false)).tooltip = "前の登録キー";
            Action(row, "<", true, () => SetFrame(frame - 1));
            frameField = new IntegerField { isDelayed = true, tooltip = "現在フレーム" };
            row.Add(frameField);
            frameField.RegisterValueChangedCallback(e => SetFrame(e.newValue));
            Action(row, ">", true, () => SetFrame(frame + 1));
            Action(row, ">.", true, () => JumpKey(true)).tooltip = "次の登録キー";
            Action(row, ">|", true, () => SetFrame(LastFrame())).tooltip = "最終登録フレーム";

            BuildWaveform(sidebar);
            timeline = Box(sidebar, "timeline", "timeline");
            timeline.focusable = true;
            timeline.generateVisualContent += DrawTimeline;
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
                        bool keyed=track!=null && track.KeyAt(at)!=null;
                        extendSelection=e.shiftKey || e.ctrlKey;
                        if(!extendSelection && !(keyed && selectedKeys.Contains((track,at))))selectedKeys.Clear();
                        rangeSelected=true;
                        if(keyed && e.ctrlKey && selectedKeys.Contains((track,at)))selectedKeys.Remove((track,at));
                        else if(keyed)selectedKeys.Add((track,at));
                        timeDrag=keyed && selectedKeys.Contains((track,at))?TimeDrag.Move:TimeDrag.Select;
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
            var edit = Box(sidebar, "key-edit", "row");
            copyButton = Action(edit, "コピー", false, CopyKey);
            pasteButton = Action(edit, "ペースト", false, () => PasteOriginalKeys(false));
            Action(edit, "縦選択", true, () => SelectKeys(frame, frame, true));
            deleteButton = Action(edit, "削除", false, DeleteKeys);
            var range = Box(sidebar, "key-range", "row");
            rangeStart = new IntegerField { isDelayed = true, tooltip = "選択開始フレーム" };
            rangeEnd = new IntegerField { isDelayed = true, tooltip = "選択終了フレーム" };
            range.Add(rangeStart); range.Add(new Label("～")); range.Add(rangeEnd);
            var scope = Box(sidebar, "key-scope", "row");
            rangeScope = Choice(scope, new[] { "選択行", "全ての行" });
            Action(scope, "範囲選択", true, () => SelectKeys(rangeStart.value, rangeEnd.value, rangeScope.index == 1));
        }

        int VisibleColumns => Mathf.Max(1, Mathf.FloorToInt((timeline.contentRect.width - NameWidth) / FrameWidth));
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
            frame = Mathf.Clamp(value, 0, MaxFrame);
            frameField.SetValueWithoutNotify(frame);SyncRibbonSeek();
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
            int index = VisibleTracks.IndexOf(track);
            if (index >= 0 && (index < firstRow || index >= firstRow + VisibleRows))
                firstRow = Mathf.Clamp(index, 0, MaxFirstRow());
            if (viewportTitle != null)
                viewportTitle.text = modelMode ? actor.avatar.name + "：" + (track?.Name ?? "ボーンなし") : SceneModeName;
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
            if (selectedTrack == null) return;
            foreach (var key in forward ? selectedTrack.Frames : selectedTrack.Frames.Reverse())
                if (forward ? key > frame : key < frame) { SetFrame(key); return; }
        }

        void SelectKeys(int start, int end, bool allTracks)
        {
            StopPlayback();
            selectedKeys.Clear(); rangeSelected = true;
            foreach (var track in VisibleTracks)
            {
                if (!allTracks && track != selectedTrack) continue;
                foreach (int key in track.EditFrames)
                    if (key >= start && key <= end) selectedKeys.Add((track, key));
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
                keyClipboard.Add(new KeyChange { Track = key.track, Frame = key.frame, After = key.track.EditKey(key.frame),AfterSource=key.track.Target==null?key.track.SourceAt(key.frame):null });
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
                    DocumentFrame=frame,After=k.After,ReplaceSource=true,AfterSource=oneTrack && target!=k.Track?null:k.AfterSource};
            });
            CommitKeys(edits);
        }

        void DeleteKeys()
        {
            CommitKeys(EditKeys().Where(k => k.frame > 0 || k.track.Target!=null).Select(k => new KeyChange { Track = k.track, Layer=k.track.ActiveLayer, Frame = k.frame, DocumentFrame=frame }));
        }

        void CommitKeys(IEnumerable<KeyChange> edits)
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
                if(change.Layer==null){
                    change.BeforeSource=change.Track.SourceAt(change.Frame);
                    if(change.After==null)change.AfterSource=null;else if(!change.ReplaceSource)change.AfterSource=change.BeforeSource;
                }
                if (!Equals(change.Before, change.After) || change.BeforeSource!=change.AfterSource) changes.Add(change);
            }
            if (changes.Count == 0) return;
            StopPlayback();
            foreach (var c in changes) ApplyKeyChange(c,c.After);
            undo.Push(changes.ToArray()); redo.Clear();
            selectedKeys.Clear(); rangeSelected = false;
            var affected=InvalidateChangedActors(changes);
            foreach (var track in changes.Select(c => c.Track).Distinct()) track.Seek(frame);
            ForEachActor(affected,RefreshRigPreview); SyncControls(); RefreshKeyButtons(); timeline.MarkDirtyRepaint(); RedrawOverlay();
        }

        static void ApplyKeyChange(KeyChange c,object value,bool before=false)
        {
            if(c.Layer==null){c.Track.Write(c.Frame,value);c.Track.SetSource(c.Frame,before?c.BeforeSource:c.AfterSource);}
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
            foreach(var change in changes.Where(c=>c.SidecarAfter!=null))
            {
                var data=forward?change.SidecarAfter:change.SidecarBefore;
                if(change.Track==cameraTrack)sceneSidecar=data;else drawOrder.First(a=>a.modelSwitch==change.Track).vmdSidecar=data;
            }
            foreach (var change in changes.Where(c=>!c.IsDraft && c.SidecarAfter==null)) ApplyKeyChange(change, forward ? change.After : change.Before,!forward);
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
            waveform?.MarkDirtyRepaint();
            timeline.Clear();
            if (!(timeline.contentRect.height > 0) || !(timeline.contentRect.width > 0)) return;
            firstRow = Mathf.Clamp(firstRow, 0, MaxFirstRow());
            LayoutTimeLabels(timeline,firstFrame,firstRow,VisibleColumns,VisibleRows,FrameWidth);
        }
        int timelineRevision;
        void LayoutTimeLabels(VisualElement board,int first,int top,int columns,int rows,int spacing)
        {
            var ruler = Box(board, "ruler", "ruler");
            ruler.pickingMode = PickingMode.Ignore;
            for (int i = 0; i < columns && first + i <= MaxFrame; ++i)
            {
                int n = first + i;
                if (n % 5 != 0 && n != first) continue;
                var tick = new Label(n.ToString()) { pickingMode = PickingMode.Ignore };
                tick.style.left = NameWidth + i * spacing;
                ruler.Add(tick);
            }
            for (int i = top; i < Mathf.Min(TimelineRows, top + rows); ++i)
            {
                var line=ribbonProfile?ribbonRows[i]:null;
                var track = line==null?VisibleTracks[i]:line.track;
                var label = new Label(line?.label??DisplayName(track)) { tooltip = track?.Name??line?.group, pickingMode = PickingMode.Ignore };
                label.AddToClassList("track-name");
                label.EnableInClassList("selected-track", track!=null && selectedRows.Contains(track) && (line==null || line.layer==track.Target));
                board.Add(label);
            }
            board.MarkDirtyRepaint();
        }

        void DrawTimeline(MeshGenerationContext ctx)=>DrawTimeGrid(ctx,timeline,firstFrame,firstRow,VisibleColumns,VisibleRows,FrameWidth,null);
        void DrawTimeGrid(MeshGenerationContext ctx,VisualElement board,int first,int top,int columns,int rows,int spacing,TimeWindow window)
        {
            var p = ctx.painter2D;
            float w = board.contentRect.width, h = board.contentRect.height;
            if(ribbonProfile)h=Mathf.Min(h,(TimelineRows-top+1)*RowHeight);
            DrawTimeDrag(p,window);
            var grid = new Color(.25f,.25f,.29f);
            for (int i = 0; i < columns; i++)
            {
                float x = NameWidth + (i + .5f) * spacing;
                Line(p, new Vector2(x, RowHeight), new Vector2(x,h), (first+i)%5==0 ? Color.gray : grid);
            }
            for (int y = RowHeight; y < h; y += RowHeight)
                Line(p, new Vector2(0,y),new Vector2(w,y),grid);
            foreach (int mark in bookmarks.GetViewBetween(first, first + columns))
            {
                float x = NameWidth + (mark - first + .5f) * spacing;
                Line(p, new Vector2(x, 0), new Vector2(x, RowHeight), Color.yellow);
            }
            float cursor = NameWidth + (frame - first + .5f) * spacing;
            if (cursor >= NameWidth && cursor < w)
                Line(p,new Vector2(cursor,RowHeight),new Vector2(cursor,h),Color.green);
            for (int i = top; i < Mathf.Min(TimelineRows, top + rows); ++i)
            {
                var line=ribbonProfile?ribbonRows[i]:null;
                var track=line==null?VisibleTracks[i]:line.track;
                float y = (i - top + 1.5f) * RowHeight;
                for (int col = 0; col < columns; col++)
                {
                    int local=first+col;
                    if(line!=null ? !line.HasKey(local) : track.KeyAt(local)==null)continue;
                    if(line?.layer!=null && line.layer==track.Target)track.LocalTime(local,out local);
                    float x = NameWidth + (col + .5f) * spacing;
                    p.fillColor = (selectedKeys.Contains((track, local)) || (!rangeSelected && track == selectedTrack && frame == first + col)) ? Color.red : Color.white;
                    if (track?.KeyAt(first+col) is BonePose bp && !bp.PhysicsOff && actor.dynamicLinks.Any(d=>d.Bone.Track==track)) {
                        Line(p,new Vector2(x-4,y-4),new Vector2(x+4,y+4),p.fillColor);
                        Line(p,new Vector2(x-4,y+4),new Vector2(x+4,y-4),p.fillColor);
                        continue;
                    }
                    if(track!=null && !track.EnabledKey(local))p.fillColor=Color.gray;
                    p.BeginPath(); p.MoveTo(new Vector2(x,y-5)); p.LineTo(new Vector2(x+5,y));
                    p.LineTo(new Vector2(x,y+5)); p.LineTo(new Vector2(x-5,y)); p.ClosePath(); p.Fill();
                }
            }
        }

        IEnumerable<MotionTrack> GroupTracks(bool secondary) => VisibleTracks.Where(t =>
            modelMode ? (secondary ? t is MotionTrack<float> : t is MotionTrack<BonePose>) :
                (secondary ? t==lightTrack : t==cameraTrack));

        void SelectGroup(bool secondary)
        {
            selectedKeys.Clear(); rangeSelected = true;
            foreach (var track in GroupTracks(secondary))
                foreach (int f in track.Frames) selectedKeys.Add((track, f));
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
                if (insert && track.EditFrames.Contains(MaxFrame)) { status.text = "移動先がフレーム上限を超えます。"; return; }
                var result = new Dictionary<int, object>();
                foreach (int f in track.EditFrames)
                    if (f >= start) result[f] = null;
                foreach (int f in track.EditFrames)
                    if (insert ? f >= start : f > start) result[f + (insert ? 1 : -1)] = track.EditKey(f);
                foreach (var entry in result)
                    edits.Add(new KeyChange { Track = track, Layer=track.ActiveLayer, Frame = entry.Key, DocumentFrame=frame, After = entry.Value,ReplaceSource=true,AfterSource=entry.Value==null?null:track.SourceAt(entry.Key+(insert?-1:1)) });
            }
            CommitKeys(edits);
        }

        void BuildPlayback(VisualElement parent)
        {
            var heading = new Label("再生"); heading.AddToClassList("header"); parent.Add(heading);
            var buttons = Box(parent, "play-actions", "row");
            playButton = Action(buttons, "再生", true, TogglePlayback);
            repeat = new Toggle("くり返し"); buttons.Add(repeat);
            var range = Box(parent, "playback-range", "row");
            playFrom = new TextField { tooltip = "開始フレーム（空欄は先頭）" };
            playTo = new TextField { tooltip = "終了フレーム（空欄は最後の登録キー）" };
            range.Add(playFrom); range.Add(new Label("～")); range.Add(playTo); BindTransport();
        }

        void TogglePlayback()
        {
            if (playing) { StopPlayback(); return; }
            if (!ReadLimit(transportStart, 0, out playStart) || !ReadLimit(transportEnd, Mathf.Max(LastFrame(), AudioLastFrame), out playEnd) || playEnd < playStart)
            { status.text = "再生範囲には開始 ≦ 終了のフレーム番号を入力してください。"; return; }
            playing = true; InvalidateDynamics();
            Seek(playStart);
            startedAt = Time.unscaledTimeAsDouble;
            playButton.text = "停止";
            audioCycle = 0; PlayAudioAt(playStart);
            ApplyView();
            RedrawOverlay();
        }

        static bool ReadLimit(string text, int fallback, out int limit)
        {
            limit = fallback;
            return string.IsNullOrWhiteSpace(text) || (int.TryParse(text, out limit) && limit >= 0 && limit <= MaxFrame);
        }

        void Update()
        {
            PumpMcp();
            PumpExtensions(); PumpSymbolKeys(); UpdateDock(); CompleteProfileSwitch();
            if (playing) AdvancePlayback(Time.unscaledTimeAsDouble); else if(physicsEngine==PhysicsEngine.Bullet)IdleBullet(); else ForEachActor(calculationOrder,IdleDynamics);
            UpdateViewInfo(); UpdateBackdrop(); detached.Synchronize();
        }

        void AdvancePlayback(double now)
        {
            long offset = (long)Math.Floor(Math.Max(0, now - startedAt) * 30);
            int length = playEnd - playStart + 1;
            if (offset >= length && !transportRepeat) { Seek(playEnd); StopPlayback(); return; }
            int next = playStart + (int)(offset % length);
            long cycle = offset / length;
            if (cycle != audioCycle) { audioCycle = cycle; PlayAudioAt(next); }
            if (next != frame) Seek(next);
        }

        void StopPlayback()
        {
            if (!playing) return;
            playing = false;
            playButton.text = "再生";
            soundtrack.Stop();
            ApplyView();
            RedrawOverlay();
        }
    }
}
