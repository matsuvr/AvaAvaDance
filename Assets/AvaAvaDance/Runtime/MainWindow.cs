using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [StructLayout(LayoutKind.Sequential)] struct ClientBounds { public int left, top, right, bottom; }
        [DllImport("user32")] static extern bool GetClientRect(IntPtr window, out ClientBounds bounds);
        [DllImport("user32")] static extern bool GetWindowRect(IntPtr window, out ClientBounds bounds);
        [DllImport("user32")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool MonitorVisitor(IntPtr monitor, IntPtr dc, ref ClientBounds bounds, IntPtr data);
        [DllImport("user32")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorVisitor visitor, IntPtr data);
        [DllImport("shcore")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [Serializable] sealed class SavedPlacement
        {
            public string monitor;
            public int x, y, width, height, dpi;
            public bool maximized;
            public bool Valid => !string.IsNullOrWhiteSpace(monitor) && width > 0 && height > 0 && dpi > 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        struct DisplayInfo
        {
            public int length;
            public ClientBounds screen, work;
            public uint flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string device;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Placement
        {
            public int length, flags, show;
            public int minX, minY, maxX, maxY;
            public ClientBounds normal;
        }

        [DllImport("user32", CharSet=CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr monitor, ref DisplayInfo info);
        [DllImport("user32")] static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32")] static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
        const int NormalWindow = 1, MinimizedWindow = 2, MaximizedWindow = 3, RestoreToMaximized = 2;
        bool mainPlacementReady;
        static string MainPlacementPath => Path.Combine(WorkspacePreferences.WindowDirectory, "MainWindow.json");

        static bool UseMainWindowPlacement()
        {
            if (Application.isBatchMode) return false;
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; ++i)
                switch (args[i].ToLowerInvariant()) {
                    case "-screen-fullscreen": if (i + 1 < args.Length && args[i+1] == "1") return false; break;
                    case "-screen-width": case "-screen-height": case "-screen-left": case "-screen-top":
                    case "-monitor": case "-popupwindow": case "-parenthwnd": return false;
                }
            return true;
        }

        static SavedPlacement ReadMainPlacement()
        {
            try {
                if (!File.Exists(MainPlacementPath)) return null;
                var saved = JsonUtility.FromJson<SavedPlacement>(File.ReadAllText(MainPlacementPath));
                return saved?.Valid == true ? saved : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) {
                Debug.LogWarning("ウィンドウ配置を読み込めません: " + e.Message);
                return null;
            }
        }

        static bool ReadDisplay(IntPtr monitor, out DisplayInfo info)
        {
            info = new DisplayInfo { length = Marshal.SizeOf<DisplayInfo>() };
            return GetMonitorInfoW(monitor, ref info) && info.work.right > info.work.left && info.work.bottom > info.work.top;
        }

        IEnumerator OpenMainWindow(VisualElement workspace, Action<bool> measure)
        {
            if (Screen.fullScreenMode != FullScreenMode.Windowed) Screen.fullScreenMode = FullScreenMode.Windowed;
            while (Screen.fullScreenMode != FullScreenMode.Windowed) yield return null;
            while (playerWindow == IntPtr.Zero || !(workspace.layout.width > 0)) yield return null;
            var saved = ReadMainPlacement();
            var target = MonitorFromWindow(playerWindow, 2);
            if (saved != null) {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref ClientBounds rect, IntPtr data) => {
                    if (!ReadDisplay(m, out var display) || display.device != saved.monitor) return true;
                    target = m;
                    return false;
                }, IntPtr.Zero);
            }
            if (!ReadDisplay(target, out var info)) yield break;
            int dpi = GetDpiForMonitor(target, 0, out uint effective, out _) == 0 ? (int)effective : (int)GetDpiForWindow(playerWindow);
            if (dpi <= 0) yield break;
            int workWidth = info.work.right - info.work.left, workHeight = info.work.bottom - info.work.top;
            bool center = saved == null || saved.monitor != info.device;
            bool initial = saved == null;
            var scroll = workspace.GetFirstAncestorOfType<ScrollView>();
            if (initial) {
                if (!GetWindowRect(playerWindow, out var outer) || !GetClientRect(playerWindow, out var client)) yield break;
                int borderWidth = outer.right - outer.left - client.right;
                int borderHeight = outer.bottom - outer.top - client.bottom;
                float scale = dpi / 96f;
                float idealWidth = 1280;
                int width = Mathf.Min(workWidth, Mathf.CeilToInt(idealWidth * scale) + borderWidth);
                measure(true);
                // Hold the workspace at the requested client size until Windows applies placement.
                scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                float panelWidth = Mathf.Max(1, width - borderWidth) / scale;
                workspace.EnableInClassList("compact-workspace", panelWidth < CompactWorkspaceWidth);
                workspace.style.width = panelWidth;
                workspace.style.minHeight = 900;
                yield return null;
                yield return new WaitForEndOfFrame();
                saved = new SavedPlacement { width = width, height = Mathf.Min(workHeight, Mathf.CeilToInt(900 * scale) + borderHeight), dpi = dpi };
            }
            var horizontal = WindowPlacement.Fit(workWidth, saved.x, saved.width, saved.dpi, dpi, center);
            var vertical = WindowPlacement.Fit(workHeight, saved.y, saved.height, saved.dpi, dpi, center);
            // WINDOWPLACEMENT uses workspace coordinates: the monitor origin already represents
            // the start of its work area, even when a taskbar occupies the top or left edge.
            int x = info.screen.left + horizontal.position, y = info.screen.top + vertical.position;
            var placement = new Placement { length = Marshal.SizeOf<Placement>(), show = saved.maximized ? MaximizedWindow : NormalWindow,
                minX = -1, minY = -1, maxX = -1, maxY = -1,
                normal = new ClientBounds { left=x, top=y, right=x+horizontal.size, bottom=y+vertical.size } };
            if (MonitorFromWindow(playerWindow, 2) != target) {
                // Let Windows and Unity process WM_DPICHANGED before restoring the final bounds.
                SetWindowPos(playerWindow, IntPtr.Zero, info.work.left+horizontal.position, info.work.top+vertical.position,
                    horizontal.size, vertical.size, 0x14); // SWP_NOZORDER | SWP_NOACTIVATE
                yield return null;
                yield return new WaitForEndOfFrame();
            }
            mainPlacementReady = SetWindowPlacement(playerWindow, ref placement);
            if (initial) {
                yield return null;
                yield return new WaitForEndOfFrame();
                measure(false);
                scroll.horizontalScrollerVisibility = ScrollerVisibility.Auto;
                scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            }
            if (mainPlacementReady) Debug.Log($"AVA_MAIN_WINDOW {info.device} dpi={dpi} size={horizontal.size}x{vertical.size} maximized={saved.maximized}");
        }

        void SaveMainPlacement()
        {
            if (!mainPlacementReady || fullPreview || Screen.fullScreenMode != FullScreenMode.Windowed) return;
            var placement = new Placement { length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(playerWindow, ref placement) || !ReadDisplay(MonitorFromWindow(playerWindow, 2), out var info)) return;
            var rect = placement.normal;
            int x = rect.left - info.screen.left, y = rect.top - info.screen.top;
            if (placement.show == NormalWindow && GetWindowRect(playerWindow, out var actual)) {
                rect = actual; // Includes a normal window snapped to a screen edge.
                x = rect.left - info.work.left;
                y = rect.top - info.work.top;
            }
            var saved = new SavedPlacement { monitor=info.device, dpi=(int)GetDpiForWindow(playerWindow), x=x, y=y,
                width=rect.right-rect.left, height=rect.bottom-rect.top,
                maximized=placement.show==MaximizedWindow || (placement.show==MinimizedWindow && (placement.flags & RestoreToMaximized)!=0) };
            if (!saved.Valid) return;
            try {
                Directory.CreateDirectory(WorkspacePreferences.WindowDirectory);
                WriteFile(MainPlacementPath, System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(saved)));
            } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
                Debug.LogWarning("ウィンドウ配置を保存できません: " + e.Message);
            }
        }

        void OnApplicationQuit() => SaveMainPlacement();
#endif
    }
}
