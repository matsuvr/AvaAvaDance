using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ava.SurfaceEffects
{
    public static partial class EffectCompiler
    {
        static string ShaderArguments(string args,Dictionary<string,string> textures)
        {
            if(string.IsNullOrWhiteSpace(args))return args;
            return string.Join(",",Arguments(args).Select(arg=>textures.TryGetValue(arg,out var tex)?tex+","+arg:arg));
        }

        static string SamplerArguments(string source,Dictionary<string,string> textures,out HashSet<string> locals)
        {
            var localNames=new HashSet<string>(StringComparer.Ordinal);locals=localNames;
            var paramsByFunction=new Dictionary<(string name,int arity),List<int>>();
            var parameter=new Regex(@"^(?<prefix>\s*(?:(?:in|uniform|const)\s+)*)sampler(?<dim>2D|3D|CUBE)?\s+(?<var>\w+)\s*$",RegexOptions.IgnoreCase);
            var declarations=new Regex(@"\b\w+\s+(?<name>\w+)\s*\((?<args>[^(){};]*)\)\s*(?::\s*\w+\s*)?(?=[{;])");
            source=declarations.Replace(source,m=>{
                var args=Arguments(m.Groups["args"].Value);var positions=new List<int>();
                for(int i=0;i<args.Count;i++){
                    var p=parameter.Match(args[i]);if(!p.Success)continue;
                    string name=p.Groups["var"].Value,pre=p.Groups["prefix"].Value;
                    string dimension=p.Groups["dim"].Value.ToUpperInvariant() switch {"CUBE"=>"Cube","3D"=>"3D",_=>"2D"};
                    args[i]=pre+"Texture"+dimension+" AvaTex_"+name+", "+pre+"SamplerState "+name;
                    localNames.Add(name);positions.Add(i);
                }
                if(positions.Count==0)return m.Value;
                int required=args.Count;
                while(required>0 && args[required-1].Contains("="))required--;
                for(int arity=required;arity<=args.Count;arity++){
                    var key=(m.Groups["name"].Value,arity);
                    if(!paramsByFunction.TryGetValue(key,out var existing))paramsByFunction[key]=existing=new List<int>();
                    foreach(int position in positions.Where(p=>p<arity))if(!existing.Contains(position))existing.Add(position);
                }
                int offset=m.Groups["args"].Index-m.Index;
                return m.Value.Substring(0,offset)+string.Join(",",args)+m.Value.Substring(offset+m.Groups["args"].Length);
            });
            if(paramsByFunction.Count==0)return source;
            var calls=Regex.Matches(source,@"\b(\w+)\s*\(");var inserts=new List<(int at,string text)>();
            var functions=paramsByFunction.Keys.Select(p=>p.name).ToHashSet();
            foreach(Match call in calls){
                string name=call.Groups[1].Value;if(!functions.Contains(name))continue;
                int at=call.Index+call.Length,start=at,depth=0;var args=new List<(int start,string value)>();
                for(;at<source.Length;at++){
                    var c=source[at];
                    if(depth==0 && (c==',' || c==')')){
                        args.Add((start,source.Substring(start,at-start).Trim()));if(c==')')break;start=at+1;
                    }else if(c=='(' || c=='[' || c=='{')depth++;
                    else if(c==')' || c==']' || c=='}')depth--;
                }
                if(!paramsByFunction.TryGetValue((name,args.Count),out var positions))continue;
                foreach(int index in positions){
                    var arg=args[index];string texture;
                    if(!textures.TryGetValue(arg.value,out texture)){
                        if(!localNames.Contains(arg.value))continue;texture="AvaTex_"+arg.value;
                    }
                    inserts.Add((arg.start,texture+","));
                }
            }
            var result=new StringBuilder(source);
            foreach(var insert in inserts.OrderByDescending(i=>i.at))result.Insert(insert.at,insert.text);
            return result.ToString();
        }
    }
}
