using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Uses the redistributable production fixture and the PhysBone solver.
        void CheckPhysicsBaking(string directory)
        {
            void Require(bool value, string message)
            { if (!value) throw new InvalidOperationException("Physics baking: " + message); }
            void ResetSolver()
            { foreach (var model in calculationOrder) ClearSimulation(model); }
            void Run(IEnumerator routine, Action<int> afterYield = null)
            {
                int count = 0;
                try { while (routine.MoveNext()) afterYield?.Invoke(++count); }
                finally { (routine as IDisposable)?.Dispose(); }
            }
            string Content() => JsonUtility.ToJson(CaptureMotion(true));
            var original = CaptureMotion(true);
            var oldMode = physicsMode;
            bool oldForce = forceOnPlayback; int oldFrame = frame;
            try
            {
                RestoreMotion(original, true); ResetHistory(); forceOnPlayback = false;
                physicsMode = PhysicsMode.Trace;
                var bones = PhysicsBakeBones(actor, 0, 30);
                var drive = actor.bones.Single(b => b.Joint.name == "LeftUpperLeg");
                Require(!actor.authoredLinks.ContainsKey(drive.Joint), "fixture drive bone must have no authored PhysBone");
                ToggleDynamicBone(drive);
                Require(actor.dynamicLinks.All(link => link.Bone != drive), "only authored PhysBones can be enabled");
                var turn = BonePose.Rest; turn.Rotation = Quaternion.Euler(0, 0, 12);
                drive.Track.Write(0, BonePose.Rest); drive.Track.Write(15, turn); drive.Track.Write(30, BonePose.Rest);
                var outside = BonePose.Rest; outside.Position = new Vector3(.01f, .02f, .03f);
                bones[0].Track.Write(45, outside);
                string before = Content();
                var expectedPositions = new Vector3[31, bones.Length];
                var expectedRotations = new Quaternion[31, bones.Length];
                ResetSolver();
                for (int at = 0; at <= 30; at++)
                {
                    EvaluateAnimation(at, true);
                    for (int b = 0; b < bones.Length; b++)
                    {
                        expectedPositions[at, b] = bones[b].Joint.localPosition;
                        expectedRotations[at, b] = bones[b].Joint.localRotation;
                    }
                }
                Require(bones.Select((bone, b) => Quaternion.Angle(expectedRotations[0, b], expectedRotations[30, b])).Any(a => a > .05f), "fixture must exercise moving physics");
                SetFrame(7);
                int history = undo.Count;
                Run(BakePhysics(actor, bones, 0, 30));
                Require(undo.Count == history + 1, "PhysBone one bake transaction: " + status.text);
                Require(bones.All(b => Enumerable.Range(0, 31).All(at => b.Track.KeyAt(at) is BonePose pose && pose.PhysicsOff)), "physics disabled on every baked key");
                Require(Equals(bones[0].Track.KeyAt(45), outside), "outside key preserved");
                string baked = Content();
                foreach (bool simulate in new[] { false, true })
                {
                    physicsMode = simulate ? PhysicsMode.Trace : PhysicsMode.None; ResetSolver();
                    for (int at = 0; at <= 30; at++)
                    {
                        EvaluateAnimation(at, true);
                        for (int b = 0; b < bones.Length; b++)
                        {
                            Require(Vector3.Distance(bones[b].Joint.localPosition, expectedPositions[at, b]) < .0002f,
                                "PhysBone baked position, simulate=" + simulate + " frame=" + at + " bone=" + bones[b].Track.Name);
                            Require(Quaternion.Angle(bones[b].Joint.localRotation, expectedRotations[at, b]) < .05f,
                                "PhysBone baked rotation, simulate=" + simulate + " frame=" + at + " bone=" + bones[b].Track.Name);
                        }
                    }
                }
                ApplyHistory(false); Require(Content() == before, "one undo restores keys");
                ApplyHistory(true); Require(Content() == baked, "one redo restores complete result");
                string path = Path.Combine(directory, "baked-PhysBone.avaproject");
                SaveProject(path); OpenProject(path);
                bones = PhysicsBakeBones(actor, 0, 30);
                foreach (bool simulate in new[] { false, true })
                {
                    physicsMode = simulate ? PhysicsMode.Trace : PhysicsMode.None; ResetSolver();
                    for (int at = 0; at <= 30; at++)
                    {
                        EvaluateAnimation(at, true);
                        for (int b = 0; b < bones.Length; b++)
                            Require(Vector3.Distance(bones[b].Joint.localPosition, expectedPositions[at, b]) < .0002f &&
                                Quaternion.Angle(bones[b].Joint.localRotation, expectedRotations[at, b]) < .05f, "saved bake playback");
                    }
                }

                RestoreMotion(original, true); ResetHistory(); physicsMode = PhysicsMode.Trace; SetFrame(9);
                bones = PhysicsBakeBones(actor, 0, 30);
                string unchanged = Content();
                foreach (int first in new[] { 0, 20 })
                {
                    var drafts = EditableTracks().Select(e => e.track.CurrentValue).ToArray();
                    var transforms = actor.bones.Select(b => (b.Joint.localPosition, b.Joint.localRotation)).ToArray();
                    Run(BakePhysics(actor, bones, first, 30), _ => cancelPhysicsBake = true);
                    Require(Content() == unchanged && undo.Count == 0 && frame == 9 && !physicsBaking && modal == null, "cancel is atomic during warmup and sampling");
                    Require(EditableTracks().Select(e => e.track.CurrentValue).SequenceEqual(drafts), "cancel restores drafts");
                    Require(actor.bones.Select(b => (b.Joint.localPosition, b.Joint.localRotation)).SequenceEqual(transforms), "cancel restores transforms");
                }
                var changed = bones[0].Track; long revision = changed.Revision;
                Run(BakePhysics(actor, bones, 0, 30), _ => changed.Revision++);
                changed.Revision = revision;
                Require(Content() == unchanged && undo.Count == 0 && !physicsBaking, "revision change discards pending bake");

                // The first sample also yields when it is the last one. An edit
                // or cancel at that point must still prevent the pending commit.
                Run(BakePhysics(actor, bones, 0, 0), _ => changed.Revision++);
                changed.Revision = revision;
                Require(Content() == unchanged && undo.Count == 0, "last-sample revision change discards completed bake");
                Run(BakePhysics(actor, bones, 0, 0), _ => cancelPhysicsBake = true);
                Require(Content() == unchanged && undo.Count == 0, "last-sample cancellation discards completed bake");

                Run(BakePhysics(actor, bones, 6, 18));
                Require(undo.Count == 1 && bones.All(b => b.Track.KeyAt(5) != null && b.Track.KeyAt(19) != null), "partial range restores boundary keys");
                Require(bones.All(b => !((BonePose)b.Track.KeyAt(5)).PhysicsOff && !((BonePose)b.Track.KeyAt(19)).PhysicsOff), "physics resumes outside baked interval");
                ApplyHistory(false); Require(Content() == unchanged, "partial range undo");
                var layer = new MotionLayer(); bones[0].Track.Layers.Add(layer);
                bool rejected = false;
                try { PhysicsBakeBones(actor, 0, 30); } catch (ArgumentException) { rejected = true; }
                finally { bones[0].Track.Layers.Remove(layer); }
                Require(rejected, "layer overlap rejected");
                Debug.Log("AVA_PHYSICS_BAKING PASS PhysBone replay, no double simulation, undo/redo, project reload, partial ranges, cancellation and revision guards");
            }
            finally
            {
                physicsMode = oldMode; forceOnPlayback = oldForce;
                ResetSolver(); RestoreMotion(original, true); ResetHistory(); SetFrame(oldFrame);
            }
        }
    }
}
