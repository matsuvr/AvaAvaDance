using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        readonly Dictionary<string, Shader> byName = new Dictionary<string, Shader>(StringComparer.Ordinal);
        readonly Dictionary<string, (AssetBundle bundle, ShaderPackManifest manifest, byte[] bytes)> packs = new();
        readonly Dictionary<string, (ShaderPackManifest manifest, byte[] bytes)> selected = new();
        static readonly Regex GuidPattern = new Regex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant);
        partial void ReadCompiledPasses(ShaderPackManifest manifest,byte[] data);
        internal ShaderPassProfile[] Passes(Shader shader)=>packs.Values.SelectMany(p=>p.manifest.shaders).FirstOrDefault(s=>s.name==shader.name)?.passes;
        internal GrabProfile[] GrabPasses(Shader shader) => packs.Values.SelectMany(p=>p.manifest.shaders).FirstOrDefault(s=>s.name==shader.name)?.grabPasses;
        internal IEnumerable<byte[]> ProjectPacks => packs.Values.Select(pack => pack.bytes);
        bool disposed, quitting;
        public ShaderLibrary() { Application.quitting += OnQuitting; }
        void OnQuitting() { quitting = true; }
        public int Count => byGuid.Count;
        public VpmPackageIdentity LoadedLilToon { get; private set; }

        public int Load(string path)
        {
            var info=new FileInfo(path);
            if(!info.Extension.Equals(".avadshader",StringComparison.OrdinalIgnoreCase) || !info.Exists || info.Length<1 || info.Length>MaxBytes)
                throw new InvalidDataException("Invalid shader pack path or size.");
            return LoadPack(File.ReadAllBytes(info.FullName), rememberSelection: true);
        }

        internal void RestoreProjectPacks(IEnumerable<byte[]> saved)
        {
            ClearPacks();
            foreach (var bytes in saved) LoadPack(bytes);
            foreach (var pack in selected.Values)
            {
                // A saved project takes precedence over overlapping session shaders.
                if (LoadedLilToon != null && pack.manifest.vpmPackage?.name == VpmInstaller.LilToon) continue;
                if (pack.manifest.shaders.Any(entry => Resolve(entry.guid) == null))
                    LoadPack(pack.bytes);
            }
        }

        private int LoadPack(byte[] bytes, bool rememberSelection = false)
        {
            ThrowIfDisposed();
            if(bytes==null || bytes.Length==0 || bytes.LongLength>MaxBytes) throw new InvalidDataException("Invalid shader pack size.");
            using var hash=System.Security.Cryptography.SHA256.Create();
            string key=BitConverter.ToString(hash.ComputeHash(bytes));
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
                bundle = AssetBundle.LoadFromMemory(bytes);
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
                ReadCompiledPasses(manifest,bytes);
                var addedGuid = new Dictionary<string, Shader>(StringComparer.OrdinalIgnoreCase);
                var addedName = new Dictionary<string, Shader>(StringComparer.Ordinal);
                foreach (var entry in manifest.shaders)
                {
                    if (byGuid.TryGetValue(entry.guid,out var existingShader) && existingShader.name==entry.name)
                        continue; // Packs can share a dependency. Keep existing material instances bound to it.
                    if (existingShader!=null || byName.ContainsKey(entry.name))
                        throw new InvalidDataException("Conflicting shader GUID or name: " + entry.name);
                    // Never use LoadAllAssets(), LoadAsset<GameObject>(), or instantiate bundle content.
                    var shader = bundle.LoadAsset<Shader>(entry.assetPath);
                    if (shader == null || !string.Equals(shader.name, entry.name, StringComparison.Ordinal) || !shader.isSupported)
                        throw new InvalidDataException("Shader missing, name mismatch, or unsupported graphics backend: " + entry.name);
                    addedGuid.Add(entry.guid, shader);
                    addedName.Add(entry.name, shader);
                }
                foreach (var pair in addedGuid) byGuid.Add(pair.Key, pair.Value);
                foreach (var pair in addedName) byName.Add(pair.Key, pair.Value);
                packs.Add(key, (bundle, manifest, bytes));
                bundle = null;
                if (rememberSelection) selected[key] = (manifest, bytes);
                if (isLilToon) LoadedLilToon = manifest.vpmPackage;
                return addedGuid.Count;
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
            return !string.IsNullOrEmpty(exactName) && byName.TryGetValue(exactName, out shader) ? shader : null;
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

