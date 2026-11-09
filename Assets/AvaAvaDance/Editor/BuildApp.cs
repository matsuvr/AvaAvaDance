using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
namespace AvaAvaDance.Editor
{
    public static class BuildApp
    {
        public static void Windows()
        {
            ProjectSetup.Configure();
            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>("Assets/AvaAvaDance/Resources/MainPanel.asset");
            if(panel == null) { panel=ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>(); AssetDatabase.CreateAsset(panel,"Assets/AvaAvaDance/Resources/MainPanel.asset"); }
            panel.themeStyleSheet=Resources.Load<UnityEngine.UIElements.ThemeStyleSheet>("ClassicTheme"); EditorUtility.SetDirty(panel); AssetDatabase.SaveAssets();
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders = settings.FindProperty("m_AlwaysIncludedShaders");
            var shader = Shader.Find("Standard");
            if (shader == null) throw new Exception("Standard shader not found");
            bool found=false;
            for(int i=0;i<shaders.arraySize;i++) if(shaders.GetArrayElementAtIndex(i).objectReferenceValue==shader) found=true;
            if (!found) { int n=shaders.arraySize; shaders.InsertArrayElementAtIndex(n); shaders.GetArrayElementAtIndex(n).objectReferenceValue=shader; settings.ApplyModifiedProperties(); }
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes=new[]{"Assets/Scenes/Main.unity"}, locationPathName="Builds/Windows/AvaAvaDance.exe",
                target=BuildTarget.StandaloneWindows64, options=BuildOptions.None
            });
            if(report.summary.result != BuildResult.Succeeded) throw new Exception("Build failed: " + report.summary.result);
            Debug.Log("AVA_BUILD_OK " + report.summary.totalSize);
        }
    }
}
