using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static int checks;
        static void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks++; }
        BoneBinding AddBone(string branch, string name, float x)
        {
            var parent = new Transform { name = branch, parent = actor.avatar.transform };
            var bone = new BoneBinding
            {
                Joint = new Transform { name = name, parent = parent },
                Track = new MotionTrack<BonePose> { Name = name, CurrentValue = new BonePose { Position = new Vector3 { x = x }, Rotation = Quaternion.identity } }
            };
            actor.bones.Add(bone); actor.modelTracks.Add(bone.Track); return bone;
        }
        MotionTrack<float> AddMorph(string name, float weight)
        {
            var track = new MotionTrack<float> { Name = "表情："+name, CurrentValue = weight };
            actor.modelTracks.Add(track); return track;
        }
        static void Rejected(Action action, string message)
        {
            try { action(); } catch (InvalidDataException) { checks++; return; }
            throw new Exception(message);
        }
        static void Main()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ava-pose-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "pose.avapose");
            try
            {
                var source = new DanceApp();
                source.AddBone("Hair", "tip", 1); source.AddBone("Sleeve", "tip", 2); source.AddBone("Rig", "hand", 3);
                source.AddMorph("Smile", .75f);
                source.SavePoseFile(file);
                var document = JsonUtility.FromJson<SavedPose>(File.ReadAllText(file));
                Check(document.format == "AvaAvaDance pose 1" && document.bones.Length == 3 && document.morphs[0].weight == .75f, "native pose and morph serialization");
                Rejected(() => source.SavePoseFile(Path.Combine(dir,"pose.vpd")), "VPD must not be a public save format");

                var target = new DanceApp();
                // Reverse duplicate-name order: a display label or first-name match would swap these.
                var sleeve = target.AddBone("Sleeve", "tip", 0); var hair = target.AddBone("Hair", "tip", 0);
                var hand = target.AddBone("RenamedRig", "hand", 0); var smile = target.AddMorph("Smile", .1f);
                object curve = new object(); var held = hair.Track.Current; held.Curves = curve; held.PhysicsOff = true; hair.Track.Restore(held);
                target.LoadPoseFile(file);
                Check(hair.Track.Current.Position.x == 1 && sleeve.Track.Current.Position.x == 2 && hand.Track.Current.Position.x == 3, "hierarchy disambiguation and unique-name fallback");
                Check(smile.Current == .75f && hair.Track.Current.Curves == curve && hair.Track.Current.PhysicsOff, "morphs load; target curves/physics retained");
                Check(target.undo.Count == 1 && target.undo.Peek().Length == 4 && target.undo.Peek().All(c => c.IsDraft && c.Frame == 42), "one production draft undo operation");
                foreach (var change in target.undo.Peek()) change.Track.Restore(change.Before);
                Check(hair.Track.Current.Position.x == 0 && sleeve.Track.Current.Position.x == 0 && hand.Track.Current.Position.x == 0 && smile.Current == .1f, "one undo restores bones and morphs");
                foreach (var change in target.undo.Peek()) change.Track.Restore(change.After);
                Check(hair.Track.Current.Position.x == 1 && smile.Current == .75f, "redo snapshot restores pose");

                void Write(SavedPose data) => File.WriteAllText(file, JsonUtility.ToJson(data));
                // A missing path must never choose the first of duplicate bone names.
                document.bones[0].path = null; document.bones = document.bones.Take(1).ToArray();
                Write(document); target.LoadPoseFile(file);
                Check(target.status.text.Contains("同名未適用 1") && hair.Track.Current.Position.x == 1 && sleeve.Track.Current.Position.x == 2, "ambiguous bone skipped, unambiguous morph retained");
                document.morphs = Array.Empty<SavedPoseMorph>(); Write(document);
                Rejected(() => target.LoadPoseFile(file), "no matching targets must fail");

                document.bones[0].path = new[] { "Hair", "tip" };
                document.bones[0].position.x = 9;
                document.morphs = new[] { new SavedPoseMorph { name = "Smile", weight = .2f }, null };
                Write(document); int undoCount = target.undo.Count;
                Rejected(() => target.LoadPoseFile(file), "late invalid expression must fail before applying bone");
                Check(hair.Track.Current.Position.x == 1 && smile.Current == .75f && target.undo.Count == undoCount, "validation is atomic");
                document.morphs = Array.Empty<SavedPoseMorph>(); document.bones[0].rotation = default; Write(document);
                Rejected(() => target.LoadPoseFile(file), "zero quaternion must fail");
                document.bones[0].rotation = Quaternion.identity; document.bones[0].position.x = float.PositiveInfinity;
                Rejected(() => ValidatePoseFile(document), "non-finite position must fail");
                document.bones[0].position.x = 1; document.morphs = new[] { new SavedPoseMorph { name = "Smile", weight = float.NaN } };
                Rejected(() => ValidatePoseFile(document), "non-finite expression must fail");
                document.morphs = Array.Empty<SavedPoseMorph>(); document.bones = new[] { document.bones[0], document.bones[0] }; Write(document);
                Rejected(() => target.LoadPoseFile(file), "duplicate target entries must fail atomically");
                Check(hair.Track.Current.Position.x == 1 && target.undo.Count == undoCount, "duplicate target does not apply");
                Check(PosePathKey(new[] {"A/B", "tip"}) != PosePathKey(new[] {"A", "B", "tip"}), "slash in bone name cannot alias a hierarchy");
                document.format = null; Write(document);
                Rejected(() => target.LoadPoseFile(file), "format marker is required");
                Console.WriteLine($"PASS {checks} pose checks");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
