using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AvaAvaDance
{
    internal sealed class WindowSurface : IDisposable
    {
        const string Bridge = "AvaRenderBridge";
        [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl)] static extern int ava_present_open(IntPtr window);
        [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl)] static extern int ava_present_queue(int id, IntPtr texture);
        [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl)] static extern void ava_present_close(int id);
        [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr ava_present_event();
        [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl)] static extern int ava_present_frames(int id, out int result);
        [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl)] static extern int ava_present_generation();

        readonly MonoBehaviour owner;
        readonly Func<IntPtr> window;
        readonly Func<RenderTexture> source;
        readonly Coroutine endFrame;
        RenderTexture display;
        IntPtr texture;
        int presenter, generation;

        internal int Frames => presenter==0 ? 0 : ava_present_frames(presenter,out _);
        internal static int DeviceGeneration => ava_present_generation();

        internal WindowSurface(MonoBehaviour owner, Func<IntPtr> window, Func<RenderTexture> source)
        {
            this.owner=owner;
            this.window=window;
            this.source=source;
            endFrame=owner.StartCoroutine(Present());
        }

        IEnumerator Present()
        {
            var end=new WaitForEndOfFrame();
            var callback=ava_present_event();
            while(true)
            {
                yield return end;
                var target=window();
                if(target==IntPtr.Zero)continue;
                if(presenter==0)presenter=ava_present_open(target);
                var image=source();
                if(presenter==0 || image==null || !image.IsCreated())continue;

                int current=DeviceGeneration;
                if(display==null || display.width!=image.width || display.height!=image.height || !display.IsCreated() || generation!=current)
                {
                    var next=new RenderTexture(image.width,image.height,0,RenderTextureFormat.ARGB32){name="ウィンドウ表示"};
                    if(!next.Create()){UnityEngine.Object.Destroy(next);continue;}
                    if(display!=null)UnityEngine.Object.Destroy(display);
                    display=next;
                    texture=display.GetNativeTexturePtr();
                    generation=current;
                }

                // Convert Unity's bottom-left texture coordinates into Windows client coordinates.
                var previous=RenderTexture.active;
                Graphics.Blit(image,display,new Vector2(1,-1),Vector2.up);
                RenderTexture.active=previous;
                if(ava_present_queue(presenter,texture)!=0)GL.IssuePluginEvent(callback,presenter);
            }
        }

        public void Dispose()
        {
            if(endFrame!=null && owner!=null)owner.StopCoroutine(endFrame);
            if(presenter!=0)ava_present_close(presenter);
            if(display!=null)UnityEngine.Object.Destroy(display);
            presenter=0;display=null;
        }
    }
}
