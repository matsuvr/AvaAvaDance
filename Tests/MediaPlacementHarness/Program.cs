using System;
using AvaAvaDance;
static class Program
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static void Main()
    {
        Check(AudioPlacement.SampleAtFrame(0, 30, 48000) == -48000, "silence before start");
        Check(AudioPlacement.SampleAtFrame(30, 30, 48000) == 0, "start sample");
        Check(AudioPlacement.SampleAtFrame(45, 30, 48000) == 24000, "seek sample");
        Check(AudioPlacement.SampleAtFrame(600000, 0, 192000) == 3840000000L, "long audio no overflow");
        Check(AudioPlacement.ExportSampleOrigin(30, 30, 0, 48000) == 0, "export starts at clip");
        Check(AudioPlacement.ExportSampleOrigin(0, 30, 48000, 48000) == 0, "export silence duration");
        Check(AudioPlacement.ExportSampleOrigin(45, 30, 0, 44100) == 22050, "trimmed output");
        for (int f = 0; f < 1000; f++) {
            double export = AudioPlacement.ExportSampleOrigin(f, 123, 0, 48000);
            Check(Math.Abs(export - AudioPlacement.SampleAtFrame(f, 123, 48000)) < .00001, "playback export agreement");
        }
        var initial = BackdropPlacement.Normalized(null);
        Check(initial.x == 0 && initial.y == 0 && initial.scale == 1, "legacy project identity");
        var input = new BackdropPlacement { x = .2f, y = -.1f, scale = 2 };
        var copy = BackdropPlacement.Normalized(input); copy.x = 0;
        Check(input.x == .2f && copy.scale == 2, "independent snapshot");
        var invalid = BackdropPlacement.Normalized(new BackdropPlacement { x = float.NaN, y = float.PositiveInfinity, scale = 0 });
        Check(invalid.x == 0 && invalid.y == 0 && invalid.scale == 1, "invalid input identity");
        var bounded = BackdropPlacement.Normalized(new BackdropPlacement { x = 20, y = -20, scale = 1000 });
        Check(bounded.x == 10 && bounded.y == -10 && bounded.scale == 100, "bounds");
        Console.WriteLine($"PASS: {checks} media placement assertions");
    }
}
