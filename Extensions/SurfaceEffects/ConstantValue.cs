using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ava.SurfaceEffects
{
    public static partial class EffectCompiler
    {
        static readonly Regex ConstantToken=new Regex(@"\G\s*(?<value>(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?[fF]?|[A-Za-z_]\w*|[()+*/{},\-])");
        static bool TryConstant(string text,Dictionary<string,float[]> known,out float[] value)
        {
            value=null;
            var tokens=new List<string>();
            for(int offset=0;offset<text.Length;){
                var token=ConstantToken.Match(text,offset);
                if(!token.Success || tokens.Count>=256)return false;
                tokens.Add(token.Groups["value"].Value);offset+=token.Length;
            }
            int at=0;
            string Peek()=>at<tokens.Count?tokens[at]:null;
            bool Take(string token){if(Peek()!=token)return false;at++;return true;}
            bool Atom(out double number){
                number=0;
                if(Take("-")){if(!Atom(out number))return false;number=-number;return true;}
                if(Take("+"))return Atom(out number);
                if(Take("("))return Sum(out number) && Take(")");
                string token=Peek();if(token==null)return false;at++;
                if(known.TryGetValue(token,out var entry) && entry.Length==1){number=entry[0];return true;}
                return double.TryParse(token.TrimEnd('f','F'),NumberStyles.Float,CultureInfo.InvariantCulture,out number);
            }
            bool Product(out double number){
                if(!Atom(out number))return false;
                while(Peek()=="*" || Peek()=="/"){
                    bool multiply=Take("*");if(!multiply)at++;
                    if(!Atom(out double rhs))return false;
                    number=multiply?number*rhs:number/rhs;
                }
                return true;
            }
            bool Sum(out double number){
                if(!Product(out number))return false;
                while(Peek()=="+" || Peek()=="-"){
                    bool add=Take("+");if(!add)at++;
                    if(!Product(out double rhs))return false;
                    number=add?number+rhs:number-rhs;
                }
                return true;
            }
            var result=new List<float>();
            // An initializer may enclose a vector or a single scalar expression in parentheses.
            int start=at;bool grouped=Take("{") || Take("(");string end=grouped && tokens[start]=="{"?"}":")";
            if(grouped && end==")"){
                int depth=1,close=at;
                while(close<tokens.Count && depth>0){
                    string part=tokens[close++];if(part=="(")depth++;else if(part==")")depth--;
                }
                if(close!=tokens.Count || depth!=0){at=start;grouped=false;}
            }
            do{
                if(!Sum(out double number) || double.IsNaN(number) || double.IsInfinity(number) || result.Count>=16)return false;
                result.Add((float)number);
            }while(Take(","));
            if(grouped && !Take(end) || at!=tokens.Count)return false;
            value=result.ToArray();return true;
        }
    }
}
