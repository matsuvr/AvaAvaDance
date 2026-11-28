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
            screenCamera.transform.SetParent(transform); screenCamera.cullingMask = 0; screenCamera.clearFlags = CameraClearFlags.SolidColor; screenCamera.backgroundColor = new Color32(36, 39, 43, 255);
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            var japaneseFont = Resources.Load<Font>("MPLUS1p-Regular");
            if (japaneseFont != null) { var textSettings = ScriptableObject.CreateInstance<PanelTextSettings>(); textSettings.defaultFontAsset = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(japaneseFont); panel.textSettings = textSettings; }
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("ClassicTheme");
            var document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            var root = document.rootVisualElement;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!Application.isBatchMode && !NativeDesktopActive)
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
            var workspace = scroll.contentContainer;
            workspace.name = "workspace";
            workspace.AddToClassList("workspace");
            workspace.style.flexGrow = 0;
            bool measuringStartup = false;
            void ResizeWorkspace()
            {
                if (measuringStartup) return;
                float width = scroll.contentViewport.contentRect.width;
                float height = root.contentRect.height;
                if (width <= 0 || height <= 0) return;
                workspace.style.width = Mathf.Max(0, width);
                ResizeAnimationWorkspace(workspace, width, height);
            }
            root.RegisterCallback<GeometryChangedEvent>(_ => ResizeWorkspace());
            scroll.RegisterCallback<GeometryChangedEvent>(_ => ResizeWorkspace());
            scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => ResizeWorkspace());
            workspace.focusable = true;
            BuildAnimationWorkspace(workspace);
            status = new Label("準備完了");
            var help = Box(workspace, "help-line", "help-line", "row");
            status.name = "status"; status.AddToClassList("status"); help.Add(status);
            var hint = new Label { name = "hover-help", pickingMode = PickingMode.Ignore };
            help.Add(hint);
            root.RegisterCallback<PointerOverEvent>(e => {
                string text = "";
                for (var item = e.target as VisualElement; item != null && item != root; item = item.parent)
                {
                    if (string.IsNullOrEmpty(item.tooltip)) continue;
                    text = item.tooltip; break;
                }
                hint.text = text;
            });
            root.RegisterCallback<PointerLeaveEvent>(_ => hint.text = "");
            SetupAvatarImport(workspace.Q("model"), viewport);
            EnsurePreview();
            BuildSceneTracks(); InitEnvironment(); InitView(); InitSurfaceRendering();
            ChangeMode(false); ApplyProfile(true); ResizeWorkspace(); workspace.schedule.Execute(workspace.Focus); MarkSaved();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!NativeDesktopActive && UseMainWindowPlacement())
                StartCoroutine(OpenMainWindow(workspace, measuring => {
                    measuringStartup = measuring;
                    if (!measuring) ResizeWorkspace();
                }));
#endif
            Application.wantsToQuit += QuitProject;
            StartMcp();
            StartNativeDesktop();
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
                physicalMainKeys=ava_editor_keyboard(playerWindow)!=0;
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
            camera.EnableInClassList("hidden", false); light.EnableInClassList("hidden", false);
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
        private static void Line(DesktopPainter painter, Vector2 start, Vector2 end, Color color)
        {
            painter.strokeColor = color; painter.lineWidth = 1;
            painter.BeginPath(); painter.MoveTo(start); painter.LineTo(end); painter.Stroke();
        }
        private void OnDestroy() { StopNativeDesktop(); ReleaseNativeLayout(); StopMcp(); if(physicalMainKeys)ava_editor_keyboard(IntPtr.Zero); SaveWorkspacePosition(); operations?.Dispose();floatingTime?.Dispose();floatingWave?.Dispose(); DisposeExtensions(); DisposeSurfaceRendering(); Application.wantsToQuit -= QuitProject; if (soundtrack != null && soundtrack.clip != null) Destroy(soundtrack.clip); DisposeView(); DisposePreview(); if (panel != null) Destroy(panel); }
    }
}
