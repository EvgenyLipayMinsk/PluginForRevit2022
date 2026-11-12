using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// Проверка уровней для кабельного журнала, только чтение. Повторяет, как журнал определяет уровень
    /// (RouteLengthCalculator.GetLevelIdFromHost / GetLevelIdForDevice), и сравнивает по линиям:
    /// трассы без уровня (журнал кладёт их в общий граф «−1»), трассы, у которых уровень журнала не совпадает
    /// с уровнем по высоте, устройства, у которых уровень журнала не совпадает с уровнем их трассы
    /// (журнал не найдёт путь и «перекинет мост»), и пустой ADSK_Этаж у трасс и устройств.
    /// </summary>
    internal static class JournalLevelCheck
    {
        public static Dictionary<string, object> Run(Document doc, double attachMm)
        {
            double attach = (attachMm <= 0 ? 600 : attachMm) / 304.8;
            List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            string LevelName(long id) => id == -1 ? "нет уровня" : levels.FirstOrDefault(l => RevitIds.IdOf(l.Id) == id)?.Name ?? id.ToString();

            var cablesByLine = new Dictionary<string, List<FamilyInstance>>();
            foreach (FamilyInstance cable in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>()
                         .Where(c => c.Location is LocationCurve && c.Symbol.FamilyName != "ARBM_Межэтажный переход"))
            {
                string line = LineOf(cable);
                if (line == null) continue;
                if (!cablesByLine.TryGetValue(line, out var list)) cablesByLine[line] = list = new List<FamilyInstance>();
                list.Add(cable);
            }

            var devicesByLine = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_DataDevices }))
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(d => d.Location is LocationPoint && int.TryParse(Read(d, DeviceMarkingOperations.ParamController), out _) && int.TryParse(Read(d, DeviceMarkingOperations.ParamLine), out _))
                .GroupBy(d => $"{int.Parse(Read(d, DeviceMarkingOperations.ParamController))}.{int.Parse(Read(d, DeviceMarkingOperations.ParamLine))}")
                .ToDictionary(g => g.Key, g => g.ToList());

            var lines = new List<Dictionary<string, object>>();
            int totalProblems = 0;
            foreach (string line in cablesByLine.Keys.Union(devicesByLine.Keys).OrderBy(k => int.Parse(k.Split('.')[0])).ThenBy(k => int.Parse(k.Split('.')[1])))
            {
                List<FamilyInstance> cables = cablesByLine.TryGetValue(line, out var c) ? c : new List<FamilyInstance>();
                List<FamilyInstance> devices = devicesByLine.TryGetValue(line, out var d) ? d : new List<FamilyInstance>();

                var cableInfo = cables.Select(cable =>
                {
                    Curve curve = ((LocationCurve)cable.Location).Curve;
                    long journal = CableLevel(cable);
                    long byHeight = RevitIds.IdOf(CableWalk.LevelByZ(doc, (curve.GetEndPoint(0) + curve.GetEndPoint(1)) / 2)?.Id ?? ElementId.InvalidElementId);
                    return (cable, curve, journal, byHeight);
                }).ToList();

                List<long> noLevel = cableInfo.Where(x => x.journal == -1).Select(x => RevitIds.IdOf(x.cable.Id)).ToList();
                List<string> wrongLevel = cableInfo.Where(x => x.journal != -1 && x.byHeight != -1 && x.journal != x.byHeight)
                    .Select(x => $"{RevitIds.IdOf(x.cable.Id)} ({LevelName(x.journal)} ≠ по высоте {LevelName(x.byHeight)})").ToList();
                List<long> cableNoFloor = cables.Where(x => string.IsNullOrWhiteSpace(Read(x, "ADSK_Этаж"))).Select(x => RevitIds.IdOf(x.Id)).ToList();

                var deviceMismatch = new List<string>();
                var notOnCable = new List<long>();
                foreach (FamilyInstance device in devices)
                {
                    XYZ p = ((LocationPoint)device.Location).Point;
                    long deviceLevel = DeviceLevel(doc, device, levels);
                    var nearest = cableInfo
                        .Select(x => (x, distance: DistanceToCurvePlan(p, x.curve)))
                        .Where(x => x.distance <= attach)
                        .OrderBy(x => Math.Abs(((x.x.curve.GetEndPoint(0) + x.x.curve.GetEndPoint(1)) / 2).Z - p.Z))
                        .ThenBy(x => x.distance)
                        .Select(x => x.x).ToList();
                    if (nearest.Count == 0)
                    {
                        notOnCable.Add(RevitIds.IdOf(device.Id));
                        continue;
                    }
                    long cableLevel = nearest[0].journal;
                    if (cableLevel != deviceLevel)
                    {
                        deviceMismatch.Add($"{RevitIds.IdOf(device.Id)} №{Read(device, DeviceMarkingOperations.ParamMark)}: устройство {LevelName(deviceLevel)}, трасса {LevelName(cableLevel)}");
                    }
                }
                List<long> deviceNoFloor = devices.Where(x => string.IsNullOrWhiteSpace(Read(x, "ADSK_Этаж"))).Select(x => RevitIds.IdOf(x.Id)).ToList();

                int problems = noLevel.Count + wrongLevel.Count + deviceMismatch.Count + notOnCable.Count;
                totalProblems += problems;
                lines.Add(new Dictionary<string, object>
                {
                    ["line"] = line,
                    ["cables"] = cables.Count,
                    ["devices"] = devices.Count,
                    ["ok"] = problems == 0,
                    ["cablesWithoutLevel"] = noLevel.Take(200).ToList(),
                    ["cablesLevelNotByHeight"] = wrongLevel.Take(100).ToList(),
                    ["devicesLevelNotAsCable"] = deviceMismatch.Take(200).ToList(),
                    ["devicesNotOnLineCable"] = notOnCable.Take(200).ToList(),
                    ["cablesWithoutAdskFloor"] = cableNoFloor.Count,
                    ["devicesWithoutAdskFloor"] = deviceNoFloor.Count,
                });
            }

            return new Dictionary<string, object>
            {
                ["lines"] = lines,
                ["problems"] = totalProblems,
                ["summary"] = totalProblems == 0
                    ? $"Линий: {lines.Count}. Уровни трасс и устройств для журнала согласованы."
                    : $"Линий: {lines.Count}, с замечаниями: {lines.Count(l => !(bool)l["ok"])}, всего замечаний: {totalProblems}. Модель не изменялась.",
            };
        }

        /// <summary>Как журнал: ADSK_Этаж (цифра) → уровень с этим номером или по высоте; основа-уровень, уровень основы, «Уровень», иначе −1.</summary>
        private static long CableLevel(FamilyInstance cable)
        {
            string floor = Read(cable, "ADSK_Этаж");
            if (int.TryParse(floor, out int number))
            {
                List<Level> all = new FilteredElementCollector(cable.Document).OfClass(typeof(Level)).Cast<Level>().ToList();
                Level byNumber = all.FirstOrDefault(l => { var m = Regex.Match(l.Name ?? "", @"-?\d+"); return m.Success && int.Parse(m.Value) == number; });
                if (byNumber != null) return RevitIds.IdOf(byNumber.Id);
                XYZ point = cable.Location is LocationPoint lp ? lp.Point
                    : cable.Location is LocationCurve lc ? (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) / 2 : null;
                Level byHeight = point == null ? null : all.Where(l => l.ProjectElevation <= point.Z + 50 / 304.8).OrderByDescending(l => l.ProjectElevation).FirstOrDefault();
                if (byHeight != null) return RevitIds.IdOf(byHeight.Id);
            }
            Element host = cable.Host;
            if (host is Level level) return RevitIds.IdOf(level.Id);
            if (host != null && host.LevelId != ElementId.InvalidElementId) return RevitIds.IdOf(host.LevelId);
            if (cable.LevelId != ElementId.InvalidElementId) return RevitIds.IdOf(cable.LevelId);
            return -1;
        }

        /// <summary>Как журнал: ADSK_Этаж как имя уровня, основа, «Уровень спецификации», ближайший по Elevation.</summary>
        private static long DeviceLevel(Document doc, FamilyInstance device, List<Level> levels)
        {
            string floor = Read(device, "ADSK_Этаж");
            Level byName = string.IsNullOrEmpty(floor) ? null : levels.FirstOrDefault(l => l.Name.Equals(floor, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return RevitIds.IdOf(byName.Id);
            long host = CableLevel(device);
            if (host != -1) return host;
            ElementId schedule = device.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)?.AsElementId();
            if (schedule != null && schedule != ElementId.InvalidElementId) return RevitIds.IdOf(schedule);
            double z = ((LocationPoint)device.Location).Point.Z;
            Level nearest = levels.OrderBy(l => Math.Abs(l.Elevation - z)).FirstOrDefault();
            return nearest == null ? -1 : RevitIds.IdOf(nearest.Id);
        }

        private static string LineOf(Element element)
        {
            string value = element.LookupParameter(DeviceMarkingOperations.ParamGrouping)?.AsString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] parts = value.Split('.');
            if (parts.Length < 3) return null;
            return int.TryParse(parts[parts.Length - 2].Trim(), out int c) && int.TryParse(parts[parts.Length - 1].Trim(), out int l) ? $"{c}.{l}" : null;
        }

        private static double DistanceToCurvePlan(XYZ p, Curve curve)
        {
            XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
            double ex = b.X - a.X, ey = b.Y - a.Y, l2 = ex * ex + ey * ey;
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * ex + (p.Y - a.Y) * ey) / l2));
            double x = a.X + ex * t - p.X, y = a.Y + ey * t - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        private static string Read(Element element, string name) => element.LookupParameter(name)?.AsString()?.Trim() ?? "";
    }
}
