using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Run from -avaMenuSmoke in Unity/Windows. Unlike the numerical harness,
        // this uses the application's real history, tracks and .avamotion loader.
        void CheckTimelineRetiming(string directory)
        {
            void Check(bool ok, string message)
            { if (!ok) throw new InvalidOperationException("Timeline retiming: " + message); }

            var oldSceneTracks = sceneTracks.ToArray();
            var oldSidecar = sceneSidecar;
            var originals = EditableTracks().Select(e => e.track).Distinct()
                .Select(t => (track: t, draft: t.CurrentValue, revision: t.Revision)).ToArray();
            var oldUndo = undo.ToArray(); var oldRedo = redo.ToArray();
            var oldKeys = selectedKeys.ToArray(); var oldRows = selectedRows.ToArray(); var oldBones = boneSelection.ToArray();
            var old = (modelMode, selectedTrack, sceneSelection, activeProp, frame, firstFrame, firstRow,
                rangeSelected, physicsMode, forceOnPlayback, scrubSound);
            var playback = (playing, playbackRange, playStart, playEnd, startedAt, audioCycle);
            double elapsed = Time.unscaledTimeAsDouble - startedAt;
            string oldStatus = status.text;
            var bone = new MotionTrack<BonePose>("Retiming bone " + Guid.NewGuid().ToString("N"), BonePose.Rest, BonePose.Blend, _ => { });
            var morph = new MotionTrack<float>("Retiming morph " + Guid.NewGuid().ToString("N"), 0, Mathf.Lerp, _ => { });
            var curve = new EaseCurve { custom = true, a = new Vector2(.2f, .1f), b = new Vector2(.7f, .9f) };
            var source = new KeySource { name = Enumerable.Range(1, 15).Select(i => (byte)i).ToArray() };
            BonePose Pose(int at) => new BonePose { Position = new Vector3(at * .1f, 0, 0), Rotation = Quaternion.Euler(0, at, 0),
                PhysicsOff = at == 2, Curves = CurveSet.With(null, 0, curve) };
            try
            {
                StopPlayback(); physicsMode = PhysicsMode.None; forceOnPlayback = false; scrubSound = false;
                sceneTracks.Add(bone); sceneTracks.Add(morph);
                foreach (int at in new[] { 1, 2, 4, 90 }) { bone.Write(at, Pose(at)); morph.Write(at, at / 100f); }
                bone.SetSource(2, source);
                morph.Write(2, new MorphKey { Weight = .8f, Curve = curve });
                var beforeBone = bone.Frames.ToDictionary(f => f, bone.SnapshotKey);
                var beforeMorph = morph.Frames.ToDictionary(f => f, morph.SnapshotKey);
                ChangeMode(false); SelectTrack(bone); SetFrame(0);
                var selected = new MotionTrack[] { bone, morph }.SelectMany(t => new[] { 1, 2, 4 }.Select(f => (track: t, frame: f))).ToArray();
                var batch = PlanTimeScale(selected, 0, .5);
                Check(batch.MergedCount == 2 && batch.OverwrittenCount == 0, "dense compression reports merged keys");
                int history = undo.Count;
                ApplyTimeScale(batch);
                Check(undo.Count == history + 1 && bone.Frames.SequenceEqual(new[] { 0, 1, 2, 90 }), "single history transaction and compressed frames");
                Check(Equals(bone.KeyAt(1), beforeBone[2]) && ReferenceEquals(bone.SourceAt(1), source), "bone curves, physics flag and source move together");
                Check(Equals(morph.KeyAt(1), .8f) && morph.CurveAt(1).Equals(curve), "morph curve moves with weight");
                Check(Equals(bone.KeyAt(90), beforeBone[90]) && Equals(morph.SnapshotKey(90), beforeMorph[90]), "unselected keys unchanged");
                ApplyHistory(false);
                Check(beforeBone.All(k => Equals(bone.SnapshotKey(k.Key), k.Value)) && bone.Frames.Count == beforeBone.Count &&
                    beforeMorph.All(k => Equals(morph.SnapshotKey(k.Key), k.Value)) && morph.Frames.Count == beforeMorph.Count &&
                    ReferenceEquals(bone.SourceAt(2), source) && bone.SourceAt(1) == null, "one undo restores compressed keys and metadata");
                ApplyHistory(true);
                Check(bone.KeyAt(4) == null && Equals(morph.KeyAt(1), .8f) && ReferenceEquals(bone.SourceAt(1), source), "redo restores retimed data");
                history = undo.Count;
                ApplyTimeScale(PlanTimeScale(new[] { (track: (MotionTrack)bone, frame: 1) }, 0, 1));
                Check(undo.Count == history, "factor one leaves history unchanged");

                // A selected key may overwrite an unselected key at zero. The
                // preserved base key still exists, and one undo restores its value.
                object zero = bone.SnapshotKey(0);
                var ontoZero = PlanTimeScale(new[] { (track: (MotionTrack)bone, frame: 1) }, 0, .1);
                Check(ontoZero.OverwrittenCount == 1, "unselected zero overwrite is visible");
                ApplyTimeScale(ontoZero);
                Check(bone.KeyAt(0) != null && ReferenceEquals(bone.SourceAt(0), source), "zero remains a valid initial key");
                ApplyHistory(false);
                Check(Equals(bone.SnapshotKey(0), zero) && ReferenceEquals(bone.SourceAt(1), source), "zero overwrite undo restores both keys");

                var baseKeys = bone.Frames.ToDictionary(f => f, bone.SnapshotKey);
                var layer = new MotionLayer { name = "Retiming layer" };
                var shared = new LayerSource { name = "Shared retiming source" };
                foreach (int at in new[] { 0, 2, 4 }) shared.Write(at, Pose(at));
                layer.sources.Add(shared);
                var first = new ClipPlacement { start = 10, source = shared.id, sourceEnd = 4, cycles = 1 };
                var second = new ClipPlacement { start = 20, source = shared.id, sourceEnd = 4, cycles = 1 };
                layer.clips.Add(first); layer.clips.Add(second); layer.Validate();
                bone.Layers.Add(layer); bone.ActiveLayer = layer.id; bone.ActiveClip = first.id;
                SetFrame(10); history = undo.Count;
                ApplyTimeScale(PlanTimeScale(bone.EditFrames.Select(f => (track: (MotionTrack)bone, frame: f)), 0, .5));
                Check(undo.Count == history + 1 && shared.keys.Select(k => k.frame).SequenceEqual(new[] { 0, 1, 2 }), "clip source is the edited layer");
                Check(baseKeys.All(k => Equals(bone.SnapshotKey(k.Key), k.Value)) && bone.Frames.Count == baseKeys.Count, "retiming a layer leaves base keys unchanged");
                Check(layer.Contribution(11).position == Pose(2).Position && layer.Contribution(21).position == Pose(2).Position &&
                    first.start == 10 && second.start == 20 && first.sourceEnd == 4, "both clips use retimed source without moving their placements");
                ApplyHistory(false);
                Check(shared.keys.Select(k => k.frame).SequenceEqual(new[] { 0, 2, 4 }) && bone.ActiveLayer == layer.id && bone.ActiveClip == first.id, "layer undo restores keys and editing context");
                ApplyHistory(true);
                Check(shared.keys.Select(k => k.frame).SequenceEqual(new[] { 0, 1, 2 }), "layer redo");

                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "timeline-retiming.avamotion");
                // Isolate the file round trip to the two fixtures. Real scene
                // keys, source objects and layer identities are never reloaded.
                sceneTracks.Clear(); sceneTracks.Add(bone); sceneTracks.Add(morph);
                sceneSidecar = new VmdSidecar();
                SaveMotion(path);
                bone.Write(1, BonePose.Rest); bone.ClearSources(); shared.keys.Clear(); morph.Write(1, .01f);
                LoadMotion(path);
                Check(bone.KeyAt(1) is BonePose restored && restored.Position == Pose(2).Position && restored.PhysicsOff &&
                    CurveSet.Get(restored.Curves, 0).Equals(curve) && bone.SourceAt(1).name.SequenceEqual(source.name), "avamotion restores retimed bone data");
                Check(Equals(morph.KeyAt(1), .8f) && morph.CurveAt(1).Equals(curve), "avamotion restores retimed morph curve");
                Check(bone.ActiveLayer == layer.id && bone.ActiveClip == first.id && bone.EditFrames.SequenceEqual(new[] { 0, 1, 2 }) &&
                    bone.Target.clips.Count == 2 && bone.Target.clips.All(c => c.source == shared.id), "avamotion restores shared clip sources and active target");
                Debug.Log("AVA_TIMELINE_RETIMING PASS compression, overwrites, metadata, one-step undo/redo, shared layer source and avamotion round trip");
            }
            finally
            {
                StopPlayback();
                sceneTracks.Clear(); sceneTracks.AddRange(oldSceneTracks); sceneSidecar = oldSidecar;
                sceneSelection = old.sceneSelection; activeProp = old.activeProp;
                ChangeMode(old.modelMode); SelectTrack(old.selectedTrack); SetFrame(old.frame);
                foreach (var entry in originals) { entry.track.Restore(entry.draft); entry.track.Revision = entry.revision; }
                physicsMode = old.physicsMode; forceOnPlayback = old.forceOnPlayback; scrubSound = old.scrubSound;
                sceneSelection = old.sceneSelection; activeProp = old.activeProp;
                selectedKeys.Clear(); selectedKeys.UnionWith(oldKeys); rangeSelected = old.rangeSelected;
                selectedRows.Clear(); selectedRows.UnionWith(oldRows); boneSelection.Clear(); boneSelection.AddRange(oldBones);
                undo.Clear(); foreach (var change in oldUndo.Reverse()) undo.Push(change);
                redo.Clear(); foreach (var change in oldRedo.Reverse()) redo.Push(change);
                firstRow = old.firstRow; SetFirstFrame(old.firstFrame);
                ForEachActor(calculationOrder, RefreshRigPreview); RestoreSolvedPose(); ApplyView(); SyncControls(); RefreshKeyButtons();
                playbackRange = playback.playbackRange; playStart = playback.playStart; playEnd = playback.playEnd; audioCycle = playback.audioCycle;
                startedAt = playback.playing ? Time.unscaledTimeAsDouble - elapsed : playback.startedAt;
                if (playback.playing) { playing = true; playButton.text = "停止"; PlayAudioAt(frame); }
                status.text = oldStatus;
            }
        }
    }
}
