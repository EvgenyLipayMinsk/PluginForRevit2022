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

        /// <param name="ids">null — любые устройства.</param>
        /// <param name="inside">true — только из набора; false — только вне набора.</param>
        public LineDeviceFilter(HashSet<long> ids = null, bool inside = true)
        {
            _ids = ids;
            _inside = inside;
        }

        public bool AllowElement(Element element) =>
            element is FamilyInstance && element.Category != null && Categories.Contains(RevitIds.IdOf(element.Category.Id)) &&
            (_ids == null || _ids.Contains(RevitIds.IdOf(element.Id)) == _inside);

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>Общий шаг команд правки линии: окно выбора линии, показывается при каждом запуске.</summary>
    internal static class LineChoice
    {
        private static string _lastKey;

        /// <summary>Возвращает выбранную линию или null при отмене.</summary>
        public static LineInfo Ask(UIApplication uiApp, string title, bool offerStart, out bool atStart)
        {
            atStart = false;
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

        public LineInfo Selected => _table.SelectedItem as LineInfo;
        public bool PickOnPlan { get; private set; }
        public bool AtStart => _atStart.IsChecked == true;

        public LinePickerWindow(string title, List<LineInfo> lines, bool offerStart)
        {
            Title = title + " — выбор линии";
            Width = 620;
            Height = 380;
            MinWidth = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            foreach (var (header, property) in new[] { ("Система", "System"), ("Корпус", "Building"), ("Прибор", "Controller"), ("Линия", "Line"), ("Устройств", "Devices"), ("Занято", "Occupied") })
            {
                _table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            }
            _table.ItemsSource = lines;
            _table.SelectedIndex = 0;
            _table.MouseDoubleClick += (s, e) => Accept(false);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(Action("Указать на плане", () => Accept(true)));
            Button ok = Action("Далее", () => Accept(false));
            ok.IsDefault = true;
            buttons.Children.Add(ok);

            var bottom = new StackPanel();
            if (offerStart) bottom.Children.Add(_atStart);
            bottom.Children.Add(buttons);

            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hint = new TextBlock { Text = "Выберите линию, с которой работаем.", Margin = new Thickness(0, 0, 0, 8) };
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

        private void Accept(bool pickOnPlan)
        {
            if (!pickOnPlan && Selected == null) return;
            PickOnPlan = pickOnPlan;
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
                LineInfo line = LineChoice.Ask(commandData.Application, Caption, true, out bool atStart);
                if (line == null) return Result.Cancelled;
                HashSet<long> members = DeviceLineOperations.MemberIds(doc, line.Key);

                SessionWindows.Inform("line-add-hint", Caption, atStart
                    ? "Кликните новое устройство: оно встанет первым номером линии."
                    : "Шаг 1: кликните устройство линии, после которого встанет новое.\n\nШаг 2: кликните новое устройство.");
                long? after = atStart
                    ? (long?)null
                    : RevitIds.IdOf(uiDoc.Selection.PickObject(ObjectType.Element, new LineDeviceFilter(members), "Устройство линии, после которого вставить").ElementId);
                long device = RevitIds.IdOf(uiDoc.Selection.PickObject(ObjectType.Element, new LineDeviceFilter(members, false), "Новое устройство").ElementId);

                Dictionary<string, object> preview = DeviceLineOperations.Add(doc, line.Key, after, device, true);
                var warnings = (List<string>)preview["warnings"];
                string text = $"Линия: {line.Title}\nНомер устройства: {preview["number"]}\nСдвинется номеров: {preview["shifted"]}\nЗанято адресов на линии: {preview["lineAddresses"]} из {preview["lineCapacity"]}" +
                              (warnings.Count > 0 ? "\n\nВнимание:\n" + string.Join("\n", warnings) : "");
                if (!SessionWindows.Confirm("line-add-confirm", Caption, text + "\n\nДобавить?")) return Result.Cancelled;
                SessionWindows.Inform("line-add-result", Caption, (string)DeviceLineOperations.Add(doc, line.Key, after, device, false)["summary"]);
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
                LineInfo line = LineChoice.Ask(commandData.Application, Caption, false, out _);
                if (line == null) return Result.Cancelled;
                HashSet<long> members = DeviceLineOperations.MemberIds(doc, line.Key);

                SessionWindows.Inform("line-remove-hint", Caption,
                    "Выделите устройства линии и нажмите «Готово» на панели параметров. Сами элементы модели не удаляются: снимается только маркировка.");
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
