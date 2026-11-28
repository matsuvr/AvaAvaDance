using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Ava.LyricVideo
{
    [DataContract]
    internal struct LyricKey
    {
        [DataMember] public int Frame;
        [DataMember] public string Text;
        [DataMember] public int X, Y, Size, Opacity, Rgb;

        public static LyricKey Blank => new LyricKey { Text = "", X = 50, Y = 82, Size = 6, Opacity = 100, Rgb = 0xffffff };
        public bool Valid(int limit) => Frame >= 0 && Frame <= limit && Text != null && Text.Length <= 4096 &&
            X >= 0 && X <= 100 && Y >= 0 && Y <= 100 && Size >= 1 && Size <= 40 &&
            Opacity >= 0 && Opacity <= 100 && Rgb >= 0 && Rgb <= 0xffffff;
    }

    [DataContract]
    internal sealed class LyricLayer
    {
        [DataMember] public string Name;
        [DataMember] public List<LyricKey> Keys = new List<LyricKey>();

        // First key strictly after the sample, including fractional movie frames.
        int Next(float frame)
        {
            int low = 0, high = Keys.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (Keys[mid].Frame <= frame) low = mid + 1; else high = mid;
            }
            return low;
        }

        public void Put(LyricKey key)
        {
            int next = Next(key.Frame);
            if (next > 0 && Keys[next - 1].Frame == key.Frame) Keys[next - 1] = key;
            else Keys.Insert(next, key);
        }

        public LyricKey Sample(float frame, out float x, out float y, out float size, out float opacity)
        {
            int next = Next(frame);
            var key = next == 0 ? LyricKey.Blank : Keys[next - 1];
            x = key.X; y = key.Y; size = key.Size; opacity = key.Opacity;
            if (next > 0 && next < Keys.Count)
            {
                var end = Keys[next];
                float t = (frame - key.Frame) / (end.Frame - key.Frame);
                x += (end.X - key.X) * t; y += (end.Y - key.Y) * t;
                size += (end.Size - key.Size) * t; opacity += (end.Opacity - key.Opacity) * t;
            }
            return key;
        }

        public LyricKey EditValue(int frame)
        {
            var key = Sample(frame, out var x, out var y, out var size, out var opacity);
            key.Frame = frame;
            key.X = (int)Math.Round(x); key.Y = (int)Math.Round(y);
            key.Size = (int)Math.Round(size); key.Opacity = (int)Math.Round(opacity);
            return key;
        }
    }

    [DataContract]
    internal sealed class Lyrics
    {
        [DataMember] public int Version = 1;
        [DataMember] public List<LyricLayer> Layers = new List<LyricLayer> { new LyricLayer { Name = "歌詞 1" } };
        static readonly DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Lyrics));

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            serializer.WriteObject(stream, this);
            return stream.ToArray();
        }

        public static Lyrics Decode(byte[] bytes, int limit)
        {
            if (bytes == null) return new Lyrics();
            using var stream = new MemoryStream(bytes, false);
            var result = (Lyrics)serializer.ReadObject(stream);
            if (result?.Version != 1 || result.Layers == null || result.Layers.Count == 0)
                throw new InvalidDataException("歌詞レイヤーの保存形式を読み取れません。");
            foreach (var layer in result.Layers)
            {
                if (layer?.Name == null || layer.Keys == null) throw new InvalidDataException("歌詞レイヤーが不正です。");
                int prior = -1;
                foreach (var key in layer.Keys)
                {
                    if (!key.Valid(limit) || key.Frame <= prior) throw new InvalidDataException("歌詞キーが不正です。");
                    prior = key.Frame;
                }
            }
            return result;
        }

        public int LastFrame => Layers.Max(layer => layer.Keys.Count == 0 ? 0 : layer.Keys[layer.Keys.Count - 1].Frame);
    }
}
