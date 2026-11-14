using System;
using System.IO;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;

static class Program
{
    static int tests;
    static void Check(bool value,string name) {if(!value)throw new Exception(name);tests++;}
    static void Reject(Action action,string name) {try{action();}catch(InvalidDataException){tests++;return;}throw new Exception(name);}
    sealed class Tree
    {
        public string Name;
        public (string Key, object Value)[] Values;
        public Tree[] Children;
        public Tree(string name, (string, object)[] values = null, params Tree[] children)
        { Name = name; Values = values ?? Array.Empty<(string, object)>(); Children = children; }
    }
    static Tree Note(long clock = 0, long duration = 960, string lyric = "あ", string phoneme = "a") =>
        new Tree("Note", new (string, object)[] { ("Clock", clock), ("Duration", duration), ("Lyric", lyric), ("Phoneme", phoneme) });
    static Tree Tempo(long clock, double bpm) =>
        new Tree("Sound", new (string, object)[] { ("Clock", clock), ("Tempo", bpm) });
    static Tree Song(Tree[] notes, params Tree[] tempos) => new Tree("StateInformation", null,
        new Tree("Song", null, new Tree("Tempo", null, tempos), new Tree("Score", null, notes)));
    // Write small, independent ValueTree fixtures so tempo/boundary expectations are explicit.
    static byte[] Encode(Tree tree)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Count(int n) {
            uint remaining = (uint)n; int width = 0;
            for (uint x = remaining; x > 0; x >>= 8) width++;
            writer.Write((byte)width);
            for (int i = 0; i < width; i++) { writer.Write((byte)remaining); remaining >>= 8; }
        }
        void Text(string s) { writer.Write(Encoding.UTF8.GetBytes(s)); writer.Write((byte)0); }
        void Write(Tree node) {
            Text(node.Name); Count(node.Values.Length);
            foreach (var value in node.Values) {
                Text(value.Key);
                switch (value.Value) {
                    case int n: Count(5); writer.Write((byte)1); writer.Write(n); break;
                    case double n: Count(9); writer.Write((byte)4); writer.Write(n); break;
                    case long n: Count(9); writer.Write((byte)6); writer.Write(n); break;
                    case string s: Count(Encoding.UTF8.GetByteCount(s) + 2); writer.Write((byte)5); Text(s); break;
                    case byte[] b: Count(b.Length + 1); writer.Write((byte)8); writer.Write(b); break;
                    case bool b: Count(1); writer.Write((byte)(b ? 2 : 3)); break;
                    default: throw new Exception("Unsupported test value");
                }
            }
            Count(node.Children.Length);
            foreach (var child in node.Children) Write(child);
        }
        Write(tree); return stream.ToArray();
    }
    static void CheckTimingAndBounds()
    {
        // Unsorted input, one note crossing a tempo change, and one starting exactly at it.
        var notes = SongLipReader.ReadTssprj(Encode(Song(new[] {
            Note(1920, 480, "い", "sh,i"), Note(480, 960), Note(960, 960, "う", "u")
        }, Tempo(1920, 240), Tempo(0, 120), Tempo(960, 60))));
        Check(notes.Count == 3 && notes[0].Lyric == "あ" && notes[2].Phoneme == "sh i", "sort and phonemes");
        Check(notes[0].Start == .25 && notes[0].End == 1 && notes[1].Start == .5 && notes[1].End == 1.5 &&
            notes[2].Start == 1.5 && notes[2].End == 1.625, "tempo integration at/across changes");
        var tree = Song(new[] { Note() }, Tempo(0, 120));
        tree.Values = new (string, object)[] { ("Ignored", new byte[] { 0, 255, 1 }), ("Enabled", true), ("Version", 1) };
        Check(SongLipReader.ReadTssprj(Encode(tree)).Count == 1, "skip binary/bool variants, read int variant");
        Reject(() => SongLipReader.ReadTssprj(Encode(Song(new[] { Note() }, Tempo(0, 120), Tempo(0, 60)))), "duplicate tempo accepted");
        foreach (double bpm in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.Epsilon })
            Reject(() => SongLipReader.ReadTssprj(Encode(Song(new[] { Note() }, Tempo(0, bpm)))), "invalid/overflowing tempo " + bpm);
        Reject(() => SongLipReader.ReadTssprj(Encode(Song(new[] { Note() }, Tempo(960, 120)))), "missing initial tempo");
        Reject(() => SongLipReader.ReadTssprj(Encode(Song(new[] { Note() }, Tempo(0, 120), Tempo(-1, 60)))), "negative tempo clock");
        Reject(() => SongLipReader.ReadTssprj(Encode(Song(new[] { Note(-1) }, Tempo(0, 120)))), "negative note clock");
        Reject(() => SongLipReader.ReadTssprj(Encode(Song(new[] { Note(0, 0) }, Tempo(0, 120)))), "zero duration");
        Reject(() => SongLipReader.ReadTssprj(Encode(Song(new[] { Note(long.MaxValue) }, Tempo(0, 120)))), "note clock overflow");
        tree = new Tree("Ignored");
        for (int i = 0; i < 65; i++) tree = new Tree("Ignored", null, tree);
        Reject(() => SongLipReader.ReadTssprj(Encode(tree)), "depth limit");
        var badUtf8 = Encode(Song(new[] { Note() }, Tempo(0, 120))); badUtf8[0] = 255;
        Reject(() => SongLipReader.ReadTssprj(badUtf8), "invalid UTF8");
    }
    static void Main() {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures/nodo-template.tssprj"));
        AvaAvaDance.DanceApp.TestImport(Path.Combine(AppContext.BaseDirectory,"Fixtures/nodo-template.tssprj"));
        var notes=SongLipReader.ReadTssprj(bytes);
        Check(notes.Count==1 && notes[0].Lyric=="ド" && notes[0].Phoneme=="d o","actual upstream TSSPRJ note");
        Check(Math.Abs(notes[0].Start-1.6)<1e-9 && Math.Abs(notes[0].End-3.2)<1e-9,"960 clocks/quarter, leading rests retained");
        for(int i=0;i<bytes.Length;i++) {int cut=i;Reject(()=>SongLipReader.ReadTssprj(bytes.Take(cut).ToArray()),"truncation "+i);}
        Reject(()=>SongLipReader.ReadTssprj(bytes.Concat(new byte[]{0}).ToArray()),"trailing junk");
        Reject(()=>SongLipReader.ReadTssprj(Encoding.UTF8.GetBytes("TSSolution\0\0\0")),"wrong project kind");
        Reject(()=>SongLipReader.ReadTssprj(new byte[16*1024*1024+1]),"size limit");
        CheckTimingAndBounds();
        Console.WriteLine($"PASS {tests} checks");
    }
}
