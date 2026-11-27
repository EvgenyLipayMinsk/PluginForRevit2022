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
        public long? ControllerElementId { get; set; }
        public string ApsLineKey { get; set; }
        public bool PreviewConnectionOrder { get; set; }
        public List<string> ConnectionOrder { get; set; }
        public bool AcceptFallbackLengths { get; set; }
        /// <summary>СОУЭ: eom или adsk.</summary>
        public string ParameterStandard { get; set; }
        public string Form { get; set; }
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
            if (!string.IsNullOrWhiteSpace(request.ApsLineKey))
            {
                if (system != "aps") throw new ArgumentException("apsLineKey используется только для АПС.");
                var parts = request.ApsLineKey.Split(new[] { '|' }, 5);
                if (parts.Length != 5 || !string.Equals(parts[0], "aps", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(parts[2], out _) || !int.TryParse(parts[3], out _) || string.IsNullOrWhiteSpace(parts[4]))
                    throw new ArgumentException("apsLineKey: используйте ключ линии АПС из list_marked_lines.");
                if (!string.IsNullOrWhiteSpace(request.Comments) &&
                    !string.Equals(request.Comments.Trim(), parts[4].Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("comments не совпадает с комментарием в apsLineKey.");
                request.Comments = parts[4].Trim();
                request.ApsLineKey = DeviceLineOperations.ApsKey(parts[1], parts[2], parts[3], request.Comments);
                if (request.PreviewConnectionOrder)
                {
                    if (!request.DryRun || request.ConnectionOrder?.Count > 0)
                        throw new ArgumentException("Для предложения порядка оставьте dryRun=true и не передавайте connectionOrder.");
                    return PreviewApsConnection(doc, request);
                }
                if (request.ConnectionOrder == null || request.ConnectionOrder.Count == 0)
                    throw new ArgumentException("Передайте подтверждённый connectionOrder или запросите previewConnectionOrder=true.");
            }
            else if (request.PreviewConnectionOrder || request.ConnectionOrder?.Count > 0 || request.AcceptFallbackLengths)
            {
                throw new ArgumentException("Для работы с порядком подключения укажите apsLineKey.");
            }
            if (system != "security" && string.IsNullOrWhiteSpace(request.Comments) && !(system == "souye" && request.ControllerElementId.HasValue))
            {
                throw new ArgumentException("Укажите comments — значение параметра «Комментарии» (например, АПС).");
            }
            string formName = (request.Form ?? "aerbim").Trim().ToLowerInvariant();
            if (formName != "aerbim" && formName != "a3")
                throw new ArgumentException("form: aerbim или a3.");
            var form = formName == "a3" ? CableJournalForm.A3 : CableJournalForm.AerBim;
            var exporter = new JournalFormExporter(form);
            if (form == CableJournalForm.A3)
            {
                var validation = exporter.Validate(JournalFormExporter.BuiltInA3, new TemplateStructure());
                if (!validation.IsValid) throw new ArgumentException(validation.Message);
            }
            else if (!string.IsNullOrWhiteSpace(request.TemplatePath))
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
            List<CableRecord> records = Calculate(doc, request, messages);

            bool connectionSaved = false;
            if (!request.DryRun)
            {
                if (request.ApsLineKey == null)
                    exporter.ExportCableRecords(request.TemplatePath, request.SavePath, records);
                else
                {
                    if (!request.AcceptFallbackLengths && records.Any(record => record.IsFallbackForLength))
                        throw new InvalidOperationException("Некоторые длины рассчитаны с обходом разрыва или по прямой. Проверьте результат dryRun и для выгрузки укажите acceptFallbackLengths=true.");
                    using (var group = new TransactionGroup(doc, "AerBIM: подтвердить порядок и выгрузить КЖ"))
                    {
                        if (group.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("Не удалось начать сохранение порядка подключения.");
                        DeviceLineOperations.SaveConnectionOrder(doc, request.ApsLineKey, request.ConnectionOrder);
                        exporter.ExportCableRecords(request.TemplatePath, request.SavePath, records);
                        connectionSaved = group.Assimilate() == TransactionStatus.Committed;
                        if (!connectionSaved) messages.Add("Журнал выгружен. Revit не сохранил порядок подключения в модели.");
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["system"] = system,
                ["form"] = formName,
                ["records"] = records.Count,
                ["fallbackLengths"] = records.Count(r => r.IsFallbackForLength),
                ["sample"] = records.Take(20).Select(Row).ToList(),
                ["messages"] = messages.Distinct().Take(100).ToList(),
                ["savedTo"] = request.DryRun ? null : request.SavePath,
                ["apsLineKey"] = request.ApsLineKey,
                ["connectionOrderSaved"] = connectionSaved,
                ["log"] = ExecutionLogger.GetLogPath(),
                ["elapsedMs"] = timer.ElapsedMilliseconds,
                ["summary"] = request.DryRun
                    ? $"Предпросмотр: строк журнала: {records.Count}. Для записи файла повторите с dryRun=false."
                    : $"Журнал сохранён: {request.SavePath}, строк: {records.Count}.",
            };
        }

        internal static List<CableRecord> Calculate(Document doc, CableJournalRequest request, List<string> messages)
        {
            string system = (request.System ?? "").Trim().ToLowerInvariant();
            if (system != "aps" && system != "security" && system != "souye") throw new ArgumentException("system: aps, security, souye.");
            if (system != "security" && string.IsNullOrWhiteSpace(request.Comments) && !(system == "souye" && request.ControllerElementId.HasValue)) throw new ArgumentException("Укажите comments.");
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

            MarkingSettings.ApplyLineReserve(records);

            return records;
        }

        private static List<CableRecord> Build(Document doc, string system, CableJournalRequest request,
                                               CancellationMonitor cancel, List<string> messages)
        {
            string comments = request.Comments?.Trim();
            switch (system)
            {
                case "aps":
                {
                    var parser = new ApsElementParser();
                    List<ApsControllerWithLoops> controllers;
                    if (request.ApsLineKey == null)
                    {
                        controllers = new ApsDataCollector(parser).CollectApsControllersWithLoopsByParam(doc, comments, cancel);
                        if (controllers == null || controllers.Count == 0) return null;
                    }
                    else
                    {
                        var (controller, loop, devices) = ApsConnection(doc, request, parser, cancel, messages);
                        var ordered = LineConnectionOrder.Ordered(devices, request.ConnectionOrder);
                        var byId = loop.Devices.ToDictionary(device => device.BaseInfo.Element.UniqueId);
                        loop.Devices = ordered.Select(device => byId[device.UniqueId]).ToList();
                        controller.Loops = new List<ApsLoopGroup> { loop };
                        controllers = new List<ApsControllerWithLoops> { controller };
                    }
                    return new ApsCableRecordBuilder(parser, doc, LengthMode.Route, MarkingSettings.CableFactor("aps")).Build(controllers, comments, cancel);
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
                    var builder = new SecurityCableRecordBuilder(parser, doc, LengthMode.Route, MarkingSettings.CableFactor("security"));
                    return all ? builder.Build(controllers) : builder.Build(controllers, comments);
                }
            }
        }

        private static (ApsControllerWithLoops controller, ApsLoopGroup loop, List<FamilyInstance> devices) ApsConnection(
            Document doc, CableJournalRequest request, ApsElementParser parser, CancellationMonitor cancel, List<string> messages)
        {
            string key = request.ApsLineKey;
            var controllers = new ApsDataCollector(parser).CollectApsControllersWithLoopsByParam(doc, request.Comments, cancel, key)
                ?? new List<ApsControllerWithLoops>();
            var candidates = (from controller in controllers
                              from loop in controller.Loops
                              where loop.Devices.Any(device => DeviceLineOperations.KeyOf(device.BaseInfo.Element) == key)
                              select new { controller, loop }).ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException((candidates.Count == 0 ? "Шлейф не найден у прибора." : "Найдено несколько приборов для одного шлейфа.") +
                    (messages.Count == 0 ? "" : " " + string.Join(" | ", messages.Distinct().Take(10))));
            var match = candidates[0];
            var devices = match.loop.Devices.Select(device => (FamilyInstance)device.BaseInfo.Element).ToList();
            if (!new HashSet<string>(DeviceLineOperations.DevicesOf(doc, key).Select(device => device.UniqueId))
                .SetEquals(devices.Select(device => device.UniqueId)))
                throw new InvalidOperationException("Часть устройств линии не прошла проверку КЖ. " + string.Join(" | ", messages.Distinct().Take(10)));
            return (match.controller, match.loop, devices);
        }

        private static Dictionary<string, object> PreviewApsConnection(Document doc, CableJournalRequest request)
        {
            var messages = new List<string>();
            using (JournalDialog.Capture(messages))
            using (var cancel = new TimeoutCancel())
            {
                var (controller, loop, devices) = ApsConnection(doc, request, new ApsElementParser(), cancel.Monitor, messages);
                var stored = LineConnectionOrder.Read(doc);
                bool hasSavedOrder = stored.TryGetValue(request.ApsLineKey, out var entry);
                var byId = devices.ToDictionary(device => device.UniqueId);
                var retained = hasSavedOrder
                    ? entry.Order.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList()
                    : null;
                bool savedOrderIsCurrent = retained != null && retained.Count == devices.Count && retained.Count == entry.Order.Count;
                var suggestion = savedOrderIsCurrent
                    ? new LineConnectionOrder.Suggestion { Devices = retained, Hint = "Показан сохранённый порядок. Сверьте его с трассой." }
                    : LineConnectionOrder.Suggest(doc, controller.Controller.Element, devices, request.ApsLineKey, retained, true, false);
                try { LineConnectionOrder.ValidateAddresses(devices); }
                catch (ArgumentException ex) { messages.Add(ex.Message); }
                return new Dictionary<string, object>
                {
                    ["dryRun"] = true,
                    ["apsLineKey"] = request.ApsLineKey,
                    ["ring"] = loop.TypeLoop == TypeLoop.Annular,
                    ["savedOrderIsCurrent"] = savedOrderIsCurrent,
                    ["suggestedOrder"] = suggestion.Devices.Select(device => device.UniqueId).ToList(),
                    ["devices"] = devices.Select(device => new
                    {
                        uniqueId = device.UniqueId,
                        id = RevitIds.IdOf(device.Id),
                        address = LineConnectionOrder.Address(device),
                        estimated = suggestion.Estimated.Contains(device.UniqueId),
                        needsReview = suggestion.Review.Contains(device.UniqueId),
                    }).ToList(),
                    ["hint"] = suggestion.Hint,
                    ["messages"] = messages.Distinct().Take(100).ToList(),
                };
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
            var mapper = new SouyeParameterMapper(standard == "eom" ? SouyeParameterStandard.Eom : SouyeParameterStandard.Adsk,
                MarkingSettings.CurrentFloorParameter()); // этаж — из «Настроек маркировки»
            Element rack = null;
            if (request.ControllerElementId.HasValue)
            {
                rack = doc.GetElement(RevitIds.ToElementId(request.ControllerElementId.Value));
                if (rack == null || !SouyeLineIdentity.IsRack(rack))
                    throw new ArgumentException("controllerElementId должен указывать на стойку СОУЭ категории «Электрооборудование».");
                if (string.IsNullOrWhiteSpace(comments)) comments = DeviceMarkingOperations.CommentOf(rack);
            }
            var collected = new SouyeDataCollector(new SouyeElementParser(mapper)).Collect(doc, comments, rack);
            if (!collected.IsSuccess)
            {
                messages.Add(collected.ErrorMessage);
                return null;
            }
            List<string> errors = new SouyeDataValidator(mapper).ValidateGroups(collected.Groups);
            if (errors.Count > 0)
            {
                messages.AddRange(errors);
                return null;
            }
            var built = new SouyeCableRecordBuilder(doc, LengthMode.Route, MarkingSettings.CableFactor("souye")).Build(collected.Groups, cancel);
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
