using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Типоразмер основной надписи с измеренным размером листа.</summary>
    internal sealed class TitleBlockInfo
    {
        public long Id { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        public double? WidthMm { get; set; }
        public double? HeightMm { get; set; }
        public List<Dictionary<string, object>> FormatParameters { get; set; } = new List<Dictionary<string, object>>();

        public string FullName => $"{Family} : {Type}";

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["family"] = Family,
            ["type"] = Type,
            ["name"] = FullName,
            ["widthMm"] = WidthMm,
            ["heightMm"] = HeightMm,
            ["formatParameters"] = FormatParameters,
        };
    }

    internal sealed class SheetSpec
    {
        public string Number { get; set; }
        public string Name { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }

    internal sealed class CreateSheetsRequest
    {
        /// <summary>Основная надпись: id, «Семейство : Тип», имя типа или «none».</summary>
        public string TitleBlock { get; set; }
        public List<SheetSpec> Sheets { get; set; } = new List<SheetSpec>();
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Листы и основные надписи. Без UI: используется MCP и другими операциями.</summary>
    internal static class SheetOperations
    {
        private static readonly Regex FormatParameterName =
            new Regex("формат|кратн|ширин|высот|размер|ориент|format|width|height|size|orient", RegexOptions.IgnoreCase);

        // ---------- Листы ----------

        public static List<ViewSheet> Sheets(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .OrderBy(s => s.SheetNumber, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        public static FamilyInstance TitleBlockOf(Document doc, ViewSheet sheet) =>
            new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault();

        public static Dictionary<string, object> Describe(Document doc, ViewSheet sheet, IEnumerable<string> parameterNames = null)
        {
            FamilyInstance titleBlock = TitleBlockOf(doc, sheet);
            var views = sheet.GetAllPlacedViews().Select(id => doc.GetElement(id) as View).Where(v => v != null)
                .Select(v => v.Name).ToList();
            var result = new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(sheet.Id),
                ["number"] = sheet.SheetNumber,
                ["name"] = sheet.Name,
                ["isPlaceholder"] = sheet.IsPlaceholder,
                ["titleBlock"] = titleBlock == null ? null : $"{titleBlock.Symbol.FamilyName} : {titleBlock.Symbol.Name}",
                ["views"] = views,
            };
            if (parameterNames != null)
            {
                result["parameters"] = parameterNames.ToDictionary(n => n, n => (object)RevitText.ValueText(sheet.LookupParameter(n)));
            }
            return result;
        }

        public static ViewSheet ResolveSheet(Document doc, long? id, string number)
        {
            if (id != null)
            {
                return doc.GetElement(RevitIds.ToElementId(id.Value)) as ViewSheet
                    ?? throw new ArgumentException($"Лист с id {id} не найден.");
            }
            if (!string.IsNullOrWhiteSpace(number))
            {
                return Sheets(doc).FirstOrDefault(s => string.Equals(s.SheetNumber, number.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    ?? throw new ArgumentException($"Лист с номером «{number}» не найден.");
            }
            return null;
        }

        // ---------- Основные надписи ----------

        public static List<FamilySymbol> TitleBlockSymbols(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .OrderBy(s => s.FamilyName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>Список основных надписей; measure — размер листа измеряется пробной расстановкой (с откатом).</summary>
        public static List<TitleBlockInfo> ListTitleBlocks(Document doc, bool measure)
        {
            List<FamilySymbol> symbols = TitleBlockSymbols(doc);
            if (!measure || doc.IsReadOnly || symbols.Count == 0)
            {
                return symbols.Select(s => Info(s)).ToList();
            }

            using (var transaction = new Transaction(doc, "AerBIM: измерение основных надписей"))
            {
                transaction.Start();
                List<TitleBlockInfo> result = MeasureInOpenTransaction(doc, symbols);
                transaction.RollBack();
                return result;
            }
        }

        /// <summary>Измерение внутри уже открытой транзакции (через подтранзакцию с откатом).</summary>
        public static List<TitleBlockInfo> MeasureInOpenTransaction(Document doc, IEnumerable<FamilySymbol> symbols)
        {
            var result = new List<TitleBlockInfo>();
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                foreach (FamilySymbol symbol in symbols)
                {
                    TitleBlockInfo info = Info(symbol);
                    try
                    {
                        if (!symbol.IsActive)
                        {
                            symbol.Activate();
                        }
                        ViewSheet sheet = ViewSheet.Create(doc, symbol.Id);
                        doc.Regenerate();
                        FamilyInstance instance = TitleBlockOf(doc, sheet);
                        BoundingBoxXYZ box = instance?.get_BoundingBox(sheet);
                        if (box != null)
                        {
                            info.WidthMm = RevitText.ToMm(box.Max.X - box.Min.X);
                            info.HeightMm = RevitText.ToMm(box.Max.Y - box.Min.Y);
                        }
                        if (instance != null)
                        {
                            info.FormatParameters = FormatParameters(instance, "instance")
                                .Concat(FormatParameters(symbol, "type")).ToList();
                        }
                    }
                    catch (Exception ex)
                    {
                        info.FormatParameters.Add(new Dictionary<string, object> { ["error"] = ex.Message });
                    }
                    result.Add(info);
                }
                sub.RollBack();
            }
            return result;
        }

        private static IEnumerable<Dictionary<string, object>> FormatParameters(Element element, string level) =>
            element.Parameters.Cast<Parameter>()
                .Where(p => FormatParameterName.IsMatch(p.Definition.Name))
                .OrderBy(p => p.Definition.Name)
                .Select(p => new Dictionary<string, object>
                {
                    ["name"] = p.Definition.Name,
                    ["level"] = level,
                    ["value"] = RevitText.ValueText(p),
                    ["readOnly"] = p.IsReadOnly,
                });

        private static TitleBlockInfo Info(FamilySymbol symbol) => new TitleBlockInfo
        {
            Id = RevitIds.IdOf(symbol.Id),
            Family = symbol.FamilyName,
            Type = symbol.Name,
        };

        /// <summary>id, «Семейство : Тип», имя типа (если уникально) или имя семейства (если в нём один тип). «none» — без надписи.</summary>
        public static FamilySymbol ResolveTitleBlock(Document doc, string value)
        {
            List<FamilySymbol> all = TitleBlockSymbols(doc);
            string wanted = (value ?? string.Empty).Trim();
            if (wanted.Length == 0)
            {
                throw new ArgumentException("Укажите основную надпись (titleBlock). Список: list_title_blocks.");
            }
            if (string.Equals(wanted, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (long.TryParse(wanted, out long id))
            {
                return all.FirstOrDefault(s => RevitIds.IdOf(s.Id) == id)
                    ?? throw new ArgumentException($"Основная надпись с id {id} не найдена.");
            }

            List<FamilySymbol> matches = all.Where(s =>
                string.Equals($"{s.FamilyName} : {s.Name}", wanted, StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals($"{s.FamilyName}:{s.Name}", wanted.Replace(" : ", ":"), StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                matches = all.Where(s => string.Equals(s.Name, wanted, StringComparison.CurrentCultureIgnoreCase)).ToList();
            }
            if (matches.Count == 0)
            {
                matches = all.Where(s => string.Equals(s.FamilyName, wanted, StringComparison.CurrentCultureIgnoreCase)).ToList();
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }
            throw new ArgumentException(matches.Count == 0
                ? $"Основная надпись «{wanted}» не найдена. Список: list_title_blocks."
                : $"«{wanted}» неоднозначно: {string.Join("; ", matches.Select(m => $"{m.FamilyName} : {m.Name} (id {RevitIds.IdOf(m.Id)})"))}. Укажите id или «Семейство : Тип».");
        }

        // ---------- Создание листов ----------

        public static Dictionary<string, object> CreateSheets(Document doc, CreateSheetsRequest request)
        {
            if (doc.IsFamilyDocument)
            {
                throw new InvalidOperationException("Листы создаются только в проекте.");
            }
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }
            if (request.Sheets == null || request.Sheets.Count == 0)
            {
                throw new ArgumentException("Список листов пуст (sheets).");
            }

            FamilySymbol titleBlock = ResolveTitleBlock(doc, request.TitleBlock);
            var taken = new HashSet<string>(Sheets(doc).Select(s => s.SheetNumber), StringComparer.CurrentCultureIgnoreCase);
            var results = new List<Dictionary<string, object>>();
            int created = 0;

            using (var transaction = new Transaction(doc, "AerBIM: создать листы"))
            {
                transaction.Start();
                if (titleBlock != null && !titleBlock.IsActive)
                {
                    titleBlock.Activate();
                }

                foreach (SheetSpec spec in request.Sheets)
                {
                    string number = spec.Number?.Trim();
                    if (!string.IsNullOrEmpty(number) && taken.Contains(number))
                    {
                        results.Add(ItemResult(spec, null, "skipped", $"номер «{number}» уже занят", null));
                        continue;
                    }

                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            ViewSheet sheet = CreateOne(doc, titleBlock, spec, out List<string> warnings);
                            sub.Commit();
                            taken.Add(sheet.SheetNumber);
                            created++;
                            Dictionary<string, object> item = ItemResult(spec, sheet, "created", null, warnings);
                            if (request.DryRun)
                            {
                                item["id"] = null; // после отката листа не существует
                            }
                            results.Add(item);
                        }
                        catch (Exception ex)
                        {
                            sub.RollBack();
                            results.Add(ItemResult(spec, null, "failed", ex.Message, null));
                        }
                    }
                }

                if (request.DryRun || created == 0)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit не применил создание листов.");
                }
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["titleBlock"] = titleBlock == null ? "none" : $"{titleBlock.FamilyName} : {titleBlock.Name}",
                ["created"] = created,
                ["total"] = request.Sheets.Count,
                ["results"] = results,
                ["summary"] = (request.DryRun ? "Предпросмотр: будет создано" : "Создано") +
                              $" листов: {created} из {request.Sheets.Count}." +
                              (request.DryRun ? " Для создания повторите с dryRun=false." : " Отменить можно в Revit через Ctrl+Z."),
            };
        }

        /// <summary>Создаёт один лист внутри открытой транзакции/подтранзакции. Ошибка номера/имени — исключение.</summary>
        internal static ViewSheet CreateOne(Document doc, FamilySymbol titleBlock, SheetSpec spec, out List<string> warnings)
        {
            warnings = new List<string>();
            ViewSheet sheet = ViewSheet.Create(doc, titleBlock?.Id ?? ElementId.InvalidElementId);
            if (!string.IsNullOrWhiteSpace(spec.Number))
            {
                sheet.SheetNumber = spec.Number.Trim();
            }
            if (!string.IsNullOrWhiteSpace(spec.Name))
            {
                sheet.Name = spec.Name.Trim();
            }
            foreach (KeyValuePair<string, string> parameter in spec.Parameters ?? new Dictionary<string, string>())
            {
                string warning = RevitText.TrySet(sheet, parameter.Key, parameter.Value);
                if (warning != null)
                {
                    warnings.Add(warning);
                }
            }
            return sheet;
        }

        private static Dictionary<string, object> ItemResult(SheetSpec spec, ViewSheet sheet, string status, string error, List<string> warnings) =>
            new Dictionary<string, object>
            {
                ["number"] = sheet?.SheetNumber ?? spec.Number,
                ["name"] = sheet?.Name ?? spec.Name,
                ["id"] = sheet == null ? (object)null : RevitIds.IdOf(sheet.Id),
                ["status"] = status,
                ["error"] = error,
                ["warnings"] = warnings ?? new List<string>(),
            };
    }
}
