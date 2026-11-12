using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Раскраска элементов вида по значению параметра (переопределение графики элементов).</summary>
    internal static class ColorOperations
    {
        private static readonly (byte r, byte g, byte b)[] Palette =
        {
            (230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (70, 190, 190),
            (240, 50, 230), (170, 110, 40), (128, 128, 0), (0, 0, 128), (210, 160, 0), (0, 128, 128),
        };
        private static readonly (byte r, byte g, byte b) EmptyColor = (160, 160, 160);

        public static Dictionary<string, object> ColorByParameter(Document doc, string viewValue, List<string> categories, string parameter, bool reset, bool dryRun)
        {
            View view = string.IsNullOrWhiteSpace(viewValue) ? doc.ActiveView : ViewOperations.ResolveView(doc, viewValue);
            if (view == null || view.IsTemplate) throw new ArgumentException("Укажите вид.");
            if (categories == null || categories.Count == 0) throw new ArgumentException("Укажите categories.");
            List<Category> cats = SharedParameterOperations.ResolveCategories(doc, categories);
            List<Element> elements = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(cats.Select(c => c.Id).ToList()))
                .WhereElementIsNotElementType().ToList();
            if (elements.Count == 0) throw new InvalidOperationException($"На виде «{view.Name}» нет элементов указанных категорий.");

            var legend = new List<Dictionary<string, object>>();
            using (ViewSession.Open(doc, view))
            using (var transaction = new Transaction(doc, reset ? "AerBIM: сбросить раскраску" : "AerBIM: раскраска по параметру"))
            {
                transaction.Start();
                if (reset)
                {
                    foreach (Element element in elements) view.SetElementOverrides(element.Id, new OverrideGraphicSettings());
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(parameter)) throw new ArgumentException("Укажите parameter.");
                    ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                        .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting)?.Id;
                    var groups = elements.GroupBy(e => ValueOf(doc, e, parameter.Trim()) ?? "")
                        .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
                    int index = 0;
                    foreach (var group in groups)
                    {
                        var (r, g, b) = group.Key.Length == 0 ? EmptyColor : Palette[index++ % Palette.Length];
                        var color = new Color(r, g, b);
                        var overrides = new OverrideGraphicSettings();
                        overrides.SetProjectionLineColor(color);
                        overrides.SetCutLineColor(color);
                        if (solid != null)
                        {
                            overrides.SetSurfaceForegroundPatternId(solid);
                            overrides.SetSurfaceForegroundPatternColor(color);
                        }
                        foreach (Element element in group) view.SetElementOverrides(element.Id, overrides);
                        legend.Add(new Dictionary<string, object>
                        {
                            ["value"] = group.Key.Length == 0 ? "(пусто)" : group.Key,
                            ["color"] = $"{r},{g},{b}",
                            ["count"] = group.Count(),
                        });
                    }
                }

                if (dryRun) transaction.RollBack();
                else if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не применил раскраску.");
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["view"] = view.Name,
                ["elements"] = elements.Count,
                ["legend"] = legend,
                ["summary"] = reset
                    ? (dryRun ? $"Предпросмотр: будет сброшена графика у {elements.Count} элементов." : $"Сброшена графика у {elements.Count} элементов.")
                    : (dryRun ? $"Предпросмотр: {legend.Count} цветов на {elements.Count} элементов. Для применения повторите с dryRun=false."
                              : $"Раскрашено элементов: {elements.Count}, цветов: {legend.Count}. Сбросить можно с reset=true."),
            };
        }

        /// <summary>Значение параметра экземпляра, а если его нет — типа.</summary>
        private static string ValueOf(Document doc, Element element, string name)
        {
            Parameter parameter = element.LookupParameter(name) ?? doc.GetElement(element.GetTypeId())?.LookupParameter(name);
            if (parameter == null && string.Equals(name, "Комментарии", StringComparison.CurrentCultureIgnoreCase))
            {
                parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            }
            return RevitText.ValueText(parameter)?.Trim();
        }
    }
}
