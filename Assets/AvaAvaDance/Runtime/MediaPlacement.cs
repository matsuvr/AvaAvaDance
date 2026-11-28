using System;

namespace AvaAvaDance
{
    internal static class AudioPlacement
    {
        public static long SampleAtFrame(int frame, int startFrame, int frequency) =>
            ((long)frame - startFrame) * frequency / 30;
        public static double ExportSampleOrigin(int firstFrame, int startFrame, long outputSample, int frequency) =>
            (((long)firstFrame - startFrame) / 30.0 + outputSample / 48000.0) * frequency;
    }

    [Serializable] internal sealed class BackdropPlacement
    {
        // Translation uses viewport fractions: +X right, +Y up. Scale is uniform.
        public float x, y, scale = 1;
        public static BackdropPlacement Normalized(BackdropPlacement value)
        {
            if (value == null) return new BackdropPlacement();
            bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
            return new BackdropPlacement {
                x = Finite(value.x) ? Math.Max(-10, Math.Min(10, value.x)) : 0,
                y = Finite(value.y) ? Math.Max(-10, Math.Min(10, value.y)) : 0,
                scale = Finite(value.scale) && value.scale > 0 ? Math.Max(.01f, Math.Min(100, value.scale)) : 1
            };
        }
    }
}
