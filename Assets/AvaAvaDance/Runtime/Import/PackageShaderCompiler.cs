using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvaAvaDance.Import
{
    public static class PackageShaderCompiler
    {
        static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".shader", ".cginc", ".hlsl", ".hlslinc", ".glslinc", ".png", ".jpg", ".jpeg", ".tga", ".exr", ".dds", ".bmp", ".txt", ".md" };

        public static async Task<string> Compile(IReadOnlyList<UnityPackageReader.PackageAsset> assets, string trustedBuilder, string cache, Action<string> progress)
        {
            if (string.IsNullOrWhiteSpace(trustedBuilder)) throw new InvalidOperationException("シェーダー変換用データがありません。");
            if (!assets.Any(a => a.HasAsset && a.Path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("UnityPackage 内に ShaderLab シェーダーがありません。アバターはモデル操作の「読込」から選択してください。");
            string editor = FindEditor();
            if (editor == null) throw new FileNotFoundException("その他の UnityPackage の変換には Unity 2022.3.22f1 が必要です。Unity Hub で導入するか、コンパイル済み .avadshader を選択してください。");
            string project = Path.Combine(cache, "ShaderCompile", Guid.NewGuid().ToString("N"));
            progress?.Invoke("UnityPackage のシェーダーを変換中…（パッケージ内の C# は実行しません）");
            await Task.Run(() => Prepare(assets, trustedBuilder, project));
            string log = Path.Combine(project, "compile.log"), pack = Path.Combine(project, "Output", "shaders.avadshader");
            var start = new ProcessStartInfo(editor, "-batchmode -quit -buildTarget Win64 -projectPath \"" + project + "\" -executeMethod AvaPackageShaderBuilder.Build -logFile \"" + log + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            await Task.Run(() => {
                using var process = Process.Start(start);
                if (!process.WaitForExit(600000)) { process.Kill(); throw new TimeoutException("シェーダー変換がタイムアウトしました。ログ: " + log); }
                if (process.ExitCode != 0 || !File.Exists(pack)) throw new InvalidDataException("シェーダー変換に失敗しました。対応していない依存ファイルがある可能性があります。ログ: " + log);
            });
            return pack;
        }

        internal static void Prepare(IReadOnlyList<UnityPackageReader.PackageAsset> assets, string trustedBuilder, string project)
        {
            string root = Path.GetFullPath(project) + Path.DirectorySeparatorChar;
            long size = 0;
            foreach (var asset in assets)
            {
                if (!asset.HasAsset || !Extensions.Contains(Path.GetExtension(asset.Path))) continue;
                if (!asset.Path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if ((size += asset.Data.LongLength) > 256L * 1024 * 1024) throw new InvalidDataException("シェーダー展開サイズが上限を超えました。");
                string path = Path.GetFullPath(Path.Combine(project, asset.Path));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("UnityPackage のパスが不正です。");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, asset.Data);
                if (!string.IsNullOrEmpty(asset.Meta)) File.WriteAllText(path + ".meta", asset.Meta, new UTF8Encoding(false));
            }
            string builder = Path.Combine(project, "Assets", "AvaPack", "Editor");
            Directory.CreateDirectory(builder);
            File.WriteAllText(Path.Combine(builder, "PackageShaderBuilder.cs"), trustedBuilder, new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.22f1\n");
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{\"dependencies\":{\"com.unity.modules.assetbundle\":\"1.0.0\",\"com.unity.modules.imageconversion\":\"1.0.0\"}}");
        }

        static string FindEditor()
        {
            string expected = Path.Combine("2022.3.22f1", "Editor", "Unity.exe");
            string configured = Environment.GetEnvironmentVariable("AVA_UNITY_EDITOR");
            var candidates = new[] { configured, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor", expected),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity 2022.3.22f1", "Editor", "Unity.exe") };
            return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        }
    }
}
