using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp : MonoBehaviour
    {
        private PanelSettings panel;
        private VisualElement timeline, viewport, bone, morph, camera, light, accessory;
        private Label status, viewportTitle, viewportNote;
        private DropdownField modeField;
        private IntegerField frameField;
        private int frame;
        private bool modelMode;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindObjectOfType<DanceApp>() == null)
                new GameObject("AvaAvaDance").AddComponent<DanceApp>();
        }

        private void OnEnable()
        {
            var savedPanel = Resources.Load<PanelSettings>("MainPanel");
            panel = savedPanel != null ? Instantiate(savedPanel) : ScriptableObject.CreateInstance<PanelSettings>();
            var screenCamera = new GameObject("Screen background").AddComponent<Camera>();
            screenCamera.transform.SetParent(transform); screenCamera.cullingMask = 0; screenCamera.clearFlags = CameraClearFlags.SolidColor; screenCamera.backgroundColor = Color.gray;
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            var japaneseFont = Resources.Load<Font>("MPLUS1p-Regular");
            if (japaneseFont != null) { var textSettings = ScriptableObject.CreateInstance<PanelTextSettings>(); textSettings.defaultFontAsset = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(japaneseFont); panel.textSettings = textSettings; }
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("ClassicTheme");
            var document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            var root = document.rootVisualElement;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!Application.isBatchMode)
            {
                RefreshWindowScale();
                root.schedule.Execute(RefreshWindowScale).Every(250);
            }
#endif
            root.AddToClassList("root");
            root.RegisterCallback<KeyDownEvent>(MainMenuKey,TrickleDown.TrickleDown);
            root.styleSheets.Add(Resources.Load<StyleSheet>("Classic"));
            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scroll.style.flexGrow = 1;
            root.Add(scroll);
            var workspace = Box(scroll, "workspace", "workspace");
            workspace.focusable = true;
            var menu = Box(workspace, "menu", "row", "menu");
            BuildMmdMenus(menu);
            var main = Box(workspace, "main", "main");
            var sidebar = Box(main, "sidebar", "sidebar");
            var header = new Label("ボーン・フレーム操作");
            header.AddToClassList("header"); header.AddToClassList("frame-header"); sidebar.Add(header);
            BuildTimeline(sidebar);
            BuildInterpolation(sidebar);

            var right = Box(main, "right", "right");
            viewport = Box(right, "viewport", "viewport");
            viewportTitle = new Label(); viewportTitle.AddToClassList("viewport-title"); viewport.Add(viewportTitle);
            var note = new Label("アバター未読込。モデル操作の「読込」からモデルを選択。"); viewportNote = note;
            note.AddToClassList("viewport-note"); viewport.Add(note);
            BuildTransformBar(right);
            var bottom = Box(right, "bottom", "bottom");
            var model = Panel(bottom, "model", "モデル操作");
            modeField = Choice(model, new[] { SceneModeName });
            modeField.RegisterValueChangedCallback(evt => ChooseMode(evt.newValue));
            Buttons(model, "読込", "削除");
            visibilityToggle = new Toggle("表示");
            model.Add(visibilityToggle);
            visibilityToggle.RegisterValueChangedCallback(evt => {
                if(actor.modelSwitch==null)return;
                StopPlayback();
                var state=actor.modelSwitch.Current; state.Hidden=!evt.newValue; actor.modelSwitch.Preview(state); RedrawOverlay();
            });
            modelSelfToggle = new Toggle("セルフ影") { value = true }; model.Add(modelSelfToggle);
            modelSelfToggle.RegisterValueChangedCallback(e => { actor.modelSelfShadow=e.newValue; ApplyShadow(shadowTrack.Current); });
            BuildRigControls(model);
            BuildBoneControls(bottom);
            BuildMorphControls(bottom);
            BuildSceneControls(bottom);
            BuildShadowPanel(bottom);
            BuildAccessories(bottom);
            var playback = Panel(bottom, "playback", "視点");
            BuildViewControls(playback);
            BuildPlayback(playback);
            BuildWaveControls(playback);
            status = new Label("モデル操作の「読込」からモデルを選択してください。");
            status.AddToClassList("status"); workspace.Add(status);
            SetupAvatarImport(model, viewport);
            EnsurePreview();
            BuildSceneTracks(); InitEnvironment(); InitView();
            ChangeMode(false); workspace.schedule.Execute(workspace.Focus); MarkSaved();
            Application.wantsToQuit += QuitProject;
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private IntPtr playerWindow;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        private void RefreshWindowScale()
        {
            if (playerWindow == IntPtr.Zero)
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                playerWindow = process.MainWindowHandle;
                if (playerWindow == IntPtr.Zero) return;
            }

            uint dpi = GetDpiForWindow(playerWindow);
            if (dpi == 0)
            {
                playerWindow = IntPtr.Zero;
                return;
            }

            float scale = dpi / 96f;
            if (panel.scale == scale) return;
            panel.scale = scale;
            viewport?.schedule.Execute(ResizePreview);
        }
#endif

        private const string SceneModeName = "カメラ・照明・アクセサリ";
        private Button modeSwitch;
        private Toggle visibilityToggle;

        private void ChangeMode(bool model)
        {
            modelMode = model && actor.avatar != null;
            modeField.choices = new List<string> { SceneModeName }; foreach(var item in drawOrder) modeField.choices.Add(item.label);
            modeField.SetValueWithoutNotify(modelMode ? actor.label : SceneModeName);
            modeField.SetEnabled(actor.avatar != null);
            modeSwitch.text = modelMode ? "カメラ編" : "モデル編";
            modeSwitch.SetEnabled(actor.avatar != null);
            visibilityToggle.SetEnabled(actor.avatar != null);
            visibilityToggle.SetValueWithoutNotify(actor.avatar != null && actor.avatar.activeSelf);
            bone.EnableInClassList("hidden", !modelMode); morph.EnableInClassList("hidden", !modelMode);
            camera.EnableInClassList("hidden", modelMode); light.EnableInClassList("hidden", modelMode);
            shadowPanel.EnableInClassList("hidden", modelMode);
            accessory.EnableInClassList("hidden", modelMode);
            firstRow = 0;
            SelectTrack(modelMode ? (actor.modelSelection ?? (MotionTrack)actor.selectedBone?.Track ?? actor.modelTracks.Find(t => t is MotionTrack<float>)) : sceneSelection);
            ApplyView();
        }

        private static VisualElement Box(VisualElement parent, string name, params string[] classes)
        {
            var element = new VisualElement { name = name };
            foreach (var c in classes) element.AddToClassList(c);
            parent.Add(element); return element;
        }
        private static VisualElement Panel(VisualElement parent, string type, string title)
        {
            var panel = Box(parent, type, "panel", type);
            var label = new Label(title); label.AddToClassList("header"); panel.Add(label); return panel;
        }
        private static Button Action(VisualElement parent, string text, bool enabled, System.Action callback = null)
        {
            var button = new Button(callback) { text = text, tooltip = callback != null || enabled ? text : text + "：未実装（配置確認用）" };
            button.SetEnabled(enabled); parent.Add(button); return button;
        }
        private static void Buttons(VisualElement parent, params string[] names)
        {
            var row = Box(parent, "buttons", "row");
            foreach (var name in names) Action(row, name, false);
        }
        private static DropdownField Choice(VisualElement parent, string[] choices)
        {
            var field = new DropdownField(new List<string>(choices), 0);
            if (choices.Length == 1) field.SetEnabled(false);
            parent.Add(field); return field;
        }
        private static void Line(Painter2D painter, Vector2 start, Vector2 end, Color color)
        {
            painter.strokeColor = color; painter.lineWidth = 1;
            painter.BeginPath(); painter.MoveTo(start); painter.LineTo(end); painter.Stroke();
        }
        private void OnDestroy() { ClearBullet(); Application.wantsToQuit -= QuitProject; if (soundtrack != null && soundtrack.clip != null) Destroy(soundtrack.clip); DisposeView(); DisposePreview(); if (panel != null) Destroy(panel); }
    }
}
