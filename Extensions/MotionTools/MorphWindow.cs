using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Ava.Extensions;

namespace Ava.MotionTools
{
    sealed partial class MotionWindow
    {
        const int PersonChoice=860,AddMapping=853,FirstMapping=30000;
        readonly Dictionary<int,List<string[]>> mappings=new Dictionary<int,List<string[]>>();
        readonly Dictionary<int,string> mappingSources=new Dictionary<int,string>();
        readonly Dictionary<int,string> mappingContent=new Dictionary<int,string>();
        int mappingPerson=100, mappingControls,mappingOffset;
        bool updatingMappings;
        string[] sourceNames=Array.Empty<string>(),targetNames=Array.Empty<string>();
        static string[] EmptyMapping()=>new[]{"","","1.00"};

        [DllImport("user32",EntryPoint="SendMessageW",CharSet=CharSet.Unicode)]
        static extern IntPtr ComboText(IntPtr hwnd,uint message,IntPtr w,string text);
        void ComboItems(int id,IEnumerable<string> names)
        {
            var handle=Control(id);SendMessageW(handle,0x14b,IntPtr.Zero,IntPtr.Zero);
            foreach(string name in names)ComboText(handle,0x143,IntPtr.Zero,name);
        }
        void BuildMappingTable()
        {
            mappingPerson=100;mappingOffset=mappingControls=0;mappings.Clear();mappingSources.Clear();mappingContent.Clear();
            Add(PersonChoice,"COMBOBOX","",0x210003);Add(AddMapping,"BUTTON","行追加",0x10000);
            Add(861,"STATIC","モーションモーフ");Add(862,"STATIC","置換後モーフ");Add(863,"STATIC","大きさ補正");
        }
        List<string[]> MappingRows(int person)
        {
            if(!mappings.TryGetValue(person,out var rows))mappings.Add(person,rows=new List<string[]>{EmptyMapping()});
            return rows;
        }
        void CheckMappingSource(int person)
        {
            string stamp=string.Join("|",new[]{person,person+10,person+20}.Select(PathField));
            if(mappingSources.TryGetValue(person,out var previous) && previous!=stamp){mappingContent.Remove(person);ResetPersonOptions(person);ResetMappings(person);}
            mappingSources[person]=stamp;
        }
        void CheckPersonContent(int person)
        {
            using var sha=System.Security.Cryptography.SHA256.Create();
            string Fingerprint(int id){
                string field=PathField(id);string path=field.Split('|')[0];
                if(!File.Exists(path))return field;
                using var data=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                return field+":"+Convert.ToBase64String(sha.ComputeHash(data));
            }
            var content=string.Join("|",new[]{person,person+10,person+20}.Select(Fingerprint));
            if(mappingContent.TryGetValue(person,out var previous) && previous!=content){
                mappings.Remove(person);ResetPersonOptions(person);
                Text(Log,"入力ファイルが変更されたため、モーフ対応と人物別補正をリセットしました。");
            }
            mappingContent[person]=content;
        }
        void ResetMappings(int person)
        {
            mappings.Remove(person);if(page==2 && mappingPerson==person)ShowMappings(true);
        }
        void LoadMorphNames()
        {
            sourceNames=targetNames=Array.Empty<string>();
            var motion=PathField(mappingPerson);var target=PathField(mappingPerson+20);
            try{
                if(File.Exists(motion))sourceNames=VmdDocument.Read(motion).Records(MotionChannel.Morph)
                    .Where(row=>BitConverter.ToSingle(row,19)>0).Select(VmdDocument.Name).Distinct().ToArray();
                if(File.Exists(target.Split('|')[0]))targetNames=Sizing.ReadRig(context,target).morphs??Array.Empty<string>();
            }catch(Exception ex){Text(Log,ex.Message);}
        }
        int[] MappingPeople()=>new[]{100}.Concat(addedSets).ToArray();
        void ShowMappings(bool visible)
        {
            foreach(int id in new[]{PersonChoice,AddMapping,861,862,863})Show(id,visible);
            for(int i=0;i<mappingControls*3;i++)Show(FirstMapping+i,false);
            if(!visible)return;
            updatingMappings=true;
            try{
                var people=MappingPeople();
                ComboItems(PersonChoice,people.Select((p,i)=>"【No."+(i+1)+"】 "+Path.GetFileNameWithoutExtension(PathField(p))));
                SendMessageW(Control(PersonChoice),0x14e,new IntPtr(Array.IndexOf(people,mappingPerson)),IntPtr.Zero);
                CheckPersonContent(mappingPerson);LoadMorphNames();
                var rows=MappingRows(mappingPerson);
                for(int i=0;i<rows.Count;i++)DisplayMappingRow(i,rows[i]);
            }finally{updatingMappings=false;}
        }
        void DisplayMappingRow(int row,string[] values)
        {
            int id=FirstMapping+3*row;
            if(row>=mappingControls){
                if(row>=1200)throw new InvalidOperationException("モーフ対応行の上限です。");
                Add(id,"COMBOBOX","",0x210042);Add(id+1,"COMBOBOX","",0x210042);Add(id+2,"EDIT","",EditStyle);mappingControls++;
            }
            ComboItems(id,sourceNames);ComboItems(id+1,targetNames);
            for(int i=0;i<3;i++)Text(id+i,values[i]);
        }
        void LayoutMappings(int w,int h)
        {
            Place(PersonChoice,14,43,w-28,210);
            int buttonWidth=Math.Max(48,(w-34)/4);
            foreach(var pair in new[]{852,850,851,AddMapping}.Select((id,i)=>(id,i)))Place(pair.id,14+pair.i*buttonWidth,75,buttonWidth-3,28);
            int unit=Math.Max(44,(w-136)/2),top=132,area=Math.Max(1,h-top-82);
            Place(861,14,110,unit,21);Place(862,18+unit,110,unit,21);Place(863,22+2*unit,110,96,21);
            int count=MappingRows(mappingPerson).Count;
            mappingOffset=Math.Max(0,Math.Min(mappingOffset,count*30-area));
            for(int row=0;row<mappingControls;row++){
                int id=FirstMapping+3*row,y=top+30*row-mappingOffset;
                bool visible=row<count && y>=top && y<top+area;
                for(int i=0;i<3;i++)Show(id+i,visible);
                if(visible){Place(id,14,y,unit,165);Place(id+1,18+unit,y,unit,165);Place(id+2,22+2*unit,y,94,23);}
            }
            ScrollRange(count*30,area,mappingOffset);Place(Log,14,h-78,w-28,64);Show(Progress,false);
        }
        void AddMappingRow(bool scroll)
        {
            var rows=MappingRows(mappingPerson);if(rows.Count>=1200)return;
            rows.Add(EmptyMapping());updatingMappings=true;
            try{DisplayMappingRow(rows.Count-1,rows.Last());}finally{updatingMappings=false;}
            if(scroll)mappingOffset=int.MaxValue;LayoutMappings(width,height);
        }
        bool MappingCommand(int id,int code)
        {
            if(page!=2 || updatingMappings)return false;
            if(id==PersonChoice && code==1){
                int at=SendMessageW(Control(id),0x147,IntPtr.Zero,IntPtr.Zero).ToInt32();
                if(at>=0){mappingPerson=MappingPeople()[at];mappingOffset=0;Refresh();}return true;
            }
            if(id==AddMapping && code==0){AddMappingRow(true);return true;}
            if(id<FirstMapping || id>=FirstMapping+3*mappingControls)return false;
            int row=(id-FirstMapping)/3,col=(id-FirstMapping)%3;
            if(col<2 && (code==1 || code==5 || code==8) || col==2 && code==0x300){
                var rows=MappingRows(mappingPerson);if(row>=rows.Count)return true;
                if(code==1 && col<2){
                    int at=SendMessageW(Control(id),0x147,IntPtr.Zero,IntPtr.Zero).ToInt32();
                    var choices=col==0?sourceNames:targetNames;
                    if(at>=0 && at<choices.Length)rows[row][col]=choices[at];
                }else rows[row][col]=Text(id);
                if(row==rows.Count-1 && rows[row][0].Length>0 && rows[row][1].Length>0)AddMappingRow(false);
            }
            return true;
        }
        void SetMappingRows(IEnumerable<string[]> data)
        {
            var rows=data.ToList();if(rows.Count>=1200)throw new InvalidDataException("モーフ対応行の上限です。");
            rows.Add(EmptyMapping());mappings[mappingPerson]=rows;Refresh();
        }
        string MappingText(int person)
        {
            if(!mappings.TryGetValue(person,out var rows))return "";
            return string.Concat(rows.Where(row=>row[0].Length>0 && row[1].Length>0)
                .Select(row=>row[0]+":"+row[1]+":"+row[2]+";").Distinct());
        }
    }
}
