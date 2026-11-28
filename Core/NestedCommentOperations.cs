using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// «Комментарии» вложенных общих семейств — как у родителя: АКБ внутри ИВЭПР, коробка внутри магнитоконтактного
    /// извещателя и т.п. Вложенный экземпляр — отдельный элемент модели со своими «Комментариями»; без этого он
    /// выпадает из системы (наборы, журнал, фильтры по комментарию).
    /// </summary>
    internal static class NestedCommentOperations
    {
        /// <summary>Верхний родитель вложенного экземпляра (цепочка SuperComponent до конца).</summary>
        public static FamilyInstance TopParent(FamilyInstance fi)
        {
            FamilyInstance top = fi;
            for (int i = 0; i < 10 && top.SuperComponent is FamilyInstance parent; i++) top = parent;
            return ReferenceEquals(top, fi) ? null : top;
        }

        /// <param name="ids">Только эти вложенные экземпляры; null — все вложенные в проекте.</param>
        /// <param name="overwrite">false — меняются только пустые «Комментарии» вложенных.</param>
        public static Dictionary<string, object> Sync(Document doc, ICollection<long> ids, bool overwrite, bool dryRun)
        {
            IEnumerable<FamilyInstance> scope = ids != null && ids.Count > 0
                ? ids.Select(id => doc.GetElement(RevitIds.ToElementId(id)) as FamilyInstance).Where(f => f != null)
                : new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.SuperComponent != null);

            var changes = new List<(FamilyInstance child, FamilyInstance parent, string value)>();
            int same = 0, kept = 0, parentEmpty = 0, notNested = 0, busy = 0;
            foreach (FamilyInstance child in scope)
            {
                FamilyInstance parent = TopParent(child);
                if (parent == null) { notNested++; continue; }
                Parameter p = child.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p == null || p.IsReadOnly) continue;
                string value = DeviceMarkingOperations.CommentOf(parent), current = DeviceMarkingOperations.CommentOf(child);
                if (value.Length == 0) { parentEmpty++; continue; }
                if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase)) { same++; continue; }
                if (current.Length > 0 && !overwrite) { kept++; continue; }
                if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, child.Id) == CheckoutStatus.OwnedByOtherUser) { busy++; continue; }
                changes.Add((child, parent, value));
            }

            var failures = new List<string>();
            using (var t = new Transaction(doc, "AerBIM: комментарии вложенных семейств"))
            {
                t.Start();
                foreach (var c in changes)
                {
                    string error = RevitText.TrySet(c.child.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), "Комментарии", c.value);
                    if (error != null) failures.Add($"id {RevitIds.IdOf(c.child.Id)}: {error}");
                }
                if (dryRun || failures.Count > 0) t.RollBack();
                else if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не применил изменения.");
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["changed"] = failures.Count > 0 ? 0 : changes.Count,
                ["byFamily"] = changes.GroupBy(c => $"{c.child.Symbol.FamilyName} ← {c.parent.Symbol.FamilyName} = «{c.value}»")
                    .ToDictionary(g => g.Key, g => g.Count()),
                ["alreadySame"] = same,
                ["keptOtherComment"] = kept,
                ["parentWithoutComment"] = parentEmpty,
                ["notNested"] = notNested,
                ["ownedByOthers"] = busy,
                ["changes"] = changes.Take(200).Select(c => new Dictionary<string, object>
                {
                    ["id"] = RevitIds.IdOf(c.child.Id), ["family"] = c.child.Symbol.FamilyName,
                    ["parentId"] = RevitIds.IdOf(c.parent.Id), ["parentFamily"] = c.parent.Symbol.FamilyName,
                    ["old"] = DeviceMarkingOperations.CommentOf(c.child), ["new"] = c.value,
                }).ToList(),
                ["failures"] = failures.Take(50).ToList(),
                ["summary"] = failures.Count > 0 ? "Изменения не применены: " + string.Join("; ", failures.Take(5))
                    : dryRun ? $"Предпросмотр: «Комментарии» как у родителя получат вложенных экземпляров: {changes.Count}."
                    : $"«Комментарии» как у родителя записаны вложенным экземплярам: {changes.Count}. Отменить можно через Ctrl+Z.",
            };
        }
    }
}
