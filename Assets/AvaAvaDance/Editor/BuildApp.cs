using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
namespace AvaAvaDance.Editor
{
    public static class BuildApp
    {
        [Serializable] sealed class PackageManifest
        {
            public string id;
            public Entry entry;
        }
        [Serializable] sealed class Entry { public string assembly; }
        static void BuildExtensions(string output)
        {
            foreach(var folder in Directory.GetDirectories("Extensions"))
            {
                string manifest=Path.Combine(folder,"plugin.json");
                if(!File.Exists(manifest))continue;
                var package=JsonUtility.FromJson<PackageManifest>(File.ReadAllText(manifest));
                var project=Directory.GetFiles(folder,"*.csproj")[0];
                var start=new System.Diagnostics.ProcessStartInfo("dotnet","build \""+Path.GetFullPath(project)+"\" -c Release --nologo")
                    {UseShellExecute=false,CreateNoWindow=true};
                using(var process=System.Diagnostics.Process.Start(start))
                {
                    process.WaitForExit(); if(process.ExitCode!=0)throw new Exception("拡張ビルド失敗 "+package.id);
                }
                var target=Path.Combine(output,"Extensions",package.id);
                var assembly=Path.Combine(target,package.entry.assembly);
                Directory.CreateDirectory(Path.GetDirectoryName(assembly));
                string artifacts=Path.Combine(folder,"bin","Release","netstandard2.1");
                foreach(var dll in Directory.GetFiles(artifacts,"*.dll"))
                    if(Path.GetFileName(dll)!="Ava.ExtensionSdk.dll")
                        File.Copy(dll,Path.Combine(Path.GetDirectoryName(assembly),Path.GetFileName(dll)),true);
                File.Copy(manifest,Path.Combine(target,"plugin.json"),true);
            }
        }

        public static void Windows()
        {
            OfficialShaderBuild.Stage();
            foreach (var input in new[] { "Assets/AvaAvaDance/Resources/MPLUS1p-Regular.ttf", "Assets/Plugins/x86_64/AvaFbx.dll",
                "Assets/Plugins/x86_64/AvaRenderBridge.dll", "Assets/Plugins/x86_64/AvaClassicUi.dll", "Assets/Plugins/x86_64/AvaBullet.dll", "Assets/Plugins/x86_64/AvaMp4.dll" })
                if (!File.Exists(input)) throw new FileNotFoundException("Run python Tools/restore_inputs.py and Native/build.ps1 before building.", input);
            ProjectSetup.Configure();
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>("Assets/AvaAvaDance/Resources/MainPanel.asset");
            if(panel == null) { panel=ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>(); AssetDatabase.CreateAsset(panel,"Assets/AvaAvaDance/Resources/MainPanel.asset"); }
            panel.themeStyleSheet=Resources.Load<UnityEngine.UIElements.ThemeStyleSheet>("ClassicTheme"); EditorUtility.SetDirty(panel); AssetDatabase.SaveAssets();
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders = settings.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in new[] { "Standard", "VRM10/MToon10", "UniGLTF/UniUnlit", "Hidden/UniGLTF/StandardMapImporter" })
            {
                var shader = Shader.Find(name);
                if (shader == null) throw new Exception("Shader not found: " + name);
                bool found=false;
                for(int i=0;i<shaders.arraySize;i++) if(shaders.GetArrayElementAtIndex(i).objectReferenceValue==shader) found=true;
                if (!found) { int n=shaders.arraySize; shaders.InsertArrayElementAtIndex(n); shaders.GetArrayElementAtIndex(n).objectReferenceValue=shader; }
            }
            settings.ApplyModifiedProperties();
            const string catalogPath = "Assets/AvaAvaDance/Resources/BundledShaderCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<Import.BundledShaderCatalog>(catalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<Import.BundledShaderCatalog>();
                AssetDatabase.CreateAsset(catalog, catalogPath);
            }
            var entries = new System.Collections.Generic.List<Import.BundledShaderCatalog.Entry>();
            for (int i = 0; i < shaders.arraySize; i++)
            {
                var shader = shaders.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (shader != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(shader, out string guid, out long id))
                    entries.Add(new Import.BundledShaderCatalog.Entry { shader=shader, guid=guid, fileId=id });
            }
            catalog.entries = entries.ToArray();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            var outputDirectory = Environment.GetEnvironmentVariable("AVA_WINDOWS_BUILD_DIR") ?? "Builds/Windows";
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes=new[]{"Assets/Scenes/Main.unity"}, locationPathName=Path.Combine(outputDirectory,"AvaAvaDance.exe"),
                target=BuildTarget.StandaloneWindows64, options=BuildOptions.None
            });
            if(report.summary.result != BuildResult.Succeeded) throw new Exception("Build failed: " + report.summary.result);
            var notices = Directory.CreateDirectory(Path.Combine(outputDirectory,"ThirdPartyNotices")).FullName;
            File.Copy("LICENSE", Path.Combine(outputDirectory, "LICENSE"), true);
            foreach (var source in Directory.GetFiles("ThirdPartyNotices", "*.txt"))
                File.Copy(source, Path.Combine(notices, Path.GetFileName(source)), true);
            OfficialShaderBuild.CopyTo(outputDirectory);
            BuildExtensions(outputDirectory);
            var mcpDirectory = Directory.CreateDirectory(Path.Combine(outputDirectory, "Mcp"));
            foreach (var script in new[] { "ava_mcp.py", "ava_mcp_windows.py" })
                File.Copy(Path.Combine("Tools", script), Path.Combine(mcpDirectory.FullName, script), true);
            File.Copy("Docs/mcp.md", Path.Combine(mcpDirectory.FullName, "README.md"), true);
            Debug.Log("AVA_BUILD_OK " + report.summary.totalSize);
        }
    }
}
