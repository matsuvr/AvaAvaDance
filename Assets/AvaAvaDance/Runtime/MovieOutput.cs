using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        const string VideoDll = "AvaMp4";
        [DllImport(VideoDll, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)]
        static extern int ava_movie_open(string path,int width,int height,int fps,int bitrate,int audio,out IntPtr handle);
        [DllImport(VideoDll, CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_movie_video(IntPtr handle, byte[] rgba, int size);
        [DllImport(VideoDll, CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_movie_audio(IntPtr handle, short[] pcm, int count);
        [DllImport(VideoDll, CallingConvention=CallingConvention.Cdecl)]
        static extern int ava_movie_close(IntPtr handle, int finish);

        [Serializable] class MovieSettings
        {
            public int width=640, height=360, fps=30;
            public string first="", last="";
            public bool audio=true;
            public bool Valid => width>=16 && height>=16 && width<=4096 && height<=4096 &&
                (width|height)%2==0 && fps>=1 && fps<=120;
        }
        MovieSettings movie=new MovieSettings();
        bool exporting,cancelExport;
        string movieError;

        void ShowOutputSize()
        {
            ShowDialog("出力サイズ",out var body);
            var w=new IntegerField("幅") {value=movie.width}; var h=new IntegerField("高さ") {value=movie.height};
            body.Add(w); body.Add(h);
            var message=new Label(); body.Add(message);
            var row=Box(body,"size-actions","row");
            Action(row,"OK",true,()=> {
                if(w.value<16 || h.value<16 || w.value>4096 || h.value>4096 || ((w.value|h.value)&1)!=0) {
                    message.text="幅・高さは16～4096の偶数で指定してください。"; return;
                }
                movie.width=w.value; movie.height=h.value; CloseDialog();
            });
            Action(row,"キャンセル",true,CloseDialog);
        }

        void ChooseMovie(bool avi)
        {
            StopPlayback();
            try {
                string format=avi ? "AVI" : "MP4";
                var path=Import.PackageFileDialog.Save(format+"出力",avi ? "avi" : "mp4"); if(path==null)return;
                ShowDialog(format+"出力設定",out var body);
                body.Add(new Label("録画サイズ　"+movie.width+" × "+movie.height+" Pixel"));
                var fps=new IntegerField("フレームレート") {value=movie.fps}; body.Add(fps);
                var range=Box(body,"movie-range","row"); range.Add(new Label("録画フレーム"));
                var from=new TextField {value=movie.first}; var to=new TextField {value=movie.last};
                range.Add(from); range.Add(new Label("～")); range.Add(to);
                var audio=new Toggle("WAVEも出力する") {value=movie.audio && soundtrack.clip!=null}; audio.SetEnabled(soundtrack.clip!=null); body.Add(audio);
                body.Add(new Label(avi ? "ビデオ形式　非圧縮RGB" : "ビデオ圧縮コーデック　H.264"));
                var error=new Label(); body.Add(error);
                var row=Box(body,"movie-actions","row");
                Action(row,"OK",true,()=> {
                    var config=new MovieSettings {width=movie.width,height=movie.height,fps=fps.value,first=from.value,last=to.value,audio=audio.value};
                    if(!MovieRange(config,out _,out _)) {error.text="録画範囲またはfps（1～120）を確認してください。";return;}
                    movie=config; StartCoroutine(ExportMovie(path,config));
                });
                Action(row,"キャンセル",true,CloseDialog);
            } catch(Exception ex) { status.text=(avi?"AVI":"MP4")+"出力中止　"+ex.Message; }
        }

        bool MovieRange(MovieSettings s,out int first,out int last)
        {
            first=0; last=0;
            return s.Valid && Limit(s.first,0,out first) && Limit(s.last,Math.Max(LastFrame(),AudioLastFrame),out last) && first<=last;
        }
        static bool Limit(string text,int fallback,out int n)
        {
            n=fallback; return string.IsNullOrWhiteSpace(text) || int.TryParse(text,out n) && n>=0 && n<=MaxFrame;
        }
        static void CheckEncoder(int hr)
        {
            if(hr<0) throw new IOException("MP4エンコーダー　0x"+hr.ToString("X8")+"　"+Marshal.GetExceptionForHR(hr)?.Message);
        }

        sealed class MovieAudio
        {
            readonly AudioClip clip;
            readonly float volume;
            float[] buffer;
            readonly short[] output;
            readonly int start;
            public MovieAudio(AudioClip clip,int first,int fps,float volume)
            {
                this.clip=clip;this.volume=volume;start=first;
                output=new short[(48000/fps+2)*2];
            }
            public void Write(IntPtr handle,AviWriter avi,int index,int fps)
            {
                long low=(long)index*48000/fps, high=(long)(index+1)*48000/fps;
                int count=(int)(high-low);
                double origin=(start/30.0+low/48000.0)*clip.frequency;
                int offset=(int)Math.Floor(origin);
                int needed=Math.Min(clip.samples,(int)Math.Ceiling(count*clip.frequency/48000.0)+2);
                if(buffer==null || buffer.Length!=needed*clip.channels) buffer=new float[needed*clip.channels];
                Array.Clear(output,0,count*2);
                if(offset<clip.samples) {
                    if(!clip.GetData(buffer,offset)) throw new IOException("WAVサンプルの取得に失敗しました。");
                    for(int i=0;i<count;i++) {
                        double pos=origin-offset+i*clip.frequency/48000.0;
                        int a=(int)pos;
                        if(offset+a>=clip.samples)break;
                        int b=offset+a+1<clip.samples?a+1:a;
                        for(int ch=0;ch<2;ch++) {
                            int c=clip.channels==1?0:ch;
                            float v=Mathf.Lerp(buffer[a*clip.channels+c],buffer[b*clip.channels+c],(float)(pos-a));
                            output[i*2+ch]=(short)Mathf.RoundToInt(Mathf.Clamp(v*volume,-1,1)*32767);
                        }
                    }
                }
                if(avi!=null)avi.Audio(output,count); else CheckEncoder(ava_movie_audio(handle,output,count));
            }
        }

        IEnumerator ExportMovie(string path, MovieSettings config)
        {
            if(exporting) yield break;
            if(!MovieRange(config,out int first,out int last)) throw new ArgumentException("録画設定が不正です。");
            bool isAvi=path.EndsWith(".avi",StringComparison.OrdinalIgnoreCase);
            string format=isAvi?"AVI":"MP4";
            ShowDialog(format+"出力",out var body);
            var progress=new Label(); body.Add(progress); cancelExport=false;
            Action(body,"キャンセル",true,()=>cancelExport=true);
            exporting=true; movieError=null;
            int count=(int)Math.Ceiling((last-first+1)*config.fps/30.0);
            string temp=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),Guid.NewGuid()+Path.GetExtension(path));
            var tracks=sceneTracks.Concat(drawOrder.SelectMany(a=>a.modelTracks)).ToArray();
            var drafts=tracks.Select(t=>t.CurrentValue).ToArray();
            var oldTexture=previewCamera.targetTexture;
            var oldAspect=previewCamera.aspect;
            bool oldGrid=floor.activeSelf;
            bool edgeTest=Environment.GetCommandLineArgs().Contains("-avaMovieEdgeSmoke");
            RenderTexture rt=null; Texture2D image=null; IntPtr writer=IntPtr.Zero; AviWriter avi=null;
            try {
                byte[] pixels=null; MovieAudio source=null;
                try {
                    rt=new RenderTexture(config.width,config.height,24,RenderTextureFormat.ARGB32);
                    if(!rt.Create())throw new IOException("録画用RenderTextureを作成できません。");
                    pixels=new byte[checked(config.width*config.height*4)];
                    image=new Texture2D(config.width,config.height,TextureFormat.RGBA32,false);
                    previewCamera.targetTexture=rt; previewCamera.aspect=(float)config.width/config.height;
                    floor.SetActive(false); InvalidateDynamics();
                    if(config.audio && soundtrack.clip!=null)source=new MovieAudio(soundtrack.clip,first,config.fps,soundtrack.volume);
                    int bitrate=Mathf.Clamp((int)((long)config.width*config.height*config.fps/5),1500000,48000000);
                    if(isAvi)avi=new AviWriter(temp,config.width,config.height,config.fps,count,source!=null);
                    else CheckEncoder(ava_movie_open(temp,config.width,config.height,config.fps,bitrate,source!=null?1:0,out writer));
                } catch(Exception ex) { movieError=ex.Message; }
                for(int i=0;i<count && movieError==null && !cancelExport;i++) {
                    try { EvaluateAnimation(first+i*30f/config.fps,true); }
                    catch(Exception ex) { movieError=ex.Message; break; }
                    // Unity updates skinning and blend shapes once per player frame. Let
                    // every sampled pose reach the renderer, including the first pose.
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    if(cancelExport)break;
                    try {
                        previewCamera.Render();
                        var prior=RenderTexture.active;
                        try { RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false); }
                        finally {RenderTexture.active=prior;}
                        image.GetRawTextureData<byte>().CopyTo(pixels);
                        if(avi!=null)avi.Video(pixels); else CheckEncoder(ava_movie_video(writer,pixels,pixels.Length));
                        if(i==0 && edgeTest) File.WriteAllBytes(path+".first.png",image.EncodeToPNG());
                        source?.Write(writer,avi,i,config.fps);
                        progress.text="録画中　"+(i+1)+" / "+count;
                    } catch(Exception ex) {movieError=ex.Message;}

                }
                try {
                    avi?.Dispose(); avi=null;
                    if(writer!=IntPtr.Zero) {
                        int hr=ava_movie_close(writer,!cancelExport && movieError==null?1:0);writer=IntPtr.Zero;CheckEncoder(hr);
                    }
                    if(!cancelExport && movieError==null) PublishFile(temp,path);
                } catch(Exception ex) {movieError=ex.Message;}
            } finally {
                avi?.Dispose();
                if(writer!=IntPtr.Zero)ava_movie_close(writer,0);
                if(File.Exists(temp))File.Delete(temp);
                previewCamera.targetTexture=oldTexture; previewCamera.aspect=oldAspect;
                if(rt!=null){rt.Release();Destroy(rt);} if(image!=null)Destroy(image);
                floor.SetActive(oldGrid); exporting=false; ResizePreview(); ForEachActor(calculationOrder,InvalidateDynamics);
                Seek(frame);
                for(int i=0;i<tracks.Length;i++)tracks[i].Restore(drafts[i]);
                RefreshRigPreview(); RestoreSolvedPose(); ApplyView(); SyncControls(); CloseDialog();
            }
            status.text=format+(movieError!=null?"出力失敗　"+movieError:cancelExport?"出力をキャンセルしました。":"出力　"+Path.GetFileName(path));
        }

        static void PublishFile(string temp,string path)
        {
            if(File.Exists(path))File.Replace(temp,path,null);
            else File.Move(temp,path);
        }
    }
}
