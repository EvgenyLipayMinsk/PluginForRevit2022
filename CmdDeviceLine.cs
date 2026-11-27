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
using Grid = System.Windows.Controls.Grid;

namespace PluginForRevit2022
{
    /// <summary>Выбор устройств: только из указанного набора или, наоборот, все устройства кроме него.</summary>
    internal sealed class LineDeviceFilter : ISelectionFilter
    {
        private static readonly HashSet<long> Categories = new HashSet<long>
        {
            (long)BuiltInCategory.OST_DataDevices, (long)BuiltInCategory.OST_FireAlarmDevices, (long)BuiltInCategory.OST_NurseCallDevices,
        };
        private readonly HashSet<long> _ids;
        private readonly bool _inside;
        private readonly Func<Element, bool> _extra;

        /// <param name="ids">null — любые устройства.</param>
        /// <param name="inside">true — только из набора; false — только вне набора.</param>
        /// <param name="extra">Дополнительное условие (например, комментарий линии и галочка системы).</param>
        public LineDeviceFilter(HashSet<long> ids = null, bool inside = true, Func<Element, bool> extra = null)
        {
            _ids = ids;
            _inside = inside;
            _extra = extra;
        }

        public bool AllowElement(Element element) =>
            element is FamilyInstance && element.Category != null && Categories.Contains(RevitIds.IdOf(element.Category.Id)) &&
            (_ids == null || _ids.Contains(RevitIds.IdOf(element.Id)) == _inside) && (_extra == null || _extra(element));

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>Общий шаг команд правки линии: окно выбора линии, показывается при каждом запуске.</summary>
    internal static class LineChoice
    {
        private static string _lastKey;

        /// <summary>Возвращает выбранную линию или null при отмене.</summary>
        public static LineInfo Ask(UIApplication uiApp, string title, bool offerStart, out bool atStart, out bool atEnd)
        {
            atStart = false;
            atEnd = false;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            List<LineInfo> lines = DeviceLineOperations.ListLines(uiDoc.Document);
            if (lines.Count == 0)
            {
                TaskDialog.Show(title, "В проекте нет размеченных линий. Сначала выполните «Создать маркировку».");
                return null;
            }

            var window = new LinePickerWindow(title, lines.OrderByDescending(l => l.Key == _lastKey).ToList(), offerStart);
            new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
            if (window.ShowDialog() != true) return null;
            atStart = window.AtStart;
            atEnd = window.AtEnd;

            LineInfo chosen = window.Selected;
            if (window.PickOnPlan)
            {
                Reference picked = uiDoc.Selection.PickObject(ObjectType.Element, new LineDeviceFilter(), "Кликните любое устройство нужной линии");
                string key = DeviceLineOperations.KeyOf(uiDoc.Document.GetElement(picked));
                chosen = lines.FirstOrDefault(l => l.Key == key);
                if (chosen == null)
                {
                    TaskDialog.Show(title, "Это устройство не размечено: у него нет линии.");
                    return null;
                }
            }
            _lastKey = chosen.Key;
            return chosen;
        }
    }

    /// <summary>
    /// Сортировка таблиц линий: по выбранному столбцу, а внутри — всегда система, корпус, прибор, линия (числом).
    /// Иначе после сортировки по прибору линии 1 и 2 одного прибора шли бы вразнобой.
    /// </summary>
    internal static class LineTableSort
    {
        private static readonly string[] Natural = { "System", "Building", "ControllerOrder", "Controller", "LineOrder", "Line" };

        public static void Attach(DataGrid table)
        {
            table.Sorting += (s, e) =>
            {
                e.Handled = true;
                string key = e.Column.SortMemberPath;
                var direction = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
                    ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
                foreach (DataGridColumn c in table.Columns) c.SortDirection = null;
                e.Column.SortDirection = direction;
                System.ComponentModel.ICollectionView view = System.Windows.Data.CollectionViewSource.GetDefaultView(table.ItemsSource);
                if (view == null) return;
                using (view.DeferRefresh())
                {
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new System.ComponentModel.SortDescription(key, direction));
                    foreach (string n in Natural.Where(n => n != key))
                        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(n, System.ComponentModel.ListSortDirection.Ascending));
                }
            };
        }
    }

    internal sealed class LinePickerWindow : Window
    {
        private readonly DataGrid _table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        private readonly System.Windows.Controls.CheckBox _atStart = new System.Windows.Controls.CheckBox { Content = "Вставить в начало линии (первым номером)", Margin = new Thickness(0, 8, 0, 0) };
        private readonly System.Windows.Controls.CheckBox _atEnd = new System.Windows.Controls.CheckBox { Content = "Добавить в конец линии (последним номером)", Margin = new Thickness(0, 4, 0, 0) };

        public LineInfo Selected => _table.SelectedItem as LineInfo;
        public bool PickOnPlan { get; private set; }
        public bool AtStart => _atStart.IsChecked == true;
        public bool AtEnd => _atEnd.IsChecked == true;

        public LinePickerWindow(string title, List<LineInfo> lines, bool offerStart)
        {
            UI.AerBimAppearance.Apply(this);
            Title = title + " — выбор линии";
            Width = 620;
            Height = 380;
            MinWidth = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            foreach (var (header, property, sort) in new[]
            {
                ("Система", "System", "System"), ("Корпус", "Building", "Building"), ("Прибор", "Controller", "ControllerOrder"),
                ("Линия", "Line", "LineOrder"), ("Комментарии", "Comment", "Comment"), ("Устройств", "Devices", "Devices"), ("Занято", "Occupied", "Devices"),
            })
            {
                _table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(property), SortMemberPath = sort, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            }
            _table.ItemsSource = lines;
            LineTableSort.Attach(_table);
            // Без выбора по умолчанию: строка выбрана — работаем с ней, не выбрана — линию укажут кликом по устройству
            _table.SelectedIndex = -1;
            _table.MouseDoubleClick += (s, e) => { if (Selected != null) Accept(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            Button ok = Action("Указать на плане", Accept);
            ok.IsDefault = true;
            UI.AerBimAppearance.Primary(this, ok);
            buttons.Children.Add(ok);

            var bottom = new StackPanel();
            if (offerStart)
            {
                // Начало и конец взаимоисключающие; без галочек — после устройства, указанного кликом
                _atStart.Checked += (s, e) => _atEnd.IsChecked = false;
                _atEnd.Checked += (s, e) => _atStart.IsChecked = false;
                bottom.Children.Add(_atStart);
                bottom.Children.Add(_atEnd);
            }
            bottom.Children.Add(buttons);

            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hint = new TextBlock { Text = "Выберите линию в таблице и нажмите «Указать на плане». Если строку не выбирать, линию определит клик по любому её устройству на плане.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(_table, 1);
            Grid.SetRow(bottom, 2);
            layout.Children.Add(hint);
            layout.Children.Add(_table);
            layout.Children.Add(bottom);
            Content = layout;
        }

        private static Button Action(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            button.Click += (s, e) => action();
            return button;
        }

        private void Accept()
        {
            PickOnPlan = Selected == null; // строка не выбрана — линию определит клик по устройству
            DialogResult = true;
        }
    }

    /// <summary>Вставка устройства в выбранную линию.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdAddDeviceToLine : IExternalCommand
    {
        private const string Caption = "Добавить устройство";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            try
            {
                LineInfo line = LineChoice.Ask(commandData.Application, Caption, true, out bool atStart, out bool atEnd);
                if (line == null) return Result.Cancelled;
                HashSet<long> members = DeviceLineOperations.MemberIds(doc, line.Key);

                SessionWindows.Inform(atStart || atEnd ? "line-add-hint-edge" : "line-add-hint", Caption,
                    (atStart ? "Кликайте новые устройства по порядку: они встанут в начало линии."
                    : atEnd ? "Кликайте новые устройства по порядку: они встанут в конец линии."
                    : "Шаг 1: кликните устройство линии, после которого встанут новые.\n\nШаг 2: кликайте новые устройства по порядку.") +
                    "\n\nКаждое выбранное устройство становится синим. Номера пойдут в порядке кликов. Чтобы закончить, нажмите Esc.");
                long? after = atStart ? (long?)null
                    : atEnd ? DeviceLineOperations.LastMemberId(doc, line.Key)
                    : RevitIds.IdOf(uiDoc.Selection.PickObject(ObjectType.Element, new LineDeviceFilter(members), "Устройство линии, после которого вставить").ElementId);
                // Несколько устройств: клики по порядку, каждое кликнутое синее, Esc — конец
                string lineComment = line.Comment ?? "";
                string system = line.Key.StartsWith("aps|") ? "aps" : line.System == "СОУЭ" ? "souye" : "security";
                MarkingSettings settings = MarkingSettings.Load();
                // Кликнуть можно только то, что линия примет: с её «Комментарии» и с галочкой системы в настройках
                Func<Element, bool> fits = e => (lineComment.Length == 0 || DeviceMarkingOperations.HasComment(e, lineComment))
                                               && e is FamilyInstance fi && settings.IsInLine(fi.Symbol.FamilyName, system);
                List<long> devices = CmdMarkDevices.PickInOrder(uiDoc, new LineDeviceFilter(members, false, fits));
                if (devices.Count == 0) return Result.Cancelled;

                Dictionary<string, object> preview = DeviceLineOperations.Add(doc, line.Key, after, devices, true);
                var warnings = (List<string>)preview["warnings"];
                string text = $"Линия: {line.Title}\nДобавить устройств: {preview["added"]}\nНомера: {preview["numbers"]}\nСдвинется номеров: {preview["shifted"]}\nЗанято адресов на линии: {preview["lineAddresses"]} из {preview["lineCapacity"]}" +
                              (warnings.Count > 0 ? "\n\nВнимание:\n" + string.Join("\n", warnings) : "");
                if (!SessionWindows.Confirm("line-add-confirm", Caption, text + "\n\nДобавить?")) return Result.Cancelled;
                SessionWindows.Inform("line-add-result", Caption, (string)DeviceLineOperations.Add(doc, line.Key, after, devices, false)["summary"]);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Caption, ex.Message);
                return Result.Failed;
            }
        }
    }

    /// <summary>Снятие устройств с выбранной линии со сдвигом номеров.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdRemoveDeviceFromLine : IExternalCommand
    {
        private const string Caption = "Удалить устройство";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            try
            {
                LineInfo line = LineChoice.Ask(commandData.Application, Caption, false, out _, out _);
                if (line == null) return Result.Cancelled;
                HashSet<long> members = DeviceLineOperations.MemberIds(doc, line.Key);

                SessionWindows.Inform("line-remove-hint", Caption,
                    "Выделите устройства линии и нажмите «Готово» на панели параметров. Сами элементы модели не удаляются: снимаются маркировка и «Комментарии» (устройство выходит из журнала), этаж и ЗКПС остаются.");
                List<long> ids = uiDoc.Selection.PickObjects(ObjectType.Element, new LineDeviceFilter(members), "Устройства линии, которые снять")
                    .Select(r => RevitIds.IdOf(r.ElementId)).ToList();
                if (ids.Count == 0) return Result.Cancelled;

                Dictionary<string, object> preview = DeviceLineOperations.Remove(doc, line.Key, ids, true);
                string text = $"Линия: {line.Title}\nСнять с линии устройств: {preview["removed"]}\nСдвинется номеров: {preview["shifted"]}\nОстанется адресов на линии: {preview["lineAddresses"]} из {preview["lineCapacity"]}";
                if (!SessionWindows.Confirm("line-remove-confirm", Caption, text + "\n\nСнять маркировку?")) return Result.Cancelled;
                SessionWindows.Inform("line-remove-result", Caption, (string)DeviceLineOperations.Remove(doc, line.Key, ids, false)["summary"]);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Caption, ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)] public class GuardAddDeviceToLine : GuardedCommand<CmdAddDeviceToLine> { }
    [Transaction(TransactionMode.Manual)] public class GuardRemoveDeviceFromLine : GuardedCommand<CmdRemoveDeviceFromLine> { }
}
