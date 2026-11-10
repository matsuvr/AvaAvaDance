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
            get => PlayerPrefs.GetInt("RememberFileFolder", 1) != 0;
            set => PlayerPrefs.SetInt("RememberFileFolder", value ? 1 : 0);
        }
        public static string Open(string title = null, string extension = "unitypackage") => Choose(title, extension, false);
        public static string Save(string title, string extension) => Choose(title, extension, true);
        private static string Choose(string title, string extension, bool saving)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            IntPtr buffer = Marshal.AllocHGlobal(65536);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var extensions = extension.Split(';');
                string filter = saving && extensions.Length > 1 ? "" : extension + " file\0*." + string.Join(";*.", extensions) + "\0";
                if (extensions.Length > 1) foreach (string ext in extensions) filter += ext.ToUpperInvariant() + " (*." + ext + ")\0*." + ext + "\0";
                var request = new OpenFileName { file = buffer, filter = filter+"\0", defaultExtension = extensions[0] };
                string folderKey = "FileFolder." + extensions[0];
                string previous = PlayerPrefs.GetString(folderKey, "");
                string folder = extensions[0] switch {
                    "x" => "Accessory", "vmd" => "Motion", "vpd" => "Pose", "vsq" => "Vsq", "wav" => "Wave",
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
                if (RememberFolder) PlayerPrefs.SetString(folderKey, System.IO.Path.GetDirectoryName(path));
                return path;
            }
            finally { Marshal.FreeHGlobal(buffer); }
#else
            throw new PlatformNotSupportedException("この試作版のファイル選択は Windows 専用です。");
#endif
        }
    }
}
