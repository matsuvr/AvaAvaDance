using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Ava.Extensions;

namespace Ava.MotionTools
{
    sealed partial class MotionWindow
    {
        int stanceMask=247;
        readonly Dictionary<int,string> autoName=new Dictionary<int,string>();
        static bool IsOutput(int id)=>id==130 || id==440 || id>=2000 && id%100==30;
        string HistoryFile(int field)
        {
            int type=PersonField(field,out _,out int component)?100+component:field;
            return Path.Combine(context.SettingsDirectory,"history-"+type+".txt");
        }
        string[] History(int field)
        {
            var path=HistoryFile(field);return File.Exists(path)?File.ReadAllLines(path).Take(50).ToArray():Array.Empty<string>();
        }
        void SelectPath(int id,string path)
        {
            if(job!=null)return;
            if(path.EndsWith(".unitypackage",StringComparison.OrdinalIgnoreCase))
            {
                var rigs=context.ReadRigs(path);
                if(rigs.Length>1)
                {
                    int item=Choose("モデルを選択",rigs.Select(r=>r.name).ToArray());
                    if(item<0)return;path+="|"+rigs[item].sourceId;
                }
            }
            if(!IsOutput(id))
            {
                Directory.CreateDirectory(context.SettingsDirectory);
                var paths=new[]{path}.Concat(History(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
                VmdDocument.AtomicFile(HistoryFile(id),tmp=>File.WriteAllLines(tmp,paths,new UTF8Encoding(false)));
            }
            Text(id,path);
            if(id>=100 && id<=120){ResetMappings(100);ResetPersonOptions(100);UpdateOutputName(100);}
            else if(PersonField(id,out int first,out int offset) && offset<=20){ResetMappings(first);ResetPersonOptions(first);Text(700,"2.5");UpdateOutputName(first);}
        }
        void UpdateOutputName(int first)
        {
            string motion=PathField(first),target=PathField(first+20);
            if(motion.Length==0 || target.Length==0 || motion.IndexOfAny(new[]{'*','?'})>=0 || Directory.Exists(motion))return;
            int output=first+30;
            string current=PathField(output);autoName.TryGetValue(output,out var previous);
            if(current.Length!=0 && current!=previous)return;
            string flags=(Checked(first==100?810:first+40)?"S":"")+(Checked(first==100?811:first+41)?"T":"")+
                (MappingText(first).Length>0?"M":"")+(Checked(820)?"I":"")+(Checked(821)?"P":"");
            var name=Path.GetFileNameWithoutExtension(motion)+"_"+Path.GetFileNameWithoutExtension(target.Split('|')[0])+"_"+
                flags+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss",CultureInfo.InvariantCulture)+".vmd";
            autoName[output]=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(motion)),name);Text(output,autoName[output]);
        }
        [DllImport("shell32",CharSet=CharSet.Unicode)] static extern uint DragQueryFileW(IntPtr drop,uint index,StringBuilder text,uint count);
        protected override void Drop(IntPtr files)
        {
            try {
                if(job!=null)return;
                var path=new StringBuilder(32768);if(DragQueryFileW(files,0,path,32768)==0)return;
                int target=DropTarget(files);
                var field=fields.FirstOrDefault(f=>f.page==page && f.pattern!=null && (target==f.id || target==f.id+1 || target==f.id+2));
                if(field.pattern==null)field=fields.FirstOrDefault(f=>f.page==page && f.pattern!=null);
                if(field.pattern!=null)SelectPath(field.id,path.ToString());
            }finally{DragFinish(files);}
        }

        void EditMappings(int command)
        {
            if(command==850)
            {
                var path=Pick("モーフ置換をインポート","*.csv");if(path==null)return;
                var lines=MotionCsv.Rows(path,false).ToArray();
                if(lines.Length!=3 || lines[0].Length!=lines[1].Length || lines[0].Length!=lines[2].Length)throw new InvalidDataException("モーフ対応CSVは同じ列数の3行です。");
                SetMappingRows(Enumerable.Range(0,lines[0].Length).Select(i=>new[]{lines[0][i],lines[1][i],lines[2][i]}));return;
            }
            var spec=MappingText(mappingPerson);
            if(command==852){CopyText(spec);Text(Log,spec);return;}
            var rules=spec.Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries).Select(entry=>entry.Split(':')).ToArray();
            if(rules.Length==0 || rules.Any(rule=>rule.Length!=3))throw new InvalidDataException("モーフ置換を指定してください。");
            var input=PathField(mappingPerson);string name=string.Join("_",new[]{input,PathField(mappingPerson+10),PathField(mappingPerson+20)}.Select(p=>Path.GetFileNameWithoutExtension(p.Split('|')[0])));
            string dest=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)),name+".csv");
            string linesText=string.Join("\r\n",Enumerable.Range(0,3).Select(i=>string.Join(",",rules.Select(r=>"\""+r[i].Replace("\"","\"\"")+"\""))))+"\r\n";
            VmdDocument.AtomicFile(dest,tmp=>File.WriteAllBytes(tmp,MmdText.Encode(linesText)));Text(Log,dest);
        }
    }
}
