using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using PluginForRevit2022.Core.Clashes;
using PluginForRevit2022.Mcp;

namespace PluginForRevit2022.Core
{
    internal static class ClashReportFileOperations
    {
        internal static ClashReport Read(IEnumerable<string> paths)
        {
            var files = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) throw new ArgumentException("Укажите reportPaths — HTML/XML отчёты Navisworks.");
            foreach (string file in files) if (!File.Exists(file)) throw new FileNotFoundException("Отчёт не найден.", file);
            var report = NavisReportReader.Read(files);
            ClashStatusStore.Load(report);
            return report;
        }

        internal static Dictionary<string, object> Row(ClashItem i) => new Dictionary<string, object>
        {
            ["key"] = i.Key, ["source"] = i.Source, ["test"] = i.Test, ["name"] = i.Name,
            ["status"] = i.Status.ToString(), ["comment"] = i.Comment,
            ["element1Id"] = i.Item1.Id, ["element1File"] = i.Item1.File,
            ["element2Id"] = i.Item2.Id, ["element2File"] = i.Item2.File,
            ["screenshotPath"] = i.EffectiveImagePath, ["hasScreenshot"] = !string.IsNullOrEmpty(i.EffectiveImagePath),
            ["screenshotExists"] = !string.IsNullOrEmpty(i.EffectiveImagePath) && File.Exists(i.EffectiveImagePath),
        };

        private static ClashStatus Status(string text)
        {
            foreach (ClashStatus s in ClashStatusText.All)
                if (string.Equals(s.ToString(), text, StringComparison.OrdinalIgnoreCase) || ClashStatusText.Of(s) == text) return s;
            throw new ArgumentException("Неизвестный статус: " + text + ". Допустимы New, InWork, Passed, Fake, Fixed.");
        }

        internal static ClashItem Find(ClashReport report, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Укажите key из режима list.");
            var items = report.Items.Where(i => i.Key == key).ToList();
            if (items.Count != 1) throw new ArgumentException("Коллизия по key не найдена однозначно: " + key);
            return items[0];
        }

        internal static bool InModel(ClashItem item, IList<string> modelNames) =>
            ClashRevit.IsModel(item.Item1.File, modelNames) || ClashRevit.IsModel(item.Item2.File, modelNames);

        public static object Manage(Document doc, string author, McpArgs args)
        {
            var report = Read(args.StringList("reportPaths"));
            string action = args.String("action") ?? "list";
            bool dry = args.Bool("dryRun") ?? true;
            var result = new Dictionary<string, object> { ["action"] = action, ["dryRun"] = dry, ["warnings"] = report.Warnings };
            if (action == "list")
            {
                IEnumerable<ClashItem> items = report.Items;
                if (args.String("status") != null) items = items.Where(i => i.Status == Status(args.String("status")));
                var all = items.ToList(); int offset = Math.Max(0, args.Int("offset") ?? 0), limit = Math.Max(1, Math.Min(300, args.Int("limit") ?? 100));
                result["total"] = all.Count; result["items"] = all.Skip(offset).Take(limit).Select(Row).ToList();
                result["nextOffset"] = offset + limit < all.Count ? (object)(offset + limit) : null;
            }
            else if (action == "update")
            {
                var item = Find(report, args.String("key")); result["before"] = Row(item);
                if (args.String("status") != null) item.Status = Status(args.String("status"));
                // Отсутствующее поле не меняет комментарий; пустая строка может явно очистить его.
                var changes = args.StringMap("changes");
                if (changes.TryGetValue("comment", out string comment)) item.Comment = comment;
                string image = args.String("imagePath"); bool remove = args.Bool("removeScreenshot") ?? false;
                if (image != null && remove) throw new ArgumentException("imagePath и removeScreenshot нельзя задавать одновременно.");
                byte[] png = null;
                if (image != null)
                {
                    using (var stream = File.OpenRead(image))
                        png = ClashScreenshot.Encode(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
                }
                if (!dry)
                {
                    item.ChangedBy = author ?? Environment.UserName;
                    item.ChangedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    ClashStatusStore.Save(item);
                    if (image != null || remove) ClashStatusStore.SetScreenshot(item, png);
                }
                result["after"] = Row(item); result["screenshotChange"] = image != null ? "replace" : remove ? "remove" : "none";
                result["summary"] = dry ? "Предпросмотр. Файлы отчёта не изменены." : "Изменения сохранены. Открытый в плагине отчёт перечитайте для обновления карточек.";
            }
            else if (action == "export")
            {
                string scope = args.String("scope") ?? "all";
                var items = report.Items;
                if (scope == "active_model")
                {
                    var modelNames = ClashRevit.ModelNames(doc);
                    if (doc == null || doc.IsFamilyDocument || modelNames.Count == 0)
                        throw new InvalidOperationException("Для scope=active_model откройте проект Revit.");
                    items = items.Where(item => InModel(item, modelNames)).ToList();
                }
                else if (scope != "all") throw new ArgumentException("scope: all или active_model.");

                bool overwrite = args.Bool("overwrite") ?? false;
                string target = DiagnosticFiles.Target(args.String("savePath"), ".xlsx", overwrite);
                if (report.Files.Any(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Результат не должен заменять исходный отчёт.");
                if (!dry)
                {
                    ElementIdJournal.FlushAll();
                    ClashExcel.Write(items, target, overwrite);
                }
                result["scope"] = scope;
                result["records"] = items.Count;
                result["savePath"] = target;
                result["saved"] = !dry;
            }
            else throw new ArgumentException("action: list, update или export.");
            return result;
        }

        public static object Compare(McpArgs args)
        {
            var before = Read(args.StringList("beforePaths")); var after = Read(args.StringList("afterPaths"));
            var diff = ClashReportComparison.Compare(before.Items, after.Items, args.Bool("matchElements") ?? true);
            int limit = Math.Max(1, Math.Min(300, args.Int("limit") ?? 100));
            return new Dictionary<string, object>
            {
                ["newCount"] = diff.Added.Count, ["disappearedCount"] = diff.Removed.Count, ["commonCount"] = diff.Common.Count,
                ["new"] = diff.Added.Take(limit).Select(Row).ToList(), ["disappeared"] = diff.Removed.Take(limit).Select(Row).ToList(),
                ["changedStatus"] = diff.Common.Where(p => p.before.Status != p.after.Status || p.before.NavisStatus != p.after.NavisStatus)
                    .Take(limit).Select(p => new { before = Row(p.before), after = Row(p.after) }).ToList(),
                ["ambiguousMatches"] = diff.Ambiguous.Take(limit).ToList(), ["warnings"] = before.Warnings.Concat(after.Warnings).ToList(),
                ["summary"] = "Исчезнувшие коллизии требуют проверки: статус «Исправлено» автоматически не присваивается. Сопоставление по ключу, затем по однозначной паре Revit ID и файлов в той же проверке.",
            };
        }

    }
}
