using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class DimensionGridsRequest
    {
        public string View { get; set; }
        /// <summary>Несколько планов за один запуск (вместо или вместе с View).</summary>
        public List<string> Views { get; set; } = new List<string>();
        /// <summary>Имена осей; пусто — все оси, видимые на плане.</summary>
        public List<string> Grids { get; set; } = new List<string>();
        /// <summary>Связи, выбранные проектировщиком (часть имени). Другие связи не используются.</summary>
        public List<string> Links { get; set; } = new List<string>();
        /// <summary>Подтверждено: работать только с осями проекта, без связей.</summary>
        public bool HostOnly { get; set; }
        /// <summary>end — у верхних/правых концов осей, start — у нижних/левых, both — с обеих сторон.</summary>
        public string Side { get; set; } = "both";
        /// <summary>Отступ размерной линии от конца оси внутрь, мм на листе.</summary>
        public double OffsetMm { get; set; } = 10;
        /// <summary>Добавить габаритный размер между крайними осями (вторая линия).</summary>
        public bool Overall { get; set; } = true;
        public string DimensionType { get; set; }
        public bool DryRun { get; set; } = true;
    }

    internal sealed class ElementPick
    {
        public long Id { get; set; }
        /// <summary>Имя связи, если элемент в связанном файле.</summary>
        public string Link { get; set; }
    }

    internal sealed class DimensionElementsRequest
    {
        public string View { get; set; }
        /// <summary>Несколько планов за один запуск (вместо или вместе с View).</summary>
        public List<string> Views { get; set; } = new List<string>();
        /// <summary>Явный список элементов (в т. ч. из связей).</summary>
        public List<ElementPick> Elements { get; set; } = new List<ElementPick>();
        /// <summary>Или отбор по категориям (OST_* или имя) на виде.</summary>
        public List<string> Categories { get; set; } = new List<string>();
        public string NameContains { get; set; }
        /// <summary>Связи, выбранные проектировщиком (часть имени): в них ищутся стены и конструкции.</summary>
        public List<string> Links { get; set; } = new List<string>();
        /// <summary>Подтверждено: работать только с проектом, без связей.</summary>
        public bool HostOnly { get; set; }
        /// <summary>Брать сами образмериваемые элементы также из выбранных связей.</summary>
        public bool LinkElements { get; set; }

        /// <summary>Направление измерения на плане: x (горизонтально) или y (вертикально).</summary>
        public string Axis { get; set; } = "x";

        /// <summary>chain — цепочка через все элементы; to_walls — от каждого элемента до стены/конструкции.</summary>
        public string Mode { get; set; } = "chain";

        /// <summary>chain: добавить ближайшие стены по краям цепочки; to_walls: nearest или both.</summary>
        public string Walls { get; set; } = "nearest";

        /// <summary>Категории «конструкций», до граней которых ставится размер.</summary>
        public List<string> WallCategories { get; set; } = new List<string> { "OST_Walls", "OST_StructuralColumns", "OST_Columns" };

        /// <summary>Максимальное расстояние поиска стены, мм модели.</summary>
        public double MaxWallDistanceMm { get; set; } = 20000;

        /// <summary>Отступ размерной линии от элементов, мм на листе.</summary>
        public double OffsetMm { get; set; } = 8;
        public string DimensionType { get; set; }
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Размеры на планах: между осями, между семействами и до стен/конструкций, включая связанные файлы.</summary>
    internal static class DimensionOperations
    {
        private const double ParallelTolerance = 1e-3;

        // ===================== Оси =====================

        private sealed class GridLine
        {
            public string Name;
            public Line Line;          // в координатах проекта, на отметке плана
            public Reference Reference;
            public string Source;
        }

        public static Dictionary<string, object> DimensionGrids(Document doc, DimensionGridsRequest request)
        {
            List<ViewPlan> views = ResolvePlans(doc, request.View, request.Views);
            if (request.Links.Count == 0 && !request.HostOnly)
            {
                return LinkScope.LinksRequired(doc, string.Join(", ", views.Select(v => v.Name)));
            }
            DimensionType dimType = ResolveDimensionType(doc, request.DimensionType);
            return RunOnViews(doc, "AerBIM: размеры между осями", request.DryRun, views, view => GridsOnView(doc, view, request, dimType));
        }

        private static Dictionary<string, object> GridsOnView(Document doc, ViewPlan view, DimensionGridsRequest request, DimensionType dimType)
        {
            var warnings = new List<string>();
            double z = view.GenLevel.ProjectElevation;

            List<GridLine> grids = CollectGrids(doc, view, request, z, warnings);
            if (grids.Count < 2)
            {
                throw new InvalidOperationException("На плане меньше двух прямых осей для образмеривания." +
                    (request.Links.Count > 0 ? "" : " Если оси в связанном файле — укажите links.") +
                    $" Найдено: {(grids.Count == 0 ? "нет" : string.Join(", ", grids.Select(g => g.Name)))}." +
                    (warnings.Count > 0 ? " " + string.Join("; ", warnings.Distinct().Take(5)) : ""));
            }

            double offset = RevitText.ToFeet(request.OffsetMm) * view.Scale;
            var groups = new List<Dictionary<string, object>>();
            int created = 0;

            foreach (List<GridLine> family in GroupParallel(grids))
            {
                if (family.Count < 2)
                {
                    continue;
                }
                XYZ d = family[0].Line.Direction.Normalize();
                XYZ n = XYZ.BasisZ.CrossProduct(d).Normalize();
                List<GridLine> sorted = family.OrderBy(g => g.Line.Origin.DotProduct(n)).ToList();
                double across0 = sorted.First().Line.Origin.DotProduct(n);
                double across1 = sorted.Last().Line.Origin.DotProduct(n);
                double endMax = family.Max(g => Math.Max(g.Line.GetEndPoint(0).DotProduct(d), g.Line.GetEndPoint(1).DotProduct(d)));
                double startMin = family.Min(g => Math.Min(g.Line.GetEndPoint(0).DotProduct(d), g.Line.GetEndPoint(1).DotProduct(d)));

                var sides = new List<(string side, double along, double inward)>();
                if (request.Side != "start") sides.Add(("end", endMax, -1));
                if (request.Side != "end") sides.Add(("start", startMin, 1));

                var ids = new List<long>();
                foreach (var (side, along, inward) in sides)
                {
                    double chainAt = along + inward * offset;
                    Dimension chain = Create(doc, view, d, n, chainAt, across0, across1, z, sorted.Select(g => g.Reference), dimType, warnings);
                    if (chain != null) { ids.Add(RevitIds.IdOf(chain.Id)); created++; }

                    if (request.Overall && sorted.Count > 2)
                    {
                        double overallAt = along + inward * offset * 2;
                        Dimension overall = Create(doc, view, d, n, overallAt, across0, across1, z,
                            new[] { sorted.First().Reference, sorted.Last().Reference }, dimType, warnings);
                        if (overall != null) { ids.Add(RevitIds.IdOf(overall.Id)); created++; }
                    }
                }

                groups.Add(new Dictionary<string, object>
                {
                    ["grids"] = sorted.Select(g => g.Name).ToList(),
                    ["source"] = sorted.Select(g => g.Source).Distinct().ToList(),
                    ["spanMm"] = RevitText.ToMm(across1 - across0),
                    ["dimensionIds"] = request.DryRun ? new List<long>() : ids,
                });
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["view"] = view.Name,
                ["created"] = created,
                ["groups"] = groups,
                ["warnings"] = warnings,
                ["summary"] = Summary(request.DryRun, created),
            };
        }

        private static List<GridLine> CollectGrids(Document doc, ViewPlan view, DimensionGridsRequest request, double z, List<string> warnings)
        {
            var wanted = new HashSet<string>(request.Grids ?? new List<string>(), StringComparer.CurrentCultureIgnoreCase);
            var result = new List<GridLine>();

            foreach (Grid grid in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (wanted.Count > 0 && !wanted.Contains(grid.Name)) continue;
                Curve curve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault() ?? grid.Curve;
                if (curve is Line line)
                {
                    result.Add(new GridLine { Name = grid.Name, Line = Flat(line, z), Reference = new Reference(grid), Source = "проект" });
                }
                else
                {
                    warnings.Add($"ось «{grid.Name}» не прямая — пропущена");
                }
            }

            if (!request.HostOnly && request.Links.Count > 0)
            {
                List<RevitLinkInstance> links = LinkScope.ResolveLinks(doc, request.Links, warnings);

                var geometryOptions = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
                foreach (RevitLinkInstance link in links)
                {
                    Transform t = link.GetTotalTransform();
                    foreach (Grid grid in new FilteredElementCollector(link.GetLinkDocument()).OfClass(typeof(Grid)).Cast<Grid>())
                    {
                        if (wanted.Count > 0 && !wanted.Contains(grid.Name)) continue;
                        if (result.Any(r => string.Equals(r.Name, grid.Name, StringComparison.CurrentCultureIgnoreCase))) continue;
                        if (!(grid.Curve is Line line)) continue;

                        Line moved = Flat(Line.CreateBound(t.OfPoint(line.GetEndPoint(0)), t.OfPoint(line.GetEndPoint(1))), z);
                        Line clipped = ClipToCrop(view, moved);
                        if (clipped == null) continue; // ось вне подрезки вида

                        // Размер требует геометрическую ссылку: берём линию из геометрии оси.
                        Reference geometric = grid.get_Geometry(geometryOptions)?.OfType<Line>().FirstOrDefault()?.Reference;
                        if (geometric == null)
                        {
                            warnings.Add($"ось «{grid.Name}» из «{link.Name}»: нет геометрической ссылки — пропущена");
                            continue;
                        }
                        result.Add(new GridLine
                        {
                            Name = grid.Name,
                            Line = clipped,
                            Reference = geometric.CreateLinkReference(link),
                            Source = link.Name,
                        });
                    }
                }
            }

            if (wanted.Count > 0)
            {
                List<string> missing = wanted.Where(w => !result.Any(r => string.Equals(r.Name, w, StringComparison.CurrentCultureIgnoreCase))).ToList();
                if (missing.Count > 0)
                {
                    warnings.Add("оси не найдены на плане: " + string.Join(", ", missing));
                }
            }
            return result;
        }

        private static IEnumerable<List<GridLine>> GroupParallel(List<GridLine> grids)
        {
            var groups = new List<List<GridLine>>();
            foreach (GridLine grid in grids)
            {
                XYZ d = grid.Line.Direction.Normalize();
                List<GridLine> group = groups.FirstOrDefault(g => Math.Abs(Math.Abs(g[0].Line.Direction.Normalize().DotProduct(d)) - 1) < ParallelTolerance);
                if (group == null) groups.Add(new List<GridLine> { grid }); else group.Add(grid);
            }
            return groups;
        }

        // ===================== Элементы и стены =====================

        private sealed class Target
        {
            public ScopedElement Item;
            public XYZ Point;
            public Reference Reference;
        }

        public static Dictionary<string, object> DimensionElements(Document doc, DimensionElementsRequest request)
        {
            List<ViewPlan> views = ResolvePlans(doc, request.View, request.Views);
            string requestedMode = (request.Mode ?? "chain").Trim().ToLowerInvariant();
            string requestedWalls = (request.Walls ?? "nearest").Trim().ToLowerInvariant();
            bool needWalls = requestedMode == "to_walls" || requestedWalls == "ends";
            if (request.Links.Count == 0 && !request.HostOnly && (needWalls || request.LinkElements))
            {
                return LinkScope.LinksRequired(doc, string.Join(", ", views.Select(v => v.Name)));
            }

            var linkWarnings = new List<string>();
            List<RevitLinkInstance> selectedLinks = request.HostOnly || request.Links.Count == 0
                ? new List<RevitLinkInstance>()
                : LinkScope.ResolveLinks(doc, request.Links, linkWarnings);
            DimensionType dimType = ResolveDimensionType(doc, request.DimensionType);

            // Временный 3D-вид и настройка его видимости — один раз на весь запуск.
            View3D probe = null;
            WallFinder finder = null;
            return RunOnViews(doc, "AerBIM: размеры до элементов", request.DryRun, views,
                view => ElementsOnView(doc, view, request, dimType, finder, linkWarnings),
                () =>
                {
                    if (!needWalls) return;
                    probe = LinkScope.CreateProbe3D(doc);
                    List<ElementId> wallCats = request.WallCategories.Select(c => CategoryId(doc, c)).Where(c => c != null).ToList();
                    finder = new WallFinder(doc, probe, wallCats, selectedLinks);
                },
                () =>
                {
                    if (probe != null) doc.Delete(probe.Id);
                });
        }

        private static Dictionary<string, object> ElementsOnView(Document doc, ViewPlan view, DimensionElementsRequest request,
                                                                 DimensionType dimType, WallFinder intersector, List<string> linkWarnings)
        {
            var warnings = new List<string>(linkWarnings);
            string axis = (request.Axis ?? "x").Trim().ToLowerInvariant();
            if (axis != "x" && axis != "y")
            {
                throw new ArgumentException("axis: x или y.");
            }
            XYZ a = axis == "x" ? view.RightDirection.Normalize() : view.UpDirection.Normalize();   // вдоль размера
            XYZ b = axis == "x" ? view.UpDirection.Normalize() : view.RightDirection.Normalize();   // поперёк
            var (bottomZ, cutZ, topZ) = LinkScope.ViewRangeZ(doc, view);
            double z = view.GenLevel.ProjectElevation;
            double offset = RevitText.ToFeet(request.OffsetMm) * view.Scale;
            string mode = (request.Mode ?? "chain").Trim().ToLowerInvariant();
            string walls = (request.Walls ?? "nearest").Trim().ToLowerInvariant();

            List<ScopedElement> items = CollectElements(doc, view, request, bottomZ, topZ, warnings);
            if (items.Count == 0)
            {
                throw new InvalidOperationException(
                    $"На виде «{view.Name}» не найдено элементов категорий: {string.Join(", ", request.Categories)}" +
                    (request.LinkElements ? "." : " (в проекте; элементы из выбранных связей — linkElements=true)."));
            }
            var targets = new List<Target>();
            foreach (ScopedElement item in items)
            {
                Target target = CenterReference(item, a, warnings);
                if (target != null) targets.Add(target);
            }
            if (targets.Count == 0)
            {
                throw new InvalidOperationException("Нет элементов с опорной плоскостью центра, перпендикулярной направлению размера." +
                    (warnings.Count > 0 ? " " + string.Join("; ", warnings.Take(5)) : ""));
            }

            var created = new List<long>();
            var details = new List<Dictionary<string, object>>();
            double maxDistance = RevitText.ToFeet(request.MaxWallDistanceMm);

            if (mode == "chain")
            {
                List<Target> sorted = targets.OrderBy(t => t.Point.DotProduct(a)).ToList();
                var refs = sorted.Select(t => t.Reference).ToList();
                double from = sorted.First().Point.DotProduct(a);
                double to = sorted.Last().Point.DotProduct(a);
                if (walls == "ends" && intersector != null)
                {
                    var (startRef, startDist) = NearestFace(intersector, sorted.First().Point, cutZ, a.Negate(), maxDistance);
                    var (endRef, endDist) = NearestFace(intersector, sorted.Last().Point, cutZ, a, maxDistance);
                    if (startRef != null) { refs.Insert(0, startRef); from -= startDist; } else warnings.Add("слева/снизу от цепочки стена не найдена");
                    if (endRef != null) { refs.Add(endRef); to += endDist; } else warnings.Add("справа/сверху от цепочки стена не найдена");
                }
                if (refs.Count < 2)
                {
                    throw new InvalidOperationException("Для цепочки нужно минимум два элемента (или элемент и стена: walls=ends).");
                }
                double at = sorted.Max(t => t.Point.DotProduct(b)) + offset;
                Dimension dim = Create(doc, view, b, a, at, from, to, z, refs, dimType, warnings);
                if (dim != null) created.Add(RevitIds.IdOf(dim.Id));
                details.Add(new Dictionary<string, object> { ["mode"] = "chain", ["elements"] = sorted.Count, ["references"] = refs.Count });
            }
            else if (mode == "to_walls")
            {
                foreach (Target t in targets)
                {
                    var (minusRef, minusDist) = NearestFace(intersector, t.Point, cutZ, a.Negate(), maxDistance);
                    var (plusRef, plusDist) = NearestFace(intersector, t.Point, cutZ, a, maxDistance);
                    var refs = new List<Reference>();
                    double p = t.Point.DotProduct(a);
                    double from = p, to = p;
                    if (walls == "both")
                    {
                        if (minusRef != null) { refs.Add(minusRef); from = p - minusDist; }
                        refs.Add(t.Reference);
                        if (plusRef != null) { refs.Add(plusRef); to = p + plusDist; }
                    }
                    else
                    {
                        bool useMinus = minusRef != null && (plusRef == null || minusDist <= plusDist);
                        if (useMinus) { refs.Add(minusRef); refs.Add(t.Reference); from = p - minusDist; }
                        else if (plusRef != null) { refs.Add(t.Reference); refs.Add(plusRef); to = p + plusDist; }
                    }

                    var detail = new Dictionary<string, object> { ["element"] = t.Item.ToDictionary() };
                    if (refs.Count < 2)
                    {
                        detail["status"] = "no_wall";
                        warnings.Add($"«{t.Item.Element.Name}» (id {RevitIds.IdOf(t.Item.Element.Id)}): стена/конструкция в пределах {request.MaxWallDistanceMm} мм не найдена");
                    }
                    else
                    {
                        double at = t.Point.DotProduct(b) + offset;
                        Dimension dim = Create(doc, view, b, a, at, from, to, z, refs, dimType, warnings);
                        detail["status"] = dim == null ? "failed" : "created";
                        if (dim != null)
                        {
                            created.Add(RevitIds.IdOf(dim.Id));
                            detail["distanceMm"] = RevitText.ToMm(Math.Min(useMin(minusRef, minusDist), useMin(plusRef, plusDist)));
                        }
                    }
                    details.Add(detail);
                }
            }
            else
            {
                throw new ArgumentException("mode: chain или to_walls.");
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["view"] = view.Name,
                ["axis"] = axis,
                ["mode"] = mode,
                ["elementsFound"] = items.Count,
                ["elementsUsed"] = targets.Count,
                ["created"] = created.Count,
                ["dimensionIds"] = request.DryRun ? new List<long>() : created,
                ["details"] = details,
                ["warnings"] = warnings.Distinct().Take(50).ToList(),
                ["summary"] = Summary(request.DryRun, created.Count),
            };

            double useMin(Reference r, double d) => r == null ? double.MaxValue : d;
        }

        private static List<ScopedElement> CollectElements(Document doc, ViewPlan view, DimensionElementsRequest request,
                                                           double bottomZ, double topZ, List<string> warnings)
        {
            var result = new List<ScopedElement>();
            // Сами элементы берутся из выбранных связей только по явному linkElements=true.
            List<RevitLinkInstance> links = request.LinkElements && request.Links.Count > 0
                ? LinkScope.ResolveLinks(doc, request.Links, warnings)
                : new List<RevitLinkInstance>();

            if (request.Elements.Count > 0)
            {
                foreach (ElementPick pick in request.Elements)
                {
                    if (string.IsNullOrWhiteSpace(pick.Link))
                    {
                        Element e = doc.GetElement(RevitIds.ToElementId(pick.Id)) ?? throw new ArgumentException($"Элемент id {pick.Id} не найден в проекте.");
                        result.Add(new ScopedElement { Element = e });
                    }
                    else
                    {
                        RevitLinkInstance link = LinkScope.ResolveLinks(doc, new[] { pick.Link }, warnings).First();
                        Element e = link.GetLinkDocument().GetElement(RevitIds.ToElementId(pick.Id))
                            ?? throw new ArgumentException($"Элемент id {pick.Id} не найден в связи «{link.Name}».");
                        result.Add(new ScopedElement { Element = e, Link = link, Transform = link.GetTotalTransform() });
                    }
                }
                return result;
            }

            if (request.Categories.Count == 0)
            {
                throw new ArgumentException("Укажите elements (id, link) или categories.");
            }

            List<ElementId> hostCats = request.Categories.Select(c => CategoryId(doc, c)).Where(c => c != null).ToList();
            if (hostCats.Count == 0)
            {
                throw new ArgumentException("Категории не найдены: " + string.Join(", ", request.Categories));
            }
            var filter = new ElementMulticategoryFilter(hostCats);

            foreach (Element e in new FilteredElementCollector(doc, view.Id).WherePasses(filter).WhereElementIsNotElementType())
            {
                if (NameOk(e, request.NameContains)) result.Add(new ScopedElement { Element = e });
            }
            foreach (RevitLinkInstance link in links)
            {
                Transform t = link.GetTotalTransform();
                foreach (Element e in new FilteredElementCollector(link.GetLinkDocument()).WherePasses(filter).WhereElementIsNotElementType())
                {
                    XYZ p = LinkScope.LocationOf(e);
                    if (p == null || !NameOk(e, request.NameContains)) continue;
                    XYZ world = t.OfPoint(p);
                    if (world.Z < bottomZ - 1e-6 || world.Z > topZ + 1e-6 || !LinkScope.InsideCrop(view, world)) continue;
                    result.Add(new ScopedElement { Element = e, Link = link, Transform = t });
                }
            }
            return result;
        }

        private static bool NameOk(Element e, string contains) =>
            string.IsNullOrEmpty(contains) ||
            e.Name.IndexOf(contains, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            ((e as FamilyInstance)?.Symbol?.FamilyName ?? "").IndexOf(contains, StringComparison.CurrentCultureIgnoreCase) >= 0;

        /// <summary>Опорная плоскость центра семейства, перпендикулярная направлению размера.</summary>
        private static Target CenterReference(ScopedElement item, XYZ axis, List<string> warnings)
        {
            if (!(item.Element is FamilyInstance instance))
            {
                warnings.Add($"«{item.Element.Name}» (id {RevitIds.IdOf(item.Element.Id)}) — не семейство, пропущен");
                return null;
            }
            XYZ local = LinkScope.LocationOf(instance);
            if (local == null) return null;

            XYZ hand = item.Transform.OfVector(instance.HandOrientation).Normalize();
            XYZ facing = item.Transform.OfVector(instance.FacingOrientation).Normalize();
            FamilyInstanceReferenceType type;
            if (Math.Abs(Math.Abs(hand.DotProduct(axis)) - 1) < 0.01) type = FamilyInstanceReferenceType.CenterLeftRight;
            else if (Math.Abs(Math.Abs(facing.DotProduct(axis)) - 1) < 0.01) type = FamilyInstanceReferenceType.CenterFrontBack;
            else
            {
                warnings.Add($"«{instance.Name}» (id {RevitIds.IdOf(instance.Id)}) повёрнут не по осям вида — пропущен");
                return null;
            }

            Reference reference = instance.GetReferences(type).FirstOrDefault();
            if (reference == null)
            {
                warnings.Add($"в семействе «{instance.Symbol.FamilyName}» нет опорной плоскости {(type == FamilyInstanceReferenceType.CenterLeftRight ? "«Центр (лево/право)»" : "«Центр (передняя/задняя часть)»")}");
                return null;
            }
            if (item.IsLinked)
            {
                reference = reference.CreateLinkReference(item.Link);
            }
            return new Target { Item = item, Point = item.Transform.OfPoint(local), Reference = reference };
        }

        private static (Reference reference, double distance) NearestFace(WallFinder finder, XYZ point, double z, XYZ direction, double maxDistance) =>
            finder.Nearest(point, z, direction, maxDistance);

        /// <summary>
        /// Поиск ближайшей грани стены/конструкции лучом, включая связанные файлы.
        /// Фильтр ReferenceIntersector применяется к элементам текущего проекта, поэтому
        /// для связей он должен пропускать сам RevitLinkInstance, а категория найденного
        /// элемента проверяется уже в документе связи.
        /// </summary>
        private sealed class WallFinder
        {
            private readonly Document _doc;
            private readonly HashSet<long> _categories;
            private readonly ReferenceIntersector _intersector;
            private readonly HashSet<long> _links;

            public WallFinder(Document doc, View3D view, List<ElementId> categories, List<RevitLinkInstance> links)
            {
                _doc = doc;
                _links = new HashSet<long>(links.Select(l => RevitIds.IdOf(l.Id)));
                bool includeLinks = links.Count > 0;
                _categories = new HashSet<long>(categories.Select(RevitIds.IdOf));
                ElementFilter filter = new ElementMulticategoryFilter(categories);
                LimitProbeView(doc, view, categories, links);
                if (includeLinks)
                {
                    filter = new LogicalOrFilter(filter, new ElementClassFilter(typeof(RevitLinkInstance)));
                }
                _intersector = new ReferenceIntersector(filter, FindReferenceTarget.Face, view)
                {
                    FindReferencesInRevitLinks = includeLinks,
                };
            }

            /// <summary>
            /// Луч учитывает видимость 3D-вида: скрываем всё, кроме нужных категорий, и связи, не выбранные проектировщиком.
            /// Иначе он проверяет геометрию всех связей (в крупном проекте — минуты на элемент).
            /// </summary>
            private static void LimitProbeView(Document doc, View3D view, List<ElementId> categories, List<RevitLinkInstance> links)
            {
                var keep = new HashSet<long>(categories.Select(RevitIds.IdOf)) { (long)BuiltInCategory.OST_RvtLinks };
                foreach (Category category in doc.Settings.Categories)
                {
                    if (category.CategoryType == CategoryType.Model && !keep.Contains(RevitIds.IdOf(category.Id)) &&
                        view.CanCategoryBeHidden(category.Id))
                    {
                        view.SetCategoryHidden(category.Id, true);
                    }
                }

                var selected = new HashSet<long>(links.Select(l => RevitIds.IdOf(l.Id)));
                List<ElementId> others = LinkScope.LoadedLinks(doc)
                    .Where(l => !selected.Contains(RevitIds.IdOf(l.Id)) && l.CanBeHidden(view))
                    .Select(l => l.Id).ToList();
                if (others.Count > 0) view.HideElements(others);
            }

            public (Reference reference, double distance) Nearest(XYZ point, double z, XYZ direction, double maxDistance)
            {
                var origin = new XYZ(point.X, point.Y, z);
                foreach (ReferenceWithContext hit in _intersector.Find(origin, direction).OrderBy(h => h.Proximity))
                {
                    if (hit.Proximity < 1e-4) continue;
                    if (hit.Proximity > maxDistance) break;
                    Reference reference = hit.GetReference();
                    if (IsWall(reference))
                    {
                        return (reference, hit.Proximity);
                    }
                }
                return (null, 0);
            }

            private bool IsWall(Reference reference)
            {
                Element element;
                if (reference.LinkedElementId != ElementId.InvalidElementId)
                {
                    if (!_links.Contains(RevitIds.IdOf(reference.ElementId))) return false;
                    var link = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                    element = link?.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                }
                else
                {
                    element = _doc.GetElement(reference.ElementId);
                }
                return element?.Category != null && _categories.Contains(RevitIds.IdOf(element.Category.Id));
            }
        }

        // ===================== Общие =====================

        /// <summary>Размер вдоль along: линия на координате lineAt по оси across, от from до to по оси along.</summary>
        private static Dimension Create(Document doc, View view, XYZ across, XYZ along, double lineAt, double from, double to, double z,
                                        IEnumerable<Reference> references, DimensionType type, List<string> warnings)
        {
            var array = new ReferenceArray();
            foreach (Reference r in references) array.Append(r);

            double pad = RevitText.ToFeet(100);
            XYZ p0 = across * lineAt + along * (from - pad);
            XYZ p1 = across * lineAt + along * (to + pad);
            Line line = Line.CreateBound(new XYZ(p0.X, p0.Y, z), new XYZ(p1.X, p1.Y, z));
            try
            {
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    Dimension dim = type == null
                        ? doc.Create.NewDimension(view, line, array)
                        : doc.Create.NewDimension(view, line, array, type);
                    sub.Commit();
                    return dim;
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Revit не создал размер: " + ex.Message);
                return null;
            }
        }

        /// <summary>Часть отрезка внутри подрезки вида (Лианг–Барски), или null. Без подрезки — отрезок как есть.</summary>
        private static Line ClipToCrop(View view, Line line)
        {
            if (!view.CropBoxActive)
            {
                return line;
            }
            BoundingBoxXYZ crop = view.CropBox;
            Transform toLocal = crop.Transform.Inverse;
            XYZ a = toLocal.OfPoint(line.GetEndPoint(0));
            XYZ b = toLocal.OfPoint(line.GetEndPoint(1));
            double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { a.X - crop.Min.X, crop.Max.X - a.X, a.Y - crop.Min.Y, crop.Max.Y - a.Y };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    if (q[i] < 0) return null;
                    continue;
                }
                double r = q[i] / p[i];
                if (p[i] < 0) t0 = Math.Max(t0, r); else t1 = Math.Min(t1, r);
                if (t0 > t1) return null;
            }
            XYZ s = line.Evaluate(t0, true);
            XYZ e = line.Evaluate(t1, true);
            return s.DistanceTo(e) < 1e-3 ? null : Line.CreateBound(s, e);
        }

        private static Line Flat(Line line, double z)
        {
            XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
            return Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
        }

        public static DimensionType ResolveDimensionType(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                       .FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                                            t.StyleType == DimensionStyleType.Linear)
                   ?? throw new ArgumentException($"Линейный тип размера «{name}» не найден. Список: list_dimension_types.");
        }

        public static List<Dictionary<string, object>> ListDimensionTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.Linear && !string.IsNullOrEmpty(t.Name))
                .OrderBy(t => t.Name)
                .Select(t => new Dictionary<string, object> { ["id"] = RevitIds.IdOf(t.Id), ["name"] = t.Name })
                .ToList();

        internal static ElementId CategoryId(Document doc, string value)
        {
            string v = (value ?? string.Empty).Trim();
            if (v.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(v, true, out BuiltInCategory bic))
            {
                return new ElementId(bic);
            }
            Category category = doc.Settings.Categories.Cast<Category>()
                .FirstOrDefault(c => string.Equals(c.Name, v, StringComparison.CurrentCultureIgnoreCase));
            return category?.Id;
        }

        /// <summary>Планы по id или имени: все проверяются до начала работы, повторы убираются.</summary>
        private static List<ViewPlan> ResolvePlans(Document doc, string single, List<string> many)
        {
            var names = new List<string>(many ?? new List<string>());
            if (!string.IsNullOrWhiteSpace(single)) names.Insert(0, single);
            if (names.Count == 0)
            {
                throw new ArgumentException("Укажите view или views (id или имена планов). Список: list_views.");
            }
            return names.Select(n => LinkScope.RequirePlan(ViewOperations.ResolveView(doc, n, ViewType.FloorPlan)))
                .GroupBy(p => RevitIds.IdOf(p.Id)).Select(g => g.First()).ToList();
        }

        /// <summary>
        /// Выполняет работу по планам в одной транзакции. При нескольких планах сбой одного не отменяет остальные
        /// (подтранзакция на план); при одном плане ошибка возвращается как обычно. В ответе — время работы, мс.
        /// </summary>
        private static Dictionary<string, object> RunOnViews(Document doc, string title, bool dryRun, List<ViewPlan> views,
                                                             Func<ViewPlan, Dictionary<string, object>> work,
                                                             Action start = null, Action end = null)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            var results = new List<Dictionary<string, object>>();
            int created = 0;
            long setupMs;
            using (var transaction = new Transaction(doc, title))
            {
                transaction.Start();
                start?.Invoke();
                setupMs = total.ElapsedMilliseconds;
                foreach (ViewPlan view in views)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    Dictionary<string, object> result;
                    try
                    {
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            result = work(view);
                            sub.Commit();
                        }
                        result["status"] = "ok";
                    }
                    catch (Exception ex) when (views.Count > 1)
                    {
                        result = new Dictionary<string, object> { ["view"] = view.Name, ["status"] = "failed", ["created"] = 0, ["error"] = ex.Message };
                    }
                    result["elapsedMs"] = timer.ElapsedMilliseconds;
                    created += (int)result["created"];
                    results.Add(result);
                }
                end?.Invoke();
                Finish(transaction, dryRun, created);
            }

            if (views.Count == 1)
            {
                results[0]["setupMs"] = setupMs;
                results[0]["totalElapsedMs"] = total.ElapsedMilliseconds;
                return results[0];
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["views"] = views.Count,
                ["failed"] = results.Count(r => (string)r["status"] == "failed"),
                ["created"] = created,
                ["setupMs"] = setupMs,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["results"] = results,
                ["summary"] = Summary(dryRun, created),
            };
        }

        private static void Finish(Transaction transaction, bool dryRun, int created)
        {
            if (dryRun || created == 0)
            {
                transaction.RollBack();
            }
            else if (transaction.Commit() != TransactionStatus.Committed)
            {
                throw new InvalidOperationException("Revit не применил изменения.");
            }
        }

        private static string Summary(bool dryRun, int created) =>
            (dryRun ? $"Предпросмотр: будет создано размеров: {created}. Для создания повторите с dryRun=false."
                    : $"Создано размеров: {created}. Отменить можно в Revit через Ctrl+Z.");
    }
}
