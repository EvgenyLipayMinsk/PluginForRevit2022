using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PluginForRevit2022.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;

namespace PluginForRevit2022
{
    /// <summary>Маркировка устройств под кабельный журнал: ручной порядок кликами или автоматический по трассам.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdMarkDevices : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            var window = new MarkDevicesWindow(doc);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            if (window.ShowDialog() != true)
            {
                return Result.Cancelled;
            }
            MarkDevicesRequest request = window.Request;

            if (!MarkingSettings.Load().AnyInLine(request.System))
            {
                TaskDialog.Show("Маркировка", "В настройках маркировки нет семейств с галочкой этой системы. Отметьте их кнопкой «Настройки маркировки».");
                return Result.Cancelled;
            }

            // Семейства системы без БЦО — сказать до кликов, а не после выбора всей линии
            MarkingSettings bcoSettings = MarkingSettings.Load();
            List<string> noBco = MarkingSettings.ReadAccount().Families
                .Where(f => bcoSettings.IsInLine(f.Name, request.System) && string.IsNullOrWhiteSpace(f.Bco)).Select(f => f.Name).ToList();
            if (noBco.Count > 0 && !SessionWindows.Confirm("mark-no-bco", "Маркировка",
                    "У семейств с галочкой этой системы не задано БЦО:\n" + string.Join("\n", noBco.Select(f => "• " + f)) +
                    "\n\nЕсли такие устройства окажутся в выборе, маркировка не начнётся. Продолжить выбор устройств?"))
                return Result.Cancelled;

            try
            {
                if (request.Order == "route")
                {
                    SessionWindows.Inform("mark-route-hint", "Маркировка",
                        $"Маршрут строится по трассам этой линии: у семейств кабеля в «ADSK_Группирование» должно стоять …АЛС.{request.Controller}.{request.Line} " +
                        "(например СПС_АЛС.1.2 — прибор 1, линия 2). Если таких трасс нет, берутся трассы с тем же значением «Комментарии», что в окне.\n\n" +
                        "Шаг 1: кликните прибор (категория «Электрооборудование»), от которого идёт линия.\n\n" +
                        "Шаг 2: кликайте устройства линии — каждое выбранное становится синим. Порядок кликов не важен: номера пойдут по трассе. Чтобы закончить выбор, нажмите Esc.\n\n" +
                        "Выбрать можно только семейства с галочкой этой системы в настройках маркировки.");
                    Reference controller = uiDoc.Selection.PickObject(ObjectType.Element, new CategoryFilter(null, BuiltInCategory.OST_ElectricalEquipment), "Выберите прибор, от которого идёт линия");
                    request.ControllerElementId = RevitIds.IdOf(controller.ElementId);
                    request.Ids = PickInOrder(uiDoc, DeviceFilter(request.System));
                }
                else
                {
                    SessionWindows.Inform("mark-manual-hint", "Маркировка",
                        "Шаг 1: кликните прибор (категория «Электрооборудование»), к которому подключена линия. " +
                        "В него запишутся данные для кабельного журнала.\n\n" +
                        "Шаг 2: кликайте устройства по порядку номеров — каждое выбранное становится синим. Чтобы закончить выбор, нажмите Esc.\n\n" +
                        "Выбрать можно только семейства с галочкой этой системы в настройках маркировки.");
                    Reference controller = uiDoc.Selection.PickObject(ObjectType.Element, new CategoryFilter(null, BuiltInCategory.OST_ElectricalEquipment), "Выберите прибор, к которому подключена линия");
                    request.ControllerElementId = RevitIds.IdOf(controller.ElementId);
                    request.Ids = PickInOrder(uiDoc, DeviceFilter(request.System));
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (request.Ids.Count == 0)
            {
                return Result.Cancelled;
            }

            try
            {
                request.DryRun = true;
                Dictionary<string, object> preview = DeviceMarkingOperations.Mark(doc, request);
                if (((List<string>)preview["failures"]).Count > 0)
                {
                    TaskDialog.Show("Маркировка", preview["summary"] + "\n\n" + string.Join("\n", ((List<string>)preview["failures"]).Take(15)));
                    return Result.Failed;
                }
                var devices = (List<Dictionary<string, object>>)preview["devices"];
                string info = $"Система: {window.SystemTitle}\nПрибор: {request.Controller}, линия: {request.Line}\nНомера: {devices.First()["number"]}–{devices.Last()["number"]}\nЗанято адресов на линии: {preview["lineAddresses"]} из {preview["lineCapacity"]}";
                if (preview["controller"] is Dictionary<string, object> controllerInfo)
                {
                    var controllerChanges = (List<Dictionary<string, object>>)controllerInfo["changes"];
                    info += controllerChanges.Count == 0
                        ? "\n\nПрибор: данные для журнала уже заполнены"
                        : "\n\nВ прибор будет записано:\n" + string.Join("\n", controllerChanges.Select(c =>
                            $"  {c["parameter"]}: {(string.IsNullOrEmpty((string)c["old"]) ? "(пусто)" : c["old"])} → {c["new"]}"));
                }
                var warnings = (List<string>)preview["warnings"];
                if (warnings.Count > 0) info += "\n\nВнимание:\n" + string.Join("\n", warnings.Take(5));
                if (!SessionWindows.Confirm("mark-devices-confirm", "Маркировка", $"Разметить устройств: {devices.Count}?\n\n{info}"))
                {
                    return Result.Cancelled;
                }
                request.DryRun = false;
                SessionWindows.Inform("mark-devices-result", "Маркировка", (string)DeviceMarkingOperations.Mark(doc, request)["summary"]);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Маркировка", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Клики по одному задают номера; Esc завершает выбор. Уже выбранные устройства
        /// временно закрашиваются синим на текущем виде; после выбора окраска откатывается
        /// (в проекте не остаётся), а устройства остаются выделенными.
        /// </summary>
        /// <summary>Выбор кликами по порядку: каждое кликнутое устройство сразу красится синим (временно), Esc — конец.</summary>
        internal static List<long> PickInOrder(UIDocument uiDoc, ISelectionFilter filter)
        {
            Document doc = uiDoc.Document;
            View view = uiDoc.ActiveView;
            var ids = new List<long>();
            var picked = new List<ElementId>();
            OverrideGraphicSettings paint = PickedOverride(doc);
            bool canPaint = paint != null;
            string note = "";

            using (var group = new TransactionGroup(doc, "AerBIM: подсветка выбранных устройств"))
            {
                group.Start();
                try
                {
                    while (true)
                    {
                        Reference reference = uiDoc.Selection.PickObject(ObjectType.Element, filter,
                            $"Устройство №{ids.Count + 1}. Выбрано: {ids.Count}.{note} Esc — завершить");
                        long id = RevitIds.IdOf(reference.ElementId);
                        if (ids.Contains(id))
                        {
                            note = $" Это устройство уже №{ids.IndexOf(id) + 1}.";
                            continue;
                        }
                        note = "";
                        ids.Add(id);
                        picked.Add(reference.ElementId);

                        if (canPaint)
                        {
                            try
                            {
                                using (var paintStep = new Transaction(doc, "подсветка"))
                                {
                                    paintStep.Start();
                                    view.SetElementOverrides(reference.ElementId, paint);
                                    paintStep.Commit();
                                }
                            }
                            catch (Exception)
                            {
                                canPaint = false; // вид занят другим пользователем или не допускает переопределений: остаётся выделение
                            }
                        }
                        uiDoc.Selection.SetElementIds(picked);
                        uiDoc.RefreshActiveView();
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // Esc — конец выбора.
                }
                finally
                {
                    group.RollBack(); // окраска временная: проект не меняется
                }
            }
            uiDoc.Selection.SetElementIds(picked);
            uiDoc.RefreshActiveView();
            return ids;
        }

        /// <summary>Зелёная сплошная заливка и линии; null — в проекте нет сплошной заливки.</summary>
        private static OverrideGraphicSettings PickedOverride(Document doc)
        {
            FillPatternElement solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>().FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            // Синий и толстые линии: заметно и на символах-аннотациях (низкая детализация) — переопределение экземпляра действует на вложенные аннотации
            var blue = new Color(0, 112, 192);
            var settings = new OverrideGraphicSettings()
                .SetProjectionLineColor(blue)
                .SetProjectionLineWeight(6);
            if (solid != null)
            {
                settings.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(blue);
            }
            return settings;
        }

        private static ISelectionFilter DeviceFilter(string system)
        {
            switch (system)
            {
                case "aps": return new CategoryFilter(system, BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_FireAlarmDevices);
                case "souye": return new CategoryFilter(system, BuiltInCategory.OST_NurseCallDevices);
                default: return new CategoryFilter(system, BuiltInCategory.OST_DataDevices);
            }
        }

        private sealed class CategoryFilter : ISelectionFilter
        {
            private readonly HashSet<long> _categories;
            private readonly MarkingSettings _settings = MarkingSettings.Load();
            private readonly string _system;

            public CategoryFilter(string system, params BuiltInCategory[] categories)
            {
                _system = system;
                _categories = new HashSet<long>(categories.Select(c => (long)c));
            }

            public bool AllowElement(Element element) =>
                element is FamilyInstance instance && element.Category != null && _categories.Contains(RevitIds.IdOf(element.Category.Id)) &&
                (_system == null || _settings.IsInLine(instance.Symbol.FamilyName, _system));

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }

    internal sealed class MarkDevicesWindow : Window
    {
        private readonly ComboBox _system = Combo("АПС|aps", "СОТС|security", "СОУЭ|souye");
        private readonly ComboBox _order = Combo("Вручную, кликами по устройствам|manual", "Автоматически по трассам журнала|route");
        private readonly TextBox _building = new TextBox();
        private readonly TextBox _controller = new TextBox();
        private readonly TextBox _line = new TextBox();
        private readonly TextBox _comments = new TextBox();
        private readonly Document _doc;
        private readonly TextBlock _floorsState = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBox _start = new TextBox { Text = "1" };

        public MarkDevicesRequest Request { get; private set; }

        public string SystemTitle => (string)((ComboBoxItem)_system.SelectedItem).Content;

        public MarkDevicesWindow(Document doc)
        {
            _doc = doc;
            Title = "Маркировка устройств";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(12) };
            void Row(string label, Control control)
            {
                panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) });
                panel.Children.Add(control);
            }
            Row("Система", _system);
            Row("Порядок номеров", _order);
            Row("Корпус (ARBM_Корпус; для СОТС не нужен)", _building);
            Row("Номер прибора", _controller);
            Row("Номер линии", _line);
            Row("Комментарии линии (например АПС): участвуют только устройства и трассы с этим значением", _comments);
            // Уровни без номера в имени («Тех. пространство», «Кровля») — у каждого свой этаж, задаётся отдельно
            var floors = new Button { Content = "Этажи уровней без номера…", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Left };
            floors.Click += (s, e) =>
            {
                var dialog = new LevelFloorsWindow(_doc) { Owner = this };
                dialog.ShowDialog();
                UpdateFloorsState();
            };
            panel.Children.Add(floors);
            panel.Children.Add(_floorsState);
            UpdateFloorsState();
            Row("Начать нумерацию с", _start);

            var ok = new Button { Content = "Далее: выбрать устройства", IsDefault = true, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(6) };
            ok.Click += (s, e) => Accept();
            panel.Children.Add(ok);
            Content = panel;
            LoadLast();
        }

        /// <summary>Подставляет значения последнего запуска; без файла окно остаётся пустым.</summary>
        private void LoadLast()
        {
            try
            {
                Dictionary<string, string> last = MarkingSettings.ReadAccount().LastMarking;
                Select(_system, last, "system");
                Select(_order, last, "order");
                Put(_building, last, "building");
                Put(_controller, last, "controller");
                Put(_line, last, "line");
                Put(_comments, last, "comments");
                Put(_start, last, "start");
            }
            catch (Exception)
            {
                // Повреждённый файл не должен мешать работе: окно откроется со значениями по умолчанию.
            }
        }

        private void SaveLast()
        {
            try
            {
                MarkingSettings.SaveLastMarking(new Dictionary<string, string>
                {
                    ["system"] = Value(_system),
                    ["order"] = Value(_order),
                    ["building"] = _building.Text,
                    ["controller"] = _controller.Text,
                    ["line"] = _line.Text,
                    ["comments"] = _comments.Text,
                    ["start"] = _start.Text,
                });
            }
            catch (Exception)
            {
                // Не удалось записать настройки: маркировка всё равно выполняется.
            }
        }

        private static void Put(TextBox box, Dictionary<string, string> last, string key)
        {
            if (last.TryGetValue(key, out string value)) box.Text = value ?? "";
        }

        private static void Select(ComboBox box, Dictionary<string, string> last, string key)
        {
            if (!last.TryGetValue(key, out string value)) return;
            ComboBoxItem item = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value);
            if (item != null) box.SelectedItem = item;
        }

        private void UpdateFloorsState()
        {
            List<string> levels = LevelFloorsWindow.UnnumberedLevels(_doc).Select(l => l.Name).ToList();
            int missing = levels.Count(l => MarkingSettings.LevelFloor(_doc, l) == null);
            _floorsState.Text = levels.Count == 0 ? "Все уровни проекта с номером в имени."
                : missing == 0 ? $"Этаж задан для всех уровней без номера ({levels.Count})."
                : $"Не задан этаж уровней: {missing} из {levels.Count}. Устройства на них не разметятся.";
        }

        private void Accept()
        {
            if (Value(_system) == "aps" && string.IsNullOrWhiteSpace(_comments.Text))
            {
                MessageBox.Show(this, "Укажите «Комментарии» линии (например АПС): в маркировке участвуют только устройства и трассы с этим значением.", Title);
                _comments.Focus();
                return;
            }
            if (!int.TryParse(_start.Text.Trim(), out int start))
            {
                MessageBox.Show(this, "Начальный номер должен быть числом.", Title);
                return;
            }
            Request = new MarkDevicesRequest
            {
                System = Value(_system),
                Order = Value(_order),
                Building = _building.Text,
                Controller = _controller.Text,
                Line = _line.Text,
                Comments = _comments.Text,
                StartNumber = start,
            };
            SaveLast();
            DialogResult = true;
        }

        private static string Value(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

        private static ComboBox Combo(params string[] items)
        {
            var box = new ComboBox();
            foreach (string item in items)
            {
                string[] parts = item.Split('|');
                box.Items.Add(new ComboBoxItem { Content = parts[0], Tag = parts[1] });
            }
            box.SelectedIndex = 0;
            return box;
        }
    }

    /// <summary>Этажи уровней без номера в имени («Тех. пространство», «Кровля»): у каждого уровня свой номер этажа.</summary>
    internal sealed class LevelFloorsWindow : Window
    {
        private readonly Document _doc;
        private readonly List<(string level, TextBox box)> _rows = new List<(string, TextBox)>();

        public static List<Level> UnnumberedLevels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l.Name ?? "", @"\d"))
                .OrderBy(l => l.Elevation).ToList();

        public LevelFloorsWindow(Document doc)
        {
            _doc = doc;
            Title = "Этажи уровней без номера";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 640;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "В имени этих уровней нет номера этажа. Укажите этаж числом — он запишется устройствам на уровне. Пустое поле — этаж не задан. Значения запоминаются для этого проекта."
                       + (string.IsNullOrEmpty(doc.PathName) ? " Проект ещё не сохранён: значения привяжутся к его временному имени — сохраните проект и задайте их заново." : ""),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            List<Level> levels = UnnumberedLevels(doc);
            foreach (Level level in levels)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = new TextBlock
                {
                    Text = $"{level.Name}  ({level.Elevation * 0.3048:0.###} м)",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 8, 2),
                };
                var box = new TextBox { Text = MarkingSettings.LevelFloor(doc, level.Name) ?? "", Margin = new Thickness(0, 2, 0, 2), TextAlignment = TextAlignment.Right };
                System.Windows.Controls.Grid.SetRow(name, row);
                System.Windows.Controls.Grid.SetRow(box, row);
                System.Windows.Controls.Grid.SetColumn(box, 1);
                grid.Children.Add(name);
                grid.Children.Add(box);
                _rows.Add((level.Name, box));
            }
            if (levels.Count == 0) panel.Children.Add(new TextBlock { Text = "В проекте нет уровней без номера в имени.", Foreground = System.Windows.Media.Brushes.DimGray });
            panel.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var save = new Button { Content = "Сохранить", IsDefault = true, Padding = new Thickness(12, 4, 12, 4) };
            save.Click += (s, e) => Save();
            var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
        }

        private void Save()
        {
            var values = new Dictionary<string, string>();
            foreach (var (level, box) in _rows)
            {
                string text = box.Text.Trim();
                if (text.Length > 0 && !int.TryParse(text, out _))
                {
                    MessageBox.Show(this, $"Этаж уровня «{level}» должен быть целым числом (журнал не принимает этаж текстом).", Title);
                    box.Focus();
                    return;
                }
                values[level] = text;
            }
            var numbered = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => System.Text.RegularExpressions.Regex.Match(l.Name ?? "", @"-?\d+"))
                .Where(m => m.Success).Select(m => int.Parse(m.Value).ToString()).ToList();
            List<string> clash = values.Where(v => v.Value.Length > 0 && numbered.Contains(int.Parse(v.Value).ToString())).Select(v => $"«{v.Key}» = {v.Value}").ToList();
            if (clash.Count > 0 && MessageBox.Show(this,
                    "Этот номер уже есть в имени другого уровня:\n" + string.Join("\n", clash) +
                    "\n\nКабельный журнал по номеру этажа отнесёт устройства к уровню с этим номером в имени. Сохранить всё равно?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            MarkingSettings.SaveLevelFloors(_doc, values);
            DialogResult = true;
        }
    }
}
