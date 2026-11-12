using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Общие помощники новых MCP-инструментов.</summary>
    internal static class OpsCommon
    {
        public const int MaxRows = 500;

        public static List<Element> Elements(Document doc, IEnumerable<long> ids)
        {
            List<long> list = ids?.Distinct().ToList() ?? new List<long>();
            if (list.Count == 0) throw new ArgumentException("Укажите ids.");
            var result = new List<Element>();
            foreach (long id in list)
            {
                result.Add(doc.GetElement(RevitIds.ToElementId(id)) ?? throw new ArgumentException($"Элемент id {id} не найден в проекте."));
            }
            return result;
        }

        public static List<ElementId> CategoryIds(Document doc, IEnumerable<string> names) =>
            SharedParameterOperations.ResolveCategories(doc, names).Select(c => c.Id).ToList();

        public static View ViewOrActive(Document doc, string value)
        {
            View view = string.IsNullOrWhiteSpace(value) ? doc.ActiveView : ViewOperations.ResolveView(doc, value);
            if (view == null || view.IsTemplate) throw new ArgumentException("Укажите вид.");
            return view;
        }

        public static void Finish(Transaction transaction, bool dryRun)
        {
            if (dryRun) transaction.RollBack();
            else if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не применил изменения.");
        }

        public static string TypeLabel(Document doc, ElementId typeId)
        {
            var type = doc.GetElement(typeId) as ElementType;
            return type == null ? null : string.IsNullOrEmpty(type.FamilyName) ? type.Name : $"{type.FamilyName} : {type.Name}";
        }

        public static Dictionary<string, object> Point(XYZ p) => p == null ? null : new Dictionary<string, object>
        {
            ["xMm"] = RevitText.ToMm(p.X), ["yMm"] = RevitText.ToMm(p.Y), ["zMm"] = RevitText.ToMm(p.Z),
        };

        public static XYZ FromMm(double[] values, string name)
        {
            if (values == null || values.Length != 3) throw new ArgumentException($"{name}: нужен массив [x, y, z] в мм.");
            return new XYZ(RevitText.ToFeet(values[0]), RevitText.ToFeet(values[1]), RevitText.ToFeet(values[2]));
        }

        public static Color ParseColor(string value)
        {
            string[] parts = (value ?? "").Split(',');
            if (parts.Length != 3 || !parts.All(p => byte.TryParse(p.Trim(), out _))) throw new ArgumentException("Цвет: «R,G,B», например 255,0,0.");
            return new Color(byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()), byte.Parse(parts[2].Trim()));
        }

        public static string Summary(bool dryRun, string preview, string done) =>
            dryRun ? $"Предпросмотр: {preview}. Для применения повторите с dryRun=false." : done + " Отменить можно через Ctrl+Z.";
    }
}
