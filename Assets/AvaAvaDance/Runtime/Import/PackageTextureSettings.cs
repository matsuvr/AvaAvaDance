using System;
using System.Globalization;
using System.IO;

namespace AvaAvaDance.Import
{
    // Read the TextureImporter subset used by the Windows runtime PNG loader.
    internal sealed class PackageTextureSettings
    {
        internal bool Linear, NormalMap, FlipGreen, Mipmaps, IgnoreAlpha;
        internal int MaxSize = 1024, Filter = 1, WrapU, WrapV, Aniso = 1;
        internal float MipBias;

        internal static PackageTextureSettings Read(string meta, bool fallbackLinear)
        {
            var s = new PackageTextureSettings { Linear = fallbackLinear };
            if (string.IsNullOrEmpty(meta)) return s;
            using var reader = new StringReader(meta);
            bool importer = false, srgb = true;
            int defaultSize = 0, standaloneSize = 0, size = 0;
            bool overridden = false;
            string section = "", target = null;
            void FinishPlatform()
            {
                if (target == "DefaultTexturePlatform") defaultSize = size;
                if (target == "Standalone" && overridden) standaloneSize = size;
                target = null; size = 0; overridden = false;
            }
            for (string line; (line = reader.ReadLine()) != null;)
            {
                if (!importer)
                {
                    if (line == "TextureImporter:") { importer = true; s.MaxSize = 2048; s.Mipmaps = true; }
                    continue;
                }
                if (line.Length == 0) continue;
                int indent = line.Length - line.TrimStart(' ').Length;
                if (indent < 2) break;
                if (indent == 2 && line.StartsWith("  - ", StringComparison.Ordinal))
                { if (section == "platformSettings") FinishPlatform(); continue; }
                int colon = line.IndexOf(':', indent);
                if (colon < 0) continue;
                string key = line.Substring(indent, colon-indent), value = line.Substring(colon+1).Trim();
                if (indent == 2)
                {
                    if (section == "platformSettings") FinishPlatform();
                    section = key;
                }
                if (indent == 4 && section == "platformSettings" && key == "buildTarget") { target = value; continue; }
                if (indent == 4 && section == "textureSettings" && key == "mipBias" &&
                    float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float bias) && float.IsFinite(bias)) s.MipBias = bias;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
                if (indent == 2 && key == "maxTextureSize" && n > 0) s.MaxSize = n;
                if (indent == 2 && key == "textureType") s.NormalMap = n == 1;
                if (indent == 2 && key == "alphaUsage") s.IgnoreAlpha = n == 0;
                if (indent != 4) continue;
                switch (section)
                {
                    case "mipmaps":
                        if (key == "sRGBTexture") srgb = n != 0;
                        if (key == "enableMipMap") s.Mipmaps = n != 0;
                        break;
                    case "bumpmap": if (key == "flipGreenChannel") s.FlipGreen = n != 0; break;
                    case "textureSettings":
                        if (key == "filterMode" && n >= 0 && n <= 2) s.Filter = n;
                        if (key == "wrapU" && n >= 0 && n <= 3) s.WrapU = n;
                        if (key == "wrapV" && n >= 0 && n <= 3) s.WrapV = n;
                        if (key == "aniso" && n >= 0 && n <= 16) s.Aniso = n;
                        break;
                    case "platformSettings":
                        if (key == "maxTextureSize") size = n;
                        if (key == "overridden") overridden = n != 0;
                        break;
                }
            }
            FinishPlatform();
            if (importer) s.Linear = !srgb || s.NormalMap;
            if (standaloneSize > 0) s.MaxSize = standaloneSize;
            else if (defaultSize > 0) s.MaxSize = defaultSize;
            return s;
        }
    }
}
