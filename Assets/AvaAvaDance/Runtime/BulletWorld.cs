using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnityEngine;

namespace AvaAvaDance
{
    internal sealed class BulletWorld : SafeHandleZeroOrMinusOneIsInvalid
    {
        const string Dll = "AvaBullet";
        [StructLayout(LayoutKind.Sequential)]
        internal struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Pose(Vector3 position, Quaternion rotation) { Position=position; Rotation=rotation; }
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Body
        {
            public Pose Pose;
            public Vector3 Size;
            public float Mass, LinearDamping, AngularDamping, Friction, Restitution, GravityScale;
            public int Shape, Group, Mask, Restricted;
        }
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int ava_bullet_version();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_bullet_create();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void ava_bullet_destroy(IntPtr world);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ava_bullet_body(BulletWorld world,ref Body body);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ava_bullet_allow(BulletWorld world,int collider,int body);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ava_bullet_joint(BulletWorld world,int parent,int child,ref Pose pivot,ref Vector3 limits,float spring,float damping);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void ava_bullet_drive(BulletWorld world,int id,int follow,ref Pose target);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void ava_bullet_transport(BulletWorld world,int body,int anchor,ref Pose delta);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void ava_bullet_step(BulletWorld world,ref Vector3 gravity);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void ava_bullet_read(BulletWorld world,int id,out Pose pose);

        internal BulletWorld():base(true)
        {
            SetHandle(ava_bullet_create());
            if(IsInvalid)throw new InvalidOperationException("Bulletワールドを作成できません。");
        }
        protected override bool ReleaseHandle() { ava_bullet_destroy(handle);return true; }
        internal int Add(Body body) {
            int id=ava_bullet_body(this,ref body);
            if(id<0)throw new InvalidOperationException("Bullet剛体を作成できません。"); return id;
        }
        internal void Allow(int collider,int body) {
            if(ava_bullet_allow(this,collider,body)==0)throw new InvalidOperationException("Bullet衝突ペアを登録できません。");
        }
        internal void Joint(int parent,int child,Pose pivot,Vector3 limits,float spring,float damping) {
            if(ava_bullet_joint(this,parent,child,ref pivot,ref limits,spring,damping)==0)
                throw new InvalidOperationException("Bulletジョイントを作成できません。");
        }
        internal void Drive(int id,bool follow,Pose target) => ava_bullet_drive(this,id,follow?1:0,ref target);
        internal void Transport(int body,int anchor,Pose delta) => ava_bullet_transport(this,body,anchor,ref delta);
        internal void Step(Vector3 gravity) => ava_bullet_step(this,ref gravity);
        internal Pose Read(int id) { ava_bullet_read(this,id,out var pose);return pose; }
    }
}
