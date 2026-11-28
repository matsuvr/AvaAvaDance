using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;
using System.Threading.Tasks;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Sample evaluated joints in avatar-facing space, with standing hip height as the unit.
        string SampleHumanoidSkeleton(string avatarPath,string motionPath,string output)
        {
            if (IsVrm(avatarPath))
            {
                var model=ReadVrmFile(avatarPath);
                ImportModel(model,new List<UnityPackageReader.PackageAsset>{model},null);
            }
            else
            {
                var assets=UnityPackageReader.Read(avatarPath);
                var models=assets.Where(a=>a.HasAsset && a.Path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase)).ToArray();
                var selected=PackageAvatar.Choices(assets,models).Single();
                ImportModel(selected.Model,assets,selected.Prefab);
            }
            Execute(Command.PhysicsOff);
            int history=undo.Count;
            ImportMotionFile(motionPath);
            if (!motionPath.EndsWith(".avamotion",StringComparison.OrdinalIgnoreCase) && undo.Count!=history+1) throw new InvalidDataException(status.text);
            int end=actor.modelTracks.Max(t=>t.LastFrame);
            var facing=Quaternion.Inverse(actor.humanForward);
            using var file=new StreamWriter(Path.Combine(output,"joints.csv"));
            file.WriteLine("frame,joint,x,y,z");
            for (int f=0;f<=end;f++)
            {
                EvaluateAnimation(f,false);
                foreach (var (role,joint) in actor.humanBones)
                {
                    var p=facing*(joint.position-actor.avatar.transform.position)/actor.hipsHeight;
                    file.WriteLine(FormattableString.Invariant($"{f},{role},{p.x:R},{p.y:R},{p.z:R}"));
                }
            }
            return $"PASS {end+1} frames, {actor.humanBones.Count} joints";
        }

        // Player regression for the four Go Wild exports; the external files stay with their owner.
        string CheckHumanoidMotion(string avatarPath,string source,string output)
        {
            var asset=ReadVrmFile(avatarPath);
            ImportModel(asset,new List<UnityPackageReader.PackageAsset>{asset},null);
            physicsMode=PhysicsMode.None;
            var tracks=actor.humanBones.ToDictionary(k=>k.Key,k=>actor.bones.Single(b=>b.Joint==k.Value).Track);
            var reference=new Dictionary<HumanBodyBones,BonePose[]>();
            var log=new StringBuilder();
            var baseline=CaptureMotion(true);
            foreach(string ext in new[]{"vrma","bvh","fbx","anim"})
            {
                int startFrame=ext=="vrma"?0:7;
                int endFrame=startFrame+521;
                SetFrame(startFrame);
                int history=undo.Count;
                ImportMotionFile(Directory.GetFiles(source,"*."+ext,SearchOption.AllDirectories).Single());
                if (undo.Count!=history+1 || tracks[HumanBodyBones.Hips].LastFrame!=endFrame)
                    throw new Exception("Duration or import transaction "+ext+" "+status.text);
                var baked=tracks.Where(k=>k.Value.LastFrame==endFrame).ToDictionary(k=>k.Key,k=>
                    Enumerable.Range(startFrame,522).Select(f=>k.Value.Sample(f)).ToArray());
                if (baked.Count<45) throw new Exception("Missing humanoid tracks: "+ext);
                if(ext=="vrma") reference=baked;
                double total=0, hipTotal=0;
                double max=0;
                float hipMax=0;
                int count=0;
                foreach (var (id,poses) in reference)
                {
                    if(!baked.TryGetValue(id,out var samples)) throw new Exception("Missing "+id+" in "+ext);
                    for(int i=0;i<poses.Length;i++)
                    {
                        double angle=RotationError(poses[i].Rotation,samples[i].Rotation);
                        total+=angle*angle; max=Math.Max(max,angle); count++;
                        if(id==HumanBodyBones.Hips)
                        {
                            float shift=actor.humanBones[id].parent.TransformVector(poses[i].Position-samples[i].Position).magnitude/actor.hipsHeight;
                            hipTotal+=shift*shift; hipMax=Mathf.Max(hipMax,shift);
                        }
                    }
                }
                double rms=Math.Sqrt(total/count);
                log.AppendLine($"{ext}: {baked.Count} tracks, {count} comparisons, rotation RMS {rms:F6}° max {max:F6}°, normalized hip RMS {Math.Sqrt(hipTotal/522):F5} max {hipMax:F5}");
                Debug.Log(log.ToString());
                if(ext!="anim" && (rms>0.3 || hipMax>.005)) throw new Exception(log.ToString());
                if(ext=="anim" && (rms>15 || hipMax>.15f)) throw new Exception(log.ToString());
                CheckImportedMotionSizing(startFrame,endFrame,Path.Combine(output,ext+"-sized.avamotion"));
                log.AppendLine(ext+": native sizing, one-step undo/redo and sized .avamotion round trip");
                string path=Path.Combine(output,ext+".avamotion");
                SaveMotion(path);
                ApplyHistory(false);
                if(tracks.Values.Any(t=>t.LastFrame!=0)) throw new Exception("Undo "+ext);
                ImportMotionFile(path);
                CheckImportedMotionSizing(startFrame,endFrame,Path.Combine(output,ext+"-reloaded-sized.avamotion"));
                log.AppendLine(ext+": sizing after native motion reload");
                foreach(var (id,samples) in baked)
                    for(int i=0;i<samples.Length;i++)
                    {
                        var restored=tracks[id].Sample(startFrame+i);
                        if(Quaternion.Angle(restored.Rotation,samples[i].Rotation)>.08f || (restored.Position-samples[i].Position).sqrMagnitude>1e-10f)
                            throw new Exception("Native round trip "+ext+" "+id);
                    }
                SetFrame(startFrame+135);
                WritePreview(Path.Combine(output,ext+".png"));
                if(ext=="vrma") { SetFrame(0); SaveProject(Path.Combine(output,"Go Wild.avaproject")); }
                RestoreMotion(baseline,true);
            }
            OpenProject(Path.Combine(output,"Go Wild.avaproject"));
            CheckImportedMotionSizing(0,521,Path.Combine(output,"project-reloaded-sized.avamotion"));
            log.AppendLine("project: sizing after saved project reload");
            SetFrame(135);
            foreach (var (id,samples) in reference)
            {
                var track=actor.bones.Single(b=>b.Joint==actor.humanBones[id]).Track;
                if (track.LastFrame!=521 || Quaternion.Angle(track.Current.Rotation,samples[135].Rotation)>.08f)
                    throw new Exception("Project round trip "+id);
            }
            return "PASS\n"+log;
        }

        async Task CheckFileSizing(string packagePath, string directory)
        {
            var source = actor;
            var bone = source.bones.First(b => b.RigJointIndex >= 0);
            var pose = BonePose.Rest; pose.Rotation = Quaternion.Euler(0, 0, 35); pose.PhysicsOff = true;
            bone.Track.Write(12, pose);
            string input = Path.Combine(directory, "入力.avamotion");
            SaveMotion(input);
            byte[] original = File.ReadAllBytes(input);
            bone.Track.Write(12, BonePose.Rest);

            var package = UnityPackageReader.Read(packagePath);
            var model = package.First(a => a.HasAsset && a.Path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase));
            ImportModel(model, package, PackageAvatar.LegacyPrefab(package, model));
            var target = actor;
            var outputBone = target.bones.Single(b => b.RigJointIndex == bone.RigJointIndex);
            ChooseMode(source.label);
            string before = JsonUtility.ToJson(CaptureMotion(true));
            OpenMotionSizing();
            await Task.Yield();
            modal.Q<DropdownField>("sizing-target").value = target.label;
            var choose = PackageFileDialog.AutomationChooser;
            int history = undo.Count;
            try {
                PackageFileDialog.AutomationChooser = (_, filter, saving) => filter == "avamotion" && !saving ? input : null;
                ClickSmokeButton(modal.Q<Button>("sizing-open-file"));
            } finally { PackageFileDialog.AutomationChooser = choose; }
            if (modal.Q<IntegerField>("sizing-last-frame").value != 12 || !modal.Q<Label>("sizing-input").text.Contains(Path.GetFileName(input)))
                throw new Exception("File not selected: " + status.text);
            foreach (var option in new[]{"sizing-feet", "sizing-hands", "sizing-twist"}) modal.Q<Toggle>(option).value = false;
            ClickSmokeButton(modal.Q<Button>("sizing-run"));
            while (motionSizingCancellation != null) await Task.Yield();
            bool Sized() => outputBone.Track.KeyAt(12) is BonePose key && key.PhysicsOff &&
                Quaternion.Angle(key.Rotation, pose.Rotation) < .1f;
            if (actor != target) throw new Exception("Sizing did not select the destination model: " + status.text);
            if (!Sized()) throw new Exception("Unexpected destination key for " + outputBone.Track.Name + ": " +
                JsonUtility.ToJson(outputBone.Track.KeyAt(12)) + "; " + status.text);
            if (undo.Count != history + 1) throw new Exception($"Sizing history {history} -> {undo.Count}");
            ApplyHistory(false);
            if (outputBone.Track.KeyAt(12) != null) throw new Exception("Sizing undo");
            ApplyHistory(true);
            if (!Sized()) throw new Exception("Sizing redo");
            string output = Path.Combine(directory, "sized.avamotion");
            SaveMotion(output); outputBone.Track.Write(12, BonePose.Rest); LoadMotion(output);
            if (!Sized()) throw new Exception("Sized output round trip");
            ChooseMode(source.label);
            if (JsonUtility.ToJson(CaptureMotion(true)) != before || !original.SequenceEqual(File.ReadAllBytes(input)))
                throw new Exception("Sizing changed its source");
            try { ReadSizingMotion(Path.ChangeExtension(input, ".bin"), source); throw new Exception("Accepted unsupported file"); }
            catch (InvalidDataException) { }
        }

        void CheckImportedMotionSizing(int first,int last,string output)
        {
            var snapshot=SnapshotMotion();
            foreach(var binding in actor.bones.Where(b=>b.RigJointIndex>=0)){
                var track=snapshot.Tracks.Single(t=>t.Owner==actor.id && t.Id==binding.Track.Id);
                if(track.RigJointIndex!=binding.RigJointIndex ||
                    actor.rigProfile.joints[track.RigJointIndex].name!=binding.Joint.name)
                    throw new Exception("Native sizing joint identity");
            }
            var request=new Ava.MotionSizing.NativeSizingRequest{
                SourceOwner=actor.id,TargetOwner=actor.id,FirstFrame=first,LastFrame=last,
                Options=new Ava.MotionSizing.NativeSizingOptions{
                    MovementScale=.8f,MatchFeet=false,AlignHands=false,Twist=false
                }
            };
            string Content()=>JsonUtility.ToJson(CaptureMotion(true));
            var original=CaptureMotion(true);string before=Content();int history=undo.Count,playhead=frame;
            var undoBefore=undo.Reverse().ToArray();var redoBefore=redo.Reverse().ToArray();
            try{
                var result=Ava.MotionSizing.NativeMotionSizing.Calculate(snapshot,actor.rigProfile,actor.rigProfile,request,System.Threading.CancellationToken.None);
                if(result.Edits.Length==0)throw new Exception("Native sizing did not change imported movement");
                ApplyMotionChanges(result.Document,result.Edits);
                string after=Content();
                if(undo.Count!=history+1 || before==after)throw new Exception("Native sizing transaction");
                ApplyHistory(false);
                if(Content()!=before)throw new Exception("Native sizing undo");
                ApplyHistory(true);
                if(Content()!=after)throw new Exception("Native sizing redo");
                SaveMotion(output);
                ApplyHistory(false);
                LoadMotion(output);
                if(Content()!=after)throw new Exception("Sized native motion round trip");
            }finally{
                // Loading a motion intentionally discards history for its tracks. Keep
                // this nested smoke independent of the caller's import/undo checks.
                RestoreMotion(original,true);
                undo.Clear();foreach(var item in undoBefore)undo.Push(item);
                redo.Clear();foreach(var item in redoBefore)redo.Push(item);
                SetFrame(playhead);
            }
            if(Content()!=before)throw new Exception("Native sizing smoke restoration");
        }

        // Quaternion.Angle rounds small discrepancies down to zero.
        static double RotationError(Quaternion a, Quaternion b)
        {
            double Dot(Quaternion x, Quaternion y) => (double)x.x*y.x+(double)x.y*y.y+(double)x.z*y.z+(double)x.w*y.w;
            double cosine=Math.Abs(Dot(a,b))/Math.Sqrt(Dot(a,a)*Dot(b,b));
            return Math.Acos(Math.Min(1,cosine))*360/Math.PI;
        }
    }
}
