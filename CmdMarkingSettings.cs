using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using PluginForRevit2022.Core;
using Binding = System.Windows.Data.Binding;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace PluginForRevit2022
{
    /// <summary>Профили настроек маркировки: БЦО по семействам и участие в линии КЖ.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdMarkingSettings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var window = new MarkingSettingsWindow(commandData.Application.ActiveUIDocument.Document);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                window.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Настройки маркировки", ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)] public class GuardMarkingSettings : GuardedCommand<CmdMarkingSettings> { }

    internal sealed class MarkingSettingsWindow : Window
    {
        private const string FileFilter = "Профиль маркировки (*.json)|*.json";

        private readonly Document _doc;
        private readonly ComboBox _profiles = new ComboBox { MinWidth = 260, Margin = new Thickness(8, 0, 0, 0) };
        private readonly DataGrid _table = FamilyTable();
        private readonly TextBox _apsPercent = Number();
        private readonly TextBox _securityPercent = Number();
        private readonly TextBox _souyePercent = Number();
        private readonly TextBox _lineReserve = Number();
        private readonly ComboBox _floorParameter = new ComboBox { MinWidth = 160, IsEditable = true, Margin = new Thickness(6, 0, 0, 0) };
        private readonly TextBlock _other = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkGoldenrod, Margin = new Thickness(0, 4, 0, 0) };
        private List<FamilySetting> _rows;
        private bool _reloading;

        public MarkingSettingsWindow(Document doc)
        {
            _doc = doc;
            Title = "Настройки маркировки";
            Width = 940;
            MinWidth = 700;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _table.Columns.Add(FlagColumn("АПС", nameof(FamilySetting.Aps)));
            _table.Columns.Add(FlagColumn("СОТС", nameof(FamilySetting.Security)));
            _table.Columns.Add(FlagColumn("СОУЭ", nameof(FamilySetting.Souye)));
            _table.Columns.Add(FamilyNameColumn());
            _table.Columns.Add(new DataGridTextColumn { Header = "БЦО", Binding = new System.Windows.Data.Binding("Bco") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 120 });

            var bar = new WrapPanel(); // узкое окно — кнопки переносятся, а не обрезаются
            bar.Children.Add(new TextBlock { Text = "Профиль", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_profiles);
            bar.Children.Add(Tool("Добавить профиль", "Создать новый профиль", NewProfile));
            bar.Children.Add(Tool("Сохранить", "Сохранить текущий профиль", SaveProfile));
            bar.Children.Add(Tool("Удалить", "Удалить текущий профиль", DeleteProfile));
            bar.Children.Add(Tool("Открыть файл…", "Подключить файл профиля, полученный от коллеги", OpenProfile));
            bar.Children.Add(Tool("Обновить список", "Перечитать семейства проекта (например, после загрузки нового семейства)", Refresh));
            bar.Children.Add(Tool("Маркировка ЗКПС", "Выбрать семейства АПС для заполнения ARBM_ЗКПС", EditZkps));
            _profiles.SelectionChanged += (s, e) => SwitchProfile();

            var groupActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            groupActions.Children.Add(ClearFlagsButton(_table, false));
            groupActions.Children.Add(new TextBlock
            {
                Text = "Выделяйте семейства по названию с Ctrl или Shift.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });

            var reserves = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            reserves.Children.Add(new TextBlock { Text = "Запас кабеля, %:", VerticalAlignment = VerticalAlignment.Center });
            foreach (var (label, box) in new[] { ("АПС", _apsPercent), ("СОТС", _securityPercent), ("СОУЭ", _souyePercent) })
            {
                reserves.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
                reserves.Children.Add(box);
            }
            reserves.Children.Add(new TextBlock { Text = "Запас на каждую линию, м", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 4, 0) });
            reserves.Children.Add(_lineReserve);

            var floor = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            floor.Children.Add(new TextBlock { Text = "Параметр этажа:", VerticalAlignment = VerticalAlignment.Center });
            _floorParameter.ToolTip = "Список — параметры проекта, в имени которых есть «этаж». Можно вписать имя вручную.";
            floor.Children.Add(_floorParameter);
            floor.Children.Add(new TextBlock { Text = "для маркировки и КЖ АПС, СОТС и СОУЭ", Foreground = System.Windows.Media.Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });

            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(bar);
            top.Children.Add(reserves);
            top.Children.Add(floor);
            top.Children.Add(new TextBlock { Text = "В линии каждой системы участвуют только семейства с её галочкой. Без БЦО семейство не маркируется: маркировка остановится и покажет такие семейства. Чтобы передать настройки, отправьте файл профиля.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            top.Children.Add(_other);
            top.Children.Add(groupActions);

            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(_table, 1);
            layout.Children.Add(top);
            layout.Children.Add(_table);
            Content = layout;
            Reload();
        }

        private static DataGrid FamilyTable() => new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
        };

        private static DataGridTextColumn FamilyNameColumn() => new DataGridTextColumn
        {
            Header = "Семейство",
            Binding = new Binding(nameof(FamilySetting.Name)),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        };

        private static DataGridTemplateColumn FlagColumn(string title, string property)
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            checkBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, title);
            checkBox.SetBinding(CheckBox.IsCheckedProperty, new Binding(property)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return new DataGridTemplateColumn
            {
                Header = title,
                IsReadOnly = true,
                SortMemberPath = property,
                CellTemplate = new DataTemplate { VisualTree = checkBox },
            };
        }

        private static Button ClearFlagsButton(DataGrid table, bool zones)
        {
            var button = new Button
            {
                Content = "Снять галочки у выделенных",
                Padding = new Thickness(8, 2, 8, 2),
                IsEnabled = false,
            };
            table.SelectionChanged += (s, e) => button.IsEnabled = table.SelectedItems.Count > 0;
            if (zones)
            {
                button.Click += (s, e) => ClearFlags(table, row => row.ZkpsOn = false);
                return button;
            }

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (var (label, clear) in new (string, Action<FamilySetting>)[]
            {
                ("АПС", row => row.Aps = false),
                ("СОТС", row => row.Security = false),
                ("СОУЭ", row => row.Souye = false),
            })
            {
                var item = new MenuItem { Header = label };
                item.Click += (s, e) => ClearFlags(table, clear);
                menu.Items.Add(item);
            }
            button.ContextMenu = menu;
            button.Click += (s, e) => menu.IsOpen = true;
            return button;
        }

        private static void ClearFlags(DataGrid table, Action<FamilySetting> clear)
        {
            if (!table.CommitEdit(DataGridEditingUnit.Row, true)) return;
            foreach (FamilySetting row in table.SelectedItems.OfType<FamilySetting>().ToList()) clear(row);
        }

        private void EditZkps()
        {
            if (!_table.CommitEdit(DataGridEditingUnit.Row, true)) return;
            const string title = "Маркировка ЗКПС";
            List<FamilySetting> families = _rows.Where(row => row.Aps).ToList();
            if (families.Count == 0)
            {
                MessageBox.Show(this, "Отметьте семейства в колонке «АПС».", title);
                return;
            }

            var edits = families.Select(row => new FamilySetting { Name = row.Name, Aps = true, Zkps = row.Zkps }).ToList();
            DataGrid table = FamilyTable();
            table.Columns.Add(FlagColumn("Заполнять ЗКПС", nameof(FamilySetting.ZkpsOn)));
            table.Columns.Add(FamilyNameColumn());
            table.ItemsSource = edits;

            var window = new Window
            {
                Owner = this,
                Title = title,
                Width = 620,
                Height = 470,
                MinWidth = 440,
                MinHeight = 280,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var apply = new Button { Content = "Применить", IsDefault = true, MinWidth = 95, Margin = new Thickness(8, 0, 0, 0) };
            apply.Click += (s, e) => window.DialogResult = true;
            var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(cancel);
            actions.Children.Add(apply);
            var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var clear = ClearFlagsButton(table, true);
            DockPanel.SetDock(clear, Dock.Left);
            bar.Children.Add(clear);
            bar.Children.Add(actions);
            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(table);
            window.Content = root;

            if (window.ShowDialog() != true) return;
            for (int i = 0; i < families.Count; i++) families[i].Zkps = edits[i].Zkps;
        }

        private Button Tool(string text, string tip, Action action)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(6, 0, 0, 4), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += (s, e) => Guarded(action);
            return button;
        }

        private void Reload()
        {
            _reloading = true;
            _profiles.ItemsSource = MarkingSettings.Profiles().Select(p => p.Name).ToList();
            _profiles.SelectedItem = MarkingSettings.Current().Name;
            JournalSettings journal = MarkingSettings.ReadAccount().Journal;
            _apsPercent.Text = Text(journal.ApsPercent);
            _securityPercent.Text = Text(journal.SecurityPercent);
            _souyePercent.Text = Text(journal.SouyePercent);
            _lineReserve.Text = Text(journal.LineReserveM);
            _floorParameter.ItemsSource = MarkingSettings.FloorParameters(_doc);
            _floorParameter.Text = string.IsNullOrWhiteSpace(journal.FloorParameter) ? MarkingSettings.DefaultFloorParameter : journal.FloorParameter;
            _rows = MarkingSettings.ForDocument(_doc);
            _table.ItemsSource = _rows;
            List<string> other = MarkingSettings.OtherCategoryFamilies(_doc);
            _other.Text = other.Count == 0 ? ""
                : $"Не показаны семейства других категорий (маркируются только «Пожарная сигнализация», «Данные» и «Вызов медперсонала»): {string.Join(", ", other.Take(8))}" + (other.Count > 8 ? $" и ещё {other.Count - 8}" : "") + ".";
            _other.Visibility = other.Count == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            _reloading = false;
        }

        /// <summary>Перечитать семейства проекта, не теряя правок в таблице.</summary>
        private void Refresh()
        {
            Store();
            Reload();
        }

        private void Store()
        {
            _table.CommitEdit(DataGridEditingUnit.Row, true);
            MarkingSettings.Save(_rows, new JournalSettings
            {
                ApsPercent = Read(_apsPercent),
                SecurityPercent = Read(_securityPercent),
                SouyePercent = Read(_souyePercent),
                LineReserveM = Read(_lineReserve),
                FloorParameter = string.IsNullOrWhiteSpace(_floorParameter.Text) ? MarkingSettings.DefaultFloorParameter : _floorParameter.Text.Trim(),
            });
        }

        private void SaveProfile()
        {
            Store();
            MessageBox.Show(this, $"Профиль «{MarkingSettings.Current().Name}» сохранён.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SwitchProfile()
        {
            if (_reloading || !(_profiles.SelectedItem is string name)) return;
            Guarded(() =>
            {
                Store(); // правки не теряются при переключении
                MarkingSettings.Select(name);
                Reload();
            });
        }

        private void NewProfile()
        {
            var dialog = new SaveFileDialog { Filter = FileFilter, FileName = "Профиль.json", Title = "Имя и расположение нового профиля" };
            if (dialog.ShowDialog(this) != true) return;
            Store();
            MarkingSettings.Create(dialog.FileName);
            Reload();
        }

        private void OpenProfile()
        {
            var dialog = new OpenFileDialog { Filter = FileFilter };
            if (dialog.ShowDialog(this) != true) return;
            Store();
            MarkingSettings.AddFromFile(dialog.FileName);
            Reload();
        }

        private void DeleteProfile()
        {
            string name = MarkingSettings.Current().Name;
            if (MessageBox.Show(this, $"Удалить профиль «{name}» из списка?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            MarkingSettings.Delete(name);
            Reload();
        }

        private static TextBox Number() => new TextBox { Width = 48, TextAlignment = TextAlignment.Right };

        private static string Text(double value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);

        private static double Read(TextBox box)
        {
            string text = box.Text.Trim().Replace(',', '.');
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                throw new ArgumentException("Запасы кабеля должны быть числами.");
            }
            return value;
        }

        private void Guarded(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title);
            }
        }
    }
}
