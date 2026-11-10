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
        Slider soundVolume;
        int AudioLastFrame => peaks.Length == 0 ? 0 : peaks.Length - 1;

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
            waveform.generateVisualContent += ctx =>
            {
                var p = ctx.painter2D;
                float w = waveform.contentRect.width, h = waveform.contentRect.height;
                Line(p, new Vector2(0,h/2), new Vector2(w,h/2), Color.gray);
                for (int col = 0; col < VisibleColumns && firstFrame + col < peaks.Length; col++)
                {
                    var range = peaks[firstFrame + col];
                    float x = NameWidth + (col + .5f) * FrameWidth;
                    Line(p, new Vector2(x, (.5f - range.y * .48f) * h), new Vector2(x, (.5f - range.x * .48f) * h), new Color(.1f,.32f,.15f));
                }
            };
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
                    StopPlayback(); soundtrack.Stop();
                    var previous = soundtrack.clip;
                    soundtrack.clip = clip; clip = null;
                    if (previous != null) Destroy(previous);
                    peaks = amplitudes; waveform.MarkDirtyRepaint();
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

        void PlayAudioAt(int atFrame, bool scrub = false)
        {
            if (soundtrack.clip == null || muteWave) return;
            int sample = (int)((long)atFrame * soundtrack.clip.frequency / 30);
            soundtrack.Stop();
            if (sample >= soundtrack.clip.samples) return;
            soundtrack.timeSamples = sample;
            soundtrack.Play();
            if (scrub) soundtrack.SetScheduledEndTime(AudioSettings.dspTime + 1.0/30);
        }
    }
}
