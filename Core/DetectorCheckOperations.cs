using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// Анализ уже расставленных точечных извещателей по СП 484.1311500.2020 с Изменением № 1. Только чтение.
    /// Проверяется: отступ от стен (п. 6.6.36), расстояние до вентиляционных отверстий (п. 6.6.32),
    /// покрытие помещения зоной контроля и предельная высота (п. 6.6.15, 6.6.16).
    /// </summary>
    internal static class DetectorCheckOperations
    {
        private const double SmokeRadiusM = 6.4, SmokeMaxHeightM = 12, HeatRadiusM = 3.5, HeatMaxHeightM = 9;
        private const double WallMinMm = 500, WallAbsoluteMinMm = 100, VentMinMm = 1000;

        private sealed class Detector
        {
            public FamilyInstance Element;
            public XYZ Point;
            public string Kind;
            public RoomRef Room;
        }

        public static Dictionary<string, object> Check(Document doc, string viewValue, List<string> smokeKeywords, List<string> heatKeywords, int coverage, bool checkVents)
        {
            if (coverage != 1 && coverage != 2) throw new ArgumentException("coverage: 1 или 2.");
            View view = OpsCommon.ViewOrActive(doc, viewValue);
            List<string> smoke = smokeKeywords != null && smokeKeywords.Count > 0 ? smokeKeywords : new List<string> { "Дымов", "ИП 212", "ИП212", "smoke" };
            List<string> heat = heatKeywords != null && heatKeywords.Count > 0 ? heatKeywords : new List<string> { "Теплов", "ИП 101", "ИП101", "heat" };
            bool Has(FamilyInstance f, List<string> words) => words.Any(w => (f.Symbol.FamilyName + " " + f.Symbol.Name).IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0);

            List<FamilyInstance> all = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            var detectors = new List<Detector>();
            foreach (FamilyInstance f in all)
            {
                string kind = Has(f, smoke) ? "smoke" : Has(f, heat) ? "heat" : null;
                XYZ point = (f.Location as LocationPoint)?.Point;
                if (kind != null && point != null) detectors.Add(new Detector { Element = f, Point = point, Kind = kind });
            }
            if (detectors.Count == 0) throw new InvalidOperationException($"На виде «{view.Name}» нет дымовых или тепловых извещателей (проверьте ключевые слова семейств).");

            List<RoomRef> rooms = RoomAnalysisOperations.Find(doc, new RoomQuery());
            foreach (Detector d in detectors) d.Room = RoomAt(rooms, d.Point);

            List<XYZ> vents = checkVents ? Vents(doc) : new List<XYZ>();
            var detectorRows = new List<Dictionary<string, object>>();
            var boundaries = new Dictionary<RoomRef, List<double[]>>();
            foreach (Detector d in detectors)
            {
                var issues = new List<string>();
                double? wallMm = null;
                if (d.Room != null)
                {
                    List<double[]> edges = Edges(d.Room, boundaries);
                    wallMm = RevitText.ToMm(Clearance(edges, d.Point.X, d.Point.Y));
                    if (wallMm < WallAbsoluteMinMm) issues.Add($"до стены {wallMm:0} мм — меньше 0,1 м (п. 6.6.36)");
                    else if (wallMm < WallMinMm) issues.Add($"до стены {wallMm:0} мм — меньше 0,5 м: допустимо только в случаях п. 6.6.36");
                }
                else
                {
                    issues.Add("не найдено помещение (в проекте и загруженных связях)");
                }
                double? ventMm = vents.Count == 0 ? (double?)null : RevitText.ToMm(vents.Min(v => v.DistanceTo(d.Point)));
                if (ventMm < VentMinMm) issues.Add($"до вентиляционного отверстия {ventMm:0} мм — меньше 1 м (п. 6.6.32; ближе допустимо у вытяжки при скорости потока не более 1 м/с)");
                detectorRows.Add(new Dictionary<string, object>
                {
                    ["id"] = RevitIds.IdOf(d.Element.Id),
                    ["kind"] = d.Kind,
                    ["family"] = $"{d.Element.Symbol.FamilyName} : {d.Element.Symbol.Name}",
                    ["room"] = d.Room?.Room.Number,
                    ["wallDistanceMm"] = wallMm,
                    ["ventDistanceMm"] = ventMm,
                    ["issues"] = issues,
                });
            }

            var roomRows = new List<Dictionary<string, object>>();
            foreach (var group in detectors.Where(d => d.Room != null).GroupBy(d => d.Room))
            {
                RoomRef room = group.Key;
                double heightM = RevitText.ToMm(RoomAnalysisOperations.HeightFeet(room.Room)) / 1000;
                var issues = new List<string>();
                var coverageRows = new Dictionary<string, object>();
                foreach (var byKind in group.GroupBy(d => d.Kind))
                {
                    bool isSmoke = byKind.Key == "smoke";
                    double radius = RevitText.ToFeet((isSmoke ? SmokeRadiusM : HeatRadiusM) * 1000);
                    double maxHeight = isSmoke ? SmokeMaxHeightM : HeatMaxHeightM;
                    if (heightM > maxHeight) issues.Add($"высота {heightM:0.##} м больше предела {maxHeight} м для {(isSmoke ? "дымовых" : "тепловых")} (п. {(isSmoke ? "6.6.16" : "6.6.15")})");
                    double uncovered = Uncovered(Edges(room, boundaries), byKind.Select(d => (d.Point.X, d.Point.Y)).ToList(), radius, coverage);
                    coverageRows[byKind.Key] = new Dictionary<string, object> { ["detectors"] = byKind.Count(), ["uncoveredPercent"] = Math.Round(uncovered * 100, 1) };
                    if (uncovered > 0.001) issues.Add($"{(isSmoke ? "дымовые" : "тепловые")}: не покрыто {uncovered * 100:0.#} % площади при контроле каждой точки {coverage} ИП (п. 6.6.5, {(isSmoke ? "6.6.16" : "6.6.15")})");
                }
                roomRows.Add(new Dictionary<string, object>
                {
                    ["room"] = room.Room.Number,
                    ["name"] = room.Room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                    ["link"] = room.Link?.Name,
                    ["heightM"] = Math.Round(heightM, 2),
                    ["coverage"] = coverageRows,
                    ["issues"] = issues,
                });
            }

            return new Dictionary<string, object>
            {
                ["view"] = view.Name,
                ["detectors"] = detectors.Count,
                ["ignored"] = all.Count - detectors.Count,
                ["ventsFound"] = vents.Count,
                ["detectorIssues"] = detectorRows.Count(r => ((List<string>)r["issues"]).Count > 0),
                ["roomIssues"] = roomRows.Count(r => ((List<string>)r["issues"]).Count > 0),
                ["rooms"] = roomRows,
                ["items"] = detectorRows.Take(OpsCommon.MaxRows).ToList(),
                ["note"] = "Анализ по СП 484 с Изм. № 1: п. 6.6.32, 6.6.36, 6.6.15–6.6.16. Не проверяются: балки и выступы, расстояние до перекрытия (п. 6.6.12, зависит от семейства), наклонные потолки, " +
                           "фальшпол и фальшпотолок. Вентиляционные отверстия — воздухораспределители в проекте и связях." + (checkVents && vents.Count == 0 ? " Воздухораспределители не найдены." : ""),
            };
        }

        private static RoomRef RoomAt(List<RoomRef> rooms, XYZ point)
        {
            // Извещатель у потолка: ищем помещение чуть ниже точки вставки.
            foreach (double drop in new[] { 300.0, 1000.0, 0.0 })
            {
                XYZ probe = new XYZ(point.X, point.Y, point.Z - RevitText.ToFeet(drop));
                RoomRef found = rooms.FirstOrDefault(r => r.Room.IsPointInRoom(r.Transform.Inverse.OfPoint(probe)));
                if (found != null) return found;
            }
            return null;
        }

        private static List<XYZ> Vents(Document doc)
        {
            var result = new List<XYZ>();
            var sources = new List<(Document, Transform)> { (doc, Transform.Identity) };
            sources.AddRange(LinkScope.LoadedLinks(doc).Select(l => (l.GetLinkDocument(), l.GetTotalTransform())));
            foreach (var (source, t) in sources)
            {
                foreach (Element e in new FilteredElementCollector(source).OfCategory(BuiltInCategory.OST_DuctTerminal).WhereElementIsNotElementType())
                {
                    XYZ p = LinkScope.LocationOf(e);
                    if (p != null) result.Add(t.OfPoint(p));
                }
            }
            return result;
        }

        /// <summary>Отрезки контура помещения в координатах проекта: [x0, y0, x1, y1].</summary>
        private static List<double[]> Edges(RoomRef room, Dictionary<RoomRef, List<double[]>> cache)
        {
            if (cache.TryGetValue(room, out List<double[]> edges)) return edges;
            edges = new List<double[]>();
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            foreach (IList<BoundarySegment> loop in room.Room.GetBoundarySegments(options) ?? new List<IList<BoundarySegment>>())
            {
                foreach (BoundarySegment segment in loop)
                {
                    IList<XYZ> points = segment.GetCurve().Tessellate();
                    for (int i = 0; i + 1 < points.Count; i++)
                    {
                        XYZ a = room.Transform.OfPoint(points[i]), b = room.Transform.OfPoint(points[i + 1]);
                        edges.Add(new[] { a.X, a.Y, b.X, b.Y });
                    }
                }
            }
            cache[room] = edges;
            return edges;
        }

        private static double Clearance(List<double[]> edges, double x, double y)
        {
            double best = double.MaxValue;
            foreach (double[] e in edges)
            {
                double dx = e[2] - e[0], dy = e[3] - e[1], len = dx * dx + dy * dy;
                double t = len < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - e[0]) * dx + (y - e[1]) * dy) / len));
                double px = e[0] + t * dx - x, py = e[1] + t * dy - y;
                best = Math.Min(best, px * px + py * py);
            }
            return Math.Sqrt(best);
        }

        private static bool Inside(List<double[]> edges, double x, double y)
        {
            bool inside = false;
            foreach (double[] e in edges)
            {
                if ((e[1] > y) != (e[3] > y) && x < e[0] + (y - e[1]) / (e[3] - e[1]) * (e[2] - e[0])) inside = !inside;
            }
            return inside;
        }

        /// <summary>Доля точек помещения (сетка и контур), которые контролируются меньше чем coverage извещателями.</summary>
        private static double Uncovered(List<double[]> edges, List<(double x, double y)> detectors, double radius, int coverage)
        {
            if (edges.Count == 0) return 0;
            double minX = edges.Min(e => Math.Min(e[0], e[2])), maxX = edges.Max(e => Math.Max(e[0], e[2]));
            double minY = edges.Min(e => Math.Min(e[1], e[3])), maxY = edges.Max(e => Math.Max(e[1], e[3]));
            double step = Math.Max(RevitText.ToFeet(250), Math.Max(maxX - minX, maxY - minY) / 250);
            var samples = new List<(double x, double y)>();
            for (double x = minX + step / 2; x < maxX; x += step)
                for (double y = minY + step / 2; y < maxY; y += step)
                    if (Inside(edges, x, y)) samples.Add((x, y));
            foreach (double[] e in edges) samples.Add((e[0], e[1]));
            if (samples.Count == 0) return 0;
            double r2 = radius * radius;
            int bad = samples.Count(s => detectors.Count(d => (d.x - s.x) * (d.x - s.x) + (d.y - s.y) * (d.y - s.y) <= r2) < coverage);
            return (double)bad / samples.Count;
        }
    }
}
