using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace AvaAvaDance.Import
{
    public sealed class OfficialShaderDownload
    {
        public readonly string FileName, Url, Sha256;
        public OfficialShaderDownload(string fileName, string url, string sha256)
        { FileName = fileName; Url = url; Sha256 = sha256; }
    }

    public sealed class OfficialShaderRelease
    {
        public readonly string Id, Title, Version, Page;
        public readonly OfficialShaderDownload[] Downloads;
        public OfficialShaderRelease(string id, string title, string version, string page, params OfficialShaderDownload[] downloads)
        { Id = id; Title = title; Version = version; Page = page; Downloads = downloads; }
    }

    public static class OfficialShaderCatalog
    {
        public static readonly OfficialShaderRelease LilToon = new OfficialShaderRelease("liltoon", "lilToon", "2.4.1",
            "https://github.com/lilxyzw/lilToon/releases/tag/2.4.1",
            new OfficialShaderDownload("jp.lilxyzw.liltoon-2.4.1.zip", "https://github.com/lilxyzw/lilToon/releases/download/2.4.1/jp.lilxyzw.liltoon-2.4.1.zip?",
                "90f8bed7cdd7435f4904bca3d885a81a1811bd25036d712f21942c531d940247"));
        public static readonly OfficialShaderRelease Vrm = new OfficialShaderRelease("vrm", "VRM (MToon / MToon10)", "0.131.4",
            "https://github.com/vrm-c/UniVRM/releases/tag/v0.131.4",
            new OfficialShaderDownload("UniVRM-0.131.4_efd2.unitypackage", "https://github.com/vrm-c/UniVRM/releases/download/v0.131.4/UniVRM-0.131.4_efd2.unitypackage",
                "3f0e7349f22914232c0621ce1a0f48b549c2553bc5d3308896cd4612730c445a"),
            new OfficialShaderDownload("VRM-0.131.4_efd2.unitypackage", "https://github.com/vrm-c/UniVRM/releases/download/v0.131.4/VRM-0.131.4_efd2.unitypackage",
                "8f91761bfe9cb498f29f7d66b5750e64909078569ffbb397ecbcfa1edd881f27"));
        public static readonly OfficialShaderRelease[] All = { LilToon, Vrm };
        const int MaxDownload = 32 * 1024 * 1024;

        public static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static bool Matches(string path, string sha256, long limit = MaxDownload)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1 || info.Length > limit) return false;
            using var sha = SHA256.Create();
            using var input = File.OpenRead(path);
            return string.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""), sha256, StringComparison.OrdinalIgnoreCase);
        }

        public static void ValidateVpm(byte[] json)
        {
            if (json == null || json.Length > 4 * 1024 * 1024) throw new InvalidDataException("VPM インデックスのサイズが不正です。");
            int offset = json.Length >= 3 && json[0] == 239 && json[1] == 187 && json[2] == 191 ? 3 : 0;
            using var input = new MemoryStream(json, offset, json.Length - offset, false);
            using var reader = JsonReaderWriterFactory.CreateJsonReader(input, new XmlDictionaryReaderQuotas { MaxDepth = 32, MaxStringContentLength = 1024 * 1024 });
            var root = XElement.Load(reader);
            // JSON object keys beginning with digits use the XML JSON mapping's item/name form.
            XElement Member(XElement parent, string key) => parent?.Elements().SingleOrDefault(e => e.Name.LocalName == key || (string)e.Attribute("item") == key);
            var release = Member(Member(Member(root, "packages"), VpmInstaller.LilToon), "versions");
            release = Member(release, LilToon.Version);
            var expected = LilToon.Downloads[0];
            if ((string)Member(release, "name") != VpmInstaller.LilToon || (string)Member(release, "version") != LilToon.Version ||
                (string)Member(release, "repo") != VpmInstaller.OfficialRepository || (string)Member(release, "url") != expected.Url ||
                (string)Member(release, "zipSHA256") != expected.Sha256)
                throw new InvalidDataException("公式VPMの配布情報が対応バージョンと一致しません。アプリの更新を確認してください。");
        }

        public static async Task Download(OfficialShaderRelease release, string directory, IProgress<string> progress, CancellationToken cancellation)
        {
            Directory.CreateDirectory(directory);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AvaAvaDance/1.0");
            var missing = release.Downloads.Where(d => !Matches(Path.Combine(directory, d.FileName), d.Sha256)).ToArray();
            if (release == LilToon && missing.Length > 0)
            {
                progress?.Report("lilToon の公式VPMを確認中…");
                ValidateVpm(await Get(client, VpmInstaller.OfficialRepository, 4 * 1024 * 1024, cancellation).ConfigureAwait(false));
            }
            foreach (var download in missing)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Report(release.Title + " をダウンロード中… " + download.FileName);
                var data = await Get(client, download.Url, MaxDownload, cancellation).ConfigureAwait(false);
                if (Hash(data) != download.Sha256) throw new InvalidDataException("ダウンロードしたファイルの SHA-256 が公式配布物と一致しません。");
                string target = Path.Combine(directory, download.FileName), temporary = target + "." + Guid.NewGuid().ToString("N") + ".download";
                try
                {
                    File.WriteAllBytes(temporary, data);
                    cancellation.ThrowIfCancellationRequested();
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            cancellation.ThrowIfCancellationRequested();
        }

        static async Task<byte[]> Get(HttpClient client, string url, int limit, CancellationToken cancellation)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("ダウンロードのサイズ上限を超えました。");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("ダウンロードのサイズ上限を超えました。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }
}
