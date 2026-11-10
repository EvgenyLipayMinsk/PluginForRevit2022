using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace PluginForRevit2022.Core
{
    /// <summary>Текст, штриховки, изменения листов и марки оборудования.</summary>
    internal static class AnnotationOperations
    {
        /// <summary>Категории оборудования, с которым работает плагин.</summary>
        public static readonly string[] PluginEquipment =
        {
            "OST_FireAlarmDevices", "OST_DataDevices", "OST_NurseCallDevices", "OST_CommunicationDevices", "OST_SecurityDevices",
            "OST_ElectricalEquipment", "OST_ElectricalFixtures", "OST_LightingDevices", "OST_TelephoneDevices",
        };

        public static Dictionary<string, object> TextNote(Document doc, string viewValue, double xMm, double yMm, string text, string typeName, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Укажите text.");
            View view = OpsCommon.ViewOrActive(doc, viewValue);
            ElementId typeId = string.IsNullOrWhiteSpace(typeName)
                ? doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)
                : new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstOrDefault(t => string.Equals(t.Name, typeName.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id
                  ?? throw new ArgumentException($"Тип текста «{typeName}» не найден.");
            long id;
            using (var transaction = new Transaction(doc, "AerBIM: текст"))
            {
                transaction.Start();
                id = RevitIds.IdOf(Autodesk.Revit.DB.TextNote.Create(doc, view.Id, new XYZ(RevitText.ToFeet(xMm), RevitText.ToFeet(yMm), 0), text, typeId).Id);
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun, ["id"] = dryRun ? (long?)null : id, ["view"] = view.Name, ["type"] = doc.GetElement(typeId)?.Name,
                ["summary"] = OpsCommon.Summary(dryRun, $"текст будет размещён на виде «{view.Name}»", $"Текст размещён на виде «{view.Name}»."),
            };
        }

        /// <summary>Штриховка по контуру из точек [x1, y1, x2, y2, …] в мм.</summary>
        public static Dictionary<string, object> FilledRegion(Document doc, string viewValue, double[] pointsMm, string typeName, bool dryRun)
        {
            if (pointsMm == null || pointsMm.Length < 6 || pointsMm.Length % 2 != 0) throw new ArgumentException("points: не меньше трёх точек [x1, y1, x2, y2, x3, y3] в мм.");
            View view = OpsCommon.ViewOrActive(doc, viewValue);
            List<FilledRegionType> types = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
            FilledRegionType type = string.IsNullOrWhiteSpace(typeName)
                ? types.FirstOrDefault() ?? throw new InvalidOperationException("В проекте нет типов штриховки.")
                : types.FirstOrDefault(t => string.Equals(t.Name, typeName.Trim(), StringComparison.CurrentCultureIgnoreCase)) ?? throw new ArgumentException($"Тип штриховки «{typeName}» не найден.");
            var points = new List<XYZ>();
            for (int i = 0; i < pointsMm.Length; i += 2) points.Add(new XYZ(RevitText.ToFeet(pointsMm[i]), RevitText.ToFeet(pointsMm[i + 1]), 0));
            var loop = new CurveLoop();
            for (int i = 0; i < points.Count; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));

            long id;
            using (var transaction = new Transaction(doc, "AerBIM: штриховка"))
            {
                transaction.Start();
                id = RevitIds.IdOf(Autodesk.Revit.DB.FilledRegion.Create(doc, type.Id, view.Id, new List<CurveLoop> { loop }).Id);
                OpsCommon.Finish(transaction, dryRun);
            }
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun, ["id"] = dryRun ? (long?)null : id, ["type"] = type.Name, ["view"] = view.Name,
                ["summary"] = OpsCommon.Summary(dryRun, $"штриховка «{type.Name}» будет создана", $"Штриховка «{type.Name}» создана на виде «{view.Name}»."),
            };
        }

        /// <summary>list — изменения проекта; create — новое изменение; assign — добавить изменение на листы.</summary>
        public static Dictionary<string, object> Revisions(Document doc, string action, string description, string date, string issuedBy, string issuedTo,
                                                          string revision, List<string> sheets, bool dryRun)
        {
            string mode = (action ?? "list").Trim().ToLowerInvariant();
            List<Revision> all = Revision.GetAllRevisionIds(doc).Select(id => (Revision)doc.GetElement(id)).ToList();
            if (mode == "list")
            {
                return new Dictionary<string, object>
                {
                    ["revisions"] = all.Select(r => new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.IdOf(r.Id), ["number"] = NumberOf(r), ["sequence"] = r.SequenceNumber, ["description"] = r.Description,
                        ["date"] = r.RevisionDate, ["issued"] = r.Issued, ["issuedBy"] = r.IssuedBy, ["issuedTo"] = r.IssuedTo,
                    }).ToList(),
                };
            }

            using (var transaction = new Transaction(doc, "AerBIM: изменения"))
            {
                transaction.Start();
                string summary;
                if (mode == "create")
                {
                    Revision created = Revision.Create(doc);
                    if (description != null) created.Description = description;
                    if (date != null) created.RevisionDate = date;
                    if (issuedBy != null) created.IssuedBy = issuedBy;
                    if (issuedTo != null) created.IssuedTo = issuedTo;
                    summary = $"изменение «{description}» будет создано";
                }
                else if (mode == "assign")
                {
                    Revision target = all.FirstOrDefault(r => RevitIds.IdOf(r.Id).ToString() == revision?.Trim()
                                                              || string.Equals(r.Description, revision?.Trim(), StringComparison.CurrentCultureIgnoreCase)
                                                              || r.SequenceNumber.ToString() == revision?.Trim())
                        ?? throw new ArgumentException($"Изменение «{revision}» не найдено (id, порядковый номер или описание).");
                    List<ViewSheet> targets = SheetsByNumber(doc, sheets);
                    foreach (ViewSheet sheet in targets)
                    {
                        ICollection<ElementId> ids = sheet.GetAdditionalRevisionIds();
                        if (!ids.Contains(target.Id))
                        {
                            ids.Add(target.Id);
                            sheet.SetAdditionalRevisionIds(ids);
                        }
                    }
                    summary = $"изменение будет добавлено на листов: {targets.Count}";
                }
                else
                {
                    throw new ArgumentException("action: list, create или assign.");
                }
                OpsCommon.Finish(transaction, dryRun);
                return new Dictionary<string, object> { ["dryRun"] = dryRun, ["action"] = mode, ["summary"] = OpsCommon.Summary(dryRun, summary, "Готово.") };
            }
        }

        /// <summary>Номер изменения; при нумерации по листам Revit его не отдаёт.</summary>
        private static string NumberOf(Revision revision)
        {
            try
            {
                return revision.RevisionNumber;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<ViewSheet> SheetsByNumber(Document doc, List<string> numbers)
        {
            if (numbers == null || numbers.Count == 0) throw new ArgumentException("Укажите sheets — номера листов.");
            List<ViewSheet> all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            return numbers.Select(n => all.FirstOrDefault(s => string.Equals(s.SheetNumber, n.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ?? throw new ArgumentException($"Лист «{n}» не найден.")).Distinct().ToList();
        }

        /// <summary>Марки оборудования плагина на виде: ставятся только там, где марки ещё нет.</summary>
        public static Dictionary<string, object> TagElements(Document doc, string viewValue, List<string> categories, string tagType, double offsetMm, bool leader, bool dryRun)
        {
            View view = OpsCommon.ViewOrActive(doc, viewValue);
            List<string> names = categories != null && categories.Count > 0 ? categories : PluginEquipment.ToList();
            List<ElementId> categoryIds = SharedParameterOperations.ResolveCategories(doc, names).Select(c => c.Id).ToList();
            List<Element> elements = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(categoryIds))
                .WhereElementIsNotElementType().Where(e => e is FamilyInstance).ToList();

            var tagged = new HashSet<long>();
            foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
#if REVIT2021
                if (tag.TaggedLocalElementId != ElementId.InvalidElementId) tagged.Add(RevitIds.IdOf(tag.TaggedLocalElementId));
#else
                foreach (ElementId id in tag.GetTaggedLocalElementIds()) tagged.Add(RevitIds.IdOf(id));
#endif
            }
            FamilySymbol type = null;
            if (!string.IsNullOrWhiteSpace(tagType))
            {
                type = ElementEditOperations.ResolveType(doc, tagType) as FamilySymbol ?? throw new ArgumentException($"«{tagType}» — не тип марки.");
            }

            var created = new Dictionary<string, int>();
            var failures = new Dictionary<string, string>();
            int skipped = 0;
            double offset = RevitText.ToFeet(offsetMm);
            using (var transaction = new Transaction(doc, "AerBIM: марки оборудования"))
            {
                transaction.Start();
                foreach (Element element in elements)
                {
                    if (tagged.Contains(RevitIds.IdOf(element.Id)))
                    {
                        skipped++;
                        continue;
                    }
                    XYZ point = LinkScope.LocationOf(element);
                    if (point == null) continue;
                    string category = element.Category.Name;
                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            IndependentTag tag = IndependentTag.Create(doc, view.Id, new Reference(element), leader, TagMode.TM_ADDBY_CATEGORY,
                                TagOrientation.Horizontal, point + new XYZ(0, offset, 0));
                            if (type != null && tag.GetTypeId() != type.Id) tag.ChangeTypeId(type.Id);
                            sub.Commit();
                            created[category] = (created.TryGetValue(category, out int n) ? n : 0) + 1;
                        }
                        catch (Exception ex)
                        {
                            if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                            if (!failures.ContainsKey(category)) failures[category] = ex.Message;
                        }
                    }
                }
                OpsCommon.Finish(transaction, dryRun || created.Count == 0);
            }
            int total = created.Values.Sum();
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["view"] = view.Name,
                ["created"] = created,
                ["alreadyTagged"] = skipped,
                ["failedCategories"] = failures,
                ["summary"] = OpsCommon.Summary(dryRun, $"будет поставлено марок: {total}, уже с марками: {skipped}", $"Поставлено марок: {total}, уже были: {skipped}.")
                              + (failures.Count > 0 ? " Для части категорий марки не созданы: проверьте, загружены ли семейства марок." : ""),
            };
        }
    }
}
