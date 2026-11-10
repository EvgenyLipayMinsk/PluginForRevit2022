using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace PluginForRevit2022.Core
{
    /// <summary>Пакетное переименование, CAD-подложки и удаление неиспользуемого.</summary>
    internal static class CleanupOperations
    {
        /// <summary>levels, grids, types (по категориям) или rooms: найти/заменить, префикс, суффикс.</summary>
        public static Dictionary<string, object> BatchRename(Document doc, string target, List<string> categories, string contains,
                                                             string find, string replace, string prefix, string suffix, bool dryRun)
        {
            string kind = (target ?? "").Trim().ToLowerInvariant();
            List<Element> elements;
            switch (kind)
            {
                case "levels": elements = new FilteredElementCollector(doc).OfClass(typeof(Level)).ToList(); break;
                case "grids": elements = new FilteredElementCollector(doc).OfClass(typeof(Grid)).ToList(); break;
                case "rooms": elements = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToList(); break;
                case "types":
                    if (categories == null || categories.Count == 0) throw new ArgumentException("Для types укажите categories.");
                    elements = new FilteredElementCollector(doc).WhereElementIsElementType()
                        .WherePasses(new ElementMulticategoryFilter(OpsCommon.CategoryIds(doc, categories))).ToList();
                    break;
                default: throw new ArgumentException("target: levels, grids, types или rooms (виды и листы — команда rename).");
            }
            if (string.IsNullOrEmpty(find) && string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix)) throw new ArgumentException("Укажите find/replace, prefix или suffix.");
            if (!string.IsNullOrWhiteSpace(contains)) elements = elements.Where(e => e.Name.IndexOf(contains, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();

            var changes = new List<(Element element, string from, string to)>();
            foreach (Element element in elements)
            {
                string name = element.Name;
                string next = string.IsNullOrEmpty(find) ? name : name.Replace(find, replace ?? "");
                next = (prefix ?? "") + next + (suffix ?? "");
                if (next != name) changes.Add((element, name, next));
            }

            // Имена уровней, осей и типов одного семейства должны быть уникальны.
            var conflicts = new List<string>();
            if (kind != "rooms")
            {
                string Scope(Element e) => kind == "types" ? (e as ElementType)?.FamilyName + "|" + e.Category?.Id : kind;
                var renamed = new HashSet<long>(changes.Select(c => RevitIds.IdOf(c.element.Id)));
                var taken = elements.Where(e => !renamed.Contains(RevitIds.IdOf(e.Id))).Select(e => Scope(e) + "|" + e.Name)
                    .Concat(changes.Select(c => Scope(c.element) + "|" + c.to)).GroupBy(n => n, StringComparer.CurrentCultureIgnoreCase);
                conflicts = taken.Where(g => g.Count() > 1).Select(g => g.Key.Split('|').Last()).ToList();
            }
            if (conflicts.Count > 0) throw new InvalidOperationException("Имена повторятся: " + string.Join(", ", conflicts.Take(10)));

            var failures = new List<string>();
            using (var transaction = new Transaction(doc, "AerBIM: переименование"))
            {
                transaction.Start();
                foreach (var (element, from, to) in changes)
                {
                    try
                    {
                        element.Name = to;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{from}: {ex.Message}");
                    }
                }
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["target"] = kind,
                ["changes"] = changes.Take(OpsCommon.MaxRows).Select(c => new Dictionary<string, object> { ["id"] = RevitIds.IdOf(c.element.Id), ["from"] = c.from, ["to"] = c.to }).ToList(),
                ["failures"] = failures,
                ["summary"] = OpsCommon.Summary(dryRun, $"будет переименовано: {changes.Count}", $"Переименовано: {changes.Count - failures.Count}."),
            };
        }

        /// <summary>list — CAD-подложки; delete — удалить указанные ids, все импортированные (imports) или все связанные (links).</summary>
        public static Dictionary<string, object> CadCleanup(Document doc, string action, string scope, List<long> ids, bool dryRun)
        {
            List<ImportInstance> all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            Dictionary<string, object> Describe(ImportInstance i) => new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(i.Id),
                ["name"] = i.Category?.Name ?? doc.GetElement(i.GetTypeId())?.Name,
                ["linked"] = i.IsLinked,
                ["viewSpecific"] = i.ViewSpecific,
                ["view"] = i.ViewSpecific ? doc.GetElement(i.OwnerViewId)?.Name : null,
                ["pinned"] = i.Pinned,
            };
            string mode = (action ?? "list").Trim().ToLowerInvariant();
            if (mode == "list")
            {
                return new Dictionary<string, object>
                {
                    ["total"] = all.Count, ["imported"] = all.Count(i => !i.IsLinked), ["linked"] = all.Count(i => i.IsLinked),
                    ["items"] = all.Take(OpsCommon.MaxRows).Select(Describe).ToList(),
                };
            }
            if (mode != "delete") throw new ArgumentException("action: list или delete.");
            List<ImportInstance> targets;
            if (ids != null && ids.Count > 0)
            {
                var wanted = new HashSet<long>(ids);
                targets = all.Where(i => wanted.Contains(RevitIds.IdOf(i.Id))).ToList();
            }
            else
            {
                switch ((scope ?? "").Trim().ToLowerInvariant())
                {
                    case "imports": targets = all.Where(i => !i.IsLinked).ToList(); break;
                    case "links": targets = all.Where(i => i.IsLinked).ToList(); break;
                    case "all": targets = all; break;
                    default: throw new ArgumentException("Укажите ids или scope: imports, links или all.");
                }
            }
            if (targets.Count == 0) throw new InvalidOperationException("Нечего удалять.");
            using (var transaction = new Transaction(doc, "AerBIM: удалить CAD"))
            {
                transaction.Start();
                doc.Delete(targets.Select(t => t.Id).ToList());
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["items"] = targets.Take(OpsCommon.MaxRows).Select(Describe).ToList(),
                ["summary"] = OpsCommon.Summary(dryRun, $"будет удалено CAD-подложек: {targets.Count}", $"Удалено CAD-подложек: {targets.Count}."),
            };
        }

        /// <summary>Удаляет неиспользуемое: типы семейств без экземпляров, шаблоны видов без видов, фильтры, не применённые ни к одному виду.</summary>
        public static Dictionary<string, object> PurgeUnused(Document doc, List<string> kinds, List<string> categories, bool dryRun)
        {
            var wanted = new HashSet<string>((kinds == null || kinds.Count == 0 ? new List<string> { "types" } : kinds).Select(k => k.Trim().ToLowerInvariant()));
            var candidates = new List<(string kind, Element element)>();
            if (wanted.Contains("types"))
            {
                List<ElementId> cats = categories != null && categories.Count > 0 ? OpsCommon.CategoryIds(doc, categories) : null;
                candidates.AddRange(ProjectInfoOperations.UnusedTypes(doc, cats).Select(s => ("types", (Element)s)));
            }
            List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            if (wanted.Contains("viewtemplates"))
            {
                var used = new HashSet<long>(views.Where(v => !v.IsTemplate).Select(v => RevitIds.IdOf(v.ViewTemplateId)));
                candidates.AddRange(views.Where(v => v.IsTemplate && !used.Contains(RevitIds.IdOf(v.Id))).Select(v => ("viewTemplates", (Element)v)));
            }
            if (wanted.Contains("filters"))
            {
                var used = new HashSet<long>();
                foreach (View view in views)
                {
                    try
                    {
                        foreach (ElementId id in view.GetFilters()) used.Add(RevitIds.IdOf(id));
                    }
                    catch (Exception)
                    {
                        // вид без поддержки фильтров
                    }
                }
                candidates.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Where(f => !used.Contains(RevitIds.IdOf(f.Id))).Select(f => ("filters", f)));
            }

            var deleted = new List<Dictionary<string, object>>();
            var skipped = new List<string>();
            using (var transaction = new Transaction(doc, "AerBIM: удалить неиспользуемое"))
            {
                transaction.Start();
                foreach (var (kind, element) in candidates)
                {
                    if (!element.IsValidObject) continue; // удалён вместе с предыдущим (например, семейство вместе с последним типом)
                    string label = kind == "types" ? OpsCommon.TypeLabel(doc, element.Id) : element.Name;
                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            doc.Delete(element.Id);
                            sub.Commit();
                            deleted.Add(new Dictionary<string, object> { ["kind"] = kind, ["id"] = RevitIds.IdOf(element.Id), ["name"] = label });
                        }
                        catch (Exception ex)
                        {
                            if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                            skipped.Add($"{label}: {ex.Message}");
                        }
                    }
                }
                OpsCommon.Finish(transaction, dryRun || deleted.Count == 0);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["byKind"] = deleted.GroupBy(d => (string)d["kind"]).ToDictionary(g => g.Key, g => g.Count()),
                ["items"] = deleted.Take(OpsCommon.MaxRows).ToList(),
                ["skipped"] = skipped.Take(50).ToList(),
                ["summary"] = OpsCommon.Summary(dryRun, $"можно удалить: {deleted.Count}", $"Удалено: {deleted.Count}."),
            };
        }
    }
}
