using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public sealed class Person
    {
        public string Motion, Source, Target, Output;
        public double Movement=1, FootOffset;
        public string MorphRules,CameraSource;
        public bool Stance,Twist;
        public string[] AvoidShapes;
        public bool[] StanceParts={true,true,true,false,true,true,true,true};
        public double HeightOffset;
        public Person Copy()=>(Person)MemberwiseClone();
    }

    public static partial class Sizing
    {
        sealed class ActorInput
        {
            public Person Person;
            public RigProfile Source,Target;
            public VmdDocument Motion,Original;
            public string Output;
            public SkeletonMotion Before,After;
            public double Horizontal=1,Vertical=1;
            public System.Numerics.Vector3 CenterOffset;
        }
        static double Ratio(double target,double source) => Math.Abs(source)>1e-8 ? target/source : 1;
        static (double x,double y,double z) Leg(RigProfile rig)
        {
            var hip=rig.Find("leftUpperLeg","左足","LeftUpperLeg","LeftUpLeg");
            var knee=rig.Find("leftLowerLeg","左ひざ","左膝","LeftLeg");
            var ankle=rig.Find("leftFoot","左足首","LeftFoot");
            if(hip==null || knee==null || ankle==null ||
                rig.Find("hips","センター","Hips")==null) throw new ArgumentException(rig.name+" に脚の基準ボーンがありません。");
            return (hip.x-ankle.x,hip.y-ankle.y,hip.z-ankle.z);
        }
        static double Length((double x,double y,double z) d) => Math.Sqrt(d.x*d.x+d.y*d.y+d.z*d.z);

        public static void RemapMorphs(VmdDocument motion,string options)
        {
            if(string.IsNullOrWhiteSpace(options)) return;
            var rules=options.Split(new[]{';','\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(text=>{
                var fields=text.Split(':');
                if(fields.Length!=3 || fields[0].Length==0 || fields[1].Length==0 ||
                    !double.TryParse(fields[2],NumberStyles.Float,CultureInfo.InvariantCulture,out var factor) || !(factor>=-10 && factor<=10))
                    throw new InvalidDataException("モーフ指定は 元:先:倍率 で入力してください。");
                return (source:fields[0], target:fields[1], factor);
            }).Distinct().ToLookup(r=>r.source);
            var values=new Dictionary<(string name,uint frame),double>();
            var rows=motion.Records(MotionChannel.Morph);
            var touched=new HashSet<string>(rules.SelectMany(group=>group.Select(rule=>rule.target)),StringComparer.Ordinal);
            var originals=new Dictionary<(string name,uint frame),byte[]>();
            var output=new List<byte[]>(rows.Count);
            foreach(var row in rows)
            {
                string name=VmdDocument.Name(row); uint frame=VmdDocument.Frame(MotionChannel.Morph,row);
                if(!rules.Contains(name) && !touched.Contains(name)){output.Add(row);continue;}
                originals[(name,frame)]=row;
                double weight=BitConverter.ToSingle(row,19);
                void Accumulate(string to,double v){values.TryGetValue((to,frame),out var prior);values[(to,frame)]=prior+v;}
                if(rules.Contains(name)) foreach(var rule in rules[name]) Accumulate(rule.target,weight*rule.factor);
                else Accumulate(name,weight);
            }
            foreach(var pair in values)
            {
                if(!originals.TryGetValue(pair.Key,out var row)){
                    row=new byte[23];Array.Copy(MmdText.Fixed(pair.Key.name,15),row,15);
                    Buffer.BlockCopy(BitConverter.GetBytes(pair.Key.frame),0,row,15,4);
                }
                VmdDocument.Number(row,19,(float)pair.Value);output.Add(row);
            }
            rows.Clear();rows.AddRange(output);
        }

        internal static RigProfile ReadRig(IPluginContext context,string path)
        {
            var parts=path.Split('|');var profiles=context.ReadRigs(parts[0]);
            if(parts.Length>1) return profiles.Single(p=>p.sourceId==parts[1] || p.name==parts[1]);
            if(profiles.Length!=1) throw new InvalidDataException("使用するモデルを パス|GUID で指定してください。\r\n"+
                string.Join("\r\n",profiles.Select(p=>p.name+"  "+p.sourceId)));
            return profiles[0];
        }

        public static void Run(IPluginContext context,Person[] people,bool checkOnly,Action<string> log,CancellationToken stop,SizingOptions options=null)
        {
            if(people.Length==0)throw new InvalidDataException("人物の入力を指定してください。");
            options??=new SizingOptions();
            string wildcard=people[0].Motion;
            if(wildcard.IndexOfAny(new[]{'*','?'})>=0 || Directory.Exists(wildcard))
            {
                var folder=Directory.Exists(wildcard)?wildcard:Path.GetDirectoryName(wildcard);
                var pattern=Directory.Exists(wildcard)?"*.vmd":Path.GetFileName(wildcard);
                var files=Directory.GetFiles(string.IsNullOrEmpty(folder)?Directory.GetCurrentDirectory():folder,pattern).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).ToArray();
                if(files.Length==0)throw new FileNotFoundException("対象のモーションがありません。",wildcard);
                foreach(var file in files){var group=people.Select(p=>p.Copy()).ToArray();group[0].Motion=file;group[0].Output=null;Run(context,group,checkOnly,log,stop,options);}return;
            }
            var inputs=new List<ActorInput>();
            foreach(var p in people)
            {
                stop.ThrowIfCancellationRequested();
                var original=VmdDocument.Read(p.Motion);
                var input=new ActorInput{Person=p,Source=LegControls(ReadRig(context,p.Source),original),
                    Target=LegControls(ReadRig(context,p.Target),original),Original=original};
                input.Motion=options.CameraOnly?VmdDocument.Read(p.Output):input.Original.Copy();
                try {
                    var source=Leg(input.Source); var target=Leg(input.Target);
                    input.Horizontal=Ratio(Length(target),Length(source));input.Vertical=Ratio(target.y,source.y);
                } catch(ArgumentException ex){log(ex.Message+" 移動倍率は1です。");}
                input.CenterOffset=MovementOffset(input);
                input.Before=new SkeletonMotion(input.Source,input.Original);
                input.After=new SkeletonMotion(input.Target,input.Motion);inputs.Add(input);
            }
            double shared=inputs.Count==1?inputs[0].Horizontal:Math.Min(1.2,inputs.Average(a=>a.Horizontal));
            foreach(var input in inputs)
            {
                var person=input.Person;
                double xz=shared*person.Movement;
                log(input.Source.name+" → "+input.Target.name+"   XZ "+xz.ToString("0.###")+" / Y "+input.Vertical.ToString("0.###"));
                if(checkOnly || options.CameraOnly)continue;
                var mapping=MotionMapping(input.Source,input.Target);
                foreach(var row in input.Motion.Records(MotionChannel.Bone))
                {
                    stop.ThrowIfCancellationRequested();
                    TransformRecord(row,input,mapping,(float)xz);
                }
                input.After=new SkeletonMotion(input.Target,input.Motion);
                CorrectStance(input,options,log,stop);
            }
            if(checkOnly)return;
            if(!options.CameraOnly)AlignHands(inputs,options,stop,log);
            if(options.CameraOnly && string.IsNullOrWhiteSpace(options.Camera))throw new InvalidDataException("カメラの入力VMDを指定してください。");
            var outputs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Unique(string path){
                path=Path.GetFullPath(path);
                if(!outputs.Add(path))throw new IOException("出力先が重複しています。"+path);
                return path;
            }
            var camera=string.IsNullOrWhiteSpace(options.Camera)?null:AdjustCamera(context,inputs,options,stop);
            string cameraPath=camera==null?null:Unique(CameraPath(inputs,options));
            if(options.CameraOnly){
                if(inputs.Any(a=>StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(a.Person.Output),cameraPath)))throw new IOException("人物とカメラの出力先が重複しています。");
            }else foreach(var input in inputs)
            {
                var person=input.Person;
                stop.ThrowIfCancellationRequested();
                var path=person.Output;
                if(string.IsNullOrWhiteSpace(path))path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(person.Motion)),
                    Path.GetFileNameWithoutExtension(person.Motion)+"_"+Path.GetFileNameWithoutExtension(person.Target.Split('|')[0])+"_"+
                    (person.Stance?"S":"")+(person.Twist?"T":"")+(string.IsNullOrWhiteSpace(person.MorphRules)?"":"M")+
                    (options.Avoid?"I":"")+(options.Align?"P":"")+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".vmd");
                input.Output=Unique(path);
                RemapMorphs(input.Motion,person.MorphRules);
            }
            foreach(var input in inputs.Where(a=>a.Output!=null)){
                stop.ThrowIfCancellationRequested();input.Motion.Save(input.Output);log(input.Output);
            }
            if(camera!=null){stop.ThrowIfCancellationRequested();camera.Save(cameraPath);log(cameraPath);}
        }
    }
}
