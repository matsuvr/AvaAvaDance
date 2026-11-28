using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace AvaAvaDance.Import
{
    public static partial class FbxNative
    {
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_load_motion(byte[] data,int size,StringBuilder error,int capacity);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_take_range(IntPtr scene,out double begin,out double end);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int ava_motion_pose(IntPtr scene,double time,[MarshalAs(UnmanagedType.I1)] bool rest,[Out] float[] values,int nodes);

        internal static void ReadMotion(byte[] bytes, string meta, Action<HumanoidSkeletonMotion> bake)
        {
            var error = new StringBuilder(2048);
            var scene = ava_load_motion(bytes,bytes.Length,error,error.Capacity);
            if (scene == IntPtr.Zero) throw new InvalidDataException("FBX を読み込めません。 "+error);
            try
            {
                if (ava_take_range(scene,out double begin,out double end)==0 || !double.IsFinite(begin) || !double.IsFinite(end) || end<begin)
                    throw new InvalidDataException("FBX にアニメーションテイクがありません。");
                int count = ava_nodes(scene);
                var parents = new int[count];
                var names = new string[count];
                var data = new float[10*count];
                for (int i=0; i<count; i++) { names[i]=Utf8(ava_name(scene,i)); parents[i]=ava_parent(scene,i); }
                void Pose(double seconds, bool rest, Matrix4x4[] local)
                {
                    if (ava_motion_pose(scene,begin+seconds,rest,data,count)==0) throw new InvalidDataException("FBX の姿勢を取得できません。");
                    for (int i=0; i<count; i++)
                    {
                        int k = i*10;
                        local[i] = Matrix4x4.TRS(new Vector3(data[k],data[k+1],data[k+2]),
                            new Quaternion(data[k+3],data[k+4],data[k+5],data[k+6]),new Vector3(data[k+7],data[k+8],data[k+9]));
                    }
                }
                var bind = new Matrix4x4[count];
                Pose(0,true,bind);
                bake(new HumanoidSkeletonMotion(names,parents,bind,end-begin,(t,m)=>Pose(t,false,m),HumanoidPreview.BoneMap(meta)));
            }
            finally { ava_free(scene); }
        }
    }
}
