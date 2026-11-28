using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class FilterRuleSpec
    {
        public string Parameter { get; set; }
        /// <summary>equals, not_equals, contains, not_contains, begins_with, ends_with, greater, greater_or_equal, less, less_or_equal, has_value, has_no_value.</summary>
        public string Operator { get; set; } = "equals";
        public string Value { get; set; }
    }

    internal sealed class CreateFilterRequest
    {
        public string Name { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public List<FilterRuleSpec> Rules { get; set; } = new List<FilterRuleSpec>();
        /// <summary>and — все правила; or — любое.</summary>
        public string Logic { get; set; } = "and";
        public bool OverwriteExisting { get; set; }
        public bool DryRun { get; set; } = true;
    }

    internal sealed class GraphicsOverrides
    {
        public string LineColor { get; set; }
        public int? LineWeight { get; set; }
        public string LinePattern { get; set; }
        public string SurfaceColor { get; set; }
        public string SurfacePattern { get; set; }
        public string CutLineColor { get; set; }
        public int? CutLineWeight { get; set; }
        public string CutColor { get; set; }
        public string CutPattern { get; set; }
        public int? Transparency { get; set; }
        public bool? Halftone { get; set; }
    }

    internal sealed class SetOverridesRequest
    {
        /// <summary>Вид или шаблон вида: id или имя.</summary>
        public string View { get; set; }
        /// <summary>Фильтр (добавляется на вид, если его нет) — или Category.</summary>
        public string Filter { get; set; }
        public string Category { get; set; }
        public bool? Visible { get; set; }
        public GraphicsOverrides Overrides { get; set; }
        /// <summary>Сбросить переопределения (до применения новых).</summary>
        public bool Reset { get; set; }
        public bool DryRun { get; set; } = true;
    }

    internal sealed class ApplyTemplateRequest
    {
        /// <summary>Шаблон: имя или id; «none» — снять шаблон.</summary>
        public string Template { get; set; }
        public List<string> Views { get; set; } = new List<string>();
        public string ViewType { get; set; }
        public string NameContains { get; set; }
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Фильтры графики, переопределения и шаблоны видов. Работает и с видами, и с шаблонами.</summary>
    internal static class ViewGraphicsOperations
    {
        // ===================== Поиск =====================

        public static View ResolveViewOrTemplate(Document doc, string value)
        {
            string wanted = (value ?? string.Empty).Trim();
            if (wanted.Length == 0)
            {
                throw new ArgumentException("Укажите вид или шаблон вида (id или имя).");
            }
            if (long.TryParse(wanted, out long id))
            {
                return doc.GetElement(RevitIds.ToElementId(id)) as View ?? throw new ArgumentException($"Вид/шаблон с id {id} не найден.");
            }
            List<View> templates = Templates(doc).Where(t => string.Equals(t.Name, wanted, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (templates.Count == 1)
            {
                return templates[0];
            }
            if (templates.Count > 1)
            {
                throw new ArgumentException($"Несколько шаблонов «{wanted}» — укажите id: {string.Join(", ", templates.Select(t => RevitIds.IdOf(t.Id)))}.");
            }
            return ViewOperations.ResolveView(doc, wanted);
        }

        public static List<View> Templates(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate)
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        public static ParameterFilterElement ResolveFilter(Document doc, string value)
        {
            string wanted = (value ?? string.Empty).Trim();
            List<ParameterFilterElement> all = Filters(doc);
            ParameterFilterElement found = long.TryParse(wanted, out long id)
                ? all.FirstOrDefault(f => RevitIds.IdOf(f.Id) == id)
                : all.FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.CurrentCultureIgnoreCase));
            return found ?? throw new ArgumentException($"Фильтр «{wanted}» не найден. Список: list_view_filters.");
        }

        private static List<ParameterFilterElement> Filters(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        // ===================== Списки =====================

        public static List<Dictionary<string, object>> ListFilters(Document doc)
        {
            List<View> allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.AreGraphicsOverridesAllowed()).ToList();
            var usage = new Dictionary<ElementId, List<string>>();
            foreach (View v in allViews)
            {
                ICollection<ElementId> filters;
                try { filters = v.GetFilters(); } catch { continue; }
                foreach (ElementId f in filters)
                {
                    if (!usage.TryGetValue(f, out List<string> list)) usage[f] = list = new List<string>();
                    list.Add((v.IsTemplate ? "шаблон: " : "") + v.Name);
                }
            }

            return Filters(doc).Select(f => new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(f.Id),
                ["name"] = f.Name,
                ["categories"] = f.GetCategories().Select(c => Category.GetCategory(doc, c)?.Name).Where(n => n != null).OrderBy(n => n).ToList(),
                ["usedIn"] = usage.TryGetValue(f.Id, out List<string> used) ? used.OrderBy(n => n).ToList() : new List<string>(),
            }).ToList();
        }

        public static List<Dictionary<string, object>> ListTemplates(Document doc)
        {
            List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            return Templates(doc).Select(t => new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(t.Id),
                ["name"] = t.Name,
                ["viewType"] = t.ViewType.ToString(),
                ["scale"] = t.Scale,
                ["filters"] = SafeFilters(t).Select(f => new Dictionary<string, object>
                {
                    ["name"] = doc.GetElement(f)?.Name,
                    ["visible"] = t.GetFilterVisibility(f),
                }).ToList(),
                ["controlsFilters"] = ControlsFilters(t),
                ["usedByViews"] = views.Count(v => v.ViewTemplateId == t.Id),
            }).ToList();
        }

        private static ICollection<ElementId> SafeFilters(View v)
        {
            try { return v.GetFilters(); } catch { return new List<ElementId>(); }
        }

        /// <summary>Шаблон управляет вкладкой «Фильтры» (V/G Overrides Filters).</summary>
        private static bool ControlsFilters(View template)
        {
            ElementId filtersParam = new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS);
            return !template.GetNonControlledTemplateParameterIds().Contains(filtersParam);
        }

        public static Dictionary<string, object> FilterableParameters(Document doc, List<string> categories, string contains)
        {
            List<ElementId> catIds = ResolveCategories(doc, categories);
            Element sample = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(catIds))
                .WhereElementIsNotElementType().FirstElement();
            var rows = new List<Dictionary<string, object>>();
            foreach (ElementId pid in ParameterFilterUtilities.GetFilterableParametersInCommon(doc, catIds))
            {
                string name = ParameterName(doc, pid);
                if (string.IsNullOrEmpty(name)) continue;
                if (!string.IsNullOrEmpty(contains) && name.IndexOf(contains, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                Parameter p = sample == null ? null : FindParameter(sample, pid);
                rows.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["id"] = RevitIds.IdOf(pid),
                    ["storage"] = (p?.StorageType ?? StorageOf(doc, pid)).ToString(),
                });
            }
            return new Dictionary<string, object>
            {
                ["categories"] = catIds.Select(c => Category.GetCategory(doc, c)?.Name).ToList(),
                ["count"] = rows.Count,
                ["parameters"] = rows.OrderBy(r => (string)r["name"]).ToList(),
                ["note"] = sample == null ? "в проекте нет элементов этих категорий: тип значения определён по параметру, числа в правилах будут приняты в миллиметрах" : null,
            };
        }

        // ===================== Создание фильтра =====================

        public static Dictionary<string, object> CreateFilter(Document doc, CreateFilterRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Укажите имя фильтра (name).");
            char[] forbidden = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };
            if (request.Name.IndexOfAny(forbidden) >= 0)
            {
                throw new ArgumentException("Имя фильтра содержит запрещённые Revit символы: \\ : { } [ ] | ; < > ? ` ~");
            }
            List<ElementId> catIds = ResolveCategories(doc, request.Categories);
            ICollection<ElementId> filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, catIds);
            Element sample = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(catIds))
                .WhereElementIsNotElementType().FirstElement();

            var descriptions = new List<string>();
            var parameterFilters = new List<ElementFilter>();
            foreach (FilterRuleSpec spec in request.Rules)
            {
                parameterFilters.Add(new ElementParameterFilter(BuildRule(doc, filterable, sample, spec, out string description)));
                descriptions.Add(description);
            }

            ElementFilter elementFilter = parameterFilters.Count == 0
                ? null
                : parameterFilters.Count == 1
                    ? parameterFilters[0]
                    : string.Equals(request.Logic, "or", StringComparison.OrdinalIgnoreCase)
                        ? (ElementFilter)new LogicalOrFilter(parameterFilters)
                        : new LogicalAndFilter(parameterFilters);

            ParameterFilterElement existing = Filters(doc).FirstOrDefault(f => string.Equals(f.Name, request.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));
            if (existing != null && !request.OverwriteExisting)
            {
                throw new InvalidOperationException($"Фильтр «{existing.Name}» уже есть (id {RevitIds.IdOf(existing.Id)}). overwriteExisting=true — обновить его.");
            }

            int matched;
            long? id = null;
            using (var transaction = new Transaction(doc, $"AerBIM: фильтр «{request.Name.Trim()}»"))
            {
                transaction.Start();
                ParameterFilterElement filter;
                if (existing != null)
                {
                    existing.SetCategories(catIds);
                    if (elementFilter != null) existing.SetElementFilter(elementFilter); else existing.ClearRules();
                    filter = existing;
                }
                else
                {
                    filter = elementFilter == null
                        ? ParameterFilterElement.Create(doc, request.Name.Trim(), catIds)
                        : ParameterFilterElement.Create(doc, request.Name.Trim(), catIds, elementFilter);
                }
                id = RevitIds.IdOf(filter.Id);

                var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(catIds)).WhereElementIsNotElementType();
                matched = (elementFilter == null ? collector : collector.WherePasses(elementFilter)).GetElementCount();

                Commit(transaction, request.DryRun);
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["filter"] = request.Name.Trim(),
                ["id"] = request.DryRun ? null : id,
                ["updatedExisting"] = existing != null,
                ["categories"] = catIds.Select(c => Category.GetCategory(doc, c)?.Name).ToList(),
                ["rules"] = descriptions,
                ["logic"] = parameterFilters.Count > 1 ? request.Logic : null,
                ["matchingElementsInProject"] = matched,
                ["summary"] = request.DryRun
                    ? "Предпросмотр: фильтр не создан. Для создания повторите с dryRun=false. Применить к виду/шаблону — set_graphics_overrides."
                    : "Фильтр сохранён. Применить к виду/шаблону — set_graphics_overrides.",
            };
        }

        private static FilterRule BuildRule(Document doc, ICollection<ElementId> filterable, Element sample, FilterRuleSpec spec, out string description)
        {
            ElementId pid = filterable.FirstOrDefault(id => string.Equals(ParameterName(doc, id), spec.Parameter?.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ?? throw new ArgumentException($"Параметр «{spec.Parameter}» нельзя использовать в фильтре для этих категорий. Список: list_filterable_parameters.");
            string op = (spec.Operator ?? "equals").Trim().ToLowerInvariant();
            description = $"{ParameterName(doc, pid)} {op} {spec.Value}".Trim();

            if (op == "has_value") return ParameterFilterRuleFactory.CreateHasValueParameterRule(pid);
            if (op == "has_no_value") return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(pid);
            if (spec.Value == null) throw new ArgumentException($"Правило «{spec.Parameter}»: не задано значение (value).");

            Parameter p = sample == null ? null : FindParameter(sample, pid);
            StorageType storage = p?.StorageType ?? StorageOf(doc, pid);

            switch (storage)
            {
                case StorageType.Integer:
                {
                    int value = ParseInt(spec.Value);
                    return NumericRule(op, pid, value, null, null);
                }
                case StorageType.Double:
                {
                    double value = double.Parse(spec.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    double internalValue = value;
                    if (p != null)
                    {
                        try
                        {
                            internalValue = UnitUtils.ConvertToInternalUnits(value, p.GetUnitTypeId());
                        }
                        catch
                        {
                            // безразмерный параметр — значение как есть
                        }
                    }
                    else
                    {
                        // Нет элемента-образца: единицы неизвестны, считаем значение в миллиметрах.
                        internalValue = RevitText.ToFeet(value);
                        description += " (мм: в проекте нет элементов, единицы приняты как миллиметры)";
                    }
                    return NumericRule(op, pid, null, internalValue, 1e-6);
                }
                case StorageType.ElementId:
                {
                    ElementId value = RevitIds.ToElementId(long.Parse(spec.Value, CultureInfo.InvariantCulture));
                    if (op == "equals") return ParameterFilterRuleFactory.CreateEqualsRule(pid, value);
                    if (op == "not_equals") return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, value);
                    throw new ArgumentException($"Для параметра-ссылки «{spec.Parameter}» доступны только equals/not_equals (значение — id элемента).");
                }
                default:
                    return StringRule(op, pid, spec.Value);
            }
        }

        private static FilterRule NumericRule(string op, ElementId pid, int? i, double? d, double? eps)
        {
            switch (op)
            {
                case "equals": return i != null ? ParameterFilterRuleFactory.CreateEqualsRule(pid, i.Value) : ParameterFilterRuleFactory.CreateEqualsRule(pid, d.Value, eps.Value);
                case "not_equals": return i != null ? ParameterFilterRuleFactory.CreateNotEqualsRule(pid, i.Value) : ParameterFilterRuleFactory.CreateNotEqualsRule(pid, d.Value, eps.Value);
                case "greater": return i != null ? ParameterFilterRuleFactory.CreateGreaterRule(pid, i.Value) : ParameterFilterRuleFactory.CreateGreaterRule(pid, d.Value, eps.Value);
                case "greater_or_equal": return i != null ? ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, i.Value) : ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, d.Value, eps.Value);
                case "less": return i != null ? ParameterFilterRuleFactory.CreateLessRule(pid, i.Value) : ParameterFilterRuleFactory.CreateLessRule(pid, d.Value, eps.Value);
                case "less_or_equal": return i != null ? ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, i.Value) : ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, d.Value, eps.Value);
                default: throw new ArgumentException($"Оператор «{op}» не подходит для числа: equals, not_equals, greater, greater_or_equal, less, less_or_equal.");
            }
        }

        private static FilterRule StringRule(string op, ElementId pid, string value)
        {
#if REVIT2021 || REVIT2022
            switch (op)
            {
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, value, false);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, value, false);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(pid, value, false);
                case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(pid, value, false);
                case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(pid, value, false);
                case "not_begins_with": return ParameterFilterRuleFactory.CreateNotBeginsWithRule(pid, value, false);
                case "ends_with": return ParameterFilterRuleFactory.CreateEndsWithRule(pid, value, false);
                case "not_ends_with": return ParameterFilterRuleFactory.CreateNotEndsWithRule(pid, value, false);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, value, false);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, value, false);
            }
#else
            switch (op)
            {
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, value);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, value);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(pid, value);
                case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(pid, value);
                case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(pid, value);
                case "not_begins_with": return ParameterFilterRuleFactory.CreateNotBeginsWithRule(pid, value);
                case "ends_with": return ParameterFilterRuleFactory.CreateEndsWithRule(pid, value);
                case "not_ends_with": return ParameterFilterRuleFactory.CreateNotEndsWithRule(pid, value);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, value);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, value);
            }
#endif
            throw new ArgumentException($"Оператор «{op}» не подходит для текста: equals, not_equals, contains, not_contains, begins_with, ends_with, has_value, has_no_value.");
        }

        // ===================== Переопределение графики =====================

        public static Dictionary<string, object> SetOverrides(Document doc, SetOverridesRequest request)
        {
            View view = ResolveViewOrTemplate(doc, request.View);
            if (!view.AreGraphicsOverridesAllowed())
            {
                throw new InvalidOperationException($"Вид «{view.Name}» не поддерживает переопределения графики.");
            }
            bool byFilter = !string.IsNullOrWhiteSpace(request.Filter);
            bool byCategory = !string.IsNullOrWhiteSpace(request.Category);
            if (byFilter == byCategory)
            {
                throw new ArgumentException("Укажите либо filter, либо category.");
            }

            var warnings = new List<string>();
            if (!view.IsTemplate && view.ViewTemplateId != ElementId.InvalidElementId)
            {
                var template = doc.GetElement(view.ViewTemplateId) as View;
                if (template != null && (byFilter ? ControlsFilters(template) : ControlsModelCategories(template)))
                {
                    warnings.Add($"к виду применён шаблон «{template.Name}», он управляет этими настройками: изменения не будут видны. Меняйте шаблон (view = имя шаблона).");
                }
            }

            string target;
            // Вид открывается на время изменений (иначе Revit перерисовывает неоткрытый вид на каждую правку)
            using (ViewSession.Open(doc, view))
            {
                using (var transaction = new Transaction(doc, $"AerBIM: графика вида «{view.Name}»"))
                {
                    transaction.Start();
                    OverrideGraphicSettings settings;
                    if (byFilter)
                    {
                        ParameterFilterElement filter = ResolveFilter(doc, request.Filter);
                        target = $"фильтр «{filter.Name}»";
                        if (!view.IsFilterApplied(filter.Id))
                        {
                            view.AddFilter(filter.Id);
                            warnings.Add("фильтр добавлен на вид");
                        }
                        settings = request.Reset ? new OverrideGraphicSettings() : view.GetFilterOverrides(filter.Id);
                        settings = Apply(doc, settings, request.Overrides);
                        view.SetFilterOverrides(filter.Id, settings);
                        if (request.Visible != null) view.SetFilterVisibility(filter.Id, request.Visible.Value);
                    }
                    else
                    {
                        ElementId categoryId = DimensionOperations.CategoryId(doc, request.Category)
                            ?? throw new ArgumentException($"Категория «{request.Category}» не найдена.");
                        target = $"категория «{Category.GetCategory(doc, categoryId)?.Name}»";
                        settings = request.Reset ? new OverrideGraphicSettings() : view.GetCategoryOverrides(categoryId);
                        settings = Apply(doc, settings, request.Overrides);
                        view.SetCategoryOverrides(categoryId, settings);
                        if (request.Visible != null)
                        {
                            if (view.CanCategoryBeHidden(categoryId)) view.SetCategoryHidden(categoryId, !request.Visible.Value);
                            else warnings.Add("видимость этой категории на виде изменить нельзя");
                        }
                    }
                    Commit(transaction, request.DryRun);
                }
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["view"] = (view.IsTemplate ? "шаблон " : "") + $"«{view.Name}»",
                ["target"] = target,
                ["warnings"] = warnings,
                ["summary"] = request.DryRun
                    ? "Предпросмотр: графика не изменена. Для применения повторите с dryRun=false."
                    : "Графика изменена. Отменить можно в Revit через Ctrl+Z.",
            };
        }

        public static Dictionary<string, object> RemoveFilter(Document doc, string viewName, string filterName, bool dryRun)
        {
            View view = ResolveViewOrTemplate(doc, viewName);
            ParameterFilterElement filter = ResolveFilter(doc, filterName);
            if (!view.IsFilterApplied(filter.Id))
            {
                throw new InvalidOperationException($"Фильтр «{filter.Name}» не применён к «{view.Name}».");
            }
            // Вид открывается на время изменений (иначе Revit перерисовывает неоткрытый вид на каждую правку)
            using (ViewSession.Open(doc, view))
            {
                using (var transaction = new Transaction(doc, $"AerBIM: убрать фильтр «{filter.Name}»"))
                {
                    transaction.Start();
                    view.RemoveFilter(filter.Id);
                    Commit(transaction, dryRun);
                }
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["view"] = view.Name,
                ["filter"] = filter.Name,
                ["summary"] = dryRun ? "Предпросмотр: фильтр не убран." : "Фильтр убран с вида (сам фильтр в проекте остался).",
            };
        }

        private static bool ControlsModelCategories(View template)
        {
            ElementId model = new ElementId(BuiltInParameter.VIS_GRAPHICS_MODEL);
            return !template.GetNonControlledTemplateParameterIds().Contains(model);
        }

        private static OverrideGraphicSettings Apply(Document doc, OverrideGraphicSettings s, GraphicsOverrides o)
        {
            if (o == null) return s;
            ElementId solid = SolidFillId(doc);

            if (o.LineColor != null) s.SetProjectionLineColor(ParseColor(o.LineColor));
            if (o.LineWeight != null) s.SetProjectionLineWeight(o.LineWeight.Value);
            if (o.LinePattern != null) s.SetProjectionLinePatternId(LinePatternId(doc, o.LinePattern));
            if (o.SurfaceColor != null)
            {
                s.SetSurfaceForegroundPatternColor(ParseColor(o.SurfaceColor));
                if (o.SurfacePattern == null && solid != null) s.SetSurfaceForegroundPatternId(solid);
                s.SetSurfaceForegroundPatternVisible(true);
            }
            if (o.SurfacePattern != null) s.SetSurfaceForegroundPatternId(FillPatternId(doc, o.SurfacePattern, solid));
            if (o.CutLineColor != null) s.SetCutLineColor(ParseColor(o.CutLineColor));
            if (o.CutLineWeight != null) s.SetCutLineWeight(o.CutLineWeight.Value);
            if (o.CutColor != null)
            {
                s.SetCutForegroundPatternColor(ParseColor(o.CutColor));
                if (o.CutPattern == null && solid != null) s.SetCutForegroundPatternId(solid);
                s.SetCutForegroundPatternVisible(true);
            }
            if (o.CutPattern != null) s.SetCutForegroundPatternId(FillPatternId(doc, o.CutPattern, solid));
            if (o.Transparency != null) s.SetSurfaceTransparency(Math.Max(0, Math.Min(100, o.Transparency.Value)));
            if (o.Halftone != null) s.SetHalftone(o.Halftone.Value);
            return s;
        }

        // ===================== Шаблоны =====================

        public static Dictionary<string, object> ApplyTemplate(Document doc, ApplyTemplateRequest request)
        {
            View template = null;
            bool remove = string.Equals(request.Template?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
            if (!remove)
            {
                string wanted = (request.Template ?? string.Empty).Trim();
                template = Templates(doc).FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.CurrentCultureIgnoreCase) ||
                                                             (long.TryParse(wanted, out long tid) && RevitIds.IdOf(t.Id) == tid))
                           ?? throw new ArgumentException($"Шаблон вида «{wanted}» не найден. Список: list_view_templates.");
            }

            List<View> views;
            if (request.Views.Count > 0)
            {
                views = request.Views.Select(v => ViewOperations.ResolveView(doc, v)).ToList();
            }
            else
            {
                if (string.IsNullOrEmpty(request.ViewType) && string.IsNullOrEmpty(request.NameContains))
                {
                    throw new ArgumentException("Укажите views или отбор viewType/nameContains.");
                }
                views = ViewOperations.ListViews(doc, request.ViewType ?? "all")
                    .Where(v => string.IsNullOrEmpty(request.NameContains) || v.Name.IndexOf(request.NameContains, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    .ToList();
            }

            var results = new List<Dictionary<string, object>>();
            int changed = 0;
            using (var transaction = new Transaction(doc, remove ? "AerBIM: снять шаблон вида" : $"AerBIM: шаблон «{template.Name}»"))
            {
                transaction.Start();
                foreach (View view in views)
                {
                    string status;
                    string message = null;
                    ElementId targetId = remove ? ElementId.InvalidElementId : template.Id;
                    if (view.ViewTemplateId == targetId)
                    {
                        status = "unchanged";
                    }
                    else if (!remove && !view.IsValidViewTemplate(template.Id))
                    {
                        status = "incompatible";
                        message = $"шаблон для {template.ViewType}, а вид — {view.ViewType}";
                    }
                    else
                    {
                        try
                        {
                            view.ViewTemplateId = targetId;
                            status = "applied";
                            changed++;
                        }
                        catch (Exception ex)
                        {
                            status = "failed";
                            message = ex.Message;
                        }
                    }
                    results.Add(new Dictionary<string, object>
                    {
                        ["view"] = view.Name,
                        ["id"] = RevitIds.IdOf(view.Id),
                        ["status"] = status,
                        ["message"] = message,
                    });
                }
                if (changed == 0) transaction.RollBack(); else Commit(transaction, request.DryRun);
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["template"] = remove ? "none" : template.Name,
                ["changed"] = changed,
                ["views"] = results,
                ["summary"] = (request.DryRun ? "Предпросмотр: будет изменено видов: " : "Изменено видов: ") + changed +
                              (request.DryRun ? ". Для применения повторите с dryRun=false." : ". Отменить можно в Revit через Ctrl+Z."),
            };
        }

        public static Dictionary<string, object> CreateTemplateFromView(Document doc, string viewName, string templateName, bool dryRun)
        {
            View view = ViewOperations.ResolveView(doc, viewName);
            if (string.IsNullOrWhiteSpace(templateName)) throw new ArgumentException("Укажите имя шаблона (name).");
            if (Templates(doc).Any(t => string.Equals(t.Name, templateName.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            {
                throw new InvalidOperationException($"Шаблон «{templateName}» уже существует.");
            }
            long? id = null;
            using (var transaction = new Transaction(doc, $"AerBIM: шаблон из вида «{view.Name}»"))
            {
                transaction.Start();
                View template = view.CreateViewTemplate();
                template.Name = templateName.Trim();
                id = RevitIds.IdOf(template.Id);
                Commit(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["fromView"] = view.Name,
                ["template"] = templateName.Trim(),
                ["id"] = dryRun ? null : id,
                ["summary"] = dryRun ? "Предпросмотр: шаблон не создан." : "Шаблон создан. Применить — apply_view_template.",
            };
        }

        // ===================== Помощники =====================

        private static List<ElementId> ResolveCategories(Document doc, List<string> categories)
        {
            if (categories == null || categories.Count == 0) throw new ArgumentException("Укажите категории (categories): OST_* или имена.");
            var ids = new List<ElementId>();
            var missing = new List<string>();
            foreach (string c in categories)
            {
                ElementId id = DimensionOperations.CategoryId(doc, c);
                if (id == null) missing.Add(c); else if (!ids.Contains(id)) ids.Add(id);
            }
            if (missing.Count > 0) throw new ArgumentException("Категории не найдены: " + string.Join(", ", missing));
            ICollection<ElementId> filterable = ParameterFilterUtilities.GetAllFilterableCategories();
            List<string> notFilterable = ids.Where(i => !filterable.Contains(i)).Select(i => Category.GetCategory(doc, i)?.Name ?? RevitIds.IdOf(i).ToString()).ToList();
            if (notFilterable.Count > 0) throw new ArgumentException("Эти категории нельзя использовать в фильтрах: " + string.Join(", ", notFilterable));
            return ids;
        }

        private static string ParameterName(Document doc, ElementId id)
        {
            long raw = RevitIds.IdOf(id);
            if (raw < 0)
            {
                try { return LabelUtils.GetLabelFor((BuiltInParameter)(int)raw); } catch { return null; }
            }
            return doc.GetElement(id)?.Name;
        }

        /// <summary>Тип значения без элемента-образца: встроенные параметры — у Revit, остальные — по типу данных определения.</summary>
        private static StorageType StorageOf(Document doc, ElementId id)
        {
            long raw = RevitIds.IdOf(id);
            if (raw < 0)
            {
                try { return doc.get_TypeOfStorage((BuiltInParameter)(int)raw); } catch { return StorageType.String; }
            }
            if (doc.GetElement(id) is ParameterElement parameterElement)
            {
                try
                {
                    return RevitCompat.StorageOf(parameterElement.GetDefinition());
                }
                catch
                {
                    // неизвестный тип — как текст
                }
            }
            return StorageType.String;
        }

        private static Parameter FindParameter(Element element, ElementId id)
        {
            foreach (Parameter p in element.Parameters)
            {
                if (p.Id == id) return p;
            }
            if (element.GetTypeId() != ElementId.InvalidElementId && element.Document.GetElement(element.GetTypeId()) is Element type)
            {
                foreach (Parameter p in type.Parameters)
                {
                    if (p.Id == id) return p;
                }
            }
            return null;
        }

        private static int ParseInt(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "да": case "yes": case "true": return 1;
                case "нет": case "no": case "false": return 0;
                default: return int.Parse(value.Trim(), CultureInfo.InvariantCulture);
            }
        }

        private static Color ParseColor(string value)
        {
            string v = value.Trim();
            if (v.StartsWith("#") && v.Length == 7)
            {
                return new Color(Convert.ToByte(v.Substring(1, 2), 16), Convert.ToByte(v.Substring(3, 2), 16), Convert.ToByte(v.Substring(5, 2), 16));
            }
            string[] parts = v.Split(',', ';', ' ').Where(p => p.Length > 0).ToArray();
            if (parts.Length == 3)
            {
                return new Color(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
            }
            throw new ArgumentException($"Цвет «{value}»: используйте #RRGGBB или «R,G,B».");
        }

        private static ElementId SolidFillId(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id;

        private static ElementId FillPatternId(Document doc, string name, ElementId solid)
        {
            if (string.Equals(name, "solid", StringComparison.OrdinalIgnoreCase) && solid != null) return solid;
            if (string.Equals(name, "none", StringComparison.OrdinalIgnoreCase)) return ElementId.InvalidElementId;
            return new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                       .FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id
                   ?? throw new ArgumentException($"Штриховка «{name}» не найдена (или «solid»).");
        }

        private static ElementId LinePatternId(Document doc, string name)
        {
            if (string.Equals(name, "solid", StringComparison.OrdinalIgnoreCase)) return LinePatternElement.GetSolidPatternId();
            return new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                       .FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id
                   ?? throw new ArgumentException($"Тип линии «{name}» не найден (или «solid»).");
        }

        private static void Commit(Transaction transaction, bool dryRun)
        {
            if (dryRun)
            {
                transaction.RollBack();
            }
            else if (transaction.Commit() != TransactionStatus.Committed)
            {
                throw new InvalidOperationException("Revit не применил изменения.");
            }
        }
    }
}
