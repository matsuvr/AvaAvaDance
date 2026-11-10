using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance.Import
{
    internal sealed class LegacyDevice : IDisposable
    {
        [DllImport("d3d9.dll")] static extern IntPtr Direct3DCreate9(uint version);
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [StructLayout(LayoutKind.Sequential)]
        struct Presentation {
            public uint width,height,format,count,samples,quality,swap; public IntPtr window;
            public int windowed,depth; public uint depthFormat,flags,refresh,interval;
        }
        delegate int CreateDevice(IntPtr self,uint adapter,uint type,IntPtr window,uint flags,ref Presentation info,out IntPtr device);
        delegate uint Release(IntPtr self);
        IntPtr d3d;
        internal IntPtr Handle, Window;
        internal static T Method<T>(IntPtr obj,int index) where T:Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),index*IntPtr.Size));
        internal static void ReleaseCom(ref IntPtr ptr) { if(ptr!=IntPtr.Zero){Method<Release>(ptr,2)(ptr);ptr=IntPtr.Zero;} }
        internal LegacyDevice()
        {
            using var process=System.Diagnostics.Process.GetCurrentProcess();
            Window=process.MainWindowHandle;if(Window==IntPtr.Zero)Window=GetDesktopWindow();
            d3d=Direct3DCreate9(32);if(d3d==IntPtr.Zero)throw new IOException("Direct3D9が利用できません。");
            try {
                var pp=new Presentation {width=16,height=16,count=1,swap=1,window=Window,windowed=1};
                Marshal.ThrowExceptionForHR(Method<CreateDevice>(d3d,16)(d3d,0,1,Window,0x24,ref pp,out Handle));
            }catch {Dispose();throw;}
        }
        public void Dispose(){ReleaseCom(ref Handle);ReleaseCom(ref d3d);}
    }

    internal static class DirectXMesh
    {
        [DllImport("d3dx9_43.dll")] static extern int D3DXLoadMeshFromXInMemory(byte[] source,int size,uint options,IntPtr device,
            IntPtr adjacency,out IntPtr materials,IntPtr effects,out int count,out IntPtr mesh);
        delegate uint Size(IntPtr self);
        delegate int Clone(IntPtr self,uint options,uint fvf,IntPtr device,out IntPtr mesh);
        delegate int Lock(IntPtr self,uint flags,out IntPtr data);
        delegate int Unlock(IntPtr self);
        delegate IntPtr Buffer(IntPtr self);
        [StructLayout(LayoutKind.Sequential)]
        struct Surface { public Color diffuse,ambient,specular,emissive;public float power;public IntPtr texture; }

        internal static Mesh Load(byte[] source,out Color[] colors,out string[] maps)
        {
            using var device=new LegacyDevice();
            IntPtr original=IntPtr.Zero,mesh=IntPtr.Zero,materials=IntPtr.Zero;
            try {
                const uint systemMemory32=0x111; // D3DXMESH_SYSTEMMEM | D3DXMESH_32BIT
                Marshal.ThrowExceptionForHR(D3DXLoadMeshFromXInMemory(source,source.Length,systemMemory32,device.Handle,
                    IntPtr.Zero,out materials,IntPtr.Zero,out int count,out original));
                int nv=checked((int)LegacyDevice.Method<Size>(original,5)(original)),nf=checked((int)LegacyDevice.Method<Size>(original,4)(original));
                if(nv<1 || nf<1 || nv>1000000 || nf>2000000 || count<0 || count>8192)throw new IOException("Xメッシュのサイズが不正です。");
                Marshal.ThrowExceptionForHR(LegacyDevice.Method<Clone>(original,11)(original,systemMemory32,0x112,device.Handle,out mesh));
                void Read(int slot,Action<IntPtr> copy) {
                    Marshal.ThrowExceptionForHR(LegacyDevice.Method<Lock>(mesh,slot)(mesh,16,out var data));
                    try {copy(data);}finally{LegacyDevice.Method<Unlock>(mesh,slot+1)(mesh);}
                }
                var floats=new float[nv*8]; var indices=new int[nf*3];var attrs=new int[nf];
                Read(15,p=>Marshal.Copy(p,floats,0,floats.Length));Read(17,p=>Marshal.Copy(p,indices,0,indices.Length));Read(24,p=>Marshal.Copy(p,attrs,0,nf));
                if(indices.Any(i=>i<0 || i>=nv) || floats.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Xメッシュの頂点が不正です。");
                var vertices=new Vector3[nv];var normals=new Vector3[nv];var uv=new Vector2[nv];
                for(int i=0;i<nv;i++) {
                    int j=i*8;vertices[i]=new Vector3(floats[j],floats[j+1],floats[j+2]);normals[i]=new Vector3(floats[j+3],floats[j+4],floats[j+5]);
                    uv[i]=new Vector2(floats[j+6],1-floats[j+7]);
                }
                colors=Enumerable.Repeat(Color.white,Math.Max(1,count)).ToArray();maps=new string[colors.Length];
                if(count>0 && materials!=IntPtr.Zero) {
                    IntPtr start=LegacyDevice.Method<Buffer>(materials,3)(materials);
                    for(int i=0;i<count;i++) {
                        var m=Marshal.PtrToStructure<Surface>(IntPtr.Add(start,i*Marshal.SizeOf<Surface>()));colors[i]=m.diffuse;
                        if(m.texture!=IntPtr.Zero) { var bytes=new List<byte>();byte b;
                            while(bytes.Count<512 && (b=Marshal.ReadByte(m.texture,bytes.Count))!=0)bytes.Add(b);
                            maps[i]=MmdText.Decode(bytes.ToArray());
                        }
                    }
                }
                var subsets=Enumerable.Range(0,colors.Length).Select(_=>new List<int>()).ToArray();
                for(int i=0;i<nf;i++) {
                    if(attrs[i]<0 || attrs[i]>=subsets.Length)throw new InvalidDataException("X材質番号が不正です。");
                    var set=subsets[attrs[i]];set.Add(indices[3*i]);set.Add(indices[3*i+1]);set.Add(indices[3*i+2]);
                }
                var result=new Mesh {indexFormat=IndexFormat.UInt32,vertices=vertices,normals=normals,uv=uv,subMeshCount=subsets.Length};
                for(int i=0;i<subsets.Length;i++)result.SetTriangles(subsets[i],i);
                if(normals.All(n=>n.sqrMagnitude<.0001f))result.RecalculateNormals();
                result.RecalculateBounds();return result;
            } finally {LegacyDevice.ReleaseCom(ref mesh);LegacyDevice.ReleaseCom(ref materials);LegacyDevice.ReleaseCom(ref original);}
        }
    }
}
