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
            PushButtonData pbdRemoveParameters = new PushButtonData("Remove Parameters", "Remove\rParameters", curAssembly, "PluginForRevit2022.GuardRemoveParameters");
            pbdRemoveParameters.ToolTip = "Remove parameters";
            pbdRemoveParameters.LongDescription = "This is Long description of remove parameters";
            pbdRemoveParameters.LargeImage = Imaging.CreateBitmapSourceFromHIcon(Properties.Resources.Remove_Parameters.GetHicon(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            workPanel.AddItem(pbdRemoveParameters);
            #endregion

            #region Параметры из ФОП
            PushButtonData pbdAddShared = new PushButtonData("Add Shared Parameters", "Параметры\rиз ФОП", curAssembly, "PluginForRevit2022.GuardAddSharedParameters");
            pbdAddShared.ToolTip = "Добавить общие параметры из ФОП в проект (с привязкой к категориям) или в открытое семейство.";
            pbdAddShared.LongDescription = "Выберите параметры, категории, тип или экземпляр и группу. Сначала «Предпросмотр», затем «Добавить». То же доступно ИИ через MCP (add_shared_parameters).";
            pbdAddShared.LargeImage = Mcp.McpHost.CreateIndicator(System.Windows.Media.Color.FromRgb(43, 87, 154), 32, "ФОП");
            workPanel.AddItem(pbdAddShared);
            #endregion

            #region Кабельный журнал
            var journalPanel = application.CreateRibbonPanel(tabName, "Кабельный журнал");
            var journalColor = System.Windows.Media.Color.FromRgb(46, 125, 50);
            foreach (var (id, text, command, tip) in new[]
            {
                ("КЖ СОТС все", "СОТС\rвсе", "GuardJournalSecurityAll", "Кабельный журнал СОТС по всем контроллерам."),
                ("КЖ СОТС по параметру", "СОТС\rпо параметру", "GuardJournalSecurityByParam", "Кабельный журнал СОТС по значению «Комментарии» выбранного элемента."),
                ("КЖ АПС", "АПС", "GuardJournalAps", "Кабельный журнал АПС по значению «Комментарии» выбранного элемента."),
                ("КЖ СОУЭ", "СОУЭ", "GuardJournalSouye", "Кабельный журнал СОУЭ по значению «Комментарии» выбранного элемента."),
            })
            {
                var button = new PushButtonData(id, text, curAssembly, "PluginForRevit2022." + command);
                button.ToolTip = tip;
                button.LargeImage = Mcp.McpHost.CreateIndicator(journalColor, 32, "КЖ");
                journalPanel.AddItem(button);
            }

            var markIcon = Mcp.McpHost.CreateIndicator(journalColor, 32, "№");
            var markMenuData = new PulldownButtonData("Маркировка", "Маркировка") { LargeImage = markIcon, ToolTip = "Маркировка устройств для кабельного журнала." };
            var markMenu = (PulldownButton)journalPanel.AddItem(markMenuData);
            foreach (var (id, text, command, tip) in new[]
            {
                ("Создать маркировку", "Создать маркировку", "GuardMarkDevices", "Заполнить параметры устройств шлейфа: номера кликами или по трассам."),
                ("Добавить устройство", "Добавить устройство", "GuardAddDeviceToLine", "Вставить устройство в размеченную линию после выбранного."),
                ("Удалить устройство", "Удалить устройство", "GuardRemoveDeviceFromLine", "Снять маркировку с устройств и закрыть пропуски в номерах."),
            })
            {
                var item = new PushButtonData(id, text, curAssembly, "PluginForRevit2022." + command);
                item.ToolTip = tip;
                item.LargeImage = markIcon;
                markMenu.AddPushButton(item);
            }

            var markSettingsButton = new PushButtonData("Настройки маркировки", "Настройки\rмаркировки", curAssembly, "PluginForRevit2022.GuardMarkingSettings");
            markSettingsButton.ToolTip = "Какое БЦО применять к семействам и какие семейства участвуют в линии КЖ.";
            markSettingsButton.LargeImage = Mcp.McpHost.CreateIndicator(journalColor, 32, "БЦО");
            journalPanel.AddItem(markSettingsButton);

            var detectorsButton = new PushButtonData("Извещатели", "Извещатели", curAssembly, "PluginForRevit2022.GuardPlaceDetectors");
            detectorsButton.ToolTip = "Разместить дымовые или тепловые извещатели в помещении кликом на плане (СП 484 с Изменением № 1).";
            detectorsButton.LargeImage = Mcp.McpHost.CreateIndicator(System.Windows.Media.Color.FromRgb(198, 40, 40), 32, "ИП");
            journalPanel.AddItem(detectorsButton);
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
