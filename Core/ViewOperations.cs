using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class CropViewRequest
    {
        public string View { get; set; }

        /// <summary>model — по элементам модели на виде; grids — по пересечениям осей; rectangle — по координатам; scopeBox — область видимости; off — выключить подрезку.</summary>
        public string Mode { get; set; } = "model";

        /// <summary>Отступ от найденных границ, мм модели.</summary>
        public double OffsetMm { get; set; } = 1000;

        public List<string> Grids { get; set; } = new List<string>();

        /// <summary>rectangle: minX, minY, maxX, maxY в мм модели (внутренняя система координат проекта).</summary>
        public double[] RectangleMm { get; set; }

        public string ScopeBox { get; set; }

        /// <summary>model: учитывать связанные модели (RVT-связи).</summary>
        public bool IncludeLinks { get; set; } = true;

        /// <summary>Включить подрезку аннотаций с отступом (мм на листе); null — не менять.</summary>
        public double? AnnotationOffsetMm { get; set; }

        public bool DryRun { get; set; } = true;
    }

    /// <summary>Виды: поиск, список, подрезка («Обрезать вид»). Без UI.</summary>
    internal static class ViewOperations
    {
        private static readonly BuiltInCategory[] IgnoredCategories =
        {
            BuiltInCategory.OST_Topography, BuiltInCategory.OST_Site, BuiltInCategory.OST_SiteProperty,
            BuiltInCategory.OST_Cameras, BuiltInCategory.OST_Elev, BuiltInCategory.OST_Sections,
            BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_VolumeOfInterest,
            BuiltInCategory.OST_PointClouds, BuiltInCategory.OST_RasterImages,
        };

        public static List<View> ListViews(Document doc, string viewType)
        {
            IEnumerable<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet && v.ViewType != ViewType.ProjectBrowser &&
                            v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined);
            if (!string.IsNullOrWhiteSpace(viewType) && !string.Equals(viewType, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse(viewType, true, out ViewType type))
                {
                    throw new ArgumentException($"Тип вида «{viewType}» неизвестен. Примеры: FloorPlan, CeilingPlan, EngineeringPlan, AreaPlan, Section, all.");
                }
                views = views.Where(v => v.ViewType == type);
            }
            // Спецификации изменений штампа и спецификации примечаний — служебные, их не размещают вручную.
            views = views.Where(v => !(v is ViewSchedule s) || (!s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule));
            return views.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Листы, на которых размещён вид (видовой экран) или спецификация.
        /// Легенды и спецификации могут стоять на нескольких листах.
        /// </summary>
        public static List<ViewSheet> SheetsOf(Document doc, View view)
        {
            IEnumerable<ElementId> sheetIds = view is ViewSchedule
                ? new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                    .Where(s => s.ScheduleId == view.Id).Select(s => s.OwnerViewId)
                : new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                    .Where(vp => vp.ViewId == view.Id).Select(vp => vp.SheetId);
            return sheetIds.Distinct().Select(id => doc.GetElement(id) as ViewSheet).Where(s => s != null)
                .OrderBy(s => s.SheetNumber, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Можно ли один и тот же элемент ставить на несколько листов.</summary>
        public static bool AllowsManySheets(View view) => view.ViewType == ViewType.Legend || view is ViewSchedule;

        public static Dictionary<string, object> Describe(Document doc, View view)
        {
            List<ViewSheet> sheets = SheetsOf(doc, view);
            Element scopeBox = ScopeBoxOf(doc, view);
            bool crop = SupportsCrop(view);
            return new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(view.Id),
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
                ["level"] = (view as ViewPlan)?.GenLevel?.Name,
                ["scale"] = view is ViewSchedule ? (int?)null : view.Scale,
                ["cropActive"] = crop ? view.CropBoxActive : (bool?)null,
                ["cropSizeMm"] = crop && view.CropBoxActive ? CropSizeModelMm(view) : null,
                ["scopeBox"] = scopeBox?.Name,
                ["template"] = view.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(view.ViewTemplateId)?.Name,
                ["sheet"] = sheets.Count == 0 ? null : $"{sheets[0].SheetNumber} - {sheets[0].Name}",
                ["sheets"] = sheets.Select(s => $"{s.SheetNumber} - {s.Name}").ToList(),
                ["multipleSheetsAllowed"] = AllowsManySheets(view),
            };
        }

        /// <summary>У вида есть параметр «Обрезать вид».</summary>
        public static bool SupportsCrop(View view) => view.get_Parameter(BuiltInParameter.VIEWER_CROP_REGION) != null;

        /// <summary>
        /// Вид по id или имени. prefer — тип вида, который выбирается при совпадении имён
        /// (например, план этажа и план потолка «Уровень 1»).
        /// </summary>
        public static View ResolveView(Document doc, string value, ViewType? prefer = null)
        {
            string wanted = (value ?? string.Empty).Trim();
            if (wanted.Length == 0)
            {
                throw new ArgumentException("Укажите вид (id или имя). Список: list_views.");
            }
            if (long.TryParse(wanted, out long id))
            {
                var byId = doc.GetElement(RevitIds.ToElementId(id)) as View;
                if (byId == null || byId.IsTemplate)
                {
                    throw new ArgumentException($"Вид с id {id} не найден.");
                }
                return byId;
            }
            List<View> matches = ListViews(doc, "all").Where(v => string.Equals(v.Name, wanted, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
            if (prefer != null && matches.Count(m => m.ViewType == prefer.Value) == 1)
            {
                return matches.First(m => m.ViewType == prefer.Value);
            }
            throw new ArgumentException(matches.Count == 0
                ? $"Вид «{wanted}» не найден. Список: list_views."
                : $"Несколько видов «{wanted}»: {string.Join(", ", matches.Select(m => $"{m.ViewType} id {RevitIds.IdOf(m.Id)}"))}. Укажите id.");
        }

        private static Element ScopeBoxOf(Document doc, View view)
        {
            ElementId id = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId();
            return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id);
        }

        private static Dictionary<string, object> CropSizeModelMm(View view)
        {
            BoundingBoxXYZ box = view.CropBox;
            return RevitText.Size(RevitText.ToMm(box.Max.X - box.Min.X), RevitText.ToMm(box.Max.Y - box.Min.Y));
        }

        // ---------- Подрезка ----------

        public static Dictionary<string, object> Crop(Document doc, CropViewRequest request)
        {
            View view = ResolveView(doc, request.View, ViewType.FloorPlan);
            if (!SupportsCrop(view))
            {
                throw new InvalidOperationException($"У вида «{view.Name}» нет области подрезки.");
            }
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }

            string mode = (request.Mode ?? "model").Trim().ToLowerInvariant();
            var warnings = new List<string>();
            Dictionary<string, object> source;

            using (var transaction = new Transaction(doc, $"AerBIM: обрезать вид «{view.Name}»"))
            {
                transaction.Start();

                if (view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    warnings.Add("к виду применён шаблон: если шаблон управляет подрезкой, изменения могут не сохраниться");
                }

                if (mode == "off")
                {
                    view.CropBoxActive = false;
                    source = new Dictionary<string, object> { ["mode"] = "off" };
                }
                else if (mode == "scopebox")
                {
                    Element scopeBox = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                        .WhereElementIsNotElementType()
                        .FirstOrDefault(e => string.Equals(e.Name, request.ScopeBox?.Trim(), StringComparison.CurrentCultureIgnoreCase))
                        ?? throw new ArgumentException($"Область видимости «{request.ScopeBox}» не найдена.");
                    view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP).Set(scopeBox.Id);
                    view.CropBoxActive = true;
                    source = new Dictionary<string, object> { ["mode"] = "scopeBox", ["scopeBox"] = scopeBox.Name };
                }
                else
                {
                    Element currentScope = ScopeBoxOf(doc, view);
                    if (currentScope != null)
                    {
                        view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP).Set(ElementId.InvalidElementId);
                        warnings.Add($"снята область видимости «{currentScope.Name}», которая управляла подрезкой");
                    }

                    ViewCropRegionShapeManager shapes = view.GetCropRegionShapeManager();
                    if (shapes.ShapeSet)
                    {
                        shapes.RemoveCropRegionShape();
                        warnings.Add("непрямоугольная подрезка заменена прямоугольной");
                    }

                    List<XYZ> points;
                    switch (mode)
                    {
                        case "model":
                            points = ModelPoints(doc, view, request.IncludeLinks, out source);
                            break;
                        case "grids":
                            points = GridPoints(doc, request.Grids, out source);
                            break;
                        case "rectangle":
                            points = RectanglePoints(request.RectangleMm, out source);
                            break;
                        default:
                            throw new ArgumentException("mode: model, grids, rectangle, scopeBox или off.");
                    }
                    ApplyCrop(view, points, request.OffsetMm);
                    source["offsetMm"] = request.OffsetMm;
                }

                if (request.AnnotationOffsetMm != null)
                {
                    SetAnnotationCrop(view, request.AnnotationOffsetMm.Value, warnings);
                }

                doc.Regenerate();
                var result = new Dictionary<string, object>
                {
                    ["dryRun"] = request.DryRun,
                    ["view"] = view.Name,
                    ["viewId"] = RevitIds.IdOf(view.Id),
                    ["scale"] = view.Scale,
                    ["source"] = source,
                    ["cropActive"] = view.CropBoxActive,
                    ["cropSizeModelMm"] = view.CropBoxActive ? CropSizeModelMm(view) : null,
                    ["sizeOnSheetMm"] = view.CropBoxActive ? SizeOnSheet(view) : null,
                    ["warnings"] = warnings,
                };

                if (request.DryRun)
                {
                    transaction.RollBack();
                    result["summary"] = "Предпросмотр: подрезка не применена. Для применения повторите с dryRun=false.";
                }
                else
                {
                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException("Revit не применил подрезку.");
                    }
                    result["summary"] = "Подрезка применена. Отменить можно в Revit через Ctrl+Z.";
                }
                return result;
            }
        }

        /// <summary>Размер области подрезки на листе при масштабе вида (без учёта подписи вида).</summary>
        public static Dictionary<string, object> SizeOnSheet(View view)
        {
            BoundingBoxXYZ box = view.CropBox;
            double scale = Math.Max(1, view.Scale);
            return RevitText.Size(RevitText.ToMm(box.Max.X - box.Min.X) / scale, RevitText.ToMm(box.Max.Y - box.Min.Y) / scale);
        }

        private static void ApplyCrop(View view, List<XYZ> modelPoints, double offsetMm)
        {
            if (modelPoints.Count == 0)
            {
                throw new InvalidOperationException("Не найдено границ для подрезки.");
            }

            BoundingBoxXYZ crop = view.CropBox;
            Transform toCrop = crop.Transform.Inverse;
            List<XYZ> local = modelPoints.Select(p => toCrop.OfPoint(p)).ToList();
            double offset = RevitText.ToFeet(offsetMm);

            crop.Min = new XYZ(local.Min(p => p.X) - offset, local.Min(p => p.Y) - offset, crop.Min.Z);
            crop.Max = new XYZ(local.Max(p => p.X) + offset, local.Max(p => p.Y) + offset, crop.Max.Z);
            view.CropBoxActive = true;
            view.CropBox = crop;
        }

        private static List<XYZ> ModelPoints(Document doc, View view, bool includeLinks, out Dictionary<string, object> source)
        {
            var points = new List<XYZ>();
            int elements = 0;
            int links = 0;
            foreach (Element element in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                Category category = element.Category;
                if (category == null || category.CategoryType != CategoryType.Model)
                {
                    continue;
                }
                if (IgnoredCategories.Any(c => RevitIds.IdOf(category.Id) == (int)c))
                {
                    continue;
                }
                bool isLink = element is RevitLinkInstance;
                if (isLink && !includeLinks)
                {
                    continue;
                }

                BoundingBoxXYZ box = element.get_BoundingBox(view);
                if (box == null)
                {
                    continue;
                }
                points.AddRange(Corners(box));
                if (isLink) links++; else elements++;
            }
            source = new Dictionary<string, object> { ["mode"] = "model", ["elements"] = elements, ["links"] = links };
            if (points.Count == 0)
            {
                throw new InvalidOperationException("На виде нет элементов модели, по которым можно обрезать вид.");
            }
            return points;
        }

        private static List<XYZ> GridPoints(Document doc, List<string> names, out Dictionary<string, object> source)
        {
            if (names == null || names.Count < 2)
            {
                throw new ArgumentException("Для mode=grids укажите минимум две оси (grids), например [\"1\",\"8\",\"А\",\"Е\"].");
            }

            List<Grid> all = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            var grids = new List<Grid>();
            var missing = new List<string>();
            foreach (string name in names)
            {
                Grid grid = all.FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (grid == null) missing.Add(name); else grids.Add(grid);
            }
            if (missing.Count > 0)
            {
                throw new ArgumentException($"Оси не найдены: {string.Join(", ", missing)}. Есть: {string.Join(", ", all.Select(g => g.Name).OrderBy(n => n))}.");
            }

            var points = new List<XYZ>();
            for (int i = 0; i < grids.Count; i++)
            {
                for (int j = i + 1; j < grids.Count; j++)
                {
                    Curve a = Flatten(grids[i].Curve);
                    Curve b = Flatten(grids[j].Curve);
                    a.MakeUnbound();
                    b.MakeUnbound();
                    if (a.Intersect(b, out IntersectionResultArray hits) == SetComparisonResult.Overlap && hits != null)
                    {
                        foreach (IntersectionResult hit in hits)
                        {
                            points.Add(hit.XYZPoint);
                        }
                    }
                }
            }
            if (points.Count == 0)
            {
                // Все оси параллельны: берём их концы.
                foreach (Grid grid in grids)
                {
                    points.Add(grid.Curve.GetEndPoint(0));
                    points.Add(grid.Curve.GetEndPoint(1));
                }
            }
            source = new Dictionary<string, object> { ["mode"] = "grids", ["grids"] = grids.Select(g => g.Name).ToList(), ["intersections"] = points.Count };
            return points;
        }

        private static Curve Flatten(Curve curve)
        {
            if (curve is Line line)
            {
                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);
                return Line.CreateBound(new XYZ(p0.X, p0.Y, 0), new XYZ(p1.X, p1.Y, 0));
            }
            // Дуговые оси: используем как есть (оси одного проекта лежат в одной плоскости).
            return curve.Clone();
        }

        private static List<XYZ> RectanglePoints(double[] rectangleMm, out Dictionary<string, object> source)
        {
            if (rectangleMm == null || rectangleMm.Length != 4)
            {
                throw new ArgumentException("Для mode=rectangle укажите rectangleMm: [minX, minY, maxX, maxY] в мм.");
            }
            source = new Dictionary<string, object> { ["mode"] = "rectangle", ["rectangleMm"] = rectangleMm };
            return new List<XYZ>
            {
                new XYZ(RevitText.ToFeet(rectangleMm[0]), RevitText.ToFeet(rectangleMm[1]), 0),
                new XYZ(RevitText.ToFeet(rectangleMm[2]), RevitText.ToFeet(rectangleMm[3]), 0),
            };
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            Transform t = box.Transform ?? Transform.Identity;
            foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
            {
                yield return t.OfPoint(new XYZ(x, y, box.Min.Z));
            }
        }

        private static void SetAnnotationCrop(View view, double offsetOnSheetMm, List<string> warnings)
        {
            Parameter active = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
            if (active == null || active.IsReadOnly)
            {
                warnings.Add("подрезка аннотаций недоступна для этого вида");
                return;
            }
            active.Set(1);
            ViewCropRegionShapeManager shapes = view.GetCropRegionShapeManager();
            double offset = RevitText.ToFeet(offsetOnSheetMm);
            try
            {
                shapes.LeftAnnotationCropOffset = offset;
                shapes.RightAnnotationCropOffset = offset;
                shapes.TopAnnotationCropOffset = offset;
                shapes.BottomAnnotationCropOffset = offset;
            }
            catch (Exception ex)
            {
                warnings.Add("отступ подрезки аннотаций не задан: " + ex.Message);
            }
        }
    }
}
