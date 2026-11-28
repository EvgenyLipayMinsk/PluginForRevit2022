using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Распределение значений параметра и сведения о диапазоне и подрезке вида.</summary>
    internal static class ModelInsightOperations
    {
        public static Dictionary<string, object> FieldValues(Document doc, List<string> categories, string parameter, string link, string view)
        {
            if (categories == null || categories.Count == 0) throw new ArgumentException("Укажите categories.");
            if (string.IsNullOrWhiteSpace(parameter)) throw new ArgumentException("Укажите parameter.");
            Document source = doc;
            if (!string.IsNullOrWhiteSpace(link))
            {
                if (!string.IsNullOrWhiteSpace(view)) throw new ArgumentException("view не работает вместе с link.");
                source = LinkScope.ResolveLinks(doc, new[] { link }, new List<string>()).First().GetLinkDocument();
            }
            List<ElementId> cats = SharedParameterOperations.ResolveCategories(source, categories).Select(c => c.Id).ToList();
            FilteredElementCollector collector = string.IsNullOrWhiteSpace(view)
                ? new FilteredElementCollector(source)
                : new FilteredElementCollector(source, OpsCommon.ViewOrActive(doc, view).Id);
            List<Element> elements = collector.WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType().ToList();

            int missing = 0;
            var values = new List<string>();
            foreach (Element e in elements)
            {
                Parameter p = e.LookupParameter(parameter.Trim()) ?? source.GetElement(e.GetTypeId())?.LookupParameter(parameter.Trim());
                if (p == null && string.Equals(parameter.Trim(), "Комментарии", StringComparison.CurrentCultureIgnoreCase)) p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p == null) { missing++; continue; }
                values.Add(RevitText.ValueText(p)?.Trim() ?? "");
            }
            List<IGrouping<string, string>> groups = values.GroupBy(v => v).OrderByDescending(g => g.Count()).ToList();
            return new Dictionary<string, object>
            {
                ["elements"] = elements.Count,
                ["withoutParameter"] = missing,
                ["empty"] = values.Count(v => v.Length == 0),
                ["distinct"] = groups.Count(g => g.Key.Length > 0),
                ["values"] = groups.Where(g => g.Key.Length > 0).Take(100).Select(g => new Dictionary<string, object> { ["value"] = g.Key, ["count"] = g.Count() }).ToList(),
                ["truncated"] = groups.Count(g => g.Key.Length > 0) > 100,
            };
        }

        public static Dictionary<string, object> ViewExtents(Document doc, string viewValue)
        {
            View view = OpsCommon.ViewOrActive(doc, viewValue);
            var result = new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(view.Id),
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
                ["scale"] = view.Scale,
                ["detailLevel"] = view.DetailLevel.ToString(),
                ["displayStyle"] = view.DisplayStyle.ToString(),
                ["template"] = doc.GetElement(view.ViewTemplateId)?.Name,
                ["scopeBox"] = doc.GetElement(view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
                ["cropActive"] = view.CropBoxActive,
                ["cropVisible"] = view.CropBoxVisible,
                ["annotationCropActive"] = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.AsInteger() == 1,
            };
            if (ViewOperations.SupportsCrop(view))
            {
                BoundingBoxXYZ crop = view.CropBox;
                result["cropMinMm"] = OpsCommon.Point(crop.Transform.OfPoint(crop.Min));
                result["cropMaxMm"] = OpsCommon.Point(crop.Transform.OfPoint(crop.Max));
            }
            if (view is ViewPlan plan)
            {
                PlanViewRange range = plan.GetViewRange();
                result["viewRange"] = new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane }
                    .ToDictionary(p => p.ToString(), p =>
                    {
                        ElementId level = range.GetLevelId(p);
                        string levelName = level == PlanViewRange.Unlimited ? "без ограничения" : level == PlanViewRange.LevelAbove ? "уровень выше"
                            : level == PlanViewRange.LevelBelow ? "уровень ниже" : doc.GetElement(level)?.Name;
                        return (object)new Dictionary<string, object> { ["level"] = levelName, ["offsetMm"] = RevitText.ToMm(range.GetOffset(p)) };
                    });
                (double bottom, double cut, double top) z = LinkScope.ViewRangeZ(doc, plan);
                result["cutPlaneMm"] = RevitText.ToMm(z.cut);
            }
            if (view is View3D view3D)
            {
                result["sectionBoxActive"] = view3D.IsSectionBoxActive;
                if (view3D.IsSectionBoxActive)
                {
                    BoundingBoxXYZ box = view3D.GetSectionBox();
                    result["sectionBoxMinMm"] = OpsCommon.Point(box.Transform.OfPoint(box.Min));
                    result["sectionBoxMaxMm"] = OpsCommon.Point(box.Transform.OfPoint(box.Max));
                }
                result["perspective"] = view3D.IsPerspective;
            }

            List<string> hidden = doc.Settings.Categories.Cast<Category>()
                .Where(c => view.CanCategoryBeHidden(c.Id) && view.GetCategoryHidden(c.Id)).Select(c => c.Name).OrderBy(n => n).ToList();
            result["hiddenCategories"] = hidden.Count;
            result["hiddenCategoryNames"] = hidden.Take(60).ToList();
            result["links"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Select(l => new Dictionary<string, object> { ["name"] = l.Name, ["hiddenInView"] = l.IsHidden(view), ["loaded"] = l.GetLinkDocument() != null }).ToList();
            result["viewElements"] = new Dictionary<string, object>
            {
                ["textNotes"] = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).GetElementCount(),
                ["filledRegions"] = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).GetElementCount(),
                ["detailLines"] = new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>().Count(c => c.ViewSpecific),
                ["dimensions"] = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).GetElementCount(),
                ["tags"] = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).GetElementCount(),
            };
            result["temporaryHideIsolate"] = view.IsTemporaryHideIsolateActive();
            return result;
        }
    }
}
