using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static void RibbonIcon(Button button,string label)
        {
            var icon=new VisualElement{pickingMode=PickingMode.Ignore};icon.AddToClassList("ribbon-icon");
            button.text="";button.Add(icon);
            button.Add(new Label(label){pickingMode=PickingMode.Ignore});button.tooltip=label;
            icon.generateVisualContent+=ctx=>{
                var p=ctx.painter2D;
                var ink=new Color32(47,64,82,255);var blue=new Color32(44,124,188,255);
                var green=new Color32(64,144,83,255);var gold=new Color32(223,173,82,255);
                void Path(Color c,bool fill,params float[] points){
                    p.lineWidth=1.6f;p.strokeColor=ink;p.fillColor=c;p.BeginPath();p.MoveTo(new Vector2(points[0],points[1]));
                    for(int i=2;i<points.Length;i+=2)p.LineTo(new Vector2(points[i],points[i+1]));
                    if(fill){p.ClosePath();p.Fill();}p.Stroke();
                }
                void Box(float x,float y,float w,float h,Color c)=>Path(c,true,x,y,x+w,y,x+w,y+h,x,y+h);
                if(label.Contains("保存")){
                    Box(4,2,17,19,blue);Box(7,3,10,5,Color.white);Box(7,12,11,7,Color.white);
                }else if(label.Contains("読込") || label=="開く"){
                    Path(gold,true,2,7,4,4,11,4,13,7,22,7,20,20,3,20);Path(blue,false,10,14,14,10,18,14);
                }else if(label=="登録" || label.Contains("選択")){
                    Path(green,true,2,12,5,9,10,14,19,3,22,6,10,21);
                }else if(label.Contains("列")){
                    Box(3,3,7,18,Color.white);Box(13,3,7,18,blue);Path(ink,false,6,12,17,12);
                    if(label.Contains("挿入"))Path(ink,false,12,7,12,17);
                }else if(label.Contains("コピー") || label.Contains("貼り")){
                    Box(3,3,12,14,Color.white);Box(8,7,12,14,gold);Path(ink,false,11,12,18,12);Path(ink,false,11,16,18,16);
                }else if(label.Contains("出力")){
                    Box(2,3,15,14,blue);Path(Color.white,true,7,6,7,14,13,10);Path(ink,false,13,19,21,19,18,16);
                }else if(label.Contains("反転")){
                    Path(blue,true,2,18,9,6,9,18);Path(green,true,14,18,14,6,21,18);
                }else if(label.Contains("切り")){
                    Path(ink,false,3,3,19,19);Path(ink,false,20,3,6,19);
                }else if(label.Contains("ブック") || label=="追加・削除"){
                    Path(gold,true,5,3,19,3,19,21,12,16,5,21);
                }else if(label.Contains("ボーン") || label.Contains("IK") || label.Contains("握り")){
                    Path(blue,true,4,17,7,13,10,13,15,7,15,4,18,2,22,5,21,8,18,9,11,17,10,21,6,22);
                }else{
                    Path(blue,true,3,16,12,4,21,16,12,21);Path(Color.white,false,7,15,12,8,17,15);
                }
            };
        }
    }
}
