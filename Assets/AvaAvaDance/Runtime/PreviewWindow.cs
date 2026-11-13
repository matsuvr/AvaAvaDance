using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ava.Extensions;
using UnityEngine;

namespace AvaAvaDance
{
    internal sealed class PreviewWindow : IDisposable
    {
        sealed class Host : ClassicWindow
        {
            [DllImport("user32")] static extern IntPtr GetParent(IntPtr child);
            [DllImport("user32")] static extern bool SetWindowPos(IntPtr window,IntPtr order,int x,int y,int w,int h,uint flags);
            IntPtr child;
            volatile bool top;
            internal IntPtr Canvas=>Interlocked.CompareExchange(ref child,IntPtr.Zero,IntPtr.Zero);

            internal Host(IWindowHost context,bool top):base(context){this.top=top;}
            internal void Show(bool visible)=>OpenWindow("AvaAvaDance プレビュー",640,480,false,visible);
            protected override void Build()
            {
                Interlocked.Exchange(ref child,Add(30,"STATIC",""));
                ApplyTop();
            }
            protected override void Layout(int w,int h)=>Place(30,0,0,w,h);
            protected override void Command(int id,int notification){}
            protected override void Closing()=>Interlocked.Exchange(ref child,IntPtr.Zero);
            internal void SetTop(bool value){top=value;OnWindow(ApplyTop);}
            void ApplyTop()=>SetWindowPos(GetParent(Canvas),new IntPtr(top?-1:-2),0,0,0,0,0x13);
        }

        readonly DanceApp app;
        readonly Func<RenderTexture> image;
        Host host;
        WindowSurface surface;
        internal bool Active=>host?.IsOpen==true;
        internal int PresentedFrames=>surface?.Frames??0;

        internal PreviewWindow(DanceApp app,Func<RenderTexture> image){this.app=app;this.image=image;}
        internal void Open(bool top)
        {
            Dispose();
            var current=host=new Host(app,top);
            current.Show(!Application.isBatchMode);
            surface=new WindowSurface(app,()=>current.Canvas,image);
        }
        internal void SetTop(bool top)=>host?.SetTop(top);
        internal void Synchronize(){if(host!=null && !Active)Dispose();}
        public void Dispose(){surface?.Dispose();surface=null;host?.Dispose();host=null;}
    }
}
