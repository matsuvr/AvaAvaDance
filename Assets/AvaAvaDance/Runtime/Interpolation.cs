using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    [Serializable]
    internal struct EaseCurve
    {
        public bool custom;
        public Vector2 a, b;
        public Vector2 First => custom ? a : Vector2.one / 3;
        public Vector2 Second => custom ? b : Vector2.one * (2f / 3);

        public Vector2 Point(float u)
        {
            float v = 1 - u;
            return 3*v*v*u*First + 3*v*u*u*Second + u*u*u*Vector2.one;
        }

        public float Evaluate(float t)
        {
            if (!custom || t <= 0 || t >= 1) return t;
            float low = 0, high = 1;
            for (int i = 0; i < 16; i++)
            {
                float mid = (low + high) * .5f;
                if (Point(mid).x < t) low = mid; else high = mid;
            }
            return Point((low + high) * .5f).y;
        }
        public bool Valid => !custom || (a.x >= 0 && a.x <= 1 && a.y >= 0 && a.y <= 1 &&
            b.x >= 0 && b.x <= 1 && b.y >= 0 && b.y <= 1);
    }

    [Serializable]
    internal sealed class CurveSet
    {
        public EaseCurve[] channels;
        public byte[] raw;
        public int modified;
        public static EaseCurve Get(CurveSet set, int axis) =>
            set?.channels != null && axis < set.channels.Length ? set.channels[axis] : default;
        public static CurveSet With(CurveSet prior, int axis, EaseCurve curve)
        {
            var result = new CurveSet { channels = new EaseCurve[6], raw=prior?.raw, modified=(prior?.modified??0) | 1<<axis };
            if (prior?.channels != null) Array.Copy(prior.channels, result.channels, Math.Min(prior.channels.Length, 6));
            result.channels[axis] = curve;
            return result;
        }
        public static bool Valid(CurveSet set) => set?.channels == null || (set.channels.Length <= 6 && Array.TrueForAll(set.channels, c => c.Valid));
        public static Vector3 Position(Vector3 start, Vector3 end, CurveSet curves, float t) =>
            new Vector3(Mathf.Lerp(start.x, end.x, Get(curves,0).Evaluate(t)),
                Mathf.Lerp(start.y, end.y, Get(curves,1).Evaluate(t)), Mathf.Lerp(start.z, end.z, Get(curves,2).Evaluate(t)));
    }

    public sealed partial class DanceApp
    {
        VisualElement curveGraph;
        DropdownField curveChannel;
        Button copyCurveButton, pasteCurveButton, linearButton;
        Toggle autoCurve;
        EaseCurve? copiedCurve;
        EaseCurve draft;
        MotionTrack draggedTrack;
        VisualElement inlineCurve,ribbonPalette;
        Button allCurves;
        bool showInlineCurve,customDefault;
        static readonly List<string> MorphChannels = new List<string> { "ウェイト" };
        static readonly List<string> BoneChannels = new List<string> { "X軸移動", "Y軸移動", "Z軸移動", "回転" };
        static readonly List<string> CameraChannels = new List<string>(BoneChannels) { "距離", "視野角" };

        void BuildInterpolation(VisualElement sidebar)
        {
            var title = new Label("補間曲線操作"){name="curve-heading"}; title.AddToClassList("header"); sidebar.Add(title);
            var row = Box(sidebar, "interpolation", "interpolation");
            curveGraph = Box(row, "curve", "curve");
            var tools = Box(row, "interpolation-controls", "interpolation-controls");
            curveChannel = Choice(tools, BoneChannels.ToArray()); curveChannel.index = 3;
            curveChannel.RegisterValueChangedCallback(_ => RefreshCurveViews());
            copyCurveButton = Action(tools, "コピー", false, () => { copiedCurve = SelectedCurve(); SyncCurve(); });
            pasteCurveButton = Action(tools, "ペースト", false, () => { if (copiedCurve.HasValue) WriteCurve(copiedCurve.Value); });
            autoCurve=new Toggle("自動設定"); tools.Add(autoCurve);
            linearButton = Action(tools, "線形補間", false, () => WriteCurve(default));
            BindCurveGraph(curveGraph,()=>CanEditCurve,SelectedCurve,curve=>WriteCurve(curve),true);
            BuildCurvePalette(tools);
            inlineCurve=CreateInlineCurve(sidebar);
            inlineCurve.RemoveFromHierarchy();sidebar.Insert(sidebar.IndexOf(timeline)+1,inlineCurve);
        }

        IEnumerable<KeyChange> RegistrationEdits(MotionTrack track)
        {
            if(!track.LocalTime(frame,out int local) || track.Target?.muted==true) yield break;
            var value=track.RegistrationValue(frame);
            if(value==null){status.text="現在位置では選択中のクリップに登録できません。";yield break;}
            if(local>0 && track.EditKey(local)==null && (ribbonProfile || customDefault))value=ApplyDefaultCurve(value);
            if(track.Layers.Count>0)
            {
                yield return new KeyChange{Track=track, Layer=track.ActiveLayer, Frame=local, DocumentFrame=frame, After=value, PreviousDraft=track.CurrentValue};
                yield break;
            }
            if(!autoCurve.value || !(value is BonePose || value is CameraPose)) {
                yield return new KeyChange {Track=track,Frame=frame,After=track.WithCurve(frame,value),PreviousDraft=value};yield break;
            }
            var frames=track.Frames.Append(frame).Distinct().OrderBy(f=>f).ToList();
            int at=frames.IndexOf(frame);
            object Pose(int f)=>f==frame?value:track.KeyAt(f);
            Vector3 Position(int f)=>Pose(f) is BonePose b?b.Position:((CameraPose)Pose(f)).Center;
            for(int i=Math.Max(0,at-1);i<=Math.Min(frames.Count-1,at+2);i++) {
                int f=frames[i]; object pose=Pose(f);
                if(f>0 && i>0) {
                    int prev=frames[i-1];
                    CurveSet curves=pose is BonePose existing?existing.Curves:((CameraPose)pose).Curves;
                    for(int ch=0;ch<3;ch++) {
                        float delta=Position(f)[ch]-Position(prev)[ch];
                        bool start=i<2 || (Position(prev)[ch]-Position(frames[i-2])[ch])*delta<=0;
                        bool stop=i+1>=frames.Count || (Position(frames[i+1])[ch]-Position(f)[ch])*delta<=0;
                        if(Mathf.Abs(delta)>1e-6f && (start||stop))curves=CurveSet.With(curves,ch,new EaseCurve {
                            custom=true,a=new Vector2(1f/3,start?0:1f/3),b=new Vector2(2f/3,stop?1:2f/3)
                        });
                    }
                    if(pose is BonePose bp){bp.Curves=curves;pose=bp;}else {var cp=(CameraPose)pose;cp.Curves=curves;pose=cp;}
                }
                yield return new KeyChange {Track=track,Frame=f,After=pose,PreviousDraft=f==frame?value:null};
            }
        }

        int CurveFrame
        {
            get{
                int at=selectedTrack!=null && selectedTrack.LocalTime(frame,out int local)?local:-1;
                if(!ribbonProfile || !rangeSelected)return at;
                int closest=-1,distance=int.MaxValue;
                foreach(var k in selectedKeys)if(k.track==selectedTrack){
                    int d=Math.Abs(k.frame-at);
                    if(d<distance || d==distance && k.frame<closest){closest=k.frame;distance=d;}
                }
                return closest>=0?closest:at;
            }
        }
        bool CanEditCurve => !playing && !exporting && !importing && (CurveFrame>0 || ribbonProfile && CurveFrame==0) && (selectedTrack is MotionTrack<CameraPose> ||
            selectedTrack is MotionTrack<BonePose> || selectedTrack is MotionTrack<float>) && selectedTrack.EditKey(CurveFrame) != null;

        EaseCurve SelectedCurve()
        {
            return CurveAtFrame(CurveFrame);
        }
        EaseCurve CurveAtFrame(int local)
        {
            if (selectedTrack is MotionTrack<float>) return selectedTrack.CurveAt(local);
            object value = selectedTrack?.EditKey(local);
            var set = value is CameraPose cam ? cam.Curves : value is BonePose pose ? pose.Curves : null;
            return CurveSet.Get(set, Mathf.Max(0, curveChannel.index));
        }

        void SyncCurve()
        {
            var choices = selectedTrack == cameraTrack ? CameraChannels : selectedTrack is MotionTrack<float> ? MorphChannels : BoneChannels;
            if (curveChannel.choices.Count != choices.Count)
            {
                int index = Mathf.Clamp(curveChannel.index, 0, choices.Count - 1);
                curveChannel.choices = choices; curveChannel.SetValueWithoutNotify(choices[index]);
            }
            curveChannel.SetEnabled(CanEditCurve); copyCurveButton.SetEnabled(CanEditCurve);
            pasteCurveButton.SetEnabled(CanEditCurve && copiedCurve.HasValue); linearButton.SetEnabled(CanEditCurve);
            if(ribbonPalette!=null){foreach(var b in presetButtons)b.SetEnabled(CanEditCurve);allCurves.SetEnabled(CanEditCurve && !(selectedTrack is MotionTrack<float>));}
            RefreshCurveViews();
        }

        void WriteCurve(EaseCurve curve,bool all=false)
        {
            if(!CanEditCurve || all && selectedTrack is MotionTrack<float>)return;
            int axis=curveChannel.index;bool morph=selectedTrack is MotionTrack<float>;
            CurveSet Edited(CurveSet previous)=>all?SetAxes(previous,curve,4):CurveSet.With(previous,axis,curve);
            var keys=selectedKeys.ToArray();bool multi=ribbonProfile && rangeSelected && keys.Length>0;
            var targets=multi?keys:new[]{(track:selectedTrack,frame:CurveFrame)};
            var edits=new List<KeyChange>();
            foreach(var target in targets){
                var track=target.track;int local=target.frame;
                var value=track.EditKey(local);
                if(morph && track is MotionTrack<float> && track.KeyAt(local) is float weight)
                    value=new MorphKey{Weight=weight,Curve=curve};
                else if(!morph && value is CameraPose cam){cam.Curves=Edited(cam.Curves);value=cam;}
                else if(!morph && value is BonePose pose && (axis<4 || all)){pose.Curves=Edited(pose.Curves);value=pose;}
                else continue;
                edits.Add(new KeyChange{Track=track,Layer=track.ActiveLayer,Frame=local,DocumentFrame=frame,After=value});
            }
            CommitKeys(edits);
            if(multi){rangeSelected=true;selectedKeys.UnionWith(keys);RefreshKeyButtons();SyncCurve();}
        }

        static void DrawBezier(Painter2D p,Rect bounds,EaseCurve curve,bool handles){
            Vector2 Point(Vector2 v)=>new Vector2(bounds.xMin+v.x*bounds.width,bounds.yMax-v.y*bounds.height);
            var start=Point(Vector2.zero);var end=Point(Vector2.one);var a=Point(curve.First);var b=Point(curve.Second);
            if(handles){Line(p,start,a,Color.gray);Line(p,end,b,Color.gray);}
            p.lineWidth=2;p.strokeColor=new Color(.1f,.6f,.2f);
            p.BeginPath();p.MoveTo(start);p.BezierCurveTo(a,b,end);p.Stroke();
            if(handles)foreach(var control in new[]{a,b}){p.fillColor=new Color(.8f,.15f,.18f);p.BeginPath();p.Arc(control,3.5f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
        }

        void BindCurveGraph(VisualElement graph,Func<bool> enabled,Func<EaseCurve> read,Action<EaseCurve> write,bool keyEditor,
            Func<Rect> area=null,Func<Vector2,bool> pick=null,Action<Painter2D> drawIntervals=null)
        {
            bool held=false;int handle=0,local=0,axis=0;string layer=null,clip=null;
            MotionTrack track=null;EaseCurve edit=default;
            Rect Bounds()=>area!=null?area():new Rect(6,6,Mathf.Max(1,graph.contentRect.width-12),Mathf.Max(1,graph.contentRect.height-12));
            Vector2 Point(Vector2 v){var r=Bounds();return new Vector2(r.xMin+v.x*r.width,r.yMax-v.y*r.height);}
            bool SameKey()=>track==selectedTrack && track!=null && layer==track.ActiveLayer && clip==track.ActiveClip && local==CurveFrame && axis==curveChannel.index;
            void Move(Vector2 p){
                var r=Bounds();var v=new Vector2(Mathf.Clamp01((p.x-r.xMin)/Mathf.Max(1,r.width)),Mathf.Clamp01((r.yMax-p.y)/Mathf.Max(1,r.height)));
                edit=new EaseCurve{custom=true,a=handle==0?v:edit.First,b=handle==1?v:edit.Second};
                if(keyEditor){draft=edit;RefreshCurveViews();}else graph.MarkDirtyRepaint();
            }
            void Release(){
                if(!held)return;held=false;if(keyEditor && draggedTrack==track)draggedTrack=null;
                if(keyEditor)RefreshCurveViews();else graph.MarkDirtyRepaint();
            }
            graph.RegisterCallback<PointerDownEvent>(e=>{
                if(e.button!=0 || keyEditor && draggedTrack!=null || pick!=null && !pick(e.localPosition) || !enabled())return;
                edit=read();held=true;
                if(keyEditor){track=draggedTrack=selectedTrack;layer=track.ActiveLayer;clip=track.ActiveClip;local=CurveFrame;axis=curveChannel.index;}
                Vector2 position=e.localPosition;
                handle=(position-Point(edit.First)).sqrMagnitude<(position-Point(edit.Second)).sqrMagnitude?0:1;
                graph.CapturePointer(e.pointerId);Move(e.localPosition);e.StopPropagation();
            });
            graph.RegisterCallback<PointerMoveEvent>(e=>{if(held){Move(e.localPosition);e.StopPropagation();}});
            graph.RegisterCallback<PointerUpEvent>(e=>{
                if(!held || !graph.HasPointerCapture(e.pointerId))return;
                if(enabled() && (!keyEditor || SameKey()))write(edit);
                Release();graph.ReleasePointer(e.pointerId);e.StopPropagation();
            });
            graph.RegisterCallback<PointerCaptureOutEvent>(_=>Release());
            graph.RegisterCallback<DetachFromPanelEvent>(_=>Release());
            graph.generateVisualContent+=ctx=>{
                if(drawIntervals!=null){drawIntervals(ctx.painter2D);return;}
                if(!enabled())return;
                var c=keyEditor && draggedTrack==selectedTrack && draggedTrack!=null?draft:held?edit:read();
                DrawBezier(ctx.painter2D,Bounds(),c,true);
            };
        }
    }
}
