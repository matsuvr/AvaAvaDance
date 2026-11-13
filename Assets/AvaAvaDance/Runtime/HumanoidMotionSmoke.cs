using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;
using UnityEngine;

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
            if (undo.Count!=history+1) throw new InvalidDataException(status.text);
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
                string path=Path.Combine(output,ext+".avamotion");
                SaveMotion(path);
                ApplyHistory(false);
                if(tracks.Values.Any(t=>t.LastFrame!=0)) throw new Exception("Undo "+ext);
                ImportMotionFile(path);
                foreach(var (id,samples) in baked)
                    for(int i=0;i<samples.Length;i++)
                    {
                        var restored=tracks[id].Sample(startFrame+i);
                        if(Quaternion.Angle(restored.Rotation,samples[i].Rotation)>.08f || (restored.Position-samples[i].Position).sqrMagnitude>1e-10f)
                            throw new Exception("Native round trip "+ext+" "+id);
                    }
                SetFrame(startFrame+135);
                WritePreview(Path.Combine(output,ext+".png"));
                if(ext=="vrma") { SetFrame(0); SaveProject(Path.Combine(output,"Go Wild.avadance")); }
                RestoreMotion(baseline,true);
            }
            OpenProject(Path.Combine(output,"Go Wild.avadance"));
            SetFrame(135);
            foreach (var (id,samples) in reference)
            {
                var track=actor.bones.Single(b=>b.Joint==actor.humanBones[id]).Track;
                if (track.LastFrame!=521 || Quaternion.Angle(track.Current.Rotation,samples[135].Rotation)>.08f)
                    throw new Exception("Project round trip "+id);
            }
            return "PASS\n"+log;
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
