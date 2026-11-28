using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Mcp;

namespace PluginForRevit2022
{
    /// <summary>Кнопка «MCP вкл/выкл»: открывает или закрывает порт для MCP-сервера.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdMcpToggle : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (McpHost.IsRunning)
                {
                    McpHost.Stop();
                }
                else
                {
                    McpHost.Start();
                }
                TaskDialog.Show("AerBIM MCP", McpHost.GetStatusText());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                McpLog.Error("Не удалось переключить MCP", ex);
                TaskDialog.Show("AerBIM MCP", ex.Message + "\n\nЛог: " + McpLog.LogPath);
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Кнопка «Сброс MCP»: разрывает зависшее подключение и заново открывает порт.
    /// Если MCP был выключен — просто включает его.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdMcpReset : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                McpHost.Reset();
                TaskDialog.Show("AerBIM MCP", "Соединение сброшено, порт открыт заново.\n\n" + McpHost.GetStatusText());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                McpLog.Error("Не удалось сбросить MCP", ex);
                TaskDialog.Show("AerBIM MCP", ex.Message + "\n\nЛог: " + McpLog.LogPath);
                return Result.Failed;
            }
        }
    }
}
