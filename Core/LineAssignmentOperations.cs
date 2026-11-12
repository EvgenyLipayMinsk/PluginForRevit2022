using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// Раскладывает устройства АПС по линиям по трассам: линия трассы берётся из ADSK_Группирование
    /// («СПС_АЛС.прибор.линия»), устройство относится к линии, ближайшая трасса которой на том же уровне
    /// проходит не дальше maxDistanceMm. Только чтение: ничего в модели не меняет.
    /// </summary>
    internal static class LineAssignmentOperations
    {
        private sealed class Segment
        {
            public string Line;
            public long Level;
            public XYZ A;
            public XYZ B;
        }

        public static Dictionary<string, object> Assign(Document doc, double maxDistanceMm)
        {
            if (maxDistanceMm <= 0) throw new ArgumentException("maxDistanceMm должен быть больше нуля.");
            double maxFeet = maxDistanceMm / 304.8;

            // Трассы линий
            var segments = new List<Segment>();
            var cablesPerLine = new Dictionary<string, int>();
            foreach (FamilyInstance cable in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                string line = LineOf(cable);
                if (line == null || !(cable.Location is LocationCurve curve)) continue;
                cablesPerLine[line] = cablesPerLine.TryGetValue(line, out int n) ? n + 1 : 1;
                segments.Add(new Segment { Line = line, Level = LevelOf(cable), A = curve.Curve.GetEndPoint(0), B = curve.Curve.GetEndPoint(1) });
            }
            if (segments.Count == 0)
            {
                throw new InvalidOperationException("Нет трасс с ADSK_Группирование вида СПС_АЛС.прибор.линия.");
            }

            // Приборы: номер из ARBM_SP_ElementMark
            var controllers = new Dictionary<string, FamilyInstance>();
            var duplicateControllers = new List<string>();
            foreach (FamilyInstance device in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                string bco = device.LookupParameter(DeviceMarkingOperations.ParamBco)?.AsString()?.Trim();
                string mark = device.LookupParameter(DeviceMarkingOperations.ParamMark)?.AsString()?.Trim();
                if (string.IsNullOrEmpty(bco) || bco.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) < 0 || !int.TryParse(mark, out int number)) continue;
                string key = number.ToString();
                if (controllers.ContainsKey(key)) duplicateControllers.Add(key);
                else controllers[key] = device;
            }

            // Устройства АПС с галочкой в настройках маркировки
            MarkingSettings settings = MarkingSettings.Load();
            List<FamilyInstance> devices = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_FireAlarmDevices }))
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(d => settings.IsInLine(d.Symbol.FamilyName, "aps") && d.Location is LocationPoint)
                .ToList();

            var assigned = new Dictionary<string, List<FamilyInstance>>();
            var distances = new Dictionary<long, double>();
            var unassigned = new List<FamilyInstance>();
            var disputed = new List<Dictionary<string, object>>();
            foreach (FamilyInstance device in devices)
            {
                XYZ point = ((LocationPoint)device.Location).Point;
                long level = LevelOf(device);
                Dictionary<string, double> best = segments
                    .Where(s => s.Level == level || level == -1 || s.Level == -1)
                    .GroupBy(s => s.Line)
                    .ToDictionary(g => g.Key, g => g.Min(s => DistanceXY(point, s.A, s.B)));
                List<KeyValuePair<string, double>> near = best.Where(p => p.Value <= maxFeet).OrderBy(p => p.Value).ToList();
                if (near.Count == 0)
                {
                    unassigned.Add(device);
                    continue;
                }
                string line = near[0].Key;
                if (!assigned.ContainsKey(line)) assigned[line] = new List<FamilyInstance>();
                assigned[line].Add(device);
                distances[RevitIds.IdOf(device.Id)] = near[0].Value;
                if (near.Count > 1)
                {
                    disputed.Add(new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.IdOf(device.Id),
                        ["family"] = device.Symbol.FamilyName,
                        ["chosen"] = line,
                        ["candidates"] = near.Select(p => $"{p.Key} ({Math.Round(p.Value * 304.8)} мм)").ToList(),
                    });
                }
            }

            var lines = new List<Dictionary<string, object>>();
            foreach (string line in cablesPerLine.Keys.OrderBy(k => int.Parse(k.Split('.')[0])).ThenBy(k => int.Parse(k.Split('.')[1])))
            {
                string[] parts = line.Split('.');
                controllers.TryGetValue(parts[0], out FamilyInstance controller);
                List<FamilyInstance> members = assigned.TryGetValue(line, out List<FamilyInstance> list) ? list : new List<FamilyInstance>();
                var current = members
                    .GroupBy(d => $"{Read(d, DeviceMarkingOperations.ParamController)}.{Read(d, DeviceMarkingOperations.ParamLine)}")
                    .ToDictionary(g => g.Key == "." ? "не размечено" : g.Key, g => g.Count());
                lines.Add(new Dictionary<string, object>
                {
                    ["line"] = line,
                    ["controller"] = parts[0],
                    ["lineNumber"] = parts[1],
                    ["controllerElementId"] = controller == null ? (object)null : RevitIds.IdOf(controller.Id),
                    ["controllerMark"] = controller == null ? null : controller.LookupParameter("Марка")?.AsString() ?? controller.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    ["controllerBuilding"] = controller == null ? null : Read(controller, DeviceMarkingOperations.ParamBuilding),
                    ["cables"] = cablesPerLine[line],
                    ["devices"] = members.Count,
                    ["maxDistanceMm"] = members.Count == 0 ? 0 : Math.Round(members.Max(d => distances[RevitIds.IdOf(d.Id)]) * 304.8),
                    ["byFamily"] = members.GroupBy(d => d.Symbol.FamilyName).ToDictionary(g => g.Key, g => g.Count()),
                    ["currentMarking"] = current,
                    ["deviceIds"] = members.Select(d => RevitIds.IdOf(d.Id)).ToList(),
                });
            }

            var warnings = new List<string>();
            foreach (string key in duplicateControllers.Distinct()) warnings.Add($"несколько приборов ARK с номером {key}");
            foreach (Dictionary<string, object> l in lines.Where(l => l["controllerElementId"] == null))
            {
                warnings.Add($"линия {l["line"]}: не найден прибор ARK с ARBM_SP_ElementMark = {l["controller"]}");
            }

            return new Dictionary<string, object>
            {
                ["maxDistanceMm"] = maxDistanceMm,
                ["apsDevices"] = devices.Count,
                ["assignedDevices"] = devices.Count - unassigned.Count,
                ["lines"] = lines,
                ["unassigned"] = unassigned.Count,
                ["unassignedByLevel"] = unassigned.GroupBy(d => (doc.GetElement(d.LevelId) as Level)?.Name ?? "без уровня").ToDictionary(g => g.Key, g => g.Count()),
                ["unassignedIds"] = unassigned.Select(d => RevitIds.IdOf(d.Id)).Take(300).ToList(),
                ["disputed"] = disputed.Count,
                ["disputedDevices"] = disputed.Take(100).ToList(),
                ["warnings"] = warnings,
                ["summary"] = $"Устройств АПС: {devices.Count}, отнесено к линиям: {devices.Count - unassigned.Count}, без линии: {unassigned.Count}, спорных: {disputed.Count}. Модель не изменялась.",
            };
        }

        /// <summary>«СПС_АЛС.1.2» → «1.2»; null, если вида прибор.линия нет.</summary>
        private static string LineOf(Element element)
        {
            string value = element.LookupParameter(DeviceMarkingOperations.ParamGrouping)?.AsString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] parts = value.Split('.');
            if (parts.Length < 3) return null;
            string controller = parts[parts.Length - 2].Trim();
            string line = parts[parts.Length - 1].Trim();
            return int.TryParse(controller, out int c) && int.TryParse(line, out int l) ? $"{c}.{l}" : null;
        }

        private static long LevelOf(FamilyInstance element)
        {
            if (element.LevelId != ElementId.InvalidElementId) return RevitIds.IdOf(element.LevelId);
            Element host = element.Host;
            if (host is Level level) return RevitIds.IdOf(level.Id);
            if (host != null && host.LevelId != ElementId.InvalidElementId) return RevitIds.IdOf(host.LevelId);
            return -1;
        }

        private static double DistanceXY(XYZ p, XYZ a, XYZ b)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double len2 = abx * abx + aby * aby;
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
            double dx = p.X - (a.X + t * abx), dy = p.Y - (a.Y + t * aby);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Read(Element element, string name) => element.LookupParameter(name)?.AsString()?.Trim() ?? "";
    }
}
