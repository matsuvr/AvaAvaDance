using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
namespace AvaAvaDance.Editor
{
    public static class BuildApp
    {
        public static void Windows()
        {
            foreach (var input in new[] { "Assets/AvaAvaDance/Resources/MPLUS1p-Regular.ttf", "Assets/Plugins/x86_64/AvaFbx.dll",
                "Assets/Plugins/x86_64/AvaBullet.dll", "Assets/Plugins/x86_64/AvaMp4.dll" })
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
            Debug.Log("AVA_BUILD_OK " + report.summary.totalSize);
        }
    }
}
