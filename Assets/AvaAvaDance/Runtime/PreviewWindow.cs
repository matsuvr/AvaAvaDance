using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AvaAvaDance
{
    // The player keeps the preview as a render texture. Present the same pixels in a normal
    // resizable Win32 window, without taking focus from the editor when updating the image.
    internal sealed class PreviewWindow : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Rectangle { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct Bitmap { public uint size; public int width,height; public ushort planes,bits; public uint compression,bytes; public int xppm,yppm; public uint colors,important; }
        [DllImport("user32", CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle,string cls,string title,uint style,
            int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32")] static extern bool SetWindowPos(IntPtr window,IntPtr order,int x,int y,int w,int h,uint flags);
        [DllImport("user32")] static extern bool GetClientRect(IntPtr window,out Rectangle rect);
        [DllImport("user32")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32")] static extern int StretchDIBits(IntPtr dc,int x,int y,int width,int height,int sx,int sy,int sw,int sh,
            IntPtr data,ref Bitmap info,uint usage,uint op);

        IntPtr window;
        Texture2D pixels;
        byte[] buffer;
        GCHandle pin;
        double nextDraw;
        internal bool Active => window!=IntPtr.Zero && IsWindow(window);

        internal void Open(bool top)
        {
            Dispose();
            window=CreateWindowExW(top?8u:0u,"STATIC","AvaAvaDance プレビュー",Application.isBatchMode?0x00cf0000u:0x10cf0000u,
                unchecked((int)0x80000000),0,640,480,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(window==IntPtr.Zero)throw new IOException("プレビューウィンドウを開けません。");
        }
        internal void SetTop(bool top) { if(Active)SetWindowPos(window,new IntPtr(top?-1:-2),0,0,0,0,0x13); }
        internal void Draw(RenderTexture source)
        {
            if(window==IntPtr.Zero)return;
            if(!Active){Dispose();return;}
            if(source==null || IsIconic(window) || Time.unscaledTimeAsDouble<nextDraw)return;
            nextDraw=Time.unscaledTimeAsDouble+1/30.0;
            if(pixels==null || source.width!=pixels.width || source.height!=pixels.height) {
                if(pixels!=null)UnityEngine.Object.Destroy(pixels);
                if(pin.IsAllocated)pin.Free();
                pixels=new Texture2D(source.width,source.height,TextureFormat.BGRA32,false);
                buffer=new byte[checked(source.width*source.height*4)];pin=GCHandle.Alloc(buffer,GCHandleType.Pinned);
            }
            var prior=RenderTexture.active;
            try { RenderTexture.active=source; pixels.ReadPixels(new Rect(0,0,pixels.width,pixels.height),0,0,false); }
            finally {RenderTexture.active=prior;}
            pixels.GetRawTextureData<byte>().CopyTo(buffer);
            if(!GetClientRect(window,out var rect))return;
            var header=new Bitmap {size=40,width=pixels.width,height=pixels.height,planes=1,bits=32};
            var dc=GetDC(window);if(dc==IntPtr.Zero)return;
            try { StretchDIBits(dc,0,0,rect.right,rect.bottom,0,0,pixels.width,pixels.height,pin.AddrOfPinnedObject(),ref header,0,0xcc0020); }
            finally {ReleaseDC(window,dc);}
        }
        public void Dispose()
        {
            if(Active)DestroyWindow(window);window=IntPtr.Zero;
            if(pixels!=null)UnityEngine.Object.Destroy(pixels);pixels=null;
            if(pin.IsAllocated)pin.Free();buffer=null;
        }
    }
}
