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

            var window = new MarkDevicesWindow(DeviceLineOperations.ListLines(doc).Select(l => l.Title).ToList());
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

            try
            {
                if (request.Order == "route")
                {
                    SessionWindows.Inform("mark-route-hint", "Маркировка",
                        $"Маршрут строится по трассам этой линии: у семейств кабеля в «ADSK_Группирование» должно стоять …АЛС.{request.Controller}.{request.Line} " +
                        "(например СПС_АЛС.1.2 — прибор 1, линия 2). Если таких трасс нет, берутся трассы с тем же значением «Комментарии», что в окне.\n\n" +
                        "Шаг 1: кликните прибор (категория «Электрооборудование»), от которого идёт линия.\n\n" +
                        "Шаг 2: выделите устройства линии и нажмите «Готово» на панели параметров вверху окна Revit.\n\n" +
                        "Выбрать можно только семейства с галочкой этой системы в настройках маркировки.");
                    Reference controller = uiDoc.Selection.PickObject(ObjectType.Element, new CategoryFilter(null, BuiltInCategory.OST_ElectricalEquipment), "Выберите прибор, от которого идёт линия");
                    request.ControllerElementId = RevitIds.IdOf(controller.ElementId);
                    IList<Reference> picked = uiDoc.Selection.PickObjects(ObjectType.Element, DeviceFilter(request.System), "Выберите устройства шлейфа и нажмите «Готово»");
                    request.Ids = picked.Select(r => RevitIds.IdOf(r.ElementId)).ToList();
                }
                else
                {
                    SessionWindows.Inform("mark-manual-hint", "Маркировка",
                        "Шаг 1: кликните прибор (категория «Электрооборудование»), к которому подключена линия. " +
                        "В него запишутся данные для кабельного журнала.\n\n" +
                        "Шаг 2: кликайте устройства по порядку номеров. Чтобы закончить выбор, нажмите Esc.\n\n" +
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
        /// временно закрашиваются зелёным на текущем виде; после выбора окраска откатывается
        /// (в проекте не остаётся), а устройства остаются выделенными.
        /// </summary>
        private static List<long> PickInOrder(UIDocument uiDoc, ISelectionFilter filter)
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
            var green = new Color(0, 176, 80);
            var settings = new OverrideGraphicSettings()
                .SetProjectionLineColor(green)
                .SetProjectionLineWeight(6);
            if (solid != null)
            {
                settings.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(green);
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
        private readonly ComboBox _standard = Combo("ADSK_Этаж|adsk", "О_Этаж|eom");
        private readonly TextBox _building = new TextBox();
        private readonly TextBox _controller = new TextBox();
        private readonly TextBox _bco = new TextBox();
        private readonly TextBox _line = new TextBox();
        private readonly TextBox _comments = new TextBox();
        private readonly TextBox _floor = new TextBox();
        private readonly TextBox _start = new TextBox { Text = "1" };

        public MarkDevicesRequest Request { get; private set; }

        public string SystemTitle => (string)((ComboBoxItem)_system.SelectedItem).Content;

        public MarkDevicesWindow(List<string> existingLines)
        {
            Title = "Маркировка устройств";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(12) };
            if (existingLines.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Линии в проекте:\n" + string.Join("\n", existingLines.Take(8)) + (existingLines.Count > 8 ? $"\n… ещё {existingLines.Count - 8}" : ""),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.DimGray,
                });
            }
            void Row(string label, Control control)
            {
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
                panel.Children.Add(control);
            }
            Row("Система", _system);
            Row("Порядок номеров", _order);
            Row("Корпус (ARBM_Корпус; для СОТС не нужен)", _building);
            Row("Номер прибора", _controller);
            Row("БЦО (если для семейства не задано в настройках)", _bco);
            Row("Номер линии", _line);
            Row("Комментарии (значение для отбора в журнале)", _comments);
            Row("Этаж для уровней без номера в имени (например «Тех. Пространство»)", _floor);
            Row("Параметр этажа для СОУЭ", _standard);
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
                Select(_standard, last, "standard");
                Put(_building, last, "building");
                Put(_controller, last, "controller");
                Put(_bco, last, "bco");
                Put(_line, last, "line");
                Put(_comments, last, "comments");
                Put(_floor, last, "floor");
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
                    ["standard"] = Value(_standard),
                    ["building"] = _building.Text,
                    ["controller"] = _controller.Text,
                    ["bco"] = _bco.Text,
                    ["line"] = _line.Text,
                    ["comments"] = _comments.Text,
                    ["floor"] = _floor.Text,
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

        private void Accept()
        {
            if (!int.TryParse(_start.Text.Trim(), out int start))
            {
                MessageBox.Show(this, "Начальный номер должен быть числом.", Title);
                return;
            }
            Request = new MarkDevicesRequest
            {
                System = Value(_system),
                Order = Value(_order),
                ParameterStandard = Value(_standard),
                Building = _building.Text,
                Controller = _controller.Text,
                Bco = _bco.Text,
                Line = _line.Text,
                Comments = _comments.Text,
                Floor = _floor.Text,
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
}
