using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Сведения о проекте и виде, рабочие наборы, стадии, статистика, «здоровье» модели, связи.</summary>
    internal static class ProjectInfoOperations
    {
        public static Dictionary<string, object> ProjectInfo(Document doc)
        {
            ProjectInfo info = doc.ProjectInformation;
            return new Dictionary<string, object>
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["fileSizeMb"] = FileSizeMb(doc),
                ["workshared"] = doc.IsWorkshared,
                ["name"] = info?.Name,
                ["number"] = info?.Number,
                ["client"] = info?.ClientName,
                ["address"] = info?.Address,
                ["status"] = info?.Status,
                ["issueDate"] = info?.IssueDate,
                ["author"] = info?.Author,
                ["organization"] = info?.OrganizationName,
                ["building"] = info?.BuildingName,
                ["activeView"] = doc.ActiveView?.Name,
                ["levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation)
                    .Select(l => new Dictionary<string, object> { ["name"] = l.Name, ["elevationMm"] = RevitText.ToMm(l.ProjectElevation), ["displayedMm"] = RevitText.ToMm(l.Elevation) }).ToList(),
                ["phases"] = doc.Phases.Cast<Phase>().Select(p => p.Name).ToList(),
                ["links"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .Select(l => new Dictionary<string, object> { ["name"] = l.Name, ["loaded"] = l.GetLinkDocument() != null }).ToList(),
            };
        }

        public static Dictionary<string, object> ViewInfo(Document doc)
        {
            View view = doc.ActiveView ?? throw new InvalidOperationException("Нет активного вида.");
            Parameter phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            return new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(view.Id),
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
                ["scale"] = view.Scale,
                ["detailLevel"] = view.DetailLevel.ToString(),
                ["level"] = (view as ViewPlan)?.GenLevel?.Name,
                ["template"] = doc.GetElement(view.ViewTemplateId)?.Name,
                ["cropActive"] = view.CropBoxActive,
                ["phase"] = phase == null ? null : doc.GetElement(phase.AsElementId())?.Name,
                ["sheets"] = ViewOperations.SheetsOf(doc, view).Select(s => $"{s.SheetNumber} - {s.Name}").ToList(),
            };
        }

        public static Dictionary<string, object> Worksets(Document doc)
        {
            if (!doc.IsWorkshared) return new Dictionary<string, object> { ["workshared"] = false, ["worksets"] = new List<object>() };
            WorksetId active = doc.GetWorksetTable().GetActiveWorksetId();
            return new Dictionary<string, object>
            {
                ["workshared"] = true,
                ["worksets"] = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => new Dictionary<string, object>
                {
                    ["id"] = w.Id.IntegerValue,
                    ["name"] = w.Name,
                    ["open"] = w.IsOpen,
                    ["editable"] = w.IsEditable,
                    ["owner"] = w.Owner,
                    ["active"] = w.Id == active,
                    ["elements"] = new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(w.Id)).WhereElementIsNotElementType().GetElementCount(),
                }).ToList(),
            };
        }

        public static Workset ResolveWorkset(Document doc, string name)
        {
            if (!doc.IsWorkshared) throw new InvalidOperationException("Проект без рабочих наборов.");
            return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => string.Equals(w.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ?? throw new ArgumentException($"Рабочий набор «{name}» не найден.");
        }

        public static Dictionary<string, object> Phases(Document doc) => new Dictionary<string, object>
        {
            ["phases"] = doc.Phases.Cast<Phase>().Select(p => new Dictionary<string, object> { ["id"] = RevitIds.IdOf(p.Id), ["name"] = p.Name }).ToList(),
            ["phaseFilters"] = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Select(f => f.Name).ToList(),
        };

        public static Dictionary<string, object> Statistics(Document doc)
        {
            List<Element> instances = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !(e is RevitLinkInstance)).ToList();
            List<Family> families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            return new Dictionary<string, object>
            {
                ["modelElements"] = instances.Count,
                ["byCategory"] = instances.GroupBy(e => e.Category.Name).OrderByDescending(g => g.Count()).Take(40).ToDictionary(g => g.Key, g => g.Count()),
                ["byLevel"] = instances.GroupBy(e => (doc.GetElement(e.LevelId) as Level)?.Name ?? "без уровня").OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
                ["families"] = families.Count,
                ["inPlaceFamilies"] = families.Count(f => f.IsInPlace),
                ["familyTypes"] = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).GetElementCount(),
                ["views"] = views.Count(v => !(v is ViewSheet) && !(v is ViewSchedule)),
                ["sheets"] = views.Count(v => v is ViewSheet),
                ["schedules"] = views.Count(v => v is ViewSchedule),
                ["viewTemplates"] = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => v.IsTemplate),
                ["revitLinks"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount(),
                ["cadImports"] = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).GetElementCount(),
                ["groups"] = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Group)).GetElementCount(),
                ["warnings"] = doc.GetWarnings().Count,
                ["fileSizeMb"] = FileSizeMb(doc),
            };
        }

        public static Dictionary<string, object> Health(Document doc)
        {
            int warnings = doc.GetWarnings().Count;
            int unusedTypes = UnusedTypes(doc).Count;
            int imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Count(i => !i.IsLinked);
            int inPlace = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Count(f => f.IsInPlace);
            var placed = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(v => RevitIds.IdOf(v.ViewId)));
            int unplaced = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Count(v => !v.IsTemplate && (v is ViewPlan || v is ViewSection || v is View3D) && !placed.Contains(RevitIds.IdOf(v.Id)));
            double? size = FileSizeMb(doc);

            var penalties = new List<(int points, string text)>
            {
                (Math.Min(30, warnings / 5), $"предупреждений Revit: {warnings} — get_warnings"),
                (Math.Min(15, unusedTypes / 20), $"неиспользуемых типов семейств: {unusedTypes} — purge_unused"),
                (Math.Min(15, imports * 3), $"импортированных (не связанных) CAD: {imports} — cad_link_cleanup"),
                (Math.Min(10, inPlace * 2), $"моделей в контексте: {inPlace}"),
                (Math.Min(10, unplaced / 20), $"видов вне листов: {unplaced}"),
                (size > 300 ? 10 : 0, $"размер файла: {size:0} МБ"),
            };
            int score = Math.Max(0, 100 - penalties.Sum(p => p.points));
            string grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : score >= 50 ? "E" : "F";
            return new Dictionary<string, object>
            {
                ["score"] = score,
                ["grade"] = grade,
                ["details"] = penalties.Select(p => new Dictionary<string, object> { ["penalty"] = p.points, ["finding"] = p.text }).ToList(),
                ["recommendations"] = penalties.Where(p => p.points > 0).OrderByDescending(p => p.points).Select(p => p.text).ToList(),
                ["note"] = "Оценка условная: штрафы за предупреждения, неиспользуемые типы, импорт CAD, модели в контексте, виды вне листов и размер файла.",
            };
        }

        public static List<FamilySymbol> UnusedTypes(Document doc, IEnumerable<ElementId> categories = null)
        {
            var used = new HashSet<long>(new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Select(e => e.GetTypeId()).Where(id => id != ElementId.InvalidElementId).Select(RevitIds.IdOf));
            IEnumerable<FamilySymbol> symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            if (categories != null)
            {
                var cats = new HashSet<long>(categories.Select(RevitIds.IdOf));
                if (cats.Count > 0) symbols = symbols.Where(s => s.Category != null && cats.Contains(RevitIds.IdOf(s.Category.Id)));
            }
            return symbols.Where(s => !used.Contains(RevitIds.IdOf(s.Id))).ToList();
        }

        public static Dictionary<string, object> Links(Document doc, string action, List<string> names, bool dryRun)
        {
            string mode = (action ?? "list").Trim().ToLowerInvariant();
            List<RevitLinkType> types = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().OrderBy(t => t.Name).ToList();
            if (mode == "list")
            {
                return new Dictionary<string, object>
                {
                    ["links"] = types.Select(t => new Dictionary<string, object>
                    {
                        ["name"] = t.Name,
                        ["loaded"] = RevitLinkType.IsLoaded(doc, t.Id),
                        ["path"] = PathOf(t),
                        ["instances"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Count(i => i.GetTypeId() == t.Id),
                    }).ToList(),
                };
            }
            if (mode != "reload" && mode != "unload") throw new ArgumentException("action: list, reload или unload.");
            if (names == null || names.Count == 0) throw new ArgumentException("Укажите links — части имён связей.");
            List<RevitLinkType> targets = types.Where(t => names.Any(n => t.Name.IndexOf(n.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
            if (targets.Count == 0) throw new ArgumentException("Связи не найдены. Список: manage_links с action=list.");

            var results = new List<Dictionary<string, object>>();
            foreach (RevitLinkType type in targets)
            {
                string status = "planned";
                if (!dryRun)
                {
                    try
                    {
                        if (mode == "reload") type.Reload();
                        else type.Unload(null);
                        status = "done";
                    }
                    catch (Exception ex)
                    {
                        status = "failed: " + ex.Message;
                    }
                }
                results.Add(new Dictionary<string, object> { ["name"] = type.Name, ["status"] = status });
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["action"] = mode,
                ["links"] = results,
                ["summary"] = dryRun
                    ? $"Предпросмотр: {(mode == "reload" ? "обновить" : "выгрузить")} связей: {targets.Count}. Для выполнения повторите с dryRun=false."
                    : $"Готово: {results.Count(r => (string)r["status"] == "done")} из {targets.Count}. Это действие не отменяется через Ctrl+Z.",
            };
        }

        private static string PathOf(RevitLinkType type)
        {
            try
            {
                ExternalFileReference reference = type.GetExternalFileReference();
                return reference == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double? FileSizeMb(Document doc)
        {
            try
            {
                return string.IsNullOrEmpty(doc.PathName) || !File.Exists(doc.PathName) ? (double?)null : Math.Round(new FileInfo(doc.PathName).Length / 1048576.0, 1);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
