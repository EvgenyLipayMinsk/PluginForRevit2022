using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using PluginForRevit2022.Core;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace PluginForRevit2022.UI
{
    /// <summary>
    /// Окно «Добавить параметры из ФОП». Логика — в SharedParameterOperations (её же вызывает MCP).
    /// Открывается модально из команды, поэтому транзакции здесь допустимы.
    /// </summary>
    public partial class AddSharedParametersWindow : Window
    {
        private readonly Document _doc;
        private readonly RevitApplication _app;
        private readonly List<CheckItem> _parameters = new List<CheckItem>();
        private readonly List<CheckItem> _categories = new List<CheckItem>();
        private string _loadedPath;

        internal AddSharedParametersWindow(Document doc, RevitApplication app)
        {
            InitializeComponent();
            _doc = doc;
            _app = app;

            Title = $"Добавить параметры из ФОП — {doc.Title}";
            bool isFamily = doc.IsFamilyDocument;
            CategoryPanel.Visibility = isFamily ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            FamilyNote.Visibility = isFamily ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            AllowKindChangeBox.IsEnabled = isFamily;

            List<ParameterGroupInfo> groups = SharedParameterOperations.ParameterGroups();
            GroupCombo.ItemsSource = groups;
            GroupCombo.SelectedItem = groups.FirstOrDefault(g => g.Key == "data") ?? groups.FirstOrDefault();

            if (!isFamily)
            {
                foreach (Category category in SharedParameterOperations.BindableCategories(doc))
                {
                    _categories.Add(new CheckItem(category.Name, category.Name, null, null, OnSelectionChanged));
                }
                CategoryList.ItemsSource = _categories;
                CollectionViewSource.GetDefaultView(_categories).Filter = CategoryFilter;
            }

            ParameterList.ItemsSource = _parameters;
            ICollectionView parameterView = CollectionViewSource.GetDefaultView(_parameters);
            parameterView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CheckItem.Group)));
            parameterView.Filter = ParameterFilter;

            FilePathBox.Text = app.SharedParametersFilename ?? string.Empty;
            LoadFile();
        }

        // ---------- ФОП ----------

        private void OnBrowseFile(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Файл общих параметров",
                Filter = "Файл общих параметров (*.txt)|*.txt|Все файлы (*.*)|*.*",
            };
            if (File.Exists(FilePathBox.Text))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(FilePathBox.Text);
            }
            if (dialog.ShowDialog(this) == true)
            {
                FilePathBox.Text = dialog.FileName;
                LoadFile();
            }
        }

        private void OnReloadFile(object sender, RoutedEventArgs e) => LoadFile();

        private void LoadFile()
        {
            _parameters.Clear();
            _loadedPath = null;
            try
            {
                var (path, definitions) = SharedParameterOperations.ReadFile(_app, FilePathBox.Text.Trim());
                _loadedPath = path;
                FilePathBox.Text = path;
                foreach (SharedParameterDef p in definitions)
                {
                    string tooltip = $"GUID: {p.Guid}\nТип данных: {p.DataType}" +
                                     (string.IsNullOrWhiteSpace(p.Description) ? "" : "\n" + p.Description);
                    _parameters.Add(new CheckItem(p.Name, p.Guid.ToString(), p.Group,
                        string.IsNullOrEmpty(p.DataType) ? null : $"  ({p.DataType})", OnSelectionChanged) { Tooltip = tooltip });
                }
                ResultBox.Text = $"Загружено параметров: {definitions.Count}";
            }
            catch (Exception ex)
            {
                ResultBox.Text = "Не удалось прочитать ФОП: " + ex.Message;
            }
            CollectionViewSource.GetDefaultView(_parameters).Refresh();
            OnSelectionChanged();
        }

        // ---------- Фильтры ----------

        private bool ParameterFilter(object item)
        {
            string text = ParameterSearchBox.Text?.Trim();
            var p = (CheckItem)item;
            return string.IsNullOrEmpty(text) ||
                   p.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   (p.Group ?? "").IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private bool CategoryFilter(object item)
        {
            var c = (CheckItem)item;
            if (OnlyCheckedCategoriesBox.IsChecked == true && !c.IsChecked)
            {
                return false;
            }
            string text = CategorySearchBox.Text?.Trim();
            return string.IsNullOrEmpty(text) || c.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void OnParameterSearchChanged(object sender, RoutedEventArgs e) =>
            CollectionViewSource.GetDefaultView(_parameters).Refresh();

        private void OnCategoryFilterChanged(object sender, RoutedEventArgs e)
        {
            if (_categories.Count > 0)
            {
                CollectionViewSource.GetDefaultView(_categories).Refresh();
            }
        }

        private void OnSelectionChanged()
        {
            int parameters = _parameters.Count(p => p.IsChecked);
            ParameterCountText.Text = $"выбрано {parameters} из {_parameters.Count}";
            SelectionText.Text = _doc.IsFamilyDocument
                ? $"Параметров: {parameters}"
                : $"Параметров: {parameters}, категорий: {_categories.Count(c => c.IsChecked)}";
        }

        // ---------- Выполнение ----------

        private AddSharedParametersRequest BuildRequest(bool dryRun)
        {
            if (_loadedPath == null)
            {
                throw new InvalidOperationException("Сначала загрузите ФОП.");
            }
            return new AddSharedParametersRequest
            {
                FilePath = _loadedPath,
                Parameters = _parameters.Where(p => p.IsChecked).Select(p => p.Key).ToList(),
                Categories = _categories.Where(c => c.IsChecked).Select(c => c.Key).ToList(),
                IsInstance = InstanceRadio.IsChecked == true,
                ParameterGroup = (GroupCombo.SelectedItem as ParameterGroupInfo)?.Id,
                AllowKindChange = AllowKindChangeBox.IsChecked == true,
                DryRun = dryRun,
            };
        }

        private void OnPreview(object sender, RoutedEventArgs e) => Run(dryRun: true);

        private void OnApply(object sender, RoutedEventArgs e)
        {
            int count = _parameters.Count(p => p.IsChecked);
            string question = _doc.IsFamilyDocument
                ? $"Добавить параметров: {count} в семейство «{_doc.Title}»?"
                : $"Добавить параметров: {count} к категориям: {_categories.Count(c => c.IsChecked)}?";
            if (MessageBox.Show(this, question + "\n\nОтменить можно через Ctrl+Z.", Title,
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                Run(dryRun: false);
            }
        }

        private void Run(bool dryRun)
        {
            try
            {
                AddSharedParametersResult result = SharedParameterOperations.Add(_doc, _app, BuildRequest(dryRun));
                ResultBox.Text = result.ToText();
            }
            catch (Exception ex)
            {
                ResultBox.Text = "Ошибка: " + ex.Message;
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        // ---------- Модель строки ----------

        internal sealed class CheckItem : INotifyPropertyChanged
        {
            private readonly Action _onChanged;
            private bool _isChecked;

            public CheckItem(string name, string key, string group, string dataType, Action onChanged)
            {
                Name = name;
                Key = key;
                Group = group;
                DataType = dataType;
                _onChanged = onChanged;
            }

            public string Name { get; }
            public string Key { get; }
            public string Group { get; }
            public string DataType { get; }
            public string Tooltip { get; set; }

            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value)
                    {
                        return;
                    }
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                    _onChanged?.Invoke();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
