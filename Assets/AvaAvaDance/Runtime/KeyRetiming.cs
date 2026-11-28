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

        internal delegate bool ReadKey<T>(int frame, out T value);

        internal static Plan<T> Scale<T>(IReadOnlyDictionary<int, T> keys, IEnumerable<int> selected,
            int origin, double factor, int maxFrame, bool preserveFrameZero) =>
            Scale<T>(keys.TryGetValue, selected, origin, factor, maxFrame, preserveFrameZero);

        internal static Plan<T> Scale<T>(ReadKey<T> readKey, IEnumerable<int> selected,
            int origin, double factor, int maxFrame, bool preserveFrameZero)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0)
                throw new ArgumentOutOfRangeException(nameof(factor), "時間倍率には0より大きい有限の数を指定してください。");
            if (origin < 0 || origin > maxFrame)
                throw new ArgumentOutOfRangeException(nameof(origin), "基準フレームが範囲外です。");

            var frames = selected.Distinct().OrderBy(f => f).ToArray();
            var originals = new T[frames.Length];
            for (int i = 0; i < frames.Length; i++)
                if (frames[i] < 0 || frames[i] > maxFrame || !readKey(frames[i], out originals[i]))
                    throw new ArgumentException("伸縮するキーが見つからないか、フレームが範囲外です。", nameof(selected));

            if (factor == 1 || frames.Length == 0)
                return new Plan<T>(Array.Empty<Change<T>>(), frames, frames.Length, 0, 0);

            var destinations = new List<int>(frames.Length);
            var values = new List<T>(frames.Length);
            for (int i = 0; i < frames.Length; i++)
            {
                int at = frames[i];
                // Base tracks always retain an initial key. Layer sources may move their
                // zero key normally, since an empty layer has an identity contribution.
                double destination = preserveFrameZero && at == 0 ? 0 :
                    Math.Round(origin + (at - (double)origin) * factor, MidpointRounding.AwayFromZero);
                if (double.IsNaN(destination) || double.IsInfinity(destination) || destination < 0 || destination > maxFrame)
                    throw new ArgumentOutOfRangeException(nameof(factor), "伸縮後のキーが0～" + maxFrame + "フレームを超えます。");

                // Dense imported motions naturally collide when compressed. Visiting the
                // original frames in ascending order makes the later original key win.
                int target = (int)destination;
                if (destinations.Count > 0 && destinations[destinations.Count - 1] == target)
                    values[values.Count - 1] = originals[i];
                else
                {
                    destinations.Add(target);
                    values.Add(originals[i]);
                }
            }

            var changes = new List<Change<T>>();
            var comparer = EqualityComparer<T>.Default;
            // A positive scale preserves order, including a pinned zero because all
            // accepted destinations are nonnegative. Merge the two sorted sequences.
            int source = 0, dest = 0, overwritten = 0;
            while (source < frames.Length || dest < destinations.Count)
            {
                int at = source == frames.Length ? destinations[dest] :
                    dest == destinations.Count ? frames[source] : Math.Min(frames[source], destinations[dest]);
                T oldValue;
                bool before;
                if (source < frames.Length && frames[source] == at)
                {
                    oldValue = originals[source++];
                    before = true;
                }
                else
                {
                    before = readKey(at, out oldValue);
                    if (before) overwritten++;
                }
                bool after = dest < destinations.Count && destinations[dest] == at;
                T newValue = after ? values[dest++] : default;
                if (before != after || before && !comparer.Equals(oldValue, newValue))
                    changes.Add(new Change<T>(at, before, oldValue, after, newValue));
            }

            return new Plan<T>(changes.ToArray(), destinations.ToArray(), frames.Length,
                frames.Length - destinations.Count, overwritten);
        }
    }
}
