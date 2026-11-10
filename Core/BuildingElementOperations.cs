using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Двери, окна и стены проекта и связей.</summary>
    internal static class BuildingElementOperations
    {
        private static List<(Document doc, RevitLinkInstance link, Transform t)> Sources(Document doc, bool includeLinks, List<string> links)
        {
            var result = new List<(Document, RevitLinkInstance, Transform)> { (doc, null, Transform.Identity) };
            if (includeLinks)
            {
                List<RevitLinkInstance> chosen = links != null && links.Count > 0 ? LinkScope.ResolveLinks(doc, links, new List<string>()) : LinkScope.LoadedLinks(doc);
                result.AddRange(chosen.Select(l => (l.GetLinkDocument(), l, l.GetTotalTransform())));
            }
            return result;
        }

        public static Dictionary<string, object> DescribeOpening(FamilyInstance opening, RevitLinkInstance link, Transform t)
        {
            var (width, height) = RoomAnalysisOperations.OpeningSize(opening);
            XYZ point = (opening.Location as LocationPoint)?.Point;
            XYZ facing = t.OfVector(opening.FacingOrientation);
            return new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(opening.Id),
                ["link"] = link?.Name,
                ["category"] = opening.Category?.Name,
                ["family"] = opening.Symbol?.FamilyName,
                ["type"] = opening.Symbol?.Name,
                ["mark"] = opening.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                ["level"] = (opening.Document.GetElement(opening.LevelId) as Level)?.Name,
                ["widthMm"] = RevitText.ToMm(width),
                ["heightMm"] = RevitText.ToMm(height),
                ["location"] = point == null ? null : OpsCommon.Point(t.OfPoint(point)),
                ["hostWallId"] = opening.Host == null ? (long?)null : RevitIds.IdOf(opening.Host.Id),
                ["facing"] = new[] { Math.Round(facing.X, 3), Math.Round(facing.Y, 3) },
                ["fromRoom"] = opening.FromRoom?.Number,
                ["toRoom"] = opening.ToRoom?.Number,
            };
        }

        public static Dictionary<string, object> DoorsWindows(Document doc, string category, string level, bool includeLinks, List<string> links, int maxCount)
        {
            string kind = (category ?? "all").Trim().ToLowerInvariant();
            var categories = new List<BuiltInCategory>();
            if (kind != "windows") categories.Add(BuiltInCategory.OST_Doors);
            if (kind != "doors") categories.Add(BuiltInCategory.OST_Windows);
            var items = new List<Dictionary<string, object>>();
            var byCategory = new Dictionary<string, int>();
            int total = 0;
            foreach (var (source, link, t) in Sources(doc, includeLinks, links))
            {
                foreach (FamilyInstance opening in new FilteredElementCollector(source).WherePasses(new ElementMulticategoryFilter(categories))
                             .WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    if (!string.IsNullOrWhiteSpace(level) && ((source.GetElement(opening.LevelId) as Level)?.Name ?? "").IndexOf(level.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    total++;
                    string name = opening.Category?.Name ?? "—";
                    byCategory[name] = (byCategory.TryGetValue(name, out int n) ? n : 0) + 1;
                    if (items.Count < maxCount) items.Add(DescribeOpening(opening, link, t));
                }
            }
            return new Dictionary<string, object>
            {
                ["total"] = total,
                ["byCategory"] = byCategory,
                ["items"] = items,
                ["truncated"] = total > items.Count,
            };
        }

        public static Dictionary<string, object> DescribeWall(Wall wall, RevitLinkInstance link, Transform t)
        {
            Document source = wall.Document;
            Curve curve = (wall.Location as LocationCurve)?.Curve;
            BoundingBoxXYZ box = wall.get_BoundingBox(null);
            double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            if (height <= 0 && box != null) height = box.Max.Z - box.Min.Z;
            return new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(wall.Id),
                ["link"] = link?.Name,
                ["type"] = wall.WallType?.Name,
                ["function"] = wall.WallType?.Function.ToString(),
                ["thicknessMm"] = RevitText.ToMm(wall.Width),
                ["lengthMm"] = curve == null ? (double?)null : RevitText.ToMm(curve.Length),
                ["heightMm"] = RevitText.ToMm(height),
                ["baseLevel"] = (source.GetElement(wall.LevelId) as Level)?.Name,
                ["structural"] = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1,
                ["start"] = curve == null ? null : OpsCommon.Point(t.OfPoint(curve.GetEndPoint(0))),
                ["end"] = curve == null ? null : OpsCommon.Point(t.OfPoint(curve.GetEndPoint(1))),
            };
        }

        public static Dictionary<string, object> WallInfo(Document doc, List<long> ids, string link)
        {
            if (ids == null || ids.Count == 0) throw new ArgumentException("Укажите ids.");
            RevitLinkInstance instance = string.IsNullOrWhiteSpace(link) ? null : LinkScope.ResolveLinks(doc, new[] { link }, new List<string>()).First();
            Document source = instance?.GetLinkDocument() ?? doc;
            Transform t = instance?.GetTotalTransform() ?? Transform.Identity;
            return new Dictionary<string, object>
            {
                ["walls"] = ids.Select(id => source.GetElement(RevitIds.ToElementId(id)) as Wall
                        ?? throw new ArgumentException($"Стена id {id} не найдена{(instance == null ? " в проекте" : " в связи")}."))
                    .Select(w => DescribeWall(w, instance, t)).ToList(),
            };
        }

        /// <summary>Стены, ось которых проходит в пределах радиуса от точки в плане.</summary>
        public static Dictionary<string, object> WallsNear(Document doc, double xMm, double yMm, double radiusMm, string level, bool includeLinks)
        {
            if (radiusMm <= 0) throw new ArgumentException("radiusMm должен быть больше нуля.");
            var point = new XYZ(RevitText.ToFeet(xMm), RevitText.ToFeet(yMm), 0);
            double radius = RevitText.ToFeet(radiusMm);
            var found = new List<(double distance, Dictionary<string, object> wall)>();
            foreach (var (source, link, t) in Sources(doc, includeLinks, null))
            {
                Transform inverse = t.Inverse;
                foreach (Wall wall in new FilteredElementCollector(source).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (!(wall.Location is LocationCurve lc)) continue;
                    if (!string.IsNullOrWhiteSpace(level) && ((source.GetElement(wall.LevelId) as Level)?.Name ?? "").IndexOf(level.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    Curve curve = lc.Curve;
                    XYZ local = inverse.OfPoint(point);
                    XYZ probe = new XYZ(local.X, local.Y, curve.GetEndPoint(0).Z);
                    IntersectionResult projection = curve.Project(probe);
                    if (projection == null) continue;
                    XYZ nearest = projection.XYZPoint;
                    double distance = Math.Sqrt(Math.Pow(nearest.X - probe.X, 2) + Math.Pow(nearest.Y - probe.Y, 2));
                    if (distance > radius) continue;
                    Dictionary<string, object> info = DescribeWall(wall, link, t);
                    info["distanceMm"] = RevitText.ToMm(distance);
                    found.Add((distance, info));
                }
            }
            return new Dictionary<string, object>
            {
                ["count"] = found.Count,
                ["walls"] = found.OrderBy(f => f.distance).Take(OpsCommon.MaxRows).Select(f => f.wall).ToList(),
                ["note"] = "distanceMm — расстояние в плане от точки до оси стены.",
            };
        }
    }
}
