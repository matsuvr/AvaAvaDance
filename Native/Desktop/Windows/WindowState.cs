using System.Runtime.InteropServices;
using System.Text.Json;

namespace AvaAvaDance.Desktop;

// Shares the placement file and the DPI rounding rule with the original Windows player.
internal sealed class WindowState(nint hwnd)
{
    private sealed record Saved(string monitor, int x, int y, int width, int height, int dpi, bool maximized);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Display
    {
        public int size;
        public Rect screen, work;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public int length, flags, show, minX, minY, maxX, maxY;
        public Rect normal;
    }

    internal static string DirectoryPath()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-avaSmokeOutput");
        string root = index >= 0 && index + 1 < args.Length ? Path.Combine(args[index + 1], "settings") :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaAvaDance");
        return Path.Combine(root, "Windows");
    }

    private static string SettingsPath() => Path.Combine(DirectoryPath(), "MainWindow.json");

    private bool ready;
    private static bool Info(nint monitor, out Display info)
    {
        info = new Display { size = Marshal.SizeOf<Display>() };
        return GetMonitorInfoW(monitor, ref info) && info.work.right > info.work.left && info.work.bottom > info.work.top;
    }

    internal void Restore()
    {
        Saved? saved = null;
        try { saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SettingsPath())); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        if (saved is { } s && (string.IsNullOrEmpty(s.monitor) || s.width <= 0 || s.height <= 0 || s.dpi <= 0)) saved = null;
        nint target = MonitorFromWindow(hwnd, 2);
        if (saved != null)
            EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
            {
                if (!Info(monitor, out var d) || saved.monitor != d.device) return true;
                target = monitor;
                return false;
            }, 0);
        if (!Info(target, out var display)) return;
        int dpi = GetDpiForMonitor(target, 0, out uint xDpi, out _) == 0 ? (int)xDpi : (int)GetDpiForWindow(hwnd);
        if (dpi <= 0) return;
        GetWindowRect(hwnd, out var outer);
        GetClientRect(hwnd, out var client);
        int desiredWidth = saved?.width ?? (int)Math.Ceiling(1280 * dpi / 96d) + outer.right - outer.left - client.right;
        int desiredHeight = saved?.height ?? (int)Math.Ceiling(900 * dpi / 96d) + outer.bottom - outer.top - client.bottom;
        bool center = saved?.monitor != display.device;
        var x = WindowPlacement.Fit(display.work.right - display.work.left, saved?.x ?? 0, desiredWidth, saved?.dpi ?? dpi, dpi, center);
        var y = WindowPlacement.Fit(display.work.bottom - display.work.top, saved?.y ?? 0, desiredHeight, saved?.dpi ?? dpi, dpi, center);
        if (MonitorFromWindow(hwnd, 2) != target)
            SetWindowPos(hwnd, 0, display.work.left + x.position, display.work.top + y.position, x.size, y.size, 0x14);
        int left = display.screen.left + x.position, top = display.screen.top + y.position;
        var p = new Placement { length = Marshal.SizeOf<Placement>(), show = saved?.maximized == true ? 3 : 1,
            minX = -1, minY = -1, maxX = -1, maxY = -1,
            normal = new Rect { left = left, top = top, right = left + x.size, bottom = top + y.size } };
        ready = SetWindowPlacement(hwnd, ref p);
    }

    internal void Save()
    {
        if (!ready || !Info(MonitorFromWindow(hwnd, 2), out var display)) return;
        var p = new Placement { length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(hwnd, ref p)) return;
        var rect = p.normal;
        int x = rect.left - display.screen.left, y = rect.top - display.screen.top;
        if (p.show == 1 && GetWindowRect(hwnd, out var current))
        {
            rect = current;
            x = rect.left - display.work.left;
            y = rect.top - display.work.top;
        }
        var saved = new Saved(display.device, x, y, rect.right - rect.left, rect.bottom - rect.top,
            (int)GetDpiForWindow(hwnd), p.show == 3 || (p.show == 2 && (p.flags & 2) != 0));
        if (saved.width <= 0 || saved.height <= 0 || saved.dpi <= 0) return;
        string path = SettingsPath(), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(error); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private delegate bool MonitorVisitor(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorVisitor visitor, nint data);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref Display info);
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32")] private static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32")] private static extern bool SetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
}
