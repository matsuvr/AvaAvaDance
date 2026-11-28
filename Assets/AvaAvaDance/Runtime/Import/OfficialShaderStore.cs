using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AvaAvaDance.Import
{
    [Serializable] public sealed class OfficialShaderPayload
    {
        public string id, version, unity, packSha256;
        public string[] sourceSha256;
    }
    [Serializable] sealed class OfficialShaderConsent
    {
        public string id, version, acceptedUtc, packSha256;
        public string[] sourceSha256;
    }

    public static class OfficialShaderStore
    {
        internal static string VerificationCacheRoot;
        public static string PayloadDirectory
        {
            get
            {
                if (Application.isEditor)
                    return Path.GetFullPath(Path.Combine(Application.dataPath, "../Builds/OfficialShaderPackages"));
                // On macOS dataPath is the bundle's Contents directory.
                if (Application.platform == RuntimePlatform.OSXPlayer)
                    return Path.Combine(Application.dataPath, "Resources", "ShaderPackages");
                return Path.GetFullPath(Path.Combine(Application.dataPath, "../ShaderPackages"));
            }
        }
        public static string CacheDirectory(OfficialShaderRelease release) => Path.Combine(VerificationCacheRoot ?? Path.Combine(Application.persistentDataPath, "ShaderPackages"), release.Id, release.Version);

        static OfficialShaderPayload ReadPayloadMetadata(OfficialShaderRelease release, out string pack)
        {
            if (release != OfficialShaderCatalog.Vrm)
                throw new InvalidOperationException("このシェーダーは外部シェーダーパックから読み込んでください。");
            string directory = Path.Combine(PayloadDirectory, release.Id);
            string metadata = Path.Combine(directory, "install.json");
            pack = Path.Combine(directory, "shaders.avadshader");
            if (!File.Exists(metadata) || new FileInfo(metadata).Length > 8192)
                throw new FileNotFoundException("シェーダーの描画用データがありません。ShaderPackages フォルダーを含むアプリ一式を配置してください。");
            var payload = JsonUtility.FromJson<OfficialShaderPayload>(File.ReadAllText(metadata));
            if (payload == null || payload.id != release.Id || payload.version != release.Version || payload.unity != ShaderLibrary.UnityVersion ||
                payload.sourceSha256 == null || !payload.sourceSha256.SequenceEqual(release.Downloads.Select(d => d.Sha256)) ||
                string.IsNullOrEmpty(payload.packSha256))
                throw new InvalidDataException("シェーダーの描画用データが公式配布バージョンと一致しません。");
            return payload;
        }

        public static string ValidatePayload(OfficialShaderRelease release)
        {
            var payload = ReadPayloadMetadata(release, out string pack);
            if (!OfficialShaderCatalog.Matches(pack, payload.packSha256, ShaderLibrary.MaxBytes))
                throw new InvalidDataException("シェーダーの描画用データが公式配布バージョンと一致しません。");
            return pack;
        }

        // Invoked only by the explicit consent button, never while opening the menu or license.
        public static async Task<string> Prepare(OfficialShaderRelease release, IProgress<string> progress, CancellationToken cancellation)
        {
            string pack = ValidatePayload(release), directory = CacheDirectory(release);
            await OfficialShaderCatalog.Download(release, directory, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return pack;
        }

        public static void RememberConsent(OfficialShaderRelease release, string pack)
        {
            var receipt = new OfficialShaderConsent {
                id = release.Id, version = release.Version, acceptedUtc = DateTime.UtcNow.ToString("O"),
                packSha256 = OfficialShaderCatalog.Hash(File.ReadAllBytes(pack)), sourceSha256 = release.Downloads.Select(d => d.Sha256).ToArray()
            };
            File.WriteAllText(Path.Combine(CacheDirectory(release), "consent.json"), JsonUtility.ToJson(receipt, true));
        }

        public static bool HasConsent(OfficialShaderRelease release) => ReadConsentedPack(release, out _) != null;

        internal static void Restore(OfficialShaderRelease release, ShaderLibrary library)
        {
            var bytes = ReadConsentedPack(release, out string digest);
            if (bytes != null) library.LoadVerifiedPack(bytes, digest);
        }

        static byte[] ReadConsentedPack(OfficialShaderRelease release, out string digest)
        {
            digest = null;
            string directory = CacheDirectory(release), receiptPath = Path.Combine(directory, "consent.json");
            if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 8192) return null;
            var receipt = JsonUtility.FromJson<OfficialShaderConsent>(File.ReadAllText(receiptPath));
            if (receipt == null || receipt.id != release.Id || receipt.version != release.Version || string.IsNullOrEmpty(receipt.acceptedUtc) ||
                receipt.sourceSha256 == null || !receipt.sourceSha256.SequenceEqual(release.Downloads.Select(d => d.Sha256))) return null;
            var payload = ReadPayloadMetadata(release, out string path);
            if (!string.Equals(receipt.packSha256, payload.packSha256, StringComparison.OrdinalIgnoreCase) ||
                !release.Downloads.All(d => OfficialShaderCatalog.Matches(Path.Combine(directory, d.FileName), d.Sha256))) return null;
            var bytes = ShaderLibrary.ReadPackBytes(path);
            digest = OfficialShaderCatalog.Hash(bytes);
            if (!string.Equals(digest, payload.packSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("シェーダーの描画用データが公式配布バージョンと一致しません。");
            return bytes;
        }
    }
}
