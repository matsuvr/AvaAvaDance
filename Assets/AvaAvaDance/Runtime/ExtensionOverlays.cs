using System;
using System.Collections.Generic;
using Ava.Extensions;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        readonly List<IFrameOverlay> frameOverlays = new List<IFrameOverlay>();
        OverlayCanvas overlayCanvas;

        void DrawFrameOverlays(Camera camera)
        {
            if (camera != previewCamera || camera.targetTexture == null || frameOverlays.Count == 0) return;
            overlayCanvas ??= new OverlayCanvas();
            var target = camera.targetTexture;
            overlayCanvas.Width = target.width;
            overlayCanvas.Height = target.height;
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(0, target.width, 0, target.height);
                foreach (var overlay in frameOverlays)
                {
                    try { overlay.Draw(overlayCanvas, evaluationTime, !playing && !exporting); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            finally { GL.PopMatrix(); }
        }

        sealed class OverlayCanvas : IOverlayCanvas, IDisposable
        {
            internal int Width, Height;
            readonly TextGenerator generator = new TextGenerator();
            Font font;

            public void Text(string text, float x, float y, float size, uint color)
            {
                if (string.IsNullOrEmpty(text) || (color & 255) == 0 || size <= 0) return;
                if (font == null) font = Resources.Load<Font>("MPLUS1p-Regular");
                if (font == null) return;
                // Keep glyphs within the font atlas even on tall export targets.
                float pixelHeight = Mathf.Max(1, size * Height);
                int rasterSize = Mathf.Clamp(Mathf.RoundToInt(pixelHeight), 1, 256);
                float scale = pixelHeight / rasterSize;
                var settings = new TextGenerationSettings
                {
                    font = font, fontSize = rasterSize,
                    color = Color.white, scaleFactor = 1, lineSpacing = 1,
                    textAnchor = TextAnchor.MiddleCenter, pivot = new Vector2(.5f, .5f),
                    generationExtents = new Vector2(Width * .96f, Height) / scale,
                    horizontalOverflow = HorizontalWrapMode.Wrap, verticalOverflow = VerticalWrapMode.Overflow
                };
                if (!generator.Populate(text, settings) || !font.material.SetPass(0)) return;
                var vertices = generator.verts;
                var tint = new Color32((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
                float cx = x * Width, cy = (1 - y) * Height;
                GL.Begin(GL.QUADS);
                GL.Color(tint);
                for (int i = 0; i < vertices.Count; i++)
                {
                    var v = vertices[i];
                    GL.TexCoord2(v.uv0.x, v.uv0.y);
                    GL.Vertex3(cx + v.position.x * scale, cy + v.position.y * scale, 0);
                }
                GL.End();
            }

            public void Dispose() => ((IDisposable)generator).Dispose();
        }
    }
}
