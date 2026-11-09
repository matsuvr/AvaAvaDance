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
    }
    [Serializable] public sealed class ShaderPackEntry
    {
        public string guid;
        public string name;
        public string assetPath;
    }

    /// <summary>Loads trusted, precompiled shaders only. Never compiles package source or instantiates bundle objects.</summary>
    public sealed class ShaderLibrary : IDisposable
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
        readonly Dictionary<string, AssetBundle> bundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex GuidPattern = new Regex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant);
        bool disposed, quitting;
        public ShaderLibrary() { Application.quitting += OnQuitting; }
        void OnQuitting() { quitting = true; }
        public int Count => byGuid.Count;

        public int LoadBundled()
        {
            ThrowIfDisposed();
            string path = Path.Combine(Application.streamingAssetsPath, "liltoon.avadshader");
            return File.Exists(path) ? Load(path) : 0;
        }

        /// <summary>Only call for a pack explicitly selected by the user or shipped with the application.</summary>
        public int Load(string path)
        {
            ThrowIfDisposed();
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)
                throw new PlatformNotSupportedException("Shader packs currently target Windows x64 only.");
            if (!string.Equals(Application.unityVersion, UnityVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Shader pack loader requires Unity " + UnityVersion);
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null || UnityEngine.SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
                throw new PlatformNotSupportedException("Shader packs require the Built-in Render Pipeline with Direct3D11.");
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(full), ".avadshader", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select an .avadshader file.");
            var info = new FileInfo(full);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxBytes)
                throw new InvalidDataException("Shader pack must be between 1 byte and 256 MiB.");
            if (bundles.ContainsKey(full)) return 0;
            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(full);
                if (bundle == null) throw new InvalidDataException("The file is not a supported shader AssetBundle.");
                var text = bundle.LoadAsset<TextAsset>(ManifestAsset);
                if (text == null || text.bytes.Length > 128 * 1024)
                    throw new InvalidDataException("Shader pack manifest is missing or too large.");
                var manifest = JsonUtility.FromJson<ShaderPackManifest>(text.text);
                ValidateManifest(manifest);
                var addedGuid = new Dictionary<string, Shader>(StringComparer.OrdinalIgnoreCase);
                var addedName = new Dictionary<string, Shader>(StringComparer.Ordinal);
                foreach (var entry in manifest.shaders)
                {
                    if (byGuid.ContainsKey(entry.guid) || byName.ContainsKey(entry.name))
                        throw new InvalidDataException("A shader with this GUID or name is already registered: " + entry.name);
                    // Never use LoadAllAssets(), LoadAsset<GameObject>(), or instantiate bundle content.
                    var shader = bundle.LoadAsset<Shader>(entry.assetPath);
                    if (shader == null || !string.Equals(shader.name, entry.name, StringComparison.Ordinal) || !shader.isSupported)
                        throw new InvalidDataException("Shader missing, name mismatch, or unsupported graphics backend: " + entry.name);
                    addedGuid.Add(entry.guid, shader);
                    addedName.Add(entry.name, shader);
                }
                foreach (var pair in addedGuid) byGuid.Add(pair.Key, pair.Value);
                foreach (var pair in addedName) byName.Add(pair.Key, pair.Value);
                bundles.Add(full, bundle); bundle = null;
                return manifest.shaders.Length;
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
        void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(ShaderLibrary)); }
        /// <summary>Destroy all materials using these shaders before disposing the library.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Application.quitting -= OnQuitting;
            byGuid.Clear(); byName.Clear();
            foreach (var bundle in bundles.Values) if (!quitting && bundle != null) bundle.Unload(true);
            bundles.Clear();
        }
    }
}

