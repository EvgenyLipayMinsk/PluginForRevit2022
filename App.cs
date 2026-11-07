using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace PluginForRevit2022
{
    internal class App : IExternalApplication
    {
        public Result OnShutdown(UIControlledApplication application)
        {
            Mcp.McpHost.Stop(); // освободить порт при закрытии Revit
            return Result.Succeeded;
        }

        public Result OnStartup(UIControlledApplication application)
        {
            //Создание новой панели AerBIM Manager
            string tabName = "AerBIM Manager";
            string panelNameWorkTools = "Work Tools";
            application.CreateRibbonTab(tabName);

            var workPanel = application.CreateRibbonPanel(tabName, panelNameWorkTools); //Создание подпанели Work Tools

            string curAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location; //получение пути сборки до .dll

            #region Создание Remove Parameters
            PushButtonData pbdRemoveParameters = new PushButtonData("Remove Parameters", "Remove\rParameters", curAssembly, "PluginForRevit2022.CmdRemoveParameters");
            pbdRemoveParameters.ToolTip = "Remove parameters";
            pbdRemoveParameters.LongDescription = "This is Long description of remove parameters";
            pbdRemoveParameters.LargeImage = Imaging.CreateBitmapSourceFromHIcon(Properties.Resources.Remove_Parameters.GetHicon(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            workPanel.AddItem(pbdRemoveParameters);
            #endregion

            #region Панель MCP: соединение с ИИ-клиентом
            var mcpPanel = application.CreateRibbonPanel(tabName, "MCP");

            PushButtonData pbdMcpToggle = new PushButtonData("MCP Toggle", "MCP\rвыкл", curAssembly, "PluginForRevit2022.CmdMcpToggle");
            var mcpToggleButton = (PushButton)mcpPanel.AddItem(pbdMcpToggle);

            PushButtonData pbdMcpReset = new PushButtonData("MCP Reset", "Сброс\rMCP", curAssembly, "PluginForRevit2022.CmdMcpReset");
            pbdMcpReset.ToolTip = "Разорвать текущее MCP-подключение и заново открыть порт " + Mcp.McpHost.Port + ".";
            pbdMcpReset.LongDescription = "Используйте, если ИИ-клиент не может подключиться или соединение зависло. Если MCP был выключен, кнопка его включит.";
            pbdMcpReset.LargeImage = Mcp.McpHost.CreateIndicator(System.Windows.Media.Color.FromRgb(214, 120, 30), 32);
            mcpPanel.AddItem(pbdMcpReset);

            Mcp.McpHost.Initialize(mcpToggleButton);
            #endregion

            return Result.Succeeded;
        }
    }
}
