using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using PluginForRevit2022.Core;

namespace PluginForRevit2022
{
    internal sealed class LineConnectionWindow : Window
    {
        internal sealed class Line
        {
            public string Key;
            public string Title;
            public string Controller;
            public bool Ring;
            public List<FamilyInstance> Devices;
            public List<FamilyInstance> Suggested;
            public string Hint;
            public string AutomaticHint;
            public LineConnectionOrder.Suggestion Automatic;
            public bool UsesAutomatic;
            public List<string> Order;
        }

        internal LineConnectionWindow(IList<Line> lines, string accept, IntPtr owner, bool renumber = false, string title = "Порядок подключения АПС")
        {
            UI.AerBimAppearance.Apply(this);
            Title = title;
            Width = 750;
            Height = 570;
            MinWidth = 550;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            new WindowInteropHelper(this).Owner = owner;

            var root = new DockPanel { Margin = new Thickness(12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
            var apply = new Button { Content = accept, IsDefault = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
            UI.AerBimAppearance.Primary(this, apply);
            apply.Click += (s, e) => DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            root.Children.Add(buttons);
            var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 10) };
            root.Children.Add(tabs);
            Content = root;
            foreach (var line in lines)
            {
                var page = new DockPanel { Margin = new Thickness(10) };
                var header = new StackPanel();
                DockPanel.SetDock(header, Dock.Top);
                var hint = new TextBlock { Text = line.Hint, TextWrapping = TextWrapping.Wrap };
                header.Children.Add(hint);
                if (!renumber)
                    header.Children.Add(new TextBlock
                    {
                        Text = "Длины между устройствами считаются по доступным трассам с теми же «Комментариями». Проверьте порядок на кольце и развилках.",
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                    });
                var addresses = new TextBlock
                {
                    Text = string.Join(", ", line.Devices.Select(LineConnectionOrder.Address).OrderBy(n => n)),
                    TextWrapping = TextWrapping.Wrap,
                };
                header.Children.Add(new Expander
                {
                    Header = $"Устройства шлейфа ({line.Devices.Count})",
                    Content = new ScrollViewer { Content = addresses, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    Margin = new Thickness(0, 6, 0, 6),
                });
                bool useIds = renumber && (line.Devices.Any(d => LineConnectionOrder.Address(d) <= 0) ||
                    line.Devices.GroupBy(LineConnectionOrder.Address).Any(g => g.Count() > 1));
                string Token(FamilyInstance device) => useIds ? RevitIds.IdOf(device.Id).ToString() : LineConnectionOrder.Address(device).ToString();
                header.Children.Add(new TextBlock
                {
                    Text = useIds ? "В линии повторяются или отсутствуют адреса. Введите порядок по ID устройств." :
                        "Порядок от прибора. Адреса через запятую или с новой строки.",
                    TextWrapping = TextWrapping.Wrap,
                });
                if (useIds)
                {
                    var devices = new ListBox { MaxHeight = 95, ItemsSource = line.Devices.Select(d => $"ID {Token(d)} · адрес {LineConnectionOrder.Address(d)} · {d.Name}").ToList() };
                    header.Children.Add(devices);
                }
                var sequence = new TextBox
                {
                    Text = string.Join(", ", line.Suggested.Select(Token)),
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MinHeight = 48, MaxHeight = 100, Margin = new Thickness(0, 4, 0, 4),
                };
                header.Children.Add(sequence);
                bool showEstimated = line.UsesAutomatic;
                var reset = new Button
                {
                    Content = line.Automatic.Estimated.Count > 0 ? "Подставить черновик по плану" : "Подставить предложенный порядок",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsEnabled = line.Automatic.Devices.Count > 0,
                };
                header.Children.Add(reset);
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
                header.Children.Add(new ScrollViewer
                {
                    Content = status, MaxHeight = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(0, 4, 0, 8),
                });
                page.Children.Add(header);
                var previewPanel = new DockPanel();
                var previewTitle = new TextBlock
                {
                    Text = renumber ? "Предпросмотр новых адресов" : "Предпросмотр соединений",
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
                };
                DockPanel.SetDock(previewTitle, Dock.Top);
                previewPanel.Children.Add(previewTitle);
                var preview = new ListBox();
                previewPanel.Children.Add(preview);
                page.Children.Add(previewPanel);
                tabs.Items.Add(new TabItem { Header = line.Title, Content = page });

                void Refresh()
                {
                    var rows = new List<string>();
                    string Mark(FamilyInstance device)
                    {
                        if (!showEstimated || !line.Automatic.Estimated.Contains(device.UniqueId)) return "";
                        return line.Automatic.Review.Contains(device.UniqueId) ? "   ? Проверьте соединение" : "   ≈ По плану";
                    }
                    try
                    {
                        line.Order = useIds ? LineConnectionOrder.ParseIds(sequence.Text, line.Devices) : LineConnectionOrder.Parse(sequence.Text, line.Devices);
                        var ordered = LineConnectionOrder.Ordered(line.Devices, line.Order, checkAddresses: !useIds);
                        if (renumber)
                        {
                            rows.AddRange(ordered.Select((d, i) =>
                                $"ID {RevitIds.IdOf(d.Id)}   {LineConnectionOrder.Address(d)} → {i + 1}{Mark(d)}"));
                        }
                        else
                        {
                            string from = line.Controller;
                            foreach (var device in ordered)
                            {
                                string to = LineConnectionOrder.Address(device).ToString();
                                rows.Add(from + " → " + to + Mark(device));
                                from = to;
                            }
                            if (line.Ring)
                            {
                                string verifyReturn = showEstimated && line.Automatic.Estimated.Count > 0 ? "   ? Проверьте возврат к прибору" : "";
                                rows.Add(from + " → " + line.Controller + verifyReturn);
                            }
                        }
                        status.Text = "Все устройства указаны по одному разу.";
                    }
                    catch (ArgumentException ex)
                    {
                        line.Order = null;
                        status.Text = ex.Message;
                    }
                    previewPanel.Visibility = rows.Count == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    preview.ItemsSource = rows;
                    apply.IsEnabled = lines.All(l => l.Order != null);
                }
                reset.Click += (s, e) =>
                {
                    showEstimated = true;
                    hint.Text = line.AutomaticHint;
                    sequence.Text = string.Join(", ", line.Automatic.Devices.Select(Token));
                    Refresh();
                };
                sequence.TextChanged += (s, e) => Refresh();
                Refresh();
            }
        }
    }
}
