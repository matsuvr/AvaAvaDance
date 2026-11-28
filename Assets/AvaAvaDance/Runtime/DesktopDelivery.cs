namespace AvaAvaDance
{
    // A resize invalidates even a frame whose dimensions happen to match again.
    // Document numbers are owned by the editor and advance on load/new/rollback.
    internal sealed class DesktopDelivery
    {
        internal bool Open { get; private set; } = true;
        internal long Layout { get; private set; }
        internal void Resize() => Layout++;
        internal void Close() => Open = false;
        internal bool AcceptInput(long? displayed, long current) => Open && displayed == current;
        internal bool AcceptFrame(long layout, long document, long current) =>
            layout == Layout && AcceptInput(document, current);
        internal static bool AcceptWindow(int? displayed, int current) =>
            current > 0 && displayed == current;
        internal static bool AcceptRevision(long displayed, long current) => displayed == current;
    }
}
