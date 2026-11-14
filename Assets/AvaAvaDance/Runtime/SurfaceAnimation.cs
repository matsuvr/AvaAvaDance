using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace AvaAvaDance
{
    sealed class SurfaceAnimation : IDisposable
    {
        const long MaxPixels=32L*1024*1024;
        readonly List<Texture2D> frames=new List<Texture2D>();
        readonly List<double> ends=new List<double>();
        double duration;

        internal Texture2D At(double seconds)
        {
            if(!double.IsFinite(seconds))seconds=0;
            double sample=seconds%duration;if(sample<0)sample+=duration;
            int index=ends.BinarySearch(sample);
            index=index<0?~index:index+1;
            return frames[Math.Min(index,frames.Count-1)];
        }
        void Append(Texture2D image,double delay)
        {
            frames.Add(image);duration+=delay>0?delay:.01;ends.Add(duration);
        }
        public void Dispose(){foreach(var frame in frames)UnityEngine.Object.Destroy(frame);frames.Clear();}
        internal static SurfaceAnimation Load(byte[] bytes,bool mipmaps)
        {
            var image=new SurfaceAnimation();
            try{
                if(bytes.Length>=6 && bytes[0]=='G' && bytes[1]=='I' && bytes[2]=='F')image.ReadGif(bytes,mipmaps);
                else image.ReadPng(bytes,mipmaps);
                if(image.frames.Count==0)throw new InvalidDataException("アニメーションにフレームがありません。");
                return image;
            }catch{image.Dispose();throw;}
        }

        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr ava_gif_open(byte[] data,int length,out int width,out int height,out int count);
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]static extern int ava_gif_frame(IntPtr gif,int index,[Out]byte[] data,int length);
        [DllImport("AvaRenderBridge",CallingConvention=CallingConvention.Cdecl)]static extern void ava_gif_close(IntPtr gif);
        void ReadGif(byte[] bytes,bool mipmaps)
        {
            var gif=ava_gif_open(bytes,bytes.Length,out int w,out int h,out int count);
            if(gif==IntPtr.Zero)throw new InvalidDataException("GIFを読み込めません。");
            try{
                var pixels=new byte[checked(w*h*4)];
                for(int i=0;i<count;i++){
                    int ms=ava_gif_frame(gif,i,pixels,pixels.Length);if(ms<0)throw new InvalidDataException("GIFフレームを読み込めません。");
                    var texture=new Texture2D(w,h,TextureFormat.RGBA32,mipmaps);
                    Append(texture,ms/1000d);texture.SetPixelData(pixels,0);texture.Apply(mipmaps,true);
                }
            }finally{ava_gif_close(gif);}
        }
        static readonly byte[] Signature={137,80,78,71,13,10,26,10};
        static uint Big(byte[] data,int at)=>((uint)data[at]<<24)|((uint)data[at+1]<<16)|((uint)data[at+2]<<8)|data[at+3];
        static byte[] Big(uint value)=>new[]{(byte)(value>>24),(byte)(value>>16),(byte)(value>>8),(byte)value};
        static readonly uint[] Crc=Enumerable.Range(0,256).Select(n=>{
            uint c=(uint)n;for(int i=0;i<8;i++)c=(c&1)!=0?0xedb88320u^(c>>1):c>>1;return c;
        }).ToArray();
        static void Chunk(Stream stream,string name,byte[] bytes)
        {
            var type=Encoding.ASCII.GetBytes(name);var size=Big((uint)bytes.Length);
            stream.Write(size,0,4);stream.Write(type,0,4);stream.Write(bytes,0,bytes.Length);
            uint crc=uint.MaxValue;foreach(byte b in type)crc=Crc[(crc^b)&255]^(crc>>8);
            foreach(byte b in bytes)crc=Crc[(crc^b)&255]^(crc>>8);
            size=Big(~crc);stream.Write(size,0,4);
        }
        static Color32 Over(Color32 src,Color32 dst)
        {
            if(src.a==255 || dst.a==0)return src;if(src.a==0)return dst;
            int tail=dst.a*(255-src.a),total=src.a*255+tail;
            byte Blend(int a,int b)=>(byte)((a*src.a*255+b*tail+total/2)/total);
            return new Color32(Blend(src.r,dst.r),Blend(src.g,dst.g),Blend(src.b,dst.b),(byte)((total+127)/255));
        }
        void ReadPng(byte[] data,bool mipmaps)
        {
            if(data.Length<33 || !Signature.SequenceEqual(data.Take(8)))throw new InvalidDataException("PNG形式を確認してください。");
            int width=checked((int)Big(data,16)),height=checked((int)Big(data,20));long pixels=(long)width*height;
            if(width<1 || height<1 || pixels>MaxPixels)throw new InvalidDataException("PNGの寸法が範囲外です。");
            var canvas=new Color32[(int)pixels];var globals=new List<(string type,byte[] bytes)>();
            byte[] header=null,control=null;var compressed=new List<byte[]>();bool animated=false;
            void Flush()
            {
                if(compressed.Count==0)return;
                int w=control==null?width:checked((int)Big(control,4)),h=control==null?height:checked((int)Big(control,8));
                int x=control==null?0:checked((int)Big(control,12)),top=control==null?0:checked((int)Big(control,16));
                if(w<=0 || h<=0 || x<0 || top<0 || x>width-w || top>height-h || (frames.Count+1)*pixels>MaxPixels)
                    throw new InvalidDataException("PNGフレームの寸法が範囲外です。");
                int y=height-top-h;
                using var stream=new MemoryStream();stream.Write(Signature,0,8);
                var ihdr=(byte[])header.Clone();Array.Copy(Big((uint)w),0,ihdr,0,4);Array.Copy(Big((uint)h),0,ihdr,4,4);
                Chunk(stream,"IHDR",ihdr);foreach(var g in globals)Chunk(stream,g.type,g.bytes);
                foreach(var block in compressed)Chunk(stream,"IDAT",block);Chunk(stream,"IEND",Array.Empty<byte>());compressed.Clear();
                int disposal=control?[24]??0,blend=control?[25]??0;var previous=disposal==2?(Color32[])canvas.Clone():null;
                var patch=new Texture2D(2,2,TextureFormat.RGBA32,false);
                try{
                    if(!patch.LoadImage(stream.ToArray()) || patch.width!=w || patch.height!=h)throw new InvalidDataException("PNGフレームが不正です。");
                    var region=patch.GetPixels32();
                    for(int j=0;j<h;j++)for(int i=0;i<w;i++){
                        int at=(j+y)*width+i+x;var src=region[j*w+i];canvas[at]=blend==0?src:Over(src,canvas[at]);
                    }
                }finally{UnityEngine.Object.Destroy(patch);}
                double delay=.1;
                if(control!=null){int num=(control[20]<<8)+control[21],den=(control[22]<<8)+control[23];delay=num/(double)(den==0?100:den);}
                var frame=new Texture2D(width,height,TextureFormat.RGBA32,mipmaps);Append(frame,delay);
                frame.SetPixels32(canvas);frame.Apply(mipmaps,true);
                if(previous!=null)canvas=previous;
                if(disposal==1)for(int j=0;j<h;j++)Array.Clear(canvas,(j+y)*width+x,w);
            }
            for(int pos=8;pos<data.Length;){
                if(data.Length-pos<12)throw new EndOfStreamException();
                int size=checked((int)Big(data,pos));string type=Encoding.ASCII.GetString(data,pos+4,4);
                if(size>data.Length-pos-12)throw new EndOfStreamException();
                var block=new byte[size];Buffer.BlockCopy(data,pos+8,block,0,size);pos+=size+12;
                switch(type){
                    case "IHDR":header=block;break;
                    case "acTL":animated=true;break;
                    case "fcTL":if(size!=26)throw new InvalidDataException("APNG制御情報が不正です。");Flush();control=block;break;
                    case "fdAT":if(size<4 || control==null)throw new InvalidDataException("APNGフレームが不正です。");compressed.Add(block.Skip(4).ToArray());break;
                    case "IDAT":if(!animated || control!=null)compressed.Add(block);break;
                    case "PLTE":case "tRNS":case "sRGB":case "gAMA":globals.Add((type,block));break;
                    case "IEND":Flush();return;
                }
            }
            throw new EndOfStreamException();
        }
    }
}
