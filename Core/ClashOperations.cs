using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class ClashRequest
    {
        public List<string> CategoriesA { get; set; } = new List<string>();
        public List<string> CategoriesB { get; set; } = new List<string>();
        public string View { get; set; }
        public bool IncludeLinks { get; set; } = true;
        public List<string> Links { get; set; } = new List<string>();
        public double MinVolumeCm3 { get; set; } = 50;
        public int Limit { get; set; } = 300;
    }

    /// <summary>
    /// Коллизии по твёрдой геометрии: элементы плагина в проекте (группа A) против конструкций и сетей
    /// в проекте и связях (группа B). Только чтение.
    /// </summary>
    internal static class ClashOperations
    {
        /// <summary>Семейства трасс кабельного журнала: условные линии, в проверке коллизий не участвуют.</summary>
        private static readonly string[] RouteFamilies = { "ARBM_Кабель", "ARBM_Межэтажный переход" };

        /// <summary>Категории плагина: лотки и короба с фитингами, оборудование слаботочных систем.</summary>
        public static readonly string[] PluginCategories =
        {
            "OST_CableTray", "OST_CableTrayFitting", "OST_Conduit", "OST_ConduitFitting",
            "OST_FireAlarmDevices", "OST_DataDevices", "OST_NurseCallDevices", "OST_CommunicationDevices", "OST_SecurityDevices",
            "OST_ElectricalEquipment", "OST_ElectricalFixtures", "OST_LightingDevices", "OST_TelephoneDevices",
        };

        /// <summary>С чем проверять по умолчанию: несущие и ограждающие конструкции и чужие сети.</summary>
        public static readonly string[] DefaultObstacles =
        {
            "OST_StructuralFraming", "OST_StructuralColumns", "OST_Columns", "OST_Walls", "OST_Floors",
            "OST_DuctCurves", "OST_DuctFitting", "OST_PipeCurves", "OST_PipeFitting",
        };

        private sealed class Item
        {
            public Element Element;
            public RevitLinkInstance Link;
            public Transform Transform;
            public XYZ Min, Max;
            public List<Solid> Solids;
            public string Key => (Link == null ? "host" : RevitIds.IdOf(Link.Id).ToString()) + ":" + RevitIds.IdOf(Element.Id);
        }

        public static Dictionary<string, object> Find(Document doc, ClashRequest request)
        {
            List<string> namesA = request.CategoriesA != null && request.CategoriesA.Count > 0 ? request.CategoriesA : PluginCategories.ToList();
            List<string> namesB = request.CategoriesB != null && request.CategoriesB.Count > 0 ? request.CategoriesB : DefaultObstacles.ToList();
            double minVolume = request.MinVolumeCm3 / 28316.846592; // см³ → фут³

            List<Item> groupA = Collect(doc, null, Transform.Identity, namesA, request.View);
            var groupB = new List<Item>(Collect(doc, null, Transform.Identity, namesB, null));
            var warnings = new List<string>();
            if (request.IncludeLinks)
            {
                List<RevitLinkInstance> links = request.Links != null && request.Links.Count > 0
                    ? LinkScope.ResolveLinks(doc, request.Links, warnings) : LinkScope.LoadedLinks(doc);
                foreach (RevitLinkInstance link in links)
                {
                    groupB.AddRange(Collect(link.GetLinkDocument(), link, link.GetTotalTransform(), namesB, null));
                }
            }

            List<Item> sortedB = groupB.OrderBy(b => b.Min.X).ToList();
            double[] startsB = sortedB.Select(b => b.Min.X).ToArray();
            double maxWidthB = sortedB.Count == 0 ? 0 : sortedB.Max(b => b.Max.X - b.Min.X);

            var pairs = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>();
            int skipped = 0;
            foreach (Item a in groupA)
            {
                HashSet<string> connected = Connected(a.Element);
                long hostOfA = a.Element is FamilyInstance fi && fi.Host != null ? RevitIds.IdOf(fi.Host.Id) : -1;
                int index = LowerBound(startsB, a.Min.X - maxWidthB);
                for (int i = index; i < sortedB.Count && sortedB[i].Min.X <= a.Max.X; i++)
                {
                    Item b = sortedB[i];
                    if (!Overlap(a, b) || a.Key == b.Key || connected.Contains(b.Key)) continue;
                    if (b.Link == null && RevitIds.IdOf(b.Element.Id) == hostOfA) continue; // основа семейства
                    string pair = string.CompareOrdinal(a.Key, b.Key) < 0 ? a.Key + "|" + b.Key : b.Key + "|" + a.Key;
                    if (seen.Contains(pair)) continue;

                    double volume = 0;
                    XYZ at = null;
                    try
                    {
                        foreach (Solid sa in SolidsOf(a))
                        {
                            foreach (Solid sb in SolidsOf(b))
                            {
                                Solid common = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                                if (common == null || common.Volume <= 1e-9) continue;
                                volume += common.Volume;
                                at = at ?? common.ComputeCentroid();
                            }
                        }
                    }
                    catch (Exception)
                    {
                        skipped++;
                        continue;
                    }
                    if (volume < minVolume) continue;
                    seen.Add(pair);
                    pairs.Add(new Dictionary<string, object>
                    {
                        ["a"] = RevitIds.IdOf(a.Element.Id),
                        ["aName"] = $"{a.Element.Category?.Name}: {a.Element.Name}",
                        ["b"] = RevitIds.IdOf(b.Element.Id),
                        ["bName"] = $"{b.Element.Category?.Name}: {b.Element.Name}",
                        ["bLink"] = b.Link?.Name,
                        ["volumeCm3"] = Math.Round(volume * 28316.846592, 1),
                        ["at"] = OpsCommon.Point(at),
                    });
                    if (pairs.Count >= request.Limit) break;
                }
                if (pairs.Count >= request.Limit) break;
            }

            return new Dictionary<string, object>
            {
                ["checkedA"] = groupA.Count,
                ["checkedB"] = groupB.Count,
                ["byCategoryA"] = groupA.GroupBy(x => x.Element.Category.Name).ToDictionary(g => g.Key, g => g.Count()),
                ["clashes"] = pairs.Count,
                ["byPair"] = pairs.GroupBy(p => ((string)p["aName"]).Split(':')[0] + " × " + ((string)p["bName"]).Split(':')[0])
                    .ToDictionary(g => g.Key, g => g.Count()),
                ["pairs"] = pairs,
                ["skipped"] = skipped,
                ["truncated"] = pairs.Count >= request.Limit,
                ["warnings"] = warnings,
                ["note"] = "Группа A — элементы проекта, группа B — проект и связи. Пересечение считается по твёрдой геометрии; меньше minVolumeCm3 не учитывается. " +
                           "Соседние участки одной трассы (соединённые напрямую) и основа семейства не считаются коллизией. Показать: show_elements.",
            };
        }

        private static List<Item> Collect(Document doc, RevitLinkInstance link, Transform t, List<string> names, string view)
        {
            List<ElementId> categories = SharedParameterOperations.ResolveCategories(doc, names).Select(c => c.Id).ToList();
            if (categories.Count == 0) return new List<Item>();
            FilteredElementCollector collector = string.IsNullOrWhiteSpace(view)
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, OpsCommon.ViewOrActive(doc, view).Id);
            var items = new List<Item>();
            foreach (Element e in collector.WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType())
            {
                string family = (e as FamilyInstance)?.Symbol?.FamilyName ?? "";
                if (RouteFamilies.Any(r => family.StartsWith(r, StringComparison.CurrentCultureIgnoreCase))) continue;
                BoundingBoxXYZ box = e.get_BoundingBox(null);
                if (box == null) continue;
                XYZ[] corners =
                {
                    box.Min, box.Max, new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                    new XYZ(box.Min.X, box.Min.Y, box.Max.Z), new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
                    new XYZ(box.Min.X, box.Max.Y, box.Max.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                };
                List<XYZ> moved = corners.Select(t.OfPoint).ToList();
                items.Add(new Item
                {
                    Element = e, Link = link, Transform = t,
                    Min = new XYZ(moved.Min(p => p.X), moved.Min(p => p.Y), moved.Min(p => p.Z)),
                    Max = new XYZ(moved.Max(p => p.X), moved.Max(p => p.Y), moved.Max(p => p.Z)),
                });
            }
            return items;
        }

        private static bool Overlap(Item a, Item b) =>
            a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

        private static int LowerBound(double[] values, double target)
        {
            int lo = 0, hi = values.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (values[mid] < target) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private static List<Solid> SolidsOf(Item item)
        {
            if (item.Solids != null) return item.Solids;
            var result = new List<Solid>();
            GeometryElement geometry = item.Element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geometry != null) Gather(geometry, result);
            bool identity = item.Transform.IsIdentity;
            item.Solids = identity ? result : result.Select(s => SolidUtils.CreateTransformed(s, item.Transform)).ToList();
            return item.Solids;
        }

        private static void Gather(GeometryElement geometry, List<Solid> result)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is Solid solid && solid.Volume > 1e-9) result.Add(solid);
                else if (obj is GeometryInstance instance) Gather(instance.GetInstanceGeometry(), result);
            }
        }

        /// <summary>Элементы, соединённые с этим через соединители (участки трассы и фитинги).</summary>
        private static HashSet<string> Connected(Element element)
        {
            var result = new HashSet<string>();
            ConnectorManager manager = (element as MEPCurve)?.ConnectorManager ?? (element as FamilyInstance)?.MEPModel?.ConnectorManager;
            if (manager == null) return result;
            foreach (Connector c in manager.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    if (r.Owner != null && r.Owner.Id != element.Id) result.Add("host:" + RevitIds.IdOf(r.Owner.Id));
                }
            }
            return result;
        }
    }
}
