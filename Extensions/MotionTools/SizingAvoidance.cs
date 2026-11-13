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
        readonly struct ArmPoint
        {
            readonly RigJoint a,b;
            internal readonly bool forearm;
            internal RigJoint EndJoint=>a==b?a:null;
            internal ArmPoint(RigJoint a,RigJoint b,bool forearm){this.a=a;this.b=b;this.forearm=forearm;}
            internal Vector3 Rest(RigProfile rig)=>Vector3.Lerp(SkeletonMotion.Rest(a,rig),SkeletonMotion.Rest(b,rig),.5f);
            internal Vector3 World(SkeletonMotion motion,uint t)=>Vector3.Lerp(motion.World(a,t).position,motion.World(b,t).position,.5f);
        }

        sealed class Obstacle
        {
            internal RigJoint anchor;
            internal RigShape shape;
            internal Vector3 center,axis,boxX,boxY,boxZ;
            internal float radius,segment;
        }

        static IEnumerable<Obstacle> Obstacles(Hand h)
        {
            var rig=h.actor.Target;var names=h.actor.Person.AvoidShapes??new[]{HeadShape};
            if(names.Contains(HeadShape)){
                var head=Joint(rig,"head");var crown=rig.Find("headTop","頭頂実体","頭頂");
                if(head!=null){
                    float r=crown!=null?Vector3.Distance(SkeletonMotion.Rest(head,rig),SkeletonMotion.Rest(crown,rig)):h.palm*1.75f;
                    yield return new Obstacle{anchor=head,shape=new RigShape{shape="sphere"},radius=r};
                }
            }
            foreach(var shape in rig.shapes??Array.Empty<RigShape>()){
                if(shape==null || !names.Contains(shape.name))continue;
                var bone=(uint)shape.jointIndex<rig.joints.Length && rig.joints[shape.jointIndex].name==shape.role?rig.joints[shape.jointIndex]:rig.Find(shape.role);
                if(bone==null)continue;
                var scale=new Vector3(bone.scaleX,bone.scaleY,bone.scaleZ);float unit=(float)rig.metersPerMotionUnit;
                Vector3 Read(double[] v,int i=0)=>new Vector3((float)v[i],(float)v[i+1],(float)v[i+2]);
                var obstacle=new Obstacle{anchor=bone,shape=shape,center=(shape.center?.Length==3?Read(shape.center):Vector3.Zero)*scale/unit};
                if(shape.shape=="box" && shape.boxAxes?.Length==9){
                    obstacle.boxX=Read(shape.boxAxes)*scale/unit;obstacle.boxY=Read(shape.boxAxes,3)*scale/unit;
                    obstacle.boxZ=Read(shape.boxAxes,6)*scale/unit;
                }else if((shape.shape=="sphere" || shape.shape=="capsule") && shape.radius>0){
                    var axis=(shape.axis?.Length==3?Read(shape.axis):Vector3.UnitY)*scale;
                    obstacle.radius=(float)shape.radius*Math.Max(Math.Abs(scale.X),Math.Max(Math.Abs(scale.Y),Math.Abs(scale.Z)))/unit;
                    obstacle.segment=Math.Max(0,(float)shape.height*axis.Length()/(2*unit)-obstacle.radius);
                    obstacle.axis=axis.LengthSquared()>1e-9f?Vector3.Normalize(axis):Vector3.UnitY;
                }else continue;
                yield return obstacle;
            }
        }

        // Candidate exits lie to the outside of the arm, in front, or behind.
        // Compare their travel over the whole contact interval so adjacent keys use one direction.
        static bool ExitGoals(Obstacle ob,SkeletonMotion motion,uint frame,Vector3 wrist,Vector3 shoulder,float length,
            float side,Vector3[] goals)
        {
            const float near=.97f,reach=.93f;
            var (position,rotation)=motion.World(ob.anchor,frame);
            var origin=position+Vector3.Transform(ob.center,rotation);
            var pt=Vector3.Transform(wrist-origin,Quaternion.Inverse(rotation));
            Vector3 offsetX,offsetFront,offsetBack;
            if(ob.shape.shape=="box"){
                var ax=ob.boxX;var ay=ob.boxY;var az=ob.boxZ;
                var nx=Vector3.Cross(ay,az);float volume=Vector3.Dot(ax,nx);
                if(Math.Abs(volume)<1e-9f)return false;
                float x=Vector3.Dot(pt,nx)/volume,y=Vector3.Dot(pt,Vector3.Cross(az,ax))/volume,z=Vector3.Dot(pt,Vector3.Cross(ax,ay))/volume;
                if(Math.Max(Math.Abs(x),Math.Max(Math.Abs(y),Math.Abs(z)))>near)return false;
                offsetX=pt+(side*near-x)*ax;offsetFront=pt+(near-z)*az;offsetBack=pt+(-near-z)*az;
            }else{
                bool capsule=ob.shape.shape=="capsule";var pivot=capsule?ob.axis*Math.Clamp(Vector3.Dot(pt,ob.axis),-ob.segment,ob.segment):Vector3.Zero;
                var local=pt-pivot;float distance=local.Length();
                if(distance>ob.radius*near)return false;
                float span=(capsule?distance:ob.radius)*near;
                float y=span>1e-8f?(capsule?Math.Abs(local.Y):local.Y)/span:0;
                float width=span*(float)Math.Acos(Math.Clamp(y,-1,1));
                offsetX=pivot+new Vector3(width*side,local.Y,local.Z);
                offsetFront=pivot+new Vector3(local.X,local.Y,width);offsetBack=pivot+new Vector3(local.X,local.Y,-width);
            }
            Vector3 Reachable(Vector3 offset){
                var goal=origin+Vector3.Transform(offset,rotation);var line=goal-shoulder;
                return length>0 && line.Length()>=length?shoulder+Vector3.Normalize(line)*(reach*length):goal;
            }
            goals[0]=Reachable(offsetX);goals[1]=Reachable(offsetFront);goals[2]=Reachable(offsetBack);return true;
        }

        static void AvoidArm(Hand h,uint[] times,CancellationToken stop)
        {
            var obstructions=Obstacles(h).ToArray();if(obstructions.Length==0 || times.Length==0)return;
            var points=new List<ArmPoint>{new ArmPoint(h.shoulder,h.elbow,false),new ArmPoint(h.elbow,h.elbow,false),
                new ArmPoint(h.elbow,h.target,true),new ArmPoint(h.target,h.target,true)};
            if(h.targetTip!=null)points.Add(new ArmPoint(h.targetTip,h.targetTip,true));
            var rig=h.actor.Target;var motion=h.actor.After;var rest=SkeletonMotion.Rest(h.shoulder,rig);
            var lengths=points.Select(p=>Vector3.Distance(rest,p.Rest(rig))).ToArray();
            float side=SkeletonMotion.Rest(h.target,rig).X>=rest.X?1:-1;
            var goals=new Vector3[3];
            bool Visit(uint t,float[] costs,int axis){
                var shoulder=motion.World(h.shoulder,t).position;bool contact=false;
                foreach(var ob in obstructions)for(int i=0;i<points.Count;i++){
                    var p=points[i];var from=p.World(motion,t);
                    if(!ExitGoals(ob,motion,t,from,shoulder,lengths[i],side,goals))continue;
                    contact=true;
                    if(costs!=null){for(int n=0;n<3;n++)costs[n]+=Vector3.Distance(from,goals[n]);}
                    else{
                        bool bend=p.forearm && h.actor.Before.Bent(h.sourceElbow,t);
                        if(motion.ReachPoint(h.shoulder,h.elbow,from,t,goals[axis],bend,.7f) && axis==0){
                            if(p.EndJoint==h.target || (p.EndJoint!=null && p.EndJoint==h.targetTip && bend))h.holdElbow.Add(t);
                            if(p.EndJoint!=h.elbow && !bend)h.holdShoulder.Add(t);
                        }
                    }
                }
                return contact;
            }
            int first=0;var totals=new float[3];
            for(int i=0;i<=times.Length;i++){
                stop.ThrowIfCancellationRequested();
                if(i<times.Length && Visit(times[i],totals,0))continue;
                int axis=totals[0]<totals[1] && totals[0]<totals[2]?0:totals[1]<totals[2]?1:2;
                for(int at=first;at<i;at++){stop.ThrowIfCancellationRequested();Visit(times[at],null,axis);}
                first=i+1;Array.Clear(totals,0,totals.Length);
            }
        }
    }
}
