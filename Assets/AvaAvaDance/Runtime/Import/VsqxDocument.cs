using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace AvaAvaDance.Import
{
    // VOCALOID 3/4 use different element names in Yamaha's VSQX namespaces.
    // Timing/field references: Docs/vsqx.md. No external schema is fetched.
    public static class VsqxDocument
    {
        public const int MaxBytes = 16 * 1024 * 1024;

        public sealed class Note
        {
            public double BeginSeconds, EndSeconds;
            public string Lyric, Phonemes;
        }

        public static List<Note> Parse(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxBytes)
                throw new InvalidDataException("VSQXのファイルサイズが不正です。");
            try { return Read(data); }
            catch (XmlException ex) { throw new InvalidDataException("VSQXのXMLが不正です。", ex); }
            catch (OverflowException ex) { throw new InvalidDataException("VSQXの時刻が範囲を超えています。", ex); }
        }

        static List<Note> Read(byte[] data)
        {
            var xml = new XmlDocument { XmlResolver = null };
            using (var stream = new MemoryStream(data, false))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxBytes
            })) xml.Load(reader);

            var root = xml.DocumentElement;
            bool v3 = root?.LocalName == "vsq3";
            if (root == null || (!v3 && root.LocalName != "vsq4") ||
                root.NamespaceURI != "http://www.yamaha.co.jp/vocaloid/schema/" + (v3 ? "vsq3/" : "vsq4/"))
                throw new InvalidDataException("VOCALOID 3/4のVSQXファイルを選択してください。");

            string ns = root.NamespaceURI;
            IEnumerable<XmlElement> Children(XmlElement parent, string name) => parent.ChildNodes
                .OfType<XmlElement>().Where(e => e.LocalName == name && e.NamespaceURI == ns);
            XmlElement Required(XmlElement parent, string name)
            {
                var elements = Children(parent, name).Take(2).ToArray();
                if (elements.Length != 1) throw new InvalidDataException("VSQXの " + name + " がないか重複しています。");
                return elements[0];
            }
            int Number(XmlElement parent, string name, int minimum = 0)
            {
                if (!int.TryParse(Required(parent, name).InnerText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value) || value < minimum)
                    throw new InvalidDataException("VSQXの " + name + " が不正です。");
                return value;
            }
            string Text(XmlElement parent, string name) => Children(parent, name).FirstOrDefault()?.InnerText ?? "";

            var master = Required(root, "masterTrack");
            int ppq = Number(master, "resolution", 1), preMeasure = Number(master, "preMeasure");
            string position = v3 ? "posTick" : "t";
            var meters = new SortedDictionary<int, (int numerator, int denominator)>();
            foreach (var meter in Children(master, "timeSig"))
            {
                int bar = Number(meter, v3 ? "posMes" : "m");
                int numerator = Number(meter, v3 ? "nume" : "nu", 1);
                int denominator = Number(meter, v3 ? "denomi" : "de", 1);
                if ((denominator & (denominator - 1)) != 0 || meters.ContainsKey(bar))
                    throw new InvalidDataException("VSQXの拍子が不正です。");
                meters.Add(bar, (numerator, denominator));
            }
            if (!meters.ContainsKey(0)) throw new InvalidDataException("VSQXに開始時の拍子がありません。");
            long prefixTicks = 0;
            int previousBar = 0, beats = 4, unit = 4;
            foreach (var meter in meters)
            {
                if (meter.Key >= preMeasure) break;
                prefixTicks = checked(prefixTicks + checked((long)(meter.Key - previousBar) * ppq * beats * 4) / unit);
                previousBar = meter.Key; beats = meter.Value.numerator; unit = meter.Value.denominator;
            }
            prefixTicks = checked(prefixTicks + checked((long)(preMeasure - previousBar) * ppq * beats * 4) / unit);

            var tempos = new SortedDictionary<int, int>();
            foreach (var tempo in Children(master, "tempo"))
            {
                int tick = Number(tempo, position), bpm100 = Number(tempo, v3 ? "bpm" : "v", 1);
                if (tempos.ContainsKey(tick)) throw new InvalidDataException("VSQXのテンポ時刻が重複しています。");
                tempos.Add(tick, bpm100);
            }
            if (!tempos.ContainsKey(0)) throw new InvalidDataException("VSQXに開始時のテンポがありません。");
            var ticks = tempos.Keys.ToArray();
            var rates = tempos.Values.Select(bpm100 => 6000.0 / bpm100 / ppq).ToArray();
            var seconds = new double[ticks.Length];
            for (int i = 1; i < ticks.Length; i++)
                seconds[i] = seconds[i - 1] + (ticks[i] - ticks[i - 1]) * rates[i - 1];
            double Seconds(long tick)
            {
                int lo = 0, hi = ticks.Length;
                while (lo + 1 < hi) { int mid = lo + (hi - lo) / 2; if (ticks[mid] <= tick) lo = mid; else hi = mid; }
                return seconds[lo] + (tick - ticks[lo]) * rates[lo];
            }
            double origin = Seconds(prefixTicks);

            // Match VSQ import: use one vocal track, without mixing backing vocals.
            foreach (var track in Children(root, "vsTrack"))
            {
                var notes = new List<Note>();
                foreach (var part in Children(track, v3 ? "musicalPart" : "vsPart"))
                {
                    int offset = Number(part, position), length = Number(part, "playTime", 1);
                    foreach (var note in Children(part, "note"))
                    {
                        int begin = Number(note, position), duration = Number(note, v3 ? "durTick" : "dur", 1);
                        if (begin >= length) continue;
                        long end = Math.Min((long)begin + duration, length);
                        notes.Add(new Note {
                            BeginSeconds = Seconds((long)offset + begin) - origin,
                            EndSeconds = Seconds((long)offset + end) - origin,
                            Lyric = Text(note, v3 ? "lyric" : "y"), Phonemes = Text(note, v3 ? "phnms" : "p")
                        });
                    }
                }
                if (notes.Count > 0) return notes.OrderBy(n => n.BeginSeconds).ToList();
            }
            throw new InvalidDataException("VSQXに歌唱音符がありません。");
        }
    }
}
