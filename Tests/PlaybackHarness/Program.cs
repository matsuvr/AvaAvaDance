using System;
using AvaAvaDance;

static class Program
{
    static int assertions, cases;
    static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception(message);
    }
    static void Case(string name, Action body)
    {
        body(); cases++; Console.WriteLine("PASS " + name);
    }
    static void Range(PlaybackRange range, int first, int last, int repeatFirst)
    {
        Check(range.First == first && range.Last == last && range.RepeatFirst == repeatFirst,
            "Unexpected playback interval: " + range.First + ".." + range.Last + " repeat " + range.RepeatFirst);
    }
    static void Step(PlaybackRange range, long elapsed, bool repeat, int frame, long cycle, bool finished = false)
    {
        var value = range.At(elapsed, repeat);
        Check(value.Frame == frame && value.Cycle == cycle && value.Finished == finished,
            "Unexpected sample at " + elapsed + ": " + value.Frame + "/" + value.Cycle + "/" + value.Finished);
    }
    static void Main()
    {
        Case("MMD explicit range keeps inclusive endpoint and its own loop start", () => {
            var range = PlaybackRange.Specified(8, 15); Range(range, 8, 15, 8);
            Step(range, 0, false, 8, 0); Step(range, 4, false, 12, 0);
            Step(range, 7, false, 15, 0); Step(range, 8, false, 15, 0, true);
            Step(range, 8, true, 8, 1); Step(range, 15, true, 15, 1);
            Step(range, 16, true, 8, 2);
        });
        Case("MMM current position first pass then full motion loops", () => {
            var range = PlaybackRange.FromCurrent(48, 59); Range(range, 48, 59, 0);
            Step(range, 0, true, 48, 0); Step(range, 11, true, 59, 0);
            Step(range, 12, true, 0, 1); Step(range, 71, true, 59, 1);
            Step(range, 72, true, 0, 2); Step(range, 73, true, 1, 2);
            Step(range, 12, false, 59, 0, true);
        });
        Case("MMM first pass frame sequence", () => {
            var range = PlaybackRange.FromCurrent(5, 9);
            int[] expected = { 5, 6, 7, 8, 9, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 1 };
            for (int i = 0; i < expected.Length; i++) {
                var step = range.At(i, true);
                Check(step.Frame == expected[i], "frame sequence at " + i);
                Check(step.Cycle == (i < 5 ? 0 : i < 15 ? 1 : 2), "audio restart cycle at " + i);
            }
        });
        Case("MMM play from beginning", () => {
            var range = PlaybackRange.FromBeginning(59); Range(range, 0, 59, 0);
            Step(range, 0, false, 0, 0); Step(range, 59, false, 59, 0);
            Step(range, 60, false, 59, 0, true); Step(range, 60, true, 0, 1);
        });
        Case("bookmark interval excludes the following mark", () => {
            var range = PlaybackRange.BetweenBookmarks(35, 90, new[] { 20, 40, 60 });
            Range(range, 20, 39, 20); Step(range, 0, true, 20, 0);
            Step(range, 19, true, 39, 0); Step(range, 20, true, 20, 1);
            Step(range, 20, false, 39, 0, true);
        });
        Case("playhead on a bookmark selects its following section", () => {
            Range(PlaybackRange.BetweenBookmarks(20, 90, new[] { 20, 40, 60 }), 20, 39, 20);
            Range(PlaybackRange.BetweenBookmarks(40, 90, new[] { 20, 40, 60 }), 40, 59, 40);
            Range(PlaybackRange.BetweenBookmarks(60, 90, new[] { 20, 40, 60 }), 60, 90, 60);
        });
        Case("missing preceding bookmark uses frame zero", () => {
            Range(PlaybackRange.BetweenBookmarks(0, 90, new[] { 20, 40 }), 0, 19, 0);
            Range(PlaybackRange.BetweenBookmarks(10, 90, new[] { 20, 40 }), 0, 19, 0);
        });
        Case("missing following bookmark uses motion end", () => {
            Range(PlaybackRange.BetweenBookmarks(45, 90, new[] { 20, 40 }), 40, 90, 40);
            Range(PlaybackRange.BetweenBookmarks(90, 90, new[] { 20, 40 }), 40, 90, 40);
        });
        Case("no bookmarks uses whole motion", () => {
            Range(PlaybackRange.BetweenBookmarks(35, 90, Array.Empty<int>()), 0, 90, 0);
            Range(PlaybackRange.BetweenBookmarks(0, 0, Array.Empty<int>()), 0, 0, 0);
        });
        Case("a single bookmark handles each side", () => {
            Range(PlaybackRange.BetweenBookmarks(5, 90, new[] { 20 }), 0, 19, 0);
            Range(PlaybackRange.BetweenBookmarks(20, 90, new[] { 20 }), 20, 90, 20);
            Range(PlaybackRange.BetweenBookmarks(25, 90, new[] { 20 }), 20, 90, 20);
        });
        Case("adjacent bookmarks make a valid one-frame loop", () => {
            var range = PlaybackRange.BetweenBookmarks(20, 90, new[] { 20, 21 });
            Range(range, 20, 20, 20); Step(range, 0, true, 20, 0);
            Step(range, 1, true, 20, 1); Step(range, 200, true, 20, 200);
            Step(range, 1, false, 20, 0, true);
        });
        Case("future bookmark can extend beyond last key", () => {
            Range(PlaybackRange.BetweenBookmarks(30, 60, new[] { 20, 120 }), 20, 119, 20);
        });
        Case("playhead after motion end remains playable", () => {
            var range = PlaybackRange.FromCurrent(120, 60); Range(range, 120, 120, 0);
            Step(range, 0, true, 120, 0); Step(range, 1, true, 0, 1);
            Step(range, 1, false, 120, 0, true);
            Range(PlaybackRange.BetweenBookmarks(120, 60, new[] { 20, 40 }), 40, 120, 40);
            Range(PlaybackRange.BetweenBookmarks(120, 60, Array.Empty<int>()), 0, 120, 0);
        });
        Case("frame limit and final bookmark", () => {
            int max = PlaybackRange.FrameLimit;
            var range = PlaybackRange.BetweenBookmarks(max, 60, new[] { 20, max });
            Range(range, max, max, max); Step(range, 1, true, max, 1);
            Range(PlaybackRange.BetweenBookmarks(max - 1, 60, new[] { 20, max }), 20, max - 1, 20);
            range = PlaybackRange.FromCurrent(max, 60);
            Step(range, 0, true, max, 0); Step(range, 1, true, 0, 1);
            Step(range, max + 1L, true, max, 1); Step(range, max + 2L, true, 0, 2);
        });
        Case("unordered duplicate bookmarks resolve the same interval", () => {
            Range(PlaybackRange.BetweenBookmarks(35, 90, new[] { 60, 40, 20, 40, 0 }), 20, 39, 20);
        });
        Case("empty project and zero frame", () => {
            var range = PlaybackRange.FromCurrent(0, 0);
            Range(range, 0, 0, 0); Step(range, 0, false, 0, 0);
            Step(range, 1, false, 0, 0, true); Step(range, 1, true, 0, 1);
        });
        Case("skipped rendered frames retain absolute audio cycle", () => {
            var range = PlaybackRange.BetweenBookmarks(35, 90, new[] { 20, 40 });
            Step(range, 65, true, 25, 3);
            range = PlaybackRange.FromCurrent(48, 59);
            Step(range, 12 + 60 * 100L + 7, true, 7, 101);
        });
        Case("large offsets do not overflow cycles", () => {
            var range = PlaybackRange.Specified(20, 20);
            Step(range, long.MaxValue, true, 20, long.MaxValue);
            Step(range, long.MaxValue, false, 20, 0, true);
        });
        Case("negative elapsed time holds first frame", () => {
            Step(PlaybackRange.FromCurrent(10, 90), -1, true, 10, 0);
        });
        Case("repeat can be disabled after looping", () => {
            var range = PlaybackRange.FromCurrent(48, 59);
            Step(range, 18, true, 6, 1);
            Step(range, 18, false, 59, 0, true);
        });
        Console.WriteLine("PASS " + cases + " cases / " + assertions + " assertions (playback frame calculations; not a Unity or audio-device test)");
    }
}
