using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly Dictionary<Command, Button> menuButtons = new Dictionary<Command, Button>();
        readonly Dictionary<Command, Binding> commands = new Dictionary<Command, Binding>();
        VisualElement menuLayer;
        ScrollView mainList, subList;
        MmdMenu.Entry menuGroup, submenu, highlight, focusedHeading;
        bool englishMenus;

        sealed class Binding
        {
            internal Action Run;
            internal Func<bool> Available, Checked;
        }

        void Bind(Command id, Action run, Func<bool> available = null, Func<bool> check = null)
        {
            commands.Add(id, new Binding { Run = run, Available = available, Checked = check });
        }

        bool Available(Command id) => commands.TryGetValue(id, out var b) && (b.Available == null || b.Available());

        bool Execute(Command id)
        {
            if (!Available(id) || modal != null || importDialog != null || exporting || importing) return false;
            try { commands[id].Run(); }
            catch (Exception ex) { status.text = "操作を完了できませんでした　" + ex.Message; Debug.LogWarning(ex.Message); }
            return true;
        }

        void BuildMmdMenus(VisualElement bar)
        {
            BindFileCommands(); BindEditCommands(); BindViewCommands(); BindOtherCommands(); BindPropCommands(); BindModelCommands();
            englishMenus = PlayerPrefs.GetInt("EnglishMenus") == 1;
            foreach (var group in MmdMenu.Bar)
            {
                var button=Action(bar,group.Label(englishMenus),true,()=>OpenGroup(group));menuButtons.Add(group.Id,button);
                button.RegisterCallback<BlurEvent>(_=>{if(focusedHeading==group)FocusHeading(null);});
            }
        }

        void FocusHeading(MmdMenu.Entry group)
        {
            if(focusedHeading!=null)menuButtons[focusedHeading.Id].RemoveFromClassList("open-menu");
            focusedHeading=group;
            if(group!=null){menuButtons[group.Id].Focus();menuButtons[group.Id].AddToClassList("open-menu");}
        }

        void SwitchLanguage()
        {
            englishMenus = !englishMenus;
            foreach (var group in MmdMenu.Bar) menuButtons[group.Id].text = group.Label(englishMenus);
            PlayerPrefs.SetInt("EnglishMenus", englishMenus ? 1 : 0); RefreshTimelineLabels();
        }

        VisualElement MenuRoot => GetComponent<UIDocument>().rootVisualElement;
        void CloseMenu()
        {
            if (menuLayer == null) return;
            menuButtons[menuGroup.Id].RemoveFromClassList("open-menu");
            menuLayer.RemoveFromHierarchy(); menuLayer = null; mainList = subList = null;
            menuGroup = submenu = highlight = null;
            MenuRoot.Q("workspace").Focus();
        }

        MmdMenu.Entry HeadingAt(Vector2 position) => MmdMenu.Bar.FirstOrDefault(g => menuButtons[g.Id].worldBound.Contains(position));

        void OpenGroup(MmdMenu.Entry group)
        {
            CloseMenu(); FocusHeading(null);
            menuGroup = group;
            menuButtons[group.Id].AddToClassList("open-menu");
            menuLayer = Box(MenuRoot, "mmd-menu-overlay", "mmd-menu-overlay");
            menuLayer.focusable = true;
            menuLayer.RegisterCallback<PointerDownEvent>(e => {
                if (e.target != menuLayer) return;
                var heading = HeadingAt(e.position);
                if (heading != null && heading != menuGroup) OpenGroup(heading); else CloseMenu();
                e.StopPropagation();
            });
            menuLayer.RegisterCallback<PointerMoveEvent>(e => {
                if (e.target != menuLayer) return;
                var heading = HeadingAt(e.position);
                if (heading != null && heading != menuGroup) OpenGroup(heading);
            });
            mainList = CreateList(group, menuButtons[group.Id].worldBound, false);
            menuLayer.Focus();
        }

        ScrollView CreateList(MmdMenu.Entry parent, Rect anchor, bool nested)
        {
            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("mmd-dropdown");
            menuLayer.Add(list);
            var root = MenuRoot.worldBound;
            float width = Mathf.Min(englishMenus ? 338 : 285, root.width);
            float x = nested ? anchor.xMax : anchor.xMin;
            if (nested && x + width > root.xMax) x = anchor.xMin - width;
            float y = nested ? anchor.yMin : anchor.yMax;
            float height = parent.Children.Sum(c => c.Id == Command.Separator ? 6 : 22) + 4;
            if (nested) y = Mathf.Max(root.yMin, Mathf.Min(y, root.yMax - height));
            list.style.left = Mathf.Clamp(x - root.xMin, 0, root.width - width);
            list.style.top = y - root.yMin; list.style.width = width;
            list.style.height = Mathf.Min(height, Mathf.Max(22, root.yMax - y));
            foreach (var entry in parent.Children)
            {
                if (entry.Id == Command.Separator) { Box(list, "", "mmd-menu-divider"); continue; }
                bool branch = entry.Children.Length > 0;
                bool enabled = branch || Available(entry.Id);
                bool tick = commands.TryGetValue(entry.Id, out var b) && b.Checked != null && b.Checked();
                var row = new Button { name = entry.Id.ToString(), focusable = false };
                row.AddToClassList("mmd-menu-row"); list.Add(row);
                var mark = new Label(tick ? "✓" : "") { pickingMode = PickingMode.Ignore }; mark.AddToClassList("menu-check"); row.Add(mark);
                var caption = new Label(entry.Label(englishMenus)) { pickingMode = PickingMode.Ignore };
                caption.AddToClassList("menu-caption"); row.Add(caption);
                if (branch) row.Add(new Label("▶") { pickingMode = PickingMode.Ignore });
                row.SetEnabled(enabled);
                if (!enabled) row.tooltip = commands.ContainsKey(entry.Id) ? "現在の編集対象では使用できません" : "AvaAvaDanceでは未対応";
                row.clicked += () => Activate(entry, row);
                row.RegisterCallback<PointerEnterEvent>(_ => {
                    if (!nested && submenu != entry) CloseSubmenu();
                    Highlight(entry);
                    if (branch && submenu != entry) OpenSubmenu(entry, row);
                });
            }
            return list;
        }

        void CloseSubmenu()
        {
            subList?.RemoveFromHierarchy(); subList = null; submenu = null;
        }

        void OpenSubmenu(MmdMenu.Entry entry, VisualElement anchor)
        {
            CloseSubmenu(); submenu = entry;
            subList = CreateList(entry, anchor.worldBound, true);
            highlight = null;
        }

        void Highlight(MmdMenu.Entry entry)
        {
            if (highlight != null) menuLayer.Q<Button>(highlight.Id.ToString())?.RemoveFromClassList("menu-selection");
            highlight = entry;
            var row = menuLayer.Q<Button>(entry.Id.ToString());
            row?.AddToClassList("menu-selection");
            if (row != null) row.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(row);
        }

        void Activate(MmdMenu.Entry entry, VisualElement row)
        {
            if (entry.Children.Length > 0) { OpenSubmenu(entry, row); return; }
            if (!Available(entry.Id)) return;
            CloseMenu(); Execute(entry.Id);
        }

        void MenuNavigation(KeyCode key)
        {
            var parent = submenu ?? menuGroup;
            var choices = parent.Children.Where(e => e.Children.Length > 0 || Available(e.Id)).ToArray();
            int i = Array.IndexOf(choices, highlight);
            if (key == KeyCode.Escape || key == KeyCode.LeftArrow && submenu != null)
            {
                if (submenu == null) CloseMenu();
                else { var old = submenu; CloseSubmenu(); Highlight(old); }
            }
            else if (key == KeyCode.UpArrow || key == KeyCode.DownArrow)
            {
                if (choices.Length > 0) Highlight(choices[(i < 0 ? (key == KeyCode.DownArrow ? 0 : choices.Length - 1) :
                    i + choices.Length + (key == KeyCode.DownArrow ? 1 : -1)) % choices.Length]);
            }
            else if (key == KeyCode.RightArrow && highlight?.Children.Length > 0)
                OpenSubmenu(highlight, menuLayer.Q<Button>(highlight.Id.ToString()));
            else if (key == KeyCode.RightArrow || key == KeyCode.LeftArrow)
            {
                int n = MmdMenu.Bar.Length;
                OpenGroup(MmdMenu.Bar[(Array.IndexOf(MmdMenu.Bar, menuGroup) + n + (key == KeyCode.RightArrow ? 1 : -1)) % n]);
            }
            else
            {
                var item = key == KeyCode.Return || key == KeyCode.KeypadEnter ? highlight :
                    choices.FirstOrDefault(c => c.Access(englishMenus) != '\0' && c.Access(englishMenus) == (char)key);
                if (item != null) Activate(item, menuLayer.Q<Button>(item.Id.ToString()));
            }
        }

        void MainMenuKey(KeyDownEvent e)
        {
            if (importDialog != null) return;
            var key = e.keyCode;
            if (key == KeyCode.None && menuLayer != null && char.IsLetter(e.character)) key = (KeyCode)char.ToLowerInvariant(e.character);
            if (modal != null)
            {
                if (key != KeyCode.Escape) return;
                if (exporting) cancelExport = true; else CloseDialog();
            }
            else if (key==KeyCode.F10 || key==KeyCode.LeftAlt || key==KeyCode.RightAlt)
            {
                if(menuLayer!=null)CloseMenu();
                else if(focusedHeading!=null){FocusHeading(null);MenuRoot.Q("workspace").Focus();}
                else FocusHeading(MmdMenu.Bar[0]);
            }
            else if (e.altKey && !e.ctrlKey && !e.shiftKey)
            {
                var group = MmdMenu.Bar.FirstOrDefault(g => g.Access(englishMenus) == (char)key);
                if (group != null) OpenGroup(group);
                else if (key == KeyCode.Return || key == KeyCode.KeypadEnter) { CloseMenu(); Execute(Command.Fullscreen); }
                else return;
            }
            else if (menuLayer != null) { if (!e.ctrlKey) MenuNavigation(key); }
            else if(focusedHeading!=null){
                if(key==KeyCode.Escape){FocusHeading(null);MenuRoot.Q("workspace").Focus();}
                else if(key==KeyCode.DownArrow || key==KeyCode.Return)OpenGroup(focusedHeading);
                else if(key==KeyCode.RightArrow || key==KeyCode.LeftArrow){
                    int n=MmdMenu.Bar.Length;
                    FocusHeading(MmdMenu.Bar[(Array.IndexOf(MmdMenu.Bar,focusedHeading)+n+(key==KeyCode.RightArrow?1:-1))%n]);
                }else{
                    var group=MmdMenu.Bar.FirstOrDefault(g=>g.Access(englishMenus)==(char)key);
                    if(group==null)return;OpenGroup(group);
                }
            }
            else
            {
                if (importing || exporting) return;
                for (var v = e.target as VisualElement; v != null; v = v.parent)
                {
                    if (v is TextField || v is FloatField || v is IntegerField || v is DropdownField || v is Slider || v is SliderInt) return;
                    if ((v is Button || v is Toggle) && (key == KeyCode.Return || key == KeyCode.KeypadEnter || key == KeyCode.Space)) return;
                }
                if (!Shortcut(key, e.ctrlKey, e.shiftKey, e.altKey || e.commandKey)) return;
            }
            e.PreventDefault(); e.StopImmediatePropagation();
        }
    }
}
