using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ava.Extensions
{
    public enum MotionChannel { Bone, Morph, Camera, Light, Shadow, Visibility }

    // Records retain the original name bytes, quaternion and interpolation payload.
    public sealed class VmdDocument
    {
        const int MaxBytes = 256 * 1024 * 1024;
        static readonly int[] Sizes = {111,23,61,28,9,9};
        byte[] header, model;
        readonly List<byte[]>[] channels = new List<byte[]>[6];
        int sectionCount;
        public byte[] TrailingData {get;set;} = Array.Empty<byte>();

        public VmdDocument(string modelName)
        {
            header = MmdText.Fixed("Vocaloid Motion Data 0002",30);
            model = MmdText.Fixed(modelName,20);
            sectionCount = channels.Length;
            for (int i=0;i<channels.Length;i++) channels[i]=new List<byte[]>();
        }

        public VmdDocument Copy()
        {
            var result=new VmdDocument(""){header=(byte[])header.Clone(),model=(byte[])model.Clone(),sectionCount=sectionCount,TrailingData=(byte[])TrailingData.Clone()};
            for(int i=0;i<channels.Length;i++)foreach(var row in channels[i])result.channels[i].Add((byte[])row.Clone());
            return result;
        }
        public List<byte[]> Records(MotionChannel channel) => channels[(int)channel];
        public string ModelName => MmdText.Decode(model);
        public static uint Frame(MotionChannel channel,byte[] record) => BitConverter.ToUInt32(record,channel<=MotionChannel.Morph?15:0);
        public static string Name(byte[] record)
        {
            var bytes=new byte[15]; Buffer.BlockCopy(record,0,bytes,0,15); return MmdText.Decode(bytes);
        }
        public static void Number(byte[] record,int offset,float value) => Buffer.BlockCopy(BitConverter.GetBytes(value),0,record,offset,4);

        public static VmdDocument Read(string path)
        {
            if(Path.GetExtension(path).Equals(".vpd",StringComparison.OrdinalIgnoreCase))return ReadPose(path);
            return ReadBinary(path);
        }

        static VmdDocument ReadBinary(string path)
        {
            using var stream=File.OpenRead(path);
            if(stream.Length>MaxBytes) throw new InvalidDataException("VMDのサイズ上限を超えました。");
            using var reader=new BinaryReader(stream);
            byte[] Bytes(int size) {
                if(size>stream.Length-stream.Position) throw new EndOfStreamException("VMDレコードが途中で終了しました。");
                return reader.ReadBytes(size);
            }
            var raw=Bytes(30);
            string signature=MmdText.Decode(raw);
            if(signature!="Vocaloid Motion Data 0002" && signature!="Vocaloid Motion Data file") throw new InvalidDataException("VMDヘッダーが不正です。");
            var document=new VmdDocument("") {header=raw,model=Bytes(signature.EndsWith("0002",StringComparison.Ordinal)?20:10),sectionCount=0};
            for(int section=0;section<Sizes.Length;section++)
            {
                if(stream.Position==stream.Length && section>=2) break;
                uint count=reader.ReadUInt32();
                if(count>(stream.Length-stream.Position)/Sizes[section]) throw new InvalidDataException("VMDレコード数が不正です。");
                var rows=document.channels[section];
                for(uint i=0;i<count;i++)
                {
                    byte[] record=Bytes(Sizes[section]);
                    if(section==(int)MotionChannel.Visibility)
                    {
                        uint ik=BitConverter.ToUInt32(record,5);
                        if(ik>(stream.Length-stream.Position)/21) throw new InvalidDataException("IKレコード数が不正です。");
                        var tail=Bytes(checked((int)ik*21)); Array.Resize(ref record,9+tail.Length);
                        Buffer.BlockCopy(tail,0,record,9,tail.Length);
                    }
                    rows.Add(record);
                }
                document.sectionCount++;
            }
            if(stream.Position<stream.Length)document.TrailingData=Bytes(checked((int)(stream.Length-stream.Position)));
            return document;
        }

        static VmdDocument ReadPose(string path)
        {
            if(new FileInfo(path).Length>16*1024*1024)throw new IOException("ポーズファイルが大きすぎます。");
            var source=System.Text.RegularExpressions.Regex.Replace(MmdText.Decode(File.ReadAllBytes(path)),@"//[^\r\n]*","");
            var header=System.Text.RegularExpressions.Regex.Match(source,@"^Vocaloid Pose Data file\s+([^;]+);\s*(\d+)\s*;");
            if(!header.Success)throw new InvalidDataException("VPDヘッダーが不正です。");
            var result=new VmdDocument(Path.GetFileNameWithoutExtension(header.Groups[1].Value.Trim()));
            var matches=System.Text.RegularExpressions.Regex.Matches(source,@"Bone\d+\s*\{([^\r\n}]+)\s+([^;{}]+);\s*([^;{}]+);\s*\}");
            if(matches.Count!=int.Parse(header.Groups[2].Value))throw new InvalidDataException("ボーン数が一致しません。");
            foreach(System.Text.RegularExpressions.Match match in matches)
            {
                var record=new byte[111];Array.Copy(MmdText.Fixed(match.Groups[1].Value.Trim(),15),record,15);
                var pos=match.Groups[2].Value.Split(',');var rot=match.Groups[3].Value.Split(',');
                if(pos.Length!=3 || rot.Length!=4)throw new InvalidDataException("VPD姿勢が不正です。");
                for(int i=0;i<7;i++){
                    float value=float.Parse(i<3?pos[i]:rot[i-3],System.Globalization.CultureInfo.InvariantCulture);
                    if(float.IsNaN(value) || float.IsInfinity(value))throw new InvalidDataException("VPD姿勢が不正です。");
                    Number(record,19+i*4,value);
                }
                for(int i=0;i<16;i++)record[47+i]=(byte)(i<8?20:107);
                for(int n=1;n<4;n++)Array.Copy(record,47+n,record,47+16*n,16-n);
                result.Records(MotionChannel.Bone).Add(record);
            }
            return result;
        }

        public void Save(string path)
        {
            AtomicFile(path,temp=>{
                using var writer=new BinaryWriter(File.Create(temp)); writer.Write(header);writer.Write(model);
                int last=sectionCount;
                for(int i=last;i<channels.Length;i++) if(channels[i].Count>0) last=i+1;
                long bytes=header.Length+model.Length+4L*last+TrailingData.LongLength;
                for(int i=0;i<last;i++)
                    foreach(var record in channels[i]) bytes+=record.LongLength;
                if(bytes>MaxBytes) throw new InvalidDataException("VMDのサイズ上限を超えました。");
                for(int i=0;i<last;i++)
                {
                    writer.Write(channels[i].Count);
                    foreach(var record in channels[i]) writer.Write(record);
                }
                writer.Write(TrailingData);
            });
        }

        public static void AtomicFile(string path,Action<string> write)
        {
            path=Path.GetFullPath(path);
            string temp=Path.Combine(Path.GetDirectoryName(path),Guid.NewGuid()+".tmp");
            try {write(temp); if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);}
            finally {if(File.Exists(temp)) File.Delete(temp);}
        }
    }
}
