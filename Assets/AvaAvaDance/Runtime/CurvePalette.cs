using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static EaseCurve Controls(int x1,int y1,int x2,int y2)=>new EaseCurve{
            custom=true,a=new Vector2(x1,y1)/127f,b=new Vector2(x2,y2)/127f};
        static readonly EaseCurve[] initialCurves={
            Controls(20,20,107,107),Controls(64,0,64,127),Controls(48,5,80,122),Controls(32,10,96,117),
            Controls(96,0,32,127),Controls(0,64,64,127),Controls(64,0,127,64)};
        readonly EaseCurve[] curveSlots=(EaseCurve[])initialCurves.Clone();
        readonly Button[] presetButtons=new Button[6];

        static CurveSet SetAxes(CurveSet previous,EaseCurve curve,int count){
            for(int i=0;i<count;i++)previous=CurveSet.With(previous,i,curve);
            return previous;
        }
        object ApplyDefaultCurve(object value){
            var curve=curveSlots[0];
            if(value is BonePose bone){bone.Curves=SetAxes(bone.Curves,curve,4);return bone;}
            if(value is CameraPose cam){cam.Curves=SetAxes(cam.Curves,curve,6);return cam;}
            return value is float weight?new MorphKey{Weight=weight,Curve=curve}:value;
        }

        void BuildCurvePalette(VisualElement tools)
        {
            for(int i=0;i<curveSlots.Length;i++){
                string json=WorkspacePreferences.GetString("Curve.Preset."+i);
                if(string.IsNullOrEmpty(json))continue;
                try{var saved=JsonUtility.FromJson<EaseCurve>(json);if(saved.custom && saved.Valid){curveSlots[i]=saved;if(i==0)customDefault=true;}}
                catch(ArgumentException){}
            }
            ribbonPalette=Box(tools,"curve-palette","curve-palette");
            var row=Box(ribbonPalette,"slots","row");
            for(int i=0;i<presetButtons.Length;i++){int index=i+1;presetButtons[i]=Action(row,index.ToString(),false,()=>WriteCurve(curveSlots[index]));}
            var actions=Box(ribbonPalette,"actions","row");
            allCurves=Action(actions,"ALL",false,()=>WriteCurve(SelectedCurve(),true));
            allCurves.tooltip="選択キーの移動XYZと回転に適用";
            Action(actions,"パレット設定",true,EditCurvePalette);
        }
        void EditCurvePalette()
        {
            var working=(EaseCurve[])curveSlots.Clone();
            ShowDialog("補間曲線パレット",out var body);
            var slot=new DropdownField("保存先",new[]{"既定","1","2","3","4","5","6"}.ToList(),0);body.Add(slot);
            var graph=Box(body,"palette-preview","curve");var inputs=new IntegerField[4];
            string[] labels={"始点側 X","始点側 Y","終点側 X","終点側 Y"};
            void Sync(){
                var c=working[slot.index];var points=new[]{c.First.x,c.First.y,c.Second.x,c.Second.y};
                for(int i=0;i<4;i++)inputs[i].SetValueWithoutNotify(Mathf.RoundToInt(points[i]*127));
                graph.MarkDirtyRepaint();
            }
            void Set(EaseCurve c){working[slot.index]=Controls(Mathf.RoundToInt(c.First.x*127),Mathf.RoundToInt(c.First.y*127),
                Mathf.RoundToInt(c.Second.x*127),Mathf.RoundToInt(c.Second.y*127));Sync();}
            for(int i=0;i<4;i++){
                var input=inputs[i]=new IntegerField(labels[i]){isDelayed=true};body.Add(input);
                input.RegisterValueChangedCallback(e=>{
                    input.SetValueWithoutNotify(Mathf.Clamp(e.newValue,0,127));
                    working[slot.index]=Controls(inputs[0].value,inputs[1].value,inputs[2].value,inputs[3].value);Sync();
                });
            }
            BindCurveGraph(graph,()=>true,()=>working[slot.index],Set,false);
            slot.RegisterValueChangedCallback(_=>Sync());Sync();
            if(CanEditCurve)Action(body,"選択キーの曲線を取得",true,()=>Set(SelectedCurve()));
            Action(body,"初期値",true,()=>Set(initialCurves[slot.index]));
            Action(body,"OK",true,()=>{
                for(int i=0;i<working.Length;i++)if(!working[i].Equals(curveSlots[i])){
                    curveSlots[i]=working[i];WorkspacePreferences.SetString("Curve.Preset."+i,JsonUtility.ToJson(working[i]));if(i==0)customDefault=true;
                }
                CloseDialog();
            });
            Action(body,"キャンセル",true,CloseDialog);
        }

        VisualElement CreateInlineCurve(VisualElement parent,TimeWindow window=null,Func<bool> available=null)
        {
            var box=Box(parent,"timeline-curve","timeline-curve");
            box.Add(new Label{name="curve-interval"});
            var line=Box(box,"curve-lane","row","curve-lane");
            var channel=new DropdownField(BoneChannels,0){name="curve-axis"};line.Add(channel);
            channel.RegisterValueChangedCallback(_=>{if(channel.index>=0 && channel.index<curveChannel.choices.Count)curveChannel.index=channel.index;});
            var graph=Box(line,"segment","curve","timeline-segment");
            IEnumerable<(int end,Rect bounds)> Segments(){
                if(!(selectedTrack is MotionTrack<BonePose> || selectedTrack is MotionTrack<CameraPose> || selectedTrack is MotionTrack<float>))yield break;
                int first=window?.First??firstFrame,spacing=window?.Spacing??FrameWidth;
                if(selectedTrack.Target?.clips.Count>0 && selectedTrack.LocalTime(frame,out int sourceTime))first+=sourceTime-frame;
                float width=graph.contentRect.width,height=Mathf.Max(1,graph.contentRect.height-12);
                int previous=-1;
                foreach(int end in selectedTrack.EditFrames){
                    float right=(end-first+.5f)*spacing;
                    if(previous>=0 && right>0){
                        float left=(previous-first+.5f)*spacing;if(left>=width)yield break;
                        yield return (end,new Rect(left,6,right-left,height));
                    }
                    previous=end;
                }
            }
            Rect active=default;
            bool Pick(Vector2 position){
                if(playing || importing || exporting || available!=null && !available())return false;
                foreach(var segment in Segments()){
                    var r=segment.bounds;if(position.x<r.xMin || position.x>r.xMax)continue;
                    selectedKeys.Clear();selectedKeys.Add((selectedTrack,segment.end));rangeSelected=true;active=r;
                    RefreshKeyButtons();SyncCurve();timeline.MarkDirtyRepaint();return true;
                }
                return false;
            }
            void Draw(Painter2D p){
                int selected=CurveFrame;
                foreach(var segment in Segments()){
                    var c=draggedTrack==selectedTrack && selectedTrack!=null && segment.end==selected?draft:CurveAtFrame(segment.end);
                    DrawBezier(p,segment.bounds,c,true);
                }
            }
            BindCurveGraph(graph,()=>CanEditCurve && (available==null || available()),SelectedCurve,curve=>WriteCurve(curve),true,()=>active,Pick,Draw);
            box.EnableInClassList("hidden",!ribbonProfile || !showInlineCurve);return box;
        }
        void RefreshCurveViews()
        {
            curveGraph?.MarkDirtyRepaint();
            void Refresh(VisualElement box){
                if(box==null)return;bool display=ribbonProfile && showInlineCurve && (box!=inlineCurve || !audioDockTab);
                box.EnableInClassList("hidden",!display);if(!display)return;
                int to=CurveFrame;int from=CanEditCurve?selectedTrack.EditFrames.Where(f=>f<to).DefaultIfEmpty(0).Max():0;
                var choice=box.Q<DropdownField>("curve-axis");if(choice.choices.Count!=curveChannel.choices.Count)choice.choices=curveChannel.choices.ToList();
                choice.SetValueWithoutNotify(curveChannel.value);choice.SetEnabled(selectedTrack is MotionTrack<BonePose> || selectedTrack is MotionTrack<CameraPose> || selectedTrack is MotionTrack<float>);
                box.Q<Label>("curve-interval").text=CanEditCurve?selectedTrack.Name+"　"+(selectedTrack.Target?.clips.Count>0?"ソース ":"")+from+" ～ "+to:"補間を編集するキーを選択";
                box.Q("segment").MarkDirtyRepaint();
            }
            Refresh(inlineCurve);Refresh(floatingTime?.CurveBox);
        }
    }
}
