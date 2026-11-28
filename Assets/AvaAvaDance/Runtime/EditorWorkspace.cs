using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        bool ribbonProfile, showMarkers, showHandles, ribbonCollapsed;
        bool? requestedProfile;
        int ribbonTab, ribbonFrameWidth;
        VisualElement ribbon, ribbonTabs, ribbonTools, quickAccess, statusControls;
        ScrollView ribbonScroll;
        readonly SortedSet<int> bookmarks = new SortedSet<int>();
        readonly List<(Button button, Func<bool> available)> ribbonActions = new List<(Button, Func<bool>)>();
        KeyCode transformKey;
        readonly HashSet<KeyCode> pressedKeys = new HashSet<KeyCode>();
        readonly HashSet<KeyCode> suppressedKeys = new HashSet<KeyCode>();
        Vector2 holdPoint;

        (VisualElement mode, VisualElement rig, VisualElement tools) BuildStatusLine(VisualElement workspace)
        {
            LoadKeyOverrides();
            MenuRoot.RegisterCallback<PointerDownEvent>(e=>{mainKeyArea=KeyArea(e.target as VisualElement)??"全体";},TrickleDown.TrickleDown);
            var status = Box(workspace, "status-line", "status-line");
            var scroll = new ScrollView(ScrollViewMode.Horizontal);
            scroll.AddToClassList("status-scroll"); status.Add(scroll);
            var bar = statusControls = Box(scroll, "status-controls", "row", "status-controls");
            var mode = Box(bar, "mode-controls", "row");
            quickAccess = Box(bar, "quick-access", "row");
            ProfileAction(quickAccess, "新規", () => Execute(Command.New));
            ProfileAction(quickAccess, "開く", () => Execute(Command.Open));
            ProfileAction(quickAccess, "保存", () => Execute(Command.Save));
            ProfileAction(quickAccess, "元に戻す", () => ApplyHistory(false), () => undo.Count > 0);
            ProfileAction(quickAccess, "やり直し", () => ApplyHistory(true), () => redo.Count > 0);
            var rig = Box(bar, "rig-toggles", "row");
            ProfileAction(rig, "L/G", ToggleCoordinateSpace);
            ProfileAction(rig, "Bones", ToggleMarkers);
            ProfileAction(rig, "IK", ToggleHandles);
            ProfileAction(rig, "Handles", ToggleExtendedHandles);
            BuildLayerSelector(bar);
            Box(bar, "status-spacer").style.flexGrow = 1;
            var tools = Box(bar, "workspace-tools", "row");
            Action(tools, "キー設定", true, EditShortcuts).focusable = false;
            MenuRoot.RegisterCallback<KeyUpEvent>(e => {
                if(DuplicateMainSymbol(e.target as VisualElement,e.keyCode))return;
                if(symbol.Present)pressedSymbols.Remove(symbol.vk);
                pressedKeys.Remove(e.keyCode); suppressedKeys.Remove(e.keyCode);
                if (transformKey!=KeyCode.None && (heldVirtual!=0?symbol.vk==heldVirtual:heldLabel==e.keyCode)) FinishHeldTransform();
            }, TrickleDown.TrickleDown);
            MenuRoot.RegisterCallback<PointerMoveEvent>(e => {
                if (transformKey == KeyCode.None) return;
                Vector2 point = e.position;
                ApplyHeldDelta(transformKey, point - holdPoint);
                holdPoint = point;
                e.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            return (mode, rig, tools);
        }

        Button ProfileAction(VisualElement parent, string label, System.Action action, Func<bool> available = null)
        {
            var button = Action(parent, label, true, () => {
                if (!importing && !exporting && modal == null && importDialog == null && (available == null || available())) action();
            });
            button.focusable = false;
            if (parent == quickAccess || ribbonTools != null && (parent == ribbonTools || ribbonTools.Contains(parent)))RibbonIcon(button,label);
            ribbonActions.Add((button, available));return button;
        }

        void BuildRibbon(VisualElement workspace)
        {
            ribbon = Box(workspace, "shelf", "shelf");
            ribbonTabs = Box(ribbon, "shelf-tabs", "row", "shelf-tabs");
            ribbonScroll = new ScrollView(ScrollViewMode.Horizontal);
            ribbonScroll.name = "shelf-scroll"; ribbonScroll.AddToClassList("shelf-scroll");
            ribbon.Add(ribbonScroll);
            ribbonTools = Box(ribbonScroll, "shelf-tools", "row", "shelf-tools");
            ribbonFrameWidth = Mathf.Clamp(WorkspacePreferences.GetInt("Ribbon.FrameWidth",9),4,48);
            showMarkers = WorkspacePreferences.GetInt("Ribbon.Markers",1) != 0;
            showHandles = WorkspacePreferences.GetInt("Ribbon.Handles") != 0;
            extendedHandles=WorkspacePreferences.GetInt("Ribbon.ExtendedHandles")!=0;
            ribbonCollapsed = WorkspacePreferences.GetInt("Ribbon.Collapsed") != 0;
            string[] categories = { "General", "Rigging", "Animation", "Expressions", "Render", "Physics", "Plugins" };
            for (int i = 0; i < categories.Length; i++)
            {
                int tab = i;
                Action(ribbonTabs, categories[i], true, () => SelectRibbonTab(tab)).focusable = false;
            }
            Action(ribbonTabs, "折りたたみ", true, () => {
                ribbonCollapsed = !ribbonCollapsed;
                WorkspacePreferences.SetInt("Ribbon.Collapsed", ribbonCollapsed ? 1 : 0);
                ribbonScroll.EnableInClassList("hidden", ribbonCollapsed);
            }).focusable = false;
            SelectRibbonTab(Mathf.Clamp(WorkspacePreferences.GetInt("Workspace.ShelfTab", 2),0,categories.Length-1));
            workspace.schedule.Execute(RefreshRibbonActions).Every(125);
        }

        void SelectRibbonTab(int tab)
        {
            ribbonTab = tab; WorkspacePreferences.SetInt("Workspace.ShelfTab", tab);
            for (int i = 0; i < ribbonTabs.childCount; i++)
                ribbonTabs[i].EnableInClassList("active-tool", i == tab);
            ribbonActions.RemoveAll(item => ribbonTools.Contains(item.button));
            ribbonTools.Clear();
            void Add(string label, Command command) =>
                ProfileAction(ribbonTools, label, () => Execute(command), () => Available(command));
            switch (tab)
            {
                case 0:
                    Add("新規", Command.New); Add("開く", Command.Open); Add("保存", Command.Save);
                    Add("モデル読込", Command.ImportModel); Add("アクセサリ読込", Command.ImportProp);
                    Add("モーション読込", Command.LoadMotion); Add("モーション保存", Command.SaveMotion);
                    Add("ポーズ読込", Command.LoadPose); Add("ポーズ保存", Command.SavePose);
                    Add("VRMA 書き出し", Command.SaveVrma); Add("リグ保存", Command.SaveRig);
                    break;
                case 1:
                    Add("ボーン表示", Command.ToggleMarkers); Add("ポーズハンドル", Command.ToggleHandles);
                    Add("拡張ハンドル", Command.ToggleExtendedHandles); Add("未登録選択", Command.SelectUnregistered);
                    Add("ポーズ反転", Command.MirrorPose); Add("位置角度補正", Command.AdjustBones);
                    Add("センター位置バイアス", Command.CenterBias); Add("モーションサイジング", Command.MotionSizing);
                    Add("モデル位置・回転", Command.ModelPlacement); Add("IKを追加", Command.AddIK);
                    Add("手指の握り", Command.HandPose); Add("外部親", Command.ExternalParent);
                    break;
                case 2:
                    Add("登録", Command.RegisterKeys); Add("切り取り", Command.CutKeys);
                    Add("コピー", Command.CopyKeys); Add("貼り付け", Command.PasteKeys);
                    Add("列挿入", Command.InsertTimelineColumn); Add("列削除", Command.DeleteTimelineColumn);
                    Add("拡大・縮小", Command.ScaleKeyframes);
                    Add("ブックマーク", Command.ToggleBookmark); Add("前のブックマーク", Command.PreviousBookmark);
                    Add("次のブックマーク", Command.NextBookmark); Add("すべてのブックマークを削除", Command.ClearBookmarks);
                    Add("WAV読込", Command.LoadWave); Add("音声の開始フレーム", Command.AudioPlacement);
                    break;
                case 3:
                    Add("全表情登録", Command.RegisterMorphs); Add("表情リセット", Command.ResetMorphs);
                    Add("リップシンク", Command.LipSync); Add("リップ移動", Command.ShiftLips);
                    Add("まばたき生成", Command.Blink); Add("目のキー消去", Command.DeleteEyes);
                    Add("リップのキー消去", Command.DeleteLips); Add("まゆのキー消去", Command.DeleteBrows);
                    break;
                case 4:
                    Add("出力サイズ", Command.OutputSize); Add("画像出力", Command.ExportImage);
                    Add("AVI出力", Command.ExportAvi); Add("MP4出力", Command.ExportMp4);
                    Add("背景画像", Command.LoadImage); Add("背景動画", Command.LoadVideo);
                    Add("背景移動・拡縮", Command.BackdropPlacement); Add("地面影", Command.GroundShadow);
                    Add("セルフ影", Command.SelfShadow); Add("別窓プレビュー", Command.SeparateWindow);
                    break;
                case 5:
                    Add("常に演算", Command.PhysicsAlways); Add("トレース", Command.PhysicsTrace);
                    Add("キーに焼込", Command.BakePhysics); Add("演算なし", Command.PhysicsOff);
                    Add("重力", Command.Gravity); Add("剛体表示", Command.ShowBodies); Add("剛体リセット", Command.ResetBodies);
                    break;
                case 6:
                    PopulateExtensionRibbon(ribbonTools);
                    break;
            }
            ribbonScroll.scrollOffset = Vector2.zero;
            ribbonScroll.EnableInClassList("hidden", ribbonCollapsed);
        }

        void RefreshRibbonActions()
        {
            bool ready = !importing && !exporting && modal == null && importDialog == null;
            foreach (var (button, available) in ribbonActions)
            {
                bool enabled = ready && (available == null || available());
                if (button.enabledSelf != enabled) button.SetEnabled(enabled);
            }
        }

        void RequestProfile(bool moving)
        {
            requestedProfile = moving;
            CompleteProfileSwitch();
        }

        void CompleteProfileSwitch()
        {
            if (!requestedProfile.HasValue || transformKey != KeyCode.None || dragging || draggedTrack != null) return;
            var captor = MenuRoot.panel.GetCapturingElement(PointerId.mousePointerId);
            if (captor != null) return;
            bool next = requestedProfile.Value;
            requestedProfile = null;
            if (next == ribbonProfile) return;
            SaveWorkspacePosition(); ApplyProfile(next);
            suppressedKeys.UnionWith(pressedKeys);
        }

        string ScrollKey => "Workspace."+(ribbonProfile?"Ribbon":"Classic");
        void SaveWorkspacePosition()
        {
            WorkspacePreferences.SetInt(ScrollKey+".Row",firstRow);WorkspacePreferences.SetInt(ScrollKey+".Frame",firstFrame);
        }

        void ApplyProfile(bool moving)
        {
            CloseTimelineMenu(); CloseMenu(); FocusHeading(null);
            ribbonProfile = moving;
            firstRow=WorkspacePreferences.GetInt(ScrollKey+".Row");
            PresentRibbonLayout(moving);PresentTimeWindows();PresentOperationWindow();
            SetFirstFrame(Mathf.Clamp(WorkspacePreferences.GetInt(ScrollKey+".Frame"), Mathf.Max(0, frame - VisibleColumns + 1), frame));
            RedrawOverlay(); SyncLayerSelector(); RefreshRibbonActions();
        }

        void CutKeys()
        {
            if (!EditKeys().Any()) return;
            CopyKey(); DeleteKeys();
        }

        void ToggleBookmark()
        {
            ChangeBookmark(frame,bookmarks.Contains(frame)?(int?)null:frame,null);
        }

        void JumpBookmark(bool next)
        {
            foreach (int at in next ? bookmarks : bookmarks.Reverse())
                if (next ? at > frame : at < frame) { SetFrame(at); return; }
        }

        void ToggleMarkers()
        {
            showMarkers = !showMarkers; WorkspacePreferences.SetInt("Ribbon.Markers",showMarkers ? 1 : 0); RedrawOverlay();
        }
        void ToggleExtendedHandles()
        {
            extendedHandles = !extendedHandles;
            WorkspacePreferences.SetInt("Ribbon.ExtendedHandles", extendedHandles ? 1 : 0);
            RedrawOverlay();
        }

        void ToggleHandles()
        {
            showHandles = !showHandles; WorkspacePreferences.SetInt("Ribbon.Handles",showHandles ? 1 : 0); RedrawOverlay();
        }

        void ShiftRibbonColumn(bool insert) => ShiftColumn(false, insert, selectedRows.Count > 0 ? selectedRows : VisibleTracks);

        void CycleEditingTarget(bool reverse)
        {
            int index = modelMode ? 1 + drawOrder.IndexOf(actor) :
                selectedTrack is MotionTrack<AccessoryPose> ? 1 + drawOrder.Count + props.FindIndex(p=>p.Track==selectedTrack) : 0;
            int count = 1 + drawOrder.Count + props.Count;
            int next = (index + count + (reverse ? -1 : 1)) % count;
            if (next > 0 && next <= drawOrder.Count) ChooseMode(drawOrder[next-1].label);
            else { ChangeMode(false); SelectTrack(next == 0 ? (MotionTrack)cameraTrack : props[next-1-drawOrder.Count].Track); }
        }

        void MirrorDraft()
        {
            if (!ModelBones()) return;
            var changes = new Dictionary<MotionTrack, object>();
            foreach (var bone in boneSelection.Count>0 ? boneSelection.AsEnumerable() : actor.bones)
            {
                var twin = MirroredTrack(bone.Track);
                if (twin != null) changes[twin] = MirrorPose(bone.Track.Current);
            }
            SetDraftValues(changes);
        }

        void SeekWithDraft(int to)
        {
            var draft = VisibleTracks.Where(t=>t.HasDraft(frame)).ToDictionary(t=>t,t=>t.CurrentValue);
            SetFrame(to);
            foreach (var pair in draft) pair.Key.Restore(pair.Value);
            RefreshRigPreview(); SyncControls(); RedrawOverlay();
        }

        bool RibbonShortcut(KeyCode key, bool ctrl, bool shift, bool other)
        {
            if (other || transformKey != KeyCode.None) return false;
            if (ctrl)
            {
                if (shift) return false;
                switch (key)
                {
                    case KeyCode.A: if(ModelBones()) SelectBoneSet(false); SelectKeys(0,MaxFrame,true); break;
                    case KeyCode.Period: JumpBookmark(true); break;
                    case KeyCode.Comma: JumpBookmark(false); break;
                    case KeyCode.M: ToggleBookmark(); break;
                    case KeyCode.X: CutKeys(); break;
                    case KeyCode.C: if(EditKeys().Any()) CopyKey(); break;
                    case KeyCode.V: case KeyCode.W: PasteOriginalKeys(key == KeyCode.W); break;
                    case KeyCode.S: Execute(Command.Save); break;
                    case KeyCode.Z: case KeyCode.Y: ApplyHistory(key == KeyCode.Y); break;
                    case KeyCode.LeftArrow: case KeyCode.RightArrow: JumpKey(key == KeyCode.RightArrow); break;
                    default: return false;
                }
                return true;
            }
            if (shift && key == KeyCode.A) { if(ModelBones()) SelectBoneSet(false); return true; }
            if (key == KeyCode.Tab) { CycleEditingTarget(shift); return true; }
            if (key == KeyCode.LeftArrow || key == KeyCode.RightArrow)
            {
                int to = frame + (key == KeyCode.RightArrow ? 1 : -1);
                if (shift) SeekWithDraft(to); else SetFrame(to);
                return true;
            }
            if (key == KeyCode.F1 || key == KeyCode.F2 || key == KeyCode.F3)
            {
                SetViewAngles(key == KeyCode.F2 ? (shift ? -90 : 90) : 0,
                    key == KeyCode.F1 ? (shift ? -90 : 90) : key == KeyCode.F3 && shift ? 180 : 0);
                return true;
            }
            if (shift) return false;
            switch (key)
            {
                case KeyCode.Z:
                    if(!modelMode && selectedTrack==cameraTrack && cameraTrack.Target==null)
                        CommitKeys(EditKeys().Where(k=>k.track==cameraTrack).Select(k=>{
                            var cam=(CameraPose)cameraTrack.KeyAt(k.frame);cam.Inactive=!cam.Inactive;
                            return new KeyChange{Track=cameraTrack,Frame=k.frame,After=cam};
                        }));
                    break;
                case KeyCode.Home: SetFrame(0); break;
                case KeyCode.End: if(!modelMode) SelectTrack(cameraTrack); break;
                case KeyCode.P: TogglePlayback(); break;
                case KeyCode.Return: case KeyCode.KeypadEnter: RegisterSelection(); break;
                case KeyCode.Delete: DeleteKeys(); break;
                case KeyCode.G: localAxes = !localAxes; SyncAxes(); break;
                case KeyCode.C: ToggleMarkers(); break;
                case KeyCode.X: ToggleHandles(); break;
                case KeyCode.S: if(ModelBones()) SelectBoneSet(true); break;
                case KeyCode.V: Execute(Command.TransparentModel); break;
                case KeyCode.W: MirrorDraft(); break;
                case KeyCode.D: Execute(Command.CenterBias); break;
                case KeyCode.E: if(ModelBones()) PreviewBone(BonePose.Rest); break;
                case KeyCode.I: case KeyCode.K: ShiftRibbonColumn(key == KeyCode.I); break;
                case KeyCode.UpArrow: case KeyCode.DownArrow:
                    int at = VisibleTracks.IndexOf(selectedTrack) + (key == KeyCode.DownArrow ? 1 : -1);
                    if (VisibleTracks.Count>0) SelectTrack(VisibleTracks[Mathf.Clamp(at,0,VisibleTracks.Count-1)]);
                    break;
                case KeyCode.Period: case KeyCode.Slash: case KeyCode.Backslash:
                case KeyCode.Semicolon: case KeyCode.Colon: case KeyCode.RightBracket:
                    if (!playing && (!modelMode || actor.selectedBone!=null))
                    {
                        transformKey = key;heldLabel=symbol.Present?symbol.Label:key;heldVirtual=symbol.vk;
                        holdPoint = NativeDesktopActive ? desktopPointer : RuntimePanelUtils.ScreenToPanel(MenuRoot.panel, new Vector2(Input.mousePosition.x,Screen.height-Input.mousePosition.y));
                        BeginDraftGesture();
                    }
                    break;
                default: return false;
            }
            return true;
        }

        void ApplyHeldDelta(KeyCode key, Vector2 delta)
        {
            bool rotate = key == KeyCode.Period || key == KeyCode.Slash || key == KeyCode.Backslash;
            int axis = key == KeyCode.Period || key == KeyCode.Semicolon ? 0 : key == KeyCode.Slash || key == KeyCode.Colon ? 1 : 2;
            float pixels=!rotate && axis==2?delta.y:-delta.y;
            if (pixels != 0) DragAxis(axis,rotate,pixels,keyboard:true);
        }

        void FinishHeldTransform()
        {
            transformKey = heldLabel = KeyCode.None;heldVirtual=0;
            FinishDraftGesture();
        }

        void ReleaseWorkspaceInput(bool focused)
        {
            if (focused) return;
            if (transformKey != KeyCode.None) FinishHeldTransform();
            FinishDraftSlider();
            pressedKeys.Clear();pressedSymbols.Clear(); suppressedKeys.Clear();
        }
    }
}
