using System;
using System.IO;
using System.Linq;
using Ava.Extensions;
using Ava.MotionTools;

if(args.Length==2 && args[0]=="--compile-effect"){
    var source=Ava.SurfaceEffects.EffectCompiler.Source(Path.GetFullPath(args[1]));
    var passes=Ava.SurfaceEffects.EffectCompiler.Compile(source,out var plan);
    try{
        foreach(var pass in passes)pass.ValidateDevice();
        Console.WriteLine($"PASS compiled {passes.Length} passes, {plan.Textures.Length} textures, {plan.Steps.Length} commands");
    }finally{foreach(var pass in passes)pass.Dispose();}
    return;
}
var dir=Directory.CreateTempSubdirectory();
try
{
    var at=new DateTime(2026,1,2,3,4,5);
    string Input(string name,string text) {
        string path=Path.Combine(dir.FullName,name+".csv");
        File.WriteAllBytes(path,MmdText.Encode(text));return path;
    }
    var interp=string.Join(",",Enumerable.Range(0,64));
    var bone=Input("b","ヘッダー\r\n\"腕,左\",3.9,1,2,-3,12,-25,43,"+interp+"\r\n");
    var morph=Input("m","header\r\nまばたき,1,0.7\r\nまばたき,1,-1.2\r\n");
    var cam=Input("c","header\r\n3,1,2,3,.2,-.4,.6,23,40,1,"+string.Join(",",Enumerable.Range(1,24))+"\r\n");
    var files=MotionCsv.Import(bone,morph,cam,at);
    void Check(bool ok,string label){if(!ok)throw new Exception(label);}
    Check(files.Count==2 && files[0].EndsWith("b_bone_20260102_030405.vmd"),"model and camera outputs");
    var model=VmdDocument.Read(files[0]);
    var record=model.Records(MotionChannel.Bone).Single();
    Check(VmdDocument.Frame(MotionChannel.Bone,record)==3 && VmdDocument.Name(record)=="腕,左","CP932 and frame truncation");
    Check(record.Skip(47).SequenceEqual(Enumerable.Range(0,64).Select(i=>(byte)i)),"bone interpolation");
    Check(BitConverter.ToSingle(model.Records(MotionChannel.Morph).Single(),19)==-1.2f,"duplicate morph and unclamped weight");
    var csv=MotionCsv.Export(files[0],at).Concat(MotionCsv.Export(files[1],at)).ToArray();
    var columns=MmdText.Decode(File.ReadAllBytes(csv[0])).Split("\r\n");
    Check(columns[0].Split(',').Length==72 && columns[1].StartsWith("\"腕,左\""),"bone CSV");
    var repeated=MotionCsv.Import(csv[0],csv[1],csv[2],at);
    var second=VmdDocument.Read(repeated[0]).Records(MotionChannel.Bone).Single();
    for(int i=31;i<47;i+=4)Check(Math.Abs(BitConverter.ToSingle(record,i)-BitConverter.ToSingle(second,i))<1e-6,"Euler roundtrip");
    var camera=VmdDocument.Read(repeated[1]).Records(MotionChannel.Camera).Single();
    Check(camera[60]==1 && BitConverter.ToSingle(camera,4)==-23 && camera.Skip(32).Take(24).SequenceEqual(Enumerable.Range(1,24).Select(i=>(byte)i)),"camera sign, radians and raw curve bytes");
    var raw=Path.Combine(dir.FullName,"raw.vmd");model.Save(raw);Check(File.ReadAllBytes(raw).SequenceEqual(File.ReadAllBytes(files[0])),"lossless binary");
    Sizing.RemapMorphs(model,"まばたき:A:-2;まばたき:B:0.5;まばたき:A:-2;");
    Check(model.Records(MotionChannel.Morph).Count==2 && BitConverter.ToSingle(model.Records(MotionChannel.Morph)[0],19)==2.4f,"morph fan-out");
    WindowChecks.Sizing(dir.FullName,files[0],files[1]);
    if(args.Contains("--windows"))WindowChecks.Run(dir.FullName,files[0]);
    Console.WriteLine("PASS motion conversion, sizing and morph mapping");
}
finally {dir.Delete(true);}
