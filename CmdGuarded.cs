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
    /// <summary>Перед запуском команды с ленты проверяет параметры плагина в проекте. Кнопки MCP не проверяются.</summary>
    public abstract class GuardedCommand<TCommand> : IExternalCommand where TCommand : IExternalCommand, new()
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return ProjectGuard.Allow(commandData.Application)
                ? new TCommand().Execute(commandData, ref message, elements)
                : Result.Cancelled;
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
        private const string WindowKey = "project-check";

        /// <summary>true — команду можно запускать.</summary>
        public static bool Allow(UIApplication uiApp)
        {
            Document doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                return true; // параметры проекта к семейству не относятся
            }

            List<ParameterIssue> issues = PluginParameterCheck.Check(doc);
            if (issues.Count == 0)
            {
                return true;
            }

            if (SessionWindows.IsHidden(WindowKey))
            {
                return true; // окно скрыто пользователем: команда идёт без изменений проекта
            }

            bool canFix = issues.Any(i => i.CanFix);
            var dialog = new TaskDialog("Проверка параметров проекта")
            {
                MainInstruction = "В проекте не настроены параметры плагина",
                MainContent = PluginParameterCheck.Report(issues),
                CommonButtons = TaskDialogCommonButtons.Cancel,
                VerificationText = SessionWindows.CheckText,
            };
            if (canFix)
            {
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Добавить параметры и категории", "Загрузить недостающее из ФОП плагина и продолжить");
            }
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Продолжить без изменений", "Команда может работать неполно");
            // Кнопка по умолчанию задаётся только после добавления ссылок: иначе Revit выдаёт ArgumentException.
            dialog.DefaultButton = canFix ? TaskDialogResult.CommandLink1 : TaskDialogResult.CommandLink2;

            TaskDialogResult answer = dialog.Show();
            if (dialog.WasVerificationChecked() && (answer == TaskDialogResult.CommandLink1 || answer == TaskDialogResult.CommandLink2))
            {
                SessionWindows.Hide(WindowKey);
            }
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
                List<ParameterIssue> left = PluginParameterCheck.Check(doc);
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
    }

    [Transaction(TransactionMode.Manual)] public class GuardRemoveParameters : GuardedCommand<CmdRemoveParameters> { }
    [Transaction(TransactionMode.Manual)] public class GuardAddSharedParameters : GuardedCommand<CmdAddSharedParameters> { }
    [Transaction(TransactionMode.Manual)] public class GuardMarkDevices : GuardedCommand<CmdMarkDevices> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalSecurityAll : GuardedCommand<ExportCableJournalCommand> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalSecurityByParam : GuardedCommand<ExportCableJournalByParamCommand> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalAps : GuardedCommand<ExportApsCableJournalCommand> { }
    [Transaction(TransactionMode.Manual)] public class GuardJournalSouye : GuardedCommand<ExportSouyeCableJournalCommand> { }
}
