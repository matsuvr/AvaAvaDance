using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvaAvaDance.Import;
using UnityEditor;
using UnityEngine;

namespace AvaAvaDance.Editor
{
    /// <summary>Build-time compiler. No shader source compilation happens in the player.</summary>
    public static class ShaderPackBuilder
    {
        const string DisableOptimization = "LILTOON_DISABLE_OPTIMIZATION";
        const string ManifestFolder = "Assets/AvaAvaDance/GeneratedShaderPack";
        const string ManifestPath = ManifestFolder + "/manifest.json";
        static readonly string[] LilToonFiles = {
            "lts.shader", "lts_o.shader", "lts_cutout.shader", "lts_cutout_o.shader", "lts_trans.shader", "lts_trans_o.shader", "ltspass_opaque.shader", "ltspass_cutout.shader", "ltspass_transparent.shader"
        };

        [MenuItem("AvaAvaDance/Shader packs/1 Prepare full-feature lilToon")]
        public static void PrepareMenu() { PrepareLilToon(); }

        // Returns false after changing scripting defines. Wait for the domain reload and invoke again.
        public static bool PrepareLilToon()
        {
            RequireVersion();
            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone);
            var values = new HashSet<string>(defines.Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries));
            if (values.Add(DisableOptimization))
            {
                PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone, string.Join(";", values));
                Debug.Log("AVA_SHADER_PREPARE_RELOAD: wait for script compilation, then run Prepare again.");
                return false;
            }
#if !LILTOON_DISABLE_OPTIMIZATION
            throw new InvalidOperationException("Wait for script compilation with LILTOON_DISABLE_OPTIMIZATION before building shader packs.");
#else
            string package = "Assets/lilToon/package.json";
            if (!File.Exists(package) || !File.ReadAllText(package).Contains("\"2.4.1\""))
                throw new InvalidOperationException("Expected the pinned official lilToon 2.4.1 at Assets/lilToon.");
            var setting = ScriptableObject.CreateInstance<lilToonSetting>();
            try
            {
                setting.isDebugOptimize = false;
                lilToonSetting.TurnOnAllShaderSetting(ref setting);
                lilToonSetting.SaveShaderSetting(setting);
                if (!EditorApplication.ExecuteMenuItem("Assets/lilToon/[Shader] Refresh shaders"))
                    throw new InvalidOperationException("Official lilToon refresh command was not available.");
                // Deterministically generate all source and UsePass dependencies using the official public API.
                foreach (var source in Directory.GetFiles("Assets/lilToon/BaseShaderResources", "*.lilinternal"))
                {
                    string target = "Assets/lilToon/Shader/" + Path.GetFileNameWithoutExtension(source) + ".shader";
                    File.WriteAllText(target, lilToon.lilShaderContainer.UnpackContainer(source.Replace("\\\\", "/"), null, false));
                    AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Debug.Log("AVA_SHADER_PREPARED: all lilToon shader settings enabled; optimization disabled.");
                return true;
            }
            finally { UnityEngine.Object.DestroyImmediate(setting); }
#endif
        }

        [MenuItem("AvaAvaDance/Shader packs/2 Build bundled lilToon")]
        public static void BuildLilToon()
        {
            if (!PrepareLilToon()) throw new InvalidOperationException("Scripting define changed. Run BuildLilToon again after domain reload.");
            var paths = LilToonFiles.Select(f => "Assets/lilToon/Shader/" + f).ToArray();
            Build(paths, "Assets/StreamingAssets/liltoon.avadshader");
        }

        [MenuItem("AvaAvaDance/Shader packs/Build selected shaders for Windows")]
        public static void BuildSelected()
        {
            RequireVersion();
            var paths = Selection.objects.OfType<Shader>().Select(AssetDatabase.GetAssetPath).Distinct().ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("Select 1 to 64 Shader assets in the Project window.");
            string output = EditorUtility.SaveFilePanel("Save compiled shader pack", "", "custom.avadshader", "avadshader");
            if (string.IsNullOrEmpty(output)) return;
            Build(paths, output);
        }

        public static void Build(IReadOnlyList<string> shaderPaths, string output)
        {
            RequireVersion();
            if (shaderPaths == null || shaderPaths.Count < 1 || shaderPaths.Count > 64)
                throw new ArgumentException("Select 1 to 64 shaders.");
            var entries = new List<ShaderPackEntry>();
            foreach (string path in shaderPaths)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null) throw new InvalidOperationException("Shader asset missing: " + path);
                entries.Add(new ShaderPackEntry { guid = AssetDatabase.AssetPathToGUID(path), name = shader.name, assetPath = path });
            }
            var manifest = new ShaderPackManifest { schema = 1, target = ShaderLibrary.Target, unity = ShaderLibrary.UnityVersion, pipeline = ShaderLibrary.Pipeline, graphicsApi = ShaderLibrary.GraphicsApi, shaders = entries.ToArray() };
            ShaderLibrary.ValidateManifest(manifest);
            string destination = Path.GetFullPath(output);
            if (!string.Equals(Path.GetExtension(destination), ".avadshader", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must use .avadshader extension.");
            Directory.CreateDirectory(ManifestFolder);
            string manifestPath = ManifestFolder + "/" + entries[0].guid + ".json";
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
            AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport);
            var names = shaderPaths.Concat(new[] { manifestPath }).ToArray();
            var addresses = shaderPaths.Concat(new[] { ShaderLibrary.ManifestAsset }).ToArray();
            string temp = "Library/AvaShaderPacks/" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temp);
            string bundleName = "avashader-" + entries[0].guid + ".avadshader";
            var build = new AssetBundleBuild { assetBundleName = bundleName, assetNames = names, addressableNames = addresses };
            var result = BuildPipeline.BuildAssetBundles(temp, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
            if (result == null) throw new InvalidOperationException("Shader bundle build failed. Inspect the Unity Console.");
            string built = Path.Combine(temp, bundleName);
            if (!File.Exists(built) || new FileInfo(built).Length > ShaderLibrary.MaxBytes)
                throw new InvalidOperationException("Shader bundle missing or exceeds the 256 MiB runtime limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(built, destination, true);
            if (destination.StartsWith(Path.GetFullPath("Assets") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("AVA_SHADER_PACK_BUILT: " + destination + " (" + entries.Count + " shader identities)");
        }
        [MenuItem("AvaAvaDance/Shader packs/Validate manifest fixtures")]
        public static void ValidateFixtures()
        {
            Func<ShaderPackManifest> valid = () => new ShaderPackManifest {
                schema = 1, target = ShaderLibrary.Target, unity = ShaderLibrary.UnityVersion,
                pipeline = ShaderLibrary.Pipeline, graphicsApi = ShaderLibrary.GraphicsApi,
                shaders = new[] { new ShaderPackEntry { guid = "0123456789abcdef0123456789abcdef", name = "Test/Valid", assetPath = "Assets/Test.shader" } }
            };
            ShaderLibrary.ValidateManifest(valid());
            int rejected = 0;
            Action<string, Action<ShaderPackManifest>> reject = (name, mutate) => {
                var fixture = valid(); mutate(fixture);
                try { ShaderLibrary.ValidateManifest(fixture); }
                catch (InvalidDataException) { rejected++; return; }
                throw new Exception("Invalid manifest fixture was accepted: " + name);
            };
            reject("schema", m => m.schema = 2);
            reject("Unity version", m => m.unity = "6000.0.0f1");
            reject("target", m => m.target = "StandaloneLinux64");
            reject("pipeline", m => m.pipeline = "URP");
            reject("graphics API", m => m.graphicsApi = "Vulkan");
            reject("traversal", m => m.shaders[0].assetPath = "Assets/../Test.shader");
            reject("absolute path", m => m.shaders[0].assetPath = "C:/Test.shader");
            reject("invalid GUID", m => m.shaders[0].guid = "not-a-guid");
            reject("duplicate", m => m.shaders = new[] {m.shaders[0], m.shaders[0]});
            reject("too many", m => m.shaders = Enumerable.Range(0, 65).Select(i => new ShaderPackEntry {
                guid = i.ToString("x32"), name = "Test/" + i, assetPath = "Assets/Test" + i + ".shader"
            }).ToArray());
            reject("empty", m => m.shaders = new ShaderPackEntry[0]);
            reject("null entries", m => m.shaders = null);
            Debug.Log("AVA_SHADER_MANIFEST_TESTS_PASS: 1 valid, " + rejected + " invalid fixtures.");
        }

        static void RequireVersion()
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
                throw new InvalidOperationException("Shader packs support the Built-in Render Pipeline only.");
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64) || apis.Length != 1 || apis[0] != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
                throw new InvalidOperationException("Configure Windows graphics APIs explicitly to Direct3D11 only before building shader packs.");
            if (Application.unityVersion != ShaderLibrary.UnityVersion)
                throw new InvalidOperationException("Shader packs must be compiled with Unity " + ShaderLibrary.UnityVersion);
        }
    }
}

