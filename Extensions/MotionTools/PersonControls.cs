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
        const int MoveSlider=900,FootButton=901,FootSummary=902,DistanceChoice=910,DistanceSlider=911,CameraPerson=912;
        readonly Dictionary<int,int> extraStance=new Dictionary<int,int>();
        readonly Dictionary<int,double> feet=new Dictionary<int,double>(),heights=new Dictionary<int,double>();
        readonly Dictionary<int,string> cameraSources=new Dictionary<int,string>();
        int cameraPerson=100;
        bool settingControls;
        string checkedArms;
        int ExtraStance(int set)=>extraStance.TryGetValue(set,out var bits)?bits:247;
        [DllImport("user32",CharSet=CharSet.Unicode)]static extern int MessageBoxW(IntPtr window,string text,string title,uint flags);
        [DllImport("AvaClassicUi",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_ui_offsets(IntPtr owner,string labels,int count,[In,Out]int[] offsets);

        void BuildLegControls()
        {
            feet.Clear();heights.Clear();cameraSources.Clear();extraStance.Clear();checkedArms=null;
            Add(MoveSlider,"msctls_trackbar32","",0x10001);Slider(MoveSlider,10,30,20);
            Add(FootButton,"BUTTON","足ＩＫオフセット指定",0x10000);Add(FootSummary,"EDIT","",0x50810844);
        }
        void BuildCameraControls()
        {
            cameraPerson=100;Add(DistanceChoice,"COMBOBOX","",0x210003);
            ComboItems(DistanceChoice,new[]{"距離制限強","距離制限弱","距離制限なし"});
            SendMessageW(Control(DistanceChoice),0x14e,new IntPtr(2),IntPtr.Zero);
            Add(DistanceSlider,"msctls_trackbar32","",0x10001);Slider(DistanceSlider,100,500,500);
            Add(CameraPerson,"COMBOBOX","",0x210003);
        }
        void Slider(int id,int min,int max,int at)
        {
            var hwnd=Control(id);
            SendMessageW(hwnd,0x407,new IntPtr(1),new IntPtr(min));SendMessageW(hwnd,0x408,new IntPtr(1),new IntPtr(max));
            SendMessageW(hwnd,0x405,new IntPtr(1),new IntPtr(at));
        }
        void ShowSizingControls()
        {
            foreach(int id in new[]{MoveSlider,FootButton,FootSummary})Show(id,page==4);
            foreach(int id in new[]{DistanceChoice,DistanceSlider,CameraPerson})Show(id,page==5);
            if(page==4)FootLabel();
            if(page!=5)return;
            var people=MappingPeople();ComboItems(CameraPerson,people.Select((p,i)=>"【No."+(i+1)+"】 "+Path.GetFileNameWithoutExtension(PathField(p+10))+" → "+Path.GetFileNameWithoutExtension(PathField(p+20))));
            SendMessageW(Control(CameraPerson),0x14e,new IntPtr(Array.IndexOf(people,cameraPerson)),IntPtr.Zero);
        }
        void LayoutLegs(int w,int h)
        {
            Place(399,14,45,170,22);Place(400,190,45,78,24);Place(MoveSlider,14,78,w-28,32);
            Place(FootSummary,14,136,Math.Min(450,w-28),80);Place(FootButton,14,224,165,30);
            Place(Log,14,272,w-28,h-310);Place(Progress,14,h-32,w-28,18);
        }
        void LayoutCamera(int w,int h)
        {
            void FileField(int id,int y,bool output){
                Place(id-1,14,y,w-28,18);Place(id,14,y+22,w-(output?110:169),24);
                Place(id+1,w-(output?88:145),y+22,73,24);if(!output)Place(id+2,w-65,y+22,50,24);
            }
            FileField(430,48,false);Place(830,14,99,w-28,22);FileField(440,130,true);
            Place(449,14,185,120,20);Place(DistanceChoice,140,182,145,155);Place(450,294,182,70,22);
            Place(DistanceSlider,14,213,w-28,30);Place(CameraPerson,14,253,w-28,180);
            FileField(460,291,false);Place(469,14,351,145,21);Place(470,164,350,90,23);
            Place(Log,14,390,w-28,h-437);Place(Progress,14,h-32,w-28,18);
        }
        void FootLabel()=>Text(FootSummary,string.Join("\r\n",MappingPeople().Select((p,i)=>"【No."+(i+1)+"】 "+(feet.TryGetValue(p,out var value)?value:0).ToString("0.00",CultureInfo.InvariantCulture))));
        bool SizingControlCommand(int id,int notification)
        {
            if(settingControls)return false;
            if((id==MoveSlider || id==DistanceSlider) && notification==0x7000){
                double value=SendMessageW(Control(id),0x400,IntPtr.Zero,IntPtr.Zero).ToInt32()/(id==MoveSlider?20d:100d);
                Text(id==MoveSlider?400:450,value.ToString("0.00",CultureInfo.InvariantCulture));return true;
            }
            if(id==DistanceChoice && notification==1){
                int at=SendMessageW(Control(id),0x147,IntPtr.Zero,IntPtr.Zero).ToInt32();
                if(at>=0){int n=new[]{105,130,500}[at];Text(450,(n/100d).ToString("0.00",CultureInfo.InvariantCulture));Slider(DistanceSlider,100,500,n);}return true;
            }
            if(id==CameraPerson && notification==1){
                RememberCameraPerson();int at=SendMessageW(Control(id),0x147,IntPtr.Zero,IntPtr.Zero).ToInt32();
                if(at>=0){cameraPerson=MappingPeople()[at];Text(460,cameraSources.TryGetValue(cameraPerson,out var s)?s:"");
                    Text(470,(heights.TryGetValue(cameraPerson,out var y)?y:0).ToString("0.0",CultureInfo.InvariantCulture));}return true;
            }
            if(id==FootButton && notification==0){
                var people=MappingPeople();var labels=people.Select((p,i)=>"【No."+(i+1)+"】 "+Path.GetFileNameWithoutExtension(PathField(p+20))).ToArray();
                var steps=people.Select(p=>feet.TryGetValue(p,out var value)?(int)Math.Round(value*20):0).ToArray();
                if(ava_ui_offsets(WindowHandle,string.Join("\0",labels)+"\0",labels.Length,steps)>0){
                    for(int i=0;i<people.Length;i++)feet[people[i]]=steps[i]/20d;FootLabel();
                }
                return true;
            }
            if((id==400 || id==450) && notification==0x200 && double.TryParse(Text(id),NumberStyles.Float,CultureInfo.InvariantCulture,out var number) && double.IsFinite(number)){
                settingControls=true;
                try{if(id==400)Slider(MoveSlider,10,30,(int)Math.Round(number*20));else Slider(DistanceSlider,100,500,(int)Math.Round(number*100));}finally{settingControls=false;}
            }
            return false;
        }
        void RememberCameraPerson()
        {
            heights[cameraPerson]=Value(470,-1000,1000);cameraSources[cameraPerson]=PathField(460);
        }
        void ApplyPersonOptions(Person person,int id)
        {
            RememberCameraPerson();person.FootOffset=feet.TryGetValue(id,out var offset)?offset:0;
            person.HeightOffset=heights.TryGetValue(id,out var height)?height:0;
            person.CameraSource=cameraSources.TryGetValue(id,out var file)?file:"";
        }
        void ResetPersonOptions(int person){feet.Remove(person);contactSelections.Remove(person);checkedArms=null;}
        bool ConfirmFingers()
        {
            SendMessageW(Control(821),0xf1,new IntPtr(1),IntPtr.Zero);
            return MessageBoxW(WindowHandle,"複数人の指位置を合わせますか？","指の位置合わせ",0x24)==6;
        }
        void CheckArmInput()
        {
            if(Checked(824))return;
            var inputs=MappingPeople().SelectMany((p,i)=>new[]{(i,path:PathField(p+10),label:"作成元"),(i,path:PathField(p+20),label:"変換先")}).Where(v=>File.Exists(v.path.Split('|')[0])).ToArray();
            var signature=string.Join("|",inputs.Select(v=>v.path+File.GetLastWriteTimeUtc(v.path.Split('|')[0]).Ticks));
            if(signature==checkedArms)return;checkedArms=signature;
            var missing=new List<string>();
            foreach(var input in inputs){
                try{
                    var rig=Sizing.ReadRig(context,input.path);
                    if(!Sizing.ArmStructure(rig))missing.Add("【No."+(input.i+1)+"】 "+input.label+" "+rig.name);
                }catch(Exception ex){Text(Log,ex.Message);}
            }
            if(missing.Count>0 && MessageBoxW(WindowHandle,string.Join("\r\n",missing)+"\r\n腕構造のチェックをスキップしますか？","腕の設定",0x24)==6)
                SendMessageW(Control(824),0xf1,new IntPtr(1),IntPtr.Zero);
        }
    }
}
