using System;
using System.Collections.Generic;
using System.Linq;
using ARBM.CableJournalExporter.Commands;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Core;

namespace PluginForRevit2022
{
    /// <summary>Журналирование ручных команд и контекст переключения видов.</summary>
    public abstract class GuardedCommand<TCommand> : IExternalCommand where TCommand : IExternalCommand, new()
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string name = typeof(TCommand).Name;
            string document = commandData.Application.ActiveUIDocument?.Document?.Title ?? "нет документа";
            var timer = System.Diagnostics.Stopwatch.StartNew();
            PluginLog.Info("command.start", $"{name} | {document}");
            try
            {

                Result result;
                ViewSession.Ui = commandData.Application.ActiveUIDocument; // команды на нескольких видах открывают их по очереди
                try
                {
                    result = new TCommand().Execute(commandData, ref message, elements);
                }
                finally
                {
                    ViewSession.Ui = null;
                }
                PluginLog.Info("command.end", $"{name} | {result} | {timer.ElapsedMilliseconds} мс" + (string.IsNullOrEmpty(message) ? "" : " | " + message));
                return result;
            }
            catch (Exception ex)
            {
                PluginLog.Error("command.crash", ex, $"{name} | {timer.ElapsedMilliseconds} мс");
                throw;
            }
        }
    }

    /// <summary>Информационные окна, скрытые галочкой. Живёт до закрытия Revit и нигде не сохраняется.</summary>
    internal static class SessionWindows
    {
        public const string CheckText = "Больше не показывать это окно";
        private static readonly HashSet<string> Hidden = new HashSet<string>();

        public static void Hide(string window) => Hidden.Add(window);

        public static bool IsHidden(string window) => Hidden.Contains(window);

        /// <summary>Сообщение о результате; скрытое окно не показывается.</summary>
        public static void Inform(string window, string title, string text)
        {
            if (IsHidden(window)) return;
            var dialog = new TaskDialog(title) { MainContent = text, CommonButtons = TaskDialogCommonButtons.Ok, VerificationText = CheckText };
            dialog.Show();
            if (dialog.WasVerificationChecked()) Hide(window);
        }

        /// <summary>Подтверждение действия; скрытое окно считается ответом «Да».</summary>
        public static bool Confirm(string window, string title, string text)
        {
            if (IsHidden(window)) return true;
            var dialog = new TaskDialog(title)
            {
                MainContent = text,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                VerificationText = CheckText,
            };
            if (dialog.Show() != TaskDialogResult.Yes) return false;
            if (dialog.WasVerificationChecked()) Hide(window);
            return true;
        }
    }

    internal static class ProjectGuard
    {
        /// <summary>Проверяет привязки, нужные выбранной системе маркировки.</summary>
        public static bool AllowMarking(UIApplication uiApp, MarkDevicesRequest request)
        {
            Document doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                return true; // параметры проекта к семейству не относятся
            }

            var required = MarkingBindings(request);
            List<ParameterIssue> issues = PluginParameterCheck.Check(doc, scope: required);
            if (issues.Count == 0)
            {
                return true;
            }
            PluginLog.Info("parameters.issues", string.Join("; ", issues.Select(i => $"{i.Name}: {i.Kind}")));


            bool canFix = issues.Any(i => i.CanFix);
            var dialog = new TaskDialog("Проверка параметров проекта")
            {
                MainInstruction = "Для выбранной системы не настроены параметры маркировки",
                MainContent = PluginParameterCheck.Report(issues),
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            if (canFix)
            {
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Добавить параметры и категории", "Загрузить недостающее из ФОП плагина и продолжить");
            }
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Продолжить без изменений", "Команда может работать неполно");
            dialog.DefaultButton = TaskDialogResult.CommandLink2;

            TaskDialogResult answer = dialog.Show();
            if (answer == TaskDialogResult.CommandLink2)
            {
                return true;
            }
            if (answer != TaskDialogResult.CommandLink1)
            {
                return false;
            }

            try
            {
                PluginParameterCheck.Fix(doc, uiApp.Application, issues, false);
                List<ParameterIssue> left = PluginParameterCheck.Check(doc, scope: required);
                if (left.Count > 0)
                {
                    TaskDialog.Show("Проверка параметров проекта", "Исправлено не всё:\n\n" + PluginParameterCheck.Report(left));
                }
                return true;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Проверка параметров проекта", "Не удалось добавить параметры: " + ex.Message);
                return false;
            }
        }

        private static Dictionary<string, BuiltInCategory[]> MarkingBindings(MarkDevicesRequest request)
        {
            const BuiltInCategory controller = BuiltInCategory.OST_ElectricalEquipment;
            BuiltInCategory[] devices;
            switch (request.System)
            {
                case "aps":
                    devices = new[] { BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_FireAlarmDevices };
                    break;
                case "souye":
                    devices = new[] { BuiltInCategory.OST_NurseCallDevices };
                    break;
                default:
                    devices = new[] { BuiltInCategory.OST_DataDevices };
                    break;
            }
            BuiltInCategory[] devicesAndController = devices.Append(controller).ToArray();
            var required = new Dictionary<string, BuiltInCategory[]>
            {
                [DeviceMarkingOperations.ParamProductName] = new[] { controller },
                [DeviceMarkingOperations.ParamBco] = devices,
                [DeviceMarkingOperations.ParamMark] = devices,
            };
            if (PluginParameterCheck.IsStandardFloor(MarkingSettings.CurrentFloorParameter()))
                required[MarkingSettings.DefaultFloorParameter] = request.Order == "route"
                    ? devicesAndController.Append(BuiltInCategory.OST_MechanicalEquipment).ToArray()
                    : devicesAndController;

            if (request.System == "security")
            {
                required[DeviceMarkingOperations.ParamAddress] = devices;
                return required;
            }

            if (!request.FunctionalZones || !string.IsNullOrWhiteSpace(request.Building))
                required[DeviceMarkingOperations.ParamBuilding] = request.FunctionalZones ? devices : devicesAndController;
            required[DeviceMarkingOperations.ParamController] = request.System == "souye" ? devicesAndController : devices;
            required[DeviceMarkingOperations.ParamLine] = devices;
            return required;
        }
    }

    [Transaction(TransactionMode.Manual)] public class GuardRemoveParameters : GuardedCommand<CmdRemoveParameters> { }
    [Transaction(TransactionMode.Manual)] public class GuardAddSharedParameters : GuardedCommand<CmdAddSharedParameters> { }
    [Transaction(TransactionMode.Manual)] public class GuardMarkDevices : GuardedCommand<CmdMarkDevices> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalSecurityAll : GuardedCommand<ExportCableJournalCommand> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalSecurityByParam : GuardedCommand<ExportCableJournalByParamCommand> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalAps : GuardedCommand<ExportApsCableJournalCommand> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalApsConnection : GuardedCommand<ExportApsConnectionJournalCommand> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalSouye : GuardedCommand<ExportSouyeCableJournalCommand> { }
}
