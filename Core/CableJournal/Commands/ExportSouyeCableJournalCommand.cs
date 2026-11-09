using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Systems.Souye;
using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace ARBM.CableJournalExporter.Commands
{
    /// <summary>
    /// Команда Revit для автоматического создания кабельного журнала
    /// Системы оповещения и управления эвакуацией (СОУЭ).
    /// <para>
    /// Порядок выполнения:
    /// <list type="number">
    ///   <item>Инициализация мониторинга отмены и watchdog-таймера (30 минут).</item>
    ///   <item>Информирование пользователя о требованиях к выбору элемента.</item>
    ///   <item>Пользователь выбирает контроллер СОУЭ в модели (OST_ElectricalEquipment).</item>
    ///   <item>Выбор стандарта параметров (ЭОМ или ADSK).</item>
    ///   <item>Выбор Excel-шаблона кабельного журнала.</item>
    ///   <item>Валидация шаблона.</item>
    ///   <item>Сбор данных из модели (<see cref="SouyeDataCollector"/>).</item>
    ///   <item>Валидация собранных данных (<see cref="SouyeDataValidator"/>).</item>
    ///   <item>Построение записей КЖ (<see cref="SouyeCableRecordBuilder"/>).</item>
    ///   <item>Выбор пути сохранения файла.</item>
    ///   <item>Экспорт в Excel-шаблон (<see cref="ExcelExporter"/>).</item>
    ///   <item>Итоговое окно с результатами по шлейфам.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportSouyeCableJournalCommand : IExternalCommand
    {
        /// <summary>
        /// Точка входа команды Revit.
        /// </summary>
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // ── Запускаем секундомер для измерения времени выполнения ──────────────
            var stopwatch = Stopwatch.StartNew();

            // ── Инициализация мониторинга отмены и watchdog-таймера ──────────────
            var cancelMonitor = new CancellationMonitor();
            TimeSpan timeout = TimeSpan.FromMinutes(30);
            Timer watchdog = null;

            try
            {
                watchdog = new Timer(_ =>
                {
                    string op = cancelMonitor.CurrentOperation ?? "<unknown>";
                    ExecutionLogger.Log(
                        "ExportSouyeCableJournalCommand",
                        "watchdog_timeout",
                        details: $"timeoutMs={timeout.TotalMilliseconds}, currentOperation={op}");
                    cancelMonitor.RequestCancel($"timeout {timeout}");
                }, null, timeout, Timeout.InfiniteTimeSpan);

                // =================================================================
                // БЛОК 1️⃣: Информирование пользователя о требованиях к выбору
                // BLOCK 1️⃣: Inform user about selection requirements
                // =================================================================

                TaskDialog.Show(
                    "Выбор оборудования СОУЭ",
                    "Выберите оборудование, входящее в состав СОУЭ.\n\n" +
                    "Допустимые категории:\n" +
                    "• Электрооборудование (OST_ElectricalEquipment) — контроллер СОУЭ\n\n" +
                    "• Устройства выбора и оповещения (OST_NurseCallDevices) — оповещатели и иные элементы СОУЭ\n\n" +
                     "Элемент должен содержать заполненный параметр «Comments».");

                // =================================================================
                // БЛОК 2️⃣: Выбор контроллера СОУЭ пользователем
                // BLOCK 2️⃣: Select SOUYÉ controller by user
                // =================================================================

                Element controllerElement = null;
                string commentsValue_Final = null;

                try
                {
                    // Фильтр ограничивает выбор только контроллерами СОУЭ (OST_ElectricalEquipment)
                    // EN: Filter restricts selection to only SOUYÉ controllers (OST_ElectricalEquipment)
                    var filter = new CommentsFilter(null,
                         BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_NurseCallDevices);
                    
                    var reference = uiDoc.Selection.PickObject(
                        ObjectType.Element,
                        filter,
                        "Выберите любой элемент, входящий в  СОУЭ (с заполненным Comments)");

                    if (reference == null)
                        return Result.Cancelled;

                    controllerElement = doc.GetElement(reference);
                    var commentsValue = controllerElement
                        .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                        ?.AsString();

                    if (string.IsNullOrWhiteSpace(commentsValue))
                    {
                        ShowError(
                            "Не удалось прочитать значение параметра Comments выбранного элемента. " +
                            "Убедитесь, что параметр заполнен.");
                        return Result.Failed;
                    }

                    commentsValue_Final = commentsValue;

                    ExecutionLogger.Log(
                        "ExportSouyeCableJournalCommand",
                        "controller_selected",
                        details: $"comments={commentsValue_Final}");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // Пользователь нажал Esc — штатная отмена
                    // EN: User pressed Esc — normal cancellation
                    return Result.Cancelled;
                }

                // Проверяем, что контроллер был успешно выбран
                // EN: Verify controller was selected
                if (controllerElement == null || string.IsNullOrWhiteSpace(commentsValue_Final))
                {
                    ShowError("Контроллер не был выбран или параметр Comments не заполнен.");
                    return Result.Failed;
                }

                // =================================================================
                // БЛОК 3️⃣: Выбор стандарта параметров
                // BLOCK 3️⃣: Select parameter standard
                // =================================================================

                var parameterStandardDialog = new TaskDialog("Выбор стандарта параметров");
                parameterStandardDialog.MainInstruction = "Выберите стандарт именования параметров для элементов СОУЭ:";
                parameterStandardDialog.MainContent = 
                    "ЭОМ — использует параметры ЭОМ_Мощность_Номинальная и О_Этаж\n\n" +
                    "ADSK — использует параметры ADSK_Номинальная мощность и ADSK_Этаж";

                parameterStandardDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "ЭОМ стандарт");
                parameterStandardDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "ADSK стандарт");
                parameterStandardDialog.CommonButtons = TaskDialogCommonButtons.Cancel;
                parameterStandardDialog.DefaultButton = TaskDialogResult.CommandLink1;

                var standardDialogResult = parameterStandardDialog.Show();

                if (standardDialogResult == TaskDialogResult.Cancel)
                    return Result.Cancelled;

                SouyeParameterStandard selectedStandard;
                if (standardDialogResult == TaskDialogResult.CommandLink1)
                {
                    selectedStandard = SouyeParameterStandard.Eom;
                    ExecutionLogger.Log(
                        "ExportSouyeCableJournalCommand",
                        "parameter_standard_selected",
                        details: "standard=EOM");
                }
                else if (standardDialogResult == TaskDialogResult.CommandLink2)
                {
                    selectedStandard = SouyeParameterStandard.Adsk;
                    ExecutionLogger.Log(
                        "ExportSouyeCableJournalCommand",
                        "parameter_standard_selected",
                        details: "standard=ADSK");
                }
                else
                {
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 4️⃣: Выбор файла шаблона Excel
                // BLOCK 4️⃣: Select Excel template file
                // =================================================================

                var fileDialog = new FileDialogService();

                string templatePath = fileDialog.SelectTemplateFile();

                if (string.IsNullOrEmpty(templatePath))
                    return Result.Cancelled;

                // =================================================================
                // БЛОК 5️⃣: Валидация структуры шаблона
                // BLOCK 5️⃣: Validate template structure
                // =================================================================

                var templateValidator = new TemplateValidator();
                var expectedStructure = new TemplateStructure();
                var validationResult = templateValidator.Validate(templatePath, expectedStructure);

                if (!validationResult.IsValid)
                {
                    ShowError(
                        $"Выбранный файл не соответствует шаблону кабельного журнала СОУЭ.\n\n" +
                        validationResult.Message);
                    return Result.Failed;
                }

                // =================================================================
                // БЛОК 6️⃣: Сбор данных из модели
                // BLOCK 6️⃣: Collect data from model
                // =================================================================

                cancelMonitor.SetCurrentOperation("CollectSouyeData");

                // Создаем маппер параметров на основе выбранного стандарта
                var parameterMapper = new SouyeParameterMapper(selectedStandard);
                var parser = new SouyeElementParser(parameterMapper);
                var collector = new SouyeDataCollector(parser);
                // SouyeControllerWithLoops Data from collectResult
                var collectResult = collector.Collect(doc, commentsValue_Final);
                cancelMonitor.ClearCurrentOperation();

                if (cancelMonitor.IsCancellationRequested)
                {
                    ExecutionLogger.Log(
                        "ExportSouyeCableJournalCommand",
                        "cancelled_by_watchdog",
                        details: cancelMonitor.CancelReason);
                    TaskDialog.Show(
                        "Отмена",
                        "Операция(cancelMonitor) отменена (истечение таймаута). См. лог для деталей.");
                    return Result.Cancelled;
                }
                // we need to check if the collectResult is null or not successful
                if (!collectResult.IsSuccess)
                {
                    ShowError(collectResult.ErrorMessage);
                    return Result.Failed;
                }

                var data = collectResult.Data;

                ExecutionLogger.Log(
                    "ExportSouyeCableJournalCommand",
                    "data_collected",
                    counts: new Dictionary<string, object>
                    {
                        { "loopGroups", data.LoopGroups?.Count ?? 0 }
                    });

                // =================================================================
                // БЛОК 7️⃣: Валидация собранных данных
                // BLOCK 7️⃣: Validate collected data
                // =================================================================

                var validator = new SouyeDataValidator(parameterMapper);
                var errors = validator.Validate(data);

                if (errors.Count > 0)
                {
                    ShowValidationErrors(errors);
                    return Result.Failed;
                }

                // =================================================================
                // БЛОК 8️⃣: Построение записей кабельного журнала
                // BLOCK 8️⃣: Build cable journal records
                // =================================================================

                cancelMonitor.SetCurrentOperation("BuildSouyeRecords");

                LengthMode mode = LengthMode.Route;

                var loggedCalculator = new LoggedRouteLengthCalculator();
                var innerCalculator = loggedCalculator.Inner;  // ✓ Получаем доступ к внутреннему объекту

                // Или используем логированный кальулятор напрямую с SouyeCableRecordBuilder
                var builder = new SouyeCableRecordBuilder(doc, mode, 1.4);

                SouyeBuildResult buildResult = null;
                try
                {
                    buildResult = builder.Build(data, commentsValue_Final, cancelMonitor);
                }
                catch (OperationCanceledException)
                {
                    ExecutionLogger.Log(
                        "ExportSouyeCableJournalCommand",
                        "cancelled_by_watchdog",
                        details: cancelMonitor.CancelReason);
                    TaskDialog.Show(
                        "Отмена",
                        "Операция(cancelMonitor) отменена (истечение таймаута). См. лог для деталей.");
                    return Result.Cancelled;
                }
                finally
                {
                    cancelMonitor.ClearCurrentOperation();
                }

                // Проверяем, что результат не null
                // EN: Verify build result
                if (buildResult == null)
                {
                    ShowError("Ошибка при построении записей кабельного журнала: результат null.");
                    return Result.Failed;
                }

                if (!buildResult.IsSuccess)
                {
                    ShowError(buildResult.ErrorMessage);
                    return Result.Failed;
                }

                var records = buildResult.Records;
                var loopSummaries = buildResult.LoopSummaries;

                if (records == null || records.Count == 0)
                {
                    ShowError("Не получено ни одной записи кабельного журнала.");
                    return Result.Failed;
                }

                ExecutionLogger.Log(
                    "ExportSouyeCableJournalCommand",
                    "records_built",
                    counts: new Dictionary<string, object>
                    {
                        { "cableRecords", records.Count },
                        { "loopSummaries", loopSummaries?.Count ?? 0 }
                    });

                // =================================================================
                // БЛОК 9️⃣: Выбор пути сохранения файла
                // BLOCK 9️⃣: Select file save path
                // =================================================================

                string outputPath = fileDialog.SelectSaveLocation();

                if (string.IsNullOrEmpty(outputPath))
                    return Result.Cancelled;

                // =================================================================
                // БЛОК 🔟: Экспорт в Excel
                // BLOCK 🔟: Export to Excel
                // =================================================================

                cancelMonitor.SetCurrentOperation("ExportToExcel");

                var exporter = new ExcelExporter();
                try
                {
                    exporter.ExportCableRecords(templatePath, outputPath, records);
                }
                finally
                {
                    cancelMonitor.ClearCurrentOperation();
                }

                ExecutionLogger.Log(
                    "ExportSouyeCableJournalCommand",
                    "export_completed",
                    details: $"outputPath={outputPath}");

                // =================================================================
                // БЛОК 1️⃣1️⃣: Итоговое окно с результатами по шлейфам
                // BLOCK 1️⃣1️⃣: Summary dialog with loop results
                // =================================================================

                // Останавливаем секундомер
                stopwatch.Stop();

                ShowSummary(loopSummaries, records.Count, outputPath, stopwatch.Elapsed);

                ExecutionLogger.Log(
                    "ExportSouyeCableJournalCommand",
                    "command_succeeded",
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: $"logPath={ExecutionLogger.GetLogPath()}");

                return Result.Succeeded;
            }
            finally
            {
                // Отключаем watchdog таймер
                // EN: Disable watchdog timer
                try { watchdog?.Dispose(); } catch { }
            }
        }

        // ─── Вспомогательные методы отображения диалоговых окон ──────────────────

        /// <summary>
        /// Отображает диалоговое окно с сообщением об ошибке.
        /// После закрытия окна команда завершается (вызывающий код возвращает <c>Result.Failed</c>).
        /// </summary>
        private static void ShowError(string message)
        {
            TaskDialog.Show(
                "Экспорт КЖ СОУЭ — Ошибка",
                message,
                TaskDialogCommonButtons.Ok,
                TaskDialogResult.Ok);
        }

        /// <summary>
        /// Отображает диалоговое окно со списком ошибок валидации данных модели.
        /// Все ошибки выводятся за один раз, чтобы пользователь мог исправить
        /// все нарушения, не запуская команду повторно для каждой.
        /// </summary>
        private static void ShowValidationErrors(List<string> errors)
        {
            var dialog = new TaskDialog("Экспорт КЖ СОУЭ — Ошибки заполнения параметров");
            dialog.MainInstruction =
                $"Обнаружено {errors.Count} ошибок в параметрах элементов модели. " +
                "Исправьте все ошибки и повторите экспорт.";

            // TaskDialog.MainContent ограничен по длине — формируем нумерованный список.
            var sb = new StringBuilder();
            for (int i = 0; i < errors.Count; i++)
                sb.AppendLine($"{i + 1}. {errors[i]}");

            dialog.MainContent = sb.ToString();
            dialog.CommonButtons = TaskDialogCommonButtons.Ok;
            dialog.Show();
        }

        /// <summary>
        /// Отображает итоговое информационное окно после успешного экспорта.
        /// Формирует строки без шапки, каждая ячейка имеет префикс.
        /// Столбцы фиксированной ширины, ширина определяется по максимальному значению.
        /// </summary>
        private static void ShowSummary(
            List<SouyeLoopSummary> loopSummaries,
            int totalRecords,
            string outputPath,
            TimeSpan executionTime)
        {
            // Префиксы для каждой колонки
            var prefixes = new[]
            {
                "ШС=",
                "Уст=",
                "P=",
                "L=",
                "S=",
                ""      // Кабель без префикса
            };

            int columnCount = prefixes.Length;

            // Собираем строки данных
            var dataRows = new List<string[]>();

            foreach (var summary in loopSummaries)
            {
                string powerStr = (summary.TotalPower % 1 == 0)
                    ? ((int)summary.TotalPower).ToString()
                    : summary.TotalPower.ToString("F2");

                int lengthInt = (int)Math.Round(summary.TotalLength);
                string sectionStr = SouyeCableCatalog.FormatCrossSection(summary.CalculatedSection);
                string cableStr = $"{summary.CableSpec?.CableType} {summary.CableSpec?.CableCategory}";

                dataRows.Add(new[]
                {
                    $"{prefixes[0]}{summary.LoopAddress}",
                    $"{prefixes[1]}{summary.DeviceCount}",
                    $"{prefixes[2]}{powerStr}",
                    $"{prefixes[3]}{lengthInt}м",
                    $"{prefixes[4]}{sectionStr}",
                    $"{prefixes[5]}{cableStr}"
                });
            }

            // Вычисляем ширину каждой колонки
            var columnWidths = new int[columnCount];

            for (int col = 0; col < columnCount; col++)
            {
                int maxLen = 0;
                foreach (var row in dataRows)
                {
                    int len = row[col].Length;
                    if (len > maxLen)
                        maxLen = len;
                }
                columnWidths[col] = maxLen;
            }

            // figure-space (ширина цифры)
            const char fs = '\u2007';

            string PadFs(string text, int width)
            {
                int diff = width - text.Length;
                if (diff <= 0) return text;
                return text + new string(fs, diff);
            }

            var sb = new StringBuilder();

            // Формируем строки
            foreach (var row in dataRows)
            {
                for (int col = 0; col < columnCount; col++)
                {
                    if (col == 0) sb.Append("|");

                    sb.Append(PadFs(row[col], columnWidths[col]));
                    sb.Append("|");
                }
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine($"Всего строк в кабельном журнале: {totalRecords}");

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

            sb.AppendLine($"Файл сохранён:");
            sb.AppendLine(SplitLongPath(outputPath, 60));

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

            var dialog = new TaskDialog("Экспорт КЖ СОУЭ — Готово");
            dialog.MainInstruction = "Кабельный журнал СОУЭ успешно сформирован.";
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
