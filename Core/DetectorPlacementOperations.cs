using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;

namespace PluginForRevit2022.Core
{
    internal sealed class PlaceDetectorsRequest
    {
        public string View { get; set; }
        /// <summary>Точка клика на плане (команда с ленты). Если задана, Room/RoomId не нужны.</summary>
        public XYZ Point { get; set; }
        /// <summary>Номер помещения.</summary>
        public string Room { get; set; }
        public long? RoomId { get; set; }
        /// <summary>Связь с помещением (часть имени); пусто — проект.</summary>
        public string Link { get; set; }
        /// <summary>smoke или heat.</summary>
        public string Detector { get; set; }
        /// <summary>«Семейство : Тип».</summary>
        public string FamilyType { get; set; }
        /// <summary>Сколько извещателей контролирует каждую точку: 1 или 2 (СП 484, п. 6.6.1, 6.6.2).</summary>
        public int Coverage { get; set; } = 1;
        /// <summary>Высота потолка от пола помещения, задаётся вручную. Пусто — потолок ищется в проекте и связях.</summary>
        public double? CeilingHeightMm { get; set; }
        /// <summary>Отступ от стен (СП 484, п. 6.6.36).</summary>
        public double WallOffsetMm { get; set; } = 500;
        /// <summary>В каких связях искать потолок; пусто — все загруженные.</summary>
        public List<string> Links { get; set; } = new List<string>();
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Расстановка точечных дымовых и тепловых извещателей в одном помещении по СП 484 с Изменением № 1.</summary>
    internal static class DetectorPlacementOperations
    {
        // СП 484.1311500.2020 с Изменением № 1: п. 6.6.15 (тепловые), п. 6.6.16 (дымовые).
        private const double SmokeRadiusM = 6.4, SmokeMaxHeightM = 12;
        private const double HeatRadiusM = 3.5, HeatMaxHeightM = 9;

        public static List<Dictionary<string, object>> ListFamilies(Document doc) =>
            PlaceableSymbols(doc).Select(s => new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(s.Id),
                ["name"] = $"{s.FamilyName} : {s.Name}",
            }).ToList();

        public static List<FamilySymbol> PlaceableSymbols(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                .Cast<FamilySymbol>()
                .Where(s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();

        public static Dictionary<string, object> Place(Document doc, PlaceDetectorsRequest request)
        {
            ViewPlan view = LinkScope.RequirePlan(ViewOperations.ResolveView(doc, request.View, ViewType.FloorPlan));
            string detector = (request.Detector ?? "").Trim().ToLowerInvariant();
            if (detector != "smoke" && detector != "heat") throw new ArgumentException("detector: smoke или heat.");
            if (request.Coverage != 1 && request.Coverage != 2) throw new ArgumentException("coverage: 1 или 2.");
            if (request.WallOffsetMm < 100) throw new ArgumentException("wallOffsetMm: не менее 100 мм (СП 484, п. 6.6.36).");
            double radiusM = detector == "smoke" ? SmokeRadiusM : HeatRadiusM;
            double maxHeightM = detector == "smoke" ? SmokeMaxHeightM : HeatMaxHeightM;
            FamilySymbol symbol = ResolveSymbol(doc, request.FamilyType);

            var warnings = new List<string>();
            if (request.WallOffsetMm < 500)
            {
                warnings.Add("отступ от стен меньше 0,5 м допускается только в случаях, указанных в п. 6.6.36 СП 484");
            }
            var (room, link, transform) = ResolveRoom(doc, view, request);
            List<Edge2> edges = Boundary(room, transform);
            if (edges.Count == 0) throw new InvalidOperationException("У помещения нет замкнутого контура.");
            BoundingBoxXYZ box = room.get_BoundingBox(null) ?? throw new InvalidOperationException("У помещения нет габаритов.");
            double floorZ = transform.OfPoint(box.Min).Z;
            double topZ = transform.OfPoint(box.Max).Z;

            List<(double x, double y)> points = Solve(edges, RevitText.ToFeet(radiusM * 1000), RevitText.ToFeet(request.WallOffsetMm), request.Coverage, out double spacing)
                ?? throw new InvalidOperationException(
                    $"Не удалось покрыть помещение с отступом {request.WallOffsetMm} мм. Уменьшите отступ или проверьте контур помещения.");

            WarnExisting(doc, edges, floorZ, topZ, warnings);

            var created = new List<Dictionary<string, object>>();
            double maxHeightFound;
            using (var transaction = new Transaction(doc, "AerBIM: размещение извещателей"))
            {
                transaction.Start();
                List<double> heights = Heights(doc, request, points, floorZ, topZ, warnings);
                maxHeightFound = heights.Max();
                double heightM = RevitText.ToMm(maxHeightFound) / 1000;
                if (heightM > maxHeightM)
                {
                    transaction.RollBack();
                    throw new InvalidOperationException(
                        $"Высота помещения {heightM:0.##} м больше предела {maxHeightM} м для этого типа извещателей (СП 484, п. {(detector == "smoke" ? "6.6.16" : "6.6.15")}).");
                }

                if (!symbol.IsActive) symbol.Activate();
                Level level = view.GenLevel;
                var instances = new List<(FamilyInstance instance, double z)>();
                for (int i = 0; i < points.Count; i++)
                {
                    double z = floorZ + heights[i];
                    instances.Add((doc.Create.NewFamilyInstance(new XYZ(points[i].x, points[i].y, z), symbol, level, StructuralType.NonStructural), z));
                }
                doc.Regenerate();
                foreach (var (instance, z) in instances)
                {
                    double delta = z - ((LocationPoint)instance.Location).Point.Z;
                    if (Math.Abs(delta) > 1e-6) ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, delta));
                    XYZ p = ((LocationPoint)instance.Location).Point;
                    created.Add(new Dictionary<string, object>
                    {
                        ["id"] = request.DryRun ? (long?)null : RevitIds.IdOf(instance.Id),
                        ["xMm"] = RevitText.ToMm(p.X),
                        ["yMm"] = RevitText.ToMm(p.Y),
                        ["heightFromFloorMm"] = RevitText.ToMm(z - floorZ),
                    });
                }

                if (request.DryRun)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit не применил размещение.");
                }
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["room"] = room.Number + " " + room.Name,
                ["link"] = link?.Name,
                ["view"] = view.Name,
                ["family"] = $"{symbol.FamilyName} : {symbol.Name}",
                ["detector"] = detector,
                ["radiusM"] = radiusM,
                ["coverage"] = request.Coverage,
                ["maxHeightMm"] = RevitText.ToMm(maxHeightFound),
                ["spacingMm"] = RevitText.ToMm(spacing),
                ["count"] = created.Count,
                ["detectors"] = created,
                ["warnings"] = warnings.Distinct().ToList(),
                ["summary"] = request.DryRun
                    ? $"Предпросмотр: будет размещено извещателей: {created.Count}. Для размещения повторите с dryRun=false."
                    : $"Размещено извещателей: {created.Count}. Отменить можно в Revit через Ctrl+Z.",
            };
        }

        // ===================== Помещение =====================

        private static (Room room, RevitLinkInstance link, Transform transform) ResolveRoom(Document doc, ViewPlan view, PlaceDetectorsRequest request)
        {
            var sources = new List<(Document source, RevitLinkInstance link, Transform transform)> { (doc, null, Transform.Identity) };
            sources.AddRange(LinkScope.LoadedLinks(doc).Select(l => (l.GetLinkDocument(), l, l.GetTotalTransform())));

            if (request.Point != null)
            {
                double cut = LinkScope.ViewRangeZ(doc, view).cut;
                double level = view.GenLevel.ProjectElevation;
                foreach (double z in new[] { cut, level + RevitText.ToFeet(1000) })
                {
                    foreach (var (source, link, transform) in sources)
                    {
                        Room room = source.GetRoomAtPoint(transform.Inverse.OfPoint(new XYZ(request.Point.X, request.Point.Y, z)));
                        if (room != null) return (room, link, transform);
                    }
                }
                throw new InvalidOperationException("В указанной точке нет помещения (в проекте и загруженных связях).");
            }

            if (!string.IsNullOrWhiteSpace(request.Link))
            {
                var wanted = LinkScope.ResolveLinks(doc, new[] { request.Link }, new List<string>());
                sources = sources.Where(s => s.link != null && wanted.Any(w => w.Id == s.link.Id)).ToList();
            }
            var matches = new List<(Room, RevitLinkInstance, Transform)>();
            foreach (var (source, link, transform) in sources)
            {
                if (request.RoomId != null)
                {
                    if (source.GetElement(RevitIds.ToElementId(request.RoomId.Value)) is Room byId) matches.Add((byId, link, transform));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(request.Room)) throw new ArgumentException("Укажите room (номер) или roomId.");
                matches.AddRange(new FilteredElementCollector(source).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                    .Where(r => r.Area > 0 && string.Equals(r.Number, request.Room.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    .Select(r => (r, link, transform)));
            }
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException(matches.Count == 0
                ? "Помещение не найдено."
                : "Найдено несколько помещений: укажите link или roomId.");
        }

        private sealed class Edge2
        {
            public double X0, Y0, X1, Y1;
        }

        /// <summary>Контур помещения (с внутренними контурами колонн) в координатах проекта.</summary>
        private static List<Edge2> Boundary(Room room, Transform transform)
        {
            var edges = new List<Edge2>();
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            foreach (IList<BoundarySegment> loop in room.GetBoundarySegments(options) ?? new List<IList<BoundarySegment>>())
            {
                foreach (BoundarySegment segment in loop)
                {
                    IList<XYZ> points = segment.GetCurve().Tessellate();
                    for (int i = 0; i + 1 < points.Count; i++)
                    {
                        XYZ a = transform.OfPoint(points[i]), b = transform.OfPoint(points[i + 1]);
                        edges.Add(new Edge2 { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y });
                    }
                }
            }
            return edges;
        }

        private static bool Inside(List<Edge2> edges, double x, double y)
        {
            bool inside = false;
            foreach (Edge2 e in edges)
            {
                if ((e.Y0 > y) != (e.Y1 > y) && x < e.X0 + (y - e.Y0) / (e.Y1 - e.Y0) * (e.X1 - e.X0)) inside = !inside;
            }
            return inside;
        }

        private static double Clearance(List<Edge2> edges, double x, double y)
        {
            double best = double.MaxValue;
            foreach (Edge2 e in edges)
            {
                double dx = e.X1 - e.X0, dy = e.Y1 - e.Y0, len = dx * dx + dy * dy;
                double t = len < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - e.X0) * dx + (y - e.Y0) * dy) / len));
                double px = e.X0 + t * dx - x, py = e.Y0 + t * dy - y;
                best = Math.Min(best, px * px + py * py);
            }
            return Math.Sqrt(best);
        }

        // ===================== Расчёт сетки =====================

        /// <summary>Самая редкая равномерная сетка, при которой каждая точка контура и площади покрыта нужное число раз.</summary>
        private static List<(double x, double y)> Solve(List<Edge2> edges, double radius, double offset, int coverage, out double spacing)
        {
            double minX = edges.Min(e => Math.Min(e.X0, e.X1)), maxX = edges.Max(e => Math.Max(e.X0, e.X1));
            double minY = edges.Min(e => Math.Min(e.Y0, e.Y1)), maxY = edges.Max(e => Math.Max(e.Y0, e.Y1));
            double width = maxX - minX, height = maxY - minY;
            List<(double x, double y)> samples = Samples(edges, minX, minY, width, height);

            double start = coverage == 1 ? radius * Math.Sqrt(2) : radius;
            for (double factor = 1; factor >= 0.1; factor -= 0.02)
            {
                double step = start * factor;
                int nx = Math.Max(1, (int)Math.Ceiling(width / step));
                int ny = Math.Max(1, (int)Math.Ceiling(height / step));
                var points = new List<(double x, double y)>();
                for (int i = 0; i < nx; i++)
                {
                    for (int j = 0; j < ny; j++)
                    {
                        double x = minX + (i + 0.5) * width / nx, y = minY + (j + 0.5) * height / ny;
                        if (Inside(edges, x, y) && Clearance(edges, x, y) >= offset) points.Add((x, y));
                    }
                }
                if (points.Count >= coverage && Covered(samples, points, radius, coverage))
                {
                    spacing = Math.Max(width / nx, height / ny);
                    return points;
                }
            }
            spacing = 0;
            return null;
        }

        private static List<(double x, double y)> Samples(List<Edge2> edges, double minX, double minY, double width, double height)
        {
            double step = Math.Max(RevitText.ToFeet(250), Math.Max(width, height) / 250);
            var samples = new List<(double x, double y)>();
            for (double x = minX + step / 2; x < minX + width; x += step)
            {
                for (double y = minY + step / 2; y < minY + height; y += step)
                {
                    if (Inside(edges, x, y)) samples.Add((x, y));
                }
            }
            foreach (Edge2 e in edges)
            {
                double length = Math.Sqrt(Math.Pow(e.X1 - e.X0, 2) + Math.Pow(e.Y1 - e.Y0, 2));
                int parts = Math.Max(1, (int)Math.Ceiling(length / step));
                for (int i = 0; i <= parts; i++)
                {
                    double t = (double)i / parts;
                    samples.Add((e.X0 + t * (e.X1 - e.X0), e.Y0 + t * (e.Y1 - e.Y0)));
                }
            }
            return samples;
        }

        private static bool Covered(List<(double x, double y)> samples, List<(double x, double y)> points, double radius, int coverage)
        {
            double r2 = radius * radius;
            foreach (var (x, y) in samples)
            {
                int count = 0;
                foreach (var (px, py) in points)
                {
                    if ((px - x) * (px - x) + (py - y) * (py - y) <= r2 && ++count >= coverage) break;
                }
                if (count < coverage) return false;
            }
            return true;
        }

        // ===================== Высота =====================

        private static List<double> Heights(Document doc, PlaceDetectorsRequest request, List<(double x, double y)> points,
                                            double floorZ, double topZ, List<string> warnings)
        {
            if (request.CeilingHeightMm != null)
            {
                if (request.CeilingHeightMm <= 0) throw new ArgumentException("ceilingHeightMm должен быть больше нуля.");
                return points.Select(_ => RevitText.ToFeet(request.CeilingHeightMm.Value)).ToList();
            }

            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs };
            View3D probe = LinkScope.CreateProbe3D(doc);
            var keep = new HashSet<long>(categories.Select(c => (long)c)) { (long)BuiltInCategory.OST_RvtLinks };
            foreach (Category category in doc.Settings.Categories)
            {
                if (category.CategoryType == CategoryType.Model && !keep.Contains(RevitIds.IdOf(category.Id)) && probe.CanCategoryBeHidden(category.Id))
                {
                    probe.SetCategoryHidden(category.Id, true);
                }
            }
            if (request.Links.Count > 0)
            {
                var selected = new HashSet<long>(LinkScope.ResolveLinks(doc, request.Links, warnings).Select(l => RevitIds.IdOf(l.Id)));
                List<ElementId> others = LinkScope.LoadedLinks(doc).Where(l => !selected.Contains(RevitIds.IdOf(l.Id)) && l.CanBeHidden(probe)).Select(l => l.Id).ToList();
                if (others.Count > 0) probe.HideElements(others);
            }

            var catIds = new HashSet<long>(categories.Select(c => (long)c));
            ElementFilter filter = new LogicalOrFilter(new ElementMulticategoryFilter(categories), new ElementClassFilter(typeof(RevitLinkInstance)));
            var intersector = new ReferenceIntersector(filter, FindReferenceTarget.Face, probe) { FindReferencesInRevitLinks = true };

            double lift = RevitText.ToFeet(300);
            var result = new List<double>();
            int missed = 0;
            foreach (var (x, y) in points)
            {
                double? height = null;
                foreach (ReferenceWithContext hit in intersector.Find(new XYZ(x, y, floorZ + lift), XYZ.BasisZ).OrderBy(h => h.Proximity))
                {
                    if (IsTarget(doc, hit.GetReference(), catIds))
                    {
                        height = lift + hit.Proximity;
                        break;
                    }
                }
                if (height == null) missed++;
                result.Add(height ?? topZ - floorZ);
            }
            doc.Delete(probe.Id);
            if (missed > 0)
            {
                warnings.Add($"потолок не найден над {missed} точками: взята верхняя граница помещения. При необходимости задайте ceilingHeightMm");
            }
            return result;
        }

        private static bool IsTarget(Document doc, Reference reference, HashSet<long> categories)
        {
            Element element = reference.LinkedElementId != ElementId.InvalidElementId
                ? (doc.GetElement(reference.ElementId) as RevitLinkInstance)?.GetLinkDocument()?.GetElement(reference.LinkedElementId)
                : doc.GetElement(reference.ElementId);
            return element?.Category != null && categories.Contains(RevitIds.IdOf(element.Category.Id));
        }

        private static void WarnExisting(Document doc, List<Edge2> edges, double floorZ, double topZ, List<string> warnings)
        {
            int existing = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_FireAlarmDevices).WhereElementIsNotElementType()
                .Select(e => (e.Location as LocationPoint)?.Point)
                .Count(p => p != null && p.Z >= floorZ - 1 && p.Z <= topZ + 1 && Inside(edges, p.X, p.Y));
            if (existing > 0) warnings.Add($"в помещении уже есть извещатели: {existing}");
        }

        private static FamilySymbol ResolveSymbol(Document doc, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Укажите familyType («Семейство : Тип»). Список: list_detector_families.");
            string wanted = value.Trim();
            FamilySymbol symbol = PlaceableSymbols(doc).FirstOrDefault(s => string.Equals($"{s.FamilyName} : {s.Name}", wanted, StringComparison.CurrentCultureIgnoreCase));
            return symbol ?? throw new ArgumentException($"Тип «{wanted}» не найден среди семейств пожарных извещателей без основы. Список: list_detector_families.");
        }
    }
}
