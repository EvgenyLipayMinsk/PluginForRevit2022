using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ARBM.CableJournalExporter.Models;
using Autodesk.Revit.DB;
using OfficeOpenXml;
using PluginForRevit2022.Mcp;

namespace PluginForRevit2022.Core
{
    internal static class JournalReviewOperations
    {
        internal static double Number(string value)
        {
            if (!double.TryParse((value ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || double.IsNaN(n) || double.IsInfinity(n) || n < 0)
                throw new ArgumentException("Некорректная длина: «" + value + "».");
            return n;
        }

        private static List<CableRecord> Records(Document doc, McpArgs args, List<string> messages) => CableJournalOperations.Calculate(doc, new CableJournalRequest
        {
            System = args.String("system"), Comments = args.String("comments"), ParameterStandard = args.String("parameterStandard"),
            ControllerElementId = args.Long("controllerElementId"), DryRun = true,
        }, messages);

        public static object Explain(Document doc, McpArgs args)
        {
            var messages = new List<string>(); var records = Records(doc, args, messages);
            int index = (args.Int("recordNumber") ?? 1) - 1;
            if (index < 0 || index >= records.Count) throw new ArgumentException("recordNumber должен быть от 1 до " + records.Count + ".");
            var record = records[index]; double final = Number(record.Length), reserve = MarkingSettings.LineReserveM();
            double factor = MarkingSettings.CableFactor((args.String("system") ?? "").Trim().ToLowerInvariant());
            var installation = (record.InstallationLengths ?? new CableInstallationLengths(0, 0)).Allocate(final);
            int offset = Math.Max(0, args.Int("segmentOffset") ?? 0), limit = Math.Max(1, Math.Min(2000, args.Int("segmentLimit") ?? 200));
            return new Dictionary<string, object>
            {
                ["recordNumber"] = index + 1, ["records"] = records.Count, ["start"] = record.StartPoint, ["end"] = record.EndPoint,
                ["rawLengthM"] = record.RawLengthMeters, ["correctionFactor"] = factor,
                ["afterCoefficientM"] = record.RawLengthMeters == null ? null : (object)(record.RawLengthMeters.Value * factor),
                ["roundingM"] = record.RawLengthMeters == null ? null : (object)(final - reserve - record.RawLengthMeters.Value * factor),
                ["beforeLineReserveM"] = final - reserve, ["lineReserveM"] = reserve, ["finalLengthM"] = final,
                ["concealedM"] = installation.Concealed, ["trayM"] = installation.Tray, ["fallback"] = record.IsFallbackForLength,
                ["segmentCount"] = record.RouteTrace?.Count ?? 0,
                ["nextSegmentOffset"] = offset + limit < (record.RouteTrace?.Count ?? 0) ? (object)(offset + limit) : null,
                ["segments"] = (record.RouteTrace ?? new List<RouteTraceSegment>()).Skip(offset).Take(limit).Select(s => new Dictionary<string, object>
                {
                    ["routeElementId"] = s.RouteElementId == 0 ? null : (object)s.RouteElementId,
                    ["virtualConnection"] = s.RouteElementId == 0, ["lengthM"] = s.LengthM,
                    ["installation"] = s.Installation, ["fromMm"] = s.FromMm, ["toMm"] = s.ToMm,
                }).ToList(),
                ["warnings"] = messages,
                ["summary"] = record.RawLengthMeters == null ? "Фиксированная служебная длина: геометрический маршрут для этой записи не рассчитывается." :
                    "Исходная длина → коэффициент → округление вверх → запас на строку. Точки — геометрия расчётного графа в мм во внутренних координатах Revit. ID отсутствует у служебных связей. При fallback путь может включать мост либо использовать прямое расстояние.",
            };
        }

        public static object Compare(Document doc, McpArgs args)
        {
            string path = args.String("journalPath");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Укажите существующий journalPath (.xlsx).", path);
            double tolerance = args.Double("lengthToleranceM") ?? 0.01;
            if (tolerance < 0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance)) throw new ArgumentException("lengthToleranceM должен быть неотрицательным.");
            // Проверяем формат до тяжёлого пересчёта модели.
            ExcelPackage.License.SetNonCommercialPersonal("ARBM");
            using (var book = new ExcelPackage(new FileInfo(path)))
            {
                var sheet = book.Workbook.Worksheets["Полный КЖ"] ?? throw new ArgumentException("Нужен журнал А3 с вкладкой «Полный КЖ». Текущий шаблон AerBIM этой командой не сравнивается.");
                if (sheet.Cells[1, 2].Text != "Начало трассы" || sheet.Cells[1, 10].Text != "Длина по проекту, м") throw new ArgumentException("Структура «Полный КЖ» не соответствует форме плагина.");
                var messages = new List<string>(); var records = Records(doc, args, messages);
                var result = CompareRows(sheet, records, tolerance);
                result["messages"] = messages; result["journalPath"] = Path.GetFullPath(path);
                return result;
            }
        }

        internal static Dictionary<string, object> CompareRows(ExcelWorksheet sheet, IList<CableRecord> records, double tolerance)
        {
            string Key(string a, string b) => (a ?? "").Trim() + "\u001f" + (b ?? "").Trim();
            var model = records.Select((r, i) => new { record = r, number = i + 1 }).GroupBy(x => Key(x.record.StartPoint, x.record.EndPoint)).ToDictionary(g => g.Key, g => g.ToList());
            var seen = new HashSet<string>(); var issues = new List<Dictionary<string, object>>();
            int rows = sheet.Dimension?.End.Row ?? 1;
            for (int row = 2; row <= rows; row++)
            {
                if (Enumerable.Range(1, 13).All(c => sheet.Cells[row, c].Value == null && sheet.Cells[row, c].Formula.Length == 0)) continue;
                string key = Key(sheet.Cells[row, 2].Text, sheet.Cells[row, 3].Text);
                var kinds = new List<string>();
                if (!seen.Add(key)) kinds.Add("duplicate_excel_endpoints");
                CableRecord expected = null; int? modelRow = null;
                if (!model.TryGetValue(key, out var candidates)) kinds.Add("not_in_model");
                else if (candidates.Count != 1) kinds.Add("ambiguous_model_endpoints");
                else { expected = candidates[0].record; modelRow = candidates[0].number; }
                try
                {
                    double total = Number(sheet.Cells[row, 10].Text), sum = 0;
                    for (int c = 4; c <= 7; c++) if (!string.IsNullOrWhiteSpace(sheet.Cells[row, c].Text)) sum += Number(sheet.Cells[row, c].Text);
                    if (Math.Abs(total - sum) > tolerance) kinds.Add("installation_sum");
                    if (expected != null)
                    {
                        if (Math.Abs(total - Number(expected.Length)) > tolerance) kinds.Add("design_length");
                        var allocated = (expected.InstallationLengths ?? new CableInstallationLengths(0, 0)).Allocate(Number(expected.Length));
                        double hidden = string.IsNullOrWhiteSpace(sheet.Cells[row, 4].Text) ? 0 : Number(sheet.Cells[row, 4].Text);
                        double tray = string.IsNullOrWhiteSpace(sheet.Cells[row, 7].Text) ? 0 : Number(sheet.Cells[row, 7].Text);
                        if (Math.Abs(hidden - allocated.Concealed) > tolerance || Math.Abs(tray - allocated.Tray) > tolerance) kinds.Add("installation_distribution");
                    }
                }
                catch (ArgumentException) { kinds.Add("invalid_length_or_formula_cache"); }
                if (expected != null && sheet.Cells[row, 8].Text != (expected.CableType ?? "")) kinds.Add("cable_type");
                if (expected != null && sheet.Cells[row, 9].Text != (expected.CableCategory ?? "")) kinds.Add("cable_cores");
                if (kinds.Count > 0) issues.Add(new Dictionary<string, object> { ["excelRow"] = row, ["modelRecordNumber"] = modelRow, ["start"] = sheet.Cells[row, 2].Text, ["end"] = sheet.Cells[row, 3].Text, ["kinds"] = kinds });
            }
            var missing = model.Where(p => !seen.Contains(p.Key)).SelectMany(p => p.Value).Select(p => new { recordNumber = p.number, start = p.record.StartPoint, end = p.record.EndPoint }).ToList();
            return new Dictionary<string, object> { ["ok"] = issues.Count == 0 && missing.Count == 0, ["issueCount"] = issues.Count, ["issues"] = issues.Take(300).ToList(), ["missingInExcelCount"] = missing.Count, ["missingInExcel"] = missing.Take(300).ToList(), ["modelRecords"] = records.Count,
                ["summary"] = "Сопоставление по точным обозначениям начала/конца, без записи в Excel или модель. После переименования устройства строка будет показана как отсутствующая/лишняя. Формулы сверяются по сохранённым значениям: пересчитайте и сохраните книгу в Excel перед проверкой." };
        }
    }
}
