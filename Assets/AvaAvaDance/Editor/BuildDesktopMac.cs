using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AvaAvaDance.Editor
{
    public static class BuildDesktopMac
    {
        public static void Build()
        {
            string bundle = Environment.GetEnvironmentVariable("AVA_MACOS_BUILD_PATH")
                ?? Path.GetFullPath("Builds/macOS/Player/AvaAvaDance.app");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
                throw new InvalidOperationException("Start the Editor with -buildTarget osxuniversal.");
            ProjectSetup.Configure();
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Main.unity" }, locationPathName = bundle,
                target = BuildTarget.StandaloneOSX, options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new IOException("macOS Player build failed: " + report.summary.result);

            string resources = Path.Combine(bundle, "Contents", "Resources");
            BuildApp.BuildExtensions(resources, false, windows: false);
            File.Copy("LICENSE", Path.Combine(resources, "LICENSE"), true);
            var notices = Directory.CreateDirectory(Path.Combine(resources, "ThirdPartyNotices"));
            foreach (string file in Directory.GetFiles("ThirdPartyNotices", "*.txt"))
                File.Copy(file, Path.Combine(notices.FullName, Path.GetFileName(file)), true);
            var mcp = Directory.CreateDirectory(Path.Combine(resources, "Mcp"));
            File.Copy("Tools/ava_mcp.py", Path.Combine(mcp.FullName, "ava_mcp.py"), true);
            Debug.Log("AVA_MAC_PLAYER_OK " + bundle);
        }
    }
}
