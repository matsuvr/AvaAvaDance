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
        WorkspaceMenu.Entry menuGroup, submenu, highlight, focusedHeading;
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
            catch (Exception ex) { ReportActionError("操作を完了できませんでした　", ex); Debug.LogWarning(ex.Message); }
            return true;
        }

        void BuildWorkspaceMenus(VisualElement bar)
        {
            BindFileCommands(); BindEditCommands(); BindViewCommands(); BindOtherCommands(); BindPropCommands(); BindModelCommands(); BindWorkspaceCommands();
            englishMenus = WorkspacePreferences.GetInt("EnglishMenus") == 1;
            PresentMenus(bar);
        }

        void BindWorkspaceCommands()
        {
            Bind(Command.ImportModel, PickPackage);
            Bind(Command.ImportProp, () => FileAction("アクセサリ読込", "x", false, ImportAccessoryFile));
            Bind(Command.SaveRig, () => FileAction("リグ保存", "avarig", true, path => WriteFile(path,
                System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(actor.rigProfile, true)))), () => actor.rigProfile != null);
            Bind(Command.AddLight, AddSceneLight, () => additionalLights.Count < MaximumAdditionalLights);
            Bind(Command.AddLayer, () => EditLayer(true), () => selectedTrack?.SupportsLayers == true);
            Bind(Command.CutKeys, CutKeys, () => EditKeys().Any(k => k.frame > 0));
            Bind(Command.CopyKeys, CopyKey, () => EditKeys().Any());
            Bind(Command.PasteKeys, () => PasteOriginalKeys(false), CanPasteOriginal);
            Bind(Command.PasteMirroredKeys, () => PasteOriginalKeys(true), CanPasteOriginal);
            Bind(Command.DeleteKeys, DeleteKeys, () => EditKeys().Any(k => k.frame > 0));
            Bind(Command.InsertTimelineColumn, () => ShiftRibbonColumn(true));
            Bind(Command.DeleteTimelineColumn, () => ShiftRibbonColumn(false));
            Bind(Command.UndoKeys, () => ApplyHistory(false), () => undo.Count > 0);
            Bind(Command.RedoKeys, () => ApplyHistory(true), () => redo.Count > 0);
            Bind(Command.ToggleBookmark, ToggleBookmark);
            Bind(Command.PreviousBookmark, () => JumpBookmark(false), () => bookmarks.Any(f => f < frame));
            Bind(Command.NextBookmark, () => JumpBookmark(true), () => bookmarks.Any(f => f > frame));
            Bind(Command.SelectUnregistered, () => SelectBoneSet(true), ModelBones);
            Bind(Command.SelectColumn, () => SelectKeys(frame, frame, true));
            Bind(Command.SelectTimelineRange, () => SelectKeys(firstFrame, firstFrame + VisibleColumns - 1, true));
            Bind(Command.MirrorPose, MirrorDraft, ModelBones);
            Bind(Command.HandPose, HandPoseDialog, ModelBones);
            Bind(Command.ExternalParent, ParentDialog, ModelBones);
            Bind(Command.RegisterKeys, RegisterSelection, () => selectedTrack != null);
            Bind(Command.CurvePreferences, EditCurvePalette);
            Bind(Command.PlayStop, TogglePlayback, check: () => playing);
            Bind(Command.PlayBeginning, PlayFromBeginning);
            Bind(Command.PlayBookmarks, PlayBetweenBookmarks);
            Bind(Command.ToggleMarkers, ToggleMarkers, check: () => showMarkers);
            Bind(Command.ToggleHandles, ToggleHandles, check: () => showHandles);
            Bind(Command.ToggleExtendedHandles, ToggleExtendedHandles, check: () => extendedHandles);
            Bind(Command.ChannelInspector, () => SelectInspector(InspectorTab.Channels, true));
            Bind(Command.ExpressionInspector, () => SelectInspector(InspectorTab.Expressions, true));
            Bind(Command.SceneInspector, () => SelectInspector(InspectorTab.Scene, true));
            Bind(Command.CurveInspector, () => SelectInspector(InspectorTab.Curve, true));
            Bind(Command.Outliner, ShowOutliner);
            Bind(Command.TimelineWindow, () => ToggleTimeWindow(false), () => NativeDesktopActive);
            Bind(Command.AudioWindow, () => ToggleTimeWindow(true), () => NativeDesktopActive);
            Bind(Command.OperationPanel, ToggleDock, () => NativeDesktopActive);
            Bind(Command.ConfigureToolbar, ConfigureTransport);
            Bind(Command.ShortcutPreferences, EditShortcuts);
            Bind(Command.ResetWorkspace, ResetRibbonLayout);
            Bind(Command.BoneUnit, () => {
                ShowDialog("ボーンの位置単位", out var body);
                var unit = Choice(body, new[] { "0.1 m", "1 m" });
                unit.index = BonePositionUnit == 1 ? 1 : 0;
                Action(body, "適用", true, () => {
                    WorkspacePreferences.SetInt("Workspace.BoneUnit", unit.index);
                    SyncControls(); CloseDialog();
                });
                Action(body, "キャンセル", true, CloseDialog);
            });
        }

        string MenuEntryName(Command id) => combinedMenus.SelectMany(menu => menu.Children)
            .FirstOrDefault(entry => entry.Id == id)?.Label(englishMenus) ?? id.ToString();

        void PresentMenus(VisualElement bar)
        {
            CloseMenu();FocusHeading(null);bar.Clear();menuButtons.Clear();
            ComposeExtensionMenus();
            foreach (var group in combinedMenus)
            {
                var button=Action(bar,group.Label(englishMenus),true,()=>OpenGroup(group));menuButtons.Add(group.Id,button);
                button.RegisterCallback<BlurEvent>(_=>{if(focusedHeading==group)FocusHeading(null);});
            }
        }

        void FocusHeading(WorkspaceMenu.Entry group)
        {
            if(focusedHeading!=null)menuButtons[focusedHeading.Id].RemoveFromClassList("open-menu");
            focusedHeading=group;
            if(group!=null){menuButtons[group.Id].Focus();menuButtons[group.Id].AddToClassList("open-menu");}
        }

        void SwitchLanguage()
        {
            englishMenus = !englishMenus;
            foreach (var group in combinedMenus) menuButtons[group.Id].text = group.Label(englishMenus);
            WorkspacePreferences.SetInt("EnglishMenus", englishMenus ? 1 : 0); RefreshTimelineLabels();
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

        WorkspaceMenu.Entry HeadingAt(Vector2 position) => combinedMenus.FirstOrDefault(g => menuButtons[g.Id].worldBound.Contains(position));

        void OpenGroup(WorkspaceMenu.Entry group)
        {
            CloseMenu(); FocusHeading(null);
            menuGroup = group;
            menuButtons[group.Id].AddToClassList("open-menu");
            menuLayer = Box(MenuRoot, "menu-overlay", "menu-overlay");
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

        ScrollView CreateList(WorkspaceMenu.Entry parent, Rect anchor, bool nested)
        {
            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("workspace-dropdown");
            menuLayer.Add(list);
            var root = MenuRoot.worldBound;
            float width = Mathf.Min(englishMenus ? 338 : 285, root.width);
            float x = nested ? anchor.xMax : anchor.xMin;
            if (nested && x + width > root.xMax) x = anchor.xMin - width;
            float y = nested ? anchor.yMin : anchor.yMax;
            float height = parent.Children.Sum(c => c.Id == Command.Separator ? 6 : 22) + 12;
            if (nested) y = Mathf.Max(root.yMin, Mathf.Min(y, root.yMax - height));
            list.style.left = Mathf.Clamp(x - root.xMin, 0, root.width - width);
            list.style.top = y - root.yMin; list.style.width = width;
            list.style.height = Mathf.Min(height, Mathf.Max(22, root.yMax - y));
            foreach (var entry in parent.Children)
            {
                if (entry.Id == Command.Separator) { Box(list, "", "workspace-menu-divider"); continue; }
                bool branch = entry.Children.Length > 0;
                bool enabled = branch || Available(entry.Id);
                bool tick = commands.TryGetValue(entry.Id, out var b) && b.Checked != null && b.Checked();
                var row = new Button { name = entry.Id.ToString(), focusable = false };
                row.AddToClassList("workspace-menu-row"); list.Add(row);
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

        void OpenSubmenu(WorkspaceMenu.Entry entry, VisualElement anchor)
        {
            CloseSubmenu(); submenu = entry;
            subList = CreateList(entry, anchor.worldBound, true);
            highlight = null;
        }

        void Highlight(WorkspaceMenu.Entry entry)
        {
            if (highlight != null) menuLayer.Q<Button>(highlight.Id.ToString())?.RemoveFromClassList("menu-selection");
            highlight = entry;
            var row = menuLayer.Q<Button>(entry.Id.ToString());
            row?.AddToClassList("menu-selection");
            if (row != null) row.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(row);
        }

        void Activate(WorkspaceMenu.Entry entry, VisualElement row)
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
                int n = combinedMenus.Length;
                OpenGroup(combinedMenus[(Array.IndexOf(combinedMenus, menuGroup) + n + (key == KeyCode.RightArrow ? 1 : -1)) % n]);
            }
            else
            {
                var item = key == KeyCode.Return || key == KeyCode.KeypadEnter ? highlight :
                    choices.FirstOrDefault(c => c.Access(englishMenus) != '\0' && c.Access(englishMenus) == (char)key);
                if (item != null) Activate(item, menuLayer.Q<Button>(item.Id.ToString()));
            }
        }

        static bool EditingField(VisualElement target, KeyCode key)
        {
            bool IsInput(VisualElement element)
            {
            for (var v = element; v != null; v = v.parent)
            {
                if (v is TextField || v is FloatField || v is IntegerField || v is DropdownField || v is Slider || v is SliderInt) return true;
                if ((v is Button || v is Toggle) && (key == KeyCode.Return || key == KeyCode.KeypadEnter || key == KeyCode.Space)) return true;
            }
            return false;
            }
            return IsInput(target) || IsInput(target?.focusController?.focusedElement as VisualElement);
        }

        bool DeletePanelSelection(VisualElement element)
        {
            for(var v=element;v!=null;v=v.parent){
                if(v is TextField || v is IntegerField || v is FloatField)return false;
                if(v==waveform || v.name=="floating-wave"){DeleteAudio();return true;}
                if(v==modeField){if(modelMode)RemoveCurrentAvatar();return true;}
                if(v==propList){if(activeProp!=null)RemoveProp(activeProp);return true;}
            }
            return false;
        }

        void MainMenuKey(KeyDownEvent e)
        {
            if (importDialog != null) return;
            if(!NativeDesktopActive && e.target is VisualElement view && view.panel==MenuRoot.panel && !Application.isFocused && ToolFocused()){e.StopImmediatePropagation();return;}
            var key = e.keyCode;
            if(DuplicateMainSymbol(e.target as VisualElement,key))return;
            bool repeatKey=symbol.Present?!pressedSymbols.Add(symbol.vk):key!=KeyCode.None && !pressedKeys.Add(key);
            if (suppressedKeys.Contains(key)) { e.StopImmediatePropagation(); e.PreventDefault(); return; }
            if (key == KeyCode.None && menuLayer != null && char.IsLetter(e.character)) key = (KeyCode)char.ToLowerInvariant(e.character);
            if(contextLayer!=null) { if(key==KeyCode.Escape)CloseTimelineMenu(); else return; }
            else if (modal != null)
            {
                if (key != KeyCode.Escape) return;
                if (exporting) cancelExport = true;
                else if (shaderDownloadCancellation != null) shaderDownloadCancellation.Cancel();
                else if (modal.userData is Action cancelDialog) cancelDialog();
                else CloseDialog();
            }
            else if(ribbonProfile && key==KeyCode.Delete && !repeatKey && !e.ctrlKey && !e.shiftKey && !e.altKey &&
                menuLayer==null && !importing && !exporting && DeletePanelSelection(e.target as VisualElement)) { }
            else if(menuLayer==null && focusedHeading==null && RouteNamedKey(e,repeated:repeatKey)) { }
            else if (StandardKeymap && (key == KeyCode.LeftAlt || key == KeyCode.RightAlt) &&
                CurrentKeyArea(e.target as VisualElement) == "ビューポート") return;
            else if (key==KeyCode.F10 || key==KeyCode.LeftAlt || key==KeyCode.RightAlt)
            {
                if(menuLayer!=null)CloseMenu();
                else if(focusedHeading!=null){FocusHeading(null);MenuRoot.Q("workspace").Focus();}
                else FocusHeading(combinedMenus[0]);
            }
            else if (e.altKey && !e.ctrlKey && !e.shiftKey)
            {
                if (key == KeyCode.B)
                {
                    var display = combinedMenus.First(g => g.Id == Command.View);
                    OpenGroup(display);
                    var background = display.Children.First(g => g.Id == Command.Background);
                    OpenSubmenu(background, mainList.Q<Button>(Command.Background.ToString()));
                    e.StopImmediatePropagation();
                    return;
                }
                var group = combinedMenus.FirstOrDefault(g => g.Access(englishMenus) == (char)key);
                if (group != null) OpenGroup(group);
                else if (key == KeyCode.Return || key == KeyCode.KeypadEnter) { CloseMenu(); Execute(Command.Fullscreen); }
                else return;
            }
            else if (menuLayer != null) { if (!e.ctrlKey) MenuNavigation(key); }
            else if(focusedHeading!=null){
                if(key==KeyCode.Escape){FocusHeading(null);MenuRoot.Q("workspace").Focus();}
                else if(key==KeyCode.DownArrow || key==KeyCode.Return)OpenGroup(focusedHeading);
                else if(key==KeyCode.RightArrow || key==KeyCode.LeftArrow){
                    int n=combinedMenus.Length;
                    FocusHeading(combinedMenus[(Array.IndexOf(combinedMenus,focusedHeading)+n+(key==KeyCode.RightArrow?1:-1))%n]);
                }else{
                    var group=combinedMenus.FirstOrDefault(g=>g.Access(englishMenus)==(char)key);
                    if(group==null)return;OpenGroup(group);
                }
            }
            else if (ribbonProfile)
            {
                if (importing || exporting || EditingField(e.target as VisualElement, key)) return;
                var resolved = symbol.Present && !e.ctrlKey ? symbol.Transform : key;
                if (!RouteWorkspaceKey(resolved, e, repeatKey)) return;
            }
            else
            {
                if (importing || exporting) return;
                if (EditingField(e.target as VisualElement, key)) return;
                if (!Shortcut(key, e.ctrlKey, e.shiftKey, e.altKey || e.commandKey) && !RouteNamedKey(e,true)) return;
            }
            e.PreventDefault(); e.StopImmediatePropagation();
        }
    }
}
