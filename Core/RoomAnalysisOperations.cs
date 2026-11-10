using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;

namespace PluginForRevit2022.Core
{
    internal sealed class RoomQuery
    {
        /// <summary>host, links или all.</summary>
        public string Source { get; set; } = "all";
        public List<string> Links { get; set; } = new List<string>();
        public string Level { get; set; }
        public List<string> Numbers { get; set; } = new List<string>();
        public List<long> Ids { get; set; } = new List<long>();
    }

    internal sealed class RoomRef
    {
        public Room Room;
        public RevitLinkInstance Link;
        public Transform Transform;
        public Document Source => Room.Document;
    }

    /// <summary>Помещения проекта и связей: геометрия, поверхности, проёмы; пространства (Spaces).</summary>
    internal static class RoomAnalysisOperations
    {
        public const double SquareFeetToM2 = 0.09290304;
        public const double CubicFeetToM3 = 0.028316846592;
        private const double FeetToM = 0.3048;

        // ===================== Отбор помещений =====================

        public static List<RoomRef> Find(Document doc, RoomQuery query)
        {
            string source = (query.Source ?? "all").Trim().ToLowerInvariant();
            var sources = new List<(Document doc, RevitLinkInstance link, Transform t)>();
            if (source != "links") sources.Add((doc, null, Transform.Identity));
            if (source != "host")
            {
                List<RevitLinkInstance> links = query.Links != null && query.Links.Count > 0
                    ? LinkScope.ResolveLinks(doc, query.Links, new List<string>())
                    : LinkScope.LoadedLinks(doc);
                sources.AddRange(links.Select(l => (l.GetLinkDocument(), l, l.GetTotalTransform())));
            }

            var numbers = new HashSet<string>(query.Numbers ?? new List<string>(), StringComparer.CurrentCultureIgnoreCase);
            var ids = new HashSet<long>(query.Ids ?? new List<long>());
            var result = new List<RoomRef>();
            foreach (var (source_, link, transform) in sources)
            {
                foreach (Room room in new FilteredElementCollector(source_).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
                {
                    if (room.Area <= 0) continue;
                    if (numbers.Count > 0 && !numbers.Contains(room.Number)) continue;
                    if (ids.Count > 0 && !ids.Contains(RevitIds.IdOf(room.Id))) continue;
                    if (!string.IsNullOrWhiteSpace(query.Level) && (room.Level?.Name ?? "").IndexOf(query.Level.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    result.Add(new RoomRef { Room = room, Link = link, Transform = transform });
                }
            }
            return result;
        }

        public static double HeightFeet(Room room)
        {
            if (room.UnboundedHeight > 1e-6) return room.UnboundedHeight;
            BoundingBoxXYZ box = room.get_BoundingBox(null);
            return box == null ? 0 : box.Max.Z - box.Min.Z;
        }

        // ===================== Помещения =====================

        public static Dictionary<string, object> Rooms(Document doc, RoomQuery query, bool includeBoundary)
        {
            List<RoomRef> rooms = Find(doc, query);
            return new Dictionary<string, object>
            {
                ["count"] = rooms.Count,
                ["totalAreaM2"] = Math.Round(rooms.Sum(r => r.Room.Area) * SquareFeetToM2, 2),
                ["rooms"] = rooms.Take(OpsCommon.MaxRows).Select(r => Describe(r, includeBoundary)).ToList(),
                ["truncated"] = rooms.Count > OpsCommon.MaxRows,
                ["note"] = "Высота — это верхний предел помещения из модели (в Revit по умолчанию 2438 мм), а не высота до потолка. Для объёма по фактической высоте нужен верхний предел помещений в АР.",
            };
        }

        private static Dictionary<string, object> Describe(RoomRef r, bool includeBoundary)
        {
            Room room = r.Room;
            double height = HeightFeet(room);
            bool revitVolume = room.Volume > 1e-6;
            XYZ center = (room.Location as LocationPoint)?.Point;
            var result = new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(room.Id),
                ["link"] = r.Link?.Name,
                ["number"] = room.Number,
                ["name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                ["department"] = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(),
                ["level"] = room.Level?.Name,
                ["areaM2"] = Math.Round(room.Area * SquareFeetToM2, 2),
                ["perimeterM"] = Math.Round(room.Perimeter * FeetToM, 2),
                ["heightMm"] = RevitText.ToMm(height),
                ["heightSource"] = room.UnboundedHeight > 1e-6 ? "верхний предел помещения" : "габарит помещения",
                ["volumeM3"] = Math.Round((revitVolume ? room.Volume : room.Area * height) * CubicFeetToM3, 2),
                ["volumeSource"] = revitVolume ? "Revit" : "площадь × высота",
                ["center"] = center == null ? null : OpsCommon.Point(r.Transform.OfPoint(center)),
            };
            if (includeBoundary)
            {
                var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
                result["boundaryMm"] = (room.GetBoundarySegments(options) ?? new List<IList<BoundarySegment>>()).Select(loop => loop
                    .Select(s => r.Transform.OfPoint(s.GetCurve().GetEndPoint(0)))
                    .Select(p => new[] { RevitText.ToMm(p.X), RevitText.ToMm(p.Y) }).ToList()).ToList();
            }
            return result;
        }

        // ===================== Поверхности =====================

        /// <summary>Площади пола, потолка и стен помещения по его геометрии; двери и окна вычитаются из стен.</summary>
        public static Dictionary<string, object> SurfaceAreas(Document doc, RoomQuery query, bool subtractOpenings, bool includeBreakdown)
        {
            List<RoomRef> rooms = Find(doc, query);
            if (rooms.Count == 0) throw new InvalidOperationException("Помещения не найдены.");
            if (rooms.Count > 200) throw new InvalidOperationException($"Найдено помещений: {rooms.Count}. Сузьте отбор (level, numbers, links) до 200.");

            var results = new List<Dictionary<string, object>>();
            var calculators = new Dictionary<Document, SpatialElementGeometryCalculator>();
            foreach (RoomRef r in rooms)
            {
                if (!calculators.TryGetValue(r.Source, out SpatialElementGeometryCalculator calculator))
                {
                    calculator = new SpatialElementGeometryCalculator(r.Source,
                        new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });
                    calculators[r.Source] = calculator;
                }
                results.Add(Surfaces(r, calculator, subtractOpenings, includeBreakdown));
            }
            return new Dictionary<string, object>
            {
                ["rooms"] = results,
                ["note"] = "Площади по чистовой границе помещения. Стены — по граням помещения у каждой ограждающей конструкции; проёмы дверей и окон, связанных с помещением, вычитаются из стены-основы.",
            };
        }

        private static Dictionary<string, object> Surfaces(RoomRef r, SpatialElementGeometryCalculator calculator, bool subtractOpenings, bool includeBreakdown)
        {
            Room room = r.Room;
            double floor = 0, ceiling = 0;
            var walls = new Dictionary<long, (string name, double area)>();
            var estimated = new List<string>();
            try
            {
                SpatialElementGeometryResults geometry = calculator.CalculateSpatialElementGeometry(room);
                foreach (Face face in geometry.GetGeometry().Faces)
                {
                    IList<SpatialElementBoundarySubface> subfaces = geometry.GetBoundaryFaceInfo(face);
                    if (subfaces.Count == 0)
                    {
                        AddByNormal(face, face.Area, ref floor, ref ceiling, walls, ElementId.InvalidElementId, r.Source);
                        continue;
                    }
                    foreach (SpatialElementBoundarySubface sub in subfaces)
                    {
                        double area = sub.GetSubface()?.Area ?? 0;
                        switch (sub.SubfaceType)
                        {
                            case SubfaceType.Bottom: floor += area; break;
                            case SubfaceType.Top: ceiling += area; break;
                            default:
                                LinkElementId boundary = sub.SpatialBoundaryElement;
                                ElementId id = boundary.LinkInstanceId == ElementId.InvalidElementId ? boundary.HostElementId : boundary.LinkedElementId;
                                AddWall(walls, id, area, r.Source);
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Нет замкнутой геометрии: оценка по площади и периметру.
                floor = ceiling = room.Area;
                walls[-1] = ("оценка: периметр × высота", room.Perimeter * HeightFeet(room));
                estimated.Add("геометрия не рассчитана: " + ex.Message);
            }
            if (floor <= 1e-6) { floor = room.Area; estimated.Add("пол"); }
            if (ceiling <= 1e-6) { ceiling = room.Area; estimated.Add("потолок"); }

            List<FamilyInstance> openings = Openings(r);
            var openingByWall = new Dictionary<long, double>();
            double openingTotal = 0;
            if (subtractOpenings)
            {
                foreach (FamilyInstance opening in openings)
                {
                    double area = OpeningSize(opening).width * OpeningSize(opening).height;
                    long host = opening.Host == null ? -1 : RevitIds.IdOf(opening.Host.Id);
                    if (!walls.ContainsKey(host)) continue;
                    openingByWall[host] = (openingByWall.TryGetValue(host, out double a) ? a : 0) + area;
                    openingTotal += area;
                }
            }
            double wallsGross = walls.Values.Sum(w => w.area);
            double wallsNet = Math.Max(0, wallsGross - openingTotal);
            var result = new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(room.Id),
                ["link"] = r.Link?.Name,
                ["number"] = room.Number,
                ["name"] = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                ["floorM2"] = Math.Round(floor * SquareFeetToM2, 2),
                ["ceilingM2"] = Math.Round(ceiling * SquareFeetToM2, 2),
                ["wallsGrossM2"] = Math.Round(wallsGross * SquareFeetToM2, 2),
                ["openingsM2"] = Math.Round(openingTotal * SquareFeetToM2, 2),
                ["wallsNetM2"] = Math.Round(wallsNet * SquareFeetToM2, 2),
                ["totalM2"] = Math.Round((floor + ceiling + wallsNet) * SquareFeetToM2, 2),
                ["estimated"] = estimated,
            };
            if (includeBreakdown)
            {
                result["walls"] = walls.Select(w => new Dictionary<string, object>
                {
                    ["elementId"] = w.Key,
                    ["element"] = w.Value.name,
                    ["grossM2"] = Math.Round(w.Value.area * SquareFeetToM2, 2),
                    ["openingsM2"] = Math.Round((openingByWall.TryGetValue(w.Key, out double o) ? o : 0) * SquareFeetToM2, 2),
                }).ToList();
            }
            return result;
        }

        private static void AddByNormal(Face face, double area, ref double floor, ref double ceiling, Dictionary<long, (string, double)> walls, ElementId id, Document source)
        {
            XYZ normal = face.ComputeNormal(new UV(0.5, 0.5));
            if (normal.Z < -0.7) floor += area;
            else if (normal.Z > 0.7) ceiling += area;
            else AddWall(walls, id, area, source);
        }

        private static void AddWall(Dictionary<long, (string name, double area)> walls, ElementId id, double area, Document source)
        {
            long key = id == null || id == ElementId.InvalidElementId ? -1 : RevitIds.IdOf(id);
            Element element = key < 0 ? null : source.GetElement(id);
            string name = element == null ? "граница помещения" : $"{element.Category?.Name}: {source.GetElement(element.GetTypeId())?.Name ?? element.Name}";
            walls[key] = (walls.TryGetValue(key, out var w) ? w.name : name, (walls.TryGetValue(key, out var v) ? v.area : 0) + area);
        }

        // ===================== Проёмы =====================

        /// <summary>Двери и окна, у которых помещение указано как «из» или «в».</summary>
        public static List<FamilyInstance> Openings(RoomRef r) =>
            new FilteredElementCollector(r.Source).WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows }))
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(f => f.FromRoom?.Id == r.Room.Id || f.ToRoom?.Id == r.Room.Id).ToList();

        public static (double width, double height) OpeningSize(FamilyInstance opening)
        {
            double Read(params BuiltInParameter[] names)
            {
                foreach (BuiltInParameter name in names)
                {
                    double value = opening.get_Parameter(name)?.AsDouble() ?? 0;
                    if (value <= 0) value = opening.Symbol?.get_Parameter(name)?.AsDouble() ?? 0;
                    if (value > 0) return value;
                }
                return 0;
            }
            return (Read(BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM),
                    Read(BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM));
        }

        public static Dictionary<string, object> RoomOpenings(Document doc, RoomQuery query, bool includeDetails)
        {
            List<RoomRef> rooms = Find(doc, query);
            return new Dictionary<string, object>
            {
                ["rooms"] = rooms.Take(OpsCommon.MaxRows).Select(r =>
                {
                    List<FamilyInstance> openings = Openings(r);
                    var item = new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.IdOf(r.Room.Id),
                        ["link"] = r.Link?.Name,
                        ["number"] = r.Room.Number,
                        ["name"] = r.Room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                        ["doors"] = openings.Count(o => o.Category.Id.Equals(new ElementId(BuiltInCategory.OST_Doors))),
                        ["windows"] = openings.Count(o => o.Category.Id.Equals(new ElementId(BuiltInCategory.OST_Windows))),
                    };
                    if (includeDetails) item["openings"] = openings.Select(o => BuildingElementOperations.DescribeOpening(o, r.Link, r.Transform)).ToList();
                    return item;
                }).ToList(),
                ["note"] = "Дверь или окно считается у обоих помещений, которые оно соединяет.",
            };
        }

        // ===================== Пространства (Spaces) =====================

        public static Dictionary<string, object> Spaces(Document doc, string level, List<long> ids)
        {
            var wanted = new HashSet<long>(ids ?? new List<long>());
            List<Space> spaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType().OfType<Space>()
                .Where(s => wanted.Count == 0 || wanted.Contains(RevitIds.IdOf(s.Id)))
                .Where(s => string.IsNullOrWhiteSpace(level) || (s.Level?.Name ?? "").IndexOf(level.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            return new Dictionary<string, object>
            {
                ["count"] = spaces.Count,
                ["spaces"] = spaces.Take(OpsCommon.MaxRows).Select(s =>
                {
                    bool placed = s.Location != null && s.Area > 0;
                    var (point, source) = placed ? Inside(s) : (null, "none");
                    return new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.IdOf(s.Id),
                        ["number"] = s.Number,
                        ["name"] = s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                        ["level"] = s.Level?.Name,
                        ["placed"] = placed,
                        ["areaM2"] = Math.Round(s.Area * SquareFeetToM2, 2),
                        ["volumeM3"] = Math.Round(s.Volume * CubicFeetToM3, 2),
                        ["boundedHeightMm"] = s.Area > 0 && s.Volume > 0 ? RevitText.ToMm(s.Volume / s.Area) : (double?)null,
                        ["point"] = OpsCommon.Point(point),
                        ["pointSource"] = source,
                    };
                }).ToList(),
            };
        }

        /// <summary>Точка внутри пространства: точка вставки, центр габарита или узел сетки — каждая проверяется.</summary>
        private static (XYZ point, string source) Inside(Space space)
        {
            XYZ location = (space.Location as LocationPoint)?.Point;
            if (location != null && space.IsPointInSpace(location)) return (location, "location");
            BoundingBoxXYZ box = space.get_BoundingBox(null);
            if (box == null) return (null, "none");
            XYZ center = (box.Min + box.Max) / 2;
            if (space.IsPointInSpace(center)) return (center, "boxCenter");
            for (int i = 1; i < 5; i++)
            {
                for (int j = 1; j < 5; j++)
                {
                    var p = new XYZ(box.Min.X + (box.Max.X - box.Min.X) * i / 5, box.Min.Y + (box.Max.Y - box.Min.Y) * j / 5, center.Z);
                    if (space.IsPointInSpace(p)) return (p, "grid");
                }
            }
            return (null, "none");
        }
    }
}
