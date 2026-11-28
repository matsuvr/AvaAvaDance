using System;
using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed partial class Extension : IDesktopFormHost
        {
            public bool SupportsDesktopForms => NativeDesktopActive;
            public string PickFile(string title, string extension, bool save) => save
                ? Import.PackageFileDialog.Save(title, extension) : Import.PackageFileDialog.Open(title, extension);
            public IDisposable OpenForm(DesktopForm form)
            {
                if (!SupportsDesktopForms || !active) throw new InvalidOperationException("Desktop form unavailable.");
                string name = "extension." + manifest.id + "." + form.Name;
                var open = new OpenDesktopForm(this, form, name, app.NextDesktopWindowId());
                if (app.desktopForms.ContainsKey(name)) throw new InvalidOperationException("Desktop form already open.");
                app.desktopForms.Add(name, open);
                var lease = KeepActive();
                Registration registration = null;
                registration = new Registration(() => {
                    app.desktopForms.Remove(name);
                    if (active) services.Remove(registration);
                    lease.Dispose();
                    form.Closed?.Invoke();
                });
                open.Close = registration;
                services.Add(registration);
                return registration;
            }
            public void ConfirmForm(string title, string message, Action accept)
            {
                long document = DocumentGeneration;
                app.ShowDialog(title, out var body);
                body.Add(new Label(message));
                var buttons = Box(body, "form-confirm", "row");
                Action(buttons, "はい", true, () => {
                    app.CloseDialog();
                    if (active && DocumentGeneration == document) accept();
                });
                Action(buttons, "キャンセル", true, app.CloseDialog);
            }
        }

        sealed class OpenDesktopForm
        {
            internal readonly Extension Owner;
            internal readonly DesktopForm Form;
            internal readonly string Name;
            internal readonly int Id;
            internal IDisposable Close;
            internal long Revision = long.MinValue;
            internal readonly Dictionary<int, int> NodeIds = new();
            internal readonly Dictionary<int, DesktopFormControl> Targets = new();

            internal OpenDesktopForm(Extension owner, DesktopForm form, string name, int id)
            { Owner = owner; Form = form; Name = name; Id = id; }
        }

        readonly Dictionary<string, OpenDesktopForm> desktopForms = new();

        void AppendDesktopForms(JArray windows, bool enabled)
        {
            foreach (var item in desktopForms.Values)
            {
                var f = item.Form;
                if (item.Revision != f.Revision)
                {
                    item.Revision = f.Revision;
                    item.NodeIds.Clear();
                }
                item.Targets.Clear();
                var nodes = new JArray();
                foreach (var c in f.Controls)
                {
                    var bounds = new Rect(c.X, c.Y, c.Width, c.Height);
                    var clip = DesktopIntersect(bounds, new Rect(0, 0, f.Width, f.Height));
                    if (clip.width <= 0 || clip.height <= 0) continue;
                    if (!item.NodeIds.TryGetValue(c.Id, out int id)) item.NodeIds[c.Id] = id = NextDesktopWindowId();
                    bool live = enabled && item.Owner.active && c.Enabled;
                    if (live && c.Hit) item.Targets[id] = c;
                    var node = new JObject {
                        ["id"] = id, ["kind"] = c.Kind, ["name"] = c.Name, ["text"] = c.Text,
                        ["tip"] = "", ["enabled"] = live, ["hit"] = c.Hit, ["bounds"] = DesktopRect(bounds),
                        ["clip"] = DesktopRect(clip), ["color"] = "#FFF0F0F0", ["background"] = "#00000000",
                        ["fontSize"] = 12, ["wrap"] = c.Kind == "multiline", ["align"] = c.Hit && c.Kind == "label" ? "center" : "left",
                        ["value"] = c.Kind == "toggle" ? new JValue(c.Checked) : new JValue(c.Text), ["readOnly"] = c.ReadOnly, ["maxLength"] = c.MaxLength
                    };
                    if (c.Choices != null) node["choices"] = new JArray(c.Choices);
                    if (c.ChoiceLabels != null) node["choiceLabels"] = new JArray(c.ChoiceLabels);
                    nodes.Add(node);
                }
                windows.Add(new JObject {
                    ["id"] = item.Id, ["name"] = item.Name, ["title"] = f.Title,
                    ["width"] = f.Width, ["height"] = f.Height, ["top"] = false, ["nodes"] = nodes,
                    ["acceptsFiles"] = f.DropFile != null, ["revision"] = f.Revision,
                    ["placementDirectory"] = item.Owner.SettingsDirectory
                });
            }
        }

        bool HandleDesktopFormAction(JObject args)
        {
            string name = (string)args["window"];
            if (name == null || !name.StartsWith("extension.", StringComparison.Ordinal)) return false;
            if (!desktopForms.TryGetValue(name, out var item) || !item.Owner.active) return true;
            string action = (string)args["action"];
            int id = (int?)args["id"] ?? 0;
            if (!DesktopDelivery.AcceptWindow(action == "window.close" || action == "window.resize" || action == "window.drop" ? id : (int?)args["windowId"], item.Id)) return true;
            var f = item.Form;
            if (action == "window.close") { item.Close.Dispose(); return true; }
            if (action == "window.resize")
            {
                f.Width = Mathf.Clamp((int)args["width"], 1, 8192);
                f.Height = Mathf.Clamp((int)args["height"], 1, 8192);
                f.Layout(f.Width, f.Height);
                return true;
            }
            if (DesktopActiveRoot(MenuRoot.panel.visualTree) != MenuRoot.panel.visualTree || !DesktopDelivery.AcceptRevision(item.Revision, f.Revision)) return true;
            if (action == "window.drop")
            {
                if ((long?)args["revision"] == f.Revision && args["path"]?.Type == JTokenType.String)
                    f.DropFile?.Invoke((string)args["path"]);
                return true;
            }
            if (!item.Targets.TryGetValue(id, out var control) || !control.Enabled) return true;
            if (action == "input" && (string)args["kind"] != "down") return true;
            if (action == "set" && control.Kind == "toggle" && args["value"]?.Type == JTokenType.Boolean)
            {
                control.Checked = (bool)args["value"];
                f.Input(control.Id, action, control.Checked.ToString(), 1);
                return true;
            }
            if (action == "set" || action == "submit")
            {
                if (control.ReadOnly || control.Kind != "text" && control.Kind != "multiline" && control.Kind != "choice") return true;
                if (args["value"]?.Type != JTokenType.String) return true;
                string value = (string)args["value"];
                if (control.MaxLength > 0 && value.Length > control.MaxLength ||
                    control.Choices != null && !control.Choices.Contains(value)) return true;
                control.Text = value;
            }
            if (action == "activate" || action == "input" || action == "set" || action == "submit")
                f.Input(control.Id, action, control.Text, (int?)args["clicks"] ?? 1);
            return true;
        }
    }
}
