using System;
using System.Linq;
using System.Collections.Generic;

namespace Ava.MotionTools
{
    sealed partial class MotionWindow
    {
        readonly Dictionary<int,string[]> contactSelections=new Dictionary<int,string[]>();
        void ChooseContacts()
        {
            var people=MappingPeople();int index=people.Length==1?0:Choose("接触回避剛体選択",people.Select((p,i)=>"【No."+(i+1)+"】 "+PathField(p+20)).ToArray());
            if(index<0)return;
            int person=people[index];string path=PathField(person+20);
            var shapes=string.IsNullOrWhiteSpace(path)?Array.Empty<Ava.Extensions.RigShape>():Sizing.ReadRig(context,path).shapes??Array.Empty<Ava.Extensions.RigShape>();
            var names=new[]{"頭接触回避"}.Concat(shapes.Select(s=>s.name)).ToArray();
            var labels=new[]{"頭接触回避 (頭)"}.Concat(shapes.Select(s=>s.name+" ("+s.role+")")).ToArray();
            if(!contactSelections.TryGetValue(person,out var previous))previous=new[]{names[0]};
            var enabled=names.Select(n=>(byte)(previous.Contains(n)?1:0)).ToArray();
            if(!ChooseItems("接触回避剛体選択",labels,enabled))return;
            contactSelections[person]=names.Where((_,i)=>enabled[i]!=0).ToArray();
            Text(885,string.Join("\r\n",people.Where(contactSelections.ContainsKey).Select(p=>"【No."+(Array.IndexOf(people,p)+1)+"】 "+string.Join("、",contactSelections[p]))));
            SendMessageW(Control(820),0xf1,new IntPtr(1),IntPtr.Zero);UpdateOutputName(person);
        }
        bool PersonField(int id,out int first,out int offset)
        {
            first=id<2000?100:id/100*100;
            offset=id-first;
            return (first==100 || addedSets.Contains(first)) && (offset==0 || offset==10 || offset==20 || offset==30);
        }
        void AddPersonSet()
        {
            if(addedSets.Count>=250)throw new InvalidOperationException("人物数の上限です。");
            int start=2000+100*addedSets.Count;
            addedSets.Add(start);
            Add(start-4,"STATIC","【No."+(addedSets.Count+1)+"】");
            Field(1,start,"調整対象モーションVMD/VPD","*.vmd;*.vpd");
            Field(1,start+10,"モーション作成元モデル","*.vrm;*.unitypackage;*.avarig");
            Field(1,start+20,"モーション変換先モデル","*.vrm;*.unitypackage;*.avarig");
            Field(1,start+30,"出力VMD","*.vmd");
            Add(start+43,"BUTTON","＊",0x10000);
            Tick(1,start+40,"スタンス追加補正");Tick(1,start+41,"捩り分散あり");
            groupOffset=int.MaxValue;Refresh();
        }
        protected override void Scroll(int position){if(page==1){groupOffset=position;LayoutGroups(width,height);}else if(page==2){mappingOffset=position;LayoutMappings(width,height);}}
        void LayoutGroups(int w,int h)
        {
            Place(Run,10,42,180,34);Place(Extra,196,42,160,34);
            Show(Log,false);Show(Progress,false);
            int top=86,area=Math.Max(1,h-top),heightPer=264;
            groupOffset=Math.Max(0,Math.Min(groupOffset,addedSets.Count*heightPer-area));
            int y=top-groupOffset;
            void Item(int id,int x,int offset,int width,int height)
            {
                bool visible=y+offset>=top && y+offset<h;
                Show(id,visible);if(visible)Place(id,x,y+offset,width,height);
            }
            foreach(int start in addedSets){
                Item(start-4,12,0,120,22);
                for(int j=0;j<4;j++){
                    int id=start+j*10,offset=24+j*45;
                    Item(id-1,12,offset,w-28,20);Item(id,12,offset+18,w-(j==3?100:150),23);
                    Item(id+1,w-(j==3?82:132),offset+18,68,23);if(j!=3)Item(id+2,w-58,offset+18,45,23);
                }
                Item(start+40,12,213,156,22);Item(start+43,170,213,24,22);Item(start+41,198,213,175,22);
                y+=heightPer;
            }
            ScrollRange(addedSets.Count*heightPer,area,groupOffset);
        }
    }
}
