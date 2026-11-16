using System;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Run by the real Player menu smoke test. Not a substitute for GPU validation.
        void CheckSceneLights()
        {
            var before = CaptureProject();
            var history = undo.ToArray(); var future = redo.ToArray();
            var originalTrack = selectedTrack; bool originalMode = modelMode;
            int originalFrame = frame;
            void Require(bool test,string message) { if(!test)throw new InvalidOperationException("Scene lights: "+message); }
            try {
                ChangeMode(false); RestoreAdditionalLights(null); ResetHistory();
                AddSceneLight(); var added = EditingLight;
                Require(added != lightTrack && additionalLights.Count == 1,"add and select");
                SetFrame(10); var primary = lightTrack.Current;
                var pose = added.Current; pose.Color = Color.red; pose.Direction = Vector3.right;
                added.Preview(pose); RegisterKey(added);
                Require(added.Frames.Contains(10) && lightTrack.Current.Equals(primary),"independent key registration");
                ApplyHistory(false); Require(!added.Frames.Contains(10),"key undo");
                ApplyHistory(true); Require(added.Frames.Contains(10),"key redo");
                SetFrame(5);
                var item = additionalLights.Single();
                Require(item.Lamp.color == added.Current.Color && item.Lamp.intensity > 0,"timeline applies rendered light");
                var savedNames = CaptureAdditionalLights(); var saved = CaptureMotion(false);
                RestoreAdditionalLights(null); RestoreAdditionalLights(savedNames); RestoreMotion(saved,false);
                Require(additionalLights.Count == 1 && additionalLights[0].Track.Frames.Contains(10),"motion restore");
                editingLight = additionalLights[0].Track; SelectTrack(editingLight);
                RemoveSceneLight(); Require(additionalLights.Count == 0 && !item.Lamp.gameObject.activeSelf,"remove");
                ApplyHistory(false); Require(additionalLights.Count == 1 && item.Lamp.gameObject.activeSelf,"remove undo");
                ApplyHistory(true); Require(additionalLights.Count == 0,"remove redo");
                for (int i=0;i<MaximumAdditionalLights+2;i++)AddSceneLight();
                Require(additionalLights.Count == MaximumAdditionalLights,"bounded lights");
                bool rejected=false;
                try { RestoreAdditionalLights(new[]{"same","same"}); } catch(System.IO.InvalidDataException) { rejected=true; }
                Require(rejected && additionalLights.Count == MaximumAdditionalLights,"reject malformed names before changing scene");
                editingLight=lightTrack;SelectTrack(lightTrack);RemoveSceneLight();
                Require(sceneTracks.Contains(lightTrack),"primary light cannot be removed");
            } finally {
                RestoreAdditionalLights(before.additionalLights); RestoreMotion(before.sceneMotion,false);
                ResetHistory(); foreach(var group in history.Reverse())undo.Push(group);foreach(var group in future.Reverse())redo.Push(group);
                ChangeMode(originalMode);SelectTrack(originalTrack);SetFrame(originalFrame);RestoreMotion(before.sceneDraft,false,true);
                SyncControls();RefreshKeyButtons();
            }
        }
    }
}
