using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Ava.Extensions;

namespace Ava.MotionTools
{
    public static partial class Sizing
    {
        sealed class Landmark
        {
            internal SkeletonMotion Before, After;
            internal RigJoint Source, Target;
            internal float SourceOffset, TargetOffset;
            internal bool Face;
            internal double HeadRatio, BodyRatio;
            internal Vector3 Sample(bool after,uint time)
            {
                var pose=(after?After:Before).World(after?Target:Source,time);
                var joint=after?Target:Source;
                var localUp=Vector3.Transform(Vector3.UnitY,Quaternion.Inverse(SkeletonMotion.RestRotation(joint)));
                return pose.position+Vector3.Transform(localUp*(after?TargetOffset:SourceOffset),pose.rotation);
            }
        }

        static IEnumerable<Landmark> CameraLandmarks(IPluginContext context,ActorInput actor)
        {
            var source=string.IsNullOrWhiteSpace(actor.Person.CameraSource)?actor.Source:ReadRig(context,actor.Person.CameraSource);
            var before=new SkeletonMotion(source,actor.Original);
            var target=actor.Target;
            float RestY(RigJoint joint,RigProfile rig)=>(float)(joint.y/rig.metersPerMotionUnit);
            (float crown,float height,float face) Measure(RigProfile rig,float offset)
            {
                var head=Joint(rig,"head");var neck=Joint(rig,"neck");
                float floor=rig.joints.Min(j=>RestY(j,rig));
                float headY=head==null?rig.joints.Max(j=>RestY(j,rig)):RestY(head,rig);
                float chin=neck==null?headY-(headY-floor)*.08f:RestY(neck,rig);
                var top=rig.Find("headTop","頭頂実体","頭頂");
                float crown=top!=null?RestY(top,rig):headY+Math.Max(0,headY-chin);
                return (crown-headY+offset,crown-floor+offset,crown-chin+offset);
            }
            var a=Measure(source,0);var b=Measure(target,(float)actor.Person.HeightOffset);
            foreach(string role in new[]{"head","headTop","neck","leftEye","rightEye","chest","spine","hips","leftUpperLeg","rightUpperLeg","leftFoot","rightFoot","leftToes","rightToes","leftHand","rightHand"}){
                bool crown=role=="headTop";var src=Joint(source,crown?"head":role);var dst=Joint(target,crown?"head":role);
                if(src==null || dst==null)continue;
                yield return new Landmark{Before=before,After=actor.After,Source=src,Target=dst,SourceOffset=crown?a.crown:0,
                    TargetOffset=crown?b.crown:0,Face=crown || role=="head" || role=="neck" || role.EndsWith("Eye",StringComparison.Ordinal),
                    HeadRatio=Ratio(b.face,a.face),BodyRatio=Ratio(b.height,a.height)};
            }
        }

        static VmdDocument AdjustCamera(IPluginContext context,List<ActorInput> actors,SizingOptions options,CancellationToken stop)
        {
            var camera=VmdDocument.Read(options.Camera);
            var marks=actors.SelectMany(actor=>CameraLandmarks(context,actor)).ToArray();
            if(marks.Length==0)return camera;
            foreach(var row in camera.Records(MotionChannel.Camera)){
                stop.ThrowIfCancellationRequested();
                uint time=VmdDocument.Frame(MotionChannel.Camera,row);
                var center=new Vector3(BitConverter.ToSingle(row,8),BitConverter.ToSingle(row,12),BitConverter.ToSingle(row,16));
                var angles=Quaternion.CreateFromYawPitchRoll(BitConverter.ToSingle(row,24),-BitConverter.ToSingle(row,20),BitConverter.ToSingle(row,28));
                var rotation=new Quaternion(-angles.X,angles.Y,angles.Z,-angles.W);
                var inverse=Quaternion.Inverse(rotation);
                float distance=BitConverter.ToSingle(row,4);
                uint fov=BitConverter.ToUInt32(row,56);
                Vector3 Project(Vector3 point,Vector3 aim,float length,uint angle){
                    var p=Vector3.Transform(point-aim,inverse);
                    double depth=length>0?length-p.Z:p.Z-length;
                    double half=Math.Tan(Math.Max(1,Math.Min(179,angle*.98))*Math.PI/360);
                    double extent=Math.Max(.001,row[60]==0?depth:Math.Abs(length))*half;
                    return new Vector3((float)(p.X/(extent*16/9)),(float)(p.Y/extent),(float)depth);
                }
                var src=marks.Select(m=>m.Sample(false,time)).ToArray();
                var dst=marks.Select(m=>m.Sample(true,time)).ToArray();
                var projected=src.Select(p=>Project(p,center,distance,fov)).ToArray();
                double Centrality(int i)=>projected[i].X*projected[i].X+projected[i].Y*projected[i].Y;
                var visible=Enumerable.Range(0,marks.Length).Where(i=>projected[i].Z>.001 && projected[i].Z<50000 &&
                    Math.Abs(projected[i].X)<=1 && Math.Abs(projected[i].Y)<=1.2).ToArray();
                if(visible.Length==0)visible=new[]{Enumerable.Range(0,marks.Length).OrderBy(i=>projected[i].Z<=0).ThenBy(Centrality).First()};
                int focus=visible.OrderBy(i=>!marks[i].Face).ThenBy(Centrality).First();
                var edges=visible.OrderBy(i=>projected[i].Y).ToArray();
                int bottom=edges[0],top=edges[edges.Length-1];
                double Span(Vector3[] points)=>Vector3.Distance(points[top],points[focus])+Vector3.Distance(points[bottom],points[focus]);
                var refMark=marks[focus];
                double ratio=visible.All(i=>marks[i].Face)?refMark.HeadRatio:Span(src)>1e-6?Span(dst)/Span(src):refMark.BodyRatio;
                if(!(ratio>0) || double.IsInfinity(ratio))ratio=1;
                double limit=options.DistanceLimit;
                double lengthRatio=limit<5?Math.Max(1/limit,Math.Min(limit,ratio)):ratio;
                double positionRatio=limit<5 && lengthRatio!=ratio && refMark.BodyRatio>0?
                    Math.Min(refMark.BodyRatio,Math.Max(1/refMark.BodyRatio,ratio)):ratio;
                var arm=Vector3.Transform(Vector3.UnitZ*distance,rotation);
                float Aim(float focusPosition,float orbit,Func<Vector3,float> axis){
                    double translation=visible.Average(i=>(double)axis(dst[i])-ratio*axis(src[i]));
                    return (float)(translation+focusPosition*ratio+orbit*(ratio-positionRatio));
                }
                var aim=new Vector3(Aim(center.X,arm.X,p=>p.X),Aim(center.Y,arm.Y,p=>p.Y),Aim(center.Z,arm.Z,p=>p.Z));
                float length=distance*(float)lengthRatio;
                if(limit>=5 && top!=bottom){
                    float expected=projected[top].Y-projected[bottom].Y;
                    float increment=(float)((marks[top].BodyRatio+marks[bottom].BodyRatio)/10)*Math.Sign(length);
                    for(int i=0;i<20 && Project(dst[top],aim,length,fov).Y-Project(dst[bottom],aim,length,fov).Y>=expected+.2f;i++){
                        length+=increment;if(i%9==0 && fov<179)fov++;
                    }
                }
                VmdDocument.Number(row,8,aim.X);VmdDocument.Number(row,12,aim.Y);VmdDocument.Number(row,16,aim.Z);
                VmdDocument.Number(row,4,length);Buffer.BlockCopy(BitConverter.GetBytes(fov),0,row,56,4);
            }
            return camera;
        }
    }
}
