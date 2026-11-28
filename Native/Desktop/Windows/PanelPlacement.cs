using System.Globalization;
using System.Runtime.InteropServices;

namespace AvaAvaDance.Desktop;

// Persist screen coordinates and the outer window size in DIPs.
internal sealed class PanelPlacement(nint hwnd, string directory, string title)
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool WindowBounds(nint window, [In, Out] int[] bounds, bool restore);

    private static readonly WindowBounds Bounds = Marshal.GetDelegateForFunctionPointer<WindowBounds>(
        NativeLibrary.GetExport(NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "AvaAvaDance_Data",
            "Plugins", "x86_64", "AvaWindows.dll")), "ava_ui_bounds"));
    private readonly string path = Path.Combine(directory, "window-" +
        new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(100).ToArray()) + ".txt");

    internal void Restore()
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var box = new int[4];
            if (lines.Length == 4 && Enumerable.Range(0, 4).All(i => int.TryParse(lines[i],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out box[i])))
                Bounds(hwnd, box, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal void Save()
    {
        var box = new int[4];
        if (!Bounds(hwnd, box, false)) return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(temporary, box.Select(n => n.ToString(CultureInfo.InvariantCulture)));
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
