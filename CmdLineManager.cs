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
        private readonly UIDocument _uiDoc;
        private readonly List<MenuItem> _connectionCommands = new List<MenuItem>();
        private readonly Button _connectionMenuButton;
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
            _uiDoc = uiDoc;
            _doc = uiDoc.Document;
            UI.AerBimAppearance.Apply(this);
            Title = "Диспетчер линий";
            Width = 1110;
            Height = 560;
            MinWidth = 640;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            foreach (var (header, property, width) in new[]
            {
                ("Система", "System", 70.0), ("Корпус", "Building", 70.0), ("Прибор", "Controller", 70.0), ("Линия", "Line", 60.0),
                ("Тип маркировки", "MarkingType", 135.0), ("Устройств", "Occupied", 90.0), ("Номера", "Numbers", 80.0), ("Пропуски / повторы", "Problems", 170.0), ("Этажи", "Floors", 0.0),
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
            if (!SystemParameters.HighContrast) _table.RowStyle = issueStyle;
            _table.SelectionChanged += (s, e) => UpdateButtons();
            LineTableSort.Attach(_table);
            _table.MouseDoubleClick += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_table, source) is DataGridRow)
                    SelectInModel();
            };

            _select = Action("Выделить в модели", SelectInModel, "Закрыть окно и выделить устройства выбранных линий");
            _delete = Action("Удалить линию", DeleteLines, "Снять маркировку со всех устройств выбранных линий (с предпросмотром)");
            UI.AerBimAppearance.Primary(this, _select);
            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(_delete, Dock.Left);
            _delete.VerticalAlignment = VerticalAlignment.Top;
            buttons.Children.Add(_delete);
            var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(Action("Обновить", Reload, "Перечитать линии из модели"));

            var menu = new ContextMenu();
            void AddConnectionCommand(string title, Action command, string hint)
            {
                var item = new MenuItem { Header = title, ToolTip = hint };
                item.Click += (sender, args) => command();
                _connectionCommands.Add(item);
                menu.Items.Add(item);
            }
            AddConnectionCommand("Порядок подключения…", () => EditConnection(false), "Проверить и сохранить порядок подключения");
            AddConnectionCommand("Добавить без перенумерации…", AddFixed, "Добавить устройство по фактическому адресу");
            AddConnectionCommand("Исключить без перенумерации…", RemoveFixed, "Исключить адрес из выбранной линии");
            AddConnectionCommand("Обновить маркировку по порядку…", () => EditConnection(true), "Проверить новые адреса и подтвердить перенумерацию");
            AddConnectionCommand("Режим «По порядку»", SwitchToSequential, "Перевести шлейф в режим последовательной маркировки");
            _connectionMenuButton = Action("Действия с АПС…", () => menu.IsOpen = true, "Операции с порядком подключения");
            menu.PlacementTarget = _connectionMenuButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            actions.Children.Add(_connectionMenuButton);

            Button close = Action("Закрыть", Close, null);
            close.IsCancel = true;
            actions.Children.Add(close);
            actions.Children.Add(_select);
            buttons.Children.Add(actions);

            _comment.SelectionChanged += (s, e) => { if (!_reloading) ShowLines(); };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var label = UI.AerBimAppearance.Label("Комментарии линий", _comment);
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
            ShowLines();
        }

        /// <summary>Линии выбранного комментария.</summary>
        private void ShowLines()
        {
            foreach (DataGridColumn c in _table.Columns) c.SortDirection = null; // новый список — в исходном порядке
            string comment = _comment.SelectedItem as string;
            List<LineInfo> lines = _lines.Where(l => CommentKey(l) == comment).ToList();
            _table.ItemsSource = lines;
            if (lines.Count == 1) _table.SelectedItem = lines[0];
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
            var line = Selected.Count == 1 ? Selected[0] : null;
            bool aps = line?.System == "АПС" && !string.IsNullOrEmpty(line.Comment);
            _connectionMenuButton.Visibility = aps ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            _connectionCommands[0].IsEnabled = aps;
            _connectionCommands[1].IsEnabled = aps && line.ByConnection;
            _connectionCommands[2].IsEnabled = aps && line.ByConnection;
            _connectionCommands[3].IsEnabled = aps && !line.ByConnection;
            _connectionCommands[4].IsEnabled = aps && line.ByConnection;
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

        internal static LineConnectionWindow.Line PrepareConnection(Document doc, string key, List<FamilyInstance> devices,
            FamilyInstance controller, string controllerName, bool ring, bool estimateFromPlan = true, bool useRouteGrouping = false)
        {
            List<FamilyInstance> confirmed = null;
            List<FamilyInstance> retained = null;
            string changeHint = null;
            if (LineConnectionOrder.Read(doc).TryGetValue(key, out var saved))
            {
                try
                {
                    confirmed = LineConnectionOrder.Ordered(devices, saved.Order);
                }
                catch (InvalidOperationException)
                {
                    var byId = devices.ToDictionary(device => device.UniqueId);
                    retained = saved.Order.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                    var kept = new HashSet<string>(retained.Select(device => device.UniqueId));
                    var added = devices.Where(device => !kept.Contains(device.UniqueId))
                        .Select(LineConnectionOrder.Address).OrderBy(address => address).ToList();
                    int removed = saved.Order.Distinct().Count(id => !byId.ContainsKey(id));
                    changeHint = $"Состав шлейфа изменился. Добавлено: {added.Count}. Удалено: {removed}.";
                    if (added.Count > 0)
                        changeHint += " Новые адреса: " + string.Join(", ", added.Take(12)) + (added.Count > 12 ? "…" : ".");
                }
                catch (ArgumentException)
                {
                    changeHint = "Адреса изменились. Проверьте порядок подключения.";
                }
            }

            var automatic = LineConnectionOrder.Suggest(doc, controller, devices, key, retained, estimateFromPlan, useRouteGrouping);
            string automaticHint = (changeHint == null ? "" : changeHint + " ") + automatic.Hint;
            return new LineConnectionWindow.Line
            {
                Key = key, Title = controllerName + " / шлейф " + key.Split('|')[3],
                Controller = controllerName, Ring = ring, Devices = devices, Suggested = confirmed ?? automatic.Devices,
                Hint = confirmed == null ? automaticHint : "Показан подтверждённый порядок подключения. Сверьте его с трассой.",
                AutomaticHint = automaticHint, Automatic = automatic, UsesAutomatic = confirmed == null,
            };
        }

        private void EditConnection(bool renumber)
        {
            WithLine(line =>
            {
                var devices = DeviceLineOperations.DevicesOf(_doc, line.Key);
                var controller = LineConnectionOrder.Controller(_doc, line.Key, line.Comment);
                var review = PrepareConnection(_doc, line.Key, devices, controller, $"{line.Building}.ARK.{line.Controller}", true,
                    estimateFromPlan: !renumber, useRouteGrouping: renumber);
                var window = new LineConnectionWindow(new[] { review }, renumber ? "Применить новые адреса" : "Сохранить «По подключению»",
                    new WindowInteropHelper(this).Handle, renumber);
                if (window.ShowDialog() != true) return;
                if (renumber) DeviceLineOperations.Renumber(_doc, line.Key, review.Order);
                else DeviceLineOperations.SaveConnectionOrder(_doc, line.Key, review.Order);
                Reload();
            });
        }

        private void SwitchToSequential()
        {
            WithLine(line =>
            {
                if (MessageBox.Show(this, "Сохранённая цепочка будет сброшена. Последующие операции в режиме «По порядку» могут сдвигать адреса. Переключить тип маркировки?",
                    line.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                DeviceLineOperations.SaveConnectionOrder(_doc, line.Key, null);
                Reload();
            });
        }

        private void AddFixed()
        {
            WithLine(line =>
            {
                var devices = ConfirmedDevices(line);
                var selection = _uiDoc.Selection.GetElementIds();
                string initialId = selection.Count == 1 ? RevitIds.IdOf(selection.First()).ToString() : "";
                string values = Prompt("Добавить без перенумерации",
                    "Введите ID нового устройства, фактический адрес и адрес предшественника через запятую. Для вставки сразу после прибора укажите 0.\nТекущий порядок: " + Addresses(devices),
                    initialId.Length == 0 ? "" : initialId + ", ");
                if (values == null) return;
                string[] parts = values.Split(',');
                if (parts.Length != 3 || !long.TryParse(parts[0], out long id) || !int.TryParse(parts[1], out int address) ||
                    !int.TryParse(parts[2], out int predecessor) || predecessor < 0)
                    throw new ArgumentException("Укажите ID, адрес устройства и адрес предшественника через запятую.");
                long? after = predecessor == 0 ? (long?)null : RevitIds.IdOf(DeviceAt(devices, predecessor).Id);
                DeviceLineOperations.Add(_doc, line.Key, after, new List<long> { id }, true, fixedAddress: address);
                var order = devices.Select(LineConnectionOrder.Address).ToList();
                order.Insert(predecessor == 0 ? 0 : order.IndexOf(predecessor) + 1, address);
                if (MessageBox.Show(this, "Порядок после добавления:\n" + string.Join(" → ", order), line.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                DeviceLineOperations.Add(_doc, line.Key, after, new List<long> { id }, false, fixedAddress: address);
                Reload();
            });
        }

        private void RemoveFixed()
        {
            WithLine(line =>
            {
                var devices = ConfirmedDevices(line);
                string values = Prompt("Исключить без перенумерации", "Введите адреса для исключения через запятую.\nТекущий порядок: " + Addresses(devices), "");
                if (values == null) return;
                var removed = new HashSet<int>();
                foreach (string part in values.Split(','))
                {
                    if (!int.TryParse(part, out int address) || !removed.Add(address))
                        throw new ArgumentException("Укажите адреса без повторов через запятую.");
                }
                var ids = removed.Select(address => RevitIds.IdOf(DeviceAt(devices, address).Id)).ToList();
                DeviceLineOperations.Remove(_doc, line.Key, ids, true, preserveAddresses: true);
                string remaining = Addresses(devices.Where(d => !removed.Contains(LineConnectionOrder.Address(d))));
                if (MessageBox.Show(this, "Порядок после исключения:\n" + (remaining.Length == 0 ? "Линия станет пустой." : remaining),
                    line.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                DeviceLineOperations.Remove(_doc, line.Key, ids, false, preserveAddresses: true);
                Reload();
            });
        }

        private List<FamilyInstance> ConfirmedDevices(LineInfo line)
        {
            if (!LineConnectionOrder.Read(_doc).TryGetValue(line.Key, out var saved))
                throw new InvalidOperationException("Сначала подтвердите порядок подключения.");
            return LineConnectionOrder.Ordered(DeviceLineOperations.DevicesOf(_doc, line.Key), saved.Order);
        }

        private static FamilyInstance DeviceAt(IEnumerable<FamilyInstance> devices, int address) =>
            devices.SingleOrDefault(d => LineConnectionOrder.Address(d) == address)
            ?? throw new ArgumentException("В линии нет адреса " + address + ".");

        private static string Addresses(IEnumerable<FamilyInstance> devices) =>
            string.Join(" → ", devices.Select(LineConnectionOrder.Address));

        private void WithLine(Action<LineInfo> action)
        {
            if (Selected.Count != 1) return;
            try { action(Selected[0]); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private string Prompt(string title, string message, string initial)
        {
            var window = new Window { Owner = this, Title = title, Width = 580,
                SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(12) };
            UI.AerBimAppearance.Apply(window);
            panel.Children.Add(new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
            var field = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(field);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var next = Action("Предпросмотр", () => window.DialogResult = true, null);
            next.IsDefault = true;
            UI.AerBimAppearance.Primary(window, next);
            buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true });
            buttons.Children.Add(next);
            panel.Children.Add(buttons);
            window.Content = panel;
            window.Loaded += (s, e) => { field.Focus(); field.CaretIndex = field.Text.Length; };
            return window.ShowDialog() == true ? field.Text : null;
        }

        private static Button Action(string text, Action action, string tip)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            button.Click += (s, e) => action();
            return button;
        }
    }
}
