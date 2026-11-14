using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AvaAvaDance.Import;

static class ShaderInstallTests
{
    public static void Run()
    {
        var lil = OfficialShaderCatalog.LilToon;
        var file = lil.Downloads[0];
        string vpm = "{\"packages\":{\"jp.lilxyzw.liltoon\":{\"versions\":{\"2.4.1\":{\"name\":\"jp.lilxyzw.liltoon\",\"version\":\"2.4.1\",\"repo\":\"" + VpmInstaller.OfficialRepository + "\",\"url\":\"" + file.Url + "\",\"zipSHA256\":\"" + file.Sha256 + "\"}}}}}";
        byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        OfficialShaderCatalog.ValidateVpm(Bytes(vpm));
        OfficialShaderCatalog.ValidateVpm(new byte[] {239,187,191}.Concat(Bytes(vpm)).ToArray());
        foreach (string invalid in new[] { vpm.Replace(file.Sha256, new string('0', 64)), vpm.Replace(file.Url, "https://example.invalid/file.zip"), vpm.Replace("2.4.1", "2.4.2"), "{}" })
        {
            try { OfficialShaderCatalog.ValidateVpm(Bytes(invalid)); throw new Exception("Untrusted VPM metadata accepted"); }
            catch (System.Xml.XmlException) { }
            catch (System.Runtime.Serialization.SerializationException) { }
            catch (InvalidDataException) { }
        }
        string root = Path.Combine(Path.GetTempPath(), "AvaShaderTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = Bytes("verified cached shader source");
            string checksum = OfficialShaderCatalog.Hash(data), cached = Path.Combine(root, "source.zip");
            File.WriteAllBytes(cached, data);
            if (!OfficialShaderCatalog.Matches(cached, checksum) || OfficialShaderCatalog.Matches(cached, new string('0', 64)) || OfficialShaderCatalog.Matches(cached, checksum, 1))
                throw new Exception("Download integrity checks failed");
            var release = new OfficialShaderRelease("test", "test", "1", "", new OfficialShaderDownload("source.zip", "https://example.invalid/never-download", checksum));
            OfficialShaderCatalog.Download(release, root, null, CancellationToken.None).GetAwaiter().GetResult();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { OfficialShaderCatalog.Download(release, root, null, cancel.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            var assets = new List<UnityPackageReader.PackageAsset> {
                new UnityPackageReader.PackageAsset("1", "Assets/Shader/Test.shader", Bytes("Shader source"), "guid: 1"),
                new UnityPackageReader.PackageAsset("2", "Assets/Shader/Editor/Untrusted.cs", Bytes("must never execute"), "guid: 2"),
                new UnityPackageReader.PackageAsset("3", "Assets/Shader/plugin.dll", new byte[] {1,2,3}, "guid: 3"),
                new UnityPackageReader.PackageAsset("4", "Assets/Shader/Test.cginc", Bytes("include"), "guid: 4")
            };
            string project = Path.Combine(root, "Project");
            PackageShaderCompiler.Prepare(assets, "trusted builder", project);
            if (!File.Exists(Path.Combine(project, "Assets/Shader/Test.shader.meta")) || !File.Exists(Path.Combine(project, "Assets/Shader/Test.cginc")) ||
                File.Exists(Path.Combine(project, "Assets/Shader/Editor/Untrusted.cs")) || File.Exists(Path.Combine(project, "Assets/Shader/plugin.dll")) ||
                File.ReadAllText(Path.Combine(project, "Assets/AvaPack/Editor/PackageShaderBuilder.cs")) != "trusted builder")
                throw new Exception("UnityPackage extraction isolation failed");
            assets.Add(new UnityPackageReader.PackageAsset("5", "Assets/../../escape.shader", Bytes("escape"), "guid: 5"));
            try { PackageShaderCompiler.Prepare(assets, "trusted builder", project); throw new Exception("Extraction traversal accepted"); }
            catch (InvalidDataException) { }
            Console.WriteLine("PASS shader installer: VPM identity/BOM, source integrity, offline cache, cancellation, isolated extraction");
        }
        finally { Directory.Delete(root, true); }
    }
}
