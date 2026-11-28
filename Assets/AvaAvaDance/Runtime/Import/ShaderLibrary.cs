using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace AvaAvaDance.Import
{
    [Serializable] public sealed class ShaderPackManifest
    {
        public int schema;
        public string target;
        public string unity;
        public string pipeline;
        public string graphicsApi;
        public ShaderPackEntry[] shaders;
        public VpmPackageIdentity vpmPackage;
    }
    [Serializable] public sealed class ShaderPassProfile {public string name,lightMode,usePass;public bool grab;}
    [Serializable] public sealed class GrabProfile {public int beforePass;public string texture;}
    [Serializable] public sealed class ShaderPackEntry
    {
        public string guid;
        public string name;
        public string assetPath;
        public GrabProfile[] grabPasses;
        public ShaderPassProfile[] passes;
    }

    /// <summary>Loads precompiled Unity shader assets from runtime shader packs.</summary>
    public sealed partial class ShaderLibrary : IDisposable
    {
        public const string ManifestAsset = "avadance-shader-manifest";
        public const string UnityVersion = "2022.3.22f1";
        public const string Target = "StandaloneWindows64";
        public const string Pipeline = "BuiltIn";
        public const string GraphicsApi = "Direct3D11";
        public const long MaxBytes = 256L * 1024 * 1024;
        const int MaxShaders = 64;
        readonly Dictionary<string, Shader> byGuid = new Dictionary<string, Shader>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (Shader shader, ShaderPackEntry entry)> byName = new(StringComparer.Ordinal);
        readonly Dictionary<string, (AssetBundle bundle, ShaderPackManifest manifest, byte[] bytes)> packs = new();
        readonly Dictionary<string, (ShaderPackManifest manifest, byte[] bytes)> selected = new();
        static readonly Regex GuidPattern = new Regex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant);
        partial void ReadCompiledPasses(ShaderPackManifest manifest,byte[] data);
        internal ShaderPackEntry GetEntry(string shaderName) =>
            byName.TryGetValue(shaderName, out var registered) ? registered.entry : null;
        internal IEnumerable<byte[]> ProjectPacks => packs.Values.Select(pack => pack.bytes);
        bool disposed, quitting;
        public ShaderLibrary() { Application.quitting += OnQuitting; }
        void OnQuitting() { quitting = true; }
        public int Count => byGuid.Count;
        public VpmPackageIdentity LoadedLilToon { get; private set; }

        public int Load(string path) => LoadPack(ReadPackBytes(path), rememberSelection: true);

        internal static byte[] ReadPackBytes(string path)
        {
            using var stream = File.OpenRead(path);
            if(!Path.GetExtension(path).Equals(".avadshader",StringComparison.OrdinalIgnoreCase) || stream.Length<1 || stream.Length>MaxBytes)
                throw new InvalidDataException("Invalid shader pack path or size.");
            using var reader = new BinaryReader(stream);
            return reader.ReadBytes((int)stream.Length);
        }

        internal int LoadVerifiedPack(byte[] bytes, string digest) => LoadPack(bytes, true, digest);

        internal void RestoreProjectPacks(IEnumerable<byte[]> saved) =>
            RestoreProjectPacksAsync(saved, false).GetAwaiter().GetResult();

        internal async Task RestoreProjectPacksAsync(IEnumerable<byte[]> saved, bool asynchronous)
        {
            var bytes = saved.ToArray();
            string[] Digests() => bytes.Select(OfficialShaderCatalog.Hash).ToArray();
            var keys = asynchronous && bytes.Length > 0 ? await Task.Run(Digests) : Digests();
            bool Needed(ShaderPackManifest manifest) =>
                !(LoadedLilToon != null && manifest.vpmPackage?.name == VpmInstaller.LilToon) &&
                manifest.shaders.Any(entry => Resolve(entry.guid) == null);
            if (packs.Keys.SequenceEqual(keys) && !selected.Values.Any(pack => Needed(pack.manifest))) return;

            ClearPacks();
            for (int i = 0; i < bytes.Length; i++)
                await LoadPackAsync(bytes[i], keys[i], false, asynchronous);
            // Saved packs take precedence over overlapping session shaders.
            foreach (var pair in selected)
                if (Needed(pair.Value.manifest))
                    await LoadPackAsync(pair.Value.bytes, pair.Key, false, asynchronous);
        }

        int LoadPack(byte[] bytes, bool rememberSelection = false, string verifiedDigest = null)
        {
            return LoadPackAsync(bytes, verifiedDigest, rememberSelection, false).GetAwaiter().GetResult();
        }

        static Task WaitForCompletion(AsyncOperation request)
        {
            if (request.isDone) return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            request.completed += _ => completion.TrySetResult(true);
            return completion.Task;
        }

        async Task<int> LoadPackAsync(byte[] bytes, string key, bool rememberSelection, bool asynchronous)
        {
            ThrowIfDisposed();
            if(bytes==null || bytes.Length==0 || bytes.LongLength>MaxBytes) throw new InvalidDataException("Invalid shader pack size.");
            key ??= OfficialShaderCatalog.Hash(bytes);
            if (packs.TryGetValue(key, out var existing))
            {
                if (rememberSelection) selected[key] = (existing.manifest, existing.bytes);
                return 0;
            }
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)
                throw new PlatformNotSupportedException("Shader packs currently target Windows x64 only.");
            if (!string.Equals(Application.unityVersion, UnityVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Shader pack loader requires Unity " + UnityVersion);
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null || UnityEngine.SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
                throw new PlatformNotSupportedException("Shader packs require the Built-in Render Pipeline with Direct3D11.");
            AssetBundle bundle = null;
            try
            {
                if (asynchronous)
                {
                    var request = AssetBundle.LoadFromMemoryAsync(bytes);
                    await WaitForCompletion(request);
                    bundle = request.assetBundle;
                }
                else bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null) throw new InvalidDataException("The file is not a supported shader AssetBundle.");
                var text = bundle.LoadAsset<TextAsset>(ManifestAsset);
                if (text == null || text.bytes.Length > 128 * 1024)
                    throw new InvalidDataException("Shader pack manifest is missing or too large.");
                var manifest = JsonUtility.FromJson<ShaderPackManifest>(text.text);
                ValidateManifest(manifest);
                bool isLilToon = manifest.vpmPackage?.name == VpmInstaller.LilToon;
                if (isLilToon && (LoadedLilToon != null || manifest.vpmPackage.repo != VpmInstaller.OfficialRepository ||
                    !Version.TryParse(manifest.vpmPackage.version, out _)))
                    throw new InvalidDataException("Invalid or duplicate lilToon VPM identity.");
                if (asynchronous) await Task.Run(() => ReadCompiledPasses(manifest, bytes));
                else ReadCompiledPasses(manifest, bytes);
                var added = new List<(Shader shader, ShaderPackEntry entry)>();
                foreach (var entry in manifest.shaders)
                {
                    if (byGuid.TryGetValue(entry.guid,out var existingShader) && existingShader.name==entry.name)
                        continue; // Packs can share a dependency. Keep existing material instances bound to it.
                    if (existingShader!=null || byName.ContainsKey(entry.name))
                        throw new InvalidDataException("Conflicting shader GUID or name: " + entry.name);
                    // Never use LoadAllAssets(), LoadAsset<GameObject>(), or instantiate bundle content.
                    Shader shader;
                    if (asynchronous)
                    {
                        var request = bundle.LoadAssetAsync<Shader>(entry.assetPath);
                        await WaitForCompletion(request);
                        shader = (Shader)request.asset;
                    }
                    else shader = bundle.LoadAsset<Shader>(entry.assetPath);
                    if (shader == null || !string.Equals(shader.name, entry.name, StringComparison.Ordinal) || !shader.isSupported)
                        throw new InvalidDataException("Shader missing, name mismatch, or unsupported graphics backend: " + entry.name);
                    added.Add((shader, entry));
                }
                foreach (var item in added) {
                    byGuid.Add(item.entry.guid, item.shader);
                    byName.Add(item.entry.name, item);
                }
                packs.Add(key, (bundle, manifest, bytes));
                bundle = null;
                if (rememberSelection) selected[key] = (manifest, bytes);
                if (isLilToon) LoadedLilToon = manifest.vpmPackage;
                return added.Count;
            }
            finally { if (bundle != null) bundle.Unload(true); }
        }

        public static void ValidateManifest(ShaderPackManifest manifest)
        {
            if (manifest == null || manifest.schema != 1 || manifest.target != Target || manifest.unity != UnityVersion || manifest.pipeline != Pipeline || manifest.graphicsApi != GraphicsApi)
                throw new InvalidDataException("Shader pack schema, target, Unity version, pipeline, or graphics API mismatch.");
            if (manifest.shaders == null || manifest.shaders.Length == 0 || manifest.shaders.Length > MaxShaders)
                throw new InvalidDataException("Shader pack must declare 1 to 64 shaders.");
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.shaders)
            {
                if (entry == null || entry.guid == null || !GuidPattern.IsMatch(entry.guid) || string.IsNullOrWhiteSpace(entry.name) || entry.name.Length > 256 || string.IsNullOrWhiteSpace(entry.assetPath) || entry.assetPath.Length > 512)
                    throw new InvalidDataException("Invalid shader identity.");
                if (!entry.assetPath.StartsWith("Assets/", StringComparison.Ordinal) && !entry.assetPath.StartsWith("Packages/", StringComparison.Ordinal))
                    throw new InvalidDataException("Shader asset path must be project-relative.");
                if (entry.assetPath.Contains("\\") || entry.assetPath.Contains(":") || entry.assetPath.Split('/').Any(p => p == ".." || p == "." || p.Length == 0))
                    throw new InvalidDataException("Invalid shader asset path.");
                if (!guids.Add(entry.guid) || !names.Add(entry.name) || !paths.Add(entry.assetPath))
                    throw new InvalidDataException("Duplicate shader identity in manifest.");
            }
        }

        public Shader Resolve(string guid, string exactName = null)
        {
            ThrowIfDisposed();
            if (!string.IsNullOrEmpty(guid) && byGuid.TryGetValue(guid, out var shader)) return shader;
            return !string.IsNullOrEmpty(exactName) && byName.TryGetValue(exactName, out var registered) ? registered.shader : null;
        }
        readonly BundledShaderCatalog bundled = Resources.Load<BundledShaderCatalog>("BundledShaderCatalog");
        internal bool IsAvailableMaterialShader(Shader shader) => byGuid.ContainsValue(shader) ||
            (bundled!=null && bundled.entries.Any(entry=>entry.shader==shader));
        internal Shader ResolveMaterial(string guid, long fileId, string exactName)
        {
            // A serialized .mat identifies an asset, including built-in subassets with a shared GUID.
            // VRM 0 materialProperties identifies a shader by its exact ShaderLab name.
            Shader shader = exactName == null
                ? (fileId == 4800000 ? Resolve(guid) : null) ?? bundled?.Find(guid,fileId)
                : Resolve(null,exactName) ?? Shader.Find(exactName);
            return shader != null && shader.isSupported ? shader : null;
        }
        void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(ShaderLibrary)); }
        /// <summary>Destroy all materials using these shaders before disposing the library.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Application.quitting -= OnQuitting;
            selected.Clear();
            ClearPacks();
        }

        private void ClearPacks()
        {
            byGuid.Clear();
            byName.Clear();
            LoadedLilToon = null;
            foreach (var pack in packs.Values) if (!quitting) pack.bundle.Unload(true);
            packs.Clear();
        }
    }
}

