using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PluginForRevit2022
{
    /// <summary>
    /// Окно коллизий Navisworks (немодальное: модель можно смотреть и править, не закрывая его).
    /// Без GuardedCommand: параметры плагина (ФОП) для коллизий не нужны; в модели меняется только свой 3D-вид.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdClashNavigator : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                message = "Откройте проект.";
                return Result.Failed;
            }
            try
            {
                Core.PluginLog.Info("command.start", $"{nameof(CmdClashNavigator)} | {doc.Title}");
                UI.ClashNavigatorWindow.Open(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Core.PluginLog.Warn("command.error", $"{nameof(CmdClashNavigator)} | {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
