using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    public static partial class EffectCompiler
    {
        static readonly Regex Include=new Regex("^\\s*#\\s*include\\s*[\"<]([^\">]+)[\">]",RegexOptions.Multiline);
        public static string Source(string path)=>Source(path,new List<string>());
        internal static string Source(string path,List<string> files,Func<string,byte[]> read=null)
        {
            var stack=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int bytes=0;string entryFolder=Path.GetDirectoryName(Path.GetFullPath(path));
            string Expand(string file)
            {
                file=Path.GetFullPath(file);
                if(stack.Count>=32 || !stack.Add(file))throw new InvalidDataException("includeが循環しています: "+file);
                try {
                    var data=read==null?File.ReadAllBytes(file):read(file);files.Add(file);bytes=checked(bytes+data.Length);
                    if(bytes>16*1024*1024)throw new InvalidDataException("FXソースのサイズ上限を超えました。");
                    string text;
                    try{text=new UTF8Encoding(false,true).GetString(data).TrimStart('\uFEFF');}
                    catch(DecoderFallbackException){text=MmdText.Decode(data);}
                    text=text.TrimEnd('\0');
                    string Label(string f)=>f.Replace('\\','/').Replace("\"","_");
                    return "#line 1 \""+Label(Path.GetFileName(file))+"\"\n"+Include.Replace(text,m=>{
                        string child=Path.Combine(Path.GetDirectoryName(file),m.Groups[1].Value);
                        int line=2+text.Take(m.Index+m.Length).Count(c=>c=='\n');
                        string nested=null;
                        foreach(var candidate in new[]{child,Path.Combine(entryFolder,m.Groups[1].Value)}.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)){
                            try{nested=Expand(candidate);break;}
                            catch(IOException ex) when(ex is FileNotFoundException || ex is DirectoryNotFoundException){}
                        }
                        nested??="\n#error Missing include: "+Label(m.Groups[1].Value)+"\n";
                        return nested+"\n#line "+line+" \""+Label(Path.GetFileName(file))+"\"";
                    });
                } finally {stack.Remove(file);}
            }
            return Expand(path);
        }

        sealed class Pass
        {
            public string vertex="AvaDefaultVS",pixel="AvaDefaultPS", name,script;
            public string vertexArgs,pixelArgs;
            public bool legacyVertex,legacyPixel;
            public bool blend,write=true,alphaTest;
            public int alphaFunction=8,alphaReference;
            public int cull;
            public readonly SurfaceStates states=new SurfaceStates();
        }
        static readonly Regex Lex=new Regex(@"//[^\n]*|/\*[\s\S]*?\*/|""(?:\\.|[^""\\])*""|[A-Za-z_]\w*|[0-9]+|[^\s]");
        // SM1–3 lower these intrinsics to instructions that take the input magnitude.
        const string LegacyMath=@"
#define AvaMath_sqrt(v) sqrt(abs(v))
#define AvaMath_rsqrt(v) rsqrt(abs(v))
#define AvaMath_log(v) log(abs(v))
#define AvaMath_log2(v) log2(abs(v))
#define AvaMath_log10(v) log10(abs(v))
#define AvaMath_pow(v,e) pow(abs(v),e)
";
        static readonly string ModernMath=@"
#define AvaMath_den(v) (v)
#define AvaMath_sqrt sqrt
#define AvaMath_rsqrt rsqrt
#define AvaMath_log log
#define AvaMath_log2 log2
#define AvaMath_log10 log10
#define AvaMath_pow pow
"+string.Join("\n",from rows in Enumerable.Range(2,3) from columns in Enumerable.Range(2,3)
    select "#define AvaMath_den"+rows+"x"+columns+"(v) (v)")+"\n";
        const string Fallback=@"
float4x4 AvaWvp,AvaWorld,AvaNormalMatrix; float4 AvaDiffuse,AvaLamp,AvaAmbient,AvaEmissive,AvaSpecular,AvaEye,AvaLightDir; float AvaGloss;
struct AvaIn { float4 p:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
struct AvaOut { float4 p:SV_Position; float2 uv:TEXCOORD0; float4 lit:COLOR0; };
AvaOut AvaDefaultVS(AvaIn v) {
    AvaOut o; o.p=mul(v.p,AvaWvp);o.uv=v.uv;
    float3 n=normalize(mul(float4(v.normal,0),AvaNormalMatrix).xyz);
    float3 light=normalize(-AvaLightDir.xyz);
    float3 eye=normalize(AvaEye.xyz-mul(v.p,AvaWorld).xyz);
    float spec=pow(saturate(dot(n,normalize(light+eye))),max(1,AvaGloss));
    o.lit=float4(AvaDiffuse.rgb*(AvaAmbient.rgb+AvaLamp.rgb*saturate(dot(n,light)))+AvaEmissive.rgb+AvaSpecular.rgb*AvaLamp.rgb*spec,AvaDiffuse.a);
    return o;
}
AvaOut AvaBufferVS(AvaIn v) { AvaOut o; o.p=v.p; o.uv=v.uv; o.lit=1; return o; }

";
        static string NormalizeSamplers(string hlsl,List<SurfaceSampler> samplers,List<SurfaceTexture> resources,StringBuilder bindings,Dictionary<string,string> declared)
        {
            // Translate legacy texture/sampler declarations before compiling each shader stage.
            for(int i=0;i<resources.Count;i++){
                var resource=resources[i];bindings.Append(resource.Name).Append(" AVATEXTURE").AppendLine(i.ToString());
                hlsl=Regex.Replace(hlsl,@"\btexture(?:2D|CUBE|3D)?\s+"+Regex.Escape(resource.Name)+@"\s*;",
                    "Texture"+resource.Dimension+" "+resource.Name+";",RegexOptions.IgnoreCase);
            }
            hlsl=Regex.Replace(hlsl,@"\b(?:sampler2D|samplerCUBE|sampler3D|sampler)\s+(\w+)\s*=\s*sampler_state\s*\{([^{}]*)\}\s*;",m=>{
                var texture=Regex.Match(m.Groups[2].Value,@"\bTexture\s*=\s*<?\s*(\w+)\s*>?\s*;",RegexOptions.IgnoreCase);
                if(!texture.Success)throw new FormatException("sampler_state にTexture指定がありません。");
                string sampler=m.Groups[1].Value;declared[sampler]=texture.Groups[1].Value;
                int slot=samplers.Count;samplers.Add(ParseSampler(m.Groups[2].Value));
                bindings.Append(sampler).Append(" AVASAMPLER").AppendLine(slot.ToString());
                return "SamplerState "+sampler+";";
            },RegexOptions.IgnoreCase);
            hlsl=SamplerArguments(hlsl,declared,out var localSamplers);
            return Regex.Replace(hlsl,@"\btex(?:2D|3D|CUBE)(lod|bias|proj|grad)?\s*\(\s*(\w+)\s*,",m=>{
                string sampler=m.Groups[2].Value;
                if(!declared.TryGetValue(sampler,out var texture)){
                    if(!localSamplers.Contains(sampler))return m.Value;
                    texture="AvaTex_"+sampler;
                }
                string mode=m.Groups[1].Value.ToLowerInvariant();
                if(mode=="lod" || mode=="bias" || mode=="proj")return "AvaSample"+mode+"("+texture+","+sampler+",";
                return "AvaSample"+mode+"("+texture+","+sampler+",";
            },RegexOptions.IgnoreCase);
        }
        const string Sampling=@"#define MME_MIPMAP 1
float2 AvaUv(float2 uv){return float2(uv.x,1-uv.y);}
float2 AvaDerivative(float2 d){return float2(d.x,-d.y);}
float4 AvaSample(Texture2D t,SamplerState s,float2 uv){return t.Sample(s,AvaUv(uv));}
float4 AvaSamplegrad(Texture2D t,SamplerState s,float2 uv,float2 dx,float2 dy){return t.SampleGrad(s,AvaUv(uv),AvaDerivative(dx),AvaDerivative(dy));}
float4 AvaSample(TextureCube t,SamplerState s,float3 uv){return t.Sample(s,uv);}
float4 AvaSamplegrad(TextureCube t,SamplerState s,float3 uv,float3 dx,float3 dy){return t.SampleGrad(s,uv,dx,dy);}
float4 AvaSample(Texture3D t,SamplerState s,float3 uv){return t.Sample(s,uv);}
float4 AvaSamplegrad(Texture3D t,SamplerState s,float3 uv,float3 dx,float3 dy){return t.SampleGrad(s,uv,dx,dy);}
float4 AvaSamplelod(Texture2D t, SamplerState s,float4 uv) { return t.SampleLevel(s,AvaUv(uv.xy),uv.w); }
float4 AvaSamplebias(Texture2D t, SamplerState s,float4 uv) { return t.SampleBias(s,AvaUv(uv.xy),uv.w); }
float4 AvaSampleproj(Texture2D t, SamplerState s,float4 uv) { return t.Sample(s,AvaUv(uv.xy/uv.w)); }
float4 AvaSamplelod(TextureCube t, SamplerState s,float4 uv) { return t.SampleLevel(s,uv.xyz,uv.w); }
float4 AvaSamplebias(TextureCube t, SamplerState s,float4 uv) { return t.SampleBias(s,uv.xyz,uv.w); }
float4 AvaSampleproj(TextureCube t, SamplerState s,float4 uv) { return t.Sample(s,uv.xyz/uv.w); }
float4 AvaSamplelod(Texture3D t, SamplerState s,float4 uv) { return t.SampleLevel(s,uv.xyz,uv.w); }
float4 AvaSamplebias(Texture3D t, SamplerState s,float4 uv) { return t.SampleBias(s,uv.xyz,uv.w); }
float4 AvaSampleproj(Texture3D t, SamplerState s,float4 uv) { return t.Sample(s,uv.xyz/uv.w); }
";
        public static SurfacePass[] Compile(string source)=>Compile(source,out _);
        public static SurfacePass[] Compile(string source,out SurfacePlan plan)=>Compile(source,null,out plan);
        internal static SurfacePass[] Compile(string source,MaterialSlot material,out SurfacePlan plan)
        {
            source=Preprocess(source);
            plan=new SurfacePlan();
            var tokens=Lex.Matches(source).Cast<Match>().Where(m=>!m.Value.StartsWith("//") && !m.Value.StartsWith("/*")).ToArray();
            var clean=source.ToCharArray();var semantics=new StringBuilder("AvaWvp WORLDVIEWPROJECTION\nAvaDiffuse DIFFUSE\n");
            semantics.Append("AvaWorld WORLD\nAvaNormalMatrix WORLDINVERSETRANSPOSE\nAvaLamp LIGHTDIFFUSE\nAvaAmbient AMBIENT\nAvaEmissive EMISSIVE\nAvaEye CAMERAPOSITION\nAvaLightDir LIGHTDIRECTION\nAvaGloss SPECULARPOWER\nAvaSpecular SPECULAR\n");
            var controls=new List<ControlVariable>();
            var scalarSources=new Dictionary<string,(string semantic,ControlVariable control)>(StringComparer.Ordinal);
            var techniques=new List<(string name,string tag,string script,TechniqueFilter filter,List<Pass> passes)>();
            var resources=new List<SurfaceTexture>();
            var values=InitialValues(source);
            string techniqueOrder=null;
            bool Is(int i,string value)=>i>=0 && i<tokens.Length && tokens[i].Value.Equals(value,StringComparison.OrdinalIgnoreCase);
            void Blank(int from,int to){for(int i=tokens[from].Index;i<tokens[to].Index+tokens[to].Length;i++)if(clean[i]!='\n' && clean[i]!='\r')clean[i]=' ';}
            int Find(int start,string symbol,int end){for(int i=start;i<end;i++)if(Is(i,symbol))return i;throw new FormatException(symbol+" がありません。");}
            int Close(int open,string a,string b)
            {
                int depth=1;
                for(int j=open+1;j<tokens.Length;j++){if(Is(j,a))depth++;if(Is(j,b) && --depth==0)return j;}
                throw new FormatException("閉じ記号 "+b+" がありません。");
            }
            int braces=0,paren=0;
            for(int i=0;i<tokens.Length;i++)
            {
                if(braces==0 && paren==0 && (Is(i,"texture") || Is(i,"texture2D") || Is(i,"textureCUBE") || Is(i,"texture3D")) && i+2<tokens.Length && !Is(i+2,":"))
                {
                    int end=Is(i+2,"<")?Close(i+2,"<",">"):i+2;
                    resources.Add(TextureDeclaration(source.Substring(tokens[i].Index,tokens[end].Index+tokens[end].Length-tokens[i].Index),
                        tokens[i+1].Value,"RESOURCE",i>0 && Is(i-1,"shared"),values));
                }
                if(braces==0 && (Is(i,"technique") || Is(i,"technique10")))
                {
                    int open=Find(i+1,"{",tokens.Length),end=Close(open,"{","}");string tag="";
                    for(int j=i+1;j+2<open;j++)if(Is(j,"MMDPass") && Is(j+1,"="))tag=tokens[j+2].Value.Trim('"');
                    var passScript=Annotation(source,tokens[i].Index,tokens[open].Index,"Script");
                    var passes=new List<Pass>();
                    for(int j=open+1;j<end;j++)
                    {
                        if(!Is(j,"pass"))continue;
                        int body=Find(j+1,"{",end),last=Close(body,"{","}");var pass=new Pass{name=tokens[j+1].Value,script=Annotation(source,tokens[j].Index,tokens[body].Index,"Script")};
                        for(int n=body+1;n+2<last;n++)
                        {
                            if(!Is(n+1,"="))continue;
                            if(Is(n,"VertexShader") || Is(n,"PixelShader"))
                            {
                                if(Is(n+2,"NULL"))continue;
                                if(!Is(n+2,"compile") || n+6>=last || !Is(n+5,"("))
                                    throw new FormatException("シェーダーのエントリ指定を読み取れません: "+tokens[n].Value);
                                int close=Close(n+5,"(",")");
                                string args=source.Substring(tokens[n+5].Index+1,tokens[close].Index-tokens[n+5].Index-1).Trim();
                                string profile=tokens[n+3].Value;
                                bool legacy=Regex.IsMatch(profile,@"^(?:vs|ps)_[123]_",RegexOptions.IgnoreCase);
                                if(Is(n,"VertexShader")){pass.vertex=tokens[n+4].Value;pass.vertexArgs=args;pass.legacyVertex=legacy;}
                                else{pass.pixel=tokens[n+4].Value;pass.pixelArgs=args;pass.legacyPixel=legacy;}n=close;
                            }
                            else if(Is(n,"AlphaBlendEnable"))pass.blend=Is(n+2,"true") || Is(n+2,"1");
                            else if(Is(n,"ZWriteEnable"))pass.write=!Is(n+2,"false") && !Is(n+2,"0");
                            else if(Is(n,"CullMode"))pass.cull=Is(n+2,"none")?1:Is(n+2,"cw")?2:3;
                            else {
                                int endValue=Find(n+2,";",last);
                                string literal=source.Substring(tokens[n+2].Index,tokens[endValue].Index-tokens[n+2].Index).Trim();
                                if(Is(n,"AlphaTestEnable"))pass.alphaTest=On(literal);
                                else if(Is(n,"AlphaFunc"))pass.alphaFunction=Comparison(literal);
                                else if(Is(n,"AlphaRef"))pass.alphaReference=Math.Min(255,Math.Max(0,StateValue(literal)));
                                else RenderState(pass.states,tokens[n].Value,literal);n=endValue;
                            }
                        }
                        passes.Add(pass);j=last;
                    }
                    techniques.Add((tokens[i+1].Value,tag,passScript,TechniqueFilter.Read(source.Substring(tokens[i].Index,tokens[open].Index-tokens[i].Index)),passes));Blank(i,end);i=end;continue;
                }
                if(Is(i,"{"))braces++;else if(Is(i,"}"))braces--;else if(Is(i,"("))paren++;else if(Is(i,")"))paren--;
                else if(braces==0 && paren==0 && i>0 && Is(i,":") && i+1<tokens.Length && !Is(i-1,")") && !Is(i+1,"register"))
                {
                    string semantic=tokens[i+1].Value.ToUpperInvariant();string variable=tokens[i-1].Value;
                    if(semantic=="TEXTUREVALUE"){
                        int count=1;
                        if(Is(i-1,"]") && i>=5 && Is(i-3,"[")){
                            if(!int.TryParse(tokens[i-2].Value,out count) || count<1 || count>4096)throw new FormatException("TEXTUREVALUEの要素数が不正です。");
                            variable=tokens[i-4].Value;
                        }
                        if(!Is(i+2,"<"))throw new FormatException("TextureNameがありません。");
                        string name=Annotation(source,tokens[i+2].Index,tokens[Close(i+2,"<",">")].Index,"TextureName");
                        if(string.IsNullOrEmpty(name))throw new FormatException("TextureNameがありません。");
                        semantic="AVATEXELS"+controls.Count;
                        controls.Add(new ControlVariable{Texture=name,Texels=count});
                        for(int j=1;j<(count+3)/4;j++)controls.Add(null);
                    }
                    if(i>=2 && tokens[i-2].Value.StartsWith("texture",StringComparison.OrdinalIgnoreCase))
                    {
                        int at=i+2,end=Is(at,"<")?Close(at,"<",">"):at;
                        resources.Add(TextureDeclaration(source.Substring(tokens[i-2].Index,tokens[end].Index+tokens[end].Length-tokens[i-2].Index),
                            tokens[i-1].Value,semantic,i>=3 && Is(i-3,"shared"),values));
                    }
                    if(semantic=="STANDARDSGLOBAL" && Is(i+2,"<"))
                    {
                        int begin=tokens[i+2].Index,last=tokens[Close(i+2,"<",">")].Index;
                        plan.Order=(Annotation(source,begin,last,"ScriptOrder")??"standard").ToLowerInvariant();
                        plan.Class=(Annotation(source,begin,last,"ScriptClass")??"object").ToLowerInvariant();
                        techniqueOrder=Annotation(source,begin,last,"Script");
                    }
                    ControlVariable control=semantic=="CONTROLOBJECT"?new ControlVariable{Type=tokens[i-2].Value}:null;
                    if(Is(i+2,"<")){
                        int end=Close(i+2,"<",">");
                        for(int n=i+3;n+2<end;n++){
                            if(control!=null && Is(n+1,"=")){if(Is(n,"name"))control.Object=tokens[n+2].Value.Trim('\"');if(Is(n,"item"))control.Item=tokens[n+2].Value.Trim('\"');}
                            if(Is(n,"Object") && Is(n+1,"=")){
                                string owner=tokens[n+2].Value.Trim('"').ToUpperInvariant();
                                if(owner=="LIGHT" || owner=="CAMERA")semantic=owner+semantic;
                            }
                            if(Is(n,"SyncInEditMode") && Is(n+1,"=") && (Is(n+2,"true") || Is(n+2,"1")))semantic="SYNC"+semantic;
                        }
                    }
                    if(tokens[i-2].Value.Equals("float",StringComparison.OrdinalIgnoreCase))scalarSources[variable]=(semantic,control);
                    if(control!=null){semantic="AVACONTROL"+controls.Count;controls.Add(control);}
                    semantics.Append(variable).Append(' ').AppendLine(semantic);Blank(i,i+1);i++;
                }
                else if(braces==0 && paren==0 && Is(i,"<") && i>0 && !Is(i-1,"="))
                {
                    int end=Close(i,"<",">");if(Is(end+1,";") || Is(end+1,"=")){Blank(i,end);i=end;}
                }
            }
            plan.Roles=techniques.Select(t=>t.tag.Length==0?"object":t.tag.ToLowerInvariant()).Distinct().ToArray();
            plan.SubsetSensitive=techniques.Any(t=>t.filter.HasSubset);
            plan.SelectPerMaterial=techniques.Any(t=>t.filter.Conditional || !string.IsNullOrEmpty(t.tag) && !t.tag.Equals("object",StringComparison.OrdinalIgnoreCase));
            string role=material?.DrawRole??(material?.SelfShadow==true && techniques.Any(t=>t.tag.Equals("object_ss",StringComparison.OrdinalIgnoreCase))?"object_ss":"object");
            var candidates=material==null?techniques:techniques.Where(t=>string.Equals(t.tag.Length==0?"object":t.tag,role,StringComparison.OrdinalIgnoreCase) && t.filter.Accepts(material)).ToList();
            var selected=candidates.FirstOrDefault();
            if(techniqueOrder!=null){
                var match=Regex.Match(techniqueOrder,@"Technique\s*=\s*([^;]+)",RegexOptions.IgnoreCase);
                if(match.Success)foreach(var name in match.Groups[1].Value.Split('?',':')){
                    var option=candidates.Find(t=>t.name==name.Trim());if(option.passes!=null){selected=option;break;}
                }
            }
            if(selected.passes==null){
                if(material==null)throw new InvalidDataException("描画用のtechniqueがありません。");
                if(material.DrawRole!=null)return null;
                var roles=plan.Roles;
                var basic=Compile("technique Basic {pass Surface {}}",null,out plan);plan.Roles=roles;return basic;
            }
            foreach(var texture in resources.Where(t=>!string.IsNullOrEmpty(t.SeekVariable))){
                if(scalarSources.TryGetValue(texture.SeekVariable,out var scalar)){
                    texture.SeekSemantic=scalar.semantic;texture.SeekControl=scalar.control;
                }else if(values.TryGetValue(texture.SeekVariable,out var literal) && literal.Length>0)texture.SeekDefault=literal[0];
                else if(!Regex.IsMatch(source,@"\bfloat\s+"+Regex.Escape(texture.SeekVariable)+@"\s*[;<:=]"))
                    throw new FormatException("アニメーション時刻のパラメータが見つかりません。"+texture.SeekVariable);
            }
            bool needsDefaultPixel=selected.passes.Any(p=>p.pixel=="AvaDefaultPS");
            var diffuse=resources.Find(t=>t.Semantic=="MATERIALTEXTURE");
            if(needsDefaultPixel && diffuse==null){
                diffuse=new SurfaceTexture{Name="AvaBaseTexture",Semantic="MATERIALTEXTURE"};
                resources.Add(diffuse);
            }
            plan.Textures=resources.ToArray();
            plan.Steps=ScriptSteps(plan,selected.script,selected.passes,values);
            foreach(var id in plan.Steps.Where(s=>s.Operation==SurfaceOperation.Parameter).Select(s=>s.Name).Distinct())
            {
                semantics.Append(id).Append(" AVACONTROL").AppendLine(controls.Count.ToString());
                var m=Regex.Match(source,@"\b(int|bool|float)\s+"+Regex.Escape(id)+@"\b");
                controls.Add(new ControlVariable{Type=m.Success?m.Groups[1].Value:"float",Parameter=id});
            }
            if(controls.Count>1024)throw new InvalidDataException("シェーダー定数が上限を超えています。");
            var samplers=new List<SurfaceSampler>();
            var samplerTextures=new Dictionary<string,string>(StringComparer.Ordinal);
            string hlsl=Sampling+NormalizeSamplers(new string(clean),samplers,resources,semantics,samplerTextures)+Fallback;
            if(needsDefaultPixel){
                if(diffuse.Name=="AvaBaseTexture")hlsl+="\nTexture2D AvaBaseTexture;";
                string sampler="AvaBaseSampler";hlsl+="\nSamplerState "+sampler+";";
                semantics.Append(sampler).Append(" AVASAMPLER").AppendLine(samplers.Count.ToString());
                samplers.Add(new SurfaceSampler());
                hlsl+="\nbool AvaHasBase; float4 AvaBase(float2 uv){return AvaHasBase?"+diffuse.Name+".Sample("+sampler+",AvaUv(uv)):float4(1,1,1,1);}";
                hlsl+="\nfloat4 AvaDefaultPS(AvaOut v):SV_Target{return v.lit*AvaBase(v.uv);}";
                hlsl+="\nfloat4 AvaMaterialPS(float2 uv:TEXCOORD0):SV_Target{return AvaDiffuse*AvaBase(uv);}";
                hlsl+="\nfloat4 AvaBorderColor,AvaGroundColor;float4 AvaBorderPS():SV_Target{return AvaBorderColor;}";
                hlsl+="\nfloat4 AvaGroundPS():SV_Target{return AvaGroundColor;}";
                hlsl+="\nfloat4 AvaDepthPS(float4 p:SV_POSITION):SV_Target{return (1-p.z).xxxx;}";
                semantics.AppendLine("AvaBorderColor EDGECOLOR");semantics.AppendLine("AvaGroundColor GROUNDSHADOWCOLOR");
                semantics.AppendLine("AvaHasBase use_texture");
            }
            string legacyHlsl=selected.passes.Any(p=>p.legacyVertex || p.legacyPixel)
                ?ArithmeticSource(hlsl):null;
            var result=new List<SurfacePass>();
            try {
                for(int i=0;i<selected.passes.Count;i++){
                    var p=selected.passes[i];
                    bool buffer=plan.Steps.Any(step=>step.Pass==i && step.Operation==SurfaceOperation.Buffer);
                    var vertex=p.vertex=="AvaDefaultVS" && buffer?"AvaBufferVS":p.vertex;
                    var unit=new StringBuilder();
                    if(p.legacyVertex || p.legacyPixel){
                        if(p.legacyVertex!=p.legacyPixel)unit.AppendLine(p.legacyVertex?"#ifdef AVA_VERTEX_STAGE":"#ifndef AVA_VERTEX_STAGE");
                        unit.Append(Denominators).Append(LegacyMath);
                        if(p.legacyVertex!=p.legacyPixel)unit.AppendLine("#else").Append(ModernMath).AppendLine("#endif");
                        unit.Append(legacyHlsl);
                    }else unit.Append(hlsl);
                    vertex=Specialize(unit,vertex,ShaderArguments(p.vertexArgs,samplerTextures),"AvaVertexEntry");
                    string pixelEntry=p.pixel;
                    if(pixelEntry=="AvaDefaultPS")pixelEntry=role switch {
                        "edge"=>"AvaBorderPS","zplot"=>"AvaDepthPS","shadow"=>"AvaGroundPS",
                        _=>p.vertex=="AvaDefaultVS"?"AvaDefaultPS":"AvaMaterialPS"};
                    var pixel=Specialize(unit,pixelEntry,ShaderArguments(p.pixelArgs,samplerTextures),"AvaPixelEntry");
                    if(p.alphaTest && p.alphaFunction!=8)pixel=WithAlphaTest(unit,pixel,p.alphaFunction,p.alphaReference);
                    SurfacePass compiled;
                    try{compiled=new SurfacePass(unit.ToString(),vertex,pixel,semantics.ToString(),p.blend,p.write && !buffer,p.cull){Controls=controls.ToArray()};}
                    catch(InvalidOperationException ex){throw new InvalidOperationException(selected.name+" / "+p.name+" ("+vertex+", "+pixel+")\n"+ex.Message,ex);}
                    result.Add(compiled);compiled.Configure(p.states,samplers.ToArray());
                }
                return result.ToArray();
            }catch{foreach(var pass in result)pass.Dispose();throw;}
        }
    }
}
