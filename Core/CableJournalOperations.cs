using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Revit;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Systems.Souye;
using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
using PluginForRevit2022.Core.CableJournal;

namespace PluginForRevit2022.Core
{
    internal sealed class CableJournalRequest
    {
        /// <summary>security, aps или souye.</summary>
        public string System { get; set; }
        /// <summary>Значение параметра «Комментарии», по которому отбираются элементы.</summary>
        public string Comments { get; set; }
        /// <summary>СОУЭ: eom или adsk.</summary>
        public string ParameterStandard { get; set; }
        public string TemplatePath { get; set; }
        public string SavePath { get; set; }
        public bool Overwrite { get; set; }
        public bool DryRun { get; set; } = true;
    }

    /// <summary>
    /// MCP-версия команд кабельного журнала: без окон, все данные приходят параметрами.
    /// Расчёт тот же, что у команд на ленте.
    /// </summary>
    internal static class CableJournalOperations
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

        public static Dictionary<string, object> Export(Document doc, CableJournalRequest request)
        {
            string system = (request.System ?? "").Trim().ToLowerInvariant();
            if (system != "security" && system != "aps" && system != "souye")
            {
                throw new ArgumentException("system: security, aps или souye.");
            }
            if (system != "security" && string.IsNullOrWhiteSpace(request.Comments))
            {
                throw new ArgumentException("Укажите comments — значение параметра «Комментарии» (например, АПС).");
            }
            if (!string.IsNullOrWhiteSpace(request.TemplatePath))
            {
                ValidateTemplate(request.TemplatePath);
            }
            else if (!request.DryRun)
            {
                throw new ArgumentException("Укажите templatePath — файл шаблона Excel.");
            }
            if (!request.DryRun)
            {
                CheckSavePath(request);
            }

            var timer = Stopwatch.StartNew();
            var messages = new List<string>();
            List<CableRecord> records;
            using (JournalDialog.Capture(messages))
            using (var cancel = new TimeoutCancel())
            {
                records = Build(doc, system, request, cancel.Monitor, messages);
            }

            if (records == null || records.Count == 0)
            {
                throw new InvalidOperationException("Кабельный журнал не сформирован." +
                    (messages.Count > 0 ? " " + string.Join(" | ", messages.Distinct().Take(10)) : " Нет подходящих элементов."));
            }

            if (!request.DryRun)
            {
                new ExcelExporter().ExportCableRecords(request.TemplatePath, request.SavePath, records);
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["system"] = system,
                ["records"] = records.Count,
                ["fallbackLengths"] = records.Count(r => r.IsFallbackForLength),
                ["sample"] = records.Take(20).Select(Row).ToList(),
                ["messages"] = messages.Distinct().Take(100).ToList(),
                ["savedTo"] = request.DryRun ? null : request.SavePath,
                ["log"] = ExecutionLogger.GetLogPath(),
                ["elapsedMs"] = timer.ElapsedMilliseconds,
                ["summary"] = request.DryRun
                    ? $"Предпросмотр: строк журнала: {records.Count}. Для записи файла повторите с dryRun=false."
                    : $"Журнал сохранён: {request.SavePath}, строк: {records.Count}.",
            };
        }

        private static List<CableRecord> Build(Document doc, string system, CableJournalRequest request,
                                               CancellationMonitor cancel, List<string> messages)
        {
            string comments = request.Comments?.Trim();
            switch (system)
            {
                case "aps":
                {
                    var controllers = new ApsDataCollector().CollectApsControllersWithLoopsByParam(doc, comments, cancel);
                    if (controllers == null || controllers.Count == 0) return null;
                    return new ApsCableRecordBuilder(new ApsElementParser(), doc, LengthMode.Route, 1.3).Build(controllers, comments, cancel);
                }
                case "souye":
                    return BuildSouye(doc, request, comments, cancel, messages);
                default:
                {
                    var parser = new SecurityElementParser();
                    var collector = new SecurityDataCollector(parser);
                    bool all = string.IsNullOrEmpty(comments);
                    var controllers = all ? collector.CollectControllersWithLoops(doc) : collector.CollectControllersWithLoopsByParam(doc, comments);
                    if (controllers == null || controllers.Count == 0) return null;
                    var builder = new SecurityCableRecordBuilder(parser, doc, LengthMode.Route, 1.3);
                    return all ? builder.Build(controllers) : builder.Build(controllers, comments);
                }
            }
        }

        private static List<CableRecord> BuildSouye(Document doc, CableJournalRequest request, string comments,
                                                    CancellationMonitor cancel, List<string> messages)
        {
            string standard = (request.ParameterStandard ?? "").Trim().ToLowerInvariant();
            if (standard != "eom" && standard != "adsk")
            {
                throw new ArgumentException("Для СОУЭ укажите parameterStandard: eom или adsk.");
            }
            var mapper = new SouyeParameterMapper(standard == "eom" ? SouyeParameterStandard.Eom : SouyeParameterStandard.Adsk);
            var collected = new SouyeDataCollector(new SouyeElementParser(mapper)).Collect(doc, comments);
            if (!collected.IsSuccess)
            {
                messages.Add(collected.ErrorMessage);
                return null;
            }
            List<string> errors = new SouyeDataValidator(mapper).Validate(collected.Data);
            if (errors.Count > 0)
            {
                messages.AddRange(errors);
                return null;
            }
            var built = new SouyeCableRecordBuilder(doc, LengthMode.Route, 1.4).Build(collected.Data, comments, cancel);
            if (built == null || !built.IsSuccess)
            {
                messages.Add(built?.ErrorMessage ?? "Ошибка при построении записей: результат null.");
                return null;
            }
            return built.Records;
        }

        private static void ValidateTemplate(string path)
        {
            TemplateValidationResult result = new TemplateValidator().Validate(path, new TemplateStructure());
            if (!result.IsValid)
            {
                throw new ArgumentException("Шаблон не подходит: " + result.Message);
            }
        }

        private static void CheckSavePath(CableJournalRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SavePath))
            {
                throw new ArgumentException("Укажите savePath — куда сохранить журнал (.xlsx).");
            }
            if (File.Exists(request.SavePath) && !request.Overwrite)
            {
                throw new InvalidOperationException($"Файл уже существует: {request.SavePath}. Укажите другой путь или overwrite=true.");
            }
        }

        private static Dictionary<string, object> Row(CableRecord r) => new Dictionary<string, object>
        {
            ["startLevel"] = r.StartLevel,
            ["startPoint"] = r.StartPoint,
            ["endLevel"] = r.EndLevel,
            ["endPoint"] = r.EndPoint,
            ["cableType"] = r.CableType,
            ["cableCategory"] = r.CableCategory,
            ["purpose"] = r.Purpose,
            ["length"] = r.Length,
            ["fallbackLength"] = r.IsFallbackForLength,
        };

        /// <summary>Сторожевой таймер, как в команде АПС: запрашивает отмену по истечении лимита.</summary>
        private sealed class TimeoutCancel : IDisposable
        {
            private readonly global::System.Threading.Timer _timer;

            public CancellationMonitor Monitor { get; } = new CancellationMonitor();

            public TimeoutCancel()
            {
                _timer = new global::System.Threading.Timer(_ => Monitor.RequestCancel($"timeout {Timeout}"), null, Timeout,
                    global::System.Threading.Timeout.InfiniteTimeSpan);
            }

            public void Dispose() => _timer.Dispose();
        }
    }
}
