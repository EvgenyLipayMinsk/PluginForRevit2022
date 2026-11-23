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
            public List<FamilyInstance> Walk;
            public List<string> Order;
        }

        internal LineConnectionWindow(IList<Line> lines, string accept, IntPtr owner, bool renumber = false)
        {
            Title = "Порядок подключения АПС";
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
                header.Children.Add(new TextBlock { Text = line.Hint, TextWrapping = TextWrapping.Wrap });
                if (!renumber)
                    header.Children.Add(new TextBlock
                    {
                        Text = "Для каждой пары КЖ рассчитает кратчайший путь по трассам шлейфа. На кольце и развилках проверьте физический ход кабеля.",
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                    });
                header.Children.Add(new TextBlock
                {
                    Text = "Устройства шлейфа (" + line.Devices.Count + ") · " + string.Join(", ", line.Devices.Select(LineConnectionOrder.Address).OrderBy(n => n)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6),
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
                var reset = new Button { Content = "Подставить обход трассы", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = line.Walk.Count > 0 };
                reset.Click += (s, e) => sequence.Text = string.Join(", ", line.Walk.Select(Token));
                header.Children.Add(reset);
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
                header.Children.Add(status);
                page.Children.Add(header);
                var preview = new ListBox();
                page.Children.Add(preview);
                tabs.Items.Add(new TabItem { Header = line.Title, Content = page });

                void Refresh()
                {
                    var rows = new List<string>();
                    try
                    {
                        line.Order = useIds ? LineConnectionOrder.ParseIds(sequence.Text, line.Devices) : LineConnectionOrder.Parse(sequence.Text, line.Devices);
                        var ordered = LineConnectionOrder.Ordered(line.Devices, line.Order, checkAddresses: !useIds);
                        if (renumber)
                        {
                            rows.AddRange(ordered.Select((d, i) =>
                                $"ID {RevitIds.IdOf(d.Id)}   {LineConnectionOrder.Address(d)} → {i + 1}"));
                        }
                        else
                        {
                            string from = line.Controller;
                            foreach (var device in ordered)
                            {
                                string to = LineConnectionOrder.Address(device).ToString();
                                rows.Add(from + " → " + to);
                                from = to;
                            }
                            if (line.Ring) rows.Add(from + " → " + line.Controller);
                        }
                        status.Text = "Все устройства указаны по одному разу.";
                    }
                    catch (ArgumentException ex)
                    {
                        line.Order = null;
                        status.Text = ex.Message;
                    }
                    preview.ItemsSource = rows;
                    apply.IsEnabled = lines.All(l => l.Order != null);
                }
                sequence.TextChanged += (s, e) => Refresh();
                Refresh();
            }
        }
    }
}
