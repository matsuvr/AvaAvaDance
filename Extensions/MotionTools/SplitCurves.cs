using System;
using System.Numerics;

namespace Ava.MotionTools
{
    static class SplitCurves
    {
        static (Vector2 a,Vector2 b) Points(byte[] row,int axis)
        {
            int first=47+axis;
            bool special=axis>=2 && (row[49]==0 && row[50]==0 || row[49]==99 && row[50]==15);
            return (new Vector2(row[first+(special?15:0)],row[first+4])/127f,new Vector2(row[first+8],row[first+12])/127f);
        }
        static float Parameter(Vector2 a,Vector2 b,float time)
        {
            float from=0,to=1;
            for(int i=0;i<20;i++){
                float u=(from+to)*.5f,v=1-u;float x=3*v*v*u*a.X+3*v*u*u*b.X+u*u*u;
                if(x<time)from=u;else to=u;
            }
            return (from+to)*.5f;
        }
        public static float Ease(byte[] row,int axis,float time)
        {
            var (a,b)=Points(row,axis);float u=Parameter(a,b,time),v=1-u;return 3*v*v*u*a.Y+3*v*u*u*b.Y+u*u*u;
        }
        static Vector2 Normalize(Vector2 p,Vector2 begin,Vector2 end)
        {
            float Axis(float value,float lo,float hi)=>Math.Abs(hi-lo)<1e-8f?0:Math.Max(0,Math.Min(1,(value-lo)/(hi-lo)));
            return new Vector2(Axis(p.X,begin.X,end.X),Axis(p.Y,begin.Y,end.Y));
        }
        static byte Pack(float n)=>(byte)Math.Round(n*127);
        static void Write(byte[] row,int axis,Vector2 a,Vector2 b)
        {
            int start=47+axis;row[start]=Pack(a.X);row[start+4]=Pack(a.Y);row[start+8]=Pack(b.X);row[start+12]=Pack(b.Y);
        }
        static void Redundancy(byte[] row,bool physicsOff)
        {
            for(int block=1;block<4;block++)Array.Copy(row,47+block,row,47+16*block,16-block);
            row[49]=physicsOff?(byte)99:(byte)0;row[50]=physicsOff?(byte)15:(byte)0;
        }
        public static void Divide(byte[] inserted,byte[] next,float at,bool priorPhysicsOff)
        {
            var original=(byte[])next.Clone();bool off=original[49]==99 && original[50]==15;
            for(int axis=0;axis<4;axis++){
                var (a,b)=Points(original,axis);float u=Parameter(a,b,at);
                var p=Vector2.Lerp(Vector2.Zero,a,u);var q=Vector2.Lerp(a,b,u);var r=Vector2.Lerp(b,Vector2.One,u);
                var left=Vector2.Lerp(p,q,u);var right=Vector2.Lerp(q,r,u);var point=Vector2.Lerp(left,right,u);
                Write(inserted,axis,Normalize(p,Vector2.Zero,point),Normalize(left,Vector2.Zero,point));
                Write(next,axis,Normalize(right,point,Vector2.One),Normalize(r,point,Vector2.One));
            }
            Redundancy(inserted,priorPhysicsOff);Redundancy(next,off);
        }
    }
}
