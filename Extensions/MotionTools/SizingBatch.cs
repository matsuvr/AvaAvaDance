using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public static class SizingBatch
    {
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        const string Header="グループNo,モーション,作成元モデル,変換先モデル,センターXZ,上半身,下半身,足IK,つま先,つま先IK,肩,センターY,捩り分散,モーフ置換,接触回避,接触回避剛体,位置合わせ,指位置合わせ,床位置合わせ,手首距離,指距離,床距離,腕チェック省略,全移動量補正値,足IKオフセット,カメラVMD,距離可動範囲,カメラ作成元,全長Yオフセット";
        static bool On(string value)=>value=="1" || value.Equals("true",StringComparison.OrdinalIgnoreCase) || value=="○";
        static double Number(string value,double fallback,double min,double max)
        {
            double result=string.IsNullOrWhiteSpace(value)?fallback:double.Parse(value,NumberStyles.Float,Invariant);
            if(!(result>=min && result<=max))throw new FormatException("数値が範囲外です: "+value);return result;
        }
        public static string Save(Person[] people,SizingOptions options)
        {
            string path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(people[0].Motion)),
                "一括サイジング用データ_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".csv");
            var lines=new List<string>{Header};string Flag(bool on)=>on?"1":"0";
            foreach(var p in people)
            {
                var fields=new List<string>{"1",p.Motion,p.Source,p.Target};
                fields.AddRange(p.StanceParts.Select(on=>Flag(p.Stance && on)));
                fields.AddRange(new[]{Flag(p.Twist),p.MorphRules,Flag(options.Avoid),string.Join(";",p.AvoidShapes??new[]{"頭接触回避"}),Flag(options.Align),Flag(options.Fingers),Flag(options.Floor),
                    options.WristDistance.ToString(Invariant),options.FingerDistance.ToString(Invariant),options.FloorDistance.ToString(Invariant),Flag(options.SkipArmCheck),
                    p.Movement.ToString(Invariant),p.FootOffset.ToString(Invariant),options.Camera,options.DistanceLimit.ToString(Invariant),p.CameraSource,p.HeightOffset.ToString(Invariant)});
                lines.Add(string.Join(",",fields.Select(s=>"\""+(s??"").Replace("\"","\"\"")+"\"")));
            }
            VmdDocument.AtomicFile(path,temp=>File.WriteAllBytes(temp,MmdText.Encode(string.Join("\r\n",lines)+"\r\n")));return path;
        }
        public static void Run(IPluginContext context,string path,bool check,Action<string> log,CancellationToken stop)
        {
            var rows=MotionCsv.Rows(path).ToArray();
            if(rows.Any(r=>r.Length<29))throw new InvalidDataException("一括CSVは29列必要です。");
            foreach(var group in rows.GroupBy(r=>int.Parse(r[0],Invariant)))
            {
                stop.ThrowIfCancellationRequested();var g=group.First();
                var options=new SizingOptions{Avoid=On(g[14]),Align=On(g[16]),Fingers=On(g[17]),Floor=On(g[18]),SkipArmCheck=On(g[22]),
                    WristDistance=Number(g[19],1.7,0,10),FingerDistance=Number(g[20],1.4,0,10),FloorDistance=Number(g[21],1.2,0,10),
                    Camera=g[25],DistanceLimit=Number(g[26],5,1,5)};
                var people=group.Select(r=>{
                    var parts=r.Skip(4).Take(8).Select(On).ToArray();
                    return new Person{Motion=r[1],Source=r[2],Target=r[3],Stance=parts.Any(b=>b),StanceParts=parts,Twist=On(r[12]),MorphRules=r[13],
                        AvoidShapes=r[15].Length==0?null:r[15].Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries),Movement=Number(r[23],1,.5,1.5),FootOffset=Number(r[24],0,-2,2),CameraSource=r[27],HeightOffset=Number(r[28],0,-1000,1000)};
                }).ToArray();
                log("グループ "+group.Key);Sizing.Run(context,people,check,log,stop,options);
            }
        }
    }
}
