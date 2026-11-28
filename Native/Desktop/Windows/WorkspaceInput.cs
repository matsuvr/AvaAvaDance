using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;

namespace AvaAvaDance.Desktop;

internal sealed partial class WorkspaceView
{
    private Item? capture;
    private Item? sliderGesture;
    private Point lastPoint, lastPressPoint;
    private int pressedButton, clickCount, lastPressId;
    private long lastPressTime;

    private void ListenForInput()
    {
        stage.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => Pointer("down", e)), true);
        stage.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => Pointer("move", e)), true);
        stage.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => Pointer("up", e)), true);
        stage.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => CancelPointer(e)), true);
        stage.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) => CancelPointer(e)), true);
        stage.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) => Pointer("wheel", e)), true);
        stage.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => Key("keyDown", e)), true);
        stage.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) => Key("keyUp", e)), true);
        stage.IsTabStop = true;
        stage.AllowDrop = true;
        stage.DragOver += (_, e) =>
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) &&
                (window == "main" || dropRevision.HasValue || DropTarget(e) != null) ? DataPackageOperation.Copy : DataPackageOperation.None;
            e.Handled = true;
        };
        stage.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var item = DropTarget(e);
            long? revision = dropRevision;
            long generation = document;
            if (item == null && window != "main" && !revision.HasValue) return;
            e.Handled = true;
            var deferral = e.GetDeferral();
            try
            {
                var files = await e.DataView.GetStorageItemsAsync();
                var paths = files.OfType<Windows.Storage.StorageFile>().Select(file => file.Path)
                    .Where(path => Path.IsPathFullyQualified(path) && (item != null || revision.HasValue ||
                        string.Equals(Path.GetExtension(path), ".unitypackage", StringComparison.OrdinalIgnoreCase))).ToArray();
                if (paths.Length != 0 && revision.HasValue && revision == dropRevision && generation == document)
                    bridge.Send(new { document = generation, window, windowId, id = windowId, action = "window.drop", revision, path = paths[0] });
                else if (paths.Length != 0 && !revision.HasValue && generation == document &&
                         (item != null ? item.Document == document : window == "main"))
                    bridge.Send(new { document = generation, window, windowId, id = item?.Id ?? 0, action = "drop", paths });
            }
            catch (COMException error) { System.Diagnostics.Debug.WriteLine(error); }
            finally { deferral.Complete(); }
        };
    }

    private Item? DropTarget(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return null;
        var p = e.GetPosition(stage);
        foreach (var item in items.Values)
        {
            var el = item.Element;
            if (item.Kind == "viewport" && el.IsHitTestVisible &&
                new Rect(Canvas.GetLeft(el), Canvas.GetTop(el), el.Width, el.Height).Contains(p)) return item;
        }
        return null;
    }

    private static Item? Target(object source)
    {
        for (var parent = source as DependencyObject; parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is FrameworkElement { Tag: Item item }) return item;
        return null;
    }

    private void CancelPointer(PointerRoutedEventArgs e)
    {
        EndSlider();
        if (capture != null) Pointer("up", e);
    }

    private void BeginSlider(Item item)
    {
        if (sliderGesture == item) return;
        EndSlider();
        sliderGesture = item;
        Action(item, "begin");
    }

    private void EndSlider()
    {
        if (sliderGesture == null) return;
        Action(sliderGesture, "end");
        sliderGesture = null;
    }

    private void Pointer(string kind, PointerRoutedEventArgs e)
    {
        if (applying) return;
        var item = capture ?? Target(e.OriginalSource);
        if (item == null || item.Document != document) return;
        var p = e.GetCurrentPoint(stage);
        var props = p.Properties;
        bool ctrl = Held(VirtualKey.Control), shift = Held(VirtualKey.Shift), alt = Held(VirtualKey.Menu);
        int button = props.IsRightButtonPressed ? 1 : props.IsMiddleButtonPressed ? 2 : 0;
        bool custom = item.Kind is "viewport" or "panel" or "canvas" or "label";
        if (kind == "up") EndSlider();
        if (kind == "down" && item.Kind == "slider" && button == 0 && !ctrl && !shift && !alt)
        {
            BeginSlider(item);
            return;
        }
        if (kind == "down")
        {
            if (!custom && button == 0 && !ctrl && !shift && !alt) return;
            if (capture != null) return;
            capture = item;
            pressedButton = button;
            long now = Environment.TickCount64;
            clickCount = lastPressId == item.Id && now - lastPressTime <= GetDoubleClickTime() &&
                Math.Abs(p.Position.X - lastPressPoint.X) < 4 && Math.Abs(p.Position.Y - lastPressPoint.Y) < 4 ? 2 : 1;
            lastPressId = item.Id; lastPressTime = now; lastPressPoint = p.Position;
            lastPoint = p.Position;
            stage.CapturePointer(e.Pointer);
            stage.Focus(FocusState.Pointer);
        }
        if (kind == "up" && capture == null) return;
        if (kind == "move" && capture != null) kind = "drag";
        if (capture != null) button = pressedButton;
        double dx = p.Position.X - lastPoint.X, dy = p.Position.Y - lastPoint.Y;
        lastPoint = p.Position;
        if (kind == "wheel")
        {
            dx = props.IsHorizontalMouseWheel ? props.MouseWheelDelta / 120d : 0;
            dy = props.IsHorizontalMouseWheel ? 0 : -props.MouseWheelDelta / 120d;
        }
        bridge.Send(new { document = item.Document, window, windowId, id = item.Id, action = "input", kind,
            x = p.Position.X - Canvas.GetLeft(item.Element), y = p.Position.Y - Canvas.GetTop(item.Element),
            dx, dy, button, clicks = Math.Max(1, clickCount), shift, ctrl, alt },
            kind == "move" ? $"hover-{item.Id}" : null);
        if (kind == "up")
        {
            capture = null;
            stage.ReleasePointerCapture(e.Pointer);
        }
        if (kind != "move") e.Handled = true;
    }

    private void Key(string kind, KeyRoutedEventArgs e)
    {
        var target = Target(e.OriginalSource);
        if (target?.Element is TextBox { IsReadOnly: false }) return;
        string? name = KeyName(e.Key);
        if (name == null) return;
        bridge.Send(new { document, window, windowId, id = target?.Id ?? 0, action = "input", kind, key = name,
            shift = Held(VirtualKey.Shift), ctrl = Held(VirtualKey.Control), alt = Held(VirtualKey.Menu) });
    }

    private static string? KeyName(VirtualKey key)
    {
        int k = (int)key;
        if (k >= 'A' && k <= 'Z' || k >= (int)VirtualKey.F1 && k <= (int)VirtualKey.F15) return key.ToString();
        if (k >= '0' && k <= '9') return $"Alpha{k - '0'}";
        if (k >= 0x60 && k <= 0x69) return $"Keypad{k - 0x60}";
        return k switch
        {
            8 => "Backspace", 9 => "Tab", 13 => "Return", 16 => "LeftShift", 17 => "LeftControl",
            18 => "LeftAlt", 19 => "Pause", 27 => "Escape", 32 => "Space", 33 => "PageUp", 34 => "PageDown",
            35 => "End", 36 => "Home", 37 => "LeftArrow", 38 => "UpArrow", 39 => "RightArrow", 40 => "DownArrow",
            45 => "Insert", 46 => "Delete", 106 => "KeypadMultiply", 107 => "KeypadPlus", 109 => "KeypadMinus",
            110 => "KeypadPeriod", 111 => "KeypadDivide", 0xba => "Semicolon", 0xbb => "Equals",
            0xbc => "Comma", 0xbd => "Minus", 0xbe => "Period", 0xbf => "Slash", 0xc0 => "BackQuote",
            0xdb => "LeftBracket", 0xdc => "Backslash", 0xdd => "RightBracket", 0xde => "Quote", 0xe2 => "Backslash",
            _ => null
        };
    }

    private static bool Held(VirtualKey key) => GetKeyState((int)key) < 0;
    [DllImport("user32")] private static extern short GetKeyState(int key);
    [DllImport("user32")] private static extern uint GetDoubleClickTime();
}
