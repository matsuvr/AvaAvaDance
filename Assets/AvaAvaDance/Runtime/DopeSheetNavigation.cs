using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        float dopeFitSpacing, dopeFitWidth;
        (MotionTrack track, long revision, string layer, string clip)[] rulerContext;
        int rulerEnd = -1;
        int[] rulerKeys = Array.Empty<int>();

        int[] TransportKeyFrames()
        {
            var tracks = selectedRows.Count == 0 ? new[] { selectedTrack } : selectedRows.ToArray();
            var context = tracks.Where(track => track != null)
                .Select(track => (track, track.Revision, track.ActiveLayer, track.ActiveClip)).ToArray();
            int end = Math.Max(ribbonSeek.highValue, frame);
            if (rulerEnd != end || rulerContext == null || !rulerContext.SequenceEqual(context))
            {
                rulerContext = context; rulerEnd = end;
                rulerKeys = context.SelectMany(item => DocumentKeys(item.track, item.track.Target, 0, end)
                    .Where(at => item.track.Target != null || item.track.EnabledKey(at)))
                    .Distinct().OrderBy(at => at).ToArray();
            }
            return rulerKeys;
        }

        static IEnumerable<int> DocumentKeys(MotionTrack track, MotionLayer layer, int start, int end)
        {
            if (layer == null) return new KeyFrames(track.Frames).Between(start, end);
            if (layer.clips.Count == 0) return new KeyFrames(layer.keys).Between(start, end);
            var clip = layer.clips.Find(c => c.id == track.ActiveClip);
            return clip == null ? Enumerable.Empty<int>() : ClipKeys(layer, clip, start, end);
        }

        static IEnumerable<int> TimelineRowKeys(RibbonRow row, MotionTrack track, int start, int end)
        {
            IEnumerable<int> Keys(MotionTrack target, MotionLayer layer) => layer?.clips.Count > 0
                ? layer.clips.SelectMany(clip => ClipKeys(layer, clip, start, end)).Distinct().OrderBy(at => at)
                : DocumentKeys(target, layer, start, end);
            return row?.members != null
                ? row.members.SelectMany(member => Keys(member, row.layerGroup ? member.GroupedLayer : null))
                    .Distinct().OrderBy(at => at)
                : Keys(track, row?.layer);
        }

        static IEnumerable<int> ClipKeys(MotionLayer layer, ClipPlacement clip, int start, int end)
        {
            var source = layer.Source(clip.source);
            if (source.keys.Count == 0 || clip.muted) yield break;
            var outer = clip;
            while (!string.IsNullOrEmpty(outer.parent))
            {
                outer = layer.clips.Find(candidate => candidate.id == outer.parent);
                if (outer == null) yield break;
            }
            start = Math.Max(start, outer.start);
            end = outer.infinite ? end : Math.Min(end, outer.EndFrame);
            for (int at = start; at <= end; at++)
            {
                if (!layer.Map(clip, at, out float local, out _)) continue;
                int key = Mathf.RoundToInt(local);
                if (Mathf.Abs(local - key) < .01f && source.At(key).HasValue) yield return at;
            }
        }

        void FrameDopeSheet(bool selection, TimeWindow window = null)
        {
            if (graphDockTab && window == null || audioDockTab && window == null) return;
            int first = MaxFrame, last = -1, row = TimelineRows;
            if (selection)
            {
                var selected = selectedKeys.Count > 0 ? selectedKeys.ToLookup(k => k.track, k => k.frame) :
                    EditKeys().ToLookup(k => k.track, k => k.frame);
                foreach (var group in selected)
                {
                    var track = group.Key;
                    var keys = group.ToHashSet();
                    var layer = track.Target;
                    var positions = layer?.clips.Count > 0
                        ? DocumentKeys(track, layer, 0, Mathf.Max(frame, track.LastFrame))
                            .Where(at => track.LocalTime(at, out int local) && keys.Contains(local))
                        : keys;
                    foreach (int at in positions) { first = Math.Min(first, at); last = Math.Max(last, at); }
                    int index = ribbonRows.FindIndex(line => line.track == track && line.layer == layer ||
                        line.members?.Contains(track) == true);
                    if (index >= 0) row = Math.Min(row, index);
                }
            }
            else
            {
                first = 0;
                last = VisibleTracks.Select(track => track.LastFrame).DefaultIfEmpty(-1).Max();
                row = 0;
            }
            if (last < first) return;
            var board = window?.Board ?? timeline;
            float width = Mathf.Max(1, board.contentRect.width - NameWidth);
            float spacing = FitDopeSpacing(width, first, last);
            if (window != null) { window.FrameRange(first, last, row, spacing); return; }
            dopeFitSpacing = spacing; dopeFitWidth = width;
            firstRow = Mathf.Clamp(row, 0, MaxFirstRow());
            SetFirstFrame(first - (VisibleColumns - (last - first + 1)) / 2);
        }

        static float FitDopeSpacing(float width, int first, int last) =>
            Mathf.Min(48, width / (last - first + 3));

        static int TimelineTickStep(float spacing)
        {
            int target = Mathf.Clamp(Mathf.CeilToInt(34 / spacing), 1, MaxFrame + 1);
            int step = 5;
            while (step < target) step *= 2;
            return step;
        }

        void DrawSelectedKeySpan(DesktopPainter pen, float width, float y, float left, float right)
        {
            left = Mathf.Clamp(left, NameWidth, width); right = Mathf.Clamp(right, NameWidth, width);
            if (right <= left) return;
            pen.fillColor = new Color32(124, 102, 48, 215);
            pen.BeginPath();
            pen.MoveTo(new Vector2(left, y - 7)); pen.LineTo(new Vector2(right, y - 7));
            pen.LineTo(new Vector2(right, y + 7)); pen.LineTo(new Vector2(left, y + 7));
            pen.ClosePath(); pen.Fill();
        }
    }
}
