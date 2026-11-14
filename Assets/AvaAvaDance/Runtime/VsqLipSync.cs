using MmdText = Ava.Extensions.MmdText;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using AvaAvaDance.Import;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        void ImportLipSync(string path)
        {
            if (Path.GetExtension(path).Equals(".tssprj", StringComparison.OrdinalIgnoreCase)) {
                var songNotes = SongLipReader.ReadTssprj(ReadMmdFile(path));
                var songEdits = new Dictionary<(MotionTrack, int), float>();
                foreach (var note in songNotes)
                    AddLipKeys(songEdits, note.Phoneme, note.Lyric, note.Start, note.End, "TSSPRJ");
                CommitLipKeys(songEdits);
                return;
            }
            if (!Path.GetExtension(path).Equals(".vsqx", StringComparison.OrdinalIgnoreCase)) { ImportVsq(path); return; }
            var notes = VsqxDocument.Parse(ReadMmdFile(path));
            var edits = new Dictionary<(MotionTrack, int), float>();
            foreach (var note in notes)
                AddLipKeys(edits, note.Phonemes, note.Lyric, note.BeginSeconds, note.EndSeconds, "VSQX");
            CommitLipKeys(edits);
        }

        // VSQ stores VOCALOID's INI sections in numbered DM text events in an SMF file.
        void ImportVsq(string path)
        {
            var data = ReadMmdFile(path);
            int p = 0;
            int Read(int length) {
                if (p > data.Length - length) throw new EndOfStreamException();
                int n=0; for(int i=0;i<length;i++)n=checked(n*256+data[p++]); return n;
            }
            int Delta() {
                int n=0;
                for(int i=0;i<4;i++){int b=Read(1);n=(n<<7)|(b&127);if(b<128)return n;}
                throw new InvalidDataException("VSQデルタ時間が不正です。");
            }
            if(Read(4)!=0x4d546864 || Read(4)!=6 || Read(2)>1)throw new InvalidDataException("VSQ形式を確認してください。");
            int tracks=Read(2), ppq=Read(2); if(ppq<=0 || ppq>=32768)throw new InvalidDataException("VSQ分解能が不正です。");
            var tempos=new SortedDictionary<int,int>{{0,500000}};
            string sequence=null; int beats=4, denominator=4;
            for(int t=0;t<tracks;t++) {
                if(Read(4)!=0x4d54726b)throw new InvalidDataException("VSQトラックが不正です。");
                int bytes=Read(4), end=checked(p+bytes);
                if(end>data.Length)throw new EndOfStreamException();
                int tick=0,running=0;
                using var text=new MemoryStream();
                while(p<end) {
                    tick=checked(tick+Delta()); if(p>=end)throw new EndOfStreamException();
                    int statusByte=data[p]>=128?Read(1):running;
                    if(statusByte==255) {
                        int type=Read(1), len=Delta();
                        if(len>end-p)throw new EndOfStreamException();
                        if(type==0x51 && len==3)tempos[tick]=data[p]<<16|data[p+1]<<8|data[p+2];
                        if(type==0x58 && len>=2 && tick==0 && data[p+1]<8) {beats=data[p];denominator=1<<data[p+1];}
                        if(type==1 && len>=8 && data[p]=='D' && data[p+1]=='M' && data[p+2]==':') {
                            int i=p+3;while(i<p+len && data[i]!=':')i++;
                            if(i<p+len)text.Write(data,i+1,p+len-i-1);
                        }
                        p+=len;
                    } else if(statusByte==0xf0 || statusByte==0xf7) { int len=Delta(); if(len>end-p)throw new EndOfStreamException();p+=len; }
                    else if(statusByte>=128 && statusByte<240) {
                        running=statusByte; p+=statusByte>>4==12 || statusByte>>4==13 ? 1 : 2;
                    } else throw new InvalidDataException("VSQイベントが不正です。");
                    if(p>end)throw new EndOfStreamException();
                }
                if(text.Length>0 && sequence==null)sequence=MmdText.Decode(text.ToArray());
            }
            if(sequence==null)throw new InvalidDataException("VSQ歌詞トラックがありません。");
            var sections=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string,string> current=null;
            foreach(string raw in sequence.Split('\n')) {
                string line=raw.Trim();
                if(line.StartsWith("[") && line.EndsWith("]")) {
                    current=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);sections[line.Substring(1,line.Length-2)]=current;
                } else if(current!=null) {
                    int split=line.IndexOf('=');if(split>0)current[line.Substring(0,split)]=line.Substring(split+1);
                }
            }
            if(!sections.TryGetValue("EventList",out var events))throw new InvalidDataException("VSQイベント一覧がありません。");
            double Seconds(int tick) {
                int start=0,tempo=500000;double us=0;
                foreach(var kv in tempos) { if(kv.Key>tick)break;us+=(kv.Key-start)*(double)tempo;start=kv.Key;tempo=kv.Value; }
                return (us+(tick-start)*(double)tempo)/ppq/1000000;
            }
            int pre=1;
            if(sections.TryGetValue("Master",out var master) && master.TryGetValue("PreMeasure",out var pm))pre=int.Parse(pm);
            double origin=Seconds(checked(pre*ppq*beats*4/denominator));
            var edits=new Dictionary<(MotionTrack,int),float>();
            foreach(var kv in events) {
                if(!int.TryParse(kv.Key,out int tick))continue;
                foreach(string id in kv.Value.Split(',')) {
                    if(!sections.TryGetValue(id,out var note) || !note.TryGetValue("LyricHandle",out var handle) ||
                       !note.TryGetValue("Length",out var duration) || !sections.TryGetValue(handle,out var lyric) || !lyric.TryGetValue("L0",out var l0))continue;
                    var words=l0.Split(',');
                    AddLipKeys(edits, words.Length>1?words[1]:"", words[0], Seconds(tick)-origin,
                        Seconds(checked(tick+int.Parse(duration)))-origin, "VSQ");
                }
            }
            CommitLipKeys(edits);
        }

        void AddLipKeys(Dictionary<(MotionTrack,int),float> edits, string phones, string lyric,
            double beginSeconds, double endSeconds, string format)
        {
            string vowel = VsqVowel(phones, lyric);
            if (vowel == null) return;
            int index = "あいうえお".IndexOf(vowel, StringComparison.Ordinal);
            string viseme = new[] { "aa", "ih", "ou", "ee", "oh" }[index];
            string[] names = { vowel, new[] { "a", "i", "u", "e", "o" }[index], viseme,
                "vrc.v_" + (index == 3 ? "e" : viseme) };
            var track = morphGroups[1].Tracks.Find(m => names.Contains(MorphName(m), StringComparer.OrdinalIgnoreCase));
            if (track == null) return;
            double start = frame + Math.Round(beginSeconds * 30, MidpointRounding.ToEven);
            double end = frame + Math.Round(endSeconds * 30, MidpointRounding.ToEven);
            if (end <= 0) return;
            if (start > MaxFrame || end > MaxFrame || double.IsNaN(start) || double.IsNaN(end))
                throw new InvalidDataException(format + "がフレーム上限を超えています。");
            int begin = (int)Math.Max(0, start), finish = Math.Max(begin + 1, (int)end);
            if (finish > MaxFrame) throw new InvalidDataException(format + "がフレーム上限を超えています。");
            edits[(track, Math.Max(0, begin - 1))] = 0;
            edits[(track, begin)] = 1;
            edits[(track, finish)] = 0;
        }

        void CommitLipKeys(Dictionary<(MotionTrack,int),float> edits)
        {
            if(edits.Count==0)throw new InvalidDataException("歌詞に対応するリップ表情がありません。");
            CommitKeys(edits.Select(k=>new KeyChange {Track=k.Key.Item1, Frame=k.Key.Item2, After=k.Value}));
        }
        static string VsqVowel(string phones,string lyric)
        {
            string token=phones.Trim(' ','"').Split((char[])null,StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if(token!=null && token.Length>0) {
                int i="aiueo".IndexOf(char.ToLowerInvariant(token[0]));
                if(i>=0)return "あいうえお"[i].ToString();
                if(token=="M")return "う";
            }
            string kana=lyric.Trim(' ','"').Normalize(NormalizationForm.FormKC);
            if(kana.Length==0)return null;
            char last=kana[kana.Length-1]; if(last>='ァ'&&last<='ヶ')last=(char)(last-0x60);
            string[] vowels={"あかがさざただなはばぱまゃやらゎわぁ","いきぎしじちぢにひびぴみりぃ","うくぐすずつづぬふぶぷむゅゆるぅ","えけげせぜてでねへべぺめれぇ","おこごそぞとどのほぼぽもょよろをぉ"};
            int n=Array.FindIndex(vowels,v=>v.Contains(last.ToString()));return n<0?null:"あいうえお"[n].ToString();
        }
    }
}
