using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        string RunEffectSmoke(string path,string dir)
        {
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
                string project=Path.Combine(dir,"assigned.avaproject");SaveProject(project);OpenProject(project);
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

        [DllImport("user32")]static extern bool PostMessageW(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
        [DllImport("user32",CharSet=CharSet.Unicode)]static extern IntPtr LoadKeyboardLayoutW(string name,uint flags);
        IEnumerator CheckSymbolLayouts(string directory,Action<string> fail)
        {
            using var process=System.Diagnostics.Process.GetCurrentProcess();
            var window=process.MainWindowHandle;
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
            [DllImport("user32")] internal static extern bool SetForegroundWindow(IntPtr window);
        }
    }
}
