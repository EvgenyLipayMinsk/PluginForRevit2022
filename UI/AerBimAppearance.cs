using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PluginForRevit2022.UI
{
    internal static class AerBimAppearance
    {
        public static readonly Color AccentColor = Color.FromRgb(24, 95, 165);
        public static readonly Brush AccentBrush = FrozenBrush(AccentColor);
        private const string PrimaryButtonKey = "AerBim.PrimaryButton";

        public static void Apply(Window window)
        {
            window.FontSize = 13;
            window.SetResourceReference(Control.BackgroundProperty, SystemColors.WindowBrushKey);
            window.SetResourceReference(Control.ForegroundProperty, SystemColors.WindowTextBrushKey);

            var buttons = new Style(typeof(Button));
            buttons.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)));
            buttons.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28.0));
            var primary = new Style(typeof(Button), buttons);
            primary.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            if (!SystemParameters.HighContrast)
            {
                primary.Setters.Add(new Setter(Control.BackgroundProperty, AccentBrush));
                primary.Setters.Add(new Setter(Control.BorderBrushProperty, AccentBrush));
                primary.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
                primary.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
                primary.Setters.Add(new Setter(Control.TemplateProperty, PrimaryTemplate()));
            }

            var fields = new Style(typeof(TextBox));
            fields.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 3, 4, 3)));
            var tables = new Style(typeof(DataGrid));
            tables.Setters.Add(new Setter(DataGrid.RowHeaderWidthProperty, 0.0));
            tables.Setters.Add(new Setter(DataGrid.GridLinesVisibilityProperty, DataGridGridLinesVisibility.Horizontal));
            tables.Setters.Add(new Setter(DataGrid.HorizontalGridLinesBrushProperty, new DynamicResourceExtension(SystemColors.ControlLightBrushKey)));

            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                { typeof(Button), buttons },
                { typeof(TextBox), fields },
                { typeof(DataGrid), tables },
                { PrimaryButtonKey, primary },
                { "AerBim.SectionForeground", SystemParameters.HighContrast ? SystemColors.WindowTextBrush : AccentBrush },
            });
        }

        public static void Primary(Window window, Button button) =>
            button.Style = (Style)window.FindResource(PrimaryButtonKey);

        public static Label Label(string text, Control target, bool wrap = true)
        {
            var caption = new Label
            {
                Content = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap },
                Target = target,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(caption, text);
            System.Windows.Automation.AutomationProperties.SetLabeledBy(target, caption);
            return caption;
        }

        private static ControlTemplate PrimaryTemplate()
        {
            var frame = new FrameworkElementFactory(typeof(Border), "Frame");
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
            AddFrameTrigger(Control.IsMouseOverProperty, FrozenBrush(Color.FromRgb(19, 78, 137)));
            AddFrameTrigger(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, FrozenBrush(Color.FromRgb(15, 65, 112)));
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Frame"));
            template.Triggers.Add(focused);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(SystemColors.ControlBrushKey), "Frame"));
            disabled.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(SystemColors.ControlDarkBrushKey), "Frame"));
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.GrayTextBrushKey)));
            template.Triggers.Add(disabled);
            return template;

            void AddFrameTrigger(DependencyProperty property, Brush brush)
            {
                var trigger = new Trigger { Property = property, Value = true };
                trigger.Setters.Add(new Setter(Border.BackgroundProperty, brush, "Frame"));
                template.Triggers.Add(trigger);
            }
        }

        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
