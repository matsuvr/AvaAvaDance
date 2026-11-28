using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ava.Extensions;

namespace Ava.SurfaceEffects
{
    public sealed partial class EffectPlugin
    {
        long sourceVersion=-1;
        void DiscoverDefaults()
        {
            if(!writable || !(context is IAssetCatalog catalog) || catalog.SourceRevision==sourceVersion)return;
            sourceVersion=catalog.SourceRevision;var next=state.Copy();bool changed=false;
            var sources=catalog.Sources();
            var slots=context.Materials();var owners=new HashSet<string>(slots.Select(m=>m.ActorId));
            changed=next.defaults.RemoveAll(r=>!owners.Contains(r.slot.Split('/')[0]))>0;
            foreach(var item in sources){
                if(next.defaults.Any(a=>a.slot==item.Owner || a.slot.StartsWith(item.Owner+"/",StringComparison.Ordinal)))continue;
                var tag=Regex.Match(item.Name??"", @"\[([^\[\]]+\.fx)\]",RegexOptions.IgnoreCase);
                string stem=Path.GetFileNameWithoutExtension(item.Asset);
                var names=new List<string>();if(tag.Success)names.Add(tag.Groups[1].Value);
                names.Add(stem+".avaemd");names.Add(stem+".fx");
                string file=names.SelectMany(n=>new[]{Path.Combine(Path.GetDirectoryName(item.Asset),n),
                    item.Folder==null?null:Path.Combine(item.Folder,n)}).Where(n=>n!=null).Select(Path.GetFullPath).FirstOrDefault(item.Exists);
                if(file==null)continue;
                try{
                    if(Path.GetExtension(file).Equals(".avaemd",StringComparison.OrdinalIgnoreCase)){
                        var preset=EffectSettings.Decode(item.Read(file));if(preset.format!="avaemd")throw new InvalidDataException("材質設定の形式が不正です。");
                        foreach(var row in preset.rows){var copy=row.Copy();copy.slot=item.Owner+(row.slot=="."?"":row.slot);next.defaults.Add(copy);}
                    }else{
                        var row=new Assignment{slot=item.Owner,visible=true};ReadEffect(row,file,item.Read);next.defaults.Add(row);
                    }
                    changed=true;
                }catch(Exception ex){context.Report(ex.Message);}
            }
            if(changed)try{Apply(next);}catch(Exception ex){context.Report(ex.Message);}
            window?.SceneChanged(slots,state.Copy());
        }
    }
}
