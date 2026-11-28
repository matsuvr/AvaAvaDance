using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace AvaAvaDance.Desktop;

internal sealed partial class WorkspaceView
{
    private sealed class Item(int id, string kind, FrameworkElement element)
    {
        internal readonly int Id = id;
        internal readonly string Kind = kind;
        internal readonly FrameworkElement Element = element;
        internal long Document;
        internal string Previous = "";
        internal bool Delayed, Integer, Dirty, Composing;
        internal string ModelValue = "";
        internal string LastText = "";
        internal string[] Choices = [];
    }

    private readonly Canvas stage;
    private readonly EngineBridge bridge;
    private readonly string window;
    private readonly int windowId;
    private readonly Dictionary<int, Item> items = new();
    private readonly Dictionary<string, SolidColorBrush> brushes = new();
    private long document;
    private bool applying;
    private WriteableBitmap? bitmap;

    internal WorkspaceView(Canvas stage, EngineBridge bridge, string window = "main", int windowId = 0)
    {
        this.stage = stage;
        this.bridge = bridge;
        this.window = window;
        this.windowId = windowId;
        ListenForInput();
    }

    private long? dropRevision;

    internal void Apply(JsonElement snapshot, long? ownerDocument = null)
    {
        if (!snapshot.TryGetProperty("nodes", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unity から受信した画面データの形式が異なります。");
        dropRevision = snapshot.TryGetProperty("acceptsFiles", out var accepts) && accepts.GetBoolean()
            ? snapshot.GetProperty("revision").GetInt64() : null;
        long nextDocument = ownerDocument ?? snapshot.GetProperty("document").GetInt64();
        applying = true;
        try
        {
            if (document != nextDocument)
            {
                capture = null;
                sliderGesture = null;
                stage.ReleasePointerCaptures();
                // Retire the active TextBox and its native editor before removing
                // the visual tree. LostFocus is suppressed for the old document.
                stage.Focus(FocusState.Programmatic);
                stage.Children.Clear();
                items.Clear();
                document = nextDocument;
                bitmap = null;
            }
            var visible = new HashSet<int>();
            int index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                int id = row.GetProperty("id").GetInt32();
                string kind = String(row, "kind");
                visible.Add(id);
                if (!items.TryGetValue(id, out var item) || item.Kind != kind)
                {
                    if (item != null) stage.Children.Remove(item.Element);
                    item = Create(id, kind);
                    item.Document = nextDocument;
                    items[id] = item;
                    stage.Children.Add(item.Element);
                }
                Canvas.SetZIndex(item.Element, index++);
                string raw = row.GetRawText();
                if (raw == item.Previous) continue;
                Update(item, row);
                item.Previous = raw;
            }
            foreach (int id in items.Keys.Where(id => !visible.Contains(id)).ToArray())
            {
                stage.Children.Remove(items[id].Element);
                items.Remove(id);
            }
        }
        finally { applying = false; }
    }

    private Item Create(int id, string kind)
    {
        FrameworkElement element = kind switch
        {
            "button" => new Button(),
            "toggle" => new CheckBox(),
            "choice" => new ComboBox(),
            "text" or "integer" or "number" or "multiline" => new TextBox
                { IsSpellCheckEnabled = false, AcceptsReturn = kind == "multiline",
                  TextWrapping = kind == "multiline" ? TextWrapping.Wrap : TextWrapping.NoWrap },
            "slider" => new Slider(),
            "viewport" => new Image { Stretch = Stretch.Fill, Source = bitmap },
            "canvas" => new Canvas(),
            "label" => new Border { Child = new TextBlock { VerticalAlignment = VerticalAlignment.Center } },
            _ => new Border()
        };
        var item = new Item(id, kind, element);
        element.Tag = item;
        if (element is Control control)
        {
            control.MinHeight = control.MinWidth = 0;
            control.Padding = new Thickness(2, 0, 2, 0);
            control.CornerRadius = new CornerRadius(2);
            control.VerticalContentAlignment = VerticalAlignment.Center;
            if (control is TextBox) control.GotFocus += (_, _) => Action(item, "focus");
        }
        switch (element)
        {
            case Button button:
                button.Click += (_, _) => Action(item, "activate");
                break;
            case CheckBox toggle:
                toggle.Click += (_, _) => Action(item, "set", toggle.IsChecked == true);
                break;
            case ComboBox choice:
                choice.SelectionChanged += (_, _) =>
                {
                    int index = choice.SelectedIndex;
                    if (index >= 0 && index < item.Choices.Length) Action(item, "set", item.Choices[index]);
                };
                break;
            case Slider slider:
                slider.ValueChanged += (_, e) =>
                {
                    if (!applying && GetKeyState(1) < 0) BeginSlider(item);
                    if (item.Integer) Action(item, "set", (int)Math.Round(e.NewValue));
                    else Action(item, "set", e.NewValue);
                };
                break;
            case TextBox field:
                field.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) =>
                {
                    if (field.IsReadOnly || field.Text == item.LastText) return;
                    item.Dirty = true;
                    if (!item.Delayed && !item.Composing) Commit(item, field);
                });
                field.TextCompositionStarted += (_, _) => item.Composing = true;
                field.TextCompositionEnded += (_, _) =>
                {
                    item.Composing = false;
                    if (!item.Delayed) Commit(item, field);
                };
                if (kind == "multiline")
                    field.PreviewKeyDown += (_, e) =>
                    {
                        if (e.Key != Windows.System.VirtualKey.Enter || item.Composing || Held(Windows.System.VirtualKey.Shift)) return;
                        Action(item, "submit", field.Text);
                        e.Handled = true;
                    };
                field.LostFocus += (_, _) =>
                {
                    if (applying || item.Composing || item.Document != document) return;
                    if (field.Text != item.LastText) item.Dirty = true;
                    if (!item.Dirty || !Commit(item, field))
                    {
                        // An external seek or Undo may have updated the model while this
                        // editor had focus. Refresh its display without replaying old text.
                        RestoreText(item, field);
                    }
                    Action(item, "unfocus");
                };
                field.KeyDown += (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter && !field.AcceptsReturn) Commit(item, field);
                };
                break;
        }
        return item;
    }

    internal void Blur()
    {
        CommitPendingEdits();
        if (sliderGesture != null) EndSlider();
        capture = null;
        stage.ReleasePointerCaptures();
        bridge.Send(new { document, id = 0, window, windowId, action = "blur" });
    }

    internal void CommitPendingEdits()
    {
        if (applying) return;
        foreach (var item in items.Values)
        {
            if (item.Element is not TextBox { IsReadOnly: false } field || item.Composing) continue;
            if (field.Text != item.LastText) item.Dirty = true;
            if (item.Dirty && !Commit(item, field)) RestoreText(item, field);
        }
    }

    private static void RestoreText(Item item, TextBox field)
    {
        item.LastText = item.ModelValue;
        item.Dirty = false;
        if (field.Text != item.ModelValue) field.Text = item.ModelValue;
    }

    private void Action(Item item, string action, object? value = null)
    {
        if (!applying && item.Document == document)
        {
            if (action == "activate") CommitPendingEdits();
            bridge.Send(new { document = item.Document, window, windowId, id = item.Id, action, value });
        }
    }

    private bool Commit(Item item, TextBox field)
    {
        if (applying || !item.Dirty) return true;
        object value = field.Text;
        if (item.Kind == "integer")
        {
            if (!int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int number)) return false;
            value = number;
        }
        else if (item.Kind == "number")
        {
            if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) &&
                !double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
            if (!double.IsFinite(number)) return false;
            value = number;
        }
        Action(item, "set", value);
        item.LastText = field.Text;
        item.Dirty = false;
        return true;
    }

    private void Update(Item item, JsonElement row)
    {
        var e = item.Element;
        var bounds = RectOf(row.GetProperty("bounds"));
        e.Width = bounds.Width;
        e.Height = bounds.Height;
        Canvas.SetLeft(e, bounds.X);
        Canvas.SetTop(e, bounds.Y);
        var clip = row.TryGetProperty("clip", out var c) ? RectOf(c) : bounds;
        e.Clip = clip == bounds ? null : new RectangleGeometry
        {
            Rect = new Rect(clip.X - bounds.X, clip.Y - bounds.Y, clip.Width, clip.Height)
        };
        e.IsHitTestVisible = Flag(row, "hit", true) && Flag(row, "enabled", true);
        string label = String(row, "text"), tip = String(row, "tip"), name = String(row, "name");
        ToolTipService.SetToolTip(e, tip.Length == 0 ? null : tip);
        AutomationProperties.SetAutomationId(e, name.Length == 0 ? item.Id.ToString() : name);
        AutomationProperties.SetName(e, label.Length > 0 ? label : tip.Length > 0 ? tip : name);
        var background = Brush(String(row, "background"), "#00000000");
        var foreground = Brush(String(row, "color"), "#FFFFFFFF");
        double font = Math.Max(1, Number(row, "fontSize", 12));
        string align = String(row, "align");
        row.TryGetProperty("value", out var value);
        if (e is Control control)
        {
            control.IsEnabled = Flag(row, "enabled", true);
            control.Background = background;
            control.Foreground = foreground;
            control.FontSize = font;
        }
        switch (e)
        {
            case Button button:
                button.Content = label;
                break;
            case CheckBox toggle:
                toggle.Content = label;
                toggle.IsChecked = value.ValueKind == JsonValueKind.True;
                break;
            case ComboBox choice:
                string[] labels = row.TryGetProperty("choiceLabels", out var captions) && captions.ValueKind == JsonValueKind.Array
                    ? captions.EnumerateArray().Select(j => j.ToString()).ToArray() : [];
                item.Choices = row.GetProperty("choices").EnumerateArray().Select(j => j.ToString()).ToArray();
                if (labels.Length != item.Choices.Length) labels = item.Choices;
                if (choice.ItemsSource is not string[] old || !old.SequenceEqual(labels)) choice.ItemsSource = labels;
                choice.SelectedIndex = Array.IndexOf(item.Choices, value.ToString());
                choice.PlaceholderText = String(row, "selectedLabel");
                break;
            case Slider slider:
                item.Integer = Flag(row, "integer");
                slider.Orientation = Flag(row, "vertical") ? Orientation.Vertical : Orientation.Horizontal;
                double min = Number(row, "min", 0), max = Number(row, "max", 1);
                slider.Minimum = Math.Min(min, max); slider.Maximum = Math.Max(min, max);
                slider.StepFrequency = item.Integer ? 1 : Math.Max(0.000001, (max - min) / 1000);
                slider.Value = Number(row, "value", min);
                break;
            case TextBox field:
                item.Delayed = Flag(row, "delayed");
                field.IsReadOnly = Flag(row, "readOnly");
                field.MaxLength = (int)Number(row, "maxLength", 0);
                item.ModelValue = value.ValueKind == JsonValueKind.Null ? "" : value.ToString();
                if (field.FocusState == FocusState.Unfocused && field.Text != item.ModelValue)
                {
                    RestoreText(item, field);
                }
                break;
            case Canvas canvas:
                canvas.Background = background;
                UpdatePaths(canvas, row);
                break;
            case Border border:
                border.Background = background;
                if (border.Child is TextBlock text)
                {
                    text.Text = label; text.FontSize = font; text.Foreground = foreground;
                    text.TextWrapping = Flag(row, "wrap") ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    text.TextAlignment = align == "center" ? TextAlignment.Center : align == "right" ? TextAlignment.Right : TextAlignment.Left;
                }
                break;
        }
    }

    private static Rect RectOf(JsonElement a) => new(a[0].GetDouble(), a[1].GetDouble(),
        Math.Max(0, a[2].GetDouble()), Math.Max(0, a[3].GetDouble()));
    private static string String(JsonElement row, string key) =>
        row.TryGetProperty(key, out var property) ? property.ToString() : "";
    private static bool Flag(JsonElement row, string key, bool fallback = false) =>
        row.TryGetProperty(key, out var property) ? property.ValueKind == JsonValueKind.True : fallback;
    private static double Number(JsonElement row, string key, double fallback) =>
        row.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.Number ? property.GetDouble() : fallback;

    private SolidColorBrush Brush(string code, string fallback)
    {
        if (code.Length != 9 || code[0] != '#') code = fallback;
        if (brushes.TryGetValue(code, out var brush)) return brush;
        uint rgba = uint.Parse(code.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        brush = new SolidColorBrush(Color.FromArgb((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba));
        // User-adjustable colors can yield a distinct shade on every frame.
        if (brushes.Count < 1024) brushes.Add(code, brush);
        return brush;
    }

    private void UpdatePaths(Canvas canvas, JsonElement row)
    {
        canvas.Children.Clear();
        if (!row.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array) return;
        foreach (var path in paths.EnumerateArray())
        {
            var geometry = new PathGeometry();
            PathFigure? figure = null;
            foreach (var cmd in path.GetProperty("commands").EnumerateArray())
            {
                Point PointAt(int i) => new(cmd[i].GetDouble(), cmd[i + 1].GetDouble());
                switch (cmd[0].GetString())
                {
                    case "M": figure = new PathFigure { StartPoint = PointAt(1) }; geometry.Figures.Add(figure); break;
                    case "L": figure?.Segments.Add(new LineSegment { Point = PointAt(1) }); break;
                    case "C": figure?.Segments.Add(new BezierSegment { Point1 = PointAt(1), Point2 = PointAt(3), Point3 = PointAt(5) }); break;
                    case "Z": if (figure != null) figure.IsClosed = true; break;
                }
            }
            var color = Brush(String(path, "color"), "#FFFFFFFF");
            bool fill = Flag(path, "fill");
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = fill ? color : null,
                Stroke = fill ? null : color, StrokeThickness = Number(path, "width", 1), IsHitTestVisible = false });
        }
    }

    internal void SetBitmap(WriteableBitmap? source)
    {
        if (ReferenceEquals(bitmap, source)) return;
        bitmap = source;
        foreach (var item in items.Values)
            if (item.Element is Image image) image.Source = source;
    }

    internal WriteableBitmap Present(EngineBridge.Frame frame)
    {
        if (bitmap == null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
            SetBitmap(new WriteableBitmap(frame.Width, frame.Height));
        var pixels = MemoryMarshal.Cast<byte, uint>(frame.Pixels.AsSpan(0, frame.Width * frame.Height * 4));
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            pixels[i] = (p & 0xff00ff00) | ((p & 0xff) << 16) | ((p >> 16) & 0xff);
        }
        using var stream = bitmap!.PixelBuffer.AsStream();
        stream.Write(frame.Pixels, 0, frame.Width * frame.Height * 4);
        bitmap.Invalidate();
        return bitmap;
    }
}
