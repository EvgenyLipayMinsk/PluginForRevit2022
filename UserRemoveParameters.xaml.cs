using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Binding = System.Windows.Data.Binding;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace PluginForRevit2022
{
    /// <summary>Строка таблицы параметров. Публичный класс: на него привязана таблица окна.</summary>
    public sealed class ParameterRow : INotifyPropertyChanged
    {
        private bool _selected;
        internal ElementId Id;
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Categories { get; set; }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Удаление параметров проекта: поиск, отметка галочками, предпросмотр списка перед удалением.
    /// Удаляется сам параметр и все его значения во всех элементах; отменить можно через Ctrl+Z.
    /// </summary>
    public partial class UserRemoveParameters : Window
    {
        private readonly Document _doc;
        private readonly List<ParameterRow> _rows;
        private readonly ICollectionView _view;
        private readonly TextBox _search = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
        private readonly TextBlock _searchHint = new TextBlock
        {
            Text = "Поиск по имени…", Foreground = Brushes.Gray, IsHitTestVisible = false, Margin = new Thickness(8, 4, 0, 0),
        };
        private readonly System.Windows.Controls.ComboBox _kind = new System.Windows.Controls.ComboBox { Width = 170, Margin = new Thickness(8, 0, 0, 0) };
        private readonly DataGrid _table = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 230)),
            RowHeaderWidth = 0,
            Margin = new Thickness(0, 8, 0, 0),
        };
        private readonly TextBlock _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
        private readonly Button _delete;

        public UserRemoveParameters(List<Element> elements, Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _rows = elements.OfType<ParameterElement>().Select(RowOf)
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (ParameterRow r in _rows) r.PropertyChanged += (s, e) => UpdateCounter();

            // Таблица: галочка, имя, вид, категории привязки
            // Галочка срабатывает с первого клика (у DataGridCheckBoxColumn первый клик только выделяет строку)
            var box = new FrameworkElementFactory(typeof(CheckBox));
            box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(nameof(ParameterRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            box.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            box.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            _table.Columns.Add(new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = box }, Width = 36 });
            _table.Columns.Add(new DataGridTextColumn { Header = "Параметр", Binding = new Binding(nameof(ParameterRow.Name)), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            _table.Columns.Add(new DataGridTextColumn { Header = "Вид", Binding = new Binding(nameof(ParameterRow.Kind)), IsReadOnly = true, Width = 140 });
            _table.Columns.Add(new DataGridTextColumn { Header = "Категории", Binding = new Binding(nameof(ParameterRow.Categories)), IsReadOnly = true, Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = o => Visible((ParameterRow)o);
            _table.ItemsSource = _view;
            // Пробел отмечает выделенные строки
            _table.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Space || _table.SelectedItems.Count == 0) return;
                List<ParameterRow> picked = _table.SelectedItems.OfType<ParameterRow>().ToList();
                bool value = !picked.All(r => r.Selected);
                foreach (ParameterRow r in picked) r.Selected = value;
                e.Handled = true;
            };

            // Верх: поиск и вид параметра
            var searchBox = new Grid();
            searchBox.Children.Add(_search);
            searchBox.Children.Add(_searchHint);
            _search.TextChanged += (s, e) =>
            {
                _searchHint.Visibility = _search.Text.Length == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                _view.Refresh();
            };
            foreach (string k in new[] { "Все виды" }.Concat(_rows.Select(r => r.Kind).Distinct().OrderBy(k => k))) _kind.Items.Add(k);
            _kind.SelectedIndex = 0;
            _kind.SelectionChanged += (s, e) => _view.Refresh();
            var top = new DockPanel();
            DockPanel.SetDock(_kind, Dock.Right);
            top.Children.Add(_kind);
            top.Children.Add(searchBox);

            var intro = new TextBlock
            {
                Text = "Отметьте параметры, которые нужно удалить из проекта. Вместе с параметром удаляются его значения во всех элементах.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };

            // Низ: выбор, счётчик, действия
            _delete = Btn("Удалить", DeleteSelected);
            _delete.IsDefault = false;
            Button close = Btn("Закрыть", Close);
            close.IsCancel = true;
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(Btn("Отметить найденные", () => SetVisible(true), new Thickness(0, 0, 8, 0)));
            left.Children.Add(Btn("Снять отметки", () => { foreach (ParameterRow r in _rows) r.Selected = false; }, new Thickness(0, 0, 12, 0)));
            left.Children.Add(_counter);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(_delete);
            right.Children.Add(close);
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            bottom.Children.Add(right);
            bottom.Children.Add(left);

            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(top, 1);
            Grid.SetRow(_table, 2);
            Grid.SetRow(bottom, 3);
            layout.Children.Add(intro);
            layout.Children.Add(top);
            layout.Children.Add(_table);
            layout.Children.Add(bottom);
            Content = layout;

            UpdateCounter();
            Loaded += (s, e) => _search.Focus();
        }

        private ParameterRow RowOf(ParameterElement p)
        {
            string kind = p is GlobalParameter ? "Глобальный" : p is SharedParameterElement ? "Общий" : "Параметр проекта";
            string categories = "—";
            try
            {
                if (!(p is GlobalParameter) && _doc.ParameterBindings.get_Item(p.GetDefinition()) is ElementBinding binding && binding.Categories != null)
                {
                    List<string> names = binding.Categories.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList();
                    if (names.Count > 0) categories = string.Join(", ", names);
                }
            }
            catch (Exception) { /* параметр без привязки — категорий нет */ }
            return new ParameterRow { Id = p.Id, Name = p.Name, Kind = kind, Categories = categories };
        }

        private bool Visible(ParameterRow r)
        {
            string text = _search.Text.Trim();
            bool byText = text.Length == 0 || r.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0
                          || r.Categories.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
            bool byKind = _kind.SelectedIndex <= 0 || r.Kind == (string)_kind.SelectedItem;
            return byText && byKind;
        }

        private void SetVisible(bool value)
        {
            foreach (ParameterRow r in _view.Cast<ParameterRow>()) r.Selected = value;
        }

        private void UpdateCounter()
        {
            int selected = _rows.Count(r => r.Selected);
            _counter.Text = $"Отмечено: {selected} из {_rows.Count}";
            if (_delete != null)
            {
                _delete.IsEnabled = selected > 0;
                _delete.Content = selected > 0 ? $"Удалить ({selected})" : "Удалить";
            }
        }

        private void DeleteSelected()
        {
            _table.CommitEdit(DataGridEditingUnit.Row, true);
            List<ParameterRow> picked = _rows.Where(r => r.Selected).ToList();
            if (picked.Count == 0) return;
            string names = string.Join("\n", picked.Take(15).Select(r => "• " + r.Name)) + (picked.Count > 15 ? $"\n… и ещё {picked.Count - 15}" : "");

            // Пробное удаление: сколько ещё элементов Revit удалит вместе с параметрами (правила фильтров, поля и т.п.)
            int extra = 0;
            try
            {
                using (var probe = new Transaction(_doc, "AerBIM: проверка удаления параметров"))
                {
                    probe.Start();
                    extra = Math.Max(0, _doc.Delete(picked.Select(r => r.Id).ToList()).Count - picked.Count);
                    probe.RollBack();
                }
            }
            catch (Exception) { /* проверка не удалась — предупредим при самом удалении */ }

            // Параметры, на которых держатся маркировка и кабельный журнал
            List<string> plugin = picked.Where(r => r.Name.StartsWith("ARBM_", StringComparison.OrdinalIgnoreCase) || r.Name.StartsWith("ADSK_", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name).ToList();
            string warnings = (extra > 0 ? $"\n\nВместе с ними Revit удалит связанных элементов: {extra} (например правила фильтров видов)." : "")
                + (plugin.Count > 0 ? $"\n\nВнимание: {string.Join(", ", plugin.Take(6))}{(plugin.Count > 6 ? " …" : "")} нужны маркировке и кабельному журналу — без них они работать не будут." : "");
            if (MessageBox.Show(this, $"Удалить параметры: {picked.Count}?\n\n{names}{warnings}\n\nЗначения этих параметров во всех элементах будут потеряны. Отменить можно через Ctrl+Z в Revit.",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                using (var t = new Transaction(_doc, "AerBIM: удалить параметры"))
                {
                    t.Start();
                    _doc.Delete(picked.Select(r => r.Id).ToList());
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не применил удаление.");
                }
                Core.PluginLog.Info("parameters.remove", string.Join("; ", picked.Select(r => r.Name)));
                foreach (ParameterRow r in picked) _rows.Remove(r);
                _view.Refresh();
                UpdateCounter();
                MessageBox.Show(this, $"Удалено параметров: {picked.Count}.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось удалить: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static Button Btn(string text, Action action, Thickness? margin = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = margin ?? new Thickness(8, 0, 0, 0), MinWidth = 90 };
            b.Click += (s, e) => action();
            return b;
        }
    }
}
