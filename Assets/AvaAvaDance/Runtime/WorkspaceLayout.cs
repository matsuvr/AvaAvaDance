using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        enum WorkspacePreset { Animation, Rigging, Compact }
        enum InspectorTab { Channels, Expressions, Scene, Curve }
        const float CompactWorkspaceWidth = WindowPlacement.CompactWorkspaceWidth;
        const int DefaultInspectorWidth = 270;
        const int DefaultViewportPercent = 58;
        VisualElement workspaceBody, workspaceStage, viewportPane, keyframeEditor, inspectorSplitter;
        VisualElement rightDock, dockContent, inlineCurvePane, graphPane, layerEditor, layerList;
        VisualElement channelDetails, sceneAttributes, sceneKeyActions;
        VisualElement[] inspectorPages;
        Button[] inspectorTabs;
        DropdownField workspaceChoice;
        Label channelTarget;
        VisualElement orientationAxes;
        WorkspacePreset workspacePreset;
        InspectorTab inspectorTab;
        bool inspectorCollapsed;
        float workspaceWidth = 1280;
        int inspectorWidth, viewportPercent;

        void BuildAnimationWorkspace(VisualElement workspace)
        {
            var menu = Box(workspace, "menu", "row", "menu");
            LoadExtensions();
            BuildWorkspaceMenus(menu);
            var statusLine = BuildStatusLine(workspace);
            BuildRibbon(workspace);

            workspaceBody = Box(workspace, "body", "workspace-body");
            workspaceStage = Box(workspaceBody, "stage", "workspace-stage");
            var toolbox = Box(workspaceStage, "toolbox", "toolbox");
            BuildOutliner(workspaceStage);
            var center = Box(workspaceStage, "center-column", "center-column");
            viewportPane = Box(center, "viewport-pane", "viewport-pane");
            BuildViewportChrome(viewportPane);
            viewport = Box(viewportPane, "viewport", "viewport");
            viewportTitle = new Label(); viewportTitle.AddToClassList("viewport-title"); viewport.Add(viewportTitle);
            viewportNote = new Label("モデルを読み込むには、Create メニューを開くか、ファイルをここへドロップしてください。");
            viewportNote.AddToClassList("viewport-note"); viewport.Add(viewportNote);
            orientationAxes = Box(viewport, "orientation", "orientation");
            orientationAxes.pickingMode = PickingMode.Ignore;
            BindDrawing(orientationAxes, DrawOrientationAxes);
            var vertical = Box(center, "viewport-splitter", "workspace-splitter", "horizontal-splitter");

            keyframeEditor = Box(center, "keyframe-editor", "keyframe-editor");
            var editorHeader = Box(keyframeEditor, "keyframe-header", "row", "keyframe-header");
            var editorTools = Box(keyframeEditor, "keyframe-tools", "keyframe-tools", "hidden");
            var keyframeBody = Box(keyframeEditor, "keyframe-body", "row", "keyframe-body");
            var dopeSheet = Box(keyframeBody, "dopesheet-pane", "dopesheet-pane");
            graphPane = Box(keyframeBody, "graph-pane", "graph-pane");
            inlineCurvePane = Box(keyframeBody, "inline-curve-pane", "inline-curve-pane");
            var transportArea = Box(keyframeEditor, "time-slider-bar", "time-slider-bar");
            BuildTransport(transportArea);
            BuildTimeline(dopeSheet, editorTools, transport);
            BuildFrameBookmark(transport);
            BuildInterpolation(inlineCurvePane, graphPane);
            BuildRibbonTimelineTabs(editorHeader, dopeSheet, editorTools);
            var range = Box(keyframeEditor, "range-slider-bar", "row", "range-slider-bar");
            BuildPlayback(range);
            BuildWaveControls(range);
            ProfileAction(editorTools, "登録", RegisterSelection, () => selectedTrack != null);
            ProfileAction(editorTools, "切り取り", CutKeys, () => EditKeys().Any(k => k.frame > 0));
            ProfileAction(editorTools, "列挿入", () => ShiftRibbonColumn(true));
            ProfileAction(editorTools, "列削除", () => ShiftRibbonColumn(false));
            ProfileAction(editorTools, "時間伸縮", () => Execute(Command.ScaleKeyframes));
            ProfileAction(editorTools, "ブックマーク", ToggleBookmark);
            ProfileAction(editorTools, "前のブックマーク", () => JumpBookmark(false));
            ProfileAction(editorTools, "次のブックマーク", () => JumpBookmark(true));
            ProfileAction(editorTools, "独立タイムライン", () => ToggleTimeWindow(false));

            inspectorSplitter = Box(workspaceBody, "inspector-splitter", "workspace-splitter", "vertical-splitter");
            BuildInspector(workspaceBody);
            BuildToolbox(toolbox);
            autoCurve.RemoveFromHierarchy();
            statusLine.rig.Add(autoCurve);
            modeSwitch = Action(statusLine.mode, "モデル編", false, () => ChangeMode(!modelMode));
            modeField = Choice(statusLine.mode, new[] { SceneModeName });
            modeField.name = "active-model";
            modeField.RegisterValueChangedCallback(e => ChooseMode(e.newValue));

            var settings = WindowPlacement.WorkspaceDefaults(WorkspacePreferences.GetInt("Workspace.Preset"),
                WorkspacePreferences.GetInt("Workspace.ViewportPercent", DefaultViewportPercent),
                WorkspacePreferences.GetInt("Workspace.InspectorWidth", DefaultInspectorWidth));
            workspacePreset = (WorkspacePreset)settings.preset;
            viewportPercent = settings.viewport;
            inspectorWidth = settings.dock;
            workspaceChoice = Choice(statusLine.tools, new[] { "Animation", "Rigging", "Compact" });
            workspaceChoice.name = "workspace-preset";
            workspaceChoice.tooltip = "Workspace";
            workspaceChoice.index = (int)workspacePreset;
            workspaceChoice.RegisterValueChangedCallback(_ => ChooseWorkspace((WorkspacePreset)workspaceChoice.index));
            Action(statusLine.tools, "Dock", true, () => SelectInspector(inspectorTab, true));
            outlinerButton = Action(statusLine.tools, "Outliner", true, ShowOutliner);

            BindSplitter(vertical, true, () => viewportPane.layout.height, pixels => {
                viewportPercent = WindowPlacement.WorkspaceDefaults(0, Mathf.RoundToInt(100 * pixels / Mathf.Max(1, center.layout.height)), inspectorWidth).viewport;
                ApplyWorkspaceSizing();
            }, () => WorkspacePreferences.SetInt("Workspace.ViewportPercent", viewportPercent));
            BindSplitter(inspectorSplitter, false, () => inspectorWidth, pixels => {
                inspectorWidth = WindowPlacement.WorkspaceDefaults(0, viewportPercent, Mathf.RoundToInt(pixels)).dock;
                ApplyWorkspaceSizing();
            }, () => WorkspacePreferences.SetInt("Workspace.InspectorWidth", inspectorWidth));
            SelectInspector(InspectorTab.Channels);
            ApplyWorkspaceSizing();
        }

        void BuildInspector(VisualElement parent)
        {
            rightDock = Box(parent, "right-dock", "right-dock");
            dockContent = Box(rightDock, "dock-content", "dock-content");
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.name = "inspector-scroll";
            scroll.AddToClassList("inspector-scroll");
            dockContent.Add(scroll);
            var rail = Box(rightDock, "dock-tabs-rail", "dock-tabs-rail");
            string[] labels = { "Channel Box", "Expressions", "Scene / Render", "Interpolation Curves" };
            string[] ids = { "channel", "expressions", "scene", "curve" };
            string[] glyphs = { "C", "E", "S", "G" };
            inspectorPages = new VisualElement[labels.Length];
            inspectorTabs = new Button[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                var tab = (InspectorTab)i;
                inspectorPages[i] = Box(scroll, "tab-" + ids[i], "inspector-page");
                var button = Action(rail, glyphs[i], true, () => SelectInspector(tab, true));
                button.name = "dock-tab-" + ids[i];
                button.tooltip = labels[i];
                inspectorTabs[i] = button;
                inspectorPages[i].Add(new Label(labels[i]) { name = "inspector-title" });
            }

            sharedBottom = inspectorPages[0];
            channelTarget = new Label(); channelTarget.AddToClassList("channel-target"); sharedBottom.Add(channelTarget);
            BuildTransformBar(sharedBottom);
            BuildModelScale(sharedBottom);
            BuildBoneControls(sharedBottom);
            sceneKeyActions = Box(sharedBottom, "scene-key-actions", "row");
            ProfileAction(sceneKeyActions, "登録", RegisterSelection, () => selectedTrack != null);
            ProfileAction(sceneKeyActions, "回転をリセット", () => Execute(Command.ZeroAngles));
            channelDetails = Box(sharedBottom, "channel-details");
            Action(sceneKeyActions, "シーン属性", true, () => SelectInspector(InspectorTab.Scene));
            layerEditor = Box(sharedBottom, "layer-editor", "layer-editor");
            layerEditor.Add(new Label("Layer Editor") { name = "layer-editor-title" });
            var layerTabs = Box(layerEditor, "layer-tabs", "row");
            layerList = Box(layerEditor, "animation-layers", "layer-list");
            var model = Panel(layerEditor, "model", "Models / IK");
            Button anim = null, models = null;
            anim = Action(layerTabs, "Anim", true, () => ChooseLayerTab(false));
            models = Action(layerTabs, "Models / IK", true, () => ChooseLayerTab(true));
            void ChooseLayerTab(bool showModels)
            {
                layerList.EnableInClassList("hidden", showModels);
                model.EnableInClassList("hidden", !showModels);
                anim.EnableInClassList("active-tool", !showModels);
                models.EnableInClassList("active-tool", showModels);
            }
            ChooseLayerTab(false);
            Buttons(model, "読込", "削除");
            visibilityToggle = new Toggle("表示"); model.Add(visibilityToggle);
            visibilityToggle.RegisterValueChangedCallback(e => {
                if (actor.modelSwitch == null) return;
                StopPlayback();
                var state = actor.modelSwitch.Current;
                state.Hidden = !e.newValue;
                actor.modelSwitch.Preview(state);
                RedrawOverlay();
            });
            modelSelfToggle = new Toggle("セルフ影") { value = true }; model.Add(modelSelfToggle);
            modelSelfToggle.RegisterValueChangedCallback(e => { actor.modelSelfShadow = e.newValue; ApplyShadow(shadowTrack.Current); });
            BuildRigControls(model);

            BuildMorphControls(inspectorPages[1]);
            var morphActions = Box(inspectorPages[1], "expression-actions", "row", "expression-actions");
            Action(morphActions, "全表情登録", true, () => Execute(Command.RegisterMorphs));
            Action(morphActions, "リセット", true, () => Execute(Command.ResetMorphs));
            Action(morphActions, "リップシンク", true, () => Execute(Command.LipSync));
            Action(morphActions, "まばたき", true, () => Execute(Command.Blink));
            sceneAttributes = Box(inspectorPages[2], "scene-attributes");
            BuildSceneControls(sceneAttributes);
            BuildShadowPanel(sceneAttributes);
            BuildAccessories(sceneAttributes);
            foreach (var panel in new[] { camera, light, shadowPanel, accessory })
                MakeCollapsible(panel, panel != accessory);
            var view = Panel(inspectorPages[2], "view-options", "View");
            BuildViewControls(view);
            MakeCollapsible(view, false);
        }

        static void MakeCollapsible(VisualElement panel, bool open)
        {
            var heading = (Label)panel[0];
            var children = panel.Children().Skip(1).ToArray();
            panel.Clear();
            var foldout = new Foldout { text = heading.text, value = open };
            foldout.AddToClassList("inspector-foldout");
            panel.Add(foldout);
            foreach (var child in children) foldout.Add(child);
        }

        void RefreshInspector()
        {
            if (channelTarget == null) return;
            channelTarget.text = selectedTrack?.Name ?? "選択対象なし";
            PresentChannelTarget();
            layerList.Clear();
            var track = selectedTrack;
            if (track?.SupportsLayers != true) return;
            var activeClip = track.Target?.clips.Find(clip => clip.id == track.ActiveClip);
            string destination = track.Target == null ? "Base Animation" : track.Target.name;
            if (activeClip != null)
            {
                int number = track.Target.clips.IndexOf(activeClip) + 1;
                destination += $"  /  Clip {number}  [{activeClip.RangeLabel}]";
            }
            layerList.Add(new Label("登録先　" + destination) { name = "layer-destination", tooltip = destination });
            if (activeClip != null)
            {
                string weight = activeClip.weight.ToString("P0");
                layerList.Add(new Label($"クリップ重み　{weight}" + (activeClip.muted ? "  •  ミュート" : "")) { name = "clip-weight" });
            }
            void Row(string name, MotionLayer layer)
            {
                var row = Box(layerList, "", "row", "animation-layer");
                var button = Action(row, name, true, () => {
                    track.ActiveLayer = layer?.id;
                    if (layer == null || !layer.clips.Any(clip => clip.id == track.ActiveClip))
                        track.ActiveClip = layer?.clips.FirstOrDefault()?.id;
                    ChangeLayerContext();
                });
                button.AddToClassList("layer-name");
                button.EnableInClassList("active-tool", track.Target == layer);
                if (layer == null) return;
                var mute = Action(row, layer.muted ? "M" : "●", true, () => {
                    var updated = track.Layers.ToList();
                    var replacement = JsonUtility.FromJson<MotionLayer>(JsonUtility.ToJson(layer));
                    replacement.muted = !layer.muted;
                    updated[updated.IndexOf(layer)] = replacement;
                    ApplyLayerEdit(track, updated, track.ActiveLayer, track.ActiveClip);
                });
                mute.tooltip = layer.muted ? "レイヤーのミュートを解除" : "レイヤーをミュート";
            }
            Row("Base Animation", null);
            for (int i = 0; i < track.Layers.Count; i++) Row($"{i + 1}. {track.Layers[i].name}", track.Layers[i]);
            var actions = Box(layerList, "layer-actions", "row");
            Action(actions, "+ Layer", true, () => EditLayer(true));
            Action(actions, "設定", track.Target != null, () => EditLayer(false));
            Action(actions, "Clip…", track.Target != null, EditClip);
        }

        void PresentChannelTarget()
        {
            VisualElement details = selectedTrack switch {
                MotionTrack<CameraPose> => camera,
                MotionTrack<LightPose> => light,
                MotionTrack<AccessoryPose> => accessory,
                _ => null
            };
            bool showInChannel = !inspectorCollapsed && inspectorTab == InspectorTab.Channels;
            foreach (var section in new[] { camera, light, shadowPanel, accessory })
            {
                var parent = showInChannel && section == details ? channelDetails : sceneAttributes;
                if (section.parent != parent)
                {
                    section.RemoveFromHierarchy();
                    if (parent == sceneAttributes)
                    {
                        var order = new[] { camera, light, shadowPanel, accessory };
                        int index = sceneAttributes.Children().Count(item => Array.IndexOf(order, item) < Array.IndexOf(order, section));
                        parent.Insert(index, section);
                    }
                    else parent.Add(section);
                }
            }
            bool showTransform = HasNumericChannels(selectedTrack);
            MenuRoot.Q("transform-bar").EnableInClassList("hidden", !showTransform);
            modelScaleField.EnableInClassList("hidden", !modelMode || selectedTrack != actor.modelSwitch);
            if (modelMode) modelScaleField.SetValueWithoutNotify(actor.placementScale);
            sceneKeyActions.EnableInClassList("hidden", modelMode || selectedTrack == null);
            channelDetails.EnableInClassList("hidden", details == null);
        }

        void SelectInspector(InspectorTab tab, bool toggle = false)
        {
            if (KeysCaptured()) return;
            inspectorCollapsed = toggle && inspectorTab == tab && !inspectorCollapsed;
            inspectorTab = tab;
            for (int i = 0; i < inspectorPages.Length; i++)
            {
                inspectorPages[i].EnableInClassList("hidden", i != (int)tab);
                inspectorTabs[i].EnableInClassList("active-tool", !inspectorCollapsed && i == (int)tab);
            }
            MoveCurveControls();
            PresentChannelTarget();
            dockContent.EnableInClassList("hidden", inspectorCollapsed);
            rightDock.EnableInClassList("dock-collapsed", inspectorCollapsed);
            ApplyWorkspaceSizing();
        }

        void MoveCurveControls()
        {
            var row = MenuRoot.Q("interpolation");
            var parent = !inspectorCollapsed && inspectorTab == InspectorTab.Curve ? inspectorPages[3] : inlineCurvePane;
            if (row.parent != parent) { row.RemoveFromHierarchy(); parent.Add(row); }
            inlineCurvePane.EnableInClassList("hidden", !showRibbonCurve || audioDockTab || graphDockTab || parent != inlineCurvePane);
        }

        void ChooseWorkspace(WorkspacePreset preset)
        {
            if (KeysCaptured())
            {
                workspaceChoice.SetValueWithoutNotify(workspaceChoice.choices[(int)workspacePreset]);
                return;
            }
            workspacePreset = preset;
            workspaceChoice.SetValueWithoutNotify(workspaceChoice.choices[(int)preset]);
            WorkspacePreferences.SetInt("Workspace.Preset", (int)preset);
            SelectInspector(InspectorTab.Channels);
            ApplyWorkspaceSizing();
        }

        bool KeysCaptured() => transformKey != KeyCode.None || dragging || draggedTrack != null || draftSlider != null;

        void ResetAnimationLayout()
        {
            foreach (var key in new[] { "Workspace.Preset", "Workspace.ViewportPercent", "Workspace.InspectorWidth", "Workspace.InlineCurve", "Workspace.Outliner" })
                WorkspacePreferences.DeleteKey(key);
            outlinerOpen = false;
            viewportPercent = DefaultViewportPercent;
            inspectorWidth = DefaultInspectorWidth;
            ChooseWorkspace(WorkspacePreset.Animation);
        }

        void ResizeAnimationWorkspace(VisualElement workspace, float width, float height)
        {
            if (workspaceStage == null || !float.IsFinite(width) || !float.IsFinite(height)) return;
            workspaceWidth = width;
            statusControls.style.minWidth = Mathf.Max(0, width - 2);
            workspace.EnableInClassList("compact-workspace", width < CompactWorkspaceWidth);
            workspace.style.minHeight = Mathf.Max(0, height);
            ApplyWorkspaceSizing();
        }

        void ApplyWorkspaceSizing()
        {
            if (rightDock == null) return;
            bool stacked = WindowPlacement.StackWorkspace((int)workspacePreset, workspaceWidth);
            workspaceBody.EnableInClassList("stacked-workspace", stacked);
            outliner.EnableInClassList("hidden", !outlinerOpen);
            outliner.style.width = stacked ? Mathf.Clamp(workspaceWidth * .26f, 100, 180) : 216;
            outlinerButton?.EnableInClassList("active-tool", outlinerOpen);
            RefreshOutliner();
            rightDock.style.width = stacked ? new StyleLength(StyleKeyword.Auto) : new StyleLength(inspectorCollapsed ? 24 : inspectorWidth);
            inspectorSplitter.EnableInClassList("hidden", stacked || inspectorCollapsed);
            float ratio = WindowPlacement.ViewportShare((int)workspacePreset, viewportPercent) / 100f;
            viewportPane.style.flexGrow = ratio;
            keyframeEditor.style.flexGrow = 1 - ratio;
        }

        static void BindSplitter(VisualElement splitter, bool horizontal, Func<float> initial, Action<float> resize, Action save)
        {
            Vector2 origin = default;
            float size = 0;
            splitter.RegisterCallback<PointerDownEvent>(e => {
                if (e.button != 0) return;
                origin = e.position; size = initial();
                splitter.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            splitter.RegisterCallback<PointerMoveEvent>(e => {
                if (splitter.HasPointerCapture(e.pointerId))
                    resize(size + (horizontal ? e.position.y - origin.y : origin.x - e.position.x));
            });
            splitter.RegisterCallback<PointerUpEvent>(e => {
                if (splitter.HasPointerCapture(e.pointerId)) splitter.ReleasePointer(e.pointerId);
            });
            splitter.RegisterCallback<PointerCaptureOutEvent>(_ => save());
        }

        void BuildToolbox(VisualElement parent)
        {
            var tools = bone.Q("bone-tools");
            tools.RemoveFromHierarchy(); parent.Add(tools);
            string[] labels = { "Q", "E", "W" };
            for (int i = 0; i < toolButtons.Length; i++)
            {
                toolButtons[i].tooltip = toolButtons[i].text;
                toolButtons[i].text = labels[i];
            }
            Action(parent, "L/G", true, ToggleCoordinateSpace).tooltip = "ローカル / ワールド";
            Action(parent, "Flip", true, MirrorDraft).tooltip = "ポーズ反転";
            Action(parent, "Rest", true, () => { if (ModelBones()) PreviewBone(BonePose.Rest); }).tooltip = "姿勢を初期化";
            var views = Action(parent, "View", true);
            views.clicked += () => ShowViewportMenu(views, ViewChoices());
            Action(parent, "Grid", true, () => Execute(Command.Grid)).tooltip = "座標軸 / 床グリッド";
            Action(parent, "Pop", true, () => Execute(Command.SeparateWindow)).tooltip = "別窓プレビュー";
            Action(parent, "Keys", true, () => ToggleTimeWindow(false)).tooltip = "独立タイムライン";
            Action(parent, "WAV", true, () => ToggleTimeWindow(true)).tooltip = "独立オーディオ";
            Action(parent, "Ops", true, ToggleDock).tooltip = "独立操作パネル";
        }

        void FocusSelectedTarget() => FrameViewport(true);

        void ToggleCoordinateSpace() { localAxes = !localAxes; SyncAxes(); }

        (string, Action)[] ViewChoices() => new (string, Action)[] {
            ("正面", () => SetViewAngles(0, 0)), ("背面", () => SetViewAngles(0, 180)),
            ("上面", () => SetViewAngles(90, 0)), ("下面", () => SetViewAngles(-90, 0)),
            ("左面", () => SetViewAngles(0, -90)), ("右面", () => SetViewAngles(0, 90)),
            ("アニメーションカメラ", ShowAnimatedCamera),
            ("全体を表示 (A)", () => FrameViewport(false)),
            ("選択対象を表示 (F)", FocusSelectedTarget),
            ("視点追従", () => Execute(Command.ViewTargetFollow)),
            ("全画面", () => Execute(Command.Fullscreen))
        };

        void BuildViewportChrome(VisualElement parent)
        {
            var menus = Box(parent, "viewport-menu", "row", "viewport-menu");
            var toolbar = Box(parent, "viewport-toolbar", "row", "viewport-toolbar");
            void Menu(string title, Func<(string, Action)[]> items)
            {
                var button = Action(menus, title, true);
                button.clicked += () => ShowViewportMenu(button, items());
            }
            void Commands(string title, params Command[] ids) => Menu(title, () =>
                ids.Select(id => (MenuEntryName(id), (Action)(() => Execute(id)))).ToArray());
            Menu("View", ViewChoices);
            Commands("Shading", Command.Wireframe, Command.TransparentModel, Command.EdgeWidth, Command.EdgeColor, Command.Antialias);
            Commands("Lighting", Command.SelfShadow, Command.GroundShadow, Command.ShadowColor);
            Commands("Show", Command.Grid, Command.HideModel, Command.ShowBodies, Command.ShowImage, Command.ShowVideo);
            Commands("Preview / Output", Command.OutputSize, Command.Limit30, Command.Limit60, Command.Unlimited);
            Commands("Panels", Command.FollowCamera, Command.SeparateWindow, Command.Topmost);
            void Tool(string label, Command command) => ProfileAction(toolbar, label, () => Execute(command), () => Available(command));
            Tool("Grid", Command.Grid); Tool("Wire", Command.Wireframe); Tool("Alpha", Command.TransparentModel);
            ProfileAction(toolbar, "Bones", ToggleMarkers); ProfileAction(toolbar, "IK", ToggleHandles);
            Tool("Bodies", Command.ShowBodies); Tool("Shadow", Command.SelfShadow);
            Tool("Floor", Command.GroundShadow); Tool("Camera", Command.FollowCamera); Tool("Pop", Command.SeparateWindow);
        }

        static void ShowViewportMenu(Button anchor, (string name, Action invoke)[] items)
        {
            var menu = new GenericDropdownMenu();
            foreach (var item in items) menu.AddItem(item.name, false, item.invoke);
            menu.DropDown(anchor.worldBound, anchor, false);
        }

        void DrawOrientationAxes(DesktopPainter painter)
        {
            if (previewCamera == null) return;
            var origin = new Vector2(24, 24);
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            var colors = new[] { new Color32(245, 112, 110, 255), new Color32(147, 222, 139, 255), new Color32(119, 180, 242, 255) };
            for (int i = 0; i < axes.Length; i++)
            {
                Vector3 direction = previewCamera.transform.InverseTransformDirection(axes[i]);
                var end = origin + 17 * new Vector2(direction.x, -direction.y);
                Line(painter, origin, end, colors[i]);
                painter.fillColor = colors[i];
                painter.BeginPath();
                painter.Circle(end, 2.5f);
                painter.Fill();
            }
        }
    }
}
