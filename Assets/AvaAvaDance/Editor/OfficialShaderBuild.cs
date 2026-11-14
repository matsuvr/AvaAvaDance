using System;
using System.IO;
using AvaAvaDance.Import;
using UnityEditor;
using UnityEngine;

namespace AvaAvaDance.Editor
{
    public static class OfficialShaderBuild
    {
        [MenuItem("AvaAvaDance/Shader packs/Stage official shader packages")]
        public static void Stage()
        {
            StageOne(OfficialShaderCatalog.LilToon, "Builds/LilToonPackProject/Output/liltoon.avadshader");
            StageOne(OfficialShaderCatalog.Vrm, "Builds/VrmShaderPackProject/Output/shaders.avadshader");
        }

        static void StageOne(OfficialShaderRelease release, string source)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Run Tools/restore_official_shaders.py and compile both official shader projects before building.", source);
            string folder = Path.Combine(OfficialShaderStore.PayloadDirectory, release.Id);
            Directory.CreateDirectory(folder);
            string pack = Path.Combine(folder, "shaders.avadshader");
            File.Copy(source, pack, true);
            var metadata = new OfficialShaderPayload {
                id = release.Id, version = release.Version, unity = ShaderLibrary.UnityVersion,
                packSha256 = OfficialShaderCatalog.Hash(File.ReadAllBytes(pack)),
                sourceSha256 = Array.ConvertAll(release.Downloads, d => d.Sha256)
            };
            File.WriteAllText(Path.Combine(folder, "install.json"), JsonUtility.ToJson(metadata, true));
            string license = "Assets/AvaAvaDance/Resources/ShaderLicenses/" + release.Id + ".txt";
            File.Copy(license, Path.Combine(folder, "LICENSE.txt"), true);
            OfficialShaderStore.ValidatePayload(release);
        }

        public static void CopyTo(string output)
        {
            foreach (var release in OfficialShaderCatalog.All)
            {
                OfficialShaderStore.ValidatePayload(release);
                string folder = Path.Combine(output, "ShaderPackages", release.Id);
                Directory.CreateDirectory(folder);
                foreach (string name in new[] { "shaders.avadshader", "install.json", "LICENSE.txt" })
                    File.Copy(Path.Combine(OfficialShaderStore.PayloadDirectory, release.Id, name), Path.Combine(folder, name), true);
            }
        }
    }
}
