using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace AvaAvaDance.Import
{
    public static class LilToonCompiler
    {
        public const string SourceFolder = "ShaderCompiler";
        const int ArchiveLimit = 16 * 1024 * 1024;
        const long ExtractedLimit = 256L * 1024 * 1024;
        static readonly string[] BuilderSources = {
            "Tools/LilToonPackBuilder.cs",
            "Assets/AvaAvaDance/Editor/ShaderPackBuilder.cs",
            "Assets/AvaAvaDance/Runtime/Import/ShaderLibrary.cs",
            "Assets/AvaAvaDance/Runtime/Import/OfficialShaderCatalog.cs",
            "Assets/AvaAvaDance/Runtime/Import/BundledShaderCatalog.cs",
            "Assets/AvaAvaDance/Runtime/Import/VpmInstaller.cs"
        };
        static readonly string[] UnityModules = {
            "animation", "assetbundle", "audio", "director", "imageconversion", "imgui", "jsonserialize",
            "particlesystem", "screencapture", "ui", "uielements", "unitywebrequest", "unitywebrequestaudio", "video"
        };

        public static void CopySources(string sourceRoot, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string path in BuilderSources)
                File.Copy(Path.Combine(sourceRoot, path), Path.Combine(destination, Path.GetFileName(path)), true);
        }

        internal static async Task<string> Compile(string applicationRoot, bool inEditor, string cache,
            IProgress<string> progress, CancellationToken cancellation)
        {
            string editor = PackageShaderCompiler.FindEditor();
            if (editor == null) throw new FileNotFoundException(
                "公式 lilToon を変換するには Windows 版の Unity " + ShaderLibrary.UnityVersion + " が必要です。Unity Hub から導入してください。");
            var release = OfficialShaderCatalog.LilToon;
            await OfficialShaderCatalog.Download(release, cache, progress, cancellation);
            var download = release.Downloads[0];
            string project = Path.Combine(cache, "Compiler", Guid.NewGuid().ToString("N"));
            progress?.Report("取得した公式 lilToon を別の Unity プロジェクトで変換中…");
            await Task.Run(() =>
            {
                using var source = File.OpenRead(Path.Combine(cache, download.FileName));
                if (source.Length < 1 || source.Length > ArchiveLimit) throw new InvalidDataException("lilToon ZIP のサイズが不正です。");
                using var reader = new BinaryReader(source);
                byte[] zip = reader.ReadBytes(ArchiveLimit + 1);
                if (zip.Length > ArchiveLimit || OfficialShaderCatalog.Hash(zip) != download.Sha256)
                    throw new InvalidDataException("lilToon ZIP が公式配布物と一致しません。");
                Prepare(project, zip, applicationRoot, inEditor, cancellation);
            }, cancellation);
            return await PackageShaderCompiler.RunEditor(editor, project,
                "AvaAvaDance.Editor.LilToonPackBuilder.Build", "liltoon.avadshader", cancellation);
        }

        static void Prepare(string project, byte[] zip, string applicationRoot, bool inEditor, CancellationToken cancellation)
        {
            string package = Path.GetFullPath(Path.Combine(project, "Assets", "lilToon")) + Path.DirectorySeparatorChar;
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false));
            long extracted = 0;
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.Name.Length == 0) continue;
                string path = Path.GetFullPath(Path.Combine(package, entry.FullName));
                if (!path.StartsWith(package, StringComparison.OrdinalIgnoreCase) || (extracted += entry.Length) > ExtractedLimit)
                    throw new InvalidDataException("lilToon ZIP のパスまたは展開サイズが不正です。");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew);
                input.CopyTo(output);
            }
            string builder = Path.Combine(project, "Assets", "AvaAvaDance", "Editor");
            if (inEditor) CopySources(applicationRoot, builder);
            else
            {
                Directory.CreateDirectory(builder);
                foreach (string path in BuilderSources)
                {
                    string name = Path.GetFileName(path);
                    File.Copy(Path.Combine(applicationRoot, SourceFolder, name), Path.Combine(builder, name));
                }
            }
            File.WriteAllText(Path.Combine(builder, "AvaAvaDance.PackEditor.asmdef"),
                "{\"name\":\"AvaAvaDance.PackEditor\",\"references\":[\"lilToon.Editor\"],\"includePlatforms\":[\"Editor\"]}");
            File.WriteAllText(Path.Combine(project, "Assets", "csc.rsp"), "-define:LILTOON_DISABLE_OPTIMIZATION\n");
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: " + ShaderLibrary.UnityVersion + "\n");
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            var manifest = "{\"dependencies\":{\"com.unity.ugui\":\"1.0.0\"";
            foreach (string module in UnityModules) manifest += ",\"com.unity.modules." + module + "\":\"1.0.0\"";
            File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), manifest + "}}");
        }
    }
}
