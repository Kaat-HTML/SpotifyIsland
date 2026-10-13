using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SpotifyIsland
{
    public enum WeatherKind
    {
        Sun, Moon, PartlySun, PartlyMoon, Cloud, Fog, Drizzle, Rain,
        Showers, ShowersNight, Thunder, Snow
    }

    /// <summary>
    /// Vector weather icons drawn in code (no image downloads, crisp at any size, almost free to render).
    /// </summary>
    public static class WeatherIcons
    {
        private static readonly Dictionary<WeatherKind, DrawingImage> Cache =
            new Dictionary<WeatherKind, DrawingImage>();

        public static DrawingImage Get(WeatherKind kind)
        {
            if (!Cache.TryGetValue(kind, out var img))
            {
                img = Build(kind);
                Cache[kind] = img;
            }
            return img;
        }

        // Buienradar icon codes: a letter per condition, a doubled letter (aa, bb, ...) is the night version.
        public static WeatherKind FromCode(string code, bool night, string description)
        {
            string c = (code ?? "").ToLowerInvariant();
            if (c.Length == 2 && c[0] == c[1])
            {
                night = true;
                c = c.Substring(0, 1);
            }

            switch (c)
            {
                case "a": return night ? WeatherKind.Moon : WeatherKind.Sun;
                case "b": case "j": case "o": case "r": return night ? WeatherKind.PartlyMoon : WeatherKind.PartlySun;
                case "c": case "p": return WeatherKind.Cloud;
                case "d": case "n": return WeatherKind.Fog;
                case "g": case "s": return WeatherKind.Thunder;
                case "f": case "h": return night ? WeatherKind.ShowersNight : WeatherKind.Showers;
                case "k": case "m": return WeatherKind.Drizzle;
                case "l": case "q": return WeatherKind.Rain;
                case "u": case "i": case "v": case "t": case "w": return WeatherKind.Snow;
            }

            // Fallback: guess from the Dutch description.
            string d = (description ?? "").ToLowerInvariant();
            if (d.Contains("onweer")) return WeatherKind.Thunder;
            if (d.Contains("sneeuw") || d.Contains("hagel")) return WeatherKind.Snow;
            if (d.Contains("mist") || d.Contains("nevel")) return WeatherKind.Fog;
            if (d.Contains("motregen")) return WeatherKind.Drizzle;
            if (d.Contains("regen") || d.Contains("bui")) return WeatherKind.Rain;
            if (d.Contains("zwaar") || d.Contains("bewolkt") && !d.Contains("half") && !d.Contains("licht") && !d.Contains("wissel"))
                return WeatherKind.Cloud;
            if (d.Contains("bewolkt")) return night ? WeatherKind.PartlyMoon : WeatherKind.PartlySun;
            return night ? WeatherKind.Moon : WeatherKind.Sun;
        }

        // Colour used for the notch glow while weather is showing.
        public static Color Accent(WeatherKind k)
        {
            switch (k)
            {
                case WeatherKind.Sun: return Color.FromRgb(255, 176, 32);
                case WeatherKind.Moon: return Color.FromRgb(99, 102, 255);
                case WeatherKind.PartlySun: return Color.FromRgb(255, 170, 70);
                case WeatherKind.PartlyMoon: return Color.FromRgb(92, 110, 230);
                case WeatherKind.Cloud: return Color.FromRgb(110, 140, 190);
                case WeatherKind.Fog: return Color.FromRgb(140, 160, 180);
                case WeatherKind.Drizzle: return Color.FromRgb(80, 140, 220);
                case WeatherKind.Rain: return Color.FromRgb(61, 139, 255);
                case WeatherKind.Showers: return Color.FromRgb(70, 150, 235);
                case WeatherKind.ShowersNight: return Color.FromRgb(70, 100, 220);
                case WeatherKind.Thunder: return Color.FromRgb(150, 100, 246);
                case WeatherKind.Snow: return Color.FromRgb(127, 214, 245);
                default: return Color.FromRgb(110, 140, 190);
            }
        }

        // ================= Drawing =================

        private static DrawingImage Build(WeatherKind kind)
        {
            var g = new DrawingGroup();
            // invisible 48x48 box so every icon has the same size
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 48, 48))));

            var rain = Color.FromRgb(98, 182, 255);

            switch (kind)
            {
                case WeatherKind.Sun:
                    AddSun(g, 24, 24, 10);
                    break;
                case WeatherKind.Moon:
                    AddMoon(g, 24, 24, 13, true);
                    break;
                case WeatherKind.PartlySun:
                    AddSun(g, 17, 17, 8);
                    AddCloud(g, 6, 9, 0.92, false);
                    break;
                case WeatherKind.PartlyMoon:
                    AddMoon(g, 17, 17, 9, false);
                    AddCloud(g, 6, 9, 0.92, false);
                    break;
                case WeatherKind.Cloud:
                    AddCloud(g, -1, 1, 1.0, false);
                    break;
                case WeatherKind.Fog:
                    AddCloud(g, 0, -4, 0.95, false);
                    AddLines(g, new[] { 9.0, 15.0, 9.0 }, new[] { 37.0, 41.0, 45.0 }, new[] { 30.0, 24.0, 28.0 },
                             Color.FromRgb(183, 194, 208), 2.6);
                    break;
                case WeatherKind.Drizzle:
                    AddCloud(g, 1, -3, 0.95, true);
                    AddDrops(g, new[] { 19.0, 29.0 }, 36, 41, -2, Color.FromRgb(140, 200, 255), 2.2);
                    break;
                case WeatherKind.Rain:
                    AddCloud(g, 1, -3, 0.95, true);
                    AddDrops(g, new[] { 17.0, 25.0, 33.0 }, 36, 44, -3, rain, 2.6);
                    break;
                case WeatherKind.Showers:
                    AddSun(g, 15, 14, 6.5);
                    AddCloud(g, 7, 2, 0.88, true);
                    AddDrops(g, new[] { 22.0, 30.0, 38.0 }, 37, 45, -3, rain, 2.6);
                    break;
                case WeatherKind.ShowersNight:
                    AddMoon(g, 15, 15, 8, false);
                    AddCloud(g, 7, 2, 0.88, true);
                    AddDrops(g, new[] { 22.0, 30.0, 38.0 }, 37, 45, -3, rain, 2.6);
                    break;
                case WeatherKind.Thunder:
                    AddCloud(g, 1, -4, 0.95, true);
                    var bolt = new LinearGradientBrush(Color.FromRgb(255, 224, 102), Color.FromRgb(255, 179, 0), 90);
                    bolt.Freeze();
                    g.Children.Add(new GeometryDrawing(bolt, null,
                        Geometry.Parse("M27,27 L19,38 L25,38 L22,46 L33,33 L27,33 L31,27 Z")));
                    break;
                case WeatherKind.Snow:
                    AddCloud(g, 1, -3, 0.95, false);
                    var flake = new SolidColorBrush(Color.FromRgb(230, 245, 255));
                    flake.Freeze();
                    foreach (var p in new[] { new Point(16, 39), new Point(24, 43), new Point(32, 39), new Point(20, 46), new Point(28, 46) })
                        g.Children.Add(new GeometryDrawing(flake, null, new EllipseGeometry(p, 2.1, 2.1)));
                    break;
            }

            g.Freeze();
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        private static void AddSun(DrawingGroup g, double cx, double cy, double r)
        {
            var rayPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 201, 60)), r * 0.3)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            rayPen.Freeze();

            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                var p1 = new Point(cx + Math.Cos(a) * r * 1.45, cy + Math.Sin(a) * r * 1.45);
                var p2 = new Point(cx + Math.Cos(a) * r * 1.9, cy + Math.Sin(a) * r * 1.9);
                g.Children.Add(new GeometryDrawing(null, rayPen, new LineGeometry(p1, p2)));
            }

            var disc = new RadialGradientBrush(Color.FromRgb(255, 232, 130), Color.FromRgb(255, 154, 28))
            {
                GradientOrigin = new Point(0.35, 0.35)
            };
            disc.Freeze();
            g.Children.Add(new GeometryDrawing(disc, null, new EllipseGeometry(new Point(cx, cy), r, r)));
        }

        private static void AddMoon(DrawingGroup g, double cx, double cy, double r, bool stars)
        {
            var outer = new EllipseGeometry(new Point(cx, cy), r, r);
            var cut = new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.35), r * 0.85, r * 0.85);
            var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, outer, cut);

            var br = new LinearGradientBrush(Color.FromRgb(255, 244, 200), Color.FromRgb(233, 196, 106), 45);
            br.Freeze();
            g.Children.Add(new GeometryDrawing(br, null, crescent));

            if (stars)
            {
                var star = new SolidColorBrush(Color.FromRgb(255, 250, 225));
                star.Freeze();
                g.Children.Add(new GeometryDrawing(star, null, new EllipseGeometry(new Point(37, 12), 1.6, 1.6)));
                g.Children.Add(new GeometryDrawing(star, null, new EllipseGeometry(new Point(41, 22), 1.1, 1.1)));
                g.Children.Add(new GeometryDrawing(star, null, new EllipseGeometry(new Point(33, 7), 1.0, 1.0)));
            }
        }

        private static void AddCloud(DrawingGroup g, double ox, double oy, double scale, bool dark)
        {
            var geo = new GeometryGroup { FillRule = FillRule.Nonzero };
            geo.Children.Add(new RectangleGeometry(new Rect(8, 24, 34, 14), 7, 7));
            geo.Children.Add(new EllipseGeometry(new Point(19, 25), 9, 9));
            geo.Children.Add(new EllipseGeometry(new Point(30, 20), 12, 12));
            geo.Transform = new TransformGroup
            {
                Children = { new ScaleTransform(scale, scale), new TranslateTransform(ox, oy) }
            };

            var brush = dark
                ? new LinearGradientBrush(Color.FromRgb(172, 184, 200), Color.FromRgb(116, 128, 146), 90)
                : new LinearGradientBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(196, 207, 222), 90);
            brush.Freeze();
            g.Children.Add(new GeometryDrawing(brush, null, geo));
        }

        private static void AddDrops(DrawingGroup g, double[] xs, double y0, double y1, double dx, Color c, double w)
        {
            var pen = new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            foreach (var x in xs)
                g.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(x, y0), new Point(x + dx, y1))));
        }

        // horizontal lines (fog): x start, y, length
        private static void AddLines(DrawingGroup g, double[] xs, double[] ys, double[] lens, Color c, double w)
        {
            var pen = new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            for (int i = 0; i < xs.Length; i++)
                g.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(xs[i], ys[i]), new Point(xs[i] + lens[i], ys[i]))));
        }
    }
}
