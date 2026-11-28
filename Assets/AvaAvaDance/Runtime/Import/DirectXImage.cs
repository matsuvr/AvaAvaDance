using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AvaAvaDance.Import
{
    internal static partial class DirectXImage
    {
        internal const string Extensions = "bmp;jpg;png;tga;dds;dib;pfm;hdr";
        [StructLayout(LayoutKind.Sequential)]
        struct Info { public uint width,height,depth,mips,format,resourceType,fileFormat; }
        [DllImport("d3dx9_43.dll")] static extern int D3DXGetImageInfoFromFileInMemory(byte[] data,int size,out Info info);
        [DllImport("d3dx9_43.dll")] static extern int D3DXCreateTextureFromFileInMemoryEx(IntPtr device,byte[] data,int size,uint width,
            uint height,uint mips,uint usage,uint format,uint pool,uint filter,uint mipFilter,uint key,IntPtr info,IntPtr palette,out IntPtr tex);
        [DllImport("d3dx9_43.dll")] static extern int D3DXSaveTextureToFileInMemory(out IntPtr buffer,int format,IntPtr texture,IntPtr palette);
        delegate uint Size(IntPtr buffer);
        delegate IntPtr Address(IntPtr buffer);

        internal static byte[] Convert(byte[] data,string extension)
        {
            int format=extension.ToLowerInvariant() switch {
                ".bmp"=>0,".jpg"=>1,".jpeg"=>1,".tga"=>2,".png"=>3,".dds"=>4,".dib"=>6,".hdr"=>7,".pfm"=>8,
                _=>throw new InvalidDataException("対応していない画像形式です。")
            };
            Marshal.ThrowExceptionForHR(D3DXGetImageInfoFromFileInMemory(data,data.Length,out var size));
            if(size.width==0 || size.height==0 || size.width>8192 || size.height>8192 || (long)size.width*size.height>32*1024*1024)
                throw new InvalidDataException("画像のサイズが範囲外です。");
            using var device=new LegacyDevice();
            IntPtr texture=IntPtr.Zero,blob=IntPtr.Zero;
            try {
                Marshal.ThrowExceptionForHR(D3DXCreateTextureFromFileInMemoryEx(device.Handle,data,data.Length,size.width,size.height,1,0,21,2,
                    1,1,0,IntPtr.Zero,IntPtr.Zero,out texture));
                Marshal.ThrowExceptionForHR(D3DXSaveTextureToFileInMemory(out blob,format,texture,IntPtr.Zero));
                int length=checked((int)LegacyDevice.Method<Size>(blob,4)(blob));
                var bytes=new byte[length];Marshal.Copy(LegacyDevice.Method<Address>(blob,3)(blob),bytes,0,length);return bytes;
            } finally {LegacyDevice.ReleaseCom(ref blob);LegacyDevice.ReleaseCom(ref texture);}
        }
    }
}
