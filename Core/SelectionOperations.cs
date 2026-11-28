using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PluginForRevit2022.Core
{
    /// <summary>Выделение в Revit: чтение выбранного и показ элементов на виде.</summary>
    internal static class SelectionOperations
    {
        private const int MaxRows = 500;

        public static Dictionary<string, object> GetSelected(UIDocument uiDoc)
        {
            Document doc = uiDoc.Document;
            List<Element> elements = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null).ToList();
            return new Dictionary<string, object>
            {
                ["count"] = elements.Count,
                ["byCategory"] = elements.GroupBy(e => e.Category?.Name ?? "—")
                    .ToDictionary(g => g.Key, g => g.Count()),
                ["elements"] = elements.Take(MaxRows).Select(Describe).ToList(),
                ["truncated"] = elements.Count > MaxRows,
            };
        }

        public static Dictionary<string, object> Describe(Element element)
        {
            XYZ point = LinkScope.LocationOf(element);
            var family = element as FamilyInstance;
            return new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(element.Id),
                ["category"] = element.Category?.Name,
                ["family"] = family?.Symbol?.FamilyName,
                ["type"] = element.Document.GetElement(element.GetTypeId())?.Name,
                ["name"] = element.Name,
                ["level"] = (element.Document.GetElement(element.LevelId) as Level)?.Name,
                ["xMm"] = point == null ? (double?)null : RevitText.ToMm(point.X),
                ["yMm"] = point == null ? (double?)null : RevitText.ToMm(point.Y),
            };
        }

        /// <summary>
        /// select — выделить; zoom — выделить и показать; isolate/hide — временно изолировать или скрыть на активном виде;
        /// reset — вернуть временно скрытое.
        /// </summary>
        public static Dictionary<string, object> Show(UIDocument uiDoc, List<long> ids, string action)
        {
            Document doc = uiDoc.Document;
            View view = doc.ActiveView ?? throw new InvalidOperationException("Нет активного вида.");
            string mode = (action ?? "zoom").Trim().ToLowerInvariant();

            if (mode == "reset")
            {
                using (var transaction = new Transaction(doc, "AerBIM: показать скрытое"))
                {
                    transaction.Start();
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    transaction.Commit();
                }
                return new Dictionary<string, object> { ["action"] = mode, ["view"] = view.Name, ["summary"] = "Временное скрытие на виде отменено." };
            }

            if (ids == null || ids.Count == 0) throw new ArgumentException("Укажите ids.");
            List<ElementId> found = ids.Distinct().Select(RevitIds.ToElementId).Where(id => doc.GetElement(id) != null).ToList();
            List<long> missing = ids.Distinct().Except(found.Select(RevitIds.IdOf)).ToList();
            if (found.Count == 0) throw new ArgumentException("Ни один элемент не найден в проекте.");

            switch (mode)
            {
                case "select":
                    uiDoc.Selection.SetElementIds(found);
                    break;
                case "zoom":
                    uiDoc.Selection.SetElementIds(found);
                    uiDoc.ShowElements(found);
                    break;
                case "isolate":
                case "hide":
                    using (var transaction = new Transaction(doc, "AerBIM: временно " + (mode == "isolate" ? "изолировать" : "скрыть")))
                    {
                        transaction.Start();
                        if (mode == "isolate") view.IsolateElementsTemporary(found);
                        else view.HideElementsTemporary(found);
                        transaction.Commit();
                    }
                    break;
                default:
                    throw new ArgumentException("action: select, zoom, isolate, hide или reset.");
            }

            return new Dictionary<string, object>
            {
                ["action"] = mode,
                ["view"] = view.Name,
                ["count"] = found.Count,
                ["missing"] = missing,
                ["summary"] = $"Обработано элементов: {found.Count}" + (missing.Count > 0 ? $", не найдено: {missing.Count}" : "") + ".",
            };
        }
    }
}
