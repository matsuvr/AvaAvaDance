using System;
using System.Collections.Generic;

namespace AvaAvaDance
{
    // Pure planning bounds shared by runtime baking and its headless harness.
    internal static class PhysicsBakeRange
    {
        internal static void Validate(int first, int last, int maxFrame, int bones, long keyLimit)
        {
            if (first < 0 || last < first || last > maxFrame)
                throw new ArgumentException("開始・終了フレームが範囲外です（0～" + maxFrame + "）。");
            if (bones <= 0) throw new ArgumentException("物理ボーンがありません。");
            if ((long)bones * ((long)last - first + 3) > keyLimit)
                throw new ArgumentException("生成キーが多すぎます。範囲を短くしてください（上限100万キー）。");
        }
        internal static IEnumerable<int> Guards(int first, int last, int maxFrame)
        {
            if (first > 0) yield return first - 1;
            if (last < maxFrame) yield return last + 1;
        }
    }
}
