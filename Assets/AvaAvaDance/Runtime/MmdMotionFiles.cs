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
    // Windows-31J is used by Japanese MMD even on installations with a different system locale.
    internal static class MmdText
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int MultiByteToWideChar(uint cp, uint flags, byte[] src, int size, [Out] StringBuilder dst, int chars);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern int WideCharToMultiByte(uint cp, uint flags, string src, int chars,
            byte[] dst, int size, IntPtr replacement, out int replaced);
        internal static string Decode(byte[] b)
        {
            int n = Array.IndexOf(b,(byte)0); if (n < 0) n = b.Length; if (n == 0) return "";
            var chars = new StringBuilder(n+1); int size = MultiByteToWideChar(932, 0, b, n, chars, chars.Capacity);
            if (size == 0) throw new InvalidDataException("Shift-JIS文字列が不正です。");
            return chars.ToString(0,size);
        }
        internal static byte[] Encode(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
            var bytes = new byte[checked(s.Length * 2)];
            int n = WideCharToMultiByte(932,0x400,s,s.Length,bytes,bytes.Length,IntPtr.Zero,out int replaced);
            if (n == 0 || replaced != 0) throw new InvalidDataException("Shift-JISで表現できない文字があります。");
            Array.Resize(ref bytes,n); return bytes;
        }
        internal static byte[] Fixed(string s, int size)
        {
            var b = Encode(s); var result = new byte[size];
            int n = Math.Min(size,b.Length);
            // Do not split a CP932 lead/trail byte pair at the fixed field boundary.
            for (int i=0;i<n;i++) if (b[i] >= 0x81 && b[i] <= 0x9f || b[i] >= 0xe0 && b[i] <= 0xfc)
                if (++i == n) n--;
            Array.Copy(b,result,n); return result;
        }
    }

    public sealed partial class DanceApp
    {
        static byte[] ReadMmdFile(string path)
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
            WriteFile(path,MmdText.Encode(text.ToString()));
        }
        void LoadVpd(string path)
        {
            string source = MmdText.Decode(ReadMmdFile(path)).TrimStart('\uFEFF');
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

        static byte[] CurveBytes(CurveSet set, bool camera)
        {
            var result = new byte[camera ? 24 : 64];
            byte Byte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(127*v),0,127);
            for (int i=0;i<(camera?6:4);i++) {
                var c=CurveSet.Get(set,i); var a=c.First; var b=c.Second;
                if(camera) { result[4*i]=Byte(a.x); result[4*i+1]=Byte(b.x); result[4*i+2]=Byte(a.y); result[4*i+3]=Byte(b.y); }
                else { result[i]=Byte(a.x);result[i+4]=Byte(a.y);result[i+8]=Byte(b.x);result[i+12]=Byte(b.y); }
            }
            if(!camera) for(int block=1;block<4;block++)
                Array.Copy(result,block,result,16*block,16-block);
            return result;
        }
        static CurveSet ReadCurves(byte[] bytes, bool camera)
        {
            var c = new CurveSet { channels = new EaseCurve[camera?6:4] };
            for(int i=0;i<c.channels.Length;i++) {
                Vector2 a = camera ? new Vector2(bytes[4*i],bytes[4*i+2]) : new Vector2(bytes[i],bytes[i+4]);
                Vector2 b = camera ? new Vector2(bytes[4*i+1],bytes[4*i+3]) : new Vector2(bytes[i+8],bytes[i+12]);
                c.channels[i] = new EaseCurve { custom=true, a=a/127, b=b/127 };
            }
            if(!CurveSet.Valid(c))throw new InvalidDataException("VMD補間データが不正です。");
            return c;
        }
        static void Put(BinaryWriter w,Vector3 p) { w.Write(p.x);w.Write(p.y);w.Write(p.z); }
        static Vector3 Vector(BinaryReader r) => new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());

        static string VmdName(MotionTrack track) => track is MotionTrack<float> m ? MorphName(m) : track.Name;

        void SaveVmd(string path)
        {
            using var output = new MemoryStream(); using var w = new BinaryWriter(output);
            w.Write(MmdText.Fixed("Vocaloid Motion Data 0002",30));
            w.Write(MmdText.Fixed(modelMode?actor.avatar.name:"カメラ・照明",20));
            void Section<T>(IEnumerable<MotionTrack<T>> tracks,Action<MotionTrack<T>,int,T> write) where T:struct {
                var list=tracks.ToArray();
                if(list.Select(t=>Convert.ToBase64String(MmdText.Fixed(VmdName(t),15))).Distinct().Count()!=list.Length)
                    throw new InvalidDataException("VMDの15バイト名が重複しています。");
                w.Write(list.Sum(t=>t.Frames.Count));
                foreach(var t in list)foreach(int f in t.Frames) {
                    write(t,f,(T)t.KeyAt(f));
                    if(output.Length>MaxMotionBytes)throw new IOException("VMDサイズ上限を超えています。");
                }
            }
            var tracks = VisibleTracks;
            Section(tracks.OfType<MotionTrack<BonePose>>(),(t,f,p)=>{
                w.Write(MmdText.Fixed(VmdName(t),15));w.Write(f);Put(w,p.Position/SceneUnit);
                w.Write(p.Rotation.x);w.Write(p.Rotation.y);w.Write(p.Rotation.z);w.Write(p.Rotation.w);
                var curves=CurveBytes(p.Curves,false);
                // MMD keeps the displaced Z/rotation control points in the shifted rows.
                curves[2] = p.PhysicsOff ? (byte)0x63 : (byte)0;
                curves[3] = p.PhysicsOff ? (byte)0x0f : (byte)0;
                w.Write(curves);
            });
            Section(tracks.OfType<MotionTrack<float>>(),(t,f,p)=>{w.Write(MmdText.Fixed(VmdName(t),15));w.Write(f);w.Write(p);});
            Section(tracks.OfType<MotionTrack<CameraPose>>(),(t,f,p)=>{
                w.Write(f);w.Write(-p.Distance);Put(w,p.Center);Put(w,p.Angles*Mathf.Deg2Rad);
                w.Write(CurveBytes(p.Curves,true));w.Write(Mathf.RoundToInt(p.ViewAngle));w.Write((byte)(p.Perspective?0:1));
            });
            Section(tracks.OfType<MotionTrack<LightPose>>(),(t,f,p)=>{w.Write(f);Put(w,new Vector3(p.Color.r,p.Color.g,p.Color.b));Put(w,p.Direction);});
            Section(tracks.OfType<MotionTrack<ShadowPose>>(),(t,f,p)=>{w.Write(f);w.Write((byte)p.Mode);w.Write((10000-p.Range)/100000f);});
            Section(tracks.OfType<MotionTrack<SwitchPose>>(),(t,f,p)=>{
                w.Write(f);w.Write((byte)(p.Hidden?0:1));w.Write(actor.ikChains.Count);
                foreach(var ik in actor.ikChains){w.Write(MmdText.Fixed(ik.Handle.Track.Name,20));w.Write((byte)(p.Enabled(ik.Handle.Track.Name)?1:0));}
            });
            WriteFile(path,output.ToArray());
        }

        void LoadVmd(string path, int startFrame = 0)
        {
            using var s=new MemoryStream(ReadMmdFile(path)); using var r=new BinaryReader(s);
            byte[] Bytes(int n) { var b=r.ReadBytes(n); if(b.Length!=n)throw new EndOfStreamException(); return b; }
            string Name(int n) => MmdText.Decode(Bytes(n));
            string header=Name(30);
            if(header!="Vocaloid Motion Data 0002" && header!="Vocaloid Motion Data file")throw new InvalidDataException("VMDヘッダーが不正です。");
            Name(header.EndsWith("0002",StringComparison.Ordinal)?20:10);
            var edits=new Dictionary<(MotionTrack,int),object>();
            int Count(int size) { uint n=r.ReadUInt32();if(n>(s.Length-s.Position)/size)throw new InvalidDataException("VMD件数が不正です。");return (int)n; }
            void Add(MotionTrack t,int f,object value) {
                if(t==null || !VisibleTracks.Contains(t))return;
                if(f<0 || f>MaxFrame-startFrame || !ValidPose(value))throw new InvalidDataException("VMDキーが編集可能範囲外です。");
                edits[(t,startFrame+f)]=value;
            }
            var names=new Dictionary<(Type,string),MotionTrack>();
            foreach(var t in actor.modelTracks) {
                string shortName;
                try { shortName=MmdText.Decode(MmdText.Fixed(VmdName(t),15)); } catch(InvalidDataException) {continue;}
                var key=(t.GetType(),shortName);
                names[key]=names.ContainsKey(key)?null:t;
            }
            MotionTrack Find<T>(string name) where T:struct => names.TryGetValue((typeof(MotionTrack<T>),name),out var t)?t:null;
            for(int i=Count(111);i>0;i--) {
                var t=Find<BonePose>(Name(15));int f=r.ReadInt32();var p=new BonePose {Position=Vector(r)*SceneUnit,
                    Rotation=new Quaternion(r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle())};
                var curves=Bytes(64);p.PhysicsOff=curves[2]==0x63 && curves[3]==0x0f;
                if(p.PhysicsOff || curves[2]==0 && curves[3]==0) { curves[2]=curves[17];curves[3]=curves[18]; }
                p.Curves=ReadCurves(curves,false);Add(t,f,p);
            }
            for(int i=Count(23);i>0;i--) {var t=Find<float>(Name(15));int f=r.ReadInt32();Add(t,f,r.ReadSingle());}
            for(int i=Count(61);i>0;i--) {
                int f=r.ReadInt32();var p=new CameraPose {Distance=-r.ReadSingle(),Center=Vector(r),Angles=Vector(r)*Mathf.Rad2Deg,
                    Curves=ReadCurves(Bytes(24),true),ViewAngle=r.ReadUInt32(),Perspective=r.ReadByte()==0};Add(cameraTrack,f,p);
            }
            for(int i=Count(28);i>0;i--) {
                int f=r.ReadInt32();var rgb=Vector(r);Add(lightTrack,f,new LightPose {Color=new Color(rgb.x,rgb.y,rgb.z),Direction=Vector(r)});
            }
            if(s.Position<s.Length)for(int i=Count(9);i>0;i--) {
                int f=r.ReadInt32();Add(shadowTrack,f,new ShadowPose {Mode=r.ReadByte(),Range=Mathf.Clamp(10000-r.ReadSingle()*100000,0,9999)});
            }
            if(s.Position<s.Length)for(int i=Count(9);i>0;i--) {
                int f=r.ReadInt32();bool hidden=r.ReadByte()==0;var disabled=new List<string>();
                for(int j=Count(21);j>0;j--){string name=Name(20);if(r.ReadByte()==0)disabled.Add(name);}
                Add(actor.modelSwitch,f,new SwitchPose {Hidden=hidden,DisabledIK=disabled.ToArray()});
            }
            if(s.Position!=s.Length || edits.Count==0)throw new InvalidDataException("現在の編集対象と一致するVMDキーがありません。");
            CommitKeys(edits.Select(e=>new KeyChange {Track=e.Key.Item1,Frame=e.Key.Item2,After=e.Value}));
            SetFrame(frame); status.text=edits.Count+" 個のVMDキーを読み込みました。";
        }
    }
}
