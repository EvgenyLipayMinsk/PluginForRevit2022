using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.UI;

namespace PluginForRevit2022
{
    /// <summary>
    /// Кнопка «Параметры из ФОП»: добавляет общие параметры в проект (с привязкой к категориям)
    /// или в открытое семейство. То же доступно через MCP: add_shared_parameters.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdAddSharedParameters : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            Document doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show("Параметры из ФОП", "Откройте проект или семейство.");
                return Result.Cancelled;
            }

            try
            {
                var window = new AddSharedParametersWindow(doc, uiApp.Application);
                new WindowInteropHelper(window) { Owner = uiApp.MainWindowHandle };
                window.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Параметры из ФОП", ex.Message);
                return Result.Failed;
            }
        }
    }
}
