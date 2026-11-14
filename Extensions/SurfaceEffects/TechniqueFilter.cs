using System;
using System.Linq;
using System.Text.RegularExpressions;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    sealed class TechniqueFilter
    {
        string subset;
        bool? texture,sphere,toon;
        public bool HasSubset=>!string.IsNullOrWhiteSpace(subset) && subset!="*";
        public bool Conditional=>subset!=null || texture.HasValue || sphere.HasValue || toon.HasValue;
        public static TechniqueFilter Read(string declaration)
        {
            string Field(string key){
                var m=Regex.Match(declaration,"\\b"+key+"\\s*=\\s*(?:\"([^\"]*)\"|([^;>]+))",RegexOptions.IgnoreCase);
                return m.Success?(m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value.Trim()):null;
            }
            bool? Flag(string key){
                string v=Field(key);
                if(v==null)return null;
                if(v=="1" || v.Equals("true",StringComparison.OrdinalIgnoreCase))return true;
                if(v=="0" || v.Equals("false",StringComparison.OrdinalIgnoreCase))return false;
                throw new FormatException(key+" の条件が不正です。");
            }
            return new TechniqueFilter{subset=Field("Subset"),texture=Flag("UseTexture"),sphere=Flag("UseSphereMap"),toon=Flag("UseToon")};
        }
        public bool Accepts(MaterialSlot material)
        {
            if(texture.HasValue && texture!=material.Textured || sphere.HasValue && sphere!=material.SphereMapped || toon.HasValue && toon!=material.Toon)return false;
            if(string.IsNullOrWhiteSpace(subset) || subset=="*")return true;
            return subset.Split(',').Any(item=>{
                string[] parts=item.Trim().Split('-');
                if(parts.Length>2 || !int.TryParse(parts[0],out var start))return false;
                int end=start;if(parts.Length==2 && !int.TryParse(parts[1],out end))return false;
                return start<=material.Subset && material.Subset<=end;
            });
        }
    }
}
