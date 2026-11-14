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
        Console.WriteLine($"PASS {tests} checks");
    }
}
