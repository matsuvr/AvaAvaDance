using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ava.Extensions;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        bool CanOrganizeBoneLayers => ribbonProfile && modelMode && actor.bones.Count > 0;

        BoneLayerSnapshot SnapshotBoneLayerTracks()
        {
            var mapped = new HashSet<Transform>(actor.humanBones?.Values.AsEnumerable() ?? Enumerable.Empty<Transform>());
            var springs = new HashSet<Transform>(actor.physBoneJoints);
            var vrmSprings = actor.vrm?.SpringBone?.Springs;
            if (vrmSprings != null)
                springs.UnionWith(vrmSprings.SelectMany(spring => spring.Joints).Where(joint => joint != null).Select(joint => joint.transform));
            return new BoneLayerSnapshot
            {
                Document = documentGeneration,
                Owner = actor.id,
                Tracks = actor.bones.Select(bone => new BoneLayerTrack
                {
                    Id = bone.Track.Id,
                    Revision = bone.Track.Revision,
                    IsHumanoid = mapped.Contains(bone.Joint),
                    IsSpringBone = springs.Contains(bone.Joint)
                }).ToArray()
            };
        }

        sealed class BoneLayerMove
        {
            readonly (int frame, BonePose pose)[] originals;
            readonly string layerId, layerName, activeLayer, activeClip;

            internal BoneLayerMove(MotionTrack<BonePose> track, string id, string name)
            {
                layerId = id;
                layerName = name;
                activeLayer = track.ActiveLayer;
                activeClip = track.ActiveClip;
                originals = track.Frames.Select(f => (f, (BonePose)track.KeyAt(f))).ToArray();
            }

            MotionLayer CreateLayer()
            {
                var layer = new MotionLayer { id = layerId, name = layerName, group = layerName, keys = new List<LayerKey>(originals.Length) };
                foreach (var (at, pose) in originals)
                {
                    var key = LayerKey.From(pose);
                    key.frame = at;
                    layer.keys.Add(key);
                }
                return layer;
            }

            internal void Apply(MotionTrack<BonePose> track, bool forward)
            {
                if (forward) track.Layers.Insert(0, CreateLayer());
                else track.Layers.RemoveAll(layer => layer.id == layerId);
                track.ActiveLayer = forward && activeLayer == null ? layerId : activeLayer;
                track.ActiveClip = activeClip;

                bool physicsOff = false;
                foreach (var (at, pose) in originals)
                {
                    if (!forward)
                    {
                        track.Write(at, pose);
                    }
                    else if (at == 0 || pose.PhysicsOff != physicsOff)
                    {
                        var basis = BonePose.Rest;
                        basis.PhysicsOff = pose.PhysicsOff;
                        track.Write(at, basis);
                    }
                    else track.Write(at, null);
                    physicsOff = pose.PhysicsOff;
                }
            }
        }

        static Action<MotionTrack<BonePose>, bool> RegroupBoneLayer(MotionLayer layer, string groupId, string name)
        {
            string id = layer.id, previousGroupId = layer.groupId, previousGroup = layer.group, previousName = layer.name;
            return (track, forward) =>
            {
                var target = track.Layers.Find(item => item.id == id);
                // Layer identity stays stable for active clips, key history and saved sources.
                target.groupId = forward ? groupId : previousGroupId;
                target.group = forward ? name : previousGroup;
                target.name = forward && previousName == previousGroup ? name : previousName;
                track.Revision++;
            };
        }

        int OrganizeBoneLayers(string plugin, long document, string owner, BoneLayerAssignment[] assignments)
        {
            if (document != documentGeneration || owner != actor.id)
                throw new InvalidOperationException("編集する作品またはモデルが切り替わっています。");
            if (assignments == null) throw new ArgumentNullException(nameof(assignments));

            var tracks = actor.bones.ToDictionary(b => b.Track.Id, b => b.Track);
            var seen = new HashSet<string>();
            var moves = new List<(MotionTrack<BonePose> track, string group, Action<MotionTrack<BonePose>, bool> apply)>();
            string prefix = plugin + ".";
            foreach (var assignment in assignments)
            {
                if (assignment == null || string.IsNullOrEmpty(assignment.Track) || !seen.Add(assignment.Track) ||
                    !tracks.TryGetValue(assignment.Track, out var track) || track.Revision != assignment.Revision)
                    throw new InvalidOperationException("対象のボーントラックが変更されています。");
                if (string.IsNullOrWhiteSpace(assignment.Name) || !Regex.IsMatch(assignment.Group ?? "", "^[a-z0-9-]{1,40}$"))
                    throw new ArgumentException("レイヤーのグループまたは名前が不正です。", nameof(assignments));
                string id = prefix + assignment.Group;
                if (track.GroupedLayer is MotionLayer existing)
                {
                    if (existing.id.StartsWith(prefix, StringComparison.Ordinal) && (existing.GroupId != id || existing.group != assignment.Name))
                        moves.Add((track, id, RegroupBoneLayer(existing, id, assignment.Name)));
                    continue;
                }
                if (track.Layers.Count >= 256 || track.Layers.Any(layer => layer.id == id))
                    throw new InvalidOperationException("レイヤーを追加できません: " + track.Name);
                moves.Add((track, id, new BoneLayerMove(track, id, assignment.Name).Apply));
            }
            if (moves.Count == 0) return 0;

            void Replay(MotionTrack _, bool forward)
            {
                // A failed project open can rebuild the model before restoring its history.
                var model = drawOrder.First(item => item.id == owner);
                var currentTracks = model.bones.ToDictionary(b => b.Track.Id, b => b.Track);
                var targets = moves.Select(move => (move.apply, track: currentTracks[move.track.Id])).ToArray();
                StopPlayback();
                foreach (var (apply, track) in targets) apply(track, forward);
                ClearSimulation(model);
                SyncControls();
                ChangeLayerContext();
            }

            foreach (var id in moves.Select(move => move.group).Distinct())
                WorkspacePreferences.SetInt(RibbonGroupPreference(id), 0);
            firstRow = 0;
            Replay(null, true);
            var history = moves.Select(move => new KeyChange { Track = move.track }).ToArray();
            history[0].Replay = Replay;
            undo.Push(history);
            redo.Clear();
            RefreshKeyButtons();
            return moves.Count;
        }
    }
}
