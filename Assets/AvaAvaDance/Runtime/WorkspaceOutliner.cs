using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class OutlinerItem
        {
            public string Name;
            public MotionTrack Track;
            public Actor Owner;
            public Func<bool> Visible;
            public Action ToggleVisibility;
            public readonly List<OutlinerItem> Children = new List<OutlinerItem>();
        }

        VisualElement outliner;
        ScrollView outlinerScroll;
        TextField outlinerSearch;
        Button outlinerButton;
        bool outlinerOpen;
        MotionTrack outlinerAnchor;
        long outlinerDocument = -1;
        readonly List<OutlinerItem> outlinerRoots = new List<OutlinerItem>();
        readonly List<(OutlinerItem item, Button name, Button visibility)> outlinerRows = new();
        readonly HashSet<OutlinerItem> outlinerExpanded = new();
        readonly HashSet<MotionTrack> hiddenBoneMarkers = new();
        MotionTrack[] outlinerTracks = Array.Empty<MotionTrack>();
        (Prop prop, BoneBinding parent)[] outlinerParents = Array.Empty<(Prop, BoneBinding)>();

        void BuildOutliner(VisualElement stage)
        {
            outliner = Box(stage, "outliner", "outliner", "hidden");
            var header = Box(outliner, "", "row", "outliner-header");
            header.Add(new Label("Outliner"));
            var close = Action(header, "−", true, ShowOutliner);
            close.AddToClassList("outliner-disclosure");
            close.tooltip = "アウトライナーを折りたたむ";
            outlinerSearch = new TextField { name = "outliner-search", tooltip = "名前で検索" };
            outlinerSearch.label = "検索";
            outliner.Add(outlinerSearch);
            outlinerSearch.RegisterValueChangedCallback(_ => DrawOutlinerRows());
            outlinerScroll = new ScrollView { name = "outliner-tree" };
            outliner.Add(outlinerScroll);
            outlinerOpen = WorkspacePreferences.GetInt("Workspace.Outliner") != 0;
            outliner.schedule.Execute(RefreshOutliner).Every(200);
        }

        void ShowOutliner()
        {
            outlinerOpen = !outlinerOpen;
            if (!outlinerOpen)
            {
                outlinerTracks = Array.Empty<MotionTrack>(); outlinerParents = Array.Empty<(Prop, BoneBinding)>();
                outlinerRoots.Clear(); outlinerRows.Clear(); outlinerExpanded.Clear(); outlinerScroll.Clear();
                outlinerAnchor = null;
            }
            WorkspacePreferences.SetInt("Workspace.Outliner", outlinerOpen ? 1 : 0);
            ApplyWorkspaceSizing();
        }

        void RefreshOutliner()
        {
            if (outlinerDocument != documentGeneration)
            {
                outlinerAnchor = null; hiddenBoneMarkers.Clear();
                outlinerDocument = documentGeneration;
            }
            if (!outlinerOpen || cameraTrack == null) return;
            var tracks = drawOrder.SelectMany(owner => owner.bones.Select(b => (MotionTrack)b.Track)
                .Prepend(owner.modelSwitch)).Concat(sceneTracks).ToArray();
            var parents = props.Select(prop => (prop, ResolvePropParent(prop.Track.Current))).ToArray();
            if (!tracks.SequenceEqual(outlinerTracks) ||
                !parents.SequenceEqual(outlinerParents))
            {
                outlinerTracks = tracks; outlinerParents = parents;
                hiddenBoneMarkers.IntersectWith(tracks);
                var expanded = outlinerExpanded.Select(item => item.Track).ToHashSet();
                outlinerExpanded.Clear(); outlinerRoots.Clear();
                var bones = new Dictionary<BoneBinding, OutlinerItem>();
                foreach (var owner in drawOrder)
                {
                    var root = new OutlinerItem { Name = owner.label, Track = owner.modelSwitch, Owner = owner,
                        Visible = () => !owner.modelSwitch.Current.Hidden,
                        ToggleVisibility = () => {
                            var pose = owner.modelSwitch.Current;
                            pose.Hidden = !pose.Hidden;
                            SetDraftValues(new Dictionary<MotionTrack, object> { [owner.modelSwitch] = pose }, true);
                        } };
                    outlinerRoots.Add(root);
                    if (expanded.Contains(root.Track)) outlinerExpanded.Add(root);
                    var nodeByJoint = new Dictionary<Transform, OutlinerItem>();
                    foreach (var bone in owner.bones)
                    {
                        var node = new OutlinerItem { Name = bone.Track.Name, Track = bone.Track, Owner = owner,
                            Visible = () => !hiddenBoneMarkers.Contains(bone.Track),
                            ToggleVisibility = () => {
                                if (!hiddenBoneMarkers.Add(bone.Track)) hiddenBoneMarkers.Remove(bone.Track);
                                RedrawOverlay();
                            } };
                        bones.Add(bone, node);
                        if (bone.Joint != null) nodeByJoint[bone.Joint] = node;
                        if (expanded.Contains(node.Track)) outlinerExpanded.Add(node);
                    }
                    foreach (var bone in owner.bones)
                    {
                        var parent = bone.Joint != null ? bone.Joint.parent : null;
                        while (parent != null && !nodeByJoint.ContainsKey(parent)) parent = parent.parent;
                        (parent == null ? root : nodeByJoint[parent]).Children.Add(bones[bone]);
                    }
                }
                var scene = new OutlinerItem { Name = "Scene" };
                outlinerRoots.Add(scene); outlinerExpanded.Add(scene);
                foreach (var track in sceneTracks.Where(t => !(t is MotionTrack<AccessoryPose>)))
                {
                    var item = new OutlinerItem { Name = track.Name, Track = track };
                    if (track is MotionTrack<LightPose> lamp)
                    {
                        item.Visible = () => lamp.Current.Direction.sqrMagnitude >= .000001f;
                        item.ToggleVisibility = () => {
                            var pose = lamp.Current;
                            pose.Direction = item.Visible() ? Vector3.zero : lamp.Sample(frame).Direction;
                            if (pose.Direction == Vector3.zero && !item.Visible()) pose.Direction = LightPose.Initial.Direction;
                            SetDraftValues(new Dictionary<MotionTrack, object> { [lamp] = pose }, true);
                        };
                    }
                    scene.Children.Add(item);
                }
                foreach (var (prop, parent) in parents)
                {
                    var item = new OutlinerItem { Name = prop.Data.name, Track = prop.Track,
                        Visible = () => !prop.Track.Current.Hidden,
                        ToggleVisibility = () => {
                            var pose = prop.Track.Current; pose.Hidden = !pose.Hidden;
                            SetDraftValues(new Dictionary<MotionTrack, object> { [prop.Track] = pose }, true);
                        } };
                    (parent != null && bones.TryGetValue(parent, out var node) ? node : scene).Children.Add(item);
                }
                DrawOutlinerRows();
            }
            foreach (var row in outlinerRows)
            {
                row.name.EnableInClassList("active-tool", row.item.Track != null && selectedRows.Contains(row.item.Track));
                if (row.visibility != null) row.visibility.text = row.item.Visible() ? "●" : "○";
            }
        }

        void DrawOutlinerRows()
        {
            outlinerScroll.Clear(); outlinerRows.Clear();
            string query = outlinerSearch.value.Trim();
            bool search = query.Length > 0;
            var matches = new HashSet<OutlinerItem>();
            bool Match(OutlinerItem item, bool ancestorMatches)
            {
                bool self = ancestorMatches || item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                bool match = self;
                foreach (var child in item.Children) match |= Match(child, self);
                if (match) matches.Add(item);
                return match;
            }
            void Draw(OutlinerItem item, int depth)
            {
                if (search && !matches.Contains(item)) return;
                var row = Box(outlinerScroll, "", "row", "outliner-row");
                row.style.paddingLeft = Mathf.Min(depth, 8) * 10;
                bool expandable = item.Children.Count > 0;
                bool open = search || outlinerExpanded.Contains(item);
                var disclosure = Action(row, expandable ? (open ? "−" : "+") : "", expandable && !search, () => {
                    if (!outlinerExpanded.Add(item)) outlinerExpanded.Remove(item);
                    DrawOutlinerRows(); RefreshOutliner();
                });
                disclosure.AddToClassList("outliner-disclosure");
                disclosure.tooltip = expandable ? "階層を開閉" : "";
                if (!expandable) disclosure.style.visibility = Visibility.Hidden;
                var name = Action(row, item.Name, true);
                name.AddToClassList("outliner-name");
                if (item.Track == null) name.clicked += () => {
                    if (!outlinerExpanded.Add(item)) outlinerExpanded.Remove(item);
                    DrawOutlinerRows(); RefreshOutliner();
                };
                else name.RegisterCallback<ClickEvent>(e => SelectOutlinerItem(item, e.ctrlKey || e.commandKey, e.shiftKey));
                var visibility = item.Visible == null ? null : Action(row, item.Visible() ? "●" : "○", true, () => {
                    if (importing || exporting || modal != null || KeysCaptured()) return;
                    item.ToggleVisibility(); RefreshOutliner();
                });
                if (visibility != null) {
                    visibility.AddToClassList("outliner-disclosure");
                    visibility.tooltip = item.Owner != null && item.Track != item.Owner.modelSwitch ? "ボーンマーカーの表示" : "表示を切り替え";
                }
                outlinerRows.Add((item, name, visibility));
                if (open) foreach (var child in item.Children) Draw(child, depth + 1);
            }
            if (search) foreach (var item in outlinerRoots) Match(item, false);
            foreach (var item in outlinerRoots) Draw(item, 0);
            if (outlinerRows.Count == 0) outlinerScroll.Add(new Label("一致する項目なし"));
            foreach (var row in outlinerRows)
                row.name.EnableInClassList("active-tool", row.item.Track != null && selectedRows.Contains(row.item.Track));
        }

        static IEnumerable<OutlinerItem> AllOutlinerItems(OutlinerItem item) =>
            item.Children.SelectMany(AllOutlinerItems).Prepend(item);

        void SelectOutlinerItem(OutlinerItem item, bool toggle, bool extend)
        {
            if (importing || exporting || KeysCaptured() || modal != null) return;
            var current = new HashSet<MotionTrack>(toggle || extend ? selectedRows : Enumerable.Empty<MotionTrack>());
            var flat = outlinerRows.Select(row => row.item).Where(row => row.Track != null).ToList();
            int anchor = flat.FindIndex(row => row.Track == outlinerAnchor);
            if (extend && anchor >= 0)
            {
                int at = flat.IndexOf(item);
                for (int i = Math.Min(anchor, at); i <= Math.Max(anchor, at); i++) current.Add(flat[i].Track);
            }
            else if (!current.Add(item.Track) && toggle) current.Remove(item.Track);
            if (!extend) outlinerAnchor = item.Track;
            var lead = current.Contains(item.Track) ? item : flat.LastOrDefault(row => current.Contains(row.Track)) ??
                outlinerRoots.SelectMany(AllOutlinerItems).LastOrDefault(row => current.Contains(row.Track));
            if (lead != null)
            {
                if (lead.Owner != null && (!modelMode || actor != lead.Owner)) ChooseMode(lead.Owner.label);
                else if (lead.Owner == null && modelMode) ChangeMode(false);
            }
            SelectTrack(lead?.Track);
            selectedRows.UnionWith(current);
            if (modelMode) boneSelection.AddRange(actor.bones.Where(b => current.Contains(b.Track)));
            if (lead != null) SelectInspector(InspectorTab.Channels);
            RefreshTimelineLabels(); RedrawOverlay();
        }
    }
}
