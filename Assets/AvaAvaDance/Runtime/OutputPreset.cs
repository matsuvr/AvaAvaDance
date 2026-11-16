using System;

namespace AvaAvaDance
{
    [Serializable]
    internal sealed class OutputPreset
    {
        public string name;
        public int width, height, fps;

        // Keep the existing movie encoder limits separate from still images.
        internal static bool ValidMovieSize(int width, int height) =>
            width>=16 && height>=16 && width<=4096 && height<=4096 && ((width|height)&1)==0;
        internal static bool ValidRate(int fps) => fps>=1 && fps<=120;
        internal bool Valid => !string.IsNullOrWhiteSpace(name) && width>0 && height>0 && ValidRate(fps);

        internal static OutputPreset[] Defaults() => new[] {
            new OutputPreset {name="640 × 360", width=640, height=360, fps=30},
            new OutputPreset {name="HD 720p", width=1280, height=720, fps=30},
            new OutputPreset {name="Full HD 1080p", width=1920, height=1080, fps=30},
            new OutputPreset {name="Full HD 1080p 60fps", width=1920, height=1080, fps=60},
            new OutputPreset {name="4K UHD", width=3840, height=2160, fps=30}
        };
    }
}
