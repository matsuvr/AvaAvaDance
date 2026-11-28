using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace AvaAvaDance.Desktop;

internal sealed class PanelWindow : Window
{
    private long document;
    private bool closing;
    private readonly PanelPlacement placement;
    internal int Instance { get; }
    internal WorkspaceView View { get; }

    internal PanelWindow(EngineBridge bridge, string name, JsonElement model, long generation, nint main)
    {
        Instance = model.GetProperty("id").GetInt32();
        document = generation;
        Title = model.GetProperty("title").GetString() ?? name;
        var stage = new Canvas { Background = new SolidColorBrush(Color.FromArgb(255, 39, 42, 46)) };
        View = new WorkspaceView(stage, bridge, name, Instance);
        Content = stage;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        placement = new PanelPlacement(hwnd, model.TryGetProperty("placementDirectory", out var directory)
            ? directory.GetString() ?? WindowState.DirectoryPath() : WindowState.DirectoryPath(), Title);
        SetWindowLongPtrW(hwnd, -8, main); // owned tool window
        double scale = GetDpiForWindow(hwnd) / 96d;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(model.GetProperty("width").GetDouble() * scale),
            (int)Math.Ceiling(model.GetProperty("height").GetDouble() * scale)));
        stage.SizeChanged += (_, e) =>
        {
            if (!closing && e.NewSize.Width >= 1 && e.NewSize.Height >= 1)
                bridge.Send(new { document, window = name, windowId = Instance, id = Instance, action = "window.resize",
                    width = (int)Math.Round(e.NewSize.Width), height = (int)Math.Round(e.NewSize.Height) }, $"resize-{Instance}");
        };
        AppWindow.Closing += (_, args) =>
        {
            if (closing) return;
            args.Cancel = true;
            View.CommitPendingEdits();
            bridge.Send(new { document, window = name, windowId = Instance, id = Instance, action = "window.close" });
        };
        Activated += (_, e) =>
        {
            if (!closing && e.WindowActivationState == WindowActivationState.Deactivated) View.Blur();
        };
        placement.Restore();
        Activate();
    }

    internal void Apply(JsonElement model, long generation)
    {
        document = generation;
        if (AppWindow.Presenter is OverlappedPresenter presenter && model.TryGetProperty("top", out var top))
            presenter.IsAlwaysOnTop = top.ValueKind == JsonValueKind.True;
        View.Apply(model, generation);
    }

    internal void Dismiss() { closing = true; placement.Save(); Close(); }

    [DllImport("user32")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
