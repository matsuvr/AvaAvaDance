using System;
using System.Collections.Generic;
using System.Linq;
using AvaAvaDance;

static class Program
{
    static int assertions, cases;
    sealed class Payload
    {
        internal readonly int Id;
        internal readonly float[] Curve = { .1f, .05f, .8f, .95f };
        internal readonly byte[] Source = { 1, 0, 255 };
        internal readonly bool PhysicsOff;
        internal Payload(int id) { Id = id; PhysicsOff = id % 2 == 0; }
    }
    static void Check(bool valid, string name)
    { assertions++; if (!valid) throw new Exception(name); }
    static void Test(string name, Action test)
    { test(); cases++; Console.WriteLine("PASS " + name); }
    static Dictionary<int, Payload> Keys(params int[] frames) => frames.ToDictionary(f => f, f => new Payload(f));
    static Dictionary<int, T> Apply<T>(IReadOnlyDictionary<int, T> before, KeyRetiming.Plan<T> plan, bool undo = false)
    {
        var result = before.ToDictionary(p => p.Key, p => p.Value);
        foreach (var edit in plan.Changes)
        {
            bool exists = undo ? edit.HasBefore : edit.HasAfter;
            if (exists) result[edit.Frame] = undo ? edit.Before : edit.After;
            else result.Remove(edit.Frame);
        }
        return result;
    }
    static void Same<T>(IReadOnlyDictionary<int, T> a, IReadOnlyDictionary<int, T> b, string name)
    {
        Check(a.Count == b.Count, name + " count");
        foreach (var pair in a)
            Check(b.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(pair.Value, value), name + " frame " + pair.Key);
    }
    static Dictionary<int, Payload> RoundTrip(Dictionary<int, Payload> before, KeyRetiming.Plan<Payload> plan)
    {
        var unchanged = before.ToDictionary(p => p.Key, p => p.Value);
        var after = Apply(before, plan);
        var restored = Apply(after, plan, true);
        Same(before, restored, "single undo restores all overwritten and merged keys");
        Same(after, Apply(restored, plan), "redo restores retimed keys");
        Same(before, unchanged, "source is not mutated");
        Check(plan.Changes.Select(c => c.Frame).Distinct().Count() == plan.Changes.Length, "one edit per destination");
        return after;
    }
    static void Reject(Dictionary<int, Payload> input, IEnumerable<int> selected, int origin, double factor, string name, int maximum = 600000)
    {
        var before = input.ToDictionary(p => p.Key, p => p.Value);
        bool threw = false;
        try { KeyRetiming.Scale(input, selected, origin, factor, maximum, true); }
        catch (ArgumentException) { threw = true; }
        Check(threw, name); Same(before, input, "invalid request is atomic");
    }

    static int Main()
    {
        Test("expand imported keys and retain all payload fields", () => {
            var keys = Keys(0, 2, 5);
            var beforePlan = keys.ToDictionary(p => p.Key, p => p.Value);
            var plan = KeyRetiming.Scale(keys, keys.Keys, 0, 2, 600000, true);
            Same(beforePlan, keys, "planning does not change the input dictionary");
            var after = RoundTrip(keys, plan);
            Check(after.Keys.OrderBy(x => x).SequenceEqual(new[] { 0, 4, 10 }), "double duration");
            Check(ReferenceEquals(after[4], keys[2]) && ReferenceEquals(after[10], keys[5]), "payload identity");
            Check(ReferenceEquals(after[4].Curve, keys[2].Curve) && ReferenceEquals(after[4].Source, keys[2].Source) && after[4].PhysicsOff, "curve/source/physics flags");
            Check(plan.MergedCount == 0 && plan.OverwrittenCount == 0, "no collisions");
        });
        Test("halve dense 30 fps motion with later key winning", () => {
            var keys = Keys(Enumerable.Range(0, 121).ToArray());
            var plan = KeyRetiming.Scale(keys, keys.Keys.Reverse(), 0, .5, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(after.Count == 61 && after.Keys.Max() == 60 && plan.MergedCount == 60, "dense motion compresses");
            for (int at = 0; at <= 60; at++) Check(ReferenceEquals(after[at], keys[at * 2]), "later original frame wins " + at);
            Check(plan.OverwrittenCount == 0, "moving selection is not counted as external overwrite");
        });
        Test("scope leaves unrelated keys unchanged and reports overwrites", () => {
            var keys = Keys(0, 10, 20, 25, 80);
            var plan = KeyRetiming.Scale(keys, new[] { 10 }, 0, 2, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(plan.OverwrittenCount == 1 && plan.MergedCount == 0, "outside selection overwrite is visible");
            Check(after.Count == 4 && !after.ContainsKey(10) && ReferenceEquals(after[20], keys[10]), "selected key replaces occupied destination");
            foreach (int at in new[] { 0, 25, 80 }) Check(ReferenceEquals(after[at], keys[at]), "unselected unaffected frame " + at);
        });
        Test("moving into another moving source reads the original snapshot", () => {
            var keys = Keys(0, 10, 20);
            var plan = KeyRetiming.Scale(keys, new[] { 10, 20 }, 0, 2, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(ReferenceEquals(after[20], keys[10]) && ReferenceEquals(after[40], keys[20]), "overlap does not cascade writes");
            Check(plan.OverwrittenCount == 0, "selected source is not external");
        });
        Test("scale around a nonzero origin and keep base zero", () => {
            var keys = Keys(0, 10, 20, 40);
            var plan = KeyRetiming.Scale(keys, keys.Keys, 10, .5, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(after.Keys.OrderBy(f => f).SequenceEqual(new[] { 0, 10, 15, 25 }), "base zero remains at zero");
            Check(ReferenceEquals(after[0], keys[0]) && ReferenceEquals(after[10], keys[10]), "origin and base are stable");
        });
        Test("layer source can move and remove its zero key", () => {
            var keys = Keys(0, 10, 20);
            var plan = KeyRetiming.Scale(keys, keys.Keys, 10, .5, 600000, false);
            var after = RoundTrip(keys, plan);
            Check(after.Keys.OrderBy(f => f).SequenceEqual(new[] { 5, 10, 15 }), "layer source zero moves");
            Check(ReferenceEquals(after[5], keys[0]), "layer zero payload retained");
        });
        Test("compression onto unselected base zero is reported and reversible", () => {
            var keys = Keys(0, 1, 50);
            var plan = KeyRetiming.Scale(keys, new[] { 1 }, 0, .1, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(ReferenceEquals(after[0], keys[1]) && plan.OverwrittenCount == 1, "zero overwritten by incoming key and counted");
            Check(after.ContainsKey(0) && !after.ContainsKey(1), "base initial key retained");
        });
        Test("compression onto selected base zero uses later key", () => {
            var keys = Keys(0, 1, 50);
            var plan = KeyRetiming.Scale(keys, new[] { 1, 0 }, 0, .1, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(ReferenceEquals(after[0], keys[1]) && plan.MergedCount == 1 && plan.OverwrittenCount == 0, "selected initial key merge");
        });
        Test("factor one and unchanged rounded times are true no-ops", () => {
            var keys = Keys(0, 1, 2, 20);
            foreach (double factor in new[] { 1d, .9999d }) {
                var plan = KeyRetiming.Scale(keys, keys.Keys, 10, factor, 600000, true);
                Check(plan.Changes.Length == 0 && plan.MergedCount == 0 && plan.OverwrittenCount == 0, "no phantom history");
                Same(keys, RoundTrip(keys, plan), "no-op values");
            }
        });
        Test("nearest-frame rounding is deterministic at half frames", () => {
            var keys = Keys(0, 1, 3, 5);
            var plan = KeyRetiming.Scale(keys, new[] { 5, 1, 3, 1 }, 0, .5, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(plan.InputCount == 3 && plan.Destinations.SequenceEqual(new[] { 1, 2, 3 }), "half frames round upward and duplicates collapse");
            Check(ReferenceEquals(after[1], keys[1]) && ReferenceEquals(after[2], keys[3]) && ReferenceEquals(after[3], keys[5]), "rounded payloads");
        });
        Test("maximum frame accepted exactly and rejected atomically beyond", () => {
            var keys = Keys(0, 300000);
            Check(RoundTrip(keys, KeyRetiming.Scale(keys, keys.Keys, 0, 2, 600000, true)).ContainsKey(600000), "inclusive maximum");
            Reject(Keys(0, 300001), new[] { 300001 }, 0, 2, "past maximum");
            Reject(Keys(0, 10), new[] { 10 }, 20, 3, "negative frame");
            Reject(Keys(0, 10), new[] { 10 }, 0, double.MaxValue, "overflowed destination");
        });
        Test("invalid factors, origins and missing selected keys", () => {
            var keys = Keys(0, 10);
            foreach (double factor in new[] { 0d, -1d, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
                Reject(keys, keys.Keys, 0, factor, "invalid factor " + factor);
            Reject(keys, keys.Keys, -1, 1, "negative origin");
            Reject(keys, keys.Keys, 600001, 1, "origin after maximum");
            Reject(keys, new[] { 11 }, 0, 2, "missing selected key");
            var empty = KeyRetiming.Scale(keys, Array.Empty<int>(), 0, 2, 600000, true);
            Check(empty.Changes.Length == 0 && empty.InputCount == 0, "empty selection plan");
        });
        Test("very small positive factors are valid compression", () => {
            var keys = Keys(0, 10, 20);
            var plan = KeyRetiming.Scale(keys, keys.Keys, 0, double.Epsilon, 600000, true);
            var after = RoundTrip(keys, plan);
            Check(after.Count == 1 && ReferenceEquals(after[0], keys[20]) && plan.MergedCount == 2, "tiny factor is not an arbitrary error");
        });
        Test("mixed unselected keys and collisions across repeatable sparse cases", () => {
            var random = new Random(314159);
            for (int iteration = 0; iteration < 80; iteration++) {
                var keys = Keys(Enumerable.Range(0, 100).Select(_ => random.Next(501)).Append(0).Distinct().ToArray());
                var selection = keys.Keys.Where(_ => random.Next(2) == 0).ToArray();
                double factor = new[] { .25, .5, 1, 2, 3 }[iteration % 5];
                var plan = KeyRetiming.Scale(keys, selection, 0, factor, 10000, true);
                var after = RoundTrip(keys, plan);
                Check(after.ContainsKey(0), "random base zero retained");
                foreach (int untouched in keys.Keys.Except(selection).Except(plan.Destinations))
                    Check(ReferenceEquals(after[untouched], keys[untouched]), "random unaffected key");
                var reordered = KeyRetiming.Scale(keys, selection.Reverse(), 0, factor, 10000, true);
                Same(after, Apply(keys, reordered), "selection order does not affect collisions");
            }
        });
        Console.WriteLine($"PASS: {cases} cases / {assertions} assertions (retiming core; not a Unity UI test)");
        return 0;
    }
}
