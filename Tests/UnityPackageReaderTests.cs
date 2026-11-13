// Standalone test executable; compile together with UnityPackageReader.cs. No Unity dependency.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using AvaAvaDance.Import;
class UnityPackageReaderTests
{
    const string G = "0123456789abcdef0123456789abcdef";
    static int tests;
    static byte[] Member(string name, byte[] data, char type = '0', bool badChecksum = false)
    {
        var h = new byte[512];
        Encoding.UTF8.GetBytes(name).CopyTo(h, 0);
        Encoding.ASCII.GetBytes(Convert.ToString(data.Length, 8).PadLeft(11,'0')).CopyTo(h,124);
        for(int i=148;i<156;i++) h[i]=32;
        h[156]=(byte)type;
        int sum=0;foreach(byte b in h)sum+=b;
        Encoding.ASCII.GetBytes(Convert.ToString(sum,8).PadLeft(6,'0')).CopyTo(h,148);
        h[154]=0;h[155]=32;
        if(badChecksum)h[0]^=1;
        using(var m=new MemoryStream()) {m.Write(h,0,512);m.Write(data,0,data.Length);m.Write(new byte[(512-data.Length%512)%512],0,(512-data.Length%512)%512);return m.ToArray();}
    }
    static byte[] Bytes(string text){return Encoding.UTF8.GetBytes(text);}
    static byte[] Package(params byte[][] members)
    {
        using(var m=new MemoryStream()) {using(var g=new GZipStream(m,CompressionMode.Compress,true)){foreach(var b in members)g.Write(b,0,b.Length);g.Write(new byte[1024],0,1024);}return m.ToArray();}
    }
    static void Reject(string name, byte[] data)
    {
        try {UnityPackageReader.Read(new MemoryStream(data));throw new Exception("Accepted invalid package: "+name);}
        catch(InvalidDataException) {tests++;}
    }
    static byte[] P(string path){return Member(G+"/pathname",Bytes(path));}
    static byte[] A(){return Member(G+"/asset",new byte[]{1,2,3});}
    static void Main()
    {
        var list=UnityPackageReader.Read(new MemoryStream(Package(Member(G+"/",new byte[0],'5'), P("Assets/Avatar/model.fbx"),A(),Member(G+"/asset.meta",Bytes("fileFormatVersion: 2")))));
        if(list.Count!=1 || list[0].Data.Length!=3 || list[0].Meta!="fileFormatVersion: 2" || list[0].Guid!=G)throw new Exception("Round-trip failed");tests++;
        var folders=UnityPackageReader.Read(new MemoryStream(Package(P("Assets/Avatar"),Member(G+"/asset.meta",Bytes("folderAsset: yes")))));
        if(folders[0].HasAsset)throw new Exception("Folder payload invented");tests++;
        var vpm=UnityPackageReader.Read(new MemoryStream(Package(Member("./",new byte[0],'5'),Member("./"+G+"/",new byte[0],'5'),Member("./"+G+"/pathname",Bytes("Assets/installer/config.json")),A())));
        if(vpm.Count!=1 || vpm[0].Path!="Assets/installer/config.json")throw new Exception("VPM tar prefix");tests++;
        TestVpmInstaller();
        ShaderInstallTests.Run();
        Reject("normalized duplicate",Package(P("Assets/a"),A(),Member("./"+G+"/asset",new byte[]{4})));
        Reject("prefixed traversal",Package(Member("./../asset",new byte[0])));
        foreach(var path in new[]{"../escape", "/absolute", "C:/absolute", "Assets/../escape", "Assets\\escape", "Assets//x", "Assets/./x", "Assets/x\0", "Assets/x.", "Assets/x "})Reject("path "+path,Package(P(path),A()));
        foreach(var type in new[]{'1','2','3','4','6','x','g','L','K'})Reject("type "+type,Package(Member(G+"/asset",new byte[0],type),P("Assets/a")));
        Reject("bad checksum",Package(Member(G+"/asset",new byte[0],'0',true),P("Assets/a")));
        Reject("duplicate",Package(P("Assets/a"),A(),A()));
        Reject("missing pathname",Package(A()));
        Reject("duplicate asset path",Package(P("Assets/a"),A(),Member("1123456789abcdef0123456789abcdef/pathname",Bytes("Assets/a")),Member("1123456789abcdef0123456789abcdef/asset",new byte[0])));
        Reject("invalid gzip",new byte[]{1,2,3,4});
        Reject("appended tar content",Package(new byte[1024],new byte[]{1}));
        Reject("unknown member",Package(Member(G+"/script.dll",new byte[0]),P("Assets/a")));
        Reject("tar traversal",Package(Member("../asset",new byte[0])));
        Reject("utf8",Package(Member(G+"/pathname",new byte[]{255}),A()));
        Reject("truncated tar",Package(new byte[510])); // 510 zeros + end padding leaves partial second record.
        var oversized=Member(G+"/asset",new byte[0]);
        Encoding.ASCII.GetBytes(Convert.ToString(UnityPackageReader.MaxFileBytes+1,8).PadLeft(11,'0')).CopyTo(oversized,124);
        for(int i=148;i<156;i++)oversized[i]=32;
        int checksum=0;for(int i=0;i<512;i++)checksum+=oversized[i];
        Encoding.ASCII.GetBytes(Convert.ToString(checksum,8).PadLeft(6,'0')).CopyTo(oversized,148);oversized[154]=0;oversized[155]=32;
        Reject("oversized",Package(oversized));
        var many = new byte[UnityPackageReader.MaxMembers + 1][];
        for(int i=0;i<many.Length;i++) many[i]=Member(i.ToString("x32")+"/",new byte[0],'5');
        Reject("too many members",Package(many));
        Console.WriteLine("PASS "+tests+" reader checks");
    }

    static void TestVpmInstaller()
    {
        var installed = new VpmPackageIdentity { name=VpmInstaller.LilToon, repo=VpmInstaller.OfficialRepository, version="2.4.1" };
        string Config(string range) => "{\"vpmRepositories\":[\""+installed.repo+"\"],\"vpmDependencies\":{\"jp.lilxyzw.liltoon\":\""+range+"\"}}";
        void Accept(string json) {
            if (!VpmInstaller.Resolve(Bytes(json), installed, "2022.3.22f1").Contains("lilToon 2.4.1"))
                throw new Exception("Expected a compatible VPM package");
            tests++;
        }
        void Refuse(string json) {
            try { VpmInstaller.Resolve(Bytes(json), installed, "2022.3.22f1"); throw new Exception("VPM config should fail: "+json); }
            catch (InvalidDataException) { tests++; }
        }
        foreach (var range in new[]{"2.x.x","2.4.*","2.4.1","^2.3.0","~2.4.0",">=2.0.0 <3.0.0"}) Accept(Config(range));
        Accept(Config("2.x").Replace("\""+installed.repo+"\"", "{\"url\":\""+installed.repo+"\"}"));
        Accept(Config("*"));
        foreach (var range in new[]{"1.x.x","3.0.0","~2.3.0","2.x.1"}) Refuse(Config(range));
        Refuse(Config("2.x").Replace(installed.repo, "https://example.invalid/index.json"));
        Refuse(Config("2.x").Replace("\"2.x\"", "\"2.x\",\"org.example.other\":\"1\""));
        Refuse(Config("2.x").Insert(1, "\"minimumUnity\":\"6000.0\","));
    }
}
