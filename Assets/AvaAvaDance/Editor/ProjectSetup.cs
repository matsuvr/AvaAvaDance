using UnityEditor;
using UnityEditor.SceneManagement;

namespace AvaAvaDance.Editor
{
    public static class ProjectSetup
    {
        [MenuItem("AvaAvaDance/Open UI prototype")]
        public static void Open()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorApplication.isPlaying = true;
        }

        [MenuItem("AvaAvaDance/Configure standalone build")]
        public static void Configure()
        {
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/Main.unity", true) };
            PlayerSettings.productName = "AvaAvaDance";
            PlayerSettings.companyName = "matsuvr";
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = UnityEngine.FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
        }
    }
}
