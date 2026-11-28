using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace PluginForRevit2022.Core
{
    /// <summary>Проверки модели: элементы без марок и размеров, предупреждения, очистка.</summary>
    internal static class ModelCheckOperations
    {
        private const int MaxRows = 300;

        private static View ViewOf(Document doc, string value)
        {
            View view = string.IsNullOrWhiteSpace(value) ? doc.ActiveView : ViewOperations.ResolveView(doc, value);
            if (view == null || view.IsTemplate) throw new ArgumentException("Укажите вид.");
            return view;
        }

        private static List<Element> InView(Document doc, View view, List<string> categories)
        {
            if (categories == null || categories.Count == 0) throw new ArgumentException("Укажите categories.");
            List<Category> cats = SharedParameterOperations.ResolveCategories(doc, categories);
            return new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(cats.Select(c => c.Id).ToList()))
                .WhereElementIsNotElementType().ToList();
        }

        private static Dictionary<string, object> Report(View view, List<Element> all, List<Element> found, string what) => new Dictionary<string, object>
        {
            ["view"] = view.Name,
            ["checked"] = all.Count,
            ["found"] = found.Count,
            ["byCategory"] = found.GroupBy(e => e.Category?.Name ?? "—").ToDictionary(g => g.Key, g => g.Count()),
            ["elements"] = found.Take(MaxRows).Select(SelectionOperations.Describe).ToList(),
            ["summary"] = $"{what}: {found.Count} из {all.Count}. Показать на виде: show_elements.",
        };

        public static Dictionary<string, object> Untagged(Document doc, string viewValue, List<string> categories)
        {
            View view = ViewOf(doc, viewValue);
            List<Element> all = InView(doc, view, categories);
            var tagged = new HashSet<long>();
            foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
#if REVIT2021
                if (tag.TaggedLocalElementId != ElementId.InvalidElementId) tagged.Add(RevitIds.IdOf(tag.TaggedLocalElementId));
#else
                foreach (ElementId id in tag.GetTaggedLocalElementIds()) tagged.Add(RevitIds.IdOf(id));
#endif
            }
            foreach (RoomTag tag in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().OfType<RoomTag>())
            {
                ElementId room = tag.TaggedLocalRoomId; // null у марок помещений из связи
                if (room != null && room != ElementId.InvalidElementId) tagged.Add(RevitIds.IdOf(room));
            }
            return Report(view, all, all.Where(e => !tagged.Contains(RevitIds.IdOf(e.Id))).ToList(), "Без марок");
        }

        public static Dictionary<string, object> Undimensioned(Document doc, string viewValue, List<string> categories)
        {
            View view = ViewOf(doc, viewValue);
            List<Element> all = InView(doc, view, categories);
            var referenced = new HashSet<long>();
            foreach (Dimension dimension in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                foreach (Reference reference in dimension.References)
                {
                    if (reference.LinkedElementId == ElementId.InvalidElementId) referenced.Add(RevitIds.IdOf(reference.ElementId));
                }
            }
            return Report(view, all, all.Where(e => !referenced.Contains(RevitIds.IdOf(e.Id))).ToList(), "Без размеров");
        }

        public static Dictionary<string, object> Warnings(Document doc)
        {
            IList<FailureMessage> warnings = doc.GetWarnings();
            return new Dictionary<string, object>
            {
                ["total"] = warnings.Count,
                ["groups"] = warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Select(g => new Dictionary<string, object>
                {
                    ["text"] = g.Key,
                    ["count"] = g.Count(),
                    ["elementIds"] = g.SelectMany(w => w.GetFailingElements()).Select(RevitIds.IdOf).Distinct().Take(50).ToList(),
                }).ToList(),
            };
        }

        /// <summary>Неиспользуемые типы загружаемых семейств (без экземпляров). Удаление — только при dryRun=false.</summary>
        public static Dictionary<string, object> PurgeUnusedTypes(Document doc, List<string> categories, bool dryRun)
        {
            var used = new HashSet<long>(new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Select(e => e.GetTypeId()).Where(id => id != ElementId.InvalidElementId).Select(RevitIds.IdOf));
            IEnumerable<FamilySymbol> symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            if (categories != null && categories.Count > 0)
            {
                var cats = new HashSet<long>(SharedParameterOperations.ResolveCategories(doc, categories).Select(c => RevitIds.IdOf(c.Id)));
                symbols = symbols.Where(s => s.Category != null && cats.Contains(RevitIds.IdOf(s.Category.Id)));
            }
            List<FamilySymbol> unused = symbols.Where(s => !used.Contains(RevitIds.IdOf(s.Id))).ToList();

            var deleted = new List<Dictionary<string, object>>();
            var skipped = new List<string>();
            using (var transaction = new Transaction(doc, "AerBIM: удалить неиспользуемые типы"))
            {
                transaction.Start();
                foreach (FamilySymbol symbol in unused)
                {
                    string label = $"{symbol.FamilyName} : {symbol.Name}";
                    long id = RevitIds.IdOf(symbol.Id);
                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            doc.Delete(symbol.Id);
                            sub.Commit();
                            deleted.Add(new Dictionary<string, object> { ["id"] = id, ["type"] = label });
                        }
                        catch (Exception ex)
                        {
                            if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                            skipped.Add($"{label}: {ex.Message}");
                        }
                    }
                }
                if (dryRun || deleted.Count == 0) transaction.RollBack();
                else if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не применил удаление.");
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["unused"] = unused.Count,
                ["deleted"] = deleted.Count,
                ["types"] = deleted.Take(MaxRows).ToList(),
                ["skipped"] = skipped.Take(50).ToList(),
                ["summary"] = dryRun
                    ? $"Предпросмотр: можно удалить неиспользуемых типов: {deleted.Count}. Для удаления повторите с dryRun=false."
                    : $"Удалено неиспользуемых типов: {deleted.Count}. Отменить можно через Ctrl+Z.",
            };
        }

        /// <summary>Марки с пустым текстом на виде (или во всём проекте).</summary>
        public static Dictionary<string, object> WipeEmptyTags(Document doc, string viewValue, bool dryRun)
        {
            FilteredElementCollector collector = string.IsNullOrWhiteSpace(viewValue)
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, ViewOf(doc, viewValue).Id);
            List<Element> empty = collector.WhereElementIsNotElementType().Where(e =>
                (e is IndependentTag tag && string.IsNullOrWhiteSpace(tag.TagText)) ||
                (e is SpatialElementTag spatial && string.IsNullOrWhiteSpace(spatial.TagText))).ToList();

            if (!dryRun && empty.Count > 0)
            {
                using (var transaction = new Transaction(doc, "AerBIM: удалить пустые марки"))
                {
                    transaction.Start();
                    doc.Delete(empty.Select(e => e.Id).ToList());
                    if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не применил удаление.");
                }
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["found"] = empty.Count,
                ["ids"] = empty.Take(MaxRows).Select(e => RevitIds.IdOf(e.Id)).ToList(),
                ["summary"] = dryRun
                    ? $"Предпросмотр: пустых марок: {empty.Count}. Для удаления повторите с dryRun=false."
                    : $"Удалено пустых марок: {empty.Count}. Отменить можно через Ctrl+Z.",
            };
        }
    }
}
