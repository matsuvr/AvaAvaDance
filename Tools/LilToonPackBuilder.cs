using System;
using System.IO;
using System.Linq;
using AvaAvaDance.Import;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance.Editor
{
    // Installed by restore_liltoon.py into the separate pack-authoring project.
    public static class LilToonPackBuilder
    {
        static readonly string[] EditorOnlyShaders = { "ltspass_baker.shader", "ltspass_bakeramp.shader", "ltspass_dummy.shader", "ltspass_proponly.shader" };

        [MenuItem("AvaAvaDance/Build external lilToon pack")]
        public static void Build()
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            const string root = "Assets/lilToon";
            var package = JsonUtility.FromJson<VpmPackageIdentity>(File.ReadAllText(root + "/package.json"));
            if (package.name != VpmInstaller.LilToon || package.version != "2.4.1" || package.repo != VpmInstaller.OfficialRepository)
                throw new InvalidDataException("Expected the pinned lilToon VPM release.");

            var settings = ScriptableObject.CreateInstance<lilToonSetting>();
            try
            {
                settings.isDebugOptimize = false;
                lilToonSetting.TurnOnAllShaderSetting(ref settings);
                lilToonSetting.SaveShaderSetting(settings);
                if (!EditorApplication.ExecuteMenuItem("Assets/lilToon/[Shader] Refresh shaders"))
                    throw new InvalidOperationException("lilToon shader generation is unavailable.");
                foreach (var template in Directory.GetFiles(root + "/BaseShaderResources", "*.lilinternal"))
                {
                    var target = root + "/Shader/" + Path.GetFileNameWithoutExtension(template) + ".shader";
                    File.WriteAllText(target, lilToon.lilShaderContainer.UnpackContainer(template.Replace('\\', '/'), null, false));
                    AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                AssetDatabase.SaveAssets();
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }

            const string output = "Output";
            Directory.CreateDirectory(output);
            File.Copy(root + "/LICENSE", output + "/lilToon-LICENSE.txt", true);
            File.Copy(root + "/Third Party Notices.md", output + "/lilToon-Third-Party-Notices.md", true);
            File.Copy(root + "/Shader/Includes/VRC Light Volumes/license.txt", output + "/VRC-Light-Volumes-LICENSE.txt", true);
            var paths = Directory.GetFiles(root + "/Shader", "*.shader")
                .Where(path => !EditorOnlyShaders.Contains(Path.GetFileName(path)))
                .Select(path => path.Replace('\\', '/')).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            ShaderPackBuilder.Build(paths, output + "/liltoon.avadshader", package);
        }
    }
}
