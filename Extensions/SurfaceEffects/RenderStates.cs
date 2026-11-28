using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    public static partial class EffectCompiler
    {
        static int StateValue(string text,params string[] values)
        {
            if(int.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out int value))return value;
            if(text.StartsWith("0x",StringComparison.OrdinalIgnoreCase))return Convert.ToInt32(text.Substring(2),16);
            int index=Array.FindIndex(values,v=>string.Equals(v,text,StringComparison.OrdinalIgnoreCase));
            if(index<0)throw new FormatException("状態の値を解釈できません: "+text);
            return index+1;
        }
        static int Comparison(string v)=>StateValue(v,"Never","Less","Equal","LessEqual","Greater","NotEqual","GreaterEqual","Always");
        static int Blend(string v)=>StateValue(v,"Zero","One","SrcColor","InvSrcColor","SrcAlpha","InvSrcAlpha","DestAlpha","InvDestAlpha",
            "DestColor","InvDestColor","SrcAlphaSat","","","BlendFactor","InvBlendFactor");
        static int BlendOp(string v)=>StateValue(v,"Add","Subtract","RevSubtract","Min","Max");
        static int Stencil(string v)=>StateValue(v,"Keep","Zero","Replace","IncrSat","DecrSat","Invert","Incr","Decr");
        static bool On(string v)=>v=="1" || v.Equals("true",StringComparison.OrdinalIgnoreCase);
        static void RenderState(SurfaceStates state,string name,string value)
        {
            switch(name.ToUpperInvariant()){
                case "SRCBLEND":state.SrcBlend=Blend(value);break;
                case "DESTBLEND":state.DestBlend=Blend(value);break;
                case "BLENDOP":state.BlendOp=BlendOp(value);break;
                case "SRCBLENDALPHA":state.SrcBlendAlpha=Blend(value);break;
                case "DESTBLENDALPHA":state.DestBlendAlpha=Blend(value);break;
                case "BLENDOPALPHA":state.BlendOpAlpha=BlendOp(value);break;
                case "ZENABLE":state.DepthEnabled=On(value);break;
                case "ZFUNC":int z=Comparison(value);state.DepthComparison=z==2?5:z==4?7:z==5?2:z==7?4:z;break;
                case "COLORWRITEENABLE":state.WriteMask=value.Equals("false",StringComparison.OrdinalIgnoreCase)?0:value.Equals("true",StringComparison.OrdinalIgnoreCase)?15:StateValue(value);break;
                case "FILLMODE":state.FillMode=value.Equals("wireframe",StringComparison.OrdinalIgnoreCase)?2:3;break;
                case "STENCILENABLE":state.StencilEnabled=On(value);break;
                case "STENCILFUNC":state.StencilComparison=Comparison(value);break;
                case "STENCILPASS":state.StencilPass=Stencil(value);break;
                case "STENCILFAIL":state.StencilFail=Stencil(value);break;
                case "STENCILZFAIL":state.StencilDepthFail=Stencil(value);break;
                case "STENCILREF":state.StencilReference=StateValue(value);break;
                case "STENCILMASK":state.StencilReadMask=StateValue(value)&255;break;
                case "STENCILWRITEMASK":state.StencilWriteMask=StateValue(value)&255;break;
            }
        }
        static SurfaceSampler ParseSampler(string fields)
        {
            var result=new SurfaceSampler();bool min=true,mag=true,mip=true,anisotropic=false;
            foreach(Match match in Regex.Matches(fields,@"(\w+)\s*=\s*([^;]+)\s*;")){
                string key=match.Groups[1].Value.ToUpperInvariant(),v=match.Groups[2].Value.Trim();
                bool linear=!v.Equals("point",StringComparison.OrdinalIgnoreCase) && !v.Equals("none",StringComparison.OrdinalIgnoreCase);
                if(key=="MINFILTER" || key=="MAGFILTER")anisotropic|=v.Equals("anisotropic",StringComparison.OrdinalIgnoreCase);
                switch(key){
                    case "MINFILTER":min=linear;break;case "MAGFILTER":mag=linear;break;case "MIPFILTER":mip=linear;break;
                    case "ADDRESSU":case "ADDRESSV":case "ADDRESSW":
                        int address=StateValue(v,"Wrap","Mirror","Clamp","Border","MirrorOnce");
                        if(key=="ADDRESSU")result.AddressU=address;else if(key=="ADDRESSV")result.AddressV=address;else result.AddressW=address;
                        break;
                    case "MAXANISOTROPY":result.Anisotropy=Math.Min(16,Math.Max(1,StateValue(v)));break;
                    case "MIPMAPLODBIAS":result.MipBias=float.Parse(v,CultureInfo.InvariantCulture);break;
                }
            }
            result.Filter=anisotropic?85:(min?16:0)|(mag?4:0)|(mip?1:0);
            return result;
        }
    }
}
