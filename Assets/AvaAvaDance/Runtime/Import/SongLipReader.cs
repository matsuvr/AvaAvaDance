using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AvaAvaDance.Import
{
    // Score timing only: synthesized/edited phoneme timing and audio are not imported.
    public static class SongLipReader
    {
        public sealed class Note
        {
            public double Start, End;
            public string Lyric, Phoneme;
        }
        sealed class TickNote
        {
            public long Start, Duration;
            public string Lyric, Phoneme;
        }
        const int MaxBytes = 16 * 1024 * 1024;
        static InvalidDataException Invalid() => new InvalidDataException("歌声プロジェクトの形式または数値が不正です。");
        static double Number(string s) {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.IsNaN(n) || double.IsInfinity(n)) throw Invalid();
            return n;
        }
        static long Integer(string s) {
            if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) throw Invalid();
            return n;
        }
        static List<Note> ConvertNotes(List<TickNote> notes, SortedDictionary<long,double> tempos, int ppq, long origin)
        {
            if (ppq <= 0 || !tempos.ContainsKey(0)) throw Invalid();
            foreach (var t in tempos) if (t.Key < 0 || t.Value <= 0) throw Invalid();
            var ticks=tempos.Keys.ToArray();var bpms=tempos.Values.ToArray();var times=new double[ticks.Length];
            for(int i=1;i<ticks.Length;i++)times[i]=times[i-1]+(ticks[i]-ticks[i-1])*60.0/ppq/bpms[i-1];
            double Seconds(long tick) {
                if (tick < 0) throw Invalid();
                int i=Array.BinarySearch(ticks,tick);if(i<0)i=~i-1;
                return times[i]+(tick-ticks[i])*60.0/ppq/bpms[i];
            }
            double offset=Seconds(origin);
            return notes.Select(n => {
                if (n.Duration <= 0) throw Invalid();
                return new Note {Start=Seconds(n.Start)-offset,End=Seconds(checked(n.Start+n.Duration))-offset,Lyric=n.Lyric,Phoneme=n.Phoneme};
            }).OrderBy(n=>n.Start).ToList();
        }
        public static List<Note> ReadTssprj(byte[] bytes)
        {
            var reader=new TreeReader(bytes);
            var root=reader.Read();
            if (!reader.AtEnd || root.Name!="StateInformation") throw Invalid();
            var song=root.One("Song");
            var tempos=new SortedDictionary<long,double>();
            foreach(var sound in song.One("Tempo").Children.Where(n=>n.Name=="Sound"))
                tempos[Integer(sound.Value("Clock"))]=Number(sound.Value("Tempo"));
            var notes=new List<TickNote>();
            foreach(var note in song.One("Score").Children.Where(n=>n.Name=="Note"))
                notes.Add(new TickNote {Start=Integer(note.Value("Clock")), Duration=Integer(note.Value("Duration")),
                    Lyric=note.Value("Lyric"),Phoneme=note.Optional("Phoneme").Replace(',', ' ')});
            // VoiSona uses 960 clocks per quarter note. Preserve the score's leading rests.
            return ConvertNotes(notes,tempos,960,0);
        }
        public static List<Note> ReadVsqx(byte[] bytes)
        {
            if(bytes==null || bytes.Length>MaxBytes) throw Invalid();
            using var stream=new MemoryStream(bytes,false);
            using var reader=XmlReader.Create(stream,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxBytes});
            var root=XDocument.Load(reader).Root;
            bool v4=root?.Name.LocalName=="vsq4";
            string ns=v4?"http://www.yamaha.co.jp/vocaloid/schema/vsq4/":"http://www.yamaha.co.jp/vocaloid/schema/vsq3/";
            if(root==null || root.Name.NamespaceName!=ns || (!v4 && root.Name.LocalName!="vsq3"))throw Invalid();
            XNamespace x=ns;
            string Field(XElement e,string name) => e.Element(x+name)?.Value ?? throw Invalid();
            var master=root.Element(x+"masterTrack") ?? throw Invalid();
            int ppq=checked((int)Integer(Field(master,"resolution")));
            long pre=Integer(Field(master,"preMeasure"));
            string pos=v4?"t":"posTick", bpm=v4?"v":"bpm";
            var tempos=new SortedDictionary<long,double>();
            foreach(var t in master.Elements(x+"tempo"))tempos[Integer(Field(t,pos))]=Number(Field(t,bpm))/100.0;
            var signature=master.Elements(x+"timeSig").FirstOrDefault(t=>Field(t,v4?"m":"posMes")=="0") ?? throw Invalid();
            long numerator=Integer(Field(signature,v4?"nu":"nume")),denominator=Integer(Field(signature,v4?"de":"denomi"));
            if(pre<0 || numerator<=0 || denominator<=0)throw Invalid();
            // Avoid silently mis-timing unusual meter changes during the count-in.
            if(master.Elements(x+"timeSig").Any(t=>{long m=Integer(Field(t,v4?"m":"posMes"));return m>0 && m<pre;}))throw new InvalidDataException("プリメジャー内の拍子変更には未対応です。");
            long origin=checked(pre*ppq*numerator*4/denominator);
            var notes=new List<TickNote>();
            // Match the legacy VSQ importer: use the first vocal track containing notes.
            foreach(var track in root.Elements(x+"vsTrack")) {
                foreach(var part in track.Elements(x+(v4?"vsPart":"musicalPart"))) {
                    long start=Integer(Field(part,pos));
                    foreach(var n in part.Elements(x+"note"))
                        notes.Add(new TickNote {Start=checked(start+Integer(Field(n,pos))),Duration=Integer(Field(n,v4?"dur":"durTick")),
                            Lyric=Field(n,v4?"y":"lyric"),Phoneme=n.Element(x+(v4?"p":"phnms"))?.Value ?? ""});
                }
                if(notes.Count>0)break;
            }
            return ConvertNotes(notes,tempos,ppq,origin);
        }
        sealed class Node
        {
            public string Name;
            public readonly Dictionary<string,string> Values=new Dictionary<string,string>();
            public readonly List<Node> Children=new List<Node>();
            public Node One(string name) {
                var matches=Children.Where(n=>n.Name==name).ToList();
                if(matches.Count!=1)throw Invalid(); return matches[0];
            }
            public string Value(string name) => Values.TryGetValue(name,out var s)?s:throw Invalid();
            public string Optional(string name) => Values.TryGetValue(name,out var s)?s:"";
        }
        // Bounded JUCE ValueTree reader. Unknown variants are skipped by their declared size.
        // No JUCE/native libraries, synthesis engine, or voice databases are bundled.
        sealed class TreeReader
        {
            readonly byte[] data; int p, nodes;
            readonly UTF8Encoding utf8=new UTF8Encoding(false,true);
            public TreeReader(byte[] bytes) {if(bytes==null || bytes.Length>MaxBytes)throw Invalid();data=bytes;}
            public bool AtEnd=>p==data.Length;
            byte Byte() {if(p>=data.Length)throw Invalid();return data[p++];}
            int Count() {
                int width=Byte(); if(width>4)throw Invalid(); uint value=0;
                for(int i=0;i<width;i++)value|=(uint)Byte()<<(8*i);
                if(value>MaxBytes)throw Invalid();return (int)value;
            }
            string Text(int end) {
                int start=p;while(p<end && data[p]!=0)p++;
                if(p==end)throw Invalid();string result=utf8.GetString(data,start,p-start);p++;return result;
            }
            public Node Read(int depth=0) {
                if(depth>64 || ++nodes>200000)throw Invalid();
                var node=new Node {Name=Text(data.Length)}; int attrs=Count();
                if(attrs>100000)throw Invalid();
                for(int i=0;i<attrs;i++) {
                    string key=Text(data.Length);int size=Count();if(size>data.Length-p)throw Invalid();int end=p+size;string value=null;
                    if(size>0) {
                        byte type=Byte();
                        if(type==1 && size==5)value=BitConverter.ToInt32(LittleEndian(4),0).ToString(CultureInfo.InvariantCulture);
                        else if(type==4 && size==9)value=BitConverter.ToDouble(LittleEndian(8),0).ToString("R",CultureInfo.InvariantCulture);
                        else if(type==6 && size==9)value=BitConverter.ToInt64(LittleEndian(8),0).ToString(CultureInfo.InvariantCulture);
                        else if(type==5) {value=Text(end);if(p!=end)throw Invalid();}
                        else if(type==1 || type==4 || type==6)throw Invalid();
                    }
                    p=end;if(value!=null) {if(node.Values.ContainsKey(key))throw Invalid();node.Values.Add(key,value);}
                }
                int children=Count();if(children>200000)throw Invalid();
                for(int i=0;i<children;i++)node.Children.Add(Read(depth+1));return node;
            }
            byte[] LittleEndian(int n) {var b=new byte[n];Array.Copy(data,p,b,0,n);p+=n;if(!BitConverter.IsLittleEndian)Array.Reverse(b);return b;}
        }
    }
}
