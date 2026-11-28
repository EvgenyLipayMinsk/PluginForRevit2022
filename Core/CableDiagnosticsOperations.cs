using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PluginForRevit2022.Mcp;

namespace PluginForRevit2022.Core
{
    internal static class CableDiagnosticsOperations
    {
        internal static string Text(Element e, string name) => RevitText.FindParameter(e, name)?.AsString()?.Trim() ?? "";
        private static List<FamilyInstance> Devices(Document doc, McpArgs args)
        {
            var settings = MarkingSettings.Load();
            return DeviceLineOperations.AllDevices(doc)
                .Where(e => settings.IsInLine(e.Symbol.FamilyName, "aps") || settings.IsInLine(e.Symbol.FamilyName, "security") || settings.IsInLine(e.Symbol.FamilyName, "souye"))
                .Where(e => Text(e, DeviceMarkingOperations.ParamBco).IndexOf("ARK", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(e => args.String("comments") == null || DeviceMarkingOperations.HasComment(e, args.String("comments")))
                .Where(e => args.String("line") == null || DeviceLineOperations.KeyOf(e, true) == args.String("line")).ToList();
        }

        public static Dictionary<string, object> Lines(Document doc, McpArgs args)
        {
            var devices = Devices(doc, args);
            var rows = new List<Dictionary<string, object>>();
            var connectionOrders = LineConnectionOrder.Read(doc);
            var allInstances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
            foreach (var group in devices.Where(d => DeviceLineOperations.KeyOf(d, true) != null).GroupBy(d => DeviceLineOperations.KeyOf(d, true)))
            {
                var list = group.ToList(); var issues = new List<object>();
                if (group.Key.StartsWith("aps|", StringComparison.Ordinal))
                {
                    var controllers = Controllers(allInstances, group.Key.Split('|'), list[0]);
                    if (controllers.Count != 1) issues.Add(new { kind = "controller_not_unique", ids = controllers.Select(e => RevitIds.IdOf(e.Id)).ToList() });
                }
                var numbered = list.Select(d => new { device = d, value = Text(d, DeviceMarkingOperations.ParamMark) })
                    .Select(x => new { x.device, number = int.TryParse(x.value, out int n) ? n : 0 }).ToList();
                foreach (var d in numbered.Where(x => x.number <= 0)) issues.Add(new { kind = "invalid_number", ids = new[] { RevitIds.IdOf(d.device.Id) } });
                foreach (var repeated in numbered.Where(x => x.number > 0).GroupBy(x => x.number).Where(g => g.Count() > 1))
                    issues.Add(new { kind = "duplicate_number", number = repeated.Key, ids = repeated.Select(x => RevitIds.IdOf(x.device.Id)).ToList() });
                var numbers = numbered.Where(x => x.number > 0).Select(x => x.number).Distinct().OrderBy(x => x).ToList();
                long missing = numbers.Count == 0 ? 0 : (long)numbers.Last() - numbers.Count;
                if (missing > 0 && !connectionOrders.ContainsKey(group.Key)) issues.Add(new { kind = "numbering_gaps", count = missing });
                if (list.Count > DeviceMarkingOperations.LineCapacity) issues.Add(new { kind = "capacity", count = list.Count, capacity = DeviceMarkingOperations.LineCapacity });
                var noComments = list.Where(d => string.IsNullOrWhiteSpace(DeviceMarkingOperations.CommentOf(d))).Select(d => RevitIds.IdOf(d.Id)).ToList();
                if (noComments.Count > 0) issues.Add(new { kind = "empty_comments", ids = noComments });
                rows.Add(new Dictionary<string, object> { ["line"] = group.Key, ["devices"] = list.Count, ["ok"] = issues.Count == 0,
                    ["ids"] = list.Select(d => RevitIds.IdOf(d.Id)).ToList(), ["issues"] = issues });
            }
            var unassigned = devices.Where(d => DeviceLineOperations.KeyOf(d, true) == null).Select(d => RevitIds.IdOf(d.Id)).ToList();
            var levels = JournalLevelCheck.Run(doc, args.Double("attachMm") ?? 600);
            return new Dictionary<string, object>
            {
                ["ok"] = rows.All(r => (bool)r["ok"]) && unassigned.Count == 0 && (int)levels["problems"] == 0, ["lines"] = rows,
                ["unassignedDeviceIds"] = unassigned.Take(300).ToList(), ["unassignedCount"] = unassigned.Count,
                ["journalLevels"] = levels,
                ["summary"] = "Нумерация и принадлежность проверены по определениям «Диспетчера линий». Проверка уровней отдельно охватывает АПС всего проекта. Неразмеченные устройства не исправляются автоматически.",
            };
        }

        private static List<FamilyInstance> Controllers(IEnumerable<FamilyInstance> all, string[] key, FamilyInstance device) =>
            all.Where(c => c.Location is LocationPoint &&
                Text(c, DeviceMarkingOperations.ParamBco).IndexOf("ARK", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Text(c, DeviceMarkingOperations.ParamMark) == key[2] && Text(c, DeviceMarkingOperations.ParamBuilding) == key[1] &&
                DeviceMarkingOperations.HasComment(c, DeviceMarkingOperations.CommentOf(device))).ToList();

        public static Dictionary<string, object> Routes(Document doc, McpArgs args)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
            var cables = all.Where(c => c.Location is LocationCurve && c.Symbol.FamilyName.StartsWith("ARBM_Кабель", StringComparison.OrdinalIgnoreCase)).ToList();
            var groups = Devices(doc, args).Where(d => DeviceLineOperations.KeyOf(d, true) != null).GroupBy(d => DeviceLineOperations.KeyOf(d, true));
            var rows = new List<Dictionary<string, object>>();
            foreach (var group in groups)
            {
                string[] key = group.Key.Split('|');
                var row = new Dictionary<string, object> { ["line"] = group.Key, ["ok"] = false };
                rows.Add(row);
                                var devices = group.ToList(); var routes = new List<FamilyInstance>();
                Func<Element, bool> routeFilter = null;
                FamilyInstance controller = args.Long("controllerElementId") == null ? null :
                    doc.GetElement(RevitIds.ToElementId(args.Long("controllerElementId").Value)) as FamilyInstance;
                if (args.Long("controllerElementId") != null && args.String("line") == null)
                    throw new ArgumentException("controllerElementId задаётся вместе с конкретной line.");
                if (key[0] == "aps")
                {
                    routes = cables.Where(c => DeviceMarkingOperations.IsLineRoute(c, key[2], key[3]) &&
                        DeviceMarkingOperations.HasComment(c, devices[0].get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "") &&
                        (Text(c, DeviceMarkingOperations.ParamBuilding).Length == 0 || Text(c, DeviceMarkingOperations.ParamBuilding) == key[1])).ToList();
                    if (controller == null)
                    {
                        var candidates = Controllers(all, key, devices[0]);
                        row["controllerCandidates"] = candidates.Select(c => RevitIds.IdOf(c.Id)).ToList();
                        if (candidates.Count == 1) controller = candidates[0];
                    }
                }
                else if (key[0] == "souye")
                {
                    int rack = int.Parse(key[2]);
                    int line = int.Parse(key[3]);
                    string comment = DeviceMarkingOperations.CommentOf(devices[0]);
                    bool functionalZones = key[1].StartsWith("@", StringComparison.Ordinal);
                    bool Matches(FamilyInstance item) => SouyeLineIdentity.IsRack(item) && (functionalZones
                        ? item.UniqueId == key[1].Substring(1) && SouyeLineIdentity.RackContains(item, rack)
                        : SouyeLineIdentity.MatchesRack(item, key[1], rack));
                    if (controller == null)
                    {
                        var candidates = SouyeLineIdentity.Racks(doc, comment).Where(Matches).ToList();
                        row["controllerCandidates"] = candidates.Select(r => RevitIds.IdOf(r.Id)).ToList();
                        if (candidates.Count == 1) controller = candidates[0];
                    }
                    else if (!Matches(controller))
                    {
                        row["status"] = "controller_mismatch";
                        row["message"] = "Указанная стойка СОУЭ не соответствует маркировке выбранной линии.";
                        continue;
                    }
                    string routeBuilding = key[1];
                    if (functionalZones)
                    {
                        var zones = devices.Select(device => SouyeLineIdentity.Read(device, DeviceMarkingOperations.ParamBuilding))
                            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                        routeBuilding = zones.Count == 1 ? zones[0] : null;
                    }
                    try
                    {
                        routeFilter = SouyeLineIdentity.MarkingRouteFilter(doc, routeBuilding, rack, line, comment, functionalZones);
                    }
                    catch (SouyeRouteLayoutException ex)
                    {
                        row["status"] = "route_layout_error";
                        row["message"] = ex.Message;
                        continue;
                    }
                    routes = cables.Where(cable => routeFilter(cable)).ToList();
                }
                else
                {
                    // Для адресных СОТС и старой СОУЭ прибор выбирает проектировщик; не угадываем его по близости.
                    routes = cables.Where(c => DeviceMarkingOperations.HasComment(c, DeviceMarkingOperations.CommentOf(devices[0]))).ToList();
                    if (args.String("routeGrouping") != null) routes = routes.Where(c => DeviceMarkingOperations.ReadGrouping(c) == args.String("routeGrouping")).ToList();
                    else { row["status"] = "route_grouping_required"; row["message"] = "Для адресной линии укажите точное routeGrouping и controllerElementId."; continue; }
                }
                row["routeCount"] = routes.Count;
                if (controller == null) { row["status"] = "controller_required"; row["message"] = "Прибор не определён однозначно. Укажите controllerElementId и line."; continue; }
                row["controllerId"] = RevitIds.IdOf(controller.Id);
                var diagnostic = new Dictionary<string, object>();
                var order = CableWalk.Order(doc, routes, controller, devices, out var missed, out string problem,
                    DeviceMarkingOperations.CommentOf(devices[0]), diagnostic);
                row["status"] = problem != null ? "error" : "checked";
                row["message"] = problem; row["diagnostics"] = diagnostic;
                row["devicesWithoutPath"] = missed.Select(d => RevitIds.IdOf(d.Id)).ToList();
                row["order"] = order?.Select(d => RevitIds.IdOf(d.Id)).ToList();
                row["ok"] = problem == null && missed.Count == 0 &&
                    (!diagnostic.TryGetValue("unreachableRouteIds", out object isolated) || ((List<long>)isolated).Count == 0);
            }
            return new Dictionary<string, object>
            {
                ["ok"] = rows.Count > 0 && rows.All(r => (bool)r["ok"]), ["lines"] = rows, ["checkedLines"] = rows.Count(r => (string)r["status"] == "checked"),
                ["summary"] = "Связность — по графу маркировки CableWalk: стык 100 мм, устройство 600 мм, прибор 3 м; межэтажные переходы учитываются. Концы и развилки выводятся для проверки, сами по себе ошибкой не считаются. Это проверка размеченных линий; расчёт КЖ использует свой граф и отдельно объясняется explain_cable_length.",
            };
        }
    }
}
