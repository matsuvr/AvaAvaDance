using System;
using System.IO;
using System.Text;
using AvaAvaDance.Import;

static class VsqxTests
{
    static int assertions;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); assertions++; }
    static void Near(double actual, double expected, string name) => Check(Math.Abs(actual - expected) < 1e-9, name);
    static void Reject(string xml, string name)
    {
        try { VsqxDocument.Parse(Encoding.UTF8.GetBytes(xml)); }
        catch (InvalidDataException) { assertions++; return; }
        throw new Exception("Accepted " + name);
    }
    static string Fixture(bool v3)
    {
        string tick = v3 ? "posTick" : "t", duration = v3 ? "durTick" : "dur";
        string part = v3 ? "musicalPart" : "vsPart", lyric = v3 ? "lyric" : "y", phones = v3 ? "phnms" : "p";
        string root = v3 ? "vsq3" : "vsq4";
        string Note(int t, int d, string y, string p) => $"<note><{tick}>{t}</{tick}><{duration}>{d}</{duration}><{lyric}><![CDATA[{y}]]></{lyric}><{phones}><![CDATA[{p}]]></{phones}></note>";
        string Part(int t, int length, string notes) => $"<{part}><{tick}>{t}</{tick}><playTime>{length}</playTime>{notes}</{part}>";
        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><{root} xmlns=\"http://www.yamaha.co.jp/vocaloid/schema/{root}/\">" +
            "<masterTrack><resolution>960</resolution><preMeasure>2</preMeasure>" +
            (v3 ? "<timeSig><posMes>0</posMes><nume>4</nume><denomi>4</denomi></timeSig><timeSig><posMes>1</posMes><nume>3</nume><denomi>4</denomi></timeSig>" :
                "<timeSig><m>0</m><nu>4</nu><de>4</de></timeSig><timeSig><m>1</m><nu>3</nu><de>4</de></timeSig>") +
            $"<tempo><{tick}>0</{tick}><{(v3 ? "bpm" : "v")}>12000</{(v3 ? "bpm" : "v")}></tempo>" +
            $"<tempo><{tick}>7200</{tick}><{(v3 ? "bpm" : "v")}>6000</{(v3 ? "bpm" : "v")}></tempo></masterTrack>" +
            "<vsTrack/>" +
            "<vsTrack>" + Part(6720, 1920, Note(0, 960, "か", "k a") + Note(960, 1920, "い", "i")) +
            Part(9600, 960, Note(0, 480, "ヴォ", "v o") + Note(960, 480, "ignored", "a")) + "</vsTrack>" +
            "<vsTrack>" + Part(6720, 960, Note(0, 960, "backing", "e")) + $"</vsTrack></{root}>";
    }

    static void Main(string[] args)
    {
        foreach (bool v3 in new[] { true, false })
        {
            string xml = Fixture(v3);
            var notes = VsqxDocument.Parse(Encoding.UTF8.GetBytes(xml));
            Check(notes.Count == 3, "first nonempty vocal track, all parts, clip part bounds");
            Check(notes[0].Lyric == "か" && notes[0].Phonemes == "k a", "CDATA lyrics and phonemes");
            Near(notes[0].BeginSeconds, 0, "changing meter in premeasure");
            Near(notes[0].EndSeconds, .75, "note spanning tempo change");
            Near(notes[1].BeginSeconds, .75, "tempo after change");
            Near(notes[1].EndSeconds, 1.75, "clipped note duration");
            Near(notes[2].BeginSeconds, 2.75, "second part absolute position");
            Near(notes[2].EndSeconds, 3.25, "second part duration");
            Check(notes[2].Lyric == "ヴォ", "non-ASCII lyric");

            byte[] bom = Encoding.UTF8.GetPreamble(), utf8 = Encoding.UTF8.GetBytes(xml);
            var data = new byte[bom.Length + utf8.Length]; bom.CopyTo(data, 0); utf8.CopyTo(data, bom.Length);
            Check(VsqxDocument.Parse(data).Count == 3, "UTF8 BOM");
            string utf16 = xml.Replace("encoding=\"utf-8\"", "encoding=\"utf-16\"");
            byte[] unicode = Encoding.Unicode.GetBytes(utf16);
            var wide = new byte[unicode.Length + 2]; wide[0] = 255; wide[1] = 254; unicode.CopyTo(wide, 2);
            Check(VsqxDocument.Parse(wide).Count == 3, "XML encoding declaration and UTF16 BOM");
            string noPhones = v3 ? xml.Replace("<phnms><![CDATA[k a]]></phnms>", "") : xml.Replace("<p><![CDATA[k a]]></p>", "");
            Check(VsqxDocument.Parse(Encoding.UTF8.GetBytes(noPhones))[0].Phonemes == "", "optional phonemes for kana fallback");
            var early = VsqxDocument.Parse(Encoding.UTF8.GetBytes(xml.Replace("<preMeasure>2</preMeasure>", "<preMeasure>3</preMeasure>")));
            Near(early[0].BeginSeconds, -2.75, "notes before musical origin");

            Reject(xml.Replace("<resolution>960</resolution>", "<resolution>0</resolution>"), "zero resolution");
            Reject(xml.Replace("<preMeasure>2</preMeasure>", "<preMeasure>-1</preMeasure>"), "negative premeasure");
            Reject(xml.Replace("<resolution>960</resolution>", "<resolution>2147483647</resolution>")
                .Replace("<preMeasure>2</preMeasure>", "<preMeasure>2147483647</preMeasure>"), "tick overflow");
            Reject(xml.Replace("12000", "0"), "zero tempo");
            Reject(xml.Replace("12000", "NaN"), "invalid tempo");
            Reject(xml.Replace("<playTime>1920</playTime>", "<playTime>0</playTime>"), "zero part duration");
            Reject(xml.Replace(v3 ? "<durTick>960</durTick>" : "<dur>960</dur>", v3 ? "<durTick>-1</durTick>" : "<dur>-1</dur>"), "negative note duration");
            Reject(xml.Replace("<resolution>960</resolution>", ""), "missing master field");
            Reject(xml.Replace("<resolution>960</resolution>", "<resolution>960</resolution><resolution>480</resolution>"), "duplicate master field");
            Reject(xml.Replace("http://www.yamaha.co.jp/vocaloid/schema/", "http://example.com/"), "wrong namespace");
            Reject(xml.Substring(0, xml.Length - 15), "truncated XML");
            Reject(xml.Replace("<vsTrack>", "<otherTrack>").Replace("</vsTrack>", "</otherTrack>"), "no vocal notes");
        }
        const string ns = "http://www.yamaha.co.jp/vocaloid/schema/vsq4/";
        string prefixed = $"<v:vsq4 xmlns:v=\"{ns}\"><v:masterTrack><v:resolution>480</v:resolution><v:preMeasure>0</v:preMeasure>" +
            "<v:timeSig><v:m>0</v:m><v:nu>4</v:nu><v:de>4</v:de></v:timeSig><v:tempo><v:t>0</v:t><v:v>12000</v:v></v:tempo></v:masterTrack>" +
            "<v:vsTrack><v:vsPart><v:t>0</v:t><v:playTime>480</v:playTime><v:note><v:t>0</v:t><v:dur>480</v:dur><v:y>a &amp; b</v:y><v:p>a</v:p></v:note></v:vsPart></v:vsTrack></v:vsq4>";
        var prefixNotes = VsqxDocument.Parse(Encoding.UTF8.GetBytes(prefixed));
        Check(prefixNotes[0].Lyric == "a & b", "namespace prefixes and XML entities");
        Near(prefixNotes[0].EndSeconds, .5, "480 PPQ");
        Reject(Fixture(false).Replace("<de>4</de>", "<de>3</de>"), "non power-of-two denominator");
        Reject(Fixture(false).Replace("<t>7200</t>", "<t>0</t>"), "duplicate tempo positions");
        Reject(Fixture(false).Replace("<m>1</m>", "<m>0</m>"), "duplicate meters");
        Reject(Fixture(false).Replace("<t>0</t><v>12000</v>", "<t>1</t><v>12000</v>"), "missing initial tempo");
        Reject(Fixture(false).Replace("<m>0</m>", "<m>2</m>"), "missing initial meter");
        Reject("<?xml version=\"1.0\"?><!DOCTYPE vsq4 [<!ENTITY ext SYSTEM \"file:///never-read\">]><vsq4>&ext;</vsq4>", "DTD and external entities");
        Reject("", "empty XML");
        Reject(new string('a', VsqxDocument.MaxBytes + 1), "byte limit");
        if (args.Length > 0)
        {
            foreach (string path in args)
                Console.WriteLine($"{Path.GetFileName(path)}: {VsqxDocument.Parse(File.ReadAllBytes(path)).Count} notes");
        }
        Console.WriteLine($"PASS VSQX: {assertions} assertions");
    }
}
