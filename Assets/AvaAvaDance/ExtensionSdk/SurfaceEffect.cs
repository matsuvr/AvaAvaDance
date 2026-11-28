using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ava.Extensions
{
    public sealed class MaterialSlot
    {
        public int Subset;
        public bool Textured,SphereMapped,Toon,SelfShadow;
        public string DrawRole;
        public MaterialSlot ForRole(string role){var slot=(MaterialSlot)MemberwiseClone();slot.DrawRole=role;return slot;}
        public string Id,ActorId,ActorName,MaterialName,SourceFile,Target,OwnerSlot,DefaultEffect;
    }

    public sealed class SurfaceBinding
    {
        public bool Visible=true;
        public SurfacePass[] Passes;
        public SurfacePlan Plan;
        public string Instance;
    }

    public enum SurfaceOperation { Target, Depth, ClearColor, ClearDepth, Geometry, Buffer, SceneColor, Parameter, ClearStencil }
    public sealed class SurfaceStep
    {
        public SurfaceOperation Operation;
        public int Slot,Pass;
        public string Name;
        public float[] Value;
    }
    public sealed class SurfaceTexture
    {
        public string Name,Semantic,Format,Resource,Description,DefaultEffect,Dimension="2D";
        public float[] ClearColor;
        public float ClearDepth=1;
        public int Width,Height,Levels=1;
        public float RatioX=1,RatioY=1;
        public float Offset,Speed=1;
        public string SeekVariable,SeekSemantic;
        public float SeekDefault;
        public ControlVariable SeekControl;
        public bool Shared,AntiAlias;
        public byte[] Data;
    }
    public sealed class SurfacePlan
    {
        public string Order="standard",Class="object";
        public bool SelectPerMaterial,SubsetSensitive;
        public string[] Roles=Array.Empty<string>();
        public SurfaceTexture[] Textures=Array.Empty<SurfaceTexture>();
        public SurfaceStep[] Steps=Array.Empty<SurfaceStep>();
    }

    public interface ISurfaceProvider
    {
        bool HasBindings {get;}
        SurfaceBinding Resolve(MaterialSlot slot);
    }

    public sealed class ControlVariable
    {
        public string Type,Object,Item,Parameter,Texture;
        public int Texels;
    }

    public sealed class SurfacePass : IDisposable
    {
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr ava_fx_compile(byte[] source,int length,string vertex,string pixel,string bindings,
            int blend,int zwrite,int cull,StringBuilder error,int capacity);
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]
        static extern void ava_fx_release(IntPtr program);

        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]
        static extern void ava_fx_states(IntPtr program,int[] raster,int[] samplers,int count);
        public void Configure(SurfaceStates states,SurfaceSampler[] samplers)
        {
            if(Handle==IntPtr.Zero)throw new ObjectDisposedException(nameof(SurfacePass));
            if(samplers.Length>1024)throw new ArgumentOutOfRangeException(nameof(samplers));
            int[] data=new int[samplers.Length*6];for(int i=0;i<samplers.Length;i++)samplers[i].Pack(data,i*6);
            ava_fx_states(Handle,states.Pack(),data,samplers.Length);
        }
        public ControlVariable[] Controls {get;set;}=Array.Empty<ControlVariable>();
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_fx_texture_indices(IntPtr program,[Out] int[] destination,int capacity);
        public System.Collections.Generic.IReadOnlyList<int> TextureInputs {get;}
        public IntPtr Handle {get; private set;}
        public SurfacePass(string hlsl,string vertex,string pixel,string bindings,bool alphaBlend,bool depthWrite,int cullMode)
        {
            var error=new StringBuilder(8192);var bytes=Encoding.UTF8.GetBytes(hlsl);
            Handle=ava_fx_compile(bytes,bytes.Length,vertex,pixel,bindings,alphaBlend?1:0,depthWrite?1:0,cullMode,error,error.Capacity);
            if(Handle==IntPtr.Zero)throw new InvalidOperationException(error.ToString());
            var inputs=new int[ava_fx_texture_indices(Handle,null,0)];
            if(inputs.Length>0)ava_fx_texture_indices(Handle,inputs,inputs.Length);
            TextureInputs=Array.AsReadOnly(inputs);
        }
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr ava_fx_disassemble(IntPtr program,out int length);
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        static extern bool ava_fx_validate(IntPtr handle,StringBuilder error,int length);
        public void ValidateDevice()
        {
            var error=new StringBuilder(2048);if(!ava_fx_validate(Handle,error,error.Capacity))throw new InvalidOperationException(error.ToString());
        }
        public string Disassemble()
        {
            var pointer=ava_fx_disassemble(Handle,out int length);if(pointer==IntPtr.Zero)return "";
            try{var bytes=new byte[length];Marshal.Copy(pointer,bytes,0,length);return Encoding.UTF8.GetString(bytes);}
            finally{Marshal.FreeCoTaskMem(pointer);}
        }
        public void Dispose(){if(Handle!=IntPtr.Zero){ava_fx_release(Handle);Handle=IntPtr.Zero;}}
    }
}
