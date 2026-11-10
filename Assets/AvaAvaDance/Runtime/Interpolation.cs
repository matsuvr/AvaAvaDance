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
        public static EaseCurve Get(CurveSet set, int axis) =>
            set?.channels != null && axis < set.channels.Length ? set.channels[axis] : default;
        public static CurveSet With(CurveSet prior, int axis, EaseCurve curve)
        {
            var result = new CurveSet { channels = new EaseCurve[6] };
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
        int draggedFrame, draggedAxis, draggedHandle;
        static readonly List<string> BoneChannels = new List<string> { "X軸移動", "Y軸移動", "Z軸移動", "回転" };
        static readonly List<string> CameraChannels = new List<string>(BoneChannels) { "距離", "視野角" };

        void BuildInterpolation(VisualElement sidebar)
        {
            var title = new Label("補間曲線操作"); title.AddToClassList("header"); sidebar.Add(title);
            var row = Box(sidebar, "interpolation", "interpolation");
            curveGraph = Box(row, "curve", "curve");
            curveGraph.generateVisualContent += DrawCurve;
            var tools = Box(row, "interpolation-controls", "interpolation-controls");
            curveChannel = Choice(tools, BoneChannels.ToArray()); curveChannel.index = 3;
            curveChannel.RegisterValueChangedCallback(_ => curveGraph.MarkDirtyRepaint());
            copyCurveButton = Action(tools, "コピー", false, () => { copiedCurve = SelectedCurve(); SyncCurve(); });
            pasteCurveButton = Action(tools, "ペースト", false, () => { if (copiedCurve.HasValue) WriteCurve(copiedCurve.Value); });
            autoCurve=new Toggle("自動設定"); tools.Add(autoCurve);
            linearButton = Action(tools, "線形補間", false, () => WriteCurve(default));
            curveGraph.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || !CanEditCurve) return;
                draft = SelectedCurve();
                Vector2 point = e.localPosition;
                draggedHandle = (point - GraphPoint(draft.First)).sqrMagnitude < (point - GraphPoint(draft.Second)).sqrMagnitude ? 0 : 1;
                draggedTrack = selectedTrack; draggedFrame = frame; draggedAxis = curveChannel.index;
                curveGraph.CapturePointer(e.pointerId); DragCurve(point); e.StopPropagation();
            });
            curveGraph.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (draggedTrack == null) return;
                DragCurve(e.localPosition); e.StopPropagation();
            });
            curveGraph.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!curveGraph.HasPointerCapture(e.pointerId)) return;
                if (draggedTrack == selectedTrack && draggedFrame == frame && draggedAxis == curveChannel.index)
                    WriteCurve(draft);
                draggedTrack = null; curveGraph.ReleasePointer(e.pointerId); curveGraph.MarkDirtyRepaint();
            });
            curveGraph.RegisterCallback<PointerCaptureOutEvent>(_ => { draggedTrack = null; curveGraph.MarkDirtyRepaint(); });
        }

        IEnumerable<KeyChange> RegistrationEdits(MotionTrack track)
        {
            var value=track.CurrentValue;
            if(!autoCurve.value || !(value is BonePose || value is CameraPose)) {
                yield return new KeyChange {Track=track,Frame=frame,After=value};yield break;
            }
            var frames=track.Frames.Append(frame).Distinct().OrderBy(f=>f).ToList();
            int at=frames.IndexOf(frame);
            object Pose(int f)=>f==frame?value:track.KeyAt(f);
            Vector3 Position(int f)=>Pose(f) is BonePose b?b.Position:((CameraPose)Pose(f)).Center;
            for(int i=Math.Max(0,at-1);i<=Math.Min(frames.Count-1,at+2);i++) {
                int f=frames[i]; object pose=Pose(f);
                if(f>0 && i>0) {
                    int prev=frames[i-1];
                    CurveSet curves=null;
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
                yield return new KeyChange {Track=track,Frame=f,After=pose};
            }
        }

        bool CanEditCurve => !playing && frame > 0 && (selectedTrack is MotionTrack<CameraPose> ||
            selectedTrack is MotionTrack<BonePose>) && selectedTrack.KeyAt(frame) != null;

        EaseCurve SelectedCurve()
        {
            object value = selectedTrack?.KeyAt(frame);
            var set = value is CameraPose cam ? cam.Curves : value is BonePose pose ? pose.Curves : null;
            return CurveSet.Get(set, Mathf.Max(0, curveChannel.index));
        }

        void SyncCurve()
        {
            var choices = selectedTrack == cameraTrack ? CameraChannels : BoneChannels;
            if (curveChannel.choices.Count != choices.Count)
            {
                int index = Mathf.Clamp(curveChannel.index, 0, choices.Count - 1);
                curveChannel.choices = choices; curveChannel.SetValueWithoutNotify(choices[index]);
            }
            curveChannel.SetEnabled(CanEditCurve); copyCurveButton.SetEnabled(CanEditCurve);
            pasteCurveButton.SetEnabled(CanEditCurve && copiedCurve.HasValue); linearButton.SetEnabled(CanEditCurve);
            curveGraph.MarkDirtyRepaint();
        }

        void WriteCurve(EaseCurve curve)
        {
            if (!CanEditCurve) return;
            int axis = curveChannel.index;
            if (selectedTrack.KeyAt(frame) is CameraPose cam)
            {
                cam.Curves = CurveSet.With(cam.Curves, axis, curve); WriteKey(selectedTrack, cam);
            }
            else if (selectedTrack.KeyAt(frame) is BonePose pose)
            {
                pose.Curves = CurveSet.With(pose.Curves, axis, curve); WriteKey(selectedTrack, pose);
            }
        }

        Vector2 GraphPoint(Vector2 v) => new Vector2(6 + v.x * (curveGraph.contentRect.width - 12),
            6 + (1-v.y) * (curveGraph.contentRect.height - 12));

        void DragCurve(Vector2 p)
        {
            var point = new Vector2(Mathf.Clamp01((p.x - 6) / (curveGraph.contentRect.width - 12)),
                1 - Mathf.Clamp01((p.y - 6) / (curveGraph.contentRect.height - 12)));
            draft = new EaseCurve { custom = true, a = draggedHandle == 0 ? point : draft.First,
                b = draggedHandle == 1 ? point : draft.Second };
            curveGraph.MarkDirtyRepaint();
        }

        void DrawCurve(MeshGenerationContext ctx)
        {
            if (!CanEditCurve) return;
            var c = draggedTrack == selectedTrack && draggedFrame == frame ? draft : SelectedCurve();
            var p = ctx.painter2D;
            var start = GraphPoint(Vector2.zero); var end = GraphPoint(Vector2.one);
            var a = GraphPoint(c.First); var b = GraphPoint(c.Second);
            Line(p, start, a, Color.gray); Line(p, end, b, Color.gray);
            p.lineWidth = 2; p.strokeColor = new Color(.1f,.6f,.2f);
            p.BeginPath(); p.MoveTo(start); p.BezierCurveTo(a, b, end); p.Stroke();
            foreach (var handle in new[] { a, b })
            {
                p.fillColor = new Color(.8f,.15f,.18f); p.BeginPath();
                p.Arc(handle, 3.5f, Angle.Degrees(0), Angle.Degrees(360)); p.Fill();
            }
        }
    }
}
