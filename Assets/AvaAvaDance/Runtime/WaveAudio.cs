using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        AudioSource soundtrack;
        VisualElement waveform;
        Vector2[] peaks = Array.Empty<Vector2>();
        bool loadingWave, scrubSound, muteWave;
        long audioCycle;
        int audioStartFrame;
        Slider soundVolume;
        int AudioLastFrame => peaks.Length == 0 ? 0 : Mathf.Min(MaxFrame, audioStartFrame + peaks.Length - 1);

        void BuildWaveControls(VisualElement playback)
        {
            soundtrack = gameObject.AddComponent<AudioSource>();
            soundtrack.playOnAwake = false;
            soundtrack.spatialBlend = 0;
            if (FindObjectOfType<AudioListener>() == null) gameObject.AddComponent<AudioListener>();
            var volume = soundVolume = new Slider("音量", 0, 1) { value = 1 };
            volume.AddToClassList("volume"); playback.Add(volume);
            volume.RegisterValueChangedCallback(e => soundtrack.volume = e.newValue);
        }

        void BuildWaveform(VisualElement sidebar)
        {
            waveform = Box(sidebar, "waveform", "waveform");
            waveform.focusable=true;
            BindDrawing(waveform, p =>
            {
                float w = waveform.contentRect.width, h = waveform.contentRect.height;
                Line(p, new Vector2(0,h/2), new Vector2(w,h/2), new Color32(69, 78, 86, 255));
                bool audioTab=ribbonProfile && audioDockTab;
                float offset=audioTab?0:NameWidth;
                int columns=Mathf.CeilToInt((w-offset)/FrameWidth);
                int step = Mathf.Max(1, Mathf.CeilToInt(1 / FrameWidth));
                for (int col = 0; col < columns; col += step)
                {
                    int sourceFrame = firstFrame + col - audioStartFrame;
                    if (sourceFrame < 0 || sourceFrame >= peaks.Length) continue;
                    var range = peaks[sourceFrame];
                    float x = offset + (col + .5f) * FrameWidth;
                    Line(p, new Vector2(x, (.5f - range.y * .48f) * h), new Vector2(x, (.5f - range.x * .48f) * h), new Color32(119, 180, 215, 255));
                }
                if(audioTab){
                    float x=(frame-firstFrame+.5f)*FrameWidth;
                    Line(p,new Vector2(x,0),new Vector2(x,h),new Color32(238, 100, 107, 255));
                }
            });
            void ScrubAt(float x)=>SetFrame(Mathf.Clamp(firstFrame+Mathf.FloorToInt(x/FrameWidth),0,MaxFrame));
            waveform.RegisterCallback<PointerDownEvent>(e=>{
                if(!ribbonProfile || !audioDockTab || e.button!=0)return;
                waveform.Focus();waveform.CapturePointer(e.pointerId);ScrubAt(e.localPosition.x);e.StopPropagation();
            });
            waveform.RegisterCallback<PointerMoveEvent>(e=>{if(waveform.HasPointerCapture(e.pointerId))ScrubAt(e.localPosition.x);});
            waveform.RegisterCallback<PointerUpEvent>(e=>{if(waveform.HasPointerCapture(e.pointerId))waveform.ReleasePointer(e.pointerId);});
        }

        sealed class WaveState
        {
            public int channels,samples,rate;
            public string name;
            public Vector2[] amplitudes;
            public float[] pcm;

            public void Capture(AudioClip clip)
            {
                if(pcm!=null)return;
                var data=new float[checked(clip.samples*clip.channels)];
                if(!clip.GetData(data,0))throw new IOException("音声データを読み取れません。");
                channels=clip.channels;samples=clip.samples;rate=clip.frequency;name=clip.name;pcm=data;
            }
            public AudioClip Restore()
            {
                var clip=AudioClip.Create(name,samples,channels,rate,false);
                if(clip.SetData(pcm,0))return clip;
                Destroy(clip);throw new IOException("音声を復元できません。");
            }
        }
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<AudioClip,WaveState> waves=new System.Runtime.CompilerServices.ConditionalWeakTable<AudioClip,WaveState>();
        WaveState CurrentWave=>soundtrack.clip==null?null:waves.GetValue(soundtrack.clip,_=>new WaveState{amplitudes=peaks});
        void ChangeWave(AudioClip incoming,Vector2[] amplitudes)
        {
            var before=CurrentWave;
            int beforeStart=audioStartFrame;
            var after=incoming==null?null:waves.GetValue(incoming,_=>new WaveState{amplitudes=amplitudes});
            void Present(WaveState next,int startFrame,AudioClip original=null){
                var current=CurrentWave;
                current?.Capture(soundtrack.clip);
                var clip=original;
                if(next!=null && clip==null){clip=next.Restore();waves.Add(clip,next);}
                StopPlayback();soundtrack.Stop();var old=soundtrack.clip;soundtrack.clip=clip;
                if(old!=null)Destroy(old);
                peaks=next?.amplitudes??Array.Empty<Vector2>();
                audioStartFrame=startFrame;
                timelineRevision++;waveform.MarkDirtyRepaint();SyncRibbonSeek();
            }
            Present(after,0,incoming);PushOperation(cameraTrack,(_,forward)=>Present(forward?after:before,forward?0:beforeStart));
        }
        void DeleteAudio()
        {
            if(soundtrack.clip!=null && !loadingWave)ChangeWave(null,null);
        }

        IEnumerator LoadWave(string path)
        {
            loadingWave = true;
            try
            {
                if (new FileInfo(path).Length > 128 * 1024 * 1024)
                { status.text = "WAV読込中止　ファイルサイズの上限は128 MiBです。"; yield break; }
                using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.GetFullPath(path)).AbsoluteUri, AudioType.WAV);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                { status.text = "WAV読込中止　" + request.error; yield break; }
                AudioClip clip = null;
                try
                {
                    clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null) clip.name=Path.GetFileName(path);
                    if (clip == null || clip.samples < 1 || clip.frequency < 1 || clip.channels < 1 || clip.channels > 8)
                        throw new InvalidDataException("WAVの音声データを読み取れません。");
                    var amplitudes = ReadWaveform(clip);
                    ChangeWave(clip,amplitudes);clip=null;
                    status.text = Path.GetFileName(path) + "　" + soundtrack.clip.length.ToString("0.00") + " 秒";
                }
                catch (Exception ex) { status.text = "WAV読込中止　" + ex.Message; }
                finally { if (clip != null) Destroy(clip); }
            }
            finally { loadingWave = false; }
        }

        static Vector2[] ReadWaveform(AudioClip clip)
        {
            int count = Mathf.Min(MaxFrame + 1, (int)Math.Ceiling(clip.samples * 30.0 / clip.frequency));
            var result = new Vector2[count];
            int block = Mathf.Min(8192, clip.samples);
            var buffer = new float[block * clip.channels];
            long lastSample = Math.Min(clip.samples, (long)Math.Ceiling(count * clip.frequency / 30.0));
            for (int start = 0; start < lastSample; start += block)
            {
                if (!clip.GetData(buffer, start)) throw new InvalidDataException("波形の取得に失敗しました。");
                int size = (int)Math.Min(block, lastSample - start);
                for (int i = 0; i < size; i++)
                {
                    int f = (int)((long)(start + i) * 30 / clip.frequency);
                    if (f >= count) break;
                    for (int channel = 0; channel < clip.channels; channel++)
                    {
                        float sample = buffer[i * clip.channels + channel];
                        result[f].x = Mathf.Min(result[f].x, sample);
                        result[f].y = Mathf.Max(result[f].y, sample);
                    }
                }
            }
            return result;
        }

        void ShowAudioPlacement()
        {
            ShowDialog("音声クリップの開始位置", out var body);
            var start = new IntegerField("開始フレーム") { value = audioStartFrame };
            body.Add(start);
            body.Add(new Label("0〜600000 フレーム。開始前は無音です。波形・再生・動画出力に適用します。"));
            Action(body, "適用", true, () => {
                if (start.value < 0 || start.value > MaxFrame) { status.text = "開始フレームは0〜600000で指定してください。"; return; }
                SetAudioStartFrame(start.value); CloseDialog();
            });
            Action(body, "キャンセル", true, CloseDialog);
        }
        void SetAudioStartFrame(int next)
        {
            next = Mathf.Clamp(next, 0, MaxFrame);
            int before = audioStartFrame;
            if (before == next) return;
            void Present(int value) {
                StopPlayback(); soundtrack.Stop(); audioStartFrame = value;
                timelineRevision++; waveform?.MarkDirtyRepaint(); SyncRibbonSeek();
            }
            Present(next);
            PushOperation(cameraTrack, (_, forward) => Present(forward ? next : before));
        }

        void PlayAudioAt(int atFrame, bool scrub = false)
        {
            if (soundtrack.clip == null || muteWave) return;
            long sample = AudioPlacement.SampleAtFrame(atFrame, audioStartFrame, soundtrack.clip.frequency);
            soundtrack.Stop();
            if (sample < 0) {
                if (!scrub) { soundtrack.timeSamples = 0; soundtrack.PlayScheduled(AudioSettings.dspTime + (audioStartFrame - atFrame) / 30.0); }
                return;
            }
            if (sample >= soundtrack.clip.samples) return;
            soundtrack.timeSamples = (int)sample;
            soundtrack.Play();
            if (scrub) soundtrack.SetScheduledEndTime(AudioSettings.dspTime + 1.0/30);
        }
    }
}
