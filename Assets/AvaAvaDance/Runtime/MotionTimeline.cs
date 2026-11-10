using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const int MaxFrame = 600000, RowHeight = 19, NameWidth = 96, FrameWidth = 12;
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
        }

        readonly Stack<KeyChange[]> undo = new Stack<KeyChange[]>();
        readonly Stack<KeyChange[]> redo = new Stack<KeyChange[]>();

        void BuildTimeline(VisualElement sidebar)
        {
            var history = Box(sidebar, "history", "row");
            undoButton = Action(history, "元に戻す", false, () => ApplyHistory(false));
            redoButton = Action(history, "やり直し", false, () => ApplyHistory(true));
            var heading = new Label("フレーム操作"); heading.AddToClassList("header"); sidebar.Add(heading);
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
            timeline.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                timeline.Focus();
                int index = firstRow + Mathf.FloorToInt(e.localPosition.y / RowHeight) - 1;
                if (e.localPosition.y >= RowHeight && index >= 0 && index < VisibleTracks.Count) {
                    var row=VisibleTracks[index];
                    if(e.shiftKey && e.localPosition.x<NameWidth) {
                        var group=new HashSet<MotionTrack>(selectedRows);
                        if(!group.Add(row) && group.Count>1)group.Remove(row);
                        SelectTrack(group.Contains(row)?row:group.First());
                        selectedRows.UnionWith(group);RefreshTimelineLabels();
                    }else SelectTrack(row);
                }
                if (e.localPosition.x >= NameWidth)
                    SetFrame(firstFrame + Mathf.RoundToInt((e.localPosition.x - NameWidth - FrameWidth / 2f) / FrameWidth));
                e.StopPropagation();
            });
            timeline.RegisterCallback<WheelEvent>(e =>
            {
                int delta = Math.Sign(e.delta.y);
                if (e.shiftKey) SetFirstFrame(firstFrame + 5 * delta);
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
        int MaxFirstRow() => Mathf.Max(0, VisibleTracks.Count - VisibleRows);

        void SetFirstFrame(int value)
        {
            firstFrame = Mathf.Clamp(value, 0, Mathf.Max(0, MaxFrame - VisibleColumns + 1));
            horizontal?.SetValueWithoutNotify(firstFrame);
            RefreshTimelineLabels();
        }

        void SetFrame(int value)
        {
            StopPlayback();
            selectedKeys.Clear(); rangeSelected = false;
            Seek(value);
            if (scrubSound) PlayAudioAt(frame, true);
        }

        void Seek(int value)
        {
            frame = Mathf.Clamp(value, 0, MaxFrame);
            frameField.SetValueWithoutNotify(frame);
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
                foreach (int key in track.Frames)
                    if (key >= start && key <= end) selectedKeys.Add((track, key));
            }
            RefreshKeyButtons(); timeline.MarkDirtyRepaint();
            status.text = selectedKeys.Count + " 個のキーを選択";
        }

        IEnumerable<(MotionTrack track, int frame)> EditKeys()
        {
            if (rangeSelected) return selectedKeys;
            return selectedTrack?.KeyAt(frame) != null ? new[] { (selectedTrack, frame) } :
                Array.Empty<(MotionTrack, int)>();
        }

        void CopyKey()
        {
            keyClipboard.Clear();
            foreach (var key in EditKeys().OrderBy(k => k.frame))
                keyClipboard.Add(new KeyChange { Track = key.track, Frame = key.frame, After = key.track.KeyAt(key.frame) });
            RefreshKeyButtons();
        }

        bool CanPaste()
        {
            if (keyClipboard.Count == 0 || frame + keyClipboard[keyClipboard.Count-1].Frame - keyClipboard[0].Frame > MaxFrame) return false;
            if (keyClipboard.All(k => k.Track == keyClipboard[0].Track))
                return selectedTrack != null && keyClipboard[0].Track.GetType() == selectedTrack.GetType();
            return keyClipboard.All(k => VisibleTracks.Contains(k.Track));
        }

        void PasteKeys()
        {
            if (!CanPaste()) return;
            bool oneTrack = keyClipboard.All(k => k.Track == keyClipboard[0].Track);
            var edits = keyClipboard.Select(k => new KeyChange {
                Track = oneTrack ? selectedTrack : k.Track, Frame = frame + k.Frame - keyClipboard[0].Frame, After = k.After
            });
            CommitKeys(edits);
        }

        void DeleteKeys()
        {
            CommitKeys(EditKeys().Where(k => k.frame > 0).Select(k => new KeyChange { Track = k.track, Frame = k.frame }));
        }

        void CommitKeys(IEnumerable<KeyChange> edits)
        {
            var changes = new List<KeyChange>();
            foreach (var edit in edits)
            {
                var change = edit;
                change.Before = change.Track.KeyAt(change.Frame);
                if (!Equals(change.Before, change.After)) changes.Add(change);
            }
            if (changes.Count == 0) return;
            StopPlayback();
            foreach (var c in changes) c.Track.Write(c.Frame, c.After);
            undo.Push(changes.ToArray()); redo.Clear();
            selectedKeys.Clear(); rangeSelected = false;
            InvalidateDynamics();
            foreach (var track in changes.Select(c => c.Track).Distinct()) track.Seek(frame);
            RefreshRigPreview(); SyncControls(); RefreshKeyButtons(); timeline.MarkDirtyRepaint(); RedrawOverlay();
        }

        void RegisterKey(MotionTrack track)
        {
            if (track == null) return;
            CommitKeys(RegistrationEdits(track)); SelectTrack(track);
        }

        void WriteKey(MotionTrack track, object value)
        {
            if (track == null || (value == null && frame == 0)) return;
            CommitKeys(new[] { new KeyChange { Track = track, Frame = frame, After = value } });
            SelectTrack(track);
        }

        void ApplyHistory(bool forward)
        {
            var source = forward ? redo : undo;
            if (source.Count == 0) return;
            var changes = source.Pop();
            (forward ? undo : redo).Push(changes);
            foreach (var change in changes) change.Track.Write(change.Frame, forward ? change.After : change.Before);
            InvalidateDynamics();
            var first = changes[0];
            var owner=drawOrder.Find(item=>item.modelTracks.Contains(first.Track));
            if(owner!=null && owner!=actor){actor=owner;RefreshActorUI();} ChangeMode(owner!=null);
            SelectTrack(first.Track);
            SetFrame(first.Frame);
        }

        void RefreshKeyButtons()
        {
            copyButton.SetEnabled(EditKeys().Any());
            pasteButton.SetEnabled(CanPasteOriginal());
            deleteButton.SetEnabled(EditKeys().Any(k => k.frame > 0));
            undoButton.SetEnabled(undo.Count != 0);
            redoButton.SetEnabled(redo.Count != 0);
        }

        void RefreshTimelineLabels()
        {
            waveform?.MarkDirtyRepaint();
            timeline.Clear();
            if (!(timeline.contentRect.height > 0) || !(timeline.contentRect.width > 0)) return;
            firstRow = Mathf.Clamp(firstRow, 0, MaxFirstRow());
            var ruler = Box(timeline, "ruler", "ruler");
            ruler.pickingMode = PickingMode.Ignore;
            for (int i = 0; i < VisibleColumns && firstFrame + i <= MaxFrame; ++i)
            {
                int n = firstFrame + i;
                if (n % 5 != 0 && n != firstFrame) continue;
                var tick = new Label(n.ToString()) { pickingMode = PickingMode.Ignore };
                tick.style.left = NameWidth + i * FrameWidth;
                ruler.Add(tick);
            }
            for (int i = firstRow; i < Mathf.Min(VisibleTracks.Count, firstRow + VisibleRows); ++i)
            {
                var track = VisibleTracks[i];
                var label = new Label(DisplayName(track)) { tooltip = track.Name, pickingMode = PickingMode.Ignore };
                label.AddToClassList("track-name");
                label.EnableInClassList("selected-track", selectedRows.Contains(track));
                timeline.Add(label);
            }
            timeline.MarkDirtyRepaint();
        }

        void DrawTimeline(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            float w = timeline.contentRect.width, h = timeline.contentRect.height;
            var grid = new Color(.25f,.25f,.29f);
            for (int i = 0; i < VisibleColumns; i++)
            {
                float x = NameWidth + (i + .5f) * FrameWidth;
                Line(p, new Vector2(x, RowHeight), new Vector2(x,h), (firstFrame+i)%5==0 ? Color.gray : grid);
            }
            for (int y = RowHeight; y < h; y += RowHeight)
                Line(p, new Vector2(0,y),new Vector2(w,y),grid);
            float cursor = NameWidth + (frame - firstFrame + .5f) * FrameWidth;
            if (cursor >= NameWidth && cursor < w)
                Line(p,new Vector2(cursor,RowHeight),new Vector2(cursor,h),Color.green);
            for (int i = firstRow; i < Mathf.Min(VisibleTracks.Count, firstRow + VisibleRows); ++i)
            {
                var track = VisibleTracks[i];
                float y = (i - firstRow + 1.5f) * RowHeight;
                for (int col = 0; col < VisibleColumns; col++)
                {
                    if (!track.Frames.Contains(firstFrame + col)) continue;
                    float x = NameWidth + (col + .5f) * FrameWidth;
                    p.fillColor = (selectedKeys.Contains((track, firstFrame + col)) || (!rangeSelected && track == selectedTrack && frame == firstFrame + col)) ? Color.red : Color.white;
                    if (track.KeyAt(firstFrame+col) is BonePose bp && !bp.PhysicsOff && actor.dynamicLinks.Any(d=>d.Bone.Track==track)) {
                        Line(p,new Vector2(x-4,y-4),new Vector2(x+4,y+4),p.fillColor);
                        Line(p,new Vector2(x-4,y+4),new Vector2(x+4,y-4),p.fillColor);
                        continue;
                    }
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

        void ShiftColumn(bool secondary, bool insert)
        {
            if (frame == 0 && !insert) { status.text = "先頭フレームは削除できません。"; return; }
            var edits = new List<KeyChange>();
            int start = Mathf.Max(1, frame);
            foreach (var track in GroupTracks(secondary))
            {
                if (insert && track.LastFrame == MaxFrame) { status.text = "移動先がフレーム上限を超えます。"; return; }
                var result = new Dictionary<int, object>();
                foreach (int f in track.Frames)
                    if (f >= start) result[f] = null;
                foreach (int f in track.Frames)
                    if (insert ? f >= start : f > start) result[f + (insert ? 1 : -1)] = track.KeyAt(f);
                foreach (var entry in result)
                    edits.Add(new KeyChange { Track = track, Frame = entry.Key, After = entry.Value });
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
            range.Add(playFrom); range.Add(new Label("～")); range.Add(playTo);
        }

        void TogglePlayback()
        {
            if (playing) { StopPlayback(); return; }
            if (!ReadLimit(playFrom, 0, out playStart) || !ReadLimit(playTo, Mathf.Max(LastFrame(), AudioLastFrame), out playEnd) || playEnd < playStart)
            { status.text = "再生範囲には開始 ≦ 終了のフレーム番号を入力してください。"; return; }
            playing = true; InvalidateDynamics();
            Seek(playStart);
            startedAt = Time.unscaledTimeAsDouble;
            playButton.text = "停止";
            audioCycle = 0; PlayAudioAt(playStart);
            ApplyView();
            RedrawOverlay();
        }

        static bool ReadLimit(TextField field, int fallback, out int limit)
        {
            limit = fallback;
            return string.IsNullOrWhiteSpace(field.value) || (int.TryParse(field.value, out limit) && limit >= 0 && limit <= MaxFrame);
        }

        void Update()
        {
            if (playing) AdvancePlayback(Time.unscaledTimeAsDouble); else if(physicsEngine==PhysicsEngine.Bullet)IdleBullet(); else ForEachActor(calculationOrder,IdleDynamics);
            UpdateViewInfo(); UpdateBackdrop(); detached.Draw(previewTexture); UpdatePropScreen();
        }

        void AdvancePlayback(double now)
        {
            long offset = (long)Math.Floor(Math.Max(0, now - startedAt) * 30);
            int length = playEnd - playStart + 1;
            if (offset >= length && !repeat.value) { Seek(playEnd); StopPlayback(); return; }
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
