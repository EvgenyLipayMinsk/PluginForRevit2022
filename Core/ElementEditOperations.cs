using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Изменение элементов: удаление, замена типа, перемещение, копирование, параметры, стадии, рабочие наборы, семейства, графика.</summary>
    internal static class ElementEditOperations
    {
        public static Dictionary<string, object> Delete(Document doc, List<long> ids, bool dryRun)
        {
            List<Element> elements = OpsCommon.Elements(doc, ids);
            int total;
            using (var transaction = new Transaction(doc, "AerBIM: удалить элементы"))
            {
                transaction.Start();
                total = doc.Delete(elements.Select(e => e.Id).ToList()).Count;
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["requested"] = elements.Count,
                ["deletedTotal"] = total,
                ["dependents"] = total - elements.Count,
                ["byCategory"] = elements.GroupBy(e => e.Category?.Name ?? "—").ToDictionary(g => g.Key, g => g.Count()),
                ["summary"] = OpsCommon.Summary(dryRun, $"будет удалено {elements.Count} элементов и зависимых {total - elements.Count}", $"Удалено элементов: {total}."),
            };
        }

        public static ElementType ResolveType(Document doc, string label, ElementId categoryId = null)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Укажите тип «Семейство : Тип».");
            List<ElementType> matches = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
                .Where(t => categoryId == null || (t.Category != null && t.Category.Id == categoryId))
                .Where(t => string.Equals(OpsCommon.TypeLabel(doc, t.Id), label.Trim(), StringComparison.CurrentCultureIgnoreCase)
                         || string.Equals(t.Name, label.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException(matches.Count == 0 ? $"Тип «{label}» не найден." : $"Тип «{label}» неоднозначен: укажите «Семейство : Тип».");
        }

        public static Dictionary<string, object> ChangeType(Document doc, List<long> ids, string category, string fromType, string toType, bool dryRun)
        {
            List<Element> elements;
            if (ids != null && ids.Count > 0)
            {
                elements = OpsCommon.Elements(doc, ids);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(fromType)) throw new ArgumentException("Укажите ids или category вместе с fromType.");
                ElementId categoryId = OpsCommon.CategoryIds(doc, new[] { category }).First();
                ElementType from = ResolveType(doc, fromType, categoryId);
                elements = new FilteredElementCollector(doc).OfCategoryId(categoryId).WhereElementIsNotElementType().Where(e => e.GetTypeId() == from.Id).ToList();
            }
            if (elements.Count == 0) throw new InvalidOperationException("Нет элементов для замены типа.");
            ElementType target = ResolveType(doc, toType, elements[0].Category?.Id);

            var failures = new List<string>();
            int changed = 0;
            using (var transaction = new Transaction(doc, "AerBIM: заменить тип"))
            {
                transaction.Start();
                foreach (Element element in elements)
                {
                    if (element.Category?.Id != target.Category?.Id)
                    {
                        failures.Add($"id {RevitIds.IdOf(element.Id)}: другая категория");
                        continue;
                    }
                    try
                    {
                        if (element.GetTypeId() != target.Id) element.ChangeTypeId(target.Id);
                        changed++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"id {RevitIds.IdOf(element.Id)}: {ex.Message}");
                    }
                }
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["targetType"] = OpsCommon.TypeLabel(doc, target.Id),
                ["changed"] = changed,
                ["failures"] = failures.Take(50).ToList(),
                ["summary"] = OpsCommon.Summary(dryRun, $"тип будет заменён у {changed} элементов", $"Тип заменён у {changed} элементов."),
            };
        }

        /// <summary>move, copy — смещение; rotate — поворот вокруг вертикали через центр; mirror — зеркало относительно оси x или y через центр.</summary>
        public static Dictionary<string, object> Modify(Document doc, List<long> ids, string action, double dx, double dy, double dz, double angle, string axis, bool dryRun)
        {
            List<Element> elements = OpsCommon.Elements(doc, ids);
            List<ElementId> elementIds = elements.Select(e => e.Id).ToList();
            string mode = (action ?? "").Trim().ToLowerInvariant();
            XYZ offset = new XYZ(RevitText.ToFeet(dx), RevitText.ToFeet(dy), RevitText.ToFeet(dz));
            XYZ center = Center(elements);
            var created = new List<long>();
            using (var transaction = new Transaction(doc, "AerBIM: изменить положение"))
            {
                transaction.Start();
                switch (mode)
                {
                    case "move":
                        ElementTransformUtils.MoveElements(doc, elementIds, offset);
                        break;
                    case "copy":
                        created.AddRange(ElementTransformUtils.CopyElements(doc, elementIds, offset).Select(RevitIds.IdOf));
                        break;
                    case "rotate":
                        ElementTransformUtils.RotateElements(doc, elementIds, Line.CreateBound(center, center + XYZ.BasisZ), angle * Math.PI / 180);
                        break;
                    case "mirror":
                        XYZ normal = string.Equals(axis, "y", StringComparison.OrdinalIgnoreCase) ? XYZ.BasisY : XYZ.BasisX;
                        ElementTransformUtils.MirrorElements(doc, elementIds, Plane.CreateByNormalAndOrigin(normal, center), false);
                        break;
                    default:
                        throw new ArgumentException("action: move, copy, rotate или mirror.");
                }
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["action"] = mode,
                ["elements"] = elements.Count,
                ["created"] = dryRun ? new List<long>() : created,
                ["center"] = OpsCommon.Point(center),
                ["summary"] = OpsCommon.Summary(dryRun, $"{mode} для {elements.Count} элементов выполняется без ошибок", $"Выполнено: {mode}, элементов: {elements.Count}."),
            };
        }

        private static XYZ Center(List<Element> elements)
        {
            List<BoundingBoxXYZ> boxes = elements.Select(e => e.get_BoundingBox(null)).Where(b => b != null).ToList();
            if (boxes.Count == 0) return XYZ.Zero;
            return new XYZ((boxes.Min(b => b.Min.X) + boxes.Max(b => b.Max.X)) / 2, (boxes.Min(b => b.Min.Y) + boxes.Max(b => b.Max.Y)) / 2, (boxes.Min(b => b.Min.Z) + boxes.Max(b => b.Max.Z)) / 2);
        }

        /// <summary>Копирует элементы вида (аннотации, детали) на другие виды того же типа.</summary>
        public static Dictionary<string, object> CopyToViews(Document doc, List<long> ids, string fromView, List<string> toViews, bool dryRun)
        {
            View source = ViewOperations.ResolveView(doc, fromView);
            List<ElementId> elementIds = OpsCommon.Elements(doc, ids).Select(e => e.Id).ToList();
            if (toViews == null || toViews.Count == 0) throw new ArgumentException("Укажите toViews.");
            var results = new List<Dictionary<string, object>>();
            using (var transaction = new Transaction(doc, "AerBIM: копировать на виды"))
            {
                transaction.Start();
                foreach (string value in toViews)
                {
                    View target = ViewOperations.ResolveView(doc, value);
                    try
                    {
                        int copied = ElementTransformUtils.CopyElements(source, elementIds, target, Transform.Identity, new CopyPasteOptions()).Count;
                        results.Add(new Dictionary<string, object> { ["view"] = target.Name, ["copied"] = copied });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new Dictionary<string, object> { ["view"] = target.Name, ["error"] = ex.Message });
                    }
                }
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["views"] = results,
                ["summary"] = OpsCommon.Summary(dryRun, $"копирование на виды: {results.Count}", $"Скопировано на виды: {results.Count(r => r.ContainsKey("copied"))}."),
            };
        }

        /// <summary>Копирует значения параметров с элемента-образца. Без списка — все пользовательские параметры экземпляра.</summary>
        public static Dictionary<string, object> MatchProperties(Document doc, long sourceId, List<long> targetIds, List<string> parameters, bool dryRun)
        {
            Element source = OpsCommon.Elements(doc, new[] { sourceId })[0];
            List<Element> targets = OpsCommon.Elements(doc, targetIds).Where(t => t.Id != source.Id).ToList();
            List<Parameter> values = parameters != null && parameters.Count > 0
                ? parameters.Select(n => source.LookupParameter(n.Trim()) ?? throw new ArgumentException($"У образца нет параметра «{n}».")).ToList()
                : source.Parameters.Cast<Parameter>().Where(p => !p.IsReadOnly && p.HasValue &&
                    (!(p.Definition is InternalDefinition d) || d.BuiltInParameter == BuiltInParameter.INVALID)).ToList();

            var failures = new List<string>();
            int written = 0;
            using (var transaction = new Transaction(doc, "AerBIM: копировать параметры"))
            {
                transaction.Start();
                foreach (Element target in targets)
                {
                    foreach (Parameter from in values)
                    {
                        Parameter to = target.LookupParameter(from.Definition.Name);
                        if (to == null || to.IsReadOnly || to.StorageType != from.StorageType)
                        {
                            failures.Add($"id {RevitIds.IdOf(target.Id)}: «{from.Definition.Name}» нельзя записать");
                            continue;
                        }
                        bool ok;
                        switch (from.StorageType)
                        {
                            case StorageType.String: ok = to.Set(from.AsString() ?? ""); break;
                            case StorageType.Integer: ok = to.Set(from.AsInteger()); break;
                            case StorageType.Double: ok = to.Set(from.AsDouble()); break;
                            case StorageType.ElementId: ok = to.Set(from.AsElementId()); break;
                            default: ok = false; break;
                        }
                        if (ok) written++;
                        else failures.Add($"id {RevitIds.IdOf(target.Id)}: «{from.Definition.Name}» не записан");
                    }
                }
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["parameters"] = values.Select(p => p.Definition.Name).ToList(),
                ["targets"] = targets.Count,
                ["written"] = written,
                ["failures"] = failures.Distinct().Take(50).ToList(),
                ["summary"] = OpsCommon.Summary(dryRun, $"будет записано значений: {written}", $"Записано значений: {written}."),
            };
        }

        public static Dictionary<string, object> SetPhase(Document doc, List<long> ids, string created, string demolished, bool dryRun)
        {
            List<Element> elements = OpsCommon.Elements(doc, ids);
            ElementId PhaseId(string name) => string.Equals(name?.Trim(), "none", StringComparison.OrdinalIgnoreCase)
                ? ElementId.InvalidElementId
                : doc.Phases.Cast<Phase>().FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id
                  ?? throw new ArgumentException($"Стадия «{name}» не найдена.");
            ElementId createdId = string.IsNullOrWhiteSpace(created) ? null : PhaseId(created);
            ElementId demolishedId = string.IsNullOrWhiteSpace(demolished) ? null : PhaseId(demolished);
            if (createdId == null && demolishedId == null) throw new ArgumentException("Укажите created и/или demolished.");

            var failures = new List<string>();
            int changed = 0;
            using (var transaction = new Transaction(doc, "AerBIM: стадии элементов"))
            {
                transaction.Start();
                foreach (Element element in elements)
                {
                    try
                    {
                        if (!element.ArePhasesModifiable()) throw new InvalidOperationException("стадии не меняются");
                        if (createdId != null) element.CreatedPhaseId = createdId;
                        if (demolishedId != null) element.DemolishedPhaseId = demolishedId;
                        changed++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"id {RevitIds.IdOf(element.Id)}: {ex.Message}");
                    }
                }
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun, ["changed"] = changed, ["failures"] = failures.Take(50).ToList(),
                ["summary"] = OpsCommon.Summary(dryRun, $"стадии будут изменены у {changed} элементов", $"Стадии изменены у {changed} элементов."),
            };
        }

        public static Dictionary<string, object> SetWorkset(Document doc, List<long> ids, string workset, bool dryRun)
        {
            List<Element> elements = OpsCommon.Elements(doc, ids);
            Workset target = ProjectInfoOperations.ResolveWorkset(doc, workset);
            var failures = new List<string>();
            int changed = 0;
            using (var transaction = new Transaction(doc, "AerBIM: рабочий набор"))
            {
                transaction.Start();
                foreach (Element element in elements)
                {
                    Parameter parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (parameter == null || parameter.IsReadOnly || !parameter.Set(target.Id.IntegerValue))
                    {
                        failures.Add($"id {RevitIds.IdOf(element.Id)}: рабочий набор не меняется");
                        continue;
                    }
                    changed++;
                }
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun, ["workset"] = target.Name, ["changed"] = changed, ["failures"] = failures.Take(50).ToList(),
                ["summary"] = OpsCommon.Summary(dryRun, $"в набор «{target.Name}» будет перенесено {changed} элементов", $"Перенесено в «{target.Name}»: {changed}."),
            };
        }

        public static Dictionary<string, object> LoadFamily(Document doc, string path, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new ArgumentException($"Файл семейства не найден: {path}");
            if (!path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Нужен файл .rfa.");
            string name = Path.GetFileNameWithoutExtension(path);
            Family existing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (existing != null)
            {
                return new Dictionary<string, object> { ["loaded"] = false, ["family"] = existing.Name, ["summary"] = $"Семейство «{existing.Name}» уже загружено, файл не перезаписывает его." };
            }
            if (dryRun)
            {
                return new Dictionary<string, object> { ["dryRun"] = true, ["family"] = name, ["summary"] = $"Предпросмотр: будет загружено семейство «{name}». Для загрузки повторите с dryRun=false." };
            }
            List<string> types;
            using (var transaction = new Transaction(doc, "AerBIM: загрузить семейство"))
            {
                transaction.Start();
                if (!doc.LoadFamily(path, out Family family) || family == null) throw new InvalidOperationException("Revit не загрузил семейство.");
                types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id).Name).ToList();
                OpsCommon.Finish(transaction, false);
            }
            return new Dictionary<string, object> { ["loaded"] = true, ["family"] = name, ["types"] = types, ["summary"] = $"Загружено семейство «{name}», типов: {types.Count}." };
        }

        public static Dictionary<string, object> OverrideGraphics(Document doc, string viewValue, List<long> ids, string color, int? lineWeight, int? transparency, bool? halftone, bool reset, bool dryRun)
        {
            View view = OpsCommon.ViewOrActive(doc, viewValue);
            List<Element> elements = OpsCommon.Elements(doc, ids);
            var overrides = new OverrideGraphicSettings();
            if (!reset)
            {
                if (!string.IsNullOrWhiteSpace(color))
                {
                    Color c = OpsCommon.ParseColor(color);
                    overrides.SetProjectionLineColor(c);
                    overrides.SetCutLineColor(c);
                    ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                        .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting)?.Id;
                    if (solid != null)
                    {
                        overrides.SetSurfaceForegroundPatternId(solid);
                        overrides.SetSurfaceForegroundPatternColor(c);
                    }
                }
                if (lineWeight != null)
                {
                    if (lineWeight < 1 || lineWeight > 16) throw new ArgumentException("lineWeight: от 1 до 16.");
                    overrides.SetProjectionLineWeight(lineWeight.Value);
                    overrides.SetCutLineWeight(lineWeight.Value);
                }
                if (transparency != null)
                {
                    if (transparency < 0 || transparency > 100) throw new ArgumentException("transparency: от 0 до 100.");
                    overrides.SetSurfaceTransparency(transparency.Value);
                }
                if (halftone != null) overrides.SetHalftone(halftone.Value);
            }
            using (var transaction = new Transaction(doc, "AerBIM: графика элементов"))
            {
                transaction.Start();
                foreach (Element element in elements) view.SetElementOverrides(element.Id, overrides);
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun, ["view"] = view.Name, ["elements"] = elements.Count,
                ["summary"] = OpsCommon.Summary(dryRun, $"графика будет {(reset ? "сброшена" : "переопределена")} у {elements.Count} элементов",
                    $"Графика {(reset ? "сброшена" : "переопределена")} у {elements.Count} элементов на виде «{view.Name}»."),
            };
        }
    }
}
