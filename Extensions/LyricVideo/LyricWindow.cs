using System;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Ava.Extensions;

namespace Ava.LyricVideo
{
    internal enum LyricAction { Draft, Put, Delete, Seek, Layer, AddLayer, DeleteLayer, Undo, Redo }

    internal sealed class LyricView
    {
        public long Document;
        public int Frame, Limit, Selected;
        public bool Editable, Ribbon, Undo, Redo;
        public string[] Layers;
        public int[] Keys;
        public LyricKey Value;
    }

    internal sealed class LyricWindow : IDisposable
    {
        const int Put = 1, Layer = 10, AddLayer = 11, DeleteLayer = 12, Undo = 13, Redo = 14;
        const int Frame = 20, Jump = 21, Previous = 22, Next = 23, FrameLabel = 24;
        const int Header = 100, Cell = 120, Columns = 10;
        const int Caption = 30, Words = 31, X = 40, Y = 41, Size = 42, Opacity = 43, Rgb = 44;
        const int DeleteKey = 50, Hint = 51;
        readonly IPluginContext context;
        readonly Action<LyricView, LyricAction, int, LyricKey> apply;
        readonly string[] labels = { "横位置 (%)", "縦位置 (%)", "文字高 (%)", "不透明度 (%)", "文字色 (RGB)" };
        LyricView view;
        int First => view.Frame / Columns * Columns;

        public LyricWindow(IPluginContext context, LyricView initial,
            Action<LyricView, LyricAction, int, LyricKey> apply)
        {
            this.context = context; this.apply = apply; view = initial;
        }

        public void Present(LyricView next)
        {
            view = next; desktop.Revision++; Display();
        }

        DesktopForm desktop;
        IDisposable openForm;
        readonly Dictionary<int, DesktopFormControl> fields = new Dictionary<int, DesktopFormControl>();

        public bool IsOpen => openForm != null;

        public void Open(string title, int width, int height)
        {
            var forms = (IDesktopFormHost)context;
            if (openForm != null) return;
            desktop = new DesktopForm { Name = "editor", Title = title, Width = width, Height = height,
                Layout = Layout, Input = DesktopInput, Closed = () => openForm = null };
            fields.Clear();
            Build();
            Layout(width, height);
            openForm = forms.OpenForm(desktop);
        }

        public Task CloseAsync() { Dispose(); return Task.CompletedTask; }
        public void Dispose() => openForm?.Dispose();

        void Add(int id, string kind, string label)
        {
            var field = new DesktopFormControl { Id = id, Name = "lyric-" + id, Kind = kind,
                Text = label, MaxLength = id == Words ? 4096 : 0,
                Hit = kind != "label" || id >= Header && id < Header + Columns || id >= Cell && id < Cell + Columns };
            fields.Add(id, field);
            desktop.Controls.Add(field);
        }
        void Place(int id, int x, int y, int w, int h)
        {
            var f = fields[id];
            f.X = x; f.Y = y; f.Width = Math.Max(1, w); f.Height = Math.Max(1, h);
        }
        void Enable(int id, bool enabled) => fields[id].Enabled = enabled;
        void Text(int id, string value) => fields[id].Text = value;
        string Text(int id) => fields[id].Text;

        void Build()
        {
            Add(Layer, "choice", "");
            Add(AddLayer, "button", "レイヤー追加");
            Add(DeleteLayer, "button", "レイヤー削除");
            Add(Undo, "button", "元に戻す"); Add(Redo, "button", "やり直す");
            Add(FrameLabel, "label", "フレーム"); Add(Frame, "text", "");
            Add(Jump, "button", "移動"); Add(Previous, "button", "前のキー");
            Add(Next, "button", "次のキー");
            for (int i = 0; i < Columns; i++)
            {
                Add(Header + i, "label", ""); // centered, clickable and vertically centered
                Add(Cell + i, "label", "");
            }
            Add(Caption, "label", "歌詞　Enterで登録・Shift+Enterで改行・空欄で非表示");
            Add(Words, "multiline", ""); // multiline, vertical scroll, accepts Return
            for (int i = 0; i < labels.Length; i++)
            {
                Add(60 + i, "label", labels[i]); Add(X + i, "text", "");
            }
            Add(Put, "button", "登録"); Add(DeleteKey, "button", "このキーを削除");
            Add(Hint, "label", "");
            Display();
        }

        void Layout(int width, int height)
        {
            int w = Math.Max(240, width - 20), layerWidth = Math.Max(80, w - 348);
            Place(Layer, 10, 8, layerWidth, 25);
            int left = 14 + layerWidth;
            foreach (int id in new[] { AddLayer, DeleteLayer, Undo, Redo })
            {
                int step = id == AddLayer || id == DeleteLayer ? 92 : 80;
                Place(id, left, 8, step - 4, 25); left += step;
            }
            Place(FrameLabel, 10, 45, 60, 22); Place(Frame, 72, 41, 72, 24);
            Place(Jump, 148, 40, 54, 25); Place(Previous, 208, 40, 76, 25); Place(Next, 288, 40, 76, 25);
            for (int i = 0; i < Columns; i++)
            {
                int x = 10 + w * i / Columns, cellWidth = 10 + w * (i + 1) / Columns - x;
                Place(Header + i, x, 70, cellWidth, 20); Place(Cell + i, x, 90, cellWidth, 24);
            }
            Place(Caption, 10, 121, w, 19);
            int wordsHeight = Math.Max(36, height - 262), bottom = 140 + wordsHeight;
            Place(Words, 10, 140, w, wordsHeight);
            for (int i = 0; i < labels.Length; i++)
            {
                int x = 10 + i * w / labels.Length, fieldWidth = w / labels.Length - 6;
                Place(60 + i, x, bottom + 6, fieldWidth, 18);
                Place(X + i, x, bottom + 25, fieldWidth, 23);
            }
            Place(Put, 10, bottom + 55, 88, 27); Place(DeleteKey, 104, bottom + 55, 112, 27);
            Place(Hint, 10, bottom + 88, w, 32);
        }

        void Display()
        {
            var key = view.Value;
            fields[Layer].Choices = view.Layers;
            Text(Layer, view.Layers[view.Selected]);
            Text(Frame, view.Frame.ToString()); Text(Words, key.Text.Replace("\n", "\r\n"));
            Text(X, key.X.ToString()); Text(Y, key.Y.ToString());
            Text(Size, key.Size.ToString()); Text(Opacity, key.Opacity.ToString()); Text(Rgb, key.Rgb.ToString("X6"));
            for (int i = 0; i < Columns; i++)
            {
                int at = First + i;
                Text(Header + i, at > view.Limit ? "" : at == view.Frame ? "[" + at + "]" : at.ToString());
                Text(Cell + i, at > view.Limit ? "" : Array.BinarySearch(view.Keys, at) >= 0 ? "◆" : "·");
                Enable(Header + i, view.Editable && at <= view.Limit);
                Enable(Cell + i, view.Editable && at <= view.Limit);
            }
            foreach (int id in new[] { Layer, AddLayer, DeleteLayer, Frame, Jump, Words, X, Y, Size, Opacity, Rgb, Put })
                Enable(id, view.Editable);
            Enable(Previous, view.Editable && view.Keys.Length > 0 && view.Keys[0] < view.Frame);
            Enable(Next, view.Editable && view.Keys.Length > 0 && view.Keys[view.Keys.Length - 1] > view.Frame);
            Enable(Undo, view.Editable && view.Undo); Enable(Redo, view.Editable && view.Redo);
            Enable(DeleteKey, view.Editable && Array.BinarySearch(view.Keys, view.Frame) >= 0);
            Text(Hint, !view.Editable ? "再生・処理中" : view.Ribbon ?
                "リボン　数字で移動。キー欄のダブルクリック、または「登録」でキーを打ちます。" :
                "クラシック　数字で移動。歌詞を編集し、「登録」で現在フレームにキーを打ちます。");
        }

        bool ReadValue(out LyricKey key)
        {
            key = new LyricKey { Frame = view.Frame, Text = Text(Words).Replace("\r\n", "\n").Replace('\r', '\n') };
            string rgb = Text(Rgb).Trim().TrimStart('#');
            return int.TryParse(Text(X), out key.X) && int.TryParse(Text(Y), out key.Y) &&
                int.TryParse(Text(Size), out key.Size) && int.TryParse(Text(Opacity), out key.Opacity) &&
                rgb.Length == 6 && int.TryParse(rgb, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out key.Rgb) &&
                key.Valid(view.Limit);
        }

        void Send(LyricAction action, int argument = 0, LyricKey value = default)
        {
            var source = view;
            context.Post(() => apply(source, action, argument, value));
        }

        void WriteKey(LyricAction action, int at)
        {
            if (ReadValue(out var key)) Send(action, at, key);
            else if (action == LyricAction.Put)
                Text(Hint, "位置・不透明度は0～100、文字高は1～40の整数、文字色は6桁のRGBで指定してください。");
        }

        void DesktopInput(int id, string action, string value, int clicks)
        {
            if (!view.Editable) return;
            if (action == "set")
            {
                if (id == Layer)
                {
                    int index = Array.IndexOf(view.Layers, value);
                    if (index >= 0 && index != view.Selected) Send(LyricAction.Layer, index);
                }
                else if (id == Words || (id >= X && id <= Rgb)) WriteKey(LyricAction.Draft, view.Frame);
                return;
            }
            if (id == Words && action == "submit") { WriteKey(LyricAction.Put, view.Frame); return; }
            if (action == "input" && clicks > 1 && id >= Cell && id < Cell + Columns)
            {
                int at = First + id - Cell;
                if (view.Ribbon) WriteKey(LyricAction.Put, at); else Send(LyricAction.Seek, at);
                return;
            }
            if (action != "activate" && action != "input") return;
            if (id >= Header && id < Header + Columns) { Send(LyricAction.Seek, First + id - Header); return; }
            switch (id)
            {
                case Put: WriteKey(LyricAction.Put, view.Frame); break;
                case Jump:
                    if (int.TryParse(Text(Frame), out int at) && at >= 0 && at <= view.Limit) Send(LyricAction.Seek, at);
                    else Text(Hint, "フレームは0～" + view.Limit + "の整数で指定してください。");
                    break;
                case Previous: Send(LyricAction.Seek, view.Keys.Last(k => k < view.Frame)); break;
                case Next: Send(LyricAction.Seek, view.Keys.First(k => k > view.Frame)); break;
                case AddLayer: Send(LyricAction.AddLayer); break;
                case DeleteLayer:
                    if (view.Keys.Length != 0)
                    {
                        var from = view;
                        ((IDesktopFormHost)context).ConfirmForm("レイヤー削除", view.Layers[view.Selected] + "とそのキーを削除しますか？",
                            () => context.Post(() => apply(from, LyricAction.DeleteLayer, 0, default)));
                    }
                    else Send(LyricAction.DeleteLayer);
                    break;
                case DeleteKey: Send(LyricAction.Delete, view.Frame); break;
                case Undo: Send(LyricAction.Undo); break;
                case Redo: Send(LyricAction.Redo); break;
            }
        }
    }
}
