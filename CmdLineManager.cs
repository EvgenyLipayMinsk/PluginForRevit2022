using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Core;
using Grid = System.Windows.Controls.Grid;

namespace PluginForRevit2022
{
    /// <summary>Диспетчер линий: размеченные линии проекта, выделение в модели и удаление маркировки линии.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdLineManager : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            try
            {
                var window = new LineManagerWindow(uiDoc);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                window.ShowDialog();
                // Выделение — после закрытия окна: модальное окно закрывает модель
                if (window.ToSelect != null && window.ToSelect.Count > 0)
                {
                    List<ElementId> ids = window.ToSelect.Select(RevitIds.ToElementId).ToList();
                    uiDoc.Selection.SetElementIds(ids);
                    uiDoc.ShowElements(ids);
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Диспетчер линий", ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)] public class GuardLineManager : GuardedCommand<CmdLineManager> { }

    internal sealed class LineManagerWindow : Window
    {
        private readonly Document _doc;
        private readonly DataGrid _table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false,
        };
        private readonly TextBlock _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        // Линии с разными «Комментарии» (системами) в одной таблице не смешиваются — показывается один комментарий
        private readonly System.Windows.Controls.ComboBox _comment = new System.Windows.Controls.ComboBox { MinWidth = 140, Margin = new Thickness(6, 0, 12, 0) };
        private bool _reloading;
        private readonly Button _select;
        private readonly Button _delete;

        /// <summary>ID устройств, которые выделить в модели после закрытия окна.</summary>
        public List<long> ToSelect { get; private set; }

        public LineManagerWindow(UIDocument uiDoc)
        {
            _doc = uiDoc.Document;
            Title = "Диспетчер линий";
            Width = 900;
            Height = 560;
            MinWidth = 640;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            foreach (var (header, property, width) in new[]
            {
                ("Система", "System", 70.0), ("Корпус", "Building", 70.0), ("Прибор", "Controller", 70.0), ("Линия", "Line", 60.0),
                ("Комментарии", "Comment", 90.0), ("Устройств", "Occupied", 90.0), ("Номера", "Numbers", 80.0), ("Пропуски / повторы", "Problems", 170.0), ("Этажи", "Floors", 0.0),
            })
            {
                // Сортировка по клику на заголовок — числом
                string sort = property == "Controller" ? "ControllerOrder" : property == "Line" ? "LineOrder" : property == "Occupied" ? "Devices" : property;
                _table.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new System.Windows.Data.Binding(property),
                    SortMemberPath = sort,
                    Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
                });
            }
            // Линии с пропусками, повторами или переполнением — подсвечены
            var issueStyle = new Style(typeof(DataGridRow));
            var trigger = new DataTrigger { Binding = new System.Windows.Data.Binding("HasIssues"), Value = true };
            trigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 238, 218))));
            issueStyle.Triggers.Add(trigger);
            _table.RowStyle = issueStyle;
            _table.SelectionChanged += (s, e) => UpdateButtons();
            LineTableSort.Attach(_table);
            _table.MouseDoubleClick += (s, e) => SelectInModel();

            _select = Action("Выделить в модели", SelectInModel, "Закрыть окно и выделить устройства выбранных линий");
            _delete = Action("Удалить линию", DeleteLines, "Снять маркировку со всех устройств выбранных линий (с предпросмотром)");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(Action("Обновить", Reload, "Перечитать линии из модели"));
            buttons.Children.Add(_select);
            buttons.Children.Add(_delete);
            Button close = Action("Закрыть", Close, null);
            close.IsCancel = true;
            buttons.Children.Add(close);

            _comment.SelectionChanged += (s, e) => { if (!_reloading) Show(); };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var label = new TextBlock { Text = "Комментарии:", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(_comment, Dock.Left);
            top.Children.Add(label);
            top.Children.Add(_comment);
            top.Children.Add(_summary);

            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_table, 1);
            Grid.SetRow(buttons, 2);
            layout.Children.Add(top);
            layout.Children.Add(_table);
            layout.Children.Add(buttons);
            Content = layout;
            Reload();
        }

        private List<LineInfo> Selected => _table.SelectedItems.OfType<LineInfo>().ToList();

        private const string NoComment = "Без комментария";
        private static string CommentKey(LineInfo l) => string.IsNullOrWhiteSpace(l.Comment) ? NoComment : l.Comment.Trim().ToUpperInvariant();
        private List<LineInfo> _lines = new List<LineInfo>();

        private void Reload()
        {
            _lines = DeviceLineOperations.ListLines(_doc, true); // с ручной разметкой АПС без комментария — пункт «Без комментария»
            // Список комментариев: самый частый первым; выбранный сохраняется при обновлении
            string current = _comment.SelectedItem as string;
            List<string> comments = _lines.GroupBy(CommentKey).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();
            _reloading = true;
            _comment.ItemsSource = comments;
            _comment.SelectedItem = current != null && comments.Contains(current) ? current : comments.FirstOrDefault();
            _comment.IsEnabled = comments.Count > 1;
            _reloading = false;
            Show();
        }

        /// <summary>Линии выбранного комментария.</summary>
        private void Show()
        {
            foreach (DataGridColumn c in _table.Columns) c.SortDirection = null; // новый список — в исходном порядке
            string comment = _comment.SelectedItem as string;
            List<LineInfo> lines = _lines.Where(l => CommentKey(l) == comment).ToList();
            _table.ItemsSource = lines;
            if (_lines.Count == 0)
            {
                _summary.Text = "В проекте нет размеченных линий.";
            }
            else
            {
                string bySystem = string.Join("; ", lines.GroupBy(l => l.System)
                    .Select(g => $"{g.Key}: линий {g.Count()}, устройств {g.Sum(l => l.Devices)}"));
                int issues = lines.Count(l => l.HasIssues);
                int other = _lines.Count - lines.Count;
                _summary.Text = bySystem + (issues > 0 ? $". С пропусками, повторами или сверх ёмкости ({DeviceMarkingOperations.LineCapacity}): {issues} — подсвечены." : ".")
                                + (other > 0 ? $" Линий с другими комментариями: {other}." : "");
            }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool any = _table.SelectedItems.Count > 0;
            _select.IsEnabled = any;
            _delete.IsEnabled = any;
            _delete.Content = _table.SelectedItems.Count > 1 ? $"Удалить линии ({_table.SelectedItems.Count})" : "Удалить линию";
        }

        private void SelectInModel()
        {
            List<LineInfo> lines = Selected;
            if (lines.Count == 0) return;
            ToSelect = DeviceLineOperations.DeviceIdsOfLines(_doc, lines.Select(l => l.Key));
            Close();
        }

        private void DeleteLines()
        {
            List<LineInfo> lines = Selected;
            if (lines.Count == 0) return;
            List<string> keys = lines.Select(l => l.Key).ToList();
            try
            {
                Dictionary<string, object> preview = DeviceLineOperations.RemoveLines(_doc, keys, true);
                string names = string.Join("\n", lines.Take(10).Select(l => "• " + l.Title)) + (lines.Count > 10 ? $"\n… и ещё {lines.Count - 10}" : "");
                string text = $"{names}\n\nМаркировка будет снята с устройств: {preview["removed"]}.\n" +
                              "Очищаются марка, адрес, номер линии и прибора, БЦО, корпус и «Комментарии» (устройства выйдут из кабельного журнала). " +
                              "Этаж и ЗКПС остаются, сами элементы не удаляются.\n\nУдалить?";
                if (MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                Dictionary<string, object> result = DeviceLineOperations.RemoveLines(_doc, keys, false);
                PluginLog.Info("lines.remove", string.Join("; ", keys) + " | " + result["summary"]);
                Reload();
                MessageBox.Show(this, (string)result["summary"], Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static Button Action(string text, Action action, string tip)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            button.Click += (s, e) => action();
            return button;
        }
    }
}
