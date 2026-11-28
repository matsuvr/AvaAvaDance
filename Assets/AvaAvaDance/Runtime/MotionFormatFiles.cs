using Ava.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static byte[] ReadFormatFile(string path)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxMotionBytes) throw new InvalidDataException("ファイルが大きすぎます。");
            var result = new byte[(int)stream.Length]; ReadFully(stream,result,result.Length); return result;
        }
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        void SaveVpd(string path)
        {
            var text = new StringBuilder("Vocaloid Pose Data file\r\n\r\n");
            text.Append(actor.avatar.name.Replace(";", "_")).Append(".osm;\r\n").Append(actor.bones.Count).Append(";\r\n");
            for (int i=0;i<actor.bones.Count;i++) {
                var bone = actor.bones[i]; var pose = bone.Track.Current; var p = pose.Position / SceneUnit; var q = pose.Rotation;
                text.Append("Bone").Append(i).Append('{').Append(bone.Track.Name).Append("\r\n");
                text.AppendFormat(Inv, "{0:R},{1:R},{2:R};\r\n{3:R},{4:R},{5:R},{6:R};\r\n}}\r\n",p.x,p.y,p.z,q.x,q.y,q.z,q.w);
            }
            WriteFile(path,Windows31J.Encode(text.ToString()));
        }
        void LoadVpd(string path)
        {
            string source = Windows31J.Decode(ReadFormatFile(path)).TrimStart('\uFEFF');
            if (!source.StartsWith("Vocaloid Pose Data file",StringComparison.Ordinal)) throw new InvalidDataException("VPDヘッダーが不正です。");
            string clean = Regex.Replace(source, @"//[^\r\n]*", "");
            var header = Regex.Match(clean, @"^Vocaloid Pose Data file\s+[^;]*;\s*(\d+)\s*;",RegexOptions.CultureInvariant);
            if (!header.Success || !int.TryParse(header.Groups[1].Value,out int count)) throw new InvalidDataException("VPDボーン数が不正です。");
            var matches = Regex.Matches(clean, @"Bone\d+\s*\{([^\r\n}]+)\s+([^;{}]+);\s*([^;{}]+);\s*\}");
            if(matches.Count!=count)throw new InvalidDataException("VPDボーン数が一致しません。");
            var poses = new Dictionary<BoneBinding,BonePose>();
            foreach (Match m in matches) {
                float[] Read(int group, int length) {
                    var a = m.Groups[group].Value.Split(',').Select(s => float.Parse(s, NumberStyles.Float, Inv)).ToArray();
                    if(a.Length!=length || a.Any(n=>!float.IsFinite(n)))throw new InvalidDataException("VPD数値が不正です。");
                    return a;
                }
                var p = Read(2,3); var q = Read(3,4); var bone = actor.bones.Find(b=>b.Track.Name==m.Groups[1].Value.Trim());
                if(bone==null)continue;
                var pose = bone.Track.Current; pose.Position = new Vector3(p[0],p[1],p[2])*SceneUnit;
                pose.Rotation = new Quaternion(q[0],q[1],q[2],q[3]);
                if(!ValidPose(pose))throw new InvalidDataException("VPD姿勢が不正です。");
                poses[bone]=pose;
            }
            if(poses.Count==0)throw new InvalidDataException("一致するボーンがありません。");
            StopPlayback(); foreach(var item in poses)item.Key.Track.Preview(item.Value);
            InvalidateDynamics(); RefreshRigPreview(); SyncControls(); RedrawOverlay();
            status.text = poses.Count + " ボーンのポーズを適用";
        }
    }
}
