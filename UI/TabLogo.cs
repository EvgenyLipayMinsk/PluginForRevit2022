using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace PluginForRevit2022.UI
{
    /// <summary>
    /// Логотип перед названием вкладки «AerBIM Manager» на ленте. API Revit картинку у вкладки не задаёт,
    /// поэтому в заголовок вкладки (TextBlock ленты AdWindows) вставляется значок. Лента может перестроиться
    /// (тема, рабочее пространство) — тогда значок ставится заново при следующей смене вида.
    /// </summary>
    internal static class TabLogo
    {
        private static string _title;
        private static TextBlock _header;

        public static void Attach(UIControlledApplication app, string tabTitle)
        {
            _title = tabTitle;
            app.Idling += OnIdling;
            app.ViewActivated += (s, e) => Safe(() => Apply());
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            bool done = false;
            Safe(() => done = Apply());
            if (done && sender is UIApplication ui) ui.Idling -= OnIdling;
        }

        private static void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { Core.PluginLog.Warn("ribbon.logo", ex.Message); }
        }

        /// <summary>true — значок на месте.</summary>
        private static bool Apply()
        {
            if (_header != null && _header.IsLoaded && _header.Inlines.OfType<InlineUIContainer>().Any()) return true;
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
            if (ribbon == null) return false;
            TextBlock header = Find(ribbon);
            if (header == null) return false;

            var image = new Image { Source = Logo(), Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            header.Inlines.Clear();
            header.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
            header.Inlines.Add(new Run(_title));
            _header = header;
            return true;
        }

        private static TextBlock Find(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock text && text.Text == _title) return text;
                TextBlock found = Find(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Фирменный знак AerBIM (треугольник из логотипа), встроен в сборку; без ресурса — рисованная «A».</summary>
        private static ImageSource Logo()
        {
            using (var stream = typeof(TabLogo).Assembly.GetManifestResourceStream("PluginForRevit2022.aerbim-logo.png"))
            {
                if (stream != null)
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            Brush accent = AerBimAppearance.AccentBrush;
            var white = new Pen(Brushes.White, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(accent, null, new RectangleGeometry(new Rect(0, 0, 24, 24), 5, 5)));
            group.Children.Add(new GeometryDrawing(null, white, Geometry.Parse("M6.5 18.5 L12 5.5 L17.5 18.5 M8.7 13.5 H15.3")));
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
    }
}
