using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        RenderTexture desktopLayout;
        readonly Dictionary<int, (VisualElement element, Vector2 offset)> desktopTargets = new();
        Vector2 desktopPointer;
        int desktopButtons;
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<VisualElement, Action> desktopDraftGestures = new();

        internal int NextDesktopWindowId() => ++nextMcpId;

        void ResizeNativeLayout(float width, float height)
        {
            int maximum = Mathf.Min(8192, SystemInfo.maxTextureSize);
            int w = Mathf.Clamp(Mathf.RoundToInt(width), 1, maximum);
            int h = Mathf.Clamp(Mathf.RoundToInt(height), 1, maximum);
            if (desktopLayout != null && desktopLayout.width == w && desktopLayout.height == h) return;
            var next = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Desktop layout" };
            if (!next.Create()) { Destroy(next); throw new InvalidOperationException("Desktop layout surface allocation failed."); }
            var old = desktopLayout;
            desktopLayout = next;
            panel.scale = 1;
            panel.targetTexture = next;
            panel.SetScreenToPanelSpaceFunction(_ => new Vector2(-100000, -100000));
            if (old != null) { old.Release(); Destroy(old); }
        }

        void ReleaseNativeLayout()
        {
            desktopTargets.Clear();
            if (desktopLayout == null) return;
            panel.targetTexture = null;
            desktopLayout.Release();
            Destroy(desktopLayout);
            desktopLayout = null;
        }

        VisualElement DesktopActiveRoot(VisualElement root)
        {
            // GenericDropdownMenu can attach at the panel level, outside the UIDocument.
            var overlay = root.Q(className: GenericDropdownMenu.ussClassName);
            if (overlay != null) return overlay;
            foreach (var candidate in new[] { importDialog, modal, contextLayer, menuLayer })
                if (candidate != null && root.Contains(candidate)) return candidate;
            return root;
        }

        static Rect DesktopIntersect(Rect a, Rect b) =>
            Rect.MinMaxRect(Mathf.Max(a.xMin, b.xMin), Mathf.Max(a.yMin, b.yMin),
                Mathf.Min(a.xMax, b.xMax), Mathf.Min(a.yMax, b.yMax));

        static JArray DesktopRect(Rect r) => new JArray(r.x, r.y, r.width, r.height);

        JObject NativeDesktopSnapshot(float width, float height)
        {
            ResizeNativeLayout(width, height);
            var root = MenuRoot.panel.visualTree;
            var active = DesktopActiveRoot(root);
            var nodes = new JArray();
            desktopTargets.Clear();
            AppendDesktopElement(root, new Rect(0, 0, desktopLayout.width, desktopLayout.height), active, nodes);
            var windows = new JArray();
            AppendDesktopForms(windows, active == root);
            foreach (var (name, window) in DesktopPanels())
            {
                if (window?.Root?.panel == null || window.Surface == null) continue;
                var panelRoot = window.Root.panel.visualTree;
                var panelNodes = new JArray();
                var surface = window.Surface;
                // A modal in the main window captures input across all desktop windows.
                var inputRoot = active == root ? DesktopActiveRoot(panelRoot) : active;
                AppendDesktopElement(panelRoot, new Rect(0, 0, surface.width, surface.height), inputRoot, panelNodes);
                windows.Add(new JObject {
                    ["name"] = name, ["id"] = McpId(window.Root), ["title"] = window.DesktopTitle, ["top"] = false,
                    ["width"] = surface.width, ["height"] = surface.height, ["nodes"] = panelNodes
                });
            }
            if (previewWindowId != 0)
            {
                var size = previewWindowSize;
                var bounds = DesktopRect(new Rect(0, 0, size.x, size.y));
                windows.Add(new JObject {
                    ["name"] = "preview", ["id"] = previewWindowId, ["title"] = "AvaAvaDance プレビュー",
                    ["width"] = size.x, ["height"] = size.y, ["top"] = previewTop,
                    ["nodes"] = new JArray(new JObject {
                        ["id"] = previewWindowId, ["kind"] = "viewport", ["bounds"] = bounds, ["clip"] = bounds,
                        ["enabled"] = false, ["hit"] = false, ["background"] = "#FF24272B", ["color"] = "#FFFFFFFF"
                    })
                });
            }
            var viewBounds = viewport.worldBound;
            return new JObject {
                ["width"] = desktopLayout.width, ["height"] = desktopLayout.height,
                ["viewport"] = DesktopRect(float.IsFinite(viewBounds.x + viewBounds.y + viewBounds.width + viewBounds.height) ? viewBounds : Rect.zero),
                ["viewportId"] = McpId(viewport), ["fullscreen"] = fullPreview, ["nodes"] = nodes, ["windows"] = windows
            };
        }

        // Unity 2022 computes the effective clip from USS, padding, transforms and
        // ScrollViews. Its public IResolvedStyle omits overflow. Bind the engine's
        // computed rectangle once so both hosts clip identically to the Player.
        static class DesktopClipping
        {
            internal static readonly Func<VisualElement, Rect> World = (Func<VisualElement, Rect>)
                typeof(VisualElement).GetProperty("worldClip", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).GetGetMethod(true).CreateDelegate(typeof(Func<VisualElement, Rect>));
        }

        void AppendDesktopElement(VisualElement e, Rect ancestorClip, VisualElement active, JArray nodes)
        {
            var style = e.resolvedStyle;
            if (style.display == DisplayStyle.None || style.opacity <= 0 || e == previewImage) return;
            ancestorClip = DesktopIntersect(ancestorClip, DesktopClipping.World(e));
            Rect bounds = e.worldBound;
            if (!float.IsFinite(bounds.x + bounds.y + bounds.width + bounds.height)) return;
            if (ancestorClip.width <= 0 || ancestorClip.height <= 0) return;
            var kind = DesktopKind(e);
            // A field's label has its own layout slot. Native input controls occupy the input slot.
            bool field = kind == "text" || kind == "number" || kind == "integer" || kind == "choice" || kind == "slider";
            VisualElement input = field ? e.Q(className: "unity-base-field__input") : null;
            var nodeBounds = input?.worldBound ?? bounds;
            if (kind == "slider")
            {
                var track = e.Q(className: "unity-base-slider__drag-container");
                if (track != null) nodeBounds = track.worldBound;
            }
            var nodeClip = DesktopIntersect(nodeBounds, ancestorClip);
            if (nodeClip.width > 0 && nodeClip.height > 0 && style.visibility == Visibility.Visible)
            {
                bool enabled = e.enabledInHierarchy && (active == e || active.Contains(e));
                AppendDesktopNode(e, kind, nodeBounds, nodeClip, enabled, nodes);
            }
            if (field)
            {
                var label = e.Q<Label>(className: "unity-base-field__label");
                if (label != null && !string.IsNullOrEmpty(label.text)) AppendDesktopElement(label, ancestorClip, active, nodes);
                if (kind == "slider" && input != null)
                {
                    // Sliders with showInputField expose the original precision editor as well.
                    VisualElement numeric = (VisualElement)e.Q<FloatField>() ?? e.Q<IntegerField>();
                    if (numeric != null) AppendDesktopElement(numeric, ancestorClip, active, nodes);
                }
                return;
            }
            if (kind == "toggle") return;

            for (int i = 0; i < e.hierarchy.childCount; ++i)
                AppendDesktopElement(e.hierarchy[i], ancestorClip, active, nodes);
        }

        string DesktopKind(VisualElement e)
        {
            if (e == viewport) return "viewport";
            if (desktopDrawings.TryGetValue(e, out _)) return "canvas";
            return e switch {
                Button => "button", Toggle => "toggle", RadioButton => "toggle", DropdownField => "choice",
                Slider => "slider", SliderInt => "slider", IntegerField => "integer", FloatField => "number",
                TextField => "text", TextElement => "label", _ => "panel"
            };
        }

        void AppendDesktopNode(VisualElement e, string kind, Rect bounds, Rect clip, bool enabled, JArray nodes)
        {
            var style = e.resolvedStyle;
            int id = McpId(e);
            bool hit = e.pickingMode == PickingMode.Position;
            string text = e switch {
                Toggle toggle => toggle.label ?? toggle.text, RadioButton radio => radio.label ?? radio.text,
                TextElement t => t.text, _ => ""
            };
            if (e is Toggle tgl && string.IsNullOrEmpty(text)) text = tgl.text;
            if (e is RadioButton rad && string.IsNullOrEmpty(text)) text = rad.text;
            var background = style.backgroundColor;
            if (kind == "panel" && background.a == 0 && !hit) return;
            if (kind == "label" && string.IsNullOrEmpty(text) && !hit && background.a == 0) return;
            var node = new JObject {
                ["id"] = id, ["kind"] = kind, ["name"] = e.name ?? "", ["text"] = text ?? "",
                ["tip"] = e.tooltip ?? "", ["enabled"] = enabled, ["hit"] = hit,
                ["bounds"] = DesktopRect(bounds), ["clip"] = DesktopRect(clip),
                ["background"] = DesktopColor(background), ["color"] = DesktopColor(style.color),
                ["fontSize"] = style.fontSize, ["wrap"] = style.whiteSpace == WhiteSpace.Normal,
                ["align"] = ((int)style.unityTextAlign % 3) switch { 0 => "left", 2 => "right", _ => "center" }
            };
            object value = ControlValue(e);
            if (value != null) node["value"] = JToken.FromObject(value);
            if (e is DropdownField choice)
            {
                node["choices"] = new JArray(choice.choices);
                if (choice.formatListItemCallback != null)
                    node["choiceLabels"] = new JArray(choice.choices.Select(choice.formatListItemCallback));
                if (choice.formatSelectedValueCallback != null)
                    node["selectedLabel"] = choice.formatSelectedValueCallback(choice.value);
            }
            if (e == captureShortcut) node["keyCapture"] = true;
            if (e is Slider slider)
            {
                node["min"] = slider.lowValue; node["max"] = slider.highValue;
                node["vertical"] = slider.direction == SliderDirection.Vertical;
            }
            if (e is SliderInt ints)
            {
                node["min"] = ints.lowValue; node["max"] = ints.highValue; node["integer"] = true;
                node["vertical"] = ints.direction == SliderDirection.Vertical;
            }
            if (e is TextField str) { node["delayed"] = str.isDelayed; node["readOnly"] = str.isReadOnly; }
            if (e is IntegerField integer) { node["delayed"] = integer.isDelayed; node["readOnly"] = integer.isReadOnly; }
            if (e is FloatField number) { node["delayed"] = number.isDelayed; node["readOnly"] = number.isReadOnly; }
            if (desktopDrawings.TryGetValue(e, out var paint))
            {
                var paths = new JArray();
                paint(new DesktopPainter(paths));
                node["paths"] = paths;
            }
            if (enabled) desktopTargets[id] = (e, bounds.min - e.worldBound.min);
            nodes.Add(node);
        }

        IEnumerable<(string name, FloatingPanel panel)> DesktopPanels()
        {
            yield return ("timeline", floatingTime);
            yield return ("audio", floatingWave);
            yield return ("operations", operations);
        }

        void DesktopWindowAction(string name, int id, FloatingPanel window, JObject args)
        {
            bool preview = name == "preview";
            int current = preview ? previewWindowId : window?.Root == null ? 0 : McpId(window.Root);
            if (!DesktopDelivery.AcceptWindow(id, current)) return;
            if ((string)args["action"] == "window.close")
            {
                if (preview) previewWindowId=0; else window.CloseDesktop();
                return;
            }
            int width = Mathf.Clamp(Mathf.RoundToInt((float)args["width"]), 1, 8192);
            int height = Mathf.Clamp(Mathf.RoundToInt((float)args["height"]), 1, 8192);
            if (preview) previewWindowSize = new Vector2Int(width, height);
            else window.ResizeDesktop(width, height);
        }

        void NativeDesktopAction(JObject args)
        {
            if (HandleDesktopFormAction(args)) return;
            var main = MenuRoot.panel.visualTree;
            var name = (string)args["window"];
            var floating = string.IsNullOrEmpty(name) || name == "main" ? null : DesktopPanels().FirstOrDefault(entry => entry.name == name).panel;
            int id = (int)args["id"];
            string action = (string)args["action"];
            if (action == "window.close" || action == "window.resize")
            {
                DesktopWindowAction(name, id, floating, args);
                return;
            }
            if (!string.IsNullOrEmpty(name) && name != "main" &&
                (floating?.Root?.panel == null || !DesktopDelivery.AcceptWindow((int?)args["windowId"], McpId(floating.Root)))) return;
            var root = floating?.Root?.panel?.visualTree ?? main;
            if (action == "blur")
            {
                if (root.panel.GetCapturingElement(PointerId.mousePointerId) is VisualElement captured)
                    captured.ReleasePointer(PointerId.mousePointerId);
                desktopButtons = 0;
                ReleaseWorkspaceInput(false);
                return;
            }
            var active = DesktopActiveRoot(root);
            if (root != main && DesktopActiveRoot(main) != main)
                return;
            var kind = (string)args["kind"];
            bool key = action == "input" && (kind == "keyDown" || kind == "keyUp");
            VisualElement e;
            Vector2 offset = Vector2.zero;
            if (id == 0 && action == "drop" && root == main)
                e = viewport;
            else if (id == 0 && key)
            {
                e = root.focusController?.focusedElement as VisualElement;
                if (e == null || !active.Contains(e) && active != e)
                    e = active == root && root == main ? MenuRoot.Q("workspace") : active;
            }
            else
            {
                if (!desktopTargets.TryGetValue(id, out var target))
                    return;
                e = target.element;
                offset = target.offset;
                if (e.panel != root.panel || !e.enabledInHierarchy || active != e && !active.Contains(e))
                    return;
                for (var item = e; item != null; item = item.parent)
                    if (item.resolvedStyle.display == DisplayStyle.None || item.ClassListContains("hidden"))
                        return;
            }
            if (action == "begin" || action == "end")
            {
                if (action == "begin" && desktopDraftGestures.TryGetValue(e, out var begin)) begin();
                if (action == "end" && draftSlider == e) FinishDraftSlider();
                return;
            }
            if (action == "unfocus")
            {
                var focused = root.focusController?.focusedElement as VisualElement;
                if (focused != null && (focused == e || e.Contains(focused))) focused.Blur();
                return;
            }
            if (action == "drop")
            {
                if (e != viewport || importing || shaderInstalling || exporting || physicsBaking || active != root) return;
                foreach (string path in args["paths"]?.Values<string>() ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathRooted(path)) continue;
                    bool package = path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);
                    if (id == 0 && !package) continue;
                    if (FindModelEffect(path) != null) { ImportModelEffect(path); continue; }
                    if (package || IsVrm(path) || IsController(path))
                    {
                        ImportPickedPackage(path);
                        break;
                    }
                }
                return;
            }
            if (action == "input")
            {
                if (!key)
                {
                    args["x"] = ((float?)args["x"] ?? 0) + offset.x;
                    args["y"] = ((float?)args["y"] ?? 0) + offset.y;
                    args["button"] ??= 0; args["clicks"] ??= 1;
                    args["dx"] ??= 0; args["dy"] ??= 0;
                    int button = (int)args["button"];
                    if (button >= 0 && button < 3)
                    {
                        if (kind == "down") desktopButtons |= 1 << button;
                        if (kind == "up") desktopButtons &= ~(1 << button);
                    }
                    desktopPointer = e.LocalToWorld(new Vector2((float)args["x"], (float)args["y"]));
                }
                if (floating != null)
                {
                    floating.DispatchMcp(() => McpInput(e, args, active));
                    if (kind == "keyUp" && Enum.TryParse<KeyCode>((string)args["key"], true, out var released))
                    {
                        pressedKeys.Remove(released); suppressedKeys.Remove(released);
                        if (transformKey != KeyCode.None && heldLabel == released) FinishHeldTransform();
                    }
                }
                else McpInput(e, args, active);
                return;
            }
            if (action == "set" && (e is IntegerField i && i.isReadOnly || e is FloatField f && f.isReadOnly))
                throw new InvalidOperationException("The desktop field is read only.");
            if (action == "set" && e is SliderInt && args["value"]?.Type == JTokenType.Float)
                args["value"] = Mathf.RoundToInt((float)args["value"]);
            if (action == "activate" || action == "set")
                mainKeyArea = KeyArea(e) ?? "全体";
            ApplyControl(e, args);
        }
    }
}
