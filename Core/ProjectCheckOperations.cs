using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using PluginForRevit2022.Mcp;

namespace PluginForRevit2022.Core
{
    internal static class ProjectCheckOperations
    {
        public static object Run(UIApplication app, Document doc, McpArgs args)
        {
            var names = args.StringList("checks");
            if (names.Count == 0) names = new List<string> { "parameters", "routes", "lines", "warnings" };
            string[] allowed = { "parameters", "routes", "lines", "warnings", "health" };
            if (names.Any(n => !allowed.Contains(n))) throw new ArgumentException("checks: parameters, routes, lines, warnings, health.");
            string target = args.String("savePath"); bool overwrite = args.Bool("overwrite") ?? false, dry = args.Bool("dryRun") ?? true;
            if (target != null)
            {
                string ext = Path.GetExtension(target).ToLowerInvariant();
                if (ext != ".json" && ext != ".xlsx") throw new ArgumentException("Для отчёта проверок нужен .json или .xlsx.");
                target = DiagnosticFiles.Target(target, ext, overwrite);
            }
            var checks = new List<Dictionary<string, object>>();
            foreach (string name in names.Distinct())
            {
                var row = new Dictionary<string, object> { ["check"] = name };
                checks.Add(row);
                try
                {
                    Dictionary<string, object> result;
                    bool ok;
                    switch (name)
                    {
                        case "parameters":
                            var issues = PluginParameterCheck.Check(doc);
                            result = new Dictionary<string, object> { ["issues"] = issues.Select(i => new { parameter = i.Name, kind = i.Kind, categories = i.MissingCategoryNames }).ToList(), ["ok"] = issues.Count == 0 };
                            ok = issues.Count == 0; break;
                        case "routes": result = CableDiagnosticsOperations.Routes(doc, args); ok = (bool)result["ok"]; break;
                        case "lines": result = CableDiagnosticsOperations.Lines(doc, args); ok = (bool)result["ok"]; break;
                        case "warnings": result = ModelCheckOperations.Warnings(doc); ok = doc.GetWarnings().Count == 0; break;
                        default: result = ProjectInfoOperations.Health(doc); ok = result.TryGetValue("grade", out object grade) && grade?.ToString() == "A"; break;
                    }
                    row["status"] = ok ? "passed" : "issues"; row["result"] = result;
                }
                catch (Exception ex) { row["status"] = "error"; row["error"] = ex.Message; }
            }
            var report = new Dictionary<string, object>
            {
                ["document"] = doc.Title, ["ok"] = checks.All(c => (string)c["status"] == "passed"), ["checks"] = checks,
                ["dryRun"] = dry, ["savePath"] = target, ["saved"] = !dry && target != null,
                ["summary"] = "Выполнены только выбранные проверки. Модель не изменяется. passed означает отсутствие замечаний в пределах этих проверок, а не подтверждение полного соответствия проекта нормам.",
            };
            if (!dry && target != null) Write(target, report, overwrite);
            return report;
        }

        internal static void Write(string target, Dictionary<string, object> report, bool overwrite)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            string json = serializer.Serialize(report);
            if (Path.GetExtension(target).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticFiles.Write(target, new UTF8Encoding(false).GetBytes(json), overwrite);
                return;
            }
            ExcelPackage.License.SetNonCommercialPersonal("ARBM");
            using (var book = new ExcelPackage())
            {
                var sheet = book.Workbook.Worksheets.Add("Проверки");
                sheet.Cells[1, 1].Value = "Путь результата"; sheet.Cells[1, 2].Value = "Значение";
                int row = 2;
                foreach (var leaf in Leaves(serializer.DeserializeObject(json), "", 0))
                {
                    if (row > 100000) throw new InvalidOperationException("Более 100000 строк детализации. Сохраните отчёт в JSON.");
                    sheet.Cells[row, 1].Value = leaf.path;
                    sheet.Cells[row, 2].Value = leaf.value;
                    row++;
                }
                sheet.Cells[1, 1, 1, 2].Style.Font.Bold = true;
                sheet.Column(1).Width = 65; sheet.Column(2).Width = 100; sheet.Column(2).Style.WrapText = true;
                sheet.View.FreezePanes(2, 1); sheet.Cells[1, 1, row - 1, 2].AutoFilter = true;
                DiagnosticFiles.Write(target, book.GetAsByteArray(), overwrite);
            }
        }

        private static IEnumerable<(string path, string value)> Leaves(object node, string path, int depth)
        {
            if (depth > 40) throw new InvalidOperationException("Слишком глубокая структура отчёта.");
            if (node is IDictionary<string, object> map)
            {
                foreach (var pair in map.OrderBy(p => p.Key))
                    foreach (var value in Leaves(pair.Value, path.Length == 0 ? pair.Key : path + "." + pair.Key, depth + 1)) yield return value;
            }
            else if (node is IEnumerable list && !(node is string))
            {
                int i = 0;
                foreach (object item in list)
                    foreach (var value in Leaves(item, path + "[" + i++ + "]", depth + 1)) yield return value;
            }
            else yield return (path, node?.ToString() ?? "");
        }
    }
}
