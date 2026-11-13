using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        void CheckVrmWorkflow(string legacy, string modern, string motion, string dir)
        {
            void Check(bool ok, string test) { if (!ok) throw new InvalidOperationException(test); }
            foreach (string path in new[] { legacy, modern })
            {
                var asset = ReadVrmFile(path);
                ImportModel(asset, new List<Asset> { asset }, null);
                var offline=System.Threading.Tasks.Task.Run(()=>RigFiles.Read(path)).GetAwaiter().GetResult().Single();
                string hash;using(var sha=System.Security.Cryptography.SHA256.Create())hash=Convert.ToBase64String(sha.ComputeHash(asset.Data));
                var crown=offline.Find("headTop");var liveCrown=actor.rigProfile.Find("headTop");
                Check(offline.sourceHash==hash && actor.rigProfile.sourceHash==hash,"VRM source hash");
                Check(crown?.origin=="weightedVertex" && liveCrown?.origin=="weightedVertex","VRM head measurement");
                Check(offline.shapes.Length==actor.rigProfile.shapes.Length,"VRM collider count");
                Check(actor.shadowCasters.SelectMany(r => r.sharedMaterials)
                    .All(m => m != null && m.shader.isSupported && m.shader.name != "Hidden/InternalErrorShader"), "VRM material");
                int history = undo.Count;
                SetFrame(0);
                ImportMotionFile(motion);
                var head = actor.humanBones[HumanBodyBones.Head];
                var headTrack = actor.bones.Single(b => b.Joint == head).Track;
                var blink = actor.modelTracks.OfType<MotionTrack<float>>().Single(t => MorphName(t) == "blink");
                var hips = actor.humanBones[HumanBodyBones.Hips];
                var hipsTrack = actor.bones.Single(b => b.Joint == hips).Track;
                Check(undo.Count == history+1 && headTrack.LastFrame == 30, "One undoable motion");
                SetFrame(14); Check(blink.Current == 0, "STEP before key");
                SetFrame(15);
                Check(blink.Current == 1 && Mathf.Abs(Quaternion.Angle(headTrack.Current.Rotation, Quaternion.identity)-60) < .1f, "STEP and retargeted head");
                actor.vrm.Runtime.Process();
                Check(Mathf.Abs(actor.vrm.Runtime.LookAt.Yaw + 30) < .1f && actor.vrm.Runtime.Expression.ActualWeights[UniVRM10.ExpressionKey.Blink] > .99f, "VRM gaze and expression");
                Check(actor.ikChains.All(c => !actor.modelSwitch.Current.Enabled(c.Handle.Track.Name)), "FK legs");
                Check(Mathf.Abs(hips.parent.TransformVector(hipsTrack.Current.Position).magnitude / actor.hipsHeight - .2f) < .001f, "Scaled hips");
                SetFrame(5);
                Check(Mathf.Abs(hips.parent.TransformVector(hipsTrack.Current.Position).magnitude / actor.hipsHeight - .2f*7/27) < .001f, "Cubic hip interpolation");
                ApplyHistory(false); Check(headTrack.LastFrame == 0 && hipsTrack.LastFrame == 0, "Undo VRMA");
                ApplyHistory(true); Check(headTrack.LastFrame == 30 && hipsTrack.LastFrame == 30, "Redo VRMA");
                SetFrame(15);
                SelectTrack(headTrack);
                var pose = headTrack.Current; pose.Rotation = Quaternion.Euler(12, 8, 4);
                headTrack.Preview(pose); RegisterKey(headTrack);
                WritePreview(Path.Combine(dir, Path.GetFileNameWithoutExtension(path)+".png"));
            }
            string project = Path.Combine(dir, "vrm.avadance");
            SaveProject(project); NewProject(); OpenProject(project);
            Check(drawOrder.Count == 2 && drawOrder.All(a => a.vrm != null), "Both embedded VRMs restored");
            SetFrame(15);
            foreach (var a in drawOrder)
            {
                var head = a.bones.Single(b => b.Joint == a.humanBones[HumanBodyBones.Head]);
                Check(Quaternion.Angle(head.Track.Current.Rotation, Quaternion.Euler(12,8,4)) < .1f, "Edited key restored");
            }
            SetFrame(0); TogglePlayback(); Check(playing, "VRMA playback"); StopPlayback();
            SetFrame(15);
            var leg = actor.ikChains.First(c => c.Lower != null);
            SetIKEnabled(leg, true);
            RegisterKey(actor.modelSwitch);
            var handle = leg.Handle.Track.Current;
            handle.Position += new Vector3(0, .08f, .08f);
            leg.Handle.Track.Preview(handle); RegisterKey(leg.Handle.Track);
            SetFrame(15);
            var solvedFoot = leg.Tip.position;
            var selectedHead = actor.bones.Single(b => b.Joint == actor.humanBones[HumanBodyBones.Head]).Track;
            var edited = selectedHead.Current;
            var draft = edited; draft.Rotation = Quaternion.Euler(45, 12, 4);
            selectedHead.Preview(draft);
            string exported = Path.Combine(dir, "edited.vrma");
            SaveVrma(exported);
            Check(frame == 15 && Quaternion.Angle(selectedHead.Current.Rotation, draft.Rotation) < .1f, "Export preserves draft");
            SetFrame(42);
            ImportMotionFile(exported);
            SetFrame(57);
            Check(selectedHead.LastFrame == 72 && Quaternion.Angle(selectedHead.Current.Rotation, edited.Rotation) < .1f, "Exported motion at nonzero playhead");
            Check(!actor.modelSwitch.Current.Enabled(leg.Handle.Track.Name) && Vector3.Distance(leg.Tip.position, solvedFoot)<.001f, "Baked foot IK round trip");
            var insertedBlink = actor.modelTracks.OfType<MotionTrack<float>>().Single(t => MorphName(t) == "blink");
            Check(insertedBlink.Current > .99f && Mathf.Abs(actor.vrm.Runtime.LookAt.LookAtInput.YawPitch.Value.Yaw+30)<.1f, "Exported face and gaze");
            ApplyHistory(false); Check(selectedHead.LastFrame == 30, "Undo pasted clip retains original motion");
            int undoSize = undo.Count;
            bool rejected = false;
            try { LoadVrma(modern); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && undo.Count == undoSize && selectedHead.LastFrame == 30, "Reject model as motion without changing keys");
            string[] args = Environment.GetCommandLineArgs();
            int sample = Array.IndexOf(args, "-avaSampleVrma");
            if (sample >= 0)
            {
                SetFrame(0); ImportMotionFile(args[sample+1]);
                Check(actor.bones.Single(b => b.Joint == actor.humanBones[HumanBodyBones.RightUpperArm]).Track.LastFrame >= 90,
                    "three-vrm-animation example");
            }
            int fbx = Array.IndexOf(args, "-avaHumanoidProject");
            if (fbx >= 0)
            {
                OpenProject(args[fbx+1]); ChangeMode(true); SetFrame(0);
                Check(actor.vrm == null, "Expected a package avatar");
                ImportMotionFile(motion); SetFrame(15);
                var head = actor.bones.Single(b => b.Joint == actor.humanBones[HumanBodyBones.Head]);
                Check(Mathf.Abs(Quaternion.Angle(head.Track.Current.Rotation, Quaternion.identity)-60) < .1f, "Humanoid FBX retarget");
                string clip = Path.Combine(dir, "humanoid.vrma");
                SaveVrma(clip);
                int start = LastFrame()+1;
                SetFrame(start); ImportMotionFile(clip); SetFrame(start+15);
                Check(head.Track.KeyAt(start+15) != null, "Humanoid FBX export and reimport");
            }
        }
    }
}
