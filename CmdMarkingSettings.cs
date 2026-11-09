using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using PluginForRevit2022.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;

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
        private readonly DataGrid _table = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        private List<FamilySetting> _rows;
        private bool _reloading;

        public MarkingSettingsWindow(Document doc)
        {
            _doc = doc;
            Title = "Настройки маркировки";
            Width = 720;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _table.Columns.Add(new DataGridCheckBoxColumn { Header = "В линии КЖ", Binding = new System.Windows.Data.Binding("InLine") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged } });
            _table.Columns.Add(new DataGridTextColumn { Header = "Семейство", Binding = new System.Windows.Data.Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _table.Columns.Add(new DataGridTextColumn { Header = "БЦО", Binding = new System.Windows.Data.Binding("Bco") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 120 });

            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(new TextBlock { Text = "Профиль", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_profiles);
            bar.Children.Add(Tool("+", "Новый профиль", NewProfile));
            bar.Children.Add(Tool("Сохранить", "Сохранить текущий профиль", SaveProfile));
            bar.Children.Add(Tool("Удалить", "Удалить текущий профиль", DeleteProfile));
            bar.Children.Add(Tool("Открыть файл…", "Подключить файл профиля, полученный от коллеги", OpenProfile));
            _profiles.SelectionChanged += (s, e) => SwitchProfile();

            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(bar);
            top.Children.Add(new TextBlock { Text = "Пустое БЦО — берётся значение из окна маркировки. Чтобы передать настройки, отправьте файл профиля.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(_table, 1);
            layout.Children.Add(top);
            layout.Children.Add(_table);
            Content = layout;
            Reload();
        }

        private Button Tool(string text, string tip, Action action)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += (s, e) => Guarded(action);
            return button;
        }

        private void Reload()
        {
            _reloading = true;
            _profiles.ItemsSource = MarkingSettings.Profiles().Select(p => p.Name).ToList();
            _profiles.SelectedItem = MarkingSettings.Current().Name;
            _rows = MarkingSettings.ForDocument(_doc);
            _table.ItemsSource = _rows;
            _reloading = false;
        }

        private void Store()
        {
            _table.CommitEdit(DataGridEditingUnit.Row, true);
            MarkingSettings.Save(_rows);
        }

        private void SaveProfile() => Store();

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
