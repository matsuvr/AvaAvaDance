using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    public static partial class EffectCompiler
    {
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr ava_fx_preprocess(byte[] source,int size,out int length,StringBuilder error,int capacity);
        static string Preprocess(string source)
        {
            var error=new StringBuilder(8192);var bytes=Encoding.UTF8.GetBytes(source);
            var pointer=ava_fx_preprocess(bytes,bytes.Length,out int size,error,error.Capacity);
            if(pointer==IntPtr.Zero)throw new FormatException(error.ToString());
            try{bytes=new byte[size];Marshal.Copy(pointer,bytes,0,size);return Encoding.UTF8.GetString(bytes).TrimEnd('\0');}
            finally{Marshal.FreeCoTaskMem(pointer);}
        }
        static string Annotation(string text,int first,int last,string name)
        {
            var pattern=@"\b"+Regex.Escape(name)+@"\s*=\s*((?:""[^""]*""\s*)+)";
            var declaration=Regex.Replace(text.Substring(first,last-first),@"(?m)^[ \t]*#[ \t]*line[^\r\n]*","");
            var match=Regex.Match(declaration,pattern,RegexOptions.IgnoreCase);
            return match.Success?string.Concat(Regex.Matches(match.Groups[1].Value,@"""([^""]*)""").Cast<Match>().Select(m=>m.Groups[1].Value)):null;
        }
        static Dictionary<string,float[]> InitialValues(string hlsl)
        {
            var map=new Dictionary<string,float[]>(StringComparer.Ordinal);
            foreach(Match m in Regex.Matches(hlsl,@"\b(?:float[1-4]?|int|bool)\s+(\w+)\s*=\s*(?:float[1-4]?\s*)?([^;]+);"))
            {
                var literal=m.Groups[2].Value.Trim();
                if(TryConstant(literal,map,out var computed)){map[m.Groups[1].Value]=computed;continue;}
                if(literal=="true" || literal=="false")map[m.Groups[1].Value]=new[]{literal=="true"?1f:0};

            }
            return map;
        }
        static SurfaceTexture TextureDeclaration(string text,string name,string semantic,bool shared,Dictionary<string,float[]> constants)
        {
            var target=new SurfaceTexture{Name=name,Semantic=semantic,Shared=shared,Format=Annotation(text,0,text.Length,"Format"),
                Resource=Annotation(text,0,text.Length,"ResourceName")};
            var dimension=Annotation(text,0,text.Length,"ResourceType")??Regex.Match(text,@"^texture(2D|3D|CUBE)",RegexOptions.IgnoreCase).Groups[1].Value;
            if(dimension.Equals("Cube",StringComparison.OrdinalIgnoreCase))target.Dimension="Cube";
            else if(dimension.Equals("3D",StringComparison.OrdinalIgnoreCase) || dimension.Equals("Volume",StringComparison.OrdinalIgnoreCase))target.Dimension="3D";
            float[] Values(string property){
                var match=Regex.Match(text,@"\b"+property+@"\s*=\s*(?:float[234]|int[234])?\s*([^;]+);",RegexOptions.IgnoreCase);
                return match.Success && TryConstant(match.Groups[1].Value.Trim(),constants,out var value)?value:Array.Empty<float>();
            }
            target.SeekVariable=Annotation(text,0,text.Length,"SeekVariable");var offset=Values("Offset");var speed=Values("Speed");
            if(offset.Length>0)target.Offset=offset[0];if(speed.Length>0)target.Speed=speed[0];
            target.AntiAlias=Regex.IsMatch(text,@"\bAntiAlias\s*=\s*(?:true|1)\s*;",RegexOptions.IgnoreCase);
            target.Description=Annotation(text,0,text.Length,"Description");target.DefaultEffect=Annotation(text,0,text.Length,"DefaultEffect");
            target.ClearColor=Values("ClearColor");var depth=Values("ClearDepth");if(depth.Length>0)target.ClearDepth=depth[0];
            var ratio=Values("ViewportRatio");if(ratio.Length>=2){target.RatioX=ratio[0];target.RatioY=ratio[1];}
            var dims=Values("Dimensions");if(dims.Length>=2){target.Width=(int)dims[0];target.Height=(int)dims[1];}
            var w=Values("Width");var h=Values("Height");if(w.Length>0)target.Width=(int)w[0];if(h.Length>0)target.Height=(int)h[0];
            var levels=Values("Miplevels");if(levels.Length==0)levels=Values("Levels");
            target.Levels=levels.Length>0?(int)levels[0]:semantic.Contains("TARGET")?1:0;
            return target;
        }
        readonly struct Instruction
        {
            public readonly string key,value;
            public Instruction(string text)
            {
                int sep=text.IndexOf('=');
                if(sep<0)throw new FormatException("Script命令に = がありません: "+text);
                key=text.Substring(0,sep).Trim().ToUpperInvariant();value=text.Substring(sep+1).Trim();
            }
        }
        static Instruction[] Instructions(string script)=>(script??"").Split(';').Select(s=>s.Trim()).Where(s=>s.Length>0).Select(s=>new Instruction(s)).ToArray();
        static SurfaceStep[] ScriptSteps(SurfacePlan plan,string script,List<Pass> passes,Dictionary<string,float[]> values)
        {
            var steps=new List<SurfaceStep>();
            var color=new[]{0f,0f,0f,0f};float depth=1,stencil=0;
            float[] Resolve(string name)
            {
                if(values.TryGetValue(name,out var value) && value.Length>0)return value;
                if(float.TryParse(name,NumberStyles.Float,CultureInfo.InvariantCulture,out float literal))return new[]{literal};
                throw new FormatException("Scriptの値を解決できません: "+name);
            }
            void Emit(SurfaceOperation op,int pass=-1,int slot=0,string name=null,float[] value=null)
            {
                if(steps.Count>=65536)throw new FormatException("Scriptの描画命令が上限を超えています。");
                steps.Add(new SurfaceStep {Operation=op,Pass=pass,Slot=slot,Name=name,Value=value});
            }
            int iterations=0,external=0;
            void Run(Instruction[] code,int start,int end,int pass,int? iteration)
            {
                for(int i=start;i<end;i++)
                {
                    if(++iterations>100000)throw new FormatException("Scriptの反復が上限を超えています。");
                    var op=code[i];var name=op.value;
                    switch(op.key)
                    {
                        case "RENDERCOLORTARGET":case "RENDERCOLORTARGET0":case "RENDERCOLORTARGET1":case "RENDERCOLORTARGET2":case "RENDERCOLORTARGET3":
                            Emit(SurfaceOperation.Target,slot:char.IsDigit(op.key.Last())?op.key.Last()-'0':0,name:name);break;
                        case "RENDERDEPTHSTENCILTARGET":Emit(SurfaceOperation.Depth,name:name);break;
                        case "CLEARSETCOLOR":color=Resolve(name);if(color.Length==1)color=Enumerable.Repeat(color[0],4).ToArray();if(color.Length!=4)throw new FormatException("ClearSetColorは4成分です。");break;
                        case "CLEARSETDEPTH":depth=Resolve(name)[0];break;
                        case "CLEARSETSTENCIL":stencil=Resolve(name)[0];break;
                        case "CLEAR":
                            if(name.Equals("Color",StringComparison.OrdinalIgnoreCase))Emit(SurfaceOperation.ClearColor,value:color);
                            else if(name.Equals("Depth",StringComparison.OrdinalIgnoreCase))Emit(SurfaceOperation.ClearDepth,value:new[]{depth});
                            else if(name.Equals("Stencil",StringComparison.OrdinalIgnoreCase))Emit(SurfaceOperation.ClearStencil,value:new[]{stencil});
                            else throw new FormatException("Clear対象: "+name);break;
                        case "PASS":
                            if(pass>=0)throw new FormatException("Pass命令の位置が不正です。");
                            int index=passes.FindIndex(p=>p.name==name);if(index<0)throw new FormatException("Passがありません: "+name);
                            DrawPass(index,iteration);break;
                        case "DRAW":
                            if(pass<0)throw new FormatException("Draw命令にはpassが必要です。");
                            if(name.Equals("Buffer",StringComparison.OrdinalIgnoreCase)){if(plan.Class!="object")Emit(SurfaceOperation.Buffer,pass);}
                            else if(name.Equals("Geometry",StringComparison.OrdinalIgnoreCase)){if(plan.Class!="scene")Emit(SurfaceOperation.Geometry,pass);}
                            else throw new FormatException("Draw対象: "+name);break;
                        case "LOOPBYCOUNT":
                            if(pass>=0)throw new FormatException("LoopByCountにはtechniqueが必要です。");
                            int last=i+1,level=1;
                            for(;last<end;last++){if(code[last].key=="LOOPBYCOUNT")level++;if(code[last].key=="LOOPEND" && --level==0)break;}
                            if(last>=end)throw new FormatException("LoopEndがありません。");
                            float count=Resolve(name)[0];if(!(count>=0 && count<=65536))throw new FormatException("ループ回数の範囲外です。");
                            for(int n=0;n<(int)count;n++)Run(code,i+1,last,pass,n);
                            i=last;break;
                        case "LOOPGETINDEX":
                            if(!iteration.HasValue)throw new FormatException("ループ外のLoopGetIndexです。");
                            values[name]=new[]{(float)iteration.Value};
                            Emit(SurfaceOperation.Parameter,name:name,value:values[name]);break;
                        case "SCRIPTEXTERNAL":
                            if(pass>=0 || plan.Order!="postprocess" || !name.Equals("Color",StringComparison.OrdinalIgnoreCase) || ++external!=1)
                                throw new FormatException("ScriptExternalの位置または対象が不正です。");
                            Emit(SurfaceOperation.SceneColor);break;
                        default:throw new FormatException("未対応のScript命令: "+op.key);
                    }
                }
            }
            void DrawPass(int pass,int? iteration)
            {
                var code=Instructions(passes[pass].script);
                if(code.Length>0)Run(code,0,code.Length,pass,iteration);
                else Emit(plan.Class=="scene"?SurfaceOperation.Buffer:SurfaceOperation.Geometry,pass);
            }
            if(script==null)for(int i=0;i<passes.Count;i++)DrawPass(i,null);
            else {var code=Instructions(script);Run(code,0,code.Length,-1,null);}
            foreach(var step in steps.Where(s=>(s.Operation==SurfaceOperation.Depth || s.Operation==SurfaceOperation.Target) && !string.IsNullOrEmpty(s.Name)))
                if(!plan.Textures.Any(t=>t.Name==step.Name && t.Semantic.Contains("TARGET")))throw new FormatException("描画先がありません: "+step.Name);
            if(plan.Order=="postprocess" && external!=1)throw new FormatException("ポスト処理にはScriptExternal=Colorが必要です。");
            return steps.ToArray();
        }
    }
}
