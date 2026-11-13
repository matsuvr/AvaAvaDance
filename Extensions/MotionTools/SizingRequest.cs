using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionTools
{
    [DataContract]
    sealed class SizingRequest
    {
        [DataMember] public Person[] people {get;set;}
        [DataMember] public SizingOptions options {get;set;}

        public static string[] Run(IPluginContext host,string path,string output,CancellationToken cancellation)
        {
            using var stream=File.OpenRead(path);
            if(stream.Length>4*1024*1024)throw new InvalidDataException("入力設定が大きすぎます。");
            var request=(SizingRequest)new DataContractJsonSerializer(typeof(SizingRequest)).ReadObject(stream);
            if(request?.people==null || request.people.Length==0)throw new InvalidDataException("人物の入力がありません。");
            string folder=Path.GetDirectoryName(Path.GetFullPath(path));
            string Input(string name)=>string.IsNullOrEmpty(name)?null:Path.GetFullPath(Path.Combine(folder,name));
            string Output(string name)=>string.IsNullOrWhiteSpace(name)?null:Path.Combine(output,Path.GetFileName(name));
            string Rig(string spec){
                if(string.IsNullOrWhiteSpace(spec))return null;
                int split=spec.IndexOf('|');return split<0?Input(spec):Input(spec.Substring(0,split))+spec.Substring(split);
            }
            var options=request.options??new SizingOptions();
            Directory.CreateDirectory(output);
            for(int i=0;i<request.people.Length;i++){
                var person=request.people[i]??throw new InvalidDataException("人物の指定が不正です。");
                person.Motion=Input(person.Motion);person.Source=Rig(person.Source);person.Target=Rig(person.Target);
                person.CameraSource=Rig(person.CameraSource);person.Output=options.CameraOnly?Input(person.Output):Output(person.Output)??Path.Combine(output,"motion-"+(i+1)+".vmd");
            }
            options.Camera=Input(options.Camera);options.CameraOutput=Output(options.CameraOutput);
            if(options.Camera!=null && options.CameraOutput==null)options.CameraOutput=Path.Combine(output,"camera.vmd");
            Sizing.Run(host,request.people,false,host.Report,cancellation,options);
            return request.people.Where(p=>!options.CameraOnly).Select(p=>p.Output)
                .Concat(options.Camera!=null?new[]{options.CameraOutput}:Array.Empty<string>()).ToArray();
        }
    }
}
