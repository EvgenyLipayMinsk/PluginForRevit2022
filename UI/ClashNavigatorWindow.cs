using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using PluginForRevit2022.Core;
using PluginForRevit2022.Core.Clashes;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace PluginForRevit2022.UI
{
    /// <summary>Действия с моделью из немодального окна — только через ExternalEvent (в контексте Revit API).</summary>
    internal sealed class RevitActionQueue : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();
        private readonly ExternalEvent _event;

        public RevitActionQueue() { _event = ExternalEvent.Create(this); }

        public void Run(Action<UIApplication> action)
        {
            _queue.Enqueue(action);
            _event.Raise();
        }

        /// <summary>Окно закрыто: невыполненные действия отбрасываются, событие освобождается.</summary>
        public void Close()
        {
            while (_queue.TryDequeue(out _)) { }
            _event.Dispose();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out Action<UIApplication> action))
            {
                try { action(app); }
                catch (Exception ex) { PluginLog.Warn("clash", ex.ToString()); }
            }
        }

        public string GetName() => "AerBIM: коллизии";
    }

    /// <summary>
    /// Окно работы с отчётом Navisworks: список коллизий, переход вперёд/назад с показом в модели,
    /// статус (в работе, передано смежному разделу, фиктивная, исправлено) и комментарий, выгрузка в Excel.
    /// </summary>
    internal sealed class ClashNavigatorWindow : Window
    {
        private static ClashNavigatorWindow _current;

        private readonly RevitActionQueue _revit;
        private ClashReport _report = new ClashReport();
        private List<ClashItem> _view = new List<ClashItem>();

        private readonly TextBlock _files = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _filter = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _counts = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
        private readonly ListBox _list = new ListBox { Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        private readonly Image _image = new Image { MaxHeight = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBox _comment = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 56, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock _commentHint = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 2) };
        private readonly TextBlock _state = new TextBlock { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly CheckBox _autoShow = new CheckBox { Content = "Показывать в модели при переходе", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 6) };
        private readonly CheckBox _onlyModel = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        private List<string> _modelNames = new List<string>();
        private string _modelKey = "";
        private string _modelTitle = "";
        private string _revitUser = "";
        private UIApplication _app;
        private readonly Dictionary<ClashStatus, Button> _statusButtons = new Dictionary<ClashStatus, Button>();
        private bool _loading;
        private bool _noAutoShow;
        private ClashItem _pendingShow; // выбор строки программно (обновление списка) — модель не перестраиваем

        private static readonly Dictionary<ClashStatus, Color> StatusColor = new Dictionary<ClashStatus, Color>
        {
            [ClashStatus.New] = Color.FromRgb(136, 135, 128),
            [ClashStatus.InWork] = Color.FromRgb(186, 117, 23),
            [ClashStatus.Passed] = Color.FromRgb(24, 95, 165),
            [ClashStatus.Fake] = Color.FromRgb(83, 74, 183),
            [ClashStatus.Fixed] = Color.FromRgb(59, 109, 17),
        };

        public static void Open(UIApplication app)
        {
            if (_current != null)
            {
                if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
                _current.Activate();
                return;
            }
            _current = new ClashNavigatorWindow(new RevitActionQueue());
            new WindowInteropHelper(_current).Owner = Process.GetCurrentProcess().MainWindowHandle;
            _current._app = app;
            _current._revitUser = app.Application.Username ?? "";
            _current.SetModel(app.ActiveUIDocument?.Document);
            // Переключились на другую модель — список коллизий перестраивается под неё
            app.ViewActivated += OnViewActivated;
            _current.Closing += (s, e) => _current.SaveComment();
            _current.Closed += (s, e) =>
            {
                try { _current._app.ViewActivated -= OnViewActivated; } catch (Exception) { }
                _current._revit.Close();
                _current = null;
            };
            _current.Show();
        }

        private static void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            ClashNavigatorWindow w = _current;
            if (w == null) return;
            Autodesk.Revit.DB.Document doc = e.Document;
            string key = doc == null ? "" : doc.PathName + "|" + doc.Title;
            if (key == w._modelKey) return; // тот же документ (в т.ч. наш вид коллизий) — ничего не меняем
            List<string> names = ClashRevit.ModelNames(doc);
            string title = doc?.Title ?? "";
            w.Dispatcher.BeginInvoke(new Action(() => w.ApplyModel(key, names, title)));
        }

        /// <summary>Вызывается в контексте Revit API: имена модели читаются здесь, окно обновляется своим потоком.</summary>
        private void SetModel(Autodesk.Revit.DB.Document doc)
        {
            ApplyModel(doc == null ? "" : doc.PathName + "|" + doc.Title, ClashRevit.ModelNames(doc), doc?.Title ?? "");
        }

        private void ApplyModel(string key, List<string> names, string title)
        {
            _modelKey = key;
            _modelNames = names;
            _modelTitle = title ?? "";
            _onlyModel.Content = string.IsNullOrEmpty(title) ? "Только активная модель" : $"Только модель «{title}»";
            if (_list != null && _report.Items.Count > 0)
            {
                SaveComment();
                Refresh(Selected);
            }
        }

        /// <summary>Коллизия относится к активной модели: один из её элементов из этого файла.</summary>
        private bool InModel(ClashItem i) => ClashRevit.IsModel(i.Item1.File, _modelNames) || ClashRevit.IsModel(i.Item2.File, _modelNames);

        /// <summary>Фильтр по модели работает, только если в отчёте есть файлы элементов (в точках обзора их нет).</summary>
        private bool ModelFilterOn => _onlyModel.IsChecked == true && _modelNames.Count > 0
            && _report.Items.Any(i => !string.IsNullOrEmpty(i.Item1.File) || !string.IsNullOrEmpty(i.Item2.File));

        private ClashNavigatorWindow(RevitActionQueue revit)
        {
            _revit = revit;
            Title = "Коллизии Navisworks";
            Width = 980;
            Height = 640;
            MinWidth = 760;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontSize = 13;

            var root = new DockPanel { Margin = new Thickness(12) };

            // Верх: открыть отчёт, фильтр
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var open = Btn("Открыть отчёт…", (s, e) => OpenReport());
            DockPanel.SetDock(open, Dock.Left);
            top.Children.Add(open);
            top.Children.Add(_files);
            root.Children.Add(top);
            DockPanel.SetDock(top, Dock.Top);

            // Счётчики переносятся на следующую строку, а не обрезаются, когда окно узкое
            var filterBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(_filter, Dock.Left);
            _filter.Items.Add("Все, кроме исправленных");
            _filter.Items.Add("Все");
            foreach (ClashStatus s in ClashStatusText.All) _filter.Items.Add(ClashStatusText.Of(s));
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += (s, e) => Refresh(Selected);
            _onlyModel.ToolTip = "Показывать только коллизии, в которых участвует элемент активной модели Revit — их исправляют в этой модели.\nПри переключении на другую модель список перестраивается.";
            _onlyModel.Click += (s, e) => { SaveComment(); Refresh(Selected); };
            DockPanel.SetDock(_onlyModel, Dock.Left);
            filterBar.Children.Add(_filter);
            filterBar.Children.Add(_onlyModel);
            filterBar.Children.Add(_counts);
            root.Children.Add(filterBar);
            DockPanel.SetDock(filterBar, Dock.Top);

            // Низ: навигация и выгрузка
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            var export = Btn("Выгрузить в Excel…", (s, e) => Export());
            DockPanel.SetDock(export, Dock.Right);
            bottom.Children.Add(export);
            // Кнопки и флажок переходят на следующую строку, когда окно узкое, — ничего не перекрывается
            var nav = new WrapPanel();
            nav.Children.Add(Btn("◀ Предыдущая", (s, e) => Step(-1)));
            nav.Children.Add(Btn("Показать в модели", (s, e) => ShowSelected()));
            nav.Children.Add(Btn("Следующая ▶", (s, e) => Step(1)));
            nav.Children.Add(_autoShow);
            bottom.Children.Add(nav);
            root.Children.Add(bottom);
            DockPanel.SetDock(bottom, Dock.Bottom);

            // Центр: список и карточка коллизии
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _list.SelectionChanged += (s, e) => ShowCard(true);
            Grid.SetColumn(_list, 0);
            grid.Children.Add(_list);

            var card = new StackPanel();
            card.Children.Add(_title);
            card.Children.Add(_details);
            card.Children.Add(_image);
            var statuses = new WrapPanel();
            foreach (ClashStatus s in ClashStatusText.All)
            {
                ClashStatus status = s;
                var b = Btn(ClashStatusText.Of(status), (o, e) => SetStatus(status));
                b.MinWidth = 120;
                _statusButtons[status] = b;
                statuses.Children.Add(b);
            }
            card.Children.Add(new TextBlock { Text = "Статус", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            card.Children.Add(statuses);
            card.Children.Add(_commentHint);
            card.Children.Add(_comment);
            card.Children.Add(_state);
            _comment.LostFocus += (s, e) => SaveComment();
            var scroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 1);
            grid.Children.Add(scroll);
            root.Children.Add(grid);

            Content = root;
            PreviewKeyDown += OnKey;
            ShowCard(false);
        }

        private ClashItem Selected => _list.SelectedItem is ListBoxItem li ? li.Tag as ClashItem : null;

        private static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += click;
            return b;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_comment.IsKeyboardFocusWithin) return; // в комментарии стрелки — для текста
            if (e.Key == Key.Down || e.Key == Key.Right || e.Key == Key.PageDown) { Step(1); e.Handled = true; }
            else if (e.Key == Key.Up || e.Key == Key.Left || e.Key == Key.PageUp) { Step(-1); e.Handled = true; }
        }

        // ===================== Отчёт =====================

        private void OpenReport()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Отчёты Navisworks",
                Filter = "Отчёты Navisworks (*.html;*.htm;*.xml)|*.html;*.htm;*.xml|Все файлы|*.*",
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                SaveComment();
                ClashReport report = NavisReportReader.Read(dialog.FileNames);
                ClashStatusStore.Load(report);
                _report = report;
                _files.Text = string.Join("; ", report.Files.Select(Path.GetFileName));
                PluginLog.Info("clash.open", $"{report.Files.Count} файл(ов), коллизий {report.Items.Count}");
                Refresh(null);
                if (report.Warnings.Count > 0) MessageBox.Show(this, string.Join("\n", report.Warnings), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось прочитать отчёт: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Refresh(ClashItem keep)
        {
            int f = _filter.SelectedIndex;
            bool byModel = ModelFilterOn;
            List<ClashItem> scope = byModel ? _report.Items.Where(InModel).ToList() : _report.Items;
            _view = scope.Where(i => f == 1 || (f == 0 ? i.Status != ClashStatus.Fixed : i.Status == ClashStatusText.All[f - 2])).ToList();
            _loading = true;
            _list.Items.Clear();
            foreach (ClashItem i in _view) _list.Items.Add(Row(i));
            _loading = false;
            int index = keep == null ? 0 : Math.Max(0, _view.IndexOf(keep));
            _noAutoShow = true;
            if (_view.Count > 0) _list.SelectedIndex = Math.Min(index, _view.Count - 1);
            else ShowCard(false);
            _noAutoShow = false;
            _counts.Text = _report.Items.Count == 0 ? "Откройте отчёт Navisworks (HTML или XML)."
                : string.Join("   ", ClashStatusText.All.Select(s => $"{ClashStatusText.Of(s)}: {scope.Count(i => i.Status == s)}"))
                  + (byModel ? $"   В этой модели: {scope.Count} из {_report.Items.Count}" : $"   Всего: {_report.Items.Count}");
        }

        private ListBoxItem Row(ClashItem i)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(StatusColor[i.Status]), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = i.Name, FontWeight = FontWeights.SemiBold });
            string sub = string.Join(" · ", new[] { i.Test, i.Group }.Where(x => !string.IsNullOrEmpty(x)));
            if (sub.Length > 0) text.Children.Add(new TextBlock { Text = sub, Foreground = Brushes.DimGray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(dot);
            panel.Children.Add(text);
            return new ListBoxItem { Content = panel, Tag = i, Padding = new Thickness(4, 3, 4, 3) };
        }

        // ===================== Карточка =====================

        private void ShowCard(bool fromSelection)
        {
            if (_loading) return;
            ClashItem i = Selected;
            bool has = i != null;
            foreach (Button b in _statusButtons.Values) b.IsEnabled = has;
            _comment.IsEnabled = has;
            if (!has)
            {
                _title.Text = _report.Items.Count == 0 ? "Отчёт не открыт" : (ModelFilterOn ? "В активной модели нет коллизий с таким статусом" : "Нет коллизий с таким статусом");
                _details.Text = _report.Items.Count == 0 ? "Нажмите «Открыть отчёт…» и выберите HTML или XML, выгруженный из Navisworks (Clash Detective или точки обзора)." : "";
                _image.Source = null;
                _comment.Text = "";
                _commentHint.Text = "";
                _state.Text = "";
                return;
            }
            _title.Text = i.Name;
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(i.Test)) lines.Add((i.IsViewpoint ? "Папка: " : "Проверка: ") + i.Test);
            if (!string.IsNullOrEmpty(i.Group)) lines.Add("Группа: " + i.Group);
            if (!string.IsNullOrEmpty(i.NavisStatus)) lines.Add("Статус Navisworks: " + i.NavisStatus);
            if (!string.IsNullOrEmpty(i.Date)) lines.Add("Обнаружена: " + i.Date);
            if (!string.IsNullOrEmpty(i.Distance)) lines.Add("Расстояние: " + i.Distance);
            if (i.IsViewpoint) lines.Add("Точка обзора Navisworks: в модели откроется вид с камеры и секущим кубом.");
            else if (i.Point != null) lines.Add("Точка конфликта: " + i.PointText);
            if (!i.Item1.IsEmpty) lines.Add("Элемент 1: " + i.Item1);
            if (!i.Item2.IsEmpty) lines.Add("Элемент 2: " + i.Item2);
            if (!string.IsNullOrEmpty(i.Description)) lines.Add("Описание: " + i.Description);
            _details.Text = string.Join("\n", lines);
            _image.Source = LoadImage(i.ImagePath);
            _comment.Text = i.Comment;
            UpdateStatusUi(i);
            if (fromSelection && !_noAutoShow && _autoShow.IsChecked == true) ShowSelected();
        }

        private void UpdateStatusUi(ClashItem i)
        {
            foreach (var pair in _statusButtons)
            {
                bool on = pair.Key == i.Status;
                pair.Value.Background = on ? new SolidColorBrush(StatusColor[pair.Key]) : SystemColors.ControlBrush;
                pair.Value.Foreground = on ? Brushes.White : SystemColors.ControlTextBrush;
                pair.Value.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
            _commentHint.Text = i.Status == ClashStatus.Fake ? "Комментарий: почему коллизия фиктивная (по желанию)" : "Комментарий (по желанию)";
            _state.Text = string.IsNullOrEmpty(i.ChangedBy) ? "" : $"Статус изменил: {i.ChangedBy}, {i.ChangedAt}";
        }

        private static BitmapImage LoadImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception) { return null; }
        }

        // ===================== Статусы =====================

        private void SetStatus(ClashStatus status)
        {
            ClashItem i = Selected;
            if (i == null) return;
            i.Comment = _comment.Text.Trim();
            i.Status = status;
            Touch(i);
            Persist(i);
            if (status == ClashStatus.Fake && i.Comment.Length == 0) _comment.Focus();
            // В фильтре «кроме исправленных» исправленная коллизия уходит из списка — выбирается следующая
            int index = _list.SelectedIndex;
            Refresh(null);
            bool moved = !_view.Contains(i);
            _noAutoShow = !moved;
            if (!moved) _list.SelectedIndex = _view.IndexOf(i);
            else if (_view.Count > 0) _list.SelectedIndex = Math.Min(index, _view.Count - 1);
            _noAutoShow = false;
            if (moved && _autoShow.IsChecked == true) ShowSelected();
        }

        internal void SaveComment()
        {
            ClashItem i = Selected;
            if (i == null || i.Comment == _comment.Text.Trim()) return;
            i.Comment = _comment.Text.Trim();
            Touch(i);
            Persist(i);
            UpdateStatusUi(i);
        }

        private void Touch(ClashItem i)
        {
            i.ChangedBy = string.IsNullOrWhiteSpace(_revitUser) ? Environment.UserName : _revitUser; // имя пользователя Revit
            i.ChangedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }

        private void Persist(ClashItem i)
        {
            try
            {
                string file = ClashStatusStore.Save(i);
                if (!file.StartsWith(i.Source, StringComparison.OrdinalIgnoreCase))
                    _state.Text = $"Папка отчёта только для чтения — статусы сохранены в профиле: {file}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Статус не сохранён: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ===================== Навигация и модель =====================

        private void Step(int delta)
        {
            if (_view.Count == 0) return;
            SaveComment();
            int index = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex + delta;
            if (index < 0 || index >= _view.Count) return;
            _list.SelectedIndex = index;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void ShowSelected()
        {
            ClashItem i = Selected;
            if (i == null) return;
            _state.Text = "Показываю в модели…";
            _pendingShow = i;
            _revit.Run(app =>
            {
                if (_pendingShow != i) return; // пока ждали Revit, выбрали другую коллизию
                string result;
                Autodesk.Revit.DB.XYZ min = null, max = null;
                try { result = ClashRevit.Show(app, i, out min, out max); }
                catch (Exception ex) { result = "Не удалось показать: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { if (Selected == i) _state.Text = result; }));
                // Только что открытый вид появляется среди открытых после возврата в Revit — масштаб отдельным шагом
                if (min != null) _revit.Run(a => ClashRevit.Zoom(a, min, max));
            });
        }

        private void Export()
        {
            if (_report.Items.Count == 0) return;
            SaveComment();
            // Как в списке: при флажке «Только модель …» — только коллизии активной модели
            bool byModel = ModelFilterOn;
            List<ClashItem> items = byModel ? _report.Items.Where(InModel).ToList() : _report.Items;
            if (items.Count == 0)
            {
                MessageBox.Show(this, "В активной модели нет коллизий из этого отчёта — выгружать нечего.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string model = byModel && _modelTitle.Length > 0 ? "_" + string.Concat(_modelTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) : "";
            var dialog = new SaveFileDialog
            {
                Title = "Выгрузка коллизий",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = $"Коллизии{model}_{DateTime.Now:yyyy-MM-dd}.xlsx",
                InitialDirectory = Path.GetDirectoryName(_report.Files.FirstOrDefault() ?? ""),
            };
            if (dialog.ShowDialog(this) != true) return;
            // Сначала Revit дописывает журнал замены ID (столбцы «новый ID»), потом выгрузка
            string target = dialog.FileName;
            _state.Text = "Выгружаю…";
            _revit.Run(app =>
            {
                ElementIdJournal.FlushAll();
                Dispatcher.BeginInvoke(new Action(() => { _state.Text = ""; WriteExcel(items, target, byModel); }));
            });
        }

        private void WriteExcel(List<ClashItem> items, string path, bool byModel)
        {
            var dialog = new { FileName = path };
            try
            {
                ClashExcel.Write(items, dialog.FileName);
                PluginLog.Info("clash.export", dialog.FileName + (byModel ? " | модель " + _modelTitle : ""));
                string scope = byModel ? $"Модель «{_modelTitle}».\n" : "";
                if (MessageBox.Show(this, $"{scope}Выгружено: {items.Count(x => x.Status != ClashStatus.Fixed)} коллизий, исправленных — {items.Count(x => x.Status == ClashStatus.Fixed)} (лист «Исправлено»).\nОткрыть файл?",
                        Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось выгрузить: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
