using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    /// <summary>Runtime layout prototype. No avatar, motion or file IO is performed.</summary>
    public sealed class DanceApp : MonoBehaviour
    {
        private PanelSettings panel;
        private VisualElement timeline, viewport, bone, morph, camera, light, accessory;
        private Label status, viewportTitle;
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
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("ClassicTheme");
            var document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            var root = document.rootVisualElement;
            root.AddToClassList("root");
            root.styleSheets.Add(Resources.Load<StyleSheet>("Classic"));
            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scroll.style.flexGrow = 1;
            root.Add(scroll);
            var workspace = Box(scroll, "workspace", "workspace");
            var menu = Box(workspace, "menu", "row", "menu");
            foreach (var title in new[] { "ファイル", "編集", "表示", "背景", "表情", "物理演算", "モーション", "ヘルプ" })
                Action(menu, title, false);

            var main = Box(workspace, "main", "main");
            var sidebar = Box(main, "sidebar", "sidebar");
            var header = new Label("ボーン・フレーム操作");
            header.AddToClassList("header"); header.AddToClassList("frame-header"); sidebar.Add(header);
            Buttons(sidebar, "元に戻す", "やり直し");
            var frameRow = Box(sidebar, "frame-row", "row");
            Action(frameRow, "◀", true, () => SetFrame(frame - 1));
            frameField = new IntegerField { value = 0, tooltip = "現在フレーム（UIのみ）" };
            frameField.style.width = 130; frameRow.Add(frameField);
            frameField.RegisterValueChangedCallback(evt => SetFrame(evt.newValue));
            Action(frameRow, "▶", true, () => SetFrame(frame + 1));
            timeline = Box(sidebar, "timeline", "timeline");
            timeline.generateVisualContent += DrawTimeline;
            timeline.RegisterCallback<PointerDownEvent>(evt =>
            {
                SetFrame(Mathf.RoundToInt((evt.localPosition.x - 84) / 8));
                evt.StopPropagation();
            });
            Buttons(sidebar, "コピー", "ペースト", "反転P");
            Buttons(sidebar, "縦選択", "削除");
            var range = Box(sidebar, "range", "row");
            Field(range, "0"); Field(range, "60");
            Buttons(sidebar, "範囲選択", "拡大縮小");
            var interpolation = Box(sidebar, "interpolation", "interpolation");
            var curve = Box(interpolation, "curve", "curve");
            curve.generateVisualContent += ctx => Line(ctx.painter2D, new Vector2(5,120), new Vector2(120,5), Color.green);
            var interpolationControls = Box(interpolation, "interpolation-controls", "interpolation-controls");
            Choice(interpolationControls, new[] { "X軸移動", "Y軸移動", "Z軸移動", "回転" });
            Buttons(interpolationControls, "コピー", "ペースト");
            Action(interpolationControls, "線形補間", false);

            var right = Box(main, "right", "right");
            viewport = Box(right, "viewport", "viewport");
            viewport.generateVisualContent += DrawGrid;
            viewportTitle = new Label(); viewportTitle.AddToClassList("viewport-title"); viewport.Add(viewportTitle);
            var note = new Label("UI配置プロトタイプ：アバター・モーションは未読込");
            note.AddToClassList("viewport-note"); viewport.Add(note);
            var axis = new Label("X   Y   Z\n視点操作：未実装"); axis.AddToClassList("axis"); viewport.Add(axis);
            var bottom = Box(right, "bottom", "bottom");
            var model = Panel(bottom, "model", "モデル操作");
            var mode = Choice(model, new[] { "カメラ・照明・アクセサリ", "モデル操作（UIプレビュー）" });
            mode.RegisterValueChangedCallback(evt => ChangeMode(evt.newValue != "カメラ・照明・アクセサリ"));
            Buttons(model, "読込", "削除");
            DisabledToggle(model, "表示"); Action(model, "加算合成", false);
            Choice(model, new[] { "モデル未読込" }); Action(model, "登録", false);
            bone = Panel(bottom, "bone", "ボーン操作");
            Buttons(bone, "選択", "回転", "移動");
            Buttons(bone, "BOX選択", "全て選択");
            Action(bone, "未登録選択", false); Buttons(bone, "コピー", "ペースト", "反転ペースト");
            Buttons(bone, "フレーム登録", "初期化");
            morph = Panel(bottom, "morph", "表情操作");
            foreach (var title in new[] { "目", "リップ", "まゆ", "その他" })
            {
                var row = Box(morph, title, "row"); row.Add(new Label(title));
                Choice(row, new[] { "未読込" }); Action(row, "登録", false);
            }
            camera = Panel(bottom, "camera", "カメラ操作");
            Action(camera, "初期化", false); DisabledToggle(camera, "パースペクティブ");
            camera.Add(new Label("視野角")); Field(camera, "45"); Action(camera, "登録", false);
            light = Panel(bottom, "light", "照明操作");
            foreach (var title in new[] { "赤", "緑", "青", "X", "Y", "Z" })
            {
                var row = Box(light, title, "row"); row.Add(new Label(title));
                var slider = new Slider(0, 1) { value = 0.5f }; slider.style.flexGrow = 1;
                slider.SetEnabled(false); row.Add(slider);
            }
            Action(light, "登録", false);
            accessory = Panel(bottom, "accessory", "アクセサリ操作");
            Choice(accessory, new[] { "アクセサリ未読込" });
            Buttons(accessory, "読込", "削除"); DisabledToggle(accessory, "表示");
            foreach (var names in new[] { new[] { "X", "Y", "Z" }, new[] { "Rx", "Ry", "Rz" } })
            {
                var row = Box(accessory, string.Join("-", names), "row");
                foreach (var name in names) { row.Add(new Label(name)); Field(row, "0"); }
            }
            Action(accessory, "登録", false);
            var playback = Panel(bottom, "playback", "視点");
            Buttons(playback, "正面", "背面", "上面"); Buttons(playback, "左面", "右面", "下面");
            DisabledToggle(playback, "操作モデル中心");
            playback.Add(new Label("再生")); Action(playback, "再生", false);
            DisabledToggle(playback, "くり返し");
            var playbackRange = Box(playback, "playback-range", "row"); Field(playbackRange, "0"); Field(playbackRange, "60");
            status = new Label("UIのみ。読込・保存・再生・登録・出力は未実装。");
            status.AddToClassList("status"); workspace.Add(status);
            ChangeMode(false);
        }

        private void SetFrame(int value)
        {
            frame = Mathf.Clamp(value, 0, 10000);
            frameField.SetValueWithoutNotify(frame);
            RefreshTimelineLabels();
            timeline.MarkDirtyRepaint();
            status.text = "現在フレーム: " + frame + "（UI選択のみ。アニメーションデータなし）";
        }

        private void ChangeMode(bool model)
        {
            modelMode = model;
            bone.EnableInClassList("hidden", !model); morph.EnableInClassList("hidden", !model);
            camera.EnableInClassList("hidden", model); light.EnableInClassList("hidden", model);
            accessory.EnableInClassList("hidden", model);
            viewportTitle.text = model ? "Model：未読込 / Bone：未選択" : "カメラ・照明・アクセサリ";
            RefreshTimelineLabels();
            timeline.MarkDirtyRepaint();
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
        private static void Action(VisualElement parent, string text, bool enabled, System.Action callback = null)
        {
            var button = new Button(callback) { text = text, tooltip = enabled ? text : text + "：未実装（配置確認用）" };
            button.SetEnabled(enabled); parent.Add(button);
        }
        private static void Buttons(VisualElement parent, params string[] names)
        {
            var row = Box(parent, "buttons", "row");
            foreach (var name in names) Action(row, name, false);
        }
        private static void Field(VisualElement parent, string value)
        {
            var field = new TextField { value = value }; field.style.flexGrow = 1;
            field.SetEnabled(false); parent.Add(field);
        }
        private static DropdownField Choice(VisualElement parent, string[] choices)
        {
            var field = new DropdownField(new List<string>(choices), 0);
            if (choices.Length == 1) field.SetEnabled(false);
            parent.Add(field); return field;
        }
        private static void DisabledToggle(VisualElement parent, string label)
        {
            var toggle = new Toggle(label); toggle.SetEnabled(false); parent.Add(toggle);
        }
        private static void Line(Painter2D painter, Vector2 start, Vector2 end, Color color)
        {
            painter.strokeColor = color; painter.lineWidth = 1;
            painter.BeginPath(); painter.MoveTo(start); painter.LineTo(end); painter.Stroke();
        }
        private void DrawGrid(MeshGenerationContext ctx)
        {
            float w = viewport.contentRect.width, h = viewport.contentRect.height, horizon = h * .62f;
            for (int i = -12; i <= 12; i++)
                Line(ctx.painter2D, new Vector2(w/2 + i*15, horizon), new Vector2(w/2 + i*100,h), new Color(.75f,.75f,.75f));
            for (int i = 0; i < 10; i++)
            {
                float y = horizon + (h-horizon)*i*i/81;
                Line(ctx.painter2D,new Vector2(0,y),new Vector2(w,y),new Color(.75f,.75f,.75f));
            }
            Line(ctx.painter2D,new Vector2(0,h*.9f),new Vector2(w,h*.9f),new Color(.65f,.2f,.2f));
        }
        private void DrawTimeline(MeshGenerationContext ctx)
        {
            float w = timeline.contentRect.width, h = timeline.contentRect.height;
            var p = ctx.painter2D;
            for (int x=84; x<w; x+=8) Line(p,new Vector2(x,0),new Vector2(x,h),new Color(.22f,.24f,.34f));
            for (int y=24; y<h; y+=24) Line(p,new Vector2(0,y),new Vector2(w,y),new Color(.25f,.25f,.25f));
            float cursor = 84 + (frame % 20)*8;
            Line(p,new Vector2(cursor,0),new Vector2(cursor,h),Color.green);
        }
        private void RefreshTimelineLabels()
        {
            // Never mutate the visual tree inside generateVisualContent.
            timeline.Clear();
            var ruler = new Label((frame / 20 * 20) + "       +5       +10       +15");
            ruler.style.marginLeft = 84; ruler.style.color = Color.white; timeline.Add(ruler);
            foreach (string label in modelMode ? new[] { "全ての親", "センター", "上半身", "下半身", "表情" } : new[] { "カメラ", "照明", "セルフ影", "アクセサリ" })
            {
                var row = new Label(label); row.style.height = 24; row.style.width = 80;
                row.style.backgroundColor = new Color(.65f,.55f,.43f); timeline.Add(row);
            }
        }
        private void OnDestroy() { if (panel != null) Destroy(panel); }
    }
}
