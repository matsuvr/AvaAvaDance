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
#endif
        public static string Open(string title = null, string extension = "unitypackage")
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            IntPtr buffer = Marshal.AllocHGlobal(65536);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var request = new OpenFileName { file = buffer, filter = extension + " file\0*." + extension + "\0\0", defaultExtension = extension };
                if (title != null) request.title = title;
                return GetOpenFileNameW(request) ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
#else
            throw new PlatformNotSupportedException("この試作版のファイル選択は Windows 専用です。");
#endif
        }
    }
}
