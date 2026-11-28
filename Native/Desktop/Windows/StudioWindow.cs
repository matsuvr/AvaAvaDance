using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.UI;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace AvaAvaDance.Desktop;

internal sealed class StudioWindow : Window
{
    private readonly Canvas stage = new() { Background = new SolidColorBrush(Color.FromArgb(255, 39, 42, 46)) };
    private readonly TextBlock status = new() { Text = "AvaAvaDance を起動しています…", Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap };
    private readonly EngineBridge bridge = new();
    private readonly WorkspaceView view;
    private readonly Dictionary<string, PanelWindow> panels = new();
    private readonly WindowState placement;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly nint hwnd;
    private nint unityParent;
    private bool finished, receivedWorkspace;
    private long shownDocument = -1, shownLayout = -1;
    private (double width, double height, double scale) lastSize;

    public StudioWindow()
    {
        Title = "AvaAvaDance";
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var root = new Grid();
        root.Children.Add(stage);
        root.Children.Add(status);
        Content = root;
        view = new WorkspaceView(stage, bridge);
        placement = new WindowState(hwnd);
        placement.Restore();
        Activated += (_, args) =>
        {
            if (receivedWorkspace && args.WindowActivationState == WindowActivationState.Deactivated) view.Blur();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen) placement.Save();
            if (finished) return;
            args.Cancel = true;
            view.CommitPendingEdits();
            foreach (var panel in panels.Values) panel.View.CommitPendingEdits();
            bridge.Send(new { type = "close" });
            if (!receivedWorkspace)
            {
                // Also allows dismissal of a version-mismatched or failed engine startup.
                nint child = FindWindowEx(unityParent, 0, "UnityWndClass", null);
                if (child != 0) PostMessage(child, 0x10, 0, 0);
            }
        };
        stage.Loaded += (_, _) =>
        {
            stage.XamlRoot.Changed += (_, _) => Resize();
            StartEngine();
        };
        stage.SizeChanged += (_, _) => Resize();
        timer.Tick += (_, _) => Present();
        Closed += (_, _) =>
        {
            timer.Stop();
            foreach (var panel in panels.Values) panel.Dismiss();
            if (unityParent != 0) DestroyWindow(unityParent);
        };
    }

    private void StartEngine()
    {
        if (unityParent != 0 || finished) return;
        try
        {
            // Keep the Unity swapchain off the visible desktop. The native viewport displays
            // camera frames and the native overlay handles all hit testing and focus.
            const int WS_CHILD = 0x40000000;
            unityParent = CreateWindowEx(0, "STATIC", "", WS_CHILD,
                0, 0, 640, 480, hwnd, 0, 0, 0);
            if (unityParent == 0) throw new System.ComponentModel.Win32Exception();
            Environment.SetEnvironmentVariable("AVA_DESKTOP_HWND", hwnd.ToString("X"));
            Resize();
            bridge.Start(unityParent, RequestCapture, (exit, error) => DispatcherQueue.TryEnqueue(() =>
            {
                finished = true;
                if (exit == 0 && error == null) Close();
                else ShowError(error ?? $"Unity が終了しました（{exit}）。");
            }));
            timer.Start();
        }
        catch (Exception error) { finished = true; ShowError(error.Message); }
    }

    private Task<byte[]> RequestCapture(int maxDimension)
    {
        var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (finished || stage.ActualWidth <= 0 || stage.ActualHeight <= 0)
                    throw new InvalidOperationException("Workspace is unavailable.");
                var image = new RenderTargetBitmap();
                double scale = Math.Min(stage.XamlRoot.RasterizationScale, maxDimension / Math.Max(stage.ActualWidth, stage.ActualHeight));
                uint w = (uint)Math.Max(1, Math.Round(stage.ActualWidth * scale));
                uint h = (uint)Math.Max(1, Math.Round(stage.ActualHeight * scale));
                // RenderAsync takes effective pixels (and applies the monitor scale).
                // The encoded result must obey the MCP caller's physical pixel cap.
                await image.RenderAsync(stage, Math.Max(1, (int)Math.Ceiling(w / stage.XamlRoot.RasterizationScale)),
                    Math.Max(1, (int)Math.Ceiling(h / stage.XamlRoot.RasterizationScale)));
                var pixels = await image.GetPixelsAsync();
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.BitmapTransform.ScaledWidth = w;
                encoder.BitmapTransform.ScaledHeight = h;
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels.ToArray());
                await encoder.FlushAsync();
                stream.Seek(0);
                using var reader = new DataReader(stream);
                byte[] png = new byte[checked((int)stream.Size)];
                await reader.LoadAsync((uint)png.Length);
                reader.ReadBytes(png);
                result.TrySetResult(png);
            }
            catch (Exception error) { result.TrySetException(error); }
        })) result.TrySetCanceled();
        return result.Task;
    }

    private void Resize()
    {
        var size = (width: stage.ActualWidth, height: stage.ActualHeight, scale: stage.XamlRoot?.RasterizationScale ?? 1);
        if (size.width <= 0 || size.height <= 0 || size == lastSize) return;
        lastSize = size;
        bridge.Send(new { type = "resize", size.width, size.height, size.scale }, "resize");
    }

    private void Present()
    {
        var (bytes, frame) = bridge.Take();
        try
        {
            if (bytes != null)
            {
                using var message = JsonDocument.Parse(bytes);
                var snapshot = message.RootElement;
                if (snapshot.TryGetProperty("error", out var error)) ShowError(error.ToString());
                else
                {
                    long layout = snapshot.GetProperty("layout").GetInt64(), doc = snapshot.GetProperty("document").GetInt64();
                    if (shownDocument != doc || shownLayout != layout)
                    {
                        view.SetBitmap(null);
                        foreach (var panel in panels.Values) panel.View.SetBitmap(null);
                    }
                    view.Apply(snapshot);
                    UpdateWindows(snapshot);
                    shownDocument = doc;
                    shownLayout = layout;
                    receivedWorkspace = true;
                    status.Visibility = Visibility.Collapsed;
                }
            }
            if (frame != null && frame.Document == shownDocument && frame.Layout == shownLayout)
            {
                var bitmap = view.Present(frame);
                foreach (var panel in panels.Values) panel.View.SetBitmap(bitmap);
            }
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { frame?.Release(); }
    }

    private void UpdateWindows(JsonElement snapshot)
    {
        bool full = snapshot.TryGetProperty("fullscreen", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (full != (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen))
        {
            if (full) placement.Save();
            AppWindow.SetPresenter(full ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        }
        var present = new HashSet<string>();
        if (snapshot.TryGetProperty("windows", out var windows))
        {
            long generation = snapshot.GetProperty("document").GetInt64();
            foreach (var model in windows.EnumerateArray())
            {
                string name = model.GetProperty("name").GetString()!;
                present.Add(name);
                if (!panels.TryGetValue(name, out var panel) || panel.Instance != model.GetProperty("id").GetInt32())
                {
                    panel?.Dismiss();
                    panel = new PanelWindow(bridge, name, model, generation, hwnd);
                    panels[name] = panel;
                }
                panel.Apply(model, generation);
            }
        }
        foreach (string name in panels.Keys.Where(name => !present.Contains(name)).ToArray())
        {
            panels[name].Dismiss();
            panels.Remove(name);
        }
    }

    private void ShowError(string message)
    {
        status.Text = message;
        status.Visibility = Visibility.Visible;
        status.VerticalAlignment = VerticalAlignment.Top;
    }

    [DllImport("user32", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string cls, string title, int style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? title);
    [DllImport("user32")] private static extern bool PostMessage(nint window, uint message, nuint w, nint l);
}
