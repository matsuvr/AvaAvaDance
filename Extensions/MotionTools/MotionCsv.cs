using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public static class MotionCsv
    {
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        const string BoneHeader = "ボーン名,フレーム,位置X,位置Y,位置Z,回転X,回転Y,回転Z,"+
            "【X_x1】,Y_x1,Z_x1,R_x1,【X_y1】,Y_y1,Z_y1,R_y1,【X_x2】,Y_x2,Z_x2,R_x2,【X_y2】,Y_y2,Z_y2,R_y2,"+
            "【Y_x1】,Z_x1,R_x1,X_y1,【Y_y1】,Z_y1,R_y1,X_x2,【Y_x2】,Z_x2,R_x2,X_y2,【Y_y2】,Z_y2,R_y2,1,"+
            "【Z_x1】,R_x1,X_y1,Y_y1,【Z_y1】,R_y1,X_x2,Y_x2,【Z_x2】,R_x2,X_y2,Y_y2,【Z_y2】,R_y2,1,0,"+
            "【R_x1】,X_y1,Y_y1,Z_y1,【R_y1】,X_x2,Y_x2,Z_x2,【R_x2】,X_y2,Y_y2,Z_y2,【R_y2】,01,00,00";
        const string CameraHeader = "フレーム,位置X,位置Y,位置Z,回転X,回転Y,回転Z,距離,視野角,パース,"+
            "X_x1,Y_x1,Z_x1,R_x1,L_x1,VA_x1,X_y1,Y_y1,Z_y1,R_y1,L_y1,VA_y1,X_x2,Y_x2,Z_x2,R_x2,L_x2,VA_x2, X_y2,Y_y2,Z_y2,R_y2,L_y2,VA_y2";
        static readonly string[] Headers={BoneHeader,"モーフ名,フレーム,大きさ",CameraHeader};
        static readonly string[] Suffixes={"bone","morph","camera"};

        static string Output(string source,int channel,DateTime at,string extension) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source)),Path.GetFileNameWithoutExtension(source)+"_"+Suffixes[channel]+"_"+at.ToString("yyyyMMdd_HHmmss",Invariant)+extension);
        static double Clamp(double value,double min,double max) => Math.Min(max,Math.Max(value,min));
        static double Num(string text)
        {
            double value=double.Parse(text,NumberStyles.Float,Invariant);
            if(double.IsInfinity(value) || double.IsNaN(value)) throw new FormatException("有限の数値を入力してください。");
            return value;
        }
        static float Float(string text)
        {
            float value=(float)Num(text);
            if(float.IsInfinity(value))throw new OverflowException("VMDに記録できる数値の範囲を超えています。");
            return value;
        }
        static uint Index(string value) => checked((uint)Math.Truncate(Num(value)));
        static string F(double value) => value.ToString("R",Invariant);

        // Y-X-Z rotation; export uses degrees for bones and native radians for cameras.
        static double[] Angles(byte[] row)
        {
            double x=BitConverter.ToSingle(row,31), y=BitConverter.ToSingle(row,35), z=BitConverter.ToSingle(row,39), w=BitConverter.ToSingle(row,43);
            double length=x*x+y*y+z*z+w*w;
            if(length<1e-16) return new double[3];
            double pitch=Math.Asin(Clamp(2*(w*x-y*z)/length,-1,1));
            double yaw,roll;
            if(Math.Abs(Math.Cos(pitch))<1e-6) {yaw=Math.Atan2(2*(w*y-x*z),length-2*(y*y+z*z));roll=0;}
            else {yaw=Math.Atan2(2*(w*y+x*z),length-2*(x*x+y*y));roll=Math.Atan2(2*(w*z+x*y),length-2*(x*x+z*z));}
            return new[]{pitch*180/Math.PI,-yaw*180/Math.PI,-roll*180/Math.PI};
        }
        static void WriteRotation(BinaryWriter w,double rx,double ry,double rz)
        {
            double k=Math.PI/360, cx=Math.Cos(rx*k),sx=Math.Sin(rx*k),cy=Math.Cos(-ry*k),sy=Math.Sin(-ry*k),cz=Math.Cos(-rz*k),sz=Math.Sin(-rz*k);
            w.Write((float)(cy*sx*cz+sy*cx*sz)); w.Write((float)(sy*cx*cz-cy*sx*sz));
            w.Write((float)(cy*cx*sz-sy*sx*cz)); w.Write((float)(cy*cx*cz+sy*sx*sz));
        }

        static IEnumerable<string> Fields(MotionChannel channel,byte[] row)
        {
            if(channel!=MotionChannel.Camera) yield return VmdDocument.Name(row);
            yield return VmdDocument.Frame(channel,row).ToString(Invariant);
            if(channel==MotionChannel.Morph) {yield return F(BitConverter.ToSingle(row,19));yield break;}
            if(channel==MotionChannel.Bone)
            {
                for(int i=19;i<31;i+=4) yield return F(BitConverter.ToSingle(row,i));
                foreach(var angle in Angles(row)) yield return F(angle);
                for(int i=47;i<111;i++) yield return row[i].ToString(Invariant);
            }
            else
            {
                for(int i=8;i<32;i+=4) yield return F(BitConverter.ToSingle(row,i));
                float distance=BitConverter.ToSingle(row,4); yield return F(distance==0?.00001:-distance);
                yield return BitConverter.ToUInt32(row,56).ToString(Invariant);yield return row[60].ToString(Invariant);
                for(int i=32;i<56;i++) yield return row[i].ToString(Invariant);
            }
        }

        public static List<string> Export(string input,DateTime at)
        {
            var document=VmdDocument.Read(input);var outputs=new List<string>();
            for(int i=0;i<3;i++)
            {
                var channel=(MotionChannel)i;var rows=document.Records(channel);
                if(rows.Count==0) continue;
                var path=Output(input,i,at,".csv");
                VmdDocument.AtomicFile(path,temp=>{
                    using var file=File.Create(temp);
                    void Line(string s) {var bytes=MmdText.Encode(s+"\r\n");file.Write(bytes,0,bytes.Length);}
                    Line(Headers[(int)channel]);
                    foreach(var names in rows.GroupBy(r=>channel==MotionChannel.Camera?"":VmdDocument.Name(r)))
                        foreach(var frames in names.GroupBy(r=>VmdDocument.Frame(channel,r)).OrderBy(g=>g.Key))
                            Line(string.Join(",",Fields(channel,channel==MotionChannel.Camera?frames.Last():frames.First()).Select(Quote)));
                });
                outputs.Add(path);
            }
            return outputs;
        }

        static string Quote(string value) => value.IndexOfAny(new[]{',','"','\r','\n'}) < 0 ? value : "\""+value.Replace("\"","\"\"")+"\"";

        internal static IEnumerable<string[]> Rows(string path,bool skipHeader=true)
        {
            if(new FileInfo(path).Length>256*1024*1024) throw new IOException("CSVサイズ上限を超えました。");
            string source=MmdText.Decode(File.ReadAllBytes(path));
            var fields=new List<string>();var cell=new StringBuilder();bool quoted=false,header=skipHeader;
            for(int i=0;i<=source.Length;i++)
            {
                char c=i<source.Length?source[i]:'\n';
                if(c=='"' && (quoted || cell.Length==0))
                {
                    if(quoted && i+1<source.Length && source[i+1]=='"') {cell.Append('"');i++;}
                    else quoted=!quoted;
                }
                else if(!quoted && c==',') {fields.Add(cell.ToString());cell.Clear();}
                else if(!quoted && (c=='\r' || c=='\n'))
                {
                    if(c=='\r' && i+1<source.Length && source[i+1]=='\n')i++;
                    if(i==source.Length && fields.Count==0 && cell.Length==0)break;
                    fields.Add(cell.ToString()); cell.Clear();
                    if(!header) yield return fields.ToArray(); header=false; fields.Clear();
                }
                else cell.Append(c);
            }
            if(quoted) throw new InvalidDataException("CSVの引用符が閉じていません。");
        }

        static void ImportChannel(VmdDocument document,MotionChannel channel,string path)
        {
            if(string.IsNullOrWhiteSpace(path)) return;
            var keys=new Dictionary<(string,uint),byte[]>();
            int line=1;
            foreach(var cells in Rows(path))
            {
                line++;
                try {
                    int minimum=channel==MotionChannel.Bone?72:channel==MotionChannel.Morph?3:34;
                    if(cells.Length<minimum) throw new FormatException(minimum+"列必要です。");
                    using var data=new MemoryStream();using var w=new BinaryWriter(data);
                    string name=channel==MotionChannel.Camera?"":cells[0];
                    uint frame=channel==MotionChannel.Camera?uint.Parse(cells[0],NumberStyles.Integer,Invariant):Index(cells[1]);
                    if(channel!=MotionChannel.Camera) w.Write(MmdText.Fixed(name,15));w.Write(frame);
                    void Curves(int first,int count) {
                        for(int i=0;i<count;i++){var n=Math.Truncate(Num(cells[first+i]));if(n<0)throw new FormatException("補間値が負です。"); w.Write((byte)Math.Min(127,n));}
                    }
                    if(channel==MotionChannel.Morph) w.Write(Float(cells[2]));
                    else if(channel==MotionChannel.Bone)
                    {
                        for(int i=2;i<5;i++) w.Write(Float(cells[i]));
                        WriteRotation(w,Num(cells[5]),Num(cells[6]),Num(cells[7])); Curves(8,64);
                    }
                    else
                    {
                        w.Write(-Float(cells[7]));for(int i=1;i<=6;i++)w.Write(Float(cells[i]));
                        Curves(10,24);w.Write(uint.Parse(cells[8],NumberStyles.Integer,Invariant));
                        byte perspective=byte.Parse(cells[9],NumberStyles.Integer,Invariant);
                        if(perspective>1)throw new FormatException("パースは0または1です。");w.Write(perspective);
                    }
                    keys[(name,frame)]=data.ToArray();
                } catch(Exception ex) {throw new InvalidDataException(Path.GetFileName(path)+" "+line+"行: "+ex.Message,ex);}
            }
            var groups=keys.GroupBy(k=>k.Key.Item1).Select(g=>g.OrderBy(k=>k.Key.Item2).ToArray()).ToArray();
            var rows=document.Records(channel);
            if(channel==MotionChannel.Camera) rows.AddRange(keys.OrderByDescending(k=>k.Key.Item2).Select(k=>k.Value));
            else {
                rows.AddRange(groups.Select(g=>g[g.Length-1].Value));
                rows.AddRange(groups.SelectMany(g=>g.Take(g.Length-1)).Select(k=>k.Value));
            }
        }

        public static List<string> Import(string bones,string morphs,string camera,DateTime at)
        {
            var outputs=new List<string>();
            var model=new VmdDocument("CSV Convert Model");
            ImportChannel(model,MotionChannel.Bone,bones); ImportChannel(model,MotionChannel.Morph,morphs);
            if(model.Records(MotionChannel.Bone).Count+model.Records(MotionChannel.Morph).Count>0)
            {
                bool hasBones=!string.IsNullOrWhiteSpace(bones);var path=Output(hasBones?bones:morphs,hasBones?0:1,at,".vmd");
                model.Save(path);outputs.Add(path);
            }
            var scene=new VmdDocument("カメラ・照明"); ImportChannel(scene,MotionChannel.Camera,camera);
            if(scene.Records(MotionChannel.Camera).Count>0){var path=Output(camera,2,at,".vmd");scene.Save(path);outputs.Add(path);}
            return outputs;
        }
    }
}
