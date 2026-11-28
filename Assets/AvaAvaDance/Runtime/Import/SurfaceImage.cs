using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AvaAvaDance.Import
{
    internal static partial class DirectXImage
    {
        [DllImport("d3dx9_43.dll")]static extern int D3DXCreateCubeTextureFromFileInMemoryEx(IntPtr device,byte[] bytes,int size,uint edge,uint mips,uint usage,uint format,uint pool,uint filter,uint mipFilter,uint key,IntPtr info,IntPtr palette,out IntPtr texture);
        [DllImport("d3dx9_43.dll")]static extern int D3DXCreateVolumeTextureFromFileInMemoryEx(IntPtr device,byte[] bytes,int size,uint w,uint h,uint d,uint mips,uint usage,uint format,uint pool,uint filter,uint mipFilter,uint key,IntPtr info,IntPtr palette,out IntPtr texture);
        [StructLayout(LayoutKind.Sequential)]struct LockedRect {public int Pitch;public IntPtr Bytes;}
        [StructLayout(LayoutKind.Sequential)]struct LockedBox {public int Pitch,Slice;public IntPtr Bytes;}
        delegate int LockCube(IntPtr self,int face,uint level,out LockedRect data,IntPtr region,uint flags);
        delegate int UnlockCube(IntPtr self,int face,uint level);
        delegate int LockVolume(IntPtr self,uint level,out LockedBox data,IntPtr region,uint flags);
        delegate int UnlockVolume(IntPtr self,uint level);

        static Color32[] Pixels(IntPtr src,int width,int height,int depth,int pitch,int slice)
        {
            var colors=new Color32[checked(width*height*depth)];var row=new byte[width*4];
            for(int z=0;z<depth;z++)for(int y=0;y<height;y++){
                Marshal.Copy(IntPtr.Add(src,checked(z*slice+y*pitch)),row,0,row.Length);
                int at=(z*height+height-1-y)*width;
                for(int x=0;x<width;x++){int p=x*4;colors[at+x]=new Color32(row[p+2],row[p+1],row[p],row[p+3]);}
            }
            return colors;
        }

        internal static Texture ReadSurface(byte[] bytes,string dimension,bool mips)
        {
            if(dimension!="Cube" && dimension!="3D"){
                var image=new Texture2D(2,2,TextureFormat.RGBA32,mips);
                try{if(!image.LoadImage(bytes) && !image.LoadImage(Convert(bytes,".png")))throw new InvalidDataException("画像を読み込めません。");return image;}
                catch{UnityEngine.Object.Destroy(image);throw;}
            }
            Marshal.ThrowExceptionForHR(D3DXGetImageInfoFromFileInMemory(bytes,bytes.Length,out var info));
            if(info.width==0 || info.height==0 || info.width>8192 || info.height>8192 || info.depth>2048 ||
                (long)info.width*info.height*(dimension=="Cube"?6:Math.Max(1u,info.depth))>32L*1024*1024)throw new InvalidDataException("テクスチャの寸法が不正です。");
            using var device=new LegacyDevice();IntPtr surface=IntPtr.Zero;Texture result=null;
            try{
                if(dimension=="Cube"){
                    Marshal.ThrowExceptionForHR(D3DXCreateCubeTextureFromFileInMemoryEx(device.Handle,bytes,bytes.Length,info.width,1,0,21,2,1,1,0,IntPtr.Zero,IntPtr.Zero,out surface));
                    var image=new Cubemap((int)info.width,TextureFormat.RGBA32,mips);result=image;
                    for(int face=0;face<6;face++){
                        Marshal.ThrowExceptionForHR(LegacyDevice.Method<LockCube>(surface,19)(surface,face,0,out var locked,IntPtr.Zero,16));
                        try{image.SetPixelData(Pixels(locked.Bytes,(int)info.width,(int)info.width,1,locked.Pitch,0),0,(CubemapFace)face);}
                        finally{LegacyDevice.Method<UnlockCube>(surface,20)(surface,face,0);}
                    }
                    image.Apply(mips,true);
                }else{
                    Marshal.ThrowExceptionForHR(D3DXCreateVolumeTextureFromFileInMemoryEx(device.Handle,bytes,bytes.Length,info.width,info.height,info.depth,1,0,21,2,1,1,0,IntPtr.Zero,IntPtr.Zero,out surface));
                    var image=new Texture3D((int)info.width,(int)info.height,(int)info.depth,TextureFormat.RGBA32,mips);result=image;
                    Marshal.ThrowExceptionForHR(LegacyDevice.Method<LockVolume>(surface,19)(surface,0,out var locked,IntPtr.Zero,16));
                    try{image.SetPixels32(Pixels(locked.Bytes,image.width,image.height,image.depth,locked.Pitch,locked.Slice));}
                    finally{LegacyDevice.Method<UnlockVolume>(surface,20)(surface,0);}
                    image.Apply(mips,true);
                }
                return result;
            }catch{if(result!=null)UnityEngine.Object.Destroy(result);throw;}
            finally{LegacyDevice.ReleaseCom(ref surface);}
        }
    }
}
