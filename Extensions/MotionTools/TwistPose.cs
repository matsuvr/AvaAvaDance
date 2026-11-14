using System;
using System.Numerics;

namespace Ava.MotionTools
{
    internal static class TwistPose
    {
        const double Radians = Math.PI / 180;
        static Quaternion Around(Vector3 axis,double degrees) => Quaternion.CreateFromAxisAngle(axis,(float)(degrees*Radians));
        static Vector3 Turn(Quaternion q,Vector3 v) => Vector3.Transform(v,q);
        static Quaternion Aim(Vector3 from,Vector3 to) => SkeletonMotion.Between(from,to);
        static double Angle(Vector3 from,Vector3 to) =>
            Math.Atan2(Vector3.Cross(from,to).Length(),Vector3.Dot(from,to))/Radians;

        static double SignedAngle(Quaternion q,Vector3 axis)
        {
            if(q.W<0)q=Quaternion.Negate(q);
            var vector=new Vector3(q.X,q.Y,q.Z);
            return 2*Math.Atan2(vector.Length(),q.W)*Math.Sign(Vector3.Dot(vector,axis))/Radians;
        }

        static Vector3 Up(Vector3 axis)
        {
            var up=Vector3.Cross(axis,-Vector3.UnitZ);
            return up.LengthSquared()>1e-8f?Vector3.Normalize(up):Vector3.UnitY;
        }

        // Isolate yaw and elevation before estimating roll. They are independently registered channels.
        static double InitialRoll(Quaternion orientation,Vector3 axis)
        {
            var aim=Turn(orientation,axis);
            var swing=Aim(axis,aim);
            var horizontal=new Vector3(aim.X,aim.Y,0);
            var rest=new Vector3(axis.X,axis.Y,0);
            if(horizontal.LengthSquared()<1e-8f || rest.LengthSquared()<1e-8f)
                return SignedAngle(Quaternion.Inverse(swing)*orientation,axis);
            var yaw=Aim(rest,horizontal);
            var elevation=Aim(axis,Turn(swing*Quaternion.Inverse(yaw),axis));
            return SignedAngle(Quaternion.Inverse(elevation)*orientation*Quaternion.Inverse(yaw),axis);
        }

        // Fit the axial joint against the two directions carried by its child. A flexed elbow
        // prioritizes forearm direction while the palm and a straight elbow also constrain roll.
        static (double angle,double score) Fit(Vector3 axis,Vector3 forward,Vector3 up,Quaternion target,
            Quaternion child,double seed,double forwardWeight,bool wrist)
        {
            var targetX=Turn(target,forward);
            var targetY=Turn(target,up);
            var childX=Turn(child,forward);
            var childY=Turn(child,up);
            (double x,double y) Deviation(double angle){
                var roll=Around(axis,angle);
                return (Angle(Turn(roll,childX),targetX),Angle(Turn(roll,childY),targetY));
            }
            var initial=Deviation(wrist?seed:0);
            var steps=new[]{initial.x,-initial.x,initial.y,-initial.y};
            int count=wrist?2:4;
            double best=seed,bestScore=-2,error=initial.x;
            int halves=0;
            for(int round=0;round<50;round++){
                bool improved=false;
                for(int i=0;i<count;i++){
                    double trial=(wrist || round>0?best:0)+steps[i];
                    var delta=Deviation(trial);
                    double score=forwardWeight*Math.Cos(delta.x*Radians)+(1-forwardWeight)*Math.Cos(delta.y*Radians);
                    if(score<=bestScore+1e-14)continue;
                    best=trial;bestScore=score;error=delta.x;improved=true;
                    if(round>0)break;
                }
                if(bestScore>=Math.Cos(Radians))break;
                if(round==0 || improved){
                    steps[0]=error;steps[1]=-error;steps[2]=2;steps[3]=-2;count=4;halves=0;
                }else{
                    if(++halves>=3)break;
                    for(int i=0;i<count;i++)steps[i]*=.5;
                }
            }
            return (best,bestScore);
        }

        internal static (Quaternion arm,Quaternion armRoll,Quaternion elbow,Quaternion wristRoll,Quaternion wrist) Solve(
            Vector3 upper,Vector3 lower,Vector3 palm,Quaternion arm,Quaternion upperRoll,Quaternion elbow,Quaternion lowerRoll,Quaternion wrist)
        {
            var shoulder=Aim(upper,Turn(arm,upper));
            var target=Quaternion.Normalize(Quaternion.Inverse(shoulder)*arm*upperRoll*elbow);
            var hinge=Up(lower);
            var aim=Turn(target,lower);
            double seed=InitialRoll(arm,upper);
            // Find the elbow angle on the cone swept out by the upper-arm twist.
            var tangent=Vector3.Cross(hinge,lower);
            double p=Vector3.Dot(upper,lower),q=Vector3.Dot(upper,tangent);
            double reach=Math.Sqrt(p*p+q*q);
            double bend=Math.Atan2(q,p)/Radians+(reach<1e-8?0:Math.Acos(Math.Clamp(Vector3.Dot(upper,aim)/reach,-1,1))/Radians);
            if(Vector3.DistanceSquared(upper,lower)<1e-10f)bend=Angle(lower,aim);
            else if(Angle(lower,Turn(elbow,lower))<.0001)bend=0;
            var elbowSwing=Around(hinge,bend);
            double weight=Math.Min(1,.5+Math.Abs(bend)/40);
            var armFit=Fit(upper,lower,hinge,target,elbowSwing,seed,weight,false);
            if(Math.Abs(bend)<20 && Vector3.Dot(new Vector3(elbow.X,elbow.Y,elbow.Z),hinge)<0){
                double other=2*Math.Atan2(q,p)/Radians-bend;
                var alternative=Around(hinge,other);
                var otherFit=Fit(upper,lower,hinge,target,alternative,seed,weight,false);
                if(otherFit.score>armFit.score){armFit=otherFit;elbowSwing=alternative;}
            }
            var armTwist=Around(upper,armFit.angle);

            var desired=Quaternion.Normalize(Quaternion.Inverse(armTwist*elbowSwing)*target*lowerRoll*wrist);
            if(Angle(lower,Turn(desired,lower))<.0001)
                return (shoulder,armTwist,elbowSwing,desired,Quaternion.Identity);
            var wristSeed=SignedAngle(lowerRoll,lower)+InitialRoll(wrist,lower);
            var baseline=Quaternion.Inverse(desired)*Around(lower,wristSeed);
            var palmUp=Up(palm);
            Quaternion WristSwing(Vector3 probe){
                var correction=Aim(Turn(baseline,lower+probe),probe);
                return Aim(lower,Turn(correction,lower));
            }
            var hand=WristSwing(palm);
            var fit=Fit(lower,palm,palmUp,desired,hand,wristSeed,.5,true);
            if(fit.score<Math.Cos(2*Radians)){
                var alternative=WristSwing(palmUp);
                var other=Fit(lower,palm,palmUp,desired,alternative,wristSeed,.5,true);
                if(other.score>fit.score){fit=other;hand=alternative;}
            }
            return (shoulder,armTwist,elbowSwing,Around(lower,fit.angle),hand);
        }
    }
}
