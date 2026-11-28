using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AvaAvaDance.Desktop;

// Unity runs Mono on its own thread. Only owned copies cross into the WinUI dispatcher.
internal sealed unsafe class EngineBridge
{
    internal sealed record Frame(byte[] Pixels, int Width, int Height, long Document, long Layout)
    {
        public void Release() => ArrayPool<byte>.Shared.Return(Pixels);
    }

    private static EngineBridge? active;
    private readonly object gate = new();
    private readonly LinkedList<(byte[] bytes, string? merge)> commands = new();
    private byte[]? snapshot;
    private Frame? frame;
    private Thread? engine;
    private Func<int, Task<byte[]>>? capture;

    internal void Send(object message, string? merge = null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        lock (gate)
        {
            // Only adjacent motion/resize updates can supersede each other. Presses, commits and
            // releases remain ordered even if Unity is busy importing a model.
            if (merge != null && commands.Last?.Value.merge == merge) commands.RemoveLast();
            commands.AddLast((bytes, merge));
        }
    }

    internal (byte[]?, Frame?) Take()
    {
        lock (gate)
        {
            var result = (snapshot, frame);
            snapshot = null;
            frame = null;
            return result;
        }
    }

    internal void Start(nint owner, Func<int, Task<byte[]>> capture, Action<int, string?> exited)
    {
        if (active != null) throw new InvalidOperationException("Unity is already running.");
        active = this;
        this.capture = capture;
        Environment.SetEnvironmentVariable("AVA_DESKTOP_PID", Environment.ProcessId.ToString());
        Export("AVA_DESKTOP_POLL", (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&Poll);
        Export("AVA_DESKTOP_PUBLISH", (nint)(delegate* unmanaged[Cdecl]<nint, int, void>)&Publish);
        Export("AVA_DESKTOP_FRAME", (nint)(delegate* unmanaged[Cdecl]<nint, int, int, long, long, void>)&PublishFrame);
        Export("AVA_DESKTOP_CAPTURE", (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int>)&Capture);
        // UnityMain owns a Win32 message loop for the lifetime of the engine.
        engine = new Thread(() =>
        {
            int result = -1;
            string? error = null;
            try
            {
                result = RunUnity($"-parentHWND {owner} -screen-fullscreen 0 -screen-width 640 -screen-height 480 ");
            }
            catch (Exception e) { error = e.Message; }
            finally { exited(result, error); }
        }) { Name = "Unity Player" };
        engine.SetApartmentState(ApartmentState.STA);
        engine.Start();
    }

    internal static int RunUnity(string prefix = "")
    {
        string folder = Path.GetDirectoryName(Environment.ProcessPath!)!;
        string dll = Path.Combine(folder, "UnityPlayer.dll");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(folder, "AvaAvaDance_Data", "globalgamemanagers")))
            throw new FileNotFoundException("Unity Player のデータがありません。Windows ホストを build.ps1 でビルドしてください。");
        nint module = NativeLibrary.Load(dll);
        var entry = Marshal.GetDelegateForFunctionPointer<UnityEntry>(NativeLibrary.GetExport(module, "UnityMain"));
        string forwarded = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(Quote));
        return entry(GetModuleHandle(null), 0, prefix + forwarded, 0);
    }

    // Windows command-line quoting, including quoted paths ending in backslashes.
    private static string Quote(string value) => "\"" +
        System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)(\"|$)", m =>
            new string('\\', m.Groups[1].Length * 2) + (m.Groups[2].Length == 0 ? "" : "\\\"")) + "\"";

    private static void Export(string name, nint value) =>
        Environment.SetEnvironmentVariable(name, value.ToString("X"));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int UnityEntry(nint instance, nint previous, string arguments, int show);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Capture(nint dest, int capacity, int maxDimension)
    {
        if (dest == 0 || capacity <= 0 || maxDimension <= 0) return -1;
        try
        {
            // Only this calling thread sees dest. If the dispatcher is shutting down
            // or busy, the pending render owns its data and can finish independently.
            byte[] png = active!.capture!(maxDimension).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (png.Length == 0 || png.Length > capacity) return -1;
            Marshal.Copy(png, 0, dest, png.Length);
            return png.Length;
        }
        catch (Exception) { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Poll(nint destination, int capacity)
    {
        var bridge = active!;
        lock (bridge.gate)
        {
            if (bridge.commands.First is not { } head) return 0;
            byte[] bytes = head.Value.bytes;
            if (bytes.Length > capacity || destination == 0) return -bytes.Length;
            Marshal.Copy(bytes, 0, destination, bytes.Length);
            bridge.commands.RemoveFirst();
            return bytes.Length;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Publish(nint json, int count)
    {
        if (json == 0 || count <= 0) return;
        byte[] copy = new byte[count];
        Marshal.Copy(json, copy, 0, count);
        var bridge = active!;
        lock (bridge.gate)
        {
            bridge.snapshot = copy;
            // A snapshot can change the document or layout before the next UI tick.
            // Deliver only frames received after it alongside that snapshot.
            bridge.frame?.Release();
            bridge.frame = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PublishFrame(nint rgba, int width, int height, long document, long layout)
    {
        if (rgba == 0 || width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4) return;
        int count = width * height * 4;
        byte[] copy = ArrayPool<byte>.Shared.Rent(count);
        Marshal.Copy(rgba, copy, 0, count);
        var bridge = active!;
        lock (bridge.gate)
        {
            bridge.frame?.Release();
            bridge.frame = new Frame(copy, width, height, document, layout);
        }
    }
}
