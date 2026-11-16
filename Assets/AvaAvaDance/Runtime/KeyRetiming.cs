using System;
using System.Collections.Generic;
using System.Linq;

namespace AvaAvaDance
{
    // A complete, non-mutating edit plan. Payloads include their curves and import metadata;
    // retiming changes only the integer frame at which the payload is stored.
    internal static class KeyRetiming
    {
        internal readonly struct Change<T>
        {
            internal readonly int Frame;
            internal readonly bool HasBefore, HasAfter;
            internal readonly T Before, After;
            internal Change(int frame, bool hasBefore, T before, bool hasAfter, T after)
            { Frame = frame; HasBefore = hasBefore; Before = before; HasAfter = hasAfter; After = after; }
        }

        internal sealed class Plan<T>
        {
            internal readonly Change<T>[] Changes;
            internal readonly int[] Destinations;
            internal readonly int InputCount, MergedCount, OverwrittenCount;
            internal Plan(Change<T>[] changes, int[] destinations, int inputCount, int mergedCount, int overwrittenCount)
            { Changes = changes; Destinations = destinations; InputCount = inputCount; MergedCount = mergedCount; OverwrittenCount = overwrittenCount; }
        }

        internal static Plan<T> Scale<T>(IReadOnlyDictionary<int, T> keys, IEnumerable<int> selected,
            int origin, double factor, int maxFrame, bool preserveFrameZero)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0)
                throw new ArgumentOutOfRangeException(nameof(factor), "時間倍率には0より大きい有限の数を指定してください。");
            if (origin < 0 || origin > maxFrame)
                throw new ArgumentOutOfRangeException(nameof(origin), "基準フレームが範囲外です。");

            var frames = selected.Distinct().OrderBy(f => f).ToArray();
            foreach (int at in frames)
                if (at < 0 || at > maxFrame || !keys.ContainsKey(at))
                    throw new ArgumentException("伸縮するキーが見つからないか、フレームが範囲外です。", nameof(selected));

            if (factor == 1 || frames.Length == 0)
                return new Plan<T>(Array.Empty<Change<T>>(), frames, frames.Length, 0, 0);

            var chosen = new HashSet<int>(frames);
            var writes = new SortedDictionary<int, T>();
            foreach (int at in frames)
            {
                // Base tracks always retain an initial key. Layer sources may move their
                // zero key normally, since an empty layer has an identity contribution.
                double destination = preserveFrameZero && at == 0 ? 0 :
                    Math.Round(origin + (at - (double)origin) * factor, MidpointRounding.AwayFromZero);
                if (double.IsNaN(destination) || double.IsInfinity(destination) || destination < 0 || destination > maxFrame)
                    throw new ArgumentOutOfRangeException(nameof(factor), "伸縮後のキーが0～" + maxFrame + "フレームを超えます。");

                // Dense imported motions naturally collide when compressed. Visiting the
                // original frames in ascending order makes the later original key win.
                writes[(int)destination] = keys[at];
            }

            var changes = new List<Change<T>>();
            var comparer = EqualityComparer<T>.Default;
            foreach (int at in frames.Concat(writes.Keys).Distinct().OrderBy(f => f))
            {
                bool before = keys.TryGetValue(at, out var oldValue);
                bool after = writes.TryGetValue(at, out var newValue);
                if (!after && preserveFrameZero && at == 0 && before) { after = true; newValue = oldValue; }
                if (before != after || before && !comparer.Equals(oldValue, newValue))
                    changes.Add(new Change<T>(at, before, oldValue, after, newValue));
            }

            int overwritten = writes.Keys.Count(at => keys.ContainsKey(at) && !chosen.Contains(at));
            return new Plan<T>(changes.ToArray(), writes.Keys.ToArray(), frames.Length,
                frames.Length - writes.Count, overwritten);
        }
    }
}
