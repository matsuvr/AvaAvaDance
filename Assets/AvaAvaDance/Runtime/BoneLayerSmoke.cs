using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ava.Extensions;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        void CheckBoneLayerOrganization(string directory)
        {
            const string plugin = "ava.bone-layers";
            const string humanId = plugin + ".humanoid", springId = plugin + ".spring", otherId = plugin + ".unmapped";
            const string legacyId = plugin + ".other";
            int priorHuman = WorkspacePreferences.GetInt(RibbonGroupPreference(humanId));
            int priorSpring = WorkspacePreferences.GetInt(RibbonGroupPreference(springId));
            int priorOther = WorkspacePreferences.GetInt(RibbonGroupPreference(otherId));
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException("Bone layers: " + message);
            }

            Check(FindCommand(plugin + ".organize", out var command), "package command loaded");
            ApplyProfile(false);
            ChangeMode(true);
            Check(!Available(command), "disabled in the classic layout");
            ApplyProfile(true);
            ChangeMode(false);
            Check(!Available(command), "disabled in scene editing");
            ChangeMode(true);
            var humanJoints = actor.humanBones.Values.ToHashSet();
            var prefab = actor.modelAssets.Single(asset => asset.Guid == actor.prefabGuid);
            var fbx = actor.modelAssets.Single(asset => asset.Guid == actor.modelGuid);
            var rawModel = Import.FbxNative.Load(fbx.Data, "PhysBone chain fixture");
            try
            {
                var physics = Import.PrefabPhysBones.Read(prefab, rawModel, fbx);
                physics.Chains.Single().Endpoint = Vector3.zero;
                var chainJoints = new HashSet<Transform>();
                var links = BuildPhysBoneLinks(physics, chainJoints);
                var terminal = chainJoints.Single(joint => joint.name == "TestBone");
                Check(!links.ContainsKey(terminal) && !chainJoints.Any(joint => joint.name == "HairIgnored"),
                    "PhysBone chain includes its terminal and respects ignoreTransforms");
            }
            finally { Destroy(rawModel); }
            var human = actor.bones.First(b => humanJoints.Contains(b.Joint) && actor.physBoneJoints.Contains(b.Joint)).Track;
            var spring = actor.bones.First(b => !humanJoints.Contains(b.Joint) && actor.physBoneJoints.Contains(b.Joint)).Track;
            var other = actor.bones.First(b => !humanJoints.Contains(b.Joint) && !actor.physBoneJoints.Contains(b.Joint)).Track;
            var keyed = new BonePose
            {
                Position = new Vector3(.2f, .35f, .6f), Rotation = Quaternion.Euler(25, 35, 15), PhysicsOff = true,
                Curves = new CurveSet { channels = new[] { new EaseCurve { custom = true, a = new Vector2(.2f, .65f), b = new Vector2(.7f, .9f) } } }
            };
            human.Write(10, keyed);
            human.Write(20, BonePose.Rest);
            spring.Write(10, keyed);
            var accent = new MotionLayer { name = "Existing layer" };
            accent.Write(0, new BonePose { Position = Vector3.right, Rotation = Quaternion.Euler(50, 0, 0) });
            human.Layers.Add(accent);
            human.Revision++;
            SelectTrack(human);
            var times = new[] { 0f, 3.5f, 10f, 14.5f, 20f, 25f };
            var samples = times.Select(t => human.Sample(t)).ToArray();
            var draft = human.Current; draft.Position += Vector3.up;
            human.Preview(draft);
            int history = undo.Count;
            var editor = (IBoneLayerEditor)extensions.Single(e => e.manifest.id == plugin);
            var stale = editor.ReadBoneLayerTracks();

            void CheckSamples()
            {
                for (int i = 0; i < times.Length; ++i)
                {
                    var sample = human.Sample(times[i]);
                    Check((sample.Position - samples[i].Position).sqrMagnitude < 1e-10f &&
                        Quaternion.Angle(sample.Rotation, samples[i].Rotation) < .05f && sample.PhysicsOff == samples[i].PhysicsOff,
                        "sampling at " + times[i]);
                }
            }

            try
            {
                Check(Execute(command) && undo.Count == history + 1, "one Undo for the whole model");
                Check(human.GroupedLayer.GroupId == humanId && spring.GroupedLayer.GroupId == springId && other.GroupedLayer.GroupId == otherId,
                    "humanoid mapping takes priority over authored springs");
                Check(human.Layers[1] == accent && human.Current.Equals(draft), "other layers and draft preserved");
                CheckSamples();
                Check(ribbonRows.Where(r => r.layerGroup).Select(r => r.group).SequenceEqual(new[] { humanId, springId, otherId }) &&
                    !ribbonRows.Any(r => r.track == human || r.track == spring || r.track == other), "all three groups initially collapsed");
                var group = ribbonRows.Single(r => r.group == humanId);
                ChooseGroup(group, true, 0, false);
                Check(ribbonRows.Any(r => r.track == human && r.layer == human.GroupedLayer) && !ribbonRows.Any(r => r.track == spring || r.track == other), "expands one group");
                ChooseGroup(ribbonRows.Single(r => r.group == humanId), false, 10, false);
                Check(selectedKeys.Contains((human, 10)) && human.Target == human.GroupedLayer, "group key targets the grouped layer");
                Check(Execute(command) && undo.Count == history + 1 && human.Layers.Count == 2, "repeated command is a no-op");
                var oldTrack = stale.Tracks.Single(t => t.Id == human.Id);
                try
                {
                    editor.OrganizeBoneLayers(stale.Document, stale.Owner, new[] {
                        new BoneLayerAssignment { Track = oldTrack.Id, Revision = oldTrack.Revision, Group = "humanoid", Name = "Humanoid" } });
                    throw new Exception("Bone layers: stale edit accepted");
                }
                catch (InvalidOperationException) { }

                string project = Path.Combine(directory, "grouped-bones.avaproject");
                SaveProject(project);
                string invalidProject = Path.Combine(directory, "invalid-layer-project.avaproject");
                var invalid = CaptureProject();
                invalid.sceneDraft.specVersion = "invalid";
                WriteProjectFile(invalidProject, invalid);
                string humanTrack = human.Id, springTrack = spring.Id, otherTrack = other.Id;
                void FindReloadedTracks()
                {
                    human = actor.bones.Single(b => b.Track.Id == humanTrack).Track;
                    spring = actor.bones.Single(b => b.Track.Id == springTrack).Track;
                    other = actor.bones.Single(b => b.Track.Id == otherTrack).Track;
                }
                try { OpenProject(invalidProject); throw new Exception("Bone layers: invalid project accepted"); }
                catch (InvalidDataException) { }
                FindReloadedTracks();
                ApplyHistory(false);
                Check(human.GroupedLayer == null && spring.GroupedLayer == null && other.GroupedLayer == null && human.KeyAt(10) is BonePose restored &&
                    restored.Position == keyed.Position && human.Layers.Single().id == accent.id, "Undo restores all tracks after project rollback");
                ApplyHistory(true);
                Check(human.GroupedLayer.GroupId == humanId && spring.GroupedLayer.GroupId == springId && other.GroupedLayer.GroupId == otherId,
                    "Redo restores all layers");
                CheckSamples();
                string path = Path.Combine(directory, "grouped-bones.avamotion");
                SaveMotion(path);
                LoadMotion(path);
                CheckSamples();
                Check(human.GroupedLayer.GroupId == humanId && spring.GroupedLayer.GroupId == springId && other.GroupedLayer.GroupId == otherId && human.Target.At(10).HasValue,
                    "layer grouping and keys survive motion save and reload");
                OpenProject(project);
                FindReloadedTracks();
                CheckSamples();
                Check(human.GroupedLayer.GroupId == humanId && spring.GroupedLayer.GroupId == springId && other.GroupedLayer.GroupId == otherId,
                    "layer grouping survives project save and reload");

                var legacy = spring.GroupedLayer;
                legacy.id = legacyId;
                legacy.groupId = null;
                legacy.group = "その他のボーン";
                legacy.name = "Custom spring take";
                var clip = new ClipPlacement { source = legacy.id, sourceEnd = 30 };
                legacy.clips.Add(clip);
                spring.ActiveLayer = legacy.id;
                spring.ActiveClip = clip.id;
                var previousSample = spring.Sample(10);
                var previousKeys = legacy.keys.ToArray();
                history = undo.Count;
                Check(Execute(command) && undo.Count == history + 1 && legacy.GroupId == springId, "legacy group updates in one operation");
                Check(legacy.id == legacyId && legacy.name == "Custom spring take" && legacy.keys.SequenceEqual(previousKeys) &&
                    spring.Target == legacy && spring.ActiveClip == clip.id && legacy.Source(clip.source) == legacy && spring.Sample(10).Equals(previousSample),
                    "migration preserves layer identity, custom name, keys, clip and sample");
                ApplyHistory(false);
                Check(legacy.GroupId == legacyId && legacy.group == "その他のボーン", "migration Undo restores old group");
                ApplyHistory(true);
                Check(legacy.GroupId == springId && legacy.group == "スプリングボーン", "migration Redo restores new group");
                SaveMotion(path);
                LoadMotion(path);
                Check(spring.Target.GroupId == springId && spring.Target.id == legacyId && spring.Target.Source(clip.source) == spring.Target,
                    "migrated group identity survives motion round trip");
                File.WriteAllText(Path.Combine(directory, "bone-layers.txt"), "PASS bone layer package, classification, timeline, sampling, Undo/Redo and save/load");
            }
            finally
            {
                WorkspacePreferences.SetInt(RibbonGroupPreference(humanId), priorHuman);
                WorkspacePreferences.SetInt(RibbonGroupPreference(springId), priorSpring);
                WorkspacePreferences.SetInt(RibbonGroupPreference(otherId), priorOther);
            }
        }
    }
}
