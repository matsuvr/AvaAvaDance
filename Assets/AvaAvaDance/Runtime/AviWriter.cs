using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AvaAvaDance
{
    // Video for Windows handles the RIFF headers and stream index for uncompressed AVI.
    internal sealed class AviWriter : IDisposable
    {
        const string Lib = "avifil32.dll";
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StreamInfo {
            public uint type, handler, flags, caps;
            public ushort priority, language;
            public uint scale, rate, start, length, initialFrames, buffer, quality, sampleSize;
            public int left, top, right, bottom;
            public uint editCount, formatChanges;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string name;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfo {
            public int size, width, height;
            public ushort planes, bits;
            public int compression, bytes, xppm, yppm, colors, important;
        }
        [StructLayout(LayoutKind.Sequential, Pack=2)]
        struct WaveFormat {
            public ushort format, channels;
            public uint rate, byteRate;
            public ushort align, bits, extra;
        }
        [DllImport(Lib)] static extern void AVIFileInit();
        [DllImport(Lib)] static extern void AVIFileExit();
        [DllImport(Lib, CharSet=CharSet.Unicode)] static extern int AVIFileOpenW(out IntPtr file, string path, int flags, IntPtr handler);
        [DllImport(Lib, CharSet=CharSet.Unicode)] static extern int AVIFileCreateStreamW(IntPtr file, out IntPtr stream, ref StreamInfo info);
        [DllImport(Lib)] static extern int AVIStreamSetFormat(IntPtr stream, int start, ref BitmapInfo data, int size);
        [DllImport(Lib)] static extern int AVIStreamSetFormat(IntPtr stream, int start, ref WaveFormat data, int size);
        [DllImport(Lib)] static extern int AVIStreamWrite(IntPtr stream, int start, int count, byte[] data, int bytes, int flags, IntPtr samples, IntPtr written);
        [DllImport(Lib)] static extern int AVIStreamWrite(IntPtr stream, int start, int count, short[] data, int bytes, int flags, IntPtr samples, IntPtr written);
        [DllImport(Lib)] static extern uint AVIStreamRelease(IntPtr stream);
        [DllImport(Lib)] static extern uint AVIFileRelease(IntPtr file);
        IntPtr file, video, audio;
        readonly byte[] dib;
        int frame, sample;
        bool initialized;

        internal AviWriter(string path, int width, int height, int fps, int count, bool hasAudio)
        {
            int bytes = checked(width * height * 4);
            if (((long)bytes + (hasAudio ? 192000 / fps : 0) + 64) * count > 2_000_000_000L)
                throw new IOException("無圧縮AVIの2 GB上限を超えます。出力範囲かサイズを小さくしてください。");
            dib = new byte[bytes]; AVIFileInit(); initialized = true;
            try {
                Check(AVIFileOpenW(out file, path, 0x1001, IntPtr.Zero));
                var info = new StreamInfo { type=0x73646976, scale=1, rate=(uint)fps, buffer=(uint)bytes, quality=uint.MaxValue,
                    right=width, bottom=height, name="Video" };
                Check(AVIFileCreateStreamW(file, out video, ref info));
                var bitmap = new BitmapInfo { size=40, width=width, height=height, planes=1, bits=32, bytes=bytes };
                Check(AVIStreamSetFormat(video, 0, ref bitmap, 40));
                if (hasAudio) {
                    info = new StreamInfo { type=0x73647561, scale=4, rate=192000, sampleSize=4, quality=uint.MaxValue, name="Audio" };
                    Check(AVIFileCreateStreamW(file, out audio, ref info));
                    var wave = new WaveFormat { format=1, channels=2, rate=48000, byteRate=192000, align=4, bits=16 };
                    Check(AVIStreamSetFormat(audio, 0, ref wave, Marshal.SizeOf<WaveFormat>()));
                }
            } catch { Dispose(); throw; }
        }
        static void Check(int result) { if (result != 0) throw new IOException("AVI書込エラー 0x" + result.ToString("X8")); }
        internal void Video(byte[] rgba)
        {
            for (int i=0;i<rgba.Length;i+=4) { dib[i]=rgba[i+2]; dib[i+1]=rgba[i+1]; dib[i+2]=rgba[i]; dib[i+3]=255; }
            Check(AVIStreamWrite(video,frame++,1,dib,dib.Length,16,IntPtr.Zero,IntPtr.Zero));
        }
        internal void Audio(short[] pcm, int count)
        {
            Check(AVIStreamWrite(audio,sample,count,pcm,count*4,0,IntPtr.Zero,IntPtr.Zero)); sample+=count;
        }
        public void Dispose()
        {
            if (!initialized) return;
            if (audio != IntPtr.Zero) AVIStreamRelease(audio);
            if (video != IntPtr.Zero) AVIStreamRelease(video);
            if (file != IntPtr.Zero) AVIFileRelease(file);
            AVIFileExit(); initialized=false;
        }
    }
}
