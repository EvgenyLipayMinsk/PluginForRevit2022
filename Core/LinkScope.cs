using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Элемент текущего проекта или связанного файла вместе с преобразованием в координаты проекта.</summary>
    internal sealed class ScopedElement
    {
        public Element Element { get; set; }
        public RevitLinkInstance Link { get; set; }
        public Transform Transform { get; set; } = Transform.Identity;

        public bool IsLinked => Link != null;
        public string Source => Link == null ? "проект" : Link.Name;

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["id"] = RevitIds.IdOf(Element.Id),
            ["name"] = Element.Name,
            ["link"] = Link?.Name,
        };
    }

    /// <summary>Связанные RVT-файлы и общие геометрические помощники для видов в плане.</summary>
    internal static class LinkScope
    {
        /// <summary>Загруженные экземпляры связей.</summary>
        public static List<RevitLinkInstance> LoadedLinks(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Where(l => l.GetLinkDocument() != null)
                .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>Связи по именам (часть имени экземпляра или файла). Пустой список — все загруженные.</summary>
        public static List<RevitLinkInstance> ResolveLinks(Document doc, IList<string> names, List<string> warnings)
        {
            List<RevitLinkInstance> all = LoadedLinks(doc);
            List<RevitLinkInstance> unloaded = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() == null).ToList();
            if (unloaded.Count > 0)
            {
                warnings.Add("выгруженные связи пропущены: " + string.Join(", ", unloaded.Select(l => l.Name)));
            }
            if (names == null || names.Count == 0)
            {
                return all;
            }

            var result = new List<RevitLinkInstance>();
            foreach (string name in names)
            {
                List<RevitLinkInstance> matches = all.Where(l =>
                    l.Name.IndexOf(name, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    (l.GetLinkDocument()?.Title ?? "").IndexOf(name, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                if (matches.Count == 0)
                {
                    throw new ArgumentException($"Связь «{name}» не найдена среди загруженных: {string.Join(", ", all.Select(l => l.Name))}.");
                }
                result.AddRange(matches.Where(m => !result.Contains(m)));
            }
            return result;
        }

        /// <summary>Ответ команды размеров, когда проектировщик ещё не выбрал связи: ничего не создаётся.</summary>
        public static Dictionary<string, object> LinksRequired(Document doc, string viewName)
        {
            List<RevitLinkInstance> all = LoadedLinks(doc);
            List<string> suggested = all.Where(l => IsArchOrStructural(l.GetLinkDocument().Title)).Select(l => l.Name).ToList();
            return new Dictionary<string, object>
            {
                ["status"] = "links_required",
                ["view"] = viewName,
                ["created"] = 0,
                ["suggestedLinks"] = suggested,
                ["otherLinks"] = all.Select(l => l.Name).Except(suggested).ToList(),
                ["message"] = "Ничего не создано. Спросите проектировщика, с какими связями работать, и повторите команду с links; " +
                              "только с проектом — hostOnly=true. Обычно это связи АР, КР, КЖ (см. suggestedLinks).",
            };
        }

        private static bool IsArchOrStructural(string title) =>
            System.Text.RegularExpressions.Regex.IsMatch(title ?? "", @"(^|[_\-\s.])(АР|КР|КЖ)([_\-\s.\d]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static ViewPlan RequirePlan(View view)
        {
            if (!(view is ViewPlan plan) || plan.GenLevel == null)
            {
                throw new ArgumentException($"«{view.Name}» — не план этажа/потолка с уровнем.");
            }
            return plan;
        }

        /// <summary>Абсолютные отметки нижней и верхней границ секущего диапазона плана (футы).</summary>
        public static (double bottom, double cut, double top) ViewRangeZ(Document doc, ViewPlan plan)
        {
            double level = plan.GenLevel.ProjectElevation;
            PlanViewRange range = plan.GetViewRange();

            double Z(PlanViewPlane plane, double fallback)
            {
                ElementId levelId = range.GetLevelId(plane);
                if (levelId == null || levelId == ElementId.InvalidElementId || levelId == PlanViewRange.Unlimited ||
                    levelId == PlanViewRange.LevelAbove || levelId == PlanViewRange.LevelBelow)
                {
                    return fallback;
                }
                return ((Level)doc.GetElement(levelId)).ProjectElevation + range.GetOffset(plane);
            }

            double cut = Z(PlanViewPlane.CutPlane, level + RevitText.ToFeet(1200));
            double bottom = Z(PlanViewPlane.BottomClipPlane, level);
            double top = Z(PlanViewPlane.TopClipPlane, level + RevitText.ToFeet(4000));
            return (Math.Min(bottom, cut), cut, Math.Max(top, cut));
        }

        /// <summary>Точка внутри подрезки вида (если подрезка включена).</summary>
        public static bool InsideCrop(View view, XYZ point)
        {
            if (!view.CropBoxActive)
            {
                return true;
            }
            BoundingBoxXYZ crop = view.CropBox;
            XYZ local = crop.Transform.Inverse.OfPoint(point);
            return local.X >= crop.Min.X && local.X <= crop.Max.X && local.Y >= crop.Min.Y && local.Y <= crop.Max.Y;
        }

        public static XYZ LocationOf(Element element)
        {
            switch (element.Location)
            {
                case LocationPoint point:
                    return point.Point;
                case LocationCurve curve:
                    return curve.Curve.Evaluate(0.5, true);
                default:
                    BoundingBoxXYZ box = element.get_BoundingBox(null);
                    return box == null ? null : (box.Min + box.Max) / 2;
            }
        }

        /// <summary>Временный изометрический 3D-вид для ReferenceIntersector (удаляется вызывающим в той же транзакции).</summary>
        public static View3D CreateProbe3D(Document doc)
        {
            ViewFamilyType type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("В проекте нет типа 3D-вида.");
            return View3D.CreateIsometric(doc, type.Id);
        }
    }
}
