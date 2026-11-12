using System.Collections.Generic;
using Autodesk.Revit.UI;
using PluginForRevit2022.UI;

namespace PluginForRevit2022
{
    internal class App : IExternalApplication
    {
        public Result OnShutdown(UIControlledApplication application)
        {
            Mcp.McpHost.Stop(); // освободить порт при закрытии Revit
            Core.PluginLog.Info("revit.shutdown");
            return Result.Succeeded;
        }

        public Result OnStartup(UIControlledApplication application)
        {
            Core.PluginLog.Cleanup();
            Core.PluginLog.Info("revit.startup", $"{application.ControlledApplication.VersionName} {application.ControlledApplication.VersionBuild} | плагин " +
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version + " | " + System.Environment.MachineName);

            // Лента в стиле Revit: линейные значки, главные команды — крупные, второстепенные — мелкие стопкой
            string tabName = "AerBIM Manager";
            application.CreateRibbonTab(tabName);
            string asm = System.Reflection.Assembly.GetExecutingAssembly().Location;

            PushButtonData Button(string id, string text, string command, string icon, string tip, string details = null)
            {
                var data = new PushButtonData(id, text, asm, "PluginForRevit2022." + command)
                {
                    LargeImage = RibbonIcons.Large(icon),
                    Image = RibbonIcons.Small(icon),
                    ToolTip = tip,
                };
                if (details != null) data.LongDescription = details;
                return data;
            }

            #region Параметры
            var parametersPanel = application.CreateRibbonPanel(tabName, "Параметры");
            parametersPanel.AddStackedItems(
                Button("Add Shared Parameters", "Параметры из ФОП", "GuardAddSharedParameters", "fop",
                    "Добавить общие параметры из ФОП в проект (с привязкой к категориям) или в открытое семейство.",
                    "Выберите параметры, категории, тип или экземпляр и группу. Сначала «Предпросмотр», затем «Добавить». То же доступно ИИ через MCP (add_shared_parameters)."),
                Button("Remove Parameters", "Удалить параметры", "GuardRemoveParameters", "removeParams",
                    "Удалить параметры проекта: выберите их в списке."));
            #endregion

            #region Кабельный журнал
            var journalPanel = application.CreateRibbonPanel(tabName, "Кабельный журнал");
            // Одна кнопка с меню: наверху последняя использованная система
            var journal = (SplitButton)journalPanel.AddItem(new SplitButtonData("Кабельный журнал", "Кабельный\rжурнал"));
            foreach (var (id, text, command, tip) in new[]
            {
                ("КЖ АПС", "Журнал\rАПС", "GuardJournalAps", "Кабельный журнал АПС: кликните элемент с нужными «Комментариями» (например прибор)."),
                ("КЖ СОТС все", "Журнал СОТС\rвсе", "GuardJournalSecurityAll", "Кабельный журнал СОТС по всем контроллерам ARK.*."),
                ("КЖ СОТС по параметру", "Журнал СОТС\rпо «Комментариям»", "GuardJournalSecurityByParam", "Кабельный журнал СОТС по значению «Комментарии» выбранного элемента."),
                ("КЖ СОУЭ", "Журнал\rСОУЭ", "GuardJournalSouye", "Кабельный журнал СОУЭ по значению «Комментарии» выбранного элемента."),
            })
            {
                journal.AddPushButton(Button(id, text, command, "journal", tip,
                    "Длины считаются по трассам; участки, где трасс не хватило и длина взята по прямой, подсвечиваются жёлтым. " +
                    "Если элементы не попали в журнал, плагин перечислит их и причину."));
            }
            #endregion

            #region Маркировка
            var markingPanel = application.CreateRibbonPanel(tabName, "Маркировка");
            markingPanel.AddItem(Button("Создать маркировку", "Маркировка", "GuardMarkDevices", "marking",
                "Заполнить параметры устройств линии: номера по ходу кабеля от прибора или кликами по порядку.",
                "Сначала кликните прибор, затем выделите устройства линии. Окно подтверждения покажет, что будет записано."));
            markingPanel.AddStackedItems(
                Button("Добавить устройство", "Добавить устройство", "GuardAddDeviceToLine", "addDevice",
                    "Вставить устройство в размеченную линию после выбранного; следующие номера сдвигаются."),
                Button("Удалить устройство", "Удалить устройство", "GuardRemoveDeviceFromLine", "removeDevice",
                    "Снять маркировку с устройств и закрыть пропуски в номерах; этаж и ЗКПС остаются."),
                Button("Настройки маркировки", "Настройки маркировки", "GuardMarkingSettings", "settings",
                    "Какое БЦО применять к семействам, какие семейства участвуют в линии и запасы кабеля."));
            #endregion

            #region Размещение оборудования
            var placementPanel = application.CreateRibbonPanel(tabName, "Размещение оборудования");
            placementPanel.AddItem(Button("Извещатели", "Извещатели", "GuardPlaceDetectors", "detector",
                "Разместить дымовые или тепловые извещатели в помещении кликом на плане (СП 484 с Изменением № 1)."));
            #endregion

            #region Поддержка
            var supportPanel = application.CreateRibbonPanel(tabName, "Поддержка");
            var helpData = new PulldownButtonData("Инструкция КЖ", "Инструкция")
            {
                LargeImage = RibbonIcons.Large("help"),
                Image = RibbonIcons.Small("help"),
                ToolTip = "Что и в каком порядке сделать и какие параметры заполнить, чтобы получился кабельный журнал.",
            };
            IList<RibbonItem> support = supportPanel.AddStackedItems(helpData,
                Button("Сохранить лог", "Сохранить лог", "CmdSaveLog", "log",
                    "Сохранить журнал действий плагина и сведения о компьютере в ZIP-архив для разработчика."));
            var help = (PulldownButton)support[0];
            foreach (var (id, text, command) in new[]
            {
                ("Инструкция АПС", "АПС", "CmdInstructionAps"),
                ("Инструкция СОТС", "СОТС", "CmdInstructionSecurity"),
                ("Инструкция СОУЭ", "СОУЭ", "CmdInstructionSouye"),
            })
            {
                help.AddPushButton(Button(id, text, command, "help", $"Инструкция: кабельный журнал {text}."));
            }
            #endregion

            #region MCP: соединение с ИИ-клиентом
            var mcpPanel = application.CreateRibbonPanel(tabName, "MCP");
            var toggleData = new PushButtonData("MCP Toggle", "MCP выкл", asm, "PluginForRevit2022.CmdMcpToggle");
            IList<RibbonItem> mcp = mcpPanel.AddStackedItems(toggleData,
                Button("MCP Reset", "Сброс MCP", "CmdMcpReset", "reset",
                    "Разорвать текущее MCP-подключение и заново открыть порт " + Mcp.McpHost.Port + ".",
                    "Используйте, если ИИ-клиент не может подключиться или соединение зависло. Если MCP был выключен, кнопка его включит."));
            Mcp.McpHost.Initialize((PushButton)mcp[0]);
            #endregion

            return Result.Succeeded;
        }
    }
}
