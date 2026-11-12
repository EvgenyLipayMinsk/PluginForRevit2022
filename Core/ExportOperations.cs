using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Пакетный экспорт листов в PDF и DWG.</summary>
    internal static class ExportOperations
    {
        public static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        public static Dictionary<string, object> ExportSheets(Document doc, List<string> sheets, string format, string folder, string dwgSetup, bool combine, bool dryRun)
        {
            string kind = (format ?? "").Trim().ToLowerInvariant();
            if (kind != "pdf" && kind != "dwg") throw new ArgumentException("format: pdf или dwg.");
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Укажите folder.");
#if REVIT2021
            if (kind == "pdf") throw new NotSupportedException("В Revit 2021 нет API экспорта в PDF. Используйте печать или DWG.");
#endif
            List<ViewSheet> selected = ResolveSheets(doc, sheets);
            var files = selected.Select(s => SafeFileName($"{s.SheetNumber} - {s.Name}") + "." + kind).ToList();
            if (kind == "pdf" && combine) files = new List<string> { SafeFileName(doc.Title) + ".pdf" };

            if (!dryRun)
            {
                Directory.CreateDirectory(folder);
                if (kind == "dwg") ExportDwg(doc, selected, folder, dwgSetup);
                else ExportPdf(doc, selected, folder, combine);
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["format"] = kind,
                ["sheets"] = selected.Count,
                ["folder"] = folder,
                ["files"] = files,
                ["summary"] = dryRun
                    ? $"Предпросмотр: будет выгружено листов: {selected.Count} в {folder}. Для выгрузки повторите с dryRun=false."
                    : $"Выгружено листов: {selected.Count} в {folder}.",
            };
        }

        private static List<ViewSheet> ResolveSheets(Document doc, List<string> values)
        {
            List<ViewSheet> all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber).ToList();
            if (values == null || values.Count == 0) throw new ArgumentException("Укажите sheets — номера листов, или [\"*\"] для всех.");
            if (values.Count == 1 && values[0].Trim() == "*") return all;
            var result = new List<ViewSheet>();
            foreach (string value in values)
            {
                ViewSheet sheet = all.FirstOrDefault(s => string.Equals(s.SheetNumber, value.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    ?? throw new ArgumentException($"Лист «{value}» не найден.");
                if (!result.Contains(sheet)) result.Add(sheet);
            }
            return result;
        }

        private static void ExportDwg(Document doc, List<ViewSheet> sheets, string folder, string setup)
        {
            DWGExportOptions options = string.IsNullOrWhiteSpace(setup) ? new DWGExportOptions() : DWGExportOptions.GetPredefinedOptions(doc, setup.Trim())
                ?? throw new ArgumentException($"Настройка экспорта DWG «{setup}» не найдена.");
            options.MergedViews = true;
            foreach (ViewSheet sheet in sheets)
            {
                if (!doc.Export(folder, SafeFileName($"{sheet.SheetNumber} - {sheet.Name}"), new List<ElementId> { sheet.Id }, options))
                {
                    throw new InvalidOperationException($"Revit не выгрузил лист {sheet.SheetNumber}.");
                }
            }
        }

        private static void ExportPdf(Document doc, List<ViewSheet> sheets, string folder, bool combine)
        {
#if REVIT2021
            throw new NotSupportedException("В Revit 2021 нет API экспорта в PDF.");
#else
            if (combine)
            {
                var options = new PDFExportOptions { Combine = true, FileName = SafeFileName(doc.Title) };
                if (!doc.Export(folder, sheets.Select(s => s.Id).ToList(), options)) throw new InvalidOperationException("Revit не выгрузил PDF.");
                return;
            }
            foreach (ViewSheet sheet in sheets)
            {
                var options = new PDFExportOptions { Combine = true, FileName = SafeFileName($"{sheet.SheetNumber} - {sheet.Name}") };
                if (!doc.Export(folder, new List<ElementId> { sheet.Id }, options)) throw new InvalidOperationException($"Revit не выгрузил лист {sheet.SheetNumber}.");
            }
#endif
        }
    }
}
