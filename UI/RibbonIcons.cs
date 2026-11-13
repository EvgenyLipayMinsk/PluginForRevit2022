using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PluginForRevit2022.UI
{
    /// <summary>
    /// Значки ленты в стиле Revit: тонкие линии на прозрачном фоне, основной цвет — серый, одна деталь — акцент.
    /// Рисуются векторно (сетка 24×24) и растеризуются под нужный размер: 32 — крупная кнопка, 16 — мелкая.
    /// Свои пиктограммы, без файлов картинок и без значков Autodesk.
    /// </summary>
    internal static class RibbonIcons
    {
        public static readonly Color Accent = Color.FromRgb(24, 95, 165);   // синий: основные действия
        public static readonly Color On = Color.FromRgb(59, 109, 17);       // зелёный: MCP включён
        public static readonly Color Off = Color.FromRgb(136, 135, 128);    // серый: MCP выключен

        // Контур (основной цвет) и деталь (акцент) каждого значка, SVG-разметка путей WPF
        private static readonly Dictionary<string, (string body, string accent)> Shapes = new Dictionary<string, (string, string)>
        {
            ["fop"] = ("M6 3 H14 L18 7 V21 H6 Z M14 3 V7 H18", "M9 14 H15 M12 11 L15 14 L12 17"),
            ["removeParams"] = ("M6 3 H14 L18 7 V21 H6 Z M14 3 V7 H18", "M9 14 H15"),
            ["journal"] = ("M3 4 H21 V20 H3 Z M3 14 H21 M9 9 V20", "M3 9 H21"),
            ["marking"] = ("M3 12 V4 H11 L21 14 L13 22 Z", "M6 8 A1.5 1.5 0 1 0 9 8 A1.5 1.5 0 1 0 6 8"),
            ["addDevice"] = ("M4 6 H15 M4 11 H15 M4 16 H10", "M17 14 V22 M13 18 H21"),
            ["removeDevice"] = ("M4 6 H15 M4 11 H15 M4 16 H10", "M13 18 H21"),
            ["settings"] = ("M4 6 H20 M4 12 H20 M4 18 H20", "M7 6 A2 2 0 1 0 11 6 A2 2 0 1 0 7 6 M13 12 A2 2 0 1 0 17 12 A2 2 0 1 0 13 12 M5 18 A2 2 0 1 0 9 18 A2 2 0 1 0 5 18"),
            ["detector"] = ("M4 12 A8 8 0 1 0 20 12 A8 8 0 1 0 4 12 M12 2 V4 M12 20 V22 M2 12 H4 M20 12 H22", "M9 12 A3 3 0 1 0 15 12 A3 3 0 1 0 9 12"),
            ["help"] = ("M12 6 C10 4.5 7 4 4 5 V18 C7 17 10 17.5 12 19 C14 17.5 17 17 20 18 V5 C17 4 14 4.5 12 6 Z", "M12 6 V19"),
            ["log"] = ("M4 4 H20 V8 H4 Z M5 8 V20 H19 V8", "M10 12 H14"),
            ["plug"] = ("M9 3 V8 M15 3 V8 M12 17 V21", "M6 8 H18 V11 A6 6 0 0 1 6 11 Z"),
            ["reset"] = ("M20 12 A8 8 0 1 1 17 5.8", "M20 3 V8 H15"),
            ["clash"] = ("M3 9 H14 V20 H3 Z M10 4 H21 V15 H10 Z", "M10 9 H14 V15 H10 Z"),
        };

        /// <summary>Крупный значок 32×32.</summary>
        public static BitmapSource Large(string name, Color? accent = null) => Render(name, 32, accent ?? Accent);

        /// <summary>Мелкий значок 16×16.</summary>
        public static BitmapSource Small(string name, Color? accent = null) => Render(name, 16, accent ?? Accent);

        /// <summary>Цвет линий: тёмно-серый на светлой ленте, светло-серый на тёмной (Revit 2024).</summary>
        private static Color Body()
        {
#if REVIT2024
            try
            {
                if (Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark) return Color.FromRgb(211, 209, 199);
            }
            catch (Exception)
            {
                // тема недоступна — светлая лента
            }
#endif
            return Color.FromRgb(68, 68, 65);
        }

        private static BitmapSource Render(string name, int size, Color accent)
        {
            if (!Shapes.TryGetValue(name, out var shape)) throw new ArgumentException($"Нет значка «{name}».");
            double scale = size / 24.0;
            // Толщина линии в пикселях: ~1,5 px на мелком значке, ~2 px на крупном — как у значков Revit
            double stroke = (size <= 16 ? 1.5 : 2.0) / scale;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawGeometry(null, Pen(Body(), stroke), Geometry.Parse(shape.body));
                if (!string.IsNullOrEmpty(shape.accent)) dc.DrawGeometry(null, Pen(accent, stroke), Geometry.Parse(shape.accent));
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static Pen Pen(Color color, double thickness)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            return pen;
        }
    }
}
