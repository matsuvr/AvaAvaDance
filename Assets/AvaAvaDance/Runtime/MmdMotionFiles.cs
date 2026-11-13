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
            int length=camera?24:64;
            bool retained=set?.raw?.Length==length;
            var result = retained ? (byte[])set.raw.Clone() : new byte[length];
            byte Byte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(127*v),0,127);
            for (int i=0;i<(camera?6:4);i++) {
                if(retained && (set.modified & 1<<i)==0)continue;
                var c=CurveSet.Get(set,i); var a=c.First; var b=c.Second;
                if(camera) { result[4*i]=Byte(a.x); result[4*i+1]=Byte(b.x); result[4*i+2]=Byte(a.y); result[4*i+3]=Byte(b.y); }
                else { result[i]=Byte(a.x);result[i+4]=Byte(a.y);result[i+8]=Byte(b.x);result[i+12]=Byte(b.y); }
            }
            if(!camera) for(int block=1;block<4;block++)
                for(int j=block;j<16;j++)
                    if(!retained || (set.modified & 1<<(j%4))!=0)result[16*block+j-block]=result[j];
            return result;
        }
        static CurveSet ReadCurves(byte[] bytes, bool camera)
        {
            var c = new CurveSet { channels = new EaseCurve[camera?6:4], raw=bytes };
            for(int i=0;i<c.channels.Length;i++) {
                Vector2 a = camera ? new Vector2(bytes[4*i],bytes[4*i+2]) : new Vector2(bytes[i],bytes[i+4]);
                // MMD stores the displaced Z and rotation controls in the shifted row.
                if(!camera && i>=2 && ((bytes[2]==0x63 && bytes[3]==0x0f) || (bytes[2]==0 && bytes[3]==0)))
                    a.x=bytes[15+i];
                Vector2 b = camera ? new Vector2(bytes[4*i+1],bytes[4*i+3]) : new Vector2(bytes[i+8],bytes[i+12]);
                c.channels[i] = new EaseCurve { custom=true, a=a/127, b=b/127 };
            }
            if(!CurveSet.Valid(c))throw new InvalidDataException("VMD補間データが不正です。");
            return c;
        }
        static void Put(BinaryWriter w,Vector3 p) { w.Write(p.x);w.Write(p.y);w.Write(p.z); }
        static Vector3 Vector(BinaryReader r) => new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());

        static string VmdName(MotionTrack track) => track is MotionTrack<float> m ? MorphName(m) : track.Name;

        // Keep the same fixed-width names when writing and reading this rig. Imported
        // records can still override the default with their original 15 bytes.
        Dictionary<(Type,string),MotionTrack> VmdTrackNames(out Dictionary<MotionTrack,byte[]> outputNames)
        {
            var names=new Dictionary<(Type,string),MotionTrack>();
            outputNames=new Dictionary<MotionTrack,byte[]>();
            foreach(var track in actor.modelTracks)
            {
                if(!(track is MotionTrack<BonePose> || track is MotionTrack<float>))continue;
                byte[] bytes;
                try { bytes=MmdText.Fixed(VmdName(track),15); } catch(InvalidDataException) { continue; }
                outputNames.Add(track,bytes);
                var key=(track.GetType(),MmdText.Decode(bytes));
                if(!names.TryGetValue(key,out var previous) || VmdName(track)==key.Item2) names[key]=track;
                else if(previous==null || VmdName(previous)!=key.Item2) names[key]=null;
            }
            foreach(var role in actor.humanBones??new Dictionary<HumanBodyBones,Transform>())
            {
                var alias=RigProfile.MotionAlias(role.Key.ToString());
                var bone=actor.bones.Find(b=>b.Joint==role.Value);
                var key=(typeof(MotionTrack<BonePose>),alias);
                if(alias.Length>0 && bone!=null && !names.ContainsKey(key))
                {
                    names.Add(key,bone.Track);
                    outputNames[bone.Track]=MmdText.Fixed(alias,15);
                }
            }
            foreach(var track in actor.modelTracks)
            {
                if(!outputNames.TryGetValue(track,out var bytes))continue;
                var key=(track.GetType(),MmdText.Decode(bytes));
                if(names[key]==track)continue;
                // Long Unity names can share a prefix (e.g. left and right hair).
                // Reserve all original names before assigning reversible short names.
                for(int n=1;;n++)
                {
                    string suffix="~"+n.ToString(Inv);
                    string prefix=MmdText.Decode(MmdText.Fixed(VmdName(track),15-suffix.Length));
                    var unique=(track.GetType(),prefix+suffix);
                    if(names.ContainsKey(unique))continue;
                    names.Add(unique,track);outputNames[track]=MmdText.Fixed(unique.Item2,15);break;
                }
            }
            return names;
        }

        void SaveVmd(string path)
        {
            var document=new VmdDocument(modelMode?actor.avatar.name:"カメラ・照明");
            VmdTrackNames(out var outputNames);
            byte[] NameAt(MotionTrack track,int at) => track.SourceAt(at)?.name is byte[] raw && raw.Length==15 ? raw :
                outputNames.TryGetValue(track,out var name)?name:MmdText.Fixed(VmdName(track),15);
            var sidecar=modelMode?actor.vmdSidecar:sceneSidecar;
            int section=0;
            using var output = new MemoryStream(); using var w = new BinaryWriter(output);
            void Section<T>(IEnumerable<MotionTrack<T>> tracks,Action<MotionTrack<T>,int,T> write) where T:struct {
                var list=tracks.ToArray();
                var owners=new Dictionary<string,MotionTrack>();
                var rows=document.Records((MotionChannel)section++);
                foreach(var t in list)foreach(int f in t.OutputFrames()) {
                    output.SetLength(0);write(t,f,t.Sample(f));var data=output.ToArray();
                    if(section<=2){
                        var name=VmdDocument.Name(data);
                        if(owners.TryGetValue(name,out var other) && other!=t)throw new InvalidDataException("VMDの15バイト名が重複しています: "+name);
                        owners[name]=t;
                    }
                    rows.Add(data);
                }
            }
            var tracks = VisibleTracks;
            Section(tracks.OfType<MotionTrack<BonePose>>(),(t,f,p)=>{
                w.Write(NameAt(t,f));w.Write(f);Put(w,p.Position/SceneUnit);
                w.Write(p.Rotation.x);w.Write(p.Rotation.y);w.Write(p.Rotation.z);w.Write(p.Rotation.w);
                var curves=CurveBytes(p.Curves,false);
                // MMD keeps the displaced Z/rotation control points in the shifted rows.
                if (p.PhysicsOff || p.Curves?.raw?.Length!=64 || (curves[2]==0x63 && curves[3]==0x0f))
                {
                    curves[2]=p.PhysicsOff?(byte)0x63:(byte)0; curves[3]=p.PhysicsOff?(byte)0x0f:(byte)0;
                }
                w.Write(curves);
            });
            Section(tracks.OfType<MotionTrack<float>>(),(t,f,p)=>{w.Write(NameAt(t,f));w.Write(f);w.Write(p);});
            Section(tracks.OfType<MotionTrack<CameraPose>>(),(t,f,p)=>{
                w.Write(f);w.Write(-p.Distance);Put(w,p.Center);Put(w,p.Angles*Mathf.Deg2Rad);
                w.Write(CurveBytes(p.Curves,true));w.Write(Mathf.RoundToInt(p.ViewAngle));w.Write((byte)(p.Perspective?0:1));
            });
            Section(tracks.OfType<MotionTrack<LightPose>>(),(t,f,p)=>{w.Write(f);Put(w,new Vector3(p.Color.r,p.Color.g,p.Color.b));Put(w,p.Direction);});
            Section(tracks.OfType<MotionTrack<ShadowPose>>(),(t,f,p)=>{w.Write(f);w.Write((byte)p.Mode);w.Write((10000-p.Range)/100000f);});
            Section(tracks.OfType<MotionTrack<SwitchPose>>(),(t,f,p)=>{
                w.Write(f);w.Write((byte)(p.Hidden?0:1));var source=t.SourceAt(f)?.ik;
                var names=new List<byte[]>();var present=new HashSet<string>();
                if(source!=null)for(int i=0;i+21<=source.Length;i+=21){
                    var name=new byte[20];Array.Copy(source,i,name,0,20);names.Add(name);present.Add(MmdText.Decode(name));
                }
                foreach(var ik in actor.ikChains)if(present.Add(ik.Handle.Track.Name))names.Add(MmdText.Fixed(ik.Handle.Track.Name,20));
                w.Write(names.Count);foreach(var name in names){w.Write(name);w.Write((byte)(p.Enabled(MmdText.Decode(name))?1:0));}
            });
            foreach(var record in sidecar.records)document.Records(record.channel).Add(record.data);
            document.TrailingData=sidecar.tail??Array.Empty<byte>();
            document.Save(path);
        }

        [Serializable] sealed class DetachedKey { public MotionChannel channel;public byte[] data; }
        [Serializable] sealed class VmdSidecar
        {
            public List<DetachedKey> records=new List<DetachedKey>();
            public byte[] tail;
        }
        VmdSidecar sceneSidecar=new VmdSidecar();

        void LoadVmd(string path,int startFrame=0)
        {
            var document=VmdDocument.Read(path);
            var edits=new Dictionary<(MotionTrack track,int frame),(object pose,KeySource source)>();
            var visible=new HashSet<MotionTrack>(VisibleTracks);
            var prior=modelMode?actor.vmdSidecar:sceneSidecar;
            var next=new VmdSidecar {tail=document.TrailingData.Length>0?document.TrailingData:prior.tail};
            var names=VmdTrackNames(out _);
            MotionTrack Find<T>(string name) where T:struct=>names.TryGetValue((typeof(MotionTrack<T>),name),out var t)?t:null;
            for(int section=0;section<6;section++)
            {
                var channel=(MotionChannel)section;
                foreach(var row in document.Records(channel))
                {
                    uint time=VmdDocument.Frame(channel,row);
                    MotionTrack track=channel switch {
                        MotionChannel.Bone=>Find<BonePose>(VmdDocument.Name(row)),MotionChannel.Morph=>Find<float>(VmdDocument.Name(row)),
                        MotionChannel.Camera=>cameraTrack, MotionChannel.Light=>lightTrack, MotionChannel.Shadow=>shadowTrack,_=>actor.modelSwitch};
                    if(track==null || !visible.Contains(track))
                    {
                        if(startFrame!=0)Buffer.BlockCopy(BitConverter.GetBytes(checked(time+(uint)startFrame)),0,row,channel<=MotionChannel.Morph?15:0,4);
                        next.records.Add(new DetachedKey {channel=channel,data=row});continue;
                    }
                    if(time>MaxFrame-startFrame)throw new InvalidDataException("VMDキーが編集可能範囲外です。");
                    using var stream=new MemoryStream(row);using var reader=new BinaryReader(stream);
                    stream.Position=channel<=MotionChannel.Morph?19:4;
                    object value;
                    switch(channel)
                    {
                        case MotionChannel.Bone:
                            var p=new BonePose {Position=Vector(reader)*SceneUnit,Rotation=new Quaternion(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle())};
                            var curves=reader.ReadBytes(64);
                            p.PhysicsOff=curves[2]==0x63 && curves[3]==0x0f;
                            p.Curves=ReadCurves(curves,false);value=p;break;
                        case MotionChannel.Morph:value=reader.ReadSingle();break;
                        case MotionChannel.Camera:value=new CameraPose{Distance=-reader.ReadSingle(),Center=Vector(reader),Angles=Vector(reader)*Mathf.Rad2Deg,
                            Curves=ReadCurves(reader.ReadBytes(24),true),ViewAngle=reader.ReadUInt32(),Perspective=reader.ReadByte()==0};break;
                        case MotionChannel.Light:var rgb=Vector(reader);value=new LightPose{Color=new Color(rgb.x,rgb.y,rgb.z),Direction=Vector(reader)};break;
                        case MotionChannel.Shadow:value=new ShadowPose{Mode=reader.ReadByte(),Range=Mathf.Clamp(10000-reader.ReadSingle()*100000,0,9999)};break;
                        default:
                            bool hidden=reader.ReadByte()==0;int count=reader.ReadInt32();var disabled=new List<string>();
                            for(int i=0;i<count;i++){string name=MmdText.Decode(reader.ReadBytes(20));if(reader.ReadByte()==0)disabled.Add(name);}
                            value=new SwitchPose{Hidden=hidden,DisabledIK=disabled.ToArray()};break;
                    }
                    if(!ValidPose(value))throw new InvalidDataException("VMDの登録値が不正です。");
                    int at=startFrame+(int)time;
                    KeySource source=null;
                    if(channel<=MotionChannel.Morph)source=new KeySource{name=row[..15]};
                    else if(channel==MotionChannel.Visibility)source=new KeySource{ik=row[9..]};
                    edits[(track,at)]=(value,source);
                }
            }
            var targetActor=modelMode?actor:null;
            int detachedCount=next.records.Count;
            if(detachedCount>0 && prior.records.Count>0)
            {
                (MotionChannel,string,uint) Identity(DetachedKey k) => (k.channel,
                    k.channel<=MotionChannel.Morph?VmdDocument.Name(k.data):null,VmdDocument.Frame(k.channel,k.data));
                var replaced=next.records.Select(Identity).ToHashSet();
                next.records.InsertRange(0,prior.records.Where(k=>!replaced.Contains(Identity(k))));
            }
            else if(detachedCount==0)next.records=prior.records;
            int oldCount=undo.Count;
            CommitKeys(edits.Select(e=>new KeyChange {Track=e.Key.track,Frame=e.Key.frame,After=e.Value.pose,
                ReplaceSource=true,AfterSource=e.Value.source,DocumentFrame=startFrame}));
            if(detachedCount!=0 || next.tail!=prior.tail)
            {
                if(targetActor==null)sceneSidecar=next;else targetActor.vmdSidecar=next;
                var owner=targetActor==null?(MotionTrack)cameraTrack:targetActor.modelSwitch;
                var record=new KeyChange {Track=owner,Frame=frame,SidecarBefore=prior,SidecarAfter=next};
                var changes=undo.Count==oldCount?new[]{record}:undo.Pop().Append(record).ToArray();
                undo.Push(changes);redo.Clear();
            }
            SetFrame(frame);
            if(!ribbonProfile)
            {
                selectedKeys.Clear();selectedKeys.UnionWith(edits.Keys);rangeSelected=true;
                RefreshKeyButtons();timeline.MarkDirtyRepaint();
            }
            status.text=edits.Count+" 個のVMDキーを読込　未割当 "+detachedCount;
        }
    }
}
