using System;

namespace Ava.Extensions
{
    // Coordinates are fractions of the output, measured from the top left.
    // Text is centered on (x,y). Size is a fraction of output height. Color is RRGGBBAA.
    public interface IOverlayCanvas
    {
        void Text(string text, float x, float y, float size, uint color);
    }

    public interface IFrameOverlay
    {
        int LastFrame { get; }
        void Draw(IOverlayCanvas canvas, float frame, bool editing);
    }

    public interface IOverlayEditor
    {
        int CurrentFrame { get; }
        int FrameLimit { get; }
        bool RibbonLayout { get; }
        bool CanEdit { get; }
        void Seek(int frame);
        IDisposable AddOverlay(IFrameOverlay overlay);
    }
}
