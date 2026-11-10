using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class MorphGroup
        {
            public DropdownField List;
            public Slider Weight;
            public VisualElement Control;
            public List<MotionTrack<float>> Tracks = new List<MotionTrack<float>>();
            public MotionTrack<float> Current => List.index >= 0 && List.index < Tracks.Count ? Tracks[List.index] : null;
        }

        readonly MorphGroup[] morphGroups = new MorphGroup[4];
        static readonly string[] MorphCategories = { "目", "リップ", "まゆ", "その他" };

        void BuildMorphControls(VisualElement bottom)
        {
            morph = Panel(bottom, "morph", "表情操作");
            var grid = Box(morph, "morph-grid", "morph-grid");
            for (int i = 0; i < morphGroups.Length; i++)
            {
                var group = new MorphGroup(); morphGroups[i] = group;
                var control = Box(grid, "morph-" + i, "morph-control"); group.Control = control;
                var header = Box(control, "weight-row", "row");
                header.Add(new Label(MorphCategories[i]));
                Action(header, "登録", true, () => RegisterKey(group.Current));
                group.List = Choice(control, new[] { "なし" });
                group.Weight = new Slider(0, 1) { showInputField = true }; control.Add(group.Weight);
                group.List.RegisterValueChangedCallback(_ => { if (group.Current != null) SelectTrack(group.Current); });
                group.Weight.RegisterValueChangedCallback(e =>
                {
                    var track = group.Current;
                    if (track == null || !float.IsFinite(e.newValue)) { SyncMorphControls(); return; }
                    StopPlayback(); track.Preview(Mathf.Clamp01(e.newValue)); SelectTrack(track);
                });
            }
        }

        static int MorphCategory(string name)
        {
            string n = name.ToLowerInvariant();
            bool Match(params string[] parts) => parts.Any(n.Contains);
            if (Match("まゆ","眉","brow","mayu")) return 2;
            if (Match("まばたき","ウィンク","目","瞳","eye","wink","blink","lash")) return 0;
            if (Match("口","舌","リップ","mouth","lip","vrc.v_","viseme","tongue") ||
                new[] { "あ","い","う","え","お","a","i","u","e","o","aa","ih","ou","ee","oh" }.Contains(n)) return 1;
            return 3;
        }

        void BindMorphs()
        {
            if (actor.vrm != null)
            {
                var expressions = actor.vrm.Runtime.Expression;
                foreach (var key in expressions.ExpressionKeys)
                    if (!key.IsLookAt) AddMorph(key.Name, weight => expressions.SetWeight(key, weight));
            }
            else BindMeshMorphs();
            foreach (var group in morphGroups)
            {
                group.List.choices = group.Tracks.Select(MorphName).ToList();
                group.List.SetValueWithoutNotify(group.List.choices.FirstOrDefault() ?? "なし");
            }
            SyncMorphControls();
        }

        void AddMorph(string name, Action<float> apply)
        {
            var track = new MotionTrack<float>("表情：" + name, 0, Mathf.Lerp, apply);
            actor.modelTracks.Add(track);
            morphGroups[MorphCategory(name)].Tracks.Add(track);
        }

        void BindMeshMorphs()
        {
            var targets = new Dictionary<string, List<(SkinnedMeshRenderer renderer, int index)>>();
            foreach (var skin in actor.avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = skin.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (!targets.TryGetValue(name, out var list))
                        targets.Add(name, list = new List<(SkinnedMeshRenderer, int)>());
                    list.Add((skin, i));
                }
            }
            foreach (var item in targets)
            {
                var destinations = item.Value;
                AddMorph(item.Key, weight =>
                {
                    foreach (var (skin, index) in destinations) skin.SetBlendShapeWeight(index, 100 * weight);
                });
            }
        }

        void SyncMorphControls()
        {
            foreach (var group in morphGroups)
            {
                group.Control.SetEnabled(group.Tracks.Count > 0);
                group.List.SetEnabled(group.Tracks.Count > 1);
                if (selectedTrack is MotionTrack<float> selected && group.Tracks.Contains(selected))
                    group.List.SetValueWithoutNotify(selected.Name.Substring(3));
                group.Weight.SetValueWithoutNotify(group.Current?.Current ?? 0);
            }
        }
    }
}
