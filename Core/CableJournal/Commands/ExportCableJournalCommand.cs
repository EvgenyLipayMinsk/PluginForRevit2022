using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Revit;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Systems.Security.Models;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;

namespace ARBM.CableJournalExporter.Commands
{
    [Transaction(TransactionMode.Manual)]

    public class ExportCableJournalCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // ---------------------------------------------------------
                // 1. Выбор шаблона templ_kzh.xlsx
                // ---------------------------------------------------------
                var dialog = new FileDialogService();
                string templatePath = dialog.SelectTemplateFile();

                if (string.IsNullOrWhiteSpace(templatePath))
                {
                    TaskDialog.Show("Отмена",
                        "Шаблон Excel не выбран. Команда прервана.");
                    return Result.Cancelled;
                }

                // ---------------------------------------------------------
                // 2. Проверка структуры шаблона
                // ---------------------------------------------------------
                var validator = new TemplateValidator();
                var expectedStructure = new TemplateStructure();
                var validation = validator.Validate(templatePath, expectedStructure);

                if (!validation.IsValid)
                {
                    TaskDialog.Show("Ошибка структуры шаблона",
                        validation.Message);
                    return Result.Failed;
                }

                // ---------------------------------------------------------
                // 3. Collect all data (without filter)
                // ---------------------------------------------------------
                var parser = new SecurityElementParser();
                var collector = new SecurityDataCollector(parser);

                List<ControllerWithLoops> controllers =
                    collector.CollectControllersWithLoops(doc);

                // ✅ Если контроллеры не найдены, валидатор уже выдал сообщение об ошибке
                if (controllers == null || controllers.Count == 0)
                    return Result.Cancelled;

                // ---------------------------------------------------------
                // 4. Convert to CableRecords
                // ---------------------------------------------------------
                LengthMode mode = LengthMode.Route;

                var builder = new SecurityCableRecordBuilder(parser, doc, mode, 1.3);
                List<CableRecord> records = builder.Build(controllers);

                if (records.Count == 0)
                {
                    TaskDialog.Show("No data",
                        "No CableRecord entries were generated.");
                    return Result.Cancelled;
                }
                // ---------------------------------------------------------
                // 5. Выбор пути сохранения итогового файла
                // ---------------------------------------------------------
                string savePath = dialog.SelectSaveLocation();

                if (string.IsNullOrWhiteSpace(savePath))
                {
                    TaskDialog.Show("Отмена",
                        "Файл для сохранения не выбран. Команда прервана.");
                    return Result.Cancelled;
                }

                // ---------------------------------------------------------
                // 6. Экспорт в Excel
                // ---------------------------------------------------------
                var exporter = new ExcelExporter();
                exporter.ExportCableRecords(templatePath, savePath, records);

                // ---------------------------------------------------------
                // 7. Сообщение об успехе
                // ---------------------------------------------------------
                TaskDialog.Show("Готово",
                    $"Кабельный журнал успешно создан:\n{savePath}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Ошибка",
                    $"Произошла ошибка:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
