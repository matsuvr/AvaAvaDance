using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ava.SurfaceEffects
{
    public static partial class EffectCompiler
    {
        static readonly Regex ArithmeticToken=new Regex(
            @"(?m)^[ \t]*\#[^\r\n]*|//[^\n]*|/\*[\s\S]*?\*/|""(?:\\.|[^""\\])*""|[A-Za-z_]\w*|(?:0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)[uUfFhHlL]*|\+\+|--|/=|[^\s]");
        static readonly Regex NumericType=new Regex(@"^(?:half|float|int|uint|bool|double)(?:[1-4](?:x[1-4])?)?$");

        static string DenominatorFunctions()
        {
            var source=new StringBuilder();
            for(int width=1;width<=4;width++){
                string suffix=width==1?"":width.ToString();
                string type="float"+suffix;
                // Signed finite reciprocals keep a zero numerator at zero on cleared floating-point targets.
                source.AppendLine(type+" AvaMath_den("+type+" v){return v==0 ? asfloat((asuint(v)&0x80000000u)|0x00800000u):v;}");
                foreach(string scalar in new[]{"int","uint"}){
                    type=scalar+suffix;source.AppendLine(type+" AvaMath_den("+type+" v){return v;}");
                }
            }
            // Distinct names avoid FXC's ambiguous vector-to-matrix overload conversions.
            for(int rows=2;rows<=4;rows++)for(int columns=2;columns<=4;columns++){
                string shape=rows+"x"+columns;
                string type="float"+shape;
                source.AppendLine(type+" AvaMath_den"+shape+"("+type+" v){return v==0 ? asfloat((asuint(v)&0x80000000u)|0x00800000u):v;}");
            }
            return source.ToString();
        }
        static readonly string Denominators=DenominatorFunctions();

        static string ArithmeticSource(string source)
        {
            var tokens=ArithmeticToken.Matches(source).Cast<Match>().Where(m=>m.Value[0]!='#' &&
                !m.Value.StartsWith("//",StringComparison.Ordinal) && !m.Value.StartsWith("/*",StringComparison.Ordinal)).ToArray();
            var ends=new int[tokens.Length];var stack=new Stack<int>();
            for(int i=0;i<tokens.Length;i++){
                string v=tokens[i].Value;
                if(v=="(" || v=="[" || v=="{")stack.Push(i);
                else if((v==")" || v=="]" || v=="}") && stack.Count>0)ends[stack.Pop()]=i;
            }
            string Value(int at)=>at<tokens.Length?tokens[at].Value:"";
            bool Group(int at)=>at<tokens.Length && ends[at]>at;
            int OperandEnd(int at){
                while(at<tokens.Length){
                    string v=Value(at);
                    if(v=="+" || v=="-" || v=="!" || v=="~" || v=="++" || v=="--"){at++;continue;}
                    if(v=="(" && Group(at) && ends[at]==at+2 && NumericType.IsMatch(Value(at+1))){at=ends[at]+1;continue;}
                    break;
                }
                at=Group(at)?ends[at]+1:at+1;
                while(at<tokens.Length){
                    if((Value(at)=="(" || Value(at)=="[") && Group(at))at=ends[at]+1;
                    else if(Value(at)==".")at+=2;
                    else if(Value(at)=="++" || Value(at)=="--")at++;
                    else break;
                }
                return at;
            }
            bool IntegerPower(int open){
                if(!Group(open))return false;
                int end=ends[open],comma=open+1;
                while(comma<end && Value(comma)!=",")comma=Group(comma)?ends[comma]+1:comma+1;
                int first=comma+1,last=end;
                while(first<last && Value(first)=="(" && ends[first]==last-1){first++;last--;}
                if(Value(first)=="+" || Value(first)=="-")first++;
                return first+1==last && double.TryParse(Value(first).TrimEnd('f','F','h','H'),NumberStyles.Float,CultureInfo.InvariantCulture,out var number) &&
                    number==Math.Truncate(number);
            }
            var shapes=new Dictionary<string,string>(StringComparer.Ordinal);
            string MatrixShape(string type){
                if(type=="matrix")return "4x4";
                int at=type.IndexOf('x');
                return at>0 && at+1==type.Length-1 && type[at-1]>='2' && type[at-1]<='4' && type[at+1]>='2' &&
                    type[at+1]<='4' && (type.StartsWith("float",StringComparison.Ordinal) || type.StartsWith("half",StringComparison.Ordinal))?
                    type.Substring(at-1):null;
            }
            // Keep the declared return shape for function calls. Their matrix arguments do not determine the result.
            for(int i=0;i+1<tokens.Length;i++)
                if(NumericType.IsMatch(Value(i)) || Value(i)=="matrix")shapes[Value(i+1)]=MatrixShape(Value(i));
            string OperandMatrix(int first,int end){
                while(first<end && Value(first)=="(" && ends[first]==end-1){first++;end--;}
                if(first>=end)return null;
                if(Value(first)=="+" || Value(first)=="-" || Value(first)=="++" || Value(first)=="--")return OperandMatrix(first+1,end);
                if(Value(first)=="(" && ends[first]==first+2 && NumericType.IsMatch(Value(first+1)))return MatrixShape(Value(first+1));
                // Swizzles and matrix row indexing return scalar or vector values.
                int rootEnd=Group(first)?ends[first]+1:first+1;
                if(Value(rootEnd)=="(" && Group(rootEnd))rootEnd=ends[rootEnd]+1;
                if(rootEnd<end && (Value(rootEnd)=="." || Value(rootEnd)=="["))return null;
                if(rootEnd==end){
                    string name=Value(first);
                    string constructor=MatrixShape(name);if(constructor!=null)return constructor;
                    if(shapes.TryGetValue(name,out var declared))return declared;
                    if((name=="transpose" || name=="mul") && Value(first+1)=="("){
                        int stop=ends[first+1],comma=first+2;
                        while(comma<stop && Value(comma)!=",")comma=Group(comma)?ends[comma]+1:comma+1;
                        string left=OperandMatrix(first+2,comma);
                        if(name=="transpose")return left==null?null:left[2]+"x"+left[0];
                        string right=OperandMatrix(comma+1,stop);
                        return left!=null && right!=null?left[0]+"x"+right[2]:null;
                    }
                    return null;
                }
                // Componentwise arithmetic inherits a matrix operand's shape.
                for(int i=first;i<end;i++){
                    if(Group(i)){i=ends[i];continue;}
                    if(Value(i)=="+" || Value(i)=="-" || Value(i)=="*" || Value(i)=="/")
                        return OperandMatrix(first,i)??OperandMatrix(i+1,end);
                }
                return null;
            }
            var inserts=new SortedDictionary<int,List<string>>();
            void Insert(int index,string text){
                if(!inserts.TryGetValue(index,out var list))inserts.Add(index,list=new List<string>());
                list.Add(text);
            }
            for(int i=0;i<tokens.Length;i++){
                string v=Value(i);
                if(v=="/" || v=="/="){
                    int first=i+1;if(first>=tokens.Length)continue;
                    int end=first;
                    if(v=="/")end=OperandEnd(first);
                    else while(end<tokens.Length && Value(end)!=";" && Value(end)!="," && Value(end)!=")" && Value(end)!="]" && Value(end)!="}")
                        end=Group(end)?ends[end]+1:end+1;
                    if(end<=first || end>tokens.Length)continue;
                    if(end==first+1 && double.TryParse(Value(first).TrimEnd('f','F','h','H'),NumberStyles.Float,
                        CultureInfo.InvariantCulture,out double literal) && literal!=0)continue;
                    Insert(tokens[first].Index,"AvaMath_den"+OperandMatrix(first,end)+"(");Insert(tokens[end-1].Index+tokens[end-1].Length,")");
                }else if(Value(i+1)=="(" && (v=="sqrt" || v=="rsqrt" || v=="log" || v=="log2" || v=="log10" || v=="pow" && !IntegerPower(i+1)))
                    Insert(tokens[i].Index,"AvaMath_");
            }
            var result=new StringBuilder(source.Length+inserts.Count*8);int cursor=0;
            foreach(var item in inserts){
                result.Append(source,cursor,item.Key-cursor);foreach(var text in item.Value)result.Append(text);cursor=item.Key;
            }
            return result.Append(source,cursor,source.Length-cursor).ToString();
        }
    }
}
