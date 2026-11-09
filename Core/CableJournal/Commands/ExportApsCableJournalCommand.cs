    using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Revit;
using ARBM.CableJournalExporter.Services;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;   
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using static ARBM.CableJournalExporter.Services.RouteLengthCalculator.Graph;

namespace ARBM.CableJournalExporter.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ExportApsCableJournalCommand : IExternalCommand
    {
        public Result Execute(
           ExternalCommandData commandData,
           ref string message,
           ElementSet elements)
        {
            // Получаем интерфейс для работы с документом
            // EN: Get interface for working with the document
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // ── Запускаем секундомер для измерения времени выполнения ──────────────
            var stopwatch = Stopwatch.StartNew();

            // Создаём монитор отмены и сторожевой таймер (watchdog).
            var cancelMonitor = new CancellationMonitor();

            // Конфиг таймаута: 30 минут (можно изменить).
            TimeSpan timeout = TimeSpan.FromMinutes(30);

            // Таймер сторожа — по истечении таймаута помечает запрос отмены и логирует текущее состояние.
            Timer watchdog = null;
            try
            {
                watchdog = new Timer(_ =>
                {
                    // Best‑effort: логируем причину и_OPERACI, затем помечаем отмену.
                    string op = cancelMonitor.CurrentOperation ?? "<unknown>";
                    ExecutionLogger.Log("watchdog", "timeout", details: $"timeoutMs={timeout.TotalMilliseconds}, currentOperation={op}");
                    cancelMonitor.RequestCancel($"timeout {timeout}");
                }, null, timeout, Timeout.InfiniteTimeSpan);

                // =================================================================
                // БЛОК 1️⃣: Выбор элемента с параметром Comments
                // BLOCK 1️⃣: Select element with Comments parameter
                // =================================================================

                // Информируем пользователя о необходимом действии
                // EN: Inform user about the required action
                TaskDialog.Show("Select element",
                    "Select one element with a filled system parameter \"Comments = АПС\".\n" +
                    "Allowed categories: MechanicalEquipment, ElectricalEquipment, DataDevices," +
                    " OST_FireAlarmDevices.");

                // Запрашиваем выбор одного элемента, используя кастомный фильтр CommentsFilter
                // Фильтр ограничивает выбор только элементами из разрешенных категорий,
                // у которых уже установлено значение параметра Comments
                // EN: Request to select one element using custom CommentsFilter
                // The filter restricts selection to only elements from allowed categories
                // that already have Comments parameter set
                Reference pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new CommentsFilter("АПС"));

                // Если пользователь отменил выбор (нажал Escape или закрыл диалог)
                // EN: If user cancelled selection (pressed Escape or closed dialog)
                if (pickedRef == null)
                    return Result.Cancelled;

                // Получаем сам элемент из документа по ссылке
                // EN: Get the actual element from the document by reference
                Element picked = doc.GetElement(pickedRef);

                // Извлекаем значение параметра "Comments" (системный параметр Revit)
                // Используем null-conditional оператор (?.) для безопасного доступа
                // EN: Extract the value of "Comments" parameter (Revit system parameter)
                // Use null-conditional operator (?.) for safe access
                string paramValue = picked
                    .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.AsString();

                // Проверяем, что значение Comments непусто
                // EN: Verify that Comments value is not empty
                if (string.IsNullOrWhiteSpace(paramValue))
                {
                    TaskDialog.Show("Error",
                        "Selected element does not contain a valid Comments parameter.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 2️⃣: Выбор файла шаблона Excel
                // BLOCK 2️⃣: Select Excel template file
                // =================================================================

                // Создаём сервис для работы с файловыми диалогами
                // EN: Create service for working with file dialogs
                var dialog = new FileDialogService();

                // Запрашиваем у пользователя выбор файла шаблона Excel
                // EN: Request user to select Excel template file
                string templatePath = dialog.SelectTemplateFile();

                // Проверяем, был ли выбран файл
                // EN: Verify that a file was selected
                if (string.IsNullOrWhiteSpace(templatePath))
                {
                    TaskDialog.Show("Cancel",
                        "Excel template not selected. Command aborted.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 3️⃣: Валидация структуры шаблона
                // BLOCK 3️⃣: Validate template structure
                // =================================================================

                // Создаём валидатор для проверки структуры шаблона
                // EN: Create validator to check template structure
                var validator = new TemplateValidator();

                // Определяем ожидаемую структуру шаблона (какие колонки, какие значения и т.д.)
                // EN: Define the expected template structure (columns, values, etc.)
                var expectedStructure = new TemplateStructure();

                // Валидируем выбранный шаблон против ожидаемой структуры
                // EN: Validate selected template against expected structure
                var validation = validator.Validate(templatePath, expectedStructure);

                // Если валидация не прошла — выводим ошибку и завершаем команду
                // EN: If validation failed — show error and abort command
                if (!validation.IsValid)
                {
                    TaskDialog.Show("Template error", validation.Message);
                    return Result.Failed;
                }

                // --- Сбор контроллеров: перед долгой операцией указываем операцию в мониторе ---
                cancelMonitor.SetCurrentOperation("CollectApsControllers");

                ApsDataCollector apsDataCollector = new ApsDataCollector();
                var apsControllersWithLoops =
                    apsDataCollector.CollectApsControllersWithLoopsByParam(doc, paramValue, cancelMonitor);

                cancelMonitor.ClearCurrentOperation();

                if (cancelMonitor.IsCancellationRequested)
                {
                    // watchdog сработал — лог и корректное завершение
                    ExecutionLogger.Log("ExportApsCableJournalCommand", "cancelled_by_watchdog", details: cancelMonitor.CancelReason);
                    TaskDialog.Show("Cancelled", "Operation cancelled (timeout). See log for details.");
                    return Result.Cancelled;
                }

                if (apsControllersWithLoops == null || apsControllersWithLoops.Count == 0)
                    return Result.Cancelled;

                LengthMode mode = LengthMode.Route;

                // Перед построением графов и расчётом длин указываем операцию
                cancelMonitor.SetCurrentOperation("BuildRecordsAndGraphs");

                var builder = new ApsCableRecordBuilder(new ApsElementParser(), doc, mode, 1.3);
                List<CableRecord> records = null;
                try
                {
                    // Перенёс Build так, чтобы он принимал монитор (будет проксировать в калькулятор графов)
                    records = builder.Build(apsControllersWithLoops, paramValue, cancelMonitor);
                }
                catch (OperationCanceledException)
                {
                    ExecutionLogger.Log("ExportApsCableJournalCommand", "cancelled_by_watchdog", details: cancelMonitor.CancelReason);
                    TaskDialog.Show("Cancelled", "Operation cancelled (timeout). See log for details.");
                    return Result.Cancelled;
                }
                finally
                {
                    cancelMonitor.ClearCurrentOperation();
                }

                if (records == null || records.Count == 0)
                {
                    TaskDialog.Show("No data",
                        "No CableRecord entries were generated.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 6️⃣: Выбор пути сохранения файла
                // BLOCK 6️⃣: Select file save path
                // =================================================================

                string savePath = dialog.SelectSaveLocation();
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    TaskDialog.Show("Cancel",
                        "Save file not selected. Command aborted.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 7️⃣: Экспорт в Excel
                // BLOCK 7️⃣: Export to Excel
                // =================================================================

                var exporter = new ExcelExporter();

                // Указываем операцию экспорта (короткая).
                cancelMonitor.SetCurrentOperation("ExportToExcel");
                try
                {
                    exporter.ExportCableRecords(templatePath, savePath, records);
                }
                finally
                {
                    cancelMonitor.ClearCurrentOperation();
                }

                // Останавливаем секундомер
                stopwatch.Stop();

                ExecutionLogger.Log(
                    "ExportApsCableJournalCommand",
                    "command_succeeded",
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: $"logPath={ExecutionLogger.GetLogPath()}");

                // =================================================================
                // БЛОК 8️⃣: Итоговое окно с результатами
                // BLOCK 8️⃣: Summary dialog with results
                // =================================================================

                ShowSummary(records.Count, savePath, stopwatch.Elapsed);

                return Result.Succeeded;
            }
            finally
            {
                // Отключаем сторожевой таймер
                try { watchdog?.Dispose(); } catch { }
            }
        }

        // ─── Вспомогательные методы отображения диалоговых окон ──────────────────

        /// <summary>
        /// Отображает итоговое информационное окно после успешного экспорта АПС.
        /// Показывает количество строк, время выполнения и пути к файлам.
        /// </summary>
        private static void ShowSummary(
            int totalRecords,
            string outputPath,
            TimeSpan executionTime)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Всего строк в кабельном журнале: {totalRecords}");
            sb.AppendLine();

            // Форматируем время выполнения
            string executionTimeStr;
            if (executionTime.TotalSeconds > 60)
            {
                executionTimeStr = $"{executionTime.TotalMinutes:F2} мин";
            }
            else
            {
                executionTimeStr = $"{executionTime.TotalSeconds:F2} сек";
            }
            sb.AppendLine($"Время выполнения команды: {executionTimeStr}");
            sb.AppendLine();

            sb.AppendLine($"Файл сохранён:");
            sb.AppendLine(SplitLongPath(outputPath, 60));
            sb.AppendLine();

            sb.AppendLine($"Логирование:");
            string logPath = ExecutionLogger.GetLogPath();
            if (!string.IsNullOrEmpty(logPath))
            {
                sb.AppendLine(SplitLongPath(logPath, 60));
            }
            else
            {
                sb.AppendLine("Не удалось получить путь к лог-файлу");
            }

            var dialog = new TaskDialog("Экспорт КЖ АПС — Готово");
            dialog.MainInstruction = "Кабельный журнал АПС успешно сформирован.";
            dialog.MainContent = sb.ToString();
            dialog.CommonButtons = TaskDialogCommonButtons.Ok;
            dialog.Show();
        }

        /// <summary>
        /// Разбивает длинный путь на несколько строк с учетом максимальной ширины.
        /// </summary>
        /// <param name="path">Полный путь к файлу.</param>
        /// <param name="maxWidth">Максимальная ширина строки в символах.</param>
        /// <returns>Путь, разбитый на строки.</returns>
        private static string SplitLongPath(string path, int maxWidth)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxWidth)
                return path;

            var sb = new StringBuilder();
            int currentPos = 0;

            while (currentPos < path.Length)
            {
                int remainingLength = path.Length - currentPos;
                int chunkLength = Math.Min(maxWidth, remainingLength);

                // Если это не последний кусок, пытаемся найти разделитель (\\ или /)
                if (chunkLength < remainingLength)
                {
                    int lastSeparator = path.LastIndexOfAny(new[] { '\\', '/' }, currentPos + chunkLength - 1, chunkLength);
                    if (lastSeparator > currentPos)
                    {
                        chunkLength = lastSeparator - currentPos + 1;
                    }
                }

                if (sb.Length > 0)
                    sb.AppendLine();

                sb.Append(path.Substring(currentPos, chunkLength));
                currentPos += chunkLength;
            }

            return sb.ToString();
        }
    }
}
