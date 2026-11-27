using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Systems.Security.Models;
using Autodesk.Revit.DB;
using PluginForRevit2022.Core.CableJournal;

namespace PluginForRevit2022.Core
{
    internal sealed class MarkDevicesRequest
    {
        /// <summary>Устройства шлейфа. При ручном порядке номера идут по этому списку.</summary>
        public List<long> Ids { get; set; } = new List<long>();
        /// <summary>aps, security или souye.</summary>
        public string System { get; set; }
        public string Building { get; set; }
        internal bool FunctionalZones { get; set; }
        public string Controller { get; set; }
        public string Bco { get; set; }
        public string Line { get; set; }
        public string Comments { get; set; }
        /// <summary>Этаж числом для уровней без номера в имени («Тех. Пространство»); остальные — по номеру из имени уровня.</summary>
        public string Floor { get; set; }
        /// <summary>СОУЭ: eom или adsk (какой параметр этажа заполнять).</summary>
        public string ParameterStandard { get; set; }
        public int StartNumber { get; set; } = 1;
        /// <summary>Шаг рядов для порядка snake, мм.</summary>
        public double RowMm { get; set; } = 3000;
        /// <summary>manual — порядок списка; route — по трассам журнала от прибора; snake — змейкой по плану.</summary>
        public string Order { get; set; } = "manual";
        /// <summary>Прибор (id): от него считается трасса при Order=route; в него записываются данные для журнала.</summary>
        public long? ControllerElementId { get; set; }
        public bool DryRun { get; set; } = true;
        // Только один модальный сценарий «предпросмотр → подтверждение» с тем же объектом запроса.
        internal bool ReusePreviewOrder { get; set; }
        internal (Document document, string settings, List<ElementId> input, List<ElementId> ordered, List<string> warnings)? PreviewOrder { get; set; }
    }

    /// <summary>Заполняет параметры устройств так, как их читает кабельный журнал.</summary>
    internal static class DeviceMarkingOperations
    {
        internal const string ParamBuilding = "ARBM_Корпус";
        internal const string ParamController = "ARBM_Номер прибора";
        internal const string ParamBco = "ARBM_ПСБ_БЦО";
        internal const string ParamLine = "ARBM_Номер линии";
        internal const string ParamAddress = "ARBM_ПСБ_Адрес устройства";
        internal const string ParamMark = "ARBM_SP_ElementMark";
        internal const string ParamProductName = "ARBM_ПС_Имя изделия";
        internal const string ParamGrouping = "Группирование";

        internal const int LineCapacity = 250;

        /// <summary>«Комментарии» элемента без пробелов по краям.</summary>
        public static string CommentOf(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.Trim() ?? "";

        /// <summary>«Комментарии» элемента равны заданному (без учёта регистра и пробелов по краям).</summary>
        public static bool HasComment(Element e, string comment) =>
            string.Equals(e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.Trim(), (comment ?? "").Trim(), StringComparison.OrdinalIgnoreCase);


        public static Dictionary<string, object> Mark(Document doc, MarkDevicesRequest request)
        {
            string system = (request.System ?? "").Trim().ToLowerInvariant();
            if (system != "aps" && system != "security" && system != "souye")
            {
                throw new ArgumentException("system: aps, security или souye.");
            }
            Require(request.Controller, "controller (номер прибора)");
            Require(request.Line, "line (номер линии)");
            request.FunctionalZones = system == "souye" && MarkingSettings.CurrentJournal().SouyeFunctionalZones;
            if (system == "aps") Require(request.Building, "building (ARBM_Корпус)");
            RequireNumber(request.Controller, "controller");
            RequireNumber(request.Line, "line");
            if (system == "aps" && !ApsElementParser.IsValidBuildingCode(request.Building.Trim()))
            {
                throw new ArgumentException($"Корпус «{request.Building.Trim()}»: используйте латинские буквы, цифры и при необходимости уточнение в скобках (например M2 или UO(OW)).");
            }
            FamilyInstance controller = ResolveController(doc, request.ControllerElementId);
            if (system == "souye")
            {
                if (!SouyeLineIdentity.Number(request.Controller, out int rack) ||
                    !SouyeLineIdentity.Number(request.Line, out int line) || line > SouyeLineIdentity.MaxLine)
                    throw new ArgumentException($"СОУЭ: укажите положительный номер прибора в стойке и номер линии от 1 до {SouyeLineIdentity.MaxLine}.");
                request.Controller = rack.ToString();
                request.Line = line.ToString();
                if (controller == null || !SouyeLineIdentity.IsRack(controller))
                    throw new ArgumentException("СОУЭ: выберите стойку в категории «Электрооборудование».");
                if (string.IsNullOrWhiteSpace(request.Comments))
                    request.Comments = CommentOf(controller).Length == 0 ? "СОУЭ" : CommentOf(controller);
                request.FunctionalZones = request.FunctionalZones || HasLinkedSouyeLine(doc, controller, rack, line);
                request.Building = SouyeLineIdentity.NormalizeBuilding(request.Building) ?? "";
                if (!request.FunctionalZones) Require(request.Building, "building (ARBM_Корпус)");
                ValidateSouyeRack(doc, controller, request, rack);
            }
            else if (controller != null &&
                int.TryParse(RevitText.FindParameter(controller, ParamMark)?.AsString(), out int controllerNumber) &&
                controllerNumber != int.Parse(request.Controller))
            {
                throw new ArgumentException($"Выбран прибор №{controllerNumber}, в запросе указан №{request.Controller.Trim()}. Укажите один номер прибора до маркировки.");
            }
            Func<FamilyInstance, bool> sameSouyeLine = system == "souye" ? SouyeLineMembership(doc, controller, request) : null;
            // Комментарий линии задаётся при маркировке (любой, например «АПС»). Для АПС обязателен: по нему линия
            // и журнал отбирают устройства; без него номера прибора и линии не образуют линию
            if (system == "aps" && string.IsNullOrWhiteSpace(request.Comments))
                throw new ArgumentException("Укажите «Комментарии» линии (например АПС): в маркировке участвуют только устройства и трассы с этим значением.");

            List<FamilyInstance> devices = ResolveDevices(doc, request.Ids);
            var protectedLines = LineConnectionOrder.Read(doc);
            string targetKey = system == "aps"
                ? DeviceLineOperations.ApsKey(request.Building, request.Controller, request.Line, request.Comments) : null;
            if ((targetKey != null && protectedLines.ContainsKey(targetKey)) ||
                devices.Any(d => protectedLines.ContainsKey(DeviceLineOperations.KeyOf(d, true) ?? "")))
                throw new InvalidOperationException("Линия размечена по подключению. Используйте действия без перенумерации в «Диспетчере линий».");
            var warnings = new List<string>();
            MarkingSettings settings = MarkingSettings.Load();
            List<FamilyInstance> excluded = devices.Where(d => !settings.IsInLine(d.Symbol.FamilyName, system)).ToList();
            if (excluded.Count > 0)
            {
                warnings.Add($"нет галочки системы в настройках маркировки, исключены: {excluded.Count} ({string.Join(", ", excluded.Select(d => d.Symbol.FamilyName).Distinct())})");
                devices = devices.Except(excluded).ToList();
                if (devices.Count == 0) throw new InvalidOperationException("Ни у одного выбранного семейства нет галочки этой системы в настройках маркировки.");
            }
            // Правило маркировки: устройства и трассы линии — с одним «Комментарии» (АПС — «АПС»).
            // Остальное не участвует, даже если подходит по остальным условиям
            string comment = request.Comments?.Trim();
            if (!string.IsNullOrEmpty(comment))
            {
                List<FamilyInstance> uncommented = devices.Where(d => !HasComment(d, comment)).ToList();
                if (uncommented.Count > 0)
                {
                    warnings.Add($"нет «Комментарии» = «{comment}», исключены: {uncommented.Count} ({string.Join(", ", uncommented.Select(d => d.Symbol.FamilyName).Distinct().Take(5))})");
                    devices = devices.Except(uncommented).ToList();
                    if (devices.Count == 0)
                        throw new InvalidOperationException($"Ни у одного выбранного устройства нет «Комментарии» = «{comment}». Заполните его у устройств линии и повторите.");
                }
            }
            if (system == "souye")
            {
                if (devices.Any(device => !SouyeLineIdentity.IsDevice(device)))
                    throw new ArgumentException("Оповещатели СОУЭ должны быть в категории «Устройства вызова и оповещения».");
                if (string.Equals(request.Order, "route", StringComparison.OrdinalIgnoreCase))
                {
                    int rack = int.Parse(request.Controller);
                    int line = int.Parse(request.Line);
                    var foreign = devices.Where(device =>
                        (!string.IsNullOrWhiteSpace(ReadGrouping(device)) &&
                         !SouyeLineIdentity.MatchesLine(device, rack, line)) ||
                        ((request.FunctionalZones ? SouyeLineIdentity.TryLineNumbers(device, out _, out _) :
                            SouyeLineIdentity.TryKey(device, out _)) && !sameSouyeLine(device))).ToList();
                    if (foreign.Count > 0)
                    {
                        devices = devices.Except(foreign).ToList();
                        warnings.Add($"Исключены устройства другой линии по маркировке или «{ParamGrouping}»: {foreign.Count}.");
                        if (devices.Count == 0) throw new InvalidOperationException("В рамке нет устройств указанной линии СОУЭ.");
                    }
                }
            }
            // БЦО задаётся в «Настройках маркировки». Чему не задано — маркировка не начинается (MCP может передать bco явно)
            List<string> noBco = devices.Select(d => d.Symbol.FamilyName).Distinct()
                .Where(f => string.IsNullOrWhiteSpace(settings.BcoOf(f)) && string.IsNullOrWhiteSpace(request.Bco)).ToList();
            if (noBco.Count > 0)
            {
                throw new InvalidOperationException("Не задано БЦО в «Настройках маркировки» для семейств:\n" +
                    string.Join("\n", noBco.Select(f => "• " + f)) +
                    "\n\nОткройте «Маркировка» → «Настройки маркировки», задайте БЦО этим семействам и повторите маркировку.");
            }
            string order = (request.Order ?? "manual").Trim().ToLowerInvariant();
            if (order != "route" && order != "snake" && order != "manual") throw new ArgumentException("order: manual, route или snake.");

            string floorParam = MarkingSettings.CurrentFloorParameter();
            if (system == "souye") ValidateSouyeNumbers(doc, request, devices, sameSouyeLine);
            var changes = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            var result = new Dictionary<string, object>();
            // Обход читает модель. В транзакции остаются только запись параметров и её пробный откат.
            devices = ResolveOrder(doc, request, devices, order, floorParam, warnings);
            using (var transaction = new Transaction(doc, "AerBIM: маркировка устройств"))
            {
                transaction.Start();

                int number = request.StartNumber;
                foreach (FamilyInstance device in devices)
                {
                    string floor = FloorOf(doc, device, request.Floor);
                    string bco = settings.BcoOf(device.Symbol.FamilyName) ?? request.Bco?.Trim();
                    if (string.IsNullOrWhiteSpace(bco))
                    {
                        failures.Add($"id {RevitIds.IdOf(device.Id)}: для семейства «{device.Symbol.FamilyName}» не задано БЦО");
                        bco = "";
                    }
                    var values = ValuesFor(system, request, number.ToString(), bco);
                    if (floor == null)
                    {
                        failures.Add($"id {RevitIds.IdOf(device.Id)}: не удалось определить номер этажа. Проверьте «Уровень» и «Уровень спецификации». Для уровня без номера задайте этаж в окне «Этажи уровней без номера…».");
                    }
                    else
                    {
                        values[floorParam] = floor;
                    }

                    foreach (KeyValuePair<string, string> pair in values)
                    {
                        string error = RevitText.TrySet(device, pair.Key, pair.Value);
                        if (error != null) failures.Add($"id {RevitIds.IdOf(device.Id)}, {pair.Key}: {error}");
                    }
                    ClearForeign(device, system, failures);
                    if (SouyeLineIdentity.IsDevice(device))
                        SouyeLineIdentity.LinkRack(device, request.FunctionalZones ? controller.UniqueId : null);
                    // Пустое значение не затирает существующие «Комментарии» устройств.
                    if (!string.IsNullOrWhiteSpace(request.Comments))
                    {
                        string commentsError = RevitText.TrySet(device.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), "Комментарии", request.Comments);
                        if (commentsError != null) failures.Add($"id {RevitIds.IdOf(device.Id)}, Комментарии: {commentsError}");
                    }

                    changes.Add(new Dictionary<string, object>
                    {
                        ["number"] = number,
                        ["id"] = RevitIds.IdOf(device.Id),
                        ["name"] = device.Name,
                        ["floor"] = floor,
                        ["bco"] = bco,
                    });
                    number++;
                }

                List<Dictionary<string, object>> controllerChanges = controller == null
                    ? new List<Dictionary<string, object>>()
                    : FillController(doc, controller, system, request, floorParam, failures, warnings);
                result["controller"] = controller == null ? null : new Dictionary<string, object>
                {
                    ["id"] = RevitIds.IdOf(controller.Id),
                    ["name"] = controller.Name,
                    ["changes"] = controllerChanges,
                };

                // Всё или ничего: частично размеченный шлейф журнал не примет.
                if (request.DryRun || failures.Count > 0)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit не применил маркировку.");
                }
            }

            int others = OtherDevicesOnLine(doc, system, request, devices, sameSouyeLine);
            if (others > 0)
            {
                warnings.Add($"на этой линии уже размечено устройств вне выбора: {others} — номера могут повториться");
            }
            int occupied = devices.Count + others;
            if (occupied > LineCapacity)
            {
                warnings.Add($"на линии {occupied} адресов при ёмкости {LineCapacity}");
            }

            bool applied = !request.DryRun && failures.Count == 0;
            if (controller == null)
            {
                warnings.Add("прибор не указан: параметры прибора для журнала не заполнены");
            }
            foreach (KeyValuePair<string, object> pair in new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["applied"] = applied,
                ["system"] = system,
                ["order"] = order,
                ["lineAddresses"] = occupied,
                ["lineCapacity"] = LineCapacity,
                ["devices"] = changes,
                ["failures"] = failures.Distinct().Take(100).ToList(),
                ["warnings"] = warnings.Distinct().Take(50).ToList(),
                ["summary"] = failures.Count > 0
                    ? $"Маркировка не применена: ошибок {failures.Count}. Проверьте параметры семейств."
                    : request.DryRun
                        ? $"Предпросмотр: будет размечено устройств: {changes.Count}. Для записи повторите с dryRun=false."
                        : $"Размечено устройств: {changes.Count}. Отменить можно в Revit через Ctrl+Z.",
            })
            {
                result[pair.Key] = pair.Value;
            }
            return result;
        }

        private static FamilyInstance ResolveController(Document doc, long? id)
        {
            if (id == null) return null;
            var controller = doc.GetElement(RevitIds.ToElementId(id.Value)) as FamilyInstance
                ?? throw new ArgumentException($"Прибор id {id} не найден.");
            if (controller.Category == null || RevitIds.IdOf(controller.Category.Id) != (long)BuiltInCategory.OST_ElectricalEquipment)
            {
                throw new ArgumentException($"Элемент id {id} не из категории «Электрооборудование».");
            }
            return controller;
        }

        /// <summary>
        /// Записывает в прибор то, по чему журнал находит его и связывает с устройствами:
        /// «Комментарии», этаж, пустой ARBM_Корпус и ARBM_ПС_Имя изделия (АПС — Корпус.БЦО.Номер, СОТС — ARK.Этаж.Номер,
        /// СОУЭ — БЦО.Номер, если имя пустое). Номер и БЦО берутся из маркировки прибора. Верное имя не меняется.
        /// </summary>
        private static List<Dictionary<string, object>> FillController(Document doc, FamilyInstance controller, string system,
            MarkDevicesRequest r, string floorParam, List<string> failures, List<string> warnings)
        {
            var changes = new List<Dictionary<string, object>>();
            long id = RevitIds.IdOf(controller.Id);
            void Put(string parameterName, Parameter parameter, string value)
            {
                string old = parameter?.AsString() ?? "";
                if (old == value) return;
                string error = RevitText.TrySet(parameter, parameterName, value);
                if (error != null)
                {
                    failures.Add($"прибор id {id}, {parameterName}: {error}");
                    return;
                }
                changes.Add(new Dictionary<string, object> { ["parameter"] = parameterName, ["old"] = old, ["new"] = value });
            }

            string building = r.Building?.Trim();
            // Номер и код прибора — из его собственной маркировки (ARBM_SP_ElementMark, ARBM_ПСБ_БЦО), иначе из окна.
            string ownMark = RevitText.FindParameter(controller, ParamMark)?.AsString()?.Trim();
            string number = system != "souye" && int.TryParse(ownMark, out int ownNumber) ? ownNumber.ToString() : r.Controller.Trim();
            if (system == "souye" && string.IsNullOrWhiteSpace(SouyeLineIdentity.Read(controller, ParamController)))
                Put(ParamController, RevitText.FindParameter(controller, ParamController), number);

            string ownCode = RevitText.FindParameter(controller, ParamBco)?.AsString()?.Trim();
            string code = string.IsNullOrEmpty(ownCode) ? "ARK" : ownCode;
            if (system == "aps" && code.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) < 0)
            {
                warnings.Add($"БЦО прибора «{code}» без «ARK»: журнал АПС такой прибор не примет");
            }

            if (!r.FunctionalZones && !string.IsNullOrEmpty(building))
            {
                Parameter buildingParameter = RevitText.FindParameter(controller, ParamBuilding);
                string ownBuilding = buildingParameter?.AsString()?.Trim();
                if (string.IsNullOrEmpty(ownBuilding))
                {
                    Put(ParamBuilding, buildingParameter, building);
                }
                else if (!string.Equals(ownBuilding, building, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"у прибора корпус «{ownBuilding}», а в окне «{building}»: корпус прибора не изменён");
                }
            }
            string currentFloor = RevitText.FindParameter(controller, floorParam)?.AsString()?.Trim();
            // Как у устройств: цифра из имени уровня, для уровня без номера — этаж из окна (r.Floor).
            string floor = int.TryParse(currentFloor, out int parsed) ? parsed.ToString() : FloorOf(doc, controller, r.Floor);
            if (floor == null)
            {
                failures.Add($"прибор id {id}: не удалось определить номер этажа. Проверьте уровень прибора или заполните «{floorParam}». Для уровня без номера задайте этаж в окне «Этажи уровней без номера…».");
            }
            else
            {
                Put(floorParam, RevitText.FindParameter(controller, floorParam), floor);
            }

            if (!string.IsNullOrWhiteSpace(r.Comments))
            {
                string comments = r.Comments.Trim();
                Put("Комментарии", controller.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), comments);

            }

            Parameter nameParameter = RevitText.FindParameter(controller, ParamProductName);
            string name = nameParameter?.AsString()?.Trim() ?? "";
            string[] parts = name.Split('.');
            switch (system)
            {
                case "aps":
                    bool apsOk = parts.Length == 3 && string.Equals(parts[0], building, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(parts[1], code, StringComparison.OrdinalIgnoreCase) && parts[2] == number;
                    if (!apsOk) Put(ParamProductName, nameParameter, $"{building}.{code}.{number}");
                    break;
                case "security":
                    if (floor != null) Put(ParamProductName, nameParameter, $"ARK.{floor}.{number}");
                    break;
                default:
                    if (name.Length == 0) Put(ParamProductName, nameParameter, $"{code}.{number}");
                    break;
            }

            if (system != "souye")
            {
                string newName = changes.Where(c => (string)c["parameter"] == ParamProductName).Select(c => (string)c["new"]).FirstOrDefault() ?? name;
                int same = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .Count(e => e.Id != controller.Id && string.Equals(RevitText.FindParameter(e, ParamProductName)?.AsString()?.Trim(), newName, StringComparison.OrdinalIgnoreCase));
                if (same > 0)
                {
                    warnings.Add($"имя прибора «{newName}» уже есть у других приборов ({same}); журнал требует уникальные имена");
                }
            }
            return changes;
        }

        /// <summary>Устройства проекта, уже записанные на эту линию и не входящие в текущую маркировку.</summary>
        private static int OtherDevicesOnLine(Document doc, string system, MarkDevicesRequest r, List<FamilyInstance> selected, Func<FamilyInstance, bool> sameSouyeLine)
        {
            var ids = new HashSet<long>(selected.Select(d => RevitIds.IdOf(d.Id)));
            string Value(Element e, string name) => RevitText.FindParameter(e, name)?.AsString()?.Trim();
            string address = AddressFor(r);
            return DeviceLineOperations.AllDevices(doc).Count(e => !ids.Contains(RevitIds.IdOf(e.Id)) &&
                (system == "souye" ? sameSouyeLine(e)
                    : system == "aps"
                        ? Value(e, ParamBuilding) == r.Building?.Trim() && Value(e, ParamController) == r.Controller.Trim() && Value(e, ParamLine) == r.Line.Trim() && HasComment(e, r.Comments)
                        : Value(e, ParamAddress) == address));
        }

        private static List<FamilyInstance> ResolveOrder(Document doc, MarkDevicesRequest request, List<FamilyInstance> devices,
            string order, string floorParameter, List<string> warnings)
        {
            var preview = request.PreviewOrder;
            request.PreviewOrder = null;
            if (order == "manual") return devices;

            var input = devices.Select(device => device.Id).ToList();
            string settings = string.Join("\n", order, request.System, request.ControllerElementId?.ToString(), request.Building,
                request.Controller, request.Line, request.Comments, request.Floor, floorParameter,
                request.FunctionalZones.ToString(), request.RowMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            if (request.ReusePreviewOrder && !request.DryRun && preview.HasValue &&
                preview.Value.document.Equals(doc) && preview.Value.settings == settings && preview.Value.input.SequenceEqual(input))
            {
                var byId = devices.ToDictionary(device => device.Id);
                warnings.AddRange(preview.Value.warnings);
                return preview.Value.ordered.Select(id => byId[id]).ToList();
            }

            var orderWarnings = new List<string>();
            var ordered = order == "route"
                ? OrderByRoute(doc, devices, request, orderWarnings)
                : OrderBySnake(devices, RevitText.ToFeet(request.RowMm));
            warnings.AddRange(orderWarnings);
            if (request.ReusePreviewOrder && request.DryRun)
                request.PreviewOrder = (doc, settings, input, ordered.Select(device => device.Id).ToList(), orderWarnings);
            return ordered;
        }

        private static Func<FamilyInstance, bool> SouyeLineMembership(Document doc, FamilyInstance controller, MarkDevicesRequest request)
        {
            int rack = int.Parse(request.Controller), line = int.Parse(request.Line);
            bool unique = request.FunctionalZones && !SouyeLineIdentity.Racks(doc, request.Comments)
                .Any(other => other.Id != controller.Id && SouyeLineIdentity.RackContains(other, rack));
            return device =>
            {
                if (device.SuperComponent != null || !SouyeLineIdentity.TryLineNumbers(device, out int ownRack, out int ownLine) ||
                    ownRack != rack || ownLine != line || !HasComment(device, request.Comments)) return false;
                string link = SouyeLineIdentity.LinkedRack(device);
                if (!string.IsNullOrEmpty(link)) return link == controller.UniqueId;
                return request.FunctionalZones ? unique :
                    SouyeLineIdentity.SameBuilding(SouyeLineIdentity.Read(device, ParamBuilding), request.Building);
            };
        }

        private static void ValidateSouyeNumbers(Document doc, MarkDevicesRequest request, List<FamilyInstance> devices,
            Func<FamilyInstance, bool> sameLine)
        {
            var selected = new HashSet<ElementId>(devices.Select(device => device.Id));
            long end = (long)request.StartNumber + devices.Count - 1;
            if (request.StartNumber < 1 || end > int.MaxValue)
                throw new ArgumentException("Начальный номер и нумерация устройств СОУЭ должны быть положительными целыми числами.");
            FamilyInstance conflict = DeviceLineOperations.AllDevices(doc)
                .FirstOrDefault(device => !selected.Contains(device.Id) && sameLine(device) &&
                    SouyeLineIdentity.Number(SouyeLineIdentity.Read(device, ParamMark), out int position) &&
                    position >= request.StartNumber && position <= end);
            if (conflict != null)
                throw new InvalidOperationException($"Номер устройства id {RevitIds.IdOf(conflict.Id)} уже занят на этой линии СОУЭ. Выберите все устройства линии или задайте свободный начальный номер.");
        }

        internal static bool HasLinkedSouyeLine(Document doc, FamilyInstance controller, int rack, int line)
        {
            if (controller == null) return false;
            return DeviceLineOperations.AllDevices(doc).Any(device =>
                SouyeLineIdentity.TryLineNumbers(device, out int deviceRack, out int deviceLine) &&
                deviceRack == rack && deviceLine == line && SouyeLineIdentity.LinkedRack(device) == controller.UniqueId);
        }

        private static void ValidateSouyeRack(Document doc, FamilyInstance controller, MarkDevicesRequest request, int rack)
        {
            string existingRack = SouyeLineIdentity.Read(controller, ParamController);
            if (!SouyeLineIdentity.TryDeviceNumbers(existingRack, out var numbers) || (numbers.Count > 0 && !numbers.Contains(rack)))
                throw new InvalidOperationException($"У стойки id {RevitIds.IdOf(controller.Id)} указаны номера приборов «{existingRack}». Выберите один из этих приборов.");
            if (!string.IsNullOrWhiteSpace(CommentOf(controller)) && !HasComment(controller, request.Comments))
                throw new InvalidOperationException($"У выбранной стойки другие «Комментарии» — «{CommentOf(controller)}». Выберите стойку той же группы.");
            if (request.FunctionalZones) return;

            string existingBuilding = SouyeLineIdentity.Read(controller, ParamBuilding);
            if (!string.IsNullOrWhiteSpace(existingBuilding) && !SouyeLineIdentity.SameBuilding(existingBuilding, request.Building))
                throw new InvalidOperationException($"У стойки id {RevitIds.IdOf(controller.Id)} указан корпус «{existingBuilding}». Укажите тот же корпус для линии.");
            if (SouyeLineIdentity.Racks(doc, request.Comments).Any(other => other.Id != controller.Id && SouyeLineIdentity.MatchesRack(other, request.Building, rack)))
                throw new InvalidOperationException($"В корпусе «{request.Building}» прибор с номером {rack} уже указан в другой стойке.");
        }

        /// <summary>
        /// Параметры другой системы, оставшиеся от прошлой маркировки, очищаются: иначе устройство, перемаркированное
        /// из АПС в СОТС, по «ARBM_Номер прибора»/«ARBM_Номер линии» осталось бы и в линии и журнале АПС.
        /// АПС и СОУЭ используют отдельные номера прибора и линии, СОТС использует адрес.
        /// Очищается только заполненный и доступный для записи параметр.
        /// </summary>
        internal static void ClearForeign(Element device, string system, List<string> failures)
        {
            string[] foreign = system == "aps" || system == "souye" ? new[] { ParamAddress }
                : new[] { ParamController, ParamLine, ParamBuilding };
            foreach (string name in foreign)
            {
                Parameter parameter = RevitText.FindParameter(device, name);
                if (parameter == null || parameter.IsReadOnly || string.IsNullOrEmpty(parameter.AsString())) continue;
                string error = RevitText.TrySet(parameter, name, "");
                if (error != null) failures.Add($"id {RevitIds.IdOf(device.Id)}, {name}: {error}");
            }
        }

        private static Dictionary<string, string> ValuesFor(string system, MarkDevicesRequest r, string mark, string bco)
        {
            string address = AddressFor(r);
            switch (system)
            {
                case "aps":
                case "souye":
                    var values = new Dictionary<string, string>
                    {
                        [ParamController] = r.Controller.Trim(), [ParamBco] = bco,
                        [ParamLine] = r.Line.Trim(), [ParamMark] = mark,
                    };
                    if (!r.FunctionalZones || !string.IsNullOrWhiteSpace(r.Building))
                        values[ParamBuilding] = r.Building.Trim();
                    return values;
                default:
                    return new Dictionary<string, string> { [ParamBco] = bco, [ParamAddress] = address, [ParamMark] = mark };
            }
        }

        /// <summary>
        /// Этаж числом из имени уровня («Этаж 03» → 3), в том числе уровня из свойств экземпляра. Для уровня без номера
        /// берётся floorForUnnumbered; без него — null.
        /// </summary>
        internal static string FloorOf(Document doc, FamilyInstance device, string floorForUnnumbered)
        {
            string level = InstanceLevel(doc, device)?.Name;
            Match match = level == null ? Match.Empty : Regex.Match(level, @"-?\d+");
            if (match.Success) return int.Parse(match.Value).ToString();
            // Уровень без номера: этаж, переданный явно (MCP floor), иначе из «Этажи уровней без номера» (окно маркировки)
            if (!string.IsNullOrWhiteSpace(floorForUnnumbered)) return floorForUnnumbered.Trim();
            return level == null ? null : MarkingSettings.LevelFloor(doc, level);
        }

        internal static Level InstanceLevel(Document doc, FamilyInstance device)
        {
            Level LevelFrom(Parameter parameter) => parameter?.StorageType == StorageType.ElementId
                ? doc.GetElement(parameter.AsElementId()) as Level
                : null;

            // У семейств на основе LevelId может быть пустым, хотя уровень задан в свойствах.
            return doc.GetElement(device.LevelId) as Level
                ?? LevelFrom(device.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM))
                ?? LevelFrom(RevitText.FindParameter(device, "Уровень"))
                ?? LevelFrom(device.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM));
        }

        /// <summary>Адрес устройств СОТС.</summary>
        private static string AddressFor(MarkDevicesRequest r) => $"{r.Controller.Trim()}.{r.Line.Trim()}";

        /// <summary>Ряды по оси Y сверху вниз, внутри ряда по X попеременно в разные стороны.</summary>
        private static List<FamilyInstance> OrderBySnake(List<FamilyInstance> devices, double rowStep)
        {
            if (rowStep <= 0) throw new ArgumentException("rowMm должен быть больше нуля.");
            var withPoints = devices.Select(d => (device: d, point: (d.Location as LocationPoint)?.Point)).ToList();
            FamilyInstance missing = withPoints.FirstOrDefault(p => p.point == null).device;
            if (missing != null) throw new InvalidOperationException($"У элемента id {RevitIds.IdOf(missing.Id)} нет точки вставки.");

            double top = withPoints.Max(p => p.point.Y);
            var result = new List<FamilyInstance>();
            bool forward = true;
            foreach (var row in withPoints.GroupBy(p => (int)Math.Floor((top - p.point.Y) / rowStep)).OrderBy(g => g.Key))
            {
                result.AddRange((forward ? row.OrderBy(p => p.point.X) : row.OrderByDescending(p => p.point.X)).Select(p => p.device));
                forward = !forward;
            }
            return result;
        }

        /// <summary>Жадный обход: от прибора к ближайшему по трассам устройству, от него к следующему.</summary>
        private static List<FamilyInstance> OrderByRoute(Document doc, List<FamilyInstance> devices, MarkDevicesRequest request, List<string> warnings)
        {
            if (request.ControllerElementId == null)
            {
                throw new ArgumentException("Для order=route укажите controllerElementId — прибор, от которого идёт линия.");
            }
            var controller = doc.GetElement(RevitIds.ToElementId(request.ControllerElementId.Value)) as FamilyInstance
                ?? throw new ArgumentException($"Прибор id {request.ControllerElementId} не найден.");

            if (string.Equals(request.System?.Trim(), "souye", StringComparison.OrdinalIgnoreCase))
                return OrderSouyeByRoute(doc, controller, devices, request);

            // 1. По ходу кабеля: кабель линии собирается из отрезков и обходится от прибора.
            string comment = request.Comments?.Trim();
            bool commented = !string.IsNullOrEmpty(comment);
            Func<Element, bool> lineCable = e => IsLineRoute(e, request.Controller, request.Line) && (!commented || HasComment(e, comment));
            List<FamilyInstance> lineCables = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(c => c.Location is LocationCurve && !CableWalk.IsTransition(c))
                .ToList();
            List<FamilyInstance> ofLine = lineCables.Where(c => lineCable(c)).ToList();
            bool byGrouping = ofLine.Count > 0;
            if (ofLine.Count == 0 && !string.IsNullOrWhiteSpace(request.Comments))
            {
                ofLine = lineCables.Where(c => string.Equals(c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()?.Trim(),
                    request.Comments.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            }
            // Этаж трасс заполняется вручную. Без него уровень пришлось бы угадывать (по высоте или основе),
            // и порядок по кабелю разошёлся бы с журналом — обход не запускается, порядок по графу журнала.
            // По «Комментариям» берутся трассы всей системы — чужая трасса без этажа не должна отключать обход
            string floorParameter = MarkingSettings.CurrentFloorParameter();
            List<FamilyInstance> noFloor = !byGrouping ? new List<FamilyInstance>() : ofLine.Where(c => !int.TryParse(RevitText.FindParameter(c, floorParameter)?.AsString()?.Trim(), out _)).ToList();
            if (noFloor.Count > 0)
            {
                warnings.Add($"у трасс линии не заполнен «{floorParameter}» цифрой: {noFloor.Count} из {ofLine.Count} " +
                    $"(id {string.Join(", ", noFloor.Take(10).Select(c => RevitIds.IdOf(c.Id)))}{(noFloor.Count > 10 ? ", …" : "")}) — " +
                    "журнал положит их на этаж основы; заполните этаж устройств, которые на них висят. Порядок по ходу кабеля не строился: порядок по минимуму кабеля");
            }
            if (ofLine.Count > 0 && noFloor.Count == 0)
            {
                List<FamilyInstance> walked = CableWalk.Order(doc, ofLine, controller, devices, out List<FamilyInstance> notReached, out string problem, commented ? comment : null);
                if (walked != null && problem == null && walked.Count >= devices.Count * 0.9)
                {
                    if (notReached.Count > 0)
                    {
                        warnings.Add($"по кабелю от прибора не найдены устройства: {notReached.Count} (id {string.Join(", ", notReached.Take(10).Select(d => RevitIds.IdOf(d.Id)))}" +
                            (notReached.Count > 10 ? ", …" : "") + "); они добавлены в конец линии по близости — проверьте стыки кабеля у них");
                        XYZ last = ((walked.LastOrDefault() ?? controller).Location as LocationPoint)?.Point ?? XYZ.Zero;
                        var rest = notReached.ToList();
                        while (rest.Count > 0)
                        {
                            FamilyInstance nearest = rest.OrderBy(d => ((LocationPoint)d.Location).Point.DistanceTo(last)).First();
                            walked.Add(nearest);
                            rest.Remove(nearest);
                            last = ((LocationPoint)nearest.Location).Point;
                        }
                    }
                    return walked;
                }
                warnings.Add(problem != null || walked == null
                    ? $"по ходу кабеля пронумеровать нельзя ({problem}): порядок по минимуму кабеля"
                    : $"кабель от прибора собирается не целиком (найдено устройств {walked.Count} из {devices.Count}) — разрывы в стыках трасс; порядок по минимуму кабеля");
            }

            // 2. Запасной вариант: минимум кабеля по графу трасс журнала.
            var messages = new List<string>();
            var ordered = new List<FamilyInstance>();
            using (JournalDialog.Capture(messages))
            {
                var calculator = new RouteLengthCalculator();
                var group = new ControllerWithLoops
                {
                    Controller = controller,
                    ControllerId = new ControllerId { ControllerPrefix = "ARK", FloorNumber = 1, ControllerNumber = 1 },
                    Loops = new List<LoopGroup> { new LoopGroup { LoopNumber = 1, Devices = devices.ToList() } },
                };
                Func<Element, bool> byLine = e => IsLineRoute(e, request.Controller, request.Line) && (!commented || HasComment(e, comment));
                int lineRoutes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                    .WhereElementIsNotElementType().Count(byLine);
                string source;
                if (lineRoutes > 0)
                {
                    source = $"«{ParamGrouping}» = …АЛС.{request.Controller.Trim()}.{request.Line.Trim()}";
                    calculator.BuildGraphsByLevel(doc, new List<ControllerWithLoops> { group }, comment, byLine);
                }
                else if (!string.IsNullOrWhiteSpace(request.Comments))
                {
                    source = $"«Комментарии» = «{request.Comments.Trim()}»";
                    warnings.Add($"нет трасс с «{ParamGrouping}» …АЛС.{request.Controller.Trim()}.{request.Line.Trim()}: маршрут по трассам с {source}");
                    calculator.BuildGraphsByLevel(doc, new List<ControllerWithLoops> { group }, request.Comments.Trim());
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Нет трасс линии: у семейств кабеля не найдено «{ParamGrouping}» вида СПС_АЛС.{request.Controller.Trim()}.{request.Line.Trim()} " +
                        "(прибор.линия). Заполните его у трасс линии или укажите «Комментарии» трасс.");
                }

                if (calculator.BuildError != null) throw new InvalidOperationException(calculator.BuildError);
                ordered = TraceRoute(calculator, controller, devices, warnings);
                if (ordered.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"От прибора не найдено ни одного устройства по трассам с {source}: проверьте, что трасса доходит до прибора, или используйте ручной порядок. " +
                        string.Join(" | ", messages.Distinct().Take(5)));
                }
            }
            warnings.AddRange(messages);
            return ordered;
        }

        private static List<FamilyInstance> OrderSouyeByRoute(Document doc, FamilyInstance controller,
            List<FamilyInstance> devices, MarkDevicesRequest request)
        {
            var matches = SouyeLineIdentity.MarkingRouteFilter(doc, request.Building, int.Parse(request.Controller), int.Parse(request.Line), request.Comments, request.FunctionalZones);
            var routes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(cable => cable.Location is LocationCurve && matches(cable)).ToList();
            if (routes.Count == 0)
                throw new SouyeRouteLayoutException($"Не найдены трассы СОУЭ для корпуса «{request.Building}», стойки {request.Controller}, линии {request.Line}. Проверьте «{ParamGrouping}», корпус и «Комментарии».");

            string floor = MarkingSettings.CurrentFloorParameter();
            var missing = routes.Where(cable => !int.TryParse(SouyeLineIdentity.Read(cable, floor), out _))
                .Select(cable => RevitIds.IdOf(cable.Id)).ToList();
            if (missing.Count > 0)
                throw new SouyeRouteLayoutException($"У {missing.Count} трасс линии не указан числовой «{floor}». ID: {string.Join(", ", missing.Take(10))}.");

            var order = CableWalk.Order(doc, routes, controller, devices, out var missed, out string problem,
                request.Comments);
            if (problem != null)
                throw new SouyeRouteLayoutException($"СОУЭ, стойка {request.Controller}, линия {request.Line}: {problem}. Проверьте трассу от стойки.");
            if (missed.Count > 0)
                throw new SouyeRouteLayoutException($"Не удалось обойти {missed.Count} из {devices.Count} устройств линии СОУЭ. ID: " +
                    string.Join(", ", missed.Take(12).Select(device => RevitIds.IdOf(device.Id))) +
                    ". Проверьте состав выделения и стыки трасс либо укажите ручной порядок.");
            return order;
        }

        internal static string ReadGrouping(Element element)
        {
            Parameter parameter = RevitText.FindParameter(element, "ADSK_Группирование");
            return parameter?.StorageType == StorageType.String ? parameter.AsString()?.Trim() : null;
        }

        /// <summary>Трасса линии: группирование вида «СПС_АЛС.прибор.линия» — две последние части после точки.</summary>
        internal static bool IsLineRoute(Element element, string controller, string line)
        {
            if (CableWalk.IsTransition(element)) return false;
            string value = ReadGrouping(element);
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] parts = value.Split('.');
            if (parts.Length < 3) return false;
            // Числом, как JournalLevelCheck.LineOf и LineAssignmentOperations.LineOf: «СПС_АЛС.01.2» — прибор 1, линия 2.
            bool Same(string a, string b) =>
                int.TryParse(a.Trim(), out int x) && int.TryParse(b.Trim(), out int y) ? x == y : a.Trim() == b.Trim();
            return Same(parts[parts.Length - 2], controller) && Same(parts[parts.Length - 1], line);
        }

        /// <summary>
        /// Порядок с минимальной общей длиной кабеля: «прибор → 1 → 2 → … → последнее» по трассам линии.
        /// Расстояния — кратчайшие пути по графу трасс журнала (тот же граф, по которому журнал считает длины),
        /// этажи соединяются через «ARBM_Межэтажный переход». Начальная цепочка «к ближайшему» улучшается
        /// разворотом участков (2-opt) и переносом коротких участков (or-opt), пока цепочка становится короче.
        /// Разрывы вида «13 → 14 в обход 30» удлиняют цепочку и поэтому убираются; на петле номера идут по кругу.
        /// Устройства, до которых от прибора нет пути по трассам, добавляются в конец по близости с предупреждением.
        /// </summary>
        private static List<FamilyInstance> TraceRoute(RouteLengthCalculator calculator, FamilyInstance controller,
            List<FamilyInstance> devices, List<string> warnings)
        {
            var neighbours = new Dictionary<RouteLengthCalculator.Graph.Node, List<KeyValuePair<RouteLengthCalculator.Graph.Node, double>>>();
            void Link(RouteLengthCalculator.Graph.Node a, RouteLengthCalculator.Graph.Node b, double length)
            {
                if (a == b) return;
                if (!neighbours.TryGetValue(a, out var la)) neighbours[a] = la = new List<KeyValuePair<RouteLengthCalculator.Graph.Node, double>>();
                if (!neighbours.TryGetValue(b, out var lb)) neighbours[b] = lb = new List<KeyValuePair<RouteLengthCalculator.Graph.Node, double>>();
                la.Add(new KeyValuePair<RouteLengthCalculator.Graph.Node, double>(b, length));
                lb.Add(new KeyValuePair<RouteLengthCalculator.Graph.Node, double>(a, length));
            }
            foreach (RouteLengthCalculator.Graph graph in calculator._graphsByLevel.Values)
            {
                foreach (RouteLengthCalculator.Graph.Edge edge in graph.Edges) Link(edge.A, edge.B, edge.Length);
            }


            if (!calculator._deviceNodes.TryGetValue(controller.Id.ToString(), out RouteLengthCalculator.Graph.Node start))
            {
                throw new InvalidOperationException("Прибор не удалось привязать к трассе линии.");
            }

            // Точки: 0 — прибор, далее устройства, привязанные к трассе.
            var points = new List<RouteLengthCalculator.Graph.Node> { start };
            var pointDevices = new List<FamilyInstance> { null };
            var unattached = new List<FamilyInstance>();
            foreach (FamilyInstance device in devices)
            {
                if (calculator._deviceNodes.TryGetValue(device.Id.ToString(), out var node))
                {
                    points.Add(node);
                    pointDevices.Add(device);
                }
                else
                {
                    unattached.Add(device);
                }
            }

            // Матрица кратчайших расстояний по трассам (Дейкстра из каждой точки).
            int count = points.Count;
            var dist = new double[count, count];
            for (int i = 0; i < count; i++)
            {
                Dictionary<RouteLengthCalculator.Graph.Node, double> d = ShortestFrom(points[i], neighbours);
                for (int j = 0; j < count; j++) dist[i, j] = d.TryGetValue(points[j], out double v) ? v : double.PositiveInfinity;
            }
            // На одной линии кабеля бывают два совпадающих кабеля («туда» и «обратно»): соседние на линии устройства
            // висят на разных кабелях и по графу далеко (35 рядом со 148). Для них берётся «по плану × 1,5».
            // Сокращение — только если по всему отрезку между устройствами идёт кабель линии параллельно ему
            // (точки отрезка через каждые 0,5 м лежат не дальше 300 мм от параллельного участка кабеля).
            // Через стену между соседними кабелями (84 ↔ 85) кабеля посередине нет — сокращения нет.
            const double planFactor = 1.5, nearLine = 300 / 304.8, step = 500 / 304.8, bucket = 1000 / 304.8;
            double sinTolerance = Math.Sin(5 * Math.PI / 180);
            var attachedNodes = new HashSet<RouteLengthCalculator.Graph.Node>(calculator._deviceNodes.Values);
            var cableEdges = calculator._graphsByLevel.Values.SelectMany(g => g.Edges)
                .Where(e => !attachedNodes.Contains(e.A) && !attachedNodes.Contains(e.B)) // без привязок устройств
                .Where(e => e.Length >= 1e-3 && Math.Abs(e.A.Point.Z - e.B.Point.Z) < 1e-3)
                .Select(e => (a: e.A.Point, b: e.B.Point)).ToList();
            var grid = new Dictionary<(long, long, long), List<int>>();
            (long, long, long) Key(double x, double y, double z) => ((long)Math.Floor(x / bucket), (long)Math.Floor(y / bucket), (long)Math.Round(z * 100));
            for (int k = 0; k < cableEdges.Count; k++)
            {
                var (p, q) = cableEdges[k];
                double length = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                int parts = Math.Max(1, (int)Math.Ceiling(length / (bucket / 2)));
                var seen = new HashSet<(long, long, long)>();
                for (int s2 = 0; s2 <= parts; s2++)
                {
                    double f = (double)s2 / parts;
                    var key = Key(p.X + (q.X - p.X) * f, p.Y + (q.Y - p.Y) * f, p.Z);
                    if (!seen.Add(key)) continue;
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(k);
                }
            }
            bool CoveredAt(double x, double y, double z, double ux, double uy)
            {
                var (cx, cy, cz) = Key(x, y, z);
                for (long gx = cx - 1; gx <= cx + 1; gx++)
                    for (long gy = cy - 1; gy <= cy + 1; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy, cz), out var list)) continue;
                        foreach (int k in list)
                        {
                            var (p, q) = cableEdges[k];
                            double ex = q.X - p.X, ey = q.Y - p.Y, el = Math.Sqrt(ex * ex + ey * ey);
                            if (Math.Abs(ux * ey / el - uy * ex / el) > sinTolerance) continue;      // не параллельно
                            double t = Math.Max(0, Math.Min(1, ((x - p.X) * ex + (y - p.Y) * ey) / (el * el)));
                            double dx = x - (p.X + ex * t), dy = y - (p.Y + ey * t);
                            if (dx * dx + dy * dy <= nearLine * nearLine) return true;
                        }
                    }
                return false;
            }
            bool AlongOneCable(XYZ a, XYZ b)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) return true;
                int samples = Math.Max(2, (int)Math.Ceiling(len / step));
                for (int s2 = 1; s2 < samples; s2++) // концы — сами устройства, они могут стоять сбоку от кабеля
                {
                    double f = (double)s2 / samples;
                    if (!CoveredAt(a.X + dx * f, a.Y + dy * f, a.Z, dx / len, dy / len)) return false;
                }
                return true;
            }
            for (int i = 1; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    XYZ a = points[i].Point, b = points[j].Point;
                    if (Math.Abs(a.Z - b.Z) > 1e-3) continue;
                    double plan = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) * planFactor;
                    if (plan >= dist[i, j] || !AlongOneCable(a, b)) continue;
                    dist[i, j] = plan;
                    dist[j, i] = plan;
                }
            }

            var reachable = Enumerable.Range(1, count - 1).Where(i => !double.IsPositiveInfinity(dist[0, i])).ToList();
            List<int> route = OptimizeOpenPath(dist, reachable);

            // Сравнение с текущей нумерацией линии (ARBM_SP_ElementMark) по той же длине кабеля:
            // перемаркировка не должна удлинять кабель. Если текущий порядок короче — он и остаётся.
            double Cost(List<int> r)
            {
                double sum = 0;
                for (int k = 0; k < r.Count; k++) sum += dist[k == 0 ? 0 : r[k - 1], r[k]];
                return sum;
            }
            var marks = reachable.Select(i => int.TryParse(RevitText.FindParameter(pointDevices[i], ParamMark)?.AsString()?.Trim(), out int m) ? m : (int?)null).ToList();
            if (reachable.Count > 1 && marks.All(m => m.HasValue) && marks.Distinct().Count() == marks.Count)
            {
                List<int> current = reachable.Zip(marks, (i, m) => (i, m: m.Value)).OrderBy(x => x.m).Select(x => x.i).ToList();
                double newCost = Cost(route), currentCost = Cost(current);
                if (!double.IsInfinity(currentCost) && currentCost < newCost - 1e-6)
                {
                    warnings.Add($"длина по кабелю: текущая нумерация {currentCost * 0.3048:F0} м короче расчётной {newCost * 0.3048:F0} м — оставлен текущий порядок");
                    route = current;
                }
                else
                {
                    warnings.Add($"длина по кабелю: новая {newCost * 0.3048:F0} м, текущая {currentCost * 0.3048:F0} м");
                }
            }
            var ordered = route.Select(i => pointDevices[i]).ToList();
            if (ordered.Count == 0) return ordered; // от прибора по трассам не дошли ни до одного устройства

            List<FamilyInstance> missed = unattached.Concat(Enumerable.Range(1, count - 1).Where(i => !reachable.Contains(i)).Select(i => pointDevices[i])).ToList();
            if (missed.Count > 0)
            {
                warnings.Add($"трасса от прибора не доходит до устройств: {missed.Count} (id {string.Join(", ", missed.Take(10).Select(d => RevitIds.IdOf(d.Id)))}" +
                    (missed.Count > 10 ? ", …" : "") + "); они добавлены в конец линии по близости — проверьте трассы");
                XYZ last = ((ordered.LastOrDefault() ?? controller).Location as LocationPoint)?.Point ?? XYZ.Zero;
                while (missed.Count > 0)
                {
                    FamilyInstance nearest = missed.OrderBy(d => ((LocationPoint)d.Location).Point.DistanceTo(last)).First();
                    ordered.Add(nearest);
                    missed.Remove(nearest);
                    last = ((LocationPoint)nearest.Location).Point;
                }
            }
            return ordered;
        }

        /// <summary>Дейкстра по графу трасс: расстояния от узла до всех достижимых узлов.</summary>
        private static Dictionary<RouteLengthCalculator.Graph.Node, double> ShortestFrom(RouteLengthCalculator.Graph.Node source,
            Dictionary<RouteLengthCalculator.Graph.Node, List<KeyValuePair<RouteLengthCalculator.Graph.Node, double>>> neighbours)
        {
            var dist = new Dictionary<RouteLengthCalculator.Graph.Node, double> { [source] = 0 };
            var ids = new Dictionary<RouteLengthCalculator.Graph.Node, int>();
            int Id(RouteLengthCalculator.Graph.Node node) => ids.TryGetValue(node, out int v) ? v : ids[node] = ids.Count;
            var queue = new SortedSet<(double d, int id, RouteLengthCalculator.Graph.Node node)>(
                Comparer<(double d, int id, RouteLengthCalculator.Graph.Node node)>.Create((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.id.CompareTo(y.id)));
            queue.Add((0, Id(source), source));
            while (queue.Count > 0)
            {
                var current = queue.Min;
                queue.Remove(current);
                if (current.d > dist[current.node]) continue;
                if (!neighbours.TryGetValue(current.node, out var list)) continue;
                foreach (KeyValuePair<RouteLengthCalculator.Graph.Node, double> next in list)
                {
                    double candidate = current.d + next.Value;
                    if (dist.TryGetValue(next.Key, out double known) && known <= candidate) continue;
                    if (dist.TryGetValue(next.Key, out double old)) queue.Remove((old, Id(next.Key), next.Key));
                    dist[next.Key] = candidate;
                    queue.Add((candidate, Id(next.Key), next.Key));
                }
            }
            return dist;
        }

        /// <summary>
        /// Открытая цепочка от точки 0 через все points с минимальной суммой dist: «к ближайшему», затем 2-opt
        /// (разворот участка) и or-opt (перенос участка из 1–3 точек), пока есть улучшение (не дольше 5 с).
        /// </summary>
        private static List<int> OptimizeOpenPath(double[,] dist, List<int> points)
        {
            var route = new List<int>();
            var left = new HashSet<int>(points);
            int current = 0;
            while (left.Count > 0)
            {
                int next = left.OrderBy(p => dist[current, p]).First();
                route.Add(next);
                left.Remove(next);
                current = next;
            }

            double D(int a, int b) => dist[a, b];
            int Prev(int i) => i == 0 ? 0 : route[i - 1];
            var timer = System.Diagnostics.Stopwatch.StartNew();
            const double eps = 1e-6;
            bool improved = true;
            while (improved && timer.ElapsedMilliseconds < 5000)
            {
                improved = false;
                int m = route.Count;

                // 2-opt: развернуть route[i..j]
                for (int i = 0; i < m - 1 && !improved; i++)
                {
                    for (int j = i + 1; j < m; j++)
                    {
                        int a = Prev(i), b = route[i], c = route[j];
                        double before = D(a, b) + (j + 1 < m ? D(c, route[j + 1]) : 0);
                        double after = D(a, c) + (j + 1 < m ? D(b, route[j + 1]) : 0);
                        if (after < before - eps)
                        {
                            route.Reverse(i, j - i + 1);
                            improved = true;
                            break;
                        }
                    }
                }
                if (improved) continue;

                // or-opt: перенести участок route[i..i+len-1] в другое место (можно развёрнутым)
                for (int len = 1; len <= 3 && !improved; len++)
                {
                    for (int i = 0; i + len <= m && !improved; i++)
                    {
                        int first = route[i], last = route[i + len - 1];
                        int before = Prev(i);
                        bool hasAfter = i + len < m;
                        double removeGain = D(before, first) + (hasAfter ? D(last, route[i + len]) : 0) - (hasAfter ? D(before, route[i + len]) : 0);
                        var rest = new List<int>(route);
                        List<int> segment = rest.GetRange(i, len);
                        rest.RemoveRange(i, len);
                        for (int k = 0; k <= rest.Count && !improved; k++)
                        {
                            if (k == i) continue;
                            int p = k == 0 ? 0 : rest[k - 1];
                            bool hasQ = k < rest.Count;
                            int q = hasQ ? rest[k] : -1;
                            double baseEdge = hasQ ? D(p, q) : 0;
                            double forward = D(p, first) + (hasQ ? D(last, q) : 0) - baseEdge;
                            double backward = D(p, last) + (hasQ ? D(first, q) : 0) - baseEdge;
                            bool reverse = backward < forward;
                            if (Math.Min(forward, backward) < removeGain - eps)
                            {
                                if (reverse) segment.Reverse();
                                rest.InsertRange(k, segment);
                                route = rest;
                                improved = true;
                            }
                        }
                    }
                }
            }
            return route;
        }

        private static List<FamilyInstance> ResolveDevices(Document doc, List<long> ids)
        {
            if (ids == null || ids.Count == 0) throw new ArgumentException("Укажите ids — устройства шлейфа.");
            if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("В ids есть повторы.");
            return ids.Select(id => doc.GetElement(RevitIds.ToElementId(id)) as FamilyInstance
                ?? throw new ArgumentException($"Экземпляр семейства id {id} не найден в проекте.")).ToList();
        }

        private static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Укажите " + name + ".");
        }

        private static void RequireNumber(string value, string name)
        {
            if (!int.TryParse(value.Trim(), out _)) throw new ArgumentException($"{name} должен быть числом.");
        }
    }
}
