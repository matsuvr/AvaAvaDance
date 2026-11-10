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
        static readonly string[] LilToonFiles = {
            "lts.shader", "lts_o.shader", "lts_cutout.shader", "lts_cutout_o.shader", "lts_trans.shader", "lts_trans_o.shader",
            "lts_onetrans.shader", "lts_onetrans_o.shader", "lts_twotrans.shader", "lts_twotrans_o.shader",
            "ltsmulti.shader", "ltsmulti_o.shader", "lts_fakeshadow.shader",
            "ltspass_opaque.shader", "ltspass_cutout.shader", "ltspass_transparent.shader"
        };

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
            var paths = LilToonFiles.Select(file => root + "/Shader/" + file).ToArray();
            ShaderPackBuilder.Build(paths, output + "/liltoon.avadshader", package);
        }
    }
}
