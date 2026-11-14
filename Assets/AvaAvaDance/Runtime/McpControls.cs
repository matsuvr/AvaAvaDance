using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class ControlId { internal int Value; }
        readonly ConditionalWeakTable<VisualElement, ControlId> mcpIds = new();
        int nextMcpId;
        int McpId(VisualElement e) => mcpIds.GetValue(e, _ => new ControlId { Value = ++nextMcpId }).Value;

        IEnumerable<(string window, VisualElement root)> McpRoots()
        {
            // Modal and popup layers receive input before the workspace.
            var overlay = importDialog ?? modal ?? menuLayer;
            yield return ("main", overlay ?? MenuRoot);
            if (overlay != null) yield break;
            if (operations?.Root?.panel != null) yield return ("operations", operations.Root);
            if (floatingTime?.Root?.panel != null) yield return ("timeline", floatingTime.Root);
            if (floatingWave?.Root?.panel != null) yield return ("audio", floatingWave.Root);
        }

        static IEnumerable<VisualElement> McpElements(VisualElement root)
        {
            if (root.resolvedStyle.display == DisplayStyle.None) yield break;
            if (root.resolvedStyle.visibility == Visibility.Visible) yield return root;
            for (int i = 0; i < root.hierarchy.childCount; i++)
                foreach (var child in McpElements(root.hierarchy[i])) yield return child;
        }

        static string ControlText(VisualElement e) => e switch {
            Button b => string.IsNullOrEmpty(b.text) ? b.Q<Label>()?.text : b.text,
            TextElement t => t.text, BaseField<bool> f => f.label, BaseField<int> f => f.label,
            BaseField<float> f => f.label, BaseField<string> f => f.label, _ => null
        };

        static object ControlValue(VisualElement e) => e switch {
            BaseField<bool> f => f.value, BaseField<float> f => f.value, BaseField<int> f => f.value,
            BaseField<string> f => f.value, _ => null
        };

        JToken McpControls(JObject args)
        {
            string query = (string)args["query"] ?? "";
            bool allElements = (bool?)args["all_elements"] == true;
            var rows = from window in McpRoots()
                       from e in McpElements(window.root)
                       let type = e.GetType().Name
                       let label = ControlText(e)
                       where allElements || e == window.root || !string.IsNullOrEmpty(e.name) || !string.IsNullOrEmpty(label) ||
                             e is Button || ControlValue(e) != null || !string.IsNullOrEmpty(e.tooltip)
                       where query.Length == 0 || new[] { window.window, type, e.name, label, e.tooltip }.Concat(e.GetClasses())
                           .Any(s => s?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                       let rect = e.worldBound
                       select new { window = window.window, id = McpId(e), parent = e.parent == null ? 0 : McpId(e.parent),
                           type, name = e.name, label, classes = e.GetClasses(), tooltip = e.tooltip, value = ControlValue(e), enabled = e.enabledInHierarchy,
                           choices = e is DropdownField drop ? drop.choices : e is RadioButtonGroup radio ? radio.choices?.ToList() : null,
                           bounds = new[] { rect.x, rect.y, rect.width, rect.height } };
            return Page(rows, args);
        }

        VisualElement McpElement(int id)
        {
            var e = McpRoots().SelectMany(w => McpElements(w.root))
                .FirstOrDefault(element => mcpIds.TryGetValue(element, out var found) && found.Value == id);
            if (e == null) throw new ArgumentException("Control is stale or covered. Read ava_ui again.");
            if (!e.enabledInHierarchy) throw new InvalidOperationException("Control is disabled.");
            return e;
        }

        static void McpSend(VisualElement target, EventBase evt)
        {
            using (evt) { evt.target = target; target.SendEvent(evt); }
        }

        void McpControl(JObject args)
        {
            var e = McpElement((int)args["id"]);
            switch ((string)args["action"])
            {
                case "focus": e.Focus(); return;
                case "reveal":
                    for (var parent = e.parent; parent != null; parent = parent.parent)
                        if (parent is ScrollView scroll && scroll.contentContainer.Contains(e)) scroll.ScrollTo(e);
                    return;
                case "activate":
                    if (e is Button) McpSend(e, NavigationSubmitEvent.GetPooled());
                    else if (e is Toggle toggle) toggle.value = !toggle.value;
                    else if (e is RadioButton radio) radio.value = true;
                    else throw new ArgumentException("Use set for a field or ava_input for a custom control.");
                    return;
                case "set":
                    var value = args["value"];
                    switch (e)
                    {
                        case DropdownField d when value.Type == JTokenType.String && d.choices.Contains((string)value): d.value = (string)value; return;
                        case RadioButtonGroup r when value.Type == JTokenType.Integer && (int)value >= 0 && (int)value < r.choices.Count(): r.value = (int)value; return;
                        case BaseField<bool> f when value.Type == JTokenType.Boolean: f.value = (bool)value; return;
                        case BaseField<int> f when !(e is RadioButtonGroup) && value.Type == JTokenType.Integer: f.value = (int)value; return;
                        case BaseField<float> f when (value.Type == JTokenType.Float || value.Type == JTokenType.Integer) && float.IsFinite((float)value):
                            f.value = (float)value; return;
                        case TextField f when !f.isReadOnly && value.Type == JTokenType.String: f.value = (string)value; return;
                        default: throw new ArgumentException("Unsupported field or invalid value. Inspect type and choices in ava_ui.");
                    }
                default: throw new ArgumentException("Unknown control action.");
            }
        }

        void McpInput(JObject args)
        {
            var element = McpElement((int)args["id"]);
            foreach (var floating in new FloatingPanel[] { operations, floatingTime, floatingWave })
                if (floating?.Root != null && (element == floating.Root || floating.Root.Contains(element)))
                {
                    floating.DispatchMcp(() => McpInput(element, args));
                    return;
                }
            McpInput(element, args);
        }

        void McpInput(VisualElement element, JObject args)
        {
            var mods = ((bool?)args["shift"] == true ? EventModifiers.Shift : 0) |
                       ((bool?)args["ctrl"] == true ? EventModifiers.Control : 0) |
                       ((bool?)args["alt"] == true ? EventModifiers.Alt : 0);
            string kind = (string)args["kind"];
            if (kind == "keyDown" || kind == "keyUp")
            {
                if (!Enum.TryParse((string)args["key"], true, out KeyCode key) || !Enum.IsDefined(typeof(KeyCode), key))
                    throw new ArgumentException("Expected a Unity KeyCode name.");
                var evt = new Event { modifiers = mods, keyCode = key, type = kind == "keyDown" ? EventType.KeyDown : EventType.KeyUp };
                if (kind == "keyDown") McpSend(element, KeyDownEvent.GetPooled(evt));
                else McpSend(element, KeyUpEvent.GetPooled(evt));
                return;
            }
            var point = element.LocalToWorld(new Vector2((float)args["x"], (float)args["y"]));
            var target = element.panel.GetCapturingElement(PointerId.mousePointerId) as VisualElement ?? element.panel.Pick(point);
            var root = McpRoots().First(w => w.root == element || w.root.Contains(element)).root;
            if (target == null || !(target == root || root.Contains(target)) || !target.enabledInHierarchy)
                throw new InvalidOperationException("Point is outside the active UI.");
            var ev = new Event { mousePosition = point, modifiers = mods, button = (int)args["button"],
                clickCount = (int)args["clicks"], delta = new Vector2((float)args["dx"], (float)args["dy"]) };
            switch (kind)
            {
                case "down": ev.type = EventType.MouseDown; McpSend(target, PointerDownEvent.GetPooled(ev)); break;
                case "up": ev.type = EventType.MouseUp; McpSend(target, PointerUpEvent.GetPooled(ev)); break;
                case "move": ev.type = EventType.MouseMove; McpSend(target, PointerMoveEvent.GetPooled(ev)); break;
                case "drag": ev.type = EventType.MouseDrag; McpSend(target, PointerMoveEvent.GetPooled(ev)); break;
                case "wheel": ev.type = EventType.ScrollWheel; McpSend(target, WheelEvent.GetPooled(ev)); break;
                default: throw new ArgumentException("Unknown pointer action.");
            }
        }
    }
}
