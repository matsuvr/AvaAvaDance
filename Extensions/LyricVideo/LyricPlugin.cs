using System;
using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;

namespace Ava.LyricVideo
{
    public sealed class LyricPlugin : IAvaPlugin, IFrameOverlay, IAsyncShutdown
    {
        IPluginContext host;
        IOverlayEditor editor;
        IDisposable command, overlay, projects;
        Lyrics lyrics;
        byte[] saved;
        LyricWindow window;
        LyricView view;
        LyricKey? draft;
        int selected;
        readonly List<byte[]> undo = new List<byte[]>(), redo = new List<byte[]>();
        public int LastFrame { get; private set; }

        public void Register(IPluginRegistrar registrar) => command = registrar.AddCommand(
            "ava.lyric-video.edit", "プラグイン(&E)", "リリックビデオ…", Open, () => lyrics != null && host is IDesktopFormHost forms && forms.SupportsDesktopForms);

        public void Activate(IPluginContext context)
        {
            host = context;
            editor = (IOverlayEditor)context;
            Reload();
            projects = host.ObserveProjects(_ => Reload(), null);
            overlay = editor.AddOverlay(this);
        }

        void Reload()
        {
            window?.Dispose();
            window = null; view = null; draft = null; selected = 0;
            undo.Clear(); redo.Clear();
            saved = host.ReadState();
            lyrics = null; LastFrame = 0;
            try { lyrics = Lyrics.Decode(saved, editor.FrameLimit); LastFrame = lyrics.LastFrame; }
            catch (Exception ex) { host.Report("リリックビデオの読込失敗　" + ex.Message); }
        }

        LyricView Snapshot()
        {
            var layer = lyrics.Layers[selected];
            return new LyricView {
                Document = host.DocumentGeneration, Frame = editor.CurrentFrame, Limit = editor.FrameLimit,
                Ribbon = editor.RibbonLayout, Editable = editor.CanEdit,
                Layers = lyrics.Layers.Select(item => item.Name).ToArray(), Selected = selected,
                Keys = layer.Keys.Select(key => key.Frame).ToArray(), Value = draft ?? layer.EditValue(editor.CurrentFrame),
                Undo = undo.Count != 0, Redo = redo.Count != 0
            };
        }

        void Open()
        {
            if (window?.IsOpen != true)
            {
                draft = null;
                view = Snapshot();
                window = new LyricWindow(host, view, Apply);
            }
            window.Open("リリックビデオ", 650, 362);
        }

        void Refresh()
        {
            view = Snapshot();
            window?.Present(view);
        }

        void SyncWindow()
        {
            if (window == null) return;
            if (!window.IsOpen) { window = null; view = null; draft = null; return; }
            if (view.Ribbon != editor.RibbonLayout || view.Editable != editor.CanEdit ||
                (editor.CanEdit && view.Frame != editor.CurrentFrame))
            {
                draft = null;
                Refresh();
            }
        }

        void Apply(LyricView from, LyricAction action, int argument, LyricKey value)
        {
            SyncWindow();
            if (from != view || view == null || !editor.CanEdit || from.Document != host.DocumentGeneration) return;
            switch (action)
            {
                case LyricAction.Draft:
                    draft = value;
                    return;
                case LyricAction.Seek:
                    draft = null; editor.Seek(argument); Refresh(); return;
                case LyricAction.Layer:
                    selected = argument; draft = null; Refresh(); return;
                case LyricAction.Undo: Restore(undo, redo); return;
                case LyricAction.Redo: Restore(redo, undo); return;
            }

            var next = Lyrics.Decode(saved, editor.FrameLimit);
            int nextLayer = selected;
            var layer = next.Layers[selected];
            switch (action)
            {
                case LyricAction.Put:
                    if (argument != view.Frame && !draft.HasValue) value = layer.EditValue(argument);
                    value.Frame = argument;
                    if (!value.Valid(editor.FrameLimit)) return;
                    layer.Put(value);
                    break;
                case LyricAction.Delete:
                    if (layer.Keys.RemoveAll(k => k.Frame == argument) == 0) return;
                    break;
                case LyricAction.AddLayer:
                    int number = next.Layers.Count + 1;
                    while (next.Layers.Any(l => l.Name == "歌詞 " + number)) number++;
                    nextLayer = next.Layers.Count;
                    next.Layers.Add(new LyricLayer { Name = "歌詞 " + number });
                    break;
                case LyricAction.DeleteLayer:
                    next.Layers.RemoveAt(selected);
                    if (next.Layers.Count == 0) next = new Lyrics();
                    nextLayer = Math.Min(selected, next.Layers.Count - 1);
                    break;
                default: return;
            }

            byte[] bytes = next.Encode();
            if (saved == null || !bytes.SequenceEqual(saved))
            {
                host.WriteState(bytes);
                Remember(undo, saved); redo.Clear();
                lyrics = next; saved = bytes; LastFrame = lyrics.LastFrame;
            }
            selected = nextLayer; draft = null;
            if (action == LyricAction.Put && editor.CurrentFrame != argument) editor.Seek(argument);
            Refresh();
        }

        static void Remember(List<byte[]> history, byte[] state)
        {
            if (history.Count == 40) history.RemoveAt(0);
            history.Add(state);
        }

        void Restore(List<byte[]> source, List<byte[]> destination)
        {
            if (source.Count == 0) return;
            var bytes = source[source.Count - 1];
            var next = Lyrics.Decode(bytes, editor.FrameLimit);
            host.WriteState(bytes);
            Remember(destination, saved); source.RemoveAt(source.Count - 1);
            lyrics = next; saved = bytes; LastFrame = lyrics.LastFrame;
            selected = Math.Min(selected, lyrics.Layers.Count - 1);
            draft = null; Refresh();
        }

        public void Draw(IOverlayCanvas canvas, float frame, bool editing)
        {
            if (lyrics == null) return;
            SyncWindow();
            for (int i = 0; i < lyrics.Layers.Count; i++)
            {
                var key = lyrics.Layers[i].Sample(frame, out float x, out float y, out float size, out float opacity);
                if (editing && draft.HasValue && i == selected && draft.Value.Frame == frame)
                {
                    key = draft.Value; x = key.X; y = key.Y; size = key.Size; opacity = key.Opacity;
                }
                if (key.Text.Length == 0 || opacity <= 0) continue;
                uint alpha = (uint)Math.Round(opacity * 2.55);
                canvas.Text(key.Text, x / 100f, (y + size * .055f) / 100f, size / 100f, alpha * 3 / 4);
                canvas.Text(key.Text, x / 100f, y / 100f, size / 100f, ((uint)key.Rgb << 8) | alpha);
            }
        }

        public void Deactivate()
        {
            overlay?.Dispose(); projects?.Dispose(); window?.Dispose();
            overlay = projects = null; window = null; view = null; lyrics = null;
            draft = null; undo.Clear(); redo.Clear(); saved = null; LastFrame = 0;
        }
        public System.Threading.Tasks.Task CloseAsync() => window?.CloseAsync() ?? System.Threading.Tasks.Task.CompletedTask;
        public void Dispose() => command?.Dispose();
    }
}
