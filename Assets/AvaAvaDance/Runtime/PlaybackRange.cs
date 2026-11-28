using System;
using System.Collections.Generic;

namespace AvaAvaDance
{
    // Frame calculations are independent of Unity's clock, audio and UI. End is
    // inclusive; a following bookmark belongs to the next section.
    internal readonly struct PlaybackRange
    {
        internal const int FrameLimit = 600000;
        internal readonly int First, Last, RepeatFirst;

        PlaybackRange(int first, int last, int repeatFirst)
        {
            CheckFrame(first, nameof(first)); CheckFrame(last, nameof(last));
            CheckFrame(repeatFirst, nameof(repeatFirst));
            if (last < first || repeatFirst > last) throw new ArgumentException("再生範囲が逆転しています。");
            First = first; Last = last; RepeatFirst = repeatFirst;
        }

        static void CheckFrame(int value, string name)
        {
            if (value < 0 || value > FrameLimit) throw new ArgumentOutOfRangeException(name);
        }

        internal static PlaybackRange Specified(int first, int last) => new PlaybackRange(first, last, first);

        internal static bool TryTransport(string from, string to, int current, int last, out PlaybackRange range)
        {
            if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
            {
                range = FromCurrent(current, last);
                return true;
            }
            if (TryLimit(from, 0, out int first) && TryLimit(to, last, out int end) && first <= end)
            {
                range = Specified(first, end);
                return true;
            }
            range = default;
            return false;
        }

        internal static bool TryLimit(string text, int fallback, out int frame)
        {
            frame = fallback;
            return string.IsNullOrWhiteSpace(text) || (int.TryParse(text, out frame) && frame >= 0 && frame <= FrameLimit);
        }

        internal static PlaybackRange FromCurrent(int current, int last) =>
            new PlaybackRange(current, Math.Max(current, last), 0);

        internal static PlaybackRange FromBeginning(int last) => new PlaybackRange(0, last, 0);

        internal static PlaybackRange BetweenBookmarks(int current, int last, IEnumerable<int> bookmarks)
        {
            CheckFrame(current, nameof(current)); CheckFrame(last, nameof(last));
            int first = 0, next = -1;
            foreach (int mark in bookmarks)
            {
                if (mark < 0 || mark > FrameLimit) continue;
                if (mark <= current) first = Math.Max(first, mark);
                else if (next < 0 || mark < next) next = mark;
            }
            // At a bookmark, play the section starting there. Missing outer
            // bookmarks use zero/the motion end; a playhead beyond the motion
            // end still has a valid section ending at the playhead.
            int end = next < 0 ? Math.Max(current, last) : next - 1;
            return new PlaybackRange(first, end, first);
        }

        internal PlaybackStep At(long elapsedFrames, bool repeat)
        {
            long offset = Math.Max(0, elapsedFrames);
            long initialLength = Last - First + 1L;
            if (offset < initialLength) return new PlaybackStep(First + (int)offset, 0, false);
            if (!repeat) return new PlaybackStep(Last, 0, true);
            long after = offset - initialLength;
            long loopLength = Last - RepeatFirst + 1L;
            return new PlaybackStep(RepeatFirst + (int)(after % loopLength), 1 + after / loopLength, false);
        }
    }

    internal readonly struct PlaybackStep
    {
        internal readonly int Frame;
        internal readonly long Cycle;
        internal readonly bool Finished;
        internal PlaybackStep(int frame, long cycle, bool finished)
        { Frame = frame; Cycle = cycle; Finished = finished; }
    }
}
