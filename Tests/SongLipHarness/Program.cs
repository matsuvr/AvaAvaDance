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
    static byte[] Xml(bool v4,string tempo="12000",string duration="480",string extra="") {
        string p=v4?"t":"posTick",d=v4?"dur":"durTick",y=v4?"y":"lyric",ph=v4?"p":"phnms",v=v4?"v":"bpm",m=v4?"m":"posMes",nu=v4?"nu":"nume",de=v4?"de":"denomi",part=v4?"vsPart":"musicalPart",root=v4?"vsq4":"vsq3";
        return Encoding.UTF8.GetBytes($"<{root} xmlns='http://www.yamaha.co.jp/vocaloid/schema/{root}/'><masterTrack><resolution>480</resolution><preMeasure>1</preMeasure><timeSig><{m}>0</{m}><{nu}>4</{nu}><{de}>4</{de}></timeSig><tempo><{p}>0</{p}><{v}>{tempo}</{v}></tempo><tempo><{p}>2160</{p}><{v}>6000</{v}></tempo></masterTrack><vsTrack><{part}><{p}>1920</{p}><note><{p}>0</{p}><{d}>{duration}</{d}><{y}>あ</{y}><{ph}>a</{ph}></note></{part}></vsTrack>{extra}</{root}>");
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
        foreach(bool v4 in new[]{false,true}) {
            var n=SongLipReader.ReadVsqx(Xml(v4)).Single();
            Check(n.Start==0 && Math.Abs(n.End-.75)<1e-9,"VSQX count-in + mid-note tempo "+v4);
            Check(n.Lyric=="あ" && n.Phoneme=="a","VSQX Unicode "+v4);
            Reject(()=>SongLipReader.ReadVsqx(Xml(v4,"0")),"zero BPM");
            Reject(()=>SongLipReader.ReadVsqx(Xml(v4,"NaN")),"NaN BPM");
            Reject(()=>SongLipReader.ReadVsqx(Xml(v4,duration:"-1")),"negative duration");
        }
        try {SongLipReader.ReadVsqx(Encoding.UTF8.GetBytes("<!DOCTYPE vsq3 [<!ENTITY bad SYSTEM 'file:///tmp/no'>]><vsq3>&bad;</vsq3>"));throw new Exception("DTD accepted");}catch(System.Xml.XmlException){tests++;}
        Console.WriteLine($"PASS {tests} checks");
    }
}
