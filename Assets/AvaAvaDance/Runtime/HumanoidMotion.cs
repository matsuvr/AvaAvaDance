using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvaAvaDance.Import;
using UniHumanoid;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const int MaxHumanoidKeys = 250000;

        void LoadHumanoidMotion(string path)
        {
            if (!modelMode || actor.avatar==null || actor.humanBones.Count==0)
                throw new InvalidDataException("モデル操作で Humanoid モデルを選択してください。");
            using var input=File.OpenRead(path);
            if (input.Length==0 || input.Length>UnityPackageReader.MaxFileBytes)
                throw new InvalidDataException("モーションファイルのサイズが範囲外です。");
            string ext=Path.GetExtension(path).ToLowerInvariant();
            if (ext==".fbx")
            {
                var bytes=new byte[(int)input.Length]; ReadFully(input,bytes,bytes.Length);
                string metaPath=path+".meta";
                string meta=File.Exists(metaPath) && new FileInfo(metaPath).Length<=4*1024*1024 ? File.ReadAllText(metaPath) : null;
                FbxNative.ReadMotion(bytes,meta,clip=>BakeSkeleton(clip,"FBX"));
            }
            else if (ext==".bvh")
            {
                using var reader=new StreamReader(input);
                BakeSkeleton(BvhMotionReader.Read(reader),"BVH");
            }
            else BakeMuscles(HumanoidAnimCurves.Read(input));
        }

        int ImportedLastFrame(double duration, int tracks)
        {
            double frames=duration*30;
            if (Math.Abs(frames-Math.Round(frames))<0.0001) frames=Math.Round(frames);
            if (!double.IsFinite(frames) || frames<0 || frames>MaxFrame-frame || (Math.Ceiling(frames)+1)*tracks>MaxHumanoidKeys)
                throw new InvalidDataException("モーションが編集可能なフレーム数またはキー数を超えています。");
            return (int)Math.Ceiling(frames);
        }

        void BakeSkeleton(HumanoidSkeletonMotion clip,string format)
        {
            var bindings=actor.bones.ToDictionary(b=>b.Joint);
            var mapped=clip.Bones.Where(k=>actor.humanBones.TryGetValue(k.Key,out var t) && bindings.ContainsKey(t)).ToArray();
            if (mapped.Length==0) throw new InvalidDataException("モデルと対応する Humanoid ボーンがありません。");
            int last=ImportedLastFrame(clip.Duration,mapped.Length);
            if ((long)(last+1)*clip.Parents.Length>16000000) throw new InvalidDataException("モーションの関節数が大きすぎます。");
            var driven=mapped.Select(k=>actor.humanBones[k.Key]).ToHashSet();
            var indices=mapped.Select((k,i)=>(k.Value,i)).ToDictionary(x=>x.Value,x=>x.i);
            var parents=new int[mapped.Length];
            var inverseRest=new Quaternion[mapped.Length];
            var basis=new Quaternion[mapped.Length];
            var offset=new Quaternion[mapped.Length];
            var bones=new BoneBinding[mapped.Length];
            var delta=new Quaternion[mapped.Length];
            var fromSource=Quaternion.Inverse(clip.Facing);
            for (int i=0; i<mapped.Length; i++)
            {
                int n=mapped[i].Value, p=clip.Parents[n];
                while (p>=0 && !indices.ContainsKey(p)) p=clip.Parents[p];
                parents[i]=p<0 ? -1 : indices[p];
                inverseRest[i]=Quaternion.Inverse(clip.Rest[n].rotation)*clip.Facing;
                var joint=actor.humanBones[mapped[i].Key];
                bones[i]=bindings[joint];
                var bind=actor.bindPose[joint];
                basis[i]=Quaternion.Inverse(actor.humanForward)*bind.WorldBasis;
                offset[i]=Quaternion.Inverse(bones[i].RestRotation)*bind.Local;
            }
            var changes=new List<KeyChange>((last+1)*mapped.Length);
            for (int f=0; f<=last; f++)
            {
                clip.Sample(f/30.0);
                for (int i=0; i<mapped.Length; i++) delta[i]=fromSource*clip.World[mapped[i].Value].rotation*inverseRest[i];
                for (int i=0; i<mapped.Length; i++)
                {
                    int p=parents[i], n=mapped[i].Value;
                    var rotation=p<0 ? delta[i] : Quaternion.Inverse(delta[p])*delta[i];
                    var track=bones[i].Track;
                    var pose=track.Sample(frame+f);
                    pose.Rotation=(offset[i]*Quaternion.Inverse(basis[i])*rotation*basis[i]).normalized;
                    if (mapped[i].Key==HumanBodyBones.Hips)
                    {
                        Vector3 move=clip.World[n].GetColumn(3)-clip.Rest[n].GetColumn(3);
                        pose.Position=actor.hipsParentInverse.MultiplyVector(actor.humanForward*(fromSource*move)*(actor.hipsHeight/clip.Height));
                    }
                    pose.Curves=null;
                    if (!ValidPose(pose)) throw new InvalidDataException("Humanoid の姿勢が編集可能範囲外です。");
                    changes.Add(new KeyChange {Track=track,Frame=frame+f,After=pose,ReplaceSource=true});
                }
            }
            CommitHumanoidMotion(changes,driven,frame,frame+last);
            status.text=$"{format} 読込　{driven.Count} ボーン　{frame}～{frame+last} フレーム";
        }

        void BakeMuscles(HumanoidAnimCurves curves)
        {
            // Evaluate Mecanim on a temporary T-pose skeleton, independent of the live playhead,
            // preview scale, IK, physics, and any edits to the avatar.
            var roles=actor.humanBones.OrderBy(k=>Depth(k.Value)).ToArray();
            if (Enumerable.Range(0,(int)HumanBodyBones.LastBone).Any(i=>HumanTrait.RequiredBone(i) && !actor.humanBones.ContainsKey((HumanBodyBones)i)))
                throw new InvalidDataException("anim には Unity Humanoid の必須ボーンが揃ったモデルが必要です。");
            ImportedLastFrame(curves.Duration,roles.Length);
            var root=new GameObject("Humanoid motion");
            Avatar avatar=null;
            try
            {
                var transforms=new Transform[roles.Length];
                var parents=new int[roles.Length];
                var rest=new Matrix4x4[roles.Length];
                var indices=roles.Select((k,i)=>(k.Value,i)).ToDictionary(x=>x.Value,x=>x.i);
                var facing=Quaternion.Inverse(actor.humanForward);
                for (int i=0; i<roles.Length; i++)
                {
                    var joint=roles[i].Value;
                    var parent=joint.parent;
                    while (parent!=null && !indices.ContainsKey(parent)) parent=parent.parent;
                    int p=parents[i]=parent==null ? -1 : indices[parent];
                    var t=transforms[i]=new GameObject(roles[i].Key.ToString()).transform;
                    t.SetParent(p<0 ? root.transform : transforms[p],false);
                    t.SetPositionAndRotation(facing*actor.bindPositions[joint],facing*actor.bindPose[joint].WorldBasis);
                    rest[i]=Matrix4x4.TRS(t.localPosition,t.localRotation,Vector3.one);
                }
                avatar=HumanoidLoader.BuildHumanAvatarFromMap(root.transform,roles.Select((k,i)=>(transforms[i],k.Key)));
                if (!avatar || !avatar.isValid || !avatar.isHuman) throw new InvalidDataException("Unity Humanoid Avatar を構築できません。");
                using var handler=new HumanPoseHandler(avatar,root.transform);
                var pose=new HumanPose {muscles=new float[HumanTrait.MuscleCount]};
                handler.GetHumanPose(ref pose);
                var body=pose.bodyPosition;
                var rotation=pose.bodyRotation;
                void Sample(double seconds,Matrix4x4[] local)
                {
                    curves.Sample((float)seconds,ref pose,body,rotation);
                    handler.SetHumanPose(ref pose);
                    for (int i=0; i<local.Length; i++)
                        local[i]=Matrix4x4.TRS(transforms[i].localPosition,transforms[i].localRotation,Vector3.one);
                }
                BakeSkeleton(new HumanoidSkeletonMotion(roles.Select(k=>k.Key.ToString()).ToArray(),parents,rest,curves.Duration,Sample),"anim");
            }
            finally { if (avatar) Destroy(avatar); Destroy(root); }

            static int Depth(Transform t) { int count=0; for (;t!=null;t=t.parent) count++; return count; }
        }
    }
}
