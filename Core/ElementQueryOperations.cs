using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class ElementFilterRequest
    {
        public List<string> Categories { get; set; } = new List<string>();
        public string FamilyContains { get; set; }
        public string TypeContains { get; set; }
        public string Level { get; set; }
        public string View { get; set; }
        public double[] MinMm { get; set; }
        public double[] MaxMm { get; set; }
    }

    /// <summary>Поиск элементов, измерения, коллизии, материалы, выгрузка данных в CSV.</summary>
    internal static class ElementQueryOperations
    {
        private const double SquareFeetToM2 = 0.09290304;
        private const double CubicFeetToM3 = 0.028316846592;

        // ===================== Поиск =====================

        public static List<Element> Find(Document doc, ElementFilterRequest r)
        {
            FilteredElementCollector collector = string.IsNullOrWhiteSpace(r.View)
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, OpsCommon.ViewOrActive(doc, r.View).Id);
            collector = collector.WhereElementIsNotElementType();
            if (r.Categories.Count > 0) collector = collector.WherePasses(new ElementMulticategoryFilter(OpsCommon.CategoryIds(doc, r.Categories)));
            if (r.MinMm != null || r.MaxMm != null)
            {
                XYZ min = OpsCommon.FromMm(r.MinMm, "minMm"), max = OpsCommon.FromMm(r.MaxMm, "maxMm");
                collector = collector.WherePasses(new BoundingBoxIntersectsFilter(new Outline(
                    new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                    new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)))));
            }
            IEnumerable<Element> result = collector.Where(e => e.Category != null && !(e is RevitLinkInstance));
            if (!string.IsNullOrWhiteSpace(r.FamilyContains))
            {
                result = result.Where(e => ((e as FamilyInstance)?.Symbol?.FamilyName ?? (doc.GetElement(e.GetTypeId()) as ElementType)?.FamilyName ?? "")
                    .IndexOf(r.FamilyContains.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(r.TypeContains))
            {
                result = result.Where(e => (doc.GetElement(e.GetTypeId())?.Name ?? "").IndexOf(r.TypeContains.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(r.Level))
            {
                result = result.Where(e => string.Equals((doc.GetElement(e.LevelId) as Level)?.Name, r.Level.Trim(), StringComparison.CurrentCultureIgnoreCase));
            }
            return result.ToList();
        }

        public static Dictionary<string, object> Filter(Document doc, ElementFilterRequest request)
        {
            if (request.Categories.Count == 0 && request.MinMm == null && string.IsNullOrWhiteSpace(request.View))
            {
                throw new ArgumentException("Задайте хотя бы categories, view или область minMm/maxMm.");
            }
            List<Element> found = Find(doc, request);
            return new Dictionary<string, object>
            {
                ["count"] = found.Count,
                ["byCategory"] = found.GroupBy(e => e.Category.Name).ToDictionary(g => g.Key, g => g.Count()),
                ["elements"] = found.Take(OpsCommon.MaxRows).Select(SelectionOperations.Describe).ToList(),
                ["truncated"] = found.Count > OpsCommon.MaxRows,
            };
        }

        // ===================== Измерение =====================

        public static Dictionary<string, object> Measure(Document doc, long first, long second)
        {
            List<Element> pair = OpsCommon.Elements(doc, new[] { first, second });
            if (pair.Count != 2) throw new ArgumentException("Нужны два разных элемента.");
            XYZ a = LinkScope.LocationOf(pair[0]), b = LinkScope.LocationOf(pair[1]);
            BoundingBoxXYZ boxA = pair[0].get_BoundingBox(null), boxB = pair[1].get_BoundingBox(null);
            double? gap = null;
            if (boxA != null && boxB != null)
            {
                double Gap(double minA, double maxA, double minB, double maxB) => Math.Max(0, Math.Max(minA - maxB, minB - maxA));
                double gx = Gap(boxA.Min.X, boxA.Max.X, boxB.Min.X, boxB.Max.X);
                double gy = Gap(boxA.Min.Y, boxA.Max.Y, boxB.Min.Y, boxB.Max.Y);
                double gz = Gap(boxA.Min.Z, boxA.Max.Z, boxB.Min.Z, boxB.Max.Z);
                gap = RevitText.ToMm(Math.Sqrt(gx * gx + gy * gy + gz * gz));
            }
            return new Dictionary<string, object>
            {
                ["first"] = SelectionOperations.Describe(pair[0]),
                ["second"] = SelectionOperations.Describe(pair[1]),
                ["centerDistanceMm"] = a == null || b == null ? (double?)null : RevitText.ToMm(a.DistanceTo(b)),
                ["dxMm"] = a == null || b == null ? (double?)null : RevitText.ToMm(b.X - a.X),
                ["dyMm"] = a == null || b == null ? (double?)null : RevitText.ToMm(b.Y - a.Y),
                ["dzMm"] = a == null || b == null ? (double?)null : RevitText.ToMm(b.Z - a.Z),
                ["boundingBoxGapMm"] = gap,
                ["note"] = "centerDistance — между точками вставки (или центрами габаритов); boundingBoxGap — зазор между габаритами, 0 при пересечении.",
            };
        }

        // ===================== Выгрузка в CSV =====================

        public static Dictionary<string, object> ExportCsv(Document doc, ElementFilterRequest request, List<string> parameters, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Укажите path — файл .csv.");
            if (request.Categories.Count == 0) throw new ArgumentException("Укажите categories.");
            List<Element> elements = Find(doc, request);
            var columns = parameters ?? new List<string>();
            var text = new StringBuilder();
            string Cell(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
            text.AppendLine(string.Join(";", new[] { "id", "Категория", "Семейство", "Тип", "Уровень" }.Concat(columns).Select(Cell)));
            foreach (Element e in elements)
            {
                Element type = doc.GetElement(e.GetTypeId());
                var row = new List<string>
                {
                    RevitIds.IdOf(e.Id).ToString(), e.Category?.Name, (e as FamilyInstance)?.Symbol?.FamilyName ?? (type as ElementType)?.FamilyName,
                    type?.Name, (doc.GetElement(e.LevelId) as Level)?.Name,
                };
                foreach (string name in columns)
                {
                    row.Add(RevitText.ValueText(e.LookupParameter(name) ?? type?.LookupParameter(name)));
                }
                text.AppendLine(string.Join(";", row.Select(Cell)));
            }
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
            return new Dictionary<string, object> { ["file"] = path, ["rows"] = elements.Count, ["summary"] = $"Выгружено строк: {elements.Count} в {path}" };
        }

        // ===================== Материалы =====================

        public static Dictionary<string, object> Materials(Document doc, string nameContains, string materialClass) => new Dictionary<string, object>
        {
            ["materials"] = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .Where(m => string.IsNullOrWhiteSpace(nameContains) || m.Name.IndexOf(nameContains.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Where(m => string.IsNullOrWhiteSpace(materialClass) || string.Equals(m.MaterialClass, materialClass.Trim(), StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(m => m.Name)
                .Select(m => new Dictionary<string, object> { ["id"] = RevitIds.IdOf(m.Id), ["name"] = m.Name, ["class"] = m.MaterialClass })
                .Take(OpsCommon.MaxRows).ToList(),
        };

        public static Dictionary<string, object> MaterialProperties(Document doc, string value)
        {
            Material material = ResolveMaterial(doc, value);
            var result = new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(material.Id),
                ["name"] = material.Name,
                ["class"] = material.MaterialClass,
                ["category"] = material.MaterialCategory,
                ["color"] = $"{material.Color.Red},{material.Color.Green},{material.Color.Blue}",
                ["transparency"] = material.Transparency,
                ["shininess"] = material.Shininess,
                ["smoothness"] = material.Smoothness,
                ["appearanceAsset"] = doc.GetElement(material.AppearanceAssetId)?.Name,
                ["structuralAsset"] = doc.GetElement(material.StructuralAssetId)?.Name,
                ["thermalAsset"] = doc.GetElement(material.ThermalAssetId)?.Name,
            };
            if (doc.GetElement(material.StructuralAssetId) is PropertySetElement structural)
            {
                StructuralAsset asset = structural.GetStructuralAsset();
                result["densityKgM3"] = Math.Round(asset.Density / CubicFeetToM3, 1);
            }
            return result;
        }

        private static Material ResolveMaterial(Document doc, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Укажите material (id или имя).");
            if (long.TryParse(value.Trim(), out long id) && doc.GetElement(RevitIds.ToElementId(id)) is Material byId) return byId;
            return new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, value.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ?? throw new ArgumentException($"Материал «{value}» не найден.");
        }

        /// <summary>Площади и объёмы материалов элементов (м², м³) и число элементов.</summary>
        public static Dictionary<string, object> MaterialQuantities(Document doc, ElementFilterRequest request)
        {
            if (request.Categories.Count == 0) throw new ArgumentException("Укажите categories.");
            List<Element> elements = Find(doc, request);
            var totals = new Dictionary<string, (double area, double volume, HashSet<long> elements)>();
            int withoutMaterial = 0;
            foreach (Element element in elements)
            {
                ICollection<ElementId> materials = element.GetMaterialIds(false);
                if (materials.Count == 0)
                {
                    withoutMaterial++;
                    continue;
                }
                foreach (ElementId materialId in materials)
                {
                    string name = doc.GetElement(materialId)?.Name ?? "—";
                    if (!totals.TryGetValue(name, out var item)) item = (0, 0, new HashSet<long>());
                    item.area += element.GetMaterialArea(materialId, false);
                    item.volume += element.GetMaterialVolume(materialId);
                    item.elements.Add(RevitIds.IdOf(element.Id));
                    totals[name] = item;
                }
            }
            return new Dictionary<string, object>
            {
                ["elements"] = elements.Count,
                ["byCategory"] = elements.GroupBy(e => e.Category.Name).ToDictionary(g => g.Key, g => g.Count()),
                ["withoutMaterial"] = withoutMaterial,
                ["materials"] = totals.OrderByDescending(t => t.Value.volume).Select(t => new Dictionary<string, object>
                {
                    ["material"] = t.Key,
                    ["areaM2"] = Math.Round(t.Value.area * SquareFeetToM2, 2),
                    ["volumeM3"] = Math.Round(t.Value.volume * CubicFeetToM3, 3),
                    ["elements"] = t.Value.elements.Count,
                }).ToList(),
            };
        }
    }
}
