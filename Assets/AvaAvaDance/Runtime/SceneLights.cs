using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // The original light remains the shadow/main pass light. Additional lights
        // use the existing ForwardAdd path, including the official shader path.
        const int MaximumAdditionalLights = 7;
        sealed class SceneLight
        {
            public MotionTrack<LightPose> Track;
            public Light Lamp;
        }
        readonly List<SceneLight> additionalLights = new List<SceneLight>();
        readonly List<SceneLight> retainedLights = new List<SceneLight>();
        MotionTrack<LightPose> editingLight;
        DropdownField lightSelector;
        Button addLightButton, removeLightButton;
        MotionTrack<LightPose> EditingLight => editingLight != null && sceneTracks.Contains(editingLight) ? editingLight : lightTrack;

        void BuildLightSelector(VisualElement parent)
        {
            lightSelector = new DropdownField { name = "scene-light-selector", tooltip = "編集する方向光（影は主照明）" };
            parent.Add(lightSelector);
            lightSelector.RegisterValueChangedCallback(e => {
                var target = sceneTracks.OfType<MotionTrack<LightPose>>().FirstOrDefault(t => t.Name == e.newValue);
                if (target == null) return;
                StopPlayback(); editingLight = target; ChangeMode(false); SelectTrack(target);
            });
            var row = Box(parent,"scene-light-management","row");
            addLightButton = Action(row,"照明追加",true,AddSceneLight);
            removeLightButton = Action(row,"照明削除",true,RemoveSceneLight);
            SyncLightSelector();
        }

        void SyncLightSelector()
        {
            if (selectedTrack is MotionTrack<LightPose> selected && sceneTracks.Contains(selected)) editingLight = selected;
            if (lightSelector == null || lightTrack == null) return;
            lightSelector.choices = sceneTracks.OfType<MotionTrack<LightPose>>().Select(t => t.Name).ToList();
            lightSelector.SetValueWithoutNotify(EditingLight.Name);
            addLightButton?.SetEnabled(additionalLights.Count < MaximumAdditionalLights && !exporting);
            removeLightButton?.SetEnabled(EditingLight != lightTrack && !exporting);
        }

        SceneLight CreateSceneLight(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(previewRig.transform, false);
            var lamp = go.AddComponent<Light>(); lamp.type = LightType.Directional;
            lamp.cullingMask = 1 << 30; lamp.shadows = LightShadows.None;
            lamp.renderMode = LightRenderMode.ForcePixel;
            var item = new SceneLight { Lamp = lamp };
            item.Track = new MotionTrack<LightPose>(name,LightPose.Initial,LightPose.Blend,p => ApplySceneLight(lamp,p));
            retainedLights.Add(item);
            return item;
        }

        void SetSceneLightPresent(SceneLight item, bool present, int index)
        {
            if (present && !additionalLights.Contains(item)) {
                additionalLights.Insert(Mathf.Clamp(index,0,additionalLights.Count),item);
                sceneTracks.Add(item.Track); item.Lamp.gameObject.SetActive(true); item.Track.Seek(frame);
            } else if (!present) {
                additionalLights.Remove(item); sceneTracks.Remove(item.Track); item.Lamp.gameObject.SetActive(false);
                selectedRows.Remove(item.Track); selectedKeys.RemoveWhere(k => k.track == item.Track);
            }
            var target = present ? item.Track : lightTrack;
            ChangeMode(false); SelectTrack(target);
        }

        void AddSceneLight()
        {
            if (exporting || additionalLights.Count >= MaximumAdditionalLights) return;
            StopPlayback();
            int number = 2;
            while (retainedLights.Any(l => l.Track.Name == "照明 " + number) || sceneTracks.Any(t => t.Name == "照明 " + number)) number++;
            var item = CreateSceneLight("照明 " + number); int index = additionalLights.Count;
            SetSceneLightPresent(item,true,index);
            PushOperation(lightTrack,(_,forward) => SetSceneLightPresent(item,forward,index));
        }

        void RemoveSceneLight()
        {
            if (exporting) return;
            var item = additionalLights.FirstOrDefault(l => l.Track == EditingLight);
            if (item == null) return;
            StopPlayback(); int index = additionalLights.IndexOf(item);
            SetSceneLightPresent(item,false,index);
            PushOperation(lightTrack,(_,forward) => SetSceneLightPresent(item,!forward,index));
        }

        string[] CaptureAdditionalLights() => additionalLights.Select(l => l.Track.Name).ToArray();

        // Call only after a successful project replacement, after rollback is no
        // longer possible. Deleted lights are retained while their undo exists.
        void ReleaseInactiveSceneLights()
        {
            if (undo.Count != 0 || redo.Count != 0) return;
            foreach (var item in retainedLights.Where(l => !additionalLights.Contains(l)).ToArray()) {
                if (item.Lamp != null) Destroy(item.Lamp.gameObject);
                retainedLights.Remove(item);
            }
        }

        void RestoreAdditionalLights(string[] names)
        {
            names ??= Array.Empty<string>();
            if (names.Length > MaximumAdditionalLights || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 80 || n == lightTrack.Name) || names.Distinct().Count() != names.Length)
                throw new InvalidDataException("追加照明の名前または数が不正です。");
            foreach (var item in retainedLights) {
                sceneTracks.Remove(item.Track);
                if (item.Lamp != null) item.Lamp.gameObject.SetActive(false);
            }
            additionalLights.Clear(); editingLight = lightTrack;
            foreach (var name in names) {
                var item = retainedLights.FirstOrDefault(l => l.Track.Name == name) ?? CreateSceneLight(name);
                additionalLights.Add(item); sceneTracks.Add(item.Track); item.Lamp.gameObject.SetActive(true); item.Track.Seek(frame);
            }
            SyncLightSelector();
        }
    }
}
