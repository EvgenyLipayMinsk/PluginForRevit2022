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
        public string Controller { get; set; }
        public string Bco { get; set; }
        public string Line { get; set; }
        public string Comments { get; set; }
        /// <summary>Этаж числом; пусто — цифры из имени уровня элемента.</summary>
        public string Floor { get; set; }
        /// <summary>СОУЭ: eom или adsk (какой параметр этажа заполнять).</summary>
        public string ParameterStandard { get; set; }
        public int StartNumber { get; set; } = 1;
        /// <summary>Шаг рядов для порядка snake, мм.</summary>
        public double RowMm { get; set; } = 3000;
        /// <summary>manual — порядок списка; route — по трассам журнала от прибора; snake — змейкой по плану.</summary>
        public string Order { get; set; } = "manual";
        /// <summary>Прибор (id), от которого считается трасса при Order=route.</summary>
        public long? ControllerElementId { get; set; }
        public bool DryRun { get; set; } = true;
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
        internal const int LineCapacity = 250;

        public static Dictionary<string, object> Mark(Document doc, MarkDevicesRequest request)
        {
            string system = (request.System ?? "").Trim().ToLowerInvariant();
            if (system != "aps" && system != "security" && system != "souye")
            {
                throw new ArgumentException("system: aps, security или souye.");
            }
            Require(request.Controller, "controller (номер прибора)");
            Require(request.Line, "line (номер линии)");
            if (system != "security") Require(request.Building, "building (ARBM_Корпус)");
            RequireNumber(request.Controller, "controller");
            RequireNumber(request.Line, "line");

            List<FamilyInstance> devices = ResolveDevices(doc, request.Ids);
            var warnings = new List<string>();
            MarkingSettings settings = MarkingSettings.Load();
            List<FamilyInstance> excluded = devices.Where(d => !settings.IsInLine(d.Symbol.FamilyName, system)).ToList();
            if (excluded.Count > 0)
            {
                warnings.Add($"нет галочки системы в настройках маркировки, исключены: {excluded.Count} ({string.Join(", ", excluded.Select(d => d.Symbol.FamilyName).Distinct())})");
                devices = devices.Except(excluded).ToList();
                if (devices.Count == 0) throw new InvalidOperationException("Ни у одного выбранного семейства нет галочки этой системы в настройках маркировки.");
            }
            string order = (request.Order ?? "manual").Trim().ToLowerInvariant();
            if (order == "route")
            {
                Require(request.Comments, "comments (по нему журнал отбирает трассы)");
                devices = OrderByRoute(doc, devices, request, warnings);
            }
            else if (order == "snake")
            {
                devices = OrderBySnake(devices, RevitText.ToFeet(request.RowMm));
            }
            else if (order != "manual")
            {
                throw new ArgumentException("order: manual, route или snake.");
            }

            string floorParam = system == "souye" && string.Equals(request.ParameterStandard, "eom", StringComparison.OrdinalIgnoreCase)
                ? "О_Этаж" : "ADSK_Этаж";
            var changes = new List<Dictionary<string, object>>();
            var failures = new List<string>();
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
                        failures.Add($"id {RevitIds.IdOf(device.Id)}: не удалось определить этаж числом, укажите floor");
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

            int others = OtherDevicesOnLine(doc, system, request, devices);
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
            return new Dictionary<string, object>
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
            };
        }

        /// <summary>Устройства проекта, уже записанные на эту линию и не входящие в текущую маркировку.</summary>
        private static int OtherDevicesOnLine(Document doc, string system, MarkDevicesRequest r, List<FamilyInstance> selected)
        {
            var ids = new HashSet<long>(selected.Select(d => RevitIds.IdOf(d.Id)));
            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_NurseCallDevices };
            string Value(Element e, string name) => e.LookupParameter(name)?.AsString()?.Trim();
            string address = $"{r.Controller.Trim()}.{r.Line.Trim()}";

            return new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Count(e => !ids.Contains(RevitIds.IdOf(e.Id)) && (system == "aps"
                    ? Value(e, ParamBuilding) == r.Building?.Trim() && Value(e, ParamController) == r.Controller.Trim() && Value(e, ParamLine) == r.Line.Trim()
                    : Value(e, ParamAddress) == address));
        }

        private static Dictionary<string, string> ValuesFor(string system, MarkDevicesRequest r, string mark, string bco)
        {
            string address = $"{r.Controller.Trim()}.{r.Line.Trim()}";
            switch (system)
            {
                case "aps":
                    return new Dictionary<string, string>
                    {
                        [ParamBuilding] = r.Building.Trim(), [ParamController] = r.Controller.Trim(), [ParamBco] = bco,
                        [ParamLine] = r.Line.Trim(), [ParamMark] = mark,
                    };
                case "souye":
                    return new Dictionary<string, string>
                    {
                        [ParamBuilding] = r.Building.Trim(), [ParamBco] = bco, [ParamAddress] = address, [ParamMark] = mark,
                    };
                default:
                    return new Dictionary<string, string> { [ParamBco] = bco, [ParamAddress] = address, [ParamMark] = mark };
            }
        }

        internal static string FloorOf(Document doc, FamilyInstance device, string explicitFloor)
        {
            if (!string.IsNullOrWhiteSpace(explicitFloor)) return explicitFloor.Trim();
            string level = (doc.GetElement(device.LevelId) as Level)?.Name;
            if (level == null) return null;
            Match match = Regex.Match(level, @"-?\d+");
            return match.Success ? int.Parse(match.Value).ToString() : null;
        }

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
                calculator.BuildGraphsByLevel(doc, new List<ControllerWithLoops> { group }, request.Comments.Trim());

                var remaining = devices.ToList();
                Element current = controller;
                while (remaining.Count > 0)
                {
                    FamilyInstance best = null;
                    double bestLength = double.MaxValue;
                    foreach (FamilyInstance candidate in remaining)
                    {
                        double length = calculator.ComputeShortestRouteBetweenDevices(current, candidate, "");
                        if (length > 0 && length < bestLength)
                        {
                            bestLength = length;
                            best = candidate;
                        }
                    }
                    if (best == null)
                    {
                        throw new InvalidOperationException(
                            $"Нет трассы до устройств: {string.Join(", ", remaining.Select(d => RevitIds.IdOf(d.Id)))}. " +
                            "Проверьте короба с тем же значением «Комментарии» или используйте ручной порядок. " +
                            string.Join(" | ", messages.Distinct().Take(5)));
                    }
                    ordered.Add(best);
                    remaining.Remove(best);
                    current = best;
                }
            }
            warnings.AddRange(messages);
            return ordered;
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
