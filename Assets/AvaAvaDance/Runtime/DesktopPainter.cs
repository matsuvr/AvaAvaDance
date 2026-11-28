using System;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // One drawing callback defines each timeline, curve, icon and rig overlay.
        // The Player paints with UI Toolkit; the desktop hosts consume the same paths.
        static readonly ConditionalWeakTable<VisualElement, Action<DesktopPainter>> desktopDrawings = new();

        static void BindDrawing(VisualElement element, Action<DesktopPainter> draw)
        {
            if (NativeDesktopActive) desktopDrawings.Add(element, draw);
            else element.generateVisualContent += context => draw(new DesktopPainter(context.painter2D));
        }

        sealed class DesktopPainter
        {
            readonly Painter2D unity;
            readonly JArray paths;
            JArray commands;
            float width = 1;
            Color fill = Color.white, stroke = Color.black;

            internal DesktopPainter(Painter2D painter) { unity = painter; }
            internal DesktopPainter(JArray output) { paths = output; }

            internal Color fillColor
            {
                get => unity == null ? fill : unity.fillColor;
                set { if (unity == null) fill = value; else unity.fillColor = value; }
            }
            internal Color strokeColor
            {
                set { if (unity == null) stroke = value; else unity.strokeColor = value; }
            }
            internal float lineWidth
            {
                set { if (unity == null) width = value; else unity.lineWidth = value; }
            }
            internal void BeginPath()
            {
                if (unity != null) unity.BeginPath(); else commands = new JArray();
            }
            internal void MoveTo(Vector2 p)
            {
                if (unity != null) unity.MoveTo(p); else commands.Add(new JArray("M", p.x, p.y));
            }
            internal void LineTo(Vector2 p)
            {
                if (unity != null) unity.LineTo(p); else commands.Add(new JArray("L", p.x, p.y));
            }
            internal void BezierCurveTo(Vector2 a, Vector2 b, Vector2 end)
            {
                if (unity != null) unity.BezierCurveTo(a, b, end);
                else commands.Add(new JArray("C", a.x, a.y, b.x, b.y, end.x, end.y));
            }
            internal void ClosePath()
            {
                if (unity != null) unity.ClosePath(); else commands.Add(new JArray("Z"));
            }
            internal void Circle(Vector2 center, float radius)
            {
                if (unity != null)
                {
                    unity.Arc(center, radius, Angle.Degrees(0), Angle.Degrees(360));
                    return;
                }
                // Four cubic segments support circles on both native graphics APIs.
                float k = radius * .55228475f;
                float x = center.x, y = center.y;
                MoveTo(new Vector2(x + radius, y));
                BezierCurveTo(new Vector2(x + radius, y + k), new Vector2(x + k, y + radius), new Vector2(x, y + radius));
                BezierCurveTo(new Vector2(x - k, y + radius), new Vector2(x - radius, y + k), new Vector2(x - radius, y));
                BezierCurveTo(new Vector2(x - radius, y - k), new Vector2(x - k, y - radius), new Vector2(x, y - radius));
                BezierCurveTo(new Vector2(x + k, y - radius), new Vector2(x + radius, y - k), new Vector2(x + radius, y));
                ClosePath();
            }
            void Record(bool closed)
            {
                paths.Add(new JObject {
                    ["commands"] = commands, ["fill"] = closed,
                    ["color"] = DesktopColor(closed ? fill : stroke), ["width"] = width
                });
            }
            internal void Fill() { if (unity != null) unity.Fill(); else Record(true); }
            internal void Stroke() { if (unity != null) unity.Stroke(); else Record(false); }
        }

        static string DesktopColor(Color value)
        {
            Color32 color = value;
            return $"#{color.a:X2}{color.r:X2}{color.g:X2}{color.b:X2}";
        }
    }
}
