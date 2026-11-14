using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Ava.Extensions;
using Ava.MotionTools;
using Ava.SurfaceEffects;

static class WindowChecks
{
    sealed class Lease:IDisposable {public void Dispose(){}}
    sealed class Host:IPluginContext,IPluginRegistrar
    {
        public string PackageDirectory {get;set;}
        public string SettingsDirectory=>PackageDirectory;
        public IntPtr MainWindow=>IntPtr.Zero;
        public long DocumentGeneration=>1;
        readonly object sync=new object();byte[] state;
        public byte[] ReadState(){lock(sync)return state;}
        public void WriteState(byte[] value){lock(sync)state=value;}
        public void Report(string message){Console.WriteLine(message);}
        public void Post(Action operation)=>operation();
        public bool Execute(string id){if(!commands.TryGetValue(id,out var action))return false;action();return true;}
        public bool CanExecute(string id)=>commands.ContainsKey(id);
        public IDisposable KeepActive()=>new Lease();
        public IDisposable ObserveProjects(Action<string> opened,Action<string> saved)=>new Lease();
        public IDisposable AddSurfaceProvider(ISurfaceProvider provider)=>new Lease();
        public MaterialSlot[] Materials()=>new[]{new MaterialSlot{ActorName="人物",ActorId="rig",Id="rig/0",MaterialName="ボディ"}};
        internal readonly Dictionary<string,Action> commands=new();
        public IDisposable AddShortcut(string command,string chord)=>new Lease();
        public IDisposable AddCommand(string id,string menu,string caption,Action execute,Func<bool> available,Func<bool> check){commands[id]=execute;return new Lease();}
        internal RigProfile MotionRig,TargetRig;
        public RigProfile[] ReadRigs(string path)
        {
            if(MotionRig!=null)return new[]{path=="target" && TargetRig!=null?TargetRig:MotionRig};
            float n=path=="target"?2:1;
            RigJoint J(string name,int parent,double y)=>new RigJoint{name=name,role=name,parent=parent,y=y*n};
            return new[]{new RigProfile{name=path,joints=new[]{J("hips",-1,1),J("leftUpperLeg",0,1),J("leftLowerLeg",1,.5),J("leftFoot",2,0),J("head",0,2)}}};
        }
    }
    public static void Sizing(string dir,string input,string camera)
    {
        var ctx=new Host{PackageDirectory=dir};var target=Path.Combine(dir,"sized.vmd");
        var model=VmdDocument.Read(input);
        Array.Copy(MmdText.Fixed("センター",15),model.Records(MotionChannel.Bone)[0],15);
        var sourceFile=Path.Combine(dir,"movement.vmd");model.Save(sourceFile);
        var person=new Person{Motion=sourceFile,Source="source",Target="target",Output=target};
        Ava.MotionTools.Sizing.Run(ctx,new[]{person},false,Console.WriteLine,CancellationToken.None);
        var scaled=VmdDocument.Read(target).Records(MotionChannel.Bone).Single();
        if(BitConverter.ToSingle(scaled,19)!=2 || BitConverter.ToSingle(scaled,23)!=4)throw new Exception("leg scale");
        var settings=new SizingOptions{CameraOnly=true,Camera=camera,CameraOutput=Path.Combine(dir,"camera-sized.vmd")};
        var before=File.ReadAllBytes(target);
        Ava.MotionTools.Sizing.Run(ctx,new[]{person},false,Console.WriteLine,CancellationToken.None,settings);
        if(!File.ReadAllBytes(target).SequenceEqual(before) || !File.Exists(settings.CameraOutput))throw new Exception("camera-only write");
        var full=VmdDocument.Read(settings.CameraOutput).Records(MotionChannel.Camera).Single();
        var original=VmdDocument.Read(camera).Records(MotionChannel.Camera).Single();
        float distance=BitConverter.ToSingle(original,4);
        if(Math.Abs(BitConverter.ToSingle(full,4)/distance-2)>1e-5)throw new Exception("unlimited camera distance");
        settings.DistanceLimit=1.05;
        Ava.MotionTools.Sizing.Run(ctx,new[]{person},false,Console.WriteLine,CancellationToken.None,settings);
        var limited=VmdDocument.Read(settings.CameraOutput).Records(MotionChannel.Camera).Single();
        if(Math.Abs(BitConverter.ToSingle(limited,4)/distance-1.05)>1e-5 || !limited.Skip(32).Take(24).SequenceEqual(original.Skip(32).Take(24)))throw new Exception("limited camera distance");
        var duplicate=new SizingOptions{Camera=camera,CameraOutput=target};
        try{
            Ava.MotionTools.Sizing.Run(ctx,new[]{person},false,Console.WriteLine,CancellationToken.None,duplicate);
            throw new Exception("duplicate outputs accepted");
        }catch(IOException){if(!File.ReadAllBytes(target).SequenceEqual(before))throw new Exception("duplicate output changed");}
        string batch=SizingBatch.Save(new[]{person},settings);SizingBatch.Run(ctx,batch,true,Console.WriteLine,CancellationToken.None);
        TwistChannels(ctx,dir);
        TrunkAndFeet(ctx,dir);
    }

    static void TrunkAndFeet(Host ctx,string dir)
    {
        RigProfile Rig(bool target){
            var joints=new List<RigJoint>();
            int Add(string name,string role,int parent,double x,double y,double z=0,string ikRoot=null){
                if(target){z+=Math.Max(0,y-9)*.19;y=y<9?y*1.17:9*1.17+(y-9)*.9;}
                joints.Add(new RigJoint{name=name,role=role,parent=parent,x=x,y=y,z=z,ikRoot=ikRoot});return joints.Count-1;
            }
            int root=Add("全ての親",null,-1,0,0),center=Add("センター",null,root,0,8);
            int groove=Add("グルーブ",null,center,0,8.1),hips=Add("下半身","hips",groove,0,9);
            int spine=Add("上半身","spine",groove,0,10),chest=Add("上半身2","chest",spine,0,12);
            int neck=Add("首","neck",chest,0,14);Add("頭","head",neck,0,15);
            foreach(var (prefix,side,sign) in new[]{("左","left",1),("右","right",-1)}){
                int thigh=Add(prefix+"足",side+"UpperLeg",hips,sign,9);
                int knee=Add(prefix+"ひざ",side+"LowerLeg",thigh,sign,5,-.1);
                int foot=Add(prefix+"足首",side+"Foot",knee,sign,1);Add(prefix+"つま先",side+"Toes",foot,sign,0,-1);
                int ik=Add(prefix+"足ＩＫ",side+"FootIK",root,sign,1,0,prefix+"足");
                Add(prefix+"つま先ＩＫ",side+"ToesIK",ik,sign,0,-1,prefix+"足首");
                Add(prefix+"腕",side+"UpperArm",chest,1.5*sign,13);
            }
            return new RigProfile{name="stature",metersPerMotionUnit=1,joints=joints.ToArray()};
        }
        ctx.MotionRig=Rig(false);ctx.TargetRig=Rig(true);
        var vmd=new VmdDocument("pose");
        void AddKey(string bone,int frame,Vector3 position,Vector3 euler){
            var q=Quaternion.CreateFromYawPitchRoll(euler.Y*MathF.PI/180,euler.X*MathF.PI/180,euler.Z*MathF.PI/180);
            var row=new byte[111];Array.Copy(MmdText.Fixed(bone,15),row,15);Array.Copy(BitConverter.GetBytes(frame),0,row,15,4);
            var values=new[]{position.X,position.Y,position.Z,q.X,q.Y,q.Z,q.W};
            for(int i=0;i<values.Length;i++)VmdDocument.Number(row,19+4*i,values[i]);
            vmd.Records(MotionChannel.Bone).Add(row);
        }
        foreach(string n in new[]{"上半身","左足ＩＫ","左つま先ＩＫ"})AddKey(n,0,Vector3.Zero,Vector3.Zero);
        AddKey("上半身",1,Vector3.Zero,new Vector3(9,-7,4));
        AddKey("左足ＩＫ",1,new Vector3(-.31f,-.12f,.27f),new Vector3(2,9,-7));
        AddKey("左つま先ＩＫ",1,new Vector3(.09f,.15f,-.21f),new Vector3(2,9,-7));
        string input=Path.Combine(dir,"stride.vmd"),output=Path.Combine(dir,"stride-sized.vmd");vmd.Save(input);
        var p=new Person{Source="source",Target="target",Motion=input,Output=output,Stance=true,
            StanceParts=new[]{false,true,false,true,false,true,false,false}};
        Ava.MotionTools.Sizing.Run(ctx,new[]{p},false,_=>{},CancellationToken.None);
        var rows=VmdDocument.Read(output).Records(MotionChannel.Bone).Where(r=>VmdDocument.Frame(MotionChannel.Bone,r)==1).ToDictionary(VmdDocument.Name);
        var upper=rows["上半身"];var toe=rows["左つま先ＩＫ"];var leg=rows["左足ＩＫ"];
        if(Math.Abs(BitConverter.ToSingle(upper,31)-.08438556f)>1e-5 || Math.Abs(BitConverter.ToSingle(upper,35)+.06353442f)>1e-5 ||
            BitConverter.ToSingle(toe,19)!=0 || BitConverter.ToSingle(toe,43)!=1 || Math.Abs(BitConverter.ToSingle(leg,35))<.01f)
            throw new InvalidDataException("Trunk orientation and toe IK transfer");
        ctx.TargetRig=null;
    }

    static void TwistChannels(Host ctx,string dir)
    {
        var joints=new List<RigJoint>();
        int Add(string name,string role,int parent,float x,float y){
            joints.Add(new RigJoint{name=name,role=role,parent=parent,x=x,y=y});
            return joints.Count-1;
        }
        int hips=Add("センター","hips",-1,0,1);
        foreach(var (role,prefix,sign) in new[]{("left","左",1),("right","右",-1)}){
            int leg=Add(prefix+"足",role+"UpperLeg",hips,.2f*sign,1);
            int knee=Add(prefix+"ひざ",role+"LowerLeg",leg,.2f*sign,.5f);
            Add(prefix+"足首",role+"Foot",knee,.2f*sign,0);
            int arm=Add(prefix+"腕",role+"UpperArm",hips,.2f*sign,1.2f);
            int roll=Add(prefix+"腕捩",role+"ArmTwist",arm,.4f*sign,1.2f);
            int elbow=Add(prefix+"ひじ",role+"LowerArm",roll,.6f*sign,1.2f);
            int forearm=Add(prefix+"手捩",role+"WristTwist",elbow,.8f*sign,1.2f);
            Add(prefix+"手首",role+"Hand",forearm,sign,1.2f);
        }
        ctx.MotionRig=new RigProfile{name="腕",metersPerMotionUnit=1,joints=joints.ToArray()};
        var input=new VmdDocument("腕");
        foreach(var (bone,degrees) in new[]{("左腕",36f),("左ひじ",0f),("左手首",-20f)}){
            var row=new byte[111];Array.Copy(MmdText.Fixed(bone,15),row,15);
            float half=degrees*MathF.PI/360;VmdDocument.Number(row,31,MathF.Sin(half));VmdDocument.Number(row,43,MathF.Cos(half));
            input.Records(MotionChannel.Bone).Add(row);
        }
        string motion=Path.Combine(dir,"twist-input.vmd"),sized=Path.Combine(dir,"twist.vmd");input.Save(motion);
        var person=new Person{Motion=motion,Source="source",Target="target",Output=sized,Twist=true};
        Ava.MotionTools.Sizing.Run(ctx,new[]{person},false,_=>{},CancellationToken.None);
        var keys=VmdDocument.Read(sized).Records(MotionChannel.Bone).ToDictionary(VmdDocument.Name);
        foreach(var (bone,degrees) in new[]{("左腕",0f),("左腕捩",36f),("左手捩",-20f),("左手首",0f)}){
            float x=BitConverter.ToSingle(keys[bone],31),w=BitConverter.ToSingle(keys[bone],43);
            if(Math.Abs(2*MathF.Atan2(x,w)*180/MathF.PI-degrees)>.001)throw new InvalidDataException("Twist distribution: "+bone);
        }

        Quaternion Axis(Vector3 axis,float angle)=>Quaternion.CreateFromAxisAngle(axis,angle*MathF.PI/180);
        Quaternion Pose(float roll,float yaw,float lift)=>
            Axis(Vector3.UnitY,yaw)*Axis(Vector3.UnitZ,lift)*Axis(Vector3.UnitX,roll);
        input=new VmdDocument("腕");
        foreach(var (bone,rotation) in new[]{("左腕",Pose(26,-17,-32)),("左腕捩",Pose(8,0,0)),
            ("左ひじ",Pose(8,71,0)),("左手捩",Pose(5,0,0)),("左手首",Pose(28,-18,7))}){
            var row=new byte[111];Array.Copy(MmdText.Fixed(bone,15),row,15);
            VmdDocument.Number(row,31,rotation.X);VmdDocument.Number(row,35,rotation.Y);
            VmdDocument.Number(row,39,rotation.Z);VmdDocument.Number(row,43,rotation.W);
            input.Records(MotionChannel.Bone).Add(row);
        }
        input.Save(motion);
        Ava.MotionTools.Sizing.Run(ctx,new[]{person},false,_=>{},CancellationToken.None);
        keys=VmdDocument.Read(sized).Records(MotionChannel.Bone).ToDictionary(VmdDocument.Name);
        foreach(var (bone,degrees) in new[]{("左腕捩",38.788f),("左手捩",39.017f)}){
            var row=keys[bone];
            float actual=2*MathF.Atan2(BitConverter.ToSingle(row,31),BitConverter.ToSingle(row,43))*180/MathF.PI;
            if(Math.Abs(actual-degrees)>.001 || Math.Abs(BitConverter.ToSingle(row,35))+Math.Abs(BitConverter.ToSingle(row,39))>.0001)
                throw new InvalidDataException("Bent arm roll: "+bone);
        }
    }

    [DllImport("user32",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowW(string cls,string caption);
    [DllImport("user32")] static extern IntPtr GetDlgItem(IntPtr window,int id);
    [DllImport("user32")] static extern IntPtr SendMessageW(IntPtr item,uint message,IntPtr w,IntPtr l);
    [DllImport("user32",CharSet=CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr item,string text);
    [DllImport("user32",CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr item,StringBuilder buffer,int count);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr window,int show);
    static IntPtr Wait(string title)
    {
        for(int i=0;i<100;i++){
            var window=FindWindowW("AvaDanceToolWindow",title);
            if(window!=IntPtr.Zero){GetWindowThreadProcessId(window,out var pid);if(pid==Environment.ProcessId)return window;}
            Thread.Sleep(40);
        }
        throw new Exception("Window timeout: "+title);
    }
    [StructLayout(LayoutKind.Sequential)] struct Notify {public IntPtr window,id;public int code;}
    [DllImport("AvaClassicUi",CallingConvention=CallingConvention.Cdecl)]static extern void ava_ui_select(IntPtr list,int index);
    [DllImport("kernel32")] static extern IntPtr GlobalAlloc(uint flags,UIntPtr size);
    [DllImport("kernel32")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32")] static extern bool GlobalUnlock(IntPtr memory);
    static void DropFile(IntPtr window,string path)
    {
        byte[] filename=Encoding.Unicode.GetBytes(path+"\0\0");int size=20+filename.Length;
        var memory=GlobalAlloc(0x42,(UIntPtr)size);var data=GlobalLock(memory);
        Marshal.WriteInt32(data,20);Marshal.WriteInt32(data,16,1);Marshal.Copy(filename,0,data+20,filename.Length);
        GlobalUnlock(memory);SendMessageW(window,0x233,memory,IntPtr.Zero);
    }
    static void ShaderGraph()
    {
        const string code="#define COUNT 2\nint repeats=COUNT; int n=0; float4 Tint(float2 uv:TEXCOORD0, uniform float4 c):COLOR { return c+n*0.01; } "+
            "texture2D ColorA:RENDERCOLORTARGET<float2 ViewportRatio={0.5,0.5};string Format=\"A16B16G16R16F\";>; "+
            "technique A<string Script=\"RenderColorTarget=ColorA;LoopByCount=repeats;LoopGetIndex=n;Pass=Paint;LoopEnd=;RenderColorTarget=;\";>"+
            "{pass Paint{PixelShader=compile ps_3_0 Tint(float4(1,0,1,1));}}";
        var programs=EffectCompiler.Compile(code,out var plan);
        try {
            if(plan.Textures.Length!=1 || plan.Textures[0].RatioX!=.5f || plan.Steps.Count(s=>s.Operation==SurfaceOperation.Geometry)!=2 ||
                !programs.Single().Disassemble().Contains("ps_5_0"))throw new Exception("FX graph or shader stages");
        }finally{foreach(var pass in programs)pass.Dispose();}
        try {EffectCompiler.Compile(code.Replace("LoopEnd=;",""));throw new Exception("Unclosed loop accepted");}catch(FormatException){}
    }
    public static void Run(string dir,string input)
    {
        ShaderGraph();
        var ctx=new Host {PackageDirectory=dir};
        using var motion=new MotionPlugin();motion.Register(ctx);motion.Activate(ctx);
        ctx.commands["ava.vmd-sizing.open"]();var w=Wait("VMDサイジング");ShowWindow(w,0);
        ctx.commands["ava.vmd-sizing.open"]();if(Wait("VMDサイジング")!=w)throw new Exception("Duplicated window");
        var tab=GetDlgItem(w,10);if(SendMessageW(tab,0x1304,IntPtr.Zero,IntPtr.Zero).ToInt32()!=9)throw new Exception("Tab count");
        SendMessageW(tab,0x130c,new IntPtr(7),IntPtr.Zero);
        var data=Marshal.AllocHGlobal(Marshal.SizeOf<Notify>());
        try{Marshal.StructureToPtr(new Notify{window=tab,id=new IntPtr(10),code=-551},data,false);SendMessageW(w,78,new IntPtr(10),data);}finally{Marshal.FreeHGlobal(data);}
        SetWindowTextW(GetDlgItem(w,500),input);
        SendMessageW(w,273,new IntPtr(11),IntPtr.Zero);
        var log=new StringBuilder(4000);
        for(int i=0;i<100;i++){GetWindowTextW(GetDlgItem(w,13),log,log.Capacity);if(log.ToString().Contains("完了"))break;Thread.Sleep(30);}
        if(!log.ToString().Contains("完了"))throw new Exception("CSV window: "+log);
        motion.Deactivate();if(IsWindow(w))throw new Exception("Motion window outlived extension");
        using var effect=new EffectPlugin();effect.Register(ctx);effect.Activate(ctx);
        ctx.commands["ava.mme.assign"]();var e=Wait("AvaAvaEffect - エフェクトファイル割り当て");ShowWindow(e,0);
        var list=GetDlgItem(e,1003);
        if(SendMessageW(list,0x1004,IntPtr.Zero,IntPtr.Zero).ToInt32()!=2)throw new Exception("Assignment rows");
        SendMessageW(e,273,new IntPtr(1200),IntPtr.Zero);
        if(SendMessageW(list,0x1004,IntPtr.Zero,IntPtr.Zero).ToInt32()!=3)throw new Exception("Subset rows");
        ava_ui_select(list,2);
        var paint=Path.Combine(dir,"paint.fx");
        File.WriteAllText(paint,"float4 Tint():COLOR{return float4(1,0,1,1);} technique Main{pass A{PixelShader=compile ps_3_0 Tint();}}");
        DropFile(e,paint);
        if(!SpinWait.SpinUntil(()=>ctx.ReadState()!=null && Encoding.UTF8.GetString(ctx.ReadState()).Contains("Tint"),10000))
            throw new Exception("Drop did not apply compiled material");
        DropFile(e,paint);SendMessageW(e,273,new IntPtr(1008),IntPtr.Zero);
        DropFile(e,paint);SendMessageW(e,273,new IntPtr(1008),IntPtr.Zero);
        if(!Encoding.UTF8.GetString(ctx.ReadState()).Contains("none"))throw new Exception("Clear superseded by compile");
        SendMessageW(e,273,new IntPtr(1011),IntPtr.Zero);SendMessageW(e,273,new IntPtr(1005),IntPtr.Zero);
        var committed=ctx.ReadState();if(committed==null)throw new Exception("Apply checkpoint");
        SendMessageW(e,273,new IntPtr(1008),IntPtr.Zero);SendMessageW(e,273,new IntPtr(2),IntPtr.Zero);
        for(int i=0;i<100 && IsWindow(e);i++)Thread.Sleep(20);
        if(!committed.SequenceEqual(ctx.ReadState()))throw new Exception("Cancel did not restore Apply checkpoint");
        effect.Deactivate();if(IsWindow(e))throw new Exception("Assignment window outlived extension");
        Console.WriteLine("PASS native window controls, conversion and shutdown");
    }
}
