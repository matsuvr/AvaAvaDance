using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ava.SurfaceEffects
{
    public static partial class EffectCompiler
    {
        static List<string> Arguments(string text)
        {
            int start=0,depth=0;var args=new List<string>();
            for(int i=0;i<text.Length;i++)
            {
                switch(text[i]){
                    case '(':case '[':case '{':depth++;break;
                    case ')':case ']':case '}':depth--;break;
                    case ',':if(depth==0){args.Add(text.Substring(start,i-start).Trim());start=i+1;}break;
                }
            }
            if(start<text.Length)args.Add(text.Substring(start).Trim());
            return args;
        }
        static string WithAlphaTest(StringBuilder hlsl,string entry,int comparison,int reference)
        {
            string source=hlsl.ToString();
            var fn=Regex.Match(source,@"\b(?<type>\w+)\s+"+Regex.Escape(entry)+@"\s*\((?<args>[^()]*)\)\s*(?<result>:\s*\w+)?\s*\{");
            if(!fn.Success)throw new FormatException("アルファテストの入力を解決できません。"+entry);
            var args=Arguments(fn.Groups["args"].Value);var names=new List<string>();string alpha="1.0";
            const string color=@"\s*:\s*(?:COLOR|SV_TARGET)0?\b";
            foreach(string arg in args){
                var varName=Regex.Match(arg,@"\b(\w+)\s*(?:\[[^\]]+\])?\s*(?::\s*\w+)?\s*$");
                if(!varName.Success)throw new FormatException("シェーダー引数を解決できません。"+arg);
                names.Add(varName.Groups[1].Value);
                if(Regex.IsMatch(arg,@"\bout\s+(?:float|half|fixed)4\b",RegexOptions.IgnoreCase) && Regex.IsMatch(arg,color,RegexOptions.IgnoreCase))
                    alpha=varName.Groups[1].Value+".a";
            }
            string type=fn.Groups["type"].Value,result=fn.Groups["result"].Value;
            if(Regex.IsMatch(type,@"^(?:float|half|fixed)4$") && Regex.IsMatch(result,color,RegexOptions.IgnoreCase))alpha="value.a";
            else if(type!="void"){
                var fields=Regex.Match(source,@"\bstruct\s+"+Regex.Escape(type)+@"\s*\{([^}]*)\}").Groups[1].Value;
                var target=Regex.Match(fields,@"\b(?:float|half|fixed)4\s+(\w+)"+color,RegexOptions.IgnoreCase);
                if(target.Success)alpha="value."+target.Groups[1].Value+".a";
            }
            const string wrapper="AvaAlphaTestPS";
            hlsl.AppendLine().Append(type).Append(' ').Append(wrapper).Append('(').Append(string.Join(",",args)).Append(')').Append(result).Append(" { ");
            if(type!="void")hlsl.Append(type).Append(" value=");
            hlsl.Append(entry).Append('(').Append(string.Join(",",names)).Append("); ");
            string op=comparison switch {2=>"<",3=>"==",4=>"<=",5=>">",6=>"!=",7=>">=",1=>null,_=>throw new FormatException("アルファの比較関数が不正です。")};
            if(op==null)hlsl.Append("clip(-1); ");
            else hlsl.Append("clip(round(255*saturate(").Append(alpha).Append("))").Append(op).Append(reference).Append(" ? 1 : -1); ");
            if(type!="void")hlsl.Append("return value; ");hlsl.AppendLine("}");
            return wrapper;
        }
        static string Specialize(StringBuilder hlsl,string entry,string parameters,string wrapper)
        {
            if(string.IsNullOrEmpty(parameters))return entry;
            var source=hlsl.ToString();
            var pattern=@"\b(?<type>\w+)\s+"+Regex.Escape(entry)+@"\s*\((?<args>[^()]*)\)\s*(?<result>:\s*\w+)?\s*\{";
            var definitions=Regex.Matches(source,pattern).Cast<Match>();
            var constants=Arguments(parameters);
            foreach(var fn in definitions)
            {
                var args=Arguments(fn.Groups["args"].Value);
                var uniform=args.Select(p=>Regex.IsMatch(p,@"\buniform\b")).ToArray();
                if(!uniform.Any(v=>v))
                    for(int i=Math.Max(0,args.Count-constants.Count);i<args.Count;i++)
                        uniform[i]=!args[i].Contains(":") && !args[i].StartsWith("out ",StringComparison.Ordinal);
                if(uniform.Count(v=>v)!=constants.Count)continue;
                var inputs=new List<string>();var call=new List<string>();int k=0;
                for(int i=0;i<args.Count;i++){
                    if(uniform[i]){call.Add(constants[k++]);continue;}
                    var name=Regex.Match(args[i],@"(\w+)(?:\s*\[[^\]]+\])?\s*(?::.*)?$");
                    if(!name.Success)throw new FormatException("入力引数を解決できません: "+args[i]);
                    inputs.Add(args[i]);call.Add(name.Groups[1].Value);
                }
                hlsl.AppendLine().Append(fn.Groups["type"].Value).Append(' ').Append(wrapper).Append('(').Append(string.Join(",",inputs))
                    .Append(')').Append(fn.Groups["result"].Value).Append(" { ");
                if(fn.Groups["type"].Value!="void")hlsl.Append("return ");
                hlsl.Append(entry).Append('(').Append(string.Join(",",call)).AppendLine("); }");
                return wrapper;
            }
            throw new FormatException("compile引数と関数定義が一致しません: "+entry);
        }
    }
}
