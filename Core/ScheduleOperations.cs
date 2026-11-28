using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Спецификации: список, чтение, создание и экспорт в текстовый файл.</summary>
    internal static class ScheduleOperations
    {
        private const int MaxRows = 500;

        private static IEnumerable<ViewSchedule> All(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule);

        public static Dictionary<string, object> List(Document doc) => new Dictionary<string, object>
        {
            ["schedules"] = All(doc).OrderBy(s => s.Name).Select(s => new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(s.Id),
                ["name"] = s.Name,
                ["category"] = Category.GetCategory(doc, s.Definition.CategoryId)?.Name ?? "многокатегорийная",
                ["fields"] = s.Definition.GetFieldCount(),
            }).ToList(),
        };

        private static ViewSchedule Resolve(Document doc, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Укажите schedule (id или имя). Список: list_schedules.");
            string wanted = value.Trim();
            if (long.TryParse(wanted, out long id) && doc.GetElement(RevitIds.ToElementId(id)) is ViewSchedule byId) return byId;
            List<ViewSchedule> matches = All(doc).Where(s => string.Equals(s.Name, wanted, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException(matches.Count == 0 ? $"Спецификация «{wanted}» не найдена." : $"Несколько спецификаций «{wanted}»: укажите id.");
        }

        public static Dictionary<string, object> Read(Document doc, string value)
        {
            ViewSchedule schedule = Resolve(doc, value);
            TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
            var rows = new List<List<string>>();
            for (int r = 0; r < body.NumberOfRows && rows.Count < MaxRows; r++)
            {
                var cells = new List<string>();
                for (int c = 0; c < body.NumberOfColumns; c++) cells.Add(schedule.GetCellText(SectionType.Body, r, c));
                rows.Add(cells);
            }
            return new Dictionary<string, object>
            {
                ["schedule"] = schedule.Name,
                ["rows"] = body.NumberOfRows,
                ["columns"] = body.NumberOfColumns,
                ["data"] = rows,
                ["truncated"] = body.NumberOfRows > MaxRows,
            };
        }

        public static Dictionary<string, object> Export(Document doc, string value, string folder, string fileName)
        {
            ViewSchedule schedule = Resolve(doc, value);
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Укажите folder.");
            Directory.CreateDirectory(folder);
            string name = ExportOperations.SafeFileName(string.IsNullOrWhiteSpace(fileName) ? schedule.Name : fileName.Trim());
            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) name += ".csv";
            schedule.Export(folder, name, new ViewScheduleExportOptions { FieldDelimiter = ";", TextQualifier = ExportTextQualifier.DoubleQuote });
            return new Dictionary<string, object> { ["file"] = Path.Combine(folder, name), ["summary"] = $"Спецификация сохранена: {Path.Combine(folder, name)}" };
        }

        public static Dictionary<string, object> Create(Document doc, string categoryValue, string name, List<string> fields, string sortBy, bool dryRun)
        {
            Category category = SharedParameterOperations.ResolveCategories(doc, new[] { categoryValue }).FirstOrDefault()
                ?? throw new ArgumentException($"Категория «{categoryValue}» не найдена.");
            if (fields == null || fields.Count == 0) throw new ArgumentException("Укажите fields — имена полей спецификации.");

            var missing = new List<string>();
            var added = new List<string>();
            long scheduleId;
            using (var transaction = new Transaction(doc, "AerBIM: создать спецификацию"))
            {
                transaction.Start();
                ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, category.Id);
                if (!string.IsNullOrWhiteSpace(name)) schedule.Name = name.Trim();
                ScheduleDefinition definition = schedule.Definition;
                IList<SchedulableField> available = definition.GetSchedulableFields();
                var fieldIds = new Dictionary<string, ScheduleFieldId>(StringComparer.CurrentCultureIgnoreCase);
                foreach (string wanted in fields)
                {
                    SchedulableField field = available.FirstOrDefault(f => string.Equals(f.GetName(doc), wanted.Trim(), StringComparison.CurrentCultureIgnoreCase));
                    if (field == null)
                    {
                        missing.Add(wanted);
                        continue;
                    }
                    fieldIds[wanted.Trim()] = definition.AddField(field).FieldId;
                    added.Add(wanted.Trim());
                }
                if (!string.IsNullOrWhiteSpace(sortBy))
                {
                    if (!fieldIds.TryGetValue(sortBy.Trim(), out ScheduleFieldId sortId)) throw new ArgumentException($"Поле сортировки «{sortBy}» не входит в fields.");
                    definition.AddSortGroupField(new ScheduleSortGroupField(sortId));
                }
                if (added.Count == 0)
                {
                    transaction.RollBack();
                    throw new ArgumentException("Ни одно поле не найдено. Доступные: " + string.Join(", ", available.Take(40).Select(f => f.GetName(doc))));
                }
                scheduleId = RevitIds.IdOf(schedule.Id);
                if (dryRun) transaction.RollBack();
                else if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не создал спецификацию.");
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["id"] = dryRun ? (long?)null : scheduleId,
                ["category"] = category.Name,
                ["fields"] = added,
                ["missingFields"] = missing,
                ["summary"] = dryRun
                    ? $"Предпросмотр: спецификация «{category.Name}», полей: {added.Count}. Для создания повторите с dryRun=false."
                    : $"Спецификация создана, полей: {added.Count}.",
            };
        }
    }
}
