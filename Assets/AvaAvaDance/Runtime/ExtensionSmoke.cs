using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Ava.Extensions;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class EffectFixture {public string package,fx,image,innerFx;public string[] companions;public string camera,controllerMotion,controllerTarget;public bool hideGrid; public int red,green,blue,frame,width,height;public bool probeDepth,automatic,inspectTargets,requireVariation;}
        [Serializable] sealed class SmokeVerdict {public string test,sha256,system,unity,graphics,result;}
        [Serializable] sealed class EffectProbeState {public int schemaVersion=1;public bool enabled=true,watch;public EffectProbeRow[] rows;}
        [Serializable] sealed class EffectProbeRow {public string slot,source,mode="effect";public bool visible=true;public EffectProbeImage[] images;}
        [Serializable] sealed class EffectProbeImage {public string resource;public int[] bytes;}

        static void SaveSmokeVerdict(string dir,string test,string input,string result)
        {
            Directory.CreateDirectory(dir);
            string hash=null;
            if(File.Exists(input)){using var stream=File.OpenRead(input);using var sha=SHA256.Create();hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
            var report=new SmokeVerdict{test=test,sha256=hash,system=SystemInfo.operatingSystem,unity=Application.unityVersion,
                graphics=SystemInfo.graphicsDeviceName,result=result};
            File.WriteAllText(Path.Combine(dir,"details.json"),JsonUtility.ToJson(report,true));
            File.WriteAllText(Path.Combine(dir,"result.txt"),result);
        }

        void CheckScreenHistory(string dir)
        {
            string path=Path.Combine(dir,"monitor.x");
            File.WriteAllText(path,"xof 0303txt 0032\nMesh { 3; -1.0;0.0;0.0;,1.0;0.0;0.0;,0.0;2.0;0.0;; 1; 3;0,1,2;; MeshMaterialList {1;1;0;; Material {1.0;1.0;1.0;1.0;;1.0;0.0;0.0;0.0;;0.0;0.0;0.0;; TextureFilename { \"screen.bmp\"; } } } }");
            LoadAccessory(path);screenMode=1;
            bool Same(Color a,Color b)=>Mathf.Abs(a.r-b.r)+Mathf.Abs(a.g-b.g)+Mathf.Abs(a.b-b.b)<.025f;
            Color Corner(RenderTexture rt){
                var active=RenderTexture.active;var pixel=new Texture2D(1,1,TextureFormat.RGB24,false);
                try{RenderTexture.active=rt;pixel.ReadPixels(new Rect(0,0,1,1),0,0,false);return pixel.GetPixel(0,0);}
                finally{RenderTexture.active=active;Destroy(pixel);}
            }
            SetFrame(0);previewCamera.Render();
            var input=previewHistory.Input;
            if(input==null || activeProp.Materials[0].mainTexture!=input)throw new InvalidDataException("スクリーンへの初期入力");
            var initial=Corner(input);var output=previewHistory.Output;var prior=Corner(output);
            previewCamera.Render();
            if(previewHistory.Input!=input || !Same(Corner(input),initial))throw new InvalidDataException("同一時刻の再描画");
            SetFrame(1);previewCamera.Render();
            if(previewHistory.Input!=output || !Same(Corner(previewHistory.Input),prior))throw new InvalidDataException("前サンプルの画像");
            exporting=true;
            try{
                previewCamera.Render();
                if(movieHistory.Input==previewHistory.Input || !Same(Corner(movieHistory.Input),initial))throw new InvalidDataException("動画の初期入力");
            }finally{exporting=false;movieHistory.Dispose();UpdateScreenMaterials();}
            screenMode=2;previewCamera.Render();
            if(previewHistory.Input.width!=previewTexture.height*4/3)throw new InvalidDataException("4:3スクリーンの寸法");
            screenMode=0;UpdateScreenMaterials();
            if(activeProp.Materials[0].mainTexture!=null)throw new InvalidDataException("スクリーンOFF");
        }

        void CheckGrabSequence(string dir)
        {
            var asset=actor.modelAssets.Single(a=>a.Guid==actor.modelGuid);
            var pack=actor.modelAssets.ToList();var back=actor.avatar.GetComponentInChildren<Renderer>();
            var backSlot=EnumerateMaterials().First(m=>m.renderer==back).slot;
            ImportModel(asset,pack,Import.PackageAvatar.LegacyPrefab(pack,asset));
            var front=actor.avatar.GetComponentInChildren<Renderer>();
            var prevBack=back.sharedMaterial;var prevFront=front.sharedMaterial;
            var shader=shaderLibrary.Resolve(null,"AvaAvaDance/GrabFixture");
            if(shader==null)throw new Exception("Grab fixture pack");
            var background=new Material(prevBack){renderQueue=3000};var foreground=new Material(shader){renderQueue=3001};
            var scope=extensions.Single(p=>p.manifest.id=="ava.mme");
            var program=new EffectProbeRow{slot=backSlot.Id,source="float4 P():COLOR{return float4(1,0,0,1);} technique M{pass Q{CullMode=None;ZWriteEnable=false;PixelShader=compile ps_3_0 P();}}"};
            try{
                back.sharedMaterial=background;front.sharedMaterial=foreground;
                scope.WriteState(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(new EffectProbeState{rows=new[]{program}})));documentGeneration++;
                var file=Path.Combine(dir,"grab.png");WritePreview(file);
                var image=new Texture2D(2,2);
                try{image.LoadImage(File.ReadAllBytes(file));if(image.GetPixels32().Count(c=>c.b>225 && c.g<30 && c.r<30)<128)
                    throw new Exception("Mixed transparent GrabPass sequence: "+status.text+" "+string.Join(",",Enumerable.Range(0,foreground.passCount).Select(foreground.GetPassName)));}
                finally{Destroy(image);}
            }finally{back.sharedMaterial=prevBack;front.sharedMaterial=prevFront;scope.WriteState(null);documentGeneration++;Destroy(background);Destroy(foreground);}
        }
        string RunFileSmoke(bool sizing,string path,string dir)
        {
            if(sizing){
                var plugin=(IFileProcessor)extensions.Single(e=>e.manifest.id=="ava.vmd-sizing").plugin;
                var files=plugin.Process(path,dir,CancellationToken.None);
                if(files.Length==0)throw new Exception("出力ファイルがありません。");
                foreach(string file in files){
                    var motion=VmdDocument.Read(file);
                    if(!Enum.GetValues(typeof(MotionChannel)).Cast<MotionChannel>().Any(c=>motion.Records(c).Count>0))throw new Exception("空のモーション");
                }
                return "PASS sizing output";
            }
            var spec=JsonUtility.FromJson<EffectFixture>(File.ReadAllText(path));
            string Input(string name)=>Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),name);
            var scope=extensions.Single(e=>e.manifest.id=="ava.mme");
            void LoadSource(string file){
                string pathName=Input(file);var ext=Path.GetExtension(file).ToLowerInvariant();
                if(ext==".x" || ext==".vac"){LoadAccessory(pathName);return;}
                if(IsVrm(file) || IsController(file)){
                    var asset=ReadVrmFile(pathName);
                    ImportModel(asset,new System.Collections.Generic.List<Import.UnityPackageReader.PackageAsset>{asset},null);
                    if(IsController(file) && !string.IsNullOrEmpty(spec.controllerMotion) &&
                        (string.IsNullOrEmpty(spec.controllerTarget) || string.Equals(file,spec.controllerTarget,StringComparison.OrdinalIgnoreCase))){ChangeMode(true);ImportMotionFile(Input(spec.controllerMotion));}
                    if(!asset.Data.SequenceEqual(File.ReadAllBytes(pathName)))throw new InvalidDataException("元データが読み込み時に変更されました。");
                    return;
                }
                var assets=Import.UnityPackageReader.Read(pathName);
                var avatar=assets.First(a=>a.HasAsset && a.Path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase));
                ImportModel(avatar,assets,Import.PackageAvatar.LegacyPrefab(assets,avatar));
            }
            LoadSource(spec.package);var material=scope.Materials().First();
            foreach(string companion in spec.companions??Array.Empty<string>())LoadSource(companion);
            if(spec.hideGrid)gridToggle.value=false;
            if(!string.IsNullOrEmpty(spec.camera)){ChangeMode(false);ImportMotionFile(Input(spec.camera));}
            if(spec.automatic){
                var provider=(ISurfaceProvider)scope.plugin;
                if(!provider.HasBindings)throw new InvalidDataException("モデルの自動エフェクトが見つかりません。");
                var applied=provider.Resolve(material);
                if(applied?.Passes==null || applied.Passes.Length==0)throw new InvalidDataException("割当材質のパスがありません。");
                void CaptureSample(string filename){
                    var prior=(exporting,outputDelta);exporting=true;outputDelta=0;
                    var priorTexture=previewTexture;var priorTarget=previewCamera.targetTexture;float aspect=previewCamera.aspect;
                    RenderTexture frameTarget=null;
                    try{
                        if(spec.width>0 && spec.height>0){
                            frameTarget=RenderTexture.GetTemporary(spec.width,spec.height,24,RenderTextureFormat.ARGB32);
                            previewTexture=frameTarget;previewCamera.targetTexture=frameTarget;previewCamera.aspect=(float)spec.width/spec.height;
                        }
                        EvaluateAnimation(spec.frame,false);WritePreview(Path.Combine(dir,filename));
                        if(spec.inspectTargets){
                            var mesh=new Mesh();var lines=new System.Collections.Generic.List<string>();
                            string V(Vector3 v)=>v.ToString("F6");
                            string Q(Quaternion q)=>q.ToString("F6");
                            try{
                                lines.Add("camera "+V(previewCamera.transform.position)+" "+Q(previewCamera.transform.rotation)+" "+previewCamera.fieldOfView);
                                foreach(var modelActor in drawOrder){
                                    var root=modelActor.avatar.transform;
                                    lines.Add(modelActor.id+" "+V(root.position)+" "+Q(root.rotation)+" "+V(root.lossyScale));
                                    foreach(var bone in modelActor.bones)lines.Add(modelActor.id+"/"+bone.Track.Id+" "+V(bone.Joint.position)+" "+Q(bone.Joint.rotation));
                                    foreach(var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>()){
                                        skin.BakeMesh(mesh,true);lines.Add(skin.name+" "+V(mesh.bounds.center)+" "+V(mesh.bounds.size));
                                    }
                                }
                                File.WriteAllLines(Path.Combine(dir,filename+".pose.txt"),lines);
                            }finally{Destroy(mesh);}
                        }
                    }
                    finally{(exporting,outputDelta)=prior;
                        previewCamera.targetTexture=priorTarget;previewCamera.aspect=aspect;previewTexture=priorTexture;
                        if(frameTarget!=null)RenderTexture.ReleaseTemporary(frameTarget);
                    }
                }
                CaptureSample("assigned.png");
                if(!string.IsNullOrEmpty(lastSurfaceDiagnostic))throw new InvalidDataException(lastSurfaceDiagnostic);
                if(spec.inspectTargets){
                    var ranges=new System.Collections.Generic.List<string>();
                    var prior=RenderTexture.active;var rt=RenderTexture.GetTemporary(32,32,0,RenderTextureFormat.ARGBFloat);
                    var probe=new Texture2D(32,32,TextureFormat.RGBAFloat,false);
                    try{
                        foreach(var pair in surfaceTargets.OrderBy(p=>p.Key)){
                            if(pair.Value.format==RenderTextureFormat.Depth)continue;
                            Graphics.Blit(pair.Value,rt);RenderTexture.active=rt;probe.ReadPixels(new Rect(0,0,32,32),0,0,false);
                            var pixels=probe.GetPixels();
                            if(spec.width>0){
                                var sample=pixels[(rt.height/2-1)*rt.width+rt.width/2];
                                ranges.Add(pair.Value.name+" center="+sample.ToString("F6")+" finite="+pixels.Count(c=>float.IsFinite(c.r+c.g+c.b))+"/"+pixels.Length);
                            }
                            ranges.Add(pair.Key+"  "+string.Join(" ",Enumerable.Range(0,4).Select(i=>pixels.Min(c=>c[i]).ToString("G4")+".."+pixels.Max(c=>c[i]).ToString("G4"))));
                        }
                    }finally{RenderTexture.active=prior;RenderTexture.ReleaseTemporary(rt);Destroy(probe);}
                    File.WriteAllLines(Path.Combine(dir,"targets.txt"),ranges);
                }
                string project=Path.Combine(dir,"assigned.avadance");SaveProject(project);OpenProject(project);
                if(!provider.HasBindings || provider.Resolve(scope.Materials().Single(m=>m.Id==material.Id))?.Passes==null)throw new InvalidDataException("保存したエフェクトを復元できません。");
                CaptureSample("reopened.png");
                if(!File.ReadAllBytes(Path.Combine(dir,"assigned.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(dir,"reopened.png"))))
                    throw new InvalidDataException("再読込後の画像が一致しません。");
                if(spec.requireVariation){
                    var check=new Texture2D(2,2);
                    try{check.LoadImage(File.ReadAllBytes(Path.Combine(dir,"reopened.png")));var pixels=check.GetPixels32();
                        if(pixels.All(c=>c.Equals(pixels[0])))throw new InvalidDataException("シーンの出力が単色です。");
                    }finally{Destroy(check);}
                }
                return "PASS automatic assignment and image round-trip";
            }
            var entry=new EffectProbeRow{slot=material.Id,source=File.ReadAllText(Input(spec.fx))};
            var config=new EffectProbeState{rows=new[]{entry}};
            if(!string.IsNullOrEmpty(spec.image)){
                var data=File.ReadAllBytes(Input(spec.image));
                entry.images=new[]{new EffectProbeImage{resource=spec.image,bytes=Array.ConvertAll(data,b=>(int)b)}};
            }
            if(!string.IsNullOrEmpty(spec.innerFx)){
                LoadSource(spec.package);
                var inner=scope.Materials().First(t=>t.ActorId!=material.ActorId);
                config.rows=new[]{entry,new EffectProbeRow{slot=inner.Id,source=File.ReadAllText(Input(spec.innerFx))}};
            }
            scope.WriteState(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(config)));documentGeneration++;SetFrame(spec.frame);
            string image=Path.Combine(dir,"surface.png");WritePreview(image);
            if(spec.probeDepth){
                var map=ReadSurfaceTexels(shadowDepth,shadowDepth.width*shadowDepth.height);
                if(!map.Any(c=>Math.Abs(c.r-.25f)<.001f))throw new InvalidDataException("影の深度値が一致しません。");
            }
            var texture=new Texture2D(2,2);
            try{texture.LoadImage(File.ReadAllBytes(image));if(!texture.GetPixels32().Any(c=>Math.Abs(c.r-spec.red)<6 && Math.Abs(c.g-spec.green)<6 && Math.Abs(c.b-spec.blue)<6))throw new InvalidDataException("描画結果の色が一致しません。");}
            finally{Destroy(texture);}
            return "PASS surface pixels";
        }

        [DllImport("user32",CharSet=CharSet.Unicode)]static extern IntPtr FindWindowExW(IntPtr parent,IntPtr after,string cls,string caption);
        [DllImport("user32")]static extern bool PostMessageW(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
        [DllImport("user32")]static extern IntPtr GetDlgItem(IntPtr hwnd,int id);
        [DllImport("user32",EntryPoint="GetDpiForWindow")]static extern uint SmokeDpi(IntPtr hwnd);
        static IntPtr SmokeNativeWindow(string caption)
        {
            using var process=System.Diagnostics.Process.GetCurrentProcess();
            IntPtr window=IntPtr.Zero;
            while((window=FindWindowExW(IntPtr.Zero,window,"AvaDanceToolWindow",caption))!=IntPtr.Zero){
                GetWindowThreadProcessId(window,out var pid);if(pid==process.Id)return window;
            }
            return IntPtr.Zero;
        }
        [DllImport("user32")]static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32")]static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32")]static extern uint GetPixel(IntPtr dc,int x,int y);
        [DllImport("user32")]static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
        [StructLayout(LayoutKind.Sequential)] struct ClientBounds {public int left,top,right,bottom;}
        [DllImport("user32")] static extern bool GetClientRect(IntPtr window,out ClientBounds bounds);

        static (int normal,int mirrored,int total) SampleWindow(IntPtr control,Texture2D capture)
        {
            if(!GetClientRect(control,out var bounds) || bounds.right<=0 || bounds.bottom<=0)return default;
            var dc=GetDC(control);if(dc==IntPtr.Zero)return default;
            int normal=0,mirrored=0,total=0;
            try{
                for(int j=1;j<22;j++)for(int i=1;i<24;i++){
                    int x=i*bounds.right/24,y=j*bounds.bottom/22;uint os=GetPixel(dc,x,y);if(os==uint.MaxValue)continue;
                    float u=(x+.5f)/bounds.right,v=(y+.5f)/bounds.bottom;
                    bool Matches(Color32 c)=>Math.Abs(c.r-(int)(os&255))+Math.Abs(c.g-(int)((os>>8)&255))+Math.Abs(c.b-(int)((os>>16)&255))<=15;
                    if(bounds.right==capture.width && bounds.bottom==capture.height){
                        if(Matches(capture.GetPixel(x,capture.height-1-y)))normal++;
                        if(Matches(capture.GetPixel(x,y)))mirrored++;
                    }else{
                        if(Matches(capture.GetPixelBilinear(u,1-v)))normal++;
                        if(Matches(capture.GetPixelBilinear(u,v)))mirrored++;
                    }
                    total++;
                }
            }finally{ReleaseDC(control,dc);}
            return (normal,mirrored,total);
        }

        [DllImport("user32")]static extern bool GetWindowRect(IntPtr window,out ClientBounds bounds);
        [DllImport("user32")]static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate bool MonitorVisitor(IntPtr monitor,IntPtr dc,ref ClientBounds bounds,IntPtr data);
        [DllImport("user32")]static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorVisitor visitor,IntPtr data);
        [DllImport("shcore")]static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);

        IEnumerator CheckMonitorScale(FloatingPanel panel,IntPtr window,IntPtr control,TextField field,string dir,Action<string> fail)
        {
            GetWindowRect(window,out var original);uint oldDpi=SmokeDpi(control),newDpi=0;ClientBounds other=default;
            var current=MonitorFromWindow(window,2);
            EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr m,IntPtr dc,ref ClientBounds r,IntPtr data)=>{
                if(m!=current && GetDpiForMonitor(m,0,out uint x,out _)==0 && x!=oldDpi){newDpi=x;other=r;return false;}
                return true;
            },IntPtr.Zero);
            if(newDpi==0){fail("FAIL second display with a different scale is required");yield break;}
            int presents=panel.PresentedFrames;
            try{
                SetWindowPos(window,IntPtr.Zero,other.left+24,other.top+24,0,0,0x15);
                var end=Time.realtimeSinceStartupAsDouble+5;
                while(Time.realtimeSinceStartupAsDouble<end && (SmokeDpi(control)!=newDpi || panel.PresentedFrames<presents+4))yield return null;
                GetClientRect(control,out var client);
                var surface=panel.Surface;
                if(SmokeDpi(control)!=newDpi || surface==null || surface.width!=client.right || surface.height!=client.bottom ||
                    Mathf.Abs(panel.Root.worldBound.width*newDpi/96f-client.right)>2){
                    fail("FAIL panel scale "+oldDpi+" -> "+SmokeDpi(control)+" / "+client.right+" / "+surface?.width);yield break;
                }
                var input=field.Q(className:"unity-text-field__input")??field;
                var point=input.worldBound.center*(newDpi/96f);
                var packed=new IntPtr(Mathf.RoundToInt(point.x)+(Mathf.RoundToInt(point.y)<<16));
                PostMessageW(control,0x201,new IntPtr(1),packed);PostMessageW(control,0x202,IntPtr.Zero,packed);
                yield return null;yield return null;
                string before=field.value;
                PostMessageW(control,0x102,new IntPtr('C'),IntPtr.Zero);
                end=Time.realtimeSinceStartupAsDouble+2;
                while(field.value==before && Time.realtimeSinceStartupAsDouble<end)yield return null;
                if(!field.value.Contains("C")){fail("FAIL pointer and text at "+newDpi+" DPI");yield break;}
                field.value=before;
                File.WriteAllText(Path.Combine(dir,"monitor-scale.txt"),oldDpi+" -> "+newDpi+" DPI / "+
                    surface.width+"x"+surface.height+" / logical width "+panel.Root.worldBound.width+" / pointer and text");
            }finally{
                SetWindowPos(window,IntPtr.Zero,original.left,original.top,original.right-original.left,original.bottom-original.top,0x14);
            }
        }

        [DllImport("user32",CharSet=CharSet.Unicode)]static extern IntPtr LoadKeyboardLayoutW(string name,uint flags);
        IEnumerator CheckSymbolLayouts(string directory,Action<string> fail)
        {
            var window=mainWindowHandle;
            if(!physicalMainKeys || window==IntPtr.Zero){fail("FAIL main keyboard window");yield break;}
            var thread=GetWindowThreadProcessId(window,out _);
            var old=ImeKeyboard.GetKeyboardLayout(thread);
            var log=new System.Text.StringBuilder();
            ImeKeyboard.SetForegroundWindow(window);
            double end=Time.realtimeSinceStartupAsDouble+30;
            while(GetForegroundWindow()!=window && Time.realtimeSinceStartupAsDouble<end)yield return null;
            if(GetForegroundWindow()!=window){fail("FAIL main window focus");yield break;}
            ApplyProfile(true);ChangeMode(false);SelectTrack(cameraTrack);MenuRoot.Q("workspace").Focus();
            try{
                foreach(var id in new[]{"00000411","00000409"}){
                    var layout=LoadKeyboardLayoutW(id,0);
                    if(layout==IntPtr.Zero){fail("FAIL unavailable layout "+id);yield break;}
                    PostMessageW(window,0x50,IntPtr.Zero,layout);end=Time.realtimeSinceStartupAsDouble+3;
                    while(ImeKeyboard.GetKeyboardLayout(thread)!=layout && Time.realtimeSinceStartupAsDouble<end)yield return null;
                    if(ImeKeyboard.GetKeyboardLayout(thread)!=layout){fail("FAIL language switch "+id);yield break;}
                    foreach(var vk in new[]{0xbb,0xba,0xdd,0xdc,0xe2,0xbe,0xbf}){
                        if(GetForegroundWindow()!=window){fail("FAIL keyboard focus changed");yield break;}
                        var scan=MapVirtualKeyExW((uint)vk,0,layout);var glyph=MapVirtualKeyExW((uint)vk,2,layout);
                        // A physical key press and release enter through the player's own window procedure.
                        int details=(int)scan<<16|1;
                        var expected=vk switch{0xbb=>KeyCode.Semicolon,0xba=>KeyCode.Colon,0xdd=>KeyCode.RightBracket,0xe2=>KeyCode.Backslash,
                            0xbe=>KeyCode.Period,0xbf=>KeyCode.Slash,_=>KeyCode.None};
                        PostMessageW(window,0x100,new IntPtr(vk),new IntPtr(details));
                        for(int i=0;i<4;i++)yield return null;
                        if(transformKey!=expected || (expected!=KeyCode.None && heldVirtual!=vk)){
                            fail("FAIL "+id+" VK "+vk+" expected "+expected+" got "+transformKey);yield break;}
                        PostMessageW(window,0x101,new IntPtr(vk),new IntPtr(details|unchecked((int)0xc0000000)));
                        for(int i=0;i<4;i++)yield return null;
                        if(transformKey!=KeyCode.None){fail("FAIL held after release "+vk);yield break;}
                        log.AppendLine(id+"  VK "+vk.ToString("X2")+"  scan "+scan.ToString("X2")+"  "+(char)glyph+"  "+expected);
                    }
                    if(id=="00000411"){
                        PostMessageW(window,0x100,new IntPtr(0xba),new IntPtr(0x280001));
                        for(int i=0;i<4;i++)yield return null;
                        if(transformKey!=KeyCode.Colon){fail("FAIL held key before language change");yield break;}
                        var other=LoadKeyboardLayoutW("00000409",0);
                        PostMessageW(window,0x50,IntPtr.Zero,other);
                        for(int i=0;i<4;i++)yield return null;
                        PostMessageW(window,0x101,new IntPtr(0xba),new IntPtr(unchecked((int)0xc0280001)));
                        for(int i=0;i<4;i++)yield return null;
                        if(transformKey!=KeyCode.None || pressedSymbols.Contains(0xba)){fail("FAIL language-change release");yield break;}
                        log.AppendLine("Held VK BA released after JIS -> US language change");
                    }
                }
            }finally{PostMessageW(window,0x50,IntPtr.Zero,old);if(transformKey!=KeyCode.None)FinishHeldTransform();}
            File.WriteAllText(Path.Combine(directory,"keyboard-layouts.txt"),log.ToString());
        }
        static class ImeKeyboard
        {
            [DllImport("user32")] internal static extern IntPtr GetKeyboardLayout(uint thread);
            [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
            [DllImport("user32")] internal static extern IntPtr GetFocus();
            [DllImport("user32")] internal static extern IntPtr GetAncestor(IntPtr child,uint flags);
            [DllImport("user32")] internal static extern bool SetForegroundWindow(IntPtr window);
            [DllImport("user32")] internal static extern IntPtr SetFocus(IntPtr child);
            [DllImport("user32")] internal static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
            [DllImport("imm32")] internal static extern IntPtr ImmGetContext(IntPtr child);
            [DllImport("imm32")] internal static extern bool ImmReleaseContext(IntPtr child,IntPtr context);
            [DllImport("imm32")] internal static extern bool ImmGetOpenStatus(IntPtr context);
            [DllImport("imm32")] internal static extern bool ImmSetOpenStatus(IntPtr context,bool open);
            [DllImport("imm32")] internal static extern bool ImmGetConversionStatus(IntPtr context,out uint conversion,out uint sentence);
            [DllImport("imm32")] internal static extern bool ImmSetConversionStatus(IntPtr context,uint conversion,uint sentence);
            [DllImport("imm32")] internal static extern uint ImmGetCandidateListW(IntPtr context,uint index,IntPtr buffer,uint size);
        }

        static IEnumerator CheckKeyboardConversion(FloatingPanel panel,TextField field,Action focusField,string dir,Action<string> fail)
        {
            string error=null;bool initialOpen=false,opened=false,haveMode=false,captured=false;uint mode=0,sentence=0,candidates=0;
            IEnumerator OnThread(Action<IntPtr> action){
                int done=0;
                panel.WithInputWindow(h=>{try{action(h);}catch(Exception ex){error=ex.Message;}finally{Interlocked.Exchange(ref done,1);}});
                var limit=Time.realtimeSinceStartupAsDouble+3;
                while(Volatile.Read(ref done)==0 && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(Volatile.Read(ref done)==0)error="操作パネルが応答しません。";
            }
            yield return OnThread(h=>{
                var top=ImeKeyboard.GetAncestor(h,2);ImeKeyboard.SetForegroundWindow(top);ImeKeyboard.SetFocus(h);
            });
            focusField();
            yield return new WaitForSecondsRealtime(.25f);
            yield return OnThread(h=>{
                var layout=ImeKeyboard.GetKeyboardLayout(0);
                if((layout.ToInt64()&65535)!=0x411){error="日本語入力のキーボードが必要です。";return;}
                var im=ImeKeyboard.ImmGetContext(h);
                if(im==IntPtr.Zero){error="IMEコンテキストがありません。";return;}
                try{
                    initialOpen=ImeKeyboard.ImmGetOpenStatus(im);captured=true;
                    haveMode=ImeKeyboard.ImmGetConversionStatus(im,out mode,out sentence);
                    ImeKeyboard.ImmSetOpenStatus(im,true);
                    ImeKeyboard.ImmSetConversionStatus(im,0x19,sentence);
                    opened=ImeKeyboard.ImmGetOpenStatus(im);
                }finally{ImeKeyboard.ImmReleaseContext(h,im);}
            });
            bool active=false;var deadline=Time.realtimeSinceStartupAsDouble+45;
            while(error==null && !active && Time.realtimeSinceStartupAsDouble<deadline){
                yield return OnThread(h=>active=ImeKeyboard.GetForegroundWindow()==ImeKeyboard.GetAncestor(h,2) && ImeKeyboard.GetFocus()==h);
                if(!active)yield return new WaitForSecondsRealtime(.15f);
            }
            bool composing=false;
            foreach(byte key in new byte[]{72,69,78,75,65,78,32,32,13}){
                if(error!=null)break;
                yield return OnThread(h=>{
                    var foreground=ImeKeyboard.GetForegroundWindow();var top=ImeKeyboard.GetAncestor(h,2);var focused=ImeKeyboard.GetFocus();
                    if(foreground!=top || focused!=h){
                        error="入力フォーカス "+focused+"/"+h+"、アクティブ窓 "+foreground+"/"+top;return;
                    }
                    var im=ImeKeyboard.ImmGetContext(h);
                    if(im!=IntPtr.Zero){candidates=Math.Max(candidates,ImeKeyboard.ImmGetCandidateListW(im,0,IntPtr.Zero,0));ImeKeyboard.ImmReleaseContext(h,im);}
                    ImeKeyboard.keybd_event(key,0,0,UIntPtr.Zero);ImeKeyboard.keybd_event(key,0,2,UIntPtr.Zero);
                });
                yield return new WaitForSecondsRealtime(.12f);
                composing|=panel.HasActiveInput;
            }
            yield return OnThread(h=>{
                if(!captured)return;
                var im=ImeKeyboard.ImmGetContext(h);if(im==IntPtr.Zero)return;
                if(haveMode)ImeKeyboard.ImmSetConversionStatus(im,mode,sentence);
                ImeKeyboard.ImmSetOpenStatus(im,initialOpen);ImeKeyboard.ImmReleaseContext(h,im);
            });
            string value=field.value;
            File.WriteAllText(Path.Combine(dir,"keyboard-ime.txt"),"IME "+opened+" / composition "+composing+" / candidates "+candidates+" / result "+value+" / "+error);
            if(error!=null || !composing || !value.Any(c=>c>=0x3400 && c<=0x9fff))
                fail("FAIL Japanese keyboard conversion: "+value+" / "+error);
        }

        IEnumerator CheckDetachedEditor(string dir,Action<string> verdict){
            bool prior=ribbonProfile;int pref=WorkspacePreferences.GetInt("Ribbon.OperationWindow");int at=frame;
            try{
                WorkspacePreferences.SetInt("Ribbon.OperationWindow",1);ApplyProfile(true);
                var limit=Time.realtimeSinceStartupAsDouble+12;
                while((operations?.Root==null || operations.PresentedFrames<2) && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(operations?.Root==null || operations.PresentedFrames<2){verdict("FAIL operation panel presentation");yield break;}
                var window=SmokeNativeWindow("操作パネル");var control=GetDlgItem(window,30);
                var field=operations.Root.Q<UnityEngine.UIElements.TextField>("float-memo");
                var button=operations.Root.Query<UnityEngine.UIElements.Button>().ToList().Find(b=>b.text=="ブックマーク登録");
                void Click(UnityEngine.UIElements.VisualElement element){
                    Vector2 pt=element.worldBound.center*(SmokeDpi(control)/96f);
                    var packed=new IntPtr(Mathf.RoundToInt(pt.x)+(Mathf.RoundToInt(pt.y)<<16));
                    PostMessageW(control,0x201,new IntPtr(1),packed);PostMessageW(control,0x202,IntPtr.Zero,packed);
                }
                Click(field.Q(className:"unity-text-field__input")??field);
                yield return null;yield return null;
                if(Environment.GetCommandLineArgs().Contains("-avaImeSmoke")){
                    string imeError=null;
                    yield return CheckKeyboardConversion(operations,field,()=>Click(field.Q(className:"unity-text-field__input")??field),dir,message=>imeError=message);
                    if(imeError!=null){verdict(imeError);yield break;}
                    field.value="";
                }
                PostMessageW(control,0x10d,IntPtr.Zero,IntPtr.Zero);
                limit=Time.realtimeSinceStartupAsDouble+3;while(!operations.HasActiveInput && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(!operations.HasActiveInput){verdict("FAIL composition start");yield break;}
                foreach(int vk in new[]{80,71,88,13}){
                    PostMessageW(control,0x100,new IntPtr(vk),IntPtr.Zero);PostMessageW(control,0x101,new IntPtr(vk),IntPtr.Zero);
                }
                yield return null;yield return null;
                if(field.value.Length!=0 || !operations.HasActiveInput || playing || frame!=at){verdict("FAIL IME shortcut isolation: "+field.value);yield break;}
                foreach(char letter in "編集")PostMessageW(control,0x286,new IntPtr(letter),IntPtr.Zero);
                PostMessageW(control,0x10e,IntPtr.Zero,IntPtr.Zero);
                PostMessageW(control,0x102,new IntPtr('A'),IntPtr.Zero);
                limit=Time.realtimeSinceStartupAsDouble+4;
                while(field.value!="編集A" && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(field.value!="編集A" || operations.HasActiveInput || playing || frame!=at){verdict("FAIL independent text input: "+field.value+" / "+operations.Root.focusController.focusedElement);yield break;}
                foreach(int vk in new[]{37,46}){
                    PostMessageW(control,0x100,new IntPtr(vk),IntPtr.Zero);PostMessageW(control,0x101,new IntPtr(vk),IntPtr.Zero);
                }
                PostMessageW(control,0x102,new IntPtr('B'),IntPtr.Zero);
                limit=Time.realtimeSinceStartupAsDouble+3;while(field.value!="編集B" && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(field.value!="編集B" || frame!=at){verdict("FAIL text selection or shortcut isolation: "+field.value);yield break;}
                Click(button);limit=Time.realtimeSinceStartupAsDouble+4;
                while(!bookmarkMemos.ContainsKey(frame) && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(!bookmarkMemos.TryGetValue(at,out string value) || value!="編集B"){verdict("FAIL panel command");yield break;}
                int initial=operations.Surface.width;SetWindowPos(window,IntPtr.Zero,0,0,initial+110,operations.Surface.height+110,0x16);
                limit=Time.realtimeSinceStartupAsDouble+4;while(operations.Surface.width==initial && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(operations.Surface.width==initial){verdict("FAIL floating panel resize");yield break;}
                int count=operations.PresentedFrames;double start=Time.realtimeSinceStartupAsDouble;
                while(Time.realtimeSinceStartupAsDouble-start<1)yield return null;
                var surface=operations.Surface;var active=RenderTexture.active;var capture=new Texture2D(surface.width,surface.height,TextureFormat.RGB24,false);
                try{RenderTexture.active=surface;capture.ReadPixels(new Rect(0,0,surface.width,surface.height),0,0,false);capture.Apply();
                    File.WriteAllBytes(Path.Combine(dir,"operation.png"),capture.EncodeToPNG());
                    var (same,flipped,total)=SampleWindow(control,capture);
                    double seconds=Time.realtimeSinceStartupAsDouble-start;
                    File.WriteAllText(Path.Combine(dir,"present.txt"),"DPI "+SmokeDpi(control)+" / pixels "+surface.width+"x"+surface.height+
                        " / presents "+(operations.PresentedFrames-count)+" / seconds "+seconds.ToString("F3")+" / matching "+same+" mirrored "+flipped+" sampled "+total);
                    if(total==0 || same<total*.8 || same<=flipped){verdict("FAIL Windows panel image: "+same+"/"+total+" mirrored "+flipped);yield break;}
                }finally{RenderTexture.active=active;Destroy(capture);}
                if(Environment.GetCommandLineArgs().Contains("-avaDpiSmoke")){
                    string scaleError=null;yield return CheckMonitorScale(operations,window,control,field,dir,error=>scaleError=error);
                    if(scaleError!=null){verdict(scaleError);yield break;}
                }
                ApplyProfile(false);yield return null;
                if(operations!=null || !bookmarkMemos.ContainsKey(at)){verdict("FAIL panel profile lifecycle");yield break;}
                ApplyHistory(false);
                if(bookmarkMemos.ContainsKey(at)){verdict("FAIL detached undo");yield break;}
            }finally{WorkspacePreferences.SetInt("Ribbon.OperationWindow",pref);ApplyProfile(prior);}
        }

        IEnumerator CheckDetachedPreview(string dir,Action<string> verdict)
        {
            bool prior=ribbonProfile;var marker=GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.layer=30;
            var material=new Material(marker.GetComponent<Renderer>().sharedMaterial){color=new Color(.95f,.05f,.25f)};
            marker.GetComponent<Renderer>().sharedMaterial=material;
            marker.transform.position=previewCamera.ViewportToWorldPoint(new Vector3(.35f,.72f,5));
            marker.transform.localScale=Vector3.one*Vector3.Distance(marker.transform.position,previewCamera.ViewportToWorldPoint(new Vector3(.35f,.55f,5)));
            try{
                previewCamera.Render();
                detached.Open(true);
                double limit=Time.realtimeSinceStartupAsDouble+5;
                while(detached.PresentedFrames<3 && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(detached.PresentedFrames<3){verdict("FAIL preview presentation");yield break;}
                var window=SmokeNativeWindow("AvaAvaDance プレビュー");var control=GetDlgItem(window,30);
                var source=previewTexture;var rt=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32);
                var capture=new Texture2D(source.width,source.height,TextureFormat.RGB24,false);var previous=RenderTexture.active;
                try{
                    RenderTexture.active=source;capture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false);capture.Apply();
                    File.WriteAllBytes(Path.Combine(dir,"camera-source.png"),capture.EncodeToPNG());
                    Graphics.Blit(source,rt);RenderTexture.active=rt;capture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false);capture.Apply();
                    File.WriteAllBytes(Path.Combine(dir,"preview.png"),capture.EncodeToPNG());
                    var (same,flipped,count)=SampleWindow(control,capture);
                    File.WriteAllText(Path.Combine(dir,"preview.txt"),"samples "+count+" matching "+same+" mirrored "+flipped+" MSAA "+source.antiAliasing);
                    if(count==0 || same<count*.80 || same<=flipped){verdict("FAIL preview pixels "+same+"/"+count+" mirrored "+flipped);yield break;}
                }finally{RenderTexture.active=previous;RenderTexture.ReleaseTemporary(rt);Destroy(capture);}
                int frames=detached.PresentedFrames;ApplyProfile(!prior);
                limit=Time.realtimeSinceStartupAsDouble+4;
                while(detached.PresentedFrames<=frames && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(!detached.Active || detached.PresentedFrames<=frames){verdict("FAIL shared preview lifetime");yield break;}
                PostMessageW(window,16,IntPtr.Zero,IntPtr.Zero);
                limit=Time.realtimeSinceStartupAsDouble+4;while(detached.Active && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                if(detached.Active)verdict("FAIL preview close");
            }finally{detached.Dispose();Destroy(marker);Destroy(material);ApplyProfile(prior);}
        }

        IEnumerator CheckExtensionWindows(string dir)
        {
            string result=null;bool baseline=Application.runInBackground;
            if(baseline)result="FAIL background setting";
            foreach(var probe in new[]{("ava.vmd-sizing.open","VMDサイジング"),("ava.mme.assign","AvaAvaEffect - エフェクトファイル割り当て")})
            {
                if(result!=null)break;
                if(!FindCommand(probe.Item1,out var id) || !Execute(id) || !Application.runInBackground){result="FAIL window activation "+probe.Item1;break;}
                double deadline=Time.realtimeSinceStartupAsDouble+8;IntPtr window;
                while((window=SmokeNativeWindow(probe.Item2))==IntPtr.Zero && Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                if(window==IntPtr.Zero){result="FAIL native window "+probe.Item2;break;}
                Execute(id);if(SmokeNativeWindow(probe.Item2)!=window){result="FAIL window reuse";break;}
                bool processed=false;extensions.First().Post(()=>processed=true);yield return null;yield return null;
                if(!processed){result="FAIL main queue";break;}
                PostMessageW(window,16,IntPtr.Zero,IntPtr.Zero);
                deadline=Time.realtimeSinceStartupAsDouble+8;
                while((activityCount>0 || SmokeNativeWindow(probe.Item2)!=IntPtr.Zero) && Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                if(activityCount!=0 || Application.runInBackground!=baseline)result="FAIL activity release";
            }
            if(result==null){
                bool profile=ribbonProfile;var savedTime=WorkspacePreferences.GetInt("Ribbon.TimeWindow");var savedAudio=WorkspacePreferences.GetInt("Ribbon.AudioWindow");
                int originalFrame=frame;
                try{
                    WorkspacePreferences.SetInt("Ribbon.TimeWindow",1);WorkspacePreferences.SetInt("Ribbon.AudioWindow",1);ApplyProfile(true);
                    double limit=Time.realtimeSinceStartupAsDouble+8;
                    IntPtr timeBoard,audioBoard;
                    while(((timeBoard=SmokeNativeWindow("タイムライン"))==IntPtr.Zero || SmokeNativeWindow("オーディオ")==IntPtr.Zero) && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                    audioBoard=SmokeNativeWindow("オーディオ");
                    if(timeBoard==IntPtr.Zero || audioBoard==IntPtr.Zero)result="FAIL independent panels";
                    else{
                        limit=Time.realtimeSinceStartupAsDouble+8;
                        while((floatingWave?.PresentedFrames<2 || floatingTime?.PresentedFrames<2) && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                        if(floatingTime?.PresentedFrames<2 || floatingWave?.PresentedFrames<2)result="FAIL independent panel rendering";
                        var control=GetDlgItem(audioBoard,30);
                        var point=(floatingWave.Board.worldBound.min+new Vector2(55,8))*(SmokeDpi(control)/96f);
                        var pos=new IntPtr(Mathf.RoundToInt(point.x)+(Mathf.RoundToInt(point.y)<<16));
                        PostMessageW(control,0x201,new IntPtr(1),pos);PostMessageW(control,0x202,IntPtr.Zero,pos);
                        limit=Time.realtimeSinceStartupAsDouble+6;
                        while(frame==originalFrame && Time.realtimeSinceStartupAsDouble<limit)yield return null;
                        if(frame==originalFrame)result="FAIL independent seek";
                    }
                }finally{
                    WorkspacePreferences.SetInt("Ribbon.TimeWindow",savedTime);WorkspacePreferences.SetInt("Ribbon.AudioWindow",savedAudio);ApplyProfile(profile);
                }
            }
            if(result==null)yield return StartCoroutine(CheckDetachedEditor(dir,error=>result=error));
            if(result==null)yield return StartCoroutine(CheckDetachedPreview(dir,error=>result=error));
            result??="PASS native windows, UI Toolkit input and presentation, shared Undo, IME routing and camera preview";
            SaveSmokeVerdict(dir,"extension-ui",null,result);Debug.Log(result);
            Application.runInBackground=true;Application.Quit(result.StartsWith("PASS")?0:1);
        }
    }
}
