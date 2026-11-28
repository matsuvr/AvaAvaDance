using System;

namespace AvaAvaDance
{
    internal static class WindowPlacement
    {
        internal const int CompactWorkspaceWidth = 760;

        internal static (int preset, int viewport, int dock) WorkspaceDefaults(int preset, int viewport, int dock) =>
            (Math.Max(0, Math.Min(2, preset)), Math.Max(33, Math.Min(73, viewport)), Math.Max(220, Math.Min(380, dock)));

        internal static bool StackWorkspace(int preset, float logicalWidth) =>
            preset == 2 || logicalWidth < CompactWorkspaceWidth;

        internal static int ViewportShare(int preset, int viewport) => preset == 1 ? Math.Max(67, viewport) : viewport;

        // Coordinates are relative to the work area. Retain physical pixels at the same DPI.
        internal static (int position, int size) Fit(int available, int offset, int size, int oldDpi, int newDpi, bool center)
        {
            long Scale(int value) => (long)Math.Round((double)value * newDpi / oldDpi, MidpointRounding.AwayFromZero);
            int extent = (int)Math.Max(1, Math.Min(available, Scale(size)));
            int room = available - extent;
            return (center ? room / 2 : (int)Math.Max(0, Math.Min(room, Scale(offset))), extent);
        }
    }
}
