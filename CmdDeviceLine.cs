using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PluginForRevit2022.Core;

namespace PluginForRevit2022
{
    internal sealed class LineDeviceFilter : ISelectionFilter
    {
        private static readonly HashSet<long> Categories = new HashSet<long>
        {
            (long)BuiltInCategory.OST_DataDevices, (long)BuiltInCategory.OST_FireAlarmDevices, (long)BuiltInCategory.OST_NurseCallDevices,
        };

        public bool AllowElement(Element element) =>
            element is FamilyInstance && element.Category != null && Categories.Contains(RevitIds.IdOf(element.Category.Id));

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>Вставка устройства в размеченную линию после выбранного.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdAddDeviceToLine : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            try
            {
                SessionWindows.Inform("line-add-hint", "Добавить устройство",
                    "Шаг 1: кликните размеченное устройство, после которого встанет новое.\n\nШаг 2: кликните новое устройство.");
                var filter = new LineDeviceFilter();
                long after = RevitIds.IdOf(uiDoc.Selection.PickObject(ObjectType.Element, filter, "Устройство, после которого вставить").ElementId);
                long device = RevitIds.IdOf(uiDoc.Selection.PickObject(ObjectType.Element, filter, "Новое устройство").ElementId);

                Dictionary<string, object> preview = DeviceLineOperations.Add(doc, after, device, true);
                var warnings = (List<string>)preview["warnings"];
                string text = $"Номер устройства: {preview["number"]}\nСдвинется номеров: {preview["shifted"]}\nЗанято адресов на линии: {preview["lineAddresses"]} из {preview["lineCapacity"]}" +
                              (warnings.Count > 0 ? "\n\nВнимание:\n" + string.Join("\n", warnings) : "");
                if (!SessionWindows.Confirm("line-add-confirm", "Добавить устройство", text + "\n\nДобавить?")) return Result.Cancelled;
                SessionWindows.Inform("line-add-result", "Добавить устройство", (string)DeviceLineOperations.Add(doc, after, device, false)["summary"]);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Добавить устройство", ex.Message);
                return Result.Failed;
            }
        }
    }

    /// <summary>Снятие устройств с линии со сдвигом номеров.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdRemoveDeviceFromLine : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            try
            {
                SessionWindows.Inform("line-remove-hint", "Удалить устройство",
                    "Выделите устройства и нажмите «Готово» на панели параметров. Сами элементы модели не удаляются: снимается только маркировка.");
                List<long> ids = uiDoc.Selection.PickObjects(ObjectType.Element, new LineDeviceFilter(), "Устройства, которые снять с линии")
                    .Select(r => RevitIds.IdOf(r.ElementId)).ToList();
                if (ids.Count == 0) return Result.Cancelled;

                Dictionary<string, object> preview = DeviceLineOperations.Remove(doc, ids, true);
                string text = $"Снять с линии устройств: {preview["removed"]}\nСдвинется номеров: {preview["shifted"]}\nЗанято адресов на линии: {preview["lineAddresses"]} из {preview["lineCapacity"]}";
                if (!SessionWindows.Confirm("line-remove-confirm", "Удалить устройство", text + "\n\nСнять маркировку?")) return Result.Cancelled;
                SessionWindows.Inform("line-remove-result", "Удалить устройство", (string)DeviceLineOperations.Remove(doc, ids, false)["summary"]);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Удалить устройство", ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)] public class GuardAddDeviceToLine : GuardedCommand<CmdAddDeviceToLine> { }
    [Transaction(TransactionMode.Manual)] public class GuardRemoveDeviceFromLine : GuardedCommand<CmdRemoveDeviceFromLine> { }
}
