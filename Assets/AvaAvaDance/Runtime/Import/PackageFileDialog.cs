using System;
using System.Runtime.InteropServices;
using UnityEngine;
namespace AvaAvaDance.Import
{
    public static class PackageFileDialog
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        private sealed class OpenFileName
        {
            public int structSize = Marshal.SizeOf(typeof(OpenFileName));
            public IntPtr owner, instance;
            public string filter = "Unity package\0*.unitypackage\0\0";
            public string customFilter;
            public int maxCustomFilter, filterIndex = 1;
            public IntPtr file;
            public int maxFile = 32768;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title = "アバターの unitypackage を選択";
            public int flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;
            public short fileOffset, fileExtension;
            public string defaultExtension = "unitypackage";
            public IntPtr customData, hook;
            public string templateName;
            public IntPtr reserved;
            public int reserved2, flagsEx;
        }
        [DllImport("comdlg32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileNameW([In, Out] OpenFileName data);
        [DllImport("comdlg32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSaveFileNameW([In, Out] OpenFileName data);
#endif
        internal static bool RememberFolder
        {
            get => WorkspacePreferences.GetInt("RememberFileFolder", 1) != 0;
            set => WorkspacePreferences.SetInt("RememberFileFolder", value ? 1 : 0);
        }
        public static string Open(string title = null, string extension = "unitypackage") => Choose(title, extension, false);
        static readonly System.Threading.AsyncLocal<Func<string,string,bool,string>> chooser = new();
        internal static Func<string,string,bool,string> AutomationChooser
        {
            get => chooser.Value;
            set => chooser.Value = value;
        }
        public static string Save(string title, string extension) => Choose(title, extension, true);
        private static string Choose(string title, string extension, bool saving)
        {
            if (AutomationChooser != null) return AutomationChooser(title, extension, saving);
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            IntPtr buffer = Marshal.AllocHGlobal(65536);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var extensions = extension.Split(';');
                string filter = saving && extensions.Length > 1 ? "" : extension + " file\0*." + string.Join(";*.", extensions) + "\0";
                if (extensions.Length > 1) foreach (string ext in extensions) filter += ext.ToUpperInvariant() + " (*." + ext + ")\0*." + ext + "\0";
                var request = new OpenFileName { file = buffer, filter = filter+"\0", defaultExtension = extensions[0],
                    owner = DesktopCallbacks.Address("AVA_DESKTOP_HWND") };
                string folderKey = "FileFolder." + extensions[0];
                string previous = WorkspacePreferences.GetString(folderKey, "");
                string folder = extensions[0] switch {
                    "x" => "Accessory", "vrma" => "Motion", "avamotion" => "Motion", "avapose" => "Pose", "vsq" => "Vsq", "wav" => "Wave",
                    "bmp" => "BackGround", "unitypackage" => "Model", _ => ""
                };
                string baseDir = System.IO.Path.GetDirectoryName(Application.dataPath);
                string standard = System.IO.Path.Combine(baseDir, "UserFile", folder);
                request.initialDir = RememberFolder && System.IO.Directory.Exists(previous) ? previous :
                    System.IO.Directory.Exists(standard) ? standard : baseDir;
                if (title != null) request.title = title;
                if (saving) request.flags = 0x00080000 | 0x00000800 | 0x00000008 | 0x00000002;
                bool accepted = saving ? GetSaveFileNameW(request) : GetOpenFileNameW(request);
                if (!accepted) return null;
                string path = Marshal.PtrToStringUni(buffer);
                if (RememberFolder) WorkspacePreferences.SetString(folderKey, System.IO.Path.GetDirectoryName(path));
                return path;
            }
            finally { Marshal.FreeHGlobal(buffer); }
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            return ChooseMac(title, extension, saving);
#else
            throw new PlatformNotSupportedException("この試作版のファイル選択は Windows 専用です。");
#endif
        }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int PickFile([In] byte[] request, [Out] byte[] result, int capacity);

        static string ChooseMac(string title, string extension, bool saving)
        {
            var extensions = extension.Split(';');
            string folderKey = "FileFolder." + extensions[0];
            string previous = RememberFolder ? WorkspacePreferences.GetString(folderKey) : "";
            if (DesktopCallbacks.Hosted)
            {
                string request = Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    title = title ?? "ファイルを選択", extensions, save = saving, directory = previous
                });
                var utf8 = System.Text.Encoding.UTF8;
                var result = new byte[32768];
                int count = DesktopCallbacks.Bind<PickFile>("AVA_DESKTOP_PICK")(utf8.GetBytes(request + "\0"), result, result.Length);
                if (count == 0) return null;
                if (count < 0 || count > result.Length) throw new System.IO.IOException("ファイルの選択に失敗しました。");
                string selected = utf8.GetString(result, 0, count);
                if (RememberFolder) WorkspacePreferences.SetString(folderKey, System.IO.Path.GetDirectoryName(selected));
                return selected;
            }
            string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
            string script = saving ? "choose file name default name " + Quote("Untitled." + extensions[0]) : "choose file";
            script += " with prompt " + Quote(title ?? "ファイルを選択");
            if (System.IO.Directory.Exists(previous)) script += " default location (POSIX file " + Quote(previous) + " as alias)";
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript", "-") {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.StandardInput.WriteLine("POSIX path of (" + script + ")");
            process.StandardInput.Close();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string message = errors.GetAwaiter().GetResult();
                if (message.Contains("(-128)")) return null;
                throw new System.IO.IOException(message.Trim());
            }
            string path = output.GetAwaiter().GetResult().TrimEnd('\r', '\n');
            string suffix = System.IO.Path.GetExtension(path).TrimStart('.');
            if (!Array.Exists(extensions, ext => ext.Equals(suffix, StringComparison.OrdinalIgnoreCase)))
                throw new System.IO.IOException("ファイルの拡張子を確認してください: " + extension);
            if (RememberFolder) WorkspacePreferences.SetString(folderKey, System.IO.Path.GetDirectoryName(path));
            return path;
        }
#endif
    }
}
