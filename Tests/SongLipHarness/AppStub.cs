// Minimal host for exercising the production importer without a Unity installation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace UnityEngine {static class Mathf {public static int RoundToInt(float n)=>(int)Math.Round(n);}}
namespace Ava.Extensions {static class MmdText {public static string Decode(byte[] b)=>Encoding.UTF8.GetString(b);}}
namespace AvaAvaDance {
    class MotionTrack {public string Name;}
    static class MmdText {public static string Decode(byte[] b)=>Encoding.UTF8.GetString(b);}
    public sealed partial class DanceApp {
        const int MaxFrame=600000;
        int frame;
        sealed class Group {public List<MotionTrack> Tracks=new List<MotionTrack>();}
        readonly Group[] morphGroups={new Group(),new Group()};
        class KeyChange {public MotionTrack Track;public int Frame;public object After;}
        readonly Dictionary<int,float> keys=new Dictionary<int,float>();
        int commits;
        string MorphName(MotionTrack t)=>t.Name;
        static byte[] ReadMmdFile(string path)=>File.ReadAllBytes(path);
        void CommitKeys(IEnumerable<KeyChange> changes) {foreach(var c in changes)keys[c.Frame]=(float)c.After;commits++;}
        public static void TestImport(string path) {
            var app=new DanceApp {frame=10}; app.morphGroups[1].Tracks.Add(new MotionTrack{Name="お"});
            app.ImportLipSync(path);
            if(app.commits!=1 || app.keys[58]!=1 || app.keys[106]!=0)throw new Exception("TSSPRJ application frame offset/commit");
            var bad=new DanceApp {frame=599999};bad.morphGroups[1].Tracks.Add(new MotionTrack{Name="お"});
            try {bad.ImportLipSync(path);throw new Exception("frame overflow accepted");}catch(InvalidDataException){}
            if(bad.commits!=0)throw new Exception("partial mutation on error");
        }
    }
}
